using System.Text.Json;
using ExoLauncher.Helpers;
using System.Linq;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Services;

/// <summary>
/// The Exo account on the onboarding account step and in Settings > Account: who is signed in, create, and sign in. The
/// password comes in once for the request and is never sent back; the recovery code goes back once, for the screen to show.
/// </summary>
public sealed partial class WebHostBridge
{
    /// <summary>
    /// This PC's session file was written or removed (a sign-in or sign-out anywhere, a deleted account, or a session the
    /// service ended during a sync): the page reads exoAccount.overview again, so the account sheet, setup and Settings share
    /// one state and the sheet asks again the moment a session is gone.
    /// </summary>
    private void OnAccountChanged(string dataDir)
    {
        if (string.Equals(dataDir, PathHelper.AppDataDir, StringComparison.OrdinalIgnoreCase)) PostEvent("exoAccount.changed", new { });
    }

    /// <summary>
    /// While the player continued without an account: whether the account service answers again, and whether a game runs
    /// (the sheet never comes back over a game). The page asks every few minutes and brings the sheet back when both allow.
    /// </summary>
    private async Task<object> ExoAccountProbeAsync()
    {
        using var client = new ExoAccountClient(dataDir: PathHelper.AppDataDir);
        var reachable = await client.ReachableAsync().ConfigureAwait(true);
        return new { reachable, gameRunning = _services.Launcher.AnyGameRunning || GameRunningSignal.Running };
    }

    /// <summary>The recovery code on screen was typed back: the account sheet may go (ExoAccountService.ConfirmCode).</summary>
    private object ExoAccountCodeConfirmed()
    {
        ExoAccountService.ConfirmCode(PathHelper.AppDataDir);
        return new { ok = true };
    }

    private object ExoAccountState() =>
        ExoAccountService.State(PathHelper.AppDataDir, _services.Settings.Current.AccountAsked);

    private async Task<object> ExoAccountCreateAsync(JsonElement p, bool hasParams)
    {
        var (email, password) = AccountCredentials(p, hasParams);
        if (email is null || password is null) return new ExoAccountOutcome(false, AccountMessages.Generic, "bad-request", null, false, null);
        return await ExoAccountService.CreateAsync(PathHelper.AppDataDir, email, password);
    }

    private async Task<object> ExoAccountSignInAsync(JsonElement p, bool hasParams)
    {
        var (email, password) = AccountCredentials(p, hasParams);
        if (email is null || password is null) return new ExoAccountOutcome(false, AccountMessages.Generic, "bad-request", null, false, null);
        return await ExoAccountService.SignInAsync(PathHelper.AppDataDir, email, password);
    }

