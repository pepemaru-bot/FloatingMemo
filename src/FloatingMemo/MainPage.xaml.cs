using System.Diagnostics;
using FloatingMemo.Controls;
using FloatingMemo.Editing;
using FloatingMemo.Services;
using FloatingMemo.Services.Audio;
using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Export;
using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Notes;
using FloatingMemo.Services.Transcription;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FloatingMemo;

/// <summary>Where the memo is in the record → transcribe cycle.</summary>
public enum VoiceState
{
    Idle,
    Recording,

    /// <summary>Stopping devices and converting audio (short).</summary>
    Processing,

    /// <summary>Local Whisper is turning the audio into text.</summary>
    Transcribing,
}

/// <summary>
/// The memo surface: tab strip, editor and audio controls. Note state (tabs, saving, routing)
/// lives in <see cref="NoteWorkspace"/>; this page renders it and connects it to the editor.
/// </summary>
public sealed partial class MainPage : Page
{
    // Save after the user pauses typing for this long.
    private static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(700);

    private readonly DispatcherQueueTimer _autosaveTimer;
    private readonly DispatcherQueueTimer _recordingClock;
    private readonly DispatcherQueueTimer _statusClear;
    private NoteWorkspace? _workspace;
    private MemoServices? _services;
    private bool _pointerOverControlBar;

    // Recording-device list (watching it never opens a device) and the last problem shown,
    // so the same warning is not repeated on every device notification.
    private IAudioDeviceCatalog? _devices;
    private string? _shownDeviceProblem;
    private string _micDeviceName = string.Empty;
    private const string DeviceProblemTitle = "マイクを使用できません";

    // Level indicators: ~30 frames per second, only while recording or testing the mic.
    private static readonly TimeSpan MicTestLimit = TimeSpan.FromSeconds(60);
    private const string MicTestTitle = "マイクテスト中";
    private readonly DispatcherQueueTimer _levelTimer;
    private readonly Stopwatch _levelClock = new();
    private readonly Stopwatch _micTestClock = new();
    private readonly LevelSmoother _micLevel = new();
    private readonly LevelSmoother _systemLevel = new();
    private AudioSources _recordingSources;
    private MicrophoneTest? _micTest;
    private bool _applyingPreferences;
    private AudioRecorder? _recorder;

    // The editor in use: the plain text box at first, the Markdown editor once WebView2 is ready.
    private readonly TextBoxEditor _plainEditor;
    private WebMarkdownEditor? _markdownEditor;
    private IMemoEditor _editor;
    private LocalWhisperTranscriptionService? _transcriber;
    private CancellationTokenSource? _transcriptionCts;

    // The note a recording belongs to: fixed when recording starts, so the transcript goes
    // there even if the user switches or closes tabs in the meantime.
    private string? _recordingNoteId;

    // Audio whose transcription failed or was cancelled, kept so the user can retry.
    private RecordedAudio? _pendingAudio;

    public MainPage()
    {
        InitializeComponent();

        _plainEditor = new TextBoxEditor(MemoTextBox);
        _editor = _plainEditor;
        Attach(_plainEditor);
#if TEST_HOOKS
        CreateTestMirrors();
#endif

        _autosaveTimer = DispatcherQueue.CreateTimer();
        _autosaveTimer.Interval = AutosaveDelay;
        _autosaveTimer.IsRepeating = false;
        _autosaveTimer.Tick += async (_, _) => await SaveActiveAsync();

        _recordingClock = DispatcherQueue.CreateTimer();
        _recordingClock.Interval = TimeSpan.FromMilliseconds(250);
        _recordingClock.Tick += (_, _) => UpdateRecordingText();

        MicIcon.Kind = LevelIconKind.Microphone;
        SystemAudioIcon.Kind = LevelIconKind.Speaker;
        _levelTimer = DispatcherQueue.CreateTimer();
        _levelTimer.Interval = TimeSpan.FromMilliseconds(33);
        _levelTimer.Tick += (_, _) => UpdateLevels();

        _statusClear = DispatcherQueue.CreateTimer();
        _statusClear.Interval = TimeSpan.FromSeconds(1.5);
        _statusClear.IsRepeating = false;
        _statusClear.Tick += (_, _) => UpdateRecordingText();

        // On first launch the window activates before this page is in the visual tree,
        // so the window's focus request is too early; focus once loaded instead.
        Loaded += async (_, _) =>
        {
            CreateShadows();
            ApplyAppearance();
            FocusEditor();
            await StartMarkdownEditorAsync();
        };
        SizeChanged += (_, _) => UpdateTabScrollerWidth();
        ActualThemeChanged += (_, _) =>
        {
            RenderTabs(); // tab chips use theme-specific glass
            CreateShadows();
            ApplyAppearance();
            var (theme, paper) = EditorColors();
            _markdownEditor?.SetTheme(theme, paper);
        };
    }

    /// <summary>
    /// True while the IME has unconfirmed (composing) text. Esc must go to the
    /// IME to cancel the conversion instead of hiding the window.
    /// </summary>
    public bool IsComposing => _editor.IsComposing;

    /// <summary>Esc was pressed inside the Markdown editor (the window's accelerator cannot see it).</summary>
    public event EventHandler? HideRequested;

    public VoiceState VoiceState { get; private set; } = VoiceState.Idle;

    /// <summary>The empty part of the tab strip, used by the window as its drag handle.</summary>
    public UIElement TitleBarDragRegion => DragHandle;

    public string? ActiveNoteTitle => _workspace?.Active?.Title;

    /// <summary>Raised on the UI thread whenever <see cref="VoiceState"/> changes.</summary>
    public event EventHandler? VoiceStateChanged;

    /// <summary>The active tab or its title changed.</summary>
    public event EventHandler? ActiveNoteChanged;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var services = (MemoServices)e.Parameter;
        _services = services;

        // Restore the last audio source choice (it never starts recording by itself).
        _applyingPreferences = true;
        MicToggle.IsChecked = services.Preferences.RecordMicrophone;
        SystemAudioToggle.IsChecked = services.Preferences.RecordSystemAudio;
        _applyingPreferences = false;
        UpdateSourceIcons();
        _recorder = services.Recorder;
        _recorder.SourceFailed += OnSourceFailed;

        // Device notifications arrive on a COM thread.
        _devices = services.Devices;
        _devices.DevicesChanged += (_, _) => DispatcherQueue.TryEnqueue(() => UpdateMicrophoneDevice(notify: true));
        UpdateMicrophoneDevice(notify: true);
        _transcriber = services.Transcriber;

        _workspace = services.Workspace;
        _workspace.TabsChanged += (_, _) => RenderTabs();
        _workspace.ActiveChanged += (_, _) => ShowActiveNote();
        _workspace.Restore();
    }

    /// <summary>Moves keyboard focus to the editor, keeping the caret position.</summary>
    public void FocusEditor()
    {
        if (_workspace is { Active: null })
        {
            EmptyOpenListButton.Focus(FocusState.Programmatic); // no note: keyboard on "open list"
            return;
        }

        _editor.Focus();
        // When the window is (re)activated, XAML restores focus to the previously focused
        // control after this call runs; repeat once that has happened so the editor wins.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _editor.Focus());
    }

    // ----- Editor ----------------------------------------------------------------

    private void Attach(IMemoEditor editor)
    {
        editor.TextChanged += OnEditorTextChanged;
        editor.CompositionChanged += OnEditorCompositionChanged;
        editor.InsertRejected += OnEditorInsertRejected;
        editor.CommandInvoked += async (_, name) => await ExecuteCommandAsync(name);
    }

    /// <summary>
    /// Loads the Markdown live-preview editor in the background. Until it is ready (or if
    /// WebView2 is unavailable) the plain text box keeps working, so typing is never blocked.
    /// </summary>
    private async Task StartMarkdownEditorAsync()
    {
        if (_markdownEditor is not null)
        {
            return;
        }

        string userData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatingMemo", "WebView2");
        // The body is white paper in light and dark mode alike, so the editor uses its light
        // colors (dark text, dark cursor). In high contrast the page follows the system colors.
        var (theme, paper) = EditorColors();
        var editor = await WebMarkdownEditor.CreateAsync(EditorWebView, userData, theme, paper);
        if (editor is null)
        {
            UpdateTestMirrors();
            return; // keep the plain editor
        }

        bool hadFocus = ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), MemoTextBox);
        _markdownEditor = editor;
        Attach(editor);
        editor.Failed += (_, _) => DispatcherQueue.TryEnqueue(FallBackToPlainEditor);
#if TEST_HOOKS
        editor.FocusChanged += (_, focused) => DispatcherQueue.TryEnqueue(() => _testEditorFocus = focused);
#endif

        // The text typed so far is already in the workspace: hand it over with the caret.
        _editor = editor;
        if (_workspace?.Active is { } note)
        {
            editor.Show(note);
        }

        UpdateEditorVisibility();
        if (hadFocus || XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is null)
        {
            FocusEditor();
        }

        UpdateTestMirrors();
    }

    /// <summary>WebView2 crashed: continue in the plain text box with the latest text.</summary>
    private void FallBackToPlainEditor()
    {
        if (ReferenceEquals(_editor, _plainEditor))
        {
            return;
        }

        _editor = _plainEditor;
        UpdateEditorVisibility();
        if (_workspace?.Active is { } note)
        {
            _plainEditor.Show(note);
        }

        FocusEditor();
        ShowStatus("簡易エディタに切り替えました");
        UpdateTestMirrors();
    }

    private void OnEditorTextChanged(object? sender, EditorTextChange change)
    {
        // Ignore anything that is not from the editor in use, or for another note.
        if (!ReferenceEquals(sender, _editor) || _workspace?.Active is not { } note || change.NoteId != note.Id)
        {
            return;
        }

        string before = note.Text;
        _workspace.UpdateText(note.Id, change.Text, change.Caret);
        if (!ReferenceEquals(before, note.Text) && before != note.Text)
        {
            // Debounce: restart the countdown on every edit.
            _autosaveTimer.Stop();
            _autosaveTimer.Start();
        }

        UpdateTestMirrors();
    }

    /// <summary>Makes sure the last keystrokes have reached the workspace (before hiding/exiting).</summary>
    public Task FlushEditorAsync() => _editor.FlushAsync(settle: true);

#if TEST_HOOKS
    // Test-only (Debug / EnableTestHooks builds): mirrors of the editor's text and focus that
    // UI Automation can read, since the Markdown editor's content lives inside WebView2.
    private TextBlock? _testMirrorText;
    private TextBlock? _testMirrorFocus;
    private TextBlock? _testMirrorKind;
    private TextBlock? _testMirrorLevels;
    private TextBlock? _testMirrorCaret;
    private bool _testEditorFocus;

    private void CreateTestMirrors()
    {
        TextBlock Make(string id)
        {
            var block = new TextBlock { Opacity = 0, IsHitTestVisible = false, Width = 1, Height = 1 };
            AutomationProperties.SetAutomationId(block, id);
            Grid.SetRow(block, 1);
            ((Grid)Content).Children.Add(block);
            return block;
        }

        _testMirrorText = Make("EditorMirror");
        _testMirrorFocus = Make("EditorFocusState");
        _testMirrorKind = Make("EditorKind");
        _testMirrorLevels = Make("LevelMirror");
        _testMirrorCaret = Make("EditorCaret");

        // Lets the leak test tell real leaks from handles that are only waiting for a GC.
        var gc = new Button { Opacity = 0, Width = 1, Height = 1, IsTabStop = false };
        AutomationProperties.SetAutomationId(gc, "TestForceGC");
        AutomationProperties.SetName(gc, "TestForceGC");
        gc.Click += (_, _) =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        };
        Grid.SetRow(gc, 1);
        ((Grid)Content).Children.Add(gc);

        // "Unplugs"/"replugs" the fake USB microphone of the --fake-devices list.
        var unplug = new Button { Opacity = 0, Width = 1, Height = 1, IsTabStop = false };
        AutomationProperties.SetAutomationId(unplug, "TestToggleUsbDevice");
        AutomationProperties.SetName(unplug, "TestToggleUsbDevice");
        bool usbPresent = true;
        unplug.Click += (_, _) =>
        {
            if (_devices is StaticDeviceCatalog fake && fake.FindCapture("{fake-usb}") is not null)
            {
                usbPresent = !usbPresent;
                var all = new[] { "{fake-array}", "{fake-usb}", "{fake-mix}" }.Select(id => fake.FindCapture(id)!).ToList();
                // Like Windows: unplugging the communications default moves that role elsewhere.
                fake.Update(all.Select(d => d.Id == "{fake-usb}" ? d with { IsActive = usbPresent } : d),
                    "{fake-mix}", usbPresent ? "{fake-usb}" : "{fake-array}");
            }
        };
        Grid.SetRow(unplug, 1);
        ((Grid)Content).Children.Add(unplug);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += (_, _) => UpdateTestMirrors();
        timer.Start();
    }
