using System.Buffers.Binary;
namespace ExoLauncher.Services;
/// <summary>
/// The decisions System > Graphics > HDR > HDR for HDR games makes, as plain facts in and answers out (HdrPlanTests).
/// MainWindow.Hdr.cs does the Windows and settings parts: which screen, the switch, its wait, the restore.
/// </summary>
internal static class HdrPlan
{
    /// <summary>How long the restore is held after the last game that wants HDR ended: a restart, or a launcher that
    /// reopens the game, keeps HDR on instead of flashing the screen twice.</summary>
    public const int RestoreHoldMs = 8_000;
    /// <summary>How long Exo waits for Windows to report the screen in HDR after the switch before it goes on anyway.</summary>
    public const int SwitchWaitMs = 6_000;
    /// <summary>The gamma ramp: 3 channels of 256 values, 16-bit each.</summary>
    private const int RampBytes = 3 * 256 * 2;

    /// <summary>
    /// Whether the restore is due: HDR was switched on at <paramref name="releasedAtMs"/> (Environment.TickCount64, the
    /// moment the last wanted game ended) and the hold has run out.
    /// </summary>
    public static bool RestoreDue(long releasedAtMs, long nowMs) => nowMs - releasedAtMs >= RestoreHoldMs;

    /// <summary>
    /// Whether HDR may be switched on for a screen now. Before a game's window is up, yes. Once it is up, only when it is
    /// not covering its screen: a fullscreen game (exclusive or borderless) is never switched under, since the switch
    /// blanks the screen.
    /// </summary>
    public static bool MaySwitchOn(bool gameWindowUp, bool windowCoversScreen) => !gameWindowUp || !windowCoversScreen;

    /// <summary>The first preferred screen (device path) that can show HDR and is connected; null when none.</summary>
    public static string? PickScreen(IEnumerable<string?> preferred, IEnumerable<string> capable)
    {
        var set = capable.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return preferred.FirstOrDefault(path => path is { Length: > 0 } && set.Contains(path));
    }

    /// <summary>Whether Windows reports the screen in the state asked for (the switch took effect).</summary>
    public static bool Reached(bool wantOn, bool? reported) => reported == wantOn;

    /// <summary>The gamma ramp as text for settings (base64 of 768 little-endian values); "" for none.</summary>
    public static string RampText(ushort[]? ramp)
    {
        if (ramp is not { Length: 3 * 256 }) return "";
        var bytes = new byte[RampBytes];
        for (var i = 0; i < ramp.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), ramp[i]);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The ramp <see cref="RampText"/> wrote; null when the text is empty or not a whole ramp.</summary>
    public static ushort[]? RampFrom(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 4096) return null;
        try
        {
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length != RampBytes) return null;
            var ramp = new ushort[3 * 256];
            for (var i = 0; i < ramp.Length; i++) ramp[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
            return ramp;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// The verdict in plain words for a game's row: where Exo's answer came from. <paramref name="hdr"/> is "yes", "no" or
    /// "unknown" (<see cref="GameHdr.Verdict"/>), <paramref name="from"/> its source, and <paramref name="wikiHackable"/>
    /// whether PCGamingWiki says HDR needs a mod.
    /// </summary>
    public static string Reason(string hdr, string? from, bool wikiHackable = false) => (hdr, from) switch
    {
        ("yes", "settings") => "Its own settings have an HDR option, so it outputs HDR.",
        ("yes", "learned") => "It ran with Windows HDR on and kept its HDR option on, so it outputs HDR.",
        ("yes", "wiki") => "PCGamingWiki says it has HDR.",
        ("yes", "files") => "Its files ship HDR calibration art and HDR formats.",
        ("no", _) when wikiHackable => "PCGamingWiki says its HDR needs a mod, so Auto leaves it alone.",
        ("no", _) => "PCGamingWiki says it has no HDR, so Auto leaves it alone.",
        _ => "Not known yet. Exo reads the game's settings each time it runs and checks PCGamingWiki.",
    };
}
