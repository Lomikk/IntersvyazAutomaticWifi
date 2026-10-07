from pathlib import Path

root = Path(__file__).resolve().parents[1]
runtime_files = [
    root / 'IS74Wifi.ps1',
    root / 'agent.ps1',
    root / 'src' / 'IS74Wifi.psm1',
]
for path in runtime_files:
    assert path.read_bytes().startswith(b'\xef\xbb\xbf'), f'{path.name}: UTF-8 BOM required for Windows PowerShell 5.1'

contract_test = root / 'tests' / 'critical_path_contract.ps1'
assert contract_test.read_bytes().startswith(b'\xef\xbb\xbf'), 'critical_path_contract.ps1: UTF-8 BOM required for Windows PowerShell 5.1'

module = (root / 'src' / 'IS74Wifi.psm1').read_text(encoding='utf-8')
cli = (root / 'IS74Wifi.ps1').read_text(encoding='utf-8')
agent = (root / 'agent.ps1').read_text(encoding='utf-8')

for text, name in [(module, 'module'), (cli, 'cli'), (agent, 'agent')]:
    assert '#requires -Version 5.1' in text, f'{name}: PS 5.1 requirement missing'

assert '-SkipHttpErrorCheck' not in module
assert 'ForEach-Object -Parallel' not in module
assert '??' not in module
assert 'Show-IS74Toast' not in module
assert 'preExpiryMinutes' not in module
assert 'maxAutomaticStepOneAttempts = 4' in module
assert 'automaticRetryDelaysSeconds = @(15, 30, 60)' in module
assert 'internetProbeConfirmDelaySeconds = 2' in module
assert 'guardWindowSeconds = 10' in module
assert 'guardProbeIntervalMilliseconds = 250' in module
assert 'Test-IS74InternetUnavailableConfirmed' in module
assert 'Get-NetConnectionProfile' not in module.replace('# NCSI/Get-NetConnectionProfile is deliberately not authoritative here.', '')
assert 'LegacyTokenFile' not in module
assert 'Get-IS74LegacyToken' not in module
assert 'bearer.dpapi' not in module
assert 'New-ScheduledTaskTrigger -AtLogOn' in module
assert 'ExecutionTimeLimit ([TimeSpan]::Zero)' in module
assert "LogonType Interactive" in module
assert "RunLevel Limited" in module
assert 'GET /stepTwo' not in module
assert 'GET /stepThree' not in module
assert 'secrets.dpapi' in module
assert 'confirmCode' in module
assert "'connect'  { $null = Connect-IS74Wifi -Force; return }" in cli
assert "authId'] = ''" in module
assert 'if ($now -lt $guardStart) {' in module and 'Update-IS74InternetProbeAddressCache' in module
assert 'At or after the expected 24-hour boundary, the timer itself is enough.' in module
assert 'Get-IS74AgentSleepMilliseconds' in module
assert 'Start-Sleep -Milliseconds $delayMs' in agent
assert '[IS74WifiNative]::DetachConsole() | Out-Null' in agent, 'background agent must detach from Windows Terminal/console'
assert 'private static extern bool FreeConsole();' in module, 'native FreeConsole binding missing'
assert 'public static bool DetachConsole()' in module, 'console detach wrapper missing'
assert "automaticStepOneAttempts" in module
assert "userActionRequired" in module
assert "Cache-Control', 'no-cache, no-store'" in module
assert "diagnostic.log" in module
assert "LogMaxBytes    = 1MB" in module
assert "LogRetentionFiles = 5" in module
assert "Protect-IS74LogText" in module
assert "<redacted-code>" in module
assert "Get-IS74DiagnosticLogPath" in module
assert "'logs'" in cli
assert "agent.start" in agent
assert "agent.tick error=" in agent

assert "System32\\WindowsPowerShell\\v1.0\\powershell.exe" in module
assert 'toast-test' not in cli
assert 'AppUserModelID' not in module
assert 'Windows.UI.Notifications' not in module
assert '.Headers.Date.HasValue' not in module
assert '.Headers.Date.Value' not in module
assert '$Json.items' not in module
assert '$json.items' not in module
assert '$Message.subject' not in module
assert '$Message.push_message' not in module
assert '$Message.full_message' not in module
assert 'Get-IS74PushMessages' in module
assert 'Get-IS74PropertyValue' in module

# Critical-path invariants discovered during production audit.
for forbidden in ['$checkJson.', '$secrets.token', '$secrets.phone', '$session.TOKEN', '$session.ACCESS_BEGIN', '$session.ACCESS_END']:
    assert forbidden not in module, f'unsafe StrictMode external property access: {forbidden}'

