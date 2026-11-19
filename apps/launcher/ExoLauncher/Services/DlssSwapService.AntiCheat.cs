using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Exo does not change a file in a game that runs anti-cheat unless the user
/// allowed it for that game (settings <c>upscalerAntiCheatConsent</c>), after
/// being told which anti-cheat it is and that a swapped DLL can get the
/// account banned. The consent names the anti-cheat it was given for and
/// lapses when a different one is detected. Riot games and games with Riot
/// Vanguard (kernel anti-cheat) are never changed, consent or not. Detection is
/// <see cref="AntiCheatCheck"/>'s and fails closed: an install folder Exo cannot
/// read counts as protected with no way to allow it.
/// </summary>
public sealed partial class DlssSwapService
{
    /// <summary>The shared tables (<see cref="AntiCheatCheck"/>); the frame generation walk names them here.</summary>
    internal static string? AntiCheatForFolder(string folderName) => AntiCheatCheck.ForFolder(folderName);

    internal static string? AntiCheatForFile(string fileName) => AntiCheatCheck.ForFile(fileName);

    internal const string RiotVanguard = "Riot Vanguard";

    /// <summary>The consent Settings' one-time allow gives every game (<see cref="AppSettings.UpscalerAntiCheatEverywhere"/>).</summary>
    internal const string AnyAntiCheat = "*";

    /// <summary>The install could not be read, so it cannot be cleared of anti-cheat.</summary>
    internal const string UnreadableInstall = "unreadable";

    /// <summary>
    /// Riot games, Riot Vanguard (kernel anti-cheat) and installs Exo cannot
    /// read: no consent lifts these.
    /// </summary>
    internal static bool NeverAllowed(GameEntry game, string antiCheat) =>
        AntiCheatCheck.IsRiot(game) || AntiCheatCheck.IsUnknownLabel(antiCheat) ||
        AntiCheatCheck.Split(antiCheat).Any(name => name is RiotVanguard || AntiCheatCheck.IsUnknownLabel(name));

    /// <summary>
    /// The user's consent counts only for the anti-cheats it was given for: the full set, so consent for one does not
    /// carry to the game once it gains another.
    /// </summary>
    internal static bool Accepted(GameEntry game, string antiCheat, string? consented) =>
        !NeverAllowed(game, antiCheat) && consented is not null &&
        (consented == AnyAntiCheat || string.Equals(antiCheat, consented, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>never</c>, <c>available</c> (the user may allow it) or <c>given</c>; null without anti-cheat.</summary>
    internal static string? ConsentState(GameEntry game, string? antiCheat, string? consented) =>
        antiCheat is null ? null
        : NeverAllowed(game, antiCheat) ? "never"
        : Accepted(game, antiCheat, consented) ? "given"
        : "available";

    /// <summary>
    /// The upscaler panel's one plain status for a game with anti-cheat (the Allow button beside it; the ban warning is said
    /// once, in Allow's confirmation): "Uses X. Upscaler updates are off for this game." or "... allowed."; Riot's and an
    /// install Exo couldn't read keep their own sentence.
    /// </summary>
    internal static string AntiCheatMessage(GameEntry game, string antiCheat, string? consented)
    {
        var answer = AntiCheatCheck.FromLabel(antiCheat);
        if (NeverAllowed(game, antiCheat))
            return AntiCheatCheck.Sentence(game, answer, AntiCheatCheck.Change.UpscalerSwap, consented) ?? "Exo never changes Riot games or games with Vanguard.";
        var uses = answer.Kind == AntiCheatCheck.Kind.PossiblyVac ? "May use Valve Anti-Cheat." : $"Uses {answer.Name}.";
        return Accepted(game, antiCheat, consented) ? uses + " Upscaler updates allowed." : uses + " Upscaler updates are off for this game.";
    }
}
