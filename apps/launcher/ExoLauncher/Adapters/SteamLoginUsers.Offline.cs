using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's own offline mode for the account it signs in as: <c>WantsOfflineMode</c> and <c>SkipOfflineModeWarning</c>
/// in that account's block of loginusers.vdf, the two values Steam's Go Offline writes (current Steam keeps both, as "0"
/// when online; steamui reads them at sign-in: "StartLogin: SetWantsOfflineMode"). It needs a saved sign-in
/// (RememberPassword). Exo writes them only while Steam is closed (Steam rewrites the file as it exits), keeps a copy of
/// the file and the values they had (<c>steam-offline.json</c>), and puts exactly those values back, removing a line it
/// added; every other byte stays Steam's. Never a password, token or any other value.
/// </summary>
internal static partial class SteamLoginUsers
{
    internal const string WantsOfflineKey = "WantsOfflineMode";
    internal const string SkipWarningKey = "SkipOfflineModeWarning";

    /// <summary>One account's two offline values as the file has them; null where its line is missing.</summary>
    internal sealed record OfflineFlags(string? WantsOfflineMode, string? SkipOfflineModeWarning);

    /// <summary>
    /// What Exo switched: the account's one-way key, the values it had before, and why (<see cref="ForOutage"/>, the
    /// default for a record written before reasons; <see cref="ForFamily"/>; <see cref="Chosen"/>).
    /// </summary>
    internal sealed record OfflineSwitch(string Account, string? WantsOfflineMode, string? SkipOfflineModeWarning, string? Reason = null)
    {
        internal OfflineFlags Before => new(WantsOfflineMode, SkipOfflineModeWarning);
        internal string Why => Reason ?? ForOutage;
    }

    /// <summary>Steam's servers were down: Exo tries it online again now and then.</summary>
    internal const string ForOutage = "outage";
    /// <summary>A Steam Family game Exo started offline: Steam goes back online once no Steam game runs.</summary>
    internal const string ForFamily = "family";
    /// <summary>The player turned offline mode on in Settings: it stays until they turn it off.</summary>
    internal const string Chosen = "chosen";

    /// <summary>A newer reason replaces a standing one only when it outranks it: the player's choice, then an outage, then a family game.</summary>
    internal static string Stronger(string standing, string reason) => Rank(reason) > Rank(standing) ? reason : standing;

    private static int Rank(string reason) => reason switch { Chosen => 3, ForOutage => 2, _ => 1 };

    private static string OfflineStatePath => Path.Combine(PathHelper.AppDataDir, "steam-offline.json");
    private static string OfflineBackupPath => Path.Combine(PathHelper.AppDataDir, "steam-offline-loginusers.vdf");

    /// <summary>The account's offline values; null when the account is not in the file.</summary>
    internal static OfflineFlags? ReadOffline(string text, string steamId64)
    {
        var block = Blocks(text).FirstOrDefault(entry => entry.SteamId64 == steamId64);
        if (block.SteamId64 is null) return null;
        var body = text[block.Open..(block.Close + 1)];
        return new OfflineFlags(FlagValue(body, WantsOfflineKey), FlagValue(body, SkipWarningKey));
    }

    /// <summary>Both values "1" for the account (a missing line added in the block's style), with what they were; null when the account is not in the file.</summary>
    internal static (string Text, OfflineFlags Before)? SetOffline(string text, string steamId64)
    {
        if (ReadOffline(text, steamId64) is not { } before) return null;
        var next = EditBlock(text, steamId64, body =>
            SetFlag(SetFlag(body, WantsOfflineKey, "1", addWhenMissing: true), SkipWarningKey, "1", addWhenMissing: true));
        return next is null ? null : (next, before);
    }

    /// <summary>The account's two values as <paramref name="before"/> had them (a line that was missing removed); null when the account is gone.</summary>
    internal static string? RestoreOffline(string text, string steamId64, OfflineFlags before) =>
        EditBlock(text, steamId64, body => Put(Put(body, WantsOfflineKey, before.WantsOfflineMode), SkipWarningKey, before.SkipOfflineModeWarning));

    /// <summary>The account Steam signs in as now, and whether Steam starts it in offline mode (Exo's switch or Steam's own).</summary>
    internal static (Account? Account, bool Offline) CurrentOffline(string steamRoot)
    {
        var text = SteamPlaytime.ReadSharedText(PathFor(steamRoot));
        if (text is null) return (null, false);
        var account = Current(Parse(text), SteamClientReady.ReadActiveUser(), ReadAutoLoginUser());
        return (account, account is not null && ReadOffline(text, account.SteamId64)?.WantsOfflineMode == "1");
    }

