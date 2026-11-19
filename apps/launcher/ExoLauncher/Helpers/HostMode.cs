namespace ExoLauncher.Helpers;

/// <summary>
/// A disposable host: one started with <see cref="PathHelper.DataDirOverrideVariable"/> set, as
/// tools/test-instance.ps1 starts one beside an installed Exo. It keeps its state in that folder and writes nothing
/// outside it: no Steam or Epic settings, overlay switches, controller settings or sound, startup entries, display or HDR
/// settings, update installs, shader builds, per-game profiles, Steam beta joins or Steam restarts. Background writers check
/// <see cref="IsDisposable"/> before they start; reading state for the page is fine.
/// </summary>
public static class HostMode
{
    /// <summary>What a refused write answers the page with.</summary>
    public const string Refusal = "A test instance changes nothing on this PC.";

    private static readonly AsyncLocal<bool?> s_forced = new();

    /// <summary>
    /// Set by the unit-test run (TestDataDirectory), which points the data folder at a temp folder for every test. A test run
    /// is not a disposable host: its writers must behave as they do for a person. Tests that check the guards use
    /// <see cref="ForceForTest"/> instead. Never set in the app.
    /// </summary>
    internal static bool TestRun { get; set; }

    public static bool IsDisposable =>
        s_forced.Value ?? (!TestRun && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable)));

    /// <summary>Tests only: makes IsDisposable answer <paramref name="value"/> in this test's async flow until disposed.</summary>
    internal static IDisposable ForceForTest(bool value)
    {
        var previous = s_forced.Value;
        s_forced.Value = value;
        return new Restore(previous);
    }

    private sealed class Restore(bool? previous) : IDisposable
    {
        public void Dispose() => s_forced.Value = previous;
    }
}
