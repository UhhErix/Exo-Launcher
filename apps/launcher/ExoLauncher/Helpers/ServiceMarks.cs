using Windows.UI;

namespace ExoLauncher.Helpers;

/// <summary>
/// The services Exo opens a window for (a sign-in, or a page's own pop-out), as that window's title bar and taskbar
/// icon show them: the service's name and its Simple Icons mark (simpleicons.org 16.36.0, CC0 1.0: the path on its
/// 24-unit box and the brand color) or, where Simple Icons has no mark, the name alone. Xbox's left Simple Icons at
/// Microsoft's request, so its window carries its name. Each opens at its sign-in page's size (DIPs, under the title
/// band). Riot, EA, Ubisoft, Battle.net and Amazon sign in in their own apps' windows and Discord by a code in Exo's
/// page, so none has a window of Exo's.
/// </summary>
internal static class ServiceMarks
{
    internal sealed record Service(string Name, string? Mark, Color Brand, int Width, int Height);

    internal static readonly IReadOnlyDictionary<string, Service> All = new Dictionary<string, Service>(StringComparer.Ordinal)
    {
        // Google's sign-in is a 450px column.
        ["youtube"] = new("YouTube Music",
            "M12 0C5.376 0 0 5.376 0 12s5.376 12 12 12 12-5.376 12-12S18.624 0 12 0zm0 19.104c-3.924 0-7.104-3.18-7.104-7.104S8.076 4.896 12 4.896s7.104 3.18 7.104 7.104-3.18 7.104-7.104 7.104zm0-13.332c-3.432 0-6.228 2.796-6.228 6.228S8.568 18.228 12 18.228s6.228-2.796 6.228-6.228S15.432 5.772 12 5.772zM9.684 15.54V8.46L15.816 12l-6.132 3.54z", Rgb(0xFF0000), 450, 640),
        ["apple"] = new("Apple Music",
            "M23.994 6.124a9.23 9.23 0 00-.24-2.19c-.317-1.31-1.062-2.31-2.18-3.043a5.022 5.022 0 00-1.877-.726 10.496 10.496 0 00-1.564-.15c-.04-.003-.083-.01-.124-.013H5.986c-.152.01-.303.017-.455.026-.747.043-1.49.123-2.193.4-1.336.53-2.3 1.452-2.865 2.78-.192.448-.292.925-.363 1.408-.056.392-.088.785-.1 1.18 0 .032-.007.062-.01.093v12.223c.01.14.017.283.027.424.05.815.154 1.624.497 2.373.65 1.42 1.738 2.353 3.234 2.801.42.127.856.187 1.293.228.555.053 1.11.06 1.667.06h11.03a12.5 12.5 0 001.57-.1c.822-.106 1.596-.35 2.295-.81a5.046 5.046 0 001.88-2.207c.186-.42.293-.87.37-1.324.113-.675.138-1.358.137-2.04-.002-3.8 0-7.595-.003-11.393zm-6.423 3.99v5.712c0 .417-.058.827-.244 1.206-.29.59-.76.962-1.388 1.14-.35.1-.706.157-1.07.173-.95.045-1.773-.6-1.943-1.536a1.88 1.88 0 011.038-2.022c.323-.16.67-.25 1.018-.324.378-.082.758-.153 1.134-.24.274-.063.457-.23.51-.516a.904.904 0 00.02-.193c0-1.815 0-3.63-.002-5.443a.725.725 0 00-.026-.185c-.04-.15-.15-.243-.304-.234-.16.01-.318.035-.475.066-.76.15-1.52.303-2.28.456l-2.325.47-1.374.278c-.016.003-.032.01-.048.013-.277.077-.377.203-.39.49-.002.042 0 .086 0 .13-.002 2.602 0 5.204-.003 7.805 0 .42-.047.836-.215 1.227-.278.64-.77 1.04-1.434 1.233-.35.1-.71.16-1.075.172-.96.036-1.755-.6-1.92-1.544-.14-.812.23-1.685 1.154-2.075.357-.15.73-.232 1.108-.31.287-.06.575-.116.86-.177.383-.083.583-.323.6-.714v-.15c0-2.96 0-5.922.002-8.882 0-.123.013-.25.042-.37.07-.285.273-.448.546-.518.255-.066.515-.112.774-.165.733-.15 1.466-.296 2.2-.444l2.27-.46c.67-.134 1.34-.27 2.01-.403.22-.043.442-.088.663-.106.31-.025.523.17.554.482.008.073.012.148.012.223.002 1.91.002 3.822 0 5.732z", Rgb(0xFA243C), 480, 660),
        // Spotify's Log in column, for its web player.
        ["spotify"] = new("Spotify",
            "M12 0C5.4 0 0 5.4 0 12s5.4 12 12 12 12-5.4 12-12S18.66 0 12 0zm5.521 17.34c-.24.359-.66.48-1.021.24-2.82-1.74-6.36-2.101-10.561-1.141-.418.122-.779-.179-.899-.539-.12-.421.18-.78.54-.9 4.56-1.021 8.52-.6 11.64 1.32.42.18.479.659.301 1.02zm1.44-3.3c-.301.42-.841.6-1.262.3-3.239-1.98-8.159-2.58-11.939-1.38-.479.12-1.02-.12-1.14-.6-.12-.48.12-1.021.6-1.141C9.6 9.9 15 10.561 18.72 12.84c.361.181.54.78.241 1.2zm.12-3.36C15.24 8.4 8.82 8.16 5.16 9.301c-.6.179-1.2-.181-1.38-.721-.18-.601.18-1.2.72-1.381 4.26-1.26 11.28-1.02 15.721 1.621.539.3.719 1.02.419 1.56-.299.421-1.02.599-1.559.3z", Rgb(0x1ED760), 480, 760),
        // The store's own login page, its header and menus with it.
        ["steam"] = new("Steam",
            "M11.979 0C5.678 0 .511 4.86.022 11.037l6.432 2.658c.545-.371 1.203-.59 1.912-.59.063 0 .125.004.188.006l2.861-4.142V8.91c0-2.495 2.028-4.524 4.524-4.524 2.494 0 4.524 2.031 4.524 4.527s-2.03 4.525-4.524 4.525h-.105l-4.076 2.911c0 .052.004.105.004.159 0 1.875-1.515 3.396-3.39 3.396-1.635 0-3.016-1.173-3.331-2.727L.436 15.27C1.862 20.307 6.486 24 11.979 24c6.627 0 11.999-5.373 11.999-12S18.605 0 11.979 0zM7.54 18.21l-1.473-.61c.262.543.714.999 1.314 1.25 1.297.539 2.793-.076 3.332-1.375.263-.63.264-1.319.005-1.949s-.75-1.121-1.377-1.383c-.624-.26-1.29-.249-1.878-.03l1.523.63c.956.4 1.409 1.5 1.009 2.455-.397.957-1.497 1.41-2.454 1.012H7.54zm11.415-9.303c0-1.662-1.353-3.015-3.015-3.015-1.665 0-3.015 1.353-3.015 3.015 0 1.665 1.35 3.015 3.015 3.015 1.663 0 3.015-1.35 3.015-3.015zm-5.273-.005c0-1.252 1.013-2.266 2.265-2.266 1.249 0 2.266 1.014 2.266 2.266 0 1.251-1.017 2.265-2.266 2.265-1.253 0-2.265-1.014-2.265-2.265z", Rgb(0x000000), 1000, 760),
        // Room for hCaptcha's challenge beside the form.
        ["epic"] = new("Epic Games",
            "M3.537 0C2.165 0 1.66.506 1.66 1.879V18.44a4.262 4.262 0 00.02.433c.031.3.037.59.316.92.027.033.311.245.311.245.153.075.258.13.43.2l8.335 3.491c.433.199.614.276.928.27h.002c.314.006.495-.071.928-.27l8.335-3.492c.172-.07.277-.124.43-.2 0 0 .284-.211.311-.243.28-.33.285-.621.316-.92a4.261 4.261 0 00.02-.434V1.879c0-1.373-.506-1.88-1.878-1.88zm13.366 3.11h.68c1.138 0 1.688.553 1.688 1.696v1.88h-1.374v-1.8c0-.369-.17-.54-.523-.54h-.235c-.367 0-.537.17-.537.539v5.81c0 .369.17.54.537.54h.262c.353 0 .523-.171.523-.54V8.619h1.373v2.143c0 1.144-.562 1.71-1.7 1.71h-.694c-1.138 0-1.7-.566-1.7-1.71V4.82c0-1.144.562-1.709 1.7-1.709zm-12.186.08h3.114v1.274H6.117v2.603h1.648v1.275H6.117v2.774h1.74v1.275h-3.14zm3.816 0h2.198c1.138 0 1.7.564 1.7 1.708v2.445c0 1.144-.562 1.71-1.7 1.71h-.799v3.338h-1.4zm4.53 0h1.4v9.201h-1.4zm-3.13 1.235v3.392h.575c.354 0 .523-.171.523-.54V4.965c0-.368-.17-.54-.523-.54zm-3.74 10.147a1.708 1.708 0 01.591.108 1.745 1.745 0 01.49.299l-.452.546a1.247 1.247 0 00-.308-.195.91.91 0 00-.363-.068.658.658 0 00-.28.06.703.703 0 00-.224.163.783.783 0 00-.151.243.799.799 0 00-.056.299v.008a.852.852 0 00.056.31.7.7 0 00.157.245.736.736 0 00.238.16.774.774 0 00.303.058.79.79 0 00.445-.116v-.339h-.548v-.565H7.37v1.255a2.019 2.019 0 01-.524.307 1.789 1.789 0 01-.683.123 1.642 1.642 0 01-.602-.107 1.46 1.46 0 01-.478-.3 1.371 1.371 0 01-.318-.455 1.438 1.438 0 01-.115-.58v-.008a1.426 1.426 0 01.113-.57 1.449 1.449 0 01.312-.46 1.418 1.418 0 01.474-.309 1.58 1.58 0 01.598-.111 1.708 1.708 0 01.045 0zm11.963.008a2.006 2.006 0 01.612.094 1.61 1.61 0 01.507.277l-.386.546a1.562 1.562 0 00-.39-.205 1.178 1.178 0 00-.388-.07.347.347 0 00-.208.052.154.154 0 00-.07.127v.008a.158.158 0 00.022.084.198.198 0 00.076.066.831.831 0 00.147.06c.062.02.14.04.236.061a3.389 3.389 0 01.43.122 1.292 1.292 0 01.328.17.678.678 0 01.207.24.739.739 0 01.071.337v.008a.865.865 0 01-.081.382.82.82 0 01-.229.285 1.032 1.032 0 01-.353.18 1.606 1.606 0 01-.46.061 2.16 2.16 0 01-.71-.116 1.718 1.718 0 01-.593-.346l.43-.514c.277.223.578.335.9.335a.457.457 0 00.236-.05.157.157 0 00.082-.142v-.008a.15.15 0 00-.02-.077.204.204 0 00-.073-.066.753.753 0 00-.143-.062 2.45 2.45 0 00-.233-.062 5.036 5.036 0 01-.413-.113 1.26 1.26 0 01-.331-.16.72.72 0 01-.222-.243.73.73 0 01-.082-.36v-.008a.863.863 0 01.074-.359.794.794 0 01.214-.283 1.007 1.007 0 01.34-.185 1.423 1.423 0 01.448-.066 2.006 2.006 0 01.025 0zm-9.358.025h.742l1.183 2.81h-.825l-.203-.499H8.623l-.198.498h-.81zm2.197.02h.814l.663 1.08.663-1.08h.814v2.79h-.766v-1.602l-.711 1.091h-.016l-.707-1.083v1.593h-.754zm3.469 0h2.235v.658h-1.473v.422h1.334v.61h-1.334v.442h1.493v.658h-2.255zm-5.3.897l-.315.793h.624zm-1.145 5.19h8.014l-4.09 1.348z", Rgb(0x313131), 600, 760),
        // GOG Galaxy's own sign-in layout.
        ["gog"] = new("GOG",
            "M7.15 15.24H4.36a.4.4 0 0 0-.4.4v2c0 .21.18.4.4.4h2.8v1.32h-3.5c-.56 0-1.02-.46-1.02-1.03v-3.39c0-.56.46-1.02 1.03-1.02h3.48v1.32zM8.16 11.54c0 .58-.47 1.05-1.05 1.05H2.63v-1.35h3.78a.4.4 0 0 0 .4-.4V6.39a.4.4 0 0 0-.4-.4H4.39a.4.4 0 0 0-.41.4v2.02c0 .23.18.4.4.4H6v1.35H3.68c-.58 0-1.05-.46-1.05-1.04V5.68c0-.57.47-1.04 1.05-1.04H7.1c.58 0 1.05.47 1.05 1.04v5.86zM21.36 19.36h-1.32v-4.12h-.93a.4.4 0 0 0-.4.4v3.72h-1.33v-4.12h-.93a.4.4 0 0 0-.4.4v3.72h-1.33v-4.42c0-.56.46-1.02 1.03-1.02h5.61v5.44zM21.37 11.54c0 .58-.47 1.05-1.05 1.05h-4.48v-1.35h3.78a.4.4 0 0 0 .4-.4V6.39a.4.4 0 0 0-.4-.4h-2.03a.4.4 0 0 0-.4.4v2.02c0 .23.18.4.4.4h1.62v1.35H16.9c-.58 0-1.05-.46-1.05-1.04V5.68c0-.57.47-1.04 1.05-1.04h3.43c.58 0 1.05.47 1.05 1.04v5.86zM13.72 4.64h-3.44c-.58 0-1.04.47-1.04 1.04v3.44c0 .58.46 1.04 1.04 1.04h3.44c.57 0 1.04-.46 1.04-1.04V5.68c0-.57-.47-1.04-1.04-1.04m-.3 1.75v2.02a.4.4 0 0 1-.4.4h-2.03a.4.4 0 0 1-.4-.4V6.4c0-.22.17-.4.4-.4H13c.23 0 .4.18.4.4zM12.63 13.92H9.24c-.57 0-1.03.46-1.03 1.02v3.39c0 .57.46 1.03 1.03 1.03h3.39c.57 0 1.03-.46 1.03-1.03v-3.39c0-.56-.46-1.02-1.03-1.02m-.3 1.72v2a.4.4 0 0 1-.4.4v-.01H9.94a.4.4 0 0 1-.4-.4v-1.99c0-.22.18-.4.4-.4h2c.22 0 .4.18.4.4zM23.49 1.1a1.74 1.74 0 0 0-1.24-.52H1.75A1.74 1.74 0 0 0 0 2.33v19.34a1.74 1.74 0 0 0 1.75 1.75h20.5A1.74 1.74 0 0 0 24 21.67V2.33c0-.48-.2-.92-.51-1.24m0 20.58a1.23 1.23 0 0 1-1.24 1.24H1.75A1.23 1.23 0 0 1 .5 21.67V2.33a1.23 1.23 0 0 1 1.24-1.24h20.5a1.24 1.24 0 0 1 1.24 1.24v19.34z", Rgb(0x86328A), 480, 680),
        // Microsoft's link page, then its account sign-in.
        ["xbox"] = new("Xbox", null, default, 520, 720),
    };

