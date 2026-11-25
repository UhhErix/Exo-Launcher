use std::path::{Component, Path, PathBuf};

use exo_rpc::anticheat::AntiCheatClass;

pub const ALLOWED_STORE_HOSTS: &[&str] = &[
    "launcher-public-service-prod06.ol.epicgames.com",
    "account-public-service-prod.ol.epicgames.com",
    "library-service.live.use1a.on.epicgames.com",
    "content-download-egs.distro.on.epicgames.com",
    "epicgames-download1.akamaized.net",
    "*.epicgamescdn.com",
    "download.epicgames.com",
    "fastly-download.epicgames.com",
    "egdownload.fastly-edge.com",
    "www.epicgames.com",
    "*.epicgames.com",
    "api.legendary.gl",
    "api.gog.com",
    "auth.gog.com",
    "gameplay.gog.com",
    "gamesdb.gog.com",
    "galaxy-library.gog.com",
    "content-system.gog.com",
    "cdn.gog.com",
    "*.gog-statics.com",
    "api.amazon.com",
    "gaming.amazon.com",
    "*.cloudfront.net",
    "store.steampowered.com",
    "api.steampowered.com",
    "steamcommunity.com",
];

pub fn extract_url_host(url: &str) -> Option<String> {
    let rest = url
        .strip_prefix("https://")
        .or_else(|| url.strip_prefix("http://"))?;
    let authority = rest.split(['/', '?', '#']).next()?;
    if authority.is_empty() {
        return None;
    }
    let host_port = authority.rsplit('@').next()?;
    let host = if let Some(stripped) = host_port.strip_prefix('[') {
        stripped.split(']').next()?
    } else {
        host_port.split(':').next()?
    };
    let lower = host.trim().to_ascii_lowercase();
    (!lower.is_empty()).then_some(lower)
}

pub fn is_allowed_store_host(host: &str, allow_loopback: bool) -> bool {
    let lower = host.trim().to_ascii_lowercase();
    if lower.is_empty() {
        return false;
    }
    if allow_loopback && matches!(lower.as_str(), "127.0.0.1" | "localhost" | "::1") {
        return true;
    }
    ALLOWED_STORE_HOSTS.iter().any(|pattern| {
        if let Some(suffix) = pattern.strip_prefix("*.") {
            lower == suffix || lower.ends_with(&format!(".{suffix}"))
        } else {
            lower == *pattern
        }
    })
}

pub fn validate_store_url(url: &str, allow_loopback: bool) -> Result<String, String> {
    let is_https = url.starts_with("https://");
    let is_http = url.starts_with("http://");
    if !is_https && !(allow_loopback && is_http) {
        return Err(format!("Disallowed store URL scheme: {url}"));
    }
    let host = extract_url_host(url).ok_or_else(|| format!("Invalid store URL host: {url}"))?;
    if !is_allowed_store_host(&host, allow_loopback) {
        return Err(format!(
            "Host '{host}' is not in NetworkDestinations allowlist"
        ));
    }
    if is_http && !matches!(host.as_str(), "127.0.0.1" | "localhost" | "::1") {
        return Err(format!("Plain HTTP refused for non-loopback host '{host}'"));
    }
    Ok(host)
}

pub fn validate_manifest_rel_path(install_root: &Path, rel: &str) -> Result<PathBuf, String> {
    if rel.is_empty() {
        return Err("Manifest entry path is empty".into());
    }
    if rel.contains('\0') {
        return Err("Manifest entry path contains NUL byte".into());
    }
    if rel.starts_with('/') || rel.starts_with('\\') {
        return Err(format!("Manifest entry path must be relative: {rel}"));
    }
    if rel.contains(':') {
        return Err(format!(
            "Manifest entry path contains drive or ADS colon: {rel}"
        ));
    }

    let normalized = rel.replace('\\', "/");
    let mut joined = install_root.to_path_buf();
    let mut count = 0usize;
    for part in normalized.split('/') {
        if part.is_empty() || part == "." {
            return Err(format!(
                "Manifest entry contains invalid path segment: {rel}"
            ));
        }
        if part == ".." {
            return Err(format!(
                "Manifest entry escapes install root via '..': {rel}"
            ));
        }
        if part.ends_with(' ') || part.ends_with('.') {
            return Err(format!(
                "Manifest entry has unsafe trailing dot/space: {rel}"
            ));
        }
        let comp = Path::new(part);
        let mut comps = comp.components();
        match (comps.next(), comps.next()) {
            (Some(Component::Normal(_)), None) => {}
            _ => {
                return Err(format!("Manifest entry segment is not normal: {rel}"));
            }
        }
        joined.push(part);
        count += 1;

        if let Ok(meta) = std::fs::symlink_metadata(&joined)
            && meta.file_type().is_symlink()
        {
            return Err(format!("Manifest entry traverses symlink: {rel}"));
        }
    }
    if count == 0 {
        return Err("Manifest entry resolved to empty path".into());
    }

    if let (Ok(canon_root), Ok(canon_parent)) = (
        std::fs::canonicalize(install_root),
        joined
            .parent()
            .map(std::fs::canonicalize)
            .unwrap_or_else(|| Err(std::io::Error::from(std::io::ErrorKind::NotFound))),
    ) && !canon_parent.starts_with(&canon_root)
    {
        return Err(format!(
            "Manifest entry escapes canonical install root: {rel}"
        ));
    }

    Ok(joined)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StoreGamePolicy {
    pub raw_class: AntiCheatClass,
    pub effective_class: AntiCheatClass,
    pub allow_launch_wrappers: bool,
    pub allow_game_dir_mod_writes: bool,
    pub require_official_client_route: bool,
    pub pause_downloads_while_running: bool,
}

impl StoreGamePolicy {
    pub fn for_class(raw_class: AntiCheatClass) -> Self {
        let effective_class = raw_class.effective();
        let strict = matches!(
            effective_class,
            AntiCheatClass::CompetitivePvpOnline | AntiCheatClass::Unknown
        );
        Self {
            raw_class,
            effective_class,
            allow_launch_wrappers: !strict
                && matches!(effective_class, AntiCheatClass::OfflineAntiCheat),
            allow_game_dir_mod_writes: !raw_class.forbids_game_tree_set_rights()
                && matches!(effective_class, AntiCheatClass::OfflineAntiCheat),
            require_official_client_route: true,
            pause_downloads_while_running: strict
                || matches!(effective_class, AntiCheatClass::OnlineNonCompetitivePve),
        }
    }
}
