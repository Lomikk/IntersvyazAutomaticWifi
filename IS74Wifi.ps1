#requires -Version 5.1

param(
    [Parameter(Position=0)]
    [ValidateSet('menu','register','connect','status','install','uninstall','reset','purge','toast-test','logs')]
    [string]$Command = 'menu'
)

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'src\IS74Wifi.psm1'
Import-Module $modulePath -Force
Initialize-IS74Storage
Write-IS74RuntimeEvent -Message ("cli.start command={0} psVersion={1}" -f $Command, $PSVersionTable.PSVersion)

function Show-Status {
    $s = Get-IS74Status
    Write-Host ''
    Write-Host '=== IS74 Automatic Wi-Fi ===' -ForegroundColor Cyan
    Write-Host ('Регистрация : {0}' -f $(if ($s.Registered) { 'есть' } else { 'нет' }))
    Write-Host ('Телефон     : {0}' -f $s.Phone)
    Write-Host ('Автозапуск  : {0}' -f $(if ($s.Autostart) { 'включён' } else { 'выключен' }))
    Write-Host ('Интернет    : {0}' -f $(if ($s.InternetNow) { 'доступен' } else { 'не подтверждён' }))
    if ($s.AccessEnd) { Write-Host "API-сессия  : до $($s.AccessEnd)" }
    if ($s.LastAuthUtc) {
        $last = [DateTime]::Parse([string]$s.LastAuthUtc).ToLocalTime()
        Write-Host ('Последняя Wi-Fi авторизация : {0}' -f $last.ToString('dd.MM.yyyy HH:mm:ss'))
    }
    if ($s.ExpectedExpiryUtc) {
        $expiry = [DateTime]::Parse([string]$s.ExpectedExpiryUtc).ToLocalTime()
        Write-Host ('Ожидаемое окончание окна    : {0}' -f $expiry.ToString('dd.MM.yyyy HH:mm:ss'))
    }
    if ($s.LastResult) { Write-Host "Последний результат          : $($s.LastResult)" }
    if ($s.AutomaticStepOneAttempts -gt 0) {
        Write-Host ("Авто stepOne                 : {0}/4" -f $s.AutomaticStepOneAttempts)
    }
    if ($s.UserActionRequired) {
        Write-Host 'Требуется действие           : да — автоматические попытки остановлены' -ForegroundColor Yellow
    }
    Write-Host "Данные приложения            : $($s.StateDirectory)"
    Write-Host "Диагностический журнал       : $($s.DiagnosticLog)"
    Write-Host ''
}

function Invoke-CommandMode {
    param([string]$Name)
    switch ($Name) {
        'register' { $null = Register-IS74Account; return }
        'connect'  { $null = Connect-IS74Wifi -Force; return }
        'status'   { Show-Status; return }
        'install'  { Enable-IS74Autostart -AgentPath (Join-Path $PSScriptRoot 'agent.ps1'); return }
        'uninstall'{ Disable-IS74Autostart; return }
        'reset'    {
            Disable-IS74Autostart
            Reset-IS74Registration
            Write-Host 'Регистрация и локальная сессия удалены.' -ForegroundColor Green
            return
        }
        'purge'    {
            Remove-IS74AllData
            Write-Host 'Автозапуск отключён, папка данных приложения удалена.' -ForegroundColor Green
            return
        }
        'toast-test' {
            $shown = Show-IS74Toast -Title 'Интерсвязь Wi-Fi' -Message 'Тестовое уведомление IS74 Automatic Wi-Fi.' -Diagnostic
            if (-not $shown) { Write-Host 'Автоматическая Wi-Fi авторизация от уведомлений не зависит.' -ForegroundColor DarkGray }
            return
        }
        'logs' {
            $logPath = Get-IS74DiagnosticLogPath
            $logDir = Split-Path -Parent $logPath
            Write-Host "Журнал: $logPath" -ForegroundColor Cyan
            Write-Host 'Для диагностики можно прислать diagnostic.log и diagnostic.N.log из этой папки.'
            try { Start-Process explorer.exe -ArgumentList $logDir } catch { }
            return
        }
    }
}

if ($Command -ne 'menu') {
    try { Invoke-CommandMode -Name $Command } catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }
    exit 0
}

while ($true) {
    Show-Status
    Write-Host '1. Зарегистрировать устройство'
    Write-Host '2. Авторизовать Wi-Fi сейчас'
    Write-Host '3. Включить автозапуск'
    Write-Host '4. Отключить автозапуск'
    Write-Host '5. Показать статус'
    Write-Host '6. Сбросить регистрацию'
    Write-Host '7. Удалить все данные приложения'
    Write-Host '8. Проверить уведомление'
    Write-Host '9. Открыть диагностические логи'
    Write-Host '0. Выход'
    Write-Host ''
    $choice = Read-Host 'Выберите действие'

    try {
        switch ($choice) {
            '1' { $null = Register-IS74Account }
            '2' { $null = Connect-IS74Wifi -Force }
            '3' { Enable-IS74Autostart -AgentPath (Join-Path $PSScriptRoot 'agent.ps1') }
            '4' { Disable-IS74Autostart }
            '5' { Show-Status }
            '6' {
                if ((Read-Host 'Удалить регистрацию и локальную сессию? Введите YES') -eq 'YES') {
                    Disable-IS74Autostart
                    Reset-IS74Registration
                    Write-Host 'Регистрация удалена.' -ForegroundColor Green
                }
            }
            '7' {
                if ((Read-Host 'Удалить ВСЕ данные приложения? Введите PURGE') -eq 'PURGE') {
                    Remove-IS74AllData
                    Write-Host 'Данные удалены.' -ForegroundColor Green
                }
            }
            '8' { Invoke-CommandMode -Name 'toast-test' }
            '9' { Invoke-CommandMode -Name 'logs' }
            '0' { break }
            default { Write-Host 'Неизвестный пункт.' -ForegroundColor Yellow }
        }
    } catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
    }

    if ($choice -eq '0') { break }
    Write-Host ''
    Read-Host 'Enter для продолжения' | Out-Null
}
