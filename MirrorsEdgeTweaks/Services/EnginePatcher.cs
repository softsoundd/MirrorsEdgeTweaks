using System.IO;
using UELib;

namespace MirrorsEdgeTweaks.Services
{
    public enum EnginePatchState { Unpatched, Phase1Only, FullyPatched }

    // Bidirectional patcher for Engine.u
    public static class EnginePatcher
    {
        // bConstrainAspectRatio bool token offset inside UpdateCamera (memory offset, stable).
        const int BC_BOOL = 0x066;

        const int UNPATCHED_BSS = 739;
        const int PHASE1_NET = 11;
        const int P1_OLD_LEN = 11;
        const int BLOB_A_SIZE = 40;

        // Phase 1 jump table: (bc_offset, expected_target) for targets >= 0x0B3.
        static readonly (int bcOff, ushort oldTarget)[] P1_JUMPS = {
            (0x0B3, 0x02D5), (0x0F8, 0x026F), (0x13C, 0x015B), (0x158, 0x0224),
            (0x15B, 0x0188), (0x185, 0x0224), (0x188, 0x01BB), (0x1B8, 0x0224),
            (0x1BB, 0x01EE), (0x1EB, 0x0224), (0x1EE, 0x0221), (0x21E, 0x0224),
            (0x26C, 0x02D5),
        };

        // Per file resolved object references and the patterns built from them.
        sealed class Resolved
        {
            public int SerialOffset;

            public byte[] InstDefaultFov = Array.Empty<byte>();
            public byte[] InstDefaultAr = Array.Empty<byte>();
            public byte[] InstPcOwner = Array.Empty<byte>();
            public byte[] InstMyHud = Array.Empty<byte>();
            public byte[] InstSizeX = Array.Empty<byte>();
            public byte[] InstSizeY = Array.Empty<byte>();
            public byte[] ImpFov = Array.Empty<byte>();
            public byte[] ImpTpov = Array.Empty<byte>();
            public byte[] LocalBlendPct = Array.Empty<byte>();
            public byte[] LocalNewPov = Array.Empty<byte>();

            public byte[] ArPropertyToken = Array.Empty<byte>();  // InstVar(ConstrainedAspectRatio)
            public byte[] P1Old = Array.Empty<byte>();            // Let CAR = FloatConst (find prefix)
            public byte[] P1New = Array.Empty<byte>();            // Let CAR = ViewTarget.AspectRatio
            public byte[] P2Sig = Array.Empty<byte>();            // GreaterThan + DefaultAR + FloatConst
        }

