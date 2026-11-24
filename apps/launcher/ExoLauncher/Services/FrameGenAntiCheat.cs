using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static class FrameGenAntiCheat
{
    internal const string OnlineBlockedMessage = "Mods would get you banned";

    internal static AntiCheatCheck.Answer AnswerFor(GameEntry? game, string? antiCheatLabel)
    {
        if (game is not null && AntiCheatCheck.IsRiot(game))
            return new(AntiCheatCheck.Kind.Found, [DlssSwapService.RiotVanguard], "riot");

        if (antiCheatLabel is null)
        {
            if (game is not null && AntiCheatCheck.WithoutAntiCheatLaunch(game) is not null)
                return new(AntiCheatCheck.Kind.None, [], AntiCheatCheck.WithoutAntiCheatEvidence);
            return new(AntiCheatCheck.Kind.None, [], "clean");
        }

        if (AntiCheatCheck.IsUnknownLabel(antiCheatLabel))
            return new(AntiCheatCheck.Kind.Unknown, [], antiCheatLabel);

        return new(AntiCheatCheck.Kind.Found, [antiCheatLabel], "framegen");
    }

    internal static GameAntiCheatClass Classify(GameEntry? game, AntiCheatCheck.Answer antiCheat) =>
        AntiCheatClass.Classify(game ?? new GameEntry { Id = "", Title = "", Store = StoreKind.Local }, antiCheat);

    internal static GameAntiCheatClass Classify(GameEntry? game, string? antiCheatLabel) =>
        Classify(game, AnswerFor(game, antiCheatLabel));

    internal static DllModPolicy PolicyFor(GameEntry? game, AntiCheatCheck.Answer antiCheat) =>
        AntiCheatClass.DllPolicyFor(Classify(game, antiCheat));

    internal static DllModPolicy PolicyFor(GameEntry? game, string? antiCheatLabel) =>
        PolicyFor(game, AnswerFor(game, antiCheatLabel));

    internal static bool IsDllOptInWithConsent(GameEntry? game, string? antiCheatLabel) =>
        PolicyFor(game, antiCheatLabel) == DllModPolicy.OptInWithBanRiskConfirmation;

    internal static bool IsDllOptInWithConsent(GameEntry? game, AntiCheatCheck.Answer antiCheat) =>
        PolicyFor(game, antiCheat) == DllModPolicy.OptInWithBanRiskConfirmation;

    internal static bool IsDllDisallowed(GameEntry? game, AntiCheatCheck.Answer antiCheat)
    {
        var policy = PolicyFor(game, antiCheat);
        if (policy == DllModPolicy.Disallowed) return true;
        if (policy == DllModPolicy.AllowedWithOfflineNote && antiCheat.Kind != AntiCheatCheck.Kind.None)
            return true;
        return false;
    }

    internal static bool IsDllDisallowed(GameEntry? game, string? antiCheatLabel) =>
        IsDllDisallowed(game, AnswerFor(game, antiCheatLabel));

    internal static bool IsOfficialOfflineModeActive(GameEntry? game, string? evidence = null) =>
        game is not null &&
        !AntiCheatCheck.IsRiot(game) &&
        (AntiCheatCheck.WithoutAntiCheatLaunch(game) is not null ||
         string.Equals(evidence, AntiCheatCheck.WithoutAntiCheatEvidence, StringComparison.OrdinalIgnoreCase));

    internal static string BanRiskWarning(GameEntry? game, string? antiCheatLabel) =>
        AntiCheatClass.BanRiskConfirmation(game?.Title ?? "This game", antiCheatLabel);

    internal static string OfflineNote(GameEntry? game) =>
        AntiCheatClass.OfflineOneTimeNote(game?.Title ?? "This game");

    internal static bool CanOfferOfflineLaunch(GameEntry game) =>
        game.Store == StoreKind.Steam &&
        !AntiCheatCheck.IsRiot(game) &&
        AntiCheatCheck.SteamAppId(game) is { } appId &&
        AntiCheatCheck.SteamFreeEntry(appId) is not null;
}
