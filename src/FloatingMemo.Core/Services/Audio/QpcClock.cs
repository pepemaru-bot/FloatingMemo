using System.Diagnostics;

namespace FloatingMemo.Services.Audio;

/// <summary>
/// The QueryPerformanceCounter clock in 100 ns units — the same clock WASAPI uses to
/// timestamp captured packets, so packets from different devices can be aligned.
/// </summary>
public static class QpcClock
{
    public static long Now
    {
        get
        {
            long ticks = Stopwatch.GetTimestamp();
            long frequency = Stopwatch.Frequency;
            // Split to avoid overflowing ticks * 10^7.
            return (ticks / frequency * 10_000_000) + (ticks % frequency * 10_000_000 / frequency);
        }
    }
}
