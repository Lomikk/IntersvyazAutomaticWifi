# Дорожная карта: авторизация по физическим сетевым путям

Статус: **проект следующей функциональной работы; runtime пока не изменён**.

Этот документ появился после полевых экспериментов с USB-tethering Android и Windows
Mobile Hotspot. Он не продолжает maintainability-рефакторинг и не требует выполнения
U3/A3/A4. Цель — заменить предположение «программа авторизует выбранный Wi-Fi-адаптер»
на модель, которая соответствует наблюдаемому поведению captive portal.

## 1. Что уже подтверждено

1. Captive portal авторизует не процесс и не обязательно компьютер, на котором запущен
   IS74Wifi. Он авторизует внешний captive-session endpoint того сетевого пути, по
   которому пришли `stepOne`/`stepTwo`.
2. При схеме `ПК -> USB tethering телефона -> Campus Wi-Fi` запуск IS74Wifi на ПК
   авторизует upstream Wi-Fi-сессию телефона. После отключения USB интернет остаётся
   на телефоне, а прямой Campus Wi-Fi ПК требует отдельной авторизации.
3. Эффект повторён на Windows NAT: `ПК -> Mobile Hotspot ноутбука -> Campus Wi-Fi`.
   На ноутбуке во время опыта upstream route, source IPv4 и gateway оставались теми же,
   а контрольный `online.susu.ru` изменился с captive
   `HTTP 200` на нормальный `HTTP 302 -> HTTPS` после авторизации, запущенной на ПК.
   Mobile Hotspot при этом поднял `Microsoft Wi-Fi Direct Virtual Adapter #2` и ICS.
4. Инициирующая машина надёжно видит только **локальный egress**: интерфейс, source IP,
   gateway, SSID своего Wi-Fi и маршрут до destination. Что происходит после первого
   gateway/NAT, в общем случае неизвестно и не должно угадываться по TTL/MAC/vendor.
5. Текущий C# runtime уже умеет привязывать authorization TCP к конкретному физическому
   интерфейсу через `Bind(source IPv4)` + Windows `IP_UNICAST_IF`. VPN/TAP/TUN не
   выбираются автоматически. Это полезный механизм, но нынешний auto-selection сам
   выбирает «лучший» физический адаптер и не является моделью фактических path'ов.
6. Один глобальный `expectedExpiryUtc` недостаточен. Авторизация одного пути не даёт
   оснований считать авторизованным другой путь.

## 2. Целевая модель

Внутренняя сущность — **physical network path**, доступный локальной Windows.

Примеры разных path'ов на одном ПК:

```text
Intel Wi-Fi   -> Campus Wi-Fi
USB RNDIS     -> телефон -> Campus Wi-Fi
Realtek Wi-Fi -> Laptop-Hotspot -> ноутбук -> Campus Wi-Fi
Ethernet      -> домашний/офисный роутер
```

VPN не является authorization path. Он может быть текущим системным default route, но
IS74Wifi не создаёт для него таймер, не пытается `stepOne`/`stepTwo` через него и не
считает его отдельной captive-сессией.

Для каждого физического path программа хранит независимое состояние:

```text
identity
last_seen_utc
last_probe_utc
last_successful_auth_utc
expected_expiry_utc     # только scheduling hint
status                  # unknown/internet/captive/unreachable/disconnected
```

Главное правило:

> **Probe — источник истины; таймер — только повод проверить конкретный path.**

По истечении ~24 часов программа сначала проверяет этот path. Авторизация запускается
только если probe подтверждает IS74 captive. Это особенно важно при нескольких
одновременно доступных выходах: нельзя расходовать SMS/`stepOne` только потому, что
старый прогноз времени закончился.

## 3. Что считается identity path

Нельзя использовать только имя адаптера или только `ifIndex`:

- `ifIndex` может измениться;
- один Wi-Fi адаптер может последовательно подключаться к Campus, hotspot телефона и
  домашней сети;
