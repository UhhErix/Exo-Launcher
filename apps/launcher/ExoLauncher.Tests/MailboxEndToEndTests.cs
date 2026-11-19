using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Exo.Identity;

namespace ExoLauncher.Tests;

/// <summary>
/// Exo identity end to end through the real mailbox Worker (cloud/mailbox/src/worker.js) running on this PC under
/// cloud/mailbox/local/server.mjs, with every Durable Object thrown away after each event as hibernation does. Needs
/// Node 22.13+.
///
/// Ways Launcher and the Worker could disagree with no error on either side:
/// - .NET's P1363 signature or SEC1 key isn't what WebCrypto checks, or the box id or login message differs, so no
///   Exo app ever gets into its own box;
/// - a sync envelope stored by one device doesn't come back byte for byte to another, live or replayed;
/// - two PCs going through the rendezvous derive different link keys or match codes (a frame bent on the way), or
///   can't open each other's sealed messages;
/// - the recovery kit put in its box can't be found and opened from the code alone on a new PC.
/// </summary>
public sealed class MailboxEndToEndTests : IAsyncLifetime
{
    private static readonly byte[] SyncKey = SHA256.HashData("end to end sync key"u8);
    private Process? _mailbox;
    private string _base = "";

    public async ValueTask InitializeAsync()
    {
        var node = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, "node.exe")).FirstOrDefault(File.Exists);
        if (node is null) return;
        _mailbox = Process.Start(new ProcessStartInfo(node, ["cloud/mailbox/local/server.mjs", "--port", "0", "--evict"])
        {
            WorkingDirectory = Repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var line = await _mailbox.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        _base = "ws://127.0.0.1:" + line!["mailbox listening ".Length..];
    }

    public ValueTask DisposeAsync()
    {
        if (_mailbox is null) return ValueTask.CompletedTask;
        _mailbox.StandardInput.Close();
        if (!_mailbox.WaitForExit(5000)) _mailbox.CloseMainWindow();
        _mailbox.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TwoDevicesShareTheSyncBox()
    {
        Assert.SkipWhen(_mailbox is null, "Node isn't on PATH");
        using var box = new EcdsaSigner(P256.Signer(Mailbox.BoxScalar(SyncKey)));
        var boxId = Mailbox.BoxId(box.PublicKey);
        using var deskPc = TestKeys.Signer();
        using var laptop = TestKeys.Signer();
        await using var desk = await Login(box);
        await using var lap = await Login(box);

        var key = Envelope.SyncSubKey(SyncKey, "launcher");
        var item = "{\"type\":\"wishlist\",\"id\":\"steam:570\",\"modified\":1761000000}"u8.ToArray();
        var envelope = Envelope.SealSync(Convert.FromHexString(boxId), 1, key, item, deskPc);
        await desk.Send([Mailbox.PutFrame, .. envelope]);
        var stored = await desk.Receive();
        Assert.Equal(Mailbox.StoredFrame, stored[0]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(stored.AsSpan(1)));

        // Live to the other device, and from a replay to one that comes later.
        var live = await lap.Receive();
        Assert.Equal(Mailbox.MessageFrame, live[0]);
        Assert.Equal(envelope, live[9..]);
        var opened = Envelope.OpenSync(live[9..], Convert.FromHexString(boxId), key, [laptop.PublicKey, deskPc.PublicKey]);
        Assert.Equal(item, opened!.Data);
        Assert.Equal(1, opened.Sender);

        await using var later = await Login(box);
        await later.Send([Mailbox.SinceFrame, 0, 0, 0, 0]);
        Assert.Equal(envelope, (await later.Receive())[9..]);
        Assert.Equal(Mailbox.CaughtUpFrame, (await later.Receive())[0]);
    }

    [Fact]
    public async Task AKeyThatIsntTheBoxsIsShutOut()
    {
        Assert.SkipWhen(_mailbox is null, "Node isn't on PATH");
        using var box = new EcdsaSigner(P256.Signer(Mailbox.BoxScalar(SyncKey)));
        using var other = TestKeys.Signer();
        await using var socket = await Socket.Open(new Uri($"{_base}/v1/box/{Mailbox.BoxId(box.PublicKey)}"));
        var challenge = await socket.Receive();
        Assert.Equal(Mailbox.ChallengeFrame, challenge[0]);
        await socket.Send(Mailbox.Auth(other, challenge[1..]));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await socket.Closed());
    }

    [Fact]
    public async Task TwoPcsLinkThroughTheRendezvous()
    {
        Assert.SkipWhen(_mailbox is null, "Node isn't on PATH");
        using var n = new LinkHandshake.NewPc();
        using var e = new LinkHandshake.ExistingPc();
        await using var newPc = await Socket.Open(new Uri($"{_base}/v1/link"));
        var plate = await newPc.Receive();
        Assert.Equal(Mailbox.NameplateFrame, plate[0]);
        var nameplate = Encoding.ASCII.GetString(plate, 1, plate.Length - 1);
        Assert.True(LinkHandshake.IsNameplate(nameplate));

        await using var existing = await Socket.Open(new Uri($"{_base}/v1/link/{nameplate}"));
        Assert.Equal(Mailbox.JoinedFrame, (await newPc.Receive())[0]);
        Assert.Equal(Mailbox.JoinedFrame, (await existing.Receive())[0]);

        await existing.Send([LinkHandshake.CommitFrame, .. e.Commitment]);
        var commit = await newPc.Receive();
        await newPc.Send([LinkHandshake.KeyFrame, .. n.TakeCommitment(commit.AsSpan(1))]);
        var newKey = await existing.Receive();
        var (reveal, eKeys) = e.TakeNewPcKey(newKey.AsSpan(1), nameplate)!.Value;
        await existing.Send([LinkHandshake.RevealFrame, .. reveal]);
        var nKeys = n.TakeReveal((await newPc.Receive()).AsSpan(1), nameplate)!;
        Assert.Equal(eKeys.MatchCode, nKeys.MatchCode);

        await existing.Send([LinkHandshake.SealedFrame, .. Envelope.SealLink(eKeys.Key, "{\"t\":\"offer\"}"u8)]);
        Assert.Equal("{\"t\":\"offer\"}"u8.ToArray(), Envelope.OpenLink((await newPc.Receive()).AsSpan(1), nKeys.Key));
        await newPc.Send([LinkHandshake.SealedFrame, .. Envelope.SealLink(nKeys.Key, "{\"t\":\"device\"}"u8)]);
        Assert.Equal("{\"t\":\"device\"}"u8.ToArray(), Envelope.OpenLink((await existing.Receive()).AsSpan(1), eKeys.Key));
    }

    [Fact]
    public async Task TheRecoveryKitComesBackFromTheCodeAlone()
    {
        Assert.SkipWhen(_mailbox is null, "Node isn't on PATH");
        var (text, code) = RecoveryCode.New();
        var kit = new RecoveryCode.Kit(Device.ExoIdOf(SHA256.HashData("first pc"u8)), 4, SyncKey);
        using var pc = TestKeys.Signer();
        using (var box = code.BoxKey())
        {
            // A linked PC has the box key and the entry's encPub (from the device list), never the code.
            await using var linkedPc = await Login(box);
            await linkedPc.Send([Mailbox.PutFrame, .. RecoveryCode.SealKit(kit, 1, code.SealPublicKey, code.BoxId, pc)!]);
            Assert.Equal(Mailbox.StoredFrame, (await linkedPc.Receive())[0]);
        }

        // Every PC lost: a new one has only the typed code.
        var typed = RecoveryCode.Read(text.ToLowerInvariant().Replace('-', ' '))!;
        using var sameBox = typed.BoxKey();
        await using var newPc = await Login(sameBox);
        await newPc.Send([Mailbox.SinceFrame, 0, 0, 0, 0]);
        var message = await newPc.Receive();
        var opened = typed.OpenKit(message.AsSpan(9))!;
        Assert.Equal(kit.ExoId, opened.Kit.ExoId);
        Assert.Equal(kit.Epoch, opened.Kit.Epoch);
        Assert.Equal(SyncKey, opened.Kit.SyncKey);
        Assert.Equal(Device.IdOf(pc.PublicKey), opened.By);
        Assert.Equal(1u, opened.CodeNumber);
        Assert.True(Envelope.SignedBy(message.AsSpan(9), pc.PublicKey));
    }

    private async Task<Socket> Login(IDeviceSigner boxKey)
    {
        var socket = await Socket.Open(new Uri($"{_base}/v1/box/{Mailbox.BoxId(boxKey.PublicKey)}"));
        var challenge = await socket.Receive();
        Assert.Equal(Mailbox.ChallengeFrame, challenge[0]);
        await socket.Send(Mailbox.Auth(boxKey, challenge[1..]));
        Assert.Equal(Mailbox.ReadyFrame, (await socket.Receive())[0]);
        return socket;
    }

    /// <summary>A client WebSocket that reads whole binary frames, 5 s at most each.</summary>
    private sealed class Socket : IAsyncDisposable
    {
        private readonly ClientWebSocket _ws = new();

        internal static async Task<Socket> Open(Uri uri)
        {
            var socket = new Socket();
            await socket._ws.ConnectAsync(uri, TestContext.Current.CancellationToken);
            return socket;
        }

        internal Task Send(byte[] data) => _ws.SendAsync(data, WebSocketMessageType.Binary, true, TestContext.Current.CancellationToken);

        internal async Task<byte[]> Receive()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var buffer = new byte[70_000];
            var length = 0;
            while (true)
            {
                var got = await _ws.ReceiveAsync(buffer.AsMemory(length), timeout.Token);
                if (got.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Closed: " + _ws.CloseStatus);
                length += got.Count;
                if (got.EndOfMessage) return buffer[..length];
            }
        }

        /// <summary>How the mailbox closed the connection.</summary>
        internal async Task<WebSocketCloseStatus?> Closed()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var buffer = new byte[256];
            while ((await _ws.ReceiveAsync(buffer, timeout.Token)).MessageType != WebSocketMessageType.Close) { }
            return _ws.CloseStatus;
        }

        public async ValueTask DisposeAsync()
        {
            if (_ws.State == WebSocketState.Open)
                try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch (WebSocketException) { /* already going */ }
            _ws.Dispose();
        }
    }
}
