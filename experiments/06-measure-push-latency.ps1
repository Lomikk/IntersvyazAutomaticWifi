$ErrorActionPreference = 'Stop'

# Diagnostic experiment only.
# Starts polling /mobile/pushmessages BEFORE /stepOne so we can measure
# when the Wi-Fi-code message becomes visible relative to the portal response.
# This script NEVER posts the code to /stepTwo.

$baseDir    = Join-Path $env:LOCALAPPDATA 'IS74Wifi'
$deviceFile = Join-Path $baseDir 'device-id.txt'
$tokenFile  = Join-Path $baseDir 'bearer.dpapi'

$apiBase    = 'https://api.is74.ru'
$portalBase = 'http://w.is74.ru'
$appVersion = '2.18.0-RS-95aa9b78'
$buildCode  = 2026061111

if (-not (Test-Path $deviceFile)) { throw "Не найден $deviceFile" }
if (-not (Test-Path $tokenFile))  { throw "Не найден $tokenFile" }

if (-not (Get-Command Start-ThreadJob -ErrorAction SilentlyContinue)) {
    Import-Module ThreadJob -ErrorAction Stop
}

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

$apiHeaders = @{
    Accept           = 'application/json; version=v2'
    Authorization    = "Bearer $token"
    'X-Device-Id'    = $deviceId
    'X-Api-Source'   = 'com.intersvyaz.lk'
    'X-App-version'  = $appVersion
    Platform         = 'Android'
    'User-Agent'     = "4.11.0 com.intersvyaz.lk/$appVersion.$buildCode"
    'Cache-Control'  = 'no-cache'
}

Write-Host ''
Write-Host '=== BASELINE ===' -ForegroundColor Cyan
$baselineResponse = Invoke-WebRequest `
    -Uri "$apiBase/mobile/pushmessages?page=1&pageSize=1" `
    -Method Get `
    -Headers $apiHeaders `
    -SkipHttpErrorCheck

if ($baselineResponse.StatusCode -ne 200) {
    throw "Baseline GET: HTTP $($baselineResponse.StatusCode)"
}

$baselineJson = $baselineResponse.Content | ConvertFrom-Json
$baselineMessage = if ($baselineJson -is [array]) { @($baselineJson)[0] } else { $baselineJson }
$baselineId = if ($baselineMessage) { [Int64]$baselineMessage.id } else { [Int64]0 }

Write-Host "Baseline ID : $baselineId"
if ($baselineMessage) {
    Write-Host "Subject     : $($baselineMessage.subject)"
    Write-Host "Date        : $($baselineMessage.ins_date)"
}
Write-Host "Cache       : $(($baselineResponse.Headers['X-Cache-Status']) -join ', ')"

$watcherScript = {
    param($ApiBase, $Bearer, $DeviceId, $AppVersion, $BuildCode, $BaselineId)

    $headers = @{
        Accept          = 'application/json; version=v2'
        Authorization   = "Bearer $Bearer"
        'X-Device-Id'   = $DeviceId
        'X-Api-Source'  = 'com.intersvyaz.lk'
        'X-App-version' = $AppVersion
        Platform        = 'Android'
        'User-Agent'    = "4.11.0 com.intersvyaz.lk/$AppVersion.$BuildCode"
        'Cache-Control' = 'no-cache'
    }

    $url = "$ApiBase/mobile/pushmessages?page=1&pageSize=1"
    $deadline = [DateTime]::UtcNow.AddSeconds(6)

    for ($n = 1; $n -le 50 -and [DateTime]::UtcNow -lt $deadline; $n++) {
        $startTick = [Diagnostics.Stopwatch]::GetTimestamp()
        try {
            $r = Invoke-WebRequest -Uri $url -Method Get -Headers $headers -SkipHttpErrorCheck
            $endTick = [Diagnostics.Stopwatch]::GetTimestamp()

            if ($r.StatusCode -ne 200) {
                [pscustomobject]@{ Kind='Poll'; N=$n; StartTick=$startTick; EndTick=$endTick; Id=0; IsNew=$false; Cache=''; Http=[int]$r.StatusCode }
                continue
            }

            $json = $r.Content | ConvertFrom-Json
            $msg = if ($json -is [array]) { @($json)[0] } else { $json }
            $id = if ($msg) { [Int64]$msg.id } else { [Int64]0 }
            $isNew = $id -gt $BaselineId

            [pscustomobject]@{
                Kind       = 'Poll'
                N          = $n
                StartTick  = $startTick
                EndTick    = $endTick
                Id         = $id
                IsNew      = $isNew
                Subject    = if ($msg) { [string]$msg.subject } else { '' }
                ServerDate = if ($msg) { [string]$msg.ins_date } else { '' }
                Cache      = ($r.Headers['X-Cache-Status'] -join ', ')
                Http       = [int]$r.StatusCode
            }

            if ($isNew -and $msg.subject -eq 'Ваш код авторизации') {
                $p = [string]$msg.push_message
                $f = [string]$msg.full_message
                if ($p -match '^\d{4} код авторизации в приложении "Интерсвязь"$' -or
                    $f -match '^\d{4} код авторизации в приложении "Интерсвязь"$') {
                    [pscustomobject]@{
                        Kind         = 'Found'
                        DetectedTick = $endTick
                        Id           = $id
                        Subject      = [string]$msg.subject
                        ServerDate   = [string]$msg.ins_date
                    }
                    return
                }
            }
        }
        catch {
            $endTick = [Diagnostics.Stopwatch]::GetTimestamp()
            [pscustomobject]@{ Kind='Poll'; N=$n; StartTick=$startTick; EndTick=$endTick; Id=0; IsNew=$false; Cache=''; Http=0 }
        }
        # Deliberately no sleep: diagnostic measurement, not production polling.
    }

    [pscustomobject]@{ Kind='Timeout' }
}

