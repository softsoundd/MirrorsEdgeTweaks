using System.IO;
using UELib;
using static UELib.Core.UStruct.UByteCodeDecompiler;

namespace MirrorsEdgeTweaks.Services
{
    public class TdGamePatchState
    {
        public bool CoreApplied { get; set; }
        public bool SensApplied { get; set; }
        public bool ClipApplied { get; set; }
        public bool OnlineSkipApplied { get; set; }
    }

    // Bidirectional patcher for TdGame.u
    public static class TdGamePatcher
    {
        public static TdGamePatchState DetectState(string tdGamePath)
        {
            byte[] data = File.ReadAllBytes(tdGamePath);
            using var pkg = UePackageLocator.LoadHeader(tdGamePath);
            return DetectStateCore(data, pkg);
        }

        public static void Reconcile(string tdGamePath, bool enableSens, bool enableClip,
            bool enableOnlineSkip = false)
        {
            var state = DetectState(tdGamePath);
            bool desiredCore = true;
            bool anyPatched = state.CoreApplied || state.SensApplied || state.ClipApplied
                              || state.OnlineSkipApplied;
            bool stateMatches = state.CoreApplied == desiredCore
                                && state.SensApplied == enableSens
                                && state.ClipApplied == enableClip
                                && state.OnlineSkipApplied == enableOnlineSkip;

            if (stateMatches && !HasBuggyUnzoomPatch(tdGamePath)) return;

            if (anyPatched) Remove(tdGamePath);
            Apply(tdGamePath, enableSens, enableClip, enableOnlineSkip);
        }