        static Resolved Resolve(UnrealPackage pkg)
        {
            var uc = UePackageLocator.FindFunction(pkg, "Camera", "UpdateCamera")
                ?? throw new InvalidOperationException("Cannot find Camera.UpdateCamera in Engine.u");

            var r = new Resolved { SerialOffset = uc.SerialOffset };

            int car = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "ConstrainedAspectRatio", "Camera"));
            int defaultFov = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "DefaultFOV", "Camera"));
            int defaultAr = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "DefaultAspectRatio", "Camera"));
            int pcOwner = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "PCOwner", "Camera"));
            int myHud = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "myHUD", "PlayerController"));
            int sizeX = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "SizeX", "HUD"));
            int sizeY = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "SizeY", "HUD"));
            int viewTarget = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "ViewTarget", "Camera"));
            var aspectRatioObj = UePackageLocator.FindExportObject(pkg, "AspectRatio", "TViewTarget");
            int aspectRatio = UePackageLocator.ObjRef(aspectRatioObj);
            int tViewTarget = aspectRatioObj != null ? UePackageLocator.ObjRef(aspectRatioObj.Outer) : 0;
            int fovImp = UePackageLocator.FindImportObjRef(pkg, "FOV", "FloatProperty");
            int tpovImp = UePackageLocator.FindImportObjRef(pkg, "TPOV", "ScriptStruct");
            // UpdateCamera locals (children of the function export), resolved by name so we
            // never depend on decompiling the (possibly already patched) function body.
            int blendPct = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "BlendPct", "UpdateCamera"));
            int newPov = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "NewPOV", "UpdateCamera"));

            if (car == 0 || defaultFov == 0 || defaultAr == 0 || pcOwner == 0 || myHud == 0
                || sizeX == 0 || sizeY == 0 || viewTarget == 0 || aspectRatio == 0 || tViewTarget == 0
                || fovImp == 0 || tpovImp == 0 || blendPct == 0 || newPov == 0)
                throw new InvalidOperationException("Cannot resolve Engine.u camera properties");

            r.InstDefaultFov = BytecodeBuilder.InstVar(defaultFov);
            r.InstDefaultAr = BytecodeBuilder.InstVar(defaultAr);
            r.InstPcOwner = BytecodeBuilder.InstVar(pcOwner);
            r.InstMyHud = BytecodeBuilder.InstVar(myHud);
            r.InstSizeX = BytecodeBuilder.InstVar(sizeX);
            r.InstSizeY = BytecodeBuilder.InstVar(sizeY);
            r.ImpFov = BytecodeBuilder.I32(fovImp);
            r.ImpTpov = BytecodeBuilder.I32(tpovImp);
            r.LocalBlendPct = BytecodeBuilder.LocalVar(blendPct);
            r.LocalNewPov = BytecodeBuilder.LocalVar(newPov);

            // Index dependent patterns rebuilt from the resolved object references.
            r.ArPropertyToken = BytecodeBuilder.InstVar(car);
            r.P1Old = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_LET }, r.ArPropertyToken,
                new byte[] { BytecodeBuilder.OP_FLOAT_CONST });
            r.P1New = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_LET }, r.ArPropertyToken,
                BytecodeBuilder.StructMemberFov(
                    BytecodeBuilder.I32(aspectRatio), BytecodeBuilder.I32(tViewTarget),
                    BytecodeBuilder.InstVar(viewTarget)));
            r.P2Sig = BytecodeBuilder.GetP2Signature(r.InstDefaultAr);

            return r;
        }

        public static EnginePatchState DetectState(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            using var pkg = UePackageLocator.Load(enginePath);
            var r = Resolve(pkg);
            return DetectFromResolved(data, r);
        }

        static EnginePatchState DetectFromResolved(byte[] data, Resolved r)
        {
            bool hasP1New = BytecodeBuilder.FindPattern(data, r.P1New) != -1;
            bool hasP2Sig = BytecodeBuilder.FindPattern(data, r.P2Sig) != -1;
            if (hasP2Sig) return EnginePatchState.FullyPatched;
            if (hasP1New) return EnginePatchState.Phase1Only;
            return EnginePatchState.Unpatched;
        }

        public static void Apply(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            Resolved r;
            using (var pkg = UePackageLocator.Load(enginePath))
            {
                r = Resolve(pkg);
            }

            int origLen = data.Length;

            bool hasP1New = BytecodeBuilder.FindPattern(data, r.P1New) != -1;
            bool hasP2Sig = BytecodeBuilder.FindPattern(data, r.P2Sig) != -1;
            if (hasP2Sig) return; // already fully patched

            int exportStart = r.SerialOffset;
            int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;
            uint bss = PackageSplicer.ReadBSS(data, exportStart);
            var hdr = PackageSplicer.ParseHeader(data);

            // Phase 1: Unhardcode AR
            if (!hasP1New)
            {
                if (bss != UNPATCHED_BSS)
                    throw new InvalidOperationException($"Engine.u BSS mismatch: expected {UNPATCHED_BSS}, got {bss}");

                int p1Pos = BytecodeBuilder.FindPattern(data, r.P1Old, bcStart, bcStart + (int)bss);
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
                data = PackageSplicer.ReplaceBytes(data, p1Pos, P1_OLD_LEN, r.P1New);

                // Fix export table
                hdr = PackageSplicer.ParseHeader(data);
                PackageSplicer.UpdateExportsHeuristic(data, hdr, exportStart, p1Pos, origLen, PHASE1_NET);
                origLen = data.Length;
            }

            // Phase 2: HOR+/VERT+ FOV scaling (function start is unchanged by Phase 1)
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

            byte[] blobA = BytecodeBuilder.BuildBlobA(pointABc, r.InstDefaultFov, r.InstDefaultAr);
            int blobBBc = pointBBc + blobA.Length;
            byte[] blobB = BytecodeBuilder.BuildBlobB(blobBBc,
                r.LocalBlendPct, r.LocalNewPov, r.InstPcOwner, r.InstMyHud,
                r.InstSizeX, r.InstSizeY, r.InstDefaultFov,
                r.ImpFov, r.ImpTpov);
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
            Resolved r;
            using (var pkg = UePackageLocator.Load(enginePath))
            {
                r = Resolve(pkg);
            }

            var state = DetectFromResolved(data, r);
            if (state == EnginePatchState.Unpatched) return;

            var hdr = PackageSplicer.ParseHeader(data);

            // Remove Phase 3
            RemovePhase3(data, hdr);

            // Remove Phase 2
            if (BytecodeBuilder.FindPattern(data, r.P2Sig) != -1)
            {
                int exportStart = r.SerialOffset;
                int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;
                uint bss = PackageSplicer.ReadBSS(data, exportStart);
                int origLen = data.Length;

                byte[] bc = new byte[(int)bss];
                Buffer.BlockCopy(data, bcStart, bc, 0, (int)bss);

                // Compute blob B size by building it
                var (checkVt, fillCache) = FindPhase2Patterns(bc);
                int p2SigPos = BytecodeBuilder.FindPattern(data, r.P2Sig, bcStart, bcStart + (int)bss);
                // Blob A starts where the P2 signature's GreaterFF condition is in the if-body.
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
                    r.LocalBlendPct, r.LocalNewPov, r.InstPcOwner, r.InstMyHud,
                    r.InstSizeX, r.InstSizeY, r.InstDefaultFov,
                    r.ImpFov, r.ImpTpov);
                int totalP2 = BLOB_A_SIZE + blobB.Length;

                int blobBFile = blobAStart + BLOB_A_SIZE;

                // Remove blob B first (higher offset), then blob A
                data = PackageSplicer.RemoveBytes(data, blobBFile, blobB.Length);
                data = PackageSplicer.RemoveBytes(data, blobAStart, BLOB_A_SIZE);

                // Function start is unchanged; fix BSS
                PackageSplicer.UpdateBSS(data, exportStart, -totalP2);
                bss = PackageSplicer.ReadBSS(data, exportStart);

                // Reverse jump target shifts
                PackageSplicer.FixJumpTargets(data, bcStart, (int)bss, blobABc, -BLOB_A_SIZE);

                hdr = PackageSplicer.ParseHeader(data);
                PackageSplicer.UpdateExportsHeuristic(data, hdr, exportStart, blobAStart, origLen, -totalP2);
            }

            // Remove Phase 1
            if (BytecodeBuilder.FindPattern(data, r.P1New) != -1)
            {
                int origLen = data.Length;
                int exportStart = r.SerialOffset;
                int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;

                // Flip bConstrainAspectRatio back to true
                data[bcStart + BC_BOOL] = BytecodeBuilder.OP_TRUE;

                // Find P1_NEW and replace with stock P1_OLD
                int p1Pos = BytecodeBuilder.FindPattern(data, r.P1New, bcStart);
                byte[] stockAr = BytecodeBuilder.BuildStockArAssignment(r.ArPropertyToken);
                data = PackageSplicer.ReplaceBytes(data, p1Pos, r.P1New.Length, stockAr);

                // Reverse jump target shifts
                uint bss = PackageSplicer.ReadBSS(data, exportStart);

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
