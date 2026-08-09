using System;
using System.Collections.Generic;
using System.Linq;

namespace ThinkBookToolkit;

public static class OverviewCardIds
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Battery = "battery";
    public const string MemoryStorage = "memory-storage";
    public const string Fans = "fans";
    public const string Power = "power";
    public const string Warranty = "warranty";
}

public sealed class OverviewCardSettings
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, bool> Items { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OverviewLayoutSettings
{
    public Dictionary<string, OverviewCardSettings> Cards { get; set; } =
        OverviewLayoutDefaults.CreateCards();
}

internal static class OverviewLayoutDefaults
{
    private static readonly IReadOnlyDictionary<string, string[]> Definitions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [OverviewCardIds.Cpu] = ["utilization", "average-frequency", "maximum-frequency", "temperature", "power"],
            [OverviewCardIds.Gpu] = ["utilization", "vram-utilization", "core-frequency", "vram-frequency", "core-temperature", "hotspot-temperature", "vram-temperature", "power"],
            [OverviewCardIds.Battery] = ["status", "charge", "health", "power"],
            [OverviewCardIds.MemoryStorage] = ["physical-memory", "virtual-memory", "slot1-temperature", "slot2-temperature", "disk-temperatures", "disk-health", "utilization", "average-temperature"],
            [OverviewCardIds.Fans] = ["fan1-speed", "fan2-speed", "fan1-target", "fan2-target"],
            [OverviewCardIds.Power] = ["cpu-pl1", "cpu-pl2", "cpu-temperature", "turbo-time", "gpu-boost", "gpu-tgp", "gpu-temperature", "gpu-to-cpu", "atpp"],
            [OverviewCardIds.Warranty] = ["status", "start-date", "end-date", "remaining-days", "progress"]
        };

    public static Dictionary<string, OverviewCardSettings> CreateCards() =>
        Definitions.ToDictionary(
            pair => pair.Key,
            pair => new OverviewCardSettings
            {
                Enabled = true,
                Items = pair.Value.ToDictionary(
                    item => item,
                    _ => true,
                    StringComparer.OrdinalIgnoreCase)
            },
            StringComparer.OrdinalIgnoreCase);

    public static OverviewLayoutSettings Normalize(
        OverviewLayoutSettings? value)
    {
        var cards = CreateCards();
        if (value?.Cards is not null)
        {
            foreach (var definition in Definitions)
            {
                if (!value.Cards.TryGetValue(definition.Key, out var source) ||
                    source is null)
                {
                    continue;
                }

                var target = cards[definition.Key];
                target.Enabled = source.Enabled;
                foreach (var item in definition.Value)
                {
                    if (source.Items?.TryGetValue(item, out var enabled) == true)
                        target.Items[item] = enabled;
                }
                if (definition.Value.Length > 0 &&
                    target.Items.Values.All(enabled => !enabled))
                {
                    target.Enabled = false;
                }
            }
        }
        return new OverviewLayoutSettings { Cards = cards };
    }

    public static OverviewLayoutSettings Clone(OverviewLayoutSettings value) =>
        Normalize(value);

    public static bool IsCardEnabled(
        OverviewLayoutSettings? value,
        string cardId)
    {
        var normalized = Normalize(value);
        return normalized.Cards.TryGetValue(cardId, out var card) &&
               card.Enabled;
    }

    public static bool IsItemEnabled(
        OverviewLayoutSettings? value,
        string cardId,
        string itemId)
    {
        var normalized = Normalize(value);
        return normalized.Cards.TryGetValue(cardId, out var card) &&
               card.Enabled &&
               card.Items.TryGetValue(itemId, out var enabled) &&
               enabled;
    }

    public static IReadOnlyDictionary<string, string[]> CardDefinitions =>
        Definitions;

    public static IReadOnlyDictionary<string, string[]>
        DetailedCardDefinitions { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [OverviewCardIds.Cpu] = ["utilization", "average-frequency", "maximum-frequency", "temperature", "power"],
            [OverviewCardIds.Gpu] = ["utilization", "vram-utilization", "core-frequency", "vram-frequency", "core-temperature", "hotspot-temperature", "vram-temperature", "power"],
            [OverviewCardIds.Battery] = ["status", "charge", "health", "power"],
            [OverviewCardIds.MemoryStorage] = ["physical-memory", "virtual-memory", "slot1-temperature", "slot2-temperature", "disk-temperatures", "disk-health"],
            [OverviewCardIds.Fans] = ["fan1-speed", "fan2-speed", "fan1-target", "fan2-target"],
            [OverviewCardIds.Power] = ["cpu-pl1", "cpu-pl2", "cpu-temperature", "turbo-time", "gpu-boost", "gpu-tgp", "gpu-temperature", "gpu-to-cpu", "atpp"],
            [OverviewCardIds.Warranty] = ["status", "start-date", "end-date", "remaining-days", "progress"]
        };

    public static IReadOnlyDictionary<string, string[]>
        CompactCardDefinitions { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [OverviewCardIds.Cpu] = ["temperature", "power"],
            [OverviewCardIds.Gpu] = ["core-temperature", "power"],
            [OverviewCardIds.Battery] = ["charge", "health", "power"],
            [OverviewCardIds.MemoryStorage] = ["utilization", "average-temperature"],
            [OverviewCardIds.Fans] = ["fan1-speed", "fan2-speed"],
            [OverviewCardIds.Warranty] = ["status", "remaining-days"]
        };

    public static bool AnyItemEnabled(
        OverviewLayoutSettings? value,
        string cardId,
        params string[] itemIds) =>
        IsCardEnabled(value, cardId) &&
        itemIds.Any(itemId => IsItemEnabled(value, cardId, itemId));
}
