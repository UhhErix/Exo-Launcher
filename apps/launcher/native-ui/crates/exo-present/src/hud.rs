use crate::metrics::FrameMetrics;
use serde::{Deserialize, Serialize};

pub const HUD_HEIGHT_PT: f32 = 28.0;
pub const HUD_MARGIN_PT: f32 = 10.0;
pub const HUD_PADDING_X_PT: f32 = 12.0;
pub const OLED_SHIFT_PERIOD_SECS: u64 = 180;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum HudPosition {
    Left,
    Middle,
    #[default]
    Right,
}

impl HudPosition {
    pub fn parse(value: Option<&str>) -> Self {
        match value.map(str::trim) {
            Some("left") => Self::Left,
            Some("middle") => Self::Middle,
            _ => Self::Right,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum HudItem {
    Low,
    FrameTime,
    GpuBusy,
    Latency,
    GpuUse,
    GpuTemp,
    CpuUse,
    Vram,
    Ram,
    Battery,
    Clock,
}

impl HudItem {
    pub fn from_key(key: &str) -> Option<Self> {
        match key {
            "low" => Some(Self::Low),
            "frametime" => Some(Self::FrameTime),
            "gpubusy" => Some(Self::GpuBusy),
            "latency" => Some(Self::Latency),
            "gpu" => Some(Self::GpuUse),
            "gputemp" => Some(Self::GpuTemp),
            "cpu" => Some(Self::CpuUse),
            "vram" => Some(Self::Vram),
            "ram" => Some(Self::Ram),
            "battery" => Some(Self::Battery),
            "clock" => Some(Self::Clock),
            _ => None,
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct HudExtras {
    pub gpu_percent: Option<i32>,
    pub gpu_temp_c: Option<i32>,
    pub cpu_percent: Option<i32>,
    pub vram_gb: Option<f64>,
    pub ram_gb: Option<f64>,
    pub battery: Option<(String, i32)>,
    pub clock_label: Option<String>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct HudLine {
    pub fps: u32,
    pub items_text: Option<String>,
    pub stutter_alert: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct HudState {
    pub visible: bool,
    pub position: HudPosition,
    pub line: HudLine,
    pub oled_pixel_shift: bool,
    pub elapsed_secs: u64,
    pub min_width_pt: f32,
    pub reduced_transparency: bool,
    pub high_contrast: bool,
}

impl Default for HudState {
    fn default() -> Self {
        Self {
            visible: false,
            position: HudPosition::Right,
            line: HudLine::default(),
            oled_pixel_shift: true,
            elapsed_secs: 0,
            min_width_pt: 0.0,
            reduced_transparency: false,
            high_contrast: false,
        }
    }
}

impl HudState {
    pub fn apply_line(&mut self, next: HudLine, measured_width_pt: f32) {
        if !same_parts(self.line.items_text.as_deref(), next.items_text.as_deref())
            || measured_width_pt > self.min_width_pt
        {
            self.min_width_pt = measured_width_pt;
        }
        self.line = next;
    }

    pub fn oled_shift_offset(&self) -> (f32, f32) {
        if !self.oled_pixel_shift {
            return (0.0, 0.0);
        }
        oled_shift_for_tick(self.elapsed_secs / OLED_SHIFT_PERIOD_SECS)
    }
}

pub fn oled_shift_for_tick(step: u64) -> (f32, f32) {
    const OFFSETS: [(f32, f32); 8] = [
        (0.0, 0.0),
        (1.0, 0.0),
        (2.0, 1.0),
        (1.0, 2.0),
        (0.0, 2.0),
        (-1.0, 2.0),
        (-2.0, 1.0),
        (-1.0, 0.0),
    ];
    OFFSETS[(step as usize) % OFFSETS.len()]
}

pub fn format_hud_line(metrics: &FrameMetrics, items: &[HudItem], extras: &HudExtras) -> HudLine {
    let fps = metrics.fps.round().clamp(0.0, 9999.0) as u32;
    let mut parts: Vec<String> = Vec::new();
    for &item in items {
        match item {
            HudItem::Low if metrics.low_1pct_fps > 0.5 => {
                parts.push(format!("{} LOW", metrics.low_1pct_fps.round() as u32));
            }
            HudItem::FrameTime if metrics.avg_ms > 0.05 => {
                parts.push(format!("{:.1} MS", metrics.avg_ms));
            }
            HudItem::GpuBusy => {
                if let Some(gb) = metrics.avg_gpu_busy_ms {
                    parts.push(format!("{gb:.1} GPU MS"));
                }
            }
            HudItem::Latency => {
                if let Some(lat) = metrics.avg_display_latency_ms {
                    parts.push(format!("{lat:.1} LAT"));
                }
            }
            HudItem::GpuUse => {
                if let Some(v) = extras.gpu_percent {
                    parts.push(format!("{v}% GPU"));
                }
            }
            HudItem::GpuTemp => {
                if let Some(v) = extras.gpu_temp_c {
                    parts.push(format!("{v}°C"));
                }
            }
            HudItem::CpuUse => {
                if let Some(v) = extras.cpu_percent {
                    parts.push(format!("{v}% CPU"));
                }
            }
            HudItem::Vram => {
                if let Some(v) = extras.vram_gb {
                    parts.push(format!("{v:.1} VRAM"));
                }
            }
            HudItem::Ram => {
                if let Some(v) = extras.ram_gb {
                    parts.push(format!("{v:.1} RAM"));
                }
            }
            HudItem::Battery => {
                if let Some((ref kind, pct)) = extras.battery {
                    parts.push(format!("{pct}% {kind}"));
                }
            }
            HudItem::Clock => {
                if let Some(ref c) = extras.clock_label {
                    parts.push(c.clone());
                }
            }
            _ => {}
        }
    }
    HudLine {
        fps,
        items_text: (!parts.is_empty()).then(|| format!(" · {}", parts.join(" · "))),
        stutter_alert: metrics.stutter_count > 0 && metrics.p99_ms > metrics.median_ms * 2.5,
    }
}

pub fn same_parts(a: Option<&str>, b: Option<&str>) -> bool {
    let mut ai = a.unwrap_or("").bytes().filter(|c| !c.is_ascii_digit());
    let mut bi = b.unwrap_or("").bytes().filter(|c| !c.is_ascii_digit());
    loop {
        match (ai.next(), bi.next()) {
            (None, None) => return true,
            (Some(x), Some(y)) if x == y => continue,
            _ => return false,
        }
    }
}

pub fn estimate_pill_width_pt(line: &HudLine) -> f32 {
    let fps_chars = format!("{} FPS", line.fps).len();
    let extra_chars = line
        .items_text
        .as_deref()
        .map(|s| s.chars().count())
        .unwrap_or(0);
    let total_chars = (fps_chars + extra_chars) as f32;
    (HUD_PADDING_X_PT * 2.0 + total_chars * 7.6).max(84.0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_hud_line_and_stabilizes_width() {
        let m = FrameMetrics {
            fps: 141.8,
            low_1pct_fps: 118.4,
            avg_ms: 7.05,
            median_ms: 7.0,
            p99_ms: 8.4,
            ..Default::default()
        };
        let line1 = format_hud_line(
            &m,
            &[HudItem::Low, HudItem::FrameTime],
            &HudExtras::default(),
        );
        assert_eq!(line1.fps, 142);
        assert_eq!(line1.items_text.as_deref(), Some(" · 118 LOW · 7.0 MS"));

        let mut hud = HudState::default();
        hud.apply_line(line1.clone(), 180.0);
        assert_eq!(hud.min_width_pt, 180.0);

        let line2 = HudLine {
            fps: 98,
            items_text: Some(" · 82 LOW · 10.2 MS".into()),
            stutter_alert: false,
        };
        hud.apply_line(line2, 170.0);
        assert_eq!(hud.min_width_pt, 180.0);
    }

    #[test]
    fn oled_pixel_shift_cycles_within_bounds() {
        for step in 0..16 {
            let (dx, dy) = oled_shift_for_tick(step);
            assert!(dx.abs() <= 2.0);
            assert!((0.0..=2.0).contains(&dy));
        }
    }
}
