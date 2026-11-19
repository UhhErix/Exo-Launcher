using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// A trusted press on the music page. A script's click has no user activation, so the page refuses to start playback;
/// a DevTools mouse event is a real input, and the page treats it as the user's own gesture.
/// The centre of Spotify's play button is read in the page (a zero-size button, a hidden page, answers null and the
/// caller falls back to the page's own control).
/// </summary>
internal static class MusicPress
{
    /// The play button's centre and whether it currently shows Pause (the song is playing). Null when there is no button to press.
    internal const string SpotifyPlayButtonCentre =
        "(function(){var b=document.querySelector('[data-testid=\"control-button-playpause\"]');if(!b)return null;" +
        "var r=b.getBoundingClientRect();if(!(r.width>0&&r.height>0))return null;" +
        "return{x:r.left+r.width/2,y:r.top+r.height/2,playing:(b.getAttribute('aria-label')||'').toLowerCase()==='pause'};})()";

    /// Input.dispatchMouseEvent for a left press and release at (x, y) in CSS pixels of the page, in the order a click makes.
    /// Null when a coordinate is not finite or is negative.
    internal static IReadOnlyList<(string Method, string Params)>? ClickCommands(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0) return null;
        var at = new { x = Math.Round(x, 1), y = Math.Round(y, 1), button = "left", clickCount = 1 };
        return
        [
            ("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", at.x, at.y, at.button, at.clickCount })),
            ("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", at.x, at.y, at.button, at.clickCount })),
        ];
    }

}
