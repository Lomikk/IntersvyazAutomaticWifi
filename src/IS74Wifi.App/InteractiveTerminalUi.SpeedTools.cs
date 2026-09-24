using System.Globalization;
using System.Text;
using IS74Wifi.Core;

namespace IS74Wifi.App;

// Native IS74 LibreSpeed measurement and the opt-in campus leaderboard are
// wired here while authorization remains isolated from high-bandwidth traffic.
internal sealed partial class InteractiveTerminalUi
{
    private const int SpeedNicknameMaximumLength = LeaderboardNicknamePreferences.MaximumNicknameLength;

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

    private const int SpeedLeaderboardDefaultLimit = 100;

    private string speedNicknameDraft = "Гость";
    private readonly List<SpeedLeaderboardRow> speedLeaderboardRows = [];
    private CampusSpeedTestRun? lastSpeedTestRun;
    private SpeedTestProgress? liveSpeedProgress;
    private double? liveDownloadMbps;
    private double? liveUploadMbps;
    private double? liveLatencyMs;
    private double? liveJitterMs;
    private bool speedMeasurementInProgress;
    private string speedStatusText = "Готово к замеру";
    private string speedLeaderboardStatusText = "Рейтинг не загружен";
    private bool speedLastPublished;

    private sealed record SpeedLeaderboardRow(
        int Rank,
        string Nickname,
        string DownloadMbps,
        string UploadMbps,
        string PingMs,
        string JitterMs);

