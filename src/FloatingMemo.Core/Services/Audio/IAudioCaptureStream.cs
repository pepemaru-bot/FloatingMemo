using NAudio.Wave;

namespace FloatingMemo.Services.Audio;

/// <summary>
/// Receives one captured packet. <paramref name="data"/> is only valid during the call.
/// </summary>
/// <param name="data">Audio bytes in the stream's <see cref="IAudioCaptureStream.WaveFormat"/>.</param>
/// <param name="silent">The device marked this packet as silence; its contents must be treated as zeros.</param>
/// <param name="qpcPosition">Capture time in 100 ns units (QueryPerformanceCounter clock), or -1 if unknown.</param>
public delegate void AudioPacketHandler(ReadOnlySpan<byte> data, bool silent, long qpcPosition);

/// <summary>
/// One capture input (microphone or PC audio). Abstracted so tests can feed synthetic audio
/// instead of opening real devices.
/// </summary>
public interface IAudioCaptureStream : IDisposable
{
    AudioSources Source { get; }

    WaveFormat WaveFormat { get; }

    /// <summary>Raised on the capture thread.</summary>
    event AudioPacketHandler? PacketAvailable;

    /// <summary>Raised once capture has fully stopped; the exception is non-null if it stopped because of an error.</summary>
    event EventHandler<Exception?>? Stopped;

    void Start();

    /// <summary>Requests a stop; <see cref="Stopped"/> fires when the last packet has been delivered.</summary>
    void Stop();
}

/// <summary>Creates capture streams. The real implementation opens WASAPI devices.</summary>
public interface IAudioCaptureFactory
{
    IAudioCaptureStream Create(AudioSources source);
}
