use std::time::Instant;

use crate::backends::TelemetryBackend;
use crate::types::{BackendKind, GpuSnapshot, GpuVendor};
use crate::win32::now_unix_ms;

const STATISTICS_SIZE: usize = 0x328;
const RESULT_AT: usize = 24;
const INPUT_AT: usize = 0x320;
const TYPE_ADAPTER: i32 = 0;
const TYPE_SEGMENT: i32 = 3;
const TYPE_NODE: i32 = 5;
const QUERY_ADAPTER_PERFDATA: i32 = 62;

#[repr(C, packed(4))]
#[derive(Clone, Copy)]
struct AdapterInfo {
    handle: u32,
    luid: i64,
    sources: u32,
    precise_regions: i32,
}

#[repr(C)]
struct EnumAdapters2 {
    count: u32,
    adapters: *mut AdapterInfo,
}

#[repr(C)]
struct QueryAdapterInfo {
    adapter: u32,
    kind: i32,
    data: *mut std::ffi::c_void,
    size: u32,
}

#[repr(C)]
struct AdapterPerfData {
    physical_adapter_index: u32,
    _pad0: u32,
    memory_frequency: u64,
    max_memory_frequency: u64,
    max_memory_frequency_oc: u64,
    memory_bandwidth: u64,
    pcie_bandwidth: u64,
    fan_rpm: u32,
    power_deci_pct: u32,
    temperature_deci_c: u32,
    power_state_override: u8,
    _pad1: [u8; 3],
}

#[link(name = "gdi32")]
unsafe extern "system" {
    fn D3DKMTEnumAdapters2(list: *mut EnumAdapters2) -> i32;
    fn D3DKMTCloseAdapter(handle: *const u32) -> i32;
    fn D3DKMTQueryStatistics(stats: *mut u8) -> i32;
    fn D3DKMTQueryAdapterInfo(query: *mut QueryAdapterInfo) -> i32;
}

pub struct D3dkmtBackend {
    adapter: u32,
    luid: i64,
    segments: u32,
    nodes: u32,
    dedicated_bytes: u64,
    prev_running: Vec<i64>,
    prev_at: Vec<Instant>,
    primed: bool,
}

impl D3dkmtBackend {
    pub fn open() -> Option<Self> {
        let mut infos = [AdapterInfo {
            handle: 0,
            luid: 0,
            sources: 0,
            precise_regions: 0,
        }; 16];
        let mut list = EnumAdapters2 {
            count: infos.len() as u32,
            adapters: infos.as_mut_ptr(),
        };
        if unsafe { D3DKMTEnumAdapters2(&mut list) } != 0 || list.count == 0 {
            return None;
        }

        let mut stats = [0u8; STATISTICS_SIZE];
        let mut best: Option<(u32, i64, u32, u32, u64)> = None;

        for info in infos.iter().take((list.count as usize).min(16)) {
            let mut memory = 0u64;
            let mut segments = 0u32;
            let mut nodes = 0u32;
            if query_stats(&mut stats, TYPE_ADAPTER, info.luid, 0) {
                segments = u32::from_le_bytes(stats[RESULT_AT..RESULT_AT + 4].try_into().unwrap());
                nodes = u32::from_le_bytes(stats[RESULT_AT + 4..RESULT_AT + 8].try_into().unwrap());
                for seg in 0..segments {
                    if query_stats(&mut stats, TYPE_SEGMENT, info.luid, seg) && is_dedicated(&stats)
                    {
                        memory +=
                            u64::from_le_bytes(stats[RESULT_AT..RESULT_AT + 8].try_into().unwrap());
                    }
                }
            }
            if best.is_none_or(|b| memory > b.4) {
                if let Some(old) = best {
                    unsafe {
                        D3DKMTCloseAdapter(&old.0);
                    }
                }
                best = Some((info.handle, info.luid, segments, nodes, memory));
            } else {
                let h = info.handle;
                unsafe {
                    D3DKMTCloseAdapter(&h);
                }
            }
        }

        let (adapter, luid, segments, nodes, dedicated_bytes) = best?;
        if nodes == 0 {
            unsafe {
                D3DKMTCloseAdapter(&adapter);
            }
            return None;
        }
        let now = Instant::now();
        Some(Self {
            adapter,
            luid,
            segments,
            nodes,
            dedicated_bytes,
            prev_running: vec![0; nodes as usize],
            prev_at: vec![now; nodes as usize],
            primed: false,
        })
    }

    fn read_perf_data(&self) -> Option<AdapterPerfData> {
        let mut perf = AdapterPerfData {
            physical_adapter_index: 0,
            _pad0: 0,
            memory_frequency: 0,
            max_memory_frequency: 0,
            max_memory_frequency_oc: 0,
            memory_bandwidth: 0,
            pcie_bandwidth: 0,
            fan_rpm: 0,
            power_deci_pct: 0,
            temperature_deci_c: 0,
            power_state_override: 0,
            _pad1: [0; 3],
        };
        let mut query = QueryAdapterInfo {
            adapter: self.adapter,
            kind: QUERY_ADAPTER_PERFDATA,
            data: (&mut perf as *mut AdapterPerfData).cast(),
            size: std::mem::size_of::<AdapterPerfData>() as u32,
        };
        (unsafe { D3DKMTQueryAdapterInfo(&mut query) } == 0).then_some(perf)
    }

