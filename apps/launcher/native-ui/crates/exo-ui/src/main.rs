#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod boot;
mod captures;
mod clients_gate;
mod devices_audio;
mod devices_controller;
mod discord;
mod discord_admin;
mod discord_chat;
mod discord_chat_state;
mod discord_md;
mod discord_pickers;
mod discord_search;
mod discord_settings;
mod discord_state;
mod discord_threads;
mod discord_voice;
mod discord_voice_state;
mod downloads;
mod events;
mod folders;
mod frame_cap_row;
mod frame_gen_row;
mod game_actions;
mod game_page;
mod game_perf;
mod game_props;
mod gamepad;
mod gifts;
mod gpu_tuning;
mod hud_overlay;
mod hw_sensors;
mod image_cache;
mod island;
mod key_modal;
mod library;
mod library_state;
mod macros;
mod media_state;
mod menus;
mod music;
mod music_pages;
mod music_sound;
mod native_autopilot;
mod notes;
mod notice;
mod palette;
mod pc;
mod pc_actions;
mod pc_ai;
mod pc_apps;
mod pc_devices;
mod pc_hdr;
mod pc_menus;
mod pc_state;
mod pc_storage;
mod pc_wp9_actions;
mod power_guard;
mod report;
mod select_bar;
mod settings;
mod settings_account;
mod settings_privacy;
mod settings_state;
mod settings_stores;
mod setup;
mod shell;
mod snip_editor;
mod state;
mod store;
mod store_engine;
mod store_media;
mod store_page;
mod video_decoder;
mod whats_new;

use crate::state::AppState;
use exo_kit::{FONT_FAMILIES, FONTS, exo_titlebar};
use rune::tray::{Tray, TrayEvent, TrayIcon, TrayItem, TraySpec};
use rune::{App, AppSpec, Key, KeyEvent, Tx, Waker};
use std::time::Duration;

struct CliArgs {
    pipe: Option<String>,
    parity_probe: Option<String>,
    probe_points: Option<String>,
    native_autopilot: bool,
    present_hud: Option<String>,
    present_hud_secs: Option<u64>,
    passthrough: Vec<String>,
}

fn split_cli_args() -> CliArgs {
    let mut pipe = None;
    let mut parity_probe = None;
    let mut probe_points = None;
    let mut native_autopilot = false;
    let mut present_hud = None;
    let mut present_hud_secs = None;
    let mut passthrough = Vec::new();
    let mut iter = std::env::args();
    if let Some(exe) = iter.next() {
        passthrough.push(exe);
    }
    while let Some(arg) = iter.next() {
        if arg == "--pipe" {
            pipe = iter.next();
        } else if let Some(name) = arg.strip_prefix("--pipe=") {
            pipe = Some(name.to_string());
        } else if arg == "--parity-probe" {
            parity_probe = iter.next();
        } else if let Some(path) = arg.strip_prefix("--parity-probe=") {
            parity_probe = Some(path.to_string());
        } else if arg == "--probe-points" {
            probe_points = iter.next();
        } else if let Some(path) = arg.strip_prefix("--probe-points=") {
            probe_points = Some(path.to_string());
        } else if arg == "--native-autopilot" {
            native_autopilot = true;
        } else if arg == "--present-hud" {
            present_hud = iter.next();
        } else if let Some(name) = arg.strip_prefix("--present-hud=") {
            present_hud = Some(name.to_string());
        } else if arg == "--present-hud-secs" {
            present_hud_secs = iter.next().and_then(|v| v.parse().ok());
        } else {
            passthrough.push(arg);
        }
    }
    CliArgs {
        pipe,
        parity_probe,
        probe_points,
        native_autopilot,
        present_hud,
        present_hud_secs,
        passthrough,
    }
}

fn step_discord_channel(s: &mut AppState, dir: isize, unread_only: bool) -> bool {
    if let Some(gid) = s.discord.active_guild_id.clone()
        && let Some(list) = s.discord.channels_by_guild.get(&gid)
    {
        let targets: Vec<String> = list
            .channels
            .iter()
            .filter(|c| c.kind != "category" && c.can_read != Some(false))
            .filter(|c| !unread_only || c.unread || c.mentions > 0)
            .map(|c| c.id.clone())
            .collect();
        if targets.is_empty() {
            return false;
        }
        let cur = s
            .discord
            .active_channel_id
            .as_deref()
            .and_then(|id| targets.iter().position(|c| c == id))
            .unwrap_or(0);
        let next = (cur as isize + dir).rem_euclid(targets.len() as isize) as usize;
        let next_id = targets[next].clone();
        s.open_discord_channel(&next_id);
        return true;
    }
    let dms: Vec<String> = s
        .discord
        .home
        .dms
        .iter()
        .filter(|d| !unread_only || d.unread || d.mentions > 0)
        .map(|d| d.id.clone())
        .collect();
    if dms.is_empty() {
        return false;
    }
    let cur = s
        .discord
        .active_dm_id
        .as_deref()
        .and_then(|id| dms.iter().position(|d| d == id))
        .unwrap_or(0);
    let next = (cur as isize + dir).rem_euclid(dms.len() as isize) as usize;
    let next_id = dms[next].clone();
    s.open_discord_dm(&next_id);
    true
}

fn key_to_vk(key: &Key) -> Option<u32> {
    match key {
        Key::Escape => Some(27),
        Key::Enter => Some(13),
        Key::Tab => Some(9),
        Key::Backspace => Some(8),
        Key::Delete => Some(46),
        Key::Left => Some(37),
        Key::Up => Some(38),
        Key::Right => Some(39),
        Key::Down => Some(40),
        Key::Home => Some(36),
        Key::End => Some(35),
        Key::PageUp => Some(33),
        Key::PageDown => Some(34),
        Key::Function(n) if (1..=24).contains(n) => Some(111 + u32::from(*n)),
        Key::Text(t) => {
            let ch = t.chars().next()?.to_ascii_uppercase();
            if ch == ' ' {
                Some(32)
            } else if ch.is_ascii_alphanumeric() {
                Some(ch as u32)
            } else {
                match ch {
                    ';' => Some(186),
                    '=' => Some(187),
                    ',' => Some(188),
                    '-' => Some(189),
                    '.' => Some(190),
                    '/' => Some(191),
                    '`' => Some(192),
                    '[' => Some(219),
                    '\\' => Some(220),
                    ']' => Some(221),
                    '\'' => Some(222),
                    _ => None,
                }
            }
        }
        _ => None,
    }
}

fn on_key(s: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    if s.discord.settings_ext.recording_keybind.is_some()
        && let Some(vk) = key_to_vk(&ev.key)
    {
        return s.record_discord_keybind_vk(vk);
    }
    if s.driver_install_overlay_visible() {
        if ev.key == Key::Escape {
            return s.dismiss_top_layer();
        }
        return true;
    }
    if s.whats_new.sheet_open || s.whats_new.mode.is_some() {
        if ev.key == Key::Escape {
            return s.dismiss_top_layer();
        }
        return true;
    }
    if ev.is_command('k') && !ev.mods.alt && !ev.mods.shift {
        tx.instant();
        if s.palette_open {
            s.close_palette();
        } else {
            s.open_palette(tx);
        }
        return true;
    }
    if ev.is_command(',') && !ev.mods.alt && !ev.mods.shift {
        tx.instant();
        s.toggle_settings();
        return true;
    }
    let modal_open = s.palette_open || s.key_modal_open || s.downloads.open;
    if !modal_open && !ev.mods.alt && !ev.mods.shift {
        for (idx, ch) in ['1', '2', '3', '4', '5', '6', '7'].into_iter().enumerate() {
            if ev.is_command(ch) {
                tx.instant();
                s.switch_room(state::Room::NAV_TABS[idx]);
                return true;
            }
        }
    }
    if !modal_open && ev.is_command('f') && !ev.mods.alt && !ev.mods.shift {
        match s.room {
            state::Room::Library => {
                s.library_search_open = true;
                tx.focus("library-search-input", false);
                return true;
            }
            state::Room::Store => {
                tx.focus("store-search-input", false);
                return true;
            }
            state::Room::Discord => {
                s.discord.chat.drawer = discord_chat_state::ChatDrawer::Search;
                tx.focus("dc-search-input", false);
                return true;
            }
            _ => {}
        }
    }
    if !modal_open
        && ev.is_command('a')
        && !ev.mods.alt
        && !ev.mods.shift
        && s.room == state::Room::Library
        && s.open_game_id.is_none()
        && !s.library_search_open
    {
        s.library_all_games = true;
        return true;
    }
    if !modal_open
        && ev.mods.shift
        && !ev.mods.alt
        && (s.room == state::Room::Discord || s.island_in_call())
    {
        if ev.is_command('m') {
            s.toggle_discord_mute();
            return true;
        }
        if ev.is_command('d') {
            s.toggle_discord_deafen();
            return true;
        }
    }
    if !modal_open && ev.mods.alt && !ev.mods.ctrl && !ev.mods.cmd {
        match ev.key {
            Key::Left => {
                if s.open_game_id.is_some() {
                    s.close_game_page();
                    return true;
                }
                if s.store_game.is_some() {
                    s.close_store_game();
                    return true;
                }
                return s.navigate_history(-1);
            }
            Key::Right => {
                return s.navigate_history(1);
            }
            Key::Up if s.room == state::Room::Discord => {
                return step_discord_channel(s, -1, ev.mods.shift);
            }
            Key::Down if s.room == state::Room::Discord => {
                return step_discord_channel(s, 1, ev.mods.shift);
            }
            _ => {}
        }
    }
    if !modal_open
        && ev.mods.shift
        && !ev.mods.ctrl
        && !ev.mods.alt
        && ev.key == Key::Escape
        && s.room == state::Room::Discord
        && let Some(gid) = s.discord.active_guild_id.clone()
    {
        s.discord_mark_guild_read(&gid);
        return true;
    }
    if !modal_open
        && s.room == state::Room::Music
        && !ev.mods.ctrl
        && !ev.mods.alt
        && !ev.mods.cmd
        && matches!(&ev.key, Key::Text(t) if t == " ")
    {
        s.music_control("toggle");
        return true;
    }
    if s.palette_open {
        return palette::handle_palette_key(s, ev, tx);
    }
    if s.handle_captures_key(ev, tx) {
        return true;
    }
    if s.setup.beat.is_some() {
        match ev.key {
            Key::Escape => {
                s.finish_tour();
                return true;
            }
            Key::Left => {
                s.tour_back();
                return true;
            }
            Key::Right | Key::Enter => {
                s.tour_next();
                return true;
            }
            _ => {}
        }
    }
    if library_state::handle_library_key(s, ev, tx) {
        return true;
    }
    if s.store_ext.lightbox_index.is_some() {
        match ev.key {
            Key::Escape => {
                s.store_close_lightbox();
                return true;
            }
            Key::Left => {
                s.store_step_lightbox(-1);
                return true;
            }
            Key::Right => {
                s.store_step_lightbox(1);
                return true;
            }
            _ => {}
        }
    }
    if music::handle_key(s, ev, tx) {
        return true;
    }
    if ev.key == Key::Escape {
        if s.room == state::Room::Discord && s.dismiss_discord_chat_layer() {
            return true;
        }
        return s.dismiss_top_layer();
    }
    if ev.key == Key::Up
        && s.room == state::Room::Discord
        && s.discord.chat.composer.text.is_empty()
        && s.discord.chat.editing.is_none()
        && s.discord.chat.apps.active_command.is_none()
    {
        s.begin_edit_last_own_message();
        return s.discord.chat.editing.is_some();
    }
    false
}

fn setup_state(s: &mut AppState, waker: Waker) {
    s.install_waker(waker);
    s.notify_shell_painted_once();
    s.preload_visible_covers_sync(1.0);
    if s.room == state::Room::Discord {
        s.preload_discord_images_sync(1.0);
    }
}

fn poll_state(s: &mut AppState, now: f64, tx: &mut Tx) -> bool {
    let mut focus_changed = false;
    if s.palette_open && s.palette_needs_focus {
        s.palette_needs_focus = false;
        tx.focus("palette-input", false);
        focus_changed = true;
    }
    let notices = s.sync_and_tick_notices(now, tx);
    let setup = s.setup_tick(now);
    let island = s.tick_island_timer(now, tx);
    let lib = s.tick_library(now);
    s.tick_audio_eq_polling(now);
    s.tick_controller_watch(now);
    s.tick_notes_autosave(now);
    let store = s.store_tick(now);
    let wp4 = s.wp4_tick(now);
    let hb = s.tick_shell_heartbeat(now, tx);
    let wp9 = s.pc_wp9_tick(now);
    let music = s.music_tick(now);
    let pg = s.tick_power_guard(now);
    let hw = s.tick_hw_sensors(now);
    focus_changed
        || notices
        || setup
        || island
        || lib
        || store
        || wp4
        || hb
        || wp9
        || music
        || pg
        || hw
}

#[cfg(windows)]
fn settle_setup_host(client: &exo_rpc::RpcClient, state: &mut AppState) {
    let timeout = Duration::from_secs(8);
    if let Ok(acct) = client.call_blocking::<_, exo_rpc::ExoAccountState>(
        exo_rpc::METHOD_EXO_ACCOUNT_STATE,
        &serde_json::json!({}),
        timeout,
    ) {
        state.pc.account = acct;
    }
    if let Ok(about) = client.call_blocking::<_, exo_rpc::SystemAbout>(
        exo_rpc::METHOD_SYSTEM_ABOUT,
        &serde_json::json!({}),
        timeout,
    ) {
        state.pc.system_about = Some(about);
    }
    if let Ok(drv) = client.call_blocking::<_, exo_rpc::DrivesReply>(
        exo_rpc::METHOD_SYSTEM_DRIVES,
        &serde_json::json!({ "space": false }),
        timeout,
    ) {
        state.pc.drives = Some(drv);
    }
    if let Ok(t) = client.call_blocking::<_, exo_rpc::SystemTuningState>(
        exo_rpc::METHOD_SYSTEM_TUNING,
        &serde_json::json!({}),
        timeout,
    ) {
        state.pc.tuning = Some(t);
    }
    if let Ok(hdr) = client.call_blocking::<_, exo_rpc::HdrState>(
        exo_rpc::METHOD_HDR_STATE,
        &serde_json::json!({}),
        timeout,
    ) {
        state.pc.hdr = Some(hdr);
    }
    if let Ok(ai) = client.call_blocking::<_, exo_rpc::ExoAiState>(
        exo_rpc::METHOD_EXO_AI_STATE,
        &serde_json::json!({}),
        timeout,
    ) {
        state.pc.exo_ai = Some(ai);
    }
    if let Ok(backup) = client.call_blocking::<_, exo_rpc::BackupStatus>(
        exo_rpc::METHOD_BACKUP_STATUS,
        &serde_json::json!({}),
        timeout,
    ) {
        state.pc.backup = Some(backup);
    }
    if let Ok(clients) = client.call_blocking::<_, exo_rpc::ClientsStatus>(
        exo_rpc::METHOD_CLIENTS_STATUS,
        &serde_json::json!({}),
        timeout,
    ) {
        state.pc.clients = Some(clients);
    }
    if let Ok(gp) =
        client.call_blocking::<_, serde_json::Value>("gpu.profile", &serde_json::json!({}), timeout)
    {
        let (gpu_name, screen) = pc_actions::parse_gpu_profile_fields(&gp);
        state.pc.gpu_short_name = gpu_name;
        state.pc.screen_label = screen;
    }
}

