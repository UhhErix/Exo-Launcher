using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.SteamSignIn;

/// <summary>The account's saved tokens, as the library calls need them. Never sent to the UI.</summary>
internal sealed record SteamAccountTokens(string AccountName, ulong SteamId, string RefreshToken, string AccessToken, long AccessExpiresAt);

/// <summary>Where the saved tokens live: DPAPI-wrapped on this PC (<see cref="StoreSecretVault"/>), or memory in tests.</summary>
internal interface ISteamTokenVault
{
    string? Read();

    /// <summary>Stores the JSON, or clears it when <paramref name="json"/> is null.</summary>
    bool Write(string? json);
}

internal sealed class StoreSecretVault : ISteamTokenVault
{
    internal const string FileName = "steam-account.bin";

    public string? Read() => StoreSecretStore.TryRead(FileName, static text => text);

    public bool Write(string? json) => StoreSecretStore.Save(FileName, json, static text => text);
}

/// <summary>
/// The Steam sign-in that gives Exo the account's own library with no Web API key: a QR code scanned in the Steam
/// mobile app, or the account name and password with Steam Guard, then Steam's refresh and access tokens. Only the
/// tokens are kept (DPAPI-wrapped, <see cref="ISteamTokenVault"/>); the password is used once to encrypt it for Steam
/// and is never stored or logged. The access token is renewed from the refresh token before it expires.
/// </summary>
internal sealed class SteamSignInService(SteamWebApi api, ISteamTokenVault vault, Func<DateTimeOffset> clock, byte[] machineId, string deviceName)
{
    /// <summary>Renew the access token this long before Steam says it ends.</summary>
    internal static readonly TimeSpan RenewWithin = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _renew = new(1, 1);
    private readonly object _gate = new();
    private PendingSignIn? _pending;

    /// <summary>One sign-in in progress. Kept in memory only; a sign-in that ends or is cancelled drops it.</summary>
    private sealed record PendingSignIn(ulong ClientId, byte[] RequestId, ulong SteamId, string AccountName, float Interval, SteamGuardKind? Awaiting);

    /// <summary>What the UI shows next. <c>State</c> is one of waiting, guard, approve, done, expired, failed.</summary>
    internal sealed record SignInStep(string State, string? ChallengeUrl = null, string? Guard = null, string? AccountName = null, string? Message = null, int IntervalSeconds = 5);

    internal sealed record AccountState(bool SignedIn, string? AccountName, ulong SteamId);

    internal AccountState State()
    {
        var tokens = Saved();
        return tokens is null ? new AccountState(false, null, 0) : new AccountState(true, tokens.AccountName, tokens.SteamId);
    }

    internal async Task<SignInStep> BeginQrAsync(CancellationToken ct)
    {
        var start = await api.BeginQrAsync(deviceName, machineId, ct).ConfigureAwait(false);
        var seconds = Seconds(start.Interval);
        lock (_gate) _pending = new PendingSignIn(start.ClientId, start.RequestId, 0, "", start.Interval, null);
        return new SignInStep("waiting", ChallengeUrl: start.ChallengeUrl, IntervalSeconds: seconds);
    }

    /// <summary>Asks Steam whether the pending sign-in was approved (by QR scan or a phone approval).</summary>
    internal async Task<SignInStep> PollAsync(CancellationToken ct)
    {
        var pending = Pending();
        if (pending is null) return Failed("No sign-in is running.");
        SteamAuthMessages.PollAnswer answer;
        try
        {
            answer = await api.PollAsync(pending.ClientId, pending.RequestId, ct).ConfigureAwait(false);
        }
        catch (SteamAuthException ex) when (ex.EResult == 9)
        {
            Cancel();
            return new SignInStep("expired", Message: ex.Message);
        }

        if (answer.RefreshToken.Length == 0)
        {
            var challenge = answer.NewChallengeUrl.Length > 0 ? answer.NewChallengeUrl : null;
            var clientId = answer.NewClientId != 0 ? answer.NewClientId : pending.ClientId;
            lock (_gate) _pending = pending with { ClientId = clientId };
            return new SignInStep("waiting", ChallengeUrl: challenge, IntervalSeconds: Seconds(pending.Interval));
        }

        var steamId = SteamTokenClaims.Parse(answer.RefreshToken).SteamId;
        var access = answer.AccessToken.Length > 0
            ? answer.AccessToken
            : await api.GenerateAccessTokenAsync(answer.RefreshToken, steamId, ct).ConfigureAwait(false);
        var name = answer.AccountName.Length > 0 ? answer.AccountName : pending.AccountName;
        Save(new SteamAccountTokens(name, steamId, answer.RefreshToken, access, SteamTokenClaims.Parse(access).ExpiresAt));
        Cancel();
        return new SignInStep("done", AccountName: name);
    }

