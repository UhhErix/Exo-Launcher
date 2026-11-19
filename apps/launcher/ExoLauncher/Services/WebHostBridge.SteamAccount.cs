using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services.SteamSignIn;

namespace ExoLauncher.Services;

/// <summary>
/// Settings › Library › Steam's sign-in with the account's own token (no Web API key): a QR code scanned in the Steam
/// mobile app, or the account name and password with Steam Guard. The UI gets each step as a plain state
/// (<see cref="SteamSignInService.SignInStep"/>); the tokens and the password never reach it or the log. The pending
/// sign-in is held in memory by <see cref="SteamSignInRuntime"/>.
/// </summary>
public sealed partial class WebHostBridge
{
    private static object SteamAccountState()
    {
        var state = SteamSignInRuntime.Service.State();
        return new { signedIn = state.SignedIn, accountName = state.AccountName };
    }

    private async Task<object> SteamSignInQrAsync(CancellationToken ct) =>
        await SteamSignInCallAsync(service => service.BeginQrAsync(ct), ct);

    private static object SteamSignInCancel()
    {
        SteamSignInRuntime.Service.Cancel();
        return new { ok = true };
    }

    private async Task<object> SteamSignInPollAsync(CancellationToken ct) =>
        await SteamSignInCallAsync(service => service.PollAsync(ct), ct);

    private async Task<object> SteamSignInPasswordAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var account = ReadString(p, hasParams, "account") ?? "";
        var password = ReadString(p, hasParams, "password") ?? "";
        return await SteamSignInCallAsync(service => service.BeginPasswordAsync(account, password, ct), ct);
    }

    private async Task<object> SteamSignInGuardAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var code = ReadString(p, hasParams, "code") ?? "";
        return await SteamSignInCallAsync(service => service.SubmitGuardAsync(code, ct), ct);
    }

    private async Task<object> SteamSignOutAsync(CancellationToken ct)
    {
        await SteamSignInRuntime.Service.SignOutAsync(ct).ConfigureAwait(false);
        return new { ok = true };
    }

    /// <summary>Runs one sign-in step and turns a refusal or a network failure into a plain line; never a stack or a token.</summary>
    private static async Task<object> SteamSignInCallAsync(
        Func<SteamSignInService, Task<SteamSignInService.SignInStep>> step, CancellationToken ct)
    {
        try
        {
            var result = await step(SteamSignInRuntime.Service).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SteamAuthException ex)
        {
            return new SteamSignInService.SignInStep("failed", Message: ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or FormatException or JsonException or InvalidOperationException)
        {
            AppLog.Debug("Steam sign-in step failed: " + ex.GetType().Name);
            return new SteamSignInService.SignInStep("failed", Message: "Could not reach Steam. Try again.");
        }
    }
}
