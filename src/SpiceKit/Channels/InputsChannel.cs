using System.Threading.Channels;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>Input dell'utente da inoltrare all'ospite.</summary>
public abstract record InputEvent
{
    /// <summary>Scancode PC XT (set 1); i tasti estesi (prefisso <c>0xE0</c>) sono <c>0x100 | codice</c>.</summary>
    public sealed record KeyDown(ushort Scancode) : InputEvent;

    public sealed record KeyUp(ushort Scancode) : InputEvent;

    /// <summary>Stato dei blocchi (Bloc Maiusc, Bloc Num, Bloc Scorr) dell'host, sincronizzato quando la finestra prende il fuoco.</summary>
    public sealed record Locks(KeyboardLocks State) : InputEvent;

    /// <summary>Modalità server: movimento relativo.</summary>
    public sealed record Motion(int Dx, int Dy, MouseButtons Buttons) : InputEvent;

    /// <summary>Modalità client: posizione assoluta in pixel dell'ospite.</summary>
    public sealed record Position(uint X, uint Y, MouseButtons Buttons, byte Display) : InputEvent;

    /// <summary>Pulsante premuto; <paramref name="Buttons"/> è lo stato dopo la pressione.</summary>
    public sealed record Press(MouseButton Button, MouseButtons Buttons) : InputEvent;

    /// <summary>Pulsante rilasciato; <paramref name="Buttons"/> è lo stato dopo il rilascio.</summary>
    public sealed record Release(MouseButton Button, MouseButtons Buttons) : InputEvent;
}

/// <summary>
/// Canale inputs (porting di <c>InputsChannel.swift</c>): tastiera, mouse e blocchi, con il
/// limite dei movimenti non confermati (<c>SPICE_INPUT_MOTION_ACK_BUNCH</c>).
/// </summary>
/// <remarks>
/// <see cref="Post"/> non blocca mai (si chiama dal thread dell'interfaccia): i messaggi vanno in
/// una coda svuotata da <see cref="RunAsync"/>. Con più di due gruppi di movimenti senza conferma
/// i movimenti successivi si accumulano (somma dei relativi, ultima posizione assoluta) e partono
/// all'arrivo della conferma: la coda resta limitata anche se la rete rallenta.
/// </remarks>
public sealed class InputsChannel
{
    private const int MotionWindow = 2 * SpiceConstants.InputMotionAckBunch;

    private readonly ChannelConnection _connection;
    private readonly Channel<SpiceMessage> _outbox = Channel.CreateUnbounded<SpiceMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private int _motionCount;
    private InputEvent.Motion? _pendingMotion;
    private InputEvent.Position? _pendingPosition;
    private KeyboardLocks _guestLocks;

    public InputsChannel(ChannelConnection connection) => _connection = connection;

    /// <summary>Capacità annunciate dal client (come macOS: scancode).</summary>
    public static CapabilitySet Capabilities { get; } = CapabilitySet.FromBits((int)InputsCapability.KeyScancode);

    /// <summary>Blocchi attivi nell'ospite secondo il server.</summary>
    public KeyboardLocks GuestLocks
    {
        get
        {
            lock (_gate) return _guestLocks;
        }
    }

    /// <summary>Movimenti inviati e non ancora confermati (test).</summary>
    internal int UnacknowledgedMotions
    {
        get
        {
            lock (_gate) return _motionCount;
        }
    }

    /// <summary>Accoda un input; ignorato dopo la chiusura del canale.</summary>
    public void Post(InputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            switch (input)
            {
                case InputEvent.KeyDown key:
                    Send(InputsClientEncoder.KeyDown(key.Scancode));
                    break;
                case InputEvent.KeyUp key:
                    Send(InputsClientEncoder.KeyUp(key.Scancode));
                    break;
                case InputEvent.Locks locks:
                    if (locks.State == _guestLocks) break;
                    _guestLocks = locks.State; // il server conferma con KEY_MODIFIERS
                    Send(InputsClientEncoder.KeyModifiers(locks.State));
                    break;
                case InputEvent.Motion motion:
                    _pendingMotion = _pendingMotion is { } pending
                        ? new InputEvent.Motion(unchecked(pending.Dx + motion.Dx), unchecked(pending.Dy + motion.Dy), motion.Buttons)
                        : motion;
                    if (_motionCount < MotionWindow) FlushPendingMotion();
                    break;
                case InputEvent.Position position:
                    _pendingPosition = position;
                    if (_motionCount < MotionWindow) FlushPendingMotion();
                    break;
                case InputEvent.Press press:
                    // Il clic deve arrivare dove si trova il puntatore: prima i movimenti in attesa.
                    FlushPendingMotion();
                    Send(InputsClientEncoder.MousePress(press.Button, press.Buttons));
                    break;
                case InputEvent.Release release:
                    FlushPendingMotion();
                    Send(InputsClientEncoder.MouseRelease(release.Button, release.Buttons));
                    break;
            }
        }
    }

    /// <summary>Invia la coda ed elabora i messaggi del server (blocchi, conferme) fino alla chiusura.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sender = SendLoopAsync(stop.Token);
        try
        {
            while (true)
            {
                var message = await _connection.NextMessageAsync(cancellationToken).ConfigureAwait(false);
                Handle(message);
            }
        }
        finally
        {
            _outbox.Writer.TryComplete();
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await sender.ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or Transport.TransportException)
            {
                // La causa della chiusura è già quella del ciclo di ricezione.
            }
        }
    }

    internal void Handle(SpiceMessage message)
    {
        try
        {
            switch ((InputsServerMessage)message.Type)
            {
                case InputsServerMessage.Init:
                case InputsServerMessage.KeyModifiers:
                    var locks = InputsDecoders.Locks(message.Payload);
                    lock (_gate) _guestLocks = locks;
                    break;
                case InputsServerMessage.MouseMotionAck:
                    lock (_gate)
                    {
                        _motionCount = Math.Max(0, _motionCount - SpiceConstants.InputMotionAckBunch);
                        FlushPendingMotion();
                    }
                    break;
                default:
                    SpiceLog.Debug("inputs", $"messaggio {message.Type} ignorato");
                    break;
            }
        }
        catch (WireException error)
        {
            throw ChannelException.Malformed($"inputs {message.Type}", error);
        }
    }

    /// <summary>Messaggi accodati e non ancora inviati (test).</summary>
    internal SpiceMessage? TakeQueued() => _outbox.Reader.TryRead(out var message) ? message : null;

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await _connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private void FlushPendingMotion()
    {
        if (_pendingMotion is { } motion)
        {
            _pendingMotion = null;
            _motionCount++;
            Send(InputsClientEncoder.MouseMotion(motion.Dx, motion.Dy, motion.Buttons));
        }
        if (_pendingPosition is { } position)
        {
            _pendingPosition = null;
            _motionCount++;
            Send(InputsClientEncoder.MousePosition(position.X, position.Y, position.Buttons, position.Display));
        }
    }

    private void Send(SpiceMessage message) => _outbox.Writer.TryWrite(message);
}
