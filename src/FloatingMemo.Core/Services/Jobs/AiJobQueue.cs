using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatingMemo.Services.Jobs;

/// <summary>What an AI job does.</summary>
public enum AiJobKind
{
    /// <summary>Speech recognition of one recording session (audio → transcript into its note).</summary>
    Transcribe,

    /// <summary>Minutes of one note with the local LLM (saved as a new note).</summary>
    Minutes,
}

public enum AiJobState
{
    /// <summary>Waiting for its turn (or for enough free memory).</summary>
    Pending,

    Running,

    Completed,

    /// <summary>Stopped with an error or cancelled; its input (audio, note) is kept for a retry.</summary>
    Failed,

    /// <summary>The app ended while it was running; it runs again from the start (or its checkpoint).</summary>
    Interrupted,
}

/// <summary>
/// One unit of AI work. Only identifiers, paths, states and error messages are stored (never
/// note text, transcripts or audio), so the queue file can be kept like the recording manifests.
/// </summary>
public sealed class AiJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public AiJobKind Kind { get; set; }

    public AiJobState State { get; set; } = AiJobState.Pending;

    public DateTime Created { get; set; } = DateTime.Now;

    public DateTime? Finished { get; set; }

    /// <summary>The recording session folder (transcription jobs).</summary>
    public string? SessionDirectory { get; set; }

    /// <summary>The note the result belongs to: the recorded note, or the note minutes are made from.</summary>
    public string? NoteId { get; set; }

    /// <summary>Speech recognizer or LLM the job was started with (e.g. "whisper", "haas-ja").</summary>
    public string? Engine { get; set; }

    /// <summary>How many times it was started (an interrupted run counts: a job that keeps ending the app is not retried forever).</summary>
    public int Attempts { get; set; }

    /// <summary>Why it failed (for the message and diagnostics; no note text).</summary>
    public string? Error { get; set; }

    /// <summary>True when the user cancelled it (the message differs from a failure).</summary>
    public bool Cancelled { get; set; }

    /// <summary>Not stored: why a pending job is not running yet (e.g. waiting for memory).</summary>
    [JsonIgnore]
    public string? Waiting { get; set; }

    public AiJob Clone() => (AiJob)MemberwiseClone();
}

/// <summary>Free physical memory, in MB (Windows: GlobalMemoryStatusEx). Injected so tests can simulate a full PC.</summary>
public interface IMemoryProbe
{
    long AvailableMegabytes { get; }
}

/// <summary>
/// The AI work of the app (speech recognition, minutes) as a persistent, ordered queue that runs
/// one job at a time, apart from recording: the recorder never waits for it and it never waits
/// for the recorder. Jobs run strictly one after another so two models are never in memory at
/// once (Nemotron + Qwen together ended in a native out-of-memory crash), and a job starts only
/// when the PC has the memory it needs; otherwise it waits and says so.
/// </summary>
/// <remarks>
/// The queue is saved after every change (temporary file, then replace). After a restart, a job
/// that was running is <see cref="AiJobState.Interrupted"/> and runs again; one that was
/// interrupted <see cref="MaxAttempts"/> times is failed instead, so a job that crashes the app
/// cannot do so at every start. Running jobs are cancelled only through their token: the
/// engines stop at their next safe point, nothing is killed from here.
/// </remarks>
public sealed class AiJobQueue : IDisposable
{
    public const string FileName = "ai-jobs.json";
    public const int MaxAttempts = 3;

    private readonly string _path;
    private readonly Func<AiJob, IProgress<double>, CancellationToken, Task> _run;
    private readonly IMemoryProbe _memory;
    private readonly Func<AiJob, long> _requiredMegabytes;
    private readonly TimeSpan _memoryRetry;
    private readonly object _lock = new();
    private readonly List<AiJob> _jobs = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _current;
    private Task? _worker;

