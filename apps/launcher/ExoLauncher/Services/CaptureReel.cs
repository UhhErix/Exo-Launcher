namespace ExoLauncher.Services;

/// <summary>
/// The host's side of a highlight reel (Captures › Make reel): several clips joined into one new video. Exo Capture joins
/// them, as they are when they are of one kind (a lossless copy of the packets), else by encoding them again; this
/// decides which clips, in what order, whether they may join, how, under what name, and what the user is told.
/// The clips themselves are only ever read. Ways it could fail the user:
/// - the clips come out in the wrong order (the order picked, or the page's), or one is in twice;
/// - clips that differ are joined as they are, and the reel breaks at the join; or clips of one kind are encoded again
///   for nothing (a slow, lossy reel);
/// - it starts during a game or a recording, or fills the drive;
/// - HDR clips of different kinds are squeezed into an SDR picture;
/// - a stopped or failed join leaves a half file, or loses a source clip;
/// - the reel is named with something Windows refuses, or over another file.
/// </summary>
internal static class CaptureReel
{
    /// <summary>The request to the add-on is one line of at most 64 KB; this many paths fit with room to spare.</summary>
    internal const int MostClips = 50;

    /// <summary>The folder (and group) a reel of clips from several games goes in.</summary>
    internal const string MixedFolder = "Reels";

    /// <param name="Folder">The clip's game folder in the captures folder: its game's name.</param>
    internal sealed record Clip(string Path, DateTime CreatedUtc, long Bytes, string Folder, string? GameId);

    /// <summary>What Exo Capture says a clip is. <paramref name="Signature"/> is equal for clips that join as they are.</summary>
    internal sealed record Facts(string Signature, int Width, int Height, double Fps, int SoundTracks, bool Hdr, double DurationMs);

    internal enum JoinMode { Copy, Reencode }

    /// <summary>How the clips join, or why they cannot.</summary>
    internal sealed record Decision(JoinMode Mode, int Width, int Height, string? Refusal);

    internal enum Go { Start, Wait, Refuse }

    /// <summary>The page's press: <paramref name="Message"/> says why not (and <paramref name="Waits"/>, that a game is why: it may offer to wait).</summary>
    internal sealed record Gate(Go Go, string? Message, bool Waits);

