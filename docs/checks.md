# Проверки без рабочего Wi-Fi и сервера

Из корня checkout, в PowerShell 7:

```powershell
# UI/меню/отрисовка; не запускает интерактивное приложение
./scripts/check.ps1 -Mode App

# Любые правила, хранилища, HTTP или фоновые сценарии Core
./scripts/check.ps1 -Mode Core

# Только приватный backend: два Node-контракта, без .NET и Google
./scripts/check.ps1 -Mode Backend -ServerPath ../IntersvyazAutomaticWifi_SERVER/Code.gs

# Полный набор исходников перед передачей результата / подготовкой сборки
./scripts/check.ps1 -Mode Full -ServerPath ../IntersvyazAutomaticWifi_SERVER/Code.gs -Configuration Release

# Для публичного checkout/CI, где приватного репозитория нет
./scripts/check.ps1 -Mode Full -SkipBackend -Configuration Release
```

Последняя команда явно пишет `SKIPPED` и **не подтверждает исправность сервера**.
Без `-ServerPath` или явного `-SkipBackend` режим Full завершается ошибкой до сборки.
Путь не угадывается; относительный путь считается от текущего каталога вызывающего.
Из другого каталога вызывайте wrapper по полному пути.

## Что входит

| Режим | Проверки |
|---|---|
| App | Сборка App и contract tests; исполняемые `manual-authorization-progress`, `terminal-ui` и `status-service`; Python static/UI |
| Core | Сборка contract project (он с U1 ссылается и на App для тестов internal UI); весь C# suite, включая Core-сценарии, сериализацию и общие JSON fixtures |
| Backend | `leaderboard_server_contract.cjs`, `server_ingestion_contract.cjs` и `update_manifest_server_contract.cjs` с явным приватным исходником |
| Full | App + весь C# suite + Python + wrapper; parse/import/critical-path PowerShell reference на 7 и 5.1; синтаксис Node suites; Backend, кроме явно указанного пропуска |

App — сокращённый проход для отображения и input. Изменения настроек, регистрации,
установки, HTTP и других бизнес-сценариев нельзя проверять только им: выбирайте Full.
Для изменения wire-контракта нужны обе стороны — C# и Backend.

Wrapper прекращает работу при **первой** неудачной команде и возвращает exit code 1.
Поздние проверки в этом случае не выполнены. Тест `tests/check_runner_contract.ps1`
проверяет это через настоящий неуспешный дочерний процесс, а также выбор режимов,
отсутствующий сервер, явный пропуск и запуск из другого каталога. При запуске через
Full он также проверяет ошибку C# suite при фильтре, не выбравшем ни одного теста.
Для отдельного запуска этого теста нужны PowerShell 7 и Node.js.

## Окружение и выходные файлы

- Windows + PowerShell 7 (`pwsh`); полноценный C# suite использует Windows DPAPI,
  WLAN и временные тестовые ключи HKCU. В ограниченной песочнице это может требовать
  разрешения. Ошибка доступа — не повод молча исключать тесты.
- Autostart fixture использует отдельную Run-запись и подменённый stop delegate:
  тест не останавливает рабочий агент. PowerShell reference suite импортирует модуль
  с временным LOCALAPPDATA, чтобы ранние HTTP/state тесты не писали в рабочий лог/state.
- .NET SDK 10 — App/Core/Full (целевой framework указан в `.csproj`).
- Python 3 (`python` в PATH) — App/Full; сторонние Python-пакеты не нужны.
- Node.js 22+ (`node` в PATH) — Backend/Full; `npm install` не нужен.
- Встроенный Windows PowerShell 5.1 (`powershell.exe`) — Full.

По умолчанию выполняется restore во время build; для NuGet может потребоваться сеть.
После успешного restore можно добавить `-NoRestore`. Это не пропуск сборки/тестов.
`-Configuration` — Debug по умолчанию или Release. `-List` показывает план команд,
**ничего не исполняет и не считается проверкой**.

Сборки лежат в игнорируемом `artifacts/check/<Configuration>/app` и `tests`;
suite запускается из только что собранного DLL, не из старого обычного `bin`.
Это позволяет не трогать EXE, открытый пользователем в другом каталоге.
Не запускайте одновременно два wrapper с одинаковой конфигурацией в одном checkout.

Точечный повтор после сборки (не заменяет Full):

```powershell
dotnet ./artifacts/check/Debug/tests/IS74Wifi.ContractTests.dll --filter=authorization-flow
python ./tests/ui_live_status_contract.py
pwsh -NoProfile -File ./tests/check_runner_contract.ps1
```

C# фильтр — подстрока имени группы; отсутствие совпадений возвращает ошибку.
Список групп — в начале `tests/IS74Wifi.ContractTests/Program.cs`.
Группа `path-edge-watch` моделирует AlreadyAuthorized → Internet → 30 минут ожидания
сетевого события без HTTP, перезапуск coordinator и поздний captive/retry по событию;
HTTP подменён, два коротких confirmation delay остаются настоящими.
Группа `path-pre-step-recovery` проверяет 30 минут отказов до stepOne, сохранение
backoff/budget, свежие probes и восстановление после рестарта/Unreachable, а также
прекращение retry после Internet и отсутствие запросов за 30 виртуальных минут.
`agent-network-ipc` включает публикацию snapshot после ошибки и ожидание
именованного wake event; тест использует уникальное имя и не будит рабочий агент.
В приватном репозитории существующий `./verify.ps1 -ClientRepository <checkout>`
запускает те же два Node suite. Вторую копию тестов там не заводить.

