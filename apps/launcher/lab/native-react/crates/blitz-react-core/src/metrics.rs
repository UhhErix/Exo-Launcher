use serde::Serialize;
use std::collections::{HashMap, VecDeque};
use std::path::Path;
use windows::Win32::Foundation::{CloseHandle, FILETIME, HANDLE};
use windows::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
};
use windows::Win32::System::ProcessStatus::{K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX};
use windows::Win32::System::Threading::{
    GetCurrentProcess, GetProcessTimes, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
};

#[derive(Clone, Debug, Default, Serialize)]
pub struct ProcessMemRow {
    pub pid: u32,
    pub parent_pid: u32,
    pub name: String,
    pub working_set_mb: f64,
    pub private_bytes_mb: f64,
}

#[derive(Clone, Debug, Default, Serialize)]
pub struct ProcessTreeStats {
    pub root_pid: u32,
    pub root_ws_mb: f64,
    pub root_priv_mb: f64,
    pub child_count: usize,
    pub child_ws_mb: f64,
    pub child_priv_mb: f64,
    pub total_procs: usize,
    pub total_ws_mb: f64,
    pub total_priv_mb: f64,
    pub processes: Vec<ProcessMemRow>,
}

#[derive(Clone, Debug, Default, Serialize)]
pub struct FrameSummary {
    pub samples: usize,
    pub avg_ms: f64,
    pub p50_ms: f64,
    pub p95_ms: f64,
    pub p99_ms: f64,
    pub max_ms: f64,
}

pub fn round2(v: f64) -> f64 {
    (v * 100.0).round() / 100.0
}

pub fn summarize_ms(samples: &[f64]) -> FrameSummary {
    if samples.is_empty() {
        return FrameSummary::default();
    }
    let mut sorted = samples.to_vec();
    sorted.sort_by(|a, b| a.total_cmp(b));
    let n = sorted.len();
    let sum: f64 = sorted.iter().sum();
    let pct = |p: f64| -> f64 {
        let idx = ((n as f64 - 1.0) * p).round().clamp(0.0, (n - 1) as f64) as usize;
        round2(sorted[idx])
    };
    FrameSummary {
        samples: n,
        avg_ms: round2(sum / n as f64),
        p50_ms: pct(0.50),
        p95_ms: pct(0.95),
        p99_ms: pct(0.99),
        max_ms: round2(sorted[n - 1]),
    }
}

pub fn current_process_cpu_ms() -> f64 {
    unsafe {
        let mut creation = FILETIME::default();
        let mut exit = FILETIME::default();
        let mut kernel = FILETIME::default();
        let mut user = FILETIME::default();
        if GetProcessTimes(
            GetCurrentProcess(),
            &mut creation,
            &mut exit,
            &mut kernel,
            &mut user,
        )
        .is_ok()
        {
            let k = ((kernel.dwHighDateTime as u64) << 32) | (kernel.dwLowDateTime as u64);
            let u = ((user.dwHighDateTime as u64) << 32) | (user.dwLowDateTime as u64);
            (k + u) as f64 / 10_000.0
        } else {
            0.0
        }
    }
}

fn query_pid_mem(pid: u32) -> (f64, f64) {
    unsafe {
        let handle = match OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
            Ok(h) => h,
            Err(_) => return (0.0, 0.0),
        };
        let mut pmc = PROCESS_MEMORY_COUNTERS_EX::default();
        pmc.cb = std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32;
        let ok = K32GetProcessMemoryInfo(
            handle,
            (&mut pmc as *mut PROCESS_MEMORY_COUNTERS_EX).cast(),
            pmc.cb,
        )
        .as_bool();
        let _ = CloseHandle(handle);
        if ok {
            let ws = pmc.WorkingSetSize as f64 / (1024.0 * 1024.0);
            let priv_b = pmc.PrivateUsage as f64 / (1024.0 * 1024.0);
            (round2(ws), round2(priv_b))
        } else {
            (0.0, 0.0)
        }
    }
}

pub fn sample_process_tree(root_pid: u32) -> ProcessTreeStats {
    let mut children_map: HashMap<u32, Vec<(u32, String)>> = HashMap::new();
    let mut names: HashMap<u32, (u32, String)> = HashMap::new();

    unsafe {
        if let Ok(snap) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) {
            if snap != HANDLE::default() {
                let mut entry = PROCESSENTRY32W::default();
                entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
                if Process32FirstW(snap, &mut entry).is_ok() {
                    loop {
                        let pid = entry.th32ProcessID;
                        let ppid = entry.th32ParentProcessID;
                        let len = entry
                            .szExeFile
                            .iter()
                            .position(|&c| c == 0)
                            .unwrap_or(entry.szExeFile.len());
                        let name = String::from_utf16_lossy(&entry.szExeFile[..len]);
                        names.insert(pid, (ppid, name.clone()));
                        children_map.entry(ppid).or_default().push((pid, name));
                        if Process32NextW(snap, &mut entry).is_err() {
                            break;
                        }
                    }
                }
                let _ = CloseHandle(snap);
            }
        }
    }

    let mut queue = VecDeque::new();
    queue.push_back(root_pid);
    let mut seen = HashMap::new();
    seen.insert(root_pid, true);
    let mut rows = Vec::new();

    while let Some(pid) = queue.pop_front() {
        let (ppid, name) = names
            .get(&pid)
            .cloned()
            .unwrap_or((0, format!("pid-{pid}")));
        let (ws, priv_b) = query_pid_mem(pid);
        rows.push(ProcessMemRow {
            pid,
            parent_pid: ppid,
            name,
            working_set_mb: ws,
            private_bytes_mb: priv_b,
        });
        if let Some(kids) = children_map.get(&pid) {
            for (kpid, _) in kids {
                if !seen.contains_key(kpid) {
                    seen.insert(*kpid, true);
                    queue.push_back(*kpid);
                }
            }
        }
    }

    let root = rows.first().cloned().unwrap_or_default();
    let child_ws: f64 = rows.iter().skip(1).map(|r| r.working_set_mb).sum();
    let child_priv: f64 = rows.iter().skip(1).map(|r| r.private_bytes_mb).sum();
    ProcessTreeStats {
        root_pid,
        root_ws_mb: round2(root.working_set_mb),
        root_priv_mb: round2(root.private_bytes_mb),
        child_count: rows.len().saturating_sub(1),
        child_ws_mb: round2(child_ws),
        child_priv_mb: round2(child_priv),
        total_procs: rows.len(),
        total_ws_mb: round2(root.working_set_mb + child_ws),
        total_priv_mb: round2(root.private_bytes_mb + child_priv),
        processes: rows,
    }
}

pub fn write_rgba_png(path: &Path, width: u32, height: u32, rgba: &[u8]) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let file = std::fs::File::create(path).map_err(|e| e.to_string())?;
    let w = std::io::BufWriter::new(file);
    let mut encoder = png::Encoder::new(w, width, height);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    let mut writer = encoder.write_header().map_err(|e| e.to_string())?;
    writer
        .write_image_data(&rgba[..(width as usize * height as usize * 4)])
        .map_err(|e| e.to_string())
}
