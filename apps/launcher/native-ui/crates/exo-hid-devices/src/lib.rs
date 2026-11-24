pub mod auto_profiles;
pub mod drivers;
pub mod irq_core;
pub mod poll_diag;
pub mod registry;
pub mod transport;
pub mod usb_topology;

use auto_profiles::{
    AntiCheatClass, AutoProfileEngine, AutoSwitchDecision, GameProfileRule, MouseRemapMode,
};
use drivers::{
    ClickMode, DpiStage, LodLevel, MouseConfig, MouseSnapshot, WritePersistence, driver_for_family,
};
use poll_diag::{PollingAnalyzer, PollingReport};
use registry::{
    DeviceCapabilities, DeviceCategory, DeviceModelEntry, DeviceVendor, DriverFamily, match_device,
};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::time::Instant;
use transport::{HidDeviceDescriptor, HidError, HidTransport, HidWriteRateLimiter};
use usb_topology::{UsbPortClass, UsbTopologyReport, analyze_usb_chain};

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct NamedMouseProfile {
    pub id: String,
    pub name: String,
    pub config: MouseConfig,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManagedMouseDevice {
    pub key: String,
    pub vid: u16,
    pub pid: u16,
    pub vendor: DeviceVendor,
    pub driver: DriverFamily,
    pub model_name: String,
    pub firmware: String,
    pub battery_pct: Option<u8>,
    pub charging: bool,
    pub capabilities: DeviceCapabilities,
    pub config: MouseConfig,
    pub usb_topology: UsbTopologyReport,
    pub polling_report: Option<PollingReport>,
    pub is_mock: bool,
}

pub struct DeviceManager<T: HidTransport> {
    pub transport: T,
    pub rate_limiter: HidWriteRateLimiter,
    pub devices: Vec<ManagedMouseDevice>,
    pub profiles: HashMap<String, Vec<NamedMouseProfile>>,
    pub auto_engines: HashMap<String, AutoProfileEngine>,
    pub analyzers: HashMap<String, PollingAnalyzer>,
}

impl<T: HidTransport> DeviceManager<T> {
    pub fn new(transport: T) -> Self {
        Self {
            transport,
            rate_limiter: HidWriteRateLimiter::default(),
            devices: Vec::new(),
            profiles: HashMap::new(),
            auto_engines: HashMap::new(),
            analyzers: HashMap::new(),
        }
    }

    pub fn scan_supported_devices(&mut self) -> Result<Vec<ManagedMouseDevice>, HidError> {
        let raw = self.transport.enumerate()?;
        let mut found = Vec::new();
        for desc in raw {
            let Some(model) = match_device(desc.vid, desc.pid) else {
                continue;
            };
            if model.category != DeviceCategory::Mouse {
                continue;
            }
            if found.iter().any(|d: &ManagedMouseDevice| d.key == desc.key) {
                continue;
            }
            let topo = desc
                .instance_id
                .as_deref()
                .map(|id| {
                    let chain = usb_topology::walk_device_chain(id);
                    analyze_usb_chain(&chain, 1000)
                })
                .unwrap_or(UsbTopologyReport {
                    port_class: UsbPortClass::Unknown,
                    controller_id: None,
                    hub_hops: 0,
                    warning: None,
                });
            let mut config = MouseConfig::default();
            config.clamp_to(&model.capabilities);
            found.push(ManagedMouseDevice {
                key: desc.key.clone(),
                vid: desc.vid,
                pid: desc.pid,
                vendor: model.vendor,
                driver: model.driver,
                model_name: if desc.product_name.is_empty() {
                    model.model_name
                } else {
                    desc.product_name
                },
                firmware: String::new(),
                battery_pct: None,
                charging: false,
                capabilities: model.capabilities,
                config: config.clone(),
                usb_topology: topo,
                polling_report: None,
                is_mock: false,
            });
            self.auto_engines
                .entry(desc.key)
                .or_insert_with(|| AutoProfileEngine::new(config, 500));
        }
        self.devices = found.clone();
        Ok(found)
    }

    pub fn read_device_snapshot(&mut self, key: &str) -> Result<MouseSnapshot, HidError> {
        let driver_family = self
            .devices
            .iter()
            .find(|d| d.key == key)
            .map(|d| d.driver)
            .ok_or_else(|| HidError::NotFound(key.to_string()))?;
        let driver = driver_for_family(driver_family)
            .ok_or_else(|| HidError::InvalidReport("No mouse driver for family".into()))?;
        if !self.transport.is_open(key) {
            self.transport.open(key)?;
        }
        let mut snap = driver.read_snapshot(&mut self.transport, key)?;
        if let Some(dev) = self.devices.iter_mut().find(|d| d.key == key) {
            snap.config.clamp_to(&dev.capabilities);
            dev.firmware = snap.firmware.clone();
            dev.battery_pct = snap.battery_pct;
            dev.charging = snap.charging;
            dev.config = snap.config.clone();
            if let Some(engine) = self.auto_engines.get_mut(key) {
                engine.set_applied_state(snap.config.clone());
            }
        }
        Ok(snap)
    }

    pub fn apply_device_config(
        &mut self,
        key: &str,
        mut target: MouseConfig,
        persistence: WritePersistence,
        now: Instant,
    ) -> Result<usize, HidError> {
        let (driver_family, current, caps) = self
            .devices
            .iter()
            .find(|d| d.key == key)
            .map(|d| (d.driver, d.config.clone(), d.capabilities.clone()))
            .ok_or_else(|| HidError::NotFound(key.to_string()))?;

        target.clamp_to(&caps);
        if current == target && persistence == WritePersistence::VolatileRam {
            return Ok(0);
        }

        self.rate_limiter.check_and_record(key, now)?;

        let driver = driver_for_family(driver_family)
            .ok_or_else(|| HidError::InvalidReport("No mouse driver for family".into()))?;
        if !self.transport.is_open(key) {
            self.transport.open(key)?;
        }
        let writes =
            driver.apply_config(&mut self.transport, key, &current, &target, persistence)?;
        if let Some(dev) = self.devices.iter_mut().find(|d| d.key == key) {
            dev.config = target.clone();
        }
        if let Some(engine) = self.auto_engines.get_mut(key) {
            engine.set_applied_state(target);
        }
        Ok(writes)
    }

    pub fn tick_auto_switch(
        &mut self,
        key: &str,
        now: Instant,
    ) -> Result<AutoSwitchDecision, HidError> {
        let decision = self
            .auto_engines
            .get_mut(key)
            .map(|e| e.poll(now))
            .unwrap_or(AutoSwitchDecision::SkippedUnchanged);
        if let AutoSwitchDecision::Apply {
            ref config,
            persistence,
            ..
        } = decision
        {
            debug_assert_eq!(persistence, WritePersistence::VolatileRam);
            let _ =
                self.apply_device_config(key, config.clone(), WritePersistence::VolatileRam, now)?;
        }
        Ok(decision)
    }

    pub fn retired_vendor_suites(&self) -> Vec<&'static str> {
        let mut out = Vec::new();
        for dev in &self.devices {
            if let Some(suite) = dev.vendor.retired_vendor_suite()
                && !out.contains(&suite)
            {
                out.push(suite);
            }
        }
        out
    }
}

