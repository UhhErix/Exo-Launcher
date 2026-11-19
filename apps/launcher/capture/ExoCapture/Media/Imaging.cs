using System.Runtime.InteropServices;
using System.Text;
using ExoCapture.Native;
using Vortice.WIC;

namespace ExoCapture.Media;

/// <summary>
/// Pictures: PNG for snips (24-bit when opaque), JPEG for thumbnails and Go Live's pictures, JPEG XR for the HDR copy
/// of an HDR snip (Windows' own WIC codecs), the HDR-to-SDR tone map, and the clipboard.
/// </summary>
internal static class Imaging
{
    private static readonly Lazy<IWICImagingFactory> Factory = new(() => new IWICImagingFactory());

    internal static void SavePng(string path, byte[] bgra, int width, int height) =>
        Save(path, ContainerFormat.Png, bgra, width, height, PixelFormat.Format32bppBGRA, Opaque(bgra) ? PixelFormat.Format24bppBGR : PixelFormat.Format32bppBGRA, 4);

    internal static void SaveJpeg(string path, byte[] bgra, int width, int height, float quality = 0.85f) =>
        Save(path, ContainerFormat.Jpeg, bgra, width, height, PixelFormat.Format32bppBGRA, PixelFormat.Format24bppBGR, 4, quality);

    internal static byte[] Jpeg(byte[] bgra, int width, int height, float quality = 0.8f)
    {
        using var memory = new MemoryStream();
        Encode(memory, ContainerFormat.Jpeg, bgra, width, height, PixelFormat.Format32bppBGRA, PixelFormat.Format24bppBGR, 4, quality);
        return memory.ToArray();
    }

    /// <summary>The HDR copy: scRGB half floats (RGBA) as JPEG XR, which keeps every value above SDR white.</summary>
    internal static void SaveJxr(string path, byte[] rgbaHalf, int width, int height) =>
        Save(path, ContainerFormat.Wmp, rgbaHalf, width, height, PixelFormat.Format64bppRGBAHalf, PixelFormat.Format64bppRGBAHalf, 8);

