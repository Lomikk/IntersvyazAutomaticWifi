#requires -Version 7.0

$ErrorActionPreference = 'Stop'
$stateDir = Join-Path $env:LOCALAPPDATA 'IS74Wifi'
$deviceFile = Join-Path $stateDir 'device-id.txt'
New-Item -ItemType Directory -Force $stateDir | Out-Null

if (Test-Path $deviceFile) {
    $deviceId = (Get-Content $deviceFile -Raw).Trim()
} else {
    $bytes = [byte[]]::new(8)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $deviceId = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    $deviceId | Set-Content $deviceFile -Encoding ASCII -NoNewline
}

$phone = (Read-Host 'Введите номер телефона') -replace '\D',''
if ($phone.Length -gt 10) { $phone = $phone.Substring($phone.Length - 10) }
if ($phone.Length -ne 10) { throw 'Номер должен содержать 10 цифр после нормализации.' }

$response = Invoke-WebRequest `
    -Uri 'https://api.is74.ru/mobile/auth/get-confirm' `
    -Method Post `
    -Headers @{
        Accept = 'application/json; version=v2'
        'X-Device-Id' = $deviceId
        'X-Api-Source' = 'com.intersvyaz.lk'
        'X-App-version' = '2.18.0-RS-95aa9b78'
        Platform = 'Android'
    } `
    -ContentType 'application/json' `
    -Body (@{ phone=$phone; deviceId=$deviceId; authType=0 } | ConvertTo-Json -Compress) `
    -SkipHttpErrorCheck

Write-Host "HTTP: $($response.StatusCode)"
Write-Host $response.Content
