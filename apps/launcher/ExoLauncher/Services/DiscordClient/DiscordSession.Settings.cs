using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// What Discord settings change in the running client: the account's settings (kept as last read,
/// sent to the room as discord.userSettings and read again when another Discord client changes
/// them), Discord's sounds (<see cref="DiscordSounds"/>), keybinds
/// while a call is on, Global Attenuation, and Current Device in Devices.
/// </summary>
internal sealed partial class DiscordSession
{
    internal static void Publish(string name, object? data) => Event?.Invoke(name, data);

    /// <summary>One of Discord's sounds by its own name (message1, mute, user_join, ...), unless the user turned it off.</summary>
    internal static void Sound(string name) => DiscordSounds.Play(name);

    /// <summary>Streamer Mode with Disable notifications on: no Windows notifications (the room says so).</summary>
    internal static bool StreamerQuiet { get; set; }

    /// <summary>A notification was due while Exo was in the background: flash the taskbar button (Enable Taskbar Flashing).</summary>
    internal static event Action? FlashRequested;

    /// <summary>This sign-in's own entry in auth/sessions (READY's auth_session_id_hash): Devices' Current Device.</summary>
    internal string? AuthSessionIdHash { get; private set; }

    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private JsonObject? _userSettings;
    private int _settingsRefreshing;

