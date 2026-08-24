using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal sealed class GameDetectionSettingsWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly ListBox _included = new();
    private readonly ListBox _excluded = new();
    private readonly TextBlock _status = new();
    private readonly ToolkitPalette _palette;

    public GameDetectionSettingsWindow(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        _palette = ToolkitPalette.For(runtime.IsDark);
        Title = runtime.L(
            "自定义游戏检测路径",
            "Custom game detection paths");
        Width = 820;
        Height = 620;
        MinWidth = 620;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brush(_palette.Canvas);
        Foreground = Brush(_palette.Text);
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        foreach (var path in runtime.Settings.IncludedGamePaths)
            _included.Items.Add(path);
        foreach (var path in runtime.Settings.ExcludedGamePaths)
            _excluded.Items.Add(path);
        Content = BuildLayout();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = _runtime.L(
                "包含应用会被始终视为游戏；排除应用优先级更高，即使 Windows 将它识别为游戏也会忽略。",
                "Included applications are always treated as games. Exclusions take priority even when Windows identifies an application as a game."),
            Foreground = Brush(_palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        columns.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        columns.Children.Add(BuildPathPanel(
            _runtime.L("包含应用", "Included applications"),
            _included));
        var excluded = BuildPathPanel(
            _runtime.L("排除应用", "Excluded applications"),
            _excluded);
        excluded.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(excluded, 1);
        columns.Children.Add(excluded);
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Foreground = Brush(_palette.Danger);
        _status.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = Button(_runtime.L("取消", "Cancel"));
        var save = Button(_runtime.L("保存", "Save"), true);
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private Border BuildPathPanel(string title, ListBox list)
    {
        list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        list.Background = Brush(_palette.SurfaceRaised);
        list.Foreground = Brush(_palette.Text);
        list.BorderBrush = Brush(_palette.Border);
        list.BorderThickness = new Thickness(1);
        list.Padding = new Thickness(4);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(
            Control.BackgroundProperty,
            Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(
            Control.ForegroundProperty,
            Brush(_palette.Text)));
        itemStyle.Setters.Add(new Setter(
            Control.PaddingProperty,
            new Thickness(8, 7, 8, 7)));
        itemStyle.Setters.Add(new Setter(
            FrameworkElement.MarginProperty,
            new Thickness(0, 0, 0, 2)));
        var selected = new Trigger
        {
            Property = ListBoxItem.IsSelectedProperty,
            Value = true
        };
        selected.Setters.Add(new Setter(
            Control.BackgroundProperty,
            Brush(_palette.AccentSoft)));
        selected.Setters.Add(new Setter(
            Control.ForegroundProperty,
            Brush(_palette.Accent)));
        itemStyle.Triggers.Add(selected);
        list.ItemContainerStyle = itemStyle;
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        Grid.SetRow(list, 1);
        panel.Children.Add(list);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var add = Button(_runtime.L("添加应用", "Add application"));
        var remove = Button(_runtime.L("移除", "Remove"));
        add.Click += (_, _) => AddPath(list);
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not null)
                list.Items.Remove(list.SelectedItem);
        };
        actions.Children.Add(add);
        actions.Children.Add(remove);
        Grid.SetRow(actions, 2);
        panel.Children.Add(actions);
        return new Border
        {
            Background = Brush(_palette.Surface),
            BorderBrush = Brush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Child = panel
        };
    }

    private void AddPath(ListBox list)
    {
        var dialog = new OpenFileDialog
        {
            Filter = _runtime.L(
                "应用程序 (*.exe)|*.exe",
                "Applications (*.exe)|*.exe"),
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        foreach (var path in dialog.FileNames.Where(File.Exists))
        {
            if (!list.Items.Cast<string>().Contains(
                    path,
                    System.StringComparer.OrdinalIgnoreCase))
            {
                list.Items.Add(path);
            }
        }
    }

    private void Save()
    {
        if (!_runtime.TrySaveGameDetectionPaths(
                _included.Items.Cast<string>(),
                _excluded.Items.Cast<string>(),
                out var error))
        {
            _status.Text = _runtime.L("保存失败：", "Save failed: ") + error;
            return;
        }
        DialogResult = true;
    }

    private Button Button(string text, bool primary = false) => new()
    {
        Content = text,
        MinWidth = 100,
        MinHeight = 38,
        Padding = new Thickness(14, 7, 14, 7),
        Margin = new Thickness(8, 0, 0, 0),
        Background = Brush(primary ? _palette.Accent : _palette.SurfaceRaised),
        Foreground = Brush(primary ? "#FFFFFF" : _palette.Text),
        BorderBrush = Brush(primary ? _palette.Accent : _palette.Border)
    };

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
