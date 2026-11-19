using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// lucid: Lucid as Exo knows it and its settings ("state"), Start with Exo ("set", on), Switch EQ with my headset
/// ("follow", on), its first set up, which may download and install what the page's row has said ("setup"), Lucid's own window for its Repair ("repair"), Equalizer APO's device
/// picker ("devices") and a change to its settings ("change", change); lucid.headphones lists its headphones. Lucid Voice
/// is only read, never changed. The state also says whether Lucid can start without asking anything of Windows (ready),
/// which output its EQ is on and whether Windows plays there (<see cref="LucidOutput"/>), and whether a change waits for a
/// game to end (held), its settings shown as they will be.
/// </summary>
public sealed partial class WebHostBridge
{
    /// <summary>Lucid's changes wait while a game plays (<see cref="LucidCompanion.Playing"/>).</summary>
    private void WatchLucid() =>
        LucidCompanion.Playing = () => _services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0;

    private async Task<object> LucidAsync(JsonElement p, bool hasParams)
    {
        var settings = _services.Settings;
        var state = LucidCompanion.Read(settings.Current);
        string? message = null;
        switch (ReadString(p, hasParams, "action"))
        {
            case "set" when state.Path is not null:
                var on = ReadBool(p, hasParams, "on") == true;
                settings.UpdateProfile(s => s.StartLucidWithExo = on);
                if (on) message = await LucidCompanion.StartWithExoAsync(settings.Current).ConfigureAwait(false);
                else await LucidCompanion.LetGoAsync().ConfigureAwait(false);
                break;
            case "follow" when state.Path is not null:
                var follow = ReadBool(p, hasParams, "on") == true;
                settings.UpdateProfile(s => s.LucidFollowHeadset = follow);
                break;
            case "setup" when state.Path is { } path:
                settings.UpdateProfile(s => s.StartLucidWithExo = true);
                LucidCompanion.HideTray(true);
                if (!state.Running && !LucidCompanion.Start(path)) message = "Lucid didn't start. Try again.";
                break;
            case "repair" when state.Path is { } path && (state.Running || LucidCompanion.Ready()):
                message = LucidCompanion.Repair(path);
                break;
            case "devices" when state.Path is not null:
                message = LucidCompanion.OpenDeviceSelector();
                break;
            case "change" when state.Path is { } path && hasParams && p.TryGetProperty("change", out var change) && change.ValueKind == JsonValueKind.Object:
                message = await LucidCompanion.ChangeAsync(path, ReadLucidChange(change)).ConfigureAwait(false);
                break;
        }
        state = LucidCompanion.Read(settings.Current);
        var root = state.Path is null ? null : LucidSettings.Load(LucidSettings.FilePath);
        var held = LucidCompanion.Held;
        // A change waiting for the game shows as it will be.
        if (root is not null && held is not null && state.Path is { } lucidPath)
            LucidSettings.Apply(root, held, id => LucidCompanion.HeadphoneName(lucidPath, id), LucidSettings.WindowsNightLight);
        var lucid = root is null ? null : LucidSettings.Read(root);
        var coverage = state.Path is not null ? LucidOutput.Read() : null;
        if (lucid is not null && held is null && state.Path is { } followed && await LucidCompanion.FollowAsync(settings.Current).ConfigureAwait(false))
        {
            held = LucidCompanion.Held;
            root = LucidSettings.Load(LucidSettings.FilePath);
            if (root is not null && held is not null) LucidSettings.Apply(root, held, id => LucidCompanion.HeadphoneName(followed, id), LucidSettings.WindowsNightLight);
            lucid = root is null ? null : LucidSettings.Read(root);
        }
        // A USB or wireless headset names itself: offer its tuning when Lucid has one and isn't on it.
        var guess = lucid is not null && state.Path is { } lucidExe && LucidCatalog.Read(lucidExe) is { } catalog
            && LucidCatalog.Guess(coverage?.Playing, catalog) is { } found && !string.Equals(found.Id, lucid.HeadphoneId, StringComparison.OrdinalIgnoreCase)
            ? found : null;
        return new
        {
            ok = message is null,
            message,
            found = state.Path is not null,
            version = state.Path is { } exe && LucidCompanion.FileVersion(exe) is { } installed ? LucidCompanion.Shown(installed) : null,
            ready = LucidCompanion.Ready(),
            running = state.Running,
            voiceOn = state.VoiceOn,
            startWithExo = settings.Current.StartLucidWithExo,
            followHeadset = settings.Current.LucidFollowHeadset,
            held = held is not null,
            updateNote = LucidCompanion.UpdateNote(),
            devices = state.Path is not null && LucidCompanion.DeviceSelector() is not null,
            eq = coverage is { } eq ? new { on = eq.On, playing = eq.Playing, covered = eq.Covered } : null,
            guess = guess is null ? null : new { id = guess.Id, name = guess.Name },
            settings = lucid is null ? null : new
            {
                headphone = new { id = lucid.HeadphoneId, name = lucid.HeadphoneName, chosen = lucid.HeadphoneChosen },
                favorites = lucid.Favorites,
                gameMode = lucid.GameMode,
                keepOnAltTab = lucid.KeepOnAltTab,
                onlyWhileGaming = lucid.OnlyWhileGaming,
                nightShift = lucid.NightShift,
                nightStart = lucid.NightStart,
                nightEnd = lucid.NightEnd,
                nightSync = lucid.NightSync,
                notifications = lucid.Notifications,
                discord = lucid.Discord,
                toggleSound = lucid.ToggleSound,
                detectSound = lucid.DetectSound,
                hotkey = new { modifiers = lucid.HotkeyModifiers, key = lucid.HotkeyKey },
                eqOn = lucid.EqOn,
            },
        };
    }

