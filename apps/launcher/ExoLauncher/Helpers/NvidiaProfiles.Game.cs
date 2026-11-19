using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// A game's own profile in the driver, the per-game layer on Exo's global profile: what Optimize sets for one game
/// (Profile Inspector's per-program profiles). The profile is found by the program's full path
/// (NvAPI_DRS_FindApplicationByName 0xEEE566B2), the only way NVIDIA's own profiles that match by a file beside the exe
/// are found (Call of Duty HQ's cod.exe is MW4 only when cod26.txt sits in its folder); a bare name answers -166.
/// <c>belongs</c> checks the profile found is the game's before anything is written. A driver without a profile for it
/// gets one when there is something to write: "Exo: title", the full path, the same file beside it. Profile name from
/// NvAPI_DRS_GetProfileInfo 0x61CD6FD6; CreateProfile 0xCC176068, CreateApplication 0x4347A9DE
/// (.claude/research/cod-dlss-model.md, section 5). Only ids the driver lists can be written (it refuses others).
/// NVDRS_APPLICATION_V4: version, isPredefined, appName, userFriendlyName, launcher, fileInFolder (4096 bytes each),
/// a flags word, commandLine: 20492 bytes. NVDRS_PROFILE_V1: version, profileName, gpuSupport, isPredefined,
/// numOfApps, numOfSettings: 4116 bytes.
/// </summary>
internal static unsafe partial class NvidiaProfiles
{
    private const int ApplicationSize = 20492, ProfileSize = 4116;
    private const uint ApplicationVersion = ApplicationSize | (4u << 16), ProfileVersion = ProfileSize | (1u << 16);
    private const int AppNameAt = 8, FileInFolderAt = 8 + 3 * UnicodeBytes;
    private const int ExecutableNotFound = -166, ProfileNotFound = -163;

    /// <summary>Where a game's program is and how its profile is recognized.</summary>
    internal sealed record GameProgram(string Exe, string Title, string FileInFolder, Func<string, string, bool> Belongs);

    /// <summary>
    /// The game's profile name and its own values of the ids asked (null: none of its own, so NVIDIA's or the global
    /// profile's applies), before and after the writes; Before equals After when nothing was written. <see cref="Skipped"/>:
    /// the ids the driver would not take for this game (it does not have them, or does not list them), which nothing was
    /// written for, so the caller never says they are on.
    /// </summary>
    internal sealed record GameState(bool Ok, string? Message, string? Profile, IReadOnlyDictionary<uint, uint?> Before, IReadOnlyDictionary<uint, uint?> After,
        IReadOnlyList<uint>? Skipped = null);

    /// <summary>What one write to a game's profile does: it goes to the driver, the driver won't take that id, or it failed.</summary>
    internal enum GameWrite
    {
        /// <summary>The driver holds the id: the write goes in.</summary>
        Write,
        /// <summary>This driver does not have the setting (or does not list it): that id is left out and the rest is written.</summary>
        Skip,
        /// <summary>Any other refusal fails the whole profile, as before.</summary>
        Fail,
    }

    /// <summary>
    /// A setting this driver refuses (NVAPI_NOT_SUPPORTED, and the setting-not-found one 621.01 answers for an id it does not
    /// list) is skipped, not fatal: a game's profile with a DLSS override the driver no longer has still gets the rest set.
    /// Every other status fails the profile. Pure, so every status is tested.
    /// </summary>
    internal static GameWrite Decide(int status) =>
        status is Ok ? GameWrite.Write : status is NotSupported or SettingNotFound ? GameWrite.Skip : GameWrite.Fail;

    /// <summary>
    /// The per-game call <see cref="ForGame"/> makes, for tests: a fake answers any game's profile with no driver at all.
    /// Setting it back to null restores <see cref="ForGame"/>.
    /// </summary>
    internal static Func<GameProgram, IReadOnlyCollection<uint>, IReadOnlyDictionary<uint, uint?>?, GameState>? ForGameSeam
    {
        get => ForcedForGame.Value;
        set => ForcedForGame.Value = value;
    }

    private static readonly AsyncLocal<Func<GameProgram, IReadOnlyCollection<uint>, IReadOnlyDictionary<uint, uint?>?, GameState>?> ForcedForGame = new();

    /// <summary><see cref="ForGame"/>, or the fake a test put in its place (<see cref="ForGameSeam"/>).</summary>
    internal static GameState ForGameLive(GameProgram game, IReadOnlyCollection<uint> ids, IReadOnlyDictionary<uint, uint?>? writes = null) =>
        ForcedForGame.Value is { } seam ? seam(game, ids, writes) : ForGame(game, ids, writes);

    /// <summary>Whether a game's profile can be asked at all: this PC's driver, or the fake a test put in its place.</summary>
    internal static bool GameProfilesUsable => ForcedForGame.Value is not null || Available;

