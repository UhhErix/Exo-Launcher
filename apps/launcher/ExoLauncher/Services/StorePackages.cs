using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>A Store package as Windows lists it for this user: its full name, whether it's a framework, and whether Windows signed it as its own.</summary>
internal sealed record PackageSeen(string FullName, string? DisplayName, string? Publisher, bool Framework, bool System);

/// <summary>
/// One Store app on this PC across its accounts. Mine: its full name for this user (null: not installed for you). Others: how
/// many other accounts have it. Provisioned: its name in Windows' image (null: new accounts don't get it).
/// </summary>
internal sealed record StorePackage(string Family, string Name, string Display, string? Publisher, string? Mine, int Others, string? Provisioned)
{
    internal bool ForMe => Mine is not null;

    /// <summary>Its files stay on this PC once it's removed for you (another account or Windows' image keeps them): Add back registers it again.</summary>
    internal bool Staged => Others > 0 || Provisioned is not null;
}

/// <summary>What one package's removal does and how it comes back, as the preview says it before anything goes.</summary>
internal sealed record PackageLine(string Id, string Name, string Does, string Back, bool Undoable);

/// <summary>
/// Store and preinstalled apps (PC › Apps › Windows apps), as Win11Debloat, BCU and O&amp;O AppBuster see them: this user's
/// packages (PackageManager), every other account's and what Windows' image gives new accounts (read from Windows' own
/// AppxAllUserStore, which any user may read, so listing never asks for rights). Removing for you is PackageManager's, with no
/// prompt, and its files stay while another account or the image keeps them, so Add back registers it again; removing for every
/// account (Remove-AppxPackage -AllUsers and Remove-AppxProvisionedPackage) is one Windows prompt and can't be undone here: only
/// the Microsoft Store brings it back, if it has it. A System Restore point doesn't bring Store apps back, so none is made.
/// Never offered, whatever is asked (<see cref="Never"/>): the Store, App Installer and winget, the app frameworks (VCLibs,
/// UI.Xaml, .NET Native, the Windows App Runtime), Edge and WebView2, Gaming Services, Xbox identity and the pieces games sign
/// in and draw through, Windows Security, the shell (Start, the taskbar, Search, the lock screen, sign-in), language packs,
/// codecs, and every package Windows signed as its own or names by a GUID.
/// </summary>
internal static partial class StorePackages
{
    // By the start of a package's name: (prefix, why), first match wins. Allowed first: the few optional apps in Windows' own namespaces.
    private static readonly string[] Optional =
        ["Microsoft.Windows.Photos", "Microsoft.Windows.DevHome", "Microsoft.Windows.AIHub", "MicrosoftWindows.CrossDevice", "MicrosoftWindows.Client.WebExperience"];

    private const string WindowsNeeds = "Windows needs it";
    private const string AppsNeed = "Store apps need it";
    private const string GamesNeed = "Games need it";
    private const string ExoNeeds = "Exo and other apps run on it";

