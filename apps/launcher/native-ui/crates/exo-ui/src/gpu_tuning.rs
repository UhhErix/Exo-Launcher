use crate::events::HostEventKind;
use crate::pc_menus::{menu_btn, menu_row, menu_switch};
use crate::state::{AppState, HostStatus};
use exo_kit::{
    BG, BadgeTone, FG, RADIUS_CARD, RADIUS_PILL, SPRING_QUICK, bad, fg_faint, fg_muted,
    filter_pill, float_line, good, progress_bar, slider_track, status_badge, subtle_fill,
    type_control, type_small, warn,
};
use exo_rpc::{
    GpuFanChannelCurve, GpuFanPoint, GpuManualProfile, GpuManualTuningState, GpuStabilityProgress,
    GpuTuningCapabilities, GpuTuningLive, GpuTuningRange, GpuVfPoint, HostEvent,
    METHOD_GPU_TUNING_APPLY, METHOD_GPU_TUNING_RESET, METHOD_GPU_TUNING_STATE,
    METHOD_GPU_TUNING_TEST, METHOD_GPU_TUNING_UNDERVOLT, METHOD_GPU_TUNING_WAIVER,
};
use rune::{Color, Cursor, El, Key, LineCap, Phase, Shape, canvas, col, el, row, text};
use serde_json::json;

const CURVE_WIDTH: f32 = 420.0;
const VF_HEIGHT: f32 = 176.0;
const FAN_HEIGHT: f32 = 160.0;
const VF_PAD_L: f32 = 44.0;
const FAN_PAD_L: f32 = 40.0;
const PAD_R: f32 = 14.0;
const PAD_T: f32 = 14.0;
const PAD_B: f32 = 26.0;

pub struct GpuManualTuningUiState {
    pub state: Option<Box<GpuManualTuningState>>,
    pub draft: Option<Box<GpuManualProfile>>,
    pub duration: String,
    pub selected_fan: usize,
    pub selected_vf_point: usize,
    pub selected_fan_point: usize,
    pub dragging_slider: Option<&'static str>,
    pub dragging_vf: Option<usize>,
    pub dragging_fan: Option<usize>,
    pub error: Option<String>,
    pub waiver_busy: bool,
    pub busy: bool,
    pub armed_reset: bool,
}

impl Default for GpuManualTuningUiState {
    fn default() -> Self {
        Self {
            state: None,
            draft: None,
            duration: "60s".into(),
            selected_fan: 0,
            selected_vf_point: 2,
            selected_fan_point: 0,
            dragging_slider: None,
            dragging_vf: None,
            dragging_fan: None,
            error: None,
            waiver_busy: false,
            busy: false,
            armed_reset: false,
        }
    }
}

impl GpuManualTuningUiState {
    pub fn demo() -> Self {
        let state = demo_nvidia_state();
        let draft = draft_from_state(&state);
        Self {
            state: Some(Box::new(state)),
            draft: Some(Box::new(draft)),
            ..Self::default()
        }
    }

    pub fn sync_from_host(&mut self, next: GpuManualTuningState, reset_draft: bool) {
        if !next.ok
            && let Some(ref msg) = next.message
            && !msg.is_empty()
        {
            self.error = Some(msg.clone());
        }
        if reset_draft || self.draft.is_none() {
            let next_draft = draft_from_state(&next);
            let vf_len = next_draft.vf_curve.as_ref().map_or(0, Vec::len);
            if vf_len > 0 {
                self.selected_vf_point = vf_len / 2;
            }
            self.draft = Some(Box::new(next_draft));
        } else if let Some(ref mut d) = self.draft {
            d.mode = next.mode.clone();
        }
        self.state = Some(Box::new(next));
    }
}

pub fn default_fan_points() -> Vec<GpuFanPoint> {
    vec![
        GpuFanPoint {
            temp_c: 35,
            speed_percent: 30,
        },
        GpuFanPoint {
            temp_c: 50,
            speed_percent: 45,
        },
        GpuFanPoint {
            temp_c: 65,
            speed_percent: 65,
        },
        GpuFanPoint {
            temp_c: 78,
            speed_percent: 85,
        },
        GpuFanPoint {
            temp_c: 85,
            speed_percent: 100,
        },
    ]
}

fn pick_range(primary: Option<i32>, fallback: Option<i32>, range: &GpuTuningRange) -> Option<i32> {
    if range.supported {
        Some(primary.or(fallback).unwrap_or(range.default_val))
    } else {
        None
    }
}

fn initial_fan_points(src: &GpuManualProfile, def: &GpuManualProfile) -> Vec<GpuFanPoint> {
    if let Some(ref pts) = src.fan_curve
        && !pts.is_empty()
    {
        return pts.clone();
    }
    if let Some(ref pts) = def.fan_curve
        && !pts.is_empty()
    {
        return pts.clone();
    }
    default_fan_points()
}

pub fn draft_from_state(state: &GpuManualTuningState) -> GpuManualProfile {
    let caps = &state.capabilities;
    let src = if state.mode == "manual" {
        &state.saved
    } else {
        &state.current
    };
    let def = &state.factory_defaults;
    let has_fan = caps.supports_hardware_fan_curve || caps.supports_software_fan_curve;
    let vf = src
        .vf_curve
        .as_ref()
        .filter(|v| !v.is_empty())
        .cloned()
        .unwrap_or_else(|| caps.vf_curve_points.clone());

    GpuManualProfile {
        mode: if state.mode == "manual" {
            "manual".into()
        } else {
            "auto".into()
        },
        core_clock_offset_mhz: pick_range(
            src.core_clock_offset_mhz,
            def.core_clock_offset_mhz,
            &caps.core_clock_offset_mhz,
        ),
        core_min_clock_mhz: pick_range(
            src.core_min_clock_mhz,
            def.core_min_clock_mhz,
            &caps.core_min_clock_mhz,
        ),
        core_max_clock_mhz: pick_range(
            src.core_max_clock_mhz,
            def.core_max_clock_mhz,
            &caps.core_max_clock_mhz,
        ),
        voltage_mv: pick_range(src.voltage_mv, def.voltage_mv, &caps.voltage_mv),
        voltage_offset_mv: pick_range(
            src.voltage_offset_mv,
            def.voltage_offset_mv,
            &caps.voltage_offset_mv,
        ),
        memory_clock_offset_mhz: pick_range(
            src.memory_clock_offset_mhz,
            def.memory_clock_offset_mhz,
            &caps.memory_clock_offset_mhz,
        ),
        memory_max_clock_mhz: pick_range(
            src.memory_max_clock_mhz,
            def.memory_max_clock_mhz,
            &caps.memory_max_clock_mhz,
        ),
        memory_timing_level: if caps.memory_timing_levels.is_empty() {
            None
        } else {
            src.memory_timing_level
                .clone()
                .or_else(|| def.memory_timing_level.clone())
                .or_else(|| caps.memory_timing_levels.first().cloned())
        },
        power_limit_percent: pick_range(
            src.power_limit_percent,
            def.power_limit_percent,
            &caps.power_limit_percent,
        ),
        power_limit_watts: pick_range(
            src.power_limit_watts,
            def.power_limit_watts,
            &caps.power_limit_watts,
        ),
        temp_limit_c: pick_range(src.temp_limit_c, def.temp_limit_c, &caps.temp_limit_c),
        clock_lock_mhz: pick_range(src.clock_lock_mhz, def.clock_lock_mhz, &caps.clock_lock_mhz),
        vf_curve: if caps.supports_curve_editor {
            Some(vf)
        } else {
            None
        },
        zero_rpm: if caps.supports_zero_rpm {
            Some(src.zero_rpm.or(def.zero_rpm).unwrap_or(true))
        } else {
            None
        },
        fan_curve: if has_fan {
            Some(initial_fan_points(src, def))
        } else {
            None
        },
        fan_channels: src.fan_channels.clone(),
    }
}

pub fn clamp_to_range(value: i32, range: &GpuTuningRange) -> i32 {
    let clamped = value.clamp(range.min, range.max);
    let step = range.step.max(1);
    let snapped = range.min + (((clamped - range.min) as f64 / step as f64).round() as i32) * step;
    snapped.clamp(range.min, range.max)
}

pub fn format_range_value(value: i32, unit: &str, signed: bool) -> String {
    if signed && value > 0 {
        format!("+{value} {unit}")
    } else {
        format!("{value} {unit}")
    }
}

pub fn needs_waiver(caps: &GpuTuningCapabilities) -> bool {
    caps.requires_waiver && !caps.waiver_accepted
}

pub fn controls_disabled(state: &GpuManualTuningState, draft: &GpuManualProfile) -> bool {
    state.capabilities.locked_by_maker
        || needs_waiver(&state.capabilities)
        || state.test.running
        || draft.mode != "manual"
}

pub fn status_color(state: &GpuManualTuningState) -> Color {
    if state.paused_after_crash || state.queued_pending || state.capabilities.locked_by_maker {
        warn()
    } else if state.mode == "manual" {
        good()
    } else {
        fg_muted()
    }
}

pub fn lock_detail(caps: &GpuTuningCapabilities) -> String {
    caps.lock_reason
        .as_deref()
        .filter(|s| !s.is_empty())
        .unwrap_or("Locked by your GPU maker")
        .to_string()
}

#[cfg(test)]
pub fn visible_slider_keys(caps: &GpuTuningCapabilities) -> Vec<&'static str> {
    let mut keys = Vec::new();
    let pairs: [(&'static str, &GpuTuningRange); 11] = [
        ("coreClockOffsetMhz", &caps.core_clock_offset_mhz),
        ("coreMinClockMhz", &caps.core_min_clock_mhz),
        ("coreMaxClockMhz", &caps.core_max_clock_mhz),
        ("voltageMv", &caps.voltage_mv),
        ("voltageOffsetMv", &caps.voltage_offset_mv),
        ("memoryClockOffsetMhz", &caps.memory_clock_offset_mhz),
        ("memoryMaxClockMhz", &caps.memory_max_clock_mhz),
        ("powerLimitPercent", &caps.power_limit_percent),
        ("powerLimitWatts", &caps.power_limit_watts),
        ("tempLimitC", &caps.temp_limit_c),
        ("clockLockMhz", &caps.clock_lock_mhz),
    ];
    for (k, r) in pairs {
        if r.supported {
            keys.push(k);
        }
    }
    keys
}