        public static void Apply(string tdGamePath, bool enableSens, bool enableClip,
            bool enableOnlineSkip = false)
        {
            byte[] data = File.ReadAllBytes(tdGamePath);

            ResolvedIndices resolved;
            using (var pkg = UePackageLocator.Load(tdGamePath))
            {
                resolved = ResolveIndices(pkg, data);
            }

            // ToggleZoomState: near clip
            int clipNet = 0;
            int tzsSo = resolved.TzsSerialOffset;
            if (enableClip)
            {
                int tzsBcStart = tzsSo + BytecodeBuilder.SCRIPT_HDR;
                int tzsBss = (int)PackageSplicer.ReadBSS(data, tzsSo);

                var clipResult = resolved.ClipResult;
                if (clipResult == null)
                    throw new InvalidOperationException("Cannot find SetNearClippingPlane(10.0) in ToggleZoomState");

                var (ctxOff, ctxLen, dcast, powner, vfunc) = clipResult.Value;
                int replaceStart = tzsBcStart + ctxOff;
                int replaceEnd = tzsBcStart + ctxOff + ctxLen;

                byte[] clipBlob = BytecodeBuilder.BuildClipBlob(ctxOff, dcast, powner, vfunc,
                    resolved.SizexImp, resolved.SizeyImp);
                clipNet = clipBlob.Length - ctxLen;

                PackageSplicer.FixJumpTargets(data, tzsBcStart, tzsBss, replaceStart - tzsBcStart, clipNet);
                PackageSplicer.UpdateBSS(data, tzsSo, clipNet);
                data = PackageSplicer.ReplaceBytes(data, replaceStart, ctxLen, clipBlob);
            }

            // PlayerInput: sensitivity + near clip
            int sensNet = 0;
            int piSo = resolved.PiSerialOffset;
            if (enableSens || enableClip)
            {
                int piSoAdj = piSo + (piSo > tzsSo ? clipNet : 0);
                int piBcStart = piSoAdj + BytecodeBuilder.SCRIPT_HDR;
                int piBss = (int)PackageSplicer.ReadBSS(data, piSoAdj);

                var fovResult = resolved.FovResult;
                if (fovResult == null)
                    throw new InvalidOperationException("Cannot find FOVScale LET in PlayerInput");

                var (letEnd, fovscaleLocal, outerVar, getfovVf) = fovResult.Value;
                int insertBc = letEnd;
                int insertFile = piBcStart + insertBc;

                byte[] clipVfunc = resolved.SncpVfunc;
                byte[] sensBlob = BytecodeBuilder.BuildSensBlob(insertBc,
                    fovscaleLocal, outerVar, getfovVf, clipVfunc,
                    resolved.MyhudImp, resolved.SizexImp, resolved.SizeyImp,
                    resolved.InstPawn, resolved.InstWeapon, resolved.TdweaponDcast,
                    resolved.IsZoomingVf, resolved.InstFovangle,
                    enableSens, enableClip);
                sensNet = sensBlob.Length;

                PackageSplicer.FixJumpTargets(data, piBcStart, piBss, insertBc, sensNet);
                PackageSplicer.UpdateBSS(data, piSoAdj, sensNet);
                data = PackageSplicer.InsertBytes(data, insertFile, sensBlob);
            }

            // StartMove: vertigo
            int smSo = resolved.SmSerialOffset;
            int smShift = 0;
            if (smSo > tzsSo) smShift += clipNet;
            if (smSo > piSo) smShift += sensNet;
            int smSoAdj = smSo + smShift;
            int smBcStart = smSoAdj + BytecodeBuilder.SCRIPT_HDR;
            int smBss = (int)PackageSplicer.ReadBSS(data, smSoAdj);

            var vertigoResult = resolved.VertigoResult;
            if (vertigoResult == null)
                throw new InvalidOperationException("Cannot find StartZoom in TdMove_Vertigo.StartMove");

            var (smStartzoomOff, smZoomfovOff, controllerCtx) = vertigoResult.Value;
            byte[] vertigoReplacement = BytecodeBuilder.BuildVertigoReplacement(controllerCtx, resolved.DefaultFovInst);
            int vertigoOrigSize = 5; // InstanceVar(ZoomFOV)
            int vertigoNet = vertigoReplacement.Length - vertigoOrigSize;

            // Update context skip-size
            int smSkipsizeAdj = smBcStart + smStartzoomOff - 4;
            ushort oldSkip = BitConverter.ToUInt16(data, smSkipsizeAdj);
            BitConverter.GetBytes((ushort)(oldSkip + vertigoNet)).CopyTo(data, smSkipsizeAdj);

            int smReplaceStart = smBcStart + smZoomfovOff;
            PackageSplicer.FixJumpTargets(data, smBcStart, smBss, smReplaceStart - smBcStart, vertigoNet);
            PackageSplicer.UpdateBSS(data, smSoAdj, vertigoNet);
            data = PackageSplicer.ReplaceBytes(data, smReplaceStart, vertigoOrigSize, vertigoReplacement);

            // UnZoom else-branch
            int uzSo = resolved.UzSerialOffset;
            int uzShift = 0;
            if (uzSo > tzsSo) uzShift += clipNet;
            if (uzSo > piSo) uzShift += sensNet;
            if (uzSo > smSo) uzShift += vertigoNet;
            int uzSoAdj = uzSo + uzShift;
            int uzBcStart = uzSoAdj + BytecodeBuilder.SCRIPT_HDR;
            int uzBss = (int)PackageSplicer.ReadBSS(data, uzSoAdj);

            var uzResult = resolved.UnzoomResult;
            if (uzResult == null)
                throw new InvalidOperationException("Cannot find UnZoom patch points");
            var (uzDefaultFov, uzElseFloatOff) = uzResult.Value;

            byte[] uzElseReplacement = BytecodeBuilder.BuildUnzoomElseReplacement(uzDefaultFov, resolved.InstFovangle);
            int uzElseOrigSize = 5; // FloatConst(20.0)
            int uzElseNet = uzElseReplacement.Length - uzElseOrigSize;

            int uzElseFile = uzBcStart + uzElseFloatOff;
            PackageSplicer.FixJumpTargets(data, uzBcStart, uzBss, uzElseFloatOff, uzElseNet);
            PackageSplicer.UpdateBSS(data, uzSoAdj, uzElseNet);
            data = PackageSplicer.ReplaceBytes(data, uzElseFile, uzElseOrigSize, uzElseReplacement);

            // SetFOV: cutscene zoom rate
            int sfSo = resolved.SfSerialOffset;
            int sfShift = 0;
            if (sfSo > tzsSo) sfShift += clipNet;
            if (sfSo > piSo) sfShift += sensNet;
            if (sfSo > smSo) sfShift += vertigoNet;
            if (sfSo > uzSo) sfShift += uzElseNet;
            int sfSoAdj = sfSo + sfShift;
            int sfBcStart = sfSoAdj + BytecodeBuilder.SCRIPT_HDR;
            int sfBss = (int)PackageSplicer.ReadBSS(data, sfSoAdj);

            var sfResult = resolved.SetFovResult;
            if (sfResult == null)
                throw new InvalidOperationException("Cannot find StartZoom call in SetFOV");
            var (sfInsertBc, sfLocalNewfov, sfLocalRate, sfDcast, sfControllerVar) = sfResult.Value;

            byte[] setfovBlob = BytecodeBuilder.BuildSetFovRateInsert(sfInsertBc,
                sfLocalRate, sfLocalNewfov, sfDcast, sfControllerVar, resolved.DefaultFovInst);
            int setfovNet = setfovBlob.Length;

            int sfInsertFile = sfBcStart + sfInsertBc;
            PackageSplicer.FixJumpTargets(data, sfBcStart, sfBss, sfInsertBc, setfovNet);
            PackageSplicer.UpdateBSS(data, sfSoAdj, setfovNet);
            data = PackageSplicer.InsertBytes(data, sfInsertFile, setfovBlob);

            // StartConnection: online skip
            int scSo = resolved.ScSerialOffset;
            int onlineSkipNet = 0;
            if (enableOnlineSkip && scSo > 0
                && resolved.OnPlayOfflinePropI32.Length == 4
                && resolved.OnPlayOfflineFnameBytes.Length == 8)
            {
                int scShift = 0;
                if (scSo > tzsSo) scShift += clipNet;
                if (scSo > piSo) scShift += sensNet;
                if (scSo > smSo) scShift += vertigoNet;
                if (scSo > uzSo) scShift += uzElseNet;
                if (scSo > sfSo) scShift += setfovNet;
                int scSoAdj = scSo + scShift;
                int scBcStart = scSoAdj + BytecodeBuilder.SCRIPT_HDR;
                int scBss = (int)PackageSplicer.ReadBSS(data, scSoAdj);

                var connReq = resolved.ConnReqBoolvar;
                if (connReq == null)
                    throw new InvalidOperationException("Cannot find ConnectionRequired BoolVar in StartConnection");

                var branch = resolved.ElseBranch;
                if (branch == null)
                    throw new InvalidOperationException("Cannot find if(Connection.IsLoggedIn()) branch in StartConnection");

                int elseTarget = branch.Value.elseTarget;
                byte[] onlineSkipBlob = BytecodeBuilder.BuildOnlineSkipBlob(
                    elseTarget, connReq,
                    resolved.OnPlayOfflinePropI32, resolved.OnPlayOfflineFnameBytes);
                onlineSkipNet = onlineSkipBlob.Length;

                int scInsertFile = scBcStart + elseTarget;
                // Strict > semantics: existing JumpIfNot targeting elseTarget should
                // still land at our new code, so threshold is elseTarget + 1
                PackageSplicer.FixJumpTargets(data, scBcStart, scBss, elseTarget + 1, onlineSkipNet);
                PackageSplicer.UpdateBSS(data, scSoAdj, onlineSkipNet);
                data = PackageSplicer.InsertBytes(data, scInsertFile, onlineSkipBlob);
            }

            // Fix export table
            var modifications = new List<(int, int, int)>();
            if (clipNet != 0) modifications.Add((tzsSo, clipNet, resolved.TzsExportIndex));
            if (sensNet != 0) modifications.Add((piSo, sensNet, resolved.PiExportIndex));
            modifications.Add((smSo, vertigoNet, resolved.SmExportIndex));
            modifications.Add((uzSo, uzElseNet, resolved.UzExportIndex));
            modifications.Add((sfSo, setfovNet, resolved.SfExportIndex));
            if (onlineSkipNet != 0) modifications.Add((scSo, onlineSkipNet, resolved.ScExportIndex));

            var hdr = PackageSplicer.ParseHeader(data);
            PackageSplicer.UpdateExportsStructural(data, hdr, modifications);

            File.WriteAllBytes(tdGamePath, data);
        }

