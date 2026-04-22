using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            return DetectStateFromData(data);
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

            if (stateMatches) return;

            if (anyPatched) Remove(tdGamePath);
            Apply(tdGamePath, enableSens, enableClip, enableOnlineSkip);
        }

        public static void Apply(string tdGamePath, bool enableSens, bool enableClip,
            bool enableOnlineSkip = false)
        {
            byte[] data = File.ReadAllBytes(tdGamePath);
            int origLen = data.Length;
            var hdr = PackageSplicer.ParseHeader(data);

            // Resolve indices from package structure
            var resolved = ResolveIndices(data, hdr);

            // ToggleZoomState: near clip
            int clipNet = 0;
            int tzsSo = resolved.TzsSerialOffset;
            if (enableClip)
            {
                int tzsBcStart = tzsSo + BytecodeBuilder.SCRIPT_HDR;
                int tzsBss = (int)PackageSplicer.ReadBSS(data, tzsSo);
                byte[] tzsBc = new byte[tzsBss];
                Buffer.BlockCopy(data, tzsBcStart, tzsBc, 0, tzsBss);

                var clipResult = ExtractContextCallPattern(tzsBc, BytecodeBuilder.F32(10.0f))
                    ?? ExtractContextCallPattern(tzsBc, null);
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
                byte[] piBc = new byte[piBss];
                Buffer.BlockCopy(data, piBcStart, piBc, 0, piBss);

                var fovResult = FindFovScaleLet(piBc);
                if (fovResult == null)
                    throw new InvalidOperationException("Cannot find FOVScale LET in PlayerInput");

                var (_, letEnd, fovscaleLocal, outerVar, getfovVf, _) = fovResult.Value;
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
            byte[] smBc = new byte[smBss];
            Buffer.BlockCopy(data, smBcStart, smBc, 0, smBss);

            var vertigoResult = FindVertigoStartZoom(smBc);
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
            byte[] uzBc = new byte[uzBss];
            Buffer.BlockCopy(data, uzBcStart, uzBc, 0, uzBss);

            var uzResult = FindUnzoomPatches(uzBc);
            if (uzResult == null)
                throw new InvalidOperationException("Cannot find UnZoom patch points");
            var (_, _, _, uzDefaultFov, uzElseFloatOff) = uzResult.Value;

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
            byte[] sfBc = new byte[sfBss];
            Buffer.BlockCopy(data, sfBcStart, sfBc, 0, sfBss);

            var sfResult = FindSetFovInsertion(sfBc);
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
                byte[] scBc = new byte[scBss];
                Buffer.BlockCopy(data, scBcStart, scBc, 0, scBss);

                var connReq = FindConnectionRequiredBoolvar(scBc);
                if (connReq == null)
                    throw new InvalidOperationException("Cannot find ConnectionRequired BoolVar in StartConnection");

                var branch = FindElseBranch(scBc);
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

            hdr = PackageSplicer.ParseHeader(data);
            PackageSplicer.UpdateExportsStructural(data, hdr, modifications);

            File.WriteAllBytes(tdGamePath, data);
        }

        public static void Remove(string tdGamePath)
        {
            byte[] data = File.ReadAllBytes(tdGamePath);
            var state = DetectStateFromData(data);
            if (!state.CoreApplied && !state.SensApplied && !state.ClipApplied
                && !state.OnlineSkipApplied) return;

            // Collect all removals first (function serial offsets, deltas, export indices)
            // then apply them in a single coordinated pass from highest offset to lowest.
            // This ensures each removal doesn't shift offsets for the others.
            var hdr = PackageSplicer.ParseHeader(data);
            var resolved = ResolveIndices(data, hdr);

            var ops = new List<RemovalOp>();

            // Analyse each patched function and compute the removal operation
            AnalyzeOnlineSkipRemoval(data, resolved, state, ops);
            AnalyzeSetFovRemoval(data, resolved, state, ops);
            AnalyzeUnzoomRemoval(data, resolved, state, ops);
            AnalyzeVertigoRemoval(data, resolved, state, ops);
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
            hdr = PackageSplicer.ParseHeader(data);
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

            int scSo = FindCurrentSerialOffset(data, r.ScSerialOffset, r.ScExportIndex);
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

        static void AnalyzeSetFovRemoval(byte[] data, ResolvedIndices r, TdGamePatchState state, List<RemovalOp> ops)
        {
            int sfSo = FindCurrentSerialOffset(data, r.SfSerialOffset, r.SfExportIndex);
            int sfBcStart = sfSo + BytecodeBuilder.SCRIPT_HDR;
            int sfBss = (int)PackageSplicer.ReadBSS(data, sfSo);
            byte[] sfBc = new byte[sfBss];
            Buffer.BlockCopy(data, sfBcStart, sfBc, 0, sfBss);

            var sfResult = FindSetFovInsertion(sfBc);
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

        static void AnalyzeUnzoomRemoval(byte[] data, ResolvedIndices r, TdGamePatchState state, List<RemovalOp> ops)
        {
            int uzSo = FindCurrentSerialOffset(data, r.UzSerialOffset, r.UzExportIndex);
            int uzBcStart = uzSo + BytecodeBuilder.SCRIPT_HDR;
            int uzBss = (int)PackageSplicer.ReadBSS(data, uzSo);

            int fmaxPos = BytecodeBuilder.FindPattern(data, BytecodeBuilder.ZoomRateSignature,
                uzBcStart, uzBcStart + uzBss);
            if (fmaxPos == -1) return;

            byte[] uzElseReplacement = BytecodeBuilder.BuildUnzoomElseReplacement(r.UzDefaultFov, r.InstFovangle);
            byte[] stockBytes = BytecodeBuilder.BuildStockUnzoomRate();

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

        static void AnalyzeVertigoRemoval(byte[] data, ResolvedIndices r, TdGamePatchState state, List<RemovalOp> ops)
        {
            int smSo = FindCurrentSerialOffset(data, r.SmSerialOffset, r.SmExportIndex);
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

            int piSo = FindCurrentSerialOffset(data, r.PiSerialOffset, r.PiExportIndex);
            int piBcStart = piSo + BytecodeBuilder.SCRIPT_HDR;
            int piBss = (int)PackageSplicer.ReadBSS(data, piSo);

            bool hasSens = BytecodeBuilder.FindPattern(data, BytecodeBuilder.SensSignature,
                piBcStart, piBcStart + piBss) != -1;
            byte[] piClipSig = BytecodeBuilder.Concat(
                new byte[] { BytecodeBuilder.OP_FMIN }, BytecodeBuilder.FloatConst(10.0f));
            bool hasClip = BytecodeBuilder.FindPattern(data, piClipSig, piBcStart, piBcStart + piBss) != -1;

            if (!hasSens && !hasClip) return;

            byte[] piBc = new byte[piBss];
            Buffer.BlockCopy(data, piBcStart, piBc, 0, piBss);
            var fovResult = FindFovScaleLet(piBc);
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

            int tzsSo = FindCurrentSerialOffset(data, r.TzsSerialOffset, r.TzsExportIndex);
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
        }

        static ResolvedIndices ResolveIndices(byte[] data, PackageSplicer.PackageHeader hdr)
        {
            var names = PackageSplicer.ReadNameTable(data, hdr);
            var imports = ReadImportTable(data, hdr, names);
            var exports = ReadExportTable(data, hdr, names);

            var r = new ResolvedIndices();

            // Find HUD.SizeX/SizeY imports
            var sizexImp = imports.FirstOrDefault(i => i.name == "SizeX" && i.className == "FloatProperty");
            var sizeyImp = imports.FirstOrDefault(i => i.name == "SizeY" && i.className == "FloatProperty");
            var myhudImp = imports.FirstOrDefault(i => i.name == "myHUD" && i.className == "ObjectProperty");
            if (sizexImp.name == null || sizeyImp.name == null || myhudImp.name == null)
                throw new InvalidOperationException("Cannot find HUD imports in TdGame.u");

            r.SizexImp = sizexImp.index;
            r.SizeyImp = sizeyImp.index;
            r.MyhudImp = myhudImp.index;

            // Find export functions
            var tzsExp = FindExport(exports, "ToggleZoomState", "TdHUD");
            var piExp = FindExport(exports, "PlayerInput", "TdPlayerInput");
            var smExp = FindExport(exports, "StartMove", "TdMove_Vertigo");
            var uzExp = FindExport(exports, "UnZoom", "TdPlayerController");
            var sfExp = FindExport(exports, "SetFOV", "TdPlayerPawn");
            var ezExp = FindExport(exports, "EndZoom", "TdPlayerController");

            if (tzsExp == null) throw new InvalidOperationException("Cannot find TdHUD.ToggleZoomState");
            if (piExp == null) throw new InvalidOperationException("Cannot find TdPlayerInput.PlayerInput");
            if (smExp == null) throw new InvalidOperationException("Cannot find TdMove_Vertigo.StartMove");
            if (uzExp == null) throw new InvalidOperationException("Cannot find TdPlayerController.UnZoom");
            if (sfExp == null) throw new InvalidOperationException("Cannot find TdPlayerPawn.SetFOV");
            if (ezExp == null) throw new InvalidOperationException("Cannot find TdPlayerController.EndZoom");

            r.TzsSerialOffset = tzsExp.Value.serialOffset;
            r.TzsExportIndex = tzsExp.Value.exportIndex;
            r.PiSerialOffset = piExp.Value.serialOffset;
            r.PiExportIndex = piExp.Value.exportIndex;
            r.SmSerialOffset = smExp.Value.serialOffset;
            r.SmExportIndex = smExp.Value.exportIndex;
            r.UzSerialOffset = uzExp.Value.serialOffset;
            r.UzExportIndex = uzExp.Value.exportIndex;
            r.SfSerialOffset = sfExp.Value.serialOffset;
            r.SfExportIndex = sfExp.Value.exportIndex;

            // Online skip: StartConnection + OnPlayOffline delegate
            var scExp = FindExport(exports, "StartConnection", "TdOnlineLoginHandler");
            if (scExp != null)
            {
                r.ScSerialOffset = scExp.Value.serialOffset;
                r.ScExportIndex = scExp.Value.exportIndex;

                var opoExp = FindExport(exports, "__OnPlayOffline__Delegate", "TdOnlineLoginHandler");
                if (opoExp != null)
                    r.OnPlayOfflinePropI32 = BitConverter.GetBytes(opoExp.Value.exportIndex);

                int opoNameIdx = names.IndexOf("OnPlayOffline");
                if (opoNameIdx >= 0)
                    r.OnPlayOfflineFnameBytes = BytecodeBuilder.Concat(
                        BytecodeBuilder.I32(opoNameIdx), BytecodeBuilder.I32(0));
            }

            // Extract tokens from UnZoom bytecodes
            int uzSo = uzExp.Value.serialOffset;
            int uzBcStart = uzSo + BytecodeBuilder.SCRIPT_HDR;
            int uzBss = (int)PackageSplicer.ReadBSS(data, uzSo);
            byte[] uzBc = new byte[uzBss];
            Buffer.BlockCopy(data, uzBcStart, uzBc, 0, uzBss);

            ExtractZoomTokens(uzBc, r);

            // Extract DefaultFOV and FOVAngle from EndZoom
            int ezSo = ezExp.Value.serialOffset;
            int ezBcStart = ezSo + BytecodeBuilder.SCRIPT_HDR;
            int ezBss = (int)PackageSplicer.ReadBSS(data, ezSo);
            byte[] ezBc = new byte[ezBss];
            Buffer.BlockCopy(data, ezBcStart, ezBc, 0, ezBss);

            // FOVAngle: second LET's LHS in EndZoom
            int letCount = 0;
            for (int off = 0; off < ezBc.Length - 6; off++)
            {
                if (ezBc[off] == BytecodeBuilder.OP_LET && ezBc[off + 1] == BytecodeBuilder.OP_INST_VAR)
                {
                    letCount++;
                    if (letCount == 1)
                    {
                        // First LET RHS is DefaultFOV
                        if (ezBc[off + 6] == BytecodeBuilder.OP_INST_VAR)
                            r.DefaultFovInst = ezBc[(off + 6)..(off + 11)];
                    }
                    if (letCount == 2)
                    {
                        r.InstFovangle = ezBc[(off + 1)..(off + 6)];
                        break;
                    }
                }
            }

            // DefaultFOV from UnZoom: Let DesiredFOV = DefaultFOV
            for (int off = 0; off < uzBc.Length - 10; off++)
            {
                if (uzBc[off] == BytecodeBuilder.OP_LET
                    && uzBc[off + 1] == BytecodeBuilder.OP_INST_VAR
                    && uzBc[off + 6] == BytecodeBuilder.OP_INST_VAR)
                {
                    r.UzDefaultFov = uzBc[(off + 6)..(off + 11)];
                    break;
                }
            }

            // Extract ToggleZoomState SNCP VirtualFunction token.
            // In patched data, the else-branch of the clip blob still has
            // a SetNearClippingPlane(10.0) call, so the pattern still works.
            int tzsBcStart2 = tzsExp.Value.serialOffset + BytecodeBuilder.SCRIPT_HDR;
            int tzsBss2 = (int)PackageSplicer.ReadBSS(data, tzsExp.Value.serialOffset);
            byte[] tzsBc2 = new byte[tzsBss2];
            Buffer.BlockCopy(data, tzsBcStart2, tzsBc2, 0, tzsBss2);
            var clipExtract = ExtractContextCallPattern(tzsBc2, BytecodeBuilder.F32(10.0f))
                ?? ExtractContextCallPattern(tzsBc2, null);
            if (clipExtract != null)
                r.SncpVfunc = clipExtract.Value.vfunc;

            // Extract controller context chain from StartMove.
            // In unpatched data, FindVertigoStartZoom finds the original pattern.
            // In patched data, the vertigo replacement changes the ZoomFOV arg,
            // but the function header (EndZoom context chain at bc+0x00) is unchanged.
            int smSoR = smExp.Value.serialOffset;
            int smBssR = (int)PackageSplicer.ReadBSS(data, smSoR);
            byte[] smBcR = new byte[smBssR];
            Buffer.BlockCopy(data, smSoR + BytecodeBuilder.SCRIPT_HDR, smBcR, 0, smBssR);

            // Controller context chain is always at the start of StartMove (bc+1..bc+21)
            if (smBcR.Length >= 21 && smBcR[0] == BytecodeBuilder.OP_CONTEXT
                && smBcR[1] == BytecodeBuilder.OP_DYNAMIC_CAST && smBcR[6] == BytecodeBuilder.OP_CONTEXT)
            {
                r.ControllerCtx = smBcR[1..21];
            }

            // InstZoomFov: in unpatched data, it's at the first arg of StartZoom.
            // In patched data, the VertigoSignature replaced it.
            // Try the unpatched path first.
            var vertigoResult = FindVertigoStartZoom(smBcR);
            if (vertigoResult != null)
            {
                r.InstZoomFov = smBcR[vertigoResult.Value.zoomfovOff..(vertigoResult.Value.zoomfovOff + 5)];
            }
            else
            {
                // Patched: ZoomFOV is a property on TdMove_Vertigo. Find its index
                // from the export table by looking for "ZoomFOV" in the name table.
                int zoomFovNameIdx = names.IndexOf("ZoomFOV");
                if (zoomFovNameIdx >= 0)
                {
                    // Find the property export whose name is ZoomFOV and outer is TdMove_Vertigo
                    var zoomFovProp = exports.FirstOrDefault(e =>
                        e.name == "ZoomFOV" && exports.Any(o => o.exportIndex == e.outerIdx && o.name == "TdMove_Vertigo"));
                    if (zoomFovProp.name != null)
                    {
                        // Build the InstanceVar token using the export's package index
                        r.InstZoomFov = BytecodeBuilder.InstVar(zoomFovProp.exportIndex);
                    }
                }
            }

            return r;
        }

        static void ExtractZoomTokens(byte[] uzBc, ResolvedIndices r)
        {
            // TdWeapon DynamicCast, Pawn, Weapon, IsZoomingOrZoomed from UnZoom bytecodes
            for (int off = 0; off < uzBc.Length - 5; off++)
            {
                if (uzBc[off] == BytecodeBuilder.OP_DYNAMIC_CAST && r.TdweaponDcast.Length == 0)
                    r.TdweaponDcast = uzBc[off..(off + 5)];

                if (uzBc[off] == BytecodeBuilder.OP_CONTEXT && off + 1 < uzBc.Length
                    && uzBc[off + 1] == BytecodeBuilder.OP_INST_VAR)
                {
                    if (r.InstPawn.Length == 0)
                    {
                        r.InstPawn = uzBc[(off + 1)..(off + 6)];
                        int p = off + 6 + 2 + 2; // skip + proptype
                        if (p < uzBc.Length && uzBc[p] == BytecodeBuilder.OP_INST_VAR)
                            r.InstWeapon = uzBc[p..(p + 5)];
                    }
                }
            }

            // IsZoomingOrZoomed: first VirtualFunction after offset 0x20
            for (int off = 0; off < uzBc.Length - 9; off++)
            {
                if (uzBc[off] == BytecodeBuilder.OP_VIRT_FUNC && off > 0x20)
                {
                    r.IsZoomingVf = uzBc[off..(off + 9)];
                    break;
                }
            }
        }

        // Pattern extraction

        static (int ctxOff, int ctxLen, byte[] dcast, byte[] powner, byte[] vfunc)?
            ExtractContextCallPattern(byte[] bc, byte[]? floatPattern)
        {
            (int ctxOff, int ctxLen, byte[] dcast, byte[] powner, byte[] vfunc)? lastMatch = null;

            for (int off = 0; off < bc.Length - 5; off++)
            {
                if (bc[off] != BytecodeBuilder.OP_FLOAT_CONST) continue;
                if (floatPattern != null)
                {
                    bool match = true;
                    for (int j = 0; j < floatPattern.Length; j++)
                        if (bc[off + 1 + j] != floatPattern[j]) { match = false; break; }
                    if (!match) continue;
                }

                for (int ctxOff = off - 1; ctxOff >= Math.Max(0, off - 50); ctxOff--)
                {
                    if (bc[ctxOff] != BytecodeBuilder.OP_CONTEXT) continue;
                    int p = ctxOff + 1;
                    if (bc[p] != BytecodeBuilder.OP_DYNAMIC_CAST) continue;
                    byte[] dcast = bc[p..(p + 5)]; p += 5;
                    if (bc[p] != BytecodeBuilder.OP_INST_VAR) continue;
                    byte[] powner = bc[p..(p + 5)]; p += 5;
                    p += 2 + 2; // skip + proptype
                    if (bc[p] != BytecodeBuilder.OP_VIRT_FUNC) continue;
                    byte[] vfunc = bc[p..(p + 9)];
                    int endOff = off + 5 + 1;

                    if (floatPattern != null)
                        return (ctxOff, endOff - ctxOff, dcast, powner, vfunc);

                    // Collect the last float match so we get the
                    // second SetNearClippingPlane call (the unzoom path).
                    lastMatch = (ctxOff, endOff - ctxOff, dcast, powner, vfunc);
                    break;
                }
            }
            return lastMatch;
        }

        static (int letStart, int letEnd, byte[] fovscaleLocal, byte[] outerVar, byte[] getfovVf, byte[] outerCtx)?
            FindFovScaleLet(byte[] bc)
        {
            byte[] flt = BytecodeBuilder.F32(BytecodeBuilder.K_SENS);

            // Try exact K_SENS match first, otherwise match any FloatConst
            (int, int, byte[], byte[], byte[], byte[])? fallback = null;

            for (int off = 0; off < bc.Length - 5; off++)
            {
                if (bc[off] != BytecodeBuilder.OP_FLOAT_CONST) continue;

                bool exactMatch = true;
                for (int j = 0; j < flt.Length; j++)
                    if (bc[off + 1 + j] != flt[j]) { exactMatch = false; break; }

                int endFpPos = off + 5;
                if (endFpPos >= bc.Length || bc[endFpPos] != BytecodeBuilder.OP_END_FP) continue;
                int letEnd = endFpPos + 1;

                for (int back = off - 1; back >= Math.Max(0, off - 60); back--)
                {
                    if (bc[back] != BytecodeBuilder.OP_LET) continue;
                    int p = back + 1;
                    if (bc[p] != BytecodeBuilder.OP_LOCAL_VAR) continue;
                    byte[] fovscaleLocal = bc[p..(p + 5)]; p += 5;
                    if (bc[p] != BytecodeBuilder.OP_MULTIPLY_FF) continue;
                    p++;
                    if (bc[p] != BytecodeBuilder.OP_CONTEXT) continue;
                    int ctxStart = p; p++;
                    if (bc[p] != BytecodeBuilder.OP_INST_VAR) continue;
                    byte[] outerVar = bc[p..(p + 5)]; p += 5;
                    p += 2 + 2;
                    if (bc[p] != BytecodeBuilder.OP_VIRT_FUNC) continue;
                    byte[] getfovVf = bc[p..(p + 9)];
                    int ctxEnd = p + 9 + 1;
                    byte[] outerCtx = bc[ctxStart..ctxEnd];

                    if (exactMatch)
                        return (back, letEnd, fovscaleLocal, outerVar, getfovVf, outerCtx);

                    fallback ??= (back, letEnd, fovscaleLocal, outerVar, getfovVf, outerCtx);
                    break;
                }
            }
            return fallback;
        }

        static (int startzoomOff, int zoomfovOff, byte[] controllerCtx)?
            FindVertigoStartZoom(byte[] bc)
        {
            if (bc[0] != BytecodeBuilder.OP_CONTEXT || bc[1] != BytecodeBuilder.OP_DYNAMIC_CAST) return null;
            if (bc[6] != BytecodeBuilder.OP_CONTEXT) return null;
            byte[] controllerCtx = bc[1..21];

            for (int off = 0; off < bc.Length - 9; off++)
            {
                if (bc[off] != BytecodeBuilder.OP_VIRT_FUNC) continue;
                int endPos = off + 9 + 5 + 5 + 5 + 1;
                if (endPos > bc.Length) continue;
                if (bc[endPos - 1] != BytecodeBuilder.OP_END_FP) continue;
                int fltPos = off + 9 + 5 + 5;
                if (bc[fltPos] != BytecodeBuilder.OP_FLOAT_CONST) continue;
                float val = BitConverter.ToSingle(bc, fltPos + 1);
                if (Math.Abs(val) < 0.001f)
                    return (off, off + 9, controllerCtx);
            }
            return null;
        }

        static (int ifInsertOff, byte[] localFov, byte[] localRate, byte[] instDefaultfov, int elseFloatOff)?
            FindUnzoomPatches(byte[] bc)
        {
            int ifInsertOff = -1;
            byte[]? localFov = null, localRate = null, instDefaultfov = null;

            for (int off = 0; off < bc.Length - 20; off++)
            {
                if (bc[off] != BytecodeBuilder.OP_VIRT_FUNC) continue;
                int p = off + 9;
                if (p + 10 >= bc.Length) continue;
                if (bc[p] != BytecodeBuilder.OP_LOCAL_VAR) continue;
                localFov = bc[p..(p + 5)]; p += 5;
                if (bc[p] != BytecodeBuilder.OP_LOCAL_VAR) continue;
                localRate = bc[p..(p + 5)]; p += 5;
                if (bc[p] != BytecodeBuilder.OP_END_FP) continue;
                p++;
                if (bc[p] != BytecodeBuilder.OP_VIRT_FUNC) continue;
                int p2 = p + 9;
                if (bc[p2] != BytecodeBuilder.OP_INST_VAR) continue;
                instDefaultfov = bc[p2..(p2 + 5)]; p2 += 5;
                if (!bc[p2..(p2 + 5)].SequenceEqual(localRate)) continue;
                ifInsertOff = p;
                break;
            }

            if (ifInsertOff == -1 || localFov == null || localRate == null || instDefaultfov == null) return null;

            int elseFloatOff = -1;
            for (int off = ifInsertOff; off < bc.Length - 5; off++)
            {
                if (bc[off] == BytecodeBuilder.OP_FLOAT_CONST)
                {
                    elseFloatOff = off;
                    break;
                }
            }
            if (elseFloatOff == -1) return null;

            return (ifInsertOff, localFov, localRate, instDefaultfov, elseFloatOff);
        }

        static (int insertOff, byte[] localNewfov, byte[] localRate, byte[] dcast, byte[] controllerVar)?
            FindSetFovInsertion(byte[] bc)
        {
            for (int off = 0; off < bc.Length - 20; off++)
            {
                if (bc[off] != BytecodeBuilder.OP_CONTEXT || bc[off + 1] != BytecodeBuilder.OP_DYNAMIC_CAST) continue;
                int p = off + 1;
                byte[] dcast = bc[p..(p + 5)]; p += 5;
                if (bc[p] != BytecodeBuilder.OP_INST_VAR) continue;
                byte[] controllerVar = bc[p..(p + 5)]; p += 5;
                p += 2 + 2;
                if (bc[p] != BytecodeBuilder.OP_VIRT_FUNC) continue;
                p += 9;
                if (bc[p] != BytecodeBuilder.OP_LOCAL_VAR) continue;
                byte[] localNewfov = bc[p..(p + 5)]; p += 5;
                if (bc[p] != BytecodeBuilder.OP_LOCAL_VAR) continue;
                byte[] localRate = bc[p..(p + 5)]; p += 5;
                if (bc[p] != BytecodeBuilder.OP_FLOAT_CONST) continue;
                return (off, localNewfov, localRate, dcast, controllerVar);
            }
            return null;
        }

        // Online skip: bytecode analysis helpers

        // Extract the 6-byte BoolVar(InstVar(ConnectionRequired)) token from the
        // first LetBool whose RHS is a BoolVar (parameter reference, not a constant).
        static byte[]? FindConnectionRequiredBoolvar(byte[] bc)
        {
            int limit = Math.Min(40, bc.Length - 13);
            for (int off = 0; off < limit; off++)
            {
                if (bc[off] != BytecodeBuilder.OP_LET_BOOL) continue;
                if (bc[off + 1] != BytecodeBuilder.OP_BOOL_VAR || bc[off + 2] != BytecodeBuilder.OP_INST_VAR) continue;
                if (bc[off + 7] == BytecodeBuilder.OP_BOOL_VAR)
                    return bc[(off + 1)..(off + 7)];
            }
            return null;
        }

        // Find the first JumpIfNot in StartConnection (the if(Connection.IsLoggedIn()) branch).
        // Returns (jnot_offset, else_target).
        static (int jnotOff, int elseTarget)? FindElseBranch(byte[] bc)
        {
            for (int off = 0; off < bc.Length - 3; off++)
            {
                if (bc[off] == BytecodeBuilder.OP_JUMP_IF_NOT)
                {
                    ushort target = BitConverter.ToUInt16(bc, off + 1);
                    return (off, target);
                }
            }
            return null;
        }

        // Import/export table reading

        struct ImportEntry { public int index; public string className; public string name; public int outerIdx; }
        struct ExportEntry { public int exportIndex; public string name; public int outerIdx; public int serialSize; public int serialOffset; }

        static List<ImportEntry> ReadImportTable(byte[] data, PackageSplicer.PackageHeader hdr, List<string> names)
        {
            var imports = new List<ImportEntry>();
            int pos = hdr.ImportOffset;
            for (int i = 0; i < hdr.ImportCount; i++)
            {
                pos += 8; // ClassPackageName UName
                int clsNi = BitConverter.ToInt32(data, pos); pos += 8;
                int outerIdx = BitConverter.ToInt32(data, pos); pos += 4;
                int objNi = BitConverter.ToInt32(data, pos); pos += 8;
                imports.Add(new ImportEntry
                {
                    index = -(i + 1),
                    className = (clsNi >= 0 && clsNi < names.Count) ? names[clsNi] : "?",
                    name = (objNi >= 0 && objNi < names.Count) ? names[objNi] : "?",
                    outerIdx = outerIdx,
                });
            }
            return imports;
        }

        static List<ExportEntry> ReadExportTable(byte[] data, PackageSplicer.PackageHeader hdr, List<string> names)
        {
            var exports = new List<ExportEntry>();
            int pos = hdr.ExportOffset;
            for (int i = 0; i < hdr.ExportCount; i++)
            {
                pos += 4; // class_idx
                pos += 4; // super_idx
                int outerIdx = BitConverter.ToInt32(data, pos); pos += 4;
                int nameIdx = BitConverter.ToInt32(data, pos); pos += 4;
                pos += 4; // name_num
                pos += 4; // archetype_idx
                pos += 8; // obj_flags
                int serialSize = BitConverter.ToInt32(data, pos); pos += 4;
                int serialOffset = BitConverter.ToInt32(data, pos); pos += 4;
                int compCount = BitConverter.ToInt32(data, pos); pos += 4;
                pos += compCount * 12;
                pos += 4; // export_flags
                int netCount = BitConverter.ToInt32(data, pos); pos += 4;
                pos += netCount * 4;
                pos += 16 + 4; // GUID + PackageFlags
                exports.Add(new ExportEntry
                {
                    exportIndex = i + 1,
                    name = (nameIdx >= 0 && nameIdx < names.Count) ? names[nameIdx] : "?",
                    outerIdx = outerIdx,
                    serialSize = serialSize,
                    serialOffset = serialOffset,
                });
            }
            return exports;
        }

        static ExportEntry? FindExport(List<ExportEntry> exports, string name, string outerName)
        {
            var byIdx = exports.ToDictionary(e => e.exportIndex);
            foreach (var exp in exports)
            {
                if (exp.name != name) continue;
                if (byIdx.TryGetValue(exp.outerIdx, out var outer) && outer.name == outerName)
                    return exp;
            }
            return null;
        }

        // Utility

        static TdGamePatchState DetectStateFromData(byte[] data)
        {
            var result = new TdGamePatchState();
            try
            {
                var hdr = PackageSplicer.ParseHeader(data);
                var names = PackageSplicer.ReadNameTable(data, hdr);
                var exports = ReadExportTable(data, hdr, names);

                byte[] GetFuncBc(string funcName, string outerName)
                {
                    var exp = FindExport(exports, funcName, outerName);
                    if (exp == null) return Array.Empty<byte>();
                    int so = exp.Value.serialOffset;
                    int bss = (int)PackageSplicer.ReadBSS(data, so);
                    if (bss <= 0 || bss > 100_000) return Array.Empty<byte>();
                    int bcStart = so + BytecodeBuilder.SCRIPT_HDR;
                    if (bcStart + bss > data.Length) return Array.Empty<byte>();
                    byte[] bc = new byte[bss];
                    Buffer.BlockCopy(data, bcStart, bc, 0, bss);
                    return bc;
                }

                byte[] tzsBc = GetFuncBc("ToggleZoomState", "TdHUD");
                result.ClipApplied = BytecodeBuilder.FindPattern(tzsBc, BytecodeBuilder.ClipSignature) != -1;

                byte[] piBc = GetFuncBc("PlayerInput", "TdPlayerInput");
                result.SensApplied = BytecodeBuilder.FindPattern(piBc, BytecodeBuilder.SensSignature) != -1;

                byte[] smBc = GetFuncBc("StartMove", "TdMove_Vertigo");
                bool vertigoApplied = smBc.Length > 0
                    && BytecodeBuilder.FindPattern(smBc, BytecodeBuilder.VertigoSignature) != -1;

                byte[] uzBc = GetFuncBc("UnZoom", "TdPlayerController");
                bool unzoomApplied = uzBc.Length > 0
                    && BytecodeBuilder.FindPattern(uzBc, BytecodeBuilder.ZoomRateSignature) != -1;

                result.CoreApplied = vertigoApplied || unzoomApplied;

                byte[] scBc = GetFuncBc("StartConnection", "TdOnlineLoginHandler");
                result.OnlineSkipApplied = scBc.Length > 0
                    && BytecodeBuilder.FindPattern(scBc, BytecodeBuilder.OnlineSkipSignature) != -1;
            }
            catch
            {
            }
            return result;
        }

        static int FindCurrentSerialOffset(byte[] data, int originalSo, int exportIndex)
        {
            // After previous removals the offset may have shifted.
            // Re read the export table to find the current serial offset for this export.
            var hdr = PackageSplicer.ParseHeader(data);
            var names = PackageSplicer.ReadNameTable(data, hdr);
            var exports = ReadExportTable(data, hdr, names);
            var exp = exports.FirstOrDefault(e => e.exportIndex == exportIndex);
            return exp.serialOffset;
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
