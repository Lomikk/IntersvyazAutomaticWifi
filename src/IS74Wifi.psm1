#requires -Version 5.1

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Net.Http

# Windows PowerShell 5.1 may otherwise inherit an older TLS default on some systems.
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
} catch {
    # Keep going on systems where the enum differs; HttpClient will use system defaults.
}

$script:StateDir       = Join-Path $env:LOCALAPPDATA 'IS74Wifi'
$script:DeviceFile     = Join-Path $script:StateDir 'device-id.txt'
$script:SecretsFile    = Join-Path $script:StateDir 'secrets.dpapi'
$script:SessionFile    = Join-Path $script:StateDir 'session-meta.json'
$script:RuntimeFile    = Join-Path $script:StateDir 'runtime-state.json'
$script:SettingsFile   = Join-Path $script:StateDir 'settings.json'
$script:DeviceMetaFile = Join-Path $script:StateDir 'device-metadata.json'
$script:LogDir         = Join-Path $script:StateDir 'logs'
$script:LogFile        = Join-Path $script:LogDir 'diagnostic.log'
$script:LogMaxBytes    = 1MB
$script:LogRetentionFiles = 5
$script:LogPreviewChars = 1024
$script:TaskName       = 'IS74WifiAgent'
$script:ProjectRoot    = Split-Path -Parent $PSScriptRoot
$script:ModulePath      = $PSCommandPath
$script:CliScriptPath  = Join-Path $script:ProjectRoot 'IS74Wifi.ps1'
$script:ApiBase        = 'https://api.is74.ru'
$script:PortalBase     = 'http://w.is74.ru'
$script:AppVersion     = '2.18.0-RS-95aa9b78'
$script:BuildCode      = 2026061111
$script:PollScheduleMs = @(100, 150, 200, 250, 350, 500, 700, 1000, 1400, 2000, 3000, 4500, 6500, 10000)

function Initialize-IS74Storage {
    if (-not (Test-Path $script:StateDir)) {
        New-Item -ItemType Directory -Force -Path $script:StateDir | Out-Null
    }
    if (-not (Test-Path $script:LogDir)) {
        New-Item -ItemType Directory -Force -Path $script:LogDir | Out-Null
    }
    if (-not (Test-Path $script:SettingsFile)) {
        [ordered]@{
            authWindowHours = 24
            agentPollSeconds = 15
            guardWindowSeconds = 10
            guardProbeIntervalMilliseconds = 250
            internetProbeConfirmDelaySeconds = 2
            maxAutomaticStepOneAttempts = 4
            automaticRetryDelaysSeconds = @(15, 30, 60)
        } | ConvertTo-Json | Set-Content -Path $script:SettingsFile -Encoding UTF8
    }
}

function Invoke-IS74LogRotation {
    try {
        if (-not (Test-Path $script:LogFile)) { return }
        $item = Get-Item -Path $script:LogFile -ErrorAction Stop
        if ($item.Length -lt $script:LogMaxBytes) { return }

        $maxBackup = [Math]::Max(1, [int]$script:LogRetentionFiles - 1)
        $oldest = Join-Path $script:LogDir ("diagnostic.{0}.log" -f $maxBackup)
        if (Test-Path $oldest) { Remove-Item -Path $oldest -Force -ErrorAction SilentlyContinue }

        for ($i = $maxBackup - 1; $i -ge 1; $i--) {
            $source = Join-Path $script:LogDir ("diagnostic.{0}.log" -f $i)
            $target = Join-Path $script:LogDir ("diagnostic.{0}.log" -f ($i + 1))
            if (Test-Path $source) { Move-Item -Path $source -Destination $target -Force }
        }

        Move-Item -Path $script:LogFile -Destination (Join-Path $script:LogDir 'diagnostic.1.log') -Force
    } catch {
        # Rotation failures must never affect authorization.
    }
}

function Protect-IS74LogText {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) { return '' }
    $value = $Text

    # Never persist reusable credentials, one-time codes or phone numbers.
    $value = [regex]::Replace($value, '(?i)(Authorization\s*[:=]\s*Bearer\s+)[A-Za-z0-9._~+/=-]+', '$1<redacted>')
    $value = [regex]::Replace($value, '(?i)("(?:TOKEN|token|bearer|access_token|authId|confirmCode|phone|AUTHORIZE_PHONE)"\s*:\s*")[^"]*(")', '$1<redacted>$2')
    $value = [regex]::Replace($value, '(?i)([?&](?:phone|confirmCode|authId)=)[^&\s]+', '$1<redacted>')
    $value = [regex]::Replace($value, '(?i)((?:phone|confirmCode|authId)\s*=\s*)[^&\s]+', '$1<redacted>')
    $value = [regex]::Replace($value, '\b\d{4}(?=\s+код авторизации)', '<redacted-code>')
    $value = [regex]::Replace($value, '(?<!\d)(?:\+?7|8)\d{10}(?!\d)', '<redacted-phone>')
    $value = $value -replace '[\r\n]+', ' '
    return $value
}

function Write-IS74Log {
    param(
        [Parameter(Mandatory=$true)][string]$Message,
        [ValidateSet('INFO','WARN','ERROR')][string]$Level = 'INFO'
    )

    try {
        Initialize-IS74Storage
        Invoke-IS74LogRotation
        $safeMessage = Protect-IS74LogText -Text $Message
        $line = '{0} [{1}] [pid={2}] {3}' -f ([DateTime]::UtcNow.ToString('o')), $Level, $PID, $safeMessage
        Add-Content -Path $script:LogFile -Value $line -Encoding UTF8
    } catch {
        # Logging must never break authorization.
    }
}

function Write-IS74HttpLog {
    param(
        [Parameter(Mandatory=$true)][string]$Operation,
        [Parameter(Mandatory=$true)][string]$Method,
        [Parameter(Mandatory=$true)][string]$Uri,
        [object]$StatusCode = $null,
        [string]$Location,
        [string]$DateUtc,
        [object]$ElapsedMs = $null,
        [string]$Body,
        [string]$ErrorMessage
    )

    $parts = New-Object 'System.Collections.Generic.List[string]'
    $parts.Add(('HTTP op={0}' -f (Protect-IS74LogText -Text $Operation)))
    $parts.Add(('method={0}' -f $Method))
    $parts.Add(('uri={0}' -f (Protect-IS74LogText -Text $Uri)))
    if ($null -ne $StatusCode) { $parts.Add(('status={0}' -f [int]$StatusCode)) }
    if ($null -ne $ElapsedMs) { $parts.Add(('elapsedMs={0}' -f [int]$ElapsedMs)) }
    if ($DateUtc) { $parts.Add(('serverDate={0}' -f (Protect-IS74LogText -Text $DateUtc))) }
    if ($Location) { $parts.Add(('location={0}' -f (Protect-IS74LogText -Text $Location))) }
    if ($ErrorMessage) { $parts.Add(('error={0}' -f (Protect-IS74LogText -Text $ErrorMessage))) }
    if ($Body) {
        $preview = Protect-IS74LogText -Text $Body
        if ($preview.Length -gt $script:LogPreviewChars) {
            $preview = $preview.Substring(0, $script:LogPreviewChars) + '<truncated>'
        }
        $parts.Add(('body={0}' -f $preview))
    }
    $level = if ($ErrorMessage) { 'WARN' } else { 'INFO' }
    Write-IS74Log -Level $level -Message ($parts -join ' ')
}

function Get-IS74DiagnosticLogPath {
    Initialize-IS74Storage
    return $script:LogFile
}

