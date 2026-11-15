using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
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
}
