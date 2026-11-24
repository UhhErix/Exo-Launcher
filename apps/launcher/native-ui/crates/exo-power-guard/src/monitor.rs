use std::collections::VecDeque;
use std::sync::Arc;
use std::sync::Mutex;

use crate::guard::{GuardEvaluation, GuardPolicy, PowerGuard};
use crate::recorder::SessionRecorder;
use crate::sidecar::SidecarClient;
use crate::types::GpuSnapshot;

#[derive(Debug, Clone)]
pub struct SessionPoint {
    pub timestamp_ms: u64,
    pub power_w: f64,
    pub power_pct: f64,
    pub core_temp_c: f64,
    pub hotspot_temp_c: Option<f64>,
    pub max_pin_a: Option<f64>,
}

#[derive(Debug, Clone)]
pub struct LiveGuardState {
    pub snapshot: Option<GpuSnapshot>,
    pub evaluation: Option<GuardEvaluation>,
    pub sample_count: usize,
    pub history: VecDeque<SessionPoint>,
    pub policy: GuardPolicy,
    pub sidecar_running: bool,
    pub last_action_note: Option<String>,
}

impl Default for LiveGuardState {
    fn default() -> Self {
        Self {
            snapshot: None,
            evaluation: None,
            sample_count: 0,
            history: VecDeque::with_capacity(64),
            policy: GuardPolicy::default(),
            sidecar_running: false,
            last_action_note: None,
        }
    }
}

pub struct PowerGuardController {
    sidecar: Option<SidecarClient>,
    guard: PowerGuard,
    recorder: Arc<Mutex<SessionRecorder>>,
    last_ts: u64,
}

impl Default for PowerGuardController {
    fn default() -> Self {
        Self::new(GuardPolicy::default())
    }
}

impl PowerGuardController {
    pub fn new(policy: GuardPolicy) -> Self {
        Self {
            sidecar: None,
            guard: PowerGuard::new(policy),
            recorder: Arc::new(Mutex::new(SessionRecorder::new(3_600))),
            last_ts: 0,
        }
    }

    pub fn set_policy(&mut self, policy: GuardPolicy) {
        self.guard.set_policy(policy);
    }

    pub fn is_running(&self) -> bool {
        self.sidecar.is_some()
    }

    pub fn ensure_started(&mut self, interval_ms: u64) -> bool {
        if self.sidecar.is_some() {
            return true;
        }
        self.sidecar = SidecarClient::spawn_beside_exe(interval_ms);
        self.sidecar.is_some()
    }

    pub fn stop(&mut self) {
        self.sidecar = None;
    }

    pub fn poll_into(&mut self, state: &mut LiveGuardState) -> Option<GuardEvaluation> {
        state.sidecar_running = self.sidecar.is_some();
        let snap = self.sidecar.as_ref().and_then(SidecarClient::latest)?;
        if snap.timestamp_ms == self.last_ts {
            return None;
        }
        self.last_ts = snap.timestamp_ms;
        Some(self.ingest_snapshot(snap, state))
    }

    pub fn ingest_snapshot(
        &mut self,
        snap: GpuSnapshot,
        state: &mut LiveGuardState,
    ) -> GuardEvaluation {
        let eval = self.guard.evaluate(&snap);
        let point = SessionPoint {
            timestamp_ms: snap.timestamp_ms,
            power_w: snap.board_power_w.unwrap_or(0.0),
            power_pct: snap.power_pct.unwrap_or(0.0),
            core_temp_c: snap.core_temp_c.unwrap_or(0.0),
            hotspot_temp_c: snap.hotspot_temp_c,
            max_pin_a: snap.connector.as_ref().map(|c| c.max_pin_current_a()),
        };
        if state.history.len() >= 48 {
            state.history.pop_front();
        }
        state.history.push_back(point);
        if let Ok(mut rec) = self.recorder.lock() {
            rec.push(snap.clone());
            state.sample_count = rec.len();
        }
        state.snapshot = Some(snap);
        state.evaluation = Some(eval.clone());
        eval
    }

    pub fn export_html(&self) -> String {
        self.recorder
            .lock()
            .map(|r| r.export_html())
            .unwrap_or_default()
    }
}
