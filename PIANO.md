# Windows Spice Client – piano di progetto (strategia 3b)

Client SPICE nativo per Windows con integrazione Proxmox VE. Il protocollo SPICE è
implementato in C# e lo schermo è disegnato con Direct3D 11. Non si usano remote-viewer,
spice-gtk né GTK.

Fonti:
- **macos-spice-client** (MIT) è il riferimento per il protocollo e per le funzioni. I moduli
  di SpiceKit si portano in C# uno per uno; regole di sicurezza, limiti e milestone vengono dal
  suo `AGENTS.md`.
- **linux-spice-client** (MIT) è il riferimento per l'organizzazione dell'interfaccia, i testi
  italiani, la logica Proxmox e le regole di riconnessione.
- Le **fixture binarie** di `Packages/SpiceKit/Tests/Fixtures` (codec, catture del display, `.vv`,
  TLS) si riusano così come sono: sono indipendenti dal linguaggio e rendono verificabile ogni
  decoder.

Attribuzione: file `NOTICE` come nella versione Linux (© Paweł Więcaszek, MIT).

---

## 1. Stack tecnologico

| Area | Scelta | Al posto di (macOS) |
|---|---|---|
| Linguaggio | C# 14, **.NET 10 LTS**, nullable e avvisi come errori | Swift 6 |
| Interfaccia | **WinUI 3** (Windows App SDK 1.8): Fluent, Mica, tema chiaro/scuro di sistema | SwiftUI |
| Schermo della VM | `SwapChainPanel` + swap chain DXGI flip-model, **Direct3D 11** (fallback WARP), shader HLSL | Metal / `CAMetalLayer` |
| Video (stream SPICE) | **Media Foundation** H.264/H.265 → texture D3D11 (DXVA quando disponibile) | VideoToolbox |
| Immagini | decoder C# per QUIC, LZ, GLZ, LZ4, ZLIB-GLZ (portati da SpiceCodecs); JPEG con **WIC** | decoder Swift + ImageIO |
| Rete e TLS | `Socket` + `SslStream`; CA del `.vv` come unica radice (`X509ChainTrustMode.CustomRootTrust`), controllo di `host-subject`, tunnel HTTP CONNECT | Network.framework |
| Ticket SPICE | `RSA.Encrypt(…, RSAEncryptionPadding.OaepSHA1)` | Security.framework |
| Audio | **WASAPI** (riproduzione e microfono), Opus | AVAudioEngine / AudioToolbox |
| Tastiera | Windows fornisce già gli scancode PC AT set 1 (con il bit "extended"): nessuna tabella di conversione; hook a basso livello per inviare Win, Alt+Tab ecc. mentre la finestra ha il focus | `Keymap.swift` |
| Puntatore | modalità client: posizione assoluta; modalità server: Raw Input + `ClipCursor` | `CGAssociateMouse…` |
| Appunti | API WinRT `Clipboard` (testo e immagini) | `NSPasteboard` |
| Smartcard | **WinSCard** (PC/SC) | CryptoTokenKit |
| USB emulato (ISO, lettore CCID) | portato da USBEmulation, nessun driver necessario | idem |
| USB reale | **UsbDk** (driver Red Hat/Daynix, Apache 2.0; installazione separata da amministratore) – funzione opzionale | IOKit |
| Token Proxmox | **Gestione credenziali di Windows** (`CredWrite`/`CredRead`) | Keychain |
| Impostazioni | JSON in `%LOCALAPPDATA%\WindowsSpiceClient` | UserDefaults |
| Notifiche | `AppNotificationManager` (toast con pulsanti, per esempio «Avvia e riconnetti») | UserNotifications |
| Istanza unica, apertura dei `.vv` | `AppInstance.FindOrRegisterForKey` + reindirizzamento dell'attivazione | `application(_:open:)` |
| Log | `Microsoft.Extensions.Logging` su file, con dati sensibili mai registrati | `os.Logger` |
| Test | **xUnit** + le fixture del progetto macOS | Swift Testing |

### Dipendenze esterne proposte (tutte con licenza permissiva, da approvare)

| Pacchetto | Licenza | Serve per |
|---|---|---|
| Microsoft.WindowsAppSDK | MIT | WinUI 3, notifiche, istanza unica |
| Vortice.Direct3D11 / DXGI / WIC / MediaFoundation | MIT | accesso a Direct3D, WIC e Media Foundation senza scrivere a mano l'interop COM |
| H.NotifyIcon.WinUI | MIT | icona nell'area di notifica (WinUI 3 non ce l'ha) |
| Concentus | BSD-3 | decodifica e codifica Opus in C# puro (in alternativa: libopus nativa) |
| CommunityToolkit.Mvvm | MIT | binding e comandi dell'interfaccia |
| xunit | Apache 2.0 | test |

Esclusi: spice-gtk, spice-common, usbredirparser/usbredirhost, libusb (LGPL), da usare solo
come riferimento di comportamento senza copiarne codice, come stabilito per la versione macOS.

---

## 2. Struttura della soluzione

