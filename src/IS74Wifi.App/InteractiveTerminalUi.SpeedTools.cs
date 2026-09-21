using System.Text;

namespace IS74Wifi.App;

// UI shell only. Networking, speed measurement, campus leaderboard storage and
// publication are intentionally left for the backend/business-logic integration.
internal sealed partial class InteractiveTerminalUi
{
    private const int SpeedNicknameMaximumLength = 18;

    private static readonly IReadOnlyDictionary<char, string[]> SpeedMetricGlyphs =
        new Dictionary<char, string[]>
        {
            ['0'] =
            [
                " ██████╗ ",
                "██╔═████╗",
                "██║██╔██║",
                "████╔╝██║",
                "╚██████╔╝",
                " ╚═════╝ "
            ],
            ['1'] =
            [
                " ██╗",
                "███║",
                "╚██║",
                " ██║",
                " ██║",
                " ╚═╝"
            ],
            ['2'] =
            [
                "██████╗ ",
                "╚════██╗",
                " █████╔╝",
                "██╔═══╝ ",
                "███████╗",
                "╚══════╝"
            ],
            ['3'] =
            [
                "██████╗ ",
                "╚════██╗",
                " █████╔╝",
                " ╚═══██╗",
                "██████╔╝",
                "╚═════╝ "
            ],
            ['4'] =
            [
                "██╗  ██╗",
                "██║  ██║",
                "███████║",
                "╚════██║",
                "     ██║",
                "     ╚═╝"
            ],
            ['5'] =
            [
                "███████╗",
                "██╔════╝",
                "███████╗",
                "╚════██║",
                "███████║",
                "╚══════╝"
            ],
            ['6'] =
            [
                " ██████╗",
                "██╔════╝",
                "███████╗",
                "██╔═══██╗",
                "╚██████╔╝",
                " ╚═════╝ "
            ],
            ['7'] =
            [
                "███████╗",
                "╚════██║",
                "    ██╔╝",
                "   ██╔╝ ",
                "   ██║  ",
                "   ╚═╝  "
            ],
            ['8'] =
            [
                " █████╗ ",
                "██╔══██╗",
                "╚█████╔╝",
                "██╔══██╗",
                "╚█████╔╝",
                " ╚════╝ "
            ],
            ['9'] =
            [
                " █████╗ ",
                "██╔══██╗",
                "╚██████║",
                " ╚═══██║",
                " █████╔╝",
                " ╚════╝ "
            ],
            ['.'] =
            [
                "   ",
                "   ",
                "   ",
                "   ",
                "██╗",
                "╚═╝"
            ],
            ['-'] =
            [
                "      ",
                "      ",
                "█████╗",
                "╚════╝",
                "      ",
                "      "
            ]
        };

    private string speedNicknameDraft = "Гость";

