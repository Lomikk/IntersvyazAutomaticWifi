from pathlib import Path

root = Path(__file__).resolve().parents[1]
runtime_files = [
    root / 'IS74Wifi.ps1',
    root / 'agent.ps1',
    root / 'src' / 'IS74Wifi.psm1',
]
for path in runtime_files:
    assert path.read_bytes().startswith(b'\xef\xbb\xbf'), f'{path.name}: UTF-8 BOM required for Windows PowerShell 5.1'

contract_test = root / 'tests' / 'critical_path_contract.ps1'
assert contract_test.read_bytes().startswith(b'\xef\xbb\xbf'), 'critical_path_contract.ps1: UTF-8 BOM required for Windows PowerShell 5.1'

module = (root / 'src' / 'IS74Wifi.psm1').read_text(encoding='utf-8')
cli = (root / 'IS74Wifi.ps1').read_text(encoding='utf-8')
agent = (root / 'agent.ps1').read_text(encoding='utf-8')

for text, name in [(module, 'module'), (cli, 'cli'), (agent, 'agent')]:
    assert '#requires -Version 5.1' in text, f'{name}: PS 5.1 requirement missing'

assert '-SkipHttpErrorCheck' not in module
assert 'ForEach-Object -Parallel' not in module
assert '??' not in module
assert 'Show-IS74Toast' not in module
assert 'preExpiryMinutes' not in module
assert 'maxAutomaticStepOneAttempts = 4' in module
assert 'automaticRetryDelaysSeconds = @(15, 30, 60)' in module
assert 'internetProbeConfirmDelaySeconds = 2' in module
assert 'guardWindowSeconds = 10' in module
assert 'guardProbeIntervalMilliseconds = 250' in module
assert 'Test-IS74InternetUnavailableConfirmed' in module
assert 'Get-NetConnectionProfile' not in module.replace('# NCSI/Get-NetConnectionProfile is deliberately not authoritative here.', '')
assert 'LegacyTokenFile' not in module
assert 'Get-IS74LegacyToken' not in module
assert 'bearer.dpapi' not in module
assert 'New-ScheduledTaskTrigger -AtLogOn' in module
assert 'ExecutionTimeLimit ([TimeSpan]::Zero)' in module
assert "LogonType Interactive" in module
assert "RunLevel Limited" in module
assert 'GET /stepTwo' not in module
assert 'GET /stepThree' not in module
assert 'secrets.dpapi' in module
assert 'confirmCode' in module
assert "'connect'  { $null = Connect-IS74Wifi -Force; return }" in cli
assert "authId'] = ''" in module
assert 'if ($now -lt $guardStart) {' in module and 'Update-IS74InternetProbeAddressCache' in module
assert 'At or after the expected 24-hour boundary, the timer itself is enough.' in module
assert 'Get-IS74AgentSleepMilliseconds' in module
assert 'Start-Sleep -Milliseconds $delayMs' in agent
assert "automaticStepOneAttempts" in module
assert "userActionRequired" in module
assert "Cache-Control', 'no-cache, no-store'" in module
assert "diagnostic.log" in module
assert "LogMaxBytes    = 1MB" in module
assert "LogRetentionFiles = 5" in module
assert "Protect-IS74LogText" in module
assert "<redacted-code>" in module
assert "Get-IS74DiagnosticLogPath" in module
assert "'logs'" in cli
assert "agent.start" in agent
assert "agent.tick error=" in agent

assert "System32\\WindowsPowerShell\\v1.0\\powershell.exe" in module
assert 'toast-test' not in cli
assert 'AppUserModelID' not in module
assert 'Windows.UI.Notifications' not in module
assert '.Headers.Date.HasValue' not in module
assert '.Headers.Date.Value' not in module
assert '$Json.items' not in module
assert '$json.items' not in module
assert '$Message.subject' not in module
assert '$Message.push_message' not in module
assert '$Message.full_message' not in module
assert 'Get-IS74PushMessages' in module
assert 'Get-IS74PropertyValue' in module

