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
        ConnectionScreenEditsDraftAndRetainsMissingDevices();
        ConnectionScreenNavigationResizeAndSsidInput();
        InitialAutomaticAuthorizationQuestionFits();
        SharedListWindowKeepsLargeListsVisible();
        PathPaneFormatsIndependentCountdowns();
        foreach (var width in new[] { 80, 100, 120 })
        {
            var layout = TerminalLayout.Calculate(width, 30);
            var columns = InteractiveTerminalUi.CalculateNetworkPathColumns(layout.RightPaneX, layout.PaneWidth);
            Assert(columns.TimerStart - columns.Gap - columns.StateStart >= "сбой проверки".Length &&
                   columns.RightExclusive - columns.TimerStart >= "по событию".Length &&
                   columns.ContentX + columns.LabelWidth <= columns.StateStart - columns.Gap,
                "new path labels overlap or truncate in a supported rich terminal");
        }
        TraySemanticStatesFollowPathHealth();
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


    private static void ConnectionScreenEditsDraftAndRetainsMissingDevices()
    {
        var current = new AuthorizationConnectionSelection { WifiSsids = ["Campus Wi-Fi", "Saved phone"],
            Adapters = [new("missing", "My USB phone")] };
        var screen = new ConnectionSelectionScreen(
            [new("wifi", "Wi-Fi", "Connected phone", true, false, true),
             new("usb", "Ethernet", null, true, false),
             new("vpn", "Radmin VPN", null, true, true)], current);
        Assert(screen.Items.Count == 5 && screen.Items.All(item => item.Adapter?.Id != "vpn"),
            "list lost saved/current Wi-Fi, missing USB or exposed a virtual adapter");
        Assert(screen.Selection.Adapters.Single().Id == "missing", "missing selected adapter disappeared");
        _ = screen.HandleKey(Key(ConsoleKey.Spacebar), 19);
        Assert(!screen.Selection.WifiSsids.Contains("Campus Wi-Fi") && current.WifiSsids.Contains("Campus Wi-Fi"),
            "Space must edit only the draft, including Campus checkbox");
        Assert(screen.HandleKey(Key(ConsoleKey.Escape), 19) == ConnectionSelectionOutcome.Cancelled,
            "Esc must discard the draft");
        Assert(screen.HandleKey(Key(ConsoleKey.Enter), 19) == ConnectionSelectionOutcome.Confirmed,
            "Enter must confirm the complete list");
        Assert(screen.AddWifi("Phone") && screen.AddWifi("Phone") && screen.Items.Count(item => item.Ssid == "Phone") == 1,
            "adding the same SSID duplicated it");
        Assert(!screen.AddWifi(new string('я', 17)) && !screen.AddWifi("a\nb"), "invalid SSID accepted");
    }

    private static void ConnectionScreenNavigationResizeAndSsidInput()
    {
        var adapters = Enumerable.Range(1, 25).Select(i =>
            new AgentNetworkAdapterOption($"usb-{i}", new string('A', 80) + i, null, true, false)).ToArray();
        var screen = new ConnectionSelectionScreen(adapters, new());
        var layout = TerminalLayout.Calculate(80, 30);
        var rows = screen.GetVisibleRowCount(layout);
        _ = screen.HandleKey(Key(ConsoleKey.End), rows);
        Assert(screen.SelectedIndex == 25, "End did not reach the last item");
        _ = screen.HandleKey(Key(ConsoleKey.PageUp), rows);
        Assert(screen.SelectedIndex == 25 - rows, "page navigation did not use visible rows");
        _ = screen.HandleKey(Key(ConsoleKey.Home), rows);
        _ = screen.HandleKey(Key(ConsoleKey.UpArrow), rows);
        Assert(screen.SelectedIndex == 25, "Up did not wrap");
        _ = screen.HandleKey(Key(ConsoleKey.DownArrow), rows);
        Assert(screen.SelectedIndex == 0, "Down did not wrap");
        _ = screen.HandleKey(Key(ConsoleKey.A, 'a'), rows);
        foreach (var ch in "Phone") _ = screen.HandleKey(Key(ConsoleKey.NoName, ch), rows);
        Assert(screen.HandleKey(Key(ConsoleKey.Enter), rows) == ConnectionSelectionOutcome.Continue && !screen.AddingSsid &&
            screen.Selection.WifiSsids.Contains("Phone"), "SSID editor Enter saved the screen or lost its input");
        _ = screen.HandleKey(Key(ConsoleKey.A, 'a'), rows);
        _ = screen.HandleKey(Key(ConsoleKey.Escape), rows);
        Assert(!screen.AddingSsid, "Esc did not leave the SSID editor");
        var index = screen.SelectedIndex;
        foreach (var width in new[] { 50, 80, 120 })
        {
            var resized = TerminalLayout.Calculate(width, 30);
            var frame = screen.BuildFrame(resized);
            Assert(frame.GetLength(1) == resized.CanvasWidth && screen.SelectedIndex == index,
                "resize changed draft selection or ignored terminal width");
            Assert(Flatten(frame).Contains("[x]", StringComparison.Ordinal), "checkbox marker disappeared after resize");
        }
        _ = screen.HandleKey(Key(ConsoleKey.Home), rows);
        _ = screen.HandleKey(Key(ConsoleKey.DownArrow), rows);
        Assert(Flatten(screen.BuildFrame(TerminalLayout.Calculate(50, 30))).Contains('…'), "long labels did not clip");
    }

    private static void InitialAutomaticAuthorizationQuestionFits()
    {
        foreach (var width in new[] { 50, 80, 120 })
        {
            var frame = InteractiveTerminalUi.BuildFullWidthYesNoFrame(TerminalLayout.Calculate(width, 30),
                "4. АВТОМАТИЧЕСКАЯ АВТОРИЗАЦИЯ",
                "IS74Wifi может автоматически восстанавливать доступ к Интернету в Campus Wi-Fi, когда заканчивается срок авторизации.\n\nВключить автоматическую авторизацию?",
                "Да, включить", "Нет, буду запускать вручную", 1);
            var text = Flatten(frame);
            Assert(text.Contains("Campus Wi-Fi", StringComparison.Ordinal) && text.Contains("Включить автоматическую", StringComparison.Ordinal) &&
                text.Contains("[Y] Да, включить", StringComparison.Ordinal) && text.Contains("› [N] Нет, буду запускать вручную", StringComparison.Ordinal),
                "initial setup question or choices overlapped/disappeared after wrapping");
        }
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
            LastResult: "success",
            AuthorizationWindowApproximate: true,
            NextCheckUtc: now.AddHours(23).AddMinutes(36));
        var opaqueTimed = new InteractiveNetworkPathStatus(
            "Ethernet 4",
            PathAuthorizationStatus.Internet,
            Preferred: true,
            ExpectedExpiryUtc: now.AddHours(18).AddMinutes(12),
            LastResult: "success",
            AuthorizationWindowApproximate: true,
            NextCheckUtc: now.AddHours(18).AddMinutes(12));
        var untimed = opaqueTimed with
        {
            ExpectedExpiryUtc = null,
            LastResult = null,
            NextCheckUtc = null,
            CheckOnConnectivityEvent = true
        };
        var unreachable = timed with { Status = PathAuthorizationStatus.Unreachable };
        var ambiguous = timed with { Status = PathAuthorizationStatus.Ambiguous };
        var unknown = timed with { Status = PathAuthorizationStatus.Unknown };
        var captive = timed with { Status = PathAuthorizationStatus.Captive };
        var authorizing = captive with { LastResult = "step-one-sent" };
        var expired = timed with
        {
            ExpectedExpiryUtc = now.AddSeconds(-1),
            NextCheckUtc = now.AddMinutes(1)
        };
        var edgeWatch = expired with
        {
            LastResult = "already-authorized",
            NextCheckUtc = now.AddSeconds(34),
            EdgeWatchActive = true
        };
        var overdue = edgeWatch with { NextCheckUtc = now.AddSeconds(-6) };
        var running = overdue with { NetworkOperationRunning = true };
        var dueNow = edgeWatch with { NextCheckUtc = now.AddSeconds(-1) };
        var noScheduledCheck = untimed with { CheckOnConnectivityEvent = false };

        var timedPresentation = InteractiveTerminalUi.FormatPathPaneStatus(timed, now);
        Assert(timedPresentation.StateText == "онлайн" && timedPresentation.TimerText == "23:36",
            "next-check column must show the exact scheduled wake, not an approximate authorization marker");
        var opaquePresentation = InteractiveTerminalUi.FormatPathPaneStatus(opaqueTimed, now);
        Assert(opaquePresentation.StateText == "онлайн" && opaquePresentation.TimerText == "18:12",
            "approximate authorization windows must not make the exact scheduled wake look approximate");
        var untimedPresentation = InteractiveTerminalUi.FormatPathPaneStatus(untimed, now);
        Assert(untimedPresentation.StateText == "онлайн" && untimedPresentation.TimerText == "по событию",
            "path without a known authorization window must show event-driven connectivity waiting");
        var unreachablePresentation = InteractiveTerminalUi.FormatPathPaneStatus(unreachable, now);
        Assert(unreachablePresentation.StateText == "сбой проверки" && unreachablePresentation.TimerText == "23:36",
            "temporary transport failure must not erase the scheduled wake");
        var ambiguousPresentation = InteractiveTerminalUi.FormatPathPaneStatus(ambiguous, now);
        Assert(ambiguousPresentation.StateText == "неясно" && ambiguousPresentation.TimerText == "23:36",
            "ambiguous live state must preserve the scheduled wake");
        var unknownPresentation = InteractiveTerminalUi.FormatPathPaneStatus(unknown, now);
        Assert(unknownPresentation.StateText == "проверка..." && unknownPresentation.TimerText == "23:36",
            "pending probe must preserve the scheduled wake");
        var captivePresentation = InteractiveTerminalUi.FormatPathPaneStatus(captive, now);
        Assert(captivePresentation.StateText == "нужна авторизация" && captivePresentation.TimerText == "—",
            "live captive observation must invalidate the displayed predicted window");
        var authorizingPresentation = InteractiveTerminalUi.FormatPathPaneStatus(authorizing, now);
        Assert(authorizingPresentation.StateText == "авторизация..." && authorizingPresentation.TimerText == "—",
            "active authorization must not show a stale window");
        var expiredPresentation = InteractiveTerminalUi.FormatPathPaneStatus(expired, now);
        Assert(expiredPresentation.StateText == "онлайн" && expiredPresentation.TimerText == "00:01",
            "expired prediction must show the next scheduled edge probe instead of an unknown dash");
        var edgeWatchPresentation = InteractiveTerminalUi.FormatPathPaneStatus(edgeWatch, now);
        Assert(edgeWatchPresentation.StateText == "наблюдение" && edgeWatchPresentation.TimerText == "34с",
            "edge-watch must be explicit and sub-minute scheduler wakes must remain visibly live");
        var dueNowPresentation = InteractiveTerminalUi.FormatPathPaneStatus(dueNow, now);
        Assert(dueNowPresentation.TimerText == "сейчас",
            "a scheduler wake in progress must not collapse to 00:00");
        var overduePresentation = InteractiveTerminalUi.FormatPathPaneStatus(overdue, now);
        Assert(overduePresentation.TimerText == "просрочено" && overduePresentation.TimerColor == Palette.Highlight,
            "an overdue scheduler snapshot must be visible instead of sticking at 00:00");
        var runningPresentation = InteractiveTerminalUi.FormatPathPaneStatus(running, now);
        Assert(runningPresentation.TimerText == "проверка..." && runningPresentation.TimerColor == Palette.Highlight,
            "a started operation taking over five seconds was mistaken for a missed scheduler wake");
        var futureBusy = InteractiveTerminalUi.FormatPathPaneStatus(edgeWatch with { NetworkOperationRunning = true }, now);
        Assert(futureBusy.TimerText == "34с",
            "an operation on another path hid this path's future deadline");
        var completed = InteractiveTerminalUi.FormatPathPaneStatus(running with
        {
            NetworkOperationRunning = false,
            NextCheckUtc = now.AddSeconds(60)
        }, now);
        Assert(completed.TimerText == "00:01", "completed operation did not return to its next deadline");
        var stoppedPresentation = InteractiveTerminalUi.FormatPathPaneStatus(noScheduledCheck, now);
        Assert(stoppedPresentation.StateText == "онлайн" && stoppedPresentation.TimerText == "—",
            "path without a running/scheduled background check must keep the explicit dash");

        var directCampus = WifiPath("campus", "Wi-Fi", "Campus Wi-Fi");
        var hiddenCampus = WifiPath("hidden", "Wi-Fi", "SUSU Hide");
        var hotspot = WifiPath("hotspot", "Wi-Fi", "DESKTOP-S327VD2 4631");
        var usb = new NetworkPathSnapshot(
            "usb",
            "Ethernet 4",
            NetworkInterfaceType.Ethernet,
            IsUp: true,
            InterfaceIndex: 8,
            SourceIPv4: IPAddress.Parse("10.253.184.3"),
            GatewayIPv4: IPAddress.Parse("10.253.184.224"),
            DnsServers: [IPAddress.Parse("192.0.2.53")],
            Ssid: null,
            LooksVirtual: false);
        Assert(!AgentNetworkOperations.IsAuthorizationWindowApproximate(directCampus),
            "Campus Wi-Fi is the only trusted direct SSID family and must use the ordinary timer");
        Assert(AgentNetworkOperations.IsAuthorizationWindowApproximate(hiddenCampus),
            "SUSU Hide may itself be a hotspot and must use an approximate timer");
        Assert(AgentNetworkOperations.IsAuthorizationWindowApproximate(hotspot),
            "Wi-Fi hotspot may change upstream behind NAT and must use an approximate timer");
        Assert(AgentNetworkOperations.IsAuthorizationWindowApproximate(usb),
            "USB/Ethernet tethering may change upstream behind NAT and must use an approximate timer");
    }


    private static void TraySemanticStatesFollowPathHealth()
    {
        static PathAuthorizationState State(
            string id,
            PathAuthorizationStatus status,
            bool userActionRequired = false,
            string? lastResult = null) => new()
        {
            Identity = new NetworkPathIdentity(id, NetworkInterfaceType.Wireless80211, $"ssid:{id}"),
            Status = status,
            UserActionRequired = userActionRequired,
            LastResult = lastResult
        };

        static TrayIconSemanticState Resolve(params PathAuthorizationState[] states) =>
            TrayIconStateResolver.Resolve(new PathAuthorizationStateDocument { Paths = states }).State;

        Assert(Resolve() == TrayIconSemanticState.Question,
            "tray must use question state before an active physical path is known");
        Assert(Resolve(State("ok", PathAuthorizationStatus.Internet)) == TrayIconSemanticState.Normal,
            "healthy Internet path must use the normal tray icon");
        Assert(Resolve(State("unknown", PathAuthorizationStatus.Unknown)) == TrayIconSemanticState.Question,
            "unknown path state must use the question tray icon");
        Assert(Resolve(State("captive", PathAuthorizationStatus.Captive)) == TrayIconSemanticState.Warning,
            "captive path must use the warning tray icon");
        Assert(Resolve(State("down", PathAuthorizationStatus.Unreachable)) == TrayIconSemanticState.Warning,
            "unreachable path must use the warning tray icon");
        Assert(Resolve(State("fatal", PathAuthorizationStatus.Captive, userActionRequired: true)) == TrayIconSemanticState.Error,
            "user-action-required state must use the error tray icon");
        Assert(Resolve(State("fatal-result", PathAuthorizationStatus.Captive, lastResult: "bearer-invalid")) == TrayIconSemanticState.Error,
            "terminal authorization failures must use the error tray icon");
        Assert(Resolve(
                   State("ok", PathAuthorizationStatus.Internet),
                   State("captive", PathAuthorizationStatus.Captive)) == TrayIconSemanticState.Warning,
            "a captive secondary path must outrank a healthy path in the tray summary");
    }

    private static NetworkPathSnapshot WifiPath(string id, string name, string ssid) =>
        new(
            id,
            name,
            NetworkInterfaceType.Wireless80211,
            IsUp: true,
            InterfaceIndex: 1,
            SourceIPv4: IPAddress.Parse("192.0.2.10"),
            GatewayIPv4: IPAddress.Parse("192.0.2.1"),
            DnsServers: [IPAddress.Parse("192.0.2.53")],
            Ssid: ssid,
            LooksVirtual: false);

    private static AgentNetworkAdapterOption Adapter(string id, string name, string? ssid) =>
        new(
            id,
            name,
            ssid,
            CanConnect: true,
            LooksVirtual: false);

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