- source IPv4 может измениться после DHCP renew;
- upstream после gateway может быть скрыт NAT и вообще недоступен наблюдению.

Базовая модель:

```text
adapter_id              # стабильный Windows ID/GUID
interface_type
network_discriminator   # SSID для Wi-Fi; gateway для Ethernet/USB, пока нет лучшего сигнала
```

`ifIndex`, source IPv4, gateway, DNS, friendly name и SSID хранятся также как текущий
snapshot/диагностика, но не все обязаны быть частью долговременного ключа.

Безопасное правило при сомнении: лучше получить новый path identity и сделать лишний
probe, чем ошибочно применить 24-часовой таймер другой сети.

BSSID не использовать как identity без отдельного доказательства: roaming между AP с
тем же SSID не должен автоматически создавать новую captive-сессию.

## 4. Правила выбора и VPN

### Автоматический режим

Автоматический режим должен означать не «выбрать один лучший адаптер», а:

1. перечислить все подходящие физические IPv4 path'ы;
2. исключить VPN/TAP/TUN/WireGuard/Wintun/очевидные виртуальные интерфейсы;
3. независимо определить состояние каждого доступного path;
4. обслуживать его таймер только пока path существует или при его повторном появлении;
5. привязывать probe/auth sockets к конкретному path.

### VPN

- VPN никогда не получает `PathAuthorizationState`.
- Если системный интернет идёт через VPN, IS74Wifi может использовать существующий
  direct connector, чтобы проверить/авторизовать underlying physical path мимо VPN.
- DNS может иметь системный fallback только для разрешения имени; authorization TCP
  остаётся привязанным к физическому интерфейсу.
- VPN kill switch/WFP может запретить direct traffic. Тогда path получает
  `unreachable`, а программа не изменяет VPN/firewall и не пытается авторизовать VPN.
- Явный legacy `system route` не должен становиться автоматическим fallback. Перед
  сохранением этого режима как пользовательской опции нужно отдельно решить, не
  противоречит ли он правилу «не авторизовать через VPN».

## 5. Поведение агента

### Path появился или изменился

```text
path appeared/changed
    -> bound Internet/captive probe
       -> internet     : сохранить status, auth не нужна
       -> IS74 captive : разрешить authorization flow
       -> unreachable  : ждать network-change/backoff, stepOne не расходовать
       -> ambiguous    : не считать captive автоматически
```

Смена path должна обходить старый глобальный 24-часовой gate. Например переход
`USB phone -> direct Campus Wi-Fi` немедленно проверяет новый path.

### Path отключился

История не удаляется. Состояние становится `disconnected`. При повторном появлении
делается новый probe; старый `expected_expiry_utc` сам по себе ничего не разрешает и
не запрещает.

### Приближение expected expiry

Существующий adaptive edge-watch можно переиспользовать как идею, но он становится
**per-path**. В новой модели даже на/после ожидаемой границы сначала нужен быстрый
bound probe конкретного path. Только подтверждённый captive разрешает `stepOne`.

Это осознанно меняет нынешнее правило, где timer после границы может быть достаточным
основанием для `stepOne`; миграция этого поведения должна иметь отдельные contracts.

### Авторизация

Перед `stepOne` фиксируется immutable `AuthorizationPathSnapshot`. API, portal и
Internet probe текущей попытки привязаны к одному physical path.

Если во время flow меняется identity path или выбранный интерфейс исчезает:

- не продолжать `stepTwo` через новый путь как будто это та же captive-сессия;
- отменить/завершить попытку безопасным domain result;
- не расходовать следующий automatic send без новой проверки;
- после стабилизации сети заново probe'ить новый path.

После `stepTwo` успех подтверждает Internet probe **через тот же path**. Только после
этого обновляются `last_successful_auth_utc` и `expected_expiry_utc` этого path.

## 6. Что делать с текущими настройками и UI

### «Проверка сети»

