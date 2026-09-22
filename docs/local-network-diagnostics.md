# Локальная диагностика сетевых запросов

Начиная со следующей версии после `v0.1.0-alpha.22`, приложение фиксирует ошибки
**API и портала** в `%LOCALAPPDATA%\IS74Wifi\logs\diagnostic.log` независимо от
согласия на отправку статистики. Параметры сетевых запросов и политика повторных
попыток не меняются. Обычные успешные запросы и частые проверки доступности
интернета не создают новых записей.

Примеры (значения времени условные):

```text
network.failure route=api.get-confirm kind=DnsUnavailable phase=before_headers elapsedMs=12 budgetMs=15000 headersMs=none httpStatus=none
network.failure route=portal.stepOne kind=Timeout phase=before_headers elapsedMs=5001 budgetMs=5000 headersMs=none httpStatus=none
network.failure route=api.pushmessages kind=ResponseTooLarge phase=reading_body elapsedMs=12 budgetMs=3000 headersMs=9 httpStatus=200
network.http-error route=api.pushmessages status=429 elapsedMs=40 headersMs=20 retryAfterMs=2000
```

- `route`: заранее заданная метка маршрута; полный URL, query-параметры, номер
  телефона, код, идентификатор устройства и Bearer **не записываются**.
- `kind`: распознанный класс транспортной ошибки. `DnsUnavailable`,
  `ConnectionFailure` и `TlsFailure` различаются, **если соответствующую ошибку
  сообщил HTTP-клиент**; `Timeout` не означает отказ самого API.
- `phase=before_headers`: ответные заголовки не получены. Этот этап включает DNS,
  TCP, TLS, отправку запроса и ожидание сервера; **по одному таймауту определить
  конкретный сетевой подэтап нельзя**.
- `phase=reading_body`: заголовки получены, ошибка возникла при чтении ответа.
  `headersMs` показывает время до получения заголовков; `httpStatus` может быть
  известен даже при ошибке тела.
- `network.http-error` сохраняет HTTP 4xx/5xx, включая 429 и безопасное
  числовое значение `Retry-After`, если сервер его прислал. Это не инициирует
  дополнительных сетевых проверок.

Отмена ожидаемых параллельных опросов после получения кода (`Cancelled`) не
логируется, чтобы не засорять журнал. Журнал остаётся локальным и использует
существующие ограничения размера и маскирование конфиденциальных данных.