    fn read_memory_used_bytes(&self, stats: &mut [u8; STATISTICS_SIZE]) -> Option<u64> {
        let mut used: Option<u64> = None;
        for seg in 0..self.segments {
            if query_stats(stats, TYPE_SEGMENT, self.luid, seg) && is_dedicated(stats) {
                let cur =
                    u64::from_le_bytes(stats[RESULT_AT + 16..RESULT_AT + 24].try_into().unwrap());
                used = Some(used.unwrap_or(0) + cur);
            }
        }
        used
    }

    fn read_busiest_util_pct(&mut self, stats: &mut [u8; STATISTICS_SIZE]) -> Option<u32> {
        let mut busiest = 0.0_f64;
        let now = Instant::now();
        for node in 0..self.nodes {
            if !query_stats(stats, TYPE_NODE, self.luid, node) {
                continue;
            }
            let running = i64::from_le_bytes(stats[RESULT_AT..RESULT_AT + 8].try_into().unwrap());
            let idx = node as usize;
            let dt = now.duration_since(self.prev_at[idx]).as_secs_f64();
            if self.primed && dt > 0.0 {
                let busy = ((running - self.prev_running[idx]) as f64) / 1e7 / dt;
                if busy > busiest {
                    busiest = busy;
                }
            }
            self.prev_running[idx] = running;
            self.prev_at[idx] = now;
        }
        if !self.primed {
            self.primed = true;
            return None;
        }
        Some((busiest.clamp(0.0, 1.0) * 100.0).round() as u32)
    }
}

impl Drop for D3dkmtBackend {
    fn drop(&mut self) {
        unsafe {
            D3DKMTCloseAdapter(&self.adapter);
        }
    }
}

impl TelemetryBackend for D3dkmtBackend {
    fn kind(&self) -> BackendKind {
        BackendKind::WindowsD3dkmt
    }

    fn read(&mut self) -> Option<GpuSnapshot> {
        let mut stats = [0u8; STATISTICS_SIZE];
        let perf = self.read_perf_data();
        let used_bytes = self.read_memory_used_bytes(&mut stats);
        let util = self.read_busiest_util_pct(&mut stats);

        let core_temp_c = perf.as_ref().and_then(|p| {
            let c = p.temperature_deci_c as f64 / 10.0;
            (c > 0.0 && c < 150.0).then_some(c)
        });
        let fan_rpm = perf
            .as_ref()
            .and_then(|p| (p.fan_rpm > 0 && p.fan_rpm < 20_000).then_some(p.fan_rpm));
        let power_pct = perf.as_ref().and_then(|p| {
            (p.power_deci_pct > 0 && p.power_deci_pct <= 2000)
                .then_some(p.power_deci_pct as f64 / 10.0)
        });
        let mem_clock_mhz = perf.as_ref().and_then(|p| {
            (p.memory_frequency >= 1_000_000).then_some((p.memory_frequency / 1_000_000) as u32)
        });

        Some(GpuSnapshot {
            timestamp_ms: now_unix_ms(),
            backend: BackendKind::WindowsD3dkmt,
            vendor: GpuVendor::Other,
            name: "WDDM Primary GPU (D3DKMT)".to_string(),
            pci_bus: None,
            core_temp_c,
            hotspot_temp_c: None,
            memory_temp_c: None,
            board_power_w: None,
            power_limit_w: None,
            enforced_power_limit_w: None,
            min_power_limit_w: None,
            max_power_limit_w: None,
            power_pct,
            gpu_clock_mhz: None,
            mem_clock_mhz,
            vram_used_mb: used_bytes.map(|b| b >> 20),
            vram_total_mb: (self.dedicated_bytes > 0).then_some(self.dedicated_bytes >> 20),
            gpu_util_pct: util,
            fan_pct: None,
            fan_rpm,
            pstate: None,
            pcie_gen: None,
            pcie_width: None,
            temp_target_c: None,
            temp_max_c: None,
            temp_slowdown_c: None,
            throttle_flags: 0,
            connector: None,
        })
    }
}

fn is_dedicated(stats: &[u8; STATISTICS_SIZE]) -> bool {
    u32::from_le_bytes(stats[RESULT_AT + 40..RESULT_AT + 44].try_into().unwrap()) == 0
}

fn query_stats(stats: &mut [u8; STATISTICS_SIZE], kind: i32, luid: i64, input: u32) -> bool {
    stats.fill(0);
    stats[0..4].copy_from_slice(&kind.to_le_bytes());
    stats[4..12].copy_from_slice(&luid.to_le_bytes());
    stats[INPUT_AT..INPUT_AT + 4].copy_from_slice(&input.to_le_bytes());
    unsafe { D3DKMTQueryStatistics(stats.as_mut_ptr()) == 0 }
}
