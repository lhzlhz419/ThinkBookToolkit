using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinkBookToolkit;

public sealed class CurveEditor : FrameworkElement
{
    private const double MarginLeft = 92;
    private const double MarginRight = 24;
    private const double MarginTop = 42;
    private const double MarginBottom = 64;

    private int _dragIndex = -1;
    private bool _darkTheme;
    private int _editFan = 1;
    private bool _syncFanSpeeds;
    private string _title;
    private string _temperatureAxisLabel = "Temperature (\u00B0C)";
    private string _fontFamilyName = "Segoe UI";

    public CurveEditor(string title, int[] temps, IReadOnlyList<int> fan1Values, IReadOnlyList<int> fan2Values)
    {
        _title = title;
        Temps = temps;
        Fan1Values = [.. fan1Values];
        Fan2Values = [.. fan2Values];
        MinRpm = 1500;
        MaxRpm = 5500;
        Fan1MinRpm = MinRpm;
        Fan1MaxRpm = MaxRpm;
        Fan2MinRpm = MinRpm;
        Fan2MaxRpm = MaxRpm;
        MinHeight = 310;
        Focusable = false;
        AddHandler(
            RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(
                (_, args) => args.Handled = true));
    }

    public int[] Temps { get; }
    public List<int> Fan1Values { get; private set; }
    public List<int> Fan2Values { get; private set; }
    public int MinRpm { get; private set; }
    public int MaxRpm { get; private set; }
    public int Fan1MinRpm { get; private set; }
    public int Fan1MaxRpm { get; private set; }
    public int Fan2MinRpm { get; private set; }
    public int Fan2MaxRpm { get; private set; }
    public double? CurrentTempC { get; private set; }
    public event Action<List<int>, List<int>>? ValuesChanged;

    public void SetLabels(string title, string temperatureAxisLabel)
    {
        _title = title;
        _temperatureAxisLabel = temperatureAxisLabel;
        InvalidateVisual();
    }

    public void SetTheme(bool darkTheme)
    {
        _darkTheme = darkTheme;
        InvalidateVisual();
    }

    public void SetFontFamily(string fontFamilyName)
    {
        _fontFamilyName = fontFamilyName;
        InvalidateVisual();
    }

    public void SetEditFan(int fan)
    {
        _editFan = fan == 2 ? 2 : 1;
        InvalidateVisual();
    }

    public void SetSyncFanSpeeds(bool syncFanSpeeds)
    {
        _syncFanSpeeds = syncFanSpeeds;
        InvalidateVisual();
    }

    public void SetValues(IReadOnlyList<int> fan1Values, IReadOnlyList<int> fan2Values)
    {
        Fan1Values = CurveProfileStore.ClampCurve(
            fan1Values,
            Fan1MinRpm,
            Fan1MaxRpm);
        Fan2Values = CurveProfileStore.ClampCurve(
            fan2Values,
            Fan2MinRpm,
            Fan2MaxRpm);
        InvalidateVisual();
    }

    public void SetCurrentTemp(double? tempC)
    {
        CurrentTempC = tempC;
        InvalidateVisual();
    }

    public void SetRpmRange(int minimum, int maximum)
        => SetRpmRanges(minimum, maximum, minimum, maximum);

