using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Helpers;

/// <summary>
/// AMD's unreleased Fluid Motion Frames 3, an opt-in on a Radeon that may have it. A Reddit post (r/radeon, relayed by VideoCardz and
/// Wccftech, November 2026) found that one per-user value, HKCU\SOFTWARE\AMD\DVR afmf3dev = 1, makes AMD Software 26.10.2 show its settings on an RX 7000 or 9000 once AMD Software
/// restarts. AMD has said nothing, nobody independent has measured it, and all of what the value changes isn't known: VideoCardz advises against it on a PC you use. So
/// Exo never sets it by itself or with Best settings: one switch on a card and driver the report covers, labelled AMD's unreleased preview, off in one press. Exo saves what was
/// there and puts exactly that back. Exo never closes AMD Software; it takes hold when AMD Software next starts. It doesn't touch Exo's own per-game choice of Fluid Motion Frames (<see cref="FluidMotionFrames"/>).
/// When AMD says it has released it, on its Fluid Motion Frames page or in the installed driver's release notes (<see cref="Released"/>), the switch goes
/// and Exo takes off a value it set: from then on it's AMD's own setting. Tested only against a fake registry: no Radeon has run this.
/// </summary>
internal sealed partial class Afmf3Preview(Afmf3Preview.IStore store, string path, Func<bool> playing)
{
    /// <summary>The value: <c>afmf3dev</c> under <c>HKCU\SOFTWARE\AMD\DVR</c>.</summary>
    internal interface IStore
    {
        /// <summary>AMD Software has made its key, so it's there to read the value.</summary>
        bool Installed();
        int? Read();
        /// <summary>Writes it, or deletes it for null.</summary>
        void Write(int? value);
    }

    /// <param name="Offer">Show the switch.</param>
    /// <param name="On">The value is set.</param>
    /// <param name="Note">One line once: AMD released it, and what Exo did about it.</param>
    internal sealed record State(bool Offer, bool On, string? Note);

    /// <summary>What Exo keeps: whether it set the value and what was there before, and that AMD's release was seen, for good.</summary>
    internal sealed record Saved(bool Set = false, int? Was = null, bool Released = false);

    /// <summary>The first driver the report covers.</summary>
    private static readonly Version First = new(26, 10, 2);

    private readonly Lock _gate = new();

    /// <summary>The RX 7000 and 9000 the report covers, laptops included; not their PRO parts, an RX 6000 or an integrated Radeon, which it doesn't.</summary>
    internal static bool Covers(string? gpu, string? driver)
    {
        if (gpu is null || driver is null || !Version.TryParse(driver, out var version) || version < First) return false;
        var name = gpu.ToUpperInvariant();
        return !name.Contains("PRO", StringComparison.Ordinal) && Card().IsMatch(name);
    }

    /// <summary>A page of AMD's that says it is out: its name with a 3 after it, and not something else's 3 (FSR 3, AFMF 2.1).</summary>
    internal static bool Released(string page) => Named().IsMatch(page);

    /// <summary>Where the switch stands. A release Exo sees for the first time takes off a value of Exo's and says so, once.</summary>
    internal State Read(string? gpu, string? driver, bool released)
    {
        using var scope = _gate.EnterScope();
        var saved = Load();
        if (released || saved.Released)
        {
            if (saved.Released) return new State(false, false, null);
            var note = saved.Set ? "AMD has released Fluid Motion Frames 3, so Exo took its preview off. Set it in AMD Software." : null;
            if (saved.Set && !Restore(saved)) return new State(true, store.Read() == 1, "AMD has released Fluid Motion Frames 3, but Exo couldn't take its preview off. Turn it off here.");
            Save(new Saved(Released: true));
            return new State(false, false, note);
        }
        var on = store.Read() == 1;
        // Once on it can always go off here, whatever changed about the card or driver.
        return new State(on && saved.Set || store.Installed() && Covers(gpu, driver), on, null);
    }

