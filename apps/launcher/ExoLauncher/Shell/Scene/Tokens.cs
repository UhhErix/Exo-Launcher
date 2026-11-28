using Windows.UI;

namespace ExoLauncher.Shell.Scene;

/// <summary>
/// Product tokens for what the host draws itself, matched to ui/src/tokens.css. Pure black (AMOLED): the page and its
/// panels are #000. A line is white at a low alpha (--exo-line), a Liquid Glass edge rather than a gray
/// outline. The type is Geist, the UI's. Not Fluent.
/// </summary>
internal static class Tokens
{
    public static readonly Color Bg = Color.FromArgb(255, 0, 0, 0);
    public static readonly Color Surface = Color.FromArgb(255, 0, 0, 0);
    /// <summary>--exo-line: rgb(255 255 255 / 0.07).</summary>
    public static readonly Color Line = Color.FromArgb(18, 255, 255, 255);
    /// <summary>--color-fg.</summary>
    public static readonly Color Fg = Color.FromArgb(255, 255, 255, 255);
    /// <summary>--color-muted.</summary>
    public static readonly Color Muted = Color.FromArgb(255, 0xb4, 0xb4, 0xb4);
    /// <summary>--hover-fill over black (#1a1a1a).</summary>
    public static readonly Color NavOn = Color.FromArgb(255, 26, 26, 26);

    public const string Font = "ms-appx:///Assets/Fonts/Geist-Variable.ttf#Geist";
}
