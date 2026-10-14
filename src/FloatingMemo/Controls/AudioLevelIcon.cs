using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.UI;

namespace FloatingMemo.Controls;

public enum LevelIconKind
{
    /// <summary>Microphone: green liquid rising from the bottom of the capsule.</summary>
    Microphone,

    /// <summary>Speaker: blue liquid filling the speaker body from left to right.</summary>
    Speaker,
}

/// <summary>
/// A 16×16 microphone/speaker icon whose body fills with "liquid" as the sound gets louder.
/// The outline never moves (it uses the button's text color); only the fill changes. Off
/// shows the icon crossed out with no fill; "no signal" shows a dashed, faded outline.
/// </summary>
public sealed partial class AudioLevelIcon : UserControl
{
    private const double Size = 16;

    // Mic capsule (the part that fills) and the stand; speaker body and sound waves.
    private const string MicBody = "M5.5,3.5 A2.5,2.5 0 0 1 10.5,3.5 L10.5,7.5 A2.5,2.5 0 0 1 5.5,7.5 Z";
    private const string MicStand = "M3.25,7.25 A4.75,4.75 0 0 0 12.75,7.25 M8,12 L8,14.5 M5.5,14.5 L10.5,14.5";
    private const string SpeakerBody = "M1.5,6 L4.5,6 L8.5,2.5 L8.5,13.5 L4.5,10 L1.5,10 Z";
    private const string SpeakerWaves = "M10.75,5.75 A3,3 0 0 1 10.75,10.25 M12.5,3.75 A5.75,5.75 0 0 1 12.5,12.25";
    private const string Slash = "M2,2 L14,14";

    // Fill range of each body: the liquid's surface moves between these coordinates.
    private const double MicTop = 1, MicBottom = 10;
    private const double SpeakerLeft = 1.5, SpeakerRight = 8.5;

    private readonly Path _fill;
    private readonly Path _highlight;
    private readonly Path _glow;
    private readonly Path _outline;
    private readonly Path _extra;
    private readonly Path _slash;
    private readonly RectangleGeometry _clip = new();
    private readonly RectangleGeometry _highlightClip = new(); // a geometry can clip only one element
    private LevelIconKind _kind;
    private bool _isOn = true;
    private bool _noSignal;
    private double _level;

    public AudioLevelIcon()
    {
        IsHitTestVisible = false;
        Width = Size;
        Height = Size;

        // The outline uses the theme's text color through {ThemeResource}, so it follows the
        // element's light/dark/high-contrast theme. (Binding to an inherited Foreground did not
        // update reliably inside the toggle buttons.) Disabled is shown via the button's opacity.
        var canvas = (Canvas)XamlReader.Load("""
            <Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="16" Height="16">
              <Path Name="Glow" StrokeThickness="3" Opacity="0" StrokeLineJoin="Round" />
              <Path Name="Fill" />
              <Path Name="Highlight" StrokeThickness="0.8" Opacity="0.45" Stroke="White" />
              <Path Name="Outline" StrokeThickness="1.1" StrokeLineJoin="Round" Stroke="{ThemeResource TextFillColorPrimaryBrush}" />
              <Path Name="Extra" StrokeThickness="1.1" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Stroke="{ThemeResource TextFillColorPrimaryBrush}" />
              <Path Name="Slash" StrokeThickness="1.3" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Stroke="{ThemeResource TextFillColorPrimaryBrush}" />
            </Canvas>
            """);
        Path Part(string name) => (Path)canvas.Children.First(c => ((FrameworkElement)c).Name == name);
        _glow = Part("Glow");
        _fill = Part("Fill");
        _highlight = Part("Highlight");
        _outline = Part("Outline");
        _extra = Part("Extra");
        _slash = Part("Slash");
        _fill.Clip = _clip;
        _highlight.Clip = _highlightClip;
        _slash.Data = Geometry(Slash);

        Content = canvas;
        ActualThemeChanged += (_, _) => ApplyColors();
        Kind = LevelIconKind.Microphone;
    }

