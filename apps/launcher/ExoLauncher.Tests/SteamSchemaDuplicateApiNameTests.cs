using System.Text;
using ExoLauncher.Models;
using ExoLauncher.Services.Achievements;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a Steam schema with two bits named the same api name can break the achievement list:
/// - the name lookup throws on the duplicate and the whole game's list loses its names;
/// - the later bit's display name replaces the first's, so the wrong words show.
/// </summary>
public sealed class SteamSchemaDuplicateApiNameTests
{
    private const string AppId = "1110910";

    private static byte[] Bytes(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static byte[] Open(string name) => [0x00, .. Encoding.UTF8.GetBytes(name), 0x00];

    private static byte[] Text(string name, string value) => [0x01, .. Encoding.UTF8.GetBytes(name), 0x00, .. Encoding.UTF8.GetBytes(value), 0x00];

    private static readonly byte[] Close = [0x08];

    // One bit: its api name and its English display name.
    private static byte[] Bit(string bit, string apiName, string display) =>
        Bytes(Open(bit), Text("name", apiName), Open("display"), Open("name"), Text("english", display), Close, Close, Close);

    // Two bits (0 and 1) of one stat, both named "Dup": the first is "First", the second "Second".
    private static byte[] Schema() => Bytes(
        Open(AppId), Open("stats"), Open("0"), Open("bits"),
        Bit("0", "Dup", "First"),
        Bit("1", "Dup", "Second"),
        Close, Close, Close, Close);

    private static AchievementSnapshot Snapshot(params AchievementEntry[] entries) => new()
    {
        ProviderId = "steam",
        SourceGameId = AppId,
        CoverageKey = "steam:test",
        Coverage = AchievementCoverageStatus.Complete,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        Entries = entries,
    };

    private static AchievementEntry Entry(string externalId) => new()
    {
        Definition = new AchievementDefinition
        {
            ProviderId = "steam",
            SourceGameId = AppId,
            ExternalId = externalId,
            Name = externalId,
        },
        State = new AchievementState { ExternalId = externalId, ObservedAtUtc = DateTimeOffset.UtcNow },
    };

    [Fact]
    public void ADuplicateApiNameDoesNotThrowAndTheFirstBitWins()
    {
        AchievementSnapshot? result = null;
        var error = Record.Exception(() =>
            result = SteamLibraryCacheAchievementProvider.WithSchemaNames(Snapshot(Entry("Dup")), Schema(), AppId, "english"));

        Assert.Null(error);
        Assert.Equal("First", Assert.Single(result!.Entries).Definition.Name);
    }
}
