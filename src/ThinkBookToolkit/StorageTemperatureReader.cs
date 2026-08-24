using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DiskInfoToolkit;

namespace ThinkBookToolkit;

internal sealed class StorageTemperatureReader
{
    private const int NvmeCompositeTemperatureOffset = 1;
    private const int NvmePercentageUsedOffset = 5;
    private const int NvmeTemperatureSensorOffset = 200;
    private readonly IReadOnlyList<StorageDevice> _devices;

    private StorageTemperatureReader(IReadOnlyList<StorageDevice> devices)
    {
        _devices = devices;
    }

    public static StorageTemperatureReader? TryCreate()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var devices = Storage.GetDisks()
                .Where(device => !device.IsFiltered)
                .ToArray();
            ToolkitLog.Info(
                $"Storage telemetry initialized with {devices.Length} device(s) " +
                $"in {stopwatch.Elapsed.TotalMilliseconds:0} ms.");
            return devices.Length > 0
                ? new StorageTemperatureReader(devices)
                : null;
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "Storage telemetry initialization failed after " +
                $"{stopwatch.Elapsed.TotalMilliseconds:0} ms: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<StorageTemperatureSnapshot> Read()
    {
        var result = new List<StorageTemperatureSnapshot>(_devices.Count);

        foreach (var device in _devices)
        {
            IReadOnlyList<double> temperatures = [];
            double? healthPercent = null;

            try
            {
                // 一次刷新后，同时读取温度和健康度。
                Storage.RefreshVolatileData(device);

                temperatures = ReadTemperatures(device);
                healthPercent = ReadHealthPercent(device);
            }
            catch
            {
                // 单块硬盘读取失败时保留该硬盘条目，数值显示为 --。
            }

            var name = !string.IsNullOrWhiteSpace(device.ProductName)
                ? device.ProductName
                : device.DisplayName;

            result.Add(new StorageTemperatureSnapshot(
                string.IsNullOrWhiteSpace(name) ? "--" : name,
                temperatures,
                healthPercent));
        }

        return result;
    }

    internal static IReadOnlyList<double> ReadNvmeTemperatures(
        byte[]? smartLogData)
    {
        if (smartLogData is null || smartLogData.Length < 216)
            return [];

        var result = new List<double>(3);
        AddKelvinTemperature(
            smartLogData,
            NvmeCompositeTemperatureOffset,
            result);

        for (var index = 0; index < 8 && result.Count < 3; index++)
        {
            AddKelvinTemperature(
                smartLogData,
                NvmeTemperatureSensorOffset + index * 2,
                result);
        }

        return result;
    }

    private static IReadOnlyList<double> ReadTemperatures(StorageDevice device)
    {
        var nvme = ReadNvmeTemperatures(device.Nvme?.SmartLogData);
        if (nvme.Count > 0)
            return nvme;

        return device.Temperature is { } temperature &&
               temperature is > -40 and < 160
            ? [(double)temperature]
            : [];
    }

    private static double? ReadHealthPercent(StorageDevice device)
    {
        // DiskInfoToolkit 2.1.0 的 Health 已经返回“剩余健康度百分比”。
        // 对 NVMe，它内部使用 100 - Percentage Used。
        if (device.Health is { } health)
            return Math.Clamp((double)health, 0d, 100d);

        // 兜底：直接从 NVMe SMART / Health Information Log 的
        // Percentage Used 字段读取，偏移为 5。
        return ReadNvmeHealthPercent(device.Nvme?.SmartLogData);
    }

    internal static double? ReadNvmeHealthPercent(byte[]? smartLogData)
    {
        if (smartLogData is null ||
            smartLogData.Length <= NvmePercentageUsedOffset)
        {
            return null;
        }

        var percentageUsed = smartLogData[NvmePercentageUsedOffset];
        return Math.Clamp(100d - percentageUsed, 0d, 100d);
    }

    private static void AddKelvinTemperature(
        byte[] data,
        int offset,
        List<double> target)
    {
        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(
            data.AsSpan(offset, 2));
        if (kelvin is 0 or ushort.MaxValue)
            return;

        var celsius = kelvin - 273.15;
        if (celsius is > -40 and < 160)
            target.Add(celsius);
    }
}
