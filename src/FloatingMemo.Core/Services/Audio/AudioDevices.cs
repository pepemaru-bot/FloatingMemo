using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;

namespace FloatingMemo.Services.Audio;

/// <summary>How the microphone (or, later, the PC-audio output) is chosen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceSelectionMode>))]
public enum DeviceSelectionMode
{
    /// <summary>Whatever Windows uses as the default recording device (follows changes).</summary>
    SystemDefault,

    /// <summary>Windows' default communications device (headsets are often set as this).</summary>
    SystemCommunications,

    /// <summary>One specific device, remembered by its stable endpoint ID.</summary>
    Specific,
}

/// <summary>
/// The user's device choice. The endpoint ID is what identifies the device (names can repeat
/// and change); the name is kept only to show something sensible when the device is missing.
/// </summary>
public sealed record AudioDeviceSelection(DeviceSelectionMode Mode, string? DeviceId = null, string? DeviceName = null)
{
    public static AudioDeviceSelection SystemDefault { get; } = new(DeviceSelectionMode.SystemDefault);
}

/// <summary>A Windows audio endpoint (microphone, headset, USB mic, stereo mix, …).</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsActive);

/// <summary>Which device to use, with its display name, or why it cannot be used.</summary>
public sealed record ResolvedDevice(AudioDeviceInfo? Device, string? Problem)
{
    public bool IsUsable => Device is { IsActive: true } && Problem is null;
}

/// <summary>Lists recording devices and reports changes (plugged in/out, new default, …).</summary>
public interface IAudioDeviceCatalog
{
    /// <summary>Recording devices that can be used right now.</summary>
    IReadOnlyList<AudioDeviceInfo> CaptureDevices();

    /// <summary>Windows' default recording device for general use or for communications.</summary>
    AudioDeviceInfo? DefaultCapture(bool communications);

    /// <summary>A device by ID, in any state (inactive if unplugged or disabled), or null if unknown.</summary>
    AudioDeviceInfo? FindCapture(string id);

    /// <summary>Raised (on a background thread) when devices or defaults change.</summary>
    event EventHandler? DevicesChanged;
}

/// <summary>Turns a selection into a concrete device, never silently substituting another one.</summary>
public static class AudioDeviceResolver
{
    public static ResolvedDevice Resolve(IAudioDeviceCatalog catalog, AudioDeviceSelection selection)
    {
        switch (selection.Mode)
        {
            case DeviceSelectionMode.SystemDefault:
            case DeviceSelectionMode.SystemCommunications:
            {
                bool communications = selection.Mode == DeviceSelectionMode.SystemCommunications;
                var device = catalog.DefaultCapture(communications);
                return device is null
                    ? new ResolvedDevice(null, communications ? "Windows の既定の通信デバイスがありません。" : "Windows の既定の録音デバイスがありません。")
                    : new ResolvedDevice(device, null);
            }

            default:
            {
                string name = selection.DeviceName ?? "選択したマイク";
                if (string.IsNullOrEmpty(selection.DeviceId) || catalog.FindCapture(selection.DeviceId) is not { } device)
                {
                    return new ResolvedDevice(null, $"「{name}」が見つかりません。取り外されたか、削除された可能性があります。");
                }

                return device.IsActive
                    ? new ResolvedDevice(device, null)
                    : new ResolvedDevice(device, $"「{device.Name}」は現在使用できません（取り外し、または無効化されています）。");
            }
        }
    }
}

/// <summary>Windows Core Audio implementation (MMDevice API via NAudio).</summary>
public sealed class WasapiDeviceCatalog : IAudioDeviceCatalog, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;

    public WasapiDeviceCatalog()
    {
        // Enumerating and watching endpoints never opens a device or starts recording.
        _notifications = _enumerator.CreateNotificationClient(false);
        _notifications.DeviceAdded += (_, _) => Raise();
        _notifications.DeviceRemoved += (_, _) => Raise();
        _notifications.DeviceStateChanged += (_, _) => Raise();
        _notifications.DefaultDeviceChanged += (_, e) =>
        {
            if (e.Flow == DataFlow.Capture)
            {
                Raise();
            }
        };
    }

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> CaptureDevices()
    {
        var result = new List<AudioDeviceInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo(device.ID, SafeName(device), true));
            }
        }

        return result.OrderBy(d => d.Name, StringComparer.CurrentCulture).ToList();
    }

    public AudioDeviceInfo? DefaultCapture(bool communications)
    {
        if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, communications ? Role.Communications : Role.Console, out var device))
        {
            return null;
        }

        using (device)
        {
            return new AudioDeviceInfo(device.ID, SafeName(device), device.State == DeviceState.Active);
        }
    }

    public AudioDeviceInfo? FindCapture(string id)
    {
        try
        {
            using var device = _enumerator.GetDevice(id);
            return device.DataFlow == DataFlow.Capture
                ? new AudioDeviceInfo(device.ID, SafeName(device), device.State == DeviceState.Active)
                : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return null; // unknown ID (device removed from the system)
        }
    }

    /// <summary>Opens the endpoint for capture (the caller owns and disposes it).</summary>
    internal MMDevice Open(string id) => _enumerator.GetDevice(id);

    private static string SafeName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return device.ID; // name unavailable (e.g. device being removed)
        }
    }

    private void Raise() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _notifications.Dispose();
        _enumerator.Dispose();
    }
}

/// <summary>
/// A fixed device list for tests (unit tests and the "--fake-devices" test hook), so device
/// selection can be exercised without touching real audio hardware.
/// </summary>
public sealed class StaticDeviceCatalog : IAudioDeviceCatalog
{
    private readonly object _lock = new();
    private List<AudioDeviceInfo> _devices;
    private string? _defaultId;
    private string? _communicationsId;

    public StaticDeviceCatalog(IEnumerable<AudioDeviceInfo> devices, string? defaultId, string? communicationsId = null)
    {
        _devices = devices.ToList();
        _defaultId = defaultId;
        _communicationsId = communicationsId ?? defaultId;
    }

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> CaptureDevices()
    {
        lock (_lock)
        {
            return _devices.Where(d => d.IsActive).ToList();
        }
    }

    public AudioDeviceInfo? DefaultCapture(bool communications)
    {
        lock (_lock)
        {
            string? id = communications ? _communicationsId : _defaultId;
            return _devices.FirstOrDefault(d => d.Id == id && d.IsActive);
        }
    }

    public AudioDeviceInfo? FindCapture(string id)
    {
        lock (_lock)
        {
            return _devices.FirstOrDefault(d => d.Id == id);
        }
    }

    /// <summary>Simulates unplugging/replugging a device or changing the Windows default.</summary>
    public void Update(IEnumerable<AudioDeviceInfo> devices, string? defaultId, string? communicationsId = null)
    {
        lock (_lock)
        {
            _devices = devices.ToList();
            _defaultId = defaultId;
            _communicationsId = communicationsId ?? defaultId;
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }
}
