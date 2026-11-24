use crate::synth::FlowProfile;
use serde::{Deserialize, Serialize};
use std::mem::size_of;
use windows::Win32::Foundation::FILETIME;
use windows::Win32::System::ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX};
use windows::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes};

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct FlowTelemetry {
    pub active: bool,
    pub backend: String,
    pub compute_path: String,
    pub capture_ms: f64,
    pub pyramid_ms: f64,
    pub flow_ms: f64,
    pub hud_mask_ms: f64,
    pub synth_ms: f64,
    pub total_gpu_ms: f64,
    pub real_fps: f64,
    pub output_fps: f64,
    pub p99_jitter_ms: f64,
    pub vram_mb: f64,
    pub working_set_mb: f64,
    pub private_bytes_mb: f64,
    pub cpu_pct: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "method", rename_all = "snake_case")]
pub enum PipeCommand {
    #[serde(rename = "framegen.flow.start")]
    Start { hwnd: u64, profile: FlowProfile },
    #[serde(rename = "framegen.flow.stop")]
    Stop,
    #[serde(rename = "framegen.flow.status")]
    Status,
    #[serde(rename = "framegen.flow.setProfile")]
    SetProfile { profile: FlowProfile },
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PipeResponse {
    pub ok: bool,
    pub message: Option<String>,
    pub telemetry: FlowTelemetry,
}

pub fn current_process_memory_mb() -> (f64, f64) {
    unsafe {
        let mut c = PROCESS_MEMORY_COUNTERS_EX {
            cb: size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
            ..Default::default()
        };
        if GetProcessMemoryInfo(
            GetCurrentProcess(),
            (&mut c as *mut PROCESS_MEMORY_COUNTERS_EX).cast(),
            c.cb,
        )
        .is_ok()
        {
            let ws = round2(c.WorkingSetSize as f64 / (1024.0 * 1024.0));
            let priv_b = round2(c.PrivateUsage as f64 / (1024.0 * 1024.0));
            return (ws, priv_b);
        }
    }
    (0.0, 0.0)
}

pub fn current_process_cpu_ticks_100ns() -> u64 {
    unsafe {
        let mut create = FILETIME::default();
        let mut exit = FILETIME::default();
        let mut kernel = FILETIME::default();
        let mut user = FILETIME::default();
        if GetProcessTimes(
            GetCurrentProcess(),
            &mut create,
            &mut exit,
            &mut kernel,
            &mut user,
        )
        .is_ok()
        {
            let k = ((kernel.dwHighDateTime as u64) << 32) | (kernel.dwLowDateTime as u64);
            let u = ((user.dwHighDateTime as u64) << 32) | (user.dwLowDateTime as u64);
            return k.saturating_add(u);
        }
    }
    0
}

pub fn round2(v: f64) -> f64 {
    (v * 100.0).round() / 100.0
}

pub fn round3(v: f64) -> f64 {
    (v * 1000.0).round() / 1000.0
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::synth::MultiplierMode;

    #[test]
    fn pipe_protocol_round_trips_all_flow_commands() {
        let start = PipeCommand::Start {
            hwnd: 0x1234,
            profile: FlowProfile {
                multiplier: MultiplierMode::VrrAdaptive,
                target_hz: 144,
                ..Default::default()
            },
        };
        let wire = serde_json::to_string(&start).unwrap();
        assert!(wire.contains("framegen.flow.start"));
        let parsed: PipeCommand = serde_json::from_str(&wire).unwrap();
        match parsed {
            PipeCommand::Start { hwnd, profile } => {
                assert_eq!(hwnd, 0x1234);
                assert_eq!(profile.target_hz, 144);
            }
            _ => panic!("expected Start"),
        }

        let stop: PipeCommand = serde_json::from_str(r#"{"method":"framegen.flow.stop"}"#).unwrap();
        assert!(matches!(stop, PipeCommand::Stop));
        let status: PipeCommand =
            serde_json::from_str(r#"{"method":"framegen.flow.status"}"#).unwrap();
        assert!(matches!(status, PipeCommand::Status));
    }

    #[test]
    fn process_memory_sampler_reads_nonzero_working_set() {
        let (ws, priv_b) = current_process_memory_mb();
        assert!(ws > 0.5 && priv_b > 0.5);
    }
}
