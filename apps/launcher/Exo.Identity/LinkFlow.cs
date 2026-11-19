using System.Buffers.Text;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// Linking a PC end to end (plan §2.4) over the rendezvous (<see cref="MailboxLink"/>): the handshake
/// (<see cref="LinkHandshake"/>), then three messages, each canonical JSON in a kind 0x04 envelope under the link key,
/// sent as a <see cref="LinkHandshake.SealedFrame"/>:
/// <list type="number">
/// <item>E to N, once the person said the 6 digits match on E (behind Windows Hello):
/// <c>{"changes":[…],"epoch":n,"exoId":"…","syncKey":"&lt;base64url&gt;","t":"offer","v":1}</c>: the device list's chain,
/// the sync key and its epoch, the Exo ID.</item>
/// <item>N to E, once the person said they match on N too: <c>{"record":{…},"t":"device","v":1}</c>, N's device
/// record signed by its own key.</item>
/// <item>E to N: <c>{"change":{…},"t":"added","v":1}</c>, E's signed <c>add</c> of that record, which E has put in the
/// sync box before sending it, so N's first rows always come after it.</item>
/// </list>
/// Each side refuses anything else (an unknown field included), so a PC in the middle can't slip a message in without
/// matching the 6 digits, one chance in a million.
/// </summary>
public static class LinkFlow
{
    /// <summary>What E hands N: the identity as E holds it.</summary>
    public sealed record Offer(string ExoId, uint Epoch, byte[] SyncKey, IReadOnlyList<JsonObject> Changes);

    /// <summary>What N holds once it's in: the offer, and the device list with its own add on top.</summary>
    public sealed record Joined(string ExoId, uint Epoch, byte[] SyncKey, DeviceList List);

    /// <summary>The new PC (N): shows the nameplate, then the match code, then joins.</summary>
    public sealed class NewPc : IAsyncDisposable
    {
        private readonly MailboxLink _link;
        private readonly LinkHandshake.NewPc _handshake = new();
        private LinkHandshake.Keys? _keys;

        private NewPc(MailboxLink link) => _link = link;

        public string Nameplate => _link.Nameplate;

        /// <summary>The QR's text, for a PC with a camera (it also proves N's key, §2.4).</summary>
        public string QrText => LinkHandshake.QrText(_link.Nameplate, _handshake.PublicKey);

        public static async Task<NewPc> StartAsync(MailboxConnect connect, CancellationToken ct) =>
            new(await MailboxLink.CreateAsync(connect, ct).ConfigureAwait(false));

        /// <summary>Waits for E to join and runs the handshake: the 6 digits both screens show.</summary>
        public async Task<string> MatchCodeAsync(CancellationToken ct)
        {
            await _link.WaitJoinedAsync(ct).ConfigureAwait(false);
            var commit = await Expect(_link, LinkHandshake.CommitFrame, ct).ConfigureAwait(false);
            if (commit.Length != LinkHandshake.CommitmentBytes) throw new MailboxClosedException("The other PC's first message isn't a commitment.");
            await _link.SendAsync([LinkHandshake.KeyFrame, .. _handshake.TakeCommitment(commit)], ct).ConfigureAwait(false);
            var reveal = await Expect(_link, LinkHandshake.RevealFrame, ct).ConfigureAwait(false);
            _keys = _handshake.TakeReveal(reveal, _link.Nameplate) ?? throw new MailboxClosedException("The other PC's key isn't the one it committed to.");
            return _keys.MatchCode;
        }

