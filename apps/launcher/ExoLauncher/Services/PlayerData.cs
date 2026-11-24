using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// The one list of every file the player's choices live in, inside Exo's data folder. Backup, export and restore read only
/// this list (<see cref="LauncherBackup"/>); <see cref="Sync"/> says how each one reaches the player's other PCs, or why it
/// stays on this PC. Every other file name Exo uses is in <see cref="NotPlayerData"/> with why it is not the player's choice,
/// and a test reads every file name compiled into Exo and fails on one that is in neither list.
///
/// Secrets and tokens are never in a backup: they are sealed for this Windows user and listed under
/// <see cref="Why.Secret"/>.
/// </summary>
internal static class PlayerData
{
    /// <summary>What a backed-up file holds, which decides how it is checked before a restore writes it.</summary>
    internal enum Shape
    {
        /// <summary>One JSON object.</summary>
        JsonObject,
        /// <summary>One JSON array.</summary>
        JsonArray,
        /// <summary>A JSON object or array.</summary>
        Json,
        /// <summary>One JSON object a line; a torn line is dropped.</summary>
        JsonLines,
        /// <summary>A Riot ID (Name#Tag) as plain text.</summary>
        RiotId,
        /// <summary>A PNG or JPEG picture, as its name says.</summary>
        Picture,
        /// <summary>A Windows scheduled task as Windows exports it (XML).</summary>
        TaskXml,
        /// <summary>A Windows shortcut (.lnk), kept as what it does (ShellLinkFile.Link as JSON), never its bytes.</summary>
        Shortcut,
        /// <summary>A game's own settings file as Exo first found it: any bytes.</summary>
        Bytes,
    }

    /// <summary>
    /// One file, or one kind of file, the player's choices live in.
    /// <paramref name="Path"/> is its place in the data folder with forward slashes; a <c>*</c> stands for one file name part
    /// (letters, digits, '.', '-' and '_', starting with a letter or digit). <paramref name="ChosenBySettings"/>: only the
    /// files settings.json names are copied (a picture the cache holds but no setting uses stays out).
    /// <paramref name="Found"/> are the files as Exo's code names them ("path@Type"), for the test that keeps the list whole.
    /// <paramref name="OfThisMachine"/>: an undo record of what Exo wrote into this PC's games; a restore of another PC's backup leaves it out.
    /// </summary>
    internal sealed record PlayerFile(string Path, Shape Shape, long MaxBytes, string Sync, string[] Found, bool ChosenBySettings = false, string? Match = null, bool OfThisMachine = false)
    {
        internal Regex Pattern { get; } = new(Match ?? "^" + Regex.Escape(Path).Replace(@"\*", "[A-Za-z0-9][A-Za-z0-9._-]{0,159}") + @"\z", RegexOptions.CultureInvariant);

        /// <summary>The folder part of the path, when the file name is a pattern.</summary>
        internal string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";

        internal bool IsBinary => Shape is Shape.Picture or Shape.Bytes;

        internal IEnumerable<(string Path, string Place)> Claims => Found.Select(use => (use[..use.IndexOf('@')], use[(use.IndexOf('@') + 1)..]));
    }

    private const long Small = 4L * 1024 * 1024;
    private const long Large = 64L * 1024 * 1024;

    /// <summary>How a file reaches the player's other PCs (the account sync), or why it stays on this PC.</summary>
    internal static class Sync
    {
        internal const string Settings = "the synced part of the settings item (AccountSyncMap.SyncedSettings), uiPrefs one key at a time";
        internal const string Log = "an item per entry, merged by entry id";
        internal const string Cover = "a cover item per game and its picture when it fits one item";
        internal const string Wishlist = "a wish item per game, a removal as a deletion";
        internal const string Choice = "a choice item, newest wins";
        internal const string ThisPc = "this PC only";
    }

