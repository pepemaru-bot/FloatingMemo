using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Transcription;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Transcription;

/// <summary>
/// FLOATINGMEMO_ASR_EVAL=1 and FLOATINGMEMO_HAAS_DIR (speech.exe, a Haas-ja GGUF, the Silero VAD
/// GGUF): the app's own Haas-ja pipeline (<see cref="SpeechCppTranscriptionService"/>) over the
/// made-up meetings, written in the common shape (&lt;case&gt;/asr/haas-app-&lt;variant&gt;-&lt;audio&gt;.json).
/// FLOATINGMEMO_HAAS_VARIANTS: "name|pad|joinGap|maxChunk|minSilenceMs|sentenceEnds|keywords[|shortSeconds];…"
/// (keywords: none, term = written forms only, read = 表記=よみ as the dictionary gives them).
/// FLOATINGMEMO_ASR_CASES: the meetings to run (comma-separated; tuning uses the dev ones only).
/// </summary>
public sealed class HaasEvalExport(ITestOutputHelper output)
{
    // Readings of the parts the scenarios do not mark, as a dictionary's owner would write the
    // whole term (same table as tests/asr/eval/run_haas.py); the legacy meeting has no scenario.
    private static readonly Dictionary<string, string> Extra = new()
    {
        ["高校"] = "こうこう", ["学院"] = "がくいん", ["高専"] = "こうせん", ["架空ホール"] = "かくうホール", ["緑川物流"] = "みどりかわぶつりゅう",
        ["白石製作所"] = "しろいしせいさくしょ", ["稟議書"] = "りんぎしょ", ["青葉商事"] = "あおばしょうじ", ["中央工務店"] = "ちゅうおうこうむてん",
        ["ホール"] = "ホール", ["商事"] = "しょうじ",
    };

    [Fact]
    public async Task ExportHaas()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_EVAL") != "1" || Environment.GetEnvironmentVariable("FLOATINGMEMO_HAAS_DIR") is not { Length: > 0 } haas)
        {
            return;
        }

        string root = Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_EVAL_DIR")!;
        var cases = (Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_CASES") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        string variants = Environment.GetEnvironmentVariable("FLOATINGMEMO_HAAS_VARIANTS") ?? "app|0.2|0|20|150|1|read";
        var marks = ReadingMarks();
        foreach (string dir in Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "truth.json"))).Order())
        {
            if (cases.Count > 0 && !cases.Contains(Path.GetFileName(dir)))
            {
                continue;
            }

            var truth = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "truth.json")))!;
            var entries = truth["terms"]!.AsArray().Select(t => t!.GetValue<string>())
                .Select(term => new TermEntry { Term = term, Reading = ReadingOf(term, marks), WhisperHint = true }).ToList();
            Directory.CreateDirectory(Path.Combine(dir, "asr"));
            foreach (string spec in variants.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = spec.Split('|');
                var chunking = new SpeechChunkOptions
                {
                    PadSeconds = double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture),
                    JoinGapSeconds = double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture),
                    MaxChunkSeconds = double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture),
                    ShortSeconds = f.Length > 7 ? double.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture) : 0,
                };
                string? keywords = f[6] switch
                {
                    "read" => TermDictionary.HaasKeywords(entries),
                    "term" => TermDictionary.HaasKeywords(entries.Select(e => new TermEntry { Term = e.Term })),
                    _ => null,
                };
                foreach (string audio in new[] { "clean", "noisy" })
                {
                    string setting = $"haas-app-{f[0]}";
                    string target = Path.Combine(dir, "asr", $"{setting}-{audio}.json");
                    if (File.Exists(target))
                    {
                        continue; // already exported: results are never overwritten
                    }

                    // FLOATINGMEMO_HAAS_MODEL (file name in the folder), _DEVICE (auto|gpu|cpu), _THREADS: the CPU benchmark for small PCs.
                    string modelFile = Environment.GetEnvironmentVariable("FLOATINGMEMO_HAAS_MODEL") ?? "Haas-616M-ja-v1.0-Q8_0.gguf";
                    int? threads = int.TryParse(Environment.GetEnvironmentVariable("FLOATINGMEMO_HAAS_THREADS"), out int n) ? n : null;
                    var service = new SpeechCppTranscriptionService(Path.Combine(haas, "speech.exe"), Path.Combine(haas, modelFile),
                        Path.Combine(haas, "silero-vad-309K-v6.2-F32.gguf"), Environment.GetEnvironmentVariable("FLOATINGMEMO_HAAS_DEVICE") ?? "auto", threads)
                    {
                        Chunking = chunking,
                        MinSilenceMs = int.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture),
                        AddSentenceEnds = f[5] == "1",
                        KeywordsProvider = () => keywords,
                    };

                    // The recognizer is another process: its memory is sampled by name.
                    long peakRam = 0, peakVram = 0, baseVram = Vram();
                    using var sampler = new Timer(_ =>
                    {
                        long sum = Process.GetProcessesByName("speech").Sum(p => { using (p) { return p.WorkingSet64; } });
                        peakRam = Math.Max(peakRam, sum);
                        peakVram = Math.Max(peakVram, Vram());
                    }, null, 0, 200);
                    var watch = Stopwatch.StartNew();
                    var result = await service.TranscribeFileAsync(Path.Combine(dir, audio + ".wav"), CancellationToken.None, null, null);
                    watch.Stop();
                    var json = new JsonObject
                    {
                        ["model"] = $"Haas-ja-v1.0 {modelFile} (speech.cpp 0.10.0, app pipeline, device {Environment.GetEnvironmentVariable("FLOATINGMEMO_HAAS_DEVICE") ?? "auto"}, threads {threads?.ToString() ?? "default"})",
                        ["setting"] = setting,
                        ["settings"] = $"{chunking} minSilenceMs={service.MinSilenceMs} sentenceEnds={service.AddSentenceEnds} keywords={f[6]}",
                        ["audio"] = audio,
                        ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 2),
                        ["peak_ram_mb"] = peakRam / 1024 / 1024,
                        ["peak_vram_mb"] = Math.Max(0, peakVram - baseVram),
                        ["speakers"] = false,
                        ["segments"] = new JsonArray(result.Segments.Select(s => (JsonNode)new JsonObject
                        {
                            ["start"] = Math.Round(s.Start.TotalSeconds, 2),
                            ["end"] = Math.Round(s.End.TotalSeconds, 2),
                            ["speaker"] = null,
                            ["text"] = s.Text,
                        }).ToArray()),
                    };
                    File.WriteAllText(target, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                    output.WriteLine($"{Path.GetFileName(dir)} {setting} {audio}: {watch.Elapsed.TotalSeconds:F1}s, {result.Segments.Count} segments, {service.LastDiagnostics?.RegionsTranscribed} pieces");
                }
            }
        }
    }

    // {表記|よみ} marks of every scenario (the readings the voices were given).
    private static Dictionary<string, string> ReadingMarks()
    {
        var marks = new Dictionary<string, string>(Extra);
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "asr-eval"), "*.json"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{([^|{}]+)\|([^{}]+)\}"))
            {
                marks[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        return marks;
    }

    private static string? ReadingOf(string term, Dictionary<string, string> marks)
    {
        string r = term;
        foreach (var (written, reading) in marks.OrderByDescending(kv => kv.Key.Length))
        {
            r = r.Replace(written, reading, StringComparison.Ordinal);
        }

        return r != term && Regex.IsMatch(r, @"^[ぁ-ヿー]+$") ? r : null;
    }

    private static long Vram()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return long.TryParse(text.Trim(), out long mb) ? mb : 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }
}
