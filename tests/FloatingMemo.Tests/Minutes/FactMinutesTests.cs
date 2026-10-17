using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Minutes;

/// <summary>The fact-first pipeline with a scripted model (made-up meeting, no real people).</summary>
public sealed class FactMinutesTests
{
    private const string Note = """
        - 参加: 森、石井（架空）
        - 事前資料: 予算の上限は三百万円

        以下、AIによる文字起こし
        えーと、始めます。リリース日は十一月二十日に決まりました。テスト計画は森さんが来週の水曜日までに作ることになりました。予算はまだ決まっていません。次回までに検討しましょう。広報のブログを出したらどうかという提案がありました。
        """;

    // B001 えーと、始めます。 B002 リリース日… B003 テスト計画… B004 予算はまだ… B005 次回までに… B006 広報…
    private const string Answer = """
        はい、抽出します。
        {"type":"決定","topic":"リリース日","text":"リリース日は11月20日","src":["B002"],"who":"","due":""}
        {"type":"作業","topic":"テスト計画","text":"テスト計画を作る","src":["B003"],"who":"森","due":"来週の水曜日"}
        {"type":"作業","topic":"テスト計画","text":"テスト計画のレビュー","src":["B003"],"who":"石井","due":"金曜日"}
        {"type":"決定","topic":"予算","text":"予算は見積もり後に決める","src":["B004","B005"],"who":"","due":""}
        {"type":"情報","topic":"予算","text":"予算の上限は三百万円","src":["A002"],"who":"","due":""}
        {"type":"決定","topic":"リリース日について","text":"リリース日は十一月二十日","src":["B002"],"who":"","due":""}
        {"type":"情報","topic":"会場","text":"会場は第3会議室","src":["B099"],"who":"","due":""}
        {"type":"決定","topic":"リリース日","text":"リリース日は11月21日","src":["B002"]}
        {"type":"提案", 壊れた行
        """;

    private sealed class Scripted(params string[] answers) : ILocalTextGenerator
    {
        private int _next;

        public List<string> Prompts { get; } = [];

        public string ModelName => "テスト用モデル";

