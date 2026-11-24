; Installer di Windows Spice Client (M9) per Inno Setup 7.
;
; Si compila con tools\build.ps1 -Installer, che pubblica l'eseguibile autonomo e passa a ISCC:
;   /DAppVersion=0.1.0  /DSourceDir=<cartella di dotnet publish>  /DArch=x64|arm64
;
; - Per l'utente corrente, senza permessi di amministratore (%LOCALAPPDATA%\Programs); chi vuole
;   può scegliere «per tutti gli utenti» nella prima pagina (serve l'amministratore).
; - Associazione dei .vv come Services\FileAssociation.cs: l'app si registra come candidata
;   (OpenWithProgids, Programmi predefiniti); la scelta del predefinito resta all'utente.
; - Collegamento nel menu Start con lo stesso AppUserModelID dell'app (Program.cs), così
;   finestre, icona aggiunta alla barra delle applicazioni, jump list e notifiche sono un'unica app.
; - Disinstallazione: l'app toglie prima ciò che ha registrato in Windows (--unregister).
; - Le impostazioni (%APPDATA%\WindowsSpiceClient) e i token in Gestione credenziali restano:
;   una reinstallazione li ritrova. Si eliminano dalle Preferenze, rimuovendo i server.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\win-" + Arch
#endif

#define AppName "Windows Spice Client"
#define AppExe "WindowsSpiceClient.exe"
#define AppUserModelId "org.windowsspiceclient.WindowsSpiceClient"
#define ProgId "WindowsSpiceClient.vv"

[Setup]
AppId={{8C7F3E2A-5B41-4E2D-9F6A-2D1C7B3E9A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppCopyright=MIT – deriva da MacOS Spice Client © Paweł Więcaszek
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Windows 10 22H2 (build 19045) o Windows 11.
MinVersion=10.0.19045
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Inno Setup 7: anche il programma d'installazione è a 64 bit.
SetupArchitecture=x64
#endif
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
WizardStyle=modern
SetupIconFile=..\src\WindowsSpiceClient\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=WindowsSpiceClient-{#AppVersion}-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
LicenseFile=..\LICENSE

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "associate"; Description: "Apri i file di connessione .vv con {#AppName}"; GroupDescription: "Integrazione con Windows:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Registry]
; Tipo di file (ProgId), come FileAssociation.Register: HKA è HKCU per l'utente, HKLM per tutti.
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueData: "File di connessione SPICE (virt-viewer)"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExe}"",0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.vv"; ValueType: string; ValueName: "Content Type"; ValueData: "application/x-virt-viewer"; Flags: createvalueifdoesntexist; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.vv\OpenWithProgids"; ValueType: none; ValueName: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\MIME\Database\Content Type\application/x-virt-viewer"; ValueType: string; ValueName: "Extension"; ValueData: ".vv"; Flags: createvalueifdoesntexist; Tasks: associate
; «Apri con» e Impostazioni > App > App predefinite.
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".vv"; ValueData: ""
Root: HKA; Subkey: "Software\WindowsSpiceClient\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\WindowsSpiceClient\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Macchine virtuali Proxmox VE e connessioni SPICE"
Root: HKA; Subkey: "Software\WindowsSpiceClient\Capabilities\FileAssociations"; ValueType: string; ValueName: ".vv"; ValueData: "{#ProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\WindowsSpiceClient\Capabilities"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Notifiche (registrate per l'utente dal Windows App SDK) e jump list.
Filename: "{app}\{#AppExe}"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "Unregister"