function Write-IS74RuntimeEvent {
    param(
        [Parameter(Mandatory=$true)][string]$Message,
        [ValidateSet('INFO','WARN','ERROR')][string]$Level = 'INFO'
    )
    Write-IS74Log -Message $Message -Level $Level
}

function Read-IS74JsonFile {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    try {
        return (Get-Content -Path $Path -Raw | ConvertFrom-Json)
    } catch {
        Write-IS74Log -Level WARN -Message "Не удалось прочитать JSON: $Path"
        return $null
    }
}

function Write-IS74JsonFile {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)]$Value
    )
    $Value | ConvertTo-Json -Depth 8 | Set-Content -Path $Path -Encoding UTF8
}

function Get-IS74Settings {
    Initialize-IS74Storage
    $defaults = [ordered]@{
        authWindowHours = 24
        agentPollSeconds = 15
        guardWindowSeconds = 10
        guardProbeIntervalMilliseconds = 250
        internetProbeConfirmDelaySeconds = 2
        maxAutomaticStepOneAttempts = 4
        automaticRetryDelaysSeconds = @(15, 30, 60)
    }

    $settings = Read-IS74JsonFile -Path $script:SettingsFile
    if (-not $settings) { return [pscustomobject]$defaults }

    foreach ($name in @($defaults.Keys)) {
        if (-not ($settings.PSObject.Properties.Name -contains $name)) {
            Add-Member -InputObject $settings -MemberType NoteProperty -Name $name -Value $defaults[$name]
        }
    }
    return $settings
}

function Protect-IS74Text {
    param([Parameter(Mandatory=$true)][string]$PlainText)
    $secure = ConvertTo-SecureString -String $PlainText -AsPlainText -Force
    return (ConvertFrom-SecureString -SecureString $secure)
}

function Unprotect-IS74Text {
    param([Parameter(Mandatory=$true)][string]$CipherText)

    $secure = ConvertTo-SecureString -String $CipherText
    $ptr = [IntPtr]::Zero
    try {
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    } finally {
        if ($ptr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        }
    }
}

function Save-IS74Secrets {
    param(
        [Parameter(Mandatory=$true)][string]$Token,
        [Parameter(Mandatory=$true)][string]$Phone
    )

    Initialize-IS74Storage
    $plain = ([ordered]@{ token = $Token; phone = $Phone } | ConvertTo-Json -Compress)
    $cipher = Protect-IS74Text -PlainText $plain
    Set-Content -Path $script:SecretsFile -Value $cipher -Encoding ASCII -NoNewline
}

function Get-IS74Secrets {
    if (-not (Test-Path $script:SecretsFile)) { return $null }
    try {
        $cipher = (Get-Content -Path $script:SecretsFile -Raw).Trim()
        $plain = Unprotect-IS74Text -CipherText $cipher
        return ($plain | ConvertFrom-Json)
    } catch {
        Write-IS74Log -Level ERROR -Message 'Не удалось расшифровать локальные секреты DPAPI.'
        return $null
    }
}

function New-IS74DeviceId {
    Initialize-IS74Storage
    if (Test-Path $script:DeviceFile) {
        $existing = (Get-Content -Path $script:DeviceFile -Raw).Trim()
        if ($existing) { return $existing }
    }

    $bytes = New-Object byte[] 8
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    } finally {
        $rng.Dispose()
    }
    $deviceId = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    Set-Content -Path $script:DeviceFile -Value $deviceId -Encoding ASCII -NoNewline
    return $deviceId
}

function Get-IS74DeviceId {
    if (-not (Test-Path $script:DeviceFile)) { return $null }
    return (Get-Content -Path $script:DeviceFile -Raw).Trim()
}

function Normalize-IS74Phone {
    param([Parameter(Mandatory=$true)][string]$Phone)
    $normalized = $Phone -replace '\D',''
    if ($normalized.Length -eq 11 -and ($normalized[0] -eq '7' -or $normalized[0] -eq '8')) {
        $normalized = $normalized.Substring(1)
    }
    if ($normalized.Length -ne 10) {
        throw 'После нормализации номер телефона должен содержать 10 цифр.'
    }
    return $normalized
}

function New-IS74HttpClientPair {
    param(
        [switch]$NoRedirect,
        [int]$TimeoutSeconds = 15,
        [int]$MaxConnections = 16
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    if ($NoRedirect) { $handler.AllowAutoRedirect = $false }
    # The captive network previously blocked CRL/OCSP reachability. Keep normal
    # certificate/name validation, but do not make online revocation lookup a
    # prerequisite for reaching the app API through the walled garden.
    try { $handler.CheckCertificateRevocationList = $false } catch { }
    try { $handler.MaxConnectionsPerServer = $MaxConnections } catch { }
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    return [pscustomobject]@{ Handler = $handler; Client = $client }
}

function Add-IS74ApiHeaders {
    param(
        [Parameter(Mandatory=$true)][System.Net.Http.HttpRequestMessage]$Request,
        [Parameter(Mandatory=$true)][string]$DeviceId,
        [string]$Token,
        [switch]$NoCache,
        [string]$UserId,
        [string]$ProfileId
    )

    $null = $Request.Headers.TryAddWithoutValidation('Accept', 'application/json; version=v2')
    $null = $Request.Headers.TryAddWithoutValidation('X-Device-Id', $DeviceId)
    $null = $Request.Headers.TryAddWithoutValidation('X-Api-Source', 'com.intersvyaz.lk')
    $null = $Request.Headers.TryAddWithoutValidation('X-App-version', $script:AppVersion)
    $null = $Request.Headers.TryAddWithoutValidation('Platform', 'Android')
    $null = $Request.Headers.TryAddWithoutValidation('User-Agent', "4.11.0 com.intersvyaz.lk/$($script:AppVersion).$($script:BuildCode)")
    if ($Token) { $null = $Request.Headers.TryAddWithoutValidation('Authorization', "Bearer $Token") }
    if ($NoCache) { $null = $Request.Headers.TryAddWithoutValidation('Cache-Control', 'no-cache') }
    if ($UserId) { $null = $Request.Headers.TryAddWithoutValidation('X-Api-User-Id', $UserId) }
    if ($ProfileId) { $null = $Request.Headers.TryAddWithoutValidation('X-Api-Profile-Id', $ProfileId) }
}

function Send-IS74Request {
    param(
        [Parameter(Mandatory=$true)][System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory=$true)][System.Net.Http.HttpRequestMessage]$Request,
        [string]$DiagnosticOperation = 'http'
    )

    $response = $null
    $method = [string]$Request.Method.Method
    $uri = [string]$Request.RequestUri.AbsoluteUri
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = $Client.SendAsync($Request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $location = ''
        if ($response.Headers.Location) { $location = $response.Headers.Location.ToString() }
        $dateUtc = $null
        if ($response.Headers.Date.HasValue) {
            $dateUtc = $response.Headers.Date.Value.UtcDateTime.ToString('o')
        }
        Write-IS74HttpLog -Operation $DiagnosticOperation -Method $method -Uri $uri -StatusCode ([int]$response.StatusCode) -Location $location -DateUtc $dateUtc -ElapsedMs ([int]$clock.Elapsed.TotalMilliseconds)
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            IsSuccess = $response.IsSuccessStatusCode
            Body = $body
            Location = $location
            DateUtc = $dateUtc
        }
    } catch {
        Write-IS74HttpLog -Operation $DiagnosticOperation -Method $method -Uri $uri -ElapsedMs ([int]$clock.Elapsed.TotalMilliseconds) -ErrorMessage $_.Exception.Message
        throw
    } finally {
        if ($response) { $response.Dispose() }
        $Request.Dispose()
    }
}

