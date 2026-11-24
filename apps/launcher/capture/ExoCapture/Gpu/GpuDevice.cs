using System.Runtime.InteropServices;
using DCommon = Vortice.DCommon;
using D2D = Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Vortice.Mathematics;

namespace ExoCapture.Gpu;

/// <summary>
/// One D3D11 device on the graphics card that drives the screens, with video support and multithread protection (the
/// capture thread, the encoder's thread and Media Foundation all use it). Its GPU thread runs at the lowest priority, so
/// a game's frames always come first.
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    internal readonly ID3D11Device Device;
    internal readonly ID3D11DeviceContext Context;
    internal readonly ID3D11VideoDevice VideoDevice;
    internal readonly ID3D11VideoContext1 VideoContext;
    internal readonly IDXGIAdapter1 Adapter;
    internal readonly IMFDXGIDeviceManager Manager;
    internal readonly string Name;
    internal readonly long Luid;
    private readonly ID3D11Multithread _multithread;

    private GpuDevice(IDXGIAdapter1 adapter, ID3D11Device device, ID3D11DeviceContext context)
    {
        Adapter = adapter;
        Device = device;
        Context = context;
        VideoDevice = device.QueryInterface<ID3D11VideoDevice>();
        VideoContext = context.QueryInterface<ID3D11VideoContext1>();
        _multithread = context.QueryInterface<ID3D11Multithread>();
        _multithread.SetMultithreadProtected(true);
        var description = adapter.Description1;
        Name = description.Description.Trim();
        Luid = ((long)description.Luid.HighPart << 32) | description.Luid.LowPart;
        Manager = MediaFactory.MFCreateDXGIDeviceManager();
        Manager.ResetDevice(device);
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        dxgi.SetGPUThreadPriority(-7);
    }

    private static readonly Dictionary<long, GpuDevice> Devices = [];
    private static readonly Lock DevicesGate = new();

    /// <summary>
    /// One device per graphics card for the whole process (capture, snips, Go Live and edits share it), so a texture of
    /// one is usable by all and a screen is duplicated once. The default is the first hardware card.
    /// </summary>
    internal static GpuDevice Shared(long? luid = null)
    {
        lock (DevicesGate)
        {
            if (luid is long want && Devices.TryGetValue(want, out var known)) return known;
            if (luid is null && Devices.Count > 0 && DefaultLuid is long d && Devices.TryGetValue(d, out var first)) return first;
            var device = Create(luid);
            DefaultLuid ??= luid is null ? device.Luid : null;
            if (Devices.TryGetValue(device.Luid, out var raced)) { device.Dispose(); return raced; }
            return Devices[device.Luid] = device;
        }
    }

    private static long? DefaultLuid;

    /// <summary>The device for the card that drives a monitor.</summary>
    internal static GpuDevice ForMonitor(nint monitor) => Shared(LuidOfMonitor(monitor));

    internal readonly record struct AdapterInfo(long Luid, string Name, bool Software);

    internal static int? SelectAdapterIndex(IReadOnlyList<AdapterInfo> adapters, long? requestedLuid, out bool fellBackToDefault)
    {
        fellBackToDefault = false;
        if (requestedLuid is long want)
        {
            for (var i = 0; i < adapters.Count; i++)
                if (!adapters[i].Software && adapters[i].Luid == want)
                    return i;
        }
        for (var i = 0; i < adapters.Count; i++)
        {
            if (!adapters[i].Software)
            {
                fellBackToDefault = requestedLuid is not null;
                return i;
            }
        }
        return null;
    }

    internal static bool ShouldRetryWithout11_1(int hresult) => hresult == unchecked((int)0x80070057);

    internal static bool IsDeviceLost(int hresult) =>
        hresult == unchecked((int)0x887A0005) || hresult == unchecked((int)0x887A0007);

    internal static void Invalidate(long? luid = null)
    {
        List<GpuDevice> stale = [];
        lock (DevicesGate)
        {
            if (luid is long want)
            {
                if (Devices.Remove(want, out var found)) stale.Add(found);
                if (DefaultLuid == want) DefaultLuid = null;
            }
            else
            {
                stale.AddRange(Devices.Values);
                Devices.Clear();
                DefaultLuid = null;
            }
        }
        foreach (var device in stale)
        {
            try { device.Dispose(); } catch { }
        }
    }

    internal static string FormatCreateFailure(string adapterName, int hresult, DeviceCreationFlags flags, ReadOnlySpan<FeatureLevel> levels) =>
        $"Capture unavailable on this GPU: adapter={adapterName}, hr=0x{hresult:X8}, flags={flags}, levels={string.Join(",", levels.ToArray())}.";

    /// <summary>The card the main screen is on (it composes the desktop), else the first hardware one.</summary>
    internal static GpuDevice Create(long? luid = null)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var adapters = new List<IDXGIAdapter1>();
        var infos = new List<AdapterInfo>();
        try
        {
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                var desc = adapter.Description1;
                var id = ((long)desc.Luid.HighPart << 32) | desc.Luid.LowPart;
                var software = (desc.Flags & AdapterFlags.Software) != 0;
                adapters.Add(adapter);
                infos.Add(new AdapterInfo(id, desc.Description.Trim(), software));
            }
            var chosenIndex = SelectAdapterIndex(infos, luid, out var fellBack);
            if (chosenIndex is null)
            {
                var seen = infos.Count == 0 ? "none" : string.Join(", ", infos.Select(a => a.Name));
                Host.Log($"Capture unavailable on this GPU: no hardware DXGI adapter found (adapters={seen}).");
                throw new CaptureException("Capture unavailable on this GPU.");
            }
            var chosen = adapters[chosenIndex.Value];
            var chosenInfo = infos[chosenIndex.Value];
            adapters[chosenIndex.Value] = null!;
            if (fellBack)
                Host.Log($"Requested GPU adapter LUID 0x{luid!.Value:X16} not found; retrying with default adapter {chosenInfo.Name}.");
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            var hr = D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, flags, levels, out ID3D11Device device, out ID3D11DeviceContext context);
            if ((hr.Failure || device is null || context is null) && ShouldRetryWithout11_1(hr.Code))
            {
                Host.Log(FormatCreateFailure(chosenInfo.Name, hr.Code, flags, levels));
                levels = [FeatureLevel.Level_11_0];
                hr = D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, flags, levels, out device, out context);
            }
            if (hr.Failure || device is null || context is null)
            {
                Host.Log(FormatCreateFailure(chosenInfo.Name, hr.Code, flags, levels));
                device?.Dispose();
                context?.Dispose();
                chosen.Dispose();
                throw new CaptureException("Capture unavailable on this GPU.");
            }
            try
            {
                return new GpuDevice(chosen, device, context);
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
            {
                Host.Log($"Capture unavailable on this GPU: adapter={chosenInfo.Name}, hr=0x{ex.HResult:X8}, flags={flags}, levels={string.Join(",", levels)}.");
                context.Dispose();
                device.Dispose();
                chosen.Dispose();
                throw new CaptureException("Capture unavailable on this GPU.");
            }
        }
        finally
        {
            foreach (var adapter in adapters)
                adapter?.Dispose();
        }
    }

    /// <summary>The adapter's LUID that owns a monitor, so a capture of that screen runs on the card that draws it.</summary>
    internal static long? LuidOfMonitor(nint monitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    using (output)
                        if (output.Description.Monitor == monitor)
                        {
                            var d = adapter.Description1;
                            return ((long)d.Luid.HighPart << 32) | d.Luid.LowPart;
                        }
            }
        }
        return null;
    }

    internal ID3D11Texture2D Texture(int width, int height, Format format, BindFlags bind = BindFlags.RenderTarget | BindFlags.ShaderResource,
        ResourceOptionFlags misc = ResourceOptionFlags.None) =>
        Device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1, bind, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, misc));

    /// <summary>The pixels of a texture (BGRA8 or RGBA16F) read back to memory, row by row without padding.</summary>
    internal byte[] Read(ID3D11Texture2D texture, out int width, out int height, out int bytesPerPixel)
    {
        var desc = texture.Description;
        width = (int)desc.Width;
        height = (int)desc.Height;
        bytesPerPixel = desc.Format is Format.R16G16B16A16_Float ? 8 : 4;
        using var staging = Device.CreateTexture2D(new Texture2DDescription(desc.Format, desc.Width, desc.Height, 1, 1, BindFlags.None,
            ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        Context.CopyResource(staging, texture);
        var mapped = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var row = width * bytesPerPixel;
            var pixels = new byte[row * height];
            for (var y = 0; y < height; y++)
                Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), pixels, y * row, row);
            return pixels;
        }
        finally { Context.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        Manager.Dispose();
        _multithread.Dispose();
        VideoContext.Dispose();
        VideoDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
        Adapter.Dispose();
    }
}

