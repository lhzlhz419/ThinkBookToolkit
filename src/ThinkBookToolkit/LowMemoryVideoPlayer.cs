using SharpGen.Runtime;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using static Vortice.Direct3D11.D3D11;

namespace ThinkBookToolkit;

internal sealed record VideoFrameFormat(
    int NaturalWidth,
    int NaturalHeight,
    int OutputWidth,
    int OutputHeight);

internal sealed class LowMemoryVideoPlayer : IDisposable
{
    private const int MaximumOutputLongEdge = 1280;
    private readonly ManualResetEventSlim _active = new(initialState: true);
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private int _speedPercent = 100;
    private int _maximumFrameRate = 30;
    private bool _disposed;
    private string _diagnostics = string.Empty;

    internal string Diagnostics => Volatile.Read(ref _diagnostics);

    public event EventHandler<VideoFrameFormat>? Opened;
    public event Action<byte[], int, int>? FrameReady;
    public event EventHandler<string>? Failed;

    public void Open(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _worker = Task.Run(() => DecodeLoop(path, token), token);
    }

    public void SetActive(bool active)
    {
        if (active)
            _active.Set();
        else
            _active.Reset();
    }

    public void SetSpeedPercent(int value) =>
        Volatile.Write(ref _speedPercent, Math.Clamp(value, 10, 500));

    public void SetMaximumFrameRate(int value) =>
        Volatile.Write(ref _maximumFrameRate, Math.Clamp(value, 1, 60));

    private void DecodeLoop(string path, CancellationToken token)
    {
        var mediaStarted = false;
        var stage = "MFStartup";
        try
        {
            MediaFactory.MFStartup().CheckError();
            mediaStarted = true;
            stage = "read native video format";
            Guid nativeSubtype;
            uint naturalWidth;
            uint naturalHeight;
            using (var metadataReader =
                   MediaFactory.MFCreateSourceReaderFromURL(path, null))
            using (var native = metadataReader.GetNativeMediaType(
                       SourceReaderIndex.FirstVideoStream,
                       0))
            {
                nativeSubtype = native.GetGUID(MediaTypeAttributeKeys.Subtype);
                MediaFactory.MFGetAttributeSize(
                        native,
                        MediaTypeAttributeKeys.FrameSize,
                        out naturalWidth,
                        out naturalHeight)
                    .CheckError();
            }
            var wantsHardware = nativeSubtype == VideoFormatGuids.H265 ||
                                nativeSubtype == VideoFormatGuids.Hevc ||
                                nativeSubtype == VideoFormatGuids.HevcEs;
            stage = "create video decoder resources";
            var hardwareError = string.Empty;
            using var hardware = wantsHardware
                ? HardwareVideoResources.TryCreate(out hardwareError)
                : null;
            var useHardware = hardware is not null;
            var deviceManager = hardware?.DeviceManager;
            using var attributes = MediaFactory.MFCreateAttributes(
                useHardware ? 4u : 1u);
            stage = "enable advanced video processing";
            attributes.Set(
                    SourceReaderAttributeKeys.EnableAdvancedVideoProcessing,
                    1u)
                .CheckError();
            if (deviceManager is not null)
            {
                attributes.Set(
                        SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms,
                        1)
                    .CheckError();
                attributes.Set(
                        SourceReaderAttributeKeys.D3DManager,
                        deviceManager)
                    .CheckError();
                attributes.Set(SinkWriterAttributeKeys.LowLatency, true)
                    .CheckError();
            }
            stage = "create source reader";
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(
                path,
                attributes);
            stage = "stream selection";
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            var (outputWidth, outputHeight) = Scale(
                checked((int)naturalWidth),
                checked((int)naturalHeight));
            using var outputType = MediaFactory.MFCreateMediaType();
            stage = "RGB output type";
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video)
                .CheckError();
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32)
                .CheckError();
            MediaFactory.MFSetAttributeSize(
                    outputType,
                    MediaTypeAttributeKeys.FrameSize,
                    (uint)outputWidth,
                    (uint)outputHeight)
                .CheckError();
            reader.SetCurrentMediaType(
                SourceReaderIndex.FirstVideoStream,
                outputType);
            using var actualOutputType = reader.GetCurrentMediaType(
                SourceReaderIndex.FirstVideoStream);
            Volatile.Write(
                ref _diagnostics,
                "codec=" + nativeSubtype +
                "; hardware=" + useHardware +
                (string.IsNullOrWhiteSpace(hardwareError)
                    ? string.Empty
                    : "; hardwareFallback=" + hardwareError) +
                "; subtype=" +
                actualOutputType.GetGUID(MediaTypeAttributeKeys.Subtype));
            stage = "opened callback";
            Opened?.Invoke(this, new VideoFrameFormat(
                (int)naturalWidth,
                (int)naturalHeight,
                outputWidth,
                outputHeight));

