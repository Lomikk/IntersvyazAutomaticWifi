namespace IS74Wifi.App;

internal enum InteractiveActionLineKind
{
    Info,
    Active,
    Success,
    Warning,
    Error
}

internal sealed record InteractiveActionLine(
    string Text,
    InteractiveActionLineKind Kind);

internal sealed class InteractiveActionHistory
{
    private readonly List<InteractiveActionLine> lines = [];

    public IReadOnlyList<InteractiveActionLine> Lines => lines;

    public void AddInfo(string text) => lines.Add(new InteractiveActionLine(text, InteractiveActionLineKind.Info));

    public void AddSuccess(string text) => lines.Add(new InteractiveActionLine(text, InteractiveActionLineKind.Success));

    public void AddWarning(string text) => lines.Add(new InteractiveActionLine(text, InteractiveActionLineKind.Warning));

    public void AddError(string text) => lines.Add(new InteractiveActionLine(text, InteractiveActionLineKind.Error));

    public void Start(string text)
    {
        FinishActiveAsInfo();
        lines.Add(new InteractiveActionLine(text, InteractiveActionLineKind.Active));
    }

    public void CompleteActive(string text)
    {
        if (!ReplaceLastActive(text, InteractiveActionLineKind.Success))
        {
            AddSuccess(text);
        }
    }

    public void UpdateActive(string text)
    {
        if (!ReplaceLastActive(text, InteractiveActionLineKind.Active))
        {
            Start(text);
        }
    }

    public void WarnActive(string text)
    {
        if (!ReplaceLastActive(text, InteractiveActionLineKind.Warning))
        {
            AddWarning(text);
        }
    }

    public void FailActive(string text)
    {
        if (!ReplaceLastActive(text, InteractiveActionLineKind.Error))
        {
            AddError(text);
        }
    }

    public void FinishActiveAsInfo()
    {
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (lines[index].Kind != InteractiveActionLineKind.Active) continue;
            lines[index] = lines[index] with { Kind = InteractiveActionLineKind.Info };
            return;
        }
    }

    private bool ReplaceLastActive(string text, InteractiveActionLineKind kind)
    {
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (lines[index].Kind != InteractiveActionLineKind.Active) continue;
            lines[index] = new InteractiveActionLine(text, kind);
            return true;
        }
        return false;
    }
}
