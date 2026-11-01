using System.IO;
using UELib;
using static UELib.Core.UStruct.UByteCodeDecompiler;

namespace MirrorsEdgeTweaks.Services
{
    public enum EnginePatchState { Unpatched, Phase1Only, FullyPatched }

    public static class EnginePatcher
    {
        // UpdateCamera assigns bConstrainAspectRatio from the True token at this script offset on every version.
        const int BC_BOOL = 0x066;

        const int UNPATCHED_BSS = 739;
        const int PHASE1_NET = 11;
        const int P1_OLD_LEN = 11;

        // Phase 1 shifts every jump that targets 0x0B3 or later.
        // Each entry holds a jump's script offset and its stock target.
        static readonly (int bcOff, ushort oldTarget)[] P1_JUMPS = {
            (0x0B3, 0x02D5), (0x0F8, 0x026F), (0x13C, 0x015B), (0x158, 0x0224),
            (0x15B, 0x0188), (0x185, 0x0224), (0x188, 0x01BB), (0x1B8, 0x0224),
            (0x1BB, 0x01EE), (0x1EB, 0x0224), (0x1EE, 0x0221), (0x21E, 0x0224),
            (0x26C, 0x02D5),
        };

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
            public byte[] CameraCacheFov = Array.Empty<byte>();
            public List<int> JumpPositions = new();

            // Stays -1 unless UELib decoded UpdateCamera through to its EndOfScript token.
            public int EndOfScriptOffset = -1;

            public byte[] ArPropertyToken = Array.Empty<byte>();  // InstVar(ConstrainedAspectRatio)

            // Holds the stock ConstrainedAspectRatio = FloatConst assignment up to its constant.
            public byte[] P1Old = Array.Empty<byte>();

            // Holds phase 1's ConstrainedAspectRatio = ViewTarget.AspectRatio assignment.
            public byte[] P1New = Array.Empty<byte>();

            // Holds the start of blob A's condition, for the current and the legacy blob A.
            public byte[] P2Sig = Array.Empty<byte>();
            public byte[] LegacyP2Sig = Array.Empty<byte>();
        }

