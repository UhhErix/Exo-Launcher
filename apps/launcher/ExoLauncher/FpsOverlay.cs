using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher;

/// <summary>
/// The FPS counter over the game: one small tab flush against the top of the game's screen ("142 FPS"), in its corner or
/// its middle, rounded only where it faces the screen's interior. Drawn by plain GDI on a layered window on a thread of its
/// own, with no WebView2, since it stays open for the whole session. It never takes focus or a click, and stays out of
/// recordings and streams (WDA_EXCLUDEFROMCAPTURE) where Windows allows that. Nothing touches the game. It looks twice a
/// second at most and only then if asked to (<see cref="Look"/>); away from the game it has no timer and sleeps until
/// Windows says the front changed. A redraw happens only when its text changes, replacing the old picture in one call, so
/// nothing flickers. <see cref="Services.FpsCounter"/> says what to show, and null hides it. It is sRGB like the desktop, so
/// on an HDR screen Windows shows it at the screen's SDR white.
/// </summary>
internal sealed unsafe class FpsOverlay : IDisposable
{
    /// <param name="Items">What follows "142 FPS", each part after a " · " (<see cref="Services.FpsItems.Text"/>).</param>
    internal readonly record struct Line(int Fps, string? Items);

    /// <summary>What to show, and when to look again: 0 is when the front changes or something says so.</summary>
    internal readonly record struct Look(Line? Line, uint NextMs);

    private const uint RefreshMessage = 0x8001, EventSystemForeground = 0x0003;
    private const int Padding = 10, Height = 24, Margin = 8, FontPixels = 13, ForegroundOutOfContext = 0;
    // The plate is black at 78%: at half alpha white numbers were lost over a bright sky.
    private const byte CapsuleAlpha = 199, NumberInk = 255, RestInk = 191;
    private const uint TextQuality = 4; // ANTIALIASED_QUALITY: gray, hinted to the pixel grid, so stems stay sharp at any scale.
    private const string ClassName = "ExoFpsCounter", SemiBoldFace = "Exoline SemiBold", RegularFace = "Exoline", Separator = " · ";

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
    private bool _shown, _failed;
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
        path ??= Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Exoline-Variable.ttf");
        if (AddFontResourceExW(path, 0x10 /* FR_PRIVATE */, IntPtr.Zero) == 0) AppLog.Warn("FPS counter: Exoline didn't load, so Windows picks a font.");
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

    private static int TextWidth(IntPtr dc, string text) => GetTextExtentPoint32W(dc, text, text.Length, out var size) ? size.Width : 0;

    /// <summary>The text's pixels over the capsule: what the text added stays white, the black plate shows where it did not.</summary>
    private static void Lay(Capsule capsule)
    {
        for (var row = 0; row < capsule.Height; row++)
        for (var column = 0; column < capsule.Width; column++)
        {
            var at = capsule.Bits + row * capsule.Width + column;
            var ink = *at & 0xFF;
            var under = (uint)Math.Round(CapsuleAlpha * Cover(capsule.Width, capsule.Height, column, row));
            var alpha = ink + under * (255 - ink) / 255;
            *at = alpha << 24 | ink << 16 | ink << 8 | ink;
        }
    }

    // ---------------------------------------------------------------- the window

    private void Run()
    {
        try
        {
            MakeWindow();
            _frontChanged ??= (_, _, _, _, _, _, _) => _current?.Tick();
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
            if (_current == this) _current = null;
            _hwnd = IntPtr.Zero;
            _ready.Set();
        }
    }

    private void MakeWindow()
    {
        var instance = GetModuleHandleW(null);
        if (_proc is null)
        {
            _proc = OnMessage;
            var type = new WndClassEx { Size = Marshal.SizeOf<WndClassEx>(), Proc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = instance, ClassName = ClassName };
            if (RegisterClassExW(ref type) == 0) throw new InvalidOperationException("The counter's window class wasn't registered.");
        }
        _current = this;
        // Layered, click-through, topmost, tool (no taskbar), no activation; a plain popup.
        const uint exStyle = 0x00080000 | 0x00000020 | 0x00000008 | 0x00000080 | 0x08000000;
        _hwnd = CreateWindowExW(exStyle, ClassName, "Exo FPS counter", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("The counter's window wasn't made.");
        if (!SetWindowDisplayAffinity(_hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */)) AppLog.Info("FPS counter: Windows wouldn't keep it out of captures here.");
    }

    private static IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l)
    {
        switch (message)
        {
            case 0x0084: return new IntPtr(-1); // WM_NCHITTEST: HTTRANSPARENT
            case 0x0021: return new IntPtr(3); // WM_MOUSEACTIVATE: MA_NOACTIVATE
            case 0x0010: DestroyWindow(hwnd); return IntPtr.Zero; // WM_CLOSE
            case 0x0002: PostQuitMessage(0); return IntPtr.Zero; // WM_DESTROY
            case 0x0113: case RefreshMessage: _current?.Tick(); return IntPtr.Zero; // WM_TIMER
            default: return DefWindowProcW(hwnd, message, w, l);
        }
    }

    /// <summary>A native callback's work: anything thrown here would end the process, so a failure hides the counter and says so once.</summary>
    private void Tick()
    {
        try
        {
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

    private void Show(Line line)
    {
        if (_shown && _drawn == line && _position() == _drawnPosition && MonitorFromWindow(GetForegroundWindow(), 2) == _drawnMonitor) return;
        var monitor = MonitorFromWindow(GetForegroundWindow(), 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) { Hide(); return; }
        var scale = GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        var position = _position();

        // A screen with another scale measures the text again.
        var margin = MarginAt(scale);
        using var capsule = Draw(line, scale, _drawnScale == scale ? MinWidth(_width, _drawn, line) : 0, info.Monitor.Right - info.Monitor.Left - margin * 2);
        _width = capsule.Width;
        var (x, y) = Place(position, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, capsule.Width, margin);
        var at = new Point { X = x, Y = y };
        var source = new Point();
        var size = new Size { Width = capsule.Width, Height = capsule.Height };
        var blend = new BlendFunction { Op = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        if (!UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref at, ref size, capsule.Dc, ref source, 0, ref blend, 2 /* ULW_ALPHA */))
            throw new InvalidOperationException($"UpdateLayeredWindow failed ({Marshal.GetLastPInvokeError()}).");
        (_drawn, _drawnScale, _drawnPosition, _drawnMonitor) = (line, scale, position, monitor);
        if (_shown) return;
        // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW, on top of whatever else is topmost.
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        _shown = true;
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
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
