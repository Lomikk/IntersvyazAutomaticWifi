#requires -Version 7.0
param([string]$ContractAssembly)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Split-Path $PSScriptRoot -Parent
$runner = Join-Path $repoRoot 'scripts/check.ps1'
$fixture = Join-Path $PSScriptRoot 'fixtures/backend-events.json'

function Assert-Contract([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-Runner([string[]]$RunnerArguments) {
    $output = & pwsh -NoProfile -NonInteractive -File $runner @RunnerArguments 2>&1
    return @{ Code = $LASTEXITCODE; Text = $output -join "`n" }
}

$app = Invoke-Runner @('-Mode', 'App', '-List')
Assert-Contract ($app.Code -eq 0 -and $app.Text.Contains('--filter=manual-authorization-progress') -and $app.Text.Contains('--filter=terminal-ui') -and $app.Text.Contains('UI live-status contracts')) 'App must build and check the UI/helper.'
Assert-Contract (-not $app.Text.Contains('Backend:')) 'App must not silently include backend work.'
$core = Invoke-Runner @('-Mode', 'Core', '-List', '-NoRestore')
Assert-Contract ($core.Code -eq 0 -and $core.Text.Contains('--no-restore') -and -not $core.Text.Contains('--filter=') -and -not $core.Text.Contains('Build App:')) 'Core must run the entire C# suite without a separate App build or UI-only filter.'
$full = Invoke-Runner @('-Mode', 'Full', '-SkipBackend', '-List', '-Configuration', 'Release')
Assert-Contract ($full.Code -eq 0 -and $full.Text.Contains('SKIPPED: private backend') -and $full.Text.Contains('powershell.exe') -and $full.Text.Contains('Syntax: server_ingestion_contract.cjs')) 'Public Full must explicitly report the backend skip and retain reference/syntax checks.'
$backend = Invoke-Runner @('-Mode', 'Backend', '-ServerPath', $fixture, '-List')
Assert-Contract ($backend.Code -eq 0 -and $backend.Text.Contains('Backend: leaderboard_server_contract.cjs') -and $backend.Text.Contains('Backend: server_ingestion_contract.cjs') -and -not $backend.Text.Contains('dotnet')) 'Backend must run both suites with no .NET dependency.'
foreach ($arguments in @(
    @('-Mode', 'Full', '-List'),
    @('-Mode', 'Backend', '-List'),
    @('-Mode', 'Backend', '-ServerPath', "$fixture.missing", '-List'),
    @('-Mode', 'App', '-SkipBackend', '-List'),
    @('-Mode', 'Core', '-ServerPath', $fixture, '-List'),
    @('-Mode', 'Full', '-SkipBackend', '-ServerPath', $fixture, '-List')
)) {
    $invalid = Invoke-Runner $arguments
    Assert-Contract ($invalid.Code -ne 0 -and -not $invalid.Text.Contains('CHECK:')) 'Invalid scope/missing private source must fail before running checks.'
}

# An existing JSON fixture is deliberately NOT valid Apps Script. This exercises
# the real child-process failure path without changing files or contacting Google.
$failed = Invoke-Runner @('-Mode', 'Backend', '-ServerPath', $fixture)
Assert-Contract ($failed.Code -ne 0 -and $failed.Text.Contains('CHECK: Backend: leaderboard_server_contract.cjs')) 'Native command failure must escape the wrapper with a nonzero exit code.'
Assert-Contract (-not $failed.Text.Contains('CHECK: Backend: server_ingestion_contract.cjs') -and -not $failed.Text.Contains('PASS: Backend')) 'The next successful check must not mask an earlier failure.'

Push-Location ([System.IO.Path]::GetTempPath())
try {
    $outside = Invoke-Runner @('-Mode', 'App', '-List')
    Assert-Contract ($outside.Code -eq 0 -and $outside.Text.Contains($repoRoot)) 'Runner must work from outside the repository.'
}
finally { Pop-Location }
if ($ContractAssembly) {
    $filterOutput = & dotnet $ContractAssembly --filter=deliberately-nonexistent-contract 2>&1
    Assert-Contract ($LASTEXITCODE -ne 0 -and ($filterOutput -join "`n").Contains('No contract tests match filter')) 'An empty C# test selection must fail, not report a silent pass.'
}
Write-Host 'PASS check-runner (scope, explicit skip, missing source, fail-fast, caller directory)'
