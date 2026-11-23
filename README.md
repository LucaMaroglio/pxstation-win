# Windows Spice Client

Le macchine virtuali **Proxmox VE** su Windows: elenco delle VM, alimentazione, connessione con
un clic e riconnessione automatica. Il protocollo **SPICE** è implementato in C# e lo schermo è
disegnato con Direct3D 11. Non servono remote-viewer né GTK.

È la versione per Windows di [MacOS Spice Client](https://github.com/pawelwiecaszek/macos-spice-client)
e di Linux Spice Client, di cui riprende interfaccia, logica Proxmox e icona.

> **Stato:** in sviluppo, milestone **M1** completata: file `.vv`, server Proxmox (elenco
> delle VM, alimentazione, connessione via API), Gestione credenziali, finestre Benvenuto,
> Macchine virtuali e Preferenze. La connessione SPICE vera e propria arriva con M2–M3.
> Il piano è in [PIANO.md](PIANO.md).

## Requisiti

- Windows 10 22H2 o Windows 11, x64 o ARM64.
- Per compilare: .NET 10 SDK (basta l'installazione per utente) e git.

## Compilare

```powershell
tools\build.ps1              # build + test
tools\build.ps1 -SelfTest    # + autoverifica di renderer e interfaccia
tools\build.ps1 -Publish     # eseguibile autonomo in artifacts\win-x64
tools\build.ps1 -Proxmox     # + prova contro il finto Proxmox (..\windows-spike, Python)
```

## Licenza

MIT. Protocollo, regole di sicurezza, fixture di test e icona derivano da MacOS Spice Client
© Paweł Więcaszek (MIT): vedi `NOTICE`.
