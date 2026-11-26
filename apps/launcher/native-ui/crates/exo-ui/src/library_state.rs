use crate::events::HostEventKind;
use crate::image_cache::{CacheRoom, bucket_dim};
use crate::library::all_grid_metrics;
use crate::state::{
    AppState, ContextMenuTarget, HostStatus, LibraryChip, LibrarySort, NoticeTone, Room,
    chips_match, friends_playing, is_shelf_game, now_unix_ms, parse_iso_utc_ms,
};
use exo_kit::normalize_folder_color;
use exo_rpc::{
    ActionResult, FolderArtReply, Game, GameExtras, GameGetReply, HostEvent, LibraryFolder,
    LibraryPayload, LibraryRefreshAllReply, METHOD_ACCOUNTS_LIST, METHOD_ACCOUNTS_SWITCH,
    METHOD_GAME_EXTRAS, METHOD_GAME_GET, METHOD_GAME_INSTALL, METHOD_GAME_LAUNCH, METHOD_GAME_STOP,
    METHOD_GAME_UNINSTALL, METHOD_GIFTS_ANSWER, METHOD_GIFTS_LIST, METHOD_LIBRARY_FOLDER_ART,
    METHOD_LIBRARY_REFRESH, METHOD_LIBRARY_REFRESH_ALL, METHOD_SETTINGS_SET,
    METHOD_SHELL_PICK_PROGRAM, PickPathReply, RiotSavedAccount, StoreAccountsReply, StoreGift,
    StoreGifts, UninstallReply, base64,
};
use rune::{DropReorder, Image, Key, KeyEvent, Pixels, ReorderAxis, TextEdit, Tx};
use serde_json::json;
use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

