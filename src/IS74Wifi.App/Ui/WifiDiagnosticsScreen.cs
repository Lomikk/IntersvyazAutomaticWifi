using static IS74Wifi.App.TerminalCanvas;

namespace IS74Wifi.App;

internal enum WifiScreenAction { Continue, Refresh, Back }
internal sealed record WifiDiagnosticRow(string Text, Palette Color);

internal sealed class WifiDiagnosticsScreen
{
    private List<WifiDiagnosticRow>? cachedRows;
    private int cachedWidth;
    internal WifiDiagnosticSnapshot Snapshot { get; private set; } = new(DateTimeOffset.UtcNow, []);
    internal int Offset { get; private set; }
    internal void ReplaceSnapshot(WifiDiagnosticSnapshot snapshot)
    {
        Snapshot = snapshot;
        cachedRows = null;
    }
    private static int Height(TerminalLayout layout) => Math.Clamp(layout.TerminalHeight, 8, TerminalLayout.CanvasHeight);
    internal int VisibleRows(TerminalLayout layout) => Height(layout) - 7;

    internal WifiScreenAction HandleKey(ConsoleKeyInfo key, TerminalLayout layout)
    {
        var count = BuildRows(layout.CanvasWidth - 6).Count;
        var visible = VisibleRows(layout);
        switch (key.Key)
        {
            case ConsoleKey.Escape: return WifiScreenAction.Back;
            case ConsoleKey.R: return WifiScreenAction.Refresh;
            case ConsoleKey.UpArrow: Offset--; break;
            case ConsoleKey.DownArrow: Offset++; break;
            case ConsoleKey.PageUp: Offset -= visible; break;
            case ConsoleKey.PageDown: Offset += visible; break;
            case ConsoleKey.Home: Offset = 0; break;
            case ConsoleKey.End: Offset = count; break;
        }
        Offset = ListWindow.FromOffset(count, Offset, visible).Offset;
        return WifiScreenAction.Continue;
    }

