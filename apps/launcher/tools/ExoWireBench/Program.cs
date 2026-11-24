using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExoWireBench;

public sealed record GameDto(
    string Id,
    string Store,
    string Title,
    bool Installed,
    string? CoverUrl,
    double PlaytimeHours,
    long SizeBytes,
    long LastPlayedUnix,
    bool AntiCheat,
    string[] Tags);

public sealed record LibraryPayload(int SchemaVersion, long GeneratedAtUnix, List<GameDto> Games);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LibraryPayload))]
internal partial class ExoWireJsonContext : JsonSerializerContext { }

internal static class ExoWireCodec
{
    internal const uint WireMagic = 0x31575845u;
    internal const byte WireVersion = 1;
    internal const int HeaderLen = 20;
    internal const byte KindRequest = 1;
    internal const byte KindResponse = 2;
    internal const byte KindBinaryMedia = 4;
    internal const ushort MethodLibraryGet = 1;
    internal const ushort MethodMediaFrame = 2;

    internal static void WriteHeader(Span<byte> dest, byte kind, ushort methodId, ulong reqId, uint payloadLen)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest[0..4], WireMagic);
        dest[4] = WireVersion;
        dest[5] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[6..8], methodId);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[8..16], reqId);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[16..20], payloadLen);
    }

    internal static void EncodeLibrary(LibraryPayload payload, ArrayBufferWriter<byte> writer)
    {
        writer.ResetWrittenCount();
        WriteI32(writer, payload.SchemaVersion);
        WriteI64(writer, payload.GeneratedAtUnix);
        WriteU32(writer, (uint)payload.Games.Count);
        foreach (var g in payload.Games)
        {
            WriteStr(writer, g.Id);
            WriteStr(writer, g.Store);
            WriteStr(writer, g.Title);
            WriteU8(writer, g.Installed ? (byte)1 : (byte)0);
            WriteOptStr(writer, g.CoverUrl);
            WriteF64(writer, g.PlaytimeHours);
            WriteI64(writer, g.SizeBytes);
            WriteI64(writer, g.LastPlayedUnix);
            WriteU8(writer, g.AntiCheat ? (byte)1 : (byte)0);
            WriteU32(writer, (uint)g.Tags.Length);
            foreach (var t in g.Tags) WriteStr(writer, t);
        }
    }

    static void WriteU8(ArrayBufferWriter<byte> w, byte v) { var s = w.GetSpan(1); s[0] = v; w.Advance(1); }
    static void WriteU32(ArrayBufferWriter<byte> w, uint v) { BinaryPrimitives.WriteUInt32LittleEndian(w.GetSpan(4), v); w.Advance(4); }
    static void WriteI32(ArrayBufferWriter<byte> w, int v) { BinaryPrimitives.WriteInt32LittleEndian(w.GetSpan(4), v); w.Advance(4); }
    static void WriteI64(ArrayBufferWriter<byte> w, long v) { BinaryPrimitives.WriteInt64LittleEndian(w.GetSpan(8), v); w.Advance(8); }
    static void WriteF64(ArrayBufferWriter<byte> w, double v) { BinaryPrimitives.WriteDoubleLittleEndian(w.GetSpan(8), v); w.Advance(8); }
    static void WriteStr(ArrayBufferWriter<byte> w, string s)
    {
        int len = Encoding.UTF8.GetByteCount(s);
        WriteU32(w, (uint)len);
        Encoding.UTF8.GetBytes(s, w.GetSpan(len));
        w.Advance(len);
    }
    static void WriteOptStr(ArrayBufferWriter<byte> w, string? s)
    {
        if (s is null) { WriteU8(w, 0); return; }
        WriteU8(w, 1);
        WriteStr(w, s);
    }
}

