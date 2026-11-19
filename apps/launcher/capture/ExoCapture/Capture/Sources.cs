using System.Runtime.InteropServices;
using ExoCapture.Gpu;
using ExoCapture.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ExoCapture.Capture;

/// <summary>The newest picture a source has: valid until the next <see cref="IFrameSource.Latest"/> call.</summary>
internal readonly record struct Frame(ID3D11Texture2D Texture, long Time, RawRect? Crop, bool Flip);

/// <summary>Where pictures come from: Windows capture of a window or a screen, the screen's own copy (desktop duplication), or a game's own frames (OBS's hook).</summary>
internal interface IFrameSource : IDisposable
{
    /// <summary>How the clip says it was taken: (game window), (the game's screen), (from inside the game), (window), (screen).</summary>
    string Kind { get; }
    int Width { get; }
    int Height { get; }
    bool Hdr { get; }
    /// <summary>Why it stopped (the window closed, the game closed), or null while it runs.</summary>
    string? Ended { get; }
    /// <summary>Whether a picture has ever come.</summary>
    bool HadFrame { get; }
    Frame? Latest();
}

/// <summary>
/// Windows.Graphics.Capture of a window or a monitor, through its ABI (no WinRT projection, so Exo Capture stays one
/// small native exe). The frame pool is polled on the clip's own clock, never waited on, and only the newest frame is
/// kept: a game faster than the clip loses no time to it.
/// </summary>
internal sealed unsafe class WgcSource : IFrameSource
{
    private static readonly Guid IID_ItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_Item = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_PoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid IID_Session2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid IID_Session3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static readonly Guid IID_Session5 = new("67C0EA62-1F85-5061-925A-239BE0AC09CB");
    private static readonly Guid IID_Session6 = new("D7419236-BE20-5E9F-BCD6-C4E98FD6AFDC");
    private static readonly Guid IID_Closable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid IID_DxgiAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private const int FormatBgra = 87, FormatFp16 = 10;

    private readonly nint _item, _pool, _session, _device;
    private readonly nint _window;
    private nint _frame;
    private ID3D11Texture2D? _texture;
    private int _poolW, _poolH;
    private readonly int _format;
    private long _lastTime;

