using ExoLauncher.Services.SteamSignIn;

namespace ExoLauncher.Tests;

/// <summary>
/// The sign-in messages, against Steam's field numbers. A QR answer and a password answer put the same confirmation
/// list in different fields (5 and 4), and a swap there would silently skip the Steam Guard prompt.
/// </summary>
public sealed class SteamAuthMessagesTests
{
    [Fact]
    public void QrAnswerReadsItsChallengeAndConfirmations()
    {
        var answer = new SteamProtoWriter()
            .Varint(1, 42)
            .String(2, "https://s.team/q/1/77")
            .Bytes(3, [9, 8])
            .Float(4, 5f)
            .Message(5, new SteamProtoWriter().Varint(1, 4).String(2, "approve").ToArray())
            .ToArray();

        var start = SteamAuthMessages.ReadQrStart(answer);

        Assert.Equal(42UL, start.ClientId);
        Assert.Equal("https://s.team/q/1/77", start.ChallengeUrl);
        Assert.Equal([9, 8], start.RequestId);
        Assert.Equal(5f, start.Interval);
        var only = Assert.Single(start.Confirmations);
        Assert.Equal(SteamGuardKind.DeviceConfirmation, only.Kind);
        Assert.Equal("approve", only.Message);
    }

    [Fact]
    public void PasswordAnswerReadsItsSteamIdAndConfirmationsFromItsOwnFields()
    {
        var answer = new SteamProtoWriter()
            .Varint(1, 7)
            .Bytes(2, [1])
            .Float(3, 4f)
            .Message(4, new SteamProtoWriter().Varint(1, 3).ToArray())
            .Varint(5, 76561197960265729)
            .ToArray();

        var start = SteamAuthMessages.ReadCredentialsStart(answer);

        Assert.Equal(76561197960265729UL, start.SteamId);
        Assert.Equal(SteamGuardKind.DeviceCode, Assert.Single(start.Confirmations).Kind);
    }

    [Fact]
    public void PollAnswerReadsTheTokens()
    {
        var answer = new SteamProtoWriter()
            .Varint(1, 8)
            .String(3, "refresh.jwt")
            .String(4, "access.jwt")
            .String(6, "someone")
            .String(7, "guard")
            .ToArray();

        var poll = SteamAuthMessages.ReadPoll(answer);

        Assert.Equal("refresh.jwt", poll.RefreshToken);
        Assert.Equal("access.jwt", poll.AccessToken);
        Assert.Equal("someone", poll.AccountName);
        Assert.Equal("guard", poll.GuardData);
    }

    [Fact]
    public void GuardCodeRequestCarriesTheSteamIdAsFixed64()
    {
        var bytes = SteamAuthMessages.GuardCodeRequest(1, 76561197960265729, "ABCDE", SteamGuardKind.DeviceCode);
        var r = new SteamProtoReader(bytes);

        Assert.Equal(1UL, r.Varint(1));
        Assert.Equal(76561197960265729UL, r.Fixed64(2));
        Assert.Equal("ABCDE", r.String(3));
        Assert.Equal(3UL, r.Varint(4));
    }

    [Fact]
    public void UnknownGuardKindReadsAsNone()
    {
        Assert.Equal(SteamGuardKind.None, SteamAuthMessages.KindOf(99));
    }

    [Fact]
    public void RevokeIsPermanent()
    {
        var r = new SteamProtoReader(SteamAuthMessages.RevokeRequest("rt"));

        Assert.Equal("rt", r.String(1));
        Assert.Equal(1UL, r.Varint(2));
    }
}
