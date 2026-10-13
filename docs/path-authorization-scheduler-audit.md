# Аудит path-aware scheduler

> **Статус исторического аудита.** Разделы ниже описывают диагностику и решения alpha.42–alpha.43, в том числе отменённое правило «первая успешная Internet probe после T прекращает наблюдение». Актуальный согласованный контракт планировщика — [authorization-edge-window.md](authorization-edge-window.md). Исторические результаты синтетических запусков не являются контрактом текущего runtime.

Дата: 2026-10-13. Проверенная база: `4414334` (`main` на момент аудита).
Сравнение с `v0.1.0-alpha.41`: рассматриваемые Core state machine и worker loop
совпадают; новое отображение дополнительно передаёт `EdgeWatchActive` в snapshot.
На первом этапе аудита runtime не изменялся. В продолжении реализовано исправление,
описанное ниже; первоначальный guard-кандидат не использован. Ниже только синтетические сценарии;
живые журналы, адреса и пользовательское состояние в репозиторий не включены.

## Реализованное исправление alpha.42 (история)

По запросу владельца внесён вариант, сохраняющий pre-step recovery:

- Первый предпочтительный timer shot по-прежнему начинается без предварительной
  Internet probe. После `RetryableBeforeStepOne` этого shot выполняется ровно одна
  bound-probe, если транспортная привязка пути сохранилась. Она обновляет
  `LastProbeUtc`/status, включая Unreachable при отказе прямого маршрута.
- Probe идёт после завершения критической операции, внутри оставшегося backoff.
  Её HTTP timeout ограничен меньшим из 2 секунд и остатка до retry (минимум 100 ms).
  Если операция всё же пересекла retry deadline, следующий tick получает 100 ms,
  а не новый минутный интервал от только что выполненной probe.
- Существующие задержки 1/2/5/15/30/60 секунд и send budget сохранены. Pending
  pre-step retry после известной границы обслуживается независимо от результата
  этой probe и текущего предпочтения Windows. Это продолжение уже начатой попытки,
  а не новая отправка только по признаку Unreachable. Snapshot и worker используют
  один `PathAuthorizationSchedule.IsPreStepRetryPending`.
- `AlreadyAuthorized`/Success завершают retry существующими мутациями manager;
  нормальный edge-watch сохраняет tapered cadence и captive confirmation.
  Состояние не переводится в бессрочное ожидание captive после pre-step failure.
- Worker tick, IPC refresh и manual runner используют
  `AgentNetworkOperations.RunAndCaptureAsync`: после частичной ошибки snapshot
  публикуется под operation gates. Вторичная ошибка capture логируется отдельно,
  исходная ошибка сохраняется. Повторная запись при ошибке выполняется один раз.
- Long-lived worker ждёт именованный AutoReset event. Short-lived fallback
  сигналит его после refresh/probe-only/authorize, включая ошибки после частичных
  изменений; сигнал посылается после освобождения operation gate. Повторы
  объединяются, открытие handle не сбрасывает сигнал. Read-only adapters не будит
  scheduler. System wake timers и periodic polling не добавлены.
- Локальный preflight log содержит вид отказа `dns/tcp/timeout/direct-route`, тип
  исключения, Winsock error и длительность; данные не требуют повторного DNS/TCP
  запроса и не добавлены в backend telemetry.

Новая группа `path-pre-step-recovery` проверяет 30 минут failures, одну probe на
попытку, прежний backoff, отсутствие requests при ранних wakes, новый LastProbeUtc,
рестарт/смену preferred, восстановление портала при Unreachable, DNS rollback,
первый shot после pre-expiry failure и overdue retry после долгой probe. Существующий
`path-edge-watch` проверяет продолжение нормального AlreadyAuthorized/captive flow.
IPC contracts проверяют wake ожидания старого deadline, видимость обновлённого
deadline, coalescing, отсутствие wake для adapters и публикацию snapshot при ошибке
с сохранением исходного исключения. Direct-network contracts различают DNS/TCP
отказы и проверяют отсутствие дополнительных dial в диагностике.