        static Resolved Resolve(UnrealPackage pkg, byte[] data)
        {
            var uc = UePackageLocator.FindFunction(pkg, "Camera", "UpdateCamera")
                ?? throw new InvalidOperationException("Cannot find Camera.UpdateCamera in Engine.u");
            var getFov = UePackageLocator.FindFunction(pkg, "Camera", "GetFOVAngle")
                ?? throw new InvalidOperationException("Cannot find Camera.GetFOVAngle in Engine.u");

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

            // The UpdateCamera locals are found by name, because the function body may already be patched.
            int blendPct = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "BlendPct", "UpdateCamera"));
            int newPov = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "NewPOV", "UpdateCamera"));

            r.CameraCacheFov = FindCameraCacheFovRead(getFov, data);

            if (car == 0 || defaultFov == 0 || defaultAr == 0 || pcOwner == 0 || myHud == 0
                || sizeX == 0 || sizeY == 0 || viewTarget == 0 || aspectRatio == 0 || tViewTarget == 0
                || fovImp == 0 || tpovImp == 0 || blendPct == 0 || newPov == 0 || r.CameraCacheFov.Length == 0)
                throw new InvalidOperationException("Cannot resolve Engine.u camera properties");

            r.JumpPositions = UePackageLocator.ExtractJumpPositions(uc.Tokens);
            if (uc.Tokens.Count > 0 && uc.Tokens[^1] is EndOfScriptToken end)
                r.EndOfScriptOffset = UePackageLocator.Pos(end);

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
            r.LegacyP2Sig = BytecodeBuilder.GetLegacyP2Signature(r.InstDefaultAr);

            return r;
        }

        // GetFOVAngle ends with return CameraCache.POV.FOV, which is the read that blob A compares DefaultFOV with.
        static byte[] FindCameraCacheFovRead(UeFunctionInfo getFov, byte[] data)
        {
            var tokens = getFov.Tokens;
            int bcStart = getFov.SerialOffset + BytecodeBuilder.SCRIPT_HDR;
            for (int i = 0; i + 3 < tokens.Count; i++)
            {
                if (tokens[i] is not ReturnToken) continue;
                if (tokens[i + 1] is not StructMemberToken || tokens[i + 2] is not StructMemberToken) continue;
                if (tokens[i + 3] is not InstanceVariableToken cache
                    || cache.Object?.Name?.ToString() != "CameraCache") continue;

                int start = bcStart + UePackageLocator.Pos(tokens[i + 1]);
                int end = bcStart + UePackageLocator.Pos(cache) + BytecodeBuilder.VAR_TOKEN_SIZE;
                return data[start..end];
            }
            return Array.Empty<byte>();
        }

        public static EnginePatchState DetectState(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            using var pkg = UePackageLocator.Load(enginePath);
            return DetectState(data, pkg);
        }

        internal static EnginePatchState DetectState(byte[] data, UnrealPackage pkg) =>
            DetectFromResolved(data, Resolve(pkg, data));

        static EnginePatchState DetectFromResolved(byte[] data, Resolved r)
        {
            bool hasP1New = BytecodeBuilder.FindPattern(data, r.P1New) != -1;
            bool hasP2Sig = BytecodeBuilder.FindPattern(data, r.P2Sig) != -1;
            if (hasP2Sig) return EnginePatchState.FullyPatched;
            if (hasP1New) return EnginePatchState.Phase1Only;
            return EnginePatchState.Unpatched;
        }

        // Finds the blob A that releases up to the 4.5.0 betas inserted, which this patcher cannot undo.
        internal static bool HasLegacyBlobA(byte[] data, UnrealPackage pkg) => HasLegacyBlobA(data, Resolve(pkg, data));

        static bool HasLegacyBlobA(byte[] data, Resolved r) => BytecodeBuilder.FindPattern(data, r.LegacyP2Sig) != -1;

        public static void Apply(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            Resolved r;
            using (var pkg = UePackageLocator.Load(enginePath))
            {
                r = Resolve(pkg, data);
            }

            if (HasLegacyBlobA(data, r))
                throw Helpers.LegacyChangeDetector.PackageRestoreRequired("Engine.u");

            int origLen = data.Length;

            bool hasP1New = BytecodeBuilder.FindPattern(data, r.P1New) != -1;
            bool hasP2Sig = BytecodeBuilder.FindPattern(data, r.P2Sig) != -1;
            if (hasP2Sig) return;

            int exportStart = r.SerialOffset;
            int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;
            uint bss = PackageSplicer.ReadBSS(data, exportStart);
            var hdr = PackageSplicer.ParseHeader(data);

            // Phase 1 clears bConstrainAspectRatio and sets ConstrainedAspectRatio from the view target's aspect ratio.
            if (!hasP1New)
            {
                if (bss != UNPATCHED_BSS)
                    throw new InvalidOperationException($"Engine.u BSS mismatch: expected {UNPATCHED_BSS}, got {bss}");

                int p1Pos = BytecodeBuilder.FindPattern(data, r.P1Old, bcStart, bcStart + (int)bss);
                if (p1Pos == -1)
                    throw new InvalidOperationException("Cannot find ConstrainedAspectRatio assignment in Engine.u");

                if (data[bcStart + BC_BOOL] != BytecodeBuilder.OP_TRUE)
                    throw new InvalidOperationException("bConstrainAspectRatio opcode mismatch");
                data[bcStart + BC_BOOL] = BytecodeBuilder.OP_FALSE;

                foreach (var (bcOff, oldTarget) in P1_JUMPS)
                {
                    int fp = bcStart + bcOff + 1;
                    ushort cur = BitConverter.ToUInt16(data, fp);
                    if (cur != oldTarget)
                        throw new InvalidOperationException($"Jump at bc+0x{bcOff:X3}: expected 0x{oldTarget:X4}, got 0x{cur:X4}");
                    BitConverter.GetBytes((ushort)(oldTarget + PHASE1_NET)).CopyTo(data, fp);
                }

                PackageSplicer.WriteBSS(data, exportStart, (uint)(UNPATCHED_BSS + PHASE1_NET));
                data = PackageSplicer.ReplaceBytes(data, p1Pos, P1_OLD_LEN, r.P1New);

                hdr = PackageSplicer.ParseHeader(data);
                PackageSplicer.UpdateExportsHeuristic(data, hdr, exportStart, p1Pos, origLen, PHASE1_NET);
                origLen = data.Length;
            }

            // Phase 2 adds HOR+ and VERT+ FOV scaling.
            // Phase 1 does not move the function's start, so exportStart and bcStart still hold.
            bss = PackageSplicer.ReadBSS(data, exportStart);

            if (bss != UNPATCHED_BSS + PHASE1_NET)
                throw new InvalidOperationException($"Phase 2: expected BSS {UNPATCHED_BSS + PHASE1_NET}, got {bss}");

            byte[] bc = new byte[(int)bss];
            Buffer.BlockCopy(data, bcStart, bc, 0, (int)bss);

            var (checkVt, fillCache) = FindPhase2Patterns(bc);

            int cvtPos = BytecodeBuilder.FindPattern(data, checkVt, bcStart, bcStart + 500);
            if (cvtPos == -1) throw new InvalidOperationException("Cannot find CheckViewTarget");
            int pointABc = cvtPos - bcStart;
            int pointAFile = cvtPos;

            int fcPos = BytecodeBuilder.FindPattern(data, fillCache, bcStart, bcStart + 2000);
            if (fcPos == -1) throw new InvalidOperationException("Cannot find FillCameraCache");
            int pointBBc = fcPos - bcStart;
            int pointBFile = fcPos;

            byte[] blobA = BytecodeBuilder.BuildBlobA(pointABc, r.InstDefaultFov, r.InstDefaultAr, r.CameraCacheFov);
            int blobBBc = pointBBc + blobA.Length;
            byte[] blobB = BuildBlobB(blobBBc, r);
            int totalP2 = blobA.Length + blobB.Length;

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

            // Phase 3 clears CameraActor's default bConstrainAspectRatio.
            ApplyPhase3(data, hdr);

            Helpers.PatchUtility.WritePreservingAttributes(enginePath, data);
        }

        public static void Remove(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            Resolved r;
            using (var pkg = UePackageLocator.Load(enginePath))
            {
                r = Resolve(pkg, data);
            }

            if (HasLegacyBlobA(data, r))
                throw Helpers.LegacyChangeDetector.PackageRestoreRequired("Engine.u");

            var state = DetectFromResolved(data, r);
            if (state == EnginePatchState.Unpatched) return;

            var hdr = PackageSplicer.ParseHeader(data);

            RemovePhase3(data, hdr);

            // Phase 2 is removed before phase 1, because phase 1's jump table does not account for phase 2's shifts.
            if (state == EnginePatchState.FullyPatched)
            {
                int exportStart = r.SerialOffset;
                int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;
                int bss = (int)PackageSplicer.ReadBSS(data, exportStart);
                int origLen = data.Length;

                // The jump fixups need every token, so they refuse a script that UELib stopped decoding part way through.
                if (r.EndOfScriptOffset != bss - 1)
                    throw new InvalidOperationException("Cannot decode all of Camera.UpdateCamera's script");

                // Blob A's 3-byte JumpIfNot comes right before the condition that P2Sig matches.
                int blobAStart = BytecodeBuilder.FindPattern(data, r.P2Sig, bcStart, bcStart + bss) - 3;
                if (blobAStart < bcStart)
                    throw new InvalidOperationException("Cannot locate blob A in Camera.UpdateCamera");
                int blobABc = blobAStart - bcStart;
                byte[] blobA = BytecodeBuilder.BuildBlobA(blobABc, r.InstDefaultFov, r.InstDefaultAr, r.CameraCacheFov);

                // Apply inserts blob B right before the FillCameraCache call.
                byte[] bc = new byte[bss];
                Buffer.BlockCopy(data, bcStart, bc, 0, bss);
                var (_, fillCache) = FindPhase2Patterns(bc);
                int fcPos = BytecodeBuilder.FindPattern(data, fillCache, bcStart, bcStart + bss);
                int blobBLength = BuildBlobB(0, r).Length;
                int blobBStart = fcPos - blobBLength;
                int blobBBc = blobBStart - bcStart;
                byte[] blobB = BuildBlobB(blobBBc, r);

                if (!data.AsSpan(blobAStart, blobA.Length).SequenceEqual(blobA)
                    || !data.AsSpan(blobBStart, blobB.Length).SequenceEqual(blobB))
                    throw new InvalidOperationException("Engine.u's FOV scaling code does not match the code Tweaks inserts");

                // The targets still use the layout with both blobs, so blob B's shift runs first.
                PackageSplicer.ShiftJumpTargets(data, bcStart, r.JumpPositions, blobBBc + blobB.Length, -blobB.Length);
                PackageSplicer.ShiftJumpTargets(data, bcStart, r.JumpPositions, blobABc + blobA.Length, -blobA.Length);

                // Blob B is removed first, because removing blob A would move it.
                data = PackageSplicer.RemoveBytes(data, blobBStart, blobB.Length);
                data = PackageSplicer.RemoveBytes(data, blobAStart, blobA.Length);

                int totalP2 = blobA.Length + blobB.Length;
                PackageSplicer.UpdateBSS(data, exportStart, -totalP2);

                hdr = PackageSplicer.ParseHeader(data);
                PackageSplicer.UpdateExportsHeuristic(data, hdr, exportStart, blobAStart, origLen, -totalP2);
            }

            if (BytecodeBuilder.FindPattern(data, r.P1New) != -1)
            {
                int origLen = data.Length;
                int exportStart = r.SerialOffset;
                int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;

                data[bcStart + BC_BOOL] = BytecodeBuilder.OP_TRUE;

                int p1Pos = BytecodeBuilder.FindPattern(data, r.P1New, bcStart);
                byte[] stockAr = BytecodeBuilder.BuildStockArAssignment(r.ArPropertyToken);
                data = PackageSplicer.ReplaceBytes(data, p1Pos, r.P1New.Length, stockAr);

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

            Helpers.PatchUtility.WritePreservingAttributes(enginePath, data);
        }

        // The Engine.u patches are always applied, so reconciling only adds what is missing.
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
                Helpers.PatchUtility.WritePreservingAttributes(enginePath, data);
        }

        static byte[] BuildBlobB(int insertBc, Resolved r) =>
            BytecodeBuilder.BuildBlobB(insertBc,
                r.LocalBlendPct, r.LocalNewPov, r.InstPcOwner, r.InstMyHud,
                r.InstSizeX, r.InstSizeY, r.InstDefaultFov,
                r.ImpFov, r.ImpTpov);

        static (byte[] checkVt, byte[] fillCache) FindPhase2Patterns(byte[] bc)
        {
            // The CheckViewTarget call is 1B [name 8] 01 [ViewTarget 4] 16, which is 15 bytes.
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

            // The FillCameraCache call is 1C [func 4] 00 [local 4] 16, which is 11 bytes.
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
            if (idx == -1) return;

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
            if (idx == -1) return;

            int boolOffset = idx + 24;
            BitConverter.GetBytes(1).CopyTo(data, boolOffset);
        }
    }
}
