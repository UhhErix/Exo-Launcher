using System.Text.Json;

namespace ExoLauncher.Helpers;

/// <summary>
/// What a screen's link carries, and what a color needs of it at a mode. A color's data rate is the mode's pixel rate
/// (Windows' pixel clock, blanking included) times its bits a pixel: 3 x bpc for RGB and YCbCr 4:4:4, 2 x bpc for 4:2:2
/// on DisplayPort (HDMI carries 4:2:2 in a 24-bit container at any depth), 1.5 x bpc for 4:2:0. A link's data rate is
/// after line coding: DisplayPort's 8b/10b over 4 lanes (HBR2 5.4 Gbps a lane on 1.2, HBR3 8.1 on 1.4), HDMI's TMDS
/// (24 data bits a clock: 600 MHz on 2.0) and FRL's 16b/18b (4 lanes of 12 Gbps on 2.1). Display Stream Compression,
/// where the GPU and the screen both have it, sends 12 bits a pixel for a deeper color and 8 for 8-bit.
/// Windows doesn't say which DisplayPort generation a link trained at (nor does ADLX's display interface), and AMD's
/// driver takes a depth the link can't carry and quietly stays on the one it ran, so a DisplayPort link's limit is
/// learned (<see cref="Memory"/>); HDMI's comes from the screen's EDID (<see cref="MonitorCaps.Takes"/>).
/// </summary>
internal static class DisplayLink
{
    internal sealed record Link(string Name, double Gbps, bool Hdmi, bool Dsc = false);

    internal static readonly Link Dp12 = new("DisplayPort 1.2", 4 * 5.4 * 0.8, Hdmi: false);
    internal static readonly Link Dp14 = new("DisplayPort 1.4", 4 * 8.1 * 0.8, Hdmi: false, Dsc: true);
    internal static readonly Link Hdmi20 = new("HDMI 2.0", 0.6 * 24, Hdmi: true);
    internal static readonly Link Hdmi21 = new("HDMI 2.1", 4 * 12 * 16.0 / 18, Hdmi: true, Dsc: true);

    /// <summary>Bits a pixel of this color takes on the wire, uncompressed.</summary>
    internal static double Bits(OutputColor color, bool hdmi) => color.Format switch
    {
        "422" => hdmi ? 24 : 2 * color.Bpc,
        "420" => 1.5 * color.Bpc,
        _ => 3 * color.Bpc,
    };

    /// <summary>The Gbps this color needs at <paramref name="pixelRate"/> (Hz); compressed (DSC), at most 12 bits a pixel, 8 for 8-bit.</summary>
    internal static double Needs(OutputColor color, long pixelRate, bool hdmi, bool dsc = false) =>
        pixelRate * (dsc ? Math.Min(Bits(color, hdmi), color.Bpc > 8 ? 12 : 8) : Bits(color, hdmi)) / 1e9;

    /// <summary>True when <paramref name="link"/> carries this color at <paramref name="pixelRate"/>; <paramref name="dsc"/>: the screen takes compression too.</summary>
    internal static bool Fits(OutputColor color, long pixelRate, Link link, bool dsc = false) =>
        Needs(color, pixelRate, link.Hdmi, dsc && link.Dsc) <= link.Gbps;

    /// <summary>
    /// What each screen's link couldn't carry, by screen and connector (the device path names the screen and the port it
    /// is on): the lowest rate a color needed that the driver took and the screen didn't run. Nothing needing that much is
    /// offered on that link again, at any format or mode; a color seen running at that rate or more (a new cable, a
    /// driver that does better) lifts it. Kept in display-links.json, so it holds across restarts.
    /// </summary>
    internal sealed class Memory(string path)
    {
        internal static Memory Shared { get; } = new(Path.Combine(PathHelper.AppDataDir, "display-links.json"));

        private readonly Lock _gate = new();
        private Dictionary<string, double>? _ceilings;

        internal static string Key(string connector, string devicePath) => $"{connector}|{devicePath}";

        /// <summary>False when this link already failed at this color's rate or less; true when Exo knows of no limit (or no rate).</summary>
        internal bool Takes(string key, OutputColor color, long pixelRate, bool hdmi)
        {
            lock (_gate)
                return pixelRate <= 0 || !Load().TryGetValue(key, out var ceiling) || Needs(color, pixelRate, hdmi) < ceiling;
        }

        /// <summary>A color the driver took but the screen didn't run: its rate becomes the link's ceiling when lower. False without a rate to remember.</summary>
        internal bool Failed(string key, OutputColor color, long pixelRate, bool hdmi)
        {
            if (pixelRate <= 0) return false;
            var needs = Needs(color, pixelRate, hdmi);
            lock (_gate)
            {
                var ceilings = Load();
                if (ceilings.TryGetValue(key, out var ceiling) && ceiling <= needs) return true;
                ceilings[key] = needs;
                Save(ceilings);
                return true;
            }
        }

        /// <summary>A color the screen runs now: a ceiling at or under its rate was wrong, and goes.</summary>
        internal void Ran(string key, OutputColor color, long pixelRate, bool hdmi)
        {
            if (pixelRate <= 0) return;
            lock (_gate)
            {
                var ceilings = Load();
                if (!ceilings.TryGetValue(key, out var ceiling) || Needs(color, pixelRate, hdmi) < ceiling) return;
                ceilings.Remove(key);
                Save(ceilings);
                AppLog.Info($"Display: {color.Bpc}-bit runs at {Needs(color, pixelRate, hdmi):0.0} Gbps on a link Exo had held to under {ceiling:0.0}; the limit is lifted.");
            }
        }

        private Dictionary<string, double> Load()
        {
            if (_ceilings is not null) return _ceilings;
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path)) is { } saved)
                    return _ceilings = new Dictionary<string, double>(saved, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppLog.Warn("Display: link limits unreadable: " + ex.GetType().Name); }
            return _ceilings = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        private void Save(Dictionary<string, double> ceilings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(ceilings));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Display: link limits not saved: " + ex.GetType().Name); }
        }
    }
}
