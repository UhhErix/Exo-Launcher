namespace ExoLauncher.Models;

public enum StoreKind
{
    Local,
    Steam,
    Epic,
    Gog,
    Riot,
    Xbox,
    Ea,
    Ubisoft,
    BattleNet,
    Amazon,
    Rockstar,
    Itch,
    Minecraft,
    Roblox,
    Paradox,
    Wargaming
}

/// <summary>
/// Current-account entitlement truth. Unknown preserves legacy adapters that
/// have not established this boundary; Unverified and NotOwned are explicit
/// states and must never fall through to Play/Update/Install.
/// </summary>
public enum EntitlementState
{
    Unknown,
    Owned,
    Unverified,
    NotOwned,
}

/// <summary>A game a card holds besides or under its own name: its Steam app and Steam's name for it (SteamGameHubs).</summary>
public sealed record IncludedGame(string AppId, string Title);

/// <summary>Canonical game model shared by adapters and the UI.</summary>
public sealed class GameEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required StoreKind Store { get; init; }
    public bool Installed { get; init; }
    public bool Owned { get; init; } = true;
    /// <summary>Steam Family / borrowed license. Playable through Steam, not in GetOwnedGames.</summary>
    public bool FamilyShared { get; init; }
    /// <summary>Who shares a <see cref="FamilyShared"/> game ("Sam", "Sam and Alex"), when Steam's family names them.</summary>
    public string? SharedBy { get; init; }
    public EntitlementState EntitlementState { get; init; } = EntitlementState.Unknown;
    public bool UpdateAvailable { get; init; }
    public bool CanInstall { get; init; }
    public string? Path { get; init; }
    public string? CoverUrl { get; init; }
    /// <summary>Verified cover provenance: steam | epic | gog | riot | local.</summary>
    public string? CoverSource { get; init; }
    /// <summary>
    /// Process-local cache generation for this visual card. It is not synced or
    /// persisted; the WebView uses it only to stop reusing stale decoded art.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long ArtRevision { get; init; }
    /// <summary>Total playtime in minutes when known.</summary>
    public int? PlaytimeMinutes { get; init; }
    /// <summary>Install size in bytes when known.</summary>
    public long? SizeBytes { get; init; }
    public string Status { get; init; } = "Ready";
    public IReadOnlyList<string> Deps { get; init; } = Array.Empty<string>();
    /// <summary>Honest one-line note about what launch/install actually does for this store.</summary>
    public string LaunchNote { get; init; } = string.Empty;
    /// <summary>Store-specific launch target (app id, product id, exe path).</summary>
    public string? LaunchTarget { get; init; }
    /// <summary>
    /// Steam parent app id when this row is DLC, a soundtrack, or another
    /// child. Empty means the row is its own parent for library dedup.
    /// </summary>
    public string? ParentAppId { get; init; }
    /// <summary>The games one Steam app holds (SteamGameHubs), newest first: the first names the card and lends it its art.</summary>
    public IReadOnlyList<IncludedGame>? Includes { get; init; }
    /// <summary>Steam appinfo <c>common/type</c> when known. Empty on ACF-only rows.</summary>
    public string? SteamAppType { get; init; }
    /// <summary>UTC last played when known (user prefs or store metadata).</summary>
    public DateTimeOffset? LastPlayedUtc { get; init; }
    /// <summary>
    /// Stable, display-only key used to group exact title matches from different
    /// stores. It is never an account identifier and must not be used for a
    /// launch, install, achievement, or playtime lookup.
    /// </summary>
    public string? CanonicalTitleKey { get; init; }
    /// <summary>
    /// The exact store entry currently projected into this backwards-compatible
    /// top-level row. It always equals <see cref="Id"/> for grouped cards.
    /// </summary>
    public string? SelectedVariantId { get; init; }
    /// <summary>
    /// Other exact store entries represented by this visual card. These entries
    /// are presentation metadata only; callers must resolve their id through
    /// the library before performing a store action.
    /// </summary>
    public IReadOnlyList<GameVariant> Variants { get; init; } = Array.Empty<GameVariant>();

    /// <summary>
    /// Copy of every property with new playtime readings. Hand-written clones
    /// dropped fields (ParentAppId broke store-title dedup); add new properties here.
    /// </summary>
    internal GameEntry WithPlaytime(int? playtimeMinutes, DateTimeOffset? lastPlayedUtc) => new()
    {
        Id = Id,
        Title = Title,
        Store = Store,
        Installed = Installed,
        Owned = Owned,
        FamilyShared = FamilyShared,
        SharedBy = SharedBy,
        EntitlementState = EntitlementState,
        UpdateAvailable = UpdateAvailable,
        CanInstall = CanInstall,
        Path = Path,
        CoverUrl = CoverUrl,
        CoverSource = CoverSource,
        ArtRevision = ArtRevision,
        PlaytimeMinutes = playtimeMinutes,
        SizeBytes = SizeBytes,
        Status = Status,
        Deps = Deps,
        LaunchNote = LaunchNote,
        LaunchTarget = LaunchTarget,
        ParentAppId = ParentAppId,
        Includes = Includes,
        SteamAppType = SteamAppType,
        LastPlayedUtc = lastPlayedUtc,
        CanonicalTitleKey = CanonicalTitleKey,
        SelectedVariantId = SelectedVariantId,
        Variants = Variants,
    };

    /// <summary>Primary action the UI should offer: play | install | update | none.</summary>
    public string PrimaryAction
    {
        get
        {
            if (string.Equals(Id, "local:add", StringComparison.OrdinalIgnoreCase))
                return "install";
            if (EntitlementState == EntitlementState.NotOwned)
                return "none";
            if (EntitlementState == EntitlementState.Unverified && !Installed)
                return "none";
            if (Installed && UpdateAvailable) return "update";
            if (Installed) return "play";
            if (CanInstall || Owned) return "install";
            return "none";
        }
    }
}

