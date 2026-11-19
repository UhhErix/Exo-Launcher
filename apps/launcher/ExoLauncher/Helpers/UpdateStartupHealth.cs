using System.Text.Json;

namespace ExoLauncher.Helpers;

/// <summary>
/// One startup receipt for the installer that is currently retaining our previous payload (Complete-ExoUpdate.ps1). It is
/// written when the UI says it reached a usable start (shell.ready: the Library or first-run setup is up), so the check reads
/// no page element. Only the WebView page sends it today; the native (GPUI) UI must call <see cref="UiReady"/> the same way,
/// or every update to a native build rolls back after three minutes (misc\OPEN-ITEMS.md). The receipt echoes the
/// version the installer named in update-pending.json, the very string the installer compares it to.
/// </summary>
internal static class UpdateStartupHealth
{
    internal const string NonceVariable = "EXO_UPDATE_HEALTH_NONCE";
    internal static bool Requested => IsNonce(Environment.GetEnvironmentVariable(NonceVariable));
    private static int _reported;

    /// <summary>The UI reached a usable start: the receipt is written once, off the caller's thread, tried again for a while
    /// (a scanner holding the folder for a second must not have the updater take a working build for a dead one).</summary>
    internal static void UiReady(string version)
    {
        if (!Requested || Interlocked.Exchange(ref _reported, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (MarkHealthy(version)) return;
                await Task.Delay(1000).ConfigureAwait(false);
            }
        });
    }

    internal static bool MarkHealthy(string version)
    {
        var app = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var root = Path.GetDirectoryName(app);
        var nonce = Environment.GetEnvironmentVariable(NonceVariable);
        if (root is null || Environment.ProcessPath is not { } executable || !IsNonce(nonce)) return false;
        var written = TryWrite(root, executable, nonce!, version, Environment.ProcessId);
        if (written) Environment.SetEnvironmentVariable(NonceVariable, null);
        return written;
    }

    internal static bool TryWrite(string root, string executable, string nonce, string version, int processId)
    {
        try
        {
            if (!IsNonce(nonce)) return false;
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var app = Path.Combine(root, "app");
            if (!Path.GetFullPath(executable).Equals(Path.Combine(app, "ExoLauncher.exe"), StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var path in new[] { root, app, executable })
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            var pendingPath = Path.Combine(root, "update-pending.json");
            if (!File.Exists(pendingPath) || new FileInfo(pendingPath).Length > 4096 ||
                (File.GetAttributes(pendingPath) & FileAttributes.ReparsePoint) != 0) return false;
            using var pending = JsonDocument.Parse(File.ReadAllText(pendingPath));
            var record = pending.RootElement;
            if (record.GetProperty("nonce").GetString() != nonce || record.GetProperty("schema").GetInt32() != 1 ||
                record.GetProperty("version").GetString() is not { } asked ||
                Services.AppUpdateService.Normalize(asked) != Services.AppUpdateService.Normalize(version) ||
                !Path.GetFullPath(record.GetProperty("executable").GetString()!).Equals(executable, StringComparison.OrdinalIgnoreCase) ||
                !record.GetProperty("createdUtc").TryGetDateTimeOffset(out var created)) return false;
            var age = DateTimeOffset.UtcNow - created;
            if (age < TimeSpan.Zero || age > TimeSpan.FromMinutes(5)) return false;
            var receipt = Path.Combine(root, "update-health-" + nonce + ".json");
            var temporary = receipt + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new { schema = 1, nonce, version = asked, processId, executable, healthy = true }));
                File.Move(temporary, receipt, overwrite: true);
                return true;
            }
            finally { if (File.Exists(temporary)) DeletionGate.DeleteFile(temporary); }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Update startup receipt was not written: " + ex.GetType().Name);
            return false;
        }
    }

    private static bool IsNonce(string? value) => value is { Length: 32 } && value.All(Uri.IsHexDigit);
}
