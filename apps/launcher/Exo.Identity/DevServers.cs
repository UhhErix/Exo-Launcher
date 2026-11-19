using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// Exo's dev servers: cloud/account and cloud/mailbox deployed with <c>--env dev</c> on the free plan (plan §2.17). An
/// Exo app uses them in place of the real ones while %LOCALAPPDATA%\Exo\dev.json names them:
/// <c>{"account": "https://…/", "mailbox": "wss://…/", "devKey": "…"}</c>. Only those two hosts are taken, so the file
/// can't send an app anywhere else; and with it the identity lives in a folder of its own (<see cref="IdentityFolder"/>,
/// keys with a test suffix), so nothing made against the dev servers mixes with the real profile.
/// </summary>
public static class DevServers
{
    public const string AccountHost = "exo-account-dev.exo-erix.workers.dev", MailboxHost = "exo-mailbox-dev.exo-erix.workers.dev";

    /// <param name="DevKey">Opens the dev account service's own way in (the Worker's DEV_KEY), or null.</param>
    public sealed record Config(Uri Account, Uri Mailbox, string? DevKey);

    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exo");
    private static readonly Lazy<Config?> Cached = new(() => Read(Path.Combine(Root, "dev.json")));

    /// <summary>The dev servers while dev.json names them (read once per run), else null.</summary>
    public static Config? Current => Cached.Value;

    /// <summary>The identity's folder while the dev servers are on: %LOCALAPPDATA%\Exo\identity-dev.</summary>
    public static string IdentityFolder => Path.Combine(Root, "identity-dev");

    /// <summary>Whether <paramref name="endpoint"/> is the dev mailbox's root.</summary>
    public static bool IsDevMailbox(Uri endpoint) =>
        endpoint is { Scheme: "wss", Host: MailboxHost, AbsolutePath: "/", Query: "", UserInfo: "" } && endpoint.IsDefaultPort;

    /// <summary>The file's servers when both are the dev ones (account over https, mailbox over wss, roots only).</summary>
    public static Config? Read(string file)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json) return null;
            var account = Uri.TryCreate(json["account"]?.GetValue<string>(), UriKind.Absolute, out var a) ? a : null;
            var mailbox = Uri.TryCreate(json["mailbox"]?.GetValue<string>(), UriKind.Absolute, out var m) ? m : null;
            var key = json["devKey"]?.GetValue<string>() is { Length: >= 32 and <= 256 } k ? k : null;
            if (account is not { Scheme: "https", Host: AccountHost, AbsolutePath: "/", Query: "", UserInfo: "" } || !account.IsDefaultPort) return null;
            return mailbox is not null && IsDevMailbox(mailbox) ? new Config(account, mailbox, key) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }
}
