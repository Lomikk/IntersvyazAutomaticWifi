using IS74Wifi.Core;
using static IS74Wifi.App.TerminalCanvas;

namespace IS74Wifi.App;

internal enum ConnectionSelectionOutcome { Continue, Confirmed, Cancelled }

internal sealed record ConnectionSelectionItem(string Label, string? Ssid, SelectedNetworkAdapter? Adapter, bool Selected);

/// <summary>Edits a draft only. Enter applies the whole list; Escape discards it.</summary>
internal sealed class ConnectionSelectionScreen
{
    private readonly List<ConnectionSelectionItem> items = [];
    private string? ssidInput;
    private string message = "Wi-Fi — по имени сети; USB/Ethernet — по устройству.";

    internal ConnectionSelectionScreen(IReadOnlyList<AgentNetworkAdapterOption> adapters, AuthorizationConnectionSelection current)
    {
        var physical = adapters.Where(adapter => !adapter.LooksVirtual).ToArray();
        var ssids = new[] { AuthorizationConnectionSelection.CampusWifi }.Concat(current.WifiSsids)
            .Concat(physical.Where(adapter => adapter.IsWifi).Select(adapter => adapter.Ssid)
                .OfType<string>().Where(AuthorizationConnectionSelection.IsValidSsid))
            .Distinct(StringComparer.Ordinal);
        foreach (var ssid in ssids)
            items.Add(new("Wi-Fi · " + ssid, ssid, null, current.WifiSsids.Contains(ssid, StringComparer.Ordinal)));
        var wired = physical.Where(adapter => !adapter.IsWifi).ToArray();
        foreach (var adapter in wired)
            items.Add(new($"{adapter.Name} · {(adapter.CanConnect ? "подключён" : "не подключён")}", null,
                new(adapter.Id, adapter.Name), current.Adapters.Any(saved => string.Equals(saved.Id, adapter.Id, StringComparison.OrdinalIgnoreCase))));
        foreach (var saved in current.Adapters.Where(saved => !wired.Any(adapter => string.Equals(adapter.Id, saved.Id, StringComparison.OrdinalIgnoreCase))))
            items.Add(new(saved.Name + " · устройство отсутствует", null, saved, true));
    }

    internal IReadOnlyList<ConnectionSelectionItem> Items => items;
    internal int SelectedIndex { get; private set; }
    internal bool AddingSsid => ssidInput is not null;
    internal AuthorizationConnectionSelection Selection => new()
    {
        WifiSsids = items.Where(item => item.Selected && item.Ssid is not null).Select(item => item.Ssid!).ToArray(),
        Adapters = items.Where(item => item.Selected && item.Adapter is not null).Select(item => item.Adapter!).ToArray()
    };

    internal int GetVisibleRowCount(TerminalLayout layout) =>
        ListWindow.VisibleRowsForBox(Math.Max(8, TerminalLayout.CanvasHeight - 3), reservedRows: 8);

    internal bool AddWifi(string ssid)
    {
        if (!AuthorizationConnectionSelection.IsValidSsid(ssid))
        {
            message = "Введите имя Wi-Fi: от 1 до 32 байт UTF-8, без управляющих символов.";
            return false;
        }
        var index = items.FindIndex(item => string.Equals(item.Ssid, ssid, StringComparison.Ordinal));
        if (index < 0)
        {
            items.Add(new("Wi-Fi · " + ssid, ssid, null, true));
            index = items.Count - 1;
        }
        items[index] = items[index] with { Selected = true };
        SelectedIndex = index;
        message = "Wi-Fi добавлен в выбор. Enter — сохранить список.";
        return true;
    }

