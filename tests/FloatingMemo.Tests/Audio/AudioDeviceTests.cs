using FloatingMemo.Services.Audio;
using FloatingMemo.Services.Settings;

namespace FloatingMemo.Tests.Audio;

public sealed class AudioDeviceResolverTests
{
    private static readonly AudioDeviceInfo Array = new("{array}", "マイク配列", true);
    private static readonly AudioDeviceInfo Usb = new("{usb}", "USB マイク", true);
    private static readonly AudioDeviceInfo StereoMix = new("{mix}", "ステレオ ミキサー", true);

    private static StaticDeviceCatalog Catalog(string? defaultId = "{mix}", string? comms = "{usb}") =>
        new([Array, Usb, StereoMix], defaultId, comms);

    [Fact]
    public void SystemDefault_FollowsWindowsDefault()
    {
        var catalog = Catalog();
        Assert.Equal(StereoMix, AudioDeviceResolver.Resolve(catalog, AudioDeviceSelection.SystemDefault).Device);

        catalog.Update([Array, Usb, StereoMix], "{array}");
        var resolved = AudioDeviceResolver.Resolve(catalog, AudioDeviceSelection.SystemDefault);
        Assert.True(resolved.IsUsable);
        Assert.Equal(Array, resolved.Device);
    }

    [Fact]
    public void Communications_UsesCommunicationsDefault()
    {
        var resolved = AudioDeviceResolver.Resolve(Catalog(), new AudioDeviceSelection(DeviceSelectionMode.SystemCommunications));
        Assert.Equal(Usb, resolved.Device);
    }

    [Fact]
    public void Specific_IsFoundById_EvenWhenNotTheDefault()
    {
        var resolved = AudioDeviceResolver.Resolve(Catalog(), new AudioDeviceSelection(DeviceSelectionMode.Specific, "{array}", "古い名前"));
        Assert.True(resolved.IsUsable);
        Assert.Equal(Array, resolved.Device);
    }

    [Fact]
    public void Specific_Missing_ReportsProblem_AndNeverFallsBack()
    {
        var resolved = AudioDeviceResolver.Resolve(Catalog(), new AudioDeviceSelection(DeviceSelectionMode.Specific, "{gone}", "ヘッドセット"));
        Assert.False(resolved.IsUsable);
        Assert.Null(resolved.Device);
        Assert.Contains("ヘッドセット", resolved.Problem);
    }

    [Fact]
    public void Specific_Unplugged_ReportsProblem()
    {
        var catalog = new StaticDeviceCatalog([Array, Usb with { IsActive = false }], "{array}");
        var resolved = AudioDeviceResolver.Resolve(catalog, new AudioDeviceSelection(DeviceSelectionMode.Specific, "{usb}", "USB マイク"));
        Assert.False(resolved.IsUsable);
        Assert.Contains("USB マイク", resolved.Problem);
        Assert.DoesNotContain(catalog.CaptureDevices(), d => d.Id == "{usb}");
    }

    [Fact]
    public void NoDefault_ReportsProblem()
    {
        var resolved = AudioDeviceResolver.Resolve(Catalog(defaultId: null, comms: null), AudioDeviceSelection.SystemDefault);
        Assert.False(resolved.IsUsable);
        Assert.NotNull(resolved.Problem);
    }

    [Fact]
    public void Update_RaisesDevicesChanged()
    {
        var catalog = Catalog();
        int raised = 0;
        catalog.DevicesChanged += (_, _) => raised++;
        catalog.Update([Array], "{array}");
        Assert.Equal(1, raised);
    }

    [Fact]
    public void UnavailableDevice_MessageIsShownAsIs()
    {
        var ex = new AudioCaptureException(AudioSources.Microphone, new AudioDeviceUnavailableException("「USB マイク」が見つかりません。"));
        Assert.Contains("「USB マイク」が見つかりません。", ex.Message);
    }

    [Fact]
    public void Selection_RoundTripsThroughSettings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SettingsService(Path.Combine(dir, "settings.json"));
            service.Save(new AppSettings { Microphone = new AudioDeviceSelection(DeviceSelectionMode.Specific, "{0.0.1.00000000}.{abc}", "USB マイク") });

            string json = File.ReadAllText(service.FilePath);
            Assert.Contains("\"Specific\"", json); // stored as text so the file stays readable

            var loaded = service.Load().Microphone;
            Assert.Equal(DeviceSelectionMode.Specific, loaded.Mode);
            Assert.Equal("{0.0.1.00000000}.{abc}", loaded.DeviceId);
            Assert.Equal("USB マイク", loaded.DeviceName);

            Assert.Equal(DeviceSelectionMode.SystemDefault, new SettingsService(Path.Combine(dir, "none.json")).Load().Microphone.Mode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void OldSettingsFile_WithoutMicrophone_LoadsDefault()
    {
        string dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, "{ \"RecordMicrophone\": false }");
            var loaded = new SettingsService(file).Load();
            Assert.False(loaded.RecordMicrophone);
            Assert.Equal(AudioDeviceSelection.SystemDefault, loaded.Microphone);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
