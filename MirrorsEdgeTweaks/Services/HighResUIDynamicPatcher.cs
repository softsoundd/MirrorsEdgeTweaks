using System.IO;
using UELib;
using static UELib.Core.UStruct.UByteCodeDecompiler;

namespace MirrorsEdgeTweaks.Services
{
    // Injects a self contained ConsoleCommand("set ...") blob into stock packages so the font
    // ResolutionTestTable, UIStyle_Text scale and subtitle region track the live viewport size and
    // are reapplied automatically whenever the resolution changes
    //
    //   Engine.u  : HUD.PreCalcValues (fires on every size change in menu and gameplay - see ApplyEngine)
    //   TdGame.u  : the video settings apply path
    //
    // The blob is equivalent to (see BytecodeBuilder.BuildHighResCommands for the exact expressions):
    //   ConsoleCommand("set MultiFont ResolutionTestTable (480,720," $ int(FMax(1080, SizeX*0.5625+0.5)) $ ")");
    //   ConsoleCommand("set UIStyle_Text Scale (X=" $ (FMin(SizeY,SizeX*0.5625)/FMin(SizeX*0.5625,1080)) $ ",Y=...)");
    //   ConsoleCommand("set TdGameViewportClient SubtitleMinRegion (X=" $ (0.5-FMin(FMax(0.4*r*r,0.5*SizeY/SizeX),0.5)) $ ",Y=" $ (0.5-0.35*r) $ ")");   // r=(SizeY/SizeX)*16/9
    //   ConsoleCommand("set TdGameViewportClient SubtitleMaxRegion (X=" $ (0.5+FMin(FMax(0.4*r*r,0.5*SizeY/SizeX),0.5)) $ ",Y=" $ (0.5+0.35*r) $ ")");
    //   ConsoleCommand("set TdUIScene bRefreshWidgetStyles true");
    public static class HighResUIDynamicPatcher
    {
        // Active == the (primary) Engine.u HUD.PreCalcValues hook is present.
        public static bool IsActive(string enginePath) => IsEnginePatched(enginePath);

        // Applies both hooks. The Engine.u hook is the primary fix; the TdGame.u menu hook is
        // best-effort (front-end instant apply) and never blocks the primary fix.
        public static void ApplyAll(string enginePath, string tdGamePath)
        {
            ApplyEngine(enginePath);
            try
            {
                if (File.Exists(tdGamePath)) ApplyTdGame(tdGamePath);
            }
            catch { }
            // Dynamic crosshair scaling (TdSPHUD.DrawLivingHUD) - best-effort, never blocks the primary fix.
            try
            {
                if (File.Exists(tdGamePath)) ApplyCrosshair(tdGamePath);
            }
            catch { }
        }

        public static void RemoveAll(string enginePath, string tdGamePath)
        {
            try { if (File.Exists(enginePath)) RemoveEngine(enginePath); } catch { }
            try { if (File.Exists(tdGamePath)) RemoveTdGame(tdGamePath); } catch { }
            try { if (File.Exists(tdGamePath)) RemoveCrosshair(tdGamePath); } catch { }
        }

        // Engine.u : HUD.PreCalcValues

        public static bool IsEnginePatched(string enginePath)
        {
            if (!File.Exists(enginePath)) return false;
            byte[] data = File.ReadAllBytes(enginePath);
            return BytecodeBuilder.FindPattern(data, BytecodeBuilder.HighResSignature) != -1;
        }

        public static void ApplyEngine(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            if (BytecodeBuilder.FindPattern(data, BytecodeBuilder.HighResSignature) != -1)
                return; // already patched

            EngineRefs r;
            int serialOffset, insertBc;
            List<int> jumpPositions;
            using (var pkg = UePackageLocator.Load(enginePath))
            {
                r = ResolveEngineRefs(pkg);
                // PreCalcValues (not PostRender): TdHUD.PostRender overrides HUD.PostRender without
                // calling super, but no HUD subclass overrides PreCalcValues, so every HUD calls the
                // inherited HUD.PreCalcValues on a size change - a universal hook that fires on boot
                // (the menu runs a TdSPHUD) and in gameplay.
                var fn = UePackageLocator.FindFunction(pkg, "HUD", "PreCalcValues")
                    ?? throw new InvalidOperationException("Cannot find HUD.PreCalcValues in Engine.u");
                serialOffset = fn.SerialOffset;
                insertBc = FindReturnInsertBc(fn.Tokens);
                jumpPositions = ExtractJumpPositions(fn.Tokens);
            }

            byte[] blob = BytecodeBuilder.BuildHighResApplyBlob(
                r.PlayerOwnerRef, r.ConsoleNameIdx, r.ConsoleReturnValueRef, r.SizeXRef, r.SizeYRef);

            int exportStart = serialOffset;
            int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;
            int origLen = data.Length;
            int insertFile = bcStart + insertBc;

            ShiftJumpTargets(data, bcStart, jumpPositions, insertBc, blob.Length);
            PackageSplicer.UpdateBSS(data, exportStart, blob.Length);
            data = PackageSplicer.InsertBytes(data, insertFile, blob);

            var hdr = PackageSplicer.ParseHeader(data);
            PackageSplicer.UpdateExportsHeuristic(data, hdr, exportStart, insertFile, origLen, blob.Length);

            File.WriteAllBytes(enginePath, data);
        }

