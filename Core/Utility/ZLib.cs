using System.Buffers;
using System.IO.Compression;
using CommunityToolkit.HighPerformance;

namespace BattleNGDP.Core;

public static class ZLib
{
    public static void Inflate(ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        using var compressedBuffer = new PooledBuffer<byte>(compressed);
        using var compressedStream = compressedBuffer.Memory.AsStream();
        using var zlib = new ZLibStream(compressedStream, CompressionMode.Decompress);
        var written = 0;
        while (written < destination.Length) {
            var count = zlib.Read(destination[written..]);
            if (count == 0) {
                break;
            }

            written += count;
        }

        if (written != destination.Length) {
            throw new InvalidDataException($"[ZLib] Produced {written} bytes but expected {destination.Length}.");
        }

        if (zlib.ReadByte() != -1) {
            throw new InvalidDataException($"[ZLib] Output exceeds declared size.");
        }
    }

    public static PooledBuffer<byte> InflateUnknownSize(ReadOnlySpan<byte> compressed)
    {
        using var compressedBuffer = new PooledBuffer<byte>(compressed);
        using var compressedStream = compressedBuffer.Memory.AsStream();
        using var zlib = new ZLibStream(compressedStream, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        var result = new PooledBuffer<byte>((int) output.Length);
        output.Position = 0;
        output.ReadExactly(result.Span);
        return result;
    }
}