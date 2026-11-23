# Windows Spice Client

Le macchine virtuali **Proxmox VE** su Windows: elenco delle VM, alimentazione, connessione con
un clic e riconnessione automatica. Il protocollo **SPICE** è implementato in C# e lo schermo è
disegnato con Direct3D 11. Non servono remote-viewer né GTK.

È la versione per Windows di [MacOS Spice Client](https://github.com/pawelwiecaszek/macos-spice-client)
e di Linux Spice Client, di cui riprende interfaccia, logica Proxmox e icona.

> **Stato:** in sviluppo, milestone **M6** completata: file `.vv`, server Proxmox (elenco
> delle VM, alimentazione, connessione via API), Gestione credenziali, finestre Benvenuto,
> Macchine virtuali e Preferenze; connessione SPICE (TCP, TLS con la CA del file e
> `host-subject`, proxy CONNECT di Proxmox, ticket) con istanza unica; schermo della VM
> (immagini QUIC, LZ, GLZ, LZ4 e JPEG; flussi video MJPEG, H.264 e H.265 con Media Foundation)
> disegnato con Direct3D 11; tastiera (anche Alt+Tab e il tasto Windows), mouse in modalità client
> e server, cursore dell'ospite, Ctrl+Alt+Canc e schermo intero con le scorciatoie del `.vv`.
> Con spice-vdagent nella VM: risoluzione che segue la finestra, appunti condivisi (testo e
> immagini) e copia di file trascinandoli sullo schermo. Riconnessione automatica nella stessa
> finestra (le sessioni Proxmox ottengono un ticket nuovo dall'API). Audio con M7.
> Il piano è in [PIANO.md](PIANO.md).
>
> Scorciatoie predefinite (come virt-viewer, sostituite da quelle del `.vv`): **Ctrl+Alt+R**
> rilascia mouse e tastiera, **Ctrl+Alt+Fine** invia Ctrl+Alt+Canc, **Maiusc+F11** passa allo
> schermo intero. Gli stessi comandi sono nel menu della barra del titolo.

## Requisiti

- Windows 10 22H2 o Windows 11, x64 o ARM64.
- Per compilare: .NET 10 SDK (basta l'installazione per utente) e git.

## Compilare

```powershell
tools\build.ps1              # build + test
tools\build.ps1 -SelfTest    # + autoverifica di renderer e interfaccia
tools\build.ps1 -Publish     # eseguibile autonomo in artifacts\win-x64
tools\build.ps1 -Proxmox     # + prova contro il finto Proxmox (..\windows-spike, Python)
tools\build.ps1 -Spice       # + prova SPICE contro QEMU per Windows (..\windows-spike, Python)
```

## Licenza

MIT. Protocollo, regole di sicurezza, fixture di test e icona derivano da MacOS Spice Client
© Paweł Więcaszek (MIT): vedi `NOTICE`.
