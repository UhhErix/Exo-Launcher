using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ExoLauncher.Services;

/// <summary>
/// Copy text from a snip: Windows' own on-device text recognition (Windows.Media.Ocr) over a picture Exo Capture already
/// saved, or the part of it the player dragged over. It reads a file in the captures folder, never the screen, and
/// nothing leaves the PC. The languages are the ones Windows has text recognition for among the player's own.
/// </summary>
internal static class CaptureText
{
    internal readonly record struct Area(uint X, uint Y, uint Width, uint Height);

    /// <summary>
    /// The part to read, in the picture's own pixels, kept inside it; null (the whole picture) when none was asked. One
    /// that is all outside, or too small to hold a letter, is nothing to read.
    /// </summary>
    internal static Area? Clamp(double? x, double? y, double? width, double? height, uint imageWidth, uint imageHeight, out bool empty)
    {
        empty = false;
        if (x is null || y is null || width is null || height is null) return null;
        if (!double.IsFinite(x.Value) || !double.IsFinite(y.Value) || !double.IsFinite(width.Value) || !double.IsFinite(height.Value))
        {
            empty = true;
            return null;
        }
        var left = Math.Clamp(Math.Floor(Math.Min(x.Value, x.Value + width.Value)), 0, imageWidth);
        var top = Math.Clamp(Math.Floor(Math.Min(y.Value, y.Value + height.Value)), 0, imageHeight);
        var right = Math.Clamp(Math.Ceiling(Math.Max(x.Value, x.Value + width.Value)), 0, imageWidth);
        var bottom = Math.Clamp(Math.Ceiling(Math.Max(y.Value, y.Value + height.Value)), 0, imageHeight);
        if (right - left < 4 || bottom - top < 4)
        {
            empty = true;
            return null;
        }
        return new Area((uint)left, (uint)top, (uint)(right - left), (uint)(bottom - top));
    }

    /// <summary>Windows reads pictures up to its own size: a larger part is scaled down to fit it, never up.</summary>
    internal static double Scale(uint width, uint height, uint max) =>
        width == 0 || height == 0 || max == 0 ? 1 : Math.Min(1, max / (double)Math.Max(width, height));

    /// <summary>The lines read, top to bottom, each trimmed; blank ones dropped.</summary>
    internal static string Join(IEnumerable<string?> lines) =>
        string.Join("\n", lines.Select(line => line?.Trim()).Where(line => !string.IsNullOrEmpty(line)));

    internal const string NoLanguage = "Windows has no text recognition for your languages. Add one in Windows Settings > Time & language > Language.";
    internal const string NothingFound = "No text found there.";

    /// <summary>The text in the picture (or the part asked), or why there is none.</summary>
    internal static async Task<(string? Text, string? Error)> ReadAsync(string path, double? x, double? y, double? width, double? height)
    {
        if (OcrEngine.TryCreateFromUserProfileLanguages() is not { } engine) return (null, NoLanguage);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var area = Clamp(x, y, width, height, decoder.PixelWidth, decoder.PixelHeight, out var empty)
            ?? (empty ? (Area?)null : new Area(0, 0, decoder.PixelWidth, decoder.PixelHeight));
        if (area is not { } part) return (null, NothingFound);
        // BitmapTransform scales the whole picture first, then cuts: the part is asked in the scaled picture's pixels.
        var scale = Scale(part.Width, part.Height, OcrEngine.MaxImageDimension);
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var bounds = new BitmapBounds
        {
            X = (uint)Math.Floor(part.X * scale),
            Y = (uint)Math.Floor(part.Y * scale),
            Width = Math.Max(1, (uint)Math.Floor(part.Width * scale)),
            Height = Math.Max(1, (uint)Math.Floor(part.Height * scale)),
        };
        bounds.Width = Math.Min(bounds.Width, transform.ScaledWidth - bounds.X);
        bounds.Height = Math.Min(bounds.Height, transform.ScaledHeight - bounds.Y);
        transform.Bounds = bounds;
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        var text = Join(result.Lines.Select(line => line.Text));
        return text.Length == 0 ? (null, NothingFound) : (text, null);
    }
}
