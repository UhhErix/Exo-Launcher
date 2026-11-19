using System.Globalization;
using System.Text;
using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// HDR peak brightness for Unreal Engine 4 and 5 games, from this PC's screen. Unreal tone maps HDR for the peak in
/// GameUserSettings.ini's HDRDisplayOutputNits (1000 by default): UE5 hands it to its tone mapper as
/// r.HDR.Display.MaxLuminance (RenderCore's HDRConfigureCVars), UE4 rounds it to its 1000 or 2000 nit curve, and stock
/// Unreal before 5.8 never asks the screen itself. So a 600-nit screen clips every highlight above 600, and a 1500-nit one
/// is left dim. Optimize writes the main screen's real peak (<see cref="GameHdr.PeakNits"/>: Windows HDR Calibration, else
/// the EDID, else DXGI) inside 400 (DisplayHDR 400, the dimmest real HDR screen) to 2000 (Unreal's brightest curve). Paper
/// white and the UI's brightness stay the engine's defaults (r.HDR.UI.Luminance, r.HDR.Display.MidLuminance: never in
/// this file). Only in the section that holds the line (the game's settings class's, or the engine's), only for a game Exo
/// judges to have HDR (<see cref="GameHdr"/>) and without anti-cheat; Put back returns the game's own value. It changes
/// nothing until HDR is on in the game.
/// </summary>
internal static partial class GameSettings
{
    internal const string UnrealHdrNits = "HDRDisplayOutputNits", UnrealHdrRange = "400 to 2000";

    private static readonly Dictionary<string, string> UnrealHdrKeys = new(StringComparer.Ordinal) { [UnrealHdrNits] = UnrealHdrRange };

    internal static Group UnrealHdrGroup(int peak) =>
        new("hdr", "HDR peak for this screen", [new(UnrealHdrNits, Math.Clamp(peak, 400, 2000).ToString(CultureInfo.InvariantCulture), Spare: true)]);

    /// <summary>
    /// Any other Unreal game's HDR peak, as its own profile ("ue.{project}"); null without a peak, for a game without HDR,
    /// and before it wrote its settings (anti-cheat is <see cref="For"/>'s, as for every writer).
    /// </summary>
    internal static Profile? UnrealHdr(GameEntry game, int? peak)
    {
        if (peak is not { } nits || game.Store == StoreKind.Riot || GameHdr.UnrealProjects(game.Path!) is not [var project, ..]) return null;
        try
        {
            if (GameHdr.UnrealSettingsFile(project, game) is not { } file || SectionHolding(File.ReadAllLines(file, Encoding.Latin1), UnrealHdrNits) is not { } section) return null;
            if (InSteamCloud(game, file)) return null;
            if (GameHdr.Judge(game, readFiles: false).Hdr != "yes") return null;
            // Nothing is written while the game or its launcher runs.
            var programs = Directory.GetFiles(Path.Combine(project, "Binaries", "Win64"), "*.exe").Concat(Directory.GetFiles(game.Path!, "*.exe"));
            var program = GameHdr.MainProgram(game.Path!);
            return new("ue." + Path.GetFileName(project), game.Title, file, _ => [UnrealHdrGroup(nits)],
                [.. programs.Select(Path.GetFileNameWithoutExtension).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
                Format: bytes => ParseIni(bytes, section, UnrealHdrKeys), Program: _ => program);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The section holding this line: the game's settings class's ("/Script/Charlie.CharlieGameUserSettings") or the engine's.</summary>
    internal static string? SectionHolding(IEnumerable<string> lines, string key)
    {
        string? section = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim();
            else if (section is not null && line.StartsWith(key + "=", StringComparison.Ordinal)) return section;
        }
        return null;
    }
}
