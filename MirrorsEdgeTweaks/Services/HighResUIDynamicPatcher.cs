using System.IO;
using UELib;
using static UELib.Core.UStruct.UByteCodeDecompiler;

namespace MirrorsEdgeTweaks.Services
{
    // Injects self-contained UnrealScript blobs into stock packages.
    // They handle the parts of the high-res UI fix that MultiFontScalePatcher cannot reach.
    // Both blobs read the live viewport size, so they follow in-game resolution changes.
    //
    //   Engine.u: HUD.PreCalcValues sets the subtitle region on every size change (see ApplyEngine).
    //   TdGame.u: TdSPHUD.DrawLivingHUD sizes the crosshair textures every frame (see ApplyCrosshair).
    //
    // BytecodeBuilder.BuildHighResCommands builds the Engine.u blob, which is equivalent to this:
    //   ConsoleCommand("set TdGameViewportClient SubtitleMinRegion (X=" $ (0.5-FMin(0.5*r,0.5)) $ ",Y=" $ (0.5-0.35*r) $ ")");   // r=(SizeY/SizeX)*16/9
    //   ConsoleCommand("set TdGameViewportClient SubtitleMaxRegion (X=" $ (0.5+FMin(0.5*r,0.5)) $ ",Y=" $ (0.5+0.35*r) $ ")");
    public static class HighResUIDynamicPatcher
    {
        public static void ApplyAll(string enginePath, string tdGamePath)
        {
            ApplyEngine(enginePath);
            try
            {
                if (File.Exists(tdGamePath)) ApplyCrosshair(tdGamePath);
            }
            catch { }
        }

        public static void RemoveAll(string enginePath, string tdGamePath)
        {
            try { if (File.Exists(enginePath)) RemoveEngine(enginePath); } catch { }
            try { if (File.Exists(tdGamePath)) RemoveCrosshair(tdGamePath); } catch { }
        }

        public static void ApplyEngine(string enginePath)
        {
            if (Helpers.LegacyChangeDetector.ContainsBetaHighResCommand(enginePath))
                throw Helpers.LegacyChangeDetector.PackageRestoreRequired("Engine.u");

            EngineRefs r;
            int serialOffset, serialSize, insertBc;
            List<int> jumpPositions;
            using (var pkg = UePackageLocator.Load(enginePath))
            {
                r = ResolveEngineRefs(pkg);

                // No HUD subclass overrides PreCalcValues, so every HUD runs it on a size change, including the menu's TdSPHUD at boot.
                // TdHUD.PostRender does not call super, so HUD.PostRender would not run.
                var fn = UePackageLocator.FindFunction(pkg, "HUD", "PreCalcValues")
                    ?? throw new InvalidOperationException("Cannot find HUD.PreCalcValues in Engine.u");
                serialOffset = fn.SerialOffset;
                serialSize = fn.SerialSize;
                insertBc = FindReturnInsertBc(fn.Tokens);
                jumpPositions = UePackageLocator.ExtractJumpPositions(fn.Tokens);
            }

            byte[] blob = BytecodeBuilder.BuildHighResApplyBlob(
                r.PlayerOwnerRef, r.ConsoleNameIdx, r.ConsoleReturnValueRef, r.SizeXRef, r.SizeYRef);

            byte[] export = UePackageLocator.ReadRange(enginePath, serialOffset, serialSize);
            int bcStart = BytecodeBuilder.SCRIPT_HDR;

            // The betas' blob also contains HighResSignature, so only this exact blob counts as installed.
            int bss = (int)PackageSplicer.ReadBSS(export, 0);
            if (BytecodeBuilder.FindPattern(export, blob, bcStart, bcStart + bss) != -1)
                return;

            int origLen = (int)new FileInfo(enginePath).Length;
            int insertFile = serialOffset + bcStart + insertBc;

            PackageSplicer.ShiftJumpTargets(export, bcStart, jumpPositions, insertBc, blob.Length);
            PackageSplicer.UpdateBSS(export, 0, blob.Length);
            export = PackageSplicer.InsertBytes(export, bcStart + insertBc, blob);

            PackageSplicer.SpliceExport(enginePath, serialOffset, serialSize, export, (header, hdr) =>
                PackageSplicer.UpdateExportsHeuristic(header, hdr, serialOffset, insertFile, origLen, blob.Length));
        }

