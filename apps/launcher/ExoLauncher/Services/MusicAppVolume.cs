using System.Globalization;
using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// The music app's own volume: the value its own slider shows. The mini
/// player reads and writes it inside the provider's page, so both sliders are
/// one control, and a watch loop reports changes made inside the app.
/// The page is Exo's own WebView2, reached through its DevTools protocol
/// (never a host object or web message).
/// </summary>
internal sealed class MusicAppVolume : IDisposable
{
    /// Runtime.evaluate (awaitPromise, returnByValue) in the provider page;
    /// null when the page did not answer.
    internal delegate Task<JsonElement?> Evaluate(string expression, TimeSpan timeout, CancellationToken cancellation);

    /// A slider waits on this: a page that doesn't answer in a second gets its level later, behind the slider.
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WaitWindow = TimeSpan.FromSeconds(20);

    private readonly Evaluate _evaluate;
    private readonly string _page;
    private readonly Func<bool> _paused;
    private readonly CancellationTokenSource _life = new();
    private bool _watching;

    internal string Service { get; }
    /// The last level the app reported (0–1).
    internal double? Last { get; private set; }
    /// The Windows session volume has been folded into the app's own level.
    internal bool SessionFolded { get; set; }
    /// Raised off the UI thread when the app's own volume changes.
    internal event Action<double>? Changed;

    private MusicAppVolume(string service, Evaluate evaluate, string adapter, Func<bool> paused)
    {
        Service = service;
        _evaluate = evaluate;
        _page = Wrapper.Replace("__ADAPTER__", adapter, StringComparison.Ordinal);
        _paused = paused;
    }

    internal static MusicAppVolume ForWebPage(string service, Evaluate evaluate, Func<bool> paused) =>
        new(service, evaluate, service switch { "apple" => AppleMusicAdapter, "spotify" => SpotifyAdapter, _ => YouTubeMusicAdapter }, paused);

    /// Apple Music: the page's own MusicKit volume (0–1), the one its own
    /// volume slider sets, and its playbackVolumeDidChange event. MusicKit's volume is the audio's own (linear: 5%
    /// is only 26 dB down, still loud), so Exo's slider, a loudness scale on every service as Spotify's is, sets it
    /// as the cube of its position and reads it back as the cube root.
    private const string AppleMusicAdapter = """
        {
          alive: function () { return true; },
          find: function () {
            try { var m = window.MusicKit && MusicKit.getInstance(); return m && 'volume' in m ? m : null; } catch (_) { return null; }
          },
          read: function (m) { return Math.cbrt(+m.volume); },
          write: function (m, v) { m.volume = v * v * v; },
          listen: function (m, wake) { m.addEventListener('playbackVolumeDidChange', wake); }
        }
        """;

    internal Task<double?> ReadAsync(CancellationToken cancellation = default) =>
        CallAsync("read()", CallTimeout, cancellation);

    internal Task<double?> WriteAsync(double level, CancellationToken cancellation = default) =>
        CallAsync("write(" + Math.Clamp(level, 0, 1).ToString("0.####", CultureInfo.InvariantCulture) + ")", CallTimeout, cancellation);