pub fn dev_mock_devices() -> Vec<ManagedMouseDevice> {
    let ulx_entry = match_device(0x361D, 0x0103).expect("Finalmouse Starlight X entry");
    let viper_entry = match_device(0x1532, 0x00C1).expect("Razer Viper V3 Pro entry");
    let gpx_entry = match_device(0x046D, 0xC09B).expect("Logitech GPX2 entry");
    let pulsar_entry = match_device(0x3710, 0x5401).expect("Pulsar X2V2 entry");

    vec![
        mock_device(
            "mock:finalmouse-tmr",
            ulx_entry,
            "1.2.8",
            Some(84),
            MouseConfig {
                active_profile: 0,
                active_dpi_stage: 1,
                dpi_stages: vec![
                    DpiStage {
                        dpi: 400,
                        color: "#FF5C5C".into(),
                    },
                    DpiStage {
                        dpi: 800,
                        color: "#3DD68C".into(),
                    },
                    DpiStage {
                        dpi: 1600,
                        color: "#3B82F6".into(),
                    },
                    DpiStage {
                        dpi: 3200,
                        color: "#FFB340".into(),
                    },
                ],
                polling_hz: 4000,
                lod: LodLevel::Low1Mm,
                motion_sync: true,
                angle_snapping: false,
                ripple_control: false,
                debounce_ms: 1,
                click_mode_left: ClickMode::UltraClick,
                click_mode_right: ClickMode::IntentTrigger,
                sensor_rotation_deg: 0,
                receiver_led_hex: "#3DD68C".into(),
            },
            UsbTopologyReport {
                port_class: UsbPortClass::CpuDirect,
                controller_id: Some("PCI\\VEN_1022&DEV_149C".into()),
                hub_hops: 0,
                warning: None,
            },
        ),
        mock_device(
            "mock:razer-viper-v3-pro",
            viper_entry,
            "1.4.2",
            Some(72),
            MouseConfig {
                active_profile: 0,
                active_dpi_stage: 2,
                dpi_stages: vec![
                    DpiStage {
                        dpi: 400,
                        color: "#FF5C5C".into(),
                    },
                    DpiStage {
                        dpi: 800,
                        color: "#3DD68C".into(),
                    },
                    DpiStage {
                        dpi: 1600,
                        color: "#3B82F6".into(),
                    },
                    DpiStage {
                        dpi: 3200,
                        color: "#FFB340".into(),
                    },
                ],
                polling_hz: 8000,
                lod: LodLevel::Low1Mm,
                motion_sync: true,
                angle_snapping: false,
                ripple_control: false,
                debounce_ms: 0,
                click_mode_left: ClickMode::Standard,
                click_mode_right: ClickMode::Standard,
                sensor_rotation_deg: -4,
                receiver_led_hex: "#FFFFFF".into(),
            },
            UsbTopologyReport {
                port_class: UsbPortClass::Chipset,
                controller_id: Some("PCI\\VEN_1022&DEV_43EE".into()),
                hub_hops: 0,
                warning: Some(
                    "8000 Hz mouse is on a chipset USB controller — a CPU-direct rear port reduces polling latency."
                        .into(),
                ),
            },
        ),
        mock_device(
            "mock:logitech-gpx2",
            gpx_entry,
            "32.3.26",
            Some(91),
            MouseConfig {
                active_profile: 0,
                active_dpi_stage: 1,
                dpi_stages: vec![
                    DpiStage {
                        dpi: 400,
                        color: "#FF5C5C".into(),
                    },
                    DpiStage {
                        dpi: 800,
                        color: "#3DD68C".into(),
                    },
                    DpiStage {
                        dpi: 1600,
                        color: "#3B82F6".into(),
                    },
                    DpiStage {
                        dpi: 3200,
                        color: "#FFB340".into(),
                    },
                ],
                polling_hz: 2000,
                lod: LodLevel::Low1Mm,
                motion_sync: false,
                angle_snapping: false,
                ripple_control: false,
                debounce_ms: 0,
                click_mode_left: ClickMode::Standard,
                click_mode_right: ClickMode::Standard,
                sensor_rotation_deg: 0,
                receiver_led_hex: "#FFFFFF".into(),
            },
            UsbTopologyReport {
                port_class: UsbPortClass::CpuDirect,
                controller_id: Some("PCI\\VEN_1022&DEV_149C".into()),
                hub_hops: 0,
                warning: None,
            },
        ),
        mock_device(
            "mock:pulsar-x2v2",
            pulsar_entry,
            "1.0.9",
            Some(63),
            MouseConfig {
                active_profile: 0,
                active_dpi_stage: 1,
                dpi_stages: vec![
                    DpiStage {
                        dpi: 400,
                        color: "#FF5C5C".into(),
                    },
                    DpiStage {
                        dpi: 800,
                        color: "#3DD68C".into(),
                    },
                    DpiStage {
                        dpi: 1600,
                        color: "#3B82F6".into(),
                    },
                    DpiStage {
                        dpi: 3200,
                        color: "#FFB340".into(),
                    },
                ],
                polling_hz: 4000,
                lod: LodLevel::Low1Mm,
                motion_sync: true,
                angle_snapping: false,
                ripple_control: false,
                debounce_ms: 2,
                click_mode_left: ClickMode::Standard,
                click_mode_right: ClickMode::Standard,
                sensor_rotation_deg: 0,
                receiver_led_hex: "#FFFFFF".into(),
            },
            UsbTopologyReport {
                port_class: UsbPortClass::CpuDirect,
                controller_id: Some("PCI\\VEN_1022&DEV_149C".into()),
                hub_hops: 0,
                warning: None,
            },
        ),
    ]
}