    /// <summary>Exo's switch while it stands; null when Exo has not put Steam offline.</summary>
    internal static OfflineSwitch? LoadOfflineSwitch()
    {
        try { return File.Exists(OfflineStatePath) ? JsonSerializer.Deserialize<OfflineSwitch>(File.ReadAllText(OfflineStatePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// While Steam is closed: offline mode on for <paramref name="steamId64"/> (else the account Steam signs in as), the
    /// file copied and the values it had kept first. A switch already standing keeps its first values and takes the
    /// stronger reason (<see cref="Stronger"/>). Offline mode Steam already had of its own stays the player's: no record.
    /// Null when written, else why not.
    /// </summary>
    internal static string? WriteOffline(string steamRoot, string? steamId64 = null, string reason = ForOutage)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        var path = PathFor(steamRoot);
        var accounts = SteamPlaytime.ReadSharedText(path) is { } first ? Parse(first) : [];
        var account = steamId64 is null ? Current(accounts, 0, ReadAutoLoginUser()) : accounts.FirstOrDefault(entry => entry.SteamId64 == steamId64);
        if (account is null || !account.Remembered) return "Steam plays offline only with a saved sign-in.";
        // A switch Exo made for another account goes back first.
        if (LoadOfflineSwitch() is { } other && other.Account != account.Key) WriteOnline(steamRoot);

        var text = SteamPlaytime.ReadSharedText(path);
        if (text is null || SetOffline(text, account.SteamId64) is not { } set) return "Steam has no saved sign-in on this PC.";
        var standing = LoadOfflineSwitch();
        if (standing is not null && Stronger(standing.Why, reason) != standing.Why) SaveOfflineSwitch(standing with { Reason = Stronger(standing.Why, reason) });
        if (string.Equals(set.Text, text, StringComparison.Ordinal)) return null;
        if (standing is null)
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            File.Copy(path, OfflineBackupPath, overwrite: true);
            SaveOfflineSwitch(new OfflineSwitch(account.Key, set.Before.WantsOfflineMode, set.Before.SkipOfflineModeWarning, reason));
        }
        WriteAtomically(path, set.Text);
        AppLog.Info($"Steam: offline mode on for its sign-in account ({reason}; loginusers.vdf backed up).");
        return null;
    }

    /// <summary>Settings' Offline mode turned on while Exo's switch stands: it stays, as the player's choice, until turned off.</summary>
    internal static void ChooseOffline()
    {
        if (LoadOfflineSwitch() is { } standing && standing.Why != Chosen) SaveOfflineSwitch(standing with { Reason = Chosen });
    }

    private static void SaveOfflineSwitch(OfflineSwitch record)
    {
        var staged = OfflineStatePath + ".exo-tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(record));
        File.Move(staged, OfflineStatePath, overwrite: true);
    }

    /// <summary>
    /// The account's <c>WantsOfflineMode</c> "0", for offline mode Steam had of its own (no record of Exo's): what Steam's
    /// own Go Online leaves. Every other byte, SkipOfflineModeWarning included, stays. Null when the account is not in
    /// the file.
    /// </summary>
    internal static string? SetOnline(string text, string steamId64) =>
        ReadOffline(text, steamId64) is null ? null : EditBlock(text, steamId64, body => SetFlag(body, WantsOfflineKey, "0", addWhenMissing: false));

    /// <summary>
    /// While Steam is closed: Exo's switch undone, the two values exactly as they were, the copy and the record removed.
    /// With <paramref name="steamsOwnFor"/> (the player turned offline mode off in Settings), offline mode Steam had of
    /// its own goes too for that account. True when the file says online for Exo's part.
    /// </summary>
    internal static bool WriteOnline(string steamRoot, string? steamsOwnFor = null)
    {
        if (HostMode.IsDisposable) return false;
        if (LoadOfflineSwitch() is not { } standing)
        {
            if (steamsOwnFor is null) return true;
            var own = SteamPlaytime.ReadSharedText(PathFor(steamRoot));
            if (own is null) return false;
            if (SetOnline(own, steamsOwnFor) is { } online && !string.Equals(online, own, StringComparison.Ordinal))
            {
                WriteAtomically(PathFor(steamRoot), online);
                AppLog.Info("Steam: its own offline mode turned off for its sign-in account.");
            }
            return true;
        }
        var path = PathFor(steamRoot);
        var text = SteamPlaytime.ReadSharedText(path);
        if (text is null) return false;
        var account = Parse(text).FirstOrDefault(entry => entry.Key == standing.Account);
        if (account is not null && RestoreOffline(text, account.SteamId64, standing.Before) is { } next &&
            !string.Equals(next, text, StringComparison.Ordinal))
            WriteAtomically(path, next);
        try
        {
            DeletionGate.DeleteFile(OfflineStatePath);
            DeletionGate.DeleteFile(OfflineBackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Steam: offline record not removed: " + ex.Message); }
        AppLog.Info(account is null ? "Steam: offline switch dropped; its account is no longer on this PC." : "Steam: offline mode values put back.");
        // Put back to offline mode Steam had of its own before Exo's switch: the player asked for online, so that goes too.
        return steamsOwnFor is null || LoadOfflineSwitch() is not null || WriteOnline(steamRoot, steamsOwnFor);
    }

    private static string? EditBlock(string text, string steamId64, Func<string, string> edit)
    {
        var block = Blocks(text).FirstOrDefault(entry => entry.SteamId64 == steamId64);
        if (block.SteamId64 is null) return null;
        var body = text[block.Open..(block.Close + 1)];
        return text[..block.Open] + edit(body) + text[(block.Close + 1)..];
    }

    private static string? FlagValue(string body, string key) =>
        FlagRegex(key).Match(body) is { Success: true } match ? match.Groups[2].Value : null;

    /// <summary>The key set to <paramref name="value"/>, or its line removed (with the line break before it) when null.</summary>
    private static string Put(string body, string key, string? value)
    {
        if (value is not null) return SetFlag(body, key, value, addWhenMissing: true);
        var line = new Regex("\\r?\\n[ \\t]*\"" + key + "\"[ \\t]+\"[^\"\\r\\n]*\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Match(body);
        return line.Success ? body.Remove(line.Index, line.Length) : body;
    }

    private static Regex FlagRegex(string key) =>
        new("(\"" + key + "\"\\s+\")([^\"]*)(\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
