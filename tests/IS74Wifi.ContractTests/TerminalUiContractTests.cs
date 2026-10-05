using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.App;
using IS74Wifi.Core;
using static IS74Wifi.App.TerminalCanvas;

internal static class TerminalUiContractTests
{
    public static Task RunAsync()
    {
        LayoutFor80By30();
        LayoutFor120By30();
        NarrowLayout();
        LongTextWrapsInMemory();
        RepeatedFrameProducesNoCellWrites();
        ResizeAndInvalidationForceFullRender();
        AdapterScreenHasAutomaticAndSystemWithoutAdapters();
        AdapterScreenHandlesLongNamesAndResize();
        AdapterScreenNavigatesLargeLists();
        AdapterScreenFastKeysAndEscape();
        SharedListWindowKeepsLargeListsVisible();
        PathPaneFormatsIndependentCountdowns();
        return Task.CompletedTask;
    }

    private static void LayoutFor80By30()
    {
        var layout = TerminalLayout.Calculate(80, 30);
        Assert(layout.SupportsInteractiveSession, "80x30 must support the interactive terminal");
        Assert(layout.SupportsRichLayout, "80x30 must be the minimum rich layout");
        Assert(!layout.Compact, "80 columns must not use compact layout");
        Assert(layout.CanvasWidth == 79 && layout.RenderLeft == 0, "80-column canvas geometry changed");
        Assert(layout.PaneWidth == 37 && layout.LeftPaneX == 1 && layout.RightPaneX == 41,
            "80-column pane geometry changed");

        var frame = Create(layout.CanvasWidth, TerminalLayout.CanvasHeight);
        Assert(frame.GetLength(0) == 30 && frame.GetLength(1) == 79,
            "80x30 terminal must produce the existing 79x30 drawable frame");
    }

    private static void LayoutFor120By30()
    {
        var layout = TerminalLayout.Calculate(120, 30);
        Assert(layout.SupportsRichLayout, "120x30 must support rich layout");
        Assert(!layout.Compact, "120 columns must not use compact layout");
        Assert(layout.CanvasWidth == 116 && layout.RenderLeft == 2, "wide canvas geometry changed");
        Assert(layout.PaneWidth == 55 && layout.LeftPaneX == 1 && layout.RightPaneX == 60,
            "wide pane geometry changed");

        var frame = Create(layout.CanvasWidth, TerminalLayout.CanvasHeight);
        Assert(frame.GetLength(0) == 30 && frame.GetLength(1) == 116,
            "120x30 terminal must preserve the preferred 116x30 drawable frame");
    }

    private static void NarrowLayout()
    {
        var layout = TerminalLayout.Calculate(50, 30);
        Assert(layout.SupportsInteractiveSession, "50x30 must remain interactive");
        Assert(!layout.SupportsRichLayout && layout.Compact, "50 columns must use compact layout");
        Assert(layout.CanvasWidth == 49 && layout.RenderLeft == 0, "compact canvas geometry changed");
        Assert(layout.PaneWidth == 47 && layout.LeftPaneX == 1 && layout.RightPaneX == 1,
            "compact pane geometry changed");

        var tooShort = TerminalLayout.Calculate(80, 29);
        Assert(!tooShort.SupportsInteractiveSession && !tooShort.SupportsRichLayout,
            "terminal height below 30 must stay outside the interactive renderer");
    }

    private static void LongTextWrapsInMemory()
    {
        var wrapped = WrapText("alpha abcdefghij omega", 5);
        Assert(wrapped.SequenceEqual(["alpha", "abcde", "fghij", "omega"]),
            "long word wrapping changed");

        var canvas = Create(12, 5);
        DrawBox(canvas, 0, 0, 12, 5, "T");
        PutWrapped(canvas, 1, 1, 5, "abcdefghij", Palette.Bright);
        Assert(Row(canvas, 1).Substring(1, 5) == "abcde" && Row(canvas, 2).Substring(1, 5) == "fghij",
            "wrapped text was not written into the in-memory frame");
    }

    private static void RepeatedFrameProducesNoCellWrites()
    {
        var canvas = Create(8, 3);
        Put(canvas, 1, 1, "hello", Palette.Bright);
        var output = new TerminalOutput(ansi: true);

        var first = output.BuildAnsiFrame(canvas, renderLeft: 0, terminalWidth: 80);
        var second = output.BuildAnsiFrame(canvas, renderLeft: 0, terminalWidth: 80);

        Assert(Count(first, "\u001b[2K") == 3, "first ANSI frame must clear every row");
        Assert(second == "\u001b[0m", "identical ANSI frame must not repaint cells");
    }

    private static void ResizeAndInvalidationForceFullRender()
    {
        var canvas = Create(8, 3);
        Put(canvas, 0, 0, "x", Palette.Text);
        var output = new TerminalOutput(ansi: true);

        _ = output.BuildAnsiFrame(canvas, renderLeft: 0, terminalWidth: 80);
        var resized = output.BuildAnsiFrame(canvas, renderLeft: 0, terminalWidth: 81);
        Assert(Count(resized, "\u001b[2K") == 3, "terminal resize must invalidate the framebuffer");

        var stable = output.BuildAnsiFrame(canvas, renderLeft: 0, terminalWidth: 81);
        Assert(stable == "\u001b[0m", "stable frame after resize must return to diff rendering");

        output.Invalidate();
        var invalidated = output.BuildAnsiFrame(canvas, renderLeft: 0, terminalWidth: 81);
        Assert(Count(invalidated, "\u001b[2K") == 3, "explicit invalidation must force a full render");
    }


