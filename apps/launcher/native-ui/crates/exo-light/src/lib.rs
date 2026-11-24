pub mod auto_context;
pub mod effects;
#[cfg(feature = "smbus-rgb")]
pub mod smbus_pawnio;
pub mod transport;
pub mod usb_hid_rgb;
pub mod win11_lamparray;

use auto_context::{AutoContextConfig, ContextState, resolve_context};
use effects::{EffectMode, render_frame};
use transport::{DeviceTier, LightDevice, Rgb8};

pub fn available_device_tiers() -> Vec<DeviceTier> {
    #[cfg(feature = "smbus-rgb")]
    {
        vec![
            DeviceTier::Win11LampArray,
            DeviceTier::UsbHid,
            DeviceTier::SmbusPawnIo,
        ]
    }
    #[cfg(not(feature = "smbus-rgb"))]
    {
        vec![DeviceTier::Win11LampArray, DeviceTier::UsbHid]
    }
}

pub const REPLACED_VENDOR_SERVICES: &[&str] = &[
    "LightingService",
    "iCUEDevicePluginHost",
    "CorsairService",
    "SignalRgb",
    "LEDKeeper2",
    "RazerCentralService",
    "LGHUBAgent",
];

pub struct LightManager {
    devices: Vec<Box<dyn LightDevice>>,
    base_mode: EffectMode,
    base_brightness_pct: u8,
    context: ContextState,
    auto_cfg: AutoContextConfig,
    static_committed: bool,
    dormant_ticks: u64,
    last_frames: Vec<Vec<Rgb8>>,
}

impl LightManager {
    pub fn new(base_mode: EffectMode, base_brightness_pct: u8) -> Self {
        Self {
            devices: Vec::new(),
            base_mode,
            base_brightness_pct: base_brightness_pct.min(100),
            context: ContextState::default(),
            auto_cfg: AutoContextConfig::default(),
            static_committed: false,
            dormant_ticks: 0,
            last_frames: Vec::new(),
        }
    }

    pub fn add_device(&mut self, device: Box<dyn LightDevice>) {
        self.devices.push(device);
        self.last_frames.push(Vec::new());
        self.static_committed = false;
    }

    pub fn set_mode(&mut self, mode: EffectMode) {
        if self.base_mode != mode {
            self.base_mode = mode;
            self.static_committed = false;
        }
    }

    pub fn set_brightness(&mut self, brightness_pct: u8) {
        let clamped = brightness_pct.min(100);
        if self.base_brightness_pct != clamped {
            self.base_brightness_pct = clamped;
            self.static_committed = false;
        }
    }

    pub fn set_context(&mut self, ctx: ContextState) {
        if self.context != ctx {
            self.context = ctx;
            self.static_committed = false;
        }
    }

    pub fn is_dormant(&self) -> bool {
        let resolved = resolve_context(
            self.base_brightness_pct,
            self.base_mode,
            self.context,
            self.auto_cfg,
        );
        resolved.mode.is_static() && self.static_committed
    }

    pub fn dormant_ticks(&self) -> u64 {
        self.dormant_ticks
    }

    pub fn tick(&mut self, elapsed_ms: u64) -> Result<usize, String> {
        let resolved = resolve_context(
            self.base_brightness_pct,
            self.base_mode,
            self.context,
            self.auto_cfg,
        );
        if resolved.mode.is_static() && self.static_committed {
            self.dormant_ticks += 1;
            return Ok(0);
        }

        let mut writes = 0usize;
        for (i, dev) in self.devices.iter_mut().enumerate() {
            let mut dev_ctx = self.context;
            dev_ctx.wireless = dev.info().wireless;
            let dev_resolved = resolve_context(
                self.base_brightness_pct,
                self.base_mode,
                dev_ctx,
                self.auto_cfg,
            );
            let zones = dev.info().zone_count;
            let frame = render_frame(
                dev_resolved.mode,
                zones,
                elapsed_ms,
                dev_resolved.brightness_pct,
            );
            if self.last_frames[i] != frame {
                dev.set_brightness(dev_resolved.brightness_pct)?;
                dev.set_zones(&frame)?;
                self.last_frames[i] = frame;
                writes += 1;
            }
        }
        if resolved.mode.is_static() {
            self.static_committed = true;
        }
        Ok(writes)
    }

