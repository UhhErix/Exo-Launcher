using System.Runtime.InteropServices;
using ExoCapture.Gpu;
using ExoCapture.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ExoCapture.Capture;

/// <summary>
/// The screen's own copy of itself (DXGI desktop duplication): a whole monitor, or one window cut from it while it is on
/// top. It works when Windows capture is off on a PC (its service disabled), and it is how a snip freezes the screen,
/// in FP16 on an HDR screen. Windows lets a process duplicate a screen once, so every user of a screen (instant
/// replay, a snip, Go Live) shares one desktop-native <see cref="Duplicator"/> and reads its newest copy.
/// </summary>
internal sealed class DuplicationSource : IFrameSource
{
    internal const int PointerMonochrome = 1, PointerColor = 2, PointerMaskedColor = 4;

    private readonly GpuDevice _gpu;
    private Duplicator _shared;
    private nint _monitor;
    private readonly nint _window;
    private readonly bool _wantHdr;
    private readonly bool _cursor;
    private ID3D11Texture2D? _cursorTarget;
    private ID3D11Texture2D? _patchStaging;
    private int _patchW, _patchH;
    private bool _patchHdr;
    private bool _disposed;

    public string Kind { get; }
    public int Width => _shared.Width;
    public int Height => _shared.Height;
    public bool Hdr => _shared.Hdr;
    public string? Ended { get; private set; }
    public bool HadFrame => _shared.HadFrame;

