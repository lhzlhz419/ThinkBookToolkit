using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;

namespace ThinkBookToolkit;

public enum KeyboardMacroDirection
{
    Down,
    Up
}

public sealed record KeyboardMacroEvent
{
    public int VirtualKey { get; set; }
    public KeyboardMacroDirection Direction { get; set; }
    public int DelayMilliseconds { get; set; }
}

public sealed record KeyboardMacroDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = string.Empty;
    public int? TriggerVirtualKey { get; set; }
    public List<KeyboardMacroEvent> Events { get; set; } = [];
}

internal static class KeyboardMacroDefaults
{
    public const int MaximumDelayMilliseconds = 600000;

    public static List<KeyboardMacroDefinition> Normalize(
        IEnumerable<KeyboardMacroDefinition>? macros)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var triggers = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<KeyboardMacroDefinition>();
        foreach (var macro in macros ?? [])
        {
            var id = Guid.TryParse(macro.Id, out var parsed)
                ? parsed.ToString("D")
                : Guid.NewGuid().ToString("D");
            if (!ids.Add(id))
                id = Guid.NewGuid().ToString("D");
            int? trigger = IsValidVirtualKey(macro.TriggerVirtualKey)
                ? macro.TriggerVirtualKey
                : null;
            if (trigger.HasValue && !triggers.Add(trigger.Value))
                trigger = null;
            var requestedName = string.IsNullOrWhiteSpace(macro.Name)
                ? "Macro"
                : macro.Name.Trim();
            var name = UniqueDefinitionNames.Create(requestedName, names);
            names.Add(name);
            result.Add(new KeyboardMacroDefinition
            {
                Id = id,
                Name = name,
                TriggerVirtualKey = trigger,
                Events = (macro.Events ?? [])
                    .Where(item => IsValidVirtualKey(item.VirtualKey) &&
                                   Enum.IsDefined(item.Direction))
                    .Select(item => item with
                    {
                        DelayMilliseconds = Math.Clamp(
                            item.DelayMilliseconds,
                            0,
                            MaximumDelayMilliseconds)
                    })
                    .ToList()
            });
        }
        return result;
    }

    public static bool IsValidVirtualKey(int? value) =>
        value is >= 1 and <= 0xFE;
}

internal static class KeyboardMacroKeyNames
{
    public static string Format(int virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        return key == Key.None
            ? $"0x{virtualKey:X2}"
            : $"{key} (0x{virtualKey:X2})";
    }

    public static bool TryParse(string? text, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var value = text.Trim();
        var parenthesis = value.IndexOf('(');
        if (parenthesis > 0)
            value = value[..parenthesis].Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(
                value[2..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out virtualKey))
        {
            return KeyboardMacroDefaults.IsValidVirtualKey(virtualKey);
        }
        if (int.TryParse(value, out virtualKey))
            return KeyboardMacroDefaults.IsValidVirtualKey(virtualKey);
        if (!Enum.TryParse<Key>(value, true, out var key) || key == Key.None)
            return false;
        virtualKey = KeyInterop.VirtualKeyFromKey(key);
        return KeyboardMacroDefaults.IsValidVirtualKey(virtualKey);
    }
}
