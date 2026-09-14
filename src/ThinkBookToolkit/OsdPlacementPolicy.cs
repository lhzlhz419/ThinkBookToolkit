using System;
using System.Windows;

namespace ThinkBookToolkit;

internal static class OsdPlacementPolicy
{
    internal static double Position(double current, double size, double start, double end, OsdSnapAnchor anchor)
    {
        var maximum = Math.Max(start, end - size);
        var position = anchor switch
        {
            OsdSnapAnchor.Start => start,
            OsdSnapAnchor.Center => start + (end - start - size) / 2,
            OsdSnapAnchor.End => end - size,
            _ => current
        };
        return Math.Clamp(position, start, maximum);
    }

    internal static OsdSnapAnchor Snap(double current, double size, double start, double end, double threshold)
    {
        if (threshold <= 0) return OsdSnapAnchor.None;
        var result = OsdSnapAnchor.None;
        var distance = threshold;
        foreach (var candidate in new[] { OsdSnapAnchor.Start, OsdSnapAnchor.End, OsdSnapAnchor.Center })
        {
            var delta = Math.Abs(current - Position(current, size, start, end, candidate));
            if (delta <= distance) { distance = delta; result = candidate; }
        }
        return result;
    }

    internal static Size Fit(Size desired, Size available, DpiScale dpi)
    {
        // Round outwards to physical pixels to preserve the last glyph and corner.
        return new(Math.Min(available.Width, Math.Ceiling(desired.Width * dpi.DpiScaleX) / dpi.DpiScaleX),
            Math.Min(available.Height, Math.Ceiling(desired.Height * dpi.DpiScaleY) / dpi.DpiScaleY));
    }
}
