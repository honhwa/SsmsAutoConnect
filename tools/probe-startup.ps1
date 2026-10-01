# Starts SSMS with /log and samples whether its UI thread responds (Process.Responding, ~5 s hang threshold
# per sample) until autoconnect.log says Done. Prints unresponsive intervals and the extension log.
param([int]$TimeoutSec = 180)

$ssms = 'C:\Program Files (x86)\Microsoft SQL Server Management Studio 18\Common7\IDE\Ssms.exe'
$log  = Join-Path $env:APPDATA 'SsmsAutoConnect\autoconnect.log'
if (Get-Process Ssms -ErrorAction SilentlyContinue) { throw 'Close SSMS first.' }

$start = Get-Date
$p = Start-Process $ssms -ArgumentList '/log' -PassThru
$hung = @()
while (((Get-Date) - $start).TotalSeconds -lt $TimeoutSec) {
    Start-Sleep -Milliseconds 250
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0 -and -not $p.Responding) { $hung += (Get-Date) }
    if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $start -and (Select-String -Path $log -Pattern 'INFO  Done|giving up|Auto-connect failed' -Quiet)) {
        Start-Sleep 3; break
    }
}
"Not responding samples: $($hung.Count)"
if ($hung) { "  first: $($hung[0].ToString('HH:mm:ss.fff'))  last: $($hung[-1].ToString('HH:mm:ss.fff'))" }
Get-Content $log
