# Opens each page of the published application and saves a montage of real screenshots (visual check of the theme).
# Usage: capture-pages.ps1 -Exe <Nexus.App.exe> -Index <index copy> -Out <montage.png>
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Index, [Parameter(Mandatory)][string]$Out, [string]$Query = 'ext:png', [string[]]$Pages = @('DashboardViewModel', 'SearchPageViewModel', 'StorageViewModel', 'AppsViewModel', 'DuplicatesViewModel', 'IndexationViewModel'))

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Namespace Cap -Name N -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
'@
$env:NEXUS_DB = $Index
$AE = [Windows.Automation.AutomationElement]; $CT = [Windows.Automation.ControlType]
$p = Start-Process $Exe -PassThru
do { Start-Sleep -Milliseconds 100; $p.Refresh() } until ($p.MainWindowHandle -ne 0)
Start-Sleep -Seconds 3
$h = $p.MainWindowHandle
function Root { $AE::FromHandle($h) }
function All($type) { (Root).FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type))) }
function Go($rx) { $i = (All $CT::ListItem) | Where-Object { $_.Current.Name -match $rx } | Select-Object -First 1; $i.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Shot {
    $r = New-Object Cap.N+RECT; [void][Cap.N]::GetWindowRect($h, [ref]$r)
    $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
    [void][Cap.N]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    return $bmp
}
$shots = @()
foreach ($page in $Pages) {
    Go $page
    if ($page -eq 'SearchPageViewModel') {
        $e = (All $CT::Edit) | Select-Object -First 1
        $e.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Query)
    }
    Start-Sleep -Seconds $(if ($page -in 'StorageViewModel', 'AppsViewModel') { 9 } else { 3 })
    $shots += , (Shot)
}
[void]$p.CloseMainWindow(); if (-not $p.WaitForExit(20000)) { $p.Kill() }

$w = 760; $hh = [int]($w * $shots[0].Height / $shots[0].Width)
$m = New-Object Drawing.Bitmap (2 * $w), ([math]::Ceiling($shots.Count / 2) * $hh)
$g = [Drawing.Graphics]::FromImage($m); $g.InterpolationMode = 'HighQualityBicubic'
for ($i = 0; $i -lt $shots.Count; $i++) { $g.DrawImage($shots[$i], ($i % 2) * $w, [math]::Floor($i / 2) * $hh, $w, $hh) }
$g.Dispose(); $m.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
Write-Output "Montage: $Out"
