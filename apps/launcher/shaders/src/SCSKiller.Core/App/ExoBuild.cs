namespace SCSKiller.Core.App;

/// <summary>The Exo build: the engine that Exo Launcher ships (engine/src/ExoShaders.Service, compiled with EXO_BUILD). It is
/// local-only: no request to SCSKiller's servers or to Patreon, no sign-in, sharing or upload. Which games count as having
/// anti-cheat, and what may record in them, is upstream's (GameFiles, the offline and risk sessions). Upstream builds
/// compile with the constant off, so a merge keeps this code as it is. The Oodle and zlib downloads from GitHub stay: they are third-party and licensed (engine/CONTRACT.md).</summary>
public static class ExoBuild
{
    public const bool Enabled =
#if EXO_BUILD
        true;
#else
        false;
#endif

    /// <summary>SCSKiller's servers and Patreon: an Exo build sends no request to these hosts (any subdomain too).</summary>
    public static readonly string[] BlockedDomains = ["scskiller.com", "scskiller.io", "scskiller.xyz", "patreon.com"];

    public static bool BlockedHost(Uri uri) =>
        BlockedDomains.Any(d => uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
}
