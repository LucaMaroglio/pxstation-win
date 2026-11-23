using System.Globalization;

namespace SpiceKit.Proxmox;

public enum ProxmoxErrorKind
{
    /// <summary>Certificato non attendibile per il sistema né per la CA del cluster; l'utente può fissarlo.</summary>
    UntrustedCertificate,
    Unauthorized,
    Forbidden,
    Http,
    InvalidResponse,
    InvalidConfiguration,
    Transport,
    /// <summary>Il QEMU della VM non ha risposto in tempo a Proxmox (<c>qmp command … got timeout</c>).</summary>
    QemuNotResponding,
    /// <summary>La VM non è arrivata allo stato "running" entro il tempo previsto.</summary>
    StartTimeout,
}

/// <summary>Errore dell'API Proxmox; <see cref="Exception.Message"/> è in italiano, per l'utente.</summary>
public sealed class ProxmoxException : Exception
{
    public ProxmoxErrorKind Kind { get; }

    /// <summary>SHA-256 (esadecimale minuscolo) del certificato rifiutato, per <see cref="ProxmoxErrorKind.UntrustedCertificate"/>.</summary>
    public string? Sha256 { get; private init; }

    /// <summary>Codice HTTP, per <see cref="ProxmoxErrorKind.Http"/>.</summary>
    public int Status { get; private init; }

    /// <summary>Messaggio del server o motivo, quando c'è.</summary>
    public string? Detail { get; private init; }

    private ProxmoxException(ProxmoxErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public ProxmoxException() : this(ProxmoxErrorKind.InvalidResponse, "Errore Proxmox.") { }

    public ProxmoxException(string message) : this(ProxmoxErrorKind.InvalidResponse, message) { }

    public ProxmoxException(string message, Exception innerException)
        : this(ProxmoxErrorKind.InvalidResponse, message, innerException) { }

    public static ProxmoxException UntrustedCertificate(string sha256) =>
        new(ProxmoxErrorKind.UntrustedCertificate,
            $"Il certificato del server non è attendibile (SHA-256 {FormatFingerprint(sha256)}).")
        { Sha256 = sha256 };

    public static ProxmoxException Unauthorized() =>
        new(ProxmoxErrorKind.Unauthorized, "Proxmox ha rifiutato il token API. Controlla l’ID e il segreto del token.");

    public static ProxmoxException Forbidden(string message) =>
        new(ProxmoxErrorKind.Forbidden,
            $"Il token API non ha i permessi per questa operazione ({message}). Servono VM.Audit, VM.PowerMgmt e VM.Console sulla VM.")
        { Detail = message };

    public static ProxmoxException Http(int status, string message) =>
        new(ProxmoxErrorKind.Http,
            string.Create(CultureInfo.InvariantCulture, $"Proxmox ha risposto HTTP {status}{(message.Length > 0 ? ": " + message : "")}."))
        { Status = status, Detail = message };

    public static ProxmoxException InvalidResponse() =>
        new(ProxmoxErrorKind.InvalidResponse, "Proxmox ha inviato una risposta inattesa.");

    public static ProxmoxException InvalidConfiguration(string reason) =>
        new(ProxmoxErrorKind.InvalidConfiguration, $"Le impostazioni del server Proxmox non sono valide ({reason}).") { Detail = reason };

    public static ProxmoxException Transport(string reason, Exception? inner = null) =>
        new(ProxmoxErrorKind.Transport, $"Impossibile raggiungere il server Proxmox ({reason}).", inner) { Detail = reason };

    public static ProxmoxException QemuNotResponding(int vmid) =>
        new(ProxmoxErrorKind.QemuNotResponding, string.Create(CultureInfo.InvariantCulture,
            $"La VM {vmid} non ha risposto in tempo a Proxmox quando sono stati chiesti i dati SPICE. ") +
            "Verifica che lo schermo sia impostato su SPICE e che non sia in corso un backup o uno snapshot, poi riprova.");

    public static ProxmoxException StartTimeout() =>
        new(ProxmoxErrorKind.StartTimeout, "La VM non si è avviata entro un minuto.");

    /// <summary><c>AB:CD:…</c> per la visualizzazione.</summary>
    public static string FormatFingerprint(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var upper = hex.ToUpperInvariant();
        return string.Join(':', Enumerable.Range(0, (upper.Length + 1) / 2)
            .Select(i => upper.Substring(i * 2, Math.Min(2, upper.Length - (i * 2)))));
    }
}