        public Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken, IProgress<int>? progress = null)
        {
            Prompts.Add(user);
            // Once the script is used up (e.g. the recovery pass), nothing more is found.
            return Task.FromResult(_next < answers.Length ? answers[_next++] : string.Empty);
        }
    }

    private static (MinutesDraft Draft, Scripted Model) Run(string note, params string[] answers) =>
        Run(MinutesPriority.Auto, note, answers);

    private static (MinutesDraft Draft, Scripted Model) Run(MinutesPriority priority, string note, params string[] answers)
    {
        var model = new Scripted(answers);
        var draft = new MinutesGenerator(model, MinutesStyle.Facts) { Priority = priority }
            .GenerateAsync(note, "定例", new DateTime(2026, 10, 16, 9, 0, 0)).Result;
        return (draft, model);
    }

    [Fact]
    public void Units_AreNumberedHere_NotesByLine_TranscriptBySentence()
    {
        var units = SourceUnits.From(NoteSections.Split(Note));
        Assert.Equal(["A001", "A002", "B001", "B002", "B003", "B004", "B005", "B006"], units.Select(u => u.Id));
        Assert.True(units[0].FromNotes);
        Assert.Equal("リリース日は十一月二十日に決まりました。", units[3].Text);
        Assert.True(units[2].IsNoise);  // "えーと、始めます。": a filler and an opening phrase only
        Assert.False(units[3].IsNoise);
    }

    [Fact]
    public void Prompt_ShowsNumberedUnits_AndNotesGoToTheFirstPart()
    {
        var (_, model) = Run(Note, Answer);
        string prompt = model.Prompts[0];
        Assert.Contains("A002 - 事前資料: 予算の上限は三百万円", prompt);
        Assert.Contains("B002 リリース日は十一月二十日に決まりました。", prompt);
        Assert.DoesNotContain(TranscriptInsertion.Header, prompt);
    }

    [Fact]
    public void Facts_AreChecked_AgainstTheCitedUnits()
    {
        var (draft, _) = Run(Note, Answer);
        var facts = draft.Facts!;

        var review = facts.Single(f => f.Text == "テスト計画のレビュー");
        Assert.Equal(string.Empty, review.Who);  // 石井 is not in B003
        Assert.Equal(string.Empty, review.Due);  // 金曜日 is not in B003
        Assert.Contains(review.Issues, i => i.Contains("担当"));

        var task = facts.Single(f => f.Text == "テスト計画を作る");
        Assert.Equal("森", task.Who);
        Assert.Empty(task.Issues);

        // "まだ決まっていません" / "検討しましょう" cited for a "decision": it is open.
        Assert.Equal(FactKind.Open, facts.Single(f => f.Text.StartsWith("予算は見積もり")).Kind);

        // A number not in the cited unit is flagged; 11月20日 = 十一月二十日 is not.
        Assert.Contains(facts.Single(f => f.Text.EndsWith("11月21日")).Issues, i => i.Contains("21"));
        Assert.Empty(facts.Single(f => f.Text == "リリース日は11月20日").Issues);

        // A unit number that does not exist.
        Assert.Contains(facts.Single(f => f.Text.StartsWith("会場")).Issues, i => i.Contains("番号"));
        Assert.Contains(draft.Warnings, w => w.Contains("1 行を読み取れません"));
    }

    [Fact]
    public void Minutes_AreWrittenFromTheFacts_MergedByTopic_WithSources()
    {
        var (draft, _) = Run(Note, Answer);
        string md = draft.Markdown;
        int body = md.IndexOf("## 概要", StringComparison.Ordinal);
        string minutes = md[body..MinutesValidator.AppendixStart(md)];

        Assert.Equal(1, CountOf(minutes, "## リリース日"));                 // "リリース日について" merged into it
        Assert.Equal(1, CountOf(minutes, "リリース日は11月20日"));           // the same statement twice: kept once …
        Assert.DoesNotContain("リリース日は十一月二十日 〔", minutes);
        Assert.Contains("リリース日は11月21日", minutes);                    // … but another number is another statement
        Assert.Contains("- [ ] テスト計画を作る — 担当: 森 / 期限: 来週の水曜日 〔B003〕", minutes);
        Assert.Contains("- 予算の上限は三百万円 〔A002〕（メモより）", minutes);
        Assert.Contains("【未決】予算は見積もり後に決める", minutes);
        Assert.Contains("## 原文（番号付き）", md);
        Assert.Contains("- B002 リリース日は十一月二十日に決まりました。", md);
    }

    [Fact]
    public void ImportantUncitedStatements_AreExtractedAgain_AndJoinTheLedger()
    {
        // First pass misses the proposal (B006); the recovery pass is asked about exactly that range.
        const string recovered = "{\"type\":\"提案\",\"topic\":\"広報\",\"text\":\"広報のブログを出したらどうかという提案があった\",\"src\":[\"B006\"]}";
        var (draft, model) = Run(MinutesPriority.Quality, Note, Answer, recovered);
        Assert.Equal(2, model.Prompts.Count);
        Assert.Contains("議事録にまだ入っていない", model.Prompts[1]);
        Assert.Contains("B006 広報のブログを", model.Prompts[1]);
        Assert.DoesNotContain("B002 リリース日", model.Prompts[1]); // only the missing range
        Assert.Contains(draft.Facts!, f => f.Sources.Contains("B006") && f.Kind == FactKind.Proposal);
        Assert.DoesNotContain(draft.Warnings, w => w.Contains("B006"));
        Assert.Matches("found=1", draft.Run!.InputMode);
    }

    [Fact]
    public void ContradictingFacts_AreBothKept_AndFlagged()
    {
        var merged = FactMinutes.Merge(
        [
            new Fact(FactKind.Decision, "リリース日", "リリース日は11月20日", ["B001"], "", "") { Id = "F001" },
            new Fact(FactKind.Decision, "リリース日", "リリース日は11月27日", ["B009"], "", "") { Id = "F002" },
        ]);
        var facts = merged.Single().Facts;
        Assert.Equal(2, facts.Count);
        Assert.Contains("F002と食い違う", facts[0].Issues);
        Assert.Contains("F001と食い違う", facts[1].Issues);
    }

    [Fact]
    public void UncitedStatements_AreReported_FillersAreNot()
    {
        var (draft, _) = Run(Note, Answer);
        Assert.Contains(draft.Warnings, w => w.Contains("B006")); // the proposal about the blog was not taken
        Assert.DoesNotContain(draft.Warnings, w => w.Contains("B001")); // "えーと、始めます。" is not content
        Assert.Contains("議事録のどの項目にも使われていない発言: B006", draft.Markdown);
    }

    [Fact]
    public void LongTranscript_IsGroupedByTokens_EveryUnitOnce_WithContextFromThePreviousPart()
    {
        string sentence = "会議では資料の確認と次の作業の分担について詳しく話しました。";
        string note = "- メモ\n\n" + TranscriptInsertion.Header + "\n" + string.Concat(Enumerable.Repeat(sentence, 600));
        var units = SourceUnits.From(NoteSections.Split(note));
        var parts = FactMinutes.Group(units);
        Assert.True(parts.Count >= 3);
        Assert.Equal(units.Count, parts.Sum(p => p.Count));
        Assert.Equal(units.Select(u => u.Id), parts.SelectMany(p => p).Select(u => u.Id));
        Assert.All(parts, p => Assert.True(p.Sum(u => FactMinutes.EstimateTokens(u.Text) + 3) <= 8192 - FactPrompt.MaxAnswerTokens - 512));

        var (draft, model) = Run(note, Enumerable.Repeat("{\"type\":\"情報\",\"topic\":\"資料\",\"text\":\"資料を確認した\",\"src\":[\"B001\"]}", parts.Count).ToArray());
        // Every part, then at most MaxRecoveryRanges recovery calls for statements no fact used.
        Assert.InRange(model.Prompts.Count, parts.Count, parts.Count + FactMinutes.MaxRecoveryRanges);
        Assert.Contains("【文脈】", model.Prompts[1]);
        Assert.Empty(draft.Run!.Unprocessed);
    }

    [Fact]
    public void ContextUnits_CannotBeCitedAsSources()
    {
        var units = new[] { new SourceUnit("B001", "前の部分の文。", false), new SourceUnit("B002", "この部分の文。", false) }.ToDictionary(u => u.Id);
        var fact = FactMinutes.Check(new Fact(FactKind.Info, "t", "前の部分", ["B001"], "", ""), units, new HashSet<string> { "B002" });
        Assert.Empty(fact.Sources);
        Assert.NotEmpty(fact.Issues);
    }

    [Theory]
    [InlineData("決定", "テスト計画は森さんが来週の水曜日までに作ることになりました。", "テスト計画を作る", "森", "来週の水曜日")]
    [InlineData("提案", "問い合わせフォームの修正は石井さんが今週中に行います。", "問い合わせフォームの修正", "石井", "今週中")]
    public void ADecisionNamingWhoAndBy_IsATask_WithBothTakenFromTheSource(string kind, string said, string text, string who, string due)
    {
        var units = new[] { new SourceUnit("B001", said, false) }.ToDictionary(u => u.Id);
        var fact = FactMinutes.Check(new Fact(kind, "t", text, ["B001"], "", ""), units, new HashSet<string> { "B001" });
        Assert.Equal(FactKind.Task, fact.Kind);
        Assert.Equal(who, fact.Who);
        Assert.Equal(due, fact.Due);
        Assert.Empty(fact.Issues);
    }

    [Theory]
    [InlineData("今日の定例会議でリリース日を決めました。", FactKind.Decision)]
    [InlineData("リリース日は来週決めましょう。", FactKind.Open)]
    [InlineData("担当者はまだ決めていません。", FactKind.Open)]
    public void DecidedInPlainWords_StaysADecision(string said, string expected)
    {
        var units = new[] { new SourceUnit("B001", said, false) }.ToDictionary(u => u.Id);
        var fact = FactMinutes.Check(new Fact(FactKind.Decision, "リリース日", said.TrimEnd('。'), ["B001"], "", ""), units, new HashSet<string> { "B001" });
        Assert.Equal(expected, fact.Kind);
    }

    [Fact]
    public void Fillers_KeepTheirNumbers_ButAreNotSent()
    {
        var units = new List<SourceUnit> { new("B001", "えーと、はい。", false), new("B002", "予算はまだ決まっていません。", false) };
        string prompt = FactPrompt.User([], units, 0, 1);
        Assert.DoesNotContain("B001", prompt);
        Assert.Contains("B002 予算はまだ決まっていません。", prompt);
    }

    [Theory]
    [InlineData("十一月二十日", "11月20日")]
    [InlineData("百二十件", "120件")]
    [InlineData("約七割", "約7割")]
    [InlineData("三百万円", "3000000円")]
    [InlineData("二〇二六年", "2026年")]
    public void KanjiNumerals_BecomeDigits(string kanji, string digits) => Assert.Equal(digits, Numerals.Arabic(kanji));

    [Fact]
    public void Merge_SameStatementAsInfoAndDecision_IsOne_TopicsSharingASubjectAreOne()
    {
        var merged = FactMinutes.Merge(
        [
            new Fact(FactKind.Info, "リリース日", "リリース日は十一月二十日に決まりました", ["B110"], "", ""),
            new Fact(FactKind.Decision, "リリース日", "リリース日は十一月二十日に決まりました", ["B110"], "", ""),
            new Fact(FactKind.Info, "アンケートの結果", "回答は百二十件", ["B200"], "", ""),
            new Fact(FactKind.Info, "アンケートの不満点", "文字が小さい", ["B201"], "", ""),
            new Fact(FactKind.Info, "色の案", "三つの案を出した", ["B036"], "", ""),
        ]);
        Assert.Equal(["リリース日", "アンケート", "色の案"], merged.Select(t => t.Topic));
        Assert.Equal(FactKind.Decision, merged[0].Facts.Single().Kind);
        Assert.Equal(2, merged[1].Facts.Count);
    }

    [Fact]
    public void AWrongCitation_IsKept_AndFlagged_WithTheOneFullMatchAsASuggestion()
    {
        var units = new[]
        {
            new SourceUnit("B001", "回答は百二十件ありました。", false),
            new SourceUnit("B002", "アプリに満足しているという回答が約七割でした。", false),
            new SourceUnit("B003", "画面の共有を止めますね。", false),
        }.ToDictionary(u => u.Id);
        var allowed = new HashSet<string>(units.Keys);
        var fact = FactMinutes.Check(new Fact(FactKind.Info, "アンケート", "回答は百二十件ありました", ["B003"], "", ""), units, allowed);
        Assert.Equal(["B003"], fact.Sources);                 // never moved automatically
        Assert.False(fact.Verified);
        Assert.Contains("候補: B001", fact.Issues);
        Assert.Contains(fact.Issues, i => i.Contains("内容を示していない"));

        // No clear match: the citation stays and is flagged.
        var unclear = FactMinutes.Check(new Fact(FactKind.Info, "x", "会場は第三会議室", ["B002"], "", ""), units, allowed);
        Assert.Equal(["B002"], unclear.Sources);
        Assert.NotEmpty(unclear.Issues);
    }

    [Fact]
    public void ADecisionNotSaidToBeDecided_IsNotShownAsADecision()
    {
        var units = new[]
        {
            new SourceUnit("B001", "移転日は三月の中旬の予定です。", false),
            new SourceUnit("B002", "参加者は二十五人です。", false),
        }.ToDictionary(u => u.Id);
        var allowed = new HashSet<string>(units.Keys);
        Assert.Equal(FactKind.Open, FactMinutes.Check(new Fact(FactKind.Decision, "移転日", "移転日は3月中旬", ["B001"], "", ""), units, allowed).Kind);
        Assert.Equal(FactKind.Info, FactMinutes.Check(new Fact(FactKind.Decision, "参加者", "参加者は25人", ["B002"], "", ""), units, allowed).Kind);
    }

    [Fact]
    public void AFactCitingOnlyContextLines_IsDropped_NotKeptWithoutASource()
    {
        var units = new[] { new SourceUnit("B001", "前の部分の文。", false), new SourceUnit("B002", "この部分の文。", false) }.ToDictionary(u => u.Id);
        var allowed = new HashSet<string> { "B002" };
        Assert.True(FactMinutes.OnlyContext(new Fact(FactKind.Info, "t", "前の部分", ["B001"], "", ""), allowed, units));
        Assert.False(FactMinutes.OnlyContext(new Fact(FactKind.Info, "t", "両方", ["B001", "B002"], "", ""), allowed, units));
        Assert.False(FactMinutes.OnlyContext(new Fact(FactKind.Info, "t", "番号が原文にない", ["B099"], "", ""), allowed, units)); // flagged instead
    }

    [Fact]
    public void OneSentenceGivenTwoKinds_IsOneFact_TheMoreSpecificKind()
    {
        var merged = FactMinutes.Merge(
        [
            new Fact(FactKind.Open, "机の配置", "机の配置はまだ決まっていません", ["B006"], "", ""),
            new Fact(FactKind.Proposal, "机の配置", "机の配置は、まだ決まっていません", ["B006"], "", ""),
        ]);
        Assert.Equal(FactKind.Open, merged.Single().Facts.Single().Kind);
    }

    [Fact]
    public void DecideLater_IsOpen_NotADecision()
    {
        var units = new[] { new SourceUnit("B001", "予算は見積もりがそろってから決めましょう。", false) }.ToDictionary(u => u.Id);
        var fact = FactMinutes.Check(new Fact(FactKind.Decision, "予算", "予算は見積もり後に決める", ["B001"], "", ""), units, new HashSet<string> { "B001" });
        Assert.Equal(FactKind.Open, fact.Kind);
    }

    [Fact]
    public void PrettyPrintedJson_InACodeBlock_AndMixedKinds_AreRead()
    {
        // The shape Qwen3 1.7B answered with for a long meeting.
        const string answer = """
            ```json
            {
              "type": "情報・意見",
              "topic": "アンケートの結果",
              "text": "満足しているという回答が約七割で、「文字が小さい」という声もあった",
              "src": ["B001"],
              "who": "",
              "due": ""
            }
            ```
            {"type":"決定","topic":"x","text":"途中で切れた回答
            """;
        var facts = FactMinutes.Parse(answer, out int malformed);
        Assert.Equal(FactKind.Info, facts.Single().Kind);
        Assert.Contains("「文字が小さい」", facts.Single().Text);
        Assert.Equal(1, malformed); // the cut-off object is counted, not ignored
    }

    private static int CountOf(string text, string part) =>
        (text.Length - text.Replace(part, string.Empty, StringComparison.Ordinal).Length) / part.Length;

    [Fact]
    public void SpeedPriority_RecoversOnlyCriticalStatements_InOneRequest()
    {
        // B006 (a proposal) is not critical: on a CPU (Auto) it is reported, not extracted again.
        var (auto, autoModel) = Run(Note, Answer);
        Assert.Single(autoModel.Prompts);
        Assert.Contains("speed", auto.Run!.InputMode);
        Assert.Contains(auto.Warnings, w => w.Contains("B006"));

        // Critical statements far apart go to the model together, in a single request.
        const string note = """
            - メモ

            以下、AIによる文字起こし
            開始します。会場は市民ホールに決まりました。天気の話をしました。景品の話もしました。受付は佐藤さんが金曜日までに準備します。雑談をしました。参加費は五百円で合意しました。
            """;
        const string first = """{"type":"情報","topic":"天気","text":"天気の話をした","src":["B003"]}""";
        var (draft, model) = Run(MinutesPriority.Speed, note, first);
        Assert.Equal(2, model.Prompts.Count);
        Assert.Contains("B002 会場は市民ホール", model.Prompts[1]);
        Assert.Contains("B005 受付は佐藤さん", model.Prompts[1]);
        Assert.Contains("B007 参加費は五百円", model.Prompts[1]);
        Assert.Contains("requests=1", draft.Run!.InputMode);
    }

    [Fact]
    public void QualityPriority_SendsSeveralRangesPerRequest()
    {
        const string note = """
            - メモ

            以下、AIによる文字起こし
            開始します。会場は市民ホールに決まりました。天気の話をしました。景品の話もしました。広報の課題が出ました。雑談をしました。旅行の話をしました。参加費は五百円で合意しました。
            """;
        const string first = """{"type":"情報","topic":"天気","text":"天気の話をした","src":["B003"]}""";
        var (draft, model) = Run(MinutesPriority.Quality, note, first);
        Assert.Equal(2, model.Prompts.Count); // three ranges, one request
        Assert.Contains("B005 広報の課題", model.Prompts[1]);
        Assert.Contains("quality", draft.Run!.InputMode);
    }

    [Fact]
    public void ProceduralRemarks_AreNotFacts_NorReportedAsMissing()
    {
        Assert.True(SourceUnits.IsProcedural("次に予算です。"));
        Assert.True(SourceUnits.IsProcedural("では、駐車場の件に移ります。"));
        Assert.True(SourceUnits.IsProcedural("続いて広報について。"));
        Assert.False(SourceUnits.IsProcedural("次に予算は百万円です。"));
        Assert.False(SourceUnits.IsProcedural("まず先週の進捗を確認しました。"));
        Assert.False(SourceUnits.IsProcedural("予算です。")); // no transition word

        const string note = """
            - メモ

            以下、AIによる文字起こし
            次に予算です。予算は百八十万円に増やすことで承認されました。
            """;
        const string answer = """
            {"type":"提案","topic":"図面","text":"次に予算です","src":["B001"]}
            {"type":"決定","topic":"予算","text":"予算は百八十万円に増やす","src":["B002"]}
            """;
        var (draft, _) = Run(note, answer);
        Assert.DoesNotContain(draft.Facts!, f => f.Text.Contains("次に予算"));
        Assert.Contains(draft.Facts!, f => f.Kind == FactKind.Decision);
        Assert.DoesNotContain(draft.Warnings, w => w.Contains("B001"));
    }

    [Fact]
    public void VagueTask_GetsItsObject_FromTheStatementBefore_OrIsMarkedForReview()
    {
        var units = SourceUnits.From(NoteSections.Split("""
            - メモ

            以下、AIによる文字起こし
            引っ越し業者は二社から見積もりを取ります。山田さんが来週までに取ります。加藤さんが今月中にやります。
            """));
        var vague = new Fact(FactKind.Task, "引越し業者", "山田さんが来週までに取ります", ["B002"], "山田", "来週");
        var clear = FactMinutes.Clarify(vague, units);
        Assert.Equal("山田さんが来週までに二社から見積もりを取ります", clear.Text);
        Assert.Equal(["B001", "B002"], clear.Sources);
        Assert.Empty(clear.Issues);

        var unknown = FactMinutes.Clarify(new Fact(FactKind.Task, "その他", "加藤さんが今月中にやります", ["B003"], "加藤", "今月中"), units);
        Assert.Equal("加藤さんが今月中にやります", unknown.Text);
        Assert.Contains("作業の内容が原文から分からない", unknown.Issues);

        // A task that says what is left alone.
        var plain = new Fact(FactKind.Task, "ポスター", "ポスターの作成は高橋さんが来週の金曜日までに行います", ["B002"], "高橋", "来週の金曜日");
        Assert.Same(plain, FactMinutes.Clarify(plain, units));
        var named = new Fact(FactKind.Task, "資料", "資料は森さんが明日までに送ります", ["B002"], "森", "明日");
        Assert.Same(named, FactMinutes.Clarify(named, units));
    }

    [Fact]
    public void RepeatedNumbers_AreNotExtractedAgain()
    {
        var units = SourceUnits.From(NoteSections.Split("""
            - メモ

            以下、AIによる文字起こし
            開催日は十二月三日に決まりました。あ、三日、三日で大丈夫です。参加者は二十五人です。
            """));
        var facts = new List<Fact> { new(FactKind.Decision, "開催日", "開催日は十二月三日に決まりました", ["B001"], "", "") };
        var ranges = FactMinutes.RecoveryRanges(units, facts, criticalOnly: true);
        Assert.Equal(["B003"], ranges.SelectMany(r => r).Select(u => u.Id)); // 25 is new, 3 is not
    }
}
