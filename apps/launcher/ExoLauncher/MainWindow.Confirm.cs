using ExoLauncher.Helpers;
using ExoLauncher.Shell.Scene;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    /// <summary>
    /// A question Exo's own window asks before money is spent (Discord's Buy), outside the page: a page that is no longer
    /// trustworthy can't answer it. Everything else asks once, in place on the page (DESIGN.md › Confirm), or lets Windows'
    /// own prompt be the confirm. False when declined, closed over, or the window can't show it.
    /// </summary>
    /// <param name="danger">The action destroys something (its words in Bad), else it is the dialog's primary.</param>
    internal async Task<bool> ConfirmNativeAsync(string title, string body, string action, bool danger = false)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.Enqueue(async () => answer.TrySetResult(await ShowConfirmAsync(title, body, action, danger))))
            return false;
        return await answer.Task.ConfigureAwait(false);
    }

    /// <summary>Asks with <see cref="Question"/> once the window's other questions are answered.</summary>
    private async Task<bool> ShowConfirmAsync(string title, string body, string action, bool danger)
    {
        try
        {
            if (_closingWindow || WebHost.XamlRoot is not { } root) return false;
            await _connectedDialogGate.WaitAsync(_lifetimeCts.Token);
            try
            {
                if (_closingWindow || WebHost.XamlRoot is null) return false;
                var (dialog, answered) = Question(root, title, body, action, danger);
                await dialog.ShowAsync();
                return answered();
            }
            finally { _connectedDialogGate.Release(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }

    /// <summary>Large glass's corners (ui/src/tokens.css, --exo-sheet-radius).</summary>
    private const double SheetRadius = 24;

    /// <summary>
    /// Every question Exo's own window asks (this one, a connected app's alert, confirm, prompt, permission and leaving),
    /// as Exo's large glass in its Solid fill (a native layer can't blur the page under it): the panel color
    /// inside the glass's one even hairline, 24px corners, over Exo's scrim; a 20px title and 13px words in Inter
    /// centered, a field when it takes one, then equal 32px capsules on one row, <paramref name="cancel"/> first (clear
    /// inside its hairline, as the page's) and the action last: the primary (the ink's color, the page's words on it) or
    /// its words in Bad. Every WinUI accent (the dialog's accent border, a field's focus line and selection) takes the
    /// page's colors instead. Cancel has the focus: Enter alone never confirms, and Escape closes it. No action: the one
    /// capsule. Exo draws it over the page, so no page can answer it; <c>Answered</c> says whether the action was pressed.
    /// </summary>
    private static (ContentDialog Dialog, Func<bool> Answered) Question(XamlRoot root, string title, string body, string? action,
        bool danger = false, string cancel = "Cancel", FrameworkElement? field = null)
    {
        var look = ConfirmLook.Void;
        var confirmed = false;
        var glass = new SolidColorBrush(look.Glass);
        var rim = new SolidColorBrush(look.Rim);
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var dialog = new ContentDialog
        {
            XamlRoot = root, RequestedTheme = ElementTheme.Dark,
            FontFamily = new FontFamily(Tokens.Font), Foreground = new SolidColorBrush(look.Ink),
            Background = glass, BorderBrush = rim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(SheetRadius),
        };
        // WinUI's own template (generic.xaml, DefaultContentDialogStyle) draws these from its theme: the smoke under the
        // dialog, a top band and its separator, the inner clip's 8px corners, the title's margin and an accent border.
        var resources = dialog.Resources;
        resources["ContentDialogBackground"] = glass;
        resources["ContentDialogTopOverlay"] = clear;
        resources["ContentDialogBorderBrush"] = rim;
        resources["ContentDialogBorderWidth"] = new Thickness(1);
        resources["ContentDialogSeparatorBorderBrush"] = clear;
        resources["ContentDialogSeparatorThickness"] = new Thickness(0);
        resources["ContentDialogSmokeFill"] = new SolidColorBrush(look.Scrim);
        resources["ContentDialogCornerRadius"] = new CornerRadius(SheetRadius);
        resources["OverlayCornerRadius"] = new CornerRadius(SheetRadius);
        resources["ContentDialogTitleMargin"] = new Thickness(0);
        resources["ContentDialogPadding"] = new Thickness(28, 28, 28, 24);
        resources["ContentDialogMinWidth"] = 400.0;
        resources["ContentDialogMaxWidth"] = 420.0;
        resources["SystemControlForegroundAccentBrush"] = rim;

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(look.Ink),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        content.Children.Add(new TextBlock
        {
            Text = body, FontSize = 13, LineHeight = 19.5, Foreground = new SolidColorBrush(look.Secondary),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        if (field is not null)
        {
            // In the dialog's colors, as the page's fields: the control fill inside the hairline, the ink's edge when
            // focused (WinUI's is an accent line), and a selection in the ink, not the accent.
            (string[] Keys, Color Color)[] paint =
            [
                (["TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused"], look.Fill),
                (["TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlElevationBorderBrush"], look.Rim),
                (["TextControlBorderBrushFocused", "TextControlElevationBorderFocusedBrush"], look.Ink),
                (["TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused"], look.Ink),
                (["TextControlSelectionHighlightColor"], look.Selection),
            ];
            foreach (var (keys, color) in paint)
                foreach (var key in keys) field.Resources[key] = new SolidColorBrush(color);
            if (field is TextBox box) box.SelectionHighlightColor = new SolidColorBrush(look.Selection);
            if (field is Control control) { control.CornerRadius = new CornerRadius(16); control.FontFamily = new FontFamily(Tokens.Font); control.FontSize = 13; }
            field.Margin = new Thickness(0, 12, 0, 0);
            content.Children.Add(field);
        }
        var no = Capsule(cancel, look, look.Fill, look.Ink, look.Hover, look.Rim);
        no.Click += (_, _) => dialog.Hide();
        var buttons = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 24, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.Children.Add(no);
        if (action is not null)
        {
            var go = danger
                ? Capsule(action, look, look.Fill, look.Bad, look.Hover, look.Rim)
                : Capsule(action, look, look.Primary, look.PrimaryInk, look.PrimaryHover, Microsoft.UI.Colors.Transparent);
            go.Click += (_, _) => { confirmed = true; dialog.Hide(); };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(go, 1);
            buttons.Children.Add(go);
        }
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Opened += (_, _) => no.Focus(FocusState.Programmatic);
        return (dialog, () => confirmed);
    }

    private static ControlTemplate? _capsuleTemplate;

    // One of the dialog's 32px capsules, Exo's .exo-set-btn: its fill inside its hairline (`edge`), its words, a lift
    // under the pointer, and the page's 2px focus ring in the ink. `hover`: a translucent lift over a clear or tinted
    // fill, or the primary's own hover step.
    private static Button Capsule(string label, ConfirmLook look, Color fill, Color ink, Color hover, Color edge)
    {
        var button = new Button
        {
            Content = label, FontFamily = new FontFamily(Tokens.Font), FontSize = 13, FontWeight = FontWeights.Medium,
            Height = 32, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 0, 14, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(fill), Foreground = new SolidColorBrush(ink),
            BorderBrush = new SolidColorBrush(edge), BorderThickness = new Thickness(1), UseSystemFocusVisuals = true,
            FocusVisualPrimaryBrush = new SolidColorBrush(look.Ink), FocusVisualSecondaryBrush = new SolidColorBrush(look.Glass),
            FocusVisualPrimaryThickness = new Thickness(2), FocusVisualSecondaryThickness = new Thickness(0),
            Template = _capsuleTemplate ??= (ControlTemplate)XamlReader.Load("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                  <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" BackgroundSizing="InnerBorderEdge" CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}">
                    <ContentPresenter Content="{TemplateBinding Content}" FontFamily="{TemplateBinding FontFamily}" FontSize="{TemplateBinding FontSize}" FontWeight="{TemplateBinding FontWeight}" Foreground="{TemplateBinding Foreground}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                  </Border>
                </ControlTemplate>
                """),
        };
        var rest = button.Background;
        var lifted = new SolidColorBrush(hover.A == 255 ? hover : Blend(fill, hover));
        button.PointerEntered += (_, _) => button.Background = lifted;
        button.PointerExited += (_, _) => button.Background = rest;
        return button;
    }

    // A translucent lift over a fill, as the page's --hover-fill shows over a control.
    private static Color Blend(Color under, Color over)
    {
        var a = over.A / 255.0;
        byte Mix(byte u, byte o) => (byte)Math.Round(under.A == 0 ? o : u * (1 - a) + o * a);
        return Color.FromArgb(Math.Max(under.A, over.A), Mix(under.R, over.R), Mix(under.G, over.G), Mix(under.B, over.B));
    }

    /// <summary>
    /// The page's tokens for large glass and its controls (ui/src/tokens.css): the Solid fill
    /// (--exo-solid-panel), the hairline (--glassy-ring), the panel's words (--exo-panel-ink, --exo-glass-panel-secondary),
    /// a control's fill and lift (--exo-fill, --hover-fill), Bad as a danger capsule's words show it, the primary and its
    /// words (--color-primary, --color-primary-fg, --exo-primary-hover), the scrim (--exo-scrim at
    /// --exo-scrim-k) and a field's selection in the ink.
    /// </summary>
    private sealed record ConfirmLook(Color Glass, Color Rim, Color Ink, Color Secondary, Color Fill, Color Hover, Color Bad,
        Color Primary, Color PrimaryInk, Color PrimaryHover, Color Scrim, Color Selection)
    {
        internal static readonly ConfirmLook Void = new(Rgb(0, 0, 0), Argb(36, 255, 255, 255), Rgb(0xff, 0xff, 0xff), Rgb(0xdc, 0xdc, 0xdc),
            Argb(0, 0, 0, 0), Argb(26, 255, 255, 255), Rgb(0xff, 0x79, 0x79),
            Rgb(0xff, 0xff, 0xff), Rgb(0, 0, 0), Rgb(0xe8, 0xe8, 0xe8), Argb(153, 0, 0, 0), Argb(77, 255, 255, 255));

        private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
        private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
    }
}
