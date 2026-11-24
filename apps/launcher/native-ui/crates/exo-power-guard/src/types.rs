use std::fmt::Write as _;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GpuVendor {
    Nvidia,
    Amd,
    Intel,
    Other,
}

impl GpuVendor {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Nvidia => "nvidia",
            Self::Amd => "amd",
            Self::Intel => "intel",
            Self::Other => "other",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BackendKind {
    NvidiaNvmlNvapi,
    AmdAdlx,
    IntelIgcl,
    WindowsD3dkmt,
}

impl BackendKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::NvidiaNvmlNvapi => "nvidia-nvml-nvapi",
            Self::AmdAdlx => "amd-adlx",
            Self::IntelIgcl => "intel-igcl",
            Self::WindowsD3dkmt => "windows-d3dkmt",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PerPinSource {
    AsusAstralI2c,
    WireViewPro2,
    MsiPsuHid,
}

impl PerPinSource {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::AsusAstralI2c => "asus-astral-i2c",
            Self::WireViewPro2 => "wireview-pro-2",
            Self::MsiPsuHid => "msi-psu-hid",
        }
    }
}

pub const THROTTLE_SW_POWER_CAP: u32 = 1 << 0;
pub const THROTTLE_SW_THERMAL: u32 = 1 << 1;
pub const THROTTLE_HW_THERMAL: u32 = 1 << 2;
pub const THROTTLE_HW_POWER_BRAKE: u32 = 1 << 3;
pub const THROTTLE_CURRENT_LIMIT: u32 = 1 << 4;

#[derive(Debug, Clone, PartialEq)]
pub struct PinReading {
    pub pin: u8,
    pub voltage_v: Option<f64>,
    pub current_a: f64,
    pub power_w: Option<f64>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ConnectorSnapshot {
    pub source: PerPinSource,
    pub connector_index: u8,
    pub pins: Vec<PinReading>,
    pub total_current_a: f64,
    pub total_power_w: Option<f64>,
    pub avg_voltage_v: Option<f64>,
    pub connector_temps_c: Vec<f64>,
    pub rated_cap_w: Option<u32>,
    pub hw_fault_bits: u16,
}

impl ConnectorSnapshot {
    pub fn max_pin_current_a(&self) -> f64 {
        self.pins
            .iter()
            .map(|p| p.current_a)
            .fold(0.0_f64, f64::max)
    }

    pub fn min_pin_current_a(&self) -> f64 {
        self.pins
            .iter()
            .map(|p| p.current_a)
            .fold(f64::INFINITY, f64::min)
    }

    pub fn imbalance_spread_a(&self) -> f64 {
        if self.pins.len() < 2 {
            return 0.0;
        }
        (self.max_pin_current_a() - self.min_pin_current_a()).max(0.0)
    }