pub const PICTURE_MAX_BYTES: u64 = 15 * 1024 * 1024;
pub const CROP_ASPECT: f32 = 2.0 / 3.0;
pub const CROP_OUT_WIDTH: u32 = 800;
pub const CROP_MAX_ZOOM: f32 = 4.0;
pub const CONTINUE_MAX: usize = 20;
pub const RESCAN_SECS: f64 = 30.0 * 60.0;
pub const SHINE_SWEEP_SECS: f64 = 1.9;
pub const FOLDER_TILE_PREFIX: &str = "folder:";

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum LibraryDragPayload {
    Folder(String),
    Game(String),
    FolderGame { folder_id: String, game_id: String },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GameHold {
    Starting,
    Closing,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HoldPress {
    Play,
    Stop,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct GameHoldEntry {
    pub kind: GameHold,
    pub release_at: Option<f64>,
}

#[derive(Clone, Debug)]
pub struct FolderView {
    pub id: String,
    pub name: String,
    pub color: &'static str,
    pub sort: String,
    pub art: Option<String>,
    pub games: Vec<Game>,
}

#[derive(Clone, Debug)]
pub enum GridTile {
    Folder(FolderView),
    Game(Box<Game>),
}

impl GridTile {
    pub fn key_id(&self) -> String {
        match self {
            Self::Folder(f) => folder_tile_id(&f.id),
            Self::Game(g) => g.id.clone(),
        }
    }
}

#[derive(Clone, Debug)]
pub struct FolderSheetState {
    pub id: String,
    pub is_new: bool,
    pub orig_name: String,
    pub orig_color: &'static str,
    pub orig_art: Option<String>,
    pub orig_games: Vec<String>,
    pub sort: Option<String>,
    pub name: TextEdit,
    pub color: &'static str,
    pub art: Option<String>,
    pub picked: Vec<String>,
    pub search: TextEdit,
    pub picture_error: Option<String>,
    pub file_hover: bool,
}

impl FolderSheetState {
    pub fn is_dirty(&self) -> bool {
        self.name.text.trim() != self.orig_name.trim()
            || self.color != self.orig_color
            || self.art != self.orig_art
            || self.picked != self.orig_games
    }

    pub fn is_ready(&self) -> bool {
        !self.name.text.trim().is_empty() && !self.picked.is_empty()
    }
}

#[derive(Clone, Debug)]
pub struct FolderCropperState {
    pub folder_id: String,
    pub raw_width: u32,
    pub raw_height: u32,
    pub rgba: Arc<Vec<u8>>,
    pub preview: Pixels,
    pub zoom: f32,
    pub crop_x: f32,
    pub crop_y: f32,
    pub drag_origin: Option<(f32, f32, f32, f32)>,
    pub busy: bool,
    pub error: Option<String>,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct ParsedLibraryQuery {
    pub filters: Vec<LibraryChip>,
    pub min_hours: Option<f64>,
    pub max_hours: Option<f64>,
    pub big: bool,
    pub small: bool,
    pub words: Vec<String>,
    pub understood: Vec<String>,
}

impl ParsedLibraryQuery {
    pub fn is_empty(&self) -> bool {
        self.filters.is_empty()
            && self.min_hours.is_none()
            && self.max_hours.is_none()
            && !self.big
            && !self.small
            && self.words.is_empty()
    }
}

#[derive(Clone, Debug)]
pub struct LibraryExtraState {
    pub open_folder_id: Option<String>,
    pub folder_renaming: bool,
    pub folder_rename_input: TextEdit,
    pub folder_sort_open: bool,
    pub folder_sheet: Option<FolderSheetState>,
    pub folder_cropper: Option<FolderCropperState>,
    pub folder_reorder: DropReorder,
    pub panel_game_reorder: DropReorder,
    pub folder_drop_target: Option<String>,
    pub dragging_folder_id: Option<String>,
    pub dragging_game_id: Option<String>,
    pub library_file_hover: bool,
    pub selecting: bool,
    pub picked: HashSet<String>,
    pub pick_anchor: Option<String>,
    pub select_folder_menu_open: bool,
    pub gifts: Vec<StoreGift>,
    pub gifts_answered: HashSet<String>,
    pub gifts_open: bool,
    pub gifts_busy: HashSet<String>,
    pub gifts_errors: HashMap<String, String>,
    pub gifts_loaded: bool,
    pub riot_other_accounts: Vec<RiotSavedAccount>,
    pub accounts_loaded: bool,
    pub game_holds: HashMap<String, GameHoldEntry>,
    pub optimize_held_titles: HashSet<String>,
    pub library_error: Option<String>,
    pub refresh_error: Option<String>,
    pub refresh_all_active: bool,
    pub covers_shining_until: f64,
    pub last_rescan_secs: f64,
    pub grid_focused_id: Option<String>,
    pub grid_intended_col: Option<usize>,
    pub grid_width: f32,
    pub adding_portable: bool,
    pub folder_seq: u64,
    pub home_extras_pending_id: Option<String>,
    pub home_extras_due_secs: f64,
}

impl Default for LibraryExtraState {
    fn default() -> Self {
        Self {
            open_folder_id: None,
            folder_renaming: false,
            folder_rename_input: TextEdit::default(),
            folder_sort_open: false,
            folder_sheet: None,
            folder_cropper: None,
            folder_reorder: DropReorder::new(ReorderAxis::Grid),
            panel_game_reorder: DropReorder::new(ReorderAxis::Grid),
            folder_drop_target: None,
            dragging_folder_id: None,
            dragging_game_id: None,
            library_file_hover: false,
            selecting: false,
            picked: HashSet::new(),
            pick_anchor: None,
            select_folder_menu_open: false,
            gifts: Vec::new(),
            gifts_answered: HashSet::new(),
            gifts_open: false,
            gifts_busy: HashSet::new(),
            gifts_errors: HashMap::new(),
            gifts_loaded: false,
            riot_other_accounts: Vec::new(),
            accounts_loaded: false,
            game_holds: HashMap::new(),
            optimize_held_titles: HashSet::new(),
            library_error: None,
            refresh_error: None,
            refresh_all_active: false,
            covers_shining_until: 0.0,
            last_rescan_secs: 0.0,
            grid_focused_id: None,
            grid_intended_col: None,
            grid_width: 0.0,
            adding_portable: false,
            folder_seq: 1,
            home_extras_pending_id: None,
            home_extras_due_secs: 0.0,
        }
    }
}

pub fn register_library_wp2_events(state: &mut AppState) {
    state.register_event(HostEventKind::GiftsChanged, |s, ev| {
        let HostEvent::GiftsChanged(payload) = ev else {
            return false;
        };
        s.apply_gifts(payload);
        true
    });
    state.register_event(HostEventKind::GameOptimizeHeld, |s, ev| {
        let HostEvent::GameOptimizeHeld(go) = ev else {
            return false;
        };
        if !go.title.trim().is_empty() {
            let title = go.title;
            s.lib.optimize_held_titles.insert(title.clone());
            s.push_notice(
                format!("{title} is running. Exo will optimize it once it closes."),
                NoticeTone::Neutral,
            );
        }
        true
    });
    state.register_event(HostEventKind::GameOptimizedAgain, |s, ev| {
        let HostEvent::GameOptimizedAgain(go) = ev else {
            return false;
        };
        if !go.title.trim().is_empty() {
            let title = go.title;
            s.lib.optimize_held_titles.remove(&title);
            s.push_notice(
                format!("{title}'s update reset its settings. Exo optimized it again."),
                NoticeTone::Good,
            );
        }
        true
    });
}

pub fn folder_tile_id(folder_id: &str) -> String {
    format!("{FOLDER_TILE_PREFIX}{folder_id}")
}

pub fn gift_key(gift: &StoreGift) -> String {
    format!("{}:{}", gift.store, gift.id)
}

pub fn press_outcome(held: Option<GameHold>, press: HoldPress, title: &str) -> Result<(), String> {
    match (held, press) {
        (None, _) => Ok(()),
        (Some(GameHold::Starting), HoldPress::Play) => Err(format!("{title} is already starting.")),
        (Some(GameHold::Starting), HoldPress::Stop) => Err(format!("{title} is still starting.")),
        (Some(GameHold::Closing), HoldPress::Play) => Err(format!("{title} is closing.")),
        (Some(GameHold::Closing), HoldPress::Stop) => Err(format!("{title} is already closing.")),
    }
}

pub fn crop_size(width: f32, height: f32, zoom: f32) -> (f32, f32) {
    let z = zoom.clamp(1.0, CROP_MAX_ZOOM);
    let (base_w, base_h) = if width / height.max(1.0) > CROP_ASPECT {
        (height * CROP_ASPECT, height)
    } else {
        (width, width / CROP_ASPECT)
    };
    (base_w / z, base_h / z)
}

pub fn fit_crop(width: f32, height: f32, zoom: f32, x: f32, y: f32) -> (f32, f32, f32) {
    let z = zoom.clamp(1.0, CROP_MAX_ZOOM);
    let (cw, ch) = crop_size(width, height, z);
    let max_x = (width - cw).max(0.0);
    let max_y = (height - ch).max(0.0);
    (z, x.clamp(0.0, max_x), y.clamp(0.0, max_y))
}

pub fn is_installing_game(game: &Game, transfer_id: Option<&str>) -> bool {
    let Some(tid) = transfer_id else {
        return false;
    };
    game.id.eq_ignore_ascii_case(tid)
        || game.variants.iter().any(|v| v.id.eq_ignore_ascii_case(tid))
}

fn cmp_title_ascii(a: &str, b: &str) -> std::cmp::Ordering {
    a.to_lowercase()
        .cmp(&b.to_lowercase())
        .then_with(|| a.cmp(b))
}

pub fn sort_games_list(games: &mut [Game], sort: LibrarySort, recent: &[String]) {
    let recent_rank = |id: &str| -> usize {
        recent
            .iter()
            .position(|r| r.eq_ignore_ascii_case(id))
            .unwrap_or(9999)
    };
    games.sort_by(|a, b| match sort {
        LibrarySort::LastLaunched => {
            let pa = a
                .last_played_utc
                .as_deref()
                .and_then(parse_iso_utc_ms)
                .unwrap_or(0);
            let pb = b
                .last_played_utc
                .as_deref()
                .and_then(parse_iso_utc_ms)
                .unwrap_or(0);
            pb.cmp(&pa)
                .then_with(|| recent_rank(&a.id).cmp(&recent_rank(&b.id)))
                .then_with(|| cmp_title_ascii(&a.title, &b.title))
        }
        LibrarySort::Installed => b
            .installed
            .cmp(&a.installed)
            .then_with(|| cmp_title_ascii(&a.title, &b.title)),
        LibrarySort::Playtime => b
            .playtime_minutes
            .cmp(&a.playtime_minutes)
            .then_with(|| cmp_title_ascii(&a.title, &b.title)),
        LibrarySort::Title => cmp_title_ascii(&a.title, &b.title),
        LibrarySort::Size => b
            .size_bytes
            .unwrap_or(0)
            .cmp(&a.size_bytes.unwrap_or(0))
            .then_with(|| cmp_title_ascii(&a.title, &b.title)),
        LibrarySort::Store => a
            .store
            .to_lowercase()
            .cmp(&b.store.to_lowercase())
            .then_with(|| cmp_title_ascii(&a.title, &b.title)),
    });
}

pub fn continue_order(games: &[Game], transfer_id: Option<&str>, recent: &[String]) -> Vec<Game> {
    let played = |g: &Game| -> i64 {
        g.last_played_utc
            .as_deref()
            .and_then(parse_iso_utc_ms)
            .unwrap_or(0)
    };
    let recent_rank = |id: &str| -> usize {
        recent
            .iter()
            .position(|r| r.eq_ignore_ascii_case(id))
            .unwrap_or(usize::MAX)
    };
    let rank = |g: &Game| -> u8 {
        if g.is_running {
            0
        } else if is_installing_game(g, transfer_id) {
            1
        } else if g.installed && (played(g) > 0 || recent_rank(&g.id) != usize::MAX) {
            2
        } else if g.installed {
            3
        } else {
            4
        }
    };
    let mut out = games.to_vec();
    out.sort_by(|a, b| {
        rank(a)
            .cmp(&rank(b))
            .then_with(|| played(b).cmp(&played(a)))
            .then_with(|| recent_rank(&a.id).cmp(&recent_rank(&b.id)))
            .then_with(|| b.playtime_minutes.cmp(&a.playtime_minutes))
            .then_with(|| cmp_title_ascii(&a.title, &b.title))
    });
    out.truncate(CONTINUE_MAX);
    out
}

pub fn with_folders(games: &[Game], all: &[Game], folders: &[LibraryFolder]) -> Vec<GridTile> {
    if folders.is_empty() {
        return games
            .iter()
            .cloned()
            .map(|g| GridTile::Game(Box::new(g)))
            .collect();
    }
    let by_id: HashMap<&str, &Game> = all.iter().map(|g| (g.id.as_str(), g)).collect();
    let mut filed: HashSet<String> = HashSet::new();
    let mut out: Vec<GridTile> = Vec::with_capacity(folders.len() + games.len());
    for folder in folders {
        let mut inside: Vec<Game> = folder
            .games
            .iter()
            .filter_map(|id| by_id.get(id.as_str()).copied().cloned())
            .collect();
        for g in &inside {
            filed.insert(g.id.clone());
        }
        if inside.is_empty() {
            continue;
        }
        let sort_key = folder.sort.clone().unwrap_or_else(|| "folder".into());
        if sort_key != "folder" {
            sort_games_list(&mut inside, LibrarySort::from_id(&sort_key), &[]);
        }
        out.push(GridTile::Folder(FolderView {
            id: folder.id.clone(),
            name: folder.name.clone(),
            color: normalize_folder_color(folder.color.as_deref()),
            sort: sort_key,
            art: folder.art.clone(),
            games: inside,
        }));
    }
    for game in games {
        if !filed.contains(game.id.as_str()) {
            out.push(GridTile::Game(Box::new(game.clone())));
        }
    }
    out
}

pub fn free_folder_name(folders: &[LibraryFolder]) -> String {
    let taken: HashSet<String> = folders.iter().map(|f| f.name.to_lowercase()).collect();
    for n in 1usize.. {
        let candidate = if n == 1 {
            "Folder".to_string()
        } else {
            format!("Folder {n}")
        };
        if !taken.contains(&candidate.to_lowercase()) {
            return candidate;
        }
    }
    "Folder".to_string()
}

pub fn new_folder_id(folders: &[LibraryFolder], seed: u64) -> String {
    let mut n = seed.max(1);
    loop {
        let candidate = format!("f{n:07x}");
        if !folders.iter().any(|f| f.id == candidate) {
            return candidate;
        }
        n = n.wrapping_add(1);
    }
}

fn clamp_folder_name(raw: &str) -> String {
    raw.trim().chars().take(40).collect()
}

pub fn save_folder(folders: &[LibraryFolder], mut folder: LibraryFolder) -> Vec<LibraryFolder> {
    let trimmed = clamp_folder_name(&folder.name);
    folder.name = if trimmed.is_empty() {
        free_folder_name(folders)
    } else {
        trimmed
    };
    folder.color = Some(normalize_folder_color(folder.color.as_deref()).to_string());
    if folders.iter().any(|item| item.id == folder.id) {
        folders
            .iter()
            .map(|item| {
                if item.id == folder.id {
                    folder.clone()
                } else {
                    item.clone()
                }
            })
            .collect()
    } else {
        let mut next = Vec::with_capacity(folders.len() + 1);
        next.push(folder);
        next.extend_from_slice(folders);
        next
    }
}

pub fn add_to_folder(
    folders: &[LibraryFolder],
    game_id: &str,
    folder_id: &str,
) -> Vec<LibraryFolder> {
    folders
        .iter()
        .map(|f| {
            if f.id == folder_id && !f.games.iter().any(|id| id == game_id) {
                let mut next = f.clone();
                next.games.push(game_id.to_string());
                next
            } else {
                f.clone()
            }
        })
        .collect()
}

pub fn leave_folder(
    folders: &[LibraryFolder],
    game_id: &str,
    folder_id: &str,
) -> Vec<LibraryFolder> {
    folders
        .iter()
        .filter_map(|f| {
            if f.id == folder_id {
                let mut next = f.clone();
                next.games.retain(|id| id != game_id);
                (!next.games.is_empty()).then_some(next)
            } else {
                Some(f.clone())
            }
        })
        .collect()
}

pub fn sort_folder(folders: &[LibraryFolder], folder_id: &str, sort: &str) -> Vec<LibraryFolder> {
    folders
        .iter()
        .map(|f| {
            if f.id == folder_id {
                let mut next = f.clone();
                next.sort = Some(sort.to_string());
                next
            } else {
                f.clone()
            }
        })
        .collect()
}

pub fn rename_folder(folders: &[LibraryFolder], folder_id: &str, name: &str) -> Vec<LibraryFolder> {
    let clean = clamp_folder_name(name);
    if clean.is_empty() {
        return folders.to_vec();
    }
    folders
        .iter()
        .map(|f| {
            if f.id == folder_id {
                let mut next = f.clone();
                next.name = clean.clone();
                next
            } else {
                f.clone()
            }
        })
        .collect()
}

pub fn move_folder(
    folders: &[LibraryFolder],
    folder_id: &str,
    to_id: &str,
    after: bool,
) -> Vec<LibraryFolder> {
    if folder_id == to_id {
        return folders.to_vec();
    }
    let Some(moving) = folders.iter().find(|f| f.id == folder_id).cloned() else {
        return folders.to_vec();
    };
    let mut rest: Vec<LibraryFolder> = folders
        .iter()
        .filter(|f| f.id != folder_id)
        .cloned()
        .collect();
    let Some(at) = rest.iter().position(|f| f.id == to_id) else {
        return folders.to_vec();
    };
    let insert_at = if after { at + 1 } else { at };
    rest.insert(insert_at, moving);
    rest
}

pub fn reorder_folder_games(
    folders: &[LibraryFolder],
    folder_id: &str,
    from_game_id: &str,
    to_game_id: &str,
    after: bool,
) -> Vec<LibraryFolder> {
    if from_game_id == to_game_id {
        return folders.to_vec();
    }
    folders
        .iter()
        .map(|f| {
            if f.id != folder_id {
                return f.clone();
            }
            if !f.games.iter().any(|g| g == from_game_id) {
                return f.clone();
            }
            let mut rest: Vec<String> = f
                .games
                .iter()
                .filter(|g| g.as_str() != from_game_id)
                .cloned()
                .collect();
            let Some(at) = rest.iter().position(|g| g == to_game_id) else {
                return f.clone();
            };
            let insert_at = if after { at + 1 } else { at };
            rest.insert(insert_at, from_game_id.to_string());
            let mut next = f.clone();
            next.games = rest;
            next
        })
        .collect()
}

pub fn delete_folder(folders: &[LibraryFolder], folder_id: &str) -> Vec<LibraryFolder> {
    folders
        .iter()
        .filter(|f| f.id != folder_id)
        .cloned()
        .collect()
}

fn push_unique_chip(out: &mut Vec<LibraryChip>, understood: &mut Vec<String>, chip: LibraryChip) {
    if !out.contains(&chip) {
        understood.push(chip.label());
        out.push(chip);
    }
}

pub fn parse_library_query(raw: &str) -> ParsedLibraryQuery {
    let mut out = ParsedLibraryQuery::default();
    let cleaned: String = raw
        .to_lowercase()
        .chars()
        .map(|c| {
            if c == '\'' || c == '\u{2019}' {
                '\0'
            } else if c.is_alphanumeric() || c == '.' || c == '-' {
                c
            } else {
                ' '
            }
        })
        .filter(|&c| c != '\0')
        .collect();
    let normalized = cleaned
        .replace('-', " ")
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ");
    if normalized.is_empty() {
        return out;
    }

    let mut rest = format!(" {normalized} ");

    let phrases: &[(&str, &[LibraryChip])] = &[
        ("havent played yet", &[LibraryChip::NotPlayed]),
        ("have not played", &[LibraryChip::NotPlayed]),
        ("havent played", &[LibraryChip::NotPlayed]),
        ("never played", &[LibraryChip::NotPlayed]),
        ("not played", &[LibraryChip::NotPlayed]),
        ("didnt play", &[LibraryChip::NotPlayed]),
        ("not started", &[LibraryChip::NotPlayed]),
        ("not installed", &[LibraryChip::NotInstalled]),
        ("isnt installed", &[LibraryChip::NotInstalled]),
        ("arent installed", &[LibraryChip::NotInstalled]),
        ("to install", &[LibraryChip::NotInstalled]),
        ("played recently", &[LibraryChip::PlayedLately]),
        ("recently played", &[LibraryChip::PlayedLately]),
        ("played lately", &[LibraryChip::PlayedLately]),
        ("friends are playing", &[LibraryChip::Friends]),
        ("friends playing", &[LibraryChip::Friends]),
        ("my friends play", &[LibraryChip::Friends]),
        ("family shared", &[LibraryChip::FamilyShared]),
        ("family sharing", &[LibraryChip::FamilyShared]),
        ("shared with me", &[LibraryChip::FamilyShared]),
        ("needs an update", &[LibraryChip::Update]),
        ("need an update", &[LibraryChip::Update]),
        ("needs update", &[LibraryChip::Update]),
        ("update ready", &[LibraryChip::Update]),
    ];

    for (phrase, chips) in phrases {
        let needle = format!(" {phrase} ");
        if rest.contains(&needle) {
            rest = rest.replace(&needle, " ");
            for chip in *chips {
                push_unique_chip(&mut out.filters, &mut out.understood, chip.clone());
            }
        }
    }

    let tag_phrases: &[(&str, &str)] = &[
        ("local co op", "local co-op"),
        ("split screen", "local co-op"),
        ("couch co op", "local co-op"),
        ("online co op", "co-op"),
        ("co op", "co-op"),
        ("single player", "single-player"),
        ("multi player", "multiplayer"),
        ("free to play", "free to play"),
        ("early access", "early access"),
        ("role playing", "rpg"),
    ];
    for (phrase, tag) in tag_phrases {
        let needle = format!(" {phrase} ");
        if rest.contains(&needle) {
            rest = rest.replace(&needle, " ");
            push_unique_chip(
                &mut out.filters,
                &mut out.understood,
                LibraryChip::Tag((*tag).to_string()),
            );
        }
    }

    for (phrase, store) in [("battle net", "battlenet"), ("itch io", "itch")] {
        let needle = format!(" {phrase} ");
        if rest.contains(&needle) {
            rest = rest.replace(&needle, " ");
            push_unique_chip(
                &mut out.filters,
                &mut out.understood,
                LibraryChip::Store((*store).to_string()),
            );
        }
    }

    let tokens: Vec<String> = rest.split_whitespace().map(str::to_string).collect();
    let mut i = 0;
    while i < tokens.len() {
        let w = tokens[i].as_str();
        if (w == "over" || w == "under" || w == "more" || w == "less") && i + 1 < tokens.len() {
            let mut num_idx = i + 1;
            if (tokens[num_idx] == "than") && num_idx + 1 < tokens.len() {
                num_idx += 1;
            }
            if let Ok(val) = tokens[num_idx].parse::<f64>()
                && num_idx + 1 < tokens.len()
                && matches!(
                    tokens[num_idx + 1].as_str(),
                    "h" | "hr" | "hrs" | "hour" | "hours"
                )
            {
                if w == "over" || w == "more" {
                    out.min_hours = Some(val);
                    out.understood.push(format!("Over {val} hr"));
                } else {
                    out.max_hours = Some(val);
                    out.understood.push(format!("Under {val} hr"));
                }
                i = num_idx + 2;
                continue;
            }
        }
        if matches!(w, "big" | "large" | "huge") {
            out.big = true;
            if !out.understood.iter().any(|u| u == "Big") {
                out.understood.push("Big".into());
            }
            i += 1;
            continue;
        }
        if matches!(w, "small" | "tiny" | "light") {
            out.small = true;
            if !out.understood.iter().any(|u| u == "Small") {
                out.understood.push("Small".into());
            }
            i += 1;
            continue;
        }

        if let Some(chips) = word_to_chips(w) {
            for chip in chips {
                push_unique_chip(&mut out.filters, &mut out.understood, chip);
            }
            i += 1;
            continue;
        }

        if is_filler_word(w) {
            i += 1;
            continue;
        }

        out.words.push(tokens[i].clone());
        i += 1;
    }

    out
}

fn word_to_chips(word: &str) -> Option<Vec<LibraryChip>> {
    let chips = match word {
        "installed" | "downloaded" | "ready" => vec![LibraryChip::Installed],
        "uninstalled" => vec![LibraryChip::NotInstalled],
        "unplayed" | "new" | "backlog" => vec![LibraryChip::NotPlayed],
        "recent" | "recently" | "lately" => vec![LibraryChip::PlayedLately],
        "updates" | "update" | "outdated" => vec![LibraryChip::Update],
        "family" | "shared" | "borrowed" => vec![LibraryChip::FamilyShared],
        "friends" => vec![LibraryChip::Friends],
        "coop" | "cooperative" => vec![LibraryChip::Tag("co-op".into())],
        "multiplayer" | "online" | "pvp" | "competitive" => {
            vec![LibraryChip::Tag("multiplayer".into())]
        }
        "singleplayer" | "solo" | "offline" => vec![LibraryChip::Tag("single-player".into())],
        "splitscreen" | "couch" => vec![LibraryChip::Tag("local co-op".into())],
        "racing" | "racer" | "racers" | "driving" | "cars" => {
            vec![LibraryChip::Tag("racing".into())]
        }
        "action" => vec![LibraryChip::Tag("action".into())],
        "adventure" | "adventures" => vec![LibraryChip::Tag("adventure".into())],
        "rpg" | "rpgs" | "roleplaying" => vec![LibraryChip::Tag("rpg".into())],
        "strategy" | "tactics" | "tactical" => vec![LibraryChip::Tag("strategy".into())],
        "simulation" | "simulator" | "simulators" | "sim" | "sims" => {
            vec![LibraryChip::Tag("simulation".into())]
        }
        "sports" | "sport" => vec![LibraryChip::Tag("sports".into())],
        "casual" | "relaxing" | "cozy" => vec![LibraryChip::Tag("casual".into())],
        "indie" | "indies" => vec![LibraryChip::Tag("indie".into())],
        "mmo" | "mmos" => vec![LibraryChip::Tag("mmo".into())],
        "mmorpg" => vec![
            LibraryChip::Tag("mmo".into()),
            LibraryChip::Tag("rpg".into()),
        ],
        "f2p" | "free" => vec![LibraryChip::Tag("free to play".into())],
        "controller" | "gamepad" | "pad" => vec![LibraryChip::Tag("controller".into())],
        "steam" => vec![LibraryChip::Store("steam".into())],
        "epic" => vec![LibraryChip::Store("epic".into())],
        "gog" => vec![LibraryChip::Store("gog".into())],
        "riot" => vec![LibraryChip::Store("riot".into())],
        "xbox" => vec![LibraryChip::Store("xbox".into())],
        "ea" => vec![LibraryChip::Store("ea".into())],
        "ubisoft" | "uplay" => vec![LibraryChip::Store("ubisoft".into())],
        "battlenet" | "blizzard" => vec![LibraryChip::Store("battlenet".into())],
        "amazon" => vec![LibraryChip::Store("amazon".into())],
        "rockstar" => vec![LibraryChip::Store("rockstar".into())],
        "itch" => vec![LibraryChip::Store("itch".into())],
        "local" => vec![LibraryChip::Store("local".into())],
        _ => return None,
    };
    Some(chips)
}

fn is_filler_word(word: &str) -> bool {
    matches!(
        word,
        "a" | "an"
            | "the"
            | "my"
            | "me"
            | "i"
            | "im"
            | "ive"
            | "id"
            | "show"
            | "find"
            | "list"
            | "all"
            | "any"
            | "some"
            | "games"
            | "game"
            | "that"
            | "which"
            | "what"
            | "with"
            | "and"
            | "or"
            | "are"
            | "is"
            | "on"
            | "in"
            | "from"
            | "of"
            | "to"
            | "for"
            | "yet"
            | "still"
            | "havent"
            | "have"
            | "got"
            | "own"
            | "owned"
            | "library"
            | "please"
            | "by"
            | "whats"
            | "thats"
            | "can"
            | "play"
            | "titles"
            | "stuff"
    )
}

pub fn matches_parsed_query(
    game: &Game,
    parsed: &ParsedLibraryQuery,
    now_ms: i64,
    playing: &HashSet<String>,
) -> bool {
    if !parsed.filters.is_empty() && !chips_match(&parsed.filters, game, now_ms, playing) {
        return false;
    }
    let hours = game.playtime_minutes as f64 / 60.0;
    if let Some(min_h) = parsed.min_hours
        && hours < min_h
    {
        return false;
    }
    if let Some(max_h) = parsed.max_hours
        && hours > max_h
    {
        return false;
    }
    const BIG_BYTES: u64 = 50 * 1024 * 1024 * 1024;
    const SMALL_BYTES: u64 = 10 * 1024 * 1024 * 1024;
    if parsed.big && game.size_bytes.unwrap_or(0) < BIG_BYTES {
        return false;
    }
    if parsed.small {
        let size = game.size_bytes.unwrap_or(0);
        if size == 0 || size > SMALL_BYTES {
            return false;
        }
    }
    if !parsed.words.is_empty() {
        let hay = game.title.to_lowercase();
        for w in &parsed.words {
            if !hay.contains(w.as_str()) {
                return false;
            }
        }
    }
    true
}

pub fn next_by_initial(tiles: &[GridTile], from: Option<usize>, ch: char) -> Option<usize> {
    if tiles.is_empty() {
        return None;
    }
    let want: String = ch.to_lowercase().collect();
    let base = from.map(|i| i as isize).unwrap_or(-1);
    let len = tiles.len() as isize;
    for step in 1..=len {
        let idx = (base + step).rem_euclid(len) as usize;
        if let GridTile::Game(g) = &tiles[idx] {
            let trimmed = g.title.trim();
            if trimmed.to_lowercase().starts_with(&want) {
                return Some(idx);
            }
        }
    }
    None
}

pub fn move_grid_focus_index(
    current: usize,
    item_count: usize,
    cols: usize,
    page_rows: usize,
    key: &Key,
    ctrl: bool,
    intended_col: Option<usize>,
) -> Option<(usize, Option<usize>)> {
    if item_count == 0 || cols == 0 {
        return None;
    }
    let cur = current.min(item_count - 1);
    let col = intended_col.unwrap_or(cur % cols).min(cols - 1);
    let row = cur / cols;
    let max_idx = item_count - 1;
    let next = match key {
        Key::Left => cur.saturating_sub(1),
        Key::Right => (cur + 1).min(max_idx),
        Key::Up => {
            if row == 0 {
                cur
            } else {
                ((row - 1) * cols + col).min(max_idx)
            }
        }
        Key::Down => {
            let candidate = (row + 1) * cols + col;
            if (row + 1) * cols > max_idx {
                cur
            } else {
                candidate.min(max_idx)
            }
        }
        Key::Home => {
            if ctrl {
                0
            } else {
                row * cols
            }
        }
        Key::End => {
            if ctrl {
                max_idx
            } else {
                ((row + 1) * cols - 1).min(max_idx)
            }
        }
        Key::PageUp => {
            let target_row = row.saturating_sub(page_rows.max(1));
            (target_row * cols + col).min(max_idx)
        }
        Key::PageDown => {
            let max_row = max_idx / cols;
            let target_row = (row + page_rows.max(1)).min(max_row);
            (target_row * cols + col).min(max_idx)
        }
        _ => return None,
    };
    let next_intended = match key {
        Key::Up | Key::Down | Key::PageUp | Key::PageDown => Some(col),
        _ => Some(next % cols),
    };
    Some((next, next_intended))
}

pub fn format_bytes_short(bytes: u64) -> String {
    const GB: f64 = 1024.0 * 1024.0 * 1024.0;
    const MB: f64 = 1024.0 * 1024.0;
    if bytes as f64 >= GB {
        let gb = bytes as f64 / GB;
        if gb >= 100.0 {
            format!("{gb:.0} GB")
        } else {
            format!("{gb:.1} GB")
        }
    } else if bytes as f64 >= MB {
        format!("{:.0} MB", bytes as f64 / MB)
    } else if bytes > 0 {
        format!("{:.0} KB", bytes as f64 / 1024.0)
    } else {
        String::new()
    }
}

pub fn format_playtime_short(minutes: u64) -> Option<String> {
    if minutes == 0 {
        return None;
    }
    if minutes < 60 {
        return Some(format!("{minutes}m"));
    }
    let hrs = minutes as f64 / 60.0;
    if hrs >= 10.0 {
        Some(format!("{:.0}h", hrs.round()))
    } else {
        Some(format!("{hrs:.1}h"))
    }
}

pub fn decode_cropper_image(path: &Path) -> Result<(u32, u32, Vec<u8>), String> {
    let meta = std::fs::metadata(path)
        .map_err(|_| "Exo couldn't read that picture. Choose another one.".to_string())?;
    if meta.len() > PICTURE_MAX_BYTES {
        return Err("Choose a picture under 15 MB.".into());
    }
    let ext_ok = path.extension().and_then(|e| e.to_str()).is_some_and(|e| {
        matches!(
            e.to_ascii_lowercase().as_str(),
            "png" | "jpg" | "jpeg" | "webp"
        )
    });
    let bytes = std::fs::read(path)
        .map_err(|_| "Exo couldn't read that picture. Choose another one.".to_string())?;
    let sig_ok = (bytes.len() >= 8 && bytes.starts_with(&[0x89, 0x50, 0x4E, 0x47]))
        || (bytes.len() >= 3 && bytes.starts_with(&[0xFF, 0xD8, 0xFF]))
        || (bytes.len() >= 12 && &bytes[0..4] == b"RIFF" && &bytes[8..12] == b"WEBP");
    if !ext_ok && !sig_ok {
        return Err("Choose a PNG, JPEG or WebP picture.".into());
    }
    let img = image::load_from_memory(&bytes)
        .map_err(|_| "Exo couldn't read that picture. Choose another one.".to_string())?;
    let rgba = img.to_rgba8();
    let (w, h) = rgba.dimensions();
    if w < 64 || h < 64 {
        return Err("Choose a picture at least 64 pixels on each side.".into());
    }
    Ok((w, h, rgba.into_raw()))
}

pub fn encode_cropped_folder_png(crop: &FolderCropperState) -> Result<String, String> {
    let (cw, ch) = crop_size(crop.raw_width as f32, crop.raw_height as f32, crop.zoom);
    let x = (crop.crop_x.round().max(0.0) as u32).min(crop.raw_width.saturating_sub(1));
    let y = (crop.crop_y.round().max(0.0) as u32).min(crop.raw_height.saturating_sub(1));
    let w = (cw.round().max(1.0) as u32).min(crop.raw_width.saturating_sub(x).max(1));
    let h = (ch.round().max(1.0) as u32).min(crop.raw_height.saturating_sub(y).max(1));
    let Some(buf) =
        image::RgbaImage::from_raw(crop.raw_width, crop.raw_height, (*crop.rgba).clone())
    else {
        return Err("Exo couldn't save that picture. Try again.".into());
    };
    let sub = image::imageops::crop_imm(&buf, x, y, w, h).to_image();
    let out_w = w.clamp(64, CROP_OUT_WIDTH);
    let out_h = ((out_w as f32) / CROP_ASPECT).round().max(64.0) as u32;
    let resized =
        image::imageops::resize(&sub, out_w, out_h, image::imageops::FilterType::Lanczos3);
    let mut png_bytes = Vec::new();
    let mut cursor = std::io::Cursor::new(&mut png_bytes);
    image::DynamicImage::ImageRgba8(resized)
        .write_to(&mut cursor, image::ImageFormat::Png)
        .map_err(|_| "Exo couldn't save that picture. Try again.".to_string())?;
    Ok(base64::encode(&png_bytes))
}

impl AppState {
    pub fn shelf_shelves(&self) -> (Vec<Game>, Vec<Game>, Vec<Game>) {
        let transfer_id = self
            .install_progress
            .as_ref()
            .filter(|p| p.is_active)
            .and_then(|p| p.game_id.as_deref());
        let show_tools = self.settings.show_library_tools;
        let recent: &[String] = if !self.recent_ids.is_empty() {
            &self.recent_ids
        } else {
            &self.settings.recent
        };
        let mut all: Vec<Game> = self
            .games
            .iter()
            .filter(|g| is_shelf_game(g, transfer_id, show_tools))
            .cloned()
            .collect();
        sort_games_list(&mut all, self.sort, recent);

        let hidden_set: HashSet<String> = self
            .settings
            .hidden_games
            .iter()
            .map(|id| id.to_lowercase())
            .collect();
        let mut library = Vec::with_capacity(all.len());
        let mut hidden = Vec::new();
        for g in all {
            if hidden_set.contains(&g.id.to_lowercase()) {
                hidden.push(g);
            } else {
                library.push(g);
            }
        }
        let playing = continue_order(&library, transfer_id, recent);
        (library, playing, hidden)
    }

    pub fn home_deck_games(&self) -> Vec<Game> {
        let (library, playing, _) = self.shelf_shelves();
        let query = self.library_search.text.trim();
        if !query.is_empty() {
            let parsed = parse_library_query(query);
            let now_ms = now_unix_ms();
            let playing_friends = if parsed.filters.contains(&LibraryChip::Friends) {
                friends_playing(&library, &self.discord.friends)
            } else {
                HashSet::new()
            };
            return library
                .into_iter()
                .filter(|g| matches_parsed_query(g, &parsed, now_ms, &playing_friends))
                .collect();
        }
        if !playing.is_empty() {
            playing
        } else {
            library.into_iter().take(CONTINUE_MAX).collect()
        }
    }

    pub fn all_games_view_model(
        &self,
    ) -> (
        String,
        Vec<GridTile>,
        Option<String>,
        usize,
        Option<usize>,
        ParsedLibraryQuery,
    ) {
        let (library, _, hidden) = self.shelf_shelves();
        let total = library.len();
        if self.library_show_hidden {
            let note = if hidden.is_empty() {
                Some("No hidden games".into())
            } else {
                None
            };
            let tiles = hidden
                .into_iter()
                .map(|g| GridTile::Game(Box::new(g)))
                .collect();
            return (
                "Hidden games".into(),
                tiles,
                note,
                total,
                None,
                ParsedLibraryQuery::default(),
            );
        }
        let parsed = parse_library_query(&self.library_search.text);
        let searching = !self.filters.is_empty() || !parsed.is_empty();
        if searching {
            let now_ms = now_unix_ms();
            let need_friends = self.filters.contains(&LibraryChip::Friends)
                || parsed.filters.contains(&LibraryChip::Friends);
            let playing_friends = if need_friends {
                friends_playing(&library, &self.discord.friends)
            } else {
                HashSet::new()
            };
            let found: Vec<Game> = library
                .into_iter()
                .filter(|g| {
                    chips_match(&self.filters, g, now_ms, &playing_friends)
                        && matches_parsed_query(g, &parsed, now_ms, &playing_friends)
                })
                .collect();
            let count = found.len();
            let note = if count == 0 {
                Some("No games match".into())
            } else {
                None
            };
            return (
                "Results".into(),
                found
                    .into_iter()
                    .map(|g| GridTile::Game(Box::new(g)))
                    .collect(),
                note,
                total,
                Some(count),
                parsed,
            );
        }
        let tiles = with_folders(&library, &library, &self.settings.library_folders);
        let note = if tiles.is_empty() && !hidden.is_empty() {
            Some("Every game is hidden".into())
        } else {
            None
        };
        (
            "All games".into(),
            tiles,
            note,
            total,
            None,
            ParsedLibraryQuery::default(),
        )
    }

    pub fn open_folder_view(&self) -> Option<FolderView> {
        let id = self.lib.open_folder_id.as_deref()?;
        let (library, _, _) = self.shelf_shelves();
        let tiles = with_folders(&library, &library, &self.settings.library_folders);
        tiles.into_iter().find_map(|tile| match tile {
            GridTile::Folder(f) if f.id == id => Some(f),
            _ => None,
        })
    }

    pub fn visible_gifts(&self) -> Vec<&StoreGift> {
        self.lib
            .gifts
            .iter()
            .filter(|g| !self.lib.gifts_answered.contains(&gift_key(g)))
            .collect()
    }

    pub fn is_game_held(&self, game_id: &str) -> Option<GameHold> {
        self.lib.game_holds.get(game_id).map(|h| h.kind)
    }

    pub fn covers_are_shining(&self) -> bool {
        self.lib.refresh_all_active || self.lib.covers_shining_until > self.clock_secs_now()
    }

    pub fn folder_art_image(&self, art: &str, scale: f32) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        self.sync_budget_for_scale(scale);
        let dim = bucket_dim(480.0);
        let key = format!("folder-art:{art}");
        if let Some(existing) = self.images.borrow_mut().get(&key, Some(cache)) {
            return existing;
        }
        let loaded = self
            .art_base
            .resolve_folder_art(art)
            .map(|path| cache.file_capped(path, dim, scale));
        self.images
            .borrow_mut()
            .insert(key, loaded.clone(), CacheRoom::Library, dim, scale);
        loaded
    }

    pub fn save_library_folders(&mut self, next: Vec<LibraryFolder>) {
        self.settings.library_folders = next.clone();
        if let Some(open_id) = self.lib.open_folder_id.as_deref()
            && !self
                .settings
                .library_folders
                .iter()
                .any(|f| f.id == open_id && !f.games.is_empty())
        {
            self.lib.open_folder_id = None;
            self.lib.folder_renaming = false;
            self.lib.folder_sort_open = false;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, exo_rpc::LauncherSettings>(
                METHOD_SETTINGS_SET,
                &json!({ "libraryFolders": next }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(set) => s.settings = set,
                        Err(_) => {
                            s.push_notice("That change did not save.", NoticeTone::Bad);
                        }
                    });
                },
            );
        }
    }

    pub fn save_hidden_games(&mut self, next: Vec<String>) {
        self.settings.hidden_games = next.clone();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, exo_rpc::LauncherSettings>(
                METHOD_SETTINGS_SET,
                &json!({ "hiddenGames": next }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(set) => s.settings = set,
                        Err(_) => {
                            s.push_notice("That change did not save.", NoticeTone::Bad);
                        }
                    });
                },
            );
        }
    }

    pub fn set_library_sort(&mut self, sort: LibrarySort) {
        self.sort = sort;
        self.settings.sort_mode = Some(sort.id().to_string());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let mode = sort.id().to_string();
            rpc.call::<_, exo_rpc::LauncherSettings>(
                METHOD_SETTINGS_SET,
                &json!({ "sortMode": mode }),
                move |res| {
                    if let Ok(set) = res {
                        handle.update(move |s, _| s.settings = set);
                    }
                },
            );
        }
    }

    pub fn open_folder_panel(&mut self, id: &str, rename: bool) {
        self.lib.open_folder_id = Some(id.to_string());
        self.lib.folder_sort_open = false;
        self.lib.folder_renaming = rename;
        if let Some(folder) = self.settings.library_folders.iter().find(|f| f.id == id) {
            self.lib.folder_rename_input = TextEdit::new(&folder.name);
        }
    }

    pub fn close_folder_panel(&mut self) {
        self.lib.open_folder_id = None;
        self.lib.folder_renaming = false;
        self.lib.folder_sort_open = false;
        self.lib.panel_game_reorder.cancel();
    }

    pub fn commit_folder_rename(&mut self) {
        if let Some(id) = self.lib.open_folder_id.clone() {
            let next = rename_folder(
                &self.settings.library_folders,
                &id,
                &self.lib.folder_rename_input.text,
            );
            self.save_library_folders(next);
        }
        self.lib.folder_renaming = false;
    }

    pub fn open_new_folder_sheet(&mut self, initial_games: Vec<String>) {
        self.library_add_open = false;
        self.library_more_open = false;
        self.context_menu = None;
        let id = new_folder_id(&self.settings.library_folders, self.lib.folder_seq);
        self.lib.folder_seq = self.lib.folder_seq.wrapping_add(1);
        let name = free_folder_name(&self.settings.library_folders);
        self.lib.folder_cropper = None;
        self.lib.folder_sheet = Some(FolderSheetState {
            id,
            is_new: true,
            orig_name: name.clone(),
            orig_color: "blue",
            orig_art: None,
            orig_games: initial_games.clone(),
            sort: None,
            name: TextEdit::new(&name),
            color: "blue",
            art: None,
            picked: initial_games,
            search: TextEdit::default(),
            picture_error: None,
            file_hover: false,
        });
    }

    pub fn open_edit_folder_sheet(&mut self, folder_id: &str) {
        self.context_menu = None;
        let Some(folder) = self
            .settings
            .library_folders
            .iter()
            .find(|f| f.id == folder_id)
            .cloned()
        else {
            return;
        };
        let color = normalize_folder_color(folder.color.as_deref());
        self.lib.folder_cropper = None;
        self.lib.folder_sheet = Some(FolderSheetState {
            id: folder.id,
            is_new: false,
            orig_name: folder.name.clone(),
            orig_color: color,
            orig_art: folder.art.clone(),
            orig_games: folder.games.clone(),
            sort: folder.sort,
            name: TextEdit::new(&folder.name),
            color,
            art: folder.art,
            picked: folder.games,
            search: TextEdit::default(),
            picture_error: None,
            file_hover: false,
        });
    }

    pub fn save_folder_sheet(&mut self) {
        let Some(sheet) = self.lib.folder_sheet.as_ref() else {
            return;
        };
        if !sheet.is_ready() {
            return;
        }
        let folder = LibraryFolder {
            id: sheet.id.clone(),
            name: sheet.name.text.trim().to_string(),
            color: Some(sheet.color.to_string()),
            sort: sheet.sort.clone(),
            art: sheet.art.clone(),
            games: sheet.picked.clone(),
        };
        let next = save_folder(&self.settings.library_folders, folder);
        self.lib.folder_sheet = None;
        self.lib.folder_cropper = None;
        self.disarm_confirm();
        self.library_all_games = true;
        self.save_library_folders(next);
    }

    pub fn request_close_folder_sheet(&mut self, tx: &mut Tx) {
        let dirty = self
            .lib
            .folder_sheet
            .as_ref()
            .is_some_and(FolderSheetState::is_dirty);
        if !dirty {
            self.lib.folder_sheet = None;
            self.lib.folder_cropper = None;
            self.disarm_confirm();
            return;
        }
        const KEY: &str = "folder-sheet-discard";
        if self.is_confirm_armed(KEY) {
            self.disarm_confirm();
            self.lib.folder_sheet = None;
            self.lib.folder_cropper = None;
        } else {
            self.arm_confirm_key(KEY, tx);
        }
    }

    pub fn open_folder_cropper_from_path(&mut self, path: &Path) {
        let Some(folder_id) = self.lib.folder_sheet.as_ref().map(|s| s.id.clone()) else {
            return;
        };
        match decode_cropper_image(path) {
            Ok((w, h, rgba)) => {
                let preview = Pixels::new();
                preview.set(w, h, rgba.clone());
                let (cw, ch) = crop_size(w as f32, h as f32, 1.0);
                let crop_x = ((w as f32) - cw) / 2.0;
                let crop_y = ((h as f32) - ch) / 2.0;
                if let Some(sheet) = self.lib.folder_sheet.as_mut() {
                    sheet.picture_error = None;
                    sheet.file_hover = false;
                }
                self.lib.folder_cropper = Some(FolderCropperState {
                    folder_id,
                    raw_width: w,
                    raw_height: h,
                    rgba: Arc::new(rgba),
                    preview,
                    zoom: 1.0,
                    crop_x,
                    crop_y,
                    drag_origin: None,
                    busy: false,
                    error: None,
                });
            }
            Err(msg) => {
                if let Some(sheet) = self.lib.folder_sheet.as_mut() {
                    sheet.picture_error = Some(msg);
                    sheet.file_hover = false;
                }
            }
        }
    }

    pub fn save_folder_cropper(&mut self) {
        let Some(crop) = self.lib.folder_cropper.as_mut() else {
            return;
        };
        if crop.busy {
            return;
        }
        crop.busy = true;
        crop.error = None;
        let folder_id = crop.folder_id.clone();
        let encoded = match encode_cropped_folder_png(crop) {
            Ok(b64) => b64,
            Err(err) => {
                crop.busy = false;
                crop.error = Some(err);
                return;
            }
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, FolderArtReply>(
                METHOD_LIBRARY_FOLDER_ART,
                &json!({ "folderId": folder_id, "image": encoded }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(reply) if reply.ok && reply.art.is_some() => {
                            let art = reply.art;
                            if let Some(sheet) = s.lib.folder_sheet.as_mut() {
                                sheet.art = art;
                                sheet.picture_error = None;
                            }
                            s.lib.folder_cropper = None;
                        }
                        Ok(reply) => {
                            if let Some(c) = s.lib.folder_cropper.as_mut() {
                                c.busy = false;
                                c.error = Some(reply.message.unwrap_or_else(|| {
                                    "Exo couldn't save that picture. Try again.".into()
                                }));
                            }
                        }
                        Err(_) => {
                            if let Some(c) = s.lib.folder_cropper.as_mut() {
                                c.busy = false;
                                c.error = Some("Exo couldn't save that picture. Try again.".into());
                            }
                        }
                    });
                },
            );
            return;
        }
        if let Some(c) = self.lib.folder_cropper.as_mut() {
            c.busy = false;
            c.error = Some("Host not connected.".into());
        }
    }

    pub fn enter_select_mode(&mut self) {
        self.library_more_open = false;
        self.library_add_open = false;
        self.context_menu = None;
        self.library_all_games = true;
        self.lib.selecting = true;
        self.lib.picked.clear();
        self.lib.pick_anchor = None;
        self.lib.select_folder_menu_open = false;
    }

    pub fn exit_select_mode(&mut self) {
        self.lib.selecting = false;
        self.lib.picked.clear();
        self.lib.pick_anchor = None;
        self.lib.select_folder_menu_open = false;
        self.disarm_confirm();
    }

    pub fn toggle_pick_game(&mut self, game_id: &str, range: bool) {
        let (_, tiles, _, _, _, _) = self.all_games_view_model();
        let pickable: Vec<String> = tiles
            .into_iter()
            .filter_map(|t| match t {
                GridTile::Game(g) => Some(g.id),
                GridTile::Folder(_) => None,
            })
            .collect();
        let Some(to) = pickable.iter().position(|id| id == game_id) else {
            return;
        };
        let from = if range {
            self.lib
                .pick_anchor
                .as_deref()
                .and_then(|a| pickable.iter().position(|id| id == a))
        } else {
            None
        };
        if from.is_none() {
            self.lib.pick_anchor = Some(game_id.to_string());
        }
        if let Some(start) = from {
            let (lo, hi) = if start <= to {
                (start, to)
            } else {
                (to, start)
            };
            for id in &pickable[lo..=hi] {
                self.lib.picked.insert(id.clone());
            }
        } else if !self.lib.picked.remove(game_id) {
            self.lib.picked.insert(game_id.to_string());
        }
    }

    pub fn pick_all_visible(&mut self) {
        let (_, tiles, _, _, _, _) = self.all_games_view_model();
        self.lib.picked = tiles
            .into_iter()
            .filter_map(|t| match t {
                GridTile::Game(g) => Some(g.id),
                GridTile::Folder(_) => None,
            })
            .collect();
    }

    pub fn picked_visible_games(&self) -> (Vec<Game>, usize) {
        let (_, tiles, _, _, _, _) = self.all_games_view_model();
        let mut total = 0usize;
        let mut out = Vec::new();
        for tile in tiles {
            if let GridTile::Game(g) = tile {
                total += 1;
                if self.lib.picked.contains(&g.id) {
                    out.push(*g);
                }
            }
        }
        (out, total)
    }

    pub fn bulk_hide_picked(&mut self) {
        let (picked_games, _) = self.picked_visible_games();
        if picked_games.is_empty() {
            return;
        }
        let ids: HashSet<String> = picked_games.iter().map(|g| g.id.clone()).collect();
        let next = if self.library_show_hidden {
            self.settings
                .hidden_games
                .iter()
                .filter(|id| !ids.contains(*id))
                .cloned()
                .collect()
        } else {
            let mut list = self.settings.hidden_games.clone();
            for id in ids {
                if !list.iter().any(|existing| existing == &id) {
                    list.push(id);
                }
            }
            list
        };
        let label = if self.library_show_hidden {
            format!("Unhid {} games", picked_games.len())
        } else {
            format!("Hid {} games", picked_games.len())
        };
        self.exit_select_mode();
        self.save_hidden_games(next);
        self.push_notice(label, NoticeTone::Neutral);
    }

    pub fn bulk_add_picked_to_folder(&mut self, folder_id: Option<&str>) {
        let (picked_games, _) = self.picked_visible_games();
        let ids: Vec<String> = picked_games.into_iter().map(|g| g.id).collect();
        if ids.is_empty() {
            return;
        }
        self.exit_select_mode();
        let Some(fid) = folder_id else {
            self.open_new_folder_sheet(ids);
            return;
        };
        let mut next = self.settings.library_folders.clone();
        for id in &ids {
            next = add_to_folder(&next, id, fid);
        }
        self.save_library_folders(next);
    }

    pub fn bulk_uninstall_picked(&mut self) {
        let (picked_games, _) = self.picked_visible_games();
        if picked_games.is_empty() {
            return;
        }
        let all_local = picked_games.iter().all(|g| g.store == "local");
        let (doing, done) = if all_local {
            ("Removing", "Removed.")
        } else {
            ("Uninstalling", "Uninstalled.")
        };
        let msg = if picked_games.len() == 1 {
            format!("{doing} {}…", picked_games[0].title)
        } else {
            format!("{doing} {} games…", picked_games.len())
        };
        self.push_notice(msg, NoticeTone::Neutral);
        let ids: Vec<(String, String)> =
            picked_games.into_iter().map(|g| (g.id, g.title)).collect();
        self.exit_select_mode();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let done_word = done.to_string();
            std::thread::spawn(move || {
                let mut failed: Option<String> = None;
                let mut handoff = false;
                let mut queued = false;
                for (id, title) in ids {
                    match rpc.call_blocking::<_, UninstallReply>(
                        METHOD_GAME_UNINSTALL,
                        &json!({ "id": id }),
                        Duration::from_secs(120),
                    ) {
                        Ok(r) => {
                            if !r.ok && failed.is_none() {
                                failed = Some(
                                    r.message.filter(|m| !m.trim().is_empty()).unwrap_or_else(
                                        || format!("Exo couldn't uninstall {title}. Try again."),
                                    ),
                                );
                            }
                            handoff |= r.handoff_only;
                            queued |= r.queued;
                        }
                        Err(_) if failed.is_none() => {
                            failed = Some(format!("Exo couldn't uninstall {title}. Try again."));
                        }
                        Err(_) => {}
                    }
                }
                handle.update(move |s, _| {
                    if let Some(err) = failed {
                        s.push_notice(err, NoticeTone::Bad);
                    } else if handoff {
                        s.push_notice("Finish uninstalling in the store app.", NoticeTone::Neutral);
                    } else if queued {
                        s.push_notice("Queued.", NoticeTone::Neutral);
                    } else {
                        s.push_notice(done_word, NoticeTone::Good);
                    }
                    s.refresh_library();
                });
            });
        }
    }

    pub fn apply_gifts(&mut self, payload: StoreGifts) {
        self.lib.gifts = payload.gifts;
        self.lib.gifts_loaded = true;
        let current_keys: HashSet<String> = self.lib.gifts.iter().map(gift_key).collect();
        self.lib.gifts_answered.retain(|k| current_keys.contains(k));
        if self.visible_gifts().is_empty() {
            self.lib.gifts_open = false;
        }
    }

    pub fn warm_library_on_startup(&mut self) {
        self.load_gifts(false);
        self.load_riot_accounts();
        self.warm_home_deck_extras();
    }

    pub fn on_library_room_enter(&mut self) {
        self.load_gifts(false);
        self.warm_home_deck_extras();
    }

    pub fn schedule_home_deck_extras(&mut self, id: String) {
        self.lib.home_extras_pending_id = Some(id);
        self.lib.home_extras_due_secs = self.clock_secs_now() + 0.24;
    }

    pub fn warm_home_deck_extras(&mut self) {
        if self.room != Room::Library || self.library_all_games || self.open_game_id.is_some() {
            return;
        }
        let deck = self.home_deck_games();
        let focused = self
            .selected_game_id
            .as_deref()
            .and_then(|id| deck.iter().find(|g| g.id == id))
            .or_else(|| deck.first())
            .map(|g| g.id.clone());
        if let Some(id) = focused {
            self.fetch_home_deck_extras(&id);
        }
    }

    fn fetch_home_deck_extras(&mut self, id: &str) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, GameExtras>(METHOD_GAME_EXTRAS, &json!({ "id": gid }), move |res| {
                if let Ok(extras) = res {
                    handle.update(move |s, _| {
                        if s.open_game_id.as_deref() == Some(gid.as_str()) {
                            s.game_extras = Some(extras);
                        }
                    });
                }
            });
        }
        #[cfg(not(windows))]
        let _ = id;
    }

    pub fn load_gifts(&mut self, force: bool) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StoreGifts>(METHOD_GIFTS_LIST, &json!({ "force": force }), move |res| {
                if let Ok(payload) = res {
                    handle.update(move |s, _| s.apply_gifts(payload));
                }
            });
        }
        #[cfg(not(windows))]
        let _ = force;
    }

    pub fn answer_gift(&mut self, store: &str, id: &str, accept: bool) {
        let key = format!("{store}:{id}");
        if self.lib.gifts_busy.contains(&key) {
            return;
        }
        self.lib.gifts_busy.insert(key.clone());
        self.lib.gifts_errors.remove(&key);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let store_s = store.to_string();
            let id_s = id.to_string();
            rpc.call::<_, ActionResult>(
                METHOD_GIFTS_ANSWER,
                &json!({ "store": store_s, "id": id_s, "accept": accept }),
                move |res| {
                    handle.update(move |s, _| {
                        s.lib.gifts_busy.remove(&key);
                        match res {
                            Ok(reply) if reply.ok => {
                                s.lib.gifts_answered.insert(key.clone());
                                if s.visible_gifts().is_empty() {
                                    s.lib.gifts_open = false;
                                }
                                if accept {
                                    s.refresh_library();
                                }
                            }
                            Ok(reply) => {
                                let fallback = if accept {
                                    "Exo couldn't accept the gift. Try again."
                                } else {
                                    "Exo couldn't decline the gift. Try again."
                                };
                                s.lib.gifts_errors.insert(
                                    key,
                                    reply
                                        .message
                                        .filter(|m| !m.trim().is_empty())
                                        .unwrap_or_else(|| fallback.into()),
                                );
                            }
                            Err(_) => {
                                let fallback = if accept {
                                    "Exo couldn't accept the gift. Try again."
                                } else {
                                    "Exo couldn't decline the gift. Try again."
                                };
                                s.lib.gifts_errors.insert(key, fallback.into());
                            }
                        }
                    });
                },
            );
            return;
        }
        self.lib.gifts_busy.remove(&key);
        self.lib.gifts_answered.insert(key);
        if self.visible_gifts().is_empty() {
            self.lib.gifts_open = false;
        }
    }

    pub fn load_riot_accounts(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StoreAccountsReply>(METHOD_ACCOUNTS_LIST, &json!({}), move |res| {
                if let Ok(reply) = res {
                    let others: Vec<RiotSavedAccount> = reply
                        .riot
                        .map(|r| {
                            r.accounts
                                .into_iter()
                                .filter(|a| a.saved && !a.current)
                                .collect()
                        })
                        .unwrap_or_default();
                    handle.update(move |s, _| {
                        s.lib.riot_other_accounts = others;
                        s.lib.accounts_loaded = true;
                    });
                }
            });
        }
    }

    pub fn play_game_as_riot_account(
        &mut self,
        game_id: &str,
        account_key: &str,
        account_name: &str,
    ) {
        self.push_notice(
            format!("Signing in as {account_name}…"),
            NoticeTone::Neutral,
        );
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = game_id.to_string();
            let key = account_key.to_string();
            rpc.call::<_, ActionResult>(
                METHOD_ACCOUNTS_SWITCH,
                &json!({ "store": "riot", "key": key }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(reply) if reply.ok => {
                            s.launch_game_held(&gid);
                        }
                        Ok(reply) => {
                            s.push_notice(
                                reply
                                    .message
                                    .filter(|m| !m.trim().is_empty())
                                    .unwrap_or_else(|| {
                                        "Exo couldn't switch the Riot account. Try again.".into()
                                    }),
                                NoticeTone::Bad,
                            );
                        }
                        Err(_) => {
                            s.push_notice(
                                "Exo couldn't switch the Riot account. Try again.",
                                NoticeTone::Bad,
                            );
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (game_id, account_key);
    }

    pub fn add_portable_program(&mut self) {
        self.library_add_open = false;
        if self.lib.adding_portable {
            return;
        }
        self.lib.adding_portable = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PickPathReply>(METHOD_SHELL_PICK_PROGRAM, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.lib.adding_portable = false;
                    match res {
                        Ok(pick) if pick.cancelled => {}
                        Ok(pick) if pick.ok => {
                            if let Some(path) = pick.path.filter(|p| !p.trim().is_empty()) {
                                s.add_portable_path(&path);
                            }
                        }
                        Ok(pick) => {
                            s.push_notice(
                                pick.message
                                    .filter(|m| !m.trim().is_empty())
                                    .unwrap_or_else(|| {
                                        "The file picker didn't open. Try again.".into()
                                    }),
                                NoticeTone::Bad,
                            );
                        }
                        Err(_) => {
                            s.push_notice(
                                "The file picker didn't open. Try again.",
                                NoticeTone::Bad,
                            );
                        }
                    }
                });
            });
            return;
        }
        self.lib.adding_portable = false;
        self.push_notice("The file picker didn't open. Try again.", NoticeTone::Bad);
    }

    pub fn add_portable_path(&mut self, path: &str) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let p = path.to_string();
            rpc.call::<_, ActionResult>(
                METHOD_GAME_INSTALL,
                &json!({ "id": "local:add", "path": p }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(r) if r.ok => {
                            s.push_notice(
                                r.message
                                    .filter(|m| !m.trim().is_empty())
                                    .unwrap_or_else(|| "Game added.".into()),
                                NoticeTone::Good,
                            );
                            s.refresh_library();
                        }
                        Ok(r) => {
                            s.push_notice(
                                r.message
                                    .filter(|m| !m.trim().is_empty())
                                    .unwrap_or_else(|| "Could not add the game. Try again.".into()),
                                NoticeTone::Bad,
                            );
                        }
                        Err(_) => {
                            s.push_notice("Could not add the game. Try again.", NoticeTone::Bad);
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = path;
    }

    pub fn refresh_library_all(&mut self) {
        if self.lib.refresh_all_active {
            return;
        }
        self.lib.refresh_all_active = true;
        self.lib.refresh_error = None;
        self.lib.covers_shining_until = self.clock_secs_now() + SHINE_SWEEP_SECS;
        self.refreshing = true;
        self.load_gifts(true);

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let rpc_ref = rpc.clone();
            let handle_ref = handle.clone();
            rpc.call::<_, LibraryPayload>(
                METHOD_LIBRARY_REFRESH,
                &json!({ "force": true }),
                move |res| {
                    handle_ref.update(move |s, _| match res {
                        Ok(lib) => {
                            s.lib.library_error = None;
                            s.apply_library(lib);
                        }
                        Err(err) => {
                            s.lib.refresh_error = Some(err.to_string());
                            s.refreshing = false;
                        }
                    });
                },
            );
            rpc_ref.call::<_, LibraryRefreshAllReply>(
                METHOD_LIBRARY_REFRESH_ALL,
                &json!({}),
                move |res| {
                    handle.update(move |s, _| {
                        s.lib.refresh_all_active = false;
                        match res {
                            Ok(reply) if reply.ok => {
                                s.lib.refresh_error = None;
                                s.images.borrow_mut().evict_room(CacheRoom::Library);
                                s.refresh_library();
                            }
                            Ok(_) => {
                                s.lib.refresh_error = Some("Try again.".into());
                                s.refreshing = false;
                            }
                            Err(err) => {
                                s.lib.refresh_error = Some(err.to_string());
                                s.refreshing = false;
                            }
                        }
                    });
                },
            );
            return;
        }
        self.lib.refresh_all_active = false;
        self.refreshing = false;
    }

    pub fn launch_game_held(&mut self, id: &str) {
        let title = self
            .games
            .iter()
            .find(|g| g.id == id)
            .map(|g| g.title.clone())
            .unwrap_or_else(|| id.to_string());
        if let Err(say) = press_outcome(self.is_game_held(id), HoldPress::Play, &title) {
            self.push_notice(say, NoticeTone::Neutral);
            return;
        }
        self.lib.game_holds.insert(
            id.to_string(),
            GameHoldEntry {
                kind: GameHold::Starting,
                release_at: None,
            },
        );
        self.notice = Some("Preparing launch…".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, ActionResult>(METHOD_GAME_LAUNCH, &json!({ "id": gid }), move |res| {
                handle.update(move |s, _| match res {
                    Ok(r) if r.ok => {
                        if let Some(msg) = r.message.filter(|m| !m.trim().is_empty()) {
                            s.notice = Some(msg);
                        }
                        let release_at = s.clock_secs_now() + 1.5;
                        s.lib.game_holds.insert(
                            gid,
                            GameHoldEntry {
                                kind: GameHold::Starting,
                                release_at: Some(release_at),
                            },
                        );
                    }
                    Ok(r) => {
                        s.lib.game_holds.remove(&gid);
                        s.push_notice(
                            r.message
                                .filter(|m| !m.trim().is_empty())
                                .unwrap_or_else(|| "Could not start the game.".into()),
                            NoticeTone::Bad,
                        );
                    }
                    Err(e) => {
                        s.lib.game_holds.remove(&gid);
                        s.push_notice(format!("Could not start the game: {e}"), NoticeTone::Bad);
                    }
                });
            });
            return;
        }
        let release_at = self.clock_secs_now() + 1.5;
        self.lib.game_holds.insert(
            id.to_string(),
            GameHoldEntry {
                kind: GameHold::Starting,
                release_at: Some(release_at),
            },
        );
    }

    pub fn stop_game_held(&mut self, id: &str) {
        let title = self
            .games
            .iter()
            .find(|g| g.id == id)
            .map(|g| g.title.clone())
            .unwrap_or_else(|| id.to_string());
        if let Err(say) = press_outcome(self.is_game_held(id), HoldPress::Stop, &title) {
            self.push_notice(say, NoticeTone::Neutral);
            return;
        }
        self.lib.game_holds.insert(
            id.to_string(),
            GameHoldEntry {
                kind: GameHold::Closing,
                release_at: None,
            },
        );
        self.push_notice(format!("Closing {title}…"), NoticeTone::Neutral);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            let rpc_get = rpc.clone();
            let handle_get = handle.clone();
            rpc.call::<_, ActionResult>(METHOD_GAME_STOP, &json!({ "id": gid }), move |res| {
                handle.update(move |s, _| {
                    s.lib.game_holds.remove(&gid);
                    match res {
                        Ok(r) if r.ok => {
                            if let Some(g) = s.games.iter_mut().find(|g| g.id == gid) {
                                g.is_running = false;
                                g.can_stop = false;
                            }
                            if let Some(msg) = r.message.filter(|m| !m.trim().is_empty()) {
                                s.push_notice(msg, NoticeTone::Neutral);
                            }
                            let gid_r = gid.clone();
                            rpc_get.call::<_, GameGetReply>(
                                METHOD_GAME_GET,
                                &json!({ "id": gid_r }),
                                move |gr| {
                                    if let Ok(reply) = gr
                                        && reply.ok
                                        && let Some(fresh) = reply.game
                                    {
                                        handle_get.update(move |st, _| {
                                            if let Some(slot) =
                                                st.games.iter_mut().find(|g| g.id == fresh.id)
                                            {
                                                *slot = fresh;
                                            }
                                        });
                                    }
                                },
                            );
                        }
                        Ok(r) => {
                            s.push_notice(
                                r.message
                                    .filter(|m| !m.trim().is_empty())
                                    .unwrap_or_else(|| "Could not close the game.".into()),
                                NoticeTone::Bad,
                            );
                        }
                        Err(_) => {
                            s.push_notice(
                                "Exo couldn't close the game. Try again.",
                                NoticeTone::Bad,
                            );
                        }
                    }
                });
            });
            return;
        }
        self.lib.game_holds.remove(id);
    }

    pub fn tick_library(&mut self, now: f64) -> bool {
        let mut changed = false;
        let expired: Vec<String> = self
            .lib
            .game_holds
            .iter()
            .filter_map(|(id, entry)| {
                entry
                    .release_at
                    .filter(|&deadline| now >= deadline)
                    .map(|_| id.clone())
            })
            .collect();
        for id in expired {
            self.lib.game_holds.remove(&id);
            changed = true;
        }
        if self.lib.covers_shining_until > 0.0 && now >= self.lib.covers_shining_until {
            self.lib.covers_shining_until = 0.0;
            changed = true;
        }
        if self.lib.home_extras_due_secs > 0.0
            && now >= self.lib.home_extras_due_secs
            && let Some(id) = self.lib.home_extras_pending_id.take()
        {
            self.lib.home_extras_due_secs = 0.0;
            self.fetch_home_deck_extras(&id);
            changed = true;
        }
        if self.status == HostStatus::Connected
            && !self.refreshing
            && !self.games.iter().any(|g| g.is_running)
        {
            if self.lib.last_rescan_secs == 0.0 {
                self.lib.last_rescan_secs = now;
            } else if now - self.lib.last_rescan_secs >= RESCAN_SECS {
                self.lib.last_rescan_secs = now;
                self.refresh_library();
                changed = true;
            }
        }
        changed
    }

    pub fn open_folder_context_menu(&mut self, folder_id: String, x: f32, y: f32) {
        if self.lib.selecting {
            return;
        }
        self.open_context_menu(ContextMenuTarget::FolderCard(folder_id), x, y);
    }

    pub fn warm_library_friends(&mut self) {
        if !self.discord.loaded && self.status == HostStatus::Connected {
            self.load_discord_room();
        }
    }

    pub fn open_store_settings_from_empty(&mut self) {
        self.switch_room(Room::Settings);
    }

    pub fn retry_library_load(&mut self) {
        self.lib.library_error = None;
        self.lib.refresh_error = None;
        self.refresh_library();
    }
}

