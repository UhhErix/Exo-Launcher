using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;
using Proc = ExoLauncher.Services.DiscordClient.Voice.StreamAudio.Proc;

namespace ExoLauncher.Tests;

/// <summary>
/// Go Live's screen sound (StreamAudio, CallEcho) and a stream's sound counts. Ways it could fail silently: an app Exo
/// runs under (File Explorer, Steam, a launcher) is captured with its whole tree, which holds Exo and so every voice of the
/// call; that app "covers" its other apps so they go uncaptured once it is left out; a reused parent id makes a stranger
/// Exo's parent (or Exo's real parent a stranger's child); an app under Discord or a mixer slips in; an app whose capture
/// failed still covers its children, so a game under it is never heard; Exo's own WebView2 or a game Exo started is left
/// out; a window of an app Exo runs under is captured whole. The name list matches too much (a prefix where an exact
/// name was meant) or drifts from Exo Capture's copy. The call detector misses the call copied into a game's sound, or
/// takes music, a game or someone else's voice for it and mutes a game; it never lets an app back once the call is gone
/// from it. A watched stream's counts misread silence as sound or a muted stream as playing.
/// </summary>
public sealed class StreamSoundTests
{
    private const uint Explorer = 5156, Exo = 12956, Steam = 7000, Game = 7100, Chrome = 8000, WebView = 13000, WebAudio = 13050,
        ExoGame = 14000, Discord = 9000, Spotify = 9500, Services = 700;

    private static Dictionary<uint, Proc> Table(params (uint Pid, uint Parent, string Name)[] rows) =>
        rows.ToDictionary(r => r.Pid, r => new Proc(r.Parent, r.Name));

    private static readonly Func<uint, uint, bool> NoneReused = (_, _) => false;

    private static StreamAudio.Plan Decide(Dictionary<uint, Proc> table, uint[] playing, Func<uint, uint, bool>? reused = null, uint[]? failed = null, uint root = 0) =>
        StreamAudio.Decide(playing.ToHashSet(), table, Exo, reused ?? NoneReused, (failed ?? []).ToHashSet(), root);

    // Explorer started Exo (the Start menu, the desktop) and plays a sound itself.
    private static Dictionary<uint, Proc> ExplorerRunsExo() => Table(
        (Explorer, 5100, "explorer"), (Exo, Explorer, "exolauncher"), (Chrome, Explorer, "chrome"), (Chrome + 1, Chrome, "chrome"),
        (WebView, Exo, "msedgewebview2"), (WebAudio, WebView, "msedgewebview2"), (ExoGame, Exo, "eldenring"), (Services, 4, "services"),
        (Spotify, Services, "spotify"));

    [Fact]
    public void AppsExoRunsUnderAreLeftOutAndCoverNothing()
    {
        var plan = Decide(ExplorerRunsExo(), [Explorer, Exo, Chrome + 1, WebAudio, ExoGame]);
        Assert.Equal(new[] { "chrome", "msedgewebview2 (Exo's)", "eldenring (Exo's)" }, plan.Take.OrderBy(t => t.Key).Select(t => t.Value));
        Assert.Empty(plan.With);
        Assert.Contains(plan.Left, l => l.Pid == Explorer && l.Why == "Exo runs under it");
        Assert.Contains(plan.Left, l => l.Pid == Exo && l.Why == "Exo itself");
        Assert.DoesNotContain(Explorer, plan.Take.Keys);
        Assert.Equal("Discord video: stream sound from chrome, msedgewebview2 (Exo's), eldenring (Exo's); leaves out explorer (Exo runs under it), exolauncher (Exo itself).",
            StreamAudio.Describe(plan, [Chrome + 1, WebAudio, ExoGame]));
    }

