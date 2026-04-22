using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum LoggingPatchState
    {
        Unknown,
        Unpatched,
        Patched
    }

    public static class LoggingPatchHelper
    {
        private const uint ImageBase = 0x00400000;

        private static readonly string[] FullSymbols =
        {
            "Logf", "GLog", "GFileManager", "GCmdLine", "Parse",
            "FOutputDeviceFile.FileArchive", "FOutputDeviceFile.Filename", "FmtPercentS",
        };

        private static readonly string[] LazySymbols = { "Logf", "GLog", "GFileManager", "FmtPercentS" };

        public static LoggingPatchState GetPatchState(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null) return LoggingPatchState.Unknown;

            if (version == "ea")
            {
                try { DecryptOoaInPlace(data); }
                catch { return LoggingPatchState.Unknown; }
            }

            VersionAddressTable addrs;
            try { addrs = VersionAddressTable.Load(version); }
            catch { return LoggingPatchState.Unknown; }

            if (!addrs.Hooks.TryGetValue("execLog", out var execDef))
                return LoggingPatchState.Unknown;

            // Try pattern scan for unpatched hook site
            try
            {
                uint hookVa = VersionAddressTable.ResolveHook(data, execDef);
                int foff = (int)(hookVa - ImageBase);
                if (foff >= 0 && foff + execDef.Size <= data.Length &&
                    data.AsSpan(foff, execDef.Pattern.Length).SequenceEqual(execDef.Pattern))
                    return LoggingPatchState.Unpatched;
            }
            catch {}

            // Pattern replaced: scan the same window for our hook signature (E9 + NOP pad)
            int startOff = (int)(execDef.ScanStart - ImageBase);
            int scanEnd = startOff + execDef.ScanSize - execDef.Size;
            if (startOff >= 0 && scanEnd + execDef.Size <= data.Length)
            {
                for (int i = startOff; i <= scanEnd; i++)
                {
                    if (data[i] == 0xE9 && data[i + 5] == 0x90)
                        return LoggingPatchState.Patched;
                }
            }

            return LoggingPatchState.Unknown;
        }

        public static void ApplyPatch(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);

            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null)
                throw new InvalidOperationException("Unrecognized executable -- cannot detect game version.");

            bool isOoa = version == "ea";
            OoaContext? ooaCtx = null;
            byte[]? ooaKey = null;

            if (isOoa)
            {
                string? dlfPath = OoaService.FindLicensePath(data);
                if (dlfPath == null)
                    throw new OoaLicenseNotFoundException(OoaService.GetExpectedLicensePath(data));
                ooaKey = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
                OoaService.StripAuthenticode(data);
                ooaCtx = OoaService.DecryptSections(data, ooaKey);
            }

            var addrs = VersionAddressTable.Load(version);
            if (addrs.Symbols.Count == 0)
                throw new InvalidOperationException($"Address table for '{version}' has no symbols.");

            var sym = addrs.Symbols;
            var hooks = addrs.Hooks;

            var execDef = hooks["execLog"];
            uint execHookVa = VersionAddressTable.ResolveHook(data, execDef);
            uint execReturnVa = execHookVa + (uint)execDef.ReturnOffset;

            int execFoff = (int)(execHookVa - ImageBase);
            if (data[execFoff] == 0xE9) return;

            bool useFullMode = hooks.ContainsKey("preInit_fileLog");
            uint preInitVa = 0;
            if (useFullMode)
            {
                try { preInitVa = VersionAddressTable.ResolveHook(data, hooks["preInit_fileLog"]); }
                catch { useFullMode = false; }
            }

            string[] needed = useFullMode ? FullSymbols : LazySymbols;
            foreach (string s in needed)
            {
                if (!sym.ContainsKey(s))
                    throw new InvalidOperationException($"Missing required symbol: {s}");
            }

            var cave = CaveSection.Open(data, versionTag: version, forceTextPadding: isOoa);

            if (useFullMode)
                data = ApplyFullMode(data, cave, sym, hooks, execHookVa, execReturnVa, preInitVa);
            else
                data = ApplyAppInitMode(data, cave, sym, execHookVa, execReturnVa);

            if (isOoa)
            {
                OoaService.UpdateEncBlockCrcs(data, ooaCtx!);
                OoaService.ReencryptSections(data, ooaKey!, ooaCtx!);
                data = OoaService.TruncateOverlay(data, ooaCtx!);
            }

            WritePreservingAttributes(exePath, data);
        }

        public static void RemovePatch(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);

            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null) return;

            bool isOoa = version == "ea";
            OoaContext? ooaCtx = null;
            byte[]? ooaKey = null;

            if (isOoa)
            {
                try
                {
                    string? dlfPath = OoaService.FindLicensePath(data);
                    if (dlfPath == null) return;
                    ooaKey = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
                    OoaService.StripAuthenticode(data);
                    ooaCtx = OoaService.DecryptSections(data, ooaKey);
                }
                catch { return; }
            }

            VersionAddressTable addrs;
            try { addrs = VersionAddressTable.Load(version); }
            catch { return; }

            if (!addrs.Hooks.TryGetValue("execLog", out var execDef))
                return;

            RestoreHook(data, execDef);

            if (addrs.Hooks.TryGetValue("preInit_fileLog", out var preInitDef))
                RestoreHook(data, preInitDef);

            if (isOoa && ooaCtx != null && ooaKey != null)
            {
                OoaService.UpdateEncBlockCrcs(data, ooaCtx);
                OoaService.ReencryptSections(data, ooaKey, ooaCtx);
                data = OoaService.TruncateOverlay(data, ooaCtx);
            }

            WritePreservingAttributes(exePath, data);
        }

        private static void RestoreHook(byte[] data, HookDefinition hdef)
        {
            int hookFoff = -1;

            try
            {
                uint hookVa = VersionAddressTable.ResolveHook(data, hdef);
                hookFoff = (int)(hookVa - ImageBase);
            }
            catch
            {
                int startOff = (int)(hdef.ScanStart - ImageBase);
                int scanEnd = startOff + hdef.ScanSize - hdef.Size;
                if (startOff >= 0 && scanEnd + hdef.Size <= data.Length)
                {
                    for (int i = startOff; i <= scanEnd; i++)
                    {
                        if (data[i] == 0xE9 && data[i + 5] == 0x90)
                        {
                            hookFoff = i;
                            break;
                        }
                    }
                }
            }

            if (hookFoff < 0 || hookFoff + hdef.Size > data.Length)
                return;

            if (data[hookFoff] == 0xE9)
            {
                Buffer.BlockCopy(hdef.Pattern, 0, data, hookFoff, hdef.Pattern.Length);
                for (int i = hdef.Pattern.Length; i < hdef.Size; i++)
                    data[hookFoff + i] = 0x90;
            }
        }

        private static byte[] ApplyFullMode(byte[] data, CaveSection cave,
            Dictionary<string, uint> sym, Dictionary<string, HookDefinition> hooks,
            uint execHookVa, uint execReturnVa, uint preInitVa)
        {
            var preInitDef = hooks["preInit_fileLog"];
            uint fileReturnVa = preInitVa + (uint)preInitDef.ReturnOffset;

            uint p1Va = cave.Alloc(64);
            byte[] p1 = BuildCavePatch1(p1Va, sym, execReturnVa);
            cave.Write(p1Va, p1);

            byte[] strData = BuildCaveDataFull();
            uint dVa = cave.Alloc(strData.Length, align: 4);
            cave.Write(dVa, strData);

            uint p2Va = cave.Alloc(192);
            byte[] p2 = BuildCavePatch2(p2Va, sym, fileReturnVa,
                dVa, dVa + 0x0C, dVa + 0x20, preInitDef.Pattern);
            cave.Write(p2Va, p2);

            data = cave.Finalize();

            byte[] hook1 = BuildHookJmp(execHookVa, p1Va, 6);
            int off1 = (int)(execHookVa - ImageBase);
            Buffer.BlockCopy(hook1, 0, data, off1, hook1.Length);

            byte[] hook2 = BuildHookJmp(preInitVa, p2Va, 6);
            int off2 = (int)(preInitVa - ImageBase);
            Buffer.BlockCopy(hook2, 0, data, off2, hook2.Length);

            return data;
        }

        private static byte[] ApplyAppInitMode(byte[] data, CaveSection cave,
            Dictionary<string, uint> sym, uint execHookVa, uint execReturnVa)
        {
            uint fileArchiveVa = sym.GetValueOrDefault("FOutputDeviceFile.FileArchive");
            uint filenameBufVa = sym.GetValueOrDefault("FOutputDeviceFile.Filename");

            byte[] strData = BuildCaveDataFull();
            int dataSize = 4 + strData.Length;
            uint dataVa = cave.Alloc(dataSize, align: 4);
            uint archivePtrVa = dataVa;
            uint strVa = dataVa + 4;
            cave.Write(strVa, strData);

            uint logKeyVa = strVa;
            uint abslogKeyVa = strVa + 0x0C;
            uint defaultFilenameVa = strVa + 0x20;

            uint? appInitHookVa = FindAppInitSite(data, sym);

            if (appInitHookVa.HasValue)
            {
                int hookOff = (int)(appInitHookVa.Value - ImageBase);
                byte[] overwritten = new byte[5];
                Buffer.BlockCopy(data, hookOff, overwritten, 0, 5);
                uint initReturnVa = appInitHookVa.Value + 5;

                uint initVa = cave.Alloc(256);
                byte[] initCode = BuildCaveInit(initVa, sym, initReturnVa,
                    archivePtrVa, defaultFilenameVa, overwritten,
                    fileArchiveVa, logKeyVa, abslogKeyVa, filenameBufVa);
                cave.Write(initVa, initCode);

                uint caveVa = cave.Alloc(256);
                byte[] caveCode = BuildCaveUnified(caveVa, sym, execReturnVa,
                    archivePtrVa, defaultFilenameVa, fileArchiveVa);
                cave.Write(caveVa, caveCode);

                data = cave.Finalize();

                byte[] initHook = new byte[5];
                initHook[0] = 0xE9;
                Buffer.BlockCopy(MachineCodeBuilder.Rel32Bytes(appInitHookVa.Value, initVa), 0, initHook, 1, 4);
                Buffer.BlockCopy(initHook, 0, data, hookOff, 5);

                byte[] execHook = BuildHookJmp(execHookVa, caveVa, 6);
                int execOff = (int)(execHookVa - ImageBase);
                Buffer.BlockCopy(execHook, 0, data, execOff, execHook.Length);
            }
            else
            {
                uint caveVa = cave.Alloc(256);
                byte[] caveCode = BuildCaveUnified(caveVa, sym, execReturnVa,
                    archivePtrVa, defaultFilenameVa, fileArchiveVa);
                cave.Write(caveVa, caveCode);

                data = cave.Finalize();

                byte[] execHook = BuildHookJmp(execHookVa, caveVa, 6);
                int execOff = (int)(execHookVa - ImageBase);
                Buffer.BlockCopy(execHook, 0, data, execOff, execHook.Length);
            }

            return data;
        }

        private static uint? FindAppInitSite(byte[] data, Dictionary<string, uint> sym)
        {
            if (!sym.TryGetValue("GLog", out uint glog)) return null;
            byte[] glogBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(glogBytes, glog);

            uint scanStart = 0x010EB000;
            int scanSize = 0x2000;
            int startOff = (int)(scanStart - ImageBase);
            if (startOff < 0 || startOff + scanSize > data.Length) return null;

            byte[] pat = { 0x56, 0xFF, 0xD2, 0x68 };
            int idx = 0;
            while (idx <= scanSize - 8)
            {
                int pos = -1;
                for (int i = startOff + idx; i <= startOff + scanSize - pat.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; j < pat.Length; j++)
                    {
                        if (data[i + j] != pat[j]) { match = false; break; }
                    }
                    if (match) { pos = i; break; }
                }
                if (pos < 0) break;

                int relPos = pos - startOff;
                int preStart = Math.Max(0, pos - 30);
                bool found = false;
                for (int k = preStart; k <= pos - 4; k++)
                {
                    if (data[k] == glogBytes[0] && data[k + 1] == glogBytes[1] &&
                        data[k + 2] == glogBytes[2] && data[k + 3] == glogBytes[3])
                    {
                        found = true;
                        break;
                    }
                }

                if (found)
                    return scanStart + (uint)relPos + 3;

                idx = relPos + 1;
            }

            return null;
        }

        private static byte[] BuildCavePatch1(uint baseVa, Dictionary<string, uint> sym, uint returnVa)
        {
            var code = new List<byte>();
            void Add(params byte[] b) => code.AddRange(b);
            void AddU32(uint v) { var buf = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buf, v); code.AddRange(buf); }
            uint Va() => baseVa + (uint)code.Count;
            void EmitCall(uint target) { uint v = Va(); Add(0xE8); code.AddRange(MachineCodeBuilder.Rel32Bytes(v, target)); }
            void EmitJmpNear(uint target) { uint v = Va(); Add(0xE9); code.AddRange(MachineCodeBuilder.Rel32Bytes(v, target)); }

            Add(0x8B, 0x44, 0x24, 0x14); // mov eax, [esp+0x14]
            Add(0x3B, 0xC7);             // cmp eax, edi

            int jePos = code.Count;
            Add(0x74, 0x00);             // je skip_log (patched)

            Add(0x50);                                     // push eax
            Add(0x68); AddU32(sym["FmtPercentS"]);         // push L"%s"
            Add(0xFF, 0x35); AddU32(sym["GLog"]);          // push [GLog]
            EmitCall(sym["Logf"]);
            Add(0x83, 0xC4, 0x0C);                        // add esp, 0x0C

            Add(0x8B, 0x44, 0x24, 0x14);                  // mov eax, [esp+0x14]
            Add(0x3B, 0xC7);                               // cmp eax, edi

            int skipTarget = code.Count;
            code[jePos + 1] = (byte)(skipTarget - (jePos + 2));

            EmitJmpNear(returnVa);

            return code.ToArray();
        }

        private static byte[] BuildCavePatch2(uint baseVa, Dictionary<string, uint> sym,
            uint returnVa, uint dataLogKey, uint dataAbslogKey, uint dataDefault,
            byte[] overwrittenBytes)
        {
            var code = new List<byte>();
            void Add(params byte[] b) => code.AddRange(b);
            void AddU32(uint v) { var buf = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buf, v); code.AddRange(buf); }
            uint Va() => baseVa + (uint)code.Count;
            void EmitCall(uint target) { uint v = Va(); Add(0xE8); code.AddRange(MachineCodeBuilder.Rel32Bytes(v, target)); }
            void EmitJmpNear(uint target) { uint v = Va(); Add(0xE9); code.AddRange(MachineCodeBuilder.Rel32Bytes(v, target)); }

            uint GFILEMANAGER = sym["GFileManager"];
            uint FILENAME = sym["FOutputDeviceFile.Filename"];
            uint ARCHIVE = sym["FOutputDeviceFile.FileArchive"];
            uint GCMDLINE = sym["GCmdLine"];
            uint PARSE = sym["Parse"];

            code.AddRange(overwrittenBytes);

            Add(0x8B, 0x0D); AddU32(GFILEMANAGER); // mov ecx, [GFileManager]
            Add(0x85, 0xC9);                        // test ecx, ecx
            int jzSkipPos = code.Count;
            Add(0x0F, 0x84, 0x00, 0x00, 0x00, 0x00); // jz skip (patched)

            Add(0x55);             // push ebp
            Add(0x8B, 0xEC);       // mov ebp, esp

            // Parse(GCmdLine, L"LOG=", Filename, 0x400)
            Add(0x68); AddU32(0x400);
            Add(0x68); AddU32(FILENAME);
            Add(0x68); AddU32(dataLogKey);
            Add(0x68); AddU32(GCMDLINE);
            EmitCall(PARSE);
            Add(0x83, 0xC4, 0x10);
            Add(0x85, 0xC0);
            int jnzParsed1 = code.Count;
            Add(0x75, 0x00);

            // Parse(GCmdLine, L"ABSLOG=", Filename, 0x400)
            Add(0x68); AddU32(0x400);
            Add(0x68); AddU32(FILENAME);
            Add(0x68); AddU32(dataAbslogKey);
            Add(0x68); AddU32(GCMDLINE);
            EmitCall(PARSE);
            Add(0x83, 0xC4, 0x10);
            Add(0x85, 0xC0);
            int jnzParsed2 = code.Count;
            Add(0x75, 0x00);

            // Default filename
            Add(0xBA); AddU32(dataDefault);
            int jmpCreate = code.Count;
            Add(0xEB, 0x00);

            int useParsed = code.Count;
            code[jnzParsed1 + 1] = (byte)(useParsed - (jnzParsed1 + 2));
            code[jnzParsed2 + 1] = (byte)(useParsed - (jnzParsed2 + 2));
            Add(0xBA); AddU32(FILENAME);

            int createFile = code.Count;
            code[jmpCreate + 1] = (byte)(createFile - (jmpCreate + 2));

            Add(0x8B, 0x0D); AddU32(GFILEMANAGER);
            Add(0x6A, 0x00);       // push 0 (MaxFileSize)
            Add(0x6A, 0x00);       // push 0 (Error)
            Add(0x6A, 0x60);       // push 0x60 (Flags)
            Add(0x52);             // push edx
            Add(0x8B, 0x01);       // mov eax, [ecx]
            Add(0xFF, 0x50, 0x08); // call [eax+8]

            Add(0x8B, 0xE5);       // mov esp, ebp
            Add(0x5D);             // pop ebp

            Add(0x85, 0xC0);
            int jzFail = code.Count;
            Add(0x74, 0x00);
            Add(0xA3); AddU32(ARCHIVE);

            int noFile = code.Count;
            byte[] rel = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(rel, noFile - (jzSkipPos + 6));
            for (int i = 0; i < 4; i++) code[jzSkipPos + 2 + i] = rel[i];

            code[jzFail + 1] = (byte)(noFile - (jzFail + 2));

            EmitJmpNear(returnVa);

            return code.ToArray();
        }

        private static byte[] BuildCaveInit(uint baseVa, Dictionary<string, uint> sym,
            uint returnVa, uint archivePtrVa, uint defaultFilenameVa,
            byte[] overwrittenBytes, uint fileArchiveVa,
            uint logKeyVa, uint abslogKeyVa, uint filenameBufVa)
        {
            var code = new List<byte>();
            void Add(params byte[] b) => code.AddRange(b);
            void AddU32(uint v) { var buf = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buf, v); code.AddRange(buf); }
            uint Va() => baseVa + (uint)code.Count;
            void EmitCall(uint target) { uint v = Va(); Add(0xE8); code.AddRange(MachineCodeBuilder.Rel32Bytes(v, target)); }
            void EmitJmpNear(uint target) { uint v = Va(); Add(0xE9); code.AddRange(MachineCodeBuilder.Rel32Bytes(v, target)); }

            uint GFILEMANAGER = sym["GFileManager"];
            uint PARSE = sym.GetValueOrDefault("Parse");
            uint GCMDLINE = sym.GetValueOrDefault("GCmdLine");
            bool canParse = PARSE != 0 && GCMDLINE != 0 && logKeyVa != 0 && abslogKeyVa != 0 && filenameBufVa != 0;

            // Fast path - already initialised
            Add(0x83, 0x3D); AddU32(archivePtrVa); Add(0x00);
            int jneDone = code.Count;
            Add(0x75, 0x00);

            Add(0x50);         // push eax
            Add(0x51);         // push ecx
            Add(0x55);         // push ebp
            Add(0x8B, 0xEC);   // mov ebp, esp

            Add(0x8B, 0x0D); AddU32(GFILEMANAGER);
            Add(0x85, 0xC9);
            int jzSkip = code.Count;
            Add(0x74, 0x00);

            if (canParse)
            {
                Add(0x68); AddU32(0x400);
                Add(0x68); AddU32(filenameBufVa);
                Add(0x68); AddU32(logKeyVa);
                Add(0x68); AddU32(GCMDLINE);
                EmitCall(PARSE);
                Add(0x83, 0xC4, 0x10);
                Add(0x85, 0xC0);
                int jnzP1 = code.Count;
                Add(0x75, 0x00);

                Add(0x68); AddU32(0x400);
                Add(0x68); AddU32(filenameBufVa);
                Add(0x68); AddU32(abslogKeyVa);
                Add(0x68); AddU32(GCMDLINE);
                EmitCall(PARSE);
                Add(0x83, 0xC4, 0x10);
                Add(0x85, 0xC0);
                int jnzP2 = code.Count;
                Add(0x75, 0x00);

                // Default filename
                Add(0xBA); AddU32(defaultFilenameVa);
                int jmpCreate = code.Count;
                Add(0xEB, 0x00);

                int useParsed = code.Count;
                code[jnzP1 + 1] = (byte)(useParsed - (jnzP1 + 2));
                code[jnzP2 + 1] = (byte)(useParsed - (jnzP2 + 2));
                Add(0xBA); AddU32(filenameBufVa);

                int createOff = code.Count;
                code[jmpCreate + 1] = (byte)(createOff - (jmpCreate + 2));
            }
            else
            {
                Add(0xBA); AddU32(defaultFilenameVa);
            }

            // CreateFileWriter
            Add(0x8B, 0x0D); AddU32(GFILEMANAGER);
            Add(0x6A, 0x00);
            Add(0x6A, 0x00);
            Add(0x6A, 0x60);
            Add(0x52);
            Add(0x8B, 0x01);
            Add(0xFF, 0x50, 0x08);

            Add(0x8B, 0xE5);
            Add(0x5D);
            Add(0x85, 0xC0);
            int jzFail = code.Count;
            Add(0x74, 0x00);
            Add(0xA3); AddU32(archivePtrVa);
            if (fileArchiveVa != 0)
            {
                Add(0xA3); AddU32(fileArchiveVa);
            }

            int skip = code.Count;
            code[jzSkip + 1] = (byte)(skip - (jzSkip + 2));
            code[jzFail + 1] = (byte)(skip - (jzFail + 2));
            Add(0x59); // pop ecx
            Add(0x58); // pop eax

            int done = code.Count;
            code[jneDone + 1] = (byte)(done - (jneDone + 2));
            code.AddRange(overwrittenBytes);
            EmitJmpNear(returnVa);

            return code.ToArray();
        }

        private static byte[] BuildCaveUnified(uint baseVa, Dictionary<string, uint> sym,
            uint returnVa, uint archivePtrVa, uint filenameVa, uint fileArchiveVa)
        {
            var code = new List<byte>();
            void Add(params byte[] b) => code.AddRange(b);
            void AddU32(uint v) { var buf = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buf, v); code.AddRange(buf); }
            uint Va() => baseVa + (uint)code.Count;
            void EmitCall(uint target) { uint v = Va(); Add(0xE8); code.AddRange(MachineCodeBuilder.Rel32Bytes(v, target)); }
            void EmitJmpNear(uint target) { uint v = Va(); Add(0xE9); code.AddRange(MachineCodeBuilder.Rel32Bytes(v, target)); }

            uint LOGF = sym["Logf"];
            uint GLOG = sym["GLog"];
            uint GFILEMANAGER = sym["GFileManager"];
            uint FMT = sym["FmtPercentS"];

            // Replay overwritten instructions
            Add(0x8B, 0x44, 0x24, 0x14);   // mov eax, [esp+0x14]
            Add(0x3B, 0xC7);               // cmp eax, edi

            int jeSkipPos = code.Count;
            Add(0x0F, 0x84, 0x00, 0x00, 0x00, 0x00); // je .done (patched)

            Add(0x56);             // push esi
            Add(0x8B, 0xF0);       // mov esi, eax

            // Lazy init check
            Add(0x83, 0x3D); AddU32(archivePtrVa); Add(0x00);
            int jneHaveFile = code.Count;
            Add(0x75, 0x00);

            // Create file writer
            Add(0x8B, 0x0D); AddU32(GFILEMANAGER);
            Add(0x85, 0xC9);
            int jzSkipInit = code.Count;
            Add(0x74, 0x00);

            Add(0x55);
            Add(0x8B, 0xEC);
            Add(0x6A, 0x00);
            Add(0x6A, 0x00);
            Add(0x6A, 0x60);
            Add(0x68); AddU32(filenameVa);
            Add(0x8B, 0x01);
            Add(0xFF, 0x50, 0x08);
            Add(0x8B, 0xE5);
            Add(0x5D);
            Add(0x85, 0xC0);
            int jzCreateFail = code.Count;
            Add(0x74, 0x00);
            Add(0xA3); AddU32(archivePtrVa);
            if (fileArchiveVa != 0)
            {
                Add(0xA3); AddU32(fileArchiveVa);
            }

            int skipInit = code.Count;
            code[jzSkipInit + 1] = (byte)(skipInit - (jzSkipInit + 2));
            code[jzCreateFail + 1] = (byte)(skipInit - (jzCreateFail + 2));

            int haveFile = code.Count;
            code[jneHaveFile + 1] = (byte)(haveFile - (jneHaveFile + 2));

            // Logf(GLog, L"%s", message)
            Add(0x56);                               // push esi
            Add(0x68); AddU32(FMT);                  // push L"%s"
            Add(0xFF, 0x35); AddU32(GLOG);           // push [GLog]
            EmitCall(LOGF);
            Add(0x83, 0xC4, 0x0C);

            Add(0x5E); // pop esi

            // .done
            int done = code.Count;
            byte[] jeRel = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(jeRel, done - (jeSkipPos + 6));
            for (int i = 0; i < 4; i++) code[jeSkipPos + 2 + i] = jeRel[i];

            Add(0x8B, 0x44, 0x24, 0x14); // mov eax, [esp+0x14]
            Add(0x3B, 0xC7);             // cmp eax, edi

            EmitJmpNear(returnVa);

            return code.ToArray();
        }

        private static byte[] BuildCaveDataFull()
        {
            var data = new byte[0x40];
            byte[] log = Encoding.Unicode.GetBytes("LOG=\0");
            Buffer.BlockCopy(log, 0, data, 0, log.Length);
            byte[] abslog = Encoding.Unicode.GetBytes("ABSLOG=\0");
            Buffer.BlockCopy(abslog, 0, data, 0x0C, abslog.Length);
            byte[] def = Encoding.Unicode.GetBytes("Logs\\Launch.log\0");
            Buffer.BlockCopy(def, 0, data, 0x20, def.Length);
            return data;
        }

        private static byte[] BuildHookJmp(uint hookVa, uint targetVa, int totalSize)
        {
            byte[] hook = new byte[totalSize];
            hook[0] = 0xE9;
            byte[] rel = MachineCodeBuilder.Rel32Bytes(hookVa, targetVa);
            Buffer.BlockCopy(rel, 0, hook, 1, 4);
            for (int i = 5; i < totalSize; i++)
                hook[i] = 0x90;
            return hook;
        }

        private static void DecryptOoaInPlace(byte[] data) => PatchUtility.DecryptOoaInPlace(data);

        private static void WritePreservingAttributes(string path, byte[] content) => PatchUtility.WritePreservingAttributes(path, content);
    }
}
