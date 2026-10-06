using System.Text;

namespace IS74Wifi.App;

internal readonly record struct Cell(char Character, Palette Color);

internal enum Palette
{
    Border,
    BrandDim,
    Brand,
    BrandBright,
    Highlight,
    Bright,
    Good,
    Warning,
    Error,
    Dim,
    Text
}

internal static class TerminalCanvas
{
    internal static Cell[,] Create(int width, int height)
    {
        var canvas = new Cell[height, width];
        for (var y = 0; y < canvas.GetLength(0); y++)
        {
            for (var x = 0; x < canvas.GetLength(1); x++)
            {
                canvas[y, x] = new Cell(' ', Palette.Text);
            }
        }

        return canvas;
    }

    internal static void Center(Cell[,] canvas, int row, string text, Palette color)
    {
        Put(canvas, Math.Max(0, (canvas.GetLength(1) - text.Length) / 2), row, text, color);
    }

    internal static void Put(Cell[,] canvas, int x, int y, string text, Palette color)
    {
        var height = canvas.GetLength(0);
        var width = canvas.GetLength(1);
        if (y < 0 || y >= height)
        {
            return;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var targetX = x + index;
            if (targetX < 0 || targetX >= width)
            {
                continue;
            }

            canvas[y, targetX] = new Cell(text[index], color);
        }
    }

    internal static void PutRightAligned(
        Cell[,] canvas,
        int minimumX,
        int rightExclusive,
        int row,
        string? value,
        Palette color)
    {
        var available = Math.Max(1, rightExclusive - minimumX);
        var text = Truncate(value, available);
        var x = Math.Max(minimumX, rightExclusive - text.Length);
        Put(canvas, x, row, text, color);
    }

    internal static void DrawBox(Cell[,] canvas, int x, int y, int width, int height, string? title)
    {
        Put(canvas, x, y, "┌" + new string('─', width - 2) + "┐", Palette.Border);
        for (var row = 1; row < height - 1; row++)
        {
            Put(canvas, x, y + row, "│", Palette.Border);
            Put(canvas, x + width - 1, y + row, "│", Palette.Border);
        }

        Put(canvas, x, y + height - 1, "└" + new string('─', width - 2) + "┘", Palette.Border);

        if (!string.IsNullOrWhiteSpace(title))
        {
            var caption = $" {title} ";
            var captionX = x + Math.Max(2, (width - caption.Length) / 2);
            Put(canvas, captionX, y, Truncate(caption, width - 4), Palette.Highlight);
        }
    }

    internal static void PutWrapped(Cell[,] canvas, int x, int y, int width, string text, Palette color)
    {
        var lines = WrapText(text, Math.Max(1, width));
        for (var index = 0; index < lines.Count; index++)
        {
            Put(canvas, x, y + index, lines[index], color);
        }
    }

    internal static List<string> WrapText(string? text, int width)
    {
        width = Math.Max(1, width);
        var value = string.IsNullOrWhiteSpace(text) ? "—" : text.Trim();
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        var line = new StringBuilder();

        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                result.Add(line.ToString());
                line.Clear();
            }

            var remaining = word;
            while (remaining.Length > width)
            {
                if (line.Length > 0)
                {
                    result.Add(line.ToString());
                    line.Clear();
                }

                result.Add(remaining[..width]);
                remaining = remaining[width..];
            }

            if (remaining.Length == 0)
            {
                continue;
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(remaining);
        }

        if (line.Length > 0)
        {
            result.Add(line.ToString());
        }

        if (result.Count == 0)
        {
            result.Add("—");
        }

        return result;
    }

    internal static string Truncate(string? value, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "—" : value;
        if (text.Length <= maxLength)
        {
            return text;
        }

        if (maxLength <= 1)
        {
            return text[..maxLength];
        }

        return text[..(maxLength - 1)] + "…";
    }
}
