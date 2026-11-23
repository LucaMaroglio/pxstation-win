# AGENTS.md — Windows Spice Client

Istruzioni per gli agenti di codice (Claude Code e altri) che lavorano in questo repository.
Leggere tutto prima di modificare. Se qualcosa contrasta con un'indicazione diretta del
maintainer, vale il maintainer: poi si aggiorna questo file.

Il piano completo, con motivazioni e stime, è in [PIANO.md](PIANO.md).

---

## 1. Obiettivo

Client **SPICE nativo per Windows** con integrazione **Proxmox VE**:

1. Protocollo SPICE implementato in **C#** (porting di SpiceKit da `../macos-spice-client`).
2. Schermo della VM disegnato con **Direct3D 11** in uno `SwapChainPanel`.
3. Interfaccia **WinUI 3** con la stessa organizzazione di `../linux-spice-client` e di
   `../macos-spice-client`: Benvenuto, Macchine virtuali, Preferenze (Generale e Proxmox), finestre
   di sessione, area di notifica, notifiche.
4. Apre i file `.vv` (Proxmox, oVirt, QEMU) e si collega subito, perché i ticket Proxmox scadono
   in circa 30 secondi.

Esclusi, salvo richiesta del maintainer: remote-viewer, spice-gtk, GTK, GStreamer, VNC/RDP,
cartelle condivise WebDAV.

---

## 2. Decisioni fisse (confermate dal maintainer il 23/11/2026)

| Area | Scelta |
|---|---|
| Linguaggio | C# (ultima versione), **.NET 10 LTS**, `Nullable` attivo, **avvisi come errori** (`Directory.Build.props`) |
| Interfaccia | **WinUI 3**, Windows App SDK incluso nell'app (`WindowsAppSDKSelfContained`), app **non pacchettizzata** |
| Distribuzione | eseguibile autonomo con icona (`Assets/app.ico`) + installer **Inno Setup** (M9), associazione `.vv` per utente senza permessi di amministratore |
| Windows minimo | Windows 10 22H2 e Windows 11 (`TargetPlatformMinVersion` 10.0.19041.0), x64 e ARM64 |
| Rendering | Direct3D 11 tramite **Vortice** (MIT); fallback WARP; swap chain flip-model "for composition" |
| Lingua | italiano, testi in risorse `.resw` (M1) presi da `linux-spice-client/linux_spice_client/core/texts.py` |
| Identità | «Windows Spice Client», id `org.windowsspiceclient.WindowsSpiceClient` |
| Test | **xunit v3** su Microsoft.Testing.Platform (`global.json`) |
| USB reale | tramite **UsbDk**, in M8 |

**Dipendenze approvate:** Microsoft.WindowsAppSDK, Microsoft.Windows.SDK.BuildTools,
Vortice.* (Direct3D11, DXGI, WIC, MediaFoundation), H.NotifyIcon.WinUI, Concentus,
CommunityToolkit.Mvvm, xunit.v3. **Chiedere prima di aggiungerne altre.**

---

## 3. Struttura

```
windows-spice-client/
├── AGENTS.md / CLAUDE.md / PIANO.md / README.md / LICENSE / NOTICE
├── Directory.Build.props        impostazioni comuni (avvisi come errori, analizzatori)
├── global.json                  SDK 10.0.404+, test su Microsoft.Testing.Platform
├── WindowsSpiceClient.sln
├── src/
│   ├── SpiceKit/                net10.0, NESSUNA dipendenza da WinUI/Direct3D
│   │   ├── Wire/                ByteReader, ByteWriter, WireException (M0); link, messaggi base/main (M2);
│   │   │                        Geometry, Images, DisplayMessages (M3)
│   │   ├── VVFile/              VVFileParser, ConnectionProfile, SecretTicket, Hotkey (M1)
│   │   ├── UsbRedirWire/        UsbFilter (M1; il resto in M7)
│   │   ├── Proxmox/             ProxmoxClient (API + regole TLS), modelli, ProxmoxException (M1)
│   │   ├── Transport/           TcpConnector (TCP, proxy CONNECT, TLS), TlsVerifier, ChannelLinker,
│   │   │                        SpiceEndpoint, HostSubject/Pem, RsaTicketEncryptor (M2)
│   │   ├── Codecs/              PixelBuffer + Canvas (operazioni raster), BitmapDecoder, LzDecoder,
│   │   │                        QuicDecoder/QuicModel, GlzDecoder, Lz4Decoder (M3)
│   │   ├── Channels/            ChannelConnection (protocollo base), ChannelOpener, MainChannel, SpiceLog (M2);
│   │   │                        DisplayChannel (superfici, contropressione), ImageResolver (cache) (M3)
│   │   └── Session/             SpiceSession (facciata), SessionFailure/SessionState (M2)
│   └── WindowsSpiceClient/      app WinUI 3
│       ├── Assets/              app.ico (multi-risoluzione), PNG
│       ├── Display/             D3D11Presenter (texture della superficie, adattamento con la trasformazione
│       │                        della swap chain), SwapChainPanelInterop
│       ├── Diagnostics/         AppLog, SelfTest
│       ├── Shell/               SingleInstance (AppInstance; Main in Program.cs) (M2)
│       ├── Services/            AppController, ServerStore, Settings, CredentialStore,
│       │                        FileAssociation, VmListLoader, Strings, JsonFiles
│       ├── Strings/it-IT/       Resources.resw (testi; x:Uid nello XAML, Strings.Get nel codice)
│       └── Views/               Welcome, VmList, Preferences, ServerEditor, Session, Dialogs, WindowChrome
├── tests/SpiceKit.Tests/        xunit v3; Fixtures/ copiate da macos-spice-client; TestHttpsServer
└── tools/                       build.ps1, selftest_proxmox.py, make_icon.py, capture-window.ps1
```

