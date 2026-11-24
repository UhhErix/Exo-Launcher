use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};

pub const STEAM_INPUT_KEY: &str = "UseSteamControllerConfig";
pub const STEAM_DESKTOP_LAYOUT_APP_ID: &str = "413080";
pub const PLAYERS_OVERRIDE_MARKER: &str = "!";

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ControllerSupportLevel {
    Full,
    Partial,
    None,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SteamAppControllerInfo {
    pub app_id: String,
    pub is_game: bool,
    pub support: ControllerSupportLevel,
    pub reads_playstation: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SteamInputPlan {
    pub writes: BTreeMap<String, String>,
    pub next_record: BTreeMap<String, String>,
    pub conflicts_fixed: usize,
}

pub fn desired_steam_input(app: &SteamAppControllerInfo) -> Option<&'static str> {
    if !app.is_game {
        return None;
    }
    match app.support {
        ControllerSupportLevel::Full => app.reads_playstation.then_some("0"),
        ControllerSupportLevel::Partial | ControllerSupportLevel::None => Some("2"),
    }
}

pub fn steam_input_in_use(
    current: Option<&str>,
    app: &SteamAppControllerInfo,
    playstation_connected: bool,
) -> bool {
    if current == Some("2") {
        return true;
    }
    matches!(current, None | Some("1"))
        && app.is_game
        && !app.reads_playstation
        && playstation_connected
}

pub fn plan_steam_input_fixes(
    current_vdf_values: &BTreeMap<String, String>,
    rules: &BTreeMap<String, Option<&'static str>>,
    prior_record: &BTreeMap<String, String>,
) -> SteamInputPlan {
    let mut writes = BTreeMap::new();
    let mut next_record = BTreeMap::new();
    let mut keys: BTreeSet<String> = BTreeSet::new();
    keys.extend(prior_record.keys().cloned());
    keys.extend(rules.keys().cloned());
    keys.insert(STEAM_DESKTOP_LAYOUT_APP_ID.to_string());

    for app_id in keys {
        if app_id.is_empty() || app_id.len() > 10 || !app_id.bytes().all(|b| b.is_ascii_digit()) {
            continue;
        }
        let current = current_vdf_values.get(&app_id).map(String::as_str);
        let entry = prior_record.get(&app_id).map(String::as_str);
        let owned = entry.and_then(|e| e.split_once('>'));

        if entry == Some(PLAYERS_OVERRIDE_MARKER)
            || owned.is_some_and(|(_, set)| current.unwrap_or("1") != set)
        {
            next_record.insert(app_id, PLAYERS_OVERRIDE_MARKER.to_string());
            continue;
        }

        let desired = if app_id == STEAM_DESKTOP_LAYOUT_APP_ID {
            Some("0")
        } else {
            rules.get(&app_id).copied().flatten()
        };

        let Some(target) = desired else {
            if let Some((prior, _)) = owned {
                let prior_opt = if prior.is_empty() { None } else { Some(prior) };
                if current != prior_opt {
                    writes.insert(app_id, prior.to_string());
                }
            }
            continue;
        };

        let prior = owned
            .map(|(p, _)| p)
            .unwrap_or_else(|| current.unwrap_or(""));
        if current.unwrap_or("1") != target {
            writes.insert(app_id.clone(), target.to_string());
        }
        let prior_effective = if prior.is_empty() { "1" } else { prior };
        if target != prior_effective {
            next_record.insert(app_id, format!("{prior}>{target}"));
        }
    }

    let conflicts_fixed = writes
        .iter()
        .filter(|(app_id, val)| {
            app_id.as_str() != STEAM_DESKTOP_LAYOUT_APP_ID && val.as_str() == "0"
        })
        .count();

    SteamInputPlan {
        writes,
        next_record,
        conflicts_fixed,
    }
}

pub fn apply_vdf_writes(
    vdf_values: &mut BTreeMap<String, String>,
    writes: &BTreeMap<String, String>,
) {
    for (app_id, value) in writes {
        if value.is_empty() {
            vdf_values.remove(app_id);
        } else {
            vdf_values.insert(app_id.clone(), value.clone());
        }
    }
}
