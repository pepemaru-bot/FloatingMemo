using FloatingMemo.Services.Export;
using Microsoft.Web.WebView2.Core;

namespace FloatingMemo.Editing;

/// <summary>
/// Prints an HTML page to an A4 PDF with an invisible WebView2 (the same Chromium engine as the
/// Markdown editor, with the same network restrictions). Fonts are embedded by the engine, so
/// Japanese text appears correctly on any PC. The page is served from a temporary folder through
/// a virtual host name; scripts are off and nothing outside that folder can be loaded.
/// </summary>
internal static class PdfExport
{
    private const string Host = "export.floatingmemo.invalid";

    public static async Task WriteAsync(string html, string destination, IntPtr parentWindow)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatingMemo", "export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        CoreWebView2Controller? controller = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "index.html"), html, new System.Text.UTF8Encoding(false));

            var environment = await WebMarkdownEditor.CreateEnvironmentAsync(WebMarkdownEditor.UserDataFolder);
            controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)parentWindow.ToInt64()));
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                }
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;

            var loaded = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate($"https://{Host}/index.html");
            if (await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(20))) != loaded.Task || !loaded.Task.Result)
            {
                throw new IOException("PDF にする前のページを読み込めませんでした。");
            }

            var settings = environment.CreatePrintSettings();
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.PageWidth = 8.27;   // A4 in inches
            settings.PageHeight = 11.69;
            settings.MarginTop = settings.MarginBottom = 0.75;
            settings.MarginLeft = settings.MarginRight = 0.67;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;

            await SafeFileWriter.WriteAsync(destination, async temp =>
            {
                if (!await core.PrintToPdfAsync(temp, settings))
                {
                    throw new IOException("PDF を作成できませんでした。");
                }
            });
        }
        finally
        {
            controller?.Close();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temporary HTML only; removed on the next export or by Windows' temp cleanup.
            }
        }
    }
}
