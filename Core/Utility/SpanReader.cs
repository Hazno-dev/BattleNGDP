using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BattleNGDP.Core;

internal ref struct SpanReader
{
    private ReadOnlySpan<byte> m_data;
    private bool m_writeable;

    public readonly int Remaining => m_data.Length;
    public readonly ReadOnlySpan<byte> RemainingSpan => m_data;

    public SpanReader(Span<byte> data)
    {
        m_data = data;
        m_writeable = true;
    }

    public SpanReader(ReadOnlySpan<byte> data)
    {
        m_data = data;
        m_writeable = false;
    }

    public byte ReadByte()
    {
        Ensure(1);

        var value = m_data[0];
        m_data = m_data[1..];
        return value;
    }

    public ushort ReadUInt16BE()
    {
        Ensure(sizeof(ushort));

        var value = BinaryPrimitives.ReadUInt16BigEndian(m_data);
        m_data = m_data[sizeof(ushort)..];
        return value;
    }

    public uint ReadUInt24BE()
    {
        Ensure(3);

        var value = ((uint) m_data[0] << 16) |
                    ((uint) m_data[1] << 8) |
                    m_data[2];

        m_data = m_data[3..];
        return value;
    }

    public uint ReadUInt32BE()
    {
        Ensure(sizeof(uint));

        var value = BinaryPrimitives.ReadUInt32BigEndian(m_data);
        m_data = m_data[sizeof(uint)..];
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if ((uint) count > (uint) m_data.Length) {
            throw new EndOfStreamException();
        }

        var value = m_data[..count];
        m_data = m_data[count..];
        return value;
    }

    public ReadOnlySpan<byte> ReadRemaining()
    {
        var value = m_data;
        m_data = default;
        return value;
    }

    public Span<byte> ReadRemainingUnsafe()
    {
        Debug.Assert(m_writeable, "Cannot read remaining as writable span from a read-only SpanReader.");

        var value = m_data;
        m_data = default;
        if (value.Length == 0) {
            return Span<byte>.Empty;
        }

        ref var first = ref MemoryMarshal.GetReference(value);
        return MemoryMarshal.CreateSpan(ref first, value.Length);
    }

    public string ReadCString()
    {
        var length = m_data.IndexOf((byte) 0);
        if (length < 0) {
            throw new InvalidDataException("Unterminated string.");
        }

        var bytes = ReadBytes(length);
        ReadByte(); // NUL

        return Encoding.UTF8.GetString(bytes);
    }

    private readonly void Ensure(int count)
    {
        if (m_data.Length < count) {
            throw new EndOfStreamException();
        }
    }

    public ulong ReadUInt40BE()
    {
        Ensure(5);

        var value = ((ulong) m_data[0] << 32) |
                    ((ulong) m_data[1] << 24) |
                    ((ulong) m_data[2] << 16) |
                    ((ulong) m_data[3] << 8) |
                    m_data[4];

        m_data = m_data[5..];

        return value;
    }
}