```
windows-spice-client/
├── WindowsSpiceClient.sln
├── src/
│   ├── SpiceKit/                  libreria .NET senza dipendenze da WinUI
│   │   ├── VVFile/                parser .vv → ConnectionProfile
│   │   ├── Wire/                  costanti e messaggi (puro: byte → valori)
│   │   ├── Transport/             socket, TLS, proxy CONNECT, link + autenticazione
│   │   ├── Channels/              Main, Display, Inputs, Cursor, Playback, Record, Smartcard, UsbRedir
│   │   ├── Agent/                 vdagent: appunti, monitor, trasferimento file
│   │   ├── Codecs/                QUIC, LZ, GLZ, LZ4, ZLIB-GLZ, bitmap (puro)
│   │   ├── UsbRedirWire/          protocollo usbredir 0.7 (puro)
│   │   ├── UsbEmulation/          CD-ROM SCSI (ISO), lettore CCID virtuale
│   │   ├── Proxmox/               API, modelli, regole TLS, riconnessione
│   │   └── Session/               SpiceSession: facciata pubblica, eventi verso l'interfaccia
│   ├── SpiceKit.Windows/          parti legate a Windows: WASAPI, WinSCard, UsbDk, credenziali, Media Foundation
│   └── WindowsSpiceClient/        app WinUI 3
│       ├── Views/                 Benvenuto, Macchine virtuali, Preferenze, Sessione
│       ├── Display/               SwapChainPanel, renderer D3D11, shader HLSL
│       ├── Input/                 tastiera (scancode, hook), puntatore, scorciatoie
│       ├── Shell/                 area di notifica, notifiche, istanza unica, associazione .vv
│       ├── Strings/it-IT/         testi (Resources.resw) presi dalla versione Linux
│       └── Assets/                icona (.ico multi-risoluzione dai PNG esistenti)
└── tests/
    ├── SpiceKit.Tests/            xUnit; fixture copiate da macos-spice-client
    └── FakeProxmox/               finto Proxmox della prova (Python), con QEMU per Windows
```

Regole riprese dal progetto macOS:
- `Wire` e `Codecs` sono puri (niente socket, thread né orologio): si testano con le fixture e
  si possono sottoporre a fuzzing.
- `SpiceKit` non conosce Direct3D. Emette aggiornamenti delle superfici (rettangolo + pixel
  BGRA, oppure frame video) e il renderer li applica.
- Ogni canale ha il suo ciclo di lettura asincrono (`Task`/`Channel<T>`) e l'interfaccia riceve
  gli eventi sul thread della UI.

---

## 3. Interfaccia (stessa organizzazione delle altre due versioni)

