# Builds the tray and the installer with the C# compiler that ships with Windows (.NET Framework 4.8). No SDK needed.
#   build\komorebi-tray.exe          the tray on its own
#   dist\komorebi-tray-setup.exe     the installer (the tray, icon and default config are embedded in it)
$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc   = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$build = Join-Path $root 'build'
$dist  = Join-Path $root 'dist'
$icon  = Join-Path $root 'assets\komorebi-tray.ico'
$manifest = Join-Path $root 'src\app.manifest'
New-Item -ItemType Directory -Force $build, $dist | Out-Null

$tray = Join-Path $build 'komorebi-tray.exe'
# WinRT (Xbox controllers through Windows.Gaming.Input, AirPods advertisements) straight from the Windows metadata;
# UI Automation for the Widgets button next to the taskbar readout
$fw    = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319"
$winmd = "$env:SystemRoot\System32\WinMetadata"
$refs  = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll',
           "$fw\WPF\UIAutomationClient.dll", "$fw\WPF\UIAutomationTypes.dll", "$fw\WPF\WindowsBase.dll",
           "$fw\System.Runtime.dll", "$fw\System.Runtime.WindowsRuntime.dll", "$fw\System.Runtime.InteropServices.WindowsRuntime.dll",
           "$winmd\Windows.Foundation.winmd", "$winmd\Windows.Gaming.winmd", "$winmd\Windows.Devices.winmd",
           "$winmd\Windows.System.winmd", "$winmd\Windows.Storage.winmd") | ForEach-Object { "/r:$_" }
$sources = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs) + @(Get-ChildItem (Join-Path $root 'src\battery') -Filter *.cs) |
    ForEach-Object { $_.FullName }
& $csc /nologo /optimize+ /target:winexe /platform:anycpu "/out:$tray" "/win32icon:$icon" "/win32manifest:$manifest" $refs $sources
if ($LASTEXITCODE -ne 0) { throw 'tray build failed' }

# Workstation GC without the background thread: less memory for an app that is idle 99% of the time
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup>
  <runtime><gcConcurrent enabled="false"/></runtime>
</configuration>
'@ | Set-Content "$tray.config" -Encoding UTF8

# Bundled HeadsetControl: the pinned official release, verified against the SHA-256 GitHub publishes for it
$hscVersion = '4.3.0'
$hscSha256  = 'E371DA043710DDB8FC70AF77F2CCAD1CD03F8C6F49BA423E08764AD7A94A3C40'
$hscExe = Join-Path $root 'third_party\headsetcontrol\headsetcontrol.exe'
New-Item -ItemType Directory -Force (Split-Path $hscExe) | Out-Null
if (-not (Test-Path $hscExe) -or (Get-FileHash $hscExe -Algorithm SHA256).Hash -ne $hscSha256) {
    Write-Host "Downloading HeadsetControl $hscVersion..."
    Invoke-WebRequest "https://github.com/Sapd/HeadsetControl/releases/download/$hscVersion/headsetcontrol-windows-x86_64.exe" -OutFile $hscExe -UseBasicParsing
}
if ((Get-FileHash $hscExe -Algorithm SHA256).Hash -ne $hscSha256) { Remove-Item $hscExe; throw 'HeadsetControl checksum mismatch - refusing to bundle it.' }

$setup = Join-Path $dist 'komorebi-tray-setup.exe'
& $csc /nologo /optimize+ /target:winexe /platform:anycpu "/out:$setup" "/win32icon:$icon" "/win32manifest:$manifest" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "/resource:$tray,komorebi-tray.exe" "/resource:$tray.config,komorebi-tray.exe.config" "/resource:$icon,komorebi-tray.ico" `
    "/resource:$hscExe,headsetcontrol.exe" "/resource:$(Join-Path $root 'THIRD_PARTY_NOTICES.txt'),THIRD_PARTY_NOTICES.txt" `
    "/resource:$(Join-Path $root 'config\komorebi.json'),komorebi.json" "/resource:$(Join-Path $root 'config\whkdrc'),whkdrc" `
    (Join-Path $root 'installer\Setup.cs')
if ($LASTEXITCODE -ne 0) { throw 'setup build failed' }

Write-Host "Built $tray" -ForegroundColor Cyan
Write-Host "Built $setup ($([math]::Round((Get-Item $setup).Length / 1KB)) KB)" -ForegroundColor Cyan
