using System.Runtime.CompilerServices;
using ExoLauncher.Helpers;
using ExoLauncher.Shell.Scene;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExoLauncher;

/// <summary>
/// Exo's card over another site's page, in that view's own window, where Edge would draw its own (WebPageGuard): the
/// card <see cref="Question"/> draws (large glass in its Solid fill, its hairline, 24px corners, a 20px title centered,
/// equal 32px capsules on one row), laid over the view alone, never over the window's band or another view. One card per
/// view at a time; the page under it can't be used while it shows.
/// </summary>
public sealed partial class MainWindow
{
    // A view's card: its layer over the view, and what ends a question it holds unanswered.
    private sealed record PageCard(Grid Layer, Action Dismiss);
    private static readonly ConditionalWeakTable<WebView2, PageCard> PageCards = new();

    /// <summary>
    /// A page that didn't load, in place of Edge's: the page color over the view and the card on it, the plain
    /// cause as its title and Try again, the primary, which runs <paramref name="retry"/> once. A failed try puts up a new
    /// card; a page that loads takes it away (<see cref="ClearPageCard"/>).
    /// </summary>
    internal static void ShowPageError(WebView2 view, string cause, Action retry)
    {
        var look = ConfirmLook.Void;
        var again = Capsule("Try again", look, look.Primary, look.PrimaryInk, look.PrimaryHover, Microsoft.UI.Colors.Transparent);
        var tried = false;
        again.Click += (_, _) =>
        {
            if (tried) return;
            tried = true;
            retry();
        };
        OpenPageCard(view, ExoConnectedTheme.PageColor(), look, cause, static () => { }, again);
    }

    /// <summary>
    /// A page asking to open another app, in place of Edge's question: the card over Exo's scrim on the page, the question
    /// as its title, Cancel first and focused (Enter alone never opens it, Escape is Cancel) and <paramref name="action"/>
    /// the primary. True only for that press; false for Cancel, the view going away, or a card already on the view.
    /// </summary>
    internal static Task<bool> AskOverPage(WebView2 view, string question, string action)
    {
        if (PageCards.TryGetValue(view, out _)) return Task.FromResult(false);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var look = ConfirmLook.Void;
        var cancel = Capsule("Cancel", look, look.Fill, look.Ink, look.Hover, look.Rim);
        var go = Capsule(action, look, look.Primary, look.PrimaryInk, look.PrimaryHover, Microsoft.UI.Colors.Transparent);
        void Gone(object sender, RoutedEventArgs e) => ClearPageCard(view);
        void Answer(bool yes)
        {
            answer.TrySetResult(yes);
            ClearPageCard(view);
        }
        cancel.Click += (_, _) => Answer(false);
        go.Click += (_, _) => Answer(true);
        var layer = OpenPageCard(view, look.Scrim, look, question, () =>
        {
            view.Unloaded -= Gone;
            answer.TrySetResult(false);
        }, cancel, go);
        if (layer is null) return Task.FromResult(false);
        layer.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            e.Handled = true;
            Answer(false);
        };
        view.Unloaded += Gone;
        return answer.Task;
    }

    /// <summary>Takes the view's card away; a question it held is answered Cancel.</summary>
    internal static void ClearPageCard(WebView2 view)
    {
        if (!PageCards.TryGetValue(view, out var card)) return;
        PageCards.Remove(view);
        (card.Layer.Parent as Panel)?.Children.Remove(card.Layer);
        card.Dismiss();
    }

    // The layer over the view (its own cell, margin and size in its panel, just above it), and the card on it.
    private static Grid? OpenPageCard(WebView2 view, Color background, ConfirmLook look, string title, Action dismiss,
        params Button[] buttons)
    {
        ClearPageCard(view);
        if (view.Parent is not Panel parent) return null;
        var layer = new Grid
        {
            Background = new SolidColorBrush(background), Margin = view.Margin, Width = view.Width, Height = view.Height,
            HorizontalAlignment = view.HorizontalAlignment, VerticalAlignment = view.VerticalAlignment,
            RequestedTheme = ElementTheme.Dark,
        };
        Grid.SetRow(layer, Grid.GetRow(view));
        Grid.SetRowSpan(layer, Grid.GetRowSpan(view));
        Grid.SetColumn(layer, Grid.GetColumn(view));
        Grid.SetColumnSpan(layer, Grid.GetColumnSpan(view));
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title, FontFamily = new FontFamily(Tokens.Font), FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(look.Ink), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 24, 0, 0) };
        foreach (var button in buttons)
        {
            Grid.SetColumn(button, row.ColumnDefinitions.Count);
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(button);
        }
        content.Children.Add(row);
        layer.Children.Add(new Border
        {
            Background = new SolidColorBrush(look.Glass), BorderBrush = new SolidColorBrush(look.Rim), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(SheetRadius), Padding = new Thickness(28, 28, 28, 24), MaxWidth = 420,
            Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        });
        parent.Children.Insert(parent.Children.IndexOf(view) + 1, layer);
        PageCards.AddOrUpdate(view, new PageCard(layer, dismiss));
        // The keyboard goes to the card's first capsule (a question's Cancel), as Question's does.
        buttons[0].Loaded += (_, _) => buttons[0].Focus(FocusState.Programmatic);
        return layer;
    }
}
