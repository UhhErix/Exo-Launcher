using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// compress.json: each game's compression on this PC (how it was compressed, when, what it saved, the extensions its
/// sample found poor, the player's yes for a game with anti-cheat) and its last estimate. A job running when Exo closed
/// reads back as paused: compressing it again skips every file already in its algorithm, so it goes on where it stopped.
/// </summary>
internal static class GameCompressionStore
{
    /// <summary>A game's status: compressing or uncompressing now, paused (by the player or by Exo closing), compressed, or kept (the player put it back: idle leaves it).</summary>
    internal static class Status
    {
        internal const string Compressing = "compressing";
        internal const string Uncompressing = "uncompressing";
        internal const string Paused = "paused";
        internal const string Compressed = "compressed";
        internal const string Kept = "kept";
    }

    internal sealed record Estimate(long Before, long After, bool Sampled, string Stamp, DateTime At);

    internal sealed record Game
    {
        public string? Status { get; init; }
        public string? Algorithm { get; init; }
        public DateTime? CompressedUtc { get; init; }
        public long Before { get; init; }
        public long After { get; init; }
        public List<string> Poor { get; init; } = [];
        /// <summary>The anti-cheat set the player allowed compression for (<see cref="AntiCheatCheck.Answer.Label"/>).</summary>
        public string? Consent { get; init; }
        public Estimate? Estimate { get; init; }
    }

    internal sealed record Saved
    {
        public int Version { get; init; } = 1;
        public Dictionary<string, Game> Games { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The file read back: a job left running is paused; a file that isn't ours is empty.</summary>
    internal static Saved Parse(string? json)
    {
        Saved? saved = null;
        try { saved = json is null ? null : JsonSerializer.Deserialize<Saved>(json); }
        catch (JsonException) { }
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, game) in saved?.Games ?? [])
        {
            if (game is null || string.IsNullOrWhiteSpace(id)) continue;
            games[id] = game.Status is Status.Compressing ? game with { Status = Status.Paused }
                // Putting files back stopped halfway: the next look says what's left; nothing to resume.
                : game.Status is Status.Uncompressing ? game with { Status = null }
                : game with { Poor = game.Poor ?? [] };
        }
        return new Saved { Games = games };
    }

    internal static string Serialize(Saved saved) => JsonSerializer.Serialize(saved);

    private static string FilePath => Path.Combine(PathHelper.AppDataDir, "compress.json");
    private static readonly Lock Gate = new();
    private static Saved? _saved;

    internal static Saved Load()
    {
        lock (Gate)
        {
            if (_saved is not null) return _saved;
            string? text = null;
            try { if (File.Exists(FilePath)) text = File.ReadAllText(FilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Compress: compress.json unread: " + ex.Message); }
            return _saved = Parse(text);
        }
    }

    internal static Game Of(string id) => Load().Games.GetValueOrDefault(id) ?? new Game();

    /// <summary>Changes one game's record and writes the file (whole, then moved over the old one).</summary>
    internal static Game Update(string id, Func<Game, Game> change)
    {
        lock (Gate)
        {
            var saved = Load();
            var next = change(saved.Games.GetValueOrDefault(id) ?? new Game());
            saved.Games[id] = next;
            try
            {
                File.WriteAllText(FilePath + ".tmp", Serialize(saved));
                File.Move(FilePath + ".tmp", FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Compress: compress.json unsaved: " + ex.Message); }
            return next;
        }
    }
}
