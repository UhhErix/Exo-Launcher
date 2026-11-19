#if DEBUG
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ExoLauncher.Helpers;

/// <summary>
/// Debug builds only, to see the service windows' chrome without signing in anywhere. EXO_CHROME_PREVIEW=1 runs this
/// instead of Exo (no hand-off to a running Exo, no main window, nothing started), and only over a throwaway data folder
/// (EXO_LAUNCHER_DATA_DIR), whose settings.json it reads. It opens every service's window over about:blank
/// without taking the front, on the screen at EXO_CHROME_PREVIEW_AT ("x,y"; else the primary), and YouTube Music's again
/// drawn at 100, 125, 150 and 200% whatever that screen's scale; writes chrome-preview-ready in the data folder once
/// they have drawn, and exits when they are all closed or after two minutes.
/// </summary>
internal static class ChromePreview
{
    internal static bool Wanted =>
        Environment.GetEnvironmentVariable("EXO_CHROME_PREVIEW") == "1" &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable));

    internal static async void Show()
    {
        try
        {
            var display = DisplayArea.Primary;
            if (Environment.GetEnvironmentVariable("EXO_CHROME_PREVIEW_AT")?.Split(',') is [var x, var y] &&
                int.TryParse(x, out var atX) && int.TryParse(y, out var atY))
                display = DisplayArea.GetFromPoint(new PointInt32(atX, atY), DisplayAreaFallback.Primary);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(PathHelper.AppDataDir, "webview"), new CoreWebView2EnvironmentOptions());
            var open = 0;
            var windows = ServiceMarks.All.Keys.Select(service => (Service: service, Scale: 0.0))
                .Concat(new[] { 1.0, 1.25, 1.5, 2.0 }.Select(scale => (Service: "youtube", Scale: scale)));
            foreach (var (service, scale) in windows)
            {
                var mark = ServiceMarks.For(service);
                var view = new WebView2();
                var window = new Window { Title = scale == 0 ? mark.Name : $"{mark.Name} at {scale * 100:0}%" };
                ExoConnectedTheme.WindowContent(window, service, view);
                var (width, height) = (mark.Width, (double)mark.Height + ExoConnectedTheme.Band);
                var factor = 1.0;
                if (scale > 0)
                {
                    // The chrome drawn at this scale on whatever screen this is: laid out at its DIPs, enlarged by the Viewbox.
                    var root = (FrameworkElement)window.Content;
                    window.Content = null;
                    height = 300 + ExoConnectedTheme.Band;
                    root.Width = width;
                    root.Height = height;
                    window.Content = new Viewbox { Child = root };
                    factor = scale / ExoConnectedTheme.Place(window, width, height, display);
                }
                open++;
                window.Closed += (_, _) => { if (--open == 0) Application.Current.Exit(); };
                window.AppWindow.Show(activateWindow: false);
                ExoConnectedTheme.Place(window, width * factor, height * factor, display);
                await view.EnsureCoreWebView2Async(environment);
                view.CoreWebView2.Navigate("about:blank");
            }
            await Task.Delay(2000);
            File.WriteAllText(Path.Combine(PathHelper.AppDataDir, "chrome-preview-ready"), "");
            await Task.Delay(TimeSpan.FromMinutes(2));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Chrome preview: " + ex);
        }
        Application.Current.Exit();
    }
}
#endif
