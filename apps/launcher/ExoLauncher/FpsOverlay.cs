using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace ExoLauncher;

/// <summary>
/// The FPS counter over the game: one small pill off the top of the game's screen ("142 FPS"), in its corner or its
/// middle: Goeist tabular figures over Exo's Liquid Glass material. Drawn via DirectComposition
/// (<see cref="Windows.UI.Composition"/> Host Backdrop blur + saturation + dark legibility tint, diagonal Fresnel rim,
/// outer hairline separation, rounded pill geometric clip, and a DirectWrite Goeist drawing surface) with a safe GDI
/// <see cref="UpdateLayeredWindow"/> fallback when backdrop composition is unavailable or High Contrast is enabled.
/// Never takes focus or clicks, excludes itself from captures (<c>WDA_EXCLUDEFROMCAPTURE</c>), and re-asserts
/// <c>HWND_TOPMOST</c> if a game rises above it in Z-order.
/// </summary>
internal sealed unsafe class FpsOverlay : IDisposable
{
    /// <param name="Items">What follows "142 FPS", each part after a " · " (<see cref="Services.FpsItems.Text"/>).</param>
    internal readonly record struct Line(int Fps, string? Items);

    /// <summary>What to show, and when to look again: 0 is when the front changes or something says so.</summary>
    internal readonly record struct Look(Line? Line, uint NextMs);

    /// <summary>Pure spec of the Liquid Glass material so tests can verify blur, saturation, tint and Fresnel stops.</summary>
    internal readonly record struct GlassSpec(
        float BlurRadius,
        float Saturation,
        uint TintArgb,
        uint OuterHairlineArgb,
        float[] FresnelStops,
        byte[] FresnelAlphas)
    {
        internal static GlassSpec LiquidGlass { get; } = new(
            BlurRadius: 11.0f,
            Saturation: 1.7f,
            TintArgb: 0x73000000u,         // argb(115, 0, 0, 0) dark legibility tint
            OuterHairlineArgb: 0x52000000u,// outer hairline dark separation
            FresnelStops: [0.00f, 0.35f, 0.68f, 1.00f],
            FresnelAlphas: [77, 26, 10, 36]); // 30% -> 10% -> 4% -> 14%
    }

    private const uint RefreshMessage = 0x8001, EventSystemForeground = 0x0003, WmThemeChanged = 0x031A;
    private const uint WsExLayered = 0x00080000, WsExNoRedirectionBitmap = 0x00200000;
    private const uint WsExTransparent = 0x00000020, WsExTopmost = 0x00000008, WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;
    private const uint SpiGetHighContrast = 0x0042, HcfHighContrastOn = 0x0001;
    private const uint GwHwndPrev = 3, SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
    private const int GwlExStyle = -20;
    private const int Padding = 10, Height = 24, Margin = 8, FontPixels = 13, ForegroundOutOfContext = 0;
    // GDI fallback plate at 78% black + edge highlight.
    private const byte CapsuleAlpha = 199, NumberInk = 255, RestInk = 191;
    private const double EdgeTop = 0.17, EdgeFoot = 0.03;
    private const uint TextQuality = 4; // ANTIALIASED_QUALITY: gray, hinted to the pixel grid, so stems stay sharp at any scale.
    private const string ClassName = "ExoFpsCounter", SemiBoldFace = "Goeist SemiBold", RegularFace = "Goeist", Separator = " · ";

    private static WndProc? _proc;
    private static WinEventProc? _frontChanged;
    private static FpsOverlay? _current;
    private static int _fontAdded;
    // The fonts of the last scale drawn, on the one thread that draws.
    [ThreadStatic] private static (int Pixels, IntPtr Number, IntPtr Regular) t_fonts;

    private readonly Func<Look> _read;
    private readonly Func<string> _position;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hwnd, _hook;
    private bool _shown, _failed, _usingGlass;
    private GlassHost? _glass;
    private Line? _drawn;
    private double _drawnScale;
    private string _drawnPosition = "";
    private IntPtr _drawnMonitor;
    private int _width;
    private uint _timerMs;