pub fn flatten_vf_from(
    points: &[GpuVfPoint],
    selected: usize,
    range: &GpuTuningRange,
) -> Vec<GpuVfPoint> {
    let Some(anchor) = points.get(selected) else {
        return points.to_vec();
    };
    let ceiling = anchor.base_clock_mhz + anchor.offset_mhz;
    points
        .iter()
        .enumerate()
        .map(|(idx, pt)| {
            if idx < selected {
                pt.clone()
            } else {
                let desired = clamp_to_range(ceiling - pt.base_clock_mhz, range);
                GpuVfPoint {
                    offset_mhz: desired,
                    ..pt.clone()
                }
            }
        })
        .collect()
}

pub fn update_fan_point(
    points: &[GpuFanPoint],
    idx: usize,
    raw_temp: i32,
    raw_speed: i32,
    temp_range: &GpuTuningRange,
    speed_range: &GpuTuningRange,
) -> Vec<GpuFanPoint> {
    let min_t = if temp_range.supported {
        temp_range.min
    } else {
        30
    };
    let max_t = if temp_range.supported {
        temp_range.max
    } else {
        100
    };
    let min_s = if speed_range.supported {
        speed_range.min
    } else {
        0
    };
    let max_s = if speed_range.supported {
        speed_range.max
    } else {
        100
    };
    let prev = idx.checked_sub(1).and_then(|i| points.get(i));
    let next = points.get(idx + 1);
    let lo_t = prev.map_or(min_t, |p| p.temp_c + 1);
    let hi_t = next.map_or(max_t, |p| p.temp_c - 1);
    let lo_s = prev.map_or(min_s, |p| p.speed_percent);
    let hi_s = next.map_or(max_s, |p| p.speed_percent);
    let clamped_t = raw_temp.clamp(lo_t, hi_t.max(lo_t));
    let clamped_s = raw_speed.clamp(lo_s, hi_s.max(lo_s));
    points
        .iter()
        .enumerate()
        .map(|(i, pt)| {
            if i == idx {
                GpuFanPoint {
                    temp_c: clamped_t,
                    speed_percent: clamped_s,
                }
            } else {
                pt.clone()
            }
        })
        .collect()
}

fn active_fan_points(draft: &GpuManualProfile, selected_fan: usize) -> Vec<GpuFanPoint> {
    if let Some(ref channels) = draft.fan_channels
        && let Some(ch) = channels
            .iter()
            .find(|c| c.fan_index == selected_fan as i32 && !c.points.is_empty())
    {
        return ch.points.clone();
    }
    draft
        .fan_curve
        .as_ref()
        .filter(|v| !v.is_empty())
        .cloned()
        .unwrap_or_else(default_fan_points)
}

fn apply_fan_points_to_draft(
    draft: &mut GpuManualProfile,
    fan_count: i32,
    selected_fan: usize,
    next_points: Vec<GpuFanPoint>,
) {
    if fan_count <= 1 {
        draft.fan_curve = Some(next_points);
        return;
    }
    let fallback = draft
        .fan_curve
        .as_ref()
        .filter(|v| !v.is_empty())
        .cloned()
        .unwrap_or_else(default_fan_points);
    let mut channels = draft.fan_channels.clone().unwrap_or_else(|| {
        (0..fan_count)
            .map(|idx| GpuFanChannelCurve {
                fan_index: idx,
                manual: true,
                points: fallback.clone(),
            })
            .collect()
    });
    for ch in &mut channels {
        if ch.fan_index == selected_fan as i32 {
            ch.manual = true;
            ch.points = next_points.clone();
        }
    }
    if selected_fan == 0 {
        draft.fan_curve = Some(next_points);
    }
    draft.fan_channels = Some(channels);
}

fn apply_vf_points_to_draft(
    draft: &mut GpuManualProfile,
    vendor: &str,
    next_points: Vec<GpuVfPoint>,
) {
    if vendor == "amd"
        && let Some(first) = next_points.first()
    {
        draft.voltage_offset_mv = Some(first.offset_mhz);
    }
    draft.vf_curve = Some(next_points);
}

pub fn register_events(state: &mut AppState) {
    state.register_event(HostEventKind::GpuTuningChanged, |s, ev| {
        let HostEvent::GpuTuningChanged(changed) = ev else {
            return false;
        };
        if let Some(ref mut st) = s.pc.gpu_tuning_ui.state {
            st.mode = changed.mode;
            st.status_line = changed.status_line;
            st.paused_after_crash = changed.paused_after_crash;
            st.queued_pending = changed.queued_pending;
            st.queued_reason = changed.queued_reason;
        }
        if s.status == HostStatus::Connected {
            s.load_gpu_manual_tuning();
        }
        true
    });

    state.register_event(HostEventKind::GpuTuningTestProgress, |s, ev| {
        let HostEvent::GpuTuningTestProgress(progress) = ev else {
            return false;
        };
        if let Some(ref mut st) = s.pc.gpu_tuning_ui.state {
            st.test = *progress;
            return true;
        }
        false
    });
}

impl AppState {
    pub fn load_gpu_manual_tuning(&mut self) {
        if self.status != HostStatus::Connected {
            if self.status == HostStatus::Standalone && self.pc.gpu_tuning_ui.state.is_none() {
                self.pc.gpu_tuning_ui = GpuManualTuningUiState::demo();
            }
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, GpuManualTuningState>(METHOD_GPU_TUNING_STATE, &json!({}), move |res| {
                handle.update(move |s, _| {
                    if let Ok(next) = res {
                        s.pc.gpu_tuning_ui.sync_from_host(next, true);
                    }
                });
            });
        }
    }

