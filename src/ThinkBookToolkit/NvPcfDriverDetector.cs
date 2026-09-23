using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal enum NvPcfDriverStatus { Unknown, Available, Missing, Disabled, Unavailable }

internal sealed class NvPcfDriverDetector
{
    private DateTimeOffset _nextCheck;
    private NvPcfDriverStatus _cached;

    internal NvPcfDriverStatus Capture()
    {
        if (DateTimeOffset.UtcNow < _nextCheck) return _cached;
        _nextCheck = DateTimeOffset.UtcNow.AddSeconds(5);
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\nvpcf");
            if (service is null) return _cached = NvPcfDriverStatus.Missing;
            if (service.GetValue("Start") is int start && start == 4) return _cached = NvPcfDriverStatus.Disabled;
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT Present, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE Service = 'nvpcf'");
            searcher.Options.Timeout = TimeSpan.FromSeconds(3);
            using var results = searcher.Get();
            var devices = new List<(bool Present, uint Error)>();
            foreach (ManagementObject device in results)
            {
                using (device)
                {
                    var error = Convert.ToUInt32(device["ConfigManagerErrorCode"] ?? uint.MaxValue);
                    devices.Add((device["Present"] is not false && error != 45, error));
                }
            }
            return _cached = Evaluate(devices);
        }
        catch (Exception ex)
        {
            // Failure to inspect Windows metadata is not proof of absence;
            // the isolated NVPCF capability probe can still establish support.
            ToolkitLog.Warning("NVPCF driver detection failed: " + ex.Message);
            return _cached = NvPcfDriverStatus.Unknown;
        }
    }

    internal static NvPcfDriverStatus Evaluate(IEnumerable<(bool Present, uint Error)> devices)
    {
        var present = devices.Where(d => d.Present).ToArray();
        if (present.Any(d => d.Error == 0)) return NvPcfDriverStatus.Available;
        if (present.Any(d => d.Error == 22)) return NvPcfDriverStatus.Disabled;
        return present.Length == 0 ? NvPcfDriverStatus.Missing : NvPcfDriverStatus.Unavailable;
    }
}
