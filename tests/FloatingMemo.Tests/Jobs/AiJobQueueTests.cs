using FloatingMemo.Services.Jobs;

namespace FloatingMemo.Tests.Jobs;

public sealed class AiJobQueueTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class FakeMemory(long free) : IMemoryProbe
    {
        public long Free { get; set; } = free;

        public long AvailableMegabytes => Free;
    }

    private static AiJob Transcribe(string session, string note) => new() { Kind = AiJobKind.Transcribe, SessionDirectory = session, NoteId = note, Engine = "whisper" };

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Jobs_RunOneAtATime_InTheOrderTheyWereQueued()
    {
        var order = new List<string>();
        int running = 0, most = 0;
        using var queue = new AiJobQueue(_dir, async (job, _, ct) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await Task.Delay(50, ct);
            lock (order)
            {
                order.Add(job.NoteId!);
            }

            Interlocked.Decrement(ref running);
        }, new FakeMemory(100_000), _ => 1000);
        queue.Start();
        queue.Enqueue(Transcribe("a", "A"));
        queue.Enqueue(new AiJob { Kind = AiJobKind.Minutes, NoteId = "A-minutes" });
        queue.Enqueue(Transcribe("b", "B"));

        await Until(() => order.Count == 3);
        Assert.Equal(["A", "A-minutes", "B"], order);
        Assert.Equal(1, most); // never two models at once
        await Until(() => queue.Unfinished == 0);
        Assert.Empty(queue.Snapshot()); // completed jobs are not kept
    }

    [Fact]
    public async Task EachJob_GetsItsOwnNoteAndSession()
    {
        var seen = new List<(string?, string?)>();
        using var queue = new AiJobQueue(_dir, (job, _, _) =>
        {
            lock (seen)
            {
                seen.Add((job.SessionDirectory, job.NoteId));
            }

            return Task.CompletedTask;
        }, new FakeMemory(100_000), _ => 0);
        queue.Start();
        for (int i = 0; i < 5; i++)
        {
            queue.Enqueue(Transcribe($"rec-{i}", $"note-{i}"));
        }

        await Until(() => seen.Count == 5);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => ((string?)$"rec-{i}", (string?)$"note-{i}")), seen);
    }

    [Fact]
    public async Task AFailedJob_IsKeptWithItsError_AndTheNextOneStillRuns()
    {
        var done = new List<string>();
        using var queue = new AiJobQueue(_dir, (job, _, _) =>
        {
            if (job.NoteId == "bad")
            {
                throw new InvalidOperationException("model missing");
            }

            lock (done)
            {
                done.Add(job.NoteId!);
            }

            return Task.CompletedTask;
        }, new FakeMemory(100_000), _ => 0);
        queue.Start();
        var bad = queue.Enqueue(Transcribe("x", "bad"));
        queue.Enqueue(Transcribe("y", "good"));

        await Until(() => done.Count == 1);
        await Until(() => queue.Snapshot().Any(j => j.State == AiJobState.Failed));
        var failed = queue.Snapshot().Single();
        Assert.Equal(bad.Id, failed.Id);
        Assert.Equal("model missing", failed.Error);
        Assert.True(queue.Uses("x")); // its recording is not a "leftover" to delete
    }

    [Fact]
    public async Task Retry_RunsAFailedJobAgain()
    {
        int calls = 0;
        using var queue = new AiJobQueue(_dir, (_, _, _) => ++calls == 1 ? throw new IOException("busy") : Task.CompletedTask,
            new FakeMemory(100_000), _ => 0);
        queue.Start();
        var job = queue.Enqueue(Transcribe("x", "n"));
        await Until(() => queue.Snapshot().Any(j => j.State == AiJobState.Failed));

        Assert.True(queue.Retry(job.Id));
        await Until(() => queue.Snapshot().Count == 0);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancel_StopsTheRunningJob_ThroughItsToken()
    {
        var started = new TaskCompletionSource();
        using var queue = new AiJobQueue(_dir, async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, new FakeMemory(100_000), _ => 0);
        queue.Start();
        var job = queue.Enqueue(Transcribe("x", "n"));
        await started.Task.WaitAsync(Wait);

        Assert.True(queue.Cancel(job.Id));
        await Until(() => queue.Snapshot().Any(j => j is { State: AiJobState.Failed, Cancelled: true }));
    }

    [Fact]
    public async Task Cancel_OfAPendingJob_RemovesItFromTheTurn()
    {
        var release = new TaskCompletionSource();
        var ran = new List<string>();
        using var queue = new AiJobQueue(_dir, async (job, _, _) =>
        {
            lock (ran)
            {
                ran.Add(job.NoteId!);
            }

            await release.Task;
        }, new FakeMemory(100_000), _ => 0);
        queue.Start();
        queue.Enqueue(Transcribe("a", "first"));
        var second = queue.Enqueue(Transcribe("b", "second"));
        await Until(() => ran.Count == 1);

        Assert.True(queue.Cancel(second.Id));
        release.SetResult();
        await Until(() => queue.Unfinished == 0);
        Assert.Equal(["first"], ran);
    }

    [Fact]
    public async Task AJob_WaitsForMemory_AndStartsWhenThereIsEnough()
    {
        var memory = new FakeMemory(1000);
        var ran = new TaskCompletionSource();
        using var queue = new AiJobQueue(_dir, (_, _, _) =>
        {
            ran.TrySetResult();
            return Task.CompletedTask;
        }, memory, _ => 3000, memoryRetry: TimeSpan.FromMilliseconds(30));
        queue.Start();
        queue.Enqueue(new AiJob { Kind = AiJobKind.Minutes, NoteId = "n" });

        await Until(() => queue.Snapshot().FirstOrDefault()?.Waiting?.Contains("メモリ") == true);
        Assert.False(ran.Task.IsCompleted);

        memory.Free = 8000;
        await ran.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task AfterARestart_ARunningJobIsInterrupted_AndRunsAgain()
    {
        var started = new TaskCompletionSource();
        var first = new AiJobQueue(_dir, async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, new FakeMemory(100_000), _ => 0);
        first.Start();
        first.Enqueue(Transcribe("rec-1", "note-1"));
        await started.Task.WaitAsync(Wait);
        first.Dispose(); // the app closes while it runs

        var restored = new List<AiJob>();
        using var second = new AiJobQueue(_dir, (job, _, _) =>
        {
            lock (restored)
            {
                restored.Add(job);
            }

            return Task.CompletedTask;
        }, new FakeMemory(100_000), _ => 0);
        Assert.Equal(AiJobState.Interrupted, second.Snapshot().Single().State);
        second.Start();
        await Until(() => restored.Count == 1);
        Assert.Equal(("rec-1", "note-1"), (restored[0].SessionDirectory, restored[0].NoteId));
    }

    [Fact]
    public async Task AJobThatKeepsEndingTheApp_IsFailedInsteadOfRunningAtEveryStart()
    {
        // A crash leaves the job "running" in the file with its attempts counted.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, AiJobQueue.FileName),
            $$"""[{"Id":"j1","Kind":"Transcribe","State":"Running","Attempts":{{AiJobQueue.MaxAttempts}},"SessionDirectory":"rec-1","NoteId":"n"}]""");
        int calls = 0;
        using var queue = new AiJobQueue(_dir, (_, _, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, new FakeMemory(100_000), _ => 0);
        queue.Start();
        await Until(() => queue.Snapshot().Single().State == AiJobState.Failed);
        Assert.Equal(0, calls);
        Assert.True(queue.Uses("rec-1")); // the recording is still there for the user
    }

    [Fact]
    public void ADamagedQueueFile_LosesOnlyTheQueue()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, AiJobQueue.FileName), "{ not json");
        using var queue = new AiJobQueue(_dir, (_, _, _) => Task.CompletedTask, new FakeMemory(100_000), _ => 0);
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public void TheQueueFile_HoldsNoText_OnlyIdsPathsAndStates()
    {
        using var queue = new AiJobQueue(_dir, (_, _, _) => Task.CompletedTask, new FakeMemory(100_000), _ => 0);
        queue.Enqueue(Transcribe("rec-1", "note-1"));
        string json = File.ReadAllText(Path.Combine(_dir, AiJobQueue.FileName));
        Assert.Contains("\"Transcribe\"", json);
        Assert.DoesNotContain("Text", json);
    }
}
