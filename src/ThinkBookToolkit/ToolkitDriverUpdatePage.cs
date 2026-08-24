using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ThinkBookToolkit;

internal sealed class ToolkitDriverUpdatePage : ToolkitPageBase
{
    private readonly DriverUpdateViewModel _viewModel;
    private readonly Button _scan;
    private readonly Button _install;
    private readonly CheckBox _showUpToDate;
    private readonly StackPanel _updates = new();
    private readonly TextBlock _status;
    private readonly Dictionary<DriverUpdateItem, Button> _downloadButtons = [];
    private readonly HashSet<string> _queuedPackageIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _installationGate = new(1, 1);
    private IReadOnlyList<DriverUpdateItem> _scanResults = [];
    private bool _hasScanned;
    private bool _scanBusy;
    private bool _batchInstallQueued;
    private int _queuedInstallCount;

    public ToolkitDriverUpdatePage(ToolkitRuntimeService runtime) : base(runtime)
    {
        _viewModel = new DriverUpdateViewModel(runtime);
        DataContext = _viewModel;
        _scan = ActionButton(L("扫描更新", "Scan for updates"), primary: true);
        _install = ActionButton(L("安装所有更新", "Install all updates"));
        _showUpToDate = new CheckBox
        {
            Content = L("显示无需更新的内容", "Show up-to-date items"),
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(8, 6, 8, 6)
        };
        _status = StatusText();
        Content = BuildLayout();
    }

    private UIElement BuildLayout()
    {
        _scan.Click += async (_, _) => await ScanAsync();
        _install.Click += async (_, _) => await InstallAllAsync();
        _showUpToDate.Checked += (_, _) => RenderVisibleUpdates();
        _showUpToDate.Unchecked += (_, _) => RenderVisibleUpdates();
        _install.IsEnabled = false;

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_scan);
        actions.Children.Add(_install);
        actions.Children.Add(_showUpToDate);

        var root = new StackPanel();
        root.Children.Add(Card(
            L("Lenovo 驱动更新", "Lenovo driver updates"),
            actions,
            L(
                "直接读取 Lenovo 公共目录并检查适用于当前设备的驱动与固件，无需安装 Lenovo System Update 组件。扫描和安装过程中请保持网络连接。",
                "Read Lenovo's public catalog directly to find applicable drivers and firmware without the Lenovo System Update component. Keep the device online while scanning or installing."),
            "\uE895"));

        root.Children.Add(Card(
            L("更新结果", "Update results"),
            _updates,
            L(
                "Toolkit 会检查适用条件、已安装版本、目录哈希、XML 签名和安装程序签名。无法安全判定的项目不会显示。",
                "Toolkit checks applicability, installed versions, catalog hashes, XML signatures, and installer signatures. Items that cannot be evaluated safely are not shown."),
            "\uE777"));

