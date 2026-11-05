using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BattleNGDP.Core;
using BattleNGDP.TACT.Data;
using CommunityToolkit.HighPerformance;

namespace BattleNGDP.CASC;

public enum BLTEChunkType : byte
{
    None = (byte) 'N',
    ZLib = (byte) 'Z',
    Encrypted = (byte) 'E',
    Frame = (byte) 'F',
    LZ4 = (byte) '4'
}

public enum BLTEEncryptionType : byte
{
    Salsa20 = (byte) 'S',
    Arc4 = (byte) 'A'
}

public sealed class BLTEHeaderSizeException(BLTEChunk.ChunkHeader[] headers) : Exception("BLTE header size mismatch.")
{
    public readonly BLTEChunk.ChunkHeader[] Headers = headers;
}

public sealed class BLTEChunk(int index, uint encodedSize, uint contentSize, BLTEChunkType type, byte[] checksum, ReadOnlyMemory<byte> data)
{
    public readonly struct ChunkHeader(uint encodedSize, uint contentSize, byte[] checksum, byte[]? uncompressedChecksum)
    {
        public readonly uint EncodedSize = encodedSize;
        public readonly uint ContentSize = contentSize;
        public readonly byte[] Checksum = checksum;
        public readonly byte[]? UncompressedChecksum = uncompressedChecksum;
        
        public void EnsureContentMatches(ReadOnlySpan<byte> content)
        {
            if (content.Length != ContentSize) {
                throw new InvalidDataException("Chunk decoded-size mismatch.");
            }

            if (UncompressedChecksum is { Length: 16 } expected) {
                Span<byte> actual = stackalloc byte[16];
                MD5.HashData(content, actual);
                if (!actual.SequenceEqual(expected)) {
                    throw new InvalidDataException(
                        $"Chunk CKey mismatch: actual={Convert.ToHexString(actual)}, expected={Convert.ToHexString(expected)}.");
                }
#if DEBUG
                Logger.Log($"Chunk CKey validation passed! Both equal {Convert.ToHexString(expected)}.");
#endif
            } 
#if DEBUG
            else {
                Logger.Log($"Chunk CKey not provided for validation. Content size: {content.Length} bytes.");
            }
#endif
        }
    }

    public int Index { get; } = index;
    public uint EncodedSize { get; } = encodedSize;
    public uint ContentSize { get; } = contentSize;
    public BLTEChunkType Type { get; } = type;
    public ReadOnlyMemory<byte> Checksum { get; } = checksum;
    public ReadOnlyMemory<byte> Data { get; } = data;

}

public sealed class BLTE : IDisposable
{
    public uint HeaderSize { get; }
    public BLTEChunk[] Chunks { get; }
    public PooledBuffer<byte> Data { get; }

