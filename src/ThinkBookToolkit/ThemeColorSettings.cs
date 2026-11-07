using System;
using System.Linq;
using System.Windows.Media;

namespace ThinkBookToolkit;

public sealed record ThemeColorSettings
{
    public string LightAccent { get; init; } = "4F6EF7";
    public string DarkAccent { get; init; } = "7C9CFF";
    public string LightBackground { get; init; } = "F4F7FB";
    public string DarkBackground { get; init; } = "0A1020";
    public string LightHeroStart { get; init; } = "5178F3";
    public string LightHeroEnd { get; init; } = "7955D9";
    public string DarkHeroStart { get; init; } = "5178F3";
    public string DarkHeroEnd { get; init; } = "7955D9";

    internal static bool Valid(string? color)
    {
        var value = color?.Trim().TrimStart('#');
        return value?.Length == 6 && value.All(Uri.IsHexDigit);
    }

    internal static bool Valid(ThemeColorSettings? value) => value is not null &&
        new[] { value.LightAccent, value.DarkAccent, value.LightBackground, value.DarkBackground, value.LightHeroStart, value.LightHeroEnd,
            value.DarkHeroStart, value.DarkHeroEnd }.All(Valid);

    internal static ThemeColorSettings Normalize(ThemeColorSettings? value)
    {
        var defaults = new ThemeColorSettings();
        if (value is null) return defaults;
        static string NormalizeColor(string? color, string fallback) =>
            Valid(color) ? color!.Trim().TrimStart('#').ToUpperInvariant() : fallback;
        return value with
        {
            LightAccent = NormalizeColor(value.LightAccent, defaults.LightAccent),
            DarkAccent = NormalizeColor(value.DarkAccent, defaults.DarkAccent),
            LightBackground = NormalizeColor(value.LightBackground, defaults.LightBackground),
            DarkBackground = NormalizeColor(value.DarkBackground, defaults.DarkBackground),
            LightHeroStart = NormalizeColor(value.LightHeroStart, defaults.LightHeroStart),
            LightHeroEnd = NormalizeColor(value.LightHeroEnd, defaults.LightHeroEnd),
            DarkHeroStart = NormalizeColor(value.DarkHeroStart, defaults.DarkHeroStart),
            DarkHeroEnd = NormalizeColor(value.DarkHeroEnd, defaults.DarkHeroEnd)
        };
    }

    internal (string Start, string End) Hero(bool dark) =>
        dark ? ("#" + DarkHeroStart, "#" + DarkHeroEnd) : ("#" + LightHeroStart, "#" + LightHeroEnd);

    internal bool DefaultHero(bool dark) => Hero(dark) == new ThemeColorSettings().Hero(dark);
    internal string HeroText(bool dark) => DefaultHero(dark) ? "#FFFFFF" : ContrastText(Hero(dark).Start);

    internal static string ContrastText(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        return luminance > 0.179 ? "#101010" : "#FFFFFF";
    }
}
