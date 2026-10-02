using IS74Wifi.App;
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
