using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ThinkBookToolkit;
using ThinkBookToolkit.PluginApi;
using ThinkBookToolkit.PluginTest;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class PluginImportTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "plugin-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pluginRoot = Path.Combine(root, "plugins"); var state = Path.Combine(root, "state"); Directory.CreateDirectory(state);
        var preferences = Path.Combine(state, "enabled.json");
        File.WriteAllText(preferences, JsonSerializer.Serialize(new Dictionary<string, PluginPreference> { ["test.previous"] = new(true, "previous-approval") }));
        var options = new PluginManagerOptions(pluginRoot, state, BundledRoot: Path.Combine(root, "bundled"));
        using var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options);
        await runtime.Plugins.DiscoverAsync();
        var catalogChanges = 0; runtime.Plugins.CatalogChanged += (_, _) => catalogChanges++;
        var sequence = 0;
        var binary = File.ReadAllBytes(typeof(AverageFanPlugin).Assembly.Location);
        PluginManifest Manifest(string id) => new(id, "Import fixture", "1.0", 1, "plugin.dll", typeof(AverageFanPlugin).FullName!, [], [],
            [new("flag", "settings", new("开关", "Flag"), "boolean", JsonSerializer.SerializeToElement(false))], []) { Author = "Import author" };
        (string Name, byte[] Bytes)[] Files(PluginManifest m, string prefix = "") =>
            [(prefix + "plugin.json", JsonSerializer.SerializeToUtf8Bytes(m)), (prefix + "plugin.dll", binary)];
        string Zip(IEnumerable<(string Name, byte[] Bytes)> files)
        {
            var path = Path.Combine(root, "input-" + sequence++ + ".zip");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, bytes) in files) { var entry = zip.CreateEntry(name); using var output = entry.Open(); output.Write(bytes); }
            return path;
        }
        var firstManifest = Manifest("test.import-root");
        var firstZip = Zip(Files(firstManifest));
        var first = await runtime.Plugins.ImportAsync(firstZip);
        Check(!first.Enabled && first.Client is null && first.Manifest.Author == "Import author" && !first.Settings["flag"].GetBoolean(),
            "Import executed code, enabled the plugin, or lost metadata/defaults.");
        Check(File.Exists(firstZip) && File.Exists(Path.Combine(first.Directory, "plugin.dll")), "Import lost the original archive or the installed binary.");
        var second = await runtime.Plugins.ImportAsync(Zip(Files(Manifest("test.import-wrapped"), "PluginFolder/")));
        Check(File.Exists(Path.Combine(second.Directory, "plugin.json")) && catalogChanges == 2, "Wrapped import failed or catalog did not update immediately.");
        var saved = JsonSerializer.Deserialize<Dictionary<string, PluginPreference>>(File.ReadAllText(preferences))!;
        Check(saved["test.previous"].Enabled && !saved[first.Manifest.Id].Enabled && !saved[second.Manifest.Id].Enabled, "Import altered other approvals or enabled new code.");
        var before = File.ReadAllText(preferences);
        await Reject(() => runtime.Plugins.ImportAsync(firstZip));
        Check(File.ReadAllText(preferences) == before && File.ReadAllBytes(Path.Combine(first.Directory, "plugin.dll")).SequenceEqual(binary), "Duplicate import changed installed files or preferences.");

        var invalid = Manifest("test.invalid");
        foreach (var name in new[] { "../outside.dll", "..\\outside.dll", "/outside.dll", "C:/outside.dll", "stream:payload", "folder./bad", "NUL.txt" })
            await Reject(() => runtime.Plugins.ImportAsync(Zip(Files(invalid).Append((name, new byte[] { 1 })))));
        await Reject(() => runtime.Plugins.ImportAsync(Zip(Files(invalid).Append(("PLUGIN.DLL", binary)))));
        await Reject(() => runtime.Plugins.ImportAsync(Zip(Files(invalid, "one/").Concat(Files(invalid, "two/")))));
        await Reject(() => runtime.Plugins.ImportAsync(Zip(Files(invalid with { ApiVersion = 999 }))));
        await Reject(() => runtime.Plugins.ImportAsync(Zip([("plugin.json", JsonSerializer.SerializeToUtf8Bytes(invalid))])));
        await Reject(() => runtime.Plugins.ImportAsync(Zip([("plugin.json", new byte[1024 * 1024 + 1]), ("plugin.dll", binary)])));
        await Reject(() => runtime.Plugins.ImportAsync(Zip(Files(invalid).Concat(Enumerable.Range(0, 2049).Select(i => ("file" + i, Array.Empty<byte>()))))));
        var linkZip = Zip(Files(invalid));
        using (var zip = ZipFile.Open(linkZip, ZipArchiveMode.Update)) zip.CreateEntry("link").ExternalAttributes = unchecked((int)0xA1FF0000);
        await Reject(() => runtime.Plugins.ImportAsync(linkZip));
        var occupied = Path.Combine(pluginRoot, "test.occupied"); Directory.CreateDirectory(occupied); File.WriteAllText(Path.Combine(occupied, "keep.txt"), "keep");
        await Reject(() => runtime.Plugins.ImportAsync(Zip(Files(Manifest("test.occupied")))));
        Check(File.ReadAllText(Path.Combine(occupied, "keep.txt")) == "keep", "Existing directory was overwritten.");
        var manual = Path.Combine(pluginRoot, "ManuallyInstalled"); Directory.CreateDirectory(manual);
        File.WriteAllText(Path.Combine(manual, "plugin.json"), JsonSerializer.Serialize(Manifest("test.manual")));
        await Reject(() => runtime.Plugins.ImportAsync(Zip(Files(Manifest("test.manual")))));
        Check(!File.Exists(Path.Combine(root, "outside.dll")) && !Directory.Exists(Path.Combine(pluginRoot, invalid.Id)), "Rejected ZIP escaped staging or installed files.");
        Check(runtime.Plugins.Installations.Count == 2 && File.ReadAllText(preferences) == before && catalogChanges == 2, "Rejected import changed the plugin catalog or preferences.");
        Check(!Directory.EnumerateFileSystemEntries(Path.Combine(pluginRoot, ".imports")).Any(), "Import staging directories were left behind.");

        using var safe = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false, pluginOptions: options);
        await safe.Plugins.InitializeAsync(safeMode: true);
        var safePlugin = await safe.Plugins.ImportAsync(Zip(Files(Manifest("test.safe-import"))));
        saved = JsonSerializer.Deserialize<Dictionary<string, PluginPreference>>(File.ReadAllText(preferences))!;
        Check(!safePlugin.Enabled && saved["test.previous"].Enabled && saved.ContainsKey(first.Manifest.Id), "Safe-mode import erased unrelated preferences.");
        Console.WriteLine("Plugin ZIP import validation/disabled-state/no-overwrite tests passed: " + root);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task<PluginInstallation>> action)
    {
        try { await action(); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or JsonException) { return; }
        throw new InvalidOperationException("Invalid plugin archive was accepted.");
    }
}