        public static void Remove(string tdGamePath)
        {
            byte[] data = File.ReadAllBytes(tdGamePath);

            ResolvedIndices resolved;
            TdGamePatchState state;
            using (var pkg = UePackageLocator.Load(tdGamePath))
            {
                resolved = ResolveIndices(pkg, data);
                state = DetectStateCore(data, pkg);
            }

            if (!state.CoreApplied && !state.SensApplied && !state.ClipApplied
                && !state.OnlineSkipApplied) return;

            // Collect all removals first (function serial offsets, deltas, export indices)
            // then apply them in a single coordinated pass from highest offset to lowest.
            // This ensures each removal doesn't shift offsets for the others.
            var ops = new List<RemovalOp>();

            // Analyse each patched function and compute the removal operation
            AnalyzeOnlineSkipRemoval(data, resolved, state, ops);
            AnalyzeSetFovRemoval(data, resolved, ops);
            AnalyzeUnzoomRemoval(data, resolved, ops);
            AnalyzeVertigoRemoval(data, resolved, ops);
            AnalyzePlayerInputRemoval(data, resolved, state, ops);
            AnalyzeToggleZoomStateRemoval(data, resolved, state, ops);

            if (ops.Count == 0) return;

            // Sort by file position descending so we remove from end to start
            ops.Sort((a, b) => b.FilePos.CompareTo(a.FilePos));

            // Apply all removals/replacements to the data
            foreach (var op in ops)
            {
                int bcStart = op.ExportSerialOffset + BytecodeBuilder.SCRIPT_HDR;
                int bss = (int)PackageSplicer.ReadBSS(data, op.ExportSerialOffset);

                if (op.SkipSizeFixPos >= 0 && op.SkipSizeFixPos < data.Length - 1)
                {
                    int curSkip = BitConverter.ToUInt16(data, op.SkipSizeFixPos);
                    int newSkip = curSkip + op.BssDelta;
                    if (newSkip > 0 && newSkip <= 0xFFFF)
                        BitConverter.GetBytes((ushort)newSkip).CopyTo(data, op.SkipSizeFixPos);
                }

                int thresholdBc = op.JumpFixThresholdBc > 0 ? op.JumpFixThresholdBc : (op.FilePos - bcStart);
                PackageSplicer.FixJumpTargets(data, bcStart, bss, thresholdBc, op.BssDelta);
                PackageSplicer.UpdateBSS(data, op.ExportSerialOffset, op.BssDelta);

                if (op.ReplacementBytes != null)
                    data = PackageSplicer.ReplaceBytes(data, op.FilePos, op.RemoveCount, op.ReplacementBytes);
                else
                    data = PackageSplicer.RemoveBytes(data, op.FilePos, op.RemoveCount);
            }

            // Fix export table in one pass
            var modifications = ops.Select(op =>
                (op.OriginalSerialOffset, op.BssDelta, op.ExportIndex)).ToList();
            var hdr = PackageSplicer.ParseHeader(data);
            PackageSplicer.UpdateExportsStructural(data, hdr, modifications);

            File.WriteAllBytes(tdGamePath, data);
        }

        struct RemovalOp
        {
            public int FilePos; // where to remove/replace in the file
            public int RemoveCount; // bytes to remove
            public byte[]? ReplacementBytes; // null = pure removal, non-null = replacement
            public int BssDelta; // net change to BSS (negative)
            public int ExportSerialOffset; // current serial offset of the containing export
            public int OriginalSerialOffset; // original serial offset (for export table fixup)
            public int ExportIndex;
            public int SkipSizeFixPos; // -1 if no skip-size fix needed
            public int JumpFixThresholdBc; // 0 (default) to derive from FilePos, > 0 to use directly
        }

        // Removal analysis - compute what to remove without modifying data

        static void AnalyzeOnlineSkipRemoval(byte[] data, ResolvedIndices r, TdGamePatchState state, List<RemovalOp> ops)
        {
            if (!state.OnlineSkipApplied || r.ScExportIndex == 0) return;

            int scSo = r.ScSerialOffset;
            int scBcStart = scSo + BytecodeBuilder.SCRIPT_HDR;
            int scBss = (int)PackageSplicer.ReadBSS(data, scSo);

            int sigPos = BytecodeBuilder.FindPattern(data, BytecodeBuilder.OnlineSkipSignature,
                scBcStart, scBcStart + scBss);
            if (sigPos == -1) return;

            // The blob starts 3 bytes before the signature (JumpIfNot + u16 target)
            int blobStart = sigPos - 3;
            if (blobStart < scBcStart) return;
            int blobSize = 30;
            int elseTargetBc = blobStart - scBcStart;

            ops.Add(new RemovalOp
            {
                FilePos = blobStart,
                RemoveCount = blobSize,
                ReplacementBytes = null,
                BssDelta = -blobSize,
                ExportSerialOffset = scSo,
                OriginalSerialOffset = r.ScSerialOffset,
                ExportIndex = r.ScExportIndex,
                SkipSizeFixPos = -1,
                // Apply used strict > (threshold = elseTarget + 1), so the JumpIfNot
                // targeting elseTarget was never shifted. Removal must match.
                JumpFixThresholdBc = elseTargetBc + 1,
            });
        }

        static void AnalyzeSetFovRemoval(byte[] data, ResolvedIndices r, List<RemovalOp> ops)
        {
            int sfSo = r.SfSerialOffset;
            int sfBcStart = sfSo + BytecodeBuilder.SCRIPT_HDR;
            int sfBss = (int)PackageSplicer.ReadBSS(data, sfSo);

            var sfResult = r.SetFovResult;
            if (sfResult == null) return;
            var (sfInsertBc, sfLocalNewfov, sfLocalRate, sfDcast, sfControllerVar) = sfResult.Value;

            byte[] setfovBlob = BytecodeBuilder.BuildSetFovRateInsert(0,
                sfLocalRate, sfLocalNewfov, sfDcast, sfControllerVar, r.DefaultFovInst);
            int blobSize = setfovBlob.Length;
            int blobStartBc = sfInsertBc - blobSize;
            if (blobStartBc < 0) return;

            ops.Add(new RemovalOp
            {
                FilePos = sfBcStart + blobStartBc,
                RemoveCount = blobSize,
                ReplacementBytes = null,
                BssDelta = -blobSize,
                ExportSerialOffset = sfSo,
                OriginalSerialOffset = r.SfSerialOffset,
                ExportIndex = r.SfExportIndex,
                SkipSizeFixPos = -1,
            });
        }

