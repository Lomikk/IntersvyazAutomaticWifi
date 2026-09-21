using System.Diagnostics;
using System.Text;

namespace IS74Wifi.App;

internal sealed class InteractiveTerminalUi
{
    private const int MinimumTerminalWidth = 80;
    private const int MinimumCanvasWidth = 79;
    private const int PreferredCanvasWidth = 116;
    private const int CanvasHeight = 30;
    private const int PaneY = 14;
    private const int PaneHeight = 14;

    private static readonly string[] Banner =
    [
        "        ..          ",
        "     .::            ",
        "   .-:              `7MMF' .M\"\"\"bgd            `7MMF'     A     `7MF'",
        " .--                  MM  ,MI    \"Y              `MA     ,MA     ,V",
        "-**.                  MM  `MMb.    M******A'  ,AM VM:   ,VVM:   ,V",
        "*%#+-::::::::::-===.  MM    `YMMNq.Y     A'  AVMM  MM.  M' MM.  M'",
        ".+*############%%%%#  MM  .     `MM     A' ,W' MM  `MM A'  `MM A'",
        "                :*%=  MM  Mb     dM    A',W'   MM   :MM;    :MM;",
        "                :+: .JMML.P\"Ybmmd\"    A' AmmmmmMMmm  VF      VF",
        "              .-:                   A'        MM",
        "            .:.                    A'         MM",
        "          .."
    ];

    private const string Subtitle = "InterSvyaz Wi-Fi Auth";

    private readonly string productVersion;
    private readonly bool ansi;
    private InteractiveStatusSnapshot? status;
    private int selected;
    private int canvasWidth = MinimumCanvasWidth;
    private int renderLeft;
    private int leftPaneX = 1;
    private int rightPaneX = 41;
    private int paneWidth = 37;
    private bool compactLayout;
    private DateTimeOffset nextAmbientSweepUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(14);
    private DateTimeOffset? ambientSweepStartedUtc;
    private Cell[,]? lastRenderedCanvas;
    private int lastRenderedLeft = -1;
    private int lastRenderedTerminalWidth = -1;

    public InteractiveTerminalUi(string productVersion)
    {
        this.productVersion = productVersion;
        ansi = ConsoleSession.SupportsVirtualTerminal;
    }

    private bool CanUseInteractiveSession
    {
        get
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                return false;
            }

