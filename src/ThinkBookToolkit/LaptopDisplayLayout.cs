using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using WindowsDisplayAPI.Native.DisplayConfig;

namespace ThinkBookToolkit;

internal readonly record struct PanelResolution(uint Width, uint Height)
{
    public override string ToString() => $"{Width} × {Height}";
}
internal sealed record LaptopDisplayLayout(string DeviceName, PanelResolution Current,
    IReadOnlyList<PanelResolution> Resolutions, int? Scale, IReadOnlyList<int> Scales, string? ScaleError);

internal sealed class ReversibleDisplayChange(Action<bool> finish) : IDisposable
{
    private bool _finished;
    internal void Finish(bool keep) { if (_finished) return; finish(keep); _finished = true; }
    public void Dispose()
    {
        if (_finished) return;
        try { Finish(false); }
        catch (Exception ex) { ToolkitLog.Error("Display rollback failed.", ex); }
    }
}

internal static partial class RefreshRateController
{
    private static string RequireLaptopDisplay(string? expected = null)
    {
        var device = FindInternalDisplayDeviceName(allowFallback: false)
            ?? throw new InvalidOperationException("未能唯一识别活动的笔记本内置屏幕。 / Cannot uniquely identify the active laptop panel.");
        if (expected is not null && device != expected)
            throw new InvalidOperationException("显示器拓扑已改变，请重新读取。 / Display topology changed; reload first.");
        var active = 0;
        for (uint index = 0; ; index++)
        {
            var monitor = CreateDisplayDevice();
            if (!EnumDisplayDevices(device, index, ref monitor, 0)) break;
            if ((monitor.StateFlags & DisplayDeviceActive) != 0) active++;
        }
        if (active > 1) throw new InvalidOperationException("请先退出复制显示模式。 / Leave duplicate-display mode first.");
        var targets = WindowsDisplayAPI.DisplayConfig.PathInfo.GetActivePaths()
            .Where(p => string.Equals(p.DisplaySource.DisplayName, device, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.TargetsInfo).Count();
        if (targets != 1) throw new InvalidOperationException("屏幕路径不唯一或正在切换，请退出复制模式后重试。 / Display path is ambiguous or changing; leave duplicate mode and retry.");
        return device;
    }

    private static DevMode ReadPanelMode(string device)
    {
        var mode = CreateDevMode();
        if (!EnumDisplaySettings(device, CurrentSettings, ref mode))
            throw new InvalidOperationException("无法读取屏幕模式。 / Could not read the display mode.");
        return mode;
    }

    private static List<DevMode> PanelModes(string device, DevMode current)
    {
        var result = new List<DevMode>();
        for (var i = 0; ; i++)
        {
            var mode = CreateDevMode();
            if (!EnumDisplaySettings(device, i, ref mode)) break;
            if (mode.dmBitsPerPel == current.dmBitsPerPel && mode.dmDisplayFlags == current.dmDisplayFlags &&
                mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0 && mode.dmDisplayFrequency > 1) result.Add(mode);
        }
        return result;
    }

    internal static LaptopDisplayLayout ReadLaptopLayout()
    {
        var device = RequireLaptopDisplay();
        var current = ReadPanelMode(device);
        var sizes = PanelModes(device, current).Select(m => new PanelResolution(m.dmPelsWidth, m.dmPelsHeight))
            .Append(new(current.dmPelsWidth, current.dmPelsHeight)).Distinct()
            .OrderByDescending(r => (long)r.Width * r.Height).ThenByDescending(r => r.Width).ToArray();
        int? scale = null;
        int[] scales = [];
        string? error = null;
        try
        {
            var source = FindDisplay(device)?.DisplayScreen.ToPathDisplaySource()
                ?? throw new InvalidOperationException("DPI display source unavailable.");
            scale = (int)source.CurrentDPIScale;
            var maximum = (int)source.MaximumDPIScale;
            scales = Enum.GetValues<DisplayConfigSourceDPIScale>().Select(s => (int)s)
                .Where(s => s >= 100 && s <= maximum).Distinct().Order().ToArray();
        }
        catch (Exception ex) { error = ex.GetBaseException().Message; }
        return new(device, new(current.dmPelsWidth, current.dmPelsHeight), sizes, scale, scales, error);
    }

    internal static uint SelectResolutionFrequency(IEnumerable<uint> frequencies, uint current) => frequencies
        .Distinct().OrderBy(f => Math.Abs((long)f - current)).ThenByDescending(f => f).First();

    internal static ReversibleDisplayChange BeginLaptopResolution(string device, PanelResolution resolution)
    {
        RequireLaptopDisplay(device);
        var original = ReadPanelMode(device);
        var candidates = PanelModes(device, original).Where(m => m.dmPelsWidth == resolution.Width && m.dmPelsHeight == resolution.Height).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("驱动未列出此分辨率。 / The driver does not list this resolution.");
        var proposed = original;
        proposed.dmPelsWidth = resolution.Width; proposed.dmPelsHeight = resolution.Height;
        proposed.dmDisplayFrequency = SelectResolutionFrequency(candidates.Select(c => c.dmDisplayFrequency), original.dmDisplayFrequency);
        proposed.dmFields = 0x00080000 | 0x00100000 | 0x00040000 | DisplayFrequencyField;
        original.dmFields = proposed.dmFields;
        var result = ChangeDisplaySettingsEx(device, ref proposed, IntPtr.Zero, 2, IntPtr.Zero); // CDS_TEST
        if (result != Successful) throw new InvalidOperationException($"Windows rejected the resolution test (code {result}).");
        var change = new ReversibleDisplayChange(keep =>
        {
            RequireLaptopDisplay(device);
            var setting = keep ? proposed : original;
            var code = ChangeDisplaySettingsEx(device, ref setting, IntPtr.Zero, keep ? UpdateRegistry : 0, IntPtr.Zero);
            if (code != Successful) throw new InvalidOperationException($"Windows rejected display {(keep ? "save" : "rollback")} (code {code}).");
        });
        try
        {
            result = ChangeDisplaySettingsEx(device, ref proposed, IntPtr.Zero, 0, IntPtr.Zero);
            if (result != Successful) throw new InvalidOperationException($"Windows rejected the resolution change (code {result}).");
            var actual = ReadPanelMode(device);
            if (actual.dmPelsWidth != resolution.Width || actual.dmPelsHeight != resolution.Height)
                throw new InvalidOperationException("驱动未确认分辨率。 / The driver did not confirm the resolution.");
            return change;
        }
        catch { change.Dispose(); throw; }
    }

    internal static ReversibleDisplayChange BeginLaptopScale(string device, int percent)
    {
        RequireLaptopDisplay(device);
        if (CustomDisplayScaling.ReadPercent() is not null)
            throw new InvalidOperationException("请先关闭系统自定义缩放并注销，再设置单屏缩放。 / Disable custom scaling and sign out before changing per-display scale.");
        var layout = ReadLaptopLayout();
        if (!layout.Scales.Contains(percent) || layout.Scale is not { } original)
            throw new ArgumentOutOfRangeException(nameof(percent), "当前屏幕不支持此缩放档位。 / Unsupported scale for this panel.");
        void Set(int value)
        {
            RequireLaptopDisplay(device);
            var source = FindDisplay(device)?.DisplayScreen.ToPathDisplaySource()
                ?? throw new InvalidOperationException("DPI display source unavailable.");
            source.CurrentDPIScale = (DisplayConfigSourceDPIScale)value;
            if ((int)source.CurrentDPIScale != value) throw new InvalidOperationException("Windows did not confirm the scale.");
        }
        var change = new ReversibleDisplayChange(keep => { if (!keep) Set(original); });
        try { Set(percent); return change; }
        catch { change.Dispose(); throw; }
    }
}

internal static class CustomDisplayScaling
{
    internal static int ToLogPixels(int percent) => percent is >= 100 and <= 500
        ? checked((int)Math.Round(percent * 96d / 100, MidpointRounding.AwayFromZero))
        : throw new ArgumentOutOfRangeException(nameof(percent), "缩放比例必须为 100–500%。 / Scale must be 100–500%.");
    internal static int? ReadPercent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return key?.GetValue("Win8DpiScaling") is int enabled && enabled == 1 && key.GetValue("LogPixels") is int dpi && dpi is >= 96 and <= 480
            ? (int)Math.Round(dpi * 100d / 96) : null;
    }
    // Windows custom scaling is account-wide, not a per-panel arbitrary DPI API.
    // Call only after explicit UI confirmation; never signs the user out.
    internal static void Save(int? percent)
    {
        var dpi = percent is { } value ? ToLogPixels(value) : 96;
        using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop", writable: true);
        var names = new[] { "LogPixels", "Win8DpiScaling" };
        var previous = names.Select(n => (Name: n, Value: key.GetValue(n), Kind: key.GetValue(n) is null ? RegistryValueKind.DWord : key.GetValueKind(n))).ToArray();
        try
        {
            if (percent is not null) key.SetValue("LogPixels", dpi, RegistryValueKind.DWord);
            key.SetValue("Win8DpiScaling", percent is null ? 0 : 1, RegistryValueKind.DWord);
            key.Flush();
        }
        catch
        {
            foreach (var item in previous)
                if (item.Value is null) key.DeleteValue(item.Name, false); else key.SetValue(item.Name, item.Value, item.Kind);
            throw;
        }
    }
}