internal static class Program
{
    static LibraryPayload MakeLibrary(int count)
    {
        var stores = new[] { "steam", "epic", "gog", "xbox", "riot", "ea" };
        var tags = new[] { "fps", "rpg", "coop", "hdr", "dlss", "verified" };
        var games = new List<GameDto>(count);
        for (int i = 0; i < count; i++)
        {
            games.Add(new GameDto(
                $"{stores[i % stores.Length]}:{100000 + i}",
                stores[i % stores.Length],
                $"Sample Exo Library Title Number {i:D4} Definitive Edition",
                (i % 3) != 0,
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{100000 + i}/library_600x900_2x.jpg",
                (i * 1.75) % 420.5,
                1_500_000_000L + (i % 80) * 1_200_000_000L,
                1_760_000_000L + i * 3600L,
                (i % 5) == 0,
                [tags[i % tags.Length], tags[(i + 2) % tags.Length]]));
        }
        return new LibraryPayload(1, 1_764_000_000L, games);
    }

    static PipeSecurity CreateCurrentUserPipeSecurity()
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return sec;
    }

    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--test")
        {
            var p = MakeLibrary(10);
            var testBuf = new ArrayBufferWriter<byte>(4096);
            ExoWireCodec.EncodeLibrary(p, testBuf);
            if (testBuf.WrittenCount < 200) return 1;
            Console.WriteLine($"ExoWireBench self-test OK ({testBuf.WrittenCount} bytes)");
            return 0;
        }
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: ExoWireBench <json-refl|json-sg|wire> <pipeName> <iters>");
            return 2;
        }

        var mode = args[0];
        var pipeName = args[1];
        int totalIters = int.Parse(args[2]) + 10;
        var payload = MakeLibrary(200);
        var reflOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var mediaFrame = new byte[128 * 1024];
        for (int i = 0; i < mediaFrame.Length; i++) mediaFrame[i] = (byte)(i * 31);

        using var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            128 * 1024,
            128 * 1024,
            CreateCurrentUserPipeSecurity());

        Console.WriteLine("READY");
        Console.Out.Flush();
        pipe.WaitForConnection();

        var bw = new ArrayBufferWriter<byte>(128 * 1024);
        var hdr = new byte[ExoWireCodec.HeaderLen];
        long allocBefore = 0;
        var sw = new Stopwatch();

        if (mode is "json-refl" or "json-sg")
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
            for (int i = 0; i < totalIters; i++)
            {
                if (i == 10) { allocBefore = GC.GetAllocatedBytesForCurrentThread(); sw.Start(); }
                if (reader.ReadLine() is null) break;
                bw.ResetWrittenCount();
                using (var jw = new Utf8JsonWriter(bw))
                {
                    if (mode == "json-refl") JsonSerializer.Serialize(jw, payload, reflOpts);
                    else JsonSerializer.Serialize(jw, payload, ExoWireJsonContext.Default.LibraryPayload);
                }
                var nl = bw.GetSpan(1);
                nl[0] = (byte)'\n';
                bw.Advance(1);
                pipe.Write(bw.WrittenSpan);
                pipe.Flush();
            }
        }
        else
        {
            for (int i = 0; i < totalIters; i++)
            {
                if (i == 10) { allocBefore = GC.GetAllocatedBytesForCurrentThread(); sw.Start(); }
                pipe.ReadExactly(hdr);
                ushort methodId = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(6, 2));
                ulong reqId = BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(8, 8));
                if (methodId == ExoWireCodec.MethodMediaFrame)
                {
                    ExoWireCodec.WriteHeader(hdr, ExoWireCodec.KindBinaryMedia, methodId, reqId, (uint)mediaFrame.Length);
                    pipe.Write(hdr);
                    pipe.Write(mediaFrame);
                    pipe.Flush();
                }
                else
                {
                    ExoWireCodec.EncodeLibrary(payload, bw);
                    ExoWireCodec.WriteHeader(hdr, ExoWireCodec.KindResponse, methodId, reqId, (uint)bw.WrittenCount);
                    pipe.Write(hdr);
                    pipe.Write(bw.WrittenSpan);
                    pipe.Flush();
                }
            }
        }

        sw.Stop();
        int measured = totalIters - 10;
        long allocPerOp = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / measured;
        Console.WriteLine($"SERVER mode={mode} measured={measured} server_alloc_bytes_per_op={allocPerOp} total_ms={sw.Elapsed.TotalMilliseconds:F1}");
        return 0;
    }
}
