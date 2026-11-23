using System.Buffers.Binary;

namespace SpiceKit.Wire;

/// <summary>
/// Lettore little-endian su un buffer immutabile (porting di <c>SpiceWire/ByteReader.swift</c>).
/// </summary>
/// <remarks>
/// Le strutture SPICE sono impacchettate e little-endian: si leggono campo per campo, mai
/// copiando la memoria in una struct. Ogni lettura è controllata e lancia
/// <see cref="WireException"/>. I campi puntatore dei messaggi SPICE sono offset dall'inizio
/// del messaggio intero: per questo il lettore conserva tutto il buffer.
/// </remarks>
public ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _offset;

    public ByteReader(ReadOnlySpan<byte> buffer, int offset = 0)
    {
        if ((uint)offset > (uint)buffer.Length)
        {
            throw WireException.BadOffset((uint)offset);
        }

        _buffer = buffer;
        _offset = offset;
    }

    /// <summary>Il messaggio intero.</summary>
    public readonly ReadOnlySpan<byte> Buffer => _buffer;

    public readonly int Offset => _offset;

    public readonly int Remaining => _buffer.Length - _offset;

    public readonly bool IsAtEnd => _offset >= _buffer.Length;

    private readonly void Require(int count)
    {
        if (count < 0 || count > _buffer.Length - _offset)
        {
            throw WireException.Truncated(count, Math.Max(0, _buffer.Length - _offset));
        }
    }

    public byte ReadU8()
    {
        Require(1);
        return _buffer[_offset++];
    }

    public ushort ReadU16()
    {
        Require(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[_offset..]);
        _offset += 2;
        return value;
    }

    public uint ReadU32()
    {
        Require(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_offset..]);
        _offset += 4;
        return value;
    }

    public ulong ReadU64()
    {
        Require(8);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer[_offset..]);
        _offset += 8;
        return value;
    }

    public short ReadI16() => unchecked((short)ReadU16());

    public int ReadI32() => unchecked((int)ReadU32());

    public long ReadI64() => unchecked((long)ReadU64());

    /// <summary>Legge <paramref name="count"/> byte grezzi (senza copiarli).</summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Require(count);
        var slice = _buffer.Slice(_offset, count);
        _offset += count;
        return slice;
    }

    /// <summary>Legge tutto ciò che resta.</summary>
    public ReadOnlySpan<byte> ReadRemaining()
    {
        var slice = _buffer[_offset..];
        _offset = _buffer.Length;
        return slice;
    }

    public void Skip(int count)
    {
        Require(count);
        _offset += count;
    }

    /// <summary>
    /// Un lettore posizionato a <paramref name="pointer"/> byte dall'inizio del messaggio.
    /// In SPICE un puntatore zero significa "nullo" ed è rifiutato: controllarlo prima.
    /// </summary>
    public readonly ByteReader AtPointer(uint pointer)
    {
        if (pointer == 0 || pointer >= (uint)_buffer.Length)
        {
            throw WireException.BadOffset(pointer);
        }

        return new ByteReader(_buffer, (int)pointer);
    }
}
