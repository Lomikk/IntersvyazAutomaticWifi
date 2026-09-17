#requires -Version 5.1

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Net.Http
$contractReferences = @([System.Net.Http.HttpClient].Assembly.Location, [System.Net.HttpStatusCode].Assembly.Location) | Select-Object -Unique
Add-Type -ReferencedAssemblies $contractReferences -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public sealed class IS74ContractHandler : HttpMessageHandler
{
    private readonly string _body;
    private readonly bool _withDate;

    public IS74ContractHandler(string body, bool withDate)
    {
        _body = body;
        _withDate = withDate;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent(_body);
        if (_withDate)
            response.Headers.Date = new DateTimeOffset(2026, 9, 17, 21, 51, 52, TimeSpan.Zero);
        return Task.FromResult(response);
    }

}

public sealed class IS74SequenceHandler : HttpMessageHandler
{
    private readonly string _baselineBody;
    private readonly string _codeBody;
    private int _calls;

    public IS74SequenceHandler(string baselineBody, string codeBody)
    {
        _baselineBody = baselineBody;
        _codeBody = codeBody;
    }

    public int Calls { get { return _calls; } }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int call = ++_calls;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent(call == 1 ? _baselineBody : _codeBody);
        response.Headers.Date = new DateTimeOffset(2026, 9, 17, 21, 51, 52, TimeSpan.Zero);
        return Task.FromResult(response);
    }
}



public sealed class IS74FallbackRaceHandler : HttpMessageHandler
{
    private readonly string _baselineBody;
    private readonly string _otherBody;
    private readonly string _codeBody;
    private int _pageSizeOneCalls;
    private int _fallbackCalls;

    public IS74FallbackRaceHandler(string baselineBody, string otherBody, string codeBody)
    {
        _baselineBody = baselineBody;
        _otherBody = otherBody;
        _codeBody = codeBody;
    }

    public int FallbackCalls { get { return _fallbackCalls; } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string query = request.RequestUri.Query ?? String.Empty;
        if (query.IndexOf("pageSize=5", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            ++_fallbackCalls;
            await Task.Delay(4500, cancellationToken).ConfigureAwait(false);
            var fallback = new HttpResponseMessage(HttpStatusCode.OK);
            fallback.Content = new StringContent(_codeBody);
            return fallback;
        }

        int call = ++_pageSizeOneCalls;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent(call == 1 ? _baselineBody : (call == 2 ? _otherBody : _codeBody));
        response.Headers.Date = new DateTimeOffset(2026, 9, 17, 21, 51, 52, TimeSpan.Zero);
        return response;
    }
}

public sealed class IS74PortalDelayHandler : HttpMessageHandler
{
    private int _stepOneCalls;
    private int _stepTwoCalls;

    public int StepOneCalls { get { return _stepOneCalls; } }
    public int StepTwoCalls { get { return _stepTwoCalls; } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri.AbsolutePath;
        if (path.EndsWith("/stepOne", StringComparison.OrdinalIgnoreCase))
        {
            ++_stepOneCalls;
            await Task.Delay(4500, cancellationToken).ConfigureAwait(false);
            var delayed = new HttpResponseMessage(HttpStatusCode.Found);
            delayed.Headers.Location = new Uri("stepTwo?phone=9000000000&isMp=true", UriKind.Relative);
            return delayed;
        }

        if (path.EndsWith("/stepTwo", StringComparison.OrdinalIgnoreCase))
        {
            ++_stepTwoCalls;
            var accepted = new HttpResponseMessage(HttpStatusCode.Found);
            accepted.Headers.Location = new Uri("stepThree", UriKind.Relative);
            accepted.Headers.Date = new DateTimeOffset(2026, 9, 17, 21, 51, 53, TimeSpan.Zero);
            return accepted;
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

}
'@

$modulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\IS74Wifi.psm1'
$module = Import-Module $modulePath -Force -PassThru

& $module {
    function Assert-True {
        param([bool]$Condition, [string]$Message)
        if (-not $Condition) { throw "ASSERT TRUE FAILED: $Message" }
    }

    function Assert-Equal {
        param($Expected, $Actual, [string]$Message)
        if ($Expected -ne $Actual) {
            throw "ASSERT EQUAL FAILED: $Message expected=[$Expected] actual=[$Actual]"
        }
    }

    # Execute the actual HttpClient/Date-header path that previously failed on .HasValue.
    $handlerWithDate = [IS74ContractHandler]::new('[]', $true)
    $clientWithDate = [System.Net.Http.HttpClient]::new($handlerWithDate)
    try {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, 'https://example.invalid/date')
        $result = Send-IS74Request -Client $clientWithDate -Request $request -DiagnosticOperation 'contract.date'
        Assert-Equal 200 $result.StatusCode 'HTTP contract status with Date'
        Assert-Equal '2026-09-17T21:51:52.0000000Z' $result.DateUtc 'HTTP Date conversion'
    } finally {
        $clientWithDate.Dispose()
        $handlerWithDate.Dispose()
    }

    $handlerNoDate = [IS74ContractHandler]::new('[]', $false)
    $clientNoDate = [System.Net.Http.HttpClient]::new($handlerNoDate)
    try {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, 'https://example.invalid/no-date')
        $result = Send-IS74Request -Client $clientNoDate -Request $request -DiagnosticOperation 'contract.no-date'
        Assert-True ($null -eq $result.DateUtc) 'missing HTTP Date remains null'
    } finally {
        $clientNoDate.Dispose()
        $handlerNoDate.Dispose()
    }