#[cfg(windows)]
fn load_live_state_sync(pipe: &str, scene: Option<&str>) -> AppState {
    let mut state = AppState::for_host_pipe();
    let Ok(client) = exo_rpc::RpcClient::connect(pipe, Duration::from_secs(6), |_| {}) else {
        state.status = state::HostStatus::Error("Connect failed".into());
        state.refreshing = false;
        return state;
    };
    if let Ok(boot) = client.call_blocking::<_, exo_rpc::ShellBootstrap>(
        exo_rpc::METHOD_SHELL_BOOTSTRAP,
        &serde_json::json!({}),
        Duration::from_secs(12),
    ) {
        state.apply_bootstrap(boot);
        state.whats_new.boot_hold_active = false;
        state.whats_new.answered = true;
    }
    if let Ok(badge) = client.call_blocking::<_, serde_json::Value>(
        "friends.badge",
        &serde_json::json!({}),
        Duration::from_secs(3),
    ) && let Some(count) = badge.get("count").and_then(|v| v.as_f64())
        && count.is_finite()
    {
        state.chrome.discord_badge_count = count.max(0.0).floor() as u32;
    }
    if let Ok(win) = client.call_blocking::<_, serde_json::Value>(
        "shell.windowState",
        &serde_json::json!({}),
        Duration::from_secs(3),
    ) {
        if let Some(max) = win.get("maximized").and_then(|v| v.as_bool()) {
            state.chrome.window_maximized = max;
        }
        state.chrome.window_maximize_hover = win
            .get("maximizeHover")
            .and_then(|v| v.as_bool())
            .unwrap_or(false);
    }
    if scene == Some("downloads")
        && let Ok(dl) = client.call_blocking::<_, serde_json::Value>(
            "downloads.list",
            &serde_json::json!({}),
            Duration::from_secs(4),
        )
    {
        state.downloads.list = Some(downloads::DownloadsList::from_json(&dl));
    }
    if let Ok(st) = client.call_blocking::<_, exo_rpc::CaptureStatusReply>(
        exo_rpc::METHOD_CAPTURE_STATUS,
        &serde_json::json!({}),
        Duration::from_secs(5),
    ) {
        state.media.capture_status = Some(st);
    }
    if scene.is_some_and(|s| s.starts_with("setup")) {
        settle_setup_host(&client, &mut state);
    }
    match scene {
        Some(sc) if sc.starts_with("store") => {
            if let Ok(home) = client.call_blocking::<_, exo_rpc::StoreHome>(
                exo_rpc::METHOD_STORE_HOME,
                &serde_json::json!({}),
                Duration::from_secs(10),
            ) && home.ok
            {
                let mut urls = Vec::new();
                for (i, h) in home.hero.iter().take(6).enumerate() {
                    if let Some(a) = h.art.as_ref() {
                        if i == 0
                            && let Some(u) = a.best_hero()
                        {
                            urls.push(u.to_string());
                        }
                        if let Some(u) = a
                            .capsule
                            .as_deref()
                            .or(a.header.as_deref())
                            .or_else(|| a.best_hero())
                        {
                            urls.push(u.to_string());
                        }
                    }
                }
                for shelf in home.shelves.iter().take(2) {
                    for tile in shelf.items.iter().take(12) {
                        if let Some(u) = tile.art.as_ref().and_then(|a| a.best_poster()) {
                            urls.push(u.to_string());
                        }
                    }
                }
                if let Some(ref cache_dir) = state.remote_cache_dir {
                    for url in urls {
                        let _ = exo_rpc::remote_art::fetch_cached_image(&url, cache_dir);
                    }
                    if let Some(first_hero) = home.hero.first() {
                        let logo = exo_rpc::StoreArt::steam_url(&first_hero.app_id, "logo");
                        let _ = exo_rpc::remote_art::fetch_cached_image(&logo, cache_dir);
                    }
                }
                state.store_ensure_shelf_slots(home.shelves.len());
                state.store_home = Some(home);
            }
            if let Ok(wish) = client.call_blocking::<_, exo_rpc::StoreWishlistReply>(
                exo_rpc::METHOD_STORE_WISHLIST,
                &serde_json::json!({ "action": "get" }),
                Duration::from_secs(10),
            ) && wish.message.is_none()
            {
                if let Some(ref cache_dir) = state.remote_cache_dir {
                    for tile in wish.items.iter().take(12) {
                        let url = tile
                            .art
                            .as_ref()
                            .and_then(|a| a.best_poster())
                            .map(ToString::to_string)
                            .unwrap_or_else(|| {
                                exo_rpc::StoreArt::steam_url(&tile.app_id, "poster")
                            });
                        let _ = exo_rpc::remote_art::fetch_cached_image(&url, cache_dir);
                    }
                }
                state.store_ext.wishlist = wish.items;
                state.store_ext.wishlist_loaded = true;
            }
            if sc == "store-search"
                && let Ok(reply) = client.call_blocking::<_, exo_rpc::StoreSearchReply>(
                    exo_rpc::METHOD_STORE_SEARCH,
                    &serde_json::json!({ "query": "elden", "typed": true }),
                    Duration::from_secs(10),
                )
                && reply.ok
            {
                if let Some(ref cache_dir) = state.remote_cache_dir {
                    for tile in reply.items.iter().take(12) {
                        let url = tile
                            .art
                            .as_ref()
                            .and_then(|a| a.best_poster())
                            .map(ToString::to_string)
                            .unwrap_or_else(|| {
                                exo_rpc::StoreArt::steam_url(&tile.app_id, "poster")
                            });
                        let _ = exo_rpc::remote_art::fetch_cached_image(&url, cache_dir);
                    }
                }
                state.store_search_results = Some(reply.items);
                state.store_ext.search_query_sent = "elden".into();
            }
            if sc == "store-game" || sc == "store-lightbox" {
                let seed_tile = state
                    .store_home
                    .as_ref()
                    .and_then(|h| h.hero.first().cloned());
                let app_id = seed_tile
                    .as_ref()
                    .map(|t| t.app_id.clone())
                    .unwrap_or_else(|| "1091500".into());
                state.store_ext.seed = seed_tile;
                if let Ok(g) = client.call_blocking::<_, exo_rpc::StoreGame>(
                    exo_rpc::METHOD_STORE_GAME,
                    &serde_json::json!({ "appId": app_id }),
                    Duration::from_secs(10),
                ) && g.ok
                {
                    if let Some(ref cache_dir) = state.remote_cache_dir {
                        let logo = exo_rpc::StoreArt::steam_url(&g.app_id, "logo");
                        let _ = exo_rpc::remote_art::fetch_cached_image(&logo, cache_dir);
                        if let Some(ref a) = g.art
                            && let Some(u) = a.best_hero()
                        {
                            let _ = exo_rpc::remote_art::fetch_cached_image(u, cache_dir);
                        }
                        if let Some(ref hdr) = g.header {
                            let _ = exo_rpc::remote_art::fetch_cached_image(hdr, cache_dir);
                        }
                        for m in g.media.iter().take(4) {
                            let _ = exo_rpc::remote_art::fetch_cached_image(&m.thumb, cache_dir);
                        }
                        for (i, shot) in g.screenshots.iter().take(8).enumerate() {
                            if i == 0 {
                                let _ = exo_rpc::remote_art::fetch_cached_image(shot, cache_dir);
                            }
                            let small = store_media::thumb_of_screenshot(shot);
                            let _ = exo_rpc::remote_art::fetch_cached_image(&small, cache_dir);
                        }
                    }
                    state.store_game = Some(g);
                    state.store_sync_trailer_for_game();
                    if sc == "store-lightbox" {
                        state.store_open_lightbox(0);
                    }
                }
                if let Ok(offers) = client.call_blocking::<_, exo_rpc::StoreOtherOffers>(
                    exo_rpc::METHOD_STORE_GAME_OFFERS,
                    &serde_json::json!({ "appId": app_id }),
                    Duration::from_secs(10),
                ) && offers.ok
                {
                    state.store_offers = Some(offers);
                }
                if let Ok(rev) = client.call_blocking::<_, exo_rpc::StoreRecentReviews>(
                    exo_rpc::METHOD_STORE_GAME_REVIEWS,
                    &serde_json::json!({ "appId": app_id }),
                    Duration::from_secs(10),
                ) && rev.ok
                {
                    state.store_ext.recent_reviews = Some(rev);
                }
            }
        }
        Some(sc) if sc == "game" || sc.starts_with("game-") => {
            if let Ok(set) = client.call_blocking::<_, exo_rpc::LauncherSettings>(
                exo_rpc::METHOD_SETTINGS_GET,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.settings = set;
            }
            if let Some(gid) = state
                .selected_game_id
                .clone()
                .or_else(|| state.games.first().map(|g| g.id.clone()))
            {
                state.open_game_id = Some(gid.clone());
                if let Ok(ext) = client.call_blocking::<_, exo_rpc::GameExtras>(
                    exo_rpc::METHOD_GAME_EXTRAS,
                    &serde_json::json!({ "id": gid }),
                    Duration::from_secs(6),
                ) {
                    state.game_extras = Some(ext);
                }
                if let Ok(props) = client.call_blocking::<_, exo_rpc::GameProperties>(
                    exo_rpc::METHOD_GAME_PROPERTIES,
                    &serde_json::json!({ "id": gid }),
                    Duration::from_secs(6),
                ) {
                    state.props_launch_opts.text = props.launch_options.clone().unwrap_or_default();
                    state.props_launch_opts.sync_invariants();
                    state.game_props = Some(props);
                }
                if let Ok(hdr) = client.call_blocking::<_, exo_rpc::HdrState>(
                    exo_rpc::METHOD_HDR_STATE,
                    &serde_json::json!({}),
                    Duration::from_secs(5),
                ) {
                    state.pc.hdr = Some(hdr);
                }
                if let Ok(dlss) = client.call_blocking::<_, exo_rpc::DlssStatus>(
                    exo_rpc::METHOD_DLSS_STATUS,
                    &serde_json::json!({ "id": gid }),
                    Duration::from_secs(8),
                ) {
                    state.game_page.dlss_loading = false;
                    state.game_page.dlss = Some(dlss);
                }
                if let Ok(fg) = client.call_blocking::<_, exo_rpc::FrameGenMod>(
                    exo_rpc::METHOD_DLSS_FRAME_GEN,
                    &serde_json::json!({ "id": gid }),
                    Duration::from_secs(6),
                ) {
                    state.game_page.frame_gen = Some(fg);
                }
                if let Ok(fg) = client.call_blocking::<_, exo_rpc::GameFrameGen>(
                    exo_rpc::METHOD_GAME_FRAME_GEN,
                    &serde_json::json!({ "id": gid }),
                    Duration::from_secs(6),
                ) {
                    state.game_frame_gen = Some(fg);
                }
                if let Ok(gs) = client.call_blocking::<_, exo_rpc::GameSettingsState>(
                    exo_rpc::METHOD_GAME_SETTINGS,
                    &serde_json::json!({ "id": gid }),
                    Duration::from_secs(6),
                ) {
                    if let Some(ref wm) = gs.window_mode {
                        state.game_display_mode = wm.clone();
                    }
                    state.game_page.settings = Some(gs);
                }
                if let Ok(ks) = client.call_blocking::<_, exo_rpc::KeepStatus>(
                    exo_rpc::METHOD_KEEP_STATUS,
                    &serde_json::json!({}),
                    Duration::from_secs(5),
                ) {
                    state.game_page.keep_status = Some(ks);
                }
                if let Ok(sh) = client.call_blocking::<_, exo_rpc::ShaderStatus>(
                    exo_rpc::METHOD_SHADERS_STATUS,
                    &serde_json::json!({ "id": gid }),
                    Duration::from_secs(5),
                ) {
                    state.game_page.shaders = Some(sh);
                }
                if let Ok(hg) = client.call_blocking::<_, exo_rpc::HdrGame>(
                    exo_rpc::METHOD_HDR_GAME,
                    &serde_json::json!({ "id": gid }),
                    Duration::from_secs(5),
                ) {
                    state.game_page.hdr_game = Some(hg);
                }
                if let Ok(gg) = client.call_blocking::<_, exo_rpc::GpuGame>(
                    exo_rpc::METHOD_GPU_GAME,
                    &serde_json::json!({ "id": gid }),
                    Duration::from_secs(5),
                ) {
                    state.game_page.gpu_game = Some(gg);
                }
                if let Ok(comp) = client.call_blocking::<_, exo_rpc::CompressStateReply>(
                    exo_rpc::METHOD_COMPRESS_STATE,
                    &serde_json::json!({ "id": gid, "estimate": false }),
                    Duration::from_secs(5),
                ) {
                    state.game_page.compress = comp.compress;
                }
                if let Ok(prog) = client.call_blocking::<_, exo_rpc::InstallProgress>(
                    exo_rpc::METHOD_GAME_PROGRESS,
                    &serde_json::json!({ "id": gid }),
                    Duration::from_secs(3),
                ) && prog.is_active
                {
                    state.install_progress = Some(prog);
                }
            }
        }
        Some("library-all") | Some("all-games") | Some("library-more") | Some("library-search") => {
            if matches!(scene, Some("library-all") | Some("all-games")) {
                state.library_all_games = true;
            }
            if let Some(ref cache_dir) = state.remote_cache_dir {
                let urls: Vec<String> = state
                    .filtered_games()
                    .into_iter()
                    .take(48)
                    .filter(|g| state.art_base.resolve_local_portrait(g).is_none())
                    .filter_map(|g| g.cover_url.as_deref())
                    .filter(|u| u.starts_with("https://"))
                    .map(str::to_string)
                    .collect();
                for url in urls {
                    let _ = exo_rpc::remote_art::fetch_cached_image(&url, cache_dir);
                }
            }
        }
        Some(sc) if sc == "settings" || sc.starts_with("settings-") || sc == "palette" => {
            if let Ok(set) = client.call_blocking::<_, exo_rpc::LauncherSettings>(
                exo_rpc::METHOD_SETTINGS_GET,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.settings = set;
            }
            if let Ok(acct) = client.call_blocking::<_, exo_rpc::ExoAccountState>(
                exo_rpc::METHOD_EXO_ACCOUNT_STATE,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.account = acct;
            }
            if let Ok(hdr) = client.call_blocking::<_, exo_rpc::HdrState>(
                exo_rpc::METHOD_HDR_STATE,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.hdr = Some(hdr);
            }
            if let Ok(about) = client.call_blocking::<_, exo_rpc::SystemAbout>(
                exo_rpc::METHOD_SYSTEM_ABOUT,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.system_about = Some(about);
            }
            if let Ok(status) = client.call_blocking::<_, exo_rpc::BackupStatus>(
                exo_rpc::METHOD_BACKUP_STATUS,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                if let Some(ref note) = status.note
                    && !note.sentence.is_empty()
                {
                    state.wp4.backup_note = Some(note.sentence.clone());
                }
                state.pc.backup = Some(status);
            }
            if let Ok(stores) = client.call_blocking::<_, Vec<exo_rpc::StoreStatus>>(
                exo_rpc::METHOD_STORES_MATRIX,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                state.wp4.stores = stores;
            }
            if let Ok(accounts) = client.call_blocking::<_, exo_rpc::StoreAccounts>(
                exo_rpc::METHOD_ACCOUNTS_LIST,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                state.wp4.store_accounts = Some(accounts);
            }
            if let Ok(steam) = client.call_blocking::<_, exo_rpc::SteamAccountReply>(
                exo_rpc::METHOD_STEAM_ACCOUNT,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.wp4.steam_account = Some(steam);
            }
            if let Ok(sh) = client.call_blocking::<_, exo_rpc::ShadersAddonStatus>(
                exo_rpc::METHOD_SHADERS_ADDON,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.wp4.shaders_present = sh.present;
            }
            if let Ok(ov) = client.call_blocking::<_, exo_rpc::ExoAccountOverview>(
                exo_rpc::METHOD_EXO_ACCOUNT_OVERVIEW,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                state.wp4.account_overview = Some(ov);
            }
            if let Ok(wp) = client.call_blocking::<_, exo_rpc::WebClientOptions>(
                exo_rpc::METHOD_WEB_PREFERENCES_GET,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.wp4.web_prefs = Some(wp.clone());
                state.pc.web_preferences = Some(wp);
            }
            if let Ok(fps) = client.call_blocking::<_, exo_rpc::FpsCounterReply>(
                exo_rpc::METHOD_FPS_COUNTER,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.fps_counter_state = Some(fps);
            }
            if let Ok(upd) = client.call_blocking::<_, exo_rpc::UpdateCheckReply>(
                exo_rpc::METHOD_APP_CHECK_UPDATE,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.update_check = Some(upd);
            }
            if state.wp4.riot_draft.text.is_empty()
                && let Some(ref id) = state.settings.riot_id
                && !id.trim().is_empty()
            {
                state.wp4.riot_draft = rune::TextEdit::new(id.trim());
                state.wp4.riot_editing = false;
            } else if state
                .settings
                .riot_id
                .as_deref()
                .unwrap_or("")
                .trim()
                .is_empty()
            {
                state.wp4.riot_editing = true;
            }
        }
        Some(sc) if sc == "pc" || sc.starts_with("pc-menu-") => {
            if let Ok(ai) = client.call_blocking::<_, exo_rpc::ExoAiState>(
                exo_rpc::METHOD_EXO_AI_STATE,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                state.pc.exo_ai = Some(ai);
            }
            if let Ok(tune) = client.call_blocking::<_, exo_rpc::AutoTuneState>(
                exo_rpc::METHOD_SYSTEM_TUNE,
                &serde_json::json!({ "action": "status" }),
                Duration::from_secs(6),
            ) {
                state.pc.auto_tune = Some(tune);
            }
            if let Ok(ap) = client.call_blocking::<_, exo_rpc::AutopilotState>(
                exo_rpc::METHOD_AUTOPILOT_STATE,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                state.pc.autopilot = Some(ap);
            }
            if let Ok(t) = client.call_blocking::<_, exo_rpc::SystemTuningState>(
                exo_rpc::METHOD_SYSTEM_TUNING,
                &serde_json::json!({}),
                Duration::from_secs(8),
            ) {
                state.pc.tuning = Some(t);
            }
            if let Ok(h) = client.call_blocking::<_, exo_rpc::PcHealth>(
                exo_rpc::METHOD_SYSTEM_HEALTH,
                &serde_json::json!({}),
                Duration::from_secs(8),
            ) {
                state.pc.health = Some(h);
            }
            if let Ok(ir) = client.call_blocking::<_, exo_rpc::InterruptsState>(
                exo_rpc::METHOD_SYSTEM_INTERRUPTS,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                state.pc.interrupts = Some(ir);
            }
            if let Ok(hdr) = client.call_blocking::<_, exo_rpc::HdrState>(
                exo_rpc::METHOD_HDR_STATE,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.hdr = Some(hdr);
            }
            if let Ok(tuning) = client.call_blocking::<_, exo_rpc::GpuManualTuningState>(
                exo_rpc::METHOD_GPU_TUNING_STATE,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.gpu_tuning_ui.sync_from_host(tuning, true);
            }
            if let Ok(v) = client.call_blocking::<_, serde_json::Value>(
                exo_rpc::METHOD_SHADERS_ADDON,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                let present = v.get("present").and_then(|x| x.as_bool()).unwrap_or(false);
                if let Some(snap_val) = v.get("snapshot") {
                    state.pc.shader_snap_games = pc_actions::parse_shader_snap_games(snap_val);
                }
                state.pc.shaders = Some(exo_rpc::ShadersAddonStatus {
                    ok: v.get("ok").and_then(|x| x.as_bool()).unwrap_or(true),
                    present,
                    state: v.get("state").and_then(|x| x.as_str()).map(str::to_string),
                    message: v
                        .get("message")
                        .and_then(|x| x.as_str())
                        .map(str::to_string),
                    snapshot: None,
                });
                if present
                    && state.pc.shader_snap_games.is_empty()
                    && let Ok(sv) = client.call_blocking::<_, serde_json::Value>(
                        exo_rpc::METHOD_SHADERS_CALL,
                        &serde_json::json!({ "method": "snapshot", "params": {} }),
                        Duration::from_secs(12),
                    )
                    && let Some(res_val) = sv.get("result")
                {
                    state.pc.shader_snap_games = pc_actions::parse_shader_snap_games(res_val);
                }
            }
            if let Ok(rb) = client.call_blocking::<_, exo_rpc::ShaderRebuild>(
                exo_rpc::METHOD_SHADERS_REBUILD,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.shaders_rebuild = Some(rb);
            }
            if let Ok(est) = client.call_blocking::<_, exo_rpc::ShadersEstimatesReply>(
                exo_rpc::METHOD_SHADERS_ESTIMATES,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) && est.ok
            {
                state.pc.shader_estimates = est.seconds;
            }
            if let Ok(sg) = client.call_blocking::<_, exo_rpc::ShadersPrepareGamesReply>(
                exo_rpc::METHOD_SHADERS_PREPARE_GAMES,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                state.pc.shader_games = sg.games;
            }
            if let Ok(about) = client.call_blocking::<_, exo_rpc::SystemAbout>(
                exo_rpc::METHOD_SYSTEM_ABOUT,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.system_about = Some(about);
            }
            if let Ok(gp) = client.call_blocking::<_, serde_json::Value>(
                exo_rpc::METHOD_GPU_PROFILE,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                let (gpu_name, screen) = pc_actions::parse_gpu_profile_fields(&gp);
                state.pc.gpu_short_name = gpu_name;
                state.pc.screen_label = screen;
                if let Ok(parsed) = serde_json::from_value::<exo_rpc::GpuProfile>(gp) {
                    state.pc.gpu_profile = Some(parsed);
                }
            }
            if let Ok(gd) = client.call_blocking::<_, exo_rpc::DriverState>(
                exo_rpc::METHOD_GPU_DRIVER,
                &serde_json::json!({ "action": "status" }),
                Duration::from_secs(6),
            ) {
                state.pc.gpu_driver = Some(gd);
            }
            if let Ok(disp) = client.call_blocking::<_, exo_rpc::DisplayState>(
                exo_rpc::METHOD_GPU_DISPLAY,
                &serde_json::json!({ "action": "state" }),
                Duration::from_secs(6),
            ) {
                state.pc.display_state = Some(disp);
            }
            if let Ok(af) = client.call_blocking::<_, exo_rpc::Afmf3State>(
                exo_rpc::METHOD_GPU_AFMF3,
                &serde_json::json!({ "action": "status" }),
                Duration::from_secs(5),
            ) {
                state.pc.afmf3 = Some(af);
            }
            if let Ok(ks) = client.call_blocking::<_, exo_rpc::KeepStatus>(
                exo_rpc::METHOD_KEEP_STATUS,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.keep_status = Some(ks);
            }
            if let Ok(ds) = client.call_blocking::<_, exo_rpc::DriversState>(
                exo_rpc::METHOD_SYSTEM_DRIVERS,
                &serde_json::json!({ "action": "status" }),
                Duration::from_secs(8),
            ) {
                state.pc.drivers_state = Some(ds);
            }
            if let Ok(bu) = client.call_blocking::<_, exo_rpc::BiosUpdateReply>(
                exo_rpc::METHOD_BIOS_UPDATE,
                &serde_json::json!({ "action": "status" }),
                Duration::from_secs(6),
            ) {
                state.pc.bios_staged = bu.staged;
            }
            if let Ok(rt) = client.call_blocking::<_, exo_rpc::RuntimesListReply>(
                exo_rpc::METHOD_RUNTIMES_LIST,
                &serde_json::json!({}),
                Duration::from_secs(8),
            ) && rt.ok
            {
                state.pc.runtimes = Some(rt.items);
            }
            if let Ok(pkgs) = client.call_blocking::<_, exo_rpc::AppsPackagesReply>(
                exo_rpc::METHOD_APPS_PACKAGES,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) && pkgs.ok
            {
                state.pc.windows_packages = pkgs.items;
            }
            if let Ok(cl) = client.call_blocking::<_, serde_json::Value>(
                exo_rpc::METHOD_COMPRESS_LIST,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                let (potential, saved) = pc_actions::parse_compress_totals(&cl);
                state.pc.compress_potential_bytes = potential;
                state.pc.compress_saved_bytes = saved;
                if let Ok(parsed) = serde_json::from_value::<exo_rpc::CompressList>(cl) {
                    state.pc.compress_list = Some(parsed);
                }
            }
            if let Ok(drv) = client.call_blocking::<_, exo_rpc::DrivesReply>(
                exo_rpc::METHOD_SYSTEM_DRIVES,
                &serde_json::json!({ "space": false }),
                Duration::from_secs(8),
            ) {
                state.pc.drives = Some(drv);
            }
            if let Ok(gp) = client.call_blocking::<_, exo_rpc::GpuProcessesState>(
                exo_rpc::METHOD_GPU_PROCESSES,
                &serde_json::json!({ "limit": 8 }),
                Duration::from_secs(6),
            ) {
                state.pc.gpu_processes = Some(gp);
            }
            if let Ok(qos) = client.call_blocking::<_, exo_rpc::QosState>(
                exo_rpc::METHOD_SYSTEM_QOS,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                state.pc.qos_state = Some(qos);
            }
            if let Ok(ov) = client.call_blocking::<_, exo_rpc::OverlayAdvisorState>(
                exo_rpc::METHOD_SYSTEM_OVERLAYS,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                state.pc.overlays_state = Some(ov);
            }
            state.refresh_auto_tune_stages_if_idle();
            if let Some(ref cache_dir) = state.remote_cache_dir {
                let urls: Vec<String> = state
                    .pc
                    .shader_snap_games
                    .iter()
                    .filter_map(|sg| {
                        state
                            .games
                            .iter()
                            .find(|g| g.id == sg.id || g.title.eq_ignore_ascii_case(&sg.name))
                    })
                    .filter(|g| state.art_base.resolve_local_portrait(g).is_none())
                    .filter_map(|g| g.cover_url.as_deref())
                    .filter(|u| u.starts_with("https://"))
                    .map(str::to_string)
                    .collect();
                for url in urls {
                    let _ = exo_rpc::remote_art::fetch_cached_image(&url, cache_dir);
                }
            }
        }
        Some(sc) if sc == "pc-apps" || sc.starts_with("pc-apps-") => {
            if let Ok(list) = client.call_blocking::<_, exo_rpc::AppsListReply>(
                exo_rpc::METHOD_APPS_LIST,
                &serde_json::json!({}),
                Duration::from_secs(12),
            ) {
                let mut by_size: Vec<&exo_rpc::InstalledAppItem> = list.programs.iter().collect();
                by_size.sort_by_key(|a| std::cmp::Reverse(a.bytes.unwrap_or(0)));
                let ids: Vec<String> = by_size
                    .into_iter()
                    .take(100)
                    .map(|p| p.id.clone())
                    .collect();
                if !ids.is_empty()
                    && let Ok(ic) = client.call_blocking::<_, exo_rpc::AppsIconsReply>(
                        exo_rpc::METHOD_APPS_ICONS,
                        &serde_json::json!({ "ids": ids }),
                        Duration::from_secs(12),
                    )
                {
                    for (k, v) in ic.icons {
                        if let Some(d) = v {
                            state.pc.app_icons.insert(k, d);
                        }
                    }
                }
                state.pc.apps_list = Some(list);
            }
            if let Ok(st) = client.call_blocking::<_, exo_rpc::StartupListReply>(
                exo_rpc::METHOD_STARTUP_LIST,
                &serde_json::json!({}),
                Duration::from_secs(10),
            ) {
                state.pc.startup_list = Some(st);
            }
            if let Ok(tidy) = client.call_blocking::<_, exo_rpc::AppFind>(
                exo_rpc::METHOD_APPS_TIDY,
                &serde_json::json!({}),
                Duration::from_secs(10),
            ) && tidy.ok
                && !tidy.items.is_empty()
            {
                let picks = tidy
                    .items
                    .iter()
                    .filter(|i| i.selected)
                    .map(|i| i.id)
                    .collect();
                state.pc.apps_tidy = Some(pc_state::RemovedAppEntry {
                    id: "tidy".into(),
                    name: "Left by programs that are gone".into(),
                    find: Some(tidy),
                    message: None,
                    cleared_line: None,
                    undo_token: None,
                    undone: false,
                    review_open: false,
                    picks,
                    error: None,
                });
            }
            if let Ok(j) = client.call_blocking::<_, exo_rpc::JunkScanReply>(
                exo_rpc::METHOD_JUNK_SCAN,
                &serde_json::json!({}),
                Duration::from_secs(8),
            ) && j.ok
            {
                state.pc.junk_items = Some(j.items);
            }
            if let Ok(ic) = client.call_blocking::<_, exo_rpc::InstallerCacheReply>(
                exo_rpc::METHOD_APPS_INSTALLER_CACHE,
                &serde_json::json!({ "action": "scan" }),
                Duration::from_secs(8),
            ) && ic.ok
            {
                state.pc.installer_cache = Some(ic);
            }
            if let Ok(pkgs) = client.call_blocking::<_, exo_rpc::AppsPackagesReply>(
                exo_rpc::METHOD_APPS_PACKAGES,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) && pkgs.ok
            {
                state.pc.windows_packages = pkgs.items;
            }
        }
        Some("pc-storage") => {
            if let Ok(drv) = client.call_blocking::<_, exo_rpc::DrivesReply>(
                exo_rpc::METHOD_SYSTEM_DRIVES,
                &serde_json::json!({ "space": true }),
                Duration::from_secs(12),
            ) {
                state.pc.drives = Some(drv);
            }
            if let Ok(cl) = client.call_blocking::<_, exo_rpc::CompressList>(
                exo_rpc::METHOD_COMPRESS_LIST,
                &serde_json::json!({}),
                Duration::from_secs(8),
            ) {
                state.pc.compress_potential_bytes = cl.potential;
                state.pc.compress_saved_bytes = cl.saved;
                state.pc.compress_list = Some(cl);
            }
        }
        Some(sc)
            if sc == "pc-devices" || sc.starts_with("pc-devices-") || sc.starts_with("setup") =>
        {
            if let Ok(eq) = client.call_blocking::<_, exo_rpc::EqStateReply>(
                exo_rpc::METHOD_EQ,
                &serde_json::json!({ "action": "state" }),
                Duration::from_secs(6),
            ) {
                if let Some(ref hp) = eq.headphone {
                    let look = if hp.form == "over-ear" {
                        "headphones"
                    } else {
                        "iem"
                    };
                    if let Ok(reply) = client.call_blocking::<_, exo_rpc::DevicesImageReply>(
                        exo_rpc::METHOD_DEVICES_IMAGE,
                        &serde_json::json!({
                            "kind": "audio",
                            "model": look,
                            "name": hp.name,
                        }),
                        Duration::from_secs(8),
                    ) && let Some(url) = reply.url
                    {
                        state.pc.device_images.insert("audio:worn".into(), url);
                    }
                }
                state.apply_eq_state(eq);
            }
            if let Ok(list) = client.call_blocking::<_, exo_rpc::DevicesListReply>(
                exo_rpc::METHOD_DEVICES_LIST,
                &serde_json::json!({}),
                Duration::from_secs(8),
            ) {
                for dev in &list.devices {
                    if let Ok(reply) = client.call_blocking::<_, exo_rpc::DevicesImageReply>(
                        exo_rpc::METHOD_DEVICES_IMAGE,
                        &serde_json::json!({
                            "kind": dev.kind,
                            "name": dev.name,
                            "model": dev.model,
                            "maker": dev.maker.as_deref().or(dev.brand.as_deref()),
                            "color": dev.color,
                            "layout": dev.layout,
                        }),
                        Duration::from_secs(8),
                    ) && let Some(url) = reply.url
                    {
                        state.pc.device_images.insert(dev.id.clone(), url);
                    }
                }
                let out_id = list
                    .devices
                    .iter()
                    .find(|d| d.kind == "audio" && d.role.as_deref() == Some("output"))
                    .or_else(|| {
                        list.devices
                            .iter()
                            .find(|d| d.kind == "audio" && d.role.as_deref() != Some("input"))
                    })
                    .map(|d| d.id.clone());
                let mic_id = list
                    .devices
                    .iter()
                    .find(|d| d.kind == "audio" && d.role.as_deref() == Some("input"))
                    .map(|d| d.id.clone());
                let kb_id = list
                    .devices
                    .iter()
                    .find(|d| d.kind == "keyboard" && d.lab)
                    .or_else(|| list.devices.iter().find(|d| d.kind == "keyboard"))
                    .map(|d| d.id.clone());
                let pad_id = list
                    .devices
                    .iter()
                    .find(|d| d.kind == "controller")
                    .map(|d| d.id.clone());
                if let Ok(aud) = client.call_blocking::<_, exo_rpc::AudioReadReply>(
                    exo_rpc::METHOD_AUDIO_READ,
                    &serde_json::json!({ "output": out_id, "mic": mic_id }),
                    Duration::from_secs(5),
                ) {
                    state.pc.audio = Some(aud);
                }
                if let Some(ref oid) = out_id
                    && let Ok(fmt) = client.call_blocking::<_, exo_rpc::AudioFormatsReply>(
                        exo_rpc::METHOD_AUDIO_FORMATS,
                        &serde_json::json!({ "id": oid }),
                        Duration::from_secs(5),
                    )
                {
                    state.pc.audio_ui.output_formats = Some(fmt);
                }
                if let Some(ref mid) = mic_id
                    && let Ok(fmt) = client.call_blocking::<_, exo_rpc::AudioFormatsReply>(
                        exo_rpc::METHOD_AUDIO_FORMATS,
                        &serde_json::json!({ "id": mid }),
                        Duration::from_secs(5),
                    )
                {
                    state.pc.audio_ui.mic_formats = Some(fmt);
                }
                if let Some(ref pid) = pad_id {
                    if let Ok(cread) = client.call_blocking::<_, exo_rpc::ControllerReadReply>(
                        exo_rpc::METHOD_CONTROLLER_READ,
                        &serde_json::json!({ "id": pid }),
                        Duration::from_secs(5),
                    ) {
                        state.pc.controller_ui.read_id = Some(pid.clone());
                        if let Some(ref st) = cread.settings {
                            state.pc.controller_ui.pad_settings = st.clone();
                        }
                        if let Some(sound) = cread.sound {
                            state.pc.controller_ui.sound_on = sound;
                        }
                        state.pc.controller_ui.read = Some(cread);
                    }
                    if let Ok(gp) = client.call_blocking::<_, exo_rpc::ControllerGameProfilesReply>(
                        exo_rpc::METHOD_CONTROLLER_GAME_PROFILES,
                        &serde_json::json!({}),
                        Duration::from_secs(5),
                    ) {
                        state.pc.controller_ui.game_profiles = gp.profiles;
                    }
                    if let Ok(si) = client.call_blocking::<_, exo_rpc::SteamInputStateReply>(
                        exo_rpc::METHOD_STEAM_INPUT_STATE,
                        &serde_json::json!({}),
                        Duration::from_secs(5),
                    ) {
                        state.pc.controller_ui.steam_input = Some(si);
                    }
                }
                state.pc.audio_output_id = out_id;
                state.pc.audio_mic_id = mic_id;
                state.pc.keyboard_id = kb_id;
                state.pc.devices = Some(list);
                if scene == Some("pc-devices-mouse") {
                    let probe = client.call_blocking::<_, exo_rpc::ActionResult>(
                        exo_rpc::METHOD_HID_SEND_REPORT,
                        &serde_json::json!({ "device": "probe", "reportId": 0, "data": "" }),
                        Duration::from_secs(2),
                    );
                    let refusal = match probe {
                        Ok(r) if !r.ok => r.message.filter(|m| m.contains("test instance")),
                        Err(e) => {
                            let s = e.to_string();
                            s.contains("test instance").then(|| {
                                s.rsplit_once(": ")
                                    .map(|(_, tail)| tail.to_string())
                                    .unwrap_or(s)
                            })
                        }
                        _ => None,
                    };
                    let err_msg = if refusal.is_some() {
                        refusal
                    } else {
                        match client.call_blocking::<_, exo_rpc::ActionResult>(
                            exo_rpc::METHOD_MOUSE_PANEL_OPEN,
                            &serde_json::json!({
                                "ownerHwnd": 0,
                                "x": 0.0,
                                "y": 56.0,
                                "width": 1920.0,
                                "height": 1024.0,
                                "visible": true,
                            }),
                            Duration::from_secs(3),
                        ) {
                            Ok(r) if r.ok => None,
                            Ok(r) => Some(
                                r.message
                                    .unwrap_or_else(|| "Exo Mouse could not open.".into()),
                            ),
                            Err(e) => Some(e.to_string()),
                        }
                    };
                    state.pc.mouse_panel_loading = false;
                    state.pc.mouse_panel_error = err_msg;
                }
            }
            let kb_payload = match state.pc.keyboard_id.as_deref() {
                Some(id) => serde_json::json!({ "id": id }),
                None => serde_json::json!({}),
            };
            let kb_res = client.call_blocking::<_, exo_rpc::KeyboardReadReply>(
                exo_rpc::METHOD_KEYBOARD_READ,
                &kb_payload,
                Duration::from_secs(5),
            );
            state.apply_keyboard_read_reply(kb_res.map_err(|e| e.to_string()));
            if let Ok(b) = client.call_blocking::<_, exo_rpc::KeyboardBackupsReply>(
                exo_rpc::METHOD_KEYBOARD_BACKUPS,
                &kb_payload,
                Duration::from_secs(5),
            ) {
                state.pc.keyboard_backups = b.backups;
            }
        }
        Some(sc) if sc == "music" || sc.starts_with("music-") => {
            if let Ok(set) = client.call_blocking::<_, exo_rpc::LauncherSettings>(
                exo_rpc::METHOD_SETTINGS_GET,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.settings = set;
                state.sync_music_service_from_settings();
            }
            if let Ok(pb) = client.call_blocking::<_, exo_rpc::MusicPlaybackReply>(
                exo_rpc::METHOD_MUSIC_PLAYBACK,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.apply_music_playback(pb.playback);
            }
            if let Ok(vol) = client.call_blocking::<_, exo_rpc::MusicVolumeReply>(
                exo_rpc::METHOD_MUSIC_VOLUME,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                if let Some(lvl) = vol.level {
                    state.media.music_volume = lvl;
                }
                state.media.music_muted = vol.muted;
            }
            if let Some(start_m) = state.media.music_service.start_method() {
                let mut running = false;
                for _ in 0..24 {
                    match client.call_blocking::<_, exo_rpc::PlayerStartReply>(
                        start_m,
                        &serde_json::json!({}),
                        Duration::from_secs(6),
                    ) {
                        Ok(reply) if reply.ok && reply.state == "running" => {
                            state.media.music_room_state = media_state::MusicRoomState::Running;
                            running = true;
                            break;
                        }
                        Ok(reply) if reply.state == "loading" => {
                            state.media.music_room_state = media_state::MusicRoomState::Loading;
                            std::thread::sleep(Duration::from_millis(500));
                        }
                        Ok(reply) if reply.state == "signedOut" => {
                            state.media.music_room_state = media_state::MusicRoomState::SignedOut;
                            break;
                        }
                        Ok(reply) if reply.state == "notPremium" => {
                            state.media.music_room_state = media_state::MusicRoomState::NotPremium;
                            break;
                        }
                        Ok(reply) => {
                            state.media.music_room_state = media_state::MusicRoomState::Error(
                                reply.message.unwrap_or_default(),
                            );
                            break;
                        }
                        Err(_) => break,
                    }
                }
                if running && let Some(view_m) = state.media.music_service.view_method() {
                    if let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                        view_m,
                        &serde_json::json!({ "view": "library" }),
                        Duration::from_secs(8),
                    ) && r.ok
                        && let Some(data) = r.data
                    {
                        state.media.music_library = data.items;
                        state.media.music_library_loaded = true;
                        if !data.filters.is_empty() {
                            state.media.music_library_top_filters = data.filters.clone();
                            state.media.music_library_filters = data.filters;
                        }
                        if !data.sorts.is_empty() {
                            state.media.music_library_sorts = data.sorts;
                        }
                        if let Some(f) = data.filter {
                            state.media.music_library_filter = f;
                        }
                        if let Some(so) = data.sort {
                            state.media.music_library_sort = so;
                        }
                    }
                    if let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                        view_m,
                        &serde_json::json!({ "view": "home" }),
                        Duration::from_secs(10),
                    ) && r.ok
                        && let Some(data) = r.data
                    {
                        state.media.music_home_sections = data.sections;
                    }
                    if state.media.music_service.has_recent()
                        && let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                            view_m,
                            &serde_json::json!({ "view": "recent" }),
                            Duration::from_secs(8),
                        )
                        && r.ok
                        && let Some(data) = r.data
                    {
                        state.media.music_recent = data.items;
                    }
                    if let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerStateReply>(
                        view_m,
                        &serde_json::json!({ "view": "player" }),
                        Duration::from_secs(6),
                    ) && r.ok
                        && let Some(data) = r.data
                    {
                        state.media.apply_player_state_data(&data);
                    }
                    if sc == "music-songs"
                        && let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                            view_m,
                            &serde_json::json!({ "view": "songs" }),
                            Duration::from_secs(8),
                        )
                        && r.ok
                        && let Some(data) = r.data
                    {
                        let len = data.tracks.len() as u32;
                        state.media.music_songs_tracks = data.tracks;
                        state.media.music_songs_total = data.total.unwrap_or(0).max(len);
                        state.media.music_songs_context_uri = data.context_uri.unwrap_or_default();
                        state.media.music_songs_sorts = data.sorts;
                        if let Some(so) = data.sort {
                            state.media.music_songs_sort = so;
                        }
                        state.media.music_songs_liked = data.liked.unwrap_or(false);
                        state.media.music_songs_loaded = true;
                    }
                    if sc == "music-queue" {
                        if let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                            view_m,
                            &serde_json::json!({ "view": "queue" }),
                            Duration::from_secs(8),
                        ) && r.ok
                            && let Some(data) = r.data
                        {
                            state.media.music_queue_data = Some(data);
                        }
                        if state.media.music_service.has_history()
                            && let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                                view_m,
                                &serde_json::json!({ "view": "history" }),
                                Duration::from_secs(6),
                            )
                            && r.ok
                            && let Some(data) = r.data
                        {
                            state.media.music_queue_history = data.tracks;
                        }
                    }
                    if (sc == "music-now-playing" || sc == "music-immersion")
                        && !state.media.music_track_uri.is_empty()
                        && let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                            view_m,
                            &serde_json::json!({ "view": "lyrics", "uri": state.media.music_track_uri }),
                            Duration::from_secs(6),
                        )
                        && r.ok
                        && let Some(data) = r.data
                    {
                        state.media.music_lyrics_uri = state.media.music_track_uri.clone();
                        state.media.music_lyrics_lines = data.lines;
                        state.media.music_lyrics_synced = data.synced.unwrap_or(false);
                    }
                    if sc == "music-devices"
                        && let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                            view_m,
                            &serde_json::json!({ "view": "devices" }),
                            Duration::from_secs(6),
                        )
                        && r.ok
                        && let Some(data) = r.data
                    {
                        state.media.music_devices = data.devices;
                        state.media.music_devices_loaded = true;
                    }
                    if sc == "music-friends"
                        && let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                            view_m,
                            &serde_json::json!({ "view": "friends" }),
                            Duration::from_secs(6),
                        )
                        && r.ok
                        && let Some(data) = r.data
                    {
                        state.media.music_friends = data.friends;
                        state.media.music_friends_loaded = true;
                    }
                    if sc == "music-jam"
                        && state.media.music_service.has_jam()
                        && let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                            view_m,
                            &serde_json::json!({ "view": "jam" }),
                            Duration::from_secs(6),
                        )
                        && r.ok
                        && let Some(data) = r.data
                    {
                        state.media.music_jam_session = data.session;
                        state.media.music_jam_experience = data.experience;
                        state.media.music_jam_loaded = true;
                    }
                    if sc == "music-sound" {
                        if state.media.music_service.has_audio()
                            && let Ok(r) = client.call_blocking::<_, exo_rpc::SpotifyAudioReply>(
                                view_m,
                                &serde_json::json!({ "view": "audio" }),
                                Duration::from_secs(6),
                            )
                            && r.ok
                        {
                            state.media.music_sound_spotify = r.data;
                        } else if state.media.music_service.has_quality()
                            && let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                                view_m,
                                &serde_json::json!({ "view": "audio" }),
                                Duration::from_secs(6),
                            )
                            && r.ok
                            && let Some(data) = r.data
                        {
                            state.media.music_sound_qualities = data.qualities;
                            state.media.music_sound_quality = data.quality.unwrap_or_default();
                        }
                    }
                    if let Some(ref cache_dir) = state.remote_cache_dir {
                        let mut urls: Vec<String> = Vec::new();
                        let mut push_url = |u: &str| {
                            if !u.is_empty() && !urls.iter().any(|x| x == u) {
                                urls.push(u.to_string());
                            }
                        };
                        if let Some(ref pb) = state.media.playback
                            && let Some(ref u) = pb.artwork_url
                        {
                            push_url(u);
                        }
                        for c in state.media.music_library.iter().take(16) {
                            push_url(&c.image);
                        }
                        for c in state.media.music_recent.iter().take(8) {
                            push_url(&c.image);
                        }
                        for sec in state.media.music_home_sections.iter().take(4) {
                            for c in sec.items.iter().take(8) {
                                push_url(&c.image);
                            }
                        }
                        let chunks = urls.chunks((urls.len() / 6).max(1));
                        std::thread::scope(|scope| {
                            for chunk in chunks {
                                scope.spawn(move || {
                                    for url in chunk {
                                        let _ =
                                            exo_rpc::remote_art::fetch_cached_image(url, cache_dir);
                                    }
                                });
                            }
                        });
                    }
                }
            }
        }
        Some(sc) if sc == "captures" || sc.starts_with("captures-") || sc == "capture-clip" => {
            if let Ok(set) = client.call_blocking::<_, exo_rpc::LauncherSettings>(
                exo_rpc::METHOD_SETTINGS_GET,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.settings = set;
            }
            if let Ok(acct) = client.call_blocking::<_, exo_rpc::ExoAccountState>(
                exo_rpc::METHOD_EXO_ACCOUNT_STATE,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.account = acct;
            }
            if let Ok(hdr) = client.call_blocking::<_, exo_rpc::HdrState>(
                exo_rpc::METHOD_HDR_STATE,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.hdr = Some(hdr);
            }
            if let Ok(about) = client.call_blocking::<_, exo_rpc::SystemAbout>(
                exo_rpc::METHOD_SYSTEM_ABOUT,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.system_about = Some(about);
            }
            if let Ok(st) = client.call_blocking::<_, exo_rpc::CaptureStatusReply>(
                exo_rpc::METHOD_CAPTURE_STATUS,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.media.capture_status = Some(st);
            }
            if let Ok(list) = client.call_blocking::<_, exo_rpc::CaptureListReply>(
                exo_rpc::METHOD_CAPTURE_LIST,
                &serde_json::json!({ "limit": 60 }),
                Duration::from_secs(8),
            ) {
                state.media.capture_items = list.items;
            }
            if let Ok(nl) = client.call_blocking::<_, exo_rpc::NotesListReply>(
                exo_rpc::METHOD_NOTES_LIST,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.media.notes.notes = Some(nl.notes);
            }
        }
        Some(sc) if sc.starts_with("discord") => {
            if let Ok(settings) = client.call_blocking::<_, exo_rpc::LauncherSettings>(
                exo_rpc::METHOD_SETTINGS_GET,
                &serde_json::json!({}),
                Duration::from_secs(4),
            ) {
                state.settings = settings;
            }
            for _ in 0..80 {
                if let Ok(st) = client.call_blocking::<_, exo_rpc::DiscordStatus>(
                    exo_rpc::METHOD_DISCORD_STATUS,
                    &serde_json::json!({ "connect": true }),
                    Duration::from_secs(5),
                ) {
                    let done = st.state == "ready" || st.state == "error" || !st.signed_in;
                    state.discord.status = st;
                    state.discord.loaded = true;
                    if done {
                        break;
                    }
                } else {
                    break;
                }
                std::thread::sleep(Duration::from_millis(250));
            }
            if let Ok(accs) = client.call_blocking::<_, exo_rpc::DiscordAccountsReply>(
                exo_rpc::METHOD_DISCORD_ACCOUNTS,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.discord.accounts = accs.accounts;
            }
            if state.discord.status.state == "ready" {
                if let Ok(home) = client.call_blocking::<_, exo_rpc::DiscordHome>(
                    exo_rpc::METHOD_DISCORD_HOME,
                    &serde_json::json!({}),
                    Duration::from_secs(6),
                ) {
                    state.discord.home = home;
                    state.discord.active_dm_id = state.default_dm_id();
                }
                if let Ok(fr) = client.call_blocking::<_, exo_rpc::DiscordFriendsReply>(
                    exo_rpc::METHOD_DISCORD_FRIENDS,
                    &serde_json::json!({}),
                    Duration::from_secs(6),
                ) {
                    state.discord.friends = fr.friends;
                    state.discord.blocked = fr.restricted.map(|r| r.blocked).unwrap_or_default();
                }
                if let Ok(unreads) = client.call_blocking::<_, exo_rpc::DiscordUnreadsReply>(
                    exo_rpc::METHOD_DISCORD_UNREADS,
                    &serde_json::json!({}),
                    Duration::from_secs(5),
                ) {
                    state.discord.unreads = unreads.channels;
                }
                if sc == "discord-messages" {
                    if let Some(def_dm) = state.default_dm_id() {
                        state.discord.active_dm_id = Some(def_dm);
                    }
                    if let Some(dm_id) = state.discord.active_dm_id.clone() {
                        if let Ok(rep) = client.call_blocking::<_, exo_rpc::DiscordMessagesReply>(
                            exo_rpc::METHOD_DISCORD_MESSAGES,
                            &serde_json::json!({ "channelId": dm_id, "limit": 40 }),
                            Duration::from_secs(6),
                        ) && let Some(mut msgs) = rep.messages
                        {
                            msgs.reverse();
                            state
                                .discord
                                .chat
                                .messages_by_channel
                                .insert(dm_id.clone(), msgs);
                            state.backfill_dm_line(&dm_id);
                        }
                        if let Some(uid) = state
                            .discord
                            .home
                            .dms
                            .iter()
                            .find(|d| d.id == dm_id && !d.group)
                            .and_then(|d| d.user_id.clone())
                        {
                            if let Ok(prof) = client
                                .call_blocking::<_, exo_rpc::DiscordProfileReply>(
                                    exo_rpc::METHOD_DISCORD_PROFILE,
                                    &serde_json::json!({ "userId": uid }),
                                    Duration::from_secs(6),
                                )
                                && prof.ok
                            {
                                state.discord.profile_note_user = Some(uid.clone());
                                state.discord.profile_note_input =
                                    rune::TextEdit::new(prof.note.as_deref().unwrap_or(""));
                                state
                                    .discord
                                    .chat
                                    .dm_profile_cache
                                    .insert(format!("{uid}:"), prof.clone());
                                state.discord.profiles_by_user.insert(uid.clone(), prof);
                            }
                            if let Ok(mf) = client.call_blocking::<_, exo_rpc::DiscordProfileReply>(
                                exo_rpc::METHOD_DISCORD_PROFILE,
                                &serde_json::json!({ "userId": uid, "mutualFriends": true }),
                                Duration::from_secs(6),
                            ) && let Some(friends) = mf.friends
                            {
                                state.discord.mutual_friends_by_user.insert(uid, friends);
                            }
                        }
                    }
                }
                if (sc == "discord-server" || sc == "discord-voice")
                    && let Some(gid) = state.discord.home.guilds.first().map(|g| g.id.clone())
                {
                    state.discord.active_guild_id = Some(gid.clone());
                    if let Ok(chans) = client.call_blocking::<_, exo_rpc::DiscordChannels>(
                        exo_rpc::METHOD_DISCORD_CHANNELS,
                        &serde_json::json!({ "guildId": gid }),
                        Duration::from_secs(6),
                    ) {
                        let first_cid = if sc == "discord-voice" {
                            chans
                                .channels
                                .iter()
                                .find(|c| matches!(c.kind.as_str(), "voice" | "stage"))
                                .or_else(|| chans.channels.iter().find(|c| c.kind != "category"))
                                .map(|c| c.id.clone())
                        } else {
                            chans
                                .channels
                                .iter()
                                .find(|c| c.kind == "text" && c.can_read != Some(false))
                                .or_else(|| chans.channels.iter().find(|c| c.kind != "category"))
                                .map(|c| c.id.clone())
                        };
                        state.sync_collapsed_categories(&chans);
                        state.discord.channels_by_guild.insert(gid.clone(), chans);
                        if let Some(cid) = first_cid {
                            state.discord.active_channel_id = Some(cid.clone());
                            if let Ok(rep) = client
                                .call_blocking::<_, exo_rpc::DiscordMessagesReply>(
                                    exo_rpc::METHOD_DISCORD_MESSAGES,
                                    &serde_json::json!({ "channelId": cid, "limit": 40 }),
                                    Duration::from_secs(6),
                                )
                                && let Some(msgs) = rep.messages
                            {
                                state.discord.chat.messages_by_channel.insert(cid, msgs);
                            }
                        }
                    }
                    if let Ok(ml) = client.call_blocking::<_, exo_rpc::DiscordMemberList>(
                        exo_rpc::METHOD_DISCORD_MEMBERS,
                        &serde_json::json!({ "guildId": gid }),
                        Duration::from_secs(6),
                    ) {
                        state.discord.members_by_guild.insert(gid, ml);
                    }
                    if sc == "discord-voice"
                        && let Ok(aud) = client.call_blocking::<_, exo_rpc::DiscordAudioReply>(
                            exo_rpc::METHOD_DISCORD_AUDIO,
                            &serde_json::json!({}),
                            Duration::from_secs(5),
                        )
                    {
                        state.discord.voice_ui.audio = Some(aud);
                    }
                }
                if let Some(ref cache_dir) = state.remote_cache_dir {
                    let urls = state.collect_discord_image_urls();
                    let missing: Vec<String> = urls
                        .into_iter()
                        .filter(|u| !exo_rpc::remote_art::cache_path_for(cache_dir, u).is_file())
                        .collect();
                    if !missing.is_empty() {
                        let workers = missing.len().min(8);
                        let chunk = missing.len().div_ceil(workers);
                        std::thread::scope(|scope| {
                            for slice in missing.chunks(chunk) {
                                let dir = cache_dir.clone();
                                scope.spawn(move || {
                                    for u in slice {
                                        let _ = exo_rpc::remote_art::fetch_cached_image(u, &dir);
                                    }
                                });
                            }
                        });
                    }
                }
            }
        }
        _ => {}
    }
    state.rpc = Some(client);
    state
}