    public async Task RunSpeedToolsAsync(
        InteractiveStatusSnapshot currentStatus,
        CancellationToken cancellationToken = default)
    {
        status = currentStatus;
        var mainMenuSelection = selected;

        if (!CanUseInteractiveSession)
        {
            RunSpeedToolsCompact();
            selected = mainMenuSelection;
            return;
        }

        PrepareInteractiveConsole(clear: false);
        try
        {
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateLayout();
                RenderSpeedDashboardFrame();

                var completed = await Task.WhenAny(keyTask, Task.Delay(16, cancellationToken)).ConfigureAwait(false);
                if (completed != keyTask)
                {
                    continue;
                }

                var key = await keyTask.ConfigureAwait(false);
                if (key.Key == ConsoleKey.Escape || key.KeyChar == '0')
                {
                    return;
                }

                if (key.Key == ConsoleKey.Enter || key.KeyChar == '1')
                {
                    await ShowSpeedToolsNoticeAsync(
                        "ИЗМЕРЕНИЕ СКОРОСТИ",
                        "Интерфейс измерителя готов. Download, upload, ping, jitter и packet loss подключит отдельный backend-модуль.",
                        cancellationToken).ConfigureAwait(false);
                }
                else if (key.KeyChar == '2' || key.Key == ConsoleKey.R)
                {
                    await ShowSpeedToolsNoticeAsync(
                        "ТАБЛИЦА ЛИДЕРОВ",
                        "Интерфейс таблицы готов. Получение и сортировку результатов кампуса подключит отдельный backend-модуль.",
                        cancellationToken).ConfigureAwait(false);
                }
                else if (key.KeyChar == '3')
                {
                    var nickname = await PromptSpeedNicknameAsync(speedNicknameDraft, cancellationToken).ConfigureAwait(false);
                    if (nickname is not null)
                    {
                        speedNicknameDraft = nickname;
                    }
                }
                else if (key.KeyChar == '4')
                {
                    await ShowSpeedToolsNoticeAsync(
                        "ПУБЛИКАЦИЯ РЕЗУЛЬТАТА",
                        "Экран публикации готов. Сохранение результата и отправку в рейтинг подключит отдельный backend-модуль.",
                        cancellationToken).ConfigureAwait(false);
                }

                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            selected = mainMenuSelection;
            RestoreConsole();
        }
    }

    private async Task<string?> PromptSpeedNicknameAsync(string currentValue, CancellationToken cancellationToken)
    {
        var value = new StringBuilder(currentValue);
        var keyTask = ReadKeyAsync();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateLayout();
            RenderSpeedNicknamePrompt(value.ToString());

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
                if (value.Length > 0)
                {
                    value.Length--;
                }
                keyTask = ReadKeyAsync();
                continue;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                var nickname = value.ToString().Trim();
                if (nickname.Length > 0)
                {
                    return nickname;
                }
                keyTask = ReadKeyAsync();
                continue;
            }

