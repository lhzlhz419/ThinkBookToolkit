using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class ThemeColorTests
{
    internal static void Run()
    {
        var defaults = new ThemeColorSettings();
        var custom = defaults with { LightAccent = "D05519", DarkAccent = "45CFA1",
            LightBackground = "F4E8DA", DarkBackground = "152C27",
            LightHeroStart = "FFF1CE", LightHeroEnd = "F8C99A", DarkHeroStart = "164D52", DarkHeroEnd = "2D275D" };
        Check(ThemeColorSettings.Normalize(null) == defaults, "Old configurations lost default colors.");
        var old = JsonSerializer.Deserialize<ThemeColorSettings>("{\"LightAccent\":\"D05519\"}")!;
        Check(old.LightBackground == defaults.LightBackground && old.DarkBackground == defaults.DarkBackground,
            "Old accent-only configurations lost background defaults.");
        Check(ThemeColorSettings.Normalize(custom with { LightAccent = "#d05519", DarkAccent = "invalid" }) ==
            custom with { DarkAccent = defaults.DarkAccent }, "Color normalization failed.");
        using var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false);
        var original = runtime.Settings.ThemeColors;
        try
        {
            Check(!runtime.TrySetThemeColors(custom with { LightHeroStart = "bad" }, out _) && runtime.Settings.ThemeColors == original,
                "Invalid colors partially changed the settings.");
            Check(!runtime.TrySetThemeColors(custom with { DarkBackground = "invalid" }, out _) && runtime.Settings.ThemeColors == original,
                "Invalid background partially changed settings.");
            Check(runtime.TrySetThemeColors(custom, out _), "Saving custom colors failed.");
            Check(CurveProfileStore.LoadSettings().ThemeColors == custom, "Custom colors did not survive settings reload.");
            ToolkitPalette.Configure(custom);
            Check(ToolkitPalette.For(false).Accent == "#D05519" && ToolkitPalette.For(true).Accent == "#45CFA1" &&
                ToolkitPalette.For(false).Canvas == "#F4E8DA" && ToolkitPalette.For(true).Canvas == "#152C27", "Palette did not preserve independent backgrounds.");
            foreach (var dark in new[] { false, true })
            {
                var opaque = ToolkitPalette.For(dark);
                var transparent = ToolkitPalette.For(dark, true);
                var expectedAlpha = dark ? new byte[] { 0xCC, 0x59, 0x80 } : new byte[] { 0x99, 0x40, 0x66 };
                var solids = new[] { opaque.Sidebar, opaque.Surface, opaque.SurfaceRaised };
                var overlays = new[] { transparent.Sidebar, transparent.Surface, transparent.SurfaceRaised };
                for (var i = 0; i < solids.Length; i++)
                {
                    var color = (Color)ColorConverter.ConvertFromString(solids[i]);
                    color.A = expectedAlpha[i];
                    Check((Color)ColorConverter.ConvertFromString(overlays[i]) == color, "Wallpaper discarded the custom surface tint or alpha.");
                }
                Check(opaque.Canvas != opaque.Surface && opaque.Surface != opaque.SurfaceRaised, "Custom background lost its surface hierarchy.");
            }
            foreach (var hex in new[] { "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "747474", "757575", "767676", "777777" })
            {
                var palette = ToolkitPalette.For(false, custom with { LightBackground = hex });
                foreach (var background in new[] { palette.Canvas, palette.CanvasAlt, palette.Sidebar, palette.Surface, palette.SurfaceRaised })
                    Check(ToolkitPalette.Contrast(palette.Text, background) >= 4.49 && ToolkitPalette.Contrast(palette.Muted, background) >= 4.49,
                        "Unreadable body/secondary text on custom background " + hex);
            }
            Check(custom.HeroText(false) == "#101010" && custom.HeroText(true) == "#FFFFFF", "Hero text contrast failed.");
            Check(ToolkitPalette.For(false, custom with { LightAccent = "FFFFFF" }).AccentText == "#101010", "Light accent has unreadable text.");
            ToolkitPalette.Configure(custom with { LightAccent = "FFFFFF" });
            var disclaimer = new ApplicationDisclaimerWindow("zh-CN", false);
            var picker = new ToolkitColorPickerWindow("FFFFFF", "zh-CN", false);
            try
            {
                foreach (var dialog in new Window[] { disclaimer, picker })
                {
                    dialog.ShowActivated = false;
                    dialog.Show();
                    dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    dialog.Measure(new Size(780, 650));
                    dialog.Arrange(new Rect(0, 0, 780, 650));
                    dialog.UpdateLayout();
                    var accept = Descendants(dialog).OfType<Button>().Single(b => b.IsDefault);
                    Check(((SolidColorBrush)accept.Foreground).Color == (Color)ColorConverter.ConvertFromString("#101010"),
                        "Dialog confirmation is unreadable on a white custom accent.");
                }
            }
            finally { disclaimer.Close(); picker.Close(); ToolkitPalette.Configure(custom); }

            var output = Path.Combine(Environment.CurrentDirectory, ".tmp", "theme-colors", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            foreach (var dark in new[] { false, true })
            {
                runtime.Settings.Theme = dark ? "dark" : "light";
                ModernTheme.Apply(Application.Current, dark);
                var dialog = new ThemeColorSettingsWindow(runtime) { ShowActivated = false };
                dialog.Show();
                dialog.SetDraft(custom);
                dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                dialog.UpdateLayout();
                Check(Descendants(dialog).OfType<TextBlock>().Count(t => t.Text.StartsWith("#", StringComparison.Ordinal)) == 8,
                    "Theme color editor is missing independent swatches.");
                var image = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(dialog);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(output, dark ? "dark.png" : "light.png"))) encoder.Save(file);
                var scroll = Descendants(dialog).OfType<ScrollViewer>().First(s => s.Content is StackPanel);
                scroll.ScrollToBottom();
                dialog.UpdateLayout();
                image = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(dialog);
                encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(output, dark ? "dark-bottom.png" : "light-bottom.png"))) encoder.Save(file);
                var reset = Descendants(dialog).OfType<Button>().Single(b => b.Content?.ToString() == "恢复默认");
                reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var draft = (ThemeColorSettings)typeof(ThemeColorSettingsWindow).GetField("_draft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                Check(draft == defaults && runtime.Settings.ThemeColors == custom, "Reset preview leaked into settings before Apply.");
                dialog.Close();
            }
            var main = new ToolkitMainWindow(runtime, enableHardwareDetection: false);
            main.Show();
            try
            {
                main.NavigateForTesting("settings");
                main.UpdateLayout();
                var labels = Descendants(main.CurrentPage!).OfType<TextBlock>().Select(t => t.Text).ToList();
                Check(labels.IndexOf("自定义文件夹位置") < labels.IndexOf("主题颜色") &&
                    labels.IndexOf("主题颜色") < labels.IndexOf("背景图像"), "Theme color setting is in the wrong position.");
                main.NavigateForTesting("overview");
                main.UpdateLayout();
                Check(Hero(main).GradientStops[0].Color == (Color)ColorConverter.ConvertFromString("#164D52"), "Dark overview did not use saved colors.");
                var next = custom with { DarkAccent = "A660DC", DarkHeroStart = "503060", DarkBackground = "241B2A" };
                Check(runtime.TrySetThemeColors(next, out _), "Applying edited colors failed.");
                main.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(ToolkitPalette.For(true).Accent == "#A660DC" &&
                    ((SolidColorBrush)main.Background).Color == (Color)ColorConverter.ConvertFromString("#241B2A") &&
                    Hero(main).GradientStops[0].Color == (Color)ColorConverter.ConvertFromString("#503060"),
                    "Applying colors did not rebuild the visible page.");
                Check(runtime.TrySetTheme("light", out _), "Switching theme failed.");
                main.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(ToolkitPalette.For(false).Accent == "#D05519" &&
                    ((SolidColorBrush)main.Background).Color == (Color)ColorConverter.ConvertFromString("#F4E8DA") &&
                    Hero(main).GradientStops[0].Color == (Color)ColorConverter.ConvertFromString("#FFF1CE"),
                    "Switching theme did not select the independent light colors.");
            }
            finally
            {
                typeof(ToolkitMainWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
                // Production closes the application with its main window.
                // Keep the shared smoke-test dispatcher alive for later tests.
                var application = Application.Current;
                var previousMode = application.ShutdownMode;
                var previousMain = application.MainWindow;
                var sentinel = new Window();
                application.MainWindow = sentinel;
                application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                try { main.Close(); }
                finally
                {
                    application.ShutdownMode = previousMode;
                    application.MainWindow = previousMain;
                    sentinel.Close();
                }
            }
            Check(runtime.TrySetThemeColors(defaults, out _) && CurveProfileStore.LoadSettings().ThemeColors == defaults, "Restoring default colors did not persist.");
            ToolkitPalette.Configure(defaults);
            Check(ToolkitPalette.For(false).Canvas == "#F4F7FB" && ToolkitPalette.For(true).Canvas == "#0A1020" &&
                ToolkitPalette.For(true, true).Surface == "#59151F33" && ToolkitPalette.For(false, true).Surface == "#40FFFFFF",
                "Restoring defaults changed the original palette.");
            Console.WriteLine("Theme color persistence, defaults, previews and contrast passed: " + output);
        }
        finally
        {
            runtime.TrySetThemeColors(original, out _);
            ToolkitPalette.Configure(new ThemeColorSettings());
            ModernTheme.Apply(Application.Current, false);
        }
    }

    private static LinearGradientBrush Hero(ToolkitMainWindow window) =>
        (LinearGradientBrush)Descendants(window.CurrentPage!).OfType<Border>().Single(b => b.MinHeight == 172).Background;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
