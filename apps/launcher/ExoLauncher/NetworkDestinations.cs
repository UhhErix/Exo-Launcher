namespace ExoLauncher;

/// <summary>
/// Every host Exo's native code sends a request to, each mapped to the row of
/// PRIVACY.md's "Other network requests" table that tells users about it.
/// ExoLauncher.Tests/NetworkDestinationsTests checks three things: every row
/// named here exists in the table, every table row is named here, and every
/// https:// host written in the host's source is either listed here or
/// explicitly classed there as not contacted by native code.
///
/// A new request: list its host here with its PRIVACY.md row (add the row
/// first if none fits), and build its client with <see cref="ExoHttp.Create"/>.
///
/// Out of scope: pages hosted in WebView2 (Spotify's, YouTube Music's and
/// Apple Music's web players, store and Google/Apple sign-in pages) and the
/// pictures Exo's own page loads; those requests are the page's, under the
/// service's own privacy policy, and PRIVACY.md describes them per service.
/// Loopback (the Riot Client's local API) never leaves the PC and is not listed.
/// </summary>
internal static class NetworkDestinations
{
    /// <param name="Host">An exact host, or "*.example.com" for example.com and any subdomain.</param>
    /// <param name="PrivacyRow">The Destination cell of the PRIVACY.md row, verbatim.</param>
    internal sealed record Destination(string Host, string PrivacyRow);