Write-Host ''
Write-Host 'Наблюдатель стартует ДО /stepOne; /stepTwo в скрипте отсутствует.' -ForegroundColor Yellow
if ((Read-Host 'Введите TEST') -ne 'TEST') {
    Remove-Variable token -ErrorAction SilentlyContinue
    return
}

$job = Start-ThreadJob -ScriptBlock $watcherScript -ArgumentList @(
    $apiBase, $token, $deviceId, $appVersion, $buildCode, $baselineId
)

Start-Sleep -Milliseconds 200

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)

try {
    $form = [System.Collections.Generic.Dictionary[string,string]]::new()
    $form['phone']        = $portalPhone
    $form['dial_code']    = '7'
    $form['country_code'] = 'ru'
    $form['sendPush']     = 'on'
    $content = [System.Net.Http.FormUrlEncodedContent]::new($form)

    $frequency = [double][Diagnostics.Stopwatch]::Frequency
    $t0Tick = [Diagnostics.Stopwatch]::GetTimestamp()

    Write-Host ''
    Write-Host '=== STEP ONE ===' -ForegroundColor Cyan
    $response = $client.PostAsync("$portalBase/stepOne", $content).GetAwaiter().GetResult()
    $stepEndTick = [Diagnostics.Stopwatch]::GetTimestamp()
    $stepRtt = (($stepEndTick - $t0Tick) * 1000.0 / $frequency)

    $status = [int]$response.StatusCode
    $location = if ($response.Headers.Location) { $response.Headers.Location.ToString() } else { '' }

    Write-Host "HTTP        : $status"
    Write-Host "Location    : $location"
    Write-Host ('stepOne RTT : {0:N1} ms' -f $stepRtt)

    if ($location -notmatch 'stepTwo') {
        Write-Host 'Portal не отправил клиента на stepTwo; тест прекращён.' -ForegroundColor Yellow
        Stop-Job $job -ErrorAction SilentlyContinue
    }
    else {
        $null = Wait-Job $job -Timeout 7
        if ($job.State -eq 'Running') { Stop-Job $job }
    }

    $events = @(Receive-Job $job -ErrorAction SilentlyContinue)
    $polls = @($events | Where-Object Kind -eq 'Poll')
    $found = @($events | Where-Object Kind -eq 'Found') | Select-Object -First 1

    Write-Host ''
    Write-Host '=== PARALLEL TIMELINE ===' -ForegroundColor Cyan
    foreach ($p in $polls) {
        $startMs = (($p.StartTick - $t0Tick) * 1000.0 / $frequency)
        $endMs   = (($p.EndTick   - $t0Tick) * 1000.0 / $frequency)
        $getMs   = (($p.EndTick - $p.StartTick) * 1000.0 / $frequency)
        Write-Host ('#{0,-2} start={1,9:N1} ms  end={2,9:N1} ms  GET={3,6:N1} ms  id={4}  new={5}  cache={6}' -f $p.N,$startMs,$endMs,$getMs,$p.Id,$p.IsNew,$p.Cache)
    }

    Write-Host ''
    if ($found) {
        $detectedMs = (($found.DetectedTick - $t0Tick) * 1000.0 / $frequency)
        Write-Host 'WI-FI CODE MESSAGE FOUND' -ForegroundColor Green
        Write-Host "ID          : $($found.Id)"
        Write-Host "Server date : $($found.ServerDate)"
        Write-Host ('Message seen : {0:N1} ms relative to /stepOne start' -f $detectedMs)
        Write-Host ('stepOne done : {0:N1} ms' -f $stepRtt)
        Write-Host 'Код намеренно не выводится; /stepTwo НЕ выполнялся.'
    }
    else {
        Write-Host 'Новое Wi-Fi-сообщение не поймано.' -ForegroundColor Yellow
    }
}
finally {
    $client.Dispose()
    $handler.Dispose()
    if ($job) {
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
    }
    Remove-Variable token -ErrorAction SilentlyContinue
}
