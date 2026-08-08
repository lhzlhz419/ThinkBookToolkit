using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ThinkBookToolkit;

internal sealed record NvidiaPrivateTelemetrySnapshot(
    IReadOnlyList<double> MemoryChipTemperaturesC,
    double? HotSpotTemperatureC)
{
    public static NvidiaPrivateTelemetrySnapshot Empty { get; } = new([], null);
}

internal sealed class NvidiaPrivateTelemetryReader
{
    private const uint InitializeInterfaceId = 0x0150E828;
    private const uint EnumPhysicalGpusInterfaceId = 0xE5AC921F;
    private const uint GpuGetFullNameInterfaceId = 0xCEEE8E9F;
    private const uint GpuRegisterOpInterfaceId = 0x2EB3C140;
    private const int RequestSize = 0x1808;
    private const int RecordSize = 0x18;
    private const ushort ReadRegisterOpcode = 0x15;
    private readonly IReadOnlyList<GpuHandle> _gpus;
    private readonly RegisterOpDelegate _registerOp;
    private readonly object _sync = new();

    private NvidiaPrivateTelemetryReader(
        IReadOnlyList<GpuHandle> gpus,
        RegisterOpDelegate registerOp)
    {
        _gpus = gpus;
        _registerOp = registerOp;
    }

    public static NvidiaPrivateTelemetryReader? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var initializePointer = QueryInterface(InitializeInterfaceId);
            var enumeratePointer = QueryInterface(EnumPhysicalGpusInterfaceId);
            var registerPointer = QueryInterface(GpuRegisterOpInterfaceId);
            if (initializePointer == IntPtr.Zero ||
                enumeratePointer == IntPtr.Zero ||
                registerPointer == IntPtr.Zero)
            {
                return null;
            }

            var initialize = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(
                initializePointer);
            if (initialize() != 0)
                return null;

            var handles = new IntPtr[64];
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumPhysicalGpusDelegate>(
                enumeratePointer);
            if (enumerate(handles, out var count) != 0 || count <= 0)
                return null;

            var getFullNamePointer = QueryInterface(GpuGetFullNameInterfaceId);
            GetFullNameDelegate? getFullName = getFullNamePointer == IntPtr.Zero
                ? null
                : Marshal.GetDelegateForFunctionPointer<GetFullNameDelegate>(
                    getFullNamePointer);
            var gpus = new List<GpuHandle>();
            for (var index = 0; index < Math.Min(count, handles.Length); index++)
            {
                if (handles[index] == IntPtr.Zero)
                    continue;
                var name = string.Empty;
                if (getFullName is not null)
                {
                    var text = new StringBuilder(64);
                    if (getFullName(handles[index], text) == 0)
                        name = text.ToString();
                }
                gpus.Add(new GpuHandle(handles[index], name));
            }
            if (gpus.Count == 0)
                return null;