    private static void AdapterScreenHasAutomaticAndSystemWithoutAdapters()
    {
        var screen = new AdapterSelectionScreen([], currentId: null);
        Assert(screen.Items.Count == 2, "empty adapter list must still expose automatic and system choices");
        Assert(screen.Items[0].Shortcut == "0" && screen.Items[0].Id is null,
            "automatic adapter option changed");
        Assert(screen.Items[1].Shortcut == "S" && screen.Items[1].Id == PhysicalAdapterSelection.SystemRoute,
            "system-route adapter option changed");
        Assert(screen.Items[0].IsCurrent, "automatic mode must be marked current when no adapter override is stored");
    }

    private static void AdapterScreenHandlesLongNamesAndResize()
    {
        var adapter = Adapter("wifi-long", new string('A', 120), "Campus Wi-Fi");
        var screen = new AdapterSelectionScreen([adapter], adapter.Id);

        var compact = TerminalLayout.Calculate(50, 30);
        var compactFrame = screen.BuildFrame(compact);
        Assert(compactFrame.GetLength(1) == compact.CanvasWidth, "adapter frame ignored compact width");
        Assert(Flatten(compactFrame).Contains('…'), "long adapter name must be clipped in a narrow frame");
        Assert(Flatten(compactFrame).Contains("текущий", StringComparison.Ordinal),
            "current adapter marker must survive clipping in a compact frame");

        var selectedBeforeResize = screen.SelectedIndex;
        var wide = TerminalLayout.Calculate(120, 30);
        var wideFrame = screen.BuildFrame(wide);
        Assert(wideFrame.GetLength(1) == wide.CanvasWidth, "adapter frame ignored wide terminal width");
        Assert(screen.SelectedIndex == selectedBeforeResize, "terminal resize must not change adapter selection");
        Assert(Flatten(wideFrame).Contains("текущий", StringComparison.Ordinal),
            "current adapter marker disappeared after resize");
    }

    private static void AdapterScreenNavigatesLargeLists()
    {
        var adapters = Enumerable.Range(1, 25)
            .Select(index => Adapter($"wifi-{index}", $"Adapter {index}", index == 1 ? "Campus Wi-Fi" : null))
            .ToArray();
        var screen = new AdapterSelectionScreen(adapters, currentId: null);
        var visibleRows = screen.GetVisibleRowCount(TerminalLayout.Calculate(80, 30));
        Assert(visibleRows == 21, "adapter list page size changed");
        Assert(screen.Items.Count == 27, "large adapter list lost automatic/system entries");

        _ = screen.HandleKey(Key(ConsoleKey.End), visibleRows);
        Assert(screen.SelectedIndex == 26, "End must select the last adapter option");
        var endWindow = ListWindow.AroundSelection(screen.Items.Count, screen.SelectedIndex, visibleRows);
        Assert(endWindow.Offset == 6 && endWindow.Count == 21, "selected adapter must stay in the visible list window");

        _ = screen.HandleKey(Key(ConsoleKey.PageUp), visibleRows);
        Assert(screen.SelectedIndex == 5, "PageUp must move by one visible page");
        _ = screen.HandleKey(Key(ConsoleKey.PageDown), visibleRows);
        Assert(screen.SelectedIndex == 26, "PageDown must move by one visible page");
        _ = screen.HandleKey(Key(ConsoleKey.Home), visibleRows);
        Assert(screen.SelectedIndex == 0, "Home must select the first option");
        _ = screen.HandleKey(Key(ConsoleKey.UpArrow), visibleRows);
        Assert(screen.SelectedIndex == 26, "Up on the first option must wrap to the end");
        _ = screen.HandleKey(Key(ConsoleKey.DownArrow), visibleRows);
        Assert(screen.SelectedIndex == 0, "Down on the last option must wrap to the start");
    }

