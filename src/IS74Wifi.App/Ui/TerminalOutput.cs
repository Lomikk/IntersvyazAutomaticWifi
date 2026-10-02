using System.Text;

namespace IS74Wifi.App;

internal sealed class TerminalOutput(bool ansi)
{
    private Cell[,]? lastRenderedCanvas;
    private int lastRenderedLeft = -1;
    private int lastRenderedTerminalWidth = -1;

    internal bool HasRenderedFrame => lastRenderedCanvas is not null;

    internal void Render(Cell[,] canvas, int renderLeft)
    {
        var terminalWidth = GetWindowWidthSafe();
        if (ansi)
        {
            var frame = ComposeAnsiFrame(canvas, renderLeft, terminalWidth);
            Console.Write(frame);
            RememberRenderedCanvas(canvas, renderLeft, terminalWidth);
            return;
        }

        RenderConsoleColors(canvas, renderLeft, terminalWidth);
    }

    internal string BuildAnsiFrame(Cell[,] canvas, int renderLeft, int terminalWidth)
    {
        var frame = ComposeAnsiFrame(canvas, renderLeft, terminalWidth);
        RememberRenderedCanvas(canvas, renderLeft, terminalWidth);
        return frame;
    }

    private string ComposeAnsiFrame(Cell[,] canvas, int renderLeft, int terminalWidth)
    {
        var width = canvas.GetLength(1);
        var height = canvas.GetLength(0);
        var fullRender = RequiresFullRender(canvas, renderLeft, terminalWidth);
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
        return output.ToString();
    }

    internal void Invalidate()
    {
        lastRenderedCanvas = null;
        lastRenderedLeft = -1;
        lastRenderedTerminalWidth = -1;
    }

    private void RenderConsoleColors(Cell[,] canvas, int renderLeft, int terminalWidth)
    {
        var width = canvas.GetLength(1);
        var height = canvas.GetLength(0);
        var fullRender = RequiresFullRender(canvas, renderLeft, terminalWidth);

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
        RememberRenderedCanvas(canvas, renderLeft, terminalWidth);
    }

    private bool RequiresFullRender(Cell[,] canvas, int renderLeft, int terminalWidth) =>
        lastRenderedCanvas is null ||
        lastRenderedCanvas.GetLength(0) != canvas.GetLength(0) ||
        lastRenderedCanvas.GetLength(1) != canvas.GetLength(1) ||
        lastRenderedLeft != renderLeft ||
        lastRenderedTerminalWidth != terminalWidth;

    private void RememberRenderedCanvas(Cell[,] canvas, int renderLeft, int terminalWidth)
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
            return TerminalLayout.MinimumTerminalWidth;
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
}
