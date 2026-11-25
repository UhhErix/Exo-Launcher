pub mod acoustic_cal;
pub mod backend;
pub mod curve;
pub mod gpu_backends;
#[cfg(feature = "superio-pawnio")]
pub mod superio_pawnio;

use backend::{FanBackend, FanDomain};
use curve::{ChannelController, FanCurveConfig, is_valid_temp_c, mix_sensor_temps};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

pub const SENSOR_STALE_MS: u64 = 3_000;
pub const SENSOR_FAULT_HANDOFF_MS: u64 = 10_000;
pub const FAN_STALL_MIN_COMMAND_PWM: u8 = 30;
pub const FAN_STALL_DURATION_MS: u64 = 5_000;
pub const WATCHDOG_MISSED_TICKS: u32 = 3;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "kind")]
pub enum ThermalAlert {
    SensorFault {
        channel_id: String,
        reason: String,
        handed_to_bios: bool,
    },
    FanStall {
        channel_id: String,
        domain: FanDomain,
        commanded_pwm: u8,
        measured_rpm: u32,
    },
    WatchdogBiosHandoff {
        channels: Vec<String>,
    },
}

struct WatchdogState {
    epoch: Instant,
    last_heartbeat_ms: AtomicU64,
    armed: AtomicBool,
    stop: AtomicBool,
    triggered: AtomicBool,
}

pub struct ThermalDaemon<B: FanBackend + 'static> {
    backend: Arc<Mutex<B>>,
    controllers: HashMap<String, ChannelController>,
    touched_channels: Arc<Mutex<HashSet<String>>>,
    fault_since_ms: HashMap<String, u64>,
    stall_since_ms: HashMap<String, u64>,
    last_commanded_pwm: HashMap<String, u8>,
    handed_to_bios_on_fault: HashSet<String>,
    alerts: Arc<Mutex<Vec<ThermalAlert>>>,
    watchdog_state: Option<Arc<WatchdogState>>,
    watchdog_thread: Option<JoinHandle<()>>,
    restored: bool,
}