fast_start = module.index('while ($clock.Elapsed.TotalMilliseconds -lt 12000 -and -not $found)')
fast_end = module.index('if (-not $found -and $pollErrorCount -gt 0)', fast_start)
fast_loop = module[fast_start:fast_end]
assert 'Find-IS74WifiCodeAfterBaseline' not in fast_loop, 'fast polling loop must not perform synchronous fallback HTTP'
assert 'Send-IS74Request' not in fast_loop, 'fast polling loop must not perform synchronous HTTP'
assert 'Write-IS74Log' not in fast_loop, 'fast polling loop must not perform disk logging'
assert "Kind = 'Fallback'" in fast_loop, 'pageSize=5 fallback must be scheduled asynchronously'
assert 'if (-not $found -and -not $stepResponse -and -not $stepException)' in module, 'fresh code must bypass waiting for stepOne HTTP completion'
assert 'New-IS74HttpClientPair -TimeoutSeconds 3 -MaxConnections 16' in module, 'critical API timeout guard missing'
assert 'New-IS74HttpClientPair -NoRedirect -TimeoutSeconds 5 -MaxConnections 4' in module, 'portal critical timeout guard missing'
assert '[Threading.Thread]::SpinWait(250)' in fast_loop, 'fast polling scheduler must preserve low-jitter experiment behavior'
assert 'critical.timing preStepOneMs=' in module, 'critical-path timing telemetry missing'
assert 'Get-IS74BaselineId -Client $apiPair.Client -Token $token -DeviceId $deviceId -SuppressSuccessLog' in module, 'critical baseline success logging must be deferred'
assert 'push.baseline schemaUnrecognized=true' in module, 'unknown baseline schema must fail closed'
assert "$bodyText.Trim() -eq '[]'" in module, 'empty root-array baseline must be engine independent'
assert 'IS74UnexpectedAfterStepOne' in module, 'unknown post-stepOne failures must not be blindly retried'
assert 'полный polling schedule' in module, 'spent stepOne attempts must be retryable after mailbox polling'
assert 'stepOne принят сервером, но свежий код не появился за полный polling schedule.' in module
assert 'Register-IS74PreStepFailure' in module, 'safe pre-step retry state missing'
assert "-Result 'bearer-invalid'" in module, 'HTTP 401 must become terminal bearer-invalid state'
assert '$script:AmbiguousStepTwoProbeScheduleMs = @(0, 250, 500, 1000, 2000, 4000)' in module
assert 'Test-IS74InternetAfterAmbiguousStepTwo' in module
assert "[Threading.Mutex]::new($false, 'Local\\IS74Wifi.Auth')" in module, 'cross-process auth transaction mutex missing'
assert 'guardProbeTimeoutMilliseconds = 300' in module, 'guard probe timeout must be explicitly bounded'
assert 'if (($expiry - $now).TotalMilliseconds -le $guardProbeBudgetMs) { return }' in module, 'pre-expiry probe must not cross T'
assert 'if (-not $first.HttpResponseReceived) { return $false }' in module, 'transport timeout must not trigger pre-expiry stepOne'
assert "$script:WifiSsidPrefix = 'Campus Wi-Fi'" in module, 'campus SSID prefix gate missing'
assert 'IS74WifiNative' in module and 'WlanQueryInterface' in module, 'SSID gate must use native WLAN API'
assert '& netsh.exe' not in module, 'authorization path must not spawn netsh for SSID detection'
assert 'Test-IS74TargetWifiConnected' in module, 'target Wi-Fi gate missing'
assert 'Update-IS74InternetProbeAddressCache' in module, 'guard DNS must be warmed outside the critical window'
assert "$request.Headers.Host = 'www.msftconnecttest.com'" in module, 'cached-IP probe must preserve HTTP Host'
assert '$first = Invoke-IS74InternetProbe -TimeoutMilliseconds $ProbeTimeoutMilliseconds -UseCachedAddress' in module, 'guard probe must not perform DNS'

step_two_accept = module.index('Set-IS74SuccessfulAuthState -InternetConfirmed:$false -AuthorizedAtUtc $stepTwo.DateUtc')
internet_verify = module.index('for ($i = 0; $i -lt 8; $i++)', step_two_accept)
assert step_two_accept < internet_verify, 'successful stepTwo state must persist before Internet verification'

assert 'Test-IS74AlreadyAuthorizedLocation' in module
assert 'landing/pages/prilozheniye' in module
assert r'openwifi\.is74\.ru/home/connect/formy_connect/landing/pages/wifi' in module
assert 'Get-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue' in module
assert 'Wait-IS74ScheduledTaskStopped' in module, 'autostart replacement must wait for the old agent to stop'
assert 'tests\\critical_path_contract.ps1' not in module  # sanity: tests stay outside runtime

