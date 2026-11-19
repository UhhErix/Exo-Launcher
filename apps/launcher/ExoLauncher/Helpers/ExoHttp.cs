namespace ExoLauncher;

/// <summary>
/// The one place Exo's native code constructs an <see cref="HttpClient"/>.
/// Every other <c>new HttpClient(...)</c> is a build error (RS0030, see
/// BannedSymbols.txt), so finding every client means finding the callers of
/// this method. Callers keep their own handler, timeout and headers. Every
/// host a client contacts is listed in <see cref="NetworkDestinations"/>.
/// </summary>
internal static class ExoHttp
{
    /// <param name="handler">Null uses the default handler, exactly as <c>new HttpClient()</c> does.</param>
    internal static HttpClient Create(TimeSpan timeout, HttpMessageHandler? handler = null, bool disposeHandler = true)
    {
#pragma warning disable RS0030 // The sanctioned construction site: this factory is what the ban points to.
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler);
#pragma warning restore RS0030
        http.Timeout = timeout;
        return http;
    }

    /// <summary>
    /// A response body read as text, stopped once it passes <paramref name="maxBytes"/>: null then, so a huge or endless
    /// answer never fills memory. The read goes chunk by chunk, so a body with no length header is capped too.
    /// </summary>
    internal static async Task<string?> ReadBodyLimitedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: Math.Min(maxBytes, 64 * 1024));
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
}
