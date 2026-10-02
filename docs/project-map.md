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
| Компоновка и сессия терминала | `App/InteractiveTerminalUi.cs`; примитивы кадра — `App/Ui/TerminalCanvas.cs`, геометрия — `App/Ui/TerminalLayout.cs`, framebuffer/diff/вывод — `App/Ui/TerminalOutput.cs`; общая геометрия больших списков — `App/Ui/ListWindow.cs`; история — `InteractiveActionHistory.cs`, снимок статуса — `InteractiveStatusSnapshot.cs` | App + ручная проверка затронутого экрана |
| Выбор сетевого адаптера | `App/Ui/AdapterSelectionScreen.cs` — пункты, selection/scroll, клавиши и кадр; `InteractiveTerminalUi.cs` — единственный reader клавиатуры и compact fallback; перечисление/сохранение остаются в `Program.cs`/Core | App + ручная проверка экрана |
| Экран скорости/рейтинга | `App/InteractiveTerminalUi.SpeedTools.cs` (partial того же UI; пока содержит и действия, и состояние) | App; бизнес-правила/запросы — Full + Backend |
| Прогресс ручной авторизации | `App/ManualAuthorizationRunner.cs`, подключение в `Program.cs` | App: исполняемый контракт runner + структурная проверка подключения |
| Авторизация и polling | `Core/AuthorizationFlow.cs`, `PushPollingEngine.cs`, `AuthorizationStateManager.cs`; инварианты — `ProtocolContract.cs` | Core |
| Когда просыпается и повторяет попытку агент | `Core/AgentService.cs`, `AgentPowerResumeMonitor.cs` | Core |
| HTTP, ошибки API, captive portal, probe | `Core/HttpTransport.cs`, `HttpClientProfiles.cs`, `Is74ApiClient.cs`, `CaptivePortalClient.cs`, `InternetConnectivityProbe.cs` | Core; подключение профилей — Full |
| Выбор адаптера, DNS, прямой маршрут | `Core/PhysicalAdapterSelection.cs`, `DirectNetworkConnector.cs`, `InterfaceDnsResolver.cs`; wiring — `App/ApplicationRuntime.cs` | Full; полевая проверка отдельно |
| Настройки, секреты, локальное состояние | `Core/AppPaths.cs`, `SettingsStore.cs`, `DpapiSecretStore.cs`, `DeviceIdentityStore.cs`, `RuntimeStateStore.cs`, `SessionMetadata.cs` | Core |
| Измерение скорости, участие/ник | `Core/Is74SpeedTestProvider.cs`, `CampusSpeedToolsService.cs`, `Leaderboard*Preferences.cs`, `LeaderboardDisplayPolicy.cs` | Core; контракт backend — также Backend |
| JSON, согласие, очередь, отправка статистики | `Core/TelemetryModels.cs`, `JsonContexts.cs`, `TelemetryStore.cs`, `TelemetryClient.cs`, `TelemetryUploader.cs`, `AuthorizationTelemetry.cs`, `RegistrationTelemetry.cs` | Full + Backend |
| Установка, автозапуск, обновления, уведомления | `Core/ProgramInstallation.cs`, `WindowsAutostartService.cs`, `WindowsInstalledAppRegistration.cs`, `GitHubUpdateClient.cs`; App — `UpdateMaintenanceService.cs`, `WindowsNotificationService.cs`, `Program.cs` | Full; упаковка/ручная проверка отдельно |
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
- Авторизация не должна зависеть от скорости отрисовки: callback кладёт прогресс
  в очередь, App читает её отдельно. Не добавлять Console/HTTP/disk I/O в callback.
- Согласие на анонимную статистику и явное участие в публичном рейтинге — разные
  решения пользователя. Отложенный `speed_test` допустим; отложенная публикация
  или команда участия — нет. JSON/schema 4 проверяется общими синтетическими fixtures.
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