# C# direct-interface authorization policy and the validated local SUSU probe.
csharp_runtime = (root / 'src' / 'IS74Wifi.App' / 'ApplicationRuntime.cs').read_text(encoding='utf-8')
csharp_http_profiles = (root / 'src' / 'IS74Wifi.Core' / 'HttpClientProfiles.cs').read_text(encoding='utf-8')
csharp_internet_probe = (root / 'src' / 'IS74Wifi.Core' / 'InternetConnectivityProbe.cs').read_text(encoding='utf-8')
assert 'new CachedDnsConnector' not in csharp_runtime and 'HostAddressCache' not in csharp_runtime, 'production runtime must not wire cached-IP/direct-connect'
assert csharp_http_profiles.count('handler.ConnectCallback = direct.ConnectAsync;') == 3, 'API, portal and SUSU must share direct-interface connector'
assert 'PhysicalAdapterSelection.Enumerate' in csharp_runtime and 'CanReachPortalAsync' in csharp_runtime, 'runtime direct adapter selection/pre-step handshake missing'
assert 'UseProxy = false' in csharp_http_profiles, 'direct auth transports must bypass system proxy'
csharp_direct = (root / 'src' / 'IS74Wifi.Core' / 'DirectNetworkConnector.cs').read_text(encoding='utf-8')
assert 'IpUnicastIf = 31' in csharp_direct and 'socket.Bind(' in csharp_direct, 'route must bind both interface and source IPv4'
assert 'AllowedHosts' in csharp_direct and 'api.is74.ru' in csharp_direct and 'w.is74.ru' in csharp_direct and 'online.susu.ru' in csharp_direct
csharp_dns = (root / 'src' / 'IS74Wifi.Core' / 'InterfaceDnsResolver.cs').read_text(encoding='utf-8')
assert 'BindSocket(socket, adapter)' in csharp_dns and 'SystemFallback' in csharp_dns, 'adapter DNS and address-only fallback required'
assert 'CanReachPortalAsync' in csharp_direct and 'ConnectHostAsync("w.is74.ru", 80' in csharp_direct, 'portal preflight must use HTTP port 80'
csharp_diagnostic_program = (root / 'src' / 'IS74Wifi.App' / 'Program.cs').read_text(encoding='utf-8')
assert 'NetworkPathDiagnostics' in csharp_diagnostic_program and 'InspectAsync(TimeSpan.FromSeconds(3))' in csharp_diagnostic_program, 'network diagnostics must inspect all bound physical paths'
assert 'http://online.susu.ru/' in csharp_internet_probe and 'https://online.susu.ru/' in csharp_internet_probe, 'local SUSU connectivity probe contract missing'
assert 'readBody: false' in csharp_internet_probe, 'SUSU success probe must be headers-only'
assert 'www.msftconnecttest.com' not in csharp_internet_probe, 'Microsoft Connect Test must not remain the primary C# probe'

