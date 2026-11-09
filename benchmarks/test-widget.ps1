# End-to-end check of the search widget on a copy of an index: real shortcut, real windows, real monitors.
# Usage: test-widget.ps1 -Exe <Nexus.App.exe> -Index <copy of nexus.db> -Work <empty folder for the probe and screenshots>
# It briefly takes the keyboard focus and moves the mouse (restored at the end).
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Index, [Parameter(Mandatory)][string]$Work, [int]$SettleSeconds = 45, [byte[]]$HotkeyKeys = @(0x12), [byte]$HotkeyVk = 0x20, [uint32]$HotkeyMask = 1)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Namespace T -Name W -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
[DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
[DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint m, uint vk);
[DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, int m, IntPtr w, IntPtr l);
'@
[void][T.W]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
Add-Type -AssemblyName System.Windows.Forms

$AE = [Windows.Automation.AutomationElement]; $CT = [Windows.Automation.ControlType]
$fails = 0
function Check($name, $ok, $detail = '') { if ($ok) { "OK    $name $detail" } else { "ECHEC $name $detail"; $script:fails++ } }
function Key([byte]$vk, [bool]$up) { [T.W]::keybd_event($vk, 0, $(if ($up) { 2 } else { 0 }), [UIntPtr]::Zero) }
function Send-Keys([byte[]]$mods, [byte]$vk) { foreach ($m in $mods) { Key $m $false }; Key $vk $false; Key $vk $true; foreach ($m in $mods) { Key $m $true } }
# Never type into another application: Escape or Enter would act on whatever the user has in front.
function Press([byte[]]$mods, [byte]$vk) {
    $h = WidgetHandle
    if ($h -eq [IntPtr]::Zero -or [T.W]::GetForegroundWindow() -ne $h) { Check "touche $vk non envoyee : le widget n'a pas le focus" $false; return }
    Send-Keys $mods $vk
}
function WidgetHandle {
    $h = [IntPtr]::Zero
    while (($h = [T.W]::FindWindowExW([IntPtr]::Zero, $h, [NullString]::Value, 'Recherche rapide NEXUS')) -ne [IntPtr]::Zero) {
        $id = 0; [void][T.W]::GetWindowThreadProcessId($h, [ref]$id); if ($id -eq $p.Id) { return $h }
    }
    return [IntPtr]::Zero
}
function Hotkey { Send-Keys $HotkeyKeys $HotkeyVk }
function WaitFor([scriptblock]$c, [int]$ms = 5000) { $s = [Diagnostics.Stopwatch]::StartNew(); while (-not (& $c)) { if ($s.ElapsedMilliseconds -gt $ms) { return -1 }; Start-Sleep -Milliseconds 1 }; return $s.Elapsed.TotalMilliseconds }
function Visible { $h = WidgetHandle; ($h -ne [IntPtr]::Zero) -and [T.W]::IsWindowVisible($h) }
function Rows { $AE::FromHandle((WidgetHandle)).FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem))) }
function Box { $AE::FromHandle((WidgetHandle)).FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Edit))) }
function Type-Query($t) { (Box).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($t) }
function SelectedIndex { $r = @(Rows); for ($i = 0; $i -lt $r.Count; $i++) { if ($r[$i].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { return $i } }; -1 }
function Ram { $p.Refresh(); [math]::Round($p.WorkingSet64 / 1MB, 1) }
function Grab($file) {
    $r = New-Object T.W+RECT; [void][T.W]::GetWindowRect((WidgetHandle), [ref]$r)
    $b = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, $b.Size); $g.Dispose(); $b.Save($file, [Drawing.Imaging.ImageFormat]::Png); "      capture : $($r.R-$r.L) x $($r.B-$r.T) -> $file"
}

