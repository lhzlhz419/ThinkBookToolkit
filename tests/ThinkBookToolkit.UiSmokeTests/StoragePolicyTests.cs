using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class StoragePolicyTests
{
    internal static void Run()
    {
        Check(new AppSettings().LogRetentionDays == 7 && new SensorRecordingSettings().RetentionDays == 0,
            "Storage retention defaults are incorrect.");
        Check(FileRetentionPolicy.Days.SequenceEqual(new[] { 1, 2, 3, 5, 7, 14, 30, 60, 0 }),
            "Retention choices do not match the supported days.");
        Check(CurveProfileStore.NormalizeSensorRecordingSettings(new() { RetentionDays = 12 }).RetentionDays == 0 &&
              FileRetentionPolicy.Normalize(-1, 7) == 7 && FileRetentionPolicy.Normalize(0, 7) == 0,
            "Invalid retention values or Forever were normalized incorrectly.");
        var root = Path.Combine(Environment.CurrentDirectory, ".tmp", "storage-policy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = DateTime.UtcNow;
        foreach (var recording in new[] { false, true })
        {
            var folder = Path.Combine(root, recording ? "recordings" : "logs");
            Directory.CreateDirectory(folder);
            string Name(int id) => recording ? $"sensors-20200101-000000-{id:000}.jsonl" : $"2020-01-01_00-00-00-{id:000}_123.log";
            string FileAt(int id, int days)
            {
                var file = Path.Combine(folder, Name(id));
                File.WriteAllText(file, "owned test file");
                File.SetLastWriteTimeUtc(file, now.AddDays(-days));
                return file;
            }
            var expired = FileAt(1, 10);
            var recent = FileAt(2, 1);
            var current = FileAt(3, 90);
            var active = FileAt(4, 90);
            var unrelated = Path.Combine(folder, "unrelated.txt");
            File.WriteAllText(unrelated, "keep"); File.SetLastWriteTimeUtc(unrelated, now.AddDays(-90));
            using (var writer = new FileStream(active, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                Check(FileRetentionPolicy.Cleanup(folder, 7, recording, current, now) == 1,
                    "Retention did not delete exactly the expired closed file.");
                Check(!File.Exists(expired) && File.Exists(active) && File.Exists(current) && File.Exists(recent) && File.Exists(unrelated),
                    "Cleanup removed a recent, current, active, or unrelated file.");
            }
            Check(FileRetentionPolicy.Cleanup(folder, 0, recording, utcNow: now) == 0 && File.Exists(active),
                "Forever/Do not delete still removes files.");
            Check(FileRetentionPolicy.Cleanup(folder, 7, recording, current, now) == 1 && !File.Exists(active),
                "A previously active expired file was not eligible after closing.");
            if (recording)
            {
                var archiveSource = FileAt(5, 30);
                var timestamp = File.GetLastWriteTimeUtc(archiveSource);
                var archive = SensorRecordingArchive.CompressAndDeleteSource(archiveSource);
                Check(File.GetLastWriteTimeUtc(archive) == timestamp, "Compression changed the retention age.");
                FileRetentionPolicy.Cleanup(folder, 7, true, current, now);
                Check(!File.Exists(archive), "Expired compressed recordings were not cleaned up.");
            }
        }
        var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        var original = Path.Combine(source, "app_settings.csharp.json");
        File.WriteAllText(original, "original");
        File.SetLastWriteTimeUtc(original, now.AddDays(-20));
        var excluded = Path.Combine(source, "log"); Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(excluded, "keep.log"), "log");
        ToolkitStoragePaths.CopyPreservingExisting(source, target, true, [excluded]);
        var copy = Path.Combine(target, Path.GetFileName(original));
        Check(File.ReadAllText(original) == "original" && File.ReadAllText(copy) == "original" &&
              File.GetLastWriteTimeUtc(copy) == File.GetLastWriteTimeUtc(original) && !Directory.Exists(Path.Combine(target, "log")),
            "Directory migration lost original files, ages, or exclusions.");
        ToolkitStoragePaths.CopyPreservingExisting(source, target, true, [excluded]);
        File.WriteAllText(copy, "destination data");
        try
        {
            ToolkitStoragePaths.CopyPreservingExisting(source, target, true, [excluded]);
            throw new Exception("Conflicting migration unexpectedly succeeded.");
        }
        catch (IOException) { }
        Check(File.ReadAllText(original) == "original" && File.ReadAllText(copy) == "destination data",
            "Migration overwrote a conflicting file.");

        using var runtime = new ToolkitRuntimeService(new AppSettings());
        var page = new ToolkitSettingsPage(runtime);
        var logChoices = Field<ComboBox>(page, "_logRetention");
        Check(logChoices.Items.OfType<ComboBoxItem>().Select(x => (int)x.Tag).SequenceEqual(FileRetentionPolicy.Days) &&
              logChoices.SelectedItem is ComboBoxItem { Tag: 7 }, "Log retention UI choices/default are incorrect.");
        var folders = new FolderLocationsWindow(runtime);
        var sensors = new SensorRecordingSettingsWindow(runtime);
        try
        {
            Check(Field<TextBox[]>(folders, "_paths").Length == 5,
                "Custom folder settings must expose all five locations.");
            Check(Field<TextBox[]>(folders, "_paths").All(path => !string.IsNullOrWhiteSpace(path.Text)),
                "Custom folder settings contains an empty effective path.");
            Check(Field<ComboBox>(sensors, "_retentionDays").Items.Count == 9,
                "Recording retention UI is missing choices.");
            folders.Show();
            folders.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            folders.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(folders.ActualWidth),
                (int)Math.Ceiling(folders.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(folders);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = File.Create(Path.Combine(Environment.CurrentDirectory, ".tmp", "storage-policy-tests", "folder-locations.png"));
            encoder.Save(image);
        }
        finally { folders.Close(); sensors.Close(); page.Dispose(); }
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
