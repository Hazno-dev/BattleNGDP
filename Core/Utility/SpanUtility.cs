using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BattleNGDP.Core;

public static class SpanUtility
{
    private static bool IsWhitespace(byte value) =>
        value is (byte) ' ' or (byte) '\t' or (byte) '\r' or (byte) '\n';

    extension(ReadOnlySpan<byte> data)
    {
        public ReadOnlySpan<byte> TrimLine()
        {
            var start = 0;
            var end = data.Length;
            while (start < end && IsWhitespace(data[start])) {
                ++start;
            }

            while (end > start && IsWhitespace(data[end - 1])) {
                --end;
            }

            return data[start..end];
        }

        public bool IsBLTEEncoded()
        {
            if (data.Length < 4) {
                throw new ArgumentOutOfRangeException(nameof(data), "Data is too short to be a valid BLTE file.");
            }

            var head = Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(data[..4]));
            return head is 0x45544C42 or 0x424C5445;
        }

        public bool IsBLTEEncodedChunk()
        {
            if (data.Length < 1) {
                throw new ArgumentOutOfRangeException(nameof(data), "Data is too short to be a BLTE chunk.");
            }

            var b = data[0];
            return b is (byte) 'N' or (byte) 'Z' or (byte) 'E' or (byte) 'F' or (byte) '4';
        }
    }

    extension(ReadOnlySpan<char> data)
    {
        public void DecodeHex(Span<byte> destination)
        {
            if (data.Length != destination.Length * 2) {
                throw new ArgumentException("Invalid hexadecimal length.", nameof(data));
            }

            for (var i = 0; i < destination.Length; i++) {
                var high = StringUtility.DecodeHexNibble(data[i * 2]);
                var low = StringUtility.DecodeHexNibble(data[i * 2 + 1]);

                if ((high | low) < 0) {
                    throw new FormatException("Invalid hexadecimal character.");
                }

                destination[i] = (byte) ((high << 4) | low);
            }
        }
    }
}