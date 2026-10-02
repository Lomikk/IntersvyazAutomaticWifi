using IS74Wifi.Core;
using static IS74Wifi.App.TerminalCanvas;

namespace IS74Wifi.App;

internal enum AdapterSelectionOutcome
{
    Continue,
    Confirmed,
    Cancelled
}

internal readonly record struct AdapterSelectionResult(
    AdapterSelectionOutcome Outcome,
    string? SelectedId)
{
    internal static AdapterSelectionResult Continue =>
        new(AdapterSelectionOutcome.Continue, null);
}

internal readonly record struct AdapterSelectionItem(
    string Shortcut,
    string Label,
    string? Id,
    bool IsCurrent)
{
    internal string DisplayLabel => IsCurrent ? Label + " · текущий" : Label;
}

internal sealed class AdapterSelectionScreen
{
    private readonly string? originalId;
    private readonly List<AdapterSelectionItem> items;

    internal AdapterSelectionScreen(IReadOnlyList<PhysicalAdapter> adapters, string? currentId)
    {
        originalId = currentId;
        items = BuildItems(adapters, currentId);
        SelectedIndex = Math.Max(0, items.FindIndex(item => AdapterIdsEqual(item.Id, currentId)));
    }

    internal IReadOnlyList<AdapterSelectionItem> Items => items;
    internal int SelectedIndex { get; private set; }

    internal int GetVisibleRowCount(TerminalLayout layout)
    {
        var boxHeight = Math.Max(8, TerminalLayout.CanvasHeight - 3);
        // One instruction row, one summary row and box padding.
        return ListWindow.VisibleRowsForBox(boxHeight, reservedRows: 6);
    }

    internal AdapterSelectionResult HandleKey(ConsoleKeyInfo key, int visibleRows)
    {
        if (items.Count == 0)
        {
            return new AdapterSelectionResult(AdapterSelectionOutcome.Cancelled, originalId);
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            SelectedIndex = (SelectedIndex - 1 + items.Count) % items.Count;
        }
        else if (key.Key == ConsoleKey.DownArrow)
        {
            SelectedIndex = (SelectedIndex + 1) % items.Count;
        }
        else if (key.Key == ConsoleKey.PageUp)
        {
            SelectedIndex = Math.Max(0, SelectedIndex - Math.Max(1, visibleRows));
        }
        else if (key.Key == ConsoleKey.PageDown)
        {
            SelectedIndex = Math.Min(items.Count - 1, SelectedIndex + Math.Max(1, visibleRows));
        }
        else if (key.Key == ConsoleKey.Home)
        {
            SelectedIndex = 0;
        }
        else if (key.Key == ConsoleKey.End)
        {
            SelectedIndex = items.Count - 1;
        }
        else if (key.Key == ConsoleKey.Escape)
        {
            return new AdapterSelectionResult(AdapterSelectionOutcome.Cancelled, originalId);
        }
        else if (key.Key == ConsoleKey.Enter)
        {
            return new AdapterSelectionResult(AdapterSelectionOutcome.Confirmed, items[SelectedIndex].Id);
        }
        else
        {
            var shortcut = ShortcutFromKey(key);
            if (shortcut is not null)
            {
                var match = items.FindIndex(item =>
                    string.Equals(item.Shortcut, shortcut, StringComparison.OrdinalIgnoreCase));
                if (match >= 0)
                {
                    SelectedIndex = match;
                    return new AdapterSelectionResult(AdapterSelectionOutcome.Confirmed, items[match].Id);
                }
            }
        }

        return AdapterSelectionResult.Continue;
    }

