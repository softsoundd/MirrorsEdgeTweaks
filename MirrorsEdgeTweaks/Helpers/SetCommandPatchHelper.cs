using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

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

        private const uint CodeCaveVa = 0x01A916E0;
        private const int CodeCaveAvailableBytes = 2336;
        private const int ExpectedPerformSetSize = 241;
        private const int ExpectedTrampolineSize = 26;

        public static bool IsDlcVersion(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return false;
            }

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
            {
                return SetCommandPatchApplyResult.NotApplicable;
            }

            byte[] originalBuffer = File.ReadAllBytes(exePath);
            byte[] buffer = (byte[])originalBuffer.Clone();
            ExecutableImageLayout image = ExecutableImageLayout.Parse(buffer);

            PatchPayload payload = BuildPatchPayload();
            SetCommandPatchState state = GetPatchState(buffer, image, payload);
            if (state == SetCommandPatchState.Patched)
            {
                return SetCommandPatchApplyResult.AlreadyPatched;
            }

            if (state != SetCommandPatchState.Unpatched)
            {
                throw new InvalidOperationException("Unsupported DLC executable revision for set-command patching.");
            }

            byte[] caveBytes = ReadAtVa(buffer, image, CodeCaveVa, payload.Bytes.Length);
            bool caveIsZeroed = caveBytes.All(b => b == 0);
            bool caveAlreadyContainsPayload = caveBytes.SequenceEqual(payload.Bytes);
            if (!caveIsZeroed && !caveAlreadyContainsPayload)
            {
                throw new InvalidDataException("Unable to apply set-command patch: expected code cave is not empty.");
            }

            WriteAtVa(buffer, image, payload.PerformSetVa, payload.PerformSetCode);
            WriteAtVa(buffer, image, payload.TrampolineSetVa, payload.TrampolineSetCode);
            WriteAtVa(buffer, image, payload.TrampolineSetNoPecVa, payload.TrampolineSetNoPecCode);
            WriteAtVa(buffer, image, JneSetVa, BuildJneBytes(JneSetVa, payload.TrampolineSetVa));
            WriteAtVa(buffer, image, JneSetNoPecVa, BuildJneBytes(JneSetNoPecVa, payload.TrampolineSetNoPecVa));

            WriteAllBytesPreservingAttributes(exePath, buffer);
            return SetCommandPatchApplyResult.Patched;
        }

        private static SetCommandPatchState GetPatchState(byte[] buffer, ExecutableImageLayout image, PatchPayload payload)
        {
            byte[] setJne = ReadAtVa(buffer, image, JneSetVa, 6);
            byte[] setNoPecJne = ReadAtVa(buffer, image, JneSetNoPecVa, 6);

            if (!(setJne[0] == 0x0F && setJne[1] == 0x85) ||
                !(setNoPecJne[0] == 0x0F && setNoPecJne[1] == 0x85))
            {
                return SetCommandPatchState.Unknown;
            }

            uint setTarget = DecodeJccRel32Target(JneSetVa, setJne);
            uint setNoPecTarget = DecodeJccRel32Target(JneSetNoPecVa, setNoPecJne);

            if (setTarget == OriginalTargetVa && setNoPecTarget == OriginalTargetVa)
            {
                return SetCommandPatchState.Unpatched;
            }

            if (setTarget == payload.TrampolineSetVa && setNoPecTarget == payload.TrampolineSetNoPecVa)
            {
                byte[] caveBytes = ReadAtVa(buffer, image, CodeCaveVa, payload.Bytes.Length);
                return caveBytes.SequenceEqual(payload.Bytes)
                    ? SetCommandPatchState.Patched
                    : SetCommandPatchState.Unknown;
            }

            return SetCommandPatchState.Unknown;
        }

        private static PatchPayload BuildPatchPayload()
        {
            uint performSetVa = CodeCaveVa;
            byte[] performSetCode = BuildPerformSetCommand(performSetVa);
            if (performSetCode.Length != ExpectedPerformSetSize)
            {
                throw new InvalidOperationException($"Unexpected PerformSetCommand payload size ({performSetCode.Length}).");
            }

            uint trampolineSetVa = checked(performSetVa + (uint)performSetCode.Length);
            byte[] trampolineSetCode = BuildTrampoline(trampolineSetVa, performSetVa, bNotifyObjectOfChange: true, StaticExecEpilog);
            if (trampolineSetCode.Length != ExpectedTrampolineSize)
            {
                throw new InvalidOperationException($"Unexpected SET trampoline payload size ({trampolineSetCode.Length}).");
            }

            uint trampolineSetNoPecVa = checked(trampolineSetVa + (uint)trampolineSetCode.Length);
            byte[] trampolineSetNoPecCode = BuildTrampoline(trampolineSetNoPecVa, performSetVa, bNotifyObjectOfChange: false, StaticExecEpilog);
            if (trampolineSetNoPecCode.Length != ExpectedTrampolineSize)
            {
                throw new InvalidOperationException($"Unexpected SETNOPEC trampoline payload size ({trampolineSetNoPecCode.Length}).");
            }

            int totalSize = checked(performSetCode.Length + trampolineSetCode.Length + trampolineSetNoPecCode.Length);
            if (totalSize > CodeCaveAvailableBytes)
            {
                throw new InvalidOperationException("Set-command patch payload exceeds the available DLC code cave.");
            }

            byte[] payloadBytes = new byte[totalSize];
            Buffer.BlockCopy(performSetCode, 0, payloadBytes, 0, performSetCode.Length);
            Buffer.BlockCopy(trampolineSetCode, 0, payloadBytes, performSetCode.Length, trampolineSetCode.Length);
            Buffer.BlockCopy(trampolineSetNoPecCode, 0, payloadBytes, performSetCode.Length + trampolineSetCode.Length, trampolineSetNoPecCode.Length);

            return new PatchPayload(
                payloadBytes,
                performSetVa,
                performSetCode,
                trampolineSetVa,
                trampolineSetCode,
                trampolineSetNoPecVa,
                trampolineSetNoPecCode);
        }

        private static byte[] BuildPerformSetCommand(uint baseVa)
        {
            MachineCodeBuilder builder = new MachineCodeBuilder(baseVa);

            // Prologue
            builder.Emit(0x55);                                    // push ebp
            builder.Emit(new byte[] { 0x8B, 0xEC });               // mov ebp, esp
            builder.Emit(new byte[] { 0x81, 0xEC, 0x04, 0x04, 0x00, 0x00 }); // sub esp, 0x404
            builder.Emit(0x56);                                    // push esi
            builder.Emit(0x57);                                    // push edi

            builder.Emit(new byte[] { 0x8B, 0x45, 0x08 });         // mov eax, [ebp+8]
            builder.Emit(new byte[] { 0x89, 0x85, 0xFC, 0xFB, 0xFF, 0xFF }); // mov [ebp-0x404], eax

            // ParseToken #1: class name
            builder.Emit(new byte[] { 0x6A, 0x01 });               // push 1
            builder.Emit(new byte[] { 0x68, 0x00, 0x01, 0x00, 0x00 }); // push 0x100
            builder.Emit(new byte[] { 0x8D, 0x8D, 0x00, 0xFE, 0xFF, 0xFF }); // lea ecx, [ebp-0x200]
            builder.Emit(0x51);                                    // push ecx
            builder.Emit(new byte[] { 0x8D, 0x95, 0xFC, 0xFB, 0xFF, 0xFF }); // lea edx, [ebp-0x404]
            builder.Emit(0x52);                                    // push edx
            builder.EmitCall(ParseToken);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x10 });         // add esp, 0x10
            builder.Emit(new byte[] { 0x85, 0xC0 });               // test eax, eax
            builder.EmitJz("error_class");

            // FindObject<UClass>(ANY_PACKAGE, ClassName, FALSE)
            builder.Emit(new byte[] { 0x6A, 0x00 });               // push 0
            builder.Emit(new byte[] { 0x8D, 0x8D, 0x00, 0xFE, 0xFF, 0xFF }); // lea ecx, [ebp-0x200]
            builder.Emit(0x51);                                    // push ecx
            builder.Emit(new byte[] { 0x6A, 0xFF });               // push -1
            builder.EmitCall(FindObjectUClass);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x0C });         // add esp, 0xC
            builder.Emit(new byte[] { 0x8B, 0xF0 });               // mov esi, eax
            builder.Emit(new byte[] { 0x85, 0xF6 });               // test esi, esi
            builder.EmitJz("error_class");

            // ParseToken #2: property name
            builder.Emit(new byte[] { 0x6A, 0x01 });
            builder.Emit(new byte[] { 0x68, 0x00, 0x01, 0x00, 0x00 });
            builder.Emit(new byte[] { 0x8D, 0x8D, 0x00, 0xFC, 0xFF, 0xFF }); // lea ecx, [ebp-0x400]
            builder.Emit(0x51);
            builder.Emit(new byte[] { 0x8D, 0x95, 0xFC, 0xFB, 0xFF, 0xFF });
            builder.Emit(0x52);
            builder.EmitCall(ParseToken);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x10 });
            builder.Emit(new byte[] { 0x85, 0xC0 });
            builder.EmitJz("error_property");

            // FindField<UProperty>(Class, PropertyName)
            builder.Emit(new byte[] { 0x8D, 0x8D, 0x00, 0xFC, 0xFF, 0xFF });
            builder.Emit(0x51);
            builder.Emit(0x56);
            builder.EmitCall(FindField);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x08 });
            builder.Emit(new byte[] { 0x85, 0xC0 });
            builder.EmitJz("error_property");
            builder.Emit(new byte[] { 0x8B, 0xF8 });               // mov edi, eax

            // Skip leading spaces in Str
            builder.Emit(new byte[] { 0x8B, 0x8D, 0xFC, 0xFB, 0xFF, 0xFF }); // mov ecx, [ebp-0x404]
            int spaceLoopOffset = builder.CurrentOffset;
            builder.Emit(new byte[] { 0x66, 0x83, 0x39, 0x20 });   // cmp word [ecx], 0x20
            builder.Emit(new byte[] { 0x75, 0x04 });               // jne +4
            builder.Emit(new byte[] { 0x83, 0xC1, 0x02 });         // add ecx, 2
            int jumpBackOpcodeOffset = builder.CurrentOffset;
            builder.Emit(0xEB);                                    // jmp rel8
            int shortRel = checked(spaceLoopOffset - (jumpBackOpcodeOffset + 2));
            builder.Emit(unchecked((byte)shortRel));

            // GlobalSetProperty(Value, Class, Property, Offset, bNotify)
            builder.Emit(new byte[] { 0x8B, 0x55, 0x0C });         // mov edx, [ebp+0xC]
            builder.Emit(0x52);
            builder.Emit(new byte[] { 0x8B, 0x57, 0x64 });         // mov edx, [edi+0x64]
            builder.Emit(0x52);
            builder.Emit(0x57);
            builder.Emit(0x56);
            builder.Emit(0x51);
            builder.EmitCall(GlobalSetProperty);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x14 });
            builder.EmitJmp("done");

            // error_property: "Unrecognized property %s"
            builder.MarkLabel("error_property");
            builder.Emit(new byte[] { 0x8D, 0x85, 0x00, 0xFC, 0xFF, 0xFF }); // lea eax, [ebp-0x400]
            builder.Emit(0x50);
            builder.Emit(0x68);
            builder.EmitUInt32(StringUnrecognizedProperty);
            builder.EmitJmp("do_log");

            // error_class: "Unrecognized class %s"
            builder.MarkLabel("error_class");
            builder.Emit(new byte[] { 0x8D, 0x85, 0x00, 0xFE, 0xFF, 0xFF }); // lea eax, [ebp-0x200]
            builder.Emit(0x50);
            builder.Emit(0x68);
            builder.EmitUInt32(StringUnrecognizedClass);

            // do_log: LogWarning(Ar, NAME_ExecWarning, fmt, token)
            builder.MarkLabel("do_log");
            builder.Emit(0x68);
            builder.EmitUInt32(NameExecWarning);
            builder.Emit(new byte[] { 0xFF, 0x75, 0x10 });         // push [ebp+0x10]
            builder.EmitCall(LogWarning);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x10 });

            // Epilogue
            builder.MarkLabel("done");
            builder.Emit(0x5F);                                    // pop edi
            builder.Emit(0x5E);                                    // pop esi
            builder.Emit(new byte[] { 0x8B, 0xE5 });               // mov esp, ebp
            builder.Emit(0x5D);                                    // pop ebp
            builder.Emit(0xC3);                                    // ret

            return builder.Build();
        }

        private static byte[] BuildTrampoline(uint baseVa, uint performSetVa, bool bNotifyObjectOfChange, uint epilogVa)
        {
            MachineCodeBuilder builder = new MachineCodeBuilder(baseVa);

            builder.Emit(new byte[] { 0x8B, 0x44, 0x24, 0x14 }); // mov eax, [esp+0x14] (Str)
            builder.Emit(0x56);                                  // push esi (Ar)
            builder.Emit(bNotifyObjectOfChange ? new byte[] { 0x6A, 0x01 } : new byte[] { 0x6A, 0x00 });
            builder.Emit(0x50);                                  // push eax (Str)
            builder.EmitCall(performSetVa);
            builder.Emit(new byte[] { 0x83, 0xC4, 0x0C });       // add esp, 0xC
            builder.Emit(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00 }); // mov eax, 1
            builder.EmitJmpAbsolute(epilogVa);

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

        private static byte[] ReadAtVa(byte[] buffer, ExecutableImageLayout image, uint va, int length)
        {
            int offset = image.VaToOffset(va);
            if (offset < 0 || offset + length > buffer.Length)
            {
                throw new InvalidDataException($"VA 0x{va:X8} points outside the executable image.");
            }

            byte[] data = new byte[length];
            Buffer.BlockCopy(buffer, offset, data, 0, length);
            return data;
        }

        private static void WriteAtVa(byte[] buffer, ExecutableImageLayout image, uint va, byte[] payload)
        {
            int offset = image.VaToOffset(va);
            if (offset < 0 || offset + payload.Length > buffer.Length)
            {
                throw new InvalidDataException($"VA 0x{va:X8} points outside the executable image.");
            }

            Buffer.BlockCopy(payload, 0, buffer, offset, payload.Length);
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

        private enum SetCommandPatchState
        {
            Unknown,
            Unpatched,
            Patched
        }

        private readonly record struct PatchPayload(
            byte[] Bytes,
            uint PerformSetVa,
            byte[] PerformSetCode,
            uint TrampolineSetVa,
            byte[] TrampolineSetCode,
            uint TrampolineSetNoPecVa,
            byte[] TrampolineSetNoPecCode);

        private sealed class MachineCodeBuilder
        {
            private readonly uint _baseVa;
            private readonly List<byte> _code = new List<byte>();
            private readonly List<PendingJump> _pendingJumps = new List<PendingJump>();
            private readonly Dictionary<string, int> _labels = new Dictionary<string, int>(StringComparer.Ordinal);

            public MachineCodeBuilder(uint baseVa)
            {
                _baseVa = baseVa;
            }

            public int CurrentOffset => _code.Count;
            private uint CurrentVa => checked(_baseVa + (uint)_code.Count);

            public void Emit(byte value) => _code.Add(value);

            public void Emit(byte[] values) => _code.AddRange(values);

            public void EmitUInt32(uint value)
            {
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
                _code.AddRange(bytes.ToArray());
            }

            public void EmitCall(uint targetVa)
            {
                uint opcodeVa = CurrentVa;
                Emit(0xE8);
                int rel = checked((int)((long)targetVa - (opcodeVa + 5)));
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, rel);
                _code.AddRange(bytes.ToArray());
            }

            public void EmitJz(string label)
            {
                int position = _code.Count;
                Emit(new byte[] { 0x0F, 0x84, 0x00, 0x00, 0x00, 0x00 });
                _pendingJumps.Add(new PendingJump(position, JumpType.Jz, label));
            }

            public void EmitJmp(string label)
            {
                int position = _code.Count;
                Emit(new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00 });
                _pendingJumps.Add(new PendingJump(position, JumpType.Jmp, label));
            }

            public void EmitJmpAbsolute(uint targetVa)
            {
                uint opcodeVa = CurrentVa;
                Emit(0xE9);
                int rel = checked((int)((long)targetVa - (opcodeVa + 5)));
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, rel);
                _code.AddRange(bytes.ToArray());
            }

            public void MarkLabel(string name)
            {
                _labels[name] = _code.Count;
            }

            public byte[] Build()
            {
                foreach (PendingJump jump in _pendingJumps)
                {
                    if (!_labels.TryGetValue(jump.Label, out int targetOffset))
                    {
                        throw new InvalidOperationException($"Unknown machine-code label '{jump.Label}'.");
                    }

                    int fromEnd = jump.Type switch
                    {
                        JumpType.Jz => checked(jump.Position + 6),
                        JumpType.Jmp => checked(jump.Position + 5),
                        _ => throw new InvalidOperationException("Unsupported jump type.")
                    };

                    int rel = checked(targetOffset - fromEnd);
                    byte[] relBytes = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(relBytes, rel);

                    if (jump.Type == JumpType.Jz)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            _code[jump.Position + 2 + i] = relBytes[i];
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            _code[jump.Position + 1 + i] = relBytes[i];
                        }
                    }
                }

                return _code.ToArray();
            }
        }

        private readonly record struct PendingJump(int Position, JumpType Type, string Label);

        private enum JumpType
        {
            Jz,
            Jmp
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
                    sections.Add(new SectionInfo(
                        ReadUInt32(buffer, sectionOffset + 12),
                        ReadUInt32(buffer, sectionOffset + 8),
                        ReadUInt32(buffer, sectionOffset + 20),
                        ReadUInt32(buffer, sectionOffset + 16)));
                }

                return new ExecutableImageLayout(imageBase, sections);
            }

            public int VaToOffset(uint va)
            {
                uint rva = checked(va - ImageBase);
                SectionInfo section = FindSectionByRva(rva);
                return checked((int)(section.PointerToRawData + (rva - section.VirtualAddress)));
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
        }

        private readonly struct SectionInfo
        {
            public SectionInfo(uint virtualAddress, uint virtualSize, uint pointerToRawData, uint sizeOfRawData)
            {
                VirtualAddress = virtualAddress;
                VirtualSize = virtualSize;
                PointerToRawData = pointerToRawData;
                SizeOfRawData = sizeOfRawData;
            }

            public uint VirtualAddress { get; }
            public uint VirtualSize { get; }
            public uint PointerToRawData { get; }
            public uint SizeOfRawData { get; }
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
    }
}
