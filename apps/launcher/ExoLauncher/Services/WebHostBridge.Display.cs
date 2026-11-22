using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private static readonly TimeSpan DisplayCacheTtl = TimeSpan.FromSeconds(15);
    private readonly Lock _displayGate = new();
    private TtlReadCache<object>? _displayCache;

    private TtlReadCache<object> DisplayCache => _displayCache ??= new(
        DisplayCacheTtl,
        () => ReadDisplayStateAsync(null),
        fingerprint: ReadDisplaySignature);

    internal static string ReadDisplaySignature() =>
        BuildDisplaySignature(WindowsHdr.PrimaryGdiName(), WindowsHdr.All());

    internal static string BuildDisplaySignature(string? primary, IReadOnlyList<WindowsHdr.Screen> screens)
    {
        var head = primary ?? "";
        var sb = new System.Text.StringBuilder(head.Length + screens.Count * 96);
        sb.Append(head);
        foreach (var s in screens)
        {
            sb.Append('|').Append(s.Adapter.HighPart).Append(':').Append(s.Adapter.LowPart)
              .Append(':').Append(s.Target).Append(':').Append(s.Source)
              .Append(':').Append(s.GdiName).Append(':').Append(s.DevicePath)
              .Append(':').Append(s.Supported ? '1' : '0').Append(s.Enabled ? '1' : '0')
              .Append(':').Append(s.PixelRate).Append(':').Append(BitConverter.DoubleToInt64Bits(s.RefreshHz))
              .Append(':').Append(s.Connector);
        }
        return sb.ToString();
    }

    internal static async Task<MonitorDdc.Levels?[]> ReadAllDdcAsync(
        IReadOnlyList<string> gdiNames, Func<string, MonitorDdc.Levels?> reader)
    {
        if (gdiNames.Count == 0) return [];
        if (gdiNames.Count == 1) return [await Task.Run(() => reader(gdiNames[0])).ConfigureAwait(false)];
        var tasks = new Task<MonitorDdc.Levels?>[gdiNames.Count];
        for (var i = 0; i < gdiNames.Count; i++)
        {
            var gdi = gdiNames[i];
            tasks[i] = Task.Run(() => reader(gdi));
        }
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>One active screen: its GPU, the refresh rates and colors it takes, and what its link carries now.</summary>
    private sealed record DisplayScreen(string Gdi, string Name, bool Main, bool Hdr, string? Vendor, uint? NvId, uint Target,
        int Refresh, int[] Refreshes, OutputColor? Color, bool Auto, List<OutputColor> Supported, MonitorCaps? Caps, WindowsHdr.Screen Raw,
        int? Scaling = null, bool? Override = null);

    /// <summary>
    /// System > Display: every screen's refresh rate and output color (format, depth, range), and
    /// the best its link carries, and on NVIDIA its scaling. "set" applies a change (or "best", or
    /// "auto": the driver's own color settings) and keeps it at once. "adjust" sets the color sliders
    /// (<see cref="SetColor"/>), which apply at once too.
    /// </summary>
    private async Task<object> GpuDisplayAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action") ?? "state";
        var force = ReadBool(p, hasParams, "force") == true;
        if (action == "state")
            return await DisplayCache.GetAsync(force, ct).ConfigureAwait(false);

        var affectsProfile = action is "set" or "video" or "override";
        var gen = DisplayCache.BeginMutation();
        if (affectsProfile) _profileCache?.Invalidate();
        var message = await Task.Run(() => action switch
        {
            "set" => SetDisplay(p, hasParams),
            "adjust" => RunLocked(() => SetColor(p, hasParams)),
            "video" => SetVideo(p, hasParams),
            "override" => SetScalingOverride(p, hasParams),
            "ddc" => RunLocked(() => SetDdc(p, hasParams)),
            _ => (string?)null,
        }, ct).ConfigureAwait(false);
        var state = await ReadDisplayStateAsync(message).ConfigureAwait(false);
        if (message is null) DisplayCache.CompleteMutation(gen, state);
        if (affectsProfile) _profileCache?.Invalidate();
        return state;
    }

    private string? RunLocked(Func<string?> work)
    {
        lock (_displayGate) return work();
    }

    private Task<object> ReadDisplayStateAsync(string? message) => Task.Run(async () =>
    {
        var primary = WindowsHdr.PrimaryGdiName();
        var rawScreens = WindowsHdr.All();
        var ddcTask = ReadAllDdcAsync(rawScreens.Select(s => s.GdiName).ToArray(), MonitorDdc.Read);
        var screens = rawScreens.Select(screen => ReadScreen(screen, primary)).ToList();
        var nvidia = screens.FirstOrDefault(screen => screen.NvId is not null)?.Gdi;
        var videoHdr = nvidia is null ? null : NvidiaVideo.Read(nvidia, NvidiaVideo.VideoHdr);
        var superRes = nvidia is null ? null : NvidiaVideo.Read(nvidia, NvidiaVideo.SuperResolution);
        var ddcLevels = await ddcTask.ConfigureAwait(false);
        return (object)new
        {
            ok = message is null,
            message,
            displays = screens.Select((screen, index) =>
            {
                var best = BestFor(screen);
                var ddc = index < ddcLevels.Length ? ddcLevels[index] : null;
                return new
                {
                    id = DisplayId(screen.Gdi),
                    name = DisplayOutput.ScreenName(screen.Name, screen.Raw.DevicePath, index + 1),
                    main = screen.Main,
                    hdr = screen.Hdr,
                    hdrSupported = screen.Raw.Supported,
                    vendor = screen.Vendor,
                    refresh = screen.Refresh,
                    refreshLabel = DisplayOutput.FormatRefreshLabel(screen.Raw.RefreshHz, screen.Refresh),
                    refreshes = screen.Refreshes,
                    color = screen.Color,
                    auto = screen.Auto,
                    // The color in use stays listed so the page can show it.
                    supported = screen.Color is { } now && !screen.Supported.Contains(now) ? [.. screen.Supported, now] : screen.Supported,
                    best = new { refresh = best.Refresh, color = best.Color, why = best.Why, scaling = best.Scaling is not null, over = best.Override is not null, videoHdr = WantsVideoHdr(screen.Hdr, screen.NvId, videoHdr) },
                    // In use only when every part matches: refresh, color, on NVIDIA no scaling on the GPU with the override
                    // on, and on a screen running HDR, RTX Video HDR on.
                    isBest = best.Refresh == screen.Refresh && (best.Color is null || best.Color == screen.Color)
                        && (best.Scaling is null || best.Scaling == screen.Scaling) && (best.Override is null || best.Override == screen.Override)
                        && !(WantsVideoHdr(screen.Hdr, screen.NvId, videoHdr) && videoHdr == 0),
                    adjust = ColorState(screen),
                    ddc = ddc is null ? null : new
                    {
                        brightness = ddc.Brightness,
                        brightnessMin = ddc.BrightnessMin,
                        brightnessMax = ddc.BrightnessMax,
                        contrast = ddc.Contrast,
                        contrastMin = ddc.ContrastMin,
                        contrastMax = ddc.ContrastMax,
                    },
                    scaling = screen.Scaling is { } code
                        ? NvidiaScaling.FromCode(code) is { } s ? new { mode = s.Mode, gpu = s.OnGpu, over = screen.Override } : new { mode = "default", gpu = true, over = screen.Override }
                        : null,
                };
            // Read here, off the UI thread: a lazy list would run these driver reads while the answer is serialized.
            }).ToList(),
            // RTX Video is global on the card: read through any screen it drives.
            video = nvidia is null ? null : new
            {
                superResolution = superRes,
                hdr = videoHdr,
            },
        };
    });

    /// <summary>
    /// A screen running Windows HDR on an RTX card that has RTX Video HDR: the best has it on, so video that isn't HDR
    /// (most of the web) is lifted into HDR instead of showing flat and grey there. Off a screen without HDR it does nothing.
    /// A screen the NVIDIA card doesn't drive (<paramref name="nvId"/> null: an Optimus laptop's panel, a screen on the
    /// built-in or the other maker's GPU) gets nothing: NVIDIA's video calls need a display on the card.
    /// </summary>
    internal static bool WantsVideoHdr(bool hdr, uint? nvId, int? videoHdr) => hdr && nvId is not null && videoHdr is not null;

    /// <summary>
    /// The best a screen takes: its highest usable refresh rate (<see cref="DisplayOutput.BestRefresh"/>), then the deepest
    /// color the link carries at that rate, and where the driver has them (NVIDIA; AMD and Intel scaling isn't in Exo)
    /// No scaling performed on the GPU, overriding what games ask for: the picture is never stretched or blurred.
    /// Scaling and Override are null where the driver doesn't say.
    /// The refresh rate comes first and the color fits it, never the other way: a smooth refresh is what a game feels,
    /// and an SDR desktop at 8-bit with the GPU's dithering shows no banding, so 10-bit is worth only what the link has
    /// left at the best rate (1440p 170 Hz over DisplayPort 1.2 has room for 8-bit only). Colors are read at the current
    /// rate; at the best one Exo keeps only those the EDID's HDMI limit and the link's learned limit
    /// (<see cref="DisplayLink.Memory"/>) still allow at the pixel rate it scales to, and Use checks again once it's there.
    /// </summary>
    private static (int Refresh, OutputColor? Color, string? Why, int? Scaling, bool? Override) BestFor(DisplayScreen screen)
    {
        var refresh = DisplayOutput.BestRefresh(screen.Main, screen.Refreshes, screen.Refresh);
        var rate = screen.Refresh > 0 ? screen.Raw.PixelRate * refresh / screen.Refresh : 0;
        var colors = refresh == screen.Refresh ? screen.Supported : screen.Supported.Where(color => LinkTakes(screen.Raw, screen.Caps, color, rate)).ToList();
        var (color, why) = DisplayOutput.BestColor(colors, screen.Refresh, screen.Caps?.PanelBpc);
        if (refresh != screen.Refresh && screen.Supported.Count > 0)
            why = $"Color checked at {screen.Refresh} Hz; Exo checks it again at {refresh} Hz when you use it." + (why is null ? "" : " " + why);
        return (refresh, color, why, screen.Scaling is null ? null : NvidiaScaling.ToCode(new("aspect", true)), screen.Override is null ? null : true);
    }

    /// <summary>"Override the scaling mode set by games and programs" (NVIDIA): applies at once, nothing on screen changes until a game asks.</summary>
    private static string? SetScalingOverride(JsonElement p, bool hasParams)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        var id = ReadString(p, hasParams, "display");
        var raw = WindowsHdr.All().FirstOrDefault(screen => DisplayId(screen.GdiName) == id);
        if (raw is null || NvidiaDisplay.DisplayId(raw.GdiName) is not { } nvId) return "That screen isn't on an NVIDIA GPU.";
        return ReadBool(p, hasParams, "override") is { } on ? NvidiaScaling.WriteOverride(nvId, on) : null;
    }

    /// <summary>
    /// RTX Video (NVIDIA): Super resolution 0 off, 1-4 quality, 5 Auto; HDR 0 off, 5 on. Video
    /// processing only, so it applies at once without the countdown. A choice that took is kept, so a driver install that
    /// resets it gets it back (<see cref="DriverRestore"/>).
    /// </summary>
    private string? SetVideo(JsonElement p, bool hasParams)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        if (NvidiaVideo.AnyScreen() is not { } gdi) return "No screen runs on an NVIDIA GPU.";
        if (ReadDouble(p, hasParams, "superResolution") is { } sr)
        {
            if (sr is not (>= 0 and <= 5)) return "Exo has no such quality.";
            return NvidiaVideo.Write(gdi, NvidiaVideo.SuperResolution, (int)sr) ?? KeepVideo(s => s.RtxVideoSuperResolution = (int)sr);
        }
        if (ReadBool(p, hasParams, "hdr") is { } hdr)
            return NvidiaVideo.Write(gdi, NvidiaVideo.VideoHdr, hdr ? 5 : 0) ?? KeepVideo(s => s.RtxVideoHdr = hdr);
        return null;
    }

    /// <summary>Keeps the RTX Video choice, held for the driver installed now. Null: it's on the screen either way.</summary>
    private string? KeepVideo(Action<Models.AppSettings> choice)
    {
        var driver = NvidiaDriverService.Installed();
        try { _services.Settings.UpdateProfile(settings => { choice(settings); settings.RtxVideoDriver = driver; }); }
        catch (Exception ex) { AppLog.Warn("Display: RTX Video choice not kept: " + ex.Message); }
        return null;
    }

    /// <summary>
    /// Applies one change and keeps it; null when it went through as asked, else what to tell the user. No trial and no
    /// Keep: Exo offers only the modes the screen reports it takes, and a trial waiting on Keep locked every other row
    /// until it was pressed, so changes went one at a time.
    /// </summary>
    private string? SetDisplay(JsonElement p, bool hasParams)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        lock (_displayGate)
        {
            var id = ReadString(p, hasParams, "display");
            var raw = WindowsHdr.All().FirstOrDefault(screen => DisplayId(screen.GdiName) == id);
            if (raw is null) return "That screen is not connected.";

            var hdrChanged = false;
            if (ReadBool(p, hasParams, "hdr") is { } wantHdr)
            {
                if (!raw.Supported) return "This screen does not support HDR.";
                if (wantHdr != raw.Enabled)
                {
                    if (!WindowsHdr.Set(raw, wantHdr)) return "Windows did not change HDR on this screen.";
                    hdrChanged = true;
                    WaitForHdrToggle(raw.DevicePath, wantHdr);
                    raw = Reread(raw);
                }
            }

            var before = ReadScreen(raw, WindowsHdr.PrimaryGdiName());
            var best = ReadBool(p, hasParams, "best") == true;

            var refresh = best ? BestFor(before).Refresh : (int?)ReadDouble(p, hasParams, "refresh") ?? before.Refresh;
            if (refresh != before.Refresh && !before.Refreshes.Contains(refresh)) return $"This screen has no {refresh} Hz mode.";
            if (refresh != before.Refresh && DisplayOutput.SetRefresh(before.Gdi, refresh) is { } refreshError) return refreshError;
            // Poll with progressive backoff (up to 3 s) until the driver settles on the new mode after link retrain.
            if (refresh != before.Refresh) DisplayOutput.WaitForRefreshSettle(() => DisplayOutput.CurrentRefresh(before.Gdi), refresh, Thread.Sleep, 3000);
            var now = refresh != before.Refresh ? ReadScreen(Reread(raw), WindowsHdr.PrimaryGdiName()) : before;

            OutputColor? wanted = null;
            string? message = null;
            var auto = ReadBool(p, hasParams, "auto") == true;
            if (auto && now.Color is { } keepColor)
                wanted = keepColor;
            else if (best)
                wanted = DisplayOutput.BestColor(now.Supported, refresh, now.Caps?.PanelBpc).Color;
            else if (ReadString(p, hasParams, "format") is { } format && now.Color is { } current)
            {
                var rgb = format == "rgb";
                wanted = new OutputColor(format, (int?)ReadDouble(p, hasParams, "bpc") ?? current.Bpc, rgb && (ReadBool(p, hasParams, "full") ?? current.Full));
                if (!now.Supported.Contains(wanted))
                    message = $"The screen doesn't take {DisplayOutput.Label(wanted)} at {refresh} Hz.";
            }

            var colorChanged = false;
            if (wanted is not null && message is null && (auto ? !now.Auto : wanted != now.Color || now.Auto))
            {
                message = WriteColor(now, wanted, auto);
                // Right after a refresh change the driver can still list the old mode's colors: read what it
                // takes at the new rate and pick again once.
                if (message is not null && message != DisplayOutput.NotKept && best && refresh != before.Refresh)
                {
                    now = ReadScreen(Reread(raw), WindowsHdr.PrimaryGdiName());
                    var retry = DisplayOutput.BestColor(now.Supported, refresh, now.Caps?.PanelBpc).Color;
                    if (retry is null || (retry == now.Color && !now.Auto)) { message = null; wanted = null; }
                    else if (retry != wanted) { wanted = retry; message = WriteColor(now, retry); }
                }
                if (message == DisplayOutput.NotKept) (message, wanted) = NotKept(raw, now, wanted!, auto, best, refresh);
                colorChanged = message is null && wanted is not null;
                if (!colorChanged) wanted = null;
            }

            // Scaling (NVIDIA): a mode and where it runs; integer scaling only runs on the GPU. Best: no scaling on the GPU.
            int? scalingTo = null;
            var bestFor = best ? BestFor(now) : default;
            if (message is null && now.NvId is { } nvId && now.Scaling is { } scalingNow)
            {
                int? code = null;
                if (best) code = bestFor.Scaling;
                else if (ReadString(p, hasParams, "scaling") is { } mode)
                {
                    var onGpu = mode == "integer" || (ReadBool(p, hasParams, "scalingGpu") ?? NvidiaScaling.FromCode(scalingNow)?.OnGpu ?? true);
                    code = NvidiaScaling.ToCode(new NvidiaScaling.Scaling(mode, onGpu));
                    if (code is null) return "Exo has no such scaling.";
                }
                if (code is { } to && to != scalingNow)
                {
                    message = NvidiaScaling.Write(nvId, to);
                    scalingTo = to;
                }
            }
            // Best also turns on "Override the scaling set by games" (NVIDIA).
            var overrideOn = false;
            if (message is null && best && bestFor.Override == true && now.Override == false && now.NvId is { } overId)
            {
                message = NvidiaScaling.WriteOverride(overId, true);
                overrideOn = message is null;
            }
            // Best also turns on RTX Video Super Resolution (Auto) and RTX Video HDR on HDR displays.
            var videoHdrOn = false;
            var videoSrOn = false;
            if (message is null && best && NvidiaVideo.AnyScreen() is { } videoGdi)
            {
                var currentSr = NvidiaVideo.Read(videoGdi, NvidiaVideo.SuperResolution);
                if (currentSr == 0)
                {
                    message = NvidiaVideo.Write(videoGdi, NvidiaVideo.SuperResolution, 5) ?? KeepVideo(s => s.RtxVideoSuperResolution = 5);
                    videoSrOn = message is null;
                    if (videoSrOn) AppLog.Info("Display: RTX Video Super Resolution Auto on.");
                }
                var currentVideoHdr = NvidiaVideo.Read(videoGdi, NvidiaVideo.VideoHdr);
                if (message is null && WantsVideoHdr(now.Hdr, now.NvId, currentVideoHdr) && currentVideoHdr == 0)
                {
                    message = NvidiaVideo.Write(videoGdi, NvidiaVideo.VideoHdr, 5) ?? KeepVideo(s => s.RtxVideoHdr = true);
                    videoHdrOn = message is null;
                    if (videoHdrOn) AppLog.Info("Display: RTX Video HDR on, for a screen running HDR.");
                }
            }
            if (refresh == before.Refresh && !colorChanged && scalingTo is null && !overrideOn && !videoHdrOn && !videoSrOn)
            {
                if (hdrChanged) AppLog.Info($"Display: HDR {(raw.Enabled ? "on" : "off")} on {DisplayId(before.Gdi)}.");
                return message;
            }
            // A new refresh rate is saved as Windows' own, so it holds after a restart.
            if (refresh != before.Refresh) message ??= DisplayOutput.SaveRefresh(before.Gdi);
            AppLog.Info($"Display: changed {DisplayId(before.Gdi)} to {refresh} Hz{(wanted is null ? "" : ", " + DisplayOutput.Label(wanted))}.");
            return message;
        }
    }

    /// <summary>Polls Windows HDR state briefly until the toggle settles (up to 1.5 s) so the reply reflects the new state.</summary>
    private static void WaitForHdrToggle(string devicePath, bool wantOn)
    {
        var until = Environment.TickCount64 + 1500;
        while (Environment.TickCount64 < until)
        {
            var reported = WindowsHdr.All().FirstOrDefault(s => string.Equals(s.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))?.Enabled;
            if (reported == wantOn) return;
            Thread.Sleep(150);
        }
    }

    private static string? SetDdc(JsonElement p, bool hasParams)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        var id = ReadString(p, hasParams, "display");
        var raw = WindowsHdr.All().FirstOrDefault(screen => DisplayId(screen.GdiName) == id);
        if (raw is null) return "That screen is not connected.";
        if (ReadDouble(p, hasParams, "brightness") is { } brightness)
            return MonitorDdc.SetBrightness(raw.GdiName, (int)Math.Round(brightness));
        if (ReadDouble(p, hasParams, "contrast") is { } contrast)
            return MonitorDdc.SetContrast(raw.GdiName, (int)Math.Round(contrast));
        return null;
    }

    /// <summary>The screen as Windows has it now (its link's pixel rate changes with the refresh rate).</summary>
    private static WindowsHdr.Screen Reread(WindowsHdr.Screen screen) =>
        WindowsHdr.All().FirstOrDefault(item => item.GdiName == screen.GdiName) ?? screen;

    private static DisplayScreen ReadScreen(WindowsHdr.Screen screen, string? primary)
    {
        var (refresh, listed) = DisplayOutput.RefreshRates(screen.GdiName);
        var caps = WindowsHdr.Caps(screen);
        var refreshes = DisplayOutput.OfferedRates(listed, refresh, caps);
        OutputColor? color = null;
        var auto = false;
        List<OutputColor> supported = [];
        string? vendor = null;
        int? scaling = null;
        bool? over = null;
        var nvId = NvidiaDisplay.DisplayId(screen.GdiName);
        if (nvId is { } id)
        {
            vendor = "nvidia";
            (color, auto, supported) = NvidiaDisplay.Read(id);
            scaling = NvidiaScaling.Read(id);
            over = NvidiaScaling.ReadOverride(id);
        }
        else if (AmdAdlx.Present && AmdAdlx.ReadDisplay(screen.Name) is { } amd)
        {
            vendor = "amd";
            (color, supported) = amd;
        }
        else if (IntelIgcl.Present && IntelIgcl.ReadDisplay(screen.Target) is { } intel)
        {
            vendor = "intel";
            (color, supported) = intel;
        }
        // The color running now proves the link carries it: a limit learned under it was wrong.
        if (color is { } running) DisplayLink.Memory.Shared.Ran(LinkKey(screen), running, screen.PixelRate, screen.Connector == "hdmi");
        var offered = DisplayOutput.Offered(supported, caps, screen.PixelRate, c => LinkTakes(screen, null, c, screen.PixelRate));
        LogRuledOut(screen, listed.Except(refreshes).Select(hz => $"{hz} Hz").Concat(supported.Except(offered).Where(c => c.Bpc <= 12).Select(DisplayOutput.Label)));
        return new DisplayScreen(screen.GdiName, screen.Name, string.Equals(screen.GdiName, primary, StringComparison.OrdinalIgnoreCase),
            screen.Enabled, vendor, nvId, screen.Target, refresh, refreshes, color, auto, offered, caps, screen, scaling, over);
    }

    private static string LinkKey(WindowsHdr.Screen screen) => DisplayLink.Memory.Key(screen.Connector, screen.DevicePath);

    /// <summary>
    /// Whether this screen's link carries a color at <paramref name="pixelRate"/>: the EDID's HDMI limit (with
    /// <paramref name="caps"/>), and nothing at or past a rate it failed to carry before (<see cref="DisplayLink.Memory"/>).
    /// </summary>
    private static bool LinkTakes(WindowsHdr.Screen screen, MonitorCaps? caps, OutputColor color, long pixelRate) =>
        caps?.Takes(color, pixelRate) != false && DisplayLink.Memory.Shared.Takes(LinkKey(screen), color, pixelRate, screen.Connector == "hdmi");

    /// <summary>
    /// A color the driver took but the screen doesn't run, because the link can't carry it at this mode (AMD's driver
    /// takes 10-bit at 1440p 170 Hz over DisplayPort 1.2 and quietly stays on 8-bit). Its rate is remembered as more
    /// than this link carries (<see cref="DisplayLink.Memory"/>), so it isn't offered again; Best goes on to the next
    /// best color the link still takes, and otherwise the color that ran before goes back. Nothing of this reaches the
    /// page as an error: the list simply no longer offers it. The line to show (null: none) and the color now set.
    /// </summary>
    private static (string? Message, OutputColor? Set) NotKept(WindowsHdr.Screen raw, DisplayScreen screen, OutputColor color, bool auto, bool best, int refresh)
    {
        var before = screen.Color;
        if (auto) return (null, null);
        for (var tries = 0; tries < 4; tries++)
        {
            var remembered = DisplayLink.Memory.Shared.Failed(LinkKey(screen.Raw), color, screen.Raw.PixelRate, screen.Raw.Connector == "hdmi");
            screen = ReadScreen(Reread(raw), WindowsHdr.PrimaryGdiName());
            AppLog.Info($"Display: {DisplayId(screen.Gdi)} ({screen.Raw.Connector}) took {DisplayOutput.Label(color)} at {screen.Refresh} Hz but runs {(screen.Color is { } now ? DisplayOutput.Label(now) : "something else")}"
                + (remembered ? $"; not offered again past {DisplayLink.Needs(color, screen.Raw.PixelRate, screen.Raw.Connector == "hdmi"):0.0} Gbps on this link." : "."));
            if (!best || DisplayOutput.BestColor(screen.Supported, refresh, screen.Caps?.PanelBpc).Color is not { } next || next == color || next == screen.Color) break;
            color = next;
            var message = WriteColor(screen, color);
            if (message != DisplayOutput.NotKept) return (message, message is null ? color : null);
        }
        if (before is null || screen.Color == before) return (null, null);
        var back = WriteColor(screen, before);
        AppLog.Info($"Display: {DisplayId(screen.Gdi)} put back to {DisplayOutput.Label(before)}: {back ?? "done"}.");
        return (back is null or DisplayOutput.NotKept ? null : back, null);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RuledOutLogged = new();

    /// <summary>Once per screen and list: what Windows or the driver listed that the screen's EDID rules out.</summary>
    private static void LogRuledOut(WindowsHdr.Screen screen, IEnumerable<string> values)
    {
        var line = string.Join(", ", values);
        if (line.Length > 0 && RuledOutLogged.TryAdd(screen.DevicePath + line, 0))
            AppLog.Info($"Display: not offered on {DisplayId(screen.GdiName)} ({screen.Name}), its EDID or its link doesn't take them: {line}.");
    }

    private static string? WriteColor(DisplayScreen screen, OutputColor color, bool auto = false) => screen.Vendor switch
    {
        "nvidia" when screen.NvId is { } id => NvidiaDisplay.Write(id, color, auto),
        "amd" => AmdAdlx.WriteDisplay(screen.Name, color),
        "intel" => IntelIgcl.WriteDisplay(screen.Target, color),
        _ => "Exo can't set the color on this screen's GPU.",
    };

    /// <summary>
    /// The screen's color sliders. NVIDIA: nvidia says whether "Use NVIDIA color settings" is on;
    /// brightness, contrast and gamma only while HDR is off. AMD: Custom Color, nvidia null. Null
    /// when the driver offers none.
    /// </summary>
    private object? ColorState(DisplayScreen screen)
    {
        if (screen.Vendor == "amd")
            return AmdAdlx.ReadColor(screen.Name) is { } amd ? new { nvidia = (bool?)null, controls = amd } : null;
        if (screen.Vendor != "nvidia" || NvidiaDisplay.ReadVibrance(screen.Gdi) is not { } dvc) return null;
        var hue = NvidiaDisplay.ReadHue(screen.Gdi);
        var saved = NvidiaColor.Parse(_services.Settings.Current.NvidiaColor.GetValueOrDefault(screen.Raw.DevicePath));
        var values = saved?.Values ?? NvidiaColor.Defaults(dvc.Default);
        List<ColorControl> controls = screen.Hdr ? [] :
        [
            new("brightness", "Brightness", values.Brightness, 0, 100, 1, 50, "%"),
            new("contrast", "Contrast", values.Contrast, 0, 100, 1, 50, "%"),
            new("gamma", "Gamma", values.Gamma, 30, 280, 1, 100, ""),
        ];
        controls.Add(new("vibrance", "Digital vibrance", dvc.Level, dvc.Min, dvc.Max, 1, dvc.Default, dvc is { Min: 0, Max: 100 } ? "%" : ""));
        if (hue is { } h) controls.Add(new("hue", "Hue", h.Angle, 0, 359, 1, h.Default, "°"));
        return new { nvidia = (bool?)(saved is not null), controls };
    }

    /// <summary>
    /// System > Display adjust: "key" and "value" set one slider (the first one turns "Use NVIDIA color
    /// settings" on); "nvidia" false hands color back to Windows and other apps (the ramp the screen had
    /// before, vibrance and hue at the driver's defaults); "reset" puts every slider to its normal value. No countdown: none
    /// of these can blank a screen. Null on success, else a plain line.
    /// </summary>
    private string? SetColor(JsonElement p, bool hasParams)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        var id = ReadString(p, hasParams, "display");
        var raw = WindowsHdr.All().FirstOrDefault(screen => DisplayId(screen.GdiName) == id);
        if (raw is null) return "That screen is not connected.";
        var reset = ReadBool(p, hasParams, "reset") == true;
        var key = ReadString(p, hasParams, "key");
        var value = (int?)ReadDouble(p, hasParams, "value");

        if (NvidiaDisplay.DisplayId(raw.GdiName) is null)
        {
            if (!AmdAdlx.Present || AmdAdlx.ReadColor(raw.Name) is not { } amd) return "Exo can't set color on this screen's GPU.";
            if (reset) return AmdAdlx.WriteColor(raw.Name, amd.Select(c => (Key: c.Key, Value: c.Normal)).ToList());
            return key is null || value is null ? null : AmdAdlx.WriteColor(raw.Name, [(key, value.Value)]);
        }

        if (NvidiaDisplay.ReadVibrance(raw.GdiName) is not { } dvc) return "The NVIDIA driver has no color settings for this screen.";
        var hue = NvidiaDisplay.ReadHue(raw.GdiName);
        var defaults = NvidiaColor.Defaults(dvc.Default, hue?.Default ?? 0);
        var saved = NvidiaColor.Parse(_services.Settings.Current.NvidiaColor.GetValueOrDefault(raw.DevicePath));
        var nvidia = ReadBool(p, hasParams, "nvidia");

        if (nvidia == false)
        {
            // Nothing of Exo's saved (vibrance or hue set by another app or an earlier driver): the driver's defaults alone.
            if (saved is not null && !SaveColor(raw.DevicePath, null)) return "Exo could not save that.";
            var restored = saved is null || WindowsHdr.WriteRamp(raw.GdiName, saved.Value.Before ?? DisplayOutput.Ramp(50, 50, 100));
            var message = NvidiaDisplay.SetVibrance(raw.GdiName, defaults.Vibrance) ?? (hue is null ? null : NvidiaDisplay.SetHue(raw.GdiName, defaults.Hue));
            if (saved is null) return message;
            AppLog.Info($"Display: other apps control color on {raw.GdiName}; ramp put back: {restored}.");
            return message ?? (restored || raw.Enabled ? null : "Windows did not take the screen's earlier gamma back.");
        }

        // The first slider moved hands color to NVIDIA ("Use NVIDIA color settings"), starting from what
        // the screen shows now: its vibrance and hue, a straight ramp, and the ramp it had kept to put back.
        var now = saved?.Values ?? defaults with { Vibrance = dvc.Level, Hue = hue?.Angle ?? 0 };
        var before = saved is { } kept ? kept.Before : WindowsHdr.ReadRamp(raw.GdiName);
        var next = reset ? defaults : key switch
        {
            "brightness" when value is { } v => now with { Brightness = Math.Clamp(v, 0, 100) },
            "contrast" when value is { } v => now with { Contrast = Math.Clamp(v, 0, 100) },
            "gamma" when value is { } v => now with { Gamma = Math.Clamp(v, 30, 280) },
            "vibrance" when value is { } v => now with { Vibrance = Math.Clamp(v, dvc.Min, dvc.Max) },
            "hue" when value is { } v => now with { Hue = Math.Clamp(v, 0, 359) },
            _ => nvidia == true && saved is null ? now : (NvidiaColor.Values?)null,
        };
        if (next is null) return nvidia == true ? null : "Exo has no such color setting.";

        if (NvidiaColor.Apply(raw, next) is { } error)
        {
            // Put the last good values back so the screen and the sliders agree.
            if (saved is { } old) NvidiaColor.Apply(raw, old.Values);
            return error;
        }
        return SaveColor(raw.DevicePath, NvidiaColor.Format(next, before)) ? null : "Exo could not save that.";
    }

    private bool SaveColor(string devicePath, string? stored)
    {
        try
        {
            _services.Settings.UpdateProfile(settings =>
            {
                if (stored is null) settings.NvidiaColor.Remove(devicePath);
                else settings.NvidiaColor[devicePath] = stored;
            });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Display: NVIDIA color not saved: " + ex.Message);
            return false;
        }
    }

    /// <summary>"\\.\DISPLAY1" is "DISPLAY1": the GDI name without its device prefix.</summary>
    private static string DisplayId(string gdiName) => gdiName.StartsWith(@"\\.\", StringComparison.Ordinal) ? gdiName[4..] : gdiName;
}
