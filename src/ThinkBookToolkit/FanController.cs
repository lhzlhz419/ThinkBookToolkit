using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit;

/// <summary>
/// Application-side facade for the replaceable fan backend. No fan transport
/// implementation belongs in the executable project.
/// </summary>
public sealed class FanController
{
    public const string BackendFileName = "ThinkBookToolkit.FanBackend.dll";
    private readonly IFanBackend _backend;

    public FanController()
    {
        FanBackendRuntimeContext.DeclaredFanCount =
            DeviceModelDetector.HasSecondFan() ? 2 : 1;
        _backend = LoadBackend();
        BackendIdentity = ComputeBackendIdentity(_backend);
    }

    public string BackendName => _backend.Name;

    public string Transport => _backend.Transport;

    public string BackendIdentity { get; }

    public FanBackendStartupNotice? StartupNotice =>
        _backend.StartupNotice;

    public bool SupportsDisableControlOnSleep =>
        _backend.SupportsDisableControlOnSleep;

    public TimeSpan MinimumReadInterval => _backend.MinimumReadInterval;

    public TimeSpan MinimumWriteInterval => _backend.MinimumWriteInterval;

    public FanBackendControlSemantics ControlSemantics =>
        _backend.ControlSemantics;

    public FanSnapshot ReadSnapshot()
    {
        var snapshot = _backend.ReadSnapshot();
        var limits = snapshot.Limits.ToDictionary(
            pair => pair.Key,
            pair => new FanLimit(
                pair.Value.Fan,
                pair.Value.Id,
                pair.Value.MinRpm,
                pair.Value.MaxRpm));
        return new FanSnapshot(
            snapshot.Timestamp,
            snapshot.Fan1Rpm,
            snapshot.Fan2Rpm,
            limits);
    }

    public void ApplyBoth(int rpm) => Apply(rpm, rpm);

    public void Apply(int fan1Rpm, int fan2Rpm) =>
        _backend.Apply(fan1Rpm, fan2Rpm);

    public void RestoreAuto() => _backend.RestoreAuto();

    public void SetFullSpeed(bool enabled) => _backend.SetFullSpeed(enabled);

    public bool TryProbeFullSpeedControl(out string detail) =>
        ProbeFullSpeedControl(_backend, out detail);

    internal static bool ProbeFullSpeedControl(
        IFanBackend backend,
        out string detail)
    {
        if (backend is IFanBackendCapabilityProbe probe)
            return probe.TryProbeFullSpeedControl(out detail);

        detail = "The fan backend declares full-speed enable and disable " +
                 "operations but does not provide a non-mutating capability probe.";
        return true;
    }

    public static int ClampForBoth(
        int rpm,
        IReadOnlyDictionary<string, FanLimit> limits)
    {
        var (minimum, maximum) = SharedRange(limits);
        return Math.Max(minimum, Math.Min(maximum, rpm));
    }

    public static (int MinRpm, int MaxRpm) SharedRange(
        IReadOnlyDictionary<string, FanLimit> limits)
    {
        var fan1 = limits["fan1"];
        if (!limits.TryGetValue("fan2", out var fan2))
            return (fan1.MinRpm, fan1.MaxRpm);
        return (
            Math.Max(fan1.MinRpm, fan2.MinRpm),
            Math.Min(fan1.MaxRpm, fan2.MaxRpm));
    }

    private static IFanBackend LoadBackend()
    {
        var path = Path.Combine(AppContext.BaseDirectory, BackendFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Fan backend was not found: {path}",
                path);
        }

        var assembly = Assembly.LoadFrom(path);
        var backendType = assembly
            .GetTypes()
            .FirstOrDefault(type =>
                !type.IsAbstract &&
                !type.IsInterface &&
                typeof(IFanBackend).IsAssignableFrom(type));
        if (backendType is null)
        {
            throw new InvalidOperationException(
                $"{BackendFileName} does not contain an IFanBackend implementation.");
        }

        return CreateBackendInstance(backendType);
    }

    internal static IFanBackend CreateBackendInstance(Type backendType)
    {
        try
        {
            var backend = (IFanBackend)(Activator.CreateInstance(backendType)
                ?? throw new InvalidOperationException(
                    $"Unable to create fan backend {backendType.FullName}."));
            if (backend.ApiVersion != FanBackendContract.CurrentVersion)
            {
                throw new NotSupportedException(
                    $"Fan backend API version {backend.ApiVersion} is incompatible with required version {FanBackendContract.CurrentVersion}.");
            }

            return backend;
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static string ComputeBackendIdentity(IFanBackend backend)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            BackendFileName);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            var module = backend.GetType().Module;
            return string.Join(
                "|",
                backend.GetType().Assembly.FullName,
                module.ModuleVersionId.ToString("D"));
        }
    }
}
