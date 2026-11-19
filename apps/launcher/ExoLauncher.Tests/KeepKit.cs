using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Fixtures for Keep's tests: Modern Warfare 4's PC file and one account's, in a folder of their own, with cod.exe as season 1
/// left it, optimized for a PC with two screens. Nothing here touches the PC's real game files, registry or processes.
/// </summary>
internal static class KeepKit
{
    internal static readonly DateTime Season1 = new(2026, 10, 14, 18, 34, 38, DateTimeKind.Utc);
    internal static readonly DateTime Season2 = new(2026, 11, 18, 17, 0, 0, DateTimeKind.Utc);
    internal const string NoGame = "exo-test-no-such-game";

    internal const string Main = """
        1


        //
        // Display

        DisplayMode@0;100;1 = Fullscreen // one of Fullscreen, Fullscreen borderless window, Windowed

        PreferredDisplayMode@0;100;2 = Fullscreen // one of Fullscreen, Fullscreen borderless window, Windowed

        ConstrainMouse@0;100;3 = false

        Resolution@0;100;4 = 1920x1080

        GPUName@0;100;5 = NVIDIA GeForce RTX 3070

        GPUDriverVersion@0;100;6 = 32.0.15.2114

        LastUsedGPU@0;100;7 = 0

        RecommendedSet@0;100;8 = 1

        VSync@0;9203;8071 = 100% // one of disabled, 100%, 50%, 33%, 25%

        NvidiaReflex@0;7717;12207 = Disabled // one of Disabled, Enabled, Enabled + boost

        MotionBlur@0;11212;9876 = On // one of Off, On, Cinematic Only

        TextureFilter@0;63279;8284 = aniso 2x // one of aniso 2x, aniso 4x, aniso 8x, aniso 16x

        //
        // Audio

        MusicVolume@0;14317;21371 = 1.000000 // 0.000000 to 1.000000

        CinematicVolume@0;14317;21372 = 1.000000 // 0.000000 to 1.000000

        SkipSeasonIntroVideo@0;57752;48403 = false

        """;

    internal const string Player = """
        1


        // 1st Person Field of View
        Fov@1;65095;45350 = 90.000000 // 60.000000 to 120.000000

        MusicVolume@0;54363;21371 = 1.000000 // 0.000000 to 1.000000

        """;

    internal sealed record Game(GameSettings.Profile Profile, string Folder, string Main, string Player, string Program);

    internal static string NewFolder()
    {
        var folder = Path.Combine(TestDataDirectory.Root, "keepkit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The game's files as it wrote them, before Optimize.</summary>
    internal static void Reset(string main, string? player = null)
    {
        File.WriteAllText(main, Main.ReplaceLineEndings("\r\n"));
        if (player is not null) File.WriteAllText(player, Player.ReplaceLineEndings("\r\n"));
    }

    /// <summary>The game rewrites a file as it closes: its text with these values in place of those.</summary>
    internal static void Rewrite(string path, params (string From, string To)[] values)
    {
        var text = File.ReadAllText(path);
        foreach (var (from, to) in values)
        {
            Assert.Contains(from, text, StringComparison.Ordinal);
            text = text.Replace(from, to, StringComparison.Ordinal);
        }
        File.WriteAllText(path, text);
    }

    /// <summary>Modern Warfare 4 in a folder of its own for a PC with these facts, not yet optimized.</summary>
    internal static Game Fresh(GameSettings.Pc? pc = null, string process = NoGame, IReadOnlyList<string>? helpers = null)
    {
        var folder = NewFolder();
        var main = Path.Combine(folder, "s.1.0.cod26.txt");
        File.WriteAllText(main, Main.ReplaceLineEndings("\r\n"));
        var player = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "76561198000000001")).FullName, "g.cod26.1.0.l.txt");
        File.WriteAllText(player, Player.ReplaceLineEndings("\r\n"));
        var program = Path.Combine(folder, "cod.exe");
        File.WriteAllBytes(program, []);
        File.SetLastWriteTimeUtc(program, Season1);
        var id = Guid.NewGuid().ToString("N");
        pc ??= new("nvidia", 5, 8, Screens: 2);
        var profile = GameSettings.WithInputs(GameSettings.ModernWarfare4(pc) with
        {
            Id = "mw4-" + id, Title = "MW4 " + id, File = main, PlayerFiles = () => [player], Processes = [process], Helpers = helpers,
        }, pc);
        return new(profile, folder, main, player, program);
    }

    internal static Game Optimized(GameSettings.Pc? pc = null, string process = NoGame, IReadOnlyList<string>? helpers = null)
    {
        var game = Fresh(pc, process, helpers);
        Assert.True(GameSettings.Set(game.Profile, null, true, game.Program));
        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(game.Main), StringComparison.Ordinal);
        return game;
    }

    /// <summary>
    /// A process name that is always running: the test's own process, so a profile with it as the game's program is busy for
    /// as long as the test is. Disposed with it.
    /// </summary>
    internal sealed class RunningGame : IDisposable
    {
        private readonly System.Diagnostics.Process _self = System.Diagnostics.Process.GetCurrentProcess();
        internal string Name => _self.ProcessName;
        public void Dispose() => _self.Dispose();
    }

    /// <summary>What Keep says in its log about one game, while the test runs.</summary>
    internal sealed class Heard : IDisposable
    {
        private readonly string _title;
        private readonly Action<string> _listen;
        internal List<string> Lines { get; } = [];

        internal Heard(string title)
        {
            _title = title;
            _listen = line => { if (line.Contains(_title, StringComparison.Ordinal)) lock (Lines) Lines.Add(line); };
            KeepEvidence.Logged += _listen;
        }

        public void Dispose() => KeepEvidence.Logged -= _listen;
    }
}
