using System.Net.Sockets;
using ExoLauncher.Adapters;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Steam's store queries and Riot's install request. A DNS miss has to be recognizable so it can be asked again, and a
/// failed query must not look like an empty catalog. Riot's refusal is one sentence, and a patch read that keeps missing
/// stops instead of polling until the client is gone.
/// </summary>
public sealed class StoreRequestTests
{
    [Fact]
    public void Dns_miss_is_a_resolver_that_did_not_answer_only()
    {
        var dns = new HttpRequestException("No such host is known.", new SocketException((int)SocketError.HostNotFound));
        Assert.True(StoreService.DnsMiss(dns));
        // app.log, 10:13: "The requested name is valid, but no data of the requested type was found", cleared on the next ask.
        Assert.True(StoreService.DnsMiss(new HttpRequestException("no data", new SocketException((int)SocketError.NoData))));
        Assert.True(StoreService.DnsMiss(new HttpRequestException("try again", new SocketException((int)SocketError.TryAgain))));
        Assert.False(StoreService.DnsMiss(new HttpRequestException("The remote server returned an error.")));
        Assert.False(StoreService.DnsMiss(new SocketException((int)SocketError.ConnectionRefused)));
    }

    [Fact]
    public void Riot_install_refusal_is_one_sentence()
    {
        Assert.Equal("Riot is not ready to install this game yet. Try again.", RiotAdapter.InstallRefusal(404));
        Assert.Equal("Riot could not start an install it needs. Try again.", RiotAdapter.InstallRefusal(500));
        Assert.Equal("Riot did not answer the install request. Try again.", RiotAdapter.InstallRefusal(0));
        Assert.Equal("Riot did not accept the install request.", RiotAdapter.InstallRefusal(403));
        Assert.Equal(40, RiotAdapter.PatchReadGiveUp);
    }
}