    /// <summary>Oldest first, as they were made; clips made at the same moment keep the order of their names.</summary>
    internal static List<Clip> Order(IEnumerable<Clip> clips) =>
        [.. clips.OrderBy(c => c.CreatedUtc).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Whether a reel may start now: not beside another, not during a game (it waits for the game to end, if asked to: it
    /// reads and writes a lot of disk, and the PC is the game's) and not while a recording is made.
    /// </summary>
    internal static Gate Admit(bool reelRunning, bool gameRunning, bool recording, bool wait) =>
        reelRunning ? new Gate(Go.Refuse, "Exo is already making a reel.", false)
        : gameRunning ? (wait ? new Gate(Go.Wait, null, false) : new Gate(Go.Refuse, "A game is running. Exo makes a reel when you aren't playing.", true))
        : recording ? new Gate(Go.Refuse, "A recording is being made. Try again when it ends.", false)
        : new Gate(Go.Start, null, false);

    /// <summary>
    /// As they are when every clip has the same signature (codec and its setup, size, frame rate, sound tracks);
    /// otherwise encoded again at the largest clip's size, each clip fitted inside it with black bars. HDR clips that differ
    /// are not squeezed into an SDR picture.
    /// </summary>
    internal static Decision Decide(IReadOnlyList<Facts> facts) =>
        facts.Select(f => f.Signature).Distinct(StringComparer.Ordinal).Count() == 1 ? new Decision(JoinMode.Copy, 0, 0, null) : Reencode(facts);

    /// <summary>Encoded again at the largest clip's size (even), or refused for HDR.</summary>
    internal static Decision Reencode(IReadOnlyList<Facts> facts)
    {
        if (facts.Any(f => f.Hdr)) return new Decision(JoinMode.Reencode, 0, 0, "These clips differ and one is HDR, so Exo can't join them.");
        var biggest = facts.MaxBy(f => (long)f.Width * f.Height)!;
        return new Decision(JoinMode.Reencode, biggest.Width & ~1, biggest.Height & ~1, null);
    }

    /// <summary>
    /// Where the reel goes: its game's folder and "Game reel 2026-10-28" (the day of the first clip), or the Reels folder and
    /// "Reel 2026-10-28" for clips of several games. The add-on adds " (2)" if the name is taken.
    /// </summary>
    internal static (string Folder, string Name) NameFor(IReadOnlyList<Clip> ordered)
    {
        var day = ordered[0].CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var folders = ordered.Select(c => c.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return folders.Count == 1 ? (folders[0], folders[0] + " reel " + day) : (MixedFolder, "Reel " + day);
    }

    /// <summary>The game the reel belongs to when all its clips do; else none.</summary>
    internal static string? GameOf(IReadOnlyList<Clip> ordered)
    {
        var ids = ordered.Select(c => c.GameId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return ids.Count == 1 ? ids[0] : null;
    }

    internal sealed record JoinJob(IReadOnlyList<string> Paths, string Output, JoinMode Mode, int Width, int Height, string? GameId);

    /// <param name="Path">The new file when it was made; else <paramref name="Skipped"/> says why not (busy, unreadable, incompatible, cancelled, failed).</param>
    internal sealed record JoinResult(string? Path, string? Skipped, string? Detail);

    /// <summary>Exo Capture, as the reel needs it (a fake in the tests).</summary>
    internal interface IReelEngine
    {
        /// <summary>One entry per path, in order; null for a clip it cannot read.</summary>
        Task<IReadOnlyList<Facts?>> FactsAsync(IReadOnlyList<string> paths, CancellationToken ct);

        /// <summary>Joins the clips; <paramref name="ct"/> asks it to stop, and it answers "cancelled".</summary>
        Task<JoinResult> JoinAsync(JoinJob job, CancellationToken ct);
    }

    /// <param name="Path">The new reel; null unless <paramref name="Ok"/>.</param>
    /// <param name="Message">What the user is told; null when it worked, or was stopped by them.</param>
    internal sealed record Outcome(bool Ok, string? Path, string? Message, bool Cancelled = false, string? Detail = null);

    /// <summary>
    /// The whole job, over the engine: refuses (without touching a clip) with fewer than
    /// two clips, too many, or not enough room; reads the clips; decides how they join; joins them. A copy the add-on finds
    /// impossible after all is tried again by encoding.
    /// </summary>
    internal static async Task<Outcome> MakeAsync(IReadOnlyList<Clip> clips, string capturesRoot, long freeBytes,
        IReelEngine engine, CancellationToken ct)
    {
        if (clips.Count < 2) return new Outcome(false, null, "Pick at least two clips.");
        if (clips.Count > MostClips) return new Outcome(false, null, $"A reel takes at most {MostClips} clips.");
        var ordered = Order(clips);
        // The reel is about as big as the clips together.
        if (freeBytes < ordered.Sum(c => c.Bytes) + ordered.Sum(c => c.Bytes) / 10)
            return new Outcome(false, null, "There is not enough room on that drive for the reel.");

        var paths = ordered.Select(c => c.Path).ToList();
        IReadOnlyList<Facts?> facts;
        try { facts = await engine.FactsAsync(paths, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Stopped(); }
        catch (InvalidOperationException ex) { return new Outcome(false, null, "Exo couldn't read the clips.", Detail: ex.Message); }
        if (facts.Count != paths.Count || facts.Any(f => f is null)) return new Outcome(false, null, "Exo couldn't read one of the clips.");
        var known = facts.Select(f => f!).ToList();

        var decision = Decide(known);
        if (decision.Refusal is { } refusal) return new Outcome(false, null, refusal);
        var (folder, name) = NameFor(ordered);
        var output = Path.Combine(capturesRoot, folder, name + ".mp4");
        var game = GameOf(ordered);

        var result = await JoinAsync(new JoinJob(paths, output, decision.Mode, decision.Width, decision.Height, game)).ConfigureAwait(false);
        // Clips the add-on finds not of one kind after all (its own check is stricter than the signature's reading of them).
        if (result.Skipped == "incompatible" && decision.Mode == JoinMode.Copy)
        {
            var again = Reencode(known);
            if (again.Refusal is { } stuck) return new Outcome(false, null, stuck);
            result = await JoinAsync(new JoinJob(paths, output, again.Mode, again.Width, again.Height, game)).ConfigureAwait(false);
        }
        if (result.Path is { } made) return new Outcome(true, made, null);
        return result.Skipped switch
        {
            "cancelled" => Stopped(),
            "busy" => new Outcome(false, null, "Exo is already making a reel."),
            "unreadable" => new Outcome(false, null, "Exo couldn't read one of the clips.", Detail: result.Detail),
            _ => new Outcome(false, null, "Exo couldn't join the clips. The details are in the log.", Detail: result.Detail),
        };

        async Task<JoinResult> JoinAsync(JoinJob job)
        {
            try { return await engine.JoinAsync(job, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new JoinResult(null, "cancelled", null); }
            catch (InvalidOperationException ex) { return new JoinResult(null, "failed", ex.Message); }
        }

        static Outcome Stopped() => new(false, null, null, Cancelled: true);
    }
}
