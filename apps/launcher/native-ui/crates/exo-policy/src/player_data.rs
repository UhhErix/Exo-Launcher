use serde::{Deserialize, Serialize};

pub const SMALL_MAX_BYTES: u64 = 4 * 1024 * 1024;
pub const LARGE_MAX_BYTES: u64 = 64 * 1024 * 1024;
pub const CUSTOM_COVER_MAX_BYTES: u64 = 16 * 1024 * 1024;
pub const MAX_UI_PREF_BYTES: usize = 16 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Shape {
    JsonObject,
    JsonArray,
    Json,
    JsonLines,
    RiotId,
    Picture,
    TaskXml,
    Shortcut,
    Bytes,
}

impl Shape {
    pub const fn is_binary(self) -> bool {
        matches!(self, Self::Picture | Self::Bytes)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerFile {
    pub path: &'static str,
    pub shape: Shape,
    pub max_bytes: u64,
    pub chosen_by_settings: bool,
    pub of_this_machine: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UiPref {
    pub key: &'static str,
    pub syncs: bool,
}

pub const FILES: &[PlayerFile] = &[
    PlayerFile {
        path: "settings.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "playtime.json",
        shape: Shape::JsonObject,
        max_bytes: LARGE_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "reported-hours.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "riot-ledger/*.jsonl",
        shape: Shape::JsonLines,
        max_bytes: LARGE_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "riot-id.txt",
        shape: Shape::RiotId,
        max_bytes: 1024,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "riot-matches.json",
        shape: Shape::JsonObject,
        max_bytes: LARGE_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "exo-imported-lifetime.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "window-placement.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "notes/notes.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "covers/custom-cover-*",
        shape: Shape::Picture,
        max_bytes: CUSTOM_COVER_MAX_BYTES,
        chosen_by_settings: true,
        of_this_machine: false,
    },
    PlayerFile {
        path: "covers/folder-art-*",
        shape: Shape::Picture,
        max_bytes: CUSTOM_COVER_MAX_BYTES,
        chosen_by_settings: true,
        of_this_machine: false,
    },
    PlayerFile {
        path: "installed-games.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "store/wishlist-v2.json",
        shape: Shape::JsonArray,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "web-client-preferences.json",
        shape: Shape::JsonObject,
        max_bytes: 64 * 1024,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "mouse/profiles.json",
        shape: Shape::JsonObject,
        max_bytes: 512 * 1024,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "mouse/presets.json",
        shape: Shape::JsonObject,
        max_bytes: 512 * 1024,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "controllers/games.json",
        shape: Shape::JsonArray,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "controllers/*.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "keyboard-backups/*.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "exo-eq.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "game-settings/window-modes.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "game-settings/*.json",
        shape: Shape::Json,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "game-settings/*",
        shape: Shape::Bytes,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "graphics-features/state.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "graphics-features/backups/*",
        shape: Shape::Bytes,
        max_bytes: LARGE_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "keep.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "session-recorder.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "autopilot-reports.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "autopilot-journal.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "hdr-journal.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "hdr-calibration.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "frame-gen/games.json",
        shape: Shape::Json,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "replaced.json",
        shape: Shape::Json,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "startup-removed.json",
        shape: Shape::JsonArray,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "shader-rebuild.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "unreal-shader-ini.json",
        shape: Shape::JsonObject,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "startup-guard.json",
        shape: Shape::Json,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: true,
    },
    PlayerFile {
        path: "startup-removed/*",
        shape: Shape::TaskXml,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
    PlayerFile {
        path: "startup-removed/*/*",
        shape: Shape::Shortcut,
        max_bytes: SMALL_MAX_BYTES,
        chosen_by_settings: false,
        of_this_machine: false,
    },
];

pub const UI_PREFS: &[UiPref] = &[
    UiPref {
        key: "discord",
        syncs: true,
    },
    UiPref {
        key: "discordCamera",
        syncs: false,
    },
    UiPref {
        key: "discordPerformance",
        syncs: true,
    },
    UiPref {
        key: "driverBranch",
        syncs: false,
    },
    UiPref {
        key: "wearing",
        syncs: false,
    },
    UiPref {
        key: "shadersOrder",
        syncs: true,
    },
    UiPref {
        key: "goLiveQuality",
        syncs: false,
    },
    UiPref {
        key: "goLivePreview",
        syncs: true,
    },
    UiPref {
        key: "voiceChat",
        syncs: true,
    },
    UiPref {
        key: "discordPinnedDms",
        syncs: true,
    },
    UiPref {
        key: "tourSeen",
        syncs: true,
    },
    UiPref {
        key: "setupProgress",
        syncs: false,
    },
];

pub const SECRET_FILES: &[&str] = &[
    "account-session.bin",
    "auth.json",
    "credentials.json",
    "gogdl/credentials.json",
    "heroic/gog_store/auth.json",
    "discord-accounts.bin",
    "discord-client.bin",
    "voice/my-voice.bin",
    "my-voice-enroll.bin",
    "steam-account.bin",
];

pub fn ui_pref_syncs(key: &str) -> bool {
    UI_PREFS.iter().any(|p| p.key == key && p.syncs)
}

pub fn is_known_ui_pref(key: &str) -> bool {
    UI_PREFS.iter().any(|p| p.key == key)
}

pub fn is_secret_file(path: &str) -> bool {
    SECRET_FILES.iter().any(|s| s.eq_ignore_ascii_case(path))
}

pub fn lookup(name: &str) -> Option<&'static PlayerFile> {
    if name.is_empty()
        || name.len() > 260
        || name.contains('\\')
        || name.contains("..")
        || name.to_ascii_lowercase().ends_with(".tmp")
    {
        return None;
    }

    for file in FILES {
        if !matches_file(file.path, name) {
            continue;
        }
        let fits = match file.shape {
            Shape::TaskXml => name.ends_with(".xml"),
            Shape::Shortcut => name.to_ascii_lowercase().ends_with(".lnk"),
            Shape::Picture => name.ends_with(".png") || name.ends_with(".jpg"),
            _ => true,
        };
        if fits {
            return Some(file);
        }
    }
    None
}

fn matches_file(pattern: &str, name: &str) -> bool {
    if pattern == "riot-ledger/*.jsonl" {
        let Some(rest) = name.strip_prefix("riot-ledger/") else {
            return false;
        };
        let Some(stem) = rest.strip_suffix(".jsonl") else {
            return false;
        };
        return stem == "unassigned"
            || (stem.len() == 20
                && stem
                    .bytes()
                    .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)));
    }

