using System.Buffers.Binary;
using System.IO;
using System.Text;

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

        // GCreateMalloc, at the same address in every build. It creates the allocator and stores it in
        // GMalloc, so it must only be called while GMalloc is still null.
        private const uint GCreateMalloc = 0x00404320;

        private static readonly string[] FullSymbols =
        {
            "Logf", "GLog", "GFileManager", "GCmdLine", "Parse",
            "FOutputDeviceFile.FileArchive", "FOutputDeviceFile.Filename", "FmtPercentS",
        };

        private static readonly string[] LazySymbols = { "Logf", "GLog", "GFileManager", "FmtPercentS" };

        // Symbols the three logging restorations need. When any is absent, or the open-check hook cannot
        // be located, logging still applies without the restorations.
        private static readonly string[] RestoreSymbols =
        {
            "Logf", "GLog", "GFileManager", "FmtPercentS", "GMalloc",
            "FFrame.Serialize", "FFrame.SerializeSlot", "FStateFrame.SerializeSlot",
            "FFrame.GetStackTrace", "UObject.GetFullName",
            "UObject.execScriptTrace", "UObject.execScriptTraceEntry",
        };

        private const int CaveDataSize = 0x40;           // BuildCaveDataFull
        private const int AiDataSize = 4 + CaveDataSize; // archive ptr + string data

        // On a fixed .text padding cave the restorations only apply when they leave this much room for the
        // other cave patches, so applying logging first never stops them fitting later. It covers the
        // high-res font hooks (~540), supersampling (~380) and the DLC set-command fix (~293) with margin.
        private const int RestorationHeadroom = 1350;

        // The script-warning line: the object's full name, the function's full name, then the message.
        private static readonly byte[] WarnFormat = Encoding.Unicode.GetBytes("%s (%s) %s\0");

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

            try
            {
                uint hookVa = VersionAddressTable.ResolveHook(data, execDef);
                int foff = (int)(hookVa - ImageBase);
                if (foff >= 0 && foff + execDef.Size <= data.Length &&
                    data.AsSpan(foff, execDef.Pattern.Length).SequenceEqual(execDef.Pattern))
                    return LoggingPatchState.Unpatched;
            }
            catch { }

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
            PatchUtility.OoaSession? ooa = isOoa ? PatchUtility.BeginOoa(data) : null;

            byte[]? patched = ApplyToImage(data, version, VersionAddressTable.Load(version));
            if (patched == null) return;

            if (isOoa)
                patched = PatchUtility.FinishOoa(patched, ooa!);

            WritePreservingAttributes(exePath, patched);
        }

        // Applies logging to an in-memory image, which must already be decrypted.
        // Returns null when execLog is already detoured, so a live cave block is never rewritten.
        internal static byte[]? ApplyToImage(byte[] data, string versionTag, VersionAddressTable addrs)
        {
            if (HasUnsupportedLeftovers(data, addrs))
                throw LegacyChangeDetector.ExeVerificationRequired();

            if (addrs.Symbols.Count == 0)
                throw new InvalidOperationException($"Address table for '{versionTag}' has no symbols.");

            var sym = addrs.Symbols;
            var hooks = addrs.Hooks;

            var execDef = hooks["execLog"];
            int execSite = FindHookSiteOff(data, execDef);
            if (execSite >= 0 && data[execSite] == 0xE9) return null;

            uint execHookVa = VersionAddressTable.ResolveHook(data, execDef);
            uint execReturnVa = execHookVa + (uint)execDef.ReturnOffset;

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

            var cave = CaveSection.Open(data, versionTag: versionTag);
            var ctx = BuildContext(data, addrs, useFullMode, execHookVa, execReturnVa, preInitVa);

            return useFullMode ? ApplyFullMode(data, cave, ctx) : ApplyAppInitMode(data, cave, ctx);
        }

        public static void RemovePatch(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);

            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null) return;

            bool isOoa = version == "ea";
            PatchUtility.OoaSession? ooa = null;
            if (isOoa)
            {
                try { ooa = PatchUtility.BeginOoa(data); }
                catch { return; }
            }

            VersionAddressTable addrs;
            try { addrs = VersionAddressTable.Load(version); }
            catch { return; }

            data = RemoveFromImage(data, addrs, out bool changed);
            if (!changed)
                return;

            if (isOoa && ooa != null)
                data = PatchUtility.FinishOoa(data, ooa);

            WritePreservingAttributes(exePath, data);
        }

        // Removes logging from an in-memory image, which must already be decrypted. It restores every
        // hook, then reclaims the cave block when nothing was allocated above it.
        // Sets changed to false when the image held no logging patch, so the caller can skip the rewrite.
        // On EA that rewrite would re-encrypt the image and strip its signature.
        internal static byte[] RemoveFromImage(byte[] data, VersionAddressTable addrs, out bool changed)
        {
            changed = false;

            if (HasUnsupportedLeftovers(data, addrs))
                throw LegacyChangeDetector.ExeVerificationRequired();

            if (!addrs.Hooks.TryGetValue("execLog", out var execDef))
                return data;

            bool fullMode = addrs.Hooks.ContainsKey("preInit_fileLog");

            // execLog's detour target is the cave base (FullMode) or AiDataSize above it (AppInitMode).
            // Capture it before restoring the hook, which overwrites the detour.
            uint? execTarget = FindDetourTarget(data, execDef);

            // The restorations share the logging toggle, so remove them here. Detect which were applied
            // from the live hooks, so the reclaim footprint matches whatever ApplyPatch allocated.
            addrs.Hooks.TryGetValue("fileSerialize_openCheck", out var openDef);
            bool restorationsPresent = RestorePresent(data, addrs.Symbols);
            changed = execTarget != null || restorationsPresent;
            RestoreRestorations(data, addrs.Symbols, openDef);

            RestoreHook(data, execDef);
            if (fullMode && addrs.Hooks.TryGetValue("preInit_fileLog", out var preInitDef))
                RestoreHook(data, preInitDef);

            // Only block lengths matter for the reclaim footprint, so the hook and return VAs are not needed.
            var ctx = BuildContext(data, addrs, fullMode, 0, 0, 0);

            uint? caveBase = null;
            int footprint = 0;
            if (fullMode)
            {
                if (execTarget != null)
                {
                    caveBase = execTarget;
                    footprint = BuildLayout(execTarget.Value, ctx, appInitPresent: false, restorationsPresent).Footprint;
                }
            }
            else
            {
                // AppInitMode: restore the dynamically located app-init hook (its saved original bytes
                // are recovered from the init cave block). The cave's data block sits AiDataSize below
                // the lowest detour (the app-init init block or the unified cave if no app-init hook).
                // If the hook is present but unrecoverable, skip the reclaim,
                // so a region a live detour still jumps to is never zeroed.
                AppInitRestore st = RestoreAppInit(data, addrs.Symbols, out uint initVa);
                changed |= st == AppInitRestore.Restored;
                if (st != AppInitRestore.Unrestored && execTarget != null)
                {
                    bool initPresent = st == AppInitRestore.Restored;
                    uint lowest = initPresent ? initVa : execTarget.Value;
                    caveBase = lowest - AiDataSize;
                    footprint = BuildLayout(caveBase.Value, ctx, initPresent, restorationsPresent).Footprint;
                }
            }

            if (caveBase != null)
                data = CaveSection.ReclaimIfTopmost(data, caveBase.Value, footprint);

            return data;
        }

        // Returns whether a 4.4 release left app-init logging code that ApplyPatch and RemovePatch cannot manage.
        // That release's RemovePatch left the app-init detour live,
        // and its init block opens with a short jne that overflows.
        internal static bool HasUnsupportedLeftovers(byte[] data, string version)
        {
            VersionAddressTable addrs;
            try { addrs = VersionAddressTable.Load(version); }
            catch { return false; }
            return HasUnsupportedLeftovers(data, addrs);
        }

        private static bool HasUnsupportedLeftovers(byte[] data, VersionAddressTable addrs)
        {
            if (addrs.Hooks.ContainsKey("preInit_fileLog") || !addrs.Hooks.TryGetValue("execLog", out var execDef))
                return false;

            uint? siteVa = FindAppInitSite(data, addrs.Symbols);
            if (siteVa == null) return false;
            int site = (int)(siteVa.Value - ImageBase);
            if (site < 0 || site + 5 > data.Length || data[site] != 0xE9) return false;

            try
            {
                VersionAddressTable.ResolveHook(data, execDef);
                return true;
            }
            catch { }

            uint initVa = siteVa.Value + 5u + (uint)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(site + 1, 4));
            int initOff = (int)(initVa - ImageBase);
            return initOff >= 0 && initOff + 8 <= data.Length
                && data[initOff] == 0x83 && data[initOff + 1] == 0x3D && data[initOff + 7] == 0x75;
        }

        // Inputs the layout builder needs, resolved once from the address table and the live image.
        private sealed class CaveContext
        {
            public bool FullMode;
            public Dictionary<string, uint> Sym = new();
            public uint ExecHookVa;                   // execLog hook site
            public uint PreInitHookVa;                // FullMode preInit hook site
            public uint ExecReturnVa;
            public uint FileReturnVa;                 // FullMode preInit return
            public byte[] PreInitPattern = Array.Empty<byte>();
            public byte[] OverwrittenBytes = new byte[5]; // AppInit app-init site bytes
            public uint InitReturnVa;                 // AppInit app-init return
            public bool CanRestore;
            public uint OpenCheckSiteVa;              // FOutputDeviceFile open-check
            public uint JeTargetVa;                   // open-check skip target
            public uint ContinueVa;                   // open-check resume (site + 9)
        }

        private static CaveContext BuildContext(byte[] data, VersionAddressTable addrs,
            bool fullMode, uint execHookVa, uint execReturnVa, uint preInitVa)
        {
            var ctx = new CaveContext
            {
                FullMode = fullMode,
                Sym = addrs.Symbols,
                ExecHookVa = execHookVa,
                PreInitHookVa = preInitVa,
                ExecReturnVa = execReturnVa,
            };

            if (fullMode && addrs.Hooks.TryGetValue("preInit_fileLog", out var preInitDef))
            {
                ctx.PreInitPattern = preInitDef.Pattern;
                ctx.FileReturnVa = preInitVa + (uint)preInitDef.ReturnOffset;
            }

            if (!fullMode)
            {
                uint? appInitHookVa = FindAppInitSite(data, addrs.Symbols);
                if (appInitHookVa.HasValue)
                {
                    int hookOff = (int)(appInitHookVa.Value - ImageBase);
                    // The site is only stock (readable) when execLog is not detoured. During Remove it may
                    // already be a detour, but the init block's length does not depend on these bytes.
                    if (hookOff >= 0 && hookOff + 5 <= data.Length && data[hookOff] != 0xE9)
                        Buffer.BlockCopy(data, hookOff, ctx.OverwrittenBytes, 0, 5);
                    ctx.InitReturnVa = appInitHookVa.Value + 5;
                }
            }

            ctx.CanRestore = ResolveRestoreTargets(data, addrs, out uint siteVa, out uint jeTargetVa, out uint continueVa);
            ctx.OpenCheckSiteVa = siteVa;
            ctx.JeTargetVa = jeTargetVa;
            ctx.ContinueVa = continueVa;
            return ctx;
        }

        // Resolves the open-check hook site into the resume and skip targets the lazy-open block jumps to,
        // and reports whether the restorations can apply (all symbols present and the hook located).
        private static bool ResolveRestoreTargets(byte[] data, VersionAddressTable addrs,
            out uint siteVa, out uint jeTargetVa, out uint continueVa)
        {
            siteVa = 0;
            jeTargetVa = 0;
            continueVa = 0;

            foreach (string s in RestoreSymbols)
                if (!addrs.Symbols.ContainsKey(s)) return false;
            if (!addrs.Hooks.TryGetValue("fileSerialize_openCheck", out var openDef))
                return false;

            try { siteVa = VersionAddressTable.ResolveHook(data, openDef); }
            catch { return false; }

            // The 9 bytes are `cmp [edx+4], ebx` (3) then `je rel32` (6). The resume point is just past
            // them, and the skip target is the je's destination.
            int off = (int)(siteVa - ImageBase);
            int rel = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 5, 4));
            continueVa = siteVa + 9;
            jeTargetVa = siteVa + 9 + (uint)rel;
            return true;
        }

        private static byte[] ApplyFullMode(byte[] data, CaveSection cave, CaveContext ctx)
        {
            Place(cave, ctx, appInitPresent: false, out uint baseVa, out bool includeRest);
            CaveLayout layout = BuildLayout(baseVa, ctx, appInitPresent: false, includeRest);
            foreach (var (off, code) in layout.Blocks)
                cave.Write(baseVa + (uint)off, code);

            data = cave.Finalize();

            byte[] hook1 = BuildHookJmp(ctx.ExecHookVa, layout.ExecVa, 6);
            int off1 = (int)(ctx.ExecHookVa - ImageBase);
            Buffer.BlockCopy(hook1, 0, data, off1, hook1.Length);

            byte[] hook2 = BuildHookJmp(ctx.PreInitHookVa, layout.PreInitVa, 6);
            int off2 = (int)(ctx.PreInitHookVa - ImageBase);
            Buffer.BlockCopy(hook2, 0, data, off2, hook2.Length);

            if (includeRest)
                WriteRestorationHooks(data, ctx, layout);

            return data;
        }

        private static byte[] ApplyAppInitMode(byte[] data, CaveSection cave, CaveContext ctx)
        {
            uint? appInitHookVa = FindAppInitSite(data, ctx.Sym);
            bool appInitPresent = appInitHookVa.HasValue;

            Place(cave, ctx, appInitPresent, out uint baseVa, out bool includeRest);
            CaveLayout layout = BuildLayout(baseVa, ctx, appInitPresent, includeRest);
            foreach (var (off, code) in layout.Blocks)
                cave.Write(baseVa + (uint)off, code);

            data = cave.Finalize();

            if (appInitPresent)
            {
                int hookOff = (int)(appInitHookVa!.Value - ImageBase);
                byte[] initHook = new byte[5];
                initHook[0] = 0xE9;
                Buffer.BlockCopy(MachineCodeBuilder.Rel32Bytes(appInitHookVa.Value, layout.InitVa), 0, initHook, 1, 4);
                Buffer.BlockCopy(initHook, 0, data, hookOff, 5);
            }

            byte[] execHook = BuildHookJmp(ctx.ExecHookVa, layout.ExecVa, 6);
            int execOff = (int)(ctx.ExecHookVa - ImageBase);
            Buffer.BlockCopy(execHook, 0, data, execOff, execHook.Length);

            if (includeRest)
                WriteRestorationHooks(data, ctx, layout);

            return data;
        }

        // Chooses the cave base. It reuses a leftover block RemovePatch could not reclaim,
        // or allocates a new block.
        // Whether the restorations are included depends only on the build's cave capacity,
        // so every apply builds the same layout and the leftover search can match it byte for byte.
        private static void Place(CaveSection cave, CaveContext ctx, bool appInitPresent,
            out uint baseVa, out bool includeRest)
        {
            includeRest = IncludeRestorations(cave, ctx, appInitPresent);
            int footprint = BuildLayout(cave.SectionVa, ctx, appInitPresent, includeRest).Footprint;
            int stringsOffset = ctx.FullMode ? FullStringsOffset(ctx) : 4;

            bool rest = includeRest;
            int? leftover = FindLeftoverBlock(cave, footprint, stringsOffset,
                va => BuildLayout(va, ctx, appInitPresent, rest).Blocks);

            baseVa = leftover is { } off ? cave.SectionVa + (uint)off : cave.Alloc(footprint);
        }

        // A growable .cave section always takes the restorations. A fixed .text padding cave takes them
        // only when they leave RestorationHeadroom free for the other cave patches.
        private static bool IncludeRestorations(CaveSection cave, CaveContext ctx, bool appInitPresent)
        {
            if (!ctx.CanRestore) return false;
            int capacity = cave.Capacity;
            if (capacity == int.MaxValue) return true;
            int full = BuildLayout(cave.SectionVa, ctx, appInitPresent, restorations: true).Footprint;
            return full + RestorationHeadroom <= capacity;
        }

        // The FullMode strings block sits directly after the execLog forwarder, so its offset is that
        // block's length. It anchors the leftover search.
        private static int FullStringsOffset(CaveContext ctx)
        {
            byte[] p1 = BuildCavePatch1(0, ctx.Sym, ctx.ExecReturnVa);
            return Align4(p1.Length);
        }

        private static int Align4(int v) => (v + 3) & ~3;

        private sealed class CaveLayout
        {
            public List<(int Offset, byte[] Code)> Blocks = new();
            public int Footprint;
            public uint ExecVa;       // execLog forwarder (FullMode p1 / AppInit unified)
            public uint PreInitVa;    // FullMode file-open block
            public uint InitVa;       // AppInit app-init block
            public uint WarnVa;
            public uint ScriptTraceVa;
            public uint LazyOpenVa;
        }

        // Single source of the cave layout for a mode: the same (offset, code) blocks are used to write,
        // to verify a leftover byte for byte, and to size the reclaim footprint. Block code depends only
        // on absolute symbols and return addresses, so rebuilding at the same base is deterministic.
        private static CaveLayout BuildLayout(uint baseVa, CaveContext ctx, bool appInitPresent, bool restorations)
        {
            var layout = new CaveLayout();
            int off = 0;
            void Add(byte[] code, Action<uint> setVa)
            {
                setVa(baseVa + (uint)off);
                layout.Blocks.Add((off, code));
                off = Align4(off + code.Length);
            }

            if (ctx.FullMode)
            {
                Add(BuildCavePatch1(baseVa + (uint)off, ctx.Sym, ctx.ExecReturnVa), va => layout.ExecVa = va);

                uint strVa = 0;
                Add(BuildCaveDataFull(), va => strVa = va);

                Add(BuildCavePatch2(baseVa + (uint)off, ctx.Sym, ctx.FileReturnVa,
                        strVa, strVa + 0x0C, strVa + 0x20, ctx.PreInitPattern),
                    va => layout.PreInitVa = va);
            }
            else
            {
                uint archivePtrVa = 0, strVa = 0;
                byte[] dataBlock = new byte[AiDataSize];
                Buffer.BlockCopy(BuildCaveDataFull(), 0, dataBlock, 4, CaveDataSize);
                Add(dataBlock, va => { archivePtrVa = va; strVa = va + 4; });

                uint fileArchiveVa = ctx.Sym.GetValueOrDefault("FOutputDeviceFile.FileArchive");
                uint filenameBufVa = ctx.Sym.GetValueOrDefault("FOutputDeviceFile.Filename");
                uint defaultFilenameVa = strVa + 0x20;

                if (appInitPresent)
                    Add(BuildCaveInit(baseVa + (uint)off, ctx.Sym, ctx.InitReturnVa,
                            archivePtrVa, defaultFilenameVa, ctx.OverwrittenBytes,
                            fileArchiveVa, strVa, strVa + 0x0C, filenameBufVa),
                        va => layout.InitVa = va);

                Add(BuildCaveUnified(baseVa + (uint)off, ctx.Sym, ctx.ExecReturnVa,
                        archivePtrVa, defaultFilenameVa, fileArchiveVa),
                    va => layout.ExecVa = va);
            }

            if (restorations)
            {
                uint warnFmtVa = 0;
                Add((byte[])WarnFormat.Clone(), va => warnFmtVa = va);
                Add(BuildWarnForwarder(baseVa + (uint)off, ctx.Sym, warnFmtVa), va => layout.WarnVa = va);
                Add(BuildScriptTrace(baseVa + (uint)off, ctx.Sym), va => layout.ScriptTraceVa = va);
                Add(BuildLazyOpen(baseVa + (uint)off, ctx.Sym, ctx.JeTargetVa, ctx.ContinueVa),
                    va => layout.LazyOpenVa = va);
            }

            layout.Footprint = off;
            return layout;
        }

        // Returns the section offset of a block that RemovePatch could not reclaim.
        // Returns null unless the block matches what this apply would write there, byte for byte.
        internal static int? FindLeftoverBlock(CaveSection cave, int footprint, int stringsOffset,
            Func<uint, List<(int Offset, byte[] Code)>> build)
        {
            byte[] anchor = BuildCaveDataFull();
            int search = 0;
            while (true)
            {
                int hit = cave.IndexOf(anchor, search);
                if (hit < 0) return null;
                search = hit + 1;

                int baseOff = hit - stringsOffset;
                if (baseOff < 0 || baseOff + footprint > cave.CurrentWatermark)
                    continue;

                uint baseVa = cave.SectionVa + (uint)baseOff;
                bool allMatch = true;
                foreach (var (off, code) in build(baseVa))
                {
                    if (cave.IndexOf(code, baseOff + off) != baseOff + off) { allMatch = false; break; }
                }
                if (allMatch) return baseOff;
            }
        }

        private static void WriteRestorationHooks(byte[] data, CaveContext ctx, CaveLayout layout)
            => WriteRestorationHooks(data, ctx.Sym, ctx.OpenCheckSiteVa,
                layout.WarnVa, layout.ScriptTraceVa, layout.LazyOpenVa);

        // Writes the three restoration hooks: the FFrame and FStateFrame Serialize vtable slots point at
        // the warning forwarder, the execScriptTrace native entry at the trace block, and the
        // FOutputDeviceFile open-check detours to the lazy-open block.
        internal static void WriteRestorationHooks(byte[] data, Dictionary<string, uint> sym, uint openSiteVa,
            uint warnVa, uint scriptTraceVa, uint lazyOpenVa)
        {
            WriteU32(data, sym["FFrame.SerializeSlot"], warnVa);
            WriteU32(data, sym["FStateFrame.SerializeSlot"], warnVa);

            uint entry = sym["UObject.execScriptTraceEntry"];
            if (ReadU32(data, entry + 4) == sym["UObject.execScriptTrace"])
                WriteU32(data, entry + 4, scriptTraceVa);

            byte[] openHook = BuildHookJmp(openSiteVa, lazyOpenVa, 9);
            Buffer.BlockCopy(openHook, 0, data, (int)(openSiteVa - ImageBase), openHook.Length);
        }

        // Reports whether the restorations are currently applied, from the FFrame Serialize slot. Used by
        // RemovePatch to pick the reclaim footprint before it restores the slot to the stub.
        internal static bool RestorePresent(byte[] data, Dictionary<string, uint> sym)
        {
            if (!sym.TryGetValue("FFrame.SerializeSlot", out uint slot)
                || !sym.TryGetValue("FFrame.Serialize", out uint stub))
                return false;
            int off = (int)(slot - ImageBase);
            if (off < 0 || off + 4 > data.Length) return false;
            return ReadU32(data, slot) != stub;
        }

        // Puts back every restoration hook that is live: the two Serialize slots, the native entry and
        // the open-check bytes. Each is guarded on its own, so a partially patched image is still cleaned.
        internal static void RestoreRestorations(byte[] data, Dictionary<string, uint> sym, HookDefinition? openDef)
        {
            if (sym.TryGetValue("FFrame.Serialize", out uint stub))
            {
                if (sym.TryGetValue("FFrame.SerializeSlot", out uint s1) && ReadU32(data, s1) != stub)
                    WriteU32(data, s1, stub);
                if (sym.TryGetValue("FStateFrame.SerializeSlot", out uint s2) && ReadU32(data, s2) != stub)
                    WriteU32(data, s2, stub);
            }

            if (sym.TryGetValue("UObject.execScriptTraceEntry", out uint entry)
                && sym.TryGetValue("UObject.execScriptTrace", out uint fn)
                && ReadU32(data, entry + 4) != fn)
                WriteU32(data, entry + 4, fn);

            if (openDef != null)
            {
                int siteOff = FindHookSiteOff(data, openDef);
                if (siteOff >= 0 && siteOff + openDef.Size <= data.Length && data[siteOff] == 0xE9)
                    Buffer.BlockCopy(openDef.Pattern, 0, data, siteOff, openDef.Pattern.Length);
            }
        }

        private enum AppInitRestore { NotPresent, Restored, Unrestored }

        // Restores the AppInitMode app-init hook. Its original 5 bytes were replayed in the init cave
        // block right before that block's single E9 return jump (back to site+5), so we locate that E9
        // by its return target and copy the preceding 5 bytes back over the detour.
        // Returns whether the hook was absent, restored, or present but unrecoverable.
        // Sets initVa to the init block when the hook is present, for the caller's cave-base calculation.
        private static AppInitRestore RestoreAppInit(byte[] data, Dictionary<string, uint> sym, out uint initVa)
        {
            initVa = 0;
            uint? siteVa = FindAppInitSite(data, sym);
            if (siteVa == null) return AppInitRestore.NotPresent;

            int site = (int)(siteVa.Value - ImageBase);
            if (site < 0 || site + 5 > data.Length || data[site] != 0xE9)
                return AppInitRestore.NotPresent;

            initVa = siteVa.Value + 5u + (uint)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(site + 1, 4));
            int initOff = (int)(initVa - ImageBase);
            if (initOff < 0 || initOff >= data.Length) return AppInitRestore.Unrestored;
            uint returnTarget = siteVa.Value + 5u;

            int scanEnd = Math.Min(initOff + 0x100, data.Length - 5);
            for (int i = initOff; i <= scanEnd; i++)
            {
                if (data[i] != 0xE9) continue;
                uint tgt = ImageBase + (uint)i + 5u + (uint)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i + 1, 4));
                if (tgt == returnTarget && i - 5 >= initOff)
                {
                    Buffer.BlockCopy(data, i - 5, data, site, 5);
                    return AppInitRestore.Restored;
                }
            }
            return AppInitRestore.Unrestored;
        }

        private static uint? FindDetourTarget(byte[] data, HookDefinition hdef)
        {
            int foff = FindHookSiteOff(data, hdef);
            if (foff < 0 || foff + 5 > data.Length || data[foff] != 0xE9) return null;
            return ImageBase + (uint)foff + 5u + (uint)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(foff + 1, 4));
        }

        // Hook site file offset, whether unpatched (find the original pattern) or patched (find the
        // E9 + NOP-pad detour in the scan window)
        private static int FindHookSiteOff(byte[] data, HookDefinition hdef)
        {
            try { return (int)(VersionAddressTable.ResolveHook(data, hdef) - ImageBase); }
            catch { }

            int start = (int)(hdef.ScanStart - ImageBase);
            int end = start + hdef.ScanSize - hdef.Size;
            for (int i = Math.Max(0, start); i <= end && i + hdef.Size <= data.Length; i++)
            {
                if (data[i] != 0xE9) continue;
                bool nops = true;
                for (int k = 5; k < hdef.Size; k++)
                    if (data[i + k] != 0x90) { nops = false; break; }
                if (nops) return i;
            }
            return -1;
        }

        private static void RestoreHook(byte[] data, HookDefinition hdef)
        {
            int hookFoff = FindHookSiteOff(data, hdef);
            if (hookFoff < 0 || hookFoff + hdef.Size > data.Length)
                return;

            if (data[hookFoff] == 0xE9)
            {
                Buffer.BlockCopy(hdef.Pattern, 0, data, hookFoff, hdef.Pattern.Length);
                for (int i = hdef.Pattern.Length; i < hdef.Size; i++)
                    data[hookFoff + i] = 0x90;
            }
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

            // push esi; call edx; then the site instruction: push imm32 (0x68, unpatched) or the E9
            // detour (patched) -  matching both lets Remove locate the site after it has been hooked
            byte[] pat = { 0x56, 0xFF, 0xD2 };
            int idx = 0;
            while (idx <= scanSize - 8)
            {
                int pos = -1;
                for (int i = startOff + idx; i <= startOff + scanSize - 4; i++)
                {
                    bool match = data[i] == pat[0] && data[i + 1] == pat[1] && data[i + 2] == pat[2]
                                 && (data[i + 3] == 0x68 || data[i + 3] == 0xE9);
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

        internal static byte[] BuildCavePatch1(uint baseVa, Dictionary<string, uint> sym, uint returnVa)
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

        internal static byte[] BuildCavePatch2(uint baseVa, Dictionary<string, uint> sym,
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

        internal static byte[] BuildCaveInit(uint baseVa, Dictionary<string, uint> sym,
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
            // rel32 jne: with Parse + FileArchive present the init body exceeds the +127 reach of a rel8.
            Add(0x0F, 0x85, 0x00, 0x00, 0x00, 0x00);

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
            byte[] doneRel = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(doneRel, done - (jneDone + 6));
            for (int i = 0; i < 4; i++) code[jneDone + 2 + i] = doneRel[i];
            code.AddRange(overwrittenBytes);
            EmitJmpNear(returnVa);

            return code.ToArray();
        }

        internal static byte[] BuildCaveUnified(uint baseVa, Dictionary<string, uint> sym,
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

            int done = code.Count;
            byte[] jeRel = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(jeRel, done - (jeSkipPos + 6));
            for (int i = 0; i < 4; i++) code[jeSkipPos + 2 + i] = jeRel[i];

            Add(0x8B, 0x44, 0x24, 0x14); // mov eax, [esp+0x14]
            Add(0x3B, 0xC7);             // cmp eax, edi

            EmitJmpNear(returnVa);

            return code.ToArray();
        }

        // Script-warning forwarder for the FFrame and FStateFrame Serialize vtable slots: a thiscall
        // `Serialize(const TCHAR* V, EName Event)` (ret 8) on the frame, whose Node is at +4 and Object at +8.
        // It logs the object's and function's full names and the message through GLog, which reaches the
        // log file, then frees both GetFullName strings through GMalloc.
        internal static byte[] BuildWarnForwarder(uint baseVa, Dictionary<string, uint> sym, uint warnFmtVa)
        {
            uint GETFULLNAME = sym["UObject.GetFullName"];
            uint LOGF = sym["Logf"];
            uint GLOG = sym["GLog"];
            uint GMALLOC = sym["GMalloc"];

            var mc = new MachineCodeBuilder(baseVa);
            mc.Emit(new byte[] { 0x55 });                       // push ebp
            mc.Emit(new byte[] { 0x8B, 0xEC });                 // mov ebp, esp
            mc.Emit(new byte[] { 0x83, 0xEC, 0x18 });           // sub esp, 0x18   (objStr @ ebp-0x0C, funcStr @ ebp-0x18)
            mc.Emit(new byte[] { 0x53 });                       // push ebx
            mc.Emit(new byte[] { 0x56 });                       // push esi
            mc.Emit(new byte[] { 0x8B, 0xF1 });                 // mov esi, ecx    (this = frame)

            // objStr = Object->GetFullName(NULL)
            mc.Emit(new byte[] { 0x6A, 0x00 });                 // push 0 (StopOuter)
            mc.Emit(new byte[] { 0x8D, 0x45, 0xF4 });           // lea eax, [ebp-0x0C]
            mc.Emit(new byte[] { 0x50 });                       // push eax (&objStr)
            mc.Emit(new byte[] { 0x8B, 0x4E, 0x08 });           // mov ecx, [esi+8] (Object)
            mc.EmitCall(GETFULLNAME);

            // funcStr = Node->GetFullName(NULL)
            mc.Emit(new byte[] { 0x6A, 0x00 });                 // push 0
            mc.Emit(new byte[] { 0x8D, 0x45, 0xE8 });           // lea eax, [ebp-0x18]
            mc.Emit(new byte[] { 0x50 });                       // push eax (&funcStr)
            mc.Emit(new byte[] { 0x8B, 0x4E, 0x04 });           // mov ecx, [esi+4] (Node)
            mc.EmitCall(GETFULLNAME);

            // Logf(GLog, warnFmt, objStr.Data, funcStr.Data, V)
            mc.Emit(new byte[] { 0xFF, 0x75, 0x08 });           // push [ebp+8] (V)
            mc.Emit(new byte[] { 0xFF, 0x75, 0xE8 });           // push [ebp-0x18] (funcStr.Data)
            mc.Emit(new byte[] { 0xFF, 0x75, 0xF4 });           // push [ebp-0x0C] (objStr.Data)
            mc.Emit(new byte[] { 0x68 }); mc.EmitUInt32(warnFmtVa);
            mc.Emit(new byte[] { 0xFF, 0x35 }); mc.EmitUInt32(GLOG);
            mc.EmitCall(LOGF);
            mc.Emit(new byte[] { 0x83, 0xC4, 0x14 });           // add esp, 0x14

            // ebx = GMalloc, creating it first if it is still null
            mc.Emit(new byte[] { 0x8B, 0x1D }); mc.EmitUInt32(GMALLOC);
            mc.Emit(new byte[] { 0x85, 0xDB });                 // test ebx, ebx
            mc.EmitJnz("haveMalloc");
            mc.EmitCall(GCreateMalloc);
            mc.Emit(new byte[] { 0x8B, 0x1D }); mc.EmitUInt32(GMALLOC);
            mc.MarkLabel("haveMalloc");

            // GMalloc->Free(objStr.Data)
            mc.Emit(new byte[] { 0x8B, 0x03 });                 // mov eax, [ebx]
            mc.Emit(new byte[] { 0x8B, 0x40, 0x0C });           // mov eax, [eax+0x0C]
            mc.Emit(new byte[] { 0xFF, 0x75, 0xF4 });           // push [ebp-0x0C]
            mc.Emit(new byte[] { 0x8B, 0xCB });                 // mov ecx, ebx
            mc.Emit(new byte[] { 0xFF, 0xD0 });                 // call eax
            // GMalloc->Free(funcStr.Data)
            mc.Emit(new byte[] { 0x8B, 0x03 });
            mc.Emit(new byte[] { 0x8B, 0x40, 0x0C });
            mc.Emit(new byte[] { 0xFF, 0x75, 0xE8 });
            mc.Emit(new byte[] { 0x8B, 0xCB });
            mc.Emit(new byte[] { 0xFF, 0xD0 });

            mc.Emit(new byte[] { 0x5E });                       // pop esi
            mc.Emit(new byte[] { 0x5B });                       // pop ebx
            mc.Emit(new byte[] { 0x8B, 0xE5 });                 // mov esp, ebp
            mc.Emit(new byte[] { 0x5D });                       // pop ebp
            mc.Emit(new byte[] { 0xC2, 0x08, 0x00 });           // ret 8
            return mc.Build();
        }

        // ScriptTrace() logger for the execScriptTrace native entry: a native thiscall
        // `void(FFrame& Stack, RESULT_DECL)` (ret 8). It first calls the stock native so the frame still
        // advances past the opcode, then logs FFrame::GetStackTrace through GLog and frees the returned
        // string through GMalloc.
        internal static byte[] BuildScriptTrace(uint baseVa, Dictionary<string, uint> sym)
        {
            uint ORIG = sym["UObject.execScriptTrace"];
            uint GETSTACKTRACE = sym["FFrame.GetStackTrace"];
            uint LOGF = sym["Logf"];
            uint GLOG = sym["GLog"];
            uint FMT = sym["FmtPercentS"];
            uint GMALLOC = sym["GMalloc"];

            var mc = new MachineCodeBuilder(baseVa);
            mc.Emit(new byte[] { 0x55 });                       // push ebp
            mc.Emit(new byte[] { 0x8B, 0xEC });                 // mov ebp, esp
            mc.Emit(new byte[] { 0x83, 0xEC, 0x0C });           // sub esp, 0x0C  (traceStr @ ebp-0x0C)

            // original execScriptTrace(Context=ecx, Stack=[ebp+8], Result=[ebp+0x0C])
            mc.Emit(new byte[] { 0xFF, 0x75, 0x0C });           // push [ebp+0x0C] (Result)
            mc.Emit(new byte[] { 0xFF, 0x75, 0x08 });           // push [ebp+8]    (&Stack)
            mc.EmitCall(ORIG);                                  // ret 8 (ecx still Context)

            // traceStr = Stack.GetStackTrace()
            mc.Emit(new byte[] { 0x8D, 0x45, 0xF4 });           // lea eax, [ebp-0x0C]
            mc.Emit(new byte[] { 0x50 });                       // push eax (&traceStr)
            mc.Emit(new byte[] { 0x8B, 0x4D, 0x08 });           // mov ecx, [ebp+8] (&Stack)
            mc.EmitCall(GETSTACKTRACE);                         // ret 4

            // Logf(GLog, "%s", traceStr.Data)
            mc.Emit(new byte[] { 0xFF, 0x75, 0xF4 });           // push [ebp-0x0C]
            mc.Emit(new byte[] { 0x68 }); mc.EmitUInt32(FMT);
            mc.Emit(new byte[] { 0xFF, 0x35 }); mc.EmitUInt32(GLOG);
            mc.EmitCall(LOGF);
            mc.Emit(new byte[] { 0x83, 0xC4, 0x0C });           // add esp, 0x0C

            // GMalloc->Free(traceStr.Data)
            mc.Emit(new byte[] { 0x8B, 0x0D }); mc.EmitUInt32(GMALLOC);
            mc.Emit(new byte[] { 0x85, 0xC9 });                 // test ecx, ecx
            mc.EmitJnz("haveMalloc");
            mc.EmitCall(GCreateMalloc);
            mc.Emit(new byte[] { 0x8B, 0x0D }); mc.EmitUInt32(GMALLOC);
            mc.MarkLabel("haveMalloc");
            mc.Emit(new byte[] { 0x8B, 0x01 });                 // mov eax, [ecx]
            mc.Emit(new byte[] { 0x8B, 0x40, 0x0C });           // mov eax, [eax+0x0C]
            mc.Emit(new byte[] { 0xFF, 0x75, 0xF4 });           // push [ebp-0x0C]
            mc.Emit(new byte[] { 0xFF, 0xD0 });                 // call eax (ecx = GMalloc)

            mc.Emit(new byte[] { 0x8B, 0xE5 });                 // mov esp, ebp
            mc.Emit(new byte[] { 0x5D });                       // pop ebp
            mc.Emit(new byte[] { 0xC2, 0x08, 0x00 });           // ret 8
            return mc.Build();
        }

        // Lazy file-open block, entered from the detoured FOutputDeviceFile::Serialize null-archive check.
        // At the site edx is the device and ebx is zero.
        // When the archive is null, the filename is set, and the dead flag is clear,
        // it opens the file with GFileManager->CreateFileWriter(Filename, 0x60) and resumes the stock body.
        // A failed open sets the dead flag.
        // Every other case keeps stock behaviour.
        // Layout: LogAr at +4, Filename at +8, Opened at +0x808, Dead at +0x80C.
        internal static byte[] BuildLazyOpen(uint baseVa, Dictionary<string, uint> sym,
            uint jeTargetVa, uint continueVa)
        {
            uint GFILEMANAGER = sym["GFileManager"];

            var mc = new MachineCodeBuilder(baseVa);
            mc.Emit(new byte[] { 0x39, 0x5A, 0x04 });           // cmp [edx+4], ebx   (LogAr == 0 ?)
            mc.EmitJnz("cont");                                 // archive present -> resume stock body
            mc.Emit(new byte[] { 0x39, 0x9A, 0x0C, 0x08, 0x00, 0x00 }); // cmp [edx+0x80C], ebx (Dead ?)
            mc.EmitJnz("skip");
            mc.Emit(new byte[] { 0x66, 0x39, 0x5A, 0x08 });     // cmp word [edx+8], bx (Filename[0] == 0 ?)
            mc.EmitJz("skip");
            mc.Emit(new byte[] { 0x8B, 0x0D }); mc.EmitUInt32(GFILEMANAGER); // mov ecx, [GFileManager]
            mc.Emit(new byte[] { 0x85, 0xC9 });                 // test ecx, ecx
            mc.EmitJz("skip");

            mc.Emit(new byte[] { 0x52 });                       // push edx (save this)
            mc.Emit(new byte[] { 0x8D, 0x42, 0x08 });           // lea eax, [edx+8] (&Filename)
            mc.Emit(new byte[] { 0x6A, 0x00 });                 // push 0 (MaxFileSize)
            mc.Emit(new byte[] { 0x6A, 0x00 });                 // push 0 (Error)
            mc.Emit(new byte[] { 0x6A, 0x60 });                 // push 0x60 (Flags)
            mc.Emit(new byte[] { 0x50 });                       // push eax (Filename)
            mc.Emit(new byte[] { 0x8B, 0x01 });                 // mov eax, [ecx]
            mc.Emit(new byte[] { 0xFF, 0x50, 0x08 });           // call [eax+8] (CreateFileWriter)
            mc.Emit(new byte[] { 0x5A });                       // pop edx (restore this)
            mc.Emit(new byte[] { 0x85, 0xC0 });                 // test eax, eax
            mc.EmitJz("dead");
            mc.Emit(new byte[] { 0x89, 0x42, 0x04 });           // mov [edx+4], eax (LogAr)
            mc.Emit(new byte[] { 0xC7, 0x82, 0x08, 0x08, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 }); // mov [edx+0x808], 1 (Opened)

            mc.MarkLabel("cont");
            mc.EmitJmpNear(continueVa);
            mc.MarkLabel("dead");
            mc.Emit(new byte[] { 0xC7, 0x82, 0x0C, 0x08, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 }); // mov [edx+0x80C], 1 (Dead)
            mc.MarkLabel("skip");
            mc.EmitJmpNear(jeTargetVa);
            return mc.Build();
        }

        internal static byte[] BuildCaveDataFull()
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

        internal static byte[] BuildHookJmp(uint hookVa, uint targetVa, int totalSize)
        {
            byte[] hook = new byte[totalSize];
            hook[0] = 0xE9;
            byte[] rel = MachineCodeBuilder.Rel32Bytes(hookVa, targetVa);
            Buffer.BlockCopy(rel, 0, hook, 1, 4);
            for (int i = 5; i < totalSize; i++)
                hook[i] = 0x90;
            return hook;
        }

        private static uint ReadU32(byte[] data, uint va)
            => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)(va - ImageBase), 4));

        private static void WriteU32(byte[] data, uint va, uint value)
            => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)(va - ImageBase), 4), value);

        private static void DecryptOoaInPlace(byte[] data) => PatchUtility.DecryptOoaInPlace(data);

        private static void WritePreservingAttributes(string path, byte[] content) => PatchUtility.WritePreservingAttributes(path, content);
    }
}
