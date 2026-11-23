using System.Threading.Channels;
using SpiceKit.Codecs;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>Aggiornamenti del cursore per l'interfaccia.</summary>
public abstract record CursorEvent
{
    /// <summary>Nuova forma (<see langword="null"/>: nessun cursore o forma non supportata).</summary>
    public sealed record Shape(CursorImage? Image) : CursorEvent;

    /// <summary>Posizione del cursore nell'ospite (serve in modalità server).</summary>
    public sealed record Move(int X, int Y) : CursorEvent;

    public sealed record Visible(bool IsVisible) : CursorEvent;

    public sealed record Reset : CursorEvent;
}

/// <summary>Canale cursor (porting di <c>CursorChannel.swift</c>): forme, cache e posizione.</summary>
public sealed class CursorChannel
{
    /// <summary>Forme tenute in cache al massimo (oltre si svuota: limite contro server ostili).</summary>
    internal const int MaxCacheEntries = 256;

    private readonly ChannelConnection _connection;
    private readonly ChannelWriter<CursorEvent> _events;
    private readonly Dictionary<ulong, CursorImage> _cache = [];

    /// <param name="connection">Canale già collegato.</param>
    /// <param name="events">Destinazione degli eventi (non viene completata qui).</param>
    public CursorChannel(ChannelConnection connection, ChannelWriter<CursorEvent> events)
    {
        _connection = connection;
        _events = events;
    }

    public static CapabilitySet Capabilities { get; } = new();

    /// <summary>Forme in cache (test).</summary>
    internal int CachedShapes => _cache.Count;

    /// <summary>Messaggi che non si sono potuti decodificare (diagnostica e test).</summary>
    public int DecodeErrorCount { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var message = await _connection.NextMessageAsync(cancellationToken).ConfigureAwait(false);
            Handle(message);
        }
    }

    /// <summary>Elabora un messaggio; quelli malformati si contano e si scartano.</summary>
    internal void Handle(SpiceMessage message)
    {
        try
        {
            switch ((CursorServerMessage)message.Type)
            {
                case CursorServerMessage.Init:
                    var init = CursorInit.Decode(message.Payload);
                    Emit(new CursorEvent.Shape(Decode(init.Cursor)));
                    Emit(new CursorEvent.Move(init.X, init.Y));
                    Emit(new CursorEvent.Visible(init.Visible));
                    break;
                case CursorServerMessage.Set:
                    var set = CursorSet.Decode(message.Payload);
                    Emit(new CursorEvent.Shape(Decode(set.Cursor)));
                    Emit(new CursorEvent.Move(set.X, set.Y));
                    Emit(new CursorEvent.Visible(set.Visible));
                    break;
                case CursorServerMessage.Move:
                    var (x, y) = CursorDecoders.Move(message.Payload);
                    Emit(new CursorEvent.Move(x, y));
                    break;
                case CursorServerMessage.Hide:
                    Emit(new CursorEvent.Visible(false));
                    break;
                case CursorServerMessage.Reset:
                    _cache.Clear();
                    Emit(new CursorEvent.Reset());
                    break;
                case CursorServerMessage.InvalOne:
                    _cache.Remove(CursorDecoders.InvalidateOne(message.Payload));
                    break;
                case CursorServerMessage.InvalAll:
                    _cache.Clear();
                    break;
                default:
                    // TRAIL e tipi ignoti: nessun effetto.
                    break;
            }
        }
        catch (WireException error)
        {
            DecodeErrorCount++;
            SpiceLog.Info("cursor", $"messaggio {message.Type} non valido: {error.Message}");
        }
    }

    private CursorImage? Decode(CursorShape shape)
    {
        if (shape.Header is not { } header) return null; // SPICE_CURSOR_FLAGS_NONE
        if (shape.Options.HasFlag(CursorOptions.FromCache)) return _cache.GetValueOrDefault(header.Unique);
        var image = CursorDecoder.Decode(header, shape.Data.Span);
        if (image is not null && shape.Options.HasFlag(CursorOptions.CacheMe))
        {
            if (_cache.Count >= MaxCacheEntries) _cache.Clear();
            _cache[header.Unique] = image;
        }
        return image;
    }

    private void Emit(CursorEvent value) => _events.TryWrite(value);
}
