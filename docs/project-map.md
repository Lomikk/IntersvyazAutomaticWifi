# Короткая карта проекта

Актуальное устройство кода на этапе S0 (2026-10-01), **не проект будущих переносов**.
Текущий клиент беты — C#/.NET 10. Сервер — Google Apps Script в отдельном приватном
репозитории. В S0 поведение приложения и сервера не меняется.

## С какой стороны начинать задачу

В таблице `App/` = `src/IS74Wifi.App/`, `Core/` = `src/IS74Wifi.Core/`.
Режимы относятся к `scripts/check.ps1`; [команды и ограничения](checks.md).

| Что меняем | Где сейчас владелец поведения | Проверка |
|---|---|---|
| Запуск, CLI, меню, регистрация, вызов действий | `App/Program.cs`; создание/освобождение сервисов — `App/ApplicationRuntime.cs` | App; при изменении сценариев/сервисов Full |
| Компоновка и сессия терминала | `App/InteractiveTerminalUi.cs`; примитивы кадра — `App/Ui/TerminalCanvas.cs`, геометрия — `App/Ui/TerminalLayout.cs`, framebuffer/diff/вывод — `App/Ui/TerminalOutput.cs`; общая геометрия больших списков — `App/Ui/ListWindow.cs`; история — `InteractiveActionHistory.cs` | App + ручная проверка затронутого экрана |
| Выбор сетевого адаптера | `App/Ui/AdapterSelectionScreen.cs` — только пункты/selection/scroll/кадр; список адаптеров UI получает через `AgentNetworkClient` от agent-owned `PhysicalAdapterSelection.Enumerate`, а сохранение выбранной настройки остаётся в `Program.cs` | App + `agent-network-ipc` + ручная проверка экрана |
| Снимок состояния и подробный отчёт | `App/AgentNetworkSnapshot.cs` — agent-owned snapshot сетевых путей/SSID/VPN/authorization state, сохраняемый после agent tick/IPC операции; `StatusService.cs` только объединяет этот snapshot с локальными настройками/регистрацией для UI/отчёта; live refresh идёт через `AgentNetworkClient` | App (`status-service` + `agent-network-ipc` + Python live-status) |
| Экран скорости/рейтинга | `App/InteractiveTerminalUi.SpeedTools.cs` (partial того же UI; пока содержит и действия, и состояние) | App; бизнес-правила/запросы — Full + Backend |
| Прогресс ручной авторизации | `App/ManualAuthorizationRunner.cs` отображает progress; сама операция идёт через `AgentNetworkClient` → agent-owned `AgentNetworkOperations` | App: `manual-authorization-progress` + `agent-network-ipc` |
| Авторизация и polling | `Core/AuthorizationFlow.cs`, `PushPollingEngine.cs`, `AuthorizationStateManager.cs`; инварианты — `ProtocolContract.cs` | Core |
| Виртуальные месяцы/годы планировщика | `tests/IS74Wifi.SimulationTests/` — настоящие `PathAwareAgentCoordinator`/`PathAuthorizationSchedule` с виртуальными часами и подменёнными внешними зависимостями; [руководство](virtual-simulation.md) | `--self-test`, месяц `--strict`, длинный ручной CI |
| Когда просыпается и повторяет попытку агент | `Core/AgentService.cs`, `PathAwareAgentCoordinator.cs`, `PathAuthorizationSchedule.cs`, `AgentPowerResumeMonitor.cs`; IPC — `App/AgentNetworkIpc.cs`; fallback будит существующий worker через именованный event в `AgentProcessControl.cs` | Full |
| HTTP, ошибки API, captive portal, probe | `Core/HttpTransport.cs`, `HttpClientProfiles.cs`, `Is74ApiClient.cs`, `CaptivePortalClient.cs`, `InternetConnectivityProbe.cs` | Core; подключение профилей — Full |
| Выбор адаптера, DNS, прямой маршрут | `Core/PhysicalAdapterSelection.cs`, `DirectNetworkConnector.cs`, `InterfaceDnsResolver.cs`; wiring — `App/ApplicationRuntime.cs` | Full; полевая проверка отдельно |
| Настройки, секреты, локальное состояние | `Core/AppPaths.cs`, `SettingsStore.cs`, `DpapiSecretStore.cs`, `DeviceIdentityStore.cs`, `RuntimeStateStore.cs`, `SessionMetadata.cs` | Core |
| Измерение скорости, участие/ник | `Core/Is74SpeedTestProvider.cs`, `CampusSpeedToolsService.cs`, `Leaderboard*Preferences.cs`, `LeaderboardDisplayPolicy.cs` | Core; контракт backend — также Backend |
| JSON, согласие, очередь, отправка статистики | `Core/TelemetryModels.cs`, `JsonContexts.cs`, `TelemetryStore.cs`, `TelemetryClient.cs`, `TelemetryUploader.cs`, `AuthorizationTelemetry.cs`, `RegistrationTelemetry.cs` | Full + Backend |
| Установка, автозапуск, обновления, уведомления и tray | `Core/ProgramInstallation.cs`, `WindowsAutostartService.cs`, `WindowsInstalledAppRegistration.cs`; discovery — `UpdateDiscoveryClient.cs` (Apps Script manifest first) + `GitHubUpdateClient.cs` fallback/download; App — `UpdateMaintenanceService.cs`, `WindowsNotificationService.cs`, `WindowsTrayIconService.cs`, `Program.cs`; ресурсы — `App/Assets/` | Full; backend manifest route и упаковка/ручная проверка отдельно |
| Приём событий, Sheets, рейтинг и лимиты | Приватный `IntersvyazAutomaticWifi_SERVER/Code.gs`; его README — происхождение исходника и rollout | Backend; при изменении JSON также Core |

