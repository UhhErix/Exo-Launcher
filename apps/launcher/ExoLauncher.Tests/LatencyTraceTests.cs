using ExoLauncher.Helpers;
using static ExoLauncher.Helpers.LatencyTrace;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the latency check could report wrongly (the trace itself needs an administrator and runs on a real PC; this is the
/// arithmetic around it):
/// - an event that isn't a DPC or ISR (or is too short) is counted, or InitialTime and the routine are read swapped;
/// - a routine is named for the wrong driver (the next one up, or one below every base);
/// - a clock that went back, or a lost pair a minute apart, becomes the longest run;
/// - ticks are turned into microseconds with the wrong rate, or DPCs and ISRs mixed in each other's longest;
/// - the elevated check writes anywhere but one answer file name in a full path.
/// </summary>
public sealed class LatencyTraceTests
{
    private static byte[] Payload(ulong initial, ulong routine)
    {
        var data = new byte[24];
        BitConverter.TryWriteBytes(data.AsSpan(0), initial);
        BitConverter.TryWriteBytes(data.AsSpan(8), routine);
        return data;
    }

    [Theory]
    [InlineData(66, false)]
    [InlineData(68, false)]
    [InlineData(69, false)]
    [InlineData(67, true)]
    [InlineData(50, true)]
    public void DpcAndIsrEventsAreRead(byte opcode, bool interrupt)
    {
        var run = Parse(opcode, Payload(1234, 0xFFFFF80012345678));
        Assert.NotNull(run);
        Assert.Equal(interrupt, run.Value.Interrupt);
        Assert.Equal(1234, run.Value.Initial);
        Assert.Equal(0xFFFFF80012345678, run.Value.Routine);
    }

    [Fact]
    public void OtherEventsAndShortOnesAreNot()
    {
        Assert.Null(Parse(46, Payload(1, 2)));      // SampledProfile
        Assert.Null(Parse(68, new byte[12]));
    }

    [Fact]
    public void AnAddressIsNamedForTheDriverItFallsIn()
    {
        var modules = new List<Module> { new("ntoskrnl.exe", 0x1000), new("ndis.sys", 0x5000), new("nvlddmkm.sys", 0x9000) };
        Assert.Null(ModuleOf(0x0FFF, modules));
        Assert.Equal("ntoskrnl.exe", ModuleOf(0x1000, modules));
        Assert.Equal("ntoskrnl.exe", ModuleOf(0x4FFF, modules));
        Assert.Equal("ndis.sys", ModuleOf(0x5001, modules));
        Assert.Equal("nvlddmkm.sys", ModuleOf(0xFFFFFF, modules));
    }

    [Fact]
    public void LongestRunsAreInMicrosecondsByDriverAndKind()
    {
        var stats = new Stats();
        var f = System.Diagnostics.Stopwatch.Frequency;
        stats.Add(false, 0x9100, f / 10_000);      // 100 µs DPC in nvlddmkm
        stats.Add(false, 0x9200, f / 2_000);       // 500 µs DPC in nvlddmkm
        stats.Add(true, 0x5100, f / 25_000);       // 40 µs ISR in ndis
        stats.Add(false, 0x5200, -5);              // a clock that went back
        stats.Add(false, 0x5300, f * 60);          // a lost pair
        var modules = new List<Module> { new("nvlddmkm.sys", 0x9000), new("ndis.sys", 0x5000) };
        var r = Summarize(stats, modules, f, 10, DateTimeOffset.UnixEpoch);
        Assert.Equal(2, r.Dpcs);
        Assert.Equal(1, r.Isrs);
        Assert.Equal(500, r.DpcMaxUs, 0);
        Assert.Equal("nvlddmkm.sys", r.DpcDriver);
        Assert.Equal(40, r.IsrMaxUs, 0);
        Assert.Equal("ndis.sys", r.IsrDriver);
        Assert.Equal("nvlddmkm.sys", r.Drivers[0].Name);
        Assert.Equal(0, r.Drivers.Single(d => d.Name == "ndis.sys").DpcMaxUs);
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\exo-0123456789abcdef0123456789abcdef.latency.json", 0)]
    public void OnlyItsAnswerFileIsWritten(string path, int _)
    {
        Assert.Matches(AnswerName(), Path.GetFileName(path));
        Assert.DoesNotMatch(AnswerName(), "exo-0123.latency.json");
        Assert.DoesNotMatch(AnswerName(), "ntoskrnl.exe");
        Assert.Equal(2, Run([Flag, @"C:\Windows\System32\drivers\etc\hosts"]));
        Assert.Equal(2, Run([Flag, "exo-0123456789abcdef0123456789abcdef.latency.json"]));
        Assert.Equal(2, Run([Flag]));
    }
}