    private static void Save(string path, ContainerFormat format, byte[] pixels, int width, int height, Guid input, Guid output, int bpp, float? quality = null)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite))
            Encode(file, format, pixels, width, height, input, output, bpp, quality);
        File.Move(temp, path, overwrite: true);
    }

    private static void Encode(Stream stream, ContainerFormat format, byte[] pixels, int width, int height, Guid input, Guid output, int bpp, float? quality)
    {
        var wic = Factory.Value;
        using var encoder = wic.CreateEncoder(format, stream, BitmapEncoderCacheOption.NoCache);
        using var frame = encoder.CreateNewFrame(out var options);
        if (quality is float q && options is not null)
            options.Set("ImageQuality", q);
        frame.Initialize(options);
        options?.Dispose();
        frame.SetSize((uint)width, (uint)height);
        var chosen = output;
        frame.SetPixelFormat(ref chosen);
        using var bitmap = wic.CreateBitmapFromMemory((uint)width, (uint)height, input, pixels, (uint)(width * bpp));
        if (chosen == input) frame.WriteSource(bitmap);
        else
        {
            using var converter = wic.CreateFormatConverter();
            converter.Initialize(bitmap, chosen, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            frame.WriteSource(converter);
        }
        frame.Commit();
        encoder.Commit();
    }

    /// <summary>A picture file read back as BGRA.</summary>
    internal static (byte[] Pixels, int Width, int Height) Load(string path)
    {
        var wic = Factory.Value;
        using var decoder = wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        using var converter = wic.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        var size = converter.Size;
        var pixels = new byte[size.Width * size.Height * 4];
        converter.CopyPixels((uint)(size.Width * 4), pixels);
        return (pixels, size.Width, size.Height);
    }

    internal static bool Opaque(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4) if (bgra[i] != 255) return false;
        return true;
    }

    /// <summary>A part of a picture (any bytes per pixel).</summary>
    internal static byte[] Crop(byte[] pixels, int width, int bpp, int x, int y, int w, int h)
    {
        var output = new byte[w * h * bpp];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(pixels, ((y + row) * width + x) * bpp, output, row * w * bpp, w * bpp);
        return output;
    }

    /// <summary>A smaller copy (box filter), for thumbnails and Go Live's pictures.</summary>
    internal static (byte[] Pixels, int Width, int Height) Shrink(byte[] bgra, int width, int height, int maxWidth)
    {
        if (width <= maxWidth) return (bgra, width, height);
        var w = maxWidth;
        var h = Math.Max(1, (int)Math.Round(height * (double)maxWidth / width));
        var output = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var y0 = y * height / h;
            var y1 = Math.Max(y0 + 1, (y + 1) * height / h);
            for (var x = 0; x < w; x++)
            {
                var x0 = x * width / w;
                var x1 = Math.Max(x0 + 1, (x + 1) * width / w);
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (var yy = y0; yy < y1; yy += Math.Max(1, (y1 - y0) / 4))
                    for (var xx = x0; xx < x1; xx += Math.Max(1, (x1 - x0) / 4))
                    {
                        var i = (yy * width + xx) * 4;
                        b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; a += bgra[i + 3]; n++;
                    }
                var o = (y * w + x) * 4;
                output[o] = (byte)(b / n); output[o + 1] = (byte)(g / n); output[o + 2] = (byte)(r / n); output[o + 3] = (byte)(a / n);
            }
        }
        return (output, w, h);
    }

    // ---------------------------------------------------------------- HDR

    /// <summary>
    /// An scRGB picture (linear BT.709, 1.0 = 80 nits, half floats RGBA) to sRGB BGRA for an SDR file: SDR white
    /// (Windows' own setting for that screen) maps to white, and anything brighter rolls off smoothly up to the screen's
    /// peak instead of clipping, on the brightest channel so colours keep their hue.
    /// </summary>
    internal static byte[] ToneMap(byte[] rgbaHalf, int width, int height, float sdrWhiteNits, float peakNits)
    {
        var count = width * height;
        var output = new byte[count * 4];
        var halfs = MemoryMarshal.Cast<byte, Half>(rgbaHalf.AsSpan());
        var white = Math.Max(80f, sdrWhiteNits) / 80f;
        var peak = Math.Max(1.01f, Math.Max(peakNits, sdrWhiteNits) / Math.Max(80f, sdrWhiteNits));
        for (var i = 0; i < count; i++)
        {
            float r = (float)halfs[i * 4] / white, g = (float)halfs[i * 4 + 1] / white, b = (float)halfs[i * 4 + 2] / white;
            r = Math.Max(0, r); g = Math.Max(0, g); b = Math.Max(0, b);
            var max = Math.Max(r, Math.Max(g, b));
            if (max > ShoulderStart)
            {
                var mapped = Shoulder(max, peak);
                var scale = mapped / max;
                r *= scale; g *= scale; b *= scale;
            }
            output[i * 4] = Srgb(b);
            output[i * 4 + 1] = Srgb(g);
            output[i * 4 + 2] = Srgb(r);
            output[i * 4 + 3] = 255;
        }
        return output;
    }

    internal const float ShoulderStart = 0.92f;

    /// <summary>
    /// The roll-off: the identity below <see cref="ShoulderStart"/>, then a soft shoulder blended with a gentle highlight
    /// ramp scaled to reach exactly 1 at the peak (and 1 past it), so SDR white stays white, brighter stays brighter,
    /// highlights above white stay distinct, and nothing clips past 1.
    /// </summary>
    internal static float Shoulder(float x, float peak)
    {
        if (x <= ShoulderStart) return x;
        if (x >= peak) return 1f;
        var head = 1 - ShoulderStart;
        var length = peak - ShoulderStart;
        float Raw(float t) => 0.44f * (1 - MathF.Exp(-t / (head * 0.42f))) + 0.56f * (t / length);
        return ShoulderStart + head * Raw(x - ShoulderStart) / Raw(length);
    }

    internal static byte Srgb(float linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        var v = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
    }

    /// <summary>Windows' SDR white level for a monitor (Settings › HDR › SDR content brightness), in nits; 80 when it can't say.</summary>
    internal static unsafe float SdrWhiteNits(nint monitor)
    {
        var device = Win32.MonitorDevice(monitor);
        uint paths, modes;
        if (GetDisplayConfigBufferSizes(2 /* QDC_ONLY_ACTIVE_PATHS */, out paths, out modes) != 0) return 80;
        var pathArray = new byte[paths * 72];
        var modeArray = new byte[modes * 64];
        fixed (byte* p = pathArray) fixed (byte* m = modeArray)
        {
            if (QueryDisplayConfig(2, ref paths, p, ref modes, m, 0) != 0) return 80;
            var source = stackalloc byte[84];
            var white = stackalloc byte[24];
            for (var i = 0; i < paths; i++)
            {
                new Span<byte>(source, 84).Clear();
                new Span<byte>(white, 24).Clear();
                var path = p + i * 72;
                // The source: adapter LUID (8), id (4); its GDI name tells which monitor it is.
                *(int*)source = 1; // GET_SOURCE_NAME
                *(int*)(source + 4) = 84;
                *(long*)(source + 8) = *(long*)path;
                *(uint*)(source + 16) = *(uint*)(path + 8);
                if (DisplayConfigGetDeviceInfo(source) != 0) continue;
                var name = new string((char*)(source + 20)).TrimEnd('\0');
                if (!string.Equals(name, device, StringComparison.OrdinalIgnoreCase)) continue;
                *(int*)white = 11; // GET_SDR_WHITE_LEVEL
                *(int*)(white + 4) = 24;
                *(long*)(white + 8) = *(long*)(path + 20); // the target's adapter
                *(uint*)(white + 16) = *(uint*)(path + 28); // the target's id
                if (DisplayConfigGetDeviceInfo(white) == 0) return *(uint*)(white + 20) / 1000f * 80f;
            }
        }
        return 80;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern unsafe int QueryDisplayConfig(uint flags, ref uint paths, byte* pathArray, ref uint modes, byte* modeArray, nint topology);
    [DllImport("user32.dll")] private static extern unsafe int DisplayConfigGetDeviceInfo(byte* packet);

    // ---------------------------------------------------------------- clipboard

    private static readonly uint PngFormat = Win32.RegisterClipboardFormat("PNG");
    private static readonly uint DropEffect = Win32.RegisterClipboardFormat("Preferred DropEffect");

    /// <summary>A picture on the clipboard, as a DIB (every app) and as the PNG file's bytes (apps that keep transparency).</summary>
    internal static unsafe void CopyImage(string path)
    {
        var (pixels, width, height) = Load(path);
        var png = File.ReadAllBytes(path);
        var header = sizeof(Win32.BITMAPINFOHEADER);
        var dib = Global(header + pixels.Length, data =>
        {
            var info = (Win32.BITMAPINFOHEADER*)data;
            *info = new Win32.BITMAPINFOHEADER { Size = (uint)header, Width = width, Height = height, Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length };
            var rows = (byte*)data + header;
            for (var y = 0; y < height; y++) // bottom-up, as a DIB is by default
                Marshal.Copy(pixels, (height - 1 - y) * width * 4, (nint)(rows + y * width * 4), width * 4);
        });
        var pngMemory = Global(png.Length, data => Marshal.Copy(png, 0, data, png.Length));
        SetClipboard((Win32.CF_DIB, dib), (PngFormat, pngMemory));
    }

    /// <summary>A file on the clipboard, to paste in Explorer, Discord or a chat.</summary>
    internal static unsafe void CopyFile(string path)
    {
        var list = Encoding.Unicode.GetBytes(Path.GetFullPath(path) + "\0\0");
        var drop = Global(20 + list.Length, data =>
        {
            *(uint*)data = 20; // pFiles
            *(int*)((byte*)data + 16) = 1; // fWide
            Marshal.Copy(list, 0, data + 20, list.Length);
        });
        var effect = Global(4, data => *(uint*)data = 1); // DROPEFFECT_COPY
        SetClipboard((Win32.CF_HDROP, drop), (DropEffect, effect));
    }

    private static nint Global(int size, Action<nint> fill)
    {
        var memory = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (nuint)size);
        var data = Win32.GlobalLock(memory);
        fill(data);
        Win32.GlobalUnlock(memory);
        return memory;
    }

    private static void SetClipboard(params (uint Format, nint Memory)[] items)
    {
        // Another app can hold the clipboard for a moment.
        for (var attempt = 0; attempt < 20 && !Win32.OpenClipboard(0); attempt++) Thread.Sleep(25);
        try
        {
            Win32.EmptyClipboard();
            foreach (var (format, memory) in items)
                if (Win32.SetClipboardData(format, memory) == 0) Win32.GlobalFree(memory);
        }
        finally { Win32.CloseClipboard(); }
    }
}