    $wifiMessage = [pscustomobject]@{
        id = '102'
        subject = 'Ваш код авторизации'
        push_message = '1234 код авторизации в приложении "Интерсвязь"'
        full_message = $null
    }
    $oldWifiMessage = [pscustomobject]@{
        id = 100
        subject = 'Ваш код авторизации'
        push_message = '9999 код авторизации в приложении "Интерсвязь"'
        full_message = $null
    }
    $otherMessage = [pscustomobject]@{
        id = 103
        subject = 'Другое уведомление'
        push_message = 'не код'
        full_message = $null
    }

    # The actually observed API shape is a root array. Parse it through the real
    # ConvertFrom-Json implementation in each PowerShell engine.
    $rootArrayJson = '[{"id":"102","subject":"Ваш код авторизации","push_message":"1234 код авторизации в приложении \"Интерсвязь\"","full_message":null},{"id":100,"subject":"Ваш код авторизации","push_message":"9999 код авторизации в приложении \"Интерсвязь\"","full_message":null}]'
    $rootArray = $rootArrayJson | ConvertFrom-Json
    $rootTop = Get-IS74TopMessage -Json $rootArray
    Assert-Equal 102 (ConvertTo-IS74MessageId -Value (Get-IS74PropertyValue -Object $rootTop -Name 'id')) 'root array top message'
    Assert-Equal '1234' (Get-IS74WifiCodeFromMessage -Message $rootTop) 'root array code parse'

    $pageSize1Json = '[{"id":"102","subject":"Ваш код авторизации","push_message":"1234 код авторизации в приложении \"Интерсвязь\"","full_message":null}]'
    $pageSize1 = $pageSize1Json | ConvertFrom-Json
    $pageSize1Top = Get-IS74TopMessage -Json $pageSize1
    Assert-Equal 102 (ConvertTo-IS74MessageId -Value (Get-IS74PropertyValue -Object $pageSize1Top -Name 'id')) 'single-item root array survives engine-specific ConvertFrom-Json behavior'

    Assert-True (Test-IS74PushResponseKnownEmpty -Json @()) 'empty root array recognized'
    Assert-True (Test-IS74PushResponseKnownEmpty -Json ([pscustomobject]@{ items = @() })) 'empty items recognized'
    Assert-True (Test-IS74PushResponseKnownEmpty -Json ([pscustomobject]@{ data = @() })) 'empty data recognized'
    Assert-True (-not (Test-IS74PushResponseKnownEmpty -Json ([pscustomobject]@{ meta = [pscustomobject]@{ count = 1 } }))) 'unknown schema is not treated as empty'

    # Baseline must fail closed on an unknown HTTP-200 schema; baseline=0 is safe
    # only when the response is demonstrably empty.
    $baselineHandler = [IS74ContractHandler]::new($rootArrayJson, $true)
    $baselineClient = [System.Net.Http.HttpClient]::new($baselineHandler)
    try {
        Assert-Equal 102 (Get-IS74BaselineId -Client $baselineClient -Token 'test-token' -DeviceId 'test-device') 'baseline id from observed array'
    } finally {
        $baselineClient.Dispose()
        $baselineHandler.Dispose()
    }

