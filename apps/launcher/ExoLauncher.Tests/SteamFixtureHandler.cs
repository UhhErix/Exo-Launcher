using System.Net;
using System.Text;

namespace ExoLauncher.Tests;

/// <summary>Answers Steam calls from recorded fixtures and keeps every request, so tests can read what Exo sent.</summary>
internal sealed class SteamFixtureHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string? EResult, byte[] Body)> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        var (status, eresult, body) = answer(request);
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        if (eresult is not null) response.Headers.Add("x-eresult", eresult);
        return response;
    }


    public static SteamFixtureHandler Json(string json) => new(_ => (HttpStatusCode.OK, null, Encoding.UTF8.GetBytes(json)));
}
