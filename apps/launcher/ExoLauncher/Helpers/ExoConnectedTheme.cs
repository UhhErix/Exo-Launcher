using System.Numerics;
using System.Runtime.InteropServices;
using ExoLauncher.Shell.Scene;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace ExoLauncher.Helpers;

internal static class ExoConnectedTheme
{
    private static ControlTemplate? _captionTemplate;
    internal static SolidColorBrush Brush(Color color) => new(color);
    internal static void Resources(ResourceDictionary resources)
    {
        // Keep native keyboard/accessibility behavior, replace Fluent paint.
        foreach (var key in new[] { "MenuFlyoutPresenterBackground", "TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver", "ListViewItemBackground" }) resources[key] = Brush(Tokens.Surface);
        foreach (var key in new[] { "MenuFlyoutPresenterBorderBrush", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "MenuFlyoutSeparatorBackground" }) resources[key] = Brush(Tokens.Line);
        foreach (var key in new[] { "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed", "MenuFlyoutSubItemForeground", "TextControlForeground", "TextControlForegroundFocused", "ListViewItemForeground", "ListViewItemForegroundSelected", "ListViewItemForegroundSelectedPointerOver", "ListViewItemForegroundSelectedPressed" }) resources[key] = Brush(Tokens.Fg);
        foreach (var key in new[] { "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed", "MenuFlyoutSubItemBackgroundPointerOver", "MenuFlyoutSubItemBackgroundSubMenuOpened", "ListViewItemBackgroundPointerOver", "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" }) resources[key] = Brush(Tokens.NavOn);
        foreach (var key in new[] { "SystemControlHighlightAccentBrush", "SystemControlHighlightListAccentLowBrush", "ListViewItemSelectionIndicatorBrush", "ListViewItemCheckBoxBrush" }) resources[key] = Brush(Tokens.Muted);
        resources["TextControlSelectionHighlightColor"] = Tokens.Muted;
        resources["MenuFlyoutItemFontFamily"] = new FontFamily(Tokens.Font);
    }

    /// The title band and its hairline, in DIPs.
    internal const int Band = 53;

    // The Bar's window buttons: Exo's minus, maximize, restore and close (ui/src/brand/glyphs.ts), lines on its 24 grid.
    private const string MinimizeGlyph = "M5 12h14";
    private const string MaximizeGlyph = "M7 4h10a3 3 0 0 1 3 3v10a3 3 0 0 1 -3 3h-10a3 3 0 0 1 -3 -3v-10a3 3 0 0 1 3 -3Z";
    private const string RestoreGlyph = "M6 8h7.5a2.5 2.5 0 0 1 2.5 2.5v7.5a2.5 2.5 0 0 1 -2.5 2.5h-7.5a2.5 2.5 0 0 1 -2.5 -2.5v-7.5a2.5 2.5 0 0 1 2.5 -2.5ZM8 8V6a2.5 2.5 0 0 1 2.5-2.5H18A2.5 2.5 0 0 1 20.5 6v7.5a2.5 2.5 0 0 1-2.5 2.5h-2";
    private const string CloseGlyph = "M6 6l12 12M18 6L6 18";

    /// The page and the Bar's small glass (Void; ui/src/tokens.css; glass.css on .exo-glass): its words, the
    /// buttons' glyph (--exo-glass-secondary), hover and pressed lifts (the ink at 6 and 12%), Bad, the rim, the hairline
    /// (--exo-float-line) and the clear glass over the page.
    private sealed record Look(Color Page, Color Ink, Color Glyph, Color Hover, Color Pressed, Color Bad, Color Rim, Color Hairline, Color Glass)
    {

        internal static Look Current() => new(Rgb(0x000000), Rgb(0xFFFFFF), Rgb(0xE4E4E4), Lift(0xFFFFFF, 15), Lift(0xFFFFFF, 31), Rgb(0xFF5C5C),
            Lift(0xFFFFFF, 36), Rgb(0x1F1F1F), Rgb(0x000000));

