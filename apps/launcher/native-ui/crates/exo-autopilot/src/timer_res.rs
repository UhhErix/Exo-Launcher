use crate::os::AutopilotOs;
use serde::{Deserialize, Serialize};

pub const TARGET_TIMER_100NS: u32 = 5070;

#[derive(Debug, Clone, Copy, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TimerResolutionState {
    pub active: bool,
    pub requested_100ns: u32,
    pub actual_100ns: Option<u32>,
}

#[derive(Debug, Default)]
pub struct TimerResolutionGuard {
    active: bool,
    requested_100ns: u32,
    actual_100ns: Option<u32>,
}

impl TimerResolutionGuard {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn state(&self) -> TimerResolutionState {
        TimerResolutionState {
            active: self.active,
            requested_100ns: self.requested_100ns,
            actual_100ns: self.actual_100ns,
        }
    }

    pub fn acquire(&mut self, os: &dyn AutopilotOs) -> bool {
        if self.active {
            return true;
        }
        if let Some(actual) = os.set_timer_resolution(TARGET_TIMER_100NS, true) {
            self.active = true;
            self.requested_100ns = TARGET_TIMER_100NS;
            self.actual_100ns = Some(actual);
            true
        } else {
            false
        }
    }

    pub fn release(&mut self, os: &dyn AutopilotOs) -> bool {
        if !self.active {
            return false;
        }
        let res = os.set_timer_resolution(self.requested_100ns, false);
        self.active = false;
        self.actual_100ns = res;
        true
    }
}