/// <summary>
/// The D3D11 video processor between a captured picture and its use: BGRA or scRGB FP16 in, NV12 or P010 for the
/// encoder or BGRA for a picture out, scaled with the aspect kept (black bars), an optional crop, and an optional
/// vertical flip (OpenGL games hand their frames over upside down). It all stays on the graphics card.
/// </summary>
internal sealed class VideoConverter : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly D2D.ID2D1Device? _d2dDevice;
    private readonly D2D.ID2D1DeviceContext? _d2dContext;
    private readonly D2D.ID2D1Effect? _toneMapEffect;
    private readonly ID3D11Texture2D? _sdrToneTarget;
    internal readonly int InWidth, InHeight, OutWidth, OutHeight;
    internal readonly Format OutFormat;

    internal static bool NeedsHdrToSdrToneMap(Format inFormat, bool hdr) =>
        inFormat == Format.R16G16B16A16_Float && !hdr;

    private static unsafe void SetToneMapFloat(D2D.ID2D1Effect effect, D2D.HDRToneMapProperties prop, float value) =>
        effect.SetValue((uint)prop, D2D.PropertyType.Float, &value, sizeof(float));

    private static unsafe void SetToneMapEnum(D2D.ID2D1Effect effect, D2D.HDRToneMapProperties prop, int value) =>
        effect.SetValue((uint)prop, D2D.PropertyType.Enum, &value, sizeof(int));

    internal VideoConverter(GpuDevice gpu, int inWidth, int inHeight, Format inFormat, int outWidth, int outHeight, Format outFormat, bool hdr)
    {
        _gpu = gpu;
        (InWidth, InHeight, OutWidth, OutHeight, OutFormat) = (inWidth, inHeight, outWidth, outHeight, outFormat);
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)inWidth,
            InputHeight = (uint)inHeight,
            OutputWidth = (uint)outWidth,
            OutputHeight = (uint)outHeight,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed,
        };
        _enumerator = gpu.VideoDevice.CreateVideoProcessorEnumerator(content);
        _processor = gpu.VideoDevice.CreateVideoProcessor(_enumerator, 0);
        var vc = gpu.VideoContext;
        var toneMapped = false;
        if (NeedsHdrToSdrToneMap(inFormat, hdr))
        {
            try
            {
                using var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>();
                _d2dDevice = D2D.D2D1.D2D1CreateDevice(dxgiDevice, new D2D.CreationProperties { ThreadingMode = D2D.ThreadingMode.MultiThreaded, Options = D2D.DeviceContextOptions.None });
                _d2dContext = _d2dDevice.CreateDeviceContext(D2D.DeviceContextOptions.None);
                _toneMapEffect = new D2D.ID2D1Effect(_d2dContext.CreateEffect(D2D.EffectGuids.HdrToneMap));
                SetToneMapFloat(_toneMapEffect, D2D.HDRToneMapProperties.InputMaxLuminance, 1000f);
                SetToneMapFloat(_toneMapEffect, D2D.HDRToneMapProperties.OutputMaxLuminance, 250f);
                SetToneMapEnum(_toneMapEffect, D2D.HDRToneMapProperties.DisplayMode, (int)D2D.HDRToneMapDisplayMode.Sdr);
                _sdrToneTarget = gpu.Texture(inWidth, inHeight, Format.B8G8R8A8_UNorm);
                toneMapped = true;
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or CaptureException)
            {
                Host.Log("HDR tone map unavailable, using direct converter: " + ex.Message);
                _toneMapEffect?.Dispose();
                _toneMapEffect = null;
                _d2dContext?.Dispose();
                _d2dContext = null;
                _d2dDevice?.Dispose();
                _d2dDevice = null;
                _sdrToneTarget?.Dispose();
                _sdrToneTarget = null;
            }
        }
        // What the input is: scRGB from an HDR screen (tone-mapped to BGRA8 when output is SDR), sRGB from capture, or a decoded clip's YCbCr.
        var inSpace = toneMapped ? ColorSpaceType.RgbFullG22NoneP709 : inFormat switch
        {
            Format.R16G16B16A16_Float => ColorSpaceType.RgbFullG10NoneP709,
            Format.P010 => ColorSpaceType.YcbcrStudioG2084LeftP2020,
            Format.NV12 => ColorSpaceType.YcbcrStudioG22LeftP709,
            _ => ColorSpaceType.RgbFullG22NoneP709,
        };
        var outSpace = outFormat switch
        {
            Format.P010 when hdr => ColorSpaceType.YcbcrStudioG2084LeftP2020,
            Format.NV12 or Format.P010 => ColorSpaceType.YcbcrStudioG22LeftP709,
            _ => ColorSpaceType.RgbFullG22NoneP709,
        };
        vc.VideoProcessorSetStreamColorSpace1(_processor, 0, inSpace);
        vc.VideoProcessorSetOutputColorSpace1(_processor, outSpace);
        vc.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        vc.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        vc.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor { Rgba = new VideoColorRgba { A = 1, R = 0, G = 0, B = 0 } });
    }

    /// <summary>The largest rectangle of the source's shape inside the output, centered, with even sides.</summary>
    internal static RawRect Fit(int srcW, int srcH, int outW, int outH)
    {
        if (srcW <= 0 || srcH <= 0) return new RawRect(0, 0, outW, outH);
        var scale = Math.Min(outW / (double)srcW, outH / (double)srcH);
        var w = Math.Min(outW, (int)Math.Round(srcW * scale) & ~1);
        var h = Math.Min(outH, (int)Math.Round(srcH * scale) & ~1);
        var x = ((outW - w) / 2) & ~1;
        var y = ((outH - h) / 2) & ~1;
        return new RawRect(x, y, x + w, y + h);
    }

    /// <summary>Converts <paramref name="source"/> (or its <paramref name="crop"/>) into <paramref name="target"/>.</summary>
    internal void Convert(ID3D11Texture2D source, ID3D11Texture2D target, RawRect? crop = null, bool flip = false, bool fill = false, uint slice = 0)
    {
        var vc = _gpu.VideoContext;
        var srcDesc = source.Description;
        var src = crop ?? new RawRect(0, 0, (int)srcDesc.Width, (int)srcDesc.Height);
        var dest = fill ? new RawRect(0, 0, OutWidth, OutHeight) : Fit(src.Right - src.Left, src.Bottom - src.Top, OutWidth, OutHeight);
        var vpSource = source;
        var vpSlice = slice;
        if (_d2dContext is not null && _toneMapEffect is not null && _sdrToneTarget is not null
            && srcDesc.Format == Format.R16G16B16A16_Float && srcDesc.ArraySize == 1 && slice == 0
            && srcDesc.Width == _sdrToneTarget.Description.Width && srcDesc.Height == _sdrToneTarget.Description.Height)
        {
            using var srcSurface = source.QueryInterface<IDXGISurface>();
            using var dstSurface = _sdrToneTarget.QueryInterface<IDXGISurface>();
            using var srcBitmap = _d2dContext.CreateBitmapFromDxgiSurface(srcSurface,
                new D2D.BitmapProperties1(new DCommon.PixelFormat(Format.R16G16B16A16_Float, DCommon.AlphaMode.Ignore), 96, 96, D2D.BitmapOptions.None));
            using var dstBitmap = _d2dContext.CreateBitmapFromDxgiSurface(dstSurface,
                new D2D.BitmapProperties1(new DCommon.PixelFormat(Format.B8G8R8A8_UNorm, DCommon.AlphaMode.Ignore), 96, 96, D2D.BitmapOptions.Target));
            using var output = _toneMapEffect.Output;
            _toneMapEffect.SetInput(0, srcBitmap, true);
            _d2dContext.Target = dstBitmap;
            try
            {
                _d2dContext.BeginDraw();
                _d2dContext.DrawImage(output);
                _d2dContext.EndDraw(out _, out _).CheckError();
            }
            finally
            {
                _d2dContext.Target = null;
                _toneMapEffect.SetInput(0, null, true);
            }
            vpSource = _sdrToneTarget;
            vpSlice = 0;
        }
        using var input = _gpu.VideoDevice.CreateVideoProcessorInputView(vpSource, _enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = vpSlice },
        });
        using var outputView = _gpu.VideoDevice.CreateVideoProcessorOutputView(target, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        });
        vc.VideoProcessorSetStreamSourceRect(_processor, 0, true, src);
        vc.VideoProcessorSetStreamDestRect(_processor, 0, true, dest);
        vc.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, OutWidth, OutHeight));
        vc.VideoProcessorSetStreamMirror(_processor, 0, flip, false, flip);
        var stream = new VideoProcessorStream { Enable = true, InputSurface = input };
        vc.VideoProcessorBlt(_processor, outputView, 0, 1, [stream]).CheckError();
    }

    public void Dispose()
    {
        _toneMapEffect?.Dispose();
        _d2dContext?.Dispose();
        _d2dDevice?.Dispose();
        _sdrToneTarget?.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
    }
}
