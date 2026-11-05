using System.Buffers.Binary;

namespace BattleNGDP.TACT.Data;

public readonly struct TACTKey : IEquatable<TACTKey>
{
    private readonly ulong m_low;
    private readonly ulong m_high;

    public readonly byte Length;

    public TACTKey(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 16) {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        Span<byte> buffer = stackalloc byte[16];
        buffer.Clear();

        data.CopyTo(buffer);

        m_low = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        m_high = BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]);

        Length = checked((byte) data.Length);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length) {
            throw new ArgumentException("Destination is too small.");
        }

        Span<byte> buffer = stackalloc byte[16];

        BinaryPrimitives.WriteUInt64LittleEndian(buffer, m_low);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], m_high);

        buffer[..Length].CopyTo(destination);
    }

    public byte[] ToArray()
    {
        var result = new byte[Length];
        CopyTo(result);
        return result;
    }

    public override string ToString()
    {
        Span<byte> buffer = stackalloc byte[16];
        CopyTo(buffer);

        return Convert.ToHexString(buffer[..Length]).ToLowerInvariant();
    }

    public bool Equals(TACTKey other) =>
        Length == other.Length &&
        m_low == other.m_low &&
        m_high == other.m_high;

    public override bool Equals(object? obj) =>
        obj is TACTKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(m_low, m_high, Length);

    public static bool operator ==(TACTKey left, TACTKey right) =>
        left.Equals(right);

    public static bool operator !=(TACTKey left, TACTKey right) =>
        !left.Equals(right);

    public int CompareSequential(TACTKey other)
    {
        Span<byte> leftData = stackalloc byte[16];
        Span<byte> rightData = stackalloc byte[16];

        CopyTo(leftData);
        other.CopyTo(rightData);
        return leftData[..Length].SequenceCompareTo(rightData[..other.Length]);
    }
}