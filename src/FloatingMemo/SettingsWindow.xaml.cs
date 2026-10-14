using FloatingMemo.Services;
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
        ClaritySlider.Value = Math.Round(services.Preferences.GlassClarity * 100);
        PaperSlider.Value = Math.Round(services.Preferences.PaperMilkiness * 100);
        UseDictionaryToggle.IsOn = _dictionary.Data.UseForCorrection;
        _loading = false;
        RenderCategories();
        RenderEntries();
        Harvest(silent: true);
        RenderCandidates();
#if TEST_HOOKS
        // Test builds only: "--import-dictionary <file>" runs the import button's code with that
        // file once the window is shown (the system file dialog itself cannot be automated reliably).
        string[] args = Environment.GetCommandLineArgs();
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
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ImportFileAsync(file);
        }
    }

    /// <summary>Imports a JSON term list chosen by the user and shows what happened.</summary>
    private async Task ImportFileAsync(Windows.Storage.StorageFile file)
    {
        try
        {
            // Read through the picker's file object (works for any location the picker offers).
            string json = await Windows.Storage.FileIO.ReadTextAsync(file);
            var result = _dictionary.Import(json, file.Name);
            await Message("インポートしました",
                $"追加 {result.Added} 件、既存の語に統合 {result.Merged} 件、読み飛ばし {result.Skipped} 件。\n分類: {string.Join("、", result.Categories)}\n\n"
                + "辞書の語は、文字起こしに関係するものだけが AI 校正のヒントになります。自動で置き換えることはありません。");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
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
