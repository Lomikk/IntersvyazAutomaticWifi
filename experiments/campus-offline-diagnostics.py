#!/usr/bin/env python3
"""Windows field wizard: bound probes, or opt-in installed-client authorization."""
import argparse
import hashlib
import datetime as dt
import ipaddress
import json
import os
from pathlib import Path
import secrets
import socket
import ssl
import struct
import subprocess
import sys
import time
from urllib.parse import urlsplit

HOSTS = ("online.susu.ru", "w.is74.ru", "api.is74.ru")
IP_UNICAST_IF = 31
FIRST_BUDGET = 2.0
CONFIRM_BUDGET = 0.3
CONFIRM_DELAY = 0.5


def ask(message):
    answer = input(message + " [Enter — продолжить, q — завершить]: ").strip()
    if answer.lower() == "q":
        raise KeyboardInterrupt
    return answer


def choose_adapter(wifi):
    while True:
        value = ask(f"Введи номер Wi-Fi-адаптера из списка (1–{len(wifi)}), НЕ телефон")
        if value.isascii() and value.isdecimal() and len(value) <= len(str(len(wifi))):
            choice = int(value)
            if 1 <= choice <= len(wifi):
                return wifi[choice - 1]["id"]
        print("Нужен порядковый номер строки выше, например 1. Телефон для диагностики не требуется. Попробуй ещё раз.")


def snapshot():
    # Use Windows PowerShell to avoid MSIX PowerShell module compatibility issues.
    code = r"""
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $rows = @(Get-NetAdapter | ForEach-Object {
      $a = $_
      [pscustomobject]@{
        id = [string]$a.InterfaceGuid; name = $a.Name
        index = [int]$a.ifIndex; status = [string]$a.Status
        wifi = ($a.InterfaceType -eq 71); hardware = [bool]$a.HardwareInterface
        ipv4 = @(Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object IPAddress)
        dns = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
      }
    })
    ConvertTo-Json -InputObject $rows -Depth 5 -Compress
    """
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", code],
        capture_output=True, encoding="utf-8", errors="replace", timeout=30,
    )
    if result.returncode:
        raise RuntimeError("Не удалось прочитать адаптеры: " + result.stderr[-1000:])
    return json.loads(result.stdout.lstrip("\ufeff"))


def selected(rows, adapter_id):
    matches = [row for row in rows if row["id"] == adapter_id]
    if not matches or matches[0]["status"] != "Up":
        raise RuntimeError("Выбранный Wi-Fi отключён. Подключись к Campus и повтори этап.")
    row = matches[0]
    addresses = [ip for ip in row["ipv4"] if not ipaddress.ip_address(ip).is_link_local]
    if not addresses:
        raise RuntimeError("У выбранного Wi-Fi пока нет пригодного IPv4.")
    return dict(row, source=addresses[0])


def binding(row):
    return row["id"], row["index"], row["source"], row["dns"]


def bound_socket(adapter, kind, timeout):
    sock = socket.socket(socket.AF_INET, kind)
    try:
        sock.settimeout(timeout)
        sock.setsockopt(socket.IPPROTO_IP, IP_UNICAST_IF, struct.pack("!I", adapter["index"]))
        sock.bind((adapter["source"], 0))
        return sock
    except BaseException:
        sock.close()
        raise


def remaining(deadline):
    value = deadline - time.perf_counter()
    if value <= 0:
        raise TimeoutError("Исчерпан общий бюджет запроса")
    return value


def skip_name(packet, pos):
    while True:
        length = packet[pos]
        pos += 1
        if length & 0xC0 == 0xC0:
            return pos + 1
        if length & 0xC0:
            raise ValueError("Неверная DNS-метка")
        if length == 0:
            return pos
        pos += length