    /// <summary>Starts its thread at once; <paramref name="read"/> and <paramref name="position"/> run on that thread and mustn't wait on the caller.</summary>
    internal FpsOverlay(Func<Look> read, Func<string> position)
    {
        _read = read;
        _position = position;
        _thread = new Thread(Run, 256 * 1024) { IsBackground = true, Name = "Exo FPS counter" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Looks again now rather than at the next tick (the position changed, a preview began, the game's processes changed).</summary>
    internal void Refresh()
    {
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, RefreshMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    // ---------------------------------------------------------------- pure

    /// <summary>One of the three, whatever a settings file held: "right" unless it says otherwise.</summary>
    internal static string PositionOf(string? value) => value is "left" or "middle" ? value : "right";

    /// <summary>Where the capsule goes, in the screen's own pixels: in off the top by <paramref name="margin"/>, and in its corner in off the side by as much (owner, 2026-11-10: a pill of its own, not a tab stuck to the screen's edge).</summary>
    internal static (int X, int Y) Place(string? position, int left, int top, int right, int width, int margin = 0) => (PositionOf(position) switch
    {
        "left" => left + margin,
        "middle" => left + (right - left - width) / 2,
        _ => right - width - margin,
    }, top + margin);

    /// <summary>The gap between the capsule and the screen's edge at a scale.</summary>
    internal static int MarginAt(double scale) => (int)Math.Round(Margin * scale);

    /// <summary>
    /// How much of a pixel the capsule covers, 0 to 1: a pill, round at both ends, the same wherever it sits.
    /// </summary>
    internal static double Cover(int width, int height, int column, int row)
    {
        var radius = height / 2.0;
        var (px, py) = (column + 0.5, row + 0.5);
        var dx = px < radius ? radius - px : px > width - radius ? px - (width - radius) : 0;
        if (dx == 0) return 1;
        var dy = py - radius;
        return Math.Clamp(radius - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
    }

    /// <summary>The narrowest the capsule may be: it never shrinks while the line keeps its parts, so a number shrinking can't make it jitter; different parts start over.</summary>
    internal static int MinWidth(int width, Line? before, Line now) => before is { } was && SameParts(was.Items, now.Items) ? width : 0;

    /// <summary>The same words in the same places, whatever their numbers say.</summary>
    internal static bool SameParts(string? a, string? b)
    {
        int i = 0, j = 0;
        var (one, two) = (a ?? "", b ?? "");
        while (true)
        {
            while (i < one.Length && IsFigure(one[i])) i++;
            while (j < two.Length && IsFigure(two[j])) j++;
            if (i == one.Length || j == two.Length) return i == one.Length && j == two.Length;
            if (one[i++] != two[j++]) return false;
        }
    }

    private static bool IsFigure(char c) => char.IsAsciiDigit(c) || c == '.';

    /// <summary>The items that fit: from the end they go, whole, so the line never runs off the screen.</summary>
    internal static string? Fit(string? items, Func<string?, int> widthOf, int maxWidth)
    {
        while (items is not null && widthOf(items) > maxWidth)
        {
            var cut = items.LastIndexOf(Separator, StringComparison.Ordinal);
            items = cut > 0 ? items[..cut] : null;
        }
        return items;
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>The private font, from the app's own files (tests name the checkout's).</summary>
    internal static void AddFont(string? path = null)
    {
        if (Interlocked.Exchange(ref _fontAdded, 1) == 1) return;
        path ??= Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Goeist-Variable.ttf");
        if (AddFontResourceExW(path, 0x10 /* FR_PRIVATE */, IntPtr.Zero) == 0) AppLog.Warn("FPS counter: Goeist didn't load, so Windows picks a font.");
    }

    /// <summary>The capsule: a 32-bit top-down DIB of premultiplied pixels, selected into its own DC.</summary>
    internal sealed class Capsule : IDisposable
    {
        internal IntPtr Dc { get; private set; }
        private readonly IntPtr _bitmap, _before;
        internal uint* Bits { get; private set; }
        internal int Width { get; }
        internal int Height { get; }

        internal Capsule(int width, int height)
        {
            (Width, Height) = (width, height);
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            Dc = CreateCompatibleDC(IntPtr.Zero);
            _bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (Dc == IntPtr.Zero || _bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("The counter's bitmap wasn't made.");
            Bits = (uint*)bits;
            _before = SelectObject(Dc, _bitmap);
        }

        public void Dispose()
        {
            if (Dc == IntPtr.Zero) return;
            SelectObject(Dc, _before);
            DeleteObject(_bitmap);
            DeleteDC(Dc);
            Dc = IntPtr.Zero;
            Bits = null;
        }
    }

    /// <summary>
    /// Draws the line at a screen's scale, at least <paramref name="minWidth"/> wide and never past <paramref name="maxWidth"/>.
    /// The text is drawn in its ink on black, so a pixel's value is the white it adds; then it goes over the black plate,
    /// shaped for where it sits (<see cref="Cover"/>), premultiplied as a layered window needs. Every digit sits in an
    /// equal cell: GDI can't ask for tabular figures.
    /// </summary>
    internal static Capsule Draw(Line line, double scale, int minWidth = 0, int maxWidth = int.MaxValue)
    {
        AddFont();
        var pixels = Math.Max(1, (int)Math.Round(FontPixels * scale));
        if (t_fonts.Pixels != pixels)
        {
            if (t_fonts.Pixels != 0) { DeleteObject(t_fonts.Number); DeleteObject(t_fonts.Regular); }
            t_fonts = (pixels, CreateFontW(-pixels, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, TextQuality, 0, SemiBoldFace),
                CreateFontW(-pixels, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, TextQuality, 0, RegularFace));
        }
        var (number, rest) = (t_fonts.Number, t_fonts.Regular);
        var digits = line.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int numberCell, restCell, numberWidth, textWidth;
        TextMetric metrics;
        string tail;
        var padding = (int)Math.Round(Padding * scale);
        using (var measure = new Capsule(1, 1))
        {
            var dc = measure.Dc;
            SelectObject(dc, number);
            numberCell = FigureCell(dc);
            GetTextMetricsW(dc, out metrics);
            numberWidth = numberCell * digits.Length;
            SelectObject(dc, rest);
            var cell = restCell = FigureCell(dc);
            tail = " FPS" + Fit(line.Items, items => padding * 2 + numberWidth + Run(dc, " FPS" + items, cell, 0, 0, false), maxWidth);
            textWidth = numberWidth + Run(dc, tail, restCell, 0, 0, false);
        }
        var capsule = new Capsule(Math.Min(maxWidth, Math.Max(minWidth, padding * 2 + textWidth)), (int)Math.Round(Height * scale));
        var (x, y) = ((capsule.Width - textWidth) / 2, (capsule.Height - (metrics.Ascent + metrics.Descent)) / 2);
        SetBkMode(capsule.Dc, 1 /* TRANSPARENT */);
        SelectObject(capsule.Dc, number);
        SetTextColor(capsule.Dc, Gray(NumberInk));
        Run(capsule.Dc, digits, numberCell, x, y, true);
        SelectObject(capsule.Dc, rest);
        SetTextColor(capsule.Dc, Gray(RestInk));
        Run(capsule.Dc, tail, restCell, x + numberWidth, y, true);
        GdiFlush();
        Lay(capsule);
        return capsule;
    }

    private static int FigureCell(IntPtr dc)
    {
        var cell = 0;
        for (var digit = '0'; digit <= '9'; digit++) cell = Math.Max(cell, TextWidth(dc, digit.ToString()));
        return cell;
    }

    /// <summary>A run of text from x, its digits in equal cells and its words as they are; its width.</summary>
    private static int Run(IntPtr dc, string text, int cell, int x, int y, bool draw)
    {
        var start = x;
        for (var i = 0; i < text.Length;)
        {
            if (char.IsAsciiDigit(text[i]))
            {
                if (draw)
                {
                    var one = text[i].ToString();
                    TextOutW(dc, x + (cell - TextWidth(dc, one)) / 2, y, one, 1);
                }
                x += cell;
                i++;
                continue;
            }
            var end = i;
            while (end < text.Length && !char.IsAsciiDigit(text[end])) end++;
            var words = text[i..end];
            if (draw) TextOutW(dc, x, y, words, words.Length);
            x += TextWidth(dc, words);
            i = end;
        }
        return x - start;
    }

    private static uint Gray(byte ink) => (uint)(ink | ink << 8 | ink << 16);

    /// <summary>The faces GDI selected for the number and the rest at a scale, as it reports them: Goeist's, unless Windows fell back.</summary>
    internal static (string Number, string Words) Faces(double scale = 1)
    {
        Draw(new(0, null), scale).Dispose();
        using var measure = new Capsule(1, 1);
        SelectObject(measure.Dc, t_fonts.Number);
        var number = Face(measure.Dc);
        SelectObject(measure.Dc, t_fonts.Regular);
        return (number, Face(measure.Dc));
    }

    private static string Face(IntPtr dc)
    {
        var name = new char[64];
        var length = GetTextFaceW(dc, name.Length, name);
        return length > 0 ? new string(name, 0, length - 1) : "";
    }

    private static int TextWidth(IntPtr dc, string text) => GetTextExtentPoint32W(dc, text, text.Length, out var size) ? size.Width : 0;

    /// <summary>
    /// How much of a pixel the capsule's edge covers, 0 to 1: its outermost pixel all the way round, the pill less the pill
    /// one pixel in (ui/src/tokens.css --exo-hair is one line, whatever the scale).
    /// </summary>
    internal static double Edge(int width, int height, int column, int row)
    {
        var inner = column >= 1 && row >= 1 && column < width - 1 && row < height - 1 ? Cover(width - 2, height - 2, column - 1, row - 1) : 0;
        return Math.Max(0, Cover(width, height, column, row) - inner);
    }

    /// <summary>
    /// The edge's white, 0 to 1, by row: Exo's Liquid Glass edge (tokens.css --glassy-ring), lit like a lens, brightest along
    /// the top (17%) and fading to the foot (3%), in place of a gray outline.
    /// </summary>
    internal static double EdgeLight(int height, int row) => height <= 1 ? EdgeTop : EdgeTop + (EdgeFoot - EdgeTop) * row / (height - 1.0);

    /// <summary>The text's pixels over the capsule: what the text added stays white, then the glass edge, then the black plate where neither is.</summary>
    private static void Lay(Capsule capsule)
    {
        for (var row = 0; row < capsule.Height; row++)
        {
            var light = EdgeLight(capsule.Height, row);
            for (var column = 0; column < capsule.Width; column++)
            {
                var at = capsule.Bits + row * capsule.Width + column;
                var ink = *at & 0xFF;
                var under = (uint)Math.Round(CapsuleAlpha * Cover(capsule.Width, capsule.Height, column, row));
                var edge = (uint)Math.Round(255 * light * Edge(capsule.Width, capsule.Height, column, row));
                var plate = edge + under * (255 - edge) / 255;
                var white = ink + edge * (255 - ink) / 255;
                var alpha = ink + plate * (255 - ink) / 255;
                *at = alpha << 24 | white << 16 | white << 8 | white;
            }
        }
    }

    /// <summary>True when Liquid Glass backdrop composition may be used (no Windows High Contrast and backdrop supported).</summary>
    internal static bool CanUseLiquidGlass(bool highContrast, bool backdropSupported) => !highContrast && backdropSupported;

    /// <summary>Whether the overlay window should be re-promoted above the foreground window.</summary>
    internal static bool NeedsTopmostReassert(IntPtr overlayHwnd, IntPtr foregroundHwnd)
    {
        if (overlayHwnd == IntPtr.Zero) return false;
        if ((GetWindowLongPtrW(overlayHwnd, GwlExStyle).ToInt64() & WsExTopmost) == 0) return true;
        if (foregroundHwnd == IntPtr.Zero) return false;
        var prev = overlayHwnd;
        for (var i = 0; i < 64; i++)
        {
            prev = GetWindow(prev, GwHwndPrev);
            if (prev == IntPtr.Zero) break;
            if (prev == foregroundHwnd) return true;
        }
        return false;
    }

    private static bool IsHighContrast()
    {
        var hc = new HighContrast { Size = Marshal.SizeOf<HighContrast>() };
        return SystemParametersInfoW(SpiGetHighContrast, (uint)hc.Size, ref hc, 0) && (hc.Flags & HcfHighContrastOn) != 0;
    }

    // ---------------------------------------------------------------- the window

    private void Run()
    {
        try
        {
            InitializeWindowAndGlass();
            _frontChanged ??= (_, _, _, _, _, _, _) =>
            {
                try { _current?.Tick(); }
                catch { }
            };
            _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _frontChanged, 0, 0, ForegroundOutOfContext);
            _ready.Set();
            Tick();
            while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0) DispatchMessageW(ref message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            AppLog.Warn("FPS counter: its window didn't open: " + ex.Message);
        }
        finally
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _glass?.Dispose();
            _glass = null;
            if (_current == this) _current = null;
            _hwnd = IntPtr.Zero;
            _ready.Set();
        }
    }

    private void InitializeWindowAndGlass()
    {
        _glass?.Dispose();
        _glass = null;
        _usingGlass = false;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            _shown = false;
            _drawn = null;
        }
        if (CanUseLiquidGlass(IsHighContrast(), backdropSupported: true))
        {
            MakeWindow(noRedirectionBitmap: true);
            _glass = GlassHost.TryCreate(_hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Goeist-Variable.ttf"));
            if (_glass is not null)
            {
                _usingGlass = true;
                return;
            }
            AppLog.Info("FPS counter: Host Backdrop Liquid Glass unavailable; falling back to layered capsule.");
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        MakeWindow(noRedirectionBitmap: false);
    }

    private void MakeWindow(bool noRedirectionBitmap)
    {
        var instance = GetModuleHandleW(null);
        if (_proc is null)
        {
            _proc = OnMessage;
            var type = new WndClassEx { Size = Marshal.SizeOf<WndClassEx>(), Proc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = instance, ClassName = ClassName };
            if (RegisterClassExW(ref type) == 0) throw new InvalidOperationException("The counter's window class wasn't registered.");
        }
        _current = this;
        var modeEx = noRedirectionBitmap ? WsExNoRedirectionBitmap : WsExLayered;
        var exStyle = modeEx | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate;
        _hwnd = CreateWindowExW(exStyle, ClassName, "Exo FPS counter", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("The counter's window wasn't made.");
        if (!SetWindowDisplayAffinity(_hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */)) AppLog.Info("FPS counter: Windows wouldn't keep it out of captures here.");
    }

    private static IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l)
    {
        try
        {
            switch (message)
            {
                case 0x0084: return new IntPtr(-1); // WM_NCHITTEST: HTTRANSPARENT
                case 0x0021: return new IntPtr(3); // WM_MOUSEACTIVATE: MA_NOACTIVATE
                case WmThemeChanged:
                    if (_current is { } overlay && overlay._usingGlass == IsHighContrast())
                    {
                        overlay.InitializeWindowAndGlass();
                        overlay.Tick();
                    }
                    return IntPtr.Zero;
                case 0x0010: DestroyWindow(hwnd); return IntPtr.Zero; // WM_CLOSE
                case 0x0002:
                    if (_current?._hwnd == hwnd) PostQuitMessage(0);
                    return IntPtr.Zero; // WM_DESTROY
                case 0x0113: case RefreshMessage: _current?.Tick(); return IntPtr.Zero; // WM_TIMER
                default: return DefWindowProcW(hwnd, message, w, l);
            }
        }
        catch
        {
            return DefWindowProcW(hwnd, message, w, l);
        }
    }

    /// <summary>A native callback's work: anything thrown here would end the process, so a failure hides the counter and says so once.</summary>
    private void Tick()
    {
        try
        {
            if (_usingGlass && IsHighContrast()) InitializeWindowAndGlass();
            var look = _read();
            if (look.NextMs != _timerMs)
            {
                _timerMs = look.NextMs;
                if (_timerMs == 0) KillTimer(_hwnd, 1);
                else SetTimer(_hwnd, 1, _timerMs, IntPtr.Zero);
            }
            if (look.Line is { } line) Show(line);
            else Hide();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_failed) AppLog.Warn($"FPS counter: {ex.GetType().Name}: {ex.Message}");
            _failed = true;
            Hide();
        }
    }

    private void Hide()
    {
        if (!_shown) return;
        ShowWindow(_hwnd, 0 /* SW_HIDE */);
        _shown = false;
    }

    private void EnsureTopmost(IntPtr foreground)
    {
        if (!NeedsTopmostReassert(_hwnd, foreground)) return;
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
    }

    private void Show(Line line)
    {
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground, 2 /* MONITOR_DEFAULTTONEAREST */);
        var position = _position();
        if (_shown && _drawn == line && position == _drawnPosition && monitor == _drawnMonitor)
        {
            EnsureTopmost(foreground);
            return;
        }
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) { Hide(); return; }
        var scale = GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        var margin = MarginAt(scale);
        var maxWidth = info.Monitor.Right - info.Monitor.Left - margin * 2;
        var minWidth = _drawnScale == scale ? MinWidth(_width, _drawn, line) : 0;

        if (_usingGlass && _glass is { } glass)
        {
            if (!glass.Render(line, scale, minWidth, maxWidth, out var width, out var height))
            {
                // Device reset or rendering failure: degrade seamlessly to GDI layered fallback.
                InitializeWindowAndGlass();
            }
            else
            {
                _width = width;
                var (x, y) = Place(position, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, width, margin);
                SetWindowPos(_hwnd, new IntPtr(-1), x, y, width, height, SwpNoActivate | SwpShowWindow);
                (_drawn, _drawnScale, _drawnPosition, _drawnMonitor) = (line, scale, position, monitor);
                _shown = true;
                return;
            }
        }

        // GDI layered fallback path (High Contrast or backdrop composition unavailable).
        using var capsule = Draw(line, scale, minWidth, maxWidth);
        _width = capsule.Width;
        var (fx, fy) = Place(position, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, capsule.Width, margin);
        var at = new Point { X = fx, Y = fy };
        var source = new Point();
        var size = new Size { Width = capsule.Width, Height = capsule.Height };
        var blend = new BlendFunction { Op = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        if (!UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref at, ref size, capsule.Dc, ref source, 0, ref blend, 2 /* ULW_ALPHA */))
            throw new InvalidOperationException($"UpdateLayeredWindow failed ({Marshal.GetLastPInvokeError()}).");
        (_drawn, _drawnScale, _drawnPosition, _drawnMonitor) = (line, scale, position, monitor);
        if (!_shown || NeedsTopmostReassert(_hwnd, foreground))
            SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
        _shown = true;
    }

    // ---------------------------------------------------------------- Liquid Glass Composition host

    private sealed class GlassHost : IDisposable
    {
        private const uint D3D11SdkVersion = 7, D3DDriverHardware = 1, D3D11CreateDeviceBgraSupport = 0x20;
        private const uint DWriteFactoryTypeShared = 0, TagTabularFigures = 0x6D756E74u;
        private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid IidD2D1Factory1 = new("bb12d362-daee-4b9a-aa1d-14ba401cfa1f");
        private static readonly Guid IidD2D1DeviceContext = new("e8f7fe7a-191c-466d-ad95-975678bda998");
        private static readonly Guid IidDWriteFactory5 = new("958DB99A-BE2A-4F09-AF7D-65189803D1D3");

        private readonly Compositor _compositor;
        private readonly DesktopWindowTarget _target;
        private readonly ContainerVisual _root;
        private readonly CompositionRoundedRectangleGeometry _clipGeo, _outerGeo, _rimGeo;
        private readonly SpriteVisual _backdropVisual, _tintVisual, _textVisual;
        private readonly ShapeVisual _rimVisual;
        private readonly CompositionGraphicsDevice _graphicsDevice;
        private readonly CompositionDrawingSurface _surface;
        private readonly CompositionSurfaceBrush _surfaceBrush;

        private IntPtr _d3dDevice, _d2dFactory, _d2dDevice, _dwriteFactory, _fontCollection, _typography;
        private int _renderedWidth, _renderedHeight;

        private GlassHost(
            Compositor compositor,
            DesktopWindowTarget target,
            ContainerVisual root,
            CompositionRoundedRectangleGeometry clipGeo,
            CompositionRoundedRectangleGeometry outerGeo,
            CompositionRoundedRectangleGeometry rimGeo,
            SpriteVisual backdropVisual,
            SpriteVisual tintVisual,
            ShapeVisual rimVisual,
            SpriteVisual textVisual,
            CompositionGraphicsDevice graphicsDevice,
            CompositionDrawingSurface surface,
            CompositionSurfaceBrush surfaceBrush,
            IntPtr d3dDevice,
            IntPtr d2dFactory,
            IntPtr d2dDevice,
            IntPtr dwriteFactory,
            IntPtr fontCollection,
            IntPtr typography)
        {
            _compositor = compositor;
            _target = target;
            _root = root;
            _clipGeo = clipGeo;
            _outerGeo = outerGeo;
            _rimGeo = rimGeo;
            _backdropVisual = backdropVisual;
            _tintVisual = tintVisual;
            _rimVisual = rimVisual;
            _textVisual = textVisual;
            _graphicsDevice = graphicsDevice;
            _surface = surface;
            _surfaceBrush = surfaceBrush;
            _d3dDevice = d3dDevice;
            _d2dFactory = d2dFactory;
            _d2dDevice = d2dDevice;
            _dwriteFactory = dwriteFactory;
            _fontCollection = fontCollection;
            _typography = typography;
        }

        internal static GlassHost? TryCreate(IntPtr hwnd, string fontPath)
        {
            IntPtr d3dDevice = IntPtr.Zero, dxgiDevice = IntPtr.Zero, d2dFactory = IntPtr.Zero, d2dDevice = IntPtr.Zero;
            IntPtr dwriteFactory = IntPtr.Zero, fontCollection = IntPtr.Zero, typography = IntPtr.Zero;
            try
            {
                EnsureDispatcherQueue();
                var compositor = new Compositor();
                var desktopInterop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(((IWinRTObject)compositor).NativeObject.ThisPtr);
                desktopInterop.CreateDesktopWindowTarget(hwnd, true, out var targetPtr);
                var target = DesktopWindowTarget.FromAbi(targetPtr);

                var spec = GlassSpec.LiquidGlass;
                var hostBackdrop = compositor.CreateHostBackdropBrush();
                using var saturation = new SaturationEffect
                {
                    Saturation = spec.Saturation,
                    Source = new CompositionEffectSourceParameter("backdrop"),
                };
                using var blur = new GaussianBlurEffect
                {
                    BlurAmount = spec.BlurRadius,
                    Optimization = EffectOptimization.Balanced,
                    BorderMode = EffectBorderMode.Hard,
                    Source = saturation,
                };
                using var effectFactory = compositor.CreateEffectFactory(blur);
                var backdropBrush = effectFactory.CreateBrush();
                backdropBrush.SetSourceParameter("backdrop", hostBackdrop);

                var d3dHr = D3D11CreateDevice(IntPtr.Zero, D3DDriverHardware, IntPtr.Zero, D3D11CreateDeviceBgraSupport, IntPtr.Zero, 0, D3D11SdkVersion, out d3dDevice, out _, out _);
                if (d3dHr < 0) return null;
                var iidDxgi = IidDxgiDevice;
                if (Marshal.QueryInterface(d3dDevice, in iidDxgi, out dxgiDevice) < 0) return null;
                var iidD2D = IidD2D1Factory1;
                if (D2D1CreateFactory(0 /* SINGLE_THREADED */, ref iidD2D, IntPtr.Zero, out d2dFactory) < 0) return null;
                if (CreateD2DDevice(d2dFactory, dxgiDevice, out d2dDevice) < 0) return null;

                var compInterop = (ICompositorInterop)Marshal.GetObjectForIUnknown(((IWinRTObject)compositor).NativeObject.ThisPtr);
                compInterop.CreateGraphicsDevice(Marshal.GetObjectForIUnknown(d2dDevice), out var gfxPtr);
                var graphicsDevice = CompositionGraphicsDevice.FromAbi(gfxPtr);
                var surface = graphicsDevice.CreateDrawingSurface(new Windows.Foundation.Size(64, Height), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
                var surfaceBrush = compositor.CreateSurfaceBrush(surface);
                surfaceBrush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;

                var iidDW = IidDWriteFactory5;
                if (DWriteCreateFactory(DWriteFactoryTypeShared, ref iidDW, out dwriteFactory) < 0) return null;
                fontCollection = TryLoadFontCollection(dwriteFactory, fontPath);
                if (CreateTypography(dwriteFactory, out typography) < 0) return null;
                AddFontFeature(typography, new FontFeature(TagTabularFigures, 1));

                var root = compositor.CreateContainerVisual();
                var clipGeo = compositor.CreateRoundedRectangleGeometry();
                root.Clip = compositor.CreateGeometricClip(clipGeo);
                target.Root = root;

                var backdropVisual = compositor.CreateSpriteVisual();
                backdropVisual.Brush = backdropBrush;

                var tintVisual = compositor.CreateSpriteVisual();
                tintVisual.Brush = compositor.CreateColorBrush(Argb(spec.TintArgb));

                var rimVisual = compositor.CreateShapeVisual();
                var outerGeo = compositor.CreateRoundedRectangleGeometry();
                var outerShape = compositor.CreateSpriteShape(outerGeo);
                outerShape.StrokeThickness = 1.0f;
                outerShape.StrokeBrush = compositor.CreateColorBrush(Argb(spec.OuterHairlineArgb));

                var rimGeo = compositor.CreateRoundedRectangleGeometry();
                var rimShape = compositor.CreateSpriteShape(rimGeo);
                rimShape.StrokeThickness = 1.0f;
                var fresnelBrush = compositor.CreateLinearGradientBrush();
                fresnelBrush.MappingMode = CompositionMappingMode.Relative;
                fresnelBrush.StartPoint = Vector2.Zero;
                fresnelBrush.EndPoint = Vector2.One;
                for (var i = 0; i < spec.FresnelStops.Length; i++)
                    fresnelBrush.ColorStops.Add(compositor.CreateColorGradientStop(spec.FresnelStops[i], Windows.UI.Color.FromArgb(spec.FresnelAlphas[i], 255, 255, 255)));
                rimShape.StrokeBrush = fresnelBrush;
                rimVisual.Shapes.Add(outerShape);
                rimVisual.Shapes.Add(rimShape);

                var textVisual = compositor.CreateSpriteVisual();
                textVisual.Brush = surfaceBrush;

                root.Children.InsertAtTop(backdropVisual);
                root.Children.InsertAtTop(tintVisual);
                root.Children.InsertAtTop(rimVisual);
                root.Children.InsertAtTop(textVisual);

                Marshal.Release(dxgiDevice);
                return new GlassHost(
                    compositor, target, root, clipGeo, outerGeo, rimGeo,
                    backdropVisual, tintVisual, rimVisual, textVisual,
                    graphicsDevice, surface, surfaceBrush,
                    d3dDevice, d2dFactory, d2dDevice, dwriteFactory, fontCollection, typography);
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
            {
                AppLog.Debug("FPS counter Liquid Glass init: " + ex.Message);
                ReleaseCom(ref typography);
                ReleaseCom(ref fontCollection);
                ReleaseCom(ref dwriteFactory);
                ReleaseCom(ref d2dDevice);
                ReleaseCom(ref d2dFactory);
                ReleaseCom(ref dxgiDevice);
                ReleaseCom(ref d3dDevice);
                return null;
            }
        }

        internal bool Render(Line line, double scale, int minWidth, int maxWidth, out int widthPx, out int heightPx)
        {
            widthPx = 0;
            heightPx = Math.Max(1, (int)Math.Round(Height * scale));
            var paddingPx = Math.Max(1, (int)Math.Round(Padding * scale));
            var fontSize = (float)(FontPixels * scale);

            var digits = line.Fps.ToString(CultureInfo.InvariantCulture);
            var textFormat = IntPtr.Zero;
            var dcPtr = IntPtr.Zero;
            var whiteBrush = IntPtr.Zero;
            var mutedBrush = IntPtr.Zero;
            var finalLayout = IntPtr.Zero;
            try
            {
                if (CreateTextFormat(_dwriteFactory, RegularFace, _fontCollection, 400, fontSize, out textFormat) < 0)
                    return false;
                var tail = " FPS" + Fit(line.Items, candidate => paddingPx * 2 + MeasureWidth(digits + " FPS" + candidate, textFormat), maxWidth);
                var fullText = digits + tail;
                if (CreateTextLayout(_dwriteFactory, fullText, textFormat, Math.Max(1f, maxWidth), heightPx, out finalLayout) < 0)
                    return false;
                ConfigureLayout(finalLayout, digits.Length, fullText.Length);
                if (GetLayoutMetrics(finalLayout, out var metrics) < 0) return false;

                var textWidth = (int)Math.Ceiling(metrics.WidthIncludingTrailingWhitespace);
                widthPx = Math.Min(maxWidth, Math.Max(minWidth, paddingPx * 2 + textWidth));

                var sizeVec = new Vector2(widthPx, heightPx);
                _root.Size = sizeVec;
                _backdropVisual.Size = sizeVec;
                _tintVisual.Size = sizeVec;
                _rimVisual.Size = sizeVec;
                _textVisual.Size = sizeVec;

                var radius = heightPx / 2.0f;
                _clipGeo.Size = sizeVec;
                _clipGeo.CornerRadius = new Vector2(radius, radius);

                _outerGeo.Offset = new Vector2(0.5f, 0.5f);
                _outerGeo.Size = new Vector2(Math.Max(1f, widthPx - 1f), Math.Max(1f, heightPx - 1f));
                _outerGeo.CornerRadius = new Vector2(Math.Max(0.5f, radius - 0.5f), Math.Max(0.5f, radius - 0.5f));

                _rimGeo.Offset = new Vector2(1.5f, 1.5f);
                _rimGeo.Size = new Vector2(Math.Max(1f, widthPx - 3f), Math.Max(1f, heightPx - 3f));
                _rimGeo.CornerRadius = new Vector2(Math.Max(0.5f, radius - 1.5f), Math.Max(0.5f, radius - 1.5f));

                var surfaceInterop = (ICompositionDrawingSurfaceInterop)Marshal.GetObjectForIUnknown(((IWinRTObject)_surface).NativeObject.ThisPtr);
                if (_renderedWidth != widthPx || _renderedHeight != heightPx)
                {
                    surfaceInterop.Resize(new SizeInt32(widthPx, heightPx));
                    _renderedWidth = widthPx;
                    _renderedHeight = heightPx;
                }

                var iidDc = IidD2D1DeviceContext;
                surfaceInterop.BeginDraw(IntPtr.Zero, ref iidDc, out dcPtr, out var offset);
                try
                {
                    SetDpi(dcPtr, 96f, 96f);
                    SetTextAntialiasMode(dcPtr, 2 /* D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE */);
                    var translate = new D2D1Matrix3x2(1f, 0f, 0f, 1f, offset.X, offset.Y);
                    SetTransform(dcPtr, ref translate);
                    var clear = new D2D1ColorF(0f, 0f, 0f, 0f);
                    Clear(dcPtr, ref clear);

                    var white = new D2D1ColorF(1f, 1f, 1f, 1f);
                    var muted = new D2D1ColorF(1f, 1f, 1f, RestInk / 255f);
                    if (CreateSolidColorBrush(dcPtr, ref white, out whiteBrush) < 0
                        || CreateSolidColorBrush(dcPtr, ref muted, out mutedBrush) < 0)
                        return false;

                    SetDrawingEffect(finalLayout, whiteBrush, new DWriteTextRange(0, (uint)digits.Length));
                    if (fullText.Length > digits.Length)
                        SetDrawingEffect(finalLayout, mutedBrush, new DWriteTextRange((uint)digits.Length, (uint)(fullText.Length - digits.Length)));

                    var originX = (widthPx - metrics.WidthIncludingTrailingWhitespace) * 0.5f;
                    var originY = (heightPx - metrics.Height) * 0.5f;
                    DrawTextLayout(dcPtr, new D2D1Point2F(originX, originY), finalLayout, whiteBrush, 0);

                    var identity = new D2D1Matrix3x2(1f, 0f, 0f, 1f, 0f, 0f);
                    SetTransform(dcPtr, ref identity);
                }
                finally
                {
                    surfaceInterop.EndDraw();
                }
                return true;
            }
            catch (ExternalException ex)
            {
                AppLog.Debug("FPS counter Liquid Glass draw failed: " + ex.Message);
                return false;
            }
            finally
            {
                ReleaseCom(ref finalLayout);
                ReleaseCom(ref mutedBrush);
                ReleaseCom(ref whiteBrush);
                ReleaseCom(ref dcPtr);
                ReleaseCom(ref textFormat);
            }
        }

        private int MeasureWidth(string text, IntPtr textFormat)
        {
            var layout = IntPtr.Zero;
            try
            {
                if (CreateTextLayout(_dwriteFactory, text, textFormat, 4096f, 256f, out layout) < 0) return 0;
                var digits = text.TakeWhile(char.IsAsciiDigit).Count();
                ConfigureLayout(layout, digits, text.Length);
                return GetLayoutMetrics(layout, out var metrics) >= 0 ? (int)Math.Ceiling(metrics.WidthIncludingTrailingWhitespace) : 0;
            }
            finally
            {
                ReleaseCom(ref layout);
            }
        }

        private void ConfigureLayout(IntPtr layout, int digitCount, int totalLength)
        {
            if (totalLength <= 0) return;
            SetTypography(layout, _typography, new DWriteTextRange(0, (uint)totalLength));
            if (digitCount > 0) SetFontWeight(layout, 600 /* DWRITE_FONT_WEIGHT_SEMI_BOLD */, new DWriteTextRange(0, (uint)digitCount));
        }

        public void Dispose()
        {
            ReleaseCom(ref _typography);
            ReleaseCom(ref _fontCollection);
            ReleaseCom(ref _dwriteFactory);
            ReleaseCom(ref _d2dDevice);
            ReleaseCom(ref _d2dFactory);
            ReleaseCom(ref _d3dDevice);
            _surfaceBrush.Dispose();
            _surface.Dispose();
            _graphicsDevice.Dispose();
            _target.Dispose();
            _compositor.Dispose();
        }

        private static Windows.UI.Color Argb(uint argb) => Windows.UI.Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        private static void EnsureDispatcherQueue()
        {
            var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            var hr = CreateDispatcherQueueController(options, out _);
            // S_OK or RPC_E_WRONG_THREAD/already created on this thread are safe.
            if (hr < 0 && unchecked((uint)hr) != 0x80070057 && unchecked((uint)hr) != 0x8001010E)
                Marshal.ThrowExceptionForHR(hr);
        }

        private static IntPtr TryLoadFontCollection(IntPtr dwriteFactory5, string fontPath)
        {
            if (!File.Exists(fontPath)) return IntPtr.Zero;
            IntPtr fontFile = IntPtr.Zero, builder = IntPtr.Zero, fontSet = IntPtr.Zero, collection = IntPtr.Zero;
            try
            {
                if (CreateFontFileReference(dwriteFactory5, fontPath, out fontFile) < 0) return IntPtr.Zero;
                if (CreateFontSetBuilder(dwriteFactory5, out builder) < 0) return IntPtr.Zero;
                if (AddFontFile(builder, fontFile) < 0) return IntPtr.Zero;
                if (CreateFontSet(builder, out fontSet) < 0) return IntPtr.Zero;
                if (CreateFontCollectionFromFontSet(dwriteFactory5, fontSet, out collection) < 0) return IntPtr.Zero;
                return collection;
            }
            finally
            {
                ReleaseCom(ref fontSet);
                ReleaseCom(ref builder);
                ReleaseCom(ref fontFile);
            }
        }

        private static void ReleaseCom(ref IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return;
            Marshal.Release(ptr);
            ptr = IntPtr.Zero;
        }

        // ---- Raw COM vtable calls verified against Windows SDK 10.0.26100.0 ----

        private static nint Slot(IntPtr comPtr, int index) => (*((nint**)comPtr))[index];

        private static int CreateD2DDevice(IntPtr factory1, IntPtr dxgiDevice, out IntPtr d2dDevice)
        {
            fixed (IntPtr* p = &d2dDevice)
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(factory1, 17))(factory1, dxgiDevice, p);
        }

        private static void SetDpi(IntPtr dc, float dpiX, float dpiY) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, float, float, void>)Slot(dc, 51))(dc, dpiX, dpiY);

        private static void SetTextAntialiasMode(IntPtr dc, uint mode) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)Slot(dc, 34))(dc, mode);

