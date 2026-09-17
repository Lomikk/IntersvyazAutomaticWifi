from pathlib import Path

root = Path(__file__).resolve().parents[1]
runtime_files = [
    root / 'IS74Wifi.ps1',
    root / 'agent.ps1',
    root / 'src' / 'IS74Wifi.psm1',
]
for path in runtime_files:
    assert path.read_bytes().startswith(b'\xef\xbb\xbf'), f'{path.name}: UTF-8 BOM required for Windows PowerShell 5.1'

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
assert "if ($now -lt $guardStart) { return }" in module
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
print('static checks: OK')
