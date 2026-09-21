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
            var submenuSelection = 0;
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateLayout();
                UpdateAmbientSweepState();
                RenderSpeedHubFrame(submenuSelection);

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

                if (key.Key == ConsoleKey.UpArrow)
                {
                    submenuSelection = (submenuSelection - 1 + 4) % 4;
                    keyTask = ReadKeyAsync();
                    continue;
                }

                if (key.Key == ConsoleKey.DownArrow)
                {
                    submenuSelection = (submenuSelection + 1) % 4;
                    keyTask = ReadKeyAsync();
                    continue;
                }

                var direct = key.KeyChar switch
                {
                    '1' => 0,
                    '2' => 1,
                    '3' => 2,
                    '0' => 3,
                    _ => -1
                };
                if (direct >= 0)
                {
                    submenuSelection = direct;
                }
                else if (key.Key != ConsoleKey.Enter)
                {
                    keyTask = ReadKeyAsync();
                    continue;
                }

                switch (submenuSelection)
                {
                    case 0:
                        await RunSpeedMeasurementShellAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case 1:
                        await RunLeaderboardShellAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case 2:
                        await RunSpeedPublicationShellAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        return;
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

    private async Task RunSpeedMeasurementShellAsync(CancellationToken cancellationToken)
    {
        var localSelection = 0;
        var keyTask = ReadKeyAsync();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateLayout();
            UpdateAmbientSweepState();
            RenderSpeedMeasurementFrame(localSelection);

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

            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
            {
                localSelection = localSelection == 0 ? 1 : 0;
                keyTask = ReadKeyAsync();
                continue;
            }

            if (key.KeyChar == '1')
            {
                localSelection = 0;
            }
            else if (key.KeyChar == '0')
            {
                return;
            }
            else if (key.Key != ConsoleKey.Enter)
            {
                keyTask = ReadKeyAsync();
                continue;
            }

            if (localSelection == 1)
            {
                return;
            }

            await ShowSpeedToolsNoticeAsync(
                "ИЗМЕРЕНИЕ СКОРОСТИ",
                "Интерфейс измерителя готов. Сетевой замер и поток прогресса подключит отдельный backend-модуль.",
                cancellationToken).ConfigureAwait(false);
            keyTask = ReadKeyAsync();
        }
    }

    private async Task RunLeaderboardShellAsync(CancellationToken cancellationToken)
    {
        var selection = 0;
        var keyTask = ReadKeyAsync();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateLayout();
            UpdateAmbientSweepState();
            RenderLeaderboardFrame(selection);

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

            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
            {
                selection = selection == 0 ? 1 : 0;
                keyTask = ReadKeyAsync();
                continue;
            }

            if (key.KeyChar == '1')
            {
                selection = 0;
            }
            else if (key.KeyChar == '0')
            {
                return;
            }
            else if (key.Key != ConsoleKey.Enter)
            {
                keyTask = ReadKeyAsync();
                continue;
            }

            if (selection == 1)
            {
                return;
            }

            await ShowSpeedToolsNoticeAsync(
                "ТАБЛИЦА ЛИДЕРОВ",
                "Интерфейс таблицы готов. Получение и сортировку результатов кампуса подключит отдельный backend-модуль.",
                cancellationToken).ConfigureAwait(false);
            keyTask = ReadKeyAsync();
        }
    }

    private async Task RunSpeedPublicationShellAsync(CancellationToken cancellationToken)
    {
        var localSelection = 0;
        var keyTask = ReadKeyAsync();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateLayout();
            UpdateAmbientSweepState();
            RenderSpeedPublicationFrame(localSelection);

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

            if (key.Key == ConsoleKey.UpArrow)
            {
                localSelection = (localSelection - 1 + 3) % 3;
                keyTask = ReadKeyAsync();
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                localSelection = (localSelection + 1) % 3;
                keyTask = ReadKeyAsync();
                continue;
            }

            var direct = key.KeyChar switch
            {
                '1' => 0,
                '2' => 1,
                '0' => 2,
                _ => -1
            };
            if (direct >= 0)
            {
                localSelection = direct;
            }
            else if (key.Key != ConsoleKey.Enter)
            {
                keyTask = ReadKeyAsync();
                continue;
            }

            if (localSelection == 0)
            {
                var nickname = await PromptSpeedNicknameAsync(speedNicknameDraft, cancellationToken).ConfigureAwait(false);
                if (nickname is not null)
                {
                    speedNicknameDraft = nickname;
                }
            }
            else if (localSelection == 1)
            {
                await ShowSpeedToolsNoticeAsync(
                    "ПУБЛИКАЦИЯ РЕЗУЛЬТАТА",
                    "Экран публикации готов. Сохранение результата и отправку в рейтинг подключит отдельный backend-модуль.",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return;
            }

            keyTask = ReadKeyAsync();
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
            UpdateAmbientSweepState();
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
            UpdateAmbientSweepState();
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

    private void RenderSpeedHubFrame(int localSelection)
    {
        var canvas = CreateActionCanvas("СКОРОСТЬ · CAMPUS", out var contentX, out var contentY, out var contentWidth);
        Put(canvas, contentX, contentY, Truncate("Замер скорости и рейтинг кампуса", contentWidth), Palette.Dim);
        DrawSelectable(canvas, contentX, contentY + 2, '1', "Измерить скорость", localSelection == 0);
        DrawSelectable(canvas, contentX, contentY + 3, '2', "Таблица лидеров кампуса", localSelection == 1);
        DrawSelectable(canvas, contentX, contentY + 4, '3', "Никнейм и публикация", localSelection == 2);
        DrawSelectable(canvas, contentX, contentY + 5, '0', "Назад", localSelection == 3);
        PutWrapped(
            canvas,
            contentX,
            contentY + 8,
            contentWidth,
            "Публикация результата добровольная: сначала можно измерить скорость без никнейма.",
            Palette.Dim);
        Center(canvas, CanvasHeight - 1, "↑ ↓ выбрать   Enter открыть   1–3/0 сразу   Esc назад", Palette.Dim);
        Render(canvas);
    }

    private void RenderSpeedMeasurementFrame(int localSelection)
    {
        var canvas = CreateActionCanvas("ИЗМЕРЕНИЕ СКОРОСТИ", out var contentX, out var contentY, out var contentWidth);
        DrawLargeSpeedMetric(canvas, contentX, contentY, contentWidth, "---", Palette.BrandBright);
        CenterWithin(canvas, contentX, contentWidth, contentY + 6, "Mbit/s", Palette.Dim);
        CenterWithin(canvas, contentX, contentWidth, contentY + 7, "↓ —    ↑ —    ping — ms", Palette.Text);
        DrawSelectable(canvas, contentX, contentY + 8, '1', "Начать замер", localSelection == 0);
        DrawSelectable(canvas, contentX, contentY + 9, '0', "Назад", localSelection == 1);
        Center(canvas, CanvasHeight - 1, "↑ ↓ выбрать   Enter продолжить   1/0 сразу   Esc назад", Palette.Dim);
        Render(canvas);
    }

    private void RenderLeaderboardFrame(int localSelection)
    {
        var canvas = CreateActionCanvas("ТАБЛИЦА ЛИДЕРОВ", out var contentX, out var contentY, out var contentWidth);
        Put(canvas, contentX, contentY, Truncate("Кампус · лучшие опубликованные замеры", contentWidth), Palette.Dim);

        if (contentWidth >= 40)
        {
            Put(canvas, contentX, contentY + 2, Truncate("#  НИК              ↓      ↑    PING", contentWidth), Palette.Highlight);
            for (var row = 0; row < 4; row++)
            {
                Put(canvas, contentX, contentY + 3 + row, Truncate($"{row + 1,-2} —                —      —      —", contentWidth), Palette.Text);
            }
        }
        else
        {
            Put(canvas, contentX, contentY + 2, Truncate("#  НИКНЕЙМ       РЕЗУЛЬТАТ", contentWidth), Palette.Highlight);
            for (var row = 0; row < 4; row++)
            {
                Put(canvas, contentX, contentY + 3 + row, Truncate($"{row + 1,-2} —              —", contentWidth), Palette.Text);
            }
        }

        Put(canvas, contentX, contentY + 7, Truncate("Ваш результат: не опубликован", contentWidth), Palette.Dim);
        DrawSelectable(canvas, contentX, contentY + 8, '1', "Обновить таблицу", localSelection == 0);
        DrawSelectable(canvas, contentX, contentY + 9, '0', "Назад", localSelection == 1);
        Center(canvas, CanvasHeight - 1, "↑ ↓ выбрать   Enter продолжить   1/0 сразу   Esc назад", Palette.Dim);
        Render(canvas);
    }

    private void RenderSpeedPublicationFrame(int localSelection)
    {
        var canvas = CreateActionCanvas("НИКНЕЙМ И ПУБЛИКАЦИЯ", out var contentX, out var contentY, out var contentWidth);
        Put(canvas, contentX, contentY, "Никнейм", Palette.Dim);
        Put(canvas, contentX, contentY + 1, Truncate(speedNicknameDraft, contentWidth), Palette.Bright);
        Put(canvas, contentX, contentY + 3, "Последний замер", Palette.Dim);
        Put(canvas, contentX, contentY + 4, Truncate("ещё не выполнен", contentWidth), Palette.Text);
        Put(canvas, contentX, contentY + 5, "Публикация", Palette.Dim);
        Put(canvas, contentX, contentY + 6, "по желанию", Palette.Good);
        DrawSelectable(canvas, contentX, contentY + 7, '1', "Изменить никнейм", localSelection == 0);
        DrawSelectable(canvas, contentX, contentY + 8, '2', "Опубликовать результат", localSelection == 1);
        DrawSelectable(canvas, contentX, contentY + 9, '0', "Назад", localSelection == 2);
        Center(canvas, CanvasHeight - 1, "↑ ↓ выбрать   Enter продолжить   1–2/0 сразу   Esc назад", Palette.Dim);
        Render(canvas);
    }

    private void RenderSpeedNicknamePrompt(string value)
    {
        var canvas = CreateActionCanvas("НИКНЕЙМ", out var contentX, out var contentY, out var contentWidth);
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
        Center(canvas, CanvasHeight - 1, "Enter сохранить   Backspace удалить   Esc отмена", Palette.Dim);
        Render(canvas);
    }

    private void RenderSpeedNoticeFrame(string title, string message)
    {
        var canvas = CreateActionCanvas(title, out var contentX, out var contentY, out var contentWidth);
        PutWrapped(canvas, contentX, contentY + 1, contentWidth, message, Palette.Text);
        PutWrapped(
            canvas,
            contentX,
            contentY + 6,
            contentWidth,
            "UI-ветка не выполняет сетевых запросов и не публикует данные.",
            Palette.Dim);
        Center(canvas, CanvasHeight - 1, "Enter / Esc — вернуться", Palette.Dim);
        Render(canvas);
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