fn tray_icon_rgba() -> TrayIcon {
    const SIZE: u32 = 16;
    let mut rgba = vec![0u8; (SIZE * SIZE * 4) as usize];
    for y in 0..SIZE {
        for x in 0..SIZE {
            let dx = x as f32 - 7.5;
            let dy = y as f32 - 7.5;
            let r2 = dx * dx + dy * dy;
            let idx = ((y * SIZE + x) * 4) as usize;
            if (16.0..=52.0).contains(&r2) {
                rgba[idx] = 255;
                rgba[idx + 1] = 255;
                rgba[idx + 2] = 255;
                rgba[idx + 3] = 255;
            }
        }
    }
    TrayIcon::rgba(SIZE, SIZE, rgba)
}

fn select_device_by_kind(s: &mut AppState, kind: &str) {
    s.switch_room(state::Room::Devices);
    s.switch_pc_tab(pc_state::PcTab::Devices);
    if let Some(id) =
        s.pc.devices
            .as_ref()
            .and_then(|d| d.devices.iter().find(|item| item.kind == kind))
            .map(|item| item.id.clone())
    {
        match kind {
            "mouse" => s.open_mouse_panel(id),
            "controller" => {
                s.pc.selected_device_id = Some(id.clone());
                s.open_controller_page(id);
            }
            "audio" => {
                s.pc.selected_device_id = Some(id);
                s.open_audio_device_page();
            }
            "keyboard" => {
                s.pc.selected_device_id = Some(id.clone());
                s.pc.keyboard_id = Some(id);
                if s.pc.keyboard.as_ref().is_none_or(|k| !k.ok) {
                    s.reload_keyboard();
                }
            }
            _ => {
                s.pc.selected_device_id = Some(id);
            }
        }
    }
}

fn open_game_props_tab(s: &mut AppState, tab: usize) {
    let target = s
        .open_game_id
        .clone()
        .or_else(|| s.selected_game_id.clone())
        .or_else(|| s.games.first().map(|g| g.id.clone()))
        .unwrap_or_else(|| "demo:cyberpunk".into());
    if s.open_game_id.as_deref() != Some(target.as_str()) {
        s.open_game_page(&target);
    } else {
        s.room = state::Room::Library;
    }
    s.props_open = true;
    s.game_props_tab = tab;
}

type SceneSetter = fn(&mut AppState);

