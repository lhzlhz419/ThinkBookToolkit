using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class DisplayExtrasTests
{
    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    internal static void VerifyPage()
    {
        using var runtime = new ToolkitRuntimeService(new AppSettings { Language = "zh-CN" }, persistSystemSessionState: false);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var id in new[] { FeatureIds.ZeroTouch, FeatureIds.OledProtection })
        {
            runtime.SetReportForTesting(new([new(id, "显示", id, true, "fixture")]));
            Check(ToolkitMainWindow.HasDisplayFeatures(runtime.Report!),
                "A new feature on its own must enable display navigation.");
        }
        using var page = new ToolkitDisplayPage(runtime);
        void Render(string field, DisplayExtraState state, string? error = null)
        {
            var group = typeof(ToolkitDisplayPage).GetField(field, flags)!.GetValue(page)!;
            typeof(ToolkitDisplayPage).GetMethod("RenderExtra", flags)!.Invoke(page, [group, state, error]);
            var card = (Border)group.GetType().GetField("Card", flags)!.GetValue(group)!;
            Check((card.Visibility == Visibility.Visible) == state.Available, "Empty feature card was visible.");
            var rows = (Dictionary<string, (FrameworkElement Row, Control Editor)>)group.GetType().GetField("Rows", flags)!.GetValue(group)!;
            foreach (var (key, (row, control)) in rows)
            {
                var visible = state.Values.TryGetValue(key, out var value);
                Check((row.Visibility == Visibility.Visible) == visible && control.IsEnabled == (value?.Writable == true),
                    "Row visibility or interactivity disagrees with the device snapshot.");
                if (visible && control is CheckBox toggle) Check(toggle.IsChecked == (value!.Value == 1), "Wrong presence state.");
                if (visible && control is ComboBox combo) Check(Equals((combo.SelectedItem as ComboBoxItem)?.Tag, value!.Value), "Wrong timer selection.");
            }
            var status = (TextBlock)group.GetType().GetField("Status", flags)!.GetValue(group)!;
            Check((status.Visibility == Visibility.Visible) == (error is not null), "Readback error was lost.");
        }
        Render("_presenceCard", new(DisplayBackend.Think, new() { ["PresentLeave"] = new(0) }));
        Render("_oledCard", new(DisplayBackend.Spectrum, new() { ["TaskbarDimmer"] = new(-1, false), ["DisplayDimmer"] = new(120) }));
        Render("_oledCard", new(DisplayBackend.Spectrum, new()), "Disconnected");
    }

    internal static async Task Run()
    {
        var actual = Json("""{"errorCode":0,"capacity":true,"capability":530,"sensorType":1,"approachEnabled":false,"presenceLeaveEnabled":null,"videoAutoPauseResumeEnabled":true,"faceIdRegistered":false}""");
        var sources = new List<DisplayBackend>();
        var writes = 0;
        bool disconnect = false;
        Task<JsonObject> Hpd(DisplayBackend source, string contract, string command, JsonObject? payload)
        {
            sources.Add(source);
            if (source != DisplayBackend.DirectHpd || disconnect) throw new InvalidOperationException("offline");
            if (command == "Get-Settings") return Task.FromResult(actual);
            Check(command == "Set-Settings" && payload!["SettingList"]!.AsObject().Count == 1 &&
                payload["SettingList"]!["SetApproachEnabled"]!.ToString() == "true", "Incorrect HPD write.");
            writes++;
            if (writes == 2) { disconnect = true; throw new TimeoutException("write timeout"); }
            return Task.FromResult(Json("""{"errorCode":0}"""));
        }
        var hpd = new DisplayExtras(Hpd);
        var state = await hpd.ReadAsync(false);
        Check(state.Source == DisplayBackend.DirectHpd && state.Values.Count == 2 && state.Values["Approach"].Value == 0 &&
            !state.Values.ContainsKey("PresentLeave") && state.NeedsHello, "Absent, false and Hello values must remain distinct.");
        sources.Clear();
        var ignored = await hpd.SetAsync(state.Source, "Approach", 1, () => true);
        Check(ignored.Error != null && ignored.State.Values["Approach"].Value == 0 && writes == 1 &&
            sources.All(x => x == state.Source), "Unapplied setting was displayed as applied or rerouted.");
        var lost = await hpd.SetAsync(state.Source, "Approach", 1, () => true);
        Check(!lost.State.Available && lost.Error!.Contains("write timeout") && writes == 2, "Lost write reply retried a mutation or retained stale state.");
        actual["capacity"] = false; disconnect = false;
        Check(!(await hpd.ReadAsync(false)).Available, "Unsupported capacity exposed rows.");

        var thinkData = Json("""{"SettingList":[{"key":"Version","value":"0x02000100"},{"key":"Capability","value":"2"},{"key":"Approach","value":"0x2"},{"key":"PresentLeave","value":"0x5"}]}""");
        var think = new DisplayExtras((s, _, cmd, body) =>
        {
            if (s != DisplayBackend.Think) throw new InvalidOperationException("unsupported");
            if (cmd == "Get-HPDSettings") return Task.FromResult(thinkData);
            Check(cmd == "Set-HPDSetting" && body!["SettingList"]![0]!["Setting"]![0]!["value"]!.ToString() == "True", "Wrong Think SET envelope.");
            thinkData["SettingList"]![2]!["value"] = "3";
            return Task.FromResult(Json("""{"ErrorCode":"0"}"""));
        });
        var thinkState = await think.ReadAsync(false);
        Check(thinkState.Values["Approach"].Value == 0 && thinkState.Values["PresentLeave"].Value == 1 && thinkState.Values.Count == 2, "Think numeric encoding failed.");
        Check((await think.SetAsync(thinkState.Source, "Approach", 1, () => true)).Error is null, "Think confirmed SET failed.");
        thinkData["SettingList"]![2]!["value"] = "99";
        Check(!(await think.ReadAsync(false)).Values.ContainsKey("Approach"), "Unknown Think encoding became false.");
        thinkData["SettingList"]![0]!["value"] = "1"; thinkData["SettingList"]![1]!["value"] = "True";
        thinkData["SettingList"]![2]!["value"] = "False";
        Check((await think.ReadAsync(false)).Values["Approach"].Value == 0, "Legacy Think boolean settings lost.");

        var cv = Json("""{"errorCode":0,"capacity":true,"capability":2,"sensorType":4,"approachEnabled":false}""");
        var cvAllowed = false;
        var camera = new DisplayExtras((source, contract, cmd, _) => Task.FromResult(cmd switch
        {
            "Get-Settings" => cv, "Get-HPDStatus" => Json("""{"Camera_Shutter_Supported":"1","Camera_Shutter":"0"}"""),
            "Get-Capability" => Json("""{"capabilityList":[{"capability":[{"key":"PrivacyPermission","value":"Allowed"}]}]}"""),
            _ => new JsonObject()
        }), cvEnabled: () => cvAllowed);
        Check(!(await camera.ReadAsync(false)).Available, "CVDSP policy disabled.");
        cvAllowed = true;
        Check((await camera.ReadAsync(false)).Values["Approach"].Writable == false, "Covered CVDSP camera allowed mutation.");

        var caps = Json("""{"CapabilityList":{"Items":[{"key":"BurningProtectionControl","value":"True"}]}}""");
        var raw = Json("""{"vendorExtension":"keep","SettingList":{"Items":[{"key":"TaskbarDimmer","enabled":"true","trigger":"Running","timer":"0"},{"key":"BackgroundDimmer","enabled":"true","trigger":"None","timer":"0","extension":7},{"key":"Unknown","x":true},{"key":"DisplayDimmer","enabled":"true","trigger":"KeyMouseIdleHalf","timer":"0"}]}}""");
        var saved = raw.DeepClone();
        int mutations = 0;
        var oled = new DisplayExtras(async (_, _, cmd, payload) =>
        {
            await Task.Delay(5);
            if (cmd == "Get-Capability") return caps;
            if (cmd == "Get-DisplaySettings") return raw;
            Check(cmd == "Set-DisplaySettings" && payload!.ToString().Contains("vendorExtension"), "Incomplete Spectrum request.");
            raw = payload!;
            mutations++;
            return Json("""{"ErrorCode":"Success"}""");
        }, () => true);
        var oledState = await oled.ReadAsync(true);
        Check(oledState.Values.Count == 3 && !oledState.Values["TaskbarDimmer"].Writable &&
            oledState.Values["DisplayDimmer"].Value == 300 && oledState.Values["BackgroundDimmer"].Value == 0, "OLED row restrictions or legacy timer misread.");
        var panelChanged = await oled.SetAsync(DisplayBackend.Spectrum, "BackgroundDimmer", 120, () => false);
        Check(mutations == 0 && panelChanged.Error != null, "Invalidated request wrote to a different panel.");
        var changes = await Task.WhenAll(oled.SetAsync(DisplayBackend.Spectrum, "BackgroundDimmer", 120, () => true),
            oled.SetAsync(DisplayBackend.Spectrum, "DisplayDimmer", -1, () => true));
        Check(changes.All(r => r.Error is null) && mutations == 2, "Concurrent timer writes failed.");
        saved["SettingList"]!["Items"]![1]!["trigger"] = "KeyMouseIdle";
        saved["SettingList"]!["Items"]![1]!["timer"] = "120";
        saved["SettingList"]!["Items"]![3]!["trigger"] = "Running";
        Check(JsonNode.DeepEquals(saved, raw), "Spectrum lost another row, order or unknown fields.");
        var rows = raw["SettingList"]!["Items"]!.AsArray();
        rows.Add(rows[1]!.DeepClone()); rows[3]!["errorCode"] = "Failure";
        Check((await oled.ReadAsync(true)).Values.Keys.SequenceEqual(["TaskbarDimmer"]), "Duplicate or failed OLED rows were exposed.");
        rows[0]!["timer"] = "999";
        Check(!(await oled.ReadAsync(true)).Available, "A card with no readable values should be hidden.");
        Check(DisplayExtrasTransport.DecodeReply(DisplayBackend.ZeroTouch, "Set-Settings", "false")["errorCode"]!.ToString() == "-1",
            "ZeroTouch negative acknowledgement was accepted.");
        Console.WriteLine("Presence capability, backend-specific writes, CVDSP policy, OLED transactions and readback tests passed.");
    }
}
