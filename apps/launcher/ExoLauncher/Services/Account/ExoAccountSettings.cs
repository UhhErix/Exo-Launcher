using System.Text.Json.Nodes;

namespace ExoLauncher.Services.Account;

/// <summary>One signed-in PC of the account, as the service lists it. The service cannot name a PC, so the page says "Another PC".</summary>
internal sealed record AccountSessionRow(string Device, long Created, long LastUsed, bool Current, bool ThisPc = false);

/// <summary>
/// Settings > Account's actions (CLIENT.md 5). Each one sends the session token (held here, never to the page) and answers
/// with a sentence and the flags the page needs. A signed-out PC answers signed-out.
/// </summary>
internal static class ExoAccountSettings
{
    private static ExoAccountOutcome SignedOut() =>
        new(false, AccountMessages.For("signed-out"), "signed-out", null, false, null);

    /// <summary>A refused request's sentence; a session the service ended also goes from this PC, so the account sheet asks again.</summary>
    private static ExoAccountOutcome Refused(string dataDir, AccountSession session, AccountReply reply)
    {
        if (reply.Error == "signed-out") AccountSessionStore.EndIfCurrent(dataDir, session.Token);
        return ExoAccountOutcome.From(reply);
    }

    /// <summary>Signs this PC out. Local data and the queue stay; this session's token file goes whatever the service answered.</summary>
    internal static async Task<ExoAccountOutcome> SignOutAsync(string dataDir, HttpMessageHandler? handler = null)
    {
        var session = AccountSessionStore.Load(dataDir);
        if (session is not null)
        {
            using var client = new ExoAccountClient(handler);
            await client.SignOutAsync(session.Token);
            // Only the session that was signed out goes: a sign-in that landed during the call keeps its new one.
            AccountSessionStore.ClearIfCurrent(dataDir, session.Token);
        }
        return ExoAccountOutcome.Done();
    }

    internal static async Task<(ExoAccountOutcome Outcome, IReadOnlyList<AccountSessionRow> Rows)> SessionsAsync(string dataDir, HttpMessageHandler? handler = null)
    {
        var session = AccountSessionStore.Load(dataDir);
        if (session is null) return (SignedOut(), []);
        using var client = new ExoAccountClient(handler);
        var reply = await client.ListSessionsAsync(session.Token);
        if (!reply.Ok || reply.Body is not JsonObject body) return (Refused(dataDir, session, reply), []);
        var rows = (body["sessions"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(row => new AccountSessionRow(
                row["device"]?.GetValue<string>() ?? "",
                row["created"]?.GetValue<long>() ?? 0,
                row["lastUsed"]?.GetValue<long>() ?? 0,
                row["current"]?.GetValue<bool>() == true))
            .Where(row => AccountDevice.IsValid(row.Device))
            .ToList();
        return (ExoAccountOutcome.Done(), AccountDevices.Merge(rows, AccountDevice.Id(dataDir)));
    }

    /// <summary>Ends another PC's sign-in. This PC's own sign-in ends with Sign out, not here.</summary>
    internal static async Task<ExoAccountOutcome> RevokeAsync(string dataDir, string device, HttpMessageHandler? handler = null)
    {
        if (!AccountDevice.IsValid(device))
            return new ExoAccountOutcome(false, AccountMessages.Generic, "bad-request", null, false, null);
        if (device == AccountDevice.Id(dataDir))
            return new ExoAccountOutcome(false, "This PC's sign-in ends with Sign out.", null, null, false, null);
        var session = AccountSessionStore.Load(dataDir);
        if (session is null) return SignedOut();
        using var client = new ExoAccountClient(handler);
        var reply = await client.RemoveSessionAsync(session.Token, device);
        return reply.Ok ? ExoAccountOutcome.Done() : Refused(dataDir, session, reply);
    }

    /// <summary>Changes the password. Every other PC is signed out; this one keeps its token.</summary>
    internal static async Task<ExoAccountOutcome> ChangePasswordAsync(string dataDir, string current, string next, HttpMessageHandler? handler = null)
    {
        var session = AccountSessionStore.Load(dataDir);
        if (session is null) return SignedOut();
        using var client = new ExoAccountClient(handler);
        var reply = await client.ChangePasswordAsync(session.Token, current, next, session.DeviceToken);
        ExoAccountService.KeepDeviceToken(dataDir, reply);
        return reply.Ok ? ExoAccountOutcome.Done() : Refused(dataDir, session, reply);
    }

    /// <summary>A new recovery code. The old one stops working; the new one is shown once.</summary>
    internal static async Task<ExoAccountOutcome> NewRecoveryCodeAsync(string dataDir, string password, HttpMessageHandler? handler = null)
    {
        var session = AccountSessionStore.Load(dataDir);
        if (session is null) return SignedOut();
        using var client = new ExoAccountClient(handler);
        var reply = await client.NewRecoveryCodeAsync(session.Token, password, session.DeviceToken);
        ExoAccountService.KeepDeviceToken(dataDir, reply);
        if (!reply.Ok || reply.Body?["recoveryCode"]?.GetValue<string>() is not { } code) return Refused(dataDir, session, reply);
        ExoAccountService.CodeIssued(dataDir);
        return ExoAccountOutcome.Done(code);
    }

    /// <summary>Deletes the account and its items. The data on this PC stays; the token is removed.</summary>
    internal static async Task<ExoAccountOutcome> DeleteAsync(string dataDir, string password, HttpMessageHandler? handler = null)
    {
        var session = AccountSessionStore.Load(dataDir);
        if (session is null) return SignedOut();
        using var client = new ExoAccountClient(handler);
        var reply = await client.DeleteAccountAsync(session.Token, password, session.DeviceToken);
        if (!reply.Ok) return Refused(dataDir, session, reply);
        AccountSessionStore.Clear(dataDir);
        return ExoAccountOutcome.Done();
    }

    /// <summary>Resets a forgotten password with the recovery code. Every PC is signed out; a new code comes back once.</summary>
    internal static async Task<ExoAccountOutcome> ResetAsync(string dataDir, string email, string code, string password, HttpMessageHandler? handler = null)
    {
        using var client = new ExoAccountClient(handler);
        var reply = await client.ResetAsync(email.Trim(), code.Trim(), password, AccountDevice.Id(dataDir), ExoAccountService.DeviceTokenFor(dataDir));
        return ExoAccountService.Finish(dataDir, reply, email.Trim(), expectsCode: true);
    }

    /// <summary>Every page of the account's data, as the export file (CLIENT.md 7.7).</summary>
    internal static async Task<AccountExport.Result> ExportAsync(string dataDir, DateTimeOffset now, HttpMessageHandler? handler = null)
    {
        var session = AccountSessionStore.Load(dataDir);
        if (session is null) return new AccountExport.Result(false, "signed-out", null);
        using var client = new ExoAccountClient(handler);
        var result = await AccountExport.CollectAsync(client, session.Token, now);
        if (result.Error == "signed-out") AccountSessionStore.EndIfCurrent(dataDir, session.Token);
        return result;
    }
}
