using ExoLauncher.Helpers;

namespace ExoLauncher.Services.Account;

/// <summary>
/// What a sign-in or creation screen gets back. It carries sentences and the recovery code shown once, never the password
/// and never the token. <see cref="Offline"/> sends the screen to the offline path (CLIENT.md 4.1 F).
/// </summary>
internal sealed record ExoAccountOutcome(bool Ok, string Message, string? Error, int? RetryAfterSeconds, bool Offline, string? RecoveryCode)
{
    /// <summary>Created or signed in: the screen goes on.</summary>
    internal static ExoAccountOutcome Done(string? recoveryCode = null) => new(true, "", null, null, false, recoveryCode);

    /// <summary>The answer to a request: a sentence for the screen, and the flags it needs.</summary>
    internal static ExoAccountOutcome From(AccountReply reply)
    {
        // The service's own key rotation (CLIENT.md 4.2): a calm sentence, not the offline screen; the same action is tried again later.
        if (reply.Error == "keyring-mismatch")
            return new ExoAccountOutcome(false, AccountMessages.For(reply.Error), reply.Error, null, false, null);

        // Busy after the client waited it out (ExoAccountClient.BusyWait): the service is up and answering, so this is not
        // the offline screen and Continue without an account is not offered; the same press works in a moment.
        if (reply.Status == 503 && reply.Error == "busy")
            return new ExoAccountOutcome(false, AccountMessages.For("busy"), "busy", reply.RetryAfterSeconds, false, null);

        if (reply.Unreachable || reply.Status >= 500)
            return new ExoAccountOutcome(false, "Can't reach Exo's account service.", reply.Error, null, true, null);

        if (reply.Error == "locked")
            return new ExoAccountOutcome(false, AccountMessages.Locked(reply.RetryAfterSeconds ?? 60), "locked", reply.RetryAfterSeconds ?? 60, false, null);

        if (reply.Error is null || AccountMessages.Shown.Contains(reply.Error))
            return new ExoAccountOutcome(false, AccountMessages.For(reply.Error), reply.Error, null, false, null);

        // A client bug or a code the screen does not show: logged with its code, shown as the generic sentence.
        AppLog.Warn("Account request failed with " + reply.Error + " (status " + reply.Status + ").");
        return new ExoAccountOutcome(false, AccountMessages.Generic, reply.Error, null, false, null);
    }
}
