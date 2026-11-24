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

    internal sealed record PasskeyRow(string Id, string Label, long Created, long LastUsed, bool Flagged);

    internal sealed record PasskeyStatus(bool Supported, uint ApiVersion, bool PlatformAvailable, IReadOnlyList<PasskeyRow> Passkeys);

    internal static async Task<PasskeyStatus> PasskeyStatusAsync(string dataDir, HttpMessageHandler? handler = null, Uri? service = null)
    {
        var avail = WindowsWebAuthn.Probe();
        var session = AccountSessionStore.Load(dataDir);
        if (session is null)
            return new PasskeyStatus(avail.Supported, avail.ApiVersion, avail.PlatformAvailable, []);
        using var client = new ExoAccountClient(handler, service);
        var reply = await client.ListPasskeysAsync(session.Token);
        var rows = new List<PasskeyRow>();
        if (reply.Ok && reply.Body?["passkeys"] is System.Text.Json.Nodes.JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not System.Text.Json.Nodes.JsonObject obj) continue;
                var id = obj["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) continue;
                rows.Add(new PasskeyRow(
                    id,
                    obj["label"]?.GetValue<string>() ?? "Windows passkey",
                    obj["created"]?.GetValue<long>() ?? 0,
                    obj["lastUsed"]?.GetValue<long>() ?? 0,
                    obj["flagged"]?.GetValue<bool>() ?? false));
            }
        }
        return new PasskeyStatus(avail.Supported, avail.ApiVersion, avail.PlatformAvailable, rows);
    }

    internal static async Task<ExoAccountOutcome> RegisterPasskeyAsync(
        string dataDir, nint hwnd, string? label, string? password = null, HttpMessageHandler? handler = null, Uri? service = null)
    {
        var session = AccountSessionStore.Load(dataDir);
        if (session is null) return new ExoAccountOutcome(false, AccountMessages.For("signed-out"), "signed-out", null, false, null);
        using var client = new ExoAccountClient(handler, service);
        var start = await client.PasskeyRegisterStartAsync(session.Token, password, DeviceTokenFor(dataDir));
        if (!start.Ok || start.Body is not System.Text.Json.Nodes.JsonObject obj)
            return ExoAccountOutcome.From(start);
        var challenge = obj["challenge"]?.GetValue<string>() ?? "";
        var rpId = obj["rp"]?["id"]?.GetValue<string>() ?? "accounts.getexo.app";
        var rpName = obj["rp"]?["name"]?.GetValue<string>() ?? "Exo Account";
        var origin = obj["origin"]?.GetValue<string>() ?? ("https://" + rpId);
        var userId = obj["user"]?["id"]?.GetValue<string>() ?? "";
        var exclude = ReadStringArray(obj["excludeCredentials"]);

        var proof = await Task.Run(() => WindowsWebAuthn.MakeCredential(
            hwnd, rpId, rpName, origin, challenge, userId,
            string.IsNullOrWhiteSpace(session.Email) ? session.AccountId : session.Email,
            exclude));
        if (!proof.Ok || proof.Value is null)
            return new ExoAccountOutcome(false, proof.Cancelled ? "Passkey setup was cancelled." : AccountMessages.For("bad-passkey"), proof.Error ?? "bad-passkey", null, false, null);

        var finish = await client.PasskeyRegisterFinishAsync(
            session.Token,
            challenge,
            proof.Value.ClientDataJson,
            proof.Value.AttestationObject,
            string.IsNullOrWhiteSpace(label) ? Environment.MachineName : label.Trim());
        return finish.Ok ? ExoAccountOutcome.Done() : ExoAccountOutcome.From(finish);
    }

    internal static async Task<ExoAccountOutcome> SignInWithPasskeyAsync(
        string dataDir, nint hwnd, string? email, HttpMessageHandler? handler = null, Uri? service = null)
    {
        using var client = new ExoAccountClient(handler, service);
        var start = await client.PasskeySignInStartAsync(email);
        if (!start.Ok || start.Body is not System.Text.Json.Nodes.JsonObject obj)
            return ExoAccountOutcome.From(start);
        var challenge = obj["challenge"]?.GetValue<string>() ?? "";
        var rpId = obj["rpId"]?.GetValue<string>() ?? "accounts.getexo.app";
        var origin = obj["origin"]?.GetValue<string>() ?? ("https://" + rpId);
        var allow = ReadStringArray(obj["allowCredentials"]);

        var proof = await Task.Run(() => WindowsWebAuthn.GetAssertion(hwnd, rpId, origin, challenge, allow));
        if (!proof.Ok || proof.Value is null)
            return new ExoAccountOutcome(false, proof.Cancelled ? "Passkey sign-in was cancelled." : AccountMessages.For("bad-passkey"), proof.Error ?? "bad-passkey", null, false, null);

        var finish = await client.PasskeySignInFinishAsync(
            challenge,
            AccountDevice.Id(dataDir),
            proof.Value.CredentialId,
            proof.Value.ClientDataJson,
            proof.Value.AuthenticatorData,
            proof.Value.Signature);
        var resolvedEmail = finish.Body?["account"]?["email"]?.GetValue<string>() ?? email?.Trim() ?? "";
        return Finish(dataDir, finish, resolvedEmail);
    }

    internal static async Task<ExoAccountOutcome> DeletePasskeyAsync(
        string dataDir, string credentialId, HttpMessageHandler? handler = null, Uri? service = null)
    {
        var session = AccountSessionStore.Load(dataDir);
        if (session is null) return new ExoAccountOutcome(false, AccountMessages.For("signed-out"), "signed-out", null, false, null);
        using var client = new ExoAccountClient(handler, service);
        var reply = await client.DeletePasskeyAsync(session.Token, credentialId);
        return reply.Ok ? ExoAccountOutcome.Done() : ExoAccountOutcome.From(reply);
    }

    private static List<string> ReadStringArray(System.Text.Json.Nodes.JsonNode? node)
    {
        var list = new List<string>();
        if (node is not System.Text.Json.Nodes.JsonArray arr) return list;
        foreach (var item in arr)
            if (item?.GetValue<string>() is { Length: > 0 } s) list.Add(s);
        return list;
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