    /// <summary>The account's settings (Discord's defaults filled in), after saving <paramref name="changes"/> when there are any.</summary>
    internal async Task<(JsonObject? Values, string? Error)> UserSettingsAsync(IReadOnlyDictionary<string, JsonElement>? changes, CancellationToken ct)
    {
        await _settingsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (changes is { Count: > 0 })
            {
                // A server dragged in the rail: every view shows the new order at once, and the old one again if Discord refuses it.
                var railBefore = changes.TryGetValue("guildFolders", out var rail) && DiscordUserSettings.Folders(rail) is { } folders ? Cache.ReplaceRail(folders) : null;
                if (railBefore is not null) Publish("discord.changed", new { guildId = (string?)null });
                var (values, error) = await DiscordUserSettings.ChangeAsync(Rest, changes, ct).ConfigureAwait(false);
                if (values is not null) RememberSettings(values);
                else if (railBefore is not null)
                {
                    Cache.ReplaceRail(railBefore);
                    Publish("discord.changed", new { guildId = (string?)null });
                    AppLog.Info("Discord client: the server order wasn't saved (" + error + ").");
                }
                return (values, error);
            }
            if (_userSettings is { } cached) return ((JsonObject)cached.DeepClone(), null);
            var (proto, fetchError) = await DiscordUserSettings.FetchAsync(Rest, ct).ConfigureAwait(false);
            if (proto is null) return (null, fetchError);
            var decoded = DiscordUserSettings.Decode(proto);
            RememberSettings(decoded);
            return (decoded, null);
        }
        catch (InvalidDataException) { return (null, "Discord sent settings Exo can't read."); }
        finally { _settingsGate.Release(); }
    }

    private void RememberSettings(JsonObject values, bool announcePresence = true)
    {
        var customBefore = _userSettings?["customStatus"]?.ToJsonString();
        // A person's volume or mute changed here and not saved yet stays as chosen over what Discord had.
        lock (_audioGate)
        {
            foreach (var kind in new[] { _people, _streams })
            {
                if (values[kind.Key] is not JsonObject people) values[kind.Key] = people = [];
                foreach (var (id, change) in kind.Pending) people[id] = AudioJson(people[id], change.Muted, change.Volume, change.SoundboardMuted);
                kind.Values = people.ToDictionary(p => p.Key, p => (
                    p.Value?["muted"]?.GetValue<bool>() ?? false,
                    p.Value?["volume"]?.GetValue<float>() ?? 100f,
                    p.Value?["soundboardMuted"]?.GetValue<bool>() ?? false));
            }
        }
        _userSettings = (JsonObject)values.DeepClone();
        Event?.Invoke("discord.userSettings", values.DeepClone());
        // The custom status changed here or in another Discord app: this session's presence says so.
        if (announcePresence && customBefore != _userSettings["customStatus"]?.ToJsonString())
            _ = SendPresenceAsync();
    }

    // ---- a person's User Volume and Mute in calls: Discord's audio_context_settings, heard at once, saved as Discord does ----

    private readonly Lock _audioGate = new();

    /// <summary>One of Discord's audio_context_settings maps: people's call voices (audioUsers) or their streams' sound (audioStreams).</summary>
    private sealed class AudioKind(string key)
    {
        public string Key { get; } = key;
        public IReadOnlyDictionary<string, (bool Muted, float Volume, bool SoundboardMuted)> Values { get; set; } = new Dictionary<string, (bool, float, bool)>();
        public Dictionary<string, (bool? Muted, float? Volume, bool? SoundboardMuted)> Pending { get; } = [];
        public CancellationTokenSource? Save { get; set; }

        public float Gain(string userId) => Values.TryGetValue(userId, out var one) ? one.Muted ? 0 : one.Volume / 100f : 1f;
    }

    private readonly AudioKind _people = new("audioUsers"), _streams = new("audioStreams");

    /// <summary>How loud a person in a call plays here: their User Volume (0-2), 0 when muted for me.</summary>
    internal float UserGain(string userId) => _people.Gain(userId);

    /// <summary>Whether another person's soundboard sounds are muted locally for me.</summary>
    internal bool UserSoundboardMuted(string userId) => _people.Values.TryGetValue(userId, out var one) && one.SoundboardMuted;

    /// <summary>How loud a person's stream plays here: its Stream Volume (0-2), 0 when muted for me.</summary>
    internal float StreamGain(string userId) => _streams.Gain(userId);

    /// <summary>User Volume (0-200), Mute, or Soundboard Mute for one person: heard now, on screen everywhere, and saved to the account.</summary>
    internal void SetAudioUser(string userId, bool? muted, float? volume, bool stream = false, bool? soundboardMuted = null)
    {
        if (userId == Cache.Me?.Id) return;
        var kind = stream ? _streams : _people;
        JsonObject? values;
        CancellationTokenSource save;
        lock (_audioGate)
        {
            var was = kind.Pending.GetValueOrDefault(userId);
            kind.Pending[userId] = (muted ?? was.Muted, volume ?? was.Volume, soundboardMuted ?? was.SoundboardMuted);
            var users = new Dictionary<string, (bool Muted, float Volume, bool SoundboardMuted)>(kind.Values);
            var old = users.TryGetValue(userId, out var known) ? known : (Muted: false, Volume: 100f, SoundboardMuted: false);
            users[userId] = (muted ?? old.Muted, Math.Clamp(volume ?? old.Volume, 0, 200), soundboardMuted ?? old.SoundboardMuted);
            kind.Values = users;
            values = _userSettings is { } current ? (JsonObject)current.DeepClone() : null;
            if (values?[kind.Key] is JsonObject people) people[userId] = AudioJson(people[userId], muted, volume, soundboardMuted);
            if (values is not null) _userSettings = (JsonObject)values.DeepClone();
            kind.Save?.Cancel();
            kind.Save = save = new CancellationTokenSource();
        }
        if (values is not null) Event?.Invoke("discord.userSettings", values);
        _ = SaveAudioAsync(kind, muted is not null || soundboardMuted is not null ? 0 : 2000, save.Token);
    }

    private static JsonObject AudioJson(JsonNode? before, bool? muted, float? volume, bool? soundboardMuted) => new()
    {
        ["muted"] = muted ?? before?["muted"]?.GetValue<bool>() ?? false,
        ["volume"] = Math.Clamp(volume ?? before?["volume"]?.GetValue<float>() ?? 100f, 0, 200),
        ["soundboardMuted"] = soundboardMuted ?? before?["soundboardMuted"]?.GetValue<bool>() ?? false,
    };

    private async Task SaveAudioAsync(AudioKind kind, int delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        JsonObject people;
        lock (_audioGate)
        {
            if (ct.IsCancellationRequested || kind.Pending.Count == 0) return;
            people = [];
            foreach (var (id, change) in kind.Pending)
            {
                var entry = new JsonObject { ["muted"] = change.Muted, ["volume"] = change.Volume };
                if (change.SoundboardMuted is { } sb) entry["soundboardMuted"] = sb;
                people[id] = entry;
            }
            kind.Pending.Clear();
        }
        var changes = new Dictionary<string, JsonElement> { [kind.Key] = JsonSerializer.SerializeToElement(people) };
        var (_, error) = await UserSettingsAsync(changes, _stop.Token).ConfigureAwait(false);
        if (error is not null) AppLog.Info("Discord voice: a person's volume wasn't saved (" + error + ").");
    }

    // Dispatches that matter to settings, before the cache takes them.
    private void OnSettingsDispatch(string type, JsonElement d)
    {
        // The pickers' kept frecency settings (DiscordRest.FrecencyProtoAsync): read again once Discord says they changed.
        if (type == "USER_SETTINGS_PROTO_UPDATE") Rest.ForgetFrecency();
        switch (type)
        {
            case "READY":
                AuthSessionIdHash = d.TryGetProperty("auth_session_id_hash", out var hash) ? hash.GetString() : null;
                Rest.ForgetFrecency();
                if (d.TryGetProperty("user_settings_proto", out var readyProtoEl) && readyProtoEl.ValueKind == JsonValueKind.String
                    && readyProtoEl.GetString() is { Length: > 0 } readyProtoStr)
                {
                    try
                    {
                        var decoded = DiscordUserSettings.Decode(Convert.FromBase64String(readyProtoStr));
                        RememberSettings(decoded, announcePresence: false);
                    }
                    catch (Exception ex) when (ex is FormatException or InvalidDataException) { }
                }
                break;
            case "USER_SETTINGS_UPDATE" or "USER_SETTINGS_PROTO_UPDATE" when _userSettings is not null:
                RefreshSettingsSoon();
                break;
            case "VOICE_STATE_UPDATE":
                JoinLeaveSound(d);
                break;
            case "MESSAGE_CREATE":
                CurrentChannelMessage(d);
                break;
        }
    }

    // Another Discord client changed a setting: read them again once things settle.
    private void RefreshSettingsSoon()
    {
        if (Interlocked.Exchange(ref _settingsRefreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, _stop.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _settingsRefreshing, 0);
                _userSettings = null;
                await UserSettingsAsync(null, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally { Interlocked.Exchange(ref _settingsRefreshing, 0); }
        });
    }

    // Someone else joining or leaving the server voice channel this user is in (Discord's User Join and User Leave).
    private void JoinLeaveSound(JsonElement d)
    {
        if (d.TryGetProperty("user_id", out var u) && u.GetString() is { } user && user != Cache.Me?.Id
            && d.TryGetProperty("guild_id", out var g) && g.GetString() is { } guild)
        {
            string? mine;
            lock (_voiceGate) mine = _voiceState == "connected" ? _voiceChannel : null;
            if (mine is null) return;
            var before = Cache.VoiceChannelOf(guild, user);
            var after = d.TryGetProperty("channel_id", out var c) ? c.GetString() : null;
            if (after == mine && before != mine) Sound("user_join");
            else if (before == mine && after != mine) Sound("user_leave");
        }
    }

    // A message in the conversation on screen: Discord's sound for it, and a /tts message read out.
    private void CurrentChannelMessage(JsonElement d)
    {
        if (Viewing is not { } viewing || !d.TryGetProperty("channel_id", out var c) || c.GetString() != viewing) return;
        if (!d.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object) return;
        if (author.TryGetProperty("id", out var id) && id.GetString() == Cache.Me?.Id) return;
        if (HostWindow.IsActive) Sound("message3");
        if (d.TryGetProperty("tts", out var tts) && tts.ValueKind == JsonValueKind.True && d.TryGetProperty("content", out var content))
        {
            var name = author.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String ? gn.GetString()
                : author.TryGetProperty("username", out var un) ? un.GetString() : null;
            Event?.Invoke("discord.tts", new { name = name ?? "Someone", text = content.GetString() ?? "" });
        }
    }

    // Mute, Unmute, Deafen and Undeafen, as Discord plays them.
    private void ToggleSound(bool wasMuted, bool wasDeafened)
    {
        if (_deafened != wasDeafened) Sound(_deafened ? "deafen" : "undeafen");
        else if (_muted != wasMuted) Sound(_muted ? "mute" : "unmute");
    }


    // ---- while a call is on: keybinds and Global Attenuation ----

    private Timer? _keyTick;
    private readonly HashSet<string> _keysHeld = [];
    private readonly HashSet<string> _speaking = [];
    private Attenuation? _attenuation;

    private static readonly string[] Toggles =
        [Keybinds.ToggleMute, Keybinds.ToggleDeafen, Keybinds.ToggleVoiceMode, Keybinds.ToggleStreamerMode, Keybinds.Disconnect];

    /// <summary>The call started or ended (the shell's sleep rule reads it).</summary>
    internal static event Action? CallStateChanged;

    /// <summary>The session goes away (a lost connection, a fatal answer): it stops being the current one and the shell hears the call may be over.</summary>
    internal static bool Drop(DiscordSession session)
    {
        lock (StartGate) { if (_current != session) return false; _current = null; }
        RaiseCallStateChanged();
        return true;
    }

    /// <summary>The call started or ended, or the session that held it went away (a torn-down session ends its call with no event of its own).</summary>
    private static void RaiseCallStateChanged()
    {
        try { CallStateChanged?.Invoke(); } catch { /* A listener never breaks the call. */ }
    }

    private static bool HasNonCallKeybind() =>
        App.Services.Settings.Shared.DiscordKeybinds.Any(k =>
            k.Action is Keybinds.ToggleMute or Keybinds.ToggleDeafen or Keybinds.ToggleVoiceMode or Keybinds.ToggleStreamerMode);

    // Called whenever the call's state is published or keybinds/session change.
    private void CallChanged(bool inCall)
    {
        RaiseCallStateChanged();
        UpdateKeyTick(inCall);
        if (inCall) return;
        lock (_speaking) _speaking.Clear();
        Interlocked.Exchange(ref _attenuation, null)?.Dispose();
    }

    internal void UpdateKeyTick(bool? knownInCall = null)
    {
        var inCall = knownInCall ?? InCall;
        var shouldRun = !_stop.IsCancellationRequested
            && ((inCall && App.Services.Settings.Shared.DiscordKeybinds.Count > 0) || HasNonCallKeybind());
        lock (_keysHeld)
        {
            if (shouldRun && _keyTick is null)
                _keyTick = new Timer(_ => KeyTick(), null, 30, 30);
            else if (!shouldRun && _keyTick is not null)
            {
                _keyTick.Dispose();
                _keyTick = null;
            }
        }
    }

    internal void StopKeybinds()
    {
        lock (_keysHeld)
        {
            _keyTick?.Dispose();
            _keyTick = null;
            _keysHeld.Clear();
        }
    }

    /// <summary>In a voice call (or joining one) right now.</summary>
    internal bool InCall
    {
        get { lock (_voiceGate) return _voiceChannel is not null; }
    }

    /// <summary>The keybinds changed: updates active keybind polling inside and outside calls.</summary>
    internal void KeybindsChanged()
    {
        lock (_keysHeld) { _keyTick?.Dispose(); _keyTick = null; }
        UpdateKeyTick(InCall);
    }

    private void KeyTick()
    {
        var inCall = InCall;
        foreach (var action in Toggles)
        {
            if (action == Keybinds.Disconnect && !inCall)
            {
                lock (_keysHeld) _keysHeld.Remove(action);
                continue;
            }
            var held = Keybinds.Held(action);
            bool pressed;
            lock (_keysHeld)
            {
                pressed = held && _keysHeld.Add(action);
                if (!held) _keysHeld.Remove(action);
            }
            // On a timer thread, which has no handler: a keybind that fails (settings.json can't be written) is logged.
            if (pressed)
            {
                try { RunKeybind(action); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("Discord keybind " + action + " failed: " + ex.GetType().Name + "."); }
            }
        }
    }

    private void RunKeybind(string action)
    {
        switch (action)
        {
            case Keybinds.ToggleMute: SetVoice(!_muted, null); break;
            case Keybinds.ToggleDeafen: SetVoice(null, !_deafened); break;
            case Keybinds.Disconnect: LeaveVoice(); break;
            case Keybinds.ToggleVoiceMode: App.Services.Settings.UpdateProfile(s => s.DiscordPushToTalk = !s.DiscordPushToTalk); break;
            default: Event?.Invoke("discord.keybind", new { action }); break;
        }
    }

    private void OnSpeaking(object data)
    {
        var e = JsonSerializer.SerializeToElement(data);
        if (e.GetProperty("userId").GetString() is not { } user) return;
        bool self, others;
        Attenuation? attenuation;
        lock (_speaking)
        {
            if (e.GetProperty("speaking").GetBoolean()) _speaking.Add(user);
            else _speaking.Remove(user);
            self = Cache.Me?.Id is { } me && _speaking.Contains(me);
            others = _speaking.Count > (self ? 1 : 0);
            if (App.Services.Settings.Shared.DiscordAttenuation > 0) _attenuation ??= new Attenuation();
            attenuation = _attenuation;
        }
        attenuation?.Update(self, others);
    }
}
