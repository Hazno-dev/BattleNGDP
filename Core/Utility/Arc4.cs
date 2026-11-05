using System;
using System.Runtime.CompilerServices;

namespace BattleNGDP.Core;

public static class Arc4
{
    /// <summary>
    /// RC4/ARC4 in place.
    /// </summary>
    /// <param name="data">The target encrypted data</param>
    /// <param name="key">Encryption key used</param>
    /// <param name="dropBytes">Discards that many keystream bytes before processing data</param>
    /// <param name="nonce">Optional, appended to the key for key scheduling</param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static void TransformInPlace(Span<byte> data, ReadOnlySpan<byte> key, int dropBytes = 1024, ReadOnlySpan<byte> nonce = default)
    {
        if (dropBytes < 0) {
            throw new ArgumentOutOfRangeException(nameof(dropBytes), "Drop bytes must be non-negative.");
        }

        var seedLength = key.Length + nonce.Length;
        if (seedLength is < 1 or > 256) {
            throw new ArgumentException("Invalid ARC4 key length.");
        }

        Span<byte> state = stackalloc byte[256];
        try {
            for (var n = 0; n < 256; n++) {
                state[n] = (byte) n;
            }

            byte j = 0;
            for (var n = 0; n < 256; n++) {
                var seedIndex = n % seedLength;
                var keyByte = seedIndex < key.Length
                    ? key[seedIndex]
                    : nonce[seedIndex - key.Length];

                j = unchecked((byte) (j + state[n] + keyByte));

                (state[n], state[j]) = (state[j], state[n]);
            }

            byte i = 0;
            j = 0;

            //dropper
            for (var n = 0; n < dropBytes; n++) {
                NextByte(state, ref i, ref j);
            }

            //decrypt in place
            for (var n = 0; n < data.Length; n++) {
                data[n] ^= NextByte(state, ref i, ref j);
            }
        } finally {
            state.Clear();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte NextByte(Span<byte> state, ref byte i, ref byte j)
    {
        i = unchecked((byte) (i + 1));
        j = unchecked((byte) (j + state[i]));

        (state[i], state[j]) = (state[j], state[i]);

        int index = unchecked((byte) (state[i] + state[j]));
        return state[index];
    }
}