    pub fn gpu_tuning_accept_waiver(&mut self) {
        let device_id = self
            .pc
            .gpu_tuning_ui
            .state
            .as_ref()
            .map(|st| st.capabilities.device_id.clone())
            .unwrap_or_default();
        self.pc.gpu_tuning_ui.error = None;
        if self.status != HostStatus::Connected {
            if let Some(ref mut st) = self.pc.gpu_tuning_ui.state {
                st.capabilities.waiver_accepted = true;
            }
            return;
        }
        self.pc.gpu_tuning_ui.waiver_busy = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, GpuManualTuningState>(
                METHOD_GPU_TUNING_WAIVER,
                &json!({ "accept": true, "deviceId": device_id }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.gpu_tuning_ui.waiver_busy = false;
                        match res {
                            Ok(next) => s.pc.gpu_tuning_ui.sync_from_host(next, true),
                            Err(_) => {
                                s.pc.gpu_tuning_ui.error =
                                    Some("Could not save waiver consent. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn gpu_tuning_apply(&mut self) {
        let Some(draft) = self.pc.gpu_tuning_ui.draft.clone() else {
            return;
        };
        self.pc.gpu_tuning_ui.error = None;
        self.pc.gpu_tuning_ui.armed_reset = false;
        if self.status != HostStatus::Connected {
            if let Some(ref mut st) = self.pc.gpu_tuning_ui.state {
                st.mode = draft.mode.clone();
                st.current = draft.as_ref().clone();
                st.saved = *draft;
                st.paused_after_crash = false;
                st.status_line = format!(
                    "{} · {}",
                    if st.mode == "manual" {
                        "Manual active"
                    } else {
                        "Auto (stock)"
                    },
                    st.capabilities.backend.to_uppercase()
                );
            }
            return;
        }
        self.pc.gpu_tuning_ui.busy = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, GpuManualTuningState>(METHOD_GPU_TUNING_APPLY, &draft, move |res| {
                handle.update(move |s, _| {
                    s.pc.gpu_tuning_ui.busy = false;
                    match res {
                        Ok(next) => {
                            let ok = next.ok;
                            s.pc.gpu_tuning_ui.sync_from_host(next, ok);
                        }
                        Err(_) => {
                            s.pc.gpu_tuning_ui.error = Some("Could not apply GPU tuning.".into());
                        }
                    }
                });
            });
        }
    }

    pub fn gpu_tuning_reapply(&mut self) {
        self.pc.gpu_tuning_ui.error = None;
        self.pc.gpu_tuning_ui.armed_reset = false;
        if self.status != HostStatus::Connected {
            if let Some(ref mut st) = self.pc.gpu_tuning_ui.state {
                st.mode = "manual".into();
                st.paused_after_crash = false;
                st.status_line =
                    format!("Manual active · {}", st.capabilities.backend.to_uppercase());
            }
            return;
        }
        self.pc.gpu_tuning_ui.busy = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, GpuManualTuningState>(
                METHOD_GPU_TUNING_APPLY,
                &json!({ "mode": "manual", "reapply": true }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.gpu_tuning_ui.busy = false;
                        match res {
                            Ok(next) => {
                                let ok = next.ok;
                                s.pc.gpu_tuning_ui.sync_from_host(next, ok);
                            }
                            Err(_) => {
                                s.pc.gpu_tuning_ui.error =
                                    Some("Could not re-apply saved tuning.".into());
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn gpu_tuning_reset(&mut self) {
        self.pc.gpu_tuning_ui.error = None;
        self.pc.gpu_tuning_ui.armed_reset = false;
        if self.status != HostStatus::Connected {
            if let Some(ref mut st) = self.pc.gpu_tuning_ui.state {
                st.mode = "auto".into();
                st.current = st.factory_defaults.clone();
                st.saved = st.factory_defaults.clone();
                st.paused_after_crash = false;
                st.status_line =
                    format!("Auto (stock) · {}", st.capabilities.backend.to_uppercase());
                let next_draft = draft_from_state(st);
                self.pc.gpu_tuning_ui.draft = Some(Box::new(next_draft));
            }
            return;
        }
        self.pc.gpu_tuning_ui.busy = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, GpuManualTuningState>(METHOD_GPU_TUNING_RESET, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.pc.gpu_tuning_ui.busy = false;
                    match res {
                        Ok(next) => s.pc.gpu_tuning_ui.sync_from_host(next, true),
                        Err(_) => {
                            s.pc.gpu_tuning_ui.error = Some("Reset failed.".into());
                        }
                    }
                });
            });
        }
    }

    pub fn gpu_tuning_toggle_test(&mut self) {
        let Some(ref st) = self.pc.gpu_tuning_ui.state else {
            return;
        };
        let running = st.test.running;
        let duration = self.pc.gpu_tuning_ui.duration.clone();
        let candidate = self
            .pc
            .gpu_tuning_ui
            .draft
            .as_deref()
            .filter(|d| d.mode == "manual")
            .cloned();
        self.pc.gpu_tuning_ui.error = None;
        if self.status != HostStatus::Connected {
            if let Some(ref mut st) = self.pc.gpu_tuning_ui.state {
                if running {
                    st.test = GpuStabilityProgress::default();
                } else {
                    st.test = GpuStabilityProgress {
                        running: false,
                        kind: "stability".into(),
                        phase: "passed".into(),
                        elapsed_sec: 60,
                        total_sec: 60,
                        passes: 120,
                        checksum_ok: true,
                        passed: Some(true),
                        summary: Some(
                            "Passed 120 compute passes (60 s) with zero artifacts.".into(),
                        ),
                        ..Default::default()
                    };
                }
            }
            return;
        }
        let body = if running {
            json!({ "action": "stop" })
        } else if let Some(prof) = candidate {
            json!({ "action": "start", "duration": duration, "profile": prof })
        } else {
            json!({ "action": "start", "duration": duration })
        };
        self.pc.gpu_tuning_ui.busy = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, GpuManualTuningState>(METHOD_GPU_TUNING_TEST, &body, move |res| {
                handle.update(move |s, _| {
                    s.pc.gpu_tuning_ui.busy = false;
                    match res {
                        Ok(next) => s.pc.gpu_tuning_ui.sync_from_host(next, false),
                        Err(_) => {
                            s.pc.gpu_tuning_ui.error =
                                Some("Stability test could not start.".into());
                        }
                    }
                });
            });
        }
    }

    pub fn gpu_tuning_toggle_undervolt(&mut self) {
        let Some(ref st) = self.pc.gpu_tuning_ui.state else {
            return;
        };
        let running = st.test.running;
        self.pc.gpu_tuning_ui.error = None;
        if self.status != HostStatus::Connected {
            if let Some(ref mut st) = self.pc.gpu_tuning_ui.state {
                if running {
                    st.test = GpuStabilityProgress::default();
                } else {
                    st.test = GpuStabilityProgress {
                        running: false,
                        kind: "undervolt".into(),
                        phase: "passed".into(),
                        elapsed_sec: 4,
                        total_sec: 5,
                        passes: 4,
                        checksum_ok: true,
                        passed: Some(true),
                        summary: Some(
                            "Stopped at step 5, backed off 2 steps to +45 MHz undervolt.".into(),
                        ),
                        ..Default::default()
                    };
                }
            }
            return;
        }
        let action = if running { "stop" } else { "start" };
        self.pc.gpu_tuning_ui.busy = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, GpuManualTuningState>(
                METHOD_GPU_TUNING_UNDERVOLT,
                &json!({ "action": action }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.gpu_tuning_ui.busy = false;
                        match res {
                            Ok(next) => s.pc.gpu_tuning_ui.sync_from_host(next, false),
                            Err(_) => {
                                s.pc.gpu_tuning_ui.error =
                                    Some("Undervolt search could not start.".into());
                            }
                        }
                    });
                },
            );
        }
    }
}

fn circle_pts(cx: f32, cy: f32, r: f32, steps: usize) -> Vec<[f32; 2]> {
    (0..=steps)
        .map(|i| {
            let a = (i as f32 / steps as f32) * std::f32::consts::TAU;
            [cx + r * a.cos(), cy + r * a.sin()]
        })
        .collect()
}

fn add_dashed_polyline(mut shape: Shape, pts: &[[f32; 2]], width: f32, color: Color) -> Shape {
    for seg in pts.windows(2) {
        let [x0, y0] = seg[0];
        let [x1, y1] = seg[1];
        let dx = x1 - x0;
        let dy = y1 - y0;
        let dist = (dx * dx + dy * dy).sqrt();
        if dist < 1.0 {
            continue;
        }
        let dash = 5.0_f32;
        let gap = 4.0_f32;
        let step = dash + gap;
        let mut t = 0.0_f32;
        while t < dist {
            let t_end = (t + dash).min(dist);
            let a = [x0 + dx * (t / dist), y0 + dy * (t / dist)];
            let b = [x0 + dx * (t_end / dist), y0 + dy * (t_end / dist)];
            shape = shape.line(a, b, width, LineCap::Butt, color);
            t += step;
        }
    }
    shape
}

#[allow(clippy::too_many_arguments)]
fn range_slider_row(
    key: &'static str,
    label: &'static str,
    range: &GpuTuningRange,
    value: Option<i32>,
    unit: &'static str,
    signed: bool,
    disabled: bool,
    dragging: bool,
    on_change: impl Fn(&mut AppState, i32) + Clone + 'static,
) -> Option<El> {
    if !range.supported {
        return None;
    }
    let current = value.unwrap_or(range.default_val);
    let span = (range.max - range.min).max(1) as f32;
    let frac = ((current - range.min) as f32 / span).clamp(0.0, 1.0);
    let val_text = format_range_value(current, unit, signed);
    let r_copy = range.clone();
    let cb = on_change.clone();
    let slider = slider_track(
        format!("gpu-tune-slider-{key}"),
        label,
        140.0,
        frac,
        dragging,
        disabled,
    )
    .on_drag(move |s: &mut AppState, d, _| {
        if disabled {
            return;
        }
        let ratio = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
        let raw = r_copy.min + (ratio * (r_copy.max - r_copy.min) as f32).round() as i32;
        let snapped = clamp_to_range(raw, &r_copy);
        match d.phase {
            Phase::Down | Phase::Move => {
                s.pc.gpu_tuning_ui.dragging_slider = Some(key);
                cb(s, snapped);
            }
            Phase::Up => {
                s.pc.gpu_tuning_ui.dragging_slider = None;
                if !d.cancelled() {
                    cb(s, snapped);
                }
            }
        }
    });

    Some(menu_row(
        label,
        Some(format!("{} to {} {unit}", range.min, range.max)),
        None,
        row().gap(10.0).center_y().child(slider).child(
            el().w(74.0)
                .child(text(val_text).font(type_small()).color(FG)),
        ),
    ))
}

fn status_header(state: &GpuManualTuningState) -> El {
    let live = &state.live;
    let mut telemetry = row().gap(10.0).center_y();
    if let Some(t) = live.temp_c {
        telemetry = telemetry.child(
            text(format!("{} °C", t.round() as i32))
                .font(type_small())
                .color(fg_faint()),
        );
    }
    if let Some(clk) = live.clock_mhz {
        telemetry = telemetry.child(
            text(format!("{clk} MHz"))
                .font(type_small())
                .color(fg_faint()),
        );
    }
    if let Some(w) = live.watts {
        telemetry = telemetry.child(
            text(format!("{} W", w.round() as i32))
                .font(type_small())
                .color(fg_faint()),
        );
    }
    row()
        .key("gpu-tune-status")
        .w_full()
        .min_h(24.0)
        .py(4.0)
        .gap(12.0)
        .justify_between()
        .center_y()
        .child(
            text(state.status_line.clone())
                .font(type_small())
                .color(status_color(state))
                .wrap(),
        )
        .child(telemetry)
}

fn vf_curve_view(
    points: &[GpuVfPoint],
    offset_range: &GpuTuningRange,
    is_amd: bool,
    selected: usize,
    disabled: bool,
) -> Option<El> {
    if points.is_empty() {
        return None;
    }
    let sel = selected.min(points.len() - 1);
    let active = &points[sel];
    let min_v = points.iter().map(|p| p.voltage_mv).min().unwrap_or(750);
    let max_v = points
        .iter()
        .map(|p| p.voltage_mv)
        .max()
        .unwrap_or(1050)
        .max(min_v + 50);
    let min_f = points
        .iter()
        .map(|p| p.base_clock_mhz)
        .min()
        .unwrap_or(1000)
        .saturating_sub(200)
        .max(300);
    let max_f = points
        .iter()
        .map(|p| p.base_clock_mhz + p.offset_mhz)
        .max()
        .unwrap_or(2100)
        + 250;

    let plot_w = CURVE_WIDTH - VF_PAD_L - PAD_R;
    let plot_h = VF_HEIGHT - PAD_T - PAD_B;
    let x_for_v =
        move |v: i32| VF_PAD_L + ((v - min_v) as f32 / (max_v - min_v).max(1) as f32) * plot_w;
    let y_for_f = move |f: i32| {
        PAD_T + plot_h - ((f - min_f) as f32 / (max_f - min_f).max(1) as f32) * plot_h
    };

    let mut shape = Shape::new().view_box(CURVE_WIDTH, VF_HEIGHT);
    for frac in [0.0_f32, 0.25, 0.5, 0.75, 1.0] {
        let y = PAD_T + plot_h * frac;
        shape = shape.line(
            [VF_PAD_L, y],
            [CURVE_WIDTH - PAD_R, y],
            1.0,
            LineCap::Butt,
            float_line(),
        );
    }

    let base_pts: Vec<[f32; 2]> = points
        .iter()
        .map(|p| [x_for_v(p.voltage_mv), y_for_f(p.base_clock_mhz)])
        .collect();
    let active_pts: Vec<[f32; 2]> = points
        .iter()
        .map(|p| {
            [
                x_for_v(p.voltage_mv),
                y_for_f(p.base_clock_mhz + p.offset_mhz),
            ]
        })
        .collect();

    shape = add_dashed_polyline(shape, &base_pts, 1.5, fg_faint());
    shape = shape.stroke(active_pts.clone(), 2.0, FG);

    for (idx, [cx, cy]) in active_pts.iter().enumerate() {
        let is_sel = idx == sel;
        let r = if is_sel { 6.0 } else { 4.5 };
        let fill_col = if is_sel { good() } else { BG };
        let ring = circle_pts(*cx, *cy, r, 16);
        shape = shape.fill(ring.clone(), fill_col);
        shape = shape.stroke(ring, 2.0, FG);
    }

    let pts_for_drag = points.to_vec();
    let range_drag = offset_range.clone();
    let range_key = offset_range.clone();
    let range_flat = offset_range.clone();

    let mut plot_overlay = el()
        .key("gpu-vf-curve-plot")
        .role("slider")
        .label("Voltage-frequency curve editor")
        .focusable(!disabled)
        .disabled(disabled)
        .hit()
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .w_full()
        .h(VF_HEIGHT)
        .radius(RADIUS_CARD)
        .fill(BG)
        .border(1.0, float_line())
        .child(canvas(shape).w_full().h(VF_HEIGHT))
        .on_drag(move |s: &mut AppState, d, _| {
            if disabled || pts_for_drag.is_empty() {
                return;
            }
            let svg_x = (d.x / d.w.max(1.0)) * CURVE_WIDTH;
            let svg_y = (d.y / d.h.max(1.0)) * VF_HEIGHT;
            let idx = match d.phase {
                Phase::Down => {
                    let mut best = 0usize;
                    let mut best_dist = f32::MAX;
                    for (i, pt) in pts_for_drag.iter().enumerate() {
                        let px = x_for_v(pt.voltage_mv);
                        let dist = (px - svg_x).abs();
                        if dist < best_dist {
                            best_dist = dist;
                            best = i;
                        }
                    }
                    s.pc.gpu_tuning_ui.selected_vf_point = best;
                    s.pc.gpu_tuning_ui.dragging_vf = Some(best);
                    best
                }
                Phase::Move => s.pc.gpu_tuning_ui.dragging_vf.unwrap_or(sel),
                Phase::Up => {
                    let cur = s.pc.gpu_tuning_ui.dragging_vf.take();
                    if d.cancelled() {
                        return;
                    }
                    cur.unwrap_or(sel)
                }
            };
            let clamped_y = svg_y.clamp(PAD_T, PAD_T + plot_h);
            let target_f = min_f as f32
                + ((PAD_T + plot_h - clamped_y) / plot_h.max(1.0)) * (max_f - min_f) as f32;
            if let Some(ref mut draft) = s.pc.gpu_tuning_ui.draft
                && let Some(ref cur_pts) = draft.vf_curve
                && let Some(pt) = cur_pts.get(idx)
            {
                let next_off =
                    clamp_to_range(target_f.round() as i32 - pt.base_clock_mhz, &range_drag);
                let updated: Vec<GpuVfPoint> = cur_pts
                    .iter()
                    .enumerate()
                    .map(|(i, item)| {
                        if is_amd || i == idx {
                            GpuVfPoint {
                                offset_mhz: next_off,
                                ..item.clone()
                            }
                        } else {
                            item.clone()
                        }
                    })
                    .collect();
                let vendor = if is_amd { "amd" } else { "nvidia" };
                apply_vf_points_to_draft(draft, vendor, updated);
            }
        })
        .on_key(move |s: &mut AppState, ev, _| {
            if disabled {
                return false;
            }
            let step = range_key.step.max(1);
            let delta = match ev.key {
                Key::Up => step,
                Key::Down => -step,
                Key::Left => {
                    s.pc.gpu_tuning_ui.selected_vf_point =
                        s.pc.gpu_tuning_ui.selected_vf_point.saturating_sub(1);
                    return true;
                }
                Key::Right => {
                    if let Some(ref draft) = s.pc.gpu_tuning_ui.draft
                        && let Some(ref pts) = draft.vf_curve
                        && !pts.is_empty()
                    {
                        s.pc.gpu_tuning_ui.selected_vf_point =
                            (s.pc.gpu_tuning_ui.selected_vf_point + 1).min(pts.len() - 1);
                    }
                    return true;
                }
                _ => return false,
            };
            let idx = s.pc.gpu_tuning_ui.selected_vf_point;
            if let Some(ref mut draft) = s.pc.gpu_tuning_ui.draft
                && let Some(ref cur_pts) = draft.vf_curve
                && let Some(pt) = cur_pts.get(idx)
            {
                let next_off = clamp_to_range(pt.offset_mhz + delta, &range_key);
                let updated: Vec<GpuVfPoint> = cur_pts
                    .iter()
                    .enumerate()
                    .map(|(i, item)| {
                        if is_amd || i == idx {
                            GpuVfPoint {
                                offset_mhz: next_off,
                                ..item.clone()
                            }
                        } else {
                            item.clone()
                        }
                    })
                    .collect();
                let vendor = if is_amd { "amd" } else { "nvidia" };
                apply_vf_points_to_draft(draft, vendor, updated);
            }
            true
        });

    for (frac_idx, frac) in [0.0_f32, 0.5, 1.0].iter().enumerate() {
        let freq = (max_f as f32 - frac * (max_f - min_f) as f32).round() as i32;
        let top = PAD_T + plot_h * frac - 6.0;
        plot_overlay = plot_overlay.child(
            el().key(format!("gpu-vf-y-{frac_idx}"))
                .absolute(6.0, top.max(2.0))
                .child(text(format!("{freq}")).font(type_small()).color(fg_faint())),
        );
    }

    let x_labels = row()
        .w_full()
        .px(12.0)
        .justify_between()
        .child(
            text(format!("{} mV", points[0].voltage_mv))
                .font(type_small())
                .color(fg_faint()),
        )
        .child(
            text(format!("{} mV", points[points.len() / 2].voltage_mv))
                .font(type_small())
                .color(fg_faint()),
        )
        .child(
            text(format!("{} mV", points[points.len() - 1].voltage_mv))
                .font(type_small())
                .color(fg_faint()),
        );

    let off_txt = if active.offset_mhz >= 0 {
        format!("+{}", active.offset_mhz)
    } else {
        format!("{}", active.offset_mhz)
    };
    let meta_txt = format!(
        "{} mV · {} MHz ({off_txt} {})",
        active.voltage_mv,
        active.base_clock_mhz + active.offset_mhz,
        if is_amd { "mV" } else { "MHz" }
    );

    let mut meta_row = row()
        .gap(8.0)
        .center_y()
        .child(text(meta_txt).font(type_small()).color(fg_muted()));
    if !is_amd {
        meta_row = meta_row.child(
            menu_btn(
                "gpu-vf-flatten".into(),
                "Flatten right",
                false,
                false,
                disabled,
            )
            .on_press(move |s: &mut AppState| {
                if disabled {
                    return;
                }
                let idx = s.pc.gpu_tuning_ui.selected_vf_point;
                if let Some(ref mut draft) = s.pc.gpu_tuning_ui.draft
                    && let Some(ref cur_pts) = draft.vf_curve
                {
                    let next = flatten_vf_from(cur_pts, idx, &range_flat);
                    draft.vf_curve = Some(next);
                }
            }),
        );
    }

    Some(
        col()
            .key("gpu-vf-curve")
            .w_full()
            .pt(10.0)
            .pb(4.0)
            .gap(8.0)
            .border_sides([1.0, 0.0, 0.0, 0.0], float_line())
            .child(
                row()
                    .w_full()
                    .justify_between()
                    .center_y()
                    .gap(8.0)
                    .child(
                        text(if is_amd {
                            "Voltage-frequency curve offset"
                        } else {
                            "Voltage-frequency curve"
                        })
                        .font(type_control())
                        .color(FG),
                    )
                    .child(meta_row),
            )
            .child(plot_overlay)
            .child(x_labels),
    )
}

#[allow(clippy::too_many_arguments)]
fn fan_curve_view(
    points: &[GpuFanPoint],
    temp_range: &GpuTuningRange,
    speed_range: &GpuTuningRange,
    fan_count: i32,
    selected_fan: usize,
    selected_pt: usize,
    supports_zero_rpm: bool,
    zero_rpm: bool,
    needs_resident: bool,
    disabled: bool,
) -> Option<El> {
    if points.is_empty() {
        return None;
    }
    let sel = selected_pt.min(points.len() - 1);
    let active = &points[sel];
    let min_t = if temp_range.supported {
        temp_range.min
    } else {
        30
    };
    let max_t = if temp_range.supported {
        temp_range.max
    } else {
        100
    };
    let min_s = if speed_range.supported {
        speed_range.min
    } else {
        0
    };
    let max_s = if speed_range.supported {
        speed_range.max
    } else {
        100
    };

    let plot_w = CURVE_WIDTH - FAN_PAD_L - PAD_R;
    let plot_h = FAN_HEIGHT - PAD_T - PAD_B;
    let x_for_t =
        move |t: i32| FAN_PAD_L + ((t - min_t) as f32 / (max_t - min_t).max(1) as f32) * plot_w;
    let y_for_s = move |sp: i32| {
        PAD_T + plot_h - ((sp - min_s) as f32 / (max_s - min_s).max(1) as f32) * plot_h
    };

    let mut shape = Shape::new().view_box(CURVE_WIDTH, FAN_HEIGHT);
    for pct in [0, 25, 50, 75, 100] {
        let y = y_for_s(pct);
        shape = shape.line(
            [FAN_PAD_L, y],
            [CURVE_WIDTH - PAD_R, y],
            1.0,
            LineCap::Butt,
            float_line(),
        );
    }

    let line_pts: Vec<[f32; 2]> = points
        .iter()
        .map(|p| [x_for_t(p.temp_c), y_for_s(p.speed_percent)])
        .collect();
    shape = shape.stroke(line_pts.clone(), 2.0, FG);

    for (idx, [cx, cy]) in line_pts.iter().enumerate() {
        let is_sel = idx == sel;
        let r = if is_sel { 6.0 } else { 4.5 };
        let fill_col = if is_sel { good() } else { BG };
        let ring = circle_pts(*cx, *cy, r, 16);
        shape = shape.fill(ring.clone(), fill_col);
        shape = shape.stroke(ring, 2.0, FG);
    }

    let pts_drag = points.to_vec();
    let tr_drag = temp_range.clone();
    let sr_drag = speed_range.clone();
    let tr_key = temp_range.clone();
    let sr_key = speed_range.clone();

    let mut plot_overlay = el()
        .key("gpu-fan-curve-plot")
        .role("slider")
        .label("GPU fan curve editor")
        .focusable(!disabled)
        .disabled(disabled)
        .hit()
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .w_full()
        .h(FAN_HEIGHT)
        .radius(RADIUS_CARD)
        .fill(BG)
        .border(1.0, float_line())
        .child(canvas(shape).w_full().h(FAN_HEIGHT))
        .on_drag(move |s: &mut AppState, d, _| {
            if disabled || pts_drag.is_empty() {
                return;
            }
            let svg_x = (d.x / d.w.max(1.0)) * CURVE_WIDTH;
            let svg_y = (d.y / d.h.max(1.0)) * FAN_HEIGHT;
            let idx = match d.phase {
                Phase::Down => {
                    let mut best = 0usize;
                    let mut best_dist = f32::MAX;
                    for (i, pt) in pts_drag.iter().enumerate() {
                        let px = x_for_t(pt.temp_c);
                        let py = y_for_s(pt.speed_percent);
                        let dist = (px - svg_x).hypot(py - svg_y);
                        if dist < best_dist {
                            best_dist = dist;
                            best = i;
                        }
                    }
                    s.pc.gpu_tuning_ui.selected_fan_point = best;
                    s.pc.gpu_tuning_ui.dragging_fan = Some(best);
                    best
                }
                Phase::Move => s.pc.gpu_tuning_ui.dragging_fan.unwrap_or(sel),
                Phase::Up => {
                    let cur = s.pc.gpu_tuning_ui.dragging_fan.take();
                    if d.cancelled() {
                        return;
                    }
                    cur.unwrap_or(sel)
                }
            };
            let raw_t = min_t
                + (((svg_x - FAN_PAD_L) / plot_w.max(1.0)) * (max_t - min_t) as f32).round() as i32;
            let raw_s = min_s
                + (((PAD_T + plot_h - svg_y) / plot_h.max(1.0)) * (max_s - min_s) as f32).round()
                    as i32;
            let cur_fan = s.pc.gpu_tuning_ui.selected_fan;
            if let Some(ref mut draft) = s.pc.gpu_tuning_ui.draft {
                let cur_pts = active_fan_points(draft, cur_fan);
                let next = update_fan_point(&cur_pts, idx, raw_t, raw_s, &tr_drag, &sr_drag);
                apply_fan_points_to_draft(draft, fan_count, cur_fan, next);
            }
        })
        .on_key(move |s: &mut AppState, ev, _| {
            if disabled {
                return false;
            }
            let (dt, ds) = match ev.key {
                Key::Up => (0, 5),
                Key::Down => (0, -5),
                Key::Right => (2, 0),
                Key::Left => (-2, 0),
                _ => return false,
            };
            let idx = s.pc.gpu_tuning_ui.selected_fan_point;
            let cur_fan = s.pc.gpu_tuning_ui.selected_fan;
            if let Some(ref mut draft) = s.pc.gpu_tuning_ui.draft {
                let cur_pts = active_fan_points(draft, cur_fan);
                if let Some(pt) = cur_pts.get(idx) {
                    let next = update_fan_point(
                        &cur_pts,
                        idx,
                        pt.temp_c + dt,
                        pt.speed_percent + ds,
                        &tr_key,
                        &sr_key,
                    );
                    apply_fan_points_to_draft(draft, fan_count, cur_fan, next);
                }
            }
            true
        });

    for (i, pct) in [100, 50, 0].iter().enumerate() {
        let top = y_for_s(*pct) - 6.0;
        plot_overlay = plot_overlay.child(
            el().key(format!("gpu-fan-y-{i}"))
                .absolute(6.0, top.max(2.0))
                .child(text(format!("{pct}%")).font(type_small()).color(fg_faint())),
        );
    }

    let mut x_labels = row().w_full().px(12.0).justify_between();
    for pt in points {
        x_labels = x_labels.child(
            text(format!("{}°", pt.temp_c))
                .font(type_small())
                .color(fg_faint()),
        );
    }

    let mut meta = row().gap(8.0).center_y().wrap().child(
        text(format!("{} °C → {}%", active.temp_c, active.speed_percent))
            .font(type_small())
            .color(fg_muted()),
    );
    if fan_count > 1 {
        for idx in 0..(fan_count as usize) {
            meta = meta.child(
                filter_pill(
                    format!("gpu-fan-ch-{idx}"),
                    format!("Fan {}", idx + 1),
                    selected_fan == idx,
                )
                .on_press(move |s: &mut AppState| {
                    if !disabled {
                        s.pc.gpu_tuning_ui.selected_fan = idx;
                    }
                }),
            );
        }
    }
    if supports_zero_rpm {
        let next_zero = !zero_rpm;
        meta = meta.child(
            row()
                .gap(6.0)
                .center_y()
                .child(text("Zero RPM").font(type_small()).color(fg_muted()))
                .child(
                    menu_switch("gpu-fan-zero-rpm".into(), "Zero RPM", zero_rpm, disabled)
                        .on_press(move |s: &mut AppState| {
                            if !disabled && let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                                d.zero_rpm = Some(next_zero);
                            }
                        }),
                ),
        );
    }