def parse_dns(packet, transaction, question):
    ident, flags, questions, answers, _, _ = struct.unpack_from("!6H", packet)
    if ident != transaction or not flags & 0x8000 or flags & 0x020F or questions != 1:
        raise ValueError("DNS: mismatch, truncated response or error")
    if packet[12:12 + len(question)] != question:
        raise ValueError("DNS question mismatch")
    pos = 12 + len(question)
    result = []
    for _ in range(answers):
        pos = skip_name(packet, pos)
        kind, cls, _, size = struct.unpack_from("!HHIH", packet, pos)
        pos += 10
        data = packet[pos:pos + size]
        if len(data) != size:
            raise ValueError("Truncated DNS record")
        if kind == 1 and cls == 1 and size == 4:
            result.append(socket.inet_ntoa(data))
        pos += size
    if not result:
        raise ValueError("DNS не вернул A-адресов")
    return list(dict.fromkeys(result))


def direct_dns(host, adapter, deadline):
    transaction = secrets.randbelow(65536)
    question = b"".join(bytes([len(part)]) + part.encode("ascii") for part in host.split(".")) + b"\0\0\1\0\1"
    packet = struct.pack("!6H", transaction, 0x0100, 1, 0, 0, 0) + question
    last = "У Wi-Fi нет DNS-серверов IPv4"
    for server in adapter["dns"]:
        try:
            with bound_socket(adapter, socket.SOCK_DGRAM, min(0.9, remaining(deadline))) as sock:
                sock.connect((server, 53))
                sock.send(packet)
                response = sock.recv(65535)
                return parse_dns(response, transaction, question)
        except (OSError, ValueError, IndexError, struct.error) as exc:
            last = str(exc)
    raise OSError(last)


def system_dns(host):
    # A child process bounds Windows DNS; a stuck resolver cannot trap the wizard.
    result = subprocess.run(
        [sys.executable, str(Path(__file__).resolve()), "--resolve-system", host],
        capture_output=True, text=True, encoding="utf-8", timeout=15,
    )
    if result.returncode:
        raise OSError("Системный DNS не вернул адрес")
    return json.loads(result.stdout)


def request(host, address, adapter, budget, resolve_fresh=False):
    started = time.perf_counter()
    deadline = started + budget
    row = {"host": host, "budget_ms": round(budget * 1000), "mode": "fresh-adapter-dns" if resolve_fresh else "ip-only"}
    sock = None
    phase = "dns" if resolve_fresh else "tcp"
    try:
        if resolve_fresh:
            address = direct_dns(host, adapter, deadline)[0]
            row["dns_ms"] = round((time.perf_counter() - started) * 1000, 2)
        row["address"] = address
        phase = "tcp"
        sock = bound_socket(adapter, socket.SOCK_STREAM, remaining(deadline))
        sock.connect((address, 443 if host == "api.is74.ru" else 80))
        row["tcp_at_ms"] = round((time.perf_counter() - started) * 1000, 2)
        if host == "api.is74.ru":
            phase = "tls"
            context = ssl.create_default_context()
            sock.settimeout(remaining(deadline))
            sock = context.wrap_socket(sock, server_hostname=host)
            row["tls_at_ms"] = round((time.perf_counter() - started) * 1000, 2)
        phase = "http_headers"
        sock.settimeout(remaining(deadline))
        sock.sendall((f"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nCache-Control: no-cache, no-store\r\nPragma: no-cache\r\n\r\n").encode("ascii"))
        headers = b""
        while b"\r\n\r\n" not in headers:
            sock.settimeout(remaining(deadline))
            chunk = sock.recv(1024)
            if not chunk:
                raise OSError("Соединение закрыто до HTTP-заголовков")
            headers += chunk
            if len(headers) > 65536:
                raise ValueError("Слишком большие HTTP-заголовки")
        lines = headers.split(b"\r\n\r\n", 1)[0].decode("iso-8859-1").split("\r\n")
        remaining(deadline)
        row["status"] = int(lines[0].split()[1])
        location = next((line.split(":", 1)[1].strip() for line in lines[1:] if line.lower().startswith("location:")), "")
        # Never persist redirect tokens, query strings, response bodies or cookies.
        target = urlsplit(location)
        row["location_host"] = target.hostname
        row["expected_internet_redirect"] = location.lower() == "https://online.susu.ru/" and 300 <= row["status"] < 400
        row["outcome"] = ("Internet" if host == HOSTS[0] and row["expected_internet_redirect"] else "HTTP200_candidate_captive" if host == HOSTS[0] and row["status"] == 200 else "OtherHTTP")
    except (OSError, ValueError, IndexError, struct.error) as exc:
        row.update(outcome="Failure", failed_phase=phase, error=type(exc).__name__, detail=str(exc)[:300])
    finally:
        if sock is not None:
            sock.close()
    row["elapsed_ms"] = round((time.perf_counter() - started) * 1000, 2)
    return row


