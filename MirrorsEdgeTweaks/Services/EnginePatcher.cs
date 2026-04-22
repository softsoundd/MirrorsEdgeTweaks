using System;
using System.IO;
using System.Collections.Generic;

namespace MirrorsEdgeTweaks.Services
{
    public enum EnginePatchState { Unpatched, Phase1Only, FullyPatched }

    // Bidirectional patcher for Engine.u
    public static class EnginePatcher
    {
        // UpdateCamera function start
        static readonly byte[] FUNC_START = { 0x07, 0x5F, 0x00, 0x2D, 0x01, 0xFF, 0x0A, 0x00, 0x00 };

        // Phase 1 constants
        const int BC_ASSIGN = 0x0A8;
        const int BC_BOOL = 0x066;
        const int UNPATCHED_BSS = 739;
        const int PHASE1_NET = 11;

        // Property/local/import indices
        static readonly byte[] LOCAL_BLENDPCT   = { 0x00, 0x42, 0x0F, 0x00, 0x00 };
        static readonly byte[] LOCAL_NEWPOV     = { 0x00, 0x44, 0x0F, 0x00, 0x00 };
        static readonly byte[] INST_PCOWNER     = { 0x01, 0x07, 0x0B, 0x00, 0x00 };
        static readonly byte[] INST_MYHUD       = { 0x01, 0xE9, 0x07, 0x00, 0x00 };
        static readonly byte[] INST_SIZEX       = { 0x01, 0x36, 0x23, 0x00, 0x00 };
        static readonly byte[] INST_SIZEY       = { 0x01, 0x35, 0x23, 0x00, 0x00 };
        static readonly byte[] INST_DEFAULT_FOV = { 0x01, 0x05, 0x0B, 0x00, 0x00 };
        static readonly byte[] INST_DEFAULT_AR  = { 0x01, 0xFC, 0x0A, 0x00, 0x00 };
        static readonly byte[] IMP_FOV  = BitConverter.GetBytes(-57);
        static readonly byte[] IMP_TPOV = BitConverter.GetBytes(-151);

        // Phase 1 old prefix (Let ConstrainedAspectRatio = FloatConst(...))
        static readonly byte[] P1_OLD_PREFIX = { 0x0F, 0x01, 0xFD, 0x0A, 0x00, 0x00, 0x1E };
        const int P1_OLD_LEN = 11;

        // Phase 1 new: Let ConstrainedAspectRatio = ViewTarget.AspectRatio (22 bytes)
        static readonly byte[] P1_NEW = {
            0x0F, 0x01, 0xFD, 0x0A, 0x00, 0x00,
            0x35, 0x0C, 0x0B, 0x00, 0x00,
            0x10, 0x0B, 0x00, 0x00, 0x00, 0x00,
            0x01, 0xF2, 0x0A, 0x00, 0x00
        };

        // ConstrainedAspectRatio property token for stock pattern reconstruction
        static readonly byte[] AR_PROPERTY_TOKEN = { 0x01, 0xFD, 0x0A, 0x00, 0x00 };

        // Phase 1 jump table: (bc_offset, expected_target) for targets >= 0x0B3
        static readonly (int bcOff, ushort oldTarget)[] P1_JUMPS = {
            (0x0B3, 0x02D5), (0x0F8, 0x026F), (0x13C, 0x015B), (0x158, 0x0224),
            (0x15B, 0x0188), (0x185, 0x0224), (0x188, 0x01BB), (0x1B8, 0x0224),
            (0x1BB, 0x01EE), (0x1EB, 0x0224), (0x1EE, 0x0221), (0x21E, 0x0224),
            (0x26C, 0x02D5),
        };

        // P2 idempotency signature
        static byte[] P2_SIG => BytecodeBuilder.GetP2Signature(INST_DEFAULT_AR);

        const int BLOB_A_SIZE = 40;

        public static EnginePatchState DetectState(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            bool hasP1New = BytecodeBuilder.FindPattern(data, P1_NEW) != -1;
            bool hasP2Sig = BytecodeBuilder.FindPattern(data, P2_SIG) != -1;

            if (hasP2Sig) return EnginePatchState.FullyPatched;
            if (hasP1New) return EnginePatchState.Phase1Only;
            return EnginePatchState.Unpatched;
        }