Ограничение: одна дополнительная HTTP probe выполняется на неудачный timer retry;
это небольшой дополнительный сетевой расход. HTTP timeout ограничивает запрос,
но не даёт жёсткой гарантии wall-clock latency при задержках ОС/синхронного DNS/диска.
Причина недоступности реального портала не исправляется scheduler-патчем: новый
лог нужен для её определения. Live authorization и установка на ПК не выполнялись.

Остальные исходные разделы сохраняют диагноз и отвергнутый кандидат как историю
аудита; актуальное уточнение поведения описано ниже.

## Уточнение после alpha.42: Internet прекращает speculative retry

Alpha.42 сохраняла retry даже после свежей успешной Internet probe. Это повторяло
попытки к порталу по старой расчётной границе, хотя потеря доступа не подтверждалась.
`PathAuthorizationSchedule.HasObservedInternetPastExpiry` теперь отличает такую
границу от реального captive: `Status=Internet && LastProbeUtc >= ExpectedExpiryUtc`.
`GetDelay` возвращает ожидание, `GetNextCheckUtc` — null; `ShouldFirePreferredTimerImmediately`
не повторяет shot. `RecordProbe`/`Observe` очищают retry, pre-step failure count и
edge-watch. Expiry, дата успеха и потраченный send budget сохраняются как история;
новая граница возникает только после MarkSuccess. Изменение binding сбрасывает
достоверность предыдущей probe, чтобы старый Internet не скрывал отказ нового пути.

После AlreadyAuthorized первая probe остаётся; если она подтверждает Internet,
минутный edge-watch прекращается. При Unreachable/Ambiguous/Captive сохраняются
существующие cadence, retry budget и captive confirmation. `CanUploadTelemetry`
больше не блокируется истёкшей оценкой при подтверждённом Internet.

App различает изменения уровней всех Windows connection profiles, даже когда VPN
сохраняет глобальный InternetAccess. Повторные callbacks без изменения игнорируются;
address/availability/resume передают coordinator признак изменения сети. IPC completion
лишь будит worker для пересчёта deadline и не добавляет вторую probe.
Это ожидание потока, не system wake timer. Background maintenance может будить worker,
но обычный tick такого пути не выполняет HTTP probe/authorization.

Цена отказа от periodic probes: Windows/NCSI должен обнаружить позднюю потерю доступа.
Если события нет (например, профиль за NAT/VPN не изменился), верхней границы задержки
авторизации нет до следующего события или explicit refresh. Per-profile tracking
уменьшает маскировку VPN, но не гарантирует событие. Контракты моделируют событие;
полевое поведение Windows этим не проверено.

`path-edge-watch` и `path-pre-step-recovery` проверяют 30 виртуальных минут без
повторных запросов при Internet, включая restart и позднее событие. Отдельно сохраняются
30 минут pre-step recovery при недоступном direct route. `path-authorization-state`
проверяет инвалидирование старой Internet probe при смене transport binding.

## Вывод

### Дополнение: начало проверки и её завершение

В alpha.42 устранено starvation probes, но осталась ошибка представления deadline.
`NextCheckUtc` указывает на начало worker операции; snapshot меняется только после
её завершения. Baseline + TCP preflight + post-failure probe может занимать больше
пяти секунд даже при своевременном wake. UI считал прежний snapshot просроченным,
не учитывая выполняющуюся операцию. Это не доказательство пропущенного scheduler wake.

Исправленный `StatusService` читает владельца существующего named network gate
вместе с локальным machine state. Если срок наступил и gate занят, колонка показывает
«проверка...». Будущий deadline другого пути остаётся видимым. Когда gate свободен,
действительно устаревший snapshot по-прежнему показывает «просрочено». Gate указывает
занятость операции, а не гарантирует продвижение HTTP: зависание требует диагностики
по логу. Само UI-исправление не меняет deadline или canonical/IPC schema.

