using System.Text.Json;
using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class DiscordParityMockTests
{
    [Fact]
    public void DisabledUnlessBothTestEnvVarsAreSet()
    {
        var prevTest = Environment.GetEnvironmentVariable("EXO_E2E_TEST_INSTANCE");
        var prevMock = Environment.GetEnvironmentVariable("EXO_DISCORD_PARITY_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("EXO_E2E_TEST_INSTANCE", null);
            Environment.SetEnvironmentVariable("EXO_DISCORD_PARITY_MOCK", null);
            Assert.False(DiscordParityMock.IsEnabled);
            Assert.False(DiscordParityMock.TryHandle("discord.home", default, false, out _));

            Environment.SetEnvironmentVariable("EXO_E2E_TEST_INSTANCE", "1");
            Assert.False(DiscordParityMock.IsEnabled);

            Environment.SetEnvironmentVariable("EXO_E2E_TEST_INSTANCE", null);
            Environment.SetEnvironmentVariable("EXO_DISCORD_PARITY_MOCK", "1");
            Assert.False(DiscordParityMock.IsEnabled);

            Environment.SetEnvironmentVariable("EXO_E2E_TEST_INSTANCE", "1");
            Environment.SetEnvironmentVariable("EXO_DISCORD_PARITY_MOCK", "1");
            Assert.True(DiscordParityMock.IsEnabled);
            Assert.True(DiscordParityMock.TryHandle("discord.status", default, false, out var status));
            Assert.NotNull(status);
            Assert.True(DiscordParityMock.TryHandle("friends.badge", default, false, out var badge));
            Assert.NotNull(badge);
            Assert.False(DiscordParityMock.TryHandle("settings.get", default, false, out _));

            using var doc = JsonDocument.Parse("""{"action":"sources"}""");
            Assert.True(DiscordParityMock.TryHandle("discord.golive", doc.RootElement, true, out var gl));
            var glJson = JsonSerializer.Serialize(gl);
            Assert.Contains("\"VALORANT\"", glJson, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXO_E2E_TEST_INSTANCE", prevTest);
            Environment.SetEnvironmentVariable("EXO_DISCORD_PARITY_MOCK", prevMock);
        }
    }
}
