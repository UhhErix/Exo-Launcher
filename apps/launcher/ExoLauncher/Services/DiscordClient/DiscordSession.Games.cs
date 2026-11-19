using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The games you opened from Exo, in launch order: what this session may say you are playing. Told only by the Launcher's
/// events, so it holds a title and a time and never looks at a game's process. A game that was running before Exo knew of
/// it (started from its store, or running across an Exo restart) never gets in, because no launch from Exo told it.
/// </summary>
internal sealed class DiscordGamesOpened
{
    /// <summary>A launcher that hands over to the game can drop out of the run state for a moment: a game stays this long.</summary>
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    internal sealed record Game(string Id, string Title, long StartedMs);

    private readonly Lock _gate = new();
    private readonly List<Game> _opened = [];
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    /// <summary>The game opened last that is still open.</summary>
    internal Game? Current
    {
        get { lock (_gate) return _opened.LastOrDefault(); }
    }

    /// <summary>Exo started it. True when what you are playing changed.</summary>
    internal bool Opened(string id, string title, long nowMs)
    {
        lock (_gate)
        {
            if (_opened.LastOrDefault()?.Id == id) return false;
            _opened.RemoveAll(g => g.Id == id);
            _opened.Add(new Game(id, title, nowMs));
            return true;
        }
    }

    /// <summary>The Launcher's run state, for any game. True when a game Exo opened lost its process: check back after <see cref="StopGrace"/>.</summary>
    internal bool RunState(string id, bool running)
    {
        lock (_gate)
        {
            if (running) { _running.Add(id); return false; }
            _running.Remove(id);
            return _opened.Exists(g => g.Id == id);
        }
    }

    /// <summary>After the grace, or when Exo's watch of it ended: gone unless its process is back. True when what you are playing changed.</summary>
    internal bool CloseUnlessRunning(string id)
    {
        lock (_gate)
        {
            if (_running.Contains(id)) return false;
            var current = _opened.LastOrDefault()?.Id == id;
            return _opened.RemoveAll(g => g.Id == id) > 0 && current;
        }
    }
}

/// <summary>
/// The friend link's first section: what you are playing, as a title and when it started, or nothing. Only ever the title
/// and the time; a store's id for the game stays here. Reads from the wire are bounded: a title a friend's Exo couldn't
/// have sent is dropped, not cleaned up.
/// </summary>
internal static class FriendGame
{
    internal const string Section = "game";
    private const int MostTitleChars = 128;
    private static readonly long AheadMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds, OldestMs = (long)TimeSpan.FromDays(7).TotalMilliseconds;

    internal static JsonObject? Write(DiscordGamesOpened.Game? game)
    {
        if (game is null) return null;
        var title = new string([.. game.Title.Where(c => !IsRefused(c))]).Trim();
        if (title.Length > MostTitleChars) title = title[..(char.IsHighSurrogate(title[MostTitleChars - 1]) ? MostTitleChars - 1 : MostTitleChars)];
        return title.Length == 0 ? null : new JsonObject { ["title"] = title, ["start"] = game.StartedMs };
    }

    /// <summary>The game in a friend's message; null when there is none, or it isn't one Exo would send.</summary>
    internal static DiscordCache.ExoGame? Read(FriendLinkFormat.Message? message, long nowMs)
    {
        if (message?.Sections.GetValueOrDefault(Section) is not { ValueKind: JsonValueKind.Object } game) return null;
        try
        {
            if (!game.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() is not { Length: > 0 and <= MostTitleChars } title) return null;
            if (title.Any(IsRefused) || string.IsNullOrWhiteSpace(title)) return null;
            if (!game.TryGetProperty("start", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out var start)) return null;
            return start > nowMs + AheadMs || start < nowMs - OldestMs ? null : new(title, start);
        }
        catch (InvalidOperationException) { return null; } // a lone surrogate in the text
    }

    // What has no place in a title, and could rearrange or hide what is around it on screen: control characters and bidi overrides.
    private static bool IsRefused(char c) => char.IsControl(c) || c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩';
}

/// <summary>
/// Share my games: the signed-in person's Exo tells their friends' Exo apps which game they opened from Exo, over the
/// friend link (DiscordSession.FriendLinks.cs). Discord and its apps never hear of it. Always on (owner, 3.26.0: not a
/// switch); a game you hid or added by hand is never named, and Exo leaves the account's own Share my activity setting alone.
/// </summary>
internal sealed partial class DiscordSession
{
    private static readonly DiscordGamesOpened GamesOpened = new();

    // Each game Exo starts or sees end, for whichever session is signed in by then. Reads the Launcher's events and Exo's
    // own settings only: no process, window or file of a game is looked at, for any game, anti-cheat or Riot's included.
    internal static void WatchGames(LaunchOrchestrator launcher, SettingsService settings, LibraryService library)
    {
        launcher.GameLaunched += game =>
        {
            if (!MayShare(game.Store, IsHidden(game.Id, settings.Shared.HiddenGames, library))) return;
            if (GamesOpened.Opened(game.Id, game.Title, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) PublishGame();
        };
        launcher.GameRunStateChanged += (gameId, state) =>
        {
            if (!GamesOpened.RunState(gameId, state.IsRunning)) return;
            _ = Task.Delay(DiscordGamesOpened.StopGrace).ContinueWith(_ => { if (GamesOpened.CloseUnlessRunning(gameId)) PublishGame(); }, TaskScheduler.Default);
        };
        launcher.GameSessionCompleted += game => { if (GamesOpened.CloseUnlessRunning(game.Id)) PublishGame(); };
    }

    /// <summary>
    /// A game you hid from your Library is never named, nor one you added yourself: its title comes from a file on this PC,
    /// which may say anything, not from a store.
    /// </summary>
    internal static bool MayShare(StoreKind store, bool hidden) => !hidden && store != StoreKind.Local;

    private static bool IsHidden(string gameId, List<string> hidden, LibraryService library) =>
        hidden.Contains(gameId, StringComparer.OrdinalIgnoreCase)
        || (library.FindVisualCard(gameId)?.Id is { } card && hidden.Contains(card, StringComparer.OrdinalIgnoreCase));

    private static void PublishGame()
    {
        if (Current is not { } session) return;
        lock (session._linkGate) session._links?.Set(FriendGame.Section, FriendGame.Write(GamesOpened.Current));
    }

    /// <summary>What this account pinned to the room's top bar, on this PC.</summary>
    internal IReadOnlyList<string> BandPins => Cache.Me?.Id is { } id && App.Services.Settings.Shared.DiscordBandPins.TryGetValue(id, out var pins) ? pins : [];

    /// <summary>Pins for the account signed in, in Exo's settings (the room's only other copy is on screen).</summary>
    internal IReadOnlyList<string> SetBandPins(IEnumerable<string?> pins)
    {
        if (Cache.Me?.Id is not { } id) return [];
        var list = SettingsService.NormalizeDiscordBandPinList(pins);
        App.Services.Settings.UpdateProfile(s =>
        {
            if (list.Count == 0) s.DiscordBandPins.Remove(id);
            else s.DiscordBandPins[id] = list;
        });
        return list;
    }

}