Контракты проверяют операцию на T+6 секунд после срока, завершение с новым deadline,
будущий deadline другого пути, реальную занятость тестового gate и отсутствие
записи snapshot при чтении transient статуса. Локальный log теперь также отмечает
`agent.ipc refresh requested`, чтобы отличать explicit refresh/probe-only от scheduler.

При этом повторный Full выявил дефект изоляции существующих проверок:
`agent-policy` вызывал `WindowsAutostartService.Enable(startNow:false)` с отдельным
Run value, но метод всё равно останавливал production agent через общий process
owner. Fixture теперь подставляет stop delegate и проверяет его вызов; production
stop behavior сохранён. PowerShell reference tests также импортируют модуль с
временным data root: их ранние HTTP/state записи больше не попадают в рабочие файлы.

Подтверждён дефект **до** `AlreadyAuthorized`: повторные pre-step failures
предпочтительного пути могут бесконечно обходить его Internet/captive probe.
При этом worker продолжает сохранять свежие snapshots и retry deadlines,
но копирует в них старый `Status=Internet`. Свежий `CapturedAtUtc` не доказывает
свежесть сетевого наблюдения.

Для штатной ветки `AlreadyAuthorized` добавленный сценарий с 30 минутами
виртуального времени не выявил застревания или лишних отправок. Состояние
`Internet + expired ExpectedExpiryUtc + EdgeWatchActive` само по себе допустимо:
пока probe подтверждает Internet, наблюдение не имеет лимита в 10 или 30 минут.
Новый 24-часовой срок создаётся только после успешной авторизации.

## Путь состояния

1. `AuthorizationStateManager.MarkSuccess` записывает `LastAuthUtc` по серверной
   дате (или часам клиента) и `ExpectedExpiryUtc = authorizedAt + AuthWindowHours`,
   сбрасывает бюджет/retry/watch. `PathRuntimeStateStore.Save` передаёт это в
   `PathAuthorizationStateStore.SaveRuntime`: `LastSuccessfulAuthUtc`, expiry,
   attempts и watch сохраняются для одного пути. `SaveRuntime` не обновляет
   `LastProbeUtc`; это время отдельного path probe.
2. `PathAuthorizationSchedule.GetDelay` будит известный Internet-путь за 10 секунд
   до expiry, затем рассчитывает deadline от `LastProbeUtc`. До expiry ожидание
   дополнительно ограничено самой границей. `AgentService` делегирует scheduling
   и ticks в `PathAwareAgentCoordinator` — legacy-ветка не является runtime владельцем.
3. В `TickAsync` предпочтительный Internet-путь с наступившей границей может
   попасть в `ShouldFirePreferredTimerImmediately` и `RunAuthorizationAsync`
   **до** цикла probes. `Force=true` убирает начальный Internet gate из flow,
   но baseline API и TCP preflight портала всё равно предшествуют `stepOne`.
4. `AuthorizationFlow` резервирует send через `RegisterStepOneSend`. Ответ
   `AlreadyAuthorized` отменяет mailbox polling и вызывает `MarkAlreadyAuthorized`.
   Для automatic/retry включается `EdgeWatchActive`; expiry не переносится.
   Первая automatic попытка не создаёт слепого retry deadline. Противоречивый
   captive + `AlreadyAuthorized` при Retry получает backoff 15/30/60 секунд.
5. Следующие ticks обходят timer fast path из-за `EdgeWatchActive`. `RecordProbe`
   сохраняет статус и `LastProbeUtc`. Internet не вызывает новую авторизацию.
   Captive требует второго captive probe; timeout/Unreachable не считается captive.
6. После двух captive и истечения backoff выполняется Retry. Свежий код приводит
   к `stepTwo`, принятие которого вызывает `MarkSuccess`; затем flow отдельно
   подтверждает Internet. При потерянном ответе stepTwo подтверждённое восстановление
   также может вызвать `MarkSuccess`. Эти wire-сценарии покрывает `authorization-flow`.

## A. Повторный timer fast path лишает путь probes — подтверждено исполнением

