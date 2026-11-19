using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// The new Epic Games Launcher's own switches for its Windows notifications and its What's New page on start
/// (settings\app-settings.json in its data folder: "enableNotifications", "showWhatsNewOnStartup"), and for how much
/// it uses while it sits hidden: its performance preset ("performanceProfile.preset": "default", "lowResources" or
/// "custom"; Epic's build names the web view switches a preset sets: no GPU process, a capped script heap, smaller
/// caches) and "verboseLogs" (a debug-level log file for the whole session). Turned off or lowered while Exo starts
/// Epic in your place, only when Epic's quiet is wanted (the "store clients stay quiet" setting is on and Epic's start
/// with Windows was handed to Exo; otherwise Epic's settings are not touched and no record is made), with the values
/// they had kept beside Exo's settings, put back by Off (the setting turned off) and by Exo's uninstall. The notifications
/// are held at every start; the preset and the logs are set once, so one the user changes back in Epic stays as they left
/// it. Written only while Epic is closed: it keeps its settings in memory while it runs. The record is written first, as
/// "intended", then the settings, then read back, then marked "done". Evidence and what was left out:
/// docs/architecture/launcher-tuning.md and docs/architecture/store-quiet.md.
/// </summary>
internal static class EpicQuietSettings
{
    internal static readonly string[] Keys = ["enableNotifications", "showWhatsNewOnStartup"];

    internal const string PresetKey = "performanceProfile.preset", VerboseKey = "verboseLogs";
    private const string LowPreset = "lowResources", DefaultPreset = "default";

    private static string BeforePath => Path.Combine(PathHelper.AppDataDir, "epic-quiet.json");
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// The settings with every key off, the performance preset on Epic's low-resources one and verbose logs off, and
    /// what they were before; null when nothing changes. <paramref name="tuned"/> are the keys Exo has changed before:
    /// the preset and the logs are not set again once the user has put them back.
    /// </summary>
    internal static (string Json, Dictionary<string, JsonNode> Before)? Quiet(string json, IReadOnlySet<string>? tuned = null)
    {
        if (JsonNode.Parse(json) is not JsonObject settings) return null;
        var before = new Dictionary<string, JsonNode>();
        foreach (var key in Keys)
        {
            if (settings[key] is not JsonValue value || !value.TryGetValue<bool>(out var on) || !on) continue;
            before[key] = true;
            settings[key] = false;
        }
        if (tuned?.Contains(VerboseKey) != true && settings[VerboseKey] is JsonValue verbose && verbose.TryGetValue<bool>(out var logging) && logging)
        {
            before[VerboseKey] = true;
            settings[VerboseKey] = false;
        }
        if (tuned?.Contains(PresetKey) != true && settings["performanceProfile"] is JsonObject profile &&
            profile["preset"] is JsonValue preset && preset.TryGetValue<string>(out var name) && name == DefaultPreset)
        {
            before[PresetKey] = DefaultPreset;
            profile["preset"] = LowPreset;
        }
        return before.Count == 0 ? null : (settings.ToJsonString(Indented), before);
    }

    /// <summary>
    /// The settings with what Exo changed put back, each only while it is still as Exo left it (a value the user has
    /// changed since is theirs); null when nothing changes.
    /// </summary>
    internal static string? Restore(string json, IReadOnlyDictionary<string, JsonNode> before)
    {
        if (JsonNode.Parse(json) is not JsonObject settings) return null;
        var changed = false;
        foreach (var (key, was) in before)
        {
            if (key == PresetKey)
            {
                if (settings["performanceProfile"] is JsonObject profile && profile["preset"] is JsonValue preset &&
                    preset.TryGetValue<string>(out var now) && now == LowPreset &&
                    was is JsonValue old && old.TryGetValue<string>(out var name))
                {
                    profile["preset"] = name;
                    changed = true;
                }
            }
            else if (settings[key] is JsonValue value && value.TryGetValue<bool>(out var on) && !on &&
                was is JsonValue wasOn && wasOn.TryGetValue<bool>(out var restored) && restored)
            {
                settings[key] = true;
                changed = true;
            }
        }
        return changed ? settings.ToJsonString(Indented) : null;
    }

