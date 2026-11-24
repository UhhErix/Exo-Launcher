use crate::pc_state::SettingsSection;
use crate::state::{AppState, Room};
use exo_kit::{
    ExoGlass, FG, GAP, RADIUS_PILL, RADIUS_SMALL, apply_glass, fg_muted, icon_gamepad,
    selected_fill, type_small,
};
use rune::{El, el, row, text};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

pub const STICK_DEADZONE_NORM: f32 = 0.28;
pub const DIR_INITIAL_DELAY_MS: u64 = 340;
pub const DIR_REPEAT_MS: u64 = 105;

static WINDOW_FOCUSED: AtomicBool = AtomicBool::new(true);

pub fn set_window_focused(focused: bool) {
    WINDOW_FOCUSED.store(focused, Ordering::Release);
}

pub fn is_window_focused() -> bool {
    WINDOW_FOCUSED.load(Ordering::Acquire)
}

pub fn stick_direction(lx: i16, ly: i16) -> (i8, i8) {
    let nx = (lx as f32) / 32767.0;
    let ny = (ly as f32) / 32767.0;
    if nx.hypot(ny) < STICK_DEADZONE_NORM {
        return (0, 0);
    }
    if nx.abs() >= ny.abs() {
        (if nx > 0.0 { 1 } else { -1 }, 0)
    } else {
        (0, if ny > 0.0 { -1 } else { 1 })
    }
}

pub fn combine_dir(dpad_dx: i8, dpad_dy: i8, stick_lx: i16, stick_ly: i16) -> (i8, i8) {
    if dpad_dx != 0 {
        (dpad_dx, 0)
    } else if dpad_dy != 0 {
        (0, dpad_dy)
    } else {
        stick_direction(stick_lx, stick_ly)
    }
}

fn dir_to_action(dir: (i8, i8)) -> Option<GamepadAction> {
    match dir {
        (-1, 0) => Some(GamepadAction::Left),
        (1, 0) => Some(GamepadAction::Right),
        (0, -1) => Some(GamepadAction::Up),
        (0, 1) => Some(GamepadAction::Down),
        _ => None,
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub struct DirRepeater {
    held: (i8, i8),
    last_fire: Option<Instant>,
    repeated: bool,
}

impl DirRepeater {
    pub fn reset(&mut self) {
        self.held = (0, 0);
        self.last_fire = None;
        self.repeated = false;
    }

    pub fn step(&mut self, dir: (i8, i8), now: Instant) -> Option<GamepadAction> {
        if dir == (0, 0) {
            self.reset();
            return None;
        }
        if dir != self.held {
            self.held = dir;
            self.last_fire = Some(now);
            self.repeated = false;
            return dir_to_action(dir);
        }
        let wait_ms = if self.repeated {
            DIR_REPEAT_MS
        } else {
            DIR_INITIAL_DELAY_MS
        };
        if self
            .last_fire
            .is_some_and(|t| now.duration_since(t) >= Duration::from_millis(wait_ms))
        {
            self.last_fire = Some(now);
            self.repeated = true;
            return dir_to_action(dir);
        }
        None
    }
}

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

#[cfg(windows)]
pub fn start_polling(on_action: impl Fn(GamepadAction) + Send + 'static) {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
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

    const DPAD_UP: u16 = 0x0001;
    const DPAD_DOWN: u16 = 0x0002;
    const DPAD_LEFT: u16 = 0x0004;
    const DPAD_RIGHT: u16 = 0x0008;
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
            let mut was_focused = is_window_focused();
            let mut prev_buttons: u16 = 0;
            let mut prev_lt = false;
            let mut prev_rt = false;
            let mut repeater = DirRepeater::default();
            let mut last_packet: u32 = 0;
            let mut last_activity = Instant::now();

            loop {
                if !is_window_focused() {
                    was_focused = false;
                    prev_buttons = 0;
                    prev_lt = false;
                    prev_rt = false;
                    repeater.reset();
                    std::thread::sleep(Duration::from_millis(250));
                    continue;
                }

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
                    prev_buttons = state.gamepad.buttons;
                    prev_lt = state.gamepad.left_trigger >= TRIGGER_THRESHOLD;
                    prev_rt = state.gamepad.right_trigger >= TRIGGER_THRESHOLD;
                    repeater.reset();
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
                if !was_focused {
                    was_focused = true;
                    prev_buttons = b;
                    prev_lt = lt;
                    prev_rt = rt;
                    repeater.reset();
                    last_packet = state.packet_number;
                    std::thread::sleep(Duration::from_millis(16));
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

                let dpad_dx = if b & DPAD_RIGHT != 0 {
                    1
                } else if b & DPAD_LEFT != 0 {
                    -1
                } else {
                    0
                };
                let dpad_dy = if b & DPAD_UP != 0 {
                    -1
                } else if b & DPAD_DOWN != 0 {
                    1
                } else {
                    0
                };
                let dir = combine_dir(
                    dpad_dx,
                    dpad_dy,
                    state.gamepad.thumb_lx,
                    state.gamepad.thumb_ly,
                );

                let now = Instant::now();
                if state.packet_number != last_packet || b != 0 || lt || rt || dir != (0, 0) {
                    last_packet = state.packet_number;
                    last_activity = now;
                }
                if let Some(act) = repeater.step(dir, now) {
                    on_action(act);
                }

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
    fn stick_direction_uses_radial_deadzone_and_dominant_axis() {
        assert_eq!(stick_direction(0, 0), (0, 0));
        assert_eq!(stick_direction(6000, 6000), (0, 0));
        assert_eq!(stick_direction(7000, 20000), (0, -1));
        assert_eq!(stick_direction(-7000, -20000), (0, 1));
        assert_eq!(stick_direction(22000, 10000), (1, 0));
        assert_eq!(stick_direction(-22000, -10000), (-1, 0));
    }

    #[test]
    fn dir_repeater_honors_initial_delay_then_repeat_cadence() {
        let mut rep = DirRepeater::default();
        let t0 = Instant::now();
        assert_eq!(rep.step((1, 0), t0), Some(GamepadAction::Right));
        assert_eq!(rep.step((1, 0), t0 + Duration::from_millis(180)), None);
        assert_eq!(rep.step((1, 0), t0 + Duration::from_millis(339)), None);
        let t1 = t0 + Duration::from_millis(DIR_INITIAL_DELAY_MS);
        assert_eq!(rep.step((1, 0), t1), Some(GamepadAction::Right));
        assert_eq!(rep.step((1, 0), t1 + Duration::from_millis(80)), None);
        let t2 = t1 + Duration::from_millis(DIR_REPEAT_MS);
        assert_eq!(rep.step((1, 0), t2), Some(GamepadAction::Right));
        assert_eq!(rep.step((0, 0), t2 + Duration::from_millis(16)), None);
        assert_eq!(
            rep.step((0, 1), t2 + Duration::from_millis(32)),
            Some(GamepadAction::Down)
        );
    }
}