    [Fact]
    public void ALauncherThatStartedExoIsLeftOutAndItsGamesTakenOnTheirOwn()
    {
        // Steam started Exo (a non-Steam game shortcut) and also a game; Steam's tree holds Exo.
        var table = Table((Explorer, 5100, "explorer"), (Steam, Explorer, "steam"), (Exo, Steam, "exolauncher"), (Game, Steam, "cs2"),
            (Steam + 1, Steam, "steamwebhelper"));
        var plan = Decide(table, [Steam, Game, Steam + 1, Exo]);
        Assert.Equal(new[] { "steamwebhelper", "cs2" }, plan.Take.OrderBy(t => t.Key).Select(t => t.Value));
        Assert.Contains(plan.Left, l => l.Pid == Steam && l.Why == "Exo runs under it");
        Assert.Contains(StreamAudio.Lineage(table, Exo, NoneReused), pid => pid == Explorer);
    }

    [Fact]
    public void ALauncherOutsideExosLineHoldsItsGame()
    {
        var table = Table((Explorer, 5100, "explorer"), (Exo, Explorer, "exolauncher"), (Steam, Explorer, "steam"), (Game, Steam, "cs2"));
        var plan = Decide(table, [Steam, Game]);
        Assert.Equal(new[] { "steam" }, plan.Take.Values);
        Assert.Equal(Steam, plan.With[Game]);
        Assert.Equal("Discord video: stream sound from steam (with cs2).", StreamAudio.Describe(plan, [Steam]));
    }

    [Fact]
    public void AppsUnderAnEchoAppAreLeftOut()
    {
        var table = Table((Explorer, 5100, "explorer"), (Exo, Explorer, "exolauncher"), (Discord, Explorer, "discord"), (Discord + 1, Discord, "discord"),
            (Game, Discord, "minecraft"), (Spotify, Explorer, "voicemeeter8x64"));
        var plan = Decide(table, [Discord + 1, Game, Spotify]);
        Assert.Empty(plan.Take);
        Assert.Contains(plan.Left, l => l.Pid == Game && l.Why == "started by discord");
        Assert.Contains(plan.Left, l => l.Pid == Discord + 1 && l.Why == "it plays the call or a mic");
        Assert.Contains(plan.Left, l => l.Pid == Spotify && l.Why == "it plays the call or a mic");
    }

    [Fact]
    public void AReusedParentIdIsNoParent()
    {
        // Exo's real parent is gone and its number now belongs to a newer app that plays: not an app Exo runs under.
        var table = Table((Explorer, 5100, "explorer"), (Exo, 4242, "exolauncher"), (4242, Explorer, "vlc"), (Game, 4242, "game"));
        bool Reused(uint child, uint parent) => child == Exo && parent == 4242;
        var plan = Decide(table, [4242, Game], Reused);
        Assert.Equal(Exo, Assert.Single(StreamAudio.Lineage(table, Exo, Reused)));
        Assert.Equal(new[] { "vlc" }, plan.Take.Values);
        Assert.Equal(4242u, plan.With[Game]);
        // Read as a parent, it is left out: a capture of its tree would hold Exo.
        Assert.Contains(Decide(table, [4242, Game]).Left, l => l.Pid == 4242 && l.Why == "Exo runs under it");
    }

    [Fact]
    public void AnAppWhoseCaptureFailedCoversNothing()
    {
        var table = Table((Explorer, 5100, "explorer"), (Exo, Explorer, "exolauncher"), (Steam, Explorer, "steam"), (Game, Steam, "cs2"));
        var plan = Decide(table, [Steam, Game], failed: [Steam]);
        Assert.Equal(new[] { "steam", "cs2" }, plan.Take.Values);
        Assert.Empty(plan.With);
    }

    [Fact]
    public void AWindowOfAnAppExoRunsUnderTakesItsOtherAppsOnly()
    {
        var plan = Decide(ExplorerRunsExo(), [Explorer, Exo, Chrome + 1, WebAudio, ExoGame, Spotify], root: Explorer);
        Assert.Equal(new[] { "chrome", "msedgewebview2 (Exo's)", "eldenring (Exo's)" }, plan.Take.OrderBy(t => t.Key).Select(t => t.Value));
        Assert.DoesNotContain(Spotify, plan.Take.Keys);
    }

