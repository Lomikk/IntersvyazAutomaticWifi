#requires -Version 5.1

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'src\IS74Wifi.psm1'
Import-Module $modulePath -Force
Initialize-IS74Storage

$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\IS74Wifi.Agent', [ref]$createdNew)
if (-not $createdNew) {
    Write-IS74RuntimeEvent -Message 'agent.start skipped reason=already-running'
    $mutex.Dispose()
    exit 0
}

Write-IS74RuntimeEvent -Message ("agent.start psVersion={0}" -f $PSVersionTable.PSVersion)

try {
    while ($true) {
        try {
            Invoke-IS74AgentTick
        } catch {
            # Tick errors are isolated so the background process stays alive.
            Write-IS74RuntimeEvent -Level ERROR -Message ("agent.tick error={0}" -f $_.Exception.Message)
        }

        $delayMs = [int](Get-IS74AgentSleepMilliseconds)
        if ($delayMs -lt 100) { $delayMs = 100 }
        Start-Sleep -Milliseconds $delayMs
    }
} finally {
    Write-IS74RuntimeEvent -Message 'agent.stop'
    try { $mutex.ReleaseMutex() } catch { }
    $mutex.Dispose()
}