    public void SetRpmRanges(
        int fan1Minimum,
        int fan1Maximum,
        int fan2Minimum,
        int fan2Maximum)
    {
        if (fan1Minimum >= fan1Maximum ||
            fan2Minimum >= fan2Maximum)
            return;

        Fan1MinRpm = fan1Minimum;
        Fan1MaxRpm = fan1Maximum;
        Fan2MinRpm = fan2Minimum;
        Fan2MaxRpm = fan2Maximum;
        MinRpm = Math.Min(fan1Minimum, fan2Minimum);
        MaxRpm = Math.Max(fan1Maximum, fan2Maximum);
        Fan1Values = CurveProfileStore.ClampCurve(
            Fan1Values,
            Fan1MinRpm,
            Fan1MaxRpm);
        Fan2Values = CurveProfileStore.ClampCurve(
            Fan2Values,
            Fan2MinRpm,
            Fan2MaxRpm);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = Math.Max(ActualWidth, MarginLeft + MarginRight + 1);
        var height = Math.Max(ActualHeight, MarginTop + MarginBottom + 1);
        var plotRight = width - MarginRight;
        var plotBottom = height - MarginBottom;

        drawingContext.DrawRectangle(ColorBrush(ChartBackground), null, new Rect(0, 0, width, height));
        drawingContext.DrawRectangle(null, new Pen(ColorBrush(BorderColor), 1), new Rect(0.5, 0.5, width - 1, height - 1));
        DrawText(drawingContext, _title, 12, FontWeights.Bold, TextColor, new Point(MarginLeft, 14), TextAlignment.Left);

        foreach (var rpm in RpmTicks())
        {
            var y = YForRpm(rpm);
            var pen = new Pen(ColorBrush(rpm % 1000 == 0 ? MajorGridColor : MinorGridColor), 1) { DashStyle = DashStyles.Dash };
            drawingContext.DrawLine(pen, new Point(MarginLeft, y), new Point(plotRight, y));
            DrawText(drawingContext, rpm.ToString(CultureInfo.InvariantCulture), 11, FontWeights.Normal, MutedTextColor, new Point(MarginLeft - 12, y - 8), TextAlignment.Right);
        }

        foreach (var temp in Temps)
        {
            var x = XForTemp(temp);
            var pen = new Pen(ColorBrush(MinorGridColor), 1) { DashStyle = DashStyles.Dash };
            drawingContext.DrawLine(pen, new Point(x, MarginTop), new Point(x, plotBottom));
            DrawText(drawingContext, temp.ToString(CultureInfo.InvariantCulture), 11, FontWeights.Normal, MutedTextColor, new Point(x, plotBottom + 10), TextAlignment.Center);
        }

        DrawCurve(drawingContext, Fan2Values, Fan2Color, _editFan == 2);
        DrawCurve(drawingContext, Fan1Values, Fan1Color, _editFan == 1);
        DrawLegend(drawingContext, plotRight);

        if (CurrentTempC is not null)
        {
            var clamped = Math.Max(Temps[0], Math.Min(Temps[^1], CurrentTempC.Value));
            var x = XForTemp(clamped);
            var pen = new Pen(ColorBrush("#dc2626"), 2) { DashStyle = DashStyles.Dash };
            drawingContext.DrawLine(pen, new Point(x, MarginTop), new Point(x, plotBottom));
            drawingContext.DrawRoundedRectangle(ColorBrush("#dc2626"), null, new Rect(x - 42, 6, 84, 26), 2, 2);
            DrawText(drawingContext, $"{CurrentTempC.Value:F0} \u00B0C", 11, FontWeights.Bold, "#ffffff", new Point(x, 11), TextAlignment.Center);
        }

        DrawText(drawingContext, _temperatureAxisLabel, 11, FontWeights.Normal, MutedTextColor, new Point(MarginLeft + PlotWidth / 2, height - 28), TextAlignment.Center);

        drawingContext.PushTransform(new RotateTransform(-90, 14, MarginTop + PlotHeight / 2));
        DrawText(drawingContext, "RPM", 11, FontWeights.Normal, MutedTextColor, new Point(14, MarginTop + PlotHeight / 2 - 8), TextAlignment.Center);
        drawingContext.Pop();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        _dragIndex = NearestPoint(e.GetPosition(this));
        if (_dragIndex >= 0)
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
            return;

        ApplyDraggedValue(_dragIndex, RpmForY(e.GetPosition(this).Y));
        ValuesChanged?.Invoke([.. Fan1Values], [.. Fan2Values]);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var wasDragging = _dragIndex >= 0;
        _dragIndex = -1;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (wasDragging)
            e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragIndex = -1;
        base.OnLostMouseCapture(e);
    }

    private double PlotWidth => Math.Max(1, ActualWidth - MarginLeft - MarginRight);
    private double PlotHeight => Math.Max(1, ActualHeight - MarginTop - MarginBottom);
    private string ChartBackground => _darkTheme ? "#111827" : "#ffffff";
    private string BorderColor => _darkTheme ? "#374151" : "#d1d5db";
    private string MajorGridColor => _darkTheme ? "#4b5563" : "#d1d5db";
    private string MinorGridColor => _darkTheme ? "#374151" : "#e5e7eb";
    private string TextColor => _darkTheme ? "#f9fafb" : "#111827";
    private string MutedTextColor => _darkTheme ? "#d1d5db" : "#4b5563";
    private string Fan1Color => _darkTheme ? "#60a5fa" : "#2563eb";
    private string Fan2Color => _darkTheme ? "#fb923c" : "#ea580c";

    private double XForTemp(double temp)
    {
        return MarginLeft + (temp - Temps[0]) / (Temps[^1] - Temps[0]) * PlotWidth;
    }

    private double YForRpm(double rpm)
    {
        return MarginTop + (MaxRpm - rpm) / (MaxRpm - MinRpm) * PlotHeight;
    }

    private int RpmForY(double y)
    {
        var ratio = (y - MarginTop) / PlotHeight;
        var rpm = MaxRpm - ratio * (MaxRpm - MinRpm);
        return CurveProfileStore.ClampRpm(rpm, MinRpm, MaxRpm);
    }