    private static void AdapterScreenFastKeysAndEscape()
    {
        var adapters = new[]
        {
            Adapter("wifi-1", "First", "Campus Wi-Fi"),
            Adapter("wifi-2", "Second", null)
        };
        var visibleRows = 21;

        var system = new AdapterSelectionScreen(adapters, adapters[1].Id)
            .HandleKey(Key(ConsoleKey.S, 's'), visibleRows);
        Assert(system.Outcome == AdapterSelectionOutcome.Confirmed &&
               system.SelectedId == PhysicalAdapterSelection.SystemRoute,
            "S fast key must confirm the system route");

        var automatic = new AdapterSelectionScreen(adapters, adapters[1].Id)
            .HandleKey(Key(ConsoleKey.D0, '0'), visibleRows);
        Assert(automatic.Outcome == AdapterSelectionOutcome.Confirmed && automatic.SelectedId is null,
            "0 fast key must confirm automatic adapter selection");

        var first = new AdapterSelectionScreen(adapters, adapters[1].Id)
            .HandleKey(Key(ConsoleKey.D1, '1'), visibleRows);
        Assert(first.Outcome == AdapterSelectionOutcome.Confirmed && first.SelectedId == adapters[0].Id,
            "numeric fast key must select the matching adapter");

        var enterScreen = new AdapterSelectionScreen(adapters, currentId: null);
        _ = enterScreen.HandleKey(Key(ConsoleKey.DownArrow), visibleRows);
        _ = enterScreen.HandleKey(Key(ConsoleKey.DownArrow), visibleRows);
        var enter = enterScreen.HandleKey(Key(ConsoleKey.Enter, '\r'), visibleRows);
        Assert(enter.Outcome == AdapterSelectionOutcome.Confirmed && enter.SelectedId == adapters[0].Id,
            "Enter must confirm the highlighted adapter");

        var escapeScreen = new AdapterSelectionScreen(adapters, adapters[1].Id);
        _ = escapeScreen.HandleKey(Key(ConsoleKey.Home), visibleRows);
        var escape = escapeScreen.HandleKey(Key(ConsoleKey.Escape, '\u001b'), visibleRows);
        Assert(escape.Outcome == AdapterSelectionOutcome.Cancelled && escape.SelectedId == adapters[1].Id,
            "Esc must cancel without replacing the stored adapter selection");
    }

    private static void SharedListWindowKeepsLargeListsVisible()
    {
        var adapterWindow = ListWindow.AroundSelection(itemCount: 27, selectedIndex: 26, visibleRows: 21);
        var leaderboardWindow = ListWindow.FromOffset(itemCount: 260, requestedOffset: 999, visibleRows: 21);
        Assert(adapterWindow.Offset == 6 && adapterWindow.LastDisplayIndex == 27,
            "selection window geometry changed");
        Assert(leaderboardWindow.Offset == 239 && leaderboardWindow.FirstDisplayIndex == 240 &&
               leaderboardWindow.LastDisplayIndex == 260,
            "scroll window geometry must clamp a large leaderboard to its final page");
    }


    private static void PathPaneFormatsIndependentCountdowns()
    {
        var now = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        var timed = new InteractiveNetworkPathStatus(
            "Wi-Fi · Campus Wi-Fi",
            PathAuthorizationStatus.Internet,
            Preferred: false,
            ExpectedExpiryUtc: now.AddHours(23).AddMinutes(36),
            LastResult: "success");
        var untimed = new InteractiveNetworkPathStatus(
            "Ethernet 4",
            PathAuthorizationStatus.Internet,
            Preferred: true,
            ExpectedExpiryUtc: null,
            LastResult: null);
        var captive = timed with
        {
            Status = PathAuthorizationStatus.Captive,
            ExpectedExpiryUtc = null,
            LastResult = null
        };
        var authorizing = captive with { LastResult = "step-one-sent" };

        Assert(InteractiveTerminalUi.FormatPathPaneStatus(timed, now).Text == "интернет 23ч 36м",
            "path pane must show the timer belonging to that exact path");
        Assert(InteractiveTerminalUi.FormatPathPaneStatus(untimed, now).Text == "интернет",
            "path without a known authorization window must not invent a countdown");
        Assert(InteractiveTerminalUi.FormatPathPaneStatus(captive, now).Text == "captive",
            "captive path label changed");
        Assert(InteractiveTerminalUi.FormatPathPaneStatus(authorizing, now).Text == "авторизация...",
            "active path authorization must be visible in the main pane");
        Assert(InteractiveTerminalUi.FormatPathPaneStatus(timed, now, compact: true).Text == "online 23:36",
            "narrow path status must keep the countdown readable");
    }
    private static PhysicalAdapter Adapter(string id, string name, string? ssid) =>
        new(
            id,
            name,
            NetworkInterfaceType.Wireless80211,
            IsUp: true,
            IPv4Index: 12,
            SourceIPv4: IPAddress.Parse("10.0.0.10"),
            DnsServers: [IPAddress.Parse("10.0.0.1")],
            HasGateway: true,
            Ssid: ssid);

    private static ConsoleKeyInfo Key(ConsoleKey key, char keyChar = '\0') =>
        new(keyChar, key, shift: false, alt: false, control: false);

    private static string Flatten(Cell[,] canvas)
    {
        var chars = new char[canvas.GetLength(0) * canvas.GetLength(1)];
        var index = 0;
        for (var y = 0; y < canvas.GetLength(0); y++)
        {
            for (var x = 0; x < canvas.GetLength(1); x++)
            {
                chars[index++] = canvas[y, x].Character;
            }
        }

        return new string(chars);
    }

    private static string Row(Cell[,] canvas, int row)
    {
        var chars = new char[canvas.GetLength(1)];
        for (var x = 0; x < chars.Length; x++)
        {
            chars[x] = canvas[row, x].Character;
        }

        return new string(chars);
    }

    private static int Count(string value, string needle) =>
        (value.Length - value.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
