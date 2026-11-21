using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExoLauncher.Services;

/// <summary>A filter as exo-eq.json keeps it.</summary>
internal sealed class EqBand
{
    /// <summary>"PK", "LSC" or "HSC".</summary>
    public string Type { get; set; } = "PK";
    public double Hz { get; set; }
    public double Gain { get; set; }
    public double Q { get; set; } = 1.41;

    internal EqFilter Filter => ExoEqConfig.Clamp(new EqFilter(Type switch { "LSC" => EqKind.LowShelf, "HSC" => EqKind.HighShelf, _ => EqKind.Peak }, Hz, Gain, Q));

    internal static EqBand Of(EqFilter f) => new()
    {
        Type = f.Kind switch { EqKind.LowShelf => "LSC", EqKind.HighShelf => "HSC", _ => "PK" },
        Hz = Math.Round(f.Hz, 2), Gain = Math.Round(f.Gain, 2), Q = Math.Round(f.Q, 3),
    };
}

/// <summary>The headphone correction picked for an output: AutoEq's result and its filters, kept so it plays offline.</summary>
internal sealed class EqCorrection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Form { get; set; } = "over-ear";
    public List<EqBand> Filters { get; set; } = [];
}

/// <summary>One output's EQ: the headphones on it and its sound.</summary>
internal sealed class EqOutput
{
    /// <summary>Windows' name for it when last seen, for the list of other outputs.</summary>
    public string Name { get; set; } = "";
    public EqCorrection? Headphone { get; set; }
    public string Sound { get; set; } = ExoEqSounds.Flat;
}

internal sealed class EqNight
{
    public bool On { get; set; }
    public string Start { get; set; } = "21:00";
    public string End { get; set; } = "06:00";
    /// <summary>The hours follow Windows' Night light schedule.</summary>
    public bool Sync { get; set; }
}

/// <summary>
/// exo-eq.json: the player's EQ. Outputs are keyed by their endpoint id (<c>{…}</c>), so the list is this PC's: Equalizer
/// APO picks each output's block itself, which is how the EQ follows a headset the moment Windows moves to it.
/// </summary>
internal sealed class EqStore
{
    public int Version { get; set; } = 1;
    /// <summary>The switch: off writes exo.txt with no filters (the Include line stays).</summary>
    public bool On { get; set; } = true;
    public Dictionary<string, EqOutput> Outputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The Custom sound's bands (ten graphic bands from the page; any peak or shelf is taken).</summary>
    public List<EqBand> Custom { get; set; } = [];
    /// <summary>A game's own sound (Exo's game id to a sound), on while it runs, the output's own after it exits.</summary>
    public Dictionary<string, string> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public EqNight Night { get; set; } = new();
    /// <summary>Lucid's settings were read once (<see cref="LucidImport"/>); never again.</summary>
    public bool LucidImported { get; set; }
    /// <summary>Lucid's headphone by its name, waiting for AutoEq's list, and the outputs it was on.</summary>
    public string? LucidHeadphone { get; set; }
    public List<string> LucidOutputs { get; set; } = [];
    /// <summary>Lucid's EQ block in config.txt goes off once its headphone's correction is in Exo EQ (it was Exo's Lucid that played it).</summary>
    public bool LucidBlockOff { get; set; }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static EqStore Read(string text)
    {
        var store = JsonSerializer.Deserialize<EqStore>(text, Json) ?? new EqStore();
        // Keys compared without case whatever the file held.
        store.Outputs = new Dictionary<string, EqOutput>(store.Outputs.Where(o => ExoEqConfig.IsGuid(o.Key)), StringComparer.OrdinalIgnoreCase);
        store.Games = new Dictionary<string, string>(store.Games.Where(g => ExoEqSounds.Known(g.Value)), StringComparer.OrdinalIgnoreCase);
        return store;
    }

    internal string Text() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// The sounds a player picks over the correction, each a few gentle filters, and how one output's chain is made. Pure.
/// </summary>
internal static class ExoEqSounds
{
    internal const string Flat = "flat", Bass = "bass", Vocal = "vocal", Footsteps = "footsteps", Cinematic = "cinematic", Custom = "custom";

    internal static readonly string[] All = [Flat, Bass, Vocal, Footsteps, Cinematic, Custom];

    internal static bool Known(string? sound) => sound is not null && All.Contains(sound);

