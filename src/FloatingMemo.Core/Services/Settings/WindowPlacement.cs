namespace FloatingMemo.Services.Settings;

/// <summary>A screen rectangle in physical pixels (e.g. a monitor's work area).</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// Decides where the memo window opens, so a remembered position can never put it off-screen
/// (e.g. after a monitor was unplugged or the resolution changed).
/// </summary>
public static class WindowPlacement
{
    // At least this much of the top strip must be on a screen so the window can be dragged back.
    private const int MinVisibleWidth = 120;
    private const int MinVisibleHeight = 32;

    /// <param name="saved">Remembered bounds, or null on first launch.</param>
    /// <param name="workAreas">Work areas of all connected monitors; the first is the primary.</param>
    /// <param name="defaultWidth">Width for first launch (physical pixels).</param>
    /// <param name="defaultHeight">Height for first launch (physical pixels).</param>
    public static WindowBounds Resolve(WindowBounds? saved, IReadOnlyList<ScreenRect> workAreas, int defaultWidth, int defaultHeight)
    {
        if (workAreas.Count == 0)
        {
            return saved ?? new WindowBounds(100, 100, defaultWidth, defaultHeight);
        }

        if (saved is not null && saved.Width > 0 && saved.Height > 0)
        {
            foreach (var area in workAreas)
            {
                if (TitleStripVisibleOn(saved, area))
                {
                    // Keep the position but never larger than the monitor it is on.
                    return saved with
                    {
                        Width = Math.Min(saved.Width, area.Width),
                        Height = Math.Min(saved.Height, area.Height),
                    };
                }
            }
        }

        // First launch or remembered position is unreachable: center on the primary monitor.
        var primary = workAreas[0];
        int width = Math.Min(saved?.Width > 0 ? saved.Width : defaultWidth, primary.Width);
        int height = Math.Min(saved?.Height > 0 ? saved.Height : defaultHeight, primary.Height);
        return new WindowBounds(
            primary.X + ((primary.Width - width) / 2),
            primary.Y + ((primary.Height - height) / 2),
            width,
            height);
    }

    private static bool TitleStripVisibleOn(WindowBounds w, ScreenRect area)
    {
        int overlapLeft = Math.Max(w.X, area.X);
        int overlapRight = Math.Min(w.X + w.Width, area.Right);
        bool topInside = w.Y >= area.Y && w.Y + MinVisibleHeight <= area.Bottom;
        return topInside && overlapRight - overlapLeft >= MinVisibleWidth;
    }
}