Владельцы: `PathAwareAgentCoordinator.TickAsync` и
`ShouldFirePreferredTimerImmediately`; `AuthorizationStateManager.SavePreStepFailure`.

Синтетическое воспроизведение:

1. Один предпочтительный физический путь: Internet, expiry наступил,
   `AutomaticStepOneAttempts=0`, `EdgeWatchActive=false`.
2. Authorization runner моделирует недоступность baseline/портала **до** отправки:
   вызывает настоящий `MarkPreStepFailure` и возвращает `RetryableBeforeStepOne`.
3. Переводить `TimeProvider` на каждый `GetSleepDelay` и вызывать `TickAsync`.

`SavePreStepFailure` увеличивает `PreStepFailureCount`, но правильно не расходует
send budget и не меняет Internet status. После каждого retry deadline
`ShouldFirePreferredTimerImmediately` снова видит нулевой send budget, запускает
авторизацию и возвращается из tick до probes. Это может продолжаться без ограничения.
Повторные задержки составляют 1/2/5/15/30/60 секунд плюс длительность операции.
Обычные сетевые wake-события не устраняют этот обход.

Результат отдельного синтетического harness на исходном Core: за 30.88 виртуальных
минут **35 auth calls, 0 probes, 0 sends**, возраст последней пробы 1854 секунды,
статус Internet, watch выключен. Harness не обращается к реальным адаптерам/API.

Минимальный кандидат для `ShouldFirePreferredTimerImmediately` — после проверки
наличия expiry исключить повторную timer-ветку, если на/после этой границы уже
был pre-step failure:

```csharp
if (pathState.PreStepFailureCount > 0 && pathState.LastAttemptUtc >= expiry)
{
    return false;
}
```

Первый timer shot остаётся без предварительного probe; после отказа обычный цикл
проверяет путь и повторяет авторизацию только при подтверждённом captive, соблюдая
уже сохранённый backoff. Ранний pre-step failure до текущей границы не отнимает
первый shot на самой границе. На копии coordinator в локальном harness этот
кандидат дал **1 auth call, 107 probes, 0 sends за 30 минут** при постоянном Internet.
Кандидат не применён к runtime и ещё требует регрессий: captive recovery,
DNS rollback резервирования, pre-expiry failure, рестарт и второй физический путь.

### Побочные эффекты кандидата — уточнение после дополнительной проверки

Кандидат устраняет starvation, но результат «107 probes вместо 35 auth calls»
не доказывает снижение нагрузки: это разные операции. Возле границы он может
чаще будить поток и выполнять HTTP probes по существующему tapered cadence.
После пяти минут при стабильном Internet остаётся одна probe в минуту вместо
повторной baseline/preflight операции. Число реальных HTTP запросов в первоначальном
harness не измерялось: runner был подменён.

Первый timer shot не задерживается. Повторная авторизация после pre-step failure
теперь требует двух captive observations. Это добавляет время probes и 500 ms
confirmation delay; после пяти минут обнаружение нового captive может ждать
до следующей минутной пробы. Если confirmation снова Unreachable или probe
ошибочно остаётся Internet, восстановленный портал сам по себе не вызывает retry.
Поэтому проверка восстановления baseline/портала и ненадёжного probe обязательна.

Дополнительное исполнение кандидата показало, что после 30 минут Internet
`NextAutomaticRetryUtc` всё ещё присутствует, хотя давно просрочен, а
`CanUploadTelemetry()` возвращает **false**. `RecordProbe` не убирает retry;
проверка `retry - now <= 1 minute` включает и прошлые deadlines. Более того,
путь с expired expiry и EdgeWatchActive=false не получает обычное разрешение
позднего edge-watch на telemetry. Это существующие правила, с которыми кандидат
взаимодействует неоптимально. Просто вызвать `ClearPreStepFailure` нельзя:
это уберёт и признак отказа, снова включив timer fast path. Перед применением
кандидата необходимо определить завершение pre-step retry/watch состояния и
проверить, что разрешение фоновой работы сохраняет защиту окна авторизации.

