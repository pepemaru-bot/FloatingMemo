using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FloatingMemo.Controls;

/// <summary>
/// Tooltips (such as the "Ctrl+R" hint of the record button) that show only while the mouse
/// pointer is over their control, and close as soon as it leaves, the memo text is clicked, or
/// the window loses focus. XAML's own tooltips also open on keyboard focus and could stay open
/// when the pointer moved into the editor (WebView2 is another window, so the button never hears
/// that the pointer left). The tooltip stays registered with ToolTipService, so screen readers
/// read it as before; help text the app sets itself (e.g. the device name) is left alone.
/// </summary>
public static class HoverToolTips
{
    private sealed class Entry
    {
        public required ToolTip Tip { get; init; }

        public DispatcherTimer? Watch { get; set; }
    }

    private static readonly ConditionalWeakTable<FrameworkElement, Entry> Entries = new();
    private static readonly List<WeakReference<FrameworkElement>> Open = [];

    /// <summary>The text of the hover tooltip that is open now (null = none); for tests and diagnostics.</summary>
    public static string? OpenText { get; private set; }

    /// <summary>Sets (or replaces) the hover text of a control; null or empty removes it.</summary>
    public static void Set(FrameworkElement element, string? text)
    {
        if (Entries.TryGetValue(element, out var existing))
        {
            existing.Tip.Content = text;
            ToolTipService.SetToolTip(element, string.IsNullOrEmpty(text) ? null : existing.Tip);
            if (string.IsNullOrEmpty(text))
            {
                Hide(element);
            }

            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            ToolTipService.SetToolTip(element, null);
            return;
        }

        // XAML opens the tooltip (after its usual delay); it is closed again at once unless the
        // mouse is over the control (it also opens on keyboard focus, which is not wanted here).
        var tip = new ToolTip { Content = text };
        var entry = new Entry { Tip = tip };
        Entries.Add(element, entry);
        ToolTipService.SetToolTip(element, tip);
        tip.Opened += (_, _) =>
        {
            if (!PointerIsOver(element))
            {
                tip.IsOpen = false;
                return;
            }

            CloseAllExcept(element);
            Open.Add(new WeakReference<FrameworkElement>(element));
            OpenText = tip.Content as string;
            StartWatch(element, entry);
        };
        element.PointerExited += OnLeft;
        element.PointerCanceled += OnLeft;
        element.PointerCaptureLost += OnLeft;
        element.PointerPressed += OnLeft; // a click starts the action: the hint has done its job
        element.Unloaded += (_, _) => Hide(element);
    }

    /// <summary>The control's tooltip from XAML (ToolTipService.ToolTip="…") becomes a hover-only one.</summary>
    public static void Adopt(FrameworkElement element)
    {
        if (ToolTipService.GetToolTip(element) is string text)
        {
            Set(element, text);
        }
    }

    /// <summary>Closes every open hover tooltip (the memo was clicked or focused, the window was deactivated).</summary>
    public static void CloseAll()
    {
        foreach (var weak in Open.ToList())
        {
            if (weak.TryGetTarget(out var element))
            {
                Hide(element);
            }
        }

        Open.Clear();
    }

    private static void OnLeft(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Hide(element);
        }
    }

    private static void CloseAllExcept(FrameworkElement keep)
    {
        foreach (var weak in Open.ToList())
        {
            if (weak.TryGetTarget(out var element) && !ReferenceEquals(element, keep))
            {
                Hide(element);
            }
        }

        Open.Clear();
    }

    private static void StartWatch(FrameworkElement element, Entry entry)
    {
        // The pointer can leave into the editor (another window) without PointerExited: check it.
        entry.Watch ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        entry.Watch.Tick -= OnWatch;
        entry.Watch.Tick += OnWatch;
        entry.Watch.Start();

        void OnWatch(object? s, object a)
        {
            if (!PointerIsOver(element))
            {
                Hide(element);
            }
        }
    }

    private static void Hide(FrameworkElement element)
    {
        if (Entries.TryGetValue(element, out var entry))
        {
            entry.Tip.IsOpen = false;
            entry.Watch?.Stop();
            if (Equals(OpenText, entry.Tip.Content))
            {
                OpenText = null;
            }
        }
    }

    /// <summary>Whether the mouse pointer is inside the control's bounds on screen.</summary>
    private static bool PointerIsOver(FrameworkElement element)
    {
        if (element.XamlRoot is null || !element.IsLoaded || element.Visibility != Visibility.Visible || !Interop.NativeMethods.GetCursorPos(out var cursor))
        {
            return false;
        }

        try
        {
            var bounds = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            double scale = element.XamlRoot.RasterizationScale;
            var origin = ClientOrigin(element);
            double x = (cursor.X - origin.X) / scale, y = (cursor.Y - origin.Y) / scale;
            return x >= bounds.Left && x <= bounds.Right && y >= bounds.Top && y <= bounds.Bottom;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The screen position of the client area of the window hosting the control.</summary>
    public static Func<FrameworkElement, (int X, int Y)> ClientOrigin { get; set; } = _ => (0, 0);
}
