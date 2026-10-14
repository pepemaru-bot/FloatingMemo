using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FloatingMemo.Services.Audio;

/// <summary>
/// Opens the selected Windows recording device (microphone) or the default playback device
/// in loopback mode (PC audio) with NAudio's WasapiRecorder. Capture stays in the device's
/// native format; conversion for transcription happens after recording stops.
/// </summary>
public sealed class WasapiCaptureFactory(WasapiDeviceCatalog catalog, Func<AudioDeviceSelection> microphone) : IAudioCaptureFactory
{
    public IAudioCaptureStream Create(AudioSources source)
    {
        MMDevice device = source switch
        {
            AudioSources.Microphone => OpenMicrophone(),
            // Loopback records the default playback device (Role.Console = the device Windows uses
            // for general playback). Choosing another output device would resolve here the same way.
            AudioSources.SystemAudio => OpenDefaultRender(),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

        try
        {
            var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode();
            if (source == AudioSources.SystemAudio)
            {
                // Loopback = record what this render device is playing (the whole output mix).
                // This is not "Stereo Mix": that is a separate (driver-provided) recording device.
                builder = builder.WithLoopbackCapture();
            }

            return new WasapiCaptureStream(source, builder.Build(), device);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private MMDevice OpenMicrophone()
    {
        // Resolved at every start: "Windows default" follows changes made in Windows settings,
        // while a specific device that is missing stops here instead of falling back silently.
        var resolved = AudioDeviceResolver.Resolve(catalog, microphone());
        if (!resolved.IsUsable)
        {
            throw new AudioDeviceUnavailableException(resolved.Problem ?? "マイクを使用できません。");
        }

        return catalog.Open(resolved.Device!.Id);
    }

    private static MMDevice OpenDefaultRender()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    }

    private sealed class WasapiCaptureStream : IAudioCaptureStream
    {
        private readonly WasapiRecorder _recorder;
        private readonly MMDevice _device;

        public WasapiCaptureStream(AudioSources source, WasapiRecorder recorder, MMDevice device)
        {
            Source = source;
            _recorder = recorder;
            _device = device;
            _recorder.DataAvailable += OnDataAvailable;
            _recorder.RecordingStopped += (_, e) => Stopped?.Invoke(this, e.Exception);
        }

        public AudioSources Source { get; }

        public WaveFormat WaveFormat => _recorder.WaveFormat;

        public event AudioPacketHandler? PacketAvailable;

        public event EventHandler<Exception?>? Stopped;

        public void Start() => _recorder.StartRecording();

        public void Stop() => _recorder.StopRecording();

        private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            bool silent = flags.HasFlag(AudioClientBufferFlags.Silent);
            long qpc = flags.HasFlag(AudioClientBufferFlags.TimestampError) ? -1 : qpcPosition;
            PacketAvailable?.Invoke(buffer, silent, qpc);
        }

        public void Dispose()
        {
            _recorder.DataAvailable -= OnDataAvailable;
            _recorder.Dispose();
            _device.Dispose();
        }
    }
}