const PARITY_SCENES: &[(&str, SceneSetter)] = &[
    ("palette", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.palette_open = true;
        s.palette_needs_focus = true;
    }),
    ("downloads", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.downloads.open = true;
        if s.status != state::HostStatus::Connected && s.downloads.list.is_none() {
            s.downloads.list = Some(downloads::DownloadsList {
                lines: vec![
                    downloads::DownloadLine {
                        game_id: "demo:cyberpunk".into(),
                        title: "Cyberpunk 2077".into(),
                        store: "steam".into(),
                        state: "running".into(),
                        can_pause: true,
                        auto_update: true,
                    },
                    downloads::DownloadLine {
                        game_id: "demo:elden".into(),
                        title: "Elden Ring".into(),
                        store: "steam".into(),
                        state: "queued".into(),
                        can_pause: false,
                        auto_update: true,
                    },
                    downloads::DownloadLine {
                        game_id: "demo:cs2".into(),
                        title: "Counter-Strike 2".into(),
                        store: "steam".into(),
                        state: "paused".into(),
                        can_pause: false,
                        auto_update: false,
                    },
                ],
                paused_by_player: false,
                held: None,
            });
            s.install_progress = Some(exo_rpc::InstallProgress {
                game_id: Some("demo:cyberpunk".into()),
                is_active: true,
                status: Some("Downloading".into()),
                percent: 46.0,
                bytes_per_second: 54_525_952,
                ..Default::default()
            });
        }
    }),
    ("whats-new", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::About;
        if s.whats_new.current_version.is_none() {
            s.whats_new.current_version = Some("3.29.0".into());
        }
        s.whats_new.mode = Some(whats_new::WhatsNewMode::All);
    }),
    ("update-sheet", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.whats_new.latest = Some("3.30.0".into());
        s.whats_new.not_now = true;
        s.whats_new.sheet_open = true;
    }),
    ("island", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.chrome.discord_badge_count = 3;
        s.discord.voice = exo_rpc::DiscordVoice {
            state: "connected".into(),
            guild_id: Some("g1".into()),
            channel_id: Some("c1".into()),
            muted: false,
            deafened: false,
            ..Default::default()
        };
        s.island.place_channel = Some("General".into());
        s.island.place_server = Some("Squad".into());
        s.island.call_started_at = Some(0.0);
    }),
    ("driver-install", |s: &mut AppState| {
        s.advance_driver_install(
            boot::DriverInstallKind::Gpu,
            boot::DriverInstallPhase::Downloading,
            Some("NVIDIA".into()),
            Some("572.16".into()),
            Some(0.42),
            None,
            None,
            None,
        );
    }),
    ("boot-error", |s: &mut AppState| {
        s.status = state::HostStatus::Error("Could not connect to Exo host.".into());
    }),
    ("game", |s: &mut AppState| {
        let target = s
            .open_game_id
            .clone()
            .or_else(|| s.selected_game_id.clone())
            .or_else(|| s.games.first().map(|g| g.id.clone()))
            .unwrap_or_else(|| "demo:cyberpunk".into());
        if s.open_game_id.as_deref() != Some(target.as_str()) {
            s.open_game_page(&target);
        } else {
            s.room = state::Room::Library;
        }
    }),
    ("game-page", |s: &mut AppState| {
        let target = if s.games.iter().any(|g| g.id == "demo:cyberpunk") {
            "demo:cyberpunk".to_string()
        } else {
            s.open_game_id
                .clone()
                .or_else(|| s.selected_game_id.clone())
                .or_else(|| s.games.first().map(|g| g.id.clone()))
                .unwrap_or_else(|| "demo:cyberpunk".into())
        };
        if s.open_game_id.as_deref() != Some(target.as_str()) {
            s.open_game_page(&target);
        } else {
            s.room = state::Room::Library;
        }
    }),
    ("game-props-files", |s: &mut AppState| {
        open_game_props_tab(s, 1);
    }),
    ("game-props-anticheat", |s: &mut AppState| {
        open_game_props_tab(s, 2);
    }),
    ("game-props-dlss", |s: &mut AppState| {
        open_game_props_tab(s, 3);
    }),
    ("game-props-betas", |s: &mut AppState| {
        open_game_props_tab(s, 2);
        s.game_page.props_tab_id = Some(game_actions::PropsSectionId::Betas);
    }),
    ("game-props-dlc", |s: &mut AppState| {
        open_game_props_tab(s, 3);
        s.game_page.props_tab_id = Some(game_actions::PropsSectionId::Content);
    }),
    ("game-props-artwork", |s: &mut AppState| {
        open_game_props_tab(s, 4);
        s.game_page.props_tab_id = Some(game_actions::PropsSectionId::Artwork);
    }),
    ("library-all", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.library_all_games = true;
    }),
    ("all-games", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.library_all_games = true;
    }),
    ("library-more", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.library_more_open = true;
    }),
    ("library-search", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.library_search_open = true;
    }),
    ("library-folders", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.library_all_games = true;
        let ordered: Vec<String> = s
            .filtered_games()
            .into_iter()
            .map(|g| g.id.clone())
            .collect();
        if s.settings.library_folders.is_empty() && ordered.len() >= 3 {
            s.settings.library_folders = vec![
                exo_rpc::LibraryFolder {
                    id: "f-favorites".into(),
                    name: "Favorites".into(),
                    color: Some("blue".into()),
                    sort: Some("folder".into()),
                    art: None,
                    games: vec![ordered[0].clone(), ordered[1].clone()],
                },
                exo_rpc::LibraryFolder {
                    id: "f-coop".into(),
                    name: "Co-op".into(),
                    color: Some("green".into()),
                    sort: Some("folder".into()),
                    art: None,
                    games: vec![ordered[2].clone()],
                },
            ];
        }
    }),
    ("library-folder-open", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.library_all_games = true;
        let ordered: Vec<String> = s
            .filtered_games()
            .into_iter()
            .map(|g| g.id.clone())
            .collect();
        if s.settings.library_folders.is_empty() && ordered.len() >= 2 {
            s.settings.library_folders = vec![exo_rpc::LibraryFolder {
                id: "f-favorites".into(),
                name: "Favorites".into(),
                color: Some("blue".into()),
                sort: Some("folder".into()),
                art: None,
                games: vec![ordered[0].clone(), ordered[1].clone()],
            }];
        }
        if let Some(first) = s.settings.library_folders.first().map(|f| f.id.clone()) {
            s.open_folder_panel(&first, false);
        }
    }),
    ("library-folder-sheet", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.library_all_games = true;
        let init: Vec<String> = s
            .filtered_games()
            .into_iter()
            .take(2)
            .map(|g| g.id.clone())
            .collect();
        s.open_new_folder_sheet(init);
    }),
    ("library-select", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.enter_select_mode();
        let picks: Vec<String> = s
            .filtered_games()
            .into_iter()
            .take(2)
            .map(|g| g.id.clone())
            .collect();
        for id in picks {
            s.lib.picked.insert(id);
        }
    }),
    ("library-gifts", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        if s.lib.gifts.is_empty() {
            s.lib.gifts = vec![exo_rpc::StoreGift {
                store: "steam".into(),
                id: "demo-gift-1".into(),
                title: "Hades II".into(),
                image: None,
                sender: Some("Alex".into()),
                message: Some("Happy birthday!".into()),
            }];
        }
        s.lib.gifts_open = true;
    }),
    ("library-empty", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.games.clear();
        s.refreshing = false;
        s.lib.library_error = None;
    }),
    ("library-error", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.games.clear();
        s.refreshing = false;
        s.lib.library_error = Some("Host connection failed".into());
    }),
    ("store", |s: &mut AppState| {
        s.switch_room(state::Room::Store)
    }),
    ("store-wishlist", |s: &mut AppState| {
        s.switch_room(state::Room::Store);
        s.store_tab = 1;
    }),
    ("store-search", |s: &mut AppState| {
        s.switch_room(state::Room::Store);
        s.store_query.text = "elden".into();
        s.store_query.sync_invariants();
    }),
    ("store-game", |s: &mut AppState| {
        s.room = state::Room::Store;
        if s.store_game.is_none() {
            s.open_store_game("1091500");
        }
    }),
    ("store-lightbox", |s: &mut AppState| {
        s.room = state::Room::Store;
        if s.store_game.is_none() {
            s.open_store_game("1091500");
        }
        s.store_open_lightbox(0);
    }),
    ("tour", |s: &mut AppState| s.start_tour()),
    ("setup-welcome", |s: &mut AppState| s.setup_go(0)),
    ("setup-accounts", |s: &mut AppState| s.setup_go(1)),
    ("setup-devices", |s: &mut AppState| s.setup_go(2)),
    ("setup-pc", |s: &mut AppState| s.setup_go(3)),
    ("setup-tour", |s: &mut AppState| s.setup_go(4)),
    ("setup-done", |s: &mut AppState| s.setup_go_complete()),
    ("setup-coach", |s: &mut AppState| s.start_tour()),
    ("activate-key", |s: &mut AppState| s.key_modal_open = true),
    ("settings", |s: &mut AppState| {
        s.switch_room(state::Room::Settings)
    }),
    ("settings-general", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::General;
    }),
    ("settings-account", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::Account;
    }),
    ("settings-appearance", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::Appearance;
    }),
    ("settings-graphics", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::Graphics;
    }),
    ("settings-updates", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::Updates;
    }),
    ("settings-about", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::About;
    }),
    ("settings-report", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::About;
        s.wp4_open_report();
    }),
    ("settings-steam-signin", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::Account;
        if s.wp4.stores.is_empty() {
            s.wp4.stores.push(exo_rpc::StoreStatus {
                store: "steam".into(),
                display_name: "Steam".into(),
                agent_present: true,
                client_present: Some(true),
                signed_in: true,
                ..Default::default()
            });
        }
        s.wp4.steam_panel_open = true;
        s.wp4.steam_step = Some(exo_rpc::SteamSignInStep {
            state: "waiting".into(),
            challenge_url: Some("https://s.team/q/1/1234567890123456".into()),
            interval_seconds: Some(5),
            ..Default::default()
        });
    }),
    ("settings-steam-password", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::Account;
        if s.wp4.stores.is_empty() {
            s.wp4.stores.push(exo_rpc::StoreStatus {
                store: "steam".into(),
                display_name: "Steam".into(),
                agent_present: true,
                client_present: Some(true),
                signed_in: true,
                ..Default::default()
            });
        }
        s.wp4.steam_panel_open = true;
        s.wp4.steam_mode = settings_state::SteamSignInMode::Password;
        s.wp4.steam_step = None;
        s.wp4.steam_user = rune::TextEdit::new("erix_demo");
        s.wp4.steam_pass = rune::TextEdit::new("hunter2secret").secure();
    }),
    ("settings-steam-guard", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.pc.settings_section = pc_state::SettingsSection::Account;
        if s.wp4.stores.is_empty() {
            s.wp4.stores.push(exo_rpc::StoreStatus {
                store: "steam".into(),
                display_name: "Steam".into(),
                agent_present: true,
                client_present: Some(true),
                signed_in: true,
                ..Default::default()
            });
        }
        s.wp4.steam_panel_open = true;
        s.wp4.steam_mode = settings_state::SteamSignInMode::Password;
        s.wp4.steam_step = Some(exo_rpc::SteamSignInStep {
            state: "guard".into(),
            ..Default::default()
        });
        s.wp4.steam_guard.set_value("A2B");
    }),
    ("account-sheet", |s: &mut AppState| {
        s.status = state::HostStatus::Connected;
        s.wp4.account_overview = Some(exo_rpc::ExoAccountOverview {
            signed_in: false,
            ..Default::default()
        });
        s.wp4.account_later_this_run = false;
    }),
    ("account-sheet-create", |s: &mut AppState| {
        s.status = state::HostStatus::Connected;
        s.wp4.account_overview = Some(exo_rpc::ExoAccountOverview {
            signed_in: false,
            ..Default::default()
        });
        s.wp4.account_later_this_run = false;
        s.wp4_flow_open(settings_state::AccountScreen::Create);
        s.wp4.flow_email = rune::TextEdit::new("player@example.com");
        s.wp4.flow_password = rune::TextEdit::new("correcthorsebatterystaple").secure();
    }),
    ("clients-gate", |s: &mut AppState| {
        s.status = state::HostStatus::Connected;
        s.settings.onboarding_complete = Some(true);
        s.pc.clients = Some(exo_rpc::ClientsStatus {
            ok: true,
            needs_fix: true,
            problem: None,
            epic: exo_rpc::ClientsEpic {
                layout: "old".into(),
                ..Default::default()
            },
            steam: exo_rpc::ClientsSteam {
                channel: "stable".into(),
            },
        });
    }),
    ("pc", |s: &mut AppState| s.switch_room(state::Room::ExoAi)),
    ("pc-menu-board", |s: &mut AppState| {
        s.switch_room(state::Room::ExoAi);
        s.pc.part_menu = Some(pc_state::PcPartMenu::Board);
    }),
    ("pc-menu-graphics", |s: &mut AppState| {
        s.switch_room(state::Room::ExoAi);
        s.pc.part_menu = Some(pc_state::PcPartMenu::Graphics);
    }),
    ("pc-gpu-tuning-nvidia", |s: &mut AppState| {
        s.switch_room(state::Room::ExoAi);
        s.pc.gpu_tuning_ui
            .sync_from_host(gpu_tuning::demo_nvidia_state(), true);
        s.pc.part_menu = Some(pc_state::PcPartMenu::Graphics);
    }),
    ("pc-gpu-tuning-amd", |s: &mut AppState| {
        s.switch_room(state::Room::ExoAi);
        s.pc.gpu_tuning_ui
            .sync_from_host(gpu_tuning::demo_amd_rdna3_state(), true);
        s.pc.part_menu = Some(pc_state::PcPartMenu::Graphics);
    }),
    ("pc-gpu-tuning-intel", |s: &mut AppState| {
        s.switch_room(state::Room::ExoAi);
        s.pc.gpu_tuning_ui
            .sync_from_host(gpu_tuning::demo_intel_arc_state(false), true);
        s.pc.part_menu = Some(pc_state::PcPartMenu::Graphics);
    }),
    ("pc-gpu-tuning-locked", |s: &mut AppState| {
        s.switch_room(state::Room::ExoAi);
        s.pc.gpu_tuning_ui
            .sync_from_host(gpu_tuning::demo_locked_laptop_state(), true);
        s.pc.part_menu = Some(pc_state::PcPartMenu::Graphics);
    }),
    ("pc-menu-display", |s: &mut AppState| {
        s.switch_room(state::Room::ExoAi);
        s.pc.part_menu = Some(pc_state::PcPartMenu::Display);
    }),
    ("pc-hdr-internal", |s: &mut AppState| {
        s.switch_room(state::Room::ExoAi);
        s.pc.hdr = Some(pc_hdr::demo_hdr_internal_state());
        s.pc.part_menu = Some(pc_state::PcPartMenu::Display);
    }),
    ("pc-hdr-external", |s: &mut AppState| {
        s.switch_room(state::Room::ExoAi);
        s.pc.hdr = Some(pc_hdr::demo_hdr_external_state());
        s.pc.part_menu = Some(pc_state::PcPartMenu::Display);
    }),
    ("game-hdr-cursor", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.pc.hdr = Some(pc_hdr::demo_hdr_external_state());
        s.game_page.hdr_game = Some(pc_hdr::demo_hdr_game_cursor_fix());
        if let Some(id) = s.games.first().map(|g| g.id.clone()) {
            s.open_game_id = Some(id);
        }
    }),
    ("discord-voice", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.discord.settings_open = true;
        s.discord.settings_page = crate::discord_state::DiscordSettingsPage::Voice;
        s.discord.settings_ext.client.noise_suppression = true;
        s.discord.settings_ext.client.noise_suppression_kind = Some("exo".into());
        s.discord.settings_ext.client.voice_tier = "auto".into();
        s.discord.settings_ext.client.voice_model_ready = Some(true);
        s.discord.settings_ext.client.voice_hq_model_ready = Some(true);
        s.discord.settings_ext.client.only_my_voice = true;
        s.discord.settings_ext.client.voice_isolation = true;
        s.discord.settings_ext.client.voice_isolation_ready = Some(true);
        s.discord.settings_ext.client.my_voice_learned = Some(true);
        s.discord.settings_ext.client.my_voice_seconds = Some(14.0);
        s.discord.settings_ext.mic_level = Some(exo_rpc::DiscordMicLevel {
            level: -28.0,
            gate_level: -22.0,
            after: -22.0,
            voiced: true,
            stages: exo_rpc::DiscordVoiceStages {
                profile: "exo".into(),
                suppression: "exo-v2".into(),
                suppression_limit_db: 70.0,
                focus: false,
                gain: "exo".into(),
                echo: "on".into(),
                tuned: false,
            },
            heard: None,
        });
    }),
    ("discord-voice-loudness", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.discord.settings_open = true;
        s.discord.settings_page = crate::discord_state::DiscordSettingsPage::Voice;
        s.discord.settings_ext.client.even_voices = true;
        s.discord.settings_ext.client.voice_target_lufs = -18;
        s.discord.settings_ext.client.mic_auto_gain = true;
    }),
    ("pc-apps", |s: &mut AppState| {
        s.switch_room(state::Room::Apps);
        s.switch_pc_tab(pc_state::PcTab::Apps);
    }),
    ("pc-apps-startup", |s: &mut AppState| {
        s.switch_room(state::Room::Apps);
        s.switch_pc_tab(pc_state::PcTab::Apps);
        s.pc.apps_sub_tab = pc_state::AppsSubTab::Startup;
    }),
    ("pc-storage", |s: &mut AppState| {
        s.switch_room(state::Room::ExoAi);
        s.switch_pc_tab(pc_state::PcTab::Storage);
        s.pc.exo_ai_storage_open = true;
    }),
    ("pc-devices", |s: &mut AppState| {
        s.switch_room(state::Room::Devices);
        s.switch_pc_tab(pc_state::PcTab::Devices);
    }),
    ("pc-devices-mouse", |s: &mut AppState| {
        select_device_by_kind(s, "mouse");
    }),
    ("pc-devices-keyboard", |s: &mut AppState| {
        select_device_by_kind(s, "keyboard");
    }),
    ("pc-devices-audio", |s: &mut AppState| {
        select_device_by_kind(s, "audio");
    }),
    ("pc-devices-controller", |s: &mut AppState| {
        select_device_by_kind(s, "controller");
    }),
    ("music", |s: &mut AppState| {
        s.switch_room(state::Room::Music)
    }),
    ("music-songs", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.media.music_nav = media_state::MusicNav::Songs;
    }),
    ("music-search", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.media.music_nav = media_state::MusicNav::Search;
    }),
    ("music-queue", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.media.music_panel = Some(media_state::MusicPanel::Queue);
    }),
    ("music-now-playing", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.media.music_panel = Some(media_state::MusicPanel::NowPlaying);
    }),
    ("music-immersion", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.media.music_immersion = true;
    }),
    ("music-devices", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.media.music_panel = Some(media_state::MusicPanel::Devices);
    }),
    ("music-friends", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.media.music_panel = Some(media_state::MusicPanel::Friends);
    }),
    ("music-jam", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.media.music_panel = Some(media_state::MusicPanel::Jam);
    }),
    ("music-sound", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.media.music_panel = Some(media_state::MusicPanel::Sound);
    }),
    ("music-signin", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.media.music_room_state = media_state::MusicRoomState::SignedOut;
    }),
    ("music-playlist-sheet", |s: &mut AppState| {
        s.switch_room(state::Room::Music);
        s.music_open_edit_playlist_sheet(
            String::new(),
            String::new(),
            String::new(),
            Some("PRIVATE".into()),
            String::new(),
        );
        if let Some(ref mut sh) = s.media.music_playlist_sheet {
            sh.uri = None;
        }
    }),
    ("captures", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.capture_menu_open = true;
    }),
    ("captures-sheet", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        let present = s
            .media
            .capture_status
            .as_ref()
            .is_none_or(|st| st.present && st.state.as_ref().is_none_or(|l| l.present));
        if present {
            s.open_captures(None);
        } else {
            s.open_notes(None, false);
        }
    }),
    ("captures-settings", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.open_captures(None);
        s.media.captures_settings_open = true;
    }),
    ("capture-clip", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.open_captures(None);
        if let Some(first) = s.media.capture_items.first().map(|c| c.id.clone()) {
            s.open_capture_item(&first);
        }
    }),
    ("captures-notes", |s: &mut AppState| {
        s.switch_room(state::Room::Library);
        s.open_notes(None, false);
    }),
    ("captures-clip-edit", |s: &mut AppState| {
        s.open_captures(None);
        let clip_id = s
            .media
            .capture_items
            .iter()
            .find(|c| c.kind == "video")
            .or_else(|| s.media.capture_items.first())
            .map(|c| c.id.clone());
        if let Some(id) = clip_id {
            s.open_capture_item(&id);
            s.media.clip_editor_open = true;
        }
    }),
    ("captures-snip-edit", |s: &mut AppState| {
        s.open_captures(None);
        let img_id = s
            .media
            .capture_items
            .iter()
            .find(|c| c.kind != "video")
            .or_else(|| s.media.capture_items.first())
            .map(|c| c.id.clone());
        if let Some(id) = img_id {
            s.open_capture_item(&id);
            s.open_snip_editor(&id);
        }
    }),
    ("discord", |s: &mut AppState| {
        s.switch_room(state::Room::Discord)
    }),
    ("discord-status-menu", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_status_menu();
    }),
    ("discord-messages", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_place(discord_state::DiscordPlace::Messages);
    }),
    ("discord-servers", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_place(discord_state::DiscordPlace::Servers);
    }),
    ("discord-server", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        let gid = s
            .discord
            .active_guild_id
            .clone()
            .or_else(|| {
                s.discord
                    .home
                    .guilds
                    .iter()
                    .find(|g| g.id == "g-exo")
                    .map(|g| g.id.clone())
            })
            .or_else(|| s.discord.home.guilds.first().map(|g| g.id.clone()));
        if let Some(gid) = gid {
            s.open_discord_guild(&gid);
        }
    }),
    ("discord-picker", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_place(discord_state::DiscordPlace::Messages);
        s.open_expression_picker_for_composer(discord_chat_state::ExpressionTab::Emoji);
    }),
    ("discord-pins", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_place(discord_state::DiscordPlace::Messages);
        s.toggle_discord_pins_drawer();
    }),
    ("discord-search", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_place(discord_state::DiscordPlace::Messages);
        s.toggle_discord_search_drawer();
    }),
    ("discord-inbox", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_inbox(discord_chat_state::InboxTab::Unreads);
    }),
    ("discord-poll", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_place(discord_state::DiscordPlace::Messages);
        s.open_poll_creator();
    }),
    ("discord-profile", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_place(discord_state::DiscordPlace::Messages);
        let (uid, name, av) = s
            .discord
            .home
            .dms
            .first()
            .map(|d| {
                (
                    d.user_id.clone().unwrap_or_else(|| "u-ada".into()),
                    d.name.clone(),
                    d.avatar.clone(),
                )
            })
            .unwrap_or_else(|| ("u-ada".into(), "Ada".into(), None));
        s.open_user_profile_dialog(discord_chat_state::ProfileOpenRequest {
            user_id: &uid,
            guild_id: None,
            fallback_name: &name,
            fallback_username: &name,
            fallback_avatar: av.as_deref(),
            full: true,
            tab: discord_chat_state::ProfileTab::About,
        });
    }),
    ("discord-create-dm", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_place(discord_state::DiscordPlace::Messages);
        s.open_create_dm_dialog();
    }),
    ("discord-invite", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        if let Some(gid) = s.discord.home.guilds.first().map(|g| g.id.clone()) {
            s.open_discord_guild(&gid);
            if let Some(cid) = s.active_chat_channel_id() {
                let gname = s
                    .discord
                    .home
                    .guilds
                    .iter()
                    .find(|g| g.id == gid)
                    .map(|g| g.name.clone())
                    .unwrap_or_else(|| "Server".into());
                s.open_invite_dialog(&gname, &cid, Some("general"));
            }
        }
    }),
    ("discord-voice", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        let gid = s
            .discord
            .active_guild_id
            .clone()
            .or_else(|| {
                s.discord
                    .home
                    .guilds
                    .iter()
                    .find(|g| g.id == "g-exo")
                    .map(|g| g.id.clone())
            })
            .or_else(|| s.discord.home.guilds.first().map(|g| g.id.clone()));
        if let Some(gid) = gid {
            let voice_cid = s
                .discord
                .active_channel_id
                .clone()
                .filter(|cid| {
                    s.discord
                        .channels_by_guild
                        .get(&gid)
                        .and_then(|c| c.channels.iter().find(|ch| ch.id == *cid))
                        .is_some_and(|ch| matches!(ch.kind.as_str(), "voice" | "stage"))
                })
                .or_else(|| {
                    s.discord.channels_by_guild.get(&gid).and_then(|c| {
                        c.channels
                            .iter()
                            .find(|ch| matches!(ch.kind.as_str(), "voice" | "stage"))
                            .map(|ch| ch.id.clone())
                    })
                })
                .unwrap_or_else(|| "vc-lounge".into());
            s.open_discord_guild(&gid);
            s.open_discord_channel(&voice_cid);
            s.discord.voice.state = "connecting".into();
            s.discord.voice.guild_id = Some(gid.clone());
            s.discord.voice.channel_id = Some(voice_cid.clone());
            s.discord.voice.e2ee = true;
        }
    }),
    ("discord-signin", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.discord.status.state = "signedOut".into();
        s.discord.home.me = None;
        s.start_discord_signin();
    }),
];

fn cursor_css_name(c: rune::Cursor) -> &'static str {
    match c {
        rune::Cursor::Default => "default",
        rune::Cursor::Pointer => "pointer",
        rune::Cursor::Text => "text",
        rune::Cursor::Grab => "grab",
        rune::Cursor::Grabbing => "grabbing",
        rune::Cursor::NotAllowed => "not-allowed",
        rune::Cursor::Move => "move",
        rune::Cursor::ResizeColumn => "col-resize",
        rune::Cursor::ResizeRow => "row-resize",
        rune::Cursor::Crosshair => "crosshair",
        rune::Cursor::Cell => "cell",
        rune::Cursor::ZoomIn => "zoom-in",
        rune::Cursor::ZoomOut => "zoom-out",
        rune::Cursor::Hidden => "none",
        rune::Cursor::Image(_) => "image",
        _ => "other",
    }
}

fn collect_backdrop_blurs(rt: &rune::Runtime<AppState>) -> Vec<serde_json::Value> {
    let mut out: Vec<serde_json::Value> = rt
        .instances
        .iter()
        .filter(|i| i.params[1] == rune::render::KIND_BACKDROP_BLUR)
        .map(|i| {
            serde_json::json!({
                "rect": [
                    (i.rect[0] * 10.0).round() / 10.0,
                    (i.rect[1] * 10.0).round() / 10.0,
                    (i.rect[2] * 10.0).round() / 10.0,
                    (i.rect[3] * 10.0).round() / 10.0,
                ],
                "sigma": (i.params[0] * 10.0).round() / 10.0,
                "kind": "backdrop_blur",
            })
        })
        .collect();
    for sh in &rt.liquid_glass_frame().shapes {
        out.push(serde_json::json!({
            "rect": [
                (sh.rect[0] * 10.0).round() / 10.0,
                (sh.rect[1] * 10.0).round() / 10.0,
                (sh.rect[2] * 10.0).round() / 10.0,
                (sh.rect[3] * 10.0).round() / 10.0,
            ],
            "sigma": (sh.tone[3] * 10.0).round() / 10.0,
            "kind": "liquid_glass",
        }));
    }
    out
}