    public string Kind { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool Hdr { get; }
    public string? Ended { get; private set; }
    public bool HadFrame { get; private set; }

    private WgcSource(GpuDevice gpu, nint item, nint window, bool hdr, bool cursor, string kind, TimeSpan minInterval)
    {
        _item = item;
        _window = window;
        Kind = kind;
        Hdr = hdr;
        _format = hdr ? FormatFp16 : FormatBgra;
        using var dxgi = gpu.Device.QueryInterface<IDXGIDevice>();
        Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out _device), "capture device");
        SizeInt32 size;
        Check(Vt<nint, nint, int>(item, 7)(item, (nint)(&size)), "capture size");
        (Width, Height, _poolW, _poolH) = (size.Width, size.Height, size.Width, size.Height);
        var statics = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", IID_PoolStatics2);
        try
        {
            nint pool;
            Check(Vt<nint, nint, int, int, SizeInt32, nint, int>(statics, 6)(statics, _device, _format, 2, size, (nint)(&pool)), "frame pool");
            _pool = pool;
        }
        finally { Marshal.Release(statics); }
        nint session;
        Check(Vt<nint, nint, nint, int>(_pool, 10)(_pool, item, (nint)(&session)), "capture session");
        _session = session;
        SetBool(_session, IID_Session2, 7, cursor);
        SetBool(_session, IID_Session3, 7, false); // no yellow border
        SetBool(_session, IID_Session6, 7, true);  // a game's child windows too
        if (Marshal.QueryInterface(_session, IID_Session5, out var s5) == 0)
        {
            Vt<nint, long, int>(s5, 7)(s5, minInterval.Ticks);
            Marshal.Release(s5);
        }
        Check(Vt<nint, int>(_session, 6)(_session), kind == "(screen)" || kind == "(the game's screen)" ? "capture the screen" : "capture the window");
    }

    internal static WgcSource ForWindow(GpuDevice gpu, nint hwnd, bool hdr, bool cursor, string kind, TimeSpan minInterval)
    {
        var interop = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", IID_ItemInterop);
        try
        {
            nint item;
            var iid = IID_Item;
            Check(Vt<nint, nint, nint, nint, int>(interop, 3)(interop, hwnd, (nint)(&iid), (nint)(&item)), "capture the window");
            return new WgcSource(gpu, item, hwnd, hdr, cursor, kind, minInterval);
        }
        finally { Marshal.Release(interop); }
    }

    internal static WgcSource ForMonitor(GpuDevice gpu, nint monitor, bool hdr, bool cursor, string kind, TimeSpan minInterval)
    {
        var interop = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", IID_ItemInterop);
        try
        {
            nint item;
            var iid = IID_Item;
            Check(Vt<nint, nint, nint, nint, int>(interop, 4)(interop, monitor, (nint)(&iid), (nint)(&item)), "capture the screen");
            return new WgcSource(gpu, item, 0, hdr, cursor, kind, minInterval);
        }
        finally { Marshal.Release(interop); }
    }

    public Frame? Latest()
    {
        if (Ended is not null) return null;
        if (_window != 0 && !Win32.IsWindow(_window)) { Ended = "the window closed"; return null; }
        nint newest = 0;
        while (true)
        {
            nint next;
            if (Vt<nint, nint, int>(_pool, 7)(_pool, (nint)(&next)) != 0 || next == 0) break;
            if (newest != 0) Close(newest);
            newest = next;
        }
        if (newest != 0)
        {
            SizeInt32 content;
            Vt<nint, nint, int>(newest, 8)(newest, (nint)(&content));
            long time;
            Vt<nint, nint, int>(newest, 7)(newest, (nint)(&time));
            nint surface;
            if (Vt<nint, nint, int>(newest, 6)(newest, (nint)(&surface)) == 0)
            {
                nint access;
                var hr = Marshal.QueryInterface(surface, IID_DxgiAccess, out access);
                Marshal.Release(surface);
                if (hr == 0)
                {
                    var iid = typeof(ID3D11Texture2D).GUID;
                    nint texture;
                    hr = Vt<nint, nint, nint, int>(access, 3)(access, (nint)(&iid), (nint)(&texture));
                    Marshal.Release(access);
                    if (hr == 0)
                    {
                        if (_frame != 0) Close(_frame);
                        _texture?.Dispose();
                        _frame = newest;
                        _texture = new ID3D11Texture2D(texture);
                        _lastTime = time;
                        HadFrame = true;
                        newest = 0;
                        if (content.Width > 0 && content.Height > 0 && (content.Width != _poolW || content.Height != _poolH))
                        {
                            // The window changed size: the pool follows from the next frame.
                            (_poolW, _poolH) = (content.Width, content.Height);
                            Vt<nint, nint, int, int, SizeInt32, int>(_pool, 6)(_pool, _device, _format, 2, content);
                        }
                        (Width, Height) = (Math.Max(2, content.Width), Math.Max(2, content.Height));
                    }
                }
            }
            if (newest != 0) Close(newest);
        }
        if (_texture is null) return null;
        var desc = _texture.Description;
        var crop = Width < desc.Width || Height < desc.Height ? new RawRect(0, 0, Math.Min(Width, (int)desc.Width), Math.Min(Height, (int)desc.Height)) : (RawRect?)null;
        return new Frame(_texture, _lastTime, crop, false);
    }

    private static void Close(nint winrtObject)
    {
        if (Marshal.QueryInterface(winrtObject, IID_Closable, out var closable) == 0)
        {
            Vt<nint, int>(closable, 6)(closable);
            Marshal.Release(closable);
        }
        Marshal.Release(winrtObject);
    }

    public void Dispose()
    {
        if (_frame != 0) Close(_frame);
        _frame = 0;
        _texture?.Dispose();
        if (_session != 0) Close(_session);
        if (_pool != 0) Close(_pool);
        if (_item != 0) Marshal.Release(_item);
        if (_device != 0) Close(_device);
    }

    // ---------------------------------------------------------------- the ABI

    private static delegate* unmanaged[Stdcall]<T1, TR> Vt<T1, TR>(nint obj, int slot) => (delegate* unmanaged[Stdcall]<T1, TR>)(*(void***)obj)[slot];
    private static delegate* unmanaged[Stdcall]<T1, T2, TR> Vt<T1, T2, TR>(nint obj, int slot) => (delegate* unmanaged[Stdcall]<T1, T2, TR>)(*(void***)obj)[slot];
    private static delegate* unmanaged[Stdcall]<T1, T2, T3, TR> Vt<T1, T2, T3, TR>(nint obj, int slot) => (delegate* unmanaged[Stdcall]<T1, T2, T3, TR>)(*(void***)obj)[slot];
    private static delegate* unmanaged[Stdcall]<T1, T2, T3, T4, TR> Vt<T1, T2, T3, T4, TR>(nint obj, int slot) => (delegate* unmanaged[Stdcall]<T1, T2, T3, T4, TR>)(*(void***)obj)[slot];
    private static delegate* unmanaged[Stdcall]<T1, T2, T3, T4, T5, TR> Vt<T1, T2, T3, T4, T5, TR>(nint obj, int slot) => (delegate* unmanaged[Stdcall]<T1, T2, T3, T4, T5, TR>)(*(void***)obj)[slot];
    private static delegate* unmanaged[Stdcall]<T1, T2, T3, T4, T5, T6, TR> Vt<T1, T2, T3, T4, T5, T6, TR>(nint obj, int slot) => (delegate* unmanaged[Stdcall]<T1, T2, T3, T4, T5, T6, TR>)(*(void***)obj)[slot];

    private static void SetBool(nint obj, Guid iid, int slot, bool value)
    {
        if (Marshal.QueryInterface(obj, iid, out var face) != 0) return;
        Vt<nint, byte, int>(face, slot)(face, value ? (byte)1 : (byte)0);
        Marshal.Release(face);
    }

    private static nint Factory(string runtimeClass, Guid iid)
    {
        nint hstring;
        fixed (char* name = runtimeClass) Check(WindowsCreateString(name, (uint)runtimeClass.Length, &hstring), "Windows capture");
        try
        {
            nint factory;
            Check(RoGetActivationFactory(hstring, &iid, &factory), "Windows capture");
            return factory;
        }
        finally { WindowsDeleteString(hstring); }
    }

    internal static void Check(int hr, string step)
    {
        if (hr < 0) throw new CaptureException($"{step} (0x{hr:X8})");
    }

    /// <summary>Windows capture can run here: its service is on and the API exists.</summary>
    internal static bool Available(out string? why)
    {
        why = null;
        try
        {
            var statics = Factory("Windows.Graphics.Capture.GraphicsCaptureSession", new Guid("2224A540-5974-49AA-B232-0882536F4CB5"));
            try
            {
                byte supported;
                Vt<nint, nint, int>(statics, 6)(statics, (nint)(&supported));
                if (supported == 0) why = "Windows capture is not supported here";
            }
            finally { Marshal.Release(statics); }
        }
        catch (CaptureException ex) { why = ex.Message; }
        return why is null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SizeInt32 { public int Width, Height; }

    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
    [DllImport("combase.dll")] private static extern int WindowsCreateString(char* source, uint length, nint* hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
}
