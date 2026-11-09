# Walks through the whole interface of the published application and reports any error written to the technical log.
# Usage: & ui-smoke.ps1 -Exe <Nexus.App.exe> -Index <index copy>
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Index)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$env:NEXUS_DB = $Index
$log = Join-Path $env:LOCALAPPDATA 'Nexus\logs\nexus.log'
$since = Get-Date
$AE = [Windows.Automation.AutomationElement]; $CT = [Windows.Automation.ControlType]
$p = Start-Process $Exe -PassThru
do { Start-Sleep -Milliseconds 200; $p.Refresh() } until ($p.MainWindowHandle -ne 0)
Start-Sleep -Seconds 4
$h = $p.MainWindowHandle
function Root { $AE::FromHandle($h) }
function All($type) { (Root).FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type))) }
function Sel($rx) { $i = (All $CT::ListItem) | Where-Object { $_.Current.Name -match $rx } | Select-Object -First 1; if (-not $i) { throw "introuvable: $rx" }; $i.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Click($rx) { $b = (All $CT::Button) | Where-Object { $_.Current.Name -match $rx } | Select-Object -First 1; if (-not $b) { throw "bouton introuvable: $rx" }; $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Texts { (All $CT::Text) | ForEach-Object { $_.Current.Name } }
function Search($q) { $e = (All $CT::Edit) | Select-Object -First 1; $e.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($q); Start-Sleep -Milliseconds 900 }
function Check($label, [bool]$ok) { "{0,-4} {1}" -f $(if ($ok) { 'OK' } else { 'KO' }), $label }

Sel 'SearchPageViewModel'; Start-Sleep 1
foreach ($q in 'ext:png', 'size:>1gb sort:-size', 'type:document dm:month', 'rapprt', '/^read.*\.md$/', 'content:windows', 'regex:(', '"', 'is:folder path:Users') {
    Search $q
    Check "recherche $q -> $((Texts | Where-Object { $_ -match 'résultat|Aucun|invalide|Expression' } | Select-Object -First 1))" ($p.Responding -and -not $p.HasExited)
}
Search 'ext:png'
foreach ($hd in 'Taille', 'Nom', 'Modifié') {
    $c = (All $CT::HeaderItem) | Where-Object { $_.Current.Name -eq $hd } | Select-Object -First 1
    if ($c) { $c.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 500 }
    Check "tri par $hd" ($null -ne $c)
}
Click '^Images$'; Start-Sleep -Milliseconds 700
Check 'filtre rapide' ((All $CT::Edit | Select-Object -First 1).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -match 'type:image')
Search ''

Sel 'StorageViewModel'; Start-Sleep 7
foreach ($t in 'Plus gros fichiers', 'Plus gros dossiers', 'Temporaires et caches', 'Explorateur') { Sel $t; Start-Sleep 2; Check "onglet $t" $p.Responding }
Sel 'AppsViewModel'; Start-Sleep 7
foreach ($t in 'Plus légères', '^Nom$', "Date d'installation", 'Plus lourdes') { Sel $t; Start-Sleep -Milliseconds 500; Check "tri apps $t" $p.Responding }
$row = (All $CT::DataItem) | Select-Object -First 1
$row.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1
Check 'détail application' ((Texts | Where-Object { $_ -match 'Déclarée par' }).Count -ge 1)
Sel 'DuplicatesViewModel'; Start-Sleep 1
Click '^Rechercher$'
$sw = [Diagnostics.Stopwatch]::StartNew()
do { Start-Sleep 1 } until ((Texts | Where-Object { $_ -match 'groupes|Aucun doublon' }) -or $sw.Elapsed.TotalSeconds -gt 180)
Check "doublons : $((Texts | Where-Object { $_ -match 'groupes|Aucun doublon' } | Select-Object -First 1))" $p.Responding
Sel 'IndexationViewModel'; Start-Sleep 1
Check 'case C: cochée' ((((All $CT::CheckBox) | Where-Object { $_.Current.Name -match 'C:' } | Select-Object -First 1).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState) -eq 'On')
Sel 'SettingsViewModel'; Start-Sleep 2
Check "paramètres : $((Texts | Where-Object { $_ -match 'Base :' } | Select-Object -First 1))" $p.Responding
Sel 'DashboardViewModel'; Start-Sleep 2

Check 'application toujours réactive' ($p.Responding -and -not $p.HasExited)
[void]$p.CloseMainWindow()
Check 'fermeture propre' ($p.WaitForExit(20000))
$errors = @()
if (Test-Path $log) { $errors = Get-Content $log -Encoding UTF8 | Where-Object { $_ -match ' ERROR ' -and [datetime]($_.Substring(0, 19)) -ge $since.AddSeconds(-1) } }
Check "erreurs dans le journal : $($errors.Count)" ($errors.Count -eq 0)
$errors | Select-Object -First 5 | ForEach-Object { $_.Substring(0, [Math]::Min(400, $_.Length)) }