    private static readonly (string Prefix, string Why)[] NeverPrefixes =
    [
        ("Microsoft.WindowsStore", AppsNeed), ("Microsoft.StorePurchaseApp", AppsNeed), ("Microsoft.DesktopAppInstaller", AppsNeed), ("Microsoft.Winget.", AppsNeed),
        ("Microsoft.VCLibs.", AppsNeed), ("Microsoft.UI.Xaml.", AppsNeed), ("Microsoft.NET.", AppsNeed), ("Microsoft.WindowsAppRuntime", AppsNeed),
        ("MicrosoftCorporationII.WinAppRuntime", AppsNeed), ("Microsoft.Services.Store.Engagement", AppsNeed), ("Microsoft.Advertising.Xaml", AppsNeed),
        ("Microsoft.WidgetsPlatformRuntime", AppsNeed), ("Microsoft.ApplicationCompatibilityEnhancements", AppsNeed),
        ("Microsoft.WebMediaExtensions", AppsNeed), ("Microsoft.HEIFImageExtension", AppsNeed), ("Microsoft.HEVCVideoExtension", AppsNeed),
        ("Microsoft.VP9VideoExtensions", AppsNeed), ("Microsoft.WebpImageExtension", AppsNeed), ("Microsoft.RawImageExtension", AppsNeed),
        ("Microsoft.AV1VideoExtension", AppsNeed), ("Microsoft.MPEG2VideoExtension", AppsNeed), ("Microsoft.AVCEncoderVideoExtension", AppsNeed),
        ("Microsoft.DolbyAudioExtensions", AppsNeed), ("Microsoft.Ink.Handwriting", WindowsNeeds),
        ("Microsoft.GamingServices", GamesNeed), ("Microsoft.GamingApp", GamesNeed), ("Microsoft.XboxIdentityProvider", GamesNeed), ("Microsoft.Xbox.TCUI", GamesNeed),
        ("Microsoft.XboxGameCallableUI", GamesNeed), ("Microsoft.XboxSpeechToTextOverlay", GamesNeed), ("Microsoft.DirectXRuntime", GamesNeed), ("Microsoft.GameInput", GamesNeed),
        ("Microsoft.MicrosoftEdge", ExoNeeds), ("Microsoft.WebView2", ExoNeeds), ("Microsoft.Win32WebViewHost", ExoNeeds),
        ("Microsoft.SecHealthUI", WindowsNeeds), ("Microsoft.Windows.", WindowsNeeds), ("MicrosoftWindows.", WindowsNeeds), ("Windows.", WindowsNeeds),
        ("Microsoft.AAD.BrokerPlugin", WindowsNeeds), ("Microsoft.AccountsControl", WindowsNeeds), ("Microsoft.LockApp", WindowsNeeds), ("Microsoft.CredDialogHost", WindowsNeeds),
        ("Microsoft.ECApp", WindowsNeeds), ("Microsoft.BioEnrollment", WindowsNeeds), ("Microsoft.AsyncTextService", WindowsNeeds), ("Microsoft.LanguageExperiencePack", WindowsNeeds),
        ("Microsoft.OneDriveSync", WindowsNeeds), ("Microsoft.MicrosoftEdgeDevToolsClient", WindowsNeeds), ("NcsiUwpApp", WindowsNeeds), ("Microsoft.WindowsAppSDK", AppsNeed),
        ("ExoLauncher", "Exo itself"),
    ];

