# Removes SsmsAutoConnect from SSMS 18. Must run in an ELEVATED PowerShell.
# Leaves %AppData%\SsmsAutoConnect (your config) untouched.
$ErrorActionPreference = 'Stop'

$ssmsIde = 'C:\Program Files (x86)\Microsoft SQL Server Management Studio 18\Common7\IDE'
$ssmsExe = Join-Path $ssmsIde 'Ssms.exe'
$target  = Join-Path $ssmsIde 'Extensions\SsmsAutoConnect'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this script from an elevated PowerShell (Run as administrator).' }
if (Get-Process Ssms -ErrorAction SilentlyContinue) { throw 'Close all SSMS windows first.' }

if (Test-Path $target) {
    Remove-Item $target -Recurse -Force
    Write-Host "Removed $target"
} else {
    Write-Host "Nothing to remove at $target"
}

Write-Host 'Refreshing SSMS extension cache (Ssms.exe /setup)...'
Start-Process -FilePath $ssmsExe -ArgumentList '/setup' -Wait
Write-Host 'Undeployed.'
