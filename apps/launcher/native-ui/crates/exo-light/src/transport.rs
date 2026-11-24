use serde::{Deserialize, Serialize};
use std::sync::{Arc, Mutex};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub struct Rgb8 {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Rgb8 {
    pub const OFF: Self = Self { r: 0, g: 0, b: 0 };
    pub const WHITE: Self = Self {
        r: 255,
        g: 255,
        b: 255,
    };
    pub const AMBER: Self = Self {
        r: 255,
        g: 168,
        b: 24,
    };
    pub const EXO_ACCENT: Self = Self {
        r: 230,
        g: 235,
        b: 245,
    };

    pub const fn new(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b }
    }

    pub fn scaled(self, brightness_pct: u8) -> Self {
        let pct = brightness_pct.min(100) as u16;
        Self {
            r: ((self.r as u16 * pct) / 100) as u8,
            g: ((self.g as u16 * pct) / 100) as u8,
            b: ((self.b as u16 * pct) / 100) as u8,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum DeviceTier {
    Win11LampArray,
    UsbHid,
    SmbusPawnIo,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LightDeviceInfo {
    pub id: String,
    pub name: String,
    pub vendor: String,
    pub tier: DeviceTier,
    pub zone_count: usize,
    pub wireless: bool,
}

pub trait LightDevice: Send {
    fn info(&self) -> &LightDeviceInfo;
    fn set_zones(&mut self, colors: &[Rgb8]) -> Result<(), String>;
    fn set_brightness(&mut self, brightness_pct: u8) -> Result<(), String>;
    fn restore_baseline(&mut self) -> Result<(), String>;
}

#[derive(Debug, Clone, Default)]
pub struct MockDeviceStats {
    pub write_count: usize,
    pub brightness_writes: usize,
    pub restore_count: usize,
    pub last_brightness_pct: u8,
    pub last_frame: Vec<Rgb8>,
}

pub struct MockLampTransport {
    info: LightDeviceInfo,
    baseline_color: Rgb8,
    baseline_brightness: u8,
    stats: Arc<Mutex<MockDeviceStats>>,
}

impl MockLampTransport {
    pub fn new(info: LightDeviceInfo, baseline_color: Rgb8, baseline_brightness: u8) -> Self {
        let zones = info.zone_count;
        let stats = MockDeviceStats {
            write_count: 0,
            brightness_writes: 0,
            restore_count: 0,
            last_brightness_pct: baseline_brightness,
            last_frame: vec![baseline_color; zones],
        };
        Self {
            info,
            baseline_color,
            baseline_brightness,
            stats: Arc::new(Mutex::new(stats)),
        }
    }

    pub fn stats_handle(&self) -> Arc<Mutex<MockDeviceStats>> {
        Arc::clone(&self.stats)
    }
}

impl LightDevice for MockLampTransport {
    fn info(&self) -> &LightDeviceInfo {
        &self.info
    }

    fn set_zones(&mut self, colors: &[Rgb8]) -> Result<(), String> {
        if colors.len() != self.info.zone_count {
            return Err(format!(
                "zone mismatch: expected {}, got {}",
                self.info.zone_count,
                colors.len()
            ));
        }
        let mut s = self.stats.lock().unwrap();
        s.write_count += 1;
        s.last_frame = colors.to_vec();
        Ok(())
    }

    fn set_brightness(&mut self, brightness_pct: u8) -> Result<(), String> {
        let mut s = self.stats.lock().unwrap();
        s.brightness_writes += 1;
        s.last_brightness_pct = brightness_pct.min(100);
        Ok(())
    }

    fn restore_baseline(&mut self) -> Result<(), String> {
        let mut s = self.stats.lock().unwrap();
        s.restore_count += 1;
        s.last_brightness_pct = self.baseline_brightness;
        s.last_frame = vec![self.baseline_color; self.info.zone_count];
        Ok(())
    }
}