#endif

    private void UpdateTestMirrors()
    {
#if TEST_HOOKS
        if (_testMirrorText is null)
        {
            return;
        }

        bool web = ReferenceEquals(_editor, _markdownEditor);
        // Same line breaks as the TextBox ("\r") so tests work with either editor.
        AutomationProperties.SetName(_testMirrorText, (_workspace?.Active?.Text ?? string.Empty).Replace('\n', '\r'));
        AutomationProperties.SetName(_testMirrorKind!, web ? "web" : "plain");
        AutomationProperties.SetName(_testMirrorCaret!, _markdownEditor?.CaretColor ?? string.Empty);
        bool focused = web ? _testEditorFocus : XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), MemoTextBox);
        AutomationProperties.SetName(_testMirrorFocus!, focused ? "focused" : "blurred");
#endif
    }

    /// <summary>Dims the tab strip while the window is in the background.</summary>
    public void SetWindowActive(bool active)
    {
        TabStrip.Opacity = active ? 1.0 : 0.6;
        if (active && _workspace?.RefreshActiveFromDisk() == true)
        {
            // Another program changed the note while we were hidden and we had no edits.
            ShowActiveNote();
        }
    }

    /// <summary>Saves every changed note (e.g. when hiding). Failures are shown, not thrown.</summary>
    public async Task FlushAsync()
    {
        _autosaveTimer.Stop();
        if (_workspace is null)
        {
            return;
        }

        await _editor.FlushAsync();

        foreach (var tab in _workspace.Tabs.Where(t => t.IsDirty).ToList())
        {
            HandleSaveResult(await _workspace.SaveAsync(tab));
        }

        _workspace.SaveSession();
    }

    /// <summary>
    /// Saves all notes synchronously before the app exits. Returns false with the error message on failure.
    /// </summary>
    public bool TryFlushForExit(out string? error)
    {
        _autosaveTimer.Stop();
        error = null;
        if (_workspace is null)
        {
            return true;
        }

        var failures = _workspace.SaveAllForExit();
        if (failures.Count == 0)
        {
            return true;
        }

        error = string.Join("\n", failures.Select(f => $"{f.Tab.Title}: {f.Error!.Message}"));
        return false;
    }

    /// <summary>Stops any recording/transcription and discards the audio (used when exiting).</summary>
    public void DiscardRecording()
    {
        _minutesCts?.Cancel();
        StopMicrophoneTest();
        _recordingClock.Stop();
        _recorder?.Cancel();
        _transcriptionCts?.Cancel();
        SetPendingAudio(null);
        SetVoiceState(VoiceState.Idle);
    }

    // ----- Tabs ------------------------------------------------------------------

    /// <summary>The controls of one tab, created once per open note and then only updated.</summary>
    private sealed class TabView
    {
        public required Button Button { get; init; }
        public required TextBlock Title { get; init; }
        public required Button Close { get; init; }
        public required Ellipse RecordingDot { get; init; }
        public required FontIcon Warning { get; init; }
    }

    // Tabs are reused rather than rebuilt: re-creating XAML controls on every state change
    // (recording start/stop, tab switch) slowly leaked native resources in WinUI 3.
    private readonly Dictionary<string, TabView> _tabViews = new(StringComparer.Ordinal);

    /// <summary>Brings the tab strip in line with the workspace: order, titles, active tab, marks.</summary>
    private void RenderTabs()
    {
        if (_workspace is null)
        {
            return;
        }

        var open = _workspace.Tabs.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string gone in _tabViews.Keys.Where(id => !open.Contains(id)).ToList())
        {
            TabItemsPanel.Children.Remove(_tabViews[gone].Button);
            _tabViews.Remove(gone);
        }

        for (int i = 0; i < _workspace.Tabs.Count; i++)
        {
            var note = _workspace.Tabs[i];
            if (!_tabViews.TryGetValue(note.Id, out var view))
            {
                view = CreateTabView(note.Id);
                _tabViews[note.Id] = view;
            }

            int current = TabItemsPanel.Children.IndexOf(view.Button);
            if (current != i)
            {
                if (current >= 0)
                {
                    TabItemsPanel.Children.RemoveAt(current);
                }

                TabItemsPanel.Children.Insert(i, view.Button);
            }

            UpdateTabView(view, note, ReferenceEquals(note, _workspace.Active));
        }

        UpdateTabScrollerWidth();
        ActiveNoteChanged?.Invoke(this, EventArgs.Empty);
    }

    private TabView CreateTabView(string noteId)
    {
        var title = new TextBlock
        {
            MaxWidth = 150,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        var warning = new FontIcon { Glyph = "\uE7BA", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            AllowFocusOnInteraction = false,
            Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
            Tag = noteId,
        };
        ToolTipService.SetToolTip(close, "タブを閉じる (Ctrl+W)。ノートは削除されません");
        close.Click += OnTabCloseClick;

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(dot);
        content.Children.Add(warning);
        content.Children.Add(title);
        content.Children.Add(close);

        var button = new Button
        {
            Height = 26,
            Margin = new Thickness(0, 3, 0, 0),
            Padding = new Thickness(10, 0, 2, 0),
            CornerRadius = new CornerRadius(6),
            AllowFocusOnInteraction = false,
            Content = content,
            Tag = noteId,
        };
        AutomationProperties.SetAutomationId(button, "NoteTab");
        // Handlers use the sender's Tag (note id) instead of capturing other controls.
        button.Click += OnTabClick;
        button.DoubleTapped += OnTabDoubleTapped;
        button.PointerEntered += OnTabPointerEntered;
        button.PointerExited += OnTabPointerExited;

        return new TabView { Button = button, Title = title, Close = close, RecordingDot = dot, Warning = warning };
    }

    private void UpdateTabView(TabView view, OpenNote note, bool active)
    {
        view.Title.Text = note.Title;
        // Unselected tabs: subdued (frosted) text; the colour itself follows the theme.
        view.Title.Opacity = active ? 1.0 : 0.62;
        view.Close.Opacity = active ? 0.8 : 0.0;
        // The note the current recording/transcription will be inserted into.
        view.RecordingDot.Visibility = note.Id == _recordingNoteId && VoiceState != VoiceState.Idle ? Visibility.Visible : Visibility.Collapsed;
        view.Warning.Visibility = note.HasConflict || note.LoadError is not null ? Visibility.Visible : Visibility.Collapsed;
        view.Button.Background = active ? GlassBrush("TabChipActiveBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        view.Button.BorderBrush = active ? GlassBrush("TabChipBorderBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        view.Button.BorderThickness = new Thickness(active ? 1 : 0);
        AutomationProperties.SetName(view.Button, note.Title);
        AutomationProperties.SetItemStatus(view.Button, active ? "選択中" : string.Empty);
        AutomationProperties.SetName(view.Close, $"{note.Title} を閉じる");
        ToolTipService.SetToolTip(view.Button, $"{note.Title}\n{note.Info.FilePath}\nダブルクリックで名前を変更 (F2)");
    }

    private static string TabId(object sender) => (string)((FrameworkElement)sender).Tag;

    /// <summary>
    /// The editor page's colours: light text on the paper colour of the current theme; in high
    /// contrast nothing is forced, so the system colours apply.
    /// </summary>
    private (string? Theme, string? Paper) EditorColors()
    {
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            return (null, null);
        }

        // The page paints exactly the card's color, so the WebView's rectangle cannot be seen.
        var color = PaperColor();
        return ("light", $"#{color.R:x2}{color.G:x2}{color.B:x2}");
    }

    /// <summary>
    /// Soft, blurred shadows under the paper and under the bottom glass pane, so they read as
    /// layers lying on the glass. XAML has no blur, so these are composition drop shadows: a
    /// visual the size of its host casts a blurred shadow. No shadows in high contrast (flat, system colors only).
    /// </summary>
    private void CreateShadows()
    {
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        bool dark = ActualTheme == ElementTheme.Dark;
        AddDropShadow(PaperShadowHost, cornerRadius: 10, blur: 20, opacity: highContrast ? 0 : dark ? 0.45f : 0.16f, offsetY: 5);
        AddDropShadow(BarShadowHost, cornerRadius: 10, blur: 12, opacity: highContrast ? 0 : dark ? 0.35f : 0.10f, offsetY: 3);
    }

    private static void AddDropShadow(Border host, float cornerRadius, float blur, float opacity, float offsetY)
    {
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(host).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = blur;
        shadow.Opacity = opacity;
        shadow.Offset = new System.Numerics.Vector3(0, offsetY, 0);
        shadow.Color = Microsoft.UI.Colors.Black;

        // The shadow's shape is a rounded rectangle exactly like the card (a rectangle would cast
        // square corners). It is drawn into a surface and used as the shadow's mask; the visual
        // itself draws nothing, so nothing shows through the translucent paper or pane.
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new System.Numerics.Vector2(cornerRadius, cornerRadius);
        var shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = compositor.CreateColorBrush(Microsoft.UI.Colors.Black);
        var shapeVisual = compositor.CreateShapeVisual();
        shapeVisual.Shapes.Add(shape);
        var surface = compositor.CreateVisualSurface();
        surface.SourceVisual = shapeVisual;
        var mask = compositor.CreateSurfaceBrush(surface);
        mask.Stretch = Microsoft.UI.Composition.CompositionStretch.Fill;
        shadow.Mask = mask;

        var visual = compositor.CreateSpriteVisual();
        visual.Brush = compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        visual.RelativeSizeAdjustment = System.Numerics.Vector2.One; // follows the host's size
        visual.Shadow = shadow;
        void Resize()
        {
            var size = new System.Numerics.Vector2((float)host.ActualWidth, (float)host.ActualHeight);
            geometry.Size = size;
            shapeVisual.Size = size;
            surface.SourceSize = size;
        }

        Resize();
        host.SizeChanged += (_, _) => Resize();
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(host, visual);
    }

    // ----- Appearance (glass clarity, paper milkiness) -----------------------------------------

    /// <summary>Raised when the user changes the glass clarity (the window adjusts its backdrop).</summary>
    public event EventHandler? AppearanceChanged;

    /// <summary>
    /// Applies the two appearance settings: how clear the outer glass is (0 = frosted, 1 = clear)
    /// and how milky the paper is (0 = white, 1 = milky blue-grey). The paper is opaque: WebView2
    /// cannot be made transparent here, and readability comes first.
    /// </summary>
    public void ApplyAppearance()
    {
        if (_services is null)
        {
            return;
        }

        double clarity = Math.Clamp(_services.Preferences.GlassClarity, 0, 1);
        VesselTint.Opacity = 1.3 - (0.8 * clarity);       // clearer glass: weaker tint layer
        BarGlass.Opacity = 1.25 - (0.5 * clarity);

        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        if (!highContrast)
        {
            PaperCard.Background = new SolidColorBrush(PaperColor());
        }

        var (theme, paperColor) = EditorColors();
        _markdownEditor?.SetTheme(theme, paperColor);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The paper color: between white and the theme's milky blue-grey (PaperBrush), by the
    /// "milkiness" setting. Opaque, identical for the card, the page and WebView2's own background.
    /// </summary>
    private Windows.UI.Color PaperColor()
    {
        var milky = ((SolidColorBrush)GlassBrush("PaperBrush")).Color;
        double k = Math.Clamp(_services?.Preferences.PaperMilkiness ?? 0.6, 0, 1);
        byte Mix(byte to) => (byte)Math.Round(255 + ((to - 255) * k));
        return Windows.UI.Color.FromArgb(255, Mix(milky.R), Mix(milky.G), Mix(milky.B));
    }

    /// <summary>A glass brush from this page's theme dictionaries for the current theme.</summary>
    private Brush GlassBrush(string key)
    {
        string theme = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? "HighContrast"
            : ActualTheme == ElementTheme.Light ? "Light" : "Default";
        return (Brush)((ResourceDictionary)Resources.ThemeDictionaries[theme])[key];
    }

    private void OnTabClick(object sender, RoutedEventArgs e) => SwitchTo(TabId(sender));

    private async void OnTabCloseClick(object sender, RoutedEventArgs e) => await CloseTabAsync(TabId(sender));

    private void OnTabDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ShowRenameFlyout(TabId(sender));

    private void OnTabPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_tabViews.TryGetValue(TabId(sender), out var view))
        {
            view.Close.Opacity = 0.8;
        }
    }

    private void OnTabPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_tabViews.TryGetValue(TabId(sender), out var view))
        {
            view.Close.Opacity = _workspace?.Active?.Id == TabId(sender) ? 0.8 : 0.0;
        }
    }
    private double _captionButtonsWidth = 138; // three caption buttons at 100 % until measured

    /// <summary>Width of Windows' minimize/maximize/close buttons (set by the window).</summary>
    public double CaptionButtonsWidth
    {
        set
        {
            if (value > 0 && Math.Abs(value - _captionButtonsWidth) > 0.5)
            {
                _captionButtonsWidth = value;
                UpdateTabScrollerWidth();
            }
        }
    }

    /// <summary>
    /// Only the tabs scroll. "+" and the note list stay right after them, and a strip for
    /// dragging the window plus the caption buttons' width is always kept free.
    /// </summary>
    private void UpdateTabScrollerWidth()
    {
        const double ButtonsWidth = 58;   // "+" and the note list
        const double MinimumDragArea = 40;
        TabScroller.MaxWidth = Math.Max(60, ActualWidth - ButtonsWidth - MinimumDragArea - _captionButtonsWidth);
        ScrollActiveTabIntoView();
    }

    private void OnTabScrollerWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(TabScroller).Properties.MouseWheelDelta;
        TabScroller.ChangeView(TabScroller.HorizontalOffset - delta, null, null, disableAnimation: true);
        e.Handled = true;
    }

    /// <summary>Scrolls the tab strip so the active tab is fully visible (after layout has run).</summary>
    private void ScrollActiveTabIntoView()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (TabItemsPanel.Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == _workspace?.Active?.Id) is not { } button
                || button.ActualWidth == 0)
            {
                return;
            }

            double left = button.TransformToVisual(TabItemsPanel).TransformPoint(default).X;
            double right = left + button.ActualWidth;
            double offset = TabScroller.HorizontalOffset;
            double viewport = TabScroller.ViewportWidth;
            if (left < offset)
            {
                TabScroller.ChangeView(left, null, null, disableAnimation: true);
            }
            else if (right > offset + viewport)
            {
                TabScroller.ChangeView(right - viewport, null, null, disableAnimation: true);
            }
        });
    }

    /// <summary>Loads the active note into the editor (text + caret).</summary>
    private void ShowActiveNote()
    {
        UpdateEditorVisibility();
        if (_workspace?.Active is not { } note)
        {
            // Every tab was closed: nothing to edit, nothing created.
            SaveErrorBar.IsOpen = false;
            RenderTabs();
            UpdateTestMirrors();
            FocusEditor();
            return;
        }

        // A note that could not be read is shown read-only so it cannot be overwritten.
        _editor.Show(note);
        UpdateTestMirrors();

        if (note.LoadError is not null)
        {
            ShowSaveError("ノートを読み込めませんでした", new IOException(note.LoadError), conflictTab: null);
        }
        else if (note.HasConflict)
        {
            ShowConflict(note);
        }
        else
        {
            SaveErrorBar.IsOpen = false;
        }

        RenderTabs();
        ScrollActiveTabIntoView();
        FocusEditor();
    }

    /// <summary>The editor in use is shown only while a note is open; otherwise the empty state.</summary>
    private void UpdateEditorVisibility()
    {
        bool hasNote = _workspace?.Active is not null;
        bool web = ReferenceEquals(_editor, _markdownEditor);
        EditorWebView.Visibility = hasNote && web ? Visibility.Visible : Visibility.Collapsed;
        MemoTextBox.Visibility = hasNote && !web ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasNote ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Switches tabs; the note being left is saved first.</summary>
    private void SwitchTo(string id)
    {
        if (_workspace?.Active?.Id != id)
        {
            SaveLeavingNote();
        }

        _workspace?.Activate(id);
    }

    /// <summary>
    /// Before another note is shown, the one being left is saved (its text was captured on
    /// every edit, so this cannot pick up the next note's text).
    /// </summary>
    private void SaveLeavingNote()
    {
        _autosaveTimer.Stop();
        if (_workspace?.Active is { IsDirty: true } leaving)
        {
            _ = SaveAndReportAsync(leaving);
        }
    }

    private async void OnNewNoteClick(object sender, RoutedEventArgs e) => await NewNoteAsync();

    private Task NewNoteAsync()
    {
        SaveLeavingNote();
        _workspace?.NewNote();
        return Task.CompletedTask;
    }

    private async Task CloseTabAsync(string id)
    {
        if (_workspace is null)
        {
            return;
        }

        if (_workspace.Active?.Id == id)
        {
            _autosaveTimer.Stop();
            await _editor.FlushAsync(); // the last keystrokes must be saved with the note
        }

        var result = await _workspace.CloseAsync(id);
        if (result is { Success: false })
        {
            // The tab stays open with its text; nothing is lost.
            HandleSaveResult(result);
        }
        else
        {
            _editor.Forget(id); // drops that note's undo history in the editor
        }
    }

    // ----- AI minutes (local LLM) ---------------------------------------------------------

    private CancellationTokenSource? _minutesCts;
    private const string MinutesTitle = "AI議事録を作成中";

    private enum MinutesOutcome
    {
        Done,
        Failed,
        Cancelled,
    }

    private readonly System.Diagnostics.Stopwatch _minutesClock = new();
    private DispatcherQueueTimer? _minutesTimer;
    private MinutesProgress? _minutesStep;
    private int _minutesSteps;

    /// <summary>Shows the minutes job in the control strip; the elapsed time ticks every second.</summary>
    private void StartMinutesIndicator(bool withCorrection)
    {
        _minutesSteps = withCorrection ? 2 : 1;
        _minutesStep = null;
        _minutesClock.Restart();
        if (_minutesTimer is null)
        {
            _minutesTimer = DispatcherQueue.CreateTimer();
            _minutesTimer.Interval = TimeSpan.FromSeconds(1);
            _minutesTimer.Tick += (_, _) => UpdateMinutesIndicator();
        }

        _minutesTimer.IsRepeating = true;
        _minutesTimer.Start();
        MinutesBar.Visibility = Visibility.Visible;
        MinutesStateIcon.Visibility = Visibility.Collapsed;
        MinutesIndicator.Visibility = Visibility.Visible;
        UpdateMinutesIndicator();
        UpdateControlBarEmphasis();
    }

    /// <summary>"議事録 校正 1/2 0:12 230字": the step, its part, elapsed time, characters written.</summary>
    private void UpdateMinutesIndicator()
    {
        if (_minutesCts is null)
        {
            return;
        }

        string elapsed = FormatDuration(_minutesClock.Elapsed);
        var p = _minutesStep;
        string step = p switch
        {
            null => "準備中",
            { LoadingModel: true } => "AIモデルを読み込み中",
            _ => p.Step,
        };
        int stepNumber = p?.Step == "議事録を作成" ? _minutesSteps : 1;
        string parts = p is { Parts: > 1 } ? $" {p.Part}/{p.Parts}" : string.Empty;
        string chars = p is { Characters: > 0 } ? $"・{p.Characters}字" : string.Empty;
        // Short on screen (the strip is narrow); the full wording is in the tooltip and for screen readers.
        string shortStep = p switch
        {
            null => "準備中",
            { LoadingModel: true } => "読込中",
            { Step: "文字起こしを校正" } => "校正",
            _ => "作成",
        };
        string full = $"AI議事録 工程{stepNumber}/{_minutesSteps}：{step}{parts}・{elapsed}{chars}";
        MinutesStatusText.Text = $"AI議事録 {stepNumber}/{_minutesSteps} {shortStep}{parts} {elapsed}{chars}";
        AutomationProperties.SetName(MinutesStatusText, full);
        ToolTipService.SetToolTip(MinutesIndicator,
            full + "\nAIの処理は進み具合の割合を正確に計算できないため、工程・経過時間・生成した文字数を表示しています。");
    }

    /// <summary>Done / failed / cancelled stays visible for a few seconds, then the strip clears.</summary>
    private void FinishMinutesIndicator(MinutesOutcome outcome)
    {
        _minutesTimer?.Stop();
        _minutesClock.Stop();
        MinutesBar.Visibility = Visibility.Collapsed;
        MinutesStateIcon.Visibility = Visibility.Visible;
        AutomationProperties.SetName(MinutesStatusText, string.Empty); // the text itself is the name again
        (MinutesStateIcon.Glyph, MinutesStatusText.Text) = outcome switch
        {
            MinutesOutcome.Done => ("\uE73E", $"AI議事録 完了（{FormatDuration(_minutesClock.Elapsed)}）"),
            MinutesOutcome.Cancelled => ("\uE711", "AI議事録 中止"),
            _ => ("\uE783", "AI議事録 失敗"),
        };
        var clear = DispatcherQueue.CreateTimer();
        clear.Interval = TimeSpan.FromSeconds(outcome == MinutesOutcome.Failed ? 8 : 5);
        clear.IsRepeating = false;
        clear.Tick += (_, _) =>
        {
            if (_minutesCts is null)
            {
                MinutesIndicator.Visibility = Visibility.Collapsed;
                UpdateControlBarEmphasis();
            }
        };
        clear.Start();
    }

    /// <summary>The menu reflects the open note: create minutes from a transcript, or switch to its pair.</summary>
    private void OnMinutesMenuOpening(object? sender, object e)
    {
        MinutesMenu.Items.Clear();
        var active = _workspace?.Active;
        bool isMinutes = active is not null && MinutesNotes.IsMinutes(active.Title, active.Text);

        var create = new MenuFlyoutItem
        {
            Text = _minutesCts is null ? "このノートからAI議事録を作成" : "AI議事録を作成中…",
            IsEnabled = active is not null && !isMinutes && _minutesCts is null,
        };
        AutomationProperties.SetAutomationId(create, "MinutesCreate");
        create.Click += async (_, _) => await CreateMinutesAsync();
        MinutesMenu.Items.Add(create);

        var pair = PairOf(active);
        var toggle = new MenuFlyoutItem
        {
            Text = isMinutes ? "文字起こし原文を表示" : "AI議事録を表示",
            IsEnabled = pair is not null,
        };
        AutomationProperties.SetAutomationId(toggle, "MinutesToggle");
        toggle.Click += (_, _) => ToggleMinutes();
        MinutesMenu.Items.Add(toggle);

        var export = new MenuFlyoutItem { Text = "PDF / Word に書き出し…", IsEnabled = active is not null };
        export.KeyboardAcceleratorTextOverride = "Ctrl+Shift+E";
        AutomationProperties.SetAutomationId(export, "MinutesExport");
        export.Click += async (_, _) => await ExportAsync();
        MinutesMenu.Items.Add(export);

        MinutesMenu.Items.Add(new MenuFlyoutSeparator());
        var settings = new MenuFlyoutItem { Text = "AIモデル・専門用語辞書の設定…" };
        AutomationProperties.SetAutomationId(settings, "MinutesSettings");
        settings.Click += (_, _) => ShowSettings(null);
        MinutesMenu.Items.Add(settings);
        if (_services?.Dictionary.Data.Candidates.Count is > 0 and var pending)
        {
            var candidates = new MenuFlyoutItem { Text = $"辞書の登録候補（{pending} 件）を確認…" };
            candidates.Click += (_, _) => ShowSettings("candidates");
            MinutesMenu.Items.Add(candidates);
        }

        MinutesMenu.Items.Add(new MenuFlyoutItem
        {
            Text = "原文は変更されず、議事録は別のノートとして保存されます。処理はすべてこのPC内です。",
            IsEnabled = false,
        });
    }

    private SettingsWindow? _settingsWindow;

    /// <summary>Opens (or brings forward) the settings window, optionally on one section.</summary>
    public void ShowSettings(string? section)
    {
        if (_services is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_services, ApplyAppearance);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        if (section is not null)
        {
            _settingsWindow.ShowSection(section);
        }

        _settingsWindow.Activate();
    }

    /// <summary>The transcript for minutes, or the minutes for a transcript.</summary>
    private NoteInfo? PairOf(OpenNote? note)
    {
        if (note is null || _workspace is null)
        {
            return null;
        }

        return MinutesNotes.IsMinutes(note.Title, note.Text)
            ? MinutesNotes.FindSource(_workspace.Library.Notes, note.Title, note.Text)
            : MinutesNotes.FindMinutes(_workspace.Library.Notes, note.Title);
    }

    /// <summary>Switches between a transcript and its AI minutes (opening the other as a tab).</summary>
    private void ToggleMinutes()
    {
        if (PairOf(_workspace?.Active) is { } pair)
        {
            SaveLeavingNote();
            _workspace!.Open(pair.Id);
        }
    }

    /// <summary>
    /// Makes a minutes draft from the open note with the local LLM and saves it as a new note
    /// next to it. The transcript itself is never changed; the user reviews the draft.
    /// </summary>
    /// <param name="withoutTranscript">The user confirmed making minutes from a note that has no transcript.</param>
    private async Task CreateMinutesAsync(bool withoutTranscript = false)
    {
        if (_workspace?.Active is not { } source || _services is null || _minutesCts is not null)
        {
            return;
        }

        // The transcript is added only after recording, audio preparation and transcription have
        // all finished. Starting earlier summarizes the note without it (input silently missing).
        if (VoiceState != VoiceState.Idle)
        {
            ShowAudioMessage(InfoBarSeverity.Warning, "録音と文字起こしが終わってから作成してください",
                "文字起こしがノートに追加される前に作成すると、文字起こしの内容が議事録に入りません。");
            return;
        }

        if (_transcriptsWaitingForIme.Count > 0 || IsComposing)
        {
            ShowAudioMessage(InfoBarSeverity.Warning, "日本語入力の変換を確定してください",
                "変換中のため、届いた文字起こしをまだノートに追加していません。確定してから作成してください。");
            return;
        }

        // The latest keystrokes belong to the transcript that is summarized.
        _autosaveTimer.Stop();
        if (!await _editor.FlushAsync())
        {
            ShowAudioMessage(InfoBarSeverity.Warning, "エディタの内容を確認できませんでした",
                "最新の本文を受け取れなかったため作成していません。少し待ってからもう一度お試しください。");
            return;
        }

        if (source.IsDirty)
        {
            HandleSaveResult(await _workspace.SaveAsync(source));
        }

        string transcript = source.Text;
        if (transcript.Trim().Length < 20)
        {
            ShowAudioMessage(InfoBarSeverity.Warning, "原文が短すぎます", "文字起こしのあるノートで作成してください。");
            return;
        }

        // A note without any "以下、AIによる文字起こし" block: say so instead of quietly making
        // minutes from the person's notes alone.
        var snapshot = MinutesInputSnapshot.Of(source.Id, transcript);
        if (!snapshot.HasTranscript && !withoutTranscript)
        {
            var anyway = new Button { Content = "メモだけで作成" };
            AutomationProperties.SetAutomationId(anyway, "MinutesWithoutTranscript");
            anyway.Click += async (_, _) =>
            {
                AudioInfoBar.IsOpen = false;
                await CreateMinutesAsync(withoutTranscript: true);
            };
            ShowAudioMessage(InfoBarSeverity.Warning, "このノートには文字起こしがありません",
                "「以下、AIによる文字起こし」の部分が見つかりません。録音の文字起こしが別のノートに入っていないか確認してください。"
                + "手で書いたメモだけから作る場合は「メモだけで作成」を押してください。", anyway);
            return;
        }

        if (_services.CreateMinutesModel() is not { } model)
        {
            ShowAudioMessage(InfoBarSeverity.Warning, "AIモデルがありません",
                $"Qwen3 の GGUF モデル（{string.Join(" / ", LlamaTextGenerator.KnownModels)}）を README の手順でダウンロードして {LlamaTextGenerator.ModelsDirectory} に置いてください。メモの編集・保存・書き出しはモデルなしで使えます。");
            return;
        }

        var cts = new CancellationTokenSource();
        _minutesCts = cts;
        var cancel = new Button { Content = "中止" };
        AutomationProperties.SetAutomationId(cancel, "MinutesCancel");
        cancel.Click += (_, _) => cts.Cancel();
        // Step 1 (optional): correct misrecognitions with the dictionary and the context.
        // Step 2: write the minutes from the corrected text. Separate prompts, same local model.
        IReadOnlyList<TermEntry>? dictionary = _services.Preferences.CorrectTranscriptBeforeMinutes ? _services.Dictionary.ActiveEntries : null;
        ShowAudioMessage(InfoBarSeverity.Informational, MinutesTitle,
            $"「{source.Title}」をPC内のAI（{model.ModelName}）で"
            + (dictionary is null ? "整理" : "校正（辞書 " + dictionary.Count + " 語から関係する語のみ使用）してから整理")
            + "しています。1〜数分かかります。原文は変更されません。", cancel);
        string sourceTitle = source.Title;
        StartMinutesIndicator(dictionary is not null);
        var minutesProgress = new Progress<MinutesProgress>(p =>
        {
            _minutesStep = p;
            UpdateMinutesIndicator();
        });
        try
        {
            string noteId = source.Id;
            var draft = await Task.Run(() => new MinutesGenerator(model, MinutesStyles.For(model.ModelName)).GenerateAsync(transcript, sourceTitle, DateTime.Now,
                minutesProgress, cts.Token, dictionary, noteId));
            draft.Run?.Append(DateTime.Now); // sizes, parts and finish reasons only (%TEMP%, not the note text)
            var (tab, saved) = await _workspace.CreateNoteAsync(MinutesNotes.TitleFor(sourceTitle), draft.Markdown);
            HandleSaveResult(saved);
            if (draft.Correction is { } correction)
            {
                // Remember the corrected text as written, to learn from the user's fixes later.
                CorrectionLearning.SaveSnapshot(_workspace.Library.MetaDirectory, tab.Id, correction.Annotated.Trim());

                // AI fixes of spellings the dictionary did not list yet are offered for approval.
                foreach (var d in correction.Decisions.Where(d => d.Status == CorrectionStatus.Applied && d.Term is { } t && !t.Misrecognitions.Contains(d.Proposal.From)))
                {
                    _services.Dictionary.AddCandidate(d.Proposal.From, d.Term!.Term, "AI校正", tab.Title);
                }
            }
            FinishMinutesIndicator(MinutesOutcome.Done);
            ShowAudioMessage(InfoBarSeverity.Success, "AI議事録の下書きを作成しました",
                (draft.Warnings.Count > 0 ? $"要確認の箇所が {draft.Warnings.Count} 件あります（⚠ の付いた項目）。" : string.Empty)
                + $"「{tab.Title}」として保存しました。原文と照らし合わせて確認してください。",
                details: string.Join("\n", draft.Warnings));
        }
        catch (OperationCanceledException)
        {
            FinishMinutesIndicator(MinutesOutcome.Cancelled);
            ShowAudioMessage(InfoBarSeverity.Informational, "AI議事録の作成を中止しました", "原文はそのままです。");
        }
        catch (Exception ex)
        {
            FinishMinutesIndicator(MinutesOutcome.Failed);
            // Model file broken, out of memory, …: the transcript is untouched either way.
            ShowAudioMessage(InfoBarSeverity.Error, "AI議事録を作成できませんでした", "原文はそのままです。", details: ex.Message);
        }
        finally
        {
            _minutesCts = null;
            cts.Dispose();
            (model as IDisposable)?.Dispose(); // frees the model's memory (~2–3 GB)
        }
    }

    // ----- Export to PDF / Word -----------------------------------------------------------

    /// <summary>The main window's handle (set by the window; file pickers and the PDF export need it).</summary>
    public IntPtr WindowHandle { get; set; }

    /// <summary>
    /// Exports the open note (or only its AI minutes) as an A4 PDF or an editable .docx. The note
    /// is only read: its Markdown is never changed, and a failed export leaves no partial file.
    /// </summary>
    private async Task ExportAsync()
    {
        if (_workspace?.Active is not { } active || XamlRoot is null)
        {
            ShowAudioMessage(InfoBarSeverity.Warning, "書き出すノートがありません", "ノートを開いてから書き出してください。");
            return;
        }

        // The latest keystrokes belong in the export.
        _autosaveTimer.Stop();
        await _editor.FlushAsync();

        // "AI minutes only": this note if it is minutes, otherwise its newest minutes note.
        string? minutesText = MinutesNotes.IsMinutes(active.Title, active.Text) ? active.Text
            : PairOf(active) is { } pair ? (_workspace.Tabs.FirstOrDefault(t => t.Id == pair.Id)?.Text ?? NoteCatalog.ReadForSearch(pair)) : null;
        string minutesTitle = MinutesNotes.IsMinutes(active.Title, active.Text) ? active.Title
            : PairOf(active)?.Title ?? active.Title;

        var format = new RadioButtons { Header = "形式", SelectedIndex = 0, Items = { "PDF（A4・印刷や共有向け）", "Word（.docx・編集向け。Google ドキュメントでも開けます）" } };
        AutomationProperties.SetAutomationId(format, "ExportFormat");
        var scope = new RadioButtons { Header = "範囲", SelectedIndex = 0, Items = { "ノート全文" } };
        scope.Items.Add(new RadioButton
        {
            Content = minutesText is null ? "AI議事録のみ（このノートの議事録はまだありません）" : "AI議事録のみ（校正の記録・校正済み文字起こしを除く）",
            IsEnabled = minutesText is not null,
        });
        AutomationProperties.SetAutomationId(scope, "ExportScope");
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(format);
        panel.Children.Add(scope);
        panel.Children.Add(new TextBlock
        {
            Text = "書き出しはこの PC の中だけで行います。元のノート（Markdown）は変更されません。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"「{active.Title}」を書き出す",
            Content = panel,
            PrimaryButtonText = "保存先を選んで書き出す",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            FocusEditor();
            return;
        }

        var exportFormat = format.SelectedIndex == 1 ? ExportFormat.Docx : ExportFormat.Pdf;
        var exportScope = scope.SelectedIndex == 1 && minutesText is not null ? ExportScope.MinutesOnly : ExportScope.WholeNote;
        string markdown = ExportContent.Select(exportScope == ExportScope.MinutesOnly ? minutesText! : active.Text, exportScope);
        string title = exportScope == ExportScope.MinutesOnly ? minutesTitle : active.Title;
        string extension = exportFormat == ExportFormat.Pdf ? ".pdf" : ".docx";
        string fileName = ExportContent.SuggestedFileName(title, exportScope);

        string? destination = await PickExportPathAsync(fileName, extension, exportFormat);
        if (destination is null)
        {
            FocusEditor();
            return;
        }

        ShowAudioMessage(InfoBarSeverity.Informational, "書き出し中…", System.IO.Path.GetFileName(destination));
        try
        {
            if (exportFormat == ExportFormat.Pdf)
            {
                await PdfExport.WriteAsync(HtmlExport.ToHtml(markdown, title), destination, WindowHandle);
            }
            else
            {
                await Task.Run(() => SafeFileWriter.Write(destination, stream => DocxExport.Write(markdown, title, stream)));
            }

            var open = new Button { Content = "フォルダを開く" };
            open.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{destination}\"") { UseShellExecute = true });
            ShowAudioMessage(InfoBarSeverity.Success, "書き出しました", $"「{System.IO.Path.GetFileName(destination)}」に保存しました。", open,
                details: destination);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The note is untouched; a partial file is never left behind (see SafeFileWriter).
            ShowAudioMessage(InfoBarSeverity.Error, "書き出せませんでした", "元のノートは変更されていません。", details: $"{destination}\n{ex.Message}");
        }

        FocusEditor();
    }

    /// <summary>Lets the user choose the folder and file name (the system "Save as" dialog).</summary>
    private async Task<string?> PickExportPathAsync(string fileName, string extension, ExportFormat format)
    {
#if TEST_HOOKS
        // Test builds only: "--export-to <folder>" skips the system dialog (it cannot be automated reliably).
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--export-to");
        if (at >= 0 && at + 1 < args.Length)
        {
            return System.IO.Path.Combine(args[at + 1], fileName + extension);
        }
#endif
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = fileName,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add(format == ExportFormat.Pdf ? "PDF" : "Word 文書", [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    // ----- Saved-notes list (Ctrl+O) ---------------------------------------------------

    private void OnNoteListClick(object sender, RoutedEventArgs e) => ShowNoteBrowser();

    private NoteSort _noteSort = NoteSort.NewestFirst;

    /// <summary>
    /// Every saved note, open in a tab or not: search (title and text) and sort by update time
    /// or name. Choosing one opens it as a tab, or switches to its tab if it is already open.
    /// </summary>
    private void ShowNoteBrowser()
    {
        if (_workspace is null)
        {
            return;
        }

        _workspace.Library.Refresh(); // pick up notes added or removed in Explorer
        var search = new TextBox { PlaceholderText = "ノートを検索（名前・本文）", Width = 300 };
        AutomationProperties.SetAutomationId(search, "NoteSearchBox");
        AutomationProperties.SetName(search, "ノートを検索");
        var sort = new ComboBox { Width = 300, ItemsSource = new[] { "更新日時が新しい順", "更新日時が古い順", "名前順" }, SelectedIndex = (int)_noteSort };
        AutomationProperties.SetAutomationId(sort, "NoteSortBox");
        AutomationProperties.SetName(sort, "並べ替え");
        var list = new ListView { Width = 300, MaxHeight = 260, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
        AutomationProperties.SetAutomationId(list, "NoteListView");
        AutomationProperties.SetName(list, "保存済みノート");
        var count = new TextBlock { FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        AutomationProperties.SetAutomationId(count, "NoteListCount");

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var reopen = new HyperlinkButton { Content = "最後に閉じたタブ (Ctrl+Shift+T)", FontSize = 12, Padding = new Thickness(4, 2, 4, 2) };
        var folder = new HyperlinkButton { Content = "フォルダを開く", FontSize = 12, Padding = new Thickness(4, 2, 4, 2) };
        var export = new HyperlinkButton { Content = "PDF / Word に書き出し", FontSize = 12, Padding = new Thickness(4, 2, 4, 2) };
        AutomationProperties.SetAutomationId(export, "NoteListExport");
        footer.Children.Add(reopen);
        footer.Children.Add(folder);
        footer.Children.Add(export);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(search);
        panel.Children.Add(sort);
        panel.Children.Add(count);
        panel.Children.Add(list);
        panel.Children.Add(footer);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        export.Click += async (_, _) =>
        {
            flyout.Hide();
            await ExportAsync();
        };

        // Note texts are read once per opening (for the body search), not on every keystroke.
        var texts = new Dictionary<string, string?>();
        string? Text(NoteInfo note) => texts.TryGetValue(note.Id, out var t) ? t : texts[note.Id] = NoteCatalog.ReadForSearch(note);

        void Refill()
        {
            var openIds = _workspace.Tabs.Select(t => t.Id).ToHashSet();
            var entries = NoteCatalog.Query(_workspace.Library.Notes, openIds, search.Text, _noteSort, Text);
            list.Items.Clear();
            foreach (var entry in entries)
            {
                list.Items.Add(NoteRow(entry));
            }

            count.Text = entries.Count == 0
                ? (string.IsNullOrWhiteSpace(search.Text) ? "保存済みのノートはありません" : "一致するノートはありません")
                : $"{entries.Count} 件";
        }

        void OpenEntry(object? item)
        {
            if (item is FrameworkElement { Tag: string id })
            {
                flyout.Hide();
                if (_workspace.Active?.Id != id)
                {
                    SaveLeavingNote();
                }

                _workspace.Open(id); // switches to the tab if the note is already open
            }
        }

        search.TextChanged += (_, _) => Refill();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && list.Items.Count > 0)
            {
                e.Handled = true;
                OpenEntry(list.Items[0]); // Enter in the search box opens the best (first) match
            }
            else if (e.Key == VirtualKey.Down && list.Items.Count > 0)
            {
                e.Handled = true;
                list.SelectedIndex = 0;
                list.Focus(FocusState.Keyboard);
            }
        };
        sort.SelectionChanged += (_, _) =>
        {
            _noteSort = (NoteSort)Math.Max(0, sort.SelectedIndex);
            Refill();
        };
        list.ItemClick += (_, e) => OpenEntry(e.ClickedItem);
        list.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                OpenEntry(list.SelectedItem);
            }
        };
        reopen.Click += (_, _) =>
        {
            flyout.Hide();
            SaveLeavingNote();
            _workspace.ReopenLastClosed();
        };
        folder.Click += (_, _) =>
        {
            flyout.Hide();
            OpenNotesFolder();
        };
        flyout.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) => FocusEditor();

        Refill();
        flyout.ShowAt(NoteListButton);
    }

    /// <summary>One row: title, last update, and whether it is open in a tab or matched in the text.</summary>
    private static FrameworkElement NoteRow(NoteListEntry entry)
    {
        var row = new StackPanel { Tag = entry.Note.Id, Padding = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = entry.Note.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        string detail = $"更新 {entry.Modified:yyyy-MM-dd HH:mm}"
            + (entry.IsOpen ? "　・タブで開いています" : string.Empty)
            + (entry.MatchedInBody ? "　・本文に一致" : string.Empty);
        row.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        AutomationProperties.SetAutomationId(row, "NoteListItem");
        AutomationProperties.SetName(row, entry.Note.Title);
        AutomationProperties.SetHelpText(row, detail);
        ToolTipService.SetToolTip(row, entry.Note.FilePath);
        return row;
    }

    private void SwitchToClosed(string id)
    {
        SaveLeavingNote();
        _workspace?.Open(id);
    }

    private void OpenNotesFolder()
    {
        if (_workspace is null)
        {
            return;
        }

        Directory.CreateDirectory(_workspace.Library.RootDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_workspace.Library.RootDirectory}\"") { UseShellExecute = true });
    }

    private void ShowRenameFlyout(string id)
    {
        if (_workspace?.Tabs.FirstOrDefault(t => t.Id == id) is not { } note
            || TabItemsPanel.Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == id) is not { } anchor)
        {
            return;
        }

        var box = new TextBox { Text = note.Title, Width = 220, SelectionStart = 0 };
        AutomationProperties.SetAutomationId(box, "RenameBox");
        AutomationProperties.SetName(box, "ノート名");
        var flyout = new Flyout { Content = box, Placement = FlyoutPlacementMode.Bottom };
        bool committed = false;

        async Task CommitAsync()
        {
            if (committed)
            {
                return;
            }

            committed = true;
            flyout.Hide();
            string requested = box.Text;
            if (string.IsNullOrWhiteSpace(requested) || requested == note.Title)
            {
                return;
            }

            try
            {
                string used = await _workspace.RenameAsync(id, requested);
                if (used != requested.Trim())
                {
                    ShowStatus($"「{used}」として保存しました");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowSaveError("名前を変更できませんでした", ex, conflictTab: null);
            }
        }

        box.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await CommitAsync();
            }
            else if (e.Key == VirtualKey.Escape)
            {
                // Cancel the rename only; the window must not hide.
                e.Handled = true;
                committed = true;
                flyout.Hide();
            }
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        flyout.Closed += (_, _) => FocusEditor();
        flyout.ShowAt(anchor);
    }

    /// <summary>Ctrl+N / Ctrl+W / Ctrl+S / Ctrl+Shift+T / F2 in the plain editor and tab strip.</summary>
    private async void OnNoteCommand(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsComposing)
        {
            return; // Never act on shortcuts while the IME is converting.
        }

        args.Handled = true;
        bool shift = sender.Modifiers.HasFlag(VirtualKeyModifiers.Shift);
        string? name = sender.Key switch
        {
            VirtualKey.N => "new",
            VirtualKey.W => "close",
            VirtualKey.S => "save",
            VirtualKey.T when shift => "reopen",
            VirtualKey.F2 => "rename",
            VirtualKey.O => "open",
            VirtualKey.E when shift => "export",
            _ => null,
        };
        if (name is not null)
        {
            await ExecuteCommandAsync(name);
        }
    }

    /// <summary>
    /// App commands, from XAML accelerators (plain editor) or from the Markdown editor, which
    /// handles keys inside WebView2 and forwards them here (never while the IME is composing).
    /// </summary>
    private async Task ExecuteCommandAsync(string name)
    {
        if (_workspace is null)
        {
            return;
        }

        // These work with no tab open too.
        switch (name)
        {
            case "hide":
                HideRequested?.Invoke(this, EventArgs.Empty);
                return;
            case "new":
                await NewNoteAsync();
                return;
            case "open":
                ShowNoteBrowser();
                return;
            case "export":
                await ExportAsync();
                return;
            case "reopen":
                SaveLeavingNote();
                _workspace.ReopenLastClosed();
                return;
            case "record":
                await ToggleRecordingAsync();
                return;
        }

        if (_workspace.Active is not { } active)
        {
            return;
        }

        switch (name)
        {
            case "close":
                await CloseTabAsync(active.Id);
                break;
            case "save":
                _autosaveTimer.Stop();
                await _editor.FlushAsync();
                var result = await _workspace.SaveAsync(active);
                HandleSaveResult(result);
                if (result.Success)
                {
                    ShowStatus("保存しました");
                }

                break;
            case "next":
            case "previous":
                if (_workspace.Tabs.Count > 1)
                {
                    int index = _workspace.Tabs.ToList().IndexOf(active);
                    int step = name == "next" ? 1 : -1;
                    SwitchTo(_workspace.Tabs[(index + step + _workspace.Tabs.Count) % _workspace.Tabs.Count].Id);
                }

                break;
            case "rename":
                ShowRenameFlyout(active.Id);
                break;
            case "focusOut":
                // F6: leave the editor with the keyboard (Tab inside the editor indents).
                RecordButton.Focus(FocusState.Keyboard);
                break;
        }
    }

    /// <summary>
    /// Ctrl+Tab / Ctrl+Shift+Tab switch tabs. Handled in the tunnelling (preview) phase because
    /// the text box would otherwise insert a tab character first.
    /// </summary>
    private async void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || IsComposing)
        {
            return;
        }

        var keyboard = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (!keyboard.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            return; // Plain Tab belongs to the editor.
        }

        e.Handled = true;
        bool back = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        await ExecuteCommandAsync(back ? "previous" : "next");
    }

    // ----- Saving ----------------------------------------------------------------

    private async Task SaveActiveAsync()
    {
        if (_workspace?.Active is { IsDirty: true } tab)
        {
            await SaveAndReportAsync(tab);
        }
    }

    private async Task SaveAndReportAsync(OpenNote tab)
    {
        if (_workspace is not null)
        {
            HandleSaveResult(await _workspace.SaveAsync(tab));
        }
    }

    private void HandleSaveResult(SaveResult result)
    {
        bool forActive = ReferenceEquals(result.Tab, _workspace?.Active);
        if (result.Success)
        {
            if (forActive)
            {
                SaveErrorBar.IsOpen = false;
            }

            return;
        }

        if (result.IsConflict)
        {
            RenderTabs(); // shows the warning icon on that tab
            if (forActive)
            {
                ShowConflict(result.Tab);
            }

            return;
        }

        // Never clear the editor: the text stays in the tab and the next edit/hide retries.
        ShowSaveError(forActive ? "保存できませんでした" : $"「{result.Tab.Title}」を保存できませんでした", result.Error!, conflictTab: null);
    }

    private void ShowSaveError(string title, Exception ex, OpenNote? conflictTab)
    {
        // Only the exception message (path/reason) is shown; memo content is never logged.
        SaveErrorBar.Severity = InfoBarSeverity.Error;
        SaveErrorBar.Title = title;
        SaveErrorBar.Message = $"{ex.Message}\nメモの内容はこの画面に残っています。";
        SaveErrorBar.ActionButton = null;
        SaveErrorBar.Content = null;
        SaveErrorBar.IsOpen = true;
    }

    /// <summary>
    /// The file was changed by another program while this note had unsaved edits. Both choices
    /// keep the other version as a "(競合 …)" copy, so nothing is ever lost.
    /// </summary>
    private void ShowConflict(OpenNote tab)
    {
        var keepMine = new Button { Content = "この内容で上書き" };
        AutomationProperties.SetName(keepMine, "この内容で上書き");
        keepMine.Click += (_, _) =>
        {
            string? copy = _workspace!.ResolveConflictKeepMine(tab);
            SaveErrorBar.IsOpen = false;
            RenderTabs();
            ShowStatus(copy is null ? "保存しました" : $"外部の内容を「{System.IO.Path.GetFileNameWithoutExtension(copy)}」に残しました");
        };

        var takeTheirs = new Button { Content = "外部の変更を読み込む", Margin = new Thickness(0, 0, 0, 4) };
        AutomationProperties.SetName(takeTheirs, "外部の変更を読み込む");
        takeTheirs.Click += (_, _) =>
        {
            string? copy = _workspace!.ResolveConflictTakeTheirs(tab);
            SaveErrorBar.IsOpen = false;
            RenderTabs();
            ShowStatus(copy is null ? "読み込みました" : $"編集内容を「{System.IO.Path.GetFileNameWithoutExtension(copy)}」に残しました");
        };

        SaveErrorBar.Severity = InfoBarSeverity.Warning;
        SaveErrorBar.Title = "外部で変更されています";
        SaveErrorBar.Message = $"「{tab.Title}」が他のアプリで変更されました。どちらを使っても、もう一方は別ファイルに残します。";
        SaveErrorBar.ActionButton = keepMine;
        SaveErrorBar.Content = takeTheirs;
        SaveErrorBar.IsOpen = true;
    }

    /// <summary>A short message in the status area (e.g. "保存しました"), cleared after a moment.</summary>
    private void ShowStatus(string text)
    {
        if (VoiceState != VoiceState.Idle)
        {
            return; // Recording status has priority.
        }

        RecordingIndicator.Visibility = Visibility.Visible;
        RecordingDot.Visibility = Visibility.Collapsed;
        BusyRing.Visibility = Visibility.Collapsed;
        TranscribeBar.Visibility = Visibility.Collapsed;
        RecordingStatusText.Text = text;
        _statusClear.Stop();
        _statusClear.Start();
    }

    /// <summary>
    /// Shows a short, non-modal message. Long technical details (paths, exception text) go in
    /// a tooltip so the small window keeps the memo visible.
    /// </summary>
    private void ShowAudioMessage(InfoBarSeverity severity, string title, string message = "",
        ButtonBase? action = null, string? details = null)
    {
        AudioInfoBar.Severity = severity;
        AudioInfoBar.Title = title;
        AudioInfoBar.Message = message;
        AudioInfoBar.ActionButton = action;
        ToolTipService.SetToolTip(AudioInfoBar, details);
        AutomationProperties.SetHelpText(AudioInfoBar, details ?? string.Empty);
        AudioInfoBar.IsOpen = true;
    }

    // ----- Recording -------------------------------------------------------------

    private AudioSources SelectedSources =>
        (MicToggle.IsChecked == true ? AudioSources.Microphone : AudioSources.None)
        | (SystemAudioToggle.IsChecked == true ? AudioSources.SystemAudio : AudioSources.None);

    private async void OnRecordClick(object sender, RoutedEventArgs e) => await ToggleRecordingAsync();

    private async void OnRecordAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsComposing)
        {
            return;
        }

        args.Handled = true;
        await ToggleRecordingAsync();
    }

    private async Task ToggleRecordingAsync()
    {
        switch (VoiceState)
        {
            case VoiceState.Idle:
                StartRecording();
                break;
            case VoiceState.Recording:
                await StopRecordingAsync();
                break;
            case VoiceState.Transcribing:
                // The button doubles as "cancel transcription"; the audio is kept for retry.
                _transcriptionCts?.Cancel();
                break;
        }
    }

    private void StartRecording()
    {
        if (_recorder is null || _workspace is null)
        {
            return;
        }

        if (_workspace.Active is not { } target)
        {
            // The transcript needs a note to go into; nothing is created behind the user's back.
            ShowAudioMessage(InfoBarSeverity.Warning, "ノートを開いてから録音してください",
                "Ctrl+O でノート一覧を開くか、Ctrl+N で新しいノートを作成できます。");
            return;
        }

        AudioSources sources = SelectedSources;
        if (sources == AudioSources.None)
        {
            ShowAudioMessage(InfoBarSeverity.Warning, "録音する音声を選んでください", "マイクまたはPC音声をオンにしてください。");
            return;
        }

        // The test holds the microphone; recording takes over.
        StopMicrophoneTest();

        // Never record from another device than the one chosen: stop and say why instead.
        if (sources.HasFlag(AudioSources.Microphone) && ResolveMicrophone() is { IsUsable: false } unusable)
        {
            ShowAudioMessage(InfoBarSeverity.Error, "録音を開始できませんでした",
                $"{unusable.Problem} マイク横の ▾ から入力デバイスを選んでください。");
            _shownDeviceProblem = unusable.Problem;
            return;
        }

        try
        {
            _recorder.Start(sources);
        }
        catch (AudioCaptureException ex)
        {
            ShowAudioMessage(InfoBarSeverity.Error, "録音を開始できませんでした",
                ex.Source == AudioSources.Microphone ? "マイクを開けませんでした。" : "PC音声を取得できませんでした。",
                details: ex.Message);
            return;
        }

        // The transcript will go into this note, whatever tab is active when it is ready.
        _recordingNoteId = target.Id;
        _recordingSources = sources;
        StartLevelTimer();

        // A new recording supersedes audio that was waiting for a retry.
        SetPendingAudio(null);
        AudioInfoBar.IsOpen = false;
        SetVoiceState(VoiceState.Recording);
        _recordingClock.Start();
    }

    private async Task StopRecordingAsync()
    {
        if (_recorder is null)
        {
            return;
        }

        _recordingClock.Stop();
        SetVoiceState(VoiceState.Processing);
        RecordedAudio audio;
        try
        {
            audio = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            ShowAudioMessage(InfoBarSeverity.Error, "録音を保存できませんでした", ex.Message);
            SetVoiceState(VoiceState.Idle);
            return;
        }

        if (!audio.HasAudio)
        {
            audio.Delete();
            ShowAudioMessage(InfoBarSeverity.Warning, "音声が録音されませんでした",
                "PC音声は、何かが再生されている間だけ録音されます。");
            SetVoiceState(VoiceState.Idle);
            return;
        }

        await TranscribeAsync(audio);
    }

    /// <summary>
    /// Runs local Whisper on the recording and inserts the text into the note it was recorded
    /// for. On failure or cancel no note is touched and the audio is kept for a retry.
    /// </summary>
    private async Task TranscribeAsync(RecordedAudio audio)
    {
        if (_transcriber is null)
        {
            return;
        }

        SetPendingAudio(audio);
        AudioInfoBar.IsOpen = false;
        using var cts = new CancellationTokenSource();
        _transcriptionCts = cts;
        _transcribeFraction = null;
        SetVoiceState(VoiceState.Transcribing);
        _recordingClock.Start(); // refreshes "preparing model" -> "transcribing"
        string outcome = "文字起こし 失敗";
        try
        {
            // Reports arrive on Whisper's thread; Progress<T> hands them to the UI thread.
            var progress = new Progress<double>(fraction =>
            {
                _transcribeFraction = fraction;
                UpdateRecordingText();
            });
            TranscriptionResult result;
            await using (var stream = File.OpenRead(audio.FilePath!))
            {
                result = await _transcriber.TranscribeAsync(stream, cts.Token, progress);
            }

            // Numbers only (durations, segment counts, repeats) for diagnosing real recordings.
            if (_transcriber.LastDiagnostics is { } diagnostics)
            {
                TranscriptionLog.Append(TranscriptionLog.Format(DateTime.Now, audio.CapturedSources, diagnostics, result,
                    System.IO.Path.GetFileName(_transcriber.ModelPath)));
            }

            if (result.IsEmpty)
            {
                SetPendingAudio(null);
                ShowAudioMessage(InfoBarSeverity.Warning, "音声を認識できませんでした", "話し声が含まれていない可能性があります。");
                return;
            }

            await DeliverTranscriptAsync(result.Text);
            outcome = "文字起こし 完了";

            // Success: raw audio is temporary by design and is deleted right away.
            SetPendingAudio(null);
        }
        catch (OperationCanceledException)
        {
            outcome = "文字起こし 中止";
            ShowAudioMessage(InfoBarSeverity.Informational, "文字起こしを中止しました",
                "音声は再試行用に一時保持しています。", CreateRetryButton());
        }
        catch (Exception ex)
        {
            // Includes TranscriptionException (e.g. model missing). The memo text is never touched.
            string message = ex is TranscriptionException { IsModelMissing: true }
                ? $"Whisperモデル（{LocalWhisperTranscriptionService.KnownModels[0]}）がありません。README の手順でダウンロードして {System.IO.Path.GetDirectoryName(LocalWhisperTranscriptionService.DefaultModelPath)} に置いてください。"
                : ex.Message;
            ShowAudioMessage(InfoBarSeverity.Error, "文字起こしできませんでした", message, CreateRetryButton(),
                details: $"{ex.Message}\n\n音声は再試行用に一時保持しています（次の録音・終了時に削除）。");
        }
        finally
        {
            _recordingClock.Stop();
            _transcriptionCts = null;
            _transcribeFraction = null;
            SetVoiceState(VoiceState.Idle);
            ShowStatus(outcome); // finished / failed / cancelled, briefly (details in the message bar)
        }
    }

    // Share of the speech Whisper has processed (null = not known yet, e.g. while loading the model).
    private double? _transcribeFraction;

    /// <summary>Puts the transcript into the note the recording belongs to.</summary>
    private async Task DeliverTranscriptAsync(string transcript)
    {
        if ((_recordingNoteId ?? _workspace?.Active?.Id) is { } target)
        {
            await DeliverTranscriptToAsync(target, transcript);
        }
    }

    private async Task DeliverTranscriptToAsync(string target, string transcript, bool editorRefused = false)
    {
        if (_workspace is null)
        {
            return;
        }

        if (editorRefused && _workspace.Active?.Id == target)
        {
            await _editor.FlushAsync(); // keep what was typed; the note is then shown again below
        }

        var (delivery, tab) = await _workspace.DeliverTranscript(target, transcript, editorRefused);
        switch (delivery)
        {
            case TranscriptDelivery.InsertIntoActiveEditor:
                InsertTranscript(target, transcript);
                break;
            case TranscriptDelivery.InsertedIntoOpenTab when ReferenceEquals(tab, _workspace.Active):
                _editor.Show(tab!); // the text changed under the editor
                ShowAudioMessage(InfoBarSeverity.Success, "文字起こしを追加しました", "ノートの末尾に追加しました。");
                break;
            case TranscriptDelivery.InsertedIntoOpenTab:
                ShowAudioMessage(InfoBarSeverity.Success, "文字起こしを追加しました", $"「{tab!.Title}」タブの末尾に追加しました。",
                    CreateOpenButton(tab.Id, "タブを開く"));
                RenderTabs();
                break;
            case TranscriptDelivery.AppendedToClosedNote:
                var info = _workspace.Library.Find(target);
                ShowAudioMessage(InfoBarSeverity.Success, "文字起こしを保存しました",
                    $"閉じていた「{info?.Title}」の末尾に追加しました。", CreateOpenButton(target, "ノートを開く"));
                break;
            case TranscriptDelivery.SavedAsNewNote:
                ShowAudioMessage(InfoBarSeverity.Warning, "新しいノートに保存しました",
                    $"録音したノートが見つからないため「{tab!.Title}」に保存しました。");
                break;
        }
    }

    private Button CreateOpenButton(string noteId, string label)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) =>
        {
            AudioInfoBar.IsOpen = false;
            SwitchToClosed(noteId); // Open() also just activates a note that is already open
        };
        return button;
    }

    private Button CreateRetryButton()
    {
        var button = new Button { Content = "再試行" };
        AutomationProperties.SetName(button, "文字起こしを再試行");
        button.Click += async (_, _) =>
        {
            if (_pendingAudio is { } audio && VoiceState == VoiceState.Idle)
            {
                await TranscribeAsync(audio);
            }
        };
        return button;
    }

    private void SetPendingAudio(RecordedAudio? audio)
    {
        if (_pendingAudio is not null && !ReferenceEquals(_pendingAudio, audio))
        {
            _pendingAudio.Delete();
        }

        _pendingAudio = audio;
    }

    // A transcript that arrived while the IME was composing. When composition ends it is
    // routed again through the workspace, so it still lands in the recorded note even if the
    // user switched tabs in between.
    // Each one keeps the note it was recorded for; several can wait (none is overwritten).
    private readonly List<(string Target, string Text)> _transcriptsWaitingForIme = [];

    /// <summary>Inserts a transcript into the active editor; <paramref name="target"/> is the note it belongs to.</summary>
    private void InsertTranscript(string target, string transcript)
    {
        if (IsComposing)
        {
            // Inserting text mid-conversion would break the IME; wait until it finishes.
            _transcriptsWaitingForIme.Add((target, transcript));
            return;
        }

        if (_workspace?.Active is not { } active || active.Id != target)
        {
            _ = DeliverTranscriptToAsync(target, transcript); // the target is no longer the active tab
            return;
        }

        // Appended to the end of the note with the "以下、AIによる文字起こし" header; the caret,
        // selection and scroll position stay. Undoable with Ctrl+Z.
        _editor.InsertTranscript(active.Id, transcript);

        // The record button is disabled while audio is processed, which can move focus away
        // from the editor; give it back unless the user is busy in another field.
        if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox { } other || ReferenceEquals(other, MemoTextBox))
        {
            FocusEditor();
        }

        // Save right away rather than waiting for the typing debounce.
        _ = FlushAsync();
    }

    /// <summary>The editor showed another note when the transcript arrived: put it into its note another way.</summary>
    private async void OnEditorInsertRejected(object? sender, EditorTextChange rejected)
    {
        if (!ReferenceEquals(sender, _editor))
        {
            return;
        }

        string transcript = rejected.Text.StartsWith(TranscriptInsertion.Header, StringComparison.Ordinal)
            ? rejected.Text[TranscriptInsertion.Header.Length..].Trim()
            : rejected.Text;
        await DeliverTranscriptToAsync(rejected.NoteId, transcript, editorRefused: true);
    }

    private void OnEditorCompositionChanged(object? sender, bool composing)
    {
        if (!composing && ReferenceEquals(sender, _editor) && _transcriptsWaitingForIme.Count > 0)
        {
            var waiting = _transcriptsWaitingForIme.ToList();
            _transcriptsWaitingForIme.Clear();
            _ = DeliverWaitingAsync(waiting);
        }
    }

    private async Task DeliverWaitingAsync(List<(string Target, string Text)> waiting)
    {
        foreach (var (target, text) in waiting)
        {
            await DeliverTranscriptToAsync(target, text);
        }
    }

    private void OnSourceFailed(object? sender, AudioCaptureException e)
    {
        // Raised on a capture thread.
        DispatcherQueue.TryEnqueue(() => ShowAudioMessage(InfoBarSeverity.Warning,
            $"{e.Source.DisplayName()}の録音が途中で止まりました",
            "それまでの音声は保持されます。停止して文字起こしできます。", details: e.Message));
    }

    private void SetVoiceState(VoiceState state)
    {
        VoiceState = state;
        bool idle = state == VoiceState.Idle;

        // Sources are fixed for the duration of one recording.
        MicToggle.IsEnabled = idle;
        MicDeviceButton.IsEnabled = idle; // the device cannot change under a running recording
        SystemAudioToggle.IsEnabled = idle;
        RecordButton.IsEnabled = state != VoiceState.Processing;
        if (idle && FocusManager.GetFocusedElement(XamlRoot) is Control focused && focused != MemoTextBox)
        {
            // Return to typing once recording/transcription is over.
            FocusEditor();
        }

        string label = state switch
        {
            VoiceState.Recording => "録音停止",
            VoiceState.Processing => "音声を処理中", // disabled; must not read as "start recording"
            VoiceState.Transcribing => "文字起こしを中止",
            _ => "録音開始",
        };
        AutomationProperties.SetName(RecordButton, label);
        ToolTipService.SetToolTip(RecordButton, $"{label} (Ctrl+R)");
        RecordIcon.Glyph = state switch
        {
            VoiceState.Recording => "",    // Stop
            VoiceState.Transcribing => "", // Cancel
            _ => "",                       // Record
        };

        // Recording: pulsing red dot. Processing/transcribing: a small spinner instead.
        bool recording = state == VoiceState.Recording;
        bool busy = state is VoiceState.Processing or VoiceState.Transcribing;
        RecordingIndicator.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        RecordingDot.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        var pulse = (Storyboard)Resources["RecordingPulse"];
        // Windows "Animation effects" off: a steady red dot (a looping pulse is exactly the kind
        // of motion that setting asks to avoid). The "録音中" text says the same either way.
        if (recording && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            pulse.Begin();
        }
        else
        {
            pulse.Stop();
        }

        UpdateControlBarEmphasis();
        UpdateRecordingText();
        RenderTabs(); // shows/hides the recording mark on the target tab
        VoiceStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The control bar stays in the background while typing.</summary>
    private void UpdateControlBarEmphasis()
    {
        bool emphasize = _pointerOverControlBar || VoiceState != VoiceState.Idle || MinutesIndicator.Visibility == Visibility.Visible;
        // Quiet while typing, but still legible on the glass (0.55 made the icons hard to find
        // over busy backgrounds).
        ControlBar.Opacity = emphasize ? 1.0 : 0.72;
    }

    private void OnControlBarPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverControlBar = true;
        UpdateControlBarEmphasis();
    }

    private void OnControlBarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverControlBar = false;
        UpdateControlBarEmphasis();
    }

    private void OnSourceToggled(object sender, RoutedEventArgs e)
    {
        if (SystemAudioIcon is null)
        {
            return; // Raised during InitializeComponent, before all controls exist.
        }

        UpdateSourceIcons();
        if (_applyingPreferences || _services is null)
        {
            return;
        }

        _services.Preferences.RecordMicrophone = MicToggle.IsChecked == true;
        _services.Preferences.RecordSystemAudio = SystemAudioToggle.IsChecked == true;
        _services.Settings.TrySave(_services.Preferences);
        UpdateMicrophoneDevice(notify: true); // a missing device matters only while the mic is on
    }

    private ResolvedDevice ResolveMicrophone() => _devices is null || _services is null
        ? new ResolvedDevice(null, "マイクの一覧を取得できません。")
        : AudioDeviceResolver.Resolve(_devices, _services.Preferences.Microphone);

    /// <summary>
    /// Re-resolves the chosen microphone (devices or Windows defaults changed): updates the
    /// shown device name and warns once if the chosen device cannot be used. Never switches
    /// to another device by itself.
    /// </summary>
    private void UpdateMicrophoneDevice(bool notify)
    {
        if (_services is null)
        {
            return;
        }

        var selection = _services.Preferences.Microphone;
        var resolved = ResolveMicrophone();
        string name = resolved.Device?.Name ?? selection.DeviceName ?? "なし";
        _micDeviceName = selection.Mode switch
        {
            DeviceSelectionMode.SystemDefault => $"Windows の既定（{name}）",
            DeviceSelectionMode.SystemCommunications => $"Windows の既定の通信デバイス（{name}）",
            _ => name,
        };
        AutomationProperties.SetHelpText(MicDeviceButton, _micDeviceName);
        ToolTipService.SetToolTip(MicDeviceButton, $"マイクの入力デバイス：{_micDeviceName}");
        UpdateSourceIcons();

        string? problem = MicToggle.IsChecked == true ? resolved.Problem : null;
        if (problem is null)
        {
            if (_shownDeviceProblem is not null && AudioInfoBar.IsOpen && AudioInfoBar.Title == DeviceProblemTitle)
            {
                AudioInfoBar.IsOpen = false; // the device came back (or another one was chosen)
            }
        }
        else if (notify && problem != _shownDeviceProblem && VoiceState == VoiceState.Idle)
        {
            ShowAudioMessage(InfoBarSeverity.Warning, DeviceProblemTitle,
                $"{problem} マイク横の ▾ から入力デバイスを選んでください。");
        }

        _shownDeviceProblem = problem;
    }

    /// <summary>Builds the device menu fresh each time, so it always shows the current devices.</summary>
    private void OnMicDeviceMenuOpening(object? sender, object e)
    {
        MicDeviceMenu.Items.Clear();
        if (_devices is null || _services is null)
        {
            return;
        }

        var selection = _services.Preferences.Microphone;
        IReadOnlyList<AudioDeviceInfo> devices;
        try
        {
            devices = _devices.CaptureDevices();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            devices = [];
        }

        MicDeviceMenu.Items.Add(new MenuFlyoutItem { Text = $"使用中：{_micDeviceName}", IsEnabled = false });

        // Opens the mic only while checked, to see the level; nothing is saved.
        var test = new ToggleMenuFlyoutItem { Text = "マイク入力テスト（録音・保存はしません）", IsChecked = _micTest is not null };
        AutomationProperties.SetAutomationId(test, "MicDevice:test");
        test.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) =>
        {
            if (test.IsChecked)
            {
                StartMicrophoneTest();
            }
            else
            {
                StopMicrophoneTest();
            }
        });
        MicDeviceMenu.Items.Add(test);
        MicDeviceMenu.Items.Add(new MenuFlyoutSeparator());

        string DefaultName(bool communications) => _devices.DefaultCapture(communications)?.Name ?? "なし";
        MicDeviceMenu.Items.Add(DeviceItem("MicDevice:default", $"Windows の既定（{DefaultName(false)}）",
            selection.Mode == DeviceSelectionMode.SystemDefault, AudioDeviceSelection.SystemDefault));
        MicDeviceMenu.Items.Add(DeviceItem("MicDevice:communications", $"Windows の既定の通信デバイス（{DefaultName(true)}）",
            selection.Mode == DeviceSelectionMode.SystemCommunications, new AudioDeviceSelection(DeviceSelectionMode.SystemCommunications)));
        MicDeviceMenu.Items.Add(new MenuFlyoutSeparator());

        foreach (var device in devices)
        {
            bool chosen = selection.Mode == DeviceSelectionMode.Specific && selection.DeviceId == device.Id;
            MicDeviceMenu.Items.Add(DeviceItem("MicDevice:" + device.Id, device.Name, chosen,
                new AudioDeviceSelection(DeviceSelectionMode.Specific, device.Id, device.Name)));
        }

        // The chosen device is gone: keep showing it (checked, unavailable) rather than
        // silently picking another one.
        if (selection.Mode == DeviceSelectionMode.Specific && !devices.Any(d => d.Id == selection.DeviceId))
        {
            var missing = DeviceItem("MicDevice:missing", $"{selection.DeviceName ?? "選択したマイク"}（使用できません）", true, selection);
            missing.IsEnabled = false;
            MicDeviceMenu.Items.Add(missing);
        }

        if (devices.Count == 0)
        {
            MicDeviceMenu.Items.Add(new MenuFlyoutItem { Text = "録音デバイスが見つかりません", IsEnabled = false });
        }
    }

    private RadioMenuFlyoutItem DeviceItem(string automationId, string text, bool chosen, AudioDeviceSelection selection)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "MicDevice", IsChecked = chosen };
        AutomationProperties.SetAutomationId(item, automationId);

        // Watching IsChecked (not Click) also covers screen readers and UI Automation, which
        // check the item through its toggle pattern without "clicking" it.
        item.RegisterPropertyChangedCallback(RadioMenuFlyoutItem.IsCheckedProperty, (_, _) =>
        {
            if (item.IsChecked && item.IsEnabled && _services?.Preferences.Microphone != selection)
            {
                SelectMicrophone(selection);
            }
        });
        return item;
    }

    private void SelectMicrophone(AudioDeviceSelection selection)
    {
        if (_services is null || VoiceState != VoiceState.Idle)
        {
            return;
        }

        StopMicrophoneTest(); // the test was for the previous device
        _services.Preferences.Microphone = selection;
        _services.Settings.TrySave(_services.Preferences);
        _shownDeviceProblem = null; // a fresh choice may need a fresh warning
        UpdateMicrophoneDevice(notify: true);
        FocusEditor();
    }

    /// <summary>On/off is shown by the icon shape (crossed out when off), not only by color.</summary>
    private void UpdateSourceIcons()
    {
        bool mic = MicToggle.IsChecked == true;
        bool pc = SystemAudioToggle.IsChecked == true;
        MicIcon.IsOn = mic || _micTest is not null; // crossed out when off (shape, not only color)
        SystemAudioIcon.IsOn = pc;
        string device = _micDeviceName.Length > 0 ? "\n" + _micDeviceName : string.Empty;
        ToolTipService.SetToolTip(MicToggle, (mic ? "マイク：録音する（クリックでオフ）" : "マイク：録音しない（クリックでオン）") + device);
        ToolTipService.SetToolTip(SystemAudioToggle, pc ? "PC音声：録音する（クリックでオフ）" : "PC音声：録音しない（クリックでオン）");
    }

    // ----- Level indicators and microphone test ------------------------------------

    private void StartLevelTimer()
    {
        _levelClock.Restart();
        _levelTimer.Start();
    }

    /// <summary>
    /// Reads the loudest level since the last frame (numbers only; capture threads never wait
    /// for the UI) and lets the liquid rise quickly and fall slowly.
    /// </summary>
    private void UpdateLevels()
    {
        TimeSpan elapsed = _levelClock.Elapsed;
        _levelClock.Restart();
        var levels = _recorder?.Levels;
        bool recording = VoiceState == VoiceState.Recording;
        bool micActive = _micTest is not null || (recording && _recordingSources.HasFlag(AudioSources.Microphone));
        bool systemActive = recording && _recordingSources.HasFlag(AudioSources.SystemAudio);

        var mic = micActive && levels is not null ? levels.Read(AudioSources.Microphone) : default;
        var system = systemActive && levels is not null ? levels.Read(AudioSources.SystemAudio) : default;
        MicIcon.Level = _micLevel.Update(micActive ? AudioLevel.ToMeter(mic.Rms) : 0, elapsed);
        SystemAudioIcon.Level = _systemLevel.Update(systemActive ? AudioLevel.ToMeter(system.Rms) : 0, elapsed);

        // A microphone always delivers buffers (silence included); none at all means the device
        // is not working. PC audio delivers nothing while nothing plays, so it is never flagged.
        bool noSignal = micActive && !mic.Receiving;
        if (noSignal != MicIcon.NoSignal)
        {
            MicIcon.NoSignal = noSignal;
            AutomationProperties.SetHelpText(MicToggle, noSignal ? "マイクの音声を受信していません" : string.Empty);
        }

        if (_micTest is not null && _micTestClock.Elapsed > MicTestLimit)
        {
            StopMicrophoneTest(); // never left running by accident
        }

#if TEST_HOOKS
        if (_testMirrorLevels is not null)
        {
            _testMirrorLevels.Text = FormattableString.Invariant(
                $"mic={MicIcon.Level:0.00};pc={SystemAudioIcon.Level:0.00};nosignal={MicIcon.NoSignal};test={_micTest is not null}");
        }
#endif

        if (!micActive && !systemActive && _micLevel.Value == 0 && _systemLevel.Value == 0)
        {
            _levelTimer.Stop(); // drained to empty: no timer while idle
            MicIcon.NoSignal = false;
        }
    }

    /// <summary>Opens the chosen microphone only to show its level. Started only by the user.</summary>
    private void StartMicrophoneTest()
    {
        if (_micTest is not null || _services is null || _recorder is null || VoiceState != VoiceState.Idle)
        {
            return;
        }

        if (ResolveMicrophone() is { IsUsable: false } unusable)
        {
            ShowAudioMessage(InfoBarSeverity.Error, "マイクテストを開始できませんでした",
                $"{unusable.Problem} マイク横の ▾ から入力デバイスを選んでください。");
            return;
        }

        var test = new MicrophoneTest(_services.CaptureFactory, _recorder.Levels);
        test.Failed += (_, error) => DispatcherQueue.TryEnqueue(() =>
        {
            StopMicrophoneTest();
            ShowAudioMessage(InfoBarSeverity.Warning, "マイクテストが止まりました", error.Message);
        });
        try
        {
            test.Start();
        }
        catch (AudioCaptureException ex)
        {
            ShowAudioMessage(InfoBarSeverity.Error, "マイクテストを開始できませんでした", ex.Message);
            return;
        }

        _micTest = test;
        _micTestClock.Restart();
        UpdateSourceIcons();
        StartLevelTimer();

        var stop = new Button { Content = "テストを終了" };
        AutomationProperties.SetAutomationId(stop, "MicTestStop");
        stop.Click += (_, _) => StopMicrophoneTest();
        ShowAudioMessage(InfoBarSeverity.Informational, MicTestTitle,
            $"{_micDeviceName}。話すとマイクのアイコンが緑色に満ちます。音声は保存されません（最長 {MicTestLimit.TotalSeconds:0} 秒）。", stop);
    }

    /// <summary>Ends the test and releases the device (safe to call at any time).</summary>
    public void StopMicrophoneTest()
    {
        if (_micTest is not { } test)
        {
            return;
        }

        _micTest = null;
        test.Dispose();
        UpdateSourceIcons();
        if (AudioInfoBar.IsOpen && AudioInfoBar.Title == MicTestTitle)
        {
            AudioInfoBar.IsOpen = false;
        }
    }

    private void UpdateRecordingText()
    {
        if (VoiceState == VoiceState.Idle && _statusClear.IsRunning)
        {
            return; // A short status message is showing.
        }

        RecordingIndicator.Visibility = VoiceState == VoiceState.Idle ? Visibility.Collapsed : Visibility.Visible;
        string target = _recordingNoteId is { } id && _workspace?.Library.Find(id) is { } note && note.Id != _workspace.Active?.Id
            ? $"（{note.Title}）"
            : string.Empty;
        RecordingStatusText.Text = VoiceState switch
        {
            VoiceState.Recording => $"録音中 {FormatDuration(_recorder?.Elapsed ?? TimeSpan.Zero)}{target}",
            VoiceState.Processing => "音声を処理中…",
            // First use loads the model from disk, which takes a moment.
            VoiceState.Transcribing when _transcriber?.IsModelLoaded == false || _transcribeFraction is null => "音声モデルを準備中…",
            VoiceState.Transcribing => $"文字起こし {(int)(_transcribeFraction!.Value * 100)}%{target}",
            _ => string.Empty,
        };

        // A real percentage gets a bar; a step without one (loading) keeps the spinner.
        bool measured = VoiceState == VoiceState.Transcribing && _transcribeFraction is not null && _transcriber?.IsModelLoaded != false;
        TranscribeBar.Visibility = measured ? Visibility.Visible : Visibility.Collapsed;
        TranscribeBar.Value = measured ? _transcribeFraction!.Value * 100 : 0;
        if (VoiceState is VoiceState.Processing or VoiceState.Transcribing)
        {
            BusyRing.Visibility = measured ? Visibility.Collapsed : Visibility.Visible;
            BusyRing.IsActive = !measured;
        }
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
}