        static void AnalyzeUnzoomRemoval(byte[] data, ResolvedIndices r, List<RemovalOp> ops)
        {
            int uzSo = r.UzSerialOffset;
            int uzBcStart = uzSo + BytecodeBuilder.SCRIPT_HDR;
            int uzBss = (int)PackageSplicer.ReadBSS(data, uzSo);

            int fmaxPos = BytecodeBuilder.FindPattern(data, BytecodeBuilder.ZoomRateSignature,
                uzBcStart, uzBcStart + uzBss);
            if (fmaxPos == -1) return;

            byte[] uzElseReplacement = BytecodeBuilder.BuildUnzoomElseReplacement(r.UzDefaultFov, r.InstFovangle);

            // version4.3.0 may have placed the FMax blob in the StartZoom delay
            // arg instead of the else-branch zoom rate. Detect this in the else-branch
            // the preceding 5-byte token is InstVar(FOVZoomRate) - in the StartZoom call
            // it's LocalVar(Rate)
            bool isBuggyPosition = fmaxPos >= uzBcStart + 5
                && data[fmaxPos - 5] == BytecodeBuilder.OP_LOCAL_VAR;
            byte[] stockBytes = isBuggyPosition
                ? BytecodeBuilder.FloatConst(0.0f)
                : BytecodeBuilder.BuildStockUnzoomRate();

            ops.Add(new RemovalOp
            {
                FilePos = fmaxPos,
                RemoveCount = uzElseReplacement.Length,
                ReplacementBytes = stockBytes,
                BssDelta = -(uzElseReplacement.Length - stockBytes.Length),
                ExportSerialOffset = uzSo,
                OriginalSerialOffset = r.UzSerialOffset,
                ExportIndex = r.UzExportIndex,
                SkipSizeFixPos = -1,
            });
        }

        static void AnalyzeVertigoRemoval(byte[] data, ResolvedIndices r, List<RemovalOp> ops)
        {
            int smSo = r.SmSerialOffset;
            int smBcStart = smSo + BytecodeBuilder.SCRIPT_HDR;
            int smBss = (int)PackageSplicer.ReadBSS(data, smSo);

            int sigPos = BytecodeBuilder.FindPattern(data, BytecodeBuilder.VertigoSignature,
                smBcStart, smBcStart + smBss);
            if (sigPos == -1) return;

            byte[] vertigoReplacement = BytecodeBuilder.BuildVertigoReplacement(r.ControllerCtx, r.DefaultFovInst);
            byte[] stockZoomFov = r.InstZoomFov;
            if (stockZoomFov.Length != 5) return;

            int vertigoNet = -(vertigoReplacement.Length - stockZoomFov.Length);

            // Find the skip-size position to fix
            int skipFixPos = -1;
            for (int i = sigPos - 1; i >= Math.Max(smBcStart, sigPos - 20); i--)
            {
                if (data[i] == BytecodeBuilder.OP_VIRT_FUNC)
                {
                    skipFixPos = i - 4;
                    if (skipFixPos < smBcStart) skipFixPos = -1;
                    break;
                }
            }

            ops.Add(new RemovalOp
            {
                FilePos = sigPos,
                RemoveCount = vertigoReplacement.Length,
                ReplacementBytes = stockZoomFov,
                BssDelta = vertigoNet,
                ExportSerialOffset = smSo,
                OriginalSerialOffset = r.SmSerialOffset,
                ExportIndex = r.SmExportIndex,
                SkipSizeFixPos = skipFixPos,
            });
        }

        static void AnalyzePlayerInputRemoval(byte[] data, ResolvedIndices r, TdGamePatchState state, List<RemovalOp> ops)
        {
            if (!state.SensApplied && !state.ClipApplied) return;

            int piSo = r.PiSerialOffset;
            int piBcStart = piSo + BytecodeBuilder.SCRIPT_HDR;
            int piBss = (int)PackageSplicer.ReadBSS(data, piSo);

            bool hasSens = BytecodeBuilder.FindPattern(data, BytecodeBuilder.SensSignature,
                piBcStart, piBcStart + piBss) != -1;
            byte[] piClipSig = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_FMIN }, BytecodeBuilder.FloatConst(10.0f));
            bool hasClip = BytecodeBuilder.FindPattern(data, piClipSig, piBcStart, piBcStart + piBss) != -1;

            if (!hasSens && !hasClip) return;

            var fovResult = r.FovResult;
            if (fovResult == null) return;

            int blobStartBc = fovResult.Value.letEnd;
            byte[] sensBlob = BytecodeBuilder.BuildSensBlob(blobStartBc,
                fovResult.Value.fovscaleLocal, fovResult.Value.outerVar,
                fovResult.Value.getfovVf, r.SncpVfunc,
                r.MyhudImp, r.SizexImp, r.SizeyImp,
                r.InstPawn, r.InstWeapon, r.TdweaponDcast,
                r.IsZoomingVf, r.InstFovangle,
                hasSens, hasClip);
            int blobSize = sensBlob.Length;
            if (blobSize <= 0) return;