    [Fact]
    public void LineageStopsAtAMissingParentAndALoop()
    {
        Assert.Equal(new[] { Exo, Explorer }, StreamAudio.Lineage(Table((Explorer, 5100, "explorer"), (Exo, Explorer, "exolauncher")), Exo, NoneReused));
        Assert.Equal(new uint[] { Exo, 1 }, StreamAudio.Lineage(Table((Exo, 1, "exolauncher"), (1, Exo, "loop")), Exo, NoneReused));
    }

    [Theory]
    [InlineData("discord", true), InlineData("discordcanary", true), InlineData("voicemeeter8x64", true), InlineData("voicemeeterpro_x64", true)]
    [InlineData("ms-teams", true), InlineData("ms-teams_modulehost", true), InlineData("ts3client_win64", true), InlineData("teamspeak", true)]
    [InlineData("nvidia broadcast", true), InlineData("nvidia broadcast ui", true), InlineData("lghub_agent", true), InlineData("streamlabs desktop", true)]
    [InlineData("morphvoxpro", true), InlineData("audiorepeater_ks", true), InlineData("signal", true), InlineData("krisp", true)]
    [InlineData("steam", false), InlineData("chrome", false), InlineData("msedgewebview2", false), InlineData("spotify", false), InlineData("obs", false)]
    [InlineData("signalrgb", false), InlineData("lghub_updater", false), InlineData("teamspeak3", false), InlineData("zoomit", false), InlineData("krisp-helper", false)]
    public void EchoNamesMatchWhatTheySay(string exe, bool echoes) => Assert.Equal(echoes, StreamAudio.Echoes(exe));

