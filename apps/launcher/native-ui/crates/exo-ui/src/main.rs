#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod captures;
mod discord;
mod discord_chat;
mod discord_chat_state;
mod discord_md;
mod discord_state;
mod discord_voice;
mod discord_voice_state;
mod game_page;
mod gamepad;
mod key_modal;
mod library;
mod media_state;
mod music;
mod notice;
mod palette;
mod pc;
mod pc_actions;
mod pc_state;
mod settings;
mod shell;
mod state;
mod store;
mod tour;
mod video_decoder;

use crate::state::AppState;
use exo_kit::{FONT_FAMILIES, FONTS, TITLEBAR_HEIGHT};
use rune::tray::{Tray, TrayEvent, TrayIcon, TrayItem, TraySpec};
use rune::{App, AppSpec, Key, KeyEvent, Titlebar, Tx, Waker};
use std::time::Duration;

struct CliArgs {
    pipe: Option<String>,
    parity_probe: Option<String>,
    probe_points: Option<String>,
    passthrough: Vec<String>,
}

fn split_cli_args() -> CliArgs {
    let mut pipe = None;
    let mut parity_probe = None;
    let mut probe_points = None;
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
        } else {
            passthrough.push(arg);
        }
    }
    CliArgs {
        pipe,
        parity_probe,
        probe_points,
        passthrough,
    }
}

fn on_key(s: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    if ev.is_command('k') {
        tx.instant();
        if s.palette_open {
            s.close_palette();
        } else {
            s.open_palette(tx);
        }
        return true;
    }
    if ev.is_command(',') {
        tx.instant();
        s.toggle_settings();
        return true;
    }
    for (idx, ch) in ['1', '2', '3', '4', '5', '6', '7'].into_iter().enumerate() {
        if ev.is_command(ch) {
            tx.instant();
            s.switch_room(state::Room::NAV_TABS[idx]);
            return true;
        }
    }
    if s.palette_open {
        return palette::handle_palette_key(s, ev, tx);
    }
    if let Some(step) = s.tour_step {
        match ev.key {
            Key::Escape => {
                s.finish_tour();
                return true;
            }
            Key::Right | Key::Enter => {
                if step + 1 >= tour::STEPS.len() {
                    s.finish_tour();
                } else {
                    s.jump_tour(step + 1);
                }
                return true;
            }
            Key::Left => {
                s.jump_tour(step.saturating_sub(1));
                return true;
            }
            _ => {}
        }
    }
    if ev.key == Key::Escape {
        return s.dismiss_top_layer();
    }
    false
}

fn setup_state(s: &mut AppState, waker: Waker) {
    s.install_waker(waker);
    s.preload_visible_covers_sync(2.0);
    s.preload_discord_images_sync(1.0);
}

