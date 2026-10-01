#requires -Version 5.1
# Offline reference-runtime checks shared by the local wrapper and candidate CI.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
foreach ($relativePath in @(
    'IS74Wifi.ps1',
    'agent.ps1',
    'src/IS74Wifi.psm1',
    'tests/critical_path_contract.ps1',
    'tests/powershell_reference_contract.ps1'
)) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repoRoot $relativePath), [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        throw "PowerShell parse failed for ${relativePath}: $($errors -join '; ')"
    }
}
Import-Module (Join-Path $repoRoot 'src/IS74Wifi.psm1') -Force
& (Join-Path $PSScriptRoot 'critical_path_contract.ps1')
Write-Host 'PASS PowerShell reference (parse, import, critical-path contracts)'