`Program.cs` пока большой: карта обозначает существующие обязанности, а не утверждает,
что они уже разделены. Базовые `TerminalCanvas` / `TerminalLayout` / `TerminalOutput` выделены
на U1; `AdapterSelectionScreen` и общий `ListWindow` выделены на U2. Последующие контроллеры из
[плана](maintainability-audit-and-plan.md) ещё не выделены. Для небольшой задачи
достаточно её строки в таблице, связанных тестов и соответствующего контракта ниже.

## Границы, которые легко перепутать

- `ApplicationRuntime` сейчас подключает **DirectNetworkConnector** к API/portal/probe:
  auto/manual физический адаптер, DNS выбранного интерфейса, системный DNS как
  fallback разрешения имени. TCP при этом не переключается скрыто на VPN.
  Явный режим «системный маршрут» отключает этот connector.
  **CachedDnsConnector + HostAddressCache** — другой, старый persisted cached-IP-first
  эксперимент: код и тесты остались, в runtime он не подключён.
- Локальный `StatusService.ReadLocalSnapshot()` не выполняет HTTP и не создаёт `ApplicationRuntime`, но сохраняет прежние локальные нормализации: `SettingsStore.Load()` может создать default `settings.json`, а `UpdateMaintenanceService.LoadState()` — очистить и сохранить устаревшее состояние доступного обновления. Сетевые побочные эффекты находятся только в явно переданных delegate для `RefreshNetworkSnapshotAsync`/подробного отчёта.
- Авторизационная сеть имеет одного владельца: interactive/CLI код не перечисляет адаптеры/SSID/VPN, не читает `PathAuthorizationState` и не вызывает `NetworkPathDiagnostics`, `Internet.ProbeAsync`, `ManualAuthorizationPathSelector` или `PathAuthorization.RunAsync` напрямую. Он использует `AgentNetworkClient`; agent возвращает полный network/auth snapshot и сохраняет его для локального первого кадра UI. Long-lived worker обслуживает named pipe, а при выключенном/недоступном фоне запускается короткоживущий `agent-network-worker` той же версии. `probe-only` сохраняет диагностический контракт без stepOne/stepTwo.
- Авторизация не должна зависеть от скорости отрисовки: callback кладёт прогресс
  в очередь, App читает её отдельно. Не добавлять Console/HTTP/disk I/O в callback.