            var frame = new byte[checked(outputWidth * outputHeight * 4)];
            using var context = hardware?.Device.ImmediateContext;
            ID3D11Texture2D? stagingTexture = null;
            var clock = Stopwatch.StartNew();
            var lastFrameDispatch = TimeSpan.MinValue;
            long firstTimestamp = -1;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    stage = "sample read";
                    _active.Wait(token);
                    using var sample = reader.ReadSample(
                        SourceReaderIndex.FirstVideoStream,
                        SourceReaderControlFlag.None,
                        out _,
                        out var flags,
                        out var timestamp);
                    if ((flags & SourceReaderFlag.EndOfStream) != 0)
                    {
                        reader.SetCurrentPosition(0);
                        firstTimestamp = -1;
                        clock.Restart();
                        continue;
                    }
                    if (sample is null)
                        continue;
                    if (firstTimestamp < 0)
                    {
                        firstTimestamp = timestamp;
                        clock.Restart();
                    }
                    var speed = Math.Max(10, Volatile.Read(ref _speedPercent));
                    var due = (timestamp - firstTimestamp) / 10_000d * 100d / speed;
                    var wait = due - clock.Elapsed.TotalMilliseconds;
                    if (wait > 1)
                        Task.Delay(TimeSpan.FromMilliseconds(wait), token)
                            .GetAwaiter().GetResult();
                    if (lastFrameDispatch != TimeSpan.MinValue &&
                        clock.Elapsed - lastFrameDispatch <
                        TimeSpan.FromSeconds(
                            1d / Math.Max(
                                1,
                                Volatile.Read(ref _maximumFrameRate))))
                        continue;
                    lastFrameDispatch = clock.Elapsed;
                    stage = "sample copy";
                    using var mediaBuffer = sample.GetBufferByIndex(0);
                    using var dxgiBuffer =
                        mediaBuffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
                    if (dxgiBuffer is not null)
                    {
                        CopyDxgiFrame(
                            hardware?.Device ?? throw new InvalidOperationException(
                                "A DXGI video frame was returned without a D3D device."),
                            context ?? throw new InvalidOperationException(
                                "A DXGI video frame was returned without a D3D context."),
                            dxgiBuffer,
                            ref stagingTexture,
                            frame,
                            outputWidth,
                            outputHeight,
                            ref _diagnostics);
                    }
                    else
                    {
                        using var contiguous =
                            sample.ConvertToContiguousBuffer();
                        CopySystemMemoryFrame(contiguous, frame);
                    }
                    FrameReady?.Invoke(frame, outputWidth, outputHeight);
                }
            }
            finally
            {
                stagingTexture?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, stage + ": " + ex.GetBaseException().Message);
        }
        finally
        {
            if (mediaStarted)
            {
                try { MediaFactory.MFShutdown(); } catch { }
            }
        }
    }

    private static (int Width, int Height) Scale(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("The video has an invalid frame size.");
        var scale = Math.Min(1d, MaximumOutputLongEdge / (double)Math.Max(width, height));
        var outputWidth = Math.Max(2, (int)Math.Round(width * scale));
        var outputHeight = Math.Max(2, (int)Math.Round(height * scale));
        outputWidth &= ~1;
        outputHeight &= ~1;
        return (Math.Max(2, outputWidth), Math.Max(2, outputHeight));
    }

    private static void CopySystemMemoryFrame(
        IMFMediaBuffer buffer,
        byte[] frame)
    {
        buffer.Lock(out var address, out _, out var length);
        try
        {
            var copy = Math.Min(length, frame.Length);
            Marshal.Copy(address, frame, 0, copy);
            if (copy < frame.Length)
                Array.Clear(frame, copy, frame.Length - copy);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private static void CopyDxgiFrame(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IMFDXGIBuffer buffer,
        ref ID3D11Texture2D? staging,
        byte[] frame,
        int width,
        int height,
        ref string diagnostics)
    {
        var pointer = buffer.GetResource(typeof(ID3D11Texture2D).GUID);
        using var source = (ID3D11Texture2D)pointer;
        var sourceDescription = source.Description;
        if (!diagnostics.Contains("surface=", StringComparison.Ordinal))
            Volatile.Write(
                ref diagnostics,
                diagnostics + "; surface=" + sourceDescription.Format +
                $" {sourceDescription.Width}x{sourceDescription.Height}; " +
                "subresource=" + buffer.SubresourceIndex);
        if (staging is null ||
            staging.Description.Width != sourceDescription.Width ||
            staging.Description.Height != sourceDescription.Height ||
            staging.Description.Format != sourceDescription.Format)
        {
            staging?.Dispose();
            sourceDescription.MipLevels = 1;
            sourceDescription.ArraySize = 1;
            sourceDescription.Usage = ResourceUsage.Staging;
            sourceDescription.BindFlags = BindFlags.None;
            sourceDescription.CPUAccessFlags = CpuAccessFlags.Read;
            sourceDescription.MiscFlags = ResourceOptionFlags.None;
            staging = device.CreateTexture2D(sourceDescription);
        }
        context.CopySubresourceRegion(
            staging,
            0,
            0,
            0,
            0,
            source,
            buffer.SubresourceIndex,
            null);
        var mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            var rowBytes = checked(width * 4);
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    IntPtr.Add(mapped.DataPointer, checked(row * (int)mapped.RowPitch)),
                    frame,
                    checked(row * rowBytes),
                    rowBytes);
            }
            // MF RGB32 is BGRX on D3D surfaces. Some drivers leave X as zero;
            // WPF's Bgra32 interprets it as alpha and would render a valid
            // decoded frame fully transparent.
            for (var index = 3; index < frame.Length; index += 4)
                frame[index] = byte.MaxValue;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private sealed class HardwareVideoResources : IDisposable
    {
        private HardwareVideoResources(
            ID3D11Device device,
            IMFDXGIDeviceManager deviceManager)
        {
            Device = device;
            DeviceManager = deviceManager;
        }

        public ID3D11Device Device { get; }
        public IMFDXGIDeviceManager DeviceManager { get; }

        public static HardwareVideoResources? TryCreate(out string error)
        {
            ID3D11Device? device = null;
            IMFDXGIDeviceManager? manager = null;
            try
            {
                device = D3D11CreateDevice(
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport |
                    DeviceCreationFlags.VideoSupport);
                manager = MediaFactory.MFCreateDXGIDeviceManager();
                manager.ResetDevice(device).CheckError();
                error = string.Empty;
                return new HardwareVideoResources(device, manager);
            }
            catch (Exception exception)
            {
                manager?.Dispose();
                device?.Dispose();
                error = exception.GetBaseException().Message;
                return null;
            }
        }

        public void Dispose()
        {
            DeviceManager.Dispose();
            Device.Dispose();
        }
    }

    private void Stop()
    {
        var cancellation = _cancellation;
        _cancellation = null;
        var worker = _worker;
        _worker = null;
        try { cancellation?.Cancel(); } catch { }
        _active.Set();
        if (worker is null)
        {
            cancellation?.Dispose();
            return;
        }
        try
        {
            if (!worker.Wait(TimeSpan.FromSeconds(3)))
            {
                _ = worker.ContinueWith(
                    _ => cancellation?.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }
        }
        catch (AggregateException exception)
            when (exception.Flatten().InnerExceptions.All(
                inner => inner is OperationCanceledException ||
                         inner is TaskCanceledException))
        {
        }
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        // A synchronous frame callback can still be returning from the UI
        // dispatcher. The slim event is intentionally left for finalization
        // instead of blocking the UI thread during media teardown.
    }
}
