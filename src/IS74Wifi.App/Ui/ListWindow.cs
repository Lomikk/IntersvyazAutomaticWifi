namespace IS74Wifi.App;

internal readonly record struct ListWindow(int Offset, int Count)
{
    internal int FirstDisplayIndex => Count == 0 ? 0 : Offset + 1;
    internal int LastDisplayIndex => Count == 0 ? 0 : Offset + Count;

    internal static int VisibleRowsForBox(int boxHeight, int reservedRows) =>
        Math.Max(1, boxHeight - reservedRows);

    internal static ListWindow AroundSelection(int itemCount, int selectedIndex, int visibleRows)
    {
        itemCount = Math.Max(0, itemCount);
        visibleRows = Math.Max(1, visibleRows);
        if (itemCount == 0)
        {
            return new ListWindow(0, 0);
        }

        selectedIndex = Math.Clamp(selectedIndex, 0, itemCount - 1);
        var offset = Math.Clamp(
            selectedIndex - visibleRows + 1,
            0,
            Math.Max(0, itemCount - visibleRows));
        return FromOffset(itemCount, offset, visibleRows);
    }

    internal static ListWindow FromOffset(int itemCount, int requestedOffset, int visibleRows)
    {
        itemCount = Math.Max(0, itemCount);
        visibleRows = Math.Max(1, visibleRows);
        if (itemCount == 0)
        {
            return new ListWindow(0, 0);
        }

        var offset = Math.Clamp(requestedOffset, 0, Math.Max(0, itemCount - visibleRows));
        return new ListWindow(offset, Math.Min(visibleRows, itemCount - offset));
    }
}
