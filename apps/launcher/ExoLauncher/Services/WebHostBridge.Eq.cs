using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// eq: Exo EQ on Devices › your audio (<see cref="ExoEq"/>), for the output the page shows (output: its endpoint id). Its
/// state, and: install (Equalizer APO from SourceForge, checked, one Windows prompt), devices (APO's Device Selector),
/// sound-settings (Windows' Sound, for audio enhancements), headphone (id, or null for none), sound, custom (ten graphic
/// gains), compare (on), on (on), game (game, sound or null), night (on, start, end, sync), lucid-off.
/// eq.headphones lists AutoEq's models (fetched the first time, then kept a week).
/// </summary>
public sealed partial class WebHostBridge
{
    // 1 while Equalizer APO's installer runs: two presses never start two installers.
    private static int _apoInstalling;
    private Action<string, GameRunState>? _eqGameStateChanged;

    /// <summary>The library game running now that has a sound of its own in Exo EQ.</summary>
    private void WatchEq()
    {
        ExoEq.RunningGame = () =>
        {
            var games = ExoEq.Current.Games;
            if (games.Count == 0) return null;
            var running = MainWindow.RunningLibraryGames();
            var library = _services.Library.PeekCachedLibrary();
            foreach (var id in running.Keys)
                if (games.ContainsKey(id) && library.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)) is { } game)
                    return (game.Id, game.Title);
            return null;
        };
        _eqGameStateChanged = (_, _) => _ = Task.Run(ExoEq.GamesChanged);
        _services.Launcher.GameRunStateChanged += _eqGameStateChanged;
    }

    private void UnwatchEq()
    {
        if (Interlocked.Exchange(ref _eqGameStateChanged, null) is { } handler)
            _services.Launcher.GameRunStateChanged -= handler;
        ExoEq.RunningGame = () => null;
    }

    internal static EqCorrection CorrectionOf(AutoEqCatalog.Headphone h, IReadOnlyList<EqFilter> filters) => new()
    {
        Id = h.Id, Name = h.Name, Source = h.Source, Form = h.Form, Filters = [.. filters.Select(EqBand.Of)],
    };

    private async Task<object> EqAsync(JsonElement p, bool hasParams)
    {
        var guid = EqualizerApo.GuidOf(ReadString(p, hasParams, "output")) ?? EqualizerApo.Playing();
        string? message = null;
        EqOutput Output(EqStore store)
        {
            if (!store.Outputs.TryGetValue(guid!, out var output)) store.Outputs[guid!] = output = new EqOutput();
            if (EqualizerApo.Read(guid!) is { } seen) output.Name = seen.Name;
            return output;
        }
        var needsOutput = "Exo can't tell which output Windows plays on. Plug it in, then try again.";
        switch (ReadString(p, hasParams, "action"))
        {
            case "install" when Interlocked.CompareExchange(ref _apoInstalling, 1, 0) == 0:
                try { message = await EqualizerApo.InstallAsync().ConfigureAwait(false); }
                finally { Volatile.Write(ref _apoInstalling, 0); }
                break;
            case "devices":
                message = EqualizerApo.OpenDeviceSelector();
                break;
            case "sound-settings":
                message = EqualizerApo.OpenSoundSettings();
                break;
            case "headphone" when guid is null:
            case "sound" when guid is null:
            case "custom" when guid is null:
                message = needsOutput;
                break;
            case "headphone":
                var id = ReadString(p, hasParams, "id");
                if (string.IsNullOrEmpty(id)) { message = ExoEq.Change(store => Output(store).Headphone = null); break; }
                var list = await AutoEqCatalog.ListAsync().ConfigureAwait(false);
                if (list?.FirstOrDefault(h => h.Id == id) is not { } picked) { message = "AutoEq's list didn't load. Check your connection, then try again."; break; }
                if (await AutoEqCatalog.FiltersAsync(picked).ConfigureAwait(false) is not { } filters) { message = $"{picked.Name}'s correction didn't download. Try again."; break; }
                message = ExoEq.Change(store => Output(store).Headphone = CorrectionOf(picked, filters));
                break;
            case "sound" when ExoEqSounds.Known(ReadString(p, hasParams, "sound")):
                var sound = ReadString(p, hasParams, "sound")!;
                message = ExoEq.Change(store => Output(store).Sound = sound);
                break;
            case "custom" when hasParams && p.TryGetProperty("gains", out var gains) && gains.ValueKind == JsonValueKind.Array:
                var values = gains.EnumerateArray().Take(ExoEqSounds.Graphic.Length).Select(g => g.ValueKind == JsonValueKind.Number ? Math.Clamp(g.GetDouble(), -12, 12) : 0).ToArray();
                message = ExoEq.Change(store =>
                {
                    store.Custom = [.. ExoEqSounds.Graphic.Select((hz, i) => new EqBand { Type = "PK", Hz = hz, Gain = Math.Round(i < values.Length ? values[i] : 0, 1), Q = 1.41 })];
                    Output(store).Sound = ExoEqSounds.Custom;
                });
                break;
            case "compare":
                message = ExoEq.Compare(ReadBool(p, hasParams, "on") == true);
                break;
            case "on":
                var on = ReadBool(p, hasParams, "on") == true;
                message = ExoEq.Change(store => store.On = on);
                break;
            case "game" when ReadString(p, hasParams, "game") is { Length: > 0 } game:
                var gameSound = ReadString(p, hasParams, "sound");
                message = ExoEq.Change(store =>
                {
                    if (ExoEqSounds.Known(gameSound)) store.Games[game] = gameSound!; else store.Games.Remove(game);
                    // A game's sound plays on the output in use, which needs its own block for that.
                    if (guid is not null) Output(store);
                });
                ExoEq.GamesChanged();
                break;
            case "night":
                var nightOn = ReadBool(p, hasParams, "on");
                var sync = ReadBool(p, hasParams, "sync");
                var start = ReadString(p, hasParams, "start");
                var end = ReadString(p, hasParams, "end");
                if (start is not null && ExoEqSounds.Clock(start) is null || end is not null && ExoEqSounds.Clock(end) is null) { message = "Pick a time."; break; }
                message = ExoEq.Change(store =>
                {
                    if (nightOn is { } night) store.Night.On = night;
                    if (sync is { } follow) store.Night.Sync = follow;
                    if (start is not null) store.Night.Start = start;
                    if (end is not null) store.Night.End = end;
                    if (guid is not null) Output(store);
                });
                break;
            case "lucid-off":
                message = ExoEq.LucidOff();
                break;
        }
        return EqState(guid, message);
    }

    private object EqState(string? guid, string? message)
    {
        var store = ExoEq.Current;
        var folder = EqualizerApo.InstallFolder();
        var output = guid is null ? null : EqualizerApo.Read(guid);
        var own = guid is not null && store.Outputs.TryGetValue(guid, out var saved) ? saved : null;
        var chain = guid is null ? null : ExoEq.ChainFor(guid);
        var library = _services.Library.PeekCachedLibrary();
        var night = store.Night.Sync ? ExoEq.WindowsNightLight() : null;
        string Clock(TimeSpan time) => time.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
        return new
        {
            ok = message is null,
            message = message ?? ExoEq.Problem,
            apo = new
            {
                installed = folder is not null,
                installing = Volatile.Read(ref _apoInstalling) == 1,
                version = EqualizerApo.Version(),
                onOutput = output?.Apo == true,
                restart = output?.Restart == true,
                effectsOff = output?.EffectsOff == true,
                outputs = folder is null ? 0 : EqualizerApo.OutputsWithApo(),
                installerVersion = EqualizerApo.Installer.Version,
            },
            on = store.On,
            comparing = ExoEq.Comparing,
            output = guid is null ? null : new { name = output?.Name ?? own?.Name },
            headphone = own?.Headphone is { } h ? new { id = h.Id, name = h.Name, source = h.Source, form = h.Form } : null,
            sound = own?.Sound ?? ExoEqSounds.Flat,
            custom = ExoEqSounds.Graphic.Select((hz, i) => new { hz, gain = store.Custom.FirstOrDefault(b => Math.Abs(b.Hz - hz) < 0.5)?.Gain ?? 0 }).ToArray(),
            preamp = chain is null ? 0 : ExoEqConfig.Preamp([.. chain.Filters]),
            playing = ExoEq.Game is { } game ? new { id = game.Id, title = game.Title, sound = store.Games.GetValueOrDefault(game.Id) } : null,
            games = store.Games.Select(g => new { id = g.Key, title = library.FirstOrDefault(e => string.Equals(e.Id, g.Key, StringComparison.OrdinalIgnoreCase))?.Title, sound = g.Value })
                .Where(g => g.title is not null).OrderBy(g => g.title, StringComparer.OrdinalIgnoreCase).ToArray(),
            library = library.Where(g => g.Installed && !store.Games.ContainsKey(g.Id)).OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                .Take(400).Select(g => new { id = g.Id, title = g.Title }).ToArray(),
            night = new
            {
                on = store.Night.On,
                start = night is { } schedule ? Clock(schedule.Start) : store.Night.Start,
                end = night is { } until ? Clock(until.End) : store.Night.End,
                sync = store.Night.Sync,
                now = ExoEq.NightNow(store, DateTime.Now),
            },
            others = store.Outputs.Where(o => !string.Equals(o.Key, guid, StringComparison.OrdinalIgnoreCase) && (o.Value.Headphone is not null || o.Value.Sound != ExoEqSounds.Flat))
                .Select(o => new { name = o.Value.Name.Length > 0 ? o.Value.Name : "An output", headphone = o.Value.Headphone?.Name, sound = o.Value.Sound }).ToArray(),
            guess = own?.Headphone is null && output is not null && AutoEqCatalog.Cached is { } known && AutoEqCatalog.Guess(output.Name, known) is { } g
                ? new { id = g.Id, name = g.Name, source = g.Source, form = g.Form } : null,
            lucidPlays = folder is not null && ExoEq.LucidPlays(),
        };
    }

    /// <summary>eq.headphones: AutoEq's models; ok false when its list could not be fetched and none is kept.</summary>
    private static async Task<object> EqHeadphonesAsync() =>
        await AutoEqCatalog.ListAsync().ConfigureAwait(false) is { } list
            ? new { ok = true, headphones = list.Select(h => new { id = h.Id, name = h.Name, source = h.Source, form = h.Form }).ToArray() }
            : new { ok = false, headphones = Array.Empty<object>() };
}