    $emptyHandler = [IS74ContractHandler]::new('[]', $true)
    $emptyClient = [System.Net.Http.HttpClient]::new($emptyHandler)
    try {
        Assert-Equal 0 (Get-IS74BaselineId -Client $emptyClient -Token 'test-token' -DeviceId 'test-device') 'empty baseline id'
    } finally {
        $emptyClient.Dispose()
        $emptyHandler.Dispose()
    }

    $unknownHandler = [IS74ContractHandler]::new('{"meta":{"count":1}}', $true)
    $unknownClient = [System.Net.Http.HttpClient]::new($unknownHandler)
    $unknownFailedClosed = $false
    try {
        try {
            $null = Get-IS74BaselineId -Client $unknownClient -Token 'test-token' -DeviceId 'test-device'
        } catch {
            $unknownFailedClosed = ($_.Exception.Message -match 'baseline-схема не распознана')
        }
        Assert-True $unknownFailedClosed 'unknown baseline schema must fail closed'
    } finally {
        $unknownClient.Dispose()
        $unknownHandler.Dispose()
    }

    $shapes = @(
        [pscustomobject]@{ items = @($wifiMessage) },
        [pscustomobject]@{ data = @($wifiMessage) },
        [pscustomobject]@{ data = [pscustomobject]@{ items = @($wifiMessage) } },
        [pscustomobject]@{ data = $null; messages = @($wifiMessage) },
        [pscustomobject]@{ data = @(); messages = @($wifiMessage) },
        [pscustomobject]@{ messages = @($wifiMessage) },
        [pscustomobject]@{ content = [pscustomobject]@{ messages = @($wifiMessage) } },
        [pscustomobject]@{
            meta = [pscustomobject]@{ id = 999; total = 1 }
            payload = [pscustomobject]@{ records = @($wifiMessage) }
        }
    )

    foreach ($shape in $shapes) {
        $msg = Get-IS74TopMessage -Json $shape
        Assert-True ($null -ne $msg) 'supported push shape should yield message'
        Assert-Equal 102 (ConvertTo-IS74MessageId -Value (Get-IS74PropertyValue -Object $msg -Name 'id')) 'supported push shape id'
        Assert-Equal '1234' (Get-IS74WifiCodeFromMessage -Message $msg) 'supported push shape code'
    }

    # Missing properties, nulls and unrelated objects must not throw under StrictMode.
    Assert-True ($null -eq (Get-IS74PropertyValue -Object ([pscustomobject]@{}) -Name 'missing')) 'missing property accessor'
    Assert-True ($null -eq (Get-IS74TopMessage -Json ([pscustomobject]@{ meta = [pscustomobject]@{ id = 999 } }))) 'metadata id is not a push message'
    Assert-True ($null -eq (ConvertTo-IS74MessageId -Value 'not-a-number')) 'invalid message id rejected'
    Assert-True ($null -eq (Get-IS74WifiCodeFromMessage -Message ([pscustomobject]@{ id = 104 }))) 'missing message text rejected'
    Assert-True ($null -eq (Get-IS74WifiCodeFromMessage -Message $otherMessage)) 'wrong subject rejected'

    # pageSize=5 fallback: a newer unrelated notification may be first; code can be second.
    $fallback = @($otherMessage, $wifiMessage, $oldWifiMessage)
    $candidate = Find-IS74WifiCodeInJsonAfterBaseline -Json $fallback -BaselineId 100
    Assert-True ($null -ne $candidate) 'fallback should find code behind unrelated top message'
    Assert-Equal 102 $candidate.Id 'fallback candidate id'
    Assert-Equal '1234' $candidate.Code 'fallback candidate code'
    Assert-True ($null -eq (Find-IS74WifiCodeInJsonAfterBaseline -Json @($oldWifiMessage) -BaselineId 100)) 'stale code must not be reused'

    # Retry state is deterministic and safe before stepOne: fast first retries,
    # no consumption of the four stepOne attempts, and reset after baseline recovery.
    $originalRuntimeFile = $script:RuntimeFile
    $testRuntimeFile = Join-Path $env:TEMP ('is74-contract-runtime-' + [Guid]::NewGuid().ToString('N') + '.json')
    $script:RuntimeFile = $testRuntimeFile
    try {
        $state = Read-IS74RuntimeState
        Assert-Equal 0 $state.preStepFailureCount 'pre-step failure counter default'
        Assert-Equal 0 $state.automaticStepOneAttempts 'stepOne attempt counter default'
        Assert-Equal 1 (Register-IS74PreStepFailure) 'first pre-step retry delay'
        $state = Read-IS74RuntimeState
        Assert-Equal 1 $state.preStepFailureCount 'first pre-step failure count'
        Assert-Equal 0 $state.automaticStepOneAttempts 'pre-step failure does not consume stepOne budget'
        Assert-Equal 2 (Register-IS74PreStepFailure) 'second pre-step retry delay'
        Clear-IS74PreStepFailureState
        $state = Read-IS74RuntimeState
        Assert-Equal 0 $state.preStepFailureCount 'pre-step failure reset'
        Assert-True ($null -eq $state.nextAutomaticRetryUtc) 'pre-step retry timestamp reset'
    } finally {
        $script:RuntimeFile = $originalRuntimeFile
        Remove-Item -Path $testRuntimeFile -Force -ErrorAction SilentlyContinue
    }