# C# terminal UI stays a real console renderer rather than a web/TUI dependency.
csharp_ui = (root / 'src' / 'IS74Wifi.App' / 'InteractiveTerminalUi.cs').read_text(encoding='utf-8')
csharp_program = (root / 'src' / 'IS74Wifi.App' / 'Program.cs').read_text(encoding='utf-8')
terminal_canvas = (root / 'src' / 'IS74Wifi.App' / 'Ui' / 'TerminalCanvas.cs').read_text(encoding='utf-8')
terminal_layout = (root / 'src' / 'IS74Wifi.App' / 'Ui' / 'TerminalLayout.cs').read_text(encoding='utf-8')
terminal_output = (root / 'src' / 'IS74Wifi.App' / 'Ui' / 'TerminalOutput.cs').read_text(encoding='utf-8')
assert 'MinimumCanvasWidth = 79;' in terminal_layout, 'terminal UI must retain an 80-column-safe layout'
assert 'PreferredCanvasWidth = 116;' in terminal_layout, 'terminal UI wide layout target changed'
assert 'CanvasHeight = 30;' in terminal_layout, 'terminal UI height contract changed'
assert 'Console.' not in terminal_layout and 'TerminalLayout Calculate(int terminalWidth, int terminalHeight)' in terminal_layout, 'terminal layout must stay a pure calculation over supplied window dimensions'
assert 'Console.WindowWidth' in csharp_ui and 'UpdateLayout()' in csharp_ui and 'TerminalLayout.Calculate' in csharp_ui, 'terminal UI must adapt to the current terminal width'
assert 'Cell' in terminal_canvas and 'Palette' in terminal_canvas and 'WrapText' in terminal_canvas and 'DrawBox' in terminal_canvas, 'terminal canvas primitives must have one owner'
assert 'ConsoleKey.UpArrow' in csharp_ui and 'ConsoleKey.DownArrow' in csharp_ui and 'ConsoleKey.Enter' in csharp_ui
assert "new MenuItem('1', \"Авторизовать Wi-Fi сейчас\"" in csharp_ui, 'terminal UI numeric hotkeys missing'
assert "new MenuItem('0', \"Выход\"" in csharp_ui, 'terminal UI exit hotkey missing'
assert 'BannerMode.InitialSweep' in csharp_ui and 'BannerMode.AmbientSweep' in csharp_ui, 'brand sweep animations missing'
assert 'glint' not in csharp_ui.lower(), 'per-letter glint animation must stay disabled'
assert 'InterSvyaz Wi-Fi Auth' in csharp_ui
assert 'IS74W_SKIP_REVEAL' in csharp_program, 'bootstrap must not replay the full reveal after installation'
# Both render paths must obtain items via the same state-aware router, rather
# than checking one brittle direct GetPrimaryItems(...) call spelling.
assert csharp_ui.count('GetCurrentItems()') >= 3, 'rich and compact menus must use the same action list router'
assert 'var items = GetCurrentItems();' in csharp_ui and 'RunCompactSelection(GetCurrentItems())' in csharp_ui, 'rich/compact action list paths diverged'
assert 'MenuPage.Settings => GetSettingsItems(status)' in csharp_ui and 'MenuPage.Maintenance => GetMaintenanceItems(status)' in csharp_ui and 'MenuPage.Updates => GetUpdateItems(status)' in csharp_ui and '_ => GetPrimaryItems(status)' in csharp_ui, 'state-aware action list router lost menu pages'
assert 'DrawBox(canvas, rightPaneX, PaneY, paneWidth, PaneHeight, "СОСТОЯНИЕ")' in csharp_ui and 'DrawNetworkPathLine' in csharp_ui and 'path.Preferred ? "> " : "  "' in csharp_ui and 's.VpnActive' in csharp_ui, 'main status pane must render per-path state, preferred path, and VPN state'
assert 'DrawStatusLine(canvas, PaneY + 2, "Интернет"' not in csharp_ui and 'DrawStatusLine(canvas, PaneY + 4, "Авторизация"' not in csharp_ui, 'main status pane must not reintroduce global Internet/authorization rows'
assert '"ещё не выполнялась ○"' in csharp_ui, 'unknown captive authorization state must not be presented as denied Wi-Fi access'
csharp_wifi = (root / 'src' / 'IS74Wifi.Core' / 'WindowsWifiService.cs').read_text(encoding='utf-8')
assert 'WlanConnectionProfileDetails' in csharp_wifi and 'GetConnectedSsid()' in csharp_wifi, 'SSID detection must use the Windows profile API'
assert 'WlanQueryInterface' not in csharp_wifi, 'C# client must not use the location-gated current_connection WLAN query'
assert '"доступен ●"' in csharp_ui, 'status indicators must render after their status text'
assert 'automaticEnabled ? InteractiveMenuAction.DisableAutomaticAuthorization : InteractiveMenuAction.EnableAutomaticAuthorization' in csharp_ui, 'settings must expose the correct auto-authorization action'
assert 'InteractiveMenuAction.ToggleTrayIconVisibility' in csharp_ui and 'Значок в трее:' in csharp_ui, 'settings must expose tray visibility'
tray_service = (root / 'src' / 'IS74Wifi.App' / 'WindowsTrayIconService.cs').read_text(encoding='utf-8')
assert 'TrayIconSemanticState.Normal' in tray_service and 'TrayIconSemanticState.Working' in tray_service and 'TrayIconSemanticState.Question' in tray_service and 'TrayIconSemanticState.Warning' in tray_service and 'TrayIconSemanticState.Error' in tray_service, 'semantic tray icon states missing'
assert 'TaskbarCreated' in tray_service and 'ShellNotifyIconW' in tray_service, 'persistent tray icon must survive Explorer restart'
app_project = (root / 'src' / 'IS74Wifi.App' / 'IS74Wifi.App.csproj').read_text(encoding='utf-8')
assert r'<ApplicationIcon>Assets\IS74Wifi.ico</ApplicationIcon>' in app_project, 'application icon is not wired into the Windows executable'
assert 'registered ? "Сбросить регистрацию" : "Зарегистрировать устройство"' in csharp_ui
assert "new MenuItem('3', \"Состояние и подробный отчёт\", InteractiveMenuAction.ShowDetailedStatus)" in csharp_ui
assert "new MenuItem('2', \"Скорость и рейтинг\", InteractiveMenuAction.SpeedTools)" in csharp_ui, 'speed/leaderboard submenu entry missing'
assert 'TargetView' not in csharp_ui, 'rich UI must not reintroduce speculative submenu navigation'
assert 'PromptDigitsAsync' in csharp_ui and 'ConsoleKey.Escape' in csharp_ui, 'interactive registration input must be cancellable in-pane'
assert 'PromptConfirmationCodeAsync' in csharp_ui and 'ConfirmationCodePromptAction.RequestNewCode' in csharp_ui, 'confirmation-code prompt must offer an explicit user-driven resend path'
assert 'lastRenderedCanvas' in terminal_output and 'cell.Equals(lastRenderedCanvas' in terminal_output, 'terminal renderer must diff frames to avoid full-screen shimmer'
assert 'PrepareInteractiveConsole(clear: !terminalOutput.HasRenderedFrame)' in csharp_ui, 'menu/workflow transitions must preserve the framebuffer for diff rendering'
for forbidden in ('InteractiveMenuAction', 'CampusSpeedToolsService', 'AuthorizationFlow', 'SettingsStore'):
    assert forbidden not in terminal_output, f'terminal output must not know application actions: {forbidden}'
