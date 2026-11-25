use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum FanDomain {
    GpuNvidia,
    GpuAmd,
    GpuIntel,
    SuperIo,
    UsbAio,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SensorKind {
    GpuCore,
    GpuHotspot,
    CpuTctl,
    Coolant,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SensorReading {
    pub temp_c: f32,
    pub updated_at_ms: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FanChannelInfo {
    pub id: String,
    pub name: String,
    pub domain: FanDomain,
    pub supports_zero_rpm: bool,
    pub is_cpu_domain: bool,
    pub calibrated_min_rpm: u32,
}

pub trait FanBackend: Send {
    fn is_laptop_locked(&self) -> bool;
    fn channels(&self) -> &[FanChannelInfo];
    fn read_sensor(&self, sensor: SensorKind) -> Option<SensorReading>;
    fn read_rpm(&self, channel_id: &str) -> Option<u32>;
    fn set_pwm_pct(&mut self, channel_id: &str, pwm_pct: u8) -> Result<(), String>;
    fn restore_auto(&mut self, channel_id: &str) -> Result<(), String>;
}

#[derive(Debug, Clone, Default)]
pub struct MockThermalState {
    pub sensors: HashMap<SensorKind, SensorReading>,
    pub current_pwm: HashMap<String, u8>,
    pub rpm_overrides: HashMap<String, u32>,
    pub pwm_writes: Vec<(String, u8)>,
    pub restored_channels: Vec<String>,
    pub driver_reset_active: bool,
}

impl MockThermalState {
    pub fn set_sensor(&mut self, kind: SensorKind, temp_c: f32, updated_at_ms: u64) {
        self.sensors.insert(
            kind,
            SensorReading {
                temp_c,
                updated_at_ms,
            },
        );
    }
}

pub struct MockFanBackend {
    channels: Vec<FanChannelInfo>,
    laptop_locked: bool,
    state: Arc<Mutex<MockThermalState>>,
}

impl MockFanBackend {
    pub fn new(channels: Vec<FanChannelInfo>, laptop_locked: bool) -> Self {
        Self {
            channels,
            laptop_locked,
            state: Arc::new(Mutex::new(MockThermalState::default())),
        }
    }

    pub fn state_handle(&self) -> Arc<Mutex<MockThermalState>> {
        Arc::clone(&self.state)
    }
}

impl FanBackend for MockFanBackend {
    fn is_laptop_locked(&self) -> bool {
        self.laptop_locked
    }

    fn channels(&self) -> &[FanChannelInfo] {
        &self.channels
    }

    fn read_sensor(&self, sensor: SensorKind) -> Option<SensorReading> {
        self.state.lock().unwrap().sensors.get(&sensor).copied()
    }

    fn read_rpm(&self, channel_id: &str) -> Option<u32> {
        let st = self.state.lock().unwrap();
        if let Some(&rpm) = st.rpm_overrides.get(channel_id) {
            return Some(rpm);
        }
        let pwm = st.current_pwm.get(channel_id).copied().unwrap_or(35);
        if pwm == 0 {
            Some(0)
        } else {
            Some(600 + (pwm as u32) * 18)
        }
    }

    fn set_pwm_pct(&mut self, channel_id: &str, pwm_pct: u8) -> Result<(), String> {
        let mut st = self.state.lock().unwrap();
        if st.driver_reset_active {
            return Err("simulated driver reset".into());
        }
        let clamped = pwm_pct.min(100);
        st.current_pwm.insert(channel_id.to_string(), clamped);
        st.pwm_writes.push((channel_id.to_string(), clamped));
        Ok(())
    }

    fn restore_auto(&mut self, channel_id: &str) -> Result<(), String> {
        let mut st = self.state.lock().unwrap();
        st.restored_channels.push(channel_id.to_string());
        st.current_pwm.remove(channel_id);
        Ok(())
    }
}
