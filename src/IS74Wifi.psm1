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
$script:InternetProbeIpv4 = $null
$script:InternetProbeDnsAttemptUtc = [DateTime]::MinValue

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
            guardProbeTimeoutMilliseconds = 300
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
        guardProbeTimeoutMilliseconds = 300
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
        [string]$DiagnosticOperation = 'http',
        [switch]$SuppressSuccessLog
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
        $dateHeader = $response.Headers.Date
        if ($null -ne $dateHeader) {
            $dateUtc = ([DateTimeOffset]$dateHeader).UtcDateTime.ToString('o')
        }
        if (-not $SuppressSuccessLog) {
            Write-IS74HttpLog -Operation $DiagnosticOperation -Method $method -Uri $uri -StatusCode ([int]$response.StatusCode) -Location $location -DateUtc $dateUtc -ElapsedMs ([int]$clock.Elapsed.TotalMilliseconds)
        }
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
        $authId = [string](Get-IS74PropertyValue -Object $checkJson -Name 'authId')
        $addresses = Get-IS74PropertyValue -Object $checkJson -Name 'addresses'
        $addressesCount = if ($null -ne $addresses) { @($addresses).Count } else { 0 }
        Write-IS74Log -Message ("auth.check-confirm parsed authIdPresent={0} addressesCount={1}" -f [bool]$authId, $addressesCount)
        if (-not $authId) { throw 'check-confirm не вернул подтверждённый authId.' }
        if ($addressesCount -gt 0) {
            throw 'Сервер вернул несколько связанных адресов. Этот MVP пока не выбирает userId автоматически; регистрация остановлена без сохранения Bearer.'
        }

        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$($script:ApiBase)/mobile/auth/get-token")
        Add-IS74ApiHeaders -Request $request -DeviceId $deviceId -UserId '-1' -ProfileId 'null'
        $form = [System.Collections.Generic.Dictionary[string,string]]::new()
        $form['authId'] = $authId
        $form['userId'] = ''
        $form['uniqueDeviceId'] = $deviceId
        $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($form)
        $tokenResult = Send-IS74Request -Client $pair.Client -Request $request -DiagnosticOperation 'auth.get-token'
        if ($tokenResult.StatusCode -lt 200 -or $tokenResult.StatusCode -ge 300) {
            throw "get-token вернул HTTP $($tokenResult.StatusCode)."
        }

        $session = $tokenResult.Body | ConvertFrom-Json
        $token = [string](Get-IS74PropertyValue -Object $session -Name 'TOKEN')
        $userId = Get-IS74PropertyValue -Object $session -Name 'USER_ID'
        $profileId = Get-IS74PropertyValue -Object $session -Name 'PROFILE_ID'
        $accessBegin = [string](Get-IS74PropertyValue -Object $session -Name 'ACCESS_BEGIN')
        $accessEnd = [string](Get-IS74PropertyValue -Object $session -Name 'ACCESS_END')
        Write-IS74Log -Message ("auth.get-token parsed tokenPresent={0} accessBegin={1} accessEnd={2}" -f [bool]$token, $accessBegin, $accessEnd)
        if (-not $token) { throw 'get-token вернул HTTP 2xx, но TOKEN отсутствует.' }

        Save-IS74Secrets -Token $token -Phone $phone
        Write-IS74JsonFile -Path $script:SessionFile -Value ([ordered]@{
            deviceId = $deviceId
            userId = $userId
            profileId = $profileId
            accessBegin = $accessBegin
            accessEnd = $accessEnd
            registeredAt = (Get-Date).ToString('o')
        })

        # Metadata is best-effort and must not invalidate an otherwise successful registration.
        $null = Register-IS74DeviceMetadata -Token $token -Phone $phone -DeviceId $deviceId
        Write-IS74Log -Message 'Регистрация аккаунта завершена.'
        Write-Host 'Регистрация завершена. Bearer и номер сохранены через DPAPI текущего пользователя.' -ForegroundColor Green
        if ($accessEnd) { Write-Host "Сессия API действует до: $accessEnd" }
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

function Update-IS74InternetProbeAddressCache {
    param([switch]$Force)

    if ($script:InternetProbeIpv4 -and -not $Force) { return $true }
    $now = [DateTime]::UtcNow
    if (-not $Force -and ($now - $script:InternetProbeDnsAttemptUtc).TotalMinutes -lt 60) { return $false }
    $script:InternetProbeDnsAttemptUtc = $now
    try {
        $address = [Net.Dns]::GetHostAddresses('www.msftconnecttest.com') |
            Where-Object { $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork } |
            Select-Object -First 1
        if ($address) {
            $script:InternetProbeIpv4 = $address.ToString()
            return $true
        }
    } catch { }
    return $false
}