function Get-IS74WindowsDescription {
    try {
        $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $buildNumber = [int]$cv.CurrentBuildNumber
        $ubr = [string]$cv.UBR
        $displayVersion = [string]$cv.DisplayVersion
        $editionId = [string]$cv.EditionID
        $windowsName = if ($buildNumber -ge 22000) { 'Windows 11' } else { 'Windows 10' }
        $edition = switch -Regex ($editionId) {
            '^Professional' { 'Pro'; break }
            '^Enterprise'   { 'Enterprise'; break }
            '^Education'    { 'Education'; break }
            '^Core'         { 'Home'; break }
            default         { $editionId }
        }
        $fullBuild = if ($ubr) { "$buildNumber.$ubr" } else { [string]$buildNumber }
        $result = $windowsName
        if ($edition) { $result += " $edition" }
        if ($displayVersion) { $result += " $displayVersion" }
        $result += " (build $fullBuild)"
        return $result
    } catch {
        return [Environment]::OSVersion.VersionString
    }
}

function Register-IS74DeviceMetadata {
    param(
        [Parameter(Mandatory=$true)][string]$Token,
        [Parameter(Mandatory=$true)][string]$Phone,
        [Parameter(Mandatory=$true)][string]$DeviceId
    )

    $pair = New-IS74HttpClientPair
    try {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, "$($script:ApiBase)/mobile/pushtoken/add-with-device-id")
        Add-IS74ApiHeaders -Request $request -DeviceId $DeviceId -Token $Token

        $osName = Get-IS74WindowsDescription
        $deviceName = $env:COMPUTERNAME
        $deviceInfo = [ordered]@{
            TYPE             = 5
            UNIQUE_DEVICE_ID = $DeviceId
            AUTHORIZE_PHONE  = $Phone
            VERS_NAME        = $script:AppVersion
            ASSEMBLY_CODE    = $script:BuildCode
            OS_VERS          = $osName
            DEVICE_MODEL     = $deviceName
        }
        $request.Content = [System.Net.Http.StringContent]::new(($deviceInfo | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
        $result = Send-IS74Request -Client $pair.Client -Request $request -DiagnosticOperation 'device-metadata'
        if ($result.StatusCode -lt 200 -or $result.StatusCode -ge 300) {
            Write-IS74Log -Level WARN -Message "Device metadata HTTP $($result.StatusCode)."
            return $false
        }

        Write-IS74JsonFile -Path $script:DeviceMetaFile -Value ([ordered]@{
            deviceId = $DeviceId
            deviceModel = $deviceName
            osVersion = $osName
            registeredAt = (Get-Date).ToString('o')
        })
        return $true
    } catch {
        Write-IS74Log -Level WARN -Message "Не удалось отправить метаданные устройства: $($_.Exception.Message)"
        return $false
    } finally {
        $pair.Client.Dispose()
        $pair.Handler.Dispose()
    }
}

function Register-IS74Account {
    Initialize-IS74Storage
    if (Get-IS74Secrets) {
        Write-Host 'Устройство уже зарегистрировано. Для новой регистрации сначала выполните reset.' -ForegroundColor Green
        return $true
    }
    $deviceId = New-IS74DeviceId
    $phone = Normalize-IS74Phone -Phone (Read-Host 'Введите номер телефона')


    $pair = New-IS74HttpClientPair
    try {
        Write-Host 'Запрашиваю код подтверждения...' -ForegroundColor Cyan
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$($script:ApiBase)/mobile/auth/get-confirm")
        Add-IS74ApiHeaders -Request $request -DeviceId $deviceId
        $payload = [ordered]@{ phone = $phone; deviceId = $deviceId; authType = 0 } | ConvertTo-Json -Compress
        $request.Content = [System.Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, 'application/json')
        $getConfirm = Send-IS74Request -Client $pair.Client -Request $request -DiagnosticOperation 'auth.get-confirm'
        if ($getConfirm.StatusCode -lt 200 -or $getConfirm.StatusCode -ge 300) {
            throw "get-confirm вернул HTTP $($getConfirm.StatusCode)."
        }

        $smsCode = (Read-Host 'Введите SMS-код').Trim()
        if ($smsCode -notmatch '^\d+$') { throw 'SMS-код должен состоять из цифр.' }

        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$($script:ApiBase)/mobile/auth/check-confirm")
        Add-IS74ApiHeaders -Request $request -DeviceId $deviceId
        $form = [System.Collections.Generic.Dictionary[string,string]]::new()
        $form['phone'] = $phone
        $form['confirmCode'] = $smsCode
        $form['authId'] = ''
        $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($form)
        $check = Send-IS74Request -Client $pair.Client -Request $request -DiagnosticOperation 'auth.check-confirm'
        if ($check.StatusCode -lt 200 -or $check.StatusCode -ge 300) {
            throw "check-confirm вернул HTTP $($check.StatusCode)."
        }

        $checkJson = $check.Body | ConvertFrom-Json
        $addressesCount = if ($checkJson.addresses) { @($checkJson.addresses).Count } else { 0 }
        Write-IS74Log -Message ("auth.check-confirm parsed authIdPresent={0} addressesCount={1}" -f [bool]$checkJson.authId, $addressesCount)
        if (-not $checkJson.authId) { throw 'check-confirm не вернул подтверждённый authId.' }
        if ($checkJson.addresses -and @($checkJson.addresses).Count -gt 0) {
            throw 'Сервер вернул несколько связанных адресов. Этот MVP пока не выбирает userId автоматически; регистрация остановлена без сохранения Bearer.'
        }

        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$($script:ApiBase)/mobile/auth/get-token")
        Add-IS74ApiHeaders -Request $request -DeviceId $deviceId -UserId '-1' -ProfileId 'null'
        $form = [System.Collections.Generic.Dictionary[string,string]]::new()
        $form['authId'] = [string]$checkJson.authId
        $form['userId'] = ''
        $form['uniqueDeviceId'] = $deviceId
        $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($form)
        $tokenResult = Send-IS74Request -Client $pair.Client -Request $request -DiagnosticOperation 'auth.get-token'
        if ($tokenResult.StatusCode -lt 200 -or $tokenResult.StatusCode -ge 300) {
            throw "get-token вернул HTTP $($tokenResult.StatusCode)."
        }

        $session = $tokenResult.Body | ConvertFrom-Json
        Write-IS74Log -Message ("auth.get-token parsed tokenPresent={0} accessBegin={1} accessEnd={2}" -f [bool]$session.TOKEN, [string]$session.ACCESS_BEGIN, [string]$session.ACCESS_END)
        if (-not $session.TOKEN) { throw 'get-token вернул HTTP 2xx, но TOKEN отсутствует.' }

        Save-IS74Secrets -Token ([string]$session.TOKEN) -Phone $phone
        Write-IS74JsonFile -Path $script:SessionFile -Value ([ordered]@{
            deviceId = $deviceId
            userId = $session.USER_ID
            profileId = $session.PROFILE_ID
            accessBegin = $session.ACCESS_BEGIN
            accessEnd = $session.ACCESS_END
            registeredAt = (Get-Date).ToString('o')
        })

        # Metadata is best-effort and must not invalidate an otherwise successful registration.
        $null = Register-IS74DeviceMetadata -Token ([string]$session.TOKEN) -Phone $phone -DeviceId $deviceId
        Write-IS74Log -Message 'Регистрация аккаунта завершена.'
        Write-Host 'Регистрация завершена. Bearer и номер сохранены через DPAPI текущего пользователя.' -ForegroundColor Green
        if ($session.ACCESS_END) { Write-Host "Сессия API действует до: $($session.ACCESS_END)" }
        return $true
    } finally {
        $pair.Client.Dispose()
        $pair.Handler.Dispose()
    }
}

function Get-IS74WindowsPowerShellPath {
    $path = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path $path) { return $path }
    $cmd = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { return [string]$cmd.Source }
    throw 'Не найден Windows PowerShell 5.1 (powershell.exe).'
}

