using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Exo.Identity;

/// <summary>
/// The one binary wrapper every Exo identity message travels in (plan §2.5), so the mailbox and the link rendezvous
/// only ever see a version, a kind, an epoch and a padded size:
/// <code>
/// kind 0x01 sync       02 01 epoch(4) nonce(12)  AES-256-GCM(padded)+tag(16)  signature(64)
/// kind 0x02 to-device  02 02 00000000 enc(65)    HPKE AES-128-GCM(padded)+tag  signature(64)
///                      (a recovery kit: 02 02 code number(4), plan §2.12)
/// kind 0x03 MLS        02 03 00000000 the MLS wire message as it is (no nonce, enc, tag or signature of its own)
/// kind 0x04 link       02 04 00000000 nonce(12)  AES-256-GCM(padded)+tag       (no signature)
/// AAD = "exo-env/1" || mailbox id (16) || bytes 0..5
/// </code>
/// The plaintext is <c>u32 length (big-endian) || data || zeros</c> padded to the next of <see cref="PadSizes"/>, and a
/// reader takes only that size; the top size is 64 KiB less the largest envelope's own bytes, so every envelope fits the
/// mailbox's 64 KiB message (plan §2.11). A sync envelope's data is a <see cref="SyncBatch"/>. The mailbox
/// id in the additional data is the box id's 16 raw bytes, never its hex. The signature is the sender's device identity key over every byte before it: AES-GCM proves someone in the
/// group wrote it, the signature which device, so a device removed in an earlier epoch is refused even if it kept an
/// old key. Opening checks the signature before decrypting.
/// </summary>
public static class Envelope
{
    public const byte Version = 0x02;
    public const byte SyncKind = 0x01, ToDeviceKind = 0x02, MlsKind = 0x03, LinkKind = 0x04;
    public const int MailboxIdBytes = 16, MaxBytes = 65536;
    private const int HeaderBytes = 6, NonceBytes = 12, TagBytes = 16, LengthBytes = 4;
    public const int MaxPadded = MaxBytes - HeaderBytes - Hpke.EncBytes - TagBytes - P256.SignatureBytes;
    public const int MaxData = MaxPadded - LengthBytes;
    public static readonly int[] PadSizes = [256, 512, 1024, 2048, 4096, 8192, 16384, 32768, MaxPadded];
    private static readonly byte[] AadLabel = "exo-env/1"u8.ToArray();
    private static readonly byte[] HpkeInfo = "exo-env/1 to-device"u8.ToArray();
    private static readonly byte[] SyncInfo = "exo sync v1/"u8.ToArray();

    /// <summary>A message opened: what was inside, and which of the candidate senders signed it.</summary>
    public sealed record Opened(byte[] Data, int Sender);

    /// <summary>The key one app's sync items are sealed with (plan §2.7): HKDF-Expand(sync key, "exo sync v1/" + app, 32).
    /// A sub-key can have a box of its own (§2.11).</summary>
    public static byte[] SyncSubKey(ReadOnlySpan<byte> syncKey, string app)
    {
        var key = new byte[32];
        HKDF.Expand(HashAlgorithmName.SHA256, syncKey, key, [.. SyncInfo, .. Encoding.ASCII.GetBytes(app)]);
        return key;
    }

