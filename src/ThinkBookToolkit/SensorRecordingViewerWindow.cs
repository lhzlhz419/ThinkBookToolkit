using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal sealed class SensorRecordingViewerWindow : Window
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly ToolkitPalette _palette;
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _range = new() { MinWidth = 150 };
    private readonly StackPanel _charts = new();
    private readonly Dictionary<string, HashSet<string>> _hiddenSeriesByChart =
        new(StringComparer.Ordinal);
    private readonly TextBlock _empty = new();
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private string _selectedPath = string.Empty;
    private string _displayPath = string.Empty;
    private string _temporaryExtractedPath = string.Empty;
    private bool _initialRangeSelected;
    private bool _refreshingCharts;
    private string _cachedPath = string.Empty;
    private long _cachedReadOffset;
    private IReadOnlyList<string> _cachedMetricKeys = [];
    private List<SensorRecordingSample> _cachedFileSamples = [];
    private int _lastRenderedSourceCount = -1;
    private DateTimeOffset _lastRenderedTimestamp = DateTimeOffset.MinValue;
    private TimeSpan? _lastRenderedRange;
    private int _lastRenderedMaximum;

    private static readonly IReadOnlyList<ChartGroupDefinition> Definitions =
    [
        new("FPS", "FPS",
        [
            C("FPS", "FPS", S("fps", "FPS"), S("fps1Low", "1% Low")),
            C("延迟", "Latency", S("latencyMs", "ms"))
        ]),
        new("CPU", "CPU",
        [
            U("利用率", "Utilization", S("cpuUtilization", "%")),
            C("频率", "Frequency",
                S("cpuAverageMhz", "平均"), S("cpuPerformanceCoreAverageMhz", "性能核"),
                S("cpuEfficiencyCoreAverageMhz", "能效核"), S("cpuMaximumMhz", "最高")),
            C("温度", "Temperature", S("cpuTemperatureC", "°C")),
            C("功耗", "Power", S("cpuPowerW", "W"))
        ]),
        new("GPU 和显存", "GPU and VRAM",
        [
            U("利用率", "Utilization", S("gpuUtilization", "GPU"), S("vramUtilization", "VRAM")),
            C("频率", "Frequency", S("gpuCoreMhz", "GPU"), S("vramMhz", "VRAM")),
            C("温度", "Temperature", S("gpuTemperatureC", "GPU"),
                S("gpuHotSpotTemperatureC", "热点"), S("vramTemperatureC", "显存")),
            C("功耗", "Power", S("gpuPowerW", "W"))
        ]),
        new("内存、硬盘和电池", "RAM, storage and battery",
        [
            C("内存数值", "Memory values", S("ramUsedGb", "物理内存"), S("committedUsedGb", "已提交")),
            U("内存利用率", "Memory utilization", S("ramUtilization", "物理内存"), S("committedUtilization", "已提交")),
            C("内存和硬盘温度", "RAM and storage temperature",
                S("memorySlot1TemperatureC", "插槽1"), S("memorySlot2TemperatureC", "插槽2"),
                S("disk1TemperatureC", "硬盘1"), S("disk2TemperatureC", "硬盘2"),
                S("disk3TemperatureC", "硬盘3"), S("disk4TemperatureC", "硬盘4"),
                S("disk5TemperatureC", "硬盘5"), S("disk6TemperatureC", "硬盘6"),
                S("disk7TemperatureC", "硬盘7"), S("disk8TemperatureC", "硬盘8")),
            C("电池容量", "Battery capacity", S("batteryCapacityWh", "Wh")),
            B("电池功率", "Battery power", S("batteryPowerW", "W"))
        ]),
        new("风扇", "Fans",
        [
            C("风扇转速", "Fan speed", S("fan1Rpm", "风扇1"), S("fan2Rpm", "风扇2"))
        ])
    ];

    public SensorRecordingViewerWindow(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
        _palette = ToolkitPalette.For(runtime.IsDark);
        Title = runtime.L("传感器记录", "Sensor recording");
        Width = 1120;
        Height = 820;
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brush(_palette.Canvas);
        Foreground = Brush(_palette.Text);
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        Content = Build();
        _refreshTimer.Tick += (_, _) => RefreshIfCurrent();
        _runtime.SensorRecordingSampleWritten += OnSampleWritten;
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _runtime.SensorRecordingSampleWritten -= OnSampleWritten;
            Content = null;
            _charts.Children.Clear();
            _hiddenSeriesByChart.Clear();
            _selectedPath = string.Empty;
            _displayPath = string.Empty;
            SensorRecordingArchive.DeleteTemporary(
                _temporaryExtractedPath);
            _temporaryExtractedPath = string.Empty;
            _cachedPath = string.Empty;
            _cachedReadOffset = 0;
            _cachedFileSamples = [];
            _cachedMetricKeys = [];
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => MediaMemoryCleanup.CollectAndTrim(
                    "sensor history viewer closed")));
        };
        Loaded += (_, _) =>
        {
            ModernTheme.RefreshWindow(this, runtime.IsDark);
            var path = runtime.Settings.SensorRecordingEnabled
                ? runtime.CurrentSensorRecordingPath
                : runtime.Settings.LastSensorRecordingPath;
            LoadPath(path);
        };
    }

    private UIElement Build()
    {
        foreach (var option in new[]
                 {
                     (_runtime.L("过去 1 分钟", "Past 1 minute"), TimeSpan.FromMinutes(1)),
                     (_runtime.L("过去 5 分钟", "Past 5 minutes"), TimeSpan.FromMinutes(5)),
                     (_runtime.L("过去 10 分钟", "Past 10 minutes"), TimeSpan.FromMinutes(10)),
                     (_runtime.L("过去 30 分钟", "Past 30 minutes"), TimeSpan.FromMinutes(30)),
                     (_runtime.L("过去 1 小时", "Past 1 hour"), TimeSpan.FromHours(1))
                 })
        {
            _range.Items.Add(new ComboBoxItem
            {
                Content = option.Item1,
                Tag = (TimeSpan?)option.Item2
            });
        }
        _range.Items.Add(new ComboBoxItem
        {
            Content = _runtime.L("所有", "All"),
            Tag = (TimeSpan?)null
        });
        _range.SelectionChanged += (_, _) => RefreshCharts();

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_path);
        var browse = Button(_runtime.L("浏览", "Browse"));
        browse.Click += (_, _) => Browse();
        Grid.SetColumn(browse, 1);
        header.Children.Add(browse);
        root.Children.Add(header);
        _empty.Text = _runtime.L("没有可显示的记录。", "No recording data to display.");
        _empty.Foreground = Brush(_palette.Muted);
        _charts.Children.Add(_empty);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _charts
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        footer.Children.Add(new TextBlock
        {
            Text = _runtime.L("显示范围", "Range"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        footer.Children.Add(_range);
        var close = Button(_runtime.L("关闭", "Close"));
        close.Margin = new Thickness(12, 0, 0, 0);
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private void Browse()
    {
        Directory.CreateDirectory(CurveProfileStore.SensorRecordingDirectory);
        var dialog = new OpenFileDialog
        {
            InitialDirectory = CurveProfileStore.SensorRecordingDirectory,
            Filter =
                "Sensor recordings (*.jsonl.gz;*.jsonl)|*.jsonl.gz;*.jsonl|" +
                "Compressed sensor recordings (*.jsonl.gz)|*.jsonl.gz|" +
                "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            LoadPath(dialog.FileName);
    }

    private void LoadPath(string? path)
    {
        SensorRecordingArchive.DeleteTemporary(_temporaryExtractedPath);
        _temporaryExtractedPath = string.Empty;
        _displayPath = path?.Trim() ?? string.Empty;
        _selectedPath = _displayPath;
        if (SensorRecordingArchive.IsCompressed(_displayPath))
        {
            try
            {
                _temporaryExtractedPath =
                    SensorRecordingArchive.ExtractToTemporary(_displayPath);
                _selectedPath = _temporaryExtractedPath;
            }
            catch (Exception ex)
            {
                _selectedPath = string.Empty;
                ToolkitLog.Error(
                    "Sensor recording archive could not be extracted: " +
                    _displayPath,
                    ex);
            }
        }
        _hiddenSeriesByChart.Clear();
        ResetFileCache();
        _lastRenderedSourceCount = -1;
        _path.Text = string.IsNullOrWhiteSpace(_displayPath)
            ? _runtime.L("尚无记录文件", "No recording file yet")
            : _displayPath;
        _initialRangeSelected = false;
        RefreshCharts();
        SyncRefreshTimer();
    }

    private void OnSampleWritten(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSampleWritten(sender, args)));
            return;
        }
        SyncRefreshTimer();
    }

    private void RefreshIfCurrent()
    {
        if (IsCurrentFile())
            RefreshCharts();
        SyncRefreshTimer();
    }

    private bool IsCurrentFile() =>
        _runtime.Settings.SensorRecordingEnabled &&
        !string.IsNullOrWhiteSpace(_runtime.CurrentSensorRecordingPath) &&
        string.Equals(
            Path.GetFullPath(_selectedPath),
            Path.GetFullPath(_runtime.CurrentSensorRecordingPath),
            StringComparison.OrdinalIgnoreCase);

    private void SyncRefreshTimer()
    {
        if (IsCurrentFile())
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    private void RefreshCharts()
    {
        if (_refreshingCharts)
            return;
        _refreshingCharts = true;
        try
        {
            if (!_initialRangeSelected)
            {
                _initialRangeSelected = true;
                _range.SelectedItem = _range.Items.OfType<ComboBoxItem>()
                    .First(item => item.Tag is TimeSpan value &&
                                   value == TimeSpan.FromMinutes(5));
            }
            var range = _range.SelectedItem is ComboBoxItem { Tag: TimeSpan value }
                ? value
                : (TimeSpan?)null;
            var samples = ReadCachedSamples(range);
            var maximumPoints = _runtime.Settings.SensorRecording.MaximumPlotPoints;
            var lastTimestamp = samples.Count > 0
                ? samples[^1].Timestamp
                : DateTimeOffset.MinValue;
            if (_lastRenderedSourceCount == samples.Count &&
                _lastRenderedTimestamp == lastTimestamp &&
                _lastRenderedRange == range &&
                _lastRenderedMaximum == maximumPoints)
                return;
            _lastRenderedSourceCount = samples.Count;
            _lastRenderedTimestamp = lastTimestamp;
            _lastRenderedRange = range;
            _lastRenderedMaximum = maximumPoints;
            samples = AverageResample(samples, maximumPoints);
            _charts.Children.Clear();
            var any = false;
            foreach (var definition in Definitions)
            {
                var charts = definition.Charts
                    .Where(chart => chart.Series.Any(series => samples.Any(sample =>
                        sample.Values.TryGetValue(series.Key, out var reading) &&
                        reading.HasValue)))
                    .ToArray();
                if (charts.Length == 0)
                    continue;
                any = true;
                _charts.Children.Add(new TextBlock
                {
                    Text = _runtime.L(definition.Chinese, definition.English),
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(2, 12, 2, 8)
                });
                var panel = new AdaptiveUniformPanel
                {
                    MaximumColumns = 2,
                    MinimumItemWidth = 400,
                    Spacing = 8
                };
                foreach (var chart in charts)
                {
                    var chartKey = definition.English + "|" + chart.English;
                    if (!_hiddenSeriesByChart.TryGetValue(chartKey, out var hidden))
                    {
                        hidden = new HashSet<string>(StringComparer.Ordinal);
                        _hiddenSeriesByChart[chartKey] = hidden;
                    }
                    panel.Children.Add(new SensorHistoryChart(
                        _runtime.L(chart.Chinese, chart.English),
                        chart.Series,
                        samples,
                        _runtime.IsDark,
                        _runtime.IsChinese,
                        chart.Minimum,
                        chart.Maximum,
                        hidden));
                }
                _charts.Children.Add(panel);
            }
            if (!any)
                _charts.Children.Add(_empty);
        }
        finally
        {
            _refreshingCharts = false;
        }
    }

    internal static List<SensorRecordingSample> ReadSamples(string? path)
    {
        var result = new List<SensorRecordingSample>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return result;
        var readPath = path;
        string? temporaryPath = null;
        try
        {
            if (SensorRecordingArchive.IsCompressed(path))
            {
                temporaryPath = SensorRecordingArchive.ExtractToTemporary(path);
                readPath = temporaryPath;
            }
            using var stream = new FileStream(
                readPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            if (!SensorRecordingFormat.TryReadHeader(
                    reader.ReadLine() ?? string.Empty,
                    out var keys))
                return result;
            string? line;
            while ((line = reader.ReadLine()) is not null)
                SensorRecordingFormat.ReadBatch(line, keys, result);
        }
        catch (IOException)
        {
        }
        catch (InvalidDataException)
        {
        }
        finally
        {
            SensorRecordingArchive.DeleteTemporary(temporaryPath);
        }
        return result;
    }

    private List<SensorRecordingSample> ReadCachedSamples(TimeSpan? range)
    {
        RefreshFileCache();
        var buffered = IsCurrentFile()
            ? _runtime.CurrentBufferedSensorRecordingSamples
            : [];
        DateTimeOffset? latest = buffered.Count > 0
            ? buffered[^1].Timestamp
            : _cachedFileSamples.Count > 0
                ? _cachedFileSamples[^1].Timestamp
                : null;
        var cutoff = range.HasValue && latest.HasValue
            ? latest.Value - range.Value
            : DateTimeOffset.MinValue;
        var fileStart = range.HasValue
            ? LowerBound(_cachedFileSamples, cutoff)
            : 0;
        var bufferStart = range.HasValue
            ? LowerBound(buffered, cutoff)
            : 0;
        var result = new List<SensorRecordingSample>(
            _cachedFileSamples.Count - fileStart + buffered.Count - bufferStart);
        for (var index = fileStart; index < _cachedFileSamples.Count; index++)
            result.Add(_cachedFileSamples[index]);
        for (var index = bufferStart; index < buffered.Count; index++)
            result.Add(buffered[index]);
        return result;
    }

    private static int LowerBound(
        IReadOnlyList<SensorRecordingSample> samples,
        DateTimeOffset timestamp)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].Timestamp < timestamp)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private void ResetFileCache()
    {
        _cachedPath = _selectedPath;
        _cachedReadOffset = 0;
        _cachedMetricKeys = [];
        _cachedFileSamples.Clear();
    }

    private void RefreshFileCache()
    {
        if (string.IsNullOrWhiteSpace(_selectedPath) ||
            !File.Exists(_selectedPath))
            return;
        if (!string.Equals(
                _cachedPath,
                _selectedPath,
                StringComparison.OrdinalIgnoreCase))
            ResetFileCache();
        try
        {
            var length = new FileInfo(_selectedPath).Length;
            if (length < _cachedReadOffset)
                ResetFileCache();
            if (length == _cachedReadOffset)
                return;
            using var stream = new FileStream(
                _selectedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_cachedReadOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            if (_cachedReadOffset == 0)
            {
                if (!SensorRecordingFormat.TryReadHeader(
                        reader.ReadLine() ?? string.Empty,
                        out _cachedMetricKeys))
                    return;
            }
            string? line;
            while ((line = reader.ReadLine()) is not null)
                SensorRecordingFormat.ReadBatch(
                    line,
                    _cachedMetricKeys,
                    _cachedFileSamples);
            _cachedReadOffset = stream.Position;
            CompactCachedHistory();
        }
        catch (IOException)
        {
        }
    }

    private void CompactCachedHistory()
    {
        if (_cachedFileSamples.Count == 0)
            return;
        var cutoff = _cachedFileSamples[^1].Timestamp - TimeSpan.FromHours(1);
        var recentStart = LowerBound(_cachedFileSamples, cutoff);
        var olderLimit = Math.Max(
            300,
            _runtime.Settings.SensorRecording.MaximumPlotPoints * 2);
        if (recentStart <= olderLimit)
            return;
        var older = AverageResample(
            _cachedFileSamples.GetRange(0, recentStart),
            olderLimit);
        var recent = _cachedFileSamples.GetRange(
            recentStart,
            _cachedFileSamples.Count - recentStart);
        _cachedFileSamples.Clear();
        _cachedFileSamples.AddRange(older);
        _cachedFileSamples.AddRange(recent);
    }

    internal static List<SensorRecordingSample> Downsample(
        IReadOnlyList<SensorRecordingSample> samples,
        int maximum) => AverageResample(samples, maximum);

    internal static List<SensorRecordingSample> AverageResample(
        IReadOnlyList<SensorRecordingSample> samples,
        int maximum)
    {
        maximum = Math.Max(2, maximum);
        if (samples.Count <= maximum)
            return samples.ToList();
        var count = Math.Min(maximum, samples.Count);
        var start = samples[0].Timestamp;
        var end = samples[^1].Timestamp;
        var durationTicks = Math.Max(1, (end - start).Ticks);
        var keys = samples
            .SelectMany(sample => sample.Values.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sums = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            sums[key] = new double[count];
            counts[key] = new int[count];
        }
        foreach (var sample in samples)
        {
            var ratio = Math.Clamp(
                (sample.Timestamp - start).Ticks / (double)durationTicks,
                0,
                1);
            var bucket = Math.Clamp(
                (int)Math.Round(ratio * (count - 1)),
                0,
                count - 1);
            foreach (var pair in sample.Values)
            {
                if (!pair.Value.HasValue ||
                    !double.IsFinite(pair.Value.Value))
                    continue;
                sums[pair.Key][bucket] += pair.Value.Value;
                counts[pair.Key][bucket]++;
            }
        }
        var result = new List<SensorRecordingSample>(count);
        var rightIndex = 0;
        for (var bucket = 0; bucket < count; bucket++)
        {
            var timestamp = bucket == count - 1
                ? end
                : start.AddTicks((long)Math.Round(
                    durationTicks * bucket / (double)(count - 1)));
            while (rightIndex < samples.Count - 1 &&
                   samples[rightIndex].Timestamp < timestamp)
                rightIndex++;
            var values = new Dictionary<string, double?>(
                keys.Length,
                StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var itemCount = counts[key][bucket];
                if (itemCount > 0)
                {
                    values[key] = Math.Round(
                        sums[key][bucket] / itemCount,
                        2,
                        MidpointRounding.AwayFromZero);
                    continue;
                }
                // An empty time bucket is not a missing sensor reading.
                // Interpolate only between adjacent valid source readings;
                // never bridge an explicit null or extrapolate outside a run.
                values[key] = null;
                var right = samples[rightIndex];
                if (!right.Values.TryGetValue(key, out var rightValue) ||
                    !rightValue.HasValue || !double.IsFinite(rightValue.Value))
                    continue;
                if (right.Timestamp == timestamp)
                    values[key] = rightValue;
                else if (rightIndex > 0)
                {
                    var left = samples[rightIndex - 1];
                    if (left.Values.TryGetValue(key, out var leftValue) &&
                        leftValue.HasValue && double.IsFinite(leftValue.Value) &&
                        left.Timestamp < timestamp && timestamp < right.Timestamp)
                    {
                        var fraction = (timestamp - left.Timestamp).Ticks /
                            (double)(right.Timestamp - left.Timestamp).Ticks;
                        values[key] = Math.Round(leftValue.Value +
                            (rightValue.Value - leftValue.Value) * fraction, 2);
                    }
                }
            }
            result.Add(new SensorRecordingSample(timestamp, values));
        }
        return result;
    }

    private Button Button(string text) => new()
    {
        Content = text,
        MinWidth = 110,
        MinHeight = 38,
        Padding = new Thickness(14, 7, 14, 7),
        Background = Brush(_palette.SurfaceRaised),
        Foreground = Brush(_palette.Text),
        BorderBrush = Brush(_palette.Border),
        BorderThickness = new Thickness(1),
        Template = ModernTheme.RoundedButtonTemplate(10)
    };

    private static ChartDefinition C(
        string chinese,
        string english,
        params SeriesDefinition[] series) =>
        new(chinese, english, series, 0, null);
    private static ChartDefinition U(
        string chinese,
        string english,
        params SeriesDefinition[] series) =>
        new(chinese, english, series, 0, 100);
    private static ChartDefinition B(
        string chinese,
        string english,
        params SeriesDefinition[] series) =>
        new(chinese, english, series, null, null);
    private static SeriesDefinition S(string key, string label) => new(key, label);
    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));

    private sealed record ChartGroupDefinition(
        string Chinese,
        string English,
        IReadOnlyList<ChartDefinition> Charts);
    private sealed record ChartDefinition(
        string Chinese,
        string English,
        IReadOnlyList<SeriesDefinition> Series,
        double? Minimum,
        double? Maximum);
}

