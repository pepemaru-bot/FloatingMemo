using System.Runtime.InteropServices;

namespace FloatingMemo.Services.Jobs;

/// <summary>The PC's physical memory (GlobalMemoryStatusEx): what AI jobs check before they load a model.</summary>
public sealed class SystemMemory : IMemoryProbe
{
    public static SystemMemory Instance { get; } = new();

    public long AvailableMegabytes => Read()?.ullAvailPhys is { } bytes ? (long)(bytes / 1_048_576) : long.MaxValue;

    public static long TotalMegabytes => Read()?.ullTotalPhys is { } bytes ? (long)(bytes / 1_048_576) : 0;

    private static MemoryStatusEx? Read()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        // On failure the check is skipped (long.MaxValue): a job is never blocked by a broken probe.
        return GlobalMemoryStatusEx(ref status) ? status : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
