$ErrorActionPreference = 'Stop'

# Experiment 08: prove that the captive flow does not need GET /stepTwo
# or GET /stepThree. Reuses experiment 07 for baseline + /stepOne + fast
# mailbox polling, then submits the fresh code directly to /stepTwo.
#
# 07 asks for TEST before it creates a code. This wrapper then asks for AUTH
# before the code is actually submitted.

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exp07 = Join-Path $here '07-fast-code-acquisition.ps1'

if (-not (Test-Path $exp07)) {
    throw "Не найден $exp07"
}

Write-Host ''
Write-Host '=== EXPERIMENT 08 ===' -ForegroundColor Cyan
Write-Host 'Сначала запускается experiment 07: baseline -> stepOne -> fast polling.'
Write-Host 'После получения свежего кода будет предложен прямой POST /stepTwo.'
Write-Host 'GET /stepTwo и GET /stepThree не выполняются.'
Write-Host ''

# Dot-source intentionally: after successful experiment 07 we need its
# in-memory $foundPoll.Code, normalized $phone and observed $stepLocation.
. $exp07

if (-not $foundPoll -or -not $foundPoll.Code) {
    throw 'Experiment 07 не оставил свежий Wi-Fi-код в памяти.'
}

if (-not $phone -or $phone.Length -ne 10) {
    throw 'После experiment 07 не найден нормализованный 10-значный phone.'
}

if (-not $stepLocation -or $stepLocation -notmatch '(^|/)stepTwo\?') {
    throw "Experiment 07 не подтвердил переход на stepTwo: $stepLocation"
}

Write-Host ''
Write-Host '============================================================'
Write-Host 'DIRECT STEP TWO WILL AUTHORIZE THIS CLIENT' -ForegroundColor Yellow
Write-Host 'GET /stepTwo: НЕТ'
Write-Host 'GET /stepThree: НЕТ'
Write-Host 'Код в консоль не выводится.'
Write-Host '============================================================'

if ((Read-Host 'Для отправки кода введите AUTH') -ne 'AUTH') {
    Write-Host 'Отменено.'
    return
}

$portalBase = 'http://w.is74.ru'
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)

$request = $null
$response = $null

try {
    $stepTwoUri = if ($stepLocation -match '^https?://') {
        [Uri]$stepLocation
    }
    else {
        [Uri]::new([Uri]"$portalBase/", $stepLocation)
    }

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        $stepTwoUri
    )

    $form = [System.Collections.Generic.Dictionary[string,string]]::new()
    $form['confirmCode'] = $foundPoll.Code
    $form['phone'] = $phone
    $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($form)

    Write-Host ''
    Write-Host '=== DIRECT STEP TWO ===' -ForegroundColor Cyan

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $response = $client.SendAsync(
        $request,
        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
    ).GetAwaiter().GetResult()
    $sw.Stop()

    $status = [int]$response.StatusCode
    $location = if ($response.Headers.Location) {
        $response.Headers.Location.ToString()
    }
    else {
        ''
    }

    Write-Host ('RTT      : {0:N1} ms' -f $sw.Elapsed.TotalMilliseconds)
    Write-Host "HTTP     : $status"
    Write-Host "Location : $location"

    $accepted = (
        $status -ge 300 -and
        $status -lt 400 -and
        $location -match '(^|/)stepThree(?:\?|$)'
    )

    if (-not $accepted) {
        throw 'Прямой POST /stepTwo не дал ожидаемый redirect на stepThree.'
    }

    Write-Host ''
    Write-Host 'DIRECT POST /stepTwo ACCEPTED.' -ForegroundColor Green
    Write-Host 'GET /stepTwo: НЕТ'
    Write-Host 'GET /stepThree: НЕТ'
    Write-Host 'Теперь можно отдельно проверить, что Интернет открылся.'
}
finally {
    if ($response) { $response.Dispose() }
    if ($request)  { $request.Dispose() }
    $client.Dispose()
    $handler.Dispose()
}
