using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BattleNGDP.Core;

public static class Salsa20
{
    public const int KeySize = 16;
    public const int NonceSize = 8;
    public const int BlockSize = 64;

    private const uint c_tau0 = 0x61707865;
    private const uint c_tau1 = 0x3120646E;
    private const uint c_tau2 = 0x79622D36;
    private const uint c_tau3 = 0x6B206574;

    /// <summary>
    /// Salsa20/20 XOR in place.
    /// </summary>
    /// <param name="data">The target encrypted data</param>
    /// <param name="key">Encryption key used</param>
    /// <param name="nonce">Nonce (if any)</param>
    /// <param name="byteOffset">The absolute byte position within the Salsa stream</param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="CryptographicException"></exception>
    public static void TransformInPlace(Span<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ulong byteOffset = 0)
    {
        if (key.Length != KeySize) {
            throw new ArgumentException($"Salsa20/128 requires a {KeySize}-byte key.", nameof(key));
        }

        if (nonce.Length != NonceSize) {
            throw new ArgumentException($"Salsa20 requires an {NonceSize}-byte nonce.", nameof(nonce));
        }

        if (data.IsEmpty) {
            return;
        }

        Span<uint> state = stackalloc uint[16];
        Span<uint> working = stackalloc uint[16];
        Span<byte> keyStream = stackalloc byte[BlockSize];

        try {
            InitializeState(state, key, nonce);

            var counter = byteOffset >> 6;
            var keyStreamOffset = (int) (byteOffset & 63);
            var dataOffset = 0;

            while (dataOffset < data.Length) {
                state[8] = (uint) counter;
                state[9] = (uint) (counter >> 32);
                GenerateBlock(state, working, keyStream);
                var count = Math.Min(BlockSize - keyStreamOffset, data.Length - dataOffset);
                for (var i = 0; i < count; i++) {
                    data[dataOffset + i] ^= keyStream[keyStreamOffset + i];
                }

                dataOffset += count;
                keyStreamOffset = 0;

                if (dataOffset < data.Length) {
                    if (counter == ulong.MaxValue) {
                        throw new CryptographicException("Salsa20 block counter overflow.");
                    }

                    counter++;
                }
            }
        } finally {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(state));
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(working));
            CryptographicOperations.ZeroMemory(keyStream);
        }
    }

    private static void InitializeState(Span<uint> state, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        //Salsa20/128 state
        //c0  k0  k1  k2
        //k3  c1  n0  n1
        //b0  b1  c2  k0
        //k1  k2  k3  c3

        state[0] = c_tau0;
        state[5] = c_tau1;
        state[10] = c_tau2;
        state[15] = c_tau3;

        var k0 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(0, 4));
        var k1 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4, 4));
        var k2 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(8, 4));
        var k3 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(12, 4));

        state[1] = k0;
        state[2] = k1;
        state[3] = k2;
        state[4] = k3;

        state[11] = k0;
        state[12] = k1;
        state[13] = k2;
        state[14] = k3;

        state[6] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[..4]);
        state[7] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..]);

        state[8] = 0;
        state[9] = 0;
    }

    private static void GenerateBlock(ReadOnlySpan<uint> state, Span<uint> working, Span<byte> output)
    {
        state.CopyTo(working);
        for (var i = 0; i < 10; i++) {
            QuarterRound(ref working[0], ref working[4], ref working[8], ref working[12]);
            QuarterRound(ref working[5], ref working[9], ref working[13], ref working[1]);
            QuarterRound(ref working[10], ref working[14], ref working[2], ref working[6]);
            QuarterRound(ref working[15], ref working[3], ref working[7], ref working[11]);

            QuarterRound(ref working[0], ref working[1], ref working[2], ref working[3]);
            QuarterRound(ref working[5], ref working[6], ref working[7], ref working[4]);
            QuarterRound(ref working[10], ref working[11], ref working[8], ref working[9]);
            QuarterRound(ref working[15], ref working[12], ref working[13], ref working[14]);
        }

        for (var i = 0; i < 16; i++) {
            var value = unchecked(working[i] + state[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        b ^= BitOperations.RotateLeft(unchecked(a + d), 7);
        c ^= BitOperations.RotateLeft(unchecked(b + a), 9);
        d ^= BitOperations.RotateLeft(unchecked(c + b), 13);
        a ^= BitOperations.RotateLeft(unchecked(d + c), 18);
    }
}