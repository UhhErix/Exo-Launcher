namespace ExoLauncher.Services;

/// <summary>
/// devices.list: the Devices room's cards (<see cref="Peripherals"/>), each with where it meets the PC and the processor's
/// free USB connectors (<see cref="UsbPorts"/>), read only; devices.changed (no payload) once a device arrives or leaves
/// or Windows' default speakers or microphone change, from the first list on.
/// </summary>
public sealed partial class WebHostBridge
{
    private int _devicesWatched;
    private Action? _controllerKnownHandler;
    private Action? _devicesChangedHandler;

    /// <summary>From Exo's start: devices.changed for the room, and an Edge Exo calibrated gets its calibration back.</summary>
    private void WatchDevices()
    {
        if (Interlocked.Exchange(ref _devicesWatched, 1) != 0) return;
        _controllerKnownHandler = () =>
        {
            PostEvent("devices.changed", new { });
            ReapplyCalibrationsSoon();
        };
        _devicesChangedHandler = () =>
        {
            PostEvent("devices.changed", new { });
            HideControllerSoundSoon();
            ReapplyCalibrationsSoon();
        };
        Peripherals.ControllerKnown += _controllerKnownHandler;
        Peripherals.Watch(_devicesChangedHandler);
        HideControllerSoundSoon();
        ReapplyCalibrationsSoon();
    }

    private void UnwatchDevices()
    {
        if (Interlocked.Exchange(ref _devicesWatched, 0) == 0) return;
        if (Interlocked.Exchange(ref _controllerKnownHandler, null) is { } controllerKnown)
            Peripherals.ControllerKnown -= controllerKnown;
        if (Interlocked.Exchange(ref _devicesChangedHandler, null) is { } devicesChanged)
            Peripherals.Unwatch(devicesChanged);
    }

    /// <summary>
    /// audio.read: the output's and microphone's format, volume and mute as Windows has them, and how Exo Voice is set, for
    /// the audio page (asked again every few seconds only while it shows). Read only.
    /// </summary>
    private static AudioDetails.Endpoint? ReadAudioEndpoint(string id)
    {
        try { return AudioDetails.Read(id); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException)
        {
            Helpers.AppLog.Warn("Audio: Windows didn't answer: " + ex.GetType().Name);
            return null;
        }
    }

    private object AudioRead(System.Text.Json.JsonElement p, bool hasParams)
    {
        var s = _services.Settings.Current;
        string? Given(string name) => ReadString(p, hasParams, name) is { Length: > 0 } id && _audioIds.ContainsKey(id) ? id : null;
        return new
        {
            ok = true,
            output = Given("output") is { } output ? ReadAudioEndpoint(output) : null,
            mic = Given("mic") is { } mic ? ReadAudioEndpoint(mic) : null,
            voice = new
            {
                custom = s.DiscordInputProfile == "custom",
                noise = !s.DiscordNoiseSuppression ? "off" : s.DiscordNoiseSuppressionKind,
                pushToTalk = s.DiscordPushToTalk,
                sensitivity = s.DiscordAutoSensitivity ? (int?)null : s.DiscordSensitivity,
                focus = s.DiscordVoiceFocus,
            },
        };
    }

    /// <summary>The audio endpoints devices.list has given the page: the only ones its audio calls may name.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _audioIds = new(StringComparer.OrdinalIgnoreCase);

    private string? GivenAudio(System.Text.Json.JsonElement p, bool hasParams) => ReadString(p, hasParams, "id") is { Length: > 0 } id && _audioIds.ContainsKey(id) ? id : null;

    /// <summary>A Windows audio call that answers for itself: a device can go at any moment.</summary>
    private static object AudioCall(Func<object> call)
    {
        try { return call(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException)
        {
            Helpers.AppLog.Warn("Audio: Windows didn't answer: " + ex.GetType().Name);
            return new { ok = false, message = "Windows didn't answer. Try again." };
        }
    }

    /// <summary>audio.formats: the formats the device really takes, asked of it, and which it's on. Changes nothing.</summary>
    private object AudioFormats(System.Text.Json.JsonElement p, bool hasParams) => AudioCall(() =>
        GivenAudio(p, hasParams) is { } id && AudioDetails.ListFormats(id) is { } offers
            ? new { ok = true, current = offers.Current, formats = offers.Formats }
            : new { ok = false, current = (string?)null, formats = (IReadOnlyList<AudioDetails.Offer>)[] });

    /// <summary>audio.setVolume: the endpoint's volume in Windows, 0 to 100, for an id the page was given.</summary>
    private object AudioSetVolume(System.Text.Json.JsonElement p, bool hasParams) => AudioCall(() =>
    {
        if (GivenAudio(p, hasParams) is not { } id || AudioDetails.Level(ReadDouble(p, hasParams, "volume")) is not { } volume) return new { ok = false, message = "Not a volume Exo knows." };
        return AudioDetails.SetVolume(id, volume) ? new { ok = true, message = (string?)null } : new { ok = false, message = (string?)"Windows didn't take it. Try again." };
    });

    /// <summary>audio.setMute: the endpoint muted or not, for an id the page was given.</summary>
    private object AudioSetMute(System.Text.Json.JsonElement p, bool hasParams) => AudioCall(() =>
    {
        if (GivenAudio(p, hasParams) is not { } id || ReadBool(p, hasParams, "muted") is not { } muted) return new { ok = false, message = "Not a device Exo knows." };
        return AudioDetails.SetMute(id, muted) ? new { ok = true, message = (string?)null } : new { ok = false, message = (string?)"Windows didn't take it. Try again." };
    });

    /// <summary>
    /// audio.setFormat: a format from those the device is offering right now, for an id the page was given, read back to say
    /// whether it took. Allowed during a game or a call, the owner's choice: the device's sound restarts for a moment.
    /// </summary>
    private object AudioSetFormat(System.Text.Json.JsonElement p, bool hasParams) => AudioCall(() =>
    {
        var key = ReadString(p, hasParams, "format");
        if (GivenAudio(p, hasParams) is not { } id || AudioDetails.ListFormats(id)?.Formats.FirstOrDefault(o => o.Key == key) is not { } offer)
            return new { ok = false, message = "Not a format this device takes." };
        var failed = AudioDetails.SetFormat(id, offer);
        if (failed is null) Helpers.AppLog.Info("Audio: a device's format changed, and it reads back the same.");
        else Helpers.AppLog.Warn("Audio: a device's format didn't change: " + failed);
        return new { ok = failed is null, message = failed };
    });

    private object DevicesList()
    {
        WatchDevices();
        var cards = Peripherals.List(() => _services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0);
        foreach (var card in cards.Where(c => c.Kind == "audio")) _audioIds[card.Id] = 0;
        try
        {
            var (ports, cpu) = UsbPorts.Read(cards);
            return new { ok = true, devices = cards.Select(c => Labbed(ports.TryGetValue(c.Id, out var port) ? c with { Port = port } : c)).ToList(), cpuPorts = cpu };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The cards stand without their ports.
            Helpers.AppLog.Warn("Devices: ports not read: " + ex.Message);
            return new { ok = true, devices = cards.Select(Labbed).ToList(), cpuPorts = (UsbPorts.CpuPorts?)null };
        }
    }

    private static Peripherals.Peripheral Labbed(Peripherals.Peripheral card) =>
        card.Kind == "controller" ? card with { Lab = ControllerLab.FamilyOf(card) is not null }
        : card.Kind == "keyboard" ? card with { Lab = WootingKeyboard.IsSupported(card) }
        : card;
}
