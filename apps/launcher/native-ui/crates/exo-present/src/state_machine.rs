use crate::metrics::{FrameSample, PresentMode};
use std::collections::HashMap;

pub const DXGI_PROVIDER_FIRST: u32 = 0xCA11_C036;
pub const D3D9_PROVIDER_FIRST: u32 = 0x783A_CA0A;
pub const DXGKRNL_PROVIDER_FIRST: u32 = 0x802E_C45A;

pub const DXGI_PRESENT_START: u16 = 42;
pub const DXGI_PRESENT_STOP: u16 = 43;
pub const DXGI_PRESENT_MPO_START: u16 = 55;
pub const DXGI_PRESENT_MPO_STOP: u16 = 56;

pub const D3D9_PRESENT_START: u16 = 1;
pub const D3D9_PRESENT_STOP: u16 = 2;

pub const DXGKRNL_BLIT: u16 = 166;
pub const DXGKRNL_FLIP: u16 = 168;
pub const DXGKRNL_PRESENT_HISTORY: u16 = 171;
pub const DXGKRNL_PRESENT: u16 = 184;
pub const DXGKRNL_PRESENT_HISTORY_DETAILED: u16 = 215;
pub const DXGKRNL_FLIP_MPO: u16 = 252;
pub const DXGKRNL_VSYNC_DPC: u16 = 17;
pub const DXGKRNL_VSYNC_DPC_MPO: u16 = 273;

pub const DXGI_PRESENT_TEST_FLAG: u32 = 0x1;
pub const KERNEL_FALLBACK_SECS: f64 = 1.5;
pub const PAUSE_RESET_GAP_MS: f64 = 500.0;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PresentEventKind {
    None,
    DxgiStart,
    DxgiStop,
    D3d9Start,
    D3d9Stop,
    KernelFlip,
    KernelDisplay,
}

#[derive(Clone, Copy, Debug)]
pub struct RawPresentEvent {
    pub pid: u32,
    pub tid: u32,
    pub qpc_ticks: i64,
    pub kind: PresentEventKind,
    pub event_id: u16,
    pub present_flags: u32,
    pub swapchain_ptr: u64,
}

pub fn classify_event(provider_first: u32, event_id: u16) -> PresentEventKind {
    match (provider_first, event_id) {
        (DXGI_PROVIDER_FIRST, DXGI_PRESENT_START | DXGI_PRESENT_MPO_START) => {
            PresentEventKind::DxgiStart
        }
        (DXGI_PROVIDER_FIRST, DXGI_PRESENT_STOP | DXGI_PRESENT_MPO_STOP) => {
            PresentEventKind::DxgiStop
        }
        (D3D9_PROVIDER_FIRST, D3D9_PRESENT_START) => PresentEventKind::D3d9Start,
        (D3D9_PROVIDER_FIRST, D3D9_PRESENT_STOP) => PresentEventKind::D3d9Stop,
        (
            DXGKRNL_PROVIDER_FIRST,
            DXGKRNL_FLIP | DXGKRNL_BLIT | DXGKRNL_PRESENT | DXGKRNL_FLIP_MPO,
        ) => PresentEventKind::KernelFlip,
        (
            DXGKRNL_PROVIDER_FIRST,
            DXGKRNL_VSYNC_DPC
            | DXGKRNL_VSYNC_DPC_MPO
            | DXGKRNL_PRESENT_HISTORY
            | DXGKRNL_PRESENT_HISTORY_DETAILED,
        ) => PresentEventKind::KernelDisplay,
        _ => PresentEventKind::None,
    }
}

pub fn is_present_test(present_flags: u32) -> bool {
    (present_flags & DXGI_PRESENT_TEST_FLAG) != 0
}

pub fn parse_dxgi_present_fields(is_32bit: bool, data: &[u8]) -> (u64, u32) {
    if is_32bit {
        if data.len() < 8 {
            return (0, 0);
        }
        let swapchain = u32::from_le_bytes([data[0], data[1], data[2], data[3]]) as u64;
        let flags = u32::from_le_bytes([data[4], data[5], data[6], data[7]]);
        (swapchain, flags)
    } else {
        if data.len() < 12 {
            return (0, 0);
        }
        let swapchain = u64::from_le_bytes([
            data[0], data[1], data[2], data[3], data[4], data[5], data[6], data[7],
        ]);
        let flags = u32::from_le_bytes([data[8], data[9], data[10], data[11]]);
        (swapchain, flags)
    }
}

pub fn wants_kernel_fallback(api_seen_recently: bool, seconds_in_front: f64) -> bool {
    !api_seen_recently && seconds_in_front >= KERNEL_FALLBACK_SECS
}

