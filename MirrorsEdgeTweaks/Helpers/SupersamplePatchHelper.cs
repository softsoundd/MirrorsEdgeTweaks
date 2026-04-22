using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum SupersamplePatchState
    {
        Unknown,
        Unpatched,
        Patched
    }

    public static class SupersamplePatchHelper
    {
        private const uint ImageBase = 0x00400000;

        private static readonly string[] RequiredSymbols =
        {
            "GSystemSettings.ScreenPercentage", "Const_001f", "Const_1f",
            "Const_half", "Const_u2f",
            "GSystemSettings", "NeedsUpscale", "Allocate",
        };

        private static readonly string[] InlinePatchNames =
        {
            "needsUpscale_nop_gate", "needsUpscale_jbe_to_je",
            "scaleViewport_nop_clamp",
            "computeUpscale_nop_gate", "computeUpscale_jbe_to_je",
            "computeUpscale_remove_clamp",
            "renderTargetSize",
            "initViews_nop_x", "initViews_nop_y",
        };

        private static readonly string[] AllocateSiteNames =
        {
            "allocate_site_base_renderer", "allocate_site_rt_realloc",
            "allocate_site_rt_setup", "allocate_site_deferred_a",
            "allocate_site_deferred_b", "allocate_site_deferred_shading",
        };

        private static readonly string[] HookNames =
        {
            "scaleViewport_centering",
            "computeUpscale_centering",
            "motionBlur_setup",
            "motionBlur_teardown",
        };

        public static SupersamplePatchState GetPatchState(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null) return SupersamplePatchState.Unknown;

            if (version == "ea")
            {
                try { DecryptOoaInPlace(data); }
                catch { return SupersamplePatchState.Unknown; }
            }

            VersionAddressTable addrs;
            try { addrs = VersionAddressTable.Load(version); }
            catch { return SupersamplePatchState.Unknown; }

            return GetPatchStateFromData(data, addrs);
        }

        private static SupersamplePatchState GetPatchStateFromData(byte[] data, VersionAddressTable addrs)
        {
            int patchedCount = 0, unpatchedCount = 0;

            foreach (string name in InlinePatchNames)
            {
                if (!addrs.InlinePatches.TryGetValue(name, out var patch)) return SupersamplePatchState.Unknown;
                int foff = (int)(patch.Va - ImageBase);
                if (foff < 0 || foff + patch.OldBytes.Length > data.Length) return SupersamplePatchState.Unknown;

                bool isOld = data.AsSpan(foff, patch.OldBytes.Length).SequenceEqual(patch.OldBytes);
                bool isNew = patch.NewBytes.Length > 0 &&
                             data.AsSpan(foff, patch.NewBytes.Length).SequenceEqual(patch.NewBytes);

                if (isOld) unpatchedCount++;
                else if (isNew) patchedCount++;
                else return SupersamplePatchState.Unknown;
            }

            foreach (string name in AllocateSiteNames)
            {
                if (!addrs.InlinePatches.TryGetValue(name, out var patch)) return SupersamplePatchState.Unknown;
                int foff = (int)(patch.Va - ImageBase);
                if (foff < 0 || foff + patch.OldBytes.Length > data.Length) return SupersamplePatchState.Unknown;

                bool isOld = data.AsSpan(foff, patch.OldBytes.Length).SequenceEqual(patch.OldBytes);
                if (isOld) unpatchedCount++;
                else if (data[foff] == 0xE8) patchedCount++;
                else return SupersamplePatchState.Unknown;
            }

            if (unpatchedCount > 0 && patchedCount == 0) return SupersamplePatchState.Unpatched;
            if (patchedCount > 0 && unpatchedCount == 0) return SupersamplePatchState.Patched;
            return SupersamplePatchState.Unknown;
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

            foreach (string sym in RequiredSymbols)
            {
                if (!addrs.Symbols.ContainsKey(sym))
                    throw new InvalidOperationException($"Missing required symbol: {sym}");
            }

            var state = GetPatchStateFromData(data, addrs);
            if (state == SupersamplePatchState.Patched) return;

            var cave = CaveSection.Open(data, versionTag: version, forceTextPadding: isOoa);

            uint c1Va = cave.Alloc(44);
            uint c2Va = cave.Alloc(160);
            uint c3Va = cave.Alloc(68);
            uint c4Va = cave.Alloc(52);
            uint c5Va = cave.Alloc(56);

            uint mbSetupVa = addrs.ResolveHook(data, "motionBlur_setup");
            uint mbTeardownVa = addrs.ResolveHook(data, "motionBlur_teardown");

            byte[] c1 = BuildCave1();
            byte[] c2 = BuildCave2(addrs.Symbols);
            byte[] c3 = BuildCave3(c3Va, addrs.Symbols);
            byte[] c4 = BuildCave4(c4Va, addrs.Symbols, mbSetupVa);
            byte[] c5 = BuildCave5(c5Va, addrs.Symbols, mbTeardownVa);

            cave.Write(c1Va, c1);
            cave.Write(c2Va, c2);
            cave.Write(c3Va, c3);
            cave.Write(c4Va, c4);
            cave.Write(c5Va, c5);

            data = cave.Finalize();

            ApplyInlinePatches(data, addrs, c3Va);
            ApplyHookPatches(data, addrs, c1Va, c2Va, c4Va, c5Va);

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

            var addrs = VersionAddressTable.Load(version);
            var state = GetPatchStateFromData(data, addrs);
            if (state != SupersamplePatchState.Patched) return;

            foreach (string name in InlinePatchNames)
            {
                var patch = addrs.InlinePatches[name];
                int foff = (int)(patch.Va - ImageBase);
                Buffer.BlockCopy(patch.OldBytes, 0, data, foff, patch.OldBytes.Length);
            }

            foreach (string name in AllocateSiteNames)
            {
                var patch = addrs.InlinePatches[name];
                int foff = (int)(patch.Va - ImageBase);
                Buffer.BlockCopy(patch.OldBytes, 0, data, foff, patch.OldBytes.Length);
            }

            foreach (string hookName in HookNames)
            {
                var hdef = addrs.Hooks[hookName];
                uint hookVa;
                try { hookVa = VersionAddressTable.ResolveHook(data, hdef); }
                catch { continue; }

                int foff = (int)(hookVa - ImageBase);
                if (data[foff] == 0xE9)
                {
                    Buffer.BlockCopy(hdef.Pattern, 0, data, foff, hdef.Pattern.Length);
                    for (int i = hdef.Pattern.Length; i < hdef.Size; i++)
                        data[foff + i] = 0x90;
                }
            }

            if (isOoa && ooaCtx != null && ooaKey != null)
            {
                OoaService.UpdateEncBlockCrcs(data, ooaCtx);
                OoaService.ReencryptSections(data, ooaKey, ooaCtx);
                data = OoaService.TruncateOverlay(data, ooaCtx);
            }

            WritePreservingAttributes(exePath, data);
        }

        private static void ApplyInlinePatches(byte[] data, VersionAddressTable addrs, uint cave3Va)
        {
            foreach (string name in InlinePatchNames)
            {
                var patch = addrs.InlinePatches[name];
                int foff = (int)(patch.Va - ImageBase);
                var actual = data.AsSpan(foff, patch.OldBytes.Length);

                if (actual.SequenceEqual(patch.OldBytes))
                    Buffer.BlockCopy(patch.NewBytes, 0, data, foff, patch.NewBytes.Length);
                else if (patch.NewBytes.Length > 0 && actual.SequenceEqual(patch.NewBytes))
                    continue;
                else
                    throw new InvalidOperationException($"Inline patch '{name}' failed: unexpected bytes at 0x{patch.Va:X8}.");
            }

            foreach (string name in AllocateSiteNames)
            {
                var patch = addrs.InlinePatches[name];
                int foff = (int)(patch.Va - ImageBase);
                var actual = data.AsSpan(foff, patch.OldBytes.Length);

                if (actual.SequenceEqual(patch.OldBytes))
                {
                    byte[] newCall = new byte[5];
                    newCall[0] = 0xE8;
                    byte[] rel = MachineCodeBuilder.Rel32Bytes(patch.Va, cave3Va);
                    Buffer.BlockCopy(rel, 0, newCall, 1, 4);
                    Buffer.BlockCopy(newCall, 0, data, foff, 5);
                }
                else if (data[foff] == 0xE8)
                    continue;
                else
                    throw new InvalidOperationException($"Allocate redirect '{name}' failed: unexpected bytes at 0x{patch.Va:X8}.");
            }
        }

        private static void ApplyHookPatches(byte[] data, VersionAddressTable addrs,
            uint cave1Va, uint cave2Va, uint cave4Va, uint cave5Va)
        {
            var targets = new (string Name, uint CaveVa)[]
            {
                ("scaleViewport_centering", cave1Va),
                ("computeUpscale_centering", cave2Va),
                ("motionBlur_setup", cave4Va),
                ("motionBlur_teardown", cave5Va),
            };

            foreach (var (hookName, caveVa) in targets)
            {
                var hdef = addrs.Hooks[hookName];
                uint hookVa = VersionAddressTable.ResolveHook(data, hdef);
                int foff = (int)(hookVa - ImageBase);

                byte[] jmpBytes = new byte[5];
                jmpBytes[0] = 0xE9;
                byte[] rel = MachineCodeBuilder.Rel32Bytes(hookVa, caveVa);
                Buffer.BlockCopy(rel, 0, jmpBytes, 1, 4);

                var actual = data.AsSpan(foff, hdef.Pattern.Length);
                if (actual.SequenceEqual(hdef.Pattern))
                {
                    Buffer.BlockCopy(jmpBytes, 0, data, foff, 5);
                    for (int i = 5; i < hdef.Size; i++)
                        data[foff + i] = 0x90;
                }
                else if (data.AsSpan(foff, 5).SequenceEqual(jmpBytes))
                    continue;
                else
                    throw new InvalidOperationException($"Hook patch '{hookName}' failed: unexpected bytes at 0x{hookVa:X8}.");
            }
        }

        private static byte[] BuildCave1()
        {
            return new byte[]
            {
                0x2B, 0x0F,
                0x5F,
                0xD1, 0xF9,
                0x03, 0xCD,
                0x85, 0xC9,
                0x79, 0x02,
                0x31, 0xC9,
                0x89, 0x0B,
                0x2B, 0x16,
                0x8B, 0x4C, 0x24, 0x1C,
                0xD1, 0xFA,
                0x03, 0x54, 0x24, 0x10,
                0x85, 0xD2,
                0x79, 0x02,
                0x31, 0xD2,
                0x5E,
                0x5D,
                0x89, 0x11,
                0x5B,
                0x83, 0xC4, 0x08,
                0xC2, 0x10, 0x00,
            };
        }

        private static byte[] BuildCave2(Dictionary<string, uint> sym)
        {
            uint SP = sym["GSystemSettings.ScreenPercentage"];
            uint C001 = sym["Const_001f"];
            uint C1 = sym["Const_1f"];
            uint CHALF = sym["Const_half"];
            uint CU2F = sym["Const_u2f"];

            var code = new List<byte>();

            void Add(params byte[] b) => code.AddRange(b);
            void AddU32(uint v) { var buf = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buf, v); code.AddRange(buf); }

            // Check: movss xmm0, [SP]; mulss xmm0, [C001]; comiss xmm0, [C1]; ja +0x64
            Add(0xF3, 0x0F, 0x10, 0x05); AddU32(SP);
            Add(0xF3, 0x0F, 0x59, 0x05); AddU32(C001);
            Add(0x0F, 0x2F, 0x05); AddU32(C1);
            Add(0x77, 0x64);

            // X block
            Add(0xDB, 0x01, 0x8B, 0x01, 0x85, 0xC0, 0x7D, 0x06);
            Add(0xD8, 0x05); AddU32(CU2F);
            Add(0xD8, 0x64, 0x24, 0x20);
            Add(0xD9, 0x05); AddU32(CHALF);
            Add(0xDC, 0xC9, 0xD9, 0x44, 0x24, 0x18, 0xDE, 0xE2);
            Add(0xD9, 0xC9, 0xD9, 0x5C, 0x24, 0x20);
            Add(0xF3, 0x0F, 0x2C, 0x44, 0x24, 0x20);
            Add(0x8B, 0x4C, 0x24, 0x08, 0x89, 0x01);

            // Y block
            Add(0x8B, 0x12, 0x85, 0xD2, 0x89, 0x54, 0x24, 0x20);
            Add(0xDB, 0x44, 0x24, 0x20, 0x7D, 0x06);
            Add(0xD8, 0x05); AddU32(CU2F);
            Add(0xD8, 0x64, 0x24, 0x24, 0xDE, 0xC9);
            Add(0xD8, 0x6C, 0x24, 0x1C, 0xD9, 0x5C, 0x24, 0x20);
            Add(0xF3, 0x0F, 0x2C, 0x44, 0x24, 0x20);
            Add(0x8B, 0x4C, 0x24, 0x0C, 0x89, 0x01);

            // Exit block
            Add(0x59, 0xC2, 0x20, 0x00);

            // Zero block (SP > 100: zero both origins)
            Add(0x8B, 0x4C, 0x24, 0x08);
            Add(0xC7, 0x01, 0x00, 0x00, 0x00, 0x00);
            Add(0x8B, 0x4C, 0x24, 0x0C);
            Add(0xC7, 0x01, 0x00, 0x00, 0x00, 0x00);
            Add(0x59, 0xC2, 0x20, 0x00);

            return code.ToArray();
        }

        private static byte[] BuildCave3(uint baseVa, Dictionary<string, uint> sym)
        {
            uint SP = sym["GSystemSettings.ScreenPercentage"];
            uint C001 = sym["Const_001f"];
            uint C1 = sym["Const_1f"];
            uint ALLOC = sym["Allocate"];

            var code = new List<byte>();

            void Add(params byte[] b) => code.AddRange(b);
            void AddU32(uint v) { var buf = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buf, v); code.AddRange(buf); }

            // Check: SP * 0.01 > 1.0? if not, skip scaling (jbe +0x24)
            Add(0xF3, 0x0F, 0x10, 0x05); AddU32(SP);
            Add(0xF3, 0x0F, 0x59, 0x05); AddU32(C001);
            Add(0x0F, 0x2F, 0x05); AddU32(C1);
            Add(0x76, 0x24);

            // Scale width and height on stack by SP/100
            Add(0xF3, 0x0F, 0x2A, 0x4C, 0x24, 0x04);
            Add(0xF3, 0x0F, 0x59, 0xC8);
            Add(0xF3, 0x0F, 0x2C, 0xC1);
            Add(0x89, 0x44, 0x24, 0x04);
            Add(0xF3, 0x0F, 0x2A, 0x4C, 0x24, 0x08);
            Add(0xF3, 0x0F, 0x59, 0xC8);
            Add(0xF3, 0x0F, 0x2C, 0xC1);
            Add(0x89, 0x44, 0x24, 0x08);

            // jmp Allocate
            uint jmpVa = baseVa + (uint)code.Count;
            Add(0xE9);
            byte[] rel = MachineCodeBuilder.Rel32Bytes(jmpVa, ALLOC);
            code.AddRange(rel);

            return code.ToArray();
        }

        private static byte[] BuildCave4(uint baseVa, Dictionary<string, uint> sym, uint hookVa)
        {
            uint GS = sym["GSystemSettings"];
            uint NU = sym["NeedsUpscale"];
            uint backbufferVa = hookVa + 19;
            uint notFinalVa = hookVa + 9 + 0x72;
            uint nonLdrVa = hookVa + 0x13 + 0x5C;

            var code = new List<byte>();

            void Add(params byte[] b) => code.AddRange(b);
            void AddU32(uint v) { var buf = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buf, v); code.AddRange(buf); }

            // cmp dword ptr [esi+0x24C], 0
            Add(0x83, 0xBE, 0x4C, 0x02, 0x00, 0x00, 0x00);
            // je +0x1F (skip NeedsUpscale check if not final)
            Add(0x74, 0x1F);

            Add(0x51); // push ecx
            Add(0xB9); AddU32(GS); // mov ecx, GSystemSettings

            uint callVa = baseVa + (uint)code.Count;
            Add(0xE8); code.AddRange(MachineCodeBuilder.Rel32Bytes(callVa, NU)); // call NeedsUpscale

            Add(0x85, 0xC0); // test eax, eax
            Add(0x59);       // pop ecx
            Add(0x75, 0x0F); // jnz -> backbuffer_va

            Add(0x8B, 0x4C, 0x24, 0x14); // mov ecx, [esp+0x14]
            Add(0xF6, 0x41, 0x04, 0x08); // test byte ptr [ecx+4], 8
            Add(0x74, 0x0A);             // je -> not_final_va

            // jmp backbuffer_va (E9 at offset 35, instruction ends at 40)
            Add(0xE9); code.AddRange(MachineCodeBuilder.Rel32Bytes(baseVa + 35, backbufferVa));
            // jmp not_final_va (E9 at offset 40, instruction ends at 45)
            Add(0xE9); code.AddRange(MachineCodeBuilder.Rel32Bytes(baseVa + 40, notFinalVa));
            // jmp non_ldr_va (E9 at offset 45, instruction ends at 50)
            Add(0xE9); code.AddRange(MachineCodeBuilder.Rel32Bytes(baseVa + 45, nonLdrVa));

            return code.ToArray();
        }

        private static byte[] BuildCave5(uint baseVa, Dictionary<string, uint> sym, uint hookVa)
        {
            uint GS = sym["GSystemSettings"];
            uint NU = sym["NeedsUpscale"];
            uint finishVa = hookVa + 18;
            uint notFinalVa = hookVa + 8 + 0x36;
            uint skipVa = hookVa + 0x12 + 0x51;

            var code = new List<byte>();

            void Add(params byte[] b) => code.AddRange(b);
            void AddU32(uint v) { var buf = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buf, v); code.AddRange(buf); }

            // cmp [esi+0x24C], eax
            Add(0x39, 0x86, 0x4C, 0x02, 0x00, 0x00);
            // je +0x21
            Add(0x74, 0x21);

            Add(0x51); // push ecx
            Add(0xB9); AddU32(GS); // mov ecx, GSystemSettings

            uint callVa = baseVa + (uint)code.Count;
            Add(0xE8); code.AddRange(MachineCodeBuilder.Rel32Bytes(callVa, NU)); // call NeedsUpscale

            Add(0x85, 0xC0); // test eax, eax
            Add(0x59);       // pop ecx
            Add(0x75, 0x11); // jnz -> finish_va

            Add(0x31, 0xC0); // xor eax, eax
            Add(0x8B, 0x4C, 0x24, 0x14); // mov ecx, [esp+0x14]
            Add(0xF6, 0x41, 0x04, 0x08); // test byte ptr [ecx+4], 8
            Add(0x75, 0x0C);             // jne -> skip_va

            // jmp finish_va (E9 at offset 36, instruction ends at 41)
            Add(0xE9); code.AddRange(MachineCodeBuilder.Rel32Bytes(baseVa + 36, finishVa));
            Add(0x31, 0xC0); // xor eax, eax
            // jmp not_final_va (E9 at offset 43, instruction ends at 48)
            Add(0xE9); code.AddRange(MachineCodeBuilder.Rel32Bytes(baseVa + 43, notFinalVa));
            // jmp skip_va (E9 at offset 48, instruction ends at 53)
            Add(0xE9); code.AddRange(MachineCodeBuilder.Rel32Bytes(baseVa + 48, skipVa));

            return code.ToArray();
        }

        private static void DecryptOoaInPlace(byte[] data) => PatchUtility.DecryptOoaInPlace(data);

        private static void WritePreservingAttributes(string path, byte[] content) => PatchUtility.WritePreservingAttributes(path, content);
    }
}