    internal DuplicationSource(GpuDevice gpu, nint monitor, nint window, bool hdr, string kind, bool cursor = true)
    {
        _gpu = gpu;
        _monitor = monitor;
        _window = window;
        _wantHdr = hdr;
        _cursor = cursor;
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

    /// <summary>Window bounds intersected against monitor desktop coordinates; null when empty or too small.</summary>
    internal static Win32.RECT? IntersectWindow(Win32.RECT bounds, Win32.RECT desktop, int width, int height)
    {
        var left = Math.Max(0, bounds.Left - desktop.Left);
        var top = Math.Max(0, bounds.Top - desktop.Top);
        var right = Math.Min(width, bounds.Right - desktop.Left);
        var bottom = Math.Min(height, bounds.Bottom - desktop.Top);
        if (right - left < 2 || bottom - top < 2) return null;
        return new Win32.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
    }

    public Frame? Latest()
    {
        if (Ended is not null) return null;
        RawRect? crop = null;
        if (_window != 0)
        {
            if (!Win32.IsWindow(_window)) { Ended = "the window closed"; return null; }
            if (Win32.IsIconic(_window)) return null;
            var currentMonitor = Win32.MonitorFromWindow(_window, Win32.MONITOR_DEFAULTTONEAREST);
            if (currentMonitor != 0 && currentMonitor != _monitor)
            {
                try
                {
                    var next = Duplicator.Open(_gpu, currentMonitor, _wantHdr);
                    if (next.Hdr != _shared.Hdr) { next.Release(); return null; }
                    _shared.Release();
                    _shared = next;
                    _monitor = currentMonitor;
                    ResetCursorResources();
                }
                catch (CaptureException) { return null; }
            }
            if (Win32.FrameBounds(_window) is not { } bounds) return null;
            if (IntersectWindow(bounds, _shared.Desktop, Width, Height) is not { } rect) return null;
            crop = new RawRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        if (_shared.Latest(out var pointer) is not { } frame) return null;
        var texture = _cursor && pointer is { Visible: true, Shape: not null }
            ? CompositeCursor(frame.Texture, pointer.X, pointer.Y, pointer.Shape)
            : frame.Texture;
        return crop is { } c ? new Frame(texture, frame.Time, c, frame.Flip) : new Frame(texture, frame.Time, frame.Crop, frame.Flip);
    }

    private ID3D11Texture2D CompositeCursor(ID3D11Texture2D source, int cursorX, int cursorY, PointerShape shape)
    {
        var desc = source.Description;
        var w = (int)desc.Width;
        var h = (int)desc.Height;
        if (_cursorTarget is null || _cursorTarget.Description.Width != desc.Width || _cursorTarget.Description.Height != desc.Height || _cursorTarget.Description.Format != desc.Format)
        {
            _cursorTarget?.Dispose();
            _cursorTarget = _gpu.Texture(w, h, desc.Format);
        }
        _gpu.Context.CopyResource(_cursorTarget, source);

        var shapeH = shape.Type == PointerMonochrome ? shape.Height / 2 : shape.Height;
        if (shape.Width <= 0 || shapeH <= 0) return _cursorTarget;

        var left = Math.Max(0, cursorX);
        var top = Math.Max(0, cursorY);
        var right = Math.Min(w, cursorX + shape.Width);
        var bottom = Math.Min(h, cursorY + shapeH);
        var pw = right - left;
        var ph = bottom - top;
        if (pw <= 0 || ph <= 0) return _cursorTarget;

        var hdr = desc.Format == Format.R16G16B16A16_Float;
        if (_patchStaging is null || _patchW < pw || _patchH < ph || _patchHdr != hdr)
        {
            _patchStaging?.Dispose();
            _patchW = Math.Max(pw, 128);
            _patchH = Math.Max(ph, 128);
            _patchHdr = hdr;
            _patchStaging = _gpu.Device.CreateTexture2D(new Texture2DDescription(
                desc.Format, (uint)_patchW, (uint)_patchH, 1, 1, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Read | CpuAccessFlags.Write, 1, 0, ResourceOptionFlags.None));
        }

        _gpu.Context.CopySubresourceRegion(_patchStaging, 0, 0, 0, 0, source, 0, new Box(left, top, 0, right, bottom, 1));
        var mapped = _gpu.Context.Map(_patchStaging, 0, MapMode.ReadWrite, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = new byte[mapped.RowPitch * ph];
            Marshal.Copy(mapped.DataPointer, bytes, 0, bytes.Length);
            BlendCursorPatch(bytes, left, top, pw, ph, (int)mapped.RowPitch, hdr, cursorX, cursorY, shape.Width, shapeH, shape.Pitch, shape.Type, shape.Data);
            Marshal.Copy(bytes, 0, mapped.DataPointer, bytes.Length);
        }
        finally { _gpu.Context.Unmap(_patchStaging, 0); }

        _gpu.Context.CopySubresourceRegion(_cursorTarget, 0, (uint)left, (uint)top, 0, _patchStaging, 0, new Box(0, 0, 0, pw, ph, 1));
        return _cursorTarget;
    }

    internal static void BlendCursorPatch(
        byte[] patch, int patchX, int patchY, int patchW, int patchH, int rowPitch, bool hdr,
        int cursorX, int cursorY, int cursorW, int cursorH, int cursorPitch, int shapeType, ReadOnlySpan<byte> shape)
    {
        var halfs = hdr ? MemoryMarshal.Cast<byte, Half>(patch.AsSpan()) : Span<Half>.Empty;
        for (var py = 0; py < patchH; py++)
        {
            var sy = patchY + py - cursorY;
            if ((uint)sy >= (uint)cursorH) continue;
            var rowOffset = py * rowPitch;
            for (var px = 0; px < patchW; px++)
            {
                var sx = patchX + px - cursorX;
                if ((uint)sx >= (uint)cursorW) continue;
                if (shapeType == PointerMonochrome)
                {
                    var andOff = sy * cursorPitch + (sx >> 3);
                    var xorOff = (sy + cursorH) * cursorPitch + (sx >> 3);
                    if (xorOff >= shape.Length) continue;
                    var mask = 0x80 >> (sx & 7);
                    var andBit = (shape[andOff] & mask) != 0;
                    var xorBit = (shape[xorOff] & mask) != 0;
                    if (andBit && !xorBit) continue;
                    if (hdr)
                    {
                        var hIdx = rowOffset / 2 + px * 4;
                        if (!andBit && !xorBit) { halfs[hIdx] = (Half)0f; halfs[hIdx + 1] = (Half)0f; halfs[hIdx + 2] = (Half)0f; }
                        else if (!andBit && xorBit) { halfs[hIdx] = (Half)1f; halfs[hIdx + 1] = (Half)1f; halfs[hIdx + 2] = (Half)1f; }
                        else { halfs[hIdx] = (Half)(1f - (float)halfs[hIdx]); halfs[hIdx + 1] = (Half)(1f - (float)halfs[hIdx + 1]); halfs[hIdx + 2] = (Half)(1f - (float)halfs[hIdx + 2]); }
                    }
                    else
                    {
                        var o = rowOffset + px * 4;
                        if (!andBit && !xorBit) { patch[o] = 0; patch[o + 1] = 0; patch[o + 2] = 0; patch[o + 3] = 255; }
                        else if (!andBit && xorBit) { patch[o] = 255; patch[o + 1] = 255; patch[o + 2] = 255; patch[o + 3] = 255; }
                        else { patch[o] = (byte)(255 - patch[o]); patch[o + 1] = (byte)(255 - patch[o + 1]); patch[o + 2] = (byte)(255 - patch[o + 2]); }
                    }
                }
                else if (shapeType is PointerColor or PointerMaskedColor)
                {
                    var cOff = sy * cursorPitch + sx * 4;
                    if (cOff + 3 >= shape.Length) continue;
                    var cb = shape[cOff]; var cg = shape[cOff + 1]; var cr = shape[cOff + 2]; var ca = shape[cOff + 3];
                    if (shapeType == PointerMaskedColor)
                    {
                        if (hdr)
                        {
                            var hIdx = rowOffset / 2 + px * 4;
                            if (ca != 0)
                            {
                                var dr = (byte)Math.Clamp((float)halfs[hIdx] * 255f, 0, 255);
                                var dg = (byte)Math.Clamp((float)halfs[hIdx + 1] * 255f, 0, 255);
                                var db = (byte)Math.Clamp((float)halfs[hIdx + 2] * 255f, 0, 255);
                                halfs[hIdx] = (Half)((dr ^ cr) / 255f);
                                halfs[hIdx + 1] = (Half)((dg ^ cg) / 255f);
                                halfs[hIdx + 2] = (Half)((db ^ cb) / 255f);
                            }
                            else { halfs[hIdx] = (Half)(cr / 255f); halfs[hIdx + 1] = (Half)(cg / 255f); halfs[hIdx + 2] = (Half)(cb / 255f); }
                        }
                        else
                        {
                            var o = rowOffset + px * 4;
                            if (ca != 0) { patch[o] ^= cb; patch[o + 1] ^= cg; patch[o + 2] ^= cr; }
                            else { patch[o] = cb; patch[o + 1] = cg; patch[o + 2] = cr; }
                            patch[o + 3] = 255;
                        }
                    }
                    else if (ca != 0)
                    {
                        if (hdr)
                        {
                            var a = ca / 255f;
                            var hIdx = rowOffset / 2 + px * 4;
                            halfs[hIdx] = (Half)((float)halfs[hIdx] * (1f - a) + cr / 255f * a);
                            halfs[hIdx + 1] = (Half)((float)halfs[hIdx + 1] * (1f - a) + cg / 255f * a);
                            halfs[hIdx + 2] = (Half)((float)halfs[hIdx + 2] * (1f - a) + cb / 255f * a);
                        }
                        else
                        {
                            var o = rowOffset + px * 4;
                            var inv = 255 - ca;
                            patch[o] = (byte)((patch[o] * inv + cb * ca) / 255);
                            patch[o + 1] = (byte)((patch[o + 1] * inv + cg * ca) / 255);
                            patch[o + 2] = (byte)((patch[o + 2] * inv + cr * ca) / 255);
                            patch[o + 3] = 255;
                        }
                    }
                }
            }
        }
    }

    private void ResetCursorResources()
    {
        _cursorTarget?.Dispose();
        _cursorTarget = null;
        _patchStaging?.Dispose();
        _patchStaging = null;
    }

    /// <summary>One picture of the whole monitor now (the snip's frozen screen).</summary>
    internal ID3D11Texture2D? Grab(int timeoutMs = 500)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (_shared.Latest(out _) is { } frame) return frame.Texture;
            Thread.Sleep(10);
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetCursorResources();
        _shared.Release();
    }
}

internal sealed record PointerShape(int Width, int Height, int Pitch, int Type, byte[] Data);
internal readonly record struct PointerState(bool Visible, int X, int Y, PointerShape? Shape);

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
    private bool _pointerVisible;
    private int _pointerX, _pointerY;
    private PointerShape? _pointerShape;
    private byte[] _pointerScratch = [];

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
        // Always open desktop-native HDR on HDR monitors so SDR Go Live and HDR Replay/Snip share one duplication without format poisoning.
        var desktopHdr = description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
        Hdr = desktopHdr || (_wantHdr && desktopHdr);
        Format[] formats = Hdr ? [Format.R16G16B16A16_Float] : [Format.B8G8R8A8_UNorm];
        try { _duplication = Duplicate(output, formats); }
        catch (SharpGen.Runtime.SharpGenException) when (Hdr)
        {
            Hdr = false;
            try { _duplication = Duplicate(output, [Format.B8G8R8A8_UNorm]); }
            catch (SharpGen.Runtime.SharpGenException)
            {
                try { _duplication = output.DuplicateOutput(_gpu.Device); }
                catch (SharpGen.Runtime.SharpGenException ex) { throw new CaptureException($"capture the screen (0x{ex.HResult:X8})"); }
            }
        }
        catch (SharpGen.Runtime.SharpGenException) when (!Hdr)
        {
            // The older call, for a process Windows does not count as per-monitor aware.
            try { _duplication = output.DuplicateOutput(_gpu.Device); }
            catch (SharpGen.Runtime.SharpGenException ex) { throw new CaptureException($"capture the screen (0x{ex.HResult:X8})"); }
        }
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

