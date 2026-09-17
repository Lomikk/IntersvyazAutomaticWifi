#requires -Version 7.0

$ErrorActionPreference = 'Stop'
$stateDir = Join-Path $env:LOCALAPPDATA 'IS74Wifi'
$deviceId = (Get-Content (Join-Path $stateDir 'device-id.txt') -Raw).Trim()
$encrypted = Get-Content (Join-Path $stateDir 'bearer.dpapi') -Raw
$secure = ConvertTo-SecureString $encrypted
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)

try {
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)

    $response = Invoke-WebRequest `
        -Uri 'https://api.is74.ru/mobile/pushmessages?page=1&pageSize=20' `
        -Method Get `
        -Headers @{
            Accept = 'application/json; version=v2'
            Authorization = "Bearer $token"
            'X-Device-Id' = $deviceId
            'X-Api-Source' = 'com.intersvyaz.lk'
            'X-App-version' = '2.18.0-RS-95aa9b78'
            Platform = 'Android'
        } `
        -SkipHttpErrorCheck

    Write-Host "HTTP: $($response.StatusCode)"
    Write-Host $response.Content
}
finally {
    if ($ptr -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
    Remove-Variable token -ErrorAction SilentlyContinue
}