            return new NvidiaPrivateTelemetryReader(
                gpus,
                Marshal.GetDelegateForFunctionPointer<RegisterOpDelegate>(
                    registerPointer));
        }
        catch
        {
            return null;
        }
    }

    public NvidiaPrivateTelemetrySnapshot Read(string gpuName)
    {
        if (!SupportsPerChipMemoryTemperature(gpuName) ||
            !GpuDevicePresenceDetector.IsActive(gpuName))
            return NvidiaPrivateTelemetrySnapshot.Empty;

        lock (_sync)
        {
            try
            {
                var gpu = SelectGpu(gpuName);
                if (gpu == IntPtr.Zero)
                    return NvidiaPrivateTelemetrySnapshot.Empty;
                var memory = ReadMemoryChipTemperatures(gpu);
                var hotSpot = IsRtx50Series(gpuName)
                    ? ReadBlackwellHotSpot(gpu)
                    : null;
                return new NvidiaPrivateTelemetrySnapshot(memory, hotSpot);
            }
            catch (Exception ex)
            {
                ToolkitLog.Warning(
                    "NVIDIA private telemetry read failed: " + ex.Message);
                return NvidiaPrivateTelemetrySnapshot.Empty;
            }
        }
    }

    internal static double DecodeMemoryChipTemperature(uint raw)
    {
        var code = (int)((raw >> 16) & 0xFF);
        return 2d * (code - 20);
    }

    internal static double? DecodeBlackwellHotSpot(
        IEnumerable<uint?> rawValues)
    {
        double? maximum = null;
        foreach (var rawValue in rawValues)
        {
            if (rawValue is not { } raw || (raw & 0x40000000u) == 0)
                continue;
            var value = ((raw >> 3) & 0x1FFFu) / 32d;
            if ((raw & 0x00010000u) != 0)
                value = -value;
            if (value is <= -40 or >= 160)
                continue;
            maximum = !maximum.HasValue || value > maximum.Value
                ? value
                : maximum;
        }
        return maximum;
    }

    private IReadOnlyList<double> ReadMemoryChipTemperatures(IntPtr gpu)
    {
        var layoutResult = ReadRegisters(gpu, [0x00900200u]);
        if (layoutResult.Count == 0 || layoutResult[0] is not { } layout)
            return [];
        var fourLaneGroups = (layout & 0x00400000u) != 0;
        var positions = Enumerable.Range(0, 16)
            .Select(index =>
            {
                var group = fourLaneGroups ? index / 4 : index / 2;
                var lane = fourLaneGroups ? index & 3 : index & 1;
                var block = 0x009024C0u + (uint)group * 0x4000u;
                return new MemoryPosition(lane, block + 0x10u, block + (uint)lane * 4u);
            })
            .ToArray();
        var addresses = positions
            .SelectMany(position => new[] { position.ValidAddress, position.DataAddress })
            .Distinct()
            .ToArray();
        var values = ReadRegisters(gpu, addresses);
        var registers = addresses
            .Select((address, index) => (address, value: values[index]))
            .ToDictionary(item => item.address, item => item.value);
        var result = new List<double>();
        foreach (var position in positions)
        {
            if (!registers.TryGetValue(position.ValidAddress, out var validValue) ||
                validValue is not { } valid || IsPoisoned(valid) ||
                ((valid >> (24 + position.Lane)) & 1u) == 0)
            {
                continue;
            }
            if (!registers.TryGetValue(position.DataAddress, out var dataValue) ||
                dataValue is not { } data || IsPoisoned(data))
            {
                continue;
            }
            var temperature = DecodeMemoryChipTemperature(data);
            if (temperature is > -40 and < 160)
                result.Add(temperature);
        }
        return result;
    }

    private double? ReadBlackwellHotSpot(IntPtr gpu)
    {
        var addresses = Enumerable.Range(0, 6)
            .Select(index => 0x00AD0A90u + (uint)index * 4u)
            .ToArray();
        return DecodeBlackwellHotSpot(ReadRegisters(gpu, addresses));
    }

    private IReadOnlyList<uint?> ReadRegisters(
        IntPtr gpu,
        IReadOnlyList<uint> addresses)
    {
        if (addresses.Count is <= 0 or > 256)
            return [];
        var request = new byte[RequestSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            request.AsSpan(0, 4),
            0x00011808u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            request.AsSpan(4, 4),
            (uint)addresses.Count);
        for (var index = 0; index < addresses.Count; index++)
        {
            var offset = 8 + index * RecordSize;
            BinaryPrimitives.WriteUInt16LittleEndian(
                request.AsSpan(offset, 2),
                ReadRegisterOpcode);
            BinaryPrimitives.WriteUInt32LittleEndian(
                request.AsSpan(offset + 4, 4),
                addresses[index]);
        }

        var pin = GCHandle.Alloc(request, GCHandleType.Pinned);
        try
        {
            if (_registerOp(gpu, pin.AddrOfPinnedObject()) != 0)
                return Enumerable.Repeat<uint?>(null, addresses.Count).ToArray();
        }
        finally
        {
            pin.Free();
        }

        var result = new uint?[addresses.Count];
        for (var index = 0; index < addresses.Count; index++)
        {
            var offset = 8 + index * RecordSize;
            if (BinaryPrimitives.ReadUInt16LittleEndian(
                    request.AsSpan(offset + 2, 2)) == 0)
            {
                result[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    request.AsSpan(offset + 0x10, 4));
            }
        }
        return result;
    }

    private IntPtr SelectGpu(string gpuName)
    {
        if (_gpus.Count == 1)
            return _gpus[0].Handle;
        var normalized = NormalizeGpuName(gpuName);
        var match = _gpus.FirstOrDefault(gpu =>
            NormalizeGpuName(gpu.Name).Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(NormalizeGpuName(gpu.Name), StringComparison.OrdinalIgnoreCase));
        return match?.Handle ?? _gpus[0].Handle;
    }

    private static string NormalizeGpuName(string value) =>
        value
            .Replace("NVIDIA", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("GeForce", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

    private static bool SupportsPerChipMemoryTemperature(string gpuName) =>
        gpuName.Contains("RTX 30", StringComparison.OrdinalIgnoreCase) ||
        gpuName.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) ||
        IsRtx50Series(gpuName);

    private static bool IsRtx50Series(string gpuName) =>
        gpuName.Contains("RTX 50", StringComparison.OrdinalIgnoreCase);

    private static bool IsPoisoned(uint value) =>
        (value & 0xFFFF0000u) == 0xBADF0000u;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate(
        [Out] IntPtr[] handles,
        out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetFullNameDelegate(
        IntPtr handle,
        StringBuilder name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RegisterOpDelegate(
        IntPtr handle,
        IntPtr request);

    private sealed record GpuHandle(IntPtr Handle, string Name);

    private sealed record MemoryPosition(
        int Lane,
        uint ValidAddress,
        uint DataAddress);
}
