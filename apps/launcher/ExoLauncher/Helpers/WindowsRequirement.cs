using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Exo Launcher runs on Windows 11 only (AGENTS.md). Exo Setup refuses the same builds before it installs anything
/// (setup/src/main.cpp); this is the safety net for a copy that got past it, such as one copied in by hand.
/// </summary>
internal static class WindowsRequirement
{
    /// <summary>Windows 11 21H2, the first build of Windows 11.</summary>
    internal const int MinimumBuild = 22000;

    internal static bool Supports(int build) => build >= MinimumBuild;

    /// <summary>Says so in a plain message box. The caller stops: nothing else runs on an older Windows.</summary>
    internal static void Refuse() => MessageBoxW(IntPtr.Zero, "Exo Launcher needs Windows 11.", "Exo Launcher", 0x10 /* MB_ICONSTOP */);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
