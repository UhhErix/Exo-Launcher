use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ControllerFamily {
    DualSense,
    DualSenseEdge,
    DualShock4,
    XboxSeries,
    XboxElite2,
    SwitchPro,
    #[default]
    Generic,
}

impl ControllerFamily {
    pub fn from_vid_pid(vid: u16, pid: u16) -> Self {
        match (vid, pid) {
            (0x054C, 0x0CE6) => Self::DualSense,
            (0x054C, 0x0DF2) => Self::DualSenseEdge,
            (0x054C, 0x05C4 | 0x09CC) => Self::DualShock4,
            (0x045E, 0x0B00 | 0x0B05) => Self::XboxElite2,
            (0x045E, 0x0B12 | 0x0B13 | 0x02EA | 0x02FD | 0x028E) => Self::XboxSeries,
            (0x057E, 0x2009 | 0x2006 | 0x2007) => Self::SwitchPro,
            _ => Self::Generic,
        }
    }

    pub fn supports_onboard_profiles(self) -> bool {
        matches!(self, Self::DualSenseEdge | Self::XboxElite2)
    }

    pub fn is_playstation(self) -> bool {
        matches!(
            self,
            Self::DualSense | Self::DualSenseEdge | Self::DualShock4
        )
    }
}

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PadReading {
    pub lx: f32,
    pub ly: f32,
    pub rx: f32,
    pub ry: f32,
    pub l2: f32,
    pub r2: f32,
    pub buttons: u32,
    pub edge_extras: u8,
    pub timestamp_us: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GamepadNavEvent {
    Up,
    Down,
    Left,
    Right,
    Confirm,
    Back,
    PrevRoom,
    NextRoom,
}

pub const BTN_SOUTH: u32 = 1 << 0;
pub const BTN_EAST: u32 = 1 << 1;
pub const BTN_WEST: u32 = 1 << 2;
pub const BTN_NORTH: u32 = 1 << 3;
pub const BTN_LB: u32 = 1 << 4;
pub const BTN_RB: u32 = 1 << 5;
pub const BTN_DPAD_UP: u32 = 1 << 8;
pub const BTN_DPAD_DOWN: u32 = 1 << 9;
pub const BTN_DPAD_LEFT: u32 = 1 << 10;
pub const BTN_DPAD_RIGHT: u32 = 1 << 11;

pub fn map_nav_events(prev: &PadReading, curr: &PadReading) -> Vec<GamepadNavEvent> {
    let rising = curr.buttons & !prev.buttons;
    let mut out = Vec::new();
    if (rising & BTN_DPAD_UP) != 0 || (curr.ly < -0.6 && prev.ly >= -0.6) {
        out.push(GamepadNavEvent::Up);
    }
    if (rising & BTN_DPAD_DOWN) != 0 || (curr.ly > 0.6 && prev.ly <= 0.6) {
        out.push(GamepadNavEvent::Down);
    }
    if (rising & BTN_DPAD_LEFT) != 0 || (curr.lx < -0.6 && prev.lx >= -0.6) {
        out.push(GamepadNavEvent::Left);
    }
    if (rising & BTN_DPAD_RIGHT) != 0 || (curr.lx > 0.6 && prev.lx <= 0.6) {
        out.push(GamepadNavEvent::Right);
    }
    if (rising & BTN_SOUTH) != 0 {
        out.push(GamepadNavEvent::Confirm);
    }
    if (rising & BTN_EAST) != 0 {
        out.push(GamepadNavEvent::Back);
    }
    if (rising & BTN_LB) != 0 {
        out.push(GamepadNavEvent::PrevRoom);
    }
    if (rising & BTN_RB) != 0 {
        out.push(GamepadNavEvent::NextRoom);
    }
    out
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GameInputProbe {
    pub runtime_available: bool,
    pub backend: String,
}

pub fn probe_gameinput_runtime() -> GameInputProbe {
    #[cfg(windows)]
    {
        use windows_sys::Win32::Foundation::FreeLibrary;
        use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};

        let dll: Vec<u16> = "GameInput.dll\0".encode_utf16().collect();
        let handle = unsafe { LoadLibraryW(dll.as_ptr()) };
        if !handle.is_null() {
            let sym = unsafe { GetProcAddress(handle, c"GameInputCreate".as_ptr().cast()) };
            unsafe {
                let _ = FreeLibrary(handle);
            }
            if sym.is_some() {
                return GameInputProbe {
                    runtime_available: true,
                    backend: "GameInput.dll".into(),
                };
            }
        }
        GameInputProbe {
            runtime_available: false,
            backend: "RawHID+XInput fallback".into(),
        }
    }
    #[cfg(not(windows))]
    {
        GameInputProbe {
            runtime_available: false,
            backend: "RawHID fallback".into(),
        }
    }
}