    let mut box_el = col()
        .key("gpu-fan-curve")
        .w_full()
        .pt(10.0)
        .pb(4.0)
        .gap(8.0)
        .border_sides([1.0, 0.0, 0.0, 0.0], float_line())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(8.0)
                .child(text("Fan curve").font(type_control()).color(FG))
                .child(meta),
        )
        .child(plot_overlay)
        .child(x_labels);

    if needs_resident {
        box_el = box_el.child(
            text("Software fan curves stay active while Exo is open; automatic fan control restores when Exo closes.")
                .font(type_small())
                .color(fg_muted())
                .wrap(),
        );
    }

    Some(box_el)
}

fn test_progress_box(test: &GpuStabilityProgress) -> Option<El> {
    if !test.running && test.summary.as_deref().is_none_or(str::is_empty) {
        return None;
    }
    let summary_col = match test.passed {
        Some(true) => good(),
        Some(false) => bad(),
        None => fg_muted(),
    };
    let ratio = if test.total_sec > 0 {
        (test.elapsed_sec as f32 / test.total_sec as f32).clamp(0.0, 1.0)
    } else {
        0.0
    };
    let mut box_el = col()
        .key("gpu-tune-test-box")
        .w_full()
        .px(12.0)
        .py(10.0)
        .gap(8.0)
        .radius(RADIUS_CARD)
        .fill(subtle_fill())
        .border(1.0, float_line());
    if test.running {
        let step_lbl = test
            .current_step
            .clone()
            .unwrap_or_else(|| "Running GPU stability test".into());
        box_el = box_el
            .child(
                row()
                    .w_full()
                    .justify_between()
                    .child(text(step_lbl).font(type_small()).color(FG))
                    .child(
                        text(format!("{}/{} s", test.elapsed_sec, test.total_sec))
                            .font(type_small())
                            .color(fg_muted()),
                    ),
            )
            .child(progress_bar(480.0, ratio));
    }
    if let Some(ref summary) = test.summary
        && !summary.is_empty()
    {
        box_el = box_el.child(
            text(summary.clone())
                .font(type_small())
                .color(summary_col)
                .wrap(),
        );
    }
    Some(box_el)
}

