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

public static class OverviewHeroIds
{
    public const string PerformanceMode = "performance-mode";
    public const string GpuMode = "gpu-mode";
    public const string FanControl = "fan-control";
    public const string DiscreteGpuStatus = "discrete-gpu-status";
    public const string RestartStatus = "restart-status";
}

public sealed class OverviewCardSettings
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, bool> Items { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OverviewLayoutSettings
{
    public Dictionary<string, bool> HeroCards { get; set; } =
        OverviewLayoutDefaults.CreateHeroCards();
    public Dictionary<string, OverviewCardSettings> Cards { get; set; } =
        OverviewLayoutDefaults.CreateCards();
}

internal static class OverviewLayoutDefaults
{
    private static readonly string[] HeroDefinitions =
    [
        OverviewHeroIds.PerformanceMode,
        OverviewHeroIds.GpuMode,
        OverviewHeroIds.FanControl,
        OverviewHeroIds.DiscreteGpuStatus,
        OverviewHeroIds.RestartStatus
    ];

    private static readonly IReadOnlyDictionary<string, string[]> Definitions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [OverviewCardIds.Cpu] = ["utilization", "average-frequency", "performance-core-average-frequency", "efficiency-core-average-frequency", "maximum-frequency", "temperature", "power"],
            [OverviewCardIds.Gpu] = ["utilization", "vram-utilization", "core-frequency", "vram-frequency", "core-temperature", "hotspot-temperature", "vram-temperature", "power"],
            [OverviewCardIds.Battery] = ["status", "charge", "capacity", "health", "power"],
            [OverviewCardIds.MemoryStorage] = ["physical-memory", "virtual-memory", "slot1-temperature", "slot2-temperature", "disk-temperatures", "disk-health", "utilization", "average-temperature"],
            [OverviewCardIds.Fans] = ["fan1-speed", "fan2-speed", "fan1-target", "fan2-target"],
            [OverviewCardIds.Power] = ["cpu-pl1", "cpu-pl2", "cpu-temperature", "turbo-time", "gpu-boost", "gpu-tgp", "gpu-temperature", "gpu-to-cpu", "atpp", "nv-target-tpp", "nv-default-gpu", "nv-min-gpu", "nv-max-gpu", "nv-gpu-temperature", "nv-dynamic-boost"],
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

    public static Dictionary<string, bool> CreateHeroCards() =>
        HeroDefinitions.ToDictionary(
            item => item,
            _ => true,
            StringComparer.OrdinalIgnoreCase);

    public static OverviewLayoutSettings Normalize(
        OverviewLayoutSettings? value)
    {
        var heroCards = CreateHeroCards();
        if (value?.HeroCards is not null)
        {
            foreach (var item in HeroDefinitions)
            {
                if (value.HeroCards.TryGetValue(item, out var enabled))
                    heroCards[item] = enabled;
            }
        }
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
        return new OverviewLayoutSettings
        {
            HeroCards = heroCards,
            Cards = cards
        };
    }

    public static OverviewLayoutSettings Clone(OverviewLayoutSettings value) =>
        Normalize(value);

    public static bool IsCardEnabled(
        OverviewLayoutSettings? value,
        string cardId)
    {
        if (!Definitions.TryGetValue(cardId, out var items)) return false;
        if (value?.Cards?.TryGetValue(cardId, out var card) != true || card is null)
            return true;
        return card.Enabled && items.Any(item =>
            card.Items?.TryGetValue(item, out var enabled) != true || enabled);
    }

    public static bool IsHeroCardEnabled(
        OverviewLayoutSettings? value,
        string cardId)
    {
        return HeroDefinitions.Contains(cardId, StringComparer.OrdinalIgnoreCase) &&
            (value?.HeroCards?.TryGetValue(cardId, out var enabled) != true || enabled);
    }

    public static bool IsItemEnabled(
        OverviewLayoutSettings? value,
        string cardId,
        string itemId)
    {
        if (!Definitions.TryGetValue(cardId, out var items) ||
            !items.Contains(itemId, StringComparer.OrdinalIgnoreCase)) return false;
        if (value?.Cards?.TryGetValue(cardId, out var card) != true || card is null)
            return true;
        return card.Enabled &&
            (card.Items?.TryGetValue(itemId, out var enabled) != true || enabled);
    }

    public static IReadOnlyDictionary<string, string[]> CardDefinitions =>
        Definitions;

    public static IReadOnlyList<string> HeroCardDefinitions =>
        HeroDefinitions;

    public static IReadOnlyDictionary<string, string[]>
        DetailedCardDefinitions { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [OverviewCardIds.Cpu] = ["utilization", "average-frequency", "performance-core-average-frequency", "efficiency-core-average-frequency", "maximum-frequency", "temperature", "power"],
            [OverviewCardIds.Gpu] = ["utilization", "vram-utilization", "core-frequency", "vram-frequency", "core-temperature", "hotspot-temperature", "vram-temperature", "power"],
            [OverviewCardIds.Battery] = ["status", "charge", "capacity", "health", "power"],
            [OverviewCardIds.MemoryStorage] = ["physical-memory", "virtual-memory", "slot1-temperature", "slot2-temperature", "disk-temperatures", "disk-health"],
            [OverviewCardIds.Fans] = ["fan1-speed", "fan2-speed", "fan1-target", "fan2-target"],
            [OverviewCardIds.Power] = ["cpu-pl1", "cpu-pl2", "cpu-temperature", "turbo-time", "gpu-boost", "gpu-tgp", "gpu-temperature", "gpu-to-cpu", "atpp", "nv-target-tpp", "nv-default-gpu", "nv-min-gpu", "nv-max-gpu", "nv-gpu-temperature", "nv-dynamic-boost"],
            [OverviewCardIds.Warranty] = ["status", "start-date", "end-date", "remaining-days", "progress"]
        };

    public static IReadOnlyDictionary<string, string[]>
        CompactCardDefinitions { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [OverviewCardIds.Cpu] = ["temperature", "power"],
            [OverviewCardIds.Gpu] = ["core-temperature", "power"],
            [OverviewCardIds.Battery] = ["charge", "capacity", "health", "power"],
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
