using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

// Compatibility component for the legacy windows that remain available to
// the internal fan controller. The current Toolkit device page does not use
// this visual; it renders its own native warranty card.
internal sealed class WarrantyCard : Border
{
    private readonly Func<string, string> _t;
    private readonly bool _isDark;
    private readonly Border _statusBadge;
    private readonly TextBlock _statusText;
    private readonly Border _progressTrack;
    private readonly Border _progressFill;
    private readonly TextBlock _startDateText;
    private readonly TextBlock _endDateText;
    private WarrantyState _state = WarrantyState.Unavailable;
    private int _progressPercentage;

    public WarrantyCard(Func<string, string> translate, bool isDark)
    {
        _t = translate;
        _isDark = isDark;
        Margin = new Thickness(0, 10, 0, 0);
        Padding = new Thickness(14);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);

        _statusText = new TextBlock
        {
            Text = _t("WarrantyLoading"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusBadge = new Border
        {
            MinWidth = 42,
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(12, 12, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = _statusText
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = _t("WarrantyInformation"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_statusBadge, 1);
        header.Children.Add(_statusBadge);

        _progressFill = new Border
        {
            Height = 6,
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(3)
        };
        _progressTrack = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 12, 0, 0),
            Child = _progressFill
        };
        _progressTrack.SizeChanged += (_, _) => UpdateProgressWidth();

        _startDateText = DateText(HorizontalAlignment.Left, TextAlignment.Left);
        _endDateText = DateText(HorizontalAlignment.Right, TextAlignment.Right);
        var dates = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_startDateText, 0);
        dates.Children.Add(_startDateText);
        Grid.SetColumn(_endDateText, 1);
        dates.Children.Add(_endDateText);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(_progressTrack);
        content.Children.Add(dates);
        Child = content;
        SetLoading();
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        SetLoading();
        try
        {
            var snapshot = await WarrantyService.GetWarrantyAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                ApplySnapshot(WarrantySnapshot.Unavailable(ex.Message));
        }
    }

    private void SetLoading()
    {
        _state = WarrantyState.Unavailable;
        _statusText.Text = _t("WarrantyLoading");
        _startDateText.Text = _t("NoInformation");
        _endDateText.Text = _t("NoInformation");
        _progressPercentage = 0;
        ToolTip = null;
        ApplyColors();
        UpdateProgressWidth();
    }

    private void ApplySnapshot(WarrantySnapshot snapshot)
    {
        _state = snapshot.State;
        _statusText.Text = _t(snapshot.State switch
        {
            WarrantyState.InWarranty => "WarrantyInCoverage",
            WarrantyState.Expired => "WarrantyExpired",
            WarrantyState.NotStarted => "WarrantyNotStarted",
            _ => "WarrantyUnavailable"
        });
        _startDateText.Text = FormatDate(snapshot.StartDate);
        _endDateText.Text = FormatDate(snapshot.EndDate);
        _progressPercentage = snapshot.ProgressPercentage;
        var tooltip = new List<string>();
        if (snapshot.IsStale)
            tooltip.Add(_t("WarrantyCached"));
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            tooltip.Add(string.Format(_t("WarrantyQueryFailedFormat"), snapshot.Error));
        ToolTip = tooltip.Count == 0 ? null : string.Join(Environment.NewLine, tooltip);
        ApplyColors();
        UpdateProgressWidth();
    }

    private TextBlock DateText(HorizontalAlignment alignment, TextAlignment textAlignment) => new()
    {
        Text = _t("NoInformation"),
        FontSize = 12,
        HorizontalAlignment = alignment,
        TextAlignment = textAlignment,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private string FormatDate(DateOnly? date) => date.HasValue
        ? date.Value.ToString(_t("WarrantyDateFormat"), CultureInfo.InvariantCulture)
        : _t("NoInformation");

    private void UpdateProgressWidth() => _progressFill.Width = Math.Max(
        0,
        _progressTrack.ActualWidth * _progressPercentage / 100.0);

    private void ApplyColors()
    {
        Background = Brush(_isDark ? "#1f2937" : "#ffffff");
        BorderBrush = Brush(_isDark ? "#374151" : "#e5e7eb");
        _statusBadge.Background = _state == WarrantyState.InWarranty
            ? Brush(_isDark ? "#334c75" : "#e8f0fe")
            : Brush(_isDark ? "#374151" : "#f3f4f6");
        _statusText.Foreground = _state == WarrantyState.InWarranty
            ? Brush(_isDark ? "#8db8ff" : "#3a78f2")
            : Brush(_isDark ? "#d1d5db" : "#6b7280");
        _startDateText.Foreground = Brush(_isDark ? "#9ca3af" : "#8c8c8c");
        _endDateText.Foreground = Brush(_isDark ? "#9ca3af" : "#8c8c8c");
        _progressTrack.Background = Brush(_isDark ? "#374151" : "#f1f1f1");
        _progressFill.Background = _state == WarrantyState.Expired
            ? _progressTrack.Background
            : new LinearGradientBrush(ColorFrom("#5898fd"), ColorFrom("#45c4ee"), 0);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush(ColorFrom(hex));
        brush.Freeze();
        return brush;
    }

    private static Color ColorFrom(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);
}