    /// <summary>Why a package is never offered for removal, or null when it may be. <paramref name="system"/>: Windows signed it as its own.</summary>
    internal static string? Never(string name, bool system = false)
    {
        if (system || Guid().IsMatch(name)) return WindowsNeeds;
        if (Optional.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return null;
        foreach (var (prefix, why) in NeverPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return why;
        // Anti-cheat, Exo, a graphics driver's own panel: Exo's own rules for Installed apps.
        return InstalledPrograms.CareFor(name, null, name) is InstalledPrograms.Cares.Exo or InstalledPrograms.Cares.AntiCheat or InstalledPrograms.Cares.ExoUses ? GamesNeed : null;
    }

    [GeneratedRegex(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-", RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    // A package full name: Name_Version_Architecture_ResourceId_PublisherId (ResourceId may be empty or ~).
    [GeneratedRegex(@"^(?<name>[A-Za-z0-9.\-]{3,50})_(?<version>\d+(\.\d+){0,3})_(?<arch>[A-Za-z0-9]+)_(?<resource>[A-Za-z0-9.\-~]*)_(?<publisher>[a-z0-9]{13})\z", RegexOptions.CultureInvariant)]
    private static partial Regex FullName();

    [GeneratedRegex(@"^[A-Za-z0-9.\-]{3,50}_[a-z0-9]{13}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex FamilyName();

    /// <summary>A full name's package name and family; null for anything else, and for a resource package (a language or scale split).</summary>
    internal static (string Name, string Family)? Parts(string fullName)
    {
        var match = FullName().Match(fullName);
        if (!match.Success || match.Groups["resource"].Value.StartsWith("split", StringComparison.OrdinalIgnoreCase)) return null;
        return (match.Groups["name"].Value, match.Groups["name"].Value + "_" + match.Groups["publisher"].Value);
    }

    /// <summary>"Microsoft.BingNews" as "Bing News", "king.com.CandyCrushSaga" as "Candy Crush Saga": for an app only other accounts have (Windows names it only for its own users).</summary>
    internal static string Pretty(string name)
    {
        var last = name.LastIndexOf('.');
        var bare = last >= 0 && last < name.Length - 1 ? name[(last + 1)..] : name;
        return CamelBreak().Replace(bare, " ").Trim();
    }

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBreak();

    /// <summary>
    /// Every package once, by family, from what this user has (<paramref name="mine"/>), every other account's
    /// (<paramref name="others"/>: their full names, per account) and Windows' image (<paramref name="provisioned"/>, less what
    /// was taken out of it). Frameworks, resource packages and what's never offered are left out; Hidden: how many of those there are.
    /// </summary>
    internal static (List<StorePackage> Packages, int Hidden) Gather(IEnumerable<PackageSeen> mine, IEnumerable<IReadOnlyCollection<string>> others,
        IEnumerable<string> provisioned, IReadOnlySet<string> deprovisioned)
    {
        var all = new Dictionary<string, (string Name, string? Display, string? Publisher, string? Mine, int Others, string? Provisioned)>(StringComparer.OrdinalIgnoreCase);
        var never = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seen in mine)
        {
            if (Parts(seen.FullName) is not { } parts) continue;
            if (seen.Framework) { frameworks.Add(parts.Family); continue; }
            if (Never(parts.Name, seen.System) is not null) { never.Add(parts.Family); continue; }
            all[parts.Family] = (parts.Name, seen.DisplayName, seen.Publisher, seen.FullName, 0, null);
        }
        foreach (var account in others)
            foreach (var parts in account.Select(Parts).OfType<(string Name, string Family)>().DistinctBy(parts => parts.Family.ToUpperInvariant()))
            {
                if (frameworks.Contains(parts.Family)) continue;
                if (never.Contains(parts.Family) || Never(parts.Name) is not null) { never.Add(parts.Family); continue; }
                all[parts.Family] = all.TryGetValue(parts.Family, out var known) ? known with { Others = known.Others + 1 } : (parts.Name, null, null, null, 1, null);
            }
        foreach (var full in provisioned)
        {
            if (Parts(full) is not { } parts || deprovisioned.Contains(parts.Family) || frameworks.Contains(parts.Family)) continue;
            if (never.Contains(parts.Family) || Never(parts.Name) is not null) { never.Add(parts.Family); continue; }
            all[parts.Family] = all.TryGetValue(parts.Family, out var known) ? known with { Provisioned = full } : (parts.Name, null, null, null, 0, full);
        }
        var packages = all.Select(pair => new StorePackage(pair.Key, pair.Value.Name, pair.Value.Display is { Length: > 0 } shown && !shown.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? shown : Pretty(pair.Value.Name),
                pair.Value.Publisher, pair.Value.Mine, pair.Value.Others, pair.Value.Provisioned))
            .OrderBy(package => package.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        return (packages, never.Count);
    }

    /// <summary>What removing it does, for you or for every account, and how it comes back: said before anything goes. Null when there's nothing of it to remove that way.</summary>
    internal static PackageLine? Line(StorePackage package, bool everyone)
    {
        if (!everyone)
        {
            if (!package.ForMe) return null;
            var stays = new List<string>();
            if (package.Others > 0) stays.Add(package.Others == 1 ? "1 other account keeps it" : $"{package.Others} other accounts keep it");
            if (package.Provisioned is not null) stays.Add("new accounts still get it");
            return new(package.Family, package.Display, "Removed for you, with its data" + (stays.Count > 0 ? "; " + string.Join(", ", stays) : ""),
                package.Staged ? "Add back puts it back here (its files stay on this PC); its data doesn't come back" : "Only the Microsoft Store brings it back, if it has it",
                package.Staged);
        }
        return new(package.Family, package.Display,
            "Removed for every account" + (package.Provisioned is not null ? ", and new accounts won't get it" : "") + ", with their data",
            "Can't be undone here: only the Microsoft Store brings it back, if it has it", false);
    }

    /// <summary>
    /// The elevated step that removes a package for every account and from Windows' image. Its names travel encoded, as
    /// literals, and only names a package can have reach it; null for anything else.
    /// </summary>
    internal static string? EveryoneStep(StorePackage package)
    {
        if (!FamilyName().IsMatch(package.Family) || !package.Family.StartsWith(package.Name + "_", StringComparison.Ordinal) ||
            package.Provisioned is { } full && Parts(full)?.Family != package.Family || Never(package.Name) is not null) return null;
        var script = new StringBuilder("$ErrorActionPreference='SilentlyContinue';")
            .Append($"Get-AppxPackage -AllUsers -Name '{package.Name}'|Where-Object{{$_.PackageFamilyName -eq '{package.Family}'}}|ForEach-Object{{Remove-AppxPackage -Package $_.PackageFullName -AllUsers}};");
        if (package.Provisioned is { } provisioned)
            script.Append($"Get-AppxProvisionedPackage -Online|Where-Object{{$_.PackageName -eq '{provisioned}'}}|Remove-AppxProvisionedPackage -Online|Out-Null;");
        return $"{LeftoverScan.PowerShell} -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
    }

    /// <summary>The Microsoft Store's page for a package family: where an app removed for every account comes back from.</summary>
    internal static string? StoreLink(string family) => FamilyName().IsMatch(family) ? "ms-windows-store://pdp/?PFN=" + family : null;

    // ---- this PC ----

    private const string AllUsers = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore";

    /// <summary>Every Store app on this PC across its accounts, read without asking for rights; Hidden: the parts of Windows never offered.</summary>
    internal static (List<StorePackage> Packages, int Hidden) Read()
    {
        var mine = new List<PackageSeen>();
        try
        {
            foreach (var package in new Windows.Management.Deployment.PackageManager().FindPackagesForUser(""))
            {
                try
                {
                    if (package.IsResourcePackage) continue;
                    mine.Add(new(package.Id.FullName, package.DisplayName, package.PublisherDisplayName, package.IsFramework,
                        package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System));
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException) { }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { AppLog.Debug("Store apps: this user's unreadable: " + ex.GetType().Name); }
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        var others = new List<IReadOnlyCollection<string>>();
        var provisioned = new List<string>();
        var deprovisioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var store = Registry.LocalMachine.OpenSubKey(AllUsers);
            foreach (var name in store?.GetSubKeyNames() ?? [])
            {
                using var sub = store!.OpenSubKey(name);
                if (sub is null) continue;
                // An account's packages under its SID (a local or Microsoft account, S-1-5-21; an Entra one, S-1-12-1).
                if ((name.StartsWith("S-1-5-21-", StringComparison.Ordinal) || name.StartsWith("S-1-12-1-", StringComparison.Ordinal)) && !name.Equals(me, StringComparison.OrdinalIgnoreCase))
                    others.Add(sub.GetSubKeyNames());
                else if (name == "Applications") provisioned.AddRange(sub.GetSubKeyNames());
                else if (name == "Deprovisioned") deprovisioned.UnionWith(sub.GetSubKeyNames());
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { AppLog.Debug("Store apps: other accounts unreadable: " + ex.GetType().Name); }
        return Gather(mine, others, provisioned, deprovisioned);
    }

    /// <summary>Puts a package removed for you back from the files still on this PC: Windows registers it for you again.</summary>
    internal static async Task<bool> AddBackAsync(string family, CancellationToken ct)
    {
        if (HostMode.IsDisposable || !FamilyName().IsMatch(family)) return false;
        try
        {
            var result = await new Windows.Management.Deployment.PackageManager()
                .RegisterPackageByFamilyNameAsync(family, null, Windows.Management.Deployment.DeploymentOptions.None, null, null).AsTask(ct).ConfigureAwait(false);
            if (result.ExtendedErrorCode is null) return true;
            AppLog.Info($"Store apps: {family} wasn't added back: 0x{result.ExtendedErrorCode.HResult:X8}");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Info($"Store apps: {family} wasn't added back: {ex.GetType().Name}");
            return false;
        }
    }
}
