using System;
using System.Collections.Generic;
using System.Linq;

namespace ThinkBookToolkit;

public enum AutomationStepKind
{
    PerformanceMode,
    GpuMode,
    GpuOverclockEnabled,
    KillGpuApplications,
    FanFullSpeed,
    FanStrategy,
    FixedRpmGameMode,
    BatteryChargeMode,
    OvernightCharging,
    AlwaysOnUsb,
    FlipToStart,
    RefreshRate,
    VantageEyeCare,
    PcManagerEyeCare,
    ColorManagement,
    DolbyEnabled,
    DolbyProfile,
    SpeakerNoiseCancellation,
    MicrophoneNoiseMode,
    KeyboardBacklight,
    KeyboardBacklightAutoOff,
    FunctionLock,
    CapsLockOsd,
    NumLockOsd,
    FnCtrlSwap,
    Touchpad,
    ShowToolkitWindow,
    MinimizeToolkitWindow,
    ToggleToolkitWindow,
    OpenApplication,
    Delay = 30,
    RunMacro = 31
}

public enum AutomationTriggerKind
{
    AcAdapterConnected,
    AcAdapterDisconnected,
    GameStarted,
    GameStopped
}

public sealed record AutomationStep
{
    public string Id { get; init; } = Guid.NewGuid().ToString("D");
    public AutomationStepKind Kind { get; init; }
    public string Value { get; set; } = string.Empty;
    public string SecondaryValue { get; set; } = string.Empty;
}

public sealed record AutomationDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = string.Empty;
    public List<AutomationStep> Steps { get; set; } = [];
    public List<AutomationTriggerKind> Triggers { get; set; } = [];
}

internal static class FnAutomationKeyIds
{
    private const string CustomPrefix = "fn-custom-";
    public const string FnQ = "fn-q";
    public const string FnSpace = "fn-space";
    public const string FnF4 = "fn-f4";
    public const string FnF8 = "fn-f8";
    public const string FnF10 = "fn-f10";
    public const string FnLock = "fn-lock";
    public const string PrintScreen = "fn-print-screen";
    public const string Touchpad = "fn-touchpad";
    public const string RefreshRate = "fn-r";
    public const string FnF9 = "fn-f9";
    public const string FnN = "fn-n";

    public static IReadOnlyList<string> All { get; } =
    [
        FnQ,
        FnSpace,
        FnF4,
        FnF8,
        FnF10,
        FnLock,
        PrintScreen,
        Touchpad,
        RefreshRate,
        FnF9,
        FnN
    ];

    public static string FromDiscovered(string channel, int code)
    {
        var source = channel.ToUpperInvariant() switch
        {
            "IOCTL" => "ioctl",
            "KEYBOARD" => "keyboard",
            _ => "wmi"
        };
        return $"{CustomPrefix}{source}-{unchecked((uint)code):X8}";
    }

    public static bool IsCustom(string keyId) =>
        TryGetCustomDetails(keyId, out _, out _);

    public static bool TryGetCustomDetails(
        string keyId,
        out string channel,
        out uint code)
    {
        channel = string.Empty;
        code = 0;
        if (!keyId.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var remainder = keyId[CustomPrefix.Length..];
        var separator = remainder.IndexOf('-');
        if (separator <= 0 || separator == remainder.Length - 1)
            return false;
        var source = remainder[..separator];
        if (!source.Equals("wmi", StringComparison.OrdinalIgnoreCase) &&
            !source.Equals("ioctl", StringComparison.OrdinalIgnoreCase) &&
            !source.Equals("keyboard", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!uint.TryParse(
                remainder[(separator + 1)..],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out code))
        {
            return false;
        }
        channel = source.ToUpperInvariant();
        return true;
    }

    public static IReadOnlyList<string> AllForSettings(
        IReadOnlyDictionary<string, string>? customNames) =>
        All.Concat((customNames ?? new Dictionary<string, string>())
                .Keys
                .Where(IsCustom)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

internal static class AutomationSettingsDefaults
{
    public static List<AutomationDefinition> Normalize(
        IEnumerable<AutomationDefinition>? automations)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AutomationDefinition>();
        foreach (var automation in automations ?? [])
        {
            var id = Guid.TryParse(automation.Id, out var parsed)
                ? parsed.ToString("D")
                : Guid.NewGuid().ToString("D");
            if (!ids.Add(id))
                id = Guid.NewGuid().ToString("D");
            var requestedName = string.IsNullOrWhiteSpace(automation.Name)
                ? "Automation"
                : automation.Name.Trim();
            var name = UniqueDefinitionNames.Create(requestedName, names);
            names.Add(name);
            result.Add(new AutomationDefinition
            {
                Id = id,
                Name = name,
                Steps = (automation.Steps ?? [])
                    .Where(step => Enum.IsDefined(step.Kind))
                    .Select(step =>
                    {
                        var kind = NormalizeStepKind(step);
                        return step with
                        {
                            Id = Guid.TryParse(step.Id, out var stepId)
                                ? stepId.ToString("D")
                                : Guid.NewGuid().ToString("D"),
                            Kind = kind
                        };
                    })
                    .ToList(),
                Triggers = (automation.Triggers ?? [])
                    .Where(Enum.IsDefined)
                    .Distinct()
                    .ToList()
            });
        }
        return result;
    }

    internal static AutomationStepKind NormalizeStepKind(
        AutomationStep step)
    {
        // Early macro builds inserted RunMacro before Delay. Since enums are
        // stored as numbers, old delays and newly saved macros can otherwise
        // trade meanings. A delay is numeric; a macro reference is a GUID.
        if (step.Kind == AutomationStepKind.Delay &&
            Guid.TryParse(step.Value, out _))
        {
            return AutomationStepKind.RunMacro;
        }
        if (step.Kind == AutomationStepKind.RunMacro &&
            double.TryParse(
                step.Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds) &&
            seconds is >= 0 and <= 86400)
        {
            return AutomationStepKind.Delay;
        }
        return step.Kind;
    }

    public static Dictionary<string, string> NormalizeFnBindings(
        IReadOnlyDictionary<string, string>? bindings,
        IEnumerable<AutomationDefinition> automations,
        IReadOnlyDictionary<string, string>? customKeyNames = null)
    {
        var automationIds = automations
            .Select(automation => automation.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var allowedKeys = FnAutomationKeyIds
            .AllForSettings(customKeyNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in bindings ??
                 new Dictionary<string, string>())
        {
            if (allowedKeys.Contains(pair.Key) &&
                automationIds.Contains(pair.Value))
            {
                result[pair.Key] = pair.Value;
            }
        }
        return result;
    }

    public static Dictionary<string, string> NormalizeCustomFnKeyNames(
        IReadOnlyDictionary<string, string>? names)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in names ?? new Dictionary<string, string>())
        {
            if (!FnAutomationKeyIds.IsCustom(pair.Key) ||
                string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }
            result[pair.Key] = pair.Value.Trim();
        }
        return result;
    }
}

internal static class UniqueDefinitionNames
{
    public static string Create(
        string baseName,
        IEnumerable<string> existingNames)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(baseName)
            ? "Item"
            : baseName.Trim();
        var existing = existingNames.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(normalizedBase))
            return normalizedBase;
        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = normalizedBase + suffix;
            if (!existing.Contains(candidate))
                return candidate;
        }
        return normalizedBase + Guid.NewGuid().ToString("N");
    }

    public static bool HasDuplicates(IEnumerable<string> names) =>
        names.Select(name => (name ?? string.Empty).Trim())
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1);
}