    /// <summary>
    /// What Exo keeps of Epic's settings: "intended" is written before Exo's change (what each key held, the keys Exo will
    /// set), "done" once the settings were written and read back. <see cref="Off"/> is set while an undo waits for Epic to
    /// close. A record from before this (the bare keys and what they held) is read as done.
    /// </summary>
    internal sealed record Record(string State, Dictionary<string, JsonNode> Before, bool Off);

    internal const string Intended = "intended", Done = "done";

    internal static Record ReadRecord(string recordPath)
    {
        var root = JsonNode.Parse(File.ReadAllText(recordPath)) as JsonObject ?? [];
        if (root["state"] is JsonValue state && state.TryGetValue<string>(out var word) && root["before"] is JsonObject before)
            return new Record(word, before.ToDictionary(entry => entry.Key, entry => entry.Value!.DeepClone()),
                root["off"] is JsonValue off && off.TryGetValue<bool>(out var wait) && wait);
        return new Record(Done, root.ToDictionary(entry => entry.Key, entry => entry.Value!.DeepClone()), false);
    }

    private static string Serialize(Record record) =>
        new JsonObject
        {
            ["state"] = record.State,
            ["before"] = new JsonObject(record.Before.Select(entry => KeyValuePair.Create(entry.Key, (JsonNode?)entry.Value.DeepClone()))),
            ["off"] = record.Off,
        }.ToJsonString(Indented);

