// GPL-3.0-only helper. It is intentionally a separate process from the MPL
// ThinkBook Toolkit application.
using System.Text.Json;
using ZenStates.Core;

try
{
    using var cpu = new Cpu();
    var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "read";
    if (command == "set")
    {
        if (args.Length != 3 || !uint.TryParse(args[2], out var value))
            throw new ArgumentException("set requires <name> <positive integer>");
        var status = args[1].ToLowerInvariant() switch
        {
            "ppt" => cpu.SetPPTLimit(value),
            "tdc" => cpu.SetTDCVDDLimit(value),
            "edc" => cpu.SetEDCVDDLimit(value),
            "stapm" => cpu.SetStapmLimit(value),
            "fast" => cpu.SetFastLimit(value),
            "slow" => cpu.SetSlowLimit(value),
            "tctlmax" => cpu.SetTctlMax(value),
            _ => throw new ArgumentOutOfRangeException(nameof(args))
        };
        if (status != SMU.Status.OK)
            throw new InvalidOperationException($"SMU returned {status}.");
        Thread.Sleep(75);
    }
    cpu.RefreshPowerTable();
    var table = cpu.powerTable.Table ?? [];
    var desktop = cpu.info.codeName is Cpu.CodeName.Matisse or
        Cpu.CodeName.Vermeer or Cpu.CodeName.Raphael or
        Cpu.CodeName.DragonRange or Cpu.CodeName.GraniteRidge;
    var limits = desktop
        ? ReadDesktop(cpu.info.codeName, table)
        : ReadApu(table);
    var system = cpu.GetSystemPowerLimit();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode = desktop ? "pbo" : "apu",
        values = limits,
        tctlMax = system?.TemperatureLimit
    }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.GetBaseException().Message);
    return 1;
}

static Dictionary<string, int> ReadApu(float[] t)
{
    if (t.Length < 6) throw new InvalidOperationException("PM table is too short.");
    return new() { ["stapm"] = R(t[0]), ["fast"] = R(t[2]), ["slow"] = R(t[4]) };
}

static Dictionary<string, int> ReadDesktop(Cpu.CodeName code, float[] t)
{
    if (code is Cpu.CodeName.Matisse or Cpu.CodeName.Vermeer)
    {
        if (t.Length <= 8) throw new InvalidOperationException("PM table is too short.");
        return new() { ["ppt"] = R(t[0]), ["tdc"] = R(t[2]), ["edc"] = R(t[8]) };
    }
    if (t.Length < 70) throw new InvalidOperationException("PM table is too short.");
    var baseIndex = -1;
    for (var i = 24; i <= Math.Min(200, t.Length - 17); i++)
        if (t[i].Equals(t[19]) && t[i + 3].Equals(t[20])) { baseIndex = i; break; }
    if (baseIndex < 0) throw new InvalidOperationException("EDC layout was not recognized.");
    return new() { ["ppt"] = R(t[2]), ["tdc"] = R(t[8]), ["edc"] = R(t[baseIndex + 15]) };
}

static int R(float value) => checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
