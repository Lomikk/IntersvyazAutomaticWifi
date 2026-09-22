#Requires -Version 5.1
<#
.SYNOPSIS
  Non-destructive Windows checks for the IS74Wifi C# repository.
.DESCRIPTION
  Mirrors the Windows CI build/contract/static checks and optionally NativeAOT.
  Never invokes a live Wi-Fi authorization and never reads or exports production secrets.
  Do not run 'status' against an overridden LOCALAPPDATA: that command can reconcile
  Windows autostart/installed-app registrations, so smoke tests use 'version' only.
.EXAMPLE
  .\IS74Wifi-Windows-Tests.ps1 -RepositoryPath D:\Repos\IntersvyazAutomaticWifi
.EXAMPLE
  .\IS74Wifi-Windows-Tests.ps1 -RepositoryPath D:\Repos\IntersvyazAutomaticWifi -NativeAot
#>
param(
    [string]$RepositoryPath = (Get-Location).Path,
    [switch]$NativeAot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepositoryPath).Path
if (-not (Test-Path -LiteralPath (Join-Path $repo 'IS74Wifi.slnx'))) {
    throw 'RepositoryPath must point to the IS74Wifi repository root (IS74Wifi.slnx not found).'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportDir = Join-Path ([Environment]::GetFolderPath('Desktop')) "IS74Wifi-Windows-Tests-$stamp"
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
$log = Join-Path $reportDir 'test-log.txt'
$summary = Join-Path $reportDir 'summary.txt'
$script:results = New-Object System.Collections.ArrayList

function Invoke-Check {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "`n========== $Name ==========" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    try {
        & $Action
        if ($LASTEXITCODE -ne 0) {
            throw "Last command exited with code $LASTEXITCODE"
        }
        [void]$script:results.Add("PASS  $Name")
        Write-Host "PASS $Name" -ForegroundColor Green
    }
    catch {
        [void]$script:results.Add("FAIL  $Name : $($_.Exception.Message)")
        Write-Warning "FAIL $Name : $($_.Exception.Message)"
    }
}

function Invoke-CleanAgentSmoke {
    param([string]$Executable)
    # The agent exits immediately when its isolated profile has no registration.
    # Do NOT run the 'status' command here: it reconciles real HKCU autostart entries.
    $previousAppData = $env:LOCALAPPDATA
    $isolatedAppData = Join-Path $reportDir 'isolated-appdata'
    New-Item -ItemType Directory -Path $isolatedAppData -Force | Out-Null
    try {
        $env:LOCALAPPDATA = $isolatedAppData
        $p = Start-Process -FilePath $Executable -ArgumentList agent -PassThru -Wait
        if ($p.ExitCode -ne 0) { throw "Clean-state agent smoke failed: $($p.ExitCode)" }
    }
    finally {
        $env:LOCALAPPDATA = $previousAppData
    }
}

Push-Location $repo
Start-Transcript -Path $log -Force | Out-Null
try {
    Write-Host "Repository: $repo"
    Write-Host "Date: $(Get-Date -Format o)"
    Write-Host "PowerShell: $($PSVersionTable.PSVersion)"
    Invoke-Check 'Git revision' {
        git status --short --branch
        git rev-parse HEAD
    }
    Invoke-Check '.NET SDK version' { dotnet --version }
    Invoke-Check 'Restore' { dotnet restore .\IS74Wifi.slnx }

    $restoreOk = [bool](@($script:results | Where-Object { $_ -eq 'PASS  Restore' }).Count)
    if ($restoreOk) {
        Invoke-Check 'Release build' {
            dotnet build .\IS74Wifi.slnx --configuration Release --no-restore
        }
    }
    else {
        [void]$script:results.Add('SKIP  Release build (restore failed)')
    }

    $buildOk = [bool](@($script:results | Where-Object { $_ -eq 'PASS  Release build' }).Count)
    if ($buildOk) {
        Invoke-Check 'C# contract tests (all 18 groups)' {
            dotnet run --project .\tests\IS74Wifi.ContractTests\IS74Wifi.ContractTests.csproj --configuration Release --no-build
        }
        Invoke-Check 'Compiled application version smoke' {
            $exe = Join-Path $repo 'src\IS74Wifi.App\bin\Release\net10.0-windows10.0.19041.0\IS74Wifi.exe'
            if (-not (Test-Path -LiteralPath $exe)) { throw "Executable not found: $exe" }
            $p = Start-Process -FilePath $exe -ArgumentList version -PassThru -Wait
            if ($p.ExitCode -ne 0) { throw "Compiled EXE version smoke failed: $($p.ExitCode)" }
        }
        Invoke-Check 'Compiled application clean-state agent smoke' {
            Invoke-CleanAgentSmoke (Join-Path $repo 'src\IS74Wifi.App\bin\Release\net10.0-windows10.0.19041.0\IS74Wifi.exe')
        }
    }
    else {
        [void]$script:results.Add('SKIP  C# contract tests (build failed)')
        [void]$script:results.Add('SKIP  Compiled application version and agent smokes (build failed)')
    }

    Invoke-Check 'PowerShell syntax and reference contract' {
        $files = @('.\IS74Wifi.ps1', '.\agent.ps1', '.\src\IS74Wifi.psm1', '.\tests\critical_path_contract.ps1')
        foreach ($file in $files) {
            $tokens = $null
            $errorsFound = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile(
                (Resolve-Path -LiteralPath $file).Path,
                [ref]$tokens,
                [ref]$errorsFound
            )
            if ($errorsFound.Count -gt 0) {
                throw "PowerShell parse errors in $file : $($errorsFound -join '; ')"
            }
        }
        Import-Module .\src\IS74Wifi.psm1 -Force
        .\tests\critical_path_contract.ps1
    }

    Invoke-Check 'Python static checks' { py -3 .\tests\static_checks.py }
    Invoke-Check 'Interactive UI source contract' { py -3 .\tests\ui_live_status_contract.py }

    if ($NativeAot) {
        Invoke-Check 'NativeAOT restore win-x64' {
            dotnet restore .\src\IS74Wifi.App\IS74Wifi.App.csproj --runtime win-x64 -p:PublishAot=true
        }
        $aotRestoreOk = [bool](@($script:results | Where-Object { $_ -eq 'PASS  NativeAOT restore win-x64' }).Count)
        if ($aotRestoreOk) {
            Invoke-Check 'NativeAOT publish win-x64' {
                dotnet publish .\src\IS74Wifi.App\IS74Wifi.App.csproj `
                    --configuration Release --runtime win-x64 --self-contained true `
                    --no-restore -p:PublishAot=true -p:StripSymbols=true `
                    -p:DebugType=None -p:DebugSymbols=false `
                    --output (Join-Path $reportDir 'nativeaot')
            }
            $aotPublishOk = [bool](@($script:results | Where-Object { $_ -eq 'PASS  NativeAOT publish win-x64' }).Count)
            if ($aotPublishOk) {
                Invoke-Check 'NativeAOT version smoke' {
                    $p = Start-Process -FilePath (Join-Path $reportDir 'nativeaot\IS74Wifi.exe') -ArgumentList version -PassThru -Wait
                    if ($p.ExitCode -ne 0) { throw "NativeAOT EXE version smoke failed: $($p.ExitCode)" }
                }
                Invoke-Check 'NativeAOT clean-state agent smoke' {
                    Invoke-CleanAgentSmoke (Join-Path $reportDir 'nativeaot\IS74Wifi.exe')
                }
            }
        }
    }
    else {
        [void]$script:results.Add('SKIP  NativeAOT checks (rerun with -NativeAot to include)')
    }
}
finally {
    $script:results | Set-Content -LiteralPath $summary -Encoding UTF8
    Write-Host "`n=== SUMMARY ===" -ForegroundColor Cyan
    $script:results | ForEach-Object { Write-Host $_ }
    Stop-Transcript | Out-Null
    Pop-Location
}

$zip = "$reportDir.zip"
Compress-Archive -Path $log, $summary -DestinationPath $zip -Force
Write-Host "`nSend this ZIP after checking it for personal details: $zip" -ForegroundColor Green
if (@($script:results | Where-Object { $_ -like 'FAIL*' }).Count -gt 0) {
    exit 1
}
