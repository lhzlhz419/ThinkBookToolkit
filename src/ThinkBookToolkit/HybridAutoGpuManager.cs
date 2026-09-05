using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal enum HybridAutoGpuState
{
    FullTelemetry,
    QuiescingDiscreteGpu,
    IntegratedOnlyTelemetry,
    WaitingForDiscreteGpu,
    Suspended
}

internal readonly record struct DiscreteGpuHardwareIdentity(
    string VendorId,
    string DeviceId)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(VendorId) ||
                           string.IsNullOrWhiteSpace(DeviceId);

    public bool Matches(string deviceInstanceId) =>
        !IsEmpty &&
        deviceInstanceId.Contains(
            "VEN_" + VendorId,
            StringComparison.OrdinalIgnoreCase) &&
        deviceInstanceId.Contains(
            "DEV_" + DeviceId,
            StringComparison.OrdinalIgnoreCase);
}

internal sealed record DiscreteGpuPresenceSnapshot(
    bool Reliable,
    bool IsPresent,
    IReadOnlyList<string> MatchingDeviceIds);

internal static class HybridAutoGpuPolicy
{
    public static bool ShouldDisconnectDiscreteGpu(
        GpuWorkingMode? workingMode,
        bool isAcConnected) =>
        workingMode == GpuWorkingMode.IntegratedOnly ||
        workingMode == GpuWorkingMode.HybridAuto && !isAcConnected;

    public static bool ShouldUseSoftwareRendering(
        GpuWorkingMode? workingMode) =>
        workingMode is GpuWorkingMode.IntegratedOnly or
            GpuWorkingMode.HybridAuto;

    public static bool ShouldEnterSilentEjectWindow(
        GpuTelemetryMode telemetryMode,
        DiscreteGpuActivityState activityState) =>
        telemetryMode == GpuTelemetryMode.Quiescing &&
        activityState is DiscreteGpuActivityState.Inactive or
            DiscreteGpuActivityState.Off;

    public static GpuTelemetryMode ResolveTelemetryMode(
        GpuWorkingMode? workingMode,
        bool isAcConnected,
        DiscreteGpuPresenceSnapshot presence,
        GpuTelemetryMode previous,
        bool startupProtectionActive = false)
    {
        if (!workingMode.HasValue)
        {
            if (presence.Reliable)
            {
                return presence.IsPresent
                    ? startupProtectionActive
                        ? previous
                        : GpuTelemetryMode.Full
                    : GpuTelemetryMode.IntegratedOnly;
            }

            return startupProtectionActive || previous != GpuTelemetryMode.Paused
                ? previous
                : GpuTelemetryMode.Full;
        }

        if (ShouldDisconnectDiscreteGpu(workingMode, isAcConnected))
        {
            if (presence.Reliable && !presence.IsPresent)
                return GpuTelemetryMode.IntegratedOnly;

            // Quiescing keeps telemetry available only while the dGPU has a
            // real client. The worker changes this to Paused as soon as NVAPI
            // reports Inactive/Off, and the policy must retain that silent
            // window until PnP removal completes or the requested mode no
            // longer requires an eject.
            return startupProtectionActive ||
                   previous == GpuTelemetryMode.Paused
                ? GpuTelemetryMode.Paused
                : GpuTelemetryMode.Quiescing;
        }

        if (presence.Reliable)
        {
            return presence.IsPresent
                ? GpuTelemetryMode.Full
                : GpuTelemetryMode.IntegratedOnly;
        }

        return previous == GpuTelemetryMode.Paused
            ? GpuTelemetryMode.Full
            : previous;
    }
}

internal sealed class HybridAutoGpuManager : IDisposable
{
    private const int MaximumNotifyAttempts = 5;
    private static readonly TimeSpan NotifyRetryDelay = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LenovoDiscreteGpuNotifier _notifier = new();
    private readonly DiscreteGpuPresenceDetector _presenceDetector;
    private CancellationTokenSource? _reconcileCancellation;
    private GpuWorkingMode? _workingMode;
    private bool _isAcConnected = true;
    private bool? _lastPresence;
    private bool _startupProtectionActive;
    private bool _suspended;
    private bool _disposed;

    public HybridAutoGpuManager()
    {
        _presenceDetector = new DiscreteGpuPresenceDetector(
            () => _notifier.HardwareIdentity);
        _startupProtectionActive =
            GpuTelemetryControl.Mode == GpuTelemetryMode.Paused;
    }

    public HybridAutoGpuState State { get; private set; } =
        HybridAutoGpuState.FullTelemetry;

