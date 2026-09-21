using System.Diagnostics;
using System.Text;

namespace IS74Wifi.App;

internal sealed class InteractiveTerminalUi
{
    private const int CanvasWidth = 79;
    private const int CanvasHeight = 30;
    private const int LeftPaneX = 1;
    private const int RightPaneX = 42;
    private const int PaneY = 14;
    private const int PaneWidth = 37;
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
    private DateTimeOffset nextAmbientSweepUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(14);
    private DateTimeOffset? ambientSweepStartedUtc;

    public InteractiveTerminalUi(string productVersion)
    {
        this.productVersion = productVersion;
        ansi = ConsoleSession.SupportsVirtualTerminal;
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
                return Console.WindowWidth >= CanvasWidth && Console.WindowHeight >= CanvasHeight;
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

        if (!CanUseRichLayout)
        {
            return RunCompactMenu(initialStatus);
        }

        PrepareInteractiveConsole();
        try
        {
            if (showReveal)
            {
                await PlayInitialRevealAsync(cancellationToken).ConfigureAwait(false);
            }

            nextAmbientSweepUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Random.Shared.Next(12, 19));
            ambientSweepStartedUtc = null;

            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!CanUseRichLayout)
                {
                    RestoreConsole();
                    return RunCompactMenu(status ?? initialStatus);
                }

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
        if (!CanUseRichLayout)
        {
            Console.Clear();
            Console.WriteLine($"=== {title} ===");
            Console.WriteLine(message);
            return;
        }