        public static void Apply(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            int origLen = data.Length;

            bool hasP1New = BytecodeBuilder.FindPattern(data, P1_NEW) != -1;
            bool hasP2Sig = BytecodeBuilder.FindPattern(data, P2_SIG) != -1;
            if (hasP2Sig) return; // already fully patched

            int funcPos = BytecodeBuilder.FindPattern(data, FUNC_START);
            if (funcPos == -1)
                throw new InvalidOperationException("Cannot find UpdateCamera function start in Engine.u");

            int bcStart = funcPos;
            int exportStart = bcStart - BytecodeBuilder.SCRIPT_HDR;
            uint bss = PackageSplicer.ReadBSS(data, exportStart);
            var hdr = PackageSplicer.ParseHeader(data);

            // Phase 1: Unhardcode AR
            if (!hasP1New)
            {
                if (bss != UNPATCHED_BSS)
                    throw new InvalidOperationException($"Engine.u BSS mismatch: expected {UNPATCHED_BSS}, got {bss}");

                int p1Pos = BytecodeBuilder.FindPattern(data, P1_OLD_PREFIX, bcStart, bcStart + (int)bss);
                if (p1Pos == -1)
                    throw new InvalidOperationException("Cannot find ConstrainedAspectRatio assignment in Engine.u");

                // 1a: bConstrainAspectRatio = false
                if (data[bcStart + BC_BOOL] != BytecodeBuilder.OP_TRUE)
                    throw new InvalidOperationException("bConstrainAspectRatio opcode mismatch");
                data[bcStart + BC_BOOL] = BytecodeBuilder.OP_FALSE;

                // 1b: fix jump targets
                foreach (var (bcOff, oldTarget) in P1_JUMPS)
                {
                    int fp = bcStart + bcOff + 1;
                    ushort cur = BitConverter.ToUInt16(data, fp);
                    if (cur != oldTarget)
                        throw new InvalidOperationException($"Jump at bc+0x{bcOff:X3}: expected 0x{oldTarget:X4}, got 0x{cur:X4}");
                    BitConverter.GetBytes((ushort)(oldTarget + PHASE1_NET)).CopyTo(data, fp);
                }

                // 1c: BSS
                PackageSplicer.WriteBSS(data, exportStart, (uint)(UNPATCHED_BSS + PHASE1_NET));

                // Splice: replace P1_OLD (11 bytes) with P1_NEW (22 bytes)
                data = PackageSplicer.ReplaceBytes(data, p1Pos, P1_OLD_LEN, P1_NEW);

                // Fix export table
                hdr = PackageSplicer.ParseHeader(data);
                PackageSplicer.UpdateExportsHeuristic(data, hdr, exportStart, p1Pos, origLen, PHASE1_NET);
                origLen = data.Length;
            }

            // Phase 2: HOR+/VERT+ FOV scaling
            funcPos = BytecodeBuilder.FindPattern(data, FUNC_START);
            bcStart = funcPos;
            exportStart = bcStart - BytecodeBuilder.SCRIPT_HDR;
            bss = PackageSplicer.ReadBSS(data, exportStart);

            if (bss != UNPATCHED_BSS + PHASE1_NET)
                throw new InvalidOperationException($"Phase 2: expected BSS {UNPATCHED_BSS + PHASE1_NET}, got {bss}");

            byte[] bc = new byte[(int)bss];
            Buffer.BlockCopy(data, bcStart, bc, 0, (int)bss);

            // Find variant specific patterns
            var (checkVt, fillCache) = FindPhase2Patterns(bc);

            int cvtPos = BytecodeBuilder.FindPattern(data, checkVt, bcStart, bcStart + 500);
            if (cvtPos == -1) throw new InvalidOperationException("Cannot find CheckViewTarget");
            int pointABc = cvtPos - bcStart;
            int pointAFile = cvtPos;

            int fcPos = BytecodeBuilder.FindPattern(data, fillCache, bcStart, bcStart + 2000);
            if (fcPos == -1) throw new InvalidOperationException("Cannot find FillCameraCache");
            int pointBBc = fcPos - bcStart;
            int pointBFile = fcPos;

            byte[] blobA = BytecodeBuilder.BuildBlobA(pointABc, INST_DEFAULT_FOV, INST_DEFAULT_AR);
            int blobBBc = pointBBc + blobA.Length;
            byte[] blobB = BytecodeBuilder.BuildBlobB(blobBBc,
                LOCAL_BLENDPCT, LOCAL_NEWPOV, INST_PCOWNER, INST_MYHUD,
                INST_SIZEX, INST_SIZEY, INST_DEFAULT_FOV,
                IMP_FOV, IMP_TPOV);
            int totalP2 = blobA.Length + blobB.Length;

            // Fix jump targets (shift targets >= pointABc)
            int funcEnd = fcPos + fillCache.Length + 20;
            for (int pos = bcStart; pos < funcEnd; pos++)
            {
                byte opcode = data[pos];
                if (opcode == BytecodeBuilder.OP_JUMP_IF_NOT ||
                    opcode == BytecodeBuilder.OP_JUMP ||
                    opcode == BytecodeBuilder.OP_CASE)
                {
                    ushort target = BitConverter.ToUInt16(data, pos + 1);
                    if (target >= pointABc && target < bss && target != 0xFFFF)
                        BitConverter.GetBytes((ushort)(target + blobA.Length)).CopyTo(data, pos + 1);
                }
            }

            PackageSplicer.WriteBSS(data, exportStart, (uint)(bss + totalP2));

            // Splice: insert blob A at pointA, blob B at pointB
            var buf = new byte[data.Length + totalP2];
            Buffer.BlockCopy(data, 0, buf, 0, pointAFile);
            Buffer.BlockCopy(blobA, 0, buf, pointAFile, blobA.Length);
            Buffer.BlockCopy(data, pointAFile, buf, pointAFile + blobA.Length, pointBFile - pointAFile);
            Buffer.BlockCopy(blobB, 0, buf, pointAFile + blobA.Length + (pointBFile - pointAFile), blobB.Length);
            int restStart = pointBFile;
            int restDest = pointAFile + blobA.Length + (pointBFile - pointAFile) + blobB.Length;
            Buffer.BlockCopy(data, restStart, buf, restDest, data.Length - restStart);
            data = buf;

            hdr = PackageSplicer.ParseHeader(data);
            PackageSplicer.UpdateExportsHeuristic(data, hdr, exportStart, pointBFile, origLen, totalP2);

            // Phase 3: CameraActor bConstrainAspectRatio = false
            ApplyPhase3(data, hdr);

            File.WriteAllBytes(enginePath, data);
        }

