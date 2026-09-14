using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal sealed class FolderLocationsWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly TextBox[] _paths = new TextBox[5];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    internal FolderLocationsWindow(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        Title = L("自定义文件夹位置", "Custom folder locations");
        Width = 980; Height = 650; MinWidth = 700; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        var palette = ToolkitPalette.For(runtime.IsDark);
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Canvas));
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Text));
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        Loaded += (_, _) => ModernTheme.RefreshWindow(this, runtime.IsDark);
        var root = new DockPanel { Margin = new Thickness(22) };
        var heading = new TextBlock
        {
            Text = L("自定义文件夹位置", "Custom folder locations"),
            FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16)
        };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = Button(L("保存", "Save"));
        save.Click += (_, _) => Save();
        var close = Button(L("关闭", "Close")); close.Click += (_, _) => Close();
        var cancelPending = Button(L("取消待生效更改", "Cancel pending changes"));
        cancelPending.Click += (_, _) =>
        {
            try
            {
                ToolkitStoragePaths.CancelPending();
                var current = ToolkitStoragePaths.Values(ToolkitStoragePaths.Current);
                for (var i = 0; i < _paths.Length; i++) _paths[i].Text = current[i];
                _status.Text = L("已取消待生效的位置更改。", "Pending location changes were cancelled.");
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        actions.Children.Add(cancelPending);
        actions.Children.Add(save); actions.Children.Add(close); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = L("下次启动时复制现有文件并切换位置，原目录保留。不同内容的同名文件不会被覆盖。正在写入的文件不会被移动。\n目录索引保留在默认配置目录；dependency 目录用于外部依赖，不包含需要随程序部署的 .NET 运行文件。",
                "Existing files will be copied on next startup; originals are retained and conflicting files are not overwritten. Active files are never moved.\nThe folder locator stays in the default configuration folder. The dependency folder contains external dependencies, not the application's .NET runtime files."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
        });
        var initial = ToolkitStoragePaths.Values(ToolkitStoragePaths.ReadManifest().Pending ?? ToolkitStoragePaths.Current);
        if (!File.Exists(ToolkitStoragePaths.LocatorPath) && LenovoDependencyDirectory.GetEnabledRoot() is { } legacy)
            initial[0] = legacy;
        var defaults = ToolkitStoragePaths.Values(ToolkitStoragePaths.Defaults);
        var names = new[] { L("dependency 文件夹", "Dependency folder"), L("配置文件夹", "Configuration folder"),
            L("日志文件夹", "Log folder"), L("下载缓存文件夹", "Download cache folder"), L("传感器记录文件夹", "Sensor recordings folder") };
        for (var i = 0; i < _paths.Length; i++)
        {
            var index = i;
            content.Children.Add(new TextBlock { Text = names[i], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 5) });
            var row = new DockPanel { LastChildFill = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var browse = Button(L("选择", "Browse"));
            browse.Click += (_, _) =>
            {
                var dialog = new OpenFolderDialog { Title = names[index], InitialDirectory = _paths[index].Text };
                if (dialog.ShowDialog(this) == true) _paths[index].Text = dialog.FolderName;
            };
            var open = Button(L("打开", "Open"));
            open.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(_paths[index].Text);
                    Process.Start(new ProcessStartInfo(_paths[index].Text) { UseShellExecute = true });
                }
                catch (Exception ex) { _status.Text = ex.Message; }
            };
            var reset = Button(L("默认", "Default")); reset.Click += (_, _) => _paths[index].Text = defaults[index];
            buttons.Children.Add(browse); buttons.Children.Add(open); buttons.Children.Add(reset);
            DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons);
            _paths[i] = new TextBox { Text = initial[i], IsReadOnly = true, MinHeight = 36,
                VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            row.Children.Add(_paths[i]); content.Children.Add(row);
        }
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private void Save()
    {
        try
        {
            CurveProfileStore.SaveSettings(_runtime.Settings);
            ToolkitStoragePaths.Schedule(new(_paths[0].Text, _paths[1].Text, _paths[2].Text, _paths[3].Text, _paths[4].Text),
                LenovoDependencyDirectory.GetEnabledRoot());
            _status.Text = L("已保存。请退出后重新启动程序，复制完成后使用新位置。", "Saved. Exit and restart Toolkit to copy files and use the new locations.");
        }
        catch (Exception ex) { _status.Text = L("保存失败：", "Save failed: ") + ex.Message; }
    }
    private string L(string chinese, string english) => _runtime.L(chinese, english);
    private static Button Button(string text) => new() { Content = text, MinHeight = 36, MinWidth = 64,
        Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4, 0, 0, 0) };
}