    # Execute the real Connect-IS74Wifi fast path with deterministic in-memory HTTP.
    # stepOne deliberately withholds its HTTP response for 4.5 seconds. A fresh
    # push code at the first scheduled poll must still lead directly to stepTwo;
    # otherwise a rare portal-response stall would add seconds of user downtime.
    $contractBaselineBody = '[{"id":100,"subject":"Другое уведомление","push_message":"старое сообщение","full_message":null}]'
    $contractCodeBody = '[{"id":102,"subject":"Ваш код авторизации","push_message":"1234 код авторизации в приложении \"Интерсвязь\"","full_message":null}]'
    $script:contractApiHandler = [IS74SequenceHandler]::new($contractBaselineBody, $contractCodeBody)
    $script:contractPortalHandler = [IS74PortalDelayHandler]::new()
    $script:contractPairIndex = 0

    function script:New-IS74HttpClientPair {
        param(
            [switch]$NoRedirect,
            [int]$TimeoutSeconds = 15,
            [int]$MaxConnections = 16
        )
        $script:contractPairIndex++
        if ($script:contractPairIndex -eq 1) {
            $client = [System.Net.Http.HttpClient]::new($script:contractApiHandler, $false)
            $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
            return [pscustomobject]@{ Handler = $script:contractApiHandler; Client = $client }
        }
        if ($script:contractPairIndex -eq 2) {
            $client = [System.Net.Http.HttpClient]::new($script:contractPortalHandler, $false)
            $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
            return [pscustomobject]@{ Handler = $script:contractPortalHandler; Client = $client }
        }
        throw 'Contract test requested an unexpected HttpClient pair.'
    }
    function script:Get-IS74Secrets { return [pscustomobject]@{ token = 'test-token'; phone = '9000000000' } }
    function script:Get-IS74DeviceId { return 'test-device' }
    function script:Test-IS74InternetAccess { param([switch]$DiagnosticOnFailure) return $true }

    $savedPaths = @{
        StateDir = $script:StateDir
        RuntimeFile = $script:RuntimeFile
        SettingsFile = $script:SettingsFile
        LogDir = $script:LogDir
        LogFile = $script:LogFile
    }
    $contractStateDir = Join-Path $env:TEMP ('is74-contract-connect-' + [Guid]::NewGuid().ToString('N'))
    $script:StateDir = $contractStateDir
    $script:RuntimeFile = Join-Path $contractStateDir 'runtime-state.json'
    $script:SettingsFile = Join-Path $contractStateDir 'settings.json'
    $script:LogDir = Join-Path $contractStateDir 'logs'
    $script:LogFile = Join-Path $script:LogDir 'diagnostic.log'

    $connectElapsed = $null
    try {
        $connectClock = [Diagnostics.Stopwatch]::StartNew()
        $connectResult = Connect-IS74Wifi -Force -Quiet -AttemptReason manual
        $connectClock.Stop()
        $connectElapsed = [int]$connectClock.Elapsed.TotalMilliseconds
        Assert-Equal 'Success' $connectResult.Status 'simulated fast path status'
        Assert-Equal 1 $script:contractPortalHandler.StepOneCalls 'simulated fast path sends one stepOne'
        Assert-Equal 1 $script:contractPortalHandler.StepTwoCalls 'simulated fast path sends one stepTwo'
        Assert-True ($connectElapsed -lt 3000) "fresh code must bypass delayed stepOne response; elapsedMs=$connectElapsed"
    } finally {
        $script:StateDir = $savedPaths.StateDir
        $script:RuntimeFile = $savedPaths.RuntimeFile
        $script:SettingsFile = $savedPaths.SettingsFile
        $script:LogDir = $savedPaths.LogDir
        $script:LogFile = $savedPaths.LogFile
        Remove-Item -Path $contractStateDir -Recurse -Force -ErrorAction SilentlyContinue
    }


