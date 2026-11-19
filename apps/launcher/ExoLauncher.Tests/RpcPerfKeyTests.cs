using System.Text.Json;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The slow-call line in app.log (PERF rpc ... key=) must trace a call to the page or kind it was for without ever carrying
/// content. Ways it could fail: an id, uri, path, name or typed text slips into the line (a Discord snowflake, "am:albums:...",
/// a folder, a search); a param that is a number or a boolean prints as text; a long value bloats the line; and a call with
/// nothing to say prints an empty key that breaks the e2e regex's line.
/// </summary>
public class RpcPerfKeyTests
{
    private static string? Key(string json) => WebHostBridge.PerfKey(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void ANamedPageAndActionTraceTheCall() => Assert.Equal("view:playlist,action:play", Key("""{"view":"playlist","action":"play","uri":"am:playlists:pl.1"}"""));

    [Theory]
    [InlineData("""{"view":"123456789012345678"}""")]            // an id
    [InlineData("""{"action":"am:albums:1"}""")]                  // a uri
    [InlineData("""{"kind":"C:/Users/Erix"}""")]                  // a path
    [InlineData("""{"mode":"a typed sentence"}""")]               // text
    [InlineData("""{"type":"Capital"}""")]                        // names begin with a capital
    [InlineData("""{"part":"averyveryveryverylongwordhere"}""")]  // too long
    [InlineData("""{"view":7,"action":true}""")]                  // not strings
    [InlineData("""{"query":"lofi","userId":"x"}""")]             // not in the list
    [InlineData("""[1,2]""")]
    public void ContentIsLeftOut(string json) => Assert.Null(Key(json));
}