    internal const string UpdateAddress = "Exo's update address (updates.getexo.app, on Exo's Cloudflare account)";
    internal const string ToolReleases = "Store tools and the voice model: Legendary, gogdl and Nile, and NVIDIA's TitaNet voice model, from Exo's cache (updates.getexo.app, on Exo's Cloudflare account)";
    internal const string StoreTools = "Epic, GOG, Amazon (through Legendary, gogdl, Nile)";
    internal const string EpicServices = "Epic account and library services";
    internal const string EpicDownloads = "Epic's download hosts (the manifest list from launcher-public-service-prod06.ol.epicgames.com, and the CDN hosts it names)";
    internal const string LegendaryApi = "Legendary's optional content lists (api.legendary.gl)";
    internal const string Gog = "GOG (gameplay.gog.com, api.gog.com, gamesdb.gog.com, galaxy-library.gog.com)";
    internal const string SteamAccount = "Steam sign-in and your account's library (api.steampowered.com)";
    internal const string SteamProfile = "Steam public game list (steamcommunity.com)";
    internal const string SteamWebsiteFamily = "Steam Family's shared games with the Steam website sign-in (api.steampowered.com)";
    internal const string TrackerGg = "tracker.gg";
    internal const string RiotHours = "Riot hours (riot.getexo.app, on Exo's Cloudflare account, pd.*.a.pvp.net, valorant-api.com)";
    internal const string PcGamingWiki = "PCGamingWiki";
    internal const string CoverArt = "Cover art: Steam CDN and Steam store search, Epic store catalog, GOG, Microsoft Store catalog, and the image servers a store's own data points to";
    internal const string CoverCache = "Exo's cover cache (covers.getexo.app, on Exo's Cloudflare account): a game's title and Steam or GOG id, asked for its portrait when Exo has none";
    internal const string Upscalers = "Vendor upscaler files (NVIDIA DLSS, AMD FidelityFX SDK and Intel XeSS, at pinned versions and each newer release, and DLSS Swapper's archive builds), from the vendors' own GitHub repositories (api.github.com, raw.githubusercontent.com) and DLSS Swapper's download host (dlss-swapper-downloads.beeradmoore.com), and Exo's cache (updates.getexo.app, on Exo's Cloudflare account) when those fail";
    internal const string FrameGenMod = "Frame generation mods (OptiScaler, MFG Unlocker and DLSSG SM86: each one's newest release), from GitHub (api.github.com, github.com and its release file hosts release-assets.githubusercontent.com and objects.githubusercontent.com), and, when GitHub fails, Exo's cache (updates.getexo.app, on Exo's Cloudflare account), which no longer serves these mods and answers not found";
    internal const string StoreRoom = "Store room: Steam store and Steam Web API (store search, ranked lists, game pages), CheapShark (prices from official stores only, and a game's lowest price ever)";
    internal const string StoreTags = "Store room: Steam tag names (IStoreService/GetTagList)";
    internal const string MusicArtwork = "Music artwork (Spotify, YouTube Music and Apple Music image servers)";
    internal const string Shaders = "Exo Shaders (compression libraries for some games, from their makers' release pages on GitHub)";
    internal const string CrashReports = "Crash, freeze and error reports (reports.getexo.app, on Exo's Cloudflare account)";
    internal const string AppTelemetry = "App telemetry (reports.getexo.app, on Exo's Cloudflare account)";
    internal const string UserReports = "Report a problem (reports.getexo.app, on Exo's Cloudflare account)";
    internal const string AmdDrivers = "AMD (newest chipset and graphics driver versions and their downloads: `www.amd.com`, `drivers.amd.com`)";
    internal const string IntelDrivers = "Intel (newest chipset, graphics and network driver versions and their downloads: `www.intel.com`, `downloadmirror.intel.com`)";
    internal const string RealtekDrivers = "Realtek (newest network driver version: `www.realtek.com`)";
    internal const string NvidiaDrivers = "NVIDIA (driver lookup and download: `www.nvidia.com`, `gfwsl.geforce.com`, `api-forums.nvidia.com`, `us.download.nvidia.com`, `international.download.nvidia.com`)";
    internal const string DiscordClient = "Exo's own Discord client (discord.com, gateway.discord.gg, remote-auth-gateway.discord.gg, cdn.discordapp.com, Discord's media proxy on discordapp.net, Discord's voice servers on discord.media, Discord's file uploads for apps on discord-attachments-uploads-prd.storage.googleapis.com)";
    internal const string StripeCards = "Stripe's card form in Exo's Discord Shop (js.stripe.com, api.stripe.com, m.stripe.network, hooks.stripe.com), only while you add a card or your bank confirms a payment";
    internal const string DiscordGifs = "GIF hosts Discord points to (*.klipy.com, *.tenor.com, *.giphy.com, i.imgur.com)";
    internal const string SteamAchievementStats = "Steam's global achievement percentages (api.steampowered.com)";
    internal const string DiscordPlayers = "Video and music players in Discord messages (YouTube, Twitch, Vimeo, TikTok, SoundCloud, Spotify, Apple Music)";
    internal const string AutoEq = "Exo EQ's headphone list and corrections (AutoEq's results on GitHub, raw.githubusercontent.com/jaakkopasanen/AutoEq)";
    internal const string EqualizerApo = "Equalizer APO's installer (SourceForge: downloads.sourceforge.net and the mirror it sends you to on *.dl.sourceforge.net)";
    internal const string ControllerFirmware = "Sony's controller firmware versions (fwupdater.dl.playstation.net)";
    internal const string WindowsRelease = "Microsoft's Windows release information page (learn.microsoft.com)";
    internal const string NetworkCheck = "Best settings › Network's connection check (Cloudflare's 1.1.1.1, your router, and Exo's own domain looked up)";
    internal const string AsusBios = "ASUS's BIOS list and, on your press, the BIOS file itself (`www.asus.com`, dlcdnets.asus.com)";
    internal const string SpotifyShareLinks = "Spotify share links (spotify.link, spotify.app.link)";
    internal const string ExoAccount = "Exo account (accounts.getexo.app, on Exo's Cloudflare account)";
    internal const string ExoRelay = "Exo's relay (Cloudflare Workers)";
    internal const string LrcLib = "Synced lyrics for YouTube Music (LRCLIB, lrclib.net)";
    internal const string Installers = "Game runtimes and app installers: Microsoft (Visual C++, DirectX, .NET and its release list, XNA, WebView2; GameInput from NuGet's `globalcdn.nuget.org`), Creative (OpenAL, `openal.org`), NVIDIA (PhysX, `us.download.nvidia.com`), Epic Games (its new launcher, `launcher-public-service-prod06.ol.epicgames.com` and `content-download-egs.distro.on.epicgames.com`)";
    internal const string DevicePictures = "Device pictures (the makers' own image servers: gmedia.playstation.com, cdn-dynmedia-1.microsoft.com, assets.nintendo.com, store.storeimages.cdn-apple.com, wooting-website.ams3.cdn.digitaloceanspaces.com, resource.logitechg.com, resource.logitech.com, images.ctfassets.net (SteelSeries), assets.corsair.com, dlcdnwebimgs.asus.com, `www.asus.com`, hyperx.com, a.storyblok.com (Cooler Master), `www.deluxworld.com`, ca.sennheiser-hearing.com, assets.sennheiser.com, api.beyerdynamic.de, `www.audio-technica.com`, images.philips.com, sony.scene7.com, assets.fractal-design.com, massdrop-s3.imgix.net (Drop), cdn.prod.website-files.com (Truthear), static.wixstatic.com (Dunu), tanchjim.com, intuaura.net, `www.cherry.de`, motospeed.cc, `www.vaxee.co`, and makers' stores on cdn.shopify.com; where a maker publishes no large or clean picture, an authorized store's copy of its official shot: Best Buy (`pisces.bbystatic.com`, `multimedia.bbycastatic.ca`), MaxGaming (`us.maxgaming.com`) and headphone and IEM stores on cdn.shopify.com; OpenMouse's img.openmouse.app)";