    pub fn shutdown_restore(&mut self) -> Result<(), String> {
        for dev in &mut self.devices {
            dev.restore_baseline()?;
        }
        self.static_committed = false;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use transport::{DeviceTier, LightDeviceInfo, MockLampTransport};

    fn mock_kb() -> (
        MockLampTransport,
        std::sync::Arc<std::sync::Mutex<transport::MockDeviceStats>>,
    ) {
        let dev = MockLampTransport::new(
            LightDeviceInfo {
                id: "mock:kb-1".into(),
                name: "Mock LampArray Keyboard".into(),
                vendor: "ExoMock".into(),
                tier: DeviceTier::Win11LampArray,
                zone_count: 68,
                wireless: false,
            },
            Rgb8::EXO_ACCENT,
            80,
        );
        let stats = dev.stats_handle();
        (dev, stats)
    }

    #[test]
    fn static_dormancy_commits_once_and_issues_zero_subsequent_writes() {
        let (dev, stats) = mock_kb();
        let mut mgr = LightManager::new(
            EffectMode::Static {
                color: Rgb8::EXO_ACCENT,
            },
            80,
        );
        mgr.add_device(Box::new(dev));

        assert_eq!(mgr.tick(0).unwrap(), 1);
        assert!(mgr.is_dormant());
        for step in 1..=1800 {
            assert_eq!(mgr.tick(step * 33).unwrap(), 0);
        }
        assert_eq!(mgr.dormant_ticks(), 1800);
        assert_eq!(stats.lock().unwrap().write_count, 1);
    }

    #[test]
    fn hdr_auto_dim_dims_and_restores_on_exit_and_shutdown() {
        let (dev, stats) = mock_kb();
        let mut mgr = LightManager::new(EffectMode::Static { color: Rgb8::WHITE }, 100);
        mgr.add_device(Box::new(dev));

        mgr.tick(0).unwrap();
        assert_eq!(stats.lock().unwrap().last_brightness_pct, 100);

        mgr.set_context(ContextState {
            hdr_active: true,
            fullscreen_game: true,
            ..Default::default()
        });
        mgr.tick(33).unwrap();
        assert_eq!(stats.lock().unwrap().last_brightness_pct, 10);

        mgr.set_context(ContextState::default());
        mgr.tick(66).unwrap();
        assert_eq!(stats.lock().unwrap().last_brightness_pct, 100);

        mgr.shutdown_restore().unwrap();
        assert_eq!(stats.lock().unwrap().restore_count, 1);
        assert_eq!(stats.lock().unwrap().last_brightness_pct, 80);
    }

    #[test]
    fn smbus_tier_is_absent_when_feature_is_off() {
        let tiers = available_device_tiers();
        #[cfg(not(feature = "smbus-rgb"))]
        assert!(!tiers.contains(&DeviceTier::SmbusPawnIo));
        #[cfg(feature = "smbus-rgb")]
        assert!(tiers.contains(&DeviceTier::SmbusPawnIo));
    }

    #[test]
    fn source_contains_zero_winring0_or_inpoutx64_references() {
        let banned = ["WinRing0", "inpoutx64", "OLS_"];
        let files = [
            include_str!("lib.rs"),
            include_str!("transport.rs"),
            include_str!("win11_lamparray.rs"),
            include_str!("usb_hid_rgb.rs"),
            include_str!("smbus_pawnio.rs"),
            include_str!("effects.rs"),
            include_str!("auto_context.rs"),
        ];
        for src in files {
            for needle in banned {
                assert!(
                    !src.contains(needle) || src.contains("let banned ="),
                    "forbidden driver symbol {needle} found in exo-light"
                );
            }
        }
    }
}
