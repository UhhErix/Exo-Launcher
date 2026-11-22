#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GamepadAction {
    Connected(bool),
    PrevRoom,
    NextRoom,
    Up,
    Down,
    Left,
    Right,
    Confirm,
    Back,
    QuickPlay,
    Search,
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

    const DPAD_UP: u16 = 0x0001;
    const DPAD_DOWN: u16 = 0x0002;
    const DPAD_LEFT: u16 = 0x0004;
    const DPAD_RIGHT: u16 = 0x0008;
    const LB: u16 = 0x0100;
    const RB: u16 = 0x0200;
    const BTN_A: u16 = 0x1000;
    const BTN_B: u16 = 0x2000;
    const BTN_X: u16 = 0x4000;
    const BTN_Y: u16 = 0x8000;
    const STICK_DEADZONE: i16 = 14000;

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
            let mut prev_dir = (0i8, 0i8);
            let mut last_dir_fire = Instant::now();

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
                    prev_dir = (0, 0);
                    on_action(GamepadAction::Connected(connected));
                }

                if !connected {
                    std::thread::sleep(Duration::from_millis(500));
                    continue;
                }

                let b = state.gamepad.buttons;
                let pressed = b & !prev_buttons;
                prev_buttons = b;

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
                if pressed & BTN_Y != 0 {
                    on_action(GamepadAction::Search);
                }

                let dx = if (b & DPAD_RIGHT != 0) || state.gamepad.thumb_lx > STICK_DEADZONE {
                    1
                } else if (b & DPAD_LEFT != 0) || state.gamepad.thumb_lx < -STICK_DEADZONE {
                    -1
                } else {
                    0
                };
                let dy = if (b & DPAD_UP != 0) || state.gamepad.thumb_ly > STICK_DEADZONE {
                    -1
                } else if (b & DPAD_DOWN != 0) || state.gamepad.thumb_ly < -STICK_DEADZONE {
                    1
                } else {
                    0
                };

                let now = Instant::now();
                let should_fire = (dx, dy) != (0, 0)
                    && ((dx, dy) != prev_dir
                        || now.duration_since(last_dir_fire) >= Duration::from_millis(180));
                if should_fire {
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
                }
                prev_dir = (dx, dy);

                std::thread::sleep(Duration::from_millis(16));
            }
        })
        .ok();
}

#[cfg(not(windows))]
pub fn start_polling(_on_action: impl Fn(GamepadAction) + Send + 'static) {}