    # A pageSize=5 fallback must never block the primary absolute-offset polling.
    # First pageSize=1 after baseline is a newer unrelated notification; the
    # fallback deliberately stalls for 4.5 s, while the next primary poll returns
    # the authorization code. The transaction must still complete quickly.
    $raceBaselineBody = '[{"id":100,"subject":"Другое уведомление","push_message":"старое сообщение","full_message":null}]'
    $raceOtherBody = '[{"id":103,"subject":"Другое уведомление","push_message":"не код","full_message":null}]'
    $raceCodeBody = '[{"id":104,"subject":"Ваш код авторизации","push_message":"5678 код авторизации в приложении \"Интерсвязь\"","full_message":null}]'
    $script:contractApiHandler = [IS74FallbackRaceHandler]::new($raceBaselineBody, $raceOtherBody, $raceCodeBody)
    $script:contractPortalHandler = [IS74PortalDelayHandler]::new()
    $script:contractPairIndex = 0

    $contractStateDir = Join-Path $env:TEMP ('is74-contract-fallback-' + [Guid]::NewGuid().ToString('N'))
    $script:StateDir = $contractStateDir
    $script:RuntimeFile = Join-Path $contractStateDir 'runtime-state.json'
    $script:SettingsFile = Join-Path $contractStateDir 'settings.json'
    $script:LogDir = Join-Path $contractStateDir 'logs'
    $script:LogFile = Join-Path $script:LogDir 'diagnostic.log'

    try {
        $raceClock = [Diagnostics.Stopwatch]::StartNew()
        $raceResult = Connect-IS74Wifi -Force -Quiet -AttemptReason manual
        $raceClock.Stop()
        Assert-Equal 'Success' $raceResult.Status 'fallback race status'
        Assert-Equal 1 $script:contractApiHandler.FallbackCalls 'fallback race starts exactly one pageSize=5 request'
        Assert-Equal 1 $script:contractPortalHandler.StepOneCalls 'fallback race sends one stepOne'
        Assert-Equal 1 $script:contractPortalHandler.StepTwoCalls 'fallback race sends one stepTwo'
        Assert-True ($raceClock.Elapsed.TotalMilliseconds -lt 3000) "stalled pageSize=5 fallback must not delay primary polling; elapsedMs=$([int]$raceClock.Elapsed.TotalMilliseconds)"
    } finally {
        $script:StateDir = $savedPaths.StateDir
        $script:RuntimeFile = $savedPaths.RuntimeFile
        $script:SettingsFile = $savedPaths.SettingsFile
        $script:LogDir = $savedPaths.LogDir
        $script:LogFile = $savedPaths.LogFile
        Remove-Item -Path $contractStateDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Portal redirects are classified narrowly: only the observed landing URL means already-authorized.
    Assert-True (Test-IS74StepTwoLocation -Location 'stepTwo?phone=123&isMp=true') 'relative stepTwo'
    Assert-True (Test-IS74StepTwoLocation -Location 'http://w.is74.ru/stepTwo?phone=123&isMp=true') 'absolute stepTwo'
    Assert-True (Test-IS74StepThreeLocation -Location 'stepThree') 'relative stepThree'
    Assert-True (Test-IS74StepThreeLocation -Location 'http://w.is74.ru/stepThree') 'absolute stepThree'
    Assert-True (Test-IS74AlreadyAuthorizedLocation -Location 'https://www.is74.ru/home/connect/formy_connect/landing/pages/prilozheniye/?utm_source=wifi') 'observed authorized landing'
    Assert-True (-not (Test-IS74AlreadyAuthorizedLocation -Location 'https://www.is74.ru/some/other/redirect')) 'unknown redirect is not already-authorized'

    $expectedSchedule = @(100,150,200,250,350,500,700,1000,1400,2000,3000,4500,6500,10000)
    Assert-Equal ($expectedSchedule -join ',') ($script:PollScheduleMs -join ',') 'poll schedule contract'
    for ($i = 1; $i -lt $script:PollScheduleMs.Count; $i++) {
        Assert-True ($script:PollScheduleMs[$i] -gt $script:PollScheduleMs[$i - 1]) 'poll schedule strictly increasing'
    }
    Assert-True ($script:PollScheduleMs[-1] -lt 12000) 'last scheduled poll fits fast loop deadline'
}

Write-Host 'critical path contract checks: OK'