Ни этот guard, ни публикация snapshot после ошибки, ни IPC event не добавляют
system wake timers или запрет сна: существующий host использует WaitAny и
уведомление **после** resume. Межпроцессный wake означает пробуждение ожидающего
потока уже работающего worker. Для fallback достаточно одного сигнала после
операции, с объединением повторных сигналов; отдельный polling не нужен.
Snapshot capture не делает HTTP, но добавляет чтение адаптеров/state и запись
на диск после ошибочной операции. Его собственная ошибка не должна запускать
немедленные бесконечные повторные попытки или скрывать исходное исключение.

Это исправляет голодание probes; причина недоступности портала — отдельный вопрос.
`DirectNetworkConnector.CanReachPortalAsync` сейчас сводит DNS/TCP/timeout к `false`,
а `AuthorizationFlow` пишет общий `portal-unreachable`. По этой записи нельзя
различить DNS, VPN filtering, отказ TCP и timeout. Для следующей диагностики нужен
тип отказа/этап/длительность в локальном логе без секретов, а не изменение таймеров.

## B. Состояние пути и canonical snapshot не являются общей транзакцией

Владельцы: `Program.RunAgentWorkerCoreAsync`, `AgentNetworkOperations.RefreshAsync`,
`CaptureAndPersistSnapshot`, `NetworkPathDiagnosticsService.InspectAsync`.

- Worker вызывает capture только **после успешного возврата** `Agent.TickAsync`.
  Если после `Observe`/`RecordProbe`/изменения auth state возникнет исключение,
  catch запишет `agent.tick error`, но capture будет пропущен.
- `RefreshAsync` также не имеет `finally` для capture: например, diagnostics уже
  сохранил первый путь, а probe второго пути или последующий Tick бросил исключение.
  IPC завершится с ошибкой и wake, но snapshot пока останется прежним.
- `AuthorizeNowAsync` имеет capture в `finally` вокруг самого runner. Это снижает
  риск для manual flow, но не устраняет отказ самого capture, перечисления адаптеров
  или записи JSON. Capture перед сохранением читает часы, адаптеры, WinRT hints и state.
- Долгая незавершённая операция тоже оставляет старый snapshot до завершения.
  Atomic temp-file replace в `JsonFileStore` защищает отдельный файл, не пару файлов.

Статически подтверждён путь частичной публикации; fault injection в настоящий host
в этом аудите не проводился. Для минимального исправления capture нужно пытаться
выполнить и после исключения из tick/refresh, пока удерживаются operation gates,
отдельно логируя ошибку snapshot, не маскируя исходную ошибку. Невозможность записи
на диск это не исправит. Тест: изменить первый путь, бросить исключение на втором,
проверить новый snapshot и сохранение исходной ошибки.

## C. NextCheckUtc и sleep/wake

`GetNextCheckUtc = now + GetDelay`, а минимальный delay равен 100 ms. При успешном
capture неизменные часы не дают deadline в прошлом относительно `CapturedAtUtc`.
Но это **прогноз**, а не подтверждение будущего выполнения:

- snapshot живёт до следующего capture; во время просроченной/долгой операции
  его deadline закономерно становится прошлым;
- повторяющиеся исключения до capture оставляют старый deadline надолго,
  хотя worker ловит ошибки и продолжает выполнять цикл;
- после capture worker последовательно ожидает update check и telemetry flush.
  `networkWake` прерывает `WaitAny`, но не эти уже выполняющиеся await.
  Поэтому cadence не является верхней границей задержки под нагрузкой;
- fresh capture сдвигает deadline, но может содержать старое наблюдение из случая A.
  В snapshot нет `LastProbeUtc`, и статус UI не различает эти два вида свежести.

Обычный IPC в long-lived worker корректно вызывает `commandCompleted` в `finally`
после освобождения `operationGate`. `AutoResetEvent.Set` до входа в `WaitAny`
сохраняет сигнал, поэтому обычной гонки «рассчитали delay, потеряли wake» здесь нет.
После wake следующая итерация и новый sleep читают persisted state заново.