/// <summary>
/// A store-specific source behind a canonical library card. Every id, launch
/// target, install path, and state remains source-specific so cross-store cards
/// never turn into a synthetic game that an adapter cannot safely act on.
/// </summary>
public sealed record GameVariant
{
    public required string Id { get; init; }
    public required StoreKind Store { get; init; }
    public bool Installed { get; init; }
    public bool Owned { get; init; }
    public bool FamilyShared { get; init; }
    public string? SharedBy { get; init; }
    public EntitlementState EntitlementState { get; init; } = EntitlementState.Unknown;
    public bool UpdateAvailable { get; init; }
    public bool CanInstall { get; init; }
    public string? Path { get; init; }
    public string? LaunchTarget { get; init; }
    public int? PlaytimeMinutes { get; init; }
    public DateTimeOffset? LastPlayedUtc { get; init; }
    public string Status { get; init; } = "Ready";
    public string PrimaryAction =>
        EntitlementState == EntitlementState.NotOwned ? "none" :
        EntitlementState == EntitlementState.Unverified && !Installed ? "none" :
        Installed && UpdateAvailable ? "update" :
        Installed ? "play" :
        CanInstall || Owned ? "install" : "none";

    internal static GameVariant FromGame(GameEntry game) => new()
    {
        Id = game.Id,
        Store = game.Store,
        Installed = game.Installed,
        Owned = game.Owned,
        FamilyShared = game.FamilyShared,
        SharedBy = game.SharedBy,
        EntitlementState = game.EntitlementState,
        UpdateAvailable = game.UpdateAvailable,
        CanInstall = game.CanInstall,
        Path = game.Path,
        LaunchTarget = game.LaunchTarget,
        PlaytimeMinutes = game.PlaytimeMinutes,
        LastPlayedUtc = game.LastPlayedUtc,
        Status = game.Status,
    };

    internal GameEntry ToGameEntry(GameEntry card) => new()
    {
        Id = Id,
        Title = card.Title,
        Store = Store,
        Installed = Installed,
        Owned = Owned,
        FamilyShared = FamilyShared,
        SharedBy = SharedBy,
        EntitlementState = EntitlementState,
        UpdateAvailable = UpdateAvailable,
        CanInstall = CanInstall,
        Path = Path,
        CoverUrl = card.CoverUrl,
        CoverSource = card.CoverSource,
        ArtRevision = card.ArtRevision,
        PlaytimeMinutes = PlaytimeMinutes,
        SizeBytes = card.SizeBytes,
        Status = Status,
        Deps = card.Deps,
        LaunchNote = card.LaunchNote,
        LaunchTarget = LaunchTarget,
        Includes = card.Includes,
        LastPlayedUtc = LastPlayedUtc,
        CanonicalTitleKey = card.CanonicalTitleKey,
        SelectedVariantId = Id,
    };
}

public sealed class DependencyInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
    public string Detail { get; init; } = string.Empty;
    public bool CanOfferInstall { get; init; }
    public string? OfficialUrl { get; init; }
}

public sealed class LaunchResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? ProcessId { get; init; }
    public string? BackendStarted { get; init; }
    /// <summary>When true, agent opened but title launch is not fully wired (phase-2 honesty).</summary>
    public bool HandoffOnly { get; init; }
    /// <summary>Missing runtimes — UI should prompt Install (consent) before retrying with skipDeps.</summary>
    public bool NeedsDependencies { get; init; }
    public IReadOnlyList<DependencyInfo> MissingDependencies { get; init; } = Array.Empty<DependencyInfo>();
    /// <summary>The store's servers are down and it plays this game in its own offline mode after a restart Exo asks about ("steam").</summary>
    public string? OfflineStore { get; init; }
}

