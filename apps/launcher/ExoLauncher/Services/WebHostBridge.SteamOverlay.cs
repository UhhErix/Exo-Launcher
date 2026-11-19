using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// What Exo keeps for each Steam game, decided per game with no switch in Properties. The Steam overlay is on only
/// where the game needs it (<see cref="SteamOverlayConfig.Needed"/>: Steam purchases, online play with friends, Remote
/// Play Together, Steam Input) and off everywhere else (it costs frames, and Exo has its own overlay); Steam Input
/// follows the game's controller support (<see cref="SteamInputConfig.Desired"/>). Steam keeps both in the account's
/// localconfig.vdf (<see cref="SteamOverlayConfig"/>), which it rewrites as it exits, so Exo writes them only while
/// Steam is not running: a pass that finds Steam running keeps what it wants in the record (its "?") and waits
/// (<see cref="OverlayPass.Decide"/>), and the values go in as Steam next closes or just before Exo itself starts it
/// (<see cref="ApplyPendingSteamSettingsAsync"/>). Exo never closes Steam to write a setting, whoever asks: the restarts
/// it does start are for its client update, an account or Beta switch or offline mode. Steam's notifications and sounds
/// and its controller dialogs before a game, always off (<see cref="StoreQuiet"/>), are written the same way. Cloud
/// saves, automatic updates and downloads while playing are the player's own switches in Steam's Properties: Exo reads
/// them for Properties, and never turns one back on.
/// </summary>
public sealed partial class WebHostBridge
{
    private readonly SemaphoreSlim _overlayGate = new(1, 1);

    /// <summary>1 while a pass left something in the record for Steam to take: the 30 s check runs only then.</summary>
    private int _overlayPending;
    private System.Threading.Timer? _pendingWritePoll;
    private int _pendingWritePolling;

    /// <summary>How often what waits is tried again while it does; the check stops as soon as nothing waits.</summary>
    private static readonly TimeSpan PendingWriteEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// After a library read: brings new Steam games to Exo's rules, in the background. Library pushes come in bursts (a
    /// cover warm raises one per batch) and each pass reads localconfig.vdf and appinfo.vdf whole, so a burst is one pass,
    /// 5 s after its first push; a push during the pass asks for one more.
    /// </summary>
    private void SteamOverlaySoon()
    {
        if (HostMode.IsDisposable) return;
        if (Interlocked.Exchange(ref _overlaySoonQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(OverlaySoonDelay, _bridgeLifetime.Token).ConfigureAwait(false);
                Volatile.Write(ref _overlaySoonQueued, 0);
                await ApplySteamOverlayAsync(_bridgeLifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Warn($"Steam overlay: {ex.GetType().Name}: {ex.Message}"); }
        });
    }

    private static readonly TimeSpan OverlaySoonDelay = TimeSpan.FromSeconds(5);
    private int _overlaySoonQueued;

