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
    public static ToolkitPalette For(bool isDark) => isDark
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
        return isDark
            ? palette with
            {
                Sidebar = "#CC10182A",
                Surface = "#59151F33",
                SurfaceRaised = "#801B2840"
            }
            : palette with
            {
                Sidebar = "#99FFFFFF",
                Surface = "#40FFFFFF",
                SurfaceRaised = "#66F7F9FD"
            };
    }
}
