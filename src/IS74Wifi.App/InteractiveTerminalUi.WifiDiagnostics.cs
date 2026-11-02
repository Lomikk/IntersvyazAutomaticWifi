namespace IS74Wifi.App;

internal sealed partial class InteractiveTerminalUi
{
    public async Task ShowWifiDiagnosticsAsync(
        Func<bool, CancellationToken, Task<WifiDiagnosticSnapshot>> capture,
        CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var screen = new WifiDiagnosticsScreen();
        if (!CanUseWifiDiagnosticsSession())
        {
            var scan = false;
            while (true)
            {
                screen.ReplaceSnapshot(await CaptureWifiAsync(capture, scan, stop.Token).ConfigureAwait(false));
                Console.WriteLine("IS74Wifi — Сигнал Wi-Fi и точки доступа");
                foreach (var row in screen.BuildRows(Math.Max(24, layout.CanvasWidth - 2))) Console.WriteLine(row.Text);
                Console.WriteLine("R — обновить точки; Enter/0 — назад.");
                var input = Console.ReadLine();
                if (!string.Equals(input?.Trim(), "r", StringComparison.OrdinalIgnoreCase)) return;
                scan = true;
            }
        }

        UpdateLayout();
        PrepareInteractiveConsole(clear: false);
        Task<WifiDiagnosticSnapshot>? refresh = CaptureWifiAsync(capture, false, stop.Token);
        try
        {
            var keyTask = ReadKeyAsync();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateLayout();
                if (refresh is { IsCompleted: true })
                {
                    screen.ReplaceSnapshot(await refresh.ConfigureAwait(false));
                    refresh = null;
                }
                Render(screen.BuildFrame(layout, refresh is not null));
                if (await Task.WhenAny(keyTask, Task.Delay(32, cancellationToken)).ConfigureAwait(false) != keyTask) continue;
                var action = screen.HandleKey(await keyTask.ConfigureAwait(false), layout);
                if (action == WifiScreenAction.Back) return;
                if (action == WifiScreenAction.Refresh && refresh is null)
                    refresh = CaptureWifiAsync(capture, true, stop.Token);
                keyTask = ReadKeyAsync();
            }
        }
        finally
        {
            stop.Cancel();
            if (refresh is not null)
            {
                try { await refresh.ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            RestoreConsole();
        }
    }

    private static bool CanUseWifiDiagnosticsSession()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        try { return Console.WindowWidth >= 30 && Console.WindowHeight >= 8; }
        catch { return false; }
    }

    private static async Task<WifiDiagnosticSnapshot> CaptureWifiAsync(
        Func<bool, CancellationToken, Task<WifiDiagnosticSnapshot>> capture, bool scan, CancellationToken cancellationToken)
    {
        try { return await capture(scan, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return WifiDiagnosticSnapshot.Unavailable("Данные Wi-Fi недоступны: " + ex.GetType().Name); }
    }
}