        public static void Remove(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            var state = DetectStateFromData(data);
            if (state == EnginePatchState.Unpatched) return;

            var hdr = PackageSplicer.ParseHeader(data);

            // Remove Phase 3
            RemovePhase3(data, hdr);

            // Remove Phase 2
            if (BytecodeBuilder.FindPattern(data, P2_SIG) != -1)
            {
                int funcPos = BytecodeBuilder.FindPattern(data, FUNC_START);
                int bcStart = funcPos;
                int exportStart = bcStart - BytecodeBuilder.SCRIPT_HDR;
                uint bss = PackageSplicer.ReadBSS(data, exportStart);
                int origLen = data.Length;

                byte[] bc = new byte[(int)bss];
                Buffer.BlockCopy(data, bcStart, bc, 0, (int)bss);

                // Compute blob B size by building it
                var (checkVt, fillCache) = FindPhase2Patterns(bc);
                // Blob A is always at the CheckViewTarget position (before it, but we find it via signature)
                int p2SigPos = BytecodeBuilder.FindPattern(data, P2_SIG, bcStart, bcStart + (int)bss);
                // Blob A starts where the P2 signature's GreaterFF condition is in the if-body.
                // The blob A starts 3 bytes before the condition (JumpIfNot opcode + 2 target bytes).
                // Walk back to find the JumpIfNot that begins blob A.
                int blobAStart = -1;
                for (int i = p2SigPos - 1; i >= bcStart && i > p2SigPos - 50; i--)
                {
                    if (data[i] == BytecodeBuilder.OP_JUMP_IF_NOT)
                    {
                        blobAStart = i;
                        break;
                    }
                }
                if (blobAStart == -1)
                    throw new InvalidOperationException("Cannot locate Blob A start for removal");

                int blobABc = blobAStart - bcStart;
                int blobBBc = blobABc + BLOB_A_SIZE;
                byte[] blobB = BytecodeBuilder.BuildBlobB(blobBBc,
                    LOCAL_BLENDPCT, LOCAL_NEWPOV, INST_PCOWNER, INST_MYHUD,
                    INST_SIZEX, INST_SIZEY, INST_DEFAULT_FOV,
                    IMP_FOV, IMP_TPOV);
                int totalP2 = BLOB_A_SIZE + blobB.Length;

                int blobBFile = blobAStart + BLOB_A_SIZE;

                // Remove blob B first (higher offset), then blob A
                data = PackageSplicer.RemoveBytes(data, blobBFile, blobB.Length);
                data = PackageSplicer.RemoveBytes(data, blobAStart, BLOB_A_SIZE);

                // Re-locate function and fix BSS
                funcPos = BytecodeBuilder.FindPattern(data, FUNC_START);
                bcStart = funcPos;
                exportStart = bcStart - BytecodeBuilder.SCRIPT_HDR;
                PackageSplicer.UpdateBSS(data, exportStart, -totalP2);
                bss = PackageSplicer.ReadBSS(data, exportStart);

                // Reverse jump target shifts
                PackageSplicer.FixJumpTargets(data, bcStart, (int)bss, blobABc, -BLOB_A_SIZE);

                hdr = PackageSplicer.ParseHeader(data);
                PackageSplicer.UpdateExportsHeuristic(data, hdr, exportStart, blobAStart, origLen, -totalP2);
            }

            // Remove Phase 1
            if (BytecodeBuilder.FindPattern(data, P1_NEW) != -1)
            {
                int origLen = data.Length;
                int funcPos = BytecodeBuilder.FindPattern(data, FUNC_START);
                int bcStart = funcPos;
                int exportStart = bcStart - BytecodeBuilder.SCRIPT_HDR;

                // Flip bConstrainAspectRatio back to true
                data[bcStart + BC_BOOL] = BytecodeBuilder.OP_TRUE;

                // Find P1_NEW and replace with stock P1_OLD
                int p1Pos = BytecodeBuilder.FindPattern(data, P1_NEW, bcStart);
                byte[] stockAr = BytecodeBuilder.BuildStockArAssignment(AR_PROPERTY_TOKEN);
                data = PackageSplicer.ReplaceBytes(data, p1Pos, P1_NEW.Length, stockAr);

                // Reverse jump target shifts
                funcPos = BytecodeBuilder.FindPattern(data, FUNC_START);
                bcStart = funcPos;
                exportStart = bcStart - BytecodeBuilder.SCRIPT_HDR;
                uint bss = PackageSplicer.ReadBSS(data, exportStart);

                // Shift targets back
                foreach (var (bcOff, oldTarget) in P1_JUMPS)
                {
                    int fp = bcStart + bcOff + 1;
                    ushort cur = BitConverter.ToUInt16(data, fp);
                    ushort expected = (ushort)(oldTarget + PHASE1_NET);
                    if (cur == expected)
                        BitConverter.GetBytes(oldTarget).CopyTo(data, fp);
                }

                PackageSplicer.UpdateBSS(data, exportStart, -PHASE1_NET);

                hdr = PackageSplicer.ParseHeader(data);
                PackageSplicer.UpdateExportsHeuristic(data, hdr, exportStart, p1Pos, origLen, -PHASE1_NET);
            }

            File.WriteAllBytes(enginePath, data);
        }

