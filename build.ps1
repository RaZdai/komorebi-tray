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
& $csc /nologo /optimize+ /target:winexe /platform:anycpu "/out:$tray" "/win32icon:$icon" "/win32manifest:$manifest" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    (Join-Path $root 'src\tray.cs')
if ($LASTEXITCODE -ne 0) { throw 'tray build failed' }

# Workstation GC without the background thread: less memory for an app that is idle 99% of the time
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup>
  <runtime><gcConcurrent enabled="false"/></runtime>
</configuration>
'@ | Set-Content "$tray.config" -Encoding UTF8

$setup = Join-Path $dist 'komorebi-tray-setup.exe'
& $csc /nologo /optimize+ /target:winexe /platform:anycpu "/out:$setup" "/win32icon:$icon" "/win32manifest:$manifest" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "/resource:$tray,komorebi-tray.exe" "/resource:$tray.config,komorebi-tray.exe.config" "/resource:$icon,komorebi-tray.ico" `
    "/resource:$(Join-Path $root 'config\komorebi.json'),komorebi.json" "/resource:$(Join-Path $root 'config\whkdrc'),whkdrc" `
    (Join-Path $root 'installer\Setup.cs')
if ($LASTEXITCODE -ne 0) { throw 'setup build failed' }

Write-Host "Built $tray" -ForegroundColor Cyan
Write-Host "Built $setup ($([math]::Round((Get-Item $setup).Length / 1KB)) KB)" -ForegroundColor Cyan