| Finestra / elemento | Contenuto | Realizzazione Windows |
|---|---|---|
| **Benvenuto** | icona, «Apri file di connessione…», «Macchine virtuali», trascinamento di un `.vv` | finestra WinUI con Mica |
| **Macchine virtuali** (Ctrl+0) | VM di tutti i server con lo stato aggiornato ogni 10 s (Ctrl+R); Connetti / Riprendi e connetti / Avvia e connetti; menu Alimentazione (⏻ e clic destro) con conferma per Reset e Spegni forzatamente | `ListView` raggruppata per server, `CommandBar`, `MenuFlyout` |
| **Preferenze** | Generale (schermo intero, riconnessione automatica, icona nell'area di notifica, app predefinita per i `.vv`, appunti) · Proxmox (server, token, impronta del certificato) | `NavigationView` con pagine «Generale» e «Proxmox», `ContentDialog` per aggiungere un server e confrontare l'impronta SHA-256 |
| **Sessione** (una finestra per schermo, fino a 4) | schermo della VM; barra con Alimentazione, Ctrl+Alt+Del, Schermo intero, USB, Monta ISO, Smartcard, Microfono; stato della riconnessione dentro la finestra stessa | finestra WinUI con barra del titolo personalizzata (`ExtendsContentIntoTitleBar`), `SwapChainPanel`, barra nascosta a schermo intero |
| **Area di notifica** | sessioni aperte con il loro menu (alimentazione, Riprova ora, Annulla riconnessione, Disconnetti) | H.NotifyIcon |
| **Notifiche** | connessione persa / riconnessa / VM spenta con «Avvia e riconnetti» | toast di Windows con pulsanti |
| **Jump list** | Macchine virtuali, Apri file di connessione…, Preferenze | `JumpList` sull'icona nella barra delle applicazioni |

Lingua: **italiano** con i testi della versione Linux, in file di risorse (`.resw`) così da poter
aggiungere l'inglese in seguito.

Riconnessione: come su macOS, avviene **nella stessa finestra**, con un ticket nuovo dall'API e i
tempi della versione Linux (8 tentativi, 1 → 30 s, attesa del ritorno della rete). Dispositivi
condivisi, ISO e microfono vengono ripristinati.

---

## 4. Milestone

Ogni milestone termina con una build funzionante e testata. La durata è indicativa ed espressa
in settimane di lavoro.

| # | Contenuto | Verifica | Settimane |
|---|---|---|---|
| **M0** | Soluzione, app WinUI 3 con icona, SpiceKit + test, swap chain D3D11 che pulisce lo schermo, CI locale (`dotnet build`/`dotnet test`) | eseguibile con icona; test verdi | 1 |
| **M1** | Parser `.vv` (fixture macOS), API Proxmox con regole TLS, Gestione credenziali, finestre Benvenuto / Macchine virtuali / Preferenze, alimentazione | finto Proxmox della prova | 2–3 |
| **M2** | Trasporto: TCP, TLS con CA del file e `host-subject`, proxy CONNECT, link + ticket RSA-OAEP, canale Main, elenco canali | QEMU per Windows (in chiaro, TLS, proxy: già pronti nella prova) | 2 |
| **M3** | Display: superfici, DRAW_FILL/COPY/BLEND/COPY_BITS/…, decoder LZ e QUIC, presentazione D3D11 | fixture `codecs/` e `display/lz.bin`; avvio di SeaBIOS visibile | 3–4 |
| **M4** | Input e cursore: scancode, hook tastiera, modalità client/server del puntatore, scorciatoie del `.vv`, Ctrl+Alt+Del | VM Linux/Windows reale | 2 |
| **M5** | Prestazioni: GLZ, ZLIB-GLZ, LZ4, JPEG, stream video H.264/H.265 con Media Foundation, controllo del flusso verso il renderer | fixture `display/glz.bin`, `lz4.bin`, `auto-glz.bin` | 3–4 |
| **M6** | Agente: risoluzione dinamica, più schermi, appunti, trascinamento di file nella VM; riconnessione nella stessa finestra | VM con spice-vdagent / guest tools | 2–3 |
| **M7** | Audio: riproduzione (PCM, Opus) e microfono (spento per impostazione predefinita) | VM con dispositivo audio SPICE | 2 |
| **M8** | ISO come CD-ROM USB, lettore smartcard CCID virtuale, smartcard via WinSCard; USB reale tramite UsbDk (opzionale) | QEMU con `usb-redir`; SeaBIOS legge l'ISO | 3–5 |
| **M9** | Rifinitura: area di notifica, toast, jump list, associazione `.vv`, accessibilità, installer | prova completa su Proxmox reale | 2 |

Totale indicativo: **22–30 settimane di lavoro**. Dopo M1 il programma gestisce già le VM, e
dopo M4 è un client SPICE utilizzabile.

---

## 5. Sicurezza (vincoli ripresi dall'audit della versione macOS)

- TLS: solo la CA del `.vv` come radice, quando è presente; `host-subject` obbligatorio se
  indicato; nessuna opzione per saltare la verifica; i canali in `secure-channels` non passano
  mai in chiaro.
- Ticket SPICE solo in memoria, azzerati dopo l'uso, mai nei log. Token Proxmox solo in
  Gestione credenziali.
- Tutti i dati del server sono considerati ostili: controllo di ogni lunghezza e offset, limiti
  di memoria (superfici ≤ 16384², budget 512 MiB, ≤ 64 stream video, cache della palette
  limitata, trasferimenti USB ≤ 4 MiB), fuzzing di `Wire` e `Codecs`.
- Gli appunti si condividono solo se attivati e solo mentre la finestra della sessione ha il
  focus.
- Ogni limite introdotto ha un test di regressione.

---

## 6. Ambiente di sviluppo e verifica

**Su questa macchina mancano:** .NET 10 SDK e git. Posso installarli io con winget, come per la
prova; Visual Studio non è indispensabile, perché bastano `dotnet build` e `dotnet test`.

**Limite importante:** questo Windows è una VM con scheda video *Red Hat QXL*, senza GPU.
Direct3D 11 funziona in modalità software (WARP), quindi lo sviluppo e i test funzionali si
possono fare qui. **Prestazioni, decodifica video hardware e HiDPI vanno però verificati su un PC
fisico con GPU.**

**Per i test servono anche:**
- QEMU per Windows e il finto Proxmox (già installati e pronti dalla prova);
- un **Proxmox reale** con almeno una VM Linux (spice-vdagent) e una Windows (guest tools), per
  M4 e le milestone successive.

---

## 7. Decisioni (confermate il 23/11/2026: tutte le opzioni consigliate)

Le decisioni in vigore sono riportate in `AGENTS.md` §2. Questo elenco resta come storico della scelta.

1. **Distribuzione:** eseguibile autonomo (self-contained) con icona + installer Inno Setup, che
   registra anche l'associazione `.vv` senza richiedere permessi di amministratore *(consigliato)*.
   L'alternativa è un pacchetto MSIX, che per essere installato richiede un certificato di firma.
2. **Versione minima di Windows:** Windows 10 22H2 e Windows 11 *(consigliato)*, oppure solo
   Windows 11.
3. **Dipendenze** della tabella in §1: approvazione dell'elenco.
4. **USB reale tramite UsbDk:** inclusa in M8 oppure rinviata. Le funzioni emulate (ISO e
   lettore CCID) restano comunque.
5. **Nome e identificativo dell'app:** proposta «Windows Spice Client», id
   `org.windowsspiceclient.WindowsSpiceClient`.