        // Engine.u patches are always on
        public static void Reconcile(string enginePath)
        {
            var state = DetectState(enginePath);
            if (state == EnginePatchState.FullyPatched)
            {
                ReconcilePhase3(enginePath);
                return;
            }
            Apply(enginePath);
        }

        static void ReconcilePhase3(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            var hdr = PackageSplicer.ParseHeader(data);
            byte[] before = (byte[])data.Clone();
            ApplyPhase3(data, hdr);
            if (!data.AsSpan().SequenceEqual(before))
                File.WriteAllBytes(enginePath, data);
        }

        // Private helpers

        static EnginePatchState DetectStateFromData(byte[] data)
        {
            bool hasP1New = BytecodeBuilder.FindPattern(data, P1_NEW) != -1;
            bool hasP2Sig = BytecodeBuilder.FindPattern(data, P2_SIG) != -1;
            if (hasP2Sig) return EnginePatchState.FullyPatched;
            if (hasP1New) return EnginePatchState.Phase1Only;
            return EnginePatchState.Unpatched;
        }

        static (byte[] checkVt, byte[] fillCache) FindPhase2Patterns(byte[] bc)
        {
            // CheckViewTarget: 1B [name 8] 01 [ViewTarget 4] 16 = 15 bytes
            byte[]? checkVt = null;
            for (int off = 0; off < Math.Min(0x100, bc.Length); off++)
            {
                if (bc[off] == BytecodeBuilder.OP_VIRT_FUNC && off + 14 < bc.Length
                    && bc[off + 9] == BytecodeBuilder.OP_INST_VAR && bc[off + 14] == BytecodeBuilder.OP_END_FP)
                {
                    checkVt = new byte[15];
                    Buffer.BlockCopy(bc, off, checkVt, 0, 15);
                    break;
                }
            }
            if (checkVt == null)
                throw new InvalidOperationException("Cannot find CheckViewTarget VirtualFunction pattern");

            // FillCameraCache: 1C [func 4] 00 [local 4] 16 = 11 bytes
            byte[]? fillCache = null;
            for (int off = bc.Length - 11; off > Math.Max(bc.Length - 100, 0); off--)
            {
                if (bc[off] == BytecodeBuilder.OP_FINAL_FUNC
                    && bc[off + 5] == BytecodeBuilder.OP_LOCAL_VAR
                    && bc[off + 10] == BytecodeBuilder.OP_END_FP)
                {
                    fillCache = new byte[11];
                    Buffer.BlockCopy(bc, off, fillCache, 0, 11);
                    break;
                }
            }
            if (fillCache == null)
                throw new InvalidOperationException("Cannot find FillCameraCache pattern");

            return (checkVt, fillCache);
        }