    /// <summary>
    /// Save the recovery code as a text file. The dialog opens with no default folder, so the person picks where it goes
    /// (CLIENT.md 4.1 C). The code is written and then forgotten here: nothing keeps it.
    /// </summary>
    private async Task<object> ExoAccountSaveCodeAsync(JsonElement p, bool hasParams)
    {
        var code = ReadString(p, hasParams, "code");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64) return new { ok = false, message = "There is no recovery code to save." };
        var picked = await OnUiThread(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow!));
            picker.FileTypeChoices.Add("Text", [".txt"]);
            picker.SuggestedFileName = "Exo recovery code";
            return (await picker.PickSaveFileAsync())?.Path;
        }).ConfigureAwait(true);
        if (picked.Failed) return new { ok = false, message = "The file picker could not open." };
        if (picked.Path is null) return new { ok = false, cancelled = true };
        await File.WriteAllTextAsync(picked.Path,
            "Your Exo recovery code" + Environment.NewLine + Environment.NewLine + code + Environment.NewLine + Environment.NewLine +
            "This is the only way back into your Exo account if you forget your password. Exo does not keep it." + Environment.NewLine);
        return new { ok = true };
    }

    /// <summary>The account step has been shown on this PC: an upgrader is not asked again.</summary>
    private object ExoAccountMarkAsked()
    {
        _services.Settings.UpdateProfile(settings => settings.AccountAsked = true);
        return new { ok = true };
    }

    private static (string? Email, string? Password) AccountCredentials(JsonElement p, bool hasParams) =>
        (ReadString(p, hasParams, "email"), ReadString(p, hasParams, "password"));

    /// <summary>Settings › Account › Sync now: one pull and push at once, then the overview as it stands after it.</summary>
    private async Task<object> ExoAccountSyncAsync()
    {
        await AccountSyncHooks.SyncNowAsync().ConfigureAwait(true);
        return ExoAccountOverview();
    }

    /// <summary>Settings > Account's overview: who is signed in, the sync status (last sync, what waits, what is held and why).</summary>
    private object ExoAccountOverview()
    {
        var session = AccountSessionStore.Load(PathHelper.AppDataDir);
        var status = AccountSyncHooks.Current?.Status(session) ?? new AccountSyncHost.AccountSyncStatus(null, 0, [], false, false);
        return new
        {
            signedIn = session is not null,
            codePending = session?.CodePending == true,
            sessionEnded = session is null && AccountSessionStore.Ended(PathHelper.AppDataDir),
            email = session?.Email,
            accountId = session?.AccountId,
            accountAsked = _services.Settings.Current.AccountAsked,
            sync = new
            {
                lastSyncAt = status.LastSyncAt?.ToString("O"),
                pending = status.Pending,
                held = status.Held.Select(item => new { key = item.Key, reason = item.Reason }).ToArray(),
                signedOut = status.SignedOut,
                pushStopped = status.PushStopped,
            },
        };
    }

    private async Task<object> ExoAccountSessionsAsync()
    {
        var (outcome, rows) = await ExoAccountSettings.SessionsAsync(PathHelper.AppDataDir);
        return new
        {
            ok = outcome.Ok,
            message = outcome.Message,
            error = outcome.Error,
            // The service keeps no PC names (CLIENT.md gap 1): this PC shows its own name, the others show none.
            sessions = rows.Select(row => new { device = row.Device, created = row.Created, lastUsed = row.LastUsed, current = row.Current, thisPc = row.ThisPc, name = row.Name ?? (row.ThisPc ? Environment.MachineName : null) }).ToArray(),
        };
    }

    private async Task<object> ExoAccountRevokeAsync(JsonElement p, bool hasParams) =>
        await ExoAccountSettings.RevokeAsync(PathHelper.AppDataDir, ReadString(p, hasParams, "device") ?? "");

    private async Task<object> ExoAccountChangePasswordAsync(JsonElement p, bool hasParams)
    {
        var current = ReadString(p, hasParams, "current") ?? "";
        var next = ReadString(p, hasParams, "password") ?? "";
        return await ExoAccountSettings.ChangePasswordAsync(PathHelper.AppDataDir, current, next);
    }

    private async Task<object> ExoAccountNewRecoveryCodeAsync(JsonElement p, bool hasParams) =>
        await ExoAccountSettings.NewRecoveryCodeAsync(PathHelper.AppDataDir, ReadString(p, hasParams, "password") ?? "");

    private async Task<object> ExoAccountDeleteAsync(JsonElement p, bool hasParams)
    {
        var outcome = await ExoAccountSettings.DeleteAsync(PathHelper.AppDataDir, ReadString(p, hasParams, "password") ?? "");
        // The account is gone: its queue and cursor go with it. Local settings and playtime stay on this PC.
        if (outcome.Ok) AccountSyncHooks.Current?.Forget();
        return outcome;
    }

    private async Task<object> ExoAccountSignOutAsync()
    {
        var outcome = await ExoAccountSettings.SignOutAsync(PathHelper.AppDataDir);
        AccountSyncHooks.Current?.Forget();
        return outcome;
    }


    private async Task<object> ExoAccountResetAsync(JsonElement p, bool hasParams) =>
        await ExoAccountSettings.ResetAsync(PathHelper.AppDataDir,
            ReadString(p, hasParams, "email") ?? "", ReadString(p, hasParams, "code") ?? "", ReadString(p, hasParams, "password") ?? "");

    /// <summary>Export my data: every page of the account, written as one JSON file the person picks (CLIENT.md 5).</summary>
    private async Task<object> ExoAccountExportAsync()
    {
        var result = await ExoAccountSettings.ExportAsync(PathHelper.AppDataDir, DateTimeOffset.UtcNow);
        if (!result.Ok || result.File is null) return new { ok = false, message = AccountMessages.For(result.Error) };
        var picked = await OnUiThread(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow!));
            picker.FileTypeChoices.Add("JSON", [".json"]);
            picker.SuggestedFileName = "Exo account export";
            return (await picker.PickSaveFileAsync())?.Path;
        }).ConfigureAwait(true);
        if (picked.Failed) return new { ok = false, message = "The file picker could not open." };
        if (picked.Path is null) return new { ok = false, cancelled = true };
        await File.WriteAllTextAsync(picked.Path, result.File.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return new { ok = true };
    }
}
