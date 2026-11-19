using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Setups, built in with nothing to set: while a competitive game runs (anti-cheat, or a competitive game by its title)
/// Exo's power plan is on and the startup apps in the way are closed, and as it ends both go back. In the way: what PC ›
/// Apps › Startup says to remove (Roblox's tray, launchers' helpers), never Exo, anti-cheat, Lucid (the headphone EQ),
/// a Spotify app you run yourself, voice or stream apps (Discord, OBS and the like) or the running game
/// itself. Any other game changes nothing. Discord is left alone: its notifications and their sounds come in as always.
/// What was changed and the way back is saved before each change (setups-held.json), so a crash or a restart still
/// puts it back; only what Exo set is undone (a plan the user picked meanwhile is theirs, an app is reopened only when
/// it isn't running).
/// </summary>
internal static class Setups
{
    internal const string Competitive = "competitive", Everyday = "everyday";

    /// <summary>An app in the way: its startup entry, the process it runs as, and the command that opens it again.</summary>
    internal sealed record App(string Id, string Process, string Command);

    /// <summary>What a Setup holds: Exo's power plan, and the apps it closes.</summary>
    internal sealed record Parts(bool ExoPlan, IReadOnlyList<App> Close);

    /// <summary>
    /// What is held now and the way back: the plan that was on and the one Exo switched to, and the apps Exo closed.
    /// GameId: the game whose Setup this is.
    /// </summary>
    internal sealed record Held(string Setup, string? GameId, Guid? PlanBefore, Guid? PlanSet, List<App> Closed)
    {
        internal bool Holds => PlanSet is not null || Closed.Count > 0;
    }

    /// <summary>What a Setup touches on this PC.</summary>
    internal interface ISystem
    {
        Guid? ActivePlan();
        /// <summary>Exo's plan, made when missing; null when Windows won't make it.</summary>
        Guid? ExoPlan();
        bool SetPlan(Guid plan);
        bool Running(string process);
        void Close(string process);
        bool Open(string command);
    }

    /// <summary>
    /// From what is held to <paramref name="to"/>: each part moves only where it must, and its way back is the one saved
    /// when Exo first changed it. A plan the user changed since is left as they have it. Returns what is held after.
    /// </summary>
    internal static Held Move(Held? from, string setup, string? gameId, Parts to, ISystem system)
    {
        Guid? planBefore = from?.PlanBefore, planSet = from?.PlanSet;
        if (to.ExoPlan)
        {
            if (planSet is null && system.ExoPlan() is { } exo && system.ActivePlan() is { } active && active != exo && system.SetPlan(exo))
                (planBefore, planSet) = (active, exo);
        }
        else if (planSet is { } set)
        {
            if (system.ActivePlan() == set && planBefore is { } before) system.SetPlan(before);
            (planBefore, planSet) = (null, null);
        }

        var closed = new List<App>();
        foreach (var app in from?.Closed ?? [])
        {
            if (to.Close.Any(c => c.Id == app.Id)) { closed.Add(app); continue; }
            if (!system.Running(app.Process)) system.Open(app.Command);
        }
        foreach (var app in to.Close)
        {
            if (closed.Any(c => c.Id == app.Id) || NeverClosed(app.Process) || !system.Running(app.Process)) continue;
            system.Close(app.Process);
            closed.Add(app);
        }
        return new Held(setup, gameId, planBefore, planSet, closed);
    }

    // Kept open always, by their process name: Lucid (the headphone EQ), a Spotify app you run yourself, voice and stream apps.
    private static readonly string[] KeptOpen = ["lucid", "spotify", "discord", "obs", "streamlabs", "xsplit", "voicemeeter", "broadcast", "wavelink", "elgato", "streamdeck", "twitchstudio", "prism"];

    /// <summary>Never closed as in the way: anti-cheat, Lucid, Spotify, voice and stream apps.</summary>
    internal static bool NeverClosed(string process) =>
        ProcessTermination.IsAntiCheat(process) || KeptOpen.Any(name => process.Contains(name, StringComparison.OrdinalIgnoreCase));

    internal static Parts PartsOf(string setup, IReadOnlyList<App> inTheWay) =>
        setup == Competitive ? new(true, inTheWay) : new(false, []);

    /// <summary>A game's Setup: Competitive with anti-cheat or as a competitive game, else Everyday.</summary>
    internal static string For(GameEntry game) =>
        AntiCheatCheck.KeptOrQuick(game).Kind == AntiCheatCheck.Kind.Found
        || FluidMotionFrames.IsCompetitive(new FluidMotionFrames.Game(game.Title ?? "")) ? Competitive : Everyday;

