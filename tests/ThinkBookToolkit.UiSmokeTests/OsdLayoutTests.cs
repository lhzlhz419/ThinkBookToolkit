using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class OsdLayoutTests
{
    internal static void Run(ToolkitRuntimeService runtime, ToolkitOsdWindow osd)
    {
        var remembered = new OsdMonitorPlacement("DISPLAY2", "stable-external", 24, 16);
        var primary = new OsdMonitor("DISPLAY1", "primary", new Rect(0, 0, 1920, 1080));
        var returned = new OsdMonitor("DISPLAY3", "stable-external", new Rect(-2560, 0, 2560, 1440));
        Check(OsdMonitorPolicy.Find(remembered, [primary]) is null &&
              OsdMonitorPolicy.Find(remembered, [primary, returned]) == returned,
            "A sleeping external monitor was replaced with primary or not recognized after renumbering.");
        foreach (var width in new[] { 120d, 799.5, 1600 })
        {
            Check(OsdPlacementPolicy.Position(0, width, -1920, 0, OsdSnapAnchor.End) == -width,
                "Right-anchored OSD did not preserve the right edge on resize.");
            Check(OsdPlacementPolicy.Position(0, width, -1920, 0, OsdSnapAnchor.Center) + width / 2 == -960,
                "Centered OSD moved away from the monitor center on resize.");
        }
        Check(OsdPlacementPolicy.Snap(802, 400, 0, 2000, 20) == OsdSnapAnchor.Center &&
              OsdPlacementPolicy.Snap(1590, 400, 0, 2000, 20) == OsdSnapAnchor.End &&
              OsdPlacementPolicy.Snap(500, 400, 0, 2000, 20) == OsdSnapAnchor.None &&
              OsdPlacementPolicy.Snap(800, 400, 0, 2000, 0) == OsdSnapAnchor.None,
            "OSD center/edge snap detection is incorrect.");
        var normalized = CurveProfileStore.NormalizeOsdSettings(new ToolkitOsdSettings
        {
            HorizontalXAnchor = OsdSnapAnchor.End,
            VerticalXAnchor = OsdSnapAnchor.Center,
            HorizontalYAnchor = (OsdSnapAnchor)99,
            HorizontalMonitor = remembered
        });
        Check(normalized.HorizontalXAnchor == OsdSnapAnchor.End &&
              normalized.VerticalXAnchor == OsdSnapAnchor.Center && normalized.HorizontalYAnchor == OsdSnapAnchor.None,
            "OSD normalization lost independent anchors or accepted an invalid value.");
        var clone = (ToolkitOsdSettings)typeof(OsdSettingsWindow).GetMethod("Clone",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [normalized])!;
        Check(clone.HorizontalXAnchor == OsdSnapAnchor.End && clone.VerticalXAnchor == OsdSnapAnchor.Center &&
              clone.HorizontalMonitor == remembered,
            "Opening OSD settings lost the saved anchors.");

        var settings = runtime.Settings.Osd;
        var originalOrientation = settings.Orientation;
        var originalSensors = settings.Sensors;
        var originalFontSize = settings.FontSize;
        var originalPlacement = (settings.HorizontalX, settings.HorizontalY,
            settings.HorizontalXAnchor, settings.HorizontalYAnchor, settings.HorizontalMonitor, settings.VerticalMonitor);
        try
        {
            settings.Orientation = OsdOrientation.Horizontal;
            settings.FontSize = 24;
            osd.ApplySettings();
            osd.RefreshForTesting();
            var naturalHeight = Measure(osd, new Size(4000, 1000), 1).Height;
            foreach (var dpi in new[] { 1d, 1.25, 1.5, 2 })
            {
                var narrow = Measure(osd, new Size(520, 400), dpi);
                Check(narrow.Width <= 520 && narrow.Height <= 400 && narrow.Height > naturalHeight,
                    "Over-wide horizontal OSD did not wrap/fit within the monitor.");
                AssertContained(osd);
                // Even a viewport too short for all sensors must not scale
                // the configured text size to make the content fit.
                Measure(osd, new Size(520, 60), dpi);
                AssertContained(osd);
                var scroll = (ScrollViewer)((Border)((Viewbox)osd.Content).Child).Child;
                Check(scroll.ScrollableHeight > 0 && osd.FontSize == 24,
                    "Overflow should scroll without changing OSD font size.");
            }
            Measure(osd, new Size(520, 400), 1);
            var output = Path.Combine(Environment.CurrentDirectory, ".tmp", "osd-layout-tests");
            Directory.CreateDirectory(output);
            SaveImage(osd, Path.Combine(output, "horizontal-wrapped.png"));
            var expandedWidth = osd.Width;
            settings.Sensors = [OsdSensor.CpuTemperature];
            osd.ApplySettings();
            osd.RefreshForTesting();
            var reduced = Measure(osd, new Size(520, 400), 1);
            Check(reduced.Width < expandedWidth, "OSD width was left stale after hiding sensors.");
            AssertContained(osd);
            SaveImage(osd, Path.Combine(output, "horizontal-short.png"));
            settings.Sensors = originalSensors;
            osd.ApplySettings();
            osd.RefreshForTesting();
            Measure(osd, new Size(1000, 600), 1.5);
            Check(osd.Width > reduced.Width, "OSD did not grow when sensors returned.");
            AssertContained(osd);
            SaveImage(osd, Path.Combine(output, "horizontal-expanded.png"));
            CheckContentDragging(runtime, osd);
            CheckNativeResize(runtime, osd, originalSensors);
        }
        finally
        {
            settings.Orientation = originalOrientation;
            settings.Sensors = originalSensors;
            settings.FontSize = originalFontSize;
            (settings.HorizontalX, settings.HorizontalY, settings.HorizontalXAnchor, settings.HorizontalYAnchor,
                settings.HorizontalMonitor, settings.VerticalMonitor) = originalPlacement;
            osd.ApplySettings();
            osd.RefreshForTesting();
        }
    }

    private static void CheckContentDragging(ToolkitRuntimeService runtime, ToolkitOsdWindow osd)
    {
        Check(!osd.IsLoaded, "Synthetic dragging test must not persist real window positions.");
        var drag = osd.DragWindow;
        var settings = runtime.Settings.Osd;
        var original = (settings.FixedPosition, settings.HorizontalXAnchor, settings.HorizontalYAnchor);
        var calls = 0;
        osd.DragWindow = () => calls++;
        try
        {
            settings.FixedPosition = false;
            var background = (Border)((Viewbox)osd.Content).Child;
            var text = FindVisual<TextBlock>((ScrollViewer)background.Child)!;
            Check(text is not null, "No OSD text found for the routed-input test.");
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            text!.RaiseEvent(down);
            Check(calls == 1 && down.Handled, "Clicking OSD text did not start dragging before ScrollViewer handled the event.");
            background.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(calls == 2, "Clicking the OSD background no longer starts dragging.");
            Check(!osd.CanStartDrag(new ScrollBar(), MouseButton.Left) &&
                  !osd.CanStartDrag(new Thumb(), MouseButton.Left) &&
                  !osd.CanStartDrag(text, MouseButton.Right),
                "Dragging interferes with scrollbars or non-left clicks.");
            settings.FixedPosition = true;
            text.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(calls == 2, "Fixed-position OSD still starts a drag.");
        }
        finally
        {
            osd.DragWindow = drag;
            (settings.FixedPosition, settings.HorizontalXAnchor, settings.HorizontalYAnchor) = original;
        }
    }

    private static T? FindVisual<T>(DependencyObject source) where T : DependencyObject
    {
        if (source is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(source, i)) is { } child) return child;
        return null;
    }

    private static void CheckNativeResize(ToolkitRuntimeService runtime, ToolkitOsdWindow osd,
        System.Collections.Generic.List<OsdSensor> sensors)
    {
        var settings = runtime.Settings.Osd;
        settings.HorizontalX = SystemParameters.WorkArea.Left + 10;
        settings.HorizontalY = SystemParameters.WorkArea.Top + 10;
        settings.HorizontalXAnchor = OsdSnapAnchor.End;
        osd.Show();
        // This is a layout test with fixture data, not a hardware polling test.
        ((DispatcherTimer)typeof(ToolkitOsdWindow).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(osd)!).Stop();
        foreach (var anchor in new[] { OsdSnapAnchor.End, OsdSnapAnchor.Center })
        {
            settings.HorizontalXAnchor = anchor;
            foreach (var expanded in new[] { false, true, false, true })
            {
                settings.Sensors = expanded ? sensors : [OsdSensor.CpuTemperature];
                osd.ApplySettings();
                osd.RefreshForTesting();
                object[] args = [Rect.Empty, Rect.Empty];
                Check((bool)typeof(ToolkitOsdWindow).GetMethod("TryGetScreen",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(osd, args)!,
                    "Could not inspect the native OSD window rectangle.");
                var work = (Rect)args[0];
                var window = (Rect)args[1];
                Check(window.Left >= work.Left - 1 && window.Right <= work.Right + 1 &&
                      window.Top >= work.Top - 1 && window.Bottom <= work.Bottom + 1,
                    "The native OSD extends off-screen after resizing.");
                var offset = anchor == OsdSnapAnchor.End ? window.Right - work.Right :
                    (window.Left + window.Right - work.Left - work.Right) / 2;
                Check(Math.Abs(offset) <= 1, "The native OSD lost its right/center anchor when content changed.");
                AssertContained(osd);
            }
        }
        var savedMonitor = settings.HorizontalMonitor;
        var savedX = settings.HorizontalX;
        var savedY = settings.HorizontalY;
        var monitorProvider = osd.MonitorProvider;
        var powerHandler = typeof(ToolkitOsdWindow).GetMethod("OnPowerModeChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        powerHandler.Invoke(osd, [osd, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Suspend)]);
        Check(!osd.IsVisible && settings.HorizontalMonitor == savedMonitor,
            "Suspend did not preserve the OSD's preferred display.");
        osd.MonitorProvider = () => [];
        powerHandler.Invoke(osd, [osd, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Resume)]);
        Check(!osd.IsVisible && settings.HorizontalMonitor == savedMonitor,
            "OSD was shown on primary while its preferred monitor was unavailable.");
        osd.Left += 50; // Simulate the OS relocating the hidden HWND during resume.
        osd.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(settings.HorizontalMonitor == savedMonitor && settings.HorizontalX == savedX && settings.HorizontalY == savedY,
            "Automatic window relocation overwrote the user's monitor or position.");
        osd.MonitorProvider = monitorProvider;
        osd.ShowIfSessionUnlocked();
        Check(osd.IsVisible && settings.HorizontalMonitor == savedMonitor,
            "OSD did not return when the preferred monitor became available again.");
        ((DispatcherTimer)typeof(ToolkitOsdWindow).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(osd)!).Stop();
        osd.Hide();
    }

    private static Size Measure(ToolkitOsdWindow osd, Size available, double dpi)
    {
        osd.ResizeContent(available, new DpiScale(dpi, dpi));
        var view = (Viewbox)osd.Content;
        view.Measure(new Size(osd.Width, osd.Height));
        view.Arrange(new Rect(0, 0, osd.Width, osd.Height));
        view.UpdateLayout();
        return new(osd.Width, osd.Height);
    }

    private static void AssertContained(ToolkitOsdWindow osd)
    {
        var view = (Viewbox)osd.Content;
        var border = (Border)view.Child;
        var transform = border.TransformToAncestor(view);
        var delta = transform.Transform(new Point(1, 1)) - transform.Transform(new Point(0, 0));
        Check(view.Stretch == Stretch.None && Math.Abs(delta.X - 1) < .001 && Math.Abs(delta.Y - 1) < .001,
            "OSD content is being scaled as its dimensions change.");
        var bounds = border.TransformToAncestor(view).TransformBounds(new Rect(border.RenderSize));
        Check(bounds.Right <= osd.Width + 1 && bounds.Bottom <= osd.Height + 1 &&
              border.CornerRadius.TopRight == 10 && border.CornerRadius.BottomRight == 10,
            "Rounded background extends beyond the window surface.");
    }

    private static void SaveImage(ToolkitOsdWindow osd, string path)
    {
        var view = (Viewbox)osd.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(osd.Width), (int)Math.Ceiling(osd.Height),
            96, 96, PixelFormats.Pbgra32);
        image.Render(view);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        Check(pixels[(image.PixelWidth - 1) * 4 + 3] < 20,
            "Top-right corner was clipped or rendered as a square.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
