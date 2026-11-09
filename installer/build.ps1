# Builds the optimized Windows x64 release and the installer.
# Usage: installer\build.ps1 [-Version 1.0.1] [-SkipTests]
# Result: artifacts\NEXUS-Setup-<version>.exe
param([string]$Version, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 est introuvable (winget install JRSoftware.InnoSetup).' }

if (-not $SkipTests) {
    dotnet test Nexus.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Des tests ont échoué : installateur non généré.' }
}

$app = "artifacts\stage-$Version"  # one build area per version: a running older copy is never touched
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish src/Nexus.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:Version=$Version -o $app -v q
if ($LASTEXITCODE -ne 0) { throw 'La publication a échoué.' }
Get-ChildItem $app -Filter *.pdb -Recurse | Remove-Item -Force

& $iscc /Qp "/DAppVersion=$Version" installer\nexus.iss
if ($LASTEXITCODE -ne 0) { throw "La compilation de l'installateur a échoué." }

$setup = Join-Path $root "artifacts\NEXUS-Setup-$Version.exe"
Write-Output ("Installateur : {0} ({1:N0} Mo)" -f $setup, ((Get-Item $setup).Length / 1MB))
