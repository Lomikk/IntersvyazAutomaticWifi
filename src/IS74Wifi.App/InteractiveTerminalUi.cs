using System.Diagnostics;
using System.Text;
using IS74Wifi.Core;
using static IS74Wifi.App.TerminalCanvas;

namespace IS74Wifi.App;

internal sealed partial class InteractiveTerminalUi
{
    private const int CanvasHeight = TerminalLayout.CanvasHeight;
    private const int PaneY = TerminalLayout.PaneY;
    private const int PaneHeight = TerminalLayout.PaneHeight;
    private static readonly TimeSpan LocalStatusRefreshInterval = TimeSpan.FromSeconds(2);

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
        "              .-:                    A'        MM",
        "            .:.                     A'         MM",
        "          .."
    ];

    private const string Subtitle = "InterSvyaz Wi-Fi Auth";
    private const string ActiveStatusDot = "\u25CF";
    private const string InactiveStatusDot = "\u25CB";

    private readonly string productVersion;
    private readonly TerminalOutput terminalOutput;
    private InteractiveStatusSnapshot? status;
    private int selected;
    private MenuPage menuPage = MenuPage.Main;
    private bool menuSelectionInitialized;
    private TerminalLayout layout = TerminalLayout.Fallback;
    private int canvasWidth => layout.CanvasWidth;
    private int renderLeft => layout.RenderLeft;
    private int leftPaneX => layout.LeftPaneX;
    private int rightPaneX => layout.RightPaneX;
    private int paneWidth => layout.PaneWidth;
    private bool compactLayout => layout.Compact;
    private DateTimeOffset nextAmbientSweepUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(14);
    private DateTimeOffset? ambientSweepStartedUtc;

    public InteractiveTerminalUi(string productVersion)
    {
        this.productVersion = productVersion;
        terminalOutput = new TerminalOutput(ConsoleSession.SupportsVirtualTerminal);
    }

    public void OpenUpdatesPage() => NavigateTo(MenuPage.Updates);

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
                return TerminalLayout.Calculate(Console.WindowWidth, Console.WindowHeight).SupportsInteractiveSession;
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
                return TerminalLayout.Calculate(Console.WindowWidth, Console.WindowHeight).SupportsRichLayout;
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
        Func<InteractiveStatusSnapshot> readLocalStatus,
        CancellationToken cancellationToken = default)
    {
        status = initialStatus;

        if (!CanUseInteractiveSession)
        {
            InvalidateRenderedFrame();
            menuSelectionInitialized = false;
            return RunCompactMenu(initialStatus);
        }

        if (!menuSelectionInitialized)
        {
            selected = 0;
            menuSelectionInitialized = true;
        }

        UpdateLayout();
        // Keep the previous framebuffer between menu/workflow calls. This lets
        // the diff renderer restore only cells that actually changed instead
        // of clearing and repainting the whole terminal on every return.
        PrepareInteractiveConsole(clear: !terminalOutput.HasRenderedFrame);
        try
        {
            if (showReveal && CanUseRichLayout)
            {
                await PlayInitialRevealAsync(cancellationToken).ConfigureAwait(false);
            }

            nextAmbientSweepUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Random.Shared.Next(12, 19));
            ambientSweepStartedUtc = null;

            var keyTask = ReadKeyAsync();
            Task<InteractiveStatusSnapshot>? localStatusTask = null;
            var nextLocalStatusRefreshUtc = DateTimeOffset.UtcNow + LocalStatusRefreshInterval;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Stay in the interactive loop while the user resizes the terminal.
                // Falling back to the blocking compact menu here made a temporary
                // narrow resize permanent until the whole menu was restarted.
                UpdateLayout();

                if (refreshedStatusTask is { IsCompleted: true })
                {
                    if (refreshedStatusTask.IsCompletedSuccessfully)
                    {
                        status = refreshedStatusTask.Result;
                    }
                    else if (refreshedStatusTask.IsFaulted)
                    {
                        _ = refreshedStatusTask.Exception;
                    }
                    refreshedStatusTask = null;
                }

                // The agent writes its authorization result into local runtime state.
                // Re-read that state while the menu stays open; otherwise the timer
                // keeps displaying the pre-authorization expiry until navigation.
                // Keep disk/WLAN reads off the 16 ms renderer and never issue an
                // Internet probe or update check from this periodic refresh.
                if (localStatusTask is { IsCompleted: true })
                {
                    if (localStatusTask.IsCompletedSuccessfully)
                    {
                        status = localStatusTask.Result;
                    }
                    else if (localStatusTask.IsFaulted)
                    {
                        _ = localStatusTask.Exception;
                    }
                    localStatusTask = null;
                }

                if (localStatusTask is null && DateTimeOffset.UtcNow >= nextLocalStatusRefreshUtc)
                {
                    nextLocalStatusRefreshUtc = DateTimeOffset.UtcNow + LocalStatusRefreshInterval;
                    localStatusTask = Task.Run(readLocalStatus, cancellationToken);
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

                if (key.Key == ConsoleKey.U && !string.IsNullOrWhiteSpace(status?.AvailableUpdateVersion))
                {
                    NavigateTo(MenuPage.Updates);
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (key.Key == ConsoleKey.Escape)
                {
                    if (menuPage != MenuPage.Main)
                    {
                        NavigateTo(menuPage == MenuPage.Updates ? MenuPage.Settings : MenuPage.Main);
                        keyTask = ReadKeyAsync();
                        continue;
                    }
                    return InteractiveMenuAction.Exit;
                }

                var items = GetCurrentItems();
                if (items.Count == 0)
                {
                    keyTask = ReadKeyAsync();
                    continue;
                }

                selected = Math.Clamp(selected, 0, items.Count - 1);

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

                InteractiveMenuAction? directAction = null;
                for (var hotkeyIndex = 0; hotkeyIndex < items.Count; hotkeyIndex++)
                {
                    if (items[hotkeyIndex].Hotkey != key.KeyChar)
                    {
                        continue;
                    }

                    // A direct numeric hotkey also becomes the current selection,
                    // so returning from an inline action (for example notifications)
                    // leaves the cursor on the item the user actually invoked.
                    selected = hotkeyIndex;
                    directAction = items[hotkeyIndex].Action;
                    break;
                }

                if (directAction is { } hotkeyAction)
                {
                    if (HandleMenuNavigation(hotkeyAction))
                    {
                        keyTask = ReadKeyAsync();
                        continue;
                    }
                    return hotkeyAction;
                }

                if (key.Key != ConsoleKey.Enter)
                {
                    keyTask = ReadKeyAsync();
                    continue;
                }

                var item = items[selected];
                if (HandleMenuNavigation(item.Action))
                {
                    keyTask = ReadKeyAsync();
                    continue;
                }
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

    private bool HandleMenuNavigation(InteractiveMenuAction action)
    {
        switch (action)
        {
            case InteractiveMenuAction.OpenSettings:
                NavigateTo(MenuPage.Settings);
                return true;
            case InteractiveMenuAction.OpenMaintenance:
                NavigateTo(MenuPage.Maintenance);
                return true;
            case InteractiveMenuAction.OpenUpdates:
                NavigateTo(MenuPage.Updates);
                return true;
            case InteractiveMenuAction.Back:
                NavigateTo(menuPage == MenuPage.Updates ? MenuPage.Settings : MenuPage.Main);
                return true;
            default:
                return false;
        }
    }

    private void NavigateTo(MenuPage page)
    {
        menuPage = page;
        selected = 0;
    }

    public async Task<bool> ConfirmRegistrationOrExitAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanUseInteractiveSession)
        {
            return ConfirmRegistrationCompact();
        }

        UpdateLayout();
        PrepareInteractiveConsole();
        try
        {
            selected = 0;
            nextAmbientSweepUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Random.Shared.Next(12, 19));
            ambientSweepStartedUtc = null;

            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanUseInteractiveSession)
                {
                    RestoreConsole();
                    return ConfirmRegistrationCompact();
                }

                UpdateLayout();
                UpdateAmbientSweepState();
                RenderRegistrationFrame();

                var completed = await Task.WhenAny(
                    keyTask,
                    Task.Delay(16, cancellationToken)).ConfigureAwait(false);
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

    public async Task<(bool Confirmed, string? SelectedId)> ChooseNetworkAdapterAsync(
        IReadOnlyList<PhysicalAdapter> adapters,
        string? currentId,
        CancellationToken cancellationToken = default)
    {
        var screen = new AdapterSelectionScreen(adapters, currentId);

        if (!CanUseInteractiveSession)
        {
            return ChooseNetworkAdapterCompact(screen, currentId, cancellationToken);
        }

        UpdateLayout();
        PrepareInteractiveConsole(clear: false);
        try
        {
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateLayout();
                UpdateAmbientSweepState();
                Render(screen.BuildFrame(layout));

                var completed = await Task.WhenAny(keyTask, Task.Delay(16, cancellationToken)).ConfigureAwait(false);
                if (completed != keyTask)
                {
                    continue;
                }

                var key = await keyTask.ConfigureAwait(false);
                var result = screen.HandleKey(key, screen.GetVisibleRowCount(layout));
                if (result.Outcome == AdapterSelectionOutcome.Confirmed)
                {
                    return (true, result.SelectedId);
                }

                if (result.Outcome == AdapterSelectionOutcome.Cancelled)
                {
                    return (false, currentId);
                }

                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            RestoreConsole();
        }
    }

    private static (bool Confirmed, string? SelectedId) ChooseNetworkAdapterCompact(
        AdapterSelectionScreen screen,
        string? currentId,
        CancellationToken cancellationToken)
    {
        Console.Clear();
        Console.WriteLine("IS74W — адаптер для авторизации");
        Console.WriteLine();
        foreach (var option in screen.Items)
        {
            Console.WriteLine($"[{option.Shortcut}] {option.DisplayLabel}");
        }
        Console.WriteLine();
        Console.WriteLine("Пустой ввод — отмена.");
        Console.Write("Выбор: ");
        cancellationToken.ThrowIfCancellationRequested();
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) return (false, currentId);
        var selectedOption = screen.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Shortcut, input, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(selectedOption.Shortcut)
            ? (false, currentId)
            : (true, selectedOption.Id);
    }

    private static bool ConfirmRegistrationCompact()
    {
        Console.Clear();
        Console.WriteLine("IS74W — InterSvyaz Wi-Fi Auth");
        Console.WriteLine();
        Console.WriteLine("Для продолжения необходимо зарегистрировать устройство.");
        Console.WriteLine();
        Console.WriteLine("[1] Зарегистрировать устройство");
        Console.WriteLine("[0] Выход");

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.KeyChar == '1' || key.Key == ConsoleKey.Enter) return true;
            if (key.KeyChar == '0' || key.Key == ConsoleKey.Escape) return false;
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

    public async Task<bool> ConfirmYesNoAsync(
        string title,
        string message,
        string yesLabel,
        string noLabel,
        InteractiveStatusSnapshot currentStatus,
        CancellationToken cancellationToken = default)
    {
        status = currentStatus;
        if (!CanUseInteractiveSession)
        {
            Console.Clear();
            Console.WriteLine(title);
            Console.WriteLine();
            Console.WriteLine(message);
            Console.WriteLine();
            Console.WriteLine($"[Y] {yesLabel}");
            Console.WriteLine($"[N] {noLabel}");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Y) return true;
                if (key.Key is ConsoleKey.N or ConsoleKey.Escape) return false;
            }
        }

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
                RenderYesNoFrame(title, message, yesLabel, noLabel);

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
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.N)
                {
                    return false;
                }
                if (key.Key == ConsoleKey.Y)
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
        status = snapshot;

        while (true)
        {
            Console.Clear();
            Console.WriteLine("IS74W — InterSvyaz Wi-Fi Auth");
            Console.WriteLine();
            Console.WriteLine($"Интернет      : {FormatInternet(snapshot.InternetAvailable)}");
            Console.WriteLine($"Сеть          : {FormatNetworkStatus(snapshot).Text}");
            Console.WriteLine($"Авторизация   : {FormatAuthorizationStatus(snapshot).Text}");
            Console.WriteLine($"Автовход      : {(snapshot.AutomaticAuthorizationEnabled ? "включён ●" : "выключен ○")}");
            Console.WriteLine($"Фоновый режим : {(snapshot.AgentRunning ? "работает ●" : "остановлен ○")}");
            if (!string.IsNullOrWhiteSpace(snapshot.AvailableUpdateVersion))
            {
                Console.WriteLine($"Обновление    : доступно {snapshot.AvailableUpdateVersion} ●");
            }
            Console.WriteLine();
            Console.WriteLine($"=== {GetMenuTitle()} ===");

            var action = RunCompactSelection(GetCurrentItems());
            if (HandleMenuNavigation(action))
            {
                continue;
            }
            return action;
        }
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
                if (key.Key == ConsoleKey.U && menuPage == MenuPage.Main && !string.IsNullOrWhiteSpace(status?.AvailableUpdateVersion))
                {
                    return InteractiveMenuAction.OpenUpdates;
                }

                var hotkeyItem = items.FirstOrDefault(item => item.Hotkey == key.KeyChar);
                if (hotkeyItem.Action != InteractiveMenuAction.None)
                {
                    return hotkeyItem.Action;
                }
            }
            if (key.Key == ConsoleKey.Escape)
            {
                return menuPage == MenuPage.Main
                    ? InteractiveMenuAction.Exit
                    : InteractiveMenuAction.Back;
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

    private void RenderYesNoFrame(
        string title,
        string message,
        string yesLabel,
        string noLabel)
    {
        var canvas = CreateActionCanvas(title, out var contentX, out var contentY, out var contentWidth);
        PutWrapped(canvas, contentX, contentY, contentWidth, message, Palette.Text);
        DrawSelectable(canvas, contentX, contentY + 7, 'Y', yesLabel, selected == 0);
        DrawSelectable(canvas, contentX, contentY + 8, 'N', noLabel, selected == 1);
        Center(canvas, CanvasHeight - 1, "↑ ↓ выбрать   Enter продолжить   Y/N сразу   Esc — нет", Palette.Dim);
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
        InteractiveActionLineKind.Success => '+',
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
        Center(canvas, 29,
            menuPage == MenuPage.Main
                ? (!string.IsNullOrWhiteSpace(status?.AvailableUpdateVersion)
                    ? "↑ ↓ выбрать   Enter открыть   U обновления   Esc выход   R reveal"
                    : "↑ ↓ выбрать   Enter открыть   цифра — сразу   Esc выход   R reveal")
                : "↑ ↓ выбрать   Enter открыть   цифра — сразу   Esc назад   R reveal",
            Palette.Dim);
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
        DrawBox(canvas, 1, boxY, Math.Max(20, canvasWidth - 2), boxHeight, GetMenuTitle());
        DrawCurrentItems(canvas, boxY + 2);
        Center(canvas, CanvasHeight - 1,
            menuPage == MenuPage.Main
                ? (!string.IsNullOrWhiteSpace(status?.AvailableUpdateVersion)
                    ? "↑ ↓   Enter   U обновления   Esc выход   R reveal"
                    : "↑ ↓   Enter   цифра — сразу   Esc выход   R reveal")
                : "↑ ↓   Enter   цифра — сразу   Esc назад   R reveal",
            Palette.Dim);
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

    private void RenderRegistrationFrame()
    {
        var canvas = CreateCanvas();
        var head = GetAmbientSweepHead();

        if (!compactLayout)
        {
            DrawBanner(canvas, 0, head is null ? BannerMode.Final : BannerMode.AmbientSweep, head ?? 0);
            Center(canvas, 13, Subtitle, Palette.Dim);
        }
        else
        {
            Center(canvas, 1, "IS74W · InterSvyaz Wi-Fi Auth", Palette.BrandBright);
        }

        var boxY = compactLayout ? 5 : PaneY;
        var boxHeight = compactLayout ? Math.Min(20, CanvasHeight - boxY - 2) : PaneHeight;
        var boxWidth = Math.Max(20, canvasWidth - 2);
        DrawBox(canvas, 1, boxY, boxWidth, boxHeight, "РЕГИСТРАЦИЯ");

        var contentX = 4;
        var contentWidth = Math.Max(10, boxWidth - 6);
        PutWrapped(
            canvas,
            contentX,
            boxY + 2,
            contentWidth,
            "Для продолжения необходимо зарегистрировать устройство.",
            Palette.Text);
        DrawSelectable(canvas, contentX, boxY + 7, '1', "Зарегистрировать устройство", selected == 0);
        DrawSelectable(canvas, contentX, boxY + 8, '0', "Выход", selected == 1);
        Center(canvas, CanvasHeight - 1, "↑ ↓ выбрать   Enter продолжить   1/0 сразу   Esc выйти", Palette.Dim);
        Render(canvas);
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
        DrawBox(canvas, leftPaneX, PaneY, paneWidth, PaneHeight, GetMenuTitle());
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
        return menuPage switch
        {
            MenuPage.Settings => GetSettingsItems(status),
            MenuPage.Maintenance => GetMaintenanceItems(status),
            MenuPage.Updates => GetUpdateItems(status),
            _ => GetPrimaryItems(status)
        };
    }

    private string GetMenuTitle() => menuPage switch
    {
        MenuPage.Settings => "НАСТРОЙКИ",
        MenuPage.Maintenance => "ОБСЛУЖИВАНИЕ",
        MenuPage.Updates => "ОБНОВЛЕНИЯ",
        _ => "МЕНЮ"
    };

    private static IReadOnlyList<MenuItem> GetPrimaryItems(InteractiveStatusSnapshot? snapshot) =>
    [
        new MenuItem('1', "Авторизовать Wi-Fi сейчас", InteractiveMenuAction.Connect),
        new MenuItem('2', "Скорость и рейтинг", InteractiveMenuAction.SpeedTools),
        new MenuItem('3', "Состояние и подробный отчёт", InteractiveMenuAction.ShowDetailedStatus),
        new MenuItem('4', "Настройки", InteractiveMenuAction.OpenSettings),
        new MenuItem('5', "Обслуживание", InteractiveMenuAction.OpenMaintenance),
        new MenuItem('0', "Выход", InteractiveMenuAction.Exit)
    ];

    private static IReadOnlyList<MenuItem> GetSettingsItems(InteractiveStatusSnapshot? snapshot)
    {
        var automaticEnabled = snapshot?.AutomaticAuthorizationEnabled == true;
        return
        [
            new MenuItem(
                '1',
                $"Автоавторизация: {(automaticEnabled ? "включена" : "выключена")}",
                automaticEnabled ? InteractiveMenuAction.DisableAutomaticAuthorization : InteractiveMenuAction.EnableAutomaticAuthorization),
            new MenuItem('2', $"Уведомления: {snapshot?.NotificationMode ?? "важные"}", InteractiveMenuAction.CycleNotifications),
            new MenuItem(
                '3',
                $"Анонимная статистика: {FormatStatisticsConsent(snapshot?.AnonymousStatisticsConsent ?? AnonymousStatisticsConsent.Unknown)}",
                InteractiveMenuAction.ToggleAnonymousStatistics),
            new MenuItem('4', "Обновления", InteractiveMenuAction.OpenUpdates),
            new MenuItem('5', "Сеть и диагностика", InteractiveMenuAction.DiagnoseDirectNetwork),
            new MenuItem('0', "Назад", InteractiveMenuAction.Back)
        ];
    }

    private static IReadOnlyList<MenuItem> GetUpdateItems(InteractiveStatusSnapshot? snapshot)
    {
        var items = new List<MenuItem>
        {
            new(
                '1',
                $"Режим: {(snapshot?.AutomaticUpdates == true ? "автоматически" : "уведомлять")}",
                InteractiveMenuAction.ToggleAutomaticUpdates),
            new(
                '2',
                snapshot?.IncludePrereleaseUpdates == true
                    ? "Pre-release (не рекомендуется): включены"
                    : "Pre-release (не рекомендуется): выключены",
                InteractiveMenuAction.TogglePrereleaseUpdates)
        };

        if (!string.IsNullOrWhiteSpace(snapshot?.AvailableUpdateVersion))
        {
            items.Add(new MenuItem('3', $"Установить {snapshot.AvailableUpdateVersion}", InteractiveMenuAction.Update));
            items.Add(new MenuItem('4', "Что изменилось", InteractiveMenuAction.OpenUpdateReleasePage));
        }
        else
        {
            items.Add(new MenuItem('3', "Проверить сейчас", InteractiveMenuAction.CheckUpdates));
        }

        items.Add(new MenuItem('0', "Назад", InteractiveMenuAction.Back));
        return items;
    }

    private static string FormatStatisticsConsent(AnonymousStatisticsConsent consent) => consent switch
    {
        AnonymousStatisticsConsent.Allowed => "включена",
        AnonymousStatisticsConsent.Declined => "выключена",
        _ => "не выбрано"
    };

    private static IReadOnlyList<MenuItem> GetMaintenanceItems(InteractiveStatusSnapshot? snapshot)
    {
        var registered = snapshot?.Registered == true;
        return
        [
            new MenuItem(
                '1',
                registered ? "Сбросить регистрацию" : "Зарегистрировать устройство",
                registered ? InteractiveMenuAction.ResetRegistration : InteractiveMenuAction.Register),
            new MenuItem('2', "Открыть диагностические логи", InteractiveMenuAction.OpenLogs),
            new MenuItem('3', "Удалить программу и данные", InteractiveMenuAction.Uninstall),
            new MenuItem('0', "Назад", InteractiveMenuAction.Back)
        ];
    }

    private void DrawStatusPane(Cell[,] canvas)
    {
        DrawNetworkPathStatusBox(canvas);
        var s = status;
        if (s is null)
        {
            Put(canvas, rightPaneX + 3, PaneY + 3, "Загрузка состояния...", Palette.Dim);
            return;
        }

        var paths = s.NetworkPaths ?? Array.Empty<InteractiveNetworkPathStatus>();
        const int maxPathRows = 4;
        var row = PaneY + 2;
        if (paths.Count == 0)
        {
            Put(canvas, rightPaneX + 3, row, "Нет активных физических путей", Palette.Dim);
        }
        else if (paths.Count <= maxPathRows)
        {
            foreach (var path in paths)
            {
                DrawNetworkPathLine(canvas, row++, path);
            }
        }
        else
        {
            foreach (var path in paths.Take(maxPathRows - 1))
            {
                DrawNetworkPathLine(canvas, row++, path);
            }
            Put(canvas, rightPaneX + 5, row, $"ещё {paths.Count - (maxPathRows - 1)} пути...", Palette.Dim);
        }

        DrawStatusLine(canvas, PaneY + 6, "VPN",
            s.VpnActive ? $"включён {ActiveStatusDot}" : $"выключен {InactiveStatusDot}",
            s.VpnActive ? Palette.Highlight : Palette.Dim);

        DrawStatusLine(canvas, PaneY + 8, "Автовход",
            s.AutomaticAuthorizationEnabled ? $"включён {ActiveStatusDot}" : $"выключен {InactiveStatusDot}",
            s.AutomaticAuthorizationEnabled ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 9, "Фоновый режим",
            s.AgentRunning ? $"работает {ActiveStatusDot}" : $"остановлен {InactiveStatusDot}",
            s.AgentRunning ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 10, "Уведомления",
            s.NotificationMode == "выкл" ? $"выкл {InactiveStatusDot}" : $"{s.NotificationMode} {ActiveStatusDot}",
            s.NotificationMode == "выкл" ? Palette.Dim : Palette.Good);
        DrawStatusLine(canvas, PaneY + 11, "Версия", s.Version, Palette.Text);

        if (!string.IsNullOrWhiteSpace(s.AvailableUpdateVersion))
        {
            DrawStatusLine(canvas, PaneY + 12, "Обновление", $"{s.AvailableUpdateVersion} {ActiveStatusDot}", Palette.Highlight);
        }
    }

    private void DrawNetworkPathStatusBox(Cell[,] canvas)
    {
        DrawBox(canvas, rightPaneX, PaneY, paneWidth, PaneHeight, title: null);
        var columns = GetNetworkPathColumns();
        PutCenteredInRange(canvas, columns.ContentX, columns.StateStart - 1, PaneY, "ПУТЬ", Palette.Highlight);
        PutRightAligned(canvas, columns.StateStart, columns.TimerStart - 1, PaneY, "СТАТУС", Palette.Highlight);

        const string timerHeader = "ДО ПРОВЕРКИ";
        var timerHeaderX = Math.Max(columns.StateStart, columns.RightExclusive - timerHeader.Length);
        Put(canvas, timerHeaderX, PaneY, timerHeader, Palette.Highlight);
    }

    private void DrawNetworkPathLine(Cell[,] canvas, int row, InteractiveNetworkPathStatus path)
    {
        var columns = GetNetworkPathColumns();
        var presentation = FormatPathPaneStatus(path);
        var marker = path.Preferred ? "> " : "  ";
        var label = marker + path.Name;

        Put(canvas, columns.ContentX, row, Truncate(label, columns.LabelWidth), path.Preferred ? Palette.Highlight : Palette.Text);
        PutRightAligned(canvas, columns.StateStart, columns.TimerStart - 1, row, presentation.StateText, presentation.StateColor);
        PutRightAligned(canvas, columns.TimerStart, columns.RightExclusive, row, presentation.TimerText, presentation.TimerColor);
    }

    private (int ContentX, int RightExclusive, int LabelWidth, int StateStart, int TimerStart) GetNetworkPathColumns()
    {
        var contentX = rightPaneX + 3;
        var rightExclusive = rightPaneX + paneWidth - 3;
        const int stateWidth = 14;
        const int timerWidth = 8;
        const int gap = 1;
        var available = Math.Max(1, rightExclusive - contentX);
        var labelWidth = Math.Max(7, available - stateWidth - timerWidth - (gap * 2));
        var stateStart = contentX + labelWidth + gap;
        var timerStart = rightExclusive - timerWidth;
        return (contentX, rightExclusive, labelWidth, stateStart, timerStart);
    }

    private static void PutCenteredInRange(Cell[,] canvas, int left, int rightExclusive, int row, string text, Palette color)
    {
        var width = Math.Max(1, rightExclusive - left);
        var value = Truncate(text, width);
        var x = left + Math.Max(0, (width - value.Length) / 2);
        Put(canvas, x, row, value, color);
    }

    internal static (string StateText, string TimerText, Palette StateColor, Palette TimerColor) FormatPathPaneStatus(
        InteractiveNetworkPathStatus path,
        DateTimeOffset? nowUtc = null)
    {
        if (string.Equals(path.LastResult, "step-one-sent", StringComparison.OrdinalIgnoreCase))
        {
            return ("авторизация...", "—", Palette.Highlight, Palette.Dim);
        }

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var timerText = FormatKnownAuthorizationWindow(path, now);
        var timerColor = timerText == "—" || path.Status != PathAuthorizationStatus.Internet
            ? Palette.Dim
            : Palette.Good;

        return path.Status switch
        {
            PathAuthorizationStatus.Internet => ("онлайн", timerText, Palette.Good, timerColor),
            // A live captive observation disproves the old predicted window.
            PathAuthorizationStatus.Captive => ("нужна авторизация", "—", Palette.Highlight, Palette.Dim),
            PathAuthorizationStatus.Unreachable => ("недоступен", timerText, Palette.Dim, Palette.Dim),
            PathAuthorizationStatus.Ambiguous => ("неясно", timerText, Palette.Highlight, Palette.Dim),
            PathAuthorizationStatus.Disconnected => ("отключён", timerText, Palette.Dim, Palette.Dim),
            _ => ("проверка...", timerText, Palette.Dim, Palette.Dim)
        };
    }

    private static string FormatKnownAuthorizationWindow(InteractiveNetworkPathStatus path, DateTimeOffset nowUtc)
    {
        if (path.ExpectedExpiryUtc is not { } expiry || expiry <= nowUtc)
        {
            return "—";
        }

        var totalMinutes = Math.Max(0, (int)Math.Floor((expiry - nowUtc).TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        var prefix = path.AuthorizationWindowApproximate ? "~" : string.Empty;
        return $"{prefix}{hours:00}:{minutes:00}";
    }

    private static (string Text, Palette Color) FormatNetworkStatus(InteractiveStatusSnapshot s)
    {
        if (s.ActivePhysicalPathCount == 0)
        {
            return ("нет активных ○", Palette.Dim);
        }
        if (s.CaptivePathCount > 0)
        {
            return ($"{s.ActivePhysicalPathCount}, captive {s.CaptivePathCount} ○", Palette.Highlight);
        }
        if (s.InternetPathCount > 0)
        {
            return ($"{s.ActivePhysicalPathCount}, Internet {s.InternetPathCount} ●", Palette.Good);
        }
        return ($"{s.ActivePhysicalPathCount}, проверяются ○", Palette.Dim);
    }

    private (string Text, Palette Color) FormatAuthorizationStatus(InteractiveStatusSnapshot s)
    {
        if (s.CaptivePathCount > 0)
        {
            return ($"нужна для {s.CaptivePathCount} пути(ей) ○", Palette.Highlight);
        }
        if (s.ActivePhysicalPathCount > 0 && s.InternetPathCount == s.ActivePhysicalPathCount)
        {
            if (s.AuthorizationExpectedExpiryUtc is null)
            {
                return ("все пути online ●", Palette.Good);
            }
        }
        if (s.AuthorizationExpectedExpiryUtc is { } expiry)
        {
            var now = DateTimeOffset.UtcNow;
            if (expiry > now)
            {
                var remaining = expiry - now;
                var totalMinutes = Math.Max(0, (int)Math.Floor(remaining.TotalMinutes));
                var hours = totalMinutes / 60;
                var minutes = totalMinutes % 60;
                var timer = $"{hours:00}:{minutes:00}";
                return paneWidth >= 44
                    ? ($"до следующей ~{timer} ●", Palette.Good)
                    : ($"до след. ~{timer} ●", Palette.Good);
            }
        }

        if (s.AuthorizationAlreadyActive)
        {
            return ("уже активна ●", Palette.Good);
        }

        if (s.AuthorizationExpectedExpiryUtc is not null)
        {
            return ("срок истёк ○", Palette.Dim);
        }

        return ("ещё не выполнялась ○", Palette.Dim);
    }

    private static string FormatInternet(bool? value) => value switch
    {
        true => "доступен ●",
        false => "нет ○",
        null => "проверка ○"
    };

    private void DrawInstallStatus(Cell[,] canvas, bool installed, bool registered, bool automatic, bool agent)
    {
        DrawBox(canvas, rightPaneX, PaneY, paneWidth, PaneHeight, "СОСТОЯНИЕ");
        DrawStatusLine(canvas, PaneY + 2, "Установка", installed ? "есть ●" : "нет ○", installed ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 3, "Регистрация", registered ? "есть ●" : "нет ○", registered ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 4, "Автовход", automatic ? "включён ●" : "выключен ○", automatic ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 5, "Фоновый режим", agent ? "работает ●" : "остановлен ○", agent ? Palette.Good : Palette.Dim);
        Put(canvas, rightPaneX + 3, PaneY + 8, "Версия", Palette.Dim);
        PutRightAligned(canvas, rightPaneX + 3, rightPaneX + paneWidth - 3, PaneY + 8, productVersion, Palette.Text);
    }

    private void DrawStatusLine(Cell[,] canvas, int row, string label, string value, Palette valueColor)
    {
        Put(canvas, rightPaneX + 3, row, label, Palette.Dim);
        var valueStart = rightPaneX + Math.Max(15, label.Length + 4);
        PutRightAligned(canvas, valueStart, rightPaneX + paneWidth - 3, row, value, valueColor);
    }

    private void DrawSelectable(Cell[,] canvas, int x, int y, char hotkey, string text, bool isSelected)
    {
        Put(canvas, x, y, isSelected ? "› " : "  ", isSelected ? Palette.Highlight : Palette.Text);
        Put(canvas, x + 2, y, $"[{hotkey}]", Palette.Dim);
        Put(canvas, x + 6, y, Truncate(text, Math.Max(1, paneWidth - 10)), isSelected ? Palette.Bright : Palette.Text);
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

    private Cell[,] CreateCanvas() => TerminalCanvas.Create(canvasWidth, CanvasHeight);

    private void Render(Cell[,] canvas) => terminalOutput.Render(canvas, renderLeft);

    private void UpdateLayout()
    {
        try
        {
            layout = TerminalLayout.Calculate(Console.WindowWidth, Console.WindowHeight);
        }
        catch
        {
            layout = TerminalLayout.Fallback;
        }
    }

    private void PrepareInteractiveConsole(bool clear = true)
    {
        try
        {
            Console.CursorVisible = false;
        }
        catch
        {
        }

        if (!clear)
        {
            return;
        }

        InvalidateRenderedFrame();
        Console.Clear();
    }

    private void InvalidateRenderedFrame() => terminalOutput.Invalidate();

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

    private enum MenuPage
    {
        Main,
        Settings,
        Maintenance,
        Updates
    }

    private readonly record struct ActionHistoryRow(
        InteractiveActionLineKind Kind,
        string Text,
        bool ShowSymbol);

    private enum BannerMode
    {
        Final,
        InitialSweep,
        AmbientSweep
    }
}