            ops.Add(new RemovalOp
            {
                FilePos = piBcStart + blobStartBc,
                RemoveCount = blobSize,
                ReplacementBytes = null,
                BssDelta = -blobSize,
                ExportSerialOffset = piSo,
                OriginalSerialOffset = r.PiSerialOffset,
                ExportIndex = r.PiExportIndex,
                SkipSizeFixPos = -1,
            });
        }

        static void AnalyzeToggleZoomStateRemoval(byte[] data, ResolvedIndices r, TdGamePatchState state, List<RemovalOp> ops)
        {
            if (!state.ClipApplied) return;

            int tzsSo = r.TzsSerialOffset;
            int tzsBcStart = tzsSo + BytecodeBuilder.SCRIPT_HDR;
            int tzsBss = (int)PackageSplicer.ReadBSS(data, tzsSo);

            int sigPos = BytecodeBuilder.FindPattern(data, BytecodeBuilder.ClipSignature,
                tzsBcStart, tzsBcStart + tzsBss);
            if (sigPos == -1) return;

            // Walk back to find the JumpIfNot that starts the clip blob
            int blobStart = -1;
            for (int i = sigPos - 1; i >= tzsBcStart && i > sigPos - 80; i--)
            {
                if (data[i] == BytecodeBuilder.OP_JUMP_IF_NOT) { blobStart = i; break; }
            }
            if (blobStart == -1) return;

            // Find blob end via the Jump instruction's target
            for (int i = blobStart + 3; i < tzsBcStart + tzsBss; i++)
            {
                if (data[i] == BytecodeBuilder.OP_JUMP)
                {
                    ushort jumpTarget = BitConverter.ToUInt16(data, i + 1);
                    int blobEndFile = tzsBcStart + jumpTarget;
                    int blobSize = blobEndFile - blobStart;
                    if (blobSize <= 0 || blobSize > tzsBss) continue;

                    var clipExtract = ExtractContextCallFromPatchedBlob(data, blobStart, blobEndFile, tzsBcStart);
                    byte[] stockCall = BytecodeBuilder.BuildStockClipCall(
                        clipExtract.dcast, clipExtract.powner, clipExtract.vfunc);

                    ops.Add(new RemovalOp
                    {
                        FilePos = blobStart,
                        RemoveCount = blobSize,
                        ReplacementBytes = stockCall,
                        BssDelta = -(blobSize - stockCall.Length),
                        ExportSerialOffset = tzsSo,
                        OriginalSerialOffset = r.TzsSerialOffset,
                        ExportIndex = r.TzsExportIndex,
                        SkipSizeFixPos = -1,
                    });
                    return;
                }
            }
        }

        // Index resolution

        class ResolvedIndices
        {
            // Export serial offsets and indices
            public int TzsSerialOffset, TzsExportIndex;
            public int PiSerialOffset, PiExportIndex;
            public int SmSerialOffset, SmExportIndex;
            public int UzSerialOffset, UzExportIndex;
            public int SfSerialOffset, SfExportIndex;
            public int ScSerialOffset, ScExportIndex;  // StartConnection

            // Import package indices
            public int SizexImp, SizeyImp, MyhudImp;

            // Token arrays extracted from bytecodes
            public byte[] SncpVfunc = Array.Empty<byte>();
            public byte[] InstPawn = Array.Empty<byte>();
            public byte[] InstWeapon = Array.Empty<byte>();
            public byte[] TdweaponDcast = Array.Empty<byte>();
            public byte[] IsZoomingVf = Array.Empty<byte>();
            public byte[] InstFovangle = Array.Empty<byte>();
            public byte[] DefaultFovInst = Array.Empty<byte>();
            public byte[] ControllerCtx = Array.Empty<byte>();
            public byte[] InstZoomFov = Array.Empty<byte>();
            public byte[] UzDefaultFov = Array.Empty<byte>();

            // Online skip: delegate property export index (4-byte LE i32) and FName (8 bytes)
            public byte[] OnPlayOfflinePropI32 = Array.Empty<byte>();
            public byte[] OnPlayOfflineFnameBytes = Array.Empty<byte>();

            // Patch point token positions/spans located from UELib token stream up
            // front so the splice logic never needs to rescan raw bytecode
            public (int ctxOff, int ctxLen, byte[] dcast, byte[] powner, byte[] vfunc)? ClipResult;
            public (int letEnd, byte[] fovscaleLocal, byte[] outerVar, byte[] getfovVf)? FovResult;
            public (int startzoomOff, int zoomfovOff, byte[] controllerCtx)? VertigoResult;
            public (byte[] instDefaultfov, int elseFloatOff)? UnzoomResult;
            public (int insertOff, byte[] localNewfov, byte[] localRate, byte[] dcast, byte[] controllerVar)? SetFovResult;
            public byte[]? ConnReqBoolvar;
            public (int jnotOff, int elseTarget)? ElseBranch;
        }

        static ResolvedIndices ResolveIndices(UnrealPackage pkg, byte[] data)
        {
            var r = new ResolvedIndices();

            // HUD imports
            r.SizexImp = UePackageLocator.FindImportObjRef(pkg, "SizeX", "FloatProperty");
            r.SizeyImp = UePackageLocator.FindImportObjRef(pkg, "SizeY", "FloatProperty");
            r.MyhudImp = UePackageLocator.FindImportObjRef(pkg, "myHUD", "ObjectProperty");
            if (r.SizexImp == 0 || r.SizeyImp == 0 || r.MyhudImp == 0)
                throw new InvalidOperationException("Cannot find HUD imports in TdGame.u");

            var tzs = UePackageLocator.FindFunction(pkg, "TdHUD", "ToggleZoomState")
                ?? throw new InvalidOperationException("Cannot find TdHUD.ToggleZoomState");
            var pi = UePackageLocator.FindFunction(pkg, "TdPlayerInput", "PlayerInput")
                ?? throw new InvalidOperationException("Cannot find TdPlayerInput.PlayerInput");
            var sm = UePackageLocator.FindFunction(pkg, "TdMove_Vertigo", "StartMove")
                ?? throw new InvalidOperationException("Cannot find TdMove_Vertigo.StartMove");
            var uz = UePackageLocator.FindFunction(pkg, "TdPlayerController", "UnZoom")
                ?? throw new InvalidOperationException("Cannot find TdPlayerController.UnZoom");
            var sf = UePackageLocator.FindFunction(pkg, "TdPlayerPawn", "SetFOV")
                ?? throw new InvalidOperationException("Cannot find TdPlayerPawn.SetFOV");
            var ez = UePackageLocator.FindFunction(pkg, "TdPlayerController", "EndZoom")
                ?? throw new InvalidOperationException("Cannot find TdPlayerController.EndZoom");

            r.TzsSerialOffset = tzs.SerialOffset; r.TzsExportIndex = tzs.ExportIndex;
            r.PiSerialOffset = pi.SerialOffset; r.PiExportIndex = pi.ExportIndex;
            r.SmSerialOffset = sm.SerialOffset; r.SmExportIndex = sm.ExportIndex;
            r.UzSerialOffset = uz.SerialOffset; r.UzExportIndex = uz.ExportIndex;
            r.SfSerialOffset = sf.SerialOffset; r.SfExportIndex = sf.ExportIndex;

            // Function bodies used to harvest raw token spans located via the token stream.
            byte[] tzsBc = Body(data, tzs.SerialOffset);
            byte[] piBc = Body(data, pi.SerialOffset);
            byte[] smBc = Body(data, sm.SerialOffset);
            byte[] uzBc = Body(data, uz.SerialOffset);
            byte[] sfBc = Body(data, sf.SerialOffset);
            byte[] ezBc = Body(data, ez.SerialOffset);

            // Precompute patch point locations from the decompiled token streams.
            r.ClipResult = ExtractContextCall(tzs.Tokens, tzsBc, 10.0f)
                ?? ExtractContextCall(tzs.Tokens, tzsBc, null);
            r.FovResult = FindFovScaleLet(pi.Tokens, piBc);
            r.VertigoResult = FindVertigoStartZoom(sm.Tokens, smBc);
            r.UnzoomResult = FindUnzoomPatches(uz.Tokens, uzBc);
            r.SetFovResult = FindSetFovInsertion(sf.Tokens, sfBc);

            // SetNearClippingPlane vfunc token
            r.SncpVfunc = r.ClipResult != null
                ? r.ClipResult.Value.vfunc
                : HarvestVFunc(tzs.Tokens, tzsBc, "SetNearClippingPlane");

            // Tokens harvested from UnZoom (TdWeapon cast, Pawn, Weapon, IsZoomingOrZoomed, DefaultFOV).
            ExtractZoomTokens(uz.Tokens, uzBc, r);

            // DefaultFOV / FOVAngle from EndZoom (both InstanceVariables of TdPlayerController).
            r.DefaultFovInst = HarvestInst(ez.Tokens, ezBc, "DefaultFOV");
            r.InstFovangle = HarvestInst(ez.Tokens, ezBc, "FOVAngle");

            // Controller context chain at the very start of StartMove (Context.DynamicCast(...));
            if (sm.Tokens.Count >= 2 && sm.Tokens[0] is ContextToken && sm.Tokens[1] is DynamicCastToken)
                r.ControllerCtx = UePackageLocator.Harvest(smBc, sm.Tokens[1], 20);

            // InstanceVar(ZoomFOV)
            byte[] zoomFov = HarvestInst(sm.Tokens, smBc, "ZoomFOV");
            if (zoomFov.Length == 5)
            {
                r.InstZoomFov = zoomFov;
            }
            else
            {
                int zoomFovIdx = UePackageLocator.FindExportIndex(pkg, "ZoomFOV", "TdMove_Vertigo");
                if (zoomFovIdx != 0)
                    r.InstZoomFov = BytecodeBuilder.InstVar(zoomFovIdx);
            }

            // Online skip: StartConnection + OnPlayOffline delegate.
            var sc = UePackageLocator.FindFunction(pkg, "TdOnlineLoginHandler", "StartConnection");
            if (sc != null)
            {
                r.ScSerialOffset = sc.SerialOffset;
                r.ScExportIndex = sc.ExportIndex;
                byte[] scBc = Body(data, sc.SerialOffset);

                r.ConnReqBoolvar = FindConnectionRequiredBoolvar(sc.Tokens, scBc);
                r.ElseBranch = FindElseBranch(sc.Tokens);

                int opoIdx = UePackageLocator.FindExportIndex(pkg, "__OnPlayOffline__Delegate", "TdOnlineLoginHandler");
                if (opoIdx != 0)
                    r.OnPlayOfflinePropI32 = BytecodeBuilder.I32(opoIdx);

                int opoNameIdx = UePackageLocator.FindNameIndex(pkg, "OnPlayOffline");
                if (opoNameIdx >= 0)
                    r.OnPlayOfflineFnameBytes = BytecodeBuilder.Concat(
                        BytecodeBuilder.I32(opoNameIdx), BytecodeBuilder.I32(0));
            }

            return r;
        }

        // Reads a function body (bytecode) slice from the package bytes.
        static byte[] Body(byte[] data, int serialOffset)
        {
            int bcStart = serialOffset + BytecodeBuilder.SCRIPT_HDR;
            int bss = (int)PackageSplicer.ReadBSS(data, serialOffset);
            var bc = new byte[bss];
            Buffer.BlockCopy(data, bcStart, bc, 0, bss);
            return bc;
        }

        // Harvests the 5-byte InstanceVariable token for a named property
        static byte[] HarvestInst(IList<Token> tokens, byte[] bc, string name)
        {
            foreach (var t in tokens)
                if (t is InstanceVariableToken iv && iv.Object?.Name?.ToString() == name)
                    return UePackageLocator.Harvest(bc, t, BytecodeBuilder.VAR_TOKEN_SIZE);
            return Array.Empty<byte>();
        }

        // Harvests the 9-byte VirtualFunction name token for a named call (empty if absent).
        static byte[] HarvestVFunc(IList<Token> tokens, byte[] bc, string name)
        {
            foreach (var t in tokens)
                if (t is VirtualFunctionToken vf && vf.FunctionName?.ToString() == name)
                    return UePackageLocator.Harvest(bc, t, BytecodeBuilder.NAME_TOKEN_SIZE);
            return Array.Empty<byte>();
        }

        static void ExtractZoomTokens(IList<Token> tokens, byte[] uzBc, ResolvedIndices r)
        {
            foreach (var t in tokens)
            {
                if (r.TdweaponDcast.Length == 0 && t is DynamicCastToken dc
                    && dc.CastClass?.Name?.ToString() == "TdWeapon")
                    r.TdweaponDcast = UePackageLocator.Harvest(uzBc, t, BytecodeBuilder.VAR_TOKEN_SIZE);

                if (r.InstPawn.Length == 0 && t is InstanceVariableToken pawn
                    && pawn.Object?.Name?.ToString() == "Pawn")
                    r.InstPawn = UePackageLocator.Harvest(uzBc, t, BytecodeBuilder.VAR_TOKEN_SIZE);

                if (r.InstWeapon.Length == 0 && t is InstanceVariableToken weapon
                    && weapon.Object?.Name?.ToString() == "Weapon")
                    r.InstWeapon = UePackageLocator.Harvest(uzBc, t, BytecodeBuilder.VAR_TOKEN_SIZE);

                if (r.IsZoomingVf.Length == 0 && t is VirtualFunctionToken vf
                    && vf.FunctionName?.ToString() == "IsZoomingOrZoomed")
                    r.IsZoomingVf = UePackageLocator.Harvest(uzBc, t, BytecodeBuilder.NAME_TOKEN_SIZE);
            }

            // DefaultFOV instance var (used to rebuild the UnZoom else-branch).
            r.UzDefaultFov = HarvestInst(tokens, uzBc, "DefaultFOV");
        }

        // Token-based patch-point location. Position == StoragePosition for Mirror's Edge
        // so a token's bytecode offset doubles as its file offset within the function body

        // Context(DynamicCast(owner)).VirtualFunc(FloatConst) - the SetNearClippingPlane calls.
        static (int ctxOff, int ctxLen, byte[] dcast, byte[] powner, byte[] vfunc)?
            ExtractContextCall(IList<Token> tokens, byte[] bc, float? floatValue)
        {
            (int, int, byte[], byte[], byte[])? last = null;
            for (int i = 0; i + 4 < tokens.Count; i++)
            {
                if (tokens[i] is not ContextToken ctx) continue;
                if (tokens[i + 1] is not DynamicCastToken) continue;
                if (tokens[i + 2] is not FieldToken) continue;
                if (tokens[i + 3] is not VirtualFunctionToken) continue;
                if (tokens[i + 4] is not FloatConstToken fc) continue;
                if (floatValue.HasValue && Math.Abs(fc.Value - floatValue.Value) > 0.001f) continue;

                int ctxOff = UePackageLocator.Pos(ctx);
                int ctxLen = UePackageLocator.Pos(fc) + 5 + 1 - ctxOff;   // + FloatConst(5) + EndFP(1)
                byte[] dcast = UePackageLocator.Harvest(bc, tokens[i + 1], BytecodeBuilder.VAR_TOKEN_SIZE);
                byte[] powner = UePackageLocator.Harvest(bc, tokens[i + 2], BytecodeBuilder.VAR_TOKEN_SIZE);
                byte[] vfunc = UePackageLocator.Harvest(bc, tokens[i + 3], BytecodeBuilder.NAME_TOKEN_SIZE);
                var result = (ctxOff, ctxLen, dcast, powner, vfunc);
                if (floatValue.HasValue) return result;
                last = result;
            }
            return last;
        }

        // FOVScale = Context(Outer).GetFOVAngle() * K_SENS
        static (int letEnd, byte[] fovscaleLocal, byte[] outerVar, byte[] getfovVf)?
            FindFovScaleLet(IList<Token> tokens, byte[] bc)
        {
            for (int i = 0; i + 8 < tokens.Count; i++)
            {
                if (tokens[i] is not LetToken) continue;
                if (tokens[i + 1] is not LocalVariableToken lv
                    || lv.Object?.Name?.ToString() != "FOVScale") continue;
                if (tokens[i + 2] is not NativeFunctionToken) continue;       // multiply
                if (tokens[i + 3] is not ContextToken) continue;
                if (tokens[i + 4] is not FieldToken outerVarTok) continue;
                if (tokens[i + 5] is not VirtualFunctionToken vf) continue;
                if (tokens[i + 6] is not EndFunctionParmsToken) continue;
                if (tokens[i + 7] is not FloatConstToken) continue;
                if (tokens[i + 8] is not EndFunctionParmsToken endfp) continue;

                int letEnd = UePackageLocator.Pos(endfp) + 1;
                byte[] fovscaleLocal = UePackageLocator.Harvest(bc, lv, BytecodeBuilder.VAR_TOKEN_SIZE);
                byte[] outerVar = UePackageLocator.Harvest(bc, outerVarTok, BytecodeBuilder.VAR_TOKEN_SIZE);
                byte[] getfovVf = UePackageLocator.Harvest(bc, vf, BytecodeBuilder.NAME_TOKEN_SIZE);
                return (letEnd, fovscaleLocal, outerVar, getfovVf);
            }
            return null;
        }

        // Controller.StartZoom(ZoomFOV, ZoomRate, 0.0) in the stock StartMove.
        static (int startzoomOff, int zoomfovOff, byte[] controllerCtx)?
            FindVertigoStartZoom(IList<Token> tokens, byte[] bc)
        {
            if (tokens.Count < 3) return null;
            if (tokens[0] is not ContextToken) return null;
            if (tokens[1] is not DynamicCastToken dcast) return null;
            if (tokens[2] is not ContextToken) return null;
            byte[] controllerCtx = UePackageLocator.Harvest(bc, dcast, 20);

            for (int i = 0; i + 3 < tokens.Count; i++)
            {
                if (tokens[i] is not VirtualFunctionToken vf
                    || vf.FunctionName?.ToString() != "StartZoom") continue;
                if (tokens[i + 1] is not FieldToken) continue;   // ZoomFOV
                if (tokens[i + 2] is not FieldToken) continue;   // ZoomRate
                if (tokens[i + 3] is not FloatConstToken fc || Math.Abs(fc.Value) > 0.001f) continue;
                return (UePackageLocator.Pos(vf), UePackageLocator.Pos(vf) + 9, controllerCtx);
            }
            return null;
        }

        // StartZoom(DefaultFOV, ...) followed by the else-branch FOVZoomRate FloatConst.
        // Returns the DefaultFOV instance-var token and the bytecode offset of the FloatConst
        // that the UnZoom else-branch replacement overwrites
        static (byte[] instDefaultfov, int elseFloatOff)?
            FindUnzoomPatches(IList<Token> tokens, byte[] bc)
        {
            for (int i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i] is not VirtualFunctionToken vf
                    || vf.FunctionName?.ToString() != "StartZoom") continue;
                if (tokens[i + 1] is not InstanceVariableToken dfov
                    || dfov.Object?.Name?.ToString() != "DefaultFOV") continue;
                byte[] instDefaultfov = UePackageLocator.Harvest(bc, dfov, BytecodeBuilder.VAR_TOKEN_SIZE);

                // First FloatConst after the StartZoom call's closing EndFunctionParms
                // (the else-branch FOVZoomRate assignment)
                int afterPos = UePackageLocator.Pos(vf) + 19;
                Token? endfp = null;
                foreach (var t in tokens)
                    if (t is EndFunctionParmsToken && UePackageLocator.Pos(t) >= afterPos) { endfp = t; break; }
                if (endfp == null) return null;

                FloatConstToken? elseFloat = null;
                foreach (var t in tokens)
                    if (t is FloatConstToken f && UePackageLocator.Pos(t) > UePackageLocator.Pos(endfp)) { elseFloat = f; break; }
                if (elseFloat == null) return null;

                return (instDefaultfov, UePackageLocator.Pos(elseFloat));
            }
            return null;
        }

        // if (Controller != None) Context(DynamicCast(Controller)).StartZoom(NewFOV, Rate, 0.0)
        static (int insertOff, byte[] localNewfov, byte[] localRate, byte[] dcast, byte[] controllerVar)?
            FindSetFovInsertion(IList<Token> tokens, byte[] bc)
        {
            for (int i = 0; i + 6 < tokens.Count; i++)
            {
                if (tokens[i] is not ContextToken ctx) continue;
                if (tokens[i + 1] is not DynamicCastToken) continue;
                if (tokens[i + 2] is not FieldToken) continue;            // controllerVar
                if (tokens[i + 3] is not VirtualFunctionToken vf
                    || vf.FunctionName?.ToString() != "StartZoom") continue;
                if (tokens[i + 4] is not FieldToken) continue;            // NewFOV (local)
                if (tokens[i + 5] is not FieldToken) continue;            // Rate (local)
                if (tokens[i + 6] is not FloatConstToken) continue;

                int insertOff = UePackageLocator.Pos(ctx);
                byte[] dcast = UePackageLocator.Harvest(bc, tokens[i + 1], BytecodeBuilder.VAR_TOKEN_SIZE);
                byte[] controllerVar = UePackageLocator.Harvest(bc, tokens[i + 2], BytecodeBuilder.VAR_TOKEN_SIZE);
                byte[] localNewfov = UePackageLocator.Harvest(bc, tokens[i + 4], BytecodeBuilder.VAR_TOKEN_SIZE);
                byte[] localRate = UePackageLocator.Harvest(bc, tokens[i + 5], BytecodeBuilder.VAR_TOKEN_SIZE);
                return (insertOff, localNewfov, localRate, dcast, controllerVar);
            }
            return null;
        }

        // Online skip
        // The 6-byte BoolVar(InstanceVar(ConnectionRequired)) operand from the LetBool that
        // stores the function's ConnectionRequired parameter into the instance variable
        static byte[]? FindConnectionRequiredBoolvar(IList<Token> tokens, byte[] bc)
        {
            for (int i = 0; i + 3 < tokens.Count; i++)
            {
                if (tokens[i] is not LetBoolToken) continue;
                if (UePackageLocator.Pos(tokens[i]) >= 40) break;
                if (tokens[i + 1] is not BoolVariableToken lhs) continue;
                if (tokens[i + 2] is not InstanceVariableToken iv
                    || iv.Object?.Name?.ToString() != "ConnectionRequired") continue;
                if (tokens[i + 3] is not BoolVariableToken) continue;   // RHS is also a bool var
                return UePackageLocator.Harvest(bc, lhs, 6);
            }
            return null;
        }

        // The first JumpIfNot in StartConnection (the if(Connection.IsLoggedIn()) branch)
        static (int jnotOff, int elseTarget)? FindElseBranch(IList<Token> tokens)
        {
            foreach (var t in tokens)
                if (t is JumpIfNotToken j)
                    return (UePackageLocator.Pos(j), j.CodeOffset);
            return null;
        }

        // Utility

        // Detect 4.3.0 buggy patch that placed the FMax blob in StartZoom's delay
        // arg instead of the else-branch zoom rate. In the correct position, the
        // ZoomRateSignature is preceded by InstVar (FOVZoomRate)
        static bool HasBuggyUnzoomPatch(string tdGamePath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(tdGamePath);
                using var pkg = UePackageLocator.LoadHeader(tdGamePath);
                int uzSo = UePackageLocator.FindExportSerialOffset(pkg, "TdPlayerController", "UnZoom");
                if (uzSo < 0) return false;

                int uzBcStart = uzSo + BytecodeBuilder.SCRIPT_HDR;
                int uzBss = (int)PackageSplicer.ReadBSS(data, uzSo);

                int sigPos = BytecodeBuilder.FindPattern(data, BytecodeBuilder.ZoomRateSignature,
                    uzBcStart, uzBcStart + uzBss);
                if (sigPos == -1) return false;

                return sigPos >= uzBcStart + 5
                    && data[sigPos - 5] == BytecodeBuilder.OP_LOCAL_VAR;
            }
            catch { return false; }
        }

        static TdGamePatchState DetectStateCore(byte[] data, UnrealPackage pkg)
        {
            var result = new TdGamePatchState();
            try
            {
                byte[] FuncBc(string outerName, string funcName)
                {
                    int so = UePackageLocator.FindExportSerialOffset(pkg, outerName, funcName);
                    if (so < 0) return Array.Empty<byte>();
                    int bss = (int)PackageSplicer.ReadBSS(data, so);
                    if (bss <= 0 || bss > 100_000) return Array.Empty<byte>();
                    int bcStart = so + BytecodeBuilder.SCRIPT_HDR;
                    if (bcStart + bss > data.Length) return Array.Empty<byte>();
                    byte[] bc = new byte[bss];
                    Buffer.BlockCopy(data, bcStart, bc, 0, bss);
                    return bc;
                }

                byte[] tzsBc = FuncBc("TdHUD", "ToggleZoomState");
                result.ClipApplied = BytecodeBuilder.FindPattern(tzsBc, BytecodeBuilder.ClipSignature) != -1;

                byte[] piBc = FuncBc("TdPlayerInput", "PlayerInput");
                result.SensApplied = BytecodeBuilder.FindPattern(piBc, BytecodeBuilder.SensSignature) != -1;

                byte[] smBc = FuncBc("TdMove_Vertigo", "StartMove");
                bool vertigoApplied = smBc.Length > 0
                    && BytecodeBuilder.FindPattern(smBc, BytecodeBuilder.VertigoSignature) != -1;

                byte[] uzBc = FuncBc("TdPlayerController", "UnZoom");
                bool unzoomApplied = uzBc.Length > 0
                    && BytecodeBuilder.FindPattern(uzBc, BytecodeBuilder.ZoomRateSignature) != -1;

                result.CoreApplied = vertigoApplied || unzoomApplied;

                byte[] scBc = FuncBc("TdOnlineLoginHandler", "StartConnection");
                result.OnlineSkipApplied = scBc.Length > 0
                    && BytecodeBuilder.FindPattern(scBc, BytecodeBuilder.OnlineSkipSignature) != -1;
            }
            catch
            {
            }
            return result;
        }

        static (byte[] dcast, byte[] powner, byte[] vfunc) ExtractContextCallFromPatchedBlob(
            byte[] data, int blobStart, int blobEnd, int bcStart)
        {
            // The else-branch of the clip blob contains the original Context call structure.
            // Find it by looking for Context(DynamicCast...) after the Jump instruction.
            for (int i = blobStart; i < blobEnd; i++)
            {
                if (data[i] == BytecodeBuilder.OP_JUMP && i + 3 < blobEnd)
                {
                    int elseStart = i + 3;
                    if (data[elseStart] == BytecodeBuilder.OP_CONTEXT
                        && data[elseStart + 1] == BytecodeBuilder.OP_DYNAMIC_CAST)
                    {
                        int p = elseStart + 1;
                        byte[] dcast = data[p..(p + 5)]; p += 5;
                        byte[] powner = data[p..(p + 5)]; p += 5;
                        p += 2 + 2; // skip + proptype
                        byte[] vfunc = data[p..(p + 9)];
                        return (dcast, powner, vfunc);
                    }
                }
            }
            throw new InvalidOperationException("Cannot extract original call structure from patched clip blob");
        }
    }
}
