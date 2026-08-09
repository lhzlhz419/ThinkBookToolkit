using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThinkBookToolkit;
using ThinkBookToolkit.FanBackend;
using ThinkBookToolkit.Guardian;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            RunSmokeTests();
            Console.WriteLine("UI smoke tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void RunSmokeTests()
    {
        VerifyGpuModeRestartState();
        VerifyLenovoDependencyDirectory();
        VerifyFanBackendStartupNotice();
        VerifyAdvancedFanCurve();
        VerifyNvidiaPrivateTelemetryDecoding();
        VerifyGpuMonitorIsolationAndWatchdogProtocol();
        VerifySingleInstanceUpdateExitSignal();
        VerifyPowerSettingsWindowManualInput();
        VerifyPowerDeviceProfiles();
        VerifyOverviewLayoutSettings();
        VerifyAdaptiveUniformPanelCollapsedItems();
        VerifySystemShutdownPreparation();
        VerifyPerformanceFanLinkSettings();
        VerifyApplicationIconTransparency();
        VerifyPerformancePageWithoutFanControl();
        VerifyUserFacingExceptionText();
        Assert(UiTypography.FontFamilyNameFor("zh-CN") == "Microsoft YaHei UI",
            "Chinese UI font must use the Windows Simplified Chinese UI family.");
        Assert(UiTypography.FontFamilyNameFor("en-US") == "Segoe UI Variable Text",
            "English UI font must use the Windows 11 text family.");

        var settings = new AppSettings
        {
            Language = "zh-CN",
            Theme = "dark",
            IntervalSeconds = 2,
            CloseToTray = false
        };
        using var runtime = new ToolkitRuntimeService(settings);
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        var window = new ToolkitMainWindow(runtime, enableHardwareDetection: false);
        runtime.SetReportForTesting(CreateReport(_ => true));
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            ItsMode = ItsMode.Intelligent,
            GpuMode = GpuWorkingMode.HybridAuto,
            SupportedGpuModes =
            [
                GpuWorkingMode.Hybrid,
                GpuWorkingMode.HybridAuto,
                GpuWorkingMode.Discrete
            ],
            FanStrategy = ControlStrategy.FanCurve,
            FanControlRunning = true,
            FanTarget = new FanTargets(2600, 2700),
            Temperatures = new TemperatureSnapshot(
                54.2,
                43.8,
                52,
                13.6,
                12.8,
                "CPU Package",
                "GPU Core",
                "GPU Memory")
            {
                CpuName = "Intel Core Ultra 7 255HX",
                CpuLoadPercent = 16.4,
                CpuAverageClockMhz = 2350,
                CpuMaximumClockMhz = 4800,
                GpuName = "NVIDIA GeForce RTX 5060 Laptop GPU",
                GpuLoadPercent = 22.5,
                GpuMemoryLoadPercent = 18.2,
                GpuCoreClockMhz = 1552,
                GpuMemoryClockMhz = 6001,
                GpuHotSpotTempC = 51.3,
                VramChipTemperaturesC = [56, 58, 56, 54],
                PhysicalMemoryUsedGb = 16.5,
                PhysicalMemoryTotalGb = 31.4,
                VirtualMemoryUsedGb = 25.9,
                VirtualMemoryTotalGb = 37.2,
                MemorySlotTemperaturesC = [47],
                StorageDevices =
                [
                    new StorageTemperatureSnapshot(
                        "YMTC PC411-1024GB-B",
                        [30, 40, 30],
                        98.5)
                ]
            },
            Fans = new FanSnapshot(
                DateTimeOffset.Now,
                2400,
                2200,
                new Dictionary<string, FanLimit>()),
            Battery = new BatteryInformationSnapshot(
                32.8,
                0,
                -22,
                65,
                69.1,
                86.9,
                85.8,
                101.234,
                null,
                32,
                new DateTime(2025, 1, 1),
                new DateTime(2025, 1, 8),
                true),
            PowerSettings = new PowerSettingsState(
                125, 157, 97, 56, 10, 105, 87, 0, 75),
            Warranty = WarrantySnapshot.FromDates(
                new DateOnly(2025, 1, 1),
                new DateOnly(2028, 1, 1))
        });

        Assert(window.FontFamily.Source == "Microsoft YaHei UI",
            "Toolkit main window did not apply the selected Chinese font.");
        Assert(Descendants(window).OfType<Image>().Any(image =>
                   image.Source?.ToString()?.Contains(
                       "app-icon-tb.png",
                       StringComparison.OrdinalIgnoreCase) == true) &&
               !Descendants(window).OfType<TextBlock>().Any(block => block.Text == "TB"),
            "The sidebar brand does not use the application icon.");
        Assert(window.MainScrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
               window.MainScrollViewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "The main content area must own the only page scrollbar.");
        Assert(!window.ToastVisibleForTesting,
            "The transient notification is visible before an operation reports a result.");
        runtime.SetStatus("已复制到剪贴板");
        Assert(window.ToastVisibleForTesting &&
               window.ToastTextForTesting == "已复制到剪贴板",
            "Operation results are not shown as transient notifications.");
        var pages = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["overview"] = typeof(ToolkitOverviewPage),
            ["performance"] = typeof(ToolkitPerformancePage),
            ["cooling"] = typeof(ToolkitPerformancePage),
            ["battery"] = typeof(ToolkitBatteryPage),
            ["display"] = typeof(ToolkitDisplayPage),
            ["sound"] = typeof(ToolkitSoundPage),
            ["input"] = typeof(ToolkitInputPage),
            ["device"] = typeof(ToolkitDevicePage),
            ["advanced"] = typeof(ToolkitAdvancedPage),
            ["settings"] = typeof(ToolkitSettingsPage)
        };
        foreach (var expected in pages)
        {
            window.NavigateForTesting(expected.Key);
            var page = window.CurrentPage ?? throw new InvalidOperationException($"Page {expected.Key} was not created.");
            Assert(page.GetType() == expected.Value,
                $"Page {expected.Key} used {page.GetType().Name} instead of {expected.Value.Name}.");
            Assert(page is UserControl,
                $"Page {expected.Key} is not a UserControl.");
            Assert(page.DataContext is INotifyPropertyChanged,
                $"Page {expected.Key} has no ViewModel implementing INotifyPropertyChanged.");
            var innerScrollers = Descendants(page).OfType<ScrollViewer>().ToArray();
            Assert(expected.Key == "cooling"
                    ? innerScrollers.Length == 1 &&
                      IsAdvancedCurveHorizontalScroller(innerScrollers[0])
                    : innerScrollers.Length == 0,
                $"Page {expected.Key} contains an unexpected inner ScrollViewer.");
            page.Measure(new Size(900, double.PositiveInfinity));
            Assert(page.DesiredSize.Width <= 901,
                $"Page {expected.Key} forces horizontal overflow ({page.DesiredSize.Width:0.0}px). ");
            page.Measure(new Size(620, double.PositiveInfinity));
            Assert(page.DesiredSize.Width <= 621,
                $"Page {expected.Key} forces narrow-window overflow ({page.DesiredSize.Width:0.0}px). ");
        }

        window.NavigateForTesting("settings");
        Assert(ContainsText(window.CurrentPage!, "编辑概览页") &&
               ContainsText(window.CurrentPage!, "使用替代方案维持风扇满转") &&
               ContainsText(window.CurrentPage!, "持续写入风扇值") &&
               ContainsText(window.CurrentPage!, "强制刷新读数"),
            "New overview, fan-write, or reader-refresh settings are missing.");

        window.NavigateForTesting("overview");
        Assert(ContainsText(window.CurrentPage!, "性能模式") &&
               ContainsText(window.CurrentPage!, "GPU 模式"),
            "Overview is missing current operating modes.");
        var overviewSelectors = Descendants(window.CurrentPage!)
            .OfType<ComboBox>()
            .ToArray();
        Assert(overviewSelectors.Any(combo =>
                   combo.Items.OfType<ComboBoxItem>()
                       .Any(item => item.Tag is ItsMode)) &&
               overviewSelectors.Any(combo =>
                   combo.Items.OfType<ComboBoxItem>()
                       .Any(item => item.Tag is GpuWorkingMode)) &&
               overviewSelectors.Any(combo =>
                   combo.Items.OfType<ComboBoxItem>()
                       .Any(item => item.Tag is FanControlMode)),
            "Overview does not expose direct performance/GPU/fan-strategy selectors.");
        var overviewText = Descendants(window.CurrentPage!)
            .OfType<TextBlock>()
            .Select(block => block.Text)
            .ToList();
        Assert(!overviewText.Contains("显存") &&
               !overviewText.Contains("设备"),
            "Overview still contains the removed VRAM or device metric card.");
        var overviewIts = overviewSelectors.First(combo =>
            combo.Items.OfType<ComboBoxItem>().Any(item => item.Tag is ItsMode));
        var overviewGpu = overviewSelectors.First(combo =>
            combo.Items.OfType<ComboBoxItem>().Any(item => item.Tag is GpuWorkingMode));
        Assert(Labels(overviewIts).SequenceEqual(
                   ["智能模式", "省电模式", "性能模式", "极客模式"]) &&
               Labels(overviewGpu).SequenceEqual(
                   ["混合模式", "混合核显模式", "混合自动模式", "独显直连模式", "核显直连模式"]),
            "Overview mode selector text or order differs from Fan Control.");
        Assert(ContainsText(window.CurrentPage!, "风扇控制"),
            "Overview is missing current fan ownership.");
        Assert(!ContainsText(window.CurrentPage!, "独立配置") &&
               !ContainsText(window.CurrentPage!, "可替换风扇后端") &&
               !ContainsText(window.CurrentPage!, "快速控制"),
            "Overview still advertises implementation details or quick control.");
        var overviewCards = Descendants(window.CurrentPage!)
            .OfType<Border>()
            .Where(border => border.Tag is string id &&
                OverviewLayoutDefaults.CardDefinitions.ContainsKey(id))
            .Select(border => (string)border.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overviewViewModel = window.CurrentPage!.DataContext as HardwareMonitorViewModel;
        var warrantyRemainingRow = Descendants(window.CurrentPage!)
            .OfType<FrameworkElement>()
            .FirstOrDefault(element =>
                string.Equals(
                    element.Tag?.GetType().GetProperty("Property")
                        ?.GetValue(element.Tag) as string,
                    nameof(HardwareMonitorViewModel.WarrantyRemainingDays),
                    StringComparison.Ordinal));
        var warrantyRemainingItemId = warrantyRemainingRow?.Tag?.GetType()
            .GetProperty("ItemId")?.GetValue(warrantyRemainingRow.Tag) as string;
        Assert(overviewCards.SetEquals(
                   OverviewLayoutDefaults.CardDefinitions.Keys) &&
               ContainsText(window.CurrentPage!, "功耗限制") &&
               ContainsText(window.CurrentPage!, "保修信息") &&
               ContainsText(window.CurrentPage!, "剩余天数") &&
               warrantyRemainingItemId == "remaining-days" &&
               overviewViewModel?.WarrantyRemainingDays != "--" &&
               overviewViewModel?.PowerCpuPl1 == "125 W",
            $"Overview does not contain all seven configurable cards or power values. " +
            $"Cards: {string.Join(",", overviewCards)}; " +
            $"power={ContainsText(window.CurrentPage!, "功耗限制")}; " +
            $"warranty={ContainsText(window.CurrentPage!, "保修信息")}; " +
            $"value={overviewViewModel?.PowerCpuPl1}");
        settings.OverviewPageMode = OverviewPageMode.Compact;
        using (var compactOverview = new ToolkitOverviewPage(runtime))
        {
            var compactMetrics = Descendants(compactOverview)
                .OfType<AdaptiveUniformPanel>()
                .FirstOrDefault(panel => panel.Children.Count == 6 &&
                    panel.Children.OfType<Border>().All(card =>
                        Math.Abs(card.MinHeight - 126) < 0.1));
            Assert(compactMetrics is not null &&
                   ContainsText(compactOverview, "CPU") &&
                   ContainsText(compactOverview, "GPU") &&
                   ContainsText(compactOverview, "电池") &&
                   ContainsText(compactOverview, "内存") &&
                   ContainsText(compactOverview, "双风扇") &&
                   ContainsText(compactOverview, "保修信息") &&
                   !ContainsText(compactOverview, "功耗限制"),
                "Compact overview does not contain the requested six cards.");
            var compactViewModel = (HardwareMonitorViewModel)compactOverview.DataContext;
            Assert(compactViewModel.CompactBattery == "80% · 101.23% · 0.0 W" &&
                   !compactViewModel.CompactBattery.Contains("健康", StringComparison.Ordinal) &&
                   compactViewModel.CompactMemory == "52.5% · 47.0 °C" &&
                   compactViewModel.CompactWarranty.Contains("在保", StringComparison.Ordinal) &&
                   compactViewModel.CompactWarranty.Contains("天", StringComparison.Ordinal),
                "Compact battery, memory, or warranty values are incorrect.");
        }
        var compactLayout = OverviewLayoutDefaults.Clone(settings.OverviewLayout);
        compactLayout.Cards[OverviewCardIds.Cpu].Items["power"] = false;
        compactLayout.Cards[OverviewCardIds.Battery].Items["health"] = false;
        compactLayout.Cards[OverviewCardIds.Battery].Items["power"] = false;
        settings.OverviewLayout = compactLayout;
        using (var filteredCompactOverview = new ToolkitOverviewPage(runtime))
        {
            var cards = Descendants(filteredCompactOverview)
                .OfType<Border>()
                .Where(card => Math.Abs(card.MinHeight - 126) < 0.1)
                .ToArray();
            var cpuCard = cards.First(card => ContainsText(card, "CPU"));
            var batteryCard = cards.First(card => ContainsText(card, "电池"));
            Assert(Descendants(cpuCard).OfType<TextBlock>()
                       .Any(block => block.Text == "温度") &&
                   !ContainsText(cpuCard, "温度与功耗") &&
                   Descendants(batteryCard).OfType<TextBlock>()
                       .Any(block => block.Text == "电量") &&
                   !ContainsText(batteryCard, "健康度") &&
                   !ContainsText(batteryCard, "功率"),
                "Compact overview detail labels do not follow item visibility settings.");
        }
        settings.OverviewLayout = new OverviewLayoutSettings();
        settings.OverviewPageMode = OverviewPageMode.Detailed;
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            PowerSettings = runtime.Snapshot.PowerSettings! with
            {
                Atpp = null,
                AvailableSettings = PowerSettingAvailability.All &
                                    ~PowerSettingAvailability.Atpp
            }
        });
        Assert(overviewViewModel?.PowerAtppVisible == false &&
               overviewViewModel?.PowerGpuBoostVisible == true &&
               overviewViewModel?.PowerGpuToCpuVisible == true &&
               ContainsText(window.CurrentPage!, "CPU 温度墙") &&
               ContainsText(window.CurrentPage!, "GPU 温度墙"),
            "Unreadable overview power values are not hidden independently.");
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            PowerSettings = new PowerSettingsState(
                125, 157, 97, 56, 10, 105, 87, 0, 75)
        });
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            PendingGpuMode = GpuWorkingMode.HybridAuto.ToString(),
            PendingGpuModeSource = GpuWorkingMode.Discrete.ToString()
        });
        Assert(overviewGpu.SelectedItem is ComboBoxItem
               {
                   Tag: GpuWorkingMode.HybridAuto
               } &&
               window.CurrentPage!.DataContext?.GetType()
                   .GetProperty("PendingRestart")
                   ?.GetValue(window.CurrentPage.DataContext)
                   ?.ToString() ==
               "需要重启",
            "Overview does not show the pending GPU target and concise restart state.");
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            PendingGpuMode = string.Empty,
            PendingGpuModeSource = string.Empty
        });

        window.NavigateForTesting("performance");
        var performance = window.CurrentPage!;
        Assert(ContainsText(performance, "性能与 GPU 模式"),
            "Performance and GPU modes are not an independent region.");
        var modeChoices = Descendants(performance)
            .OfType<AdaptiveUniformPanel>()
            .First(panel => panel.Children.Count == 2);
        ArrangePanel(modeChoices, 850);
        Assert(SameRow(modeChoices.Children[0], modeChoices.Children[1]),
            "Performance and GPU mode choices use an unnecessarily wide breakpoint.");
        Assert(!ContainsText(performance, "当前控制归属") &&
               !ContainsText(performance, "控制策略") &&
               !ContainsText(performance, "实时状态"),
            "Fan controls or live status were not removed from the performance page.");

        window.NavigateForTesting("cooling");
        var cooling = window.CurrentPage!;
        Assert(ContainsText(cooling, "实时状态") &&
               ContainsText(cooling, "显存与热点"),
            "Cooling does not contain the compact live-status cards.");
        var fan1Target = cooling.DataContext?.GetType()
            .GetProperty("Fan1Target")
            ?.GetValue(cooling.DataContext)
            ?.ToString();
        var fan2Target = cooling.DataContext?.GetType()
            .GetProperty("Fan2Target")
            ?.GetValue(cooling.DataContext)
            ?.ToString();
        Assert(ContainsText(cooling, "转速目标") &&
               fan1Target == "2600 RPM" &&
               fan2Target == "2700 RPM" &&
               PropertyText(cooling.DataContext!, "CompactFanTargets") ==
               "2600 / 2700 RPM",
            "Live status does not expose the actual fan target.");
        var telemetryViewModel = cooling.DataContext!;
        var storageMetrics = PropertyValue<IReadOnlyList<HardwareMonitorMetric>>(
            telemetryViewModel,
            "StorageMetrics");
        Assert(PropertyText(telemetryViewModel, "CpuAverageFrequency") != "--" &&
               PropertyText(telemetryViewModel, "GpuMemoryUtilization") != "--" &&
               PropertyText(telemetryViewModel, "GpuMemoryTemperature") == "56/58/56/54 °C" &&
               PropertyText(telemetryViewModel, "PhysicalMemory") == "16.5 / 31.4 GB" &&
               storageMetrics.Count == 2 &&
               storageMetrics[0].Label.Contains(
                   "YMTC PC411-1024GB-B",
                   StringComparison.Ordinal) &&
               storageMetrics[0].Value == "30.0/40.0/30.0 °C" &&
               storageMetrics[1] == new HardwareMonitorMetric(
                   "硬盘1健康度",
                   "98.5%"),
            "Expanded CPU, GPU, memory, or storage telemetry is missing.");
        Assert(PropertyText(telemetryViewModel, "MemorySlot1Temperature") == "47.0 °C" &&
               PropertyText(telemetryViewModel, "MemorySlot2Temperature") == "-",
            "Missing memory-slot temperatures are not represented correctly.");
        var populatedTemperatures = runtime.Snapshot.Temperatures!;
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            FanTarget = new FanTargets(0, 0),
            Temperatures = populatedTemperatures with
            {
                VramChipTemperaturesC = []
            }
        });
        Assert(PropertyText(telemetryViewModel, "Fan1Target") == "固件自动" &&
               PropertyText(telemetryViewModel, "Fan2Target") == "固件自动" &&
               PropertyText(telemetryViewModel, "GpuMemoryTemperature") == "52.0 °C",
            "Firmware-auto zero targets or the LHM VRAM-temperature fallback are incorrect.");
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            FanTarget = new FanTargets(2600, 2700),
            Temperatures = populatedTemperatures
        });
        var telemetry = Descendants(cooling)
            .OfType<AdaptiveUniformPanel>()
            .FirstOrDefault(panel => panel.Children.Count == 5 &&
                panel.Children.OfType<Border>().All(card =>
                    Math.Abs(card.MinHeight - 126) < 0.1));
        Assert(telemetry is not null,
            "Cooling live status does not contain the five requested monitoring cards.");
        telemetry!.Measure(new Size(1400, double.PositiveInfinity));
        Assert(telemetry.DesiredSize.Height < 340,
            "Five live monitoring cards do not fit on one row at the target width.");
        var originalOverviewLayout = settings.OverviewLayout;
        var filteredLayout = OverviewLayoutDefaults.Clone(originalOverviewLayout);
        filteredLayout.Cards[OverviewCardIds.Cpu].Enabled = false;
        settings.OverviewLayout = filteredLayout;
        using (var filteredCooling = new ToolkitPerformancePage(
                   runtime,
                   coolingOnly: true))
        {
            var filteredTelemetry = Descendants(filteredCooling)
                .OfType<AdaptiveUniformPanel>()
                .FirstOrDefault(panel => panel.Children.Count == 4 &&
                    panel.Children.OfType<Border>().All(card =>
                        Math.Abs(card.MinHeight - 126) < 0.1));
            Assert(filteredTelemetry is not null,
                "Cooling live status does not follow overview card visibility settings.");
        }
        settings.OverviewLayout = originalOverviewLayout;
        Assert(Descendants(cooling).OfType<ComboBoxItem>()
                   .Any(item => item.Content?.ToString() == "固件自动"),
            "Cooling does not expose the firmware-automatic strategy.");
        Assert(ContainsText(cooling, "与性能模式联动") &&
               ContainsText(
                   cooling,
                   "切换性能模式时，自动切换风扇策略") &&
               Descendants(cooling).OfType<ComboBoxItem>().Any(item =>
                   item.Content?.ToString()?.StartsWith(
                       "风扇曲线 1：",
                       StringComparison.Ordinal) == true) &&
               GetPrivateField<CheckBox>(
                   cooling,
                   "_linkFanStrategyToPerformanceMode").IsChecked == false &&
               GetPrivateField<ComboBox>(
                   cooling,
                   "_fanControlTargetMode").SelectedItem is ComboBoxItem
               {
                   Tag: ItsMode.Unknown
               },
            "Cooling is missing the default performance/fan linkage controls or named fan profiles.");
        Assert(ContainsText(cooling, "风扇拉满") &&
                ContainsText(cooling, "最高转速运行") &&
                !ContainsText(cooling, "SetFullSpeed(true)") &&
                !ContainsText(cooling, "SetFullSpeed(false)") &&
                !ContainsText(cooling, "紧急散热"),
            "Full fan speed still exposes backend implementation details.");
        Assert(ContainsText(cooling, "当前固定转速状态") &&
                ContainsText(cooling, "写入 0 会将对应风扇交还固件控制"),
            "Fixed-RPM manual state or backend-specific zero-RPM semantics are missing.");
        var fixedPanel = GetPrivateField<StackPanel>(
            cooling,
            "_fixedPanel");
        var curvePanel = GetPrivateField<StackPanel>(
            cooling,
            "_curvePanel");
        var advancedCurvePanel = GetPrivateField<StackPanel>(
            cooling,
            "_advancedCurvePanel");
        var strategySelector = GetPrivateField<ComboBox>(
            cooling,
            "_strategy");
        Assert(fixedPanel.Visibility == Visibility.Collapsed &&
               curvePanel.Visibility == Visibility.Visible &&
               advancedCurvePanel.Visibility == Visibility.Collapsed &&
               Labels(strategySelector).SequenceEqual(
                   ["固件自动", "固定转速", "风扇曲线", "高级曲线"]),
            "Curve strategy did not hide fixed-only options.");
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            FanStrategy = ControlStrategy.AdvancedCurve,
            FanControlRunning = true
        });
        Assert(advancedCurvePanel.Visibility == Visibility.Visible &&
               fixedPanel.Visibility == Visibility.Collapsed &&
               curvePanel.Visibility == Visibility.Collapsed &&
               ContainsText(advancedCurvePanel, "高级曲线点位") &&
               ContainsText(advancedCurvePanel, "CPU 升速阈值") &&
               ContainsButtonText(advancedCurvePanel, "+") &&
               ContainsButtonText(advancedCurvePanel, "−"),
            "Advanced curve is missing from the unified strategy editor.");
        Assert(Descendants(advancedCurvePanel).OfType<Button>()
                   .Count(button => Equals(button.Content, "+")) == 12 &&
               Descendants(advancedCurvePanel).OfType<Button>()
                   .Count(button => Equals(button.Content, "−")) == 12,
            "Every advanced-curve point must have insert and remove actions.");
        var advancedLabels = Descendants(advancedCurvePanel)
            .OfType<Grid>()
            .Single(grid => Equals(grid.Tag, "AdvancedFanCurveLabels"));
        var advancedPoints = Descendants(advancedCurvePanel)
            .OfType<Grid>()
            .Single(grid => Equals(grid.Tag, "AdvancedFanCurvePoints"));
        Assert(advancedLabels.Height == advancedPoints.Height &&
               advancedLabels.RowDefinitions.Select(row => row.Height.Value)
                   .SequenceEqual(advancedPoints.RowDefinitions.Select(row => row.Height.Value)),
            "Advanced-curve labels and scrollable cells do not share aligned rows.");
        cooling.Measure(new Size(620, double.PositiveInfinity));
        Assert(cooling.DesiredSize.Width <= 621,
            "Advanced-curve editing forces horizontal page overflow.");
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            FanStrategy = ControlStrategy.FixedRpm,
            FanControlRunning = true
        });
        Assert(fixedPanel.Visibility == Visibility.Visible &&
               curvePanel.Visibility == Visibility.Collapsed,
            "Fixed strategy did not restore fixed-only options.");
        var fixedTable = Descendants(fixedPanel)
            .OfType<Grid>()
            .FirstOrDefault(grid =>
                grid.RowDefinitions.Count == 5 &&
                grid.ColumnDefinitions.Count == 5);
        Assert(fixedTable is not null,
            "Fixed RPM values are not presented as one four-row table.");
        Assert(MainWindow.RuntimeIsValidHotkey("Ctrl+Alt+F") &&
               MainWindow.RuntimeIsValidHotkey(string.Empty) &&
               !MainWindow.RuntimeIsValidHotkey("Ctrl+NotAKey"),
            "Fixed-mode hotkey validation is incomplete.");
        var draftStatus = GetPrivateField<TextBlock>(cooling, "_draftStatus");
        Assert(string.IsNullOrWhiteSpace(draftStatus.Text) &&
               draftStatus.Visibility == Visibility.Collapsed,
            "The clean fan draft still shows a log-like status.");
        Assert(!ContainsText(cooling, "开机自启") &&
               !ContainsText(cooling, "启动到托盘"),
            "Global preferences still appear on the performance page.");
        var performanceScrollers = Descendants(cooling)
            .OfType<ScrollViewer>()
            .ToArray();
        Assert(performanceScrollers.Length == 1 &&
               IsAdvancedCurveHorizontalScroller(performanceScrollers[0]),
            "Cooling page contains an unexpected nested scrollbar.");
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            PendingGpuMode = GpuWorkingMode.HybridAuto.ToString(),
            PendingGpuModeSource = GpuWorkingMode.Discrete.ToString()
        });
        Assert(ContainsText(performance, "等待重启") &&
               ContainsButtonText(performance, "立即重启") &&
               ContainsText(
                   performance,
                   "将从“独显直连模式”切换到“混合自动模式”") &&
               GetPrivateField<ComboBox>(performance, "_gpuMode")
                   .SelectedItem is ComboBoxItem
                   {
                       Tag: GpuWorkingMode.HybridAuto
                   },
            "Pending GPU changes have no inline restart state or action.");
        runtime.SetSnapshotForTesting(runtime.Snapshot with { PendingGpuMode = string.Empty });
        Assert(ContainsText(performance, "CPU PL1") &&
               ContainsText(performance, "GPU Power Boost"),
            "Power settings do not show the current-value summary.");
        Assert(Descendants(performance).OfType<Slider>().Any(),
            "Fully available power settings have no editor sliders.");
        Assert(GetPrivateField<StackPanel>(
                   performance,
                   "_powerEditorPanel").Visibility == Visibility.Collapsed,
            "Power editor is not collapsed by default.");
        var confirmedPower = new PowerSettingsState(130, 185, 100, 56, 15, 100, 85, 10, 75);
        performance.GetType()
            .GetMethod("ApplyPowerState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(performance, [confirmedPower, true]);
        GetPrivateField<Button>(performance, "_togglePowerEditor")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var powerEditorHost = GetPrivateField<Border>(performance, "_powerEditorHost");
        Assert(GetPrivateField<StackPanel>(
                   performance,
                   "_powerEditorPanel").Visibility == Visibility.Visible &&
               ContainsText(powerEditorHost, "参数调整") &&
               Descendants(powerEditorHost).OfType<TextBox>()
                   .Select(box => box.Text)
                   .Contains("130") &&
               Descendants(powerEditorHost).OfType<TextBox>()
                   .Select(box => box.Text)
                   .Contains("185"),
            "Expanding power settings did not seed the editor from current values.");
        var atppReadout = GetPrivateField<FrameworkElement>(
            performance,
            "_atppReadout");
        var atppEditorRow = GetPrivateField<FrameworkElement>(
            performance,
            "_atppEditorRow");
        var atppSlider = Descendants(atppEditorRow).OfType<Slider>().Single();
        var atppTextBox = Descendants(atppEditorRow).OfType<TextBox>().Single();
        Assert(atppReadout.Visibility == Visibility.Visible &&
               atppEditorRow.Visibility == Visibility.Visible &&
               atppSlider.Minimum == 25 &&
               atppSlider.Maximum == 105 &&
               atppTextBox.Text == "75",
            "Available ATPP is not shown with the required 25–105 W slider range.");
        atppTextBox.Text = "106";
        object?[] collectAtppArguments = [null, null];
        Assert((bool)performance.GetType()
                   .GetMethod(
                       "TryCollectPower",
                       BindingFlags.Instance | BindingFlags.NonPublic)!
                   .Invoke(performance, collectAtppArguments)! &&
               collectAtppArguments[0] is PowerSettingsState { Atpp: 106 },
            "ATPP manual input above the slider range is not accepted.");
        atppTextBox.Text = "0";
        collectAtppArguments = [null, null];
        Assert(!(bool)performance.GetType()
                   .GetMethod(
                       "TryCollectPower",
                       BindingFlags.Instance | BindingFlags.NonPublic)!
                   .Invoke(performance, collectAtppArguments)!,
            "ATPP accepts a non-positive manual value.");
        atppTextBox.Text = "75";
        var gpuBoostSlider = Descendants(powerEditorHost)
            .OfType<Slider>()
            .Single(slider => slider.Minimum == 0 && slider.Maximum == 15);
        var gpuBoostTextBox = ((Panel)gpuBoostSlider.Parent)
            .Children
            .OfType<TextBox>()
            .Single();
        gpuBoostTextBox.Text = "0";
        object?[] collectGpuBoostArguments = [null, null];
        Assert((bool)performance.GetType()
                   .GetMethod(
                       "TryCollectPower",
                       BindingFlags.Instance | BindingFlags.NonPublic)!
                   .Invoke(performance, collectGpuBoostArguments)! &&
               collectGpuBoostArguments[0] is PowerSettingsState
               {
                   GpuPowerBoost: 0
               } &&
               gpuBoostSlider.Value == 0,
            "GPU Power Boost does not accept zero while preserving the 0–15 slider range.");
        gpuBoostTextBox.Text = "20";
        collectGpuBoostArguments = [null, null];
        Assert((bool)performance.GetType()
                   .GetMethod(
                       "TryCollectPower",
                       BindingFlags.Instance | BindingFlags.NonPublic)!
                   .Invoke(performance, collectGpuBoostArguments)! &&
               collectGpuBoostArguments[0] is PowerSettingsState
               {
                   GpuPowerBoost: 20
               } &&
               gpuBoostSlider.Value == 15,
            "GPU Power Boost manual input is incorrectly capped by the 0–15 slider range.");
        gpuBoostTextBox.Text = "-1";
        collectGpuBoostArguments = [null, null];
        Assert(!(bool)performance.GetType()
                   .GetMethod(
                       "TryCollectPower",
                       BindingFlags.Instance | BindingFlags.NonPublic)!
                   .Invoke(performance, collectGpuBoostArguments)!,
            "GPU Power Boost accepts a negative manual value.");
        gpuBoostTextBox.Text = "15";
        var powerLockToggles = GetPrivateField<
            Dictionary<PowerSetting, CheckBox>>(
                performance,
                "_powerLockToggles");
        Assert(powerLockToggles.Count == 9 &&
               Enum.GetValues<PowerSetting>().All(
                   setting => powerLockToggles.TryGetValue(setting, out var toggle) &&
                              toggle.IsChecked == false &&
                              Equals(toggle.Content, "锁定") &&
                              toggle.Parent is Grid lockLayout &&
                              lockLayout.Children.Count == 2 &&
                              ReferenceEquals(lockLayout.Children[1], toggle) &&
                              Grid.GetColumn(lockLayout.Children[0]) == 0 &&
                              Grid.GetColumn(toggle) == 1 &&
                              toggle.Margin.Left == 18 &&
                              toggle.Margin.Right == 0) &&
               GetPrivateField<ComboBox>(performance, "_powerLockInterval") is
               { SelectedItem: ComboBoxItem { Tag: 2 } } powerLockInterval &&
               Labels(powerLockInterval).SequenceEqual(
                   ["1 秒", "2 秒", "3 秒", "5 秒", "10 秒"]) &&
               ContainsText(powerEditorHost, "锁定检查间隔") &&
               !ContainsText(powerEditorHost, "锁定功耗设置"),
            "Power-setting lock controls or interval defaults are incorrect.");
        Assert(Labels(GetPrivateField<ComboBox>(cooling, "_smoothing"))
                   .SequenceEqual(["1", "2", "3", "5", "10"]) &&
               Labels(GetPrivateField<ComboBox>(cooling, "_rampDown"))
                   .SequenceEqual(["10", "20", "50", "100", "无限制"]) &&
               Labels(GetPrivateField<ComboBox>(cooling, "_gameHold"))
                   .SequenceEqual(["0", "10", "20", "30", "60"]),
            "Fan timing selector text or order differs from Fan Control.");

        window.NavigateForTesting("battery");
        Assert(ContainsText(window.CurrentPage!, "充电模式"),
            "Battery settings are not inline.");
        foreach (var label in new[]
                 {
                     "最小功率", "最大功率", "满充容量", "设计容量",
                     "本次电池使用", "生产日期", "首次使用日期"
                 })
        {
            Assert(ContainsText(window.CurrentPage!, label),
                $"Battery details are missing {label}.");
        }
        var batteryHealth = window.CurrentPage!.DataContext?.GetType()
            .GetProperty("Health")
            ?.GetValue(window.CurrentPage.DataContext)
            ?.ToString();
        Assert(batteryHealth == "101.23%",
            "Battery health does not retain two decimal places.");
        var batteryCombos = Descendants(window.CurrentPage!).OfType<ComboBox>().ToArray();
        Assert(batteryCombos.Any(combo => Labels(combo).SequenceEqual(["养护", "普通", "快充"])) &&
               batteryCombos.Any(combo => Labels(combo).SequenceEqual(["关闭", "仅睡眠时开启", "保持开启"])),
            "Battery selector text or order differs from Fan Control.");
        VerifySwitchAndCombo(window.CurrentPage!);

        window.NavigateForTesting("display");
        Assert(ContainsText(window.CurrentPage!, "Vantage 护眼") &&
               ContainsText(window.CurrentPage!, "可能只有在核显模式下可用") &&
               ContainsText(window.CurrentPage!, "色彩管理"),
            "Display controls are not inline native cards or lack the compatibility note.");
        Assert(Descendants(window.CurrentPage!).OfType<ComboBoxItem>()
                   .Any(item => item.Content?.ToString() == "生动") &&
               Descendants(window.CurrentPage!).OfType<ComboBoxItem>()
                   .Any(item => item.Content?.ToString() == "泛黄") &&
               Descendants(window.CurrentPage!).OfType<ComboBoxItem>()
                   .Any(item => item.Content?.ToString() == "Native") &&
               !Descendants(window.CurrentPage!).OfType<ComboBoxItem>()
                   .Any(item => item.Content?.ToString() == "原生"),
            "Display color labels do not match the original software.");
        var gamutCombo = Descendants(window.CurrentPage!).OfType<ComboBox>()
            .First(combo => combo.Items.OfType<ComboBoxItem>()
                .Any(item => item.Tag is ColorManagementMode));
        Assert(gamutCombo.Items.OfType<ComboBoxItem>().First().Tag is ColorManagementMode.Default,
            "Default is not the first display gamut option.");
        Assert(Labels(gamutCombo).Take(10).SequenceEqual(
                   ["默认", "Adobe RGB", "sRGB", "Display P3", "Native", "REC709", "DCI P3", "自动", "DICOM Dim", "DICOM Office"]),
            "Display gamut text or order differs from Fan Control.");
        var temperatureCombo = Descendants(window.CurrentPage!).OfType<ComboBox>()
            .First(combo => combo.Items.OfType<ComboBoxItem>()
                .Any(item => Equals(item.Tag, 2700)));
        Assert(Labels(temperatureCombo).First() == "2700 K" &&
               Labels(temperatureCombo).Last() == "6500 K" &&
               Labels(temperatureCombo).Count == 39,
            "Custom color-temperature choices differ from Fan Control.");

        window.NavigateForTesting("sound");
        Assert(ContainsText(window.CurrentPage!, "Dolby Atmos") &&
               ContainsText(window.CurrentPage!, "麦克风降噪"),
            "Sound controls are not inline native cards.");
        Assert(ContainsText(window.CurrentPage!, "扬声器降噪") &&
               !ContainsText(window.CurrentPage!, "消除通话对端"),
            "Speaker noise cancellation still uses the old wording.");
        var soundCombos = Descendants(window.CurrentPage!).OfType<ComboBox>().ToArray();
        Assert(soundCombos.Any(combo => Labels(combo).SequenceEqual(
                   ["动态", "电影", "音乐", "游戏", "语音", "自定义"])) &&
               soundCombos.Any(combo => Labels(combo).SequenceEqual(
                   ["正常", "声音识别", "仅我的声音", "多人声音", "关"])),
            "Sound selector text or order differs from Fan Control.");

        window.NavigateForTesting("input");
        Assert(ContainsText(window.CurrentPage!, "键盘背光亮度") &&
               ContainsText(window.CurrentPage!, "触摸板"),
            "Input settings are incomplete.");
        Assert(Descendants(window.CurrentPage!).OfType<ComboBox>()
                .Any(combo => Labels(combo).SequenceEqual(["自动", "低", "高", "关闭"])),
            "Keyboard-backlight selector text or order differs from Fan Control.");

        window.NavigateForTesting("device");
        Assert(ContainsText(window.CurrentPage!, "正在读取设备信息"),
            "Device page has no inline loading state.");
        Assert(!Descendants(window.CurrentPage!).OfType<ScrollViewer>().Any(),
            "Device page has a second scrollbar.");
        var eyeIconMethod = typeof(ToolkitDevicePage).GetMethod(
            "EyeIcon",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(ToolkitDevicePage),
                "EyeIcon");
        var openEye = (Grid)eyeIconMethod.Invoke(
            window.CurrentPage!,
            [false])!;
        var slashedEye = (Grid)eyeIconMethod.Invoke(
            window.CurrentPage!,
            [true])!;
        Assert(openEye.Children.Count == 1 &&
               !openEye.Children.OfType<System.Windows.Shapes.Line>().Any() &&
               slashedEye.Children.OfType<System.Windows.Shapes.Line>().Count() == 1,
            "Serial-number show/hide states do not use eye and slashed-eye icons.");

        window.NavigateForTesting("advanced");
        Assert(ContainsButtonText(window.CurrentPage!, "开机画面"),
            "Advanced tools are missing the boot-logo button.");
        Assert(!Descendants(window.CurrentPage!).OfType<CheckBox>().Any(checkBox =>
                checkBox.Content?.ToString()?.Contains("Windows", StringComparison.Ordinal) == true),
            "The boot-logo editor is still embedded in the advanced page.");
        var logoWindow = new BootLogoCustomizationWindow(
            owner: null,
            key => MainWindow.Translate(key, true),
            isDark: true,
            window.FontFamily,
            window.FontSize);
        var preview = (Grid)(typeof(BootLogoCustomizationWindow)
            .GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(logoWindow)
            ?? throw new MissingFieldException("Boot logo preview was not found."));
        Assert(Math.Abs(preview.Width / preview.Height - 1.6) < .001,
            "Boot-logo preview does not use a 16:10 ratio.");
        Assert(ContainsText(logoWindow, "预览的画面与实际开机画面不一定相同") &&
               logoWindow.ResizeMode == ResizeMode.CanResize &&
               logoWindow.Height <= SystemParameters.WorkArea.Height,
            "Boot-logo dialog is not responsive or is missing its preview disclaimer.");
        logoWindow.Close();

        window.NavigateForTesting("settings");
        var settingsPage = window.CurrentPage!;
        var settingsPanels = Descendants(settingsPage)
            .OfType<AdaptiveUniformPanel>()
            .ToArray();
        var globalSettings = settingsPanels.Single(panel => panel.Children.Count == 6);
        var startupSettings = settingsPanels.Single(panel =>
            ContainsText(panel, "开机自启"));
        var fanBehaviorSettings = settingsPanels.Single(panel =>
            ContainsText(panel, "持续写入风扇值"));
        ArrangePanel(globalSettings, 1000);
        ArrangePanel(startupSettings, 900);
        ArrangePanel(fanBehaviorSettings, 900);
        Assert(SameRow(globalSettings.Children[0], globalSettings.Children[1]) &&
               SameRow(globalSettings.Children[1], globalSettings.Children[2]) &&
               SameRow(globalSettings.Children[3], globalSettings.Children[4]) &&
               SameRow(globalSettings.Children[4], globalSettings.Children[5]) &&
               !SameRow(globalSettings.Children[2], globalSettings.Children[3]),
            "Global settings require too much width for the requested 3+3 layout.");
        Assert(SameRow(startupSettings.Children[0], startupSettings.Children[1]) &&
               SameRow(startupSettings.Children[2], startupSettings.Children[3]) &&
               SameRow(fanBehaviorSettings.Children[0], fanBehaviorSettings.Children[1]) &&
               !ContainsText(startupSettings, "睡眠时关闭风扇控制") &&
               !ContainsText(startupSettings, "风扇读写最小间隔") &&
               !ContainsText(fanBehaviorSettings, "睡眠时关闭风扇控制") &&
               !ContainsText(fanBehaviorSettings, "风扇读写最小间隔"),
            "Startup and fan behavior settings require too much width for two-column rows.");
        var settingSwitchRows = Descendants(settingsPage)
            .OfType<Border>()
            .Where(border => border.Child is Grid grid &&
                grid.Children.OfType<CheckBox>().Any())
            .ToArray();
        foreach (var border in settingSwitchRows)
        {
            border.Measure(new Size(400, double.PositiveInfinity));
            border.Arrange(new Rect(0, 0, 400, border.DesiredSize.Height));
        }
        Assert(settingSwitchRows.Length >= 7 && settingSwitchRows.All(border =>
            border.Child is Grid grid &&
            grid.Children.OfType<CheckBox>().All(toggle =>
                Grid.GetRow(toggle) == 0 &&
                Grid.GetColumn(toggle) == grid.ColumnDefinitions.Count - 1)),
            "One or more setting switches move below their labels at compact widths.");
        Assert(ContainsText(settingsPage, "启动与程序行为") &&
               !ContainsText(settingsPage, "启动与窗口行为"),
            "The startup section does not use the requested program-behavior title.");
        Assert(!ContainsText(settingsPage, "关闭单项后，同一行剩余的数据会自动占满整行。") &&
               settingsPage.GetType().GetField(
                       "_overviewEditorWindow",
                       BindingFlags.Instance | BindingFlags.NonPublic)
                   ?.GetValue(settingsPage) is null,
            "The overview editor is still embedded in the settings page.");
        Assert(ContainsText(settingsPage, "全局设置") &&
               ContainsText(settingsPage, "完整功能监测结果"),
            "Settings is missing global preferences or inline availability.");
        Assert(ContainsText(settingsPage, "概览页模式选择") &&
               Labels(GetPrivateField<ComboBox>(settingsPage, "_overviewMode"))
                   .SequenceEqual(["简洁模式", "详细模式"]),
            "Settings does not expose the compact/detailed overview mode selector.");
        Assert(!ContainsText(settingsPage, "重新启动"),
            "Settings copy still describes appearance changes in terms of restarting.");
        foreach (var text in new[]
                 {
                     "界面语言", "主题", "状态刷新间隔", "开机自启", "启动到托盘",
                     "最小化到托盘", "关闭时最小化", "睡眠时关闭风扇控制",
                     "风扇读写最小间隔"
                 })
        {
            Assert(ContainsText(settingsPage, text), $"Settings is missing {text}.");
        }
        Assert(string.IsNullOrWhiteSpace(
                   GetPrivateField<TextBox>(
                       settingsPage,
                       "_fanReadMinimumInterval").Text) &&
                string.IsNullOrWhiteSpace(
                    GetPrivateField<TextBox>(
                        settingsPage,
                        "_fanWriteMinimumInterval").Text) &&
                ContainsText(settingsPage, "过短的间隔可能造成卡顿") &&
                ContainsText(settingsPage, "默认间隔"),
            "Fan I/O interval overrides are not blank by default or their defaults are not explained.");
        Assert(Descendants(settingsPage).OfType<ComboBox>()
                .SelectMany(combo => combo.Items.OfType<ComboBoxItem>())
                .Any(item => item.Content?.ToString() == "跟随系统"),
            "System theme option is missing.");
        Assert(ContainsText(settingsPage, "可用/总共：") &&
               Descendants(settingsPage).OfType<StackPanel>()
                   .Any(panel => panel.Visibility == Visibility.Collapsed &&
                                 Descendants(panel).OfType<Border>().Any()),
            "Availability summary is not compact or its details are not collapsed by default.");
        var availabilityToggle = Descendants(settingsPage).OfType<Button>()
            .First(button => button.Content?.ToString() == "展开检测详情");
        Assert(LogicalTreeHelper.GetParent(availabilityToggle) is Grid,
            "Availability expand action is not placed in the card header row.");
        var availabilityBadges = Descendants(settingsPage)
            .OfType<Border>()
            .Where(border => border.Child is TextBlock block &&
                             block.Text is "可用" or "不可用" or "部分可用")
            .ToArray();
        Assert(availabilityBadges.Length > 0 &&
               availabilityBadges.All(border =>
                   border.VerticalAlignment == VerticalAlignment.Center &&
                   border.Child is TextBlock
                   {
                       TextAlignment: TextAlignment.Center,
                       HorizontalAlignment: HorizontalAlignment.Center,
                       VerticalAlignment: VerticalAlignment.Center
                   }),
            "Availability badges are not centered in their rows.");
        Assert(!ContainsText(settingsPage, "运行环境") &&
               !ContainsText(settingsPage, "配置目录") &&
               !ContainsButtonText(settingsPage, "查看所有功能"),
            "Removed runtime/configuration/popup content is still present.");
        var centeredRows = Descendants(settingsPage).OfType<Grid>()
            .Where(grid => Math.Abs(grid.MinHeight - 62) < .01)
            .ToArray();
        Assert(centeredRows.Length > 0 &&
               centeredRows.All(grid =>
                   grid.RowDefinitions.Count >= 1 &&
                   grid.RowDefinitions[0].Height.IsStar),
            "Setting-row content is not vertically centered.");

        var overviewButton = Descendants(window).OfType<Button>()
            .First(button => Equals(button.Tag, "overview"));
        var performanceButton = Descendants(window).OfType<Button>()
            .First(button => Equals(button.Tag, "performance"));
        var overviewWeight = overviewButton.FontWeight;
        var performanceWeight = performanceButton.FontWeight;
        window.NavigateForTesting("overview");
        window.NavigateForTesting("performance");
        Assert(overviewButton.FontWeight == overviewWeight &&
               performanceButton.FontWeight == performanceWeight,
            "Navigation selection changes text metrics and causes vertical movement.");
        Assert(overviewButton.FocusVisualStyle is null &&
               performanceButton.FocusVisualStyle is null &&
               Descendants(window).OfType<ComboBox>()
                   .All(combo => combo.FocusVisualStyle is null),
            "Controls still expose the dotted default focus visual after Alt.");
        window.NavigateForTesting("overview");
        Assert(!ContainsText(window, "可用/总共"),
            "Feature counts are visible outside Settings.");
        var trayFanSnapshot = runtime.Snapshot with
        {
            FanControlRunning = true,
            FullSpeed = false,
            FanStrategy = ControlStrategy.FanCurve
        };
        Assert(ToolkitRuntimeService.ResolveTrayFanMode(trayFanSnapshot) ==
                   FanControlMode.FanCurve &&
               ToolkitRuntimeService.ResolveTrayFanMode(
                   trayFanSnapshot with
                   {
                       FanControlRunning = false,
                       FullSpeed = false
                   }) == FanControlMode.FirmwareAutomatic &&
               ToolkitRuntimeService.ResolveTrayFanMode(
                   trayFanSnapshot with
                   {
                       FanControlRunning = false,
                       FullSpeed = true,
                       FanStrategy = ControlStrategy.FixedRpm
                   }) == FanControlMode.FixedRpm &&
               ToolkitRuntimeService.ResolveTrayFanMode(
                   trayFanSnapshot with
                   {
                       FanStrategy = ControlStrategy.AdvancedCurve
                   }) == FanControlMode.AdvancedCurve,
            "Tray fan-strategy check state does not follow the active fan mode.");

        runtime.SetReportForTesting(CreateReport(id =>
            id is FeatureIds.PerformanceMode or FeatureIds.GpuMode));
        window.NavigateForTesting("performance");
        var independentPerformance = window.CurrentPage!;
        Assert(ContainsText(independentPerformance, "性能与 GPU 模式"),
            "Performance/GPU controls disappeared when fan control is unavailable.");
        Assert(!ContainsText(independentPerformance, "控制策略") &&
               !ContainsText(independentPerformance, "CPU PL1"),
            "Unavailable fan/power controls were not hidden independently.");

        runtime.SetReportForTesting(CreateReport(id => id == FeatureIds.WarrantyInformation));
        window.NavigateForTesting("device");
        var warrantyOnly = (ToolkitDevicePage)window.CurrentPage!;
        warrantyOnly.RenderForTesting();
        Assert(ContainsText(warrantyOnly, "保修信息"),
            "Warranty does not render independently in the native Toolkit style.");

        using var partialRuntime = new ToolkitRuntimeService(new AppSettings
        {
            Language = "zh-CN",
            Theme = "light"
        });
        var partialReport = new FeatureAvailabilityReport([
            new FeatureAvailability(
                FeatureIds.PowerSettings,
                "性能",
                "功耗设置",
                false,
                "8 项功耗参数可读取；写入仅支持 ThinkBook 16p G6 IAX",
                PartiallyAvailable: true)
        ]);
        partialRuntime.SetReportForTesting(partialReport);
        using var partialPowerPage = new ToolkitPerformancePage(partialRuntime);
        Assert(partialReport.IsAvailable(FeatureIds.PowerSettings) &&
               !partialReport.IsFullyAvailable(FeatureIds.PowerSettings) &&
               partialReport.IsPartiallyAvailable(FeatureIds.PowerSettings),
            "Partially available power capability is not modeled correctly.");
        Assert(ContainsText(partialPowerPage, "CPU PL1"),
            "Partially available power values are not shown.");
        using var partialSettingsPage = new ToolkitSettingsPage(partialRuntime);
        Assert(ContainsText(partialSettingsPage, "可用/总共：1/1") &&
               ContainsText(partialSettingsPage, "部分可用"),
            "Partial power capability is not represented in feature monitoring.");

        using var atppRuntime = new ToolkitRuntimeService(new AppSettings
        {
            Language = "zh-CN",
            Theme = "light"
        });
        atppRuntime.SetReportForTesting(new FeatureAvailabilityReport([
            new FeatureAvailability(
                FeatureIds.PowerSettings,
                "性能",
                "功耗设置",
                true,
                "ATPP 可调整。",
                EnglishDetail: "ATPP is adjustable.")
        ]));
        using var atppSettingsPage = new ToolkitSettingsPage(atppRuntime);
        Assert(ContainsText(atppSettingsPage, "ATPP 可调整。") &&
               ContainsText(atppSettingsPage, "可用"),
            "Feature monitoring does not note that ATPP is adjustable.");

        window.UpdateResponsiveForTesting(900);
        Assert(window.SidebarCollapsed,
            "Sidebar did not collapse for a narrow window.");
        window.UpdateResponsiveForTesting(1300);
        Assert(!window.SidebarCollapsed,
            "Sidebar remained collapsed for a wide window.");

        using var startupRuntime = new ToolkitRuntimeService(new AppSettings
        {
            Language = "zh-CN",
            Theme = "light",
            CloseToTray = false
        });
        var startupWindow = new ToolkitMainWindow(
            startupRuntime,
            enableHardwareDetection: true);
        Assert(startupWindow.CurrentPage is ToolkitOverviewPage &&
               Descendants(startupWindow).OfType<Button>()
                   .Where(button => button.Tag is string)
                   .Select(button => button.Tag?.ToString())
                   .All(id => id is "overview" or "settings"),
            "Startup does not render Overview first or creates hardware pages before detection.");
        startupWindow.Close();

        using var englishRuntime = new ToolkitRuntimeService(new AppSettings
        {
            Language = "en-US",
            Theme = "light"
        });
        englishRuntime.SetReportForTesting(new FeatureAvailabilityReport([
            new FeatureAvailability(
                FeatureIds.FanControl,
                "散热",
                "风扇监控与控制",
                true,
                "不应显示的内部实现信息",
                EnglishDetail:
                    "Private backend implementation detail"),
            new FeatureAvailability(
                FeatureIds.PowerSettings,
                "性能",
                "功耗设置",
                false,
                "仅支持 ThinkBook 16p G6 IAX")
        ]));
        using var englishSettings = new ToolkitSettingsPage(englishRuntime);
        Assert(ContainsText(englishSettings, "Complete feature availability") &&
               ContainsText(englishSettings, "Power values cannot be viewed or changed") &&
               !ContainsText(englishSettings, "Private backend implementation detail") &&
               !ContainsText(englishSettings, "不应显示的内部实现信息") &&
               !ContainsText(englishSettings, "仅支持"),
            "Availability results expose implementation details or untranslated detection text.");

        var curve = new CurveEditor(
            "test",
            [30, 40, 50],
            [1500, 3000, 5500],
            [1500, 3000, 5500]);
        curve.SetRpmRanges(1600, 5200, 1800, 5000);
        Assert(!curve.Focusable &&
               curve.Fan1Values.SequenceEqual([1600, 3000, 5200]) &&
               curve.Fan2Values.SequenceEqual([1800, 3000, 5000]),
            "Curve editor focus suppression or per-fan range clamping is incorrect.");
        var curveHost = new Border { Child = curve };
        var bringIntoViewBubbled = false;
        curveHost.AddHandler(
            FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(
                (_, _) => bringIntoViewBubbled = true));
        curve.BringIntoView();
        Assert(!bringIntoViewBubbled,
            "Curve editor can still request ancestor scrolling while dragging.");

        var detectedMethod = typeof(MainWindow).GetMethod(
            "TryReadDetectedFanLimits",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("WMI fan-limit resolver was not found.");
        object?[] detectedArguments =
        [
            new Dictionary<string, FanLimit>
            {
                ["fan1"] = new("Fan 1", 1, 0, 5600),
                ["fan2"] = new("Fan 2", 2, 1600, 5400)
            },
            null
        ];
        Assert((bool)detectedMethod.Invoke(null, detectedArguments)! &&
               detectedArguments[1] is FanRpmLimits
               {
                   Fan1MinimumRpm: 0,
                   Fan1MaximumRpm: 5600,
                   Fan2MinimumRpm: 1600,
                   Fan2MaximumRpm: 5400
               },
            "Default fan limits are not resolved from per-fan WMI values.");
        object?[] fallbackArguments =
        [
            new Dictionary<string, FanLimit>(),
            null
        ];
        Assert(!(bool)detectedMethod.Invoke(null, fallbackArguments)! &&
               fallbackArguments[1] is FanRpmLimits
               {
                   Fan1MinimumRpm: 1500,
                   Fan1MaximumRpm: 5500,
                   Fan2MinimumRpm: 1500,
                   Fan2MaximumRpm: 5500
               },
            "Invalid WMI fan limits do not fall back to 1500–5500 RPM.");
        var defaultLimits = new AppSettings();
        Assert(!defaultLimits.FanRpmLimitsCustomized &&
               defaultLimits.FanRpmLimits.Fan1MinimumRpm == 1500 &&
               defaultLimits.FanRpmLimits.Fan1MaximumRpm == 5500,
            "Fan-limit fallback defaults are not 1500–5500 RPM.");
        var limitsWindow = new FanRpmLimitsWindow(
            owner: null,
            defaultLimits.FanRpmLimits,
            new FanBackendControlSemantics(
                FanTargetZeroBehavior.StopFanWhileKeepingManualControl,
                FanAutomaticControlRestoreMechanism.DedicatedBackendOperation,
                "Dedicated restore operation",
                new(
                    FanFullSpeedControlMechanism.DedicatedBackendOperation,
                    "Dedicated full-speed enable operation",
                    "Dedicated full-speed disable operation")),
            isChinese: true,
            isDark: false,
            window.FontFamily,
            window.FontSize);
        Assert(ContainsText(limitsWindow, "警告：") &&
               ContainsText(limitsWindow, "概不负责") &&
               ContainsText(limitsWindow, "关闭对应风扇") &&
               ContainsText(limitsWindow, "固件自动") &&
               !ContainsText(limitsWindow, "Dedicated restore operation") &&
               Descendants(limitsWindow).OfType<TextBox>()
                   .Select(box => box.Text)
                   .SequenceEqual(["1500", "5500", "1500", "5500"]),
            "Fan-limit dialog is missing defaults or the required warning.");
        var tryReadRpm = typeof(FanRpmLimitsWindow).GetMethod(
            "TryRead",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Fan-limit input validator was not found.");
        object?[] zeroRpmArguments = [new TextBox { Text = "0" }, null];
        Assert(CurveProfileStore.AbsoluteMinimumFanRpm == 0 &&
               (bool)tryReadRpm.Invoke(null, zeroRpmArguments)! &&
               zeroRpmArguments[1] is 0,
            "Fan-limit lower-bound input does not accept 0 RPM.");
        Assert(typeof(IFanBackend).GetProperty(nameof(IFanBackend.ControlSemantics)) is not null,
            "Fan backends do not declare zero-RPM and automatic-restore semantics.");
        Assert(FanBackendContract.CurrentVersion == new Version(1, 1) &&
               typeof(IFanBackend).GetProperty(nameof(IFanBackend.ApiVersion)) is not null,
            "The fan-backend API version is not declared as 1.1.");
        Assert(typeof(IFanBackend).GetProperty(
                   nameof(IFanBackend.StartupNotice)) is not null,
            "Fan backends cannot declare an optional startup notice.");
        Assert(typeof(IFanBackend).GetProperty(nameof(IFanBackend.MinimumReadInterval)) is not null &&
               typeof(IFanBackend).GetProperty(nameof(IFanBackend.MinimumWriteInterval)) is not null,
            "Fan backends do not declare minimum read/write intervals.");
        Assert(typeof(FanBackendControlSemantics).GetProperty(
                   nameof(FanBackendControlSemantics.FullSpeed)) is not null,
            "Fan backends do not declare full-speed enable/disable semantics.");
        var wmiBackend =
            new ThinkBookToolkit.FanBackend.Wmi.WmiFanBackend();
        Assert(wmiBackend.MinimumReadInterval == TimeSpan.FromSeconds(0.5) &&
               wmiBackend.MinimumWriteInterval == TimeSpan.FromSeconds(6) &&
               wmiBackend.ApiVersion == new Version(1, 1) &&
               wmiBackend.StartupNotice is null,
            "WMI backend read/write interval defaults are incorrect.");
        Assert(MainWindow.ResolveFanIoMinimumInterval(
                   null,
                   TimeSpan.FromSeconds(6)) == TimeSpan.FromSeconds(6) &&
               MainWindow.ResolveFanIoMinimumInterval(
                   1,
                   TimeSpan.FromSeconds(6)) == TimeSpan.FromSeconds(1) &&
               MainWindow.ResolveFanIoMinimumInterval(
                   0.5,
                   TimeSpan.FromSeconds(6)) == TimeSpan.FromSeconds(6),
            "Fan I/O interval overrides do not preserve backend defaults and positive-integer overrides.");
        Assert(CurveProfileStore.IsValidFanIoIntervalOverride(null) &&
               CurveProfileStore.IsValidFanIoIntervalOverride(1) &&
               CurveProfileStore.IsValidFanIoIntervalOverride(60) &&
               !CurveProfileStore.IsValidFanIoIntervalOverride(0) &&
               !CurveProfileStore.IsValidFanIoIntervalOverride(0.5) &&
               !CurveProfileStore.IsValidFanIoIntervalOverride(1.5) &&
               !CurveProfileStore.IsValidFanIoIntervalOverride(-1),
            "Fan I/O interval validation is incorrect.");
        Assert(MainWindow.CanAttemptDisableControlOnSleep(
                   true,
                   TimeSpan.FromSeconds(0.5),
                   TimeSpan.FromSeconds(6)) &&
               MainWindow.CanAttemptDisableControlOnSleep(
                   false,
                   TimeSpan.FromSeconds(0.5),
                   TimeSpan.FromSeconds(0.5)) &&
               MainWindow.CanAttemptDisableControlOnSleep(
                   false,
                   TimeSpan.FromSeconds(1),
                   TimeSpan.FromSeconds(1)) &&
               !MainWindow.CanAttemptDisableControlOnSleep(
                   false,
                   TimeSpan.FromSeconds(0.5),
                   TimeSpan.FromSeconds(6)),
            "Sleep-release availability does not follow backend support and effective I/O intervals.");
        Assert(new AppSettings() is
               {
                   ConfigurationVersion: CurveProfileStore.CurrentConfigurationVersion,
                   FanReadMinimumIntervalSeconds: null,
                    FanWriteMinimumIntervalSeconds: null,
                    AttemptDisableControlOnSleepWhenUnsupported: false,
                    PowerSettingsLocks: { Any: false },
                    PowerSettingsLockIntervalSeconds: 2,
                    PowerSettingsLockTarget: null,
                    PowerSettingsLocksByMode.Count: 0
               },
            "Fan I/O interval overrides or unsupported-backend sleep behavior have unsafe defaults.");
        var cpuPl1Lock = new PowerSettingsLockSelection { CpuPl1 = true };
        var atppLock = new PowerSettingsLockSelection { Atpp = true };
        var g6PowerProfile = PowerSettingsController.ResolveProfile(
            "ThinkBook 16p G6 IAX");
        Assert(CurveProfileStore.IsLegacyPowerSettingsLockEnabled(
                   "{\"PowerSettingsLockEnabled\":true}") &&
               !CurveProfileStore.IsLegacyPowerSettingsLockEnabled(
                   "{\"PowerSettingsLockEnabled\":false}") &&
               PowerSettingsController.LockIntervals.SequenceEqual(
                   [1, 2, 3, 5, 10]) &&
               PowerSettingsController.IsSupportedLockInterval(2) &&
               !PowerSettingsController.IsSupportedLockInterval(4) &&
               !PowerSettingsController.RequiresLockReapply(
                    confirmedPower,
                    confirmedPower,
                    cpuPl1Lock) &&
               !PowerSettingsController.RequiresLockReapply(
                    confirmedPower,
                    confirmedPower with { GpuPowerBoost = 14 },
                    cpuPl1Lock) &&
               PowerSettingsController.RequiresLockReapply(
                    confirmedPower,
                    confirmedPower with { CpuPl1 = 129 },
                    cpuPl1Lock) &&
               !PowerSettingsController.RequiresLockReapply(
                    confirmedPower with { Atpp = null },
                    confirmedPower,
                    atppLock) &&
               PowerSettingsController.IsValidLockConfiguration(
                    cpuPl1Lock,
                    confirmedPower,
                    g6PowerProfile) &&
               !PowerSettingsController.IsValidLockConfiguration(
                    new PowerSettingsLockSelection(),
                    confirmedPower,
                    g6PowerProfile) &&
               !PowerSettingsController.IsValidLockConfiguration(
                    atppLock,
                    confirmedPower with { Atpp = null },
                    g6PowerProfile) &&
               PowerSettingsController.WithSetting(
                    confirmedPower,
                    confirmedPower with { CpuPl1 = 129, CpuPl2 = 150 },
                    PowerSetting.CpuPl1) ==
                    confirmedPower with { CpuPl1 = 129 } &&
               PowerSettingsController.IsValidState(
                    confirmedPower with { Atpp = 106 }, g6PowerProfile) &&
               PowerSettingsController.IsValidState(
                    confirmedPower with { GpuPowerBoost = 0 }, g6PowerProfile) &&
               !PowerSettingsController.IsValidState(
                    confirmedPower with { GpuPowerBoost = -1 }, g6PowerProfile) &&
               !PowerSettingsController.IsValidState(
                   confirmedPower with { Atpp = 0 }, g6PowerProfile),
            "Power-setting lock interval or change-detection policy is incorrect.");
        Assert(PowerSettingsController.GetDefaultState(ItsMode.PowerSaving, g6PowerProfile)?.Atpp == 25 &&
               PowerSettingsController.GetDefaultState(ItsMode.Intelligent, g6PowerProfile)?.Atpp == 45 &&
               PowerSettingsController.GetDefaultState(ItsMode.Performance, g6PowerProfile)?.Atpp == 85 &&
               PowerSettingsController.GetDefaultState(ItsMode.Geek, g6PowerProfile)?.Atpp == 105,
            "ATPP defaults do not match the four performance modes.");
        var intelligentLocks = new PowerSettingsLockSelection { CpuPl1 = true };
        var performanceLocks = new PowerSettingsLockSelection { CpuPl2 = true };
        settings.PowerSettingsLocks = new PowerSettingsLockSelection();
        settings.PowerSettingsLockTarget = null;
        settings.PowerSettingsLocksByMode = new Dictionary<string, PowerModeLockSettings>
        {
            [ItsMode.Intelligent.ToString()] = new()
            {
                Locks = intelligentLocks,
                Target = confirmedPower with { CpuPl1 = 95 }
            },
            [ItsMode.Performance.ToString()] = new()
            {
                Locks = performanceLocks,
                Target = confirmedPower with { CpuPl2 = 157 }
            }
        };
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            ItsMode = ItsMode.Intelligent
        });
        Assert(runtime.CurrentPowerSettingsLocks.CpuPl1 &&
               !runtime.CurrentPowerSettingsLocks.CpuPl2,
            "Intelligent-mode power locks were not selected.");
        runtime.SetSnapshotForTesting(runtime.Snapshot with
        {
            ItsMode = ItsMode.Performance
        });
        Assert(runtime.CurrentPowerSettingsLocks.CpuPl2 &&
               !runtime.CurrentPowerSettingsLocks.CpuPl1 &&
               CurveProfileStore.NormalizePowerModeLocks(
                   settings.PowerSettingsLocksByMode).Count == 2,
            "Power locks are not isolated by performance mode.");
        Assert(MainWindow.SuppressSmallTargetChanges(
                   new FanTargets(1599, 1600),
                   new FanTargets(1500, 1500)) ==
               new FanTargets(1500, 1600),
            "The 100 RPM threshold is not applied independently to both fans.");
        Assert(MainWindow.ResolveContinuousFanWriteInterval(
                   TimeSpan.FromSeconds(2),
                   TimeSpan.FromSeconds(6)) == TimeSpan.FromSeconds(6) &&
               MainWindow.ResolveContinuousFanWriteInterval(
                   TimeSpan.FromSeconds(5),
                   TimeSpan.FromSeconds(0.5)) == TimeSpan.FromSeconds(5),
            "Continuous fan writes do not use the longer configured interval.");
        var gen6FanLimits = CurveProfileStore.DefaultFanRpmLimitsForModel(
            "ThinkBook 14 G6+ IMH");
        Assert(gen6FanLimits.Fan1MaximumRpm == 6400 &&
               gen6FanLimits.Fan2MaximumRpm == 6400 &&
               DeviceModelDetector.UsesAlternativeFullSpeedByDefault(
                   "ThinkBook 14 G6+ IMH") &&
               DeviceModelDetector.UsesAlternativeFullSpeedByDefault(
                   "ThinkBook 16p G6 ADR") &&
               !DeviceModelDetector.UsesAlternativeFullSpeedByDefault(
                   "ThinkBook 16p G6 IAX"),
            "Model-specific full-speed and fan-limit defaults are incorrect.");
        Assert(MainWindow.EffectiveRampDownRate(0, 20) == 20 &&
               MainWindow.EffectiveRampDownRate(50, 0) == 50 &&
               MainWindow.EffectiveRampDownRate(50, 20) == 20,
            "Full-range and post-high-temperature ramp-down limits are not combined safely.");
        try
        {
            _ = FanController.CreateBackendInstance(
                typeof(ThrowingFanBackend));
            throw new InvalidOperationException(
                "A throwing backend constructor unexpectedly succeeded.");
        }
        catch (NotSupportedException ex)
            when (ex.Message == ThrowingFanBackend.FailureMessage)
        {
        }
        limitsWindow.Close();

        window.Close();
    }

    private static void VerifyFanBackendStartupNotice()
    {
        var chinese = new FanBackendStartupNoticeText(
            "中文标题",
            "中文内容");
        var english = new FanBackendStartupNoticeText(
            "English title",
            "English content");
        var notice = new FanBackendStartupNotice(
            new Dictionary<string, FanBackendStartupNoticeText>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["zh-CN"] = chinese,
                ["en-US"] = english
            },
            english);

        Assert(notice.Resolve("zh-CN") == chinese &&
               notice.Resolve("zh-TW") == chinese &&
               notice.Resolve("en-US") == english &&
               notice.Resolve("fr-FR") == english,
            "Backend startup notice localization or fallback is incorrect.");

        var settings = new AppSettings();
        Assert(FanBackendStartupNoticePreference.ReconcileBackend(
                   settings,
                   "backend-a") &&
               settings.LastFanBackendIdentity == "backend-a" &&
               string.IsNullOrEmpty(
                   settings.SuppressedFanBackendStartupNoticeIdentity),
            "The first backend identity was not recorded.");

        var pending = FanBackendStartupNoticePreference.GetPending(
            settings,
            "backend-a",
            notice,
            "zh-CN");
        Assert(pending is
               {
                   BackendIdentity: "backend-a",
                   Title: "中文标题",
                   Content: "中文内容"
               },
            "The first declared backend notice was not prepared.");
        Assert(FanBackendStartupNoticePreference.Suppress(
                   settings,
                   "backend-a") &&
               FanBackendStartupNoticePreference.GetPending(
                   settings,
                   "backend-a",
                   notice,
                   "zh-CN") is null,
            "The do-not-show preference was not applied.");

        Assert(FanBackendStartupNoticePreference.ReconcileBackend(
                   settings,
                   "backend-b") &&
               string.IsNullOrEmpty(
                   settings.SuppressedFanBackendStartupNoticeIdentity) &&
               FanBackendStartupNoticePreference.GetPending(
                   settings,
                   "backend-b",
                   null,
                   "zh-CN") is null,
            "Replacing a backend did not reset the notice preference.");
        Assert(FanBackendStartupNoticePreference.ReconcileBackend(
                   settings,
                   "backend-a") &&
               FanBackendStartupNoticePreference.GetPending(
                   settings,
                   "backend-a",
                   notice,
                   "en-US")?.Title == "English title",
            "Replacing the backend with a previously used DLL did not show its notice again.");

        var dialog = new FanBackendStartupNoticeWindow(
            chinese.Title,
            chinese.Content,
            "zh-CN",
            isDark: false);
        var labels = Descendants(dialog)
            .OfType<Button>()
            .Select(button => button.Content?.ToString())
            .ToArray();
        Assert(labels.SequenceEqual(["确定", "确定并不再显示"]),
            "The backend notice dialog does not expose the required two actions.");
    }

    private static void VerifyNvidiaPrivateTelemetryDecoding()
    {
        var memoryRaw = 48u << 16;
        Assert(NvidiaPrivateTelemetryReader.DecodeMemoryChipTemperature(memoryRaw) == 56,
            "The per-chip VRAM MR-code decoder does not match the HWiNFO 6034 formula.");
        var hotSpotRaw = 0x40000000u | (2416u << 3);
        var hotSpot = NvidiaPrivateTelemetryReader.DecodeBlackwellHotSpot(
            [null, hotSpotRaw, 0u]);
        Assert(hotSpot == 75.5,
            "The RTX 50-series hot-spot decoder does not match the HWiNFO 6034 formula.");

        var smartLog = new byte[512];
        WriteUInt16(smartLog, 1, 303);
        WriteUInt16(smartLog, 200, 313);
        WriteUInt16(smartLog, 202, 304);
        var storageTemperatures = StorageTemperatureReader.ReadNvmeTemperatures(smartLog);
        Assert(storageTemperatures.Count == 3 &&
               Math.Abs(storageTemperatures[0] - 29.85) < 0.001 &&
               Math.Abs(storageTemperatures[1] - 39.85) < 0.001 &&
               Math.Abs(storageTemperatures[2] - 30.85) < 0.001,
            "NVMe composite and per-sensor temperatures are not decoded correctly.");
        smartLog[5] = 12;
        Assert(StorageTemperatureReader.ReadNvmeHealthPercent(smartLog) == 88 &&
               StorageTemperatureReader.ReadNvmeHealthPercent([]) is null,
            "NVMe remaining-health fallback does not decode Percentage Used correctly.");
    }

    private static void VerifyGpuMonitorIsolationAndWatchdogProtocol()
    {
        var unreliable = new GpuDevicePresenceSnapshot(1, [], false);
        var reliable = new GpuDevicePresenceSnapshot(
            2,
            ["NVIDIA GeForce RTX 5060 Laptop GPU"],
            true);
        Assert(unreliable.IsActive("NVIDIA GeForce RTX 5060 Laptop GPU") &&
               reliable.IsActive("GeForce RTX 5060"),
            "Display-adapter presence snapshots do not preserve fallback and normalized matching behavior.");

        var snapshot = new GpuMonitorWorkerSnapshot(
            "GPU",
            10,
            20,
            1000,
            2000,
            50,
            60,
            55,
            [54, 55],
            30,
            "core",
            "memory");
        var roundTrip = JsonSerializer.Deserialize<GpuMonitorWorkerSnapshot>(
            JsonSerializer.Serialize(snapshot));
        Assert(roundTrip is not null &&
               roundTrip.Name == snapshot.Name &&
               roundTrip.PowerW == snapshot.PowerW &&
               roundTrip.MemoryChipTemperaturesC.SequenceEqual(
                   snapshot.MemoryChipTemperaturesC),
            "The isolated GPU monitor JSON contract does not round-trip.");

        var fields = typeof(TemperatureReader).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(fields.Any(field => field.FieldType == typeof(GpuMonitorWorkerClient)) &&
               fields.All(field => field.FieldType != typeof(NvidiaPrivateTelemetryReader)),
            "TemperatureReader must keep native NVIDIA polling outside the Toolkit process.");
        Assert(GpuMonitorWorker.ReadCommand == "READ" &&
               GpuMonitorWorker.ReadNonNvidiaCommand == "READ_NON_NVIDIA" &&
               GpuMonitorWorker.ReadCommand != GpuMonitorWorker.ReadNonNvidiaCommand,
            "The GPU worker and client do not expose distinct normal and non-NVIDIA fallback commands.");
        Assert(FanWatchdogClient.ServiceName == FanWatchdogService.ServiceNameValue &&
               string.Equals(
                   FanWatchdogClient.MarkerDirectory(),
                   FanWatchdogService.MarkerDirectory(),
                   StringComparison.OrdinalIgnoreCase),
            "The Toolkit client and Windows watchdog service disagree on their protocol names.");
    }

    private static void VerifySingleInstanceUpdateExitSignal()
    {
        var instanceId = Guid.NewGuid().ToString("N");
        if (!SingleInstanceCoordinator.TryAcquireForTesting(
                instanceId,
                out var coordinator) ||
            coordinator is null)
        {
            throw new InvalidOperationException(
                "The single-instance coordinator could not be acquired for the update-exit protocol test.");
        }
        using (coordinator)
        using (var activated = new ManualResetEventSlim())
        using (var exitRequested = new ManualResetEventSlim())
        {
            coordinator.Listen(activated.Set, exitRequested.Set);
            Assert(SingleInstanceCoordinator.TrySignalExitForUpdateForTesting(
                       instanceId) &&
                   exitRequested.Wait(TimeSpan.FromSeconds(2)) &&
                   !activated.IsSet,
                "The installer update-exit signal did not reach the running Toolkit instance independently of activation.");
        }
    }

    private static void VerifyPowerSettingsWindowManualInput()
    {
        var window = new PowerSettingsWindow(
            key => key,
            () => ItsMode.Intelligent,
            false,
            new System.Windows.Media.FontFamily("Segoe UI"),
            14,
            embeddedMode: true);
        try
        {
            var editor = GetPrivateField<object>(window, "_gpuPowerBoost");
            var editorType = editor.GetType();
            var setValue = editorType.GetMethod("SetValue")!;
            var tryGetValue = editorType.GetMethod("TryGetValue")!;
            var slider = (Slider)editorType.GetProperty("Slider")!.GetValue(editor)!;

            setValue.Invoke(editor, [0]);
            object?[] zeroArguments = [null];
            Assert((bool)tryGetValue.Invoke(editor, zeroArguments)! &&
                   zeroArguments[0] is 0 &&
                   slider.Minimum == 0 &&
                   slider.Maximum == 15,
                "The legacy GPU Power Boost editor does not accept zero with its original slider range.");

            var textBox = (TextBox)editorType
                .GetProperty("TextBox")!
                .GetValue(editor)!;
            textBox.Text = "-1";
            object?[] negativeArguments = [null];
            Assert(!(bool)tryGetValue.Invoke(editor, negativeArguments)!,
                "The legacy GPU Power Boost editor accepts a negative value.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
    }

    private static void VerifyAdvancedFanCurve()
    {
        var points = AdvancedFanCurve.CreateDefaultPoints();
        Assert(points.Count == 12 &&
               points[0] is
               {
                   Fan1Rpm: 1500,
                   Fan2Rpm: 1500,
                   CpuRampUpTemperatureC: 46,
                   CpuRampDownTemperatureC: null,
                   GpuRampUpTemperatureC: 36,
                   GpuRampDownTemperatureC: null,
                   RampUpRpmPerSecond: 50,
                   RampDownRpmPerSecond: 20
               } &&
               points[5].CpuRampDownTemperatureC == 67 &&
               points[5].GpuRampDownTemperatureC == 57 &&
               points[5].RampUpRpmPerSecond == 100 &&
               points[5].RampDownRpmPerSecond == 50 &&
               points[^1] is
               {
                   Fan1Rpm: 5000,
                   Fan2Rpm: 5000,
                   CpuRampUpTemperatureC: null,
                   CpuRampDownTemperatureC: 88,
                   GpuRampUpTemperatureC: null,
                   GpuRampDownTemperatureC: 78,
                   RampUpRpmPerSecond: 0,
                   RampDownRpmPerSecond: 100
               },
            "Advanced-curve defaults do not match the requested RPM and threshold table.");
        Assert(AdvancedFanCurve.TryValidate(points, out _),
            "The built-in advanced curve is invalid.");

        var oldDefaults = new AdvancedFanCurveSettings
        {
            TemperatureSmoothing = 5,
            Points = points.Select(AdvancedFanCurve.Clone).ToList()
        };
        for (var index = 0; index < oldDefaults.Points.Count; index++)
        {
            oldDefaults.Points[index].RampUpRpmPerSecond = index < 4 ? 20 : 50;
            oldDefaults.Points[index].RampDownRpmPerSecond = index < 4 ? 10 : 20;
        }
        var migratedDefaults = CurveProfileStore
            .MigrateOldAdvancedFanCurveDefaults(oldDefaults)!;
        Assert(migratedDefaults.TemperatureSmoothing == 5 &&
               migratedDefaults.Points[0].RampUpRpmPerSecond == 50 &&
               migratedDefaults.Points[0].RampDownRpmPerSecond == 20 &&
               migratedDefaults.Points[5].RampUpRpmPerSecond == 100 &&
               migratedDefaults.Points[5].RampDownRpmPerSecond == 50 &&
               migratedDefaults.Points[^1].RampUpRpmPerSecond == 0 &&
               migratedDefaults.Points[^1].RampDownRpmPerSecond == 100,
            "Old built-in advanced-curve rates were not migrated to the new defaults.");

        var intermediateDefaults = new AdvancedFanCurveSettings
        {
            Points = points.Select(AdvancedFanCurve.Clone).ToList()
        };
        for (var index = 0; index < intermediateDefaults.Points.Count; index++)
        {
            intermediateDefaults.Points[index].RampUpRpmPerSecond = index < 4 ? 100 : 0;
            intermediateDefaults.Points[index].RampDownRpmPerSecond = index < 4 ? 20 : 100;
        }
        var migratedIntermediate = CurveProfileStore
            .MigrateOldAdvancedFanCurveDefaults(intermediateDefaults)!;
        Assert(migratedIntermediate.Points[0].RampUpRpmPerSecond == 50 &&
               migratedIntermediate.Points[5].RampUpRpmPerSecond == 100 &&
               migratedIntermediate.Points[5].RampDownRpmPerSecond == 50,
            "Intermediate advanced-curve defaults were not migrated.");
        var released020Defaults = new AdvancedFanCurveSettings
        {
            Points = points.Select(AdvancedFanCurve.Clone).ToList()
        };
        released020Defaults.Points[0].Fan1Rpm = 0;
        released020Defaults.Points[0].Fan2Rpm = 0;
        Assert(ReferenceEquals(
                   CurveProfileStore.MigrateOldAdvancedFanCurveDefaults(
                       released020Defaults),
                   released020Defaults),
            "Changing the new default unexpectedly migrated an existing v0.2.0 curve.");
        oldDefaults.Points[2].Fan1Rpm = 2100;
        Assert(ReferenceEquals(
                   CurveProfileStore.MigrateOldAdvancedFanCurveDefaults(oldDefaults),
                   oldDefaults),
            "A customized advanced curve was incorrectly replaced during default migration.");

        var raised = AdvancedFanCurve.Evaluate(points, 0, 50, 20);
        Assert(raised.Index == 1 &&
               raised.Target == new FanTargets(1800, 1900),
            "Advanced-curve ramp-up hysteresis selected the wrong point.");
        var lowered = AdvancedFanCurve.Evaluate(points, raised.Index, 40, 20);
        Assert(lowered.Index == 0 &&
               lowered.Target == new FanTargets(1500, 1500),
            "Advanced-curve ramp-down hysteresis selected the wrong point.");
        var maximum = AdvancedFanCurve.Evaluate(points, 0, 100, 100);
        Assert(maximum.Index == points.Count - 1 &&
               maximum.Target == new FanTargets(5000, 5000),
            "Advanced curve cannot reach its highest point.");

        var invalid = points.Select(AdvancedFanCurve.Clone).ToList();
        invalid[1].Fan1Rpm = 1850;
        Assert(!AdvancedFanCurve.TryValidate(invalid, out _),
            "Advanced curve accepts fan speeds that are not multiples of 100 RPM.");

        var equalValues = points.Select(AdvancedFanCurve.Clone).ToList();
        equalValues[2].Fan1Rpm = equalValues[1].Fan1Rpm;
        equalValues[2].CpuRampUpTemperatureC = equalValues[1].CpuRampUpTemperatureC;
        equalValues[2].CpuRampDownTemperatureC = equalValues[1].CpuRampDownTemperatureC;
        equalValues[2].GpuRampUpTemperatureC = equalValues[1].GpuRampUpTemperatureC;
        equalValues[2].GpuRampDownTemperatureC = equalValues[1].GpuRampDownTemperatureC;
        Assert(AdvancedFanCurve.TryValidate(equalValues, out _),
            "Advanced curve rejects equal values in adjacent columns.");

        var outOfRange = new AdvancedFanCurveSettings
        {
            Points = points.Select(AdvancedFanCurve.Clone).ToList()
        };
        outOfRange.Points[0].Fan1Rpm = 0;
        outOfRange.Points[0].Fan2Rpm = 0;
        outOfRange.Points[^1].Fan1Rpm = 6000;
        outOfRange.Points[^1].Fan2Rpm = 6000;
        var bounded = AdvancedFanCurve.Normalize(
            outOfRange,
            new FanRpmLimits
            {
                Fan1MinimumRpm = 1600,
                Fan1MaximumRpm = 4500,
                Fan2MinimumRpm = 1700,
                Fan2MaximumRpm = 4600
            });
        Assert(bounded.Points[0].Fan1Rpm == 1600 &&
               bounded.Points[0].Fan2Rpm == 1700 &&
               bounded.Points[^1].Fan1Rpm == 4500 &&
               bounded.Points[^1].Fan2Rpm == 4600,
            "Advanced-curve endpoints were not clamped to the configured fan RPM limits.");

        var descending = points.Select(AdvancedFanCurve.Clone).ToList();
        descending[2].Fan1Rpm = 1700;
        Assert(!AdvancedFanCurve.TryValidate(descending, out var descendingError) &&
               descendingError.Contains("must not decrease", StringComparison.Ordinal),
            "Advanced curve accepts a value that decreases from left to right.");

        var limiter = new FanRateLimiter();
        var started = limiter.Apply(
            new FanTargets(1500, 1500),
            100,
            100,
            DateTimeOffset.UnixEpoch,
            limitChanges: true);
        var halfway = limiter.Apply(
            new FanTargets(2500, 2500),
            100,
            100,
            DateTimeOffset.UnixEpoch.AddSeconds(0.5),
            limitChanges: true);
        var firstStep = limiter.Apply(
            new FanTargets(2500, 2500),
            100,
            100,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            limitChanges: true);
        Assert(started == new FanTargets(1500, 1500) &&
               halfway == new FanTargets(1500, 1500) &&
               firstStep == new FanTargets(1600, 1600) &&
               firstStep.Fan1Rpm % 100 == 0,
            "Fan rate limiting does not accumulate sub-100 RPM progress or round hardware targets to hundreds.");
        var zeroTransition = limiter.Apply(
            new FanTargets(0, 0),
            10,
            10,
            DateTimeOffset.UnixEpoch.AddSeconds(1.5),
            limitChanges: true);
        Assert(zeroTransition == new FanTargets(0, 0),
            "The special zero-RPM operation was incorrectly treated as an intermediate physical RPM.");
    }

    private static void VerifyGpuModeRestartState()
    {
        const string originalBoot = "boot-a";
        const string restartedBoot = "boot-b";
        var settings = new AppSettings();

        GpuModeRestartState.MarkPending(
            settings,
            GpuWorkingMode.Discrete,
            true,
            GpuWorkingMode.HybridAuto,
            originalBoot);
        Assert(
            settings.PendingGpuMode == nameof(GpuWorkingMode.HybridAuto) &&
            settings.PendingGpuModeSource == nameof(GpuWorkingMode.Discrete) &&
            settings.PendingGpuModeSourceUsesDirectGraphicsConfiguration == true &&
            settings.PendingGpuModeBootSessionId == originalBoot,
            "A restart-required GPU change must persist its source, target, route, and boot session.");
        Assert(
            GpuModeRestartState.TryGetTransition(
                settings,
                out var transition) &&
            transition.Source == GpuWorkingMode.Discrete &&
            transition.Target == GpuWorkingMode.HybridAuto &&
            transition.SourceUsesDirectGraphicsConfiguration,
            "The persisted GPU transition cannot be reconstructed.");
        Assert(
            GpuModeRestartState.TryGetCurrentBootTransition(
                settings,
                originalBoot,
                out var sameBootTransition) &&
            sameBootTransition.Target == GpuWorkingMode.HybridAuto,
            "Selecting the same target in the same boot must preserve the pending transition.");
        Assert(
            GpuModeRestartState.TryGetCurrentBootTarget(
                settings,
                originalBoot,
                out var sameBootTarget) &&
            sameBootTarget == GpuWorkingMode.HybridAuto,
            "A repeated target selection must be recognized even for older pending settings.");
        Assert(
            !GpuModeRestartState.TryGetCurrentBootTransition(
                settings,
                restartedBoot,
                out _),
            "A transition from an earlier boot must not be treated as a same-session request.");
        Assert(
            !GpuModeRestartState.ShouldClearAfterReadback(
                settings.PendingGpuMode,
                settings.PendingGpuModeBootSessionId,
                originalBoot,
                GpuWorkingMode.HybridAuto),
            "Firmware readback during the same boot must not clear the restart state.");
        Assert(
            GpuModeRestartState.ShouldClearAfterReadback(
                settings.PendingGpuMode,
                settings.PendingGpuModeBootSessionId,
                restartedBoot,
                GpuWorkingMode.HybridAuto),
            "A confirmed target after a Windows restart must clear the restart state.");
        Assert(
            !GpuModeRestartState.ShouldClearAfterReadback(
                settings.PendingGpuMode,
                settings.PendingGpuModeBootSessionId,
                restartedBoot,
                GpuWorkingMode.Hybrid),
            "A restart must not clear an unconfirmed GPU target.");
        Assert(
            GpuModeRestartState.RequiresRestart(
                GpuWorkingMode.Discrete,
                true,
                GpuWorkingMode.HybridAuto,
                false),
            "Changing a pending direct mode to a hybrid target must still require a restart.");
        Assert(
            !GpuModeRestartState.RequiresRestart(
                GpuWorkingMode.Discrete,
                true,
                GpuWorkingMode.Discrete,
                true),
            "Selecting the effective source mode must cancel the pending restart.");
        Assert(
            !GpuModeRestartState.RequiresRestart(
                GpuWorkingMode.Hybrid,
                false,
                GpuWorkingMode.HybridAuto,
                false),
            "Changing between live switchable-graphics modes must not require a restart.");
        Assert(
            GpuModeRestartState.HasRestartedSince(
                string.Empty,
                restartedBoot),
            "Pending settings from older configuration files must be checked once.");
        Assert(
            GpuModeRestartState.TryParsePendingMode(
                nameof(GpuWorkingMode.IntegratedDirect),
                out var parsed) &&
            parsed == GpuWorkingMode.IntegratedDirect,
            "All supported GPU modes must survive application restarts.");

        GpuModeRestartState.Clear(settings);
        Assert(
            string.IsNullOrEmpty(settings.PendingGpuMode) &&
            string.IsNullOrEmpty(settings.PendingGpuModeSource) &&
            settings.PendingGpuModeSourceUsesDirectGraphicsConfiguration is null &&
            string.IsNullOrEmpty(settings.PendingGpuModeBootSessionId),
            "Clearing the restart state must clear every persisted transition field.");
    }

    private static void VerifyLenovoDependencyDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ThinkBookToolkit-dependency-test-{Guid.NewGuid():N}");
        var customFile = Path.Combine(
            root,
            "LenovoPcManager",
            "WrapPlugin.dll");
        var fallbackFile = Path.Combine(root, "fallback", "WrapPlugin.dll");
        var customAddinRoot = Path.Combine(
            root,
            "VantageAddins",
            "TestAddin");
        var fallbackAddinRoot = Path.Combine(root, "fallback-addin");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(customFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fallbackFile)!);
            Directory.CreateDirectory(Path.Combine(customAddinRoot, "1.0.0"));
            Directory.CreateDirectory(Path.Combine(fallbackAddinRoot, "2.0.0"));
            File.WriteAllBytes(customFile, [1]);
            File.WriteAllBytes(fallbackFile, [2]);
            File.WriteAllBytes(
                Path.Combine(customAddinRoot, "1.0.0", "Test.dll"),
                [1]);
            File.WriteAllBytes(
                Path.Combine(fallbackAddinRoot, "2.0.0", "Test.dll"),
                [2]);

            Assert(LenovoDependencyDirectory.FindExistingFile(
                       root,
                       Path.Combine("LenovoPcManager", "WrapPlugin.dll"),
                       fallbackFile) == customFile,
                "The custom Lenovo DLL directory does not take priority over the application fallback.");
            Assert(LenovoVantageAddinLocator.FindLatestFileInRoots(
                       [customAddinRoot, fallbackAddinRoot],
                       "Test.dll") == Path.Combine(
                           customAddinRoot,
                           "1.0.0",
                           "Test.dll"),
                "The custom Lenovo Vantage add-in directory does not take priority.");

            LenovoDependencyDirectory.Configure(false, root);
            Assert(LenovoDependencyDirectory.GetEnabledRoot() is null,
                "The custom Lenovo DLL directory is enabled by default.");
            LenovoDependencyDirectory.Configure(true, root);
            Assert(LenovoDependencyDirectory.GetEnabledRoot() == root,
                "An enabled valid custom Lenovo DLL directory was not accepted.");
        }
        finally
        {
            LenovoDependencyDirectory.Configure(false, string.Empty);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void VerifySwitchAndCombo(DependencyObject root)
    {
        var checkBox = Descendants(root).OfType<CheckBox>().FirstOrDefault()
            ?? throw new InvalidOperationException("No boolean switch was rendered.");
        checkBox.ApplyTemplate();
        Assert(checkBox.Template.FindName("SwitchTrack", checkBox) is Border track &&
               track.Width >= 46 && track.Height >= 24 && track.CornerRadius.TopLeft >= 12,
            "Boolean settings do not use the rounded switch template.");

        var combo = Descendants(root).OfType<ComboBox>().FirstOrDefault()
            ?? throw new InvalidOperationException("No selector was rendered.");
        combo.ApplyTemplate();
        Assert(combo.Template.FindName("ComboChrome", combo) is Border,
            "Selector does not use Toolkit combo chrome.");
        var toggle = combo.Template.FindName("DropDownToggle", combo) as ToggleButton;
        Assert(toggle is not null && double.IsNaN(toggle.Width) &&
               toggle.HorizontalAlignment == HorizontalAlignment.Stretch,
            "The full selector surface is not clickable.");
    }

    private static FeatureAvailabilityReport CreateReport(Func<string, bool> available) =>
        new(typeof(FeatureIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new FeatureAvailability(
                id,
                Category(id),
                id,
                available(id),
                available(id) ? "smoke test available" : "smoke test unavailable")));

    private static void VerifyPerformancePageWithoutFanControl()
    {
        using var runtime = new ToolkitRuntimeService(new AppSettings
        {
            Language = "zh-CN",
            Theme = "dark"
        });
        runtime.SetReportForTesting(CreateReport(_ => false));
        var page = new ToolkitPerformancePage(runtime);
        try
        {
            var reload = typeof(ToolkitPerformancePage).GetMethod(
                "ReloadFanDrafts",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(
                    nameof(ToolkitPerformancePage),
                    "ReloadFanDrafts");
            reload.Invoke(page, null);
            Assert(!ContainsText(page, "风扇控制"),
                "The unavailable fan-control editor was unexpectedly created.");
        }
        finally
        {
            page.Dispose();
        }
    }

    private static void VerifyUserFacingExceptionText()
    {
        var display = ThinkBookToolkit.Program.FormatExceptionForDisplay(
            new TargetInvocationException(
                new KeyNotFoundException(
                    "PowerSavingNormalFan1Rpm was not present in the dictionary.")));
        Assert(display.Contains("KeyNotFoundException", StringComparison.Ordinal) &&
               display.Contains("PowerSavingNormalFan1Rpm", StringComparison.Ordinal) &&
               display.Contains("log", StringComparison.OrdinalIgnoreCase),
            "The user-facing exception summary omits useful diagnostic information.");
        Assert(!display.Contains("ToolkitPerformancePage.cs", StringComparison.Ordinal) &&
               !display.Contains(@"C:\Users\", StringComparison.OrdinalIgnoreCase) &&
               !display.Contains("\r\n   at ", StringComparison.Ordinal),
            "The user-facing exception summary exposes a source path or stack trace.");
    }

    private static void VerifyPowerDeviceProfiles()
    {
        var g5 = PowerSettingsController.ResolveProfile(
            "ThinkBook 16p G5 IRX",
            ["NVIDIA GeForce RTX 4060 Laptop GPU"]);
        var g5Defaults = PowerSettingsController.GetDefaultState(
            ItsMode.Performance,
            g5);
        Assert(g5.Writable && g5.SupportsDefaults &&
               g5.CpuTemperatureOffset == 0 && g5.GpuTgpOffset == 55 &&
               g5.Rules[PowerSetting.GpuPowerBoost] == new PowerSettingRule(0, 25) &&
               g5.Rules[PowerSetting.Atpp] == new PowerSettingRule(20, 95, 1) &&
               g5Defaults == new PowerSettingsState(125, 157, 97, 56, 10, 105, 87, 0, 75)
               { AvailableSettings = PowerSettingAvailability.All },
            "ThinkBook 16p G5 IRX power profile is incorrect.");
        var g6 = PowerSettingsController.ResolveProfile(
            "ThinkBook 16p G6 IAX");
        Assert(g6.Rules[PowerSetting.GpuPowerBoost] ==
               new PowerSettingRule(0, 15, 0),
            "ThinkBook 16p G6 IAX GPU Power Boost must use a 0–15 slider while allowing any non-negative manual value.");
        var g5Required =
            PowerSettingsController.RequiredSettingsForFullAvailability(g5);
        var g5WithoutAtpp =
            PowerSettingAvailability.All & ~PowerSettingAvailability.Atpp;
        Assert((g5Required & PowerSettingAvailability.Atpp) == 0 &&
               (g5WithoutAtpp & g5Required) == g5Required,
            "G5 IRX incorrectly requires ATPP for full power-setting availability.");

        var g5With4050 = PowerSettingsController.ResolveProfile(
            "ThinkBook 16p G5 IRX",
            ["NVIDIA GeForce RTX 4050 Laptop GPU"]);
        Assert(PowerSettingsController.GetDefaultState(
                   ItsMode.Performance,
                   g5With4050) is
               { GpuConfigurableTgp: 85, Atpp: 80 },
            "The RTX 4050 G5 IRX performance defaults are incorrect.");

        var gen6Plus = PowerSettingsController.ResolveProfile(
            "ThinkBook 14 G6+ IMH");
        var gen6Count = Enum.GetValues<PowerSetting>()
            .Count(gen6Plus.IsExpected);
        Assert(gen6Plus.Writable && !gen6Plus.SupportsDefaults &&
               gen6Count == 6 &&
               gen6Plus.GpuTgpOffset == 60 &&
               gen6Plus.Rules[PowerSetting.CpuPl1] == new PowerSettingRule(10, 100, 1) &&
               gen6Plus.Rules[PowerSetting.GpuConfigurableTgp] == new PowerSettingRule(60, 65),
            "ThinkBook 14 G6+ IMH power profile is incorrect.");

        var readOnly = PowerSettingsController.ResolveProfile("Unknown model");
        Assert(!readOnly.Writable && readOnly.CpuTemperatureOffset == 0 &&
               readOnly.GpuTgpOffset == 0,
            "Unknown devices must expose raw read-only power values.");
    }

    private static void VerifyPerformanceFanLinkSettings()
    {
        var value = new PerformanceFanLinkSettings
        {
            SwitchFanStrategyWithPerformanceMode = true,
            FanControlTargetMode = ItsMode.Performance,
            FanStrategiesByMode = new Dictionary<string, FanStrategySelection>
            {
                [ItsMode.Intelligent.ToString()] = new()
                {
                    Mode = FanControlMode.FanCurve,
                    ProfileIndex = 99
                }
            },
            NoSwitchModes = new Dictionary<string, bool>
            {
                [ItsMode.PowerSaving.ToString()] = true,
                [ItsMode.Performance.ToString()] = false
            }
        };
        var normalized = PerformanceFanLinkDefaults.Normalize(value);
        Assert(normalized.SwitchFanStrategyWithPerformanceMode &&
               normalized.FanStrategiesByMode.Count == 4 &&
               PerformanceFanLinkDefaults.SelectionFor(
                   normalized,
                   ItsMode.Intelligent) is
               {
                   Mode: FanControlMode.FanCurve,
                   ProfileIndex: 4
               } &&
               PerformanceFanLinkDefaults.SelectionFor(
                   normalized,
                   ItsMode.Geek).Mode ==
               FanControlMode.FirmwareAutomatic &&
               PerformanceFanLinkDefaults.IsNoSwitchMode(
                   normalized,
                   ItsMode.PowerSaving) &&
               PerformanceFanLinkDefaults.IsNoSwitchMode(
                   normalized,
                   ItsMode.Performance),
            "Performance/fan linkage settings were not normalized safely.");

        var releaseSemantics = new FanBackendControlSemantics(
            FanTargetZeroBehavior.ReleaseFanToFirmwareControl,
            FanAutomaticControlRestoreMechanism.WriteZeroToBothTargets,
            "test restore",
            new(
                FanFullSpeedControlMechanism.DedicatedBackendOperation,
                "test full speed on",
                "test full speed off"));
        var fixedAuto = ToolkitRuntimeSnapshot.Empty with
        {
            FanControlRunning = true,
            FanStrategy = ControlStrategy.FixedRpm,
            FanTarget = new FanTargets(0, 0)
        };
        Assert(!ToolkitRuntimeService.IsUsingFanControl(
                   fixedAuto,
                   releaseSemantics) &&
               ToolkitRuntimeService.IsUsingFanControl(
                   fixedAuto with
                   {
                       FanTarget = new FanTargets(0, 100)
                   },
                   releaseSemantics) &&
               ToolkitRuntimeService.IsUsingFanControl(
                   fixedAuto with
                   {
                       FanStrategy = ControlStrategy.FanCurve
                   },
                   releaseSemantics) &&
               ToolkitRuntimeService.IsUsingFanControl(
                   fixedAuto with
                   {
                       FanControlRunning = false,
                       FullSpeed = true
                   },
                   releaseSemantics),
            "Effective fan-control detection does not handle firmware-auto fixed targets or full speed correctly.");
    }

    private static void VerifyApplicationIconTransparency()
    {
        var uri = new Uri(
            "pack://application:,,,/ThinkBookToolkit;component/Assets/app-icon-tb.png",
            UriKind.Absolute);
        var resource = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException(
                "The application icon resource could not be opened.");
        using var stream = resource.Stream;
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(
            decoder.Frames[0],
            PixelFormats.Bgra32,
            null,
            0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        int AlphaAt(int x, int y) => pixels[y * stride + x * 4 + 3];
        Assert(AlphaAt(0, 0) == 0 &&
               AlphaAt(bitmap.PixelWidth - 1, 0) == 0 &&
               AlphaAt(0, bitmap.PixelHeight - 1) == 0 &&
               AlphaAt(bitmap.PixelWidth - 1, bitmap.PixelHeight - 1) == 0 &&
               AlphaAt(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2) == 255,
            "The application icon corners are not transparent or the logo body lost opacity.");
    }

    private static void VerifyOverviewLayoutSettings()
    {
        var layout = new OverviewLayoutSettings();
        foreach (var item in layout.Cards[OverviewCardIds.Cpu].Items.Keys.ToArray())
            layout.Cards[OverviewCardIds.Cpu].Items[item] = false;
        var normalized = OverviewLayoutDefaults.Normalize(layout);
        Assert(!normalized.Cards[OverviewCardIds.Cpu].Enabled &&
               normalized.Cards[OverviewCardIds.Warranty].Enabled &&
               normalized.Cards[OverviewCardIds.Warranty].Items.Count == 5 &&
               OverviewLayoutDefaults.CompactCardDefinitions.Count == 6 &&
               OverviewLayoutDefaults.CompactCardDefinitions[
                   OverviewCardIds.Cpu].SequenceEqual(
                   ["temperature", "power"]) &&
               OverviewLayoutDefaults.CompactCardDefinitions[
                   OverviewCardIds.MemoryStorage].SequenceEqual(
                   ["utilization", "average-temperature"]) &&
               OverviewLayoutDefaults.CompactCardDefinitions[
                   OverviewCardIds.Fans].SequenceEqual(
                   ["fan1-speed", "fan2-speed"]) &&
               OverviewLayoutDefaults.CompactCardDefinitions[
                   OverviewCardIds.Warranty].SequenceEqual(
                   ["status", "remaining-days"]),
            "Overview layout normalization or compact card definitions are incorrect.");
    }

    private static void VerifyAdaptiveUniformPanelCollapsedItems()
    {
        var panel = new AdaptiveUniformPanel
        {
            MinimumItemWidth = 180,
            Spacing = 8
        };
        var items = Enumerable.Range(0, 9)
            .Select(_ => new Border { Height = 80 })
            .ToArray();
        foreach (var item in items)
            panel.Children.Add(item);
        items[3].Visibility = Visibility.Collapsed;
        items[6].Visibility = Visibility.Collapsed;
        items[8].Visibility = Visibility.Collapsed;

        ArrangePanel(panel, 932);
        var third = items[2].TranslatePoint(new Point(), panel);
        var fourthVisible = items[4].TranslatePoint(new Point(), panel);
        var first = items[0].TranslatePoint(new Point(), panel);
        var sixthVisible = items[7].TranslatePoint(new Point(), panel);
        Assert(SameRow(items[2], items[4]) &&
               Math.Abs(fourthVisible.X - third.X - 188) < 1 &&
               !SameRow(items[0], items[7]) &&
               Math.Abs(sixthVisible.X - first.X) < 1,
            "Collapsed adaptive-panel items still reserve grid positions.");
    }

    private static void VerifySystemShutdownPreparation()
    {
        using var runtime = new ToolkitRuntimeService(new AppSettings
        {
            Language = "zh-CN",
            Theme = "dark"
        });
        runtime.SetSnapshotForTesting(ToolkitRuntimeSnapshot.Empty with
        {
            FanControlRunning = true,
            FullSpeed = true,
            FanTarget = new FanTargets(3200, 3300)
        });

        runtime.PrepareForSystemShutdown(ReasonSessionEnding.Shutdown);
        runtime.PrepareForSystemShutdown(ReasonSessionEnding.Logoff);
        Assert(runtime.IsSystemSessionEnding &&
               runtime.ExitRequested &&
               !runtime.Snapshot.FanControlRunning &&
               !runtime.Snapshot.FullSpeed &&
               runtime.Snapshot.FanTarget is null,
            "System-session shutdown preparation is not idempotent or does not stop fan control state.");
    }

    private static string Category(string id)
    {
        if (id is FeatureIds.FanControl or FeatureIds.SleepFanControl) return "散热";
        if (id.StartsWith("performance.", StringComparison.Ordinal)) return "性能";
        if (id.StartsWith("battery.", StringComparison.Ordinal)) return "电池与电源";
        if (id.StartsWith("display.", StringComparison.Ordinal)) return "显示";
        if (id.StartsWith("sound.", StringComparison.Ordinal)) return "声音";
        if (id.StartsWith("input.", StringComparison.Ordinal)) return "输入设备";
        if (id.StartsWith("device.", StringComparison.Ordinal)) return "设备";
        if (id.StartsWith("advanced.", StringComparison.Ordinal)) return "高级工具";
        return "监控";
    }

    private static bool ContainsText(DependencyObject root, string text) =>
        Descendants(root).Any(item => item switch
        {
            TextBlock block => block.Text.Contains(text, StringComparison.Ordinal),
            ContentControl content => content.Content?.ToString()?.Contains(text, StringComparison.Ordinal) == true,
            _ => false
        });

    private static bool ContainsButtonText(DependencyObject root, string text) =>
        Descendants(root).OfType<Button>().Any(button =>
            button.Content?.ToString()?.Contains(text, StringComparison.Ordinal) == true);

    private static void ArrangePanel(AdaptiveUniformPanel panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        panel.UpdateLayout();
    }

    private static bool SameRow(UIElement first, UIElement second) =>
        Math.Abs(first.TranslatePoint(new Point(), null).Y -
                 second.TranslatePoint(new Point(), null).Y) < 1;

    private static bool IsAdvancedCurveHorizontalScroller(ScrollViewer scroll) =>
        Equals(scroll.Tag, "AdvancedFanCurveHorizontalScroll") &&
        scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto &&
        scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled &&
        scroll.PanningMode == PanningMode.HorizontalOnly;

    private static IReadOnlyList<string> Labels(ComboBox combo) =>
        combo.Items.OfType<ComboBoxItem>()
            .Select(item => item.Content?.ToString() ?? string.Empty)
            .ToArray();

    private static T GetPrivateField<T>(object owner, string name)
        where T : class =>
        (T)(owner.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(owner)
            ?? throw new MissingFieldException(owner.GetType().Name, name));

    private static string PropertyText(object owner, string name) =>
        owner.GetType().GetProperty(name)?.GetValue(owner)?.ToString() ??
        throw new MissingMemberException(owner.GetType().Name, name);

    private static T PropertyValue<T>(object owner, string name) =>
        owner.GetType().GetProperty(name)?.GetValue(owner) is T value
            ? value
            : throw new MissingMemberException(owner.GetType().Name, name);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ThrowingFanBackend : IFanBackend
    {
        public const string FailureMessage =
            "Backend compatibility detail.";

        public ThrowingFanBackend() =>
            throw new NotSupportedException(FailureMessage);

        public string Name => "Throwing backend";

        public Version ApiVersion => FanBackendContract.CurrentVersion;

        public string Transport => "Test";

        public FanBackendStartupNotice? StartupNotice => null;

        public bool SupportsDisableControlOnSleep => false;

        public TimeSpan MinimumReadInterval => TimeSpan.FromSeconds(0.5);

        public TimeSpan MinimumWriteInterval => TimeSpan.FromSeconds(0.5);

        public FanBackendControlSemantics ControlSemantics { get; } = new(
            FanTargetZeroBehavior.ReleaseFanToFirmwareControl,
            FanAutomaticControlRestoreMechanism.WriteZeroToBothTargets,
            "test restore",
            new(
                FanFullSpeedControlMechanism.FeatureToggle,
                "test full speed on",
                "test full speed off"));

        public FanBackendSnapshot ReadSnapshot() =>
            throw new NotSupportedException();

        public void Apply(int fan1Rpm, int fan2Rpm) =>
            throw new NotSupportedException();

        public void RestoreAuto() =>
            throw new NotSupportedException();

        public void SetFullSpeed(bool enabled) =>
            throw new NotSupportedException();
    }
}
