using ExoLauncher.Services.DiscordClient.Voice;
using Song = ExoLauncher.Services.DiscordClient.CallRelayFormat.Song;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Call songs: in a call with DAVE on, the Exo apps in it tell each other what their Music room plays through Exo's
/// call relay (<see cref="CallSongs"/>; relay/README.md), sealed under a key from the call's own MLS group, so Listen
/// along and the Jam box can follow someone's Exo music while Discord is never told it. The page gets each member's song
/// as a Listening activity (discord.callSong, discord.callSongs), the shape of a friend's presence; yours goes only to
/// the relay, for the people in your call. A call's <see cref="CallSongs"/> starts with its first secret and ends with it.
/// </summary>
internal sealed partial class DiscordSession
{
    private readonly Lock _songGate = new();
    private CallSongs? _callSongs;
    private int _secretTurn;
    // Your song, and the tick its position was read, kept between calls for the next one.
    private (Song? Song, long At) _mine;

    /// <summary>From the call's connection, under DAVE's lock: taken on the pool, the newest secret winning.</summary>
    private void OnCallSecret(VoiceConnection call, byte[]? secret)
    {
        var turn = Interlocked.Increment(ref _secretTurn);
        _ = Task.Run(() =>
        {
            CallSongs songs;
            lock (_songGate)
            {
                if (turn != Volatile.Read(ref _secretTurn) || !ReferenceEquals(call, _voice) || Cache.Me is not { } me) return;
                if (_callSongs is null)
                {
                    if (secret is null) return;
                    _callSongs = songs = new CallSongs(me.Id, CallRelayClient.Endpoint, user => _voice?.InCall(user) == true);
                    songs.Changed += (user, heard) => Event?.Invoke("discord.callSong", CallSongView(user, heard));
                    songs.SetMine(_mine.Song, _mine.At);
                }
                else songs = _callSongs;
            }
            songs.UseSecret(secret);
        });
    }

    /// <summary>The call ended: your song is cleared on the relay, and everyone's here.</summary>
    private void StopCallSongs()
    {
        CallSongs? songs;
        lock (_songGate)
        {
            Interlocked.Increment(ref _secretTurn);
            songs = _callSongs;
            _callSongs = null;
        }
        if (songs is not null) _ = songs.StopAsync();
    }

    private void ForgetCallSong(string user) => _callSongs?.Forget(user);

    /// <summary>What your Music room plays (null: nothing), with the tick its position was read.</summary>
    internal void SetCallSong(Song? song, long at)
    {
        CallSongs? songs;
        lock (_songGate)
        {
            _mine = (song, at);
            songs = _callSongs;
        }
        songs?.SetMine(song, at);
    }

    /// <summary>Everyone in your call with a song in Exo, for a page that opens mid-call.</summary>
    internal object CallSongList() =>
        new { songs = (_callSongs?.Current() ?? []).Select(h => CallSongView(h.User, h.Heard)).ToArray() };

    // As the page reads a friend's presence: a Listening activity (type 2) whose start puts the song where it is now on
    // this PC's clock; paused, no activity, as Discord shows a paused song.
    private object CallSongView(string user, CallSongs.Heard? heard)
    {
        var (name, avatar) = Cache.Person(user);
        if (heard is not { Song: var song }) return new { userId = user, name, avatar, rich = (object?)null, paused = false };
        if (!song.Playing) return new { userId = user, name, avatar, rich = (object?)null, paused = true };
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - song.PositionMs - (Environment.TickCount64 - heard.At);
        return new
        {
            userId = user,
            name,
            avatar,
            rich = new
            {
                type = 2,
                name = Helpers.MusicWebPolicy.Label(song.Service),
                details = song.Title,
                state = string.Join("; ", song.Artists),
                album = song.Album,
                start,
                end = song.DurationMs is { } ms ? start + ms : (long?)null,
                syncId = song.Service == "spotify" && song.Id is { } id ? id["spotify:track:".Length..] : null,
                uri = song.Id,
            },
            paused = false,
        };
    }

    /// <summary>A song as the Music room's page names it, in the relay's form (its id only where the service has one).</summary>
    internal static Song? CallSongOf(string service, string status, string title, string artist, long? positionMs, long? durationMs, string? trackId)
    {
        if (status is not ("playing" or "paused") || string.IsNullOrWhiteSpace(title) || !CallRelayFormat.Services.Contains(service)) return null;
        var id = service switch
        {
            "spotify" => trackId,
            "youtube" => trackId is { Length: 11 } video ? "ytm:track:" + video : null,
            "apple" => trackId is { Length: > 0 } song && song.All(char.IsAsciiDigit) ? "am:songs:" + song : null,
            _ => null,
        };
        var length = durationMs is > 0 and <= CallRelayFormat.MaxMs ? durationMs : null;
        var position = Math.Clamp(positionMs ?? 0, 0, length ?? CallRelayFormat.MaxMs);
        return new(title, [artist], null, length, position, status == "playing", service, CallRelayFormat.Valid(service, id) ? id : null);
    }
}
