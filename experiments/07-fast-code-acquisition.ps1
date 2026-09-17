$ErrorActionPreference = 'Stop'

# Production-like timing experiment for Wi-Fi-code acquisition.
# Starts exactly one /stepOne at T=0, then launches mailbox GET requests
# at absolute offsets from the /stepOne start. GET requests do NOT wait for
# earlier GET responses. Stops scheduling new GETs as soon as a fresh Wi-Fi
# code is observed. This script NEVER posts the code to /stepTwo and never
# prints the 4-digit code.

$baseDir    = Join-Path $env:LOCALAPPDATA 'IS74Wifi'
$deviceFile = Join-Path $baseDir 'device-id.txt'
$tokenFile  = Join-Path $baseDir 'bearer.dpapi'

$apiBase    = 'https://api.is74.ru'
$portalBase = 'http://w.is74.ru'
$appVersion = '2.18.0-RS-95aa9b78'
$buildCode  = 2026061111

# Candidate production polling profile. These are ABSOLUTE offsets from
# /stepOne launch, not delays after the previous GET completes.
$pollScheduleMs = @(100, 150, 200, 250, 350, 500, 700, 1000, 1400, 2000, 3000, 4500, 6500, 10000)

if (-not (Test-Path $deviceFile)) { throw "Не найден $deviceFile" }
if (-not (Test-Path $tokenFile))  { throw "Не найден $tokenFile" }