    /// <param name="directory">Where the queue file is kept (next to the recording sessions).</param>
    /// <param name="run">Runs one job; returns normally when done, throws on failure (OperationCanceledException = cancelled).</param>
    /// <param name="requiredMegabytes">Free memory a job needs before it starts (0 = no check).</param>
    /// <param name="memoryRetry">How often a job waiting for memory checks again.</param>
    public AiJobQueue(string directory, Func<AiJob, IProgress<double>, CancellationToken, Task> run, IMemoryProbe memory,
        Func<AiJob, long> requiredMegabytes, TimeSpan? memoryRetry = null)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, FileName);
        _run = run;
        _memory = memory;
        _requiredMegabytes = requiredMegabytes;
        _memoryRetry = memoryRetry ?? TimeSpan.FromSeconds(5);
        Load();
    }

    /// <summary>Raised (on a worker thread) after any change: state, progress or waiting reason.</summary>
    public event EventHandler? Changed;

    /// <summary>The running job's progress (0–1), or null when unknown.</summary>
    public double? Progress { get; private set; }

    /// <summary>A copy of every job, oldest first.</summary>
    public IReadOnlyList<AiJob> Snapshot()
    {
        lock (_lock)
        {
            return _jobs.Select(j => j.Clone()).ToList();
        }
    }

    public AiJob? Running
    {
        get
        {
            lock (_lock)
            {
                return _jobs.FirstOrDefault(j => j.State == AiJobState.Running)?.Clone();
            }
        }
    }

    /// <summary>Jobs that have not finished (pending, running, interrupted).</summary>
    public int Unfinished
    {
        get
        {
            lock (_lock)
            {
                return _jobs.Count(IsUnfinished);
            }
        }
    }

    /// <summary>Starts working through the queue (also the jobs restored from the file).</summary>
    public void Start() => _worker ??= Task.Run(WorkAsync);

    public AiJob Enqueue(AiJob job)
    {
        lock (_lock)
        {
            job.State = AiJobState.Pending;
            _jobs.Add(job);
            Save();
        }

        Notify();
        return job.Clone();
    }

    /// <summary>
    /// Cancels a job: a pending one is failed (cancelled) at once, a running one is asked to stop
    /// at its next safe point. Its input is kept either way. Returns false if it was not unfinished.
    /// </summary>
    public bool Cancel(string id)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null || !IsUnfinished(job))
            {
                return false;
            }

            if (job.State == AiJobState.Running)
            {
                _current?.Cancel();
                return true;
            }

            Finish(job, AiJobState.Failed, "中止しました", cancelled: true);
        }

        Notify();
        return true;
    }

    /// <summary>Queues a failed job again (the user pressed retry).</summary>
    public bool Retry(string id)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is not { State: AiJobState.Failed })
            {
                return false;
            }

            job.State = AiJobState.Pending;
            job.Error = null;
            job.Cancelled = false;
            job.Finished = null;
            job.Attempts = 0;
            Save();
        }

        Notify();
        return true;
    }

    /// <summary>Removes a finished job from the list (its input was dealt with by the caller).</summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            int removed = _jobs.RemoveAll(j => j.Id == id && !IsUnfinished(j));
            if (removed > 0)
            {
                Save();
            }

            return removed > 0;
        }
    }

    /// <summary>Is any unfinished job using this recording session (so it is not offered as a leftover)?</summary>
    public bool Uses(string sessionDirectory)
    {
        lock (_lock)
        {
            return _jobs.Any(j => j.SessionDirectory is { } d && string.Equals(Path.GetFullPath(d), Path.GetFullPath(sessionDirectory), StringComparison.OrdinalIgnoreCase)
                && j.State != AiJobState.Completed);
        }
    }

    private static bool IsUnfinished(AiJob j) => j.State is AiJobState.Pending or AiJobState.Running or AiJobState.Interrupted;

    private async Task WorkAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            AiJob? job;
            lock (_lock)
            {
                job = _jobs.FirstOrDefault(j => j.State is AiJobState.Pending or AiJobState.Interrupted);
            }

            if (job is null)
            {
                try
                {
                    await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            // Memory first: a model that does not fit would fail (or crash the app) half-way.
            long need = _requiredMegabytes(job);
            long free = _memory.AvailableMegabytes;
            if (need > 0 && free < need)
            {
                SetWaiting(job, $"メモリの空き待ち（空き {free / 1024.0:0.0}GB／必要 {need / 1024.0:0.0}GB）");
                try
                {
                    await Task.WhenAny(_signal.WaitAsync(_stop.Token), Task.Delay(_memoryRetry, _stop.Token)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await RunAsync(job).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(AiJob job)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        AiJob input;
        lock (_lock)
        {
            if (!IsUnfinished(job) || job.State == AiJobState.Running)
            {
                return; // cancelled meanwhile
            }

            if (job.Attempts >= MaxAttempts)
            {
                Finish(job, AiJobState.Failed, "処理中にアプリが繰り返し終了したため、自動では再開しません");
                Notify();
                return;
            }

            job.State = AiJobState.Running;
            job.Attempts++;
            job.Waiting = null;
            _current = cts;
            Progress = null;
            Save();
            input = job.Clone();
        }

        Notify();
        var progress = new SynchronousProgress(p =>
        {
            Progress = p;
            Changed?.Invoke(this, EventArgs.Empty);
        });
        try
        {
            await _run(input, progress, cts.Token).ConfigureAwait(false);
            lock (_lock)
            {
                Finish(job, AiJobState.Completed, null);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The app is closing: the job runs again at the next start.
            lock (_lock)
            {
                job.State = AiJobState.Interrupted;
                job.Attempts = Math.Max(0, job.Attempts - 1); // not the job's fault
                Save();
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                Finish(job, AiJobState.Failed, "中止しました", cancelled: true);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                Finish(job, AiJobState.Failed, ex.Message);
            }
        }
        finally
        {
            lock (_lock)
            {
                _current = null;
                Progress = null;
            }
        }

        Notify();
    }

    private void Finish(AiJob job, AiJobState state, string? error, bool cancelled = false)
    {
        job.State = state;
        job.Error = error;
        job.Cancelled = cancelled;
        job.Waiting = null;
        job.Finished = DateTime.Now;
        // Completed jobs are not kept: their result is in the notes.
        if (state == AiJobState.Completed)
        {
            _jobs.Remove(job);
        }

        Save();
    }

    private void SetWaiting(AiJob job, string reason)
    {
        bool changed;
        lock (_lock)
        {
            changed = job.Waiting != reason;
            job.Waiting = reason;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Notify()
    {
        // Wake the worker (at most one pending signal is needed).
        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize(File.ReadAllText(_path), AiJobJsonContext.Default.ListAiJob) ?? [];
            foreach (var job in stored)
            {
                // Running when the app ended (crash, power loss, closed): run again.
                if (job.State == AiJobState.Running)
                {
                    job.State = AiJobState.Interrupted;
                }

                _jobs.Add(job);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged queue file loses only the queue: the recordings stay and are offered as leftovers.
        }
    }

    private void Save()
    {
        string temp = _path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_jobs, AiJobJsonContext.Default.ListAiJob));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the job still runs; only its restart after a crash is lost.
        }
    }

    /// <summary>Stops the worker. A running job is cancelled and stays Interrupted (it runs again at the next start).</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>Progress&lt;T&gt; posts to a captured context; this reports on the caller's thread.</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<AiJob>))]
internal sealed partial class AiJobJsonContext : JsonSerializerContext
{
}
