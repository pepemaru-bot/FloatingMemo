using FloatingMemo.Services.Acceleration;

namespace FloatingMemo.Tests.Settings;

/// <summary>CPU / GPU choice for made-up adapters (no real hardware is read here).</summary>
public sealed class AccelerationTests
{
    private const long Gb = 1L << 30;

    private static readonly GpuAdapter Discrete8 = new("架空 GPU 8GB", 0x10DE, 8 * Gb, 16 * Gb, false);
    private static readonly GpuAdapter Discrete2 = new("架空 GPU 2GB", 0x1002, 2 * Gb, 16 * Gb, false);
    private static readonly GpuAdapter Integrated = new("架空 内蔵 GPU", 0x8086, 128L << 20, 16 * Gb, false);
    private static readonly GpuAdapter Software = new("Microsoft Basic Render Driver", 0x1414, 0, 16 * Gb, true);

    private const long Model4B = 2_497_280_256;

    [Fact]
    public void CpuOnly_NeverUsesTheGpu()
    {
        Assert.False(AccelerationPolicy.ForWhisper(AccelerationMode.CpuOnly, [Discrete8]).UseGpu);
        Assert.False(AccelerationPolicy.ForLlm(AccelerationMode.CpuOnly, [Discrete8], Model4B).UseGpu);
    }

    [Fact]
    public void Auto_UsesADedicatedCard_WhenTheModelFits()
    {
        Assert.True(AccelerationPolicy.ForWhisper(AccelerationMode.Auto, [Integrated, Discrete8]).UseGpu);
        var llm = AccelerationPolicy.ForLlm(AccelerationMode.Auto, [Integrated, Discrete8], Model4B);
        Assert.True(llm.UseGpu);
        Assert.Contains("架空 GPU 8GB", llm.Reason);
    }

    [Fact]
    public void Auto_StaysOnTheCpu_WhenTheModelDoesNotFit_OrThereIsNoDedicatedCard()
    {
        var small = AccelerationPolicy.ForLlm(AccelerationMode.Auto, [Discrete2], Model4B);
        Assert.False(small.UseGpu);
        Assert.Contains("足りない", small.Reason);

        Assert.False(AccelerationPolicy.ForWhisper(AccelerationMode.Auto, [Integrated]).UseGpu);
        Assert.False(AccelerationPolicy.ForLlm(AccelerationMode.Auto, [Integrated], Model4B).UseGpu);
        Assert.False(AccelerationPolicy.ForWhisper(AccelerationMode.Auto, [Software]).UseGpu);
        Assert.False(AccelerationPolicy.ForWhisper(AccelerationMode.Auto, []).UseGpu);
    }

    [Fact]
    public void PreferGpu_AlsoTriesIntegratedGraphics_ButNeverTheSoftwareRenderer()
    {
        Assert.True(AccelerationPolicy.ForWhisper(AccelerationMode.PreferGpu, [Integrated]).UseGpu);
        Assert.True(AccelerationPolicy.ForLlm(AccelerationMode.PreferGpu, [Integrated], Model4B).UseGpu); // shared memory is enough
        Assert.False(AccelerationPolicy.ForWhisper(AccelerationMode.PreferGpu, [Software]).UseGpu);
    }

    [Fact]
    public void Report_ListsAdaptersAndEngines()
    {
        AccelerationState.Record(new EngineChoice("Whisper", true, "GPU / Vulkan", "架空 GPU 8GB を使用"));
        string report = AccelerationState.Report();
        Assert.Contains("Whisper: GPU / Vulkan（架空 GPU 8GB を使用）", report);
        Assert.Contains("Qwen:", report);
    }
}
