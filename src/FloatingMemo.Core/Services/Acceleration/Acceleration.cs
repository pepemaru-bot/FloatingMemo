using System.Runtime.InteropServices;
using System.Text;

namespace FloatingMemo.Services.Acceleration;

/// <summary>How speech recognition and AI minutes use the graphics card.</summary>
public enum AccelerationMode
{
    /// <summary>The GPU when it is a dedicated card with enough memory for the job, otherwise the CPU.</summary>
    Auto,

    /// <summary>Always try the GPU first (also integrated graphics); the CPU if it fails.</summary>
    PreferGpu,

    /// <summary>Never use the GPU.</summary>
    CpuOnly,
}

/// <summary>A graphics adapter as Windows reports it (name and memory; nothing is sent anywhere).</summary>
public sealed record GpuAdapter(string Name, uint VendorId, long DedicatedMemory, long SharedMemory, bool Software)
{
    public string Vendor => VendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        _ => $"0x{VendorId:X4}",
    };

    /// <summary>A card with its own memory (integrated graphics share the system memory).</summary>
    public bool Dedicated => !Software && DedicatedMemory >= 1L << 30;
}

/// <summary>What was decided for one engine and why (shown in the settings window).</summary>
public sealed record EngineChoice(string Engine, bool UseGpu, string Backend, string Reason);

/// <summary>
/// Chooses CPU or GPU for Whisper (whisper.cpp) and Qwen (llama.cpp). Both use the Vulkan
/// backend for the GPU, which works with NVIDIA, AMD and Intel drivers; the CPU backend is
/// always there as the fallback. The native libraries are chosen once per process, so a change
/// of mode applies after a restart.
/// </summary>
public static class AccelerationPolicy
{
    /// <summary>Memory kept free on the card besides the model (context, buffers, other apps).</summary>
    public const long Headroom = 1L << 30;

    /// <summary>Whisper base needs little memory; a dedicated card of this size is enough.</summary>
    public const long WhisperMinimum = 1L << 30;

    public static EngineChoice ForWhisper(AccelerationMode mode, IReadOnlyList<GpuAdapter> adapters)
    {
        if (mode == AccelerationMode.CpuOnly)
        {
            return new("Whisper", false, "CPU", "設定で「CPU のみ」");
        }

        var best = Best(adapters);
        if (best is null)
        {
            return new("Whisper", false, "CPU", "GPU が見つからない");
        }

        if (mode == AccelerationMode.PreferGpu || (best.Dedicated && best.DedicatedMemory >= WhisperMinimum))
        {
            return new("Whisper", true, "Vulkan", $"{best.Name} を使用");
        }

        return new("Whisper", false, "CPU", "専用メモリのある GPU がない（自動）");
    }

    /// <summary>All layers on the GPU when the model fits in its memory, otherwise none (no partial guess).</summary>
    public static EngineChoice ForLlm(AccelerationMode mode, IReadOnlyList<GpuAdapter> adapters, long modelBytes)
    {
        if (mode == AccelerationMode.CpuOnly)
        {
            return new("Qwen", false, "CPU", "設定で「CPU のみ」");
        }

        var best = Best(adapters);
        if (best is null)
        {
            return new("Qwen", false, "CPU", "GPU が見つからない");
        }

        long memory = best.Dedicated ? best.DedicatedMemory : best.SharedMemory;
        if (memory >= modelBytes + Headroom && (best.Dedicated || mode == AccelerationMode.PreferGpu))
        {
            return new("Qwen", true, "Vulkan", $"{best.Name} を使用");
        }

        return new("Qwen", false, "CPU", best.Dedicated
            ? $"GPU のメモリ（{Gb(best.DedicatedMemory)}）がモデル（{Gb(modelBytes)}）＋余裕に足りない"
            : "専用メモリのある GPU がない（自動）");
    }

    /// <summary>The adapter with the most dedicated memory (software renderers never).</summary>
    public static GpuAdapter? Best(IReadOnlyList<GpuAdapter> adapters) =>
        adapters.Where(a => !a.Software).OrderByDescending(a => a.DedicatedMemory).ThenByDescending(a => a.SharedMemory).FirstOrDefault();

    public static string Gb(long bytes) => $"{bytes / (double)(1L << 30):0.0} GB";

