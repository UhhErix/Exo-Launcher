using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal sealed record HdrCalibrationEntry(
    string DevicePath,
    string ScreenName,
    int EdidPeakNits,
    int MeasuredPeakNits,
    int GlobalBrightness,
    uint SdrWhiteRaw,
    double SdrWhiteNits,
    bool Enabled,
    string CalibratedAtUtc);

internal sealed record CalibrationProgress(
    string DevicePath,
    int Step,
    int Total,
    int Slider,
    int? MeasuredNits,
    int TargetNits);

internal sealed record CalibrationResult(
    bool Ok,
    string Status,
    string Message,
    HdrCalibrationEntry? Entry = null,
    string[]? ManualSteps = null);

internal interface ICalibrationBackend
{
    string DevicePath { get; }
    string ScreenName { get; }
    bool IsInternal { get; }
    int? EdidPeakNits { get; }
    int? ReadGlobalBrightness();
    bool WriteGlobalBrightness(int level);
    uint? ReadSdrWhiteRaw();
    HdrBrightness.WriteSdrResult WriteSdrWhiteRaw(uint rawLevel);
    float? ReadFreshDxgiMaxLuminance();
    void Settle(CancellationToken ct);
}

internal sealed class NativeCalibrationBackend(WindowsHdr.Screen screen) : ICalibrationBackend
{
    public string DevicePath => screen.DevicePath;
    public string ScreenName => screen.Name;
    public bool IsInternal => screen.IsInternal;
    public int? EdidPeakNits => WindowsHdr.PeakNits(screen);
    public int? ReadGlobalBrightness() => HdrBrightness.ReadPanelBrightness();
    public bool WriteGlobalBrightness(int level) => HdrBrightness.WritePanelBrightness(level);
    public uint? ReadSdrWhiteRaw() => HdrBrightness.ReadSdrWhiteRaw(screen);
    public HdrBrightness.WriteSdrResult WriteSdrWhiteRaw(uint rawLevel) => HdrBrightness.WriteSdrWhiteRaw(screen, rawLevel);
    public float? ReadFreshDxgiMaxLuminance() => GameHdr.DxgiPeakExact(screen.GdiName);

    public void Settle(CancellationToken ct)
    {
        if (ct.WaitHandle.WaitOne(120)) ct.ThrowIfCancellationRequested();
    }
}

internal sealed class HdrCalibrationStore(string path)
{
    internal const string FileName = "hdr-calibration.json";
    internal static string DefaultPath => Path.Combine(PathHelper.AppDataDir, FileName);
    internal static HdrCalibrationStore Shared { get; } = new(DefaultPath);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private Dictionary<string, HdrCalibrationEntry>? _items;

    private Dictionary<string, HdrCalibrationEntry> Loaded()
    {
        if (_items is not null) return _items;
        _items = new Dictionary<string, HdrCalibrationEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 256 * 1024 &&
                JsonSerializer.Deserialize<Dictionary<string, HdrCalibrationEntry>>(File.ReadAllText(path), Json) is { } read)
                foreach (var (k, v) in read) _items[k] = v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Debug("HDR calibration store unreadable: " + ex.GetType().Name);
        }
        return _items;
    }

    internal HdrCalibrationEntry? Get(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;
        lock (_gate) return Loaded().GetValueOrDefault(devicePath);
    }

    internal int? ValidPeakFor(string devicePath)
    {
        var entry = Get(devicePath);
        return entry is { Enabled: true, EdidPeakNits: > 0 } ? entry.EdidPeakNits : null;
    }

    internal bool Save(HdrCalibrationEntry entry)
    {
        lock (_gate)
        {
            var map = Loaded();
            map[entry.DevicePath] = entry;
            return Write(map);
        }
    }

    internal bool SetEnabled(string devicePath, bool enabled)
    {
        lock (_gate)
        {
            var map = Loaded();
            if (!map.TryGetValue(devicePath, out var existing)) return false;
            map[devicePath] = existing with { Enabled = enabled };
            return Write(map);
        }
    }

    internal bool Remove(string devicePath)
    {
        lock (_gate)
        {
            var map = Loaded();
            if (!map.Remove(devicePath)) return true;
            return Write(map);
        }
    }

    private bool Write(Dictionary<string, HdrCalibrationEntry> map)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, map, Json);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("HDR calibration store write failed: " + ex.GetType().Name);
            return false;
        }
    }
}

internal static class HdrCalibrator
{
    private const int EstimatedSteps = 12;

