using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum SetCommandPatchApplyResult
    {
        NotApplicable,
        AlreadyPatched,
        Patched
    }

    public static class SetCommandPatchHelper
    {
        private const string DlcVersion = "1.1.0.0";

        private const uint ParseToken = 0x010DE130;
        private const uint FindObjectUClass = 0x00409850;
        private const uint FindField = 0x0083A8D0;
        private const uint GlobalSetProperty = 0x0110F5D0;
        private const uint LogWarning = 0x010E19D0;
        private const uint StaticExecEpilog = 0x0110C0E9;

        private const uint StringUnrecognizedProperty = 0x01CD177C;
        private const uint StringUnrecognizedClass = 0x01CD17B0;
        private const uint NameExecWarning = 0x300;

        private const uint JneSetVa = 0x0110BC9F;
        private const uint JneSetNoPecVa = 0x0110BCB9;
        private const uint OriginalTargetVa = 0x0110B671;

        private const uint LegacyCaveVa = 0x01A916E0;
        private const int ExpectedPerformSetSize = 241;
        private const int ExpectedTrampolineSize = 26;
        private const int TotalPayloadSize = ExpectedPerformSetSize + ExpectedTrampolineSize * 2;

        public static bool IsDlcVersion(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            try
            {
                string? fileVersion = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                return !string.IsNullOrWhiteSpace(fileVersion)
                    && fileVersion.StartsWith(DlcVersion, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static SetCommandPatchApplyResult EnsurePatchedIfApplicable(string exePath)
        {
            if (!IsDlcVersion(exePath))
                return SetCommandPatchApplyResult.NotApplicable;

            byte[] buffer = File.ReadAllBytes(exePath);
            var image = PeImageLayout.Parse(buffer);

            SetCommandPatchState state = GetPatchState(buffer, image);
            if (state == SetCommandPatchState.Patched || state == SetCommandPatchState.LegacyPatched)
                return SetCommandPatchApplyResult.AlreadyPatched;

            if (state != SetCommandPatchState.Unpatched)
                throw new InvalidOperationException("Unsupported DLC executable revision for set-command patching.");

            var cave = CaveSection.Open(buffer, versionTag: "dlc");
            uint performSetVa = cave.Alloc(TotalPayloadSize);
            byte[] performSetCode = BuildPerformSetCommand(performSetVa);
            if (performSetCode.Length != ExpectedPerformSetSize)
                throw new InvalidOperationException($"Unexpected PerformSetCommand payload size ({performSetCode.Length}).");

            uint trampolineSetVa = checked(performSetVa + (uint)performSetCode.Length);
            byte[] trampolineSetCode = BuildTrampoline(trampolineSetVa, performSetVa, bNotifyObjectOfChange: true, StaticExecEpilog);
            if (trampolineSetCode.Length != ExpectedTrampolineSize)
                throw new InvalidOperationException($"Unexpected SET trampoline payload size ({trampolineSetCode.Length}).");

            uint trampolineSetNoPecVa = checked(trampolineSetVa + (uint)trampolineSetCode.Length);
            byte[] trampolineSetNoPecCode = BuildTrampoline(trampolineSetNoPecVa, performSetVa, bNotifyObjectOfChange: false, StaticExecEpilog);
            if (trampolineSetNoPecCode.Length != ExpectedTrampolineSize)
                throw new InvalidOperationException($"Unexpected SETNOPEC trampoline payload size ({trampolineSetNoPecCode.Length}).");

            byte[] payload = new byte[performSetCode.Length + trampolineSetCode.Length + trampolineSetNoPecCode.Length];
            Buffer.BlockCopy(performSetCode, 0, payload, 0, performSetCode.Length);
            Buffer.BlockCopy(trampolineSetCode, 0, payload, performSetCode.Length, trampolineSetCode.Length);
            Buffer.BlockCopy(trampolineSetNoPecCode, 0, payload, performSetCode.Length + trampolineSetCode.Length, trampolineSetNoPecCode.Length);

            cave.Write(performSetVa, payload);
            buffer = cave.Finalize();

            image = PeImageLayout.Parse(buffer);
            image.WriteAtVa(JneSetVa, BuildJneBytes(JneSetVa, trampolineSetVa));
            image.WriteAtVa(JneSetNoPecVa, BuildJneBytes(JneSetNoPecVa, trampolineSetNoPecVa));

            WriteAllBytesPreservingAttributes(exePath, buffer);
            return SetCommandPatchApplyResult.Patched;
        }

        private static SetCommandPatchState GetPatchState(byte[] buffer, PeImageLayout image)
        {
            byte[] setJne = image.ReadAtVa(JneSetVa, 6);
            byte[] setNoPecJne = image.ReadAtVa(JneSetNoPecVa, 6);

            if (!(setJne[0] == 0x0F && setJne[1] == 0x85) ||
                !(setNoPecJne[0] == 0x0F && setNoPecJne[1] == 0x85))
                return SetCommandPatchState.Unknown;

            uint setTarget = DecodeJccRel32Target(JneSetVa, setJne);
            uint setNoPecTarget = DecodeJccRel32Target(JneSetNoPecVa, setNoPecJne);

            if (setTarget == OriginalTargetVa && setNoPecTarget == OriginalTargetVa)
                return SetCommandPatchState.Unpatched;

            if (setTarget >= LegacyCaveVa && setTarget < LegacyCaveVa + 2336 &&
                setNoPecTarget >= LegacyCaveVa && setNoPecTarget < LegacyCaveVa + 2336)
                return SetCommandPatchState.LegacyPatched;

            if (setTarget != OriginalTargetVa && setNoPecTarget != OriginalTargetVa)
                return SetCommandPatchState.Patched;

            return SetCommandPatchState.Unknown;
        }

        private static byte[] BuildPerformSetCommand(uint baseVa)
        {
            var builder = new MachineCodeBuilder(baseVa);

            builder.Emit(0x55);
            builder.Emit(new byte[] { 0x8B, 0xEC });
            builder.Emit(new byte[] { 0x81, 0xEC, 0x04, 0x04, 0x00, 0x00 });
            builder.Emit(0x56);
            builder.Emit(0x57);

            builder.Emit(new byte[] { 0x8B, 0x45, 0x08 });
            builder.Emit(new byte[] { 0x89, 0x85, 0xFC, 0xFB, 0xFF, 0xFF });

            builder.Emit(new byte[] { 0x6A, 0x01 });
            builder.Emit(new byte[] { 0x68, 0x00, 0x01, 0x00, 0x00 });
            builder.Emit(new byte[] { 0x8D, 0x8D, 0x00, 0xFE, 0xFF, 0xFF });
            builder.Emit(0x51);
            builder.Emit(new byte[] { 0x8D, 0x95, 0xFC, 0xFB, 0xFF, 0xFF });
            builder.Emit(0x52);
            builder.EmitCall(ParseToken);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x10 });
            builder.Emit(new byte[] { 0x85, 0xC0 });
            builder.EmitJz("error_class");

            builder.Emit(new byte[] { 0x6A, 0x00 });
            builder.Emit(new byte[] { 0x8D, 0x8D, 0x00, 0xFE, 0xFF, 0xFF });
            builder.Emit(0x51);
            builder.Emit(new byte[] { 0x6A, 0xFF });
            builder.EmitCall(FindObjectUClass);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x0C });
            builder.Emit(new byte[] { 0x8B, 0xF0 });
            builder.Emit(new byte[] { 0x85, 0xF6 });
            builder.EmitJz("error_class");

            builder.Emit(new byte[] { 0x6A, 0x01 });
            builder.Emit(new byte[] { 0x68, 0x00, 0x01, 0x00, 0x00 });
            builder.Emit(new byte[] { 0x8D, 0x8D, 0x00, 0xFC, 0xFF, 0xFF });
            builder.Emit(0x51);
            builder.Emit(new byte[] { 0x8D, 0x95, 0xFC, 0xFB, 0xFF, 0xFF });
            builder.Emit(0x52);
            builder.EmitCall(ParseToken);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x10 });
            builder.Emit(new byte[] { 0x85, 0xC0 });
            builder.EmitJz("error_property");

            builder.Emit(new byte[] { 0x8D, 0x8D, 0x00, 0xFC, 0xFF, 0xFF });
            builder.Emit(0x51);
            builder.Emit(0x56);
            builder.EmitCall(FindField);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x08 });
            builder.Emit(new byte[] { 0x85, 0xC0 });
            builder.EmitJz("error_property");
            builder.Emit(new byte[] { 0x8B, 0xF8 });

            builder.Emit(new byte[] { 0x8B, 0x8D, 0xFC, 0xFB, 0xFF, 0xFF });
            int spaceLoopOffset = builder.CurrentOffset;
            builder.Emit(new byte[] { 0x66, 0x83, 0x39, 0x20 });
            builder.Emit(new byte[] { 0x75, 0x04 });
            builder.Emit(new byte[] { 0x83, 0xC1, 0x02 });
            int jumpBackOpcodeOffset = builder.CurrentOffset;
            builder.Emit(0xEB);
            int shortRel = checked(spaceLoopOffset - (jumpBackOpcodeOffset + 2));
            builder.Emit(unchecked((byte)shortRel));

            builder.Emit(new byte[] { 0x8B, 0x55, 0x0C });
            builder.Emit(0x52);
            builder.Emit(new byte[] { 0x8B, 0x57, 0x64 });
            builder.Emit(0x52);
            builder.Emit(0x57);
            builder.Emit(0x56);
            builder.Emit(0x51);
            builder.EmitCall(GlobalSetProperty);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x14 });
            builder.EmitJmp("done");

            builder.MarkLabel("error_property");
            builder.Emit(new byte[] { 0x8D, 0x85, 0x00, 0xFC, 0xFF, 0xFF });
            builder.Emit(0x50);
            builder.Emit(0x68);
            builder.EmitUInt32(StringUnrecognizedProperty);
            builder.EmitJmp("do_log");

            builder.MarkLabel("error_class");
            builder.Emit(new byte[] { 0x8D, 0x85, 0x00, 0xFE, 0xFF, 0xFF });
            builder.Emit(0x50);
            builder.Emit(0x68);
            builder.EmitUInt32(StringUnrecognizedClass);

            builder.MarkLabel("do_log");
            builder.Emit(0x68);
            builder.EmitUInt32(NameExecWarning);
            builder.Emit(new byte[] { 0xFF, 0x75, 0x10 });
            builder.EmitCall(LogWarning);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x10 });

            builder.MarkLabel("done");
            builder.Emit(0x5F);
            builder.Emit(0x5E);
            builder.Emit(new byte[] { 0x8B, 0xE5 });
            builder.Emit(0x5D);
            builder.Emit(0xC3);

            return builder.Build();
        }

        private static byte[] BuildTrampoline(uint baseVa, uint performSetVa, bool bNotifyObjectOfChange, uint epilogVa)
        {
            var builder = new MachineCodeBuilder(baseVa);

            builder.Emit(new byte[] { 0x8B, 0x44, 0x24, 0x14 });
            builder.Emit(0x56);
            builder.Emit(bNotifyObjectOfChange ? new byte[] { 0x6A, 0x01 } : new byte[] { 0x6A, 0x00 });
            builder.Emit(0x50);
            builder.EmitCall(performSetVa);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x0C });
            builder.Emit(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00 });
            builder.EmitJmpNear(epilogVa);

            return builder.Build();
        }

        private static byte[] BuildJneBytes(uint instructionVa, uint targetVa)
        {
            int rel = checked((int)((long)targetVa - (instructionVa + 6)));
            byte[] bytes = new byte[6];
            bytes[0] = 0x0F;
            bytes[1] = 0x85;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), rel);
            return bytes;
        }

        private static uint DecodeJccRel32Target(uint instructionVa, byte[] instruction)
        {
            int rel = BinaryPrimitives.ReadInt32LittleEndian(instruction.AsSpan(2, 4));
            return checked((uint)((long)instructionVa + 6 + rel));
        }

        private static void WriteAllBytesPreservingAttributes(string path, byte[] content) => PatchUtility.WritePreservingAttributes(path, content);

        private enum SetCommandPatchState
        {
            Unknown,
            Unpatched,
            Patched,
            LegacyPatched
        }
    }
}
