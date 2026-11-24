using ExoLauncher.Models;

namespace ExoLauncher.Services.Macros;

internal static class MacroPolicy
{
    internal const string TierOnboardOnly = "onboard-only";
    internal const string TierSoftwareAndOnboard = "software-and-onboard";
    internal const string OfflineOneTimeNote = "Offline / single-player game: Tier 2 software macros are allowed. Keep software macros off in online or competitive games.";

    private static readonly string[] OnlineOrCompetitiveAntiCheats =
    [
        "Vanguard",
        "Ricochet",
        "Valve Anti-Cheat",
        "FACEIT",
        "Easy Anti-Cheat",
        "BattlEye",
        "Anti-Cheat Expert",
        "Hyperion",
        "EA AntiCheat",
        "Warden",
        "nProtect GameGuard",
        "XIGNCODE",
        "PunkBuster",
    ];

    internal static MacroGamePolicy Evaluate(GameEntry game, AntiCheatCheck.Answer answer, IReadOnlyList<MacroGameConsent> consents)
    {
        var isOnlineOrProtected = answer.AssumeProtected
            || answer.Kind != AntiCheatCheck.Kind.None
            || AntiCheatCheck.IsRiot(game);

        var names = new List<string>(answer.Names);
        if (AntiCheatCheck.IsRiot(game) && !names.Any(n => n.Contains("Vanguard", StringComparison.OrdinalIgnoreCase)))
        {
            names.Add("Riot Vanguard");
        }

        var label = answer.Label ?? (AntiCheatCheck.IsRiot(game) ? "Riot Vanguard" : null);
        var kind = answer.Kind switch
        {
            AntiCheatCheck.Kind.None => AntiCheatCheck.IsRiot(game) ? "found" : "none",
            AntiCheatCheck.Kind.Found => "found",
            AntiCheatCheck.Kind.PossiblyVac => "possiblyVac",
            _ => "unknown",
        };

        if (isOnlineOrProtected)
        {
            var reason = kind == "unknown"
                ? "Anti-cheat or online status is unknown, so Exo treats this game as online."
                : $"Uses {(names.Count > 0 ? string.Join(", ", names) : (label ?? "online anti-cheat"))}.";

            return new MacroGamePolicy
            {
                GameId = game.Id,
                Title = game.Title,
                AntiCheatKind = kind,
                AntiCheatNames = names,
                AntiCheatLabel = label ?? "unknown (treated as online)",
                SoftwareAllowed = false,
                AllowedTier = TierOnboardOnly,
                RequiresConfirmation = false,
                Confirmed = false,
                AutoEnableAllowed = false,
                SoftwareWarning = $"{reason} Tier 2 software macros are disabled (no override). Only Tier 1 onboard hardware device macros are allowed.",
            };
        }

        var noteAcknowledged = consents.Any(c =>
            string.Equals(c.GameId, game.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.AntiCheatLabel, "offline-note", StringComparison.Ordinal));

        return new MacroGamePolicy
        {
            GameId = game.Id,
            Title = game.Title,
            AntiCheatKind = "none",
            AntiCheatNames = [],
            AntiCheatLabel = null,
            SoftwareAllowed = true,
            AllowedTier = TierSoftwareAndOnboard,
            RequiresConfirmation = !noteAcknowledged,
            Confirmed = noteAcknowledged,
            AutoEnableAllowed = true,
            SoftwareWarning = OfflineOneTimeNote,
        };
    }

    internal static bool IsKnownOnlineOrCompetitive(string name) =>
        OnlineOrCompetitiveAntiCheats.Any(ac => name.Contains(ac, StringComparison.OrdinalIgnoreCase));

    internal static bool CanRunSoftwareMacros(MacroGamePolicy? policy, bool manualActivation)
    {
        if (policy is null) return false;
        return policy.SoftwareAllowed;
    }

    internal static bool IsBindingHardwareSafe(MacroBinding binding) =>
        string.Equals(binding.Delivery, MacroDelivery.Onboard, StringComparison.OrdinalIgnoreCase);
}