    public LevelIconKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            bool mic = value == LevelIconKind.Microphone;
            _fill.Data = Geometry(mic ? MicBody : SpeakerBody);
            _glow.Data = Geometry(mic ? MicBody : SpeakerBody);
            _outline.Data = Geometry(mic ? MicBody : SpeakerBody);
            _highlight.Data = Geometry(mic ? "M6.6,3.2 L6.6,7.6" : "M2.4,6.8 L4.3,6.8");
            _extra.Data = Geometry(mic ? MicStand : SpeakerWaves);
            ApplyColors();
            Update();
        }
    }

    /// <summary>Off = not recorded: crossed out, never filled.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            _isOn = value;
            Update();
        }
    }

    /// <summary>The device is open but delivers no audio at all (different from silence).</summary>
    public bool NoSignal
    {
        get => _noSignal;
        set
        {
            if (_noSignal != value)
            {
                _noSignal = value;
                Update();
            }
        }
    }

    /// <summary>0 = silent (empty) … 1 = loud (full).</summary>
    public double Level
    {
        get => _level;
        set
        {
            double clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(clamped - _level) > 0.001)
            {
                _level = clamped;
                Update();
            }
        }
    }

    private void Update()
    {
        double level = _isOn && !_noSignal ? _level : 0;
        _clip.Rect = _kind == LevelIconKind.Microphone
            ? new Rect(0, MicBottom - ((MicBottom - MicTop) * level), Size, Size)        // rises from the bottom
            : new Rect(0, 0, SpeakerLeft + ((SpeakerRight - SpeakerLeft) * level), Size); // grows to the right
        _highlightClip.Rect = _clip.Rect;
        _fill.Visibility = level > 0 ? Visibility.Visible : Visibility.Collapsed;
        _highlight.Visibility = _fill.Visibility;

        // Only really loud sound lights up a soft halo around the outline.
        _glow.Opacity = level > 0.85 ? (level - 0.85) / 0.15 * 0.35 : 0;

        _slash.Visibility = _isOn ? Visibility.Collapsed : Visibility.Visible;
        _extra.Visibility = !_isOn && _kind == LevelIconKind.Speaker ? Visibility.Collapsed : Visibility.Visible;

        var dashes = new DoubleCollection();
        if (_isOn && _noSignal)
        {
            dashes.Add(1.5);
            dashes.Add(1.2);
        }

        _outline.StrokeDashArray = dashes;
        _outline.Opacity = _isOn && _noSignal ? 0.55 : 1;
    }

    /// <summary>Calm green / blue, a little brighter on dark backgrounds, with a soft gradient.</summary>
    private void ApplyColors()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        (Color top, Color bottom) = _kind == LevelIconKind.Microphone
            ? dark ? (Rgb(0x7E, 0xE7, 0x87), Rgb(0x2E, 0xA0, 0x43)) : (Rgb(0x4A, 0xC2, 0x6B), Rgb(0x1A, 0x7F, 0x37))
            : dark ? (Rgb(0x8C, 0xC8, 0xFF), Rgb(0x2F, 0x81, 0xF7)) : (Rgb(0x58, 0xA6, 0xFF), Rgb(0x0B, 0x5C, 0xD5));

        // Mic liquid: lighter at the surface. Speaker liquid: lighter toward the leading edge.
        _fill.Fill = new LinearGradientBrush
        {
            StartPoint = _kind == LevelIconKind.Microphone ? new Point(0.5, 0) : new Point(1, 0.5),
            EndPoint = _kind == LevelIconKind.Microphone ? new Point(0.5, 1) : new Point(0, 0.5),
            GradientStops = { new GradientStop { Color = top, Offset = 0 }, new GradientStop { Color = bottom, Offset = 1 } },
        };
        _glow.Stroke = new SolidColorBrush(top);
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

    private static Geometry Geometry(string data) =>
        (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);
}