internal sealed record SeriesDefinition(string Key, string Label);

internal sealed class SensorHistoryChart : FrameworkElement
{
    private static readonly Color[] SeriesColors =
    [
        Color.FromRgb(124, 156, 255), Color.FromRgb(83, 214, 154),
        Color.FromRgb(245, 185, 76), Color.FromRgb(255, 123, 134),
        Color.FromRgb(182, 140, 255), Color.FromRgb(80, 205, 230),
        Color.FromRgb(255, 155, 80), Color.FromRgb(210, 225, 120),
        Color.FromRgb(235, 120, 205), Color.FromRgb(150, 210, 255)
    ];

    private readonly string _title;
    private readonly IReadOnlyList<SeriesDefinition> _series;
    private readonly IReadOnlyList<SensorRecordingSample> _samples;
    private readonly bool _dark;
    private readonly bool _isChinese;
    private readonly double? _minimumBound;
    private readonly double? _maximumBound;
    private readonly HashSet<string> _hiddenSeries;
    private readonly List<LegendHit> _legendHits = [];
    private readonly Dictionary<string, StreamGeometry> _geometryCache =
        new(StringComparer.Ordinal);
    private string _geometrySignature = string.Empty;
    private double _geometryWidth;
    private double _geometryHeight;
    private double _geometryMinimum;
    private double _geometryMaximum;
    private string _dataSignature = string.Empty;
    private (SeriesDefinition series, int index)[] _cachedLegendItems = [];
    private (SeriesDefinition series, int index)[] _cachedAvailable = [];
    private double _cachedAxisMinimum;
    private double _cachedAxisMaximum = 1;
    private Rect _plot;
    private Point? _hoverPoint;

