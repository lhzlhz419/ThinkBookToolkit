using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit.Tests;

// Never talks to hardware. This assembly is only copied into the test fixture directory.
public class FakeFanBackend : IFanBackend, IFanBackendCapabilityProbe
{
    private int _first = 1234, _second = 2345;
    public virtual Version ApiVersion => FanBackendContract.CurrentVersion;
    public string Name => "Plugin test backend";
    public string Transport => "Memory only";
    public FanBackendStartupNotice? StartupNotice => null;
    public bool SupportsDisableControlOnSleep => true;
    public TimeSpan MinimumReadInterval => TimeSpan.FromMilliseconds(100);
    public TimeSpan MinimumWriteInterval => TimeSpan.FromMilliseconds(200);
    public FanBackendControlSemantics ControlSemantics => new(FanTargetZeroBehavior.StopFanWhileKeepingManualControl,
        FanAutomaticControlRestoreMechanism.DedicatedBackendOperation, "RestoreAuto",
        new(FanFullSpeedControlMechanism.DedicatedBackendOperation, "SetFullSpeed(true)", "SetFullSpeed(false)"));
    public FanBackendSnapshot ReadSnapshot() => new(DateTimeOffset.UtcNow, _first,
        FanBackendRuntimeContext.DeclaredFanCount == 1 ? 0 : _second,
        new Dictionary<string, FanBackendRange> { ["fan1"] = new("fan1", 1, 0, 6000), ["fan2"] = new("fan2", 2, 0, 6000) });
    public void Apply(int fan1Rpm, int fan2Rpm) { _first = fan1Rpm; _second = fan2Rpm; }
    public void RestoreAuto() { _first = 1234; _second = 2345; }
    public void SetFullSpeed(bool enabled) { if (enabled) Apply(6000, 6000); }
    public bool TryProbeFullSpeedControl(out string detail) { detail = "memory probe"; return true; }
}
public sealed class IncompatibleFanBackend : FakeFanBackend
{
    public override Version ApiVersion => new(99, 0);
}