        private static void SetTransform(IntPtr dc, ref D2D1Matrix3x2 matrix)
        {
            fixed (D2D1Matrix3x2* m = &matrix)
                ((delegate* unmanaged[Stdcall]<IntPtr, D2D1Matrix3x2*, void>)Slot(dc, 30))(dc, m);
        }

        private static void Clear(IntPtr dc, ref D2D1ColorF color)
        {
            fixed (D2D1ColorF* c = &color)
                ((delegate* unmanaged[Stdcall]<IntPtr, D2D1ColorF*, void>)Slot(dc, 47))(dc, c);
        }

        private static int CreateSolidColorBrush(IntPtr dc, ref D2D1ColorF color, out IntPtr brush)
        {
            fixed (D2D1ColorF* c = &color)
            fixed (IntPtr* b = &brush)
                return ((delegate* unmanaged[Stdcall]<IntPtr, D2D1ColorF*, IntPtr, IntPtr*, int>)Slot(dc, 8))(dc, c, IntPtr.Zero, b);
        }

        private static void DrawTextLayout(IntPtr dc, D2D1Point2F origin, IntPtr layout, IntPtr brush, uint options) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, D2D1Point2F, IntPtr, IntPtr, uint, void>)Slot(dc, 28))(dc, origin, layout, brush, options);

        private static int CreateFontFileReference(IntPtr factory, string path, out IntPtr fontFile)
        {
            fixed (char* p = path)
            fixed (IntPtr* f = &fontFile)
                return ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, IntPtr*, int>)Slot(factory, 7))(factory, p, IntPtr.Zero, f);
        }

        private static int CreateTextFormat(IntPtr factory, string family, IntPtr collection, uint weight, float size, out IntPtr textFormat)
        {
            fixed (char* fam = family)
            fixed (char* loc = "en-US")
            fixed (IntPtr* tf = &textFormat)
                return ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, uint, uint, uint, float, char*, IntPtr*, int>)Slot(factory, 15))(
                    factory, fam, collection, weight, 0 /* NORMAL */, 5 /* NORMAL */, size, loc, tf);
        }

        private static int CreateTypography(IntPtr factory, out IntPtr typography)
        {
            fixed (IntPtr* t = &typography)
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(factory, 16))(factory, t);
        }

        private static int CreateTextLayout(IntPtr factory, string text, IntPtr format, float maxWidth, float maxHeight, out IntPtr layout)
        {
            fixed (char* s = text)
            fixed (IntPtr* l = &layout)
                return ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, float, float, IntPtr*, int>)Slot(factory, 18))(
                    factory, s, (uint)text.Length, format, maxWidth, maxHeight, l);
        }

        private static int CreateFontCollectionFromFontSet(IntPtr factory3, IntPtr fontSet, out IntPtr collection)
        {
            fixed (IntPtr* c = &collection)
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(factory3, 37))(factory3, fontSet, c);
        }

        private static int CreateFontSetBuilder(IntPtr factory5, out IntPtr builder1)
        {
            fixed (IntPtr* b = &builder1)
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(factory5, 43))(factory5, b);
        }

        private static int AddFontFile(IntPtr builder1, IntPtr fontFile) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(builder1, 7))(builder1, fontFile);

        private static int CreateFontSet(IntPtr builder, out IntPtr fontSet)
        {
            fixed (IntPtr* s = &fontSet)
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(builder, 6))(builder, s);
        }

        private static int AddFontFeature(IntPtr typography, FontFeature feature) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, FontFeature, int>)Slot(typography, 3))(typography, feature);

        private static int SetFontWeight(IntPtr layout, uint weight, DWriteTextRange range) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, DWriteTextRange, int>)Slot(layout, 32))(layout, weight, range);

        private static int SetDrawingEffect(IntPtr layout, IntPtr effect, DWriteTextRange range) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, DWriteTextRange, int>)Slot(layout, 38))(layout, effect, range);

        private static int SetTypography(IntPtr layout, IntPtr typography, DWriteTextRange range) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, DWriteTextRange, int>)Slot(layout, 40))(layout, typography, range);

        private static int GetLayoutMetrics(IntPtr layout, out DWriteTextMetrics metrics)
        {
            fixed (DWriteTextMetrics* m = &metrics)
                return ((delegate* unmanaged[Stdcall]<IntPtr, DWriteTextMetrics*, int>)Slot(layout, 60))(layout, m);
        }

        [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositorDesktopInterop
        {
            void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
            void EnsureOnThread(uint threadId);
        }

        [ComImport, Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositorInterop
        {
            void CreateCompositionSurfaceForHandle(IntPtr swapChain, out IntPtr result);
            void CreateCompositionSurfaceForSwapChain([MarshalAs(UnmanagedType.IUnknown)] object swapChain, out IntPtr result);
            void CreateGraphicsDevice([MarshalAs(UnmanagedType.IUnknown)] object renderingDevice, out IntPtr result);
        }

        [ComImport, Guid("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositionDrawingSurfaceInterop
        {
            void BeginDraw(IntPtr updateRect, ref Guid iid, out IntPtr updateObject, out Point updateOffset);
            void EndDraw();
            void Resize(SizeInt32 sizePixels);
            void Scroll(IntPtr scrollRect, IntPtr clipRect, int offsetX, int offsetY);
            void ResumeDraw();
            void SuspendDraw();
        }

        [StructLayout(LayoutKind.Sequential)] private readonly struct DispatcherQueueOptions { public int Size { get; init; } public int ThreadType { get; init; } public int ApartmentType { get; init; } }
        [StructLayout(LayoutKind.Sequential)] private readonly struct FontFeature(uint nameTag, uint parameter) { public readonly uint NameTag = nameTag, Parameter = parameter; }
        [StructLayout(LayoutKind.Sequential)] private readonly struct DWriteTextRange(uint start, uint length) { public readonly uint StartPosition = start, Length = length; }
        [StructLayout(LayoutKind.Sequential)] private struct DWriteTextMetrics { public float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight; public uint MaxBidiReorderingDepth, LineCount; }
        [StructLayout(LayoutKind.Sequential)] private readonly struct D2D1ColorF(float r, float g, float b, float a) { public readonly float R = r, G = g, B = b, A = a; }
        [StructLayout(LayoutKind.Sequential)] private readonly struct D2D1Point2F(float x, float y) { public readonly float X = x, Y = y; }
        [StructLayout(LayoutKind.Sequential)] private readonly struct D2D1Matrix3x2(float m11, float m12, float m21, float m22, float m31, float m32) { public readonly float M11 = m11, M12 = m12, M21 = m21, M22 = m22, M31 = m31, M32 = m32; }

        [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
        [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr immediateContext);
        [DllImport("d2d1.dll")] private static extern int D2D1CreateFactory(uint factoryType, ref Guid iid, IntPtr options, out IntPtr factory);
        [DllImport("dwrite.dll")] private static extern int DWriteCreateFactory(uint factoryType, ref Guid iid, out IntPtr factory);
    }

    // ---------------------------------------------------------------- native

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size; public uint Style; public IntPtr Proc; public int ClsExtra; public int WndExtra; public IntPtr Instance;
        public IntPtr Icon; public IntPtr Cursor; public IntPtr Background; public string? MenuName; public string ClassName; public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public IntPtr Hwnd; public uint Message; public IntPtr W, L; public uint Time; public Point Pt; }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction { public byte Op, Flags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, Used, Important; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TextMetric { public int TextHeight, Ascent, Descent, InternalLeading, ExternalLeading, AveCharWidth, MaxCharWidth, Weight, Overhang, DigitizedAspectX, DigitizedAspectY; public char First, Last, Default, Break; public byte Italic, Underlined, StruckOut, PitchAndFamily, Charset; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HighContrast { public int Size; public uint Flags; public IntPtr DefaultScheme; }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResourceExW(string path, uint flags, IntPtr reserved);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint precision, uint clip, uint quality, uint family, string face);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(IntPtr dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int length, out Size size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextMetricsW(IntPtr dc, out TextMetric metrics);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetTextFaceW(IntPtr dc, int count, [Out] char[] name);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string type, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out NativeMessage message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref NativeMessage message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, nuint id, uint ms, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, nuint id);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screen, ref Point at, ref Size size, IntPtr source, ref Point sourceAt, uint key, ref BlendFunction blend, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfoW(uint action, uint param, ref HighContrast highContrast, uint winIni);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
