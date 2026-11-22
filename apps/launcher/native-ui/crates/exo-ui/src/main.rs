mod library;
mod palette;
mod shell;
mod state;

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
    if ev.key == Key::Escape && s.notice.is_some() {
        s.notice = None;
        return true;
    }
    false
}

fn setup_state(s: &mut AppState, waker: Waker) {
    s.install_waker(waker);
    s.preload_visible_covers_sync(2.0);
}

#[cfg(windows)]
fn load_live_state_sync(pipe: &str) -> AppState {
    let mut state = AppState::for_host_pipe();
    let Ok(client) = exo_rpc::RpcClient::connect(pipe, Duration::from_secs(6), |_| {}) else {
        state.status = state::HostStatus::Error("Connect failed".into());
        state.refreshing = false;
        return state;
    };
    if let Ok(base) =
        client.call_blocking::<_, exo_rpc::ArtBase>(exo_rpc::METHOD_ART_BASE, &serde_json::json!({}), Duration::from_secs(5))
    {
        state.apply_art_base(base);
    }
    if let Ok(lib) = client.call_blocking::<_, exo_rpc::LibraryPayload>(
        exo_rpc::METHOD_LIBRARY_GET,
        &serde_json::json!({}),
        Duration::from_secs(12),
    ) {
        state.apply_library(lib);
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
    spec.scenes = &[("palette", |s: &mut AppState| s.palette_open = true)];

    let mut app = App::new(spec)
        .args(rune_args)
        .titlebar(Titlebar::new(TITLEBAR_HEIGHT))
        .release_gpu_when_closed();

    if let Some(ref pipe) = pipe_name {
        if headless_flag {
            #[cfg(windows)]
            {
                let pipe_owned = pipe.clone();
                app = app.init(move || load_live_state_sync(&pipe_owned));
            }
        } else {
            app = app.init(AppState::for_host_pipe);
        }
    }

    let handle = app.handle();

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
    use crate::state::{LibraryFilter, Room};
    use rune::{
        Env, Mods, Runtime,
        render::{KIND_BACKDROP_BLUR, KIND_SEGMENT},
    };

    #[test]
    fn phase1_shell_switches_rooms_filters_and_palette() {
        let mut rt = Runtime::new(AppState::default(), shell::view, (1280.0, 800.0), Env::new(true, 1.0));
        rt.frame(0.0);

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(blurs >= 3, "expected glass surfaces, got {blurs}");

        let strokes = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_SEGMENT)
            .count();
        assert!(strokes >= 10, "expected vector icons, got {strokes}");

        let (ix, iy) = rt.find(LibraryFilter::Installed.key()).expect("Installed filter");
        rt.pointer_down(ix, iy);
        rt.pointer_up(ix, iy);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.filter, LibraryFilter::Installed);

        let (sx, sy) = rt.find(Room::Store.key()).expect("Store nav");
        assert!(sy < 60.0, "nav should sit in titlebar band, y={sy}");
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.room, Room::Store);

        let (kx, ky) = rt.find("search-capsule").expect("Search capsule");
        rt.pointer_down(kx, ky);
        rt.pointer_up(kx, ky);
        rt.frame(1.0 / 60.0);
        assert!(rt.state.palette_open);
        let rect = rt.presented("command-palette").expect("palette rect").0;
        let cx = rect[0] + rect[2] * 0.5;
        assert!((cx - 640.0).abs() < 2.0, "palette centered, cx={cx}");

        rt.state.palette_query.text = "elden".into();
        let results = rt.state.palette_results();
        assert_eq!(results.first().map(|r| r.title.as_str()), Some("Elden Ring"));

        let mut tx = Tx::default();
        let handled = on_key(
            &mut rt.state,
            &KeyEvent {
                key: Key::Escape,
                mods: Mods::default(),
            },
            &mut tx,
        );
        assert!(handled);
        assert!(!rt.state.palette_open);
    }
}