    /// <summary>One press, on or off; null when done, else the line to show. Never during a game.</summary>
    internal string? Set(bool on, string? gpu, string? driver, bool released)
    {
        using var scope = _gate.EnterScope();
        if (playing()) return "Close your game first.";
        var saved = Load();
        try
        {
            if (on)
            {
                if (released || saved.Released) return "AMD has released it. Set it in AMD Software.";
                if (!store.Installed() || !Covers(gpu, driver)) return "This card or driver isn't one it was reported on.";
                var was = saved.Set ? saved.Was : store.Read();
                if (!Save(new Saved(true, was))) return "Exo couldn't save what was there, so nothing changed.";
                store.Write(1);
                if (store.Read() == 1)
                {
                    AppLog.Info("Fluid Motion Frames 3: AMD's preview turned on at the user's press.");
                    return null;
                }
                Save(saved);
                return "Windows didn't take the change.";
            }
            // A value someone set by hand has nothing saved: off is off.
            if (!(saved.Set ? Restore(saved) : Remove())) return "Windows didn't take the change.";
            Save(new Saved());
            AppLog.Info("Fluid Motion Frames 3: AMD's preview turned off.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn("Fluid Motion Frames 3: the value couldn't be changed: " + ex.Message);
            return "Windows didn't take the change.";
        }
    }

    private bool Remove()
    {
        store.Write(null);
        return store.Read() != 1;
    }

    private bool Restore(Saved saved)
    {
        store.Write(saved.Was);
        return store.Read() == saved.Was;
    }

    private Saved Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) ?? new Saved() : new Saved(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new Saved(); }
    }

    private bool Save(Saved saved)
    {
        try
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(saved));
            File.Move(path + ".tmp", path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // "RX 7800 XT", "RX 9070 XT", "RX 7600M".
    [GeneratedRegex(@"\bRX\s*-?\s*[79]\d{3}")]
    private static partial Regex Card();

    // "AFMF 3", "AMD Fluid Motion Frames 3", with AMD's trademark in any of the ways a page writes it: not 2.1, 30 or the afmf3dev value's name.
    [GeneratedRegex(@"\b(?:AFMF|Fluid\s+Motion\s+Frames)(?:\s|&nbsp;|™|&trade;|\(TM\)|</?sup>|TM)*3(?!\w)", RegexOptions.IgnoreCase)]
    private static partial Regex Named();

    /// <summary>This PC's value.</summary>
    internal sealed class Registered : IStore
    {
        private const string Key = @"SOFTWARE\AMD\DVR", Name = "afmf3dev";

        public bool Installed()
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key is not null;
        }

        public int? Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Name) is int value ? value : null;
        }

        public void Write(int? value)
        {
            if (HostMode.IsDisposable) return;
            using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (key is null) return;
            if (value is { } set) key.SetValue(Name, set, RegistryValueKind.DWord);
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }

    internal static Afmf3Preview ThisPc { get; } = new(new Registered(), Path.Combine(PathHelper.AppDataDir, "afmf3-preview.json"),
        () => App.Services.Launcher.AnyGameRunning || GameRunningSignal.Running);

    private static bool _officialSeen;
    private static DateTimeOffset _officialAt;
    private static string? _officialFor;

    /// <summary>
    /// Whether AMD says it is out: AMD's Fluid Motion Frames page, and the release notes of the driver installed. At most once a day for a driver, and only for a card the switch is
    /// for. A page that doesn't answer says nothing: the switch stays a preview until a page says otherwise.
    /// </summary>
    internal static async Task<bool> OfficialAsync(string? gpu, string? driver, CancellationToken ct)
    {
        if (!Covers(gpu, driver)) return false;
        if (_officialSeen) return true;
        if (_officialFor == driver && DateTimeOffset.UtcNow - _officialAt < TimeSpan.FromDays(1)) return false;
        (_officialFor, _officialAt) = (driver, DateTimeOffset.UtcNow);
        string[] pages = ["https://www.amd.com/en/products/software/adrenalin/afmf.html", $"https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-{driver!.Replace('.', '-')}.html"];
        foreach (var page in pages)
        {
            try
            {
                if (Released(await GpuDriverUpdate.GetTextAsync("amd", page, ct).ConfigureAwait(false))) return _officialSeen = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                AppLog.Debug("Fluid Motion Frames 3: AMD's page didn't answer: " + ex.GetType().Name);
            }
        }
        return false;
    }
}