    internal Cell[,] BuildFrame(TerminalLayout layout)
    {
        var canvas = Create(layout.CanvasWidth, TerminalLayout.CanvasHeight);
        var boxY = 1;
        var boxHeight = Math.Max(8, TerminalLayout.CanvasHeight - 3);
        var boxX = layout.Compact
            ? 1
            : Math.Max(1, (layout.CanvasWidth - Math.Min(layout.CanvasWidth - 2, 112)) / 2);
        var boxWidth = layout.Compact
            ? Math.Max(20, layout.CanvasWidth - 2)
            : Math.Min(layout.CanvasWidth - 2, 112);

        DrawBox(canvas, boxX, boxY, boxWidth, boxHeight, "АДАПТЕР ДЛЯ АВТОРИЗАЦИИ");

        var contentX = boxX + 3;
        var contentY = boxY + 2;
        var contentWidth = Math.Max(1, boxWidth - 6);
        Put(canvas, contentX, contentY,
            Truncate("Выберите маршрут запросов к порталу Интерсвязи", contentWidth), Palette.Text);

        var visibleRows = GetVisibleRowCount(layout);
        var window = ListWindow.AroundSelection(items.Count, SelectedIndex, visibleRows);
        var firstOptionRow = contentY + 2;
        for (var row = 0; row < window.Count; row++)
        {
            var optionIndex = window.Offset + row;
            DrawOption(
                canvas,
                contentX,
                firstOptionRow + row,
                contentWidth,
                items[optionIndex],
                optionIndex == SelectedIndex);
        }

        var selected = items[Math.Clamp(SelectedIndex, 0, items.Count - 1)];
        Put(
            canvas,
            contentX,
            boxY + boxHeight - 2,
            Truncate(
                $"Пункты {window.FirstDisplayIndex}–{window.LastDisplayIndex} / {items.Count}   Выбрано: [{selected.Shortcut}] {selected.DisplayLabel}",
                contentWidth),
            Palette.Dim);

        Center(
            canvas,
            TerminalLayout.CanvasHeight - 1,
            "↑↓ выбрать   PgUp/PgDn страница   Home/End край   Enter применить   Esc назад",
            Palette.Dim);
        return canvas;
    }

    private static List<AdapterSelectionItem> BuildItems(
        IReadOnlyList<PhysicalAdapter> adapters,
        string? currentId)
    {
        var result = new List<AdapterSelectionItem>
        {
            new("0", "Автоматически — выбрать физический адаптер", null, AdapterIdsEqual(null, currentId)),
            new("S", "Системный маршрут — без обхода VPN", PhysicalAdapterSelection.SystemRoute,
                AdapterIdsEqual(PhysicalAdapterSelection.SystemRoute, currentId))
        };

        for (var i = 0; i < adapters.Count; i++)
        {
            var adapter = adapters[i];
            var state = adapter.CanConnect ? "доступен" : "не подключён";
            var ssid = string.IsNullOrWhiteSpace(adapter.Ssid) ? "" : $" · {adapter.Ssid}";
            var virtualMark = adapter.LooksVirtual ? " · виртуальный" : "";
            result.Add(new AdapterSelectionItem(
                (i + 1).ToString(),
                $"{adapter.Name}{ssid}{virtualMark} — {state}",
                adapter.Id,
                AdapterIdsEqual(adapter.Id, currentId)));
        }

        return result;
    }

    private static string? ShortcutFromKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.S)
        {
            return "S";
        }

        if (key.KeyChar is >= '0' and <= '9')
        {
            return key.KeyChar.ToString();
        }

        return null;
    }

    private static bool AdapterIdsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void DrawOption(
        Cell[,] canvas,
        int x,
        int y,
        int width,
        AdapterSelectionItem option,
        bool isSelected)
    {
        Put(canvas, x, y, isSelected ? "› " : "  ", isSelected ? Palette.Highlight : Palette.Text);
        Put(canvas, x + 2, y, $"[{option.Shortcut}]", Palette.Dim);
        Put(canvas, x + 7, y, Truncate(option.DisplayLabel, Math.Max(1, width - 7)),
            isSelected ? Palette.Bright : Palette.Text);
    }
}
