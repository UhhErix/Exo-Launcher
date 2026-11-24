using ExoLauncher.Models;

namespace ExoLauncher.Services.Macros;

internal static class MacroPolicy
{
    private static readonly string[] KernelOrOnlineAntiCheats =
    [
        "Vanguard",
        "Easy Anti-Cheat",
        "BattlEye",
        "Anti-Cheat Expert",
        "FACEIT",
        "Ricochet",
        "EA AntiCheat",
        "Valve Anti-Cheat",
        "Warden",
        "Hyperion",
        "nProtect GameGuard",
        "XIGNCODE",
        "PunkBuster",
    ];

    internal static MacroGamePolicy Evaluate(GameEntry game, AntiCheatCheck.Answer answer, IReadOnlyList<MacroGameConsent> consents)
    {
        var isProtected = answer.AssumeProtected || AntiCheatCheck.IsRiot(game);
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

        var confirmed = isProtected
            && label is not null
            && consents.Any(c =>
                string.Equals(c.GameId, game.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.AntiCheatLabel, label, StringComparison.Ordinal));

        string? warning = null;
        if (isProtected)
        {
            var named = names.Count > 0 ? string.Join(", ", names) : (label ?? "anti-cheat");
            warning = $"Uses {named}. Software macros and injected input can break this game's rules and risk a ban. Exo never auto-enables software macros here; confirm explicitly per game, or use onboard device memory remaps (hardware-level, no software risk).";
        }

        return new MacroGamePolicy
        {
            GameId = game.Id,
            Title = game.Title,
            AntiCheatKind = kind,
            AntiCheatNames = names,
            AntiCheatLabel = label,
            RequiresConfirmation = isProtected,
            Confirmed = confirmed,
            AutoEnableAllowed = !isProtected,
            SoftwareWarning = warning,
        };
    }

    internal static bool IsKnownKernelOrOnline(string name) =>
        KernelOrOnlineAntiCheats.Any(ac => name.Contains(ac, StringComparison.OrdinalIgnoreCase));

    internal static bool CanRunSoftwareMacros(MacroGamePolicy? policy, bool manualActivation)
    {
        if (policy is null || !policy.RequiresConfirmation) return true;
        if (!manualActivation && !policy.AutoEnableAllowed) return false;
        return policy.Confirmed;
    }

    internal static bool IsBindingHardwareSafe(MacroBinding binding) =>
        string.Equals(binding.Delivery, MacroDelivery.Onboard, StringComparison.OrdinalIgnoreCase);
}
