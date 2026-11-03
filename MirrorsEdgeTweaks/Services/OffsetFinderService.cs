using System.IO;
using UELib;
using UELib.Core;
using UELib.Flags;
using static UELib.Core.UStruct.UByteCodeDecompiler;

namespace MirrorsEdgeTweaks.Services
{
    public interface IOffsetFinderService
    {
        long FindPropertyOffsetByName(UObject owner, string propertyName, float expectedValue, UnrealPackage package, string? packagePath);
        long FindFloatOffsetInBytecode(UFunction function, UProperty propertyToFind);
        long FindConsoleHeightOffset(UFunction function);
        long FindClippingPlaneOffset(UFunction function);
        long FindFovScaleMultiplierOffset(UFunction function);
        long FindPropertyFlagsOffset(UProperty property, UnrealPackage package, string? packagePath);
        int FindPattern(byte[] source, byte[] pattern);
        float ReadFloatFromFile(string path, long offset);
    }

    public class OffsetFinderService : IOffsetFinderService
    {
        public long FindPropertyOffsetByName(UObject owner, string propertyName, float expectedValue, UnrealPackage package, string? packagePath)
        {
            if (packagePath == null || owner.ExportTable == null) return -1;

            int nameIndex = package.Names.FindIndex(n => n.ToString() == propertyName);
            if (nameIndex == -1) return -1;

            byte[] nameIndexBytes = BitConverter.GetBytes(nameIndex);
            byte[] valueBytes = BitConverter.GetBytes(expectedValue);

            using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(stream))
            {
                long searchStart = owner.ExportTable.SerialOffset;
                long searchEnd = searchStart + owner.ExportTable.SerialSize;
                stream.Position = searchStart;

                for (long i = searchStart; i < searchEnd - nameIndexBytes.Length; i++)
                {
                    stream.Position = i;
                    byte[] buffer = reader.ReadBytes(nameIndexBytes.Length);

                    if (buffer.SequenceEqual(nameIndexBytes))
                    {
                        const int searchWindow = 24;
                        long valueSearchStart = i + nameIndexBytes.Length;

                        for (long j = valueSearchStart; j < valueSearchStart + searchWindow && j < searchEnd - valueBytes.Length; j++)
                        {
                            stream.Position = j;
                            byte[] valueBuffer = reader.ReadBytes(valueBytes.Length);
                            if (valueBuffer.SequenceEqual(valueBytes))
                            {
                                return j;
                            }
                        }
                    }
                }
            }
            return -1;
        }

        public long FindFloatOffsetInBytecode(UFunction function, UProperty propertyToFind)
        {
            if (function.ByteCodeManager == null || function.ExportTable == null) return -1;

            function.ByteCodeManager.Deserialize();
            var tokens = function.ByteCodeManager.DeserializedTokens;

            for (int i = 0; i < tokens.Count - 2; i++)
            {
                if (tokens[i] is LetToken &&
                  tokens[i + 1] is InstanceVariableToken instVarToken &&
                  tokens[i + 2] is FloatConstToken floatToken)
                {
                    if (instVarToken.Object != null && string.Equals(instVarToken.Object.Name?.ToString(), propertyToFind.Name?.ToString()))
                    {
                        return function.ExportTable.SerialOffset + function.ScriptOffset + floatToken.StoragePosition + 1;
                    }
                }
            }
            return -1;
        }

        public long FindConsoleHeightOffset(UFunction function)
        {
            if (function.ByteCodeManager == null || function.ExportTable == null) return -1;

            function.ByteCodeManager.Deserialize();
            var tokens = function.ByteCodeManager.DeserializedTokens;

            for (int i = 0; i < tokens.Count - 2; i++)
            {
                if (tokens[i] is LetToken &&
                    tokens[i + 1] is LocalVariableToken localToken &&
                    localToken.Object?.Name.ToString() == "Height")
                {
                    for (int j = i + 2; j < tokens.Count; j++)
                    {
                        var expressionToken = tokens[j];

                        if (expressionToken is FloatConstToken floatToken)
                        {
                            return function.ExportTable.SerialOffset + function.ScriptOffset + floatToken.StoragePosition + 1;
                        }

                        if (expressionToken is LetToken ||
                            expressionToken is ReturnToken ||
                            expressionToken is JumpToken ||
                            expressionToken is JumpIfNotToken ||
                            expressionToken is SwitchToken ||
                            expressionToken is IteratorToken ||
                            expressionToken is EndOfScriptToken)
                        {
                            break;
                        }
                    }
                    break;
                }
            }

            return -1;
        }