assert 'private bool menuSelectionInitialized;' in csharp_ui and 'selected = hotkeyIndex;' in csharp_ui, 'menu selection must survive actions and direct numeric hotkeys'
assert 'CanUseInteractiveSession' in csharp_ui and 'compactLayout' in csharp_ui, 'terminal UI must recover after temporary narrow resize'
assert 'PrepareForAction(' not in csharp_program, 'menu actions must stay inside the terminal panes instead of reopening the legacy action screen'
onboarding_start = csharp_program.index('private static async Task<bool> CompleteInteractiveOnboardingAsync')
onboarding_end = csharp_program.index('private static async Task RegisterFromMenuAsync', onboarding_start)
onboarding = csharp_program[onboarding_start:onboarding_end]
assert onboarding.index('consent == AnonymousStatisticsConsent.Unknown') < onboarding.index('if (!registered)'), 'anonymous statistics consent must be requested before device registration during onboarding'
register_start = csharp_program.index('private static async Task<int> RegisterAsync()')
register_end = csharp_program.index('private static async Task<int> ConnectAsync()', register_start)
register_flow = csharp_program[register_start:register_end]
assert register_flow.index('PromptAnonymousStatisticsConsentConsole();') < register_flow.index('RequestConfirmationAsync'), 'console registration must request anonymous statistics consent before contacting the registration API'
speed_tools_ui = (root / 'src' / 'IS74Wifi.App' / 'InteractiveTerminalUi.SpeedTools.cs').read_text(encoding='utf-8')
assert 'RunSpeedToolsAsync' in speed_tools_ui and 'ЛИДЕРЫ КАМПУСА' in speed_tools_ui, 'speed tools UI missing'
assert 'Jitter' in speed_tools_ui, 'speed measurement diagnostics missing'
assert 'Packet loss' not in speed_tools_ui and 'Loss       ' not in speed_tools_ui, 'unmeasured packet loss must stay hidden from the UI'
assert 'PromptSpeedNicknameAsync' in speed_tools_ui and '[4] Опубликовать' in speed_tools_ui, 'speed publication UI missing'
assert 'CampusSpeedToolsService' in speed_tools_ui and 'speedTools.MeasureAsync' in speed_tools_ui, 'speed tools UI must be connected to the measurement service'
launch_settings = (root / 'src' / 'IS74Wifi.App' / 'Properties' / 'launchSettings.json').read_text(encoding='utf-8')
assert 'IS74Wifi.App (Local Debug)' in launch_settings and 'IS74W_RUN_LOCAL' in launch_settings, 'Visual Studio local-debug profile missing'

# Menu UX: Esc is explicitly labeled as exit on the main screen, confirmation code is fixed at four digits,
# successful manual authorization remains visible until the user returns, and technical details open copy-friendly.
assert 'Esc выход' in csharp_ui, 'main menu must label Esc as exit'
assert 'PromptConfirmationCodeAsync' in csharp_program and 'digits.Length == 4' in csharp_ui, 'confirmation code must require exactly four digits'
assert 'ShowActionHistoryAsync' in csharp_program and 'DescribeAuthorizationOutcomeForUi(outcome)' in csharp_program, 'manual authorization history must stay visible for success and failure'
assert 'IS74Wifi-status.txt' in csharp_program and 'notepad.exe' in csharp_program, 'detailed status must open as a copy-friendly external report'
assert 'input.Length != 4' in register_flow, 'non-interactive registration must enforce the same four-digit confirmation contract'
assert 'SMS-код' not in csharp_program and 'код подтверждения' in csharp_program, 'registration UI must not assume SMS delivery'
assert 'network.failure route=' in (root / 'src' / 'IS74Wifi.Core' / 'HttpTransport.cs').read_text(encoding='utf-8'), 'local network failure diagnostics missing'