    private void ApplyDraggedValue(int index, int rpm)
    {
        var values = _editFan == 2 ? Fan2Values : Fan1Values;
        values[index] = ClampForFan(rpm, _editFan);
        EnforceNonDecreasingInPlace(values, index);

        if (_syncFanSpeeds)
        {
            var otherValues = _editFan == 2 ? Fan1Values : Fan2Values;
            var otherFan = _editFan == 2 ? 1 : 2;
            otherValues[index] = ClampForFan(rpm, otherFan);
            EnforceNonDecreasingInPlace(otherValues, index);
        }
    }

    private int ClampForFan(int rpm, int fan) =>
        fan == 2
            ? Math.Clamp(rpm, Fan2MinRpm, Fan2MaxRpm)
            : Math.Clamp(rpm, Fan1MinRpm, Fan1MaxRpm);

    private static void EnforceNonDecreasingInPlace(List<int> values, int index)
    {
        for (var i = index + 1; i < values.Count; i++)
        {
            if (values[i] < values[i - 1])
                values[i] = values[i - 1];
        }

        for (var i = index - 1; i >= 0; i--)
        {
            if (values[i] > values[i + 1])
                values[i] = values[i + 1];
        }
    }

    private int NearestPoint(Point point)
    {
        var values = _editFan == 2 ? Fan2Values : Fan1Values;
        var bestIndex = -1;
        var bestDistance = 20.0;
        for (var i = 0; i < values.Count; i++)
        {
            var candidate = new Point(XForTemp(Temps[i]), YForRpm(values[i]));
            var distance = (candidate - point).Length;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private IEnumerable<int> RpmTicks()
    {
        var ticks = new List<int> { MinRpm };
        var first = ((MinRpm + 499) / 500) * 500;
        for (var rpm = first; rpm <= MaxRpm; rpm += 500)
        {
            if (!ticks.Contains(rpm))
                ticks.Add(rpm);
        }
        if (!ticks.Contains(MaxRpm))
            ticks.Add(MaxRpm);
        return ticks;
    }

    private void DrawCurve(DrawingContext drawingContext, IReadOnlyList<int> values, string color, bool active)
    {
        var points = values.Select((rpm, index) => new Point(XForTemp(Temps[index]), YForRpm(rpm))).ToList();
        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(points[0], false, false);
                context.PolyLineTo(points.Skip(1).ToList(), true, false);
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, new Pen(ColorBrush(color), active ? 3.2 : 2.2), geometry);
        }

        for (var i = 0; i < points.Count; i++)
        {
            drawingContext.DrawEllipse(active ? ColorBrush(color) : ColorBrush(ChartBackground), new Pen(ColorBrush(color), 2.2), points[i], active ? 7 : 5.5, active ? 7 : 5.5);
            if (active)
                DrawText(drawingContext, values[i].ToString(CultureInfo.InvariantCulture), 8, FontWeights.Normal, TextColor, new Point(points[i].X, points[i].Y - 26), TextAlignment.Center);
        }
    }

    private void DrawLegend(DrawingContext drawingContext, double plotRight)
    {
        var y = 16.0;
        DrawLegendItem(drawingContext, plotRight - 176, y, Fan1Color, "Fan 1", _editFan == 1);
        DrawLegendItem(drawingContext, plotRight - 88, y, Fan2Color, "Fan 2", _editFan == 2);
    }

    private void DrawLegendItem(DrawingContext drawingContext, double x, double y, string color, string text, bool active)
    {
        drawingContext.DrawLine(new Pen(ColorBrush(color), active ? 3 : 2), new Point(x, y + 6), new Point(x + 24, y + 6));
        drawingContext.DrawEllipse(active ? ColorBrush(color) : ColorBrush(ChartBackground), new Pen(ColorBrush(color), 2), new Point(x + 12, y + 6), active ? 5 : 4, active ? 5 : 4);
        DrawText(drawingContext, text, 10, active ? FontWeights.Bold : FontWeights.Normal, TextColor, new Point(x + 30, y), TextAlignment.Left);
    }

    private void DrawText(DrawingContext context, string text, double size, FontWeight weight, string color, Point point, TextAlignment alignment)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(_fontFamilyName), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            ColorBrush(color),
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = alignment
        };

        context.DrawText(formattedText, alignment switch
        {
            TextAlignment.Right => new Point(point.X - formattedText.Width, point.Y),
            TextAlignment.Center => new Point(point.X - formattedText.Width / 2, point.Y),
            _ => point
        });
    }

    private static SolidColorBrush ColorBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