    internal static readonly PlayerFile[] Files =
    [
        new("settings.json", Shape.JsonObject, Small, Sync.Settings, ["settings.json@PathHelper", "settings.json@LauncherBackup"]),
        new("playtime.json", Shape.JsonObject, Large, Sync.Log + " (playtime)", ["playtime.json@PlaytimeService", "playtime.json@LauncherBackup"]),
        new("reported-hours.json", Shape.JsonObject, Small, Sync.Log + " (playtime-seed)", ["reported-hours.json@ReportedHours"]),
        new("riot-ledger/*.jsonl", Shape.JsonLines, Large, Sync.Log + " (riot-ledger)", ["riot-ledger/unassigned.jsonl@RiotLedger"],
            Match: @"^riot-ledger/(?:[0-9a-f]{20}|unassigned)\.jsonl\z"),
        new("riot-id.txt", Shape.RiotId, 1024, Sync.Choice + " (riot-id)", ["riot-id.txt@RiotIdStore"]),
        new("riot-matches.json", Shape.JsonObject, Large, Sync.ThisPc + ": accumulated Riot match durations read on this PC", ["riot-matches.json@RiotLocalMatchHistory"]),
        new("exo-imported-lifetime.json", Shape.JsonObject, Small, Sync.ThisPc + ": the tracker.gg hours from before the ledger, which become a Riot ID's ledger seed when that ID is used", ["exo-imported-lifetime.json@RiotLedger"]),
        new("window-placement.json", Shape.JsonObject, Small, Sync.ThisPc + ": the window's place on this PC's screens", ["window-placement.json@WindowPlacementStore"]),
        new("notes/notes.json", Shape.JsonObject, Small, Sync.ThisPc + ": notes are not synced yet", ["notes/notes.json@NoteStore"]),
        new("covers/custom-cover-*", Shape.Picture, GameCoverImageStore.MaxBytes, Sync.Cover, [], ChosenBySettings: true),
        new("covers/folder-art-*", Shape.Picture, FolderArtStore.MaxBytes, Sync.ThisPc + ": a folder picture is too big for one item; the folder itself syncs with the settings", [], ChosenBySettings: true),
        new("installed-games.json", Shape.JsonObject, Small, Sync.ThisPc + ": the games' folders are this PC's", ["installed-games.json@InstalledGameCatalog"]),
        new("store/wishlist-v2.json", Shape.JsonArray, Small, Sync.Wishlist, ["wishlist-v2.json@StoreService"]),
        new("web-client-preferences.json", Shape.JsonObject, 64 * 1024, Sync.Choice + " (music-service); the permission answers stay on this PC", ["web-client-preferences.json@WebClientPreferences"]),
        new("mouse/profiles.json", Shape.JsonObject, 512 * 1024, Sync.ThisPc + ": a profile is for the mouse on this PC", ["profiles.json@WebHostBridge"]),
        new("mouse/presets.json", Shape.JsonObject, 512 * 1024, Sync.ThisPc + ": presets are for the mouse on this PC", ["presets.json@WebHostBridge"]),
        new("controllers/games.json", Shape.JsonArray, Small, Sync.ThisPc + ": per-game controller settings for the pads on this PC", ["controllers/games.json@ControllerLab"]),
        new("controllers/*.json", Shape.JsonObject, Small, Sync.ThisPc + ": a pad's own memory (its factory calibration) is kept where the pad was read", []),
        new("keyboard-backups/*.json", Shape.JsonObject, Small, Sync.ThisPc + ": Wooting keyboard profile backups saved on this PC", []),
        new("exo-eq.json", Shape.JsonObject, Small, Sync.ThisPc + ": Exo EQ's outputs are this PC's audio devices, by Windows' ids for them", ["exo-eq.json@ExoEq"]),
        new("game-settings/window-modes.json", Shape.JsonObject, Small, Sync.ThisPc + ": each game's window mode, which fits this PC's screens", ["window-modes.json@GameSettings"]),
        new("game-settings/*.json", Shape.Json, Small, Sync.ThisPc + ": what Optimize changed in this PC's game files, to put back", ["kept.json@GameSettings", ".on.json@GameSettings", ".nvidia.json@GameSettings", "game-settings/sync.json@CompetitiveSync"], OfThisMachine: true),
        new("game-settings/*", Shape.Bytes, Small, Sync.ThisPc + ": a game's own file as Exo first found it, to put back", [], OfThisMachine: true),
        new("keep.json", Shape.JsonObject, Small, Sync.ThisPc + ": what Exo wrote into this PC's games and settings, and what it has seen since, to read back, watch and put back; its paths and ids mean nothing on another PC, and a line the player kept as theirs on one PC is no evidence on another", ["keep.json@Keep"], OfThisMachine: true),
        new("session-recorder.json", Shape.JsonObject, Small, Sync.ThisPc + ": each game's frame times by session and settings version (a histogram, the average, the lows and the stalls), measured on this PC's hardware and never sent anywhere", ["session-recorder.json@SessionStore"], OfThisMachine: true),
        new("autopilot-reports.json", Shape.JsonObject, Small, Sync.ThisPc + ": what Autopilot saw in each game session on this PC (the frames, the stutters and what came with them, the background apps it learned to hold back), measured on this PC's hardware and never sent anywhere", ["autopilot-reports.json@AutopilotReports"], OfThisMachine: true),
        new("autopilot-journal.json", Shape.JsonObject, Small, Sync.ThisPc + ": each change Autopilot made while a game runs (a process's priority, efficiency mode, disk and memory priority, a paused app, core parking) and Windows Game Mode while Autopilot is on, with what each held before, so it all goes back after the game, after a crash or when Autopilot is turned off; process ids mean nothing on another PC", ["autopilot-journal.json@AutopilotJournal"], OfThisMachine: true),
        new("frame-gen/games.json", Shape.Json, Small, Sync.ThisPc + ": which games have frame generation on and what Exo wrote into their folders, to undo it", ["frame-gen/games.json@DlssSwapService"], OfThisMachine: true),
        new("replaced.json", Shape.Json, Small, Sync.ThisPc + ": the apps the player removed, which Exo removes again after a driver brings them back", ["replaced.json@ReplacedApps"]),
        new("startup-removed.json", Shape.JsonArray, Small, Sync.ThisPc + ": what Exo took out of this PC's start-up", ["startup-removed.json@StartupEntries"]),
        new("shader-rebuild.json", Shape.JsonObject, Small, Sync.ThisPc + ": which graphics driver this PC's shaders were last compiled again for, and the games still queued for it", ["shader-rebuild.json@ShaderRebuild"], OfThisMachine: true),
        new("unreal-shader-ini.json", Shape.JsonObject, Small, Sync.ThisPc + ": Unreal Engine.ini shader pipeline cache lines Exo added, to put back", ["unreal-shader-ini.json@UnrealShaderIni"], OfThisMachine: true),
        new("startup-guard.json", Shape.Json, Small, Sync.ThisPc + ": which removed start-up items the guard keeps off, when they came back, and the program setting Exo changed, to put back", ["startup-guard.json@StartupGuard"], OfThisMachine: true),
        new("startup-removed/*", Shape.TaskXml, Small, Sync.ThisPc, []),
        new("startup-removed/*/*", Shape.Shortcut, Small, Sync.ThisPc, []),
    ];

    /// <summary>One of the page's choices and where it belongs: <paramref name="Syncs"/> to every PC of the player's, or to this PC, and why.</summary>
    internal sealed record UiPref(string Key, bool Syncs, string Why);

    /// <summary>
    /// The page's own choices Exo keeps in settings.json (AppSettings.UiPrefs, ui/src/lib/uiPrefs.ts), each at most
    /// <see cref="MaxUiPrefBytes"/> of JSON. A key not listed is refused, so a choice never lands somewhere a backup misses.
    /// Every one is backed up; only the choices about the player sync, one key at a time (AccountSyncMap). A choice about this
    /// PC's hardware stays on it: another PC must never install a driver or pick a device because this one did.
    /// </summary>
    internal static readonly UiPref[] UiPrefs =
    [
        new("discord", Syncs: true, "Discord's look, text size and streamer mode: how the player likes Discord"),
        new("discordCamera", Syncs: false, "the camera chosen for Discord: a device id of this PC"),
        new("discordPerformance", Syncs: true, "Discord's Performance mode: a look the player chose"),
        new("driverBranch", Syncs: false, "Game Ready or Studio: decides the driver Exo installs on this PC's GPU"),
        new("wearing", Syncs: false, "the headphones worn on this PC's jack"),
        new("shadersOrder", Syncs: true, "the order the player wants games' shaders built in"),
        new("goLiveQuality", Syncs: false, "Go Live's resolution and rate, which fit this PC's screen and upload"),
        new("goLivePreview", Syncs: true, "whether the player's own stream shows as a tile: a look"),
        new("voiceChat", Syncs: true, "whether the call's chat panel shows: a look"),
        new("discordPinnedDms", Syncs: true, "pinned Discord DM channel ids, in pinned order"),
        new("tourSeen", Syncs: true, "whether the player completed or skipped the guided tour"),
    ];