    internal static Service For(string service) =>
        All.TryGetValue(service, out var found) ? found : throw new ArgumentOutOfRangeException(nameof(service), service, "No window chrome for this service.");

    /// The mark's color on <paramref name="page"/>: its brand color where that holds 3:1 there (WCAG's least for a graphic
    /// you must make out), else the ink. Steam, Epic and GOG are dark on Void, so they take the ink.
    internal static Color Fill(Service service, Color page, Color ink) => Contrast(service.Brand, page) >= 3 ? service.Brand : ink;

    internal static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static Color Rgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ── The taskbar icon ──────────────────────────────────────────────────────────────────────────────────────────

    /// The sizes Windows asks a window's icon for, 100% to 200% (title bar and Alt+Tab 16 to 32, taskbar 24 to 48).
    internal static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64];

    // Exo's own icon at small sizes (tools/make-logo.py, PLATE_SMALL): a black plate, graphite at its top.
    private static readonly Color PlateTop = Color.FromArgb(255, 26, 26, 29);
    private static readonly Color PlateFoot = Color.FromArgb(255, 6, 6, 7);
    private static readonly Color Ink = Color.FromArgb(255, 255, 255, 255);

    /// The service's taskbar icon file (Exo's data folder, marks\), written when missing or out of date; null for a
    /// service shown by its name alone, whose window keeps Exo's icon.
    internal static string? IconFile(string service)
    {
        if (Icon(For(service)) is not { } bytes) return null;
        var folder = Path.Combine(PathHelper.AppDataDir, "marks");
        var file = Path.Combine(folder, service + ".ico");
        try
        {
            if (File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes)) return file;
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(file, bytes);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Window icon for " + service + " not written: " + ex.GetType().Name);
            return null;
        }
    }

    /// The mark on Exo's plate, as Exo's own icon sits in the taskbar, in an .ico of every size in
    /// <see cref="IconSizes"/> (32-bit, straight alpha); null for a service without a mark.
    internal static byte[]? Icon(Service service)
    {
        if (service.Mark is null) return null;
        var figures = SvgPath.Parse(service.Mark);
        var fill = Fill(service, PlateFoot, Ink);
        var images = IconSizes.Select(size => Image(figures, fill, size)).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Length);
        var offset = 6 + 16 * images.Length;
        for (var n = 0; n < images.Length; n++)
        {
            var size = IconSizes[n];
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[n].Length);
            writer.Write(offset);
            offset += images[n].Length;
        }
        foreach (var image in images) writer.Write(image);
        writer.Flush();
        return stream.ToArray();
    }

    // One icon image: a BITMAPINFOHEADER, the pixels bottom-up in BGRA, then an empty AND mask.
    private static byte[] Image(List<SvgPath.Figure> figures, Color fill, int size)
    {
        var plate = Coverage(RoundedSquare(size, size * 0.225), size);
        // The mark in the plate's middle 64%, as Exo's bars fill its own.
        var markSize = size * 0.64;
        var mark = Coverage(SvgPath.Outlines(figures, markSize / 24, (size - markSize) / 2, (size - markSize) / 2), size);
        var maskRow = (size + 31) / 32 * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(size * size * 4 + maskRow * size);
        writer.Write(0L);
        writer.Write(0L);
        for (var y = size - 1; y >= 0; y--)
        {
            var t = size == 1 ? 0 : (double)y / (size - 1);
            for (var x = 0; x < size; x++)
            {
                var a = Math.Min(1, plate[y * size + x]);
                var m = Math.Min(a, mark[y * size + x]);
                byte Mix(byte top, byte foot, byte ink)
                {
                    var under = top + (foot - top) * t;
                    return (byte)Math.Round(under + (ink - under) * (a > 0 ? m / a : 0));
                }
                writer.Write(Mix(PlateTop.B, PlateFoot.B, fill.B));
                writer.Write(Mix(PlateTop.G, PlateFoot.G, fill.G));
                writer.Write(Mix(PlateTop.R, PlateFoot.R, fill.R));
                writer.Write((byte)Math.Round(a * 255));
            }
        }
        writer.Write(new byte[maskRow * size]);
        writer.Flush();
        return stream.ToArray();
    }

    private static List<List<SvgPath.Pt>> RoundedSquare(double size, double radius)
    {
        var points = new List<SvgPath.Pt>();
        (double X, double Y, double From)[] corners = [(size - radius, radius, -90), (size - radius, size - radius, 0), (radius, size - radius, 90), (radius, radius, 180)];
        foreach (var (cx, cy, from) in corners)
            for (var step = 0; step <= 12; step++)
            {
                var angle = (from + step * 7.5) * Math.PI / 180;
                points.Add(new SvgPath.Pt(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }
        return [points];
    }

    /// How much of each pixel the outlines cover under the nonzero rule (SVG's default fill rule): sixteen lines a row,
    /// each span's ends measured to the fraction of a pixel.
    internal static float[] Coverage(List<List<SvgPath.Pt>> outlines, int size)
    {
        const int lines = 16;
        var cover = new float[size * size];
        var edges = new List<(double X0, double Y0, double X1, double Y1, int Winding)>();
        foreach (var outline in outlines)
            for (var n = 0; n < outline.Count; n++)
            {
                var (a, b) = (outline[n], outline[(n + 1) % outline.Count]);
                if (a.Y == b.Y) continue;
                edges.Add(a.Y < b.Y ? (a.X, a.Y, b.X, b.Y, 1) : (b.X, b.Y, a.X, a.Y, -1));
            }
        var crossings = new List<(double X, int Winding)>();
        for (var row = 0; row < size; row++)
            for (var line = 0; line < lines; line++)
            {
                var y = row + (line + 0.5) / lines;
                crossings.Clear();
                foreach (var e in edges)
                    if (y >= e.Y0 && y < e.Y1) crossings.Add((e.X0 + (y - e.Y0) * (e.X1 - e.X0) / (e.Y1 - e.Y0), e.Winding));
                crossings.Sort((p, q) => p.X.CompareTo(q.X));
                var winding = 0;
                var from = 0.0;
                foreach (var (x, w) in crossings)
                {
                    var before = winding;
                    winding += w;
                    if (before == 0 && winding != 0) from = x;
                    else if (before != 0 && winding == 0) Span(cover, row * size, size, from, x, 1f / lines);
                }
            }
        return cover;
    }

    private static void Span(float[] cover, int rowStart, int size, double from, double to, float weight)
    {
        from = Math.Clamp(from, 0, size);
        to = Math.Clamp(to, 0, size);
        for (var px = (int)Math.Floor(from); px < Math.Ceiling(to) && px < size; px++)
            cover[rowStart + px] += weight * (float)(Math.Min(to, px + 1) - Math.Max(from, px));
    }
}