fn parse_size_arg(args: &[String]) -> (f32, f32) {
    args.windows(2)
        .find(|w| w[0] == "--size")
        .and_then(|w| w[1].split_once('x'))
        .and_then(|(ws, hs)| Some((ws.parse::<f32>().ok()?, hs.parse::<f32>().ok()?)))
        .unwrap_or((1920.0, 1080.0))
}

fn parse_scale_arg(args: &[String]) -> f32 {
    args.windows(2)
        .find(|w| w[0] == "--scale")
        .and_then(|w| w[1].parse::<f32>().ok())
        .unwrap_or(1.0)
}

fn resolve_script_point(
    rt: &rune::Runtime<AppState>,
    ev: &serde_json::Value,
) -> Option<(f32, f32)> {
    if let Some(key) = ev.get("key").and_then(|v| v.as_str()) {
        return rt.find(key);
    }
    let x = ev.get("x").and_then(|v| v.as_f64())? as f32;
    let y = ev.get("y").and_then(|v| v.as_f64())? as f32;
    Some((x, y))
}

fn replay_probe_script(rt: &mut rune::Runtime<AppState>, script_path: &str) {
    let Ok(raw) = std::fs::read_to_string(script_path) else {
        return;
    };
    let Ok(val) = serde_json::from_str::<serde_json::Value>(&raw) else {
        return;
    };
    let events = val
        .as_array()
        .or_else(|| val.get("events").and_then(|e| e.as_array()));
    let Some(events) = events else {
        return;
    };
    let mut now = 0.0_f64;
    let dt = 1.0 / 60.0;
    for ev in events {
        let target_t = ev.get("t").and_then(|v| v.as_f64()).unwrap_or(now);
        while now + dt * 0.5 < target_t {
            now += dt;
            rt.frame(dt);
        }
        let ty = ev.get("type").and_then(|v| v.as_str()).unwrap_or("tap");
        match ty {
            "move" => {
                if let Some((x, y)) = resolve_script_point(rt, ev) {
                    rt.pointer_move(x, y);
                }
            }
            "down" => {
                if let Some((x, y)) = resolve_script_point(rt, ev) {
                    rt.pointer_move(x, y);
                    rt.pointer_down(x, y);
                }
            }
            "up" => {
                if let Some((x, y)) = resolve_script_point(rt, ev) {
                    rt.pointer_up(x, y);
                }
            }
            "tap" | "click" => {
                if let Some((x, y)) = resolve_script_point(rt, ev) {
                    rt.pointer_move(x, y);
                    rt.pointer_down(x, y);
                    rt.frame(dt);
                    now += dt;
                    rt.pointer_up(x, y);
                }
            }
            "secondary-click" => {
                if let Some((x, y)) = resolve_script_point(rt, ev) {
                    rt.pointer_move(x, y);
                    rt.secondary_down(x, y);
                    rt.frame(dt);
                    now += dt;
                    rt.secondary_up(x, y);
                }
            }
            "secondary-down" => {
                if let Some((x, y)) = resolve_script_point(rt, ev) {
                    rt.pointer_move(x, y);
                    rt.secondary_down(x, y);
                }
            }
            "secondary-up" => {
                if let Some((x, y)) = resolve_script_point(rt, ev) {
                    rt.secondary_up(x, y);
                }
            }
            "key" => {
                let name = ev.get("name").and_then(|v| v.as_str()).unwrap_or("");
                let ctrl = ev.get("ctrl").and_then(|v| v.as_bool()).unwrap_or(false);
                let cmd = ev.get("cmd").and_then(|v| v.as_bool()).unwrap_or(ctrl);
                let shift = ev.get("shift").and_then(|v| v.as_bool()).unwrap_or(false);
                let alt = ev.get("alt").and_then(|v| v.as_bool()).unwrap_or(false);
                let mods = rune::Mods {
                    shift,
                    ctrl,
                    alt,
                    cmd,
                    ..rune::Mods::default()
                };
                let key = match name.to_ascii_lowercase().as_str() {
                    "escape" | "esc" => Some(Key::Escape),
                    "enter" | "return" => Some(Key::Enter),
                    "tab" => Some(Key::Tab),
                    "left" | "arrowleft" => Some(Key::Left),
                    "right" | "arrowright" => Some(Key::Right),
                    "up" | "arrowup" => Some(Key::Up),
                    "down" | "arrowdown" => Some(Key::Down),
                    "backspace" => Some(Key::Backspace),
                    s if s.len() == 1 => {
                        let ch = s.chars().next().unwrap();
                        if cmd {
                            Some(Key::Command(ch))
                        } else {
                            Some(Key::Text(s.to_string()))
                        }
                    }
                    _ => None,
                };
                if let Some(k) = key {
                    let ke = KeyEvent { key: k, mods };
                    let mut tx_ctx = Tx::default();
                    let handled = on_key(&mut rt.state, &ke, &mut tx_ctx);
                    if !handled {
                        rt.key(ke);
                    }
                }
            }
            "type" => {
                if let Some(text) = ev.get("text").and_then(|v| v.as_str()) {
                    for ch in text.chars() {
                        rt.key(KeyEvent {
                            key: Key::Text(ch.to_string()),
                            mods: rune::Mods::default(),
                        });
                        rt.frame(dt);
                        now += dt;
                    }
                }
            }
            "wheel" => {
                if let Some((x, y)) = resolve_script_point(rt, ev) {
                    let dy = ev.get("dy").and_then(|v| v.as_f64()).unwrap_or(0.0) as f32;
                    rt.wheel(x, y, dy);
                }
            }
            _ => {}
        }
        rt.frame(dt);
        now += dt;
    }
}

fn popover_trace_sample(rt: &rune::Runtime<AppState>, frame: usize) -> serde_json::Value {
    const KEY: &str = "capture-popover-menu";
    let presented = rt.presented(KEY);
    let element = rt
        .elements()
        .into_iter()
        .find(|e| !e.exiting && e.key.as_deref() == Some(KEY));
    let blur_on_popover = presented.is_some_and(|(rect, _)| {
        rt.instances.iter().any(|i| {
            i.params[1] == rune::render::KIND_BACKDROP_BLUR
                && (0..4).all(|k| (i.rect[k] - rect[k]).abs() < 1.5)
        }) || rt
            .liquid_glass_frame()
            .shapes
            .iter()
            .any(|sh| (0..4).all(|k| (sh.rect[k] - rect[k]).abs() < 1.5))
    });
    serde_json::json!({
        "frame": frame,
        "presented": presented.is_some(),
        "presentedOpacity": presented.map(|(_, o)| (o * 1000.0).round() / 1000.0),
        "elementOpacity": element.as_ref().map(|e| (e.opacity * 1000.0).round() / 1000.0),
        "elementScale": element.as_ref().map(|e| (e.scale * 1000.0).round() / 1000.0),
        "elementBlur": element.as_ref().map(|e| e.blur),
        "fill": element.as_ref().map(|e| format!("{:?}", e.fill)),
        "backdropBlurCount": collect_backdrop_blurs(rt).len(),
        "blurOnPopover": blur_on_popover,
    })
}

fn run_parity_probe(
    pipe: Option<&str>,
    target_scene: Option<&str>,
    rune_args: &[String],
    out_path: &str,
    points_path: Option<&str>,
) {
    let viewport = parse_size_arg(rune_args);
    let scale = parse_scale_arg(rune_args);
    let script_path = rune_args
        .windows(2)
        .find(|w| w[0] == "--script")
        .map(|w| w[1].clone());

    #[cfg(windows)]
    let mut state = if let Some(p) = pipe {
        load_live_state_sync(p, target_scene)
    } else {
        AppState::default()
    };
    #[cfg(not(windows))]
    let mut state = {
        let _ = pipe;
        AppState::default()
    };

    let mut scene_matched = false;
    if let Some(scene_name) = target_scene
        && let Some((_, setup_fn)) = PARITY_SCENES.iter().find(|(n, _)| *n == scene_name)
    {
        setup_fn(&mut state);
        scene_matched = true;
    }
    setup_state(&mut state, std::sync::Arc::new(|| {}));

    let text = rune::text::TextSystem::for_window(FONTS, FONT_FAMILIES);
    let mut rt = rune::Runtime::with_text(
        state,
        shell::view,
        viewport,
        rune::Env::new(true, scale),
        text,
    );
    rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
    rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
    rt.frame(0.0);
    if rt.state.palette_open {
        rt.update(|s, tx| {
            s.palette_needs_focus = false;
            tx.focus("palette-input", false);
        });
        rt.frame(0.0);
    }
    if let Some(ref sp) = script_path {
        replay_probe_script(&mut rt, sp);
    }
    let glass_frame0 = collect_backdrop_blurs(&rt);

    let mut popover_trace = vec![popover_trace_sample(&rt, 0)];
    for frame in 1..=10 {
        rt.frame(1.0 / 60.0);
        popover_trace.push(popover_trace_sample(&rt, frame));
    }
    let glass_frame10 = collect_backdrop_blurs(&rt);

    for _ in 0..24 {
        rt.frame(1.0 / 60.0);
    }
    if target_scene.is_some_and(|s| s.starts_with("setup")) {
        let until = std::time::Instant::now() + Duration::from_secs(4);
        while std::time::Instant::now() < until {
            std::thread::sleep(Duration::from_millis(50));
            rt.frame(1.0 / 60.0);
        }
    }
    let glass_final = collect_backdrop_blurs(&rt);

    let elements = rt.elements();
    let hovered_keys: Vec<String> = elements
        .iter()
        .filter(|e| e.hover > 0.01)
        .map(|e| e.key.clone().unwrap_or_else(|| e.path.clone()))
        .collect();
    let pressed_keys: Vec<String> = elements
        .iter()
        .filter(|e| e.press > 0.01)
        .map(|e| e.key.clone().unwrap_or_else(|| e.path.clone()))
        .collect();

    let semantic_menu_prefixes = [
        "capture-popover-menu",
        "library-more",
        "library-add",
        "exo-ctx-menu",
        "exo-ctx-submenu",
        "dd-",
        "command-palette",
        "pc-part-menu",
        "library-filter-pop",
        "dc-golive-panel",
    ];
    let legacy_menu_prefixes = ["dc-status-menu", "dc-menu"];
    let mut menu_items: Vec<String> = Vec::new();
    for el in &elements {
        if el.exiting {
            continue;
        }
        let in_semantic_menu = semantic_menu_prefixes
            .iter()
            .any(|p| el.path.contains(p) || el.key.as_deref().is_some_and(|k| k.contains(p)));
        let in_legacy_menu = legacy_menu_prefixes
            .iter()
            .any(|p| el.path.contains(p) || el.key.as_deref().is_some_and(|k| k.contains(p)));
        let label_opt = if in_semantic_menu && matches!(el.role, "menuitem" | "option") {
            el.label.as_deref().or(el.text.as_deref())
        } else if in_legacy_menu {
            el.text.as_deref().or(el.label.as_deref())
        } else {
            None
        };
        if let Some(t) = label_opt {
            let trimmed = t.trim();
            if !trimmed.is_empty() && !menu_items.iter().any(|x| x == trimmed) {
                menu_items.push(trimmed.to_string());
            }
        }
    }

    let popup_menu_open = rt.popup_menu().is_some()
        || rt.state.media.music_menu.is_some()
        || elements
            .iter()
            .any(|e| !e.exiting && e.key.as_deref() == Some("pc-part-menu"));

    let state_summary = serde_json::json!({
        "room": format!("{:?}", rt.state.room),
        "libraryAllGames": rt.state.library_all_games,
        "librarySearchOpen": rt.state.library_search_open,
        "libraryMoreOpen": rt.state.library_more_open,
        "libraryAddOpen": rt.state.library_add_open,
        "contextMenuOpen": rt.state.context_menu.is_some() || rt.state.media.music_menu.is_some(),
        "openDropdown": rt.state.open_dropdown.map(|d| format!("{d:?}")),
        "openGameId": rt.state.open_game_id,
        "propsOpen": rt.state.props_open,
        "gamePropsTab": rt.state.game_props_tab,
        "storeTab": rt.state.store_tab,
        "storeGameOpen": rt.state.store_has_page_open(),
        "paletteOpen": rt.state.palette_open,
        "captureMenuOpen": rt.state.capture_menu_open,
        "capturesOpen": rt.state.media.captures_open,
        "capturesSettingsOpen": rt.state.media.captures_settings_open,
        "keyModalOpen": rt.state.key_modal_open,
        "downloadsOpen": rt.state.downloads.open,
        "whatsNewOpen": rt.state.whats_new.mode.is_some(),
        "updateSheetOpen": rt.state.whats_new.sheet_open,
        "islandVisible": rt.state.island_visible(),
        "driverInstallOpen": rt.state.driver_install_overlay_visible(),
        "tourStep": rt.state.setup.beat,
        "setupStage": rt.state.setup.stage,
        "settingsSection": format!("{:?}", rt.state.pc.settings_section),
        "pcTab": format!("{:?}", rt.state.pc.pc_tab),
        "pcPartMenu": rt.state.pc.part_menu.map(|m| format!("{m:?}")),
        "appsSubTab": format!("{:?}", rt.state.pc.apps_sub_tab),
        "selectedDeviceId": rt.state.pc.selected_device_id,
        "musicNav": format!("{:?}", rt.state.media.music_nav),
        "musicPanel": rt.state.media.music_panel.map(|p| format!("{p:?}")),
        "musicImmersion": rt.state.media.music_immersion,
        "musicRoomState": format!("{:?}", rt.state.media.music_room_state),
        "discordPlace": format!("{:?}", rt.state.discord.place),
        "discordStatusMenuOpen": rt.state.discord.status_menu_open
            || matches!(
                rt.state.context_menu.as_ref().map(|m| &m.target),
                Some(state::ContextMenuTarget::DiscordStatusMenu)
            ),
        "discordSettingsOpen": rt.state.discord.settings_open,
        "discordVoiceChannel": rt.state.discord.voice.channel_id.clone().or_else(|| {
            rt.state
                .discord
                .active_channel()
                .filter(|c| matches!(c.kind.as_str(), "voice" | "stage"))
                .map(|c| c.id.clone())
        }),
        "discordSignedIn": rt.state.discord.status.signed_in,
    });

    let mut cursors: Vec<serde_json::Value> = Vec::new();
    if let Some(pp) = points_path
        && let Ok(raw) = std::fs::read_to_string(pp)
        && let Ok(pts) = serde_json::from_str::<Vec<serde_json::Value>>(&raw)
    {
        for pt in pts {
            let name = pt.get("name").and_then(|v| v.as_str()).unwrap_or("");
            let x = pt.get("x").and_then(|v| v.as_f64()).unwrap_or(0.0) as f32;
            let y = pt.get("y").and_then(|v| v.as_f64()).unwrap_or(0.0) as f32;
            rt.pointer_move(x, y);
            rt.frame(1.0 / 60.0);
            let cursor = cursor_css_name(rt.cursor());
            let picked = rt.element_at(x, y);
            cursors.push(serde_json::json!({
                "name": name,
                "x": x,
                "y": y,
                "cursor": cursor,
                "hitKey": picked.as_ref().and_then(|p| p.key()),
                "hitPath": picked.as_ref().map(|p| p.path()),
            }));
        }
    }

    let report = serde_json::json!({
        "scene": target_scene,
        "sceneMatched": scene_matched,
        "viewport": [viewport.0, viewport.1],
        "scale": scale,
        "elementsCount": elements.len(),
        "glassAtFrame0": glass_frame0,
        "glassAtFrame10": glass_frame10,
        "glassFinal": glass_final,
        "glassAppearedLate": glass_frame0.len() < glass_final.len(),
        "popoverTrace": popover_trace,
        "popupMenuOpen": popup_menu_open,
        "menuItems": menu_items,
        "hoveredKeys": hovered_keys,
        "pressedKeys": pressed_keys,
        "cursors": cursors,
        "stateSummary": state_summary,
    });
    if let Some(parent) = std::path::Path::new(out_path).parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let _ = std::fs::write(
        out_path,
        serde_json::to_string_pretty(&report).unwrap_or_default(),
    );
}

#[global_allocator]
static GLOBAL_ALLOCATOR: mimalloc::MiMalloc = mimalloc::MiMalloc;

const MI_OPTION_ARENA_EAGER_COMMIT: libmimalloc_sys::mi_option_t = 4;
const MI_OPTION_PURGE_DECOMMITS: libmimalloc_sys::mi_option_t = 5;
const MI_OPTION_PURGE_DELAY: libmimalloc_sys::mi_option_t = 15;
const MI_OPTION_ARENA_PURGE_MULT: libmimalloc_sys::mi_option_t = 24;
const MI_OPTION_PAGE_COMMIT_ON_DEMAND: libmimalloc_sys::mi_option_t = 40;

fn configure_mimalloc() {
    unsafe {
        libmimalloc_sys::mi_option_set(MI_OPTION_ARENA_EAGER_COMMIT, 0);
        libmimalloc_sys::mi_option_set(MI_OPTION_PURGE_DECOMMITS, 1);
        libmimalloc_sys::mi_option_set(MI_OPTION_PURGE_DELAY, 0);
        libmimalloc_sys::mi_option_set(MI_OPTION_ARENA_PURGE_MULT, 1);
        libmimalloc_sys::mi_option_set(MI_OPTION_PAGE_COMMIT_ON_DEMAND, 1);
    }
}

fn main() {
    configure_mimalloc();
    let cli = split_cli_args();
    let pipe_name = cli.pipe;
    let rune_args = cli.passthrough;
    let target_scene = rune_args
        .windows(2)
        .find(|w| w[0] == "--scene")
        .map(|w| w[1].clone());

    if let Some(ref probe_out) = cli.parity_probe {
        run_parity_probe(
            pipe_name.as_deref(),
            target_scene.as_deref(),
            &rune_args,
            probe_out,
            cli.probe_points.as_deref(),
        );
        return;
    }

    if let Some(ref target_exe) = cli.present_hud {
        run_standalone_present_hud(target_exe, cli.present_hud_secs.unwrap_or(15));
        return;
    }

    let headless_flag = rune_args
        .iter()
        .any(|a| a == "--snap" || a == "--tree" || a == "--script" || a == "--headless");

    let _native_autopilot = if !headless_flag {
        let _ = native_autopilot::recover_orphaned_session();
        if cli.native_autopilot {
            Some(native_autopilot::NativeAutopilotGuard::start())
        } else {
            None
        }
    } else {
        None
    };

    let mut spec = AppSpec::new(
        "Exo Launcher",
        (1280.0, 800.0),
        AppState::default,
        shell::view,
    );
    spec.fonts = FONTS;
    spec.families = FONT_FAMILIES;
    spec.setup = Some(setup_state);
    spec.poll = Some(poll_state);
    spec.on_key = Some(on_key);
    spec.scenes = PARITY_SCENES;

    let mut app = App::new(spec)
        .args(rune_args)
        .glass(exo_kit::EXO_GLASS_POLICY)
        .max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS)
        .titlebar(exo_titlebar())
        .on_rest(AppState::on_rest)
        .release_gpu_when_closed();

    if let Some(ref pipe) = pipe_name {
        if headless_flag {
            #[cfg(windows)]
            {
                let pipe_owned = pipe.clone();
                let scene_owned = target_scene.clone();
                app = app.init(move || load_live_state_sync(&pipe_owned, scene_owned.as_deref()));
            }
        } else {
            app = app.init(AppState::for_host_pipe);
        }
    }

    let handle = app.handle();
    let win_handle = handle.clone();
    app = app
        .on_window(move |w| {
            let hwnd = w
                .with_raw_handle(|raw| match raw.as_raw() {
                    rune::raw_window_handle::RawWindowHandle::Win32(h) => h.hwnd.get() as u64,
                    _ => 0,
                })
                .unwrap_or(0);
            if hwnd != 0 {
                win_handle.update(move |s, _| s.set_owner_hwnd(hwnd));
            }
        })
        .on_close(|s, _| s.close_mouse_panel())
        .on_quit(|s, _| s.close_mouse_panel())
        .on_visibility(|s, visible, _| s.set_mouse_panel_window_visible(visible));

    if !headless_flag {
        let gp_handle = handle.clone();
        gamepad::start_polling(move |action| {
            gp_handle.update(move |s, tx| s.apply_gamepad_action(action, tx));
        });
    }

    let _tray = if !headless_flag {
        let tray_handle = handle.clone();
        let spec = TraySpec::new("exo-launcher-rune", "Exo Launcher", tray_icon_rgba())
            .tooltip("Exo Launcher")
            .menu(vec![
                TrayItem::action("open", "Open Exo"),
                TrayItem::action("refresh", "Refresh library"),
                TrayItem::action("quit", "Quit"),
            ]);
        Tray::new(spec, move |ev| match ev {
            TrayEvent::Activate { token } => {
                if let Some(t) = token {
                    tray_handle.activate(t);
                } else {
                    tray_handle.open();
                }
            }
            TrayEvent::Menu(id) if id == "open" => tray_handle.open(),
            TrayEvent::Menu(id) if id == "refresh" => {
                tray_handle.update(|s, _| s.refresh_library());
            }
            TrayEvent::Menu(id) if id == "quit" => tray_handle.quit(),
            _ => {}
        })
        .ok()
    } else {
        None
    };

    #[cfg(windows)]
    if !headless_flag && let Some(pipe) = pipe_name {
        let connect_handle = handle.clone();
        std::thread::Builder::new()
            .name("exo-rpc-connect".into())
            .spawn(move || {
                let event_handle = connect_handle.clone();
                match exo_rpc::RpcClient::connect(&pipe, Duration::from_secs(6), move |ev| {
                    event_handle.update_if(move |s, _| s.apply_host_event(ev));
                }) {
                    Ok(client) => {
                        let rpc_for_boot = std::sync::Arc::clone(&client);
                        let boot_handle = connect_handle.clone();
                        connect_handle.update(move |s, _| {
                            let router = s.video_router.clone();
                            client.set_video_frame_handler(move |frame| router.try_push(frame));
                            s.rpc = Some(client);
                            s.handle = Some(boot_handle.clone());
                            s.status = state::HostStatus::Connected;
                            AppState::bootstrap_rpc(rpc_for_boot, boot_handle);
                        });
                    }
                    Err(err) => {
                        let msg = err.to_string();
                        connect_handle.update(move |s, _| {
                            s.refreshing = false;
                            s.status = state::HostStatus::Error(msg.clone());
                            s.notice = Some(format!("Could not connect to host: {msg}"));
                        });
                    }
                }
            })
            .ok();
    }

    let _hud_overlay = if !headless_flag {
        Some(app.overlay(
            hud_overlay::configured_overlay(),
            exo_present::HudState::default(),
        ))
    } else {
        None
    };

    app.run();
}

