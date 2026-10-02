#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('App', 'Core', 'Backend', 'Full')]
    [string]$Mode = 'App',
    [string]$ServerPath,
    [switch]$SkipBackend,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoRestore,
    [switch]$List
)

$ErrorActionPreference = 'Stop'
# Check every native exit code ourselves, consistently across PowerShell 7 versions.
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Split-Path $PSScriptRoot -Parent
$steps = [System.Collections.Generic.List[object]]::new()

function Add-Step([string]$Label, [string]$Command, [string[]]$Arguments) {
    $steps.Add([pscustomobject]@{ Label = $Label; Command = $Command; Arguments = $Arguments })
}

function Invoke-Checked($Step) {
    Write-Host "`nCHECK: $($Step.Label)"
    & $Step.Command @($Step.Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "$($Step.Label) failed (exit $LASTEXITCODE). Remaining checks were not run."
    }
}

try {
    if ($SkipBackend -and ($Mode -ne 'Full' -or $ServerPath)) {
        throw '-SkipBackend is only valid with -Mode Full and without -ServerPath.'
    }
    if ($ServerPath -and $Mode -notin @('Backend', 'Full')) {
        throw '-ServerPath is only valid with -Mode Backend or Full.'
    }
    $checkBackend = $Mode -eq 'Backend' -or ($Mode -eq 'Full' -and -not $SkipBackend)
    if ($checkBackend) {
        if (-not $ServerPath -or -not (Test-Path -LiteralPath $ServerPath -PathType Leaf)) {
            throw 'Backend requires -ServerPath pointing to the private Code.gs. For public-only Full checks explicitly use -SkipBackend.'
        }
        # Resolve relative paths from the caller's directory before changing location.
        $ServerPath = (Resolve-Path -LiteralPath $ServerPath).Path
    }
    if ($Mode -ne 'Backend' -and -not $IsWindows) {
        throw 'App/Core/Full checks require Windows (DPAPI, WLAN and Windows registry contracts).'
    }

    $appProject = Join-Path $repoRoot 'src/IS74Wifi.App/IS74Wifi.App.csproj'
    $testProject = Join-Path $repoRoot 'tests/IS74Wifi.ContractTests/IS74Wifi.ContractTests.csproj'
    $outputRoot = Join-Path $repoRoot "artifacts/check/$Configuration"
    if ($Mode -in @('App', 'Full')) {
        $buildArgs = @('build', $appProject, '-c', $Configuration, '-o', (Join-Path $outputRoot 'app'))
        if ($NoRestore) { $buildArgs += '--no-restore' }
        Add-Step 'Build App' 'dotnet' $buildArgs
    }
    if ($Mode -in @('App', 'Core', 'Full')) {
        $buildArgs = @('build', $testProject, '-c', $Configuration, '-o', (Join-Path $outputRoot 'tests'))
        if ($NoRestore) { $buildArgs += '--no-restore' }
        Add-Step 'Build contract tests' 'dotnet' $buildArgs
        $contractAssembly = Join-Path $outputRoot 'tests/IS74Wifi.ContractTests.dll'
        if ($Mode -eq 'App') {
            Add-Step 'C# contract: manual authorization progress' 'dotnet' @($contractAssembly, '--filter=manual-authorization-progress')
            Add-Step 'C# contract: terminal UI' 'dotnet' @($contractAssembly, '--filter=terminal-ui')
            Add-Step 'C# contract: status service' 'dotnet' @($contractAssembly, '--filter=status-service')
        }
        else {
            Add-Step 'C# contracts' 'dotnet' @($contractAssembly)
        }
    }
    if ($Mode -in @('App', 'Full')) {
        Add-Step 'Static source contracts' 'python' @((Join-Path $repoRoot 'tests/static_checks.py'))
        Add-Step 'UI live-status contracts' 'python' @((Join-Path $repoRoot 'tests/ui_live_status_contract.py'))
    }
    if ($Mode -eq 'Full') {
        Add-Step 'Check runner contracts' 'pwsh' @('-NoProfile', '-NonInteractive', '-File', (Join-Path $repoRoot 'tests/check_runner_contract.ps1'), '-ContractAssembly', (Join-Path $outputRoot 'tests/IS74Wifi.ContractTests.dll'))
        foreach ($shell in @('pwsh', 'powershell.exe')) {
            Add-Step "PowerShell reference contracts ($shell)" $shell @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoRoot 'tests/powershell_reference_contract.ps1'))
        }
        # Public CI can check the Node suites' syntax without the private source.
        foreach ($suite in @('leaderboard_server_contract.cjs', 'server_ingestion_contract.cjs', 'update_manifest_server_contract.cjs')) {
            Add-Step "Syntax: $suite" 'node' @('--check', (Join-Path $repoRoot "tests/$suite"))
        }
    }
    if ($checkBackend) {
        foreach ($suite in @('leaderboard_server_contract.cjs', 'server_ingestion_contract.cjs', 'update_manifest_server_contract.cjs')) {
            Add-Step "Backend: $suite" 'node' @((Join-Path $repoRoot "tests/$suite"), $ServerPath)
        }
    }

    if ($SkipBackend) { Write-Warning 'SKIPPED: private backend execution (-SkipBackend). This is not a backend pass.' }
    if ($List) {
        foreach ($step in $steps) {
            Write-Host "$($step.Label): $($step.Command) $($step.Arguments -join ' ')"
        }
        Write-Host 'PLAN ONLY: no checks executed.'
        exit 0
    }

    # Fail on missing prerequisites before spending time on a partial build.
    foreach ($command in ($steps.Command | Select-Object -Unique)) {
        if (-not (Get-Command $command -CommandType Application -ErrorAction SilentlyContinue)) {
            throw "Missing prerequisite: $command. See docs/checks.md."
        }
    }
    Push-Location $repoRoot
    try {
        foreach ($step in $steps) { Invoke-Checked $step }
    }
    finally { Pop-Location }
    Write-Host "`nPASS: $Mode checks ($Configuration)."
    if ($SkipBackend) { Write-Warning 'Backend NOT verified. Run -Mode Backend -ServerPath <private Code.gs> separately.' }
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
