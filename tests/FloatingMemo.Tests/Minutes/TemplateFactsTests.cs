using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Minutes;

/// <summary>Templates filled from the checked fact ledger (made-up meeting and template).</summary>
public sealed class TemplateFactsTests
{
    private const string Note = "- 参加: 森（架空）\n\n" + TranscriptInsertion.Header + "\n"
        + "リリース日は十一月一日に決まりました。資料の準備は森さんが金曜日までに行います。花火を上げるかはまだ決まっていません。会場の下見は先週終わりました。";

    // B001 リリース日… B002 資料… B003 花火… B004 会場の下見…
    private const string Extraction = """
        {"type":"決定","topic":"リリース日","text":"リリース日は11月1日","src":["B001"]}
        {"type":"作業","topic":"資料","text":"資料の準備","src":["B002"],"who":"森","due":"金曜日"}
        {"type":"未決","topic":"花火","text":"花火を上げるかは未定","src":["B003"]}
        {"type":"情報","topic":"会場","text":"会場の下見は先週終わった","src":["B004"]}
        """;

    private static readonly MinutesTemplate Template = new()
    {
        Name = "テスト用",
        Sections =
        [
            new() { Title = "概要" },
            new() { Title = "決定事項" },
            new() { Title = "準備の状況", Instruction = "準備で済んだこと" },
            new() { Title = "ToDo" },
            new() { Title = "未決事項" },
            new() { Title = "予算", WhenMissing = TemplateRules.Unconfirmed },
            new() { Title = "共有メモ", Kind = TemplateRules.Draft },
        ],
    };

    private sealed class Scripted(params string[] answers) : ILocalTextGenerator
    {
        private int _next;

        public List<(string System, string User)> Calls { get; } = [];

        public string ModelName => "テスト用モデル";

        public Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken, IProgress<int>? progress = null)
        {
            Calls.Add((system, user));
            return Task.FromResult(_next < answers.Length ? answers[_next++] : string.Empty);
        }
    }

    private static (MinutesDraft Draft, Scripted Model) Run(params string[] answers)
    {
        var model = new Scripted(answers);
        var draft = new MinutesGenerator(model, MinutesStyle.Facts, Template).GenerateAsync(Note, "定例", new DateTime(2026, 10, 17)).Result;
        return (draft, model);
    }

    [Fact]
    public void KindsGoToTheirHeadings_TheModelOnlyPlacesTheRest_DraftsAreChecked()
    {
        // extraction, (recovery: nothing), heading assignment, draft
        var (draft, model) = Run(Extraction, "F004: 3\nF001: 3\nF099: 3\nF003: 9",
            "## 共有メモ\nリリース日は11月1日に決まりました。[F001]\n予算は100万円です。[F001]");
        string md = draft.Markdown;

        var assign = model.Calls.Single(c => c.System == TemplateFacts.AssignSystem);
        Assert.Contains("F004 [情報]", assign.User);
        Assert.DoesNotContain("F001 [決定]", assign.User); // decisions have their heading by kind

        Assert.Contains("## 決定事項\n- 【決定】リリース日は11月1日 〔B001〕", md);
        Assert.Contains("## 準備の状況\n- 会場の下見は先週終わった 〔B004〕", md);
        Assert.Contains("## ToDo\n- [ ] 資料の準備 — 担当: 森 / 期限: 金曜日 〔B002〕", md);
        Assert.Contains("## 未決事項\n- 【未決】花火を上げるかは未定 〔B003〕", md);
        Assert.Contains("## 予算\n- 未確認", md);                       // nothing said: the template's rule
        Assert.Contains("リリース日は11月1日に決まりました。〔B001〕", md); // draft sentence carried by F001
        Assert.DoesNotContain("100万円", md);                          // draft sentence no fact carries
        Assert.Contains("送信前の下書き", md);
        Assert.Contains("- 話題:", md);                                 // overview from the facts
        Assert.Contains("## 原文（番号付き）", md);
    }

    [Fact]
    public void FactsTheTemplateHasNoPlaceFor_AreKeptUnderSonota()
    {
        var (draft, _) = Run(Extraction, "F004: 0", string.Empty);
        Assert.Contains("## その他（テンプレートの見出しに当てはまらない内容）\n- 会場の下見は先週終わった", draft.Markdown);
        Assert.Contains("## 共有メモ", draft.Markdown);
    }

    [Fact]
    public void TheSmallModel_KeepsTheFormerTemplatePath()
    {
        var model = new Scripted("## 決定事項\n- 【決定】リリース日は十一月一日（原文:「リリース日は十一月一日に決まりました」）");
        var draft = new MinutesGenerator(model, MinutesStyle.FixedSections, Template).GenerateAsync(Note, "定例", new DateTime(2026, 10, 17)).Result;
        Assert.DoesNotContain(model.Calls, c => c.System == FactPrompt.System);
        Assert.Contains("## 決定事項", draft.Markdown);
    }
}