    /// <summary>The account name and password sign-in. The password is encrypted for Steam here and dropped.</summary>
    internal async Task<SignInStep> BeginPasswordAsync(string accountName, string password, CancellationToken ct)
    {
        var account = accountName.Trim();
        if (account.Length == 0 || password.Length == 0) return Failed("Enter your Steam account name and password.");
        var key = await api.RsaKeyAsync(account, ct).ConfigureAwait(false);
        var encrypted = SteamPasswordCrypto.Encrypt(key.Modulus, key.Exponent, password);
        var start = await api.BeginCredentialsAsync(deviceName, account, encrypted, key.Timestamp, machineId, "", ct).ConfigureAwait(false);
        var awaiting = start.Confirmations.Select(c => c.Kind).FirstOrDefault(k => k is SteamGuardKind.DeviceCode or SteamGuardKind.EmailCode or SteamGuardKind.DeviceConfirmation);
        lock (_gate) _pending = new PendingSignIn(start.ClientId, start.RequestId, start.SteamId, account, start.Interval, awaiting == 0 ? null : awaiting);
        return awaiting switch
        {
            SteamGuardKind.DeviceCode or SteamGuardKind.EmailCode => new SignInStep("guard", Guard: awaiting.ToString(), AccountName: account, IntervalSeconds: Seconds(start.Interval)),
            SteamGuardKind.DeviceConfirmation => new SignInStep("approve", AccountName: account, IntervalSeconds: Seconds(start.Interval)),
            _ => new SignInStep("waiting", AccountName: account, IntervalSeconds: Seconds(start.Interval)),
        };
    }

    /// <summary>Sends the Steam Guard code the user typed for the pending password sign-in.</summary>
    internal async Task<SignInStep> SubmitGuardAsync(string code, CancellationToken ct)
    {
        var pending = Pending();
        if (pending is null || pending.Awaiting is not (SteamGuardKind.DeviceCode or SteamGuardKind.EmailCode))
            return Failed("No Steam Guard code is needed right now.");
        if (code.Trim().Length == 0) return Failed("Enter the Steam Guard code.");
        await api.SubmitGuardCodeAsync(pending.ClientId, pending.SteamId, code, pending.Awaiting.Value, ct).ConfigureAwait(false);
        lock (_gate) _pending = pending with { Awaiting = null };
        return new SignInStep("waiting", AccountName: pending.AccountName, IntervalSeconds: Seconds(pending.Interval));
    }

    internal void Cancel()
    {
        lock (_gate) _pending = null;
    }

    /// <summary>Forgets the tokens here and asks Steam to end the saved sign-in. Local removal happens even when Steam can't be reached.</summary>
    internal async Task SignOutAsync(CancellationToken ct)
    {
        Cancel();
        var saved = Saved();
        if (saved is not null)
        {
            try { await api.RevokeAsync(saved.RefreshToken, saved.AccessToken, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SteamAuthException)
            {
                AppLog.Debug("Steam sign-out was not confirmed: " + ex.GetType().Name);
            }
        }
        vault.Write(null);
    }

    /// <summary>The saved sign-in with a fresh access token, or null when none is saved or Steam no longer accepts it (then the tokens are cleared).</summary>
    internal async Task<SteamAccountTokens?> FreshTokensAsync(CancellationToken ct)
    {
        var saved = Saved();
        if (saved is null) return null;
        if (saved.AccessExpiresAt - clock().ToUnixTimeSeconds() > RenewWithin.TotalSeconds) return saved;
        await _renew.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            saved = Saved();
            if (saved is null) return null;
            if (saved.AccessExpiresAt - clock().ToUnixTimeSeconds() > RenewWithin.TotalSeconds) return saved;
            var access = await api.GenerateAccessTokenAsync(saved.RefreshToken, saved.SteamId, ct).ConfigureAwait(false);
            var renewed = saved with { AccessToken = access, AccessExpiresAt = SteamTokenClaims.Parse(access).ExpiresAt };
            Save(renewed);
            return renewed;
        }
        catch (SteamAuthException ex) when (ex.EResult is 0 or 9)
        {
            // Steam no longer takes the refresh token: the account is signed out until the user signs in again.
            vault.Write(null);
            return null;
        }
        finally
        {
            _renew.Release();
        }
    }

