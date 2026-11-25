using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal sealed record HdrJournalEntry(
    string DevicePath,
    string GdiName,
    string Kind,
    uint BeforeSdrWhiteRaw,
    uint AppliedSdrWhiteRaw,
    int? BeforeGlobalBrightness = null,
    int? AppliedGlobalBrightness = null);

internal sealed class HdrJournal(string path)
{
    internal const string FileName = "hdr-journal.json";
    internal static string DefaultPath => Path.Combine(PathHelper.AppDataDir, FileName);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private List<HdrJournalEntry>? _entries;

    private sealed class Doc
    {
        public int Version { get; set; } = 1;
        public List<HdrJournalEntry> Entries { get; set; } = [];
    }

    internal IReadOnlyList<HdrJournalEntry> Entries
    {
        get { lock (_gate) return [.. Loaded()]; }
    }

    private List<HdrJournalEntry> Loaded()
    {
        if (_entries is not null) return _entries;
        try
        {
            if (!File.Exists(path)) return _entries = [];
            var text = File.ReadAllText(path);
            return _entries = text.Length > 0 && JsonSerializer.Deserialize<Doc>(text, Json) is { } doc ? doc.Entries ?? [] : [];
        }
        catch (JsonException)
        {
            AppLog.Warn("HDR: the restore journal is damaged, set aside as .bad.");
            try { File.Move(path, path + ".bad", overwrite: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return _entries = [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("HDR: the restore journal could not be read: " + ex.GetType().Name);
            return [];
        }
    }

    internal bool Add(HdrJournalEntry entry)
    {
        lock (_gate)
        {
            var entries = Loaded();
            var next = entries.Where(e => !SameTarget(e, entry)).Append(entry).ToList();
            if (!Write(next)) return false;
            _entries = next;
            return true;
        }
    }

    internal void Remove(string devicePath, string kind)
    {
        lock (_gate)
        {
            var entries = Loaded();
            var next = entries.Where(e => !(string.Equals(e.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase))).ToList();
            if (next.Count == entries.Count) return;
            if (Write(next)) _entries = next;
        }
    }

    private static bool SameTarget(HdrJournalEntry a, HdrJournalEntry b) =>
        string.Equals(a.DevicePath, b.DevicePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase);

    private bool Write(List<HdrJournalEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Doc { Entries = entries }, Json);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("HDR: the restore journal could not be written: " + ex.GetType().Name);
            return false;
        }
    }
}

internal interface IHdrDisplayControl
{
    WindowsHdr.Screen? FindScreen(string devicePath);
    uint? ReadSdrWhiteRaw(WindowsHdr.Screen screen);
    HdrBrightness.WriteSdrResult WriteSdrWhiteRaw(WindowsHdr.Screen screen, uint rawLevel);
    int? ReadPanelBrightness();
    bool WritePanelBrightness(int level);
}

internal sealed class NativeHdrDisplayControl : IHdrDisplayControl
{
    public WindowsHdr.Screen? FindScreen(string devicePath) =>
        WindowsHdr.All().FirstOrDefault(s => string.Equals(s.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));

    public uint? ReadSdrWhiteRaw(WindowsHdr.Screen screen) => HdrBrightness.ReadSdrWhiteRaw(screen);

    public HdrBrightness.WriteSdrResult WriteSdrWhiteRaw(WindowsHdr.Screen screen, uint rawLevel) =>
        HdrBrightness.WriteSdrWhiteRaw(screen, rawLevel);

    public int? ReadPanelBrightness() => HdrBrightness.ReadPanelBrightness();

    public bool WritePanelBrightness(int level) => HdrBrightness.WritePanelBrightness(level);
}

internal sealed class HdrSessionController(IHdrDisplayControl control, HdrJournal journal, HdrCalibrationStore store)
{
    internal const string KindNeutralPair = "neutral_pair";
    internal const string KindCursorFix = "cursor_fix";

    private readonly Lock _gate = new();
    private HdrJournalEntry? _activeNeutral;
    private HdrJournalEntry? _activeCursor;
    private string? _brightnessWarning;
    private bool _warnedThisSession;

    internal string? BrightnessWarning
    {
        get { lock (_gate) return _brightnessWarning; }
    }

    internal event Action<string, string, string>? WarningRaised;

    internal int RecoverFromJournal()
    {
        var recovered = 0;
        foreach (var entry in journal.Entries)
        {
            if (RestoreEntry(entry)) recovered++;
            journal.Remove(entry.DevicePath, entry.Kind);
        }
        return recovered;
    }

