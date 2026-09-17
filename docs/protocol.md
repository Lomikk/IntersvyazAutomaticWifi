# Protocol notes

Ниже зафиксированы только наблюдавшиеся/проверенные части протокола. Значения телефона, токенов, `authId` и локальных идентификаторов намеренно не приводятся.

## 1. Авторизация API приложения с Windows

Базовый API:

```text
https://api.is74.ru
```

### 1.1. GET-CONFIRM

```http
POST /mobile/auth/get-confirm
Content-Type: application/json
Accept: application/json; version=v2
```

Тело:

```json
{
  "phone": "10 цифр без +7/8",
  "deviceId": "постоянный идентификатор клиента",
  "authType": 0
}
```

Для SMS сервер возвращает объект с `authId`, `authType`, `confirmType`, `message` и таймерами. Полученный здесь `authId` **не используется в SMS-проверке**.

### 1.2. CHECK-CONFIRM для обычного SMS

```http
POST /mobile/auth/check-confirm
Content-Type: application/x-www-form-urlencoded
```

Поля:

```text
phone=<10 цифр>
confirmCode=<SMS-код>
authId=
```

Критическая деталь: для обычного SMS поле `authId` отправляется пустым. Успешный ответ возвращает **новый** `authId` и `addresses`.

### 1.3. GET-TOKEN

```http
POST /mobile/auth/get-token
Content-Type: application/x-www-form-urlencoded
```

Поля:

```text
authId=<authId из успешного check-confirm>
userId=<пусто, если addresses=[]>
uniqueDeviceId=<тот же постоянный deviceId Windows>
```

Успешный ответ содержит `AuthSession`, включая:

```text
TOKEN
USER_ID
PROFILE_ID
ACCESS_BEGIN
ACCESS_END
...
```

`TOKEN` используется как:

```http
Authorization: Bearer <TOKEN>
```

В проверенном эксперименте Windows получил собственный Bearer, а `GET /mobile/pushmessages` с ним вернул HTTP 200. Срок сессии составил ровно один год между `ACCESS_BEGIN` и `ACCESS_END`.

## 2. Уведомления / Wi‑Fi-код

```http
GET /mobile/pushmessages?page=1&pageSize=1
Accept: application/json; version=v2
Authorization: Bearer <TOKEN>
Cache-Control: no-cache
```

Наблюдавшиеся Wi‑Fi-сообщения имеют признаки:

```text
subject = "Ваш код авторизации"
user_id = null
tag = null
payload = null
push_message/full_message = "NNNN код авторизации в приложении \"Интерсвязь\""
```

### 2.1. Порядок выдачи и pageSize=1

Экспериментально сравнивались `pageSize=1` и `pageSize=5`. Первая запись совпала, а ID пяти записей шли от большего к меньшему, то есть от новых к старым. Поэтому для штатного ожидания достаточно `page=1&pageSize=1`.

Редкий случай, когда между baseline и чтением пришло другое сообщение, можно обрабатывать одним дополнительным запросом `pageSize=5` и поиском подходящего Wi‑Fi-сообщения среди `id > baselineId`.

### 2.2. Кэш

Обычный ответ `/pushmessages` наблюдался с заголовками вида:

```text
Cache-Control: public, max-age=30
Expires: <примерно Date + 30 секунд>
X-Cache-Status: MISS
```

Несколько одинаковых GET подряд каждый раз возвращали новый `Date` и `X-Cache-Status: MISS`, то есть в этих измерениях готовый ответ из proxy-cache не отдавался.

При запросе с:

```http
Cache-Control: no-cache
```

наблюдался:

```text
X-Cache-Status: BYPASS
```

Поэтому Windows-клиент должен использовать `Cache-Control: no-cache`, чтобы явно обходить промежуточный cache при ожидании свежего кода. `ETag` и `Last-Modified` в наблюдавшемся ответе отсутствовали.

Проверенные пути `/mobile/pushmessages/unreadcount` и `/mobile/pushmessages/badges?lastIncidentId=0` в текущем production backend отвечали HTTP 404, поэтому для MVP они не используются.

### 2.3. Выбор свежего кода

Для надёжного выбора не нужно ориентироваться на часы:

1. Перед `/stepOne` получить текущий верхний `id` (`baselineId`).
2. Запустить `/stepOne`.
3. Читать `page=1&pageSize=1` с `Cache-Control: no-cache`.
4. Если `id > baselineId` и сообщение соответствует шаблону Wi‑Fi-кода — использовать его.
5. Если верхнее новое сообщение другого типа — один раз прочитать несколько последних записей и найти подходящую среди `id > baselineId`.

Regex содержимого:

```text
^(\d{4}) код авторизации в приложении "Интерсвязь"$
```

### 2.4. Измерение задержки появления сообщения

Проведён диагностический параллельный тест: наблюдатель `/pushmessages?page=1&pageSize=1` был запущен **до** `POST /stepOne`, без искусственной паузы между последовательными GET. `/stepTwo` в тесте не вызывался.

В одном измерении относительно начала `/stepOne` наблюдалось:

```text
GET со старым ID: start ≈ -13.2 ms, end ≈ 70.6 ms
следующий GET:     start ≈ 70.7 ms, end ≈ 139.3 ms, уже новый ID
/stepOne HTTP 302: завершился ≈ 135.4 ms
```

