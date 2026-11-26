using System.Diagnostics;
using ExoLauncher.Models;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal static class ExoUpscaleService
{
    internal sealed record PolicyDecision(
        GameAntiCheatClass Class,
        bool DrsOverrideAllowed,
        bool SignedDllSwapAllowed,
        bool FgBridgeAllowed,
        bool RequiresBanRiskConfirmation,
        bool RequiresOfflineNote,
        string? WarningOrNote);

    internal static PolicyDecision EvaluatePolicy(
        GameEntry game,
        AntiCheatCheck.Answer antiCheat,
        bool banRiskConfirmed = false,
        bool officialOfflineMode = false)
    {
        if (antiCheat.Kind == AntiCheatCheck.Kind.Unknown)
        {
            return new PolicyDecision(
                Class: GameAntiCheatClass.CompetitivePvpOnline,
                DrsOverrideAllowed: true,
                SignedDllSwapAllowed: false,
                FgBridgeAllowed: false,
                RequiresBanRiskConfirmation: false,
                RequiresOfflineNote: false,
                WarningOrNote: null);
        }

        var acClass = AntiCheatClass.Classify(game, antiCheat);
        return acClass switch
        {
            GameAntiCheatClass.NoAntiCheat => new PolicyDecision(
                Class: acClass,
                DrsOverrideAllowed: true,
                SignedDllSwapAllowed: true,
                FgBridgeAllowed: true,
                RequiresBanRiskConfirmation: false,
                RequiresOfflineNote: false,
                WarningOrNote: null),

            GameAntiCheatClass.OfflineAntiCheat => new PolicyDecision(
                Class: acClass,
                DrsOverrideAllowed: true,
                SignedDllSwapAllowed: officialOfflineMode || antiCheat.Kind == AntiCheatCheck.Kind.None,
                FgBridgeAllowed: officialOfflineMode || antiCheat.Kind == AntiCheatCheck.Kind.None,
                RequiresBanRiskConfirmation: false,
                RequiresOfflineNote: true,
                WarningOrNote: AntiCheatClass.OfflineOneTimeNote(game.Title)),

            GameAntiCheatClass.OnlineNonCompetitivePve => new PolicyDecision(
                Class: acClass,
                DrsOverrideAllowed: true,
                SignedDllSwapAllowed: banRiskConfirmed,
                FgBridgeAllowed: banRiskConfirmed,
                RequiresBanRiskConfirmation: true,
                RequiresOfflineNote: false,
                WarningOrNote: AntiCheatClass.BanRiskConfirmation(game.Title, antiCheat.Name ?? antiCheat.Label)),

            _ => new PolicyDecision(
                Class: GameAntiCheatClass.CompetitivePvpOnline,
                DrsOverrideAllowed: true,
                SignedDllSwapAllowed: false,
                FgBridgeAllowed: false,
                RequiresBanRiskConfirmation: false,
                RequiresOfflineNote: false,
                WarningOrNote: null),
        };
    }

    internal static string? FindExePath()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] direct =
        [
            Path.Combine(baseDir, "exo-upscale.exe"),
            Path.Combine(baseDir, "native-ui", "exo-upscale.exe")
        ];
        foreach (var candidate in direct)
        {
            if (File.Exists(candidate))
                return candidate;
        }

#if DEBUG
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var rel = Path.Combine(dir.FullName, "native-ui", "target", "release", "exo-upscale.exe");
            if (File.Exists(rel))
                return rel;
            var dbg = Path.Combine(dir.FullName, "native-ui", "target", "debug", "exo-upscale.exe");
            if (File.Exists(dbg))
                return dbg;
            dir = dir.Parent;
        }

#endif
        return null;
    }

    internal static string? FindFgBridgeDllPath()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] direct =
        [
            Path.Combine(baseDir, "exo_fg_bridge.dll"),
            Path.Combine(baseDir, "native-ui", "exo_fg_bridge.dll")
        ];
        foreach (var candidate in direct)
        {
            if (File.Exists(candidate))
                return candidate;
        }

#if DEBUG
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var rel = Path.Combine(dir.FullName, "native-ui", "target", "release", "exo_fg_bridge.dll");
            if (File.Exists(rel))
                return rel;
            var dbg = Path.Combine(dir.FullName, "native-ui", "target", "debug", "exo_fg_bridge.dll");
            if (File.Exists(dbg))
                return dbg;
            dir = dir.Parent;
        }

#endif
        return null;
    }

    internal static bool IsFgBridgeAvailable() => FindFgBridgeDllPath() is not null;

    internal static bool RecoverOrphanedSessionsOnStartup(string stateDir, string backupDir)
    {
        if (HostMode.IsDisposable) return false;
        var exe = FindExePath();
        if (exe is null || !Directory.Exists(stateDir))
            return false;

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--recover-orphans");
            psi.ArgumentList.Add(stateDir);
            psi.ArgumentList.Add(backupDir);
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.WaitForExit(10_000);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Exo Upscale orphan recovery skipped: " + ex.Message);
            return false;
        }
    }
}
