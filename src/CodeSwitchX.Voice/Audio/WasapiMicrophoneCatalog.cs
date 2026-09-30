using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CodeSwitchX.Voice.Audio;

/// <summary>
/// The capture devices. <see cref="List"/> and <see cref="Default"/> create their own enumerator on the calling thread:
/// the panel lists the devices on the thread pool, and the long-lived one, created with the catalog on the UI thread,
/// could have its calls marshalled back to that thread. The long-lived one only carries the notifications.
/// </summary>
public sealed class WasapiMicrophoneCatalog : IMicrophoneCatalog, IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private int _disposed;

    public WasapiMicrophoneCatalog()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<MicrophoneDevice> List()
    {
        var result = new List<MicrophoneDevice>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new MicrophoneDevice(device.ID, device.FriendlyName));
            }
        }

        return result;
    }

    public MicrophoneDevice? Default()
    {
        using var enumerator = new MMDeviceEnumerator();
        return DefaultFor(enumerator, Role.Communications) ?? DefaultFor(enumerator, Role.Console);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Raise();

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Raise();

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Raise();

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Capture)
        {
            Raise();
        }
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    private static MicrophoneDevice? DefaultFor(MMDeviceEnumerator enumerator, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            return new MicrophoneDevice(device.ID, device.FriendlyName);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void Raise() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
