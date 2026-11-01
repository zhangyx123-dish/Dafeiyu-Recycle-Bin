param([switch]$RecycleProbe)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { Write-Output '{"status":"WINDOWS_REQUIRED","compile":"NOT_RUN","recycle_test":"NOT_RUN","files_processed":0}'; exit 2 }
try {
    . (Join-Path $PSScriptRoot 'Runtime.ps1')
    Import-RecycleRuntime
    if (-not $RecycleProbe) { Write-Output '{"compile":"PASS","recycle_test":"NOT_RUN","files_processed":0}'; exit 0 }
    $result = [RecyclePet.Native]::Probe([IntPtr]::Zero)
    Write-Output $result.Json()
    if (-not $result.Passed) { exit 1 }
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
