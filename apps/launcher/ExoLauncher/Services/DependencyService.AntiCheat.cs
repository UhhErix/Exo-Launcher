using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Easy Anti-Cheat as a game's dependency. It is the PC's, not the game's: one service shared by every game and every
/// store's copy that uses it, so uninstalling one copy (a store's uninstall runs the setup's own uninstall) takes it from
/// all the others, which then refuse to start ("Easy Anti-Cheat is not installed"). A game that ships its setup and finds
/// the service gone gets it before Play, from that setup, signed by Easy Anti-Cheat's maker, behind Windows' permission.
/// Exo only runs the game's own installer; it never changes an anti-cheat file.
/// </summary>
public sealed partial class DependencyService
{
    internal const string AntiCheatPrefix = "easy-anti-cheat:";
    internal const string AntiCheatName = "Easy Anti-Cheat";
    internal static readonly string[] AntiCheatServices = ["EasyAntiCheat_EOS", "EasyAntiCheat"];
    private static readonly string[] AntiCheatPublishers = ["EasyAntiCheat Oy", "Epic Games, Inc.", "Epic Games Inc."];

    /// <param name="Service">The Windows service the setup installs.</param>
    /// <param name="Setup">The setup the game ships.</param>
    /// <param name="ProductId">The game's product id (its Settings.json), which the setup installs for.</param>
    internal sealed record AntiCheatSetup(string Service, string Setup, string ProductId);

    // Setups Exo found in its own scan of a library game's folder, by dependency id: the only ones it will run, whatever
    // id the page sends back.
    private static readonly ConcurrentDictionary<string, AntiCheatSetup> AntiCheatFound = new(StringComparer.Ordinal);

    /// <summary>
    /// The Easy Anti-Cheat setup a game ships: an EasyAntiCheat folder within four folders of the install (the game's
    /// root, or beside its executable as Binaries\Win64\EasyAntiCheat) holding the EOS setup or the older one, and the
    /// product id from its Settings.json. Null when the game ships none.
    /// </summary>
    internal static AntiCheatSetup? AntiCheatOf(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath)) return null;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var folder in Directory.EnumerateDirectories(gamePath, "EasyAntiCheat", options).Prepend(Path.Combine(gamePath, "EasyAntiCheat")))
            {
                if (!Directory.Exists(folder)) continue;
                var (service, setup) = File.Exists(Path.Combine(folder, "EasyAntiCheat_EOS_Setup.exe"))
                    ? ("EasyAntiCheat_EOS", Path.Combine(folder, "EasyAntiCheat_EOS_Setup.exe"))
                    : ("EasyAntiCheat", Path.Combine(folder, "EasyAntiCheat_Setup.exe"));
                if (!File.Exists(setup) || ProductIdOf(Path.Combine(folder, "Settings.json")) is not { } product) continue;
                return new AntiCheatSetup(service, setup, product);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    /// <summary>The game's product id from its Easy Anti-Cheat Settings.json: 32 hex digits, or null.</summary>
    internal static string? ProductIdOf(string settings)
    {
        try
        {
            if (!File.Exists(settings) || new FileInfo(settings).Length > 64 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(settings), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("productid", out var id) &&
                   id.ValueKind == JsonValueKind.String && id.GetString() is { Length: 32 } text && text.All(char.IsAsciiHexDigit)
                ? text
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Whether a Windows service is registered (its key under Services), read without starting anything.</summary>
    internal static bool ServiceExists(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The anti-cheat services registered now, of those Exo watches.</summary>
    internal static IReadOnlySet<string> AntiCheatPresent() => AntiCheatServices.Where(ServiceExists).ToHashSet(StringComparer.Ordinal);

    /// <summary>The game's Easy Anti-Cheat as a missing dependency, when it ships the setup and the service is gone.</summary>
    internal static DependencyInfo? MissingAntiCheat(GameEntry game)
    {
        if (!game.Installed || AntiCheatOf(game.Path) is not { } setup || ServiceExists(setup.Service)) return null;
        var id = AntiCheatPrefix + game.Id;
        AntiCheatFound[id] = setup;
        return new DependencyInfo { Id = id, Name = AntiCheatName, Status = "missing", Detail = setup.Setup, CanOfferInstall = true };
    }

    /// <summary>
    /// Puts Easy Anti-Cheat back with the setup Exo found for <paramref name="id"/> (<see cref="MissingAntiCheat"/>):
    /// only when it is signed by Easy Anti-Cheat's maker, behind Windows' permission, then checked by its service.
    /// </summary>
    internal static async Task<ItemResult> RepairAntiCheatAsync(string id, CancellationToken ct)
    {
        if (!AntiCheatFound.TryGetValue(id, out var setup))
            return new(id, AntiCheatName, "failed", "Exo couldn't find the game's Easy Anti-Cheat. Press Play again.", null);
        if (ServiceExists(setup.Service)) return new(id, AntiCheatName, "installed", "Easy Anti-Cheat is installed.", null);
        if (!AuthenticodeVerifier.HasPublisher(setup.Setup, AntiCheatPublishers))
            return new(id, AntiCheatName, "failed", "The game's Easy Anti-Cheat installer isn't signed by its maker, so Exo won't run it.", null);
        int? code = null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(setup.Setup)
            {
                Arguments = "install " + setup.ProductId,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(setup.Setup)!,
            });
            if (process is not null)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromMinutes(5));
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                code = process.ExitCode;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(id, AntiCheatName, "failed", "Windows' permission was declined, so Easy Anti-Cheat wasn't installed.", null);
        }
        catch (OperationCanceledException)
        {
            return new(id, AntiCheatName, "failed", "Easy Anti-Cheat's installer didn't finish. Try again.", null);
        }
        var back = ServiceExists(setup.Service);
        AppLog.Info($"Anti-cheat: {setup.Service} {(back ? "put back" : "still missing")} with the game's own setup (exit {code?.ToString() ?? "?"}).");
        return back
            ? new(id, AntiCheatName, "installed", "Easy Anti-Cheat is installed.", code)
            : new(id, AntiCheatName, "failed", "Easy Anti-Cheat's installer finished, but its service isn't there. Try again.", code);
    }
}
