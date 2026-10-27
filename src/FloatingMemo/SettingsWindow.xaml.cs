using FloatingMemo.Services;
using FloatingMemo.Services.Acceleration;
using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Notes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FloatingMemo;

/// <summary>
/// Settings for AI minutes: which local model, whether to correct the transcript first, the
/// user's term dictionary (import / add / edit / delete / switch off) and candidates waiting
/// for approval. Everything is stored locally.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly MemoServices _services;
    private readonly TermDictionary _dictionary;
    private IReadOnlyList<LlamaTextGenerator.ModelInfo> _models = [];
    private TermEntry? _editing;
    private bool _loading;

    private readonly Action _appearanceChanged;

    public SettingsWindow(MemoServices services, Action appearanceChanged)
    {
        InitializeComponent();
        _services = services;
        _appearanceChanged = appearanceChanged;
        _dictionary = services.Dictionary;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(720 * Scale), (int)(620 * Scale)));

        // The memo floats above other windows; keep the settings above it too, or it hides behind.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
        }

        _loading = true;
        LoadModels();
        CorrectToggle.IsOn = services.Preferences.CorrectTranscriptBeforeMinutes;
        VisualToggle.IsOn = services.Preferences.VisualEditor;
        WrapToggle.IsOn = services.Preferences.WrapLines;
        AccelerationBox.SelectedIndex = (int)services.Preferences.Acceleration;
        PriorityBox.SelectedIndex = (int)services.Preferences.MinutesPriority;
        ShowWhisperModels();
        ShowAccelerationReport();
        ClaritySlider.Value = Math.Round(services.Preferences.GlassClarity * 100);
        PaperSlider.Value = Math.Round(services.Preferences.PaperMilkiness * 100);
        UseDictionaryToggle.IsOn = _dictionary.Data.UseForCorrection;
        _loading = false;
        RenderCategories();
        RenderEntries();
        RenderTemplates();
        Harvest(silent: true);
        RenderCandidates();
#if TEST_HOOKS
        // Test builds only: "--import-dictionary <file>" runs the import button's code with that
        // file once the window is shown (the system file dialog itself cannot be automated reliably).
        string[] args = Environment.GetCommandLineArgs();
        int tpl = Array.IndexOf(args, "--import-template");
        if (tpl >= 0 && tpl + 1 < args.Length)
        {
            string templatePath = args[tpl + 1];
            ((FrameworkElement)Content).Loaded += async (_, _) =>
            {
                ShowSection("templates");
                await ImportTemplateAsync(templatePath);
            };
        }

        int at = Array.IndexOf(args, "--import-dictionary");
        if (at >= 0 && at + 1 < args.Length)
        {
            string path = args[at + 1];
            ((FrameworkElement)Content).Loaded += async (_, _) =>
            {
                ShowSection("dictionary");
                await ImportFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
            };
        }
