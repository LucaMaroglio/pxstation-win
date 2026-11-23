<#
.SYNOPSIS
  Build locale (la "CI" del progetto, AGENTS.md §4): compila con avvisi come errori, esegue i
  test e, a richiesta, l'autoverifica dell'app e la pubblicazione dell'eseguibile autonomo.

.EXAMPLE
  tools\build.ps1                  # build + test
  tools\build.ps1 -SelfTest        # + autoverifica (renderer Direct3D e interfaccia XAML)
  tools\build.ps1 -Publish         # + eseguibile autonomo Release in artifacts\win-x64
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [switch] $SelfTest,
    [switch] $Publish
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

    if ($Publish) {
        $out = Join-Path $root 'artifacts\win-x64'
        Invoke-Step 'Publish' {
            & $dotnet publish src\WindowsSpiceClient\WindowsSpiceClient.csproj -c Release -r win-x64 -p:Platform=x64 -o $out -v:minimal
        }
        $exe = Join-Path $out 'WindowsSpiceClient.exe'
        Write-Host "Eseguibile: $exe"
    }

    if ($SelfTest) {
        $report = Join-Path $root 'artifacts\self-test'
        Remove-Item -Recurse -Force $report -ErrorAction SilentlyContinue
        Write-Host "`n=== Autoverifica ($exe)" -ForegroundColor Cyan
        $process = Start-Process $exe -ArgumentList '--self-test', $report -PassThru
        if (-not $process.WaitForExit(60000)) { Stop-Process -Id $process.Id; throw 'Autoverifica: timeout.' }
        $lines = Get-Content (Join-Path $report 'self-test.txt') -Encoding UTF8
        $lines | ForEach-Object { Write-Host "  $_" }
        if ($lines -match 'ERRORE' -or -not ($lines -match '^xaml: OK')) { throw 'Autoverifica non superata.' }
        Write-Host "  immagine dell'interfaccia: $(Join-Path $report 'ui.png')"
    }

    Write-Host "`nTutto OK." -ForegroundColor Green
}
finally {
    Pop-Location
}
