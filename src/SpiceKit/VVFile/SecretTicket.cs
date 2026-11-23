using System.Security.Cryptography;
using System.Text;

namespace SpiceKit.VVFile;

/// <summary>
/// Ticket SPICE monouso (password) tenuto solo in memoria.
/// </summary>
/// <remarks>
/// I byte si possono azzerare dopo l'autenticazione e <see cref="ToString"/> non rivela mai il
/// valore. L'azzeramento è "best effort": il garbage collector può aver spostato copie
/// temporanee. Il ticket non va mai scritto su disco né nei log.
/// </remarks>
public sealed class SecretTicket : IDisposable
{
    private readonly Lock _lock = new();
    private byte[]? _bytes;

    public SecretTicket(string value) => _bytes = Encoding.UTF8.GetBytes(value);

    public SecretTicket(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();

    /// <summary>Chiama <paramref name="body"/> con i byte del ticket (vuoto dopo <see cref="Wipe"/>).</summary>
    /// <remarks><paramref name="body"/> non deve conservare lo span.</remarks>
    public TResult WithBytes<TResult>(TicketFunc<TResult> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (_lock)
        {
            return body(_bytes is null ? ReadOnlySpan<byte>.Empty : _bytes, _bytes is not null);
        }
    }

    /// <summary><see langword="true"/> dopo <see cref="Wipe"/>.</summary>
    public bool IsWiped
    {
        get
        {
            lock (_lock) { return _bytes is null; }
        }
    }

    /// <summary>Sovrascrive i byte con zeri e li scarta.</summary>
    public void Wipe()
    {
        lock (_lock)
        {
            if (_bytes is not null) CryptographicOperations.ZeroMemory(_bytes);
            _bytes = null;
        }
    }

    public void Dispose() => Wipe();

    public override string ToString() => "<ticket nascosto>";
}

/// <summary>Accesso ai byte del ticket; <paramref name="present"/> è falso dopo l'azzeramento.</summary>
public delegate TResult TicketFunc<out TResult>(ReadOnlySpan<byte> bytes, bool present);
