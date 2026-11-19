using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Models;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The friend link's second section, "prep": what this Exo is doing to get a game ready to play (installing it, building
/// its shaders) or that it is ready, as a title, a kind and for an install a percent in steps of ten. It is what lets a
/// Party say who is still compiling. Only ever a title Exo already shares as the game you play (<see cref="DiscordSession.MayShare"/>),
/// and only for people who turned Share when I'm getting ready on, which is off until they do.
///
/// How it can fail, and what stops it:
/// - A percent every few hundred milliseconds floods the relay: it is cut to steps of ten, and an unchanged section is not sent again.
/// - A stale "ready" is shown long after: it expires <see cref="ReadyForMs"/> after it began, and launching the game ends it.
/// - Installing a game you hid, or one you added by hand, names it to friends: <see cref="DiscordSession.MayShare"/> decides before anything is tracked.
/// - A friend's Exo sends a title with control characters, a kind we don't know or a percent outside 0 to 100: the whole section is dropped, not cleaned up.
/// - A failed or cancelled install stays on as "installing": both end it, as does Idle.
/// - Two things at once (a download while a compile runs): compiling is said first, then installing, then ready, the latest of each.
/// - A message passes its fixed size and is sent by nobody (<see cref="FriendLinkFormat.Seal"/> returns null, and the game goes with it): a title of
///   emoji takes twelve bytes a character as JSON, so with the game's title beside it this one is cut to <see cref="MostTitleBytes"/> once written.
/// </summary>
internal static class FriendPrep
{
    internal const string Section = "prep";
    internal const string Installing = "installing", Compiling = "compiling", Ready = "ready";
    private const int MostTitleChars = 128;
    /// <summary>The most a title may take as JSON on the wire; the game's title (at most 768), this and the envelope stay under <see cref="FriendLinkFormat.PlainBytes"/>.</summary>
    private const int MostTitleBytes = 400;
    private static readonly JsonSerializerOptions Escaping = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>What is said: the game's title, the kind, and for an install how far (0 to 100, in tens).</summary>
    internal sealed record State(string Title, string Kind, int? Percent);

    internal static JsonObject? Write(State? state)
    {
        if (state is null) return null;
        var title = Clean(state.Title);
        if (title.Length == 0 || state.Kind is not (Installing or Compiling or Ready)) return null;
        var body = new JsonObject { ["title"] = title, ["kind"] = state.Kind };
        if (state.Kind == Installing && state.Percent is { } percent) body["pct"] = Math.Clamp(percent, 0, 100) / 10 * 10;
        return body;
    }

    /// <summary>What a friend's message says about getting ready; null when nothing, or it isn't what Exo would send.</summary>
    internal static DiscordCache.ExoPrep? Read(FriendLinkFormat.Message? message)
    {
        if (message?.Sections.GetValueOrDefault(Section) is not { ValueKind: JsonValueKind.Object } prep) return null;
        try
        {
            if (!prep.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() is not { Length: > 0 and <= MostTitleChars } title) return null;
            if (title.Any(IsRefused) || string.IsNullOrWhiteSpace(title)) return null;
            if (!prep.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String) return null;
            var kind = k.GetString();
            if (kind is not (Installing or Compiling or Ready)) return null;
            int? percent = null;
            if (prep.TryGetProperty("pct", out var p))
            {
                if (kind != Installing || p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var value) || value is < 0 or > 100) return null;
                percent = value;
            }
            return new(title, kind, percent);
        }
        catch (InvalidOperationException) { return null; } // a lone surrogate in the text
    }

    private static string Clean(string title)
    {
        var clean = new string([.. title.Where(c => !IsRefused(c))]).Trim();
        if (clean.Length > MostTitleChars) clean = clean[..(char.IsHighSurrogate(clean[MostTitleChars - 1]) ? MostTitleChars - 1 : MostTitleChars)];
        while (clean.Length > 0 && JsonSerializer.SerializeToUtf8Bytes(clean, Escaping).Length - 2 > MostTitleBytes)
            clean = clean[..(clean.Length - (clean.Length > 1 && char.IsLowSurrogate(clean[^1]) ? 2 : 1))];
        return clean.Trim();
    }

    // What has no place in a title, and could rearrange or hide what is around it on screen: control characters and bidi overrides.
    private static bool IsRefused(char c) => char.IsControl(c) || c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩';
}

/// <summary>
/// What this Exo is getting ready, by game, for <see cref="FriendPrep"/>. Told only by the Launcher's install events and
/// Prepare's own state, so it holds a title, a kind and a time and never looks at a game's process or files.
/// </summary>
internal sealed class DiscordPrep
{
    /// <summary>How long "ready" is said before it goes by itself.</summary>
    internal const long ReadyForMs = 30 * 60 * 1000;