        PrepareInteractiveConsole();
        status = currentStatus;
        var canvas = CreateCanvas();
        DrawBanner(canvas, 0, BannerMode.Final, 0);
        Center(canvas, 13, Subtitle, Palette.Dim);
        DrawBox(canvas, LeftPaneX, PaneY, PaneWidth, PaneHeight, title);
        PutWrapped(canvas, LeftPaneX + 3, PaneY + 3, PaneWidth - 6, message, Palette.Bright);
        DrawStatusPane(canvas);
        Center(canvas, 29, "Пожалуйста, подождите...", Palette.Dim);
        Render(canvas);
        RestoreConsole(showCursor: false);
    }

    public void PrepareForAction(string title)
    {
        RestoreConsole();
        Console.Clear();
        Console.WriteLine($"IS74W · {title}");
        Console.WriteLine(new string('─', Math.Min(60, Math.Max(10, title.Length + 10))));
        Console.WriteLine();
    }

    public void PauseAfterAction()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        Console.WriteLine();
        Console.Write("Нажмите любую клавишу, чтобы вернуться в меню...");
        _ = Console.ReadKey(intercept: true);
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
                Console.WriteLine($"{(i == index ? '›' : ' ')} {items[i].Label}");
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
            else if (key.Key == ConsoleKey.Escape)
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
        Console.WriteLine("Enter — установить / обновить");
        Console.WriteLine("Esc   — выйти");

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return true;
            if (key.Key == ConsoleKey.Escape) return false;
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

    private void RenderInteractiveFrame()
    {
        var canvas = CreateCanvas();
        var head = GetAmbientSweepHead();
        DrawBanner(canvas, 0, head is null ? BannerMode.Final : BannerMode.AmbientSweep, head ?? 0);
        Center(canvas, 13, Subtitle, Palette.Dim);
        DrawLeftPane(canvas);
        DrawStatusPane(canvas);
        Center(canvas, 29, "↑ ↓ выбрать   Enter открыть   Esc назад   R reveal", Palette.Dim);
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

        DrawBox(canvas, LeftPaneX, PaneY, PaneWidth, PaneHeight, upgrade ? "ОБНОВЛЕНИЕ" : "ПЕРВЫЙ ЗАПУСК");
        if (upgrade)
        {
            Put(canvas, LeftPaneX + 3, PaneY + 2, "Установленная версия", Palette.Dim);
            Put(canvas, LeftPaneX + 3, PaneY + 3, Truncate(installedVersion ?? "неизвестно", 29), Palette.Text);
            Put(canvas, LeftPaneX + 3, PaneY + 5, "Запущенная версия", Palette.Dim);
            Put(canvas, LeftPaneX + 3, PaneY + 6, Truncate(productVersion, 29), Palette.Text);
        }
        else
        {
            PutWrapped(canvas, LeftPaneX + 3, PaneY + 2, 29,
                "IS74W необходимо установить для дальнейшей работы.", Palette.Text);
            Put(canvas, LeftPaneX + 3, PaneY + 6, "Без прав администратора", Palette.Dim);
            Put(canvas, LeftPaneX + 3, PaneY + 7, Truncate(installDirectory, 29), Palette.Dim);
        }

        var installLabel = upgrade ? "Обновить IS74W" : "Установить IS74W";
        DrawSelectable(canvas, LeftPaneX + 3, PaneY + 10, installLabel, selected == 0);
        DrawSelectable(canvas, LeftPaneX + 3, PaneY + 11, "Выход", selected == 1);

        DrawInstallStatus(canvas, installed: upgrade, registered: false, automatic: false, agent: false);
        Center(canvas, 29, "↑ ↓ выбрать   Enter продолжить   Esc выйти   R reveal", Palette.Dim);
        Render(canvas);
    }

    private void DrawLeftPane(Cell[,] canvas)
    {
        DrawBox(canvas, LeftPaneX, PaneY, PaneWidth, PaneHeight, "МЕНЮ");
        DrawCurrentItems(canvas, PaneY + 2);
    }

    private void DrawCurrentItems(Cell[,] canvas, int firstRow)
    {
        var items = GetCurrentItems();
        for (var i = 0; i < items.Count; i++)
        {
            DrawSelectable(canvas, LeftPaneX + 3, firstRow + i, items[i].Label, i == selected);
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
            new MenuItem("Авторизовать Wi-Fi сейчас", InteractiveMenuAction.Connect),
            new MenuItem(
                automaticEnabled ? "Отключить автоавторизацию" : "Включить автоавторизацию",
                automaticEnabled ? InteractiveMenuAction.DisableAutomaticAuthorization : InteractiveMenuAction.EnableAutomaticAuthorization),
            new MenuItem(
                registered ? "Сбросить регистрацию" : "Зарегистрировать устройство",
                registered ? InteractiveMenuAction.ResetRegistration : InteractiveMenuAction.Register),
            new MenuItem("Подробное состояние", InteractiveMenuAction.ShowDetailedStatus),
            new MenuItem("Открыть диагностические логи", InteractiveMenuAction.OpenLogs),
            new MenuItem("Проверить обновления", InteractiveMenuAction.Update),
            new MenuItem("Удалить программу и данные", InteractiveMenuAction.Uninstall),
            new MenuItem("Выход", InteractiveMenuAction.Exit)
        ];
    }

    private void DrawStatusPane(Cell[,] canvas)
    {
        DrawBox(canvas, RightPaneX, PaneY, PaneWidth, PaneHeight, "СОСТОЯНИЕ");
        var s = status;
        if (s is null)
        {
            Put(canvas, RightPaneX + 3, PaneY + 3, "Загрузка состояния...", Palette.Dim);
            return;
        }

        DrawStatusLine(canvas, PaneY + 2, "Интернет", FormatInternet(s.InternetAvailable),
            s.InternetAvailable == true ? Palette.Good : s.InternetAvailable == false ? Palette.Dim : Palette.Highlight);
        DrawStatusLine(canvas, PaneY + 3, "Авторизация", s.WifiAuthorizationActive ? "● активна" : "○ нет",
            s.WifiAuthorizationActive ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 4, "Автовход", s.AutomaticAuthorizationEnabled ? "● включён" : "○ выключен",
            s.AutomaticAuthorizationEnabled ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 5, "Агент", s.AgentRunning ? "● работает" : "○ остановлен",
            s.AgentRunning ? Palette.Good : Palette.Dim);

        Put(canvas, RightPaneX + 3, PaneY + 7, "Телефон", Palette.Dim);
        Put(canvas, RightPaneX + 17, PaneY + 7, Truncate(s.MaskedPhone, 16), Palette.Text);
        Put(canvas, RightPaneX + 3, PaneY + 8, "API-сессия", Palette.Dim);
        Put(canvas, RightPaneX + 14, PaneY + 8, Truncate(s.ApiSessionEnd, 19), Palette.Text);

        Put(canvas, RightPaneX + 3, PaneY + 10, "Результат", Palette.Dim);
        Put(canvas, RightPaneX + 14, PaneY + 10, Truncate(s.LastResult, 19), Palette.Text);
        Put(canvas, RightPaneX + 3, PaneY + 11, "Версия", Palette.Dim);
        Put(canvas, RightPaneX + 17, PaneY + 11, Truncate(s.Version, 16), Palette.Text);
    }

    private static string FormatInternet(bool? value) => value switch
    {
        true => "● доступен",
        false => "○ нет",
        null => "◌ проверка"
    };

    private void DrawInstallStatus(Cell[,] canvas, bool installed, bool registered, bool automatic, bool agent)
    {
        DrawBox(canvas, RightPaneX, PaneY, PaneWidth, PaneHeight, "СОСТОЯНИЕ");
        DrawStatusLine(canvas, PaneY + 2, "Установка", installed ? "● есть" : "○ нет", installed ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 3, "Регистрация", registered ? "● есть" : "○ нет", registered ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 4, "Автовход", automatic ? "● включён" : "○ выключен", automatic ? Palette.Good : Palette.Dim);
        DrawStatusLine(canvas, PaneY + 5, "Агент", agent ? "● работает" : "○ остановлен", agent ? Palette.Good : Palette.Dim);
        Put(canvas, RightPaneX + 3, PaneY + 8, "Версия", Palette.Dim);
        Put(canvas, RightPaneX + 17, PaneY + 8, Truncate(productVersion, 16), Palette.Text);
    }

    private static void DrawStatusLine(Cell[,] canvas, int row, string label, string value, Palette valueColor)
    {
        Put(canvas, RightPaneX + 3, row, label, Palette.Dim);
        var x = RightPaneX + PaneWidth - 3 - value.Length;
        Put(canvas, Math.Max(RightPaneX + 15, x), row, value, valueColor);
    }

    private static void DrawSelectable(Cell[,] canvas, int x, int y, string text, bool isSelected)
    {
        Put(canvas, x, y, isSelected ? "› " : "  ", isSelected ? Palette.Highlight : Palette.Text);
        Put(canvas, x + 2, y, Truncate(text, 29), isSelected ? Palette.Bright : Palette.Text);
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
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var row = y;
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                Put(canvas, x, row++, line.ToString(), color);
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0)
        {
            Put(canvas, x, row, line.ToString(), color);
        }
    }

    private static int BannerWidth => Banner.Max(line => line.Length);

    private static void DrawBanner(Cell[,] canvas, int top, BannerMode mode, double head)
    {
        var left = Math.Max(0, (CanvasWidth - BannerWidth) / 2);
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
                        < 0.8 => Palette.Highlight,
                        < 2.5 => Palette.BrandBright,
                        < 5 => Palette.Brand,
                        _ => Palette.Brand
                    };
                }

                Put(canvas, left + col, top + row, ch.ToString(), color);
            }
        }
    }

    private static Cell[,] CreateCanvas()
    {
        var canvas = new Cell[CanvasHeight, CanvasWidth];
        for (var y = 0; y < CanvasHeight; y++)
        {
            for (var x = 0; x < CanvasWidth; x++)
            {
                canvas[y, x] = new Cell(' ', Palette.Text);
            }
        }
        return canvas;
    }

    private static void Center(Cell[,] canvas, int row, string text, Palette color)
    {
        Put(canvas, Math.Max(0, (CanvasWidth - text.Length) / 2), row, text, color);
    }

    private static void Put(Cell[,] canvas, int x, int y, string text, Palette color)
    {
        if (y < 0 || y >= CanvasHeight) return;
        for (var i = 0; i < text.Length; i++)
        {
            var targetX = x + i;
            if (targetX < 0 || targetX >= CanvasWidth) continue;
            canvas[y, targetX] = new Cell(text[i], color);
        }
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

    private static void RenderAnsi(Cell[,] canvas)
    {
        var output = new StringBuilder(CanvasHeight * (CanvasWidth + 32));
        output.Append("\u001b[H");
        Palette? active = null;
        for (var y = 0; y < CanvasHeight; y++)
        {
            for (var x = 0; x < CanvasWidth; x++)
            {
                var cell = canvas[y, x];
                if (active != cell.Color)
                {
                    output.Append(ToAnsi(cell.Color));
                    active = cell.Color;
                }
                output.Append(cell.Character);
            }
            if (y < CanvasHeight - 1) output.Append('\n');
        }
        output.Append("\u001b[0m");
        Console.Write(output.ToString());
    }

    private static void RenderConsoleColors(Cell[,] canvas)
    {
        try
        {
            Console.SetCursorPosition(0, 0);
        }
        catch
        {
            Console.Clear();
        }

        Palette? active = null;
        for (var y = 0; y < CanvasHeight; y++)
        {
            for (var x = 0; x < CanvasWidth; x++)
            {
                var cell = canvas[y, x];
                if (active != cell.Color)
                {
                    Console.ForegroundColor = ToConsoleColor(cell.Color);
                    active = cell.Color;
                }
                Console.Write(cell.Character);
            }
            if (y < CanvasHeight - 1) Console.WriteLine();
        }
        Console.ResetColor();
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

    private static void PrepareInteractiveConsole()
    {
        try
        {
            Console.CursorVisible = false;
        }
        catch
        {
        }
        Console.Clear();
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
        string Label,
        InteractiveMenuAction Action);

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