# Multi-step interactive actions keep a readable, scrollable history instead of replacing the pane with only a final verdict.
action_history = (root / 'src' / 'IS74Wifi.App' / 'InteractiveActionHistory.cs').read_text(encoding='utf-8')
assert 'InteractiveActionHistory' in action_history and 'InteractiveActionLineKind.Active' in action_history
assert 'ShowActionProgress' in csharp_ui and 'ShowActionHistoryAsync' in csharp_ui, 'terminal action journal rendering missing'
assert "InteractiveActionLineKind.Success => '+'" in csharp_ui and "InteractiveActionLineKind.Success => '✓'" not in csharp_ui, 'success action marker must stay ASCII-safe for Windows consoles'
assert 'UpdateProgressStage.VerifyingChecksum' in csharp_program and 'UpdateApplyProgressStage.ValidatingExecutable' in csharp_program, 'update workflow must expose package verification/apply history'
assert 'history: history' in csharp_program, 'registration prompts must preserve prior action history'
assert 'Запросов кода в этой сессии' in csharp_program, 'registration must label only the locally known confirmation-request count'
assert 'registrationTelemetry.Record("get_confirm", confirmationRequestCount, requested)' in csharp_program, 'repeated get-confirm calls must keep their real request index'
assert 'ClassifyConfirmationRequestFailure' in csharp_program and 'EnterCodeWithoutRetry' in csharp_program, 'ambiguous get-confirm failures and HTTP 429 must preserve code entry without blind auto-retry'
assert 'BuildActionHistoryRows' in csharp_ui and 'WrapText(line.Text' in csharp_ui, 'action history must wrap to the current pane width'
assert 'Truncate(line.Text' not in csharp_ui, 'action history must wrap instead of truncating long entries'

# Self-update UX/reliability: the apply helper keeps the console alive, reports byte progress,
# retries transient Windows file locks, and runs the apply phase from the verified new binary.
csharp_update = (root / 'src' / 'IS74Wifi.Core' / 'GitHubUpdateClient.cs').read_text(encoding='utf-8')
assert 'UpdateTransferProgress' in csharp_update and 'BytesReceived' in csharp_update and 'TotalBytes' in csharp_update, 'update download byte progress missing'
assert 'ConsoleSession.EnsureInteractiveConsole();' in csharp_program and 'command == "update-apply"' in csharp_program, 'update helper must keep the terminal console attached'
assert 'command != "network-diagnose"' in csharp_program, 'network diagnostic must not be forwarded to an older installed build'
assert 'File.Copy(prepared.ExecutablePath, helperPath, overwrite: true);' in csharp_program, 'verified new binary must drive the apply phase'
assert 'ReplaceInstalledExecutableWithRetry' in csharp_program and 'attempts: 24' in csharp_program, 'update apply must retry transient executable locks'
assert 'Func<Task<bool>>? confirmApply = null' in csharp_program and 'ОБНОВЛЕНИЕ ГОТОВО' in csharp_program, 'interactive update must confirm handoff before launching the apply helper'
assert 'if (!WaitForProcessExit(parentPid))' in csharp_program, 'update apply helper must wait for deliberate user confirmation without a fixed timeout'
assert 'update.helper exited-before-handoff' in csharp_program and 'WaitForExit(750)' in csharp_program, 'parent must detect an apply helper that dies before handoff'
assert 'IS74W_UPDATE_RESULT' in csharp_program and 'РЕЗУЛЬТАТ ОБНОВЛЕНИЯ' in csharp_program, 'post-restart update result handoff missing'