Cartelle previste nelle prossime milestone (vedi PIANO.md §2): `SpiceKit/{Agent,
UsbEmulation}`, il progetto `SpiceKit.Windows` (WASAPI, WinSCard, UsbDk,
Media Foundation; da creare quando serve la prima di queste parti) e, nell'app, `Input/`
(e in `Shell/` area di notifica e notifiche). Gestione credenziali è rimasta nell'app (`Services/CredentialStore`): sono poche
righe di P/Invoke e servono solo all'interfaccia.

Dati dell'app: `%APPDATA%\WindowsSpiceClient\` (`settings.json`, `servers.json`,
`cluster-cas.json`, stessi nomi e campi del client Linux); segreti dei token in Gestione
credenziali (`org.windowsspiceclient.WindowsSpiceClient/proxmox-token/<id>`).

Regole:
- `SpiceKit/Wire` e `SpiceKit/Codecs` sono **puri**: byte in ingresso, valori in uscita. Niente
  socket, thread, orologio né allocazioni per pixel nei cicli critici.
- `SpiceKit` emette aggiornamenti delle superfici (rettangolo + pixel BGRA premoltiplicati,
  oppure frame video). Il codice Direct3D sta solo in `WindowsSpiceClient/Display`.
- Un ciclo di lettura asincrono per canale. Gli eventi verso l'interfaccia passano dal
  `DispatcherQueue`.

---

## 4. Build, test, verifica

```powershell
tools\build.ps1                # build (avvisi = errori) + test
tools\build.ps1 -SelfTest      # + autoverifica dell'app
tools\build.ps1 -Publish       # + eseguibile autonomo Release in artifacts\win-x64
tools\build.ps1 -Proxmox       # + autoverifica contro il finto Proxmox di ..\windows-spike (serve Python)
tools\build.ps1 -Spice         # + autoverifica SPICE contro QEMU per Windows (serve Python e QEMU)
```

- **.NET SDK:** se `dotnet` non è nel PATH, lo script usa `%LOCALAPPDATA%\Microsoft\dotnet`
  (installazione per utente con `dotnet-install.ps1`, senza amministratore).
- **git:** MinGit portabile in `%LOCALAPPDATA%\Programs\MinGit\cmd\git.exe`.
- **Autoverifica** (`WindowsSpiceClient.exe --self-test <cartella>`): apre una alla volta
  Benvenuto, Macchine virtuali, Preferenze (due pagine) e una sessione, le disegna con
  `RenderTargetBitmap` in PNG (`welcome.png`, `vmlist.png`, …), rilegge un pixel dal buffer
  Direct3D dopo un clear di colore noto, scrive `self-test.txt` ed esce. Con `WSC_DATA_DIR`
  (impostata da `build.ps1`) usa una configurazione isolata e un server di prova, rimosso alla
  fine anche da Gestione credenziali; con `WSC_SELFTEST_SERVER` (impostata da
  `selftest_proxmox.py`) usa il finto Proxmox e prova anche la connessione con `spiceproxy` e
  la sessione SPICE che ne segue; con `WSC_SELFTEST_VV=a.vv;b.vv` (impostata da
  `selftest_spice.py`) apre quei file e riporta lo stato SPICE di ciascuno (QEMU in chiaro, TLS
  con CA e `host-subject` attraverso un proxy CONNECT, ticket errato). Per ogni sessione collegata
  salva lo schermo della VM letto dalla texture Direct3D in `screen-<nome>.png` (con le VM di prova:
  il testo del BIOS e di iPXE, 720×400), perché `RenderTargetBitmap` mostra lo SwapChainPanel nero.
  **Guardare i PNG** dopo ogni modifica all'interfaccia.
  **Usarla al posto delle catture dello schermo:** nella VM di sviluppo (senza GPU, sessione
  remota) `CopyFromScreen` e `PrintWindow` restituiscono fotogrammi vecchi e le finestre a
  composizione appaiono nere anche quando sono disegnate correttamente.
