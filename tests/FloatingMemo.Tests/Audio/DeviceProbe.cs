using FloatingMemo.Services.Audio;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Audio;

/// <summary>Runs only when FLOATINGMEMO_DEVICES=1: lists this PC's recording devices (never opens one).</summary>
public sealed class DeviceProbeFactAttribute : FactAttribute
{
    public DeviceProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_DEVICES") != "1")
        {
            Skip = "Set FLOATINGMEMO_DEVICES=1 to list the real recording devices.";
        }
    }
}

public sealed class DeviceProbe(ITestOutputHelper output)
{
    [DeviceProbeFact]
    public void ListCaptureDevices()
    {
        using var catalog = new WasapiDeviceCatalog();
        output.WriteLine($"default: {catalog.DefaultCapture(false)?.Name}");
        output.WriteLine($"communications: {catalog.DefaultCapture(true)?.Name}");
        foreach (var device in catalog.CaptureDevices())
        {
            output.WriteLine($"{device.Name}  [{device.Id}]");
            Assert.Equal(device, catalog.FindCapture(device.Id));
        }

        Assert.Null(catalog.FindCapture("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"));
    }
}
