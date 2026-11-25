use crate::model::{DeviceKind, InputSpec, LayerActivation, MacroLayer, MacroTrigger};
use std::collections::HashMap;
use std::time::{Duration, Instant};

#[derive(Clone, Debug, Default)]
pub struct ChordMatcher {
    pressed: HashMap<String, Instant>,
}

impl ChordMatcher {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn input_key(spec: &InputSpec) -> String {
        let dev = spec.device_id.as_deref().unwrap_or("*");
        let kind = match spec.device_kind {
            DeviceKind::Keyboard => "kb",
            DeviceKind::Mouse => "mouse",
            DeviceKind::Controller => "pad",
        };
        format!("{dev}:{kind}:{}", spec.code.to_ascii_lowercase())
    }

    pub fn key_down(&mut self, spec: &InputSpec, now: Instant) {
        self.pressed.insert(Self::input_key(spec), now);
    }

    pub fn key_up(&mut self, spec: &InputSpec) {
        self.pressed.remove(&Self::input_key(spec));
    }

    pub fn is_emergency_panic_chord(&self) -> bool {
        let has_esc = self
            .pressed
            .keys()
            .any(|k| k.ends_with(":kb:escape") || k.ends_with(":kb:esc"));
        let has_pause = self
            .pressed
            .keys()
            .any(|k| k.ends_with(":kb:pause") || k.ends_with(":kb:pausebreak"));
        has_esc && has_pause
    }

    pub fn matches(&self, trigger: &MacroTrigger, active_layer: &str, now: Instant) -> bool {
        if !trigger.layer_id.eq_ignore_ascii_case(active_layer) || trigger.chord.is_empty() {
            return false;
        }

        let mut earliest: Option<Instant> = None;
        let mut latest: Option<Instant> = None;

        for spec in &trigger.chord {
            let exact = Self::input_key(spec);
            let matched_at = if let Some(ts) = self.pressed.get(&exact) {
                *ts
            } else if spec.device_id.is_none() {
                let suffix = format!(
                    ":{}:{}",
                    match spec.device_kind {
                        DeviceKind::Keyboard => "kb",
                        DeviceKind::Mouse => "mouse",
                        DeviceKind::Controller => "pad",
                    },
                    spec.code.to_ascii_lowercase()
                );
                let Some((_, ts)) = self.pressed.iter().find(|(k, _)| k.ends_with(&suffix)) else {
                    return false;
                };
                *ts
            } else {
                return false;
            };

            earliest = Some(earliest.map_or(matched_at, |e| e.min(matched_at)));
            latest = Some(latest.map_or(matched_at, |l| l.max(matched_at)));
        }

        if trigger.chord.len() == 1 {
            return true;
        }

        let (Some(e), Some(l)) = (earliest, latest) else {
            return false;
        };
        let window = Duration::from_millis(u64::from(trigger.chord_window_ms.max(1)));
        let age_cap = Duration::from_millis(u64::from((trigger.chord_window_ms * 2).max(50)));
        l.saturating_duration_since(e) <= window && now.saturating_duration_since(l) <= age_cap
    }
}

#[derive(Clone, Debug)]
pub struct LayerStack {
    base_id: String,
    toggled_id: Option<String>,
    held_stack: Vec<String>,
}

impl Default for LayerStack {
    fn default() -> Self {
        Self::new("base")
    }
}

impl LayerStack {
    pub fn new(base_id: impl Into<String>) -> Self {
        Self {
            base_id: base_id.into(),
            toggled_id: None,
            held_stack: Vec::new(),
        }
    }

    pub fn active(&self) -> &str {
        self.held_stack
            .last()
            .map(String::as_str)
            .or(self.toggled_id.as_deref())
            .unwrap_or(&self.base_id)
    }

    pub fn on_input_down(&mut self, spec: &InputSpec, layers: &[MacroLayer]) {
        for layer in layers {
            let Some(ref trigger) = layer.trigger else {
                continue;
            };
            if !input_matches(spec, trigger) {
                continue;
            }
            match layer.activation {
                LayerActivation::Hold => {
                    if !self.held_stack.iter().any(|id| id == &layer.id) {
                        self.held_stack.push(layer.id.clone());
                    }
                }
                LayerActivation::Toggle => {
                    if self.toggled_id.as_deref() == Some(layer.id.as_str()) {
                        self.toggled_id = None;
                    } else {
                        self.toggled_id = Some(layer.id.clone());
                    }
                }
                LayerActivation::Base => {}
            }
        }
    }

    pub fn on_input_up(&mut self, spec: &InputSpec, layers: &[MacroLayer]) {
        for layer in layers {
            if layer.activation != LayerActivation::Hold {
                continue;
            }
            let Some(ref trigger) = layer.trigger else {
                continue;
            };
            if input_matches(spec, trigger) {
                self.held_stack.retain(|id| id != &layer.id);
            }
        }
    }
}

fn input_matches(event: &InputSpec, target: &InputSpec) -> bool {
    if event.device_kind != target.device_kind || !event.code.eq_ignore_ascii_case(&target.code) {
        return false;
    }
    match &target.device_id {
        None => true,
        Some(expected) => event
            .device_id
            .as_deref()
            .is_some_and(|actual| actual.eq_ignore_ascii_case(expected)),
    }
}
