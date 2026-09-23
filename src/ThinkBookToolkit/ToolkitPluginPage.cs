using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal sealed class ToolkitPluginPage : ToolkitPageBase
{
    internal ToolkitPluginPage(ToolkitRuntimeService runtime, PluginInstallation plugin, PluginPage page) : base(runtime)
    {
        var panel = new StackPanel();
        foreach (var setting in plugin.Manifest.Settings.Where(s => s.PageId == page.Id))
            panel.Children.Add(Card(setting.Title.Resolve(runtime.IsChinese), PluginSettingControl.Create(runtime, plugin, setting)));
        panel.Children.Add(new PluginSensorPanel(runtime, pluginId: plugin.Manifest.Id));
        Content = panel;
    }
}

internal static class PluginSettingControl
{
    internal static UIElement Create(ToolkitRuntimeService runtime, PluginInstallation plugin, PluginSetting setting)
    {
        if (runtime.Plugins.Setting(plugin.Manifest.Id + ".setting." + setting.Id) is { } replacement)
        { plugin = replacement.Plugin; setting = replacement.Setting; }
        var root = new StackPanel();
        var status = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var updating = false;
        Action reload = () => { };
        async void Save(JsonElement value)
        {
            root.IsEnabled = false;
            try { await runtime.Plugins.SetSettingAsync(plugin, setting, value); status.Text = plugin.Error ?? ""; }
            catch (Exception ex) { status.Text = ex.Message; reload(); }
            finally { root.IsEnabled = true; }
        }
        var current = runtime.Plugins.SettingValue(plugin, setting);
        if (setting.Kind == "boolean")
        {
            var control = new CheckBox { Content = setting.Title.Resolve(runtime.IsChinese), IsChecked = current.GetBoolean(), Margin = new Thickness(4) };
            reload = () => control.IsChecked = runtime.Plugins.SettingValue(plugin, setting).GetBoolean();
            control.Click += (_, _) => Save(JsonSerializer.SerializeToElement(control.IsChecked == true));
            root.Children.Add(control);
        }
        else if (setting.Kind == "choice")
        {
            var control = new ComboBox { MinWidth = 150, ItemsSource = setting.Choices, SelectedItem = current.GetString() };
            reload = () => { updating = true; try { control.SelectedItem = runtime.Plugins.SettingValue(plugin, setting).GetString(); } finally { updating = false; } };
            control.SelectionChanged += (_, _) => { if (!updating && control.SelectedItem is string value) Save(JsonSerializer.SerializeToElement(value)); };
            root.Children.Add(control);
        }
        else
        {
            var control = new TextBox { Text = current.ToString(), MinWidth = 180 };
            reload = () => { if (!control.IsKeyboardFocusWithin) control.Text = runtime.Plugins.SettingValue(plugin, setting).ToString(); };
            var apply = new Button { Content = runtime.L("应用", "Apply"), Margin = new Thickness(0, 6, 0, 0) };
            apply.Click += (_, _) =>
            {
                if (setting.Kind == "number")
                {
                    if (!double.TryParse(control.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) || !double.IsFinite(number)) { status.Text = runtime.L("请输入有效数字。", "Enter a valid number."); return; }
                    Save(JsonSerializer.SerializeToElement(number));
                }
                else Save(JsonSerializer.SerializeToElement(control.Text));
            };
            root.Children.Add(control); root.Children.Add(apply);
        }
        var subscribed = false;
        EventHandler changed = (_, _) => { if (root.IsEnabled && plugin.Enabled) reload(); };
        root.Loaded += (_, _) => { if (!subscribed) runtime.SnapshotChanged += changed; subscribed = true; };
        root.Unloaded += (_, _) => { runtime.SnapshotChanged -= changed; subscribed = false; };
        root.Children.Add(status); return root;
    }
}

