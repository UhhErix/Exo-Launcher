namespace ExoLauncher.Services.SteamSignIn;

/// <summary>What Steam asks the signing-in computer to confirm (<c>EAuthSessionGuardType</c>).</summary>
internal enum SteamGuardKind
{
    None = 1,
    EmailCode = 2,
    DeviceCode = 3,
    DeviceConfirmation = 4,
    EmailConfirmation = 5,
    MachineToken = 6,
}

/// <summary>
/// The <c>IAuthenticationService</c> and token messages, encoded and read with <see cref="SteamProtoWriter"/>. Field
/// numbers follow Valve's <c>steammessages_auth.steamclient.proto</c> as Irori's proto.rs has them (MIT).
/// </summary>
internal static class SteamAuthMessages
{
    internal const int PlatformSteamClient = 1;
    internal const int PersistencePersistent = 1;
    internal const int GamingDeviceStandardPc = 1;
    internal const int OsTypeWindows10 = 16;
    internal const string WebsiteId = "Client";

    internal sealed record Confirmation(SteamGuardKind Kind, string? Message);

    internal sealed record QrStart(ulong ClientId, string ChallengeUrl, byte[] RequestId, float Interval, IReadOnlyList<Confirmation> Confirmations);

    internal sealed record CredentialsStart(ulong ClientId, byte[] RequestId, float Interval, ulong SteamId, IReadOnlyList<Confirmation> Confirmations);

    internal sealed record PollAnswer(ulong NewClientId, string NewChallengeUrl, string RefreshToken, string AccessToken, string AccountName, string GuardData);

    internal sealed record RsaKey(string Modulus, string Exponent, ulong Timestamp);

    /// <summary>The device the sign-in claims to be. Steam shows its name in the mobile app's approval prompt.</summary>
    internal static byte[] DeviceDetails(string deviceName, byte[] machineId) =>
        new SteamProtoWriter()
            .String(1, deviceName)
            .Int32(2, PlatformSteamClient)
            .Int32(3, OsTypeWindows10)
            .Varint(4, GamingDeviceStandardPc)
            .Bytes(6, machineId)
            .ToArray();

    internal static byte[] BeginQrRequest(string deviceName, byte[] machineId) =>
        new SteamProtoWriter()
            .String(1, deviceName)
            .Int32(2, PlatformSteamClient)
            .Message(3, DeviceDetails(deviceName, machineId))
            .String(4, WebsiteId)
            .ToArray();

    internal static QrStart ReadQrStart(byte[] message)
    {
        var r = new SteamProtoReader(message);
        return new QrStart(r.Varint(1), r.String(2), r.Bytes(3), r.Float(4), Confirmations(r, 5));
    }

    internal static byte[] PollRequest(ulong clientId, byte[] requestId) =>
        new SteamProtoWriter().Varint(1, clientId).Bytes(2, requestId).ToArray();

    internal static PollAnswer ReadPoll(byte[] message)
    {
        var r = new SteamProtoReader(message);
        return new PollAnswer(r.Varint(1), r.String(2), r.String(3), r.String(4), r.String(6), r.String(7));
    }

    internal static byte[] RsaKeyRequest(string accountName) =>
        new SteamProtoWriter().String(1, accountName).ToArray();

    internal static RsaKey ReadRsaKey(byte[] message)
    {
        var r = new SteamProtoReader(message);
        return new RsaKey(r.String(1), r.String(2), r.Varint(3));
    }

    internal static byte[] BeginCredentialsRequest(
        string deviceName, string accountName, string encryptedPasswordBase64, ulong encryptionTimestamp,
        byte[] machineId, string guardData) =>
        new SteamProtoWriter()
            .String(1, deviceName)
            .String(2, accountName)
            .String(3, encryptedPasswordBase64)
            .Varint(4, encryptionTimestamp)
            .Bool(5, true)
            .Int32(6, PlatformSteamClient)
            .Int32(7, PersistencePersistent)
            .String(8, WebsiteId)
            .Message(9, DeviceDetails(deviceName, machineId))
            .String(10, guardData)
            .ToArray();

    internal static CredentialsStart ReadCredentialsStart(byte[] message)
    {
        var r = new SteamProtoReader(message);
        return new CredentialsStart(r.Varint(1), r.Bytes(2), r.Float(3), r.Varint(5), Confirmations(r, 4));
    }

    internal static byte[] GuardCodeRequest(ulong clientId, ulong steamId, string code, SteamGuardKind kind) =>
        new SteamProtoWriter()
            .Varint(1, clientId)
            .Fixed64(2, steamId)
            .String(3, code)
            .Int32(4, (int)kind)
            .ToArray();

    internal static byte[] AccessTokenRequest(string refreshToken, ulong steamId) =>
        new SteamProtoWriter().String(1, refreshToken).Fixed64(2, steamId).ToArray();

    /// <summary>The new access token (field 1) of <c>GenerateAccessTokenForApp</c>.</summary>
    internal static string ReadAccessToken(byte[] message) => new SteamProtoReader(message).String(1);

    /// <summary>Permanent revoke (<c>EAuthTokenRevokeAction</c> 1), so a copy of the refresh token stops working.</summary>
    internal static byte[] RevokeRequest(string refreshToken) =>
        new SteamProtoWriter().String(1, refreshToken).Int32(2, 1).ToArray();

    internal static SteamGuardKind KindOf(int code) =>
        Enum.IsDefined(typeof(SteamGuardKind), code) ? (SteamGuardKind)code : SteamGuardKind.None;

    /// <summary>The ways Steam accepts to confirm: field 5 of the QR answer, field 4 of the password one.</summary>
    private static IReadOnlyList<Confirmation> Confirmations(SteamProtoReader r, int field) =>
        r.Messages(field).Select(c =>
        {
            var inner = new SteamProtoReader(c);
            var message = inner.String(2);
            return new Confirmation(KindOf((int)inner.Varint(1)), string.IsNullOrEmpty(message) ? null : message);
        }).ToList();
}
