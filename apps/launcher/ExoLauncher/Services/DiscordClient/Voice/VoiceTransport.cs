using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// The call's UDP leg to Discord's voice server: IP discovery, then RTP packets of Opus frames under
/// Discord's transport encryption, "rtpsize" variants (the RTP header and extension preamble are the
/// additional data; a 32-bit counter is appended as the nonce): aead_aes256_gcm_rtpsize when offered,
/// else aead_xchacha20_poly1305_rtpsize. Received packets are checked and decrypted here and handed
/// on by SSRC; anything that fails to authenticate is dropped.
/// </summary>
internal sealed class VoiceTransport : IDisposable
{
    internal const string Aes = "aead_aes256_gcm_rtpsize", XChaCha = "aead_xchacha20_poly1305_rtpsize";
    private const int HeaderSize = 12, TagSize = 16, NonceSize = 4;

    private readonly UdpClient _udp;
    private readonly IPEndPoint _server;
    private readonly uint _ssrc;
    private readonly CancellationTokenSource _stop = new();
    private AesGcm? _aes;
    private byte[]? _chachaKey;
    private string _mode = Aes;
    private uint _nonce;
    private int _wideSequence;
    private ushort _sequence;
    private uint _timestamp;

    /// <summary>One received RTP packet, transport-decrypted (its payload still DAVE-encrypted when the call is E2EE).</summary>
    /// <summary>One received RTP packet; Orientation is its urn:3gpp:video-orientation byte (C, F, R1 R0), or -1 when it carries none.</summary>
    internal readonly record struct Packet(uint Ssrc, int PayloadType, bool Marker, ushort Sequence, uint Timestamp, ReadOnlyMemory<byte> Payload, int Orientation = -1, int TransportSequence = -1,
        ReadOnlyMemory<byte> Extension = default);

    // The video-orientation extension's id. Discord's media servers fix each extension's id (no SDP): audio level 1, toffset 2,
    // abs-send-time 3, transport-wide sequence 5, video-orientation 13.
    private const int OrientationId = 13;

    /// <summary>The video-orientation byte in a one-byte-header extension block (RFC 8285), or -1.</summary>
    internal static int Orientation(ReadOnlySpan<byte> block)
    {
        for (var at = 0; at < block.Length;)
        {
            var head = block[at++];
            if (head == 0) continue; // padding
            int id = head >> 4, length = (head & 0x0F) + 1;
            if (id == 15 || at + length > block.Length) return -1;
            if (id == OrientationId && length == 1) return block[at];
            at += length;
        }
        return -1;
    }

    /// <summary>
    /// The transport-wide sequence number in a one-byte-header extension block, or -1: the only libwebrtc extension two
    /// bytes long (draft-holmer-rmcat-transport-wide-cc-extensions), whatever id the server gave it.
    /// </summary>
    /// <summary>A one-byte extension block's elements as "id:length", for app.log.</summary>
    internal static string Elements(ReadOnlySpan<byte> block)
    {
        var parts = new List<string>();
        for (var at = 0; at < block.Length;)
        {
            var head = block[at++];
            if (head == 0) continue; // padding
            int id = head >> 4, length = (head & 0x0F) + 1;
            if (id == 15 || at + length > block.Length) break;
            parts.Add($"{id}:{length}");
            at += length;
        }
        return parts.Count == 0 ? "none" : string.Join(' ', parts);
    }

    internal static int TransportSequence(ReadOnlySpan<byte> block)
    {
        for (var at = 0; at < block.Length;)
        {
            var head = block[at++];
            if (head == 0) continue; // padding
            int id = head >> 4, length = (head & 0x0F) + 1;
            if (id == 15 || at + length > block.Length) return -1;
            if (length == 2) return BinaryPrimitives.ReadUInt16BigEndian(block[at..]);
            at += length;
        }
        return -1;
    }

    /// <summary>A decrypted RTP packet from an SSRC. Its payload is the receive buffer: read or copied before the handler returns.</summary>
    internal event Action<Packet>? Received;

    /// <summary>A decrypted RTCP packet (compound, header included): the server's NACKs and keyframe requests.</summary>
    internal event Action<byte[]>? RtcpReceived;