Текущий SSID gate `Campus Wi-Fi*` больше не подходит как обязательное условие:
посредник может скрывать upstream SSID. Его нужно заменить проверкой смысла попытки:

```text
Internet уже есть?
  yes -> auth не нужна
  no  -> виден ли IS74 captive через этот physical path?
          yes -> auth разрешена
          no/unknown -> не тратить stepOne
```

SSID остаётся сильным диагностическим сигналом прямого подключения, но не gatekeeper.
В UI пункт лучше превратить в **«Сеть и диагностика»**.

### «Адаптер для авторизации»

Текущий auto-selection одного «лучшего» адаптера не соответствует multi-path модели.
Предпочтительный итог:

- `Автоматически` — обслуживать все подходящие physical path'ы;
- расширенная настройка может позволять исключить конкретный path/interface;
- старый manual pin можно временно сохранить как compatibility mode, но его смысл
  должен быть «ограничить обслуживание этим интерфейсом», а не «это точно устройство,
  которое авторизуется»;
- `system route` оставить только как явно помеченный legacy/diagnostic режим либо
  удалить после полевой проверки VPN-сценариев.

### Что показывать

Не утверждать, что известна upstream сеть за NAT. Допустимо:

```text
СЕТЕВЫЕ ПУТИ

Wi-Fi · Campus Wi-Fi
  Internet: доступен
  Последняя авторизация: 14:32
  Ожидаемая проверка: ~22 ч

USB Ethernet
  Internet: доступен
  Upstream: неизвестен
  Последняя авторизация: 15:20
  Ожидаемая проверка: ~23 ч

Wi-Fi · Laptop-Hotspot
  Отключён
  Последняя авторизация: 16:05

VPN: подключён; для captive-авторизации не используется
```

Для активного/system route отдельно показывать фактический локальный путь до
`w.is74.ru`, но не путать его с невидимым upstream после gateway.

## 7. Этапы реализации

Каждый этап должен быть отдельным небольшим изменением с тестами. Не переносить UI и
agent scheduling одновременно, пока Core-модель path не проверена.

### N0 — зафиксировать модель и полевые evidence

**Статус: этот документ.**

- Документировать NAT/USB/Mobile-Hotspot результаты и ограничения наблюдаемости.
- Зафиксировать различие physical path / VPN layer / unknown upstream.
- Не менять runtime.

Готово, когда следующий агент может объяснить, почему global expiry и SSID-only gate
некорректны, не читая историю чата.

### N1 — read-only `NetworkPathSnapshot`

**Статус: foundation реализован в Core, в runtime/Agent ещё не подключён.**

- В Core добавить чистую модель snapshot и enumerator/resolver физических path'ов.
- Не менять authorization flow, timers или UI decisions.
- Получать adapter ID, ifIndex, type, source IPv4, gateway, SSID, up/down и признаки
  virtual/VPN.
- Отдельно уметь определить system-selected route до `w.is74.ru` для диагностики.

Contracts:

- direct Campus Wi-Fi;
- USB RNDIS + disconnected Wi-Fi со stale default route;
- Wi-Fi hotspot;
- Ethernet;
- VPN + physical interface;
- два физических интерфейса одновременно;
- no gateway/no IPv4;
- ifIndex change при сохранении adapter ID.

Готово: snapshot можно построить/сравнить без сетевых side effects.

Перед N4 текущую `LooksVirtual`/name-based эвристику нужно усилить позитивным Windows
hardware-сигналом (эквивалентом `Get-NetAdapter -Physical` либо нативным IP Helper/NDIS
признаком). N1 намеренно не меняет production selection policy.

### N2 — bound probe конкретного path

**Статус: foundation реализован в Core, `stepOne` не подключён.**

- Обобщить существующий `DirectNetworkConnector`, чтобы он принимал явный path, а не
  сам выбирал один preferred adapter.
- Internet/captive probe уметь выполнить для каждого конкретного path.
- Не запускать `stepOne` на этом этапе.
- VPN candidates отвергать до создания probe.

