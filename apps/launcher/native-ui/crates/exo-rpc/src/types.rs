use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Deserialize)]
pub struct HostContract {
    pub version: u32,
    pub methods: BTreeMap<String, String>,
    pub events: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Game {
    pub id: String,
    pub title: String,
    #[serde(default)]
    pub store: String,
    #[serde(default)]
    pub stores: Vec<String>,
    #[serde(default)]
    pub installed: bool,
    #[serde(default)]
    pub owned: bool,
    #[serde(default)]
    pub update_available: bool,
    #[serde(default)]
    pub can_install: bool,
    #[serde(default)]
    pub primary_action: Option<String>,
    #[serde(default)]
    pub cover_url: Option<String>,
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
    #[serde(default)]
    pub tags: Vec<String>,
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
    #[serde(default)]
    pub phase: String,
    #[serde(default)]
    pub percent: f64,
    #[serde(default)]
    pub bytes_downloaded: u64,
    #[serde(default)]
    pub bytes_to_download: u64,
    #[serde(default)]
    pub bytes_per_second: u64,
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default)]
    pub can_cancel: bool,
    #[serde(default)]
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
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameProperties {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub launch_options: Option<String>,
    #[serde(default)]
    pub steam_overlay: Option<bool>,
    #[serde(default)]
    pub cloud_sync: Option<bool>,
    #[serde(default)]
    pub language: Option<String>,
    #[serde(default)]
    pub beta: Option<String>,
    #[serde(default)]
    pub folder: Option<String>,
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
    pub price_text: Option<String>,
    #[serde(default)]
    pub was_text: Option<String>,
    #[serde(default)]
    pub discount: i32,
    #[serde(default)]
    pub free: bool,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub offers: Vec<StoreOffer>,
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
    pub discount: i32,
    #[serde(default)]
    pub currency: Option<String>,
    #[serde(default)]
    pub price_text: Option<String>,
    #[serde(default)]
    pub url: String,
}

impl StoreOffer {
    pub fn display_price(&self) -> String {
        if let Some(txt) = self.price_text.as_deref().filter(|s| !s.is_empty()) {
            return txt.to_string();
        }
        if let Some(cents) = self.final_cents {
            return format_cents(cents, self.currency.as_deref());
        }
        String::new()
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
    pub onboarding_complete: Option<bool>,
    #[serde(default)]
    pub ui_prefs: HashMap<String, Value>,
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
    Raw { event: String, data: Value },
    Disconnected(String),
}
