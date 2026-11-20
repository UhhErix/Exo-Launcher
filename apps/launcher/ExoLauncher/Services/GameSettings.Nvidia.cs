using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Optimize's NVIDIA layer: every game Exo optimizes gets its own profile in the driver, built on Exo's global profile
/// (<see cref="GpuProfile"/>), the way Profile Inspector keeps per-program profiles. It holds only what that game needs
/// differently, researched per game; everything else carries over from the global profile. It shows as its own group in
/// the game's Optimize, applied and put back with the rest; the game's own values it replaced are kept in
/// game-settings\{id}.nvidia.json for Put back, and PC › Graphics › Driver and profile lists each game's profile under
/// the global one (<see cref="NvidiaGames"/>). A game whose settings ask nothing of the driver on this PC has no group.
/// </summary>
internal static partial class GameSettings
{
    internal const string NvidiaGroup = "nvidia";
    internal static readonly NvidiaValue PerGamePowerPerformance = new(GpuProfile.PowerManagement, 1, "Power management", "Prefer maximum performance");

    internal static readonly SettingsFile EmptyFile = new([], "\n", false, []);

    /// <summary>Snapshot of a profile's groups (file + NVIDIA), overall standing, and whether any backing source was found.</summary>
    internal sealed record StateSummary(List<GroupState>? Groups, Standing Standing, bool Found);

    /// <summary>Reads file groups and optional NVIDIA group for a profile, handling both INI-backed and NVIDIA-only profiles.</summary>
    internal static StateSummary InspectState(Models.GameEntry game, Profile profile, bool includePerGamePower = false)
    {
        var fileBased = !string.IsNullOrEmpty(profile.File);
        var files = fileBased ? ReadAll(profile) : [];
        var states = fileBased
            ? (files.Count == 0 ? null : States(profile, files, Kept(profile.Id), KeptOn(profile)))
            : new List<GroupState>();
        if (states is not null && NvidiaState(game, profile, files.Count > 0 ? files[0].File : EmptyFile, includePerGamePower) is { } nvidia)
            states.Add(nvidia);
        var standing = states is null ? Standing.Off : Stands(states, fileBased ? Missing(profile, files).Count : 0);
        return new(states, standing, !fileBased || files.Count > 0);
    }

    /// <summary>Applies Optimize/group changes including the NVIDIA driver profile when appropriate; returns an error sentence or null.</summary>
    internal static string? ApplyWithNvidia(Models.GameEntry game, Profile profile, string? group, bool all, bool on, bool updateNvidiaEvenIfSingle = false, bool includePerGamePower = false)
    {
        if (!Set(profile, all ? null : group, on, profile.Program?.Invoke(game))) return "Could not save. Try again.";
        NoteGame(profile.Id, game.Id);
        var fileBased = !string.IsNullOrEmpty(profile.File);
        var driverSource = fileBased ? (ReadAll(profile) is [var first, ..] ? first.File : null) : EmptyFile;
        if ((all || group == NvidiaGroup || updateNvidiaEvenIfSingle) && driverSource is not null
            && SetNvidia(game, profile, driverSource, on, includePerGamePower) is { } refused)
            return refused;
        return null;
    }

    /// <summary>One value in a game's NVIDIA profile: the setting, Exo's value (null: the driver's own), and how it reads.</summary>
    internal sealed record NvidiaValue(uint Id, uint? Value, string Label, string Words);

    /// <summary>How a game's NVIDIA profile is found (its program, <see cref="Profile.Program"/>, and the file NVIDIA matches beside it) and what Exo sets in it for these settings.</summary>
    internal sealed record NvidiaGame(string FileInFolder, Func<SettingsFile, IReadOnlyList<NvidiaValue>> Values);

    /// <summary>
    /// What Exo set in a game's profile and what the game's own values were before (null: none of its own); FileInFolder (the
    /// file NVIDIA matches beside the program) lets Exo make the profile again after a clean driver install removed it.
    /// </summary>
    internal sealed record NvidiaBackup(string Title, string Exe, string? Profile, Dictionary<uint, uint?> Before, List<NvidiaValue> Set, string? FileInFolder = null);