    internal static string Name(string sound) => sound switch
    {
        Bass => "Bass", Vocal => "Vocal", Footsteps => "Footsteps", Cinematic => "Immersion", Custom => "Custom", _ => "Flat",
    };

    /// <summary>The graphic bands of the page's Custom sound, at Q 1.41 (an octave each).</summary>
    internal static readonly double[] Graphic = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    private static EqFilter Pk(double hz, double gain, double q) => new(EqKind.Peak, hz, gain, q);

    internal static readonly IReadOnlyList<EqFilter> IemNaturalTarget =
    [
        Pk(190, 1.5, 0.85),
        Pk(3400, -2.0, 1.35),
        Pk(5200, -1.0, 2.0),
    ];

    internal static bool UsesIemRefinement(EqCorrection? headphone) =>
        headphone is { Filters.Count: > 0 } && headphone.Form is "in-ear" or "earbud";

    internal static IReadOnlyList<EqFilter> Filters(string sound, IReadOnlyList<EqBand> custom) => sound switch
    {
        Bass => [new(EqKind.LowShelf, 90, 4.0, 0.71), Pk(250, -1.0, 1.1)],
        Vocal => [new(EqKind.LowShelf, 85, -1.5, 0.71), Pk(210, -1.8, 0.9), Pk(1500, 1.5, 1.0), Pk(3100, 1.8, 1.3), Pk(6800, -1.2, 2.0)],
        Footsteps => [new(EqKind.LowShelf, 105, -5.0, 0.71), Pk(260, -2.0, 1.1), Pk(1150, 1.5, 1.2), Pk(2600, 2.8, 1.15), Pk(4200, 2.0, 1.4), Pk(7500, -2.0, 1.8), new(EqKind.HighShelf, 11000, -1.5, 0.71)],
        Cinematic => [new(EqKind.LowShelf, 75, 3.0, 0.71), Pk(240, -1.2, 1.0), Pk(2200, 0.8, 1.0), new(EqKind.HighShelf, 9000, 1.5, 0.71)],
        Custom => [.. custom.Select(b => b.Filter).Where(f => Math.Abs(f.Gain) >= 0.05)],
        _ => [],
    };

    /// <summary>Night Shift's warmer sound: the top softened, a little low end added.</summary>
    internal static readonly IReadOnlyList<EqFilter> Night = [new(EqKind.HighShelf, 5000, -3, 0.71), new(EqKind.LowShelf, 150, 1, 0.71)];

    /// <summary>Whether <paramref name="now"/> is inside start to end (across midnight when end comes first).</summary>
    internal static bool InHours(TimeSpan now, TimeSpan start, TimeSpan end) =>
        start == end || (start < end ? now >= start && now < end : now >= start || now < end);

    internal static TimeSpan? Clock(string text) =>
        TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var time) && time.TotalHours < 24 ? time : null;

    /// <summary>What an output plays now: its correction, then the running game's automatic or saved sound or its own, then Night Shift.</summary>
    internal static EqChain Chain(string guid, EqOutput output, EqStore store, (string Id, string Title)? game, bool night, string? autoSound = null, bool desktopFlat = false)
    {
        var notes = new List<string>();
        var filters = new List<EqFilter>();
        if (!string.IsNullOrWhiteSpace(output.Name)) notes.Add(output.Name);
        if (output.Headphone is { Filters.Count: > 0 } headphone)
        {
            notes.Add($"{headphone.Name}: correction from AutoEq (MIT), measured by {headphone.Source}");
            filters.AddRange(headphone.Filters.Select(f => f.Filter));
            if (UsesIemRefinement(headphone))
            {
                notes.Add("Target refinement: JM-1 / IEF 2025 natural IEM balance");
                filters.AddRange(IemNaturalTarget);
            }
        }
        string sound;
        if (autoSound is { } auto && Known(auto))
        {
            sound = auto;
            if (game is { } playing) notes.Add($"Sound: {Name(sound)}, for {playing.Title}");
        }
        else if (game is { } playing && store.Games.TryGetValue(playing.Id, out var own))
        {
            sound = own;
            notes.Add($"Sound: {Name(sound)}, for {playing.Title}");
        }
        else
        {
            sound = desktopFlat ? Flat : output.Sound;
            if (sound != Flat) notes.Add("Sound: " + Name(sound));
        }
        filters.AddRange(Filters(sound, store.Custom));
        if (night)
        {
            notes.Add("Night Shift");
            filters.AddRange(Night);
        }
        return new EqChain(guid, notes, filters);
    }
}
