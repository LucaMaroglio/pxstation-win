using System.Buffers.Binary;

namespace SpiceKit.Wire;

/// <summary>Scrittore little-endian (porting di <c>SpiceWire/ByteWriter.swift</c>).</summary>
public sealed class ByteWriter
{
    private byte[] _buffer;

    public ByteWriter(int capacity = 64) => _buffer = new byte[Math.Max(capacity, 16)];

    public int Count { get; private set; }

    /// <summary>I byte scritti finora (vista, non copia).</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, Count);

    public byte[] ToArray() => WrittenSpan.ToArray();

    private Span<byte> Grow(int count)
    {
        var needed = Count + count;
        if (needed > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
        }

        var span = _buffer.AsSpan(Count, count);
        Count = needed;
        return span;
    }

    public void WriteU8(byte value) => Grow(1)[0] = value;

    public void WriteU16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), value);

    public void WriteU32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value);

    public void WriteU64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), value);

    public void WriteI16(short value) => WriteU16(unchecked((ushort)value));

    public void WriteI32(int value) => WriteU32(unchecked((uint)value));

    public void WriteI64(long value) => WriteU64(unchecked((ulong)value));

    public void WriteBytes(ReadOnlySpan<byte> data) => data.CopyTo(Grow(data.Length));
}
