using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ThinkBookToolkit;

internal sealed record BiosIoDefinition(
    string Id,
    string ChineseName,
    string EnglishName,
    string ChineseDescription,
    string EnglishDescription,
    string ChineseDisableWarning,
    string EnglishDisableWarning,
    bool IsVirtualization = false);

internal sealed record BiosIoState(
    BiosIoDefinition Definition,
    bool Enabled,
    string CurrentValue,
    IReadOnlyList<string> AllowedValues);

internal sealed record BiosIoWriteResult(
    BiosIoState State,
    bool RestartRequired);

internal static class BiosIoController
{
    private const string NamespacePath = @"root\WMI";
    private const string EnabledValue = "Enable";
    private const string DisabledValue = "Disable";

    public static IReadOnlyList<BiosIoDefinition> Definitions { get; } =
    [
        new(
            "USBPort",
            "USB 数据端口",
            "USB data ports",
            "控制外接 USB 设备的数据连接。",
            "Control data connections for external USB devices.",
            "关闭后，外接 USB 键盘、鼠标、存储设备和扩展坞可能立即或在重启后不可用。请先确认仍有可用的输入方式。",
            "External USB keyboards, mice, storage devices, and docks may stop working immediately or after restart. Make sure another input method is available."),
        new(
            "Bluetooth",
            "蓝牙",
            "Bluetooth",
            "控制内置蓝牙设备。",
            "Control the built-in Bluetooth device.",
            "关闭后，蓝牙鼠标、耳机和键盘将断开连接。",
            "Bluetooth mice, headsets, and keyboards will be disconnected."),
        new(
            "IntegratedCamera",
            "集成摄像头",
            "Integrated camera",
            "控制内置摄像头及依赖它的功能。",
            "Control the built-in camera and dependent features.",
            "关闭后，摄像头和 Windows Hello 人脸识别可能不可用。",
            "The camera and Windows Hello face recognition may become unavailable."),
        new(
            "FingerprintReader",
            "指纹读取器",
            "Fingerprint reader",
            "控制内置指纹读取器。",
            "Control the built-in fingerprint reader.",
            "关闭后，Windows Hello 指纹登录将不可用。",
            "Windows Hello fingerprint sign-in will become unavailable."),
        new(
            "MemoryCardSlot",
            "存储卡插槽",
            "Memory card slot",
            "控制内置存储卡读取器。",
            "Control the built-in memory card reader.",
            "关闭后，SD 卡及其他受支持的存储卡将不可用。",
            "SD cards and other supported memory cards will become unavailable."),
        new(
            "Microphone",
            "内置麦克风",
            "Built-in microphone",
            "控制内置麦克风硬件。",
            "Control the built-in microphone hardware.",
            "关闭后，内置麦克风和依赖它的会议功能将不可用。",
            "The built-in microphone and dependent conferencing features will become unavailable."),
        new(
            "Thunderbolt(TM)",
            "Thunderbolt / USB4",
            "Thunderbolt / USB4",
            "控制 Thunderbolt 与 USB4 数据连接。",
            "Control Thunderbolt and USB4 data connections.",
            "关闭后，扩展坞、外接显示器和高速存储设备可能断开。",
            "Docks, external displays, and high-speed storage devices may disconnect."),
        new(
            "WirelessLAN",
            "无线局域网",
            "Wireless LAN",
            "控制内置 Wi-Fi 设备。",
            "Control the built-in Wi-Fi device.",
            "关闭后 Wi-Fi 将不可用。远程操作前请确认存在其他网络连接。",
            "Wi-Fi will become unavailable. Before changing this remotely, make sure another network connection exists."),
        new(
            "Intel(R)VirtualizationTechnology",
            "Intel CPU 虚拟化（VT-x）",
            "Intel CPU virtualization (VT-x)",
            "供 Hyper-V、WSL2、Windows Sandbox 和虚拟机使用。",
            "Used by Hyper-V, WSL2, Windows Sandbox, and virtual machines.",
            "关闭前请停止虚拟机、WSL、Docker 和依赖虚拟化的安全功能。",
            "Stop virtual machines, WSL, Docker, and virtualization-based security features before disabling this setting.",
            true),
        new(
            "Intel(R)VT-dFeature",
            "Intel VT-d / IOMMU",
            "Intel VT-d / IOMMU",
            "控制 DMA 重映射、设备直通和部分 VBS 能力。",
            "Control DMA remapping, device passthrough, and some VBS capabilities.",
            "关闭前请停止使用设备直通、DMA 重映射或相关 VBS 功能的工作负载。",
            "Stop workloads using device passthrough, DMA remapping, or related VBS features before disabling this setting.",
            true)
    ];

