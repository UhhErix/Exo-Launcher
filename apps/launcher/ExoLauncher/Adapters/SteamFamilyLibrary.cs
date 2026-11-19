using System.Globalization;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam Families (2024 on): which of the library's games the account's family shares with it, and who owns each.
/// Three sources, best first:
/// <list type="number">
/// <item>Steam's family service (<c>IFamilyGroupsService/GetSharedLibraryApps</c>), with Exo's Steam sign-in or the
/// Steam website sign-in's token: the whole shared library, installed or not, with names and owners, read even while
/// Steam is closed.</item>
/// <item>The running Steam client: it licenses every family game to the account, so what it licenses beyond the
/// account's own list (the Web API's) is the family's. It answers only for the ids Exo asks about.</item>
/// <item>What either last said, kept per account (<see cref="SteamFamilyShares"/>), while neither can answer.</item>
/// </list>
/// The family service wins: the client's answer is only as complete as the ids Exo knew to ask about (Steam's local
/// app list went missing for a day when Steam moved it, and the family list fell from 110 games to 3). The account's own
/// games are never the family's. Pure, so the merge is tested alone.
/// </summary>
internal static class SteamFamilyLibrary
{
    /// <summary>One shared game: its name as Steam's family service gives it (null when unknown) and its owners' account ids.</summary>
    internal sealed record SharedApp(string AppId, string? Name, IReadOnlyList<string> OwnerAccountIds);

    /// <summary>The family service's answer. <see cref="GroupId"/> 0: the account is in no family (and <see cref="Apps"/> is empty).</summary>
    internal sealed record Shares(ulong GroupId, IReadOnlyDictionary<string, SharedApp> Apps)
    {
        internal static Shares None { get; } = new(0, new Dictionary<string, SharedApp>(StringComparer.Ordinal));
    }

    /// <summary>The family's games for this scan, where they came from, and whether the answer is one to keep.</summary>
    internal sealed record Resolved(IReadOnlyDictionary<string, SharedApp> Apps, string Source, bool Remember)
    {
        internal IReadOnlySet<string> AppIds { get; } = Apps.Keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <param name="service">The family service's answer; null when it was not asked or did not answer.</param>
    /// <param name="clientLicensed">The running client's licensed ids among those asked; null when it did not answer.</param>
    /// <param name="ownedByAccount">The account's own games (the Web API's list); null when unknown.</param>
    /// <param name="localInFamily">localconfig.vdf names family members for the account.</param>
    /// <param name="clientOffline">Steam is in its offline mode: its licence list is what it cached.</param>
    /// <param name="remembered">What a source last said for this account; null when none ever did.</param>
    /// <param name="clientUnchecked">Ids the client was asked about but did not answer for (a later batch failed).</param>
    internal static Resolved Resolve(
        Shares? service,
        IReadOnlySet<string>? clientLicensed,
        IReadOnlySet<string>? ownedByAccount,
        bool localInFamily,
        bool clientOffline,
        IReadOnlyDictionary<string, SharedApp>? remembered,
        IReadOnlyCollection<string>? clientUnchecked = null)
    {
        bool Own(string id) => ownedByAccount?.Contains(id) == true;

        if (service is not null)
        {
            // In no family: an empty list, kept, so a family left (or a member gone) takes its games away.
            var apps = service.GroupId == 0
                ? new Dictionary<string, SharedApp>(StringComparer.Ordinal)
                : service.Apps.Values.Where(app => !Own(app.AppId))
                    .ToDictionary(app => app.AppId, app => Named(app, remembered), StringComparer.Ordinal);
            return new Resolved(apps, "family-service", Remember: true);
        }

        var knownFamily = localInFamily || remembered is { Count: > 0 };
        if (clientLicensed is not null && !clientOffline && ownedByAccount is not null && knownFamily)
        {
            var apps = clientLicensed.Where(id => !Own(id)).ToDictionary(
                id => id,
                id => remembered?.GetValueOrDefault(id) ?? new SharedApp(id, null, []),
                StringComparer.Ordinal);
            // A batch the client never answered says nothing about its ids: a kept share among them stays, so a failed
            // batch can't shrink the family. Only a share the client checked and no longer licenses leaves.
            if (clientUnchecked is { Count: > 0 } && remembered is { Count: > 0 })
                foreach (var id in clientUnchecked)
                    if (!Own(id) && remembered.TryGetValue(id, out var kept))
                        apps.TryAdd(id, kept);
            return new Resolved(apps, "steam-client", Remember: true);
        }

        if (remembered is { Count: > 0 })
            return new Resolved(
                remembered.Values.Where(app => !Own(app.AppId)).ToDictionary(app => app.AppId, StringComparer.Ordinal),
                "remembered", Remember: false);

        return new Resolved(new Dictionary<string, SharedApp>(StringComparer.Ordinal), "none", Remember: false);
    }

    /// <summary>The service's entry, with the name a previous answer had when this one has none.</summary>
    private static SharedApp Named(SharedApp app, IReadOnlyDictionary<string, SharedApp>? remembered) =>
        app.Name is null && remembered?.GetValueOrDefault(app.AppId)?.Name is { } name ? app with { Name = name } : app;

    /// <summary>
    /// Who shares the game, for its page: the owners' names as localconfig.vdf's family block has them ("Sam", "Sam
    /// and Alex", "Sam and 2 others"), the account itself left out. Null when no owner has a known name.
    /// </summary>
    internal static string? SharedBy(SharedApp? app, IReadOnlyDictionary<string, string>? memberNames, string? selfAccountId)
    {
        if (app is null || memberNames is not { Count: > 0 }) return null;
        var names = app.OwnerAccountIds
            .Where(id => !string.Equals(id, selfAccountId, StringComparison.Ordinal))
            .Select(id => memberNames.GetValueOrDefault(id))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return names.Count switch
        {
            0 => null,
            1 => names[0],
            2 => names[0] + " and " + names[1],
            _ => names[0] + " and " + (names.Count - 1).ToString(CultureInfo.InvariantCulture) + " others",
        };
    }

    /// <summary>The account id (the low 32 bits) of a SteamID64; null for anything that is not one.</summary>
    internal static string? AccountIdOf(ulong steamId64) =>
        steamId64 >> 32 == 0x01100001 && (steamId64 & 0xFFFFFFFF) != 0
            ? (steamId64 & 0xFFFFFFFF).ToString(CultureInfo.InvariantCulture)
            : null;
}