#[derive(Clone, Debug, Default)]
struct PidTrack {
    last_api_qpc: Option<i64>,
    last_kernel_qpc: Option<i64>,
    in_flight_start_qpc: Option<i64>,
    in_flight_tid: u32,
    last_cpu_present_ms: f64,
    last_completed_start_qpc: Option<i64>,
    last_display_qpc: Option<i64>,
    last_display_latency_ms: Option<f64>,
    last_gpu_busy_ms: Option<f64>,
    pending_displays: u32,
    saw_mpo_api: bool,
    present_mode: PresentMode,
}

#[derive(Clone, Debug)]
pub struct PresentStateMachine {
    qpc_freq: f64,
    watched_pids: Vec<u32>,
    tracks: HashMap<u32, PidTrack>,
    last_any_api_qpc: Option<i64>,
}

impl PresentStateMachine {
    pub fn new(qpc_freq: i64) -> Self {
        Self {
            qpc_freq: (qpc_freq.max(1)) as f64,
            watched_pids: Vec::new(),
            tracks: HashMap::new(),
            last_any_api_qpc: None,
        }
    }

    pub fn watch(&mut self, pids: &[u32]) {
        self.watched_pids.clear();
        self.watched_pids.extend_from_slice(pids);
        if !pids.is_empty() {
            self.tracks.retain(|pid, _| pids.contains(pid));
        }
    }

    pub fn forget(&mut self) {
        self.tracks.clear();
        self.last_any_api_qpc = None;
    }

    pub fn api_seen_recently(&self, now_qpc: i64) -> bool {
        let Some(last) = self.last_any_api_qpc else {
            return false;
        };
        let elapsed_s = (now_qpc - last).max(0) as f64 / self.qpc_freq;
        elapsed_s < KERNEL_FALLBACK_SECS
    }

