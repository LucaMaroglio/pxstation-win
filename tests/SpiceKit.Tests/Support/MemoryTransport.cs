using System.Threading.Channels;
using SpiceKit.Transport;

namespace SpiceKit.Tests.Support;

/// <summary>Coppia di trasporti collegati in memoria (porting di <c>MemoryByteStream</c>).</summary>
public sealed class MemoryTransport : ISpiceTransport
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private MemoryTransport? _peer;
    private byte[] _pending = [];
    private int _pendingOffset;
    private int _closed;

    public static (MemoryTransport Client, MemoryTransport Server) Pair()
    {
        var a = new MemoryTransport();
        var b = new MemoryTransport();
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (IsClosed || _peer is null || !_peer._inbound.Writer.TryWrite(bytes.ToArray())) throw TransportException.Closed();
        return Task.CompletedTask;
    }

    public async Task ReceiveExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            if (_pendingOffset == _pending.Length)
            {
                try
                {
                    _pending = await _inbound.Reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    throw TransportException.Closed();
                }
                _pendingOffset = 0;
            }
            var count = Math.Min(buffer.Length - filled, _pending.Length - _pendingOffset);
            _pending.AsMemory(_pendingOffset, count).CopyTo(buffer[filled..]);
            _pendingOffset += count;
            filled += count;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _inbound.Writer.TryComplete();
            _peer?._inbound.Writer.TryComplete();
        }
        return ValueTask.CompletedTask;
    }
}
