#requires -Version 5.1

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'src\IS74Wifi.psm1'
Import-Module $modulePath -Force
Initialize-IS74Storage

$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\IS74Wifi.Agent', [ref]$createdNew)
if (-not $createdNew) {
    $mutex.Dispose()
    exit 0
}

try {
    while ($true) {
        try {
            Invoke-IS74AgentTick
        } catch {
            # Tick errors are isolated; Connect-IS74Wifi writes its own diagnostics.
        }

        $delayMs = [int](Get-IS74AgentSleepMilliseconds)
        if ($delayMs -lt 100) { $delayMs = 100 }
        Start-Sleep -Milliseconds $delayMs
    }
} finally {
    try { $mutex.ReleaseMutex() } catch { }
    $mutex.Dispose()
}