pub fn handle_library_key(s: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    if s.room != Room::Library || s.open_game_id.is_some() || s.media.captures_open {
        return false;
    }

    if let Some(crop) = s.lib.folder_cropper.as_mut() {
        match &ev.key {
            Key::Escape => {
                s.lib.folder_cropper = None;
                return true;
            }
            Key::Left | Key::Right | Key::Up | Key::Down => {
                let (cw, _) = crop_size(crop.raw_width as f32, crop.raw_height as f32, crop.zoom);
                let step = cw * if ev.mods.shift { 0.1 } else { 0.02 };
                let (dx, dy) = match ev.key {
                    Key::Left => (step, 0.0),
                    Key::Right => (-step, 0.0),
                    Key::Up => (0.0, step),
                    Key::Down => (0.0, -step),
                    _ => (0.0, 0.0),
                };
                let (z, x, y) = fit_crop(
                    crop.raw_width as f32,
                    crop.raw_height as f32,
                    crop.zoom,
                    crop.crop_x + dx,
                    crop.crop_y + dy,
                );
                crop.zoom = z;
                crop.crop_x = x;
                crop.crop_y = y;
                return true;
            }
            Key::Text(t) if t == "+" || t == "=" || t == "-" || t == "_" => {
                let factor = if t == "+" || t == "=" { 1.1 } else { 1.0 / 1.1 };
                let (old_w, old_h) =
                    crop_size(crop.raw_width as f32, crop.raw_height as f32, crop.zoom);
                let next_zoom = (crop.zoom * factor).clamp(1.0, CROP_MAX_ZOOM);
                let (new_w, new_h) =
                    crop_size(crop.raw_width as f32, crop.raw_height as f32, next_zoom);
                let cx = crop.crop_x + old_w * 0.5;
                let cy = crop.crop_y + old_h * 0.5;
                let (z, x, y) = fit_crop(
                    crop.raw_width as f32,
                    crop.raw_height as f32,
                    next_zoom,
                    cx - new_w * 0.5,
                    cy - new_h * 0.5,
                );
                crop.zoom = z;
                crop.crop_x = x;
                crop.crop_y = y;
                return true;
            }
            _ => return false,
        }
    }

    if s.lib.folder_sheet.is_some() {
        if ev.key == Key::Escape {
            s.request_close_folder_sheet(tx);
            return true;
        }
        return false;
    }

    if s.lib.open_folder_id.is_some() {
        if ev.key == Key::Escape {
            if s.lib.folder_renaming {
                s.lib.folder_renaming = false;
            } else if s.lib.folder_sort_open {
                s.lib.folder_sort_open = false;
            } else {
                s.close_folder_panel();
            }
            return true;
        }
        return false;
    }

    if s.lib.gifts_open && ev.key == Key::Escape {
        s.lib.gifts_open = false;
        return true;
    }

    if s.library_filter_open && ev.key == Key::Escape {
        s.library_filter_open = false;
        return true;
    }

    let modal_blocked = s.context_menu.is_some()
        || s.open_dropdown.is_some()
        || s.library_more_open
        || s.library_add_open
        || s.key_modal_open
        || s.palette_open;
    if modal_blocked {
        return false;
    }

    if ev.is_command('f') && !ev.mods.shift && !ev.mods.alt {
        if !s.library_all_games && !s.library_show_hidden {
            s.library_search_open = true;
            tx.focus("library-search-input", false);
        } else {
            s.library_show_hidden = false;
            s.library_all_games = true;
            tx.focus("all-games-search-input", false);
        }
        return true;
    }

    if s.lib.selecting {
        if ev.key == Key::Escape {
            s.exit_select_mode();
            return true;
        }
        if ev.is_command('a') && !ev.mods.shift && !ev.mods.alt {
            s.pick_all_visible();
            return true;
        }
    }

    if !s.library_all_games && !s.library_show_hidden {
        if s.library_search_open && ev.key == Key::Escape {
            s.library_search.text.clear();
            s.library_search.sync_invariants();
            s.library_search_open = false;
            return true;
        }
        if matches!(ev.key, Key::Left | Key::Right) && !ev.mods.ctrl && !ev.mods.cmd && !ev.mods.alt
        {
            let deck = s.home_deck_games();
            if deck.len() >= 2 {
                let cur = s
                    .selected_game_id
                    .as_deref()
                    .and_then(|id| deck.iter().position(|g| g.id == id))
                    .unwrap_or(0);
                let delta: isize = if ev.key == Key::Right { 1 } else { -1 };
                let next_idx = (cur as isize + delta).rem_euclid(deck.len() as isize) as usize;
                let next_id = deck[next_idx].id.clone();
                s.selected_game_id = Some(next_id.clone());
                s.schedule_home_deck_extras(next_id.clone());
                tx.scroll_into_view(format!("tile-{next_id}"));
                return true;
            }
        }
        return false;
    }

    let (_, tiles, _, _, _, _) = s.all_games_view_model();
    if tiles.is_empty() {
        return false;
    }

    if !ev.mods.ctrl
        && !ev.mods.cmd
        && !ev.mods.alt
        && let Key::Text(ref t) = ev.key
        && t.chars().count() == 1
        && let Some(ch) = t.chars().next()
        && ch.is_alphanumeric()
    {
        let from = s
            .lib
            .grid_focused_id
            .as_deref()
            .and_then(|id| tiles.iter().position(|t| t.key_id() == id));
        if let Some(next_idx) = next_by_initial(&tiles, from, ch) {
            let (grid_cols, _, _) = all_grid_metrics(s.lib.grid_width);
            let key_id = tiles[next_idx].key_id();
            s.lib.grid_focused_id = Some(key_id.clone());
            s.lib.grid_intended_col = Some(next_idx % grid_cols);
            let dom_key = format!("tile-{key_id}");
            tx.scroll_into_view(dom_key.clone());
            tx.focus(dom_key, true);
            return true;
        }
        return false;
    }

    if matches!(
        ev.key,
        Key::Left
            | Key::Right
            | Key::Up
            | Key::Down
            | Key::Home
            | Key::End
            | Key::PageUp
            | Key::PageDown
    ) && !ev.mods.alt
    {
        let (grid_cols, _, _) = all_grid_metrics(s.lib.grid_width);
        let cur = s
            .lib
            .grid_focused_id
            .as_deref()
            .and_then(|id| tiles.iter().position(|t| t.key_id() == id))
            .unwrap_or(0);
        if let Some((next_idx, intended)) = move_grid_focus_index(
            cur,
            tiles.len(),
            grid_cols,
            3,
            &ev.key,
            ev.mods.ctrl || ev.mods.cmd,
            s.lib.grid_intended_col,
        ) {
            let key_id = tiles[next_idx].key_id();
            s.lib.grid_focused_id = Some(key_id.clone());
            s.lib.grid_intended_col = intended;
            let dom_key = format!("tile-{key_id}");
            tx.scroll_into_view(dom_key.clone());
            tx.focus(dom_key, true);
            return true;
        }
    }

    if ev.key == Key::Enter
        && let Some(focused_id) = s.lib.grid_focused_id.clone()
        && let Some(tile) = tiles.iter().find(|t| t.key_id() == focused_id)
    {
        match tile {
            GridTile::Folder(f) if !s.lib.selecting => {
                s.open_folder_panel(&f.id, false);
                return true;
            }
            GridTile::Game(g) => {
                if s.lib.selecting {
                    s.toggle_pick_game(&g.id, ev.mods.shift);
                } else {
                    s.open_game_page_from(&g.id, Some(format!("grid:{}", g.id)), tx);
                }
                return true;
            }
            _ => {}
        }
    }

    false
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_game(id: &str, title: &str, store: &str, installed: bool) -> Game {
        Game {
            id: id.into(),
            title: title.into(),
            store: store.into(),
            installed,
            owned: true,
            ..Default::default()
        }
    }

    #[test]
    fn folder_crud_and_reorder_match_react_semantics() {
        let f1 = LibraryFolder {
            id: "a".into(),
            name: "RPGs".into(),
            color: Some("blue".into()),
            sort: None,
            art: None,
            games: vec!["g1".into(), "g2".into()],
        };
        let f2 = LibraryFolder {
            id: "b".into(),
            name: "Co-op".into(),
            color: Some("green".into()),
            sort: None,
            art: None,
            games: vec!["g3".into()],
        };
        let list = save_folder(&[], f1.clone());
        let list = save_folder(&list, f2.clone());
        assert_eq!(list[0].id, "b");
        assert_eq!(list[1].id, "a");

        let moved = move_folder(&list, "a", "b", false);
        assert_eq!(moved[0].id, "a");
        assert_eq!(moved[1].id, "b");

        let added = add_to_folder(&moved, "g4", "b");
        assert_eq!(added[1].games, vec!["g3", "g4"]);

        let left = leave_folder(&added, "g3", "b");
        assert_eq!(left[1].games, vec!["g4"]);
        let emptied = leave_folder(&left, "g4", "b");
        assert_eq!(emptied.len(), 1);
        assert_eq!(emptied[0].id, "a");

        let deleted = delete_folder(&emptied, "a");
        assert!(deleted.is_empty());
    }

    #[test]
    fn with_folders_puts_nonempty_folders_first_and_hides_filed_games() {
        let g1 = sample_game("g1", "Alpha", "steam", true);
        let g2 = sample_game("g2", "Beta", "epic", true);
        let g3 = sample_game("g3", "Gamma", "gog", false);
        let games = vec![g1, g2, g3];
        let folders = vec![
            LibraryFolder {
                id: "empty".into(),
                name: "Empty".into(),
                color: Some("gray".into()),
                sort: None,
                art: None,
                games: vec!["missing".into()],
            },
            LibraryFolder {
                id: "fav".into(),
                name: "Favorites".into(),
                color: Some("red".into()),
                sort: Some("title".into()),
                art: None,
                games: vec!["g2".into(), "g1".into()],
            },
        ];
        let tiles = with_folders(&games, &games, &folders);
        assert_eq!(tiles.len(), 2);
        match &tiles[0] {
            GridTile::Folder(f) => {
                assert_eq!(f.id, "fav");
                assert_eq!(f.games[0].id, "g1");
                assert_eq!(f.games[1].id, "g2");
            }
            _ => panic!("expected folder first"),
        }
        match &tiles[1] {
            GridTile::Game(g) => assert_eq!(g.id, "g3"),
            _ => panic!("expected loose game second"),
        }
    }

    #[test]
    fn continue_order_ranks_running_installing_played_installed_rest() {
        let mut g_run = sample_game("run", "Running", "steam", true);
        g_run.is_running = true;
        let g_inst = sample_game("inst", "Installing", "steam", false);
        let mut g_played = sample_game("played", "Played", "steam", true);
        g_played.last_played_utc = Some("2026-11-20T12:00:00Z".into());
        let g_ready = sample_game("ready", "Ready", "steam", true);
        let g_uninst = sample_game("uninst", "Uninstalled", "steam", false);
        let ordered = continue_order(
            &[g_uninst, g_ready, g_played, g_inst, g_run],
            Some("inst"),
            &[],
        );
        let ids: Vec<&str> = ordered.iter().map(|g| g.id.as_str()).collect();
        assert_eq!(ids, vec!["run", "inst", "played", "ready", "uninst"]);
    }

    #[test]
    fn natural_language_query_parses_phrases_stores_tags_and_hours() {
        let parsed = parse_library_query("racing games I haven't played on steam over 10 hours");
        assert!(parsed.filters.contains(&LibraryChip::Tag("racing".into())));
        assert!(parsed.filters.contains(&LibraryChip::NotPlayed));
        assert!(parsed.filters.contains(&LibraryChip::Store("steam".into())));
        assert_eq!(parsed.min_hours, Some(10.0));
        assert!(parsed.words.is_empty());

        let parsed2 = parse_library_query("cyberpunk installed");
        assert!(parsed2.filters.contains(&LibraryChip::Installed));
        assert_eq!(parsed2.words, vec!["cyberpunk"]);
    }

    #[test]
    fn press_outcome_matches_game_hold_table() {
        assert!(press_outcome(None, HoldPress::Play, "Hades").is_ok());
        assert_eq!(
            press_outcome(Some(GameHold::Starting), HoldPress::Play, "Hades").unwrap_err(),
            "Hades is already starting."
        );
        assert_eq!(
            press_outcome(Some(GameHold::Starting), HoldPress::Stop, "Hades").unwrap_err(),
            "Hades is still starting."
        );
        assert_eq!(
            press_outcome(Some(GameHold::Closing), HoldPress::Play, "Hades").unwrap_err(),
            "Hades is closing."
        );
        assert_eq!(
            press_outcome(Some(GameHold::Closing), HoldPress::Stop, "Hades").unwrap_err(),
            "Hades is already closing."
        );
    }

    #[test]
    fn grid_navigation_and_typeahead_work() {
        let tiles = vec![
            GridTile::Folder(FolderView {
                id: "f1".into(),
                name: "Action".into(),
                color: "blue",
                sort: "folder".into(),
                art: None,
                games: vec![],
            }),
            GridTile::Game(Box::new(sample_game("g1", "Apex Legends", "steam", true))),
            GridTile::Game(Box::new(sample_game("g2", "Balatro", "steam", true))),
            GridTile::Game(Box::new(sample_game("g3", "Cyberpunk", "steam", true))),
        ];
        assert_eq!(next_by_initial(&tiles, None, 'a'), Some(1));
        assert_eq!(next_by_initial(&tiles, Some(1), 'b'), Some(2));
        assert_eq!(next_by_initial(&tiles, Some(2), 'a'), Some(1));

        let (down, col) = move_grid_focus_index(0, 12, 5, 2, &Key::Down, false, None).unwrap();
        assert_eq!(down, 5);
        assert_eq!(col, Some(0));
        let (end, _) = move_grid_focus_index(0, 12, 5, 2, &Key::End, true, None).unwrap();
        assert_eq!(end, 11);
    }

    #[test]
    fn select_mode_shift_range_and_bulk_folder() {
        let mut s = AppState::with_demo_games();
        s.enter_select_mode();
        let first = s.games[0].id.clone();
        let third = s.games[2].id.clone();
        s.toggle_pick_game(&first, false);
        assert!(s.lib.picked.contains(&first));
        s.toggle_pick_game(&third, true);
        assert!(s.lib.picked.len() >= 3);
        s.bulk_add_picked_to_folder(None);
        assert!(!s.lib.selecting);
        assert!(s.lib.folder_sheet.is_some());
        assert!(s.lib.folder_sheet.as_ref().unwrap().picked.len() >= 3);
    }

    #[test]
    fn wp2_push_events_dispatch_through_registry() {
        let mut s = AppState::with_demo_games();
        let reg = s.events.clone();
        let changed = reg.dispatch(
            &mut s,
            HostEvent::GiftsChanged(StoreGifts {
                ok: true,
                steam_signed_in: true,
                gifts: vec![StoreGift {
                    store: "steam".into(),
                    id: "gift-1".into(),
                    title: "Hades II".into(),
                    sender: Some("Alex".into()),
                    ..Default::default()
                }],
            }),
        );
        assert!(changed);
        assert_eq!(s.visible_gifts().len(), 1);

        assert!(reg.dispatch(
            &mut s,
            HostEvent::GameOptimizeHeld(exo_rpc::GameOptimizeEvent {
                title: "Cyberpunk 2077".into(),
            })
        ));
        assert!(s.lib.optimize_held_titles.contains("Cyberpunk 2077"));

        assert!(reg.dispatch(
            &mut s,
            HostEvent::GameOptimizedAgain(exo_rpc::GameOptimizeEvent {
                title: "Cyberpunk 2077".into(),
            })
        ));
        assert!(!s.lib.optimize_held_titles.contains("Cyberpunk 2077"));
        assert!(
            s.notices
                .iter()
                .any(|n| n.text.contains("optimized it again"))
        );
    }

    #[test]
    fn crop_fit_clamps_within_image_bounds() {
        let (cw, ch) = crop_size(1200.0, 900.0, 1.0);
        assert!((cw - 600.0).abs() < 0.01);
        assert!((ch - 900.0).abs() < 0.01);
        let (z, x, y) = fit_crop(1200.0, 900.0, 2.0, -50.0, 9999.0);
        assert_eq!(z, 2.0);
        assert_eq!(x, 0.0);
        assert!((y - 450.0).abs() < 0.01);
    }

    #[test]
    fn home_deck_extras_debounce_flushes_after_240ms() {
        let mut s = AppState::with_demo_games();
        s.schedule_home_deck_extras("demo:cyberpunk".into());
        assert_eq!(
            s.lib.home_extras_pending_id.as_deref(),
            Some("demo:cyberpunk")
        );
        assert!(!s.tick_library(0.10));
        assert_eq!(
            s.lib.home_extras_pending_id.as_deref(),
            Some("demo:cyberpunk")
        );
        assert!(s.tick_library(0.30));
        assert!(s.lib.home_extras_pending_id.is_none());
        assert_eq!(s.lib.home_extras_due_secs, 0.0);
    }
}
