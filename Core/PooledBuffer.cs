using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BattleNGDP.Core;

[StructLayout(LayoutKind.Sequential)]
public readonly struct PooledBuffer<T> : IDisposable where T : unmanaged
{
    private readonly T[]? m_data;

    public int Length { get; }

    public PooledBuffer(int size) : this(ArrayPool<T>.Shared.Rent(size), size) { }

    public PooledBuffer(ReadOnlySpan<T> data)
    {
        if (data.IsEmpty) {
            return;
        }

        m_data = ArrayPool<T>.Shared.Rent(data.Length);
        Length = data.Length;

        data.CopyTo(m_data);
    }

    private PooledBuffer(T[] data, int length)
    {
        m_data = data;
        Length = length;
    }

    public static PooledBuffer<T> FromRental(T[] data, int length) =>
        new(data, length);

    public Span<T> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => m_data.AsSpan(0, Length);
    }

    public ReadOnlySpan<T> ReadOnlySpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => m_data.AsSpan(0, Length);
    }

    public Memory<T> Memory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => m_data.AsMemory(0, Length);
    }

    public ReadOnlyMemory<T> ReadOnlyMemory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => m_data.AsMemory(0, Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (m_data != null) {
            ArrayPool<T>.Shared.Return(m_data);
        }
    }
}