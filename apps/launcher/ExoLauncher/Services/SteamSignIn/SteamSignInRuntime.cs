using ExoLauncher.Helpers;

namespace ExoLauncher.Services.SteamSignIn;

/// <summary>The app's one Steam sign-in: Steam's web API, the DPAPI token vault, this PC's device identity.</summary>
internal static class SteamSignInRuntime
{
    private static readonly Lazy<SteamSignInService> Shared = new(() => new SteamSignInService(
        new SteamWebApi(SteamWebApi.CreateDefaultClient()),
        new StoreSecretVault(),
        () => DateTimeOffset.UtcNow,
        SteamSignInService.MachineIdFor(Environment.MachineName, Environment.UserName),
        "Exo on " + Environment.MachineName));

    internal static SteamSignInService Service => Shared.Value;
}