internal sealed class PluginSensorPanel : StackPanel
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly string? _overviewCard;
    private readonly string? _category;
    private readonly string? _pluginId;
    private bool _subscribed;
    private readonly System.Collections.Generic.Dictionary<string, (Grid Row, TextBlock Label, TextBlock Value)> _rows = new();
    internal PluginSensorPanel(ToolkitRuntimeService runtime, string? overviewCard = null, string? pluginId = null, string? category = null)
    {
        _runtime = runtime; _overviewCard = overviewCard; _pluginId = pluginId; _category = category;
        Orientation = Orientation.Vertical;
        Loaded += (_, _) => { if (!_subscribed) runtime.Plugins.ValuesChanged += Changed; _subscribed = true; Refresh(); };
        Unloaded += (_, _) => { runtime.Plugins.ValuesChanged -= Changed; _subscribed = false; };
        Refresh();
    }
    private void Changed(object? sender, EventArgs args) => Refresh();
    internal void Refresh()
    {
        var sensors = _runtime.Plugins.Sensors.Where(s => s.Replaces is null &&
            (_overviewCard is null || PluginSensorPlacement.OverviewCard(s.Category) == _overviewCard) &&
            (_category is null || s.Category == _category) &&
            (_pluginId is null || s.PluginId == _pluginId)).ToArray();
        var ids = sensors.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            Children.Remove(_rows[id].Row); _rows.Remove(id);
        }
        for (var index = 0; index < sensors.Length; index++)
        {
            var sensor = sensors[index];
            var label = _runtime.IsChinese ? sensor.Name : sensor.EnglishName;
            if (_rows.TryGetValue(sensor.Id, out var existing))
            {
                existing.Label.Text = label; existing.Value.Text = sensor.Text;
                if (Children.IndexOf(existing.Row) != index) { Children.Remove(existing.Row); Children.Insert(index, existing.Row); }
                continue;
            }
            var palette = ToolkitPalette.For(_runtime.IsDark);
            var row = new Grid { Margin = new Thickness(0, 2, 0, 0), ToolTip = sensor.PluginId + " · " + sensor.Id };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var caption = new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Muted)) };
            row.Children.Add(caption);
            var reading = new TextBlock { Text = sensor.Text, FontSize = 12, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Text)), TextAlignment = TextAlignment.Right };
            Grid.SetColumn(reading, 1); row.Children.Add(reading); Children.Insert(index, row);
            _rows[sensor.Id] = (row, caption, reading);
        }
        Visibility = Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

internal sealed class ToolkitPluginsPage : ToolkitPageBase
{

