using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

public sealed class TermDictionaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "dictionary.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void Import_AcceptsCategoryObjects_AndMergesWithoutDuplicates()
    {
        var dictionary = new TermDictionary(FilePath);
        var first = dictionary.Import("""
            {
              "施設名": [
                { "name": "架空ホール", "reading": "かくうほーる", "略称": "架空", "誤認識": ["加工ホール"] },
                "未来中学校"
              ],
              "業務用語": [ { "用語": "稟議書", "読み": "りんぎしょ", "aliases": ["稟議"], "misrecognitions": "林業書、輪議書" } ]
            }
            """, "初期辞書.json");

        Assert.Equal(3, first.Added);
        var hall = dictionary.Entries.Single(e => e.Term == "架空ホール");
        Assert.Equal("施設名", hall.Category);
        Assert.Equal(["架空"], hall.Aliases);
        Assert.Equal(["加工ホール"], hall.Misrecognitions);
        Assert.Equal("import:初期辞書.json", hall.Source);
        Assert.Equal(["林業書", "輪議書"], dictionary.Entries.Single(e => e.Term == "稟議書").Misrecognitions);

        var again = dictionary.Import("""[ { "term": "稟議書", "misrecognitions": ["稟儀書"] } ]""", "追加.json");
        Assert.Equal(0, again.Added);
        Assert.Equal(1, again.Merged);
        Assert.Equal(3, new TermDictionary(FilePath).Entries.Count); // saved, no duplicates
        Assert.Contains("稟儀書", new TermDictionary(FilePath).Entries.Single(e => e.Term == "稟議書").Misrecognitions);
    }

    [Fact]
    public void Import_LexiconWithEntriesList_IgnoresRulesAndScope_AndMapsFields()
    {
        // Same shape as the user's initial lexicon (made-up names).
        var dictionary = new TermDictionary(FilePath);
        var result = dictionary.Import("""
            {
              "schema_version": "1.0",
              "description": "説明文",
              "scope": { "school_regions": ["東京都", "神奈川県"], "school_count": 2 },
              "handling_rules": ["自動置換は禁止。", "全件を投入しない。"],
              "entries": [
                { "term": "架空学園中学校", "category": "school", "region": "東京都", "aliases": ["架空"], "source_id": "tokyo", "policy": "hint_only" },
                { "term": "未来中学校", "category": "school", "region": "神奈川県", "aliases": [], "policy": "hint_only" },
                { "term": "解き方", "category": "指導・学習管理", "aliases": [], "policy": "hint_only", "recognition_candidates": ["時方"] },
                { "term": "使わない語", "category": "指導・学習管理", "policy": "disabled" }
              ]
            }
            """, "lexicon.json");

        Assert.Equal(4, result.Added);
        Assert.Equal(4, dictionary.Entries.Count); // no rules, regions or descriptions taken as terms
        Assert.Equal("学校名（東京都）", dictionary.Entries.Single(e => e.Term == "架空学園中学校").Category);
        Assert.Equal("学校名（神奈川県）", dictionary.Entries.Single(e => e.Term == "未来中学校").Category);
        Assert.Equal(["架空"], dictionary.Entries.Single(e => e.Term == "架空学園中学校").Aliases);
        Assert.Equal(["時方"], dictionary.Entries.Single(e => e.Term == "解き方").Misrecognitions);
        Assert.False(dictionary.Entries.Single(e => e.Term == "使わない語").Enabled);
        Assert.All(dictionary.Entries, e => Assert.False(e.WhisperHint)); // nothing goes to Whisper unless the user marks it
    }

    [Fact]
    public void Entries_CanBeAddedEditedRemoved_AndSwitchedOff()
    {
        var dictionary = new TermDictionary(FilePath);
        var entry = new TermEntry { Term = " 見積書 ", Misrecognitions = ["三積書", " "], Category = "業務用語" };
        dictionary.Upsert(entry);
        Assert.Equal("見積書", dictionary.Entries.Single().Term);
        Assert.Equal(["三積書"], dictionary.Entries.Single().Misrecognitions);

        entry.Reading = "みつもりしょ";
        dictionary.Upsert(entry);
        Assert.Equal("みつもりしょ", new TermDictionary(FilePath).Entries.Single().Reading);

        dictionary.SetCategoryEnabled("業務用語", false);
        Assert.Empty(dictionary.ActiveEntries);
        dictionary.SetCategoryEnabled("業務用語", true);
        dictionary.SetUseForCorrection(false);
        Assert.Empty(dictionary.ActiveEntries); // master switch: nothing is used
        dictionary.SetUseForCorrection(true);
        Assert.Single(dictionary.ActiveEntries);

        dictionary.Remove(entry.Id);
        Assert.Empty(new TermDictionary(FilePath).Entries);
    }

    [Fact]
    public void Candidates_NeedApproval_RejectedOnesAreNotProposedAgain()
    {
        var dictionary = new TermDictionary(FilePath);
        Assert.True(dictionary.AddCandidate("加工ホール", "架空ホール", "ユーザーの修正", "会議"));
        dictionary.AddCandidate("加工ホール", "架空ホール", "ユーザーの修正", "会議2");
        Assert.Equal(2, dictionary.Data.Candidates.Single().Count);
        Assert.Empty(dictionary.Entries); // nothing registered without approval

        var entry = dictionary.Approve(dictionary.Data.Candidates.Single().Id, "施設名");
        Assert.Equal("架空ホール", entry.Term);
        Assert.Equal(["加工ホール"], entry.Misrecognitions);
        Assert.False(dictionary.AddCandidate("加工ホール", "架空ホール", "ユーザーの修正", null)); // already known

        dictionary.AddCandidate("雨天", "運転", "AI校正", null);
        dictionary.Reject(dictionary.Data.Candidates.Single().Id);
        Assert.False(dictionary.AddCandidate("雨天", "運転", "AI校正", null));
        Assert.Empty(dictionary.Data.Candidates);
    }

    [Fact]
    public void DamagedFile_IsKeptAside_NotOverwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ broken");
        var dictionary = new TermDictionary(FilePath);
        Assert.Empty(dictionary.Entries);
        Assert.Single(Directory.GetFiles(_dir, "dictionary.json.damaged-*"));
    }

    [Fact]
    public void Matcher_ReturnsOnlyRelatedTerms()
    {
        var entries = new[]
        {
            new TermEntry { Term = "架空ホール", Misrecognitions = ["加工ホール"] },
            new TermEntry { Term = "稟議書", Reading = "りんぎしょ" },
            new TermEntry { Term = "定例報告会" },
            new TermEntry { Term = "未来中学校" },
        };

        var related = TermMatcher.Related(entries, "加工ホールの説明会と、稟議書の締め切り、それから定例報告書の話をしました。");
        var terms = related.Select(m => m.Entry.Term).ToList();
        Assert.Contains("架空ホール", terms);   // via a known misrecognition
        Assert.Contains("稟議書", terms);     // exact
        Assert.Contains("定例報告会", terms);     // similar spelling (fuzzy)
        Assert.DoesNotContain("未来中学校", terms); // unrelated: not sent to the AI
    }
}

