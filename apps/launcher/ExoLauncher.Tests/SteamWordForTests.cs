using System.Runtime.CompilerServices;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// No test reads this PC's Steam: by default Steam's app list is missing, so a Steam game reads as Steam can't say
/// (possibly Valve Anti-Cheat). A test that needs Steam to say something gives AntiCheatCheck its own word or file.
/// </summary>
internal static class SteamWordForTests
{
    [ModuleInitializer]
    internal static void NoSteamHere() => AntiCheatCheck.SteamSource = new AntiCheatCheck.SteamVacFile(() => []).Word;
}
