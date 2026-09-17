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
GET /mobile/pushmessages?page=1&pageSize=20
Accept: application/json; version=v2
Authorization: Bearer <TOKEN>
```

Наблюдавшиеся Wi‑Fi-сообщения имеют признаки:

```text
subject = "Ваш код авторизации"
user_id = null
tag = null
payload = null
push_message/full_message = "NNNN код авторизации в приложении \"Интерсвязь\""
```

Для надёжного выбора свежего кода лучше не ориентироваться на часы. Алгоритм:

1. Перед запуском captive-авторизации получить текущий максимальный `id` подходящего сообщения (`baselineId`).
2. Запустить `/stepOne`.
3. Опросить `/mobile/pushmessages` раз в 1–2 секунды.
4. Взять первое подходящее сообщение с `id > baselineId`.
5. Извлечь код regex-ом `^(\d{4}) код авторизации в приложении "Интерсвязь"$`.

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

Наблюдавшийся ответ:

```text
302 Location: /stepTwo?phone=XXXXXXXXXX&isMp=true
```

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
