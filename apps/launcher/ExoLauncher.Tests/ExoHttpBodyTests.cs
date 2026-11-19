using System.Net;
using System.Text;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a capped HTTP body read can go wrong: a body over its cap is returned whole (memory, and a parse of a page that
/// was never the answer), or a body under the cap comes back cut or changed.
/// </summary>
public sealed class ExoHttpBodyTests
{
    [Fact]
    public async Task ABodyUnderTheCapIsReadWhole()
    {
        var content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json");

        Assert.Equal("{\"ok\":true}", await ExoHttp.ReadBodyLimitedAsync(content, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task ABodyOverTheCapIsNullNotTruncated()
    {
        var content = new StringContent(new string('a', 5000), Encoding.UTF8, "text/plain");

        Assert.Null(await ExoHttp.ReadBodyLimitedAsync(content, 4096, CancellationToken.None));
    }
}
