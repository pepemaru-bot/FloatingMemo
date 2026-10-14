using System.Diagnostics;
using FloatingMemo.Services.Minutes;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Minutes;

/// <summary>Runs only when FLOATINGMEMO_LLM=1 and the model files exist (they are large; never in CI).</summary>
public sealed class LlmFactAttribute : FactAttribute
{
    public LlmFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM") != "1")
        {
            Skip = "Set FLOATINGMEMO_LLM=1 to compare the local LLMs (needs the GGUF models).";
        }
    }
}

/// <summary>
/// Compares the candidate models on synthetic meetings (made up for testing: no real people,
/// companies or meetings). Prints speed, the checks' findings and the drafts for review.
/// </summary>
public sealed class MinutesBenchmark(ITestOutputHelper output)
{
    public const string MeetingA =
        "それでは週次の定例会議を始めます。まず先週の進捗です。ログイン画面の改修は予定どおり完了しました。" +
        "次にリリース日についてです。テストの状況を見て、リリース日は十一月十四日に決まりました。" +
        "リリースノートの作成は山本さんが担当し、来週の水曜日までに用意することになりました。" +
        "問い合わせフォームのデザインは、もう少し案を出したいという意見がありました。誰かが案を作ったほうがいいと思います。" +
        "サーバーの費用については、見積もりを取ってから判断することにして、今日は決めませんでした。" +
        "次回は見積もりの結果を確認しましょう。以上で終わります。";

    public const string MeetingB =
        "地域清掃ボランティアの打ち合わせです。参加者は十二人の予定です。集合場所は駅前の広場にすることで合意しました。" +
        "ゴミ袋と軍手は鈴木さんが準備します。雨の場合の対応はまだ決まっていません。中止にするか延期にするか、" +
        "来週までに意見を集めたいと思います。チラシを作ってはどうかという提案もありました。";

    [LlmFact]
    public async Task CompareModels()
    {
        foreach (string name in LlamaTextGenerator.KnownModels.Reverse())
        {
            string path = LlamaTextGenerator.AvailableModels().FirstOrDefault(m => m.FileName == name)?.Path ?? name;
            if (!File.Exists(path))
            {
                output.WriteLine($"(skip {name}: not downloaded)");
                continue;
            }

            using var model = new LlamaTextGenerator(path);
            var generator = new MinutesGenerator(model);
            output.WriteLine($"===== {name} =====");
            var load = Stopwatch.StartNew();
            await model.CompleteAsync("短く答えてください。", "こんにちは /no_think", 4, CancellationToken.None); // load + warm-up
            output.WriteLine($"load + warm-up: {load.Elapsed.TotalSeconds:0.0} s");
            foreach (var (label, transcript) in new[] { ("A", MeetingA), ("B", MeetingB) })
            {
                var watch = Stopwatch.StartNew();
                var draft = await generator.GenerateAsync(transcript, "会議" + label, DateTime.Now);
                watch.Stop();
                output.WriteLine($"--- meeting {label}: {watch.Elapsed.TotalSeconds:0.0} s {draft.Warnings.Count} warnings");
                foreach (string warning in draft.Warnings)
                {
                    output.WriteLine("  ! " + warning);
                }

                output.WriteLine(draft.Markdown);
                Assert.All(MinutesPrompt.Sections, s => Assert.Contains("## " + s, draft.Markdown));
            }
        }
    }
}