    public event EventHandler<bool>? PresenceChanged;

    public static void PrepareForApplicationStartup()
    {
        try
        {
            var state = GpuModeController.ReadState();
            if (!HybridAutoGpuPolicy.ShouldUseSoftwareRendering(
                    state.CurrentMode))
                return;

            var isAcConnected = true;
            if (BatteryInformationReader.TryGetAcConnectionState(
                    out var detectedAcState))
            {
                isAcConnected = detectedAcState;
            }
            if (HybridAutoGpuPolicy.ShouldDisconnectDiscreteGpu(
                    state.CurrentMode,
                    isAcConnected))
            {
                GpuTelemetryControl.SetMode(
                    GpuTelemetryMode.Paused,
                    state.CurrentMode == GpuWorkingMode.IntegratedOnly
                        ? "iGPU-only mode is active at application startup"
                        : "Hybrid Auto started on battery power");
            }
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "Hybrid GPU startup protection could not be initialized: " +
                ex.Message);
        }
    }

    public async Task UpdateAsync(
        GpuWorkingMode? workingMode,
        bool isAcConnected)
    {
        if (_disposed)
            return;

        bool? changed = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _workingMode = workingMode;
            _isAcConnected = isAcConnected;
            _suspended = false;
            if (!HybridAutoGpuPolicy.ShouldDisconnectDiscreteGpu(
                    workingMode,
                    isAcConnected))
            {
                _startupProtectionActive = false;
            }
            ApplyRenderMode();
            var presence = _presenceDetector.Capture();
            changed = ApplyPresence(presence);
            SyncReconcileLoop(presence);
        }
        finally
        {
            _gate.Release();
        }

        PublishPresenceChange(changed);
    }

    public async Task ObserveAsync()
    {
        if (_disposed || _suspended)
            return;

        bool? changed = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var presence = _presenceDetector.Capture();
            changed = ApplyPresence(presence);
            SyncReconcileLoop(presence);
        }
        finally
        {
            _gate.Release();
        }

        PublishPresenceChange(changed);
    }

    public void Suspend()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
                return;

            _suspended = true;
            CancelReconcileLoop();
            State = HybridAutoGpuState.Suspended;
            GpuTelemetryControl.SetMode(
                GpuTelemetryMode.Paused,
                "Windows is entering sleep");
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool? ApplyPresence(DiscreteGpuPresenceSnapshot presence)
    {
        bool? changed = null;
        if (presence.Reliable)
        {
            if (_lastPresence.HasValue &&
                _lastPresence.Value != presence.IsPresent)
            {
                changed = presence.IsPresent;
            }
            _lastPresence = presence.IsPresent;
        }

        if (presence.Reliable && !presence.IsPresent)
            _startupProtectionActive = false;

        var previousMode = GpuTelemetryControl.Mode;
        var telemetryMode = HybridAutoGpuPolicy.ResolveTelemetryMode(
            _workingMode,
            _isAcConnected,
            presence,
            previousMode,
            _startupProtectionActive);
        GpuTelemetryControl.SetMode(
            telemetryMode,
            DescribeTransition(presence, telemetryMode));
        State = telemetryMode switch
        {
            GpuTelemetryMode.IntegratedOnly =>
                HybridAutoGpuState.IntegratedOnlyTelemetry,
            GpuTelemetryMode.Quiescing =>
                HybridAutoGpuState.QuiescingDiscreteGpu,
            _ when HybridAutoGpuPolicy.ShouldDisconnectDiscreteGpu(
                _workingMode,
                _isAcConnected) &&
                (!presence.Reliable || presence.IsPresent) =>
                HybridAutoGpuState.QuiescingDiscreteGpu,
            GpuTelemetryMode.Full => HybridAutoGpuState.FullTelemetry,
            _ => HybridAutoGpuState.WaitingForDiscreteGpu
        };

        if (changed.HasValue)
        {
            ToolkitLog.Info(
                changed.Value
                    ? "The discrete GPU was connected."
                    : "The discrete GPU was disconnected.");
            _ = Task.Run(() => _notifier.TryNotify(changed.Value));
        }

        return changed;
    }

    private string DescribeTransition(
        DiscreteGpuPresenceSnapshot presence,
        GpuTelemetryMode telemetryMode)
    {
        if (telemetryMode == GpuTelemetryMode.Paused)
        {
            return HybridAutoGpuPolicy.ShouldDisconnectDiscreteGpu(
                       _workingMode,
                       _isAcConnected)
                ? _workingMode == GpuWorkingMode.IntegratedOnly
                    ? "iGPU-only mode is preparing to disconnect the discrete GPU"
                    : "Hybrid Auto is preparing to disconnect the discrete GPU"
                : "waiting for the discrete GPU state to stabilize";
        }
        if (telemetryMode == GpuTelemetryMode.IntegratedOnly)
            return "the discrete GPU is not present";
        if (HybridAutoGpuPolicy.ShouldDisconnectDiscreteGpu(
                _workingMode,
                _isAcConnected) &&
            (!presence.Reliable || presence.IsPresent))
        {
            return telemetryMode == GpuTelemetryMode.Paused
                ? "the discrete GPU is inactive; NVIDIA monitoring is fully stopped while firmware removal completes"
                : "the discrete GPU is still active; monitoring remains available until its clients release it";
        }
        return presence.Reliable
            ? "the discrete GPU is present"
            : "the previous reliable GPU state is being retained";
    }

    private void SyncReconcileLoop(DiscreteGpuPresenceSnapshot presence)
    {
        var shouldDisconnect =
            HybridAutoGpuPolicy.ShouldDisconnectDiscreteGpu(
                _workingMode,
                _isAcConnected);
        var shouldWake =
            _workingMode == GpuWorkingMode.HybridAuto && _isAcConnected;
        var unresolved =
            shouldDisconnect && (!presence.Reliable || presence.IsPresent) ||
            shouldWake && presence.Reliable && !presence.IsPresent;
        if (!unresolved)
        {
            CancelReconcileLoop();
            return;
        }

        if (_reconcileCancellation is not null)
            return;

        _reconcileCancellation = new CancellationTokenSource();
        var token = _reconcileCancellation.Token;
        _ = ReconcileAsync(shouldDisconnect, token);
    }

    private async Task ReconcileAsync(
        bool shouldDisconnect,
        CancellationToken token)
    {
        try
        {
            for (var attempt = 1;
                 attempt <= MaximumNotifyAttempts;
                 attempt++)
            {
                await Task.Delay(NotifyRetryDelay, token).ConfigureAwait(false);

                bool? changed;
                bool resolved;
                DiscreteGpuPresenceSnapshot presence;
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var contextStillMatches = shouldDisconnect
                        ? HybridAutoGpuPolicy.ShouldDisconnectDiscreteGpu(
                            _workingMode,
                            _isAcConnected)
                        : _workingMode == GpuWorkingMode.HybridAuto &&
                          _isAcConnected;
                    if (!contextStillMatches || _suspended)
                        return;

                    presence = _presenceDetector.Capture(force: true);
                    changed = ApplyPresence(presence);
                    resolved = presence.Reliable &&
                        (shouldDisconnect
                            ? !presence.IsPresent
                            : presence.IsPresent);
                }
                finally
                {
                    _gate.Release();
                }

                PublishPresenceChange(changed);
                if (resolved)
                    return;
                ToolkitLog.Info(
                    $"Notifying Lenovo firmware of dGPU presence " +
                    $"(attempt {attempt}/{MaximumNotifyAttempts}, " +
                    $"present={presence.IsPresent}, reliable={presence.Reliable}).");
                await Task.Run(
                    () => _notifier.TryNotify(presence.IsPresent),
                    token).ConfigureAwait(false);
            }

            if (shouldDisconnect)
            {
                bool? changed = null;
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var contextStillMatches =
                        HybridAutoGpuPolicy.ShouldDisconnectDiscreteGpu(
                            _workingMode,
                            _isAcConnected);
                    if (contextStillMatches &&
                        !_suspended &&
                        _startupProtectionActive)
                    {
                        _startupProtectionActive = false;
                        var presence = _presenceDetector.Capture(force: true);
                        changed = ApplyPresence(presence);
                        ToolkitLog.Info(
                            "Startup dGPU ejection retries completed without a PnP disconnect; NVIDIA monitoring remains stopped so it cannot prevent a later firmware removal.");
                    }
                }
                finally
                {
                    _gate.Release();
                }

                PublishPresenceChange(changed);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                "Hybrid GPU dGPU reconciliation failed.",
                ex);
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_reconcileCancellation?.Token == token)
                {
                    _reconcileCancellation.Dispose();
                    _reconcileCancellation = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private void PublishPresenceChange(bool? connected)
    {
        if (connected.HasValue)
            PresenceChanged?.Invoke(this, connected.Value);
    }

    private void ApplyRenderMode()
    {
        if (!_workingMode.HasValue)
            return;

        RenderOptions.ProcessRenderMode =
            HardwareAccelerationManager.CurrentMode ==
            HardwareAccelerationMode.Disabled
                ? RenderMode.SoftwareOnly
                : RenderMode.Default;
    }

    private void CancelReconcileLoop()
    {
        var cancellation = _reconcileCancellation;
        _reconcileCancellation = null;
        if (cancellation is null)
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            CancelReconcileLoop();
        }
        finally
        {
            _gate.Release();
        }
        // A cancelled retry may still be unwinding through the semaphore.
        // It is process-local and intentionally left undisposed here so that
        // shutdown cannot turn cancellation into an unobserved exception.
    }
}