    /// <summary>
    /// Reads <paramref name="ids"/> in the game's profile and, with <paramref name="writes"/>, sets them (null puts the
    /// driver's own value back) in one session and one save. A game with no profile reads as none of its own, and gets
    /// Exo's only when something is written. A setting the driver refuses is skipped (<see cref="Decide"/>) and named in
    /// <see cref="GameState.Skipped"/>; the save takes, and the rest is read back.
    /// </summary>
    internal static GameState ForGame(GameProgram game, IReadOnlyCollection<uint> ids, IReadOnlyDictionary<uint, uint?>? writes = null)
    {
        // Reads stay open to the page; only a request that would change a profile is refused.
        if (HostMode.IsDisposable && writes is { Count: > 0 })
            return new GameState(false, HostMode.Refusal, null, new Dictionary<uint, uint?>(), new Dictionary<uint, uint?>(), []);
        GameState? state = null;
        var result = RunSession(session =>
        {
            var app = Marshal.AllocHGlobal(ApplicationSize);
            var info = Marshal.AllocHGlobal(ProfileSize);
            var name = Marshal.AllocHGlobal(UnicodeBytes);
            try
            {
                Unicode(name, game.Exe);
                Clear(app, ApplicationSize, ApplicationVersion);
                nint profile = 0;
                string? profileName = null;
                var status = F4(0xEEE566B2)(session, name, (nint)(&profile), app);
                if (status is ExecutableNotFound or ProfileNotFound)
                {
                    var none = ids.ToDictionary(id => id, _ => (uint?)null);
                    if (writes is not { Count: > 0 } || writes.Values.All(v => v is null))
                    {
                        state = new GameState(true, null, null, none, none);
                        return new Result(true, null, null);
                    }
                    profileName = $"Exo: {game.Title}";
                    status = MakeProfile(session, profileName, game.Exe, game.FileInFolder, out profile);
                    if (status != Ok) return Failed("make the game's profile", status);
                }
                else if (status != Ok) return Failed("find the game's profile", status);
                else
                {
                    Clear(info, ProfileSize, ProfileVersion);
                    profileName = F3(0x61CD6FD6)(session, profile, info) == Ok ? Text(info + 4) : "";
                    if (!game.Belongs(profileName, Text(app + FileInFolderAt)))
                        return new Result(false, null, $"The NVIDIA driver's profile for this program isn't {game.Title}'s.");
                }
                var before = ReadValues(session, profile, ids);
                if (!before.Ok) return new Result(false, null, before.Message);
                var skipped = new List<uint>();
                if (writes is { Count: > 0 })
                {
                    foreach (var (id, value) in writes)
                    {
                        status = value is { } dword ? SetDword(session, profile, id, dword) : Reset(session, profile, id);
                        switch (Decide(status))
                        {
                            case GameWrite.Write: continue;
                            case GameWrite.Skip: skipped.Add(id); continue;
                            default: return Failed("change the game's setting", status);
                        }
                    }
                    // All skipped: nothing was written, so there is nothing to save; the caller says the driver doesn't have them.
                    if (skipped.Count < writes.Count)
                    {
                        status = F1(0xFCBC7E14)(session);
                        if (status != Ok) return Failed("save the driver profile", status);
                    }
                    if (skipped.Count > 0)
                        AppLog.Warn($"NVIDIA profile for {game.Title}: this driver doesn't take " +
                            string.Join(", ", skipped.Select(id => $"0x{id:X8}")) + "; left as they are.");
                    AppLog.Info($"NVIDIA profile for {game.Title} ({profileName}): " +
                        string.Join(", ", writes.Where(w => !skipped.Contains(w.Key))
                            .Select(w => $"0x{w.Key:X8}={(w.Value is { } v ? "0x" + v.ToString("X") : "default")}")) + ".");
                }
                var after = writes is { Count: > 0 } ? ReadValues(session, profile, ids) : before;
                state = new GameState(after.Ok, after.Message, profileName, before.Values, after.Values, skipped);
                return new Result(after.Ok, null, after.Message);
            }
            finally
            {
                Marshal.FreeHGlobal(app);
                Marshal.FreeHGlobal(info);
                Marshal.FreeHGlobal(name);
            }
        });
        return state is { } done && result.Ok ? done : new GameState(false, result.Message ?? state?.Message, null, new Dictionary<uint, uint?>(), new Dictionary<uint, uint?>(), []);
    }

    /// <summary>A profile of Exo's own for a program the driver has none for: its name, the full path, the file beside it.</summary>
    private static int MakeProfile(nint session, string profileName, string exe, string fileInFolder, out nint profile)
    {
        profile = 0;
        var info = Marshal.AllocHGlobal(ProfileSize);
        var app = Marshal.AllocHGlobal(ApplicationSize);
        try
        {
            Clear(info, ProfileSize, ProfileVersion);
            Unicode(info + 4, profileName);
            nint made = 0;
            var status = F3(0xCC176068)(session, info, (nint)(&made));
            if (status != Ok) return status;
            Clear(app, ApplicationSize, ApplicationVersion);
            Unicode(app + AppNameAt, exe);
            Unicode(app + FileInFolderAt, fileInFolder);
            status = F3(0x4347A9DE)(session, made, app);
            if (status == Ok) profile = made;
            return status;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(app);
        }
    }

    private static void Clear(nint block, int size, uint version)
    {
        new Span<byte>((void*)block, size).Clear();
        *(uint*)block = version;
    }

    /// <summary>An NvAPI_UnicodeString: UTF-16, zero-ended, within 2048 characters.</summary>
    private static void Unicode(nint at, string text)
    {
        var target = new Span<char>((void*)at, UnicodeBytes / 2);
        target.Clear();
        text.AsSpan(0, Math.Min(text.Length, target.Length - 1)).CopyTo(target);
    }

    private static string Text(nint at)
    {
        var chars = new ReadOnlySpan<char>((void*)at, UnicodeBytes / 2);
        var end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars[..end] : chars);
    }
}
