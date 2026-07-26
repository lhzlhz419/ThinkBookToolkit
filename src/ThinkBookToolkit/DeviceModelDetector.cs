using System;
using System.Linq;

namespace ThinkBookToolkit;

internal static class DeviceModelDetector
{
    private const string SupportedPowerModel = "ThinkBook 16p G6 IAX";
    private static readonly Lazy<DeviceIdentity> Identity = new(
        DeviceInformationService.ReadIdentity);

    public static DeviceIdentity CurrentIdentity => Identity.Value;

    public static bool IsThinkBook16pG6Iax()
    {
        var model = Normalize(Identity.Value.Model);
        return string.Equals(
                   model,
                   Normalize(SupportedPowerModel),
                   StringComparison.OrdinalIgnoreCase) ||
               model.Contains(
                   Normalize(SupportedPowerModel),
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