    internal ToolkitPluginsPage(ToolkitRuntimeService runtime) : base(runtime)
    {
        var root = new StackPanel();
        var intro = new Grid();
        intro.ColumnDefinitions.Add(new ColumnDefinition());
        intro.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        intro.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        intro.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var explanation = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        explanation.Children.Add(Text(L("只启用可信插件", "Enable trusted plugins only"), 14, strong: true));
        explanation.Children.Add(Text(L("外部插件默认停用。独立进程用于隔离崩溃，不是安全沙箱。", "External plugins start disabled. Process isolation contains crashes; it is not a security sandbox."), 12, muted: true, top: 5));
        intro.Children.Add(explanation);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var import = ActionButton(L("导入插件", "Import plugin"), primary: true);
        import.Margin = new Thickness(0, 0, 8, 6);
        var importStatus = Text("", 12, top: 8); importStatus.Foreground = Brush(Palette.Danger); importStatus.Visibility = Visibility.Collapsed;
        import.Click += async (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = L("选择插件压缩包", "Select plugin archive"), Filter = L("ZIP 插件压缩包 (*.zip)|*.zip", "ZIP plugin archives (*.zip)|*.zip"),
                CheckFileExists = true, Multiselect = false
            };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            var owner = Window.GetWindow(this);
            import.IsEnabled = false; import.Content = L("正在导入…", "Importing…"); importStatus.Visibility = Visibility.Collapsed;
            try
            {
                var installed = await runtime.Plugins.ImportAsync(picker.FileName);
                runtime.SetStatus(L("已导入 ", "Imported ") + installed.Manifest.Name + L("，请审核权限后启用。", ". Review its permissions before enabling it."));
                if (runtime.Plugins.RequiresRestart(installed)) AskRestart(owner, L("插件已安装，需要重启 Toolkit 才能完成切换。", "The plugin is installed. Restart Toolkit to complete the change."));
            }
            catch (PluginRestartRequiredException ex)
            {
                importStatus.Text = ex.Message; importStatus.Visibility = Visibility.Visible;
                AskRestart(owner, ex.Message);
            }
            catch (Exception ex)
            {
                importStatus.Text = L("导入失败：", "Import failed: ") + ex.GetBaseException().Message;
                importStatus.Visibility = Visibility.Visible;
            }
            finally { import.IsEnabled = true; import.Content = L("导入插件", "Import plugin"); }
        };
        actions.Children.Add(import);
        var open = ActionButton(L("打开插件目录", "Open plugin folder"));
        open.HorizontalAlignment = HorizontalAlignment.Right;
        open.VerticalAlignment = VerticalAlignment.Center;
        open.Click += (_, _) =>
        {
            try { System.IO.Directory.CreateDirectory(runtime.Plugins.Root); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(runtime.Plugins.Root) { UseShellExecute = true }); }
            catch (Exception ex) { runtime.SetStatus(L("无法打开插件目录：", "Could not open plugin folder: ") + ex.Message); }
        };
        open.Margin = new Thickness(0, 0, 0, 6); actions.Children.Add(open);
        Grid.SetColumn(actions, 1); intro.Children.Add(actions);
        intro.SizeChanged += (_, _) =>
        {
            var narrow = intro.ActualWidth < 620;
            Grid.SetRow(actions, narrow ? 1 : 0); Grid.SetColumn(actions, narrow ? 0 : 1);
            Grid.SetColumnSpan(actions, narrow ? 2 : 1);
            Grid.SetColumnSpan(explanation, narrow ? 2 : 1);
            actions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            actions.Margin = new Thickness(0, narrow ? 12 : 0, 0, 0);
        };
        root.Children.Add(Surface(intro));
        root.Children.Add(importStatus);
        if (runtime.Plugins.HasPendingUninstalls)
        {
            var pending = new StackPanel();
            pending.Children.Add(Text(L("有插件等待卸载完成。重启 Toolkit 后会继续清理，插件设置将保留。", "Plugin uninstalls are pending. Restart Toolkit to finish cleanup; plugin settings are retained."), 12));
            var restart = ActionButton(L("重启 Toolkit", "Restart Toolkit")); restart.HorizontalAlignment = HorizontalAlignment.Left; restart.Margin = new Thickness(0, 10, 0, 0);
            restart.Click += (_, _) => AskRestart(Window.GetWindow(this), L("需要重启 Toolkit 才能完成插件卸载。", "Restart Toolkit to finish uninstalling plugins."));
            pending.Children.Add(restart); root.Children.Add(Surface(pending));
        }
        root.Children.Add(Text(L($"已安装插件 · {runtime.Plugins.Installations.Count}", $"Installed plugins · {runtime.Plugins.Installations.Count}"), 13, muted: true));
        foreach (var error in runtime.Plugins.DiscoveryErrors)
            root.Children.Add(Notice(L("插件未能加载", "Plugin could not be loaded"), error, Palette.Danger));
        foreach (var plugin in runtime.Plugins.Installations)
            root.Children.Add(PluginCard(plugin));
        if (runtime.Plugins.Installations.Count == 0)
            root.Children.Add(Surface(Text(L("尚未安装插件。点击“导入插件”选择 ZIP 压缩包，或手动放入插件目录后重启。", "No plugins installed. Import a ZIP archive, or manually copy a plugin into the plugin folder and restart."), 14, muted: true)));
        var help = new StackPanel();
        help.Children.Add(Text(L("安装与更新", "Install and update"), 13, strong: true));
        help.Children.Add(Text(L("导入 ZIP 后可直接审核并启用；不会自动运行或覆盖已有插件。手动安装或更新需重启，更新后重新授权。", "Imported ZIPs are ready for review without restarting. Import never runs code or overwrites installed plugins. Manual installs and updates require a restart and approval."), 12, muted: true, top: 5));
        help.Children.Add(Text(L("无法正常启动时，可使用 --disable-plugins 跳过插件加载。", "If startup fails, use --disable-plugins to skip plugin loading."), 12, muted: true, top: 5));
        root.Children.Add(Surface(help));
        Content = root;
    }

    private Border PluginCard(PluginInstallation plugin)
    {
        var manifest = plugin.Manifest;
        var body = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var icon = IconTile("\uEA86", Palette.Accent, 42, 21);
        icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 2, 14, 0); header.Children.Add(icon);
        var identity = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        identity.Children.Add(Text(manifest.Name, 18, strong: true));
        if (!string.IsNullOrWhiteSpace(manifest.Author))
            identity.Children.Add(Text(L("作者：", "Author: ") + manifest.Author.Trim(), 12, muted: true, top: 5));
        var badges = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        badges.Children.Add(Badge("v" + manifest.Version));
        badges.Children.Add(Badge(plugin.PendingUninstall ? L("待卸载 · 需要重启", "Uninstall pending · Restart required") : plugin.Error is not null ? L("运行异常", "Error") : plugin.Enabled ? L("已启用", "Enabled") : L("已停用", "Disabled"),
            plugin.PendingUninstall ? Palette.Warning : plugin.Error is not null ? Palette.Danger : plugin.Enabled ? Palette.Success : Palette.Muted));
        if (manifest.FanBackend is not null && plugin.Enabled != (Runtime.Plugins.ActiveFanBackend?.PluginId == manifest.Id))
            badges.Children.Add(Badge(L("后端切换需重启", "Backend change needs restart"), Palette.Warning));
        identity.Children.Add(badges); Grid.SetColumn(identity, 1); header.Children.Add(identity);
        var toggle = new CheckBox { Content = L("启用插件", "Enable plugin"), IsChecked = plugin.Enabled,
            IsEnabled = !plugin.PendingUninstall, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(toggle, 2); header.Children.Add(toggle);
        header.SizeChanged += (_, _) =>
        {
            var narrow = header.ActualWidth < 600;
            Grid.SetColumnSpan(identity, narrow ? 2 : 1);
            Grid.SetRow(toggle, narrow ? 1 : 0); Grid.SetColumn(toggle, narrow ? 1 : 2);
            Grid.SetColumnSpan(toggle, narrow ? 2 : 1);
            toggle.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            toggle.Margin = new Thickness(0, narrow ? 10 : 0, 0, 0);
        };
        body.Children.Add(header);
        var sections = new AdaptiveUniformPanel { MinimumItemWidth = 280, MaximumColumns = 2, Spacing = 18, Margin = new Thickness(0, 18, 0, 0) };
        var permissions = Section(L("请求权限", "Requested permissions"));
        var permissionChips = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        foreach (var permission in manifest.Permissions.Distinct())
        {
            var chip = Badge(PermissionLabel(permission)); chip.ToolTip = permission; permissionChips.Children.Add(chip);
        }
        permissions.Children.Add(permissionChips);
        if (manifest.Permissions.Length == 0) permissions.Children.Add(Text(L("未请求宿主权限", "No host permissions requested"), 12, muted: true, top: 7));
        sections.Children.Add(permissions);
        var features = Section(L("扩展内容", "Contributions"));
        var featureChips = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        void Count(int count, string cn, string en) { if (count > 0) featureChips.Children.Add(Badge(L(cn, en) + " · " + count)); }
        Count(manifest.Pages.Length, "页面", "Pages"); Count(manifest.Settings.Length, "设置", "Settings");
        Count(manifest.Sensors.Length, "传感器", "Sensors"); Count(manifest.OverviewItems.Length, "概览条目", "Overview items");
        Count(manifest.SensorCategories.Length, "自建类别", "Custom categories");
        if (manifest.FanBackend is not null) featureChips.Children.Add(Badge(L("风扇后端", "Fan backend")));
        Count(manifest.Pages.Count(p => p.View is not null), "自绘页面", "Custom views");
        features.Children.Add(featureChips);
        if (featureChips.Children.Count == 0) features.Children.Add(Text(L("后台逻辑", "Background logic"), 12, muted: true, top: 7));
        var replacedSensors = manifest.Sensors.Where(s => s.Replaces is not null).Select(s => s.Name.Resolve(Runtime.IsChinese)).ToArray();
        if (replacedSensors.Length > 0) features.Children.Add(Text(L("替换读数：", "Replaced readings: ") + string.Join(L("、", ", "), replacedSensors), 12, muted: true, top: 6));
        sections.Children.Add(features); body.Children.Add(sections);
        if (manifest.Permissions.Contains("ui.custom"))
            body.Children.Add(Notice(L("主程序内运行 · 高风险", "In-process UI · High risk"), CustomUiWarning, Palette.Warning));
        if (manifest.FanBackend is not null)
            body.Children.Add(Notice(Runtime.Plugins.FanBackendStatus(plugin), FanWarning, Palette.Warning));
        var unclassified = manifest.Sensors.Where(s => s.Replaces is null && PluginSensorPlacement.OverviewCard(s.Category) is null &&
            PluginSensorPlacement.OsdGroup(s.Category) is null && !manifest.SensorCategories.Any(c => c.Id == s.Category)).ToArray();
        if (unclassified.Length > 0)
            body.Children.Add(Notice(L("部分传感器未分类", "Some sensors have no category"),
                L("仅在插件页面或记录中显示：", "Shown only on plugin pages or in recordings: ") + string.Join(", ", unclassified.Select(s => s.Name.Resolve(Runtime.IsChinese))), Palette.Warning));
        var error = Notice(L("插件未正常运行", "Plugin is not running normally"), plugin.Error ?? "", Palette.Danger);
        error.Visibility = plugin.Error is null ? Visibility.Collapsed : Visibility.Visible; body.Children.Add(error);
        var technical = TechnicalDetails(plugin);
        var details = new Expander
        {
            Header = L("技术详情", "Technical details"), IsExpanded = false, Foreground = Brush(Palette.Muted),
            FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 14, 0, 0),
            Content = Text(technical, 12, muted: true, top: 10)
        };
        var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(details);
        var uninstall = ActionButton(L("卸载", "Uninstall"), danger: true); uninstall.MinWidth = 76;
        uninstall.IsEnabled = !plugin.PendingUninstall; uninstall.VerticalAlignment = VerticalAlignment.Top; uninstall.Margin = new Thickness(14, 10, 0, 0);
        Grid.SetColumn(uninstall, 1); footer.Children.Add(uninstall); body.Children.Add(footer);
        uninstall.Click += async (_, _) =>
        {
            var owner = Window.GetWindow(this);
            if (ToolkitMessageBox.Show(owner, L("确定卸载此插件？插件文件会移除，插件设置保留。已加载的页面或风扇后端需要重启才能完成卸载。", "Uninstall this plugin? Its files will be removed and settings retained. Loaded custom views or fan backends require a restart to finish uninstalling."),
                    manifest.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            uninstall.IsEnabled = false; toggle.IsEnabled = false;
            try
            {
                var restart = await Runtime.Plugins.UninstallAsync(plugin);
                if (restart) AskRestart(owner, L("已停用插件并安排卸载。需要重启 Toolkit 完成清理；当前风扇后端会继续使用到安全退出。", "The plugin is disabled and scheduled for removal. Restart Toolkit to finish cleanup; the current fan backend remains available until safe exit."));
                else Runtime.SetStatus(L("已卸载 ", "Uninstalled ") + manifest.Name + L("，插件设置已保留。", "; plugin settings were retained."));
            }
            catch (Exception ex)
            {
                error.Child = Text(ex.Message, 12); error.Visibility = Visibility.Visible;
                uninstall.IsEnabled = !plugin.PendingUninstall; toggle.IsEnabled = !plugin.PendingUninstall;
                Runtime.SetStatus(L("卸载未完成：", "Uninstall did not finish: ") + ex.GetBaseException().Message);
            }
        };
        toggle.Click += async (_, _) =>
        {
            var enabled = toggle.IsChecked == true;
            var owner = Window.GetWindow(this);
            if (enabled && ToolkitMessageBox.Show(Window.GetWindow(this),
                L("仅启用你信任的插件。插件继承宿主权限，权限声明不是操作系统沙箱。\n\n", "Enable only trusted plugins. Plugins inherit host privileges; permission declarations are not an OS sandbox.\n\n") +
                (manifest.FanBackend is null ? "" : FanWarning + "\n\n") +
                (manifest.Permissions.Contains("ui.custom") ? CustomUiWarning + "\n\n" : "") + technical,
                manifest.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { toggle.IsChecked = plugin.Enabled; return; }
            toggle.IsEnabled = false;
            try
            {
                await Runtime.Plugins.SetEnabledAsync(plugin, enabled);
                if (Runtime.Plugins.RequiresRestart(plugin)) AskRestart(owner, Runtime.Plugins.FanBackendStatus(plugin));
            }
            catch (Exception ex)
            {
                error.Child = Text(ex.Message, 12); error.Visibility = Visibility.Visible; toggle.IsChecked = plugin.Enabled;
            }
            finally { toggle.IsEnabled = !plugin.PendingUninstall; }
        };
        var card = Surface(body); card.Tag = "plugin-card:" + manifest.Id; return card;
    }

    private string FanWarning => L("优先于程序目录中的后端 DLL，切换需重启。后端在主程序及恢复服务中执行，不受插件进程隔离保护。", "Overrides the application-directory backend DLL after restart. Backend code runs inside Toolkit and its recovery service, without plugin process isolation.");

    private void AskRestart(Window? owner, string reason)
    {
        if (Runtime.IsSystemSessionEnding || Runtime.ApplicationRestartRequested || owner is not null && !owner.IsLoaded) return;
        var message = reason + "\n\n" + L("是否立即重启 Toolkit？选择“否”可稍后手动重启。这不会重启 Windows。", "Restart Toolkit now? Choose No to restart later. Windows will not restart.");
        var answer = owner is null ? ToolkitMessageBox.Show(message, "ThinkBook Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : ToolkitMessageBox.Show(owner, message, "ThinkBook Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) Runtime.RequestApplicationRestart();
    }

    private string PermissionLabel(string permission) => permission switch
    {
        "sensors.read" => L("读取传感器", "Read sensors"), "data.read" => L("读取运行数据", "Read runtime data"),
        "settings.read" => L("读取设置", "Read settings"), "host.control" => L("控制宿主", "Control host"),
        "ui.custom" => L("自绘页面（进程内）", "Custom UI (in-process)"),
        "replace" => L("替换内置内容", "Replace built-in content"), "fan.backend" => L("风扇后端", "Fan backend"), _ => permission
    };

    private string TechnicalDetails(PluginInstallation plugin)
    {
        var m = plugin.Manifest;
        var lines = new System.Collections.Generic.List<string> { "ID: " + m.Id, "API: " + m.ApiVersion,
            "Assembly: " + m.EntryAssembly, "Entry type: " + m.EntryType, "Permissions: " + string.Join(", ", m.Permissions),
            L("目录：", "Folder: ") + plugin.Directory };
        if (!string.IsNullOrWhiteSpace(m.Author)) lines.Insert(1, L("作者：", "Author: ") + m.Author.Trim());
        lines.AddRange(m.Pages.Where(p => p.Replaces is not null).Select(p => "page: " + p.Replaces));
        lines.AddRange(m.Pages.Where(p => p.View is not null).Select(p => "view: " + p.Id + " · " + p.View!.Assembly + " · " + p.View.Type));
        lines.AddRange(m.Settings.Where(s => s.Replaces is not null).Select(s => "setting: " + s.Replaces));
        lines.AddRange(m.Sensors.Where(s => s.Replaces is not null).Select(s => "sensor: " + s.Replaces));
        lines.AddRange(m.OverviewItems.Select(i => "overview: " + i.CardId + "/" + (i.Target ?? i.Id) + " (" + i.Action + ")"));
        lines.AddRange(m.SensorCategories.Select(c => "category: " + c.Id + " · " + c.Title.Resolve(Runtime.IsChinese)));
        if (m.FanBackend is { } backend) { lines.Add("Fan assembly: " + backend.Assembly); lines.Add("Fan type: " + backend.Type); }
        return string.Join("\n", lines);
    }

    private string CustomUiWarning => L("自绘页面代码在 Toolkit 主程序中执行，拥有主程序权限，可能导致整个程序卡住或退出；不受工作进程隔离保护。仅启用可信代码，更新已加载的页面 DLL 需要重启。", "Custom page code runs inside Toolkit with its privileges and may freeze or terminate the entire application. Worker isolation does not apply. Trust the code before enabling it; updating loaded UI DLLs requires a restart.");

    private StackPanel Section(string heading)
    {
        var panel = new StackPanel(); panel.Children.Add(Text(heading, 12, muted: true)); return panel;
    }
    private TextBlock Text(string value, double size = 12, bool muted = false, bool strong = false, double top = 0) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = Brush(muted ? Palette.Muted : Palette.Text), Margin = new Thickness(0, top, 0, 0)
    };
    private Border Badge(string value, string? color = null) => new()
    {
        Background = Brush(Palette.SurfaceRaised), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4),
        Margin = new Thickness(0, 0, 6, 6), Child = new TextBlock { Text = value, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(color ?? Palette.Text) }
    };
    private Border Notice(string title, string detail, string accent)
    {
        var panel = new StackPanel();
        var heading = Text(title, 12, strong: true); heading.Foreground = Brush(accent); panel.Children.Add(heading);
        panel.Children.Add(Text(detail, 12, muted: true, top: 5));
        return new Border { Background = Brush(Palette.SurfaceRaised), BorderBrush = Brush(accent), BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 14, 0, 0), Child = panel };
    }
    private Border Surface(UIElement content) => new()
    {
        Background = Brush(Palette.Surface), BorderBrush = Brush(Palette.Border), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16), Padding = new Thickness(18), Margin = new Thickness(0, 10, 0, 12), Child = content
    };
}
