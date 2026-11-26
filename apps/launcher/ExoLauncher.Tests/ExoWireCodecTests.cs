using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class ExoWireCodecTests
{
    [Fact]
    public void SchemaTablesAreSortedInvertibleAndContainHotEntries()
    {
        Assert.True(ExoWireSchema.Methods.Length > 300);
        Assert.True(ExoWireSchema.Events.Length > 50);
        Assert.NotEqual(0UL, ExoWireSchema.SchemaHash);
        Assert.NotEqual(ExoWireSchema.UnknownId, ExoWireSchema.MethodLibraryGet);
        Assert.NotEqual(ExoWireSchema.UnknownId, ExoWireSchema.MethodGameGet);
        Assert.NotEqual(ExoWireSchema.UnknownId, ExoWireSchema.MethodGameExtras);
        Assert.NotEqual(ExoWireSchema.UnknownId, ExoWireSchema.MethodShellBootstrap);
        Assert.NotEqual(ExoWireSchema.UnknownId, ExoWireSchema.MethodRpcCancel);
        Assert.NotEqual(ExoWireSchema.UnknownId, ExoWireSchema.EventAutopilotLive);
        Assert.NotEqual(ExoWireSchema.UnknownId, ExoWireSchema.EventInstallProgress);
        Assert.NotEqual(ExoWireSchema.UnknownId, ExoWireSchema.EventLibraryUpdated);

        for (var i = 0; i < ExoWireSchema.Methods.Length; i++)
        {
            var id = ExoWireSchema.GetMethodId(ExoWireSchema.Methods[i]);
            Assert.Equal((ushort)(i + 1), id);
            Assert.Equal(ExoWireSchema.Methods[i], ExoWireSchema.GetMethodName(id));
        }

        for (var i = 0; i < ExoWireSchema.Events.Length; i++)
        {
            var id = ExoWireSchema.GetEventId(ExoWireSchema.Events[i]);
            Assert.Equal((ushort)(i + 1), id);
            Assert.Equal(ExoWireSchema.Events[i], ExoWireSchema.GetEventName(id));
        }

        Assert.Equal(ExoWireSchema.UnknownId, ExoWireSchema.GetMethodId("unknown.method.test"));
        Assert.Null(ExoWireSchema.GetMethodName(0));
    }

    [Fact]
    public async Task NegotiatesBinaryWireRoundTripsHotResponsesEventsAndMedia()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-wire-test-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);

        transport.WireRequestReceived += req =>
        {
            try
            {
                if (req.MethodName == "game.get")
                {
                    var reply = new GameGetReplyDto
                    {
                        Ok = true,
                        Game = new GamePageDto
                        {
                            Id = req.BinaryIdParam ?? "missing",
                            Title = "Control Resonant",
                            Store = "steam",
                            Stores = ["steam"],
                            Installed = true,
                            Owned = true,
                            EntitlementState = EntitlementState.Owned,
                            PlaytimeMinutes = 120,
                            Tags = ["Action", "Singleplayer"],
                        },
                    };
                    transport.PostWireResponse(req.ReqId, req.MethodId, ok: true, result: reply, error: null);
                }
                else if (req.MethodName == "system.about")
                {
                    transport.PostWireResponse(req.ReqId, req.MethodId, ok: true, result: new { ok = true, version = "test" }, error: null);
                }
                else
                {
                    transport.PostWireResponse(req.ReqId, req.MethodId, ok: false, result: null, error: "nope");
                }
            }
            finally
            {
                req.Dispose();
            }
        };
        transport.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);

        var hello = BuildHelloFrame(ExoWireSchema.SchemaHash, ExoWireCodec.FlagUseSchemaIds | ExoWireCodec.FlagBinaryHotCodecs);
        await client.WriteAsync(hello, ct);

        var ackBuf = new byte[ExoWireCodec.HeaderLen + ExoWireCodec.HelloPayloadLen];
        await client.ReadExactlyAsync(ackBuf, ct);
        Assert.True(ExoWireFrameHeader.TryRead(ackBuf, out var ackHdr));
        Assert.Equal(ExoWireFrameKind.HelloAck, ackHdr.Kind);
        Assert.Equal(ExoWireCodec.WireVersion, ackBuf[ExoWireCodec.HeaderLen]);
        Assert.Equal(ExoWireCodec.HelloStatusOk, ackBuf[ExoWireCodec.HeaderLen + 1]);
        Assert.True(transport.IsBinaryWireActive);
        Assert.True(transport.UseSchemaIds);
        Assert.True(transport.UseBinaryHotCodecs);

        var idBytes = Encoding.UTF8.GetBytes("steam:870780");
        var reqPayload = new byte[1 + 4 + idBytes.Length];
        reqPayload[0] = (byte)ExoWireEncoding.BinaryTyped;
        BinaryPrimitives.WriteUInt32LittleEndian(reqPayload.AsSpan(1), (uint)idBytes.Length);
        idBytes.CopyTo(reqPayload.AsSpan(5));
        await WriteClientFrameAsync(client, ExoWireFrameKind.Request, ExoWireSchema.MethodGameGet, 7, reqPayload, ct);

        var (respHdr, respBody) = await ReadClientFrameAsync(client, ct);
        Assert.Equal(ExoWireFrameKind.ResponseOk, respHdr.Kind);
        Assert.Equal(7UL, respHdr.ReqId);
        Assert.Equal(ExoWireSchema.MethodGameGet, respHdr.MethodId);
        Assert.Equal((byte)ExoWireEncoding.BinaryTyped, respBody[0]);
        Assert.Equal(1, respBody[1]);
        Assert.Equal(1, respBody[2]);

        var genericReq = new byte[] { (byte)ExoWireEncoding.Empty };
        await WriteClientFrameAsync(client, ExoWireFrameKind.Request, ExoWireSchema.GetMethodId("system.about"), 8, genericReq, ct);
        var (genHdr, genBody) = await ReadClientFrameAsync(client, ct);
        Assert.Equal(ExoWireFrameKind.ResponseOk, genHdr.Kind);
        Assert.Equal(8UL, genHdr.ReqId);
        Assert.Equal((byte)ExoWireEncoding.JsonUtf8, genBody[0]);
        using (var doc = JsonDocument.Parse(genBody.AsMemory(1)))
            Assert.Equal("test", doc.RootElement.GetProperty("version").GetString());

        transport.PostWireEvent("autopilot.live", new AutopilotLiveDto
        {
            Game = "steam:870780",
            Title = "Control Resonant",
            Seconds = 42,
            Fps = 144.2,
            Low1 = 112.5,
            Low01 = 98.0,
            Stutters = 1,
            Gpu = 95.0,
            GpuTemp = 64.0,
        });
        var (evHdr, evBody) = await ReadClientFrameAsync(client, ct);
        Assert.Equal(ExoWireFrameKind.Event, evHdr.Kind);
        Assert.Equal(ExoWireSchema.EventAutopilotLive, evHdr.MethodId);
        Assert.Equal((byte)ExoWireEncoding.BinaryTyped, evBody[0]);
        Assert.Equal(1, evBody[1]);

        byte[] rawMedia = [9, 8, 7, 6, 5, 4];
        transport.PostBinaryFrame("""{"source":"discord"}""", rawMedia);
        var (mediaHdr, mediaBody) = await ReadClientFrameAsync(client, ct);
        Assert.Equal(ExoWireFrameKind.BinaryMedia, mediaHdr.Kind);
        var metaLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(mediaBody);
        Assert.Equal("""{"source":"discord"}""", Encoding.UTF8.GetString(mediaBody, 4, metaLen));
        Assert.Equal(rawMedia, mediaBody[(4 + metaLen)..]);
    }

    [Fact]
    public async Task SchemaHashMismatchDisablesSchemaIdsAndSupportsInlineNamesAndReconnectToJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-wire-mismatch-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);

        transport.WireRequestReceived += req =>
        {
            try
            {
                transport.PostWireResponse(req.ReqId, req.MethodId, ok: true, result: new { method = req.MethodName }, error: null);
            }
            finally
            {
                req.Dispose();
            }
        };
        transport.MessageReceived += _ =>
        {
            transport.PostJson("""{"version":1,"id":"json-1","ok":true,"result":{"mode":"json"}}""");
        };
        transport.Start();

        using (var binClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await binClient.ConnectAsync(5000, ct);
            var hello = BuildHelloFrame(ExoWireSchema.SchemaHash ^ 0x1234UL, ExoWireCodec.FlagUseSchemaIds | ExoWireCodec.FlagBinaryHotCodecs);
            await binClient.WriteAsync(hello, ct);

            var ackBuf = new byte[ExoWireCodec.HeaderLen + ExoWireCodec.HelloPayloadLen];
            await binClient.ReadExactlyAsync(ackBuf, ct);
            Assert.Equal(ExoWireCodec.HelloStatusSchemaMismatch, ackBuf[ExoWireCodec.HeaderLen + 1]);
            Assert.False(transport.UseSchemaIds);

            var nameBytes = Encoding.UTF8.GetBytes("system.about");
            var inlinePayload = new byte[2 + nameBytes.Length + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(inlinePayload, (ushort)nameBytes.Length);
            nameBytes.CopyTo(inlinePayload.AsSpan(2));
            inlinePayload[2 + nameBytes.Length] = (byte)ExoWireEncoding.Empty;
            await WriteClientFrameAsync(binClient, ExoWireFrameKind.Request, ExoWireSchema.UnknownId, 99, inlinePayload, ct);

            var (hdr, body) = await ReadClientFrameAsync(binClient, ct);
            Assert.Equal(ExoWireFrameKind.ResponseOk, hdr.Kind);
            Assert.Equal(99UL, hdr.ReqId);
            using var doc = JsonDocument.Parse(body.AsMemory(1));
            Assert.Equal("system.about", doc.RootElement.GetProperty("method").GetString());
        }

        await Task.Delay(50, ct);

        using (var jsonClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await jsonClient.ConnectAsync(5000, ct);
            using var writer = new StreamWriter(jsonClient, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(jsonClient, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await writer.WriteLineAsync("""{"version":1,"id":"json-1","method":"system.about"}""".AsMemory(), ct);
            var line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.NotNull(line);
            Assert.False(transport.IsBinaryWireActive);
            using var doc = JsonDocument.Parse(line!);
            Assert.Equal("json", doc.RootElement.GetProperty("result").GetProperty("mode").GetString());
        }
    }

    [Fact]
    public async Task JsonOnlyLegacyUiNegotiatesDownAndServesEveryHostV1Method()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-json-all-methods-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);

        var wireRequestsSeen = 0;
        transport.WireRequestReceived += req =>
        {
            Interlocked.Increment(ref wireRequestsSeen);
            req.Dispose();
        };
        transport.MessageReceived += raw =>
        {
            using var doc = JsonDocument.Parse(raw);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var method = doc.RootElement.GetProperty("method").GetString()!;
            transport.PostJson(JsonSerializer.Serialize(new
            {
                version = 1,
                id,
                ok = true,
                result = new { method },
            }));
        };
        transport.Start();

        using var jsonClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await jsonClient.ConnectAsync(5000, ct);
        using var writer = new StreamWriter(jsonClient, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(jsonClient, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        for (var i = 0; i < ExoWireSchema.Methods.Length; i++)
        {
            var method = ExoWireSchema.Methods[i];
            var reqId = $"j-{i + 1}";
            var req = JsonSerializer.Serialize(new { version = 1, id = reqId, method, @params = new { } });
            await writer.WriteLineAsync(req.AsMemory(), ct);

            var replyLine = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.NotNull(replyLine);
            Assert.False(replyLine!.StartsWith("EXW1", StringComparison.Ordinal));
            using var doc = JsonDocument.Parse(replyLine);
            Assert.Equal(reqId, doc.RootElement.GetProperty("id").GetString());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(method, doc.RootElement.GetProperty("result").GetProperty("method").GetString());
        }

        Assert.False(transport.IsBinaryWireActive);
        Assert.Equal(0, Volatile.Read(ref wireRequestsSeen));
    }

    [Fact]
    public void FuzzAndCorruptWireFramesDropToErrorNeverPanicOrHang()
    {
        var validHdr = new byte[ExoWireCodec.HeaderLen];
        new ExoWireFrameHeader(ExoWireCodec.WireVersion, ExoWireFrameKind.Request, ExoWireSchema.MethodGameGet, 1, 16)
            .WriteTo(validHdr);

        for (var v = 0; v <= 255; v++)
        {
            if (v == ExoWireCodec.WireVersion) continue;
            validHdr[4] = (byte)v;
            Assert.False(ExoWireFrameHeader.TryRead(validHdr, out _));
        }
        validHdr[4] = ExoWireCodec.WireVersion;

        foreach (byte badKind in new byte[] { 0, 8, 9, 127, 255 })
        {
            validHdr[5] = badKind;
            Assert.False(ExoWireFrameHeader.TryRead(validHdr, out _));
        }
        validHdr[5] = (byte)ExoWireFrameKind.Request;

        foreach (var huge in new uint[] { ExoWireCodec.MaxFrameBytes + 1, uint.MaxValue / 2, uint.MaxValue })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(validHdr.AsSpan(16), huge);
            Assert.False(ExoWireFrameHeader.TryRead(validHdr, out _));
        }

        for (var len = 0; len < ExoWireCodec.HeaderLen; len++)
            Assert.False(ExoWireFrameHeader.TryRead(validHdr.AsSpan(0, len), out _));

        for (var len = 0; len < ExoWireCodec.HelloPayloadLen; len++)
            Assert.False(ExoWireHello.TryRead(new byte[len], out _));
        Assert.False(ExoWireHello.TryRead(new byte[ExoWireCodec.HelloPayloadLen + 1], out _));

        Assert.False(ExoWireCodec.TryDecodeRequest(
            new ExoWireFrameHeader(ExoWireCodec.WireVersion, ExoWireFrameKind.Request, ExoWireSchema.MethodGameGet, 1, 8),
            null,
            8,
            out _));

        byte[] invalidUtf8Name = [4, 0, 0xFF, 0xFE, 0xFD, 0xFC, (byte)ExoWireEncoding.Empty];
        Assert.False(ExoWireCodec.TryDecodeRequest(
            new ExoWireFrameHeader(ExoWireCodec.WireVersion, ExoWireFrameKind.Request, ExoWireSchema.UnknownId, 1, (uint)invalidUtf8Name.Length),
            invalidUtf8Name,
            invalidUtf8Name.Length,
            out _));

        byte[] overflowIdPayload = [(byte)ExoWireEncoding.BinaryTyped, 0xFF, 0xFF, 0xFF, 0x7F, 0x41];
        Assert.False(ExoWireCodec.TryDecodeRequest(
            new ExoWireFrameHeader(ExoWireCodec.WireVersion, ExoWireFrameKind.Request, ExoWireSchema.MethodGameGet, 1, (uint)overflowIdPayload.Length),
            overflowIdPayload,
            overflowIdPayload.Length,
            out _));

        ulong seed = 0x517c_c1b7_2722_0a95;
        ulong NextU64()
        {
            seed ^= seed << 13;
            seed ^= seed >> 7;
            seed ^= seed << 17;
            return seed;
        }

        for (var i = 0; i < 4096; i++)
        {
            var len = (int)(NextU64() % 96);
            var buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, len));
            for (var j = 0; j < len; j++)
                buf[j] = (byte)(NextU64() & 0xFF);
            if (i % 4 == 0 && len >= ExoWireCodec.HeaderLen)
                BinaryPrimitives.WriteUInt32LittleEndian(buf, ExoWireCodec.WireMagic);

            _ = ExoWireFrameHeader.TryRead(buf.AsSpan(0, len), out _);
            _ = ExoWireHello.TryRead(buf.AsSpan(0, len), out _);

            var reqHdr = new ExoWireFrameHeader(
                ExoWireCodec.WireVersion,
                (ExoWireFrameKind)((i % 7) + 1),
                (ushort)(NextU64() & 0xFFFF),
                (ulong)i,
                (uint)len);
            if (ExoWireCodec.TryDecodeRequest(reqHdr, buf, len, out var req) && req is not null)
                req.Dispose();
            else
                ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Fact]
    public async Task CorruptBinaryFrameOnLivePipeDropsCleanlyAndAcceptsNextClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-wire-corrupt-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);

        var resets = 0;
        var resetSignal = new SemaphoreSlim(0);
        transport.SessionReset += () =>
        {
            Interlocked.Increment(ref resets);
            resetSignal.Release();
        };
        transport.WireRequestReceived += req =>
        {
            try
            {
                transport.PostWireResponse(req.ReqId, req.MethodId, ok: true, result: new { ok = true, method = req.MethodName }, error: null);
            }
            finally
            {
                req.Dispose();
            }
        };
        transport.Start();

        byte[] oversizedHeader = new byte[ExoWireCodec.HeaderLen];
        new ExoWireFrameHeader(ExoWireCodec.WireVersion, ExoWireFrameKind.Request, ExoWireSchema.MethodGameGet, 1, 4).WriteTo(oversizedHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedHeader.AsSpan(16), uint.MaxValue);

        byte[][] corruptFrames =
        [
            Encoding.ASCII.GetBytes("BAD_MAGIC_CORRUPT_HEADER_1234567890"),
            oversizedHeader,
            [.. Encoding.ASCII.GetBytes("EXW1"), 99, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 1, 2],
        ];

        foreach (var bad in corruptFrames)
        {
            using (var badClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await badClient.ConnectAsync(5000, ct);
                var hello = BuildHelloFrame(ExoWireSchema.SchemaHash, ExoWireCodec.FlagUseSchemaIds | ExoWireCodec.FlagBinaryHotCodecs);
                await badClient.WriteAsync(hello, ct);
                var ack = new byte[ExoWireCodec.HeaderLen + ExoWireCodec.HelloPayloadLen];
                await badClient.ReadExactlyAsync(ack, ct);

                await badClient.WriteAsync(bad, ct);
                await badClient.FlushAsync(ct);
            }

            Assert.True(await resetSignal.WaitAsync(TimeSpan.FromSeconds(5), ct));
        }

        Assert.Equal(corruptFrames.Length, Volatile.Read(ref resets));

        using var goodClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await goodClient.ConnectAsync(5000, ct);
        var goodHello = BuildHelloFrame(ExoWireSchema.SchemaHash, ExoWireCodec.FlagUseSchemaIds | ExoWireCodec.FlagBinaryHotCodecs);
        await goodClient.WriteAsync(goodHello, ct);
        var goodAck = new byte[ExoWireCodec.HeaderLen + ExoWireCodec.HelloPayloadLen];
        await goodClient.ReadExactlyAsync(goodAck, ct);

        await WriteClientFrameAsync(goodClient, ExoWireFrameKind.Request, ExoWireSchema.GetMethodId("system.about"), 42, [(byte)ExoWireEncoding.Empty], ct);
        var (hdr, body) = await ReadClientFrameAsync(goodClient, ct);
        Assert.Equal(ExoWireFrameKind.ResponseOk, hdr.Kind);
        Assert.Equal(42UL, hdr.ReqId);
        using var doc = JsonDocument.Parse(body.AsMemory(1));
        Assert.Equal("system.about", doc.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task PreHandshakeJsonEventsDoNotCorruptBinaryWireSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-wire-prejson-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);
        transport.WireRequestReceived += req =>
        {
            try
            {
                transport.PostWireResponse(req.ReqId, req.MethodId, ok: true, result: new { ok = true }, error: null);
            }
            finally
            {
                req.Dispose();
            }
        };
        transport.Start();

        transport.PostJson("""{"version":1,"event":"update.state","data":{"state":"Checking"}}""");
        transport.PostJson("""{"version":1,"event":"friends.badge","data":{"count":2}}""");

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        await Task.Delay(25, ct);
        var hello = BuildHelloFrame(ExoWireSchema.SchemaHash, ExoWireCodec.FlagUseSchemaIds | ExoWireCodec.FlagBinaryHotCodecs);
        await client.WriteAsync(hello, ct);
        await client.FlushAsync(ct);

        var ack = new byte[ExoWireCodec.HeaderLen + ExoWireCodec.HelloPayloadLen];
        await client.ReadExactlyAsync(ack, ct);
        Assert.True(ExoWireFrameHeader.TryRead(ack.AsSpan(0, ExoWireCodec.HeaderLen), out var ackHdr));
        Assert.Equal(ExoWireFrameKind.HelloAck, ackHdr.Kind);

        await WriteClientFrameAsync(client, ExoWireFrameKind.Request, ExoWireSchema.GetMethodId("system.about"), 7, [(byte)ExoWireEncoding.Empty], ct);
        var (hdr, body) = await ReadClientFrameAsync(client, ct);
        Assert.Equal(ExoWireFrameKind.ResponseOk, hdr.Kind);
        Assert.Equal(7UL, hdr.ReqId);
        using var doc = JsonDocument.Parse(body.AsMemory(1));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    private static byte[] BuildHelloFrame(ulong schemaHash, ushort flags)
    {
        var buf = new byte[ExoWireCodec.HeaderLen + ExoWireCodec.HelloPayloadLen];
        var hdr = new ExoWireFrameHeader(ExoWireCodec.WireVersion, ExoWireFrameKind.Hello, 0, 0, ExoWireCodec.HelloPayloadLen);
        hdr.WriteTo(buf);
        buf[ExoWireCodec.HeaderLen] = ExoWireCodec.WireVersion;
        buf[ExoWireCodec.HeaderLen + 1] = ExoWireCodec.WireVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(ExoWireCodec.HeaderLen + 2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(ExoWireCodec.HeaderLen + 4), ExoWireCodec.MaxFrameBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(ExoWireCodec.HeaderLen + 8), schemaHash);
        return buf;
    }

    private static async Task WriteClientFrameAsync(
        Stream stream,
        ExoWireFrameKind kind,
        ushort methodId,
        ulong reqId,
        byte[] payload,
        CancellationToken ct)
    {
        var hdrBuf = new byte[ExoWireCodec.HeaderLen];
        var hdr = new ExoWireFrameHeader(ExoWireCodec.WireVersion, kind, methodId, reqId, (uint)payload.Length);
        hdr.WriteTo(hdrBuf);
        await stream.WriteAsync(hdrBuf, ct);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<(ExoWireFrameHeader Header, byte[] Payload)> ReadClientFrameAsync(Stream stream, CancellationToken ct)
    {
        var hdrBuf = new byte[ExoWireCodec.HeaderLen];
        await stream.ReadExactlyAsync(hdrBuf, ct);
        Assert.True(ExoWireFrameHeader.TryRead(hdrBuf, out var hdr));
        var payload = new byte[hdr.PayloadLen];
        if (payload.Length > 0)
            await stream.ReadExactlyAsync(payload, ct);
        return (hdr, payload);
    }
}