public sealed class TranscriptCorrectorTests
{
    private const string Transcript = "加工ホールの説明会は三月五日です。担当は田中さんです。稟儀書の書き方も話しました。";

    private static readonly TermEntry Hall = new() { Term = "架空ホール", Misrecognitions = ["加工ホール"], Category = "施設名" };
    private static readonly TermEntry Form = new() { Term = "稟議書", Reading = "りんぎしょ", Category = "業務用語" };

    [Fact]
    public void Parse_ReadsTheProposalLines()
    {
        var proposals = TranscriptCorrector.Parse("""
            - 原文「加工ホール」→ 修正「架空ホール」（理由: 施設名の誤認識／確信: 高）
            - 原文「林業書」→修正「稟議書」(理由: 読みが近い／確信: 低)
            なし
            """);
        Assert.Equal(2, proposals.Count);
        Assert.True(proposals[0].Confident);
        Assert.Equal("施設名の誤認識", proposals[0].Reason);
        Assert.False(proposals[1].Confident);
    }

    [Fact]
    public void DictionaryBackedChange_IsApplied()
    {
        var result = TranscriptCorrector.Apply(Transcript, [new("加工ホール", "架空ホール", "施設名", true)], [Hall, Form]);
        Assert.StartsWith("架空ホールの説明会", result.Clean);
        Assert.Equal(CorrectionStatus.Applied, result.Decisions.Single().Status);
    }

    [Fact]
    public void ReadingLikeChange_FromTheDictionary_IsApplied()
    {
        var result = TranscriptCorrector.Apply(Transcript, [new("稟儀書", "稟議書", "読み", true)], [Hall, Form]);
        Assert.Contains("稟議書の書き方", result.Clean);
    }

    [Fact]
    public void ChangesWithoutDictionarySupport_NumberChanges_AndLowConfidence_AreOnlyMarked()
    {
        var result = TranscriptCorrector.Apply(Transcript,
        [
            new("田中", "山中", "人名", true),             // a person's name: never changed automatically
            new("三月五日", "三月十五日", "日付", true),     // numbers: never changed automatically
            new("加工ホール", "架空ホール", "施設名", false), // low confidence
        ], [Hall, Form]);

        Assert.Equal(Transcript, result.Clean); // nothing applied
        Assert.All(result.Decisions, d => Assert.Equal(CorrectionStatus.NeedsReview, d.Status));
        Assert.Contains("田中〔要確認: 山中？〕", result.Annotated);
        Assert.Contains("三月五日〔要確認: 三月十五日？〕", result.Annotated);
        Assert.Contains("加工ホール〔要確認: 架空ホール？〕", result.Annotated);
    }

