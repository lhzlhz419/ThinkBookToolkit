using System.Windows.Media;

namespace ThinkBookToolkit;

internal static class UiTypography
{
    public static string FontFamilyNameFor(string language) =>
        language == "en-US"
            ? "Segoe UI Variable Text"
            : "Microsoft YaHei UI";

    public static FontFamily FontFamilyFor(string language) =>
        new(FontFamilyNameFor(language));
}
