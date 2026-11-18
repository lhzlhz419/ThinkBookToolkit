using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class DriverCatalogVisibilityTests
{
    internal static void Run()
    {
        const string xml = """
            <Package id="example" version="2.0" hide="False">
              <Title><Desc id="EN">Example</Desc></Title>
              <Dependencies><And>
                <_OS><OS>WIN11</OS></_OS>
                <_WindowsBuildVersion><Version>26200</Version></_WindowsBuildVersion>
                <_PnPID>PCI\VEN_1234</_PnPID>
              </And></Dependencies>
              <DetectInstall><_Driver><HardwareID>PCI\VEN_1234</HardwareID><Version>2.0^</Version></_Driver></DetectInstall>
              <Install type="cmd"><Cmdline>%PACKAGEPATH%\example.exe /silent</Cmdline></Install>
              <Files><Installer><File><Name>example.exe</Name>
                <CRC>BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB</CRC>
              </File></Installer></Files>
            </Package>
            """;
        var snapshot = new DriverSystemSnapshot(26300, "R2CN59WW",
            [new InstalledDriverSnapshot(["PCI\\VEN_1234"], "2.0", null)]);
        VerifyCatalogSelectionAsync().GetAwaiter().GetResult();
        var win10 = snapshot with { WindowsBuild = 19045, EditionId = "Professional",
            Drivers = [new InstalledDriverSnapshot(["PCI\\VEN_1234"], "1.0", null)] };
        var anyBuild = xml.Replace("<_WindowsBuildVersion><Version>26200</Version></_WindowsBuildVersion>", "");
        DriverUpdateItem OnWin10(string descriptor, bool fallback = false) =>
            LenovoDriverCatalogService.ParseDescriptor(descriptor, new Uri("https://download.lenovo.com/example.xml"),
                "Driver", win10, "zh-CN", includeFiltered: true, fromFallbackCatalog: fallback)!;
        Check(OnWin10(anyBuild) is { IsApplicable: false, IsRecommendedUpdate: false }, "WIN11 must not match Windows 10.");
        var win10Xml = anyBuild.Replace("WIN11", "WIN10");
        Check(OnWin10(win10Xml).IsRecommendedUpdate && OnWin10(anyBuild.Replace("WIN11", "WIN10-PRO.*")).IsRecommendedUpdate &&
            !OnWin10(anyBuild.Replace("WIN11", "WIN10-ENT")).IsRecommendedUpdate, "Win10 package/edition matching failed.");
        foreach (var descriptor in new[] { anyBuild, win10Xml, anyBuild.Replace("<_OS><OS>WIN11</OS></_OS>", "") })
        {
            var reference = OnWin10(descriptor, fallback: true);
            Check(reference.CanInstall && !reference.IsRecommendedUpdate && reference.ApplicabilityWarning.Contains("Windows 11") &&
                reference.ApplicabilityWarning.Contains("Windows 10") &&
                ToolkitDriverUpdatePage.FilterUpdatesForDisplay([reference], false).Any(),
                "Fallback packages must be visible by default with a confirmation notice, and excluded from bulk updates.");
        }
        DriverUpdateItem Parse(string text, DriverSystemSnapshot? state = null) =>
            LenovoDriverCatalogService.ParseDescriptor(text,
                new Uri("https://download.lenovo.com/consumer/mobiles/example.xml"),
                "Drivers", state ?? snapshot, "zh-CN", includeFiltered: true)!;
        var mismatch = Parse(xml);
        Check(mismatch is { IsApplicable: false, IsUpdateRequired: false, CanInstall: true } &&
            mismatch.ApplicabilityWarning.Contains("26200") && mismatch.ApplicabilityWarning.Contains("26300") &&
            mismatch.ApplicabilityWarning.Contains("谨慎安装"), "Windows mismatch should remain visible with a precise warning and manual installation.");
        var newMismatch = Parse(xml, snapshot with { Drivers = [new InstalledDriverSnapshot(["PCI\\VEN_1234"], "1.0", null)] });
        Check(newMismatch.IsUpdateRequired && !newMismatch.IsRecommendedUpdate,
            "An undeclared OS build must not join the default/bulk update set.");
        var hardwareMismatch = Parse(xml.Replace("<_PnPID>PCI\\VEN_1234</_PnPID>", "<_PnPID>PCI\\VEN_9999</_PnPID>"));
        Check(hardwareMismatch.CanInstall && !hardwareMismatch.IsRecommendedUpdate && hardwareMismatch.ApplicabilityWarning.Contains("前置条件"),
            "A hardware mismatch should allow confirmed individual installation but not bulk installation.");
        var bios = Parse(xml.Replace("<Dependencies><And>", "<Dependencies><And><_Bios><Level>R2CN57W*</Level></_Bios>"));
        Check(bios.CanInstall && !bios.IsRecommendedUpdate, "BIOS prerequisite mismatch should require individual confirmation.");
        var biosXml = """
            <Package id="r2cn59ww" version="R2CN59WW">
              <Dependencies><_Bios><Level>R2CN57W*</Level></_Bios></Dependencies>
              <DetectInstall><_Bios><Level>R2CN59*</Level></_Bios></DetectInstall>
              <Install type="cmd"><Cmdline>%PACKAGEPATH%\r2cn59ww.exe -s</Cmdline></Install>
              <Files><Installer><File><Name>r2cn59ww.exe</Name>
                <CRC>BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB</CRC>
              </File></Installer></Files>
            </Package>
            """;
        var installedBios = Parse(biosXml);
        Check(installedBios is { IsUpdateRequired: false, IsApplicable: false, CanInstall: true, CurrentVersion: "R2CN59WW" } &&
            installedBios.ApplicabilityWarning.Contains("R2CN57W*"),
            "Installed BIOS should be retained even when upgrade-from conditions no longer match.");
        var unknown = Parse(xml.Replace("<_PnPID>PCI\\VEN_1234</_PnPID>", "<_ExternalDetection>check.exe</_ExternalDetection>"));
        var unknownState = Parse(xml.Replace("<DetectInstall><_Driver>", "<DetectInstall><_Unsupported>" ).Replace("</_Driver></DetectInstall>", "</_Unsupported></DetectInstall>"));
        Check(!unknown.CanInstall && !unknownState.IsInstalledStateKnown && !unknownState.CanInstall,
            "Unknown applicability or installed-state rules must remain visible without being installable.");
        var malformed = Parse(xml.Replace("<Version>26200</Version>", "<Version>invalid</Version>"));
        var hidden = Parse(xml.Replace("hide=\"False\"", "hide=\"True\""));
        var invalidPlan = Parse(xml.Replace("<CRC>BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB</CRC>", "<CRC>bad</CRC>"));
        Check(!malformed.CanInstall && !hidden.CanInstall && !invalidPlan.CanInstall && invalidPlan.InstallPlan is null,
            "Invalid build rules, hidden packages and invalid install plans must not become installable.");
        var applicable = Parse(xml, snapshot with { WindowsBuild = 26200, Drivers = [new InstalledDriverSnapshot(["PCI\\VEN_1234"], "1.0", null)] });
        DriverUpdateItem[] all = [mismatch, newMismatch, hardwareMismatch, installedBios, unknown, unknownState, malformed, hidden, invalidPlan, applicable];
        Check(ToolkitDriverUpdatePage.FilterUpdatesForDisplay(all, true).Count() == all.Length &&
            ToolkitDriverUpdatePage.FilterUpdatesForDisplay(all, false).SequenceEqual([applicable]),
            "The toggle must reveal all verified entries, while the normal list contains only recommended updates.");
        using var runtime = new ToolkitRuntimeService(new AppSettings { Language = "zh-CN" }, persistSystemSessionState: false);
        using var page = new ToolkitDriverUpdatePage(runtime);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pageType = typeof(ToolkitDriverUpdatePage);
        pageType.GetField("_scanResults", flags)!.SetValue(page, all);
        pageType.GetField("_hasScanned", flags)!.SetValue(page, true);
        var toggle = (CheckBox)pageType.GetField("_showUpToDate", flags)!.GetValue(page)!;
        var render = pageType.GetMethod("RenderVisibleUpdates", flags)!;
        toggle.IsChecked = true;
        render.Invoke(page, null);
        var buttons = (Dictionary<DriverUpdateItem, System.Windows.Controls.Button>)pageType.GetField("_downloadButtons", flags)!.GetValue(page)!;
        Check(buttons.Count == all.Length && buttons[hardwareMismatch].IsEnabled && buttons[installedBios].IsEnabled &&
            buttons[mismatch].IsEnabled && !buttons[unknown].IsEnabled && !buttons[invalidPlan].IsEnabled,
            "Rendered rows did not preserve display/installation eligibility for filtered packages.");
        toggle.IsChecked = false;
        render.Invoke(page, null);
        Check(buttons.Count == 1 && buttons.ContainsKey(applicable), "Turning off the toggle left filtered rows visible.");
        Console.WriteLine("Filtered driver visibility, Windows compatibility warnings, BIOS state and installation eligibility tests passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task VerifyCatalogSelectionAsync()
    {
        var catalog = Encoding.UTF8.GetBytes("<packages count='1'><package><location>https://download.lenovo.com/test.xml</location><category>Driver</category><checksum>" +
            new string('A', 64) + "</checksum></package></packages>");
        var calls = new List<string>();
        async Task<bool> Select(int build, Func<byte[]> first)
        {
            calls.Clear();
            var selected = await LenovoDriverCatalogService.ReadCatalogAsync("21R0", build, (uri, _) =>
            {
                calls.Add(uri.AbsolutePath);
                return Task.FromResult(calls.Count == 1 ? first() : catalog);
            }, CancellationToken.None);
            return selected.Fallback;
        }
        Check(!await Select(19045, () => catalog) && calls.SequenceEqual(["/catalog/21R0_Win10.xml"]), "Win10 must prefer its native catalog.");
        Check(!await Select(26100, () => catalog) && calls.SequenceEqual(["/catalog/21R0_Win11.xml"]), "Win11 must read its native catalog directly.");
        foreach (var missing in new Func<byte[]>[] {
            () => Encoding.UTF8.GetBytes("<packages count='0'/>"),
            () => throw new HttpRequestException("missing", null, HttpStatusCode.NotFound),
            () => throw new HttpRequestException("gone", null, HttpStatusCode.Gone) })
            Check(await Select(19045, missing) && calls.SequenceEqual(["/catalog/21R0_Win10.xml", "/catalog/21R0_Win11.xml"]),
                "Missing/empty Win10 catalog must fall back exactly once.");
        try { await Select(19045, () => throw new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable)); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable && calls.Count == 1) { return; }
        throw new InvalidOperationException("Network failures must not select a different OS catalog.");
    }
}