pub fn default_named_profiles(
    base: &MouseConfig,
    caps: &DeviceCapabilities,
) -> Vec<NamedMouseProfile> {
    let mut comp = base.clone();
    comp.polling_hz = *caps.polling_rates_hz.last().unwrap_or(&1000);
    comp.motion_sync = caps.has_motion_sync;
    comp.lod = LodLevel::Low1Mm;
    if caps.has_tmr_ds_click_modes {
        comp.click_mode_left = ClickMode::UltraClick;
        comp.click_mode_right = ClickMode::IntentTrigger;
    }
    comp.clamp_to(caps);

    let mut desktop = base.clone();
    desktop.polling_hz = 1000;
    if caps.has_tmr_ds_click_modes {
        desktop.click_mode_left = ClickMode::Standard;
        desktop.click_mode_right = ClickMode::Standard;
    }
    desktop.clamp_to(caps);

    vec![
        NamedMouseProfile {
            id: "profile-default".into(),
            name: "Default".into(),
            config: base.clone(),
        },
        NamedMouseProfile {
            id: "profile-competitive".into(),
            name: "Competitive".into(),
            config: comp,
        },
        NamedMouseProfile {
            id: "profile-desktop".into(),
            name: "Desktop".into(),
            config: desktop,
        },
    ]
}

pub fn default_game_rules(base: &MouseConfig, caps: &DeviceCapabilities) -> Vec<GameProfileRule> {
    let mut comp = base.clone();
    comp.polling_hz = *caps.polling_rates_hz.last().unwrap_or(&1000);
    if caps.has_tmr_ds_click_modes {
        comp.click_mode_left = ClickMode::UltraClick;
        comp.click_mode_right = ClickMode::IntentTrigger;
    }
    comp.clamp_to(caps);
    vec![
        GameProfileRule {
            id: "rule-valorant".into(),
            game_id: "riot:valorant".into(),
            game_name: "VALORANT".into(),
            profile_name: "Competitive".into(),
            config: comp.clone(),
            anti_cheat_class: AntiCheatClass::CompetitivePvpOnline,
            remap_mode: MouseRemapMode::HardwareOnboard,
        },
        GameProfileRule {
            id: "rule-cs2".into(),
            game_id: "steam:730".into(),
            game_name: "Counter-Strike 2".into(),
            profile_name: "Competitive".into(),
            config: comp,
            anti_cheat_class: AntiCheatClass::CompetitivePvpOnline,
            remap_mode: MouseRemapMode::HardwareOnboard,
        },
    ]
}

fn mock_device(
    key: &str,
    entry: DeviceModelEntry,
    firmware: &str,
    battery_pct: Option<u8>,
    mut config: MouseConfig,
    usb_topology: UsbTopologyReport,
) -> ManagedMouseDevice {
    config.clamp_to(&entry.capabilities);
    ManagedMouseDevice {
        key: key.into(),
        vid: entry.vid,
        pid: entry.pid,
        vendor: entry.vendor,
        driver: entry.driver,
        model_name: entry.model_name,
        firmware: firmware.into(),
        battery_pct,
        charging: false,
        capabilities: entry.capabilities,
        config,
        usb_topology,
        polling_report: None,
        is_mock: true,
    }
}