    private static readonly string[] GuidedManualSteps =
    [
        "Open Windows Settings > System > Display > HDR.",
        "Set your laptop's screen brightness to the level you want to play at.",
        "Adjust the HDR content brightness slider until games stop clipping highlights or looking dim.",
        "Avoid changing brightness with the Fn keys while an HDR game is already running.",
    ];

    internal static CalibrationResult Run(
        ICalibrationBackend backend,
        HdrCalibrationStore? store = null,
        Action<CalibrationProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!backend.IsInternal)
            return new CalibrationResult(false, "not_internal", "Neutral-pair calibration applies only to internal laptop HDR panels.");

        if (backend.EdidPeakNits is not { } target || target <= 0)
            return new CalibrationResult(false, "unknown_edid", "Panel EDID peak luminance is unknown — calibration skipped.");

        var origBright = backend.ReadGlobalBrightness();
        var origSdrRaw = backend.ReadSdrWhiteRaw();
        if (origBright is null || origSdrRaw is null)
            return new CalibrationResult(false, "unreadable", "Could not read current panel brightness or SDR content brightness.");

        var samples = new Dictionary<int, float>();
        var step = 0;
        int? lastSlider = null;
        float? lastReading = null;
        var touchedSdr = false;

        try
        {
            var probeWrite = backend.WriteSdrWhiteRaw(origSdrRaw.Value);
            if (!probeWrite.Ok)
            {
                if (probeWrite.Unsupported)
                    return new CalibrationResult(
                        false,
                        "unsupported_api",
                        "This Windows build does not accept programmatic HDR content brightness changes. Follow the manual steps instead.",
                        ManualSteps: GuidedManualSteps);
                return new CalibrationResult(false, "write_failed", $"Windows rejected the HDR content brightness change (error {probeWrite.Status}).");
            }
            touchedSdr = true;

            float? Measure(int slider)
            {
                ct.ThrowIfCancellationRequested();
                slider = Math.Clamp(slider, 0, 100);
                if (samples.TryGetValue(slider, out var cached)) return cached;

                var raw = HdrBrightness.SliderToRaw(slider);
                var write = backend.WriteSdrWhiteRaw(raw);
                if (!write.Ok) throw new UnsupportedWriteException(write);
                backend.Settle(ct);
                var reading = backend.ReadFreshDxgiMaxLuminance();

                if (reading is { } r && lastSlider is { } prevS && lastReading is { } prevR &&
                    Math.Abs(slider - prevS) >= 2 && Math.Abs(r - prevR) < 0.5f)
                {
                    var nudge = slider == 100 ? 99 : slider + 1;
                    var nudgeRes = backend.WriteSdrWhiteRaw(HdrBrightness.SliderToRaw(nudge));
                    if (!nudgeRes.Ok) throw new UnsupportedWriteException(nudgeRes);
                    backend.Settle(ct);
                    var retryRes = backend.WriteSdrWhiteRaw(raw);
                    if (!retryRes.Ok) throw new UnsupportedWriteException(retryRes);
                    backend.Settle(ct);
                    reading = backend.ReadFreshDxgiMaxLuminance();
                }

                step++;
                onProgress?.Invoke(new CalibrationProgress(
                    backend.DevicePath,
                    step,
                    Math.Max(EstimatedSteps, step),
                    slider,
                    reading is { } n ? (int)Math.Round(n) : null,
                    target));

                if (reading is { } measured && measured > 0)
                {
                    samples[slider] = measured;
                    lastSlider = slider;
                    lastReading = measured;
                    return measured;
                }
                return null;
            }

            var lowVal = Measure(0);
            var highVal = Measure(100);
            if (lowVal is null || highVal is null)
                return new CalibrationResult(false, "dxgi_unavailable", "DXGI did not report display MaxLuminance during calibration.");

            var increasing = highVal.Value >= lowVal.Value;
            var lo = 0;
            var hi = 100;
            var sawNonMonotonic = false;
            var loVal = lowVal.Value;
            var hiVal = highVal.Value;

            for (var iter = 0; iter < 9 && hi - lo > 1; iter++)
            {
                var mid = (lo + hi) / 2;
                var val = Measure(mid);
                if (val is null) break;

                if (increasing)
                {
                    if (val.Value < Math.Min(loVal, hiVal) - 1f || val.Value > Math.Max(loVal, hiVal) + 1f)
                        sawNonMonotonic = true;
                    if (val.Value < target)
                    {
                        lo = mid;
                        loVal = val.Value;
                    }
                    else
                    {
                        hi = mid;
                        hiVal = val.Value;
                    }
                }
                else
                {
                    if (val.Value < Math.Min(loVal, hiVal) - 1f || val.Value > Math.Max(loVal, hiVal) + 1f)
                        sawNonMonotonic = true;
                    if (val.Value > target)
                    {
                        lo = mid;
                        loVal = val.Value;
                    }
                    else
                    {
                        hi = mid;
                        hiVal = val.Value;
                    }
                }
            }

            var best = BestSample(samples, target);
            var bestRelErr = Math.Abs(best.Nits - target) / target;
            if (sawNonMonotonic || bestRelErr > 0.05)
            {
                int[] coarse = [12, 25, 37, 50, 62, 75, 87];
                foreach (var p in coarse) Measure(p);
                var bracket = BestBracket(samples, target);
                if (bracket is { } b)
                {
                    var bLo = b.Lo;
                    var bHi = b.Hi;
                    for (var iter = 0; iter < 6 && bHi - bLo > 1; iter++)
                    {
                        var mid = (bLo + bHi) / 2;
                        var val = Measure(mid);
                        if (val is null) break;
                        var goRight = increasing ? val.Value < target : val.Value > target;
                        if (goRight) bLo = mid;
                        else bHi = mid;
                    }
                }
                best = BestSample(samples, target);
            }

            for (var s = Math.Max(0, best.Slider - 2); s <= Math.Min(100, best.Slider + 2); s++)
                Measure(s);

            best = BestSample(samples, target);
            var bestRaw = HdrBrightness.SliderToRaw(best.Slider);
            var bestSdrNits = HdrBrightness.RawToNits(bestRaw);
            var measuredInt = (int)Math.Round(best.Nits);
            var entry = new HdrCalibrationEntry(
                backend.DevicePath,
                backend.ScreenName,
                target,
                measuredInt,
                origBright.Value,
                bestRaw,
                bestSdrNits,
                Enabled: true,
                DateTime.UtcNow.ToString("O"));

            store?.Save(entry);
            return new CalibrationResult(
                true,
                "ok",
                $"Neutral pair found: brightness {origBright.Value}%, HDR content brightness {bestSdrNits:0} nits (reports {measuredInt} nits vs {target} nits EDID).",
                entry);
        }
        catch (UnsupportedWriteException ex)
        {
            if (ex.Result.Unsupported)
                return new CalibrationResult(
                    false,
                    "unsupported_api",
                    "This Windows build does not accept programmatic HDR content brightness changes. Follow the manual steps instead.",
                    ManualSteps: GuidedManualSteps);
            return new CalibrationResult(false, "write_failed", $"Windows rejected the HDR content brightness change (error {ex.Result.Status}).");
        }
        catch (OperationCanceledException)
        {
            return new CalibrationResult(false, "cancelled", "Calibration cancelled.");
        }
        finally
        {
            if (touchedSdr)
            {
                backend.WriteGlobalBrightness(origBright.Value);
                backend.WriteSdrWhiteRaw(origSdrRaw.Value);
            }
        }
    }

    private static (int Slider, float Nits) BestSample(Dictionary<int, float> samples, int target)
    {
        var bestSlider = 0;
        var bestNits = 0f;
        var bestErr = double.MaxValue;
        foreach (var (s, n) in samples)
        {
            var err = Math.Abs(n - target);
            if (err < bestErr)
            {
                bestErr = err;
                bestSlider = s;
                bestNits = n;
            }
        }
        return (bestSlider, bestNits);
    }

    private static (int Lo, int Hi)? BestBracket(Dictionary<int, float> samples, int target)
    {
        var ordered = samples.OrderBy(kv => kv.Key).ToList();
        (int Lo, int Hi)? best = null;
        var bestSpan = int.MaxValue;
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var a = ordered[i];
            var b = ordered[i + 1];
            var crosses = (a.Value <= target && b.Value >= target) || (a.Value >= target && b.Value <= target);
            if (!crosses) continue;
            var span = b.Key - a.Key;
            if (span < bestSpan)
            {
                bestSpan = span;
                best = (a.Key, b.Key);
            }
        }
        return best;
    }

    private sealed class UnsupportedWriteException(HdrBrightness.WriteSdrResult result) : Exception
    {
        internal HdrBrightness.WriteSdrResult Result { get; } = result;
    }
}
