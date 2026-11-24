use std::io::{self, BufRead, BufReader, Read, Write};
use std::os::windows::process::CommandExt;
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use crate::backends::discover_primary_backend;
use crate::per_pin::msi_psu::MsiPsuTransport;
use crate::per_pin::wireview::WireViewTransport;
use crate::types::{
    BackendKind, ConnectorSnapshot, GpuSnapshot, GpuVendor, PerPinSource, PinReading,
};

const CREATE_NO_WINDOW: u32 = 0x0800_0000;

pub fn run_sidecar_loop(interval_ms: u64) {
    let stop = Arc::new(AtomicBool::new(false));
    let stop_stdin = Arc::clone(&stop);
    let _stdin_watcher = thread::Builder::new()
        .name("exo-pg-stdin".into())
        .spawn(move || {
            let mut stdin = io::stdin();
            let mut buf = [0u8; 64];
            while let Ok(n) = stdin.read(&mut buf) {
                if n == 0 {
                    break;
                }
            }
            stop_stdin.store(true, Ordering::Relaxed);
        });

    let mut backend = discover_primary_backend();
    let mut wireview = WireViewTransport::discover();
    let msi = if wireview.is_none() {
        MsiPsuTransport::discover()
    } else {
        None
    };

    let stdout = io::stdout();
    let mut out = stdout.lock();
    let sleep_ms = interval_ms.clamp(200, 10_000);

    while !stop.load(Ordering::Relaxed) {
        if backend.is_none() {
            backend = discover_primary_backend();
        }
        if let Some(ref mut b) = backend
            && let Some(mut snap) = b.read()
        {
            if snap.connector.is_none() {
                if let Some(ref mut wv) = wireview {
                    snap.connector = wv.read();
                } else if let Some(ref mp) = msi {
                    snap.connector = mp.read_primary_connector();
                }
            }
            if writeln!(out, "{}", snap.to_json()).is_err() || out.flush().is_err() {
                break;
            }
        }

        let steps = (sleep_ms / 50).max(1);
        for _ in 0..steps {
            if stop.load(Ordering::Relaxed) {
                break;
            }
            thread::sleep(Duration::from_millis(50));
        }
    }
}

pub struct SidecarClient {
    child: Child,
    stdin: Option<ChildStdin>,
    latest: Arc<Mutex<Option<GpuSnapshot>>>,
    reader: Option<JoinHandle<()>>,
}

impl SidecarClient {
    pub fn find_sidecar_exe() -> Option<PathBuf> {
        let exe = std::env::current_exe().ok()?;
        let dir = exe.parent()?;
        let direct = dir.join("exo-power-guard.exe");
        if direct.is_file() {
            return Some(direct);
        }
        if let Some(parent) = dir.parent() {
            let up = parent.join("exo-power-guard.exe");
            if up.is_file() {
                return Some(up);
            }
        }
        None
    }

    pub fn spawn(exe: &PathBuf, interval_ms: u64) -> Option<Self> {
        let mut child = Command::new(exe)
            .args(["sidecar", "--interval-ms", &interval_ms.to_string()])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .ok()?;

        let stdin = child.stdin.take();
        let stdout = child.stdout.take()?;
        let latest = Arc::new(Mutex::new(None));
        let latest_bg = Arc::clone(&latest);

        let reader = thread::Builder::new()
            .name("exo-pg-reader".into())
            .spawn(move || {
                let mut br = BufReader::new(stdout);
                let mut line = String::new();
                loop {
                    line.clear();
                    match br.read_line(&mut line) {
                        Ok(0) | Err(_) => break,
                        Ok(_) => {
                            if let Some(snap) = parse_snapshot_json(line.trim())
                                && let Ok(mut g) = latest_bg.lock()
                            {
                                *g = Some(snap);
                            }
                        }
                    }
                }
            })
            .ok();

        Some(Self {
            child,
            stdin,
            latest,
            reader,
        })
    }

    pub fn spawn_beside_exe(interval_ms: u64) -> Option<Self> {
        let exe = Self::find_sidecar_exe()?;
        Self::spawn(&exe, interval_ms)
    }

    pub fn latest(&self) -> Option<GpuSnapshot> {
        self.latest.lock().ok().and_then(|g| g.clone())
    }
}

impl Drop for SidecarClient {
    fn drop(&mut self) {
        self.stdin.take();
        for _ in 0..10 {
            if let Ok(Some(_)) = self.child.try_wait() {
                break;
            }
            thread::sleep(Duration::from_millis(20));
        }
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(h) = self.reader.take() {
            let _ = h.join();
        }
    }
}