            try
            {
                return Console.WindowWidth >= 30 && Console.WindowHeight >= CanvasHeight;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool CanUseRichLayout
    {
        get
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                return false;
            }

            try
            {
                return Console.WindowWidth >= MinimumTerminalWidth && Console.WindowHeight >= CanvasHeight;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<InteractiveMenuAction> RunMenuAsync(
        InteractiveStatusSnapshot initialStatus,
        Task<InteractiveStatusSnapshot>? refreshedStatusTask,
        bool showReveal,
        CancellationToken cancellationToken = default)
    {
        status = initialStatus;
        selected = 0;

        if (!CanUseInteractiveSession)
        {
            return RunCompactMenu(initialStatus);
        }

        UpdateLayout();
        PrepareInteractiveConsole();
        try
        {
            if (showReveal && CanUseRichLayout)
            {
                await PlayInitialRevealAsync(cancellationToken).ConfigureAwait(false);
            }

            nextAmbientSweepUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Random.Shared.Next(12, 19));
            ambientSweepStartedUtc = null;

            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Stay in the interactive loop while the user resizes the terminal.
                // Falling back to the blocking compact menu here made a temporary
                // narrow resize permanent until the whole menu was restarted.
                UpdateLayout();

                if (refreshedStatusTask is { IsCompletedSuccessfully: true })
                {
                    status = refreshedStatusTask.Result;
                    refreshedStatusTask = null;
                }

                UpdateAmbientSweepState();
                RenderInteractiveFrame();

                var completed = await Task.WhenAny(
                    keyTask,
                    Task.Delay(16, cancellationToken)).ConfigureAwait(false);
                if (completed != keyTask)
                {
                    continue;
                }

                var key = await keyTask.ConfigureAwait(false);
                if (key.Key == ConsoleKey.R)
                {
                    await PlayInitialRevealAsync(cancellationToken).ConfigureAwait(false);
                    nextAmbientSweepUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Random.Shared.Next(12, 19));
                    ambientSweepStartedUtc = null;
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (key.Key == ConsoleKey.Escape)
                {
                    return InteractiveMenuAction.Exit;
                }

                var items = GetCurrentItems();
                if (items.Count == 0)
                {
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (key.Key == ConsoleKey.UpArrow)
                {
                    selected = (selected - 1 + items.Count) % items.Count;
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (key.Key == ConsoleKey.DownArrow)
                {
                    selected = (selected + 1) % items.Count;
                    keyTask = ReadKeyAsync();
                    continue;
                }

                var hotkeyItem = items.FirstOrDefault(item => item.Hotkey == key.KeyChar);
                if (hotkeyItem.Action != InteractiveMenuAction.None)
                {
                    return hotkeyItem.Action;
                }

                if (key.Key != ConsoleKey.Enter)
                {
                    keyTask = ReadKeyAsync();
                    continue;
                }

                var item = items[selected];
                if (item.Action != InteractiveMenuAction.None)
                {
                    return item.Action;
                }

                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            RestoreConsole();
        }
    }

    public async Task<bool> ConfirmInstallOrUpgradeAsync(
        bool upgrade,
        string? installedVersion,
        string installDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!CanUseRichLayout)
        {
            return ConfirmInstallCompact(upgrade, installedVersion, installDirectory);
        }

        UpdateLayout();
        PrepareInteractiveConsole();
        try
        {
            await PlayInitialRevealAsync(cancellationToken).ConfigureAwait(false);
            selected = 0;
            nextAmbientSweepUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Random.Shared.Next(12, 19));
            ambientSweepStartedUtc = null;

            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanUseRichLayout)
                {
                    RestoreConsole();
                    return ConfirmInstallCompact(upgrade, installedVersion, installDirectory);
                }
                UpdateLayout();
                UpdateAmbientSweepState();
                RenderInstallFrame(upgrade, installedVersion, installDirectory);

                var completed = await Task.WhenAny(
                    keyTask,
                    Task.Delay(16, cancellationToken)).ConfigureAwait(false);
                if (completed != keyTask)
                {
                    continue;
                }

                var key = await keyTask.ConfigureAwait(false);
                if (key.Key == ConsoleKey.R)
                {
                    await PlayInitialRevealAsync(cancellationToken).ConfigureAwait(false);
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.DownArrow)
                {
                    selected = selected == 0 ? 1 : 0;
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (key.Key == ConsoleKey.Escape)
                {
                    return false;
                }

                if (key.KeyChar == '1')
                {
                    return true;
                }

                if (key.KeyChar == '0')
                {
                    return false;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    return selected == 0;
                }

                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            RestoreConsole();
        }
    }

    public void ShowBusyMessage(string title, string message, InteractiveStatusSnapshot currentStatus)
    {
        if (!CanUseInteractiveSession)
        {
            Console.Clear();
            Console.WriteLine($"=== {title} ===");
            Console.WriteLine(message);
            return;
        }

        status = currentStatus;
        UpdateLayout();
        PrepareInteractiveConsole(clear: false);
        var canvas = CreateActionCanvas(title, out var contentX, out var contentY, out var contentWidth);
        PutWrapped(canvas, contentX, contentY + 1, contentWidth, message, Palette.Bright);
        Center(canvas, CanvasHeight - 1, "Пожалуйста, подождите...", Palette.Dim);
        Render(canvas);
        RestoreConsole(showCursor: false);
    }

    public void ShowActionProgress(
        string title,
        InteractiveActionHistory history,
        InteractiveStatusSnapshot currentStatus)
    {
        if (!CanUseInteractiveSession)
        {
            Console.Clear();
            Console.WriteLine($"=== {title} ===");
            foreach (var line in history.Lines)
            {
                Console.WriteLine($"{ActionLineSymbol(line.Kind)} {line.Text}");
            }
            return;
        }

        status = currentStatus;
        UpdateLayout();
        UpdateAmbientSweepState();
        PrepareInteractiveConsole(clear: false);
        RenderActionHistoryFrame(title, history.Lines, offset: null, waitingForDismiss: false);
        RestoreConsole(showCursor: false);
    }

    public async Task<string?> PromptDigitsAsync(
        string title,
        string prompt,
        string prefix,
        int minimumDigits,
        int maximumDigits,
        InteractiveStatusSnapshot currentStatus,
        InteractiveActionHistory? history = null,
        CancellationToken cancellationToken = default)
    {
        if (minimumDigits < 0 || maximumDigits < minimumDigits)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDigits));
        }

        status = currentStatus;
        var digits = new StringBuilder();
        PrepareInteractiveConsole(clear: false);
        try
        {
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateLayout();
                UpdateAmbientSweepState();
                RenderPromptFrame(title, prompt, prefix, digits.ToString(), minimumDigits, maximumDigits, history?.Lines);

                var completed = await Task.WhenAny(keyTask, Task.Delay(16, cancellationToken)).ConfigureAwait(false);
                if (completed != keyTask)
                {
                    continue;
                }

                var key = await keyTask.ConfigureAwait(false);
                if (key.Key == ConsoleKey.Escape)
                {
                    return null;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (digits.Length > 0)
                    {
                        digits.Length--;
                    }
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (digits.Length >= minimumDigits)
                    {
                        return digits.ToString();
                    }
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (char.IsAsciiDigit(key.KeyChar) && digits.Length < maximumDigits)
                {
                    digits.Append(key.KeyChar);
                }
                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            RestoreConsole();
        }
    }

    public async Task ShowMessageAsync(
        string title,
        string message,
        InteractiveStatusSnapshot currentStatus,
        bool isError = false,
        CancellationToken cancellationToken = default)
    {
        status = currentStatus;
        PrepareInteractiveConsole(clear: false);
        try
        {
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateLayout();
                UpdateAmbientSweepState();
                RenderMessageFrame(title, message, isError);

                var completed = await Task.WhenAny(keyTask, Task.Delay(16, cancellationToken)).ConfigureAwait(false);
                if (completed != keyTask)
                {
                    continue;
                }

                var key = await keyTask.ConfigureAwait(false);
                if (key.Key is ConsoleKey.Enter or ConsoleKey.Escape)
                {
                    return;
                }
                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            RestoreConsole();
        }
    }

    public async Task ShowActionHistoryAsync(
        string title,
        InteractiveActionHistory history,
        InteractiveStatusSnapshot currentStatus,
        string? dismissHint = null,
        CancellationToken cancellationToken = default)
    {
        status = currentStatus;
        var offset = 0;
        var pinnedToEnd = true;
        PrepareInteractiveConsole(clear: false);
        try
        {
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateLayout();
                UpdateAmbientSweepState();
                var visibleRows = GetActionHistoryVisibleRows();
                var physicalRowCount = BuildActionHistoryRows(history.Lines, GetActionContentWidth()).Count;
                var maxOffset = Math.Max(0, physicalRowCount - visibleRows);
                offset = pinnedToEnd ? maxOffset : Math.Clamp(offset, 0, maxOffset);
                RenderActionHistoryFrame(title, history.Lines, offset, waitingForDismiss: true, dismissHint);

                var completed = await Task.WhenAny(keyTask, Task.Delay(16, cancellationToken)).ConfigureAwait(false);
                if (completed != keyTask)
                {
                    continue;
                }

                var key = await keyTask.ConfigureAwait(false);
                if (key.Key is ConsoleKey.Enter or ConsoleKey.Escape)
                {
                    return;
                }
                if (key.Key == ConsoleKey.UpArrow)
                {
                    offset = Math.Max(0, offset - 1);
                    pinnedToEnd = false;
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    offset = Math.Min(maxOffset, offset + 1);
                    pinnedToEnd = offset >= maxOffset;
                }
                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            RestoreConsole();
        }
    }

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel,
        InteractiveStatusSnapshot currentStatus,
        InteractiveActionHistory? history = null,
        CancellationToken cancellationToken = default)
    {
        status = currentStatus;
        selected = 0;
        PrepareInteractiveConsole(clear: false);
        try
        {
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateLayout();
                UpdateAmbientSweepState();
                RenderConfirmFrame(title, message, confirmLabel, history?.Lines);

                var completed = await Task.WhenAny(keyTask, Task.Delay(16, cancellationToken)).ConfigureAwait(false);
                if (completed != keyTask)
                {
                    continue;
                }

                var key = await keyTask.ConfigureAwait(false);
                if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
                {
                    selected = selected == 0 ? 1 : 0;
                    keyTask = ReadKeyAsync();
                    continue;
                }
                if (key.Key == ConsoleKey.Escape || key.KeyChar == '0')
                {
                    return false;
                }
                if (key.KeyChar == '1')
                {
                    return true;
                }
                if (key.Key == ConsoleKey.Enter)
                {
                    return selected == 0;
                }
                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            RestoreConsole();
        }
    }

    public async Task ShowDetailsAsync(
        string title,
        IReadOnlyList<string> lines,
        InteractiveStatusSnapshot currentStatus,
        CancellationToken cancellationToken = default)
    {
        status = currentStatus;
        var offset = 0;
        PrepareInteractiveConsole(clear: false);
        try
        {
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateLayout();
                UpdateAmbientSweepState();
                var visibleRows = compactLayout ? 15 : PaneHeight - 4;
                var maxOffset = Math.Max(0, lines.Count - visibleRows);
                offset = Math.Clamp(offset, 0, maxOffset);
                RenderDetailsFrame(title, lines, offset, visibleRows);

                var completed = await Task.WhenAny(keyTask, Task.Delay(16, cancellationToken)).ConfigureAwait(false);
                if (completed != keyTask)
                {
                    continue;
                }

                var key = await keyTask.ConfigureAwait(false);
                if (key.Key is ConsoleKey.Enter or ConsoleKey.Escape)
                {
                    return;
                }
                if (key.Key == ConsoleKey.UpArrow)
                {
                    offset = Math.Max(0, offset - 1);
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    offset = Math.Min(maxOffset, offset + 1);
                }
                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            RestoreConsole();
        }
    }

    private InteractiveMenuAction RunCompactMenu(InteractiveStatusSnapshot snapshot)
    {
        Console.Clear();
        Console.WriteLine("IS74W — InterSvyaz Wi-Fi Auth");
        Console.WriteLine();
        Console.WriteLine($"Интернет     : {FormatInternet(snapshot.InternetAvailable)}");
        Console.WriteLine($"Регистрация  : {(snapshot.Registered ? "есть" : "нет")}");
        Console.WriteLine($"Автовход     : {(snapshot.AutomaticAuthorizationEnabled ? "включён" : "выключен")}");
        Console.WriteLine($"Агент        : {(snapshot.AgentRunning ? "работает" : "остановлен")}");
        Console.WriteLine();

        return RunCompactSelection(GetPrimaryItems(snapshot));
    }

    private InteractiveMenuAction RunCompactSelection(IReadOnlyList<MenuItem> items)
    {
        var index = 0;
        while (true)
        {
            var row = Console.CursorTop;
            for (var i = 0; i < items.Count; i++)
            {
                Console.WriteLine($"{(i == index ? '›' : ' ')} [{items[i].Hotkey}] {items[i].Label}");
            }

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.UpArrow)
            {
                index = (index - 1 + items.Count) % items.Count;
            }
            else if (key.Key == ConsoleKey.DownArrow)
            {
                index = (index + 1) % items.Count;
            }
            else if (key.Key == ConsoleKey.Enter)
            {
                return items[index].Action;
            }
            else
            {
                var hotkeyItem = items.FirstOrDefault(item => item.Hotkey == key.KeyChar);
                if (hotkeyItem.Action != InteractiveMenuAction.None)
                {
                    return hotkeyItem.Action;
                }
            }
            if (key.Key == ConsoleKey.Escape)
            {
                return InteractiveMenuAction.Exit;
            }

            try
            {
                Console.SetCursorPosition(0, row);
            }
            catch
            {
                Console.Clear();
            }
        }
    }

    private bool ConfirmInstallCompact(bool upgrade, string? installedVersion, string installDirectory)
    {
        Console.Clear();
        Console.WriteLine("IS74W — InterSvyaz Wi-Fi Auth");
        Console.WriteLine();
        if (upgrade)
        {
            Console.WriteLine($"Установлена версия: {installedVersion ?? "неизвестно"}");
            Console.WriteLine($"Запущена версия    : {productVersion}");
            Console.WriteLine("Установленную копию нужно обновить, прежде чем продолжить.");
        }
        else
        {
            Console.WriteLine("Для обычной работы IS74W должна быть установлена для текущего пользователя Windows.");
            Console.WriteLine($"Путь: {installDirectory}");
            Console.WriteLine("Права администратора не требуются.");
        }
        Console.WriteLine();
        Console.WriteLine("[1] / Enter — установить / обновить");
        Console.WriteLine("[0] / Esc   — выйти");

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter || key.KeyChar == '1') return true;
            if (key.Key == ConsoleKey.Escape || key.KeyChar == '0') return false;
        }
    }

    private async Task PlayInitialRevealAsync(CancellationToken cancellationToken)
    {
        if (!CanUseRichLayout)
        {
            return;
        }

        const int startTop = 7;
        var stopwatch = Stopwatch.StartNew();
        const double sweepDurationMs = 470;

        while (stopwatch.Elapsed.TotalMilliseconds < sweepDurationMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryConsumeSkipKey())
            {
                return;
            }

            var t = stopwatch.Elapsed.TotalMilliseconds / sweepDurationMs;
            var head = -7 + t * (BannerWidth + 14);
            var canvas = CreateCanvas();
            DrawBanner(canvas, startTop, BannerMode.InitialSweep, head);
            Render(canvas);
            await Task.Delay(12, cancellationToken).ConfigureAwait(false);
        }

        for (var top = startTop; top > 0; top -= 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryConsumeSkipKey())
            {
                return;
            }

            var canvas = CreateCanvas();
            DrawBanner(canvas, Math.Max(0, top), BannerMode.Final, 0);
            Center(canvas, Math.Min(CanvasHeight - 1, Math.Max(0, top) + 13), Subtitle, Palette.Dim);
            Render(canvas);
            await Task.Delay(26, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<ConsoleKeyInfo> ReadKeyAsync()
    {
        // Console.KeyAvailable is unreliable in some Windows console hosts while
        // the UI is continuously redrawing. Keep exactly one blocking ReadKey
        // on a worker thread instead; the render loop can continue independently
        // without polling the console input buffer.
        return Task.Run(() => Console.ReadKey(intercept: true));
    }

    private bool TryConsumeSkipKey()
    {
        if (!Console.KeyAvailable)
        {
            return false;
        }

        _ = Console.ReadKey(intercept: true);
        return true;
    }

    private void UpdateAmbientSweepState()
    {
        var now = DateTimeOffset.UtcNow;
        if (ambientSweepStartedUtc is { } started)
        {
            if (now - started >= TimeSpan.FromMilliseconds(620))
            {
                ambientSweepStartedUtc = null;
                nextAmbientSweepUtc = now + TimeSpan.FromSeconds(Random.Shared.Next(18, 31));
            }
            return;
        }

        if (now >= nextAmbientSweepUtc)
        {
            ambientSweepStartedUtc = now;
        }
    }

    private void RenderPromptFrame(
        string title,
        string prompt,
        string prefix,
        string digits,
        int minimumDigits,
        int maximumDigits,
        IReadOnlyList<InteractiveActionLine>? history)
    {
        var canvas = CreateActionCanvas(title, out var contentX, out var contentY, out var contentWidth);
        var historyRows = DrawActionHistoryTail(canvas, contentX, contentY, contentWidth, history, maxRows: 3);
        var promptY = contentY + historyRows + (historyRows > 0 ? 1 : 0);
        Put(canvas, contentX, promptY, Truncate(prompt, contentWidth), Palette.Text);
        Put(canvas, contentX, promptY + 2, Truncate(prefix + digits + "_", contentWidth), Palette.Bright);
        var requirement = minimumDigits == maximumDigits
            ? $"Нужно цифр: {minimumDigits}"
            : $"Цифр: {minimumDigits}–{maximumDigits}";
        Put(canvas, contentX, promptY + 4, requirement, Palette.Dim);
        Center(canvas, CanvasHeight - 1, "Enter продолжить   Backspace удалить   Esc отмена", Palette.Dim);
        Render(canvas);
    }

    private void RenderMessageFrame(string title, string message, bool isError)
    {
        var canvas = CreateActionCanvas(title, out var contentX, out var contentY, out var contentWidth);
        PutWrapped(canvas, contentX, contentY, contentWidth, message, isError ? Palette.Bright : Palette.Text);
        Center(canvas, CanvasHeight - 1, "Enter / Esc — вернуться", Palette.Dim);
        Render(canvas);
    }

    private void RenderConfirmFrame(
        string title,
        string message,
        string confirmLabel,
        IReadOnlyList<InteractiveActionLine>? history)
    {
        var canvas = CreateActionCanvas(title, out var contentX, out var contentY, out var contentWidth);
        var historyRows = DrawActionHistoryTail(canvas, contentX, contentY, contentWidth, history, maxRows: 3);
        if (historyRows == 0)
        {
            PutWrapped(canvas, contentX, contentY, contentWidth, message, Palette.Text);
            DrawSelectable(canvas, contentX, contentY + 5, '1', confirmLabel, selected == 0);
            DrawSelectable(canvas, contentX, contentY + 6, '0', "Отмена", selected == 1);
        }
        else
        {
            var messageY = contentY + historyRows + 1;
            Put(canvas, contentX, messageY, Truncate(message, contentWidth), Palette.Text);
            DrawSelectable(canvas, contentX, messageY + 3, '1', confirmLabel, selected == 0);
            DrawSelectable(canvas, contentX, messageY + 4, '0', "Отмена", selected == 1);
        }
        Center(canvas, CanvasHeight - 1, "↑ ↓ выбрать   Enter продолжить   1/0 сразу   Esc отмена", Palette.Dim);
        Render(canvas);
    }

    private void RenderActionHistoryFrame(
        string title,
        IReadOnlyList<InteractiveActionLine> lines,
        int? offset,
        bool waitingForDismiss,
        string? dismissHint = null)
    {
        var canvas = CreateActionCanvas(title, out var contentX, out var contentY, out var contentWidth);
        var rows = BuildActionHistoryRows(lines, contentWidth);
        var visibleRows = GetActionHistoryVisibleRows();
        var maxOffset = Math.Max(0, rows.Count - visibleRows);
        var start = Math.Clamp(offset ?? maxOffset, 0, maxOffset);
        var count = Math.Min(visibleRows, Math.Max(0, rows.Count - start));

        for (var index = 0; index < count; index++)
        {
            DrawActionHistoryRow(canvas, contentX, contentY + index, rows[start + index]);
        }

        if (rows.Count > visibleRows)
        {
            PutRightAligned(
                canvas,
                contentX,
                contentX + contentWidth,
                contentY + visibleRows,
                $"{start + 1}–{start + count} / {rows.Count}",
                Palette.Dim);
        }

        Center(
            canvas,
            CanvasHeight - 1,
            waitingForDismiss
                ? dismissHint ?? "↑ ↓ история   Enter / Esc — вернуться"
                : "Выполняется...",
            Palette.Dim);
        Render(canvas);
    }

    private int DrawActionHistoryTail(
        Cell[,] canvas,
        int x,
        int y,
        int width,
        IReadOnlyList<InteractiveActionLine>? lines,
        int maxRows)
    {
        if (lines is null || lines.Count == 0 || maxRows <= 0)
        {
            return 0;
        }

        var rows = BuildActionHistoryRows(lines, width);
        var count = Math.Min(maxRows, rows.Count);
        var start = rows.Count - count;
        for (var index = 0; index < count; index++)
        {
            DrawActionHistoryRow(canvas, x, y + index, rows[start + index]);
        }
        return count;
    }

    private static char ActionLineSymbol(InteractiveActionLineKind kind) => kind switch
    {
        InteractiveActionLineKind.Active => '›',
        InteractiveActionLineKind.Success => '✓',
        InteractiveActionLineKind.Warning => '!',
        InteractiveActionLineKind.Error => '×',
        _ => '·'
    };

    private static Palette ActionLinePalette(InteractiveActionLineKind kind) => kind switch
    {
        InteractiveActionLineKind.Active => Palette.Highlight,
        InteractiveActionLineKind.Success => Palette.Good,
        InteractiveActionLineKind.Warning => Palette.Warning,
        InteractiveActionLineKind.Error => Palette.Error,
        _ => Palette.Text
    };

    private static int GetActionHistoryVisibleRows() => PaneHeight - 4;

    private int GetActionContentWidth() => compactLayout
        ? Math.Max(10, canvasWidth - 8)
        : Math.Max(1, paneWidth - 6);

    private static List<ActionHistoryRow> BuildActionHistoryRows(
        IReadOnlyList<InteractiveActionLine> lines,
        int contentWidth)
    {
        var textWidth = Math.Max(1, contentWidth - 2);
        var rows = new List<ActionHistoryRow>();
        foreach (var line in lines)
        {
            var wrapped = WrapText(line.Text, textWidth);
            for (var index = 0; index < wrapped.Count; index++)
            {
                rows.Add(new ActionHistoryRow(line.Kind, wrapped[index], ShowSymbol: index == 0));
            }
        }
        return rows;
    }

    private static void DrawActionHistoryRow(
        Cell[,] canvas,
        int x,
        int y,
        ActionHistoryRow row)
    {
        var palette = ActionLinePalette(row.Kind);
        if (row.ShowSymbol)
        {
            Put(canvas, x, y, ActionLineSymbol(row.Kind).ToString(), palette);
        }
        Put(canvas, x + 2, y, row.Text, palette);
    }

    private void RenderDetailsFrame(
        string title,
        IReadOnlyList<string> lines,
        int offset,
        int visibleRows)
    {
        var canvas = CreateActionCanvas(title, out var contentX, out var contentY, out var contentWidth);
        var count = Math.Min(visibleRows, Math.Max(0, lines.Count - offset));
        for (var i = 0; i < count; i++)
        {
            Put(canvas, contentX, contentY + i, Truncate(lines[offset + i], contentWidth), Palette.Text);
        }

        if (lines.Count > visibleRows)
        {
            PutRightAligned(canvas, contentX, contentX + contentWidth, contentY + visibleRows,
                $"{offset + 1}–{offset + count} / {lines.Count}", Palette.Dim);
        }
        Center(canvas, CanvasHeight - 1, "↑ ↓ прокрутка   Enter / Esc — вернуться", Palette.Dim);
        Render(canvas);
    }

    private Cell[,] CreateActionCanvas(
        string title,
        out int contentX,
        out int contentY,
        out int contentWidth)
    {
        var canvas = CreateCanvas();
        var head = GetAmbientSweepHead();

        if (compactLayout)
        {
            Center(canvas, 1, "IS74W · InterSvyaz Wi-Fi Auth", Palette.BrandBright);
            var boxY = 4;
            var boxHeight = Math.Min(22, CanvasHeight - boxY - 2);
            DrawBox(canvas, 1, boxY, Math.Max(20, canvasWidth - 2), boxHeight, title);
            contentX = 4;
            contentY = boxY + 2;
            contentWidth = GetActionContentWidth();
            return canvas;
        }

        DrawBanner(canvas, 0, head is null ? BannerMode.Final : BannerMode.AmbientSweep, head ?? 0);
        Center(canvas, 13, Subtitle, Palette.Dim);
        DrawBox(canvas, leftPaneX, PaneY, paneWidth, PaneHeight, title);
        DrawStatusPane(canvas);
        contentX = leftPaneX + 3;
        contentY = PaneY + 2;
        contentWidth = GetActionContentWidth();
        return canvas;
    }

    private void RenderInteractiveFrame()
    {
        if (compactLayout)
        {
            RenderCompactInteractiveFrame();
            return;
        }

        var canvas = CreateCanvas();
        var head = GetAmbientSweepHead();
        DrawBanner(canvas, 0, head is null ? BannerMode.Final : BannerMode.AmbientSweep, head ?? 0);
        Center(canvas, 13, Subtitle, Palette.Dim);
        DrawLeftPane(canvas);
        DrawStatusPane(canvas);
        Center(canvas, 29, "↑ ↓ выбрать   Enter открыть   1–7/0 сразу   Esc выход   R reveal", Palette.Dim);
        Render(canvas);
    }

    private void RenderCompactInteractiveFrame()
    {
        var canvas = CreateCanvas();
        Center(canvas, 1, "IS74W · InterSvyaz Wi-Fi Auth", Palette.BrandBright);

        var s = status;
        if (s is not null)
        {
            Center(canvas, 3,
                $"Интернет: {FormatInternet(s.InternetAvailable)}   Автовход: {(s.AutomaticAuthorizationEnabled ? "вкл ●" : "выкл ○")}",
                Palette.Dim);
        }

        var boxY = 5;
        var boxHeight = Math.Min(20, CanvasHeight - boxY - 2);
        DrawBox(canvas, 1, boxY, Math.Max(20, canvasWidth - 2), boxHeight, "МЕНЮ");
        DrawCurrentItems(canvas, boxY + 2);
        Center(canvas, CanvasHeight - 1, "↑ ↓   Enter   1–7/0   Esc выход   R reveal", Palette.Dim);
        Render(canvas);
    }

    private double? GetAmbientSweepHead()
    {
        if (ambientSweepStartedUtc is not { } started)
        {
            return null;
        }

        var progress = Math.Clamp((DateTimeOffset.UtcNow - started).TotalMilliseconds / 620.0, 0, 1);
        return -7 + progress * (BannerWidth + 14);
    }

    private void RenderInstallFrame(bool upgrade, string? installedVersion, string installDirectory)
    {
        var canvas = CreateCanvas();
        var head = GetAmbientSweepHead();
        DrawBanner(canvas, 0, head is null ? BannerMode.Final : BannerMode.AmbientSweep, head ?? 0);
        Center(canvas, 13, Subtitle, Palette.Dim);

        DrawBox(canvas, leftPaneX, PaneY, paneWidth, PaneHeight, upgrade ? "ОБНОВЛЕНИЕ" : "ПЕРВЫЙ ЗАПУСК");
        var innerWidth = paneWidth - 6;
        if (upgrade)
        {
            Put(canvas, leftPaneX + 3, PaneY + 2, "Установленная версия", Palette.Dim);
            Put(canvas, leftPaneX + 3, PaneY + 3, Truncate(installedVersion ?? "неизвестно", innerWidth), Palette.Text);
            Put(canvas, leftPaneX + 3, PaneY + 5, "Запущенная версия", Palette.Dim);
            Put(canvas, leftPaneX + 3, PaneY + 6, Truncate(productVersion, innerWidth), Palette.Text);
        }
        else
        {
            PutWrapped(canvas, leftPaneX + 3, PaneY + 2, innerWidth,
                "IS74W необходимо установить для дальнейшей работы.", Palette.Text);
            Put(canvas, leftPaneX + 3, PaneY + 6, "Без прав администратора", Palette.Dim);
            Put(canvas, leftPaneX + 3, PaneY + 7, Truncate(installDirectory, innerWidth), Palette.Dim);
        }

        var installLabel = upgrade ? "Обновить IS74W" : "Установить IS74W";
        DrawSelectable(canvas, leftPaneX + 3, PaneY + 10, '1', installLabel, selected == 0);
        DrawSelectable(canvas, leftPaneX + 3, PaneY + 11, '0', "Выход", selected == 1);

        DrawInstallStatus(canvas, installed: upgrade, registered: false, automatic: false, agent: false);
        Center(canvas, 29, "↑ ↓ выбрать   Enter продолжить   1/0 сразу   Esc выйти   R reveal", Palette.Dim);
        Render(canvas);
    }

    private void DrawLeftPane(Cell[,] canvas)
    {
        DrawBox(canvas, leftPaneX, PaneY, paneWidth, PaneHeight, "МЕНЮ");
        DrawCurrentItems(canvas, PaneY + 2);
    }

    private void DrawCurrentItems(Cell[,] canvas, int firstRow)
    {
        var items = GetCurrentItems();
        for (var i = 0; i < items.Count; i++)
        {
            DrawSelectable(canvas, leftPaneX + 3, firstRow + i, items[i].Hotkey, items[i].Label, i == selected);
        }
    }

    private IReadOnlyList<MenuItem> GetCurrentItems()
    {
        return GetPrimaryItems(status);
    }

    private static IReadOnlyList<MenuItem> GetPrimaryItems(InteractiveStatusSnapshot? snapshot)
    {
        var automaticEnabled = snapshot?.AutomaticAuthorizationEnabled == true;
        var registered = snapshot?.Registered == true;

        return
        [
            new MenuItem('1', "Авторизовать Wi-Fi сейчас", InteractiveMenuAction.Connect),
            new MenuItem(
                '2',
                automaticEnabled ? "Отключить автоавторизацию" : "Включить автоавторизацию",
                automaticEnabled ? InteractiveMenuAction.DisableAutomaticAuthorization : InteractiveMenuAction.EnableAutomaticAuthorization),
            new MenuItem(
                '3',
                registered ? "Сбросить регистрацию" : "Зарегистрировать устройство",
                registered ? InteractiveMenuAction.ResetRegistration : InteractiveMenuAction.Register),
            new MenuItem('4', "Открыть подробный отчёт", InteractiveMenuAction.ShowDetailedStatus),
            new MenuItem('5', "Открыть диагностические логи", InteractiveMenuAction.OpenLogs),
            new MenuItem('6', "Проверить обновления", InteractiveMenuAction.Update),
            new MenuItem('7', "Удалить программу и данные", InteractiveMenuAction.Uninstall),
            new MenuItem('0', "Выход", InteractiveMenuAction.Exit)
        ];
    }

    private void DrawStatusPane(Cell[,] canvas)
    {
        DrawBox(canvas, rightPaneX, PaneY, paneWidth, PaneHeight, "СОСТОЯНИЕ");
        var s = status;
        if (s is null)
        {
            Put(canvas, rightPaneX + 3, PaneY + 3, "Загрузка состояния...", Palette.Dim);
            return;
        }

        DrawStatusLine(canvas, PaneY + 2, "Интернет", FormatInternet(s.InternetAvailable),
            s.InternetAvailable == true ? Palette.Good : s.InternetAvailable == false ? Palette.Dim : Palette.Highlight);
        DrawStatusLine(canvas, PaneY + 3, "Wi-Fi доступ", s.WifiAuthorizationActive ? "активен ●" : "нет ○",
            s.WifiAuthorizationActive ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 4, "Автовход", s.AutomaticAuthorizationEnabled ? "включён ●" : "выключен ○",
            s.AutomaticAuthorizationEnabled ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 5, "Агент", s.AgentRunning ? "работает ●" : "остановлен ○",
            s.AgentRunning ? Palette.Good : Palette.Dim);

        Put(canvas, rightPaneX + 3, PaneY + 7, "Телефон", Palette.Dim);
        PutRightAligned(canvas, rightPaneX + 3, rightPaneX + paneWidth - 3, PaneY + 7, s.MaskedPhone, Palette.Text);
        Put(canvas, rightPaneX + 3, PaneY + 8, "API-сессия", Palette.Dim);
        PutRightAligned(canvas, rightPaneX + 3, rightPaneX + paneWidth - 3, PaneY + 8, s.ApiSessionEnd, Palette.Text);

        Put(canvas, rightPaneX + 3, PaneY + 10, "Результат", Palette.Dim);
        PutRightAligned(canvas, rightPaneX + 3, rightPaneX + paneWidth - 3, PaneY + 10, s.LastResult, Palette.Text);
        Put(canvas, rightPaneX + 3, PaneY + 11, "Версия", Palette.Dim);
        PutRightAligned(canvas, rightPaneX + 3, rightPaneX + paneWidth - 3, PaneY + 11, s.Version, Palette.Text);
    }

    private static string FormatInternet(bool? value) => value switch
    {
        true => "доступен ●",
        false => "нет ○",
        null => "проверка ◌"
    };

    private void DrawInstallStatus(Cell[,] canvas, bool installed, bool registered, bool automatic, bool agent)
    {
        DrawBox(canvas, rightPaneX, PaneY, paneWidth, PaneHeight, "СОСТОЯНИЕ");
        DrawStatusLine(canvas, PaneY + 2, "Установка", installed ? "есть ●" : "нет ○", installed ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 3, "Регистрация", registered ? "есть ●" : "нет ○", registered ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 4, "Автовход", automatic ? "включён ●" : "выключен ○", automatic ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 5, "Агент", agent ? "работает ●" : "остановлен ○", agent ? Palette.Good : Palette.Dim);
        Put(canvas, rightPaneX + 3, PaneY + 8, "Версия", Palette.Dim);
        PutRightAligned(canvas, rightPaneX + 3, rightPaneX + paneWidth - 3, PaneY + 8, productVersion, Palette.Text);
    }

    private void DrawStatusLine(Cell[,] canvas, int row, string label, string value, Palette valueColor)
    {
        Put(canvas, rightPaneX + 3, row, label, Palette.Dim);
        PutRightAligned(canvas, rightPaneX + 15, rightPaneX + paneWidth - 3, row, value, valueColor);
    }

    private void DrawSelectable(Cell[,] canvas, int x, int y, char hotkey, string text, bool isSelected)
    {
        Put(canvas, x, y, isSelected ? "› " : "  ", isSelected ? Palette.Highlight : Palette.Text);
        Put(canvas, x + 2, y, $"[{hotkey}]", Palette.Dim);
        Put(canvas, x + 6, y, Truncate(text, Math.Max(1, paneWidth - 10)), isSelected ? Palette.Bright : Palette.Text);
    }

    private static void DrawBox(Cell[,] canvas, int x, int y, int width, int height, string title)
    {
        Put(canvas, x, y, "┌" + new string('─', width - 2) + "┐", Palette.Border);
        for (var row = 1; row < height - 1; row++)
        {
            Put(canvas, x, y + row, "│", Palette.Border);
            Put(canvas, x + width - 1, y + row, "│", Palette.Border);
        }
        Put(canvas, x, y + height - 1, "└" + new string('─', width - 2) + "┘", Palette.Border);

        var caption = $" {title} ";
        var captionX = x + Math.Max(2, (width - caption.Length) / 2);
        Put(canvas, captionX, y, Truncate(caption, width - 4), Palette.Highlight);
    }

    private static void PutWrapped(Cell[,] canvas, int x, int y, int width, string text, Palette color)
    {
        var lines = WrapText(text, Math.Max(1, width));
        for (var index = 0; index < lines.Count; index++)
        {
            Put(canvas, x, y + index, lines[index], color);
        }
    }

    private static List<string> WrapText(string? text, int width)
    {
        width = Math.Max(1, width);
        var value = string.IsNullOrWhiteSpace(text) ? "—" : text.Trim();
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        var line = new StringBuilder();

        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                result.Add(line.ToString());
                line.Clear();
            }

            var remaining = word;
            while (remaining.Length > width)
            {
                if (line.Length > 0)
                {
                    result.Add(line.ToString());
                    line.Clear();
                }
                result.Add(remaining[..width]);
                remaining = remaining[width..];
            }

            if (remaining.Length == 0)
            {
                continue;
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }
            line.Append(remaining);
        }

        if (line.Length > 0)
        {
            result.Add(line.ToString());
        }

        if (result.Count == 0)
        {
            result.Add("—");
        }
        return result;
    }

    private static int BannerWidth => Banner.Max(line => line.Length);

    private static void DrawBanner(Cell[,] canvas, int top, BannerMode mode, double head)
    {
        var width = canvas.GetLength(1);
        var left = Math.Max(0, (width - BannerWidth) / 2);
        for (var row = 0; row < Banner.Length; row++)
        {
            var line = Banner[row];
            for (var col = 0; col < line.Length; col++)
            {
                var ch = line[col];
                if (ch == ' ') continue;

                var color = Palette.Brand;
                var distance = col - head;
                if (mode == BannerMode.InitialSweep)
                {
                    color = distance switch
                    {
                        > 4 => Palette.Border,
                        > 1 => Palette.BrandDim,
                        >= -1 => Palette.Bright,
                        _ when Math.Abs(distance) <= 4 => Palette.Highlight,
                        _ => Palette.Brand
                    };
                }
                else if (mode == BannerMode.AmbientSweep)
                {
                    var absolute = Math.Abs(distance);
                    color = absolute switch
                    {
                        < 0.9 => Palette.Bright,
                        < 2.4 => Palette.Highlight,
                        < 4.8 => Palette.BrandBright,
                        < 7.0 => Palette.Brand,
                        _ => Palette.Brand
                    };
                }

                Put(canvas, left + col, top + row, ch.ToString(), color);
            }
        }
    }

    private Cell[,] CreateCanvas()
    {
        var canvas = new Cell[CanvasHeight, canvasWidth];
        for (var y = 0; y < CanvasHeight; y++)
        {
            for (var x = 0; x < canvasWidth; x++)
            {
                canvas[y, x] = new Cell(' ', Palette.Text);
            }
        }
        return canvas;
    }

    private static void Center(Cell[,] canvas, int row, string text, Palette color)
    {
        Put(canvas, Math.Max(0, (canvas.GetLength(1) - text.Length) / 2), row, text, color);
    }

    private static void Put(Cell[,] canvas, int x, int y, string text, Palette color)
    {
        var height = canvas.GetLength(0);
        var width = canvas.GetLength(1);
        if (y < 0 || y >= height) return;
        for (var i = 0; i < text.Length; i++)
        {
            var targetX = x + i;
            if (targetX < 0 || targetX >= width) continue;
            canvas[y, targetX] = new Cell(text[i], color);
        }
    }

    private static void PutRightAligned(
        Cell[,] canvas,
        int minimumX,
        int rightExclusive,
        int row,
        string? value,
        Palette color)
    {
        var available = Math.Max(1, rightExclusive - minimumX);
        var text = Truncate(value, available);
        var x = Math.Max(minimumX, rightExclusive - text.Length);
        Put(canvas, x, row, text, color);
    }

    private void Render(Cell[,] canvas)
    {
        if (ansi)
        {
            RenderAnsi(canvas);
        }
        else
        {
            RenderConsoleColors(canvas);
        }
    }

    private void RenderAnsi(Cell[,] canvas)
    {
        var width = canvas.GetLength(1);
        var height = canvas.GetLength(0);
        var terminalWidth = GetWindowWidthSafe();
        var fullRender = lastRenderedCanvas is null ||
                         lastRenderedCanvas.GetLength(0) != height ||
                         lastRenderedCanvas.GetLength(1) != width ||
                         lastRenderedLeft != renderLeft ||
                         lastRenderedTerminalWidth != terminalWidth;

        var output = new StringBuilder(height * (fullRender ? width + 48 : 32));
        Palette? active = null;

        if (fullRender)
        {
            for (var y = 0; y < height; y++)
            {
                output.Append("\u001b[").Append(y + 1).Append(";1H\u001b[2K");
                output.Append("\u001b[").Append(y + 1).Append(';').Append(renderLeft + 1).Append('H');
                active = null;
                for (var x = 0; x < width; x++)
                {
                    var cell = canvas[y, x];
                    if (active != cell.Color)
                    {
                        output.Append(ToAnsi(cell.Color));
                        active = cell.Color;
                    }
                    output.Append(cell.Character);
                }
            }
        }
        else
        {
            // Only repaint cells that actually changed. Redrawing and clearing the
            // complete banner at 60 FPS made static glyphs appear to shimmer in
            // Windows Terminal even though their coordinates never moved.
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = canvas[y, x];
                    if (cell.Equals(lastRenderedCanvas![y, x]))
                    {
                        continue;
                    }

                    output.Append("\u001b[").Append(y + 1).Append(';').Append(renderLeft + x + 1).Append('H');
                    if (active != cell.Color)
                    {
                        output.Append(ToAnsi(cell.Color));
                        active = cell.Color;
                    }
                    output.Append(cell.Character);
                }
            }
        }

        output.Append("\u001b[0m");
        Console.Write(output.ToString());
        RememberRenderedCanvas(canvas, terminalWidth);
    }

    private void RenderConsoleColors(Cell[,] canvas)
    {
        var width = canvas.GetLength(1);
        var height = canvas.GetLength(0);
        var terminalWidth = GetWindowWidthSafe();
        var fullRender = lastRenderedCanvas is null ||
                         lastRenderedCanvas.GetLength(0) != height ||
                         lastRenderedCanvas.GetLength(1) != width ||
                         lastRenderedLeft != renderLeft ||
                         lastRenderedTerminalWidth != terminalWidth;

        if (fullRender)
        {
            for (var y = 0; y < height; y++)
            {
                try
                {
                    Console.SetCursorPosition(0, y);
                    Console.Write(new string(' ', Math.Max(1, terminalWidth - 1)));
                }
                catch
                {
                }
            }
        }

        Palette? active = null;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cell = canvas[y, x];
                if (!fullRender && cell.Equals(lastRenderedCanvas![y, x]))
                {
                    continue;
                }

                try
                {
                    Console.SetCursorPosition(renderLeft + x, y);
                }
                catch
                {
                    continue;
                }

                if (active != cell.Color)
                {
                    Console.ForegroundColor = ToConsoleColor(cell.Color);
                    active = cell.Color;
                }
                Console.Write(cell.Character);
            }
        }
        Console.ResetColor();
        RememberRenderedCanvas(canvas, terminalWidth);
    }

    private void RememberRenderedCanvas(Cell[,] canvas, int terminalWidth)
    {
        lastRenderedCanvas = (Cell[,])canvas.Clone();
        lastRenderedLeft = renderLeft;
        lastRenderedTerminalWidth = terminalWidth;
    }

    private static int GetWindowWidthSafe()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch
        {
            return MinimumTerminalWidth;
        }
    }

    private static string ToAnsi(Palette color) => color switch
    {
        Palette.Border => "\u001b[38;2;18;54;75m",
        Palette.BrandDim => "\u001b[38;2;26;91;126m",
        Palette.Brand => "\u001b[38;2;42;151;202m",
        Palette.BrandBright => "\u001b[38;2;88;190;235m",
        Palette.Highlight => "\u001b[38;2;132;220;255m",
        Palette.Bright => "\u001b[38;2;238;250;255m",
        Palette.Good => "\u001b[38;2;104;207;174m",
        Palette.Warning => "\u001b[38;2;255;205;96m",
        Palette.Error => "\u001b[38;2;255;116;116m",
        Palette.Dim => "\u001b[38;2;92;122;139m",
        _ => "\u001b[38;2;166;194;208m"
    };

    private static ConsoleColor ToConsoleColor(Palette color) => color switch
    {
        Palette.Border => ConsoleColor.DarkBlue,
        Palette.BrandDim => ConsoleColor.DarkCyan,
        Palette.Brand => ConsoleColor.Blue,
        Palette.BrandBright => ConsoleColor.Cyan,
        Palette.Highlight => ConsoleColor.Cyan,
        Palette.Bright => ConsoleColor.White,
        Palette.Good => ConsoleColor.Green,
        Palette.Warning => ConsoleColor.Yellow,
        Palette.Error => ConsoleColor.Red,
        Palette.Dim => ConsoleColor.DarkGray,
        _ => ConsoleColor.Gray
    };

    private static string Truncate(string? value, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "—" : value;
        if (text.Length <= maxLength) return text;
        if (maxLength <= 1) return text[..maxLength];
        return text[..(maxLength - 1)] + "…";
    }

    private void UpdateLayout()
    {
        try
        {
            var terminalWidth = Console.WindowWidth;
            compactLayout = terminalWidth < MinimumTerminalWidth;

            if (compactLayout)
            {
                canvasWidth = Math.Max(20, terminalWidth - 1);
                renderLeft = 0;
                paneWidth = Math.Max(18, canvasWidth - 2);
                leftPaneX = 1;
                rightPaneX = 1;
                return;
            }

            var drawableWidth = Math.Max(MinimumCanvasWidth, terminalWidth - 1);
            canvasWidth = Math.Min(PreferredCanvasWidth, drawableWidth);
            renderLeft = Math.Max(0, (terminalWidth - canvasWidth) / 2);

            var gap = canvasWidth >= 100 ? 4 : 3;
            paneWidth = Math.Max(30, (canvasWidth - 2 - gap) / 2);
            leftPaneX = 1;
            rightPaneX = leftPaneX + paneWidth + gap;
        }
        catch
        {
            compactLayout = false;
            canvasWidth = MinimumCanvasWidth;
            renderLeft = 0;
            paneWidth = 37;
            leftPaneX = 1;
            rightPaneX = 41;
        }
    }

    private void PrepareInteractiveConsole(bool clear = true)
    {
        lastRenderedCanvas = null;
        lastRenderedLeft = -1;
        lastRenderedTerminalWidth = -1;
        try
        {
            Console.CursorVisible = false;
        }
        catch
        {
        }
        if (clear)
        {
            Console.Clear();
        }
    }

    private static void RestoreConsole(bool showCursor = true)
    {
        try
        {
            Console.ResetColor();
            if (showCursor) Console.CursorVisible = true;
        }
        catch
        {
        }
    }

    private readonly record struct MenuItem(
        char Hotkey,
        string Label,
        InteractiveMenuAction Action);

    private readonly record struct ActionHistoryRow(
        InteractiveActionLineKind Kind,
        string Text,
        bool ShowSymbol);

    private readonly record struct Cell(char Character, Palette Color);

    private enum Palette
    {
        Border,
        BrandDim,
        Brand,
        BrandBright,
        Highlight,
        Bright,
        Good,
        Warning,
        Error,
        Dim,
        Text
    }

    private enum BannerMode
    {
        Final,
        InitialSweep,
        AmbientSweep
    }
}
