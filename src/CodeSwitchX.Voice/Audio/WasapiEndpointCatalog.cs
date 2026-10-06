using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CodeSwitchX.Voice.Audio;

/// <summary>
/// The active endpoints of one direction, capture or render. <see cref="List"/> and <see cref="Default"/> create their
/// own enumerator on the calling thread: the panel lists the devices on the thread pool, and the long-lived one, created
/// with the catalog on the UI thread, could have its calls marshalled back to that thread. The long-lived one only
/// carries the notifications.
/// </summary>
public abstract class WasapiEndpointCatalog<T> : IMMNotificationClient, IDisposable
    where T : class
{
    private readonly DataFlow _flow;
    private readonly Func<string, string, T> _make;
    private readonly MMDeviceEnumerator _enumerator = new();
    private int _disposed;

    protected WasapiEndpointCatalog(DataFlow flow, Func<string, string, T> make)
    {
        _flow = flow;
        _make = make;
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<T> List()
    {
        var result = new List<T>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(_flow, DeviceState.Active))
        {
            using (device)
            {
                result.Add(_make(device.ID, device.FriendlyName));
            }
        }

        return result;
    }

    public T? Default()
    {
        using var enumerator = new MMDeviceEnumerator();
        return DefaultFor(enumerator, Role.Console) ?? DefaultFor(enumerator, Role.Communications);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Raise();

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Raise();

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Raise();

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == _flow)
        {
            Raise();
        }
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    private T? DefaultFor(MMDeviceEnumerator enumerator, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(_flow, role);
            return _make(device.ID, device.FriendlyName);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void Raise() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>The capture devices.</summary>
public sealed class WasapiMicrophoneCatalog() : WasapiEndpointCatalog<MicrophoneDevice>(DataFlow.Capture, (id, name) => new MicrophoneDevice(id, name)),
    IMicrophoneCatalog;

/// <summary>The output devices.</summary>
public sealed class WasapiSpeakerCatalog() : WasapiEndpointCatalog<SpeakerDevice>(DataFlow.Render, (id, name) => new SpeakerDevice(id, name)),
    ISpeakerCatalog;