internal sealed class LenovoDiscreteGpuNotifier
{
    private const uint GpuStatusCapabilityId = 0x02070000;
    private const uint GpuDidVidCapabilityId = 0x02090000;
    private static readonly Regex HardwareIdPattern = new(
        @"VEN_([0-9A-Fa-f]{4}).*DEV_([0-9A-Fa-f]{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Lazy<DiscreteGpuHardwareIdentity?> _hardwareIdentity =
        new(() =>
        {
            var identity = TryReadHardwareIdentity();
            ToolkitLog.Info(
                identity is { IsEmpty: false } value
                    ? $"Lenovo dGPU hardware ID resolved as " +
                      $"VEN_{value.VendorId}&DEV_{value.DeviceId}."
                    : "Lenovo dGPU hardware ID is unavailable; NVIDIA PCI identity fallback will be used.");
            return identity;
        });

    public DiscreteGpuHardwareIdentity? HardwareIdentity =>
        _hardwareIdentity.Value;

    public bool TryNotify(bool isPresent)
    {
        var failures = new List<string>();
        if (TryInvoke(
                "LENOVO_GAMEZONE_DATA.NotifyDGPUStatus",
                () =>
                {
                    using var instance = LenovoWmi.GetActiveInstance(
                        "LENOVO_GAMEZONE_DATA");
                    LenovoWmi.InvokeVoid(
                        instance,
                        "NotifyDGPUStatus",
                        new Dictionary<string, object>
                        {
                            ["Status"] = isPresent ? 1 : 0
                        });
                },
                failures))
        {
            return true;
        }

        if (TryInvoke(
                "LENOVO_OTHER_METHOD.Set_DGPU_Device_Status",
                () =>
                {
                    using var instance = LenovoWmi.GetActiveInstance(
                        "LENOVO_OTHER_METHOD");
                    LenovoWmi.InvokeVoid(
                        instance,
                        "Set_DGPU_Device_Status",
                        new Dictionary<string, object>
                        {
                            ["Status"] = isPresent ? 1 : 0
                        });
                },
                failures))
        {
            return true;
        }

        if (TryInvoke(
                "LENOVO_OTHER_METHOD.SetFeatureValue(GPUStatus)",
                () => LenovoWmi.SetFeatureValue(
                    GpuStatusCapabilityId,
                    isPresent ? 1 : 0),
                failures))
        {
            return true;
        }

        ToolkitLog.Warning(
            "No Lenovo dGPU notification method succeeded: " +
            string.Join(" | ", failures));
        return false;
    }

    private static DiscreteGpuHardwareIdentity? TryReadHardwareIdentity()
    {
        try
        {
            using var instance = LenovoWmi.GetActiveInstance(
                "LENOVO_GAMEZONE_DATA");
            var value = LenovoWmi.InvokeString(
                instance,
                "GetDGPUHWId",
                null,
                "Data");
            var match = HardwareIdPattern.Match(
                value.Replace("PCIVEN_", "PCI\\VEN_", StringComparison.OrdinalIgnoreCase));
            if (match.Success)
            {
                return new(
                    match.Groups[1].Value.ToUpperInvariant(),
                    match.Groups[2].Value.ToUpperInvariant());
            }
        }
        catch
        {
        }

        foreach (var read in new Func<int>[]
                 {
                     () =>
                     {
                         using var instance = LenovoWmi.GetActiveInstance(
                             "LENOVO_OTHER_METHOD");
                         return LenovoWmi.InvokeInt(
                             instance,
                             "Get_DGPU_Device_DIDVID",
                             null,
                             "DGPU_ID");
                     },
                     () => LenovoWmi.GetFeatureValue(GpuDidVidCapabilityId)
                 })
        {
            try
            {
                var value = unchecked((uint)read());
                var vendorId = value & 0xFFFF;
                var deviceId = value >> 16;
                if (vendorId != 0 && deviceId != 0)
                {
                    return new(
                        vendorId.ToString("X4"),
                        deviceId.ToString("X4"));
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool TryInvoke(
        string name,
        Action action,
        ICollection<string> failures)
    {
        try
        {
            action();
            ToolkitLog.Info(name + " completed successfully.");
            return true;
        }
        catch (Exception ex)
        {
            failures.Add(name + ": " + ex.GetBaseException().Message);
            return false;
        }
    }
}

internal sealed class DiscreteGpuPresenceDetector
{
    private static readonly Guid DisplayClassGuid =
        new("4D36E968-E325-11CE-BFC1-08002BE10318");
    private readonly Func<DiscreteGpuHardwareIdentity?> _identityProvider;
    private DateTimeOffset _lastCapture;
    private DiscreteGpuPresenceSnapshot _cached = new(false, false, []);

    public DiscreteGpuPresenceDetector(
        Func<DiscreteGpuHardwareIdentity?> identityProvider)
    {
        _identityProvider = identityProvider;
    }

    public DiscreteGpuPresenceSnapshot Capture(bool force = false)
    {
        if (!force &&
            DateTimeOffset.UtcNow - _lastCapture < TimeSpan.FromMilliseconds(500))
        {
            return _cached;
        }

        _lastCapture = DateTimeOffset.UtcNow;
        try
        {
            var devices = EnumeratePresentDisplayDevices();
            var matches = new List<string>();
            var identity = _identityProvider();
            foreach (var device in devices)
            {
                if (identity is { IsEmpty: false } value
                    ? value.Matches(device)
                    : device.Contains(
                        "VEN_10DE",
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(device);
                }
            }

            _cached = new(true, matches.Count > 0, matches);
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "PnP dGPU presence check failed: " + ex.Message);
            _cached = new(false, _cached.IsPresent, _cached.MatchingDeviceIds);
        }

        return _cached;
    }

    private static IReadOnlyList<string> EnumeratePresentDisplayDevices()
    {
        var classGuid = DisplayClassGuid;
        var handle = Native.SetupDiGetClassDevs(
            ref classGuid,
            null,
            IntPtr.Zero,
            Native.DigcfPresent);
        if (handle == Native.InvalidHandleValue)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "SetupDiGetClassDevs failed for display adapters.");
        }

        try
        {
            var result = new List<string>();
            for (uint index = 0; ; index++)
            {
                var data = new Native.SpDevInfoData
                {
                    Size = Marshal.SizeOf<Native.SpDevInfoData>()
                };
                if (!Native.SetupDiEnumDeviceInfo(handle, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == Native.ErrorNoMoreItems)
                        break;
                    throw new Win32Exception(
                        error,
                        "SetupDiEnumDeviceInfo failed for display adapters.");
                }

                if (Native.CMGetDevNodeStatus(
                        out var status,
                        out _,
                        data.DeviceInstance,
                        0) != 0 ||
                    (status & Native.DnHasProblem) != 0)
                {
                    continue;
                }

                _ = Native.SetupDiGetDeviceInstanceId(
                    handle,
                    ref data,
                    null,
                    0,
                    out var required);
                if (required <= 1)
                    continue;
                var instanceId = new StringBuilder(required);
                if (!Native.SetupDiGetDeviceInstanceId(
                        handle,
                        ref data,
                        instanceId,
                        instanceId.Capacity,
                        out _))
                {
                    continue;
                }
                result.Add(instanceId.ToString());
            }

            return result;
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(handle);
        }
    }

    private static class Native
    {
        public static readonly IntPtr InvalidHandleValue = new(-1);
        public const uint DigcfPresent = 0x00000002;
        public const int ErrorNoMoreItems = 259;
        public const uint DnHasProblem = 0x00000400;

        [StructLayout(LayoutKind.Sequential)]
        public struct SpDevInfoData
        {
            public int Size;
            public Guid ClassGuid;
            public uint DeviceInstance;
            public UIntPtr Reserved;
        }

        [DllImport(
            "setupapi.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            string? enumerator,
            IntPtr parent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet,
            uint memberIndex,
            ref SpDevInfoData deviceInfoData);

        [DllImport(
            "setupapi.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet,
            ref SpDevInfoData deviceInfoData,
            StringBuilder? deviceInstanceId,
            int deviceInstanceIdSize,
            out int requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(
            IntPtr deviceInfoSet);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
        public static extern uint CMGetDevNodeStatus(
            out uint status,
            out uint problemNumber,
            uint deviceInstance,
            uint flags);
    }
}