    internal static readonly string[] UiPrefKeys = [.. UiPrefs.Select(pref => pref.Key)];

    internal static bool UiPrefSyncs(string key) => UiPrefs.Any(pref => pref.Key == key && pref.Syncs);

    internal const int MaxUiPrefBytes = 16 * 1024;

    /// <summary>The entry a backed-up name belongs to: the first that matches, so an exact name wins over a pattern.</summary>
    internal static PlayerFile? Of(string name)
    {
        // A file half written (.tmp) is never the player's choice.
        if (name.Length == 0 || name.Length > 260 || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var file in Files)
        {
            if (!file.Pattern.IsMatch(name)) continue;
            // A pattern's shape is part of its name: a task export is .xml, a shortcut .lnk, a picture .png or .jpg.
            var fits = file.Shape switch
            {
                Shape.TaskXml => name.EndsWith(".xml", StringComparison.Ordinal),
                Shape.Shortcut => name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase),
                Shape.Picture => name.EndsWith(".png", StringComparison.Ordinal) || name.EndsWith(".jpg", StringComparison.Ordinal),
                _ => true,
            };
            if (fits) return file;
        }
        return null;
    }

    /// <summary>Why a file Exo keeps is not the player's choice.</summary>
    internal enum Why
    {
        /// <summary>A token, key or sign-in, sealed for this Windows user: never in a plain backup.</summary>
        Secret,
        /// <summary>Exo can fetch or work it out again.</summary>
        Cache,
        /// <summary>This PC's own state: a queue, a log, a marker, what a run was doing.</summary>
        ThisPc,
        /// <summary>Another program's file that Exo reads or writes in that program's place.</summary>
        Elsewhere,
        /// <summary>A file an older Exo kept, which this one only removes.</summary>
        Retired,
        /// <summary>A file in what Exo installs or downloads (a tool's manifest, a licence).</summary>
        Shipped,
    }

    /// <summary>
    /// A file Exo's code names that is not the player's choice: its path as the code builds it (the folders named right before
    /// the file), the types that name it, why, and its own reason in a few words.
    /// </summary>
    internal sealed record NotPlayers(string Path, string Places, Why Why, string Reason)
    {
        internal IEnumerable<(string Path, string Place)> Claims => Places.Split(',').Select(place => (Path, place.Trim()));
    }

    /// <summary>Every other file Exo's code names, each with why it is not the player's choice.</summary>
    internal static readonly NotPlayers[] NotPlayerData =
    [
        // Tokens, keys and sign-ins, sealed for this Windows user: never in a plain backup.
        new("account-session.bin", "AccountSessionStore", Why.Secret, "the Exo account session token, sealed with DPAPI"),
        new("auth.json", "GogAuthService", Why.Secret, "GOG's sign-in tokens as Exo keeps them"),
        new("credentials.json", "GogAuthService", Why.Secret, "GOG's refresh token for gogdl"),
        new("gogdl/credentials.json", "GogAuthService", Why.Secret, "gogdl's own copy of the GOG tokens"),
        new("heroic/gog_store/auth.json", "GogAuthService", Why.Secret, "Heroic's GOG sign-in, read to sign in once"),
        new("discord-accounts.bin", "DiscordAccounts", Why.Secret, "the Discord accounts' tokens, sealed with DPAPI"),
        new("discord-client.bin", "DiscordToken", Why.Secret, "the Discord session token, sealed with DPAPI"),
        new("voice/my-voice.bin", "Voiceprint", Why.Secret, "the learned voiceprint, sealed with DPAPI and learned again by itself"),
        new("steam-account.bin", "StoreSecretVault", Why.Secret, "the Steam sign-in, sealed with DPAPI"),
        // What Exo fetches or works out again.
        new("-intel.json", "NetworkDrivers", Why.Cache, "Intel's network driver list as last fetched"),
        new("-newest.json", "ChipsetUpdate", Why.Cache, "the newest chipset driver as last looked up"),
        new("-tight.png", "DeviceImages", Why.Cache, "a device picture cut to its outline, made again from the download"),
        new(".local.jpg", "CoverArtService", Why.Cache, "a store cover saved beside the cache"),
        new(".local_2x.jpg", "CoverArtService", Why.Cache, "a store cover at double size"),
        new("_2x.jpg", "CoverArtService", Why.Cache, "a store picture's double-size copy"),
        new("_card.png", "CoverArtService", Why.Cache, "a cover cut to a card, made again from the cover"),
        new("cover.jpg", "CoverArtService", Why.Cache, "a game's own JPEG cover in its folder, read for art"),
        new("cover.png", "CoverArtService", Why.Cache, "a game's own PNG cover in its folder, read for art"),
        new("icon.png", "CoverArtService", Why.Cache, "a game's own icon in its folder"),
        new("library.jpg", "CoverArtService", Why.Cache, "a game's own library picture in its folder"),
        new("library_600x900.jpg", "CoverArtService", Why.Cache, "Steam's portrait cover in Steam's cache"),
        new("library_600x900_2x.jpg", "CoverArtService", Why.Cache, "Steam's portrait cover at double size"),
        new("library_capsule.jpg", "CoverArtService", Why.Cache, "Steam's capsule picture"),
        new("library_capsule_2x.jpg", "CoverArtService", Why.Cache, "Steam's capsule picture at double size"),
        new("library_header_2x.jpg", "CoverArtService", Why.Cache, "Steam's library header at double size"),
        new("library_hero.jpg", "CoverArtService", Why.Cache, "Steam's hero picture"),
        new("library_hero_2x.jpg", "CoverArtService", Why.Cache, "Steam's hero picture at double size"),
        new("poster.png", "CoverArtService", Why.Cache, "a game's own poster in its folder"),
        new("banner-fill-verdicts.json", "CoverArtService", Why.Cache, "which covers fill a banner, worked out again from the pictures"),
        new("title-steam-map.json", "CoverArtService", Why.Cache, "titles matched to Steam apps for covers"),
        new("cover-misses.json", "CoverMissMemory", Why.Cache, "games no cover was found for, asked again later"),
        new("image.png", "DiscordImages", Why.Cache, "a Discord picture's file name while it is saved"),
        new("achievements.json", "AchievementService", Why.Cache, "achievements read from the stores"),
        new("apps-icons.json", "WebHostBridge", Why.Cache, "the apps list's icons"),
        new("apps-list.json", "WebHostBridge", Why.Cache, "the apps list's last answer"),
        new("bios-newest.json", "BiosUpdate", Why.Cache, "the newest BIOS for this board as last looked up"),
        new("catalog-trusted.json", "DlssSwapService", Why.Cache, "the upscaler catalog that last checked out"),
        new("catalog-v1.json", "DlssSwapService", Why.Cache, "an older upscaler catalog"),
        new("catalog-v2.json", "DlssSwapService", Why.Cache, "the upscaler catalog as fetched"),
        new("drive-heat.json", "DriveHeat", Why.Cache, "drive temperatures read lately"),
        new("driver-advisories.json", "DriverAdvisories", Why.Cache, "driver advisories as fetched"),
        new("epic-owned.json", "EpicAdapter", Why.Cache, "the Epic games owned, read again from Epic"),
        new("epic-playtime.json", "EpicPlaytime", Why.Cache, "Epic's own playtime readings"),
        new("foryou-v1.json", "StoreService", Why.Cache, "the Store's picks for you, made again from your library"),
        new("frame-gen-games.json", "FrameGenFacts", Why.Cache, "which games support frame generation, from Steam and PCGamingWiki"),
        new("game-hdr.json", "GameHdr", Why.Cache, "whether each game outputs HDR, learned again"),
        new("game-wiki.json", "GameWiki", Why.Cache, "PCGamingWiki facts per game"),
        new("hdr-games.json", "GameWiki", Why.Cache, "PCGamingWiki's list of HDR games"),
        new("gog-owned.json", "GogOwnedLibraryService", Why.Cache, "the GOG games owned, read again from GOG"),
        new("gpu-drivers.json", "GpuDriverUpdate", Why.Cache, "the GPU drivers on offer as fetched"),
        new("home-v2.json", "StoreService", Why.Cache, "an older Store home page, removed"),
        new("home-v3.json", "StoreService", Why.Cache, "an older Store home page, removed too"),
        new("home-v4.json", "StoreService", Why.Cache, "the previous Store home page, removed"),
        new("home-v5.json", "StoreService", Why.Cache, "the Store home page as last loaded"),
        new("home.json", "StoreService", Why.Cache, "the first Store home page, removed"),
        new("items-v3.json", "StoreService", Why.Cache, "Steam's public facts about games the Store showed"),
        new("tags-v1.json", "StoreService", Why.Cache, "Steam's tag names"),
        new("titles-v1.json", "StoreService", Why.Cache, "game titles the Store's picks are made from"),
        new("library-cache.json", "PathHelper", Why.Cache, "the library as last scanned, scanned again"),
        new("nvidia-drivers.json", "NvidiaDriverService", Why.Cache, "NVIDIA's driver list as fetched"),
        new("offers.json", "WindowsUpdateDrivers", Why.Cache, "drivers Windows Update offers, read again"),
        new("runtimes-graphics.json", "DependencyService", Why.Cache, "the graphics runtimes found on this PC"),
        new("dotnet-releases.json", "DependencyService", Why.Cache, "Microsoft's .NET release list as fetched"),
        new("status-v2.json", "DlssSwapService", Why.Cache, "the upscaler files' status as checked"),
        new("steam-app-types.json", "SteamStoreAppTypes", Why.Cache, "which Steam apps are games, demos or tools"),
        new("steam-family.json", "SteamFamilyShares", Why.Cache, "Steam family-shared games as read: ids, names and owners' account ids"),
        new("steam-owned.json", "SteamOwnershipCatalog", Why.Cache, "the Steam games owned, read again from Steam"),
        new("swapper-refused.json", "DlssSwapService", Why.Cache, "upscaler releases that did not check out"),
        new("swapper-releases.json", "DlssSwapService", Why.Cache, "upscaler releases as fetched"),
        new("update.json", "ChipsetUpdate", Why.Cache, "the chipset update as last looked up"),
        new("upscaler-available.json", "DlssSwapService", Why.Cache, "which upscaler updates are out"),
        new("vendor-releases.json", "DlssSwapService", Why.Cache, "the vendors' upscaler releases"),
        new("verified.json", "DlssSwapService", Why.Cache, "upscaler files whose hashes were checked"),
        new("discord/web-build.txt", "DiscordRest", Why.Cache, "Discord's web build number, read again"),
        new("whats-new.json", "AppUpdateService", Why.Cache, "the next version's What's New, fetched with the update"),
        new("windows-release.json", "PcAbout", Why.Cache, "the Windows release names as fetched"),
        // This PC's own state, or Windows, driver and client values with what they held before: a Windows reinstall resets
        // them, so the way back means nothing on a new install. Optimize's undo data is different: games' files outlive Windows.
        new("-layout.json", "MouseCore", Why.ThisPc, "the mouse's interrupt layout before Exo spread it"),
        new("account-sync-pending.json", "AccountSyncHost", Why.ThisPc, "changes waiting for a sign-in to sync"),
        new("account-sync.json", "AccountSyncHost,AccountSyncStore", Why.ThisPc, "the sync queue and cursor for the account"),
        new("afmf3-preview.json", "Afmf3Preview", Why.ThisPc, "the AMD driver values before the AFMF preview"),
        new("amd-games.json", "RadeonProfileService", Why.ThisPc, "AMD per-game values before Exo set them"),
        new("amd-profile.json", "RadeonProfileService", Why.ThisPc, "AMD global values before Exo set them"),
        new("apps-seen.json", "AppsMemory", Why.ThisPc, "apps this PC has shown before"),
        new("backup-answered.txt", "LauncherBackup", Why.ThisPc, "this install answered the restore offer"),
        new("backup-device.txt", "LauncherBackup", Why.ThisPc, "this PC's short id in backup names"),
        new("backup-last.json", "LauncherBackup", Why.ThisPc, "how this PC's last backup went, for Settings"),
        new("bios-prep.json", "BiosPrep", Why.ThisPc, "a BIOS update waiting for a restart"),
        new("bitlocker-paused.txt", "BiosPrep", Why.ThisPc, "BitLocker paused for one BIOS update"),
        new("staged.txt", "BiosPrep", Why.ThisPc, "the BIOS file staged for a restart"),
        new("cleanup.json", "WebHostBridge", Why.ThisPc, "how much Clean up freed on this PC"),
        new("consent.json", "Telemetry", Why.ThisPc, "the old telemetry answer, no longer read"),
        new("install.json", "Telemetry", Why.ThisPc, "this install's random telemetry id"),
        new("queue.jsonl", "Telemetry", Why.ThisPc, "telemetry events waiting to be sent"),
        new("sent.jsonl", "Telemetry", Why.ThisPc, "telemetry events already sent"),
        new("crash-report-limit.json", "CrashReportLimiter", Why.ThisPc, "how many crash reports went out today"),
        new("device-id.txt", "AccountDevice,AccountDevices", Why.ThisPc, "this PC's random device id for the account"),
        new("display-links.json", "DisplayLink", Why.ThisPc, "what was learned about this PC's screens and cables"),
        new("download-intents.json", "LaunchOrchestrator", Why.ThisPc, "downloads queued on this PC"),
        new("epic-quiet.json", "EpicQuietSettings", Why.ThisPc, "Epic's settings before Exo quieted them"),
        new("exo-drives.json", "Drives", Why.ThisPc, "this PC's drive settings before Exo changed them"),
        new("exo-network-best.json", "NetworkBest", Why.ThisPc, "this PC's DNS and TCP settings before Best settings › Network, and the items turned off"),
        new("exo-network-tune.json", "NetworkTune", Why.ThisPc, "network adapter values before Exo tuned them"),
        new("exo-power-plan.json", "PowerPlan", Why.ThisPc, "the power plan before Exo's"),
        new("exo-system-tuning.json", "SystemTuning", Why.ThisPc, "this PC's Windows settings before Best settings › System, the items turned off, and the last two latency checks"),
        new(".latency.json", "SystemTuning", Why.ThisPc, "a temporary DPC and ISR latency check answer file written in the Windows temp folder"),
        new("exo-shared.json", "WebViewHostProfile", Why.ThisPc, "marks the shared WebView profile"),
        new("exo-snapshot.json", "ShadersAddon", Why.ThisPc, "the shader engine's last state"),
        new("gpu-preference.json", "GpuPreference", Why.ThisPc, "Windows' per-game GPU choice before Exo set it"),
        new("gpu-profile.json", "GpuProfileService", Why.ThisPc, "NVIDIA global values before Exo set them"),
        new("intel-games.json", "IntelProfileService", Why.ThisPc, "Intel per-game values before Exo set them"),
        new("intel-profile.json", "IntelProfileService", Why.ThisPc, "Intel global values before Exo set them"),
        new("junk-clean.json", "JunkAutoClean", Why.ThisPc, "when the junk clean last ran"),
        new("compress.json", "GameCompressionStore", Why.ThisPc, "which games on this PC Windows compressed, and the yes for one with anti-cheat"),
        new("keep.txt", "PackagedSmoke", Why.ThisPc, "a marker for the packaged smoke test"),
        new("smoke-result.json", "PackagedSmoke", Why.ThisPc, "the packaged smoke test's result"),
        new("last-exo-version.txt", "WebViewCacheCleaner", Why.ThisPc, "the version whose WebView cache was cleared"),
        new("legacy-task-cleanup-v1.json", "ExoScheduledTaskCleanup", Why.ThisPc, "old scheduled tasks already removed"),
        new("lucid-update.json", "LucidRetire", Why.ThisPc, "what an earlier Exo kept for the Lucid it hosted, deleted once"),
        new("lucid-held.json", "LucidRetire", Why.ThisPc, "Lucid's settings an earlier Exo held for a game to end, deleted once"),
        new("autoeq-index.txt", "AutoEqCatalog", Why.ThisPc, "AutoEq's headphone list, kept a week"),
        new("mouse-core-backup.json", "WebHostBridge", Why.ThisPc, "interrupt affinity values before Exo spread the mouse"),
        new("msi-mode-backup.json", "InterruptMode", Why.ThisPc, "MSI mode values before Exo changed them"),
        new("removals.json", "RemovalHistory", Why.ThisPc, "what was uninstalled on this PC and when"),
        new("retry.json", "GogOwnedLibraryService", Why.ThisPc, "a GOG library read to try again"),
        new("setups-held.json", "Setups", Why.ThisPc, "a Setup's way back during a restart"),
        new("shader-cache-watch.json", "ShaderCacheWatch", Why.ThisPc, "shader caches seen on this PC"),
        new("shader-wipe.json", "ShaderWipeMarks", Why.ThisPc, "shader caches cleared after a driver change"),
        new("shaders-prepared.json", "ShaderPrepare", Why.ThisPc, "games whose shaders were built for this GPU and driver"),
        new("state.json", "ScsKillerStatus,TuneStore", Why.ThisPc, "the shader engine's and AutoTune's run state"),
        new("applied-at.txt", "TuneStore", Why.ThisPc, "when AutoTune last applied a value, for the crash guard window"),
        new("data/games.json", "ScsKillerStatus", Why.ThisPc, "the shader engine's old game list, read to move it"),
        new("games.json", "ScsKillerStatus,WebHostBridge", Why.ThisPc, "the shader engine's game list and Steam's controller folder file name"),
        new("testing.json", "TuneStore", Why.ThisPc, "an AutoTune test in progress"),
        new("steam-offline.json", "SteamLoginUsers", Why.ThisPc, "Steam's offline values before Exo set them"),
        new("update-pending.json", "UpdateStartupHealth", Why.ThisPc, "an update waiting for its health check"),
        new("update-result.json", "UpdateOutcome", Why.ThisPc, "how the last update went"),
        new("version-watch.json", "VersionWatch", Why.ThisPc, "versions of tools seen on this PC"),
        new("windows-update-progress.json", "WindowsUpdateDrivers", Why.ThisPc, "a driver install in progress"),
        new("windows-update-result.json", "WindowsUpdateDrivers", Why.ThisPc, "how the last driver install went"),
        new("written-games.json", "DlssSwapService", Why.ThisPc, "games whose upscaler files Exo wrote, with their hashes"),
        new("learned.json", "DlssSwapService", Why.ThisPc, "what frame generation taught Exo in each game on this PC"),
        new("launch-handoff.json", "UpdateHandOff", Why.ThisPc, "a launch handed to the next Exo across an update"),
        new("staged-update.json", "AppUpdateService", Why.ThisPc, "the update installer kept for after a game"),
        new("skipped-update.json", "AppUpdateService", Why.ThisPc, "the update that did not take, not offered again by itself"),
        new("delta-failed.json", "AppUpdateService", Why.ThisPc, "the version whose file-level update failed, so its next try uses the installer"),
        new("app-file-hashes.json", "AppFileHashes,AppUpdateService", Why.Cache, "the SHA-256 of each installed app file, for the next file-level update"),
        // Another program's files that Exo reads or writes in that program's place.
        new(".1.0.l.txt", "GameSettings", Why.Elsewhere, "Call of Duty's settings file suffix in its own folder"),
        new(".pm.1.0.l.txt", "GameSettings", Why.Elsewhere, "Call of Duty's profile settings suffix"),
        new("cod26.txt", "GameSettings", Why.Elsewhere, "Call of Duty's config name"),
        new("s.1.0.cod26.txt", "GameSettings", Why.Elsewhere, "Call of Duty's settings file"),
        new("cs2_video.txt", "GameSettings", Why.Elsewhere, "Counter-Strike 2's video settings"),
        new("GameUserSettings.ini", "EpicPlaytime,GameHdr,GameSettings", Why.Elsewhere, "an Unreal game's own settings"),
        new("Engine.ini", "GameHdr", Why.Elsewhere, "an Unreal game's engine settings"),
        new("settings/settings.ini", "ScsKillerStatus", Why.Elsewhere, "a Stingray game's own settings file, read to identify its engine"),
        new("TASystemSettings.ini", "GameSettings", Why.Elsewhere, "a game's own system settings"),
        new("Remedy/CONTROLResonant/renderer.ini", "GameSettings", Why.Elsewhere, "Control's renderer settings"),
        new("renderer.ini", "GameSettings", Why.Elsewhere, "a Remedy game's renderer settings"),
        new("poe2_production_Config.ini", "GameSettings", Why.Elsewhere, "Path of Exile 2's settings"),
        new("UserSettings.json", "GameSettings", Why.Elsewhere, "a game's user settings"),
        new("system.xml", "GameSettings", Why.Elsewhere, "a game's system settings XML"),
        new("user_engine_option_save.xml", "GameSettings", Why.Elsewhere, "a game's saved engine options"),
        new("held.json", "DlssSwapService", Why.ThisPc, "what Exo took out of a game started without its anti-cheat, held for its next session"),
        new("exo-frame-gen.json", "DlssSwapService,GameSettings", Why.Elsewhere, "the note Exo leaves in a game's folder beside frame generation"),
        new(".exo-upscale.json", "DlssSwapService", Why.Elsewhere, "the note Exo leaves beside a swapped upscaler file"),
        new("OptiScaler.ini", "DlssSwapService", Why.Elsewhere, "OptiScaler's settings in a game's folder"),
        new("optiscaler.ini", "DlssSwapService", Why.Elsewhere, "OptiScaler's settings, lower-case name"),
        new("dlssg_sm86.ini", "DlssSwapService", Why.Elsewhere, "the DLSSG SM86 mod's settings in a game's folder"),
        new("dlssg_to_fsr3.ini", "DlssSwapService", Why.Elsewhere, "the DLSSG to FSR3 mod's settings"),
        new("fakenvapi.ini", "DlssSwapService", Why.Elsewhere, "fakenvapi's settings in a game's folder"),
        new("fsr2dlss.ini", "DlssSwapService", Why.Elsewhere, "the FSR2 to DLSS mod's settings"),
        new("helixsr.ini", "DlssSwapService", Why.Elsewhere, "HelixSR's settings in a game's folder"),
        new("nvngx.ini", "DlssSwapService", Why.Elsewhere, "NVIDIA NGX's settings in a game's folder"),
        new("rtx40mfg-ui.ini", "DlssSwapService", Why.Elsewhere, "the RTX 40 MFG mod's settings"),
        new("rtxmfg-ui.ini", "DlssSwapService", Why.Elsewhere, "the RTX MFG mod's settings"),
        new("RTXMFG-Universal.json", "DlssSwapService", Why.Elsewhere, "the RTX MFG universal mod's manifest"),
        new("rtxmfg-universal.json", "DlssSwapService", Why.Elsewhere, "the RTX MFG universal mod's manifest, lower-case"),
        new("rtxmfg-universal.status.json", "DlssSwapService", Why.Elsewhere, "the RTX MFG universal mod's status file"),
        new("scskiller.ini", "DlssSwapService", Why.Elsewhere, "the shader engine's settings in its folder"),
        new("dlssnr_on_amd.ini", "DlssSwapService", Why.Elsewhere, "the DLSS denoiser on AMD mod's settings in a game's folder"),
        new("danielblnc_attribution.txt", "DlssSwapService", Why.Elsewhere, "the attribution note of the DLSS denoiser on AMD mod"),
        new("ue4ss-settings.ini", "DlssSwapService", Why.Elsewhere, "UE4SS's settings in a game's folder"),
        new("EULA.txt", "NvidiaDriverService", Why.Elsewhere, "NVIDIA's licence in the driver package"),
        new("ListDevices.txt", "NvidiaDriverService", Why.Elsewhere, "the driver package's list of cards"),
        new("setup.cfg", "NvidiaDriverService", Why.Elsewhere, "the NVIDIA installer's setup file"),
        new("Config/InstallManifest.json", "GpuDriverUpdate", Why.Elsewhere, "the AMD driver package's manifest"),
        new("AppxManifest.xml", "RealStartupHost", Why.Elsewhere, "a Store app's manifest, read for its start-up task"),
        new("desktop.ini", "StartupEntries", Why.Elsewhere, "Windows' folder file in the Startup folder, skipped"),
        new("Settings.json", "DependencyService", Why.Elsewhere, "an anti-cheat's own settings file"),
        new("results.txt", "DependencyService", Why.Elsewhere, "a runtime installer's results"),
        new("LucidEQ/settings.json", "LucidRetire", Why.Elsewhere, "Lucid's own settings, read once to carry them into Exo EQ"),
        new("config.txt", "ExoEq,LucidRetire", Why.Elsewhere, "Equalizer APO's config, which gets Exo EQ's one Include line"),
        new("exo.txt", "ExoEq,ExoEqConfig", Why.Elsewhere, "Exo EQ's file in Equalizer APO's config folder, written from exo-eq.json"),
        new("RiotLocalMachine.ini", "RiotAccounts", Why.Elsewhere, "the Riot client's machine settings"),
        new("update-status.json", "RiotProductMetadata", Why.Elsewhere, "the Riot client's update status"),
        new("achievement_progress.json", "SteamLibraryCacheAchievementProvider", Why.Elsewhere, "Steam's achievement progress cache"),
        new("connection_log.txt", "SteamOutage", Why.Elsewhere, "Steam's connection log"),
        new("steamui_login.txt", "SteamOutage", Why.Elsewhere, "Steam's sign-in log"),
        new("gameprocess_log.txt", "SteamGameProcessLog", Why.Elsewhere, "Steam's game process log"),
        new("gameprocess_log.previous.txt", "SteamGameProcessLog", Why.Elsewhere, "Steam's previous game process log"),
        new("logs/gameprocess_log.txt", "SteamGameProcessLog", Why.Elsewhere, "Steam's game process log in its logs folder"),
        new("logs/bootstrap_log.txt", "SteamClientUpdate", Why.Elsewhere, "Steam's bootstrap log"),
        new("logs/content_log.txt", "SteamContentLogProgress,WebHostBridge", Why.Elsewhere, "Steam's download log"),
        new("config.ini", "WebHostBridge", Why.Elsewhere, "a Steam controller config file"),
        new("version.json", "WebHostBridge", Why.Elsewhere, "the Epic SDK's version file in a game"),
        new("ccs_history.json", "GameSettings", Why.Elsewhere, "Call of Duty HQ's own content history in the install: only its time is read, as a sign the game's content was updated"),
        new("installed.json", "EpicAdapter,NetworkDrivers,NileCli,NileJobEvidence,WebHostBridge", Why.Elsewhere, "Legendary's, Nile's and a driver package's installed list"),
        new("assets.json", "EpicAdapter", Why.Elsewhere, "Legendary's asset list"),
        new("user.json", "EpicAccounts,EpicLegendaryAchievementProvider,EpicPlaytime,NileCli", Why.Elsewhere, "Legendary's and Nile's signed-in user"),
        new("legendary/user.json", "EpicLegendaryAchievementProvider,EpicPlaytime", Why.Elsewhere, "Legendary's user file in its folder"),
        new("ExoLauncher/legendary/user.json", "EpicPlaytime", Why.Elsewhere, "Legendary's user file in Exo's copy of its folder"),
        new("installations.jsonl", "EpicLauncherV2,LibraryWatchers", Why.Elsewhere, "the Epic launcher's installations list"),
        new("cache/prod/catalog-cache.json", "EpicCatCacheArt,EpicLauncherV2", Why.Elsewhere, "the Epic launcher's catalog cache"),
        new("cache/prod/engines-cache.json", "EpicLauncherV2", Why.Elsewhere, "the Epic launcher's engines cache"),
        new("cache/prod/tools-cache.json", "EpicLauncherV2", Why.Elsewhere, "the Epic launcher's tools cache"),
        new("download/history.json", "EpicLauncherV2", Why.Elsewhere, "the Epic launcher's download history"),
        new("settings/app-settings.json", "EpicQuietSettings", Why.Elsewhere, "the Epic launcher's app settings"),
        new("current_user.json", "NileCli", Why.Elsewhere, "Nile's signed-in Amazon user"),
        new("library.json", "AmazonAdapter,GogAdapter,LocalOwnedSources,NileCli,NileJobEvidence", Why.Elsewhere, "the Amazon, GOG and other clients' library files"),
        new("gog/library.json", "GogAdapter", Why.Elsewhere, "the GOG library in Heroic's folder"),
        new("heroic/gog_store/library.json", "GogAdapter", Why.Elsewhere, "Heroic's GOG library"),
        new("heroic/store_cache/gog_library.json", "GogdlCli", Why.Elsewhere, "Heroic's GOG library cache"),
        new("galaxy-2.0.db", "GogGalaxySqlite", Why.Elsewhere, "GOG Galaxy's database"),
        new("ownership.json", "GogOwnedLibraryService,LocalOwnedSources", Why.Elsewhere, "a client's list of owned games"),
        new("butler.db", "ItchButlerDb", Why.Elsewhere, "the itch app's database"),
        new("itch/db/butler.db", "ItchButlerDb", Why.Elsewhere, "the itch app's database in its folder"),
        new("entitlements.json", "LocalOwnedSources", Why.Elsewhere, "a client's entitlements file"),
        new("game-data.json", "LocalOwnedSources", Why.Elsewhere, "a client's game data file"),
        new("launcher_accounts.json", "LocalOwnedSources", Why.Elsewhere, "a launcher's accounts file"),
        new("LocalState/library.json", "LocalOwnedSources", Why.Elsewhere, "a Store app's library in its LocalState"),
        new("LocalState/owned.json", "LocalOwnedSources", Why.Elsewhere, "a Store app's owned list in its LocalState"),
        new("title.json", "LocalOwnedSources", Why.Elsewhere, "a client's title file"),
        new("titles.json", "LocalOwnedSources", Why.Elsewhere, "a client's titles file"),
        new("fuel.json", "OfficialInstalledLibraries", Why.Elsewhere, "an Amazon game's fuel file"),
        new("install.dat", "OfficialInstalledLibraries", Why.Elsewhere, "a client's install record"),
        new("game_info.xml", "OfficialInstalledLibraries", Why.Elsewhere, "a game's info file from its client"),
        new("wgc_gameinfo.xml", "OfficialInstalledLibraries", Why.Elsewhere, "a Wargaming game's info file"),
        new("index.json", "MainWindow", Why.Elsewhere, "Exo Capture's index of clips"),
        new("settings.json", "MainWindow", Why.Elsewhere, "Exo Capture's own settings"),
        new("cache.db", "DriveSpace", Why.Elsewhere, "an app's cache database, measured for Clean up"),
        new("task.xml", "TuneSignIn", Why.Elsewhere, "the scheduled task AutoTune registers"),
        new(".product.db", "AntiCheatCheck", Why.Elsewhere, "names Exo looks for in a game's folder to recognise anti-cheat; Exo never writes them"),
        new("ACE-Base.dat", "AntiCheatCheck", Why.Elsewhere, "names Exo looks for in a game's folder to recognise anti-cheat; Exo never writes them"),
        new("equ8_conf.json", "AntiCheatCheck", Why.Elsewhere, "names Exo looks for in a game's folder to recognise anti-cheat; Exo never writes them"),
        // What an older Exo kept, which this one only removes.
        new("auth.bin", "LegacyProfileDataCleanup", Why.Retired, "the 3.4 account's session"),
        new("cider-api.bin", "LegacyProfileDataCleanup", Why.Retired, "the old Cider key"),
        new("config.json", "MouseBridgeRetirement", Why.Retired, "the old mouse bridge's config"),
        new("openmouse-bridge.json", "MouseBridgeRetirement", Why.Retired, "the old OpenMouse bridge's settings"),
        new("device-profiles.json", "LegacyProfileDataCleanup", Why.Retired, "old device profiles"),
        new("devices-last-good.json", "LegacyProfileDataCleanup", Why.Retired, "old devices' last good state"),
        new("discover-us-v1.json", "LegacyProfileDataCleanup", Why.Retired, "an old Discover page"),
        new("exo-profile-state.json", "LegacyProfileDataCleanup", Why.Retired, "an old profile state"),
        new("gg-deals.bin", "LegacyProfileDataCleanup", Why.Retired, "the old GG.deals key"),
        new("igdb-twitch-access.bin", "LegacyProfileDataCleanup", Why.Retired, "the old IGDB access token"),
        new("igdb-twitch-client-id.bin", "LegacyProfileDataCleanup", Why.Retired, "the old IGDB client id"),
        new("igdb-twitch-client-secret.bin", "LegacyProfileDataCleanup", Why.Retired, "the old IGDB client secret"),
        new("igdb-twitch-refresh.bin", "LegacyProfileDataCleanup", Why.Retired, "the old IGDB refresh token"),
        new("igdb-twitch-until.bin", "LegacyProfileDataCleanup", Why.Retired, "the old IGDB token's expiry"),
        new("itad.bin", "LegacyProfileDataCleanup", Why.Retired, "the old IsThereAnyDeal key"),
        new("itch-api.bin", "LegacyProfileDataCleanup", Why.Retired, "the old itch.io key"),
        new("items-v1.json", "StoreService", Why.Retired, "the first Store facts file"),
        new("items-v2.json", "StoreService", Why.Retired, "the second Store facts file"),
        new("mouse-profiles.json", "LegacyProfileDataCleanup", Why.Retired, "the old mouse profiles file"),
        new("openmouse-games.json", "LegacyProfileDataCleanup", Why.Retired, "the old OpenMouse game links"),
        new("openxbl.bin", "LegacyProfileDataCleanup", Why.Retired, "the old OpenXBL key"),
        new("spotify-client-id.bin", "LegacyProfileDataCleanup", Why.Retired, "the old Spotify client id"),
        new("spotify-queries.json", "LegacyProfileDataCleanup", Why.Retired, "old Spotify searches"),
        new("spotify-tokens.bin", "LegacyProfileDataCleanup", Why.Retired, "the old Spotify tokens"),
        new("status-v1.json", "DlssSwapService", Why.Retired, "the first upscaler status file"),
        new("steam-web-api.bin", "LegacyProfileDataCleanup", Why.Retired, "the old Steam Web API key"),
        new("steamgriddb.bin", "LegacyProfileDataCleanup", Why.Retired, "the old SteamGridDB key"),
        new("store-metadata.json", "LegacyProfileDataCleanup", Why.Retired, "old store metadata"),
        new("tracker-id-destiny2.txt", "LegacyProfileDataCleanup", Why.Retired, "an old Destiny 2 tracker id"),
        new("tracker-id-r6siege.txt", "LegacyProfileDataCleanup", Why.Retired, "an old Rainbow Six tracker id"),
        new("xbox-signin.bin", "LegacyProfileDataCleanup", Why.Retired, "the old Xbox sign-in"),
        // Files in what Exo installs or downloads.
        new("DriverAdvisories.json", "DriverAdvisories", Why.Shipped, "the advisories Exo ships"),
        new("files.json", "AppFiles,AppUpdateService", Why.Shipped, "a release's file list, read from the update address for a file-level update"),
        new("payload-manifest.json", "AppFiles", Why.Shipped, "the installer's list of the app folder's files"),
        new("LICENSE.txt", "WebExtensionManifest", Why.Shipped, "an extension's licence"),
        new("SOURCE.txt", "WebExtensionManifest", Why.Shipped, "an extension's source note"),
        new("manifest.json", "WebExtensions", Why.Shipped, "an extension's manifest"),
        new("RyzenSMU.bin", "TuneSignIn", Why.Shipped, "the Ryzen SMU driver AutoTune uses"),
        new("Assets/pawnio/RyzenSMU.bin", "AutoTuneWorker,TuneSignIn", Why.Shipped, "the Ryzen SMU driver in Exo's assets"),
        new("StartupCatalog.json", "StartupCatalog", Why.Shipped, "the start-up catalog Exo ships"),
        new("Assets/device-images.json", "DeviceImages", Why.Shipped, "the device picture links Exo ships"),
        new("Assets/Overlay/sample-icon.png", "AchievementIconCache", Why.Shipped, "the sample achievement icon Exo ships"),
        new("helixsr_weights.bin", "DlssSwapService", Why.Shipped, "HelixSR's model weights, downloaded"),
        new("dlssnr_on_amd_weights.bin", "DlssSwapService", Why.Shipped, "the DLSS denoiser on AMD mod's weights, downloaded"),    ];

    /// <summary>Every (path, type) the two lists claim.</summary>
    internal static IEnumerable<(string Path, string Place)> AllClaims() =>
        Files.SelectMany(file => file.Claims).Concat(NotPlayerData.SelectMany(entry => entry.Claims));

    /// <summary>How many entries claim a file as the code at that type names it (one is right).</summary>
    internal static int Claims(string path, string place) => AllClaims().Count(claim => claim.Path == path && claim.Place == place);
}