    pub fn max_connector_temp_c(&self) -> Option<f64> {
        self.connector_temps_c
            .iter()
            .copied()
            .filter(|t| t.is_finite() && *t > -40.0 && *t < 200.0)
            .reduce(f64::max)
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct GpuCapabilities {
    pub has_board_power_w: bool,
    pub has_power_limit_control: bool,
    pub has_hotspot_temp: bool,
    pub has_throttle_flags: bool,
    pub has_per_pin_shunts: bool,
    pub summary: String,
    pub limitation_note: Option<String>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct GpuSnapshot {
    pub timestamp_ms: u64,
    pub backend: BackendKind,
    pub vendor: GpuVendor,
    pub name: String,
    pub pci_bus: Option<u32>,
    pub core_temp_c: Option<f64>,
    pub hotspot_temp_c: Option<f64>,
    pub memory_temp_c: Option<f64>,
    pub board_power_w: Option<f64>,
    pub power_limit_w: Option<f64>,
    pub enforced_power_limit_w: Option<f64>,
    pub min_power_limit_w: Option<f64>,
    pub max_power_limit_w: Option<f64>,
    pub power_pct: Option<f64>,
    pub gpu_clock_mhz: Option<u32>,
    pub mem_clock_mhz: Option<u32>,
    pub vram_used_mb: Option<u64>,
    pub vram_total_mb: Option<u64>,
    pub gpu_util_pct: Option<u32>,
    pub fan_pct: Option<u32>,
    pub fan_rpm: Option<u32>,
    pub pstate: Option<u32>,
    pub pcie_gen: Option<u32>,
    pub pcie_width: Option<u32>,
    pub temp_target_c: Option<u32>,
    pub temp_max_c: Option<u32>,
    pub temp_slowdown_c: Option<u32>,
    pub throttle_flags: u32,
    pub connector: Option<ConnectorSnapshot>,
}

impl GpuSnapshot {
    pub fn capabilities(&self) -> GpuCapabilities {
        let has_board_power_w = self.board_power_w.is_some();
        let has_power_limit_control =
            self.vendor == GpuVendor::Nvidia && self.min_power_limit_w.is_some();
        let has_hotspot_temp = self.hotspot_temp_c.is_some();
        let has_throttle_flags = matches!(
            self.backend,
            BackendKind::NvidiaNvmlNvapi | BackendKind::IntelIgcl
        );
        let has_per_pin_shunts = self.connector.is_some();

        let mut can = Vec::new();
        if has_board_power_w {
            can.push("board watts");
        } else if self.power_pct.is_some() {
            can.push("% TDP");
        }
        if has_hotspot_temp {
            can.push("core + hotspot temp");
        } else if self.core_temp_c.is_some() {
            can.push("core temp");
        }
        if has_throttle_flags {
            can.push("HW throttle flags");
        }
        if has_per_pin_shunts {
            can.push("6-pin 12V-2x6 shunts");
        }
        let summary = if can.is_empty() {
            "Basic WDDM adapter stats".to_string()
        } else {
            can.join(" · ")
        };

        let limitation_note = if has_per_pin_shunts {
            None
        } else {
            match self.backend {
                BackendKind::NvidiaNvmlNvapi => Some(
                    "Per-pin 12V-2x6 current needs shunt hardware (ASUS ROG Astral, WireView Pro II, or MSI Ai1300TS/Ai1600TS); public NVIDIA APIs also omit hotspot temp."
                        .to_string(),
                ),
                BackendKind::AmdAdlx => Some(
                    "Per-pin 12V-2x6 current needs inline/PSU shunt hardware (WireView Pro II or MSI Ai1300TS/Ai1600TS)."
                        .to_string(),
                ),
                BackendKind::IntelIgcl => Some(
                    "Per-pin 12V-2x6 current needs inline/PSU shunt hardware (WireView Pro II or MSI Ai1300TS/Ai1600TS)."
                        .to_string(),
                ),
                BackendKind::WindowsD3dkmt => Some(
                    "Universal WDDM fallback: no absolute watts, hardware throttle bits, or per-pin 12V-2x6 shunts."
                        .to_string(),
                ),
            }
        };

        GpuCapabilities {
            has_board_power_w,
            has_power_limit_control,
            has_hotspot_temp,
            has_throttle_flags,
            has_per_pin_shunts,
            summary,
            limitation_note,
        }
    }

    pub fn to_json(&self) -> String {
        let mut s = String::with_capacity(512);
        s.push('{');
        let _ = write!(
            s,
            "\"timestamp_ms\":{},\"backend\":\"{}\",\"vendor\":\"{}\",\"name\":\"{}\"",
            self.timestamp_ms,
            self.backend.as_str(),
            self.vendor.as_str(),
            json_escape(&self.name)
        );
        push_opt_u32(&mut s, "pci_bus", self.pci_bus);
        push_opt_f64(&mut s, "core_temp_c", self.core_temp_c);
        push_opt_f64(&mut s, "hotspot_temp_c", self.hotspot_temp_c);
        push_opt_f64(&mut s, "memory_temp_c", self.memory_temp_c);
        push_opt_f64(&mut s, "board_power_w", self.board_power_w);
        push_opt_f64(&mut s, "power_limit_w", self.power_limit_w);
        push_opt_f64(
            &mut s,
            "enforced_power_limit_w",
            self.enforced_power_limit_w,
        );
        push_opt_f64(&mut s, "min_power_limit_w", self.min_power_limit_w);
        push_opt_f64(&mut s, "max_power_limit_w", self.max_power_limit_w);
        push_opt_f64(&mut s, "power_pct", self.power_pct);
        push_opt_u32(&mut s, "gpu_clock_mhz", self.gpu_clock_mhz);
        push_opt_u32(&mut s, "mem_clock_mhz", self.mem_clock_mhz);
        push_opt_u64(&mut s, "vram_used_mb", self.vram_used_mb);
        push_opt_u64(&mut s, "vram_total_mb", self.vram_total_mb);
        push_opt_u32(&mut s, "gpu_util_pct", self.gpu_util_pct);
        push_opt_u32(&mut s, "fan_pct", self.fan_pct);
        push_opt_u32(&mut s, "fan_rpm", self.fan_rpm);
        push_opt_u32(&mut s, "pstate", self.pstate);
        push_opt_u32(&mut s, "pcie_gen", self.pcie_gen);
        push_opt_u32(&mut s, "pcie_width", self.pcie_width);
        push_opt_u32(&mut s, "temp_target_c", self.temp_target_c);
        push_opt_u32(&mut s, "temp_max_c", self.temp_max_c);
        push_opt_u32(&mut s, "temp_slowdown_c", self.temp_slowdown_c);
        let _ = write!(s, ",\"throttle_flags\":{}", self.throttle_flags);
        if let Some(conn) = &self.connector {
            s.push_str(",\"connector\":");
            push_connector_json(&mut s, conn);
        } else {
            s.push_str(",\"connector\":null");
        }
        s.push('}');
        s
    }
}

fn push_connector_json(s: &mut String, conn: &ConnectorSnapshot) {
    let _ = write!(
        s,
        "{{\"source\":\"{}\",\"connector_index\":{},\"total_current_a\":{:.3}",
        conn.source.as_str(),
        conn.connector_index,
        conn.total_current_a
    );
    push_opt_f64(s, "total_power_w", conn.total_power_w);
    push_opt_f64(s, "avg_voltage_v", conn.avg_voltage_v);
    push_opt_u32(s, "rated_cap_w", conn.rated_cap_w);
    let _ = write!(
        s,
        ",\"imbalance_spread_a\":{:.3},\"hw_fault_bits\":{},\"pins\":[",
        conn.imbalance_spread_a(),
        conn.hw_fault_bits
    );
    for (idx, pin) in conn.pins.iter().enumerate() {
        if idx > 0 {
            s.push(',');
        }
        let _ = write!(
            s,
            "{{\"pin\":{},\"current_a\":{:.3}",
            pin.pin, pin.current_a
        );
        push_opt_f64(s, "voltage_v", pin.voltage_v);
        push_opt_f64(s, "power_w", pin.power_w);
        s.push('}');
    }
    s.push_str("],\"connector_temps_c\":[");
    for (idx, t) in conn.connector_temps_c.iter().enumerate() {
        if idx > 0 {
            s.push(',');
        }
        let _ = write!(s, "{t:.2}");
    }
    s.push_str("]}");
}

pub fn json_escape(input: &str) -> String {
    let mut out = String::with_capacity(input.len());
    for ch in input.chars() {
        match ch {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => {
                let _ = write!(out, "\\u{:04x}", c as u32);
            }
            c => out.push(c),
        }
    }
    out
}

fn push_opt_f64(s: &mut String, key: &str, val: Option<f64>) {
    match val {
        Some(v) if v.is_finite() => {
            let _ = write!(s, ",\"{key}\":{v:.3}");
        }
        _ => {
            let _ = write!(s, ",\"{key}\":null");
        }
    }
}

fn push_opt_u32(s: &mut String, key: &str, val: Option<u32>) {
    match val {
        Some(v) => {
            let _ = write!(s, ",\"{key}\":{v}");
        }
        None => {
            let _ = write!(s, ",\"{key}\":null");
        }
    }
}

fn push_opt_u64(s: &mut String, key: &str, val: Option<u64>) {
    match val {
        Some(v) => {
            let _ = write!(s, ",\"{key}\":{v}");
        }
        None => {
            let _ = write!(s, ",\"{key}\":null");
        }
    }
}