#endif
    }

    /// <summary>Opens directly on the dictionary (or another section).</summary>
    public void ShowSection(string name)
    {
        Sections.SelectedItem = name switch
        {
            "dictionary" => DictionarySection,
            "candidates" => CandidateSection,
            "appearance" => AppearanceSection,
            "templates" => TemplateSection,
            _ => ModelSection,
        };
    }

    private double Scale => (Content?.XamlRoot?.RasterizationScale) ?? Interop.NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ModelPanel.Visibility = sender.SelectedItem == ModelSection ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = sender.SelectedItem == AppearanceSection ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPanel.Visibility = sender.SelectedItem == DictionarySection ? Visibility.Visible : Visibility.Collapsed;
        CandidatePanel.Visibility = sender.SelectedItem == CandidateSection ? Visibility.Visible : Visibility.Collapsed;
        TemplatePanel.Visibility = sender.SelectedItem == TemplateSection ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Minutes templates (the user's own; data only, never executed) --------------------

    private IReadOnlyList<MinutesTemplate> _templates = [];

    private void RenderTemplates(string? select = null)
    {
        _loading = true;
        _templates = _services.Templates.All();
        TemplateList.Items.Clear();
        foreach (var t in _templates)
        {
            TemplateList.Items.Add($"{t.Name}（{t.Sections.Count} 見出し）");
        }

        int at = _templates.ToList().FindIndex(t => t.Id == select);
        TemplateList.SelectedIndex = at >= 0 ? at : (_templates.Count > 0 ? 0 : -1);
        TemplateJson.Text = SelectedTemplate is { } current ? TemplateLibrary.ToJson(current) : string.Empty;
        _loading = false;
    }

    private MinutesTemplate? SelectedTemplate =>
        TemplateList.SelectedIndex >= 0 && TemplateList.SelectedIndex < _templates.Count ? _templates[TemplateList.SelectedIndex] : null;

    private void OnTemplateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            TemplateJson.Text = SelectedTemplate is { } t ? TemplateLibrary.ToJson(t) : string.Empty;
        }
    }

    private async void OnImportTemplate(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFileAsync() is { } file)
        {
            await ImportTemplateAsync(file.Path);
        }
    }

    private async Task ImportTemplateAsync(string path)
    {
        try
        {
            // Checked and shown before anything is saved; another JSON shape gets guidance, not a guess.
            if (new FileInfo(path).Length <= TemplateRules.MaxFileBytes)
            {
                var check = TemplateImportCheck.Inspect(File.ReadAllText(path), Path.GetFileName(path));
                if (check.Template is null)
                {
                    string hints = check.Hints.Count > 0 ? "\n\n" + string.Join("\n", check.Hints.Select(h => "・" + h)) : string.Empty;
                    await Message("読み込めませんでした", check.Error + hints + "\n\n対応している形式（JSON）の例:\n" + TemplateImportCheck.Example);
                    return;
                }

                var preview = string.Join("\n", check.Template.Sections.Select((sec, i) =>
                    $"{i + 1}. {sec.Title}{(sec.Kind == TemplateRules.Draft ? "（下書き）" : string.Empty)}{(sec.Instruction.Length > 0 ? " — " + sec.Instruction : string.Empty)}"));
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = $"「{check.Template.Name}」を読み込みますか？",
                    Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = $"{check.Template.Sections.Count} 個の見出し:\n{preview}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                    PrimaryButtonText = "読み込む",
                    CloseButtonText = "やめる",
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            var t = _services.Templates.Import(path);
            RenderTemplates(t.Id);
            await Message("テンプレートを読み込みました", $"「{t.Name}」（{t.Sections.Count} 見出し）。議事録ボタン →「議事録の形式」で選ぶと使われます。");
        }
        catch (Exception ex) when (ex is TemplateFormatException or IOException or UnauthorizedAccessException)
        {
            await Message("読み込めませんでした", ex.Message);
        }
    }

    private async void OnSaveTemplate(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } current)
        {
            return;
        }

        try
        {
            var edited = TemplateLibrary.ParseJson(TemplateJson.Text);
            edited.Id = current.Id; // the same template, edited
            RenderTemplates(_services.Templates.Save(edited).Id);
        }
        catch (TemplateFormatException ex)
        {
            await Message("保存できませんでした", ex.Message);
        }
    }

    private void OnDuplicateTemplate(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is { } current)
        {
            RenderTemplates(_services.Templates.Duplicate(current).Id);
        }
    }

    private async void OnExportTemplate(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } current)
        {
            return;
        }

        var picker = new FileSavePicker { SuggestedFileName = current.Name };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSaveFileAsync() is { } file)
        {
            await Windows.Storage.FileIO.WriteTextAsync(file, TemplateLibrary.ToJson(current));
        }
    }

    private async void OnDeleteTemplate(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } current)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"テンプレート「{current.Name}」を削除しますか？",
            Content = "作成済みの議事録は変わりません。",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _services.Templates.Delete(current.Id);
        if (_services.Preferences.MinutesTemplateId == current.Id)
        {
            _services.Preferences.MinutesTemplateId = null; // back to おまかせ
            _services.Settings.TrySave(_services.Preferences);
        }

        RenderTemplates();
    }

    // ----- Appearance ------------------------------------------------------------------------

    private void OnAppearanceChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading || ClaritySlider is null || PaperSlider is null)
        {
            return;
        }

        _services.Preferences.GlassClarity = ClaritySlider.Value / 100;
        _services.Preferences.PaperMilkiness = PaperSlider.Value / 100;
        _services.Settings.TrySave(_services.Preferences);
        _appearanceChanged(); // applied right away, so the effect can be seen while sliding
    }

    private void OnAppearanceReset(object sender, RoutedEventArgs e)
    {
        ClaritySlider.Value = 60;
        PaperSlider.Value = 60;
    }

    // ----- Models --------------------------------------------------------------------------

    private void LoadModels()
    {
        _models = LlamaTextGenerator.AvailableModels();
        ModelList.Items.Clear();
        string? current = LlamaTextGenerator.FindModel(_services.Preferences.MinutesModel) is { } path ? Path.GetFileName(path) : null;
        foreach (var model in _models)
        {
            string where = model.Bundled ? "アプリのフォルダ" : "ユーザーのフォルダ";
            ModelList.Items.Add($"{model.Label}　{model.Bytes / 1e9:0.0} GB・{where}\n{model.Notes}");
            if (string.Equals(model.FileName, current, StringComparison.OrdinalIgnoreCase))
            {
                ModelList.SelectedIndex = ModelList.Items.Count - 1;
            }
        }

        NoModelText.Visibility = _models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModelFolders.Text = "モデルを探す場所（上から順）:\n" + string.Join("\n",
            ModelLocations.SearchDirectories(ModelLocations.QwenFolder).Select(d => "・" + d.Directory));
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelList.SelectedIndex < 0 || ModelList.SelectedIndex >= _models.Count)
        {
            return;
        }

        _services.Preferences.MinutesModel = _models[ModelList.SelectedIndex].FileName;
        _services.Settings.TrySave(_services.Preferences);
    }

    private void OnOpenModelsFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LlamaTextGenerator.ModelsDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{LlamaTextGenerator.ModelsDirectory}\"") { UseShellExecute = true });
    }

    private IReadOnlyList<(string Name, string Path)> _whisperModels = [];
    private bool _fillingWhisper; // the list is being rebuilt: selection changes are not choices

    /// <summary>Installed Whisper models (nothing is downloaded, moved or deleted) and the one in use.</summary>
    private void ShowWhisperModels()
    {
        _whisperModels = FloatingMemo.Services.Transcription.LocalWhisperTranscriptionService.InstalledModels();
        _fillingWhisper = true;
        WhisperBox.Items.Clear();
        WhisperBox.Items.Add("自動（入っているモデルのうち base を優先）");
        foreach (var (name, _) in _whisperModels)
        {
            WhisperBox.Items.Add(FloatingMemo.Services.Transcription.LocalWhisperTranscriptionService.Describe(name));
        }

        int chosen = _whisperModels.ToList().FindIndex(m => string.Equals(m.Name, _services.Preferences.WhisperModel, StringComparison.OrdinalIgnoreCase));
        WhisperBox.SelectedIndex = chosen + 1;
        _fillingWhisper = false;
        string inUse = System.IO.Path.GetFileName(_services.Transcriber.ModelPath);
        string pending = string.Equals(System.IO.Path.GetFileName(FloatingMemo.Services.Transcription.LocalWhisperTranscriptionService.ResolveModelPath(_services.Preferences.WhisperModel)), inUse, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : "\n変更はアプリを再起動すると反映されます。";
        WhisperReport.Text = (_whisperModels.Count == 0
            ? "Whisper のモデルが見つかりません（README の手順で models\\whisper に置いてください）。"
            : $"使用中: {inUse}（{(_services.Transcriber.IsModelLoaded ? "読み込み済み" : "最初の文字起こしで読み込み")}）") + pending;
    }

    private void OnWhisperChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _fillingWhisper || WhisperBox.SelectedIndex < 0)
        {
            return;
        }

        _services.Preferences.WhisperModel = WhisperBox.SelectedIndex == 0 ? null : _whisperModels[WhisperBox.SelectedIndex - 1].Name;
        _services.Settings.TrySave(_services.Preferences);
        // Only the report changes; rebuilding the list here would fight the selection being made.
        DispatcherQueue.TryEnqueue(ShowWhisperModels);
    }

    private void OnPriorityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PriorityBox.SelectedIndex < 0)
        {
            return;
        }

        _services.Preferences.MinutesPriority = (FloatingMemo.Services.Minutes.MinutesPriority)PriorityBox.SelectedIndex;
        _services.Settings.TrySave(_services.Preferences);
    }

    private void OnAccelerationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AccelerationBox.SelectedIndex < 0)
        {
            return;
        }

        _services.Preferences.Acceleration = (AccelerationMode)AccelerationBox.SelectedIndex;
        _services.Settings.TrySave(_services.Preferences);
        ShowAccelerationReport();
    }

    /// <summary>GPUs found, what each engine uses and why (shown on this PC only; not logged).</summary>
    private void ShowAccelerationReport()
    {
        string pending = _services.Preferences.Acceleration != AccelerationState.Mode
            ? $"\n変更はアプリを再起動すると反映されます（現在: {AccelerationPolicy.Label(AccelerationState.Mode)}）。"
            : string.Empty;
        AccelerationReport.Text = AccelerationState.Report() + pending
            + "\nGPU は Vulkan で使います（NVIDIA・AMD・Intel）。使えないときは自動で CPU になります。";
    }

    private void OnWrapToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _services.Preferences.WrapLines = WrapToggle.IsOn;
        _services.Settings.TrySave(_services.Preferences);
        FloatingMemo.Editing.EditorDisplay.SetWrap(WrapToggle.IsOn);
    }

    private void OnVisualToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _services.Preferences.VisualEditor = VisualToggle.IsOn;
        _services.Settings.TrySave(_services.Preferences);
    }

    private void OnCorrectToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _services.Preferences.CorrectTranscriptBeforeMinutes = CorrectToggle.IsOn;
        _services.Settings.TrySave(_services.Preferences);
    }

    // ----- Dictionary ----------------------------------------------------------------------

    private void OnUseDictionaryToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _dictionary.SetUseForCorrection(UseDictionaryToggle.IsOn);
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ImportFileAsync(file);
        }
    }

    /// <summary>The whole dictionary as CSV (opens in Excel) or JSON, every field, to a file the user picks.</summary>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"用語辞書-{DateTime.Now:yyyyMMdd}" };
        picker.FileTypeChoices.Add("CSV（Excel で開けます）", [".csv"]);
        picker.FileTypeChoices.Add("JSON", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await ExportToAsync(file.Path);
            await Message("書き出しました", $"{file.Path}\n{_dictionary.Entries.Count} 語");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Message("書き出せませんでした", $"{file.Path}\n{ex.Message}");
        }
    }

    private Task ExportToAsync(string path) => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        ? File.WriteAllTextAsync(path, DictionaryTransfer.ExportCsv(_dictionary.Entries), new System.Text.UTF8Encoding(true))
        : File.WriteAllTextAsync(path, DictionaryTransfer.ExportJson(_dictionary.Entries), new System.Text.UTF8Encoding(false));

    /// <summary>
    /// CSV or a file this app exported: shows what would change (added / updated / unchanged /
    /// removed, terms twice in the file) for the chosen way (add, update, replace) before anything
    /// changes. Replace and update first copy the current dictionary to a backup file.
    /// </summary>
    private async Task<bool> ImportWithChoiceAsync(List<TermEntry> incoming, string name)
    {
        var modes = new RadioButtons { Header = "取り込み方" };
        modes.Items.Add("追加（新しい語だけ。既存の語は変えない）");
        modes.Items.Add("更新（新しい語を追加し、既存の語はファイルの内容に）");
        modes.Items.Add("置換（辞書をファイルの内容にする。今の辞書はバックアップ）");
        modes.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(modes, "ImportMode");
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(preview, "ImportPreview");
        ImportMode Mode() => (ImportMode)Math.Max(0, modes.SelectedIndex);
        void Show()
        {
            var p = DictionaryTransfer.Preview(_dictionary.Entries, incoming, Mode());
            preview.Text = $"追加 {p.Added} 語、更新 {p.Updated} 語、変更なし {p.Unchanged} 語"
                + (p.Removed > 0 ? $"、削除 {p.Removed} 語（バックアップに残ります）" : string.Empty)
                + (p.DuplicatesInFile.Count > 0 ? $"\nファイル内の重複（最初の行だけ使う）: {string.Join("、", p.DuplicatesInFile.Take(10))}{(p.DuplicatesInFile.Count > 10 ? " ほか" : string.Empty)}" : string.Empty)
                + (p.Skipped > 0 ? $"\n用語が空の行: {p.Skipped} 行（読み飛ばし）" : string.Empty);
        }

        modes.SelectionChanged += (_, _) => Show();
        Show();
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"辞書の取り込み: {name}（{incoming.Count} 行）",
            Content = new StackPanel { Spacing = 12, Children = { modes, preview } },
            PrimaryButtonText = "取り込む",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        var (result, backup) = _dictionary.Apply(incoming, Mode());
        await Message("取り込みました", $"追加 {result.Added} 語、更新 {result.Updated} 語"
            + (result.Removed > 0 ? $"、削除 {result.Removed} 語" : string.Empty)
            + (backup is not null ? $"\n取り込む前の辞書: {backup}" : string.Empty));
        return true;
    }

    /// <summary>Imports a JSON term list chosen by the user and shows what happened.</summary>
    private async Task ImportFileAsync(Windows.Storage.StorageFile file)
    {
        try
        {
            // Read through the picker's file object (works for any location the picker offers).
            string json = await Windows.Storage.FileIO.ReadTextAsync(file);
            // CSV, or a JSON file this app exported: the add / update / replace choice with a preview.
            var incoming = file.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? DictionaryTransfer.ParseCsv(json) : DictionaryTransfer.ParseExportedJson(json);
            if (incoming is not null)
            {
                await ImportWithChoiceAsync(incoming, file.Name);
                RenderCategories();
                RenderEntries();
                return;
            }

            var result = _dictionary.Import(json, file.Name);
            await Message("インポートしました",
                $"追加 {result.Added} 件、既存の語に統合 {result.Merged} 件、読み飛ばし {result.Skipped} 件。\n分類: {string.Join("、", result.Categories)}\n\n"
                + "辞書の語は、文字起こしに関係するものだけが AI 校正のヒントになります。自動で置き換えることはありません。");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            await Message("インポートできませんでした", $"{file.Path}\n{ex.Message}");
        }

        RenderCategories();
        RenderEntries();
    }

    private void RenderCategories()
    {
        CategoryPanel.Children.Clear();
        foreach (var group in _dictionary.Entries.GroupBy(e => e.Category).OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var box = new CheckBox
            {
                Content = $"{group.Key}（{group.Count()}）",
                IsChecked = !_dictionary.Data.DisabledCategories.Contains(group.Key),
                MinWidth = 0,
            };
            AutomationProperties.SetAutomationId(box, "Category:" + group.Key);
            string category = group.Key;
            box.Checked += (_, _) => _dictionary.SetCategoryEnabled(category, true);
            box.Unchecked += (_, _) => _dictionary.SetCategoryEnabled(category, false);
            CategoryPanel.Children.Add(box);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RenderEntries();

    private void RenderEntries()
    {
        string query = TermMatcher.Normalize(SearchBox.Text ?? string.Empty);
        var entries = _dictionary.Entries
            .Where(e => query.Length == 0 || e.AllForms.Append(e.Reading ?? string.Empty).Any(f => TermMatcher.Normalize(f).Contains(query, StringComparison.Ordinal)))
            .OrderBy(e => e.Category, StringComparer.CurrentCulture).ThenBy(e => e.Term, StringComparer.CurrentCulture)
            .ToList();
        EntryList.Items.Clear();
        foreach (var entry in entries)
        {
            var item = new ListViewItem { Content = Describe(entry), Tag = entry.Id };
            AutomationProperties.SetName(item, entry.Term);
            EntryList.Items.Add(item);
        }

        EntryCount.Text = $"{entries.Count} / {_dictionary.Entries.Count} 件";
    }

    private static string Describe(TermEntry e)
    {
        string text = e.Term + (string.IsNullOrEmpty(e.Reading) ? string.Empty : $"（{e.Reading}）");
        if (e.Aliases.Count > 0)
        {
            text += $"　略称: {string.Join("、", e.Aliases)}";
        }

        if (e.Misrecognitions.Count > 0)
        {
            text += $"　誤認識: {string.Join("、", e.Misrecognitions)}";
        }

        return text + $"　［{e.Category}］" + (e.Enabled ? string.Empty : "（使わない）") + (e.WhisperHint ? "（Whisperヒント）" : string.Empty);
    }

    private void OnEntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is not ListViewItem { Tag: string id } || _dictionary.Entries.FirstOrDefault(x => x.Id == id) is not { } entry)
        {
            return;
        }

        _editing = entry;
        TermBox.Text = entry.Term;
        ReadingBox.Text = entry.Reading ?? string.Empty;
        CategoryBox.Text = entry.Category;
        AliasBox.Text = string.Join("、", entry.Aliases);
        MisBox.Text = string.Join("、", entry.Misrecognitions);
        EnabledBox.IsChecked = entry.Enabled;
        HintBox.IsChecked = entry.WhisperHint;
        DeleteButton.IsEnabled = true;
    }

    private void OnNewEntry(object sender, RoutedEventArgs e)
    {
        _editing = null;
        EntryList.SelectedItem = null;
        TermBox.Text = ReadingBox.Text = AliasBox.Text = MisBox.Text = string.Empty;
        CategoryBox.Text = "ユーザー登録";
        EnabledBox.IsChecked = true;
        HintBox.IsChecked = false;
        DeleteButton.IsEnabled = false;
        TermBox.Focus(FocusState.Programmatic);
    }

    private async void OnSaveEntry(object sender, RoutedEventArgs e)
    {
        static List<string> Split(string text) => text.Split(['、', ',', '，'], StringSplitOptions.RemoveEmptyEntries).ToList();
        var entry = _editing ?? new TermEntry { Source = "user" };
        entry.Term = TermBox.Text;
        entry.Reading = string.IsNullOrWhiteSpace(ReadingBox.Text) ? null : ReadingBox.Text.Trim();
        entry.Category = string.IsNullOrWhiteSpace(CategoryBox.Text) ? "ユーザー登録" : CategoryBox.Text.Trim();
        entry.Aliases = Split(AliasBox.Text);
        entry.Misrecognitions = Split(MisBox.Text);
        entry.Enabled = EnabledBox.IsChecked == true;
        entry.WhisperHint = HintBox.IsChecked == true;
        try
        {
            _dictionary.Upsert(entry);
        }
        catch (ArgumentException ex)
        {
            await Message("保存できませんでした", ex.Message);
            return;
        }

        _editing = entry;
        RenderCategories();
        RenderEntries();
    }

    private async void OnDeleteEntry(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } entry)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"「{entry.Term}」を辞書から削除しますか？",
            Content = "ノートの内容は変わりません。",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _dictionary.Remove(entry.Id);
        OnNewEntry(sender, e);
        RenderCategories();
        RenderEntries();
    }

    // ----- Candidates ----------------------------------------------------------------------

    private void OnHarvest(object sender, RoutedEventArgs e) => Harvest(silent: false);

    /// <summary>Looks for the user's fixes in AI minutes notes (see <see cref="CorrectionLearning"/>).</summary>
    private void Harvest(bool silent)
    {
        var library = _services.Workspace.Library;
        int found = CorrectionLearning.Harvest(library.MetaDirectory, _dictionary, id =>
        {
            // An open tab may hold newer text than the file.
            if (_services.Workspace.Tabs.FirstOrDefault(t => t.Id == id) is { } tab)
            {
                return (tab.Title, tab.Text);
            }

            return library.Find(id) is { Exists: true } info && NoteCatalog.ReadForSearch(info) is { } text ? (info.Title, text) : null;
        }, MinutesValidator.CorrectedTextMarker);
        if (!silent)
        {
            CandidateStatus.Text = found > 0 ? $"{found} 件の候補が見つかりました。" : "新しい候補はありません。";
        }

        RenderCandidates();
    }

    private void RenderCandidates()
    {
        CandidateList.Items.Clear();
        foreach (var c in _dictionary.Data.Candidates.OrderByDescending(c => c.Count).ThenByDescending(c => c.Seen))
        {
            var item = new ListViewItem
            {
                Content = $"「{c.Heard}」→「{c.Correct}」　{c.Origin}・{c.Count} 回" + (c.Note is null ? string.Empty : $"・{c.Note}"),
                Tag = c.Id,
            };
            AutomationProperties.SetName(item, $"{c.Heard}→{c.Correct}");
            CandidateList.Items.Add(item);
        }

        CandidateSection.Text = _dictionary.Data.Candidates.Count > 0 ? $"登録候補（{_dictionary.Data.Candidates.Count}）" : "登録候補";
    }

    private void OnApprove(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is ListViewItem { Tag: string id })
        {
            var entry = _dictionary.Approve(id);
            CandidateStatus.Text = $"「{entry.Term}」に登録しました。";
            RenderCandidates();
            RenderCategories();
            RenderEntries();
        }
    }

    private void OnReject(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is ListViewItem { Tag: string id })
        {
            _dictionary.Reject(id);
            CandidateStatus.Text = "却下しました。";
            RenderCandidates();
        }
    }

    private async Task Message(string title, string text) => await new ContentDialog
    {
        XamlRoot = Content.XamlRoot,
        Title = title,
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
        CloseButtonText = "OK",
    }.ShowAsync();
}