    internal Frame? Latest(out PointerState pointer)
    {
        lock (_gate)
        {
            try
            {
                if (_duplication is null) Reopen();
                var hr = _duplication!.AcquireNextFrame(0, out var info, out var resource);
                if (hr.Success)
                {
                    try
                    {
                        if (resource is not null)
                        {
                            using (resource)
                            {
                                if (info.LastPresentTime != 0 || _copy is null)
                                {
                                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                                    var desc = texture.Description;
                                    if (_copy is null || _copy.Description.Width != desc.Width || _copy.Description.Height != desc.Height || _copy.Description.Format != desc.Format)
                                    {
                                        _copy?.Dispose();
                                        _copy = _gpu.Texture((int)desc.Width, (int)desc.Height, desc.Format);
                                    }
                                    _gpu.Context.CopyResource(_copy, texture);
                                    _time = Win32.Now();
                                    HadFrame = true;
                                }
                            }
                        }
                        if (info.LastMouseUpdateTime != 0)
                        {
                            _pointerVisible = info.PointerPosition.Visible;
                            _pointerX = info.PointerPosition.Position.X;
                            _pointerY = info.PointerPosition.Position.Y;
                        }
                        if (info.PointerShapeBufferSize > 0)
                            UpdatePointerShape(info.PointerShapeBufferSize);
                    }
                    finally { _duplication.ReleaseFrame(); }
                }
                else if (hr.Code == unchecked((int)0x887A0026)) Reopen(); // DXGI_ERROR_ACCESS_LOST: a mode change or a full-screen switch
            }
            catch (SharpGen.Runtime.SharpGenException) { _duplication?.Dispose(); _duplication = null; }
            catch (CaptureException) { _duplication = null; }
            pointer = new PointerState(_pointerVisible, _pointerX, _pointerY, _pointerShape);
            return _copy is null ? null : new Frame(_copy, _time, null, false);
        }
    }

    private unsafe void UpdatePointerShape(uint bufferSize)
    {
        var len = checked((int)bufferSize);
        if (_pointerScratch.Length < len) _pointerScratch = new byte[len];
        fixed (byte* ptr = _pointerScratch)
        {
            _duplication!.GetFramePointerShape(bufferSize, (nint)ptr, out var required, out var shapeInfo);
            var req = Math.Min((int)required, len);
            var copy = new byte[req];
            Buffer.BlockCopy(_pointerScratch, 0, copy, 0, req);
            _pointerShape = new PointerShape((int)shapeInfo.Width, (int)shapeInfo.Height, (int)shapeInfo.Pitch, (int)shapeInfo.Type, copy);
        }
    }
}