pub(crate) fn view(s: &AppState) -> El {
    let ui = &s.pc.gpu_tuning_ui;
    let (Some(state), Some(draft)) = (ui.state.as_deref(), ui.draft.as_deref()) else {
        return menu_row(
            "Manual tuning",
            Some("Reading GPU tuning capabilities…".into()),
            None,
            row(),
        );
    };

    let caps = &state.capabilities;
    let locked = caps.locked_by_maker;
    let waiver_needed = needs_waiver(caps);
    let testing = state.test.running;
    let disabled = controls_disabled(state, draft) || ui.busy;

    let mut root = col()
        .key("gpu-manual-tuning")
        .w_full()
        .gap(10.0)
        .child(status_header(state));

    if locked {
        root = root.child(menu_row(
            "Tuning mode",
            Some(lock_detail(caps)),
            None,
            status_badge("Locked".into(), BadgeTone::Neutral),
        ));
    } else {
        let mode_detail = caps
            .fallback_reason
            .clone()
            .or_else(|| caps.persistence_note.clone());
        let is_manual = draft.mode == "manual";
        let state_is_manual = state.mode == "manual";
        let mode_ctrl = row()
            .gap(4.0)
            .px(2.0)
            .py(2.0)
            .radius(RADIUS_PILL)
            .fill(subtle_fill())
            .border(1.0, float_line())
            .child(
                filter_pill("gpu-tune-mode-auto", "Auto", !is_manual).on_press(
                    move |s: &mut AppState| {
                        if testing {
                            return;
                        }
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.mode = "auto".into();
                        }
                        if state_is_manual {
                            s.gpu_tuning_reset();
                        }
                    },
                ),
            )
            .child(
                filter_pill("gpu-tune-mode-manual", "Manual", is_manual).on_press(
                    move |s: &mut AppState| {
                        if testing {
                            return;
                        }
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.mode = "manual".into();
                        }
                    },
                ),
            );
        root = root.child(menu_row("Tuning mode", mode_detail, None, mode_ctrl));

        if waiver_needed {
            let waiver_copy = caps.waiver_text.clone().unwrap_or_else(|| {
                "Altering GPU clock, voltage, power, or fan settings outside factory specifications may cause instability.".into()
            });
            root = root.child(
                col()
                    .key("gpu-tune-waiver")
                    .w_full()
                    .px(14.0)
                    .py(12.0)
                    .gap(10.0)
                    .radius(RADIUS_CARD)
                    .fill(subtle_fill())
                    .border(1.0, float_line())
                    .child(
                        text(waiver_copy)
                            .font(type_small())
                            .color(fg_muted())
                            .wrap(),
                    )
                    .child(
                        row().w_full().justify_end().child(
                            menu_btn(
                                "gpu-tune-waiver-accept".into(),
                                "I understand, unlock tuning",
                                true,
                                false,
                                ui.waiver_busy,
                            )
                            .on_press(|s: &mut AppState| s.gpu_tuning_accept_waiver()),
                        ),
                    ),
            );
        }

        if draft.mode == "manual" && !waiver_needed {
            let vram_unit = if caps.vendor == "intel" {
                "MT/s"
            } else {
                "MHz"
            };
            let amd_power_signed = caps.vendor == "amd";
            let sliders: Vec<Option<El>> = vec![
                range_slider_row(
                    "coreClockOffsetMhz",
                    "Core clock offset",
                    &caps.core_clock_offset_mhz,
                    draft.core_clock_offset_mhz,
                    "MHz",
                    true,
                    disabled,
                    ui.dragging_slider == Some("coreClockOffsetMhz"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.core_clock_offset_mhz = Some(v);
                        }
                    },
                ),
                range_slider_row(
                    "coreMinClockMhz",
                    "GPU min clock",
                    &caps.core_min_clock_mhz,
                    draft.core_min_clock_mhz,
                    "MHz",
                    false,
                    disabled,
                    ui.dragging_slider == Some("coreMinClockMhz"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.core_min_clock_mhz = Some(v);
                        }
                    },
                ),
                range_slider_row(
                    "coreMaxClockMhz",
                    "GPU max clock",
                    &caps.core_max_clock_mhz,
                    draft.core_max_clock_mhz,
                    "MHz",
                    false,
                    disabled,
                    ui.dragging_slider == Some("coreMaxClockMhz"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.core_max_clock_mhz = Some(v);
                        }
                    },
                ),
                range_slider_row(
                    "voltageMv",
                    "GPU voltage",
                    &caps.voltage_mv,
                    draft.voltage_mv,
                    "mV",
                    false,
                    disabled,
                    ui.dragging_slider == Some("voltageMv"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.voltage_mv = Some(v);
                        }
                    },
                ),
                range_slider_row(
                    "voltageOffsetMv",
                    "Voltage offset",
                    &caps.voltage_offset_mv,
                    draft.voltage_offset_mv,
                    "mV",
                    true,
                    disabled,
                    ui.dragging_slider == Some("voltageOffsetMv"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.voltage_offset_mv = Some(v);
                        }
                    },
                ),
                range_slider_row(
                    "memoryClockOffsetMhz",
                    "Memory clock offset",
                    &caps.memory_clock_offset_mhz,
                    draft.memory_clock_offset_mhz,
                    "MHz",
                    true,
                    disabled,
                    ui.dragging_slider == Some("memoryClockOffsetMhz"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.memory_clock_offset_mhz = Some(v);
                        }
                    },
                ),
                range_slider_row(
                    "memoryMaxClockMhz",
                    "VRAM clock limit",
                    &caps.memory_max_clock_mhz,
                    draft.memory_max_clock_mhz,
                    vram_unit,
                    false,
                    disabled,
                    ui.dragging_slider == Some("memoryMaxClockMhz"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.memory_max_clock_mhz = Some(v);
                        }
                    },
                ),
            ];
            for item in sliders.into_iter().flatten() {
                root = root.child(item);
            }

            if caps.memory_timing_levels.len() > 1 {
                let current_lvl = draft
                    .memory_timing_level
                    .as_deref()
                    .or_else(|| caps.memory_timing_levels.first().map(String::as_str))
                    .unwrap_or("");
                let mut pills = row().gap(6.0).center_y().wrap();
                for (idx, level) in caps.memory_timing_levels.iter().enumerate() {
                    let lv = level.clone();
                    pills = pills.child(
                        filter_pill(
                            format!("gpu-mem-timing-{idx}"),
                            level.clone(),
                            current_lvl == level,
                        )
                        .on_press(move |s: &mut AppState| {
                            if !disabled && let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                                d.memory_timing_level = Some(lv.clone());
                            }
                        }),
                    );
                }
                root = root.child(menu_row("Memory timing", None, None, pills));
            }

            let tail_sliders: Vec<Option<El>> = vec![
                range_slider_row(
                    "powerLimitPercent",
                    "Power limit",
                    &caps.power_limit_percent,
                    draft.power_limit_percent,
                    "%",
                    amd_power_signed,
                    disabled,
                    ui.dragging_slider == Some("powerLimitPercent"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.power_limit_percent = Some(v);
                        }
                    },
                ),
                range_slider_row(
                    "powerLimitWatts",
                    "Power limit",
                    &caps.power_limit_watts,
                    draft.power_limit_watts,
                    "W",
                    false,
                    disabled,
                    ui.dragging_slider == Some("powerLimitWatts"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.power_limit_watts = Some(v);
                        }
                    },
                ),
                range_slider_row(
                    "tempLimitC",
                    "Temperature limit",
                    &caps.temp_limit_c,
                    draft.temp_limit_c,
                    "°C",
                    false,
                    disabled,
                    ui.dragging_slider == Some("tempLimitC"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.temp_limit_c = Some(v);
                        }
                    },
                ),
                range_slider_row(
                    "clockLockMhz",
                    "Clock ceiling lock",
                    &caps.clock_lock_mhz,
                    draft.clock_lock_mhz,
                    "MHz",
                    false,
                    disabled,
                    ui.dragging_slider == Some("clockLockMhz"),
                    |s, v| {
                        if let Some(ref mut d) = s.pc.gpu_tuning_ui.draft {
                            d.clock_lock_mhz = Some(v);
                        }
                    },
                ),
            ];
            for item in tail_sliders.into_iter().flatten() {
                root = root.child(item);
            }

            if caps.supports_curve_editor
                && let Some(ref vf_pts) = draft.vf_curve
                && !vf_pts.is_empty()
            {
                let is_amd = caps.vendor == "amd";
                let offset_range = if is_amd {
                    &caps.voltage_offset_mv
                } else {
                    &caps.core_clock_offset_mhz
                };
                if let Some(vf_el) =
                    vf_curve_view(vf_pts, offset_range, is_amd, ui.selected_vf_point, disabled)
                {
                    root = root.child(vf_el);
                }
            }

            if caps.supports_hardware_fan_curve || caps.supports_software_fan_curve {
                let fan_pts = active_fan_points(draft, ui.selected_fan);
                if let Some(fan_el) = fan_curve_view(
                    &fan_pts,
                    &caps.fan_temp_c,
                    &caps.fan_speed_percent,
                    caps.fan_count,
                    ui.selected_fan,
                    ui.selected_fan_point,
                    caps.supports_zero_rpm,
                    draft.zero_rpm == Some(true),
                    caps.needs_resident_for_fan_curve,
                    disabled,
                ) {
                    root = root.child(fan_el);
                }
            }
        }

        if let Some(progress_el) = test_progress_box(&state.test) {
            root = root.child(progress_el);
        }

        let test_label = if testing && state.test.kind == "stability" {
            "Stop test"
        } else {
            "Test"
        };
        let uv_label = if testing && state.test.kind == "undervolt" {
            "Stop search"
        } else {
            "Find my undervolt"
        };
        let dur_60 = ui.duration != "5m";

        let mut left_actions = row()
            .gap(6.0)
            .center_y()
            .wrap()
            .child(filter_pill("gpu-tune-dur-60s", "60 s", dur_60).on_press(
                move |s: &mut AppState| {
                    if !testing && !waiver_needed {
                        s.pc.gpu_tuning_ui.duration = "60s".into();
                    }
                },
            ))
            .child(filter_pill("gpu-tune-dur-5m", "5 min", !dur_60).on_press(
                move |s: &mut AppState| {
                    if !testing && !waiver_needed {
                        s.pc.gpu_tuning_ui.duration = "5m".into();
                    }
                },
            ))
            .child(
                menu_btn(
                    "gpu-tune-test".into(),
                    test_label,
                    false,
                    false,
                    waiver_needed || ui.busy,
                )
                .on_press(|s: &mut AppState| s.gpu_tuning_toggle_test()),
            );

        if caps.supports_undervolt_search {
            let uv_disabled =
                waiver_needed || ui.busy || (testing && state.test.kind != "undervolt");
            left_actions = left_actions.child(
                menu_btn(
                    "gpu-tune-undervolt".into(),
                    uv_label,
                    false,
                    false,
                    uv_disabled,
                )
                .on_press(|s: &mut AppState| s.gpu_tuning_toggle_undervolt()),
            );
        }

        let mut right_actions = row().gap(6.0).center_y().wrap();
        if state.paused_after_crash && state.can_reapply {
            right_actions = right_actions.child(
                menu_btn(
                    "gpu-tune-reapply".into(),
                    "Re-apply",
                    true,
                    false,
                    testing || ui.busy,
                )
                .on_press(|s: &mut AppState| s.gpu_tuning_reapply()),
            );
        }
        let reset_label = if ui.armed_reset {
            "Reset GPU to factory defaults?"
        } else {
            "Reset"
        };
        right_actions = right_actions
            .child(
                menu_btn(
                    "gpu-tune-reset".into(),
                    reset_label,
                    false,
                    true,
                    testing || ui.busy,
                )
                .on_press(|s: &mut AppState| {
                    if s.pc.gpu_tuning_ui.armed_reset {
                        s.gpu_tuning_reset();
                    } else {
                        s.pc.gpu_tuning_ui.armed_reset = true;
                    }
                }),
            )
            .child(
                menu_btn("gpu-tune-apply".into(), "Apply", true, false, disabled)
                    .on_press(|s: &mut AppState| s.gpu_tuning_apply()),
            );

        root = root.child(
            row()
                .key("gpu-tune-actions")
                .w_full()
                .pt(8.0)
                .gap(8.0)
                .justify_between()
                .center_y()
                .wrap()
                .border_sides([1.0, 0.0, 0.0, 0.0], float_line())
                .motion(SPRING_QUICK)
                .child(left_actions)
                .child(right_actions),
        );
    }

    if let Some(ref err) = ui.error
        && !err.is_empty()
    {
        root = root.child(
            text(err.clone())
                .key("gpu-tune-error")
                .font(type_small())
                .color(bad())
                .wrap(),
        );
    }

    root
}

