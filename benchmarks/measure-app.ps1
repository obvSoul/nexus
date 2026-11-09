# Measures the published application on an existing index: start-up time, idle memory and idle CPU.
# Usage: measure-app.ps1 -Exe <Nexus.App.exe> -Index <index copy> [-Starts 3] [-IdleSeconds 30]
# The database is used through the NEXUS_DB variable; the real index of the machine is not touched.
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Index, [int]$Starts = 3, [int]$IdleSeconds = 30, [int]$SettleSeconds = 70)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$env:NEXUS_DB = $Index
$AE = [Windows.Automation.AutomationElement]; $CT = [Windows.Automation.ControlType]

function Close-App($p) {
    [void]$p.CloseMainWindow()
    if (-not $p.WaitForExit(20000)) { $p.Kill(); Write-Output "  (arrêt forcé)" }
}

$times = @()
for ($i = 1; $i -le $Starts; $i++) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $Exe -PassThru
    do { Start-Sleep -Milliseconds 20; $p.Refresh() } until ($p.MainWindowHandle -ne 0 -or $sw.Elapsed.TotalSeconds -gt 60)
    $ms = $sw.ElapsedMilliseconds
    $times += $ms
    Write-Output ("Démarrage {0} : fenêtre en {1} ms" -f $i, $ms)
    Start-Sleep -Seconds 2
    Close-App $p
    Start-Sleep -Seconds 1
}
Write-Output ("Démarrage médian : {0} ms" -f (($times | Sort-Object)[[int][math]::Floor($times.Count / 2)]))

$p = Start-Process $Exe -PassThru
do { Start-Sleep -Milliseconds 100; $p.Refresh() } until ($p.MainWindowHandle -ne 0)
Start-Sleep -Seconds 1
# No UI Automation here: a connected automation client changes what is being measured.
Start-Sleep -Seconds $SettleSeconds
$p.Refresh(); $cpu0 = $p.TotalProcessorTime
Start-Sleep -Seconds $IdleSeconds
$p.Refresh(); $cpu1 = $p.TotalProcessorTime
Write-Output ("Repos ({0} s) : Working Set {1} Mo | mémoire privée {2} Mo | CPU {3:F3} s consommées ({4:F3} % d'un coeur)" -f `
    $IdleSeconds, [math]::Round($p.WorkingSet64 / 1MB), [math]::Round($p.PrivateMemorySize64 / 1MB), ($cpu1 - $cpu0).TotalSeconds, (100 * ($cpu1 - $cpu0).TotalSeconds / $IdleSeconds))
Close-App $p
