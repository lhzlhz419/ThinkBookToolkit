using System.Collections.Generic;

namespace ThinkBookToolkit.FanBackend;

public static class FanBackendContract
{
    public static Version CurrentVersion { get; } = new(1, 0);
}

public sealed record FanBackendRange(string Fan, uint Id, int MinRpm, int MaxRpm);

public sealed record FanBackendSnapshot(
    DateTimeOffset Timestamp,
    int Fan1Rpm,
    int Fan2Rpm,
    IReadOnlyDictionary<string, FanBackendRange> Limits);

public enum FanTargetZeroBehavior
{
    ReleaseFanToFirmwareControl,
    StopFanWhileKeepingManualControl
}

public enum FanAutomaticControlRestoreMechanism
{
    WriteZeroToBothTargets,
    DedicatedBackendOperation
}

public enum FanFullSpeedControlMechanism
{
    FeatureToggle,
    DedicatedBackendOperation
}

public sealed record FanFullSpeedControlSemantics(
    FanFullSpeedControlMechanism Mechanism,
    string EnableOperation,
    string DisableOperation);

public sealed record FanBackendControlSemantics(
    FanTargetZeroBehavior ZeroRpmBehavior,
    FanAutomaticControlRestoreMechanism RestoreAutomaticControlMechanism,
    string RestoreAutomaticControlOperation,
    FanFullSpeedControlSemantics FullSpeed);

/// <summary>
/// Stable contract implemented by the replaceable fan backend DLL.
/// </summary>
public interface IFanBackend
{
    /// <summary>
    /// Version of the IFanBackend contract implemented by this DLL.
    /// </summary>
    Version ApiVersion { get; }

    string Name { get; }

    string Transport { get; }

    /// <summary>
    /// Declares whether this backend should expose the option that releases fan
    /// control before sleep and restores it after resume.
    /// </summary>
    bool SupportsDisableControlOnSleep { get; }

    /// <summary>
    /// Minimum interval between ordinary fan telemetry reads.
    /// </summary>
    TimeSpan MinimumReadInterval { get; }

    /// <summary>
    /// Minimum interval between ordinary target-write batches. Apply writes
    /// both fan targets as one batch, so no delay may be inserted between its
    /// FAN1 and FAN2 operations. RestoreAuto and SetFullSpeed are explicit
    /// control operations and are not constrained by this interval.
    /// </summary>
    TimeSpan MinimumWriteInterval { get; }

    /// <summary>
    /// Declares what a target value of 0 means, which backend operation
    /// RestoreAuto uses, and how full-speed mode is enabled and disabled.
    /// Callers must not infer automatic control from 0.
    /// </summary>
    FanBackendControlSemantics ControlSemantics { get; }

    FanBackendSnapshot ReadSnapshot();

    /// <summary>
    /// Writes both fan targets. A target of 0 must follow ControlSemantics;
    /// it must not be treated as a universal alias for RestoreAuto.
    /// </summary>
    void Apply(int fan1Rpm, int fan2Rpm);

    /// <summary>
    /// Restores firmware-automatic control using the backend-specific
    /// operation declared by ControlSemantics.
    /// </summary>
    void RestoreAuto();

    /// <summary>
    /// Enables or disables full speed with the backend-specific operations
    /// declared by ControlSemantics.FullSpeed.
    /// </summary>
    void SetFullSpeed(bool enabled);
}
