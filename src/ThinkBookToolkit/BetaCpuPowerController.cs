using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ThinkBookToolkit;

public enum BetaCpuPowerKind { IntelMmio, AmdPbo, AmdApu }
internal sealed record BetaCpuPowerSnapshot(
    BetaCpuPowerKind Kind,
    IReadOnlyDictionary<string, int> Values,
    int? TctlMax = null);

internal static class CpuVendorDetector
{
    private static readonly Lazy<string> Cached = new(ReadManufacturer);
    public static string Manufacturer()
        => Cached.Value;
    private static string ReadManufacturer()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer FROM Win32_Processor");
            foreach (ManagementObject item in searcher.Get())
                using (item) return Convert.ToString(item["Manufacturer"]) ?? "";
        }
        catch { }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
    }
    public static bool IsIntel => Manufacturer().Contains("Intel", StringComparison.OrdinalIgnoreCase);
    public static bool IsAmd => Manufacturer().Contains("AMD", StringComparison.OrdinalIgnoreCase);
}

internal static class AmdZenStatesPowerController
{
    public static BetaCpuPowerKind? CachedKind { get; private set; }
    internal static void SetCachedKindForTesting(BetaCpuPowerKind? kind) =>
        CachedKind = kind;
    private static string HelperPath => Path.Combine(
        AppContext.BaseDirectory, "AmdPowerHelper", "ThinkBookToolkit.AmdPowerHelper.exe");
    public static BetaCpuPowerSnapshot Read() => Run("read");
    public static BetaCpuPowerSnapshot Write(string name, int value) =>
        Run("set", name, value.ToString());
    private static BetaCpuPowerSnapshot Run(params string[] args)
    {
        if (!File.Exists(HelperPath))
            throw new FileNotFoundException("AMD ZenStates helper is missing.", HelperPath);
        var start = new ProcessStartInfo(HelperPath)
        {
            WorkingDirectory = Path.GetDirectoryName(HelperPath)!,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("AMD helper could not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new TimeoutException("AMD helper timed out."); }
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
        using var json = JsonDocument.Parse(output);
        var mode = json.RootElement.GetProperty("mode").GetString();
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in json.RootElement.GetProperty("values").EnumerateObject())
            values[item.Name] = item.Value.GetInt32();
        int? tctl = json.RootElement.TryGetProperty("tctlMax", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() : null;
        var result = new BetaCpuPowerSnapshot(
            mode == "pbo" ? BetaCpuPowerKind.AmdPbo : BetaCpuPowerKind.AmdApu,
            values, tctl);
        CachedKind = result.Kind;
        return result;
    }
}

internal static class IntelMmioPowerController
{
    private const ulong LimitOffset = 0x59A0;
    public static BetaCpuPowerSnapshot Read()
    {
        using var io = Open(out var mmio, out var unit);
        var state = Decode(unit, mmio.Read());
        return Snapshot(state);
    }
    public static BetaCpuPowerSnapshot Write(int pl1, int pl2, int turbo)
    {
        using var io = Open(out var mmio, out var unit);
        var current = Decode(unit, mmio.Read());
        var encoded = Build(current, pl1, pl2, turbo);
        mmio.Write(encoded);
        Thread.Sleep(20);
        return Snapshot(Decode(unit, mmio.Read()));
    }
    private static IDisposable Open(out Mmio mmio, out ulong unit)
    {
        var root = AppContext.BaseDirectory;
        var msr = new Pawn(Path.Combine(root, "IntelMSR.bin"));
        try
        {
            unit = msr.Execute("ioctl_read_msr", [0x606], 1)[0];
            var mch = new Pawn(Path.Combine(root, "IntelMCHBAR.bin"));
            var baseAddress = mch.Execute("ioctl_get_mchbar_addr", [], 1)[0];
            var reference = mch.Execute("ioctl_read_qword", [LimitOffset], 1)[0];
            mmio = new Mmio(baseAddress + LimitOffset);
            if (mmio.Read() != reference) throw new IOException("MMIO verification against PawnIO failed.");
            return new Pair(msr, mch);
        }
        catch { msr.Dispose(); throw; }
    }
    private static BetaCpuPowerSnapshot Snapshot(Rapl s) => new(
        BetaCpuPowerKind.IntelMmio,
        new Dictionary<string, int> { ["pl1"] = (int)Math.Round(s.Pl1), ["pl2"] = (int)Math.Round(s.Pl2), ["turbo"] = (int)Math.Round(s.Time) });
    private sealed record Rapl(ulong Raw, double PowerUnit, double TimeUnit, double Pl1, double Pl2, double Time);
    private static Rapl Decode(ulong units, ulong raw)
    {
        var pu = 1d / Math.Pow(2, (int)(units & 0xF));
        var tu = 1d / Math.Pow(2, (int)((units >> 16) & 0xF));
        var tr = (int)((raw >> 17) & 0x7F); var y = tr & 31; var z = tr >> 5;
        return new(raw, pu, tu, (raw & 0x7FFF) * pu, ((raw >> 32) & 0x7FFF) * pu,
            Math.Pow(2, y) * (1 + z / 4d) * tu);
    }
    private static ulong Build(Rapl s, int pl1, int pl2, int turbo)
    {
        if ((s.Raw & (1UL << 63)) != 0) throw new InvalidOperationException("MMIO power limit is locked.");
        uint P(int w) => checked((uint)Math.Round(w / s.PowerUnit));
        var best = 0; var error = double.MaxValue;
        for (var y = 0; y < 32; y++) for (var z = 0; z < 4; z++)
        { var v = Math.Pow(2, y) * (1 + z / 4d) * s.TimeUnit; if (Math.Abs(v - turbo) < error) { error = Math.Abs(v - turbo); best = y | z << 5; } }
        var raw = s.Raw;
        raw = (raw & ~0x7FFFUL) | P(pl1) | 1UL << 15;
        raw = (raw & ~(0x7FUL << 17)) | ((ulong)best << 17);
        raw = (raw & ~(0x7FFFUL << 32)) | (ulong)P(pl2) << 32 | 1UL << 47;
        return raw;
    }
    private sealed class Pair(IDisposable a, IDisposable b) : IDisposable { public void Dispose() { b.Dispose(); a.Dispose(); } }

    private sealed class Pawn : IDisposable
    {
        private readonly SafeFileHandle handle;
        public Pawn(string module)
        {
            if (!File.Exists(module)) throw new FileNotFoundException("PawnIO module is missing.", module);
            handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, 0, 3, 0x80, 0);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "PawnIO could not be opened.");
            var blob = File.ReadAllBytes(module); if (!DeviceIoControl(handle, 0xA1B22084, blob, (uint)blob.Length, null, 0, out _, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public ulong[] Execute(string name, ulong[] input, int count)
        {
            var ib = new byte[32 + input.Length * 8]; Encoding.ASCII.GetBytes(name).CopyTo(ib, 0);
            Buffer.BlockCopy(input, 0, ib, 32, input.Length * 8); var ob = new byte[count * 8];
            if (!DeviceIoControl(handle, 0xA1B22104, ib, (uint)ib.Length, ob, (uint)ob.Length, out var returned, 0) || returned < ob.Length) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new ulong[count]; Buffer.BlockCopy(ob, 0, result, 0, ob.Length); return result;
        }
        public void Dispose() => handle.Dispose();
    }
    private sealed class Mmio
    {
        private readonly ulong address;
        public Mmio(ulong address) { this.address = address; if (IsInpOutDriverOpen() == 0) throw new InvalidOperationException("InpOutx64 driver is unavailable."); }
        public ulong Read() { var p = MapPhysToLin((nint)address, 8, out var h); if (p == 0) throw new IOException("MMIO map failed."); try { return (ulong)Marshal.ReadInt64(p); } finally { UnmapPhysicalMemory(h, p); } }
        public void Write(ulong v) { var p = MapPhysToLin((nint)address, 8, out var h); if (p == 0) throw new IOException("MMIO map failed."); try { Marshal.WriteInt64(p, (long)v); } finally { UnmapPhysicalMemory(h, p); } }
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, nint sa, uint c, uint f, nint t);
    [DllImport("kernel32", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint c, byte[] i, uint il, byte[]? o, uint ol, out uint r, nint ov);
    [DllImport("InpOutx64.dll")] static extern uint IsInpOutDriverOpen();
    [DllImport("InpOutx64.dll")] static extern nint MapPhysToLin(nint p, uint s, out nint h);
    [DllImport("InpOutx64.dll")] static extern bool UnmapPhysicalMemory(nint h, nint p);
}
