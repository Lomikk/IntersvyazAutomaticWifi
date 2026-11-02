using System.Globalization;

namespace IS74Wifi.App;

// Local, on-demand radio observations only. Never serialized into agent state or telemetry.
internal sealed record WifiAccessPoint(string Ssid, string Bssid, uint PhyType,
    int? SignalPercent, int? RssiDbm, uint FrequencyKhz);

internal sealed record WifiConnection(string Ssid, string Bssid, uint PhyType,
    int? SignalPercent, uint RxKbps, uint TxKbps, int? Channel = null,
    uint FrequencyKhz = 0, int? RssiDbm = null);

internal sealed record WifiAdapterSnapshot(Guid Id, string Name, uint State, bool? RadioOn,
    WifiConnection? Connection, WifiAccessPoint[] AccessPoints, string[] Errors);

internal sealed record WifiDiagnosticSnapshot(DateTimeOffset CapturedAtUtc,
    WifiAdapterSnapshot[] Adapters, string? Error = null)
{
    internal static WifiDiagnosticSnapshot Unavailable(string error) => new(DateTimeOffset.UtcNow, [], error);
}

internal static class WifiDiagnosticText
{
    internal const string Unavailable = "недоступно";
    internal static string Safe(string? value) => string.IsNullOrEmpty(value) ? "<скрытая сеть>" :
        new string(value.Select(ch => char.IsControl(ch) || ch is '\u2028' or '\u2029' ||
            ch is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069' ? '�' : ch).ToArray());
    internal static string Ssid(string value) => Safe(value);
    internal static string State(WifiAdapterSnapshot adapter) => adapter.RadioOn == false ? "радио выключено" : adapter.State switch
    {
        0 => "адаптер не готов", 1 or 2 => "подключён", 3 => "отключение", 4 => "не подключён",
        5 => "подключение", 6 => "поиск сети", 7 => "проверка подлинности", _ => Unavailable
    };
    internal static string Standard(uint phy) => phy switch
    {
        1 => "FHSS", 2 => "DSSS", 3 => "IR", 4 => "802.11a", 5 => "802.11b", 6 => "802.11g",
        7 => "802.11n", 8 => "802.11ac", 9 => "802.11ad", 10 => "802.11ax", 11 => "802.11be", _ => Unavailable
    };
    internal static string Band(uint khz) => khz switch
    {
        >= 2400000 and <= 2500000 => "2,4 ГГц",
        >= 4900000 and < 5925000 => "5 ГГц",
        >= 5925000 and <= 7125000 => "6 ГГц", _ => Unavailable
    };
    internal static int? Channel(uint khz)
    {
        var mhz = khz / 1000;
        if (khz % 1000 != 0) return null;
        if (mhz == 2484) return 14;
        if (mhz is >= 2412 and <= 2472 && (mhz - 2407) % 5 == 0) return (int)(mhz - 2407) / 5;
        if (mhz is >= 5000 and < 5925 && mhz % 5 == 0) return (int)(mhz - 5000) / 5;
        if (mhz == 5935) return 2;
        if (mhz is >= 5955 and <= 7115 && (mhz - 5950) % 5 == 0) return (int)(mhz - 5950) / 5;
        return null;
    }
    internal static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? Unavailable;
    internal static string Signal(int? percent, int? rssi) =>
        $"{(percent is { } p ? p + "%" : Unavailable)} / {(rssi is { } dbm ? dbm + " dBm" : Unavailable)}";
    internal static string Rate(uint kbps) => kbps == 0 ? Unavailable :
        (kbps / 1000d).ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")) + " Мбит/с";
    internal static IEnumerable<IGrouping<string, WifiAccessPoint>> Groups(WifiAdapterSnapshot adapter) =>
        adapter.AccessPoints.GroupBy(ap => ap.Ssid, StringComparer.Ordinal)
            .OrderByDescending(group => adapter.Connection?.Ssid == group.Key)
            .ThenBy(group => group.Key, StringComparer.Ordinal);
    internal static IEnumerable<WifiAccessPoint> Points(IGrouping<string, WifiAccessPoint> group) =>
        group.OrderByDescending(ap => ap.SignalPercent ?? -1).ThenBy(ap => ap.Bssid, StringComparer.Ordinal);

    internal static IReadOnlyList<string> BuildReport(WifiDiagnosticSnapshot snapshot)
    {
        var lines = new List<string>
        {
            "=== Wi-Fi: текущее подключение ===",
            $"Снимок: {snapshot.CapturedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}",
            "Скорости приёма/передачи ниже — скорости радиосоединения, а не скорость Интернета."
        };
        if (snapshot.Error is { } error) lines.Add(Safe(error));
        if (snapshot.Adapters.Length == 0) lines.Add("Wi-Fi-адаптеры не обнаружены или недоступны.");
        foreach (var adapter in snapshot.Adapters)
        {
            lines.Add($"Адаптер: {Safe(adapter.Name)}; GUID: {adapter.Id}; состояние: {State(adapter)}");
            var c = adapter.Connection;
            lines.Add($"  SSID: {(c is null ? Unavailable : Ssid(c.Ssid))}; BSSID: {c?.Bssid ?? Unavailable}");
            lines.Add($"  Сигнал: {Signal(c?.SignalPercent, c?.RssiDbm)}");
            lines.Add($"  Диапазон: {Band(c?.FrequencyKhz ?? 0)}; канал: {Number(c?.Channel)}; стандарт: {Standard(c?.PhyType ?? 0)}");
            lines.Add($"  Приём: {Rate(c?.RxKbps ?? 0)}; передача: {Rate(c?.TxKbps ?? 0)}");
            foreach (var message in adapter.Errors) lines.Add("  " + Safe(message));
        }
        lines.Add("");
        lines.Add("=== Доступные беспроводные сети ===");
        lines.Add("Список из кеша Windows; при создании отчёта сканирование не запрашивается.");
        foreach (var adapter in snapshot.Adapters)
        {
            lines.Add($"Адаптер: {Safe(adapter.Name)}");
            if (adapter.AccessPoints.Length == 0) lines.Add("  Точки доступа не обнаружены или список недоступен.");
            foreach (var group in Groups(adapter))
            {
                lines.Add("  SSID: " + Ssid(group.Key));
                foreach (var ap in Points(group))
                    lines.Add($"    {(ap.Bssid == adapter.Connection?.Bssid ? "*" : " ")} {ap.Bssid}; {Band(ap.FrequencyKhz)}; канал {Number(Channel(ap.FrequencyKhz))}; {Standard(ap.PhyType)}; {Signal(ap.SignalPercent, ap.RssiDbm)}");
            }
        }
        lines.Add("* — текущий BSSID. Данные окружающих сетей остаются в локальном отчёте и не отправляются в статистику.");
        return lines;
    }
}
