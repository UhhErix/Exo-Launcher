using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// The games whose Optimize is only a table: each a <see cref="Recipe"/>, so adding one is a few lines and a fixture (file,
/// format, what to pick). Every line name was seen in that game's own file, on this PC or in a public dump, and its test
/// (CatalogProfileTests) runs against a fixture of it; a game whose lines are only inferred isn't here. Values are
/// BenchmarKing's where he has measured the game (a strong card takes the upper of two, <see cref="Strong"/>; what he gives for
/// 8 GB applies at 8 GB or less). Past 8 GB textures go one step above his, as in Hell is Us. A game he hasn't covered and no
/// one has measured has its window mode (and an upscaler) at most.
/// A recipe never caps frames or changes V-Sync, never touches the install folder, and has no profile on Steam when Steam
/// Cloud holds its file on this PC (<see cref="InSteamCloud"/>). The Xbox app's copies keep their config elsewhere, so they
/// have none. Lines an update drops, or a spelling that turns out wrong, leave the line saying Partly optimized, not Applied.
/// </summary>
internal static partial class GameSettings
{
    /// <summary>A line and its value on an ordinary card, and on a strong one if he gives two.</summary>
    internal sealed record Pick(string Key, string Value, string? Strong = null);

    /// <summary>
    /// A game. Program: the install's main program, relative (its marker outside Steam, and what an update rewrites). Id: for an
    /// Unreal game "ue.{project}", the id its HDR peak was kept under before it had a profile, so Put back still finds that.
    /// Pictures: the graphics group. Textures: its line at 8 GB or less and above that. RayOff: lines that go to their value under
    /// 12 GB. Unreal: a GameUserSettings.ini, with the engine's window lines and the HDR peak. Windows: its own window lines, as
    /// borderless and fullscreen.
    /// </summary>
    internal sealed record Recipe(string Id, string Title, string? SteamApp, string Program, Func<string> File, string[] Processes, Func<byte[], IReadOnlyDictionary<string, string>, SettingsFile> Format, Pick[] Pictures)
    {
        internal bool Unreal { get; init; }
        internal Pick? Textures { get; init; }
        internal Pick[]? RayOff { get; init; }
        internal (Change[] Borderless, Change[] Fullscreen)? Windows { get; init; }
        internal Func<Pc, Group?>? Upscaler { get; init; }
    }