## Что эти команды не делают

Нет live-авторизации, speedtest, HTTP POST в Google, изменения рабочих Sheets,
запуска `install`/`uninstall`/`agent`, публикации или deployment. HTTP/Sheets в
контрактах подменены. Есть локальные временные файлы, DPAPI, чтение Windows WLAN
и тестовые записи в реестре. Это не полностью hermetic тесты.

Python UI contracts проверяют структуру исходников, не визуальную корректность всех
экранов. После UI-правки отдельно проверяйте затронутый экран, resize, узкое окно,
Enter/Esc и возврат назад; не включайте сетевые действия без необходимости.
`IS74W_RUN_LOCAL=1` отключает forwarding/предложение установки, но **не изолирует**
данные, реестр или сеть. Это не sandbox для запуска всего приложения.

Full не выполняет NativeAOT publish и полевые проверки Windows 10/11.
Упаковка остаётся в `.github/workflows/csharp-alpha-candidate.yml` и `release.yml`;
релиз, push и обновление Apps Script выполняются только по явному запросу.
`.github/workflows/dotnet.yml` и preflight alpha-кандидата используют Full с явным
пропуском приватного backend; CLI/clean-state smoke запускаются отдельно только
в чистом CI-окружении. Совместимость PowerShell также проверяется в `ps51.yml` — отдельный запуск
происходит только при изменении reference PowerShell-кода и связанных тестов;
`Full` в `Client checks` по-прежнему проверяет PowerShell на изменениях C#.
После правок документации без изменений исходников автоматические Windows-сборки
не запускаются. GitHub Actions `C# alpha candidate` (полевой ZIP + SHA-256)
и `C# packaging benchmark` (сравнение вариантов упаковки) запускаются **вручную**
через `Actions → нужный workflow → Run workflow`. Это не ослабляет официальный
`Release`: он по-прежнему независимо собирает NativeAOT и проверяет артефакт
перед публикацией. Длинная `Virtual years simulation` также остаётся ручной.

## Долгие виртуальные испытания расписания

`network-path-foundation` также проверяет две настоящие HTTP-пробы через общий
IP-first при медленном DNS fallback, persisted IP после перезапуска, новые сокеты
при gateway/DHCP/ifIndex и отдельно усиленный ключ короткого DNS-кеша.
`path-aware-agent` проверяет минимум 2 секунды на вторую captive-пробу и запрет
stepOne при её отказе. `path-authorization-state` включает upstream-регрессии
backup/recovery, строгих ошибок чтения и конкурентных изменений.
`path-aware-agent` также проверяет объединение причин сетевых событий без новых
HTTP-проб/авторизаций/таймеров и запись timeout/budget/binding. `network-path-foundation`
проверяет TCP cached-IP/Winsock diagnostics с подменённым dialer. Поля журнала и
анализ частоты описаны в [network-probe-logging.md](network-probe-logging.md).

Группа `authorization-connections` проверяет default Campus-only, миграцию режимов
и явного адаптера, незавершённую первоначальную настройку, сохранение ответа Да/Нет,
пропуск wizard для ранее зарегистрированных установок, доступ USB без Wi-Fi до
подтверждения списка, сохранение пустого выбора и нескольких подключений, точный SSID,
независимость USB от profile/ifIndex/DHCP и отказ при замене adapter GUID.
Также проверяются persisted IP между запусками, отсутствие DNS после TCP failure,
безопасный отказ и успешное явное обновление адресов. Тест использует общий настоящий
`PathAuthorizationRunner`/probe на сохранённых IP для Campus, USB и другого Wi-Fi,
проверяет разделение адресов адаптеров и совместимость старых Wi-Fi-ключей.
`terminal-ui` проверяет черновик чекбоксов, Enter/Esc, редактор SSID, отсутствующий
сохранённый USB, длинный список и resize 50/80/120 столбцов. Вопрос об автовходе
рисуется на всю ширину: текст и обе кнопки не перекрываются в узком окне.
`status-service` не должен показывать Internet/expiry исключённого подключения.
`agent-network-ipc` проверяет maintenance без авторизации и worker portable-сборки.
Сеть в этих сценариях подменена; они не заменяют полевой цикл на ноутбуке.

`terminal-ui-wifi-diagnostics` входит в App через фильтр `terminal-ui`: меню/клавиши,
native WLAN buffers, 2,4/5/6 ГГц, radio on/off/unknown, несколько SSID/BSSID/адаптеров,
прокрутка, resize 30–120 столбцов и 12–40 строк, ошибки/отсутствующие данные.
`status-service` также проверяет сохранение всех разделов TXT, одну on-demand сборку
Wi-Fi без periodic-сбора, времена bound probes, маску телефона и независимость
ошибок Wi-Fi/agent snapshot. Эти тесты не запрашивают живое сканирование WLAN.
Ручная проверка терминала и драйверов остаётся отдельной; [ограничения](wifi-diagnostics.md).

Отдельный проект `tests/IS74Wifi.SimulationTests` моделирует месяцы и годы
без работы с реальной сетью; [параметры и ограничения](virtual-simulation.md).
`dotnet run --project tests/IS74Wifi.SimulationTests -c Release -- --self-test`
проверяет повторяемость результата. `Client checks` и Release workflow
запускают виртуальный месяц с `--strict`; для длинных серий предусмотрен
ручной GitHub Actions workflow `Virtual years simulation`.
На Linux локально полноценный Windows CI/DPAPI suite не запускается;
проверяйте в GitHub Actions.