Следовательно, по клиентским временным меткам переход от старого к новому состоянию произошёл в окне между двумя чтениями; новый код был доступен не позднее ~139.3 ms после начала `/stepOne`. Нельзя утверждать, что он появился ровно на 139.3 ms или ровно после ответа `/stepOne`: сетевой RTT каждого GET включает путь туда/обратно и обработку на сервере.

В другом последовательном измерении `/stepOne` занял около 2.3 s, а первый GET после его ответа сразу увидел новый код. Это подтверждает, что рабочая логика может сначала дождаться ответа `/stepOne`, а затем сразу проверить `pageSize=1`, используя короткий fallback polling только при необходимости. Параллельное чтение остаётся возможной оптимизацией и диагностическим инструментом.

Проверенный скрипт измерения: `experiments/06-measure-push-latency.ps1`.

На captive-сети ранее была подтверждена доступность `api.is74.ru` через walled garden при обходе проблемного DNS. Это надо учесть в MVP отдельно, а не жёстко привязываться навсегда к одному IP.

## 3. Captive portal

Стартовая точка:

```text
http://w.is74.ru/stepOne
```

### 3.1. STEP ONE

```http
POST http://w.is74.ru/stepOne
Content-Type: application/x-www-form-urlencoded
```

Поля:

```text
phone=8XXXXXXXXXX
dial_code=7
country_code=ru
sendPush=on
```

Наблюдавшийся ответ для неавторизованного клиента:

```text
302 Location: stepTwo?phone=XXXXXXXXXX&isMp=true
```

Для уже авторизованного captive-клиента наблюдался редирект сразу на landing page вместо `stepTwo`; в таком состоянии новый Wi‑Fi-код не создавался.

### 3.2. STEP TWO page

Браузер штатно делает:

```http
GET http://w.is74.ru/stepTwo?phone=XXXXXXXXXX&isMp=true
```

HTML-форма содержит:

```html
<form action="" method="post">
    <input name="confirmCode">
    <input type="hidden" name="phone" value="XXXXXXXXXX">
</form>
```

### 3.3. Финальный STEP TWO

```http
POST http://w.is74.ru/stepTwo?phone=XXXXXXXXXX&isMp=true
Content-Type: application/x-www-form-urlencoded
```

Поля:

```text
confirmCode=NNNN
phone=XXXXXXXXXX
```

При успехе наблюдался:

```text
302 Location: stepThree
```

Затем `/stepThree` ведёт на landing page после открытия Интернета.

В наблюдавшемся HAR у `stepOne/stepTwo/stepThree` не было обязательных cookies, CSRF-токена или отдельного transaction ID. Тем не менее MVP сначала должен повторять штатную длинную последовательность `POST stepOne → GET stepTwo → POST stepTwo`, а уже потом можно экспериментально удалить промежуточный GET.

## 4. Привязка captive-авторизации

Практически подтверждено, что смена MAC приводит к потере Wi‑Fi-доступа. Поэтому финальный `POST /stepTwo` должен выполняться **тем же Windows-клиентом**, которому требуется открыть Интернет. Bearer нужен только для чтения серверной ленты уведомлений и не заменяет сетевую привязку captive portal.

## 5. Device metadata

При Windows-входе с собственным `deviceId` сервер создаёт отдельную активную сессию. Без дополнительной синхронизации приложение отображало её как устройство с пустыми `devicemodel` и `osvers`.

Экспериментально подтверждено, что метаданные существующей Windows-сессии можно обновить через:

```http
PUT /mobile/pushtoken/add-with-device-id
Content-Type: application/json
Authorization: Bearer <TOKEN>
```

Минимально использованный JSON:

```json
{
  "TYPE": 5,
  "UNIQUE_DEVICE_ID": "<тот же постоянный deviceId>",
  "AUTHORIZE_PHONE": "<10 цифр>",
  "VERS_NAME": "2.18.0-RS-95aa9b78",
  "ASSEMBLY_CODE": 2026061111,
  "OS_VERS": "Windows 10 Pro 22H2 (build 19045.x)",
  "DEVICE_MODEL": "DESKTOP-..."
}
```

В проверенном сценарии сервер ответил HTTP 200, после чего экран «Устройства» в Android-приложении стал отображать Windows-сессию с переданными `DEVICE_MODEL` и `OS_VERS`.

Настоящий FCM/RuStore push-token и SIM-поля для этого эксперимента не передавались. Это важно: Windows-клиенту не требуется подделывать мобильный push-token только ради человекочитаемого имени устройства.

Для обновления уже существующей сессии следует переиспользовать тот же `UNIQUE_DEVICE_ID`; генерировать новый идентификатор не нужно.

Проверенный скрипт: `experiments/05-register-device-metadata.ps1`.

## 6. Локальное хранение

Эксперименты используют Windows DPAPI через `ConvertFrom-SecureString` / `ConvertTo-SecureString` и хранят данные в `%LOCALAPPDATA%\IS74Wifi`.

В репозиторий нельзя коммитить:

- Bearer;
- живые SMS/Wi‑Fi-коды;
- HAR с личными данными;
- локальный `device-id.txt` конкретного клиента;
- сырые auth/session responses.
