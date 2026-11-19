using System.Security.Cryptography;

namespace Exo.Identity;

/// <summary>
/// The client's half of the mailbox Worker's protocol (cloud/mailbox/README.md, plan §2.6): a box is named by the
/// first 16 bytes of SHA-256 of its P-256 public key, and whoever holds the private key logs in by signing
/// "exo-box-auth/1" || box id (16 bytes) || the Worker's 32-byte challenge. The Worker stores ciphertext only. The
/// shared sync box's key is derived from the sync key, so every linked device can open it and nobody else can;
/// a box of an app's own from its sub-key (§2.11); the recovery box's from the recovery code.
/// </summary>
public static class Mailbox
{
    public const int ChallengeBytes = 32;

    // Frames: the first byte says what it is. To the client:
    public const byte ChallengeFrame = 0x10, ReadyFrame = 0x11, MessageFrame = 0x12, StoredFrame = 0x13, CaughtUpFrame = 0x14;
    // From the client:
    public const byte AuthFrame = 0x20, PutFrame = 0x21, SinceFrame = 0x22, TrimFrame = 0x23;
    // The link rendezvous' own, to either PC: gone is the other PC leaving, crowded a third one coming (start again).
    public const byte NameplateFrame = 0x30, JoinedFrame = 0x31, GoneFrame = 0x32, CrowdedFrame = 0x33;

    /// <summary>The box id as text: 32 lowercase hex, the form the URL path takes. Whatever is signed or used as
    /// additional data (the login, the envelope, the recovery kit) holds its 16 raw bytes instead.</summary>
    public static string BoxId(ReadOnlySpan<byte> publicKey) => Convert.ToHexStringLower(SHA256.HashData(publicKey).AsSpan(0, 16));

    /// <summary>The bytes a box key signs to log in.</summary>
    public static byte[] AuthMessage(string boxId, ReadOnlySpan<byte> challenge) =>
        [.. "exo-box-auth/1"u8, .. Convert.FromHexString(boxId), .. challenge];

    /// <summary>The answer to a challenge: 0x20, the box's public key, its signature.</summary>
    public static byte[] Auth(IDeviceSigner boxKey, ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != ChallengeBytes) throw new ArgumentException("A challenge is 32 bytes.", nameof(challenge));
        return [AuthFrame, .. boxKey.PublicKey, .. boxKey.Sign(AuthMessage(BoxId(boxKey.PublicKey), challenge))];
    }

    /// <summary>A sync box's private key: from the sync key for the shared box, or from an app's own sub-key for a box of
    /// its own. A new sync key, so a new box, at every device removal.</summary>
    public static byte[] BoxScalar(ReadOnlySpan<byte> key) => P256.DeriveScalar(key, "exo sync box v1"u8);

    /// <summary>A test copy (<see cref="IdentityStore.IsTest"/>) talks only to a mailbox on this PC, never the real one.</summary>
    public static bool EndpointAllowed(Uri endpoint, bool testCopy) =>
        endpoint.Scheme is "wss" or "ws" && (!testCopy || endpoint.IsLoopback) && (endpoint.Scheme == "wss" || endpoint.IsLoopback);
}
