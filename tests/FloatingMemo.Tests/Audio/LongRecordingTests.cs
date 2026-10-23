using FloatingMemo.Services.Audio;
using NAudio.Wave;

namespace FloatingMemo.Tests.Audio;

/// <summary>
/// Long recordings without writing gigabytes: segments are made tiny so every boundary, size
/// limit and failure path runs in a second. Synthetic audio only.
/// </summary>
public sealed class LongRecordingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", "long-" + Guid.NewGuid().ToString("N"));

    public LongRecordingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static Segment Tone(double seconds, double hz = 440) => new(SegmentKind.Tone, TimeSpan.FromSeconds(seconds), hz);

    private static Segment Gap(double seconds) => new(SegmentKind.Gap, TimeSpan.FromSeconds(seconds));

    private static SyntheticCaptureStream Stream(AudioSources source, WaveFormat format, params Segment[] timeline) => new(source, format, timeline);

    /// <summary>Writes one track with the given segment size and returns all its audio bytes, in order.</summary>
    private byte[] WriteTrack(string folder, long segmentBytes, out IReadOnlyList<string> segments, params Segment[] timeline)
    {
        Directory.CreateDirectory(folder);
        var stream = Stream(AudioSources.Microphone, Formats.DeviceFloatStereo48k, timeline);
        using var track = new SourceTrackWriter(stream, folder, QpcClock.Now, segmentBytes: segmentBytes);
        track.Start();
        Assert.True(stream.TimelineDelivered.Wait(TimeSpan.FromSeconds(10)));
        track.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        segments = track.Segments;
        var all = new MemoryStream();
        foreach (var file in segments)
        {
            using var reader = new WaveFileReader(file);
            reader.CopyTo(all);
        }

        return all.ToArray();
    }

    [Fact]
    public void Segments_CutAtTheSizeLimit_JoinToExactlyTheSameAudio()
    {
        // 48 kHz stereo float = 384,000 bytes/s; 100,000-byte segments (not a multiple of a frame).
        var timeline = new[] { Tone(1.5), Gap(0.4), Tone(0.6, 660) };
        byte[] whole = WriteTrack(Path.Combine(_dir, "one"), long.MaxValue / 2, out var single, timeline);
        byte[] joined = WriteTrack(Path.Combine(_dir, "many"), 100_000, out var many, timeline);

        Assert.Single(single);
        Assert.True(many.Count >= 9);
        Assert.Equal(whole.Length, joined.Length); // nothing lost or repeated at a boundary (gap filling included)
        Assert.Equal(whole, joined);
        Assert.All(many, f => Assert.Equal(0, DataBytes(f) % 8)); // whole stereo float frames
        Assert.All(many.SkipLast(1), f => Assert.InRange(DataBytes(f), 99_993, 100_000));
    }

    [Fact]
    public async Task SegmentedRecording_IsPreparedWithTheRightLength_AndSegmentsRemovedAfterwards()
    {
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.DeviceFloatStereo48k, Tone(2.0)))
            .With(AudioSources.SystemAudio, () => Stream(AudioSources.SystemAudio, Formats.DeviceFloatStereo48k, Gap(0.5), Tone(1.0, 880)));
        using var recorder = new AudioRecorder(factory, _dir, segmentBytes: 64_000);
        recorder.Start(AudioSources.Microphone | AudioSources.SystemAudio, noteId: "note-1");
        foreach (var s in factory.Created.Cast<SyntheticCaptureStream>())
        {
            Assert.True(s.TimelineDelivered.Wait(TimeSpan.FromSeconds(10)));
        }

        var audio = await recorder.StopAsync();

        Assert.InRange(audio.Duration.TotalSeconds, 1.95, 2.1);
        Assert.Equal(["recording.wav", RecordingManifest.FileName], Directory.GetFiles(audio.SessionDirectory).Select(Path.GetFileName).Order());
        var manifest = RecordingManifest.Load(audio.SessionDirectory)!;
        Assert.Equal(RecordingStates.Prepared, manifest.State);
        Assert.Equal("note-1", manifest.NoteId);
        Assert.True(manifest.Tracks.Single(t => t.Source == "Microphone").Segments.Count > 5);
    }

    [Fact]
    public async Task DiskFull_StopsThatTrack_KeepsWhatWasWritten_AndTheRecordingStillWorks()
    {
        var failures = new List<AudioCaptureException>();
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.DeviceFloatStereo48k, Tone(3.0)));
        // The disk "fills up" 500,000 bytes into the recording (about 1.3 s).
        long budget = 500_000;
        using var recorder = new AudioRecorder(factory, _dir, segmentBytes: 200_000,
            openFile: path => new LimitedStream(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite), () => budget, n => budget -= n));
        recorder.SourceFailed += (_, e) => failures.Add(e);
        recorder.Start(AudioSources.Microphone);
        Assert.True(((SyntheticCaptureStream)factory.Created[0]).TimelineDelivered.Wait(TimeSpan.FromSeconds(10)));

        budget = long.MaxValue; // space is back (the user deleted something) before stopping
        var audio = await recorder.StopAsync();

        Assert.Single(failures);
        Assert.Contains("ディスク", failures[0].InnerException!.Message);
        Assert.InRange(audio.Duration.TotalSeconds, 0.9, 1.4); // complete segments + the part before the failure
    }

    [Fact]
    public async Task ProcessingFailure_DeletesNothing_AndTheRecordingCanBeRecovered()
    {
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.DeviceFloatStereo48k, Tone(1.0)));
        using var recorder = new AudioRecorder(factory, _dir, segmentBytes: 100_000);
        recorder.Start(AudioSources.Microphone, noteId: "note-7");
        Assert.True(((SyntheticCaptureStream)factory.Created[0]).TimelineDelivered.Wait(TimeSpan.FromSeconds(10)));
        string session = Directory.GetDirectories(_dir).Single();
        recorder.BeforePrepare = () => throw new IOException("ディスクの空き容量が不足しています。"); // making recording.wav fails

        var error = await Assert.ThrowsAsync<RecordingSaveException>(recorder.StopAsync);

        Assert.Equal(session, error.SessionDirectory);
        Assert.True(Directory.GetFiles(session, "Microphone-*.wav").Length >= 3); // nothing deleted
        Assert.Equal(RecordingStates.Failed, RecordingManifest.Load(session)!.State);
        var leftover = Assert.Single(recorder.FindLeftovers());
        Assert.Equal(RecordingStates.Failed, leftover.State);
        Assert.InRange(leftover.EstimatedDuration.TotalSeconds, 0.9, 1.1);

        var recovered = await AudioRecorder.RecoverAsync(session); // space is back: made from the kept segments
        Assert.InRange(recovered.Duration.TotalSeconds, 0.95, 1.05);
        Assert.Equal("note-7", RecordingManifest.Load(session)!.NoteId);
        Assert.True(Directory.GetFiles(session, "Microphone-*.wav").Length >= 3); // still kept until transcribed
    }

    [Fact]
    public async Task Crash_WhileRecording_LeavesAudioThatIsFoundAndRecovered()
    {
        // The writer's files are opened so a "crash snapshot" can be copied while they are open.
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.DeviceFloatStereo48k, Tone(1.5)));
        string work = Path.Combine(_dir, "work");
        using var recorder = new AudioRecorder(factory, work, segmentBytes: 250_000,
            openFile: path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), flushInterval: TimeSpan.FromHours(1));
        recorder.Start(AudioSources.Microphone);
        Assert.True(((SyntheticCaptureStream)factory.Created[0]).TimelineDelivered.Wait(TimeSpan.FromSeconds(10)));

        // What the disk holds at the moment of a crash: the open segment's header was never updated.
        string session = Directory.GetDirectories(work).Single();
        string crashed = Path.Combine(_dir, "after-crash", Path.GetFileName(session));
        Directory.CreateDirectory(crashed);
        foreach (var file in Directory.GetFiles(session))
        {
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var target = File.Create(Path.Combine(crashed, Path.GetFileName(file)));
            source.CopyTo(target);
        }

        recorder.Cancel();

        var leftovers = new AudioRecorder(new SyntheticCaptureFactory(), Path.GetDirectoryName(crashed)!).FindLeftovers();
        Assert.Equal(RecordingStates.Recording, Assert.Single(leftovers).State); // "録音中に終了"
        var recovered = await AudioRecorder.RecoverAsync(crashed);
        Assert.InRange(recovered.Duration.TotalSeconds, 1.45, 1.55); // the open segment too, thanks to the header repair
    }

    [Fact]
    public void Leftovers_AreListed_NotDeleted_AndDeletedOnlyWhenAsked()
    {
        string old = Path.Combine(_dir, "rec-old");
        Directory.CreateDirectory(old);
        File.WriteAllBytes(Path.Combine(old, "Microphone.wav"), new byte[1000]);
        Directory.CreateDirectory(Path.Combine(_dir, "rec-empty")); // nothing captured: not offered
        using var recorder = new AudioRecorder(new SyntheticCaptureFactory(), _dir);

        var leftovers = recorder.FindLeftovers();

        Assert.Equal(old, Assert.Single(leftovers).Directory);
        Assert.True(Directory.Exists(old));
        Assert.Equal(0, recorder.DeleteLeftovers([new LeftoverRecording(Path.GetTempPath(), DateTime.Now, "x", TimeSpan.Zero, 0)])); // never outside the folder
        Assert.Equal(1, recorder.DeleteLeftovers(leftovers));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void WavRepair_ExtendsAHeaderLeftBehindByACrash()
    {
        string path = Path.Combine(_dir, "cut.wav");
        var format = Formats.DeviceFloatStereo48k;
        using (var writer = new WaveFileWriter(path, format))
        {
            writer.Write(new byte[8 * 48_000], 0, 8 * 48_000); // 1 s: the header says 1 s
        }

        using (var file = new FileStream(path, FileMode.Append))
        {
            file.Write(new byte[8 * 24_000 + 3]); // +0.5 s and a torn frame written after the last header update
        }

        Assert.True(WavRepair.ExtendToFileLength(path));
        using (var reader = new WaveFileReader(path))
        {
            Assert.Equal(1.5, reader.TotalTime.TotalSeconds, 3);
        }

        Assert.False(WavRepair.ExtendToFileLength(path)); // already right
    }

    private static long DataBytes(string wav)
    {
        using var reader = new WaveFileReader(wav);
        return reader.Length;
    }

    /// <summary>A file that refuses writes once the "disk" has no space left.</summary>
    private sealed class LimitedStream(Stream inner, Func<long> space, Action<long> used) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            // Rewriting the header (within the file) needs no new space.
            long growth = Math.Max(0, inner.Position + buffer.Length - inner.Length);
            if (growth > space())
            {
                throw new IOException("ディスクの空き容量が不足しています。");
            }

            inner.Write(buffer);
            used(growth);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
