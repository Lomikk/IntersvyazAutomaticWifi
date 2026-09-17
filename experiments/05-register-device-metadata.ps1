& {
    $ErrorActionPreference = "Stop"

    $baseDir    = Join-Path $env:LOCALAPPDATA "IS74Wifi"
    $deviceFile = Join-Path $baseDir "device-id.txt"
    $tokenFile  = Join-Path $baseDir "bearer.dpapi"
    $metaFile   = Join-Path $baseDir "device-metadata.json"

    $appVersion = "2.18.0-RS-95aa9b78"
    $buildCode  = 2026061111

    if (-not (Test-Path $deviceFile)) {
        throw "Не найден $deviceFile"
    }

    if (-not (Test-Path $tokenFile)) {
        throw "Не найден $tokenFile. Сначала получите Windows Bearer."
    }

    $deviceId = (Get-Content $deviceFile -Raw).Trim()
    if (-not $deviceId) {
        throw "device-id.txt пуст"
    }

    # Bearer хранится через Windows DPAPI и не выводится в консоль.
    $encryptedToken = (Get-Content $tokenFile -Raw).Trim()
    $secureToken = ConvertTo-SecureString $encryptedToken
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)

    try {
        $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
        if (-not $token) {
            throw "Не удалось расшифровать Bearer"
        }

        # Телефон нужен как AUTHORIZE_PHONE в device-info.
        $phone = (Read-Host "Номер телефона аккаунта") -replace '\D',''
        if ($phone.Length -eq 11 -and ($phone[0] -eq '7' -or $phone[0] -eq '8')) {
            $phone = $phone.Substring(1)
        }
        if ($phone.Length -ne 10) {
            throw "После нормализации должно быть 10 цифр"
        }

        # Человекочитаемое имя устройства.
        $defaultDeviceName = $env:COMPUTERNAME
        $enteredName = Read-Host "Название устройства [$defaultDeviceName]"
        $deviceName = if ([string]::IsNullOrWhiteSpace($enteredName)) {
            $defaultDeviceName
        } else {
            $enteredName.Trim()
        }

        # Формируем нелокализованное имя Windows.
        $cv = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
        $buildNumber = [int]$cv.CurrentBuildNumber
        $ubr = [string]$cv.UBR
        $displayVersion = [string]$cv.DisplayVersion
        $editionId = [string]$cv.EditionID

        $windowsName = if ($buildNumber -ge 22000) { "Windows 11" } else { "Windows 10" }

        $edition = switch -Regex ($editionId) {
            '^Professional' { 'Pro'; break }
            '^Enterprise'   { 'Enterprise'; break }
            '^Education'    { 'Education'; break }
            '^Core'         { 'Home'; break }
            default         { $editionId }
        }

        $fullBuild = if ($ubr) { "$buildNumber.$ubr" } else { [string]$buildNumber }
        $defaultOsName = "$windowsName"
        if ($edition) { $defaultOsName += " $edition" }
        if ($displayVersion) { $defaultOsName += " $displayVersion" }
        $defaultOsName += " (build $fullBuild)"

        $enteredOs = Read-Host "Название ОС [$defaultOsName]"
        $osName = if ([string]::IsNullOrWhiteSpace($enteredOs)) {
            $defaultOsName
        } else {
            $enteredOs.Trim()
        }

        Write-Host ""
        Write-Host "=== DEVICE ===" -ForegroundColor Cyan
        Write-Host "Device ID : $deviceId"
        Write-Host "Name      : $deviceName"
        Write-Host "OS        : $osName"

        $headers = @{
            Accept             = "application/json; version=v2"
            Authorization      = "Bearer $token"
            "X-Device-Id"      = $deviceId
            "X-Api-Source"     = "com.intersvyaz.lk"
            "X-App-version"    = $appVersion
            Platform           = "Android"
            "User-Agent"       = "4.11.0 com.intersvyaz.lk/$appVersion.$buildCode"
        }

        # Сначала проверяем, что локальная сессия ещё действительна.
        Write-Host ""
        Write-Host "=== ПРОВЕРКА BEARER ===" -ForegroundColor Cyan

        $tokenTest = Invoke-WebRequest `
            -Uri "https://api.is74.ru/mobile/pushmessages?page=1&pageSize=1" `
            -Method Get `
            -Headers $headers `
            -SkipHttpErrorCheck

        Write-Host "HTTP: $($tokenTest.StatusCode)"
        if ($tokenTest.StatusCode -ne 200) {
            throw "Текущий Bearer не прошёл проверку. Метаданные не отправляем."
        }

        # Проверено экспериментально: сервер принимает metadata без push TOKEN.
        # Windows не подделывает FCM/RuStore push-token и SIM-поля.
        $deviceInfo = [ordered]@{
            TYPE             = 5
            UNIQUE_DEVICE_ID = $deviceId
            AUTHORIZE_PHONE  = $phone
            VERS_NAME        = $appVersion
            ASSEMBLY_CODE    = $buildCode
            OS_VERS          = $osName
            DEVICE_MODEL     = $deviceName
        }

        Write-Host ""
        Write-Host "=== РЕГИСТРАЦИЯ МЕТАДАННЫХ ===" -ForegroundColor Cyan

        $register = Invoke-WebRequest `
            -Uri "https://api.is74.ru/mobile/pushtoken/add-with-device-id" `
            -Method Put `
            -Headers $headers `
            -ContentType "application/json" `
            -Body ($deviceInfo | ConvertTo-Json -Compress) `
            -SkipHttpErrorCheck

        Write-Host "HTTP: $($register.StatusCode)"

        if ($register.StatusCode -lt 200 -or $register.StatusCode -ge 300) {
            Write-Host $register.Content
            throw "Сервер отклонил DeviceInfo"
        }

        Write-Host "DeviceInfo принят сервером." -ForegroundColor Green

        $localMeta = [ordered]@{
            deviceId       = $deviceId
            deviceModel    = $deviceName
            osVersion      = $osName
            appVersion     = $appVersion
            assemblyCode   = $buildCode
            registeredAt   = (Get-Date).ToString("o")
        }

        $localMeta |
            ConvertTo-Json |
            Set-Content $metaFile -Encoding UTF8

        Write-Host ""
        Write-Host "Локальные несекретные метаданные:"
        Write-Host $metaFile
        Write-Host ""
        Write-Host "Обновите экран 'Устройства' в приложении и проверьте отображаемое имя." -ForegroundColor Green
    }
    finally {
        if ($ptr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        }
        Remove-Variable token -ErrorAction SilentlyContinue
    }
}
