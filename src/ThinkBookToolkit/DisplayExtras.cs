using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal sealed record DisplayExtraValue(int Value, bool Writable = true);
internal sealed record DisplayExtraState(DisplayBackend Source, Dictionary<string, DisplayExtraValue> Values,
    string Detail = "", bool NeedsHello = false, bool CameraBlocked = false)
{
    internal bool Available => Values.Count > 0;
}

internal sealed class DisplayExtras
{
    internal static readonly DisplayExtras Shared = new(DisplayExtrasTransport.CallAsync,
        preferLocal: DisplayExtrasTransport.PreferLocal, hostAvailable: DisplayExtrasTransport.HasHost);
    internal static readonly int[] Timers = [0, -1, 30, 60, 120, 180, 300, 600, 900, 1200, 1800];
    internal static readonly string[] DimmerKeys = ["TaskbarDimmer", "BackgroundDimmer", "DisplayDimmer"];
    internal static readonly string[] PresenceKeys = ["Approach", "PresentLeave", "Video"];
    private readonly SemaphoreSlim _presence = new(1), _oled = new(1);
    private readonly Func<DisplayBackend, string, string, JsonObject?, Task<JsonObject>> _call;
    private readonly Func<bool> _darkTaskbar, _cvEnabled;
    private readonly Func<DisplayBackend, bool> _preferLocal;
    private readonly Func<bool> _hostAvailable;
    private const string Zero = "SystemManagement.ZeroTouch", Think = "SystemManagement.SmartSense", Spectrum = "SystemManagement.Spectrum";

