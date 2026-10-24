using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Minutes;

/// <summary>Saved minutes carry no internal source numbers in their items (made-up meeting text).</summary>
public sealed class MinutesPresentationTests
{
    private const string Draft = """
        # 議事録

        ## 決定事項
        - 【決定】会場は架空ホールにする 〔B002〕
        - [ ] 備品を数える — 担当: 山本 / 期限: 10/30 〔B003・A001〕（メモ・発言）
        - 【決定】予算は五万円 〔B002〕
        - 根拠が付録にない項目 〔B099〕

        ---

        ## 原文（番号付き）
        - A001 備品の数を確認
        - B002 会場は架空ホール、予算は五万円で決まりです
        - B003 山本さんが三十日までに備品を数えます
        """;

    [Fact]
    public void Citations_BecomeFootnotesQuotingTheSource()
    {
        string reader = MinutesPresentation.ForReader(Draft.Replace("\r\n", "\n"));
        string body = reader[..reader.IndexOf("## 原文", StringComparison.Ordinal)];
        Assert.Contains("- 【決定】会場は架空ホールにする[^1]\n", body);
        Assert.Contains("担当: 山本 / 期限: 10/30[^2]（メモ・発言）", body);
        Assert.Contains("- 【決定】予算は五万円[^1]\n", body);                 // same source, same note
        Assert.Contains("[^1]: 原文: 「会場は架空ホール、予算は五万円で決まりです」", body);
        Assert.Contains("[^2]: 原文: 「山本さんが三十日までに備品を数えます」 ／ 「備品の数を確認」", body);
        Assert.Contains("根拠が付録にない項目 〔B099〕", body);                 // unknown number: kept, nothing lost
        Assert.DoesNotContain("〔B002", body);
        Assert.Contains("- B002 会場は架空ホール", reader);                    // appendix unchanged
    }

    [Fact]
    public void WithoutAppendix_NothingChanges()
    {
        const string text = "# メモ\n- 項目 〔B001〕\n";
        Assert.Equal(text, MinutesPresentation.ForReader(text));
    }

    [Fact]
    public void LongQuotes_AreShortened_AndBracketsCannotBreakTheFootnote()
    {
        string longText = new string('あ', 100) + "]";
        string draft = $"- 項目 〔B001〕\n\n---\n\n## 原文（番号付き）\n- B001 [x] {longText}\n";
        string reader = MinutesPresentation.ForReader(draft);
        Assert.Contains("[^1]: 原文: 「［x］", reader);
        Assert.Contains("…」", reader);
    }

    [Theory]
    [InlineData(MinutesStyle.Facts)]
    [InlineData(MinutesStyle.FixedSections)]
    public async Task GeneratedMinutes_HaveNoSourceNumbersInTheBody(MinutesStyle style)
    {
        string speech = "会場は架空ホールに決まりました。山本さんが金曜日までに備品を数えます。予算は五万円です。";
        string note = "- メモ\n\n" + TranscriptInsertion.Header + "\n" + speech;
        var draft = await new MinutesGenerator(new FakeMinutesModel(), style).GenerateAsync(note, "t", new DateTime(2026, 10, 16));
        string reader = MinutesPresentation.ForReader(draft.Markdown);
        int appendix = reader.IndexOf(MinutesPresentation.AppendixHeading, StringComparison.Ordinal);
        string body = appendix < 0 ? reader : reader[..appendix];
        Assert.DoesNotMatch(@"〔[AB]\d{3}", body);
    }
}
