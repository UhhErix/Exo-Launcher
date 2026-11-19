using ExoCapture.Encode;
using ExoCapture.Gpu;
using ExoCapture.Media;

namespace ExoCapture;

/// <summary>Command-line checks of what Exo Capture writes and what this PC offers it, for development and the release checks.</summary>
internal static class SelfTest
{
    /// <summary>Each clip read back: its tracks by Exo Capture's reader, and every picture through Windows' own decoder.</summary>
    internal static int Check(string[] paths)
    {
        var failed = 0;
        using var gpu = GpuDevice.Create();
        foreach (var path in paths)
        {
            try
            {
                var mp4 = Mp4File.Read(path);
                var video = mp4.Video ?? throw new CaptureException("no video track");
                using var decoder = new VideoDecoder(gpu, path);
                var frames = 0;
                while (decoder.Next() is not null) frames++;
                var tracks = string.Join(", ", mp4.Tracks.Select(t => $"{t.Info.Format} {t.Info.Name} {t.Samples.Count}"));
                Console.WriteLine($"{path}: {video.Info.Width}x{video.Info.Height}, {mp4.DurationMs / 1000:0.00} s, {frames} of {video.Samples.Count} frames decoded; {tracks}");
                if (frames < video.Samples.Count * 0.98) { failed++; Console.WriteLine("  FAIL: frames missing"); }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"{path}: FAIL {ex.Message}");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>SSIM of two clips' first frames, frame by frame (a check of the shrink's picture comparison).</summary>
    internal static int Compare(string a, string b)
    {
        var gpu = GpuDevice.Shared();
        var info = Mp4File.Read(a).Video!.Info;
        var w = Math.Min(640, info.Width) & ~1;
        var h = Math.Max(2, (int)(w * (double)info.Height / info.Width)) & ~1;
        var picks = new HashSet<int>(Enumerable.Range(0, 30));
        var left = Edit.Transcode.Frames(gpu, a, w, h, picks, CancellationToken.None);
        var right = Edit.Transcode.Frames(gpu, b, w, h, picks, CancellationToken.None);
        foreach (var i in picks.Order())
            if (left.TryGetValue(i, out var x) && right.TryGetValue(i, out var y))
                Console.WriteLine($"{i}: {Edit.Ssim.Compare(x, y, w, h):0.0000}" + (i > 0 && right.TryGetValue(i - 1, out var prev) ? $" (against the frame before: {Edit.Ssim.Compare(x, prev, w, h):0.0000})" : ""));
        return 0;
    }

    /// <summary>
    /// OBS's hook in a test app (never a game with anti-cheat: the same refusals apply): the window of a program in
    /// <paramref name="folder"/>, its frames for three seconds, the last saved as <paramref name="png"/>.
    /// </summary>
    internal static int Hook(string folder, string png)
    {
        var window = GameWindow.Find(folder);
        if (window == 0) { Console.WriteLine("no window from that folder"); return 1; }
        if (Capture.HookSource.Refusal(window, Native.Win32.ImagePath(Native.Win32.ProcessOf(window))) is { } why) { Console.WriteLine("refused: " + why); return 1; }
        var gpu = GpuDevice.ForMonitor(Native.Win32.MonitorFromWindow(window, Native.Win32.MONITOR_DEFAULTTONEAREST));
        using var hook = Capture.HookSource.Start(gpu, window, Path.Combine(AppContext.BaseDirectory, "obs"), 60);
        var frames = 0;
        Capture.Frame? last = null;
        var until = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < until)
        {
            if (hook.Latest() is { } f) { frames++; last = f; }
            Thread.Sleep(16);
        }
        Console.WriteLine($"hook: {frames} frames, {hook.Width}x{hook.Height}, ended: {hook.Ended ?? "no"}");
        if (last is { } frame)
        {
            var pixels = gpu.Read(frame.Texture, out var w, out var h, out var bpp);
            if (bpp == 4) Imaging.SavePng(png, pixels, w, h);
            Console.WriteLine("saved " + png + $" ({frame.Texture.Description.Format})");
        }
        return frames > 0 ? 0 : 1;
    }

    internal static int Window(string folder)
    {
        var w = GameWindow.Find(folder);
        var m = Native.Win32.MonitorFromWindow(w, Native.Win32.MONITOR_DEFAULTTONEAREST);
        var b = Native.Win32.FrameBounds(w);
        var r = Native.Win32.MonitorInfo(m).Monitor;
        Console.WriteLine($"window {w:x} {Native.Win32.Title(w)} bounds {b?.Left},{b?.Top},{b?.Right},{b?.Bottom} monitor {r.Left},{r.Top},{r.Right},{r.Bottom} fills {GameWindow.FillsMonitor(w, m)}");
        return 0;
    }

    internal static int Encoders()
    {
        using var gpu = GpuDevice.Create();
        Console.WriteLine("Graphics card: " + gpu.Name);
        foreach (var codec in Enum.GetValues<Codec>())
            foreach (var hardware in new[] { true, false })
                foreach (var c in VideoEncoder.Find(codec, gpu.Luid, hardware))
                    Console.WriteLine($"{codec} {(hardware ? "card" : "processor")}: {c.Name}{(VideoEncoder.Playable(codec) ? "" : " (no decoder here)")}");
        return 0;
    }
}