    /// <summary>
    /// Brings Steam's switches to Exo's rules. Returns why a change waits, or null when Steam has it. Steam is never
    /// closed for it: while Steam runs, what Exo wants waits in the record (<see cref="OverlayPass.Decide"/>) and goes in
    /// as Steam next closes, or just before Exo next starts it.
    /// </summary>
    private async Task<string?> ApplySteamOverlayAsync(CancellationToken ct)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        await _overlayGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await OverlayPassAsync(ct).ConfigureAwait(false); }
        finally { _overlayGate.Release(); }
    }

    /// <summary>
    /// Writes what waited in the record while Steam ran, if Steam is not running now. Nothing at all while Steam runs (it
    /// holds both files in memory), and one caller at a time. Called just before Exo starts Steam
    /// (<see cref="SteamPendingSettings"/>), by the 30 s check <see cref="StartPendingWriteCheck"/> arms while something
    /// waits, and after a running Steam exits (the same check sees it gone).
    /// </summary>
    internal async Task ApplyPendingSteamSettingsAsync(CancellationToken ct)
    {
        if (HostMode.IsDisposable) return;
        if (ProcessHelper.IsProcessRunning("steam")) return;
        if (!await _overlayGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try { await OverlayPassAsync(ct).ConfigureAwait(false); }
        finally { _overlayGate.Release(); }
    }

    /// <summary>The 30 s check while something waits: tries what waited, and stops itself once nothing does.</summary>
    private void StartPendingWriteCheck()
    {
        if (HostMode.IsDisposable) return;
        if (_pendingWritePoll is not null) return;
        var poll = new System.Threading.Timer(_ => _ = PendingWriteTickAsync(), null, PendingWriteEvery, PendingWriteEvery);
        if (Interlocked.CompareExchange(ref _pendingWritePoll, poll, null) is not null) poll.Dispose();
    }

    private void StopPendingWriteCheck() => Interlocked.Exchange(ref _pendingWritePoll, null)?.Dispose();

    private async Task PendingWriteTickAsync()
    {
        if (_detached)
        {
            StopPendingWriteCheck();
            return;
        }
        if (Volatile.Read(ref _overlayPending) == 0)
        {
            StopPendingWriteCheck();
            return;
        }
        // Still waiting for Steam to close: the check stays armed (it was disarmed here before, so a change that waited
        // for a running Steam was never tried again once it exited).
        if (ProcessHelper.IsProcessRunning("steam")) return;
        if (Interlocked.Exchange(ref _pendingWritePolling, 1) == 1) return;
        Interlocked.Exchange(ref _overlayPending, 0);
        try { await ApplyPendingSteamSettingsAsync(_bridgeLifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warn($"Steam overlay: what waited could not be written: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _pendingWritePolling, 0);
            if (Volatile.Read(ref _overlayPending) == 0) StopPendingWriteCheck();
        }
    }

    /// <summary>Whether something waits in the record for Steam's next close: the check runs only then.</summary>
    private void SetOverlayPending(bool pending)
    {
        if (pending)
        {
            Interlocked.Exchange(ref _overlayPending, 1);
            StartPendingWriteCheck();
            return;
        }
        if (Interlocked.Exchange(ref _overlayPending, 0) == 1) StopPendingWriteCheck();
    }

    /// <summary>One pass, the gate held by its caller already: nothing in it waits on that gate again.</summary>
    private async Task<string?> OverlayPassAsync(CancellationToken ct)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        if (SteamInstallation.ResolveRoot() is not { } root || SteamPlaytime.TryGetLocalConfigPath(root) is not { } config) return null;
        var games = _services.Library.PeekCachedLibrary()
            .Where(game => game.Store == StoreKind.Steam && game.Installed && SteamProtocol.IsValidAppId(game.LaunchTarget))
            .ToList();
        var installed = games.Select(game => game.LaunchTarget!).Distinct(StringComparer.Ordinal).ToList();
        var names = games.GroupBy(game => game.LaunchTarget!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
        var settings = _services.Settings.Current;
        var configText = SteamPlaytime.ReadSharedText(config);
        // A game appinfo doesn't have is left as it is.
        var info = InstalledAppInfo(root, installed);
        var rule = info.ToDictionary(a => a.Key, a => SteamInputConfig.Desired(a.Value), StringComparer.Ordinal);
        // Games that aren't Steam's, sent through Steam Input: their shortcuts, with Steam Input on for each.
        var (shortcutRecord, playersInput) = ShortcutRecord();
        var wantedShortcuts = ShortcutsWanted();
        var shortcuts = PlanShortcuts(config, wantedShortcuts, shortcutRecord);
        var apps = SteamOverlayConfig.Apps(configText);
        var input = SteamInputConfig.Plan(apps, rule, ShortcutInputChoices(shortcuts?.Record ?? shortcutRecord, playersInput), settings.SteamInput);
        var (writes, record) = SteamOverlayConfig.Plan(apps, OverlayWanted(info, apps, input.Writes), settings.SteamOverlay);
        var quietWanted = settings.StoreClientsQuiet;
        var quiet = StoreQuietPass.Pending(root, quietWanted);
        var anything = writes.Count > 0 || quiet > 0 || input.Writes.Count > 0 || shortcuts?.File is not null;
        // Whether there is anything to write and whether Steam is running decide the pass: nothing to do, values
        // waiting for Steam's next close, or the files written now because Steam is not holding them.
        switch (OverlayPass.Decide(ProcessHelper.IsProcessRunning("steam"), anything))
        {
            case OverlayPass.Plan.Nothing:
                // Steam running with everything in place: it may rewrite its files as it exits (an update, Steam Cloud), so a
                // pass runs once it has closed; the values it changed back go in again then.
                if (quietWanted && ProcessHelper.IsProcessRunning("steam")) SetOverlayPending(true);
                SaveOverlayRecord(record);
                if (shortcuts is { } kept) SaveShortcuts(kept.Record, kept.Dropped, playersInput, input.Choices);
                SaveSteamInput(input.Record);
                return null;
            case OverlayPass.Plan.Wait:
                // Steam holds both files in memory and writes them as it exits: whatever was asked for, and however
                // it was asked for, it waits. Said in the log too: a change that waits looks, from the page, like one
                // that did nothing.
                AppLog.Info($"Steam overlay: {writes.Count} change(s) and Steam Input: {input.Writes.Count}{(quiet > 0 ? $", and {quiet} file(s) of Steam's notifications and dialogs," : "")} wait. Steam is running; they go in as it next closes.");
                SaveOverlayRecord(record);
                if (shortcuts is { } waiting) SaveShortcuts(waiting.Record, waiting.Dropped, playersInput, input.Choices);
                SaveSteamInput(input.Record);
                SetOverlayPending(true);
                return "Steam takes it when it next closes.";
            default:
                break;
        }
        // Steam is not running: the files on disk are its own last write, and Exo's values go into them.
        var text = SteamPlaytime.ReadSharedText(config) ?? throw new IOException("localconfig.vdf is gone.");
        var shortcutsNow = PlanShortcuts(config, wantedShortcuts, shortcutRecord);
        if (shortcutsNow?.File is { } shortcutsFile)
        {
            WriteShortcuts(config, shortcutsFile);
            AppLog.Info($"Steam shortcuts: {shortcutsNow.Value.Record.Count} game(s) through Steam Input.");
        }
        var inputNow = SteamInputConfig.Plan(SteamOverlayConfig.Apps(text), rule,
            ShortcutInputChoices(shortcutsNow?.Record ?? shortcutRecord, playersInput), settings.SteamInput);
        var (next, landed) = OverlayPass.Written(text, new OverlayPass.Wanted(
            writes, record, inputNow.Writes));
        if (next != text) SteamOverlayConfig.WriteFile(config, next);
        if (shortcutsNow is { } keptNow) SaveShortcuts(keptNow.Record, keptNow.Dropped, playersInput, inputNow.Choices);
        SaveSteamInput(inputNow.Record);
        foreach (var (appId, value) in inputNow.Writes)
            AppLog.Info($"Steam Input: {SteamName(names, appId)} {value switch { "0" => "off, it reads the controller itself", "2" => "on", _ => "back to Steam's own" }}.");
        // Every account's notifications, sounds and dialogs before a game, from the files on disk: put in (and read back,
        // with what they held kept) while quiet is on, put back while it is off.
        var (quieted, _) = StoreQuietPass.Run(root, quietWanted);
        if (quieted > 0) AppLog.Info($"Steam: notifications, sounds and dialogs before a game {(quietWanted ? "off" : "back as they were")} ({quieted} file(s)).");
        SaveOverlayRecord(landed);
        foreach (var (appId, on) in writes) AppLog.Info($"Steam overlay: {SteamName(names, appId)} {(on ? "on" : "off")}.");
        return null;
    }

    private static string SteamName(IReadOnlyDictionary<string, string> names, string appId) =>
        names.TryGetValue(appId, out var title) ? $"{title} ({appId})" : appId == SteamInputConfig.DesktopLayout ? "desktop layout" : appId;

    /// <summary>Each game's overlay as it needs it, with Steam Input as it will be once these writes land.</summary>
    private Dictionary<string, bool> OverlayWanted(IReadOnlyDictionary<string, SteamKeyValues> info, SteamKeyValues apps, IReadOnlyDictionary<string, string> inputWrites)
    {
        bool? pad = null;
        bool Pad() => pad ??= PlayStationPadKnown();
        return info.ToDictionary(a => a.Key, a => SteamOverlayConfig.Needed(a.Value, SteamInputConfig.InUse(
            inputWrites.TryGetValue(a.Key, out var written) ? written.Length == 0 ? null : written : SteamInputConfig.Read(apps, a.Key), a.Value, Pad)),
            StringComparer.Ordinal);
    }

    /// <summary>Set once Play has asked for a pass for its controller dialogs this run, so a file Steam won't take is not asked for at every Play.</summary>
    private int _steamDialogsAsked;

    /// <summary>Whether Steam's controller notice settings need a pass (once a run).</summary>
    private bool SteamDialogPending() =>
        SteamInstallation.ResolveRoot() is { } root &&
        _services.Settings.Current.StoreClientsQuiet && StoreQuiet.LaunchDialogsPending(root) && Interlocked.Exchange(ref _steamDialogsAsked, 1) == 0;

    /// <summary>These games' appinfo (Steam's local copy of each store entry), by app id.</summary>
    private static Dictionary<string, SteamKeyValues> InstalledAppInfo(string root, IReadOnlyCollection<string> installed)
    {
        var ids = installed.Select(id => uint.TryParse(id, out var n) ? n : 0).Where(n => n != 0).ToHashSet();
        if (ids.Count == 0 || ReadSharedBytes(Path.Combine(root, "appcache", "appinfo.vdf")) is not { } appInfo) return [];
        return SteamAppInfoNames.ReadApps(appInfo, ids).ToDictionary(a => a.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), a => a.Value, StringComparer.Ordinal);
    }


    private void SaveSteamInput(Dictionary<string, string> record)
    {
        var kept = _services.Settings.Current.SteamInput;
        if (record.Count == kept.Count && record.All(e => kept.GetValueOrDefault(e.Key) == e.Value)) return;
        _services.Settings.UpdateProfile(x => x.SteamInput = new Dictionary<string, string>(record, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether Steam Input maps the controller in this game, as Exo keeps it (or as the player set it in Steam):
    /// Properties then offers Steam's own controller page.
    /// </summary>
    private bool SteamInputInUse(string appId, SteamKeyValues app)
    {
        if (SteamInstallation.ResolveRoot() is not { } root || SteamPlaytime.TryGetLocalConfigPath(root) is not { } config) return false;
        var value = _services.Settings.Current.SteamInput.GetValueOrDefault(appId) != SteamInputConfig.Players && SteamInputConfig.Desired(app) is { } desired
            ? desired
            : SteamInputConfig.Read(SteamOverlayConfig.Apps(SteamPlaytime.ReadSharedText(config)), appId);
        return SteamInputConfig.InUse(value, app, PlayStationPadKnown);
    }

    /// <summary>How many installed Steam games read the controller themselves, and how many keep Steam Input.</summary>
    private object SteamInputState()
    {
        if (SteamInstallation.ResolveRoot() is not { } root) return new { ok = true, steam = false, native = 0, translated = 0 };
        var installed = _services.Library.PeekCachedLibrary()
            .Where(game => game.Store == StoreKind.Steam && game.Installed && SteamProtocol.IsValidAppId(game.LaunchTarget))
            .Select(game => game.LaunchTarget!).ToList();
        var games = InstalledAppInfo(root, installed).Values.Where(SteamInputConfig.IsGame).ToList();
        var native = games.Count(app => SteamInputConfig.Desired(app) == "0");
        return new { ok = true, steam = true, native, translated = games.Count - native };
    }

    private void SaveOverlayRecord(Dictionary<string, string> record)
    {
        var current = _services.Settings.Current.SteamOverlay;
        if (current.Count == record.Count && record.All(entry => current.GetValueOrDefault(entry.Key) == entry.Value)) return;
        _services.Settings.UpdateProfile(s => s.SteamOverlay = new Dictionary<string, string>(record, StringComparer.OrdinalIgnoreCase));
    }
}
