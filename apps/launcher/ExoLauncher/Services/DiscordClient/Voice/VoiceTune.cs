using System.Globalization;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Voice Tune's tunings, kept per microphone by an earlier Exo (a guided take analyzed into how far noise suppression cuts
/// and the gain to add): Exo Voice still applies a kept one (<see cref="VoiceChain"/>). Also what Equalizer APO does to a
/// microphone, read from its config.
/// </summary>
internal static class VoiceTune
{
    /// <summary>The kept tuning for a microphone (by endpoint id), if there is one.</summary>
    internal static VoiceTuning? For(string? mic) => Kept(mic ?? "default");

    // An earlier build kept a tuning from a few seconds of a call (no spectrum, as a guided take always has): not a Voice Tune.
    private static VoiceTuning? Kept(string mic) =>
        App.Services.Settings.Shared.DiscordVoiceTunes.TryGetValue(mic, out var text) && Parse(text) is { } tuning && tuning.Spectrum.Any(band => band != 0) ? tuning : null;

    // Equalizer APO's pre-mix and post-mix effects, as Windows lists them on an endpoint: Lucid Voice and others tune a
    // mic through it, and Exo Voice then gates, compresses and de-esses the same voice a second time.
    private static readonly string[] EqualizerApo = ["{EACD2258-FCAC-4FF4-B36D-419E924A6D79}", "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}"];

    // An FxProperties value naming Equalizer APO: a plain effect slot (REG_SZ) or Windows 11's list of them (REG_MULTI_SZ).
    internal static bool NamesEqualizerApo(object? value) => value switch
    {
        string one => EqualizerApo.Contains(one, StringComparer.OrdinalIgnoreCase),
        string[] many => many.Any(one => EqualizerApo.Contains(one, StringComparer.OrdinalIgnoreCase)),
        _ => false,
    };

    /// <summary>
    /// Whether Lucid Voice is on, from the settings its plugin reads (%ProgramData%\LucidEQ\lucid_voice_dsp.json):
    /// turned off, Lucid leaves its block in Equalizer APO's config and its plugin passes the sound through. Without
    /// that file (or one Exo can't read) the plugin is taken as on, since it is still in the mic's chain.
    /// </summary>
    internal static bool LucidVoiceOn(string? dspSettings)
    {
        if (dspSettings is null) return true;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(dspSettings);
            return !(doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("enabled", out var on) && on.ValueKind == System.Text.Json.JsonValueKind.False);
        }
        catch (System.Text.Json.JsonException) { return true; }
    }

    /// <summary>
    /// Whether Equalizer APO's config.txt does anything to this microphone: "lucid" when Lucid Voice's block does and
    /// Lucid Voice is on, "apo" for anything else that applies to it (a Device line naming it, or all), null when
    /// nothing does. Include lines are followed (<paramref name="include"/> reads a file beside config.txt, null when
    /// there is none); Lucid Voice's plugin, turned off, passes the sound through.
    /// </summary>
    internal static string? EqualizerApoUse(string config, string guid, Func<string, string?> include, bool lucidOn)
    {
        string? use = null;
        return Walk(config, inLucid: false, depth: 0) ? "lucid" : use;

        // True as soon as Lucid Voice's own processing applies; anything else that applies sets use to "apo".
        bool Walk(string text, bool inLucid, int depth)
        {
            var applies = true;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("# Lucid Voice begin", StringComparison.OrdinalIgnoreCase)) inLucid = true;
                else if (line.StartsWith("# Lucid Voice end", StringComparison.OrdinalIgnoreCase)) inLucid = false;
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var command = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (command.Equals("Device", StringComparison.OrdinalIgnoreCase))
                {
                    applies = value.Equals("all", StringComparison.OrdinalIgnoreCase) || value.Contains(guid, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!applies || command.Equals("Channel", StringComparison.OrdinalIgnoreCase) || command.Equals("Stage", StringComparison.OrdinalIgnoreCase)) continue;
                if (command.Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    // A file that isn't there does nothing; Equalizer APO's own nesting is shallow, so a loop stops here.
                    if (depth < 4 && include(value.Trim('"')) is { } included && Walk(included, inLucid, depth + 1)) return true;
                    continue;
                }
                // A preamp of 0 dB changes nothing.
                if (command.Equals("Preamp", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(value.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var db) && db == 0) continue;
                if (inLucid && !lucidOn && command.Equals("VSTPlugin", StringComparison.OrdinalIgnoreCase)
                    && value.Contains("lucid_dsp", StringComparison.OrdinalIgnoreCase)) continue;
                if (inLucid && lucidOn) return true;
                use = "apo";
            }
            return false;
        }
    }

    // A kept tuning as one line in settings: "limit=15;gain=-3;spec=-6,1,0,-5,-9,-12,-15". One kept before 3.11 also has
    // hp=, gate= and hum= (Voice Focus no longer tunes those) and may say limit=100: it is read with the limit at its most.
    internal static string Format(VoiceTuning t) => string.Create(CultureInfo.InvariantCulture,
        $"limit={t.SuppressionLimitDb};gain={t.GainDb};spec={string.Join(',', t.Spectrum.Select(v => v.ToString(CultureInfo.InvariantCulture)))}");

    internal static VoiceTuning? Parse(string text)
    {
        // A key given twice takes the last (ToDictionary would throw, on the audio thread that reads it).
        var f = new Dictionary<string, string>();
        foreach (var p in text.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)) f[p[0]] = p[1];
        static float Num(string? v, float min, float max) =>
            float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && float.IsFinite(n) ? Math.Clamp(n, min, max) : float.NaN;
        var limit = Num(f.GetValueOrDefault("limit"), 6, NoiseSuppressor.MostDb);
        var gain = Num(f.GetValueOrDefault("gain"), -20, 20);
        if (float.IsNaN(limit) || float.IsNaN(gain)) return null;
        var spectrum = (f.GetValueOrDefault("spec") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => Num(v, -40, 40)).ToArray();
        if (spectrum.Length != VoiceAnalysis.Bands.Length || spectrum.Any(float.IsNaN)) spectrum = new float[VoiceAnalysis.Bands.Length];
        return new VoiceTuning((int)limit, gain, spectrum);
    }
}