# Critical-path invariants discovered during production audit.
for forbidden in ['$checkJson.', '$secrets.token', '$secrets.phone', '$session.TOKEN', '$session.ACCESS_BEGIN', '$session.ACCESS_END']:
    assert forbidden not in module, f'unsafe StrictMode external property access: {forbidden}'

fast_start = module.index('while ($clock.Elapsed.TotalMilliseconds -lt 12000 -and -not $found)')
fast_end = module.index('if (-not $found -and $pollErrorCount -gt 0)', fast_start)
fast_loop = module[fast_start:fast_end]
assert 'Find-IS74WifiCodeAfterBaseline' not in fast_loop, 'fast polling loop must not perform synchronous fallback HTTP'
assert 'Send-IS74Request' not in fast_loop, 'fast polling loop must not perform synchronous HTTP'
assert 'Write-IS74Log' not in fast_loop, 'fast polling loop must not perform disk logging'
assert "Kind = 'Fallback'" in fast_loop, 'pageSize=5 fallback must be scheduled asynchronously'
assert 'if (-not $found -and -not $stepResponse -and -not $stepException)' in module, 'fresh code must bypass waiting for stepOne HTTP completion'
assert 'New-IS74HttpClientPair -TimeoutSeconds 3 -MaxConnections 16' in module, 'critical API timeout guard missing'
assert 'New-IS74HttpClientPair -NoRedirect -TimeoutSeconds 5 -MaxConnections 4' in module, 'portal critical timeout guard missing'
assert '[Threading.Thread]::SpinWait(250)' in fast_loop, 'fast polling scheduler must preserve low-jitter experiment behavior'
assert 'critical.timing preStepOneMs=' in module, 'critical-path timing telemetry missing'
assert 'Get-IS74BaselineId -Client $apiPair.Client -Token $token -DeviceId $deviceId -SuppressSuccessLog' in module, 'critical baseline success logging must be deferred'
assert 'push.baseline schemaUnrecognized=true' in module, 'unknown baseline schema must fail closed'
assert "$bodyText.Trim() -eq '[]'" in module, 'empty root-array baseline must be engine independent'
assert 'IS74UnexpectedAfterStepOne' in module, 'unknown post-stepOne failures must not be blindly retried'
assert '$stepOneAmbiguous = $true' in module, 'async stepOne transport loss must not cause blind retry'
assert 'Register-IS74PreStepFailure' in module, 'safe pre-step retry state missing'
assert "[Threading.Mutex]::new($false, 'Local\\IS74Wifi.Auth')" in module, 'cross-process auth transaction mutex missing'
assert 'guardProbeTimeoutMilliseconds = 300' in module, 'guard probe timeout must be explicitly bounded'
assert 'if (($expiry - $now).TotalMilliseconds -le $guardProbeBudgetMs) { return }' in module, 'pre-expiry probe must not cross T'
assert 'if (-not $first.HttpResponseReceived) { return $false }' in module, 'transport timeout must not trigger pre-expiry stepOne'
assert '[Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()' in module, 'Wi-Fi presence check must avoid netsh on the normal path'
assert 'Update-IS74InternetProbeAddressCache' in module, 'guard DNS must be warmed outside the critical window'
assert "$request.Headers.Host = 'www.msftconnecttest.com'" in module, 'cached-IP probe must preserve HTTP Host'
assert '$first = Invoke-IS74InternetProbe -TimeoutMilliseconds $ProbeTimeoutMilliseconds -UseCachedAddress' in module, 'guard probe must not perform DNS'

step_two_accept = module.index('Set-IS74SuccessfulAuthState -InternetConfirmed:$false -AuthorizedAtUtc $stepTwo.DateUtc')
internet_verify = module.index('for ($i = 0; $i -lt 8; $i++)', step_two_accept)
assert step_two_accept < internet_verify, 'successful stepTwo state must persist before Internet verification'

assert 'Test-IS74AlreadyAuthorizedLocation' in module
assert 'landing/pages/prilozheniye' in module
assert 'Get-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue' in module
assert 'Wait-IS74ScheduledTaskStopped' in module, 'autostart replacement must wait for the old agent to stop'
assert 'tests\\critical_path_contract.ps1' not in module  # sanity: tests stay outside runtime

print('static checks: OK')
