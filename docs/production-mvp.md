# Production MVP (Windows PowerShell 5.1)

Целевая версия MVP работает на встроенном Windows PowerShell 5.1 и не требует PowerShell 7, отдельного .NET Runtime или постоянной иконки в tray.

## Компоненты

- `IS74Wifi.ps1` — CLI и интерактивное меню.
- `agent.ps1` — фоновый пользовательский агент.
- `src/IS74Wifi.psm1` — HTTP, регистрация, DPAPI, captive portal, уведомления и Task Scheduler.
- `experiments/*` — остаются неизменёнными как воспроизводимые исследования протокола.

## Первый запуск

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\IS74Wifi.ps1 register
```

Пользователь один раз вводит номер телефона и затем SMS-код. После успешного `/get-token` приложение сохраняет номер и Bearer в одном DPAPI-зашифрованном файле текущего Windows-пользователя:

```text
%LOCALAPPDATA%\IS74Wifi\secrets.dpapi
```

`deviceId`, несекретные метаданные API-сессии, runtime-state и журналы хранятся отдельно в той же папке. Промежуточный `authId` на диск не записывается.

Проверенный backend-вариант `addresses=[]` поддерживается полностью. Если `/check-confirm` вернул непустой `addresses`, MVP останавливает регистрацию до выбора `userId`, потому что этот вариант ещё не зафиксирован экспериментально достаточно точно.

## CLI

```text
.\IS74Wifi.ps1 register
.\IS74Wifi.ps1 connect
.\IS74Wifi.ps1 status
.\IS74Wifi.ps1 install
.\IS74Wifi.ps1 uninstall
.\IS74Wifi.ps1 reset
.\IS74Wifi.ps1 purge
.\IS74Wifi.ps1 toast-test
```

Без аргументов открывается текстовое меню.

## Автозапуск

`install` создаёт задачу `IS74WifiAgent` в Task Scheduler для текущего пользователя и запускает её с `LogonType=Interactive` и `RunLevel=Limited`.

Агент запускается скрыто:

```text
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File agent.ps1
```

Windows Service не используется. Это сохраняет тот же пользовательский DPAPI-контекст и позволяет показывать уведомления в интерактивной сессии.

`uninstall` останавливает и удаляет задачу, но оставляет регистрацию и Bearer. `reset` удаляет регистрацию и deviceId. `purge` дополнительно удаляет `%LOCALAPPDATA%\IS74Wifi`.

## Уведомления

MVP отправляет toast в четырёх случаях:

1. примерно за 10 минут до ожидаемого окончания 24-часового окна;
2. непосредственно перед началом captive-авторизации;
3. после успешного `POST /stepTwo`;
4. при ошибке автоматической авторизации.

Для обычного desktop-процесса Windows требует AppUserModelID, связанный с Start Menu. Чтобы не устанавливать сторонний модуль и отдельный GUI, MVP использует AppID существующего ярлыка `Windows PowerShell`, найденный через `Get-StartApps`. Команда `toast-test` позволяет проверить это на конкретной Windows-системе.

24 часа — ожидаемое окно для информирования пользователя. Агент не делает повторную авторизацию только по таймеру: сначала используется состояние Windows NCSI (`Get-NetConnectionProfile`). Поэтому постоянный цикл не генерирует внешний HTTP-запрос каждые 15 секунд. Активный Microsoft Connect Test выполняется уже перед/после реальной попытки авторизации и при `status`. Если Windows всё ещё видит Интернет, ничего не происходит.

## Автоматическая авторизация

Когда Интернет недоступен и Wi-Fi подключён, агент выполняет подтверждённый минимальный путь:

```text
baseline /pushmessages
→ POST /stepOne
→ GET /pushmessages по быстрому абсолютному расписанию
→ свежий 4-значный код только в памяти
→ direct POST /stepTwo
→ проверка Internet
```

`GET /stepTwo` и `GET /stepThree` отсутствуют. В журнал не записываются Bearer, SMS-код или Wi-Fi-код.

Текущий retry-backoff фонового агента — 60 секунд после неудачной попытки. Polling внутри одной попытки использует подтверждённый экспериментами профиль до 10 секунд.

## Ограничения первого MVP

- IP/DNS cache/fallback из `docs/protocol.md` пока не перенесён в production-модуль; HTTP использует системный DNS.
- Ветка с непустым `addresses` намеренно не угадывает схему выбора `userId`.
- Toast должен быть проверен командой `toast-test`; системные настройки Windows могут запрещать уведомления.
- Проверка наличия Интернета использует стандартный Microsoft Connect Test URL и требует точного ожидаемого ответа.
