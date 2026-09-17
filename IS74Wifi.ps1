#requires -Version 5.1

param(
    [Parameter(Position=0)]
    [ValidateSet('menu','register','connect','status','install','uninstall','reset','purge','toast-test')]
    [string]$Command = 'menu'
)

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'src\IS74Wifi.psm1'
Import-Module $modulePath -Force
Initialize-IS74Storage

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
    Write-Host "Данные приложения            : $($s.StateDirectory)"
    Write-Host ''
}

function Invoke-CommandMode {
    param([string]$Name)
    switch ($Name) {
        'register' { $null = Register-IS74Account; return }
        'connect'  { $null = Connect-IS74Wifi; return }
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
            $shown = Show-IS74Toast -Title 'Интерсвязь Wi-Fi' -Message 'Тестовое уведомление IS74 Automatic Wi-Fi.'
            if ($shown) { Write-Host 'Toast отправлен.' -ForegroundColor Green }
            else { Write-Host 'Toast показать не удалось. Смотрите журнал приложения.' -ForegroundColor Yellow }
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
    Write-Host '0. Выход'
    Write-Host ''
    $choice = Read-Host 'Выберите действие'

    try {
        switch ($choice) {
            '1' { $null = Register-IS74Account }
            '2' { $null = Connect-IS74Wifi }
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