        public static void RemoveEngine(string enginePath)
        {
            if (!Helpers.PatternHelper.FileContainsAny(enginePath, [BytecodeBuilder.HighResSignature]))
                return;

            EngineRefs r;
            int serialOffset, serialSize;
            List<int> jumpPositions;
            using (var pkg = UePackageLocator.Load(enginePath))
            {
                r = ResolveEngineRefs(pkg);
                var fn = UePackageLocator.FindFunction(pkg, "HUD", "PreCalcValues")
                    ?? throw new InvalidOperationException("Cannot find HUD.PreCalcValues in Engine.u");
                serialOffset = fn.SerialOffset;
                serialSize = fn.SerialSize;
                jumpPositions = UePackageLocator.ExtractJumpPositions(fn.Tokens);
            }

            byte[] blob = BytecodeBuilder.BuildHighResApplyBlob(
                r.PlayerOwnerRef, r.ConsoleNameIdx, r.ConsoleReturnValueRef, r.SizeXRef, r.SizeYRef);

            byte[] export = UePackageLocator.ReadRange(enginePath, serialOffset, serialSize);
            int bcStart = BytecodeBuilder.SCRIPT_HDR;
            int bss = (int)PackageSplicer.ReadBSS(export, 0);
            int origLen = (int)new FileInfo(enginePath).Length;

            int blobPos = BytecodeBuilder.FindPattern(export, blob, bcStart, bcStart + bss);
            if (blobPos == -1) return;
            int insertBc = blobPos - bcStart;

            PackageSplicer.ShiftJumpTargets(export, bcStart, jumpPositions, insertBc, -blob.Length);
            PackageSplicer.UpdateBSS(export, 0, -blob.Length);
            export = PackageSplicer.RemoveBytes(export, blobPos, blob.Length);

            PackageSplicer.SpliceExport(enginePath, serialOffset, serialSize, export, (header, hdr) =>
                PackageSplicer.UpdateExportsHeuristic(header, hdr, serialOffset, serialOffset + blobPos, origLen, -blob.Length));
        }

        // Holds each TdSPHUD crosshair texture member with its stock pixel size.
        static readonly (string name, int baseSize)[] CrosshairMembers =
        {
            ("UnarmedCrossHair", 16), ("ReactionCrossHair", 16), ("WeaponCrossHair", 64),
        };

        sealed class CrosshairRefs
        {
            public (int textureRef, int baseSize)[] Crosshairs = Array.Empty<(int, int)>();
            public int TexSizeXRef;
            public int TexSizeYRef;
            public int HudSizeYRef;
        }