    public static byte[] Pad(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxData) throw new ArgumentException($"At most {MaxData} bytes go in one envelope; split the batch.", nameof(data));
        var needed = data.Length + LengthBytes;
        var padded = new byte[PadSizes.First(size => size >= needed)];
        BinaryPrimitives.WriteUInt32BigEndian(padded, (uint)data.Length);
        data.CopyTo(padded.AsSpan(LengthBytes));
        return padded;
    }

    /// <summary>The data, or null unless the size is the smallest padding size that holds it (a larger one would be a
    /// second spelling of the same message), the length fits and the rest is zeros.</summary>
    public static byte[]? Unpad(ReadOnlySpan<byte> padded)
    {
        if (Array.IndexOf(PadSizes, padded.Length) < 0) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(padded);
        if (length > padded.Length - LengthBytes || PadSizes.First(size => size >= length + LengthBytes) != padded.Length) return null;
        var rest = padded[(LengthBytes + (int)length)..];
        return rest.ContainsAnyExcept((byte)0) ? null : padded.Slice(LengthBytes, (int)length).ToArray();
    }

    /// <summary>The kind and epoch, or null when it isn't a version 2 envelope.</summary>
    public static (byte Kind, uint Epoch)? Header(ReadOnlySpan<byte> envelope) =>
        envelope.Length >= HeaderBytes && envelope[0] == Version && envelope[1] is >= SyncKind and <= LinkKind
            ? (envelope[1], BinaryPrimitives.ReadUInt32BigEndian(envelope[2..]))
            : null;

    public static byte[] SealSync(ReadOnlySpan<byte> mailboxId, uint epoch, ReadOnlySpan<byte> subKey, ReadOnlySpan<byte> data, IDeviceSigner sender) =>
        Sign(SealAes(SyncKind, epoch, mailboxId, subKey, data, RandomNumberGenerator.GetBytes(NonceBytes)), sender);

    /// <summary>Null when the recipient's key isn't a point on the curve. The header's epoch is 0, except a recovery
    /// kit's: its code's number (plan §2.12), never the sync-key epoch.</summary>
    public static byte[]? SealToDevice(ReadOnlySpan<byte> mailboxId, ReadOnlySpan<byte> recipient, ReadOnlySpan<byte> data, IDeviceSigner sender, ECDiffieHellman? ephemeral = null, uint epoch = 0)
    {
        var header = Head(ToDeviceKind, epoch);
        if (Hpke.SetupSender(recipient, HpkeInfo, ephemeral) is not { } setup) return null;
        var (enc, context) = setup;
        var sealedData = context.Seal(Aad(mailboxId, header), Pad(data));
        return Sign([.. header, .. enc, .. sealedData], sender);
    }

    /// <summary>A link message, under the key both PCs derived (plan §2.4); the match code is what authenticates it.</summary>
    public static byte[] SealLink(ReadOnlySpan<byte> linkKey, ReadOnlySpan<byte> data) =>
        SealAes(LinkKind, 0, new byte[MailboxIdBytes], linkKey, data, RandomNumberGenerator.GetBytes(NonceBytes));

    public static Opened? OpenSync(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> mailboxId, ReadOnlySpan<byte> subKey, IReadOnlyList<byte[]> senders)
    {
        if (Header(envelope) is not (SyncKind, _) || envelope.Length < HeaderBytes + NonceBytes + PadSizes[0] + TagBytes + P256.SignatureBytes) return null;
        if (Signer(envelope, senders) is not { } sender) return null;
        return OpenAes(envelope[..^P256.SignatureBytes], mailboxId, subKey) is { } data ? new(data, sender) : null;
    }

    /// <summary>
    /// A sync envelope opened before its sender is known: only a PC restoring from the recovery code (plan §2.12), which
    /// reads the device list from inside the profile's batches (each change carries its own signature). It then checks
    /// each envelope's sender with <see cref="SignedBy"/> against that list before it believes anything else in it.
    /// </summary>
    public static byte[]? OpenSyncBeforeSender(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> mailboxId, ReadOnlySpan<byte> subKey) =>
        Header(envelope) is (SyncKind, _) && envelope.Length >= HeaderBytes + NonceBytes + PadSizes[0] + TagBytes + P256.SignatureBytes
            ? OpenAes(envelope[..^P256.SignatureBytes], mailboxId, subKey)
            : null;

    public static Opened? OpenToDevice(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> mailboxId, IDeviceDecrypter recipient, IReadOnlyList<byte[]> senders)
    {
        if (Header(envelope) is not (ToDeviceKind, 0) || envelope.Length < HeaderBytes + Hpke.EncBytes + PadSizes[0] + TagBytes + P256.SignatureBytes) return null;
        if (Signer(envelope, senders) is not { } sender) return null;
        return DecryptToDevice(envelope, mailboxId, recipient) is { } data ? new(data, sender) : null;
    }

    /// <summary>
    /// A kind 0x02 envelope opened before its sender is known: only the recovery kit (plan §2.12), whose reader gets the
    /// device list that names the sender from inside it. The reader then checks the sender with <see cref="SignedBy"/>,
    /// and the header's epoch (the kit's code number) against the list.
    /// </summary>
    public static byte[]? OpenToDeviceBeforeSender(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> mailboxId, IDeviceDecrypter recipient) =>
        Header(envelope) is (ToDeviceKind, _) && envelope.Length >= HeaderBytes + Hpke.EncBytes + PadSizes[0] + TagBytes + P256.SignatureBytes
            ? DecryptToDevice(envelope, mailboxId, recipient)
            : null;

    public static bool SignedBy(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> sender) =>
        envelope.Length > P256.SignatureBytes && P256.Verify(sender, envelope[..^P256.SignatureBytes], envelope[^P256.SignatureBytes..]);

    private static byte[]? DecryptToDevice(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> mailboxId, IDeviceDecrypter recipient)
    {
        var context = Hpke.SetupRecipient(recipient, envelope.Slice(HeaderBytes, Hpke.EncBytes), HpkeInfo);
        var padded = context?.Open(Aad(mailboxId, envelope[..HeaderBytes]), envelope[(HeaderBytes + Hpke.EncBytes)..^P256.SignatureBytes]);
        return padded is not null ? Unpad(padded) : null;
    }

    public static byte[]? OpenLink(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> linkKey) =>
        Header(envelope) is (LinkKind, 0) ? OpenAes(envelope, new byte[MailboxIdBytes], linkKey) : null;

    /// <summary>Kinds 0x01 and 0x04: header, nonce, AES-256-GCM of the padded data.</summary>
    public static byte[] SealAes(byte kind, uint epoch, ReadOnlySpan<byte> mailboxId, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, ReadOnlySpan<byte> nonce)
    {
        var header = Head(kind, epoch);
        var padded = Pad(data);
        var output = new byte[HeaderBytes + NonceBytes + padded.Length + TagBytes];
        header.CopyTo(output, 0);
        nonce.CopyTo(output.AsSpan(HeaderBytes));
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, padded, output.AsSpan(HeaderBytes + NonceBytes, padded.Length), output.AsSpan(^TagBytes), Aad(mailboxId, header));
        return output;
    }

    public static byte[] Sign(byte[] unsigned, IDeviceSigner sender) => [.. unsigned, .. sender.Sign(unsigned)];

    private static byte[]? OpenAes(ReadOnlySpan<byte> unsigned, ReadOnlySpan<byte> mailboxId, ReadOnlySpan<byte> key)
    {
        var length = unsigned.Length - HeaderBytes - NonceBytes - TagBytes;
        if (length < PadSizes[0] || length > MaxPadded) return null;
        var padded = new byte[length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(unsigned.Slice(HeaderBytes, NonceBytes), unsigned.Slice(HeaderBytes + NonceBytes, length), unsigned[^TagBytes..], padded, Aad(mailboxId, unsigned[..HeaderBytes]));
        }
        catch (AuthenticationTagMismatchException) { return null; }
        return Unpad(padded);
    }

    /// <summary>Which candidate's identity key signed the envelope, or null when none did.</summary>
    private static int? Signer(ReadOnlySpan<byte> envelope, IReadOnlyList<byte[]> senders)
    {
        var unsigned = envelope[..^P256.SignatureBytes];
        var signature = envelope[^P256.SignatureBytes..];
        for (var i = 0; i < senders.Count; i++)
            if (P256.Verify(senders[i], unsigned, signature)) return i;
        return null;
    }

    private static byte[] Head(byte kind, uint epoch)
    {
        var header = new byte[HeaderBytes];
        header[0] = Version;
        header[1] = kind;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(2), epoch);
        return header;
    }

    private static byte[] Aad(ReadOnlySpan<byte> mailboxId, ReadOnlySpan<byte> header)
    {
        if (mailboxId.Length != MailboxIdBytes) throw new ArgumentException("A mailbox id is 16 bytes.", nameof(mailboxId));
        return [.. AadLabel, .. mailboxId, .. header];
    }
}