fn poll_state(s: &mut AppState, now: f64, tx: &mut Tx) -> bool {
    s.sync_and_tick_notices(now, tx)
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
                }
                state.store_home = Some(home);
            }
            if sc == "store-game" {
                let app_id = state
                    .store_home
                    .as_ref()
                    .and_then(|h| h.hero.first())
                    .map(|t| t.app_id.clone())
                    .unwrap_or_else(|| "1091500".into());
                if let Ok(g) = client.call_blocking::<_, exo_rpc::StoreGame>(
                    exo_rpc::METHOD_STORE_GAME,
                    &serde_json::json!({ "appId": app_id }),
                    Duration::from_secs(10),
                ) && g.ok
                {
                    if let Some(ref cache_dir) = state.remote_cache_dir {
                        if let Some(ref a) = g.art
                            && let Some(u) = a.best_hero()
                        {
                            let _ = exo_rpc::remote_art::fetch_cached_image(u, cache_dir);
                        }
                        if let Some(ref hdr) = g.header {
                            let _ = exo_rpc::remote_art::fetch_cached_image(hdr, cache_dir);
                        }
                    }
                    state.store_game = Some(g);
                }
                if let Ok(offers) = client.call_blocking::<_, exo_rpc::StoreOtherOffers>(
                    exo_rpc::METHOD_STORE_GAME_OFFERS,
                    &serde_json::json!({ "appId": app_id }),
                    Duration::from_secs(10),
                ) && offers.ok
                {
                    state.store_offers = Some(offers);
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
        }
        Some("pc") => {
            if let Ok(ai) = client.call_blocking::<_, exo_rpc::ExoAiState>(
                exo_rpc::METHOD_EXO_AI_STATE,
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                state.pc.exo_ai = Some(ai);
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
            if let Ok(hdr) = client.call_blocking::<_, exo_rpc::HdrState>(
                exo_rpc::METHOD_HDR_STATE,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.hdr = Some(hdr);
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
                "gpu.profile",
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                let (gpu_name, screen) = pc_actions::parse_gpu_profile_fields(&gp);
                state.pc.gpu_short_name = gpu_name;
                state.pc.screen_label = screen;
            }
            if let Ok(cl) = client.call_blocking::<_, serde_json::Value>(
                "compress.list",
                &serde_json::json!({}),
                Duration::from_secs(6),
            ) {
                let (potential, saved) = pc_actions::parse_compress_totals(&cl);
                state.pc.compress_potential_bytes = potential;
                state.pc.compress_saved_bytes = saved;
            }
            if let Ok(drv) = client.call_blocking::<_, exo_rpc::DrivesReply>(
                exo_rpc::METHOD_SYSTEM_DRIVES,
                &serde_json::json!({ "space": false }),
                Duration::from_secs(8),
            ) {
                state.pc.drives = Some(drv);
            }
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
            if sc == "pc-apps-startup"
                && let Ok(st) = client.call_blocking::<_, exo_rpc::StartupListReply>(
                    exo_rpc::METHOD_STARTUP_LIST,
                    &serde_json::json!({}),
                    Duration::from_secs(10),
                )
            {
                state.pc.startup_list = Some(st);
            }
        }
        Some("pc-storage") => {
            if let Ok(drv) = client.call_blocking::<_, exo_rpc::DrivesReply>(
                exo_rpc::METHOD_SYSTEM_DRIVES,
                &serde_json::json!({ "space": false }),
                Duration::from_secs(10),
            ) {
                state.pc.drives = Some(drv);
            }
        }
        Some(sc) if sc == "pc-devices" || sc.starts_with("pc-devices-") => {
            if let Ok(eq) = client.call_blocking::<_, exo_rpc::EqStateReply>(
                exo_rpc::METHOD_EQ,
                &serde_json::json!({ "action": "state" }),
                Duration::from_secs(6),
            ) && let Some(hp) = eq.headphone
            {
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
                state.pc.eq_headphone = Some(hp);
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
                    .find(|d| d.kind == "audio" && d.role.as_deref() != Some("mic"))
                    .map(|d| d.id.clone());
                let mic_id = list
                    .devices
                    .iter()
                    .find(|d| d.kind == "audio" && d.role.as_deref() == Some("mic"))
                    .map(|d| d.id.clone());
                if let Ok(aud) = client.call_blocking::<_, exo_rpc::AudioReadReply>(
                    exo_rpc::METHOD_AUDIO_READ,
                    &serde_json::json!({ "output": out_id, "mic": mic_id }),
                    Duration::from_secs(5),
                ) {
                    state.pc.audio = Some(aud);
                }
                state.pc.audio_output_id = out_id;
                state.pc.audio_mic_id = mic_id;
                state.pc.devices = Some(list);
            }
            if let Ok(kb) = client.call_blocking::<_, exo_rpc::KeyboardReadReply>(
                exo_rpc::METHOD_KEYBOARD_READ,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) && kb.ok
            {
                state.pc.keyboard = Some(kb);
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
                state.media.playback = pb.playback;
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
                        if !data.filters.is_empty() {
                            state.media.music_library_filters = data.filters;
                        }
                        if let Some(f) = data.filter {
                            state.media.music_library_filter = f;
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
                    if let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                        view_m,
                        &serde_json::json!({ "view": "player" }),
                        Duration::from_secs(6),
                    ) && r.ok
                        && let Some(data) = r.data
                    {
                        state.media.apply_player_state(&data);
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
                        state.media.music_page_data = Some(data);
                    }
                    if sc == "music-queue"
                        && let Ok(r) = client.call_blocking::<_, exo_rpc::PlayerViewReply>(
                            view_m,
                            &serde_json::json!({ "view": "queue" }),
                            Duration::from_secs(8),
                        )
                        && r.ok
                        && let Some(data) = r.data
                    {
                        state.media.music_queue_data = Some(data);
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
        }
        Some(sc) if sc.starts_with("discord") => {
            if let Ok(settings) = client.call_blocking::<_, exo_rpc::LauncherSettings>(
                exo_rpc::METHOD_SETTINGS_GET,
                &serde_json::json!({}),
                Duration::from_secs(4),
            ) {
                state.settings = settings;
            }
            for _ in 0..40 {
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
                    state.discord.active_dm_id = home.dms.first().map(|d| d.id.clone());
                    state.discord.home = home;
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
                if sc == "discord-messages"
                    && let Some(dm_id) = state.discord.active_dm_id.clone()
                    && let Ok(rep) = client.call_blocking::<_, exo_rpc::DiscordMessagesReply>(
                        exo_rpc::METHOD_DISCORD_MESSAGES,
                        &serde_json::json!({ "channelId": dm_id, "limit": 40 }),
                        Duration::from_secs(6),
                    )
                    && let Some(msgs) = rep.messages
                {
                    state.discord.chat.messages_by_channel.insert(dm_id, msgs);
                }
                if sc == "discord-server"
                    && let Some(gid) = state.discord.home.guilds.first().map(|g| g.id.clone())
                {
                    state.discord.active_guild_id = Some(gid.clone());
                    if let Ok(chans) = client.call_blocking::<_, exo_rpc::DiscordChannels>(
                        exo_rpc::METHOD_DISCORD_CHANNELS,
                        &serde_json::json!({ "guildId": gid }),
                        Duration::from_secs(6),
                    ) {
                        let first_cid = chans
                            .channels
                            .iter()
                            .find(|c| c.kind == "text" && c.can_read != Some(false))
                            .or_else(|| chans.channels.iter().find(|c| c.kind != "category"))
                            .map(|c| c.id.clone());
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
        s.pc.selected_device_id = Some(id);
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
        s.switch_room(state::Room::Settings);
        s.palette_open = true;
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
    ("tour", |s: &mut AppState| s.start_tour()),
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
    ("pc", |s: &mut AppState| s.switch_room(state::Room::ExoAi)),
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
    ("captures", |s: &mut AppState| {
        s.switch_room(state::Room::Settings);
        s.capture_menu_open = true;
    }),
    ("captures-sheet", |s: &mut AppState| {
        s.open_captures(None);
    }),
    ("captures-settings", |s: &mut AppState| {
        s.open_captures(None);
        s.media.captures_settings_open = true;
    }),
    ("capture-clip", |s: &mut AppState| {
        s.open_captures(None);
        if let Some(first) = s.media.capture_items.first().map(|c| c.id.clone()) {
            s.open_capture_item(&first);
        }
    }),
    ("discord", |s: &mut AppState| {
        s.switch_room(state::Room::Discord)
    }),
    ("discord-status-menu", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.discord.status_menu_open = true;
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
    ("discord-voice", |s: &mut AppState| {
        s.switch_room(state::Room::Discord);
        s.open_discord_guild("g-exo");
        s.open_discord_channel("vc-lounge");
        s.join_discord_voice(Some("g-exo"), "vc-lounge");
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
    rt.instances
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
            })
        })
        .collect()
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

    let text = rune::text::TextSystem::for_window(FONTS, FONT_FAMILIES);
    let mut rt = rune::Runtime::with_text(
        state,
        shell::view,
        viewport,
        rune::Env::new(true, scale),
        text,
    );
    rt.frame(0.0);
    let glass_frame0 = collect_backdrop_blurs(&rt);

    if let Some(ref sp) = script_path {
        replay_probe_script(&mut rt, sp);
    }

    for _ in 0..10 {
        rt.frame(1.0 / 60.0);
    }
    let glass_frame10 = collect_backdrop_blurs(&rt);

    for _ in 0..24 {
        rt.frame(1.0 / 60.0);
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

    let menu_prefixes = [
        "capture-popover-menu",
        "dc-status-menu",
        "library-more",
        "dc-menu",
    ];
    let mut menu_items: Vec<String> = Vec::new();
    for el in &elements {
        let in_menu = menu_prefixes
            .iter()
            .any(|p| el.path.contains(p) || el.key.as_deref().is_some_and(|k| k.contains(p)));
        if in_menu && let Some(t) = el.text.as_deref().or(el.label.as_deref()) {
            let trimmed = t.trim();
            if !trimmed.is_empty() && !menu_items.iter().any(|x| x == trimmed) {
                menu_items.push(trimmed.to_string());
            }
        }
    }

    let popup_menu_open = rt.popup_menu().is_some();

    let state_summary = serde_json::json!({
        "room": format!("{:?}", rt.state.room),
        "libraryAllGames": rt.state.library_all_games,
        "librarySearchOpen": rt.state.library_search_open,
        "libraryMoreOpen": rt.state.library_more_open,
        "openGameId": rt.state.open_game_id,
        "propsOpen": rt.state.props_open,
        "gamePropsTab": rt.state.game_props_tab,
        "storeTab": rt.state.store_tab,
        "storeGameOpen": rt.state.store_game.is_some(),
        "paletteOpen": rt.state.palette_open,
        "captureMenuOpen": rt.state.capture_menu_open,
        "capturesOpen": rt.state.media.captures_open,
        "capturesSettingsOpen": rt.state.media.captures_settings_open,
        "keyModalOpen": rt.state.key_modal_open,
        "tourStep": rt.state.tour_step,
        "settingsSection": format!("{:?}", rt.state.pc.settings_section),
        "pcTab": format!("{:?}", rt.state.pc.pc_tab),
        "appsSubTab": format!("{:?}", rt.state.pc.apps_sub_tab),
        "selectedDeviceId": rt.state.pc.selected_device_id,
        "musicNav": format!("{:?}", rt.state.media.music_nav),
        "musicPanel": rt.state.media.music_panel.map(|p| format!("{p:?}")),
        "musicImmersion": rt.state.media.music_immersion,
        "musicRoomState": format!("{:?}", rt.state.media.music_room_state),
        "discordPlace": format!("{:?}", rt.state.discord.place),
        "discordStatusMenuOpen": rt.state.discord.status_menu_open,
        "discordVoiceChannel": rt.state.discord.voice.channel_id,
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

fn main() {
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

    let headless_flag = rune_args
        .iter()
        .any(|a| a == "--snap" || a == "--tree" || a == "--script" || a == "--headless");

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
        .titlebar(Titlebar::new(TITLEBAR_HEIGHT))
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
                    event_handle.update(move |s, _| match ev {
                        exo_rpc::HostEvent::LibraryUpdated(lib) => s.apply_library(lib),
                        exo_rpc::HostEvent::InstallProgress(p) => {
                            s.install_progress = p.is_active.then_some(p);
                        }
                        exo_rpc::HostEvent::UpdateProgress(p) => {
                            s.pc.update_progress = Some(p);
                        }
                        exo_rpc::HostEvent::MusicPlayback(pb) => {
                            s.media.playback = pb;
                        }
                        exo_rpc::HostEvent::DiscordState(ev) => s.handle_discord_state(ev),
                        exo_rpc::HostEvent::DiscordSignIn(step) => s.handle_discord_signin(step),
                        exo_rpc::HostEvent::DiscordChanged(ev) => s.handle_discord_changed(ev),
                        exo_rpc::HostEvent::DiscordMembers { guild_id } => {
                            s.handle_discord_members(&guild_id);
                        }
                        exo_rpc::HostEvent::DiscordPresence(ev) => s.handle_discord_presence(ev),
                        exo_rpc::HostEvent::DiscordUnread(ev) => s.handle_discord_unread(ev),
                        exo_rpc::HostEvent::DiscordVoice(v) => s.handle_discord_voice(v),
                        exo_rpc::HostEvent::DiscordSpeaking { user_id, speaking } => {
                            s.handle_discord_speaking(&user_id, speaking);
                        }
                        exo_rpc::HostEvent::DiscordMessage(ev) => s.handle_discord_message(*ev),
                        exo_rpc::HostEvent::DiscordReaction(ev) => s.handle_discord_reaction(ev),
                        exo_rpc::HostEvent::DiscordTyping(ev) => s.handle_discord_typing(ev),
                        exo_rpc::HostEvent::DiscordVideo(ev) => s.handle_discord_video_event(ev),
                        exo_rpc::HostEvent::DiscordVideoFrame(f) => s.handle_discord_video_frame(f),
                        exo_rpc::HostEvent::DiscordGoLive(ev) => s.handle_discord_golive_state(ev),
                        exo_rpc::HostEvent::DiscordGoLiveStats(ev) => {
                            s.handle_discord_golive_stats(ev);
                        }
                        exo_rpc::HostEvent::DiscordGoLiveViewers(ids) => {
                            s.handle_discord_golive_viewers(ids);
                        }
                        exo_rpc::HostEvent::DevicesChanged => {
                            if s.room == state::Room::Devices {
                                s.load_pc_devices();
                            }
                        }
                        exo_rpc::HostEvent::AppsChanged => {
                            if s.room == state::Room::Apps {
                                s.load_pc_apps();
                            }
                        }
                        exo_rpc::HostEvent::MusicVolume(ev) => s.apply_music_volume(ev),
                        exo_rpc::HostEvent::CaptureState(live) => s.apply_capture_state(live),
                        exo_rpc::HostEvent::CaptureSaved(ev) => s.apply_capture_saved(*ev),
                        exo_rpc::HostEvent::CaptureReel(ev) => s.apply_capture_reel(*ev),
                        exo_rpc::HostEvent::InstallQueue(ev) => s.apply_install_queue(ev),
                        exo_rpc::HostEvent::GameRunState(ev) => s.apply_game_run_state(ev),
                        exo_rpc::HostEvent::LaunchStatus(ev) => s.apply_launch_status(ev),
                        exo_rpc::HostEvent::AppUpdateState(ev) => s.apply_app_update_state(ev),
                        exo_rpc::HostEvent::ExoAccountChanged => s.apply_exo_account_changed(),
                        exo_rpc::HostEvent::AutopilotReport(ev) => s.apply_autopilot_report(ev),
                        exo_rpc::HostEvent::AutopilotLive(live) => s.apply_autopilot_live(live),
                        exo_rpc::HostEvent::ShadersChanged => s.apply_shaders_changed(),
                        exo_rpc::HostEvent::StoreChanged(ev) => s.apply_store_changed(ev),
                        exo_rpc::HostEvent::Disconnected(why) => {
                            s.discord.voice_ui.decoders.clear();
                            s.video_router.clear();
                            s.status = state::HostStatus::Error(why);
                            s.notice = Some("Disconnected from Exo host".into());
                        }
                        exo_rpc::HostEvent::Raw { .. } => {}
                    });
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

    app.run();
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::gamepad::GamepadAction;
    use crate::state::{LibraryFilter, Room};
    use rune::{
        Env, Mods, Runtime,
        render::{KIND_BACKDROP_BLUR, KIND_SEGMENT},
    };

    #[test]
    fn phase2_game_page_store_tour_key_modal_and_gamepad() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.frame(0.0);

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            (2..=4).contains(&blurs),
            "expected 2..=4 floating glass surfaces within MAX_BACKDROP_BLURS=4, got {blurs}"
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
        assert_eq!(rt.state.tour_step, Some(0));
        assert_eq!(rt.state.room, Room::Library);
        let (nx, ny) = rt.find("tour-next").expect("Tour Next");
        rt.pointer_down(nx, ny);
        rt.pointer_up(nx, ny);
        rt.frame(5.0 / 60.0);
        assert_eq!(rt.state.tour_step, Some(1));
        rt.state.finish_tour();
        assert!(rt.state.tour_step.is_none());
        assert!(rt.state.tour_seen);

        rt.state.filter = LibraryFilter::Installed;
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

        rt.update(|s, _| s.switch_room(Room::ExoAi));
        rt.frame(2.0 / 60.0);
        assert_eq!(rt.state.pc.pc_tab, pc_state::PcTab::ExoAi);
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(blurs <= 4, "PC room exceeded MAX_BACKDROP_BLURS=4: {blurs}");

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
    fn phase4_music_mini_player_and_captures_theater() {
        let mut rt = Runtime::new(
            AppState::default(),
            shell::view,
            (1280.0, 800.0),
            Env::new(true, 1.0),
        );
        rt.frame(0.0);
        assert!(rt.find("music-mini-player").is_some());
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= 4,
            "mini-player exceeded MAX_BACKDROP_BLURS=4: {blurs}"
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
        assert!(rt.find("dc-voice-dock").is_some());

        let (tx, ty) = rt.find("dc-me-trigger").expect("MePanel status trigger");
        rt.pointer_down(tx, ty);
        rt.pointer_up(tx, ty);
        rt.frame(5.0 / 60.0);
        let (dx, dy) = rt.find("dc-set-status-dnd").expect("DND status option");
        rt.pointer_down(dx, dy);
        rt.pointer_up(dx, dy);
        rt.frame(6.0 / 60.0);
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
        rt.frame(0.0);
        assert!(rt.find("dc-voice-stage").is_some());
        assert!(rt.find("dc-stream-theater").is_some());
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= 4,
            "Discord voice exceeded MAX_BACKDROP_BLURS=4: {blurs}"
        );

        let (gx, gy) = rt.find("dc-vc-share").expect("Share screen button");
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

        let (sx, sy) = rt.find("dc-vc-sb").expect("Soundboard button");
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        rt.frame(3.0 / 60.0);
        assert_eq!(
            rt.state.discord.voice_ui.panel,
            discord_voice_state::VoicePanel::Soundboard
        );

        let (ax, ay) = rt.find("dc-vc-audio").expect("Audio mixer button");
        rt.pointer_down(ax, ay);
        rt.pointer_up(ax, ay);
        rt.frame(4.0 / 60.0);
        assert_eq!(
            rt.state.discord.voice_ui.panel,
            discord_voice_state::VoicePanel::Audio
        );
    }

    #[test]
    fn blur_budget_and_glass_palette_invariants() {
        let check_case = |label: &str,
                          setup: &dyn Fn(&mut AppState),
                          expected_overlay: Option<&str>| {
            let mut state = AppState::default();
            setup(&mut state);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.frame(0.0);
            rt.frame(1.0 / 60.0);
            let blur_rects: Vec<[f32; 4]> = rt
                .instances
                .iter()
                .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
                .map(|i| i.rect)
                .collect();
            assert!(
                blur_rects.len() <= rune::MAX_BACKDROP_BLURS,
                "{label}: emitted {} backdrop blurs (cap {})",
                blur_rects.len(),
                rune::MAX_BACKDROP_BLURS
            );
            if let Some(key) = expected_overlay {
                let (rect, _) = rt
                    .presented(key)
                    .unwrap_or_else(|| panic!("{label}: overlay {key} not presented"));
                let hit = blur_rects.iter().take(rune::MAX_BACKDROP_BLURS).any(|r| {
                    (r[0] - rect[0]).abs() < 2.0
                        && (r[1] - rect[1]).abs() < 2.0
                        && (r[2] - rect[2]).abs() < 2.0
                        && (r[3] - rect[3]).abs() < 2.0
                });
                assert!(
                    hit,
                    "{label}: overlay {key} rect {rect:?} missing from first {} blur slots: {blur_rects:?}",
                    rune::MAX_BACKDROP_BLURS
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
            pal_rect[3] <= 520.5,
            "palette height {} exceeded 520px cap",
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
        let settings_blurs = rt_settings
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert_eq!(
            settings_blurs, 0,
            "void-top Settings room should emit 0 backdrop blurs, got {settings_blurs}"
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
            rt.poll = Some(poll_state);
            rt.frame(0.0);
            let blur_count = rt
                .instances
                .iter()
                .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
                .count();
            assert!(
                blur_count <= rune::MAX_BACKDROP_BLURS,
                "{label}: emitted {blur_count} backdrop blurs (cap {})",
                rune::MAX_BACKDROP_BLURS
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
}