    [Fact]
    public void ProposalNotInTheTranscript_IsRejected()
    {
        var result = TranscriptCorrector.Apply(Transcript, [new("予算は百万円", "予算は二百万円", "", true)], [Hall]);
        Assert.Equal(Transcript, result.Clean);
        Assert.Equal(Transcript, result.Annotated);
        Assert.Equal(CorrectionStatus.Rejected, result.Decisions.Single().Status);
    }

    [Fact]
    public void Dictionary_IsNeverAppliedMechanically()
    {
        // The misrecognition is in the dictionary and in the text, but the AI proposed nothing.
        var result = TranscriptCorrector.Apply(Transcript, [], [Hall, Form]);
        Assert.Equal(Transcript, result.Clean);
    }

    [Fact]
    public async Task Pipeline_ReportsSteps_InOrder_WithoutPercentages()
    {
        var model = new ScriptedGenerator("なし", "## 決定事項\n- なし");
        var steps = new List<MinutesProgress>();
        await new MinutesGenerator(model).GenerateAsync(Transcript, "会議", DateTime.Now,
            new SynchronousProgress<MinutesProgress>(steps.Add), dictionary: [Hall]);

        var names = steps.Select(s => s.Step).Distinct().ToList();
        Assert.Equal(["文字起こしを校正", "議事録を作成"], names);
        Assert.All(steps, s => Assert.Equal(1, s.Parts));
    }

    [Fact]
    public async Task Pipeline_CorrectsFirst_ThenSummarizesTheCorrectedText_WithAnAppendix()
    {
        var model = new ScriptedGenerator(
            "- 原文「加工ホール」→ 修正「架空ホール」（理由: 施設名／確信: 高）\n- 原文「田中」→ 修正「山中」（確信: 高）",
            "## 決定事項\n- 説明会は三月五日（原文:「架空ホールの説明会は三月五日です」）");
        var draft = await new MinutesGenerator(model).GenerateAsync(Transcript, "会議", DateTime.Now, dictionary: [Hall, Form]);

        Assert.Equal(2, model.Prompts.Count);                       // two separate steps
        Assert.Contains("架空ホール", model.Prompts[0]);            // related term sent as a hint
        Assert.Contains("架空ホールの説明会", model.Prompts[1]);      // minutes read the corrected text
        Assert.Contains("## 校正の記録", draft.Markdown);
        Assert.Contains("「加工ホール」→「架空ホール」：修正済み", draft.Markdown);
        Assert.Contains("「田中」→「山中」：要確認（原文のまま）", draft.Markdown);
        Assert.Contains("田中〔要確認: 山中？〕", draft.Markdown);
        Assert.DoesNotContain("説明会は三月五日（原文:「架空ホールの説明会は三月五日です」） ⚠", draft.Markdown); // quote found in the corrected text
    }
}

public sealed class CorrectionLearningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void ChangedWords_FindsUserFixes_IncludingAcceptedReviewMarks()
    {
        string before = "加工ホールの説明会です。担当は田中〔要確認: 山中？〕さんです。変更のない文です。";
        string after = "架空ホールの説明会です。担当は山中さんです。変更のない文です。新しく足した文です。";
        var pairs = CorrectionLearning.ChangedWords(before, after);
        Assert.Contains(("加工ホール", "架空ホール"), pairs); // widened to the whole word
        Assert.Contains(("田中", "山中"), pairs);
        Assert.Equal(2, pairs.Count); // the added sentence is not a "correction"
    }

    [Fact]
    public void Harvest_AddsCandidatesOnce_AndOnlyWithApproval()
    {
        var dictionary = new TermDictionary(Path.Combine(_dir, "dictionary.json"));
        const string marker = "> 校正済み";
        CorrectionLearning.SaveSnapshot(_dir, "note1", "加工ホールの説明会です。");
        string note = $"# 議事録\n\n{marker}\n架空ホールの説明会です。\n";

        int found = CorrectionLearning.Harvest(_dir, dictionary, id => id == "note1" ? ("会議（AI議事録）", note) : null, marker);
        Assert.Equal(1, found);
        var candidate = dictionary.Data.Candidates.Single();
        Assert.Equal(("加工ホール", "架空ホール"), (candidate.Heard, candidate.Correct));
        Assert.Equal("会議（AI議事録）", candidate.Note);
        Assert.Empty(dictionary.Entries); // not registered yet

        Assert.Equal(0, CorrectionLearning.Harvest(_dir, dictionary, _ => ("会議（AI議事録）", note), marker)); // counted once
    }

    [Fact]
    public void WhisperHint_OnlyMarkedTerms()
    {
        var dictionary = new TermDictionary(Path.Combine(_dir, "dictionary.json"));
        Assert.Null(dictionary.WhisperHintText());
        dictionary.Upsert(new TermEntry { Term = "稟議書", WhisperHint = true });
        dictionary.Upsert(new TermEntry { Term = "定例報告会" });
        Assert.Equal("用語: 稟議書。", dictionary.WhisperHintText());
    }
}