**Отдельный разрыв в fallback IPC:** `AgentNetworkClient.ExecuteWithAgentAsync`
может запустить short-lived worker после timeout подключения к живому агенту.
`Program.RunAgentNetworkWorkerAsync` вызывает `RunSingleAsync` без
`commandCompleted`. Его cross-process gate сериализует запись, но не будит
long-lived scheduler. Если основной worker уже пересчитал ожидание после предыдущей
команды, а fallback позже записал более ранний retry deadline, основной worker
может ждать прежний срок до другого события. Это статический сценарий, не диагноз
конкретного пользовательского эпизода. Нужен отдельный тест такого порядка операций;
минимальное направление исправления — межпроцессный wake по завершении fallback,
без periodic polling и без запуска ещё одного постоянного scheduler.

## D. Tapered cadence и тесты

При defaults `AgentTiming.GetEdgeProbeInterval` задаёт 500 ms в пределах ±10 секунд,
2 секунды до +30 секунд, 5 секунд до +5 минут, затем 1 минуту. В path-aware schedule
интервал считается от **завершения** последнего probe. Реальная частота дополнительно
включает HTTP, файловые операции, diagnostics и фоновые задачи. При пересечении
порога taper очередной wake может стать ранним: tick пересчитает больший интервал
и перенесёт probe. Это разовый переход диапазона, а не остановка наблюдения.

Старые `agent-policy` проверки AlreadyAuthorized/edge-watch в основном проверяют
legacy `AgentService` без `pathAware`. Старый `path-aware-agent` использует runner,
который всегда завершает Success; его `MutableTimeProvider` фактически не меняет время.
IPC contracts проверяют transport и факт callback, но не спящий worker с новым deadline.

Новая группа `path-edge-watch` проводит **одну непрерывную последовательность**:
timer → AlreadyAuthorized → 30 минут Internet по настоящим coordinator deadlines →
пересоздание coordinator после 10 минут → captive + confirmation → Retry с повторным
AlreadyAuthorized → backoff → Retry + MarkSuccess. Проверяются все четыре интервала,
продвижение `LastProbeUtc`, совпадение рассчитанного NextCheck и sleep, отсутствие
лишних sends, сохранение expiry в watch и сброс watch после успеха.

Clock виртуализирует длительные ожидания; два guard confirmation delay остаются
настоящими (суммарно примерно 1 секунда), поскольку production `Task.Delay` не
получает TimeProvider. HTTP runner подменён; мутации выполняют настоящие manager/store.
Этот тест не выдаётся за end-to-end test процесса, IPC или реального stepTwo.

Для полного host-сценария ещё нужны: snapshot capture с внедряемыми часами/адаптерами,
ошибка после частичной записи, смена deadline через IPC во время sleep, завершение
fallback после пересчёта sleep, и восстановление после pre-step failure на протяжении
30 виртуальных минут. Если требуется виртуализировать и guard/settling delays,
существующий TimeProvider нужно передать в соответствующие `Task.Delay`, сохранив
их длительности; новый scheduling framework для этого не требуется.

При запуске suite найден существующий дефект fixture `status-service`: проверка
требовала «приблизительно», но переданный snapshot имел флаг approximate=false.
Исправлен только флаг синтетического fixture, без изменения status runtime.

## Проверка

Команда: `./scripts/check.ps1 -Mode Full -ServerPath <private checkout>/Code.gs -Configuration Release`.
Итог: **PASS**, включая C#, Python, wrapper, PowerShell 7/5.1 и три backend suite.
Остались три существующих CS8604 warning в `InteractiveTerminalUi.SpeedTools.cs`.
В первом запуске также однократно упал существующий early-stepTwo тест;
его отдельный повтор и финальный Full прошли, причина первого сбоя не установлена.
Live authorization, agent запуск,
изменение установленной копии, push/release/deploy в этот аудит не входят.
