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
            var page = 0; // 0 = measurement, 1 = leaderboard/profile
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateLayout();

                if (page == 0)
                {
                    RenderSpeedMeasurementFrame();
                }
                else
                {
                    RenderLeaderboardFrame();
                }

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

                if (key.Key == ConsoleKey.LeftArrow || key.KeyChar == '1')
                {
                    page = 0;
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (key.Key == ConsoleKey.RightArrow || key.KeyChar == '2')
                {
                    page = 1;
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (page == 0 && key.Key == ConsoleKey.Enter)
                {
                    await ShowSpeedToolsNoticeAsync(
                        "ИЗМЕРЕНИЕ СКОРОСТИ",
                        "Интерфейс измерителя готов. Сетевой замер и поток прогресса подключит отдельный backend-модуль.",
                        cancellationToken).ConfigureAwait(false);
                }
                else if (page == 1 && key.KeyChar == '3')
                {
                    var nickname = await PromptSpeedNicknameAsync(speedNicknameDraft, cancellationToken).ConfigureAwait(false);
                    if (nickname is not null)
                    {
                        speedNicknameDraft = nickname;
                    }
                }
                else if (page == 1 && key.KeyChar == '4')
                {
                    await ShowSpeedToolsNoticeAsync(
                        "ПУБЛИКАЦИЯ РЕЗУЛЬТАТА",
                        "Экран публикации готов. Сохранение результата и отправку в рейтинг подключит отдельный backend-модуль.",
                        cancellationToken).ConfigureAwait(false);
                }
                else if (page == 1 && (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.R))
                {
                    await ShowSpeedToolsNoticeAsync(
                        "ТАБЛИЦА ЛИДЕРОВ",
                        "Интерфейс таблицы готов. Получение и сортировку результатов кампуса подключит отдельный backend-модуль.",
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

    private void RenderSpeedMeasurementFrame()
    {
        var canvas = CreateSpeedToolsCanvas("СКОРОСТЬ · ЗАМЕР", out var contentX, out var contentY, out var contentWidth, out var contentHeight);
        DrawSpeedPageTabs(canvas, contentX, contentY, contentWidth, page: 0);

        var metricY = contentY + 3;
        DrawLargeSpeedMetric(canvas, contentX, metricY, contentWidth, "---", Palette.BrandBright);
        CenterWithin(canvas, contentX, contentWidth, metricY + 6, "Mbit/s", Palette.Dim);
        CenterWithin(canvas, contentX, contentWidth, metricY + 8, "↓ — Mbit/s     ↑ — Mbit/s     ping — ms", Palette.Text);

        var actionY = Math.Min(contentY + contentHeight - 5, metricY + 11);
        CenterWithin(canvas, contentX, contentWidth, actionY, "[Enter] Начать замер", Palette.Highlight);
        CenterWithin(canvas, contentX, contentWidth, actionY + 2, "[2] Рейтинг кампуса     [0] Назад", Palette.Dim);
        Center(canvas, CanvasHeight - 1, "← → / 1–2 страницы   Enter начать   Esc назад", Palette.Dim);
        Render(canvas);
    }

    private void RenderLeaderboardFrame()
    {
        var canvas = CreateSpeedToolsCanvas("СКОРОСТЬ · РЕЙТИНГ", out var contentX, out var contentY, out var contentWidth, out var contentHeight);
        DrawSpeedPageTabs(canvas, contentX, contentY, contentWidth, page: 1);

        var tableY = contentY + 3;
        if (contentWidth >= 68)
        {
            Put(canvas, contentX, tableY, Truncate("#   НИКНЕЙМ                    ↓ Mbit/s    ↑ Mbit/s    PING", contentWidth), Palette.Highlight);
            Put(canvas, contentX, tableY + 1, new string('─', Math.Min(contentWidth, 64)), Palette.Dim);
            var rows = Math.Min(8, Math.Max(4, contentHeight - 12));
            for (var row = 0; row < rows; row++)
            {
                Put(canvas, contentX, tableY + 2 + row,
                    Truncate($"{row + 1,-3} —                           —           —          —", contentWidth), Palette.Text);
            }
        }
        else
        {
            Put(canvas, contentX, tableY, Truncate("#   НИКНЕЙМ              ↓     ↑   PING", contentWidth), Palette.Highlight);
            var rows = Math.Min(6, Math.Max(4, contentHeight - 12));
            for (var row = 0; row < rows; row++)
            {
                Put(canvas, contentX, tableY + 2 + row,
                    Truncate($"{row + 1,-3} —                    —     —     —", contentWidth), Palette.Text);
            }
        }

        var profileY = contentY + contentHeight - 7;
        Put(canvas, contentX, profileY, Truncate($"Ваш никнейм: {speedNicknameDraft}", contentWidth), Palette.Bright);
        Put(canvas, contentX, profileY + 1, Truncate("Ваш результат: ещё не опубликован", contentWidth), Palette.Dim);
        Put(canvas, contentX, profileY + 3,
            Truncate("[Enter/R] Обновить   [3] Никнейм   [4] Опубликовать   [1] Замер   [0] Назад", contentWidth), Palette.Highlight);
        Center(canvas, CanvasHeight - 1, "← → / 1–2 страницы   3 никнейм   4 публикация   Esc назад", Palette.Dim);
        Render(canvas);
    }

    private void RenderSpeedNicknamePrompt(string value)
    {
        var canvas = CreateSpeedToolsCanvas("СКОРОСТЬ · НИКНЕЙМ", out var contentX, out var contentY, out var contentWidth, out _);
        PutWrapped(
            canvas,
            contentX,
            contentY + 2,
            contentWidth,
            "Никнейм будет виден в таблице лидеров только после добровольной публикации результата.",
            Palette.Text);
        CenterWithin(canvas, contentX, contentWidth, contentY + 7, "Введите никнейм:", Palette.Dim);
        CenterWithin(canvas, contentX, contentWidth, contentY + 9, value + "_", Palette.Bright);
        CenterWithin(canvas, contentX, contentWidth, contentY + 11, $"До {SpeedNicknameMaximumLength} символов", Palette.Dim);
        Center(canvas, CanvasHeight - 1, "Enter сохранить   Backspace удалить   Esc отмена", Palette.Dim);
        Render(canvas);
    }

    private void RenderSpeedNoticeFrame(string title, string message)
    {
        var canvas = CreateSpeedToolsCanvas(title, out var contentX, out var contentY, out var contentWidth, out _);
        PutWrapped(canvas, contentX, contentY + 4, contentWidth, message, Palette.Text);
        PutWrapped(
            canvas,
            contentX,
            contentY + 9,
            contentWidth,
            "UI-ветка не выполняет сетевых запросов и не публикует данные.",
            Palette.Dim);
        Center(canvas, CanvasHeight - 1, "Enter / Esc — вернуться", Palette.Dim);
        Render(canvas);
    }

    private Cell[,] CreateSpeedToolsCanvas(
        string title,
        out int contentX,
        out int contentY,
        out int contentWidth,
        out int contentHeight)
    {
        var canvas = CreateCanvas();
        var boxX = compactLayout ? 1 : 2;
        var boxY = 2;
        var boxWidth = Math.Max(20, canvasWidth - (boxX * 2));
        var boxHeight = Math.Max(16, CanvasHeight - 5);

        DrawBox(canvas, boxX, boxY, boxWidth, boxHeight, title);
        contentX = boxX + 3;
        contentY = boxY + 2;
        contentWidth = Math.Max(1, boxWidth - 6);
        contentHeight = Math.Max(1, boxHeight - 4);
        return canvas;
    }

    private static void DrawSpeedPageTabs(Cell[,] canvas, int x, int y, int width, int page)
    {
        const string measurement = "[1] ЗАМЕР";
        const string leaderboard = "[2] РЕЙТИНГ";
        var gap = width >= 50 ? 8 : 3;
        var total = measurement.Length + gap + leaderboard.Length;
        var left = x + Math.Max(0, (width - total) / 2);
        Put(canvas, left, y, measurement, page == 0 ? Palette.BrandBright : Palette.Dim);
        Put(canvas, left + measurement.Length + gap, y, leaderboard, page == 1 ? Palette.BrandBright : Palette.Dim);
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
            Console.WriteLine("[1] Измерить скорость");
            Console.WriteLine("[2] Таблица лидеров кампуса");
            Console.WriteLine("[3] Никнейм и публикация");
            Console.WriteLine("[0] Назад");
            Console.WriteLine();
            Console.WriteLine("UI готов; измерение и публикация будут подключены отдельным backend-модулем.");

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape || key.KeyChar == '0')
            {
                return;
            }

            if (key.KeyChar is '1' or '2' or '3')
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
        }
    }
}