            if (!char.IsControl(key.KeyChar) && value.Length < SpeedNicknameMaximumLength)
            {
                value.Append(key.KeyChar);
            }
            keyTask = ReadKeyAsync();
        }
    }

    private async Task ShowSpeedToolsNoticeAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var keyTask = ReadKeyAsync();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateLayout();
            RenderSpeedNoticeFrame(title, message);

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

    private void RenderSpeedDashboardFrame()
    {
        var canvas = CreateSpeedToolsDashboardCanvas(
            "ИЗМЕРЕНИЕ СКОРОСТИ",
            out var leftContentX,
            out var leftContentY,
            out var leftContentWidth);

        DrawSpeedMeasurementPane(canvas, leftContentX, leftContentY, leftContentWidth);
        DrawSpeedLeaderboardPane(canvas);
        Center(canvas, CanvasHeight - 1, "Enter/1 замер   2/R обновить рейтинг   3 ник   4 публикация   Esc назад", Palette.Dim);
        Render(canvas);
    }

    private void RenderSpeedNicknamePrompt(string value)
    {
        var canvas = CreateSpeedToolsDashboardCanvas(
            "НИКНЕЙМ",
            out var contentX,
            out var contentY,
            out var contentWidth);

        PutWrapped(
            canvas,
            contentX,
            contentY,
            contentWidth,
            "Никнейм будет виден в таблице лидеров только после добровольной публикации результата.",
            Palette.Text);
        Put(canvas, contentX, contentY + 4, "Введите никнейм:", Palette.Dim);
        Put(canvas, contentX, contentY + 6, Truncate(value + "_", contentWidth), Palette.Bright);
        Put(canvas, contentX, contentY + 8, $"До {SpeedNicknameMaximumLength} символов", Palette.Dim);

        DrawSpeedLeaderboardPane(canvas);
        Center(canvas, CanvasHeight - 1, "Enter сохранить   Backspace удалить   Esc отмена", Palette.Dim);
        Render(canvas);
    }

    private void RenderSpeedNoticeFrame(string title, string message)
    {
        var canvas = CreateSpeedToolsDashboardCanvas(
            title,
            out var contentX,
            out var contentY,
            out var contentWidth);

        PutWrapped(canvas, contentX, contentY, contentWidth, message, Palette.Text);
        PutWrapped(
            canvas,
            contentX,
            contentY + 6,
            contentWidth,
            "UI-ветка не выполняет сетевых запросов и не публикует данные.",
            Palette.Dim);

        DrawSpeedLeaderboardPane(canvas);
        Center(canvas, CanvasHeight - 1, "Enter / Esc — вернуться", Palette.Dim);
        Render(canvas);
    }

    private Cell[,] CreateSpeedToolsDashboardCanvas(
        string leftTitle,
        out int leftContentX,
        out int leftContentY,
        out int leftContentWidth)
    {
        var canvas = CreateCanvas();
        var head = GetAmbientSweepHead();

        if (compactLayout)
        {
            Center(canvas, 1, "IS74W · Скорость и рейтинг", Palette.BrandBright);
            var boxY = 4;
            var boxHeight = Math.Min(22, CanvasHeight - boxY - 2);
            DrawBox(canvas, 1, boxY, Math.Max(20, canvasWidth - 2), boxHeight, leftTitle);
            leftContentX = 4;
            leftContentY = boxY + 2;
            leftContentWidth = Math.Max(1, canvasWidth - 8);
            return canvas;
        }

        DrawBanner(canvas, 0, head is null ? BannerMode.Final : BannerMode.AmbientSweep, head ?? 0);
        Center(canvas, 13, Subtitle, Palette.Dim);
        DrawBox(canvas, leftPaneX, PaneY, paneWidth, PaneHeight, leftTitle);
        DrawBox(canvas, rightPaneX, PaneY, paneWidth, PaneHeight, "ЛИДЕРЫ КАМПУСА");

        leftContentX = leftPaneX + 3;
        leftContentY = PaneY + 2;
        leftContentWidth = Math.Max(1, paneWidth - 6);
        return canvas;
    }

    private static void DrawSpeedMeasurementPane(Cell[,] canvas, int x, int y, int width)
    {
        DrawLargeSpeedMetric(canvas, x, y, width, "---", Palette.BrandBright);
        CenterWithin(canvas, x, width, y + 6, "DOWNLOAD · Mbit/s", Palette.Dim);

        Put(canvas, x, y + 7, Truncate("↑ Upload     — Mbit/s", width), Palette.Text);
        Put(canvas, x, y + 8, Truncate("Ping         — ms", width), Palette.Text);
        Put(canvas, x, y + 9, Truncate("Jitter       — ms", width), Palette.Text);
        Put(canvas, x, y + 10, Truncate("Packet loss  — %", width), Palette.Text);
    }

    private void DrawSpeedLeaderboardPane(Cell[,] canvas)
    {
        if (compactLayout)
        {
            return;
        }

        var x = rightPaneX + 3;
        var y = PaneY + 2;
        var width = Math.Max(1, paneWidth - 6);

        if (width >= 43)
        {
            Put(canvas, x, y, Truncate("#  НИК            ↓      ↑   PING  JIT", width), Palette.Highlight);
            Put(canvas, x, y + 1, new string('─', Math.Min(width, 41)), Palette.Dim);
            for (var row = 0; row < 5; row++)
            {
                Put(canvas, x, y + 2 + row,
                    Truncate($"{row + 1,-2} —              —      —      —    —", width), Palette.Text);
            }
        }
        else
        {
            Put(canvas, x, y, Truncate("# НИК          ↓   PING  JIT", width), Palette.Highlight);
            Put(canvas, x, y + 1, new string('─', Math.Min(width, 29)), Palette.Dim);
            for (var row = 0; row < 5; row++)
            {
                Put(canvas, x, y + 2 + row,
                    Truncate($"{row + 1,-2} —            —     —    —", width), Palette.Text);
            }
        }

        Put(canvas, x, y + 8, Truncate($"Ник: {speedNicknameDraft}", width), Palette.Bright);
        Put(canvas, x, y + 9, Truncate("Ваш результат: ещё не опубликован", width), Palette.Dim);
        Put(canvas, x, y + 10, Truncate("[2] Обновить  [3] Ник  [4] Опубликовать", width), Palette.Highlight);
    }

    private static void DrawLargeSpeedMetric(
        Cell[,] canvas,
        int contentX,
        int contentY,
        int contentWidth,
        string value,
        Palette color)
    {
        var glyphs = value
            .Where(SpeedMetricGlyphs.ContainsKey)
            .Select(ch => SpeedMetricGlyphs[ch])
            .ToArray();
        if (glyphs.Length == 0)
        {
            CenterWithin(canvas, contentX, contentWidth, contentY + 2, value, color);
            return;
        }

        var totalWidth = glyphs.Sum(glyph => glyph.Max(row => row.Length)) + Math.Max(0, glyphs.Length - 1);
        if (totalWidth > contentWidth)
        {
            CenterWithin(canvas, contentX, contentWidth, contentY + 2, value, color);
            return;
        }

        var left = contentX + Math.Max(0, (contentWidth - totalWidth) / 2);
        for (var row = 0; row < 6; row++)
        {
            var x = left;
            foreach (var glyph in glyphs)
            {
                var glyphWidth = glyph.Max(line => line.Length);
                Put(canvas, x, contentY + row, glyph[row], color);
                x += glyphWidth + 1;
            }
        }
    }

    private static void CenterWithin(Cell[,] canvas, int x, int width, int row, string text, Palette color)
    {
        var clipped = Truncate(text, Math.Max(1, width));
        Put(canvas, x + Math.Max(0, (width - clipped.Length) / 2), row, clipped, color);
    }

    private void RunSpeedToolsCompact()
    {
        while (true)
        {
            Console.Clear();
            Console.WriteLine("IS74W — Скорость и рейтинг кампуса");
            Console.WriteLine();
            Console.WriteLine("ЗАМЕР");
            Console.WriteLine("  Download   — Mbit/s");
            Console.WriteLine("  Upload     — Mbit/s");
            Console.WriteLine("  Ping       — ms");
            Console.WriteLine("  Jitter     — ms");
            Console.WriteLine("  Loss       — %");
            Console.WriteLine();
            Console.WriteLine("ЛИДЕРЫ КАМПУСА");
            Console.WriteLine("  #  Ник          ↓   Ping  Jit");
            Console.WriteLine("  1  —            —     —    —");
            Console.WriteLine("  2  —            —     —    —");
            Console.WriteLine("  3  —            —     —    —");
            Console.WriteLine();
            Console.WriteLine($"Ник: {speedNicknameDraft}");
            Console.WriteLine();
            Console.WriteLine("[1] Начать замер   [2] Обновить рейтинг");
            Console.WriteLine("[3] Никнейм        [4] Опубликовать");
            Console.WriteLine("[0] Назад");
            Console.WriteLine();
            Console.WriteLine("UI готов; измерение и публикация будут подключены отдельным backend-модулем.");

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape || key.KeyChar == '0')
            {
                return;
            }

            if (key.KeyChar is '1' or '2' or '4' || key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.R)
            {
                Console.Clear();
                Console.WriteLine("Интерфейс готов. Бизнес-логика пока не подключена.");
                Console.WriteLine();
                Console.WriteLine("Enter / Esc — назад");
                while (true)
                {
                    var dismiss = Console.ReadKey(intercept: true);
                    if (dismiss.Key is ConsoleKey.Enter or ConsoleKey.Escape)
                    {
                        break;
                    }
                }
            }
            else if (key.KeyChar == '3')
            {
                Console.Clear();
                Console.Write("Никнейм: " );
                var nickname = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(nickname))
                {
                    speedNicknameDraft = nickname[..Math.Min(nickname.Length, SpeedNicknameMaximumLength)];
                }
            }
        }
    }

}
