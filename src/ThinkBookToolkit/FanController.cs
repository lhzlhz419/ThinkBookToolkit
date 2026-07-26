using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
        _backend = LoadBackend();
    }

    public string BackendName => _backend.Name;

    public string Transport => _backend.Transport;

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

    public static int ClampForBoth(
        int rpm,
        IReadOnlyDictionary<string, FanLimit> limits)
    {
        var (minimum, maximum) = SharedRange(limits);
        return Math.Max(minimum, Math.Min(maximum, rpm));
    }

    public static (int MinRpm, int MaxRpm) SharedRange(
        IReadOnlyDictionary<string, FanLimit> limits) =>
        (
            Math.Max(limits["fan1"].MinRpm, limits["fan2"].MinRpm),
            Math.Min(limits["fan1"].MaxRpm, limits["fan2"].MaxRpm)
        );

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
}
