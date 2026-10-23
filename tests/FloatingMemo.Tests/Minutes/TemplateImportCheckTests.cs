using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

/// <summary>Template files are checked and previewed before saving; other shapes get guidance, not a guess.</summary>
public sealed class TemplateImportCheckTests
{
    [Fact]
    public void ASupportedTemplate_IsPreviewed()
    {
        var check = TemplateImportCheck.Inspect("""
            { "schemaVersion": 1, "name": "定例（架空）", "sections": [ { "title": "決定事項" }, { "title": "ToDo" } ] }
            """, "t.json");

        Assert.Null(check.Error);
        Assert.Equal(["決定事項", "ToDo"], check.Template!.Sections.Select(s => s.Title));
    }

    [Fact]
    public void AnotherShape_SaysWhatIsMissing_AndWhereTheHeadingsSeemToBe()
    {
        var check = TemplateImportCheck.Inspect("""
            { "templateName": "定例会（架空）", "items": [ { "name": "会場", "guide": "決まったこと" }, { "name": "予算" } ] }
            """, "custom.json");

        Assert.Null(check.Template);
        Assert.NotNull(check.Error);
        Assert.Contains(check.Hints, h => h.Contains("schemaVersion"));
        Assert.Contains(check.Hints, h => h.Contains("\"sections\"") && h.Contains("必須"));
        Assert.Contains(check.Hints, h => h.Contains("\"items\"") && h.Contains("会場・予算"));
    }

    [Fact]
    public void ASectionWithoutTitle_IsNamed()
    {
        var check = TemplateImportCheck.Inspect("""
            { "schemaVersion": 1, "sections": [ { "title": "概要" }, { "heading": "ToDo" } ] }
            """, "t.json");

        Assert.Null(check.Template);
        Assert.Contains(check.Hints, h => h.Contains("2 番目") && h.Contains("ToDo"));
    }

    [Fact]
    public void BrokenJson_GetsAPlainHint()
    {
        var check = TemplateImportCheck.Inspect("{ \"schemaVersion\": 1, \"sections\": [ ", "t.json");

        Assert.NotNull(check.Error);
        Assert.Contains(check.Hints, h => h.Contains("カンマ"));
    }
}