- Registro dell'app: `%LOCALAPPDATA%\WindowsSpiceClient\logs\app.log` (`WSC_LOG_DEBUG=1` aggiunge
  i messaggi di protocollo di SpiceKit).
- Istanza unica: un secondo avvio passa i suoi `.vv` all'istanza in esecuzione ed esce. Non vale
  per l'autoverifica né con `WSC_DATA_DIR`.

Prima di dichiarare finito un lavoro:
1. `tools\build.ps1 -SelfTest` termina con «Tutto OK» (zero avvisi, test verdi, autoverifica superata).
2. Il nuovo codice di parsing o di codec ha test con fixture (quelle macOS quando esistono).
3. Nessun segreto (ticket, password, token, CA di host reali) in fixture, log o commit.

**Ambiente di prova** (da `../windows-spike`): QEMU 11.1 per Windows con SPICE (in chiaro, TLS
con `password-secret`), finto Proxmox (`spike_env.py`) e proxy CONNECT stile Proxmox.
La VM di sviluppo non ha GPU (adattatore *Microsoft Basic Render Driver* = WARP): prestazioni,
decodifica video hardware e HiDPI vanno verificati su un PC fisico.

---

## 5. Porting da macOS: regole

- La fonte è `../macos-spice-client/Packages/SpiceKit` (MIT). Si traduce un modulo alla volta
  mantenendo i nomi dei tipi in stile C# (`DisplayServerMessage.DrawCopy`, valori uguali agli
  header) e la stessa struttura dei test.
- Le regole di `../macos-spice-client/AGENTS.md` §5 (.vv), §6 (protocollo), §7a–§7d (USB, ISO,
  smartcard, Proxmox) e §9 (sicurezza) **valgono anche qui**: rileggerle prima di ogni milestone.
- Costanti dagli header di spice-protocol (BSD). Codice LGPL (spice-gtk, spice-common,
  usbredirparser/host, libusb) solo come riferimento di comportamento: **non copiarlo**.
- Mai inventare costanti del protocollo: se non è verificabile, `// TODO(verify):` e segnalarlo.
- Scorciatoie Windows rispetto al Mac: gli scancode della tastiera arrivano già in set 1 (bit
  "extended" → prefisso `0xE0`); RSA-OAEP-SHA1 con `RSA.Encrypt`; zlib con `ZLibStream`.

## 6. Sicurezza (vincoli non negoziabili)

- TLS: se il `.vv` contiene `ca`, è l'**unica** radice di fiducia (`X509ChainTrustMode.CustomRootTrust`);
  `host-subject` va sempre verificato se presente; **nessuna** opzione per saltare la verifica,
  nemmeno in debug; i canali in `secure-channels` non passano mai in chiaro.
- Ticket SPICE solo in memoria, azzerati dopo l'uso, mai nei log. Token Proxmox solo in Gestione
  credenziali di Windows.
- Dati del server ostili: controllare ogni lunghezza e offset (`ByteReader`), limiti di memoria
  come su macOS (superfici ≤ 16384², budget 512 MiB, ≤ 64 stream, palette limitata, USB ≤ 4 MiB
  per trasferimento). Ogni limite ha un test di regressione.

## 7. Convenzioni

- Commenti e testi in italiano; identificatori in inglese.
- Errori: eccezioni tipizzate con messaggi presentabili all'utente; niente `!` (null-forgiving)
  fuori dai test senza un commento che spieghi perché.
- File sotto le ~600 righe; un tipo per file sopra le ~150 righe.
- Commit piccoli in stile Conventional Commits (`feat(wire): …`, `fix(display): …`).

## 8. Stato delle milestone

| # | Stato |
|---|---|
| **M0** – soluzione, app WinUI 3 con icona, SpiceKit + test, swap chain D3D11, build locale | ✅ 23/11/2026 |
| M1 – `.vv`, API Proxmox, Gestione credenziali, finestre Benvenuto / Macchine virtuali / Preferenze | ✅ 23/11/2026 |
| M2 – trasporto (TCP, TLS, proxy CONNECT), link e autenticazione, canale main; istanza unica | ✅ 23/11/2026 |
| M3 – display: superfici, comandi di disegno, decoder LZ, QUIC, GLZ e LZ4, presentazione D3D11 | ✅ 23/11/2026 |
| M4 … M9 | vedi PIANO.md §4 |
