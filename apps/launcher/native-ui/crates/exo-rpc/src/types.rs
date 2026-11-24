use serde::{Deserialize, Deserializer, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

pub(crate) fn null_as_default<'de, D, T>(deserializer: D) -> Result<T, D::Error>
where
    D: Deserializer<'de>,
    T: Default + Deserialize<'de>,
{
    Ok(Option::<T>::deserialize(deserializer)?.unwrap_or_default())
}

#[derive(Debug, Clone, Deserialize)]
pub struct HostContract {
    pub version: u32,
    pub methods: BTreeMap<String, String>,
    pub events: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GameInclude {
    #[serde(default)]
    pub app_id: Option<String>,
    #[serde(default)]
    pub title: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GameVariant {
    #[serde(default, deserialize_with = "null_as_default")]
    pub id: String,
    #[serde(default, deserialize_with = "null_as_default")]
    pub store: String,
    #[serde(default)]
    pub installed: bool,
    #[serde(default)]
    pub owned: bool,
    #[serde(default)]
    pub entitlement_state: Option<String>,
    #[serde(default)]
    pub update_available: bool,
    #[serde(default)]
    pub can_install: bool,
    #[serde(default)]
    pub primary_action: Option<String>,
    #[serde(default)]
    pub launch_target: Option<String>,
    #[serde(default)]
    pub playtime_minutes: Option<u64>,
    #[serde(default)]
    pub last_played_utc: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub status: String,
    #[serde(default)]
    pub is_running: bool,
    #[serde(default)]
    pub can_stop: bool,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Game {
    pub id: String,
    pub title: String,
    #[serde(default, deserialize_with = "null_as_default")]
    pub store: String,
    #[serde(default, deserialize_with = "null_as_default")]
    pub stores: Vec<String>,
    #[serde(default)]
    pub installed: bool,
    #[serde(default)]
    pub owned: bool,
    #[serde(default)]
    pub entitlement_state: Option<String>,
    #[serde(default)]
    pub update_available: bool,
    #[serde(default)]
    pub can_install: bool,
    #[serde(default)]
    pub primary_action: Option<String>,
    #[serde(default)]
    pub canonical_title_key: Option<String>,
    #[serde(default)]
    pub selected_variant_id: Option<String>,
    #[serde(default)]
    pub buy_url: Option<String>,
    #[serde(default)]
    pub cover_url: Option<String>,
    #[serde(default)]
    pub cover_source: Option<String>,
    #[serde(default)]
    pub hero_url: Option<String>,
    #[serde(default)]
    pub launch_target: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub includes: Vec<GameInclude>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub variants: Vec<GameVariant>,
    #[serde(default)]
    pub art_revision: Option<u64>,
    #[serde(default)]
    pub playtime_minutes: u64,
    #[serde(default)]
    pub size_bytes: Option<u64>,
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default)]
    pub launch_note: Option<String>,
    #[serde(default)]
    pub family_shared: bool,
    #[serde(default)]
    pub shared_by: Option<String>,
    #[serde(default)]
    pub last_played_utc: Option<String>,
    #[serde(default)]
    pub is_running: bool,
    #[serde(default)]
    pub can_stop: bool,
    #[serde(default)]
    pub is_add_portable: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub tags: Vec<String>,
}

fn steam_app_id_from_text(value: &str) -> Option<String> {
    if let Some(rest) = value
        .strip_prefix("steam:")
        .or_else(|| value.strip_prefix("STEAM:"))
    {
        let digits: String = rest.chars().take_while(|c| c.is_ascii_digit()).collect();
        if !digits.is_empty() {
            return Some(digits);
        }
    }
    if let Some(idx) = value.find("/apps/") {
        let digits: String = value[idx + 6..]
            .chars()
            .take_while(|c| c.is_ascii_digit())
            .collect();
        if !digits.is_empty() {
            return Some(digits);
        }
    }
    for prefix in ["steam://rungameid/", "steam://launch/", "steam://install/"] {
        if let Some(idx) = value.to_ascii_lowercase().find(prefix) {
            let digits: String = value[idx + prefix.len()..]
                .chars()
                .take_while(|c| c.is_ascii_digit())
                .collect();
            if !digits.is_empty() {
                return Some(digits);
            }
        }
    }
    None
}

pub fn sanitize_cache_id(game_id: &str) -> String {
    game_id
        .chars()
        .map(|c| if c.is_alphanumeric() { c } else { '_' })
        .collect()
}

impl Game {
    pub fn art_steam_app_id(&self) -> Option<String> {
        if let Some(id) = self.includes.first().and_then(|inc| inc.app_id.as_deref())
            && !id.is_empty()
        {
            return Some(id.to_string());
        }
        if let Some(id) = steam_app_id_from_text(&self.id) {
            return Some(id);
        }
        if let Some(url) = self.cover_url.as_deref()
            && let Some(id) = steam_app_id_from_text(url)
        {
            return Some(id);
        }
        if let Some(target) = self.launch_target.as_deref()
            && let Some(id) = steam_app_id_from_text(target)
        {
            return Some(id);
        }
        for v in &self.variants {
            if let Some(id) = steam_app_id_from_text(&v.id) {
                return Some(id);
            }
            if let Some(target) = v.launch_target.as_deref()
                && let Some(id) = steam_app_id_from_text(target)
            {
                return Some(id);
            }
        }
        None
    }

    pub fn hero_cache_target(&self) -> String {
        if let Some(id) = self.includes.first().and_then(|inc| inc.app_id.as_deref())
            && !id.is_empty()
        {
            return format!("steam:{id}");
        }
        self.id.clone()
    }

    pub fn steam_hero_url(&self) -> Option<String> {
        let app_id = self.art_steam_app_id()?;
        Some(format!(
            "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{app_id}/library_hero.jpg"
        ))
    }

    pub fn steam_logo_url(&self) -> Option<String> {
        let app_id = self.art_steam_app_id()?;
        Some(format!(
            "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{app_id}/logo.png"
        ))
    }

    pub fn steam_portrait_url(&self) -> Option<String> {
        let app_id = self.art_steam_app_id()?;
        Some(format!(
            "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{app_id}/library_600x900.jpg"
        ))
    }

    pub fn portrait_art_candidates(&self) -> Vec<ArtCandidate> {
        let app_id = self.art_steam_app_id();
        let steam_cache_urls: Vec<String> = match app_id.as_deref() {
            Some(id) => [
                format!("{COVER_CACHE_ORIGIN}/{id}_2x.jpg"),
                format!("{COVER_CACHE_ORIGIN}/{id}.jpg"),
                format!("{COVER_CACHE_ORIGIN}/steam_{id}.jpg"),
            ]
            .into_iter()
            .map(|u| with_art_revision(&u, self.art_revision))
            .collect(),
            None => Vec::new(),
        };
        build_portrait_art_candidates(
            self.cover_url.as_deref(),
            self.art_revision,
            app_id.as_deref(),
            &steam_cache_urls,
        )
    }

    pub fn is_preloaded(&self) -> bool {
        self.status
            .as_deref()
            .is_some_and(|s| s.eq_ignore_ascii_case("preloaded"))
    }
}

pub const COVER_CACHE_ORIGIN: &str = "https://covers.exo-launcher.local";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ArtCandidate {
    pub url: String,
    pub derived: bool,
}

pub fn with_art_revision(url: &str, art_revision: Option<u64>) -> String {
    match art_revision {
        Some(rev) if rev > 0 && url.starts_with(&format!("{COVER_CACHE_ORIGIN}/")) => {
            let sep = if url.contains('?') { '&' } else { '?' };
            format!("{url}{sep}rev={rev}")
        }
        _ => url.to_string(),
    }
}

const SAFE_COVER_HOSTS: &[&str] = &[
    "gog-statics.com",
    "ddragon.leagueoflegends.com",
    "riotgames.com",
    "playvalorant.com",
    "leagueoflegends.com",
    "store-images.s-microsoft.com",
    "images-eds-ssl.xboxlive.com",
    "ubisoft.com",
    "ubi.com",
    "ea.com",
    "origin.com",
    "blizzard.com",
    "battle.net",
    "cdn.akamai.steamstatic.com",
];

fn https_authority(url: &str) -> Option<(&str, Option<&str>)> {
    let rest = url.strip_prefix("https://")?;
    let authority = rest.split(['/', '?', '#']).next()?;
    if authority.is_empty() || authority.contains('@') {
        return None;
    }
    if let Some((host, port)) = authority.rsplit_once(':') {
        if host.is_empty() || !port.chars().all(|c| c.is_ascii_digit()) {
            return None;
        }
        Some((host, Some(port)))
    } else {
        Some((authority, None))
    }
}

fn https_host_is(url: &str, host: &str) -> bool {
    let Some((hostname, port)) = https_authority(url) else {
        return false;
    };
    if !matches!(port, None | Some("") | Some("443")) {
        return false;
    }
    let lower = hostname.to_ascii_lowercase();
    lower == host || lower.ends_with(&format!(".{host}"))
}

fn is_official_epic_portrait_cdn(url: &str) -> bool {
    let Some((hostname, _)) = https_authority(url) else {
        return false;
    };
    let lower = hostname.to_ascii_lowercase();
    lower == "cdn1.epicgames.com" || lower == "cdn2.unrealengine.com"
}

fn is_official_steam_portrait_cdn(url: &str) -> bool {
    let host_ok =
        https_host_is(url, "steamstatic.com") || https_host_is(url, "steamcdn-a.akamaihd.net");
    if !host_ok {
        return false;
    }
    if url.contains("library_hero")
        || url.contains("header.jpg")
        || url.contains("capsule_231")
        || url.contains("capsule_184")
        || url.contains("capsule_616")
        || url.contains("capsule_sm")
    {
        return false;
    }
    url.contains("library_600x900")
        || url.contains("library_capsule")
        || url.contains("portrait.png")
        || url.contains("library_600x900_schinese")
}

fn is_official_steam_hero_cdn(url: &str) -> bool {
    let host_ok =
        https_host_is(url, "steamstatic.com") || https_host_is(url, "steamcdn-a.akamaihd.net");
    host_ok
        && (url.contains("library_hero")
            || url.contains("page_bg_raw")
            || url.contains("page_bg_generated"))
}

fn is_official_steam_header_cdn(url: &str) -> bool {
    let host_ok =
        https_host_is(url, "steamstatic.com") || https_host_is(url, "steamcdn-a.akamaihd.net");
    host_ok
        && (url.contains("/header.jpg")
            || url.contains("/header_2x.jpg")
            || url.contains("capsule_616x353"))
}

pub fn is_safe_cover_url(url: &str, allow_hero: bool) -> bool {
    if url.is_empty() {
        return false;
    }
    if url.starts_with("data:image/") || url.starts_with(&format!("{COVER_CACHE_ORIGIN}/")) {
        return true;
    }
    if is_official_steam_portrait_cdn(url) || is_official_epic_portrait_cdn(url) {
        return true;
    }
    if allow_hero && (is_official_steam_hero_cdn(url) || is_official_steam_header_cdn(url)) {
        return true;
    }
    SAFE_COVER_HOSTS.iter().any(|host| https_host_is(url, host))
}

fn has_prefixed_token_suffix(lower: &str, token: &str, suffixes: &[u8]) -> bool {
    let bytes = lower.as_bytes();
    let tbytes = token.as_bytes();
    if bytes.len() < tbytes.len() + 1 {
        return false;
    }
    for i in 0..=bytes.len() - tbytes.len() {
        if &bytes[i..i + tbytes.len()] == tbytes {
            let start_ok = i == 0 || matches!(bytes[i - 1], b'/' | b'_');
            let end = i + tbytes.len();
            let end_ok = end < bytes.len() && suffixes.contains(&bytes[end]);
            if start_ok && end_ok {
                return true;
            }
        }
    }
    false
}

fn has_header_segment(lower: &str) -> bool {
    let bytes = lower.as_bytes();
    let token = b"header";
    if bytes.len() < token.len() {
        return false;
    }
    for i in 0..=bytes.len() - token.len() {
        if &bytes[i..i + token.len()] == token {
            let start_ok = i == 0 || matches!(bytes[i - 1], b'/' | b'_');
            if !start_ok {
                continue;
            }
            let end = i + token.len();
            if end == bytes.len() {
                return true;
            }
            if bytes[end] == b'.' && end + 1 < bytes.len() && bytes[end + 1].is_ascii_alphanumeric()
            {
                return true;
            }
        }
    }
    false
}

fn has_product_tile_256(url: &str) -> bool {
    let bytes = url.as_bytes();
    let needle = b"product_tile_256";
    if bytes.len() < needle.len() {
        return false;
    }
    for i in 0..=bytes.len() - needle.len() {
        if &bytes[i..i + needle.len()] == needle {
            let next = i + needle.len();
            if next == bytes.len() || bytes[next] != b'0' {
                return true;
            }
        }
    }
    false
}

pub fn is_rejected_cover_url(url: &str, allow_hero: bool) -> bool {
    let lower = url.to_ascii_lowercase();
    let has_hero = has_prefixed_token_suffix(&lower, "hero", b"_./-");
    if allow_hero && has_hero {
        return false;
    }
    if allow_hero && is_official_steam_header_cdn(url) {
        return false;
    }
    has_hero
        || has_prefixed_token_suffix(&lower, "banner", b"_./-")
        || has_header_segment(&lower)
        || url.contains("capsule_231")
        || url.contains("capsule_184")
        || url.contains("capsule_sm")
        || url.contains("capsule_616")
        || url.contains("library_hero")
        || url.contains("product_card")
        || (url.contains("product_tile") && !has_product_tile_256(url))
}

fn push_unique_portrait(list: &mut Vec<ArtCandidate>, url: Option<&str>) {
    let Some(url) = url else {
        return;
    };
    if url.is_empty() || list.iter().any(|c| c.url == url) {
        return;
    }
    if !is_safe_cover_url(url, false) || is_rejected_cover_url(url, false) {
        return;
    }
    list.push(ArtCandidate {
        url: url.to_string(),
        derived: false,
    });
}

pub fn build_portrait_art_candidates(
    cover_url: Option<&str>,
    art_revision: Option<u64>,
    app_id: Option<&str>,
    steam_cache_urls: &[String],
) -> Vec<ArtCandidate> {
    let mut list = Vec::new();
    let raw = cover_url
        .filter(|u| !u.is_empty())
        .map(|u| with_art_revision(u, art_revision));
    let raw_is_hi_res = raw
        .as_deref()
        .is_some_and(|r| r.contains("custom_") || r.contains("_2x"));
    if raw_is_hi_res && let Some(ref raw_url) = raw {
        push_unique_portrait(&mut list, Some(raw_url.as_str()));
        if raw_url.contains("library_600x900_2x") {
            let fallback = raw_url.replace("library_600x900_2x", "library_600x900");
            push_unique_portrait(&mut list, Some(fallback.as_str()));
        }
    }
    if let Some(id) = app_id {
        let fastly =
            format!("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{id}");
        let base = format!("https://cdn.cloudflare.steamstatic.com/steam/apps/{id}");
        for url in steam_cache_urls {
            if url.contains("_2x") {
                push_unique_portrait(&mut list, Some(url.as_str()));
            }
        }
        if !raw_is_hi_res {
            let fastly_2x = format!("{fastly}/library_600x900_2x.jpg");
            let base_2x = format!("{base}/library_600x900_2x.jpg");
            push_unique_portrait(&mut list, Some(fastly_2x.as_str()));
            push_unique_portrait(&mut list, Some(base_2x.as_str()));
            push_unique_portrait(&mut list, raw.as_deref());
        }
        for url in steam_cache_urls {
            push_unique_portrait(&mut list, Some(url.as_str()));
        }
        for url in [
            format!("{fastly}/library_600x900_2x.jpg"),
            format!("{fastly}/library_600x900.jpg"),
            format!("{fastly}/library_capsule.jpg"),
            format!("{base}/library_600x900_2x.jpg"),
            format!("{base}/library_600x900.jpg"),
            format!("{base}/library_capsule.jpg"),
        ] {
            push_unique_portrait(&mut list, Some(url.as_str()));
        }
        for store_url in [
            format!("{fastly}/header.jpg"),
            format!("{base}/header.jpg"),
            format!("{base}/capsule_616x353.jpg"),
        ] {
            if !list.iter().any(|c| c.url == store_url) {
                list.push(ArtCandidate {
                    url: store_url,
                    derived: true,
                });
            }
        }
    } else if !raw_is_hi_res {
        push_unique_portrait(&mut list, raw.as_deref());
    }
    list
}

impl Game {
    pub fn hours_label(&self) -> String {
        if self.playtime_minutes == 0 {
            return String::new();
        }
        if self.playtime_minutes < 60 {
            return format!("{}m played", self.playtime_minutes);
        }
        let hours = self.playtime_minutes as f64 / 60.0;
        if hours >= 10.0 {
            format!("{}h played", hours.round() as u64)
        } else {
            format!("{hours:.1}h played")
        }
    }

    pub fn size_label(&self) -> Option<String> {
        let bytes = self.size_bytes?;
        if bytes == 0 {
            return None;
        }
        const GB: f64 = 1024.0 * 1024.0 * 1024.0;
        const MB: f64 = 1024.0 * 1024.0;
        if (bytes as f64) >= GB {
            Some(format!("{:.1} GB", bytes as f64 / GB))
        } else {
            Some(format!("{} MB", (bytes as f64 / MB).round() as u64))
        }
    }

    pub fn store_display(&self) -> &'static str {
        store_label(&self.store)
    }
}

pub fn store_label(store: &str) -> &'static str {
    match store.to_ascii_lowercase().as_str() {
        "steam" => "Steam",
        "epic" | "epic games store" => "Epic",
        "gog" => "GOG",
        "riot" => "Riot",
        "xbox" => "Xbox",
        "ea" => "EA",
        "ubisoft" | "ubisoft store" => "Ubisoft",
        "battlenet" => "Battle.net",
        "amazon" => "Amazon",
        "rockstar" => "Rockstar",
        "itch" => "itch.io",
        "minecraft" => "Minecraft",
        "roblox" => "Roblox",
        "local" => "Local",
        _ => "Store",
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallProgress {
    #[serde(default)]
    pub game_id: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub phase: String,
    #[serde(default, deserialize_with = "null_as_default")]
    pub percent: f64,
    #[serde(default, deserialize_with = "null_as_default")]
    pub bytes_downloaded: u64,
    #[serde(default, deserialize_with = "null_as_default")]
    pub bytes_to_download: u64,
    #[serde(default, deserialize_with = "null_as_default")]
    pub bytes_per_second: u64,
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub can_cancel: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub is_active: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryPayload {
    #[serde(default)]
    pub games: Vec<Game>,
    #[serde(default)]
    pub count: usize,
    #[serde(default)]
    pub sort_mode: Option<String>,
    #[serde(default)]
    pub recent: Vec<String>,
    #[serde(default)]
    pub progress: Option<InstallProgress>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameGetReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub game: Option<Game>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameExtras {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub achievements_unlocked: Option<u32>,
    #[serde(default)]
    pub achievements_total: Option<u32>,
    #[serde(default)]
    pub drive_heat: Option<crate::game_types::DriveHeat>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FrameGenRoute {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub tier: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub summary: String,
    #[serde(default)]
    pub gpu_supported: bool,
    #[serde(default)]
    pub gpu_note: String,
    #[serde(default)]
    pub anti_cheat_verdict: String,
    #[serde(default)]
    pub anti_cheat_note: String,
    #[serde(default)]
    pub auto_enable: bool,
    #[serde(default)]
    pub latency_note: String,
    #[serde(default)]
    pub selectable: bool,
    #[serde(default)]
    pub active: bool,
    #[serde(default)]
    pub reason: Option<String>,
    #[serde(default)]
    pub how_to_enable: Option<String>,
    #[serde(default)]
    pub choice_value: Option<String>,
    #[serde(default)]
    pub choices: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameFrameGen {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub best_route_id: String,
    #[serde(default)]
    pub selected_route_id: String,
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub word: String,
    #[serde(default)]
    pub good: bool,
    #[serde(default)]
    pub bad: bool,
    #[serde(default)]
    pub reason: Option<String>,
    #[serde(default)]
    pub how_to_enable: Option<String>,
    #[serde(default)]
    pub routes: Vec<FrameGenRoute>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameProperties {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub store: String,
    #[serde(default)]
    pub launch_options: Option<String>,
    #[serde(default)]
    pub launch_options_note: Option<String>,
    #[serde(default)]
    pub size_bytes: Option<u64>,
    #[serde(default)]
    pub verify_label: Option<String>,
    #[serde(default)]
    pub can_browse: bool,
    #[serde(default)]
    pub running: bool,
    #[serde(default)]
    pub steam_overlay: Option<bool>,
    #[serde(default)]
    pub steam_overlay_note: Option<String>,
    #[serde(default)]
    pub through_steam: Option<bool>,
    #[serde(default)]
    pub store_properties: Option<String>,
    #[serde(default)]
    pub cloud_sync: Option<bool>,
    #[serde(default)]
    pub language: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub language_choices: Vec<crate::game_types::PropertyChoice>,
    #[serde(default)]
    pub beta: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub beta_choices: Vec<crate::game_types::PropertyChoice>,
    #[serde(default)]
    pub beta_code: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub content_label: String,
    #[serde(default)]
    pub content: Option<Vec<crate::game_types::GameContent>>,
    #[serde(default)]
    pub folder: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub folder_choices: Vec<crate::game_types::PropertyChoice>,
    #[serde(default)]
    pub moving: Option<crate::game_types::MovingProgress>,
    #[serde(default)]
    pub shader_cache_bytes: Option<u64>,
    #[serde(default)]
    pub anti_cheat_launch: Option<crate::game_types::AntiCheatLaunch>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GamePropertySetReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub pending: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub properties: Option<GameProperties>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeyPreview {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub looks_like_key: bool,
    #[serde(default)]
    pub store: Option<String>,
    #[serde(default)]
    pub key: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreArt {
    #[serde(default)]
    pub poster: Option<String>,
    #[serde(default)]
    pub hero: Option<String>,
    #[serde(default)]
    pub capsule: Option<String>,
    #[serde(default)]
    pub header: Option<String>,
}

impl StoreArt {
    pub fn best_poster(&self) -> Option<&str> {
        self.poster
            .as_deref()
            .or(self.header.as_deref())
            .or(self.capsule.as_deref())
            .or(self.hero.as_deref())
    }

    pub fn best_hero(&self) -> Option<&str> {
        self.hero
            .as_deref()
            .or(self.header.as_deref())
            .or(self.capsule.as_deref())
            .or(self.poster.as_deref())
    }

    pub fn steam_url(app_id: &str, kind: &str) -> String {
        let file = match kind {
            "poster" => "library_600x900.jpg",
            "hero" => "library_hero.jpg",
            "logo" => "logo.png",
            "capsule" => "capsule_616x353.jpg",
            _ => "header.jpg",
        };
        format!(
            "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{app_id}/{file}"
        )
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreTile {
    #[serde(default)]
    pub app_id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub final_price: Option<i64>,
    #[serde(rename = "final", default)]
    pub final_cents: Option<i64>,
    #[serde(default)]
    pub initial: Option<i64>,
    #[serde(default)]
    pub discount: i32,
    #[serde(default)]
    pub currency: Option<String>,
    #[serde(default)]
    pub price_text: Option<String>,
    #[serde(default)]
    pub was_text: Option<String>,
    #[serde(default)]
    pub review: Option<String>,
    #[serde(default)]
    pub review_percent: Option<i32>,
    #[serde(default)]
    pub free: bool,
    #[serde(default)]
    pub soon: bool,
    #[serde(default)]
    pub when: Option<String>,
    #[serde(default)]
    pub wished: bool,
    #[serde(default)]
    pub added_at: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub similar_to: Vec<String>,
    #[serde(default)]
    pub art: Option<StoreArt>,
}

impl StoreTile {
    pub fn display_price(&self) -> String {
        if self.free {
            return "Free".into();
        }
        if let Some(txt) = self.price_text.as_deref().filter(|s| !s.is_empty()) {
            return txt.to_string();
        }
        if let Some(cents) = self.final_cents.or(self.final_price) {
            return format_cents(cents, self.currency.as_deref());
        }
        if self.soon {
            return self.when.clone().unwrap_or_else(|| "Coming soon".into());
        }
        String::new()
    }
}

pub fn format_cents(cents: i64, currency: Option<&str>) -> String {
    if cents <= 0 {
        return "Free".into();
    }
    let sym = match currency.unwrap_or("USD") {
        "EUR" => "€",
        "GBP" => "£",
        "CAD" => "CA$",
        "AUD" => "A$",
        _ => "$",
    };
    format!("{sym}{:.2}", cents as f64 / 100.0)
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreShelf {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub items: Vec<StoreTile>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreHome {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub at: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub hero: Vec<StoreTile>,
    #[serde(default)]
    pub shelves: Vec<StoreShelf>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreSearchReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub items: Vec<StoreTile>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreWishlistReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub items: Vec<StoreTile>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreMedia {
    #[serde(default)]
    pub thumb: String,
    #[serde(default)]
    pub hls: Option<String>,
    #[serde(default)]
    pub av1: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreSpec {
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub minimum: Option<String>,
    #[serde(default)]
    pub recommended: Option<String>,
    #[serde(default)]
    pub mine: Option<String>,
    #[serde(default)]
    pub meets: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreRecentReviews {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub word: Option<String>,
    #[serde(default)]
    pub percent: Option<i32>,
    #[serde(default)]
    pub count: u64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreGame {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub app_id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub header: Option<String>,
    #[serde(default)]
    pub coming_soon: bool,
    #[serde(default)]
    pub released: Option<String>,
    #[serde(default)]
    pub developers: Vec<String>,
    #[serde(default)]
    pub publishers: Vec<String>,
    #[serde(default)]
    pub genres: Vec<String>,
    #[serde(default)]
    pub metacritic: Option<i32>,
    #[serde(default)]
    pub review_word: Option<String>,
    #[serde(default)]
    pub review_percent: Option<i32>,
    #[serde(default)]
    pub review_count: u64,
    #[serde(default)]
    pub price_text: Option<String>,
    #[serde(default)]
    pub was_text: Option<String>,
    #[serde(default)]
    pub discount: i32,
    #[serde(default)]
    pub free: bool,
    #[serde(default)]
    pub tags: Vec<String>,
    #[serde(default)]
    pub drm: Option<String>,
    #[serde(default)]
    pub account: Option<String>,
    #[serde(default)]
    pub controller: Option<String>,
    #[serde(default)]
    pub play_station: bool,
    #[serde(default)]
    pub languages: Vec<String>,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub offers: Vec<StoreOffer>,
    #[serde(default)]
    pub media: Vec<StoreMedia>,
    #[serde(default)]
    pub specs: Vec<StoreSpec>,
    #[serde(default)]
    pub screenshots: Vec<String>,
    #[serde(default)]
    pub art: Option<StoreArt>,
    #[serde(default)]
    pub fit: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreOffer {
    #[serde(default)]
    pub store: String,
    #[serde(default)]
    pub name: String,
    #[serde(rename = "final", default)]
    pub final_cents: Option<i64>,
    #[serde(default)]
    pub initial: Option<i64>,
    #[serde(default)]
    pub discount: i32,
    #[serde(default)]
    pub currency: Option<String>,
    #[serde(default)]
    pub price_text: Option<String>,
    #[serde(default)]
    pub was_text: Option<String>,
    #[serde(default)]
    pub url: String,
}

impl StoreOffer {
    pub fn display_name(&self) -> String {
        let trimmed = self.name.trim();
        if !trimmed.is_empty() {
            return trimmed.to_string();
        }
        match self.store.trim() {
            "1" => "Steam".into(),
            "7" => "GOG".into(),
            "13" => "Ubisoft".into(),
            "25" => "Epic".into(),
            "" => "Official store".into(),
            other => store_label(other).to_string(),
        }
    }

    pub fn display_price(&self) -> String {
        if let Some(txt) = self.price_text.as_deref().filter(|s| !s.is_empty()) {
            return txt.to_string();
        }
        if let Some(cents) = self.final_cents {
            return format_cents(cents, self.currency.as_deref());
        }
        String::new()
    }

    pub fn display_was(&self) -> Option<String> {
        if self.discount <= 0 {
            return None;
        }
        self.was_text.clone().filter(|s| !s.is_empty()).or_else(|| {
            self.initial
                .filter(|&v| v > 0)
                .map(|v| format_cents(v, self.currency.as_deref()))
        })
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreLowest {
    #[serde(rename = "final", default)]
    pub final_cents: i64,
    #[serde(default)]
    pub at: String,
}

impl StoreLowest {
    pub fn formatted_date(&self) -> Option<String> {
        let trimmed = self.at.trim();
        if trimmed.is_empty() {
            return None;
        }
        let date_part = trimmed.split('T').next().unwrap_or(trimmed);
        let mut parts = date_part.split('-');
        if let (Some(y), Some(m)) = (parts.next(), parts.next())
            && y.len() == 4
            && y.chars().all(|c| c.is_ascii_digit())
            && let Ok(month) = m.parse::<usize>()
            && (1..=12).contains(&month)
        {
            const MONTHS: [&str; 12] = [
                "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
            ];
            return Some(format!("{} {y}", MONTHS[month - 1]));
        }
        Some(trimmed.to_string())
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreOtherOffers {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub offers: Vec<StoreOffer>,
    #[serde(default)]
    pub lowest: Option<StoreLowest>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LauncherSettings {
    #[serde(default)]
    pub reduce_motion: bool,
    #[serde(default)]
    pub start_with_windows: bool,
    #[serde(default)]
    pub start_maximized: bool,
    #[serde(default)]
    pub show_library_tools: bool,
    #[serde(default)]
    pub pause_downloads_while_playing: bool,
    #[serde(default)]
    pub keep_awake_downloads: bool,
    #[serde(default)]
    pub auto_update_games: bool,
    #[serde(default)]
    pub store_clients_quiet: bool,
    #[serde(default)]
    pub achievement_banners: bool,
    #[serde(default)]
    pub fps_counter: bool,
    #[serde(default)]
    pub fps_counter_position: Option<String>,
    #[serde(default)]
    pub hdr_for_games: bool,
    #[serde(default)]
    pub auto_update_upscalers: bool,
    #[serde(default)]
    pub auto_update_chipset: bool,
    #[serde(default)]
    pub text_size: Option<String>,
    #[serde(default)]
    pub riot_id: Option<String>,
    #[serde(default)]
    pub onboarding_complete: Option<bool>,
    #[serde(default)]
    pub ui_prefs: HashMap<String, Value>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub hidden_games: Vec<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub recent: Vec<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub preferred_game_sources: HashMap<String, String>,
}

use std::collections::HashMap;

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAccountState {
    #[serde(default)]
    pub signed_in: bool,
    #[serde(default)]
    pub email: Option<String>,
    #[serde(default)]
    pub account_asked: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ArtBase {
    #[serde(default)]
    pub covers: String,
    #[serde(default)]
    pub covers_dir: Option<String>,
    #[serde(default)]
    pub devices_dir: Option<String>,
    #[serde(default)]
    pub notes_dir: Option<String>,
    #[serde(default)]
    pub captures_dir: Option<String>,
    #[serde(default)]
    pub capture_thumbs_dir: Option<String>,
}

impl ArtBase {
    pub fn resolve_local_cover(&self, cover_url: &str) -> Option<PathBuf> {
        let root = self.covers_dir.as_deref()?;
        let rest = cover_url
            .strip_prefix("https://covers.exo-launcher.local/")
            .or_else(|| cover_url.strip_prefix("https://thumbs.exo-launcher.local/"))?;
        let without_query = rest.split('?').next().unwrap_or(rest);
        let leaf = without_query.rsplit('/').next()?;
        if !is_safe_leaf(leaf) {
            return None;
        }
        let decoded = percent_decode_simple(leaf)?;
        if !is_safe_leaf(&decoded) {
            return None;
        }
        let candidate = Path::new(root).join(&decoded);
        if candidate.is_file() {
            Some(candidate)
        } else {
            None
        }
    }

    pub fn resolve_local_sanitized_portrait(&self, game: &Game) -> Option<PathBuf> {
        let root = self.covers_dir.as_deref()?;
        let sanitized = sanitize_cache_id(&game.id);
        for ext in ["jpg", "png", "webp"] {
            let p = Path::new(root).join(format!("{sanitized}.{ext}"));
            if p.is_file() {
                return Some(p);
            }
        }
        None
    }

    pub fn resolve_local_portrait(&self, game: &Game) -> Option<PathBuf> {
        for cand in game.portrait_art_candidates() {
            if let Some(p) = self.resolve_local_cover(&cand.url) {
                return Some(p);
            }
        }
        self.resolve_local_sanitized_portrait(game)
    }

    pub fn resolve_local_hero(&self, game: &Game) -> Option<PathBuf> {
        let root = self.covers_dir.as_deref()?;
        let target = sanitize_cache_id(&game.hero_cache_target());
        let direct = Path::new(root).join(format!("hero_{target}.jpg"));
        if direct.is_file() {
            return Some(direct);
        }
        if let Some(app_id) = game.art_steam_app_id() {
            for name in [
                format!("hero_steam_{app_id}.jpg"),
                format!("{app_id}_hero.jpg"),
            ] {
                let p = Path::new(root).join(&name);
                if p.is_file() {
                    return Some(p);
                }
            }
        }
        None
    }

    pub fn resolve_device_image(&self, url: &str) -> Option<PathBuf> {
        let root = self.devices_dir.as_deref()?;
        let rest = url.strip_prefix("https://devices.exo-launcher.local/")?;
        let leaf = rest.split('?').next().unwrap_or(rest).rsplit('/').next()?;
        let decoded = percent_decode_simple(leaf)?;
        if !is_safe_leaf(&decoded) {
            return None;
        }
        let p = Path::new(root).join(decoded);
        p.is_file().then_some(p)
    }

    pub fn resolve_capture_thumb(&self, thumb_url: &str) -> Option<PathBuf> {
        if let Some(rest) = thumb_url.strip_prefix("https://capture-thumbs.exo-launcher.local/") {
            let root = self.capture_thumbs_dir.as_deref()?;
            let leaf = rest.split('?').next().unwrap_or(rest).rsplit('/').next()?;
            let decoded = percent_decode_simple(leaf)?;
            if !is_safe_leaf(&decoded) {
                return None;
            }
            let p = Path::new(root).join(decoded);
            return p.is_file().then_some(p);
        }
        self.resolve_capture_file(thumb_url)
    }

    pub fn resolve_capture_file(&self, media_url: &str) -> Option<PathBuf> {
        let root = self.captures_dir.as_deref()?;
        let rest = media_url.strip_prefix("https://captures.exo-launcher.local/")?;
        let without_query = rest.split('?').next().unwrap_or(rest);
        let mut path = PathBuf::from(root);
        let mut parts = 0usize;
        for seg in without_query.split('/') {
            if seg.is_empty() {
                continue;
            }
            let decoded = percent_decode_simple(seg)?;
            if !is_safe_leaf(&decoded) {
                return None;
            }
            path.push(decoded);
            parts += 1;
        }
        if parts == 0 || !path.is_file() {
            return None;
        }
        Some(path)
    }
}

fn is_safe_leaf(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 200
        && !name.contains("..")
        && !name.contains('/')
        && !name.contains('\\')
        && !name.contains(':')
        && !name.bytes().any(|b| b < 0x20)
}

fn percent_decode_simple(input: &str) -> Option<String> {
    let bytes = input.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' {
            if i + 2 >= bytes.len() {
                return None;
            }
            let hi = (bytes[i + 1] as char).to_digit(16)?;
            let lo = (bytes[i + 2] as char).to_digit(16)?;
            out.push(((hi << 4) | lo) as u8);
            i += 3;
        } else {
            out.push(bytes[i]);
            i += 1;
        }
    }
    String::from_utf8(out).ok()
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActionResult {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub store: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone)]
pub enum HostEvent {
    LibraryUpdated(LibraryPayload),
    InstallProgress(InstallProgress),
    UpdateProgress(crate::pc_types::UpdateProgress),
    MusicPlayback(Option<crate::media_types::MusicPlayback>),
    DiscordState(crate::discord_types::DiscordStateEvent),
    DiscordSignIn(crate::discord_types::DiscordSignInStep),
    DiscordChanged(crate::discord_types::DiscordChangedEvent),
    DiscordMembers { guild_id: String },
    DiscordPresence(crate::discord_types::DiscordPresenceEvent),
    DiscordUnread(crate::discord_types::DiscordUnreadEvent),
    DiscordVoice(crate::discord_types::DiscordVoice),
    DiscordSpeaking { user_id: String, speaking: bool },
    DiscordMessage(Box<crate::discord_types::DiscordMessageEvent>),
    DiscordReaction(crate::discord_types::DiscordReactionEvent),
    DiscordTyping(crate::discord_types::DiscordTypingEvent),
    DiscordVideo(crate::discord_types::DiscordVideoEvent),
    DiscordVideoFrame(crate::discord_types::DiscordVideoFrame),
    DiscordGoLive(crate::discord_types::DiscordGoLiveStateEvent),
    DiscordGoLiveStats(crate::discord_types::DiscordGoLiveStatsEvent),
    DiscordGoLiveViewers(Vec<String>),
    DevicesChanged,
    ControllerState(crate::device_types::ControllerStateEvent),
    HidChanged,
    HidInput(crate::device_types::HidInputEvent),
    AppsChanged,
    MusicVolume(crate::events::MusicVolumeEvent),
    CaptureState(crate::media_types::CaptureLiveState),
    CaptureSaved(Box<crate::events::CaptureSavedEvent>),
    CaptureReel(Box<crate::events::CaptureReelEvent>),
    CaptureShrunk(Box<crate::events::CaptureShrunkEvent>),
    NotesOpen(crate::events::NotesOpenEvent),
    InstallQueue(crate::events::InstallQueueEvent),
    GameRunState(crate::events::GameRunStateEvent),
    LaunchStatus(crate::events::LaunchStatusEvent),
    AppUpdateState(crate::events::AppUpdateStateEvent),
    ExoAccountChanged,
    AutopilotReport(crate::events::AutopilotReportEvent),
    AutopilotLive(Option<crate::pc_types::AutopilotLive>),
    ShadersChanged,
    StoreChanged(crate::events::StoreChangedEvent),
    GameAchievements(crate::game_types::GameAchievementsEvent),
    GameOptimizedAgain(crate::game_types::GameOptimizeEvent),
    GameOptimizeHeld(crate::game_types::GameOptimizeEvent),
    CompressProgress(crate::game_types::CompressProgressEvent),
    StoreSale(crate::events::StoreSaleEvent),
    Raw { event: String, data: Value },
    Disconnected(String),
}

#[cfg(test)]
mod cover_policy_tests {
    use super::*;

    fn rocket_league_game() -> Game {
        Game {
            id: "epic:Sugar".into(),
            title: "Rocket League".into(),
            store: "epic".into(),
            stores: vec!["epic".into(), "steam".into()],
            installed: true,
            owned: true,
            cover_url: Some("https://covers.exo-launcher.local/252950.jpg".into()),
            variants: vec![
                GameVariant {
                    id: "epic:Sugar".into(),
                    launch_target: None,
                    installed: true,
                    owned: true,
                    ..Default::default()
                },
                GameVariant {
                    id: "steam:252950".into(),
                    launch_target: None,
                    installed: false,
                    owned: true,
                    ..Default::default()
                },
            ],
            ..Default::default()
        }
    }

    #[test]
    fn rocket_league_candidates_match_react_order() {
        let game = rocket_league_game();
        let cands = game.portrait_art_candidates();
        let urls: Vec<&str> = cands.iter().map(|c| c.url.as_str()).collect();
        assert_eq!(
            urls,
            vec![
                "https://covers.exo-launcher.local/252950_2x.jpg",
                "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/252950/library_600x900_2x.jpg",
                "https://cdn.cloudflare.steamstatic.com/steam/apps/252950/library_600x900_2x.jpg",
                "https://covers.exo-launcher.local/252950.jpg",
                "https://covers.exo-launcher.local/steam_252950.jpg",
                "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/252950/library_600x900.jpg",
                "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/252950/library_capsule.jpg",
                "https://cdn.cloudflare.steamstatic.com/steam/apps/252950/library_600x900.jpg",
                "https://cdn.cloudflare.steamstatic.com/steam/apps/252950/library_capsule.jpg",
                "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/252950/header.jpg",
                "https://cdn.cloudflare.steamstatic.com/steam/apps/252950/header.jpg",
                "https://cdn.cloudflare.steamstatic.com/steam/apps/252950/capsule_616x353.jpg",
            ]
        );
        assert!(cands[..9].iter().all(|c| !c.derived));
        assert!(cands[9..].iter().all(|c| c.derived));
    }

    #[test]
    fn rocket_league_resolves_2x_cover_over_1x_raw_cover() {
        let game = rocket_league_game();
        let dir = std::env::temp_dir().join(format!("exo-rpc-cover-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let file_1x = dir.join("252950.jpg");
        let file_2x = dir.join("252950_2x.jpg");
        std::fs::write(&file_1x, b"1x").unwrap();
        std::fs::write(&file_2x, b"2x").unwrap();

        let base = ArtBase {
            covers_dir: Some(dir.to_string_lossy().into_owned()),
            ..Default::default()
        };
        assert_eq!(base.resolve_local_portrait(&game), Some(file_2x.clone()));

        std::fs::remove_file(&file_2x).unwrap();
        assert_eq!(base.resolve_local_portrait(&game), Some(file_1x));

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn custom_hires_cover_beats_steam_2x_and_rejects_headers() {
        let mut game = rocket_league_game();
        game.cover_url = Some("https://covers.exo-launcher.local/custom_epic_Sugar.jpg".into());
        game.art_revision = Some(7);
        let cands = game.portrait_art_candidates();
        assert_eq!(
            cands[0].url,
            "https://covers.exo-launcher.local/custom_epic_Sugar.jpg?rev=7"
        );
        assert_eq!(
            cands[1].url,
            "https://covers.exo-launcher.local/252950_2x.jpg?rev=7"
        );
        assert!(is_rejected_cover_url(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/252950/header.jpg",
            false
        ));
        assert!(!is_rejected_cover_url(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/252950/header.jpg",
            true
        ));
        assert!(!is_safe_cover_url(
            "https://example.invalid/cover.jpg",
            false
        ));
        assert!(is_safe_cover_url(
            "https://cdn1.epicgames.com/offer/sugar/portrait.jpg",
            false
        ));
    }
}