Contracts:

- каждый path получает свой source/interface;
- failure одного path не влияет на другой;
- DNS fallback не меняет bound TCP path;
- kill-switch/direct-block -> `unreachable`, не fallback на VPN/system route;
- `HTTP 200` captive и нормальный `302` классифицируются отдельно.

Windows field check: direct Wi-Fi, USB phone, Windows hotspot, VPN on/off.

Полевой smoke 2026-10-06 подтвердил ключевой N2-инвариант: при одновременно активных
USB-RNDIS и Wi-Fi через Windows Mobile Hotspot, поверх которых был включён TAP-VPN,
bound HTTP probes к IS74/SUSU успешно прошли независимо через оба физических пути без
переключения системного default route и без отключения VPN.

### N3 — `PathAuthorizationStateStore`

- Ввести per-path durable state и schema/version migration.
- `expected_expiry_utc` хранить только после подтверждённого Internet успеха данного
  path.
- Path disappearance -> `disconnected`, история сохраняется.
- Старый global authorization state при upgrade не приписывать произвольному path:
  использовать только как legacy diagnostic/hint и обязательно probe'ить первый path.

Contracts: два независимых path, network switch на одном адаптере, DHCP/source-IP
change, stale state, corrupt state, disconnected/reappeared.

### N4 — path-aware agent scheduling

- Агент перечисляет все eligible physical path'ы.
- New/changed/reappeared path -> immediate bound probe.
- Каждый path имеет собственный next-check/expiry schedule.
- На expiry всегда probe first; captive -> auth candidate, internet -> reschedule.
- Один unreachable path не блокирует остальные.
- Ограничить concurrency: не запускать несколько SMS authorization flows одновременно;
  очередь/arbitration должна быть детерминированной.

Contracts: два истекающих path одновременно, один disconnected, один captive/один
internet, network-change до expiry, resume после нескольких expiry.

### N5 — привязать authorization flow к path snapshot

- `AuthorizationFlow` получает явный immutable path snapshot/connector.
- baseline API, `stepOne`, polling API, `stepTwo` и post-auth probe работают через
  один path.
- Перед side-effect checkpoints проверять, что path ещё совместим с snapshot.
- Route/interface change между `stepOne` и `stepTwo` -> безопасный abort/reprobe,
  никогда не продолжать silently через другой path.
- Four-attempt budget остаётся per authorization cycle/path.

Contracts: path change до stepOne, после stepOne, после fresh code, во время stepTwo;
потеря интерфейса; recovery без blind resend.

Windows field check: реально переключить USB <-> Wi-Fi во время контролируемой попытки.

### N6 — заменить SSID gate на captive/path policy

- Удалить `Campus Wi-Fi*` как обязательный runtime gate для normal auto mode.
- Direct Campus SSID остаётся diagnostic confidence signal.
- `IgnoreNetworkCheck` мигрировать/переименовать либо удалить после определения нового
  policy; не сохранять настройку с уже неверным названием только ради совместимости UI.
- Home/office Internet path не авторизовать: успешный Internet probe заканчивает цикл.
- Неизвестный captive, не похожий на IS74, не должен расходовать `stepOne`.

На этом этапе обновить исторические/production docs, где `Campus Wi-Fi*` ещё записан как
текущий invariant. До N6 эти документы описывают существующий runtime и не должны быть
тихо переписаны заранее.

### N7 — UI «Сеть и диагностика»

- Заменить старую пользовательскую модель «Проверка сети» на список path'ов и их
  фактический status.
- Показать current/system path до portal, но отдельно от managed physical paths.
- Не обещать знание upstream за NAT.
- `Автоматически` = все eligible physical paths.
- VPN показывать отдельно: «подключён; captive-авторизация через него не выполняется».
- Manual interface restriction спрятать в advanced/compatibility, если она всё ещё
  нужна после field tests.

UI contracts: narrow/resize, 0/1/many path'ов, длинные SSID/names, VPN indicator,
unknown upstream, disconnected history.