    internal DisplayExtras(Func<DisplayBackend, string, string, JsonObject?, Task<JsonObject>> call,
        Func<bool>? darkTaskbar = null, Func<bool>? cvEnabled = null, Func<DisplayBackend, bool>? preferLocal = null,
        Func<bool>? hostAvailable = null)
    {
        _call = call;
        _preferLocal = preferLocal ?? (_ => false);
        _hostAvailable = hostAvailable ?? (() => true);
        _darkTaskbar = darkTaskbar ?? TaskbarDimmingRestricted;
        _cvEnabled = cvEnabled ?? (() =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Lenovo", "Vantage", "SystemData", "Hypothesis", "hypothesis.config");
            return File.Exists(path) && Bool(JsonNode.Parse(File.ReadAllText(path))?["Choiceses"]?["EnableCVDSP"]) == true;
        });
    }

    private static bool TaskbarDimmingRestricted()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 1) is 0; }
        catch (Exception ex)
        {
            ToolkitLog.Warning("Taskbar theme unavailable: " + ex.Message);
            return true;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue ? node.ToString() : null;
    private static bool? Bool(JsonNode? node) => bool.TryParse(Text(node), out var value) ? value : null;
    private static int? Number(JsonNode? node)
    {
        var text = Text(node);
        if (text is null) return null;
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(hex ? text[2..] : text, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : null;
    }
    private static JsonObject? Unique(JsonNode? items, string key)
    {
        if (items is not JsonArray array) return null;
        var matches = array.OfType<JsonObject>().Where(x => Text(x["key"]) == key).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static bool Success(JsonNode? code) => Text(code) is string value &&
        (value == "0" || value.Equals("Success", StringComparison.OrdinalIgnoreCase));
    private static void CheckErrors(JsonObject payload)
    {
        foreach (var pair in payload.Where(p => p.Key.Equals("errorCode", StringComparison.OrdinalIgnoreCase)))
            if (!Success(pair.Value)) throw new InvalidDataException("Lenovo errorCode=" + pair.Value);
    }
    private async Task<JsonObject> Call(DisplayBackend source, string contract, string command, JsonObject? payload = null)
    {
        var reply = await _call(source, contract, command, payload).ConfigureAwait(false);
        CheckErrors(reply);
        return reply;
    }

    internal async Task<DisplayExtraState> ReadAsync(bool oled)
    {
        var gate = oled ? _oled : _presence;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var errors = new List<string>();
            var families = oled ? new[] { (DisplayBackend.DirectSpectrum, DisplayBackend.Spectrum) } :
                new[] { (DisplayBackend.DirectHpd, DisplayBackend.ZeroTouch), (DisplayBackend.DirectThink, DisplayBackend.Think) };
            foreach (var (local, hosted) in families)
            {
                DisplayBackend[] order;
                try { order = !_hostAvailable() ? [local] : _preferLocal(local) ? [local, hosted] : [hosted, local]; }
                catch (Exception ex)
                {
                    errors.Add("Display dependency discovery: " + ex.GetBaseException().Message);
                    order = [local, hosted];
                }
                foreach (var source in order)
                {
                    try
                    {
                        var (state, _) = await ReadSource(source).ConfigureAwait(false);
                        if (state.Available) return state with { Detail = "" };
                        errors.Add(source + ": " + state.Detail);
                        break;
                    }
                    catch (Exception ex) { errors.Add(source + ": " + ex.GetBaseException().Message); }
                }
            }
            var detail = string.Join(" · ", errors);
            ToolkitLog.Info("Display capability probe: " + detail);
            return new(oled ? DisplayBackend.Spectrum : DisplayBackend.ZeroTouch, new(), detail);
        }
        finally { gate.Release(); }
    }

    private async Task<(DisplayExtraState State, JsonObject? List)> ReadSource(DisplayBackend source)
    {
        var result = new DisplayExtraState(source, new(), "无可用设置 / No supported setting with a readable value.");
        if (source is DisplayBackend.Spectrum or DisplayBackend.DirectSpectrum)
        {
            var caps = await Call(source, Spectrum, "Get-Capability").ConfigureAwait(false);
            var supported = Bool(Unique(caps["CapabilityList"]?["Items"], "BurningProtectionControl")?["value"]);
            if (supported != true) return (result with { Detail = supported == false ? "BurningProtectionControl=False" : "Missing or invalid BurningProtectionControl." }, null);
            var data = await Call(source, Spectrum, "Get-DisplaySettings").ConfigureAwait(false);
            foreach (var key in DimmerKeys)
            {
                var row = Unique(data["SettingList"]?["Items"], key);
                if (row is null || Bool(row["enabled"]) is not bool writable) continue;
                if (row.Any(p => p.Key.Equals("errorCode", StringComparison.OrdinalIgnoreCase) &&
                    Text(p.Value) != "" && !Success(p.Value))) continue;
                var timer = (Text(row["trigger"]), Text(row["timer"])) switch
                {
                    ("None", "0") => 0, ("Running", "0") => -1, ("KeyMouseIdleHalf", "0") => 300,
                    ("KeyMouseIdle", var seconds) when int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
                        n > 0 && Timers.Contains(n) && seconds == n.ToString(CultureInfo.InvariantCulture) => n,
                    _ => (int?)null
                };
                if (timer.HasValue) result.Values[key] = new(timer.Value, writable && (key != "TaskbarDimmer" || !_darkTaskbar()));
            }
            return (result, data);
        }
        if (source is DisplayBackend.Think or DisplayBackend.DirectThink)
        {
            var data = await Call(source, Think, "Get-HPDSettings").ConfigureAwait(false);
            JsonNode? Value(string key) => Unique(data["SettingList"], key)?["value"];
            if (Number(Value("Version")) is not int version) return (result, null);
            var legacy = version < 0x02000100;
            if (legacy ? Bool(Value("Capability")) != true : Number(Value("Capability")) is not > 1)
                return (result with { Detail = "Think HPD capability unavailable." }, null);
            foreach (var key in PresenceKeys.Take(2))
            {
                bool? value = legacy ? Bool(Value(key)) : Number(Value(key)) switch { 2 or 4 => false, 3 or 5 => true, _ => (bool?)null };
                if (value.HasValue) result.Values[key] = new(value.Value ? 1 : 0);
            }
            return (result with { NeedsHello = Bool(Value("FacialFeatureRegistered")) == false || Number(Value("FacialFeatureRegistered")) == 1 }, null);
        }
        var hpd = await Call(source, Zero, "Get-Settings").ConfigureAwait(false);
        if (Bool(hpd["capacity"]) == false) return (result with { Detail = "HPD capacity=false" }, null);
        if (!Success(hpd["errorCode"]) || Bool(hpd["capacity"]) != true || Number(hpd["capability"]) is not int bits || bits < 0)
            return (result with { Detail = "Missing or invalid HPD status/capability." }, null);
        var blocked = false;
        if (Number(hpd["sensorType"]) == 4)
        {
            if (!_cvEnabled()) return (result with { Detail = "EnableCVDSP policy unavailable or disabled." }, null);
            if (!_hostAvailable()) return (result with { Detail = "Cannot verify CVDSP camera permission and shutter status without the Lenovo host." }, null);
            var status = await Call(DisplayBackend.ZeroTouch, Zero, "Get-HPDStatus").ConfigureAwait(false);
            var camera = await Call(DisplayBackend.ZeroTouch, "SystemManagement.Camera", "Get-Capability").ConfigureAwait(false);
            var permissions = (camera["capabilityList"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["capability"];
            blocked = Text(Unique(permissions, "PrivacyPermission")?["value"]) != "Allowed" ||
                Text(status["Camera_Shutter_Supported"]) switch { "0" => false, "1" => Text(status["Camera_Shutter"]) != "1", _ => true };
        }
        foreach (var (key, mask, field) in new[] { ("Approach", 2, "approachEnabled"), ("PresentLeave", 16, "presenceLeaveEnabled"), ("Video", 512, "videoAutoPauseResumeEnabled") })
            if ((bits & mask) != 0 && Bool(hpd[field]) is bool value) result.Values[key] = new(value ? 1 : 0, !blocked);
        return (result with { NeedsHello = Bool(hpd["faceIdRegistered"]) == false, CameraBlocked = blocked }, null);
    }

    internal async Task<(DisplayExtraState State, string? Error)> SetAsync(DisplayBackend source, string key, int value,
        Func<bool> stillCurrent)
    {
        var oled = source is DisplayBackend.Spectrum or DisplayBackend.DirectSpectrum;
        var gate = oled ? _oled : _presence;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (before, raw) = await ReadSource(source).ConfigureAwait(false);
            if (!stillCurrent() || !before.Values.TryGetValue(key, out var row) || !row.Writable)
                return (before, "设置当前不可更改 / Setting unavailable.");
            if (row.Value == value) return (before, null);
            string? writeError = null;
            try
            {
                if (oled)
                {
                    if (!Timers.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
                    var payload = (JsonObject)raw!.DeepClone();
                    var target = Unique(payload["SettingList"]?["Items"], key)!;
                    target["trigger"] = value switch { 0 => "None", -1 => "Running", _ => "KeyMouseIdle" };
                    target["timer"] = Math.Max(value, 0).ToString(CultureInfo.InvariantCulture);
                    var reply = await Call(source, Spectrum, "Set-DisplaySettings", payload).ConfigureAwait(false);
                    if (!Success(reply["ErrorCode"])) throw new InvalidDataException("Missing Spectrum success status.");
                }
                else
                {
                    if (value is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(value));
                    var enabled = value == 1;
                    JsonObject reply;
                    if (source is DisplayBackend.Think or DisplayBackend.DirectThink)
                        reply = await Call(source, Think, "Set-HPDSetting", new JsonObject { ["SettingList"] = new JsonArray(new JsonObject {
                            ["Setting"] = new JsonArray(new JsonObject { ["key"] = key, ["value"] = enabled.ToString() }) }) }).ConfigureAwait(false);
                    else
                    {
                        var field = key switch { "Approach" => "SetApproachEnabled", "PresentLeave" => "SetPresenceLeaveEnabled", "Video" => "SetVideoAutoPauseResume", _ => throw new ArgumentException(key) };
                        reply = await Call(source, Zero, "Set-Settings", new JsonObject { ["SettingList"] = new JsonObject {
                            [field] = enabled ? "true" : "false" } }).ConfigureAwait(false);
                    }
                    if (!Success(reply["ErrorCode"] ?? reply["errorCode"])) throw new InvalidDataException("Missing HPD setting result.");
                }
            }
            catch (Exception ex) { writeError = ex.GetBaseException().Message; }
            for (var i = 0; ; i++)
            {
                try
                {
                    var (actual, _) = await ReadSource(source).ConfigureAwait(false);
                    if (actual.Values.TryGetValue(key, out var read) && read.Value == value) return (actual, writeError);
                    if (i == 2 || !actual.Values.ContainsKey(key) || !stillCurrent())
                        return (actual, (writeError is null ? "" : writeError + " · ") + $"设置未生效 / Not applied ({key}: {value} → {read?.Value.ToString() ?? "?"}).");
                }
                catch (Exception ex)
                {
                    return (new(source, new()), (writeError is null ? "" : writeError + " · ") + "回读失败 / Readback failed: " + ex.GetBaseException().Message);
                }
                await Task.Delay(180).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { return (new(source, new()), ex.GetBaseException().Message); }
        finally { gate.Release(); }
    }
}