    public static string Label(AccelerationMode mode) => mode switch
    {
        AccelerationMode.PreferGpu => "GPU 優先",
        AccelerationMode.CpuOnly => "CPU のみ",
        _ => "自動（推奨）",
    };
}

/// <summary>
/// The process-wide state: the mode chosen at start-up, the adapters found, and what each engine
/// actually ended up using (including a fallback to the CPU and its reason).
/// </summary>
public static class AccelerationState
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, EngineChoice> Actual = [];

    public static AccelerationMode Mode { get; private set; } = AccelerationMode.Auto;

    public static IReadOnlyList<GpuAdapter> Adapters { get; private set; } = [];

    /// <summary>Called once at start-up, before Whisper or llama.cpp load any native library.</summary>
    public static void Initialize(AccelerationMode mode, IReadOnlyList<GpuAdapter>? adapters = null)
    {
        Mode = mode;
        Adapters = adapters ?? GpuAdapters.Find();

        // The GPU builds of whisper.cpp / llama.cpp are loaded only if a GPU may be used at all;
        // the CPU builds stay the fallback (missing driver, failed start).
        bool gpu = mode != AccelerationMode.CpuOnly && AccelerationPolicy.Best(Adapters) is { } best
            && (best.Dedicated || mode == AccelerationMode.PreferGpu);
        Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = gpu
            ? [Whisper.net.LibraryLoader.RuntimeLibrary.Vulkan, Whisper.net.LibraryLoader.RuntimeLibrary.Cpu, Whisper.net.LibraryLoader.RuntimeLibrary.CpuNoAvx]
            : [Whisper.net.LibraryLoader.RuntimeLibrary.Cpu, Whisper.net.LibraryLoader.RuntimeLibrary.CpuNoAvx];
        LLama.Native.NativeLibraryConfig.All.WithVulkan(gpu).WithAutoFallback(true);
    }

    public static void Record(EngineChoice choice)
    {
        lock (Gate)
        {
            Actual[choice.Engine] = choice;
        }
    }

    /// <summary>A plain-text report for the settings window (this PC only; never logged or sent).</summary>
    public static string Report()
    {
        var sb = new StringBuilder();
        sb.Append("処理方式: ").Append(AccelerationPolicy.Label(Mode)).Append('\n');
        if (Adapters.Count == 0)
        {
            sb.Append("GPU: 見つかりません\n");
        }

        foreach (var a in Adapters.Where(a => !a.Software))
        {
            sb.Append("GPU: ").Append(a.Name).Append("（").Append(a.Vendor).Append("、専用 ")
              .Append(AccelerationPolicy.Gb(a.DedicatedMemory)).Append("／共有 ").Append(AccelerationPolicy.Gb(a.SharedMemory)).Append("）\n");
        }

        lock (Gate)
        {
            foreach (string engine in new[] { "Whisper", "Qwen" })
            {
                sb.Append(engine).Append(": ");
                sb.Append(Actual.TryGetValue(engine, out var c) ? $"{c.Backend}（{c.Reason}）" : "まだ使っていません（初回の処理で決まります）");
                sb.Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>Lists graphics adapters with DXGI (no extra package, no network).</summary>
public static class GpuAdapters
{
    public static IReadOnlyList<GpuAdapter> Find()
    {
        var list = new List<GpuAdapter>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factoryObject) != 0 || factoryObject is not IDXGIFactory1 factory)
            {
                return list;
            }

            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
                {
                    try
                    {
                        if (adapter.GetDesc1(out var desc) == 0)
                        {
                            list.Add(new GpuAdapter(desc.Description.TrimEnd('\0'), desc.VendorId, (long)desc.DedicatedVideoMemory,
                                (long)desc.SharedSystemMemory, (desc.Flags & 2) != 0)); // DXGI_ADAPTER_FLAG_SOFTWARE
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            // No DXGI (very unusual): treat as "no GPU", the CPU is used.
        }

        // The same card can be listed once per output; keep one entry per name.
        return list.GroupBy(a => (a.Name, a.DedicatedMemory)).Select(g => g.First()).ToList();
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    // Only the methods used are declared; the vtable order below must match dxgi.h.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();

        [PreserveSig]
        int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();

        [PreserveSig]
        int GetDesc1(out DxgiAdapterDesc1 desc);
    }
}