    private static string NvidiaBackupPath(Profile profile) => Path.Combine(Folder, profile.Id + ".nvidia.json");

    private static NvidiaProfiles.GameProgram? Program(Models.GameEntry game, Profile profile) =>
        profile.Program?.Invoke(game) is { } exe
            ? new(exe, profile.Title, profile.Nvidia?.FileInFolder ?? "", (name, beside) =>
                (profile.Nvidia is { FileInFolder.Length: > 0 } && beside.Equals(profile.Nvidia.FileInFolder, StringComparison.OrdinalIgnoreCase))
                || name.Contains(profile.Title, StringComparison.OrdinalIgnoreCase))
            : null;

    private static IReadOnlyList<NvidiaValue> EffectiveValues(Profile profile, SettingsFile file, bool includePerGamePower)
    {
        var baseValues = profile.Nvidia?.Values(file) ?? [];
        if (!includePerGamePower || baseValues.Any(v => v.Id == GpuProfile.PowerManagement)) return baseValues;
        return [.. baseValues, PerGamePowerPerformance];
    }

    /// <summary>The group's words: "Its own NVIDIA profile: DLSS model the game's own, CNN".</summary>
    internal static string NvidiaLabel(IReadOnlyList<NvidiaValue> values) =>
        "Its own NVIDIA profile: " + string.Join(", ", values.Select(v => $"{v.Label} {v.Words}"));

    /// <summary>The game's NVIDIA group as Optimize shows it, or null when there's nothing for the driver here.</summary>
    internal static GroupState? NvidiaState(Models.GameEntry game, Profile profile, SettingsFile file, bool includePerGamePower = false)
    {
        if (!NvidiaProfiles.GameProfilesUsable || (!includePerGamePower && profile.Nvidia is null) || Program(game, profile) is not { } program) return null;
        var wanted = EffectiveValues(profile, file, includePerGamePower);
        var backup = LoadNvidia(profile);
        if (wanted.Count == 0) return null;
        var state = NvidiaProfiles.ForGameLive(program, wanted.Select(v => v.Id).ToList());
        return new GroupState(NvidiaGroup, NvidiaLabel(wanted), GroupOn(state, wanted, out _), backup is not null);
    }

    /// <summary>
    /// Whether the group is on: every setting the driver would take reads back as Exo set it, and at least one was written.
    /// A setting the driver skipped is not one it can be on for, so it is neither a reason for off nor for on.
    /// </summary>
    private static bool GroupOn(NvidiaProfiles.GameState state, IReadOnlyList<NvidiaValue> wanted, out bool allSkipped)
    {
        var skipped = state.Skipped ?? [];
        var written = wanted.Where(value => !skipped.Contains(value.Id)).ToList();
        allSkipped = written.Count == 0;
        return state.Ok && written.Count > 0 && written.All(value => state.After.GetValueOrDefault(value.Id) == value.Value);
    }