        /// <summary>
        /// After the person said the digits match here: takes E's offer, sends <paramref name="record"/> (this PC's,
        /// signed by its key) and takes E's add of it. Throws <see cref="MailboxClosedException"/> when anything isn't
        /// what it must be.
        /// </summary>
        public async Task<Joined> JoinAsync(JsonObject record, CancellationToken ct)
        {
            var keys = _keys ?? throw new InvalidOperationException("No match code yet.");
            var offer = ReadOffer(await Sealed(_link, keys.Key, ct).ConfigureAwait(false)) ?? throw new MailboxClosedException("The other PC's offer couldn't be read.");
            var list = DeviceList.Load(offer.Changes) is { } loaded && loaded.ExoId == offer.ExoId
                ? loaded : throw new MailboxClosedException("The other PC's device list doesn't check out.");
            var mine = Device.Read(record) ?? throw new ArgumentException("Not a device record.", nameof(record));
            await SendSealed(_link, keys.Key, DeviceJson(record), ct).ConfigureAwait(false);
            if (ReadAdded(await Sealed(_link, keys.Key, ct).ConfigureAwait(false)) is not { } change || change["device"] is not JsonObject addedRecord ||
                Device.Text(addedRecord, "id") != mine.Id || list.Take(change).Result != DeviceList.Result.Appended)
                throw new MailboxClosedException("The other PC didn't add this one.");
            return new(offer.ExoId, offer.Epoch, offer.SyncKey, list);
        }