        public static void RemoveEngine(string enginePath)
        {
            byte[] data = File.ReadAllBytes(enginePath);
            if (BytecodeBuilder.FindPattern(data, BytecodeBuilder.HighResSignature) == -1)
                return; // not patched

            EngineRefs r;
            int serialOffset;
            List<int> jumpPositions;
            using (var pkg = UePackageLocator.Load(enginePath))
            {
                r = ResolveEngineRefs(pkg);
                var fn = UePackageLocator.FindFunction(pkg, "HUD", "PreCalcValues")
                    ?? throw new InvalidOperationException("Cannot find HUD.PreCalcValues in Engine.u");
                serialOffset = fn.SerialOffset;
                jumpPositions = ExtractJumpPositions(fn.Tokens);
            }

            byte[] blob = BytecodeBuilder.BuildHighResApplyBlob(
                r.PlayerOwnerRef, r.ConsoleNameIdx, r.ConsoleReturnValueRef, r.SizeXRef, r.SizeYRef);

            int exportStart = serialOffset;
            int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;
            int bss = (int)PackageSplicer.ReadBSS(data, exportStart);
            int origLen = data.Length;

            int blobPos = BytecodeBuilder.FindPattern(data, blob, bcStart, bcStart + bss);
            if (blobPos == -1) return;
            int insertBc = blobPos - bcStart;

            ShiftJumpTargets(data, bcStart, jumpPositions, insertBc, -blob.Length);
            PackageSplicer.UpdateBSS(data, exportStart, -blob.Length);
            data = PackageSplicer.RemoveBytes(data, blobPos, blob.Length);

            var hdr = PackageSplicer.ParseHeader(data);
            PackageSplicer.UpdateExportsHeuristic(data, hdr, exportStart, blobPos, origLen, -blob.Length);

            File.WriteAllBytes(enginePath, data);
        }

        // TdGame.u : TdSPHUD.DrawLivingHUD (dynamic crosshair scaling)

        // Each crosshair texture member of TdSPHUD and its stock pixel size. SizeX/SizeY are scaled to
        // base * FMax(1, HUD.SizeY/1080) at the start of DrawLivingHUD (runs each frame -> follows
        // in-game resolution changes). Matches the old static fix's values (Unarmed/Reaction 16, Weapon 64).
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
            byte[] data = File.ReadAllBytes(tdGamePath);
            CrosshairRefs r;
            int serialOffset, exportIndex, insertBc;
            List<int> jumpPositions;
            using (var pkg = UePackageLocator.Load(tdGamePath))
            {
                r = ResolveCrosshairRefs(pkg);
                var fn = UePackageLocator.FindFunction(pkg, "TdSPHUD", "DrawLivingHUD")
                    ?? throw new InvalidOperationException("Cannot find TdSPHUD.DrawLivingHUD in TdGame.u");
                serialOffset = fn.SerialOffset;
                exportIndex = fn.ExportIndex;
                jumpPositions = ExtractJumpPositions(fn.Tokens);
                // Insert before the function's final Return (not the start): the early-return guards
                // include "UnarmedCrossHair == none", so the textures are only guaranteed non-none once
                // execution falls through to the end. Costs a 1-frame size lag (imperceptible).
                insertBc = FindLastReturnInsertBc(fn.Tokens);
            }

            if (BytecodeBuilder.FindPattern(data, CrosshairSig(r)) != -1)
                return; // already patched

            byte[] blob = CrosshairBlob(r);
            int exportStart = serialOffset;
            int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;

            ShiftJumpTargets(data, bcStart, jumpPositions, insertBc, blob.Length);
            PackageSplicer.UpdateBSS(data, exportStart, blob.Length);
            data = PackageSplicer.InsertBytes(data, bcStart + insertBc, blob);

            var hdr = PackageSplicer.ParseHeader(data);
            PackageSplicer.UpdateExportsStructural(data, hdr,
                new List<(int, int, int)> { (exportStart, blob.Length, exportIndex) });

            File.WriteAllBytes(tdGamePath, data);
        }

