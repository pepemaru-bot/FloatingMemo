using System.Text.Json;
using System.Text.Json.Nodes;
using FloatingMemo.Services.Notes;
using FloatingMemo.Services.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace FloatingMemo.Editing;

/// <summary>
/// The Markdown live-preview editor: CodeMirror 6 (Editor\editor.html, files shipped with the
/// app) inside WebView2.
/// </summary>
/// <remarks>
/// Security: the page is served from the app folder through a virtual host name; any other
/// navigation, new window, download or permission request is refused; browser shortcuts,
/// DevTools (Release), host objects, script dialogs and autofill are off; the page's CSP allows
/// no network access. Messages from the page are parsed defensively and only accepted for the
/// note currently shown.
/// </remarks>
internal sealed class WebMarkdownEditor : IMemoEditor
{
    private const string HostName = "editor.floatingmemo.invalid"; // .invalid never resolves on a network
    private const int MaxTextLength = 20_000_000;
    private static readonly Uri EditorUri = new($"https://{HostName}/editor.html");

    private readonly WebView2 _webView;
    private readonly HashSet<string> _knownNotes = new(StringComparer.Ordinal);
    private TaskCompletionSource? _flush;
    private string? _noteId;

    private WebMarkdownEditor(WebView2 webView)
    {
        _webView = webView;
    }

    public event EventHandler<EditorTextChange>? TextChanged;

    public event EventHandler<bool>? CompositionChanged;

    public event EventHandler<string>? CommandInvoked;

    /// <summary>Focus moved into / out of the editor (used by test hooks).</summary>
    public event EventHandler<bool>? FocusChanged;

    /// <summary>The WebView2 process crashed: the page switches back to the plain editor.</summary>
    public event EventHandler? Failed;

    public bool IsComposing { get; private set; }

