using System.Text.Json;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Compress games for the page (<see cref="GameCompressor"/>): one game's state for its Properties row, every game's for
/// the PC room's list, the actions, and its three settings. The page gets sizes and words, never a path.
/// </summary>
public sealed partial class WebHostBridge
{
    private int _compressWired;
    private readonly Dictionary<string, long> _compressSaid = new(StringComparer.OrdinalIgnoreCase);

    private void WireCompress()
    {
        if (Interlocked.Exchange(ref _compressWired, 1) == 1) return;
        _services.Compressor.Changed += OnCompressChanged;
    }

    private void UnwireCompress()
    {
        if (Interlocked.Exchange(ref _compressWired, 0) == 1) _services.Compressor.Changed -= OnCompressChanged;
    }

    /// <summary>compress.progress, at most twice a second a game (a job's end always goes).</summary>
    private void OnCompressChanged(string id)
    {
        var game = _services.Library.Find(id);
        if (game is null) return;
        var view = _services.Compressor.ViewOf(game);
        var now = Environment.TickCount64;
        lock (_compressSaid)
        {
            var working = view.State is "compressing" or "uncompressing";
            if (working && _compressSaid.TryGetValue(id, out var last) && now - last < 500) return;
            _compressSaid[id] = now;
        }
        PostEvent("compress.progress", new { id, compress = CompressDto(view) });
    }

    private static object CompressDto(GameCompressor.View v) => new
    {
        state = v.State,
        percent = v.Percent,
        saved = v.Saved,
        size = v.Before > 0 ? v.Before : v.EstimateBefore,
        estimate = v.EstimateBefore is { } before && v.EstimateAfter is { } after ? Math.Max(0, before - after) : (long?)null,
        sampled = v.Sampled,
        changed = v.Changed,
        algorithm = v.Algorithm,
        message = v.Message,
        antiCheat = v.AntiCheat,
        held = v.Held,
    };

    /// <summary>compress.state: one game's; <c>estimate</c> reads its files for the real estimate (a few seconds).</summary>
    private async Task<object> CompressStateAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        WireCompress();
        if (await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false) is not { } game) return new { ok = false, message = "Game not found." };
        var view = ReadBool(p, hasParams, "estimate") == true
            ? await Task.Run(() => _services.Compressor.Estimate(game, ct), ct).ConfigureAwait(true)
            : await Task.Run(() => _services.Compressor.ViewOf(game), ct).ConfigureAwait(true);
        return new { ok = true, compress = CompressDto(view) };
    }

    /// <summary>compress.list: every installed game's state, the total saved and still to save, and the settings.</summary>
    private async Task<object> CompressListAsync(CancellationToken ct)
    {
        WireCompress();
        var rows = await Task.Run(() => _services.Compressor.List(), ct).ConfigureAwait(true);
        var s = _services.Settings.Current;
        return new
        {
            ok = true,
            // The most to gain first, then the most saved.
            games = rows.OrderByDescending(row => GameCompressor.Gain(row.View)).ThenByDescending(row => row.View.Saved)
                .Select(row => new { id = row.Game.Id, title = row.Game.Title, compress = CompressDto(row.View), eligible = GameCompressor.Eligible(row.View) }).ToArray(),
            saved = rows.Sum(row => row.View.Saved),
            potential = rows.Where(row => GameCompressor.Eligible(row.View)).Sum(row => GameCompressor.Gain(row.View)),
            settings = new { whileIdle = s.CompressWhileIdle, afterUpdates = s.RecompressAfterUpdates, speed = s.CompressSpeed },
        };
    }

    /// <summary>compress.act: compress, uncompress, pause, resume, cancel, allow (the anti-cheat set shown) or deny.</summary>
    private async Task<object> CompressActAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        WireCompress();
        if (await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false) is not { } game) return new { ok = false, message = "Game not found." };
        var compressor = _services.Compressor;
        var action = ReadString(p, hasParams, "action");
        var refused = await Task.Run(() => action switch
        {
            "compress" => compressor.Begin(game, compress: true),
            "uncompress" => compressor.Begin(game, compress: false),
            "pause" => compressor.Pause(game.Id, true) ? null : "Nothing is compressing.",
            "resume" => compressor.Pause(game.Id, false) ? null : compressor.Begin(game, compress: true),
            "cancel" => Cancel(compressor, game),
            "allow" => compressor.Consent(game, true, ReadString(p, hasParams, "antiCheat")),
            "deny" => compressor.Consent(game, false, null),
            _ => "Exo doesn't know that action.",
        }, ct).ConfigureAwait(true);
        return new { ok = refused is null, message = refused, compress = CompressDto(compressor.ViewOf(game)) };

        static string? Cancel(GameCompressor compressor, GameEntry game) { compressor.Cancel(game.Id); return null; }
    }

    /// <summary>compress.all: every game worth it queued, one at a time.</summary>
    private async Task<object> CompressAllAsync(CancellationToken ct)
    {
        WireCompress();
        var queued = await Task.Run(() => _services.Compressor.CompressAll(), ct).ConfigureAwait(true);
        return new { ok = true, queued, message = queued == 0 ? "Nothing more is worth compressing." : null };
    }

    /// <summary>compress.settings: whileIdle, afterUpdates, speed (gentle, balanced, fast); each on its own.</summary>
    private object CompressSettings(JsonElement p, bool hasParams)
    {
        _services.Settings.ApplyCompressPatch(ReadBool(p, hasParams, "whileIdle"), ReadBool(p, hasParams, "afterUpdates"), ReadString(p, hasParams, "speed"));
        var s = _services.Settings.Current;
        return new { ok = true, settings = new { whileIdle = s.CompressWhileIdle, afterUpdates = s.RecompressAfterUpdates, speed = s.CompressSpeed } };
    }
}