    public async Task RunSpeedToolsAsync(
        InteractiveStatusSnapshot currentStatus,
        CampusSpeedToolsService speedTools,
        Func<CancellationToken, Task<bool>> ensureAnonymousStatisticsConsent,
        LeaderboardNicknamePreferences nicknamePreferences,
        CancellationToken cancellationToken = default)
    {
        speedNicknameDraft = nicknamePreferences.Load();
        status = currentStatus;
        var mainMenuSelection = selected;

        if (!CanUseInteractiveSession)
        {
            await RunSpeedToolsCompactAsync(speedTools, ensureAnonymousStatisticsConsent, nicknamePreferences, cancellationToken).ConfigureAwait(false);
            selected = mainMenuSelection;
            return;
        }

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<LeaderboardReadResult>? initialLeaderboard = null;
        if (speedTools.BackendEnabled)
        {
            speedLeaderboardStatusText = "Обновляю рейтинг…";
            initialLeaderboard = speedTools.GetLeaderboardAsync(SpeedLeaderboardDefaultLimit, sessionCts.Token);
        }
        else
        {
            speedLeaderboardStatusText = "Backend не настроен";
        }

        PrepareInteractiveConsole(clear: false);
        try
        {
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (initialLeaderboard is { IsCompleted: true })
                {
                    ApplyLeaderboardResult(await initialLeaderboard.ConfigureAwait(false));
                    initialLeaderboard = null;
                }

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
                    var pendingKey = await RunSpeedMeasurementAsync(speedTools, cancellationToken).ConfigureAwait(false);
                    keyTask = pendingKey ?? ReadKeyAsync();
                    continue;
                }

                if (key.KeyChar == '2')
                {
                    await ShowExpandedLeaderboardAsync(speedTools, cancellationToken).ConfigureAwait(false);
                }
                else if (key.Key == ConsoleKey.R)
                {
                    await RefreshSpeedLeaderboardAsync(speedTools, cancellationToken).ConfigureAwait(false);
                }
                else if (key.KeyChar == '3')
                {
                    var nickname = await PromptSpeedNicknameAsync(speedNicknameDraft, cancellationToken).ConfigureAwait(false);
                    if (nickname is not null)
                    {
                        if (nicknamePreferences.TrySave(nickname, out var savedNickname))
                        {
                            speedNicknameDraft = savedNickname;
                            speedStatusText = "Ник сохранён";
                        }
                        else
                        {
                            speedStatusText = "Ник не сохранён: используйте буквы или цифры";
                        }
                    }
                }
                else if (key.KeyChar == '4')
                {
                    await PublishLastSpeedResultAsync(speedTools, ensureAnonymousStatisticsConsent, cancellationToken).ConfigureAwait(false);
                }

                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            sessionCts.Cancel();
            if (initialLeaderboard is not null)
            {
                try
                {
                    _ = await initialLeaderboard.ConfigureAwait(false);
                }
                catch
                {
                }
            }
            selected = mainMenuSelection;
            RestoreConsole();
        }
    }

    private async Task<Task<ConsoleKeyInfo>?> RunSpeedMeasurementAsync(
        CampusSpeedToolsService speedTools,
        CancellationToken cancellationToken)
    {
        liveSpeedProgress = null;
        liveDownloadMbps = null;
        liveUploadMbps = null;
        liveLatencyMs = null;
        liveJitterMs = null;
        speedMeasurementInProgress = true;
        speedStatusText = "Подготовка LibreSpeed…  Esc — отмена";

        using var measurementCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new InlineProgress<SpeedTestProgress>(ApplyLiveSpeedProgress);
        var measurementTask = speedTools.MeasureAsync(progress, measurementCts.Token);
        var keyTask = ReadKeyAsync();

        while (!measurementTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateLayout();
            RenderSpeedDashboardFrame();

            var completed = await Task.WhenAny(
                measurementTask,
                keyTask,
                Task.Delay(33, cancellationToken)).ConfigureAwait(false);

            if (completed != keyTask)
            {
                continue;
            }

            var key = await keyTask.ConfigureAwait(false);
            if (key.Key == ConsoleKey.Escape || key.KeyChar == '0')
            {
                measurementCts.Cancel();
                try
                {
                    await measurementTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                speedStatusText = "Замер отменён";
                speedMeasurementInProgress = false;
                liveSpeedProgress = null;
                return null;
            }

            keyTask = ReadKeyAsync();
        }

        try
        {
            lastSpeedTestRun = await measurementTask.ConfigureAwait(false);
            speedLastPublished = false;
            speedMeasurementInProgress = false;
            liveSpeedProgress = new SpeedTestProgress(
                SpeedTestStage.Completed,
                1,
                lastSpeedTestRun.Measurement.DownloadMbps,
                lastSpeedTestRun.Measurement.LatencyMs,
                lastSpeedTestRun.Measurement.JitterMs);

            speedStatusText = DescribeSpeedStatisticsStatus(lastSpeedTestRun);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            speedStatusText = "Замер отменён";
            speedMeasurementInProgress = false;
            liveSpeedProgress = null;
        }
        catch (Exception ex)
        {
            speedStatusText = "Ошибка замера: " + FriendlySpeedError(ex.Message);
            speedMeasurementInProgress = false;
            liveSpeedProgress = null;
        }

        return keyTask;
    }

    private async Task RefreshSpeedLeaderboardAsync(
        CampusSpeedToolsService speedTools,
        CancellationToken cancellationToken)
    {
        if (!speedTools.BackendEnabled)
        {
            speedLeaderboardStatusText = "Backend не настроен";
            return;
        }

        speedLeaderboardStatusText = "Обновляю рейтинг…";
        UpdateLayout();
        RenderSpeedDashboardFrame();
        var result = await speedTools.GetLeaderboardAsync(
            SpeedLeaderboardDefaultLimit,
            cancellationToken).ConfigureAwait(false);
        ApplyLeaderboardResult(result);
    }

    private void ApplyLeaderboardResult(LeaderboardReadResult result)
    {
        if (!result.Success)
        {
            speedLeaderboardStatusText = "Рейтинг недоступен: " + FriendlyTelemetryError(result.Error);
            return;
        }

        speedLeaderboardRows.Clear();
        speedLeaderboardRows.AddRange(result.Entries.Select(entry => new SpeedLeaderboardRow(
            entry.Rank,
            entry.Nickname,
            FormatMetric(entry.DownloadMbps),
            FormatMetric(entry.UploadMbps),
            FormatMetric(entry.LatencyMs),
            FormatMetric(entry.JitterMs))));
        speedLeaderboardStatusText = speedLeaderboardRows.Count == 0
            ? "Пока нет опубликованных результатов"
            : $"Загружено: {speedLeaderboardRows.Count}";
    }

    private async Task PublishLastSpeedResultAsync(
        CampusSpeedToolsService speedTools,
        Func<CancellationToken, Task<bool>> ensureAnonymousStatisticsConsent,
        CancellationToken cancellationToken)
    {
        if (lastSpeedTestRun is null)
        {
            await ShowSpeedToolsNoticeAsync(
                "ПУБЛИКАЦИЯ РЕЗУЛЬТАТА",
                "Сначала выполните замер скорости.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!speedTools.BackendEnabled)
        {
            await ShowSpeedToolsNoticeAsync(
                "ПУБЛИКАЦИЯ РЕЗУЛЬТАТА",
                "Backend статистики не настроен. Сам замер работает локально, но опубликовать результат в общем рейтинге пока нельзя.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!speedTools.AnonymousStatisticsAllowed &&
            !await ensureAnonymousStatisticsConsent(cancellationToken).ConfigureAwait(false))
        {
            speedStatusText = "Публикация отменена · анонимная статистика отключена";
            return;
        }

        speedStatusText = "Публикую результат…";
        UpdateLayout();
        RenderSpeedDashboardFrame();
        var result = await speedTools.PublishAsync(
            lastSpeedTestRun,
            speedNicknameDraft,
            cancellationToken).ConfigureAwait(false);

        if (result.Write.Success)
        {
            speedLastPublished = true;
            speedStatusText = $"Опубликовано как {speedNicknameDraft}";
            await RefreshSpeedLeaderboardAsync(speedTools, cancellationToken).ConfigureAwait(false);
            return;
        }

        speedStatusText = result.Queued
            ? "Публикация отложена до следующей выгрузки"
            : "Публикация не выполнена: " + FriendlyTelemetryError(result.Write.Error);
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
                // Empty input is reported by the shared validator. It never
                // clears the previously saved preference.
                return value.ToString().Trim();
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

    private async Task ShowExpandedLeaderboardAsync(
        CampusSpeedToolsService speedTools,
        CancellationToken cancellationToken)
    {
        var scrollOffset = 0;
        var keyTask = ReadKeyAsync();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateLayout();

            var visibleRows = GetExpandedLeaderboardVisibleRowCount();
            scrollOffset = Math.Clamp(
                scrollOffset,
                0,
                Math.Max(0, speedLeaderboardRows.Count - visibleRows));

            RenderExpandedLeaderboardFrame(scrollOffset, visibleRows);

            var completed = await Task.WhenAny(keyTask, Task.Delay(16, cancellationToken)).ConfigureAwait(false);
            if (completed != keyTask)
            {
                continue;
            }

            var key = await keyTask.ConfigureAwait(false);
            if (key.Key is ConsoleKey.Enter or ConsoleKey.Escape || key.KeyChar == '0' || key.KeyChar == '2')
            {
                return;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                scrollOffset = Math.Max(0, scrollOffset - 1);
            }
            else if (key.Key == ConsoleKey.DownArrow)
            {
                scrollOffset = Math.Min(
                    Math.Max(0, speedLeaderboardRows.Count - visibleRows),
                    scrollOffset + 1);
            }
            else if (key.Key == ConsoleKey.PageUp)
            {
                scrollOffset = Math.Max(0, scrollOffset - visibleRows);
            }
            else if (key.Key == ConsoleKey.PageDown)
            {
                scrollOffset = Math.Min(
                    Math.Max(0, speedLeaderboardRows.Count - visibleRows),
                    scrollOffset + visibleRows);
            }
            else if (key.Key == ConsoleKey.Home)
            {
                scrollOffset = 0;
            }
            else if (key.Key == ConsoleKey.End)
            {
                scrollOffset = Math.Max(0, speedLeaderboardRows.Count - visibleRows);
            }
            else if (key.Key == ConsoleKey.R)
            {
                await RefreshSpeedLeaderboardAsync(speedTools, cancellationToken).ConfigureAwait(false);
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
        Center(canvas, CanvasHeight - 1, "Enter/1 замер   2 таблица   R обновить   3 ник   4 публикация   Esc назад", Palette.Dim);
        Render(canvas);
    }

    private void RenderExpandedLeaderboardFrame(int scrollOffset, int visibleRows)
    {
        var canvas = CreateCanvas();
        var boxY = 1;
        var boxHeight = Math.Max(8, CanvasHeight - 3);
        var boxX = compactLayout ? 1 : Math.Max(1, (canvasWidth - Math.Min(canvasWidth - 2, 112)) / 2);
        var boxWidth = compactLayout ? Math.Max(20, canvasWidth - 2) : Math.Min(canvasWidth - 2, 112);

        DrawBox(canvas, boxX, boxY, boxWidth, boxHeight, "ЛИДЕРЫ КАМПУСА");

        var x = boxX + 3;
        var y = boxY + 2;
        var width = Math.Max(1, boxWidth - 6);

        DrawLeaderboardHeader(canvas, x, y, width);

        if (speedLeaderboardRows.Count == 0)
        {
            Put(canvas, x, y + 2, Truncate(speedLeaderboardStatusText, width), Palette.Dim);
        }
        else
        {
            for (var rowIndex = 0; rowIndex < visibleRows; rowIndex++)
            {
                var sourceIndex = scrollOffset + rowIndex;
                if (sourceIndex >= speedLeaderboardRows.Count)
                {
                    break;
                }

                DrawLeaderboardRow(canvas, x, y + 2 + rowIndex, width, speedLeaderboardRows[sourceIndex]);
            }
        }

        var first = speedLeaderboardRows.Count == 0 ? 0 : scrollOffset + 1;
        var last = speedLeaderboardRows.Count == 0
            ? 0
            : Math.Min(speedLeaderboardRows.Count, scrollOffset + visibleRows);
        Put(
            canvas,
            x,
            boxY + boxHeight - 2,
            Truncate($"Строки {first}–{last} / {speedLeaderboardRows.Count}   Ник: {speedNicknameDraft}", width),
            Palette.Dim);

        Center(
            canvas,
            CanvasHeight - 1,
            "↑↓ листать   PgUp/PgDn страница   Home/End край   R обновить   Enter/Esc назад",
            Palette.Dim);
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
            "Замер выполняется против s.is74.ru. Публикация в рейтинг происходит только по команде пользователя.",
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

    private void DrawSpeedMeasurementPane(Cell[,] canvas, int x, int y, int width)
    {
        var measurement = speedMeasurementInProgress ? null : lastSpeedTestRun?.Measurement;
        var progress = liveSpeedProgress;

        var download = speedMeasurementInProgress ? liveDownloadMbps : measurement?.DownloadMbps;
        var upload = speedMeasurementInProgress ? liveUploadMbps : measurement?.UploadMbps;
        var latency = speedMeasurementInProgress ? liveLatencyMs : measurement?.LatencyMs;
        var jitter = speedMeasurementInProgress ? liveJitterMs : measurement?.JitterMs;

        DrawLargeSpeedMetric(canvas, x, y, width, FormatLargeMetric(download), Palette.BrandBright);
        CenterWithin(canvas, x, width, y + 6, SpeedStageCaption(progress), Palette.Dim);

        Put(canvas, x, y + 7, Truncate($"↑ Upload     {FormatMetric(upload)} Mbit/s", width), Palette.Text);
        Put(canvas, x, y + 8, Truncate($"Ping         {FormatMetric(latency)} ms", width), Palette.Text);
        Put(canvas, x, y + 9, Truncate($"Jitter       {FormatMetric(jitter)} ms", width), Palette.Text);
        Put(canvas, x, y + 10, Truncate($"Packet loss  {FormatMetric(measurement?.PacketLossPct)} %", width), Palette.Text);
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

        DrawLeaderboardHeader(canvas, x, y, width);
        if (speedLeaderboardRows.Count == 0)
        {
            Put(canvas, x, y + 2, Truncate(speedLeaderboardStatusText, width), Palette.Dim);
        }
        else
        {
            for (var row = 0; row < 5 && row < speedLeaderboardRows.Count; row++)
            {
                DrawLeaderboardRow(canvas, x, y + 2 + row, width, speedLeaderboardRows[row]);
            }
        }

        Put(canvas, x, y + 8, Truncate($"Ник: {speedNicknameDraft}", width), Palette.Bright);
        var publication = lastSpeedTestRun is null
            ? "Ваш результат: замер ещё не выполнен"
            : speedLastPublished
                ? "Ваш результат: опубликован"
                : "Ваш результат: не опубликован";
        Put(canvas, x, y + 9, Truncate(publication, width), Palette.Dim);
        Put(canvas, x, y + 10, Truncate(speedStatusText, width), Palette.Dim);
    }

    private static void DrawLeaderboardHeader(Cell[,] canvas, int x, int y, int width)
    {
        if (width >= 43)
        {
            Put(canvas, x, y, Truncate("#  НИК            ↓      ↑   PING  JIT", width), Palette.Highlight);
            Put(canvas, x, y + 1, new string('─', Math.Min(width, 41)), Palette.Dim);
        }
        else
        {
            Put(canvas, x, y, Truncate("# НИК          ↓   PING  JIT", width), Palette.Highlight);
            Put(canvas, x, y + 1, new string('─', Math.Min(width, 29)), Palette.Dim);
        }
    }

    private static void DrawLeaderboardRow(
        Cell[,] canvas,
        int x,
        int y,
        int width,
        SpeedLeaderboardRow row)
    {
        if (width >= 43)
        {
            Put(
                canvas,
                x,
                y,
                Truncate(
                    $"{row.Rank,-2} {Truncate(row.Nickname, 14),-14} {row.DownloadMbps,6} {row.UploadMbps,6} {row.PingMs,6} {row.JitterMs,4}",
                    width),
                Palette.Text);
        }
        else
        {
            Put(
                canvas,
                x,
                y,
                Truncate(
                    $"{row.Rank,-2} {Truncate(row.Nickname, 12),-12} {row.DownloadMbps,3} {row.PingMs,5} {row.JitterMs,4}",
                    width),
                Palette.Text);
        }
    }

    private int GetExpandedLeaderboardVisibleRowCount()
    {
        var boxHeight = Math.Max(8, CanvasHeight - 3);
        // Two rows for header/separator, one summary row at the bottom and box padding.
        return Math.Max(1, boxHeight - 6);
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

    private async Task RunSpeedToolsCompactAsync(
        CampusSpeedToolsService speedTools,
        Func<CancellationToken, Task<bool>> ensureAnonymousStatisticsConsent,
        LeaderboardNicknamePreferences nicknamePreferences,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Console.Clear();
            Console.WriteLine("IS74W — Скорость и рейтинг кампуса");
            Console.WriteLine();
            Console.WriteLine("ЗАМЕР");
            Console.WriteLine($"  Download   {FormatMetric(lastSpeedTestRun?.Measurement.DownloadMbps)} Mbit/s");
            Console.WriteLine($"  Upload     {FormatMetric(lastSpeedTestRun?.Measurement.UploadMbps)} Mbit/s");
            Console.WriteLine($"  Ping       {FormatMetric(lastSpeedTestRun?.Measurement.LatencyMs)} ms");
            Console.WriteLine($"  Jitter     {FormatMetric(lastSpeedTestRun?.Measurement.JitterMs)} ms");
            Console.WriteLine($"  Loss       {FormatMetric(lastSpeedTestRun?.Measurement.PacketLossPct)} %");
            Console.WriteLine($"  {speedStatusText}");
            Console.WriteLine();
            Console.WriteLine("ЛИДЕРЫ КАМПУСА");
            Console.WriteLine("  #  Ник              ↓      ↑   Ping  Jit");
            foreach (var row in speedLeaderboardRows.Take(3))
            {
                Console.WriteLine($"  {row.Rank,-2} {Truncate(row.Nickname, 14),-14} {row.DownloadMbps,6} {row.UploadMbps,6} {row.PingMs,6} {row.JitterMs,4}");
            }
            if (speedLeaderboardRows.Count == 0)
            {
                Console.WriteLine("  " + speedLeaderboardStatusText);
            }
            Console.WriteLine();
            Console.WriteLine($"Ник: {speedNicknameDraft}");
            Console.WriteLine();
            Console.WriteLine("[1] Начать замер   [2] Развернуть таблицу");
            Console.WriteLine("[R] Обновить рейтинг");
            Console.WriteLine("[3] Никнейм        [4] Опубликовать");
            Console.WriteLine("[0] Назад");

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape || key.KeyChar == '0')
            {
                return;
            }

            if (key.KeyChar == '1' || key.Key == ConsoleKey.Enter)
            {
                Console.Clear();
                Console.WriteLine("Измеряю скорость через s.is74.ru…");
                Console.WriteLine("В компактном режиме отмена доступна через Ctrl+C.");
                try
                {
                    lastSpeedTestRun = await speedTools.MeasureAsync(null, cancellationToken).ConfigureAwait(false);
                    speedLastPublished = false;
                    speedStatusText = DescribeSpeedStatisticsStatus(lastSpeedTestRun);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    speedStatusText = "Ошибка замера: " + FriendlySpeedError(ex.Message);
                }
            }
            else if (key.KeyChar == '2')
            {
                RunSpeedLeaderboardCompact();
            }
            else if (key.Key == ConsoleKey.R)
            {
                var result = await speedTools.GetLeaderboardAsync(SpeedLeaderboardDefaultLimit, cancellationToken).ConfigureAwait(false);
                ApplyLeaderboardResult(result);
            }
            else if (key.KeyChar == '3')
            {
                Console.Clear();
                Console.Write("Никнейм: ");
                var nickname = Console.ReadLine()?.Trim();
                if (nicknamePreferences.TrySave(nickname, out var savedNickname))
                {
                    speedNicknameDraft = savedNickname;
                    speedStatusText = "Ник сохранён";
                }
                else
                {
                    speedStatusText = "Ник не сохранён: используйте буквы или цифры";
                }
            }
            else if (key.KeyChar == '4')
            {
                if (lastSpeedTestRun is null)
                {
                    speedStatusText = "Сначала выполните замер";
                }
                else if (!speedTools.BackendEnabled)
                {
                    speedStatusText = "Публикация недоступна: backend не настроен";
                }
                else if (!speedTools.AnonymousStatisticsAllowed &&
                         !await ensureAnonymousStatisticsConsent(cancellationToken).ConfigureAwait(false))
                {
                    speedStatusText = "Публикация отменена · анонимная статистика отключена";
                }
                else
                {
                    var result = await speedTools.PublishAsync(lastSpeedTestRun, speedNicknameDraft, cancellationToken).ConfigureAwait(false);
                    speedLastPublished = result.Write.Success;
                    speedStatusText = result.Write.Success
                        ? $"Опубликовано как {speedNicknameDraft}"
                        : result.Queued
                            ? "Публикация отложена"
                            : "Публикация не выполнена: " + FriendlyTelemetryError(result.Write.Error);
                    if (result.Write.Success)
                    {
                        ApplyLeaderboardResult(await speedTools.GetLeaderboardAsync(
                            SpeedLeaderboardDefaultLimit,
                            cancellationToken).ConfigureAwait(false));
                    }
                }
            }
        }
    }

    private void RunSpeedLeaderboardCompact()
    {
        var offset = 0;
        while (true)
        {
            Console.Clear();
            var visible = Math.Max(3, Console.WindowHeight - 8);
            offset = Math.Clamp(offset, 0, Math.Max(0, speedLeaderboardRows.Count - visible));

            Console.WriteLine("IS74W — Лидеры кампуса");
            Console.WriteLine();
            Console.WriteLine("#  Ник              ↓      ↑   Ping  Jit");
            Console.WriteLine(new string('-', 43));
            foreach (var row in speedLeaderboardRows.Skip(offset).Take(visible))
            {
                Console.WriteLine($"{row.Rank,-2} {Truncate(row.Nickname, 14),-14} {row.DownloadMbps,6} {row.UploadMbps,6} {row.PingMs,6} {row.JitterMs,4}");
            }
            if (speedLeaderboardRows.Count == 0)
            {
                Console.WriteLine(speedLeaderboardStatusText);
            }

            Console.WriteLine();
            Console.WriteLine("↑↓ листать   PgUp/PgDn страница   Home/End край   Enter/Esc назад");

            var key = Console.ReadKey(intercept: true);
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
                offset = Math.Min(Math.Max(0, speedLeaderboardRows.Count - visible), offset + 1);
            }
            else if (key.Key == ConsoleKey.PageUp)
            {
                offset = Math.Max(0, offset - visible);
            }
            else if (key.Key == ConsoleKey.PageDown)
            {
                offset = Math.Min(Math.Max(0, speedLeaderboardRows.Count - visible), offset + visible);
            }
            else if (key.Key == ConsoleKey.Home)
            {
                offset = 0;
            }
            else if (key.Key == ConsoleKey.End)
            {
                offset = Math.Max(0, speedLeaderboardRows.Count - visible);
            }
        }
    }

    private void ApplyLiveSpeedProgress(SpeedTestProgress value)
    {
        liveSpeedProgress = value;
        if (value.LatencyMs is { } latency)
        {
            liveLatencyMs = latency;
        }
        if (value.JitterMs is { } jitter)
        {
            liveJitterMs = jitter;
        }
        if (value.Stage == SpeedTestStage.Download && value.Mbps is { } download)
        {
            liveDownloadMbps = download;
        }
        if (value.Stage == SpeedTestStage.Upload && value.Mbps is { } upload)
        {
            liveUploadMbps = upload;
        }
    }

    private static string SpeedStageCaption(SpeedTestProgress? progress)
    {
        if (progress is null)
        {
            return "DOWNLOAD · Mbit/s";
        }

        var percent = Math.Clamp((int)Math.Round(progress.Progress * 100), 0, 100);
        return progress.Stage switch
        {
            SpeedTestStage.Latency => $"PING · {percent}%",
            SpeedTestStage.Download => $"DOWNLOAD · Mbit/s · {percent}%",
            SpeedTestStage.Upload => $"UPLOAD · {percent}%",
            _ => "DOWNLOAD · Mbit/s"
        };
    }

    private static string FormatLargeMetric(double? value)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            return "---";
        }

        return value.Value switch
        {
            < 100 => value.Value.ToString("0.0", CultureInfo.InvariantCulture),
            < 1000 => value.Value.ToString("0", CultureInfo.InvariantCulture),
            _ => value.Value.ToString("0", CultureInfo.InvariantCulture)
        };
    }

    private static string DescribeSpeedStatisticsStatus(CampusSpeedTestRun run)
    {
        if (run.StatisticsWrite.Success)
        {
            return "Замер завершён · статистика отправлена";
        }
        if (run.StatisticsQueued)
        {
            return "Замер завершён · статистика сохранена в очереди";
        }
        if (string.Equals(run.StatisticsWrite.Error, "statistics_disabled", StringComparison.Ordinal))
        {
            return "Замер завершён · локально, статистика отключена";
        }
        return "Замер завершён · статистика не отправлена";
    }

    private static string FormatMetric(double? value)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            return "—";
        }

        return value.Value switch
        {
            < 10 => value.Value.ToString("0.0", CultureInfo.InvariantCulture),
            < 100 => value.Value.ToString("0.0", CultureInfo.InvariantCulture),
            _ => value.Value.ToString("0", CultureInfo.InvariantCulture)
        };
    }

    private static string FriendlyTelemetryError(string? error)
    {
        if (error?.StartsWith("http_", StringComparison.Ordinal) == true)
        {
            return "HTTP " + error[5..];
        }

        return error switch
        {
            "backend_not_configured" => "backend не настроен",
            "statistics_disabled" => "анонимная статистика отключена",
            "statistics_consent_required" => "нужно разрешить анонимную статистику",
            "timeout" => "таймаут HTTP-запроса",
            "cancelled" => "запрос отменён интерфейсом",
            "dns" => "ошибка DNS",
            "connect" => "не удалось подключиться",
            "tls" => "ошибка TLS",
            "proxy" => "ошибка proxy",
            "redirect" => "ошибка redirect",
            "http_version" => "ошибка согласования HTTP",
            "protocol" => "ошибка HTTP-протокола",
            "transport" => "сетевая ошибка",
            "contract_mismatch" => "backend устарел или несовместим",
            "invalid_response" => "некорректный ответ backend",
            "invalid_nickname" => "некорректный никнейм",
            null or "" => "неизвестная ошибка",
            _ => "backend: " + error
        };
    }

    private static string FriendlySpeedError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "неизвестная ошибка";
        }

        return error.Length <= 72 ? error : error[..72];
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
