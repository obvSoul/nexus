# End-to-end test of the installers in a sandbox folder: install, single instance, update, uninstall (keeping then deleting data).
# Usage: installer\test-installer.ps1 -Sandbox <empty folder> [-Old 1.0.0] [-New 1.0.1]
param([Parameter(Mandatory)][string]$Sandbox, [string]$Old = '1.0.0', [string]$New = '1.0.1')

$root = Split-Path $PSScriptRoot -Parent
$setupOld = Join-Path $root "artifacts\NEXUS-Setup-$Old.exe"
$setupNew = Join-Path $root "artifacts\NEXUS-Setup-$New.exe"
$appDir = Join-Path $Sandbox 'app'
$exe = Join-Path $appDir 'Nexus.App.exe'
$regKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7E0C5B62-3F4A-4C1D-9B7E-2A6D8F1C4E90}_is1'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NEXUS.lnk'
$env:NEXUS_DB = Join-Path $Sandbox 'index\t.db'   # the real index of the machine is never used

function Check($label, [bool]$ok) { "{0,-4} {1}" -f $(if ($ok) { 'OK' } else { 'KO' }), $label }
$leaf = [regex]::Escape('\' + (Split-Path $Sandbox -Leaf) + '\app\Nexus.App.exe')   # the sandbox may be given with a short 8.3 path
function Ours { @(Get-Process Nexus.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -match $leaf }) }
function RunSetup($file, [string[]]$arguments) { (Start-Process $file -ArgumentList $arguments -PassThru).WaitForExit() } # not -Wait: it would also wait for the relaunched app
function WaitOurs($n) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ((Ours).Count -ne $n -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 300 }; (Ours).Count -eq $n }
function CloseOurs { foreach ($p in Ours) { $p.Refresh(); [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(20000)) { $p.Kill() } } }
function Version { (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0] }
function Uninstall($extra) {
    $u = Join-Path $appDir 'unins000.exe'
    Start-Process $u -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') + $extra) -Wait
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path $regKey) -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 300 } # the uninstaller relaunches itself from a temp copy
    Start-Sleep -Seconds 2
}

if (Test-Path $regKey) { throw 'NEXUS est déjà installé sur cette session : test annulé pour ne pas toucher à cette installation.' }

# ---- installation
RunSetup $setupOld '/VERYSILENT', '/SP-', '/SUPPRESSMSGBOXES', "/DIR=`"$appDir`""
Check "installation de $Old" (Test-Path $exe)
Check "version installée = $Old" ((Version) -eq $Old)
Check 'installation sans droits administrateur (clé utilisateur)' (Test-Path $regKey)
Check 'raccourci du menu Démarrer' (Test-Path $shortcut)
Check "l'application démarre après l'installation" (WaitOurs 1)
Start-Sleep -Seconds 4

# ---- single instance
Start-Process $exe; Start-Sleep -Seconds 4
Check 'une seule instance à la fois' ((Ours).Count -eq 1)
CloseOurs
Check 'base créée' (Test-Path $env:NEXUS_DB)

# ---- update (same arguments as the button in Settings)
$marker = (Get-Item $env:NEXUS_DB).Length
RunSetup $setupNew '/SILENT', '/SP-', '/NOCANCEL'
Check "mise à jour vers $New, au même emplacement" ((Version) -eq $New)
Check "une seule entrée dans la liste des programmes, version $New" ((Get-ItemProperty $regKey).DisplayVersion -eq $New)
Check "relance après la mise à jour" (WaitOurs 1)
Start-Sleep -Seconds 3
CloseOurs
Check 'index conservé par la mise à jour' ((Test-Path $env:NEXUS_DB) -and (Get-Item $env:NEXUS_DB).Length -ge $marker)

# ---- uninstall, keeping the data (LOCALAPPDATA points to the sandbox: the real data cannot be touched)
$fakeLocal = Join-Path $Sandbox 'local'
New-Item -ItemType Directory -Force (Join-Path $fakeLocal 'Nexus') | Out-Null
Set-Content (Join-Path $fakeLocal 'Nexus\nexus.db') 'x'
$realLocal = $env:LOCALAPPDATA
$env:LOCALAPPDATA = $fakeLocal
Uninstall @()
$env:LOCALAPPDATA = $realLocal
Check 'programme supprimé' (-not (Test-Path $exe))
Check 'dossier du programme entièrement retiré' (-not (Test-Path $appDir))
Check 'entrée retirée de la liste des programmes' (-not (Test-Path $regKey))
Check 'raccourci retiré' (-not (Test-Path $shortcut))
Check 'données conservées par défaut' (Test-Path (Join-Path $fakeLocal 'Nexus\nexus.db'))

# ---- uninstall, deleting the data
RunSetup $setupOld '/VERYSILENT', '/SP-', '/SUPPRESSMSGBOXES', "/DIR=`"$appDir`""
[void](WaitOurs 1); Start-Sleep -Seconds 3; CloseOurs   # the installer starts NEXUS, and an uninstall refuses to run while it is open
$env:LOCALAPPDATA = $fakeLocal
Uninstall @('/DELETEDATA=1')
$env:LOCALAPPDATA = $realLocal
Check 'données supprimées sur demande' (-not (Test-Path (Join-Path $fakeLocal 'Nexus')))
Check 'tout est retiré' ((-not (Test-Path $appDir)) -and (-not (Test-Path $regKey)))
