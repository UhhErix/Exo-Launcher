pub mod acoustic_cal;
pub mod backend;
pub mod curve;
pub mod gpu_backends;
#[cfg(feature = "superio-pawnio")]
pub mod superio_pawnio;

use backend::FanBackend;
use curve::{ChannelController, FanCurveConfig, mix_sensor_temps};
use std::collections::{HashMap, HashSet};

pub struct ThermalDaemon<B: FanBackend> {
    backend: B,
    controllers: HashMap<String, ChannelController>,
    touched_channels: HashSet<String>,
    restored: bool,
}

impl<B: FanBackend> ThermalDaemon<B> {
    pub fn new(backend: B) -> Self {
        Self {
            backend,
            controllers: HashMap::new(),
            touched_channels: HashSet::new(),
            restored: false,
        }
    }

    pub fn backend(&self) -> &B {
        &self.backend
    }

    pub fn bind_curve(&mut self, channel_id: impl Into<String>, config: FanCurveConfig) {
        self.controllers
            .insert(channel_id.into(), ChannelController::new(config));
    }

    pub fn tick(&mut self) -> Result<usize, String> {
        if self.backend.is_laptop_locked() {
            return Ok(0);
        }
        let mut updates = 0usize;
        let ids: Vec<String> = self.controllers.keys().cloned().collect();
        for id in ids {
            let readings: Vec<(f32, f32)> = {
                let ctrl = &self.controllers[&id];
                ctrl.config
                    .sensors
                    .iter()
                    .filter_map(|(kind, weight)| {
                        self.backend.read_sensor_c(*kind).map(|t| (t, *weight))
                    })
                    .collect()
            };
            let Some(mixed_temp) =
                mix_sensor_temps(self.controllers[&id].config.mix_mode, &readings)
            else {
                continue;
            };
            let pwm = self.controllers.get_mut(&id).unwrap().step(mixed_temp);
            match self.backend.set_pwm_pct(&id, pwm) {
                Ok(()) => {
                    self.touched_channels.insert(id);
                    self.restored = false;
                    updates += 1;
                }
                Err(err) => {
                    self.restore_all_to_bios();
                    return Err(err);
                }
            }
        }
        Ok(updates)
    }

    pub fn restore_all_to_bios(&mut self) {
        if self.restored && self.touched_channels.is_empty() {
            return;
        }
        let touched: Vec<String> = self.touched_channels.drain().collect();
        for id in touched {
            let _ = self.backend.restore_auto(&id);
        }
        self.restored = true;
    }
}

impl<B: FanBackend> Drop for ThermalDaemon<B> {
    fn drop(&mut self) {
        self.restore_all_to_bios();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use backend::{FanChannelInfo, FanDomain, MockFanBackend, SensorKind};
    use curve::{CurvePoint, MixMode};

    fn make_channels() -> Vec<FanChannelInfo> {
        vec![
            FanChannelInfo {
                id: "gpu:0".into(),
                name: "Mock RTX 3070 Fan 0".into(),
                domain: FanDomain::GpuNvidia,
                supports_zero_rpm: true,
                is_cpu_domain: false,
            },
            FanChannelInfo {
                id: "case:intake".into(),
                name: "Mock Case Intake".into(),
                domain: FanDomain::UsbAio,
                supports_zero_rpm: false,
                is_cpu_domain: true,
            },
        ]
    }

    #[test]
    fn drop_and_panic_always_restore_bios_auto_control() {
        let mock = MockFanBackend::new(make_channels(), false);
        let state = mock.state_handle();
        state
            .lock()
            .unwrap()
            .sensors_c
            .insert(SensorKind::GpuCore, 68.0);

        let res = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let mut daemon = ThermalDaemon::new(mock);
            daemon.bind_curve("gpu:0", FanCurveConfig::default());
            daemon.tick().unwrap();
            panic!("simulated crash");
        }));
        assert!(res.is_err());
        assert!(
            state
                .lock()
                .unwrap()
                .restored_channels
                .iter()
                .any(|c| c == "gpu:0")
        );
    }

    #[test]
    fn driver_reset_triggers_immediate_failsafe_restore() {
        let mock = MockFanBackend::new(make_channels(), false);
        let state = mock.state_handle();
        state
            .lock()
            .unwrap()
            .sensors_c
            .insert(SensorKind::GpuCore, 72.0);
        let mut daemon = ThermalDaemon::new(mock);
        daemon.bind_curve("gpu:0", FanCurveConfig::default());
        daemon.tick().unwrap();

        state.lock().unwrap().driver_reset_active = true;
        assert!(daemon.tick().is_err());
        assert!(
            state
                .lock()
                .unwrap()
                .restored_channels
                .iter()
                .any(|c| c == "gpu:0")
        );
    }

    #[test]
    fn laptop_locked_ec_stays_strictly_read_only() {
        let mock = MockFanBackend::new(make_channels(), true);
        let state = mock.state_handle();
        state
            .lock()
            .unwrap()
            .sensors_c
            .insert(SensorKind::GpuCore, 78.0);
        let mut daemon = ThermalDaemon::new(mock);
        daemon.bind_curve("gpu:0", FanCurveConfig::default());
        assert_eq!(daemon.tick().unwrap(), 0);
        assert!(state.lock().unwrap().pwm_writes.is_empty());
    }

    #[test]
    fn mixed_max_sensor_and_hard_floor_work_end_to_end() {
        let mock = MockFanBackend::new(make_channels(), false);
        let state = mock.state_handle();
        {
            let mut st = state.lock().unwrap();
            st.sensors_c.insert(SensorKind::CpuTctl, 50.0);
            st.sensors_c.insert(SensorKind::GpuHotspot, 86.0);
        }
        let mut daemon = ThermalDaemon::new(mock);
        daemon.bind_curve(
            "case:intake",
            FanCurveConfig {
                points: vec![
                    CurvePoint {
                        temp_c: 30.0,
                        pwm_pct: 10,
                    },
                    CurvePoint {
                        temp_c: 90.0,
                        pwm_pct: 10,
                    },
                ],
                mix_mode: MixMode::Max,
                sensors: vec![(SensorKind::CpuTctl, 1.0), (SensorKind::GpuHotspot, 1.0)],
                is_cpu_domain: true,
                ..Default::default()
            },
        );
        daemon.tick().unwrap();
        assert_eq!(state.lock().unwrap().current_pwm["case:intake"], 100);
    }

    #[test]
    fn source_contains_zero_winring0_or_inpoutx64_references() {
        let banned = ["WinRing0", "inpoutx64", "OLS_"];
        let files = [
            include_str!("lib.rs"),
            include_str!("backend.rs"),
            include_str!("curve.rs"),
            include_str!("acoustic_cal.rs"),
            include_str!("gpu_backends.rs"),
            include_str!("superio_pawnio.rs"),
        ];
        for src in files {
            for needle in banned {
                assert!(
                    !src.contains(needle) || src.contains("let banned ="),
                    "forbidden driver symbol {needle} found in exo-thermal"
                );
            }
        }
    }
}
