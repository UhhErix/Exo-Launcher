using ExoLauncher.Helpers;

namespace ExoLauncher.Services.Account;

/// <summary>
/// Create and sign in for the Exo account, as the onboarding account step and Settings > Account call them. The password
/// goes to the service once and is never kept; the token goes to the DPAPI file; the recovery code goes back to the screen
/// once.
/// </summary>
internal static class ExoAccountService
{
    internal static async Task<ExoAccountOutcome> CreateAsync(string dataDir, string email, string password, HttpMessageHandler? handler = null)
    {
        using var client = new ExoAccountClient(handler);
        var reply = await client.CreateAccountAsync(email.Trim(), password, AccountDevice.Id(dataDir));
        return Finish(dataDir, reply, email.Trim(), expectsCode: true);
    }

    internal static async Task<ExoAccountOutcome> SignInAsync(string dataDir, string email, string password, HttpMessageHandler? handler = null)
    {
        using var client = new ExoAccountClient(handler);
        var reply = await client.SignInAsync(email.Trim(), password, AccountDevice.Id(dataDir), DeviceTokenFor(dataDir));
        return Finish(dataDir, reply, email.Trim());
    }

    /// <summary>This PC's stored device token, or null when signed out (sign-out removes it, CLIENT.md 3).</summary>
    internal static string? DeviceTokenFor(string dataDir) => AccountSessionStore.Load(dataDir)?.DeviceToken;

    /// <summary>An answer that carries a device token (password change, new code) replaces the stored one; an answer without one changes nothing.</summary>
    internal static void KeepDeviceToken(string dataDir, AccountReply reply)
    {
        if (!reply.Ok || reply.Body?["deviceToken"]?.GetValue<string>() is not { Length: > 0 } token) return;
        AccountSessionStore.Update(dataDir, session => session with { DeviceToken = token });
    }

    /// <summary>
    /// The player typed the recovery code back (RecoveryCodeScreen): the account sheet may go. Until then the session
    /// carries <see cref="AccountSession.CodePending"/>, so quitting on the code screen brings a new code at the next start.
    /// </summary>
    internal static void ConfirmCode(string dataDir) =>
        AccountSessionStore.Update(dataDir, session => session with { CodePending = false });

    /// <summary>A new recovery code was issued for this account (Settings): it must be confirmed like the first one.</summary>
    internal static void CodeIssued(string dataDir) =>
        AccountSessionStore.Update(dataDir, session => session with { CodePending = true });

    /// <summary>Who is signed in on this PC, and whether the account step has been shown (for upgraders).</summary>
    internal static object State(string dataDir, bool accountAsked)
    {
        var session = AccountSessionStore.Load(dataDir);
        return new { signedIn = session is not null, email = session?.Email, accountId = session?.AccountId, accountAsked };
    }

    /// <summary>Saves the session from a sign-in answer. A sign-up or reset (<paramref name="expectsCode"/>) leaves the code to confirm, even when the answer lacked it.</summary>
    internal static ExoAccountOutcome Finish(string dataDir, AccountReply reply, string email, bool expectsCode = false)
    {
        var signIn = ExoAccountClient.ReadSignIn(reply);
        if (signIn is null) return ExoAccountOutcome.From(reply);
        // A sign-up or a reset brings a recovery code, shown once: the session says it waits to be confirmed (A4).
        AccountSessionStore.Save(dataDir, new AccountSession(signIn.Token, signIn.AccountId, email, signIn.DeviceToken, CodePending: expectsCode || signIn.RecoveryCode is not null));
        return ExoAccountOutcome.Done(signIn.RecoveryCode);
    }
}
