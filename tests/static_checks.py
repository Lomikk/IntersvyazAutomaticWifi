from pathlib import Path

root = Path(__file__).resolve().parents[1]
module = (root / 'src' / 'IS74Wifi.psm1').read_text(encoding='utf-8')
cli = (root / 'IS74Wifi.ps1').read_text(encoding='utf-8')
agent = (root / 'agent.ps1').read_text(encoding='utf-8')

for text, name in [(module, 'module'), (cli, 'cli'), (agent, 'agent')]:
    assert '#requires -Version 5.1' in text, f'{name}: PS 5.1 requirement missing'

assert '-SkipHttpErrorCheck' not in module
assert 'ForEach-Object -Parallel' not in module
assert '??' not in module
assert 'Show-IS74Toast' in module
assert 'Начинаю автоматическую авторизацию Wi-Fi.' in module
assert 'Авторизация завершена.' in module
assert 'preExpiryMinutes = 10' in module
assert 'New-ScheduledTaskTrigger -AtLogOn' in module
assert 'Get-NetConnectionProfile' in module
assert 'ExecutionTimeLimit ([TimeSpan]::Zero)' in module
assert "LogonType Interactive" in module
assert "RunLevel Limited" in module
assert 'GET /stepTwo' not in module
assert 'GET /stepThree' not in module
assert 'secrets.dpapi' in module
assert 'confirmCode' in module
assert "authId'] = ''" in module
print('static checks: OK')
