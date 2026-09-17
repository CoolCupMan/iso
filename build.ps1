<#
.SYNOPSIS
    Baut IsoForge.exe mit einer eigenen, eindeutigen Build-Kennung.

.DESCRIPTION
    Jeder Lauf erzeugt standardmaessig eine neue Kennung. Sie steckt in der Programmdatei, im
    Installationspfad, in der Datentraegerbezeichnung erzeugter Abbilder und im Namen der
    Starteintraege - so lassen sich mehrere Staende nebeneinander installieren und betreiben.

.PARAMETER BuildId
    Vorgegebene GUID statt einer neu erzeugten. Damit laesst sich ein Build reproduzieren.

.PARAMETER Configuration
    Release (Vorgabe) oder Debug.

.PARAMETER OutputDirectory
    Ablage der fertigen Programmdatei. Vorgabe: .\artifacts

.PARAMETER SkipTests
    Ueberspringt die Testlaeufe.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -BuildId 6f9619ff-8b86-d011-b42d-00cf4fc964ff
#>
[CmdletBinding()]
param(
    [string] $BuildId,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

function Get-ShortId {
    <#  Muss Zeichen fuer Zeichen dem Verfahren in BuildIdentity.cs entsprechen:
        die ersten fuenf Bytes der GUID, Crockford-Base32, acht Stellen. #>
    param([guid] $Id)

    $alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'
    $bytes = $Id.ToByteArray()

    [uint64] $bits = 0
    for ($i = 0; $i -lt 5; $i++) {
        $bits = ($bits -shl 8) -bor $bytes[$i]
    }

    $result = [char[]]::new(8)
    for ($i = 7; $i -ge 0; $i--) {
        $result[$i] = $alphabet[[int]($bits -band 0x1F)]
        $bits = $bits -shr 5
    }

    -join $result
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Das .NET SDK wurde nicht gefunden. Es steht unter https://dotnet.microsoft.com/download bereit."
}

$guid = if ($BuildId) { [guid]::Parse($BuildId) } else { [guid]::NewGuid() }
$shortId = Get-ShortId -Id $guid

Write-Host ''
Write-Host '===========================================================================' -ForegroundColor Cyan
Write-Host '  IsoForge wird gebaut' -ForegroundColor Cyan
Write-Host '===========================================================================' -ForegroundColor Cyan
Write-Host "  Build-GUID    : $guid"
Write-Host "  Build-Kennung : $shortId"
Write-Host "  Konfiguration : $Configuration"
Write-Host ''

if (-not $SkipTests) {
    Write-Host '==> Tests' -ForegroundColor Cyan
    dotnet test (Join-Path $PSScriptRoot 'tests/IsoForge.Tests/IsoForge.Tests.csproj') `
        --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Die Tests sind fehlgeschlagen.' }
}

$publishDirectory = Join-Path $PSScriptRoot "obj/publish-$shortId"
if (Test-Path $publishDirectory) { Remove-Item $publishDirectory -Recurse -Force }

Write-Host ''
Write-Host '==> Programmdatei' -ForegroundColor Cyan
dotnet publish (Join-Path $PSScriptRoot 'src/IsoForge/IsoForge.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    --nologo `
    -p:IsoForgeBuildId=$guid `
    -p:InformationalVersion="1.0.0+$shortId"

if ($LASTEXITCODE -ne 0) { throw 'Der Build ist fehlgeschlagen.' }

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$targetName = "IsoForge-1.0.0-$shortId.exe"
$targetPath = Join-Path $OutputDirectory $targetName
Copy-Item (Join-Path $publishDirectory 'IsoForge.exe') $targetPath -Force

$hash = (Get-FileHash -Path $targetPath -Algorithm SHA256).Hash.ToLower()
"$hash *$targetName" | Set-Content -Path "$targetPath.sha256" -Encoding ascii

Write-Host ''
Write-Host '===========================================================================' -ForegroundColor Green
Write-Host '  Fertig.' -ForegroundColor Green
Write-Host '===========================================================================' -ForegroundColor Green
Write-Host "  Programmdatei : $targetPath"
Write-Host "  Groesse       : $([math]::Round((Get-Item $targetPath).Length / 1MB, 1)) MB"
Write-Host "  SHA-256       : $hash"
Write-Host "  Build-Kennung : $shortId"
Write-Host ''
Write-Host '  Die Datei ist eigenstaendig - auf dem Zielrechner wird kein .NET gebraucht.'
Write-Host '  Sie muss als Administrator gestartet werden.'
Write-Host ''
