namespace ExoLauncher.Services;

/// <summary>
/// Phantom Blade Zero (S-GAME; Unreal Engine 5.7, DX12; Steam's and Epic's installs; Denuvo anti-tamper, no anti-cheat).
/// Its whole Video page (upscaler and Output Scale, frame generation, Reflex, V-Sync, the frame cap, every quality level,
/// motion blur, hair, ray tracing) is JSON in a sealed save in the install's ProjectPBZ\Saved (PBZLocalSetting.sav,
/// PBZSharedSetting.sav): the game authenticates it as it loads, quarantines one that fails and stops saving settings for
/// the session, so Exo never touches it. The [GraphicsQualitySettings] and [ScalabilityGroups] in GameUserSettings.ini
/// are the engine's copy of those choices, which the sealed save is restored over at each start, so Exo writes none of
/// them either. Exo writes only what the game reads from GameUserSettings.ini alone
/// (%LOCALAPPDATA%\ProjectPBZ\Saved\Config\Windows, the same file for every store), in its settings class's section:
/// the window mode, which the engine reads before it opens the window. Borderless full screen by default, as Modern
/// Warfare 4 runs: it runs at the desktop's rate, which PC › Display sets to the screen's highest (exclusive full screen
/// picks its own), and Alt+Tab never switches modes, which recreates the swap chain the game's frame generation and DLSS
/// run on; true full screen when that's chosen on the line. The
/// rest of that section stays as the game writes it: resolution (borderless uses the desktop's), Lyra's hidden caps (144
/// in menus, 30 in the background, 60 on battery) and the benchmark it reruns at every start. No Engine.ini: by its mod
/// authors' accounts the Steam build ignores one in Saved\Config or drops its lines when it quits (theirs set the file
/// read-only or ship the engine's own WindowsEngine.ini in a pak), and no console variable has a measured gain. October
/// 2026, launch week: Wccftech's per-setting tests, DSOGaming's benchmarks, NVIDIA's notes, S-GAME's driver notice,
/// creators' videos, Nexus Mods' first 62 mods, and this PC's files and the game's own program, read and never run.
/// November 2026, all 253 Nexus mods: the settings save is still sealed (file magic PBZS, and the program names
/// "SAVE ENCRYPTION FAILED"). The graphics mods are a pak, a read-only Engine.ini, a ReShade, or a UE4SS hook with its
/// own panel. Engine.ini is a name in the program, and those mods say the game drops its lines unless the file is locked,
/// then reapplies vignette and the rest during play, so a locked file still is not the Video page. A DLSS preset L ini
/// is the slow letter on an RTX 20 or 30. The NVIDIA profile is G-SYNC off, so this profile still sets no frame cap and
/// does not turn V-Sync off: both of those live on the sealed page, and a cap under the refresh is the G-SYNC recipe.
/// BenchmarKing's Optimized Settings (youtu.be/0sIIVX23KIw, October 29 2026, measured on a 5700X3D with an RTX 3060 Ti 8 GB
/// and a 4070 Super 12 GB; Ultra preset to his: 54-65% faster) are all on that sealed page, so Exo writes none of them.
/// </summary>
internal static partial class GameSettings
{
    internal static string PhantomBladeConfig =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectPBZ", "Saved", "Config", "Windows");

    /// <summary>Phantom Blade Zero's install, any store's: its Unreal project folder.</summary>
    internal static bool IsPhantomBlade(string path) => Directory.Exists(Path.Combine(path, "ProjectPBZ"));

    /// <summary>
    /// The section's lines Exo may change, and the values Unreal takes for each: 0 full screen, 1 borderless, 2 windowed
    /// (the mode Alt+Enter goes to is 0 or 1); the HDR peak (<see cref="UnrealHdrGroup"/>).
    /// </summary>
    private static readonly Dictionary<string, string> PhantomBladeKeys = new(StringComparer.Ordinal)
    {
        ["FullscreenMode"] = "0 to 2", ["LastConfirmedFullscreenMode"] = "0 to 2", ["PreferredFullscreenMode"] = "0 to 1",
        [UnrealHdrNits] = UnrealHdrRange,
    };

    internal static SettingsFile ParsePhantomBlade(byte[] bytes) => ParseIni(bytes, "/Script/ProjectPBZ.PBZSettingsLocal", PhantomBladeKeys);

    /// <summary>
    /// The store's launcher and the game both run as PBZ.exe: nothing is written while either does. Stock Unreal 5.7 HDR,
    /// tone mapped for 1000 nits until told the screen's peak: with <paramref name="hdrPeak"/> known, that goes in too.
    /// </summary>
    internal static Profile PhantomBladeZero(int? hdrPeak = null, bool fullscreen = false) =>
        new("pbz", "Phantom Blade Zero", Path.Combine(PhantomBladeConfig, "GameUserSettings.ini"),
            _ => hdrPeak is { } peak ? [UnrealWindow(fullscreen), UnrealHdrGroup(peak)] : [UnrealWindow(fullscreen)], ["PBZ"],
            Format: ParsePhantomBlade, Program: PhantomBladeProgram, WindowMode: fullscreen ? Fullscreen : Borderless,
            WindowLines: UnrealWindowLines);

    private static readonly string[] UnrealWindowLines = ["FullscreenMode", "LastConfirmedFullscreenMode", "PreferredFullscreenMode"];

    /// <summary>Unreal's three window mode lines, at the mode chosen.</summary>
    internal static Group UnrealWindow(bool fullscreen)
    {
        var mode = fullscreen ? "0" : "1";
        return new("display", fullscreen ? "Fullscreen" : "Borderless at the highest refresh",
            [new("FullscreenMode", mode), new("LastConfirmedFullscreenMode", mode), new("PreferredFullscreenMode", mode)]);
    }

    /// <summary>The game itself, platform\{store}\PBZ.exe (the install's other PBZ.exe, steam\PBZ.exe on Steam, only starts it).</summary>
    internal static string? PhantomBladeProgram(Models.GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return null;
        var platform = Path.Combine(game.Path, "platform");
        try
        {
            return Directory.Exists(platform)
                ? Directory.EnumerateDirectories(platform).Select(store => Path.Combine(store, "PBZ.exe")).Where(File.Exists)
                    .Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
