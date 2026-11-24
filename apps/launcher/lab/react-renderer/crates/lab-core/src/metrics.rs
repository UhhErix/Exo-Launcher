// SPDX-License-Identifier: MIT

use serde::Serialize;

#[derive(Clone, Debug, Default, Serialize)]
pub struct MemorySample {
    pub working_set_mb: f64,
    pub private_bytes_mb: f64,
    pub peak_working_set_mb: f64,
}

#[derive(Clone, Debug, Default, Serialize)]
pub struct BenchReport {
    pub engine: String,
    pub bundle: String,
    pub mode: String,
    pub pipe_connected: bool,
    pub js_init_ms: f64,
    pub bundle_eval_ms: f64,
    pub css_parse_ms: f64,
    pub first_cascade_ms: f64,
    pub first_layout_ms: f64,
    pub cold_first_frame_ms: f64,
    pub dom_nodes: usize,
    pub commit_count: u64,
    pub total_ops: u64,
    pub css_rules: usize,
    pub css_decls: usize,
    pub css_keyframes_skipped: usize,
    pub input_latency_ms: Vec<f64>,
    pub input_latency_p50_ms: f64,
    pub frame_times_ms: Vec<f64>,
    pub frame_p50_ms: f64,
    pub frame_p95_ms: f64,
    pub memory: MemorySample,
    pub child_processes: usize,
    pub rpc_calls_sent: u64,
    pub rpc_events_received: u64,
}

pub fn percentile(samples: &[f64], p: f64) -> f64 {
    if samples.is_empty() {
        return 0.0;
    }
    let mut sorted = samples.to_vec();
    sorted.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let idx = ((sorted.len() - 1) as f64 * p).round() as usize;
    sorted[idx.min(sorted.len() - 1)]
}

#[cfg(windows)]
pub fn sample_process_memory() -> MemorySample {
    use windows_sys::Win32::System::ProcessStatus::{
        K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX,
    };
    use windows_sys::Win32::System::Threading::GetCurrentProcess;

    let mut counters: PROCESS_MEMORY_COUNTERS_EX = unsafe { std::mem::zeroed() };
    counters.cb = std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32;
    let ok = unsafe {
        K32GetProcessMemoryInfo(
            GetCurrentProcess(),
            (&mut counters as *mut PROCESS_MEMORY_COUNTERS_EX).cast(),
            counters.cb,
        )
    };
    if ok == 0 {
        return MemorySample::default();
    }
    const MB: f64 = 1024.0 * 1024.0;
    MemorySample {
        working_set_mb: counters.WorkingSetSize as f64 / MB,
        private_bytes_mb: counters.PrivateUsage as f64 / MB,
        peak_working_set_mb: counters.PeakWorkingSetSize as f64 / MB,
    }
}

#[cfg(not(windows))]
pub fn sample_process_memory() -> MemorySample {
    MemorySample::default()
}
