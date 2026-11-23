using SpiceKit.Wire;

namespace SpiceKit.Tests.Wire;

public class ByteReaderTests
{
    [Fact]
    public void Reads_little_endian_integers()
    {
        byte[] data = [0x01, 0x34, 0x12, 0x78, 0x56, 0x34, 0x12, 0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01, 0xFE, 0xFF];
        var reader = new ByteReader(data);
        Assert.Equal(0x01, reader.ReadU8());
        Assert.Equal(0x1234, reader.ReadU16());
        Assert.Equal(0x12345678u, reader.ReadU32());
        Assert.Equal(0x0123456789ABCDEFUL, reader.ReadU64());
        Assert.Equal(-2, reader.ReadI16());
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void Truncated_read_throws_and_does_not_advance()
    {
        byte[] data = [1, 2, 3];
        var reader = new ByteReader(data);
        WireException? error = null;
        try
        {
            reader.ReadU32();
        }
        catch (WireException e)
        {
            error = e;
        }

        Assert.Equal(WireErrorKind.Truncated, error?.Kind);
        Assert.Equal(0, reader.Offset);
        Assert.Equal(3, reader.Remaining);
    }

    [Fact]
    public void Negative_or_oversized_counts_are_rejected()
    {
        byte[] data = new byte[8];
        Assert.Equal(WireErrorKind.Truncated, CatchWire(() => new ByteReader(data).ReadBytes(-1)).Kind);
        Assert.Equal(WireErrorKind.Truncated, CatchWire(() => new ByteReader(data).Skip(9)).Kind);
        Assert.Equal(WireErrorKind.Truncated, CatchWire(() => new ByteReader(data).ReadBytes(int.MaxValue)).Kind);
    }

    [Fact]
    public void Pointers_are_offsets_from_message_start()
    {
        byte[] data = [0, 0, 0, 0, 0xAA, 0xBB];
        var reader = new ByteReader(data);
        reader.Skip(2);
        var at = reader.AtPointer(4);
        Assert.Equal(0xBBAA, at.ReadU16());
        Assert.Equal(2, reader.Offset);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(6u)]
    [InlineData(uint.MaxValue)]
    public void Null_or_out_of_range_pointers_are_rejected(uint pointer)
    {
        byte[] data = new byte[6];
        Assert.Equal(WireErrorKind.BadOffset, CatchWire(() => new ByteReader(data).AtPointer(pointer)).Kind);
    }

    [Fact]
    public void Writer_round_trips_through_reader()
    {
        var writer = new ByteWriter(capacity: 1);
        writer.WriteU8(7);
        writer.WriteI16(-300);
        writer.WriteU32(0xDEADBEEF);
        writer.WriteI64(long.MinValue);
        writer.WriteBytes([9, 8, 7]);
        Assert.Equal(1 + 2 + 4 + 8 + 3, writer.Count);

        var reader = new ByteReader(writer.WrittenSpan);
        Assert.Equal(7, reader.ReadU8());
        Assert.Equal(-300, reader.ReadI16());
        Assert.Equal(0xDEADBEEFu, reader.ReadU32());
        Assert.Equal(long.MinValue, reader.ReadI64());
        Assert.Equal([9, 8, 7], reader.ReadRemaining().ToArray());
    }

    private static WireException CatchWire(Action action) => Assert.Throws<WireException>(action);
}
