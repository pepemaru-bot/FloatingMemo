using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using FloatingMemo.Services.Acceleration;

namespace FloatingMemo.Services.Minutes;

/// <summary>
/// Runs a GGUF model (e.g. Qwen3) in this process with llama.cpp via LLamaSharp. Everything
/// stays on this PC: no server, no network. The model is loaded on first use (it takes a few
/// seconds and ~2–3 GB of memory) and released with <see cref="Dispose"/>.
/// </summary>
public sealed class LlamaTextGenerator : ILocalTextGenerator, IDisposable
{
    private readonly string _modelPath;
    private readonly ModelParams _parameters;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;

    public LlamaTextGenerator(string modelPath, uint contextSize = 8192, int? threads = null)
    {
        _modelPath = modelPath;
        _parameters = new ModelParams(modelPath)
        {
            ContextSize = contextSize,
            GpuLayerCount = 0, // decided when the model is loaded (see LoadAsync)
            Threads = threads ?? Math.Max(1, Environment.ProcessorCount / 2),
        };
    }

    public string ModelName => Path.GetFileNameWithoutExtension(_modelPath);

    public string LastFinish { get; private set; } = MinutesFinish.Unknown;

    /// <summary>Where the user is told to put Qwen models (see <see cref="ModelLocations"/>).</summary>
    public static string ModelsDirectory => ModelLocations.RecommendedDirectory(ModelLocations.QwenFolder);

    /// <summary>Preferred models, best first (the user downloads them; nothing is fetched here).</summary>
    public static readonly string[] KnownModels = ["Qwen3-4B-Q4_K_M.gguf", "Qwen3-1.7B-Q8_0.gguf"];

    /// <summary>A GGUF model found on this PC, with what is known about it.</summary>
    public sealed record ModelInfo(string Path, string FileName, long Bytes, string Label, string Notes, bool Bundled);

    /// <summary>What we measured or know about each candidate (Core i7-10700, CPU only).</summary>
    private static readonly Dictionary<string, (string Label, string Notes)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Qwen3-1.7B-Q8_0.gguf"] = ("Qwen3 1.7B（軽量）", "必要メモリ 約2.5GB・短い会議で約40〜50秒。内容の取り違えが多め"),
        ["Qwen3-4B-Q4_K_M.gguf"] = ("Qwen3 4B（標準）", "必要メモリ 約3.5GB・短い会議で約60秒。原文に忠実"),
        ["Qwen3-8B-Q4_K_M.gguf"] = ("Qwen3 8B（高性能）", "未計測。目安: 必要メモリ 約6GB・4B の約2倍の時間。自宅PC向け"),
    };

    /// <summary>All GGUF models in the folders of <see cref="ModelLocations"/>; Bundled = inside the app folder.</summary>
    public static IReadOnlyList<ModelInfo> AvailableModels()
    {
        var list = new List<ModelInfo>();
        foreach (var (dir, bundled) in ModelLocations.SearchDirectories(ModelLocations.QwenFolder))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(dir, "*.gguf"))
            {
                string name = Path.GetFileName(file);
                if (list.Any(m => string.Equals(m.FileName, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue; // the copy found first wins
                }

                var (label, notes) = Known.TryGetValue(name, out var k) ? k : (Path.GetFileNameWithoutExtension(name), "未評価のモデル");
                list.Add(new ModelInfo(file, name, new FileInfo(file).Length, label, notes, bundled));
            }
        }

        return list.OrderBy(m => m.Bytes).ToList();
    }

    /// <summary>The chosen model if it is present, otherwise the best known one available.</summary>
    public static string? FindModel(string? preferredFileName)
    {
        var models = AvailableModels();
        return models.FirstOrDefault(m => string.Equals(m.FileName, preferredFileName, StringComparison.OrdinalIgnoreCase))?.Path ?? FindModel();
    }

    /// <summary>The first known model present (app folder first, then the user's models folder).</summary>
    public static string? FindModel()
    {
        foreach (string name in KnownModels)
        {
            foreach (var (dir, _) in ModelLocations.SearchDirectories(ModelLocations.QwenFolder))
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// All layers on the GPU when the policy says the model fits, otherwise none. If loading on
    /// the GPU fails (driver, memory), the model is loaded again for the CPU.
    /// </summary>
    private async Task<LLamaWeights> LoadAsync(CancellationToken cancellationToken)
    {
        long bytes = File.Exists(_modelPath) ? new FileInfo(_modelPath).Length : 0;
        var choice = AccelerationPolicy.ForLlm(AccelerationState.Mode, AccelerationState.Adapters, bytes);
        _parameters.GpuLayerCount = choice.UseGpu ? 999 : 0;
        try
        {
            var weights = await Task.Run(() => LLamaWeights.LoadFromFile(_parameters), cancellationToken).ConfigureAwait(false);
            AccelerationState.Record(choice with { Backend = choice.UseGpu ? "GPU / Vulkan（全層）" : "CPU" });
            return weights;
        }
        catch (Exception ex) when (choice.UseGpu && ex is not OperationCanceledException)
        {
            _parameters.GpuLayerCount = 0;
            var weights = await Task.Run(() => LLamaWeights.LoadFromFile(_parameters), cancellationToken).ConfigureAwait(false);
            AccelerationState.Record(choice with { UseGpu = false, Backend = "CPU", Reason = "GPU で読み込めず CPU に切り替え" });
            return weights;
        }
    }

    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_weights is null)
            {
                progress?.Report(-1); // loading the model file (a few seconds)
                _weights = await LoadAsync(cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(0);
            var executor = new StatelessExecutor(_weights, _parameters);
            var inference = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>"],
                // Low temperature: stay close to the transcript instead of being "creative".
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f, TopP = 0.9f, RepeatPenalty = 1.05f, Seed = 42 },
            };

            var answer = new StringBuilder();
            int tokens = 0;
            await foreach (string piece in executor.InferAsync(ChatPrompt(system, user), inference, cancellationToken).ConfigureAwait(false))
            {
                tokens++; // one piece per generated token
                answer.Append(piece);
                progress?.Report(answer.Length);
            }

            // Reaching the token budget means the answer was cut off, not finished.
            LastFinish = tokens >= maxTokens ? MinutesFinish.MaxTokens : MinutesFinish.End;
            return answer.ToString().Replace("<|im_end|>", string.Empty).Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Qwen's ChatML format. The empty think block is what Qwen3's own template inserts when
    /// "thinking" is switched off: the model answers directly (faster, no reasoning text).
    /// </summary>
    internal static string ChatPrompt(string system, string user) =>
        $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";

    public void Dispose()
    {
        _weights?.Dispose();
        _weights = null;
        _gate.Dispose();
    }
}
