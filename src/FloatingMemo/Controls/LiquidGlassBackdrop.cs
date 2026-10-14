using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FloatingMemo.Controls;

/// <summary>
/// The window's glass: Windows' desktop acrylic (it blurs whatever is behind the window), tuned
/// to be lighter and clearer than the default — a cool, slightly blue-white glass that lets the
/// desktop's colors through. Windows still switches to a solid color by itself when transparency
/// effects are off, in battery saver, or in high contrast.
/// </summary>
public sealed partial class LiquidGlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropTheme _theme = SystemBackdropTheme.Default;
    private double _clarity = 0.6;

    /// <summary>0 = frosted (the glass tints and evens out what is behind), 1 = clear.</summary>
    public double Clarity
    {
        get => _clarity;
        set
        {
            _clarity = Math.Clamp(value, 0, 1);
            Apply(_theme);
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported())
        {
            return; // the window's own background is used instead
        }

        _controller = new DesktopAcrylicController();
        _controller.AddSystemBackdropTarget(connectedTarget);
        var configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _controller.SetSystemBackdropConfiguration(configuration);
        Apply(configuration.Theme);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        Apply(GetDefaultSystemBackdropConfiguration(target, xamlRoot).Theme);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
    }

    /// <summary>
    /// Tint = the glass's own color; tint opacity = how strongly it colors the view through it;
    /// luminosity opacity = how much it evens out the brightness behind it (lower = clearer).
    /// </summary>
    private void Apply(SystemBackdropTheme theme)
    {
        _theme = theme;
        if (_controller is null)
        {
            return;
        }

        // Clearer glass = less tint and less evening-out, so the colors behind come through
        // (still blurred by the acrylic). Even at "clear" a little tint stays, so text on the
        // glass (tabs, buttons) remains readable.
        bool dark = theme == SystemBackdropTheme.Dark;
        float c = (float)_clarity;
        _controller.TintColor = dark ? Color.FromArgb(0xFF, 0x2A, 0x31, 0x40) : Color.FromArgb(0xFF, 0xF2, 0xF6, 0xFB);
        _controller.TintOpacity = dark ? Lerp(0.40f, 0.06f, c) : Lerp(0.30f, 0.02f, c);
        _controller.LuminosityOpacity = dark ? Lerp(0.70f, 0.18f, c) : Lerp(0.60f, 0.12f, c);
        _controller.FallbackColor = dark ? Color.FromArgb(0xFF, 0x2B, 0x30, 0x38) : Color.FromArgb(0xFF, 0xEC, 0xF0, 0xF5);
    }

    private static float Lerp(float frosted, float clear, float clarity) => frosted + ((clear - frosted) * clarity);
}