    /// <summary>lucid.headphones: the headphones this PC's Lucid has tunings for, Lucid IEMs first.</summary>
    private object LucidHeadphones() =>
        LucidCompanion.Locate(_services.Settings.Current) is { } path && LucidCatalog.Read(path) is { } list
            ? new { ok = true, headphones = list.Select(h => new { id = h.Id, model = h.Model, brand = h.Brand, category = h.Category, grade = h.Grade, keywords = h.Keywords }).ToArray() }
            : new { ok = false, headphones = Array.Empty<object>() };

    private static LucidSettings.Change ReadLucidChange(JsonElement c)
    {
        bool? Flag(string name) => ReadBool(c, true, name);
        var favorites = c.TryGetProperty("favorites", out var fav) && fav.ValueKind == JsonValueKind.Object
            ? fav.EnumerateObject().Take(64).Where(e => e.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                .ToDictionary(e => e.Name, e => e.Value.GetBoolean(), StringComparer.OrdinalIgnoreCase)
            : null;
        return new LucidSettings.Change
        {
            Headphone = ReadString(c, true, "headphone"),
            Favorites = favorites,
            GameMode = Flag("gameMode"),
            KeepOnAltTab = Flag("keepOnAltTab"),
            OnlyWhileGaming = Flag("onlyWhileGaming"),
            NightShift = Flag("nightShift"),
            NightStart = ReadString(c, true, "nightStart"),
            NightEnd = ReadString(c, true, "nightEnd"),
            NightSync = Flag("nightSync"),
            Notifications = Flag("notifications"),
            Discord = Flag("discord"),
            ToggleSound = ReadDouble(c, true, "toggleSound") is { } toggle ? (int)Math.Round(toggle) : null,
            DetectSound = ReadDouble(c, true, "detectSound") is { } detect ? (int)Math.Round(detect) : null,
            Hotkey = c.TryGetProperty("hotkey", out var hotkey) && hotkey.ValueKind == JsonValueKind.Object
                && ReadDouble(hotkey, true, "key") is { } key && ReadDouble(hotkey, true, "modifiers") is { } modifiers
                ? ((int)modifiers, (int)key) : null,
        };
    }
}