    public SensorHistoryChart(
        string title,
        IReadOnlyList<SeriesDefinition> series,
        IReadOnlyList<SensorRecordingSample> samples,
        bool dark,
        bool isChinese,
        double? minimumBound,
        double? maximumBound,
        HashSet<string>? hiddenSeries = null)
    {
        _title = title;
        _series = series;
        _samples = samples;
        _dark = dark;
        _isChinese = isChinese;
        _minimumBound = minimumBound;
        _maximumBound = maximumBound;
        _hiddenSeries = hiddenSeries ?? new HashSet<string>(StringComparer.Ordinal);
        Height = 250;
        MinWidth = 360;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) =>
        {
            _hoverPoint = null;
            InvalidateVisual();
        };
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var background = new SolidColorBrush(_dark
            ? Color.FromArgb(220, 21, 31, 51)
            : Color.FromArgb(225, 255, 255, 255));
        var textColor = new SolidColorBrush(_dark
            ? Colors.White
            : Color.FromRgb(23, 32, 51));
        var muted = new SolidColorBrush(_dark
            ? Color.FromRgb(155, 170, 194)
            : Color.FromRgb(109, 122, 144));
        drawing.DrawRoundedRectangle(
            background,
            new Pen(new SolidColorBrush(_dark
                ? Color.FromRgb(42, 56, 82)
                : Color.FromRgb(226, 233, 242)), 1),
            new Rect(0, 0, ActualWidth, ActualHeight),
            12,
            12);
        DrawText(drawing, _title, 14, FontWeights.SemiBold, textColor, 14, 10);
        EnsureDataCache();
        var legendItems = _cachedLegendItems;
        if (legendItems.Length == 0 || _samples.Count == 0)
            return;
        var available = _cachedAvailable;
        var minimum = _cachedAxisMinimum;
        var maximum = _cachedAxisMaximum;
        _plot = new Rect(
            48,
            42,
            Math.Max(1, ActualWidth - 62),
            Math.Max(1, ActualHeight - 78));
        drawing.DrawLine(new Pen(muted, 1), _plot.BottomLeft, _plot.TopLeft);
        drawing.DrawLine(new Pen(muted, 1), _plot.BottomLeft, _plot.BottomRight);
        DrawText(drawing, maximum.ToString("0.##", CultureInfo.InvariantCulture), 10, FontWeights.Normal, muted, 4, _plot.Top - 6);
        DrawText(drawing, minimum.ToString("0.##", CultureInfo.InvariantCulture), 10, FontWeights.Normal, muted, 4, _plot.Bottom - 10);
        var start = _samples[0].Timestamp;
        var duration = Math.Max(1, (_samples[^1].Timestamp - start).TotalMilliseconds);
        EnsureGeometryCache(
            available,
            start,
            duration,
            minimum,
            maximum);
        foreach (var item in available)
        {
            var color = new SolidColorBrush(SeriesColors[item.index % SeriesColors.Length]);
            var pen = new Pen(color, 1.8);
            if (_geometryCache.TryGetValue(item.series.Key, out var geometry))
                drawing.DrawGeometry(null, pen, geometry);
        }
        DrawHover(drawing, available, start, duration, muted);
        _legendHits.Clear();
        var legendX = 14d;
        foreach (var item in legendItems)
        {
            var hidden = _hiddenSeries.Contains(item.series.Key);
            var color = new SolidColorBrush(hidden
                ? (_dark ? Color.FromRgb(82, 92, 110) : Color.FromRgb(165, 172, 184))
                : SeriesColors[item.index % SeriesColors.Length]);
            var marker = new Rect(legendX, ActualHeight - 24, 9, 9);
            drawing.DrawRectangle(color, null, marker);
            legendX += 13;
            var labelX = legendX;
            var width = DrawText(
                drawing,
                item.series.Label,
                10,
                FontWeights.Normal,
                hidden ? muted : textColor,
                labelX,
                ActualHeight - 28);
            var hit = new Rect(
                marker.Left - 3,
                ActualHeight - 31,
                width + 19,
                22);
            _legendHits.Add(new LegendHit(item.series.Key, hit));
            if (hidden)
            {
                drawing.DrawLine(
                    new Pen(muted, 1),
                    new Point(labelX, ActualHeight - 19),
                    new Point(labelX + width, ActualHeight - 19));
            }
            legendX += width + 12;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        var point = args.GetPosition(this);
        Cursor = _legendHits.Any(hit => hit.Bounds.Contains(point))
            ? Cursors.Hand
            : _plot.Contains(point)
                ? Cursors.Cross
                : Cursors.Arrow;
        var next = _plot.Contains(point) ? point : (Point?)null;
        if (_hoverPoint == next)
            return;
        _hoverPoint = next;
        InvalidateVisual();
    }

