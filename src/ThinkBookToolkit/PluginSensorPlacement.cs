namespace ThinkBookToolkit;

/// <summary>Use hardware categories, not the origin of a reading, to choose its display group.</summary>
internal static class PluginSensorPlacement
{
    internal static string? OverviewCard(string? category) => category?.Trim().ToLowerInvariant() switch
    {
        "cpu" => OverviewCardIds.Cpu,
        "gpu" or "vram" => OverviewCardIds.Gpu,
        "battery" => OverviewCardIds.Battery,
        "memory" or "storage" or "memory-storage" => OverviewCardIds.MemoryStorage,
        "fans" => OverviewCardIds.Fans,
        "power" => OverviewCardIds.Power,
        "warranty" => OverviewCardIds.Warranty,
        _ => null
    };

    internal static string? OsdGroup(string? category) => category?.Trim().ToLowerInvariant() switch
    {
        "fps" => "fps",
        "cpu" => "cpu",
        "gpu" => "gpu",
        "vram" => "vram",
        "battery" => "battery",
        "memory" or "memory-storage" => "memory",
        "storage" => "storage",
        "fans" => "fans",
        _ => null
    };

    internal static string? HistoryGroup(string? category) => OsdGroup(category) switch
    {
        "fps" => "FPS",
        "cpu" => "CPU",
        "gpu" or "vram" => "GPU and VRAM",
        "battery" or "memory" or "storage" => "RAM, storage and battery",
        "fans" => "Fans",
        _ => null
    };
}
