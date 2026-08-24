using System;
using System.Collections.Generic;
using System.Linq;

namespace ThinkBookToolkit;

internal static class PerformanceModeCycle
{
    internal static IReadOnlyList<ItsMode> DefaultOrder { get; } =
    [
        ItsMode.PowerSaving,
        ItsMode.Intelligent,
        ItsMode.Performance,
        ItsMode.Geek
    ];

    internal static List<ItsMode> NormalizeOrder(
        IEnumerable<ItsMode>? order)
    {
        var normalized = (order ?? [])
            .Where(IsSelectableMode)
            .Distinct()
            .ToList();
        foreach (var mode in DefaultOrder)
        {
            if (!normalized.Contains(mode))
                normalized.Add(mode);
        }
        return normalized;
    }

    internal static List<ItsMode> NormalizeEnabled(
        IEnumerable<ItsMode>? enabled)
    {
        var normalized = (enabled ?? DefaultOrder)
            .Where(IsSelectableMode)
            .Distinct()
            .ToList();
        if (normalized.Count == 0)
            normalized.Add(DefaultOrder[0]);
        return normalized;
    }

    internal static ItsMode Next(
        IEnumerable<ItsMode>? order,
        IEnumerable<ItsMode>? enabled,
        ItsMode current,
        bool isAcConnected,
        Func<ItsMode, bool>? isSupported = null)
    {
        var enabledSet = NormalizeEnabled(enabled).ToHashSet();
        var available = NormalizeOrder(order)
            .Where(enabledSet.Contains)
            .Where(mode => isAcConnected || mode != ItsMode.Geek)
            .Where(mode => isSupported?.Invoke(mode) != false)
            .ToArray();
        if (available.Length == 0)
            return ItsMode.Unknown;
        var currentIndex = Array.IndexOf(available, current);
        return currentIndex < 0
            ? available[0]
            : available[(currentIndex + 1) % available.Length];
    }

    internal static bool TryParseSelectableMode(
        string? value,
        out ItsMode mode)
    {
        if (Enum.TryParse(value, ignoreCase: true, out mode) &&
            IsSelectableMode(mode))
        {
            return true;
        }
        mode = ItsMode.Unknown;
        return false;
    }

    internal static bool IsSelectableMode(ItsMode mode) =>
        mode is ItsMode.PowerSaving or ItsMode.Intelligent or
            ItsMode.Performance or ItsMode.Geek;
}
