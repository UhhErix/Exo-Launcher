use std::collections::VecDeque;
use std::fmt::Write as _;

use crate::types::{GpuSnapshot, json_escape};

#[derive(Debug, Clone)]
pub struct MetricSummary {
    pub min: f64,
    pub avg: f64,
    pub max: f64,
    pub samples: usize,
}

#[derive(Debug, Clone)]
pub struct SessionSummary {
    pub gpu_name: String,
    pub backend: String,
    pub samples: usize,
    pub duration_ms: u64,
    pub power_w: Option<MetricSummary>,
    pub core_temp_c: Option<MetricSummary>,
    pub max_pin_current_a: Option<MetricSummary>,
    pub max_imbalance_a: Option<MetricSummary>,
    pub throttle_events: usize,
}

pub struct SessionRecorder {
    capacity: usize,
    ring: VecDeque<GpuSnapshot>,
}

impl SessionRecorder {
    pub fn new(capacity: usize) -> Self {
        let cap = capacity.max(1);
        Self {
            capacity: cap,
            ring: VecDeque::with_capacity(cap),
        }
    }

    pub fn push(&mut self, snap: GpuSnapshot) {
        if self.ring.len() == self.capacity {
            self.ring.pop_front();
        }
        self.ring.push_back(snap);
    }

    pub fn len(&self) -> usize {
        self.ring.len()
    }

    pub fn is_empty(&self) -> bool {
        self.ring.is_empty()
    }

    pub fn summarize(&self) -> Option<SessionSummary> {
        let first = self.ring.front()?;
        let last = self.ring.back()?;
        let duration_ms = last.timestamp_ms.saturating_sub(first.timestamp_ms);
        let power_w = summarize_series(self.ring.iter().filter_map(|s| s.board_power_w));
        let core_temp_c = summarize_series(self.ring.iter().filter_map(|s| s.core_temp_c));
        let max_pin_current_a = summarize_series(
            self.ring
                .iter()
                .filter_map(|s| s.connector.as_ref().map(|c| c.max_pin_current_a())),
        );
        let max_imbalance_a = summarize_series(
            self.ring
                .iter()
                .filter_map(|s| s.connector.as_ref().map(|c| c.imbalance_spread_a())),
        );
        let throttle_events = self.ring.iter().filter(|s| s.throttle_flags != 0).count();

        Some(SessionSummary {
            gpu_name: last.name.clone(),
            backend: last.backend.as_str().to_string(),
            samples: self.ring.len(),
            duration_ms,
            power_w,
            core_temp_c,
            max_pin_current_a,
            max_imbalance_a,
            throttle_events,
        })
    }

    pub fn export_html(&self) -> String {
        let summary = self.summarize();
        let title = summary
            .as_ref()
            .map(|s| s.gpu_name.as_str())
            .unwrap_or("No samples");

        let power_pts: Vec<f64> = self
            .ring
            .iter()
            .map(|s| s.board_power_w.unwrap_or(0.0))
            .collect();
        let temp_pts: Vec<f64> = self
            .ring
            .iter()
            .map(|s| s.core_temp_c.unwrap_or(0.0))
            .collect();

        let power_poly = svg_polyline(&power_pts, 760.0, 180.0);
        let temp_poly = svg_polyline(&temp_pts, 760.0, 180.0);

        let mut html = String::with_capacity(4096);
        let _ = write!(
            html,
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Exo Power Guard - {}</title>\
            <style>body{{background:#000;color:#ededed;font-family:system-ui,sans-serif;margin:32px}} \
            .card{{border:1px solid #262626;padding:16px;margin:16px 0;border-radius:8px}} \
            svg{{width:100%;max-width:760px;height:180px;background:#0a0a0a;border:1px solid #1f1f1f}} \
            .meta{{color:#999;font-size:13px}}</style></head><body><h1>Exo Power Guard Session</h1>\
            <p class=\"meta\">GPU: {} | Samples: {}</p>",
            html_escape(title),
            html_escape(title),
            self.ring.len()
        );
        let _ = write!(
            html,
            "<div class=\"card\"><h2>Board Power (W)</h2><svg viewBox=\"0 0 760 180\">\
            <polyline fill=\"none\" stroke=\"#38bdf8\" stroke-width=\"2\" points=\"{power_poly}\"/></svg></div>\
            <div class=\"card\"><h2>GPU Core Temperature (C)</h2><svg viewBox=\"0 0 760 180\">\
            <polyline fill=\"none\" stroke=\"#f97316\" stroke-width=\"2\" points=\"{temp_poly}\"/></svg></div>\
            <div class=\"card\"><h2>Raw Summary JSON</h2><pre>"
        );
        if let Some(sum) = &summary {
            let _ = write!(
                html,
                "{{\"gpu\":\"{}\",\"backend\":\"{}\",\"samples\":{},\"duration_ms\":{},\"throttle_events\":{}}}",
                json_escape(&sum.gpu_name),
                json_escape(&sum.backend),
                sum.samples,
                sum.duration_ms,
                sum.throttle_events
            );
        }
        html.push_str("</pre></div></body></html>");
        html
    }
}

fn summarize_series<I: Iterator<Item = f64>>(iter: I) -> Option<MetricSummary> {
    let mut min = f64::INFINITY;
    let mut max = f64::NEG_INFINITY;
    let mut sum = 0.0;
    let mut n = 0usize;
    for v in iter {
        if !v.is_finite() {
            continue;
        }
        min = min.min(v);
        max = max.max(v);
        sum += v;
        n += 1;
    }
    (n > 0).then_some(MetricSummary {
        min,
        avg: sum / n as f64,
        max,
        samples: n,
    })
}

fn svg_polyline(values: &[f64], width: f64, height: f64) -> String {
    if values.is_empty() {
        return String::new();
    }
    let min = values.iter().copied().fold(f64::INFINITY, f64::min);
    let max = values.iter().copied().fold(f64::NEG_INFINITY, f64::max);
    let span = (max - min).max(1.0);
    let step = if values.len() > 1 {
        width / (values.len() - 1) as f64
    } else {
        0.0
    };
    let mut out = String::new();
    for (i, &v) in values.iter().enumerate() {
        if i > 0 {
            out.push(' ');
        }
        let x = i as f64 * step;
        let y = height - 10.0 - ((v - min) / span) * (height - 20.0);
        let _ = write!(out, "{x:.1},{y:.1}");
    }
    out
}

fn html_escape(input: &str) -> String {
    input
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}