    /// <summary>Whether each of these keys now reads as Exo sets it.</summary>
    internal static bool Took(string json, IEnumerable<string> keys)
    {
        if (JsonNode.Parse(json) is not JsonObject settings) return false;
        foreach (var key in keys)
        {
            var ok = key == PresetKey
                ? settings["performanceProfile"] is JsonObject profile && profile["preset"] is JsonValue preset && preset.TryGetValue<string>(out var name) && name == LowPreset
                : settings[key] is JsonValue value && value.TryGetValue<bool>(out var on) && !on;
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Exo's uninstall: Epic's settings go back to what they were, if Epic is closed; Exo's record of them goes.</summary>
    internal static void PutBack()
    {
        if (!File.Exists(BeforePath)) return;
        try
        {
            if (UndoAt(BeforePath, SettingsPath(), EpicRunning(), WriteAtomic))
                AppLog.Info("Uninstall: Epic's notifications, What's New page, performance preset and logging are back as they were.");
            else AppLog.Info("Uninstall: Epic is open; its quieter settings stay until they are changed in Epic.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Uninstall: Epic's settings not put back: " + ex.Message);
        }
    }

    /// <summary>
    /// Quiet is switched off: Epic's settings go back to what they were, now if Epic is closed, else the next time Exo is about
    /// to start it (the record says so meanwhile, and nothing writes quiet values). Only a key that still holds what Exo wrote
    /// is put back; the record goes once it has been.
    /// </summary>
    internal static void Off()
    {
        if (HostMode.IsDisposable) return;
        try { HandAt(false, BeforePath, SettingsPath(), EpicRunning(), WriteAtomic); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Epic: settings not put back: " + ex.Message);
        }
    }

    /// <summary>
    /// The undo: true when nothing of Exo's is left in Epic's settings and the record is gone; false when Epic runs, which
    /// marks the record off so that no pass writes again while the undo waits.
    /// </summary>
    internal static bool UndoAt(string recordPath, string? settingsPath, bool epicRunning, Action<string, string> write)
    {
        if (!File.Exists(recordPath)) return true;
        var record = ReadRecord(recordPath);
        if (settingsPath is not null)
        {
            if (epicRunning)
            {
                if (!record.Off) write(recordPath, Serialize(record with { Off = true }));
                return false;
            }
            if (Restore(File.ReadAllText(settingsPath), record.Before) is { } restored) write(settingsPath, restored);
        }
        DeletionGate.DeleteFile(recordPath);
        return true;
    }

    /// <summary>
    /// Epic's quiet, as the settings now say: <paramref name="wanted"/> is the switch on and Epic's start with Windows handed
    /// to Exo. Wanted: the record (the marker) is made and Epic's switches are quieted now if Epic is closed, else when Exo
    /// next starts it. Not wanted: Epic's settings are not touched and no record is made, and a record an earlier Exo made
    /// without asking is undone.
    /// </summary>
    internal static void HandToExo(bool wanted)
    {
        if (HostMode.IsDisposable) return;
        try { HandAt(wanted, BeforePath, SettingsPath(), EpicRunning(), WriteAtomic); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Epic: settings not changed: " + ex.Message);
        }
    }

    /// <summary>The same with its files and the write named (the tests' seam; Epic is closed unless <paramref name="epicRunning"/>).</summary>
    internal static void HandAt(bool wanted, string recordPath, string? settingsPath, bool epicRunning, Action<string, string> write)
    {
        if (!wanted)
        {
            if (!File.Exists(recordPath)) return;
            if (UndoAt(recordPath, settingsPath, epicRunning, write))
                AppLog.Info("Epic: notifications, What's New page, performance preset and logging are back as they were.");
            else AppLog.Info("Epic: it is open; its settings go back as it next closes.");
            return;
        }
        if (!File.Exists(recordPath)) write(recordPath, Serialize(new Record(Done, [], false)));
        else if (ReadRecord(recordPath) is { Off: true } waiting) write(recordPath, Serialize(waiting with { Off = false }));
        if (settingsPath is not null && !epicRunning) ApplyAt(recordPath, settingsPath, write);
    }

    /// <summary>Before Exo starts Epic: its switches off, if quiet was wanted (the record is there); an undo that waited goes first.</summary>
    internal static void ApplyIfHanded()
    {
        if (HostMode.IsDisposable) return;
        if (!File.Exists(BeforePath) || SettingsPath() is not { } path || EpicRunning()) return;
        try { ApplyAt(BeforePath, path, WriteAtomic); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Epic: settings not changed: " + ex.Message);
        }
    }

    /// <summary>
    /// The order: the record first as "intended" (what each key holds and the keys Exo will set), then the settings, then the
    /// settings read back, and only then the record marked "done". A write that fails leaves a record whose undo changes
    /// nothing: undo only touches a key that holds what Exo wrote. Epic is closed (the caller checked).
    /// </summary>
    internal static void ApplyAt(string recordPath, string settingsPath, Action<string, string> write)
    {
        var record = ReadRecord(recordPath);
        if (record.Off)
        {
            UndoAt(recordPath, settingsPath, false, write);
            AppLog.Info("Epic: notifications, What's New page, performance preset and logging are back as they were.");
            return;
        }
        var before = record.Before;
        if (Quiet(File.ReadAllText(settingsPath), before.Keys.ToHashSet(StringComparer.Ordinal)) is not { } quiet) return;
        foreach (var (key, was) in quiet.Before) before.TryAdd(key, was);
        write(recordPath, Serialize(new Record(Intended, before, false)));
        write(settingsPath, quiet.Json);
        if (!Took(File.ReadAllText(settingsPath), quiet.Before.Keys))
        {
            AppLog.Warn("Epic: " + string.Join(", ", quiet.Before.Keys) + " did not keep after a write; Exo tries again at its next start of Epic.");
            return;
        }
        write(recordPath, Serialize(new Record(Done, before, false)));
        AppLog.Info("Epic: " + string.Join(", ", quiet.Before.Keys) + " set quieter and lighter while Exo starts it.");
    }

    /// <summary>One word for the page: applied, waiting (a write or an undo needs Epic closed), or off.</summary>
    internal static string Word(bool wanted)
    {
        if (SettingsPath() is not { } path || !File.Exists(BeforePath)) return "off";
        try
        {
            var record = ReadRecord(BeforePath);
            if (record.Off || !wanted) return "waiting";
            return Quiet(File.ReadAllText(path), record.Before.Keys.ToHashSet(StringComparer.Ordinal)) is null ? "applied" : "waiting";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return "waiting"; }
    }

    private static string? SettingsPath()
    {
        var path = EpicLauncherV2.DataDir() is { } dir ? Path.Combine(dir, "settings", "app-settings.json") : null;
        return path is not null && File.Exists(path) ? path : null;
    }

    private static bool EpicRunning() => StoreWindowHider.EpicProcessNames.Any(ProcessHelper.IsProcessRunning);

    private static void WriteAtomic(string path, string json)
    {
        var staged = path + ".exo-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(staged, json);
        File.Move(staged, path, overwrite: true);
    }
}
