namespace ExoLauncher.Services;

/// <summary>
/// Transport boundary between <see cref="WebHostBridge"/> and the active UI shell
/// (WebView2 today or an out-of-process native Rune client over named pipes).
/// </summary>
public interface IHostTransport : IDisposable
{
    event Action<string>? MessageReceived;
    event Action? SessionReset;

    void PostJson(string json);
    void PostBinaryFrame(string metaJson, byte[] frame);
    void Detach();
}