impl<B: FanBackend + 'static> ThermalDaemon<B> {
    pub fn new(backend: B) -> Self {
        Self {
            backend: Arc::new(Mutex::new(backend)),
            controllers: HashMap::new(),
            touched_channels: Arc::new(Mutex::new(HashSet::new())),
            fault_since_ms: HashMap::new(),
            stall_since_ms: HashMap::new(),
            last_commanded_pwm: HashMap::new(),
            handed_to_bios_on_fault: HashSet::new(),
            alerts: Arc::new(Mutex::new(Vec::new())),
            watchdog_state: None,
            watchdog_thread: None,
            restored: false,
        }
    }

    pub fn start_watchdog(&mut self, tick_interval: Duration) {
        if self.watchdog_thread.is_some() {
            return;
        }
        let state = Arc::new(WatchdogState {
            epoch: Instant::now(),
            last_heartbeat_ms: AtomicU64::new(0),
            armed: AtomicBool::new(false),
            stop: AtomicBool::new(false),
            triggered: AtomicBool::new(false),
        });
        let bg_state = Arc::clone(&state);
        let bg_backend = Arc::clone(&self.backend);
        let bg_touched = Arc::clone(&self.touched_channels);
        let bg_alerts = Arc::clone(&self.alerts);
        let timeout_ms = (tick_interval.as_millis() as u64)
            .saturating_mul(WATCHDOG_MISSED_TICKS as u64)
            .max(1);
        let poll_interval = (tick_interval / 2).max(Duration::from_millis(5));

        let handle = std::thread::spawn(move || {
            while !bg_state.stop.load(Ordering::Acquire) {
                if bg_state.armed.load(Ordering::Acquire)
                    && !bg_state.triggered.load(Ordering::Acquire)
                {
                    let now_ms = bg_state.epoch.elapsed().as_millis() as u64;
                    let last = bg_state.last_heartbeat_ms.load(Ordering::Acquire);
                    if now_ms.saturating_sub(last) >= timeout_ms {
                        let channels: Vec<String> = {
                            let mut set = bg_touched.lock().unwrap();
                            set.drain().collect()
                        };
                        if !channels.is_empty() {
                            if let Ok(mut b) = bg_backend.lock() {
                                for id in &channels {
                                    let _ = b.restore_auto(id);
                                }
                            }
                            bg_alerts
                                .lock()
                                .unwrap()
                                .push(ThermalAlert::WatchdogBiosHandoff { channels });
                            bg_state.triggered.store(true, Ordering::Release);
                        }
                    }
                }
                std::thread::sleep(poll_interval);
            }
        });
        self.watchdog_state = Some(state);
        self.watchdog_thread = Some(handle);
    }

    pub fn alerts(&self) -> Vec<ThermalAlert> {
        self.alerts.lock().unwrap().clone()
    }

    pub fn bind_curve(&mut self, channel_id: impl Into<String>, config: FanCurveConfig) {
        self.controllers
            .insert(channel_id.into(), ChannelController::new(config));
    }

    fn feed_watchdog(&self) {
        if let Some(wd) = &self.watchdog_state {
            let now = wd.epoch.elapsed().as_millis() as u64;
            wd.last_heartbeat_ms.store(now, Ordering::Release);
            wd.armed.store(true, Ordering::Release);
            wd.triggered.store(false, Ordering::Release);
        }
    }

    pub fn tick_at(&mut self, now_ms: u64) -> Result<usize, String> {
        self.feed_watchdog();
        if self.backend.lock().unwrap().is_laptop_locked() {
            return Ok(0);
        }

        let channel_infos: HashMap<String, (FanDomain, u32)> = {
            let b = self.backend.lock().unwrap();
            b.channels()
                .iter()
                .map(|c| (c.id.clone(), (c.domain, c.calibrated_min_rpm)))
                .collect()
        };

        let mut stalled_domains: HashSet<FanDomain> = HashSet::new();
        for (id, &cmd_pwm) in &self.last_commanded_pwm {
            if cmd_pwm < FAN_STALL_MIN_COMMAND_PWM {
                self.stall_since_ms.remove(id);
                continue;
            }
            let Some(&(domain, min_rpm)) = channel_infos.get(id) else {
                continue;
            };
            let rpm_threshold = (min_rpm / 10).max(1);
            let measured = self.backend.lock().unwrap().read_rpm(id).unwrap_or(0);
            if measured == 0 || measured < rpm_threshold {
                let since = *self.stall_since_ms.entry(id.clone()).or_insert(now_ms);
                if now_ms.saturating_sub(since) >= FAN_STALL_DURATION_MS {
                    stalled_domains.insert(domain);
                    let already = self.alerts.lock().unwrap().iter().any(|a| {
                        matches!(a, ThermalAlert::FanStall { channel_id, .. } if channel_id == id)
                    });
                    if !already {
                        self.alerts.lock().unwrap().push(ThermalAlert::FanStall {
                            channel_id: id.clone(),
                            domain,
                            commanded_pwm: cmd_pwm,
                            measured_rpm: measured,
                        });
                    }
                }
            } else {
                self.stall_since_ms.remove(id);
            }
        }

        let mut updates = 0usize;
        let ids: Vec<String> = self.controllers.keys().cloned().collect();
        for id in ids {
            let domain = channel_infos.get(&id).map(|(d, _)| *d);
            let is_stalled_self = self
                .stall_since_ms
                .get(&id)
                .is_some_and(|&s| now_ms.saturating_sub(s) >= FAN_STALL_DURATION_MS);
            let domain_emergency =
                domain.is_some_and(|d| stalled_domains.contains(&d)) && !is_stalled_self;

            let mut readings = Vec::new();
            let mut fault_reason: Option<String> = None;
            {
                let ctrl = &self.controllers[&id];
                let b = self.backend.lock().unwrap();
                for (kind, weight) in &ctrl.config.sensors {
                    match b.read_sensor(*kind) {
                        None => {
                            fault_reason = Some(format!("unreadable sensor {kind:?}"));
                            break;
                        }
                        Some(r) if !is_valid_temp_c(r.temp_c) => {
                            fault_reason = Some(format!("invalid temperature {} C", r.temp_c));
                            break;
                        }
                        Some(r) if now_ms.saturating_sub(r.updated_at_ms) >= SENSOR_STALE_MS => {
                            fault_reason = Some(format!(
                                "stale sensor reading ({} ms old)",
                                now_ms.saturating_sub(r.updated_at_ms)
                            ));
                            break;
                        }
                        Some(r) => readings.push((r.temp_c, *weight)),
                    }
                }
            }

            if let Some(reason) = fault_reason {
                let since = *self.fault_since_ms.entry(id.clone()).or_insert(now_ms);
                if now_ms.saturating_sub(since) >= SENSOR_FAULT_HANDOFF_MS {
                    if self.handed_to_bios_on_fault.insert(id.clone()) {
                        let _ = self.backend.lock().unwrap().restore_auto(&id);
                        self.touched_channels.lock().unwrap().remove(&id);
                        self.last_commanded_pwm.remove(&id);
                        self.alerts.lock().unwrap().push(ThermalAlert::SensorFault {
                            channel_id: id.clone(),
                            reason,
                            handed_to_bios: true,
                        });
                    }
                    continue;
                }
                let fault_write = self.backend.lock().unwrap().set_pwm_pct(&id, 100);
                if let Err(err) = fault_write {
                    self.restore_all_to_bios();
                    return Err(err);
                }
                self.touched_channels.lock().unwrap().insert(id.clone());
                self.last_commanded_pwm.insert(id.clone(), 100);
                self.restored = false;
                updates += 1;
                continue;
            }

            self.fault_since_ms.remove(&id);
            self.handed_to_bios_on_fault.remove(&id);

            let Some(mixed_temp) =
                mix_sensor_temps(self.controllers[&id].config.mix_mode, &readings)
            else {
                continue;
            };
            let pwm = if domain_emergency {
                100
            } else {
                self.controllers.get_mut(&id).unwrap().step(mixed_temp)
            };
            let write_res = self.backend.lock().unwrap().set_pwm_pct(&id, pwm);
            match write_res {
                Ok(()) => {
                    self.touched_channels.lock().unwrap().insert(id.clone());
                    self.last_commanded_pwm.insert(id, pwm);
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
        let touched: Vec<String> = {
            let mut set = self.touched_channels.lock().unwrap();
            set.drain().collect()
        };
        if self.restored && touched.is_empty() {
            return;
        }
        if let Ok(mut b) = self.backend.lock() {
            for id in touched {
                let _ = b.restore_auto(&id);
            }
        }
        self.last_commanded_pwm.clear();
        self.restored = true;
    }
}

impl<B: FanBackend + 'static> Drop for ThermalDaemon<B> {
    fn drop(&mut self) {
        if let Some(wd) = &self.watchdog_state {
            wd.stop.store(true, Ordering::Release);
        }
        if let Some(handle) = self.watchdog_thread.take() {
            let _ = handle.join();
        }
        self.restore_all_to_bios();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use backend::{FanChannelInfo, MockFanBackend, SensorKind};
    use curve::{CurvePoint, MixMode};

    fn make_channels() -> Vec<FanChannelInfo> {
        vec![
            FanChannelInfo {
                id: "gpu:0".into(),
                name: "Mock RTX 3070 Fan 0".into(),
                domain: FanDomain::GpuNvidia,
                supports_zero_rpm: true,
                is_cpu_domain: false,
                calibrated_min_rpm: 600,
            },
            FanChannelInfo {
                id: "gpu:1".into(),
                name: "Mock RTX 3070 Fan 1".into(),
                domain: FanDomain::GpuNvidia,
                supports_zero_rpm: true,
                is_cpu_domain: false,
                calibrated_min_rpm: 600,
            },
            FanChannelInfo {
                id: "case:intake".into(),
                name: "Mock Case Intake".into(),
                domain: FanDomain::UsbAio,
                supports_zero_rpm: false,
                is_cpu_domain: true,
                calibrated_min_rpm: 500,
            },
        ]
    }

    #[test]
    fn sensor_faults_go_to_100_immediately_and_handoff_to_bios_after_10s() {
        let bad_cases: [Option<(f32, u64)>; 7] = [
            Some((f32::NAN, 1000)),
            Some((f32::INFINITY, 1000)),
            Some((f32::NEG_INFINITY, 1000)),
            Some((-25.0, 1000)),
            Some((160.0, 1000)),
            Some((50.0, 0)),
            None,
        ];

        for (idx, case) in bad_cases.into_iter().enumerate() {
            let mock = MockFanBackend::new(make_channels(), false);
            let state = mock.state_handle();
            let mut daemon = ThermalDaemon::new(mock);
            daemon.bind_curve("gpu:0", FanCurveConfig::default());

            {
                let mut st = state.lock().unwrap();
                st.sensors.clear();
                if let Some((temp, ts)) = case {
                    let updated_at = if idx == 5 { ts } else { 4_000 };
                    st.set_sensor(SensorKind::GpuCore, temp, updated_at);
                }
            }
            daemon.tick_at(4_000).unwrap();
            assert_eq!(
                state.lock().unwrap().current_pwm.get("gpu:0").copied(),
                Some(100),
                "case {idx} must jump to 100% immediately"
            );

            if let Some((temp, _)) = case
                && idx != 5
            {
                state
                    .lock()
                    .unwrap()
                    .set_sensor(SensorKind::GpuCore, temp, 14_000);
            }
            daemon.tick_at(14_000).unwrap();
            assert!(
                state
                    .lock()
                    .unwrap()
                    .restored_channels
                    .iter()
                    .any(|c| c == "gpu:0"),
                "case {idx} must hand back to BIOS after 10s continuous fault"
            );
        }
    }

    #[test]
    fn fan_stall_raises_alert_and_pushes_sibling_domain_fans_to_100() {
        let mock = MockFanBackend::new(make_channels(), false);
        let state = mock.state_handle();
        let mut daemon = ThermalDaemon::new(mock);
        daemon.bind_curve("gpu:0", FanCurveConfig::default());
        daemon.bind_curve("gpu:1", FanCurveConfig::default());

        {
            let mut st = state.lock().unwrap();
            st.set_sensor(SensorKind::GpuCore, 60.0, 1_000);
            st.rpm_overrides.insert("gpu:0".into(), 0);
            st.rpm_overrides.insert("gpu:1".into(), 1200);
        }
        daemon.tick_at(1_000).unwrap();
        state
            .lock()
            .unwrap()
            .set_sensor(SensorKind::GpuCore, 60.0, 2_000);
        daemon.tick_at(2_000).unwrap();
        state
            .lock()
            .unwrap()
            .set_sensor(SensorKind::GpuCore, 60.0, 7_500);
        daemon.tick_at(7_500).unwrap();

        assert!(
            daemon.alerts().iter().any(|a| matches!(
                a,
                ThermalAlert::FanStall { channel_id, domain: FanDomain::GpuNvidia, .. } if channel_id == "gpu:0"
            )),
            "stall alert must be raised"
        );
        assert_eq!(state.lock().unwrap().current_pwm["gpu:1"], 100);
    }

    #[test]
    fn zero_rpm_mode_does_not_trigger_fan_stall_alert() {
        let mock = MockFanBackend::new(make_channels(), false);
        let state = mock.state_handle();
        let mut daemon = ThermalDaemon::new(mock);
        daemon.bind_curve(
            "gpu:0",
            FanCurveConfig {
                points: vec![
                    CurvePoint {
                        temp_c: 30.0,
                        pwm_pct: 0,
                    },
                    CurvePoint {
                        temp_c: 50.0,
                        pwm_pct: 0,
                    },
                    CurvePoint {
                        temp_c: 85.0,
                        pwm_pct: 100,
                    },
                ],
                ..Default::default()
            },
        );
        {
            let mut st = state.lock().unwrap();
            st.set_sensor(SensorKind::GpuCore, 35.0, 1_000);
            st.rpm_overrides.insert("gpu:0".into(), 0);
        }
        daemon.tick_at(1_000).unwrap();
        state
            .lock()
            .unwrap()
            .set_sensor(SensorKind::GpuCore, 35.0, 7_000);
        daemon.tick_at(7_000).unwrap();
        assert!(daemon.alerts().is_empty());
    }

    #[test]
    fn watchdog_restores_bios_control_when_controller_stalls_3_ticks() {
        let mock = MockFanBackend::new(make_channels(), false);
        let state = mock.state_handle();
        state
            .lock()
            .unwrap()
            .set_sensor(SensorKind::GpuCore, 62.0, 1_000);

        let mut daemon = ThermalDaemon::new(mock);
        daemon.bind_curve("gpu:0", FanCurveConfig::default());
        daemon.start_watchdog(Duration::from_millis(20));
        daemon.tick_at(1_000).unwrap();

        std::thread::sleep(Duration::from_millis(120));

        assert!(
            state
                .lock()
                .unwrap()
                .restored_channels
                .iter()
                .any(|c| c == "gpu:0"),
            "watchdog must hand gpu:0 back to BIOS while daemon is still alive"
        );
        assert!(
            daemon
                .alerts()
                .iter()
                .any(|a| matches!(a, ThermalAlert::WatchdogBiosHandoff { .. }))
        );
    }

    #[test]
    fn drop_and_panic_always_restore_bios_auto_control() {
        let mock = MockFanBackend::new(make_channels(), false);
        let state = mock.state_handle();
        state
            .lock()
            .unwrap()
            .set_sensor(SensorKind::GpuCore, 68.0, 1_000);

        let res = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let mut daemon = ThermalDaemon::new(mock);
            daemon.bind_curve("gpu:0", FanCurveConfig::default());
            daemon.tick_at(1_000).unwrap();
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
            .set_sensor(SensorKind::GpuCore, 72.0, 1_000);
        let mut daemon = ThermalDaemon::new(mock);
        daemon.bind_curve("gpu:0", FanCurveConfig::default());
        daemon.tick_at(1_000).unwrap();

        state.lock().unwrap().driver_reset_active = true;
        assert!(daemon.tick_at(2_000).is_err());
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
            .set_sensor(SensorKind::GpuCore, 78.0, 1_000);
        let mut daemon = ThermalDaemon::new(mock);
        daemon.bind_curve("gpu:0", FanCurveConfig::default());
        assert_eq!(daemon.tick_at(1_000).unwrap(), 0);
        assert!(state.lock().unwrap().pwm_writes.is_empty());
    }

    #[test]
    fn mixed_max_sensor_and_hard_floor_work_end_to_end() {
        let mock = MockFanBackend::new(make_channels(), false);
        let state = mock.state_handle();
        {
            let mut st = state.lock().unwrap();
            st.set_sensor(SensorKind::CpuTctl, 50.0, 1_000);
            st.set_sensor(SensorKind::GpuHotspot, 86.0, 1_000);
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
        daemon.tick_at(1_000).unwrap();
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