    internal bool ApplyNeutralPairForGame(WindowsHdr.Screen screen)
    {
        if (!screen.IsInternal || !screen.Supported) return false;
        var cal = store.Get(screen.DevicePath);
        if (cal is null || !cal.Enabled || cal.EdidPeakNits <= 0) return false;

        lock (_gate)
        {
            if (_activeNeutral is not null &&
                string.Equals(_activeNeutral.DevicePath, screen.DevicePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var beforeSdr = control.ReadSdrWhiteRaw(screen);
        var beforeBright = control.ReadPanelBrightness();
        if (beforeSdr is null || beforeBright is null) return false;

        var entry = new HdrJournalEntry(
            screen.DevicePath,
            screen.GdiName,
            KindNeutralPair,
            beforeSdr.Value,
            cal.SdrWhiteRaw,
            beforeBright.Value,
            cal.GlobalBrightness);

        if (!journal.Add(entry)) return false;

        var brightOk = control.WritePanelBrightness(cal.GlobalBrightness);
        var sdrResult = control.WriteSdrWhiteRaw(screen, cal.SdrWhiteRaw);
        if (!brightOk || !sdrResult.Ok)
        {
            if (brightOk) control.WritePanelBrightness(beforeBright.Value);
            if (sdrResult.Ok) control.WriteSdrWhiteRaw(screen, beforeSdr.Value);
            journal.Remove(entry.DevicePath, entry.Kind);
            AppLog.Warn($"HDR: neutral pair could not be applied on {screen.GdiName} (status {sdrResult.Status}).");
            return false;
        }

        lock (_gate)
        {
            _activeNeutral = entry;
            _brightnessWarning = null;
            _warnedThisSession = false;
        }
        AppLog.Info($"HDR: applied neutral pair on {screen.GdiName} (brightness {cal.GlobalBrightness}%, SDR white {cal.SdrWhiteNits:0} nits).");
        return true;
    }

    internal bool ApplyCursorFixForGame(WindowsHdr.Screen screen, bool optIn)
    {
        var beforeSdr = control.ReadSdrWhiteRaw(screen);
        if (beforeSdr is null) return false;
        var beforeNits = HdrBrightness.RawToNits(beforeSdr.Value);
        if (!HdrDisplayHealth.ShouldApplyCursorFix(screen.IsInternal, screen.Supported, optIn, beforeNits)) return false;

        lock (_gate)
        {
            if (_activeCursor is not null &&
                string.Equals(_activeCursor.DevicePath, screen.DevicePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var targetRaw = HdrBrightness.NitsToRaw(HdrBrightness.DefaultCursorFixNits);
        var entry = new HdrJournalEntry(
            screen.DevicePath,
            screen.GdiName,
            KindCursorFix,
            beforeSdr.Value,
            targetRaw);

        if (!journal.Add(entry)) return false;

        var sdrResult = control.WriteSdrWhiteRaw(screen, targetRaw);
        if (!sdrResult.Ok)
        {
            journal.Remove(entry.DevicePath, entry.Kind);
            AppLog.Warn($"HDR: cursor brightness fix could not be applied on {screen.GdiName} (status {sdrResult.Status}).");
            return false;
        }

        lock (_gate) _activeCursor = entry;
        AppLog.Info($"HDR: raised SDR white to {HdrBrightness.DefaultCursorFixNits} nits for HDR cursor on {screen.GdiName} (was {beforeNits:0} nits).");
        return true;
    }

    internal bool CheckMidGameBrightnessChange()
    {
        HdrJournalEntry? active;
        lock (_gate) active = _activeNeutral;
        if (active is null) return false;

        var screen = control.FindScreen(active.DevicePath);
        if (screen is null) return false;

        var curBright = control.ReadPanelBrightness();
        var curSdr = control.ReadSdrWhiteRaw(screen);
        var brightMoved = curBright is not null && active.AppliedGlobalBrightness is not null &&
            Math.Abs(curBright.Value - active.AppliedGlobalBrightness.Value) >= 2;
        var sdrMoved = curSdr is not null &&
            Math.Abs((int)curSdr.Value - (int)active.AppliedSdrWhiteRaw) >= (int)HdrBrightness.SdrWhiteStep;

        if (!brightMoved && !sdrMoved) return false;

        const string message = "Display brightness changed while playing — running games do not receive the new peak luminance until restarted.";
        var raise = false;
        lock (_gate)
        {
            _brightnessWarning = message;
            if (!_warnedThisSession)
            {
                _warnedThisSession = true;
                raise = true;
            }
        }
        if (raise)
        {
            AppLog.Warn($"HDR: {message}");
            WarningRaised?.Invoke(active.DevicePath, "brightness_changed", message);
        }
        return true;
    }

    internal void RestoreAll()
    {
        HdrJournalEntry? neutral;
        HdrJournalEntry? cursor;
        lock (_gate)
        {
            neutral = _activeNeutral;
            cursor = _activeCursor;
            _activeNeutral = null;
            _activeCursor = null;
            _brightnessWarning = null;
            _warnedThisSession = false;
        }
        if (neutral is not null)
        {
            RestoreEntry(neutral);
            journal.Remove(neutral.DevicePath, neutral.Kind);
        }
        if (cursor is not null)
        {
            RestoreEntry(cursor);
            journal.Remove(cursor.DevicePath, cursor.Kind);
        }
    }

    internal void RestoreNeutralOnly()
    {
        HdrJournalEntry? neutral;
        lock (_gate)
        {
            neutral = _activeNeutral;
            _activeNeutral = null;
            _brightnessWarning = null;
            _warnedThisSession = false;
        }
        if (neutral is not null)
        {
            RestoreEntry(neutral);
            journal.Remove(neutral.DevicePath, neutral.Kind);
        }
    }

    internal void RestoreCursorOnly()
    {
        HdrJournalEntry? cursor;
        lock (_gate)
        {
            cursor = _activeCursor;
            _activeCursor = null;
        }
        if (cursor is not null)
        {
            RestoreEntry(cursor);
            journal.Remove(cursor.DevicePath, cursor.Kind);
        }
    }

    private bool RestoreEntry(HdrJournalEntry entry)
    {
        var screen = control.FindScreen(entry.DevicePath);
        var restored = false;
        if (entry.BeforeGlobalBrightness is { } bright)
            restored |= control.WritePanelBrightness(bright);
        if (screen is not null)
            restored |= control.WriteSdrWhiteRaw(screen, entry.BeforeSdrWhiteRaw).Ok;
        if (restored)
            AppLog.Info($"HDR: restored {entry.Kind} on {entry.GdiName}.");
        return restored;
    }
}