### N8 — очистка legacy модели и release gate

Только после N1–N7 и полевого прогона:

- удалить/упростить старый one-adapter auto-selection, если он больше не нужен;
- удалить global expiry как decision source;
- решить судьбу `system route` и `IgnoreNetworkCheck`;
- обновить `direct-network.md`, `production-mvp.md`, migration/history docs и project map;
- добавить upgrade notes для существующих установок.

Не делать эту очистку раньше: до подтверждения новой модели старый runtime остаётся
рабочим rollback/reference path.

## 8. Матрица проверки

### Автоматические Core/contracts

Минимум:

1. один direct Campus path;
2. USB/RNDIS path;
3. Wi-Fi hotspot path;
4. Ethernet path;
5. два и более physical path одновременно;
6. VPN присутствует и исключён;
7. stale/disconnected route не выбирается как живой path;
8. один path Internet, второй captive;
9. один path unreachable, второй рабочий;
10. смена SSID на том же Wi-Fi adapter;
11. DHCP/source IP change;
12. gateway change;
13. sleep/resume;
14. path disappears/reappears;
15. path change в каждой критической фазе authorization;
16. два expiry одновременно без параллельного SMS-flow;
17. invalid/corrupt persisted per-path state;
18. legacy global state upgrade.

### Windows integration/field

Перед релизом новой модели повторить минимум:

| Сценарий | Ожидание |
|---|---|
| Direct Campus Wi-Fi | probe/auth конкретного Wi-Fi path |
| USB Android -> Campus | авторизуется upstream телефона, состояние хранится у USB path ПК |
| Wi-Fi hotspot телефона -> Campus | состояние хранится у hotspot path ПК |
| Windows Mobile Hotspot ноутбука -> Campus | авторизуется upstream ноутбука, инициатор хранит свой hotspot path |
| Campus Wi-Fi + USB одновременно | оба physical path видны и обслуживаются независимо |
| Ethernet + Campus Wi-Fi | ни один path не подавляет другой |
| VPN off/on | physical auth одинаково работает мимо VPN |
| VPN kill switch | physical path -> unreachable, VPN не становится fallback |
| Switch path до expiry | новый path немедленно probe'ится |
| Switch во время auth | flow не продолжает stepTwo через другой path |
| Resume после >24h | каждый появившийся path probe'ится независимо |

Для полевых опытов достаточно route/source/probe logs. Packet capture (`pktmon`/Wireshark)
добавлять только когда нужно доказать конкретный спорный маршрут; не делать его обычным
условием теста.

## 9. Что не делать

- Не пытаться определять upstream SSID/устройство за NAT эвристиками.
- Не считать MAC vendor, TTL или частную подсеть доказательством «это телефон/ноутбук».
- Не создавать таймер для VPN/TAP/TUN.
- Не считать наличие default route доказательством, что интерфейс реально пригоден.
- Не переносить старый global expiry на первый найденный path при upgrade.
- Не отправлять `stepOne` только по таймеру без bound probe конкретного path.
- Не запускать несколько одновременных SMS flows ради нескольких истёкших path'ов.
- Не смешивать эту работу с U3/A3/A4 maintainability roadmap без конкретной причины.

## 10. Точка остановки

После **N5** сделать обязательную паузу и полевой checkpoint.

Если direct Campus, USB tethering, phone hotspot, Windows hotspot и VPN scenarios
проходят на path-aware Core/agent, только тогда выполнять N6/N7 и менять пользовательскую
модель. Если N1–N5 показывают, что multi-path обслуживание слишком сложно или создаёт
неприемлемые SMS/route races, UI и migration не начинать — пересмотреть модель на
основании логов.

Цель roadmap — не «идеальная сеть», а одно проверяемое обещание:

> **IS74Wifi независимо обслуживает каждый видимый физический путь, никогда не пытается
> авторизовать VPN и не переносит предположение об авторизации одного path на другой.**