    public static IReadOnlyList<BiosIoState> ReadSupportedStates()
    {
        var current = ReadCurrentValues();
        var result = new List<BiosIoState>();
        using var selector = LenovoWmi.GetActiveInstance(
            "Lenovo_GetBiosSelections");
        foreach (var definition in Definitions)
        {
            if (!current.TryGetValue(definition.Id, out var currentValue))
                continue;

            IReadOnlyList<string> allowed;
            try
            {
                var selections = LenovoWmi.InvokeString(
                    selector,
                    "GetBiosSelections",
                    new Dictionary<string, object>
                    {
                        ["Item"] = definition.Id
                    },
                    "Selections");
                allowed = ParseSelections(selections);
            }
            catch
            {
                continue;
            }

            if (!SupportsToggle(allowed))
                continue;
            result.Add(new(
                definition,
                currentValue.Equals(EnabledValue, StringComparison.OrdinalIgnoreCase),
                currentValue,
                allowed));
        }
        return result;
    }

    public static BiosIoWriteResult SetEnabled(string id, bool enabled)
    {
        var before = ReadSupportedStates().FirstOrDefault(state =>
                         state.Definition.Id.Equals(id, StringComparison.Ordinal))
                     ?? throw new NotSupportedException(
                         "The requested BIOS I/O setting is unavailable.");
        var value = enabled ? EnabledValue : DisabledValue;
        if (before.CurrentValue.Equals(value, StringComparison.OrdinalIgnoreCase))
            return new(before, false);

        using (var setter = LenovoWmi.GetActiveInstance("Lenovo_SetBiosSetting"))
        {
            var result = LenovoWmi.InvokeString(
                setter,
                "SetBiosSetting",
                new Dictionary<string, object>
                {
                    ["parameter"] = $"{id},{value},"
                },
                "return");
            EnsureSuccess("SetBiosSetting", result);
        }

        using (var saver = LenovoWmi.GetActiveInstance("Lenovo_SaveBiosSettings"))
        {
            var result = LenovoWmi.InvokeString(
                saver,
                "SaveBiosSettings",
                null,
                "return");
            EnsureSuccess("SaveBiosSettings", result);
        }

        return new(
            before with
            {
                Enabled = enabled,
                CurrentValue = value
            },
            RestartRequired: true);
    }

    internal static IReadOnlyList<string> ParseSelections(string value) =>
        value.Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static bool SupportsToggle(IEnumerable<string> values)
    {
        var set = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Contains(EnabledValue) && set.Contains(DisabledValue);
    }

    private static Dictionary<string, string> ReadCurrentValues()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var searcher = new ManagementObjectSearcher(
            NamespacePath,
            "SELECT CurrentSetting FROM Lenovo_BiosSetting");
        using var collection = searcher.Get();
        foreach (ManagementObject item in collection)
        {
            using (item)
            {
                var raw = Convert.ToString(item["CurrentSetting"]);
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var parts = raw.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                    result[parts[0]] = parts[1];
            }
        }
        return result;
    }

    private static void EnsureSuccess(string operation, string result)
    {
        if (result.Equals("Success", StringComparison.OrdinalIgnoreCase))
            return;
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result)
                ? $"{operation} returned no result."
                : $"{operation}: {result}");
    }
}