    /// 0x0F - normal
    /// 0x10 - containerless
    /// 0x00 - headerless/single chunk BLTE (custom)
    public byte Format { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose() =>
        Data.Dispose();

    public BLTE(Span<byte> buffer, bool verifyChecksums = true)
    {
        if (buffer.Length < 8) {
            throw new InvalidDataException("BLTE file is too small.");
        }

        var reader = new SpanReader(buffer);
        if (!reader.ReadBytes(4).SequenceEqual("BLTE"u8)) {
            throw new InvalidDataException("Invalid BLTE signature.");
        }

        HeaderSize = reader.ReadUInt32BE();

        //Headerless data
        if (HeaderSize == 0) {
            Format = 0;

            var encodedChunk = reader.ReadRemainingUnsafe();
            if (encodedChunk.IsEmpty) {
                throw new InvalidDataException("Headerless BLTE contains no chunk.");
            }

            var type = (BLTEChunkType) encodedChunk[0];
            Data = DecodeUnknownSize(encodedChunk, 0, verifyChecksums);
            Chunks = [
                new BLTEChunk(0, checked((uint) encodedChunk.Length), checked((uint) Data.Length), type, [], Data.ReadOnlyMemory)
            ];

            return;
        }

        if (HeaderSize > buffer.Length) {
            throw new InvalidDataException($"BLTE header size {HeaderSize} exceeds file size {buffer.Length}.");
        }

        Format = reader.ReadByte();
        if (Format != 0x0F && Format != 0x10) {
            throw new NotSupportedException($"Unsupported BLTE header format 0x{Format:X2}.");
        }

        var chunkSize = 24;
        if (Format == 0x10) {
            chunkSize = 40;
        }

        var chunkCount = checked((int) reader.ReadUInt24BE());
        var expectedHeaderSize = checked(12 + chunkCount * chunkSize);

        if (HeaderSize != expectedHeaderSize) {
            throw new InvalidDataException($"Invalid BLTE header size. Expected {expectedHeaderSize}, got {HeaderSize}.");
        }

        var headers = new BLTEChunk.ChunkHeader[chunkCount];

        long totalContentSize = 0;
        long totalEncodedSize = 0;

        for (var i = 0; i < headers.Length; i++) {
            var encodedSize = reader.ReadUInt32BE();
            var contentSize = reader.ReadUInt32BE();

            if (encodedSize == 0) {
                throw new InvalidDataException($"Chunk {i} has zero encoded size.");
            }

            byte[] checksum = [.. reader.ReadBytes(16)];
            byte[]? uncompressedChecksum = null;
            if (Format >= 0x10) {
                uncompressedChecksum = [.. reader.ReadBytes(16)];
            }

            headers[i] = new BLTEChunk.ChunkHeader(encodedSize, contentSize, checksum, uncompressedChecksum);
            totalEncodedSize += encodedSize;
            totalContentSize += contentSize;
        }

        if (totalContentSize > int.MaxValue) {
            throw new NotSupportedException("BLTE decompressed size exceeds the maximum .NET array size.");
        }

        var encodedData = buffer[(int) HeaderSize..];

        if (totalEncodedSize != encodedData.Length) {
            if (totalContentSize > encodedData.Length && Format == 0x10) {
                throw new BLTEHeaderSizeException(headers);
            }

            throw new InvalidDataException($"Chunk sizes total {totalEncodedSize} bytes, but {encodedData.Length} bytes remain after the header.");
        }

        Data = new PooledBuffer<byte>((int) totalContentSize);
        Chunks = new BLTEChunk[chunkCount];
        var outputOffset = 0;
        for (var i = 0; i < headers.Length; i++) {
            ref readonly var header = ref headers[i];

            var encodedSize = checked((int) header.EncodedSize);
            var contentSize = checked((int) header.ContentSize);

            var encodedChunk = encodedData[..encodedSize];

            encodedData = encodedData[encodedSize..];

            if (verifyChecksums) {
                VerifyChecksum(i, encodedChunk, header.Checksum);
            }

            var type = (BLTEChunkType) encodedChunk[0];
            var destination = Data.Span.Slice(outputOffset, contentSize);
            DecodeKnownSize(encodedChunk, destination, i, verifyChecksums);

            Chunks[i] = new BLTEChunk(i, header.EncodedSize, header.ContentSize, type, header.Checksum,
                Data.ReadOnlyMemory.Slice(outputOffset, contentSize));
            outputOffset += contentSize;
        }
    }

    /// <summary>
    /// Parse BLTE chunks from an independent header. Supports 0x0F and 0x10 formats.
    /// </summary>
    /// <param name="headerBytes">BLTE header </param>
    /// <returns></returns>
    /// <exception cref="InvalidDataException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    public static BLTEChunk.ChunkHeader[] ReadChunkHeaders(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < 12) {
            throw new InvalidDataException("Truncated Chunk header.");
        }

        var reader = new SpanReader(headerBytes);
        if (!reader.ReadBytes(4).SequenceEqual("BLTE"u8)) {
            throw new InvalidDataException("Invalid BLTE signature.");
        }

        var headerSize = reader.ReadUInt32BE();
        if (headerSize == 0) {
            throw new InvalidDataException("Headerless BLTE has no independent chunk table.");
        }

        var format = reader.ReadByte();
        var entryLength = format switch {
            0x0F => 24,
            0x10 => 40,
            _ => throw new NotSupportedException($"Unsupported BLTE format 0x{format:X2}.")
        };

        var count = checked((int) reader.ReadUInt24BE());
        var expectedSize = checked(12 + count * entryLength);
        if (headerSize != expectedSize || headerBytes.Length < expectedSize) {
            throw new InvalidDataException(
                $"Invalid BLTE header: expected {expectedSize} bytes, declared {headerSize}, available {headerBytes.Length}.");
        }

        var headers = new BLTEChunk.ChunkHeader[count];
        for (var i = 0; i < count; i++) {
            var encodedSize = reader.ReadUInt32BE();
            var decodedSize = reader.ReadUInt32BE();
            byte[] encodedMd5 = [.. reader.ReadBytes(16)];
            byte[]? decodedMd5 = format == 0x10
                ? [.. reader.ReadBytes(16)]
                : null;

            if (encodedSize == 0) {
                throw new InvalidDataException($"Chunk {i} is empty.");
            }

            headers[i] = new BLTEChunk.ChunkHeader(encodedSize, decodedSize, encodedMd5, decodedMd5);
        }