pub fn parse_snapshot_json(input: &str) -> Option<GpuSnapshot> {
    let trimmed = input.trim();
    if !trimmed.starts_with('{') || !trimmed.ends_with('}') {
        return None;
    }
    let timestamp_ms = extract_u64(trimmed, "\"timestamp_ms\":")?;
    let backend = match extract_str(trimmed, "\"backend\":")?.as_str() {
        "nvidia-nvml-nvapi" => BackendKind::NvidiaNvmlNvapi,
        "amd-adlx" => BackendKind::AmdAdlx,
        "intel-igcl" => BackendKind::IntelIgcl,
        "windows-d3dkmt" => BackendKind::WindowsD3dkmt,
        _ => return None,
    };
    let vendor = match extract_str(trimmed, "\"vendor\":")?.as_str() {
        "nvidia" => GpuVendor::Nvidia,
        "amd" => GpuVendor::Amd,
        "intel" => GpuVendor::Intel,
        _ => GpuVendor::Other,
    };
    let name = extract_str(trimmed, "\"name\":")?;
    let connector = extract_object(trimmed, "\"connector\":").and_then(parse_connector_json);

    Some(GpuSnapshot {
        timestamp_ms,
        backend,
        vendor,
        name,
        pci_bus: extract_u32(trimmed, "\"pci_bus\":"),
        core_temp_c: extract_f64(trimmed, "\"core_temp_c\":"),
        hotspot_temp_c: extract_f64(trimmed, "\"hotspot_temp_c\":"),
        memory_temp_c: extract_f64(trimmed, "\"memory_temp_c\":"),
        board_power_w: extract_f64(trimmed, "\"board_power_w\":"),
        power_limit_w: extract_f64(trimmed, "\"power_limit_w\":"),
        enforced_power_limit_w: extract_f64(trimmed, "\"enforced_power_limit_w\":"),
        min_power_limit_w: extract_f64(trimmed, "\"min_power_limit_w\":"),
        max_power_limit_w: extract_f64(trimmed, "\"max_power_limit_w\":"),
        power_pct: extract_f64(trimmed, "\"power_pct\":"),
        gpu_clock_mhz: extract_u32(trimmed, "\"gpu_clock_mhz\":"),
        mem_clock_mhz: extract_u32(trimmed, "\"mem_clock_mhz\":"),
        vram_used_mb: extract_u64(trimmed, "\"vram_used_mb\":"),
        vram_total_mb: extract_u64(trimmed, "\"vram_total_mb\":"),
        gpu_util_pct: extract_u32(trimmed, "\"gpu_util_pct\":"),
        fan_pct: extract_u32(trimmed, "\"fan_pct\":"),
        fan_rpm: extract_u32(trimmed, "\"fan_rpm\":"),
        pstate: extract_u32(trimmed, "\"pstate\":"),
        pcie_gen: extract_u32(trimmed, "\"pcie_gen\":"),
        pcie_width: extract_u32(trimmed, "\"pcie_width\":"),
        temp_target_c: extract_u32(trimmed, "\"temp_target_c\":"),
        temp_max_c: extract_u32(trimmed, "\"temp_max_c\":"),
        temp_slowdown_c: extract_u32(trimmed, "\"temp_slowdown_c\":"),
        throttle_flags: extract_u32(trimmed, "\"throttle_flags\":").unwrap_or(0),
        connector,
    })
}

