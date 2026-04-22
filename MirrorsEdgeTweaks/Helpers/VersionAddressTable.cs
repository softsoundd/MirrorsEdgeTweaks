using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace MirrorsEdgeTweaks.Helpers
{
    public sealed class HookDefinition
    {
        public byte[] Pattern { get; init; } = Array.Empty<byte>();
        public int Size { get; init; }
        public int ReturnOffset { get; init; }
        public uint ScanStart { get; init; }
        public int ScanSize { get; init; } = 0x2000;
    }

    public sealed class InlinePatchDef
    {
        public uint Va { get; init; }
        public byte[] OldBytes { get; init; } = Array.Empty<byte>();
        public byte[] NewBytes { get; init; } = Array.Empty<byte>();
    }

    public sealed class VersionAddressTable
    {
        public string Version { get; init; } = "";
        public string Label { get; init; } = "";
        public Dictionary<string, uint> Symbols { get; init; } = new();
        public Dictionary<string, HookDefinition> Hooks { get; init; } = new();
        public Dictionary<string, InlinePatchDef> InlinePatches { get; init; } = new();

        private const uint ImageBase = 0x00400000;

        public static VersionAddressTable Load(string version)
        {
            string resourceName = $"MirrorsEdgeTweaks.Versions.{version}.json";
            var assembly = Assembly.GetExecutingAssembly();
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                throw new FileNotFoundException($"No address table for variant '{version}' (resource: {resourceName}).");

            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var table = new VersionAddressTable
            {
                Version = version,
                Label = root.TryGetProperty("label", out var lbl) ? lbl.GetString() ?? version : version,
            };

            if (root.TryGetProperty("symbols", out var syms))
            {
                foreach (var prop in syms.EnumerateObject())
                    table.Symbols[prop.Name] = ParseAddr(prop.Value);
            }

            if (root.TryGetProperty("hooks", out var hooks))
            {
                foreach (var prop in hooks.EnumerateObject())
                {
                    var hObj = prop.Value;
                    table.Hooks[prop.Name] = new HookDefinition
                    {
                        Pattern = ParseHexBytes(hObj.GetProperty("pattern").GetString()!),
                        Size = hObj.GetProperty("size").GetInt32(),
                        ReturnOffset = hObj.TryGetProperty("return_offset", out var ro)
                            ? ro.GetInt32()
                            : hObj.GetProperty("size").GetInt32(),
                        ScanStart = hObj.TryGetProperty("scan_start", out var ss)
                            ? ParseAddr(ss) : 0,
                        ScanSize = hObj.TryGetProperty("scan_size", out var sz)
                            ? sz.GetInt32() : 0x2000,
                    };
                }
            }

            if (root.TryGetProperty("inline_patches", out var patches))
            {
                foreach (var prop in patches.EnumerateObject())
                {
                    var pObj = prop.Value;
                    table.InlinePatches[prop.Name] = new InlinePatchDef
                    {
                        Va = ParseAddr(pObj.GetProperty("va")),
                        OldBytes = ParseHexBytes(pObj.GetProperty("old").GetString()!),
                        NewBytes = pObj.TryGetProperty("new", out var nv)
                            ? ParseHexBytes(nv.GetString()!) : Array.Empty<byte>(),
                    };
                }
            }

            return table;
        }

        public uint ResolveHook(byte[] peData, string hookName)
        {
            if (!Hooks.TryGetValue(hookName, out var hdef))
                throw new InvalidOperationException($"Hook '{hookName}' not found in address table.");
            return ResolveHook(peData, hdef);
        }

        public static uint ResolveHook(byte[] peData, HookDefinition hdef)
        {
            byte[] pattern = hdef.Pattern;
            uint startVa = hdef.ScanStart;
            int scanSize = hdef.ScanSize;

            int startOff = (int)(startVa - ImageBase);
            if (startOff < 0 || startOff + scanSize > peData.Length)
                throw new InvalidOperationException(
                    $"Hook scan window 0x{startVa:X8}+{scanSize} is out of bounds.");

            var matches = new List<uint>();
            int idx = 0;
            while (idx <= scanSize - pattern.Length)
            {
                int pos = FindPattern(peData, startOff + idx, startOff + scanSize, pattern);
                if (pos < 0) break;
                matches.Add(startVa + (uint)(pos - startOff));
                idx = pos - startOff + 1;
            }

            if (matches.Count == 0)
                throw new InvalidOperationException(
                    $"Hook pattern not found in 0x{startVa:X8}..0x{startVa + (uint)scanSize:X8}.");
            if (matches.Count > 1)
                throw new InvalidOperationException(
                    $"Hook pattern found {matches.Count} times (expected exactly 1).");

            return matches[0];
        }

        private static int FindPattern(byte[] data, int start, int end, byte[] pattern)
        {
            int limit = end - pattern.Length;
            for (int i = start; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static uint ParseAddr(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number)
                return (uint)el.GetInt64();
            string s = el.GetString()!;
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt32(s, 16)
                : uint.Parse(s);
        }

        private static byte[] ParseHexBytes(string hex)
        {
            hex = hex.Replace(" ", "");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