class Report:
    def __init__(self, folder):
        self.folder = folder
        folder.mkdir(parents=True, exist_ok=False)
        self.file = (folder / "results.jsonl").open("a", encoding="utf-8")

    def save(self, event, **data):
        self.file.write(json.dumps(dict(time=dt.datetime.now().astimezone().isoformat(), event=event, **data), ensure_ascii=False) + "\n")
        self.file.flush()


def confirm_word(message, word):
    return ask(message + f" Введи {word}").upper() == word


def invoke_authorization(report, executable):
    """One explicit invocation of the installed runtime; never retries or handles secrets."""
    if not confirm_word("Это реальная попытка авторизации штатным IS74Wifi, возможен расход попытки портала.", "АВТОРИЗОВАТЬ"):
        report.save("authorization_skipped", reason="not explicitly confirmed")
        return
    root = Path(os.environ["LOCALAPPDATA"]) / "IS74Wifi"
    log = root / "logs" / "diagnostic.log"
    before = log.stat().st_size if log.exists() else 0
    report.save("authorization_start", executable=str(executable), sha256=hashlib.sha256(executable.read_bytes()).hexdigest(),
                transport="installed C# runtime; DNS policy unchanged; NOT IP-only authorization",
                diagnostic_log=str(log), log_offset_before=before)
    started = time.perf_counter()
    # No shell interpolation, credential extraction, automatic retry or child timeout:
    # killing the CLI would not reliably cancel an agent-owned authorization flow.
    result = subprocess.run([str(executable), "connect"], check=False)
    report.save("authorization_end", exit_code=result.returncode,
                elapsed_ms=round((time.perf_counter() - started) * 1000, 2),
                log_size_after=log.stat().st_size if log.exists() else 0)
    print("Код завершения клиента:", result.returncode,
          "(0 может означать и AlreadyOnline; сам по себе не доказывает новую авторизацию).")