- После pre-step отказа timer shot coordinator выполняет одну bound-probe для свежего
  статуса. Подтверждённый Internet на/после расчётной границы отменяет ненужные
  portal retries, **но не 3-минутное наблюдение**. С T−30 c до T+3 мин
  `PathAuthorizationSchedule` применяет адаптивный cadence; после финальной probe
  путь становится event-driven. При Unreachable/Ambiguous в окне действует
  ограниченный retry; после T+3 мин одна финальная probe, однократное уведомление
  при неопределённости и ожидание события Windows. Только успешная авторизация
  создаёт новую 24-часовую границу; изменение binding требует свежей probe.
  Подробный контракт — [authorization-edge-window.md](authorization-edge-window.md).
  App отслеживает также уровни Windows connection profiles: глобальный Internet
  через VPN не должен маскировать изменение физического профиля. Address/availability
  и resume будят coordinator с признаком изменения сети; IPC completion только
  пересчитывает deadline, без второй probe. После T+3 мин без события Windows
  event-driven путь может не обнаружить поздний captive до явного refresh.
  Agent-owned snapshot публикуется также после частичной ошибки операции, под
  теми же gates; ошибка публикации не маскирует исходную ошибку.
- `NextCheckUtc` — срок начала операции, а canonical snapshot обновляется после её
  окончания. `StatusService` локально читает занятость network operation gate:
  UI показывает «проверка...» вместо «просрочено» для уже наступившего срока,
  пока gate удерживается. Это transient UI state, без HTTP и записи нового snapshot.
- Согласие на анонимную статистику и явное участие в публичном рейтинге — разные
  решения пользователя. Отложенный `speed_test` допустим; отложенная публикация
  или команда участия — нет. Legacy JSON/schema 4 и path-aware schema 5 проверяются общими синтетическими fixtures.
- Приватность репозитория сервера не защищает публичный endpoint. `install_id`
  — не аккаунт; обход per-ID лимитов сменой ID оставлен осознанно. Не расширять
  небольшую задачу до новой системы аутентификации.
- `IS74Wifi.ps1`, `agent.ps1`, `src/IS74Wifi.psm1` — историческая PowerShell/reference
  реализация со своими регрессиями. `experiments/`, `prototypes/` и документы
  экспериментов — исследования, иногда с реальными сетевыми действиями, не check suite.

## Какие документы читать

- [checks.md](checks.md) — воспроизводимая проверка, prerequisites, безопасные границы.
- [protocol.md](protocol.md) — исследованные wire-инварианты авторизации.
- [direct-network.md](direct-network.md) — нынешний маршрут C# и его ограничения.
- [network-path-roadmap.md](network-path-roadmap.md) — следующая функциональная модель
  multi-path: per-path probe/expiry, NAT и VPN; пока это план, а не текущий runtime.
- [telemetry.md](telemetry.md), [speedtest.md](speedtest.md),
  [speed-tools-ui.md](speed-tools-ui.md) — текущие контракты и поведение скорости/рейтинга.
- [maintainability-audit-and-plan.md](maintainability-audit-and-plan.md) — аудит,
  целевые границы и карточки будущих этапов; статус смотрите в реестре в конце.
- `csharp-migration-roadmap.md`, `csharp-auth-timing-audit.md`, `production-mvp.md`
  — история решений/аудитов, не очередь актуальных задач. При расхождении сначала
  сверить реализацию и тест, затем поправить относящийся к изменению документ.

Серверный README и `AGENTS.md` читаются в приватном checkout. Публичные Node-тесты
исполняют переданный `Code.gs` в VM с таблицами в памяти; исходник не копируется
в этот репозиторий. Локальные проверки не доказывают, что Apps Script deployment
обновлён. Коммит, push и deployment — три разных действия.