    /// <summary>
    /// Starts WebView2 and loads the editor page. Returns null if WebView2 is not available
    /// (e.g. the runtime is missing), in which case the plain editor stays in use.
    /// </summary>
    /// <summary>
    /// The browser environment shared by the editor and the PDF export (they must use identical
    /// options, because they share one user data folder).
    /// </summary>
    internal static Task<CoreWebView2Environment> CreateEnvironmentAsync(string userDataFolder)
    {
        Directory.CreateDirectory(userDataFolder);
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--proxy-server=http://127.0.0.1:9 --proxy-bypass-list=<-loopback> "
                + "--disable-background-networking --disable-component-update --disable-domain-reliability "
                + "--disable-sync --no-pings --disable-breakpad --metrics-recording-only "
                + "--disable-features=msSmartScreenProtection,msEdgeEnhancedSecurityMode,AutofillServerCommunication,OptimizationHints",
        };
        return CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options).AsTask();
    }

    /// <summary>%LOCALAPPDATA%\FloatingMemo\WebView2 (browser cache; never next to the exe).</summary>
    internal static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatingMemo", "WebView2");

    public static async Task<WebMarkdownEditor?> CreateAsync(WebView2 webView, string userDataFolder, string? theme, string? paper = null)
    {
        string editorFolder = Path.Combine(AppContext.BaseDirectory, "Editor");
        if (!File.Exists(Path.Combine(editorFolder, "editor.html")))
        {
            return null;
        }

        try
        {
            // WebView2 paints this color before (and under) the page. It must be set before
            // creation; it matches the paper so no different-colored rectangle ever shows.
            webView.DefaultBackgroundColor = ParseColor(paper) ?? Microsoft.UI.Colors.White;

            // Browser data (cache etc.) goes to a per-user folder, never next to the exe:
            // the portable app folder may be read-only and must stay free of user data.
            Directory.CreateDirectory(userDataFolder);
            // The editor is a local page (served from the app folder through a virtual host, which
            // never touches the network). The browser engine itself still talks to Microsoft
            // services in the background (configuration, updates, safe browsing), so all of its
            // network traffic is sent to a proxy address where nothing listens: nothing leaves
            // the PC. The page is additionally locked down by its CSP.
            var environment = await CreateEnvironmentAsync(userDataFolder);
            await webView.EnsureCoreWebView2Async(environment);

            var editor = new WebMarkdownEditor(webView);
            var ready = new TaskCompletionSource();
            editor._theme = theme;
            editor._paper = paper;
            editor.Configure(webView.CoreWebView2, editorFolder, ready);
            webView.CoreWebView2.Navigate(EditorUri.ToString());

            var finished = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            return finished == ready.Task ? editor : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // WebView2 runtime missing or failed to start.
            return null;
        }
    }

    private void Configure(CoreWebView2 core, string editorFolder, TaskCompletionSource ready)
    {
        var settings = core.Settings;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif
        settings.AreBrowserAcceleratorKeysEnabled = false; // F5/Ctrl+F/Ctrl+P... and lets Ctrl+N/W/Tab reach the editor
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsWebMessageEnabled = true;

        // Only the editor folder is reachable, and only from the editor's own origin.
        core.SetVirtualHostNameToFolderMapping(HostName, editorFolder, CoreWebView2HostResourceAccessKind.Deny);

        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri != EditorUri)
            {
                e.Cancel = true; // links, dropped files, script redirects: never navigate away
            }
        };
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += (_, _) => Failed?.Invoke(this, EventArgs.Empty);

        core.WebMessageReceived += (_, e) =>
        {
            JsonObject? message;
            try
            {
                message = JsonNode.Parse(e.WebMessageAsJson) as JsonObject;
            }
            catch (JsonException)
            {
                return;
            }

            if (message is null || e.Source != EditorUri.ToString())
            {
                return;
            }

            if (Str(message, "type") == "ready")
            {
                PostTheme();

                ready.TrySetResult();
                return;
            }

            Handle(message);
        };
    }

    private static readonly HashSet<string> Commands =
        ["hide", "new", "open", "export", "close", "save", "next", "previous", "reopen", "rename", "record", "focusOut"];

    // Last text reported by the page; caret-only messages carry no text.
    private string _lastText = string.Empty;

    private void Handle(JsonObject message)
    {
        string? type = Str(message, "type");
        switch (type)
        {
            case "change":
            case "caret":
            case "flushed":
            {
                // Accept text only for the note this editor is showing.
                if (Str(message, "id") is { } id && id == _noteId && Int(message, "caret") is { } caret)
                {
                    if (type != "caret")
                    {
                        if (Str(message, "text") is not { Length: <= MaxTextLength } text)
                        {
                            break;
                        }

                        _lastText = text;
                    }

                    TextChanged?.Invoke(this, new EditorTextChange(id, _lastText, Math.Min(caret, _lastText.Length)));
                }

                if (type == "flushed")
                {
                    _flush?.TrySetResult();
                }

                break;
            }

            case "caretStyle":
                if (Str(message, "color") is { Length: <= 64 } color)
                {
                    CaretColor = color;
                }

                break;
            case "composition":
                IsComposing = message["active"]?.GetValueKind() == JsonValueKind.True;
                CompositionChanged?.Invoke(this, IsComposing);
                break;
            case "focus":
                FocusChanged?.Invoke(this, message["focused"]?.GetValueKind() == JsonValueKind.True);
                break;
            case "command":
                if (Str(message, "name") is { } name && Commands.Contains(name))
                {
                    CommandInvoked?.Invoke(this, name);
                }

                break;
        }
    }
    private string? _theme;
    private string? _paper;

    /// <summary>
    /// Colours of the page: "light"/"dark" text colours (null = follow Windows) and the paper
    /// background as #rrggbb (null = the system's, e.g. in high contrast).
    /// </summary>
    public void SetTheme(string? theme, string? paper)
    {
        _theme = theme;
        _paper = paper;
        if (ParseColor(paper) is { } color)
        {
            _webView.DefaultBackgroundColor = color;
        }
        PostTheme();
    }

    /// <summary>"#rrggbb" → an opaque color (null for anything else).</summary>
    private static Windows.UI.Color? ParseColor(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && uint.TryParse(hex[1..], System.Globalization.NumberStyles.HexNumber, null, out uint rgb)
            ? Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;

    private void PostTheme() => Post(new JsonObject
    {
        ["type"] = "theme",
        ["theme"] = _theme is "light" or "dark" ? _theme : null,
        ["paper"] = _paper,
    });

    /// <summary>The cursor colour the page reports (diagnostics and tests).</summary>
    public string? CaretColor { get; private set; }

    public void Show(OpenNote note)
    {
        _noteId = note.Id;
        _knownNotes.Add(note.Id);
        _lastText = note.Text;
        Post(new JsonObject
        {
            ["type"] = "load",
            ["id"] = note.Id,
            ["text"] = note.Text,
            ["caret"] = Math.Clamp(note.Caret, 0, note.Text.Length),
            ["readOnly"] = note.LoadError is not null,
        });
    }

    public void InsertTranscript(string noteId, string transcript) =>
        Post(new JsonObject { ["type"] = "insert", ["id"] = noteId, ["text"] = TranscriptInsertion.Block(transcript, "\n") });

    public void Forget(string noteId)
    {
        _knownNotes.Remove(noteId);
        Post(new JsonObject { ["type"] = "forget", ["id"] = noteId });
    }

    public void Focus()
    {
        _webView.Focus(FocusState.Programmatic);
        Post(new JsonObject { ["type"] = "focus" });
    }

    public async Task FlushAsync(bool settle = false)
    {
        _flush = new TaskCompletionSource();
        Post(new JsonObject { ["type"] = "flush", ["settle"] = settle });
        await Task.WhenAny(_flush.Task, Task.Delay(1000)); // on timeout the last reported text is used
    }

    private void Post(JsonObject message)
    {
        try
        {
            _webView.CoreWebView2?.PostWebMessageAsJson(message.ToJsonString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // WebView2 was closed or crashed; Failed has been / will be raised.
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static int? Int(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out int i) && i >= 0 ? i : null;
}