fn unsupported_range() -> GpuTuningRange {
    GpuTuningRange {
        min: 0,
        max: 0,
        step: 1,
        default_val: 0,
        supported: false,
    }
}

fn demo_vf_points() -> Vec<GpuVfPoint> {
    vec![
        GpuVfPoint {
            index: 0,
            voltage_mv: 750,
            base_clock_mhz: 1500,
            offset_mhz: 0,
        },
        GpuVfPoint {
            index: 1,
            voltage_mv: 825,
            base_clock_mhz: 1710,
            offset_mhz: 30,
        },
        GpuVfPoint {
            index: 2,
            voltage_mv: 900,
            base_clock_mhz: 1890,
            offset_mhz: 45,
        },
        GpuVfPoint {
            index: 3,
            voltage_mv: 975,
            base_clock_mhz: 2025,
            offset_mhz: 15,
        },
        GpuVfPoint {
            index: 4,
            voltage_mv: 1050,
            base_clock_mhz: 2100,
            offset_mhz: 0,
        },
    ]
}

pub fn demo_nvidia_state() -> GpuManualTuningState {
    let vf = demo_vf_points();
    let fan = default_fan_points();
    let defaults = GpuManualProfile {
        mode: "auto".into(),
        core_clock_offset_mhz: Some(0),
        memory_clock_offset_mhz: Some(0),
        power_limit_percent: Some(100),
        temp_limit_c: Some(83),
        clock_lock_mhz: Some(2100),
        vf_curve: Some(vf.clone()),
        fan_curve: Some(fan.clone()),
        ..Default::default()
    };
    let manual = GpuManualProfile {
        mode: "manual".into(),
        core_clock_offset_mhz: Some(75),
        memory_clock_offset_mhz: Some(500),
        power_limit_percent: Some(105),
        temp_limit_c: Some(85),
        clock_lock_mhz: Some(2055),
        vf_curve: Some(vf.clone()),
        fan_curve: Some(fan),
        ..Default::default()
    };
    GpuManualTuningState {
        ok: true,
        mode: "manual".into(),
        status_line: "Manual active · NVAPI".into(),
        live: GpuTuningLive {
            temp_c: Some(52.0),
            clock_mhz: Some(1965),
            fan_percent: Some(46),
            watts: Some(142.0),
        },
        capabilities: GpuTuningCapabilities {
            vendor: "nvidia".into(),
            generation: "Ampere".into(),
            device_id: "nvidia:NVIDIA GeForce RTX 3070".into(),
            device_name: "NVIDIA GeForce RTX 3070".into(),
            backend: "nvapi".into(),
            needs_resident_for_fan_curve: true,
            persistence_note: Some(
                "Clocks and limits re-apply at sign-in; software fan curves stay active while Exo is open."
                    .into(),
            ),
            supports_curve_editor: true,
            supports_undervolt_search: true,
            core_clock_offset_mhz: GpuTuningRange {
                min: -500,
                max: 500,
                step: 15,
                default_val: 0,
                supported: true,
            },
            core_min_clock_mhz: unsupported_range(),
            core_max_clock_mhz: unsupported_range(),
            voltage_mv: unsupported_range(),
            voltage_offset_mv: unsupported_range(),
            memory_clock_offset_mhz: GpuTuningRange {
                min: -1000,
                max: 1500,
                step: 25,
                default_val: 0,
                supported: true,
            },
            memory_max_clock_mhz: unsupported_range(),
            power_limit_percent: GpuTuningRange {
                min: 50,
                max: 115,
                step: 1,
                default_val: 100,
                supported: true,
            },
            power_limit_watts: unsupported_range(),
            temp_limit_c: GpuTuningRange {
                min: 65,
                max: 90,
                step: 1,
                default_val: 83,
                supported: true,
            },
            clock_lock_mhz: GpuTuningRange {
                min: 600,
                max: 2200,
                step: 15,
                default_val: 2100,
                supported: true,
            },
            vf_curve_points: vf,
            fan_count: 2,
            supports_software_fan_curve: true,
            fan_max_points: 5,
            fan_speed_percent: GpuTuningRange {
                min: 0,
                max: 100,
                step: 1,
                default_val: 50,
                supported: true,
            },
            fan_temp_c: GpuTuningRange {
                min: 30,
                max: 100,
                step: 1,
                default_val: 60,
                supported: true,
            },
            ..Default::default()
        },
        factory_defaults: defaults,
        current: manual.clone(),
        saved: manual,
        ..Default::default()
    }
}