    /// <summary>
    /// The owned games and the account's Steam Family shares, with Exo's own Steam sign-in; null without one for
    /// <paramref name="steamId"/>.
    /// </summary>
    internal async Task<(SteamCommunityGames.OwnedGamesResult Owned, SteamFamilyLibrary.Shares? Family)?> LibraryAsync(ulong steamId, CancellationToken ct)
    {
        var tokens = await FreshTokensAsync(ct).ConfigureAwait(false);
        if (tokens is null || tokens.SteamId != steamId) return null;
        return await LibraryWithTokenAsync(api, tokens.AccessToken, steamId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The owned games and the Family shares with any access token for <paramref name="steamId"/> (Exo's sign-in, or
    /// the Steam website's). Family: <see cref="SteamFamilyLibrary.Shares.None"/> in no family (it clears what was
    /// remembered); null when Steam did not answer (what was remembered stands).
    /// </summary>
    internal static async Task<(SteamCommunityGames.OwnedGamesResult Owned, SteamFamilyLibrary.Shares? Family)> LibraryWithTokenAsync(
        SteamWebApi api, string accessToken, ulong steamId, CancellationToken ct)
    {
        var ownedTask = api.OwnedGamesAsync(accessToken, steamId, ct);
        var group = await api.FamilyGroupAsync(accessToken, steamId, ct).ConfigureAwait(false);
        SteamFamilyLibrary.Shares? family = group switch
        {
            null => null,
            0 => SteamFamilyLibrary.Shares.None,
            _ => await api.FamilySharedAppsAsync(accessToken, group.Value, steamId, ct).ConfigureAwait(false) is { } apps
                ? new SteamFamilyLibrary.Shares(group.Value, apps)
                : null,
        };
        return (await ownedTask.ConfigureAwait(false), family);
    }

    private PendingSignIn? Pending()
    {
        lock (_gate) return _pending;
    }

    private SteamAccountTokens? Saved()
    {
        var json = vault.Read();
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<SavedJson>(json);
            if (doc is null || string.IsNullOrEmpty(doc.Refresh) || string.IsNullOrEmpty(doc.Access)) return null;
            if (!ulong.TryParse(doc.SteamId, out var steamId) || steamId == 0) return null;
            return new SteamAccountTokens(doc.Account ?? "", steamId, doc.Refresh, doc.Access, SteamTokenClaims.Parse(doc.Access).ExpiresAt);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private void Save(SteamAccountTokens tokens)
    {
        var json = JsonSerializer.Serialize(new SavedJson(tokens.AccountName, tokens.SteamId.ToString(), tokens.RefreshToken, tokens.AccessToken));
        if (!vault.Write(json)) AppLog.Warn("Steam sign-in could not be saved on this PC.");
    }

    private sealed record SavedJson(string? Account, string? SteamId, string? Refresh, string? Access)
    {
        public SavedJson() : this(null, null, null, null) { }
    }

    private static SignInStep Failed(string message) => new("failed", Message: message);

    private static int Seconds(float interval) => interval > 0 ? (int)Math.Ceiling(interval) : 5;

    /// <summary>The Steam sign-in's own machine id: stable for this PC and Windows user, so Steam sees one device.</summary>
    internal static byte[] MachineIdFor(string machineName, string userName) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("exo-steam-device\0" + machineName + "\0" + userName)).AsSpan(0, 16).ToArray();
}

/// <summary>The claims Exo reads from a Steam token: the account's SteamID64 (<c>sub</c>) and when it ends (<c>exp</c>). Not verified; Steam is the one that accepts it.</summary>
internal readonly record struct SteamTokenClaims(ulong SteamId, long ExpiresAt)
{
    internal static SteamTokenClaims Parse(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) throw new FormatException("token");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        var root = doc.RootElement;
        var sub = root.GetProperty("sub");
        var steamId = sub.ValueKind == JsonValueKind.String ? ulong.Parse(sub.GetString()!) : sub.GetUInt64();
        var exp = root.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
        return new SteamTokenClaims(steamId, exp);
    }
}

/// <summary>The RSA step of Steam's password sign-in: PKCS#1 v1.5, as Steam's client does it (Irori auth.rs encrypt_password).</summary>
internal static class SteamPasswordCrypto
{
    internal static string Encrypt(string modulusHex, string exponentHex, string password)
    {
        var parameters = new RSAParameters
        {
            Modulus = Hex(modulusHex),
            Exponent = Hex(exponentHex),
        };
        using var rsa = RSA.Create(parameters);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1));
    }

    private static byte[] Hex(string text)
    {
        var clean = text.Trim();
        if (clean.Length % 2 == 1) clean = "0" + clean;
        return Convert.FromHexString(clean);
    }
}
