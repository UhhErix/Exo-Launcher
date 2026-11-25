use exo_policy::network_destinations::is_listed;
use exo_policy::process_termination::is_anti_cheat;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::HashMap;
use std::sync::Arc;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuOverviewPayload {
    pub detected: bool,
    pub vendor: Option<String>,
    pub name: Option<String>,
    pub temp_c: Option<f64>,
    pub power_w: Option<f64>,
    pub boot_crash_paused: bool,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SensorsAuditPayload {
    pub audit_duration_ms: f64,
    pub drive_count: usize,
    pub finding_count: usize,
    pub whea_24h_count: u32,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DisplayMonitorsPayload {
    pub monitor_count: usize,
    pub models: Vec<String>,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ThermalStatusPayload {
    pub active_curves: usize,
    pub watchdog_armed: bool,
    pub bios_fallback_ready: bool,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PowerGuardStatusPayload {
    pub monitoring: bool,
    pub auto_actions_opt_in: bool,
    pub active_alerts: usize,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotStatusPayload {
    pub active: bool,
    pub active_game: Option<String>,
    pub timer_resolution_us: Option<u32>,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NetLatencyStatusPayload {
    pub dscp_qos_available: bool,
    pub packet_hooks_forbidden: bool,
    pub read_only: bool,
}

const WINDOWS_PROTECTED_PROCESSES: &[&str] = &[
    "system",
    "dwm",
    "csrss",
    "explorer",
    "textinputhost",
    "searchhost",
    "shellexperiencehost",
    "lsass",
    "smss",
    "wininit",
    "winlogon",
    "services",
    "svchost",
    "sihost",
    "fontdrvhost",
    "taskhostw",
    "startmenuexperiencehost",
    "runtimebroker",
    "audiodg",
];

pub fn classify_process_protection(process_name: &str) -> Option<&'static str> {
    if is_anti_cheat(Some(process_name)) {
        return Some("anti-cheat");
    }
    let trimmed = process_name.trim();
    if trimmed.is_empty() {
        return None;
    }
    let tail = trimmed
        .rsplit_once(['\\', '/'])
        .map(|(_, f)| f)
        .unwrap_or(trimmed);
    let lower = tail.to_ascii_lowercase();
    let stem = lower.strip_suffix(".exe").unwrap_or(&lower);
    if WINDOWS_PROTECTED_PROCESSES.contains(&stem) {
        Some("part of Windows")
    } else {
        None
    }
}

pub fn enforce_process_policy_on_payload(payload: &mut Value) {
    let Some(procs) = payload.get_mut("processes").and_then(Value::as_array_mut) else {
        return;
    };
    for proc_entry in procs.iter_mut() {
        let Some(obj) = proc_entry.as_object_mut() else {
            continue;
        };
        let name = obj
            .get("name")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_owned();
        if let Some(reason) = classify_process_protection(&name)
            && (reason == "anti-cheat" || !obj.contains_key("protectedReason"))
        {
            obj.insert("protectedReason".into(), Value::String(reason.into()));
        }
    }
}

pub trait HostBackend: Send + Sync {
    fn read_gpu_overview(&self) -> GpuOverviewPayload;
    fn read_sensors_audit(&self) -> SensorsAuditPayload;
    fn read_display_monitors(&self) -> DisplayMonitorsPayload;
    fn read_thermal_status(&self) -> ThermalStatusPayload;
    fn read_power_guard_status(&self) -> PowerGuardStatusPayload;
    fn read_autopilot_status(&self) -> AutopilotStatusPayload;
    fn read_net_latency_status(&self) -> NetLatencyStatusPayload;

    fn read_system_health(&self) -> Value;
    fn read_gpu_profile(&self) -> Value;
    fn read_gpu_processes(&self, limit: usize) -> Value;
    fn read_gpu_tuning_state(&self) -> Value;
    fn read_system_about(&self) -> Value;
    fn read_gpu_display(&self) -> Value;
    fn read_hdr_state(&self) -> Value;
    fn read_system_drives(&self) -> Value;
}

#[derive(Debug, Default, Clone, Copy)]
pub struct LiveReadOnlyHostBackend;

fn gpu_vendor_wire(v: exo_sensors::gpu::GpuVendor) -> &'static str {
    match v {
        exo_sensors::gpu::GpuVendor::Nvidia => "nvidia",
        exo_sensors::gpu::GpuVendor::Amd => "amd",
        exo_sensors::gpu::GpuVendor::Intel => "intel",
        exo_sensors::gpu::GpuVendor::Other => "unknown",
    }
}

fn gpu_backend_wire(b: exo_sensors::gpu::GpuBackendKind) -> &'static str {
    match b {
        exo_sensors::gpu::GpuBackendKind::NvidiaNvmlNvapi => "nvml",
        exo_sensors::gpu::GpuBackendKind::AmdAdlx => "adlx",
        exo_sensors::gpu::GpuBackendKind::IntelIgcl => "igcl",
        exo_sensors::gpu::GpuBackendKind::WindowsD3dkmt => "d3dkmt",
    }
}

fn short_cpu_label(raw: &str) -> String {
    let trimmed = raw.trim();
    let no_vendor = trimmed
        .strip_prefix("AMD ")
        .or_else(|| trimmed.strip_prefix("Intel(R) Core(TM) "))
        .or_else(|| trimmed.strip_prefix("Intel "))
        .unwrap_or(trimmed);
    no_vendor
        .split(" 6-Core")
        .next()
        .unwrap_or(no_vendor)
        .split(" 8-Core")
        .next()
        .unwrap_or(no_vendor)
        .split(" 12-Core")
        .next()
        .unwrap_or(no_vendor)
        .split(" 16-Core")
        .next()
        .unwrap_or(no_vendor)
        .trim()
        .to_owned()
}

fn short_gpu_label(raw: &str) -> String {
    raw.trim()
        .strip_prefix("NVIDIA GeForce ")
        .or_else(|| raw.trim().strip_prefix("AMD Radeon "))
        .or_else(|| raw.trim().strip_prefix("Intel(R) "))
        .unwrap_or(raw.trim())
        .to_owned()
}

fn normalize_bios_date(raw: Option<&str>) -> Option<String> {
    let s = raw?.trim();
    let parts: Vec<&str> = s.split('/').collect();
    if parts.len() == 3 {
        let month: u32 = parts[0].parse().ok()?;
        let day: u32 = parts[1].parse().ok()?;
        let mut year: u32 = parts[2].parse().ok()?;
        if year < 100 {
            year += 2000;
        }
        return Some(format!("{year:04}-{month:02}-{day:02}"));
    }
    Some(s.to_owned())
}

fn drive_vendor_meta(model: &str) -> Option<Value> {
    let lower = model.to_ascii_lowercase();
    if lower.contains("samsung") {
        let url = "https://semiconductor.samsung.com/consumer-storage/support/tools/";
        Some(json!({
            "name": "Samsung",
            "tool": "Samsung Magician",
            "url": url,
        }))
    } else if lower.contains("wd ")
        || lower.contains("western digital")
        || lower.contains("wd_black")
    {
        Some(json!({
            "name": "Western Digital",
            "tool": "Western Digital Dashboard",
            "url": "https://support-en.wd.com/app/answers/detailweb/a_id/31759",
        }))
    } else if lower.contains("crucial") || lower.contains("micron") {
        Some(json!({
            "name": "Crucial",
            "tool": "Crucial Storage Executive",
            "url": "https://www.crucial.com/support/storage-executive",
        }))
    } else if lower.contains("sabrent") {
        Some(json!({
            "name": "Sabrent",
            "tool": "Sabrent Rocket Control Panel",
            "url": "https://sabrent.com/pages/rocket-control-panel",
        }))
    } else if lower.contains("sk hynix") || lower.contains("solidigm") {
        Some(json!({
            "name": "SK hynix",
            "tool": "Drive Manager",
            "url": "https://ssd.skhynix.com/download/",
        }))
    } else {
        None
    }
}

#[cfg(windows)]
mod win_readonly {
    use serde_json::{Value, json};
    use std::ffi::OsString;
    use std::os::windows::ffi::OsStringExt;
    use std::ptr::{null, null_mut};

    const HKEY_LOCAL_MACHINE: isize = -2147483646_isize;
    const KEY_READ: u32 = 0x20019;
    const REG_SZ: u32 = 1;
    const REG_DWORD: u32 = 4;
    const DISPLAY_DEVICE_ACTIVE: u32 = 0x0000_0001;
    const DISPLAY_DEVICE_PRIMARY_DEVICE: u32 = 0x0000_0004;
    const ENUM_CURRENT_SETTINGS: u32 = 0xFFFF_FFFF;

    #[repr(C)]
    struct DisplayDeviceW {
        cb: u32,
        device_name: [u16; 32],
        device_string: [u16; 128],
        state_flags: u32,
        device_id: [u16; 128],
        device_key: [u16; 128],
    }

    impl DisplayDeviceW {
        fn zeroed() -> Self {
            Self {
                cb: std::mem::size_of::<Self>() as u32,
                device_name: [0; 32],
                device_string: [0; 128],
                state_flags: 0,
                device_id: [0; 128],
                device_key: [0; 128],
            }
        }
    }

    #[repr(C)]
    struct DevModeW {
        dm_device_name: [u16; 32],
        dm_spec_version: u16,
        dm_driver_version: u16,
        dm_size: u16,
        dm_driver_extra: u16,
        dm_fields: u32,
        dm_position_x: i32,
        dm_position_y: i32,
        dm_display_orientation: u32,
        dm_display_fixed_output: u32,
        dm_color: i16,
        dm_duplex: i16,
        dm_y_resolution: i16,
        dm_tt_option: i16,
        dm_collate: i16,
        dm_form_name: [u16; 32],
        dm_log_pixels: u16,
        dm_bits_per_pel: u32,
        dm_pels_width: u32,
        dm_pels_height: u32,
        dm_display_flags: u32,
        dm_display_frequency: u32,
        dm_icm_method: u32,
        dm_icm_intent: u32,
        dm_media_type: u32,
        dm_dither_type: u32,
        dm_reserved1: u32,
        dm_reserved2: u32,
        dm_panning_width: u32,
        dm_panning_height: u32,
    }

    impl DevModeW {
        fn zeroed() -> Self {
            Self {
                dm_device_name: [0; 32],
                dm_spec_version: 0,
                dm_driver_version: 0,
                dm_size: std::mem::size_of::<Self>() as u16,
                dm_driver_extra: 0,
                dm_fields: 0,
                dm_position_x: 0,
                dm_position_y: 0,
                dm_display_orientation: 0,
                dm_display_fixed_output: 0,
                dm_color: 0,
                dm_duplex: 0,
                dm_y_resolution: 0,
                dm_tt_option: 0,
                dm_collate: 0,
                dm_form_name: [0; 32],
                dm_log_pixels: 0,
                dm_bits_per_pel: 0,
                dm_pels_width: 0,
                dm_pels_height: 0,
                dm_display_flags: 0,
                dm_display_frequency: 0,
                dm_icm_method: 0,
                dm_icm_intent: 0,
                dm_media_type: 0,
                dm_dither_type: 0,
                dm_reserved1: 0,
                dm_reserved2: 0,
                dm_panning_width: 0,
                dm_panning_height: 0,
            }
        }
    }

    #[link(name = "advapi32")]
    unsafe extern "system" {
        fn RegOpenKeyExW(
            hkey: isize,
            sub_key: *const u16,
            options: u32,
            sam_desired: u32,
            result: *mut isize,
        ) -> i32;
        fn RegQueryValueExW(
            hkey: isize,
            value_name: *const u16,
            reserved: *mut u32,
            value_type: *mut u32,
            data: *mut u8,
            cb_data: *mut u32,
        ) -> i32;
        fn RegCloseKey(hkey: isize) -> i32;
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetLogicalDriveStringsW(buffer_length: u32, buffer: *mut u16) -> u32;
        fn GetDriveTypeW(root_path_name: *const u16) -> u32;
        fn GetDiskFreeSpaceExW(
            directory_name: *const u16,
            free_bytes_available: *mut u64,
            total_number_of_bytes: *mut u64,
            total_number_of_free_bytes: *mut u64,
        ) -> i32;
    }

    #[link(name = "user32")]
    unsafe extern "system" {
        fn EnumDisplayDevicesW(
            device: *const u16,
            dev_num: u32,
            display_device: *mut DisplayDeviceW,
            flags: u32,
        ) -> i32;
        fn EnumDisplaySettingsW(
            device_name: *const u16,
            mode_num: u32,
            dev_mode: *mut DevModeW,
        ) -> i32;
    }

    fn to_wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn from_wide_buf(buf: &[u16]) -> String {
        let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        OsString::from_wide(&buf[..len])
            .to_string_lossy()
            .into_owned()
    }

    struct RegKeyGuard(isize);

    impl RegKeyGuard {
        fn open_hklm_readonly(subkey: &str) -> Option<Self> {
            let sk = to_wide(subkey);
            let mut hkey: isize = 0;
            let rc =
                unsafe { RegOpenKeyExW(HKEY_LOCAL_MACHINE, sk.as_ptr(), 0, KEY_READ, &mut hkey) };
            if rc == 0 { Some(Self(hkey)) } else { None }
        }
    }

    impl Drop for RegKeyGuard {
        fn drop(&mut self) {
            unsafe {
                RegCloseKey(self.0);
            }
        }
    }

    pub fn read_hklm_string(subkey: &str, name: &str) -> Option<String> {
        let key = RegKeyGuard::open_hklm_readonly(subkey)?;
        let vn = to_wide(name);
        let mut kind = 0u32;
        let mut size = 0u32;
        let rc_len = unsafe {
            RegQueryValueExW(
                key.0,
                vn.as_ptr(),
                null_mut(),
                &mut kind,
                null_mut(),
                &mut size,
            )
        };
        if rc_len != 0 || kind != REG_SZ || size == 0 || size > 4096 {
            return None;
        }
        let mut bytes = vec![0u8; size as usize];
        let rc_val = unsafe {
            RegQueryValueExW(
                key.0,
                vn.as_ptr(),
                null_mut(),
                &mut kind,
                bytes.as_mut_ptr(),
                &mut size,
            )
        };
        if rc_val != 0 || kind != REG_SZ {
            return None;
        }
        let written = (size as usize).min(bytes.len());
        let u16s: Vec<u16> = bytes[..written]
            .as_chunks::<2>()
            .0
            .iter()
            .map(|c| u16::from_le_bytes(*c))
            .collect();
        let s = from_wide_buf(&u16s).trim().to_owned();
        if s.is_empty() { None } else { Some(s) }
    }

    pub fn read_hklm_dword(subkey: &str, name: &str) -> Option<u32> {
        let key = RegKeyGuard::open_hklm_readonly(subkey)?;
        let vn = to_wide(name);
        let mut kind = 0u32;
        let mut buf = [0u8; 4];
        let mut size = buf.len() as u32;
        let rc_val = unsafe {
            RegQueryValueExW(
                key.0,
                vn.as_ptr(),
                null_mut(),
                &mut kind,
                buf.as_mut_ptr(),
                &mut size,
            )
        };
        if rc_val == 0 && kind == REG_DWORD && size == 4 {
            Some(u32::from_ne_bytes(buf))
        } else {
            None
        }
    }

    pub fn read_fixed_volumes() -> Vec<Value> {
        let mut buf = [0u16; 256];
        let len = unsafe { GetLogicalDriveStringsW(buf.len() as u32, buf.as_mut_ptr()) } as usize;
        if len == 0 || len >= buf.len() {
            return Vec::new();
        }
        let mut out = Vec::new();
        let mut start = 0usize;
        while start < len {
            let Some(rel_end) = buf[start..len].iter().position(|&c| c == 0) else {
                break;
            };
            let end = start + rel_end;
            if end == start {
                break;
            }
            let slice = &buf[start..=end];
            let drive_type = unsafe { GetDriveTypeW(slice.as_ptr()) };
            if drive_type == 3 {
                let mut free_avail = 0u64;
                let mut total = 0u64;
                let mut free_total = 0u64;
                let ok = unsafe {
                    GetDiskFreeSpaceExW(
                        slice.as_ptr(),
                        &mut free_avail,
                        &mut total,
                        &mut free_total,
                    )
                };
                if ok != 0 && total > 0 {
                    let root = from_wide_buf(&buf[start..end]);
                    let letter = root.trim_end_matches('\\').to_owned();
                    out.push(json!({
                        "letter": letter,
                        "totalGb": (total as f64) / 1_073_741_824.0,
                        "freeGb": (free_total as f64) / 1_073_741_824.0,
                    }));
                }
            }
            start = end + 1;
        }
        out
    }

    pub struct LiveDisplayRow {
        pub id: String,
        pub gdi_name: String,
        pub main: bool,
        pub refresh: u32,
    }

    pub fn read_active_displays() -> Vec<LiveDisplayRow> {
        let mut out = Vec::new();
        for idx in 0..16u32 {
            let mut dd = DisplayDeviceW::zeroed();
            let ok = unsafe { EnumDisplayDevicesW(null(), idx, &mut dd, 0) };
            if ok == 0 {
                break;
            }
            if dd.state_flags & DISPLAY_DEVICE_ACTIVE == 0 {
                continue;
            }
            let gdi_name = from_wide_buf(&dd.device_name);
            let id = gdi_name.trim_start_matches("\\\\.\\").to_owned();
            let main = dd.state_flags & DISPLAY_DEVICE_PRIMARY_DEVICE != 0;
            let mut dm = DevModeW::zeroed();
            let wide_name = to_wide(&gdi_name);
            let refresh = if unsafe {
                EnumDisplaySettingsW(wide_name.as_ptr(), ENUM_CURRENT_SETTINGS, &mut dm)
            } != 0
            {
                dm.dm_display_frequency
            } else {
                60
            };
            out.push(LiveDisplayRow {
                id,
                gdi_name,
                main,
                refresh,
            });
        }
        out
    }
}

impl HostBackend for LiveReadOnlyHostBackend {
    fn read_gpu_overview(&self) -> GpuOverviewPayload {
        let ov = exo_gpu::read_gpu_control_overview();
        let (detected, vendor, name, temp_c, power_w) = match ov.gpu {
            Some(g) => (
                true,
                Some(format!("{:?}", g.vendor)),
                Some(g.name),
                g.core_temp_c,
                g.board_power_w,
            ),
            None => (false, None, None, None, None),
        };
        GpuOverviewPayload {
            detected,
            vendor,
            name,
            temp_c,
            power_w,
            boot_crash_paused: matches!(
                ov.boot_crash_guard,
                exo_gpu::undervolt::CrashGuardDecision::PauseAndKeepStock { .. }
            ),
            read_only: true,
        }
    }

    fn read_sensors_audit(&self) -> SensorsAuditPayload {
        let audit = exo_sensors::adaptive::run_full_hardware_audit();
        SensorsAuditPayload {
            audit_duration_ms: audit.audit_duration_ms,
            drive_count: audit.drives.len(),
            finding_count: audit.findings.len(),
            whea_24h_count: audit.stability_24h.whea_errors,
            read_only: true,
        }
    }

    fn read_display_monitors(&self) -> DisplayMonitorsPayload {
        let monitors = exo_display::read_live_monitors_readonly();
        let models = monitors
            .iter()
            .map(|m| {
                m.parsed
                    .monitor_name
                    .clone()
                    .unwrap_or_else(|| m.parsed.manufacturer.clone())
            })
            .collect();
        DisplayMonitorsPayload {
            monitor_count: monitors.len(),
            models,
            read_only: true,
        }
    }

    fn read_thermal_status(&self) -> ThermalStatusPayload {
        ThermalStatusPayload {
            active_curves: 0,
            watchdog_armed: false,
            bios_fallback_ready: true,
            read_only: true,
        }
    }

    fn read_power_guard_status(&self) -> PowerGuardStatusPayload {
        PowerGuardStatusPayload {
            monitoring: false,
            auto_actions_opt_in: false,
            active_alerts: 0,
            read_only: true,
        }
    }

    fn read_autopilot_status(&self) -> AutopilotStatusPayload {
        AutopilotStatusPayload {
            active: false,
            active_game: None,
            timer_resolution_us: None,
            read_only: true,
        }
    }

    fn read_net_latency_status(&self) -> NetLatencyStatusPayload {
        NetLatencyStatusPayload {
            dscp_qos_available: true,
            packet_hooks_forbidden: true,
            read_only: true,
        }
    }

    fn read_system_health(&self) -> Value {
        let audit = exo_sensors::adaptive::run_full_hardware_audit();
        let cpu_label = audit
            .smbios
            .as_ref()
            .and_then(|s| s.cpu_model.as_deref())
            .map(short_cpu_label);
        json!({
            "ok": true,
            "items": [
                {
                    "key": "power",
                    "label": "Power plan",
                    "detail": "Read-only sensor audit",
                    "applied": true,
                    "canUndo": false,
                    "busy": false,
                    "cpu": cpu_label,
                    "current": false,
                    "held": false,
                    "off": false
                }
            ],
            "audit": {
                "durationMs": audit.audit_duration_ms,
                "driveCount": audit.drives.len(),
                "findingCount": audit.findings.len(),
                "whea24hCount": audit.stability_24h.whea_errors,
            }
        })
    }

    fn read_gpu_profile(&self) -> Value {
        let gpu = exo_sensors::gpu::discover_primary_gpu().and_then(|mut b| b.read());
        let monitors = exo_display::read_live_monitors_readonly();
        let screen_name = monitors.first().and_then(|m| m.parsed.monitor_name.clone());
        let vrr = monitors
            .first()
            .map(|m| m.parsed.vrr_range.is_some())
            .unwrap_or(false);
        #[cfg(windows)]
        let hz = win_readonly::read_active_displays()
            .into_iter()
            .find(|d| d.main)
            .map(|d| d.refresh)
            .unwrap_or(60);
        #[cfg(not(windows))]
        let hz = 60u32;

        let (vendor, gpu_short) = match gpu {
            Some(g) => (
                Some(gpu_vendor_wire(g.vendor).to_owned()),
                Some(short_gpu_label(&g.name)),
            ),
            None => (None, None),
        };
        json!({
            "ok": true,
            "vendor": vendor,
            "gpu": gpu_short,
            "screen": {
                "name": screen_name,
                "hz": hz,
                "vrr": vrr,
            },
            "status": "readonly",
            "canUndo": false,
            "driverUpdated": false,
            "items": [],
        })
    }

    fn read_gpu_processes(&self, limit: usize) -> Value {
        let gpu = exo_sensors::gpu::discover_primary_gpu().and_then(|mut b| b.read());
        let adapters = match gpu {
            Some(g) => {
                let vendor_id = match g.vendor {
                    exo_sensors::gpu::GpuVendor::Nvidia => 4318u32,
                    exo_sensors::gpu::GpuVendor::Amd => 4098u32,
                    exo_sensors::gpu::GpuVendor::Intel => 32902u32,
                    exo_sensors::gpu::GpuVendor::Other => 0u32,
                };
                vec![json!({
                    "luid": "0x00000000_0x00000000",
                    "name": g.name,
                    "vendorId": vendor_id,
                    "dedicatedBytes": g.vram_total_mb.unwrap_or(0) * 1_048_576,
                    "sharedBytes": 0u64,
                    "software": false,
                })]
            }
            None => Vec::new(),
        };
        let _ = limit;
        let mut payload = json!({
            "ok": true,
            "adapters": adapters,
            "processes": [],
            "answer": {
                "summary": "Read-only GPU process telemetry",
                "hogs": []
            }
        });
        enforce_process_policy_on_payload(&mut payload);
        payload
    }

    fn read_gpu_tuning_state(&self) -> Value {
        let ov = exo_gpu::read_gpu_control_overview();
        let paused = matches!(
            ov.boot_crash_guard,
            exo_gpu::undervolt::CrashGuardDecision::PauseAndKeepStock { .. }
        );
        let (
            vendor,
            device_name,
            backend,
            temp_c,
            clock_mhz,
            watts,
            power_min,
            power_max,
            power_def,
        ) = match ov.gpu {
            Some(g) => (
                gpu_vendor_wire(g.vendor),
                g.name,
                gpu_backend_wire(g.backend),
                g.core_temp_c,
                g.gpu_clock_mhz,
                g.board_power_w,
                g.min_power_limit_w.unwrap_or(0.0) as i32,
                g.max_power_limit_w.unwrap_or(0.0) as i32,
                g.power_limit_w.unwrap_or(0.0) as i32,
            ),
            None => ("unknown", String::new(), "none", None, None, None, 0, 0, 0),
        };
        json!({
            "ok": true,
            "mode": "auto",
            "pausedAfterCrash": paused,
            "canReapply": false,
            "queuedPending": false,
            "statusLine": format!("Auto (stock) · {}", backend.to_ascii_uppercase()),
            "live": {
                "tempC": temp_c,
                "clockMhz": clock_mhz,
                "watts": watts,
            },
            "capabilities": {
                "vendor": vendor,
                "deviceId": format!("{vendor}:{device_name}"),
                "deviceName": device_name,
                "backend": backend,
                "powerLimitWatts": {
                    "min": power_min,
                    "max": power_max,
                    "step": 5,
                    "default": power_def,
                    "supported": power_max > power_min,
                }
            }
        })
    }

    fn read_system_about(&self) -> Value {
        let smbios = exo_sensors::smbios::read_live_smbios();
        let gpu = exo_sensors::gpu::discover_primary_gpu().and_then(|mut b| b.read());

        let (
            processor,
            cores,
            threads,
            memory_gb,
            memory_type,
            memory_speed,
            memory_rated,
            slots_used,
            slots_total,
            motherboard,
            bios,
            bios_date,
        ) = match smbios {
            Some(ref s) => {
                let fitted: Vec<_> = s.dimms.iter().filter(|d| d.size_mb > 0).collect();
                let mem_type = fitted.first().map(|d| d.memory_type.clone());
                let speed = fitted
                    .iter()
                    .filter(|d| d.configured_mts > 0)
                    .map(|d| d.configured_mts)
                    .min();
                let rated = fitted
                    .iter()
                    .filter(|d| d.speed_mts > 0)
                    .map(|d| d.speed_mts)
                    .max();
                let board = match (s.board_maker.as_deref(), s.board_product.as_deref()) {
                    (Some(m), Some(p)) if !p.is_empty() => {
                        let maker_short = if m.to_ascii_uppercase().contains("ASUS") {
                            "ASUS"
                        } else {
                            m.trim()
                        };
                        if p.to_ascii_uppercase()
                            .starts_with(&maker_short.to_ascii_uppercase())
                        {
                            Some(p.trim().to_owned())
                        } else {
                            Some(format!("{maker_short} {}", p.trim()))
                        }
                    }
                    (_, Some(p)) => Some(p.trim().to_owned()),
                    _ => None,
                };
                (
                    s.cpu_model.clone(),
                    s.cpu_cores,
                    s.cpu_threads.unwrap_or_else(|| {
                        std::thread::available_parallelism()
                            .map(|n| n.get() as u32)
                            .unwrap_or(1)
                    }),
                    Some(s.total_ram_gb()),
                    mem_type,
                    speed,
                    rated,
                    Some(fitted.len() as u32),
                    Some(s.dimms.len() as u32),
                    board,
                    s.bios_version.clone(),
                    normalize_bios_date(s.bios_date.as_deref()),
                )
            }
            None => (
                None,
                None,
                std::thread::available_parallelism()
                    .map(|n| n.get() as u32)
                    .unwrap_or(1),
                None,
                None,
                None,
                None,
                None,
                None,
                None,
                None,
                None,
            ),
        };

        let (graphics, vram_gb) = match gpu {
            Some(g) => (
                Some(g.name),
                g.vram_total_mb.map(|mb| ((mb as f64) / 1024.0).round()),
            ),
            None => (None, None),
        };

        #[cfg(windows)]
        let (windows_name, windows_version, build, secure_boot) = {
            let sub = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
            let build_num = win_readonly::read_hklm_string(sub, "CurrentBuildNumber")
                .and_then(|s| s.parse::<u32>().ok())
                .unwrap_or(0);
            let ubr = win_readonly::read_hklm_dword(sub, "UBR");
            let full_build = match ubr {
                Some(r) if build_num > 0 => Some(format!("{build_num}.{r}")),
                _ if build_num > 0 => Some(build_num.to_string()),
                _ => None,
            };
            let prod = win_readonly::read_hklm_string(sub, "ProductName").map(|p| {
                if build_num >= 22000 {
                    p.replace("Windows 10", "Windows 11")
                } else {
                    p
                }
            });
            let ver = win_readonly::read_hklm_string(sub, "DisplayVersion");
            let sb = win_readonly::read_hklm_dword(
                "SYSTEM\\CurrentControlSet\\Control\\SecureBoot\\State",
                "UEFISecureBootEnabled",
            )
            .map(|v| v != 0);
            (prod, ver, full_build, sb)
        };
        #[cfg(not(windows))]
        let (windows_name, windows_version, build, secure_boot): (
            Option<String>,
            Option<String>,
            Option<String>,
            Option<bool>,
        ) = (None, None, None, None);

        let release_host_allowed = is_listed("learn.microsoft.com");
        let _ = release_host_allowed;

        json!({
            "name": std::env::var("COMPUTERNAME").ok(),
            "processor": processor,
            "cores": cores,
            "threads": threads,
            "graphics": graphics,
            "videoMemoryGb": vram_gb,
            "memoryGb": memory_gb,
            "memoryType": memory_type,
            "memorySpeed": memory_speed,
            "memoryRated": memory_rated,
            "memorySlotsUsed": slots_used,
            "memorySlots": slots_total,
            "motherboard": motherboard,
            "bios": bios,
            "biosDate": bios_date,
            "windows": windows_name,
            "windowsVersion": windows_version,
            "build": build,
            "secureBoot": secure_boot,
        })
    }

    fn read_gpu_display(&self) -> Value {
        let edids = exo_display::read_live_monitors_readonly();
        let gpu = exo_sensors::gpu::discover_primary_gpu().and_then(|mut b| b.read());
        let vendor = gpu.map(|g| gpu_vendor_wire(g.vendor).to_owned());

        #[cfg(windows)]
        let active = win_readonly::read_active_displays();
        #[cfg(not(windows))]
        let active: Vec<win_readonly::LiveDisplayRow> = Vec::new();

        let count = active.len().max(edids.len());
        let mut displays = Vec::with_capacity(count);
        for i in 0..count {
            let act = active.get(i);
            let edid = edids.get(i);
            let id = act
                .map(|a| a.id.clone())
                .unwrap_or_else(|| format!("DISPLAY{}", i + 1));
            let name = edid
                .and_then(|e| e.parsed.monitor_name.clone())
                .unwrap_or_else(|| {
                    edid.map(|e| e.parsed.manufacturer.clone())
                        .unwrap_or_else(|| id.clone())
                });
            let main = act.map(|a| a.main).unwrap_or(i == 0);
            let refresh = act.map(|a| a.refresh).unwrap_or(60);
            let hdr_supported = edid
                .and_then(|e| e.parsed.hdr_metadata)
                .map(|h| h.supports_pq)
                .unwrap_or(false);
            displays.push(json!({
                "id": id,
                "name": name,
                "main": main,
                "hdr": false,
                "hdrSupported": hdr_supported,
                "vendor": vendor,
                "refresh": refresh,
                "refreshLabel": format!("{refresh} Hz"),
                "refreshes": [refresh],
            }));
        }

        json!({
            "ok": true,
            "displays": displays,
        })
    }

    fn read_hdr_state(&self) -> Value {
        let edids = exo_display::read_live_monitors_readonly();
        #[cfg(windows)]
        let active = win_readonly::read_active_displays();
        #[cfg(not(windows))]
        let active: Vec<win_readonly::LiveDisplayRow> = Vec::new();

        let count = active.len().max(edids.len());
        let mut displays = Vec::with_capacity(count);
        let mut primary_supported = false;
        for i in 0..count {
            let act = active.get(i);
            let edid = edids.get(i);
            let gdi_name = act
                .map(|a| a.gdi_name.clone())
                .unwrap_or_else(|| format!("\\\\.\\DISPLAY{}", i + 1));
            let name = edid
                .and_then(|e| e.parsed.monitor_name.clone())
                .unwrap_or_else(|| {
                    edid.map(|e| e.parsed.manufacturer.clone())
                        .unwrap_or_else(|| format!("DISPLAY{}", i + 1))
                });
            let main = act.map(|a| a.main).unwrap_or(i == 0);
            let supported = edid
                .and_then(|e| e.parsed.hdr_metadata)
                .map(|h| h.supports_pq)
                .unwrap_or(false);
            if main {
                primary_supported = supported;
            }
            displays.push(json!({
                "name": name,
                "gdiName": gdi_name,
                "main": main,
                "supported": supported,
                "on": false,
                "internal": false,
                "colorMode": "sdr",
                "peakHealth": {
                    "status": "unknown"
                }
            }));
        }
        json!({
            "supported": primary_supported,
            "internal": false,
            "colorMode": "sdr",
            "displays": displays,
        })
    }

    fn read_system_drives(&self) -> Value {
        let nvme_drives = exo_sensors::nvme_smart::read_all_nvme_drives();
        #[cfg(windows)]
        let volumes = win_readonly::read_fixed_volumes();
        #[cfg(not(windows))]
        let volumes: Vec<Value> = Vec::new();

        let mut drives = Vec::with_capacity(nvme_drives.len());
        for (idx, d) in nvme_drives.iter().enumerate() {
            let kind = if d.is_nvme { "NVMe SSD" } else { "SSD" };
            let temperature = d.temp_c.map(|now| {
                json!({
                    "now": now,
                    "warn": d.warn_temp_c,
                    "critical": d.critical_temp_c,
                })
            });
            let health = if d.life_used_pct.is_some() || d.spare_pct.is_some() {
                Some(json!({
                    "lifeUsed": d.life_used_pct,
                    "spare": d.spare_pct,
                    "unsafeShutdowns": d.unsafe_shutdowns,
                    "mediaErrors": d.media_errors,
                }))
            } else {
                None
            };
            let drive_volumes = if idx == 0 {
                volumes.clone()
            } else {
                Vec::new()
            };
            drives.push(json!({
                "id": d.disk_number.to_string(),
                "model": d.model,
                "kind": kind,
                "vendor": drive_vendor_meta(&d.model),
                "temperature": temperature,
                "health": health,
                "volumes": drive_volumes,
                "games": [],
                "verdicts": [],
            }));
        }
        json!({
            "ok": true,
            "fixes": [],
            "drives": drives,
        })
    }
}

#[derive(Debug, Clone)]
pub struct MockHostBackend {
    pub gpu: GpuOverviewPayload,
    pub sensors: SensorsAuditPayload,
    pub displays: DisplayMonitorsPayload,
    pub thermal: ThermalStatusPayload,
    pub power_guard: PowerGuardStatusPayload,
    pub autopilot: AutopilotStatusPayload,
    pub net_latency: NetLatencyStatusPayload,
    pub recorded_responses: Arc<HashMap<String, Value>>,
}

impl MockHostBackend {
    pub fn from_recorded_responses(map: HashMap<String, Value>) -> Self {
        Self {
            recorded_responses: Arc::new(map),
            ..Self::default()
        }
    }

    fn recorded_or(&self, method: &str, fallback: Value) -> Value {
        self.recorded_responses
            .get(method)
            .cloned()
            .unwrap_or(fallback)
    }
}

impl Default for MockHostBackend {
    fn default() -> Self {
        Self {
            gpu: GpuOverviewPayload {
                detected: true,
                vendor: Some("Nvidia".into()),
                name: Some("Mock RTX GPU".into()),
                temp_c: Some(41.0),
                power_w: Some(18.5),
                boot_crash_paused: false,
                read_only: true,
            },
            sensors: SensorsAuditPayload {
                audit_duration_ms: 1.2,
                drive_count: 2,
                finding_count: 0,
                whea_24h_count: 0,
                read_only: true,
            },
            displays: DisplayMonitorsPayload {
                monitor_count: 1,
                models: vec!["Mock 240Hz OLED".into()],
                read_only: true,
            },
            thermal: ThermalStatusPayload {
                active_curves: 1,
                watchdog_armed: true,
                bios_fallback_ready: true,
                read_only: true,
            },
            power_guard: PowerGuardStatusPayload {
                monitoring: true,
                auto_actions_opt_in: false,
                active_alerts: 0,
                read_only: true,
            },
            autopilot: AutopilotStatusPayload {
                active: false,
                active_game: None,
                timer_resolution_us: Some(507),
                read_only: true,
            },
            net_latency: NetLatencyStatusPayload {
                dscp_qos_available: true,
                packet_hooks_forbidden: true,
                read_only: true,
            },
            recorded_responses: Arc::new(HashMap::new()),
        }
    }
}

impl HostBackend for MockHostBackend {
    fn read_gpu_overview(&self) -> GpuOverviewPayload {
        self.gpu.clone()
    }

    fn read_sensors_audit(&self) -> SensorsAuditPayload {
        self.sensors.clone()
    }

    fn read_display_monitors(&self) -> DisplayMonitorsPayload {
        self.displays.clone()
    }

    fn read_thermal_status(&self) -> ThermalStatusPayload {
        self.thermal.clone()
    }

    fn read_power_guard_status(&self) -> PowerGuardStatusPayload {
        self.power_guard.clone()
    }

    fn read_autopilot_status(&self) -> AutopilotStatusPayload {
        self.autopilot.clone()
    }

    fn read_net_latency_status(&self) -> NetLatencyStatusPayload {
        self.net_latency.clone()
    }

    fn read_system_health(&self) -> Value {
        self.recorded_or(
            "system.health",
            json!({
                "ok": true,
                "items": []
            }),
        )
    }

    fn read_gpu_profile(&self) -> Value {
        self.recorded_or(
            "gpu.profile",
            json!({
                "ok": true,
                "vendor": "nvidia",
                "gpu": self.gpu.name,
                "items": []
            }),
        )
    }

    fn read_gpu_processes(&self, limit: usize) -> Value {
        let mut payload = self.recorded_or(
            "gpu.processes",
            json!({
                "ok": true,
                "adapters": [],
                "processes": [],
                "answer": { "summary": "", "hogs": [] }
            }),
        );
        if let Some(arr) = payload.get_mut("processes").and_then(Value::as_array_mut)
            && arr.len() > limit
        {
            arr.truncate(limit);
        }
        enforce_process_policy_on_payload(&mut payload);
        payload
    }

    fn read_gpu_tuning_state(&self) -> Value {
        self.recorded_or(
            "gpu.tuning.state",
            json!({
                "ok": true,
                "mode": "auto",
                "pausedAfterCrash": self.gpu.boot_crash_paused,
                "live": {
                    "tempC": self.gpu.temp_c,
                    "watts": self.gpu.power_w
                }
            }),
        )
    }

    fn read_system_about(&self) -> Value {
        self.recorded_or(
            "system.about",
            json!({
                "processor": "Mock CPU",
                "cores": 6,
                "threads": 12
            }),
        )
    }

    fn read_gpu_display(&self) -> Value {
        self.recorded_or(
            "gpu.display",
            json!({
                "ok": true,
                "displays": []
            }),
        )
    }

    fn read_hdr_state(&self) -> Value {
        self.recorded_or(
            "hdr.state",
            json!({
                "supported": false,
                "internal": false,
                "colorMode": "sdr",
                "displays": []
            }),
        )
    }

    fn read_system_drives(&self) -> Value {
        self.recorded_or(
            "system.drives",
            json!({
                "ok": true,
                "fixes": [],
                "drives": []
            }),
        )
    }
}