pub fn demo_amd_rdna3_state() -> GpuManualTuningState {
    let vf: Vec<GpuVfPoint> = demo_vf_points()
        .into_iter()
        .map(|p| GpuVfPoint {
            offset_mhz: -35,
            ..p
        })
        .collect();
    let fan = default_fan_points();
    let defaults = GpuManualProfile {
        mode: "auto".into(),
        core_min_clock_mhz: Some(500),
        core_max_clock_mhz: Some(2800),
        voltage_offset_mv: Some(0),
        memory_max_clock_mhz: Some(2500),
        memory_timing_level: Some("Default".into()),
        power_limit_percent: Some(0),
        zero_rpm: Some(true),
        vf_curve: Some(vf.clone()),
        fan_curve: Some(fan.clone()),
        ..Default::default()
    };
    let manual = GpuManualProfile {
        mode: "manual".into(),
        core_min_clock_mhz: Some(500),
        core_max_clock_mhz: Some(2950),
        voltage_offset_mv: Some(-35),
        memory_max_clock_mhz: Some(2620),
        memory_timing_level: Some("Fast Timing".into()),
        power_limit_percent: Some(10),
        zero_rpm: Some(true),
        vf_curve: Some(vf.clone()),
        fan_curve: Some(fan),
        ..Default::default()
    };
    GpuManualTuningState {
        ok: true,
        mode: "manual".into(),
        status_line: "Manual active · ADLX".into(),
        live: GpuTuningLive {
            temp_c: Some(56.0),
            clock_mhz: Some(2740),
            fan_percent: Some(42),
            watts: Some(218.0),
        },
        capabilities: GpuTuningCapabilities {
            vendor: "amd".into(),
            generation: "RDNA3".into(),
            device_id: "amd:AMD Radeon RX 7900 XT".into(),
            device_name: "AMD Radeon RX 7900 XT".into(),
            backend: "adlx".into(),
            persists_in_driver: true,
            persistence_note: Some(
                "AMD Adrenalin persists manual tuning and hardware fan curves in the driver."
                    .into(),
            ),
            supports_curve_editor: true,
            supports_undervolt_search: true,
            core_clock_offset_mhz: unsupported_range(),
            core_min_clock_mhz: GpuTuningRange {
                min: 500,
                max: 2900,
                step: 10,
                default_val: 500,
                supported: true,
            },
            core_max_clock_mhz: GpuTuningRange {
                min: 500,
                max: 3200,
                step: 10,
                default_val: 2800,
                supported: true,
            },
            voltage_mv: unsupported_range(),
            voltage_offset_mv: GpuTuningRange {
                min: -100,
                max: 50,
                step: 5,
                default_val: 0,
                supported: true,
            },
            memory_clock_offset_mhz: unsupported_range(),
            memory_max_clock_mhz: GpuTuningRange {
                min: 1750,
                max: 2750,
                step: 10,
                default_val: 2500,
                supported: true,
            },
            power_limit_percent: GpuTuningRange {
                min: -10,
                max: 15,
                step: 1,
                default_val: 0,
                supported: true,
            },
            power_limit_watts: unsupported_range(),
            temp_limit_c: unsupported_range(),
            clock_lock_mhz: unsupported_range(),
            memory_timing_levels: vec!["Default".into(), "Fast Timing".into()],
            vf_curve_points: vf,
            fan_count: 1,
            supports_hardware_fan_curve: true,
            supports_zero_rpm: true,
            fan_max_points: 5,
            fan_speed_percent: GpuTuningRange {
                min: 0,
                max: 100,
                step: 1,
                default_val: 50,
                supported: true,
            },
            fan_temp_c: GpuTuningRange {
                min: 30,
                max: 100,
                step: 1,
                default_val: 60,
                supported: true,
            },
            ..Default::default()
        },
        factory_defaults: defaults,
        current: manual.clone(),
        saved: manual,
        ..Default::default()
    }
}

pub fn demo_intel_arc_state(waiver_accepted: bool) -> GpuManualTuningState {
    let fan = default_fan_points();
    let defaults = GpuManualProfile {
        mode: "auto".into(),
        core_clock_offset_mhz: Some(0),
        voltage_offset_mv: Some(0),
        power_limit_watts: Some(190),
        temp_limit_c: Some(90),
        fan_curve: Some(fan.clone()),
        ..Default::default()
    };
    let current = GpuManualProfile {
        mode: if waiver_accepted { "manual" } else { "auto" }.into(),
        core_clock_offset_mhz: Some(if waiver_accepted { 50 } else { 0 }),
        voltage_offset_mv: Some(if waiver_accepted { -25 } else { 0 }),
        power_limit_watts: Some(if waiver_accepted { 210 } else { 190 }),
        temp_limit_c: Some(90),
        fan_curve: Some(fan),
        ..Default::default()
    };
    GpuManualTuningState {
        ok: true,
        mode: current.mode.clone(),
        status_line: if waiver_accepted {
            "Manual active · IGCL".into()
        } else {
            "Auto (stock) · IGCL".into()
        },
        live: GpuTuningLive {
            temp_c: Some(48.0),
            clock_mhz: Some(2670),
            fan_percent: Some(36),
            watts: Some(64.0),
        },
        capabilities: GpuTuningCapabilities {
            vendor: "intel".into(),
            generation: "Battlemage".into(),
            device_id: "intel:Intel Arc B580".into(),
            device_name: "Intel Arc B580".into(),
            backend: "igcl".into(),
            requires_waiver: true,
            waiver_accepted,
            waiver_text: Some(
                "Overclocking or altering GPU clock frequency, voltage, power, or fan settings outside Intel factory specifications may reduce hardware lifetime, cause instability, and is not covered by Intel warranty. Continue only if you accept these risks for this GPU."
                    .into(),
            ),
            persistence_note: Some(
                "Intel Arc hardware fan tables run in the driver; clocks and limits re-apply at sign-in."
                    .into(),
            ),
            supports_curve_editor: false,
            supports_undervolt_search: true,
            core_clock_offset_mhz: GpuTuningRange {
                min: -200,
                max: 300,
                step: 5,
                default_val: 0,
                supported: true,
            },
            voltage_offset_mv: GpuTuningRange {
                min: -100,
                max: 100,
                step: 5,
                default_val: 0,
                supported: true,
            },
            power_limit_watts: GpuTuningRange {
                min: 100,
                max: 228,
                step: 5,
                default_val: 190,
                supported: true,
            },
            temp_limit_c: GpuTuningRange {
                min: 65,
                max: 95,
                step: 1,
                default_val: 90,
                supported: true,
            },
            fan_count: 2,
            supports_hardware_fan_curve: true,
            fan_max_points: 5,
            fan_speed_percent: GpuTuningRange {
                min: 0,
                max: 100,
                step: 1,
                default_val: 50,
                supported: true,
            },
            fan_temp_c: GpuTuningRange {
                min: 30,
                max: 100,
                step: 1,
                default_val: 60,
                supported: true,
            },
            ..Default::default()
        },
        factory_defaults: defaults,
        current: current.clone(),
        saved: current,
        ..Default::default()
    }
}