function Test-IS74InternetAccess {
    param([switch]$DiagnosticOnFailure)

    $pair = New-IS74HttpClientPair -NoRedirect -TimeoutSeconds 4 -MaxConnections 2
    $request = $null
    $response = $null
    $uri = 'http://www.msftconnecttest.com/connecttest.txt'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $uri)
        $null = $request.Headers.TryAddWithoutValidation('Cache-Control', 'no-cache, no-store')
        $null = $request.Headers.TryAddWithoutValidation('Pragma', 'no-cache')
        $response = $pair.Client.SendAsync($request).GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $ok = ($status -eq 200 -and $body.Trim() -eq 'Microsoft Connect Test')
        if (-not $ok -and $DiagnosticOnFailure) {
            Write-IS74HttpLog -Operation 'internet.probe' -Method 'GET' -Uri $uri -StatusCode $status -ElapsedMs ([int]$clock.Elapsed.TotalMilliseconds) -Body $body
        }
        return $ok
    } catch {
        if ($DiagnosticOnFailure) {
            Write-IS74HttpLog -Operation 'internet.probe' -Method 'GET' -Uri $uri -ElapsedMs ([int]$clock.Elapsed.TotalMilliseconds) -ErrorMessage $_.Exception.Message
        }
        return $false
    } finally {
        if ($response) { $response.Dispose() }
        if ($request) { $request.Dispose() }
        $pair.Client.Dispose()
        $pair.Handler.Dispose()
    }
}

function Test-IS74WifiConnected {
    try {
        $lines = & netsh.exe wlan show interfaces 2>$null
        foreach ($line in $lines) {
            if ($line -match '^\s*SSID\s*:\s*(.+?)\s*$') {
                if ($Matches[1] -and $Matches[1] -notmatch '^N/A$') { return $true }
            }
        }
    } catch { }
    return $false
}

function New-IS74PushRequest {
    param(
        [Parameter(Mandatory=$true)][string]$Token,
        [Parameter(Mandatory=$true)][string]$DeviceId,
        [int]$PageSize = 1
    )
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$($script:ApiBase)/mobile/pushmessages?page=1&pageSize=$PageSize")
    Add-IS74ApiHeaders -Request $request -DeviceId $DeviceId -Token $Token -NoCache
    return $request
}

function Get-IS74TopMessage {
    param($Json)
    if ($null -eq $Json) { return $null }
    if ($Json -is [array]) { return @($Json)[0] }
    if ($Json.items) { return @($Json.items)[0] }
    if ($Json.data -is [array]) { return @($Json.data)[0] }
    return $Json
}

function Get-IS74WifiCodeFromMessage {
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

function Get-IS74BaselineId {
    param(
        [Parameter(Mandatory=$true)][System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory=$true)][string]$Token,
        [Parameter(Mandatory=$true)][string]$DeviceId
    )
    $request = New-IS74PushRequest -Token $Token -DeviceId $DeviceId -PageSize 1
    $result = Send-IS74Request -Client $Client -Request $request -DiagnosticOperation 'push.baseline'
    if ($result.StatusCode -eq 401) { throw 'Bearer отклонён сервером (HTTP 401). Выполните регистрацию заново.' }
    if (-not $result.IsSuccess) { throw "Baseline pushmessages HTTP $($result.StatusCode)." }
    $json = $result.Body | ConvertFrom-Json
    $msg = Get-IS74TopMessage -Json $json
    if ($msg -and $msg.id) {
        Write-IS74Log -Message ("push.baseline topId={0}" -f [Int64]$msg.id)
        return [Int64]$msg.id
    }
    Write-IS74Log -Message 'push.baseline empty topId=0'
    return [Int64]0
}

function Find-IS74WifiCodeAfterBaseline {
    param(
        [Parameter(Mandatory=$true)][System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory=$true)][string]$Token,
        [Parameter(Mandatory=$true)][string]$DeviceId,
        [Parameter(Mandatory=$true)][Int64]$BaselineId
    )
    $request = New-IS74PushRequest -Token $Token -DeviceId $DeviceId -PageSize 5
    $result = Send-IS74Request -Client $Client -Request $request -DiagnosticOperation 'push.fallback'
    if (-not $result.IsSuccess) { return $null }
    $json = $result.Body | ConvertFrom-Json
    $messages = @()
    if ($json -is [array]) { $messages = @($json) }
    elseif ($json.items) { $messages = @($json.items) }
    elseif ($json.data -is [array]) { $messages = @($json.data) }
    else { $messages = @($json) }

    foreach ($msg in $messages) {
        if ($msg.id -and [Int64]$msg.id -gt $BaselineId) {
            $code = Get-IS74WifiCodeFromMessage -Message $msg
            if ($code) {
                Write-IS74Log -Message ("push.fallback codeFound=true messageId={0}" -f [Int64]$msg.id)
                return [pscustomobject]@{ Code = $code; Id = [Int64]$msg.id }
            }
        }
    }
    return $null
}

function Read-IS74RuntimeState {
    $defaults = [ordered]@{
        lastAuthUtc = $null
        expectedExpiryUtc = $null
        lastAttemptUtc = $null
        lastAttemptReason = $null
        lastResult = $null
        internetConfirmed = $null
        automaticStepOneAttempts = 0
        nextAutomaticRetryUtc = $null
        userActionRequired = $false
        edgeWatchActive = $false
    }

    $saved = Read-IS74JsonFile -Path $script:RuntimeFile
    if ($saved) {
        foreach ($name in @($defaults.Keys)) {
            if ($saved.PSObject.Properties.Name -contains $name) {
                $defaults[$name] = $saved.$name
            }
        }
    }
    return [pscustomobject]$defaults
}

function Save-IS74RuntimeState {
    param([Parameter(Mandatory=$true)]$State)
    Write-IS74JsonFile -Path $script:RuntimeFile -Value $State
}

function Clear-IS74AutomaticRetryState {
    $state = Read-IS74RuntimeState
    $state.automaticStepOneAttempts = 0
    $state.nextAutomaticRetryUtc = $null
    $state.userActionRequired = $false
    $state.edgeWatchActive = $false
    Save-IS74RuntimeState -State $state
}

function Set-IS74SuccessfulAuthState {
    param(
        [bool]$InternetConfirmed,
        [string]$AuthorizedAtUtc
    )
    $settings = Get-IS74Settings
    $authorizedAt = [DateTime]::UtcNow
    if ($AuthorizedAtUtc) {
        try { $authorizedAt = [DateTime]::Parse($AuthorizedAtUtc).ToUniversalTime() } catch { }
    }
    $state = [ordered]@{
        lastAuthUtc = $authorizedAt.ToString('o')
        expectedExpiryUtc = $authorizedAt.AddHours([double]$settings.authWindowHours).ToString('o')
        lastAttemptUtc = [DateTime]::UtcNow.ToString('o')
        lastAttemptReason = 'success'
        lastResult = 'success'
        internetConfirmed = $InternetConfirmed
        automaticStepOneAttempts = 0
        nextAutomaticRetryUtc = $null
        userActionRequired = $false
        edgeWatchActive = $false
    }
    Save-IS74RuntimeState -State $state
    Write-IS74Log -Message ("auth.state success authorizedAtUtc={0} expectedExpiryUtc={1} internetConfirmed={2}" -f $state.lastAuthUtc, $state.expectedExpiryUtc, $InternetConfirmed)
}