    /// <summary>Whether a startup app is a running game: its process is the game's program, or its program is in the game's folder.</summary>
    internal static bool PartOfGame(string? program, string process, IEnumerable<(string? Exe, string? Folder)> games) =>
        games.Any(game =>
            (game.Exe is { Length: > 0 } exe && Path.GetFileNameWithoutExtension(exe).Equals(process, StringComparison.OrdinalIgnoreCase))
            || (program is not null && game.Folder is { Length: > 3 } folder
                && program.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

    // ---- This PC ----

    private static readonly Lock Gate = new();
    internal static string HeldPath { get; set; } = Path.Combine(PathHelper.AppDataDir, "setups-held.json");

    internal static Held? HeldNow()
    {
        try { return File.Exists(HeldPath) ? JsonSerializer.Deserialize<Held>(File.ReadAllText(HeldPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Exo's power plan is held by a Setup: PowerPlan.Auto leaves it be at start.</summary>
    internal static bool HoldsPlan() => HeldNow()?.PlanSet is not null;

    /// <summary>
    /// The startup apps in the way while a competitive game runs: PC › Apps › Startup's Run values and Startup folder
    /// files it says to remove, on at sign-in or not, except what is never closed and the games running.
    /// </summary>
    private static List<App> InTheWay(IReadOnlyList<(string? Exe, string? Folder)> games)
    {
        var list = new List<App>();
        foreach (var entry in StartupEntries.Read())
        {
            if (entry.Locked || entry.Missing || entry.Program is null || entry.Known?.Verdict != "remove"
                || entry.Source is not (StartupSource.Run or StartupSource.Folder)) continue;
            var names = StartupApps.ExecutableNames(entry.Command);
            var process = Path.GetFileNameWithoutExtension(names.Count > 0 ? names[^1] : entry.Program);
            if (string.IsNullOrEmpty(process) || NeverClosed(process) || PartOfGame(entry.Program, process, games)) continue;
            list.Add(new App(entry.Id, process, entry.Command));
        }
        return list;
    }

    /// <summary>The library games running now and the one starting: each one's program and install folder.</summary>
    private static List<(string? Exe, string? Folder)> Playing(string starting)
    {
        var services = ExoLauncher.App.Services;
        return [.. MainWindow.RunningLibraryGames().Keys.Append(starting).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => (services.Launcher.GameExecutable(id), services.Library.Find(id)?.Path))];
    }

    private static void Apply(string setup, string? gameId)
    {
        var inTheWay = setup == Competitive && gameId is not null ? InTheWay(Playing(gameId)) : [];
        var held = Move(HeldNow(), setup, gameId, PartsOf(setup, inTheWay), Native.Shared);
        if (held.Holds) Write(held);
        else
        {
            try { DeletionGate.DeleteFile(HeldPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        AppLog.Info($"Setups: {setup}{(gameId is null ? "" : $" for {gameId}")}: plan {(held.PlanSet is null ? "as it was" : "Exo's")}, {held.Closed.Count} app(s) closed.");
    }

    private static void Write(Held held)
    {
        try
        {
            var temp = HeldPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(held));
            File.Move(temp, HeldPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Setups: not saved: " + ex.Message); }
    }

    /// <summary>
    /// Each game's start and end; at start, what a Setup held before Exo closed is put back once its game is no longer
    /// running (half a minute in, when Exo knows what runs).
    /// </summary>
    internal static void Start(LaunchOrchestrator launcher, CancellationToken stop)
    {
        launcher.GameRunStateChanged += (gameId, state) => _ = Task.Run(() => GameChanged(launcher, gameId, state.IsRunning));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stop).ConfigureAwait(false);
                lock (Gate)
                {
                    if (HeldNow() is not { } held || (held.GameId is { } id && MainWindow.RunningLibraryGames().ContainsKey(id))) return;
                    Apply(Everyday, null);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Warn("Setups: " + ex.Message); }
        }, stop);
    }

    private static void GameChanged(LaunchOrchestrator launcher, string gameId, bool running)
    {
        try
        {
            // Decided before the lock and from the kept answer: no install walk while a game starts.
            var setup = running && ExoLauncher.App.Services.Library.Find(gameId) is { } started ? For(started) : null;
            lock (Gate)
            {
                var held = HeldNow();
                if (running)
                {
                    // The first competitive game holds its Setup; another started beside it runs under the same one.
                    if (held?.GameId is not null || setup != Competitive) return;
                    Apply(Competitive, gameId);
                }
                else if (held?.GameId == gameId && !launcher.AnyGameRunning) Apply(Everyday, null);
            }
        }
        catch (Exception ex) { AppLog.Warn("Setups: " + ex.Message); }
    }

    /// <summary>The real PC: Exo's power plan, processes closed normally and opened as Windows does at sign-in.</summary>
    private sealed class Native : ISystem
    {
        internal static readonly Native Shared = new();

        public Guid? ActivePlan() => PowerPlan.Shared.Active();
        public Guid? ExoPlan() => PowerPlan.Shared.Ready(PcHealth.ThisPcProfile());
        public bool SetPlan(Guid plan) => PowerPlan.Shared.Switch(plan);

        public bool Running(string process)
        {
            var found = Process.GetProcessesByName(process);
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }

        public void Close(string process) => ProcessHelper.TryCloseProcesses(process);

        public bool Open(string command)
        {
            try
            {
                var (program, arguments) = Split(Environment.ExpandEnvironmentVariables(command).Trim());
                using var started = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(program) ?? "" });
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                AppLog.Info("Setups: didn't reopen an app: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>A startup command's program and its arguments: quoted, or up to its .exe.</summary>
    internal static (string Program, string Arguments) Split(string command)
    {
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 0 ? (command[1..end], command[(end + 1)..].Trim()) : (command.Trim('"'), "");
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? (command[..(exe + 4)], command[(exe + 4)..].Trim()) : (command, "");
    }
}
