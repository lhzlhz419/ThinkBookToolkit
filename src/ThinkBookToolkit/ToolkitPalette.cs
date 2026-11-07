using System;
using System.Linq;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal readonly record struct ToolkitPalette(
    string Canvas,
    string CanvasAlt,
    string Sidebar,
    string Surface,
    string SurfaceRaised,
    string Border,
    string Text,
    string Muted,
    string Accent,
    string AccentSoft,
    string Success,
    string Warning,
    string Danger)
{
    private static ThemeColorSettings _colors = new();
    internal string AccentText => Accent is "#4F6EF7" or "#7C9CFF" ? "#FFFFFF" : ThemeColorSettings.ContrastText(Accent);
    internal static void Configure(ThemeColorSettings? colors) => _colors = ThemeColorSettings.Normalize(colors);

    public static ToolkitPalette For(bool isDark) => For(isDark, _colors);

    internal static ToolkitPalette For(bool isDark, ThemeColorSettings colors)
    {
        var palette = DefaultFor(isDark);
        var backgroundHex = "#" + (isDark ? colors.DarkBackground : colors.LightBackground);
        var customBackground = backgroundHex != palette.Canvas;
        if (customBackground) palette = WithBackground(palette, backgroundHex);
        var accent = "#" + (isDark ? colors.DarkAccent : colors.LightAccent);
        if (!customBackground && accent == palette.Accent) return palette;
        var weight = Luminance(Parse(palette.Surface)) < 0.18 ? 0.26 : 0.12;
        return palette with { Accent = accent, AccentSoft = Mix(palette.Surface, accent, weight) };
    }

    private static ToolkitPalette WithBackground(ToolkitPalette palette, string background)
    {
        var darkSurface = Luminance(Parse(background)) < 0.18;
        var foreground = Contrast("#FFFFFF", background) > Contrast("#000000", background) ? "#FFFFFF" : "#000000";
        string ReadableSurface(string candidate)
        {
            for (var step = 0; step < 20 && Contrast(foreground, candidate) < 4.5; step++)
                candidate = Mix(candidate, background, 0.5);
            return candidate;
        }
        // Retain the chosen canvas exactly. Surface elevation uses nearby tints.
        var sidebar = ReadableSurface(Mix(background, "#FFFFFF", darkSurface ? 0.035 : 0.45));
        var surface = ReadableSurface(Mix(background, "#FFFFFF", darkSurface ? 0.055 : 0.65));
        var raised = ReadableSurface(Mix(background, darkSurface ? "#FFFFFF" : "#000000", darkSurface ? 0.09 : 0.025));
        var alternate = ReadableSurface(Mix(background, darkSurface ? "#FFFFFF" : "#000000", 0.025));
        var backgrounds = new[] { background, alternate, sidebar, surface, raised };
        double Readability(string text) => backgrounds.Min(value => Contrast(text, value));
        var muted = Mix(surface, foreground, 0.65);
        for (var weight = 0.70; Readability(muted) < 4.5 && weight <= 1.001; weight += 0.05)
            muted = Mix(surface, foreground, Math.Min(weight, 1));
        return palette with
        {
            Canvas = background,
            CanvasAlt = alternate,
            Sidebar = sidebar,
            Surface = surface,
            SurfaceRaised = raised,
            Border = Mix(surface, foreground, 0.22),
            Text = foreground,
            Muted = muted
        };
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static string Mix(string background, string foreground, double weight)
    {
        var a = Parse(background);
        var b = Parse(foreground);
        byte Channel(byte from, byte to) => (byte)Math.Round(from * (1 - weight) + to * weight);
        return $"#{Channel(a.R, b.R):X2}{Channel(a.G, b.G):X2}{Channel(a.B, b.B):X2}";
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    internal static double Contrast(string text, string background)
    {
        var a = Luminance(Parse(text));
        var b = Luminance(Parse(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static ToolkitPalette DefaultFor(bool isDark) => isDark
        ? new(
            "#0A1020",
            "#111A2C",
            "#10182A",
            "#151F33",
            "#1B2840",
            "#2A3852",
            "#F7F9FC",
            "#9BAAC2",
            "#7C9CFF",
            "#243964",
            "#53D69A",
            "#F5B94C",
            "#FF7B86")
        : new(
            "#F4F7FB",
            "#EAF0F8",
            "#FFFFFF",
            "#FFFFFF",
            "#F7F9FD",
            "#E2E9F2",
            "#172033",
            "#6D7A90",
            "#4F6EF7",
            "#E9EEFF",
            "#1FA971",
            "#C57A12",
            "#D94A59");

    public static ToolkitPalette For(bool isDark, bool hasBackgroundImage)
    {
        var palette = For(isDark);
        if (!hasBackgroundImage)
            return palette;
        static string WithAlpha(string hex, byte alpha)
        {
            var color = Parse(hex);
            color.A = alpha;
            return color.ToString();
        }
        return palette with
        {
            Sidebar = WithAlpha(palette.Sidebar, isDark ? (byte)0xCC : (byte)0x99),
            Surface = WithAlpha(palette.Surface, isDark ? (byte)0x59 : (byte)0x40),
            SurfaceRaised = WithAlpha(palette.SurfaceRaised, isDark ? (byte)0x80 : (byte)0x66)
        };
    }
}