        ShowEmptyState(L(
            "尚未扫描。点击“扫描更新”获取当前设备的适用更新。",
            "Not scanned yet. Select Scan for updates to retrieve applicable updates for this device."));
        _status.Margin = new Thickness(4, 2, 4, 10);
        root.Children.Add(_status);
        return root;
    }

    private async Task ScanAsync()
    {
        if (_scanBusy || _queuedInstallCount > 0)
            return;
        _scanBusy = true;
        UpdateActionStates();
        _status.Text = L("正在通过 Lenovo 引擎扫描更新……", "Scanning with the Lenovo update engine…");
        try
        {
            var result = await _viewModel.ScanAsync();
            _scanResults = result.Updates;
            _hasScanned = true;
            RenderVisibleUpdates();
            var availableCount = result.Updates.Count(update =>
                update.IsUpdateRequired);
            _status.Text = availableCount == 0
                ? L("未发现适用于当前设备的新更新。", "No new updates applicable to this device were found.")
                : L(
                    $"发现 {availableCount} 项可用更新。",
                    $"{availableCount} available update(s) found.");
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Lenovo driver update scan failed.", ex);
            _status.Text = L("驱动更新扫描失败：", "Driver update scan failed: ") +
                           ex.Message;
            ShowEmptyState(L(
                "无法获取更新结果。请检查网络连接后重试。",
                "Update results are unavailable. Check the network connection and try again."));
        }
        finally
        {
            _scanBusy = false;
            UpdateActionStates();
        }
    }

    private async Task InstallAllAsync()
    {
        var updates = _scanResults
            .Where(update => update.IsUpdateRequired)
            .ToArray();
        if (updates.Length == 0 ||
            !ConfirmInstallation(updates, installingAll: true))
            return;

        await QueueInstallationAsync(updates, isBatch: true);
    }

    private bool ConfirmInstallation(
        IReadOnlyCollection<DriverUpdateItem> updates,
        bool installingAll)
    {
        var includesFirmware = updates.Any(update =>
            update.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase) ||
            update.Category.Contains("firmware", StringComparison.OrdinalIgnoreCase) ||
            update.Name.Contains("BIOS", StringComparison.OrdinalIgnoreCase) ||
            update.Name.Contains("firmware", StringComparison.OrdinalIgnoreCase));
        if (!includesFirmware && !installingAll)
            return true;

        var warning = includesFirmware
            ? L(
                "所选内容包含 BIOS 或固件更新。请连接电源、保存工作并暂停 BitLocker（如适用）。安装期间不要关闭电脑。是否继续？",
                "The selection includes BIOS or firmware. Connect AC power, save your work, and suspend BitLocker if applicable. Do not turn off the PC during installation. Continue?")
            : L(
                $"将下载并安装全部 {updates.Count} 项 Lenovo 更新。安装过程中设备或应用可能暂时不可用。是否继续？",
                $"Lenovo will download and install all {updates.Count} update(s). Devices or applications may be temporarily unavailable. Continue?");
        return MessageBox.Show(
                Window.GetWindow(this),
                warning,
                L("安装 Lenovo 更新", "Install Lenovo updates"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async Task QueueInstallationAsync(
        IReadOnlyCollection<DriverUpdateItem> updates,
        bool isBatch)
    {
        if (updates.Count == 0 ||
            updates.Any(update => _queuedPackageIds.Contains(update.PackageId)))
        {
            return;
        }

        foreach (var update in updates)
            _queuedPackageIds.Add(update.PackageId);
        _queuedInstallCount++;
        _batchInstallQueued |= isBatch;
        UpdateActionStates();

        await _installationGate.WaitAsync();
        string completionMessage;
        IReadOnlySet<string> successfulPackageIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            _status.Text = updates.Count == 1
                ? L(
                    $"正在下载并安装 {updates.First().Name}……",
                    $"Downloading and installing {updates.First().Name}…")
                : L(
                    $"正在下载并安装全部 {updates.Count} 项更新……",
                    $"Downloading and installing all {updates.Count} updates…");
            var result = await _viewModel.InstallAsync(updates);
            successfulPackageIds = updates
                .Select(update => update.PackageId)
                .Except(
                    result.FailedPackageIds,
                    StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (result.FailedPackageIds.Count > 0)
            {
                completionMessage = L(
                    $"部分更新未安装成功（{result.FailedPackageIds.Count} 项）。请重新扫描或打开 Lenovo Vantage 查看详情。",
                    $"Some updates were not installed ({result.FailedPackageIds.Count}). Scan again or open Lenovo Vantage for details.");
            }
            else
            {
                completionMessage = result.RebootNeeded
                    ? L("更新安装完成，需要重新启动电脑。", "Updates installed. Restart the PC to finish.")
                    : L("更新安装完成。", "Updates installed.");
            }
        }
        catch (Exception ex)
        {
            ToolkitLog.Error("Lenovo driver update installation failed.", ex);
            completionMessage = L("更新安装失败：", "Update installation failed: ") +
                                ex.Message;
        }
        finally
        {
            _installationGate.Release();
            _queuedInstallCount--;
        }

        foreach (var update in updates)
            _queuedPackageIds.Remove(update.PackageId);
        if (successfulPackageIds.Count > 0)
        {
            _scanResults = ApplySuccessfulInstallations(
                _scanResults,
                successfulPackageIds,
                keepAsUpToDate: _showUpToDate.IsChecked == true);
        }
        RenderVisibleUpdates();

        if (_queuedInstallCount == 0)
        {
            _batchInstallQueued = false;
            UpdateActionStates();
            _status.Text = completionMessage;
        }
        else
        {
            _status.Text = completionMessage + " " + L(
                "正在等待安装队列中的下一项。",
                "Waiting for the next queued installation.");
            UpdateActionStates();
        }
    }

    private void RenderUpdates(IReadOnlyList<DriverUpdateItem> updates)
    {
        _updates.Children.Clear();
        _downloadButtons.Clear();
        foreach (var update in updates)
        {
            var metadata = new List<string>();
            if (!string.IsNullOrWhiteSpace(update.Version))
                metadata.Add(L("版本 ", "Version ") + update.Version);
            if (!string.IsNullOrWhiteSpace(update.CurrentVersion))
                metadata.Add(L("当前 ", "Current ") + update.CurrentVersion);
            if (!string.IsNullOrWhiteSpace(update.Severity))
                metadata.Add(update.Severity);
            if (!string.IsNullOrWhiteSpace(update.RebootType))
                metadata.Add(DriverUpdateController.FormatRebootType(
                    update.RebootType,
                    Runtime.IsChinese));
            if (update.SizeBytes > 0)
                metadata.Add(DriverUpdateController.FormatSize(update.SizeBytes));
            if (!string.IsNullOrWhiteSpace(update.ReleaseDate))
                metadata.Add(update.ReleaseDate);

            _updates.Children.Add(BuildUpdateRow(
                update,
                string.Join(" · ", metadata)));
        }

        if (updates.Count == 0)
            ShowEmptyState(L("当前没有可用更新。", "No updates are currently available."));
        UpdateActionStates();
    }

    private void RenderVisibleUpdates()
    {
        if (!_hasScanned)
            return;
        var showUpToDate = _showUpToDate.IsChecked == true;
        var visible = _scanResults
            .Where(update => update.IsUpdateRequired ||
                             showUpToDate);
        RenderUpdates(SortUpdatesForDisplay(visible, showUpToDate));
    }

    private void ShowEmptyState(string message)
    {
        _updates.Children.Clear();
        _downloadButtons.Clear();
        _updates.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Brush(Palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 2, 2, 4)
        });
        UpdateActionStates();
    }

    private Border BuildUpdateRow(DriverUpdateItem update, string metadata)
    {
        var row = new Grid { MinHeight = 72 };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var labels = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        labels.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(update.Category)
                ? L("未分类", "Uncategorized")
                : update.Category,
            FontSize = 12,
            Foreground = Brush(Palette.Muted),
            TextWrapping = TextWrapping.Wrap
        });
        labels.Children.Add(new TextBlock
        {
            Text = update.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Text),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 12, 0)
        });
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            labels.Children.Add(new TextBlock
            {
                Text = metadata,
                FontSize = 12,
                Foreground = Brush(Palette.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 12, 0)
            });
        }
        row.Children.Add(labels);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(new TextBlock
        {
            Text = update.IsUpdateRequired
                ? L("需要更新", "Update available")
                : L("无需更新", "Up to date"),
            Foreground = Brush(update.IsUpdateRequired
                ? Palette.Warning
                : Palette.Success),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var download = ActionButton("\uE896", primary: update.IsUpdateRequired);
        download.FontFamily = new FontFamily(
            "Segoe Fluent Icons, Segoe MDL2 Assets");
        download.Width = 42;
        download.MinWidth = 42;
        download.Padding = new Thickness(0);
        download.Margin = new Thickness(12, 0, 0, 0);
        download.ToolTip = L(
            "下载并安装此更新",
            "Download and install this update");
        AutomationProperties.SetName(
            download,
            L(
                $"下载并安装 {update.Name}",
                $"Download and install {update.Name}"));
        download.Click += async (_, _) =>
        {
            if (ConfirmInstallation([update], installingAll: false))
                await QueueInstallationAsync([update], isBatch: false);
        };
        _downloadButtons[update] = download;
        SetDownloadBusy(
            download,
            _queuedPackageIds.Contains(update.PackageId));
        actions.Children.Add(download);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

        return new Border
        {
            Background = Brush(Palette.SurfaceRaised),
            BorderBrush = Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = row
        };
    }

    private void UpdateActionStates()
    {
        _scan.IsEnabled = !_scanBusy && _queuedInstallCount == 0;
        _install.IsEnabled = !_scanBusy &&
                             _queuedInstallCount == 0 &&
                             _scanResults.Any(update =>
                                 update.IsUpdateRequired);
        _showUpToDate.IsEnabled = !_scanBusy;
        foreach (var pair in _downloadButtons)
        {
            var queued = _queuedPackageIds.Contains(pair.Key.PackageId);
            SetDownloadBusy(pair.Value, queued);
            pair.Value.IsEnabled = !_scanBusy &&
                                   !_batchInstallQueued &&
                                   !queued;
        }
    }

    private static void SetDownloadBusy(Button button, bool busy)
    {
        if (!busy)
        {
            button.Content = "\uE896";
            return;
        }

        if (button.Content is TextBlock)
            return;
        var spinner = new TextBlock
        {
            Text = "\uE895",
            FontFamily = new FontFamily(
                "Segoe Fluent Icons, Segoe MDL2 Assets"),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform()
        };
        ((RotateTransform)spinner.RenderTransform).BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        button.Content = spinner;
    }

    internal static IReadOnlyList<DriverUpdateItem> ApplySuccessfulInstallations(
        IReadOnlyList<DriverUpdateItem> updates,
        IReadOnlySet<string> installedPackageIds,
        bool keepAsUpToDate) =>
        keepAsUpToDate
            ? updates.Select(update =>
                    installedPackageIds.Contains(update.PackageId)
                        ? update with
                        {
                            CurrentVersion = update.Version,
                            IsUpdateRequired = false
                        }
                        : update)
                .ToArray()
            : updates.Where(update =>
                    !installedPackageIds.Contains(update.PackageId))
                .ToArray();

    internal static IReadOnlyList<DriverUpdateItem> SortUpdatesForDisplay(
        IEnumerable<DriverUpdateItem> updates,
        bool includeUpToDate) =>
        updates
            .OrderByDescending(update =>
                includeUpToDate && update.IsUpdateRequired)
            .ThenByDescending(update => ParseReleaseDate(update.ReleaseDate))
            .ThenBy(update => update.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static DateTime ParseReleaseDate(string value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var date)
            ? date
            : DateTime.MinValue;

    private sealed class DriverUpdateViewModel : ToolkitViewModelBase
    {
        public DriverUpdateViewModel(ToolkitRuntimeService runtime) : base(runtime) { }

        public async Task<DriverUpdateScanResult> ScanAsync()
        {
            IsBusy = true;
            try
            {
                var result = await DriverUpdateController.ScanAsync(
                    Runtime.Settings.Language);
                ToolkitLog.Info(
                    "Lenovo driver update scan completed with " +
                    $"{result.Updates.Count(update => update.IsUpdateRequired)} " +
                    "available update(s) and " +
                    $"{result.Updates.Count(update => !update.IsUpdateRequired)} " +
                    "up-to-date item(s).");
                return result;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<DriverUpdateInstallResult> InstallAsync(
            IReadOnlyCollection<DriverUpdateItem> updates)
        {
            IsBusy = true;
            try
            {
                ToolkitLog.Info(
                    $"Starting Lenovo update installation for {updates.Count} selected package(s).");
                return await DriverUpdateController.InstallAsync(updates);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
