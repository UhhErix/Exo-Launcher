use serde::Deserialize;
use std::collections::HashMap;

#[derive(Clone, Debug, Deserialize)]
pub struct WaapiSpec {
    pub id: u64,
    pub js_node_id: u64,
    pub duration_ms: f64,
    pub delay_ms: f64,
    pub iterations: f64,
    pub easing: String,
    pub fill: String,
    pub start_ms: f64,
    pub frames: Vec<HashMap<String, String>>,
}

#[derive(Default)]
pub struct WaapiEngine {
    pub active: Vec<WaapiSpec>,
    pub total_created: u64,
    pub total_ticks: u64,
}

impl WaapiEngine {
    pub fn add(&mut self, spec: WaapiSpec) {
        self.total_created += 1;
        self.active.retain(|a| a.id != spec.id);
        self.active.push(spec);
    }

    pub fn cancel(&mut self, id: u64) {
        self.active.retain(|a| a.id != id);
    }

    pub fn tick(&mut self, now_ms: f64) -> Vec<(u64, String, String)> {
        let mut updates = Vec::new();
        let mut keep = Vec::new();
        self.total_ticks += 1;

        for anim in self.active.drain(..) {
            if anim.frames.len() < 2 || anim.duration_ms <= 0.0 {
                continue;
            }
            let elapsed = now_ms - anim.start_ms - anim.delay_ms;
            if elapsed < 0.0 {
                keep.push(anim);
                continue;
            }
            let total_dur = if anim.iterations.is_infinite() {
                f64::INFINITY
            } else {
                anim.duration_ms * anim.iterations.max(1.0)
            };
            let finished = elapsed >= total_dur;
            let raw_t = if finished {
                1.0
            } else {
                (elapsed % anim.duration_ms) / anim.duration_ms
            };
            let eased = eval_easing(&anim.easing, raw_t.clamp(0.0, 1.0));
            let seg_count = (anim.frames.len() - 1) as f64;
            let scaled = eased * seg_count;
            let idx = (scaled.floor() as usize).min(anim.frames.len() - 2);
            let local_t = (scaled - idx as f64).clamp(0.0, 1.0);
            let f0 = &anim.frames[idx];
            let f1 = &anim.frames[idx + 1];

            for (prop, v1) in f1 {
                let v0 = f0.get(prop).map(String::as_str).unwrap_or(v1.as_str());
                let interp = interpolate_css_value(prop, v0, v1, local_t);
                updates.push((anim.js_node_id, camel_to_kebab(prop), interp));
            }

            if !finished {
                keep.push(anim);
            }
        }
        self.active = keep;
        updates
    }
}

fn eval_easing(name: &str, t: f64) -> f64 {
    match name {
        "linear" => t,
        "ease-in" => t * t * t,
        "ease-out" => 1.0 - (1.0 - t).powi(3),
        "ease-in-out" | "ease" => {
            if t < 0.5 {
                4.0 * t * t * t
            } else {
                1.0 - (-2.0 * t + 2.0).powi(3) / 2.0
            }
        }
        _ => t * t * (3.0 - 2.0 * t),
    }
}

fn camel_to_kebab(s: &str) -> String {
    if s.contains('-') {
        return s.to_string();
    }
    let mut out = String::with_capacity(s.len() + 4);
    for ch in s.chars() {
        if ch.is_ascii_uppercase() {
            out.push('-');
            out.push(ch.to_ascii_lowercase());
        } else {
            out.push(ch);
        }
    }
    out
}

fn interpolate_css_value(_prop: &str, a: &str, b: &str, t: f64) -> String {
    if let (Ok(fa), Ok(fb)) = (a.trim().parse::<f64>(), b.trim().parse::<f64>()) {
        return format!("{:.4}", fa + (fb - fa) * t);
    }
    if let (Some(na), Some(nb)) = (parse_unit(a, "px"), parse_unit(b, "px")) {
        return format!("{:.2}px", na + (nb - na) * t);
    }
    if t < 0.5 {
        a.to_string()
    } else {
        b.to_string()
    }
}

fn parse_unit(s: &str, unit: &str) -> Option<f64> {
    s.trim().strip_suffix(unit)?.trim().parse::<f64>().ok()
}