pub fn demo_locked_laptop_state() -> GpuManualTuningState {
    GpuManualTuningState {
        ok: true,
        mode: "auto".into(),
        status_line: "Locked · NVAPI".into(),
        live: GpuTuningLive {
            temp_c: Some(54.0),
            clock_mhz: Some(1680),
            fan_percent: None,
            watts: Some(55.0),
        },
        capabilities: GpuTuningCapabilities {
            vendor: "nvidia".into(),
            generation: "Ada".into(),
            device_id: "nvidia:NVIDIA GeForce RTX 4070 Laptop GPU".into(),
            device_name: "NVIDIA GeForce RTX 4070 Laptop GPU".into(),
            backend: "nvapi".into(),
            is_laptop: true,
            locked_by_maker: true,
            lock_reason: Some("Locked by your GPU maker".into()),
            ..Default::default()
        },
        ..Default::default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::GpuTuningChangedEvent;

    #[test]
    fn vendor_range_gating_matches_capabilities() {
        let nv = demo_nvidia_state();
        assert_eq!(
            visible_slider_keys(&nv.capabilities),
            vec![
                "coreClockOffsetMhz",
                "memoryClockOffsetMhz",
                "powerLimitPercent",
                "tempLimitC",
                "clockLockMhz"
            ]
        );
        let amd = demo_amd_rdna3_state();
        assert_eq!(
            visible_slider_keys(&amd.capabilities),
            vec![
                "coreMinClockMhz",
                "coreMaxClockMhz",
                "voltageOffsetMv",
                "memoryMaxClockMhz",
                "powerLimitPercent"
            ]
        );
        let intel = demo_intel_arc_state(false);
        assert_eq!(
            visible_slider_keys(&intel.capabilities),
            vec![
                "coreClockOffsetMhz",
                "voltageOffsetMv",
                "powerLimitWatts",
                "tempLimitC"
            ]
        );
    }

    #[test]
    fn locked_and_waiver_states_gate_controls_and_status_text() {
        let locked = demo_locked_laptop_state();
        let locked_draft = draft_from_state(&locked);
        assert!(controls_disabled(&locked, &locked_draft));
        assert_eq!(
            lock_detail(&locked.capabilities),
            "Locked by your GPU maker"
        );
        assert_eq!(status_color(&locked), warn());

        let intel_unwaived = demo_intel_arc_state(false);
        let intel_draft = draft_from_state(&intel_unwaived);
        assert!(needs_waiver(&intel_unwaived.capabilities));
        assert!(controls_disabled(&intel_unwaived, &intel_draft));

        let intel_waived = demo_intel_arc_state(true);
        let intel_waived_draft = draft_from_state(&intel_waived);
        assert!(!needs_waiver(&intel_waived.capabilities));
        assert!(!controls_disabled(&intel_waived, &intel_waived_draft));
        assert_eq!(status_color(&intel_waived), good());
    }

    #[test]
    fn curve_editors_enforce_clamps_flatten_and_monotonic_fan_points() {
        let range = GpuTuningRange {
            min: -500,
            max: 500,
            step: 15,
            default_val: 0,
            supported: true,
        };
        assert_eq!(clamp_to_range(22, &range), 25);
        assert_eq!(clamp_to_range(999, &range), 500);
        assert_eq!(format_range_value(45, "MHz", true), "+45 MHz");
        assert_eq!(format_range_value(-30, "mV", true), "-30 mV");

        let vf = demo_vf_points();
        let flat = flatten_vf_from(&vf, 2, &range);
        let ceiling = vf[2].base_clock_mhz + vf[2].offset_mhz;
        assert!((flat[3].base_clock_mhz + flat[3].offset_mhz - ceiling).abs() <= range.step);
        assert!((flat[4].base_clock_mhz + flat[4].offset_mhz - ceiling).abs() <= range.step);

        let fan = default_fan_points();
        let tr = GpuTuningRange {
            min: 30,
            max: 100,
            step: 1,
            default_val: 60,
            supported: true,
        };
        let sr = GpuTuningRange {
            min: 0,
            max: 100,
            step: 1,
            default_val: 50,
            supported: true,
        };
        let moved = update_fan_point(&fan, 2, 10, 99, &tr, &sr);
        assert_eq!(moved[2].temp_c, fan[1].temp_c + 1);
        assert_eq!(moved[2].speed_percent, fan[3].speed_percent);
    }

    #[test]
    fn register_events_updates_tuning_status_and_test_progress() {
        let mut s = AppState::with_demo_games();
        s.pc.gpu_tuning_ui = GpuManualTuningUiState::demo();
        assert!(
            s.apply_host_event(HostEvent::GpuTuningChanged(GpuTuningChangedEvent {
                mode: "auto".into(),
                status_line: "Paused after crash · NVAPI".into(),
                paused_after_crash: true,
                queued_pending: false,
                queued_reason: None,
                backend: "nvapi".into(),
            }))
        );
        let st = s.pc.gpu_tuning_ui.state.as_ref().expect("state");
        assert!(st.paused_after_crash);
        assert_eq!(st.status_line, "Paused after crash · NVAPI");

        assert!(
            s.apply_host_event(HostEvent::GpuTuningTestProgress(Box::new(
                GpuStabilityProgress {
                    running: true,
                    kind: "stability".into(),
                    phase: "running".into(),
                    elapsed_sec: 20,
                    total_sec: 60,
                    passes: 40,
                    checksum_ok: true,
                    current_step: Some("Pass 40".into()),
                    ..Default::default()
                }
            )))
        );
        let st = s.pc.gpu_tuning_ui.state.as_ref().expect("state");
        assert!(st.test.running);
        assert_eq!(st.test.elapsed_sec, 20);
    }

    #[test]
    fn gpu_manual_tuning_gridlines_and_motion_gate() {
        use crate::pc_state::PcPartMenu;
        use crate::shell;
        use crate::state::Room;
        use rune::motion_gate::Run;
        use rune::{Env, Runtime};

        for (label, fixture, expect_vf, expect_waiver) in [
            ("nvidia", demo_nvidia_state(), true, false),
            ("amd", demo_amd_rdna3_state(), true, false),
            ("intel", demo_intel_arc_state(false), false, true),
            ("locked", demo_locked_laptop_state(), false, false),
        ] {
            let mut state = AppState::with_demo_games();
            state.switch_room(Room::ExoAi);
            state.pc.gpu_tuning_ui.sync_from_host(fixture, true);
            state.pc.part_menu = Some(PcPartMenu::Graphics);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.frame(0.0);
            rt.settle_all();
            rt.frame(1.0 / 60.0);

            let (panel, _) = rt.presented("pc-part-menu").expect("pc-part-menu");
            let (tuning, _) = rt
                .presented("gpu-manual-tuning")
                .unwrap_or_else(|| panic!("{label}: gpu-manual-tuning missing"));
            let (status, _) = rt
                .presented("gpu-tune-status")
                .unwrap_or_else(|| panic!("{label}: gpu-tune-status missing"));
            assert!(
                (tuning[0] - status[0]).abs() <= 1.0,
                "{label}: status x={} misaligned with container x={}",
                status[0],
                tuning[0]
            );
            assert!(
                tuning[0] + tuning[2] <= panel[0] + panel[2] + 1.0,
                "{label}: gpu-manual-tuning overflows popover"
            );
            assert_eq!(rt.presented("gpu-vf-curve").is_some(), expect_vf);
            assert_eq!(rt.presented("gpu-tune-waiver").is_some(), expect_waiver);
        }

        for reduce_motion in [false, true] {
            let mut state = AppState::with_demo_games();
            state.settings.reduce_motion = reduce_motion;
            state.switch_room(Room::ExoAi);
            let mut auto_fixture = demo_nvidia_state();
            auto_fixture.mode = "auto".into();
            auto_fixture.status_line = "Auto (stock) · NVAPI".into();
            state.pc.gpu_tuning_ui.sync_from_host(auto_fixture, true);
            state.pc.part_menu = Some(PcPartMenu::Graphics);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.4);
            run.step();
            run.act("switch duration to 5 min", |rt| {
                Run::tap(rt, "gpu-tune-dur-5m")
            });
            run.play(0.3);
            run.act("switch duration to 60 s", |rt| {
                Run::tap(rt, "gpu-tune-dur-60s")
            });
            run.play(0.3);
            let findings: Vec<_> = run
                .finish()
                .into_iter()
                .filter(|f| !(reduce_motion && matches!(f.kind, "jump" | "text-swap")))
                .collect();
            assert!(
                findings.is_empty(),
                "gpu_tuning motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }
}
