using ExoCapture.Gpu;
using ExoCapture.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExoCapture.Capture;

/// <summary>
/// The screen's own copy of itself (DXGI desktop duplication): a whole monitor, or one window cut from it while it is on
/// top. It works when Windows capture is off on a PC (its service disabled), and it is how a snip freezes the screen,
/// in FP16 on an HDR screen. Windows lets a process duplicate a screen once, so every user of a screen (instant
/// replay, a snip, Go Live) shares one <see cref="Duplicator"/> and reads its newest copy.
/// </summary>
internal sealed class DuplicationSource : IFrameSource
{
    private readonly Duplicator _shared;
    private readonly nint _window;
    private bool _disposed;

    public string Kind { get; }
    public int Width => _shared.Width;
    public int Height => _shared.Height;
    public bool Hdr => _shared.Hdr;
    public string? Ended { get; private set; }
    public bool HadFrame => _shared.HadFrame;

    internal DuplicationSource(GpuDevice gpu, nint monitor, nint window, bool hdr, string kind)
    {
        _window = window;
        Kind = kind;
        _shared = Duplicator.Open(gpu, monitor, hdr);
    }

    /// <summary>The monitor's output on this device's card.</summary>
    internal static IDXGIOutput6? OutputOf(GpuDevice gpu, nint monitor, out OutputDescription1 description)
    {
        description = default;
        for (uint o = 0; gpu.Adapter.EnumOutputs(o, out var output).Success; o++)
        {
            if (output.Description.Monitor == monitor)
            {
                var six = output.QueryInterfaceOrNull<IDXGIOutput6>();
                output.Dispose();
                if (six is not null) description = six.Description1;
                return six;
            }
            output.Dispose();
        }
        return null;
    }

    /// <summary>Whether the monitor runs in HDR now (Windows HD Color on).</summary>
    internal static bool MonitorIsHdr(GpuDevice gpu, nint monitor)
    {
        using var output = OutputOf(gpu, monitor, out var d);
        return output is not null && d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
    }

    public Frame? Latest()
    {
        if (Ended is not null) return null;
        if (_window != 0 && !Win32.IsWindow(_window)) { Ended = "the window closed"; return null; }
        if (_shared.Latest() is not { } frame) return null;
        if (_window == 0) return frame;
        if (Win32.FrameBounds(_window) is not { } bounds) return null;
        var d = _shared.Desktop;
        var left = Math.Clamp(bounds.Left - d.Left, 0, Width - 2);
        var top = Math.Clamp(bounds.Top - d.Top, 0, Height - 2);
        var right = Math.Clamp(bounds.Right - d.Left, left + 2, Width);
        var bottom = Math.Clamp(bounds.Bottom - d.Top, top + 2, Height);
        return frame with { Crop = new RawRect(left, top, right, bottom) };
    }

    /// <summary>One picture of the whole monitor now (the snip's frozen screen).</summary>
    internal ID3D11Texture2D? Grab(int timeoutMs = 500)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (_shared.Latest() is { } frame) return frame.Texture;
            Thread.Sleep(10);
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shared.Release();
    }
}

/// <summary>One desktop duplication of one screen, shared by everything that reads that screen; it goes when the last reader does.</summary>
internal sealed class Duplicator
{
    private static readonly Dictionary<nint, Duplicator> Opened = [];
    private static readonly Lock OpenGate = new();
    private readonly GpuDevice _gpu;
    private readonly nint _monitor;
    private readonly bool _wantHdr;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _copy;
    private long _time;
    private int _users;
    private readonly Lock _gate = new();

    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal bool Hdr { get; private set; }
    internal bool HadFrame { get; private set; }
    internal Win32.RECT Desktop { get; private set; }

    private Duplicator(GpuDevice gpu, nint monitor, bool hdr)
    {
        (_gpu, _monitor, _wantHdr) = (gpu, monitor, hdr);
        Reopen();
    }

    internal static Duplicator Open(GpuDevice gpu, nint monitor, bool hdr)
    {
        lock (OpenGate)
        {
            if (!Opened.TryGetValue(monitor, out var d)) Opened[monitor] = d = new Duplicator(gpu, monitor, hdr);
            d._users++;
            return d;
        }
    }

    internal void Release()
    {
        lock (OpenGate)
        {
            if (--_users > 0) return;
            Opened.Remove(_monitor);
        }
        lock (_gate)
        {
            _duplication?.Dispose();
            _copy?.Dispose();
            _duplication = null;
            _copy = null;
        }
    }

    private void Reopen()
    {
        _duplication?.Dispose();
        _duplication = null;
        using var output = DuplicationSource.OutputOf(_gpu, _monitor, out var description) ?? throw new CaptureException("capture the screen (no output on this graphics card)");
        var c = description.DesktopCoordinates;
        Desktop = new Win32.RECT { Left = c.Left, Top = c.Top, Right = c.Right, Bottom = c.Bottom };
        Hdr = _wantHdr && description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
        Format[] formats = Hdr ? [Format.R16G16B16A16_Float] : [Format.B8G8R8A8_UNorm];
        try { _duplication = Duplicate(output, formats); }
        catch (SharpGen.Runtime.SharpGenException) when (!Hdr)
        {
            // The older call, for a process Windows does not count as per-monitor aware.
            try { _duplication = output.DuplicateOutput(_gpu.Device); }
            catch (SharpGen.Runtime.SharpGenException ex) { throw new CaptureException($"capture the screen (0x{ex.HResult:X8})"); }
        }
        catch (SharpGen.Runtime.SharpGenException ex) { throw new CaptureException($"capture the screen (0x{ex.HResult:X8})"); }
        var mode = _duplication.Description.ModeDescription;
        Width = (int)mode.Width;
        Height = (int)mode.Height;
    }

    // IDXGIOutput5::DuplicateOutput1 (vtable slot 26), called directly: the binding's own wrapper crashed here.
    private unsafe IDXGIOutputDuplication Duplicate(IDXGIOutput6 output, Format[] formats)
    {
        nint result;
        fixed (Format* list = formats)
        {
            var call = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, Format*, nint*, int>)(*(void***)output.NativePointer)[26];
            var hr = call(output.NativePointer, _gpu.Device.NativePointer, 0, (uint)formats.Length, list, &result);
            if (hr < 0) throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(hr));
        }
        return new IDXGIOutputDuplication(result);
    }

    internal Frame? Latest()
    {
        lock (_gate)
        {
            try
            {
                if (_duplication is null) Reopen();
                var hr = _duplication!.AcquireNextFrame(0, out _, out var resource);
                if (hr.Success && resource is not null)
                {
                    using (resource)
                    {
                        using var texture = resource.QueryInterface<ID3D11Texture2D>();
                        var desc = texture.Description;
                        if (_copy is null || _copy.Description.Width != desc.Width || _copy.Description.Height != desc.Height || _copy.Description.Format != desc.Format)
                        {
                            _copy?.Dispose();
                            _copy = _gpu.Texture((int)desc.Width, (int)desc.Height, desc.Format);
                        }
                        _gpu.Context.CopyResource(_copy, texture);
                    }
                    _duplication.ReleaseFrame();
                    _time = Win32.Now();
                    HadFrame = true;
                }
                else if (hr.Code == unchecked((int)0x887A0026)) Reopen(); // DXGI_ERROR_ACCESS_LOST: a mode change or a full-screen switch
            }
            catch (SharpGen.Runtime.SharpGenException) { _duplication?.Dispose(); _duplication = null; }
            catch (CaptureException) { _duplication = null; }
            return _copy is null ? null : new Frame(_copy, _time, null, false);
        }
    }
}
