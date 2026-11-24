<#
.SYNOPSIS
  Build locale (la "CI" del progetto, AGENTS.md §4): compila con avvisi come errori, esegue i
  test e, a richiesta, l'autoverifica dell'app e la pubblicazione dell'eseguibile autonomo.

.EXAMPLE
  tools\build.ps1                  # build + test
  tools\build.ps1 -SelfTest        # + autoverifica (renderer Direct3D e interfaccia XAML)
  tools\build.ps1 -Publish         # + eseguibile autonomo Release in artifacts\win-x64
  tools\build.ps1 -Proxmox         # autoverifica contro il finto Proxmox di ..\windows-spike
  tools\build.ps1 -Spice           # autoverifica SPICE contro QEMU (in chiaro, TLS via proxy, ticket errato)
  tools\build.ps1 -Installer       # + installer Inno Setup in artifacts\installer (implica -Publish)
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [switch] $SelfTest,
    [switch] $Publish,
    [switch] $Proxmox,
    [switch] $Spice,
    [switch] $Installer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# dotnet: nel PATH, oppure l'installazione per utente (%LOCALAPPDATA%\Microsoft\dotnet).
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw '.NET SDK non trovato: vedi AGENTS.md §4.' }

function Invoke-Step([string] $title, [scriptblock] $action) {
    Write-Host "`n=== $title" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$title non riuscito (codice $LASTEXITCODE)." }
}

Push-Location $root
try {
    Invoke-Step 'Build' { & $dotnet build WindowsSpiceClient.sln -c $Configuration -p:Platform=x64 -v:minimal }
    Invoke-Step 'Test' { & $dotnet test --project tests\SpiceKit.Tests\SpiceKit.Tests.csproj -c $Configuration --no-build }

    $exe = Join-Path $root "src\WindowsSpiceClient\bin\x64\$Configuration\net10.0-windows10.0.19041.0\win-x64\WindowsSpiceClient.exe"

    if ($Installer) { $Publish = $true }

    if ($Publish) {
        $out = Join-Path $root 'artifacts\win-x64'
        Invoke-Step 'Publish' {
            & $dotnet publish src\WindowsSpiceClient\WindowsSpiceClient.csproj -c Release -r win-x64 -p:Platform=x64 -o $out -v:minimal
        }
        $exe = Join-Path $out 'WindowsSpiceClient.exe'
        Write-Host "Eseguibile: $exe"
    }

    if ($Installer) {
        # Inno Setup 7: nel PATH o nelle cartelle d'installazione abituali (vedi README, «Installer»).
        $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
        if (-not $iscc) {
            $iscc = @("$env:ProgramFiles\Inno Setup 7\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
                "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
        }
        if (-not $iscc) { throw 'Inno Setup 7 non trovato (ISCC.exe): installalo da https://jrsoftware.org/isdl.php (vedi README).' }
        $version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Encoding UTF8)).Project.PropertyGroup.Version |
            Where-Object { $_ } | Select-Object -First 1
        Invoke-Step "Installer $version" {
            & $iscc /Q "/DAppVersion=$version" '/DArch=x64' "/DSourceDir=$out" (Join-Path $root 'installer\WindowsSpiceClient.iss')
        }
        Write-Host "Installer: $(Join-Path $root "artifacts\installer\WindowsSpiceClient-$version-x64-setup.exe")"
    }

    function Test-Report([string] $report, [string[]] $required) {
        $lines = Get-Content (Join-Path $report 'self-test.txt') -Encoding UTF8
        $lines | ForEach-Object { Write-Host "  $_" }
        $captures = @($lines -cmatch '^xaml: OK').Count
        if ($lines -cmatch 'ERRORE' -or $captures -lt 5) { throw 'Autoverifica non superata.' }
        foreach ($pattern in $required) {
            if (-not ($lines -cmatch $pattern)) { throw "Autoverifica non superata: manca '$($pattern)'." }
        }
        Write-Host "  immagini dell'interfaccia: $report\*.png"
    }

    if ($SelfTest) {
        $report = Join-Path $root 'artifacts\self-test'
        Remove-Item -Recurse -Force $report -ErrorAction SilentlyContinue
        Write-Host "`n=== Autoverifica ($exe)" -ForegroundColor Cyan
        # Configurazione isolata: l'autoverifica aggiunge un server di prova senza toccare quella reale.
        $env:WSC_DATA_DIR = Join-Path $report 'data'
        try {
            $process = Start-Process $exe -ArgumentList '--self-test', "`"$report`"" -PassThru
            if (-not $process.WaitForExit(120000)) { Stop-Process -Id $process.Id; throw 'Autoverifica: timeout.' }
        }
        finally {
            Remove-Item Env:WSC_DATA_DIR
        }
        Test-Report $report @('^risorse: OK', '^direct3d: OK', 'errore mostrato', '^area di notifica: OK icona mostrata', '^jump list: OK',
            '^accessibilità benvenuto: OK', '^accessibilità preferenze generali: OK')
    }

    function Get-Python {
        $python = (Get-Command python -ErrorAction SilentlyContinue | Where-Object Source -notlike '*WindowsApps*').Source
        if (-not $python) { $python = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe' }
        if (-not (Test-Path $python)) { throw 'Python non trovato (serve per l''ambiente di ..\windows-spike).' }
        $python
    }

    if ($Proxmox) {
        $report = Join-Path $root 'artifacts\self-test-proxmox'
        Remove-Item -Recurse -Force $report -ErrorAction SilentlyContinue
        $python = Get-Python
        Invoke-Step "Autoverifica con il finto Proxmox ($exe)" { & $python (Join-Path $PSScriptRoot 'selftest_proxmox.py') $exe $report }
        Test-Report $report @('^risorse: OK', '^direct3d: OK', '=paused', '^connessione .*: OK', '^spice .*: connessa',
            '^riconnessione debian-test: OK', '^notifiche debian-test: OK', '^menu sessione debian-test: OK')
    }

    if ($Spice) {
        $report = Join-Path $root 'artifacts\self-test-spice'
        Remove-Item -Recurse -Force $report -ErrorAction SilentlyContinue
        $python = Get-Python
        Invoke-Step "Autoverifica SPICE con QEMU ($exe)" { & $python (Join-Path $PSScriptRoot 'selftest_spice.py') $exe $report }
        Test-Report $report @('^direct3d: OK', '^spice in-chiaro: connessa', '^spice proxmox-tls: connessa',
            '^schermo in-chiaro: OK', '^schermo proxmox-tls: OK',
            '^input in-chiaro: OK puntatore Server', '^input proxmox-tls: OK', '^video video: OK', '^audio audio: OK .*tono ~4[34][0-9] Hz', '^iso usb: OK', '^smartcard usb: OK', '^riconnessione in-chiaro: OK nessun tentativo',
            '^menu sessione in-chiaro: OK', '^accessibilità sessione: OK',
            '^spice ticket-errato: non riuscita \(ticket\)', '^spice senza server: non riuscita')
    }

    Write-Host "`nTutto OK." -ForegroundColor Green
}
finally {
    Pop-Location
}