    if !pattern.contains('*') {
        return pattern == name;
    }

    let pat_segs: Vec<&str> = pattern.split('/').collect();
    let name_segs: Vec<&str> = name.split('/').collect();
    if pat_segs.len() != name_segs.len() {
        return false;
    }
    for (p, n) in pat_segs.iter().zip(name_segs.iter()) {
        if !matches_segment(p, n) {
            return false;
        }
    }
    true
}

fn matches_segment(pattern: &str, seg: &str) -> bool {
    let Some((prefix, suffix)) = pattern.split_once('*') else {
        return pattern == seg;
    };
    if !seg.starts_with(prefix) || !seg.ends_with(suffix) || seg.len() < prefix.len() + suffix.len()
    {
        return false;
    }
    let wild = &seg[prefix.len()..seg.len() - suffix.len()];
    is_valid_wildcard(wild)
}

fn is_valid_wildcard(s: &str) -> bool {
    let bytes = s.as_bytes();
    if bytes.is_empty() || bytes.len() > 160 {
        return false;
    }
    if !bytes[0].is_ascii_alphanumeric() {
        return false;
    }
    bytes[1..]
        .iter()
        .all(|&b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exact_wildcard_shape_and_rejection_rules_match_cs_player_data() {
        assert_eq!(lookup("settings.json").unwrap().shape, Shape::JsonObject);
        assert_eq!(
            lookup("riot-ledger/0123456789abcdef0123.jsonl")
                .unwrap()
                .shape,
            Shape::JsonLines
        );
        assert_eq!(
            lookup("riot-ledger/unassigned.jsonl").unwrap().shape,
            Shape::JsonLines
        );
        assert!(lookup("riot-ledger/nothex.jsonl").is_none());
        assert_eq!(
            lookup("covers/custom-cover-steam-730.png").unwrap().shape,
            Shape::Picture
        );
        assert!(lookup("covers/custom-cover-steam-730.exe").is_none());
        assert_eq!(
            lookup("startup-removed/my-task.xml").unwrap().shape,
            Shape::TaskXml
        );
        assert!(lookup("startup-removed/my-task.txt").is_none());
        assert_eq!(
            lookup("startup-removed/user/app.lnk").unwrap().shape,
            Shape::Shortcut
        );
        assert!(lookup("settings.json.tmp").is_none());
        assert!(lookup("../settings.json").is_none());
        assert!(lookup(r"notes\notes.json").is_none());
        assert!(lookup("account-session.bin").is_none());
        assert!(is_secret_file("account-session.bin"));
        assert!(is_secret_file("steam-account.bin"));
        assert!(ui_pref_syncs("discord"));
        assert!(!ui_pref_syncs("driverBranch"));
    }
}