def auth_scenario(report, adapter_id, cache, executable):
    print("\nСЦЕНАРИЙ: VPN ПОДКЛЮЧЁН НА LTE → LTE ОТКЛЮЧЁН → CAPTIVE CAMPUS")
    print("IS74Wifi должен быть заранее зарегистрирован. Телефон/код вводятся только в самом приложении при регистрации, не здесь.")
    print("Приостанови фоновую автоавторизацию и закрой меню IS74Wifi. Скрипт выполнит только одну команду connect.")
    print("1. На LTE установи VPN-соединение и убедись, что VPN действительно подключён.")
    if not confirm_word("VPN подключён на работающем LTE?", "VPN"):
        report.save("authorization_scenario_skipped", reason="VPN not established on LTE")
        return
    report.save("vpn_established_on_lte", adapters=snapshot(), declaration="user confirmed; tunnel not independently verified")
    print("2. Не выключая исследуемый VPN, отключи LTE/USB-тетеринг и другие интернет-каналы.")
    print("3. Подключи прямой Wi-Fi к НЕАВТОРИЗОВАННОМУ Campus. Radmin отключи; исследуемый VPN-клиент оставь включённым.")
    print("Удалённая связь пропадёт. VPN может показывать reconnecting — сохрани это состояние, не подключай LTE заново.")
    if not confirm_word("LTE отключён, Campus подключён, VPN-клиент остался включённым?", "CAMPUS"):
        report.save("authorization_scenario_skipped", reason="network transition not confirmed")
        return
    notes = ask("Укажи состояние VPN после отключения LTE (connected/reconnecting/другое)")
    adapter = selected(snapshot(), adapter_id)
    report.save("vpn_after_lte_loss", declared_conditions=notes, selected=adapter)
    for part, budget in ((1, FIRST_BUDGET), (2, CONFIRM_BUDGET)):
        if part == 2:
            time.sleep(CONFIRM_DELAY)
        result = request(HOSTS[0], cache[HOSTS[0]][0], adapter, budget)
        report.save("pre_authorization_probe", part=part, **result)
        print("До авторизации:", result["outcome"], result.get("status", ""))
    # These Python probes are observations, not a replacement for runtime gates.
    # Refuse a changed transport and require the user's explicit condition declaration.
    current = selected(snapshot(), adapter_id)
    if binding(current) != binding(adapter):
        raise RuntimeError("Binding Wi-Fi изменился. Авторизация не запущена; повтори сценарий.")
    if not confirm_word("Проверь ещё раз: LTE/другие интернет-пути выключены; Campus ещё НЕ авторизован; автоавторизация приостановлена.", "ГОТОВО"):
        report.save("authorization_scenario_skipped", reason="final conditions not confirmed")
        return
    print("Штатный клиент сам выбирает путь и использует собственную DNS-политику. Python не подменяет его транспорт.")
    invoke_authorization(report, executable)
    after = selected(snapshot(), adapter_id)
    report.save("after_authorization_binding", binding_stable=binding(after) == binding(adapter), selected=after)
    for number in range(1, 4):
        result = request(HOSTS[0], cache[HOSTS[0]][0], after, FIRST_BUDGET)
        report.save("post_authorization_probe", number=number, **result)
        print("После попытки:", result["outcome"], result.get("status", ""))
        if number < 3:
            time.sleep(1)
    print("Повторная авторизация автоматически не запускается. Возвращай LTE/Radmin для разбора результатов.")