function Set-IS74AttemptState {
    param(
        [Parameter(Mandatory=$true)][string]$Result,
        [string]$Reason
    )
    $state = Read-IS74RuntimeState
    $state.lastAttemptUtc = [DateTime]::UtcNow.ToString('o')
    if ($Reason) { $state.lastAttemptReason = $Reason }
    $state.lastResult = $Result
    Save-IS74RuntimeState -State $state
}

function Register-IS74AutomaticStepOneSend {
    param([Parameter(Mandatory=$true)][string]$Reason)
    $settings = Get-IS74Settings
    $state = Read-IS74RuntimeState
    if ([bool]$state.userActionRequired) {
        throw 'Автоматические попытки остановлены до действия пользователя.'
    }
    $count = [int]$state.automaticStepOneAttempts
    if ($count -ge [int]$settings.maxAutomaticStepOneAttempts) {
        $state.userActionRequired = $true
        Save-IS74RuntimeState -State $state
        throw 'Достигнут лимит автоматических отправок stepOne.'
    }
    $count++
    $state.automaticStepOneAttempts = $count
    $state.lastAttemptUtc = [DateTime]::UtcNow.ToString('o')
    $state.lastAttemptReason = $Reason
    $state.lastResult = 'step-one-sent'
    Save-IS74RuntimeState -State $state
    Write-IS74Log -Message ("portal.stepOne send attempt={0} reason={1}" -f $count, $Reason)
    return $count
}

function Set-IS74AutomaticRetry {
    param([int]$DelaySeconds)
    $state = Read-IS74RuntimeState
    $state.nextAutomaticRetryUtc = [DateTime]::UtcNow.AddSeconds($DelaySeconds).ToString('o')
    Save-IS74RuntimeState -State $state
}

function Set-IS74UserActionRequired {
    param([string]$Result = 'user-action-required')
    $state = Read-IS74RuntimeState
    $state.userActionRequired = $true
    $state.nextAutomaticRetryUtc = $null
    $state.lastResult = $Result
    Save-IS74RuntimeState -State $state
    Write-IS74Log -Level WARN -Message ("automatic.state userActionRequired=true result={0}" -f $Result)
}

function New-IS74ConnectException {
    param(
        [Parameter(Mandatory=$true)][string]$Message,
        [switch]$RetryableStepOne,
        [switch]$UserActionRequired
    )
    $ex = [System.InvalidOperationException]::new($Message)
    if ($RetryableStepOne) { $ex.Data['IS74RetryableStepOne'] = $true }
    if ($UserActionRequired) { $ex.Data['IS74UserActionRequired'] = $true }
    return $ex
}

function Test-IS74InternetUnavailableConfirmed {
    param([int]$DelayMilliseconds = -1)

    $settings = Get-IS74Settings
    if (Test-IS74InternetAccess -DiagnosticOnFailure) { return $false }
    Write-IS74Log -Level WARN -Message 'internet.probe firstFailure=true; confirming availability loss.'

    if ($DelayMilliseconds -lt 0) {
        $DelayMilliseconds = [int]([double]$settings.internetProbeConfirmDelaySeconds * 1000)
    }
    if ($DelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $DelayMilliseconds }
    $secondFailed = -not (Test-IS74InternetAccess -DiagnosticOnFailure)
    if ($secondFailed) {
        Write-IS74Log -Level WARN -Message 'internet.probe unavailableConfirmed=true.'
        return $true
    }

    Write-IS74Log -Message 'internet.probe transientFailure=true; confirmation succeeded.'
    return $false
}

