#requires -Version 7.0

<#
Прототип длинной штатной последовательности captive portal:
POST stepOne -> GET stepTwo -> ручной ввод 4 цифр -> POST stepTwo.

Этот файл основан на успешном HAR, но сам цельным запуском ещё не проверен.
#>

& {
    $ErrorActionPreference = 'Stop'

    $workDir = Join-Path $env:TEMP 'IS74Wifi-captive-test'
    New-Item -ItemType Directory -Force $workDir | Out-Null

    $phone = (Read-Host 'Введите номер телефона') -replace '\D',''
    if ($phone.Length -gt 10) {
        $phone = $phone.Substring($phone.Length - 10)
    }
    if ($phone.Length -ne 10) {
        throw 'После нормализации должно быть 10 цифр.'
    }

    $phoneWith8 = "8$phone"
    $cookieJar = Join-Path $workDir 'cookies.txt'
    $h1 = Join-Path $workDir 'step1-headers.txt'
    $b1 = Join-Path $workDir 'step1-body.html'

    Write-Host "`n=== STEP ONE ===" -ForegroundColor Cyan

    $status1 = & curl.exe @(
        '-sS'
        '-D', $h1
        '-o', $b1
        '-c', $cookieJar
        '-b', $cookieJar
        '-w', '%{http_code}'
        '-X', 'POST'
        '-H', 'Content-Type: application/x-www-form-urlencoded'
        '--data-urlencode', "phone=$phoneWith8"
        '--data-urlencode', 'dial_code=7'
        '--data-urlencode', 'country_code=ru'
        '--data-urlencode', 'sendPush=on'
        'http://w.is74.ru/stepOne'
    )

    Write-Host "HTTP: $status1"
    if ($status1 -notin @('301','302','303','307','308')) {
        Write-Host (Get-Content $b1 -Raw)
        throw 'stepOne не вернул ожидаемый redirect.'
    }

    $locationLine = Select-String -Path $h1 -Pattern '^Location:\s*(.+)$' | Select-Object -Last 1
    if (-not $locationLine) { throw 'В stepOne нет Location.' }
    $location = $locationLine.Matches[0].Groups[1].Value.Trim()
    $stepTwoUrl = [Uri]::new([Uri]'http://w.is74.ru/stepOne', $location).AbsoluteUri

    Write-Host "stepTwo: $stepTwoUrl"

    Write-Host "`n=== GET STEP TWO ===" -ForegroundColor Cyan
    $statusGet = & curl.exe @(
        '-sS'
        '-o', (Join-Path $workDir 'step2-page.html')
        '-c', $cookieJar
        '-b', $cookieJar
        '-w', '%{http_code}'
        $stepTwoUrl
    )
    Write-Host "HTTP: $statusGet"

    $code = (Read-Host 'Введите 4-значный Wi-Fi код').Trim()
    if ($code -notmatch '^\d{4}$') { throw 'Ожидается 4-значный код.' }

    $h2 = Join-Path $workDir 'step2-post-headers.txt'
    $b2 = Join-Path $workDir 'step2-post-body.html'

    Write-Host "`n=== POST STEP TWO ===" -ForegroundColor Cyan
    $status2 = & curl.exe @(
        '-sS'
        '-D', $h2
        '-o', $b2
        '-c', $cookieJar
        '-b', $cookieJar
        '-w', '%{http_code}'
        '-X', 'POST'
        '-H', 'Content-Type: application/x-www-form-urlencoded'
        '--data-urlencode', "confirmCode=$code"
        '--data-urlencode', "phone=$phone"
        $stepTwoUrl
    )

    Write-Host "HTTP: $status2"

    $finalLocation = Select-String -Path $h2 -Pattern '^Location:\s*(.+)$' | Select-Object -Last 1
    if ($finalLocation) {
        $value = $finalLocation.Matches[0].Groups[1].Value.Trim()
        Write-Host "Location: $value"
    }

    if ($status2 -in @('301','302','303','307','308')) {
        Write-Host 'stepTwo вернул redirect; при корректном коде ожидается stepThree.' -ForegroundColor Green
    }
    else {
        Write-Host 'stepTwo не вернул redirect. Тело ответа сохранено локально.' -ForegroundColor Yellow
    }

    Write-Host "Артефакты: $workDir"
}
