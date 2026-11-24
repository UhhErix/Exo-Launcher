namespace ExoLauncher.Services.Account;

/// <summary>
/// What each account error says on screen (CLIENT.md 4.2). Codes that are client bugs, or that the spec says never to show
/// (too-big), get the generic sentence, and the caller logs the code with AppLog instead.
/// </summary>
internal static class AccountMessages
{
    internal const string Generic = "Exo's account service didn't answer. Try again in a moment.";

    private static readonly Dictionary<string, string> Sentences = new()
    {
        ["bad-email"] = "Enter an email address, like name@example.com.",
        ["bad-password"] = "Use at least 12 characters.",
        ["common-password"] = "That password is too common. Try a longer one that's harder to guess.",
        ["email-taken"] = "An account already exists for this email. Sign in instead.",
        ["wrong-credentials"] = "That email and password don't match. Check both and try again.",
        ["wrong-code"] = "That recovery code doesn't match this account.",
        ["wrong-password"] = "That password isn't right.",
        ["slow-down"] = "Too many attempts. Wait a minute and try again.",
        ["signed-out"] = "Your session ended. Sign in again to keep syncing.",
        ["account-full"] = "Your sync storage is full. Your data stays on this PC.",
        ["busy"] = "Exo's account service is busy. Try again in a moment.",
        ["server-error"] = "Exo's account service can't be reached right now. Try again later.",
        ["not-configured"] = "Exo's account service can't be reached right now. Try again later.",
        // The service is between keys (its own rotation); nothing is wrong with this PC, and the same action works later.
        ["keyring-mismatch"] = "Exo's account service has a problem. Try again later.",
        ["passkey-limit"] = "You've reached the passkey limit for this account.",
        ["passkey-exists"] = "That passkey is already added to this account.",
        ["passkey-replay"] = "That passkey check was rejected. Try again or use your password.",
        ["passkey-uv-required"] = "Verify with Windows Hello or your security key PIN and try again.",
        ["reauth-required"] = "Enter your account password to confirm adding a passkey.",
        ["last-sign-in-method"] = "Set a password before removing your last passkey.",
        ["bad-passkey"] = "Windows couldn't verify that passkey. Try again or use your password.",
        ["bad-challenge"] = "That passkey request expired. Try again.",
        ["bad-signature"] = "That passkey didn't match this account. Try again or use your password.",
        ["unsupported-algorithm"] = "That security key isn't supported here. Use Windows Hello or an ES256/RS256 key.",
    };

    /// <summary>Codes a screen can show: each has its own sentence above. locked is shown through <see cref="Locked"/>.</summary>
    internal static readonly IReadOnlyCollection<string> Shown = [.. Sentences.Keys];

    internal static string For(string? code) =>
        code is not null && Sentences.TryGetValue(code, out var text) ? text : Generic;

    /// <summary>The lock countdown, m:ss (CLIENT.md 4.2).</summary>
    internal static string Locked(int seconds)
    {
        var wait = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"Too many tries for this email from this PC. Wait {(int)wait.TotalMinutes}:{wait.Seconds:00} and try again.";
    }
}
