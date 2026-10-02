namespace IS74Wifi.App;

internal readonly record struct TerminalLayout(
    int TerminalWidth,
    int TerminalHeight,
    bool Compact,
    int CanvasWidth,
    int RenderLeft,
    int PaneWidth,
    int LeftPaneX,
    int RightPaneX)
{
    internal const int MinimumInteractiveWidth = 30;
    internal const int MinimumTerminalWidth = 80;
    internal const int MinimumCanvasWidth = 79;
    internal const int PreferredCanvasWidth = 116;
    internal const int CanvasHeight = 30;
    internal const int PaneY = 14;
    internal const int PaneHeight = 14;

    internal bool SupportsInteractiveSession =>
        TerminalWidth >= MinimumInteractiveWidth && TerminalHeight >= CanvasHeight;

    internal bool SupportsRichLayout =>
        TerminalWidth >= MinimumTerminalWidth && TerminalHeight >= CanvasHeight;

    internal static TerminalLayout Calculate(int terminalWidth, int terminalHeight)
    {
        var compact = terminalWidth < MinimumTerminalWidth;
        if (compact)
        {
            var canvasWidth = Math.Max(20, terminalWidth - 1);
            return new TerminalLayout(
                terminalWidth,
                terminalHeight,
                Compact: true,
                CanvasWidth: canvasWidth,
                RenderLeft: 0,
                PaneWidth: Math.Max(18, canvasWidth - 2),
                LeftPaneX: 1,
                RightPaneX: 1);
        }

        var drawableWidth = Math.Max(MinimumCanvasWidth, terminalWidth - 1);
        var richCanvasWidth = Math.Min(PreferredCanvasWidth, drawableWidth);
        var renderLeft = Math.Max(0, (terminalWidth - richCanvasWidth) / 2);
        var gap = richCanvasWidth >= 100 ? 4 : 3;
        var paneWidth = Math.Max(30, (richCanvasWidth - 2 - gap) / 2);
        const int leftPaneX = 1;

        return new TerminalLayout(
            terminalWidth,
            terminalHeight,
            Compact: false,
            CanvasWidth: richCanvasWidth,
            RenderLeft: renderLeft,
            PaneWidth: paneWidth,
            LeftPaneX: leftPaneX,
            RightPaneX: leftPaneX + paneWidth + gap);
    }

    internal static TerminalLayout Fallback => Calculate(MinimumTerminalWidth, CanvasHeight);
}