fn run_standalone_present_hud(target_exe: &str, duration_secs: u64) {
    let spec = AppSpec::new(
        "Exo Present HUD",
        (320.0, 120.0),
        || (),
        |_, _| rune::el().w_full().h_full().fill(exo_kit::TRANSPARENT),
    );
    let mut app = App::new(spec)
        .windowless()
        .keep_running()
        .glass(exo_kit::EXO_GLASS_POLICY)
        .max_backdrop_blurs(1);
    let hud_handle = app.overlay(
        hud_overlay::configured_overlay(),
        exo_present::HudState::default(),
    );
    let app_handle = app.handle();
    let target = target_exe.to_string();
    std::thread::Builder::new()
        .name("exo-present-hud-loop".into())
        .spawn(move || {
            let cfg = exo_present::TraceConfig {
                session_name: "ExoPresentHudLive".into(),
                enable_kernel_display: false,
                flush_timer_secs: 1,
                record_raw_samples: false,
                record_diag: false,
            };
            let Ok(mut sess) = exo_present::LiveEtwSession::start(cfg) else {
                app_handle.quit();
                return;
            };
            let t0 = std::time::Instant::now();
            let limit = Duration::from_secs(duration_secs.max(2));
            let items = [exo_present::HudItem::Low, exo_present::HudItem::FrameTime];
            let extras = exo_present::HudExtras::default();
            while t0.elapsed() < limit {
                let pids = exo_present::find_pids_by_name(&target);
                if !pids.is_empty() {
                    sess.watch(&pids);
                }
                let (_fg_pid, hmon) = exo_present::foreground_pid_and_monitor();
                let metrics = sess.snapshot();
                if metrics.frame_count > 0 {
                    let line = exo_present::format_hud_line(&metrics, &items, &extras);
                    hud_overlay::sync_overlay(
                        &hud_handle,
                        true,
                        exo_present::HudPosition::Right,
                        line,
                        hmon,
                        t0.elapsed().as_secs(),
                    );
                }
                std::thread::sleep(Duration::from_millis(500));
            }
            hud_overlay::sync_overlay(
                &hud_handle,
                false,
                exo_present::HudPosition::Right,
                exo_present::HudLine::default(),
                0,
                0,
            );
            drop(sess);
            app_handle.quit();
        })
        .ok();
    app.run();
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::gamepad::GamepadAction;
    use crate::media_state::MusicService;
    use crate::state::{LibraryChip, LibrarySort, Room};
    use rune::{
        Cursor, Env, Mods, Runtime,
        render::{KIND_BACKDROP_BLUR, KIND_LAYER, KIND_LAYER_END, KIND_LIQUID_GLASS, KIND_SEGMENT},
    };

    #[test]
    fn phase2_game_page_store_tour_key_modal_and_gamepad() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
        rt.frame(0.0);

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert_eq!(blurs, 0);
        let glass_shapes = rt.liquid_glass_frame().shapes.len();
        assert!(
            (2..=exo_kit::EXO_MAX_BACKDROP_BLURS).contains(&glass_shapes),
            "expected 2..={} floating glass surfaces within EXO_MAX_BACKDROP_BLURS, got {glass_shapes}",
            exo_kit::EXO_MAX_BACKDROP_BLURS
        );
        let strokes = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_SEGMENT)
            .count();
        assert!(strokes >= 10, "expected vector icons, got {strokes}");

        let (tx, ty) = rt.find("tile-demo:cyberpunk").expect("Cyberpunk tile");
        rt.pointer_down(tx, ty);
        rt.pointer_up(tx, ty);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.open_game_id.as_deref(), Some("demo:cyberpunk"));

        let (px, py) = rt.find("gp-prop-tab-1").expect("Installed files tab");
        rt.pointer_down(px, py);
        rt.pointer_up(px, py);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.game_props_tab, 1);

        let mut tx_ctx = Tx::default();
        assert!(on_key(
            &mut rt.state,
            &KeyEvent {
                key: Key::Escape,
                mods: Mods::default()
            },
            &mut tx_ctx
        ));
        assert!(rt.state.open_game_id.is_none());

        rt.update(|s, tx| {
            s.apply_gamepad_action(GamepadAction::Connected(true), tx);
            s.apply_gamepad_action(GamepadAction::NextRoom, tx);
        });
        rt.frame(2.0 / 60.0);
        assert_eq!(rt.state.room, Room::Store);
        assert!(rt.find("gamepad-bar").is_some());

        let (hx, hy) = rt.find("store-hero-open").expect("Store hero button");
        rt.pointer_down(hx, hy);
        rt.pointer_up(hx, hy);
        rt.frame(3.0 / 60.0);
        assert!(rt.state.store_game.is_some());
        assert!(rt.state.store_offers.is_some());

        rt.update(|s, _| s.start_tour());
        rt.frame(4.0 / 60.0);
        assert_eq!(rt.state.setup.beat, Some(0));
        assert_eq!(rt.state.room, Room::Library);
        rt.state.setup_beat_skip();
        assert_eq!(rt.state.setup.beat, Some(1));
        rt.state.finish_tour();
        assert!(rt.state.setup.beat.is_none());

        rt.state.filters = vec![LibraryChip::Installed];
        assert!(rt.state.filtered_games().iter().all(|g| g.installed));
    }

    #[test]
    fn phase3_settings_and_this_pc_tabs_and_wooting() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| s.switch_room(Room::Settings));
        rt.frame(0.0);
        let (x, y) = rt.find("set-start-win").expect("Start with Windows switch");
        let before = rt.state.settings.start_with_windows;
        rt.pointer_down(x, y);
        rt.pointer_up(x, y);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.settings.start_with_windows, !before);

        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
        rt.update(|s, _| s.switch_room(Room::ExoAi));
        rt.frame(2.0 / 60.0);
        assert_eq!(rt.state.pc.pc_tab, pc_state::PcTab::ExoAi);
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "PC room exceeded EXO_MAX_BACKDROP_BLURS={}: {blurs}",
            exo_kit::EXO_MAX_BACKDROP_BLURS
        );

        let (sx, sy) = rt.find("pc-tab-storage").expect("Storage button");
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        rt.frame(3.0 / 60.0);
        assert!(rt.state.pc.exo_ai_storage_open);

        let (ax, ay) = rt.find("nav-apps").expect("Apps nav tab");
        rt.pointer_down(ax, ay);
        rt.pointer_up(ax, ay);
        rt.frame(4.0 / 60.0);
        assert_eq!(rt.state.room, Room::Apps);

        let (dx, dy) = rt.find("nav-devices").expect("Devices nav tab");
        rt.pointer_down(dx, dy);
        rt.pointer_up(dx, dy);
        rt.frame(5.0 / 60.0);
        assert_eq!(rt.state.room, Room::Devices);

        let (wx, wy) = rt
            .find("dev-card-dev:wooting")
            .expect("Wooting device card");
        rt.pointer_down(wx, wy);
        rt.pointer_up(wx, wy);
        rt.frame(6.0 / 60.0);
        let act_before = rt
            .state
            .pc
            .keyboard
            .as_ref()
            .and_then(|k| k.actuation_mm)
            .unwrap_or(0.4);
        let (ux, uy) = rt.find("kb-act-up").expect("Wooting actuation +");
        rt.pointer_down(ux, uy);
        rt.pointer_up(ux, uy);
        rt.frame(7.0 / 60.0);
        let act_after = rt
            .state
            .pc
            .keyboard
            .as_ref()
            .and_then(|k| k.actuation_mm)
            .unwrap_or(0.0);
        assert!((act_after - (act_before + 0.1)).abs() < 0.01);
    }

    #[test]
    fn wooting_native_layouts_controls_and_error_recovery() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| {
            s.switch_room(Room::Devices);
            s.pc.selected_device_id = Some("dev:wooting".into());
            if let Some(ref mut kb) = s.pc.keyboard {
                kb.model = Some("Wooting 60HE v2".into());
                kb.form_factor = Some("60".into());
                kb.layout = Some("ANSI · Split spacebar".into());
                kb.layout_code = Some("ansi-split".into());
                kb.actuation_mm = Some(1.0);
                kb.rapid_trigger = Some(true);
                kb.rapid_trigger_mm = Some(0.15);
                kb.rgb_brightness = Some(70);
                kb.can_write = true;
            }
        });
        rt.frame(0.0);

        let (sx, sy) = rt
            .find("kb-key-space-l")
            .expect("60HE v2 left split spacebar");
        assert!(rt.find("kb-key-fn-mid").is_some());
        assert!(rt.find("kb-key-space-r").is_some());
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        rt.frame(1.0 / 60.0);
        assert_eq!(
            rt.state.pc.keyboard_selected_key.as_deref(),
            Some("space-l")
        );
        assert!(rt.find("kb-key-inspect").is_some());

        let (rtx, rty) = rt.find("kb-rt-up").expect("RT +0.05 button");
        rt.pointer_down(rtx, rty);
        rt.pointer_up(rtx, rty);
        rt.frame(2.0 / 60.0);
        assert!(
            (rt.state
                .pc
                .keyboard
                .as_ref()
                .and_then(|k| k.rapid_trigger_mm)
                .unwrap_or(0.0)
                - 0.20)
                .abs()
                < 0.005
        );

        let (rgbx, rgby) = rt.find("kb-rgb-down").expect("RGB -5% button");
        rt.pointer_down(rgbx, rgby);
        rt.pointer_up(rgbx, rgby);
        rt.frame(3.0 / 60.0);
        assert_eq!(
            rt.state.pc.keyboard.as_ref().and_then(|k| k.rgb_brightness),
            Some(65)
        );

        let snap = rt.state.pc.keyboard.clone();
        rt.update(|s, _| {
            if let Some(ref mut kb) = s.pc.keyboard {
                kb.actuation_mm = Some(2.5);
            }
            s.apply_keyboard_write_reply(
                snap.clone(),
                Ok(exo_rpc::KeyboardReadReply {
                    ok: false,
                    message: Some("HID report rejected by firmware".into()),
                    ..Default::default()
                }),
                "Failed",
            );
        });
        rt.frame(4.0 / 60.0);
        assert_eq!(
            rt.state.pc.keyboard.as_ref().and_then(|k| k.actuation_mm),
            Some(1.0)
        );
        assert_eq!(
            rt.state.pc.keyboard_error.as_deref(),
            Some("HID report rejected by firmware")
        );
        assert!(rt.find("kb-write-error").is_some());

        rt.update(|s, _| {
            s.pc.keyboard_error = None;
            if let Some(ref mut kb) = s.pc.keyboard {
                kb.model = Some("Wooting 80HE".into());
                kb.form_factor = Some("80".into());
                kb.layout_code = Some("ansi".into());
            }
        });
        rt.frame(5.0 / 60.0);
        assert!(rt.find("kb-key-led-bar").is_some());
        assert!(rt.find("kb-key-mode").is_some());

        rt.update(|s, _| {
            if let Some(ref mut kb) = s.pc.keyboard {
                kb.model = Some("Wooting Two HE".into());
                kb.form_factor = Some("full".into());
                kb.layout_code = Some("iso".into());
            }
        });
        rt.frame(6.0 / 60.0);
        assert!(rt.find("kb-key-a1").is_some());
        assert!(rt.find("kb-key-a3").is_some());
        assert!(rt.find("kb-key-iso-lt").is_some());

        rt.update(|s, _| {
            if let Some(ref mut kb) = s.pc.keyboard {
                kb.model = Some("Wooting UwU".into());
                kb.form_factor = Some("keypad3".into());
                kb.layout_code = Some("keypad".into());
                kb.has_rgb = false;
                kb.has_rapid_trigger = true;
            }
        });
        rt.frame(7.0 / 60.0);
        assert!(rt.find("kb-key-uwu-k1").is_some());
        assert!(rt.find("kb-rgb-up").is_none());

        rt.update(|s, _| {
            if let Some(ref mut kb) = s.pc.keyboard {
                kb.model = Some("Wooting One".into());
                kb.form_factor = Some("tkl".into());
                kb.layout_code = Some("ansi".into());
                kb.hall_effect = false;
                kb.has_rapid_trigger = false;
                kb.has_rgb = true;
                kb.min_actuation_mm = Some(1.5);
                kb.max_actuation_mm = Some(3.6);
                kb.actuation_mm = Some(1.5);
                kb.rapid_trigger = None;
                kb.tachyon = None;
                kb.can_write = false;
                kb.message = Some("Close Wootility to edit".into());
            }
        });
        rt.frame(8.0 / 60.0);
        assert!(rt.find("kb-rt-toggle").is_none());
        assert!(rt.find("kb-readonly-banner").is_some());
        rt.update(|s, _| s.update_keyboard_settings(Some(0.2), None, None, None, None));
        assert_eq!(
            rt.state.pc.keyboard.as_ref().and_then(|k| k.actuation_mm),
            Some(1.5)
        );

        rt.update(|s, _| {
            s.apply_keyboard_read_reply(Ok(exo_rpc::KeyboardReadReply {
                ok: false,
                message: Some("Wooting keyboard not connected.".into()),
                ..Default::default()
            }));
        });
        rt.frame(9.0 / 60.0);
        assert!(rt.find("kb-error-card").is_some());
        assert!(rt.find("kb-retry").is_some());
    }

    #[test]
    fn phase4_music_mini_player_and_captures_theater() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
        rt.frame(0.0);
        assert!(rt.find("music-mini-player").is_some());
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "mini-player exceeded EXO_MAX_BACKDROP_BLURS={}: {blurs}",
            exo_kit::EXO_MAX_BACKDROP_BLURS
        );

        let (mx, my) = rt.find("mini-open-music").expect("Mini player open Music");
        rt.pointer_down(mx, my);
        rt.pointer_up(mx, my);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.room, Room::Music);
        assert!(rt.find("music-mini-player").is_none());

        let (px, py) = rt.find("music-toggle").expect("Music play/pause");
        rt.pointer_down(px, py);
        rt.pointer_up(px, py);
        rt.frame(2.0 / 60.0);
        assert!(!rt.state.media.playback.as_ref().unwrap().is_playing());

        rt.update(|s, _| s.open_captures(None));
        rt.frame(3.0 / 60.0);
        assert!(rt.state.media.captures_open);
        let (cx, cy) = rt
            .find("cap-card-cap0000000000001")
            .expect("First capture card");
        rt.pointer_down(cx, cy);
        rt.pointer_up(cx, cy);
        rt.frame(4.0 / 60.0);
        assert_eq!(
            rt.state.media.open_capture_id.as_deref(),
            Some("cap0000000000001")
        );
        assert!(rt.find("cap-trim-go").is_some());
    }

    #[test]
    fn phase5a_discord_navigation_voice_members_and_qr_signin() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| s.switch_room(Room::Discord));
        rt.frame(0.0);
        assert_eq!(rt.state.room, Room::Discord);
        assert_eq!(rt.state.discord.place, discord_state::DiscordPlace::Home);
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(blurs <= 4, "Discord exceeded MAX_BACKDROP_BLURS=4: {blurs}");

        let (mx, my) = rt.find("dc-dm-dm-ada").expect("DM row");
        rt.pointer_down(mx, my);
        rt.pointer_up(mx, my);
        rt.frame(1.0 / 60.0);
        assert_eq!(
            rt.state.discord.place,
            discord_state::DiscordPlace::Messages
        );
        assert!(rt.find("dc-dm-stage").is_some());

        let (gx, gy) = rt.find("dc-guild-tile-g-exo").expect("Exo HQ server tile");
        rt.pointer_down(gx, gy);
        rt.pointer_up(gx, gy);
        rt.frame(2.0 / 60.0);
        assert_eq!(rt.state.discord.place, discord_state::DiscordPlace::Servers);
        assert_eq!(rt.state.discord.active_guild_id.as_deref(), Some("g-exo"));
        assert!(rt.find("dc-members-panel").is_some());

        let (vx, vy) = rt
            .find("dc-ch-vc-lounge")
            .expect("Main Lounge voice channel");
        rt.pointer_down(vx, vy);
        rt.pointer_up(vx, vy);
        rt.frame(4.0 / 60.0);
        assert_eq!(rt.state.discord.voice.state, "connected");
        assert!(rt.find("dc-call-dock").is_some());

        let (tx, ty) = rt.find("dc-me-trigger").expect("MePanel status trigger");
        rt.pointer_down(tx, ty);
        rt.pointer_up(tx, ty);
        rt.frame(5.0 / 60.0);
        let (dx, dy) = rt.find("dc-set-status-dnd").expect("DND status option");
        rt.pointer_down(dx, dy);
        rt.pointer_up(dx, dy);
        rt.frame(6.0 / 60.0);
        let (ux, uy) = rt.find("dc-status-dnd-60").expect("DND for one hour");
        rt.pointer_down(ux, uy);
        rt.pointer_up(ux, uy);
        rt.frame(6.5 / 60.0);
        assert_eq!(
            rt.state
                .discord
                .home
                .me
                .as_ref()
                .and_then(|m| m.status.as_deref()),
            Some("dnd")
        );

        rt.update(|s, _| {
            s.sign_out_discord();
            s.start_discord_signin();
        });
        rt.frame(7.0 / 60.0);
        assert!(rt.find("dc-signin-card").is_some());
    }

    #[test]
    fn phase5b_discord_virtual_timeline_markdown_send_react_pins_and_search() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| {
            s.switch_room(Room::Discord);
            s.open_discord_guild("g-exo");
        });
        rt.frame(0.0);
        assert!(rt.find("dc-composer").is_some());
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= 4,
            "Discord chat exceeded MAX_BACKDROP_BLURS=4: {blurs}"
        );

        let before_count = rt
            .state
            .discord
            .chat
            .messages_by_channel
            .get("ch-general")
            .map(|v| v.len())
            .unwrap_or(0);
        rt.update(|s, _| {
            s.discord.chat.composer.text = "Hello **Rune** `virtual_rows`!".into();
        });
        rt.frame(1.0 / 60.0);
        let (sx, sy) = rt.find("dc-composer-send").expect("Composer send button");
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        rt.frame(2.0 / 60.0);
        let after_count = rt
            .state
            .discord
            .chat
            .messages_by_channel
            .get("ch-general")
            .map(|v| v.len())
            .unwrap_or(0);
        assert_eq!(after_count, before_count + 1);

        let (px, py) = rt.find("dc-hdr-pins").expect("Pins drawer trigger");
        rt.pointer_down(px, py);
        rt.pointer_up(px, py);
        rt.frame(3.0 / 60.0);
        assert!(rt.find("dc-pins-drawer").is_some());

        let (qx, qy) = rt.find("dc-hdr-search").expect("Search drawer trigger");
        rt.pointer_down(qx, qy);
        rt.pointer_up(qx, qy);
        rt.frame(4.0 / 60.0);
        assert!(rt.find("dc-search-drawer").is_some());
        rt.update(|s, _| {
            s.discord.chat.search_input.text = "DX12".into();
            s.run_discord_search();
        });
        rt.frame(5.0 / 60.0);
        assert!(rt.state.discord.chat.search_total >= 1);
    }

    #[test]
    fn phase5c_discord_voice_stage_stream_golive_soundboard_and_audio() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| {
            s.switch_room(Room::Discord);
            s.open_discord_guild("g-exo");
            s.open_discord_channel("vc-lounge");
            s.join_discord_voice(Some("g-exo"), "vc-lounge");
        });
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
        rt.frame(0.0);
        assert!(rt.find("dc-voice-stage").is_some());
        assert!(rt.find("dc-stream-theater").is_some());
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "Discord voice exceeded EXO_MAX_BACKDROP_BLURS={}: {blurs}",
            exo_kit::EXO_MAX_BACKDROP_BLURS
        );

        let (gx, gy) = rt.find("dc-dock-share").expect("Share screen button");
        rt.pointer_down(gx, gy);
        rt.pointer_up(gx, gy);
        rt.frame(1.0 / 60.0);
        assert_eq!(
            rt.state.discord.voice_ui.panel,
            discord_voice_state::VoicePanel::GoLive
        );
        rt.update(|s, _| s.start_discord_golive());
        rt.frame(2.0 / 60.0);
        assert_eq!(rt.state.discord.voice_ui.golive_state, "live");
        assert!(rt.find("dc-golive-banner").is_some());

        let (sx, sy) = rt.find("dc-dock-sb").expect("Soundboard button");
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        rt.frame(3.0 / 60.0);
        assert_eq!(
            rt.state.discord.voice_ui.panel,
            discord_voice_state::VoicePanel::Soundboard
        );

        rt.update(|s, _| s.open_voice_panel(discord_voice_state::VoicePanel::None));
        rt.frame(3.5 / 60.0);
        let (ax, ay) = rt.find("dc-dock-status").expect("Audio mixer button");
        rt.pointer_down(ax, ay);
        rt.pointer_up(ax, ay);
        rt.frame(4.0 / 60.0);
        assert_eq!(
            rt.state.discord.voice_ui.panel,
            discord_voice_state::VoicePanel::Audio
        );
    }

    #[test]
    fn phase5d_discord_channel_secondary_press_opens_menu() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| {
            s.switch_room(Room::Discord);
            s.open_discord_guild("g-exo");
            s.open_discord_channel("ch-general");
        });
        rt.frame(0.0);
        for id in ["ch-general", "vc-lounge"] {
            let (x, y) = rt.find(&format!("dc-ch-{id}")).expect("channel row");
            rt.pointer_move(x, y);
            rt.secondary_down(x, y);
            rt.frame(1.0 / 60.0);
            rt.secondary_up(x, y);
            rt.frame(1.0 / 60.0);
            eprintln!(
                "PROBE {id} ctx={:?} items={}",
                rt.state.context_menu.as_ref().map(|c| c.x),
                rt.find("dc-ch-copy-id").is_some()
            );
            rt.update(|s, _| s.context_menu = None);
            rt.frame(1.0 / 60.0);
        }
    }

    #[test]
    fn phase5e_bar_and_menu_items_report_pointer_cursor() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| {
            s.switch_room(Room::Discord);
            s.open_discord_guild("g-exo");
            s.open_discord_channel("ch-general");
        });
        rt.frame(0.0);
        let (x, y) = rt.find("nav-library").expect("library tab");
        rt.pointer_move(x, y);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), rune::Cursor::Pointer, "nav tab cursor");
        let (x, y) = rt.find("search-capsule").expect("search");
        rt.pointer_move(x, y);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), rune::Cursor::Pointer, "search cursor");
        rt.update(|s, _| {
            s.open_context_menu(crate::state::ContextMenuTarget::DiscordStatusMenu, 0.0, 0.0)
        });
        for _ in 0..90 {
            rt.frame(1.0 / 60.0);
        }
        let (x, y) = rt.find("dc-set-status-idle").expect("status item");
        rt.pointer_move(x, y);
        for _ in 0..90 {
            rt.frame(1.0 / 60.0);
        }
        assert_eq!(
            rt.cursor(),
            rune::Cursor::Pointer,
            "menu item with submenu cursor"
        );
        let el = rt
            .elements()
            .into_iter()
            .find(|e| e.key.as_deref() == Some("dc-set-status-idle"))
            .expect("item el");
        let (ex, ey, eh) = (el.rect[0], el.rect[1], el.rect[3]);
        rt.pointer_move(ex + 3.0, ey + eh * 0.5);
        for _ in 0..90 {
            rt.frame(1.0 / 60.0);
        }
        assert_eq!(rt.cursor(), rune::Cursor::Pointer, "menu item edge cursor");
        let sub_key = rt
            .elements()
            .into_iter()
            .filter_map(|e| e.key)
            .find(|k| k.starts_with("dc-status-idle-"))
            .expect("submenu item");
        let (sx, sy) = rt.find(&sub_key).expect("submenu item position");
        rt.pointer_move(sx, sy);
        for _ in 0..90 {
            rt.frame(1.0 / 60.0);
        }
        assert_eq!(rt.cursor(), rune::Cursor::Pointer, "submenu item cursor");
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        rt.frame(1.0 / 60.0);
        rt.update(|s, _| {
            let status = s.discord.home.me.as_ref().and_then(|m| m.status.clone());
            eprintln!(
                "SUBMENU key={sub_key} status={status:?} menu_open={}",
                s.context_menu.is_some()
            );
        });
    }

    #[test]
    fn blur_budget_and_glass_palette_invariants() {
        let check_case = |label: &str,
                          setup: &dyn Fn(&mut AppState),
                          expected_overlay: Option<&str>| {
            let mut state = AppState::default();
            setup(&mut state);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
            rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
            rt.frame(0.0);
            rt.frame(1.0 / 60.0);
            let adhoc_blurs = rt
                .instances
                .iter()
                .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
                .count();
            assert_eq!(adhoc_blurs, 0, "{label}: unexpected ad-hoc backdrop_blur");
            let glass_rects: Vec<[f32; 4]> = rt
                .liquid_glass_frame()
                .shapes
                .iter()
                .map(|sh| sh.rect)
                .collect();
            let glass_draws = rt
                .instances
                .iter()
                .filter(|i| i.params[1] == KIND_LIQUID_GLASS)
                .count();
            assert!(
                glass_draws <= exo_kit::EXO_MAX_BACKDROP_BLURS,
                "{label}: emitted {glass_draws} liquid glass draws (cap {})",
                exo_kit::EXO_MAX_BACKDROP_BLURS
            );
            if let Some(key) = expected_overlay {
                let (rect, _) = rt
                    .presented(key)
                    .unwrap_or_else(|| panic!("{label}: overlay {key} not presented"));
                let hit = glass_rects.iter().any(|r| {
                    (r[0] - rect[0]).abs() < 2.0
                        && (r[1] - rect[1]).abs() < 2.0
                        && (r[2] - rect[2]).abs() < 2.0
                        && (r[3] - rect[3]).abs() < 2.0
                });
                assert!(
                    hit,
                    "{label}: overlay {key} rect {rect:?} missing from liquid glass shapes: {glass_rects:?}"
                );
            }
            rt
        };

        check_case("library-normal", &|_| {}, None);
        check_case(
            "music-normal",
            &|s| {
                s.switch_room(Room::Music);
            },
            None,
        );
        check_case(
            "music-capture-menu",
            &|s| {
                s.switch_room(Room::Music);
                s.capture_menu_open = true;
            },
            Some("capture-popover-menu"),
        );
        let rt_palette = check_case(
            "library-mini-palette",
            &|s| {
                s.palette_open = true;
            },
            Some("command-palette"),
        );
        let (pal_rect, _) = rt_palette
            .presented("command-palette")
            .expect("command-palette presented");
        assert!(
            pal_rect[3] <= 821.3,
            "palette height {} exceeded 821px cap",
            pal_rect[3]
        );
        check_case(
            "settings-key-modal",
            &|s| {
                s.switch_room(Room::Settings);
                s.key_modal_open = true;
            },
            Some("activate-key-modal"),
        );
        check_case(
            "library-tour",
            &|s| {
                s.start_tour();
            },
            Some("exo-tour-card"),
        );
        check_case(
            "library-more-menu",
            &|s| {
                s.library_more_open = true;
            },
            Some("library-more-menu"),
        );
        let rt_settings = check_case(
            "settings-void-top",
            &|s| {
                s.switch_room(Room::Settings);
                s.media.playback = None;
            },
            None,
        );
        let settings_blur_rects: Vec<[f32; 4]> = rt_settings
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .map(|i| i.rect)
            .collect();
        assert!(
            settings_blur_rects.len() <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "void-top Settings room emitted {} backdrop blurs (cap {}): {settings_blur_rects:?}",
            settings_blur_rects.len(),
            exo_kit::EXO_MAX_BACKDROP_BLURS
        );
    }

    #[test]
    fn notice_stack_queue_caps_at_three_and_escape_dismisses_newest() {
        let mut s = AppState::default();
        let id1 = s.push_notice("First notice", state::NoticeTone::Neutral);
        let id2 = s.push_notice("Second notice", state::NoticeTone::Good);
        let id3 = s.push_notice("Third notice", state::NoticeTone::Bad);
        let id4 = s.push_notice("Fourth notice", state::NoticeTone::Neutral);

        assert_eq!(s.notices.len(), state::MAX_NOTICES);
        assert_eq!(
            s.notices.iter().map(|n| n.id).collect::<Vec<_>>(),
            vec![id2, id3, id4]
        );
        assert_ne!(s.notices[0].id, id1);

        assert!(s.dismiss_top_layer());
        assert_eq!(
            s.notices.iter().map(|n| n.id).collect::<Vec<_>>(),
            vec![id2, id3]
        );
        assert!(s.dismiss_top_layer());
        assert_eq!(
            s.notices.iter().map(|n| n.id).collect::<Vec<_>>(),
            vec![id2]
        );
        assert!(s.dismiss_top_layer());
        assert!(s.notices.is_empty());
        assert!(s.notice.is_none());
    }

    #[test]
    fn notice_stack_syncs_legacy_notice_and_auto_dismisses_with_hover_pause() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.poll = Some(poll_state);
        rt.frame(0.0);
        assert!(rt.state.notices.is_empty());

        rt.update(|s, _| {
            s.notice = Some("Clip saved".into());
        });
        rt.frame(0.016);
        assert_eq!(rt.state.notices.len(), 1);
        let id = rt.state.notices[0].id;
        assert_eq!(rt.state.notices[0].tone, state::NoticeTone::Good);
        assert!(rt.presented(&format!("notice-{id}")).is_some());

        rt.frame(1.5);
        assert_eq!(rt.state.notices.len(), 1);
        assert!(rt.state.notices[0].remaining_secs < 2.6);

        rt.update(|s, tx| s.set_notice_hovered(id, true, tx));
        rt.frame(3.0);
        assert_eq!(rt.state.notices.len(), 1);
        assert!(
            rt.state.notices[0].remaining_secs > 2.4,
            "hovered toast timer should remain paused, got {}",
            rt.state.notices[0].remaining_secs
        );

        rt.update(|s, tx| s.set_notice_hovered(id, false, tx));
        rt.frame(2.6);
        assert!(rt.state.notices.is_empty());
        assert!(rt.state.notice.is_none());
    }

    #[test]
    fn notice_stack_swipe_dismiss_and_snap_back_thresholds() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.poll = Some(poll_state);
        rt.frame(0.0);

        let id_partial = rt
            .state
            .push_notice("Partial swipe", state::NoticeTone::Neutral);
        rt.frame(0.016);
        rt.update(|s, tx| {
            s.begin_notice_drag(id_partial, tx.clock());
            s.update_notice_drag(id_partial, 64.0, tx.clock() + 0.5);
            s.end_notice_drag(id_partial, false, tx);
        });
        assert_eq!(rt.state.notices.len(), 1);
        assert_eq!(rt.state.notices[0].drag_x, 0.0);

        rt.update(|s, tx| {
            s.begin_notice_drag(id_partial, tx.clock());
            s.update_notice_drag(id_partial, notice::SWIPE_DISMISS_DX + 2.0, tx.clock() + 0.5);
            s.end_notice_drag(id_partial, true, tx);
        });
        assert_eq!(rt.state.notices.len(), 1);
        assert_eq!(rt.state.notices[0].drag_x, 0.0);

        rt.update(|s, tx| {
            s.begin_notice_drag(id_partial, tx.clock());
            s.update_notice_drag(
                id_partial,
                notice::SWIPE_DISMISS_DX + 10.0,
                tx.clock() + 0.5,
            );
            s.end_notice_drag(id_partial, false, tx);
        });
        assert!(rt.state.notices.is_empty());

        let id_flick = rt
            .state
            .push_notice("Fast flick", state::NoticeTone::Neutral);
        rt.frame(0.016);
        rt.update(|s, tx| {
            let t0 = tx.clock();
            s.begin_notice_drag(id_flick, t0);
            s.update_notice_drag(id_flick, 36.0, t0 + 0.02);
            s.update_notice_drag(id_flick, 72.0, t0 + 0.04);
            s.end_notice_drag(id_flick, false, tx);
        });
        assert!(rt.state.notices.is_empty());
    }

    #[test]
    fn notice_stack_respects_blur_budget_with_mini_player_and_music() {
        for (label, setup) in [
            (
                "library+mini+3-toasts",
                Box::new(|s: &mut AppState| {
                    s.switch_room(Room::Library);
                    s.push_notice("One", state::NoticeTone::Neutral);
                    s.push_notice("Two", state::NoticeTone::Good);
                    s.push_notice("Three", state::NoticeTone::Bad);
                }) as Box<dyn Fn(&mut AppState)>,
            ),
            (
                "music+transport+3-toasts",
                Box::new(|s: &mut AppState| {
                    s.switch_room(Room::Music);
                    s.push_notice("One", state::NoticeTone::Neutral);
                    s.push_notice("Two", state::NoticeTone::Good);
                    s.push_notice("Three", state::NoticeTone::Bad);
                }),
            ),
            (
                "palette+3-toasts",
                Box::new(|s: &mut AppState| {
                    s.switch_room(Room::Library);
                    s.palette_open = true;
                    s.push_notice("One", state::NoticeTone::Neutral);
                    s.push_notice("Two", state::NoticeTone::Good);
                    s.push_notice("Three", state::NoticeTone::Bad);
                }),
            ),
        ] {
            let mut state = AppState::default();
            setup(&mut state);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
            rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
            rt.poll = Some(poll_state);
            rt.frame(0.0);
            let blur_count = rt
                .instances
                .iter()
                .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
                .count();
            assert!(
                blur_count <= exo_kit::EXO_MAX_BACKDROP_BLURS,
                "{label}: emitted {blur_count} backdrop blurs (cap {})",
                exo_kit::EXO_MAX_BACKDROP_BLURS
            );
        }
    }

    #[test]
    fn top_bar_nav_x_is_invariant_across_mini_player_and_popover_states() {
        for (vw, vh) in [(1920.0_f32, 1080.0_f32), (1280.0_f32, 800.0_f32)] {
            let mut rt = Runtime::new(
                AppState::default(),
                shell::view,
                (vw, vh),
                Env::new(true, 1.0),
            );
            rt.frame(0.0);
            let (nav_with_mini, _) = rt.presented("nav-capsule").expect("nav with mini");
            let (mini_rect, _) = rt
                .presented("music-mini-player")
                .expect("mini player present");
            let (tools_rect, _) = rt.presented("titlebar-tools").expect("tools present");
            if vw >= 1900.0 {
                assert!(
                    mini_rect[0] >= nav_with_mini[0] + nav_with_mini[2] + 12.0,
                    "mini player overlapped nav at {vw}px: nav={nav_with_mini:?} mini={mini_rect:?}"
                );
                assert!(
                    mini_rect[0] + mini_rect[2] <= tools_rect[0],
                    "mini player overlapped tools at {vw}px: mini={mini_rect:?} tools={tools_rect:?}"
                );
            }

            rt.update(|s, tx| {
                tx.instant();
                s.capture_menu_open = true;
            });
            rt.frame(1.0 / 60.0);
            let (nav_with_capture, _) = rt.presented("nav-capsule").expect("nav with capture");
            assert_eq!(
                nav_with_mini[0], nav_with_capture[0],
                "nav x shifted on capture_menu_open at {vw}px"
            );

            rt.update(|s, tx| {
                tx.instant();
                s.capture_menu_open = false;
                s.media.playback = None;
            });
            rt.frame(2.0 / 60.0);
            let (nav_without_mini, _) = rt.presented("nav-capsule").expect("nav without mini");
            assert_eq!(
                nav_with_mini[0], nav_without_mini[0],
                "nav x shifted when mini_player disappeared at {vw}px"
            );
        }

        let mut rt_narrow = Runtime::new(
            AppState::default(),
            shell::view,
            (1100.0, 800.0),
            Env::new(true, 1.0),
        );
        rt_narrow.frame(0.0);
        rt_narrow.frame(1.0 / 60.0);
        rt_narrow.frame(0.25);
        assert_eq!(rt_narrow.state.mini_bar_slot, state::MiniBarSlot::Hidden);
        assert!(rt_narrow.presented("music-mini-player").is_none());

        let mut s = AppState::default();
        s.switch_room(Room::Music);
        assert_eq!(s.room_dir, 1.0);
        s.switch_room(Room::Store);
        assert_eq!(s.room_dir, -1.0);
    }

    #[test]
    fn mini_player_stays_inline_and_clears_nav_and_room_headers() {
        for (vw, expected_slot) in [
            (1920.0_f32, state::MiniBarSlot::Inline),
            (1480.0_f32, state::MiniBarSlot::Medium),
            (1280.0_f32, state::MiniBarSlot::Compact),
            (1180.0_f32, state::MiniBarSlot::Compact),
        ] {
            let mut state = AppState::default();
            state.switch_room(Room::ExoAi);
            let mut rt = Runtime::new(state, shell::view, (vw, 800.0), Env::new(true, 1.0));
            rt.frame(0.0);
            rt.frame(1.0 / 60.0);
            rt.frame(0.5);
            assert_eq!(rt.state.mini_bar_slot, expected_slot, "width={vw}");
            let (nav, _) = rt.presented("nav-capsule").expect("nav");
            let (tools, _) = rt.presented("titlebar-tools").expect("tools");
            let (mini, _) = rt.presented("music-mini-player").expect("mini");
            let (storage, _) = rt.presented("pc-tab-storage").expect("storage");
            assert!(
                mini[1] + mini[3] <= 52.0,
                "mini intruded below top bar at {vw}px: mini={mini:?}"
            );
            assert!(
                mini[0] >= nav[0] + nav[2] + 8.0,
                "mini overlapped nav at {vw}px: nav={nav:?} mini={mini:?}"
            );
            assert!(
                mini[0] + mini[2] <= tools[0],
                "mini overlapped tools at {vw}px: mini={mini:?} tools={tools:?}"
            );
            assert!(
                mini[1] + mini[3] < storage[1],
                "mini overlapped Exo AI storage button at {vw}px: mini={mini:?} storage={storage:?}"
            );
        }
    }

    fn assert_blur_outside_layers(rt: &Runtime<AppState>, key: &str, phase: &str) {
        let (rect, _) = rt
            .presented(key)
            .unwrap_or_else(|| panic!("{phase}: {key} not presented"));
        let mut open_layers: Vec<([f32; 4], f32)> = Vec::new();
        let mut total_glass_draws = 0usize;
        let mut matched_layers: Option<Vec<([f32; 4], f32)>> = None;
        let mut glass_rects: Vec<[f32; 4]> = Vec::new();
        let shapes = &rt.liquid_glass_frame().shapes;
        for inst in &rt.instances {
            let kind = inst.params[1];
            if kind == KIND_LAYER {
                open_layers.push((inst.rect, inst.fill[3]));
            } else if kind == KIND_LAYER_END {
                open_layers.pop();
            } else if kind == KIND_LIQUID_GLASS {
                total_glass_draws += 1;
                let first = inst.params[2].max(0.0).round() as usize;
                let count = inst.uv[0].max(1.0).round() as usize;
                for sh in shapes.iter().skip(first).take(count) {
                    glass_rects.push(sh.rect);
                    if (sh.rect[0] - rect[0]).abs() < 4.0
                        && (sh.rect[1] - rect[1]).abs() < 4.0
                        && (sh.rect[2] - rect[2]).abs() < 4.0
                        && (sh.rect[3] - rect[3]).abs() < 4.0
                    {
                        matched_layers = Some(open_layers.clone());
                    }
                }
            }
        }
        assert!(
            total_glass_draws <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "{phase}: emitted {total_glass_draws} liquid glass draws (cap {})",
            exo_kit::EXO_MAX_BACKDROP_BLURS
        );
        assert_eq!(
            matched_layers,
            Some(Vec::new()),
            "{phase}: {key} liquid glass must be emitted at layer_depth 0 (outside fading ancestor layers), got {matched_layers:?}; key rect {rect:?}, glass rects {glass_rects:?}"
        );
    }

    #[test]
    fn liquid_glass_blurs_from_frame_one_during_enter_and_exit() {
        let mut state = AppState::default();
        state.media.playback = None;
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
        rt.on_key = Some(on_key);
        rt.frame(0.0);
        rt.frame(0.5);

        let (cx, cy) = rt.find("top-capture-btn").expect("top-capture-btn");
        rt.pointer_down(cx, cy);
        rt.pointer_up(cx, cy);

        rt.frame(1.0 / 60.0);
        assert!(rt.state.capture_menu_open);
        assert_blur_outside_layers(&rt, "capture-popover-menu", "capture-frame-1");
        assert_blur_outside_layers(&rt, "nav-capsule", "nav-kept-blur-with-capture");

        for step in 2..=24 {
            rt.frame(1.0 / 60.0);
            assert_blur_outside_layers(
                &rt,
                "capture-popover-menu",
                &format!("capture-enter-step-{step}"),
            );
        }

        rt.key(KeyEvent {
            key: Key::Escape,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert!(!rt.state.capture_menu_open);
        if rt.presented("capture-popover-menu").is_some() {
            assert_blur_outside_layers(&rt, "capture-popover-menu", "capture-exit-frame-1");
        }
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }

        rt.update(|s, tx| {
            tx.animated();
            s.open_palette(tx);
        });
        rt.frame(1.0 / 60.0);
        assert_blur_outside_layers(&rt, "command-palette", "palette-frame-1");
        rt.update(|s, _| s.close_palette());
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }

        rt.update(|s, tx| {
            tx.animated();
            s.open_key_modal(tx);
        });
        rt.frame(1.0 / 60.0);
        assert_blur_outside_layers(&rt, "activate-key-modal", "key-modal-frame-1");
    }

    #[test]
    fn cursors_match_interactive_element_roles() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.frame(0.0);
        rt.frame(0.5);

        for key in [
            "search-capsule",
            "nav-library",
            "top-capture-btn",
            "nav-settings",
        ] {
            let (x, y) = rt.find(key).unwrap_or_else(|| panic!("missing {key}"));
            rt.pointer_move(x, y);
            rt.frame(1.0 / 60.0);
            assert_eq!(rt.cursor(), Cursor::Pointer, "expected Pointer over {key}");
        }

        rt.update(|s, tx| s.open_key_modal(tx));
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let (ix, iy) = rt.find("activate-key-input").expect("activate-key-input");
        rt.pointer_move(ix, iy);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), Cursor::Text);

        let (dx, dy) = rt.find("key-submit").expect("key-submit");
        rt.pointer_move(dx, dy);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), Cursor::NotAllowed);

        rt.pointer_move(10.0, 10.0);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), Cursor::Default);

        rt.update(|s, _| s.close_key_modal());
        let mut notice_id = 0_u64;
        rt.update(|s, _| {
            notice_id = s.push_notice("Saved clip", state::NoticeTone::Good);
        });
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let nkey = format!("notice-{notice_id}");
        let (nx, ny) = rt.find(&nkey).expect("notice card");
        rt.pointer_move(nx - 40.0, ny);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), Cursor::Grab);
        rt.pointer_down(nx - 40.0, ny);
        rt.pointer_move(nx - 20.0, ny);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), Cursor::Grabbing);
        rt.pointer_up(nx - 20.0, ny);
    }

    #[test]
    fn top_bar_context_menus_right_click_keyboard_and_submenus() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
        rt.on_key = Some(on_key);
        rt.frame(0.0);
        rt.frame(0.5);

        let (sx, sy) = rt.find("search-capsule").expect("search-capsule");
        rt.secondary_down(sx, sy);
        rt.secondary_up(sx, sy);
        for _ in 0..90 {
            rt.frame(1.0 / 60.0);
        }
        assert!(rt.presented("exo-ctx-menu").is_some());
        assert!(rt.presented("ctx-search-open").is_some());
        assert_blur_outside_layers(&rt, "exo-ctx-menu", "ctx-search-settled");
        rt.key(KeyEvent {
            key: Key::Escape,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert!(rt.state.context_menu.is_none());

        let (nx, ny) = rt.find("nav-library").expect("nav-library");
        rt.secondary_down(nx, ny);
        rt.secondary_up(nx, ny);
        for _ in 0..10 {
            rt.frame(1.0 / 60.0);
        }
        assert!(rt.presented("exo-ctx-menu").is_some());
        assert!(rt.presented("ctx-nav-rooms").is_some());

        for _ in 0..2 {
            rt.key(KeyEvent {
                key: Key::Down,
                mods: Mods::default(),
            });
            rt.frame(1.0 / 60.0);
        }
        rt.key(KeyEvent {
            key: Key::Right,
            mods: Mods::default(),
        });
        for _ in 0..10 {
            rt.frame(1.0 / 60.0);
        }
        assert!(rt.presented("exo-ctx-submenu").is_some());
        rt.key(KeyEvent {
            key: Key::Left,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert_eq!(
            rt.state.context_menu.as_ref().and_then(|c| c.open_submenu),
            None
        );
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        assert!(rt.presented("exo-ctx-submenu").is_none());
        rt.key(KeyEvent {
            key: Key::Right,
            mods: Mods::default(),
        });
        for _ in 0..10 {
            rt.frame(1.0 / 60.0);
        }
        assert!(rt.presented("exo-ctx-submenu").is_some());
        let (store_x, store_y) = rt
            .find("ctx-sub-room-nav-store")
            .expect("ctx-sub-room-nav-store");
        rt.pointer_down(store_x, store_y);
        rt.pointer_up(store_x, store_y);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.room, Room::Store);
        assert!(rt.state.context_menu.is_none());

        rt.update(|s, _| s.media.capture_status = None);
        rt.frame(1.0 / 60.0);
        let (cx, cy) = rt.find("top-capture-btn").expect("top-capture-btn");
        rt.secondary_down(cx, cy);
        rt.secondary_up(cx, cy);
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        assert!(rt.presented("exo-ctx-menu").is_some());
        let (rx, ry) = rt.find("ctx-cap-replay").expect("ctx-cap-replay");
        rt.pointer_move(rx, ry);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.cursor(), Cursor::Default);
        rt.key(KeyEvent {
            key: Key::Escape,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);

        let (set_x, set_y) = rt.find("nav-settings").expect("nav-settings");
        rt.secondary_down(set_x, set_y);
        rt.secondary_up(set_x, set_y);
        for _ in 0..10 {
            rt.frame(1.0 / 60.0);
        }
        assert!(rt.presented("exo-ctx-menu").is_some());
        let (ts_x, ts_y) = rt.find("ctx-set-text-size").expect("ctx-set-text-size");
        rt.pointer_down(ts_x, ts_y);
        rt.pointer_up(ts_x, ts_y);
        for _ in 0..10 {
            rt.frame(1.0 / 60.0);
        }
        assert!(rt.presented("exo-ctx-submenu").is_some());
        let (lg_x, lg_y) = rt.find("ctx-sub-text-large").expect("ctx-sub-text-large");
        rt.pointer_down(lg_x, lg_y);
        rt.pointer_up(lg_x, lg_y);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.settings.text_size.as_deref(), Some("large"));
        assert!(rt.state.context_menu.is_none());

        rt.update(|_, tx| tx.focus("nav-library", true));
        rt.frame(1.0 / 60.0);
        rt.key(KeyEvent {
            key: Key::ContextMenu,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert!(rt.presented("exo-ctx-menu").is_some());
        rt.key(KeyEvent {
            key: Key::Escape,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert!(rt.state.context_menu.is_none());

        rt.update(|_, tx| tx.focus("nav-library", true));
        rt.frame(1.0 / 60.0);
        rt.key(KeyEvent {
            key: Key::Function(10),
            mods: Mods {
                shift: true,
                ..Mods::default()
            },
        });
        rt.frame(1.0 / 60.0);
        assert!(rt.presented("exo-ctx-menu").is_some());
    }

    #[test]
    fn dropdowns_replace_all_five_fake_selects_with_pointer_and_keyboard() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
        rt.on_key = Some(on_key);
        rt.frame(0.0);
        rt.frame(0.5);

        rt.update(|s, _| s.switch_room(Room::Settings));
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let (tx, ty) = rt
            .find("set-text-size-select")
            .expect("set-text-size-select");
        rt.pointer_down(tx, ty);
        rt.pointer_up(tx, ty);
        for _ in 0..10 {
            rt.frame(1.0 / 60.0);
        }
        assert!(rt.presented("dd-text-size-menu").is_some());
        assert_blur_outside_layers(&rt, "dd-text-size-menu", "dropdown-frame-1");
        let (ox, oy) = rt
            .find("dd-text-size-opt-large")
            .expect("dd-text-size-opt-large");
        rt.pointer_down(ox, oy);
        rt.pointer_up(ox, oy);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.settings.text_size.as_deref(), Some("large"));
        assert!(rt.state.open_dropdown.is_none());

        rt.update(|_, tx| tx.focus("set-text-size-select", true));
        rt.frame(1.0 / 60.0);
        rt.key(KeyEvent {
            key: Key::Down,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert!(rt.presented("dd-text-size-menu").is_some());
        rt.key(KeyEvent {
            key: Key::Up,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        rt.key(KeyEvent {
            key: Key::Enter,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.settings.text_size.as_deref(), Some("normal"));
        assert!(rt.state.open_dropdown.is_none());

        rt.key(KeyEvent {
            key: Key::Down,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert!(rt.presented("dd-text-size-menu").is_some());
        rt.key(KeyEvent {
            key: Key::Escape,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert!(rt.state.open_dropdown.is_none());

        rt.update(|s, _| s.pc.settings_section = pc_state::SettingsSection::Updates);
        for _ in 0..20 {
            rt.frame(1.0 / 60.0);
        }
        let (mx, my) = rt
            .find("set-music-service-select")
            .expect("set-music-service-select");
        rt.pointer_down(mx, my);
        rt.pointer_up(mx, my);
        for _ in 0..10 {
            rt.frame(1.0 / 60.0);
        }
        let (yx, yy) = rt
            .find("dd-music-service-opt-youtube")
            .expect("dd-music-service-opt-youtube");
        rt.pointer_down(yx, yy);
        rt.pointer_up(yx, yy);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.media.music_service, MusicService::YouTube);

        rt.update(|s, _| {
            s.switch_room(Room::Library);
            s.library_all_games = true;
        });
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let (sx, sy) = rt.find("sort-cycle").expect("sort-cycle");
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        for _ in 0..10 {
            rt.frame(1.0 / 60.0);
        }
        let (ax, ay) = rt
            .find("dd-library-sort-opt-title")
            .expect("dd-library-sort-opt-title");
        rt.pointer_down(ax, ay);
        rt.pointer_up(ax, ay);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.sort, LibrarySort::Title);

        let first_id = rt.state.games[0].id.clone();
        rt.update(|s, _| {
            s.library_all_games = false;
            s.open_game_page(&first_id);
        });
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let (dx, dy) = rt
            .find("gp-display-mode-select")
            .expect("gp-display-mode-select");
        rt.pointer_down(dx, dy);
        rt.pointer_up(dx, dy);
        for _ in 0..10 {
            rt.frame(1.0 / 60.0);
        }
        let (fx, fy) = rt
            .find("dd-game-display-opt-fullscreen")
            .expect("dd-game-display-opt-fullscreen");
        rt.pointer_down(fx, fy);
        rt.pointer_up(fx, fy);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.game_display_mode, "fullscreen");

        let (lx, ly) = rt.find("gp-language-select").expect("gp-language-select");
        rt.pointer_down(lx, ly);
        rt.pointer_up(lx, ly);
        for _ in 0..10 {
            rt.frame(1.0 / 60.0);
        }
        let (jx, jy) = rt
            .find("dd-game-language-opt-japanese")
            .expect("dd-game-language-opt-japanese");
        rt.pointer_down(jx, jy);
        rt.pointer_up(jx, jy);
        rt.frame(1.0 / 60.0);
        assert_eq!(
            rt.state
                .game_props
                .as_ref()
                .and_then(|p| p.language.as_deref()),
            Some("japanese")
        );
    }
}

#[cfg(test)]
mod motion_audit {
    use super::*;
    use crate::state::Room;
    use rune::motion_gate::{Finding, Job, Recording, Run, check, report, run};
    use rune::{Env, Runtime};
    use std::sync::atomic::{AtomicBool, Ordering};

    const HZ: f64 = 120.0;

    fn make_run(reduce_motion: bool) -> Run<AppState> {
        let mut state = AppState::default();
        state.settings.reduce_motion = reduce_motion;
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.poll = Some(poll_state);
        let mut r = Run::new(rt, HZ);
        r.idle(0.5);
        r.step();
        r
    }

    fn boot_idle_recording(reduce_motion: bool) -> Recording {
        let mut r = make_run(reduce_motion);
        r.play(0.4);
        r.quiet();
        r.play(0.35);
        r.rec
    }

    fn room_switch_recording(reduce_motion: bool) -> Recording {
        let mut r = make_run(reduce_motion);
        for (name, room) in [
            ("to-store", Room::Store),
            ("to-music", Room::Music),
            ("to-settings", Room::Settings),
            ("to-library", Room::Library),
        ] {
            r.act(name, |rt| {
                rt.update(|s, tx| {
                    tx.animated();
                    s.switch_room(room);
                })
            });
            r.play(0.45);
        }
        r.quiet();
        r.play(0.35);
        r.rec
    }

    fn overlays_recording(reduce_motion: bool) -> Recording {
        let mut r = make_run(reduce_motion);
        r.act("open-captures", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.capture_menu_open = true;
            });
        });
        r.play(0.4);
        r.act("close-captures", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.capture_menu_open = false;
            });
        });
        r.play(0.4);
        r.act("open-palette", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.open_palette(tx);
            });
        });
        r.play(0.4);
        r.act("close-palette", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.close_palette();
            });
        });
        r.play(0.4);
        r.act("open-key-modal", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.key_modal_open = true;
            });
        });
        r.play(0.4);
        r.act("close-key-modal", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.close_key_modal();
            });
        });
        r.play(0.4);
        r.act("open-tour", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.start_tour();
            });
        });
        r.play(0.4);
        r.act("close-tour", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.finish_tour();
            });
        });
        r.play(0.4);
        r.quiet();
        r.play(0.35);
        r.rec
    }

    fn mini_player_recording(reduce_motion: bool) -> Recording {
        let mut r = make_run(reduce_motion);
        let saved = r.rt.state.media.playback.clone();
        r.act("hide-mini", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.media.playback = None;
            });
        });
        r.play(0.4);
        r.act("show-mini", |rt| {
            let track = saved.clone();
            rt.update(|s, tx| {
                tx.animated();
                s.media.playback = track;
            });
        });
        r.play(0.45);
        r.quiet();
        r.play(0.35);
        r.rec
    }

    fn mid_flight_reduce_motion_toggle() -> Recording {
        let mut r = make_run(false);
        r.act("open-key-modal", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.key_modal_open = true;
            });
        });
        r.play(0.08);
        r.act("enable-reduce-motion", |rt| {
            rt.update(|s, _| s.settings.reduce_motion = true);
        });
        r.play(0.4);
        r.act("close-key-modal", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.close_key_modal();
            });
        });
        r.play(0.4);
        r.quiet();
        r.play(0.35);
        r.rec
    }

    fn assert_no_kinds(label: &str, findings: &[Finding], forbidden: &[&str]) {
        let hits: Vec<&Finding> = findings
            .iter()
            .filter(|f| forbidden.contains(&f.kind))
            .collect();
        assert!(hits.is_empty(), "{label} motion findings: {hits:#?}");
    }

    #[test]
    fn reduce_motion_drives_cx_runtime_override() {
        static OBSERVED: AtomicBool = AtomicBool::new(false);

        fn spy_view(s: &AppState, cx: &mut rune::Cx<AppState>) -> rune::El {
            let root = shell::view(s, cx);
            OBSERVED.store(cx.is_reduced_motion(), Ordering::SeqCst);
            root
        }

        let mut rt = Runtime::new(
            AppState::default(),
            spy_view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.frame(0.0);
        assert!(!OBSERVED.load(Ordering::SeqCst));

        rt.update(|s, _| s.settings.reduce_motion = true);
        rt.frame(1.0 / 60.0);
        assert!(OBSERVED.load(Ordering::SeqCst));

        rt.update(|s, _| s.settings.reduce_motion = false);
        rt.frame(1.0 / 60.0);
        assert!(!OBSERVED.load(Ordering::SeqCst));
    }

    #[test]
    fn boot_idle_has_no_load_motion_or_wakes() {
        for reduced in [false, true] {
            let rec = boot_idle_recording(reduced);
            let findings = check(&rec);
            assert!(
                findings.is_empty(),
                "idle reduced={reduced} findings: {findings:#?}"
            );
        }
    }

    #[test]
    fn room_switches_and_overlays_settle_cleanly() {
        let jobs: Vec<(String, Job)> = vec![
            (
                "boot/normal".into(),
                Box::new(|| boot_idle_recording(false)),
            ),
            (
                "rooms/normal".into(),
                Box::new(|| room_switch_recording(false)),
            ),
            (
                "rooms/reduced".into(),
                Box::new(|| room_switch_recording(true)),
            ),
            (
                "overlays/normal".into(),
                Box::new(|| overlays_recording(false)),
            ),
            (
                "overlays/reduced".into(),
                Box::new(|| overlays_recording(true)),
            ),
            (
                "mini-player/normal".into(),
                Box::new(|| mini_player_recording(false)),
            ),
            (
                "mini-player/reduced".into(),
                Box::new(|| mini_player_recording(true)),
            ),
            (
                "toggle/mid-flight".into(),
                Box::new(mid_flight_reduce_motion_toggle),
            ),
        ];
        let cells = run(jobs, "");
        let summary = report(&cells, &[], false, 0.0);
        assert_eq!(cells.len(), 8);
        for cell in &cells {
            assert!(!cell.skipped, "cell {} skipped", cell.name);
            assert_no_kinds(
                &cell.name,
                &cell.findings,
                &[
                    "load-motion",
                    "never-settles",
                    "wakes-after-end",
                    "overshoot",
                    "jerk",
                    "flicker",
                ],
            );
        }
        assert!(summary.text.contains("8 cells") || !summary.text.is_empty());
    }

    #[test]
    fn reduce_motion_eliminates_spatial_travel_on_room_and_overlay_changes() {
        let mut r = make_run(true);
        r.act("to-store", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.switch_room(Room::Store);
            });
        });
        let mut prev_pos: Option<[f32; 4]> = r.rt.presented("room-store").map(|(rect, _)| rect);
        for _ in 0..30 {
            r.step();
            if let Some((rect, _)) = r.rt.presented("room-store") {
                if let Some(prev) = prev_pos {
                    assert!(
                        (rect[0] - prev[0]).abs() < 0.01 && (rect[1] - prev[1]).abs() < 0.01,
                        "unexpected spatial translation under reduce_motion: {prev:?} -> {rect:?}"
                    );
                }
                prev_pos = Some(rect);
            }
        }
        r.act("open-key-modal", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.key_modal_open = true;
            });
        });
        let mut modal_prev: Option<[f32; 4]> =
            r.rt.presented("activate-key-modal").map(|(rect, _)| rect);
        for _ in 0..30 {
            r.step();
            if let Some((rect, _)) = r.rt.presented("activate-key-modal") {
                if let Some(prev) = modal_prev {
                    assert!(
                        (rect[0] - prev[0]).abs() < 0.01 && (rect[1] - prev[1]).abs() < 0.01,
                        "modal translated under reduce_motion: {prev:?} -> {rect:?}"
                    );
                }
                modal_prev = Some(rect);
            }
        }
    }

    fn notice_stack_recording(reduce_motion: bool) -> Recording {
        let mut r = make_run(reduce_motion);
        let mut toast_id = 0_u64;
        r.act("push-notice", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                toast_id = s.push_notice("Clip saved to Captures", state::NoticeTone::Good);
            });
        });
        r.play(0.45);
        r.act("drag-start", |rt| {
            rt.update(|s, tx| s.begin_notice_drag(toast_id, tx.clock()));
        });
        for step in 1..=6 {
            let x = step as f32 * 10.0;
            r.act("drag-move", |rt| {
                rt.update(|s, tx| s.update_notice_drag(toast_id, x, tx.clock()));
            });
            r.step();
        }
        r.act("drag-snap-back", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.end_notice_drag(toast_id, false, tx);
            });
        });
        r.play(0.45);
        r.act("swipe-start", |rt| {
            rt.update(|s, tx| s.begin_notice_drag(toast_id, tx.clock()));
        });
        for step in 1..=6 {
            let x = step as f32 * 22.0;
            r.act("swipe-move", |rt| {
                rt.update(|s, tx| s.update_notice_drag(toast_id, x, tx.clock()));
            });
            r.step();
        }
        r.act("swipe-dismiss", |rt| {
            rt.update(|s, tx| {
                tx.animated();
                s.end_notice_drag(toast_id, false, tx);
            });
        });
        r.play(0.45);
        r.quiet();
        r.play(0.35);
        r.rec
    }

    #[test]
    fn notice_stack_enter_drag_snap_back_and_dismiss_settle_cleanly() {
        for (label, reduced) in [("notice/normal", false), ("notice/reduced", true)] {
            let rec = notice_stack_recording(reduced);
            let findings = check(&rec);
            assert_no_kinds(
                label,
                &findings,
                &[
                    "pop-in",
                    "cut-exit",
                    "overshoot",
                    "never-settles",
                    "wakes-after-end",
                    "flicker",
                ],
            );
        }
    }

    #[test]
    fn devices_mouse_card_opens_hosted_panel_and_back_or_room_switch_closes_it() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.state.switch_room(Room::Devices);
        rt.frame(0.0);

        let (mx, my) = rt.find("dev-card-dev:mouse").expect("mouse device card");
        rt.pointer_down(mx, my);
        rt.pointer_up(mx, my);
        rt.frame(1.0 / 60.0);
        rt.frame(2.0 / 60.0);

        assert!(rt.state.pc.mouse_panel_open);
        assert!(rt.find("dev-back").is_some(), "native Devices back button");
        assert!(
            rt.find("mouse-panel-slot").is_some(),
            "mouse panel layout slot"
        );
        let rect = rt
            .state
            .pc
            .mouse_panel_rect
            .expect("mouse panel rect from on_layout");
        assert!(
            rect[2] > 1000.0 && rect[3] > 600.0,
            "unexpected slot size: {rect:?}"
        );
        assert!(
            rect[1] >= 80.0 && rect[1] < 140.0,
            "slot should start below Bar and back header: {rect:?}"
        );

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == rune::render::KIND_BACKDROP_BLUR)
            .count();
        assert!(blurs <= 4, "blur cap exceeded on mouse panel view: {blurs}");

        assert!(rt.state.mouse_panel_should_be_visible());
        rt.state.palette_open = true;
        assert!(!rt.state.mouse_panel_should_be_visible());
        rt.state.palette_open = false;
        assert!(rt.state.mouse_panel_should_be_visible());

        let (bx, by) = rt.find("dev-back").expect("back button");
        rt.pointer_down(bx, by);
        rt.pointer_up(bx, by);
        rt.frame(3.0 / 60.0);
        assert!(!rt.state.pc.mouse_panel_open);
        assert!(
            rt.find("dev-card-dev:mouse").is_some(),
            "back returns to Devices cards"
        );

        let (mx2, my2) = rt.find("dev-card-dev:mouse").expect("mouse card again");
        rt.pointer_down(mx2, my2);
        rt.pointer_up(mx2, my2);
        rt.frame(4.0 / 60.0);
        assert!(rt.state.pc.mouse_panel_open);
        rt.state.switch_room(Room::Library);
        assert!(!rt.state.pc.mouse_panel_open);

        rt.state.switch_room(Room::Devices);
        rt.state.open_mouse_panel("dev:mouse".into());
        assert!(rt.state.dismiss_top_layer());
        assert!(!rt.state.pc.mouse_panel_open);
    }

    #[test]
    fn wp3_global_shortcuts_and_modal_holds() {
        let mut s = AppState::default();
        let mut tx = Tx::default();
        let ctrl = rune::Mods {
            ctrl: true,
            ..Default::default()
        };
        let alt = rune::Mods {
            alt: true,
            ..Default::default()
        };
        let ctrl_shift = rune::Mods {
            ctrl: true,
            shift: true,
            ..Default::default()
        };

        assert!(!s.library_all_games);
        assert!(on_key(
            &mut s,
            &KeyEvent {
                key: Key::Command('a'),
                mods: ctrl,
            },
            &mut tx
        ));
        assert!(s.library_all_games);

        assert!(on_key(
            &mut s,
            &KeyEvent {
                key: Key::Command('f'),
                mods: ctrl,
            },
            &mut tx
        ));
        assert!(s.library_search_open);

        s.switch_room(Room::Store);
        s.switch_room(Room::Music);
        assert_eq!(s.room, Room::Music);
        assert!(on_key(
            &mut s,
            &KeyEvent {
                key: Key::Left,
                mods: alt,
            },
            &mut tx
        ));
        assert_eq!(s.room, Room::Store);
        assert!(on_key(
            &mut s,
            &KeyEvent {
                key: Key::Right,
                mods: alt,
            },
            &mut tx
        ));
        assert_eq!(s.room, Room::Music);

        s.switch_room(Room::Discord);
        let muted_before = s.discord.voice.muted;
        assert!(on_key(
            &mut s,
            &KeyEvent {
                key: Key::Command('m'),
                mods: ctrl_shift,
            },
            &mut tx
        ));
        assert_eq!(s.discord.voice.muted, !muted_before);

        let deaf_before = s.discord.voice.deafened;
        assert!(on_key(
            &mut s,
            &KeyEvent {
                key: Key::Command('d'),
                mods: ctrl_shift,
            },
            &mut tx
        ));
        assert_eq!(s.discord.voice.deafened, !deaf_before);

        s.whats_new.sheet_open = true;
        assert!(on_key(
            &mut s,
            &KeyEvent {
                key: Key::Command('k'),
                mods: ctrl,
            },
            &mut tx
        ));
        assert!(!s.palette_open);
    }

    #[test]
    fn wp3_mini_player_seek_and_mute_state() {
        let mut s = AppState::default();
        s.media.music_volume = 0.65;
        s.media.music_muted = false;
        s.toggle_mini_mute();
        assert!(s.media.music_muted);
        assert!((s.chrome.mini_last_audible - 0.65).abs() < 0.01);

        s.media.music_volume = 0.0;
        s.toggle_mini_mute();
        assert!(!s.media.music_muted);
        assert!((s.media.music_volume - 0.65).abs() < 0.01);

        s.seek_music_ms(42_000);
        if let Some(p) = s.media.playback.as_ref() {
            assert_eq!(p.position_ms, Some(42_000));
        }
    }

    #[test]
    fn wp7_top_bar_cursor_stays_pointer_over_open_discord_menus() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| {
            s.switch_room(Room::Discord);
            s.open_discord_guild("g-exo");
            s.open_discord_channel("ch-general");
        });
        rt.frame(0.0);

        for target in [
            crate::state::ContextMenuTarget::DiscordStatusMenu,
            crate::state::ContextMenuTarget::DiscordChannel {
                channel_id: "ch-general".into(),
                name: "general".into(),
                guild_id: "g-exo".into(),
                kind: "text".into(),
                unread: false,
                muted: false,
                can_edit: true,
                can_manage: true,
            },
            crate::state::ContextMenuTarget::DiscordDm {
                dm_id: "dm-aria".into(),
                user_id: Some("u-aria".into()),
                name: "Aria".into(),
                username: Some("aria".into()),
                unread: false,
                muted: false,
                group: false,
            },
        ] {
            rt.update(|s, _| s.open_context_menu(target.clone(), 420.0, 320.0));
            for _ in 0..20 {
                rt.frame(1.0 / 60.0);
            }
            let (nx, ny) = rt.find("nav-library").expect("nav-library");
            rt.pointer_move(nx, ny);
            rt.frame(1.0 / 60.0);
            assert_eq!(
                rt.cursor(),
                rune::Cursor::Pointer,
                "nav-library should keep pointer cursor over {target:?}"
            );
            let (sx, sy) = rt.find("search-capsule").expect("search-capsule");
            rt.pointer_move(sx, sy);
            rt.frame(1.0 / 60.0);
            assert_eq!(
                rt.cursor(),
                rune::Cursor::Pointer,
                "search-capsule should keep pointer cursor over {target:?}"
            );
        }
    }

    #[test]
    fn wp7_event_registry_handles_all_new_discord_push_events() {
        let mut s = AppState::default();
        s.switch_room(Room::Discord);

        s.apply_host_event(exo_rpc::HostEvent::DiscordCall(exo_rpc::DiscordCallEvent {
            channel_id: "dm-aria".into(),
            ringing: true,
        }));
        assert_eq!(
            s.discord.voice_ui.ringing_channel_id.as_deref(),
            Some("dm-aria")
        );

        s.apply_host_event(exo_rpc::HostEvent::DiscordCall(exo_rpc::DiscordCallEvent {
            channel_id: "dm-aria".into(),
            ringing: false,
        }));
        assert!(s.discord.voice_ui.ringing_channel_id.is_none());

        s.apply_host_event(exo_rpc::HostEvent::DiscordCallSong(
            exo_rpc::DiscordCallSong {
                user_id: "u2".into(),
                name: "Aria".into(),
                avatar: None,
                rich: Some(exo_rpc::DiscordRich {
                    name: "Spotify".into(),
                    details: Some("Synthwave".into()),
                    state: Some("Artist".into()),
                    ..Default::default()
                }),
                paused: false,
            },
        ));
        assert_eq!(s.discord.voice_ui.call_songs.len(), 1);

        s.apply_host_event(exo_rpc::HostEvent::DiscordCamera {
            state: "live".into(),
        });
        assert!(s.discord.voice_ui.camera_on);

        s.apply_host_event(exo_rpc::HostEvent::DiscordColors {
            guild_id: "g-exo".into(),
        });

        s.apply_host_event(exo_rpc::HostEvent::DiscordKeybind {
            action: "TOGGLE_STREAMER_MODE".into(),
        });
        assert!(s.discord.settings_ext.streamer_mode);

        s.apply_host_event(exo_rpc::HostEvent::DiscordMicLevel(
            exo_rpc::DiscordMicLevel {
                level: 0.55,
                voiced: true,
                ..Default::default()
            },
        ));
        assert!(
            s.discord
                .settings_ext
                .mic_level
                .as_ref()
                .is_some_and(|m| m.voiced)
        );

        s.apply_host_event(exo_rpc::HostEvent::DiscordUserSettings(
            serde_json::json!({ "developerMode": true, "messageDisplayCompact": true }),
        ));
        assert!(s.discord.developer_mode);
        assert!(s.discord.chat_compact);
    }

    #[test]
    fn wp7_voice_island_incoming_call_golive_and_settings_render_cleanly() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.update(|s, _| {
            s.switch_room(Room::Library);
            s.discord.voice_ui.ringing_channel_id = Some("dm-aria".into());
        });
        rt.frame(0.0);
        assert!(
            rt.find("dc-incoming-call").is_some(),
            "incoming call overlay over Library"
        );

        rt.update(|s, _| {
            s.discord.voice_ui.ringing_channel_id = None;
            s.discord.voice.state = "connected".into();
            s.discord.voice.channel_id = Some("ch-voice".into());
            s.discord.voice.guild_id = Some("g-exo".into());
        });
        rt.frame(1.0 / 60.0);
        assert!(
            rt.find("exo-island-call").is_some(),
            "Exo Island call capsule in top bar over Library"
        );

        rt.update(|s, _| {
            s.switch_room(Room::Discord);
            s.open_discord_settings(crate::discord_state::DiscordSettingsPage::Account);
        });
        rt.frame(2.0 / 60.0);
        assert!(rt.find("dc-settings-modal").is_some());
        for page in [
            crate::discord_state::DiscordSettingsPage::Profiles,
            crate::discord_state::DiscordSettingsPage::Voice,
            crate::discord_state::DiscordSettingsPage::Notifications,
            crate::discord_state::DiscordSettingsPage::Chat,
            crate::discord_state::DiscordSettingsPage::Privacy,
            crate::discord_state::DiscordSettingsPage::Keybinds,
            crate::discord_state::DiscordSettingsPage::ConnectedApps,
        ] {
            rt.update(|s, _| s.open_discord_settings(page));
            rt.frame(3.0 / 60.0);
            assert!(rt.find("dc-settings-modal").is_some());
        }
        assert!(rt.state.dismiss_top_layer());
        assert!(!rt.state.discord.settings_open);

        rt.update(|s, _| {
            s.discord.shop_open = true;
        });
        rt.frame(4.0 / 60.0);
        assert!(rt.find("dc-shop-view").is_some());
    }

    #[test]
    fn wp7_pinned_dm_selected_first_and_categories_sync_collapsed() {
        let mut s = AppState::default();
        s.discord.home.dms = vec![
            exo_rpc::DiscordDm {
                id: "dm-colton".into(),
                name: "Colton".into(),
                ..Default::default()
            },
            exo_rpc::DiscordDm {
                id: "dm-lazy".into(),
                name: "lazy".into(),
                ..Default::default()
            },
        ];
        s.settings
            .ui_prefs
            .insert("discordPinnedDms".into(), serde_json::json!(["dm-lazy"]));
        assert_eq!(s.default_dm_id().as_deref(), Some("dm-lazy"));

        let chans = exo_rpc::DiscordChannels {
            channels: vec![
                exo_rpc::DiscordChannel {
                    id: "cat-open".into(),
                    name: "COMMUNITY".into(),
                    kind: "category".into(),
                    collapsed: false,
                    ..Default::default()
                },
                exo_rpc::DiscordChannel {
                    id: "cat-staff".into(),
                    name: "STAFF".into(),
                    kind: "category".into(),
                    collapsed: true,
                    ..Default::default()
                },
            ],
            ..Default::default()
        };
        s.sync_collapsed_categories(&chans);
        assert!(!s.discord.collapsed_categories.contains("cat-open"));
        assert!(s.discord.collapsed_categories.contains("cat-staff"));
    }

    #[test]
    fn liquid_glass_bar_grouping_interactive_reduced_motion_and_a11y() {
        use rune::render::{KIND_BACKDROP_BLUR, KIND_LIQUID_GLASS};
        let mut state = AppState::default();
        state.palette_open = false;
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let adhoc = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert_eq!(adhoc, 0, "expected zero ad-hoc backdrop_blur instances");

        let bar_container = rt
            .instances
            .iter()
            .find(|i| {
                i.params[1] == KIND_LIQUID_GLASS
                    && i.uv[0] >= 3.0
                    && (i.uv[1] - exo_kit::GLASS_CONTAINER_SPACING).abs() < 0.5
            })
            .expect("top-bar glass_container must group Bar capsules into one draw");
        assert!(
            bar_container.uv[0] >= 3.0,
            "Bar container should hold >=3 pills, got {}",
            bar_container.uv[0]
        );

        let (sx, sy) = rt.find("search-capsule").expect("search-capsule");
        rt.pointer_move(sx, sy);
        rt.pointer_down(sx, sy);
        for step in 2..=6 {
            rt.frame(step as f64 / 60.0);
        }
        let search_shape_live = rt
            .liquid_glass_frame()
            .shapes
            .iter()
            .find(|sh| sh.rect[0] < 30.0 && sh.rect[1] < 20.0)
            .expect("search-capsule liquid glass shape");
        assert!(
            search_shape_live.glow[3] > 0.0,
            "interactive search-capsule must emit touch glow on press"
        );
        rt.pointer_up(sx, sy);
        rt.frame(7.0 / 60.0);

        rt.update(|s, _| s.settings.reduce_motion = true);
        rt.set_system_reduced_motion(true);
        rt.frame(8.0 / 60.0);
        rt.settle_all();
        rt.frame(9.0 / 60.0);
        rt.pointer_move(sx, sy);
        rt.pointer_down(sx, sy);
        for step in 10..=14 {
            rt.frame(step as f64 / 60.0);
        }
        let search_shape_rm = rt
            .liquid_glass_frame()
            .shapes
            .iter()
            .find(|sh| sh.rect[0] < 30.0 && sh.rect[1] < 20.0)
            .expect("search-capsule liquid glass shape under reduced motion");
        assert_eq!(
            search_shape_rm.glow[3], 0.0,
            "touch glow must be disabled under reduced motion"
        );
        rt.pointer_up(sx, sy);

        rt.update(|s, _| s.settings.reduce_motion = false);
        rt.set_system_reduced_motion(false);
        rt.set_system_reduced_transparency(true);
        rt.frame(15.0 / 60.0);
        assert!(!rt.glass_on());
        assert!(
            rt.liquid_glass_frame()
                .shapes
                .iter()
                .all(|sh| ((sh.light[1] as u32) & 16) != 0)
        );

        rt.set_system_reduced_transparency(false);
        rt.set_system_increased_contrast(true);
        rt.frame(16.0 / 60.0);
        assert!(!rt.glass_on());
        assert!(
            rt.liquid_glass_frame()
                .shapes
                .iter()
                .all(|sh| ((sh.light[1] as u32) & 32) != 0)
        );

        rt.set_system_increased_contrast(false);
        rt.set_glass_tier_override(Some(rune::GlassTier::Solid));
        rt.frame(17.0 / 60.0);
        assert_eq!(rt.glass_tier(), rune::GlassTier::Solid);
        assert!(rt.liquid_glass_frame().shapes.is_empty());

        rt.set_glass_tier_override(None);
        rt.update(|s, _| {
            s.capture_menu_open = true;
            s.palette_open = true;
            s.push_notice("Glass active", state::NoticeTone::Good);
        });
        rt.frame(18.0 / 60.0);
        let max_draws = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_LIQUID_GLASS)
            .count();
        assert!(
            max_draws <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "stacked bar+menu+modal+notice draws ({max_draws}) exceeded cap {}",
            exo_kit::EXO_MAX_BACKDROP_BLURS
        );
        assert!(rt.liquid_glass_frame().shapes.len() >= 6);
    }
}
