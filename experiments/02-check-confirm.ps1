#requires -Version 7.0

$ErrorActionPreference = 'Stop'
$stateDir = Join-Path $env:LOCALAPPDATA 'IS74Wifi'
$deviceId = (Get-Content (Join-Path $stateDir 'device-id.txt') -Raw).Trim()

$phone = (Read-Host 'Введите номер телефона') -replace '\D',''
if ($phone.Length -gt 10) { $phone = $phone.Substring($phone.Length - 10) }
if ($phone.Length -ne 10) { throw 'Номер должен содержать 10 цифр после нормализации.' }

$code = (Read-Host 'Введите SMS-код').Trim()
if ($code -notmatch '^\d+$') { throw 'Код должен состоять из цифр.' }

$response = Invoke-WebRequest `
    -Uri 'https://api.is74.ru/mobile/auth/check-confirm' `
    -Method Post `
    -Headers @{
        Accept = 'application/json; version=v2'
        'X-Device-Id' = $deviceId
        'X-Api-Source' = 'com.intersvyaz.lk'
        'X-App-version' = '2.18.0-RS-95aa9b78'
        Platform = 'Android'
    } `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ phone=$phone; confirmCode=$code; authId='' } `
    -SkipHttpErrorCheck

Write-Host "HTTP: $($response.StatusCode)"
Write-Host $response.Content

if ($response.StatusCode -eq 200) {
    $response.Content | Set-Content (Join-Path $stateDir 'check-confirm-success.json') -Encoding UTF8
}
