namespace SpiceKit.Transport;

/// <summary>
/// Flusso di byte affidabile e ordinato (TCP, eventualmente TLS e/o attraverso un proxy).
/// </summary>
/// <remarks>
/// I canali dipendono solo da questa interfaccia, così la logica del protocollo si prova con
/// flussi in memoria. Un solo lettore alla volta; gli invii possono arrivare da più task.
/// </remarks>
public interface ISpiceTransport : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <summary>Riempie <paramref name="buffer"/> per intero o lancia (<see cref="TransportErrorKind.Closed"/> a fine flusso).</summary>
    Task ReceiveExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}

public static class TransportExtensions
{
    public static async Task<byte[]> ReceiveAsync(this ISpiceTransport stream, int count, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[count];
        if (count > 0) await stream.ReceiveExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }
}

/// <summary><see cref="ISpiceTransport"/> su uno <see cref="Stream"/> (NetworkStream o SslStream).</summary>
public sealed class StreamTransport(Stream stream, IDisposable? owner = null) : ISpiceTransport
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            throw Volatile.Read(ref _disposed) != 0 ? TransportException.Closed() : TransportException.ConnectionFailed(error.Message, error);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task ReceiveExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw TransportException.Closed();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            throw Volatile.Read(ref _disposed) != 0 ? TransportException.Closed() : TransportException.ConnectionFailed(error.Message, error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Chiudere il flusso sblocca le letture in corso.
        await stream.DisposeAsync().ConfigureAwait(false);
        owner?.Dispose();
    }
}
