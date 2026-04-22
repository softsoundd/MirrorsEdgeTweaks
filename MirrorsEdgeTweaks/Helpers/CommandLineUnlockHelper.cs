using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum CommandLineUnlockMode
    {
        Unsupported,
        PersistentFilePatch
    }

    public static class CommandLineUnlockHelper
    {
        private const string StockMarker = "FlybyFlight";
        private const string LegacyMarker = "CmdLineArgs";
        private const string StockFlybyCommandLine = "escape_p?Loadcheckpoint=ChaseFlyby?Causeevent=startflyby -nostartupmovies";
        private const string StockNoStartupMoviesToken = "nostartupmovies";
        private const string StockNoStartupMoviesSwitch = "-nostartupmovies";

        private const int BranchLength = 43;
        private const int EmptyGapBytes = 2;
        private const int EmptySpanBytes = 8;
        private const uint ExecuteSectionFlag = 0x20000000;
        private static readonly byte[] BranchPrefix = Convert.FromHexString("83C40885C0740768");

        public static CommandLineUnlockMode GetUnlockMode(string exePath)
        {
            byte[] buffer = File.ReadAllBytes(exePath);
            ExecutableImageLayout image = ExecutableImageLayout.Parse(buffer);

            if (TryDerivePersistentLayout(image, buffer, out _))
            {
                return CommandLineUnlockMode.PersistentFilePatch;
            }

            if (OoaService.HasOoaSection(buffer))
            {
                return CommandLineUnlockMode.PersistentFilePatch;
            }

            return CommandLineUnlockMode.Unsupported;
        }

        public static bool IsUnlocked(string exePath)
        {
            byte[] buffer = File.ReadAllBytes(exePath);
            ExecutableImageLayout image = ExecutableImageLayout.Parse(buffer);

            if (TryDerivePersistentLayout(image, buffer, out CommandLineUnlockLayout layout))
            {
                return buffer.AsSpan(layout.BranchOffset, BranchLength)
                    .SequenceEqual(BuildUnlockedBranch(layout));
            }

            if (!OoaService.HasOoaSection(buffer))
            {
                return false;
            }

            try
            {
                string? dlfPath = OoaService.FindLicensePath(buffer);
                if (dlfPath == null) return false;

                byte[] key = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
                OoaService.DecryptSections(buffer, key);
                image = ExecutableImageLayout.Parse(buffer);

                if (!TryDerivePersistentLayout(image, buffer, out layout))
                {
                    return false;
                }

                return buffer.AsSpan(layout.BranchOffset, BranchLength)
                    .SequenceEqual(BuildUnlockedBranch(layout));
            }
            catch
            {
                return false;
            }
        }

        public static bool Unlock(string exePath)
        {
            return PatchExecutable(exePath, unlock: true);
        }

        public static bool RestoreStock(string exePath)
        {
            return PatchExecutable(exePath, unlock: false);
        }

        private static bool PatchExecutable(string exePath, bool unlock)
        {
            byte[] originalBuffer = File.ReadAllBytes(exePath);
            byte[] buffer = (byte[])originalBuffer.Clone();

            ExecutableImageLayout image = ExecutableImageLayout.Parse(buffer);
            if (TryDerivePersistentLayout(image, buffer, out CommandLineUnlockLayout layout))
            {
                PatchBranch(buffer, image, layout, unlock);
                PatchStrings(buffer, image, layout);

                if (buffer.AsSpan().SequenceEqual(originalBuffer))
                {
                    return false;
                }

                WriteAllBytesPreservingAttributes(exePath, buffer);
                return true;
            }

            if (OoaService.HasOoaSection(buffer))
            {
                return PatchOoaExecutable(exePath, buffer, unlock);
            }

            throw new InvalidOperationException(
                "Could not locate the command line bootstrap in this executable.");
        }

        private static bool PatchOoaExecutable(string exePath, byte[] buffer, bool unlock)
        {
            string? dlfPath = OoaService.FindLicensePath(buffer);
            if (dlfPath == null)
            {
                throw new OoaLicenseNotFoundException(
                    OoaService.GetExpectedLicensePath(buffer));
            }

            byte[] key = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
            OoaService.StripAuthenticode(buffer);
            OoaContext ctx = OoaService.DecryptSections(buffer, key);

            ExecutableImageLayout image = ExecutableImageLayout.Parse(buffer);
            if (!TryDerivePersistentLayout(image, buffer, out CommandLineUnlockLayout layout))
            {
                throw new InvalidOperationException(
                    "Could not locate the command line bootstrap in the decrypted EA executable.");
            }

            byte[] branchBefore = buffer.AsSpan(layout.BranchOffset, BranchLength).ToArray();

            PatchBranch(buffer, image, layout, unlock);
            PatchStrings(buffer, image, layout);

            if (buffer.AsSpan(layout.BranchOffset, BranchLength).SequenceEqual(branchBefore))
            {
                return false;
            }

            OoaService.UpdateEncBlockCrcs(buffer, ctx);
            OoaService.ReencryptSections(buffer, key, ctx);
            byte[] output = OoaService.TruncateOverlay(buffer, ctx);
            WriteAllBytesPreservingAttributes(exePath, output);
            return true;
        }

        // Branch + string patching

        private static void PatchBranch(byte[] buffer, ExecutableImageLayout image, CommandLineUnlockLayout layout, bool unlock)
        {
            if (layout.BranchOffset < 0)
            {
                throw new InvalidOperationException("The command line branch is not available for on-disk patching in this executable.");
            }

            ReadOnlySpan<byte> currentBranch = buffer.AsSpan(layout.BranchOffset, BranchLength);
            byte[] unlockedBranch = BuildUnlockedBranch(layout);

            if (unlock)
            {
                if (currentBranch.SequenceEqual(unlockedBranch))
                {
                    return;
                }

                if (!MatchesStockBranch(currentBranch, layout))
                {
                    throw new InvalidOperationException(
                        $"Unexpected command line branch bytes at file offset 0x{layout.BranchOffset:X}. Unsupported executable revision.");
                }

                unlockedBranch.AsSpan().CopyTo(buffer.AsSpan(layout.BranchOffset, BranchLength));
                return;
            }

            if (MatchesStockBranch(currentBranch, layout))
            {
                return;
            }

            if (!currentBranch.SequenceEqual(unlockedBranch))
            {
                throw new InvalidOperationException(
                    $"Unexpected command line branch bytes at file offset 0x{layout.BranchOffset:X}. Unsupported executable revision.");
            }

            byte[] stockBranch = BuildStockBranch(image, layout);
            stockBranch.AsSpan().CopyTo(buffer.AsSpan(layout.BranchOffset, BranchLength));
        }

        private static void PatchStrings(byte[] buffer, ExecutableImageLayout image, CommandLineUnlockLayout layout)
        {
            WriteSpan(buffer, image, layout.MarkerVa, checked((int)(layout.FlybyCommandLineVa - layout.MarkerVa)), EncodeUtf16Le(StockMarker));
            WriteSpan(buffer, image, layout.FlybyCommandLineVa, checked((int)(layout.NoStartupMoviesTokenVa - layout.FlybyCommandLineVa)), EncodeUtf16Le(StockFlybyCommandLine));
            WriteSpan(buffer, image, layout.NoStartupMoviesTokenVa, checked((int)(layout.NoStartupMoviesSwitchVa - layout.NoStartupMoviesTokenVa)), EncodeUtf16Le(StockNoStartupMoviesToken));
            WriteSpan(buffer, image, layout.NoStartupMoviesSwitchVa, checked((int)(layout.EmptyVa - layout.NoStartupMoviesSwitchVa)), EncodeUtf16Le(StockNoStartupMoviesSwitch));
            WriteSpan(buffer, image, layout.EmptyVa, checked((int)(layout.ErrorHistoryVa - layout.EmptyVa)), new byte[] { 0x00, 0x00 });
        }

        private static void WriteSpan(byte[] buffer, ExecutableImageLayout image, uint va, int spanSize, byte[] payload)
        {
            if (payload.Length > spanSize)
            {
                throw new InvalidOperationException($"Payload for 0x{va:X8} does not fit inside a {spanSize}-byte span.");
            }

            int offset = image.VaToOffset(va);
            byte[] paddedPayload = new byte[spanSize];
            payload.AsSpan().CopyTo(paddedPayload);
            paddedPayload.AsSpan().CopyTo(buffer.AsSpan(offset, spanSize));
        }

        // Layout derivation

        private static bool TryDerivePersistentLayout(ExecutableImageLayout image, byte[] buffer, out CommandLineUnlockLayout layout)
        {
            int markerSpan = EncodeUtf16Le(StockMarker).Length;
            int flybySpan = EncodeUtf16Le(StockFlybyCommandLine).Length;
            int noStartupTokenSpan = EncodeUtf16Le(StockNoStartupMoviesToken).Length;
            int noStartupSwitchSpan = EncodeUtf16Le(StockNoStartupMoviesSwitch).Length;

            foreach (string markerText in new[] { StockMarker, LegacyMarker })
            {
                byte[] markerBytes = EncodeUtf16Le(markerText);
                foreach (int markerOffset in FindAllOffsets(buffer, markerBytes))
                {
                    uint markerVa = image.OffsetToVa(markerOffset);
                    byte[] codeReference = new byte[6];
                    codeReference[0] = 0x68;
                    WriteUInt32(codeReference, 1, markerVa);
                    codeReference[5] = 0x56;

                    foreach (int referenceOffset in FindAllOffsets(buffer, codeReference))
                    {
                        if (!image.IsExecutableOffset(referenceOffset))
                        {
                            continue;
                        }

                        int branchOffset = FindPattern(buffer, BranchPrefix, referenceOffset, Math.Min(referenceOffset + 0x40, buffer.Length));
                        if (branchOffset == -1)
                        {
                            continue;
                        }

                        if (!TryFindParseParamLikeTarget(image, buffer, referenceOffset, branchOffset, out uint parseParamLikeTargetVa))
                        {
                            continue;
                        }

                        uint flybyVa = markerVa + (uint)markerSpan;
                        uint noStartupTokenVa = flybyVa + (uint)flybySpan;
                        uint noStartupSwitchVa = noStartupTokenVa + (uint)noStartupTokenSpan;
                        uint emptyVa = noStartupSwitchVa + (uint)noStartupSwitchSpan + EmptyGapBytes;
                        uint errorHistoryVa = emptyVa + EmptySpanBytes;
                        uint branchVa = image.OffsetToVa(branchOffset);

                        layout = new CommandLineUnlockLayout(
                            markerVa,
                            flybyVa,
                            noStartupTokenVa,
                            noStartupSwitchVa,
                            emptyVa,
                            errorHistoryVa,
                            branchOffset,
                            parseParamLikeTargetVa,
                            branchVa);
                        return true;
                    }
                }
            }

            layout = default;
            return false;
        }

        // Branch construction

        private static byte[] BuildUnlockedBranch(CommandLineUnlockLayout layout)
        {
            byte[] branch = new byte[BranchLength];
            BranchPrefix.AsSpan().CopyTo(branch);
            WriteUInt32(branch, 8, layout.FlybyCommandLineVa);
            branch[12] = 0xEB;
            branch[13] = 0x01;
            branch[14] = 0x56;
            branch.AsSpan(15).Fill(0x90);
            return branch;
        }

        private static bool MatchesStockBranch(ReadOnlySpan<byte> current, CommandLineUnlockLayout layout)
        {
            return
                current.Length == BranchLength &&
                current.Slice(0, 8).SequenceEqual(BranchPrefix) &&
                BinaryPrimitives.ReadUInt32LittleEndian(current.Slice(8, 4)) == layout.FlybyCommandLineVa &&
                current.Slice(12, 3).SequenceEqual(new byte[] { 0xEB, 0x1D, 0x68 }) &&
                BinaryPrimitives.ReadUInt32LittleEndian(current.Slice(15, 4)) == layout.NoStartupMoviesTokenVa &&
                current.Slice(19, 2).SequenceEqual(new byte[] { 0x56, 0xE8 }) &&
                current.Slice(25, 6).SequenceEqual(new byte[] { 0x83, 0xC4, 0x08, 0x85, 0xC0, 0xB8 }) &&
                BinaryPrimitives.ReadUInt32LittleEndian(current.Slice(31, 4)) == layout.NoStartupMoviesSwitchVa &&
                current.Slice(35, 3).SequenceEqual(new byte[] { 0x75, 0x05, 0xB8 }) &&
                BinaryPrimitives.ReadUInt32LittleEndian(current.Slice(38, 4)) == layout.EmptyVa &&
                current[42] == 0x50;
        }

        private static byte[] BuildStockBranch(ExecutableImageLayout image, CommandLineUnlockLayout layout)
        {
            byte[] branch = new byte[BranchLength];
            BranchPrefix.AsSpan().CopyTo(branch);

            WriteUInt32(branch, 8, layout.FlybyCommandLineVa);
            branch[12] = 0xEB;
            branch[13] = 0x1D;
            branch[14] = 0x68;
            WriteUInt32(branch, 15, layout.NoStartupMoviesTokenVa);
            branch[19] = 0x56;
            branch[20] = 0xE8;

            uint nextInstructionVa = layout.BranchVa + 25;
            int callDisplacement = checked((int)((long)layout.ParseParamLikeTargetVa - nextInstructionVa));
            BinaryPrimitives.WriteInt32LittleEndian(branch.AsSpan(21, 4), callDisplacement);

            branch[25] = 0x83;
            branch[26] = 0xC4;
            branch[27] = 0x08;
            branch[28] = 0x85;
            branch[29] = 0xC0;
            branch[30] = 0xB8;
            WriteUInt32(branch, 31, layout.NoStartupMoviesSwitchVa);
            branch[35] = 0x75;
            branch[36] = 0x05;
            branch[37] = 0xB8;
            WriteUInt32(branch, 38, layout.EmptyVa);
            branch[42] = 0x50;

            return branch;
        }

        private static bool TryFindParseParamLikeTarget(ExecutableImageLayout image, byte[] buffer, int referenceOffset, int branchOffset, out uint targetVa)
        {
            targetVa = 0;

            for (int callOpcodeOffset = branchOffset - 5; callOpcodeOffset >= referenceOffset + 6; callOpcodeOffset--)
            {
                if (buffer[callOpcodeOffset] != 0xE8)
                {
                    continue;
                }

                if (TryResolveCallTarget(image, buffer, callOpcodeOffset, out targetVa) && image.IsExecutableVa(targetVa))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveCallTarget(ExecutableImageLayout image, byte[] buffer, int callOpcodeOffset, out uint targetVa)
        {
            targetVa = 0;

            if (callOpcodeOffset + 5 > buffer.Length || buffer[callOpcodeOffset] != 0xE8)
            {
                return false;
            }

            int relativeOffset = BinaryPrimitives.ReadInt32LittleEndian(ReadSpan(buffer, callOpcodeOffset + 1, 4));
            uint nextInstructionVa = image.OffsetToVa(callOpcodeOffset + 5);
            long resolvedTarget = (long)nextInstructionVa + relativeOffset;
            if (resolvedTarget < 0 || resolvedTarget > uint.MaxValue)
            {
                return false;
            }

            targetVa = (uint)resolvedTarget;
            return true;
        }

        // Utility

        private static IEnumerable<int> FindAllOffsets(byte[] buffer, byte[] pattern)
        {
            if (pattern.Length == 0 || buffer.Length < pattern.Length)
            {
                yield break;
            }

            for (int offset = 0; offset <= buffer.Length - pattern.Length; offset++)
            {
                if (buffer.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
                {
                    yield return offset;
                }
            }
        }

        private static int FindPattern(byte[] buffer, byte[] pattern, int startOffset, int endExclusive)
        {
            if (pattern.Length == 0 || startOffset < 0)
            {
                return -1;
            }

            int lastPossibleStart = Math.Min(buffer.Length - pattern.Length, endExclusive - pattern.Length);
            for (int offset = startOffset; offset <= lastPossibleStart; offset++)
            {
                if (buffer.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
                {
                    return offset;
                }
            }

            return -1;
        }

        private static byte[] EncodeUtf16Le(string text)
        {
            return Encoding.Unicode.GetBytes(text + '\0');
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);
        }

        private static void WriteAllBytesPreservingAttributes(string path, byte[] content)
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            if (wasReadOnly)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            try
            {
                File.WriteAllBytes(path, content);
            }
            finally
            {
                if (wasReadOnly)
                {
                    File.SetAttributes(path, attributes);
                }
            }
        }

        private static ReadOnlySpan<byte> ReadSpan(byte[] buffer, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > buffer.Length)
            {
                throw new InvalidDataException("The executable appears to be truncated or invalid.");
            }

            return buffer.AsSpan(offset, length);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(ReadSpan(buffer, offset, sizeof(ushort)));
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(ReadSpan(buffer, offset, sizeof(uint)));
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(ReadSpan(buffer, offset, sizeof(ulong)));
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(ReadSpan(buffer, offset, sizeof(int)));
        }

        // Types

        private readonly struct CommandLineUnlockLayout
        {
            public CommandLineUnlockLayout(
                uint markerVa,
                uint flybyCommandLineVa,
                uint noStartupMoviesTokenVa,
                uint noStartupMoviesSwitchVa,
                uint emptyVa,
                uint errorHistoryVa,
                int branchOffset,
                uint parseParamLikeTargetVa,
                uint branchVa)
            {
                MarkerVa = markerVa;
                FlybyCommandLineVa = flybyCommandLineVa;
                NoStartupMoviesTokenVa = noStartupMoviesTokenVa;
                NoStartupMoviesSwitchVa = noStartupMoviesSwitchVa;
                EmptyVa = emptyVa;
                ErrorHistoryVa = errorHistoryVa;
                BranchOffset = branchOffset;
                ParseParamLikeTargetVa = parseParamLikeTargetVa;
                BranchVa = branchVa;
            }

            public uint MarkerVa { get; }
            public uint FlybyCommandLineVa { get; }
            public uint NoStartupMoviesTokenVa { get; }
            public uint NoStartupMoviesSwitchVa { get; }
            public uint EmptyVa { get; }
            public uint ErrorHistoryVa { get; }
            public int BranchOffset { get; }
            public uint ParseParamLikeTargetVa { get; }
            public uint BranchVa { get; }
        }

        private sealed class ExecutableImageLayout
        {
            private readonly List<SectionInfo> _sections;

            private ExecutableImageLayout(uint imageBase, List<SectionInfo> sections)
            {
                ImageBase = imageBase;
                _sections = sections;
            }

            public uint ImageBase { get; }

            public static ExecutableImageLayout Parse(byte[] buffer)
            {
                if (buffer.Length < 0x40 || buffer[0] != 'M' || buffer[1] != 'Z')
                {
                    throw new InvalidDataException("The selected file is not a valid PE executable.");
                }

                int peHeaderOffset = ReadInt32(buffer, 0x3C);
                if (peHeaderOffset < 0 || peHeaderOffset + 24 > buffer.Length)
                {
                    throw new InvalidDataException("The selected executable has an invalid PE header.");
                }

                if (!ReadSpan(buffer, peHeaderOffset, 4).SequenceEqual(new byte[] { 0x50, 0x45, 0x00, 0x00 }))
                {
                    throw new InvalidDataException("The selected executable has an invalid PE signature.");
                }

                ushort sectionCount = ReadUInt16(buffer, peHeaderOffset + 6);
                ushort optionalHeaderSize = ReadUInt16(buffer, peHeaderOffset + 20);
                int optionalHeaderOffset = peHeaderOffset + 24;
                ushort optionalHeaderMagic = ReadUInt16(buffer, optionalHeaderOffset);

                uint imageBase = optionalHeaderMagic switch
                {
                    0x10B => ReadUInt32(buffer, optionalHeaderOffset + 28),
                    0x20B => checked((uint)ReadUInt64(buffer, optionalHeaderOffset + 24)),
                    _ => throw new InvalidDataException("Unsupported PE optional header format.")
                };

                int sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
                int requiredSectionBytes = checked(sectionCount * 40);
                if (sectionTableOffset < 0 || sectionTableOffset + requiredSectionBytes > buffer.Length)
                {
                    throw new InvalidDataException("The executable section table is incomplete.");
                }

                List<SectionInfo> sections = new List<SectionInfo>(sectionCount);
                for (int index = 0; index < sectionCount; index++)
                {
                    int sectionOffset = sectionTableOffset + (index * 40);
                    string name = ReadSectionName(buffer, sectionOffset);
                    sections.Add(new SectionInfo(
                        name,
                        ReadUInt32(buffer, sectionOffset + 12),
                        ReadUInt32(buffer, sectionOffset + 8),
                        ReadUInt32(buffer, sectionOffset + 20),
                        ReadUInt32(buffer, sectionOffset + 16),
                        ReadUInt32(buffer, sectionOffset + 36)));
                }

                return new ExecutableImageLayout(imageBase, sections);
            }

            public uint OffsetToVa(int offset)
            {
                SectionInfo section = FindSectionByRawOffset(offset);
                uint rva = section.VirtualAddress + checked((uint)(offset - section.PointerToRawData));
                return ImageBase + rva;
            }

            public int VaToOffset(uint va)
            {
                uint rva = checked(va - ImageBase);
                SectionInfo section = FindSectionByRva(rva);
                return checked((int)(section.PointerToRawData + (rva - section.VirtualAddress)));
            }

            public bool IsExecutableOffset(int offset)
            {
                uint rva = checked(OffsetToVa(offset) - ImageBase);
                SectionInfo section = FindSectionByRva(rva);
                return (section.Characteristics & ExecuteSectionFlag) != 0;
            }

            public bool IsExecutableVa(uint va)
            {
                uint rva = checked(va - ImageBase);
                SectionInfo section = FindSectionByRva(rva);
                return (section.Characteristics & ExecuteSectionFlag) != 0;
            }

            private SectionInfo FindSectionByRawOffset(int offset)
            {
                foreach (SectionInfo section in _sections)
                {
                    if (section.SizeOfRawData == 0)
                    {
                        continue;
                    }

                    uint start = section.PointerToRawData;
                    uint end = start + section.SizeOfRawData;
                    if ((uint)offset >= start && (uint)offset < end)
                    {
                        return section;
                    }
                }

                throw new InvalidDataException($"Could not map file offset 0x{offset:X} into a PE section.");
            }

            private SectionInfo FindSectionByRva(uint rva)
            {
                foreach (SectionInfo section in _sections)
                {
                    uint size = Math.Max(section.VirtualSize, section.SizeOfRawData);
                    uint start = section.VirtualAddress;
                    uint end = start + size;
                    if (rva >= start && rva < end)
                    {
                        return section;
                    }
                }

                throw new InvalidDataException($"Could not map RVA 0x{rva:X} into a PE section.");
            }

            private static string ReadSectionName(byte[] buffer, int sectionOffset)
            {
                ReadOnlySpan<byte> nameBytes = ReadSpan(buffer, sectionOffset, 8);
                int zeroIndex = nameBytes.IndexOf((byte)0);
                if (zeroIndex >= 0)
                {
                    nameBytes = nameBytes[..zeroIndex];
                }

                return Encoding.ASCII.GetString(nameBytes);
            }
        }

        private readonly struct SectionInfo
        {
            public SectionInfo(string name, uint virtualAddress, uint virtualSize, uint pointerToRawData, uint sizeOfRawData, uint characteristics)
            {
                Name = name;
                VirtualAddress = virtualAddress;
                VirtualSize = virtualSize;
                PointerToRawData = pointerToRawData;
                SizeOfRawData = sizeOfRawData;
                Characteristics = characteristics;
            }

            public string Name { get; }
            public uint VirtualAddress { get; }
            public uint VirtualSize { get; }
            public uint PointerToRawData { get; }
            public uint SizeOfRawData { get; }
            public uint Characteristics { get; }
        }
    }
}