pub fn mock_descriptor(key: &str, vid: u16, pid: u16, name: &str) -> HidDeviceDescriptor {
    HidDeviceDescriptor {
        key: key.into(),
        vid,
        pid,
        usage_page: 0xFF00,
        usage: 0x0001,
        product_name: name.into(),
        manufacturer: String::new(),
        input_report_len: 64,
        output_report_len: 64,
        feature_report_len: 64,
        instance_id: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drivers::{finalmouse, logitech_hidpp, oem_nordic, razer, wooting};
    use crate::irq_core::{CpuCoreDescriptor, plan_xhci_irq_affinity};
    use crate::poll_diag::PollingHealth;
    use crate::transport::MockHidTransport;
    use std::time::Duration;

    fn make_fm_reply(cmd: u8, payload: &[u8]) -> Vec<u8> {
        let mut pkt = vec![0u8; finalmouse::PACKET_LEN];
        pkt[0] = finalmouse::MAGIC_DEVICE;
        pkt[1] = cmd;
        pkt[2] = payload.len() as u8;
        pkt[3..3 + payload.len()].copy_from_slice(payload);
        pkt[finalmouse::PACKET_LEN - 1] = finalmouse::checksum(&pkt[..finalmouse::PACKET_LEN - 1]);
        pkt
    }

    fn make_razer_reply(status: u8, class: u8, cmd: u8, args: &[u8]) -> Vec<u8> {
        let mut pkt = vec![0u8; razer::REPORT_LEN];
        pkt[0] = status;
        pkt[1] = 0x1F;
        pkt[5] = args.len() as u8;
        pkt[6] = class;
        pkt[7] = cmd;
        pkt[8..8 + args.len()].copy_from_slice(args);
        pkt[88] = razer::crc_xor(&pkt);
        pkt
    }

    fn make_nordic_reply(opcode: u8, payload: &[u8]) -> Vec<u8> {
        let mut pkt = vec![0u8; oem_nordic::PACKET_LEN];
        pkt[0] = opcode;
        pkt[4] = payload.len() as u8;
        pkt[5..5 + payload.len()].copy_from_slice(payload);
        pkt[oem_nordic::PACKET_LEN - 1] = oem_nordic::checksum(&pkt);
        pkt
    }

    #[test]
    fn finalmouse_golden_roundtrip_crc_tmr_ds_and_bootloader_guard() {
        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor(
            "fm1",
            0x361D,
            0x0103,
            "Finalmouse Starlight X (TMR-DS)",
        ));
        mock.set_responder(|_key, _id, payload| {
            let cmd = payload[1];
            match cmd {
                finalmouse::CMD_GET_STATUS => {
                    let mut st = vec![0u8; 19];
                    st[0] = 1;
                    st[1] = 2;
                    st[2] = 8;
                    st[3] = 87;
                    st[4] = 0;
                    st[5] = 1;
                    st[6..8].copy_from_slice(&1600u16.to_le_bytes());
                    st[8..10].copy_from_slice(&4000u16.to_le_bytes());
                    st[10] = 1;
                    st[11] = 1;
                    st[12] = 0x01;
                    st[13] = 0x03;
                    st[14] = 1;
                    st[15] = 0;
                    st[16..19].copy_from_slice(&[0x3D, 0xD6, 0x8C]);
                    Some(make_fm_reply(cmd, &st))
                }
                _ => Some(make_fm_reply(cmd, &[0x00])),
            }
        });

        let mut mgr = DeviceManager::new(mock);
        let devs = mgr.scan_supported_devices().unwrap();
        assert_eq!(devs.len(), 1);
        assert!(devs[0].capabilities.has_tmr_ds_click_modes);

        let snap = mgr.read_device_snapshot("fm1").unwrap();
        assert_eq!(snap.firmware, "1.2.8");
        assert_eq!(snap.battery_pct, Some(87));
        assert_eq!(snap.config.active_dpi(), 1600);
        assert_eq!(snap.config.polling_hz, 4000);
        assert_eq!(snap.config.click_mode_right, ClickMode::IntentTrigger);
        assert_eq!(snap.config.click_mode_left, ClickMode::UltraClick);
        assert_eq!(snap.config.receiver_led_hex, "#3DD68C");

        let mut next = snap.config.clone();
        next.polling_hz = 8000;
        next.click_mode_right = ClickMode::UltraClick;
        next.debounce_ms = 0;
        let writes = mgr
            .apply_device_config("fm1", next, WritePersistence::CommitFlash, Instant::now())
            .unwrap();
        assert_eq!(writes, 3);

        let click_pkt = mgr
            .transport
            .written_outputs
            .iter()
            .find(|w| w.payload[1] == finalmouse::CMD_SET_CLICK_MODE)
            .expect("click mode packet");
        assert_eq!(&click_pkt.payload[3..5], &[0x03, 0x03]);

        assert!(matches!(
            finalmouse::encode_packet(finalmouse::CMD_BOOTLOADER_DFU, &[]),
            Err(HidError::BootloaderBlocked(_))
        ));
        assert!(matches!(
            finalmouse::encode_packet(finalmouse::CMD_FACTORY_RESET, &[]),
            Err(HidError::BootloaderBlocked(_))
        ));

        let mut bad = make_fm_reply(finalmouse::CMD_GET_STATUS, &[1, 2, 3]);
        bad[63] ^= 0xFF;
        assert!(matches!(
            finalmouse::verify_reply(&bad, finalmouse::CMD_GET_STATUS),
            Err(HidError::InvalidReport(_))
        ));
    }

    #[test]
    fn razer_golden_roundtrip_crc_busy_retry_rotation_and_bootloader_guard() {
        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor("rz1", 0x1532, 0x00C1, "Razer Viper V3 Pro"));
        let mut busy_once = true;
        mock.set_responder(move |_key, _id, payload| {
            let class = payload[6];
            let cmd = payload[7];
            if class == razer::CLASS_DEVICE && cmd == razer::CMD_GET_FIRMWARE && busy_once {
                busy_once = false;
                return Some(make_razer_reply(razer::STATUS_BUSY, class, cmd, &[0, 0]));
            }
            match (class, cmd) {
                (razer::CLASS_DEVICE, razer::CMD_GET_FIRMWARE) => {
                    Some(make_razer_reply(razer::STATUS_OK, class, cmd, &[1, 4]))
                }
                (razer::CLASS_DPI, razer::CMD_GET_DPI_XY) => Some(make_razer_reply(
                    razer::STATUS_OK,
                    class,
                    cmd,
                    &[0x01, 0x06, 0x40, 0x06, 0x40, 0, 0],
                )),
                (razer::CLASS_DEVICE, razer::CMD_GET_POLLING_8K) => Some(make_razer_reply(
                    razer::STATUS_OK,
                    class,
                    cmd,
                    &[0x00, 0x01],
                )),
                (razer::CLASS_BATTERY, razer::CMD_GET_BATTERY) => {
                    Some(make_razer_reply(razer::STATUS_OK, class, cmd, &[0x00, 204]))
                }
                (razer::CLASS_BATTERY, razer::CMD_GET_CHARGING) => {
                    Some(make_razer_reply(razer::STATUS_OK, class, cmd, &[0x00, 0]))
                }
                (razer::CLASS_SENSOR, razer::CMD_GET_ROTATION) => Some(make_razer_reply(
                    razer::STATUS_OK,
                    class,
                    cmd,
                    &[0x01, (-6i8) as u8],
                )),
                _ => Some(make_razer_reply(
                    razer::STATUS_OK,
                    class,
                    cmd,
                    &[0x01, 0x00],
                )),
            }
        });

        let mut mgr = DeviceManager::new(mock);
        mgr.scan_supported_devices().unwrap();
        let snap = mgr.read_device_snapshot("rz1").unwrap();
        assert_eq!(snap.firmware, "v1.4");
        assert_eq!(snap.config.active_dpi(), 1600);
        assert_eq!(snap.config.polling_hz, 8000);
        assert_eq!(snap.battery_pct, Some(80));
        assert_eq!(snap.config.sensor_rotation_deg, -6);

        let mut next = snap.config.clone();
        next.sensor_rotation_deg = 12;
        next.polling_hz = 4000;
        let writes = mgr
            .apply_device_config("rz1", next, WritePersistence::VolatileRam, Instant::now())
            .unwrap();
        assert_eq!(writes, 2);

        assert!(matches!(
            razer::encode_report(0x1F, razer::CLASS_DEVICE, razer::CMD_BOOTLOADER, &[]),
            Err(HidError::BootloaderBlocked(_))
        ));
        assert!(razer::encode_rotation_angle(31).is_err());
        assert!(razer::encode_rotation_angle(-31).is_err());
    }

    #[test]
    fn logitech_hidpp_feature_discovery_and_writes() {
        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor(
            "lg1",
            0x046D,
            0xC09B,
            "Logitech G Pro X Superlight 2",
        ));
        mock.set_responder(|_key, _id, body| {
            let feat_idx = body[1];
            let fn_id = body[2] >> 4;
            let mut rep = vec![0u8; 20];
            rep[0] = logitech_hidpp::REPORT_LONG;
            rep[1] = body[0];
            rep[2] = feat_idx;
            rep[3] = body[2];
            if feat_idx == 0x00 && fn_id == 0x00 {
                let fid = u16::from_be_bytes([body[3], body[4]]);
                rep[4] = match fid {
                    logitech_hidpp::FEATURE_UNIFIED_BATTERY => 0x05,
                    logitech_hidpp::FEATURE_ADJUSTABLE_DPI => 0x08,
                    logitech_hidpp::FEATURE_EXTENDED_REPORT_RATE => 0x09,
                    logitech_hidpp::FEATURE_ONBOARD_PROFILES => 0x0A,
                    _ => 0x00,
                };
            } else if feat_idx == 0x05 {
                rep[4] = 92;
                rep[6] = 0;
            } else if feat_idx == 0x08 && fn_id == 0x02 {
                rep[5..7].copy_from_slice(&1600u16.to_be_bytes());
            } else if feat_idx == 0x09 && fn_id == 0x01 {
                rep[4] = 0x02;
            } else if feat_idx == 0x0A && fn_id == 0x04 {
                rep[5] = 1;
            }
            Some(rep)
        });

        let mut mgr = DeviceManager::new(mock);
        mgr.scan_supported_devices().unwrap();
        let snap = mgr.read_device_snapshot("lg1").unwrap();
        assert_eq!(snap.battery_pct, Some(92));
        assert_eq!(snap.config.active_dpi(), 1600);
        assert_eq!(snap.config.polling_hz, 4000);
        assert_eq!(snap.config.active_profile, 1);

        let mut next = snap.config.clone();
        next.dpi_stages[1].dpi = 800;
        next.polling_hz = 8000;
        let writes = mgr
            .apply_device_config("lg1", next, WritePersistence::VolatileRam, Instant::now())
            .unwrap();
        assert_eq!(writes, 2);

        assert!(matches!(
            logitech_hidpp::discover_feature_index(
                &mut mgr.transport,
                "lg1",
                logitech_hidpp::FEATURE_DFU_CONTROL
            ),
            Err(HidError::BootloaderBlocked(_))
        ));
    }

    #[test]
    fn oem_nordic_golden_roundtrip_and_checksum() {
        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor(
            "oem1",
            0x3710,
            0x5401,
            "Pulsar X2V2 Wireless",
        ));
        mock.set_responder(|_key, _id, payload| {
            let op = payload[0];
            match op {
                oem_nordic::OP_GET_BATTERY => Some(make_nordic_reply(op, &[68, 0])),
                oem_nordic::OP_READ_CONFIG => {
                    let [lo, hi] = 1600u16.to_le_bytes();
                    Some(make_nordic_reply(op, &[0x20, 1, lo, hi, 1, 0x05, 3, 0]))
                }
                _ => Some(make_nordic_reply(op, &[0x00])),
            }
        });

        let mut mgr = DeviceManager::new(mock);
        mgr.scan_supported_devices().unwrap();
        let snap = mgr.read_device_snapshot("oem1").unwrap();
        assert_eq!(snap.battery_pct, Some(68));
        assert_eq!(snap.config.polling_hz, 4000);
        assert_eq!(snap.config.active_dpi(), 1600);
        assert!(snap.config.motion_sync);
        assert!(!snap.config.angle_snapping);
        assert!(snap.config.ripple_control);
        assert_eq!(snap.config.debounce_ms, 3);

        let mut next = snap.config.clone();
        next.angle_snapping = true;
        let writes = mgr
            .apply_device_config("oem1", next, WritePersistence::CommitFlash, Instant::now())
            .unwrap();
        assert_eq!(writes, 2);
        assert!(matches!(
            oem_nordic::encode_packet(oem_nordic::OP_BOOTLOADER, 0, &[]),
            Err(HidError::BootloaderBlocked(_))
        ));
    }

    #[test]
    fn wooting_arm_and_avr_framing_sequencing_and_bounds() {
        let caps_60v2 = wooting::capabilities_for_pid(0x31E3, 0x1342).expect("60HE v2");
        assert_eq!(caps_60v2.framing, wooting::WootingFraming::ArmMultiReport);
        assert_eq!(caps_60v2.output_report_len, 2047);
        assert_eq!(caps_60v2.max_scan_hz, 8000);

        let caps_one = wooting::capabilities_for_pid(0x03EB, 0x2402).expect("Wooting One legacy");
        assert_eq!(caps_one.min_actuation_mm, 1.5);
        assert!(!caps_one.has_rapid_trigger);

        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor("w1", 0x31E3, 0x1342, "Wooting 60HE v2"));
        mock.open("w1").unwrap();
        let writes = wooting::apply_settings(
            &mut mock,
            "w1",
            &caps_60v2,
            &wooting::WootingSettings {
                active_profile: 0,
                actuation_mm: 0.2,
                rapid_trigger: true,
                rapid_trigger_mm: 0.15,
                tachyon: true,
                rgb_brightness: 80,
            },
        )
        .unwrap();
        assert_eq!(writes, 5);
        assert_eq!(
            mock.written_features[0].payload[2],
            wooting::CMD_WOOT_DEV_INIT
        );
        assert_eq!(mock.written_outputs[0].payload.len(), 2046);
        assert_eq!(
            mock.written_features.last().unwrap().payload[2],
            wooting::CMD_RELOAD_PROFILE
        );

        let mut ver = vec![0u8; 12];
        ver[6] = 2;
        ver[7] = 15;
        ver[8] = 0;
        assert_eq!(
            wooting::decode_version(wooting::WootingFraming::ArmMultiReport, &ver).unwrap(),
            "2.15.0"
        );
        let mut cfg = vec![0u8; 12];
        cfg[1] = wooting::MAGIC_ARM_0;
        cfg[2] = wooting::MAGIC_1;
        cfg[3] = wooting::CMD_GET_DEVICE_CONFIG;
        cfg[10] = 3;
        assert_eq!(
            wooting::decode_device_layout(wooting::WootingFraming::ArmMultiReport, &cfg).unwrap(),
            "ansi-split"
        );
        assert!(matches!(
            wooting::encode_feature_command(
                wooting::WootingFraming::ArmMultiReport,
                wooting::CMD_ENTER_BOOTLOADER,
                &[]
            ),
            Err(HidError::BootloaderBlocked(_))
        ));
    }

    #[test]
    fn write_loop_hysteresis_and_deduplication_and_rate_limit() {
        let desktop = MouseConfig {
            polling_hz: 1000,
            ..Default::default()
        };
        let mut comp = desktop.clone();
        comp.polling_hz = 4000;

        let mut engine = AutoProfileEngine::new(desktop.clone(), 500);
        engine.set_applied_state(desktop.clone());
        engine.upsert_rule(GameProfileRule {
            id: "r1".into(),
            game_id: "riot:valorant".into(),
            game_name: "VALORANT".into(),
            profile_name: "Competitive".into(),
            config: comp.clone(),
            anti_cheat_class: AntiCheatClass::CompetitivePvpOnline,
            remap_mode: MouseRemapMode::HardwareOnboard,
        });

        let t0 = Instant::now();
        let mut applied_count = 0usize;
        for i in 0..20u64 {
            let now = t0 + Duration::from_millis(i * 100);
            let game = if i % 2 == 0 {
                Some("riot:valorant")
            } else {
                None
            };
            engine.on_foreground_changed(game, now);
            if matches!(engine.poll(now), AutoSwitchDecision::Apply { .. }) {
                applied_count += 1;
            }
        }
        assert_eq!(applied_count, 0);

        let settled_game = t0 + Duration::from_millis(2_600);
        engine.on_foreground_changed(Some("riot:valorant"), t0 + Duration::from_millis(2_000));
        match engine.poll(settled_game) {
            AutoSwitchDecision::Apply { config, .. } => assert_eq!(config.polling_hz, 4000),
            other => panic!("expected Apply after settle, got {other:?}"),
        }

        engine.on_foreground_changed(
            Some("riot:valorant"),
            settled_game + Duration::from_millis(10),
        );
        assert_eq!(
            engine.poll(settled_game + Duration::from_millis(600)),
            AutoSwitchDecision::SkippedUnchanged
        );

        let mut limiter =
            HidWriteRateLimiter::new(Duration::from_millis(50), 3, Duration::from_secs(1));
        assert!(limiter.check_and_record("d1", t0).is_ok());
        assert!(matches!(
            limiter.check_and_record("d1", t0 + Duration::from_millis(10)),
            Err(HidError::RateLimited { .. })
        ));
        assert!(
            limiter
                .check_and_record("d1", t0 + Duration::from_millis(60))
                .is_ok()
        );
        assert!(
            limiter
                .check_and_record("d1", t0 + Duration::from_millis(120))
                .is_ok()
        );
        assert!(matches!(
            limiter.check_and_record("d1", t0 + Duration::from_millis(200)),
            Err(HidError::RateLimited { .. })
        ));
    }

    #[test]
    fn usb_topology_polling_diag_and_irq_affinity_planner() {
        let hub_report = analyze_usb_chain(
            &[
                "USB\\VID_361D&PID_0103\\1".into(),
                "USB\\VID_05E3&PID_0610&HUB\\2".into(),
                "USB\\ROOT_HUB30\\4".into(),
                "PCI\\VEN_1022&DEV_149C\\0".into(),
            ],
            8000,
        );
        assert_eq!(hub_report.port_class, UsbPortClass::ExternalHub);
        assert!(hub_report.warning.is_some());

        let cpu_direct = analyze_usb_chain(
            &[
                "USB\\VID_361D&PID_0103\\1".into(),
                "USB\\ROOT_HUB30\\4".into(),
                "PCI\\VEN_1022&DEV_149C\\0".into(),
            ],
            8000,
        );
        assert_eq!(cpu_direct.port_class, UsbPortClass::CpuDirect);
        assert!(cpu_direct.warning.is_none());

        let mut analyzer = PollingAnalyzer::new();
        for _ in 0..200 {
            analyzer.push_interval_us(250.0);
        }
        let clean = analyzer.report().unwrap();
        assert_eq!(clean.nominal_hz, 4000);
        assert_eq!(clean.health, PollingHealth::Good);
        assert!(clean.jitter_pct < 1.0);

        let mut noisy = PollingAnalyzer::new();
        for i in 0..100 {
            noisy.push_interval_us(if i % 2 == 0 { 120.0 } else { 380.0 });
        }
        let bad = noisy.report().unwrap();
        assert_eq!(bad.health, PollingHealth::Bad);
        assert!(bad.warning.is_some());

        let plan = plan_xhci_irq_affinity(
            "PCI\\VEN_1022&DEV_149C",
            &[
                CpuCoreDescriptor {
                    logical_index: 0,
                    physical_core: 0,
                    ccd_index: 0,
                    is_efficiency_core: false,
                    is_smt_sibling: false,
                },
                CpuCoreDescriptor {
                    logical_index: 1,
                    physical_core: 0,
                    ccd_index: 0,
                    is_efficiency_core: false,
                    is_smt_sibling: true,
                },
                CpuCoreDescriptor {
                    logical_index: 2,
                    physical_core: 1,
                    ccd_index: 0,
                    is_efficiency_core: false,
                    is_smt_sibling: false,
                },
            ],
        )
        .unwrap();
        assert_eq!(plan.recommended_logical_cpu, 2);
        assert_eq!(plan.affinity_mask, 1 << 2);
    }

    #[test]
    fn finalmouse_polls_no_change_zero_writes_and_one_change_one_batch() {
        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor("fm-1", 0x361D, 0x0103, "Starlight X"));
        mock.set_responder(|_key, _report_id, payload| {
            let cmd = payload[1];
            let mut reply = vec![0u8; finalmouse::PACKET_LEN];
            reply[0] = finalmouse::MAGIC_DEVICE;
            reply[1] = cmd;
            reply[finalmouse::PACKET_LEN - 1] =
                finalmouse::checksum(&reply[..finalmouse::PACKET_LEN - 1]);
            Some(reply)
        });

        let mut mgr = DeviceManager::new(mock);
        mgr.scan_supported_devices().unwrap();
        let t0 = Instant::now();
        let initial = mgr.devices[0].config.clone();

        for i in 0..20u64 {
            let writes = mgr
                .apply_device_config(
                    "fm-1",
                    initial.clone(),
                    WritePersistence::VolatileRam,
                    t0 + Duration::from_millis(i * 100),
                )
                .unwrap();
            assert_eq!(writes, 0);
        }
        assert!(mgr.transport.written_outputs.is_empty());

        let mut changed = initial.clone();
        changed.polling_hz = 4000;
        let writes = mgr
            .apply_device_config(
                "fm-1",
                changed.clone(),
                WritePersistence::VolatileRam,
                t0 + Duration::from_secs(3),
            )
            .unwrap();
        assert_eq!(writes, 1);
        assert_eq!(mgr.transport.written_outputs.len(), 1);
        assert_eq!(
            mgr.transport.written_outputs[0].payload[1],
            finalmouse::CMD_SET_POLLING
        );
        assert!(
            !mgr.transport
                .written_outputs
                .iter()
                .any(|w| w.payload[1] == finalmouse::CMD_COMMIT_FLASH)
        );

        for i in 0..20u64 {
            let extra = mgr
                .apply_device_config(
                    "fm-1",
                    changed.clone(),
                    WritePersistence::VolatileRam,
                    t0 + Duration::from_secs(4) + Duration::from_millis(i * 100),
                )
                .unwrap();
            assert_eq!(extra, 0);
        }
        assert_eq!(mgr.transport.written_outputs.len(), 1);

        let save_writes = mgr
            .apply_device_config(
                "fm-1",
                changed,
                WritePersistence::CommitFlash,
                t0 + Duration::from_secs(7),
            )
            .unwrap();
        assert_eq!(save_writes, 1);
        assert_eq!(
            mgr.transport.written_outputs.last().unwrap().payload[1],
            finalmouse::CMD_COMMIT_FLASH
        );
    }

    #[test]
    fn logitech_hidpp_polls_no_change_zero_writes_and_one_change_one_batch() {
        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor("lg-1", 0x046D, 0xC09B, "GPX2"));
        mock.set_responder(|_key, _report_id, body| {
            let feat_idx = body[1];
            let fn_id = body[2] >> 4;
            let mut reply = vec![0u8; 20];
            reply[0] = logitech_hidpp::REPORT_LONG;
            reply[1] = body[0];
            reply[2] = feat_idx;
            reply[3] = body[2];
            if feat_idx == 0x00 && fn_id == 0x00 {
                let feat = u16::from_be_bytes([body[3], body[4]]);
                reply[4] = match feat {
                    logitech_hidpp::FEATURE_ADJUSTABLE_DPI => 0x05,
                    logitech_hidpp::FEATURE_EXTENDED_REPORT_RATE => 0x07,
                    logitech_hidpp::FEATURE_ONBOARD_PROFILES => 0x09,
                    _ => 0x00,
                };
            }
            Some(reply)
        });

        let mut mgr = DeviceManager::new(mock);
        mgr.scan_supported_devices().unwrap();
        let t0 = Instant::now();
        let initial = mgr.devices[0].config.clone();

        for i in 0..20u64 {
            assert_eq!(
                mgr.apply_device_config(
                    "lg-1",
                    initial.clone(),
                    WritePersistence::VolatileRam,
                    t0 + Duration::from_millis(i * 100),
                )
                .unwrap(),
                0
            );
        }
        assert!(mgr.transport.written_outputs.is_empty());

        let mut changed = initial.clone();
        changed.polling_hz = 4000;
        changed.active_profile = 2;
        let writes = mgr
            .apply_device_config(
                "lg-1",
                changed.clone(),
                WritePersistence::VolatileRam,
                t0 + Duration::from_secs(3),
            )
            .unwrap();
        assert_eq!(writes, 1);
        let batch_len = mgr.transport.written_outputs.len();
        assert!(
            !mgr.transport
                .written_outputs
                .iter()
                .any(|w| w.payload[1] == 0x09)
        );

        for i in 0..20u64 {
            assert_eq!(
                mgr.apply_device_config(
                    "lg-1",
                    changed.clone(),
                    WritePersistence::VolatileRam,
                    t0 + Duration::from_secs(4) + Duration::from_millis(i * 100),
                )
                .unwrap(),
                0
            );
        }
        assert_eq!(mgr.transport.written_outputs.len(), batch_len);
    }

    #[test]
    fn razer_polls_no_change_zero_writes_and_one_change_one_batch() {
        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor("rz-1", 0x1532, 0x00C1, "Viper V3 Pro"));
        mock.set_responder(|_key, _id, req| {
            let mut reply = req.to_vec();
            reply[0] = razer::STATUS_OK;
            reply[88] = razer::crc_xor(&reply);
            Some(reply)
        });

        let mut mgr = DeviceManager::new(mock);
        mgr.scan_supported_devices().unwrap();
        let t0 = Instant::now();
        let initial = mgr.devices[0].config.clone();

        for i in 0..20u64 {
            assert_eq!(
                mgr.apply_device_config(
                    "rz-1",
                    initial.clone(),
                    WritePersistence::VolatileRam,
                    t0 + Duration::from_millis(i * 100),
                )
                .unwrap(),
                0
            );
        }
        assert!(mgr.transport.written_features.is_empty());

        let mut changed = initial.clone();
        changed.sensor_rotation_deg = -4;
        let writes = mgr
            .apply_device_config(
                "rz-1",
                changed.clone(),
                WritePersistence::VolatileRam,
                t0 + Duration::from_secs(3),
            )
            .unwrap();
        assert_eq!(writes, 1);
        assert_eq!(mgr.transport.written_features.len(), 1);
        assert_eq!(mgr.transport.written_features[0].payload[8], 0x00);

        for i in 0..20u64 {
            assert_eq!(
                mgr.apply_device_config(
                    "rz-1",
                    changed.clone(),
                    WritePersistence::VolatileRam,
                    t0 + Duration::from_secs(4) + Duration::from_millis(i * 100),
                )
                .unwrap(),
                0
            );
        }
        assert_eq!(mgr.transport.written_features.len(), 1);
    }

    #[test]
    fn wooting_polls_no_change_zero_writes_and_one_change_one_batch() {
        let caps = wooting::capabilities_for_pid(0x31E3, 0x1400).unwrap();
        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor("woot-1", 0x31E3, 0x1400, "Wooting 80HE"));
        mock.open("woot-1").unwrap();
        let current = wooting::WootingSettings {
            active_profile: 0,
            actuation_mm: 0.4,
            rapid_trigger: true,
            rapid_trigger_mm: 0.15,
            tachyon: true,
            rgb_brightness: 80,
        };

        for _ in 0..20 {
            let w = wooting::apply_settings_diff(
                &mut mock,
                "woot-1",
                &caps,
                Some(&current),
                &current,
                WritePersistence::VolatileRam,
            )
            .unwrap();
            assert_eq!(w, 0);
        }
        assert!(mock.written_outputs.is_empty());
        assert!(mock.written_features.is_empty());

        let mut next = current.clone();
        next.actuation_mm = 0.2;
        let w = wooting::apply_settings_diff(
            &mut mock,
            "woot-1",
            &caps,
            Some(&current),
            &next,
            WritePersistence::VolatileRam,
        )
        .unwrap();
        assert!(w > 0);
        assert!(
            !mock
                .written_features
                .iter()
                .any(|f| f.payload[2] == wooting::CMD_SAVE_RGB_PROFILE)
        );
        let out_after_change = mock.written_outputs.len();
        let feat_after_change = mock.written_features.len();

        for _ in 0..20 {
            assert_eq!(
                wooting::apply_settings_diff(
                    &mut mock,
                    "woot-1",
                    &caps,
                    Some(&next),
                    &next,
                    WritePersistence::VolatileRam,
                )
                .unwrap(),
                0
            );
        }
        assert_eq!(mock.written_outputs.len(), out_after_change);
        assert_eq!(mock.written_features.len(), feat_after_change);

        let save = wooting::apply_settings_diff(
            &mut mock,
            "woot-1",
            &caps,
            Some(&next),
            &next,
            WritePersistence::CommitFlash,
        )
        .unwrap();
        assert!(save > 0);
        assert!(
            mock.written_features
                .iter()
                .any(|f| f.payload[2] == wooting::CMD_SAVE_RGB_PROFILE)
        );
    }

    #[test]
    fn oem_nordic_polls_no_change_zero_writes_and_one_change_one_batch() {
        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor("oem-1", 0x3710, 0x5401, "Pulsar X2V2"));
        mock.set_responder(|_key, _id, req| Some(make_nordic_reply(req[0], &[])));

        let mut mgr = DeviceManager::new(mock);
        mgr.scan_supported_devices().unwrap();
        let t0 = Instant::now();
        let initial = mgr.devices[0].config.clone();

        for i in 0..20u64 {
            assert_eq!(
                mgr.apply_device_config(
                    "oem-1",
                    initial.clone(),
                    WritePersistence::VolatileRam,
                    t0 + Duration::from_millis(i * 100),
                )
                .unwrap(),
                0
            );
        }
        assert!(mgr.transport.written_outputs.is_empty());

        let mut changed = initial.clone();
        changed.polling_hz = 4000;
        let writes = mgr
            .apply_device_config(
                "oem-1",
                changed.clone(),
                WritePersistence::VolatileRam,
                t0 + Duration::from_secs(3),
            )
            .unwrap();
        assert_eq!(writes, 1);
        assert_eq!(mgr.transport.written_outputs.len(), 1);
        assert_eq!(
            mgr.transport.written_outputs[0].payload[0],
            oem_nordic::OP_WRITE_CONFIG
        );
        assert!(
            !mgr.transport
                .written_outputs
                .iter()
                .any(|w| w.payload[0] == oem_nordic::OP_COMMIT_EEPROM)
        );

        for i in 0..20u64 {
            assert_eq!(
                mgr.apply_device_config(
                    "oem-1",
                    changed.clone(),
                    WritePersistence::VolatileRam,
                    t0 + Duration::from_secs(4) + Duration::from_millis(i * 100),
                )
                .unwrap(),
                0
            );
        }
        assert_eq!(mgr.transport.written_outputs.len(), 1);

        let save = mgr
            .apply_device_config(
                "oem-1",
                changed,
                WritePersistence::CommitFlash,
                t0 + Duration::from_secs(7),
            )
            .unwrap();
        assert_eq!(save, 1);
        assert_eq!(
            mgr.transport.written_outputs.last().unwrap().payload[0],
            oem_nordic::OP_COMMIT_EEPROM
        );
    }

    #[test]
    fn auto_profile_switch_is_volatile_only_and_rate_limited_to_2s() {
        let mut mock = MockHidTransport::new();
        mock.add_device(mock_descriptor("fm-auto", 0x361D, 0x0103, "Starlight X"));
        mock.set_responder(|_key, _id, payload| {
            let mut reply = vec![0u8; finalmouse::PACKET_LEN];
            reply[0] = finalmouse::MAGIC_DEVICE;
            reply[1] = payload[1];
            reply[finalmouse::PACKET_LEN - 1] =
                finalmouse::checksum(&reply[..finalmouse::PACKET_LEN - 1]);
            Some(reply)
        });

        let mut mgr = DeviceManager::new(mock);
        mgr.scan_supported_devices().unwrap();
        let desktop = mgr.devices[0].config.clone();
        let mut game_a = desktop.clone();
        game_a.polling_hz = 4000;
        let mut game_b = desktop.clone();
        game_b.polling_hz = 8000;

        {
            let engine = mgr.auto_engines.get_mut("fm-auto").unwrap();
            engine.set_applied_state(desktop.clone());
            engine.upsert_rule(GameProfileRule {
                id: "ga".into(),
                game_id: "riot:valorant".into(),
                game_name: "VALORANT".into(),
                profile_name: "Val".into(),
                config: game_a,
                anti_cheat_class: AntiCheatClass::CompetitivePvpOnline,
                remap_mode: MouseRemapMode::HardwareOnboard,
            });
            engine.upsert_rule(GameProfileRule {
                id: "gb".into(),
                game_id: "steam:730".into(),
                game_name: "CS2".into(),
                profile_name: "CS2".into(),
                config: game_b,
                anti_cheat_class: AntiCheatClass::CompetitivePvpOnline,
                remap_mode: MouseRemapMode::HardwareOnboard,
            });
        }

        let t0 = Instant::now();
        mgr.auto_engines
            .get_mut("fm-auto")
            .unwrap()
            .on_foreground_changed(Some("riot:valorant"), t0);
        let first = mgr
            .tick_auto_switch("fm-auto", t0 + Duration::from_millis(600))
            .unwrap();
        assert!(matches!(
            first,
            AutoSwitchDecision::Apply {
                persistence: WritePersistence::VolatileRam,
                ..
            }
        ));
        assert_eq!(mgr.transport.written_outputs.len(), 1);
        assert!(
            !mgr.transport
                .written_outputs
                .iter()
                .any(|w| w.payload[1] == finalmouse::CMD_COMMIT_FLASH)
        );

        mgr.auto_engines
            .get_mut("fm-auto")
            .unwrap()
            .on_foreground_changed(Some("steam:730"), t0 + Duration::from_millis(800));
        let limited = mgr
            .tick_auto_switch("fm-auto", t0 + Duration::from_millis(1_500))
            .unwrap();
        assert!(matches!(limited, AutoSwitchDecision::RateLimited { .. }));
        assert_eq!(mgr.transport.written_outputs.len(), 1);

        let second = mgr
            .tick_auto_switch("fm-auto", t0 + Duration::from_millis(2_700))
            .unwrap();
        assert!(matches!(
            second,
            AutoSwitchDecision::Apply {
                persistence: WritePersistence::VolatileRam,
                ..
            }
        ));
        assert_eq!(mgr.transport.written_outputs.len(), 2);
        assert!(
            !mgr.transport
                .written_outputs
                .iter()
                .any(|w| w.payload[1] == finalmouse::CMD_COMMIT_FLASH)
        );
    }

    #[test]
    fn anti_cheat_class_enforces_onboard_only_for_online_and_unknown_games() {
        for ac in [
            AntiCheatClass::CompetitivePvpOnline,
            AntiCheatClass::OnlineNonCompetitivePve,
            AntiCheatClass::Unknown,
        ] {
            let decision = auto_profiles::resolve_mouse_remap_policy(
                MouseRemapMode::OfflineSoftwareMacroOrRemap,
                ac,
            );
            assert!(
                !decision.software_macros_allowed,
                "software macros must be disabled for {ac:?}"
            );
            assert_eq!(decision.effective_mode, MouseRemapMode::HardwareOnboard);
            assert!(decision.blocked_reason.is_some());
        }

        let offline = auto_profiles::resolve_mouse_remap_policy(
            MouseRemapMode::OfflineSoftwareMacroOrRemap,
            AntiCheatClass::OfflineAntiCheat,
        );
        assert!(offline.software_macros_allowed);
        assert_eq!(
            offline.effective_mode,
            MouseRemapMode::OfflineSoftwareMacroOrRemap
        );
        assert!(offline.blocked_reason.is_none());
    }

    #[test]
    fn read_only_hid_enumeration_benchmark_never_writes() {
        #[cfg(windows)]
        {
            let started = Instant::now();
            let list = transport::win32::Win32HidTransport::enumerate_read_only().unwrap();
            let elapsed_ms = started.elapsed().as_secs_f64() * 1000.0;
            assert!(elapsed_ms < 5_000.0, "enumeration took {elapsed_ms:.1} ms");
            let _ = list.len();
        }
    }
}
