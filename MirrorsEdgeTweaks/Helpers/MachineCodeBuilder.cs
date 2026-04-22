using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace MirrorsEdgeTweaks.Helpers
{
    public sealed class MachineCodeBuilder
    {
        private readonly uint _baseVa;
        private readonly List<byte> _code = new();
        private readonly List<PendingJump> _pendingJumps = new();
        private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);

        public MachineCodeBuilder(uint baseVa)
        {
            _baseVa = baseVa;
        }

        public int CurrentOffset => _code.Count;
        public uint CurrentVa => checked(_baseVa + (uint)_code.Count);

        public void Emit(byte value) => _code.Add(value);

        public void Emit(ReadOnlySpan<byte> values)
        {
            foreach (byte b in values)
                _code.Add(b);
        }

        public void Emit(byte[] values) => _code.AddRange(values);

        public void EmitUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _code.AddRange(bytes.ToArray());
        }

        public void EmitInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _code.AddRange(bytes.ToArray());
        }

        public void EmitNop(int count)
        {
            for (int i = 0; i < count; i++)
                _code.Add(0x90);
        }

        public void EmitCall(uint targetVa)
        {
            uint opcodeVa = CurrentVa;
            Emit(0xE8);
            int rel = checked((int)((long)targetVa - (opcodeVa + 5)));
            EmitInt32(rel);
        }

        public void EmitJmpNear(uint targetVa)
        {
            uint opcodeVa = CurrentVa;
            Emit(0xE9);
            int rel = checked((int)((long)targetVa - (opcodeVa + 5)));
            EmitInt32(rel);
        }

        public void EmitJz(string label)
        {
            int position = _code.Count;
            Emit(new byte[] { 0x0F, 0x84, 0x00, 0x00, 0x00, 0x00 });
            _pendingJumps.Add(new PendingJump(position, JumpKind.Jcc6, label));
        }

        public void EmitJnz(string label)
        {
            int position = _code.Count;
            Emit(new byte[] { 0x0F, 0x85, 0x00, 0x00, 0x00, 0x00 });
            _pendingJumps.Add(new PendingJump(position, JumpKind.Jcc6, label));
        }

        public void EmitJmp(string label)
        {
            int position = _code.Count;
            Emit(new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00 });
            _pendingJumps.Add(new PendingJump(position, JumpKind.Jmp5, label));
        }

        public void MarkLabel(string name)
        {
            _labels[name] = _code.Count;
        }

        public byte[] Build()
        {
            foreach (var jump in _pendingJumps)
            {
                if (!_labels.TryGetValue(jump.Label, out int targetOffset))
                    throw new InvalidOperationException($"Unknown machine-code label '{jump.Label}'.");

                int instrLen = jump.Kind == JumpKind.Jcc6 ? 6 : 5;
                int fromEnd = checked(jump.Position + instrLen);
                int rel = checked(targetOffset - fromEnd);

                byte[] relBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(relBytes, rel);

                int patchOffset = jump.Kind == JumpKind.Jcc6 ? jump.Position + 2 : jump.Position + 1;
                for (int i = 0; i < 4; i++)
                    _code[patchOffset + i] = relBytes[i];
            }

            return _code.ToArray();
        }

        public static byte[] Rel32Bytes(uint fromVa, uint toVa)
        {
            int rel = checked((int)((long)toVa - (fromVa + 5)));
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, rel);
            return bytes;
        }

        private readonly record struct PendingJump(int Position, JumpKind Kind, string Label);

        private enum JumpKind
        {
            Jcc6,
            Jmp5
        }
    }
}