public sealed record InstallResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
    public bool HandoffOnly { get; init; }
    /// <summary>A folder this install created and left unfinished: removed on Cancel, kept on Pause so it can resume.</summary>
    public string? PartialPath { get; init; }
    public bool Queued { get; init; }
    public bool NeedsDependencies { get; init; }
    public IReadOnlyList<DependencyInfo> MissingDependencies { get; init; } = Array.Empty<DependencyInfo>();
}

public enum InstallPhase
{
    Idle,
    Preparing,
    Downloading,
    Installing,
    Finalizing,
    Completed,
    Failed,
    Cancelled
}

public sealed class InstallProgress
{
    private static long _sequence;
    [System.Text.Json.Serialization.JsonIgnore]
    internal long Sequence { get; init; } = Interlocked.Increment(ref _sequence);
    public string GameId { get; init; } = string.Empty;
    public InstallPhase Phase { get; init; } = InstallPhase.Idle;
    public double? Percent { get; init; }
    /// <summary>Downloaded bytes for the current download phase. Null means unknown; zero can be a valid reading.</summary>
    public long? BytesDownloaded { get; init; }
    /// <summary>Total bytes this job, when the store reports them.</summary>
    public long? BytesToDownload { get; init; }
    /// <summary>Bytes per second when known.</summary>
    public double? BytesPerSecond { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool CanCancel { get; init; } = true;
    public bool IsActive => Phase is InstallPhase.Preparing or InstallPhase.Downloading
        or InstallPhase.Installing or InstallPhase.Finalizing;
}

/// <summary>A folder of games on the Library's grid, made by the player; a game may be in several.</summary>
public sealed class LibraryFolder
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Its color on the grid: gray, red, orange, yellow, green, teal, blue or pink.</summary>
    public string Color { get; set; } = "gray";
    /// <summary>How its games are ordered: "folder" (the folder's own order) or one of the Library's sorts.</summary>
    public string Sort { get; set; } = "folder";
    /// <summary>Its picture (<see cref="ExoLauncher.Services.FolderArtStore"/>), shown in place of its games' covers; null for the covers.</summary>
    public string? Art { get; set; }
    /// <summary>Its games' ids, in the folder's order.</summary>
    public List<string> Games { get; set; } = [];
}