def run_stage(report, adapter_id, cache, name, instructions, pairs):
    print("\n" + "=" * 65 + "\n" + name + "\n" + instructions)
    if ask("Готово? Введи s, чтобы пропустить этап").lower() == "s":
        report.save("stage_skipped", stage=name)
        return
    print("Убедись: Windows подключена именно к Campus Wi-Fi. Имя SSID не проверяется скриптом автоматически.")
    if input("Введи CAMPUS для подтверждения (любое другое значение — пропустить): ").strip() != "CAMPUS":
        report.save("stage_skipped", stage=name, reason="Campus not confirmed")
        return
    try:
        rows = snapshot()
        adapter = selected(rows, adapter_id)
    except (RuntimeError, subprocess.SubprocessError) as exc:
        print(str(exc))
        report.save("stage_skipped", stage=name, reason=str(exc))
        return
    notes = ask("Укажи фактическое состояние VPN (подключён/не подключился/выключен) и замечания")
    report.save("stage_start", stage=name, declared_conditions=notes, adapters=rows, selected=adapter)
    for fresh in (False, True):
        count = min(pairs, 3) if fresh else pairs
        print(f"\n{'Свежий DNS выбранного Wi-Fi + HTTP' if fresh else 'Только сохранённый IP, без DNS'}: {count} пар")
        for pair in range(1, count + 1):
            for part, budget in ((1, FIRST_BUDGET), (2, CONFIRM_BUDGET)):
                if part == 2:
                    time.sleep(CONFIRM_DELAY)
                result = request(HOSTS[0], cache[HOSTS[0]][0], adapter, budget, fresh)
                report.save("probe", stage=name, pair=pair, part=part, **result)
                print(f"  {pair:02}/{part}: {result['outcome']} {result.get('status', '')} {result['elapsed_ms']} мс")
    print("\nОтдельная проверка портала и API (до 3 с на адрес; без переходов и авторизации).")
    for host in HOSTS[1:]:
        for address in cache.get(host, [])[:3]:
            result = request(host, address, adapter, 3.0)
            report.save("endpoint", stage=name, **result)
            print(f"  {host}: {result['outcome']} {result.get('status', '')}")
    try:
        after = selected(snapshot(), adapter_id)
        stable = binding(after) == binding(adapter)
        report.save("stage_end", stage=name, binding_stable=stable, selected=after)
        if not stable:
            print("ВНИМАНИЕ: параметры адаптера изменились — результаты этапа нельзя считать чистым сравнением.")
    except (RuntimeError, subprocess.SubprocessError) as exc:
        report.save("stage_end", stage=name, binding_stable=False, error=str(exc))
    print("Этап завершён. Статусы HTTP — наблюдения, не подтверждение оператора или авторизации.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pairs", type=int, default=10, choices=range(1, 21))
    parser.add_argument("--vpn-auth", action="store_true", help="Guided pre-established VPN loss scenario with one confirmed installed-client connect")
    parser.add_argument("--app", type=Path, help="Path to installed IS74Wifi.exe for --vpn-auth")
    base = Path(__file__).resolve().parent
    default_output = base.parent / "artifacts" / "campus-offline" if base.name == "experiments" else base / "results"
    parser.add_argument("--output", type=Path, default=default_output)
    parser.add_argument("--resolve-system", choices=HOSTS, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.resolve_system:
        print(json.dumps(sorted({item[4][0] for item in socket.getaddrinfo(args.resolve_system, None, socket.AF_INET, socket.SOCK_STREAM)})))
        return
    if os.name != "nt":
        parser.error("Этот эксперимент поддерживает только Windows")
    report = Report(args.output / dt.datetime.now().strftime("%Y%m%d-%H%M%S-%f"))
    try:
        print("CAMPUS: " + ("сценарий с одной явной авторизацией штатным клиентом" if args.vpn_auth else "автономная диагностика, без SMS и авторизации") + "\n")
        print("Результаты: " + str(report.folder))
        print("Сеть, VPN, службы и настройки скрипт не переключает. Все действия выполняешь ты.")
        print("Закрой IS74Wifi и приостанови его фоновую автоавторизацию, чтобы она не изменила состояние во время опыта.")
        print("Не вводи пароль, номер телефона или токены. q на приглашении / Ctrl+C — выход с сохранением результатов.")
        ask("Сначала оставь LTE/Интернет включённым для получения адресов; VPN по возможности выключи")
        cache = {}
        for host in HOSTS:
            try:
                addresses = system_dns(host)
                report.save("bootstrap_dns", host=host, addresses=addresses, source="Windows system DNS; may use LTE/VPN")
            except (OSError, subprocess.SubprocessError, ValueError) as exc:
                print(f"{host}: DNS не ответил ({type(exc).__name__}).")
                value = ask("Введи известные IPv4 через запятую; пусто — пропустить сервер")
                addresses = [str(ipaddress.IPv4Address(item.strip())) for item in value.split(",") if item.strip()]
                report.save("bootstrap_dns", host=host, addresses=addresses, source="manual")
            cache[host] = addresses
            print(f"  {host}: {', '.join(addresses) or 'нет адресов'}")
        if not cache[HOSTS[0]]:
            raise RuntimeError("Без IP online.susu.ru продолжить нельзя. Верни Интернет и запусти скрипт заново.")
        print("Адреса получены, но их актуальность в Campus ещё не подтверждена. Captive HTTP 200 её не доказывает.")
        rows = snapshot()
        wifi = [row for row in rows if row["wifi"] and row["hardware"]]
        if not wifi:
            raise RuntimeError("Физический Wi-Fi адаптер не найден.")
        print("\nВыбери прямой Wi-Fi адаптер ноутбука:")
        for number, row in enumerate(wifi, 1):
            print(f"  {number}. {row['name']} ({row['status']})")
        adapter_id = choose_adapter(wifi)
        report.save("session", python=sys.version, adapter_id=adapter_id, addresses=cache,
                    note="Python experiment, not C# runtime or packet capture. No operator identity inferred.")
        if args.vpn_auth:
            executable = args.app or Path(os.environ["LOCALAPPDATA"]) / "Programs" / "IS74Wifi" / "IS74Wifi.exe"
            if not executable.is_file() or executable.name.lower() != "is74wifi.exe":
                raise RuntimeError("Не найден IS74Wifi.exe. Укажи --app с путём к программе.")
            if not (Path(os.environ["LOCALAPPDATA"]) / "IS74Wifi" / "secrets.dpapi").is_file():
                raise RuntimeError("Сначала зарегистрируй IS74Wifi обычным способом на LTE, затем повтори опыт. Скрипт не читает секреты.")
            auth_scenario(report, adapter_id, cache, executable)
            report.save("completed", mode="vpn-auth")
            return
        run_stage(report, adapter_id, cache, "1. БЕЗ АВТОРИЗАЦИИ / VPN OFF",
                  "Подключи ноутбук напрямую к Campus Wi-Fi. Отключи LTE/USB-тетеринг, Ethernet с Интернетом, VPN и Radmin VPN.\n"
                  "Удалённая связь пропадёт — это ожидаемо. Не открывай страницу авторизации.\n"
                  "Если Campus уже авторизован, пропусти этот этап: скрипт не снимает авторизацию.", args.pairs)
        run_stage(report, adapter_id, cache, "2. БЕЗ АВТОРИЗАЦИИ / VPN ON",
                  "Временно верни LTE, подключи исследуемый VPN на работающем Интернете.\n"
                  "Затем, НЕ выключая VPN-клиент, отключи LTE и оставь неавторизованный Campus. Radmin выключи.\n"
                  "В замечании укажи connected/reconnecting после отключения LTE. Не авторизуй Campus на этом этапе.", args.pairs)
        print("\nПроверки неавторизованного состояния закончены. Можно завершить опыт прямо сейчас.")
        if ask("Хочешь вручную авторизоваться и проверить ещё два состояния? Введи ДА").upper() == "ДА":
            run_stage(report, adapter_id, cache, "3. ПОСЛЕ РУЧНОЙ АВТОРИЗАЦИИ / VPN OFF",
                      "Выключи VPN. Самостоятельно авторизуй Campus обычным способом.\n"
                      "Затем снова закрой/приостанови IS74Wifi. LTE и Radmin оставь выключенными.", args.pairs)
            run_stage(report, adapter_id, cache, "4. ПОСЛЕ РУЧНОЙ АВТОРИЗАЦИИ / VPN ON",
                      "Оставь авторизованный Campus и включи исследуемый VPN. LTE и Radmin оставь выключенными.", args.pairs)
        report.save("completed")
    except KeyboardInterrupt:
        print("\nОстановлено пользователем. Уже полученные данные сохранены.")
        report.save("interrupted")
    except Exception as exc:
        print(f"\nОстановка: {type(exc).__name__}: {exc}")
        report.save("error", error=type(exc).__name__, detail=str(exc))
    finally:
        report.file.close()
        print("\nРезультаты: " + str(report.folder / "results.jsonl"))
        print("Теперь можно вернуть LTE, Radmin и VPN для удалённой связи. Скрипт сам ничего не восстанавливает.")
        print("Файл содержит локальные IP и имена адаптеров. Не публикуй его в открытом репозитории.")
        if sys.stdin.isatty():
            input("Нажми Enter, чтобы закрыть окно...")


if __name__ == "__main__":
    main()
