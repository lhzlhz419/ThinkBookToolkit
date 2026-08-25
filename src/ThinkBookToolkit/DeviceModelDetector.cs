using System;
using System.Collections.Generic;
using System.Linq;

namespace ThinkBookToolkit;

internal static class DeviceModelDetector
{
    public const string ThinkBook16pG6Iax = "ThinkBook 16p G6 IAX";
    public const string ThinkBook16pG5Irx = "ThinkBook 16p G5 IRX";
    public const string ThinkBook16pG6Adr = "ThinkBook 16p G6 ADR";
    public const string ThinkBook14G6PlusImh = "ThinkBook 14 G6+ IMH";
    public const string ThinkBook16G8PlusIph = "ThinkBook 16 G8+ IPH";
    public static IReadOnlyList<string> SingleFanModels { get; } =
    [
        ThinkBook16G8PlusIph
    ];
    private static readonly Lazy<DeviceIdentity> Identity = new(
        DeviceInformationService.ReadIdentity);

    public static DeviceIdentity CurrentIdentity => Identity.Value;

    public static bool IsThinkBook16pG6Iax()
    {
        return IsModel(ThinkBook16pG6Iax);
    }

    public static bool IsThinkBook16pG5Irx() => IsModel(ThinkBook16pG5Irx);

    public static bool IsThinkBook14G6PlusImh() => IsModel(ThinkBook14G6PlusImh);

    public static bool HasSecondFan() => HasSecondFan(Identity.Value.Model);

    internal static bool HasSecondFan(string model) =>
        !SingleFanModels.Any(singleFanModel =>
            ModelMatches(model, singleFanModel));

    public static bool UsesAlternativeFullSpeedByDefault() =>
        UsesAlternativeFullSpeedByDefault(Identity.Value.Model);

    internal static bool UsesAlternativeFullSpeedByDefault(string model) =>
        ModelMatches(model, ThinkBook16pG6Adr) ||
        ModelMatches(model, ThinkBook14G6PlusImh);

    internal static bool ModelMatches(string actual, string expected)
    {
        var model = Normalize(actual);
        var target = Normalize(expected);
        return string.Equals(model, target, StringComparison.OrdinalIgnoreCase) ||
               model.Contains(target, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModel(string expected) =>
        ModelMatches(Identity.Value.Model, expected);

    private static string Normalize(string value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Split(
                    [' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim()));
}
