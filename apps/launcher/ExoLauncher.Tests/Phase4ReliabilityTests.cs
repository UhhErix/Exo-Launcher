using System.Text.Json;
using ExoLauncher.Services;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Tests;

public sealed class Phase4ReliabilityTests
{
    [Fact]
    public void VoiceConnectionFallbackToH264ClearsHevcState()
    {
        var conn = new VoiceConnection("us-east.discord.media:443", "100", "200", "300", "sess", "tok", listening: true, screen: true)
        {
            EncodesH265 = true,
        };
        using var doc = JsonDocument.Parse("""{"video_codec":"H265"}""");
        conn.UseCodec(doc.RootElement);

        Assert.True(conn.IsHevc);

        conn.FallbackToH264();

        Assert.False(conn.EncodesH265);
        Assert.False(conn.IsHevc);
    }

    [Fact]
    public void PickInstalledValueHandlesAmdAndIntelFallbacks()
    {
        Assert.Equal("26.10.1", GpuDriverUpdate.PickInstalledValue("amd", " 26.10.1 ", "32.0.12000.1"));
        Assert.Equal("32.0.12000.1", GpuDriverUpdate.PickInstalledValue("amd", null, " 32.0.12000.1 "));
        Assert.Equal("32.0.12000.1", GpuDriverUpdate.PickInstalledValue("amd", "   ", "32.0.12000.1"));
        Assert.Equal("32.0.101.9150", GpuDriverUpdate.PickInstalledValue("intel", "99.9.9", " 32.0.101.9150 "));
        Assert.Null(GpuDriverUpdate.PickInstalledValue("amd", " ", ""));
        Assert.Null(GpuDriverUpdate.PickInstalledValue("intel", "26.10.1", null));
    }
}