# Local-first telemetry must never add network or disk work to the millisecond authorization race.
telemetry_models = (root / 'src' / 'IS74Wifi.Core' / 'TelemetryModels.cs').read_text(encoding='utf-8')
telemetry_trace = (root / 'src' / 'IS74Wifi.Core' / 'AuthorizationTelemetry.cs').read_text(encoding='utf-8')
telemetry_store = (root / 'src' / 'IS74Wifi.Core' / 'TelemetryStore.cs').read_text(encoding='utf-8')
telemetry_uploader = (root / 'src' / 'IS74Wifi.Core' / 'TelemetryUploader.cs').read_text(encoding='utf-8')
auth_flow = (root / 'src' / 'IS74Wifi.Core' / 'AuthorizationFlow.cs').read_text(encoding='utf-8')
push_polling = (root / 'src' / 'IS74Wifi.Core' / 'PushPollingEngine.cs').read_text(encoding='utf-8')
assert 'MailboxPollStarted' in push_polling and 'MailboxPollCompleted' in push_polling, 'mailbox race telemetry missing'
assert 'InFlightAtStart' in telemetry_models and 'MaxMailboxInFlight' in telemetry_models and 'OverlappingMailboxObserved' in telemetry_models, 'overlap telemetry fields missing'
assert 'StepTwoBeforeStepOneResponse' in telemetry_models and 'FastPathSuccess' in telemetry_models, 'fast-path outcome telemetry missing'
assert 'queue.Enqueue(serialized);' in telemetry_trace, 'completed traces must be persisted locally'
assert 'SendTelemetryBatchAsync' not in auth_flow and 'TryFlushIfDueAsync' not in auth_flow, 'authorization flow must never upload telemetry'
assert 'File.' not in push_polling, 'push polling critical path must not write telemetry to disk'
assert 'TelemetryUploadIntervalHours' in (root / 'src' / 'IS74Wifi.Core' / 'AppSettings.cs').read_text(encoding='utf-8')
assert 'TimeSpan.FromHours(6)' in telemetry_uploader, 'backlogged telemetry should respect the server upload window'
assert 'MaxUploadEvents = 64' in telemetry_models and 'MaxUploadPayloadBytes = 60 * 1024' in telemetry_models, 'telemetry transport batch must fit the external Apps Script receiver guards'
assert 'public const int Schema = 4;' in telemetry_models, 'telemetry client must emit the deployed schema-v4 contract'
assert 'TelemetryRegistrationEvent' in telemetry_models and 'registration_event' in telemetry_models, 'registration telemetry DTO missing'
registration_telemetry = (root / 'src' / 'IS74Wifi.Core' / 'RegistrationTelemetry.cs').read_text(encoding='utf-8')
assert 'get_confirm' in csharp_program and 'check_confirm' in csharp_program and 'get_token' in csharp_program and 'device_metadata' in csharp_program, 'registration stages are not recorded by the application'
assert 'anonymousStatisticsAllowed' in registration_telemetry and 'queue.Enqueue' in registration_telemetry, 'registration telemetry must remain consent-gated and local-first'
for forbidden in ['BearerToken', 'Phone', 'ConfirmCode', 'AuthId', 'UserId', 'ProfileId', 'PushMessage', 'FullMessage']:
    assert forbidden not in telemetry_models, f'sensitive field leaked into telemetry DTO contract: {forbidden}'
assert 'install-id.txt' in (root / 'src' / 'IS74Wifi.Core' / 'AppPaths.cs').read_text(encoding='utf-8'), 'stable telemetry install ID storage missing'
assert 'Guid.NewGuid().ToString("N")' in telemetry_store, 'telemetry install ID must be random and app-generated'
assert 'IS74W_TELEMETRY_URL' in csharp_runtime, 'telemetry endpoint override missing'
assert 'AKfycbx0vUU3ypA1q_Mehd7e3r4Ker3XHOsYuc1stSucHecBqy6wzlsHBFA_48kJYmdntXwlKg/exec' in csharp_runtime, 'production telemetry endpoint missing'
agent_service = (root / 'src' / 'IS74Wifi.Core' / 'AgentService.cs').read_text(encoding='utf-8')
assert 'app.Agent.CanUploadTelemetry()' in csharp_program and 'TryFlushIfDueAsync' in csharp_program, 'agent must check the actual authorization schedule before uploading'
assert 'AgentTiming.CanUploadTelemetry(' in agent_service and '!checkNetwork || IsNetworkPolicySatisfied()' in agent_service, 'telemetry safety must use persisted expiry/retry timestamps and the network policy'
assert 'delay >= TimeSpan.FromMinutes(1)' not in csharp_program, 'do not gate telemetry on the normal 15-second agent sleep'
assert 'Math.Clamp(settings.TelemetryHttpTimeoutMilliseconds, 10000, 30000)' in telemetry_uploader, 'persisted short telemetry timeouts must not strand existing users'
assert 'TelemetryUploader.GetHttpTimeout(settings)' in agent_service and 'TelemetryMaxBatchesPerFlush' in agent_service, 'the pre-auth safety window must reserve the entire configured flush budget'
assert 'longIdleEnd - now' in agent_service and 'ExpiryReminderWindow = TimeSpan.FromMinutes(5)' in agent_service, 'healthy daytime agent must sleep to the next actual deadline'
assert 'BoundSleepByBackgroundWork' in csharp_program and 'BoundSleepByBackgroundWork' in agent_service, 'idle sleep must respect update and telemetry deadlines'
assert 'NetworkChange.NetworkAddressChanged +=' in csharp_program and 'NetworkChange.NetworkAvailabilityChanged +=' in csharp_program, 'long-idle agent must respond to network changes'
assert 'AgentPowerResumeMonitor.TryRegister(' in csharp_program, 'long-idle agent must respond to Windows power resume'
agent_supervisor = (root / 'src' / 'IS74Wifi.App' / 'AgentSupervisor.cs').read_text(encoding='utf-8')
agent_process_control = (root / 'src' / 'IS74Wifi.Core' / 'AgentProcessControl.cs').read_text(encoding='utf-8')
assert 'agent-worker' in csharp_program and 'AgentSupervisor.RunAsync' in csharp_program, 'autostart agent must isolate the restartable worker behind a supervisor'
assert 'agent.worker exited' in agent_supervisor and 'GetRestartDelay' in agent_supervisor, 'agent supervisor must restart a worker that exits unexpectedly'
assert 'AgentWorkerGateName' in agent_process_control and 'SignalWorkerStop' in agent_process_control, 'agent stop control must cover both supervisor and worker'
assert 'TryRecoverAutomaticAuthorizationAgent();' in csharp_program, 'interactive menu must recover an enabled but missing background agent'
assert 'MissingNetworkFallbackInterval' in agent_service and 'PublishAutomaticLimitNotification()' in agent_service, 'overdue agent must use sparse fallback and notify on exhausted attempts'


