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
        match self.store.as_str() {
            "steam" => "Steam",
            "epic" => "Epic",
            "gog" => "GOG",
            "riot" => "Riot",
            "xbox" => "Xbox",
            "ea" => "EA",
            "ubisoft" => "Ubisoft",
            "battlenet" => "Battle.net",
            "amazon" => "Amazon",
            "rockstar" => "Rockstar",
            "itch" => "itch.io",
            "minecraft" => "Minecraft",
            "roblox" => "Roblox",
            "local" => "Local",
            _ => "Game",
        }
    }
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
    pub message: Option<String>,
}

#[derive(Debug, Clone)]
pub enum HostEvent {
    LibraryUpdated(LibraryPayload),
    Raw { event: String, data: Value },
    Disconnected(String),
}
