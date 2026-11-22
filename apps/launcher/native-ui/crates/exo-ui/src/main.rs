mod game_page;
mod gamepad;
mod key_modal;
mod library;
mod palette;
mod pc;
mod pc_actions;
mod pc_state;
mod settings;
mod shell;
mod state;
mod store;
mod tour;

use crate::state::AppState;
use exo_kit::{FONT_FAMILIES, FONTS, TITLEBAR_HEIGHT};
use rune::tray::{Tray, TrayEvent, TrayIcon, TrayItem, TraySpec};
use rune::{App, AppSpec, Key, KeyEvent, Titlebar, Tx, Waker};
use std::time::Duration;

fn split_cli_args() -> (Option<String>, Vec<String>) {
    let mut pipe = None;
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
        } else {
            passthrough.push(arg);
        }
    }
    (pipe, passthrough)
}

fn on_key(s: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    if ev.is_command('k') {
        if s.palette_open {
            s.close_palette();
        } else {
            s.open_palette(tx);
        }
        return true;
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
}

#[cfg(windows)]
fn load_live_state_sync(pipe: &str, scene: Option<&str>) -> AppState {
    let mut state = AppState::for_host_pipe();
    let Ok(client) = exo_rpc::RpcClient::connect(pipe, Duration::from_secs(6), |_| {}) else {
        state.status = state::HostStatus::Error("Connect failed".into());
        state.refreshing = false;
        return state;
    };
    if let Ok(base) = client.call_blocking::<_, exo_rpc::ArtBase>(
        exo_rpc::METHOD_ART_BASE,
        &serde_json::json!({}),
        Duration::from_secs(5),
    ) {
        state.apply_art_base(base);
    }
    if let Ok(lib) = client.call_blocking::<_, exo_rpc::LibraryPayload>(
        exo_rpc::METHOD_LIBRARY_GET,
        &serde_json::json!({}),
        Duration::from_secs(12),
    ) {
        state.apply_library(lib);
    }
    match scene {
        Some("store") => {
            if let Ok(home) = client.call_blocking::<_, exo_rpc::StoreHome>(
                exo_rpc::METHOD_STORE_HOME,
                &serde_json::json!({}),
                Duration::from_secs(10),
            ) && home.ok
            {
                let mut urls = Vec::new();
                if let Some(hero) = home
                    .hero
                    .first()
                    .and_then(|t| t.art.as_ref())
                    .and_then(|a| a.best_hero())
                {
                    urls.push(hero.to_string());
                }
                for shelf in home.shelves.iter().take(2) {
                    for tile in shelf.items.iter().take(6) {
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
        }
        Some("settings") => {
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
            if let Ok(sh) = client.call_blocking::<_, exo_rpc::ShadersAddonStatus>(
                exo_rpc::METHOD_SHADERS_ADDON,
                &serde_json::json!({}),
                Duration::from_secs(5),
            ) {
                state.pc.shaders = Some(sh);
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
        Some("pc-devices") => {
            if let Ok(list) = client.call_blocking::<_, exo_rpc::DevicesListReply>(
                exo_rpc::METHOD_DEVICES_LIST,
                &serde_json::json!({}),
                Duration::from_secs(8),
            ) {
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
            if r2 <= 52.0 && r2 >= 16.0 {
                rgba[idx] = 255;
                rgba[idx + 1] = 255;
                rgba[idx + 2] = 255;
                rgba[idx + 3] = 255;
            }
        }
    }
    TrayIcon::rgba(SIZE, SIZE, rgba)
}

fn main() {
    let (pipe_name, rune_args) = split_cli_args();
    let headless_flag = rune_args
        .iter()
        .any(|a| a == "--snap" || a == "--tree" || a == "--script" || a == "--headless");

    let mut spec = AppSpec::new("Exo Launcher", (1280.0, 800.0), AppState::default, shell::view);
    spec.fonts = FONTS;
    spec.families = FONT_FAMILIES;
    spec.setup = Some(setup_state);
    spec.on_key = Some(on_key);
    spec.scenes = &[
        ("palette", |s: &mut AppState| s.palette_open = true),
        ("game-page", |s: &mut AppState| {
            let target = if s.games.iter().any(|g| g.id == "demo:cyberpunk") {
                "demo:cyberpunk".to_string()
            } else {
                s.selected_game_id
                    .clone()
                    .or_else(|| s.games.first().map(|g| g.id.clone()))
                    .unwrap_or_else(|| "demo:cyberpunk".into())
            };
            s.open_game_page(&target);
        }),
        ("store", |s: &mut AppState| s.switch_room(state::Room::Store)),
        ("store-game", |s: &mut AppState| {
            s.switch_room(state::Room::Store);
            s.open_store_game("1091500");
        }),
        ("tour", |s: &mut AppState| s.start_tour()),
        ("activate-key", |s: &mut AppState| s.key_modal_open = true),
        ("settings", |s: &mut AppState| s.switch_room(state::Room::Settings)),
        ("pc", |s: &mut AppState| s.switch_room(state::Room::Pc)),
        ("pc-apps", |s: &mut AppState| {
            s.switch_room(state::Room::Pc);
            s.switch_pc_tab(pc_state::PcTab::Apps);
        }),
        ("pc-storage", |s: &mut AppState| {
            s.switch_room(state::Room::Pc);
            s.switch_pc_tab(pc_state::PcTab::Storage);
        }),
        ("pc-devices", |s: &mut AppState| {
            s.switch_room(state::Room::Pc);
            s.switch_pc_tab(pc_state::PcTab::Devices);
        }),
    ];

    let target_scene = rune_args
        .windows(2)
        .find(|w| w[0] == "--scene")
        .map(|w| w[1].clone());

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
                        exo_rpc::HostEvent::DevicesChanged => {
                            if s.room == state::Room::Pc && s.pc.pc_tab == pc_state::PcTab::Devices {
                                s.load_pc_devices();
                            }
                        }
                        exo_rpc::HostEvent::AppsChanged => {
                            if s.room == state::Room::Pc && s.pc.pc_tab == pc_state::PcTab::Apps {
                                s.load_pc_apps();
                            }
                        }
                        exo_rpc::HostEvent::Disconnected(why) => {
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
        assert_eq!(blurs, 2, "expected 2 floating glass top-bar surfaces, got {blurs}");
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

        let (px, py) = rt.find("gp-props").expect("Properties button");
        rt.pointer_down(px, py);
        rt.pointer_up(px, py);
        rt.frame(1.0 / 60.0);
        assert!(rt.state.props_open);
        let modal_blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert_eq!(
            modal_blurs, 3,
            "expected modal blur within MAX_BACKDROP_BLURS=4, got {modal_blurs}"
        );

        let mut tx_ctx = Tx::default();
        assert!(on_key(
            &mut rt.state,
            &KeyEvent {
                key: Key::Escape,
                mods: Mods::default()
            },
            &mut tx_ctx
        ));
        assert!(!rt.state.props_open);
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

        rt.update(|s, _| s.switch_room(Room::Pc));
        rt.frame(2.0 / 60.0);
        assert_eq!(rt.state.pc.pc_tab, pc_state::PcTab::ExoAi);
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(blurs <= 4, "PC room exceeded MAX_BACKDROP_BLURS=4: {blurs}");

        let (ax, ay) = rt.find("pc-tab-apps").expect("PC Apps tab");
        rt.pointer_down(ax, ay);
        rt.pointer_up(ax, ay);
        rt.frame(3.0 / 60.0);
        assert_eq!(rt.state.pc.pc_tab, pc_state::PcTab::Apps);

        let (sx, sy) = rt.find("pc-tab-storage").expect("PC Storage tab");
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        rt.frame(4.0 / 60.0);
        assert_eq!(rt.state.pc.pc_tab, pc_state::PcTab::Storage);

        let (dx, dy) = rt.find("pc-tab-devices").expect("PC Devices tab");
        rt.pointer_down(dx, dy);
        rt.pointer_up(dx, dy);
        rt.frame(5.0 / 60.0);
        assert_eq!(rt.state.pc.pc_tab, pc_state::PcTab::Devices);
        let act_before = rt.state.pc.keyboard.as_ref().and_then(|k| k.actuation_mm).unwrap_or(0.4);
        let (ux, uy) = rt.find("kb-act-up").expect("Wooting actuation +");
        rt.pointer_down(ux, uy);
        rt.pointer_up(ux, uy);
        rt.frame(6.0 / 60.0);
        let act_after = rt.state.pc.keyboard.as_ref().and_then(|k| k.actuation_mm).unwrap_or(0.0);
        assert!((act_after - (act_before + 0.1)).abs() < 0.01);
    }
}
