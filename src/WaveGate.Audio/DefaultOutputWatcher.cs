using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WaveGate.Audio;

/// <summary>Raises <see cref="Changed"/> (on a COM worker thread) when the default playback device changes.</summary>
public sealed class DefaultOutputWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public DefaultOutputWatcher() => _enumerator.RegisterEndpointNotificationCallback(this);

    public event Action? Changed;

    // Windows reports each role separately; Multimedia is the one SoundEngine follows.
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) Changed?.Invoke();
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }
}