        return headers;
    }

    public static void DecodeStandaloneChunk(Span<byte> encoded, Span<byte> destination, int chunkIndex, bool verifyChecksums = true) =>
        DecodeKnownSize(encoded, destination, chunkIndex, verifyChecksums);

    private static void DecodeKnownSize(Span<byte> encoded, Span<byte> destination, int chunkIndex, bool verifyChecksums)
    {
        if (encoded.IsEmpty) {
            throw new InvalidDataException($"Chunk {chunkIndex} is empty.");
        }

        var type = (BLTEChunkType) encoded[0];
        var payload = encoded[1..];

        switch (type) {
            case BLTEChunkType.None: {
                if (payload.Length != destination.Length) {
                    throw new InvalidDataException($"Chunk {chunkIndex}: raw chunk expected {destination.Length} bytes, got {payload.Length}.");
                }

                payload.CopyTo(destination);
                return;
            }
            case BLTEChunkType.ZLib: {
                ZLib.Inflate(payload, destination);
                return;
            }
            case BLTEChunkType.Frame: {
                using var nested = new BLTE(payload, verifyChecksums);
                if (nested.Data.Length != destination.Length) {
                    throw new InvalidDataException($"Chunk {chunkIndex}: nested BLTE has {nested.Data.Length} bytes. expected {destination.Length}.");
                }

                nested.Data.Span.CopyTo(destination);
                return;
            }
            case BLTEChunkType.Encrypted: {
                ContainerCrypt.DecryptContainerlessInPlace(ref encoded, 1024, chunkIndex);
                //DecodeEncrypted(payload, destination, chunkIndex, verifyChecksums);
                DecodeKnownSize(encoded, destination, chunkIndex, verifyChecksums);
                return;
            }
            case BLTEChunkType.LZ4: {
                throw new NotSupportedException($"Chunk {chunkIndex} uses LZ4.");
            }
            default: {
                throw new InvalidDataException($"Unknown Chunk type 0x{(byte) type:X2}.");
            }
        }
    }

    private static PooledBuffer<byte> DecodeUnknownSize(Span<byte> encoded, int chunkIndex, bool verifyChecksums)
    {
        var type = (BLTEChunkType) encoded[0];
        encoded = encoded[1..];

        return type switch {
            BLTEChunkType.None => new PooledBuffer<byte>(encoded),
            BLTEChunkType.ZLib => ZLib.InflateUnknownSize(encoded),
            BLTEChunkType.Frame => new BLTE(encoded, verifyChecksums).Data,
            BLTEChunkType.Encrypted => throw new NotSupportedException($"Chunk {chunkIndex} is encrypted but idk how to derive the size :("),
            BLTEChunkType.LZ4 => throw new NotSupportedException($"Chunk {chunkIndex} uses LZ4."),
            _ => throw new InvalidDataException($"Unknown Chunk type 0x{(byte) type:X2}.")
        };
    }

    /*private static void DecodeEncrypted(ReadOnlySpan<byte> payload, Span<byte> destination, int chunkIndex, bool verifyChecksums)
    {
        if (payload.Length < 10) {
            throw new InvalidDataException($"Chunk {chunkIndex}: encrypted header is truncated.");
        }

        int keyNameLength = payload[0];
        if (keyNameLength != 8) {
            throw new InvalidDataException($"Chunk {chunkIndex}: unsupported key name length {keyNameLength}.");
        }

        var keyName = payload.Slice(1, 8);

        int ivLength = payload[9];
        if (ivLength is not (4 or 8)) {
            throw new InvalidDataException($"Chunk {chunkIndex}: invalid IV length {ivLength}.");
        }

        var encryptionTypeOffset = 10 + ivLength;

        if (payload.Length <= encryptionTypeOffset) {
            throw new InvalidDataException($"Chunk {chunkIndex}: missing encryption type or ciphertext.");
        }

        var iv = payload.Slice(10, ivLength);
        var encryptionType = payload[encryptionTypeOffset];
        var ciphertext = payload[(encryptionTypeOffset + 1)..];
        Span<byte> key = stackalloc byte[16];
        if (!TACTKeyringService.TryGetUniqueByNameBytes(keyName, key)) {
            throw new KeyNotFoundException($"Missing or ambiguous BLTE encryption key {Convert.ToHexString(keyName)}.");
        }

        Span<byte> nonce = stackalloc byte[8];
        nonce.Clear();
        iv.CopyTo(nonce);

        //incorporate the chunk index to IV
        var index = checked((uint) chunkIndex);
        for (var i = 0; i < 4; i++) {
            nonce[i] ^= (byte) (index >> (i * 8));
        }

        var rented = ArrayPool<byte>.Shared.Rent(ciphertext.Length);
        try {
            var decrypted = rented.AsSpan(0, ciphertext.Length);
            ciphertext.CopyTo(decrypted);
            switch (encryptionType) {
                case (byte) 'S': {
                    Salsa20.TransformInPlace(decrypted, key, nonce, 0);
                    break;
                }
                case (byte) 'A': {
                    Arc4.TransformInPlace(decrypted, key, 1024);
                    break;
                }
                default: {
                    throw new NotSupportedException($"Chunk {chunkIndex}: unknown encryption type 0x{encryptionType:X2}");
                }
            }

            if (decrypted.IsEmpty) {
                throw new InvalidDataException($"Chunk {chunkIndex}: decrypted data is empty");
            }

            if (decrypted[0] is not ((byte) 'N' or (byte) 'Z' or (byte) 'F' or (byte) '4')) {
                throw new InvalidDataException($"Chunk {chunkIndex}: decrypted data has an invalid inner mode 0x{decrypted[0]:X2}");
            }

            DecodeKnownSize(decrypted, destination, chunkIndex, verifyChecksums);
        } finally {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(nonce);

            ArrayPool<byte>.Shared.Return(rented, true);
        }
    }*/

    private static void VerifyChecksum(int chunkIndex, ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[16];
        MD5.HashData(encoded, actual);
        if (!actual.SequenceEqual(expected)) {
            throw new InvalidDataException($"Chunk {chunkIndex} checksum mismatch.");
        }
    }
}