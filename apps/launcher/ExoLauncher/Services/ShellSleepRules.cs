namespace ExoLauncher.Services;

/// <summary>What the shell's sleep decision reads: the window, and the things a suspended page could disturb.</summary>
internal readonly record struct ShellSleepInputs(
    bool HiddenOrMinimized, bool Closing, bool VoiceConnected, bool MusicPlaying, bool CaptureRecording, bool DownloadRunning);

/// <summary>
/// When the shell's page may be suspended: Exo is hidden or minimised to the tray, and none of voice, music, a capture
/// or recording, or a download or install needs the page to run. Background work for those lives in the host.
/// </summary>
internal static class ShellSleepRules
{
    /// <summary>Why the shell stays awake, or null when it may sleep.</summary>
    internal static string? Blocker(ShellSleepInputs inputs) =>
        !inputs.HiddenOrMinimized ? "shown"
        : inputs.Closing ? "closing"
        : inputs.VoiceConnected ? "voice call"
        : inputs.MusicPlaying ? "music"
        : inputs.CaptureRecording ? "capture"
        : inputs.DownloadRunning ? "download"
        : null;

    internal static bool MaySleep(ShellSleepInputs inputs) => Blocker(inputs) is null;

    /// <summary>What the host does with the shell page next.</summary>
    internal enum Step { Stay, StartTimer, Wake }

    /// <summary>
    /// The whole table: sleeping (or about to) and a blocker appears: wake; awake and nothing blocks: start the
    /// 30 s timer; every other row: stay as it is. Run again whenever a blocker can have changed, not only when the
    /// window does.
    /// </summary>
    internal static Step Next(ShellSleepInputs inputs, bool asleepOrPending) =>
        (MaySleep(inputs), asleepOrPending) switch
        {
            (true, false) => Step.StartTimer,
            (false, true) => Step.Wake,
            _ => Step.Stay,
        };
}