fn parse_connector_json(obj: &str) -> Option<ConnectorSnapshot> {
    let source = match extract_str(obj, "\"source\":")?.as_str() {
        "asus-astral-i2c" => PerPinSource::AsusAstralI2c,
        "wireview-pro-2" => PerPinSource::WireViewPro2,
        "msi-psu-hid" => PerPinSource::MsiPsuHid,
        _ => return None,
    };
    let connector_index = extract_u32(obj, "\"connector_index\":").unwrap_or(1) as u8;
    let total_current_a = extract_f64(obj, "\"total_current_a\":").unwrap_or(0.0);
    let total_power_w = extract_f64(obj, "\"total_power_w\":");
    let avg_voltage_v = extract_f64(obj, "\"avg_voltage_v\":");
    let rated_cap_w = extract_u32(obj, "\"rated_cap_w\":");
    let hw_fault_bits = extract_u32(obj, "\"hw_fault_bits\":").unwrap_or(0) as u16;

    let mut pins = Vec::new();
    if let Some(arr) = extract_array(obj, "\"pins\":") {
        let mut cursor = arr;
        while let Some(start) = cursor.find('{') {
            let rest = &cursor[start..];
            let Some(end) = matching_brace(rest) else {
                break;
            };
            let item = &rest[..=end];
            if let (Some(pin), Some(current_a)) = (
                extract_u32(item, "\"pin\":"),
                extract_f64(item, "\"current_a\":"),
            ) {
                pins.push(PinReading {
                    pin: pin as u8,
                    voltage_v: extract_f64(item, "\"voltage_v\":"),
                    current_a,
                    power_w: extract_f64(item, "\"power_w\":"),
                });
            }
            cursor = &rest[end + 1..];
        }
    }

    let mut connector_temps_c = Vec::new();
    if let Some(arr) = extract_array(obj, "\"connector_temps_c\":") {
        let inner = arr.trim_matches(|c| c == '[' || c == ']');
        for tok in inner.split(',') {
            if let Ok(v) = tok.trim().parse::<f64>() {
                connector_temps_c.push(v);
            }
        }
    }

    Some(ConnectorSnapshot {
        source,
        connector_index,
        pins,
        total_current_a,
        total_power_w,
        avg_voltage_v,
        connector_temps_c,
        rated_cap_w,
        hw_fault_bits,
    })
}

fn find_val<'a>(src: &'a str, key: &str) -> Option<&'a str> {
    let idx = src.find(key)?;
    Some(src[idx + key.len()..].trim_start())
}

fn extract_str(src: &str, key: &str) -> Option<String> {
    let rest = find_val(src, key)?;
    let bytes = rest.as_bytes();
    if bytes.first() != Some(&b'"') {
        return None;
    }
    let mut out = String::new();
    let mut i = 1usize;
    while i < bytes.len() {
        match bytes[i] {
            b'"' => return Some(out),
            b'\\' if i + 1 < bytes.len() => {
                i += 1;
                match bytes[i] {
                    b'"' => out.push('"'),
                    b'\\' => out.push('\\'),
                    b'n' => out.push('\n'),
                    b'r' => out.push('\r'),
                    b't' => out.push('\t'),
                    other => out.push(other as char),
                }
            }
            b => out.push(b as char),
        }
        i += 1;
    }
    None
}

fn extract_token<'a>(src: &'a str, key: &str) -> Option<&'a str> {
    let rest = find_val(src, key)?;
    let end = rest
        .find([',', '}', ']', ' ', '\n', '\r'])
        .unwrap_or(rest.len());
    let tok = &rest[..end];
    if tok.is_empty() || tok == "null" {
        None
    } else {
        Some(tok)
    }
}

fn extract_u64(src: &str, key: &str) -> Option<u64> {
    extract_token(src, key)?.parse().ok()
}

fn extract_u32(src: &str, key: &str) -> Option<u32> {
    extract_token(src, key)?.parse().ok()
}

fn extract_f64(src: &str, key: &str) -> Option<f64> {
    extract_token(src, key)?.parse().ok()
}

fn extract_object<'a>(src: &'a str, key: &str) -> Option<&'a str> {
    let rest = find_val(src, key)?;
    if !rest.starts_with('{') {
        return None;
    }
    let end = matching_brace(rest)?;
    Some(&rest[..=end])
}

fn extract_array<'a>(src: &'a str, key: &str) -> Option<&'a str> {
    let rest = find_val(src, key)?;
    if !rest.starts_with('[') {
        return None;
    }
    let mut depth = 0i32;
    for (idx, ch) in rest.char_indices() {
        match ch {
            '[' => depth += 1,
            ']' => {
                depth -= 1;
                if depth == 0 {
                    return Some(&rest[..=idx]);
                }
            }
            _ => {}
        }
    }
    None
}

fn matching_brace(src: &str) -> Option<usize> {
    let mut depth = 0i32;
    let mut in_str = false;
    let mut escaped = false;
    for (idx, ch) in src.char_indices() {
        if in_str {
            if escaped {
                escaped = false;
            } else if ch == '\\' {
                escaped = true;
            } else if ch == '"' {
                in_str = false;
            }
            continue;
        }
        match ch {
            '"' => in_str = true,
            '{' => depth += 1,
            '}' => {
                depth -= 1;
                if depth == 0 {
                    return Some(idx);
                }
            }
            _ => {}
        }
    }
    None
}
