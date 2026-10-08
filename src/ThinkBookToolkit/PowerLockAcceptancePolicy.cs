using System;
using System.Linq;

namespace ThinkBookToolkit;

internal static class PowerLockAcceptancePolicy
{
    internal static PowerSettingsState? Effective(PowerModeLockSettings? profile)
    {
        if (profile?.Target is not { } target) return null;
        if (profile.AcceptedTarget is not { } accepted) return target;
        return PowerSettingsController.ApplyLockedValues(target, accepted, profile.Locks);
    }

    internal static bool Accept(PowerModeLockSettings profile, PowerSettingsState confirmed, PowerSettingsLockSelection selection)
    {
        if (profile.Target is not { } target || Enum.GetValues<PowerSetting>().Where(selection.IsLocked)
            .Any(s => !confirmed.IsAvailable(s) || !PowerSettingsController.Value(confirmed, s).HasValue)) return false;
        var accepted = PowerSettingsController.ApplyLockedValues(profile.AcceptedTarget ?? target, confirmed, selection);
        if (!PowerSettingsController.IsValidState(accepted)) return false;
        profile.AcceptedTarget = accepted;
        return true;
    }

    internal static void Clear(AppSettings settings)
    {
        foreach (var profile in settings.PowerSettingsLocksByMode.Values.Concat(settings.NvApiPowerSettingsLocksByMode.Values))
            profile.AcceptedTarget = null;
    }
}
