param([int]$ObserveSeconds = 120)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$msi = Join-Path $root 'artifacts/FloatHub-1.0.2-x64.msi'
$published = Join-Path $root 'artifacts/win-x64/FloatHub.exe'
$testDir = Join-Path $root ('.test-work/package-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $testDir 'app'
$exe = Join-Path $install 'FloatHub.exe'
$startLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'FloatHub.lnk'
$loginLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'FloatHub.lnk'
if ((Get-Process FloatHub -ErrorAction SilentlyContinue) -or (Test-Path $startLink) -or (Test-Path $loginLink)) {
    throw 'Use a clean test account with no FloatHub installation or running instance.'
}
[void][IO.Directory]::CreateDirectory($testDir)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class FloatHubTrayTest {
    private delegate bool Callback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder name,int length);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
    public static void ShowMenu(int process) {
        EnumWindows(delegate(IntPtr window, IntPtr state) {
            int owner; GetWindowThreadProcessId(window,out owner);
            if(owner!=process) return true;
            var name=new StringBuilder(256);GetClassName(window,name,256);
            if(name.ToString().StartsWith("WindowsForms10.Window.0")) PostMessage(window,0x800,new IntPtr(1),new IntPtr(0x205));
            return true;
        },IntPtr.Zero);
    }
}
'@
function Find-Named($element, $name, $scope) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $name
    return $element.FindFirst($scope, $condition)
}
function Find-Window($name) {
    $window = Find-Named ([System.Windows.Automation.AutomationElement]::RootElement) $name 'Children'
    if ($window -and $window.Current.ProcessId -eq $app.Id) { return $window }
}
function Invoke-Control($element) {
    if (!$element) { throw 'Expected UI Automation element was missing' }
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Invoke-TrayItem($label) {
    [FloatHubTrayTest]::ShowMenu($app.Id)
    Start-Sleep -Milliseconds 400
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $mine = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $app.Id
    foreach ($window in $desktop.FindAll('Children',$mine)) {
        $item = Find-Named $window $label 'Descendants'
        if ($item) { Invoke-Control $item; return }
    }
    throw "Tray item missing: $label"
}
function Run-Msi($arguments) {
    $p = Start-Process msiexec.exe -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Windows Installer exit $($p.ExitCode)" }
}
$installed = $false
try {
    Run-Msi "/i `"$msi`" /qn /norestart INSTALLDIR=`"$install`" /l*v `"$testDir/install.log`""
    $installed = $true
    if (!(Test-Path $startLink) -or (Get-FileHash $exe).Hash -ne (Get-FileHash $published).Hash) { throw 'Installed payload or shortcut mismatch' }
    'PASS MSI installation, Start menu shortcut and EXE hash'
    $app = Start-Process $exe -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    $app.Refresh()
    "Widget working set before opening settings: $([Math]::Round($app.WorkingSet64/1MB,1)) MiB"
    $other = Start-Process $exe -WindowStyle Hidden -PassThru
    if (!$other.WaitForExit(10000) -or $app.HasExited) { throw 'Single instance failed' }
    $settings = $null
    for ($n=0; $n -lt 25 -and !$settings; $n++) {
        Start-Sleep -Milliseconds 250
        $settings = Find-Window 'FloatHub 设置'
    }
    if (!$settings) { throw 'Packaged application settings window did not appear' }
    'PASS standalone launch and single-instance activation'
    foreach ($page in '模块管理','股票管理','行情显示','数据设置','关于','常规设置') {
        Invoke-Control (Find-Named $settings $page 'Descendants')
        Start-Sleep -Milliseconds 150
    }
    'PASS navigation to all six settings pages'
    $login = Find-Named $settings '登录 Windows 后启动' 'Descendants'
    $toggle = $login.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle()
    Start-Sleep -Milliseconds 400
    if (!(Test-Path $loginLink)) { throw 'Startup shortcut missing' }
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($loginLink).TargetPath -ne $exe) { throw 'Startup target mismatch' }
    $toggle.Toggle()
    Start-Sleep -Milliseconds 200
    if (Test-Path $loginLink) { throw 'Startup shortcut was not removed' }
    'PASS startup toggle and target path'
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Invoke-TrayItem '显示 / 隐藏悬浮组件'
    Start-Sleep -Milliseconds 300
    if (Find-Window 'FloatHub 悬浮组件') { throw 'Tray hide failed' }
    Invoke-TrayItem '显示 / 隐藏悬浮组件'
    Start-Sleep -Milliseconds 300
    if (!(Find-Window 'FloatHub 悬浮组件')) { throw 'Tray restore failed' }
    'PASS tray hide and restore'
    $cpu = $app.TotalProcessorTime.TotalSeconds
    $samples = @()
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while ($elapsed.Elapsed.TotalSeconds -lt $ObserveSeconds) {
        Start-Sleep -Seconds ([Math]::Min(15,$ObserveSeconds))
        $app.Refresh()
        if ($app.HasExited -or !$app.Responding) { throw 'Installed app stopped responding' }
        $samples += [Math]::Round($app.WorkingSet64 / 1MB,1)
    }
    "PASS responsive for $([int]$elapsed.Elapsed.TotalSeconds)s; working set samples MiB = $($samples -join ', '); CPU seconds = $([Math]::Round($app.TotalProcessorTime.TotalSeconds-$cpu,2))"
    Invoke-TrayItem '退出 FloatHub'
    if (!$app.WaitForExit(8000)) { throw 'Tray exit did not stop application' }
    'PASS tray menu and graceful exit'
} finally {
    if ($app -and !$app.HasExited -and $app.Path -eq $exe) { $app.Kill(); $app.WaitForExit() }
    if ($installed) { Run-Msi "/x `"$msi`" /qn /norestart /l*v `"$testDir/uninstall.log`"" }
}
if ((Test-Path $exe) -or (Test-Path $startLink) -or (Test-Path $loginLink)) { throw 'Uninstall left an application file or shortcut' }
'PASS MSI uninstall'
