using System.Diagnostics;

namespace ExoLauncher.Helpers;

/// <summary>
/// Fixed command lines (reg.exe, pnputil) chained in one cmd.exe run as administrator, so Windows asks once. Callers build
/// every line from values Windows itself names, checked against their own patterns, so cmd reads nothing specially.
/// </summary>
internal static class ElevatedCommands
{
    /// <summary>
    /// Whether running something as administrator shows Windows' prompt: not when Exo already runs elevated, UAC is off, or
    /// the user is an administrator Windows elevates without asking (ConsentPromptBehaviorAdmin 0). Unsure is yes.
    /// </summary>
    internal static bool WindowsAsks()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            // With UAC on, the Administrators role holds only in an elevated process.
            if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return false;
            using var policy = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            if (policy?.GetValue("EnableLUA") is 0) return false;
            // An administrator whose elevation Windows grants without asking (its token holds Administrators deny-only until then).
            var admin = identity.Claims.Any(c => c.Type is System.Security.Claims.ClaimTypes.DenyOnlySid or System.Security.Claims.ClaimTypes.GroupSid && c.Value == "S-1-5-32-544");
            return !(admin && policy?.GetValue("ConsentPromptBehaviorAdmin") is 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return true; }
    }

    /// <summary>Runs <paramref name="commands"/>; null when it ran, else the line to show. <paramref name="log"/> names the caller in app.log; <paramref name="wait"/> how long it may take, the prompt included (a minute).</summary>
    internal static string? Run(IReadOnlyList<string> commands, string log, TimeSpan? wait = null)
    {
        if (commands.Count == 0) return null;
        try
        {
            // /s: cmd strips only the outer quotes, so each quoted path stays whole.
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + string.Join(" & ", commands) + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            });
            if (process is null) return "Windows didn't start the change.";
            if (!process.WaitForExit(wait ?? TimeSpan.FromSeconds(60))) return "The change didn't finish.";
            if (process.ExitCode != 0) AppLog.Warn($"{log}: the change ended with exit code {process.ExitCode}.");
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Windows' permission was declined, so nothing changed.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn($"{log}: the change didn't start: " + ex.Message);
            return "Windows didn't start the change.";
        }
    }
}
