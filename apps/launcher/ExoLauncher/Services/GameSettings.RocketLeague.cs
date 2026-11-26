namespace ExoLauncher.Services;

/// <summary>
/// Rocket League (Psyonix; Unreal Engine 3, DX11), from Epic or Steam. Both copies read one settings file per Windows
/// user, Documents\My Games\Rocket League\TAGame\Config\TASystemSettings.ini (the same Default*.ini and engine build in
/// both installs; only the per-account saves differ, SaveDataEpic and SaveData), so both get this one profile and its
/// one record of what Exo changed. The video menu's choices live in that account's encrypted .save (resolution, display
/// mode, V-Sync, the frame rate list, render quality and detail, texture, world and particle detail, light shafts, lens
/// flares, weather, anti-aliasing), which the game puts back over their ini lines, so Exo leaves those to the menu and
/// writes only lines the game reads from this file alone (October 2026: UE3 build 10897's own source, PCGamingWiki, Rocket
/// Science's latency and consistency measurements, RLCS players' settings, creators' videos and shared configs):
/// - the engine's frame pacing as Psyonix ships it, against the "tweaks" still passed round: OneFrameThreadLag off
///   measured no lower lag (Rocket Science #13 and #15) and costs frames, AllowPerFrameSleep off switches every frame
///   cap off (the menu's too), WaitForGPU on froze some PCs at start, OnlyStreamInTextures on hung loading;
/// - full resolution (no dynamic scaling, no scaled-down view: low floors gave black borders), and 16x texture filtering
///   on a card with its own memory (Psyonix ships 4x: the field's lines blur at an angle);
/// - no motion blur, depth of field, bloom (0.5 ms of lag, Rocket Science #15) or ambient occlusion, as every pro runs;
/// - the ball's ground marker, its shadow on walls and see-through goalposts kept (the "potato" configs break them).
/// Left as the player has them: the menu's own choices, dynamic shadows (some read the ball's height by it), high
/// quality shaders and effect intensity (taste), and the frame cap (CustomFPS: a line here only so a cap earlier versions
/// of Exo set can go back; Optimize caps nothing). Its display mode is in that .save, so its line has no window mode
/// choice. At its first start after an update the game
/// rebuilds the file from its Default*.ini, which it knows by [IniVersion] (those files' write times, which every update
/// and either store's copy has its own of): every line goes back to Psyonix's default at once, and the mark changes, so
/// Exo writes Optimize again once the game closes (<see cref="Resets"/>). Never read-only, which hangs its start on some
/// PCs.
/// </summary>
internal static partial class GameSettings
{
    internal static string RocketLeagueConfig =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Rocket League", "TAGame", "Config");

    /// <summary>Rocket League's install, Epic's or Steam's: its game folder and its program where Psyonix puts them.</summary>
    internal static bool IsRocketLeague(string path) =>
        Directory.Exists(Path.Combine(path, "TAGame")) && File.Exists(Path.Combine(path, "Binaries", "Win64", "RocketLeague.exe"));

    /// <summary>The live section's lines Exo may change, and what the engine takes for each.</summary>
    private static readonly Dictionary<string, string> RocketLeagueKeys = new(StringComparer.Ordinal)
    {
        ["OneFrameThreadLag"] = Toggle, ["AllowPerFrameSleep"] = Toggle, ["WaitForGPU"] = Toggle, ["OnlyStreamInTextures"] = Toggle,
        ["CustomFPS"] = "0 to 1000",
        ["UpscaleScreenPercentage"] = Toggle, ["MinimumScreenScale"] = "1 to 100", ["AllowDynamicResolution"] = Toggle,
        ["MaxAnisotropy"] = "1 to 16",
        ["MotionBlur"] = Toggle, ["DepthOfField"] = Toggle, ["Bloom"] = Toggle, ["AmbientOcclusion"] = Toggle,
        ["DynamicShadows"] = Toggle, ["bAllowD3D9MSAA"] = Toggle,
        ["DynamicDecals"] = Toggle, ["UnbatchedDecals"] = Toggle, ["bEnableForegroundShadowsOnWorld"] = Toggle, ["bUseTranslucentArenaShaders"] = Toggle,
    };

    internal static SettingsFile ParseRocketLeague(byte[] bytes) => ParseIni(bytes, "SystemSettings", RocketLeagueKeys);

    internal static Profile RocketLeague(Pc pc) =>
        new("rocketleague", "Rocket League", Path.Combine(RocketLeagueConfig, "TASystemSettings.ini"), _ => RocketLeagueGroups(pc),
            ["RocketLeague", "RocketLeague_EAC"], Nvidia: RocketLeagueNvidia, Format: ParseRocketLeague, Program: RocketLeagueProgram, Mark: IniVersion,
            Advice: _ => RocketLeagueAdvice(pc));

    internal static IReadOnlyList<string> RocketLeagueAdvice(Pc pc) =>
    [
        "Display mode and V-Sync are stored in Rocket League's encrypted account save; set Borderless or Fullscreen and V-Sync off in-game.",
        "Dynamic shadows are off for consistent frame pacing; keep DynamicShadows on if you read ball height from floor shadows.",
    ];

    /// <summary>RocketLeague.exe in the copy's own install (NVIDIA's Rocket League profile is that program in Epic's and Steam's alike).</summary>
    internal static string? RocketLeagueProgram(Models.GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return null;
        var exe = Path.Combine(game.Path, "Binaries", "Win64", "RocketLeague.exe");
        return File.Exists(exe) ? exe : null;
    }

    internal static IReadOnlyList<Group> RocketLeagueGroups(Pc pc)
    {
        List<Group> groups = [new("frames", "The game's own frame pacing",
            [new("OneFrameThreadLag", "True"), new("AllowPerFrameSleep", "True"), new("WaitForGPU", "False"), new("OnlyStreamInTextures", "False")])];
        groups.Add(new("resolution", "Full resolution, never scaled down",
            [new("UpscaleScreenPercentage", "True"), new("MinimumScreenScale", "100"), new("AllowDynamicResolution", "False")]));
        // A card with its own memory: 16x costs it nothing measurable; a laptop's shared-memory graphics keeps the game's 4x.
        if (pc.VideoMemoryGb is >= 2) groups.Add(new("filtering", "Sharpest texture filtering", [new("MaxAnisotropy", "16")]));
        groups.Add(new("effects", "No motion blur, depth of field, bloom, ambient occlusion, MSAA or dynamic shadows",
            [new("MotionBlur", "False"), new("DepthOfField", "False"), new("Bloom", "False"), new("AmbientOcclusion", "False"),
             new("DynamicShadows", "False"), new("bAllowD3D9MSAA", "False")]));
        groups.Add(new("ball", "Ball marker, wall shadows and see-through goalposts",
            [new("DynamicDecals", "True"), new("UnbatchedDecals", "True"), new("bEnableForegroundShadowsOnWorld", "True"), new("bUseTranslucentArenaShaders", "True")]));
        if (VrrFrameCap(pc) is { } cap)
            groups.Add(new("framecap", $"Frame cap at {cap} FPS for VRR", [new("CustomFPS", cap.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
        return groups;
    }

    /// <summary>
    /// Rocket League's own NVIDIA profile asks for nothing. It stays so a press takes back the driver's cap that earlier
    /// versions lifted there for the game's own (<see cref="SetNvidia"/> restores what it no longer asks for).
    /// </summary>
    internal static readonly NvidiaGame RocketLeagueNvidia = new("", _ => []);
}