    internal ConnectionSelectionOutcome HandleKey(ConsoleKeyInfo key, int visibleRows)
    {
        if (ssidInput is not null)
        {
            if (key.Key == ConsoleKey.Escape) ssidInput = null;
            else if (key.Key == ConsoleKey.Enter)
            {
                if (AddWifi(ssidInput)) ssidInput = null;
            }
            else if (key.Key == ConsoleKey.Backspace && ssidInput.Length > 0) ssidInput = ssidInput[..^1];
            else if (!char.IsControl(key.KeyChar) && ssidInput.Length < 64) ssidInput += key.KeyChar;
            return ConnectionSelectionOutcome.Continue;
        }
        switch (key.Key)
        {
            case ConsoleKey.Escape: return ConnectionSelectionOutcome.Cancelled;
            case ConsoleKey.Enter: return ConnectionSelectionOutcome.Confirmed;
            case ConsoleKey.A:
                ssidInput = "";
                message = "Введите точное имя Wi-Fi; пробелы и регистр сохраняются.";
                break;
            case ConsoleKey.Spacebar: Toggle(SelectedIndex); break;
            case ConsoleKey.UpArrow: SelectedIndex = (SelectedIndex - 1 + items.Count) % items.Count; break;
            case ConsoleKey.DownArrow: SelectedIndex = (SelectedIndex + 1) % items.Count; break;
            case ConsoleKey.PageUp: SelectedIndex = Math.Max(0, SelectedIndex - Math.Max(1, visibleRows)); break;
            case ConsoleKey.PageDown: SelectedIndex = Math.Min(items.Count - 1, SelectedIndex + Math.Max(1, visibleRows)); break;
            case ConsoleKey.Home: SelectedIndex = 0; break;
            case ConsoleKey.End: SelectedIndex = items.Count - 1; break;
        }
        return ConnectionSelectionOutcome.Continue;
    }

    internal void Toggle(int index) => items[index] = items[index] with { Selected = !items[index].Selected };

    internal Cell[,] BuildFrame(TerminalLayout layout)
    {
        var canvas = Create(layout.CanvasWidth, TerminalLayout.CanvasHeight);
        var width = Math.Min(layout.CanvasWidth - 2, 112);
        var x = Math.Max(1, (layout.CanvasWidth - width) / 2);
        var height = TerminalLayout.CanvasHeight - 3;
        DrawBox(canvas, x, 1, width, height, "ПОДКЛЮЧЕНИЯ ДЛЯ АВТОРИЗАЦИИ");
        var contentWidth = Math.Max(1, width - 6);
        Put(canvas, x + 3, 3, Truncate("Отметьте подключения, через которые разрешена авторизация.", contentWidth), Palette.Text);
        var window = ListWindow.AroundSelection(items.Count, SelectedIndex, GetVisibleRowCount(layout));
        for (var row = 0; row < window.Count; row++)
        {
            var index = window.Offset + row;
            var item = items[index];
            Put(canvas, x + 3, 5 + row, Truncate($"{(index == SelectedIndex ? "›" : " ")} [{(item.Selected ? "x" : " ")}] {item.Label}", contentWidth),
                index == SelectedIndex ? Palette.Bright : Palette.Text);
        }
        Put(canvas, x + 3, height - 3, Truncate(ssidInput is not null ? message :
            $"Выбрано: {items.Count(item => item.Selected)} · Пункты {window.FirstDisplayIndex}–{window.LastDisplayIndex}/{items.Count}", contentWidth), Palette.Dim);
        Put(canvas, x + 3, height - 1, Truncate(ssidInput is null ? message : "SSID: " + ssidInput + "_", contentWidth), Palette.Text);
        Center(canvas, TerminalLayout.CanvasHeight - 2,
            ssidInput is not null ? "Введите имя сети Wi-Fi (SSID)" : layout.Compact
                ? "↑↓ выбрать · Пробел отметить · A Wi-Fi" : "↑↓ выбрать · Пробел отметить · A добавить Wi-Fi", Palette.Dim);
        Center(canvas, TerminalLayout.CanvasHeight - 1, ssidInput is not null ? "Enter добавить · Esc назад" : layout.Compact
            ? "Enter сохранить · Esc отменить" : "PgUp/PgDn · Home/End · Enter сохранить · Esc отменить", Palette.Dim);
        return canvas;
    }
}