$deviceId = (Get-Content $deviceFile -Raw).Trim()
$encrypted = (Get-Content $tokenFile -Raw).Trim()
$secure = ConvertTo-SecureString $encrypted
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
}
finally {
    if ($ptr -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

if (-not $token) { throw 'Не удалось прочитать Bearer.' }

$phoneInput = Read-Host 'Номер телефона аккаунта'
$phone = $phoneInput -replace '\D', ''
if ($phone.Length -eq 11 -and ($phone[0] -eq '7' -or $phone[0] -eq '8')) {
    $phone = $phone.Substring(1)
}
if ($phone.Length -ne 10) { throw 'После нормализации должно остаться 10 цифр.' }
$portalPhone = "8$phone"

$apiHandler = [System.Net.Http.HttpClientHandler]::new()
$apiHandler.MaxConnectionsPerServer = 16
$apiClient = [System.Net.Http.HttpClient]::new($apiHandler)
$apiClient.Timeout = [TimeSpan]::FromSeconds(15)

$portalHandler = [System.Net.Http.HttpClientHandler]::new()
$portalHandler.AllowAutoRedirect = $false
$portalClient = [System.Net.Http.HttpClient]::new($portalHandler)
$portalClient.Timeout = [TimeSpan]::FromSeconds(15)

function New-ApiRequest {
    param([int]$PageSize = 1)

    $req = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Get,
        "$apiBase/mobile/pushmessages?page=1&pageSize=$PageSize"
    )

    $null = $req.Headers.TryAddWithoutValidation('Accept', 'application/json; version=v2')
    $null = $req.Headers.TryAddWithoutValidation('Authorization', "Bearer $token")
    $null = $req.Headers.TryAddWithoutValidation('X-Device-Id', $deviceId)
    $null = $req.Headers.TryAddWithoutValidation('X-Api-Source', 'com.intersvyaz.lk')
    $null = $req.Headers.TryAddWithoutValidation('X-App-version', $appVersion)
    $null = $req.Headers.TryAddWithoutValidation('Platform', 'Android')
    $null = $req.Headers.TryAddWithoutValidation('User-Agent', "4.11.0 com.intersvyaz.lk/$appVersion.$buildCode")
    $null = $req.Headers.TryAddWithoutValidation('Cache-Control', 'no-cache')
    return $req
}

function Get-TopMessageFromJson {
    param($Json)

    if ($Json -is [array]) { return @($Json)[0] }
    if ($Json.items)       { return @($Json.items)[0] }
    if ($Json.data -is [array]) { return @($Json.data)[0] }
    return $Json
}

function Get-WifiCode {
    param($Message)

    if ($null -eq $Message) { return $null }
    if ([string]$Message.subject -ne 'Ваш код авторизации') { return $null }

    foreach ($text in @([string]$Message.push_message, [string]$Message.full_message)) {
        if ($text -match '^(\d{4}) код авторизации в приложении "Интерсвязь"$') {
            return $Matches[1]
        }
    }

    return $null
}

function Complete-Poll {
    param(
        $Poll,
        [Diagnostics.Stopwatch]$Clock,
        [Int64]$BaselineId
    )

    if ($Poll.Processed -or -not $Poll.Task.IsCompleted) { return $null }

    $Poll.Processed = $true
    $Poll.ObservedDoneMs = $Clock.Elapsed.TotalMilliseconds

    try {
        $resp = $Poll.Task.GetAwaiter().GetResult()
        try {
            $Poll.Http = [int]$resp.StatusCode
            if ($resp.Headers.Contains('X-Cache-Status')) {
                $Poll.Cache = ($resp.Headers.GetValues('X-Cache-Status') -join ', ')
            }

            $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if (-not $resp.IsSuccessStatusCode) { return $Poll }

            $json = $body | ConvertFrom-Json
            $msg = Get-TopMessageFromJson $json
            if (-not $msg) { return $Poll }

            $Poll.Id = [Int64]$msg.id
            $Poll.Subject = [string]$msg.subject
            $Poll.ServerDate = [string]$msg.ins_date
            $Poll.IsNew = $Poll.Id -gt $BaselineId

            if ($Poll.IsNew) {
                $candidate = Get-WifiCode $msg
                if ($candidate) {
                    $Poll.IsWifiCode = $true
                    $Poll.Code = $candidate
                }
            }
        }
        finally {
            $resp.Dispose()
        }
    }
    catch {
        $Poll.Error = $_.Exception.Message
    }

    return $Poll
}

$baselineReq = $null
$baselineResp = $null
$stepRequest = $null
$stepResponse = $null

try {
    Write-Host ''
    Write-Host '=== BASELINE ===' -ForegroundColor Cyan

    # Baseline also warms the API connection used by scheduled GETs.
    $baselineReq = New-ApiRequest
    $baselineSw = [Diagnostics.Stopwatch]::StartNew()
    $baselineResp = $apiClient.SendAsync($baselineReq).GetAwaiter().GetResult()
    $baselineBody = $baselineResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $baselineSw.Stop()

    if (-not $baselineResp.IsSuccessStatusCode) {
        throw "Baseline HTTP $([int]$baselineResp.StatusCode)"
    }

    $baselineJson = $baselineBody | ConvertFrom-Json
    $baselineMsg = Get-TopMessageFromJson $baselineJson
    $baselineId = if ($baselineMsg) { [Int64]$baselineMsg.id } else { [Int64]0 }
    $baselineCache = if ($baselineResp.Headers.Contains('X-Cache-Status')) {
        $baselineResp.Headers.GetValues('X-Cache-Status') -join ', '
    } else { '' }

    Write-Host "Baseline ID : $baselineId"
    if ($baselineMsg) {
        Write-Host "Subject     : $($baselineMsg.subject)"
        Write-Host "Date        : $($baselineMsg.ins_date)"
    }
    Write-Host ('GET time    : {0:N1} ms' -f $baselineSw.Elapsed.TotalMilliseconds)
    Write-Host "Cache       : $baselineCache"

    $baselineResp.Dispose(); $baselineResp = $null
    $baselineReq.Dispose();  $baselineReq = $null

    Write-Host ''
    Write-Host '============================================================'
    Write-Host 'SCHEDULED FAST CODE ACQUISITION TEST' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Будет выполнен РОВНО ОДИН POST /stepOne.'
    Write-Host 'GET запускаются по абсолютному времени от старта /stepOne:'
    Write-Host (($pollScheduleMs -join ', ') + ' ms')
    Write-Host 'Ответ предыдущего GET не задерживает запуск следующего.'
    Write-Host 'После обнаружения нового Wi-Fi-кода новые GET больше не запускаются.'
    Write-Host '/stepTwo отсутствует; код не выводится.'
    Write-Host '============================================================'

    if ((Read-Host 'Введите TEST') -ne 'TEST') {
        Write-Host 'Отменено.'
        return
    }

    $stepRequest = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "$portalBase/stepOne"
    )

    $form = [System.Collections.Generic.Dictionary[string,string]]::new()
    $form['phone']        = $portalPhone
    $form['dial_code']    = '7'
    $form['country_code'] = 'ru'
    $form['sendPush']     = 'on'
    $stepRequest.Content = [System.Net.Http.FormUrlEncodedContent]::new($form)

    Write-Host ''
    Write-Host '=== START ===' -ForegroundColor Cyan

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $stepTask = $portalClient.SendAsync(
        $stepRequest,
        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
    )

    $polls = [System.Collections.Generic.List[object]]::new()
    $nextIndex = 0
    $foundPoll = $null
    $stepObserved = $false
    $stepDoneMs = $null
    $stepStatus = $null
    $stepLocation = ''

    while ($clock.Elapsed.TotalMilliseconds -lt 12000) {
        $now = $clock.Elapsed.TotalMilliseconds

        # Launch every due GET according to the absolute schedule.
        while (-not $foundPoll -and $nextIndex -lt $pollScheduleMs.Count -and $now -ge $pollScheduleMs[$nextIndex]) {
            $target = [double]$pollScheduleMs[$nextIndex]
            $req = New-ApiRequest
            $actualStart = $clock.Elapsed.TotalMilliseconds
            $task = $apiClient.SendAsync($req)

            $poll = [pscustomobject]@{
                N              = $nextIndex + 1
                TargetMs       = $target
                StartMs        = $actualStart
                Request        = $req
                Task           = $task
                Processed      = $false
                ObservedDoneMs = $null
                Http           = $null
                Id             = $null
                Subject        = ''
                ServerDate     = ''
                IsNew          = $false
                IsWifiCode     = $false
                Code           = $null
                Cache          = ''
                Error          = ''
            }
            $polls.Add($poll)

            Write-Host ('launch #{0,-2} target={1,6:N0} ms actual={2,8:N1} ms' -f $poll.N,$target,$actualStart)
            $nextIndex++
            $now = $clock.Elapsed.TotalMilliseconds
        }

        # Observe completed GETs without blocking on unfinished ones.
        foreach ($p in $polls) {
            $completed = Complete-Poll -Poll $p -Clock $clock -BaselineId $baselineId
            if (-not $completed) { continue }

            Write-Host ('done   #{0,-2} target={1,6:N0} start={2,8:N1} seen={3,8:N1} id={4} new={5} code={6} cache={7}' -f `
                $completed.N,$completed.TargetMs,$completed.StartMs,$completed.ObservedDoneMs,$completed.Id,$completed.IsNew,$completed.IsWifiCode,$completed.Cache)

            if ($completed.IsWifiCode -and -not $foundPoll) {
                $foundPoll = $completed
            }
        }

        # Observe /stepOne completion independently; never wait for it before polling.
        if (-not $stepObserved -and $stepTask.IsCompleted) {
            $stepObserved = $true
            $stepDoneMs = $clock.Elapsed.TotalMilliseconds
            try {
                $stepResponse = $stepTask.GetAwaiter().GetResult()
                $stepStatus = [int]$stepResponse.StatusCode
                if ($stepResponse.Headers.Location) {
                    $stepLocation = $stepResponse.Headers.Location.ToString()
                }
                Write-Host ('>>> /stepOne observed complete at {0:N1} ms | HTTP {1} | {2}' -f $stepDoneMs,$stepStatus,$stepLocation) -ForegroundColor DarkCyan
            }
            catch {
                Write-Host ('>>> /stepOne error observed at {0:N1} ms | {1}' -f $stepDoneMs,$_.Exception.Message) -ForegroundColor Yellow
            }
        }

        if ($foundPoll) { break }

        # If every scheduled request was launched and all of them finished, there is nothing left to wait for.
        if ($nextIndex -ge $pollScheduleMs.Count -and @($polls | Where-Object { -not $_.Processed }).Count -eq 0) {
            break
        }

        # Keep launch jitter small without burning a full CPU core.
        $nextTarget = if ($nextIndex -lt $pollScheduleMs.Count) { [double]$pollScheduleMs[$nextIndex] } else { $null }
        if ($null -ne $nextTarget) {
            $remaining = $nextTarget - $clock.Elapsed.TotalMilliseconds
            if ($remaining -gt 4) { Start-Sleep -Milliseconds 1 }
            else { [Threading.Thread]::SpinWait(250) }
        }
        else {
            Start-Sleep -Milliseconds 1
        }
    }

    # /stepOne is diagnostic here. Wait only after code acquisition/schedule completion
    # so that this wait cannot delay any mailbox request.
    if (-not $stepObserved) {
        try {
            $stepResponse = $stepTask.GetAwaiter().GetResult()
            $stepDoneMs = $clock.Elapsed.TotalMilliseconds
            $stepStatus = [int]$stepResponse.StatusCode
            if ($stepResponse.Headers.Location) {
                $stepLocation = $stepResponse.Headers.Location.ToString()
            }
        }
        catch {
            $stepDoneMs = $clock.Elapsed.TotalMilliseconds
        }
    }

    $clock.Stop()

    Write-Host ''
    Write-Host '=== RESULT ===' -ForegroundColor Cyan

    if ($foundPoll) {
        Write-Host 'WI-FI CODE ACQUIRED' -ForegroundColor Green
        Write-Host "Message ID  : $($foundPoll.Id)"
        Write-Host "Server date : $($foundPoll.ServerDate)"
        Write-Host "Subject     : $($foundPoll.Subject)"
        Write-Host ('GET target  : {0:N0} ms' -f $foundPoll.TargetMs)
        Write-Host ('GET started : {0:N1} ms' -f $foundPoll.StartMs)
        Write-Host ('Seen by app : {0:N1} ms' -f $foundPoll.ObservedDoneMs)
        Write-Host 'Код получен только в памяти и намеренно не выводится.'
    }
    else {
        Write-Host 'Новый Wi-Fi-код не обнаружен по расписанию.' -ForegroundColor Yellow
    }

    if ($null -ne $stepStatus) {
        Write-Host "stepOne HTTP : $stepStatus"
        Write-Host "Location     : $stepLocation"
        Write-Host ('stepOne seen : {0:N1} ms' -f $stepDoneMs)
    }
    else {
        Write-Host 'stepOne      : нормальный HTTP-ответ не получен'
    }

    Write-Host 'POST /stepTwo: НЕТ'
}
finally {
    if ($baselineResp) { $baselineResp.Dispose() }
    if ($baselineReq)  { $baselineReq.Dispose() }
    if ($stepResponse) { $stepResponse.Dispose() }
    if ($stepRequest)  { $stepRequest.Dispose() }

    if ($polls) {
        foreach ($p in $polls) {
            if ($p.Request) { $p.Request.Dispose() }
        }
    }

    $apiClient.Dispose()
    $apiHandler.Dispose()
    $portalClient.Dispose()
    $portalHandler.Dispose()

    Remove-Variable token -ErrorAction SilentlyContinue
}