public sealed class AppSettings
{
    /// <summary>Always on. Not in Settings.</summary>
    public bool CloseStoreClientsAfterLaunch { get; set; } = true;
    /// <summary>Always on. Prompt for missing redistributables on launch or install.</summary>
    public bool AutoInstallRedistributables { get; set; } = true;
    /// <summary>Always on. Not in Settings.</summary>
    public bool MinimizeWhilePlaying { get; set; } = true;
    /// <summary>Always true. Not a user toggle.</summary>
    public bool AntiCheatSafeMode { get; set; } = true;
    /// <summary>When true, Exo keeps motion short. When false, Exo plays its animations even if Windows reduce-animations is on.</summary>
    public bool ReduceMotion { get; set; }
    /// <summary>When true, Exo's window always opens maximized. When false, it opens as it was when it closed.</summary>
    public bool StartMaximized { get; set; }
    /// <summary>Always on. Chrome hides until an edge hover; pin insets the page.</summary>
    public bool FramelessMode { get; set; } = true;
    public bool FramelessPinTop { get; set; }
    public bool FramelessPinSide { get; set; }
    /// <summary>Shell text color: "white" or "grey".</summary>
    public string TextColour { get; set; } = "white";
    /// <summary>Shell text size: "small", "normal" or "large".</summary>
    public string TextSize { get; set; } = "normal";
    /// <summary>The page's background palette (Settings › General › Background): "black", "near", "grey" (the default), "windows" or "graphite".</summary>
    public string Background { get; set; } = "black";
    /// <summary>
    /// The choices the page makes and Exo keeps for it (Discord's look and streamer mode, the driver branch, the headphones
    /// worn, the Shaders order, Go Live's quality, ...), by key. Only the keys in <see cref="ExoLauncher.Services.PlayerData.UiPrefKeys"/>
    /// are kept; each syncs on its own (AccountSyncMap).
    /// </summary>
    public Dictionary<string, System.Text.Json.JsonElement> UiPrefs { get; set; } = new(StringComparer.Ordinal);
    /// <summary>
    /// UiPrefs keys moved in from the page's old storage on the update that moved them (this PC only). They stay out of the
    /// synced settings until the account has been read in full: the account's value wins over a moved one, and a moved one
    /// goes up only where the account has none (SettingsService.SettleMovedUiPrefs).
    /// </summary>
    public List<string> UiPrefsMoved { get; set; } = [];
    public string AppVersion { get; set; } = "1.0.0";
    /// <summary>The last version whose What's New sheet was shown (ui/src/lib/whatsNew.ts). A new settings file starts at the
    /// running version, so a new install sees nothing; a file from before the sheet has none, so it shows once.</summary>
    public string? WhatsNewSeen { get; set; }
    /// <summary>Optional override; null = auto <c>%LOCALAPPDATA%\ExoLauncher\Games</c>.</summary>
    public string? DefaultInstallRoot { get; set; }
    /// <summary>Legacy field; Local portable path removed — always false.</summary>
    public bool CopyPortableIntoLibrary { get; set; } = false;
    /// <summary>Unused. The window is always resizable.</summary>
    public bool AllowResize { get; set; } = false;
    /// <summary>Always true — in-app update checks on start.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Library sort: name | installed | recent | played | size | store.</summary>
    public string SortMode { get; set; } = "installed";
    /// <summary>Preferred exact store source for each canonical multi-store game. Local to this PC.</summary>
    public Dictionary<string, string> PreferredGameSources { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Recently launched game ids (newest first, capped).</summary>
    public List<string> Recent { get; set; } = new();
    /// <summary>Last played timestamps by game id (ISO).</summary>
    public Dictionary<string, string> LastPlayed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>True after the first-run launcher setup flow.</summary>
    public bool OnboardingComplete { get; set; }
    /// <summary>The account step has been shown on this PC (an upgrader is asked once).</summary>
    public bool AccountAsked { get; set; }
    /// <summary>Per-game launch extras (args / cwd / admin). Never applied to anti-cheat titles.</summary>
    public Dictionary<string, GameLaunchOverride> LaunchOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Exo-owned custom cover file names keyed by exact library source id.
    /// Device-local by design; account sync never reads this dictionary.
    /// </summary>
    public Dictionary<string, string> CustomCoverImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Legacy field retained for settings-file compatibility. Not read or exposed.</summary>
    public bool LibraryShowInstalled { get; set; } = true;
    /// <summary>Legacy field retained for settings-file compatibility. Not read or exposed.</summary>
    public bool LibraryShowOwned { get; set; } = true;
    /// <summary>Show demos, soundtracks, and tools on the library shelf.</summary>
    public bool ShowLibraryTools { get; set; }
    /// <summary>Exo's achievement banners over the game when one unlocks (<see cref="ExoLauncher.AchievementBanner"/>).</summary>
    public bool AchievementBanners { get; set; } = true;
    /// <summary>How loud a banner's sound plays, 0 (silent) to 100.</summary>
    public int AchievementVolume { get; set; } = 60;
    /// <summary>The FPS counter over a game (<see cref="ExoLauncher.Services.FpsCounter"/>). Off until turned on: it needs a group only Windows' prompt can add.</summary>
    public bool FpsCounter { get; set; }
    /// <summary>Where it shows: left, middle or right along the top of the game's screen.</summary>
    public string FpsCounterPosition { get; set; } = "right";
    /// <summary>What it shows after the FPS (<see cref="ExoLauncher.Services.FpsItems"/>); nothing until switched on.</summary>
    public List<string> FpsCounterItems { get; set; } = [];
    /// <summary>Opt-in suppression when a known game can show its own built-in FPS counter and no extra items are chosen. Off by default.</summary>
    public bool FpsCounterHideOverBuiltIn { get; set; }
    /// <summary>Autopilot (<see cref="ExoLauncher.Services.Autopilot"/>): one switch, off until the player turns it on, and its tucked-away choices.</summary>
    public AutopilotSettings Autopilot { get; set; } = new();
    /// <summary>Exo AI coordinator preferences (<see cref="ExoLauncher.Services.ExoAiCoordinator"/>): background safe optimization and GPU fan curves.</summary>
    public ExoAiSettings ExoAi { get; set; } = new();
    /// <summary>HDR on for the screen an HDR game is on while it runs, back off after (System > Graphics > HDR). On by default:
    /// it only acts on games whose verdict says they output HDR (<see cref="ExoLauncher.Services.GameHdr"/>). A settings file
    /// that never recorded it gets the default; one that did keeps its choice.</summary>
    public bool HdrForGames { get; set; } = true;
    /// <summary>SDR content drawn with gamma 2.2 while a screen is in HDR (System > Graphics > HDR; <see cref="ExoLauncher.Helpers.SdrInHdr"/>).</summary>
    public bool HdrSdrGamma { get; set; } = true;
    /// <summary>DeepFilterNet 3 on the microphone in Exo's own Discord calls (<see cref="ExoLauncher.Services.DiscordClient.Voice.NoiseSuppressor"/>).</summary>
    public bool DiscordNoiseSuppression { get; set; } = true;
    /// <summary>Which noise suppression runs when it is on: "exo" (Exo Voice, the default: the tuned Exo AI chain), "standard" (Google's WebRTC alone: <see cref="ExoLauncher.Services.DiscordClient.Voice.WebRtcNoise"/>), or an earlier build's "ai" or "krisp" (all Exo Voice now).</summary>
    public string DiscordNoiseSuppressionKind { get; set; } = "exo";
    /// <summary>The same on everyone else's voice in a call, each their own (<see cref="ExoLauncher.Services.DiscordClient.Voice.InboundCleaner"/>); off by default.</summary>
    public bool DiscordOthersNoiseSuppression { get; set; }
    /// <summary>Only My Voice: voices that aren't the user's learned one are held back in calls (<see cref="ExoLauncher.Services.DiscordClient.Voice.MyVoice"/>); off by default.</summary>
    public bool DiscordOnlyMyVoice { get; set; }
    /// <summary>Each person's voice in a call brought toward one speaking level, for you (<see cref="ExoLauncher.Services.DiscordClient.Voice.VoiceLeveling"/>); on by default.</summary>
    public bool DiscordEvenVoices { get; set; } = true;
    /// <summary>Game focus: no Discord notifications while a game runs (the unread badge still counts).</summary>
    public bool DiscordQuietWhilePlaying { get; set; } = true;
    /// <summary>Exo's Discord calls: the microphone and speakers chosen (Windows endpoint ids; none is Windows' default) and their volume, 0-200 like Discord's.</summary>
    public string? DiscordInputDevice { get; set; }
    public string? DiscordOutputDevice { get; set; }
    public int DiscordInputVolume { get; set; } = 100;
    public int DiscordOutputVolume { get; set; } = 100;
    /// <summary>Others' soundboard sounds in a call: how loud (0-100%, as Discord's Soundboard Volume) and whether they play at all.</summary>
    public int DiscordSoundboardVolume { get; set; } = 100;
    public bool DiscordSoundboardMuted { get; set; }
    /// <summary>Discord settings > Voice & Video > Input Profile: "exo" (Exo Voice) or "custom"; none, or an
    /// earlier profile, is Exo Voice. In Custom, <see cref="DiscordNoiseSuppression"/> is the Noise Suppression choice.</summary>
    public string? DiscordInputProfile { get; set; }
    /// <summary>Push to Talk instead of Voice Activity, and how long the mic stays open after the key is let go (ms, Discord's 0-2000).</summary>
    public bool DiscordPushToTalk { get; set; }
    public int DiscordPttDelay { get; set; } = 20;
    /// <summary>Input Sensitivity: automatic, or a fixed threshold in dB (-100 to 0, Discord's default -60).</summary>
    public bool DiscordAutoSensitivity { get; set; } = true;
    public int DiscordSensitivity { get; set; } = -60;
    /// <summary>Echo Cancellation (Exo's, on speakers that aren't on your head: <see cref="ExoLauncher.Services.DiscordClient.Voice.EchoCanceller"/>) and Automatic Gain Control.</summary>
    public bool DiscordEchoCancellation { get; set; } = true;
    /// <summary>Voice Focus in the Custom profile (Voice Isolation always has it): Exo's gate, leveler and limiter after noise suppression.</summary>
    public bool DiscordVoiceFocus { get; set; } = true;
    /// <summary>Voice Tune's tuning kept per microphone (by its Windows endpoint id), in the one-line form of
    /// <see cref="ExoLauncher.Services.DiscordClient.Voice.VoiceTune.Format"/>.</summary>
    public Dictionary<string, string> DiscordVoiceTunes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Global Attenuation: other apps' volume lowered by this percent while someone speaks (0 is off).</summary>
    public int DiscordAttenuation { get; set; }
    public bool DiscordAttenuateSelf { get; set; }
    public bool DiscordAttenuateOthers { get; set; } = true;
    /// <summary>Discord settings > Keybinds: actions on keys held anywhere in Windows during a call.</summary>
    public List<DiscordKeybind> DiscordKeybinds { get; set; } = [];
    /// <summary>Discord settings > Notifications: Windows notifications, the taskbar's unread badge, and flashing the taskbar button.</summary>
    public bool DiscordDesktopNotifications { get; set; } = true;
    public bool DiscordUnreadBadge { get; set; } = true;
    public bool DiscordTaskbarFlash { get; set; } = true;
    /// <summary>Discord's sounds turned off one by one (Discord's names: message1, mute, user_join, ...), all of them at once,
    /// and New Message in the channel I'm currently reading (off unless chosen, as in Discord).</summary>
    public List<string> DiscordDisabledSounds { get; set; } = [];
    public bool DiscordDisableAllSounds { get; set; }
    public bool DiscordSelectedChannelSound { get; set; }
    /// <summary>How loud all of Discord's sounds play, in percent (60 until chosen, the level they always had).</summary>
    public int DiscordSoundVolume { get; set; } = 60;
    /// <summary>Discord Go Live defaults: resolution height (0 for Source), frame rate, and hardware HEVC codec support.</summary>
    public int DiscordGoLiveHeight { get; set; } = 1080;
    public int DiscordGoLiveFps { get; set; } = 30;
    public bool DiscordGoLiveHevc { get; set; } = true;
    /// <summary>Per game: "on" or "off" overrides what PCGamingWiki says; no entry is Auto.</summary>
    public Dictionary<string, string> HdrGameModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>System > Display > Use NVIDIA color settings, by screen device path (format in <see cref="ExoLauncher.Helpers.NvidiaColor"/>). No entry: other apps control color.</summary>
    public Dictionary<string, string> NvidiaColor { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// System > Display > RTX Video as last chosen in Exo: Super resolution 0 off, 1-4 quality, 5 Auto (null: Exo's built-in
    /// Auto on an RTX card), and HDR (null: as the driver has it). Put back after a driver install resets it
    /// (<see cref="ExoLauncher.Services.DriverRestore"/>), once per driver: <see cref="RtxVideoDriver"/> is the driver it last held for.
    /// </summary>
    public int? RtxVideoSuperResolution { get; set; }
    public bool? RtxVideoHdr { get; set; }
    public string? RtxVideoDriver { get; set; }
    /// <summary>NVIDIA global profile G-SYNC choice (null = follow main screen's VRR capability).</summary>
    public bool? NvidiaGsync { get; set; }
    /// <summary>NVIDIA global profile low-latency mode ("off", "on", "ultra"; null = "on").</summary>
    public string? NvidiaLowLatency { get; set; }
    /// <summary>System > Graphics > HDR > SDR looks right in HDR: by screen device path, the advanced-color profile the
    /// screen had before Exo's SDR gamma 2.2 one ("" for none), put back when HDR goes off.</summary>
    public Dictionary<string, string> SdrGammaBefore { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>System > Graphics > HDR > HDR for HDR games: the device path of the screen Exo switched HDR on for ("" for
    /// none), so HDR still goes back off when Exo closed without doing it (a crash, an update, the PC shut down).</summary>
    public string HdrSwitchedOn { get; set; } = "";
    /// <summary>The gamma ramp the screen had before Exo switched HDR on (base64, <see cref="ExoLauncher.Services.HdrPlan.RampText"/>;
    /// "" for none), put back with HDR, so a crash can't leave the calibration lost.</summary>
    public string HdrSwitchedRamp { get; set; } = "";
    /// <summary>Per game: the device path of the screen its window was last on while HDR ran, so the next start switches that screen
    /// before the window opens.</summary>
    public Dictionary<string, string> HdrGameScreens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Downloads Exo runs itself (Epic, GOG, Amazon) pause while a game runs and resume after it.</summary>
    public bool PauseDownloadsWhilePlaying { get; set; } = true;
    /// <summary>The PC stays awake while Exo downloads or installs a game or update, or warms shaders (<see cref="ExoLauncher.Helpers.KeepAwake"/>).</summary>
    public bool KeepAwakeDownloads { get; set; } = true;
    /// <summary>Download hours: downloads start only from this hour (0-23) until <see cref="DownloadHoursEnd"/>; null = any time.</summary>
    public int? DownloadHoursStart { get; set; }
    public int? DownloadHoursEnd { get; set; }
    /// <summary>
    /// Store clients (Steam, Epic) stay quiet: their notifications, sounds and pop-ups off, kept off, and put back exactly as they
    /// were when this is switched off (<see cref="ExoLauncher.Adapters.StoreQuietPass"/>, <see cref="ExoLauncher.Adapters.EpicQuietSettings"/>).
    /// </summary>
    public bool StoreClientsQuiet { get; set; } = true;
    /// <summary>Games from Epic, GOG and Amazon update by themselves while the PC is idle.</summary>
    public bool AutoUpdateGames { get; set; } = true;
    /// <summary>Compress games while idle: the games worth it on this PC are compressed by Windows (<see cref="ExoLauncher.Services.GameCompressor"/>) while nobody uses it. Off until the player turns it on.</summary>
    public bool CompressWhileIdle { get; set; }
    /// <summary>A compressed game's files an update wrote back are compressed again while the PC is idle. Off until the player turns it on.</summary>
    public bool RecompressAfterUpdates { get; set; }
    /// <summary>This PC's chipset driver (AMD's on a Ryzen, Intel's on an Intel processor) updates by itself while the PC is idle (<see cref="ExoLauncher.Services.ChipsetUpdate"/>).</summary>
    public bool AutoUpdateChipset { get; set; } = true;
    /// <summary>Newer DLSS, FSR and XeSS files download by themselves (from the vendor's own release, Exo's cache when that fails) while the PC is idle. On by default.</summary>
    public bool AutoUpdateUpscalers { get; set; } = true;
    /// <summary>
    /// Settings' one-time allow: the vendors' signed DLSS, FSR and XeSS files update in games with anti-cheat too, at the player's
    /// risk, without asking per game. Never Riot games, Vanguard or an install Exo can't read; never a frame generation mod.
    /// </summary>
    public bool UpscalerAntiCheatEverywhere { get; set; }
    /// <summary>Set once Exo has turned <see cref="AutoUpdateUpscalers"/> on for a settings file written while it was off by default (3.26 and earlier).</summary>
    public bool UpscalerAutoUpdateDefaulted { get; set; } = true;
    /// <summary>On a PC with two graphics chips, Windows is asked to run Exo's games on the fast one (<see cref="ExoLauncher.Services.GpuPreference"/>); off until the user turns it on.</summary>
    public bool FastGpuForGames { get; set; }
    /// <summary>
    /// An earlier Exo hosted Lucid with itself (3.25 and before); read once by the move to Exo EQ
    /// (<see cref="ExoLauncher.Services.LucidRetire"/>), which then turns it off for good.
    /// </summary>
    public bool StartLucidWithExo { get; set; }
    /// <summary>Apps whose start with Windows Exo turned off (<see cref="ExoLauncher.Helpers.StartupApps"/>); one turned back on stays on.</summary>
    public List<string> StartupHandedToExo { get; set; } = [];
    /// <summary>Exo opens when Windows starts (the current user's Run key, <see cref="ExoLauncher.Services.StartWithWindows"/>); off until the player turns it on.</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>
    /// Retired with Exo's own OpenMouse bridge, read once by
    /// <see cref="ExoLauncher.Services.MouseBridgeRetirement"/> and then
    /// cleared: "exo" means Use Exo's bridge had stopped the OpenMouse Bridge app.
    /// </summary>
    public string? MouseBridge { get; set; }
    /// <summary>Retired: the OpenMouse Bridge app's Windows sign-in entry as Exo removed it.</summary>
    public string? MouseBridgeAppAutostart { get; set; }
    /// <summary>Retired: the OpenMouse Bridge app Exo stopped.</summary>
    public string? MouseBridgeAppPath { get; set; }
    /// <summary>
    /// Game sheet > Upscalers: games with anti-cheat whose upscaler files the
    /// user allowed Exo to change at their own risk, keyed by exact library id.
    /// The value is the anti-cheat named in the confirmation; the consent lapses
    /// when a different one is detected. Only <c>dlss.consent</c> writes it, and
    /// it refuses Riot games and Riot Vanguard.
    /// </summary>
    public Dictionary<string, string> UpscalerAntiCheatConsent { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The Library's folders, in their order on the grid (<see cref="LibraryFolder"/>).</summary>
    public List<LibraryFolder> LibraryFolders { get; set; } = [];
    /// <summary>Games hidden from the Library (card ids): out of its grid, Continue and folders; Library's Hidden view lists them.</summary>
    public List<string> HiddenGames { get; set; } = [];
    /// <summary>
    /// Best settings › Frame generation in games without anti-cheat: Exo puts its frame generation mod in every such
    /// game it works in, by itself (<see cref="ExoLauncher.Services.DlssSwapService.FrameGenEverywhereAsync"/>).
    /// </summary>
    public bool FrameGenEverywhere { get; set; }
    /// <summary>Games the player turned Exo's frame generation off in (card ids), which <see cref="FrameGenEverywhere"/> leaves alone.</summary>
    public List<string> FrameGenKeptOff { get; set; } = [];
    /// <summary>
    /// Steam games (card ids) set to "Launch without anti-cheat" in Properties: Play starts Steam's own entry without it
    /// (NBA 2K26's "without EAC (offline only)"), and the game then counts as one without anti-cheat
    /// (<see cref="ExoLauncher.Services.AntiCheatCheck.WithoutAntiCheatLaunch"/>).
    /// </summary>
    public List<string> SteamWithoutAntiCheat { get; set; } = [];
    /// <summary>Games the player keeps out of Update games automatically (game ids): they update only when the player presses Update.</summary>
    public List<string> AutoUpdateOff { get; set; } = [];
    /// <summary>
    /// Each installed Steam game's overlay switch as Exo set it, by app id: "on" or "off", with "?" until
    /// Steam has it and "!" once the player changed it in Steam (<see cref="ExoLauncher.Adapters.SteamOverlayConfig.Plan"/>).
    /// </summary>
    public Dictionary<string, string> SteamOverlay { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Pre-Exo <c>OverlayAppEnable</c> value per Steam account and app (<c>"&lt;accountId&gt;/&lt;appId&gt;"</c> -&gt; <c>"1"</c>,
    /// <c>"0"</c>, or <c>""</c> when the key was absent before Exo touched <c>localconfig.vdf</c>), so Exo can cleanly reverse its edits.
    /// </summary>
    public Dictionary<string, string> SteamOverlayPrior { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-game opt-in for NVIDIA driver Smooth Motion (<c>0xB0D384C0</c>) on RTX 40/50 when not using native DLSS-G.</summary>
    public Dictionary<string, bool> SmoothMotion { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Devices > controller > Sound in Windows: controllers' own speakers and microphones as Windows audio devices.
    /// Off by default: Exo turns them off whenever a controller connects, so Windows never switches your sound to one.
    /// </summary>
    public bool ControllerSound { get; set; }
    /// <summary>
    /// Steam Input values Exo wrote, by app id: "prior>set", so letting go puts the prior value back exactly; "!" once
    /// the player changed it in Steam (<see cref="ExoLauncher.Adapters.SteamInputConfig.Plan"/>).
    /// </summary>
    public Dictionary<string, string> SteamInput { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Games that aren't Steam's, sent through Steam Input from their Properties (they read Xbox controllers only), by
    /// Exo game id: each starts as a Steam shortcut (<see cref="ExoLauncher.Adapters.SteamShortcuts"/>).
    /// </summary>
    public List<string> ThroughSteam { get; set; } = [];
    /// <summary>The shortcuts Exo added for them, by game id: "&lt;Steam's app id&gt;:&lt;stamp&gt;", with "!" after it once the player changed its Steam Input in Steam.</summary>
    public Dictionary<string, string> SteamShortcuts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A PlayStation controller has been on this PC while Exo ran: Properties offers Steam Input for games that aren't Steam's.</summary>
    public bool PlayStationPadSeen { get; set; }
    /// <summary>Exo has put Steam on its Beta Update (once; a player who leaves it in Steam stays off it).</summary>
    public bool SteamBetaJoined { get; set; }
    /// <summary>The NVIDIA driver branch the driver row last showed ("studio"; null: Game Ready), for the version watch.</summary>
    public string? NvidiaDriverBranch { get; set; }
    /// <summary>One-time startup cleanup/migration watermark already completed on this PC.</summary>
    public int StartupMigrationVersion { get; set; }
}

/// <summary>User-supplied launch extras for one library id.</summary>
public sealed class GameLaunchOverride
{
    public string? ExtraArgs { get; set; }
    public string? WorkingDirectory { get; set; }
    public bool RunAsAdmin { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ExtraArgs) &&
        string.IsNullOrWhiteSpace(WorkingDirectory) &&
        !RunAsAdmin;
}

/// <summary>One of Discord's keybinds in Exo's Discord client: an action and the keys (Windows virtual-key codes) held together.</summary>
public sealed class DiscordKeybind
{
    public string Action { get; set; } = "";
    public List<int> Keys { get; set; } = [];
}

/// <summary>Autopilot's switch and choices (PC › Autopilot). Always active with Exo's best settings while a game runs.</summary>
public sealed class AutopilotSettings
{
    /// <summary>Always on by default so Autopilot actively protects every game session.</summary>
    public bool On { get; set; } = true;
    /// <summary>The game's processes: above (the default), high, or normal (left alone). Never realtime.</summary>
    public string GamePriority { get; set; } = "above";
    /// <summary>Keep Windows Game Mode on although Autopilot covers it (the player's override).</summary>
    public bool KeepGameMode { get; set; }
    /// <summary>Keep every core awake while a game runs, where the active plan would park them.</summary>
    public bool CoreParking { get; set; } = true;
    /// <summary>Apps (executable names) always held back while a game runs, from PC › Task Manager.</summary>
    public List<string> AlwaysDemote { get; set; } = [];
    /// <summary>Apps (executable names) paused while a game runs and started again after, from PC › Task Manager.</summary>
    public List<string> Suspend { get; set; } = [];

    internal AutopilotSettings Copy() => new()
    {
        On = On, GamePriority = GamePriority is "high" or "normal" ? GamePriority : "above", KeepGameMode = KeepGameMode, CoreParking = CoreParking,
        AlwaysDemote = [.. (AlwaysDemote ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(200)],
        Suspend = [.. (Suspend ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(50)],
    };
}

/// <summary>Exo AI coordinator preferences: silent safe background tuning and GPU fan curve preset.</summary>
public sealed class ExoAiSettings
{
    /// <summary>Automatically apply safe non-elevated Best Settings in the background when idle.</summary>
    public bool AutoApplyBestSettings { get; set; } = true;
    /// <summary>Periodically re-check safe settings for Windows/driver drift while idle.</summary>
    public bool BackgroundDriftCheck { get; set; } = true;
    /// <summary>GPU fan curve preset: auto (driver default), quiet, balanced, or performance.</summary>
    public string FanPreset { get; set; } = "auto";

    internal ExoAiSettings Copy() => new()
    {
        AutoApplyBestSettings = AutoApplyBestSettings,
        BackgroundDriftCheck = BackgroundDriftCheck,
        FanPreset = NormalizePreset(FanPreset),
    };

    internal static string NormalizePreset(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "quiet" => "quiet",
        "balanced" => "balanced",
        "performance" => "performance",
        _ => "auto",
    };
}
