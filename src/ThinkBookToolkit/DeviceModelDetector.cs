using System;
using System.Linq;

namespace ThinkBookToolkit;

internal static class DeviceModelDetector
{
    private const string G6IaxModel = "ThinkBook 16p G6 IAX";
    private const string G5IrxModel = "ThinkBook 16p G5 IRX";
    private const string G5ArxModel = "ThinkBook 16p G5 ARX";
    private static readonly Lazy<DeviceIdentity> Identity = new(
        DeviceInformationService.ReadIdentity);
    private static DeviceIdentity? _identityOverrideForTesting;

    public static DeviceIdentity CurrentIdentity =>
        _identityOverrideForTesting ?? Identity.Value;

    public static bool IsThinkBook16pG6Iax() => IsModel(G6IaxModel);

    public static bool IsThinkBook16pG5Irx() => IsModel(G5IrxModel);

    public static bool IsThinkBook16pG5Arx() => IsModel(G5ArxModel);

    public static bool IsThinkBook16pG5() =>
        IsThinkBook16pG5Irx() || IsThinkBook16pG5Arx();

    public static bool IsPowerSettingsWritable() =>
        IsThinkBook16pG6Iax() || IsThinkBook16pG5Irx();

    internal static void SetIdentityForTesting(DeviceIdentity? identity) =>
        _identityOverrideForTesting = identity;

    private static bool IsModel(string token)
    {
        var model = Normalize(CurrentIdentity.Model);
        var normalizedToken = Normalize(token);
        return string.Equals(
                   model,
                   normalizedToken,
                   StringComparison.OrdinalIgnoreCase) ||
               model.Contains(
                   normalizedToken,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Split(
                    [' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim()));
}