    private sealed record Item(string Title, string Kind, int? Percent, long At);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);

    /// <summary>What to say now: compiling, then installing, then ready, the latest of its kind; null when there is nothing.</summary>
    internal FriendPrep.State? Current(long nowMs)
    {
        lock (_gate) return CurrentLocked(nowMs);
    }

    /// <summary>A game's install moved. <paramref name="resolve"/> is asked only when this changes what is said: the title, and whether it may be named at all (null: not shared). True when what is said changed.</summary>
    internal bool Install(string id, InstallPhase phase, double? percent, Func<(string Title, bool May)?> resolve, long nowMs)
    {
        lock (_gate)
        {
            var before = CurrentLocked(nowMs);
            switch (phase)
            {
                case InstallPhase.Preparing or InstallPhase.Downloading or InstallPhase.Installing or InstallPhase.Finalizing:
                    var step = percent is { } p ? (int)Math.Clamp(p, 0, 100) / 10 * 10 : (int?)null;
                    if (_items.TryGetValue(id, out var had) && had.Kind == FriendPrep.Installing && had.Percent == step) return false;
                    if (resolve() is not ({ } title, true)) { _items.Remove(id); break; }
                    _items[id] = new(title, FriendPrep.Installing, step, nowMs);
                    break;
                case InstallPhase.Completed:
                    if (_items.TryGetValue(id, out var done) && done.Kind == FriendPrep.Installing) _items[id] = done with { Kind = FriendPrep.Ready, Percent = null, At = nowMs };
                    break;
                default: // Idle, Failed, Cancelled
                    if (_items.TryGetValue(id, out var stopped) && stopped.Kind == FriendPrep.Installing) _items.Remove(id);
                    break;
            }
            return before != CurrentLocked(nowMs);
        }
    }

    /// <summary>Prepare's state for a game: running, or just ended with its shaders built (<paramref name="prepared"/>), or neither. True when what is said changed.</summary>
    internal bool Compile(string id, bool running, bool prepared, Func<(string Title, bool May)?> resolve, long nowMs)
    {
        lock (_gate)
        {
            var before = CurrentLocked(nowMs);
            var had = _items.GetValueOrDefault(id);
            if (running)
            {
                if (had?.Kind == FriendPrep.Compiling) return false;
                if (resolve() is ({ } title, true)) _items[id] = new(title, FriendPrep.Compiling, null, nowMs); else _items.Remove(id);
            }
            else if (had?.Kind == FriendPrep.Compiling)
            {
                if (prepared) _items[id] = had with { Kind = FriendPrep.Ready, At = nowMs };
                else _items.Remove(id);
            }
            return before != CurrentLocked(nowMs);
        }
    }

    /// <summary>The game started: it is being played, which the game section says.</summary>
    internal bool Launched(string id)
    {
        lock (_gate) return _items.Remove(id);
    }

    /// <summary>Drops a "ready" that has been said long enough. True when one went, so what is said is worked out again (<see cref="Current"/> never shows an expired one, whatever the timer).</summary>
    internal bool Expire(long nowMs)
    {
        lock (_gate)
        {
            var gone = _items.Where(i => i.Value.Kind == FriendPrep.Ready && nowMs - i.Value.At >= ReadyForMs).Select(i => i.Key).ToList();
            foreach (var id in gone) _items.Remove(id);
            return gone.Count > 0;
        }
    }

    /// <summary>How long until the first "ready" runs out, or null when none is waiting: what a timer is set for, so a later change never pushes an earlier expiry out.</summary>
    internal long? NextExpiryIn(long nowMs)
    {
        lock (_gate)
        {
            var left = _items.Values.Where(i => i.Kind == FriendPrep.Ready).Select(i => i.At + ReadyForMs - nowMs).ToList();
            return left.Count == 0 ? null : Math.Max(0, left.Min());
        }
    }

    /// <summary>Nothing is said any more (Share when I'm getting ready went off, or Discord signed out).</summary>
    internal void Clear()
    {
        lock (_gate) _items.Clear();
    }

    private FriendPrep.State? CurrentLocked(long nowMs)
    {
        static int Rank(string kind) => kind == FriendPrep.Compiling ? 0 : kind == FriendPrep.Installing ? 1 : 2;
        var item = _items.Values.Where(i => i.Kind != FriendPrep.Ready || nowMs - i.At < ReadyForMs)
            .OrderBy(i => Rank(i.Kind)).ThenByDescending(i => i.At).FirstOrDefault();
        return item is null ? null : new(item.Title, item.Kind, item.Percent);
    }
}