    /// <summary>
    /// Sets the game's NVIDIA profile (<paramref name="on"/>) or puts back what it held. A set keeps the game's first values
    /// for Put back, and takes out what Exo set before that these settings no longer ask for. Null when done.
    /// </summary>
    internal static string? SetNvidia(Models.GameEntry game, Profile profile, SettingsFile file, bool on, bool includePerGamePower = false)
    {
        if (!NvidiaProfiles.GameProfilesUsable || (!includePerGamePower && profile.Nvidia is null) || Program(game, profile) is not { } program) return null;
        lock (Gate)
        {
            var backup = LoadNvidia(profile);
            var wanted = on ? EffectiveValues(profile, file, includePerGamePower) : [];
            // Exo's earlier values the settings no longer ask for go back to what the game had; the rest are set.
            var writes = new Dictionary<uint, uint?>();
            foreach (var old in backup?.Set ?? []) if (wanted.All(v => v.Id != old.Id)) writes[old.Id] = backup!.Before.GetValueOrDefault(old.Id);
            foreach (var value in wanted) writes[value.Id] = value.Value;
            if (writes.Count == 0) return null;
            var state = NvidiaProfiles.ForGameLive(program, writes.Keys.ToList(), writes);
            if (!state.Ok) return state.Message ?? "The NVIDIA driver didn't take the game's profile. Try again.";
            // Every setting this driver has was skipped: it doesn't have them, and that is said, not "done".
            if (wanted.Count > 0 && GroupOn(state, [.. wanted], out var allSkipped) is false && allSkipped) return "This driver doesn't have these settings.";
            if (wanted.Count == 0)
            {
                DeleteNvidia(profile);
                return null;
            }
            var before = backup?.Before ?? [];
            // Only what the driver took is kept as Exo's: a skipped setting was never written, so it is never put back or checked.
            var taken = wanted.Where(value => state.Skipped?.Contains(value.Id) is not true).ToList();
            foreach (var value in taken) before.TryAdd(value.Id, state.Before.GetValueOrDefault(value.Id));
            SaveNvidia(profile, new NvidiaBackup(profile.Title, program.Exe, state.Profile, before, [.. taken], program.FileInFolder));
            return null;
        }
    }

    /// <summary>
    /// After a driver install (a clean one removes every profile Exo made and resets the rest): each game's NVIDIA profile
    /// set again where the driver no longer holds what Exo set. The games put back, and the ones the driver refused.
    /// </summary>
    internal static (List<string> Done, List<string> Failed) RestoreNvidiaGames()
    {
        List<string> done = [], failed = [];
        if (!NvidiaProfiles.Available) return (done, failed);
        foreach (var game in NvidiaGames().Where(game => game.Set.Count > 0))
        {
            var program = new NvidiaProfiles.GameProgram(game.Exe, game.Title, game.FileInFolder ?? "", (name, beside) =>
                name == game.Profile || name.Contains(game.Title, StringComparison.OrdinalIgnoreCase)
                || (game.FileInFolder is { Length: > 0 } file && beside.Equals(file, StringComparison.OrdinalIgnoreCase)));
            var ids = game.Set.Select(value => value.Id).ToList();
            lock (Gate)
            {
                var now = NvidiaProfiles.ForGame(program, ids);
                if (now.Ok && game.Set.All(value => now.After.GetValueOrDefault(value.Id) == value.Value)) continue;
                var written = NvidiaProfiles.ForGame(program, ids, game.Set.ToDictionary(value => value.Id, value => value.Value));
                (written.Ok ? done : failed).Add(game.Title);
            }
        }
        return (done, failed);
    }

    /// <summary>Every game with its own NVIDIA profile from Exo: its title, the driver's name for the profile, and what Exo set.</summary>
    internal static IReadOnlyList<NvidiaBackup> NvidiaGames()
    {
        try
        {
            if (!Directory.Exists(Folder)) return [];
            return Directory.EnumerateFiles(Folder, "*.nvidia.json").Select(path =>
            {
                try { return JsonSerializer.Deserialize<NvidiaBackup>(File.ReadAllText(path)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
            }).OfType<NvidiaBackup>().OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static NvidiaBackup? LoadNvidia(Profile profile)
    {
        try { return File.Exists(NvidiaBackupPath(profile)) ? JsonSerializer.Deserialize<NvidiaBackup>(File.ReadAllText(NvidiaBackupPath(profile))) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void SaveNvidia(Profile profile, NvidiaBackup backup)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temporary = NvidiaBackupPath(profile) + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(backup));
            File.Move(temporary, NvidiaBackupPath(profile), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn($"Game settings: {profile.Title}'s NVIDIA backup couldn't be saved: {ex.Message}"); }
    }

    private static void DeleteNvidia(Profile profile)
    {
        try { DeletionGate.DeleteFile(NvidiaBackupPath(profile)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn($"Game settings: {profile.Title}'s NVIDIA backup couldn't be removed: {ex.Message}"); }
    }
}
