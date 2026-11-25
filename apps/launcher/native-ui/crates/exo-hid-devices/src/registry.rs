use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "camelCase")]
pub enum DeviceVendor {
    Finalmouse,
    Wooting,
    Logitech,
    Razer,
    Pulsar,
    Lamzu,
    Ninjutso,
    EndgameGear,
    WlMouse,
    VxeAtk,
}

impl DeviceVendor {
    pub fn display_name(self) -> &'static str {
        match self {
            Self::Finalmouse => "Finalmouse",
            Self::Wooting => "Wooting",
            Self::Logitech => "Logitech G",
            Self::Razer => "Razer",
            Self::Pulsar => "Pulsar",
            Self::Lamzu => "Lamzu",
            Self::Ninjutso => "Ninjutso",
            Self::EndgameGear => "Endgame Gear",
            Self::WlMouse => "WLMouse",
            Self::VxeAtk => "VXE / ATK",
        }
    }

    pub fn retired_vendor_suite(self) -> Option<&'static str> {
        match self {
            Self::Razer => Some("Razer Synapse"),
            Self::Logitech => Some("Logitech G HUB"),
            Self::Wooting => Some("Wootility"),
            Self::Finalmouse => Some("XPANEL"),
            Self::Pulsar => Some("Pulsar Fusion"),
            Self::Lamzu => Some("Lamzu Aurora"),
            Self::EndgameGear => Some("Endgame Gear Config"),
            _ => None,
        }
    }
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum DeviceCategory {
    Mouse,
    Keyboard,
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum DriverFamily {
    Finalmouse,
    Wooting,
    LogitechHidpp,
    Razer,
    OemNordic,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DeviceCapabilities {
    pub min_dpi: u32,
    pub max_dpi: u32,
    pub dpi_step: u32,
    pub max_dpi_stages: u8,
    pub polling_rates_hz: Vec<u16>,
    pub has_lod: bool,
    pub has_motion_sync: bool,
    pub has_angle_snapping: bool,
    pub has_ripple_control: bool,
    pub has_debounce_ms: bool,
    pub debounce_min_ms: u8,
    pub debounce_max_ms: u8,
    pub has_tmr_ds_click_modes: bool,
    pub has_sensor_rotation: bool,
    pub rotation_min_deg: i8,
    pub rotation_max_deg: i8,
    pub has_battery: bool,
    pub has_receiver_led: bool,
    pub max_onboard_profiles: u8,
    pub wireless: bool,
}

impl Default for DeviceCapabilities {
    fn default() -> Self {
        Self {
            min_dpi: 100,
            max_dpi: 26_000,
            dpi_step: 50,
            max_dpi_stages: 4,
            polling_rates_hz: vec![125, 250, 500, 1000],
            has_lod: true,
            has_motion_sync: true,
            has_angle_snapping: false,
            has_ripple_control: false,
            has_debounce_ms: false,
            debounce_min_ms: 0,
            debounce_max_ms: 20,
            has_tmr_ds_click_modes: false,
            has_sensor_rotation: false,
            rotation_min_deg: -30,
            rotation_max_deg: 30,
            has_battery: false,
            has_receiver_led: false,
            max_onboard_profiles: 1,
            wireless: false,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DeviceModelEntry {
    pub vid: u16,
    pub pid: u16,
    pub pid_mask: u16,
    pub vendor: DeviceVendor,
    pub category: DeviceCategory,
    pub driver: DriverFamily,
    pub model_name: String,
    pub capabilities: DeviceCapabilities,
}

pub fn match_device(vid: u16, pid: u16) -> Option<DeviceModelEntry> {
    catalog()
        .into_iter()
        .find(|e| e.vid == vid && (pid & e.pid_mask) == (e.pid & e.pid_mask))
}

pub fn catalog() -> Vec<DeviceModelEntry> {
    let mut list = Vec::with_capacity(48);

    let fm_ulx = DeviceCapabilities {
        min_dpi: 100,
        max_dpi: 26_000,
        dpi_step: 50,
        max_dpi_stages: 4,
        polling_rates_hz: vec![250, 500, 1000, 2000, 4000, 8000],
        has_lod: true,
        has_motion_sync: true,
        has_angle_snapping: false,
        has_ripple_control: false,
        has_debounce_ms: true,
        debounce_min_ms: 0,
        debounce_max_ms: 16,
        has_tmr_ds_click_modes: true,
        has_sensor_rotation: false,
        rotation_min_deg: 0,
        rotation_max_deg: 0,
        has_battery: true,
        has_receiver_led: true,
        max_onboard_profiles: 4,
        wireless: true,
    };

    for (vid, pid, name, tmr_ds) in [
        (0x361D, 0x0100, "Finalmouse ULX", true),
        (0x361D, 0x0101, "Finalmouse ULX Dongle", true),
        (0x361D, 0x0102, "Finalmouse ULX Competition", true),
        (0x361D, 0x0103, "Finalmouse Starlight X (TMR-DS)", true),
        (
            0x361D,
            0x0104,
            "Finalmouse Starlight X Dongle (TMR-DS)",
            true,
        ),
        (0x1915, 0x521C, "Finalmouse Starlight-12", false),
        (0x1915, 0x521D, "Finalmouse Starlight Pro", false),
    ] {
        let mut caps = fm_ulx.clone();
        caps.has_tmr_ds_click_modes = tmr_ds;
        if !tmr_ds {
            caps.polling_rates_hz = vec![250, 500, 1000];
        }
        list.push(DeviceModelEntry {
            vid,
            pid,
            pid_mask: 0xFFFF,
            vendor: DeviceVendor::Finalmouse,
            category: DeviceCategory::Mouse,
            driver: DriverFamily::Finalmouse,
            model_name: name.into(),
            capabilities: caps,
        });
    }

    let logi_8k = DeviceCapabilities {
        min_dpi: 100,
        max_dpi: 44_000,
        dpi_step: 50,
        max_dpi_stages: 5,
        polling_rates_hz: vec![125, 250, 500, 1000, 2000, 4000, 8000],
        has_lod: true,
        has_motion_sync: false,
        has_angle_snapping: false,
        has_ripple_control: false,
        has_debounce_ms: false,
        debounce_min_ms: 0,
        debounce_max_ms: 0,
        has_tmr_ds_click_modes: false,
        has_sensor_rotation: false,
        rotation_min_deg: 0,
        rotation_max_deg: 0,
        has_battery: true,
        has_receiver_led: false,
        max_onboard_profiles: 5,
        wireless: true,
    };

    for (pid, name, max_hz, max_dpi) in [
        (0xC094, "Logitech G Pro X Superlight", 1000u16, 25_600u32),
        (0xC09B, "Logitech G Pro X Superlight 2", 8000, 44_000),
        (0xC09E, "Logitech G Pro X Superlight 2 DEX", 8000, 44_000),
        (0xC547, "Logitech Lightspeed Receiver", 8000, 44_000),
        (0xC539, "Logitech G305 Lightspeed", 1000, 12_000),
        (0xC099, "Logitech G502 X Plus", 1000, 25_600),
        (0xC097, "Logitech G502 X Lightspeed", 1000, 25_600),
    ] {
        let mut caps = logi_8k.clone();
        caps.max_dpi = max_dpi;
        caps.polling_rates_hz = if max_hz >= 8000 {
            vec![125, 250, 500, 1000, 2000, 4000, 8000]
        } else {
            vec![125, 250, 500, 1000]
        };
        list.push(DeviceModelEntry {
            vid: 0x046D,
            pid,
            pid_mask: 0xFFFF,
            vendor: DeviceVendor::Logitech,
            category: DeviceCategory::Mouse,
            driver: DriverFamily::LogitechHidpp,
            model_name: name.into(),
            capabilities: caps,
        });
    }

    let razer_base = DeviceCapabilities {
        min_dpi: 100,
        max_dpi: 35_000,
        dpi_step: 50,
        max_dpi_stages: 5,
        polling_rates_hz: vec![125, 500, 1000, 2000, 4000, 8000],
        has_lod: true,
        has_motion_sync: true,
        has_angle_snapping: false,
        has_ripple_control: false,
        has_debounce_ms: false,
        debounce_min_ms: 0,
        debounce_max_ms: 0,
        has_tmr_ds_click_modes: false,
        has_sensor_rotation: false,
        rotation_min_deg: -30,
        rotation_max_deg: 30,
        has_battery: true,
        has_receiver_led: false,
        max_onboard_profiles: 5,
        wireless: true,
    };

    for (pid, name, rotation, max_hz, max_dpi) in [
        (
            0x00C0,
            "Razer Viper V3 Pro (Wired)",
            true,
            8000u16,
            35_000u32,
        ),
        (0x00C1, "Razer Viper V3 Pro (Wireless)", true, 8000, 35_000),
        (0x00A5, "Razer Viper V2 Pro (Wired)", false, 1000, 30_000),
        (0x00A6, "Razer Viper V2 Pro (Wireless)", false, 4000, 30_000),
        (
            0x00B6,
            "Razer DeathAdder V3 Pro (Wired)",
            false,
            1000,
            30_000,
        ),
        (
            0x00B7,
            "Razer DeathAdder V3 Pro (Wireless)",
            false,
            4000,
            30_000,
        ),
        (
            0x00B3,
            "Razer HyperPolling Wireless Dongle",
            false,
            8000,
            35_000,
        ),
    ] {
        let mut caps = razer_base.clone();
        caps.has_sensor_rotation = rotation;
        caps.max_dpi = max_dpi;
        caps.polling_rates_hz = match max_hz {
            8000 => vec![125, 500, 1000, 2000, 4000, 8000],
            4000 => vec![125, 500, 1000, 2000, 4000],
            _ => vec![125, 500, 1000],
        };
        list.push(DeviceModelEntry {
            vid: 0x1532,
            pid,
            pid_mask: 0xFFFF,
            vendor: DeviceVendor::Razer,
            category: DeviceCategory::Mouse,
            driver: DriverFamily::Razer,
            model_name: name.into(),
            capabilities: caps,
        });
    }

    let oem_base = DeviceCapabilities {
        min_dpi: 50,
        max_dpi: 30_000,
        dpi_step: 50,
        max_dpi_stages: 6,
        polling_rates_hz: vec![125, 250, 500, 1000, 2000, 4000, 8000],
        has_lod: true,
        has_motion_sync: true,
        has_angle_snapping: true,
        has_ripple_control: true,
        has_debounce_ms: true,
        debounce_min_ms: 0,
        debounce_max_ms: 20,
        has_tmr_ds_click_modes: false,
        has_sensor_rotation: false,
        rotation_min_deg: 0,
        rotation_max_deg: 0,
        has_battery: true,
        has_receiver_led: false,
        max_onboard_profiles: 4,
        wireless: true,
    };

    for (vid, pid, vendor, name) in [
        (0x3710, 0x5401, DeviceVendor::Pulsar, "Pulsar X2V2 Wireless"),
        (0x3710, 0x5402, DeviceVendor::Pulsar, "Pulsar X2H Wireless"),
        (
            0x3554,
            0xF5D5,
            DeviceVendor::Lamzu,
            "Lamzu Atlantis OG V2 4K",
        ),
        (0x3554, 0xF5D6, DeviceVendor::Lamzu, "Lamzu Maya X 8K"),
        (0x093A, 0xEB01, DeviceVendor::Ninjutso, "Ninjutso Sora V2"),
        (
            0x3367,
            0x1941,
            DeviceVendor::EndgameGear,
            "Endgame Gear OP1 8k",
        ),
        (
            0x3367,
            0x1942,
            DeviceVendor::EndgameGear,
            "Endgame Gear XM2w 4k",
        ),
        (0x36A7, 0xA801, DeviceVendor::WlMouse, "WLMouse Beast X 8K"),
        (0x373B, 0x1001, DeviceVendor::VxeAtk, "VXE R1 Pro / ATK F1"),
    ] {
        list.push(DeviceModelEntry {
            vid,
            pid,
            pid_mask: 0xFFFF,
            vendor,
            category: DeviceCategory::Mouse,
            driver: DriverFamily::OemNordic,
            model_name: name.into(),
            capabilities: oem_base.clone(),
        });
    }

    for (vid, pid, mask, name, hz) in [
        (0x03EB, 0xFF01, 0xFFFF, "Wooting One (Legacy)", 1000u16),
        (0x03EB, 0x2402, 0xFFFF, "Wooting One (Legacy)", 1000),
        (0x31E3, 0x1100, 0xFFF0, "Wooting One", 1000),
        (0x03EB, 0xFF02, 0xFFFF, "Wooting Two (Legacy)", 1000),
        (0x03EB, 0x2403, 0xFFFF, "Wooting Two (Legacy)", 1000),
        (0x31E3, 0x1200, 0xFFF0, "Wooting Two", 1000),
        (0x31E3, 0x1210, 0xFFF0, "Wooting Two Lekker Edition", 1000),
        (0x31E3, 0x1220, 0xFFF0, "Wooting Two HE (AVR)", 1000),
        (0x31E3, 0x1230, 0xFFF0, "Wooting Two HE (ARM)", 1000),
        (0x31E3, 0x1300, 0xFFF0, "Wooting 60HE (AVR)", 1000),
        (0x31E3, 0x1310, 0xFFF0, "Wooting 60HE (ARM)", 1000),
        (0x31E3, 0x1320, 0xFFF0, "Wooting 60HE+", 1000),
        (0x31E3, 0x1340, 0xFFF0, "Wooting 60HE v2", 8000),
        (0x31E3, 0x1400, 0xFFF0, "Wooting 80HE", 8000),
        (0x31E3, 0x1410, 0xFFF0, "Wooting 80HE+", 8000),
        (0x31E3, 0x1500, 0xFFF0, "Wooting UwU", 1000),
        (0x31E3, 0x1510, 0xFFF0, "Wooting UwU RGB", 1000),
    ] {
        list.push(DeviceModelEntry {
            vid,
            pid,
            pid_mask: mask,
            vendor: DeviceVendor::Wooting,
            category: DeviceCategory::Keyboard,
            driver: DriverFamily::Wooting,
            model_name: name.into(),
            capabilities: DeviceCapabilities {
                min_dpi: 0,
                max_dpi: 0,
                dpi_step: 0,
                max_dpi_stages: 0,
                polling_rates_hz: if hz >= 8000 {
                    vec![1000, 8000]
                } else {
                    vec![1000]
                },
                has_lod: false,
                has_motion_sync: false,
                has_angle_snapping: false,
                has_ripple_control: false,
                has_debounce_ms: false,
                debounce_min_ms: 0,
                debounce_max_ms: 0,
                has_tmr_ds_click_modes: false,
                has_sensor_rotation: false,
                rotation_min_deg: 0,
                rotation_max_deg: 0,
                has_battery: false,
                has_receiver_led: false,
                max_onboard_profiles: 4,
                wireless: false,
            },
        });
    }

    list
}