        static void ApplyPhase3(byte[] data, PackageSplicer.PackageHeader hdr)
        {
            var names = PackageSplicer.ReadNameTable(data, hdr);
            int bcarNi = names.IndexOf("bConstrainAspectRatio");
            int boolNi = names.IndexOf("BoolProperty");
            if (bcarNi < 0 || boolNi < 0) return;

            byte[] bcarUname = BytecodeBuilder.Concat(BitConverter.GetBytes(bcarNi), BitConverter.GetBytes(0));
            byte[] boolUname = BytecodeBuilder.Concat(BitConverter.GetBytes(boolNi), BitConverter.GetBytes(0));
            byte[] patternTrue = BytecodeBuilder.Concat(
                bcarUname, boolUname,
                BitConverter.GetBytes(0), BitConverter.GetBytes(0), BitConverter.GetBytes(1));

            int idx = BytecodeBuilder.FindPattern(data, patternTrue);
            if (idx == -1) return; // already false or not found

            int boolOffset = idx + 24;
            BitConverter.GetBytes(0).CopyTo(data, boolOffset);
        }

        static void RemovePhase3(byte[] data, PackageSplicer.PackageHeader hdr)
        {
            var names = PackageSplicer.ReadNameTable(data, hdr);
            int bcarNi = names.IndexOf("bConstrainAspectRatio");
            int boolNi = names.IndexOf("BoolProperty");
            if (bcarNi < 0 || boolNi < 0) return;

            byte[] bcarUname = BytecodeBuilder.Concat(BitConverter.GetBytes(bcarNi), BitConverter.GetBytes(0));
            byte[] boolUname = BytecodeBuilder.Concat(BitConverter.GetBytes(boolNi), BitConverter.GetBytes(0));
            byte[] patternFalse = BytecodeBuilder.Concat(
                bcarUname, boolUname,
                BitConverter.GetBytes(0), BitConverter.GetBytes(0), BitConverter.GetBytes(0));

            int idx = BytecodeBuilder.FindPattern(data, patternFalse);
            if (idx == -1) return; // already true or not found

            int boolOffset = idx + 24;
            BitConverter.GetBytes(1).CopyTo(data, boolOffset);
        }
    }
}