# Campus speed test: reproduce the provider's observed standalone LibreSpeed
# measurement traffic without leaking the captured public IP or calling the
# provider's own telemetry endpoint.
speedtest = (root / 'src' / 'IS74Wifi.Core' / 'Is74SpeedTestProvider.cs').read_text(encoding='utf-8')
speedtest_models = (root / 'src' / 'IS74Wifi.Core' / 'SpeedTestModels.cs').read_text(encoding='utf-8')
telemetry_client = (root / 'src' / 'IS74Wifi.Core' / 'TelemetryClient.cs').read_text(encoding='utf-8')
speed_tools_service = (root / 'src' / 'IS74Wifi.Core' / 'CampusSpeedToolsService.cs').read_text(encoding='utf-8')
assert 'PingCount { get; init; } = 10' in speedtest, 'IS74 LibreSpeed ping count changed'
assert 'DownloadStreams { get; init; } = 5' in speedtest and 'UploadStreams { get; init; } = 3' in speedtest, 'IS74 LibreSpeed stream profile changed'
assert 'DownloadGraceTime { get; init; } = TimeSpan.FromSeconds(1.5)' in speedtest and 'UploadGraceTime { get; init; } = TimeSpan.FromSeconds(3)' in speedtest, 'IS74 LibreSpeed grace windows changed'
assert 'OverheadCompensationFactor { get; init; } = 1.06' in speedtest, 'IS74 LibreSpeed bandwidth calibration changed'
assert 'backend/garbage.php' in speedtest and 'backend/empty.php' in speedtest, 'IS74 speed-test endpoints missing'
assert 'BuildUri("backend/getIP.php"' not in speedtest and 'BuildUri("results/telemetry.php"' not in speedtest, 'native speed test must not fetch public IP or submit provider telemetry'
assert 'PacketLossPct: null' in speedtest, 'unsupported packet loss must remain unmeasured'
assert 'IProgress<SpeedTestProgress>' in speedtest_models and 'CancellationToken' in speedtest_models, 'speed-test UI progress/cancellation contract missing'
assert 'BuildRouteUri("speedtest")' in telemetry_client and 'BuildRouteUri("leaderboard")' in telemetry_client, 'speed-test and leaderboard routes must be explicit'
assert 'BuildRouteUri("telemetry")' in telemetry_client, 'queued telemetry batches must use the explicit telemetry route'
assert 'QueueSpeedTest(telemetry)' in speed_tools_service, 'completed speed tests must remain local-first when backend upload is unavailable'
assert 'LeaderboardNicknamePreferences.Normalize(nickname)' in speed_tools_service, 'leaderboard publication must use the shared nickname validator'
assert 'leaderboardcontrol' in telemetry_client, 'leaderboard lifecycle must use a dedicated control route'
assert 'telemetryQueue.Enqueue([TelemetrySerialization.Serialize(entry)])' not in speed_tools_service, 'leaderboard publication must never use delayed telemetry queue'
speed_ui = (root / 'src' / 'IS74Wifi.App' / 'InteractiveTerminalUi.SpeedTools.cs').read_text(encoding='utf-8')
assert 'CampusSpeedToolsService' in speed_ui and 'RunSpeedMeasurementAsync' in speed_ui, 'speed-tools UI is not connected to measurement core'
assert 'PublishLastSpeedResultAsync' in speed_ui and 'GetLeaderboardAsync' in speed_ui, 'speed-tools UI is not connected to leaderboard backend'
assert 'nicknamePreferences.LoadSaved()' in speed_ui and 'nicknamePreferences.TrySave(' in speed_ui, 'speed-tools menus must persist the nickname through shared preferences'
assert 'new LeaderboardNicknamePreferences(leaderboardSettings)' in (root / 'src' / 'IS74Wifi.App' / 'Program.cs').read_text(encoding='utf-8'), 'nickname preferences must be wired into speed menu'
assert 'new LeaderboardParticipationPreferences(leaderboardSettings)' in (root / 'src' / 'IS74Wifi.App' / 'Program.cs').read_text(encoding='utf-8'), 'leaderboard participation preferences must be wired into speed menu'
assert 'LeaderboardNickname' in (root / 'src' / 'IS74Wifi.Core' / 'AppSettings.cs').read_text(encoding='utf-8'), 'nickname preference missing from settings'

print('static checks: OK')