        public async ValueTask DisposeAsync()
        {
            _handshake.Dispose();
            await _link.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A PC already in the identity (E): joins the nameplate N shows, then hands N the identity.</summary>
    public sealed class ExistingPc : IAsyncDisposable
    {
        private readonly MailboxLink _link;
        private readonly byte[]? _qrKeyHash;
        private readonly LinkHandshake.ExistingPc _handshake = new();
        private LinkHandshake.Keys? _keys;

        private ExistingPc(MailboxLink link, byte[]? qrKeyHash)
        {
            _link = link;
            _qrKeyHash = qrKeyHash;
        }

        /// <summary>Joins what the person typed: the 8 digits (spaces allowed) or a QR's text.</summary>
        public static async Task<ExistingPc> JoinAsync(MailboxConnect connect, string typed, CancellationToken ct)
        {
            var (nameplate, hash) = LinkHandshake.ReadQr(typed.Trim()) is { } qr ? (qr.Nameplate, qr.KeyHash) : (Digits(typed), null);
            if (nameplate is null) throw new ArgumentException("A link code is 8 digits.", nameof(typed));
            return new(await MailboxLink.JoinAsync(connect, nameplate, ct).ConfigureAwait(false), hash);
        }

        /// <summary>The 8 digits in what was typed, spaces and hyphens aside; null when it isn't 8 digits.</summary>
        public static string? Digits(string typed)
        {
            var digits = new string([.. typed.Where(c => c is not (' ' or '-'))]);
            return LinkHandshake.IsNameplate(digits) ? digits : null;
        }

        /// <summary>Commits, takes N's key and reveals: the 6 digits both screens show.</summary>
        public async Task<string> MatchCodeAsync(CancellationToken ct)
        {
            await _link.SendAsync([LinkHandshake.CommitFrame, .. _handshake.Commitment], ct).ConfigureAwait(false);
            var key = await Expect(_link, LinkHandshake.KeyFrame, ct).ConfigureAwait(false);
            var (reveal, keys) = _handshake.TakeNewPcKey(key, _link.Nameplate, _qrKeyHash) ?? throw new MailboxClosedException("The other PC's key isn't a key, or isn't the one in its QR.");
            await _link.SendAsync([LinkHandshake.RevealFrame, .. reveal], ct).ConfigureAwait(false);
            _keys = keys;
            return keys.MatchCode;
        }

        /// <summary>After the person said the digits match (and Windows Hello agreed): sends the offer and answers N's
        /// record, checked: well-formed, signed by its own key, a PC (not a recovery entry) the list doesn't hold.</summary>
        public async Task<JsonObject> OfferAsync(Offer offer, CancellationToken ct)
        {
            var keys = _keys ?? throw new InvalidOperationException("No match code yet.");
            await SendSealed(_link, keys.Key, OfferJson(offer), ct).ConfigureAwait(false);
            if (ReadDevice(await Sealed(_link, keys.Key, ct).ConfigureAwait(false)) is not { } record || DeviceList.Load(offer.Changes)?.Find(Device.Text(record, "id")!) is not null)
                throw new MailboxClosedException("The other PC's answer isn't a new PC's record.");
            return record;
        }

        /// <summary>Sends E's add of N's record, once it is in the sync box.</summary>
        public Task FinishAsync(JsonObject addChange, CancellationToken ct)
        {
            var keys = _keys ?? throw new InvalidOperationException("No match code yet.");
            return SendSealed(_link, keys.Key, AddedJson(addChange), ct);
        }

        public async ValueTask DisposeAsync()
        {
            _handshake.Dispose();
            await _link.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static JsonObject OfferJson(Offer offer) => new()
    {
        ["v"] = 1,
        ["t"] = "offer",
        ["exoId"] = offer.ExoId,
        ["epoch"] = (long)offer.Epoch,
        ["syncKey"] = Base64Url.EncodeToString(offer.SyncKey),
        ["changes"] = new JsonArray([.. offer.Changes.Select(c => (JsonNode)c.DeepClone())]),
    };

    public static JsonObject DeviceJson(JsonObject record) => new() { ["v"] = 1, ["t"] = "device", ["record"] = record.DeepClone() };

    public static JsonObject AddedJson(JsonObject change) => new() { ["v"] = 1, ["t"] = "added", ["change"] = change.DeepClone() };

    /// <summary>N's answer: its device record, signed by its own key and not an entry (recovery or account); null for anything else,
    /// an unknown field included. Whether the list already holds it is E's to check.</summary>
    public static JsonObject? ReadDevice(JsonObject? json) =>
        json is { Count: 3 } && Device.Int(json, "v") == 1 && Device.Text(json, "t") == "device" && json["record"] is JsonObject record &&
        Device.Read(record) is { Entry: false } ? (JsonObject)record.DeepClone() : null;

    /// <summary>E's last message: an add change; null for anything else, an unknown field included. Whether it adds N on
    /// top of the offered chain is N's to check (<see cref="DeviceList.Take"/>).</summary>
    public static JsonObject? ReadAdded(JsonObject? json) =>
        json is { Count: 3 } && Device.Int(json, "v") == 1 && Device.Text(json, "t") == "added" && json["change"] is JsonObject change &&
        Device.Text(change, "op") == "add" ? (JsonObject)change.DeepClone() : null;

    public static Offer? ReadOffer(JsonObject? json)
    {
        if (json is not { Count: 6 } || Device.Int(json, "v") != 1 || Device.Text(json, "t") != "offer" || Device.Text(json, "exoId") is not { } exoId ||
            Device.Int(json, "epoch") is not { } epoch || epoch is < 0 or > uint.MaxValue ||
            Device.Text(json, "syncKey") is not { } text || CanonicalJson.Bytes(text) is not { Length: 32 } key ||
            json["changes"] is not JsonArray { Count: > 0 } changes || changes.Any(c => c is not JsonObject))
            return null;
        return new(exoId, (uint)epoch, key, [.. changes.Select(c => (JsonObject)c!.DeepClone())]);
    }

    private static async Task<byte[]> Expect(MailboxLink link, byte kind, CancellationToken ct)
    {
        var frame = await link.ReceiveAsync(ct).ConfigureAwait(false);
        return frame[0] == kind ? frame[1..] : throw new MailboxClosedException("The other PC sent the link's steps out of order.");
    }

    private static Task SendSealed(MailboxLink link, byte[] key, JsonObject message, CancellationToken ct) =>
        link.SendAsync([LinkHandshake.SealedFrame, .. Envelope.SealLink(key, CanonicalJson.Serialize(message))], ct);

    private static async Task<JsonObject?> Sealed(MailboxLink link, byte[] key, CancellationToken ct) =>
        ReadSealed(await Expect(link, LinkHandshake.SealedFrame, ct).ConfigureAwait(false), key);

    /// <summary>A link message from its 0x04 envelope (the frame without its 0x04 byte): the JSON, only when the envelope
    /// opens under <paramref name="key"/> and the bytes are canonical (one spelling per message).</summary>
    public static JsonObject? ReadSealed(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key)
    {
        var data = Envelope.OpenLink(envelope, key);
        if (data is null || CanonicalJson.ParseObject(data) is not { } json) return null;
        try { return CanonicalJson.Serialize(json).AsSpan().SequenceEqual(data) ? json : null; }
        catch (FormatException) { return null; }
    }
}