    internal List<WifiDiagnosticRow> BuildRows(int width)
    {
        width = Math.Max(1, width);
        if (cachedRows is not null && cachedWidth == width) return cachedRows;
        var rows = new List<WifiDiagnosticRow>();
        void Add(string text, Palette color = Palette.Text)
        {
            if (text.Length <= width) { rows.Add(new(text, color)); return; }
            foreach (var line in WrapText(text, width)) rows.Add(new(line, color));
        }
        if (Snapshot.Error is { } error) Add(WifiDiagnosticText.Safe(error), Palette.Warning);
        if (Snapshot.Adapters.Length == 0) Add("Wi-Fi-адаптеры не обнаружены или недоступны.", Palette.Dim);
        foreach (var adapter in Snapshot.Adapters)
        {
            Add(WifiDiagnosticText.Safe(adapter.Name) + " · " + WifiDiagnosticText.State(adapter), Palette.Highlight);
            var c = adapter.Connection;
            if (c is not null)
            {
                Add("SSID: " + WifiDiagnosticText.Ssid(c.Ssid), Palette.Bright);
                Add("BSSID: " + c.Bssid);
                Add(WifiDiagnosticText.ConnectionQuality(c.SignalPercent), SignalColor(c.SignalPercent));
                Add(WifiDiagnosticText.CachedRssi(c.RssiDbm));
                Add($"{WifiDiagnosticText.Band(c.FrequencyKhz)} · канал {WifiDiagnosticText.Number(c.Channel)} · PHY по Windows: {WifiDiagnosticText.Phy(c.PhyType, c.FrequencyKhz)}");
                Add($"Радиосоединение: RX {WifiDiagnosticText.Rate(c.RxKbps)} · TX {WifiDiagnosticText.Rate(c.TxKbps)}");
                Add("Радиоскорость по Windows на момент снимка; не скорость Интернета.", Palette.Dim);
            }
            else Add("Текущее подключение: недоступно.", Palette.Dim);
            Add("Обновление списка: " + WifiDiagnosticText.Scan(adapter.ScanStatus),
                adapter.ScanStatus is WifiScanStatus.Failed or WifiScanStatus.NotConfirmed ? Palette.Warning : Palette.Dim);
            foreach (var message in adapter.Errors) Add(WifiDiagnosticText.Safe(message), Palette.Warning);
            Add("");
            Add("Доступные сети и точки доступа", Palette.Highlight);
            Add("PHY по Windows; сигнал: качество драйвера / RSSI из кеша BSS.", Palette.Dim);
            if (adapter.AccessPoints.Length == 0) Add("Список пуст или недоступен.", Palette.Dim);
            foreach (var group in WifiDiagnosticText.Groups(adapter))
            {
                Add("SSID: " + WifiDiagnosticText.Ssid(group.Key), Palette.Bright);
                if (width >= 72) Add($"  {"BSSID",-17}  {"ДИАПАЗОН",10}  {"КАНАЛ",5}  {"PHY Windows",-11}  СИГНАЛ", Palette.Dim);
                foreach (var ap in WifiDiagnosticText.Points(group))
                {
                    var current = ap.Bssid == c?.Bssid;
                    var marker = current ? "*" : " ";
                    var color = current ? Palette.Highlight : SignalColor(ap.SignalPercent);
                    if (width >= 72)
                        Add($"{marker} {ap.Bssid,-17}  {WifiDiagnosticText.Band(ap.FrequencyKhz),10}  {WifiDiagnosticText.Number(WifiDiagnosticText.Channel(ap.FrequencyKhz)),5}  {(WifiDiagnosticText.HasPhyBandConflict(ap.PhyType, ap.FrequencyKhz) ? "VHT (!)" : WifiDiagnosticText.Standard(ap.PhyType)),-11}  {WifiDiagnosticText.Signal(ap.SignalPercent, ap.RssiDbm)}", color);
                    else
                    {
                        Add($"{marker} {ap.Bssid}  {WifiDiagnosticText.Signal(ap.SignalPercent, ap.RssiDbm)}", color);
                        Add($"  {WifiDiagnosticText.Band(ap.FrequencyKhz)} · канал {WifiDiagnosticText.Number(WifiDiagnosticText.Channel(ap.FrequencyKhz))} · PHY Windows: {WifiDiagnosticText.Standard(ap.PhyType)}", Palette.Dim);
                    }
                    if (WifiDiagnosticText.HasPhyBandConflict(ap.PhyType, ap.FrequencyKhz))
                        Add("  (!) VHT по Windows: противоречие с диапазоном 2,4 ГГц.", Palette.Warning);
                }
            }
            Add("");
        }
        cachedWidth = width;
        return cachedRows = rows;
    }

    internal Cell[,] BuildFrame(TerminalLayout layout, bool loading)
    {
        var height = Height(layout);
        var canvas = Create(layout.CanvasWidth, height);
        var width = layout.CanvasWidth - 2;
        DrawBox(canvas, 1, 0, width, height - 3, "СИГНАЛ WI-FI И ТОЧКИ ДОСТУПА");
        var contentWidth = width - 4;
        Put(canvas, 3, 1, Truncate(loading ? "Получаю данные Wi-Fi..." :
            $"Кеш прочитан: {Snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss} · * текущий BSSID", contentWidth), Palette.Dim);
        var rows = BuildRows(contentWidth);
        var window = ListWindow.FromOffset(rows.Count, Offset, VisibleRows(layout));
        Offset = window.Offset;
        for (var i = 0; i < window.Count; i++) Put(canvas, 3, 3 + i, rows[window.Offset + i].Text, rows[window.Offset + i].Color);
        Center(canvas, height - 3, Truncate($"Строки {window.FirstDisplayIndex}–{window.LastDisplayIndex}/{rows.Count} · без автообновления", layout.CanvasWidth), Palette.Dim);
        Center(canvas, height - 2, Truncate("↑↓ PgUp/PgDn Home/End — прокрутка", layout.CanvasWidth), Palette.Dim);
        Center(canvas, height - 1, Truncate(loading ? "Esc — назад · обновление..." : "R — обновить точки · Esc — назад", layout.CanvasWidth), Palette.Dim);
        return canvas;
    }

    private static Palette SignalColor(int? quality) => quality switch
    {
        >= 70 => Palette.Good, >= 40 => Palette.Warning, not null => Palette.Error, _ => Palette.Dim
    };
}