    internal static readonly Destination[] Hosts =
    [
        // AppUpdateService: Exo's update address (cloud/updates); it sends a release's files from Exo's own copy on Cloudflare.
        // DriverAdvisories: the signed driver advisories (/v1/advisories.json), at most once a day.
        new("updates.getexo.app", UpdateAddress),
        // ExoAccountClient: the account service (cloud/account); sign-in, the account, and its synced items.
        new("accounts.getexo.app", ExoAccount),
        // CrashReport: every install (owner, 2026-11-09: no switch); cloud/reports.
        new("reports.getexo.app", CrashReports),
        // Telemetry: every install (owner, 2026-11-09: no switch; Telemetry.cs).
        new("reports.getexo.app", AppTelemetry),
        // UserReport: Settings > About > Report a problem, only on Send; cloud/reports files it in the private Exo-Reports repo.
        new("reports.getexo.app", UserReports),

        // VerifiedGitHubReleaseDownloader: pinned tool assets (Legendary, gogdl, Nile), read from Exo's cache.
        new("updates.getexo.app", ToolReleases),

        // EpicPlaytime: the playtime endpoint.
        new("library-service.live.use1a.on.epicgames.com", EpicServices),
        // StoreGifts: a one-time exchange code that signs the hidden gifts page in to Epic's store.
        new("account-public-service-prod.ol.epicgames.com", EpicServices),
        // StoreGifts: the exchange code goes to Epic's sign-in page (www.epicgames.com/id/exchange).
        new("www.epicgames.com", EpicServices),

        // EpicNewLauncherInstaller: Epic's new launcher's installer, for the Game clients card's Fix and for Settings > Epic >
        // Switch to Epic's new launcher (the link sends the download on to Epic's download host).
        new("launcher-public-service-prod06.ol.epicgames.com", Installers),
        new("content-download-egs.distro.on.epicgames.com", Installers),

        // EpicCdn: a game's manifest list (Legendary's own request before every install), then a 256 KB timing probe of that
        // manifest on each CDN host it names, so Legendary downloads from the fastest. Only these hosts are probed.
        new("launcher-public-service-prod06.ol.epicgames.com", EpicDownloads),
        new("epicgames-download1.akamaized.net", EpicDownloads),
        new("*.epicgamescdn.com", EpicDownloads),
        new("download.epicgames.com", EpicDownloads),
        new("fastly-download.epicgames.com", EpicDownloads),
        new("egdownload.fastly-edge.com", EpicDownloads),

        // WebHostBridge.Properties: an installed Epic game's optional content (selective download) list.
        new("api.legendary.gl", LegendaryApi),

        // GogGameplayAchievementProvider, GogOwnedLibraryService.
        new("gameplay.gog.com", Gog),
        new("api.gog.com", Gog),
        new("gamesdb.gog.com", Gog),
        // GogOwnedLibraryService: the owned-games list, with the account's user id in the address and its token.
        new("galaxy-library.gog.com", Gog),


        // TrackerGgPlaytime.
        new("api.tracker.gg", TrackerGg),
        new("riot.getexo.app", RiotHours),
        new("*.a.pvp.net", RiotHours),
        new("valorant-api.com", RiotHours),

        // GameWiki: whether a game outputs HDR (only with HDR for HDR games, or a game page on an HDR screen), and its facts for Fluid Motion Frames (a Radeon that has it).
        new("www.pcgamingwiki.com", PcGamingWiki),

        // CoverArtService, EpicCatalogArt, MicrosoftStoreArt, SteamOwnershipCatalog.
        new("*.steamstatic.com", CoverArt),
        new("steamcdn-a.akamaihd.net", CoverArt),
        new("store.steampowered.com", CoverArt),
        new("steamcommunity.com", CoverArt),
        // SteamCommunityGames: the account's own public game list, only with its SteamID64 and a public profile.
        new("steamcommunity.com", SteamProfile),
        new("api.steampowered.com", CoverArt),
        // SteamWebApi (Services/SteamSignIn): Exo's own Steam sign-in, then the account's games, Steam Family and achievements with its token.
        new("api.steampowered.com", SteamAccount),
        // SteamCommunityGames: without Exo's Steam sign-in, the Steam website sign-in's token (SteamWebsiteToken) reads the same library and family.
        new("api.steampowered.com", SteamWebsiteFamily),
        // AchievementWatch: a game's global unlock percentages, by app id, when its watch starts.
        new("api.steampowered.com", SteamAchievementStats),
        new("*.epicgames.com", CoverArt),
        new("*.unrealengine.com", CoverArt),
        new("api.gog.com", CoverArt),
        // CoverCacheClient: Exo's shared cover cache (cloud/covers) gets the game's title and store id, and answers with an art URL.
        new("covers.getexo.app", CoverCache),
        new("images.gog.com", CoverArt),
        new("*.gog-statics.com", CoverArt),
        new("displaycatalog.mp.microsoft.com", CoverArt),
        new("store-images.s-microsoft.com", CoverArt),
        new("images-eds-ssl.xboxlive.com", CoverArt),
        new("*.riotgames.com", CoverArt),
        new("*.riotcdn.net", CoverArt), // Riot theme manifests and art
        new("*.playvalorant.com", CoverArt),
        new("*.leagueoflegends.com", CoverArt),
        new("*.ubisoft.com", CoverArt),
        new("*.ubi.com", CoverArt),
        new("*.ea.com", CoverArt),
        new("*.origin.com", CoverArt),
        new("*.blizzard.com", CoverArt),
        new("*.battle.net", CoverArt),
        // DlssSwapService.Releases, and DlssSwapService.VendorWatch reading each vendor's latest release: upstream first
        // (UpstreamSource), Exo's cache when upstream fails.
        new("api.github.com", Upscalers),
        new("raw.githubusercontent.com", Upscalers),
        new("dlss-swapper-downloads.beeradmoore.com", Upscalers),
        new("updates.getexo.app", Upscalers),
        new("www.nvidia.com", NvidiaDrivers),
        new("gfwsl.geforce.com", NvidiaDrivers),
        new("us.download.nvidia.com", NvidiaDrivers),
        new("api-forums.nvidia.com", NvidiaDrivers),
        new("international.download.nvidia.com", NvidiaDrivers),

        // ChipsetUpdate: the newest AMD chipset package (only on a Ryzen with AMD's chipset driver), and its download.
        // GpuDriverUpdate: the newest AMD Software for an AMD GPU, and its download.
        new("www.amd.com", AmdDrivers),
        new("drivers.amd.com", AmdDrivers),

        // ChipsetUpdate: the newest Intel Chipset Device Software (only on an Intel processor with it installed), and its download.
        // GpuDriverUpdate: the newest Intel graphics driver for an Intel GPU, and its download.
        // NetworkDrivers: the newest Intel network driver (Ethernet package, its zip's directory and INFs read in parts) and Wi-Fi page.
        new("www.intel.com", IntelDrivers),
        new("downloadmirror.intel.com", IntelDrivers),
        // NetworkDrivers: Realtek's network driver page, for the newest version (opened in the browser to download).
        new("www.realtek.com", RealtekDrivers),

        // DlssSwapService.FrameGen: each mod's newest release from GitHub, its file through VerifiedGitHubReleaseDownloader.Upstream
        // (github.com redirects to its release file hosts); Exo's cache when GitHub fails.
        new("api.github.com", FrameGenMod),
        new("github.com", FrameGenMod),
        new("release-assets.githubusercontent.com", FrameGenMod),
        new("objects.githubusercontent.com", FrameGenMod),
        new("updates.getexo.app", FrameGenMod),

        // StoreService.
        new("store.steampowered.com", StoreRoom),
        new("api.steampowered.com", StoreRoom),
        new("www.cheapshark.com", StoreRoom),
        new("api.steampowered.com", StoreTags),

        // MediaSessionService: the playing song's cover from a web player, for the mini player.
        new("*.googleusercontent.com", MusicArtwork),
        new("*.ggpht.com", MusicArtwork),
        new("*.ytimg.com", MusicArtwork),
        new("*.scdn.co", MusicArtwork),
        new("*.spotifycdn.com", MusicArtwork),
        new("open.spotify.com", MusicArtwork), // a Spotify song's cover from its public oEmbed, when the player names none
        new("*.mzstatic.com", MusicArtwork),
        // SpotifyLinks: a short link's redirect, read from its Location header (the page it names is never fetched).
        new("spotify.link", SpotifyShareLinks),
        new("spotify.app.link", SpotifyShareLinks),
        new("spotify-alternate.app.link", SpotifyShareLinks),
        // SyncedLyrics: the playing YouTube Music song's timed lyrics, when its Lyrics panel opens.
        new("lrclib.net", LrcLib),

        // DiscordClient: Exo's own Discord client, only while signed in.
        new("discord.com", DiscordClient),
        new("gateway.discord.gg", DiscordClient),
        new("*.discord.gg", DiscordClient),
        new("remote-auth-gateway.discord.gg", DiscordClient),
        new("cdn.discordapp.com", DiscordClient),
        new("*.discordapp.net", DiscordClient), // Copy Image and Save Image from the media proxies (media., images-ext-1.)
        new("*.discord.media", DiscordClient),
        new("discord-attachments-uploads-prd.storage.googleapis.com", DiscordClient),

        // The Discord room's embedded players (Message.tsx): their pages load in the room, not through native code, but each is a request Exo's page makes.
        new("www.youtube.com", DiscordPlayers),
        new("www.youtube-nocookie.com", DiscordPlayers),
        new("player.twitch.tv", DiscordPlayers),
        new("clips.twitch.tv", DiscordPlayers),
        new("player.vimeo.com", DiscordPlayers),
        new("www.tiktok.com", DiscordPlayers),
        new("w.soundcloud.com", DiscordPlayers),
        new("open.spotify.com", DiscordPlayers),
        new("embed.music.apple.com", DiscordPlayers),

        // StripeCards: the Shop's card form (its own page, pay.exo-launcher.local); card numbers go only to Stripe.
        new("js.stripe.com", StripeCards),
        new("api.stripe.com", StripeCards),
        new("m.stripe.network", StripeCards),
        new("hooks.stripe.com", StripeCards),

        // AutoEqCatalog: AutoEq's recommended list (at most weekly) and the one model's ParametricEQ.txt the player picks.
        new("raw.githubusercontent.com", AutoEq),
        // EqualizerApo.InstallAsync: the pinned installer, only on Install; SourceForge redirects to one of its mirrors.
        new("downloads.sourceforge.net", EqualizerApo),
        new("*.sourceforge.net", EqualizerApo),
        new("fwupdater.dl.playstation.net", ControllerFirmware),
        // PcAbout: which Windows version is newest, for PC > Overview's About card.
        new("learn.microsoft.com", WindowsRelease),
        // BiosUpdate: the newest BIOS ASUS lists for an ASUS board or laptop, by its model.
        new("1.1.1.1", NetworkCheck),
        new("dns.msftncsi.com", NetworkCheck),
        new("www.asus.com", AsusBios),
        new("dlcdnets.asus.com", AsusBios),

        // DeviceImages: one product picture per device model, from the links in Assets/device-images.json (exact hosts only).
        new("gmedia.playstation.com", DevicePictures),
        new("cdn-dynmedia-1.microsoft.com", DevicePictures),
        new("assets.nintendo.com", DevicePictures),
        new("cdn.shopify.com", DevicePictures), // makers' own stores, and headphone and IEM stores' copies
        new("hyperx.com", DevicePictures),
        new("ca.sennheiser-hearing.com", DevicePictures),
        new("api.beyerdynamic.de", DevicePictures),
        new("images.ctfassets.net", DevicePictures), // SteelSeries' image server
        new("resource.logitechg.com", DevicePictures),
        new("cdn.prod.website-files.com", DevicePictures), // Truthear's site
        new("img.openmouse.app", DevicePictures),
        new("wooting-website.ams3.cdn.digitaloceanspaces.com", DevicePictures), // Wooting's site
        new("assets.sennheiser.com", DevicePictures), // Sennheiser's pro site
        new("www.audio-technica.com", DevicePictures),
        new("images.philips.com", DevicePictures),
        new("resource.logitech.com", DevicePictures),
        new("dlcdnwebimgs.asus.com", DevicePictures),
        new("www.asus.com", DevicePictures), // ASUS's own gallery files (TUF Gaming mice)
        new("a.storyblok.com", DevicePictures), // Cooler Master's site
        new("assets.corsair.com", DevicePictures),
        new("www.deluxworld.com", DevicePictures),
        new("us.maxgaming.com", DevicePictures), // makers' official shots it carries larger than the makers publish (IPI, G-Wolves, RAWM)
        new("pisces.bbystatic.com", DevicePictures), // Best Buy's copies of makers' official shots (Razer's own are 500 px)
        new("multimedia.bbycastatic.ca", DevicePictures),
        new("store.storeimages.cdn-apple.com", DevicePictures),
        new("static.wixstatic.com", DevicePictures), // Dunu's site
        new("intuaura.net", DevicePictures),
        new("massdrop-s3.imgix.net", DevicePictures), // Drop's
        new("assets.fractal-design.com", DevicePictures),
        new("sony.scene7.com", DevicePictures),
        new("tanchjim.com", DevicePictures),
        new("www.cherry.de", DevicePictures), // Cherry's own site
        new("motospeed.cc", DevicePictures), // Motospeed's own site
        new("www.vaxee.co", DevicePictures), // VAXEE's own site

        // DependencyService: installers you chose, and their download redirects; .NET's release list
        // (builds.dotnet.microsoft.com) when Game runtimes shows.
        new("aka.ms", Installers),
        new("*.microsoft.com", Installers),
        new("globalcdn.nuget.org", Installers),
        new("openal.org", Installers),
        new("us.download.nvidia.com", Installers),

        // Exo's relay: sealed messages between the Exo apps in one Discord call, and between friends' Exo apps (relay/README.md).
        new("exo-relay.exo-erix.workers.dev", ExoRelay),
    ];

    /// <summary>
    /// Rows whose requests Exo's own code does not make: the store tools and
    /// Exo Shaders are separate programs, and the Steam row says nothing is sent.
    /// </summary>
    internal static readonly string[] RowsWithoutNativeHosts = [StoreTools, Shaders, DiscordGifs];

    internal static bool Covers(string pattern, string host) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    internal static bool IsListed(string host) => Hosts.Any(destination => Covers(destination.Host, host));
}