    /// <summary>
    /// Sends one RTCP packet (a NACK or PLI): as with RTP under rtpsize, its first 8 bytes (header and
    /// sender SSRC) are the additional data, the rest is encrypted, and the counter nonce follows the tag.
    /// </summary>
    internal void SendRtcp(ReadOnlySpan<byte> rtcp)
    {
        if (rtcp.Length < 8 || !Started) return;
        Span<byte> packet = stackalloc byte[rtcp.Length + TagSize + NonceSize];
        rtcp[..8].CopyTo(packet);
        var counter = Interlocked.Increment(ref _nonce) - 1; // atomic: sound, video and RTCP send from different threads, and a nonce must never repeat
        BinaryPrimitives.WriteUInt32BigEndian(packet[^NonceSize..], counter);
        Seal(counter, packet[..8], rtcp[8..], packet.Slice(8, rtcp.Length - 8), packet.Slice(rtcp.Length, TagSize));
        try { _udp.Client.Send(packet); }
        catch (SocketException ex) { AppLog.Debug("Discord video: RTCP send failed " + ex.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    internal VoiceTransport(string ip, int port, uint ssrc)
    {
        _server = new IPEndPoint(IPAddress.Parse(ip), port);
        _ssrc = ssrc;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.ReceiveBufferSize = 1 << 20;
        _udp.Connect(_server);
    }

    /// <summary>Asks the voice server for this PC's public address and port (Discord's IP discovery packet).</summary>
    internal async Task<(string Address, int Port)> DiscoverAsync(CancellationToken ct)
    {
        var request = new byte[74];
        BinaryPrimitives.WriteUInt16BigEndian(request, 1);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 70);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), _ssrc);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await _udp.SendAsync(request, ct).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var reply = (await _udp.ReceiveAsync(wait.Token).ConfigureAwait(false)).Buffer;
                if (reply.Length < 74 || BinaryPrimitives.ReadUInt16BigEndian(reply) != 2) continue;
                var address = Encoding.ASCII.GetString(reply, 8, 64).TrimEnd('\0');
                return (address, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(72)));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        throw new TimeoutException("Discord's voice server didn't answer.");
    }

    /// <summary>The session key from op 4 Session Description; starts receiving.</summary>
    internal void Start(string mode, byte[] key)
    {
        _mode = mode;
        if (mode == Aes) _aes = new AesGcm(key, TagSize);
        else _chachaKey = key;
        Started = true;
        _ = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>The session key is in: until then (a reconnect's new transport) nothing can be sealed, so nothing is sent.</summary>
    internal bool Started { get; private set; }

    /// <summary>Sends one 20 ms Opus frame (already DAVE-encrypted when the call is E2EE).</summary>
    internal void Send(ReadOnlySpan<byte> frame)
    {
        if (!Started) return;
        Span<byte> packet = stackalloc byte[HeaderSize + frame.Length + TagSize + NonceSize];
        packet[0] = 0x80;
        packet[1] = 0x78;
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], _sequence++);
        SyncTimestamp();
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], _timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], _ssrc);
        _timestamp += Opus.FrameSamples;
        var counter = Interlocked.Increment(ref _nonce) - 1; // atomic: sound, video and RTCP send from different threads, and a nonce must never repeat
        BinaryPrimitives.WriteUInt32BigEndian(packet[^NonceSize..], counter);
        Seal(counter, packet[..HeaderSize], frame, packet.Slice(HeaderSize, frame.Length), packet.Slice(HeaderSize + frame.Length, TagSize));
        try { _udp.Client.Send(packet); }
        catch (SocketException ex) { AppLog.Debug("Discord voice: send failed " + ex.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    // RTP time is the sound's own clock, as Discord's client keeps it: after a pause (silence, mute, a dropped late
    // packet) it jumps by the time that passed. Counted only per frame sent, a 10 s pause read as 20 ms, so the
    // listener's jitter buffer took the next words for packets 10 s late and grew its delay each time.
    private long _clockStart;
    private uint _timestampStart;

    private void SyncTimestamp()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_clockStart == 0) { _clockStart = now; _timestampStart = _timestamp; return; }
        var frames = (now - _clockStart) * Opus.SampleRate / System.Diagnostics.Stopwatch.Frequency / Opus.FrameSamples;
        var due = unchecked(_timestampStart + (uint)(frames * Opus.FrameSamples));
        // Within a frame of the clock, a talk spurt keeps its even 20 ms steps (send jitter never shifts them).
        if (unchecked((int)(due - _timestamp)) >= 2 * Opus.FrameSamples) _timestamp = due;
    }

    /// <summary>Sends one video RTP packet (its frame already DAVE-encrypted), on the call's own counter.</summary>
    internal void SendVideo(uint ssrc, int payloadType, bool marker, ushort sequence, uint timestamp, ReadOnlySpan<byte> payload)
    {
        // A one-byte header extension (RFC 8285) as Discord's own video packets carry it, in their order: abs-send-time (id 3),
        // toffset (id 2, zero) and the transport-wide sequence (id 5, two bytes). With id 5 holding playout-delay's three
        // bytes, Discord's server took none of the video (its receiver reports stayed at sequence 0) and viewers spun forever.
        // With rtpsize the preamble is additional data and the elements are encrypted with the payload.
        if (!Started) return;
        var sentAt = AbsSendTime(System.Diagnostics.Stopwatch.GetTimestamp());
        var wide = (ushort)Interlocked.Increment(ref _wideSequence);
        Span<byte> extension = [0x32, (byte)(sentAt >> 16), (byte)(sentAt >> 8), (byte)sentAt, 0x22, 0, 0, 0, 0x51, (byte)(wide >> 8), (byte)wide, 0];
        var body = extension.Length + payload.Length;
        Span<byte> packet = stackalloc byte[HeaderSize + 4 + body + TagSize + NonceSize];
        packet[0] = 0x90;
        packet[1] = (byte)(payloadType | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], ssrc);
        packet[12] = 0xBE; packet[13] = 0xDE;
        BinaryPrimitives.WriteUInt16BigEndian(packet[14..], (ushort)(extension.Length / 4));
        Span<byte> plain = stackalloc byte[body];
        extension.CopyTo(plain);
        payload.CopyTo(plain[extension.Length..]);
        var counter = Interlocked.Increment(ref _nonce) - 1; // atomic: sound, video and RTCP send from different threads, and a nonce must never repeat
        BinaryPrimitives.WriteUInt32BigEndian(packet[^NonceSize..], counter);
        Seal(counter, packet[..(HeaderSize + 4)], plain, packet.Slice(HeaderSize + 4, body), packet.Slice(HeaderSize + 4 + body, TagSize));
        try { _udp.Client.Send(packet); }
        catch (SocketException ex) { AppLog.Debug("Discord video: send failed " + ex.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>abs-send-time: a send time as 6.18 fixed-point seconds, its low 24 bits.</summary>
    internal static uint AbsSendTime(long ticks)
    {
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        return (uint)(((ticks / frequency) << 18) + ((ticks % frequency) << 18) / frequency) & 0xFFFFFF;
    }

    // One buffer for what arrives and one for what it decrypts to, for the whole call: a packet costs no allocation
    // (UdpClient.ReceiveAsync made a new array per datagram, and each payload was copied out again).
    private async Task ReceiveLoopAsync()
    {
        var packet = new byte[4096];
        var plain = new byte[4096];
        while (!_stop.IsCancellationRequested)
        {
            int length;
            try { length = await _udp.Client.ReceiveAsync(packet, SocketFlags.None, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            Handle(packet.AsSpan(0, length), plain);
        }
    }

    private void Handle(ReadOnlySpan<byte> packet, byte[] plain)
    {
        if (packet.Length < HeaderSize + TagSize + NonceSize || (packet[0] >> 6) != 2) return;
        if (packet[1] is >= 200 and <= 206)
        {
            // RTCP: the 8-byte header is additional data, the rest is encrypted.
            var bodyLength = packet.Length - 8 - TagSize - NonceSize;
            if (bodyLength < 0 || bodyLength > plain.Length) return;
            var rtcpCounter = BinaryPrimitives.ReadUInt32BigEndian(packet[^NonceSize..]);
            if (!Open(rtcpCounter, packet[..8], packet.Slice(8, bodyLength), packet.Slice(8 + bodyLength, TagSize), plain.AsSpan(0, bodyLength)))
                return;
            var rtcp = new byte[8 + bodyLength];
            packet[..8].CopyTo(rtcp);
            plain.AsSpan(0, bodyLength).CopyTo(rtcp.AsSpan(8));
            RtcpReceived?.Invoke(rtcp);
            return;
        }
        var type = packet[1] & 0x7F;
        // rtpsize: the header, CSRCs and the extension preamble are additional data; the extension body is encrypted.
        var aad = HeaderSize + (packet[0] & 0x0F) * 4;
        var extended = (packet[0] & 0x10) != 0;
        var extensionWords = 0;
        var oneByte = false;
        if (extended)
        {
            if (packet.Length < aad + 4) return;
            oneByte = BinaryPrimitives.ReadUInt16BigEndian(packet[aad..]) == 0xBEDE;
            extensionWords = BinaryPrimitives.ReadUInt16BigEndian(packet[(aad + 2)..]);
            aad += 4;
        }
        var cipherLength = packet.Length - aad - TagSize - NonceSize;
        if (cipherLength <= 0 || cipherLength > plain.Length) return;
        var counter = BinaryPrimitives.ReadUInt32BigEndian(packet[^NonceSize..]);
        if (!Open(counter, packet[..aad], packet.Slice(aad, cipherLength), packet.Slice(aad + cipherLength, TagSize), plain.AsSpan(0, cipherLength)))
            return;
        var skip = extensionWords * 4;
        if (skip >= cipherLength) return;
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        var length = cipherLength - skip;
        // Padding (P bit): its last byte says how many bytes of padding end the payload.
        if ((packet[0] & 0x20) != 0 && length > 0) length -= Math.Min(length, plain[skip + length - 1]);
        if (length <= 0) return;
        var orientation = oneByte ? Orientation(plain.AsSpan(0, skip)) : -1;
        var transport = oneByte ? TransportSequence(plain.AsSpan(0, skip)) : -1;
        Received?.Invoke(new Packet(ssrc, type, (packet[1] & 0x80) != 0, sequence, timestamp, plain.AsMemory(skip, length), orientation, transport,
            oneByte ? plain.AsMemory(0, skip) : default));
    }

    private void Seal(uint counter, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plain, Span<byte> cipher, Span<byte> tag)
    {
        if (_aes is not null)
        {
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(nonce, counter);
            _aes.Encrypt(nonce, plain, cipher, tag, aad);
            return;
        }
        XChaChaEncrypt(_chachaKey!, counter, aad, plain, cipher, tag);
    }

    private bool Open(uint counter, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, Span<byte> plain)
    {
        try
        {
            if (_aes is not null)
            {
                Span<byte> nonce = stackalloc byte[12];
                BinaryPrimitives.WriteUInt32BigEndian(nonce, counter);
                _aes.Decrypt(nonce, cipher, tag, plain, aad);
                return true;
            }
            if (_chachaKey is null) return false;
            return XChaChaDecrypt(_chachaKey, counter, aad, cipher, tag, plain);
        }
        catch (CryptographicException) { return false; }
    }

    internal static void XChaChaEncrypt(ReadOnlySpan<byte> key, uint counter, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plain, Span<byte> cipher, Span<byte> tag)
    {
        Span<byte> nonce16 = stackalloc byte[16];
        nonce16.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(nonce16, counter);
        Span<byte> subkey = stackalloc byte[32];
        HChaCha20(key, nonce16, subkey);
        Span<byte> otkBlock = stackalloc byte[64];
        ChaCha20Block(subkey, 0, otkBlock);
        ChaCha20Xor(subkey, 1, plain, cipher);
        Poly1305AeadTag(otkBlock[..32], aad, cipher, tag);
        CryptographicOperations.ZeroMemory(subkey);
        CryptographicOperations.ZeroMemory(otkBlock);
    }

    internal static bool XChaChaDecrypt(ReadOnlySpan<byte> key, uint counter, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, Span<byte> plain)
    {
        Span<byte> nonce16 = stackalloc byte[16];
        nonce16.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(nonce16, counter);
        Span<byte> subkey = stackalloc byte[32];
        HChaCha20(key, nonce16, subkey);
        Span<byte> otkBlock = stackalloc byte[64];
        ChaCha20Block(subkey, 0, otkBlock);
        Span<byte> expected = stackalloc byte[16];
        Poly1305AeadTag(otkBlock[..32], aad, cipher, expected);
        var ok = CryptographicOperations.FixedTimeEquals(expected, tag);
        if (ok) ChaCha20Xor(subkey, 1, cipher, plain);
        CryptographicOperations.ZeroMemory(subkey);
        CryptographicOperations.ZeroMemory(otkBlock);
        CryptographicOperations.ZeroMemory(expected);
        return ok;
    }

    internal static byte[] HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16)
    {
        var output = new byte[32];
        HChaCha20(key, nonce16, output);
        return output;
    }

    internal static void HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16, Span<byte> output)
    {
        Span<uint> s = stackalloc uint[16];
        s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
        for (var i = 0; i < 8; i++) s[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        for (var i = 0; i < 4; i++) s[12 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce16[(i * 4)..]);
        for (var round = 0; round < 10; round++)
        {
            Quarter(s, 0, 4, 8, 12); Quarter(s, 1, 5, 9, 13); Quarter(s, 2, 6, 10, 14); Quarter(s, 3, 7, 11, 15);
            Quarter(s, 0, 5, 10, 15); Quarter(s, 1, 6, 11, 12); Quarter(s, 2, 7, 8, 13); Quarter(s, 3, 4, 9, 14);
        }
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(output[(i * 4)..], s[i]);
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(output[(16 + i * 4)..], s[12 + i]);
    }

    private static void ChaCha20Block(ReadOnlySpan<byte> key, uint counter, Span<byte> output)
    {
        Span<uint> init = stackalloc uint[16];
        Span<uint> work = stackalloc uint[16];
        init[0] = 0x61707865; init[1] = 0x3320646e; init[2] = 0x79622d32; init[3] = 0x6b206574;
        for (var i = 0; i < 8; i++) init[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        init[12] = counter;
        init[13] = 0; init[14] = 0; init[15] = 0;
        init.CopyTo(work);
        for (var round = 0; round < 10; round++)
        {
            Quarter(work, 0, 4, 8, 12); Quarter(work, 1, 5, 9, 13); Quarter(work, 2, 6, 10, 14); Quarter(work, 3, 7, 11, 15);
            Quarter(work, 0, 5, 10, 15); Quarter(work, 1, 6, 11, 12); Quarter(work, 2, 7, 8, 13); Quarter(work, 3, 4, 9, 14);
        }
        for (var i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(output[(i * 4)..], work[i] + init[i]);
    }

    private static void ChaCha20Xor(ReadOnlySpan<byte> key, uint counter, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> block = stackalloc byte[64];
        var offset = 0;
        while (offset < input.Length)
        {
            ChaCha20Block(key, counter++, block);
            var take = Math.Min(64, input.Length - offset);
            for (var i = 0; i < take; i++) output[offset + i] = (byte)(input[offset + i] ^ block[i]);
            offset += take;
        }
    }

    private static void Poly1305AeadTag(ReadOnlySpan<byte> otk, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> cipher, Span<byte> tag)
    {
        var r0 = BinaryPrimitives.ReadUInt64LittleEndian(otk) & 0x0ffffffc0fffffffUL;
        var r1 = BinaryPrimitives.ReadUInt64LittleEndian(otk[8..]) & 0x0ffffffc0ffffffcUL;
        var t0 = r0 & 0xfffffffffffUL;
        var t1 = ((r0 >> 44) | (r1 << 20)) & 0xfffffffffffUL;
        var t2 = (r1 >> 24) & 0x3ffffffffffUL;
        var s1 = t1 * 20;
        var s2 = t2 * 20;
        ulong h0 = 0, h1 = 0, h2 = 0;

        void Feed(ReadOnlySpan<byte> data, bool pad16)
        {
            var at = 0;
            Span<byte> block = stackalloc byte[16];
            while (at < data.Length)
            {
                var rem = data.Length - at;
                ulong lo, hi, hibit;
                if (rem >= 16)
                {
                    lo = BinaryPrimitives.ReadUInt64LittleEndian(data[at..]);
                    hi = BinaryPrimitives.ReadUInt64LittleEndian(data[(at + 8)..]);
                    hibit = 1UL << 40;
                    at += 16;
                }
                else
                {
                    block.Clear();
                    data[at..].CopyTo(block);
                    lo = BinaryPrimitives.ReadUInt64LittleEndian(block);
                    hi = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
                    hibit = 1UL << 40;
                    at = data.Length;
                }
                h0 += lo & 0xfffffffffffUL;
                h1 += ((lo >> 44) | (hi << 20)) & 0xfffffffffffUL;
                h2 += ((hi >> 24) & 0x3ffffffffffUL) | hibit;

                UInt128 d0 = (UInt128)h0 * t0 + (UInt128)h1 * s2 + (UInt128)h2 * s1;
                UInt128 d1 = (UInt128)h0 * t1 + (UInt128)h1 * t0 + (UInt128)h2 * s2;
                UInt128 d2 = (UInt128)h0 * t2 + (UInt128)h1 * t1 + (UInt128)h2 * t0;

                var c = (ulong)(d0 >> 44); h0 = (ulong)d0 & 0xfffffffffffUL;
                d1 += c; c = (ulong)(d1 >> 44); h1 = (ulong)d1 & 0xfffffffffffUL;
                d2 += c; c = (ulong)(d2 >> 42); h2 = (ulong)d2 & 0x3ffffffffffUL;
                h0 += c * 5; c = h0 >> 44; h0 &= 0xfffffffffffUL;
                h1 += c;
            }
            _ = pad16;
        }

        Feed(aad, pad16: true);
        Feed(cipher, pad16: true);
        Span<byte> lens = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(lens, (ulong)aad.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(lens[8..], (ulong)cipher.Length);
        Feed(lens, pad16: false);

        var c2 = h1 >> 44; h1 &= 0xfffffffffffUL;
        h2 += c2; c2 = h2 >> 42; h2 &= 0x3ffffffffffUL;
        h0 += c2 * 5; c2 = h0 >> 44; h0 &= 0xfffffffffffUL;
        h1 += c2; c2 = h1 >> 44; h1 &= 0xfffffffffffUL;
        h2 += c2; c2 = h2 >> 42; h2 &= 0x3ffffffffffUL;
        h0 += c2 * 5; c2 = h0 >> 44; h0 &= 0xfffffffffffUL;
        h1 += c2;

        var g0 = h0 + 5; c2 = g0 >> 44; g0 &= 0xfffffffffffUL;
        var g1 = h1 + c2; c2 = g1 >> 44; g1 &= 0xfffffffffffUL;
        var g2 = h2 + c2 - (1UL << 42);
        var mask = (g2 >> 63) - 1UL;
        h0 = (h0 & ~mask) | (g0 & mask);
        h1 = (h1 & ~mask) | (g1 & mask);
        h2 = (h2 & ~mask) | (g2 & mask);

        var f0 = h0 | (h1 << 44);
        var f1 = (h1 >> 20) | (h2 << 24);
        var k0 = BinaryPrimitives.ReadUInt64LittleEndian(otk[16..]);
        var k1 = BinaryPrimitives.ReadUInt64LittleEndian(otk[24..]);
        var sum0 = (UInt128)f0 + k0;
        var sum1 = (UInt128)f1 + k1 + (sum0 >> 64);
        BinaryPrimitives.WriteUInt64LittleEndian(tag, (ulong)sum0);
        BinaryPrimitives.WriteUInt64LittleEndian(tag[8..], (ulong)sum1);
    }

    private static void Quarter(Span<uint> s, int a, int b, int c, int d)
    {
        s[a] += s[b]; s[d] = uint.RotateLeft(s[d] ^ s[a], 16);
        s[c] += s[d]; s[b] = uint.RotateLeft(s[b] ^ s[c], 12);
        s[a] += s[b]; s[d] = uint.RotateLeft(s[d] ^ s[a], 8);
        s[c] += s[d]; s[b] = uint.RotateLeft(s[b] ^ s[c], 7);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        _aes?.Dispose();
        if (_chachaKey is not null) CryptographicOperations.ZeroMemory(_chachaKey);
    }
}