        public static void RemoveCrosshair(string tdGamePath)
        {
            byte[] data = File.ReadAllBytes(tdGamePath);
            CrosshairRefs r;
            int serialOffset, exportIndex;
            List<int> jumpPositions;
            using (var pkg = UePackageLocator.Load(tdGamePath))
            {
                r = ResolveCrosshairRefs(pkg);
                var fn = UePackageLocator.FindFunction(pkg, "TdSPHUD", "DrawLivingHUD")
                    ?? throw new InvalidOperationException("Cannot find TdSPHUD.DrawLivingHUD in TdGame.u");
                serialOffset = fn.SerialOffset;
                exportIndex = fn.ExportIndex;
                jumpPositions = ExtractJumpPositions(fn.Tokens);
            }

            byte[] blob = CrosshairBlob(r);
            int exportStart = serialOffset;
            int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;
            int bss = (int)PackageSplicer.ReadBSS(data, exportStart);

            int blobPos = BytecodeBuilder.FindPattern(data, blob, bcStart, bcStart + bss);
            if (blobPos == -1) return; // not patched
            int insertBc = blobPos - bcStart;

            ShiftJumpTargets(data, bcStart, jumpPositions, insertBc, -blob.Length);
            PackageSplicer.UpdateBSS(data, exportStart, -blob.Length);
            data = PackageSplicer.RemoveBytes(data, blobPos, blob.Length);

            var hdr = PackageSplicer.ParseHeader(data);
            PackageSplicer.UpdateExportsStructural(data, hdr,
                new List<(int, int, int)> { (exportStart, -blob.Length, exportIndex) });

            File.WriteAllBytes(tdGamePath, data);
        }

        // TdGame.u : TdUIScene_VideoSettingsPC.ApplyVideoSettings

        public static void ApplyTdGame(string tdGamePath)
        {
            byte[] data = File.ReadAllBytes(tdGamePath);
            if (BytecodeBuilder.FindPattern(data, BytecodeBuilder.HighResSignature) != -1)
                return; // already patched

            TdGameRefs r;
            int serialOffset, exportIndex, insertBc;
            List<int> jumpPositions;
            using (var pkg = UePackageLocator.Load(tdGamePath))
            {
                r = ResolveTdGameRefs(pkg);
                var fn = UePackageLocator.FindFunction(pkg, "TdUIScene_VideoSettingsPC", "ApplyVideoSettings")
                    ?? throw new InvalidOperationException("Cannot find TdUIScene_VideoSettingsPC.ApplyVideoSettings");
                serialOffset = fn.SerialOffset;
                exportIndex = fn.ExportIndex;
                insertBc = FindAfterCall(fn.Tokens, "SetScreenResolution");
                jumpPositions = ExtractJumpPositions(fn.Tokens);
            }

            byte[] blob = BuildTdGameBlob(r);

            int exportStart = serialOffset;
            int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;
            int insertFile = bcStart + insertBc;

            ShiftJumpTargets(data, bcStart, jumpPositions, insertBc, blob.Length);
            PackageSplicer.UpdateBSS(data, exportStart, blob.Length);
            data = PackageSplicer.InsertBytes(data, insertFile, blob);

            var hdr = PackageSplicer.ParseHeader(data);
            PackageSplicer.UpdateExportsStructural(data, hdr,
                new List<(int, int, int)> { (exportStart, blob.Length, exportIndex) });

            File.WriteAllBytes(tdGamePath, data);
        }

        public static void RemoveTdGame(string tdGamePath)
        {
            byte[] data = File.ReadAllBytes(tdGamePath);
            if (BytecodeBuilder.FindPattern(data, BytecodeBuilder.HighResSignature) == -1)
                return; // not patched

            TdGameRefs r;
            int serialOffset, exportIndex;
            List<int> jumpPositions;
            using (var pkg = UePackageLocator.Load(tdGamePath))
            {
                r = ResolveTdGameRefs(pkg);
                var fn = UePackageLocator.FindFunction(pkg, "TdUIScene_VideoSettingsPC", "ApplyVideoSettings")
                    ?? throw new InvalidOperationException("Cannot find TdUIScene_VideoSettingsPC.ApplyVideoSettings");
                serialOffset = fn.SerialOffset;
                exportIndex = fn.ExportIndex;
                jumpPositions = ExtractJumpPositions(fn.Tokens);
            }

            byte[] blob = BuildTdGameBlob(r);

            int exportStart = serialOffset;
            int bcStart = exportStart + BytecodeBuilder.SCRIPT_HDR;
            int bss = (int)PackageSplicer.ReadBSS(data, exportStart);

            int blobPos = BytecodeBuilder.FindPattern(data, blob, bcStart, bcStart + bss);
            if (blobPos == -1)
                throw new InvalidOperationException("High-res menu blob not found for removal");
            int insertBc = blobPos - bcStart;

            ShiftJumpTargets(data, bcStart, jumpPositions, insertBc, -blob.Length);
            PackageSplicer.UpdateBSS(data, exportStart, -blob.Length);
            data = PackageSplicer.RemoveBytes(data, blobPos, blob.Length);

            var hdr = PackageSplicer.ParseHeader(data);
            PackageSplicer.UpdateExportsStructural(data, hdr,
                new List<(int, int, int)> { (exportStart, -blob.Length, exportIndex) });

            File.WriteAllBytes(tdGamePath, data);
        }

