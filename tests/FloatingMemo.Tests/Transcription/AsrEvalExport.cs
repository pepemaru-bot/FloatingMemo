using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatingMemo.Services.Transcription;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Transcription;

/// <summary>
/// FLOATINGMEMO_ASR_EVAL=1 only: the app's Whisper pipeline over the made-up meetings of
/// tests/asr/eval (FLOATINGMEMO_ASR_EVAL_DIR = the folder of assemble.py's outputs), clean and noisy,
/// with and without the dictionary hint. Each run is written as &lt;case&gt;/asr/&lt;setting&gt;-&lt;audio&gt;.json in
/// the shape every speech recognizer of the comparison writes (tests/asr/eval/README.md): model,
/// settings, segments (start, end, speaker, text), seconds, peak memory. Scored by score_asr.py.
/// </summary>
public sealed class AsrEvalExport(ITestOutputHelper output)
{
    [Fact]
    public async Task ExportWhisper()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_EVAL") != "1")
        {
            return;
        }

        string root = Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_EVAL_DIR")!;
        string modelName = Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_MODEL") ?? "ggml-small.bin";
        string model = Path.Combine(Services.ModelLocations.UserModelsDirectory, modelName);
        Services.Acceleration.AccelerationState.Initialize(Services.Acceleration.AccelerationMode.Auto);
        // FLOATINGMEMO_ASR_TAG: a suffix for a changed pipeline (e.g. "-tail"); FLOATINGMEMO_ASR_HINT_ONLY=1: only with the hint.
        string tag = Path.GetFileNameWithoutExtension(modelName).Replace("ggml-", "whisper-", StringComparison.Ordinal);
        string suffix = Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_TAG") ?? string.Empty;
        bool hintOnly = Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_HINT_ONLY") == "1";

        foreach (string dir in Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "truth.json"))).Order())
        {
            var truth = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "truth.json")))!;
            // The hint exactly as the app builds it from dictionary entries marked for Whisper (TermDictionary.WhisperHintText).
            var terms = truth["terms"]!.AsArray().Select(t => t!.GetValue<string>()).Take(30).ToList();
            string hint = "用語: " + string.Join("、", terms) + "。";
            Directory.CreateDirectory(Path.Combine(dir, "asr"));
            foreach (string audio in new[] { "clean", "noisy" })
            foreach (bool useHint in hintOnly ? new[] { true } : new[] { true, false })
            {
                string setting = $"{tag}-{(useHint ? "hint" : "nohint")}{suffix}";
                if (File.Exists(Path.Combine(dir, "asr", $"{setting}-{audio}.json")))
                {
                    continue; // already exported: results are never overwritten
                }

                using var service = new LocalWhisperTranscriptionService(model, "ja") { Options = WhisperPipelineOptions.Default };
                if (useHint)
                {
                    service.HintProvider = () => hint;
                }

                long peakRam = 0, peakVram = 0, baseVram = Vram();
                using var sampler = new Timer(_ =>
                {
                    using var p = Process.GetCurrentProcess();
                    peakRam = Math.Max(peakRam, p.WorkingSet64);
                    peakVram = Math.Max(peakVram, Vram());
                }, null, 0, 500);
                await using var stream = File.OpenRead(Path.Combine(dir, audio + ".wav"));
                var watch = Stopwatch.StartNew();
                var result = await service.TranscribeAsync(stream);
                watch.Stop();
                var json = new JsonObject
                {
                    ["model"] = $"Whisper {Path.GetFileNameWithoutExtension(modelName).Replace("ggml-", string.Empty, StringComparison.Ordinal)} (Whisper.net 1.9.1, app pipeline)",
                    ["setting"] = setting,
                    ["settings"] = $"WhisperPipelineOptions.Default (energy VAD, repetition guard){(useHint ? ", dictionary hint" : string.Empty)}",
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
                File.WriteAllText(Path.Combine(dir, "asr", $"{setting}-{audio}.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                output.WriteLine($"{Path.GetFileName(dir)} {setting} {audio}: {watch.Elapsed.TotalSeconds:F1}s");
            }
        }
    }

    // GPU memory in use (MB), from nvidia-smi; 0 when there is none.
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
