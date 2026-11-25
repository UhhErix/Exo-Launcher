use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Destination {
    pub host: &'static str,
    pub category: &'static str,
}

pub const HOSTS: &[Destination] = &[
    Destination {
        host: "updates.getexo.app",
        category: "updates",
    },
    Destination {
        host: "accounts.getexo.app",
        category: "account",
    },
    Destination {
        host: "reports.getexo.app",
        category: "reports",
    },
    Destination {
        host: "library-service.live.use1a.on.epicgames.com",
        category: "epic",
    },
    Destination {
        host: "account-public-service-prod.ol.epicgames.com",
        category: "epic",
    },
    Destination {
        host: "www.epicgames.com",
        category: "epic",
    },
    Destination {
        host: "launcher-public-service-prod06.ol.epicgames.com",
        category: "epic-downloads",
    },
    Destination {
        host: "content-download-egs.distro.on.epicgames.com",
        category: "epic-downloads",
    },
    Destination {
        host: "epicgames-download1.akamaized.net",
        category: "epic-downloads",
    },
    Destination {
        host: "*.epicgamescdn.com",
        category: "epic-downloads",
    },
    Destination {
        host: "download.epicgames.com",
        category: "epic-downloads",
    },
    Destination {
        host: "fastly-download.epicgames.com",
        category: "epic-downloads",
    },
    Destination {
        host: "egdownload.fastly-edge.com",
        category: "epic-downloads",
    },
    Destination {
        host: "api.legendary.gl",
        category: "legendary",
    },
    Destination {
        host: "gameplay.gog.com",
        category: "gog",
    },
    Destination {
        host: "api.gog.com",
        category: "gog",
    },
    Destination {
        host: "gamesdb.gog.com",
        category: "gog",
    },
    Destination {
        host: "galaxy-library.gog.com",
        category: "gog",
    },
    Destination {
        host: "api.tracker.gg",
        category: "tracker-gg",
    },
    Destination {
        host: "riot.getexo.app",
        category: "riot-hours",
    },
    Destination {
        host: "*.a.pvp.net",
        category: "riot-hours",
    },
    Destination {
        host: "valorant-api.com",
        category: "riot-hours",
    },
    Destination {
        host: "www.pcgamingwiki.com",
        category: "pcgamingwiki",
    },
    Destination {
        host: "*.steamstatic.com",
        category: "cover-art",
    },
    Destination {
        host: "steamcdn-a.akamaihd.net",
        category: "cover-art",
    },
    Destination {
        host: "store.steampowered.com",
        category: "steam",
    },
    Destination {
        host: "steamcommunity.com",
        category: "steam",
    },
    Destination {
        host: "api.steampowered.com",
        category: "steam",
    },
    Destination {
        host: "*.epicgames.com",
        category: "cover-art",
    },
    Destination {
        host: "*.unrealengine.com",
        category: "cover-art",
    },
    Destination {
        host: "covers.getexo.app",
        category: "cover-cache",
    },
    Destination {
        host: "images.gog.com",
        category: "cover-art",
    },
    Destination {
        host: "*.gog-statics.com",
        category: "cover-art",
    },
    Destination {
        host: "displaycatalog.mp.microsoft.com",
        category: "cover-art",
    },
    Destination {
        host: "store-images.s-microsoft.com",
        category: "cover-art",
    },
    Destination {
        host: "images-eds-ssl.xboxlive.com",
        category: "cover-art",
    },
    Destination {
        host: "*.riotgames.com",
        category: "cover-art",
    },
    Destination {
        host: "*.riotcdn.net",
        category: "cover-art",
    },
    Destination {
        host: "*.playvalorant.com",
        category: "cover-art",
    },
    Destination {
        host: "*.leagueoflegends.com",
        category: "cover-art",
    },
    Destination {
        host: "*.ubisoft.com",
        category: "cover-art",
    },
    Destination {
        host: "*.ubi.com",
        category: "cover-art",
    },
    Destination {
        host: "*.ea.com",
        category: "cover-art",
    },
    Destination {
        host: "*.origin.com",
        category: "cover-art",
    },
    Destination {
        host: "*.blizzard.com",
        category: "cover-art",
    },
    Destination {
        host: "*.battle.net",
        category: "cover-art",
    },
    Destination {
        host: "api.github.com",
        category: "upscalers",
    },
    Destination {
        host: "raw.githubusercontent.com",
        category: "upscalers",
    },
    Destination {
        host: "dlss-swapper-downloads.beeradmoore.com",
        category: "upscalers",
    },
    Destination {
        host: "www.nvidia.com",
        category: "nvidia-drivers",
    },
    Destination {
        host: "gfwsl.geforce.com",
        category: "nvidia-drivers",
    },
    Destination {
        host: "us.download.nvidia.com",
        category: "nvidia-drivers",
    },
    Destination {
        host: "api-forums.nvidia.com",
        category: "nvidia-drivers",
    },
    Destination {
        host: "international.download.nvidia.com",
        category: "nvidia-drivers",
    },
    Destination {
        host: "www.amd.com",
        category: "amd-drivers",
    },
    Destination {
        host: "drivers.amd.com",
        category: "amd-drivers",
    },
    Destination {
        host: "www.intel.com",
        category: "intel-drivers",
    },
    Destination {
        host: "downloadmirror.intel.com",
        category: "intel-drivers",
    },
    Destination {
        host: "www.realtek.com",
        category: "realtek-drivers",
    },
    Destination {
        host: "github.com",
        category: "frame-gen",
    },
    Destination {
        host: "release-assets.githubusercontent.com",
        category: "frame-gen",
    },
    Destination {
        host: "objects.githubusercontent.com",
        category: "frame-gen",
    },
    Destination {
        host: "www.cheapshark.com",
        category: "store",
    },
    Destination {
        host: "*.googleusercontent.com",
        category: "music-art",
    },
    Destination {
        host: "*.ggpht.com",
        category: "music-art",
    },
    Destination {
        host: "*.ytimg.com",
        category: "music-art",
    },
    Destination {
        host: "*.scdn.co",
        category: "music-art",
    },
    Destination {
        host: "*.spotifycdn.com",
        category: "music-art",
    },
    Destination {
        host: "open.spotify.com",
        category: "music-art",
    },
    Destination {
        host: "*.mzstatic.com",
        category: "music-art",
    },
    Destination {
        host: "spotify.link",
        category: "spotify-links",
    },
    Destination {
        host: "spotify.app.link",
        category: "spotify-links",
    },
    Destination {
        host: "spotify-alternate.app.link",
        category: "spotify-links",
    },
    Destination {
        host: "lrclib.net",
        category: "lyrics",
    },
    Destination {
        host: "discord.com",
        category: "discord",
    },
    Destination {
        host: "gateway.discord.gg",
        category: "discord",
    },
    Destination {
        host: "*.discord.gg",
        category: "discord",
    },
    Destination {
        host: "remote-auth-gateway.discord.gg",
        category: "discord",
    },
    Destination {
        host: "cdn.discordapp.com",
        category: "discord",
    },
    Destination {
        host: "*.discordapp.net",
        category: "discord",
    },
    Destination {
        host: "*.discord.media",
        category: "discord",
    },
    Destination {
        host: "discord-attachments-uploads-prd.storage.googleapis.com",
        category: "discord",
    },
    Destination {
        host: "www.youtube.com",
        category: "discord-players",
    },
    Destination {
        host: "www.youtube-nocookie.com",
        category: "discord-players",
    },
    Destination {
        host: "player.twitch.tv",
        category: "discord-players",
    },
    Destination {
        host: "clips.twitch.tv",
        category: "discord-players",
    },
    Destination {
        host: "player.vimeo.com",
        category: "discord-players",
    },
    Destination {
        host: "www.tiktok.com",
        category: "discord-players",
    },
    Destination {
        host: "w.soundcloud.com",
        category: "discord-players",
    },
    Destination {
        host: "embed.music.apple.com",
        category: "discord-players",
    },
    Destination {
        host: "js.stripe.com",
        category: "stripe",
    },
    Destination {
        host: "api.stripe.com",
        category: "stripe",
    },
    Destination {
        host: "m.stripe.network",
        category: "stripe",
    },
    Destination {
        host: "hooks.stripe.com",
        category: "stripe",
    },
    Destination {
        host: "downloads.sourceforge.net",
        category: "equalizer-apo",
    },
    Destination {
        host: "*.sourceforge.net",
        category: "equalizer-apo",
    },
    Destination {
        host: "fwupdater.dl.playstation.net",
        category: "controller-fw",
    },
    Destination {
        host: "learn.microsoft.com",
        category: "windows-release",
    },
    Destination {
        host: "1.1.1.1",
        category: "network-check",
    },
    Destination {
        host: "dns.msftncsi.com",
        category: "network-check",
    },
    Destination {
        host: "www.asus.com",
        category: "asus-bios",
    },
    Destination {
        host: "dlcdnets.asus.com",
        category: "asus-bios",
    },
    Destination {
        host: "api.modrinth.com",
        category: "modrinth",
    },
    Destination {
        host: "cdn.modrinth.com",
        category: "modrinth",
    },
    Destination {
        host: "gmedia.playstation.com",
        category: "device-pictures",
    },
    Destination {
        host: "cdn-dynmedia-1.microsoft.com",
        category: "device-pictures",
    },
    Destination {
        host: "assets.nintendo.com",
        category: "device-pictures",
    },
    Destination {
        host: "cdn.shopify.com",
        category: "device-pictures",
    },
    Destination {
        host: "hyperx.com",
        category: "device-pictures",
    },
    Destination {
        host: "ca.sennheiser-hearing.com",
        category: "device-pictures",
    },
    Destination {
        host: "api.beyerdynamic.de",
        category: "device-pictures",
    },
    Destination {
        host: "images.ctfassets.net",
        category: "device-pictures",
    },
    Destination {
        host: "resource.logitechg.com",
        category: "device-pictures",
    },
    Destination {
        host: "cdn.prod.website-files.com",
        category: "device-pictures",
    },
    Destination {
        host: "img.openmouse.app",
        category: "device-pictures",
    },
    Destination {
        host: "wooting-website.ams3.cdn.digitaloceanspaces.com",
        category: "device-pictures",
    },
    Destination {
        host: "assets.sennheiser.com",
        category: "device-pictures",
    },
    Destination {
        host: "www.audio-technica.com",
        category: "device-pictures",
    },
    Destination {
        host: "images.philips.com",
        category: "device-pictures",
    },
    Destination {
        host: "resource.logitech.com",
        category: "device-pictures",
    },
    Destination {
        host: "dlcdnwebimgs.asus.com",
        category: "device-pictures",
    },
    Destination {
        host: "a.storyblok.com",
        category: "device-pictures",
    },
    Destination {
        host: "assets.corsair.com",
        category: "device-pictures",
    },
    Destination {
        host: "www.deluxworld.com",
        category: "device-pictures",
    },
    Destination {
        host: "us.maxgaming.com",
        category: "device-pictures",
    },
    Destination {
        host: "pisces.bbystatic.com",
        category: "device-pictures",
    },
    Destination {
        host: "multimedia.bbycastatic.ca",
        category: "device-pictures",
    },
    Destination {
        host: "store.storeimages.cdn-apple.com",
        category: "device-pictures",
    },
    Destination {
        host: "static.wixstatic.com",
        category: "device-pictures",
    },
    Destination {
        host: "intuaura.net",
        category: "device-pictures",
    },
    Destination {
        host: "massdrop-s3.imgix.net",
        category: "device-pictures",
    },
    Destination {
        host: "assets.fractal-design.com",
        category: "device-pictures",
    },
    Destination {
        host: "sony.scene7.com",
        category: "device-pictures",
    },
    Destination {
        host: "tanchjim.com",
        category: "device-pictures",
    },
    Destination {
        host: "www.cherry.de",
        category: "device-pictures",
    },
    Destination {
        host: "motospeed.cc",
        category: "device-pictures",
    },
    Destination {
        host: "www.vaxee.co",
        category: "device-pictures",
    },
    Destination {
        host: "aka.ms",
        category: "installers",
    },
    Destination {
        host: "*.microsoft.com",
        category: "installers",
    },
    Destination {
        host: "globalcdn.nuget.org",
        category: "installers",
    },
    Destination {
        host: "openal.org",
        category: "installers",
    },
    Destination {
        host: "exo-relay.exo-erix.workers.dev",
        category: "relay",
    },
];