    pub fn ingest(&mut self, ev: RawPresentEvent) -> Option<FrameSample> {
        if !self.watched_pids.is_empty() && !self.watched_pids.contains(&ev.pid) {
            return None;
        }
        match ev.kind {
            PresentEventKind::None => None,
            PresentEventKind::DxgiStart | PresentEventKind::D3d9Start => {
                if ev.kind == PresentEventKind::DxgiStart && is_present_test(ev.present_flags) {
                    return None;
                }
                self.last_any_api_qpc = Some(ev.qpc_ticks);
                let qpc_freq = self.qpc_freq;
                let track = self.tracks.entry(ev.pid).or_default();
                if ev.kind == PresentEventKind::DxgiStart {
                    track.saw_mpo_api = ev.event_id == DXGI_PRESENT_MPO_START;
                }
                let prev = track.last_api_qpc.replace(ev.qpc_ticks);
                track.in_flight_start_qpc = Some(ev.qpc_ticks);
                track.in_flight_tid = ev.tid;
                track.pending_displays = track.pending_displays.saturating_add(1);

                let prev_qpc = prev?;
                let dt_ticks = ev.qpc_ticks - prev_qpc;
                if dt_ticks <= 0 {
                    return None;
                }
                let dt_ms = (dt_ticks as f64 * 1000.0) / qpc_freq;
                if dt_ms > PAUSE_RESET_GAP_MS {
                    track.pending_displays = 1;
                    return None;
                }
                let dropped = track.pending_displays > 3;
                if dropped {
                    track.pending_displays = 2;
                }
                Some(FrameSample {
                    pid: ev.pid,
                    qpc_ticks: ev.qpc_ticks,
                    frame_time_ms: dt_ms,
                    cpu_present_ms: track.last_cpu_present_ms,
                    gpu_busy_ms: track.last_gpu_busy_ms,
                    display_latency_ms: track.last_display_latency_ms,
                    dropped,
                    kernel_source: false,
                    present_mode: track.present_mode,
                })
            }
            PresentEventKind::DxgiStop | PresentEventKind::D3d9Stop => {
                let qpc_freq = self.qpc_freq;
                let track = self.tracks.get_mut(&ev.pid)?;
                if let Some(start) = track.in_flight_start_qpc
                    && (track.in_flight_tid == 0 || track.in_flight_tid == ev.tid)
                {
                    let dur_ticks = ev.qpc_ticks - start;
                    if dur_ticks >= 0 {
                        let ms = (dur_ticks as f64 * 1000.0) / qpc_freq;
                        if ms < 200.0 {
                            track.last_cpu_present_ms = ms;
                        }
                    }
                    track.last_completed_start_qpc = Some(start);
                    track.in_flight_start_qpc = None;
                }
                None
            }
            PresentEventKind::KernelFlip => {
                let qpc_freq = self.qpc_freq;
                let track = self.tracks.entry(ev.pid).or_default();
                match ev.event_id {
                    DXGKRNL_BLIT => track.present_mode = PresentMode::ComposedCopyGpuGdi,
                    DXGKRNL_FLIP => track.present_mode = PresentMode::HardwareLegacyFlip,
                    DXGKRNL_FLIP_MPO => {
                        track.present_mode = if track.saw_mpo_api {
                            PresentMode::HardwareComposedIndependentFlip
                        } else {
                            PresentMode::HardwareIndependentFlip
                        };
                    }
                    _ => {}
                }
                if let Some(last_api) = track.last_api_qpc {
                    let api_age_s = (ev.qpc_ticks - last_api).max(0) as f64 / qpc_freq;
                    if api_age_s < KERNEL_FALLBACK_SECS {
                        let lat_ticks = ev.qpc_ticks - last_api;
                        if lat_ticks >= 0 {
                            let lat_ms = (lat_ticks as f64 * 1000.0) / qpc_freq;
                            if lat_ms < 250.0 {
                                track.last_display_latency_ms = Some(lat_ms);
                            }
                        }
                        if let Some(prev_disp) = track.last_display_qpc {
                            let gpu_ticks = ev.qpc_ticks - prev_disp;
                            if gpu_ticks > 0 {
                                let busy = ((gpu_ticks as f64 * 1000.0) / qpc_freq).min(100.0);
                                track.last_gpu_busy_ms = Some(busy);
                            }
                        }
                        track.last_display_qpc = Some(ev.qpc_ticks);
                        track.pending_displays = track.pending_displays.saturating_sub(1);
                        return None;
                    }
                }
                let prev = track.last_kernel_qpc.replace(ev.qpc_ticks)?;
                let dt_ticks = ev.qpc_ticks - prev;
                if dt_ticks <= 0 {
                    return None;
                }
                let dt_ms = (dt_ticks as f64 * 1000.0) / qpc_freq;
                if dt_ms > PAUSE_RESET_GAP_MS {
                    return None;
                }
                Some(FrameSample {
                    pid: ev.pid,
                    qpc_ticks: ev.qpc_ticks,
                    frame_time_ms: dt_ms,
                    cpu_present_ms: 0.0,
                    gpu_busy_ms: Some(dt_ms),
                    display_latency_ms: None,
                    dropped: false,
                    kernel_source: true,
                    present_mode: track.present_mode,
                })
            }
            PresentEventKind::KernelDisplay => {
                let qpc_freq = self.qpc_freq;
                let track = self.tracks.get_mut(&ev.pid)?;
                match ev.event_id {
                    DXGKRNL_VSYNC_DPC_MPO => {
                        track.present_mode = if track.saw_mpo_api {
                            PresentMode::HardwareComposedIndependentFlip
                        } else if track.present_mode == PresentMode::Unknown {
                            PresentMode::HardwareIndependentFlip
                        } else {
                            track.present_mode
                        };
                    }
                    DXGKRNL_PRESENT_HISTORY | DXGKRNL_PRESENT_HISTORY_DETAILED
                        if track.present_mode == PresentMode::Unknown
                            || track.present_mode == PresentMode::ComposedFlip =>
                    {
                        track.present_mode = PresentMode::ComposedFlip;
                    }
                    _ => {}
                }
                if let Some(start) = track.last_completed_start_qpc.or(track.last_api_qpc) {
                    let lat_ticks = ev.qpc_ticks - start;
                    if lat_ticks >= 0 {
                        let lat_ms = (lat_ticks as f64 * 1000.0) / qpc_freq;
                        if lat_ms < 250.0 {
                            track.last_display_latency_ms = Some(lat_ms);
                        }
                    }
                }
                track.last_display_qpc = Some(ev.qpc_ticks);
                track.pending_displays = track.pending_displays.saturating_sub(1);
                None
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn filters_present_test_and_tracks_dxgi_frametimes() {
        let freq = 10_000_000i64;
        let mut sm = PresentStateMachine::new(freq);
        sm.watch(&[42]);
        let step = freq / 100;

        assert!(
            sm.ingest(RawPresentEvent {
                pid: 42,
                tid: 1,
                qpc_ticks: step,
                kind: PresentEventKind::DxgiStart,
                event_id: DXGI_PRESENT_START,
                present_flags: DXGI_PRESENT_TEST_FLAG,
                swapchain_ptr: 0x1000,
            })
            .is_none()
        );
        assert!(
            sm.ingest(RawPresentEvent {
                pid: 42,
                tid: 1,
                qpc_ticks: step * 2,
                kind: PresentEventKind::DxgiStart,
                event_id: DXGI_PRESENT_START,
                present_flags: 0,
                swapchain_ptr: 0x1000,
            })
            .is_none()
        );
        sm.ingest(RawPresentEvent {
            pid: 42,
            tid: 1,
            qpc_ticks: step * 2 + freq / 2000,
            kind: PresentEventKind::DxgiStop,
            event_id: DXGI_PRESENT_STOP,
            present_flags: 0,
            swapchain_ptr: 0x1000,
        });
        let s = sm
            .ingest(RawPresentEvent {
                pid: 42,
                tid: 1,
                qpc_ticks: step * 3,
                kind: PresentEventKind::DxgiStart,
                event_id: DXGI_PRESENT_START,
                present_flags: 0,
                swapchain_ptr: 0x1000,
            })
            .expect("second present yields frame sample");
        assert!((s.frame_time_ms - 10.0).abs() < 0.01);
        assert!((s.cpu_present_ms - 0.5).abs() < 0.01);
        assert!(!s.kernel_source);
    }

    #[test]
    fn kernel_flip_feeds_vulkan_and_ignores_when_dxgi_active() {
        let freq = 10_000_000i64;
        let step = freq / 60;
        let mut sm = PresentStateMachine::new(freq);
        sm.watch(&[7]);

        assert!(
            sm.ingest(RawPresentEvent {
                pid: 7,
                tid: 1,
                qpc_ticks: step,
                kind: PresentEventKind::KernelFlip,
                event_id: DXGKRNL_FLIP,
                present_flags: 0,
                swapchain_ptr: 0,
            })
            .is_none()
        );
        let vk = sm
            .ingest(RawPresentEvent {
                pid: 7,
                tid: 1,
                qpc_ticks: step * 2,
                kind: PresentEventKind::KernelFlip,
                event_id: DXGKRNL_FLIP,
                present_flags: 0,
                swapchain_ptr: 0,
            })
            .expect("vulkan kernel flip emits frame");
        assert!(vk.kernel_source);
        assert_eq!(vk.present_mode, PresentMode::HardwareLegacyFlip);
        assert!((vk.frame_time_ms - (1000.0 / 60.0)).abs() < 0.05);

        sm.ingest(RawPresentEvent {
            pid: 7,
            tid: 1,
            qpc_ticks: step * 3,
            kind: PresentEventKind::DxgiStart,
            event_id: DXGI_PRESENT_START,
            present_flags: 0,
            swapchain_ptr: 0x2000,
        });
        assert!(
            sm.ingest(RawPresentEvent {
                pid: 7,
                tid: 1,
                qpc_ticks: step * 3 + freq / 500,
                kind: PresentEventKind::KernelFlip,
                event_id: DXGKRNL_FLIP_MPO,
                present_flags: 0,
                swapchain_ptr: 0,
            })
            .is_none()
        );
    }

    #[test]
    fn alt_tab_pause_gap_does_not_pollute_frametimes() {
        let freq = 10_000_000i64;
        let mut sm = PresentStateMachine::new(freq);
        sm.watch(&[9]);
        sm.ingest(RawPresentEvent {
            pid: 9,
            tid: 1,
            qpc_ticks: freq,
            kind: PresentEventKind::DxgiStart,
            event_id: DXGI_PRESENT_START,
            present_flags: 0,
            swapchain_ptr: 1,
        });
        assert!(
            sm.ingest(RawPresentEvent {
                pid: 9,
                tid: 1,
                qpc_ticks: freq * 3,
                kind: PresentEventKind::DxgiStart,
                event_id: DXGI_PRESENT_START,
                present_flags: 0,
                swapchain_ptr: 1,
            })
            .is_none()
        );
    }

    #[test]
    fn parses_dxgi_flags_32_and_64_bit() {
        let mut buf64 = [0u8; 12];
        buf64[0..8].copy_from_slice(&0x1234_5678_9ABC_DEF0u64.to_le_bytes());
        buf64[8..12].copy_from_slice(&DXGI_PRESENT_TEST_FLAG.to_le_bytes());
        assert_eq!(
            parse_dxgi_present_fields(false, &buf64),
            (0x1234_5678_9ABC_DEF0, DXGI_PRESENT_TEST_FLAG)
        );
        let mut buf32 = [0u8; 8];
        buf32[0..4].copy_from_slice(&0x1234_5678u32.to_le_bytes());
        buf32[4..8].copy_from_slice(&0u32.to_le_bytes());
        assert_eq!(parse_dxgi_present_fields(true, &buf32), (0x1234_5678, 0));
    }
}