function Invoke-IS74InternetProbe {
    param(
        [int]$TimeoutMilliseconds = 4000,
        [switch]$UseCachedAddress
    )

    if ($TimeoutMilliseconds -lt 50) { $TimeoutMilliseconds = 50 }
    $canonicalUri = 'http://www.msftconnecttest.com/connecttest.txt'
    if ($UseCachedAddress -and -not $script:InternetProbeIpv4) {
        return [pscustomobject]@{
            Online = $false
            HttpResponseReceived = $false
            StatusCode = $null
            Body = $null
            ErrorMessage = 'probe address cache unavailable'
            ElapsedMs = 0
            Uri = $canonicalUri
        }
    }

    $uri = if ($UseCachedAddress) { "http://$($script:InternetProbeIpv4)/connecttest.txt" } else { $canonicalUri }
    $pair = New-IS74HttpClientPair -NoRedirect -TimeoutSeconds 4 -MaxConnections 2
    try { $pair.Handler.UseProxy = $false } catch { }
    $pair.Client.Timeout = [TimeSpan]::FromMilliseconds($TimeoutMilliseconds)
    $request = $null
    $response = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $uri)
        if ($UseCachedAddress) { $request.Headers.Host = 'www.msftconnecttest.com' }
        $null = $request.Headers.TryAddWithoutValidation('Cache-Control', 'no-cache, no-store')
        $null = $request.Headers.TryAddWithoutValidation('Pragma', 'no-cache')
        $response = $pair.Client.SendAsync($request).GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $ok = ($status -eq 200 -and $body.Trim() -eq 'Microsoft Connect Test')
        return [pscustomobject]@{
            Online = $ok
            HttpResponseReceived = $true
            StatusCode = $status
            Body = $body
            ErrorMessage = $null
            ElapsedMs = [int]$clock.Elapsed.TotalMilliseconds
            Uri = $canonicalUri
        }
    } catch {
        return [pscustomobject]@{
            Online = $false
            HttpResponseReceived = $false
            StatusCode = $null
            Body = $null
            ErrorMessage = $_.Exception.Message
            ElapsedMs = [int]$clock.Elapsed.TotalMilliseconds
            Uri = $canonicalUri
        }
    } finally {
        if ($response) { $response.Dispose() }
        if ($request) { $request.Dispose() }
        $pair.Client.Dispose()
        $pair.Handler.Dispose()
    }
}

function Test-IS74InternetAccess {
    param(
        [switch]$DiagnosticOnFailure,
        [int]$TimeoutMilliseconds = 4000
    )

    $probe = Invoke-IS74InternetProbe -TimeoutMilliseconds $TimeoutMilliseconds
    if (-not $probe.Online -and $DiagnosticOnFailure) {
        if ($probe.HttpResponseReceived) {
            Write-IS74HttpLog -Operation 'internet.probe' -Method 'GET' -Uri $probe.Uri -StatusCode $probe.StatusCode -ElapsedMs $probe.ElapsedMs -Body $probe.Body
        } else {
            Write-IS74HttpLog -Operation 'internet.probe' -Method 'GET' -Uri $probe.Uri -ElapsedMs $probe.ElapsedMs -ErrorMessage $probe.ErrorMessage
        }
    }
    return [bool]$probe.Online
}