    private async Task<double?> CallAsync(string call, TimeSpan timeout, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _life.Token);
        JsonElement? value;
        try { value = await _evaluate("(" + _page + ")." + call, timeout, linked.Token); }
        catch { return null; } // timed out, cancelled, or the page went away
        if (value is not { ValueKind: JsonValueKind.Number } number) return null;
        var level = Math.Clamp(number.GetDouble(), 0, 1);
        var changed = Last is not { } previous || Math.Abs(previous - level) > 0.004;
        Last = level;
        if (changed) Changed?.Invoke(level);
        return level;
    }

    /// Long-polls the page for changes made inside the app (its own slider,
    /// keyboard, a Connect device). Runs until disposed.
    internal void Watch()
    {
        if (_watching) return;
        _watching = true;
        _ = WatchAsync();
    }

    private async Task WatchAsync()
    {
        var token = _life.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_paused()) { await Task.Delay(5000, token); continue; }
                var last = Last is { } known ? known.ToString("0.####", CultureInfo.InvariantCulture) : "null";
                var level = await CallAsync("wait(" + last + "," + (int)WaitWindow.TotalMilliseconds + ")",
                    WaitWindow + CallTimeout, token);
                // A page that did not answer (not loaded, restarting, suspended while
                // idle) is retried every second so a wake is picked up quickly.
                if (level is null) await Task.Delay(1000, token);
            }
            catch (OperationCanceledException) { return; }
            catch { try { await Task.Delay(3000, token); } catch (OperationCanceledException) { return; } }
        }
    }

    public void Dispose()
    {
        _life.Cancel();
        Changed = null;
    }

    /// Installed once per document and reused: read, write, and a wait that
    /// resolves when the level moves away from the caller's last value (or
    /// after the window), woken by the app's own volume event, with a one-second poll behind it.
    /// A write answers with the level asked for and never waits on the app: Spotify's setVolume settles only once its
    /// player has, which it may not while nothing plays, and the wait reports what the level really became.
    internal const string Wrapper = """
        (function (adapter) {
          var w = window;
          if (w.__exoVolume && w.__exoVolume.v === 4) return w.__exoVolume;
          var handle = null, heard = null, waiters = [];
          function wake() { var list = waiters; waiters = []; list.forEach(function (f) { f(); }); }
          function locate() {
            try { if (!handle || !adapter.alive(handle)) handle = adapter.find(); } catch (_) { handle = null; }
            if (handle && heard !== handle) { heard = handle; try { adapter.listen(handle, wake); } catch (_) {} }
            return handle;
          }
          function clamp(v) { return typeof v === 'number' && isFinite(v) ? Math.max(0, Math.min(1, v)) : null; }
          async function read() { var h = locate(); if (!h) return null; try { return clamp(await adapter.read(h)); } catch (_) { return null; } }
          async function write(v) { var h = locate(), c = clamp(v); if (!h || c === null) return null; try { Promise.resolve(adapter.write(h, c)).catch(function () {}); } catch (_) { return null; } return c; }
          async function wait(last, ms) {
            var end = Date.now() + ms;
            for (;;) {
              var v = await read();
              if (v !== null && (last === null || Math.abs(v - last) > 0.004)) return v;
              var left = end - Date.now();
              if (left <= 0) return v;
              // A wait that times out takes its waiter back, so the list never grows between events.
              await new Promise(function (resolve) {
                var woke = function () { clearTimeout(t); resolve(); };
                var t = setTimeout(function () { var i = waiters.indexOf(woke); if (i >= 0) waiters.splice(i, 1); resolve(); }, Math.min(left, 1000));
                waiters.push(woke);
              });
            }
          }
          return (w.__exoVolume = { v: 4, read: read, write: write, wait: wait });
        })(__ADAPTER__)
        """;

    /// Spotify keeps no global handle; its PlaybackAPI (getVolume, setVolume,
    /// getEvents — the API its own volume bar uses) is found through the
    /// React tree: first on the hooks of the volume bar's ancestors, then, if
    /// the bar is not mounted, in a bounded walk of the whole tree that also
    /// requires raiseVolume/lowerVolume so a preview player is never taken.
    private const string SpotifyAdapter = """
        {
          alive: function () { return true; },
          find: function () {
            function api(v, strict) {
              try {
                return !!v && typeof v === 'object' && typeof v.getVolume === 'function' && typeof v.setVolume === 'function' &&
                  typeof v.getEvents === 'function' && (!strict || (typeof v.raiseVolume === 'function' && typeof v.lowerVolume === 'function'));
              } catch (_) { return false; }
            }
            function inspect(f, strict) {
              var props = f.memoizedProps;
              if (props && api(props.value, strict)) return props.value;
              var h = f.memoizedState;
              for (var i = 0; h && typeof h === 'object' && i < 64; i++, h = h.next) {
                var s = h.memoizedState;
                var deps = Array.isArray(s) && Array.isArray(s[1]) ? s[1] : (s && typeof s === 'object' && Array.isArray(s.deps) ? s.deps : null);
                if (deps) for (var j = 0; j < deps.length; j++) if (api(deps[j], strict)) return deps[j];
                if (!('next' in h)) break;
              }
              return null;
            }
            function key(node, prefix) { for (var k in node) if (k.lastIndexOf(prefix, 0) === 0) return node[k]; return null; }
            var bar = document.querySelector('[data-testid="volume-bar"]');
            for (var f = bar && key(bar, '__reactFiber$'), d = 0; f && d < 80; f = f.return, d++) {
              var near = inspect(f, false);
              if (near) return near;
            }
            var roots = [document.getElementById('main'), document.getElementById('root')].concat([].slice.call(document.body.children));
            for (var r = 0; r < roots.length; r++) {
              var container = roots[r] && key(roots[r], '__reactContainer$');
              if (!container) continue;
              var top = container.stateNode && container.stateNode.current || container;
              var stack = [top], seen = 0;
              while (stack.length && seen++ < 60000) {
                var n = stack.pop(), hit = inspect(n, true);
                if (hit) return hit;
                if (n.sibling) stack.push(n.sibling);
                if (n.child) stack.push(n.child);
              }
            }
            return null;
          },
          read: function (api) { return api.getVolume(); },
          write: function (api, v) { return api.setVolume(v); },
          listen: function (api, wake) { api.getEvents().addListener('volume', wake); }
        }
        """;

    /// YouTube Music's player element (the YouTube player API, 0–100, linear like Apple's) and the player bar's own
    /// slider, which is moved to match. Exo's loudness slider sets it as the cube of its position (at least 1 while
    /// not 0, so the bottom of the slider never mutes) and reads it as the cube root.
    private const string YouTubeMusicAdapter = """
        {
          alive: function (p) { return p.isConnected; },
          find: function () {
            var p = document.getElementById('movie_player');
            return p && typeof p.getVolume === 'function' && typeof p.setVolume === 'function' ? p : null;
          },
          read: function (p) { return p.isMuted && p.isMuted() ? 0 : Math.cbrt(p.getVolume() / 100); },
          write: function (p, v) {
            var n = v > 0 ? Math.max(1, Math.round(v * v * v * 100)) : 0;
            p.setVolume(n);
            if (n > 0 && p.isMuted && p.isMuted()) p.unMute();
            var slider = document.querySelector('ytmusic-player-bar #volume-slider');
            if (slider && 'value' in slider && slider.value !== n) slider.value = n;
          },
          listen: function (p, wake) { p.addEventListener('onVolumeChange', wake); }
        }
        """;
}
