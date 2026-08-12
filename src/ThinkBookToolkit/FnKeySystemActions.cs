using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ThinkBookToolkit;

internal static class FnKeySystemActions
{
    private static readonly Guid RadioManagerClsid =
        new("581333F6-28DB-41BE-BC7A-FF201F12F3F6");
    internal static bool TryToggleAirplaneMode(
        out bool airplaneModeOn,
        out string error)
    {
        airplaneModeOn = false;
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(RadioManagerClsid) ??
                throw new InvalidOperationException(
                    "Windows Radio Manager is unavailable.");
            instance = Activator.CreateInstance(type) ??
                throw new InvalidOperationException(
                    "Windows Radio Manager could not be created.");
            var manager = (IRadioManager)instance;
            ThrowForHResult(manager.GetSystemRadioState(
                out var state,
                out _,
                out _));
            var next = state == 0 ? 1 : 0;
            ThrowForHResult(manager.SetSystemRadioState(next));
            airplaneModeOn = next == 0;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                Marshal.FinalReleaseComObject(instance);
        }
    }

    internal static bool TryToggleMicrophones(
        out bool muted,
        out string error)
    {
        muted = false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator
                .EnumerateAudioEndPoints(
                    DataFlow.Capture,
                    DeviceState.Active)
                .ToArray();
            if (devices.Length == 0)
            {
                throw new InvalidOperationException(
                    "No active microphone endpoint is available.");
            }
            try
            {
                var allMuted = devices.All(device =>
                    device.AudioEndpointVolume.Mute);
                muted = !allMuted;
                foreach (var device in devices)
                    device.AudioEndpointVolume.Mute = muted;
            }
            finally
            {
                foreach (var device in devices)
                    device.Dispose();
            }
            _ = SynchronizeMicrophoneLed(muted);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    internal static bool TryReadMuteState(
        DataFlow dataFlow,
        out bool muted)
    {
        muted = false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (dataFlow == DataFlow.Render)
            {
                MMDevice? device = null;
                try
                {
                    device = enumerator.GetDefaultAudioEndpoint(
                        DataFlow.Render,
                        Role.Multimedia);
                }
                catch
                {
                    device = enumerator
                        .EnumerateAudioEndPoints(
                            DataFlow.Render,
                            DeviceState.Active)
                        .FirstOrDefault();
                }
                if (device is null)
                    return false;
                using (device)
                {
                    muted = device.AudioEndpointVolume.Mute;
                    return true;
                }
            }

            var devices = enumerator
                .EnumerateAudioEndPoints(dataFlow, DeviceState.Active)
                .ToArray();
            if (devices.Length == 0)
                return false;
            try
            {
                muted = devices.All(device =>
                    device.AudioEndpointVolume.Mute);
                return true;
            }
            finally
            {
                foreach (var device in devices)
                    device.Dispose();
            }
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                $"The {dataFlow} mute state could not be read: " +
                ex.Message);
            return false;
        }
    }

    internal static bool SynchronizeMicrophoneLed(bool muted) =>
        SynchronizeLed(muted ? 1 : 2, "microphone");

    internal static bool SynchronizeSpeakerLed(bool muted) =>
        SynchronizeLed(muted ? 4 : 5, "speaker");

    private static bool SynchronizeLed(int state, string device)
    {
        try
        {
            LenovoWmi.SetUtilityFeature(state);
            return true;
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                $"The {device} indicator could not be synchronized: " +
                ex.Message);
            return false;
        }
    }

    private static void ThrowForHResult(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    [ComImport]
    [Guid("581333F6-28DB-41BE-BC7A-FF201F12F3F6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRadioManager
    {
        [PreserveSig] int Reserved1();
        [PreserveSig] int Reserved2();
        [PreserveSig]
        int GetSystemRadioState(
            out int state,
            out int argument2,
            out int argument3);
        [PreserveSig] int SetSystemRadioState(int state);
    }

}

internal sealed class AudioIndicatorMonitor : IMMNotificationClient, IDisposable
{
    private readonly object _deviceGate = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _speaker;
    private bool? _speakerMuted;
    private int _rebinding;
    private bool _disposed;

    internal void Start()
    {
        _enumerator = new MMDeviceEnumerator();
        _enumerator.RegisterEndpointNotificationCallback(this);
        RebindDevices();
        // Microphone Fn presses update their LED synchronously. Only perform
        // one initial reconciliation here: polling or capture-endpoint
        // callbacks can race a rapid second press and restore a stale LED.
        if (FnKeySystemActions.TryReadMuteState(
                DataFlow.Capture,
                out var microphoneMuted))
        {
            FnKeySystemActions.SynchronizeMicrophoneLed(microphoneMuted);
        }
    }

    private void RebindDevices()
    {
        if (_disposed)
            return;
        lock (_deviceGate)
        {
            if (_disposed || _enumerator is null)
                return;
            ClearDevices();
            try
            {
                try
                {
                    _speaker = _enumerator.GetDefaultAudioEndpoint(
                        DataFlow.Render,
                        Role.Multimedia);
                }
                catch
                {
                    _speaker = _enumerator.EnumerateAudioEndPoints(
                            DataFlow.Render,
                            DeviceState.Active)
                        .FirstOrDefault();
                }
                if (_speaker is not null)
                {
                    _speaker.AudioEndpointVolume.OnVolumeNotification +=
                        OnSpeakerVolumeNotification;
                }
            }
            catch (Exception ex)
            {
                ToolkitLog.Warning(
                    "Audio indicator event monitoring could not be initialized: " +
                    ex.Message);
                ClearDevices();
            }
        }
        SynchronizeSpeaker();
    }

    private void OnSpeakerVolumeNotification(
        AudioVolumeNotificationData data)
    {
        if (_speakerMuted == data.Muted)
            return;
        if (FnKeySystemActions.SynchronizeSpeakerLed(data.Muted))
            _speakerMuted = data.Muted;
    }

    private void QueueRebind()
    {
        if (Interlocked.Exchange(ref _rebinding, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50).ConfigureAwait(false);
                RebindDevices();
            }
            finally
            {
                Volatile.Write(ref _rebinding, 0);
            }
        });
    }

    private void SynchronizeSpeaker()
    {
        if (FnKeySystemActions.TryReadMuteState(
                DataFlow.Render,
                out var speakerMuted) &&
            _speakerMuted != speakerMuted &&
            FnKeySystemActions.SynchronizeSpeakerLed(speakerMuted))
        {
            _speakerMuted = speakerMuted;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_deviceGate)
        {
            ClearDevices();
            if (_enumerator is not null)
            {
                try
                {
                    _enumerator.UnregisterEndpointNotificationCallback(this);
                }
                catch
                {
                }
                _enumerator.Dispose();
                _enumerator = null;
            }
        }
    }

    private void ClearDevices()
    {
        if (_speaker is not null)
        {
            _speaker.AudioEndpointVolume.OnVolumeNotification -=
                OnSpeakerVolumeNotification;
            _speaker.Dispose();
            _speaker = null;
        }
    }

    public void OnDefaultDeviceChanged(
        DataFlow dataFlow,
        Role deviceRole,
        string defaultDeviceId) => QueueRebind();

    public void OnDeviceAdded(string deviceId) => QueueRebind();

    public void OnDeviceRemoved(string deviceId) => QueueRebind();

    public void OnDeviceStateChanged(
        string deviceId,
        DeviceState newState) => QueueRebind();

    public void OnPropertyValueChanged(
        string deviceId,
        PropertyKey propertyKey)
    {
    }
}
