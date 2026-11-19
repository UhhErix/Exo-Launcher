namespace ExoLauncher.Services;

/// <summary>The last acknowledged target. Calls are serialized by the host.</summary>
internal sealed class CaptureGameTarget
{
    private long _generation;
    private bool _known;
    private string? _gameId;

    internal bool Begin(long generation, string? gameId)
    {
        if (_generation != generation) Acknowledge(generation, null);
        if (_known && string.Equals(_gameId, gameId, StringComparison.OrdinalIgnoreCase)) return false;
        // A lost reply does not mean the add-on didn't apply the request.
        _known = false;
        return true;
    }

    internal void Acknowledge(long generation, string? gameId)
    {
        (_generation, _gameId, _known) = (generation, gameId, true);
    }
}
