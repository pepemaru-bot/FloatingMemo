using FloatingMemo.Interop;
using FloatingMemo.Services;
using FloatingMemo.Services.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FloatingMemo;

/// <summary>
/// The floating memo window: a small, always-on-top acrylic surface without a classic
/// title bar. Hosts <see cref="MainPage"/> and owns show/hide, placement and exit.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int ToggleHotkeyId = 1;
    private const uint VK_M = 0x4D;

    // Default and minimum size in effective (DPI-independent) pixels.
    private const int DefaultWidth = 440;
    private const int DefaultHeight = 320;
    private const int MinWidth = 300;
    private const int MinHeight = 180;

    private readonly IntPtr _hwnd;
    private readonly MemoServices _services;
    private GlobalHotkey? _toggleHotkey;

    public MainWindow(MemoServices services)
    {
        InitializeComponent();
        _services = services;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Absolute path: the portable build may be started from any working directory.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ConfigureChrome();
        RestorePlacement();

        // The close (X) button exits the app for now (tray residency comes later).
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            _toggleHotkey?.Dispose();
            // Releases the Whisper model's native memory.
            services.Transcriber.Dispose();
        };

        // Focus set before the window is actually active can be lost, so (re)focus the
        // editor whenever the window becomes active. This covers the first launch too.
        Activated += (_, args) =>
        {
            bool active = args.WindowActivationState != WindowActivationState.Deactivated;
            MemoPage?.SetWindowActive(active);
            if (active)
            {
                MemoPage?.FocusEditor();
            }
        };

        RootFrame.Navigate(typeof(MainPage), services);
        if (MemoPage is { } page)
        {
            // The empty part of the page's tab strip is the drag handle; tabs stay clickable.
            SetTitleBar(page.TitleBarDragRegion);
            page.WindowHandle = _hwnd; // for file pickers and the PDF export

            // The tab strip must end where Windows' caption buttons begin. Their width depends
            // on the DPI and on which buttons are shown, so it is measured, not assumed.
            AppWindow.Changed += (_, e) =>
            {
                if (e.DidSizeChange || e.DidPresenterChange)
                {
                    page.CaptionButtonsWidth = AppWindow.TitleBar.RightInset / Scale;
                }
            };
            page.CaptionButtonsWidth = AppWindow.TitleBar.RightInset / Scale;
            RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionColors();
            UpdateCaptionColors();
            page.AppearanceChanged += (_, _) =>
            {
                if (SystemBackdrop is Controls.LiquidGlassBackdrop glass)
                {
                    glass.Clarity = _services.Preferences.GlassClarity;
                }
            };
            page.VoiceStateChanged += (_, _) => UpdateTitle();
            page.HideRequested += (_, _) => { if (!CloseOpenMenus()) { HideMemo(); } }; // Esc inside the Markdown editor
            page.ActiveNoteChanged += (_, _) => UpdateTitle();
            UpdateTitle();
        }
    }

    /// <summary>Caption button symbols follow the memo's theme (dark on light glass, light on dark).</summary>
    private void UpdateCaptionColors()
    {
        bool light = RootGrid.ActualTheme == ElementTheme.Light;
        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = light ? Windows.UI.Color.FromArgb(0xE6, 0x1A, 0x1A, 0x1A) : Windows.UI.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonHoverBackgroundColor = light ? Windows.UI.Color.FromArgb(0x1A, 0, 0, 0) : Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        bar.ButtonPressedBackgroundColor = light ? Windows.UI.Color.FromArgb(0x2E, 0, 0, 0) : Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        bar.ButtonInactiveForegroundColor = light ? Windows.UI.Color.FromArgb(0x80, 0x1A, 0x1A, 0x1A) : Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);

        // A quiet hairline instead of the (often thick-looking, saturated) accent-colored border;
        // the glass rim drawn by the page does the rest. COLORREF is 0x00BBGGRR.
        uint border = light ? 0x00E3DDD8u : 0x00575049u; // light: #D8DDE3, dark: #495057
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(uint));
    }

    private MainPage? MemoPage => RootFrame.Content as MainPage;

    private OverlappedPresenter? Presenter => AppWindow.Presenter as OverlappedPresenter;

    private bool IsRecording => MemoPage?.VoiceState == VoiceState.Recording;

    private double Scale => NativeMethods.GetDpiForWindow(_hwnd) / 96.0;

    private void ConfigureChrome()
    {
        // Content extends into the title bar area; the page's tab strip becomes the title bar
        // (SetTitleBar is called once the page exists).
        ExtendsContentIntoTitleBar = true;

        // Let the acrylic show through behind the caption button.
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (Presenter is { } presenter)
        {
            // Windows' own caption buttons: minimize, maximize/restore, close. (Esc still hides.)
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.IsResizable = true;
            // Float above the current task while visible (spec 5.3).
            presenter.IsAlwaysOnTop = true;
            presenter.PreferredMinimumWidth = (int)(MinWidth * Scale);
            presenter.PreferredMinimumHeight = (int)(MinHeight * Scale);
        }
    }

    /// <summary>Opens where the user left it, or centered on first launch / if now off-screen.</summary>
    private void RestorePlacement()
    {
        // Index access on purpose: enumerating DisplayArea.FindAll() (e.g. with LINQ) throws
        // InvalidCastException through the C#/WinRT projection.
        var displays = DisplayArea.FindAll();
        var workAreas = new List<ScreenRect>();
        for (int i = 0; i < displays.Count; i++)
        {
            var area = displays[i].WorkArea;
            var rect = new ScreenRect(area.X, area.Y, area.Width, area.Height);
            if (displays[i].IsPrimary)
            {
                workAreas.Insert(0, rect); // WindowPlacement treats the first entry as primary.
            }
            else
            {
                workAreas.Add(rect);
            }
        }

        var bounds = WindowPlacement.Resolve(
            _services.Preferences.Window, workAreas, (int)(DefaultWidth * Scale), (int)(DefaultHeight * Scale));
        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    private void SavePlacement()
    {
        if (Presenter?.State != OverlappedPresenterState.Restored)
        {
            return; // Don't remember a minimized window's position.
        }

        var p = AppWindow.Position;
        var s = AppWindow.Size;
        _services.Preferences.Window = new WindowBounds(p.X, p.Y, s.Width, s.Height);
        _services.Settings.TrySave(_services.Preferences);
    }

    private void UpdateTitle()
    {
        // The taskbar shows this title, so a minimized window still says it is recording.
        string note = MemoPage?.ActiveNoteTitle is { Length: > 0 } t ? $"{t} - " : string.Empty;
        Title = IsRecording ? $"● 録音中 - {note}FloatingMemo" : $"{note}FloatingMemo";
    }

    /// <summary>
    /// Registers Ctrl+Shift+M. Call after the window is shown so an error dialog appears above it.
    /// </summary>
    public void RegisterToggleHotkey()
    {
        _toggleHotkey = new GlobalHotkey(
            _hwnd, ToggleHotkeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VK_M);
        _toggleHotkey.Pressed += (_, _) => ToggleMemo();

        if (!_toggleHotkey.TryRegister(out int error))
        {
            string reason = error == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                ? "Ctrl + Shift + M は他のアプリ（または起動済みの Floating Memo）が使用中です。"
                : $"Windows エラーコード: {error}";
            NativeMethods.MessageBox(
                _hwnd,
                $"グローバルホットキーを登録できませんでした。\n\n{reason}\n\nホットキーなしで起動しています。",
                "Floating Memo",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
        }
    }

    /// <summary>Brings the memo up from anywhere (hotkey, or a second launch of the app).</summary>
    public void ShowMemo()
    {
        bool wasHidden = !AppWindow.IsVisible;
        if (Presenter is { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        if (wasHidden)
        {
            // Start transparent; the OpacityTransition fades the content in.
            RootGrid.Opacity = 0;
        }

        AppWindow.Show(activateWindow: true);
        Activate();

        // Receiving WM_HOTKEY grants this process the right to take the foreground,
        // so SetForegroundWindow succeeds even while another app is active.
        NativeMethods.SetForegroundWindow(_hwnd);

        // Typing can start immediately, without a mouse click.
        MemoPage?.FocusEditor();
        RootGrid.Opacity = 1;
    }

    private void ToggleMemo()
    {
        // Visible but behind another app: bring it forward instead of hiding,
        // so one press always gets the user to the memo.
        if (AppWindow.IsVisible && NativeMethods.GetForegroundWindow() == _hwnd)
        {
            HideMemo();
        }
        else
        {
            ShowMemo();
        }
    }

    private void HideMemo()
    {
        SavePlacement();

        // A microphone test never keeps running out of sight.
        MemoPage?.StopMicrophoneTest();

        if (IsRecording && Presenter is { } presenter)
        {
            // While recording, never vanish completely: minimize so the taskbar button
            // (titled "● 録音中") keeps showing that audio is being captured.
            presenter.Minimize();
        }
        else
        {
            // Hide only; the process and the hotkey registration stay alive.
            AppWindow.Hide();
        }

        // Save in the background; a failure is shown in the memo the next time it opens.
        _ = MemoPage?.FlushAsync();
    }

    // Exit sequence. The Markdown editor reports text asynchronously, so closing is cancelled
    // once, the editor is asked for its last keystrokes, and then the usual exit steps run
    // (confirmations, saving, clean-up) before the window is closed for real.
    // (Window.Close() does not raise AppWindow.Closing again, so the steps cannot rely on it.)
    private bool _exitConfirmed;
    private bool _exitInProgress;

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (MemoPage is null || _exitConfirmed)
        {
            return; // nothing to protect, or the exit steps already ran
        }

        args.Cancel = true;
        if (!_exitInProgress)
        {
            ExitAfterFlush();
        }
    }

    private async void ExitAfterFlush()
    {
        _exitInProgress = true;
        try
        {
            if (MemoPage is { } page)
            {
                await page.FlushEditorAsync();
                if (!ConfirmAndPrepareExit(page))
                {
                    return; // the user chose to stay
                }
            }

            _exitConfirmed = true;
            Close();
        }
        finally
        {
            _exitInProgress = false;
        }
    }

    /// <summary>Asks about recording/unsaved notes and saves. Returns false if the user chose to stay.</summary>
    private bool ConfirmAndPrepareExit(MainPage page)
    {
        if (page.VoiceState != VoiceState.Idle)
        {
            string activity = page.VoiceState == VoiceState.Recording ? "録音中" : "文字起こし中";
            int discard = NativeMethods.MessageBox(
                _hwnd,
                $"{activity}です。録音した音声を破棄して終了しますか？\n（［いいえ］でメモに戻ります）",
                "Floating Memo",
                NativeMethods.MB_YESNO | NativeMethods.MB_ICONWARNING | NativeMethods.MB_DEFBUTTON2);
            if (discard != NativeMethods.IDYES)
            {
                return false;
            }
        }

        // Save synchronously: the process exits right after the window closes.
        if (!page.TryFlushForExit(out string? error))
        {
            int answer = NativeMethods.MessageBox(
                _hwnd,
                $"メモを保存できませんでした。\n\n{error}\n\nこのまま終了すると、保存されていない内容は失われます。終了しますか？\n（［いいえ］でメモに戻ります）",
                "Floating Memo",
                NativeMethods.MB_YESNO | NativeMethods.MB_ICONWARNING | NativeMethods.MB_DEFBUTTON2);
            if (answer != NativeMethods.IDYES)
            {
                return false;
            }
        }

        // Really exiting: remember where the window was, stop capture/transcription and
        // delete temporary audio (including audio kept for a retry).
        SavePlacement();
        page.DiscardRecording();
        return true;
    }
    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // During IME conversion, leave Esc to the IME so it can cancel the conversion.
        if (MemoPage?.IsComposing == true)
        {
            return;
        }

        if (CloseOpenMenus())
        {
            args.Handled = true;
            return;
        }

        args.Handled = true;
        HideMemo();
    }

    /// <summary>
    /// An open menu (e.g. the microphone device list) closes first; Esc must not also hide the
    /// memo. The menu does not always own keyboard focus (the editor may still have it), so it
    /// is closed here. Tooltips are popups too, but they must not swallow Esc.
    /// </summary>
    private bool CloseOpenMenus()
    {
        if (Content?.XamlRoot is not { } root)
        {
            return false;
        }

        var menus = VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Where(p => p.Child is not ToolTip).ToList();
        foreach (var popup in menus)
        {
            popup.IsOpen = false;
        }

        return menus.Count > 0;
    }
}
