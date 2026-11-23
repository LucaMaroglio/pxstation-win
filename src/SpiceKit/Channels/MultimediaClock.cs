using System.Diagnostics;

namespace SpiceKit.Channels;

/// <summary>
/// Orologio multimediale del server (millisecondi, uint32 che si riavvolge), appreso da
/// <c>MAIN_INIT</c> e <c>MULTI_MEDIA_TIME</c> e fatto avanzare localmente fra un aggiornamento e
/// l'altro. I report dei flussi video (e in M7 la sincronia dell'audio) usano questa base dei tempi.
/// </summary>
public sealed class MultimediaClock
{
    private readonly Lock _lock = new();
    private uint _server;
    private long _at;
    private bool _set;

    public void Set(uint serverTime)
    {
        lock (_lock)
        {
            _server = serverTime;
            _at = Stopwatch.GetTimestamp();
            _set = true;
        }
    }

    /// <summary>Ora del server, o <see langword="null"/> prima della prima sincronizzazione.</summary>
    public uint? Now
    {
        get
        {
            lock (_lock)
            {
                if (!_set) return null;
                var elapsed = (long)Stopwatch.GetElapsedTime(_at).TotalMilliseconds;
                return unchecked(_server + (uint)elapsed);
            }
        }
    }
}
