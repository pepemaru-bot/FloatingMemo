using FloatingMemo.Services.Settings;

namespace FloatingMemo.Tests.Settings;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Load_Missing_ReturnsDefaults()
    {
        var settings = new SettingsService(FilePath).Load();

        Assert.Null(settings.Window);
        Assert.True(settings.RecordMicrophone);
        Assert.False(settings.RecordSystemAudio);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var service = new SettingsService(FilePath);
        service.Save(new AppSettings
        {
            Window = new WindowBounds(10, 20, 440, 320),
            RecordMicrophone = false,
            RecordSystemAudio = true,
        });

        var loaded = new SettingsService(FilePath).Load();

        Assert.Equal(new WindowBounds(10, 20, 440, 320), loaded.Window);
        Assert.False(loaded.RecordMicrophone);
        Assert.True(loaded.RecordSystemAudio);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    public void Load_Corrupt_ReturnsDefaultsWithoutThrowing(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, content);

        var settings = new SettingsService(FilePath).Load();

        Assert.True(settings.RecordMicrophone);
        Assert.Null(settings.Window);
    }

    [Fact]
    public void TrySave_Unwritable_ReturnsFalse()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{}");
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);
        try
        {
            Assert.False(new SettingsService(FilePath).TrySave(new AppSettings()));
        }
        finally
        {
            File.SetAttributes(FilePath, FileAttributes.Normal);
        }
    }
}

public sealed class WindowPlacementTests
{
    private static readonly ScreenRect Primary = new(0, 0, 1920, 1040);
    private static readonly ScreenRect Right = new(1920, 0, 2560, 1400);

    [Fact]
    public void FirstLaunch_CentersOnPrimary()
    {
        var bounds = WindowPlacement.Resolve(null, [Primary], 440, 320);

        Assert.Equal(new WindowBounds(740, 360, 440, 320), bounds);
    }

    [Fact]
    public void SavedOnScreen_IsKept()
    {
        var saved = new WindowBounds(2000, 100, 500, 400);

        Assert.Equal(saved, WindowPlacement.Resolve(saved, [Primary, Right], 440, 320));
    }

    [Fact]
    public void SavedOnDisconnectedMonitor_MovesToPrimaryKeepingSize()
    {
        var saved = new WindowBounds(2000, 100, 500, 400);

        var bounds = WindowPlacement.Resolve(saved, [Primary], 440, 320);

        Assert.Equal(new WindowBounds(710, 320, 500, 400), bounds);
    }

    [Fact]
    public void TitleStripAboveScreen_IsMovedBack()
    {
        var saved = new WindowBounds(100, -200, 440, 320);

        var bounds = WindowPlacement.Resolve(saved, [Primary], 440, 320);

        Assert.True(bounds.Y >= 0);
    }

    [Fact]
    public void LargerThanMonitor_IsShrunk()
    {
        var saved = new WindowBounds(0, 0, 5000, 3000);

        var bounds = WindowPlacement.Resolve(saved, [Primary], 440, 320);

        Assert.Equal(1920, bounds.Width);
        Assert.Equal(1040, bounds.Height);
    }

    [Fact]
    public void MostlyOffScreenToTheLeft_ButGrabbable_IsKept()
    {
        var saved = new WindowBounds(-300, 100, 440, 320); // 140 px still visible

        Assert.Equal(saved, WindowPlacement.Resolve(saved, [Primary], 440, 320));
    }
}