    private void EnsureDataCache()
    {
        var signature = string.Join('|', _hiddenSeries.OrderBy(key => key));
        if (_dataSignature == signature && _cachedLegendItems.Length > 0)
            return;
        _dataSignature = signature;
        _cachedLegendItems = _series.Select((series, index) => (series, index))
            .Where(item => _samples.Any(sample =>
                sample.Values.TryGetValue(item.series.Key, out var value) &&
                value.HasValue))
            .ToArray();
        _cachedAvailable = _cachedLegendItems
            .Where(item => !_hiddenSeries.Contains(item.series.Key))
            .ToArray();
        var values = _cachedAvailable.SelectMany(item => _samples
                .Select(sample => sample.Values.TryGetValue(item.series.Key, out var value)
                    ? value
                    : null)
                .Where(value => value.HasValue)
                .Select(value => value!.Value))
            .ToArray();
        (_cachedAxisMinimum, _cachedAxisMaximum) = ResolveAxisBounds(
            values,
            _minimumBound,
            _maximumBound);
    }

    private void EnsureGeometryCache(
        IReadOnlyList<(SeriesDefinition series, int index)> available,
        DateTimeOffset start,
        double duration,
        double minimum,
        double maximum)
    {
        var signature = string.Join('|', available.Select(item => item.series.Key));
        if (_geometrySignature == signature &&
            Math.Abs(_geometryWidth - _plot.Width) < .1 &&
            Math.Abs(_geometryHeight - _plot.Height) < .1 &&
            Math.Abs(_geometryMinimum - minimum) < .0001 &&
            Math.Abs(_geometryMaximum - maximum) < .0001)
            return;
        _geometrySignature = signature;
        _geometryWidth = _plot.Width;
        _geometryHeight = _plot.Height;
        _geometryMinimum = minimum;
        _geometryMaximum = maximum;
        _geometryCache.Clear();
        foreach (var item in available)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var open = false;
                foreach (var sample in _samples)
                {
                    if (!sample.Values.TryGetValue(
                            item.series.Key,
                            out var value) || !value.HasValue)
                    {
                        open = false;
                        continue;
                    }
                    var x = _plot.Left +
                        (sample.Timestamp - start).TotalMilliseconds /
                        duration * _plot.Width;
                    var normalized = Math.Clamp(
                        (value.Value - minimum) / (maximum - minimum),
                        0,
                        1);
                    var point = new Point(
                        x,
                        _plot.Bottom - normalized * _plot.Height);
                    if (!open)
                    {
                        context.BeginFigure(point, false, false);
                        open = true;
                    }
                    else
                    {
                        context.LineTo(point, true, false);
                    }
                }
            }
            geometry.Freeze();
            _geometryCache[item.series.Key] = geometry;
        }
    }

    internal static (double Minimum, double Maximum) ResolveAxisBounds(
        IReadOnlyCollection<double> values,
        double? minimumBound,
        double? maximumBound)
    {
        var rawMinimum = values.Count > 0 ? values.Min() : 0;
        var rawMaximum = values.Count > 0 ? values.Max() : 1;
        var minimum = minimumBound ?? rawMinimum;
        var maximum = maximumBound ?? rawMaximum;
        if (!minimumBound.HasValue)
        {
            var range = Math.Max(1, maximum - minimum);
            minimum -= range * .08;
        }
        if (!maximumBound.HasValue)
        {
            var range = Math.Max(1, maximum - minimum);
            maximum += range * .08;
        }
        if (maximum <= minimum)
            maximum = minimum + 1;
        return (minimum, maximum);
    }

    internal void ToggleSeriesForTesting(string key)
    {
        if (!_hiddenSeries.Add(key))
            _hiddenSeries.Remove(key);
        _dataSignature = "\0";
        InvalidateVisual();
    }

    internal bool IsSeriesVisibleForTesting(string key) =>
        !_hiddenSeries.Contains(key);

    private void OnMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs args)
    {
        var point = args.GetPosition(this);
        var hit = _legendHits.FirstOrDefault(item => item.Bounds.Contains(point));
        if (hit is null)
            return;
        if (!_hiddenSeries.Add(hit.Key))
            _hiddenSeries.Remove(hit.Key);
        _dataSignature = "\0";
        args.Handled = true;
        InvalidateVisual();
    }

    private void DrawHover(
        DrawingContext drawing,
        IReadOnlyList<(SeriesDefinition series, int index)> available,
        DateTimeOffset start,
        double durationMilliseconds,
        Brush muted)
    {
        if (!_hoverPoint.HasValue || available.Count == 0 || _samples.Count == 0)
            return;
        var point = _hoverPoint.Value;
        var ratio = Math.Clamp(
            (point.X - _plot.Left) / Math.Max(1, _plot.Width),
            0,
            1);
        var target = start.AddMilliseconds(durationMilliseconds * ratio);
        var sampleIndex = NearestSampleIndex(target);
        var sample = _samples[sampleIndex];
        var x = _plot.Left +
            (sample.Timestamp - start).TotalMilliseconds /
            durationMilliseconds * _plot.Width;
        var guide = new Pen(muted, 1) { DashStyle = DashStyles.Dash };
        drawing.DrawLine(
            guide,
            new Point(x, _plot.Top),
            new Point(x, _plot.Bottom));

        var lines = new List<(string Text, Brush Brush)>
        {
            (sample.Timestamp.ToLocalTime().ToString(
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.CurrentCulture), muted)
        };
        foreach (var item in available)
        {
            sample.Values.TryGetValue(item.series.Key, out var value);
            var name = SeriesDisplayName(item.series);
            var formatted = value.HasValue
                ? FormatHoverValue(item.series.Key, value.Value)
                : "--";
            lines.Add(($"{name}: {formatted}", new SolidColorBrush(
                SeriesColors[item.index % SeriesColors.Length])));
        }
        var width = lines.Max(line => MeasureText(
            line.Text,
            10,
            FontWeights.Normal)) + 20;
        var height = lines.Count * 17 + 12;
        var left = x + 10;
        if (left + width > ActualWidth - 6)
            left = x - width - 10;
        left = Math.Clamp(left, 6, Math.Max(6, ActualWidth - width - 6));
        var top = Math.Clamp(
            point.Y - height / 2,
            _plot.Top + 4,
            Math.Max(_plot.Top + 4, _plot.Bottom - height - 4));
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(_dark
                ? Color.FromArgb(242, 10, 16, 32)
                : Color.FromArgb(245, 255, 255, 255)),
            new Pen(new SolidColorBrush(_dark
                ? Color.FromRgb(67, 83, 112)
                : Color.FromRgb(190, 200, 215)), 1),
            new Rect(left, top, width, height),
            7,
            7);
        var y = top + 6;
        foreach (var line in lines)
        {
            DrawText(
                drawing,
                line.Text,
                10,
                FontWeights.Normal,
                line.Brush,
                left + 10,
                y);
            y += 17;
        }
    }

    private int NearestSampleIndex(DateTimeOffset timestamp)
    {
        var low = 0;
        var high = _samples.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_samples[middle].Timestamp < timestamp)
                low = middle + 1;
            else
                high = middle;
        }
        if (low == 0)
            return 0;
        var before = _samples[low - 1].Timestamp;
        return timestamp - before <= _samples[low].Timestamp - timestamp
            ? low - 1
            : low;
    }

    private string SeriesDisplayName(SeriesDefinition series) =>
        series.Label is "%" or "W" or "Wh" or "°C" or "ms" or "RPM"
            ? _title
            : series.Label;

    private string FormatHoverValue(string key, double value)
    {
        var number = value.ToString("0.##", CultureInfo.CurrentCulture);
        if (key.Contains("Utilization", StringComparison.OrdinalIgnoreCase))
            return number + "%";
        if (key.EndsWith("Mhz", StringComparison.OrdinalIgnoreCase))
            return number + " MHz";
        if (key.EndsWith("TemperatureC", StringComparison.OrdinalIgnoreCase))
            return number + " °C";
        if (key.EndsWith("PowerW", StringComparison.OrdinalIgnoreCase))
            return number + " W";
        if (key.EndsWith("Wh", StringComparison.OrdinalIgnoreCase))
            return number + " Wh";
        if (key.EndsWith("Rpm", StringComparison.OrdinalIgnoreCase))
            return number + (_isChinese ? " 转" : " RPM");
        if (key.EndsWith("Gb", StringComparison.OrdinalIgnoreCase))
            return number + " GB";
        if (key.EndsWith("Ms", StringComparison.OrdinalIgnoreCase))
            return number + " ms";
        return number;
    }

    private double MeasureText(
        string text,
        double size,
        FontWeight weight) => CreateFormattedText(
            text,
            size,
            weight,
            Brushes.Transparent).WidthIncludingTrailingWhitespace;

    private double DrawText(
        DrawingContext drawing,
        string text,
        double size,
        FontWeight weight,
        Brush brush,
        double x,
        double y)
    {
        var formatted = CreateFormattedText(text, size, weight, brush);
        drawing.DrawText(formatted, new Point(x, y));
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private FormattedText CreateFormattedText(
        string text,
        double size,
        FontWeight weight,
        Brush brush) => new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private sealed record LegendHit(string Key, Rect Bounds);
}
