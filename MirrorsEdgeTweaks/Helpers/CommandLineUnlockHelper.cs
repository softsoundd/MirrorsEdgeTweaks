using MirrorsEdgeTweaks.Services;
using System.Buffers.Binary;
using System.IO;
using System.Text;

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
        private const string StockFlybyCommandLine = "escape_p?Loadcheckpoint=ChaseFlyby?Causeevent=startflyby -nostartupmovies";
        private const string StockNoStartupMoviesToken = "nostartupmovies";
        private const string StockNoStartupMoviesSwitch = "-nostartupmovies";

        private const int BranchLength = 43;
        private const int EmptyGapBytes = 2;
        private const int EmptySpanBytes = 8;
        private static readonly byte[] BranchPrefix = Convert.FromHexString("83C40885C0740768");

        public static CommandLineUnlockMode GetUnlockMode(string exePath)
        {
            byte[] buffer = File.ReadAllBytes(exePath);
            PeImageLayout image = PeImageLayout.Parse(buffer);

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
            PeImageLayout image = PeImageLayout.Parse(buffer);

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
                image = PeImageLayout.Parse(buffer);

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

            PeImageLayout image = PeImageLayout.Parse(buffer);
            if (TryDerivePersistentLayout(image, buffer, out CommandLineUnlockLayout layout))
            {
                PatchBranch(buffer, image, layout, unlock);
                PatchStrings(buffer, image, layout);

                if (buffer.AsSpan().SequenceEqual(originalBuffer))
                {
                    return false;
                }

                PatchUtility.WritePreservingAttributes(exePath, buffer);
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
            PatchUtility.OoaSession session = PatchUtility.BeginOoa(buffer);

            PeImageLayout image = PeImageLayout.Parse(buffer);
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

            byte[] output = PatchUtility.FinishOoa(buffer, session);
            PatchUtility.WritePreservingAttributes(exePath, output);
            return true;
        }

        private static void PatchBranch(byte[] buffer, PeImageLayout image, CommandLineUnlockLayout layout, bool unlock)
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

        private static void PatchStrings(byte[] buffer, PeImageLayout image, CommandLineUnlockLayout layout)
        {
            WriteSpan(buffer, image, layout.MarkerVa, checked((int)(layout.FlybyCommandLineVa - layout.MarkerVa)), EncodeUtf16Le(StockMarker));
            WriteSpan(buffer, image, layout.FlybyCommandLineVa, checked((int)(layout.NoStartupMoviesTokenVa - layout.FlybyCommandLineVa)), EncodeUtf16Le(StockFlybyCommandLine));
            WriteSpan(buffer, image, layout.NoStartupMoviesTokenVa, checked((int)(layout.NoStartupMoviesSwitchVa - layout.NoStartupMoviesTokenVa)), EncodeUtf16Le(StockNoStartupMoviesToken));
            WriteSpan(buffer, image, layout.NoStartupMoviesSwitchVa, checked((int)(layout.EmptyVa - layout.NoStartupMoviesSwitchVa)), EncodeUtf16Le(StockNoStartupMoviesSwitch));
            WriteSpan(buffer, image, layout.EmptyVa, checked((int)(layout.ErrorHistoryVa - layout.EmptyVa)), new byte[] { 0x00, 0x00 });
        }

        private static void WriteSpan(byte[] buffer, PeImageLayout image, uint va, int spanSize, byte[] payload)
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

        private static bool TryDerivePersistentLayout(PeImageLayout image, byte[] buffer, out CommandLineUnlockLayout layout)
        {
            int markerSpan = EncodeUtf16Le(StockMarker).Length;
            int flybySpan = EncodeUtf16Le(StockFlybyCommandLine).Length;
            int noStartupTokenSpan = EncodeUtf16Le(StockNoStartupMoviesToken).Length;
            int noStartupSwitchSpan = EncodeUtf16Le(StockNoStartupMoviesSwitch).Length;

            byte[] markerBytes = EncodeUtf16Le(StockMarker);
            foreach (int markerOffset in FindAllOffsets(buffer, markerBytes))
            {
                // A marker match outside any mapped section (headers, appended data) cannot be the
                // referenced string; skip it rather than fault.
                uint? markerVaMaybe = image.TryOffsetToVa(markerOffset);
                if (markerVaMaybe == null)
                {
                    continue;
                }

                uint markerVa = markerVaMaybe.Value;
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

            layout = default;
            return false;
        }

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

        private static byte[] BuildStockBranch(PeImageLayout image, CommandLineUnlockLayout layout)
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

        private static bool TryFindParseParamLikeTarget(PeImageLayout image, byte[] buffer, int referenceOffset, int branchOffset, out uint targetVa)
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

        private static bool TryResolveCallTarget(PeImageLayout image, byte[] buffer, int callOpcodeOffset, out uint targetVa)
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

        private static IEnumerable<int> FindAllOffsets(byte[] buffer, byte[] pattern)
            => PatternHelper.FindAll(buffer, pattern);

        private static int FindPattern(byte[] buffer, byte[] pattern, int startOffset, int endExclusive)
            => PatternHelper.FindPattern(buffer, pattern, startOffset, endExclusive);

        private static byte[] EncodeUtf16Le(string text)
        {
            return Encoding.Unicode.GetBytes(text + '\0');
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);
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

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(ReadSpan(buffer, offset, sizeof(int)));
        }

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

    }
}
