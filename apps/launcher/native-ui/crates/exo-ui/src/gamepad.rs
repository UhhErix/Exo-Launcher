use crate::pc_state::SettingsSection;
use crate::state::{AppState, Room};
use exo_kit::{
    ExoGlass, FG, GAP, RADIUS_PILL, RADIUS_SMALL, apply_glass, fg_muted, icon_gamepad,
    selected_fill, type_small,
};
use rune::{El, el, row, text};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum GamepadKind {
    #[default]
    Xbox,
    PlayStation,
    Nintendo,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct GamepadGlyphs {
    pub confirm: &'static str,
    pub back: &'static str,
    pub action: &'static str,
    pub search: &'static str,
    pub view: &'static str,
    pub prev_room: &'static str,
    pub next_room: &'static str,
    pub prev_tab: &'static str,
    pub next_tab: &'static str,
    pub menu: &'static str,
}

pub fn detect_gamepad_kind(id: &str) -> GamepadKind {
    let lower = id.to_lowercase();
    if lower.contains("xbox") || lower.contains("xinput") || lower.contains("045e") {
        return GamepadKind::Xbox;
    }
    if lower.contains("dualsense")
        || lower.contains("dualshock")
        || lower.contains("054c")
        || lower.contains("sony")
        || lower.contains("wireless controller")
        || lower.contains("ps4")
        || lower.contains("ps5")
        || lower.contains("playstation")
    {
        return GamepadKind::PlayStation;
    }
    if lower.contains("nintendo")
        || lower.contains("pro controller")
        || lower.contains("joy-con")
        || lower.contains("057e")
        || lower.contains("switch")
    {
        return GamepadKind::Nintendo;
    }
    GamepadKind::Xbox
}

pub fn gamepad_labels(kind: GamepadKind) -> GamepadGlyphs {
    match kind {
        GamepadKind::PlayStation => GamepadGlyphs {
            confirm: "✕",
            back: "○",
            action: "◻",
            search: "△",
            view: "Create",
            prev_room: "L1",
            next_room: "R1",
            prev_tab: "L2",
            next_tab: "R2",
            menu: "Options",
        },
        GamepadKind::Nintendo => GamepadGlyphs {
            confirm: "A",
            back: "B",
            action: "X",
            search: "Y",
            view: "-",
            prev_room: "L",
            next_room: "R",
            prev_tab: "ZL",
            next_tab: "ZR",
            menu: "+",
        },
        GamepadKind::Xbox => GamepadGlyphs {
            confirm: "A",
            back: "B",
            action: "X",
            search: "Y",
            view: "View",
            prev_room: "LB",
            next_room: "RB",
            prev_tab: "LT",
            next_tab: "RT",
            menu: "Menu",
        },
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GamepadAction {
    Connected(bool),
    PrevRoom,
    NextRoom,
    PrevTab,
    NextTab,
    Up,
    Down,
    Left,
    Right,
    Confirm,
    Back,
    QuickPlay,
    Search,
    Settings,
}

pub fn hints_for(kind: GamepadKind, room: Room, overlay_open: bool) -> Vec<(String, &'static str)> {
    let g = gamepad_labels(kind);
    if overlay_open {
        return vec![
            (g.confirm.into(), "Select"),
            (g.action.into(), "Play"),
            (g.back.into(), "Back"),
            (g.view.into(), "Search"),
            (g.menu.into(), "Settings"),
        ];
    }
    if room == Room::Settings {
        return vec![
            (format!("{}/{}", g.prev_tab, g.next_tab), "Sections"),
            (g.confirm.into(), "Select"),
            (g.back.into(), "Close"),
            (g.view.into(), "Search"),
        ];
    }
    vec![
        (format!("{}/{}", g.prev_room, g.next_room), "Rooms"),
        (
            g.confirm.into(),
            if matches!(room, Room::Devices | Room::Library) {
                "Open"
            } else {
                "Select"
            },
        ),
        (g.back.into(), "Back"),
        (g.view.into(), "Search"),
        (g.menu.into(), "Settings"),
    ]
}

pub fn bar_view(s: &AppState) -> El {
    let overlay_open = s.palette_open
        || s.key_modal_open
        || s.downloads.open
        || s.open_game_id.is_some()
        || s.store_game.is_some()
        || s.whats_new.mode.is_some()
        || s.whats_new.sheet_open;

    if s.chrome.gamepad_paused {
        return apply_glass(
            row()
                .key("gamepad-bar")
                .role("complementary")
                .label("Controller shortcuts")
                .pin(None, None, Some(GAP), Some(GAP))
                .h(36.0)
                .px(14.0)
                .gap(10.0)
                .center_y()
                .radius(RADIUS_PILL)
                .child(icon_gamepad(14.0, FG))
                .child(
                    text("Controller Lab · navigation paused · Esc to exit")
                        .font(type_small())
                        .color(fg_muted()),
                ),
            ExoGlass::Bar,
            false,
        );
    }

    let detected =
        s.pc.devices
            .as_ref()
            .and_then(|list| {
                list.devices
                    .iter()
                    .find(|d| d.kind == "controller")
                    .map(|d| detect_gamepad_kind(&d.name))
            })
            .unwrap_or(s.chrome.gamepad_kind);
    let hints = hints_for(detected, s.room, overlay_open);
    apply_glass(
        row()
            .key("gamepad-bar")
            .role("complementary")
            .label("Controller shortcuts")
            .pin(None, None, Some(GAP), Some(GAP))
            .h(36.0)
            .px(12.0)
            .gap(12.0)
            .center_y()
            .radius(RADIUS_PILL)
            .child(icon_gamepad(14.0, FG))
            .children(hints.into_iter().map(|(keys, label)| {
                row()
                    .gap(5.0)
                    .center_y()
                    .child(
                        el().h(20.0)
                            .px(6.0)
                            .radius(RADIUS_SMALL)
                            .fill(selected_fill())
                            .center()
                            .child(text(keys).font(type_small()).color(FG)),
                    )
                    .child(text(label).font(type_small()).color(fg_muted()))
            })),
        ExoGlass::Bar,
        false,
    )
}

pub fn cycle_room_subtab(s: &mut AppState, delta: isize) {
    match s.room {
        Room::Settings => {
            let all = SettingsSection::ALL;
            let cur = all
                .iter()
                .position(|&sec| sec == s.pc.settings_section)
                .unwrap_or(0);
            let next = (cur as isize + delta).rem_euclid(all.len() as isize) as usize;
            s.pc.settings_section = all[next];
        }
        Room::Store => {
            let next = (s.store_tab as isize + delta).rem_euclid(3) as usize;
            s.store_tab = next;
        }
        Room::Library => {
            s.library_all_games = !s.library_all_games;
        }
        _ => {}
    }
}

const DPAD_UP_BIT: u16 = 0x0001;
const DPAD_DOWN_BIT: u16 = 0x0002;
const DPAD_LEFT_BIT: u16 = 0x0004;
const DPAD_RIGHT_BIT: u16 = 0x0008;
const STICK_RADIAL_DEADZONE: f32 = 32767.0 * 0.28;
const DIR_INITIAL_DELAY_MS: u64 = 340;
const DIR_REPEAT_INTERVAL_MS: u64 = 105;

fn directional_input(buttons: u16, thumb_lx: i16, thumb_ly: i16) -> (i8, i8) {
    let dpad_x = if buttons & DPAD_RIGHT_BIT != 0 {
        1
    } else if buttons & DPAD_LEFT_BIT != 0 {
        -1
    } else {
        0
    };
    let dpad_y = if buttons & DPAD_UP_BIT != 0 {
        -1
    } else if buttons & DPAD_DOWN_BIT != 0 {
        1
    } else {
        0
    };
    if dpad_x != 0 || dpad_y != 0 {
        return (dpad_x, dpad_y);
    }
    let lx = thumb_lx as f32;
    let ly = thumb_ly as f32;
    if lx.hypot(ly) < STICK_RADIAL_DEADZONE {
        return (0, 0);
    }
    if lx.abs() >= ly.abs() {
        (if lx > 0.0 { 1 } else { -1 }, 0)
    } else {
        (0, if ly > 0.0 { -1 } else { 1 })
    }
}

fn dir_repeat_due(same_dir: bool, repeating: bool, elapsed_ms: u64) -> bool {
    if !same_dir {
        return true;
    }
    let threshold = if repeating {
        DIR_REPEAT_INTERVAL_MS
    } else {
        DIR_INITIAL_DELAY_MS
    };
    elapsed_ms >= threshold
}

#[cfg(windows)]
fn is_process_foreground() -> bool {
    #[link(name = "user32")]
    unsafe extern "system" {
        fn GetForegroundWindow() -> isize;
        fn GetWindowThreadProcessId(hwnd: isize, lpdw_process_id: *mut u32) -> u32;
    }
    let hwnd = unsafe { GetForegroundWindow() };
    if hwnd == 0 {
        return false;
    }
    let mut pid: u32 = 0;
    unsafe { GetWindowThreadProcessId(hwnd, &mut pid) };
    pid == std::process::id()
}

#[cfg(windows)]
pub fn start_polling(on_action: impl Fn(GamepadAction) + Send + 'static) {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use std::time::{Duration, Instant};
    use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};

    #[repr(C)]
    #[derive(Clone, Copy, Default)]
    struct XInputGamepad {
        buttons: u16,
        left_trigger: u8,
        right_trigger: u8,
        thumb_lx: i16,
        thumb_ly: i16,
        thumb_rx: i16,
        thumb_ry: i16,
    }

    #[repr(C)]
    #[derive(Clone, Copy, Default)]
    struct XInputState {
        packet_number: u32,
        gamepad: XInputGamepad,
    }

    type XInputGetStateFn = unsafe extern "system" fn(u32, *mut XInputState) -> u32;

    const BTN_START: u16 = 0x0010;
    const BTN_BACK: u16 = 0x0020;
    const LB: u16 = 0x0100;
    const RB: u16 = 0x0200;
    const BTN_A: u16 = 0x1000;
    const BTN_B: u16 = 0x2000;
    const BTN_X: u16 = 0x4000;
    const BTN_Y: u16 = 0x8000;
    const TRIGGER_THRESHOLD: u8 = 128;

    std::thread::Builder::new()
        .name("exo-gamepad".into())
        .spawn(move || {
            let dll: Vec<u16> = OsStr::new("xinput1_4.dll\0").encode_wide().collect();
            let module = unsafe { LoadLibraryW(dll.as_ptr()) };
            if module.is_null() {
                return;
            }
            let proc = unsafe { GetProcAddress(module, c"XInputGetState".as_ptr().cast::<u8>()) };
            let Some(raw_fn) = proc else {
                return;
            };
            let get_state: XInputGetStateFn = unsafe { std::mem::transmute(raw_fn) };

            let mut connected = false;
            let mut prev_buttons: u16 = 0;
            let mut prev_lt = false;
            let mut prev_rt = false;
            let mut prev_dir = (0i8, 0i8);
            let mut dir_repeating = false;
            let mut last_dir_fire = Instant::now();
            let mut last_packet: u32 = 0;
            let mut last_activity = Instant::now();

            loop {
                let mut state = XInputState::default();
                let mut found = false;
                for slot in 0..4u32 {
                    if unsafe { get_state(slot, &mut state) } == 0 {
                        found = true;
                        break;
                    }
                }

                if found != connected {
                    connected = found;
                    prev_buttons = 0;
                    prev_lt = false;
                    prev_rt = false;
                    prev_dir = (0, 0);
                    last_packet = state.packet_number;
                    last_activity = Instant::now();
                    on_action(GamepadAction::Connected(connected));
                }

                if !connected {
                    std::thread::sleep(Duration::from_millis(500));
                    continue;
                }

                let b = state.gamepad.buttons;
                let lt = state.gamepad.left_trigger >= TRIGGER_THRESHOLD;
                let rt = state.gamepad.right_trigger >= TRIGGER_THRESHOLD;

                if !is_process_foreground() {
                    prev_buttons = b;
                    prev_lt = lt;
                    prev_rt = rt;
                    prev_dir = (0, 0);
                    dir_repeating = false;
                    std::thread::sleep(Duration::from_millis(100));
                    continue;
                }

                let pressed = b & !prev_buttons;
                prev_buttons = b;

                if lt && !prev_lt {
                    on_action(GamepadAction::PrevTab);
                }
                if rt && !prev_rt {
                    on_action(GamepadAction::NextTab);
                }
                prev_lt = lt;
                prev_rt = rt;

                if pressed & LB != 0 {
                    on_action(GamepadAction::PrevRoom);
                }
                if pressed & RB != 0 {
                    on_action(GamepadAction::NextRoom);
                }
                if pressed & BTN_A != 0 {
                    on_action(GamepadAction::Confirm);
                }
                if pressed & BTN_B != 0 {
                    on_action(GamepadAction::Back);
                }
                if pressed & BTN_X != 0 {
                    on_action(GamepadAction::QuickPlay);
                }
                if pressed & (BTN_Y | BTN_BACK) != 0 {
                    on_action(GamepadAction::Search);
                }
                if pressed & BTN_START != 0 {
                    on_action(GamepadAction::Settings);
                }

                let (dx, dy) = directional_input(b, state.gamepad.thumb_lx, state.gamepad.thumb_ly);

                let now = Instant::now();
                if state.packet_number != last_packet || b != 0 || lt || rt || (dx, dy) != (0, 0) {
                    last_packet = state.packet_number;
                    last_activity = now;
                }
                let same_dir = (dx, dy) == prev_dir;
                let elapsed_ms = now.duration_since(last_dir_fire).as_millis() as u64;
                if (dx, dy) != (0, 0) && dir_repeat_due(same_dir, dir_repeating, elapsed_ms) {
                    dir_repeating = same_dir;
                    last_dir_fire = now;
                    if dx < 0 {
                        on_action(GamepadAction::Left);
                    } else if dx > 0 {
                        on_action(GamepadAction::Right);
                    } else if dy < 0 {
                        on_action(GamepadAction::Up);
                    } else if dy > 0 {
                        on_action(GamepadAction::Down);
                    }
                } else if (dx, dy) == (0, 0) {
                    dir_repeating = false;
                }
                prev_dir = (dx, dy);

                let poll_ms = if now.duration_since(last_activity) >= Duration::from_secs(2) {
                    100
                } else {
                    16
                };
                std::thread::sleep(Duration::from_millis(poll_ms));
            }
        })
        .ok();
}

#[cfg(not(windows))]
pub fn start_polling(_on_action: impl Fn(GamepadAction) + Send + 'static) {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn gamepad_kind_and_hints_match_react() {
        assert_eq!(
            detect_gamepad_kind("DualSense Wireless Controller (054c)"),
            GamepadKind::PlayStation
        );
        assert_eq!(
            detect_gamepad_kind("Nintendo Switch Pro Controller"),
            GamepadKind::Nintendo
        );
        assert_eq!(
            detect_gamepad_kind("Xbox Wireless Controller"),
            GamepadKind::Xbox
        );

        let settings_hints = hints_for(GamepadKind::PlayStation, Room::Settings, false);
        assert_eq!(settings_hints[0], ("L2/R2".into(), "Sections"));
        let overlay_hints = hints_for(GamepadKind::Xbox, Room::Library, true);
        assert_eq!(overlay_hints[1], ("X".into(), "Play"));
    }

    #[test]
    fn radial_deadzone_dominant_axis_and_repeat_delay() {
        assert_eq!(directional_input(0, 5000, 5000), (0, 0));
        assert_eq!(directional_input(0, 15000, 22000), (0, -1));
        assert_eq!(directional_input(0, -24000, 12000), (-1, 0));
        assert_eq!(directional_input(DPAD_DOWN_BIT, 25000, 0), (0, 1));

        assert!(dir_repeat_due(false, false, 0));
        assert!(!dir_repeat_due(true, false, 200));
        assert!(dir_repeat_due(true, false, 340));
        assert!(!dir_repeat_due(true, true, 90));
        assert!(dir_repeat_due(true, true, 105));
    }
}
