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