        static CrosshairRefs ResolveCrosshairRefs(UnrealPackage pkg)
        {
            var list = new List<(int, int)>();
            foreach (var (name, baseSize) in CrosshairMembers)
            {
                int tref = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, name, "TdSPHUD"));
                if (tref == 0) throw new InvalidOperationException($"Cannot resolve TdSPHUD.{name} in TdGame.u");
                list.Add((tref, baseSize));
            }
            var r = new CrosshairRefs
            {
                Crosshairs = list.ToArray(),
                TexSizeXRef = UePackageLocator.FindImportObjRef(pkg, "SizeX", "IntProperty", "Texture2D"),
                TexSizeYRef = UePackageLocator.FindImportObjRef(pkg, "SizeY", "IntProperty", "Texture2D"),
                HudSizeYRef = UePackageLocator.FindImportObjRef(pkg, "SizeY", "FloatProperty", "HUD"),
            };
            if (r.TexSizeXRef == 0 || r.TexSizeYRef == 0 || r.HudSizeYRef == 0)
                throw new InvalidOperationException("Cannot resolve Texture2D.SizeX/SizeY or HUD.SizeY in TdGame.u");
            return r;
        }

        static byte[] CrosshairBlob(CrosshairRefs r) =>
            BytecodeBuilder.BuildCrosshairBlob(r.Crosshairs, r.TexSizeXRef, r.TexSizeYRef, r.HudSizeYRef);

        static byte[] CrosshairSig(CrosshairRefs r) =>
            BytecodeBuilder.BuildCrosshairSignature(r.Crosshairs[0].textureRef, r.TexSizeXRef);

        public static void ApplyCrosshair(string tdGamePath)
        {
            CrosshairRefs r;
            int serialOffset, serialSize, exportIndex, insertBc;
            List<int> jumpPositions;
            using (var pkg = UePackageLocator.Load(tdGamePath))
            {
                r = ResolveCrosshairRefs(pkg);
                var fn = UePackageLocator.FindFunction(pkg, "TdSPHUD", "DrawLivingHUD")
                    ?? throw new InvalidOperationException("Cannot find TdSPHUD.DrawLivingHUD in TdGame.u");
                serialOffset = fn.SerialOffset;
                serialSize = fn.SerialSize;
                exportIndex = fn.ExportIndex;
                jumpPositions = UePackageLocator.ExtractJumpPositions(fn.Tokens);

                // The blob runs after DrawLivingHUD's early returns, which guard against UnarmedCrossHair == none.
                // It also runs after the crosshair is drawn, so new sizes show from the next frame.
                insertBc = FindLastReturnInsertBc(fn.Tokens);
            }

            if (Helpers.PatternHelper.FileContainsAny(tdGamePath, [CrosshairSig(r)]))
                return;

            byte[] blob = CrosshairBlob(r);
            byte[] export = UePackageLocator.ReadRange(tdGamePath, serialOffset, serialSize);
            int bcStart = BytecodeBuilder.SCRIPT_HDR;

            PackageSplicer.ShiftJumpTargets(export, bcStart, jumpPositions, insertBc, blob.Length);
            PackageSplicer.UpdateBSS(export, 0, blob.Length);
            export = PackageSplicer.InsertBytes(export, bcStart + insertBc, blob);

            PackageSplicer.SpliceExport(tdGamePath, serialOffset, serialSize, export, (header, hdr) =>
                PackageSplicer.UpdateExportsStructural(header, hdr,
                    new List<(int, int, int)> { (serialOffset, blob.Length, exportIndex) }));
        }

        public static void RemoveCrosshair(string tdGamePath)
        {
            CrosshairRefs r;
            int serialOffset, serialSize, exportIndex;
            List<int> jumpPositions;
            using (var pkg = UePackageLocator.Load(tdGamePath))
            {
                r = ResolveCrosshairRefs(pkg);
                var fn = UePackageLocator.FindFunction(pkg, "TdSPHUD", "DrawLivingHUD")
                    ?? throw new InvalidOperationException("Cannot find TdSPHUD.DrawLivingHUD in TdGame.u");
                serialOffset = fn.SerialOffset;
                serialSize = fn.SerialSize;
                exportIndex = fn.ExportIndex;
                jumpPositions = UePackageLocator.ExtractJumpPositions(fn.Tokens);
            }

            byte[] blob = CrosshairBlob(r);
            byte[] export = UePackageLocator.ReadRange(tdGamePath, serialOffset, serialSize);
            int bcStart = BytecodeBuilder.SCRIPT_HDR;
            int bss = (int)PackageSplicer.ReadBSS(export, 0);

            int blobPos = BytecodeBuilder.FindPattern(export, blob, bcStart, bcStart + bss);
            if (blobPos == -1) return;
            int insertBc = blobPos - bcStart;

            PackageSplicer.ShiftJumpTargets(export, bcStart, jumpPositions, insertBc, -blob.Length);
            PackageSplicer.UpdateBSS(export, 0, -blob.Length);
            export = PackageSplicer.RemoveBytes(export, blobPos, blob.Length);

            PackageSplicer.SpliceExport(tdGamePath, serialOffset, serialSize, export, (header, hdr) =>
                PackageSplicer.UpdateExportsStructural(header, hdr,
                    new List<(int, int, int)> { (serialOffset, -blob.Length, exportIndex) }));
        }

        sealed class EngineRefs
        {
            public int PlayerOwnerRef;
            public int SizeXRef;
            public int SizeYRef;
            public int ConsoleNameIdx;
            public int ConsoleReturnValueRef;
        }

        static EngineRefs ResolveEngineRefs(UnrealPackage pkg)
        {
            var r = new EngineRefs
            {
                PlayerOwnerRef = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "PlayerOwner", "HUD")),
                SizeXRef = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "SizeX", "HUD")),
                SizeYRef = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "SizeY", "HUD")),
                ConsoleNameIdx = UePackageLocator.FindNameIndex(pkg, "ConsoleCommand"),
                ConsoleReturnValueRef = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "ReturnValue", "ConsoleCommand", "PlayerController")),
            };
            if (r.PlayerOwnerRef == 0 || r.SizeXRef == 0 || r.SizeYRef == 0 || r.ConsoleNameIdx < 0
                || r.ConsoleReturnValueRef == 0)
                throw new InvalidOperationException("Cannot resolve HUD.PreCalcValues references in Engine.u");
            return r;
        }

        // Returns the offset of the Return that ends PreCalcValues, so the blob runs after SizeX and SizeY are recalculated.
        // PreCalcValues has no jumps, so the insertion moves no jump targets.
        static int FindReturnInsertBc(IList<Token> tokens)
        {
            foreach (var t in tokens)
                if (t is ReturnToken)
                    return UePackageLocator.Pos(t);
            throw new InvalidOperationException("Cannot locate Return in HUD.PreCalcValues");
        }

        // Returns the offset of the last Return, which only runs once every early-return guard has passed.
        static int FindLastReturnInsertBc(IList<Token> tokens)
        {
            int pos = -1;
            foreach (var t in tokens)
                if (t is ReturnToken)
                    pos = UePackageLocator.Pos(t);
            if (pos < 0)
                throw new InvalidOperationException("Cannot locate Return in TdSPHUD.DrawLivingHUD");
            return pos;
        }
    }
}