pub fn covers(pattern: &str, host: &str) -> bool {
    let host = host.trim().trim_end_matches('.');
    if host.is_empty() {
        return false;
    }
    if let Some(base) = pattern.strip_prefix("*.") {
        if host.eq_ignore_ascii_case(base) {
            return true;
        }
        let suffix = &pattern[1..];
        host.len() > suffix.len() && host[host.len() - suffix.len()..].eq_ignore_ascii_case(suffix)
    } else {
        host.eq_ignore_ascii_case(pattern)
    }
}

pub fn is_listed(host: &str) -> bool {
    HOSTS.iter().any(|dest| covers(dest.host, host))
}

pub fn categories_for(host: &str) -> Vec<&'static str> {
    let mut out = Vec::new();
    for dest in HOSTS {
        if covers(dest.host, host) && !out.contains(&dest.category) {
            out.push(dest.category);
        }
    }
    out
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UrlVerdict {
    pub allowed: bool,
    pub host: Option<String>,
    pub reason: Option<String>,
}

pub fn check_url(url: &str) -> UrlVerdict {
    let trimmed = url.trim();
    let Some((scheme, rest)) = trimmed.split_once("://") else {
        return UrlVerdict {
            allowed: false,
            host: None,
            reason: Some("missing scheme".into()),
        };
    };
    if !scheme.eq_ignore_ascii_case("https") && !scheme.eq_ignore_ascii_case("wss") {
        return UrlVerdict {
            allowed: false,
            host: None,
            reason: Some(format!("disallowed scheme: {scheme}")),
        };
    }
    let authority = rest.split(['/', '?', '#']).next().unwrap_or("");
    if authority.contains('@') {
        return UrlVerdict {
            allowed: false,
            host: None,
            reason: Some("credentials in URL are refused".into()),
        };
    }
    let host = authority.split(':').next().unwrap_or("").trim();
    if host.is_empty() {
        return UrlVerdict {
            allowed: false,
            host: None,
            reason: Some("missing host".into()),
        };
    }
    if is_listed(host) {
        UrlVerdict {
            allowed: true,
            host: Some(host.to_ascii_lowercase()),
            reason: None,
        }
    } else {
        UrlVerdict {
            allowed: false,
            host: Some(host.to_ascii_lowercase()),
            reason: Some(format!("host {host} is not in NetworkDestinations")),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exact_and_wildcard_hosts_match_and_spoofed_suffixes_fail() {
        assert!(is_listed("updates.getexo.app"));
        assert!(is_listed("UPDATES.GETEXO.APP"));
        assert!(is_listed("shared.fastly.steamstatic.com"));
        assert!(is_listed("steamstatic.com"));
        assert!(is_listed("pd.na.a.pvp.net"));
        assert!(is_listed("exo-relay.exo-erix.workers.dev"));

        assert!(!is_listed("evilsteamstatic.com"));
        assert!(!is_listed("updates.getexo.app.evil.com"));
        assert!(!is_listed("example.com"));
        assert!(!is_listed(""));
    }

    #[test]
    fn check_url_enforces_https_or_wss_no_userinfo_and_allowlist() {
        assert!(check_url("https://updates.getexo.app/v1/release.json").allowed);
        assert!(check_url("wss://gateway.discord.gg/?v=10").allowed);
        assert!(!check_url("http://updates.getexo.app/v1/release.json").allowed);
        assert!(!check_url("https://user@updates.getexo.app/").allowed);
        assert!(!check_url("https://evil.example.net/payload").allowed);
    }
}