    [Fact]
    public void EchoNamesMatchExoCapturesCopy()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "capture", "src", "audio.cpp"))) dir = dir.Parent;
        // Exo Capture's source lives in its own repository; where it isn't beside the launcher there is nothing to compare.
        Assert.SkipUnless(dir is not null, "Exo Capture's source (capture/src/audio.cpp) is not in this checkout.");
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "capture", "src", "audio.cpp"));
        var start = source.IndexOf("EchoNames[] = {", StringComparison.Ordinal);
        var list = source[start..source.IndexOf("};", start, StringComparison.Ordinal)];
        var names = System.Text.RegularExpressions.Regex.Matches(list, "L\"([^\"]+)\"").Select(m => m.Groups[1].Value);
        Assert.Equal(StreamAudio.EchoNames, names);
    }

    // ---- the call heard in an app's sound ----

    private const int Rate = 48000;

    // A voice-like sound: syllables of a few harmonics, 120-300 ms each with short gaps, in phrases of 1-4 s with pauses.
    private static float[] Voice(int seed, int seconds)
    {
        var rng = new Random(seed);
        var x = new float[seconds * Rate];
        for (var at = 0; at < x.Length; at += rng.Next(Rate * 4 / 10, Rate * 2))
            for (var end = at + rng.Next(Rate, Rate * 4); at < end && at < x.Length; at += rng.Next(Rate / 25, Rate * 15 / 100))
            {
                var length = rng.Next(Rate * 12 / 100, Rate * 3 / 10);
                double f0 = 110 + rng.NextDouble() * 80, amp = 0.1 + rng.NextDouble() * 0.2;
                for (var i = 0; i < length && at + i < x.Length; i++)
                {
                    var t = (double)i / Rate;
                    x[at + i] = (float)(amp * Math.Sin(Math.PI * i / length) * (Math.Sin(2 * Math.PI * f0 * t) + 0.5 * Math.Sin(4 * Math.PI * f0 * t) + 0.3 * Math.Sin(6 * Math.PI * f0 * t)));
                }
                at += length;
            }
        return x;
    }

    // A game: a steady bed of noise, explosions now and then, footsteps in runs.
    private static float[] GameSound(int seed, int seconds)
    {
        var rng = new Random(seed);
        var x = new float[seconds * Rate];
        for (var i = 0; i < x.Length; i++) x[i] = (float)((rng.NextDouble() * 2 - 1) * 0.06);
        for (var at = rng.Next(Rate); at < x.Length; at += rng.Next(Rate / 2, Rate * 2))
        {
            double amp = 0.2 + rng.NextDouble() * 0.4, decay = Rate * (0.05 + rng.NextDouble() * 0.25);
            for (var i = 0; i < Rate && at + i < x.Length; i++) x[at + i] += (float)((rng.NextDouble() * 2 - 1) * amp * Math.Exp(-i / decay));
        }
        for (var at = 0; at < x.Length; at += Rate * 3)
            for (var step = at; step < at + Rate * 2 && step < x.Length; step += Rate * 2 / 5)
                for (var i = 0; i < Rate / 50 && step + i < x.Length; i++) x[step + i] += (float)((rng.NextDouble() * 2 - 1) * 0.15);
        return x;
    }

    // Music: a chord that changes every bar of 2 s over a kick on every beat (120 BPM).
    private static float[] Music(int seed, int seconds)
    {
        var rng = new Random(seed);
        var x = new float[seconds * Rate];
        double[] chord = [220, 277, 330];
        for (var i = 0; i < x.Length; i++)
        {
            if (i % (Rate * 2) == 0) chord = [.. chord.Select(_ => 196 + rng.NextDouble() * 200)];
            var t = (double)i / Rate;
            var beat = i % (Rate / 2);
            x[i] = (float)(0.05 * chord.Sum(f => Math.Sin(2 * Math.PI * f * t)) + 0.3 * Math.Exp(-beat / (Rate * 0.06)) * Math.Sin(2 * Math.PI * 55 * t));
        }
        return x;
    }

    private static float[] Mix(float[] a, float[] b, float bGain = 1, int bLagMs = 0)
    {
        var y = (float[])a.Clone();
        var lag = bLagMs * Rate / 1000;
        for (var i = lag; i < y.Length; i++) y[i] += b[i - lag] * bGain;
        return y;
    }

    private static float Rms(float[] x) => (float)Math.Sqrt(x.Average(v => (double)v * v));

    // The app's sound against the call (as Exo plays it, or as your mic), fed in time order as 20 ms stereo frames and
    // checked every 250 ms: when EchoWatch first said it carries the call (seconds in), and when it took that back.
    private static (double? Carried, double? Cleared) Watch(float[] call, float[] app, bool mic = false)
    {
        var heard = new Envelope();
        var said = new Envelope();
        var sound = new Envelope();
        var start = 1_000_000_000_000L;
        var watch = new EchoWatch(start / CallEcho.BlockTicks);
        var frame = new float[1920];
        double? carried = null, cleared = null;
        for (var f = 0; f * 960 < app.Length; f++)
        {
            var at = start + f * 200_000L;
            Feed(mic ? said : heard, call, f, at, frame);
            (mic ? heard : said).Quiet(960, at);
            Feed(sound, app, f, at, frame);
            if (f % 12 != 0 || !watch.Check(sound, heard, said, start / CallEcho.BlockTicks + 2L * (f + 1))) continue;
            if (watch.Carrying) carried ??= f / 50.0;
            else cleared ??= f / 50.0;
        }
        return (carried, cleared);
    }

    private static void Feed(Envelope envelope, float[] mono, int f, long at, float[] frame)
    {
        for (var i = 0; i < 960; i++) frame[2 * i] = frame[2 * i + 1] = f * 960 + i < mono.Length ? mono[f * 960 + i] : 0;
        envelope.Add(frame, at);
    }

    [Theory]
    [InlineData(false, 0), InlineData(false, 120), InlineData(false, 380), InlineData(true, 40), InlineData(true, 250)]
    public void FindsTheCallCopiedIntoAGame(bool mic, int lagMs)
    {
        var call = Voice(1, 40);
        var game = GameSound(2, 40);
        var app = Mix(game, call, Rms(game) / Rms(call) * 1.4f, lagMs); // the call as loud as the game, a little over
        var (carried, _) = Watch(call, app, mic);
        Assert.NotNull(carried);
        Assert.InRange(carried!.Value, 5, 20);
    }

    [Fact]
    public void FindsAMicMonitorPlayingYourVoiceAlone()
    {
        var voice = Voice(3, 30);
        var (carried, _) = Watch(voice, Mix(new float[voice.Length], voice, 0.5f, 30), mic: true);
        Assert.InRange(carried!.Value, 5, 15);
    }

    [Theory]
    [InlineData(10), InlineData(11), InlineData(12), InlineData(13)]
    public void NeverTakesMusicGamesOrSomeoneElsesVoiceForTheCall(int seed)
    {
        var call = Voice(seed, 90);
        foreach (var app in new[] { Music(seed + 100, 90), GameSound(seed + 200, 90), Voice(seed + 300, 90), Mix(Music(seed + 400, 90), Voice(seed + 500, 90), 0.7f) })
        {
            Assert.Null(Watch(call, app).Carried);
            Assert.Null(Watch(call, app, mic: true).Carried);
        }
    }

    [Fact]
    public void LetsTheAppBackOnceTheCallIsGoneFromIt()
    {
        var call = Voice(4, 60);
        var game = GameSound(5, 60);
        var copy = (float[])call.Clone();
        Array.Clear(copy, 20 * Rate, copy.Length - 20 * Rate); // the app stops playing the call after 20 s
        var (carried, cleared) = Watch(call, Mix(game, copy, Rms(game) / Rms(call) * 2));
        Assert.InRange(carried!.Value, 5, 20);
        Assert.InRange(cleared!.Value, 20, 40);
    }

    [Fact]
    public void ScoresACopyAtItsLagAndJudgesNothingWhileTheCallIsQuiet()
    {
        var rng = new Random(6);
        var call = new float[CallEcho.Window + CallEcho.Latest - CallEcho.Earliest];
        for (var i = 0; i < call.Length; i++) call[i] = rng.NextDouble() < 0.5 ? 0.0005f : (float)(0.01 + rng.NextDouble() * 0.1);
        var app = new float[CallEcho.Window];
        for (var b = 0; b < app.Length; b++) app[b] = call[b + CallEcho.Latest - 12] * 0.5f; // 120 ms after the call
        var (r, lag, judged) = CallEcho.Score(app, call, CallEcho.Earliest, CallEcho.Latest);
        Assert.True(judged);
        Assert.Equal(12, lag);
        Assert.True(r > 0.99f);
        Array.Fill(call, 0.0001f);
        Assert.False(CallEcho.Score(app, call, CallEcho.Earliest, CallEcho.Latest).Judged);
        Array.Fill(app, float.NaN);
        Assert.False(CallEcho.Score(app, call, CallEcho.Earliest, CallEcho.Latest).Judged);
    }

    // ---- a stream's sound counts ----

    [Fact]
    public void TheWatchedStreamsSoundSaysWhereItStops()
    {
        var sound = new StreamSound { Packets = 1500, Unknown = 2, Silent = 30, Decrypted = 1468, Undecrypted = 0 };
        var frame = new float[1920];
        frame[7] = 0.25f;
        for (var i = 0; i < 1468; i++) sound.Decoded(frame);
        Assert.Equal("sound: 1500 packets (2 on an SSRC no one announced, 30 silence), 1468 decrypted, 0 not, 1468 decoded, 0 not, 1460 played, 3 dropped behind; loudest -12 dBFS; Stream Volume 0%.",
            sound.Line(0f, false, 1460, 3));
        // The loudest is per line: none decoded since.
        Assert.Contains("loudest silent; no one's sound yet, deafened.", sound.Line(null, true, 1460, 3));
        Assert.Equal("silent", StreamSound.Db(0));
        Assert.Equal("0 dBFS", StreamSound.Db(1));
    }

    [Fact]
    public void YourStreamsSoundSaysWhatWentOut()
    {
        var sound = new StreamSound();
        var frame = new float[1920];
        sound.Going(frame, sent: true);
        frame[0] = 0.5f;
        sound.Going(frame, sent: false);
        Assert.Equal("Sound: 1 frames sent, 1 not (DAVE didn't encrypt them), loudest -6 dBFS.", sound.Sent());
        Assert.Equal("Sound: 0 frames sent, 0 not (DAVE didn't encrypt them), loudest silent.", sound.Sent());
    }
}