function Connect-IS74Wifi {
    param(
        [switch]$Force,
        [switch]$Quiet,
        [ValidateSet('manual','automatic','retry')][string]$AttemptReason = 'manual'
    )

    Initialize-IS74Storage
    $secrets = Get-IS74Secrets
    $deviceId = Get-IS74DeviceId
    if (-not $secrets -or -not $secrets.token -or -not $secrets.phone -or -not $deviceId) {
        throw 'Устройство не зарегистрировано. Сначала выполните register.'
    }

    if ($AttemptReason -eq 'manual') {
        # Explicit user action unlocks a stopped automatic cycle, but never changes
        # the 24-hour timer. Only successful stepTwo may do that.
        Clear-IS74AutomaticRetryState
    }

    if (-not $Force -and (Test-IS74InternetAccess)) {
        if (-not $Quiet) { Write-Host 'Интернет уже доступен; авторизация не требуется.' -ForegroundColor Green }
        return [pscustomobject]@{ Status='AlreadyOnline'; InternetConfirmed=$true }
    }

    Set-IS74AttemptState -Result 'started' -Reason $AttemptReason
    Write-IS74Log -Message "Начата Wi-Fi авторизация. Reason=$AttemptReason"
    if (-not $Quiet) { Write-Host 'Начинаю Wi-Fi авторизацию...' -ForegroundColor Cyan }

    $apiPair = New-IS74HttpClientPair -TimeoutSeconds 15 -MaxConnections 16
    $portalPair = New-IS74HttpClientPair -NoRedirect -TimeoutSeconds 15 -MaxConnections 4
    $polls = New-Object System.Collections.ArrayList
    $stepRequest = $null
    $stepResponse = $null

    try {
        $token = [string]$secrets.token
        $phone = [string]$secrets.phone
        $baselineId = Get-IS74BaselineId -Client $apiPair.Client -Token $token -DeviceId $deviceId

        if ($AttemptReason -ne 'manual') {
            $null = Register-IS74AutomaticStepOneSend -Reason $AttemptReason
        }

        $stepRequest = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$($script:PortalBase)/stepOne")
        $form = [System.Collections.Generic.Dictionary[string,string]]::new()
        $form['phone'] = "8$phone"
        $form['dial_code'] = '7'
        $form['country_code'] = 'ru'
        $form['sendPush'] = 'on'
        $stepRequest.Content = [System.Net.Http.FormUrlEncodedContent]::new($form)

        $clock = [Diagnostics.Stopwatch]::StartNew()
        $stepTask = $portalPair.Client.SendAsync($stepRequest, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead)
        $stepException = $null
        $stepResponseElapsedMs = $null
        $nextIndex = 0
        $found = $null
        $fallbackUsed = $false

        while ($clock.Elapsed.TotalMilliseconds -lt 12000 -and -not $found) {
            $nowMs = $clock.Elapsed.TotalMilliseconds
            while ($nextIndex -lt $script:PollScheduleMs.Count -and $nowMs -ge $script:PollScheduleMs[$nextIndex] -and -not $found) {
                $request = New-IS74PushRequest -Token $token -DeviceId $deviceId -PageSize 1
                $task = $apiPair.Client.SendAsync($request)
                $poll = [pscustomobject]@{
                    Request = $request
                    Task = $task
                    Processed = $false
                    TargetMs = $script:PollScheduleMs[$nextIndex]
                }
                $null = $polls.Add($poll)
                $nextIndex++
                $nowMs = $clock.Elapsed.TotalMilliseconds
            }

            foreach ($poll in @($polls)) {
                if ($poll.Processed -or -not $poll.Task.IsCompleted) { continue }
                $poll.Processed = $true
                $response = $null
                try {
                    $response = $poll.Task.GetAwaiter().GetResult()
                    if (-not $response.IsSuccessStatusCode) { continue }
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    $json = $body | ConvertFrom-Json
                    $msg = Get-IS74TopMessage -Json $json
                    if (-not $msg -or -not $msg.id) { continue }
                    $id = [Int64]$msg.id
                    if ($id -le $baselineId) { continue }
                    $code = Get-IS74WifiCodeFromMessage -Message $msg
                    if ($code) {
                        $found = [pscustomobject]@{ Code = $code; Id = $id }
                        Write-IS74Log -Message ("push.poll codeFound=true messageId={0} targetMs={1} observedMs={2}" -f $id, $poll.TargetMs, [int]$clock.Elapsed.TotalMilliseconds)
                        break
                    }
                    if (-not $fallbackUsed) {
                        $fallbackUsed = $true
                        $candidate = Find-IS74WifiCodeAfterBaseline -Client $apiPair.Client -Token $token -DeviceId $deviceId -BaselineId $baselineId
                        if ($candidate) {
                            $found = $candidate
                            Write-IS74Log -Message ("push.poll fallbackFound=true messageId={0} observedMs={1}" -f $candidate.Id, [int]$clock.Elapsed.TotalMilliseconds)
                            break
                        }
                    }
                } catch {
                    Write-IS74Log -Level WARN -Message "Polling GET error: $($_.Exception.Message)"
                } finally {
                    if ($response) { $response.Dispose() }
                }
            }

            if ($found) { break }

            # Observe stepOne completion concurrently with push polling. An
            # already-authorized landing redirect must be handled immediately;
            # waiting out the 10-second code schedule would create avoidable
            # downtime exactly at the 24-hour edge. A normal stepTwo redirect
            # does not stop polling because its code may arrive a little later.
            if (-not $stepResponse -and -not $stepException -and $stepTask.IsCompleted) {
                try {
                    $stepResponse = $stepTask.GetAwaiter().GetResult()
                    $stepResponseElapsedMs = [int]$clock.Elapsed.TotalMilliseconds
                    $earlyStatus = [int]$stepResponse.StatusCode
                    $earlyLocation = if ($stepResponse.Headers.Location) { $stepResponse.Headers.Location.ToString() } else { '' }
                    if ($earlyStatus -ge 300 -and $earlyStatus -lt 400 -and $earlyLocation -notmatch '(^|/)stepTwo\?') {
                        break
                    }
                } catch {
                    # Keep polling: the request may have reached the backend even
                    # when the client did not receive its HTTP response. A fresh
                    # code is stronger evidence than the transport exception.
                    $stepException = $_.Exception
                }
            }

            if ($nextIndex -ge $script:PollScheduleMs.Count -and @($polls | Where-Object { -not $_.Processed }).Count -eq 0) { break }
            Start-Sleep -Milliseconds 1
        }

        if (-not $stepResponse -and -not $stepException) {
            try {
                $stepResponse = $stepTask.GetAwaiter().GetResult()
                $stepResponseElapsedMs = [int]$clock.Elapsed.TotalMilliseconds
            } catch {
                $stepException = $_.Exception
            }
        }

        $stepStatus = $null
        $stepLocation = ''
        if ($stepResponse) {
            $stepStatus = [int]$stepResponse.StatusCode
            if ($stepResponse.Headers.Location) { $stepLocation = $stepResponse.Headers.Location.ToString() }
            $stepDateUtc = $null
            if ($stepResponse.Headers.Date.HasValue) { $stepDateUtc = $stepResponse.Headers.Date.Value.UtcDateTime.ToString('o') }
            Write-IS74HttpLog -Operation 'portal.stepOne' -Method 'POST' -Uri "$($script:PortalBase)/stepOne" -StatusCode $stepStatus -Location $stepLocation -DateUtc $stepDateUtc -ElapsedMs $stepResponseElapsedMs
        } elseif ($stepException) {
            Write-IS74HttpLog -Operation 'portal.stepOne' -Method 'POST' -Uri "$($script:PortalBase)/stepOne" -ElapsedMs ([int]$clock.Elapsed.TotalMilliseconds) -ErrorMessage $stepException.Message
        }

        # A fresh code proves stepOne reached the backend even if the HTTP response
        # was lost. In that case direct stepTwo is safer than generating another code.
        $stepOneAccepted = $false
        if ($found) {
            $stepOneAccepted = $true
        } elseif ($stepResponse -and $stepStatus -ge 300 -and $stepStatus -lt 400 -and $stepLocation -match '(^|/)stepTwo\?') {
            $stepOneAccepted = $true
        }

        if ($stepResponse -and $stepStatus -ge 300 -and $stepStatus -lt 400 -and $stepLocation -notmatch '(^|/)stepTwo\?') {
            # Experimentally, an already-authorized client is redirected straight
            # to the landing page and no fresh Wi-Fi code is created. Classify the
            # portal response itself; do not probe Internet here because the actual
            # cutoff can happen a few hundred milliseconds after this response.
            Set-IS74AttemptState -Result 'already-authorized' -Reason $AttemptReason
            Write-IS74Log -Message ("portal.stepOne classified=already-authorized reason={0}" -f $AttemptReason)
            if (-not $Quiet) { Write-Host 'Captive portal сообщает, что клиент ещё авторизован.' -ForegroundColor Green }
            return [pscustomobject]@{ Status='AlreadyAuthorized'; InternetConfirmed=$null }
        }

        if (-not $stepOneAccepted) {
            if ($stepException) {
                throw (New-IS74ConnectException -Message ("stepOne завершился сетевой ошибкой: " + $stepException.Message) -RetryableStepOne)
            }
            if ($stepStatus -eq 429) {
                throw (New-IS74ConnectException -Message 'stepOne вернул HTTP 429. Автоматические повторы остановлены.' -UserActionRequired)
            }
            if ($stepStatus -eq 408 -or $stepStatus -ge 500) {
                throw (New-IS74ConnectException -Message "stepOne временно недоступен (HTTP $stepStatus)." -RetryableStepOne)
            }
            throw (New-IS74ConnectException -Message "stepOne не перевёл клиент на stepTwo (HTTP $stepStatus, Location=$stepLocation)." -UserActionRequired)
        }

        # If stepOne explicitly accepted the transaction, never create another code
        # merely because delivery to pushmessages is delayed. Give the existing
        # transaction up to one minute to appear.
        if (-not $found) {
            $slowDeadline = [DateTime]::UtcNow.AddSeconds(50)
            while (-not $found -and [DateTime]::UtcNow -lt $slowDeadline) {
                Start-Sleep -Seconds 5
                $candidate = Find-IS74WifiCodeAfterBaseline -Client $apiPair.Client -Token $token -DeviceId $deviceId -BaselineId $baselineId
                if ($candidate) {
                    $found = $candidate
                    Write-IS74Log -Message ("push.slowPoll codeFound=true messageId={0}" -f $candidate.Id)
                    break
                }
            }
        }
        if (-not $found) {
            throw (New-IS74ConnectException -Message 'stepOne принят сервером, но код не появился в pushmessages. Новый stepOne автоматически не отправляется.' -UserActionRequired)
        }

        $stepTwoUri = $null
        if ($stepLocation -and $stepLocation -match '(^|/)stepTwo\?') {
            $stepTwoUri = if ($stepLocation -match '^https?://') {
                [Uri]$stepLocation
            } else {
                [Uri]::new([Uri]"$($script:PortalBase)/", $stepLocation)
            }
        } else {
            $stepTwoUri = [Uri]("$($script:PortalBase)/stepTwo?phone=$phone&isMp=true")
        }

        $request2 = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $stepTwoUri)
        $form2 = [System.Collections.Generic.Dictionary[string,string]]::new()
        $form2['confirmCode'] = [string]$found.Code
        $form2['phone'] = $phone
        $request2.Content = [System.Net.Http.FormUrlEncodedContent]::new($form2)
        $stepTwo = Send-IS74Request -Client $portalPair.Client -Request $request2 -DiagnosticOperation 'portal.stepTwo'
        if ($stepTwo.StatusCode -lt 300 -or $stepTwo.StatusCode -ge 400 -or $stepTwo.Location -notmatch '(^|/)stepThree(?:\?|$)') {
            throw (New-IS74ConnectException -Message "stepTwo не подтвердил авторизацию (HTTP $($stepTwo.StatusCode), Location=$($stepTwo.Location))." -UserActionRequired)
        }

        $internetConfirmed = $false
        for ($i = 0; $i -lt 8; $i++) {
            if (Test-IS74InternetAccess) { $internetConfirmed = $true; break }
            Start-Sleep -Milliseconds 500
        }

        Set-IS74SuccessfulAuthState -InternetConfirmed:$internetConfirmed -AuthorizedAtUtc $stepTwo.DateUtc
        Write-IS74Log -Message "Wi-Fi авторизация завершена. InternetConfirmed=$internetConfirmed"
        if (-not $Quiet) {
            Write-Host 'Авторизация завершена.' -ForegroundColor Green
            if (-not $internetConfirmed) { Write-Host 'Portal принял код, но проверка Интернета пока не подтвердилась.' -ForegroundColor Yellow }
        }
        return [pscustomobject]@{ Status='Success'; InternetConfirmed=$internetConfirmed }
    } catch {
        $message = $_.Exception.Message
        $result = 'error'
        if ($_.Exception.Data['IS74RetryableStepOne']) { $result = 'step-one-retryable-error' }
        if ($_.Exception.Data['IS74UserActionRequired']) { $result = 'user-action-required' }
        Set-IS74AttemptState -Result $result -Reason $AttemptReason
        Write-IS74Log -Level ERROR -Message "Wi-Fi авторизация: $message"
        throw
    } finally {
        if ($stepResponse) { $stepResponse.Dispose() }
        if ($stepRequest) { $stepRequest.Dispose() }
        foreach ($poll in @($polls)) {
            try { if ($poll.Request) { $poll.Request.Dispose() } } catch { }
        }
        $apiPair.Client.Dispose()
        $apiPair.Handler.Dispose()
        $portalPair.Client.Dispose()
        $portalPair.Handler.Dispose()
    }
}