    private static string Local(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), .. parts]);

    private static string Documents(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), .. parts]);

    /// <summary>A file under Steam's userdata\{account}: the account that wrote it last, or "" when none has.</summary>
    private static string SteamUserFile(params string[] parts)
    {
        try
        {
            if (SteamInstallation.ResolveRoot() is not { } root || !Directory.Exists(Path.Combine(root, "userdata"))) return "";
            return Directory.EnumerateDirectories(Path.Combine(root, "userdata")).Select(account => Path.Combine([account, .. parts])).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    // ------------------------------------------------------------------ the formats

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_]\w*)\((?<quote>""?)(?<value>[^"")]*)\k<quote>\)\s*$")]
    private static partial Regex ScrLine();

    [GeneratedRegex(@"^\s*<(?<name>\w+)(?:\s+value=""(?<value>[^""]*)""\s*/>|>(?<value>[^<]*)</\k<name>>)\s*$")]
    private static partial Regex XmlLine();

    [GeneratedRegex(@"(?<name>Template)=""(?<value>[^""]*)""|Name=""(?<name>\w+)""\s+(?:_select|_value)=""(?<value>[^""]*)""")]
    private static partial Regex PearlAbyssLine();

    [GeneratedRegex(@"^\s*(?:\[""(?<name>[^""]+)""\]|(?<name>[A-Za-z_]\w*))\s*=\s*(?<quote>""?)(?<value>[^"",{]*)\k<quote>\s*,?\s*$")]
    private static partial Regex LuaLine();

    [GeneratedRegex(@"^\s*""(?<name>[^""]+)""\s+""(?<value>[^""]*)""\s*$")]
    private static partial Regex ValveLine();

    [GeneratedRegex(@"^\s*""(?<field>name|value|index)""\s*:\s*(?<quote>""?)(?<value>[^"",]*)\k<quote>\s*,?\s*$")]
    private static partial Regex CyberpunkLine();

    /// <summary>Latin-1, one to one, so what Exo doesn't change goes back as it was; a UTF-16 file has no setting lines.</summary>
    private static (string[] Lines, string NewLine, bool Bom) TextOf(byte[] bytes)
    {
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var text = bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] ? "" : Encoding.Latin1.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return (text.Split(newLine), newLine, bom);
    }

    private static string AllowedOn(string allowed, string value) => allowed == Toggle ? ToggleChoices(value) : allowed;

    /// <summary>A file with one setting a line, whose name and value <paramref name="line"/> finds. Only <paramref name="keys"/> are settings.</summary>
    internal static SettingsFile ParseLines(byte[] bytes, Regex line, IReadOnlyDictionary<string, string> keys)
    {
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..]) return new SettingsFile([Encoding.Latin1.GetString(bytes)], "\r\n", false, [], Encoding.Latin1);
        var (lines, newLine, bom) = TextOf(bytes);
        var settings = new List<Line>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (line.Match(lines[i]) is not { Success: true } m || !keys.TryGetValue(m.Groups["name"].Value, out var allowed)) continue;
            var value = m.Groups["value"];
            settings.Add(new Line(i, m.Groups["name"].Value, m.Groups["name"].Value, value.Value, AllowedOn(allowed, value.Value), value.Index, value.Length));
        }
        return new SettingsFile(lines, newLine, bom, settings, Encoding.Latin1);
    }

    /// <summary>
    /// Cyberpunk's UserSettings.json: each option's name, value and index on lines of their own, the index a line called
    /// "{name}.index". An option in two groups (CrowdDensity) is two lines of one name.
    /// </summary>
    internal static SettingsFile ParseCyberpunk(byte[] bytes, IReadOnlyDictionary<string, string> keys)
    {
        var (lines, newLine, bom) = TextOf(bytes);
        var settings = new List<Line>();
        var option = "";
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            if (CyberpunkLine().Match(lines[i]) is not { Success: true } m) continue;
            var field = m.Groups["field"].Value;
            var value = m.Groups["value"];
            if (field == "name") { option = value.Value; continue; }
            var name = field == "index" ? option + ".index" : option;
            if (!keys.TryGetValue(name, out var allowed)) continue;
            var again = seen.GetValueOrDefault(name);
            seen[name] = again + 1;
            settings.Add(new Line(i, name, again == 0 ? name : $"{name}#{again}", value.Value, AllowedOn(allowed, value.Value), value.Index, value.Length));
        }
        return new SettingsFile(lines, newLine, bom, settings, Encoding.Latin1);
    }

    /// <summary>A GameUserSettings.ini: [ScalabilityGroups] and whichever section holds the window lines (the game's settings class's, or the engine's).</summary>
    internal static SettingsFile ParseUnreal(byte[] bytes, IReadOnlyDictionary<string, string> keys)
    {
        var sections = new List<string> { "ScalabilityGroups" };
        if (SectionHolding(TextOf(bytes).Lines, "FullscreenMode") is { } section) sections.Add(section);
        return ParseIni(bytes, sections, keys);
    }

    // ------------------------------------------------------------------ the games

    private static readonly Pick[] NoPictures = [];

    private static (Change[], Change[]) WindowOf(string borderless, string fullscreen, params string[] keys) =>
        ([.. keys.Select(key => new Change(key, borderless))], [.. keys.Select(key => new Change(key, fullscreen))]);

    internal static readonly Recipe[] Catalog =
    [
        // PC Gaming Wiki's path; a real file in a public parser's repo (github.com/Forceflow/rdr2_settings_parser), which also names
        // each line and its ladder. His spoken verdicts, youtu.be/Hyzp4zRivis. Rockstar's cloud is saves, under Profiles.
        new("rdr2", "Red Dead Redemption 2", "1174180", "PlayRDR2.exe", () => Documents("Rockstar Games", "Red Dead Redemption 2", "Settings", "system.xml"), ["RDR2", "PlayRDR2"],
            (bytes, keys) => ParseLines(bytes, XmlLine(), keys),
            [
                new("textureQuality", "kSettingLevel_Ultra"), new("anisotropicFiltering", "4"), new("tessellation", "kSettingLevel_Ultra"),
                new("POMQuality", "kSettingLevel_Medium", "kSettingLevel_High"), new("shadowQuality", "kSettingLevel_High", "kSettingLevel_Ultra"),
                new("farShadowQuality", "kSettingLevel_Ultra"), new("shadowSoftShadows", "kSettingLevel_Ultra"), new("shadowGrassShadows", "kSettingLevel_Ultra"),
                new("shadowLongShadows", "true"), new("ssao", "kSettingLevel_High"), new("ssaoFullScreenEnabled", "false"),
                new("lightingQuality", "kSettingLevel_Medium", "kSettingLevel_High"), new("ambientLightingQuality", "kSettingLevel_Ultra"),
                new("scatteringVolumeQuality", "kSettingLevel_Medium"), new("volumetricsRaymarchQuality", "kSettingLevel_High", "kSettingLevel_Ultra"),
                new("volumetricsLightingQuality", "kSettingLevel_Ultra"), new("volumetricsRaymarchResolutionUnclamped", "false"),
                new("reflectionQuality", "kSettingLevel_Medium"), new("reflectionMSAA", "0"), new("mirrorQuality", "kSettingLevel_Ultra"),
                new("treeQuality", "kSettingLevel_Ultra"), new("treeTessellationEnabled", "false"), new("waterRefractionQuality", "kSettingLevel_Medium"),
                new("waterReflectionQuality", "kSettingLevel_Medium"), new("waterSimulationQuality", "2"),
                new("particleQuality", "kSettingLevel_Medium", "kSettingLevel_High"), new("particleLightingQuality", "kSettingLevel_Medium", "kSettingLevel_High"),
                new("decalQuality", "kSettingLevel_Ultra"), new("furDisplayQuality", "kSettingLevel_Medium"),
            ]) { Windows = WindowOf("2", "0", "windowed") },

        // PC Gaming Wiki's path; a current real file (version 140) in github.com/dariulone/cyberpunk-vr-port. His spoken verdicts,
        // youtu.be/6K0nMGv4ttQ. Each word goes with its place in the option's own list; the preset reads Custom, as a file
        // with these choices does. Steam Cloud is Saved Games, not this folder.
        new("cyberpunk2077", "Cyberpunk 2077", "1091500", @"bin\x64\Cyberpunk2077.exe", () => Local("CD Projekt Red", "Cyberpunk 2077", "UserSettings.json"), ["Cyberpunk2077"], ParseCyberpunk,
            [
                new("QuickPresets", "Custom"), new("QuickPresets.index", "0"),
                new("ColorPrecision", "Medium"), new("ColorPrecision.index", "0"),
                new("MaxDynamicDecals", "Ultra"), new("MaxDynamicDecals.index", "3"),
                new("ContactShadows", "true"),
                new("Anisotropy", "16"), new("Anisotropy.index", "3"),
                new("ShadowMeshQuality", "High"), new("ShadowMeshQuality.index", "2"),
                new("LocalShadowsQuality", "Medium", "High"), new("LocalShadowsQuality.index", "2", "3"),
                new("CascadedShadowsRange", "Medium"), new("CascadedShadowsRange.index", "1"),
                new("CascadedShadowsResolution", "Medium", "High"), new("CascadedShadowsResolution.index", "1", "2"),
                new("DistantShadowsResolution", "High"), new("DistantShadowsResolution.index", "1"),
                new("VolumetricFogResolution", "Medium"), new("VolumetricFogResolution.index", "1"),
                new("VolumetricCloudsQuality", "Medium"), new("VolumetricCloudsQuality.index", "1"),
                new("ScreenSpaceReflectionsQuality", "Medium", "High"), new("ScreenSpaceReflectionsQuality.index", "2", "3"),
                new("AmbientOcclusion", "Low", "Medium"), new("AmbientOcclusion.index", "1", "2"),
                new("MirrorQuality", "High"), new("MirrorQuality.index", "2"),
                new("CrowdDensity", "Medium"), new("CrowdDensity.index", "1"),
            ]),

        // TechPowerUp prints the whole file (techpowerup.com/review/alan-wake-2-performance-benchmark/4.html), the same form as
        // CONTROL Resonant's. His spoken verdicts, youtu.be/ANcq4xBMmzw. The numbers count up from the menu's lowest step
        // he names, as that sibling's do: seen for post-processing and ray tracing, inferred for the rest. Ray and path tracing as
        // in CONTROL Resonant: off under 12 GB. Its window mode's numbers aren't sourced for this game, so it has no choice.
        new("alanwake2", "Alan Wake 2", null, "AlanWake2.exe", () => Local("Remedy", "AlanWake2", "renderer.ini"), ["AlanWake2"], ParseJson,
            [
                new("m_eHighQualityPost", "0"), new("m_eVolumeLightQuality", "0", "1"), new("m_eVolumetricSpotLightQuality", "0"), new("m_eGIQuality", "2"),
                new("m_eShadowResolutionQuality", "1", "2"), new("m_eSSRQuality", "1"), new("m_eScatteredEntityQuantity", "1", "2"), new("m_bLensDistortion", "false"),
            ])
        {
            RayOff = [new("m_eRTReflectionQuality", "0"), new("m_eRTTransparentQuality", "0"), new("m_eRTIndirectDiffuseQuality", "0"), new("m_eRTReSTIRQuality", "0"), new("m_eRTDebrisQuality", "0"), new("m_ePTQuality", "0")],
            Upscaler = pc => string.IsNullOrEmpty(pc.Gpu) ? null : GeForceRtx().IsMatch(pc.Gpu)
                ? new("upscaler", "DLSS for your card", [new("m_eSSAAMethod", "2")]) : new("upscaler", "FSR for your card", [new("m_eSSAAMethod", "3")]),
        },

        // The file with its own comments on each line's numbers (steamcommunity.com/sharedfiles/filedetails/?id=3570571459). His list,
        // youtu.be/CLGKeflC8xA. Shadow Quality's ladder doesn't match the file's and Post-process has no line, so those stay yours.
        new("dyinglightbeast", "Dying Light: The Beast", "3008130", @"ph_ft\work\bin\x64\DyingLightGame_TheBeast_x64_rwdi.exe", () => Documents("dying light the beast", "out", "settings", "video.scr"), ["DyingLightGame_TheBeast_x64_rwdi"],
            (bytes, keys) => ParseLines(bytes, ScrLine(), keys),
            [
                new("AsyncCompute", "1"), new("TextureQuality", "Medium"), new("LodQuality", "0", "1"), new("LodMultiplier", "130", "150"), new("ScreenSpaceShadows", "1"),
                new("AmbientOcclusionQuality", "1"), new("VoxelGiQuality", "3"), new("ReflectionsQuality", "1", "2"), new("FogQuality", "1"),
            ]) { Windows = WindowOf("1", "2", "WindowMode") },

        // The same format (steamcommunity.com/sharedfiles/filedetails/?id=3161508499). His spoken verdicts, youtu.be/n-dqVeeP5TA (2022).
        new("dyinglight2", "Dying Light 2", "534380", @"ph\work\bin\x64\DyingLightGame_x64_rwdi.exe", () => Documents("dying light 2", "out", "settings", "video.scr"), ["DyingLightGame_x64_rwdi"],
            (bytes, keys) => ParseLines(bytes, ScrLine(), keys),
            [
                new("GpuFxQuality", "0"), new("ContactShadowsQuality", "4"), new("AmbientOcclusionQuality", "0", "2"), new("VoxelGiQuality", "3"),
                new("ReflectionsQuality", "0", "1"), new("FogQuality", "0"),
            ]) { Windows = WindowOf("1", "2", "WindowMode") },

        // steamcommunity.com/app/3321460/discussions/0/805720464937919738: the file; /796715497279289082: Custom, so the menu
        // keeps these. His list, youtu.be/ybSTkBBibVc; Ray Tracing and Particles have no line seen. Steam's rules cover only *.save.
        new("crimsondesert", "Crimson Desert", "3321460", @"bin64\CrimsonDesert.exe", () => Local("Pearl Abyss", "CD", "save", "user_engine_option_save.xml"), ["CrimsonDesert"],
            (bytes, keys) => ParseLines(bytes, PearlAbyssLine(), keys),
            [
                new("Template", "/OS_M_Windows::Custom.engineoptiongraphics"), new("_modelQualityLevelSelect", "High", "Ultra"), new("_textureQualityLevelSelect", "Ultra"),
                new("_shadowQualityLevelSelect", "Medium", "Ultra"), new("_lightingQualityLevelSelect", "Ultra", "Cinematic"), new("_reflectionQualityLevelSelect", "Ultra", "Cinematic"),
                new("_enableAdvancedWeatherEffect", "True"), new("_waterQualityLevelSelect", "High"), new("_foliageDensityLevelSelect", "High"), new("_volumetricFogQualityLevelSelect", "High"),
                new("_effectQualityLevelSelect", "High"), new("_simulationQualityLevelSelect", "High"), new("_postProcessingQualityLevelSelect", "Medium"),
            ]),

        // Two players' files (steamcommunity.com/app/2842040/discussions/0/694249110938632747). His list, youtu.be/l5Y7nOtXM38. Only
        // lines named as his menu names them. The file's last lines hold a hash, which PC Gaming Wiki's own edits leave alone.
        new("outlaws", "Star Wars Outlaws", "2842040", "Outlaws.exe", () => Documents("My Games", "Outlaws", "graphic settings.cfg"), ["Outlaws", "Outlaws_Plus"],
            (bytes, keys) => ParseLines(bytes, LuaLine(), keys),
            [
                new("rtxdi", "Off"), new("microdetail quality", "High"), new("particle quality", "Low", "Medium"), new("scatter density", "Medium"), new("shadow quality2", "High"),
                new("shadow proxies", "true"), new("destruction quality", "High"), new("terrain tessellation quality", "Medium"), new("rtdiffuseRes", "High"), new("rtspec", "High"),
                new("rtspecRes", "High"), new("bvhQuality", "High"), new("volumetric clouds", "Medium"),
            ]),

        // A player's full file (steamcommunity.com/app/2623190/discussions/0/604166319349340582), where all Ultra is 3. His list,
        // youtu.be/TMwDCxa2u7A. PC Gaming Wiki: the game uses the fixed Documents path, not a moved one.
        new("ue.Altar", "The Elder Scrolls IV: Oblivion Remastered", "2623190", "OblivionRemastered.exe",
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "My Games", "Oblivion Remastered", "Saved", "Config", "Windows", "GameUserSettings.ini"),
            ["OblivionRemastered", "Altar-Win64-Shipping", "Altar"], ParseUnreal,
            [
                new("sg.ViewDistanceQuality", "1", "2"), new("sg.EffectsQuality", "1", "2"), new("sg.FoliageQuality", "1"), new("sg.ShadowQuality", "1", "2"),
                new("sg.GlobalIlluminationQuality", "2"), new("sg.ReflectionQuality", "2"), new("sg.PostProcessQuality", "2"),
            ]) { Unreal = true, Textures = new("sg.TextureQuality", "0", "2") },

        // pcoptimizedsettings.com/metal-gear-solid-delta-pc-optimization-settings-guide-ue5-engine-tweaks, which measures these lines. His
        // list, youtu.be/Gmr7fwJgvdo. Low turns shadows and lighting off, so never below Medium.
        new("ue.MGSDelta", "Metal Gear Solid Delta: Snake Eater", "2417610", "MGSDelta.exe", () => Local("MGSDelta", "Saved", "Config", "Windows", "GameUserSettings.ini"),
            ["MGSDelta", "MGSDelta-Win64-Shipping"], ParseUnreal,
            [new("sg.ShadowQuality", "1", "2"), new("sg.GlobalIlluminationQuality", "1", "2")]) { Unreal = true, Textures = new("sg.TextureQuality", "2", "3") },

        // A player's file (steamcommunity.com/app/1282100/discussions/0/3809533247346384555). His spoken verdicts, youtu.be/b2kGwwHEBWk.
        new("ue.Remnant2", "Remnant II", "1282100", "Remnant2.exe", () => Local("Remnant2", "Saved", "Config", "Windows", "GameUserSettings.ini"),
            ["Remnant2", "Remnant2-Win64-Shipping"], ParseUnreal,
            [new("sg.ShadowQuality", "1"), new("sg.PostProcessQuality", "1", "2"), new("sg.FoliageQuality", "2", "3"), new("sg.EffectsQuality", "1")]) { Unreal = true },

        // Popular, unmeasured: the window mode alone. Public tools have written these lines since the 2023 beta (github.com/komeg1/cs2res_fix)
        // with no punishment found; VendorID, DeviceID and the rest stay, or the game rebuilds the file.
        new("cs2", "Counter-Strike 2", "730", @"game\bin\win64\cs2.exe", () => SteamUserFile("730", "local", "cfg", "cs2_video.txt"), ["cs2"],
            (bytes, keys) => ParseLines(bytes, ValveLine(), keys), NoPictures)
        { Windows = ([new("setting.fullscreen", "0"), new("setting.nowindowborder", "1")], [new("setting.fullscreen", "1"), new("setting.nowindowborder", "0")]) },

        // Popular, unmeasured: the window mode, and DLSS on an RTX card, both as two players' files write them
        // (pastebin.com/bvq1rRHq, pastebin.com/7ts7X71K); true fullscreen as Path of Exile 1's does. FSR's spelling wasn't seen.
        new("poe2", "Path of Exile 2", "2694490", "PathOfExileSteam.exe", () => Documents("My Games", "Path of Exile 2", "poe2_production_Config.ini"), ["PathOfExileSteam", "PathOfExile"],
            (bytes, keys) => ParseIni(bytes, "DISPLAY", keys), NoPictures)
        {
            Windows = ([new("fullscreen", "false"), new("borderless_windowed_fullscreen", "true")], [new("fullscreen", "true"), new("borderless_windowed_fullscreen", "false")]),
            Upscaler = pc => !string.IsNullOrEmpty(pc.Gpu) && GeForceRtx().IsMatch(pc.Gpu) ? new("upscaler", "DLSS Quality", [new("upscale", "DLSS"), new("upscale_quality", "Quality")]) : null,
        },
    ];

    /// <summary>The catalog's entry for this game: Steam's by its app, any other store's by its program; the Xbox app's copies keep their config elsewhere.</summary>
    internal static Recipe? RecipeFor(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path) || game.Store == StoreKind.Xbox) return null;
        if (game.Store == StoreKind.Steam) return Array.Find(Catalog, recipe => recipe.SteamApp is { } app && game.Id == "steam:" + app);
        return Array.Find(Catalog, recipe => File.Exists(Path.Combine(game.Path, recipe.Program)));
    }

    /// <summary>The recipe's profile for this PC.</summary>
    internal static Profile ProfileOf(Recipe recipe, Pc pc)
    {
        var keys = new Lazy<Dictionary<string, string>>(() => KeysOf(recipe));
        IReadOnlyList<string>? window = recipe.Unreal ? UnrealWindowLines : recipe.Windows is { } own ? [.. own.Borderless.Select(change => change.Key)] : null;
        return new(recipe.Id, recipe.Title, recipe.File(), _ => GroupsOf(recipe, pc), recipe.Processes, Format: bytes => recipe.Format(bytes, keys.Value),
            Program: game => string.IsNullOrEmpty(game.Path) ? null : Path.Combine(game.Path, recipe.Program),
            WindowMode: window is null ? null : pc.Fullscreen ? Fullscreen : Borderless, WindowLines: window);
    }

    internal static IReadOnlyList<Group> GroupsOf(Recipe recipe, Pc pc)
    {
        var strong = Strong(pc.Gpu);
        List<Group> groups = [];
        if (recipe.Unreal) groups.Add(UnrealWindow(pc.Fullscreen));
        else if (recipe.Windows is { } own) groups.Add(new("display", pc.Fullscreen ? "Fullscreen" : "Borderless at the highest refresh", pc.Fullscreen ? own.Fullscreen : own.Borderless));
        if (recipe.Pictures.Length > 0)
            groups.Add(new("quality", strong ? "Graphics for a strong card" : "Graphics for this card", [.. recipe.Pictures.Select(pick => new Change(pick.Key, strong && pick.Strong is { } upper ? upper : pick.Value))]));
        if (recipe.Textures is { } textures && pc.VideoMemoryGb is { } memory)
            groups.Add(new("memory", "Textures for this card's memory", [new(textures.Key, memory <= 8.5 ? textures.Value : textures.Strong ?? textures.Value)]));
        if (recipe.RayOff is { } ray && pc.VideoMemoryGb is < 12)
            groups.Add(new("ray", "Ray tracing off", [.. ray.Select(pick => new Change(pick.Key, pick.Value))]));
        if (recipe.Upscaler?.Invoke(pc) is { } upscaler) groups.Add(upscaler);
        // A game with its window mode alone has nothing else written.
        if (recipe.Unreal && recipe.Pictures.Length > 0 && pc.HdrPeak is { } peak) groups.Add(UnrealHdrGroup(peak));
        return groups;
    }

    /// <summary>Each line the recipe can write and every value it may give it, on any PC: what the line takes.</summary>
    private static Dictionary<string, string> KeysOf(Recipe recipe)
    {
        Pc[] pcs =
        [
            new("nvidia", 8, 16, Gpu: "NVIDIA GeForce RTX 4080", HdrPeak: 1000), new("nvidia", 8, 16, Gpu: "NVIDIA GeForce RTX 4080", Fullscreen: true),
            new("amd", 8, 8, Gpu: "AMD Radeon RX 6600", Fullscreen: true), new("amd", 8, 8, Gpu: "AMD Radeon RX 6600"), new(null, null, null),
        ];
        var values = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var change in pcs.SelectMany(pc => GroupsOf(recipe, pc)).SelectMany(group => group.Changes))
        {
            if (!values.TryGetValue(change.Key, out var seen)) values[change.Key] = seen = new(StringComparer.Ordinal);
            seen.Add(change.Value);
        }
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, seen) in values)
            keys[key] = key == UnrealHdrNits ? UnrealHdrRange
                : seen.All(value => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)) ? Toggle
                : "one of " + string.Join(", ", seen);
        return keys;
    }

    /// <summary>
    /// Whether Steam Cloud holds this file on this PC: Steam lists every file it syncs for a game in its remotecache.vdf, as
    /// "Sekiro/GraphicsConfig.xml". A game's own settings in the cloud can come back over what Exo wrote, so Exo stays out.
    /// </summary>
    internal static bool InSteamCloud(GameEntry game, string file)
    {
        if (game.Store != StoreKind.Steam || !game.Id.StartsWith("steam:", StringComparison.Ordinal)) return false;
        try
        {
            if (SteamInstallation.ResolveRoot() is not { } root || !Directory.Exists(Path.Combine(root, "userdata"))) return false;
            return Directory.EnumerateDirectories(Path.Combine(root, "userdata")).Select(account => Path.Combine(account, game.Id["steam:".Length..], "remotecache.vdf"))
                .Where(File.Exists).Any(cache => CloudLists(File.ReadLines(cache, Encoding.UTF8), file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A remotecache.vdf's lines name this file: a lone quoted path the file's own path ends with.</summary>
    internal static bool CloudLists(IEnumerable<string> remotecache, string file)
    {
        var path = file.Replace('\\', '/');
        foreach (var raw in remotecache)
        {
            var line = raw.Trim();
            if (line.Length > 2 && line[0] == '"' && line[^1] == '"' && line.IndexOf('"', 1) == line.Length - 1 && line.Contains('/', StringComparison.Ordinal)
                && path.EndsWith("/" + line[1..^1], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