        sealed class TdGameRefs
        {
            public int ConsoleNameIdx;
            public int ResXRef;
            public int ResYRef;
            public int StructRef;
            public int NewResolutionRef;
        }

        static TdGameRefs ResolveTdGameRefs(UnrealPackage pkg)
        {
            var r = new TdGameRefs
            {
                ConsoleNameIdx = UePackageLocator.FindNameIndex(pkg, "ConsoleCommand"),
                ResXRef = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "ResX", "ScreenResSetting")),
                ResYRef = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "ResY", "ScreenResSetting")),
                StructRef = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "ScreenResSetting", "TdUIScene_VideoSettingsPC")),
                NewResolutionRef = UePackageLocator.ObjRef(UePackageLocator.FindExportObject(pkg, "NewResolution", "TdUIScene_VideoSettingsPC")),
            };
            if (r.ConsoleNameIdx < 0 || r.ResXRef == 0 || r.ResYRef == 0 || r.StructRef == 0 || r.NewResolutionRef == 0)
                throw new InvalidOperationException("Cannot resolve TdUIScene_VideoSettingsPC references in TdGame.u");
            return r;
        }

        static byte[] BuildTdGameBlob(TdGameRefs r)
        {
            byte[] width = BytecodeBuilder.StructMember(r.ResXRef, r.StructRef, BytecodeBuilder.InstVar(r.NewResolutionRef));
            byte[] height = BytecodeBuilder.StructMember(r.ResYRef, r.StructRef, BytecodeBuilder.InstVar(r.NewResolutionRef));
            return BytecodeBuilder.BuildHighResApplyBlobSelfCall(r.ConsoleNameIdx, width, height);
        }

        // Insertion point = byte offset just after the closing EndFunctionParms of the named call.
        static int FindAfterCall(IList<Token> tokens, string funcName)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] is VirtualFunctionToken vf && vf.FunctionName?.ToString() == funcName)
                {
                    for (int j = i + 1; j < tokens.Count; j++)
                        if (tokens[j] is EndFunctionParmsToken)
                            return UePackageLocator.Pos(tokens[j]) + 1;
                }
            }
            throw new InvalidOperationException($"Cannot locate {funcName}() call");
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

        // Insertion point = the function's terminating Return token. Inserting before it appends the
        // blob to the end of PreCalcValues' body (after SizeX/SizeY have been recalculated), and
        // PreCalcValues has no jumps so nothing else needs shifting.
        static int FindReturnInsertBc(IList<Token> tokens)
        {
            foreach (var t in tokens)
                if (t is ReturnToken)
                    return UePackageLocator.Pos(t);
            throw new InvalidOperationException("Cannot locate Return in HUD.PreCalcValues");
        }

        // The function's terminating (last) Return. Used for crosshair insertion: a function with
        // early-return guards has several Returns, and only the final one is reached after all guards
        // pass, so inserting there avoids touching guarded-against (possibly none) state.
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

        // Bytecode offsets of every real jump opcode (Jump/JumpIfNot/Case). Using token positions
        // rather than a raw byte scan avoids misreading data bytes (e.g. a 0x06 inside an ObjectConst)
        // as a jump.
        static List<int> ExtractJumpPositions(IList<Token> tokens)
        {
            var positions = new List<int>();
            foreach (var t in tokens)
                if (t is JumpToken || t is JumpIfNotToken || t is CaseToken)
                    positions.Add(UePackageLocator.Pos(t));
            return positions;
        }

        // Shifts the u16 code-offset (at jumpBc+1) of each real jump whose target is >= thresholdBc.
        static void ShiftJumpTargets(byte[] data, int bcStart, List<int> jumpPositions, int thresholdBc, int delta)
        {
            foreach (int bcPos in jumpPositions)
            {
                int fieldPos = bcStart + bcPos + 1;
                ushort cur = BitConverter.ToUInt16(data, fieldPos);
                if (cur == 0xFFFF) continue; // default case / no target
                if (cur >= thresholdBc)
                {
                    int nt = cur + delta;
                    if (nt >= 0 && nt <= 0xFFFF)
                        BitConverter.GetBytes((ushort)nt).CopyTo(data, fieldPos);
                }
            }
        }
    }
}
