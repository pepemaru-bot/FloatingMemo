using NAudio.Wave;

namespace FloatingMemo.Services.Audio;

/// <summary>
/// Development/testing stand-in for real devices: every source "captures" the contents of
/// a WAV file instead of a microphone or loopback. Lets automated tests exercise the full
/// record → transcribe path without touching real audio hardware.
/// </summary>
public sealed class WavFileCaptureFactory : IAudioCaptureFactory
{
    private readonly string _wavPath;
    private readonly bool _realTime;

    /// <param name="realTime">
    /// false: the whole file is delivered at once (fast tests). true: delivered at playback
    /// speed and repeated until stopped, like a live device (for the level indicators).
    /// </param>
    public WavFileCaptureFactory(string wavPath, bool realTime = false)
    {
        _wavPath = wavPath;
        _realTime = realTime;
    }

    public IAudioCaptureStream Create(AudioSources source) => new WavFileCaptureStream(source, _wavPath, _realTime);

    private sealed class WavFileCaptureStream : IAudioCaptureStream
    {
        private readonly string _path;
        private readonly bool _realTime;
        private readonly ManualResetEventSlim _stopRequested = new();
        private readonly object _gate = new();
        private bool _finished;

        public WavFileCaptureStream(AudioSources source, string path, bool realTime)
        {
            Source = source;
            _path = path;
            _realTime = realTime;
            using var reader = new WaveFileReader(path);
            WaveFormat = reader.WaveFormat;
        }

        public AudioSources Source { get; }

        public WaveFormat WaveFormat { get; }

        public event AudioPacketHandler? PacketAvailable;

        public event EventHandler<Exception?>? Stopped;

        public void Start()
        {
            long startQpc = QpcClock.Now;
            new Thread(() => Run(startQpc)) { IsBackground = true }.Start();
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_finished)
                {
                    _stopRequested.Set();
                }
            }
        }

        private void Run(long startQpc)
        {
            try
            {
                long frames = 0;
                do
                {
                    using var reader = new WaveFileReader(_path);
                    var buffer = new byte[WaveFormat.AverageBytesPerSecond / 10 / WaveFormat.BlockAlign * WaveFormat.BlockAlign];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        // Timestamps as if the file had been captured in real time.
                        long qpc = startQpc + (frames * 10_000_000 / WaveFormat.SampleRate);
                        if (_realTime && _stopRequested.Wait(TimeSpan.FromTicks(Math.Max(0, qpc - QpcClock.Now))))
                        {
                            break;
                        }

                        PacketAvailable?.Invoke(buffer.AsSpan(0, read), false, qpc);
                        frames += read / WaveFormat.BlockAlign;
                    }
                }
                while (_realTime && !_stopRequested.IsSet);

                _stopRequested.Wait();
                Stopped?.Invoke(this, null);
            }
            catch (Exception ex)
            {
                Stopped?.Invoke(this, ex);
            }
            finally
            {
                // Release the wait handle (one per recording would otherwise accumulate).
                lock (_gate)
                {
                    _finished = true;
                    _stopRequested.Dispose();
                }
            }
        }

        public void Dispose() => Stop();
    }
}
