using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ThinkBookToolkit;

internal sealed class AnimatedBackgroundImage : Grid, IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(
        [".bmp", ".dib", ".gif", ".jpg", ".jpeg", ".jpe", ".jfif", ".png"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VideoExtensions = new(
        [".avi", ".m4v", ".mov", ".mp4", ".mpeg", ".mpg", ".wmv"],
        StringComparer.OrdinalIgnoreCase);

    private readonly Image _image = new()
    {
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private LowMemoryVideoPlayer? _video;
    private WriteableBitmap? _videoOutput;
    private readonly object _videoFrameGate = new();
    private byte[]? _queuedVideoFrame;
    private byte[]? _drawingVideoFrame;
    private byte[]? _videoBlurScratch;
    private int _queuedVideoWidth;
    private int _queuedVideoHeight;
    private int _videoBlurRadiusX;
    private int _videoBlurRadiusY;
    private bool _videoFrameDispatchPending;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _videoOpenWatchdog = new()
    {
        Interval = TimeSpan.FromSeconds(10)
    };
    private IGifAnimation? _gif;
    private WriteableBitmap? _gifOutput;
    private byte[]? _displayBuffer;
    private byte[]? _blurBuffer;
    private BitmapSource? _staticNormalSource;
    private int _playbackSpeedPercent = 100;
    private double _scalePercent = 100;
    private double _naturalWidth;
    private double _naturalHeight;
    private double _blurRadius;
    private bool _inverted;
    private bool _playbackActive = true;
    private bool _videoOpened;
    private bool _videoPlaybackRequested;
    private bool _videoFailureReported;
    private bool _videoPreviewOnly;
    private bool _videoPreviewFramePresented;
    private BackgroundImageSizeMode _sizeMode = BackgroundImageSizeMode.Fixed;
    private Size _viewport;

    public AnimatedBackgroundImage()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_image);
        _timer.Tick += OnFrameTick;
        _videoOpenWatchdog.Tick += (_, _) =>
        {
            _videoOpenWatchdog.Stop();
            if (!_videoOpened && IsVideo)
            {
                ReportVideoFailure(
                    "Windows did not open the background video within 10 seconds. Check the file format and installed Media Foundation codecs.");
            }
        };
        IsVisibleChanged += (_, _) => SyncPlaybackState();
        Loaded += (_, _) => SyncPlaybackState();
        Unloaded += (_, _) => PausePlayback();
    }

    public event EventHandler<string>? PlaybackFailed;

    public string LoadedPath { get; private set; } = string.Empty;
    public bool IsGif => ExtensionEquals(".gif");
    public bool IsVideo => VideoExtensions.Contains(Path.GetExtension(LoadedPath));
    public bool SupportsPlaybackSpeed => IsGif || IsVideo;
    public double BlurRadius => _blurRadius;
    public ImageSource? Source => _image.Source;
    public Stretch Stretch { get; private set; } = Stretch.Uniform;
    internal int FrameCountForTesting => _gif?.FrameCount ??
                                         (_staticNormalSource is null ? 0 : 1);
    internal int CachedComposedFrameCountForTesting => _gif is null ? 0 : 1;
    internal long EstimatedManagedGifBytesForTesting =>
        (_gif?.EstimatedManagedBytes ?? 0) +
        (_displayBuffer?.LongLength ?? 0) +
        (_blurBuffer?.LongLength ?? 0);
    internal bool UsesSourceFrameBlurForTesting =>
        _gif is not null && _blurRadius > 0 && _image.Effect is null &&
        Effect is null;
    internal int PlaybackSpeedPercentForTesting => _playbackSpeedPercent;
    internal bool InvertedForTesting => _inverted;
    internal string VideoDiagnosticsForTesting =>
        _video?.Diagnostics ?? string.Empty;
    internal TimeSpan CurrentIntervalForTesting => _timer.Interval;
    internal void AdvanceGifFrameForTesting()
    {
        _gif?.MoveNext();
        UpdateGifOutput();
    }

    public static bool IsSupportedPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (ImageExtensions.Contains(Path.GetExtension(path)) ||
         VideoExtensions.Contains(Path.GetExtension(path)));

    public static bool IsVideoPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsAnimatedPath(string path) =>
        IsVideoPath(path) || string.Equals(
            Path.GetExtension(path),
            ".gif",
            StringComparison.OrdinalIgnoreCase);

    public void SetScale(double percent)
    {
        _scalePercent = Math.Clamp(percent, 10, 500);
        ApplySize();
    }

    public void SetSizeMode(BackgroundImageSizeMode mode)
    {
        _sizeMode = Enum.IsDefined(mode)
            ? mode
            : BackgroundImageSizeMode.Fixed;
        ApplySize();
    }

    public void SetViewport(Size viewport)
    {
        _viewport = new Size(
            Math.Max(0, viewport.Width),
            Math.Max(0, viewport.Height));
        ApplySize();
    }

    public void SetPlaybackSpeedPercent(int percent)
    {
        _playbackSpeedPercent = Math.Clamp(percent, 10, 500);
        if (_timer.IsEnabled && _gif is not null)
            _timer.Interval = EffectiveDelay(_gif.CurrentDelay);
        _video?.SetSpeedPercent(_playbackSpeedPercent);
    }

    public void SetPlaybackActive(bool active)
    {
        _playbackActive = active;
        SyncPlaybackState();
    }

    public void SetInverted(bool inverted)
    {
        if (_inverted == inverted)
            return;
        _inverted = inverted;
        if (_gif is not null)
            UpdateGifOutput();
        else if (_staticNormalSource is not null)
            _image.Source = inverted
                ? Invert(_staticNormalSource)
                : _staticNormalSource;
    }

    public void SetBlurRadius(double radius)
    {
        _blurRadius = Math.Clamp(radius, 0, 40);
        _video?.SetMaximumFrameRate(_blurRadius > 0 ? 15 : 30);
        UpdateVideoBlurRadii();
        if (_blurRadius <= 0)
        {
            _blurBuffer = null;
            if (!_inverted)
                _displayBuffer = null;
        }
        ApplyBlurRendering();
    }

    public void LoadFile(string? path, bool enableAnimatedPlayback = true)
    {
        Clear();
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!IsSupportedPath(path))
            throw new NotSupportedException(
                "The selected background media format is not supported.");
        LoadedPath = Path.GetFullPath(path);
        if (IsVideo)
        {
            _image.Visibility = Visibility.Visible;
            _videoPreviewOnly = !enableAnimatedPlayback;
            _video = new LowMemoryVideoPlayer();
            _video.Opened += OnVideoOpened;
            _video.FrameReady += OnVideoFrameReady;
            _video.Failed += (_, error) => ReportVideoFailure(error);
            _video.SetSpeedPercent(_playbackSpeedPercent);
            _video.SetMaximumFrameRate(_blurRadius > 0 ? 15 : 30);
            _video.Open(LoadedPath);
            ToolkitLog.Info(
                "Background video source configured: " +
                Path.GetFileName(LoadedPath) + ".");
            SyncPlaybackState();
            return;
        }

        if (IsGif && enableAnimatedPlayback)
        {
            _image.Visibility = Visibility.Visible;
            _gif = PackedGifAnimation.Create(LoadedPath);
            _naturalWidth = _gif.Width;
            _naturalHeight = _gif.Height;
            _gifOutput = new WriteableBitmap(
                _gif.Width,
                _gif.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null);
            _image.Source = _gifOutput;
            _gif.MoveNext();
            UpdateGifOutput();
            if (_gif.FrameCount > 1)
            {
                _timer.Interval = EffectiveDelay(_gif.CurrentDelay);
                _timer.Start();
            }
            ApplySize();
            return;
        }

        using var stream = new FileStream(
            LoadedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("The image contains no frames.");
        _image.Visibility = Visibility.Visible;
        var frame = decoder.Frames[0];
        if (frame.CanFreeze)
            frame.Freeze();
        _staticNormalSource = frame;
        _naturalWidth = frame.Width;
        _naturalHeight = frame.Height;
        _image.Source = _inverted ? Invert(frame) : frame;
        ApplySize();
    }

    public void Clear()
    {
        _timer.Stop();
        _videoOpenWatchdog.Stop();
        Effect = null;
        CacheMode = null;
        _image.Effect = null;
        _image.CacheMode = null;
        _image.RenderTransform = Transform.Identity;
        _image.Width = double.NaN;
        _image.Height = double.NaN;
        var video = _video;
        if (video is not null)
        {
            try { video.Dispose(); } catch { }
            _video = null;
        }
        _videoOutput = null;
        lock (_videoFrameGate)
        {
            _queuedVideoFrame = null;
            _drawingVideoFrame = null;
            _videoBlurScratch = null;
            _videoFrameDispatchPending = false;
        }
        _videoOpened = false;
        _videoPlaybackRequested = false;
        _videoFailureReported = false;
        _videoPreviewOnly = false;
        _videoPreviewFramePresented = false;
        _image.Visibility = Visibility.Visible;
        _image.Source = null;
        LoadedPath = string.Empty;
        _gif?.Dispose();
        _gif = null;
        _gifOutput = null;
        _displayBuffer = null;
        _blurBuffer = null;
        _staticNormalSource = null;
        _naturalWidth = 0;
        _naturalHeight = 0;
        _blurRadius = 0;
        Width = double.NaN;
        Height = double.NaN;
    }

    private void OnFrameTick(object? sender, EventArgs args)
    {
        if (_gif is null || _gif.FrameCount <= 1)
        {
            _timer.Stop();
            return;
        }
        _gif.MoveNext();
        UpdateGifOutput();
        _timer.Interval = EffectiveDelay(_gif.CurrentDelay);
    }

    private void UpdateGifOutput()
    {
        if (_gif is null || _gifOutput is null)
            return;
        WriteDynamicPixels(
            _gifOutput,
            _gif.CurrentPixels,
            _gif.Width,
            _gif.Height,
            _inverted);
    }

    private void WriteDynamicPixels(
        WriteableBitmap output,
        byte[] source,
        int pixelWidth,
        int pixelHeight,
        bool inverted)
    {
        var pixels = source;
        if (inverted || _blurRadius > 0)
        {
            if (_displayBuffer is null || _displayBuffer.Length != pixels.Length)
                _displayBuffer = new byte[pixels.Length];
            Buffer.BlockCopy(pixels, 0, _displayBuffer, 0, pixels.Length);
            if (inverted)
                InvertPixels(_displayBuffer);
            if (_blurRadius > 0)
            {
                if (_blurBuffer is null || _blurBuffer.Length != pixels.Length)
                    _blurBuffer = new byte[pixels.Length];
                var sourceRadiusX = Math.Clamp(
                    (int)Math.Round(_blurRadius / Math.Max(
                        .1,
                        Width / Math.Max(1, pixelWidth))),
                    1,
                    40);
                var sourceRadiusY = Math.Clamp(
                    (int)Math.Round(_blurRadius / Math.Max(
                        .1,
                        Height / Math.Max(1, pixelHeight))),
                    1,
                    40);
                BoxBlurBgra(
                    _displayBuffer,
                    _blurBuffer,
                    pixelWidth,
                    pixelHeight,
                    sourceRadiusX,
                    sourceRadiusY);
            }
            pixels = _displayBuffer;
        }
        output.WritePixels(
            new Int32Rect(0, 0, pixelWidth, pixelHeight),
            pixels,
            pixelWidth * 4,
            0);
    }

    private static void BoxBlurBgra(
        byte[] pixels,
        byte[] scratch,
        int width,
        int height,
        int radiusX,
        int radiusY)
    {
        var diameterX = radiusX * 2 + 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var blue = 0;
            var green = 0;
            var red = 0;
            var alpha = 0;
            for (var sample = -radiusX; sample <= radiusX; sample++)
            {
                var index = (row + Math.Clamp(sample, 0, width - 1)) * 4;
                blue += pixels[index];
                green += pixels[index + 1];
                red += pixels[index + 2];
                alpha += pixels[index + 3];
            }
            for (var x = 0; x < width; x++)
            {
                var target = (row + x) * 4;
                scratch[target] = (byte)(blue / diameterX);
                scratch[target + 1] = (byte)(green / diameterX);
                scratch[target + 2] = (byte)(red / diameterX);
                scratch[target + 3] = (byte)(alpha / diameterX);
                var remove = (row + Math.Clamp(x - radiusX, 0, width - 1)) * 4;
                var add = (row + Math.Clamp(x + radiusX + 1, 0, width - 1)) * 4;
                blue += pixels[add] - pixels[remove];
                green += pixels[add + 1] - pixels[remove + 1];
                red += pixels[add + 2] - pixels[remove + 2];
                alpha += pixels[add + 3] - pixels[remove + 3];
            }
        }
        var diameterY = radiusY * 2 + 1;
        for (var x = 0; x < width; x++)
        {
            var blue = 0;
            var green = 0;
            var red = 0;
            var alpha = 0;
            for (var sample = -radiusY; sample <= radiusY; sample++)
            {
                var index = (Math.Clamp(sample, 0, height - 1) * width + x) * 4;
                blue += scratch[index];
                green += scratch[index + 1];
                red += scratch[index + 2];
                alpha += scratch[index + 3];
            }
            for (var y = 0; y < height; y++)
            {
                var target = (y * width + x) * 4;
                pixels[target] = (byte)(blue / diameterY);
                pixels[target + 1] = (byte)(green / diameterY);
                pixels[target + 2] = (byte)(red / diameterY);
                pixels[target + 3] = (byte)(alpha / diameterY);
                var remove = (Math.Clamp(y - radiusY, 0, height - 1) * width + x) * 4;
                var add = (Math.Clamp(y + radiusY + 1, 0, height - 1) * width + x) * 4;
                blue += scratch[add] - scratch[remove];
                green += scratch[add + 1] - scratch[remove + 1];
                red += scratch[add + 2] - scratch[remove + 2];
                alpha += scratch[add + 3] - scratch[remove + 3];
            }
        }
    }

    private TimeSpan EffectiveDelay(TimeSpan source) =>
        TimeSpan.FromMilliseconds(Math.Clamp(
            source.TotalMilliseconds * 100d / _playbackSpeedPercent,
            1,
            60_000));

    private void OnVideoOpened(object? sender, VideoFrameFormat format)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnVideoOpened(sender, format));
            return;
        }
        if (_video is null || Dispatcher.HasShutdownStarted)
            return;
        _videoOpenWatchdog.Stop();
        _videoOpened = true;
        _naturalWidth = Math.Max(1, format.NaturalWidth);
        _naturalHeight = Math.Max(1, format.NaturalHeight);
        _videoOutput = new WriteableBitmap(
            format.OutputWidth,
            format.OutputHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        _image.Source = _videoOutput;
        ToolkitLog.Info(
            $"Background video opened: " +
            $"{format.NaturalWidth}x{format.NaturalHeight}; " +
            $"decoded={format.OutputWidth}x{format.OutputHeight}; " +
            $"speed={_playbackSpeedPercent}%; " +
            (_video?.Diagnostics ?? "decoder=unknown") + ".");
        ApplySize();
        SyncPlaybackState();
    }

    private void OnVideoFrameReady(byte[] frame, int width, int height)
    {
        if (Dispatcher.HasShutdownStarted || _videoOutput is null ||
            !_playbackActive || !IsVisible)
            return;
        lock (_videoFrameGate)
        {
            if (_queuedVideoFrame is null ||
                _queuedVideoFrame.Length != frame.Length)
                _queuedVideoFrame = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, _queuedVideoFrame, 0, frame.Length);
            if (_inverted)
                InvertPixels(_queuedVideoFrame);
            var radiusX = Volatile.Read(ref _videoBlurRadiusX);
            var radiusY = Volatile.Read(ref _videoBlurRadiusY);
            if (radiusX > 0 && radiusY > 0)
            {
                if (_videoBlurScratch is null ||
                    _videoBlurScratch.Length != frame.Length)
                    _videoBlurScratch = new byte[frame.Length];
                BoxBlurBgra(
                    _queuedVideoFrame,
                    _videoBlurScratch,
                    width,
                    height,
                    radiusX,
                    radiusY);
            }
            _queuedVideoWidth = width;
            _queuedVideoHeight = height;
            if (_videoFrameDispatchPending)
                return;
            _videoFrameDispatchPending = true;
        }
        if (_videoPreviewOnly)
            _video?.SetActive(false);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(PresentQueuedVideoFrame));
    }

    private void PresentQueuedVideoFrame()
    {
        byte[]? frame;
        int width;
        int height;
        lock (_videoFrameGate)
        {
            (_drawingVideoFrame, _queuedVideoFrame) =
                (_queuedVideoFrame, _drawingVideoFrame);
            frame = _drawingVideoFrame;
            width = _queuedVideoWidth;
            height = _queuedVideoHeight;
            _videoFrameDispatchPending = false;
        }
        if (frame is null || _videoOutput is null ||
            !_playbackActive || !IsVisible)
            return;
        _videoOutput.WritePixels(
            new Int32Rect(0, 0, width, height),
            frame,
            width * 4,
            0);
        if (_videoPreviewOnly && !_videoPreviewFramePresented)
        {
            _videoPreviewFramePresented = true;
            var video = _video;
            _video = null;
            _videoOpened = false;
            video?.Dispose();
        }
    }

    private void SyncPlaybackState()
    {
        if (!_playbackActive || !IsVisible)
        {
            PausePlayback();
            return;
        }
        if (_gif is { FrameCount: > 1 })
            _timer.Start();
        if (IsVideo && _video is not null)
        {
            try
            {
                if (!_videoPlaybackRequested)
                {
                    _videoPlaybackRequested = true;
                    ToolkitLog.Info(
                        "Opening background video through Windows Media Foundation.");
                }
                _video.SetActive(true);
                if (!_videoOpened && !_videoOpenWatchdog.IsEnabled)
                    _videoOpenWatchdog.Start();
            }
            catch (Exception ex)
            {
                ReportVideoFailure(ex.GetBaseException().Message);
            }
        }
    }

    private void PausePlayback()
    {
        _timer.Stop();
        if (IsVideo && _videoOpened && _video is not null)
        {
            _video.SetActive(false);
            ToolkitLog.Info("Background video playback paused.");
        }
    }

    private void ReportVideoFailure(string message)
    {
        _videoOpenWatchdog.Stop();
        if (_videoFailureReported)
            return;
        _videoFailureReported = true;
        ToolkitLog.Warning(
            "Background video playback failed: " + message);
        PlaybackFailed?.Invoke(this, message);
    }

    private void ApplySize()
    {
        if (_naturalWidth <= 0 || _naturalHeight <= 0)
            return;
        Stretch = _sizeMode == BackgroundImageSizeMode.Stretch
            ? Stretch.Fill
            : Stretch.Uniform;
        _image.Stretch = Stretch;
        switch (_sizeMode)
        {
            case BackgroundImageSizeMode.MatchLength when _viewport.Height > 0:
                Height = _viewport.Height;
                Width = _naturalWidth / _naturalHeight * Height;
                break;
            case BackgroundImageSizeMode.MatchWidth when _viewport.Width > 0:
                Width = _viewport.Width;
                Height = _naturalHeight / _naturalWidth * Width;
                break;
            case BackgroundImageSizeMode.Stretch
                when _viewport.Width > 0 && _viewport.Height > 0:
                Width = _viewport.Width;
                Height = _viewport.Height;
                break;
            default:
                var factor = _scalePercent / 100d;
                Width = _naturalWidth * factor;
                Height = _naturalHeight * factor;
                break;
        }
        ApplyBlurRendering();
        UpdateVideoBlurRadii();
    }

    private void UpdateVideoBlurRadii()
    {
        if (_blurRadius <= 0 || _videoOutput is null)
        {
            Volatile.Write(ref _videoBlurRadiusX, 0);
            Volatile.Write(ref _videoBlurRadiusY, 0);
            return;
        }
        Volatile.Write(
            ref _videoBlurRadiusX,
            Math.Clamp(
                (int)Math.Round(_blurRadius / Math.Max(
                    .1,
                    Width / Math.Max(1, _videoOutput.PixelWidth))),
                1,
                40));
        Volatile.Write(
            ref _videoBlurRadiusY,
            Math.Clamp(
                (int)Math.Round(_blurRadius / Math.Max(
                    .1,
                    Height / Math.Max(1, _videoOutput.PixelHeight))),
                1,
                40));
    }

    private void ApplyBlurRendering()
    {
        var target = _image;
        if (_gif is not null || IsVideo)
        {
            ResetBlurElement(target);
            if (_gif is not null)
                UpdateGifOutput();
            return;
        }
        if (_blurRadius <= 0 ||
            !double.IsFinite(Width) || Width <= 0)
        {
            ResetBlurElement(target);
            return;
        }
        ResetBlurElement(target);
        target.Effect = new BlurEffect
        {
            Radius = _blurRadius,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance
        };
        target.CacheMode = null;
        RenderOptions.SetBitmapScalingMode(
            target,
            BitmapScalingMode.LowQuality);
    }

    private static void ResetBlurElement(FrameworkElement? element)
    {
        if (element is null)
            return;
        element.Effect = null;
        element.CacheMode = null;
        element.RenderTransform = Transform.Identity;
        element.Width = double.NaN;
        element.Height = double.NaN;
        element.HorizontalAlignment = HorizontalAlignment.Stretch;
        element.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private bool ExtensionEquals(string extension) => string.Equals(
        Path.GetExtension(LoadedPath),
        extension,
        StringComparison.OrdinalIgnoreCase);

    internal static GifDecodeResult DecodeGif(
        IReadOnlyList<BitmapFrame> sourceFrames)
    {
        var animation = GifAnimation.Create(sourceFrames);
        var output = new List<BitmapSource>(animation.FrameCount);
        var delays = new List<TimeSpan>(animation.FrameCount);
        for (var index = 0; index < animation.FrameCount; index++)
        {
            animation.MoveNext();
            output.Add(CreateFrame(
                animation.CurrentPixels,
                animation.Width,
                animation.Height));
            delays.Add(animation.CurrentDelay);
        }
        return new(output, delays);
    }

    private static GifFrameDescriptor Descriptor(
        BitmapFrame frame,
        int logicalWidth,
        int logicalHeight)
    {
        var left = Math.Max(0, MetadataInt(frame, "/imgdesc/Left", 0));
        var top = Math.Max(0, MetadataInt(frame, "/imgdesc/Top", 0));
        var width = Math.Clamp(
            MetadataInt(frame, "/imgdesc/Width", frame.PixelWidth),
            0,
            Math.Max(0, logicalWidth - left));
        var height = Math.Clamp(
            MetadataInt(frame, "/imgdesc/Height", frame.PixelHeight),
            0,
            Math.Max(0, logicalHeight - top));
        var destination = new Int32Rect(left, top, width, height);
        var converted = new FormatConvertedBitmap(
            frame,
            PixelFormats.Bgra32,
            null,
            0);
        BitmapSource pixels = converted;
        if (converted.PixelWidth == logicalWidth &&
            converted.PixelHeight == logicalHeight &&
            width > 0 && height > 0)
        {
            pixels = new CroppedBitmap(converted, destination);
        }
        var hundredths = MetadataInt(frame, "/grctlext/Delay", 10);
        if (hundredths <= 0)
            hundredths = 10;
        return new(
            pixels,
            pixels.PixelWidth,
            pixels.PixelHeight,
            destination,
            MetadataInt(frame, "/grctlext/Disposal", 0),
            TimeSpan.FromMilliseconds(hundredths * 10d));
    }

    private static void BlendFrame(
        byte[] canvas,
        int logicalWidth,
        int logicalHeight,
        GifFrameDescriptor frame,
        ref byte[] scratch)
    {
        var required = frame.PixelWidth * frame.PixelHeight * 4;
        if (scratch.Length < required)
            scratch = new byte[required];
        frame.Source.CopyPixels(scratch, frame.PixelWidth * 4, 0);
        var pixels = scratch;
        var width = Math.Min(frame.Rect.Width, frame.PixelWidth);
        var height = Math.Min(frame.Rect.Height, frame.PixelHeight);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceIndex = (y * frame.PixelWidth + x) * 4;
                var targetIndex =
                    ((frame.Rect.Y + y) * logicalWidth + frame.Rect.X + x) * 4;
                BlendPixel(pixels, sourceIndex, canvas, targetIndex);
            }
        }
    }

    private static void BlendPixel(
        byte[] source,
        int sourceIndex,
        byte[] target,
        int targetIndex)
    {
        var sourceAlpha = source[sourceIndex + 3];
        if (sourceAlpha == 0)
            return;
        if (sourceAlpha == 255)
        {
            target[targetIndex] = source[sourceIndex];
            target[targetIndex + 1] = source[sourceIndex + 1];
            target[targetIndex + 2] = source[sourceIndex + 2];
            target[targetIndex + 3] = 255;
            return;
        }
        var targetAlpha = target[targetIndex + 3];
        var inverse = 255 - sourceAlpha;
        var outputAlpha = sourceAlpha + targetAlpha * inverse / 255;
        if (outputAlpha == 0)
            return;
        for (var channel = 0; channel < 3; channel++)
        {
            var premultiplied =
                source[sourceIndex + channel] * sourceAlpha +
                target[targetIndex + channel] * targetAlpha * inverse / 255;
            target[targetIndex + channel] =
                (byte)Math.Clamp(premultiplied / outputAlpha, 0, 255);
        }
        target[targetIndex + 3] = (byte)outputAlpha;
    }

    private static Color LogicalBackground(
        IReadOnlyList<BitmapFrame> frames)
    {
        var first = frames[0];
        var index = MetadataInt(first, "/logscrdesc/BackgroundColorIndex", -1);
        var transparent = frames.Any(frame =>
            MetadataInt(frame, "/grctlext/TransparencyFlag", 0) != 0 &&
            MetadataInt(frame, "/grctlext/TransparentColorIndex", -2) == index);
        if (transparent || index < 0 || first.Palette is null ||
            index >= first.Palette.Colors.Count)
            return Colors.Transparent;
        return first.Palette.Colors[index];
    }

    private static int MetadataInt(
        BitmapFrame frame,
        string query,
        int fallback)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.GetQuery(query) is { } value)
                return Convert.ToInt32(value);
        }
        catch
        {
        }
        return fallback;
    }

    private static void Fill(byte[] pixels, Color color)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = color.A;
        }
    }

    private static void FillRect(
        byte[] pixels,
        int width,
        int height,
        Int32Rect rect,
        Color color)
    {
        var left = Math.Clamp(rect.X, 0, width);
        var top = Math.Clamp(rect.Y, 0, height);
        var right = Math.Clamp(rect.X + rect.Width, left, width);
        var bottom = Math.Clamp(rect.Y + rect.Height, top, height);
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var index = (y * width + x) * 4;
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = color.A;
            }
        }
    }

    private static BitmapSource CreateFrame(
        byte[] pixels,
        int width,
        int height)
    {
        var result = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            (byte[])pixels.Clone(),
            width * 4);
        if (result.CanFreeze)
            result.Freeze();
        return result;
    }

    private static BitmapSource Invert(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(
            source,
            PixelFormats.Bgra32,
            null,
            0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        InvertPixels(pixels);
        return CreateFrame(pixels, converted.PixelWidth, converted.PixelHeight);
    }

    private static void InvertPixels(byte[] pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)(255 - pixels[index]);
            pixels[index + 1] = (byte)(255 - pixels[index + 1]);
            pixels[index + 2] = (byte)(255 - pixels[index + 2]);
        }
    }

    public void Dispose() => Clear();

    internal sealed record GifDecodeResult(
        IReadOnlyList<BitmapSource> Frames,
        IReadOnlyList<TimeSpan> Delays);

    private interface IGifAnimation : IDisposable
    {
        int Width { get; }
        int Height { get; }
        int FrameCount { get; }
        byte[] CurrentPixels { get; }
        TimeSpan CurrentDelay { get; }
        long EstimatedManagedBytes { get; }
        void MoveNext();
    }

    private sealed record GifFrameDescriptor(
        BitmapSource Source,
        int PixelWidth,
        int PixelHeight,
        Int32Rect Rect,
        int Disposal,
        TimeSpan Delay);

    private sealed class GifAnimation : IGifAnimation
    {
        private readonly IReadOnlyList<GifFrameDescriptor> _frames;
        private readonly Color _background;
        private byte[] _canvas;
        private byte[]? _previousBackup;
        private byte[] _frameScratch = [];
        private Int32Rect _previousRect;
        private int _previousDisposal;
        private int _index = -1;

        private GifAnimation(
            int width,
            int height,
            Color background,
            IReadOnlyList<GifFrameDescriptor> frames)
        {
            Width = width;
            Height = height;
            _background = background;
            _frames = frames;
            _canvas = new byte[width * height * 4];
            Fill(_canvas, background);
        }

        public int Width { get; }
        public int Height { get; }
        public int FrameCount => _frames.Count;
        public byte[] CurrentPixels => _canvas;
        public TimeSpan CurrentDelay => _index >= 0
            ? _frames[_index].Delay
            : TimeSpan.FromMilliseconds(100);
        public long EstimatedManagedBytes =>
            _canvas.LongLength + (_previousBackup?.LongLength ?? 0) +
            _frameScratch.LongLength;

        public static GifAnimation Create(
            IReadOnlyList<BitmapFrame> sourceFrames)
        {
            if (sourceFrames.Count == 0)
                throw new InvalidDataException("The GIF contains no frames.");
            var width = Math.Max(1, MetadataInt(
                sourceFrames[0],
                "/logscrdesc/Width",
                sourceFrames.Max(frame => frame.PixelWidth)));
            var height = Math.Max(1, MetadataInt(
                sourceFrames[0],
                "/logscrdesc/Height",
                sourceFrames.Max(frame => frame.PixelHeight)));
            var descriptors = sourceFrames
                .Select(frame => Descriptor(frame, width, height))
                .ToArray();
            return new(
                width,
                height,
                LogicalBackground(sourceFrames),
                descriptors);
        }

        public void MoveNext()
        {
            var next = _index + 1;
            if (next >= _frames.Count)
            {
                Reset();
                next = 0;
            }
            if (_index >= 0)
            {
                if (_previousDisposal == 2)
                {
                    FillRect(
                        _canvas,
                        Width,
                        Height,
                        _previousRect,
                        _background);
                }
                else if (_previousDisposal == 3 &&
                         _previousBackup is not null)
                {
                    Buffer.BlockCopy(
                        _previousBackup,
                        0,
                        _canvas,
                        0,
                        _canvas.Length);
                }
            }
            var frame = _frames[next];
            if (frame.Disposal == 3)
            {
                _previousBackup ??= new byte[_canvas.Length];
                Buffer.BlockCopy(
                    _canvas,
                    0,
                    _previousBackup,
                    0,
                    _canvas.Length);
            }
            BlendFrame(_canvas, Width, Height, frame, ref _frameScratch);
            _index = next;
            _previousDisposal = frame.Disposal;
            _previousRect = frame.Rect;
        }

        public void Dispose()
        {
        }

        private void Reset()
        {
            Fill(_canvas, _background);
            _previousRect = new Int32Rect();
            _previousDisposal = 0;
            _index = -1;
        }
    }

    private sealed class PackedGifAnimation : IGifAnimation
    {
        private static readonly (int Start, int Step)[] InterlacePasses =
            [(0, 8), (4, 8), (2, 4), (1, 2)];
        private readonly IReadOnlyList<PackedGifFrame> _frames;
        private readonly GifDataReader _reader;
        private readonly Color _background;
        private readonly byte[] _canvas;
        private byte[] _decoded = [];
        private byte[] _raster = [];
        private byte[] _compressed = [];
        private readonly ushort[] _prefix = new ushort[4096];
        private readonly byte[] _suffix = new byte[4096];
        private readonly byte[] _stack = new byte[4097];
        private byte[]? _previousBackup;
        private Int32Rect _previousRect;
        private int _previousDisposal;
        private int _index = -1;

        private PackedGifAnimation(
            int width,
            int height,
            Color background,
            IReadOnlyList<PackedGifFrame> frames,
            GifDataReader reader)
        {
            Width = width;
            Height = height;
            _background = background;
            _frames = frames;
            _reader = reader;
            _canvas = new byte[checked(width * height * 4)];
            Fill(_canvas, background);
        }

        public int Width { get; }
        public int Height { get; }
        public int FrameCount => _frames.Count;
        public byte[] CurrentPixels => _canvas;
        public TimeSpan CurrentDelay => _index >= 0
            ? _frames[_index].Delay
            : TimeSpan.FromMilliseconds(100);
        public long EstimatedManagedBytes =>
            _canvas.LongLength + _decoded.LongLength + _raster.LongLength +
            _compressed.LongLength +
            (_previousBackup?.LongLength ?? 0) +
            _frames.Select(frame => frame.Palette)
                .Distinct()
                .Sum(palette => (long)palette.Length * 4);

        public static PackedGifAnimation Create(string path)
        {
            var reader = new GifDataReader(path);
            try
            {
            var signature = reader.ReadAscii(6);
            if (signature is not ("GIF87a" or "GIF89a"))
                throw new InvalidDataException("The file is not a valid GIF image.");
            var width = Math.Max(1, reader.ReadUInt16());
            var height = Math.Max(1, reader.ReadUInt16());
            var packed = reader.ReadByte();
            var backgroundIndex = reader.ReadByte();
            _ = reader.ReadByte();
            var globalPalette = (packed & 0x80) != 0
                ? reader.ReadPalette(1 << ((packed & 0x07) + 1))
                : [];
            var frames = new List<PackedGifFrame>();
            var disposal = 0;
            var delay = 10;
            int? transparentIndex = null;
            var transparentBackground = false;
            while (!reader.End)
            {
                var marker = reader.ReadByte();
                if (marker == 0x3B)
                    break;
                if (marker == 0x21)
                {
                    var label = reader.ReadByte();
                    if (label == 0xF9)
                    {
                        var size = reader.ReadByte();
                        if (size != 4)
                            throw new InvalidDataException("Invalid GIF graphic-control block.");
                        var control = reader.ReadByte();
                        disposal = (control >> 2) & 0x07;
                        delay = reader.ReadUInt16();
                        var transparent = reader.ReadByte();
                        transparentIndex = (control & 0x01) != 0
                            ? transparent
                            : null;
                        _ = reader.ReadByte();
                    }
                    else
                    {
                        reader.SkipSubBlocks();
                    }
                    continue;
                }
                if (marker != 0x2C)
                    throw new InvalidDataException("Unexpected GIF block marker.");
                var left = reader.ReadUInt16();
                var top = reader.ReadUInt16();
                var frameWidth = reader.ReadUInt16();
                var frameHeight = reader.ReadUInt16();
                var imagePacked = reader.ReadByte();
                var palette = (imagePacked & 0x80) != 0
                    ? reader.ReadPalette(1 << ((imagePacked & 0x07) + 1))
                    : globalPalette;
                if (palette.Length == 0)
                    throw new InvalidDataException("GIF frame has no color table.");
                var minimumCodeSize = reader.ReadByte();
                var blocks = reader.ReadSubBlockReferences();
                transparentBackground |= transparentIndex == backgroundIndex;
                frames.Add(new PackedGifFrame(
                    new Int32Rect(left, top, frameWidth, frameHeight),
                    palette,
                    transparentIndex,
                    (imagePacked & 0x40) != 0,
                    minimumCodeSize,
                    blocks.Blocks,
                    blocks.Length,
                    disposal,
                    TimeSpan.FromMilliseconds((delay <= 0 ? 10 : delay) * 10d)));
                disposal = 0;
                delay = 10;
                transparentIndex = null;
            }
            if (frames.Count == 0)
                throw new InvalidDataException("The GIF contains no image frames.");
            var background = !transparentBackground &&
                             backgroundIndex < globalPalette.Length
                ? globalPalette[backgroundIndex]
                : Colors.Transparent;
            return new PackedGifAnimation(
                width,
                height,
                background,
                frames,
                reader);
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        public void MoveNext()
        {
            var next = _index + 1;
            if (next >= _frames.Count)
            {
                Fill(_canvas, _background);
                _previousDisposal = 0;
                _previousRect = new Int32Rect();
                _index = -1;
                next = 0;
            }
            if (_index >= 0)
            {
                if (_previousDisposal == 2)
                    FillRect(_canvas, Width, Height, _previousRect, _background);
                else if (_previousDisposal == 3 && _previousBackup is not null)
                    Buffer.BlockCopy(
                        _previousBackup,
                        0,
                        _canvas,
                        0,
                        _canvas.Length);
            }
            var frame = _frames[next];
            if (frame.Disposal == 3)
            {
                _previousBackup ??= new byte[_canvas.Length];
                Buffer.BlockCopy(
                    _canvas,
                    0,
                    _previousBackup,
                    0,
                    _canvas.Length);
            }
            var pixelCount = checked(frame.Rect.Width * frame.Rect.Height);
            EnsureBuffer(ref _decoded, pixelCount);
            EnsureBuffer(ref _compressed, frame.CompressedLength);
            _reader.ReadBlocks(frame.Blocks, _compressed);
            var decoded = DecodeLzw(
                _compressed,
                frame.CompressedLength,
                frame.MinimumCodeSize,
                _decoded,
                pixelCount);
            if (decoded < pixelCount)
                Array.Clear(_decoded, decoded, pixelCount - decoded);
            byte[] pixels;
            if (frame.Interlaced)
            {
                EnsureBuffer(ref _raster, pixelCount);
                Deinterlace(_decoded, _raster, frame.Rect.Width, frame.Rect.Height);
                pixels = _raster;
            }
            else
            {
                pixels = _decoded;
            }
            BlendIndexed(frame, pixels);
            _index = next;
            _previousDisposal = frame.Disposal;
            _previousRect = frame.Rect;
        }

        private void BlendIndexed(PackedGifFrame frame, byte[] indexes)
        {
            var width = Math.Min(frame.Rect.Width, Width - frame.Rect.X);
            var height = Math.Min(frame.Rect.Height, Height - frame.Rect.Y);
            if (width <= 0 || height <= 0)
                return;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var paletteIndex = indexes[y * frame.Rect.Width + x];
                    if (frame.TransparentIndex == paletteIndex ||
                        paletteIndex >= frame.Palette.Length)
                        continue;
                    var color = frame.Palette[paletteIndex];
                    var target = ((frame.Rect.Y + y) * Width + frame.Rect.X + x) * 4;
                    _canvas[target] = color.B;
                    _canvas[target + 1] = color.G;
                    _canvas[target + 2] = color.R;
                    _canvas[target + 3] = 255;
                }
            }
        }

        private int DecodeLzw(
            byte[] compressed,
            int compressedLength,
            int minimumCodeSize,
            byte[] output,
            int expected)
        {
            minimumCodeSize = Math.Clamp(minimumCodeSize, 2, 8);
            var clearCode = 1 << minimumCodeSize;
            var endCode = clearCode + 1;
            var nextCode = endCode + 1;
            var codeSize = minimumCodeSize + 1;
            for (var index = 0; index < clearCode; index++)
                _suffix[index] = (byte)index;
            var dataIndex = 0;
            var bits = 0;
            var datum = 0;
            var outputIndex = 0;
            var oldCode = -1;
            byte first = 0;
            while (outputIndex < expected)
            {
                while (bits < codeSize)
                {
                    if (dataIndex >= compressedLength)
                        return outputIndex;
                    datum |= compressed[dataIndex++] << bits;
                    bits += 8;
                }
                var code = datum & ((1 << codeSize) - 1);
                datum >>= codeSize;
                bits -= codeSize;
                if (code == clearCode)
                {
                    codeSize = minimumCodeSize + 1;
                    nextCode = endCode + 1;
                    oldCode = -1;
                    continue;
                }
                if (code == endCode)
                    break;
                if (oldCode < 0)
                {
                    if (code >= clearCode)
                        continue;
                    first = _suffix[code];
                    output[outputIndex++] = first;
                    oldCode = code;
                    continue;
                }
                var current = code;
                var stackSize = 0;
                if (current >= nextCode)
                {
                    _stack[stackSize++] = first;
                    current = oldCode;
                }
                while (current >= clearCode && current < 4096)
                {
                    _stack[stackSize++] = _suffix[current];
                    current = _prefix[current];
                    if (stackSize >= _stack.Length - 1)
                        break;
                }
                if (current >= clearCode)
                    break;
                first = _suffix[current];
                _stack[stackSize++] = first;
                while (stackSize > 0 && outputIndex < expected)
                    output[outputIndex++] = _stack[--stackSize];
                if (nextCode < 4096)
                {
                    _prefix[nextCode] = (ushort)oldCode;
                    _suffix[nextCode] = first;
                    nextCode++;
                    if (nextCode == 1 << codeSize && codeSize < 12)
                        codeSize++;
                }
                oldCode = code;
            }
            return outputIndex;
        }

        private static void Deinterlace(
            byte[] source,
            byte[] destination,
            int width,
            int height)
        {
            var sourceRow = 0;
            foreach (var (start, step) in InterlacePasses)
            {
                for (var y = start; y < height; y += step)
                {
                    Buffer.BlockCopy(
                        source,
                        sourceRow * width,
                        destination,
                        y * width,
                        width);
                    sourceRow++;
                }
            }
        }

        private static void EnsureBuffer(ref byte[] buffer, int length)
        {
            if (buffer.Length < length)
                buffer = new byte[length];
        }

        public void Dispose() => _reader.Dispose();

        private sealed record PackedGifFrame(
            Int32Rect Rect,
            Color[] Palette,
            int? TransparentIndex,
            bool Interlaced,
            int MinimumCodeSize,
            IReadOnlyList<GifDataBlock> Blocks,
            int CompressedLength,
            int Disposal,
            TimeSpan Delay);

        private sealed record GifDataBlock(long Offset, int Count);

        private sealed class GifDataReader : IDisposable
        {
            private readonly FileStream _stream;

            public GifDataReader(string path) => _stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.RandomAccess);
            public bool End => _stream.Position >= _stream.Length;

            public byte ReadByte()
            {
                if (End)
                    throw new EndOfStreamException();
                var value = _stream.ReadByte();
                if (value < 0)
                    throw new EndOfStreamException();
                return (byte)value;
            }

            public int ReadUInt16() => ReadByte() | ReadByte() << 8;

            public string ReadAscii(int count)
            {
                Ensure(count);
                var buffer = new byte[count];
                _stream.ReadExactly(buffer);
                var result = System.Text.Encoding.ASCII.GetString(buffer);
                return result;
            }

            public Color[] ReadPalette(int count)
            {
                Ensure(checked(count * 3));
                var result = new Color[count];
                for (var index = 0; index < count; index++)
                {
                    var red = ReadByte();
                    var green = ReadByte();
                    var blue = ReadByte();
                    result[index] = Color.FromRgb(red, green, blue);
                }
                return result;
            }

            public (IReadOnlyList<GifDataBlock> Blocks, int Length)
                ReadSubBlockReferences()
            {
                var blocks = new List<GifDataBlock>();
                var total = 0;
                while (true)
                {
                    var count = ReadByte();
                    if (count == 0)
                        break;
                    Ensure(count);
                    blocks.Add(new GifDataBlock(_stream.Position, count));
                    total = checked(total + count);
                    _stream.Seek(count, SeekOrigin.Current);
                }
                return (blocks, total);
            }

            public void ReadBlocks(
                IReadOnlyList<GifDataBlock> blocks,
                byte[] destination)
            {
                var offset = 0;
                foreach (var block in blocks)
                {
                    _stream.Seek(block.Offset, SeekOrigin.Begin);
                    _stream.ReadExactly(destination.AsSpan(offset, block.Count));
                    offset += block.Count;
                }
            }

            public void SkipSubBlocks()
            {
                while (true)
                {
                    var count = ReadByte();
                    if (count == 0)
                        return;
                    Ensure(count);
                    _stream.Seek(count, SeekOrigin.Current);
                }
            }

            private void Ensure(int count)
            {
                if (count < 0 || _stream.Position > _stream.Length - count)
                    throw new EndOfStreamException();
            }

            public void Dispose() => _stream.Dispose();
        }
    }
}
