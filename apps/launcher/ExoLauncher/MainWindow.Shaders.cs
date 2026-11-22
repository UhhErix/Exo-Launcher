namespace ExoLauncher;

public sealed partial class MainWindow
{
    internal Task<bool> ShadersVerifyAsync(string engineId, CancellationToken ct) =>
        App.Services.ShadersHost.VerifyAsync(engineId, ct);

    internal Task<bool> ShadersRebuildStartAsync(IReadOnlyList<string> engineIds, CancellationToken ct) =>
        App.Services.ShadersHost.RebuildStartAsync(engineIds, ct);

    internal Task<bool> ShadersScanAsync(CancellationToken ct) =>
        App.Services.ShadersHost.ScanAsync(ct);

    internal Task ShadersRebuildPauseAsync(CancellationToken ct) =>
        App.Services.ShadersHost.RebuildPauseAsync(ct);

    internal object ShadersAddonStatus() =>
        App.Services.ShadersHost.Status();

    internal Task<string?> ShadersRecorderSetAsync(string gameId, bool on, CancellationToken ct) =>
        App.Services.ShadersHost.RecorderSetAsync(gameId, on, ct);

    internal Task<string?> ShadersRecorderOffAsync(string gameId, CancellationToken ct) =>
        App.Services.ShadersHost.RecorderOffAsync(gameId, ct);

    internal Task<object> ShadersAddonCallAsync(string? method, System.Text.Json.Nodes.JsonNode? parameters, CancellationToken ct) =>
        App.Services.ShadersHost.CallAsync(method, parameters, ct);
}
