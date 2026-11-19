using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Exo.Identity;

/// <summary>
/// The mailbox's media parts (cloud/mailbox/src/media.js): <c>PUT</c>, <c>GET</c> and <c>DELETE</c>
/// <c>/v1/media/&lt;box&gt;/&lt;name&gt;</c>, each signed by the box's own key over <c>"exo-media/1" || method || box id ||
/// name || Unix seconds || SHA-256(body)</c>. What goes up is already sealed (<see cref="MediaSync"/>); the Worker stores
/// ciphertext under names it can't trace to a file.
/// </summary>
public sealed class MediaStore(Uri baseUri, HttpClient http)
{
    private static readonly byte[] Label = "exo-media/1"u8.ToArray();

    /// <summary>The media routes' base for a mailbox's WebSocket address (<c>ws</c> to <c>http</c>, <c>wss</c> to <c>https</c>).</summary>
    public static Uri? BaseOf(Uri mailbox) => mailbox.Scheme switch
    {
        "ws" => new UriBuilder(mailbox) { Scheme = "http", Path = "/" }.Uri,
        "wss" => new UriBuilder(mailbox) { Scheme = "https", Path = "/" }.Uri,
        "http" or "https" => new UriBuilder(mailbox) { Path = "/" }.Uri,
        _ => null,
    };

    public async Task PutAsync(IDeviceSigner boxKey, string name, byte[] body, CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Put, boxKey, name, body), ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created) throw new HttpRequestException($"The media store answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    /// <summary>A part's bytes, or null when the store has none by that name.</summary>
    public async Task<byte[]?> GetAsync(IDeviceSigner boxKey, string name, CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, boxKey, name, []), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"The media store answered {(int)response.StatusCode}.", null, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(IDeviceSigner boxKey, string name, CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Delete, boxKey, name, []), ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotFound))
            throw new HttpRequestException($"The media store answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    /// <summary>What a request's signature is over: <c>"exo-media/1" || method || box id || name || time || SHA-256(body)</c>.</summary>
    public static byte[] Signed(string method, ReadOnlySpan<byte> boxId, string name, string time, ReadOnlySpan<byte> body) =>
        [.. Label, .. Encoding.ASCII.GetBytes(method), .. boxId, .. Encoding.ASCII.GetBytes(name), .. Encoding.ASCII.GetBytes(time), .. SHA256.HashData(body)];

    private HttpRequestMessage Request(HttpMethod method, IDeviceSigner boxKey, string name, byte[] body)
    {
        var box = SHA256.HashData(boxKey.PublicKey).AsSpan(0, 16).ToArray();
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var message = Signed(method.Method, box, name, time, body);
        var request = new HttpRequestMessage(method, new Uri(baseUri, $"v1/media/{Convert.ToHexStringLower(box)}/{name}"));
        request.Headers.Add("x-exo-key", Base64Url.EncodeToString(boxKey.PublicKey));
        request.Headers.Add("x-exo-time", time);
        request.Headers.Add("x-exo-sig", Base64Url.EncodeToString(boxKey.Sign(message)));
        if (method == HttpMethod.Put) request.Content = new ByteArrayContent(body);
        return request;
    }
}