        public long FindClippingPlaneOffset(UFunction function)
        {
            if (function.ByteCodeManager == null || function.ExportTable == null) return -1;

            function.ByteCodeManager.Deserialize();
            var tokens = function.ByteCodeManager.DeserializedTokens;

            var functionCallIndices = new List<int>();
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                string functionName = string.Empty;

                if (token is FinalFunctionToken ffToken && ffToken.Function != null) functionName = ffToken.Function.Name?.ToString() ?? string.Empty;
                else if (token is VirtualFunctionToken vfToken) functionName = vfToken.FunctionName?.ToString() ?? string.Empty;

                if (functionName == "SetNearClippingPlane")
                {
                    functionCallIndices.Add(i);
                }
            }

            if (functionCallIndices.Count < 2) return -1;
            int secondCallIndex = functionCallIndices[1];

            for (int i = secondCallIndex + 1; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token is FloatConstToken floatToken)
                {
                    return function.ExportTable.SerialOffset + function.ScriptOffset + floatToken.StoragePosition + 1;
                }
                if (token is IntConstToken intToken)
                {
                    return function.ExportTable.SerialOffset + function.ScriptOffset + intToken.StoragePosition + 1;
                }
                if (token is EndFunctionParmsToken)
                {
                    break;
                }
            }
            return -1;
        }

        public long FindFovScaleMultiplierOffset(UFunction function)
        {
            if (function.ByteCodeManager == null || function.ExportTable == null)
            {
                return -1;
            }

            function.ByteCodeManager.Deserialize();
            var tokens = function.ByteCodeManager.DeserializedTokens;

            for (int i = 0; i < tokens.Count - 1; i++)
            {
                string? varName = null;
                var varTokenCandidate = tokens[i + 1];

                if (varTokenCandidate is InstanceVariableToken instToken)
                {
                    varName = instToken.Object?.Name?.ToString();
                }
                else if (varTokenCandidate is LocalVariableToken localToken)
                {
                    varName = localToken.Object?.Name?.ToString();
                }

                if (tokens[i] is LetToken && varName == "FOVScale")
                {
                    for (int j = i + 2; j < tokens.Count; j++)
                    {
                        var currentToken = tokens[j];

                        if (currentToken is FloatConstToken floatToken)
                        {
                            return function.ExportTable.SerialOffset + function.ScriptOffset + floatToken.StoragePosition + 1;
                        }

                        if (currentToken is LetToken || currentToken is ReturnToken || currentToken is JumpToken || currentToken is JumpIfNotToken || currentToken is SwitchToken || currentToken is EndOfScriptToken)
                        {
                            break;
                        }
                    }
                }
            }

            return -1;
        }

        public long FindPropertyFlagsOffset(UProperty property, UnrealPackage package, string? packagePath)
        {
            if (packagePath == null || property.ExportTable == null) return -1;

            ulong flagsFromObject = property.PropertyFlags;
            ulong configFlagBitmask = property.PropertyFlags.GetFlag(PropertyFlag.Config);
            ulong toggledFlags = flagsFromObject ^ configFlagBitmask;

            byte[] originalFlagBytes = BitConverter.GetBytes(flagsFromObject);
            byte[] toggledFlagBytes = BitConverter.GetBytes(toggledFlags);

            using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(stream))
            {
                long searchStart = property.ExportTable.SerialOffset;
                long searchEnd = searchStart + property.ExportTable.SerialSize;

                for (long i = searchStart; i <= searchEnd - originalFlagBytes.Length; i++)
                {
                    stream.Position = i;
                    byte[] buffer = reader.ReadBytes(originalFlagBytes.Length);
                    if (buffer.SequenceEqual(originalFlagBytes))
                    {
                        return i;
                    }
                }

                for (long i = searchStart; i <= searchEnd - toggledFlagBytes.Length; i++)
                {
                    stream.Position = i;
                    byte[] buffer = reader.ReadBytes(toggledFlagBytes.Length);
                    if (buffer.SequenceEqual(toggledFlagBytes))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        public int FindPattern(byte[] source, byte[] pattern)
            => Helpers.PatternHelper.FindPattern(source, pattern);

        // Returns 0 when the file cannot be read at the offset.
        public float ReadFloatFromFile(string path, long offset)
        {
            if (offset < 0) return 0f;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Position = offset;
                Span<byte> buffer = stackalloc byte[sizeof(float)];
                stream.ReadExactly(buffer);
                return BitConverter.ToSingle(buffer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0f;
            }
        }
    }
}