function Test-IS74AutostartEnabled {
    try {
        Import-Module ScheduledTasks -ErrorAction Stop
        $task = Get-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue
        return ($null -ne $task)
    } catch {
        return $false
    }
}

function Enable-IS74Autostart {
    param([Parameter(Mandatory=$true)][string]$AgentPath)

    if (-not (Get-IS74Secrets)) { throw 'Сначала зарегистрируйте устройство.' }
    Import-Module ScheduledTasks -ErrorAction Stop

    $powershellExe = Get-IS74WindowsPowerShellPath
    $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $AgentPath
    $identityName = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $action = New-ScheduledTaskAction -Execute $powershellExe -Argument $arguments
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identityName
    $principal = New-ScheduledTaskPrincipal -UserId $identityName -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

    Register-ScheduledTask -TaskName $script:TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Автоматическая авторизация Wi-Fi Интерсвязь' -Force | Out-Null
    Start-ScheduledTask -TaskName $script:TaskName
    Write-IS74Log -Message 'Автозапуск агента включён.'
    Write-Host 'Автозапуск включён. Агент запущен в текущей пользовательской сессии.' -ForegroundColor Green
}

function Disable-IS74Autostart {
    try {
        Import-Module ScheduledTasks -ErrorAction Stop
        $task = Get-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue
        if ($task) {
            try { Stop-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue } catch { }
            Unregister-ScheduledTask -TaskName $script:TaskName -Confirm:$false
        }
        Write-IS74Log -Message 'Автозапуск агента отключён.'
        Write-Host 'Автозапуск отключён.' -ForegroundColor Green
    } catch {
        throw "Не удалось отключить автозапуск: $($_.Exception.Message)"
    }
}

function Reset-IS74Registration {
    foreach ($path in @($script:SecretsFile, $script:SessionFile, $script:DeviceMetaFile, $script:RuntimeFile, $script:DeviceFile)) {
        if (Test-Path $path) { Remove-Item -Path $path -Force }
    }
    Write-IS74Log -Message 'Локальная регистрация сброшена.'
}

function Remove-IS74AllData {
    if (Test-IS74AutostartEnabled) { Disable-IS74Autostart }
    if (Test-Path $script:StateDir) {
        Remove-Item -Path $script:StateDir -Recurse -Force
    }
}

function Get-IS74Status {
    Initialize-IS74Storage
    $secrets = Get-IS74Secrets
    $session = Read-IS74JsonFile -Path $script:SessionFile
    $state = Read-IS74RuntimeState
    $registered = ($null -ne $secrets -and $null -ne $secrets.token)
    $maskedPhone = '-'
    if ($secrets -and $secrets.phone -and ([string]$secrets.phone).Length -eq 10) {
        $p = [string]$secrets.phone
        $maskedPhone = '+7 *** ***-' + $p.Substring(6,2) + '-' + $p.Substring(8,2)
    }
    return [pscustomobject]@{
        Registered = $registered
        Phone = $maskedPhone
        AccessEnd = if ($session) { $session.accessEnd } else { $null }
        Autostart = Test-IS74AutostartEnabled
        LastAuthUtc = $state.lastAuthUtc
        ExpectedExpiryUtc = $state.expectedExpiryUtc
        LastResult = $state.lastResult
        AutomaticStepOneAttempts = [int]$state.automaticStepOneAttempts
        UserActionRequired = [bool]$state.userActionRequired
        InternetNow = Test-IS74InternetAccess
        StateDirectory = $script:StateDir
        DiagnosticLog = $script:LogFile
    }
}

function Get-IS74AgentSleepMilliseconds {
    $settings = Get-IS74Settings
    $state = Read-IS74RuntimeState
    $idleMs = [Math]::Max(1000, [int]$settings.agentPollSeconds * 1000)

    if (-not $state.expectedExpiryUtc) { return $idleMs }

    try {
        $expiry = [DateTime]::Parse([string]$state.expectedExpiryUtc).ToUniversalTime()
    } catch {
        return $idleMs
    }

    $now = [DateTime]::UtcNow
    $guardSeconds = [Math]::Max(1, [int]$settings.guardWindowSeconds)
    $guardStart = $expiry.AddSeconds(-$guardSeconds)
    $guardEnd = $expiry.AddSeconds($guardSeconds)
    $guardMs = [Math]::Max(100, [int]$settings.guardProbeIntervalMilliseconds)

    if ($now -lt $guardStart) {
        # Never sleep across the start of the guarded zone.
        $untilGuardMs = [int][Math]::Ceiling(($guardStart - $now).TotalMilliseconds)
        return [Math]::Max(100, [Math]::Min($idleMs, $untilGuardMs))
    }

    if ($now -le $guardEnd) {
        return $guardMs
    }

    if ($state.nextAutomaticRetryUtc) {
        try {
            $retryAt = [DateTime]::Parse([string]$state.nextAutomaticRetryUtc).ToUniversalTime()
            if ($now -lt $retryAt) {
                $untilRetryMs = [int][Math]::Ceiling(($retryAt - $now).TotalMilliseconds)
                return [Math]::Max(100, [Math]::Min($idleMs, $untilRetryMs))
            }
        } catch { }
    }

    # Once overdue, wake promptly so reconnecting Wi-Fi can be repaired without
    # waiting for a long idle interval. No Internet probe is performed here.
    return [Math]::Min($idleMs, 1000)
}

