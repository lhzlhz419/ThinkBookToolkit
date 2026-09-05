using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class WarrantyDetailsWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly ToolkitPalette _palette;

    public WarrantyDetailsWindow(
        ToolkitRuntimeService runtime,
        WarrantySnapshot snapshot)
    {
        _runtime = runtime;
        _palette = ToolkitPalette.For(runtime.IsDark);
        Title = runtime.L("详细保修信息", "Warranty details");
        Width = 900;
        Height = 720;
        MinWidth = 650;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brush(_palette.Canvas);
        Foreground = Brush(_palette.Text);
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        Content = Build(snapshot);
    }

    private UIElement Build(WarrantySnapshot snapshot)
    {
        var list = new StackPanel { Margin = new Thickness(22) };
        list.Children.Add(new TextBlock
        {
            Text = _runtime.L("详细保修信息", "Warranty details"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold
        });
        list.Children.Add(new TextBlock
        {
            Text = _runtime.L(
                "以下内容来自联想接口的详细保修信息，仅显示同时具有有效开始和截止日期的项目。",
                "The entries below come from Lenovo's detailed warranty data. Only entries with valid start and end dates are shown."),
            Foreground = Brush(_palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 16)
        });
        foreach (var item in snapshot.Entitlements
                     .OrderBy(item => item.EndDate)
                     .ThenBy(item => item.StartDate))
        {
            list.Children.Add(EntitlementCard(item));
        }
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list
        };
    }

    private Border EntitlementCard(WarrantyEntitlement item)
    {
        var content = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Name)
                ? _runtime.L("未命名保修服务", "Unnamed warranty service")
                : item.Name,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        var category = new TextBlock
        {
            Text = Category(item.Category),
            Foreground = Brush(_palette.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(category, 1);
        header.Children.Add(category);
        content.Children.Add(header);
        content.Children.Add(Line(
            _runtime.L("有效期", "Coverage"),
            $"{Date(item.StartDate)} — {Date(item.EndDate)} · " +
            $"{Math.Max(0, item.EndDate.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber)} " +
            _runtime.L("天", "days")));
        Add(content, _runtime.L("服务编号", "Service number"), item.ProductNumber);
        Add(content, _runtime.L("服务类别", "Service class"), item.SmallClass);
        AddRange(content, _runtime.L("部件", "Parts"), item.PartStartDate, item.PartEndDate);
        AddRange(content, _runtime.L("人工", "Labor"), item.LaborStartDate, item.LaborEndDate);
        AddRange(content, _runtime.L("上门", "On-site"), item.OnSiteStartDate, item.OnSiteEndDate);
        Add(content, _runtime.L("说明", "Description"), item.Remark);
        return new Border
        {
            Background = Brush(_palette.Surface),
            BorderBrush = Brush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10),
            Child = content
        };
    }

    private void Add(Panel panel, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            panel.Children.Add(Line(label, value));
    }

    private void AddRange(
        Panel panel,
        string label,
        DateOnly? start,
        DateOnly? end)
    {
        if (start.HasValue && end.HasValue)
            panel.Children.Add(Line(label, $"{Date(start.Value)} — {Date(end.Value)}"));
    }

    private UIElement Line(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush(_palette.Muted)
        });
        var valueText = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(valueText, 1);
        row.Children.Add(valueText);
        return row;
    }

    private string Category(string value) => value switch
    {
        "Base" => _runtime.L("基础信息", "Base"),
        "Warranty" => _runtime.L("标准保修", "Warranty"),
        "On-site" => _runtime.L("上门服务", "On-site"),
        _ => _runtime.L("其他服务", "Other")
    };

    private static string Date(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
