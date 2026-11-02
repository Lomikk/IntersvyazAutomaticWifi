using System.Buffers.Binary;
using System.Text;
using IS74Wifi.App;
using static IS74Wifi.App.TerminalCanvas;

internal static class WifiDiagnosticsContractTests
{
    public static async Task RunAsync()
    {
        MenuOrder();
        NativeBuffers();
        RadioAndFrequency();
        ScreenNavigationAndResize();
        MissingAndDeniedData();
        SourceLabelsAndScanResults();
        await ScanNotificationDeliveryAsync();
    }

    internal static WifiDiagnosticSnapshot Fixture(int points = 3)
    {
        var aps = Enumerable.Range(0, points).Select(i => new WifiAccessPoint(i % 2 == 0 ? "Campus Wi-Fi" : "Phone",
            $"02:00:00:00:{i / 256:x2}:{i % 256:x2}", i % 2 == 0 ? 8u : 10u, 90 - i % 90, -55 - i % 40,
            i % 2 == 0 ? 5220000u : 5955000u)).ToArray();
        return new(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero),
        [
            new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Wi-Fi one", 1, true,
                new("Campus Wi-Fi", aps[0].Bssid, 8, 90, 866700, 433300, 44, 5220000, -55), aps, []),
            new(Guid.Parse("00000000-0000-0000-0000-000000000002"), "USB Wi-Fi", 4, false, null, [], [])
        ]);
    }

    private static void MenuOrder()
    {
        var menu = InteractiveTerminalUi.GetPrimaryItems(null);
        Assert(menu.Select(item => item.Hotkey).SequenceEqual("12345670"), "main menu digits/order changed");
        Assert(menu.Select(item => item.Action).SequenceEqual(new[]
        {
            InteractiveMenuAction.Connect, InteractiveMenuAction.SpeedTools, InteractiveMenuAction.ShowWifiDiagnostics,
            InteractiveMenuAction.ShowDetailedStatus, InteractiveMenuAction.OpenSettings, InteractiveMenuAction.OpenMaintenance,
            InteractiveMenuAction.SupportProject, InteractiveMenuAction.Exit
        }), "arrow/Enter order and numeric menu actions differ from the requested order");
        var settings = InteractiveTerminalUi.GetSettingsItems(null);
        Assert(settings.Select(item => item.Hotkey).SequenceEqual("12345670"), "settings hotkeys have a gap or duplicate");
        Assert(settings.Single(item => item.Hotkey == '6').Action == InteractiveMenuAction.ChooseAuthorizationConnections &&
               settings.Single(item => item.Hotkey == '7').Action == InteractiveMenuAction.RefreshServerAddresses,
            "remaining network settings were not renumbered");
        Assert(settings.All(item => !item.Label.Contains("Сеть и диагностика", StringComparison.Ordinal)), "removed diagnostic entry remains");
    }

    private static void NativeBuffers()
    {
        // wlanapi.h: WLAN_CONNECTION_ATTRIBUTES is 604 bytes, association starts at 520.
        var connection = new byte[604];
        var ssid = Encoding.UTF8.GetBytes("Кампус");
        PutU32(connection, 520, (uint)ssid.Length);
        ssid.CopyTo(connection, 524);
        new byte[] { 0xcc, 0x9d, 0xa2, 0xe7, 0x90, 0xb0 }.CopyTo(connection, 560);
        PutU32(connection, 568, 10);
        PutU32(connection, 576, 93);
        PutU32(connection, 580, 1201000);
        PutU32(connection, 584, 573500);
        var parsed = WindowsWifiDiagnostics.ParseConnection(connection);
        Assert(parsed.Ssid == "Кампус" && parsed.Bssid == "cc:9d:a2:e7:90:b0" && parsed.PhyType == 10 &&
               parsed.SignalPercent == 93 && parsed.RxKbps == 1201000 && parsed.TxKbps == 573500 && parsed.RssiDbm is null,
            "native association layout, UTF-8, rates or unknown RSSI were misread");
        Assert(WifiDiagnosticText.Rate(parsed.RxKbps) == "1201 Мбит/с", "native rates are Kbit/s, not bits/s");
        ThrowsInvalid(() => WindowsWifiDiagnostics.ParseConnection(connection.AsSpan(0, 603)));
        PutU32(connection, 520, 33);
        ThrowsInvalid(() => WindowsWifiDiagnostics.ParseConnection(connection));

        // Two fixed-size BSS entries followed by arbitrary IE bytes: never parse/trust those bytes.
        var bss = new byte[8 + 2 * 360 + 3];
        PutU32(bss, 0, (uint)bss.Length);
        PutU32(bss, 4, 2);
        for (var i = 0; i < 2; i++)
        {
            var start = 8 + i * 360;
            PutU32(bss, start, 4);
            Encoding.UTF8.GetBytes("Test").CopyTo(bss, start + 4);
            new byte[] { 2, 3, 4, 5, 6, (byte)i }.CopyTo(bss, start + 40);
            PutU32(bss, start + 52, 8);
            PutU32(bss, start + 56, unchecked((uint)-63));
            PutU32(bss, start + 60, 74);
            PutU32(bss, start + 92, i == 0 ? 2412000u : 5955000u);
            PutU32(bss, start + 352, uint.MaxValue); // hostile IE offset/size ignored
            PutU32(bss, start + 356, uint.MaxValue);
        }
        var points = WindowsWifiDiagnostics.ParseBssList(bss);
        Assert(points.Length == 2 && points[0].RssiDbm == -63 && points[0].SignalPercent == 74 &&
               points[1].Bssid == "02:03:04:05:06:01" && WifiDiagnosticText.Channel(points[1].FrequencyKhz) == 1,
            "native BSS stride, RSSI or frequency was misread");
        PutU32(bss, 4, 3);
        ThrowsInvalid(() => WindowsWifiDiagnostics.ParseBssList(bss));
        PutU32(bss, 4, 2);
        PutU32(bss, 0, uint.MaxValue);
        ThrowsInvalid(() => WindowsWifiDiagnostics.ParseBssList(bss));
        ThrowsInvalid(() => WindowsWifiDiagnostics.ParseBssList(new byte[7]));
    }

    private static void RadioAndFrequency()
    {
        var radio = new byte[28];
        PutU32(radio, 0, 2);
        PutU32(radio, 8, 2); PutU32(radio, 12, 1);
        PutU32(radio, 20, 1); PutU32(radio, 24, 1);
        Assert(WindowsWifiDiagnostics.ParseRadio(radio) == true, "one enabled PHY must keep radio enabled");
        PutU32(radio, 24, 2);
        Assert(WindowsWifiDiagnostics.ParseRadio(radio) == false, "all disabled PHYs must show radio off");
        PutU32(radio, 20, 0); PutU32(radio, 24, 0);
        Assert(WindowsWifiDiagnostics.ParseRadio(radio) is null, "unknown radio must not be presented as off");
        PutU32(radio, 0, 65);
        Assert(WindowsWifiDiagnostics.ParseRadio(radio) is null, "truncated radio data accepted");
        foreach (var (khz, channel, band) in new[]
        {
            (2412000u, 1, "2,4 ГГц"), (2484000u, 14, "2,4 ГГц"),
            (5180000u, 36, "5 ГГц"), (5220000u, 44, "5 ГГц"),
            (5935000u, 2, "6 ГГц"), (5955000u, 1, "6 ГГц"), (7115000u, 233, "6 ГГц")
        })
            Assert(WifiDiagnosticText.Channel(khz) == channel && WifiDiagnosticText.Band(khz) == band,
                "band/channel conversion failed, including overlapping 2.4/6 GHz channel numbers");
        Assert(WifiDiagnosticText.Channel(0) is null && WifiDiagnosticText.Channel(5180123) is null &&
               WifiDiagnosticText.Standard(0) == "недоступно", "missing/unknown data was invented");
    }

    private static void ScreenNavigationAndResize()
    {
        var screen = new WifiDiagnosticsScreen();
        screen.ReplaceSnapshot(Fixture(50));
        var layout = TerminalLayout.Calculate(120, 30);
        var rows = screen.BuildRows(layout.CanvasWidth - 6);
        Assert(rows.Count(row => row.Text == "SSID: Campus Wi-Fi") == 2 && rows.Count(row => row.Text == "SSID: Phone") == 1,
            "AP list was not grouped by SSID separately from the current connection");
        Assert(rows.Any(row => row.Text.StartsWith("* 02:00:00:00:00:00", StringComparison.Ordinal) && row.Color == Palette.Highlight),
            "current BSSID is not visibly marked");
        var header = rows.First(row => row.Text.Contains("ДИАПАЗОН", StringComparison.Ordinal)).Text;
        var currentRow = rows.First(row => row.Text.StartsWith("* 02:00", StringComparison.Ordinal)).Text;
        Assert(currentRow.IndexOf("802.11ac", StringComparison.Ordinal) == header.IndexOf("PHY Windows", StringComparison.Ordinal),
            "word wrapping collapsed the table column spacing");
        Assert(rows.Any(row => row.Text.Contains("Радиосоединение", StringComparison.Ordinal)) &&
               rows.Any(row => row.Text.Contains("не скорость Интернета", StringComparison.Ordinal)), "radio rate is ambiguous");
        Assert(screen.HandleKey(Key(ConsoleKey.R), layout) == WifiScreenAction.Refresh &&
               screen.HandleKey(Key(ConsoleKey.Escape), layout) == WifiScreenAction.Back &&
               screen.HandleKey(Key(ConsoleKey.Enter), layout) == WifiScreenAction.Continue, "screen changed connection or ignored R/Esc");
        screen.HandleKey(Key(ConsoleKey.PageDown), layout);
        Assert(screen.Offset == screen.VisibleRows(layout), "PgDn did not scroll a page");
        screen.HandleKey(Key(ConsoleKey.End), layout);
        foreach (var (width, height) in new[] { (120, 40), (80, 30), (50, 30), (30, 18), (100, 12), (120, 30) })
        {
            var resized = TerminalLayout.Calculate(width, height);
            var frame = screen.BuildFrame(resized, false);
            Assert(frame.GetLength(1) == resized.CanvasWidth && frame.GetLength(0) <= height, "screen ignored terminal resize");
            Assert(screen.BuildRows(resized.CanvasWidth - 6).All(row => row.Text.Length <= resized.CanvasWidth - 6), "row crossed the border");
            var footer = Row(frame, frame.GetLength(0) - 1);
            Assert(footer.Contains("Esc", StringComparison.Ordinal), "return hint disappeared after resize");
            Assert(screen.Offset <= screen.BuildRows(resized.CanvasWidth - 6).Count - screen.VisibleRows(resized), "resize left blank space beyond the list");
        }
        screen.HandleKey(Key(ConsoleKey.Home), layout);
        Assert(screen.Offset == 0, "Home did not return to the current connection");
        screen.ReplaceSnapshot(new(DateTimeOffset.UtcNow, []));
        _ = screen.BuildFrame(layout, false);
        Assert(screen.Offset == 0, "refresh to an empty list retained an invalid scroll offset");
    }

    private static void MissingAndDeniedData()
    {
        var snapshot = Fixture();
        var report = string.Join('\n', WifiDiagnosticText.BuildReport(snapshot));
        Assert(report.Contains("USB Wi-Fi", StringComparison.Ordinal) && report.Contains("радио выключено", StringComparison.Ordinal) &&
               report.Contains("SSID: недоступно; BSSID: недоступно", StringComparison.Ordinal), "disconnected second adapter lost");
        var denied = WindowsWifiDiagnostics.DescribeError("Список", 5);
        Assert(denied.Contains("местоположение", StringComparison.Ordinal) && denied.Contains("код 5", StringComparison.Ordinal), "denied access lacks useful guidance");
        var screen = new WifiDiagnosticsScreen();
        screen.ReplaceSnapshot(WifiDiagnosticSnapshot.Unavailable(denied));
        Assert(screen.BuildRows(72).Any(row => row.Color == Palette.Warning), "API error was hidden");
        Assert(!WifiDiagnosticText.Safe("name\u001b[31m\r\n").Any(char.IsControl), "SSID can inject terminal control characters");
    }

    private static void SourceLabelsAndScanResults()
    {
        var fixture = Fixture();
        var adapter = fixture.Adapters[0] with
        {
            Connection = fixture.Adapters[0].Connection! with { SignalPercent = 99, RssiDbm = -25 },
            AccessPoints = [new("Phone", "02:00:00:00:00:99", 8, 100, -25, 2462000)]
        };
        Assert(WifiDiagnosticText.HasPhyBandConflict(8, 2462000) &&
               !WifiDiagnosticText.HasPhyBandConflict(8, 5220000) &&
               !WifiDiagnosticText.HasPhyBandConflict(7, 2462000) &&
               !WifiDiagnosticText.HasPhyBandConflict(8, 0), "PHY conflict inferred without known contradictory frequency");
        foreach (var status in Enum.GetValues<WifiScanStatus>())
        {
            var snapshot = fixture with { Adapters = [adapter with { ScanStatus = status }] };
            var screen = new WifiDiagnosticsScreen(); screen.ReplaceSnapshot(snapshot);
            var text = string.Join('\n', screen.BuildRows(110).Select(row => row.Text));
            var report = string.Join('\n', WifiDiagnosticText.BuildReport(snapshot));
            foreach (var value in new[] { text, report })
            {
                Assert(value.Contains("Качество текущего соединения (Windows): 99%", StringComparison.Ordinal) &&
                       value.Contains("RSSI из кеша BSSID: -25 dBm", StringComparison.Ordinal), "association quality and cached RSSI were merged");
                Assert(value.Contains("противоречие с диапазоном", StringComparison.Ordinal) &&
                       !value.Contains("802.11n", StringComparison.Ordinal), "contradictory VHT silently rewritten");
                Assert(value.Contains(WifiDiagnosticText.Scan(status), StringComparison.Ordinal), "scan result missing or falsely claims freshness");
            }
            foreach (var width in new[] { 30, 50, 80, 120 })
                Assert(screen.BuildRows(width - 7).All(row => row.Text.Length <= width - 7), "new labels overflow narrow screen");
        }
    }

    private static async Task ScanNotificationDeliveryAsync()
    {
        // A successful completion just beyond the former four-second boundary must be accepted.
        var late = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliver = Task.Run(async () => { await Task.Delay(4100); late.TrySetResult(7); });
        await WindowsWifiDiagnostics.WaitForScanNotificationsAsync([late.Task], CancellationToken.None);
        await deliver;
        var missing = new TaskCompletionSource<uint>();
        try
        {
            await WindowsWifiDiagnostics.WaitForScanNotificationsAsync([missing.Task], CancellationToken.None);
            throw new InvalidOperationException("missing notification did not time out");
        }
        catch (TimeoutException) { }
        Assert(!missing.Task.IsCompleted, "timeout manufactured a failed/completed scan");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try
        {
            await WindowsWifiDiagnostics.WaitForScanNotificationsAsync([missing.Task], stop.Token);
            throw new InvalidOperationException("scan wait ignored cancellation");
        }
        catch (OperationCanceledException) { }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);
    private static string Row(Cell[,] frame, int y) => new(Enumerable.Range(0, frame.GetLength(1)).Select(x => frame[y, x].Character).ToArray());
    private static void PutU32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void ThrowsInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("invalid native buffer was accepted");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