function Invoke-IS74AgentTick {
    Initialize-IS74Storage
    $secrets = Get-IS74Secrets
    if (-not $secrets) { return }

    $settings = Get-IS74Settings
    $state = Read-IS74RuntimeState
    $now = [DateTime]::UtcNow

    # No successful stepTwo means there is no trustworthy 24-hour reference yet.
    # The first authorization is therefore explicit user action.
    if (-not $state.expectedExpiryUtc) { return }

    $expiry = $null
    try {
        $expiry = [DateTime]::Parse([string]$state.expectedExpiryUtc).ToUniversalTime()
    } catch {
        Write-IS74Log -Level WARN -Message 'Не удалось разобрать expectedExpiryUtc.'
        return
    }

    $guardSeconds = [Math]::Max(1, [int]$settings.guardWindowSeconds)
    $guardStart = $expiry.AddSeconds(-$guardSeconds)
    $guardEnd = $expiry.AddSeconds($guardSeconds)
    $guardProbeDelayMs = [Math]::Max(100, [int]$settings.guardProbeIntervalMilliseconds)

    # Outside the small guarded zone we deliberately do not probe the Internet
    # before expiry and do not touch the captive portal.
    if ($now -lt $guardStart) { return }
    if ([bool]$state.userActionRequired) { return }
    if (-not (Test-IS74WifiConnected)) { return }

    # Retryable stepOne failures keep their own backoff. A manual attempt never
    # shifts the 24-hour reference, but an actual failed automatic send should not
    # be hammered merely because the expiry boundary has arrived.
    if ($state.nextAutomaticRetryUtc) {
        try {
            $retryAt = [DateTime]::Parse([string]$state.nextAutomaticRetryUtc).ToUniversalTime()
            if ($now -lt $retryAt) { return }
        } catch { }
    }

    $state = Read-IS74RuntimeState
    $attempts = [int]$state.automaticStepOneAttempts
    $maxAttempts = [int]$settings.maxAutomaticStepOneAttempts
    if ($attempts -ge $maxAttempts) {
        Set-IS74UserActionRequired -Result 'automatic-step-one-limit'
        Write-IS74Log -Level ERROR -Message "Достигнут лимит автоматических stepOne: $maxAttempts. Требуется действие пользователя."
        return
    }

    $shouldSendStepOne = $false
    $reason = if ($attempts -eq 0) { 'automatic' } else { 'retry' }

    if ($now -lt $expiry) {
        # Only inside the short pre-expiry guard do we actively watch Internet.
        # Two close failures mean the real cutoff arrived slightly earlier than
        # our predicted 24-hour timestamp, so stepOne is sent immediately.
        if (Test-IS74InternetUnavailableConfirmed -DelayMilliseconds $guardProbeDelayMs) {
            $shouldSendStepOne = $true
        } else {
            return
        }
    } else {
        # At or after the expected 24-hour boundary, the timer itself is enough.
        # Do NOT spend time probing Internet first. This also covers a machine that
        # slept through the boundary and starts again many hours later (for example
        # at +32h): its first eligible tick goes straight to stepOne.
        $lastAutomaticSendWasBeforeExpiry = $false
        if ($attempts -gt 0 -and $state.lastAttemptUtc) {
            try {
                $lastAttempt = [DateTime]::Parse([string]$state.lastAttemptUtc).ToUniversalTime()
                $lastAutomaticSendWasBeforeExpiry = ($lastAttempt -lt $expiry)
            } catch { }
        }

        if ($attempts -eq 0 -or $lastAutomaticSendWasBeforeExpiry) {
            $shouldSendStepOne = $true
        } elseif ([bool]$state.edgeWatchActive -and $now -le $guardEnd) {
            # stepOne said the old authorization was still alive right around the
            # boundary. Watch only this tiny edge window; the moment Internet
            # actually disappears, send the next allowed stepOne immediately.
            if (Test-IS74InternetUnavailableConfirmed -DelayMilliseconds $guardProbeDelayMs) {
                $shouldSendStepOne = $true
            } else {
                return
            }
        } elseif ([bool]$state.edgeWatchActive -and $now -gt $guardEnd) {
            # The server kept the old session alive beyond the guarded skew window.
            # Stop probing and use the timer/overdue rule again. One direct stepOne
            # is allowed; if the server still says "already authorized", normal
            # retry spacing is used rather than a tight loop.
            $shouldSendStepOne = $true
        } elseif ($state.lastResult -eq 'step-one-retryable-error') {
            $shouldSendStepOne = $true
        } else {
            return
        }
    }

    if (-not $shouldSendStepOne) { return }

    try {
        $result = Connect-IS74Wifi -Force -Quiet -AttemptReason $reason

        if ($result.Status -eq 'AlreadyAuthorized') {
            $state = Read-IS74RuntimeState
            $state.edgeWatchActive = $true
            $state.nextAutomaticRetryUtc = $null
            Save-IS74RuntimeState -State $state

            # Inside the guarded edge we do not wait a fixed number of seconds;
            # fast probes will catch a cutoff that follows this response by only
            # a few hundred milliseconds. Outside the guard, avoid burning the
            # remaining stepOne budget in a tight loop.
            if ([DateTime]::UtcNow -gt $guardEnd) {
                $delays = @($settings.automaticRetryDelaysSeconds)
                $currentAttempts = [int]$state.automaticStepOneAttempts
                $index = [Math]::Max(0, [Math]::Min($currentAttempts - 1, $delays.Count - 1))
                $delay = if ($delays.Count -gt 0) { [int]$delays[$index] } else { 60 }
                Set-IS74AutomaticRetry -DelaySeconds $delay
            }
        }
        return
    } catch {
        $ex = $_.Exception
        $state = Read-IS74RuntimeState
        $attempts = [int]$state.automaticStepOneAttempts

        if ($ex.Data['IS74UserActionRequired']) {
            Set-IS74UserActionRequired
            return
        }

        if ($ex.Data['IS74RetryableStepOne']) {
            if ($attempts -ge $maxAttempts) {
                Set-IS74UserActionRequired -Result 'automatic-step-one-limit'
                return
            }

            $delays = @($settings.automaticRetryDelaysSeconds)
            $index = [Math]::Max(0, [Math]::Min($attempts - 1, $delays.Count - 1))
            $delay = if ($delays.Count -gt 0) { [int]$delays[$index] } else { 60 }
            Set-IS74AutomaticRetry -DelaySeconds $delay
            Write-IS74Log -Level WARN -Message "stepOne будет повторён не раньше чем через $delay сек. Попыток: $attempts/$maxAttempts."
            return
        }

        # Failure before stepOne (for example API baseline/DNS) must not consume
        # the four portal attempts. Back off for one minute to avoid a busy loop.
        Set-IS74AutomaticRetry -DelaySeconds 60
    }
}

Export-ModuleMember -Function @(
    'Initialize-IS74Storage',
    'Register-IS74Account',
    'Connect-IS74Wifi',
    'Get-IS74Status',
    'Enable-IS74Autostart',
    'Disable-IS74Autostart',
    'Test-IS74AutostartEnabled',
    'Reset-IS74Registration',
    'Remove-IS74AllData',
    'Invoke-IS74AgentTick',
    'Get-IS74AgentSleepMilliseconds',
    'Get-IS74Settings',
    'Get-IS74DiagnosticLogPath',
    'Write-IS74RuntimeEvent'
)