        private static Color Rgb(uint rgb) => Lift(rgb, 255);
        private static Color Lift(uint rgb, byte alpha) => Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// The page color (Void's black), which these windows' band and a page that didn't load wear (MainWindow.ShowPageError).
    internal static Color PageColor() => Look.Current().Page;

    /// <summary>
    /// Every window Exo opens for a service (its sign-ins, a page's own pop-outs), in Exo's look: a 52px band in the
    /// page's black over a hairline, the service's mark (or its name alone, ServiceMarks) and name at its left, and
    /// the window's buttons at its right as the Bar draws them: Exo's glyphs, 34 by 30 in a 36px capsule of small
    /// glass, the glass's lift under the pointer and the close glyph in Bad. The whole band drags and double-clicks to
    /// maximize; Maximize is Windows' own maximize region, so its hover opens snap layouts. The window opens at the
    /// service's size (or <paramref name="size"/>, in DIPs under the band), centered on the screen Exo is on, with the
    /// service's mark as its icon. <paramref name="content"/> fills the rest; Exo never draws into a provider's page.
    /// </summary>
    internal static void WindowContent(Window window, string service, FrameworkElement content, (int Width, int Height)? size = null)
    {
        var mark = ServiceMarks.For(service);
        var look = Look.Current();
        var reduceMotion = App.Services.Settings.Current.ReduceMotion;
        var root = new Grid { Background = Brush(look.Page), RequestedTheme = ElementTheme.Dark };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Band) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var band = new Grid { Background = Brush(look.Page), BorderBrush = Brush(look.Hairline), BorderThickness = new Thickness(0, 0, 0, 1) };
        band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        band.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        if (mark.Mark is { } path)
            title.Children.Add(new XamlPath { Data = Geometry(path), Fill = Brush(ServiceMarks.Fill(mark, look.Page, look.Ink)), Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock
        {
            Text = mark.Name, FontFamily = new FontFamily(Tokens.Font), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(look.Ink), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        band.Children.Add(title);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        (Button Button, XamlPath Glyph, Action<bool, bool> Paint) Control(string glyph, string name, bool close = false)
        {
            var stroke = Brush(look.Glyph);
            var icon = new XamlPath
            {
                Data = Geometry(glyph), Stroke = stroke, StrokeThickness = 4.0 / 3, Width = 16, Height = 16,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            var button = new Button
            {
                Content = icon, Width = 34, Height = 30, CornerRadius = new CornerRadius(15), Padding = new Thickness(0),
                Background = Brush(Microsoft.UI.Colors.Transparent), Template = _captionTemplate ??= (ControlTemplate)XamlReader.Load("""
                    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                      <Border Background="{TemplateBinding Background}" CornerRadius="{TemplateBinding CornerRadius}">
                        <Border.BackgroundTransition><BrushTransition Duration="0:0:0.12"/></Border.BackgroundTransition>
                        <ContentPresenter Content="{TemplateBinding Content}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                      </Border>
                    </ControlTemplate>
                    """),
                // As Windows' own caption buttons: no tab stop, so the page has the keyboard from the start and a click
                // here never takes it from a half-typed field.
                IsTabStop = false,
                CenterPoint = new Vector3(17, 15, 0), ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(90) },
            };
            AutomationProperties.SetName(button, name);
            void Paint(bool hover, bool pressed)
            {
                button.Background = Brush(pressed ? look.Pressed : hover ? look.Hover : Microsoft.UI.Colors.Transparent);
                stroke.Color = !hover && !pressed ? look.Glyph : close ? look.Bad : look.Ink;
                // Every press gives way a little (0.97) and comes back; Reduce motion holds it still.
                if (reduceMotion) return;
                button.ScaleTransition.Duration = TimeSpan.FromMilliseconds(pressed ? 90 : 120);
                button.Scale = pressed ? new Vector3(0.97f, 0.97f, 1) : Vector3.One;
            }
            button.PointerEntered += (_, _) => Paint(true, false);
            button.PointerExited += (_, _) => Paint(false, false);
            button.PointerCaptureLost += (_, _) => Paint(false, false);
            button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Paint(true, true)), true);
            button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Paint(true, false)), true);
            buttons.Children.Add(button);
            return (button, icon, Paint);
        }
        var minimize = Control(MinimizeGlyph, "Minimize");
        minimize.Button.Click += (_, _) => (window.AppWindow.Presenter as OverlappedPresenter)?.Minimize();
        var maximize = Control(MaximizeGlyph, "Maximize");
        // A pointer's click is Windows' own (the maximize region); this is Narrator's.
        maximize.Button.Click += (_, _) => ToggleMaximize(window);
        var close = Control(CloseGlyph, "Close", close: true);
        close.Button.Click += (_, _) => window.Close();
        var capsule = new Border
        {
            Height = 36, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), BorderBrush = Brush(look.Rim),
            Background = Brush(look.Glass), Padding = new Thickness(2, 0, 2, 0), Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = buttons,
        };
        Grid.SetColumn(capsule, 1);
        band.Children.Add(capsule);
        root.Children.Add(band);
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        window.Content = root;
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(band);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(true, false);

        var maximized = false;
        void State()
        {
            var now = Maximized(window);
            if (now == maximized) return;
            maximized = now;
            maximize.Glyph.Data = Geometry(now ? RestoreGlyph : MaximizeGlyph);
            AutomationProperties.SetName(maximize.Button, now ? "Restore" : "Maximize");
        }
        window.AppWindow.Changed += (_, e) => { if (e.DidPresenterChange || e.DidSizeChange) State(); };

        // The band is the caption; the buttons take their own clicks, and Maximize is Windows' maximize region.
        var input = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        void Regions()
        {
            if (band.XamlRoot is not { } xamlRoot || close.Button.ActualWidth == 0) return;
            var scale = xamlRoot.RasterizationScale;
            RectInt32 Rect(FrameworkElement element)
            {
                var r = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                return new((int)Math.Round(r.X * scale), (int)Math.Round(r.Y * scale), (int)Math.Round(r.Width * scale), (int)Math.Round(r.Height * scale));
            }
            try
            {
                input.SetRegionRects(NonClientRegionKind.Passthrough, [Rect(minimize.Button), Rect(close.Button)]);
                input.SetRegionRects(NonClientRegionKind.Maximize, [Rect(maximize.Button)]);
            }
            catch { /* the next layout or scale change applies them */ }
        }
        band.SizeChanged += (_, _) => Regions();
        capsule.SizeChanged += (_, _) => Regions();
        band.Loaded += (_, _) =>
        {
            Regions();
            if (band.XamlRoot is { } xamlRoot) xamlRoot.Changed += (_, _) => Regions();
        };
        // Windows' maximize region gets no XAML pointer events: its hover and press come from the caption's input.
        bool? pressedFrom = null;
        void OnMaximize(NonClientPointerEventArgs e, Action act)
        {
            if (e.RegionKind == NonClientRegionKind.Maximize) window.DispatcherQueue.Enqueue(() => act());
        }
        input.PointerEntered += (_, e) => UiGuard.Run(() => OnMaximize(e, () => maximize.Paint(true, false)));
        input.PointerExited += (_, e) => UiGuard.Run(() => OnMaximize(e, () => maximize.Paint(false, false)));
        input.PointerPressed += (_, e) => UiGuard.Run(() => OnMaximize(e, () =>
        {
            pressedFrom = Maximized(window);
            maximize.Paint(true, true);
        }));
        input.PointerReleased += (_, e) => UiGuard.Run(() =>
        {
            var inside = e.IsPointInRegion;
            OnMaximize(e, () =>
            {
                maximize.Paint(inside, false);
                var from = pressedFrom;
                pressedFrom = null;
                if (from is not { } before || !inside) return;
                // Windows performs the click on its own maximize region; should it not have, the button still does its job.
                var check = window.DispatcherQueue.CreateTimer();
                check.Interval = TimeSpan.FromMilliseconds(200);
                check.IsRepeating = false;
                check.Tick += (_, _) => UiGuard.Run(() =>
                {
                    check.Stop();
                    try { if (Maximized(window) == before) ToggleMaximize(window); }
                    catch { /* closed meanwhile */ }
                });
                check.Start();
            });
        });

        var (width, height) = size ?? (mark.Width, mark.Height);
        Place(window, width, height + Band);
        _ = SetIconAsync(window, service);
    }

    /// The size a page asked for its new window (window.open's width and height), when it asked for a usable one.
    internal static (int Width, int Height)? AskedSize(CoreWebView2WindowFeatures features) =>
        features is { HasSize: true, Width: >= 320, Height: >= 320 } ? ((int)features.Width, (int)features.Height) : null;

    private static bool Maximized(Window window) =>
        window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };

    private static void ToggleMaximize(Window window)
    {
        if (window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
        else presenter.Maximize();
    }

    /// An SVG path (on a 24-unit box) at 16px, nonzero as SVG fills it (XAML's own default is even-odd).
    private static PathGeometry Geometry(string data)
    {
        const double scale = 16.0 / 24;
        static Windows.Foundation.Point At(SvgPath.Pt p) => new(p.X * scale, p.Y * scale);
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        foreach (var figure in SvgPath.Parse(data))
        {
            var xaml = new PathFigure { StartPoint = At(figure.Start), IsClosed = figure.Closed, IsFilled = true };
            foreach (var segment in figure.Segments)
                xaml.Segments.Add(segment.Line
                    ? new LineSegment { Point = At(segment.To) }
                    : new BezierSegment { Point1 = At(segment.C1), Point2 = At(segment.C2), Point3 = At(segment.To) });
            geometry.Figures.Add(xaml);
        }
        return geometry;
    }

    /// <summary>
    /// <paramref name="window"/> at <paramref name="width"/> by <paramref name="height"/> DIPs in its screen's scale (never
    /// past the work area), centered on the screen Exo's window is on, or <paramref name="on"/>. Returns that scale.
    /// </summary>
    internal static double Place(Window window, double width, double height, DisplayArea? on = null)
    {
        try
        {
            var display = on ?? (App.MainAppWindow is { } exo
                ? DisplayArea.GetFromWindowId(exo.AppWindow.Id, DisplayAreaFallback.Primary)
                : DisplayArea.Primary);
            var area = display.WorkArea;
            var monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
            // Onto that screen first: its scale change, if any, lands before the size is set.
            window.AppWindow.Move(new PointInt32(area.X + 20, area.Y + 20));
            // The size is the window's inside; Windows' invisible resize border goes around it. (ResizeClient would also
            // make room for a system title bar these windows don't have.)
            var (outer, inner) = (window.AppWindow.Size, window.AppWindow.ClientSize);
            var w = Math.Min((int)Math.Round(width * scale), area.Width - 40) + outer.Width - inner.Width;
            var h = Math.Min((int)Math.Round(height * scale), area.Height - 40) + outer.Height - inner.Height;
            window.AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
            return scale;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Service window placement: " + ex.GetType().Name);
            return 1;
        }
    }

    // The mark as the window's icon (title bar, taskbar, Alt+Tab), drawn off the UI thread the first time.
    private static async Task SetIconAsync(Window window, string service)
    {
        try
        {
            if (await Task.Run(() => ServiceMarks.IconFile(service)) is { } file) window.AppWindow.SetIcon(file);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Service window icon: " + ex.GetType().Name);
        }
    }

    [DllImport("Shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