$probeDir = Join-Path $Work 'probe'; New-Item -ItemType Directory -Force $probeDir | Out-Null
$marker = Join-Path $Work 'opened.txt'; Remove-Item $marker -ErrorAction SilentlyContinue
$probe = Join-Path $probeDir 'nexus-widget-probe.cmd'
Set-Content -LiteralPath $probe -Encoding ASCII -Value "@echo opened> `"$marker`""
Set-Content -LiteralPath (Join-Path $probeDir 'nexus-widget-probe-2.txt') -Value 'x'

$cursor = New-Object T.W+POINT; [void][T.W]::GetCursorPos([ref]$cursor)
$before = [T.W]::GetForegroundWindow()
$env:NEXUS_DB = $Index
$p = Start-Process $Exe -PassThru
do { Start-Sleep -Milliseconds 100; $p.Refresh() } until ($p.MainWindowHandle -ne 0)
$main = $p.MainWindowHandle
"Attente de l'analyse ($SettleSeconds s)..."; Start-Sleep -Seconds $SettleSeconds
$main0 = Ram; "RAM avant le premier affichage : $main0 Mo"

# --- window minimised, real shortcut
[void][T.W]::ShowWindow($main, 6); Start-Sleep -Seconds 1
Hotkey
$first = WaitFor { Visible }
Check 'Alt+Espace ouvre le widget, fenetre principale reduite' ($first -ge 0) ("premier affichage {0:F0} ms" -f $first)
if ($first -lt 0) { $p.Kill(); Remove-Item Env:NEXUS_DB; "Arret : le widget ne s'ouvre pas."; exit 1 }
Start-Sleep -Milliseconds 300
$h = WidgetHandle
Check 'le widget a le focus' ([T.W]::GetForegroundWindow() -eq $h)
$ex = [T.W]::GetWindowLong($h, -20)
Check 'hors barre des taches et Alt+Tab (WS_EX_TOOLWINDOW)' (($ex -band 0x80) -ne 0)
Check 'toujours au-dessus (WS_EX_TOPMOST)' (($ex -band 0x8) -ne 0)
$r = New-Object T.W+RECT; [void][T.W]::GetWindowRect($h, [ref]$r); "      hauteur vide : $($r.B-$r.T) px"
Grab (Join-Path $Work 'widget-vide.png')

# --- search
$s = [Diagnostics.Stopwatch]::StartNew(); Type-Query 'nexus-widget-probe'
$t = WaitFor { @(Rows).Count -ge 2 } 4000
Check 'recherche dans le moteur partage' ($t -ge 0) ("{0:F0} ms du texte aux lignes, UI Automation comprise" -f $t)
Check 'premier resultat selectionne' ((SelectedIndex) -eq 0)
$focusOk = [T.W]::GetForegroundWindow() -eq $h
if ($focusOk) {
    Press @() 0x28; Start-Sleep -Milliseconds 200; Check 'fleche bas' ((SelectedIndex) -eq 1)
    Press @() 0x26; Start-Sleep -Milliseconds 200; Check 'fleche haut' ((SelectedIndex) -eq 0)
    Press @() 0x26; Start-Sleep -Milliseconds 200; Check 'la selection boucle' ((SelectedIndex) -eq (@(Rows).Count - 1))
    Press @() 0x28; Start-Sleep -Milliseconds 200
} else { Check 'focus perdu : clavier non teste' $false }
Grab (Join-Path $Work 'widget-resultats.png')

foreach ($q in 'ext:cmd nexus-widget', 'path:probe nexus', 'size:<1kb nexus-widget', 'dm:today nexus-widget', 'nexus-widgt-probe') {
    Type-Query $q; $c = -1; Start-Sleep -Milliseconds 700; $c = @(Rows).Count; Check "requete [$q]" ($c -gt 0) "$c ligne(s)"
}
Type-Query '.'; $w = WaitFor { @(Rows).Count -gt 5 } 4000; Start-Sleep -Milliseconds 400; $n = @(Rows).Count
Check 'limite du nombre de lignes' ($n -le 9) "$n lignes (reglage 9)"
Type-Query 'nexus-widget-probe.cmd'; [void](WaitFor { @(Rows).Count -eq 1 } 3000)

# --- Enter opens, the widget goes away
Press @() 0x0D
$o = WaitFor { Test-Path $marker } 6000
Check 'Entree ouvre le fichier' ($o -ge 0)
[void](WaitFor { -not (Visible) } 2000); Check 'le widget se masque apres ouverture' (-not (Visible))

# --- shortcut toggles, Escape hides, focus returns
Start-Sleep -Seconds 1; Hotkey; $again = WaitFor { Visible }; Check 'reouverture' ($again -ge 0) ("{0:F0} ms" -f $again)
Start-Sleep -Milliseconds 200
Check 'texte efface par defaut' ([string]::IsNullOrEmpty((Box).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value))
Hotkey; $off = WaitFor { -not (Visible) }; Check 'le raccourci masque aussi' ($off -ge 0)
Start-Sleep -Milliseconds 300; Hotkey; [void](WaitFor { Visible }); Start-Sleep -Milliseconds 300; Press @() 0x1B; $esc = WaitFor { -not (Visible) }; Check 'Echap masque' ($esc -ge 0)

# --- every monitor
foreach ($screen in [Windows.Forms.Screen]::AllScreens) {
    $wa = $screen.WorkingArea
    [void][T.W]::SetCursorPos($wa.X + 50, $wa.Y + 50); Start-Sleep -Milliseconds 200
    Hotkey; [void](WaitFor { Visible }); Start-Sleep -Milliseconds 400
    Type-Query 'nexus-widget-probe'; [void](WaitFor { @(Rows).Count -ge 2 } 3000); Start-Sleep -Milliseconds 300
    $r = New-Object T.W+RECT; [void][T.W]::GetWindowRect((WidgetHandle), [ref]$r)
    $w = $r.R - $r.L; $cx = ($r.L + $r.R) / 2; $mid = $wa.X + $wa.Width / 2
    Check "ecran $($screen.DeviceName)" ([math]::Abs($cx - $mid) -le 3 -and $r.T -ge $wa.Y -and $r.B -le ($wa.Y + $wa.Height)) "x=$($r.L) y=$($r.T) largeur=$w (centre attendu $mid), echelle $([math]::Round($w/700,2))"
    Grab (Join-Path $Work ("widget-" + $screen.DeviceName.Replace('\\.\', '') + ".png"))
    Press @() 0x1B; [void](WaitFor { -not (Visible) }); Start-Sleep -Milliseconds 200
}
[void][T.W]::SetCursorPos($cursor.X, $cursor.Y)

# --- memory and CPU while hidden
Start-Sleep -Seconds 6
$hidden = Ram; "RAM widget masque, apres usage : $hidden Mo (avant le premier affichage : $main0 Mo)"
$c0 = $p.TotalProcessorTime; Start-Sleep -Seconds 10; $p.Refresh(); $cpu = ($p.TotalProcessorTime - $c0).TotalSeconds / 10 / [Environment]::ProcessorCount * 100
"CPU widget masque : {0:F2} % de la machine sur 10 s" -f $cpu

# --- closing the main window keeps the shortcut alive
[void][T.W]::PostMessageW($main, 0x10, [IntPtr]::Zero, [IntPtr]::Zero); Start-Sleep -Seconds 2
Check 'fermer la fenetre principale ne quitte pas NEXUS' (-not $p.HasExited)
Hotkey; $bg = WaitFor { Visible }; Check 'raccourci disponible, fenetre fermee' ($bg -ge 0); Press @() 0x1B; [void](WaitFor { -not (Visible) })

# --- a second launch brings the hidden window back
$second = Start-Process $Exe -PassThru; [void]$second.WaitForExit(15000); Start-Sleep -Seconds 1
Check 'un second lancement rouvre la fenetre' ([T.W]::IsWindowVisible($main))

function Open-Widget($query) {
    Hotkey; [void](WaitFor { Visible }); Start-Sleep -Milliseconds 300
    Type-Query $query; [void](WaitFor { @(Rows).Count -ge 1 } 3000); Start-Sleep -Milliseconds 200
}
function MainItems($type) { $AE::FromHandle($main).FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type))) }
function Shot-Main($file) {
    $r = New-Object T.W+RECT; [void][T.W]::GetWindowRect($main, [ref]$r)
    $b = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [Drawing.Graphics]::FromImage($b); $dc = $g.GetHdc()
    [void][T.W]::PrintWindow($main, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose(); $b.Save($file, [Drawing.Imaging.ImageFormat]::Png)
}

# --- applications, ahead of the files
$calcBefore = @(Get-Process CalculatorApp -ErrorAction SilentlyContinue).Id
function FirstRowTexts { $t = (@(Rows))[0].FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text))); @($t | ForEach-Object { $_.Current.Name }) -join ' | ' }
foreach ($pair in @(@('calculatrice', 'Calculatrice'), @('discord', 'Discord'), @('vsc', 'Visual Studio Code'), @('calculatirce', 'Calculatrice'))) {
    Open-Widget $pair[0]; Start-Sleep -Milliseconds 500
    $first = FirstRowTexts
    Check "application [$($pair[0])] en tete" ($first -like "$($pair[1]) | Application*") $first
    Grab (Join-Path $Work ("app-" + $pair[0] + ".png"))
    Press @() 0x1B; [void](WaitFor { -not (Visible) }); Start-Sleep -Milliseconds 250
}
Open-Widget 'ext:exe calculatrice'; Start-Sleep -Milliseconds 400
$n = @(Rows).Count; $first = if ($n -gt 0) { FirstRowTexts } else { '' }
Check 'un filtre ne propose que des fichiers' ($first -notlike '*| Application*') "$n ligne(s)"
Press @() 0x1B; [void](WaitFor { -not (Visible) }); Start-Sleep -Milliseconds 250
Open-Widget 'calculatrice'; Start-Sleep -Milliseconds 400
Press @() 0x0D
$started = WaitFor { @(Get-Process CalculatorApp -ErrorAction SilentlyContinue | Where-Object { $calcBefore -notcontains $_.Id }).Count -gt 0 } 8000
Check 'Entree lance une application du Store' ($started -ge 0) ("{0:F0} ms" -f $started)
Check 'le widget se masque apres le lancement' (-not (Visible))
Get-Process CalculatorApp -ErrorAction SilentlyContinue | Where-Object { $calcBefore -notcontains $_.Id } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# --- right click menu
Open-Widget 'nexus-widget-probe'
$row = (@(Rows))[0].Current.BoundingRectangle
[void][T.W]::SetCursorPos([int]($row.X + 120), [int]($row.Y + 20)); Start-Sleep -Milliseconds 150
[T.W]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); [T.W]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 600
$popups = 0; $h = [IntPtr]::Zero
while (($h = [T.W]::FindWindowExW([IntPtr]::Zero, $h, [NullString]::Value, [NullString]::Value)) -ne [IntPtr]::Zero) {
    $id = 0; [void][T.W]::GetWindowThreadProcessId($h, [ref]$id)
    if ($id -eq $p.Id -and [T.W]::IsWindowVisible($h) -and $h -ne (WidgetHandle) -and $h -ne $main) {
        $m = New-Object T.W+RECT; [void][T.W]::GetWindowRect($h, [ref]$m); "      fenetre du menu : $($m.R-$m.L) x $($m.B-$m.T)"; if (($m.B - $m.T) -gt 100) { $popups++ }
    }
}
Check 'clic droit : menu des actions' ($popups -ge 1) "$popups menu"
Check 'le widget reste ouvert pendant le menu' (Visible)
Grab (Join-Path $Work 'widget-menu.png')
if ([T.W]::GetForegroundWindow() -eq (WidgetHandle)) { Send-Keys @() 0x1B; Start-Sleep -Milliseconds 250 }
[void][T.W]::SetCursorPos($cursor.X, $cursor.Y)
if (Visible) { Press @() 0x1B; [void](WaitFor { -not (Visible) }) }

# --- "see all results"
Start-Sleep -Milliseconds 300
Open-Widget 'nexus-widget-probe'
$all = @($AE::FromHandle((WidgetHandle)).FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)))) | Where-Object { $_.Current.Name -like 'Voir tous*' } | Select-Object -First 1
$all.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds 1
$text = (@(MainItems $CT::Edit))[0].GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
Check 'Voir tous ouvre la page Search avec la requete' ($text -eq 'nexus-widget-probe' -and -not (Visible)) "[$text]"

# --- settings
$set = @(MainItems $CT::ListItem) | Where-Object { $_.Current.Name -match 'SettingsViewModel' } | Select-Object -First 1
$set.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 2
Shot-Main (Join-Path $Work 'settings.png')
$auto = @(MainItems $CT::CheckBox) | Where-Object { $_.Current.Name -like '*marrer le widget avec Windows*' } | Select-Object -First 1
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$hadRun = (Get-ItemProperty $run -Name NEXUS -ErrorAction SilentlyContinue).NEXUS
if ($auto -and -not $hadRun) {
    $t = $auto.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern); $t.Toggle(); Start-Sleep -Milliseconds 400
    $v = (Get-ItemProperty $run -Name NEXUS -ErrorAction SilentlyContinue).NEXUS
    Check 'demarrage avec Windows : cle ecrite' ($v -like '*--background') "$v"
    $t.Toggle(); Start-Sleep -Milliseconds 400
    Check 'demarrage avec Windows : cle retiree' (-not (Get-ItemProperty $run -Name NEXUS -ErrorAction SilentlyContinue).NEXUS)
} elseif ($hadRun) { "      demarrage avec Windows deja active par l'utilisateur : case non touchee" } else { Check 'case du demarrage avec Windows trouvee' $false }

# --- every size of the bar, chosen in the settings page
$main1 = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$combo = (@(MainItems $CT::ComboBox))[0]
$sizeIndex = 0
foreach ($expected in 560, 700, 875, 1050, 700) {
    $pick = if ($sizeIndex -eq 4) { 1 } else { $sizeIndex }
    $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 300
    (@($combo.FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)))))[$pick].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); Start-Sleep -Milliseconds 300
    [void][T.W]::SetCursorPos($main1.X + 50, $main1.Y + 50); Start-Sleep -Milliseconds 150
    Open-Widget 'nexus-widget-probe'
    $r = New-Object T.W+RECT; [void][T.W]::GetWindowRect((WidgetHandle), [ref]$r)
    Check "taille $pick" (($r.R - $r.L) -eq $expected) "$($r.R-$r.L) x $($r.B-$r.T) px"
    Grab (Join-Path $Work "size-$sizeIndex.png")
    Press @() 0x1B; [void](WaitFor { -not (Visible) }); Start-Sleep -Milliseconds 200
    $sizeIndex++
}
[void][T.W]::SetCursorPos($cursor.X, $cursor.Y)
Shot-Main (Join-Path $Work 'settings2.png')

# --- Windows asking to close (installer, logoff)
function Host-Window { $h = [IntPtr]::Zero; while (($h = [T.W]::FindWindowExW([IntPtr]::Zero, $h, [NullString]::Value, 'NexusBackground')) -ne [IntPtr]::Zero) { $id = 0; [void][T.W]::GetWindowThreadProcessId($h, [ref]$id); if ($id -eq $p.Id) { return $h } }; [IntPtr]::Zero }
[void][T.W]::PostMessageW((Host-Window), 0x16, [IntPtr]::new(1), [IntPtr]::new(1))
$gone = $p.WaitForExit(15000)
Check 'une demande de fermeture de Windows arrete NEXUS' $gone
if (-not $gone) { $p.Kill(); [void]$p.WaitForExit(5000) }

# --- started with Windows: no window, shortcut available
$p = Start-Process $Exe -ArgumentList '--background' -PassThru
Start-Sleep -Seconds 12; $p.Refresh()
Check 'demarrage en arriere-plan sans fenetre' ($p.MainWindowHandle -eq [IntPtr]::Zero)
"RAM en arriere-plan, sans fenetre : $(Ram) Mo"
Hotkey; $b = WaitFor { Visible }; Check 'raccourci en arriere-plan' ($b -ge 0) ("{0:F0} ms" -f $b)
Start-Sleep -Milliseconds 300; Press @() 0x1B; [void](WaitFor { -not (Visible) })
Start-Sleep -Seconds 6; "RAM en arriere-plan, widget utilise puis masque : $(Ram) Mo"
[void][T.W]::PostMessageW((Host-Window), 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
Check 'quitter depuis l arriere-plan' ($p.WaitForExit(15000))
if (-not $p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) }

# --- the shortcut is already taken
$taken = [T.W]::RegisterHotKey([IntPtr]::Zero, 77, $HotkeyMask, $HotkeyVk)
Check 'raccourci occupe pour le test' $taken
$p = Start-Process $Exe -PassThru
do { Start-Sleep -Milliseconds 100; $p.Refresh() } until ($p.MainWindowHandle -ne 0)
$main = $p.MainWindowHandle; Start-Sleep -Seconds 2
$set = @(MainItems $CT::ListItem) | Where-Object { $_.Current.Name -match 'SettingsViewModel' } | Select-Object -First 1
$set.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 2
$msg = @(MainItems $CT::Text) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like '*par Windows ou une autre application*' } | Select-Object -First 1
Check 'conflit de raccourci signale, sans plantage' ([bool]$msg -and -not $p.HasExited) "$msg"
[void][T.W]::UnregisterHotKey([IntPtr]::Zero, 77)
[void][T.W]::PostMessageW((Host-Window), 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
if (-not $p.WaitForExit(15000)) { $p.Kill() }
Remove-Item Env:NEXUS_DB
"Echecs : $fails"
