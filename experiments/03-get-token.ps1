#requires -Version 7.0

$ErrorActionPreference = 'Stop'
$stateDir = Join-Path $env:LOCALAPPDATA 'IS74Wifi'
$deviceId = (Get-Content (Join-Path $stateDir 'device-id.txt') -Raw).Trim()
$check = Get-Content (Join-Path $stateDir 'check-confirm-success.json') -Raw | ConvertFrom-Json
$authId = $check.authId
if (-not $authId) { throw 'В check-confirm-success.json нет authId.' }

$response = Invoke-WebRequest `
    -Uri 'https://api.is74.ru/mobile/auth/get-token' `
    -Method Post `
    -Headers @{
        Accept = 'application/json; version=v2'
        'X-Device-Id' = $deviceId
        'X-Api-User-Id' = '-1'
        'X-Api-Profile-Id' = 'null'
        'X-Api-Source' = 'com.intersvyaz.lk'
        'X-App-version' = '2.18.0-RS-95aa9b78'
        Platform = 'Android'
    } `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ authId=$authId; userId=''; uniqueDeviceId=$deviceId } `
    -SkipHttpErrorCheck

Write-Host "HTTP: $($response.StatusCode)"
if ($response.StatusCode -ne 200) {
    Write-Host $response.Content
    return
}

$session = $response.Content | ConvertFrom-Json
$token = $session.TOKEN
if (-not $token) { throw 'HTTP 200, но TOKEN не найден.' }

$secureToken = ConvertTo-SecureString $token -AsPlainText -Force
ConvertFrom-SecureString $secureToken |
    Set-Content (Join-Path $stateDir 'bearer.dpapi') -Encoding ASCII -NoNewline

[pscustomobject]@{
    deviceId    = $deviceId
    userId      = $session.USER_ID
    profileId   = $session.PROFILE_ID
    accessBegin = $session.ACCESS_BEGIN
    accessEnd   = $session.ACCESS_END
} | ConvertTo-Json | Set-Content (Join-Path $stateDir 'session-meta.json') -Encoding UTF8

Write-Host 'Bearer получен и сохранён через Windows DPAPI.' -ForegroundColor Green
Write-Host "ACCESS_BEGIN: $($session.ACCESS_BEGIN)"
Write-Host "ACCESS_END:   $($session.ACCESS_END)"
Remove-Variable token -ErrorAction SilentlyContinue
