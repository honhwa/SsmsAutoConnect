# Builds SsmsAutoConnect and deploys it into SSMS 18. Must run in an ELEVATED PowerShell (writes to Program Files).
#   powershell -ExecutionPolicy Bypass -File .\deploy.ps1 [-SkipBuild]
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'

$ssmsIde = 'C:\Program Files (x86)\Microsoft SQL Server Management Studio 18\Common7\IDE'
$ssmsExe = Join-Path $ssmsIde 'Ssms.exe'
$target  = Join-Path $ssmsIde 'Extensions\SsmsAutoConnect'
$out     = Join-Path $PSScriptRoot 'src\SsmsAutoConnect\bin\Release'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this script from an elevated PowerShell (Run as administrator).' }
if (-not (Test-Path $ssmsExe)) { throw "SSMS not found at $ssmsExe" }
if (Get-Process Ssms -ErrorAction SilentlyContinue) { throw 'Close all SSMS windows first.' }

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }

$files = 'extension.vsixmanifest', 'SsmsAutoConnect.dll', 'SsmsAutoConnect.pkgdef'
foreach ($f in $files) { if (-not (Test-Path (Join-Path $out $f))) { throw "Missing build output: $f" } }

New-Item -ItemType Directory -Force $target | Out-Null
foreach ($f in $files) { Copy-Item (Join-Path $out $f) $target -Force }
Write-Host "Copied $($files -join ', ') to $target"

Write-Host 'Refreshing SSMS extension cache (Ssms.exe /setup)...'
Start-Process -FilePath $ssmsExe -ArgumentList '/setup' -Wait
Write-Host 'Deployed. Start SSMS normally (or "Ssms.exe /log" to write ActivityLog.xml).'
