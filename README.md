# IntersvyazAutomaticWifi

Экспериментальный Windows-клиент для автоматизации авторизации в captive portal Wi‑Fi «Интерсвязь» на собственном устройстве и с собственной учётной записью.

## Что уже подтверждено экспериментально

1. Windows может самостоятельно пройти телефонную авторизацию API приложения и получить собственный долгоживущий Bearer без Android/эмулятора.
2. Для обычного SMS-подтверждения `POST /mobile/auth/check-confirm` должен получать **пустой `authId`**. Новый `authId`, пригодный для `/mobile/auth/get-token`, приходит уже в ответе `/check-confirm`.
3. Полученный Windows Bearer успешно работает с `GET https://api.is74.ru/mobile/pushmessages`.
4. В тесте сервер выдал сессию сроком на один год (`ACCESS_BEGIN` → `ACCESS_END`).
5. Captive portal подтверждён HAR-трассировкой: `stepOne` → `stepTwo` → `stepThree`. Финальный `stepTwo` содержит только `confirmCode` и `phone`; cookies/CSRF в наблюдавшемся сценарии не требовались.
6. Wi‑Fi-код появляется в серверной ленте `/mobile/pushmessages`, поэтому ежедневная работа не требует Android push.

Подробности: [`docs/protocol.md`](docs/protocol.md).

## Структура

- `experiments/01-get-confirm.ps1` — создаёт/переиспользует Windows `deviceId` и запускает телефонное подтверждение.
- `experiments/02-check-confirm.ps1` — проверяет SMS-код; для обычного SMS отправляет пустой `authId` и сохраняет новый подтверждённый `authId`.
- `experiments/03-get-token.ps1` — получает `AuthSession`, сохраняет Bearer через Windows DPAPI и записывает несекретные метаданные сессии.
- `experiments/04-read-pushmessages.ps1` — проверяет чтение `/mobile/pushmessages` с локальным Bearer.
- `prototypes/01-captive-manual-code.ps1` — прототип штатной длинной последовательности captive portal с ручным вводом 4-значного кода.
- `docs/protocol.md` — зафиксированные параметры протокола и известные неизвестные.

## Локальное состояние

По умолчанию эксперименты используют:

```text
%LOCALAPPDATA%\IS74Wifi\
    device-id.txt
    bearer.dpapi
    session-meta.json
    check-confirm-success.json
```

`bearer.dpapi` создаётся через Windows DPAPI и не должен попадать в репозиторий.

## Статус

Следующая цель — MVP, который:

```text
обнаруживает captive Wi‑Fi
→ читает существующий Bearer из DPAPI
→ запоминает baseline ID уведомлений
→ запускает /stepOne
→ ждёт новый Wi‑Fi-код через /mobile/pushmessages
→ отправляет /stepTwo с того же Windows-клиента
→ проверяет появление Интернета
```

Регистрация человекочитаемых метаданных Windows-устройства (например `DESKTOP-H9F324S`) пока вынесена в отдельную задачу: endpoint обновления device metadata ещё нужно точно определить.

## Безопасность

Не коммитьте Bearer, SMS-коды, HAR-файлы с живыми данными, cookies или локальные файлы `%LOCALAPPDATA%\IS74Wifi`. Репозиторий содержит только воспроизводимую логику и обезличенные параметры протокола.