function Test-IS74WifiConnected {
    try {
        foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            if ($adapter.NetworkInterfaceType -eq [Net.NetworkInformation.NetworkInterfaceType]::Wireless80211 -and
                $adapter.OperationalStatus -eq [Net.NetworkInformation.OperationalStatus]::Up) {
                return $true
            }
        }
        return $false
    } catch {
        # Only use the slower external command as a compatibility fallback.
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

function Get-IS74PropertyValue {
    param(
        $Object,
        [Parameter(Mandatory=$true)][string]$Name
    )
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function ConvertTo-IS74MessageId {
    param($Value)
    if ($null -eq $Value) { return $null }
    [Int64]$parsed = 0
    if ([Int64]::TryParse([string]$Value, [ref]$parsed)) { return $parsed }
    return $null
}

function Get-IS74PushMessages {
    param(
        $Json,
        [int]$Depth = 0
    )

    if ($null -eq $Json -or $Depth -gt 4) { return @() }
    if ($Json -is [array]) { return @($Json) }

    $id = ConvertTo-IS74MessageId -Value (Get-IS74PropertyValue -Object $Json -Name 'id')
    $subject = Get-IS74PropertyValue -Object $Json -Name 'subject'
    $pushMessage = Get-IS74PropertyValue -Object $Json -Name 'push_message'
    $fullMessage = Get-IS74PropertyValue -Object $Json -Name 'full_message'
    $hasMessageText = ($null -ne $subject -or $null -ne $pushMessage -or $null -ne $fullMessage)
    if ($null -ne $id -and $hasMessageText) { return ,$Json }

    foreach ($name in @('items', 'data', 'messages', 'pushMessages', 'push_messages', 'result', 'content')) {
        $property = $Json.PSObject.Properties[$name]
        if ($null -eq $property) { continue }
        $value = $property.Value
        if ($null -eq $value) { continue }
        $messages = @(Get-IS74PushMessages -Json $value -Depth ($Depth + 1))
        if ($messages.Count -gt 0) { return $messages }
    }

    foreach ($property in @($Json.PSObject.Properties)) {
        $value = $property.Value
        if ($null -eq $value -or $value -is [string] -or $value -is [ValueType]) { continue }
        $messages = @(Get-IS74PushMessages -Json $value -Depth ($Depth + 1))
        if ($messages.Count -gt 0) { return $messages }
    }

    return @()
}

function Get-IS74TopMessage {
    param($Json)
    $messages = @(Get-IS74PushMessages -Json $Json)
    if ($messages.Count -eq 0) { return $null }
    return $messages[0]
}

function Get-IS74WifiCodeFromMessage {
    param($Message)
    if ($null -eq $Message) { return $null }
    $subject = [string](Get-IS74PropertyValue -Object $Message -Name 'subject')
    if ($subject -ne 'Ваш код авторизации') { return $null }
    foreach ($text in @(
        [string](Get-IS74PropertyValue -Object $Message -Name 'push_message'),
        [string](Get-IS74PropertyValue -Object $Message -Name 'full_message')
    )) {
        if ($text -match '^(\d{4}) код авторизации в приложении "Интерсвязь"$') {
            return $Matches[1]
        }
    }
    return $null
}

function Test-IS74StepTwoLocation {
    param([string]$Location)
    return [bool]($Location -and $Location -match '(^|/)stepTwo\?')
}

function Test-IS74StepThreeLocation {
    param([string]$Location)
    return [bool]($Location -and $Location -match '(^|/)stepThree(?:\?|$)')
}

function Test-IS74AlreadyAuthorizedLocation {
    param([string]$Location)
    if (-not $Location) { return $false }

    # Both landing targets below have been observed after stepOne for a client
    # whose current 24-hour Wi-Fi authorization is still active. Keep this
    # allow-list narrow: an arbitrary 3xx must never be treated as success.
    if ($Location -match '(?i)^(?:https?://(?:www\.)?is74\.ru)?/home/connect/formy_connect/landing/pages/prilozheniye(?:/|\?|#|$)') {
        return $true
    }
    if ($Location -match '(?i)^https?://openwifi\.is74\.ru/home/connect/formy_connect/landing/pages/wifi(?:/|\?|#|$)') {
        return $true
    }
    return $false
}

function Test-IS74PushResponseKnownEmpty {
    param(
        $Json,
        [int]$Depth = 0
    )
    if ($null -eq $Json -or $Depth -gt 4) { return $false }
    if ($Json -is [array]) { return (@($Json).Count -eq 0) }

    $sawKnownContainer = $false
    foreach ($name in @('items', 'data', 'messages', 'pushMessages', 'push_messages', 'result', 'content')) {
        $property = $Json.PSObject.Properties[$name]
        if ($null -eq $property) { continue }
        $sawKnownContainer = $true
        $value = $property.Value
        if ($null -eq $value) { continue }
        if ($value -is [array]) {
            if (@($value).Count -gt 0) { return $false }
            continue
        }
        if (-not (Test-IS74PushResponseKnownEmpty -Json $value -Depth ($Depth + 1))) { return $false }
    }
    return $sawKnownContainer
}

function Get-IS74BaselineId {
    param(
        [Parameter(Mandatory=$true)][System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory=$true)][string]$Token,
        [Parameter(Mandatory=$true)][string]$DeviceId,
        [switch]$SuppressSuccessLog
    )
    $request = New-IS74PushRequest -Token $Token -DeviceId $DeviceId -PageSize 1
    $result = Send-IS74Request -Client $Client -Request $request -DiagnosticOperation 'push.baseline' -SuppressSuccessLog:$SuppressSuccessLog
    if ($result.StatusCode -eq 401) { throw 'Bearer отклонён сервером (HTTP 401). Выполните регистрацию заново.' }
    if (-not $result.IsSuccess) { throw "Baseline pushmessages HTTP $($result.StatusCode)." }
    $bodyText = [string]$result.Body
    if ($bodyText.Trim() -eq '[]') {
        if (-not $SuppressSuccessLog) { Write-IS74Log -Message 'push.baseline empty topId=0 rootProperties=<array>' }
        return [Int64]0
    }
    $json = $bodyText | ConvertFrom-Json
    $msg = Get-IS74TopMessage -Json $json
    $messageId = if ($msg) { ConvertTo-IS74MessageId -Value (Get-IS74PropertyValue -Object $msg -Name 'id') } else { $null }
    if ($null -ne $messageId) {
        if (-not $SuppressSuccessLog) { Write-IS74Log -Message ("push.baseline topId={0}" -f $messageId) }
        return $messageId
    }
    $rootProperties = if ($null -eq $json) { '<null>' } elseif ($json -is [array]) { '<array>' } else { @($json.PSObject.Properties.Name) -join ',' }
    if (Test-IS74PushResponseKnownEmpty -Json $json) {
        if (-not $SuppressSuccessLog) { Write-IS74Log -Message ("push.baseline empty topId=0 rootProperties={0}" -f $rootProperties) }
        return [Int64]0
    }
    Write-IS74Log -Level ERROR -Message ("push.baseline schemaUnrecognized=true rootProperties={0}" -f $rootProperties)
    throw 'pushmessages вернул HTTP 200, но baseline-схема не распознана. stepOne не отправлен.'
}

function Find-IS74WifiCodeInJsonAfterBaseline {
    param(
        $Json,
        [Parameter(Mandatory=$true)][Int64]$BaselineId
    )
    $messages = @(Get-IS74PushMessages -Json $Json)
    foreach ($msg in $messages) {
        $id = ConvertTo-IS74MessageId -Value (Get-IS74PropertyValue -Object $msg -Name 'id')
        if ($null -eq $id -or $id -le $BaselineId) { continue }
        $code = Get-IS74WifiCodeFromMessage -Message $msg
        if ($code) { return [pscustomobject]@{ Code = $code; Id = $id } }
    }
    return $null
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
    $candidate = Find-IS74WifiCodeInJsonAfterBaseline -Json $json -BaselineId $BaselineId
    if ($candidate) {
        Write-IS74Log -Message ("push.fallback codeFound=true messageId={0}" -f $candidate.Id)
        return $candidate
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
        preStepFailureCount = 0
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
    $state.preStepFailureCount = 0
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
        preStepFailureCount = 0
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
        throw (New-IS74ConnectException -Message 'Автоматические попытки остановлены до действия пользователя.' -UserActionRequired)
    }
    $count = [int]$state.automaticStepOneAttempts
    if ($count -ge [int]$settings.maxAutomaticStepOneAttempts) {
        $state.userActionRequired = $true
        Save-IS74RuntimeState -State $state
        throw (New-IS74ConnectException -Message 'Достигнут лимит автоматических отправок stepOne.' -UserActionRequired)
    }
    $count++
    $state.automaticStepOneAttempts = $count
    $state.preStepFailureCount = 0
    $state.nextAutomaticRetryUtc = $null
    $state.lastAttemptUtc = [DateTime]::UtcNow.ToString('o')
    $state.lastAttemptReason = $Reason
    $state.lastResult = 'step-one-sent'
    Save-IS74RuntimeState -State $state
    return $count
}

function Set-IS74AutomaticRetry {
    param([int]$DelaySeconds)
    $state = Read-IS74RuntimeState
    $state.nextAutomaticRetryUtc = [DateTime]::UtcNow.AddSeconds($DelaySeconds).ToString('o')
    Save-IS74RuntimeState -State $state
}

function Register-IS74PreStepFailure {
    $state = Read-IS74RuntimeState
    $count = [int]$state.preStepFailureCount + 1
    $delays = @(1, 2, 5, 15, 30, 60)
    $index = [Math]::Min($count - 1, $delays.Count - 1)
    $delay = [int]$delays[$index]
    $state.preStepFailureCount = $count
    $state.nextAutomaticRetryUtc = [DateTime]::UtcNow.AddSeconds($delay).ToString('o')
    $state.lastResult = 'pre-step-retryable-error'
    Save-IS74RuntimeState -State $state
    Write-IS74Log -Level WARN -Message ("pre-step failure count={0} retryInSeconds={1}" -f $count, $delay)
    return $delay
}

function Clear-IS74PreStepFailureState {
    $state = Read-IS74RuntimeState
    if ([int]$state.preStepFailureCount -eq 0 -and -not $state.nextAutomaticRetryUtc) { return }
    $state.preStepFailureCount = 0
    $state.nextAutomaticRetryUtc = $null
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
    param(
        [int]$DelayMilliseconds = -1,
        [int]$ProbeTimeoutMilliseconds = 300
    )

    $settings = Get-IS74Settings
    if ($DelayMilliseconds -lt 0) {
        $DelayMilliseconds = [int]([double]$settings.internetProbeConfirmDelaySeconds * 1000)
    }

    # Before the authoritative expiry timestamp, only explicit HTTP evidence of
    # captive behavior may trigger stepOne. A DNS/transport timeout is ambiguous
    # and must never burn the four-attempt budget early.
    $first = Invoke-IS74InternetProbe -TimeoutMilliseconds $ProbeTimeoutMilliseconds -UseCachedAddress
    if ($first.Online) { return $false }
    if (-not $first.HttpResponseReceived) { return $false }

    if ($DelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $DelayMilliseconds }
    $second = Invoke-IS74InternetProbe -TimeoutMilliseconds $ProbeTimeoutMilliseconds -UseCachedAddress
    if ($second.Online) { return $false }
    return [bool]$second.HttpResponseReceived
}

function Connect-IS74Wifi {
    param(
        [switch]$Force,
        [switch]$Quiet,
        [ValidateSet('manual','automatic','retry')][string]$AttemptReason = 'manual'
    )

    $connectClock = [Diagnostics.Stopwatch]::StartNew()
    Initialize-IS74Storage
    $secrets = Get-IS74Secrets
    $deviceId = Get-IS74DeviceId
    $savedToken = [string](Get-IS74PropertyValue -Object $secrets -Name 'token')
    $savedPhone = [string](Get-IS74PropertyValue -Object $secrets -Name 'phone')
    if (-not $savedToken -or -not $savedPhone -or -not $deviceId) {
        throw 'Устройство не зарегистрировано. Сначала выполните register.'
    }

    if (-not $Force -and (Test-IS74InternetAccess)) {
        if (-not $Quiet) { Write-Host 'Интернет уже доступен; авторизация не требуется.' -ForegroundColor Green }
        return [pscustomobject]@{ Status='AlreadyOnline'; InternetConfirmed=$true }
    }

    if (-not $Quiet) { Write-Host 'Начинаю Wi-Fi авторизацию...' -ForegroundColor Cyan }

    $authMutex = [Threading.Mutex]::new($false, 'Local\IS74Wifi.Auth')
    $authMutexOwned = $false
    $apiPair = $null
    $portalPair = $null
    $polls = New-Object System.Collections.ArrayList
    $stepRequest = $null
    $stepResponse = $null
    $stepOneStarted = $false
    try {
        try {
            $authMutexOwned = $authMutex.WaitOne(0)
        } catch [Threading.AbandonedMutexException] {
            $authMutexOwned = $true
        }
        if (-not $authMutexOwned) {
            if ($Quiet) { return [pscustomobject]@{ Status='Busy'; InternetConfirmed=$null } }
            throw 'Другая Wi-Fi авторизация уже выполняется.'
        }

        if ($AttemptReason -eq 'manual') {
            # Explicit user action unlocks a stopped automatic cycle, but never changes
            # the 24-hour timer. Only successful stepTwo may do that.
            Clear-IS74AutomaticRetryState
        }

        # The API normally answers in ~100 ms. A long timeout here directly delays
        # stepOne, so cap critical-path API stalls aggressively.
        $apiPair = New-IS74HttpClientPair -TimeoutSeconds 3 -MaxConnections 16
        $portalPair = New-IS74HttpClientPair -NoRedirect -TimeoutSeconds 5 -MaxConnections 4
        $token = $savedToken
        $phone = $savedPhone
        $baselineId = Get-IS74BaselineId -Client $apiPair.Client -Token $token -DeviceId $deviceId -SuppressSuccessLog
        $stepOneAttempt = $null

        $stepRequest = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$($script:PortalBase)/stepOne")
        $form = [System.Collections.Generic.Dictionary[string,string]]::new()
        $form['phone'] = "8$phone"
        $form['dial_code'] = '7'
        $form['country_code'] = 'ru'
        $form['sendPush'] = 'on'
        $stepRequest.Content = [System.Net.Http.FormUrlEncodedContent]::new($form)

        # The attempt counter must be durable before the network side effect, but
        # request construction and baseline validation must not consume the budget.
        if ($AttemptReason -ne 'manual') {
            $stepOneAttempt = Register-IS74AutomaticStepOneSend -Reason $AttemptReason
        }

        $preStepOneMs = [int]$connectClock.Elapsed.TotalMilliseconds
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $stepOneStarted = $true
        try {
            $stepTask = $portalPair.Client.SendAsync($stepRequest, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead)
        } catch {
            throw (New-IS74ConnectException -Message ("stepOne не удалось запустить: " + $_.Exception.Message) -RetryableStepOne)
        }
        $stepException = $null
        $stepResponseElapsedMs = $null
        $nextIndex = 0
        $found = $null
        $fallbackUsed = $false
        $pollErrorCount = 0
        $pollLastError = ''

        while ($clock.Elapsed.TotalMilliseconds -lt 12000 -and -not $found) {
            $nowMs = $clock.Elapsed.TotalMilliseconds
            while ($nextIndex -lt $script:PollScheduleMs.Count -and $nowMs -ge $script:PollScheduleMs[$nextIndex] -and -not $found) {
                $request = New-IS74PushRequest -Token $token -DeviceId $deviceId -PageSize 1
                $actualStartMs = [int]$clock.Elapsed.TotalMilliseconds
                $task = $apiPair.Client.SendAsync($request)
                $poll = [pscustomobject]@{
                    Request = $request
                    Task = $task
                    Processed = $false
                    TargetMs = $script:PollScheduleMs[$nextIndex]
                    StartMs = $actualStartMs
                    Kind = 'Primary'
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

                    if ($poll.Kind -eq 'Fallback') {
                        $candidate = Find-IS74WifiCodeInJsonAfterBaseline -Json $json -BaselineId $baselineId
                        if ($candidate) {
                            $found = [pscustomobject]@{
                                Code = $candidate.Code
                                Id = $candidate.Id
                                Source = 'fallback'
                                TargetMs = $poll.TargetMs
                                StartMs = $poll.StartMs
                                ObservedMs = [int]$clock.Elapsed.TotalMilliseconds
                            }
                            break
                        }
                        continue
                    }

                    $msg = Get-IS74TopMessage -Json $json
                    $id = if ($msg) { ConvertTo-IS74MessageId -Value (Get-IS74PropertyValue -Object $msg -Name 'id') } else { $null }
                    if ($null -eq $id -or $id -le $baselineId) { continue }
                    $code = Get-IS74WifiCodeFromMessage -Message $msg
                    if ($code) {
                        $found = [pscustomobject]@{
                            Code = $code
                            Id = $id
                            Source = 'primary'
                            TargetMs = $poll.TargetMs
                            StartMs = $poll.StartMs
                            ObservedMs = [int]$clock.Elapsed.TotalMilliseconds
                        }
                        break
                    }
                    if (-not $fallbackUsed) {
                        # Never block the millisecond-sensitive loop with a synchronous
                        # pageSize=5 request. Schedule it beside the other GETs.
                        $fallbackUsed = $true
                        $fallbackRequest = New-IS74PushRequest -Token $token -DeviceId $deviceId -PageSize 5
                        $fallbackTask = $apiPair.Client.SendAsync($fallbackRequest)
                        $null = $polls.Add([pscustomobject]@{
                            Request = $fallbackRequest
                            Task = $fallbackTask
                            Processed = $false
                            TargetMs = [int]$clock.Elapsed.TotalMilliseconds
                            StartMs = [int]$clock.Elapsed.TotalMilliseconds
                            Kind = 'Fallback'
                        })
                    }
                } catch {
                    # Disk logging here can delay the next absolute poll offset.
                    # Aggregate errors in memory and flush only after the fast phase.
                    $pollErrorCount++
                    $pollLastError = $_.Exception.Message
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
                    if ($earlyStatus -ge 300 -and $earlyStatus -lt 400 -and -not (Test-IS74StepTwoLocation -Location $earlyLocation)) {
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

            # Keep launch jitter small without burning a full CPU core. This mirrors
            # the experimentally validated scheduler used by experiment 07.
            $nextTarget = if ($nextIndex -lt $script:PollScheduleMs.Count) { [double]$script:PollScheduleMs[$nextIndex] } else { $null }
            if ($null -ne $nextTarget) {
                $remaining = $nextTarget - $clock.Elapsed.TotalMilliseconds
                if ($remaining -gt 4) { Start-Sleep -Milliseconds 1 }
                else { [Threading.Thread]::SpinWait(250) }
            } else {
                Start-Sleep -Milliseconds 1
            }
        }

        if (-not $found -and $pollErrorCount -gt 0) {
            Write-IS74Log -Level WARN -Message ("push.poll errors={0} lastError={1}" -f $pollErrorCount, $pollLastError)
        }

        if (-not $found -and -not $stepResponse -and -not $stepException) {
            try {
                $stepResponse = $stepTask.GetAwaiter().GetResult()
                $stepResponseElapsedMs = [int]$clock.Elapsed.TotalMilliseconds
            } catch {
                $stepException = $_.Exception
            }
        }

        $stepStatus = $null
        $stepLocation = ''
        $stepDateUtc = $null
        $deferStepOneHttpLog = [bool]$found
        if ($stepResponse) {
            $stepStatus = [int]$stepResponse.StatusCode
            if ($stepResponse.Headers.Location) { $stepLocation = $stepResponse.Headers.Location.ToString() }
            $stepDateHeader = $stepResponse.Headers.Date
            if ($null -ne $stepDateHeader) { $stepDateUtc = ([DateTimeOffset]$stepDateHeader).UtcDateTime.ToString('o') }
            if (-not $deferStepOneHttpLog) {
                Write-IS74HttpLog -Operation 'portal.stepOne' -Method 'POST' -Uri "$($script:PortalBase)/stepOne" -StatusCode $stepStatus -Location $stepLocation -DateUtc $stepDateUtc -ElapsedMs $stepResponseElapsedMs
            }
        } elseif ($stepException -and -not $deferStepOneHttpLog) {
            Write-IS74HttpLog -Operation 'portal.stepOne' -Method 'POST' -Uri "$($script:PortalBase)/stepOne" -ElapsedMs ([int]$clock.Elapsed.TotalMilliseconds) -ErrorMessage $stepException.Message
        }

        # A fresh code proves stepOne reached the backend even if the HTTP response
        # was lost. In that case direct stepTwo is safer than generating another code.
        $stepOneAccepted = $false
        $stepOneAmbiguous = $false
        if ($found) {
            $stepOneAccepted = $true
        } elseif ($stepResponse -and $stepStatus -ge 300 -and $stepStatus -lt 400 -and (Test-IS74StepTwoLocation -Location $stepLocation)) {
            $stepOneAccepted = $true
        } elseif ($stepException) {
            # SendAsync was started, so a later transport failure cannot prove that
            # the backend did not process stepOne. Never issue a blind replacement.
            $stepOneAccepted = $true
            $stepOneAmbiguous = $true
        }

        if ($stepResponse -and $stepStatus -ge 300 -and $stepStatus -lt 400 -and (Test-IS74AlreadyAuthorizedLocation -Location $stepLocation)) {
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
                    $found = [pscustomobject]@{
                        Code = $candidate.Code
                        Id = $candidate.Id
                        Source = 'slow'
                        TargetMs = $null
                        StartMs = $null
                        ObservedMs = [int]$clock.Elapsed.TotalMilliseconds
                    }
                    break
                }
            }
        }
        if (-not $found) {
            $missingCodeMessage = if ($stepOneAmbiguous) {
                'Ответ stepOne потерян/оборван, а код не появился в pushmessages. Состояние server-side неоднозначно; новый stepOne автоматически не отправляется.'
            } else {
                'stepOne принят сервером, но код не появился в pushmessages. Новый stepOne автоматически не отправляется.'
            }
            throw (New-IS74ConnectException -Message $missingCodeMessage -UserActionRequired)
        }

        $stepTwoUri = $null
        if (Test-IS74StepTwoLocation -Location $stepLocation) {
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
        $stepTwoStartMs = [int]$clock.Elapsed.TotalMilliseconds
        $stepTwoStartedAtUtc = [DateTime]::UtcNow.ToString('o')
        try {
            $stepTwo = Send-IS74Request -Client $portalPair.Client -Request $request2 -DiagnosticOperation 'portal.stepTwo'
        } catch {
            $stepTwoError = $_.Exception
            if ($pollErrorCount -gt 0) {
                Write-IS74Log -Level WARN -Message ("push.poll errors={0} lastError={1}" -f $pollErrorCount, $pollLastError)
                $pollErrorCount = 0
            }
            if ($deferStepOneHttpLog) {
                if ($stepResponse) {
                    Write-IS74HttpLog -Operation 'portal.stepOne' -Method 'POST' -Uri "$($script:PortalBase)/stepOne" -StatusCode $stepStatus -Location $stepLocation -DateUtc $stepDateUtc -ElapsedMs $stepResponseElapsedMs
                } elseif ($stepException) {
                    Write-IS74HttpLog -Operation 'portal.stepOne' -Method 'POST' -Uri "$($script:PortalBase)/stepOne" -ElapsedMs $stepTwoStartMs -ErrorMessage $stepException.Message
                }
            }
            # A lost HTTP response does not prove stepTwo failed. If Internet is
            # already back, persist success instead of generating another code.
            if (Test-IS74InternetAccess) {
                Set-IS74SuccessfulAuthState -InternetConfirmed:$true -AuthorizedAtUtc $stepTwoStartedAtUtc
                Write-IS74Log -Level WARN -Message 'portal.stepTwo responseLost=true internetConfirmed=true; accepted by side effect.'
                return [pscustomobject]@{ Status='Success'; InternetConfirmed=$true }
            }
            throw (New-IS74ConnectException -Message ("stepTwo завершился сетевой ошибкой после отправки кода: " + $stepTwoError.Message) -UserActionRequired)
        }
        $stepTwoDoneMs = [int]$clock.Elapsed.TotalMilliseconds
        if ($pollErrorCount -gt 0) {
            Write-IS74Log -Level WARN -Message ("push.poll errors={0} lastError={1}" -f $pollErrorCount, $pollLastError)
            $pollErrorCount = 0
        }
        if ($deferStepOneHttpLog) {
            if ($stepResponse) {
                Write-IS74HttpLog -Operation 'portal.stepOne' -Method 'POST' -Uri "$($script:PortalBase)/stepOne" -StatusCode $stepStatus -Location $stepLocation -DateUtc $stepDateUtc -ElapsedMs $stepResponseElapsedMs
            } elseif ($stepException) {
                Write-IS74HttpLog -Operation 'portal.stepOne' -Method 'POST' -Uri "$($script:PortalBase)/stepOne" -ElapsedMs $stepTwoStartMs -ErrorMessage $stepException.Message
            }
        }
        $foundSource = [string](Get-IS74PropertyValue -Object $found -Name 'Source')
        $foundTargetMs = Get-IS74PropertyValue -Object $found -Name 'TargetMs'
        $foundStartMs = Get-IS74PropertyValue -Object $found -Name 'StartMs'
        $foundObservedMs = Get-IS74PropertyValue -Object $found -Name 'ObservedMs'
        Write-IS74Log -Message ("critical.timing preStepOneMs={0} baselineId={1} stepOneAttempt={2} source={3} targetMs={4} pollStartMs={5} codeObservedMs={6} stepTwoStartMs={7} stepTwoDoneMs={8}" -f $preStepOneMs, $baselineId, $stepOneAttempt, $foundSource, $foundTargetMs, $foundStartMs, $foundObservedMs, $stepTwoStartMs, $stepTwoDoneMs)

        if ($stepTwo.StatusCode -lt 300 -or $stepTwo.StatusCode -ge 400 -or -not (Test-IS74StepThreeLocation -Location $stepTwo.Location)) {
            throw (New-IS74ConnectException -Message "stepTwo не подтвердил авторизацию (HTTP $($stepTwo.StatusCode), Location=$($stepTwo.Location))." -UserActionRequired)
        }

        # Persist the new 24-hour reference immediately after stepTwo. Internet
        # verification is diagnostic and must never delay or endanger that state.
        Set-IS74SuccessfulAuthState -InternetConfirmed:$false -AuthorizedAtUtc $stepTwo.DateUtc

        $internetConfirmed = $false
        for ($i = 0; $i -lt 8; $i++) {
            if (Test-IS74InternetAccess) { $internetConfirmed = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if ($internetConfirmed) {
            $state = Read-IS74RuntimeState
            $state.internetConfirmed = $true
            Save-IS74RuntimeState -State $state
        }

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
        if ($stepOneStarted -and $result -eq 'error') {
            $_.Exception.Data['IS74UnexpectedAfterStepOne'] = $true
            $result = 'unexpected-error-after-step-one'
        }
        Set-IS74AttemptState -Result $result -Reason $AttemptReason
        Write-IS74Log -Level ERROR -Message "Wi-Fi авторизация: $message"
        throw
    } finally {
        if ($stepResponse) { $stepResponse.Dispose() }
        if ($stepRequest) { $stepRequest.Dispose() }
        foreach ($poll in @($polls)) {
            try { if ($poll.Request) { $poll.Request.Dispose() } } catch { }
        }
        if ($apiPair) {
            $apiPair.Client.Dispose()
            $apiPair.Handler.Dispose()
        }
        if ($portalPair) {
            $portalPair.Client.Dispose()
            $portalPair.Handler.Dispose()
        }
        if ($authMutexOwned) {
            try { $authMutex.ReleaseMutex() } catch { }
        }
        $authMutex.Dispose()
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

function Wait-IS74ScheduledTaskStopped {
    param(
        [Parameter(Mandatory=$true)][string]$TaskName,
        [int]$TimeoutMilliseconds = 5000
    )

    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($null -eq $task -or [string]$task.State -ne 'Running') { return $true }
        Start-Sleep -Milliseconds 100
    } while ($clock.ElapsedMilliseconds -lt $TimeoutMilliseconds)

    return $false
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

    # Updating runtime files while an old agent is still running leaves the old
    # module resident in memory. Stop the existing task before replacing it.
    $existingTask = Get-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue
    if ($existingTask) {
        try { Stop-ScheduledTask -TaskName $script:TaskName -ErrorAction Stop } catch {
            throw "Не удалось остановить предыдущий агент: $($_.Exception.Message)"
        }
        if (-not (Wait-IS74ScheduledTaskStopped -TaskName $script:TaskName)) {
            throw 'Предыдущий агент не остановился за 5 секунд; задача не заменена, чтобы не оставить старый модуль в памяти.'
        }
    }
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
    $statusToken = [string](Get-IS74PropertyValue -Object $secrets -Name 'token')
    $statusPhone = [string](Get-IS74PropertyValue -Object $secrets -Name 'phone')
    $registered = [bool]$statusToken
    $maskedPhone = '-'
    if ($statusPhone -and $statusPhone.Length -eq 10) {
        $maskedPhone = '+7 *** ***-' + $statusPhone.Substring(6,2) + '-' + $statusPhone.Substring(8,2)
    }
    return [pscustomobject]@{
        Registered = $registered
        Phone = $maskedPhone
        AccessEnd = Get-IS74PropertyValue -Object $session -Name 'accessEnd'
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
    if (-not (Test-Path $script:SecretsFile)) { return }

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
    $guardProbeTimeoutMs = [Math]::Max(100, [int]$settings.guardProbeTimeoutMilliseconds)

    # Outside the small guarded zone we deliberately do not probe the Internet
    # before expiry and do not touch the captive portal. DNS for the guard probe is
    # warmed here so name resolution can never block the millisecond-sensitive edge.
    if ($now -lt $guardStart) {
        $null = Update-IS74InternetProbeAddressCache
        return
    }
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

    $guardDetectionMs = $null
    if ($now -lt $expiry) {
        # Never let a pre-expiry probe run across the authoritative T boundary.
        # Near T it is faster and safer to let the next 250 ms tick use the timer.
        $guardProbeBudgetMs = (2 * $guardProbeTimeoutMs) + $guardProbeDelayMs + 100
        if (($expiry - $now).TotalMilliseconds -le $guardProbeBudgetMs) { return }

        $guardClock = [Diagnostics.Stopwatch]::StartNew()
        if (Test-IS74InternetUnavailableConfirmed -DelayMilliseconds $guardProbeDelayMs -ProbeTimeoutMilliseconds $guardProbeTimeoutMs) {
            $guardDetectionMs = [int]$guardClock.Elapsed.TotalMilliseconds
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
            if (Test-IS74InternetUnavailableConfirmed -DelayMilliseconds $guardProbeDelayMs -ProbeTimeoutMilliseconds $guardProbeTimeoutMs) {
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
        if ($null -ne $guardDetectionMs) {
            Write-IS74Log -Message ("guard.timing detectionMs={0}" -f $guardDetectionMs)
        }

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

        if ($ex.Data['IS74UnexpectedAfterStepOne']) {
            # The request may already have changed server state. Never issue a blind
            # replacement stepOne for an unclassified post-stepOne failure.
            Set-IS74UserActionRequired -Result 'unexpected-error-after-step-one'
            return
        }

        # Failure before stepOne (for example API baseline/DNS) is safe to retry and
        # does not consume the four portal attempts. Retry quickly at first, then
        # back off exponentially if the API is genuinely unavailable.
        $null = Register-IS74PreStepFailure
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
