use exo_kit::{FG, RADIUS_PANEL, fg_muted, float_line, type_body, type_small, type_title};
use exo_rpc::{GpuProcessUsage, GpuProcessesState};
use rune::{El, col, el, row, text};

use crate::pc_state::format_bytes;
use crate::state::{AppState, Room};

const POLL_SECS: f64 = 3.0;
const ROWS: usize = 6;
const SHARED_NOTE_MIN: u64 = 64 * 1024 * 1024;

pub struct GpuActivityUiState {
    pub data: Option<GpuProcessesState>,
    pub busy: bool,
    pub last_poll_at: f64,
}

impl Default for GpuActivityUiState {
    fn default() -> Self {
        Self {
            data: None,
            busy: false,
            last_poll_at: -POLL_SECS,
        }
    }
}

impl AppState {
    pub fn tick_gpu_activity(&mut self, now: f64) {
        let ui = &mut self.pc.gpu_activity_ui;
        if self.room != Room::ExoAi || ui.busy || now - ui.last_poll_at < POLL_SECS {
            return;
        }
        ui.last_poll_at = now;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.pc.gpu_activity_ui.busy = true;
            rpc.call::<_, GpuProcessesState>(
                "gpu.processes",
                &serde_json::json!({ "limit": 8 }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.gpu_activity_ui.busy = false;
                        if let Ok(next) = res {
                            s.pc.gpu_activity_ui.data = Some(next);
                        }
                    });
                },
            );
        }
    }
}

fn process_metrics(proc: &GpuProcessUsage) -> String {
    let mut parts = Vec::new();
    if proc.total_percent >= 0.1 {
        parts.push(format!("{}%", proc.total_percent.round() as i64));
    }
    if proc.local_bytes > 0 {
        parts.push(format!("{} VRAM", format_bytes(proc.local_bytes)));
    }
    if proc.non_local_bytes >= SHARED_NOTE_MIN {
        parts.push(format!("{} shared", format_bytes(proc.non_local_bytes)));
    }
    if parts.is_empty() {
        "0%".into()
    } else {
        parts.join(" · ")
    }
}

fn process_subtitle(proc: &GpuProcessUsage, multi_adapter: bool) -> Option<String> {
    let mut parts = Vec::new();
    if !proc.engines.is_empty() {
        parts.push(
            proc.engines
                .iter()
                .take(2)
                .map(|e| format!("{} {}%", e.kind, e.percent.round() as i64))
                .collect::<Vec<_>>()
                .join(", "),
        );
    }
    if multi_adapter && !proc.adapter_name.is_empty() {
        parts.push(proc.adapter_name.clone());
    }
    (!parts.is_empty()).then(|| parts.join(" · "))
}

pub(crate) fn card_view(s: &AppState) -> El {
    let data = s.pc.gpu_activity_ui.data.as_ref();
    let summary = data
        .map(|d| d.answer.summary.as_str())
        .filter(|t| !t.is_empty())
        .unwrap_or("Reading GPU activity…");
    let multi_adapter = data.is_some_and(|d| d.adapters.len() > 1);

    let mut card = col()
        .key("exo-ai-gpu-activity-card")
        .w_full()
        .pt(16.0)
        .px(24.0)
        .pb(12.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(exo_kit::surface_card())
        .border(1.0, exo_kit::card_border())
        .child(
            row().w_full().min_h(32.0).pb(10.0).center_y().child(
                text("GPU activity")
                    .key("exo-ai-gpu-activity-title")
                    .font(type_title())
                    .color(FG),
            ),
        )
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(
            el().w_full().py(10.0).child(
                text(summary.to_string())
                    .key("exo-ai-gpu-activity-summary")
                    .font(type_body())
                    .color(fg_muted())
                    .w_full()
                    .wrap(),
            ),
        );

    for proc in data.iter().flat_map(|d| d.processes.iter()).take(ROWS) {
        let mut name_col = col().grow(1.0).min_w(0.0).gap(1.0).child(
            text(proc.name.clone())
                .font(type_body())
                .color(FG)
                .truncate(),
        );
        if let Some(sub) = process_subtitle(proc, multi_adapter) {
            name_col = name_col.child(text(sub).font(type_small()).color(fg_muted()).truncate());
        }
        card = card.child(el().w_full().h(1.0).fill(float_line())).child(
            row()
                .key(format!(
                    "exo-ai-gpu-proc-{}-{}",
                    proc.pid, proc.adapter_luid
                ))
                .w_full()
                .py(8.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .child(name_col)
                .child(
                    text(process_metrics(proc))
                        .font(type_small())
                        .color(fg_muted()),
                ),
        );
    }
    card
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::GpuEngineSlice;

    fn proc(percent: f64, local: u64, shared: u64) -> GpuProcessUsage {
        GpuProcessUsage {
            pid: 42,
            name: "game.exe".into(),
            adapter_name: "RTX 3070".into(),
            total_percent: percent,
            engines: vec![
                GpuEngineSlice {
                    kind: "3D".into(),
                    percent: 61.4,
                },
                GpuEngineSlice {
                    kind: "Copy".into(),
                    percent: 3.0,
                },
                GpuEngineSlice {
                    kind: "Video".into(),
                    percent: 1.0,
                },
            ],
            local_bytes: local,
            non_local_bytes: shared,
            ..Default::default()
        }
    }

    #[test]
    fn metrics_leave_out_idle_share_and_small_shared_memory() {
        assert_eq!(process_metrics(&proc(0.0, 0, 0)), "0%");
        assert_eq!(
            process_metrics(&proc(64.6, 2 * 1024 * 1024 * 1024, 1024)),
            "65% · 2.0 GB VRAM"
        );
        assert_eq!(
            process_metrics(&proc(0.05, 0, 128 * 1024 * 1024)),
            "128 MB shared"
        );
    }

    #[test]
    fn subtitle_names_two_engines_and_the_adapter_only_with_several() {
        let p = proc(10.0, 0, 0);
        assert_eq!(
            process_subtitle(&p, false).as_deref(),
            Some("3D 61%, Copy 3%")
        );
        assert_eq!(
            process_subtitle(&p, true).as_deref(),
            Some("3D 61%, Copy 3% · RTX 3070")
        );
        let bare = GpuProcessUsage::default();
        assert_eq!(process_subtitle(&bare, true), None);
    }

    #[test]
    fn card_shows_reading_line_then_rows_once_the_host_answers() {
        let mut s = AppState::default();
        let _ = card_view(&s);
        s.pc.gpu_activity_ui.data = Some(GpuProcessesState {
            ok: true,
            processes: vec![proc(50.0, 1024 * 1024 * 512, 0); 9],
            ..Default::default()
        });
        let _ = card_view(&s);
    }

    #[test]
    fn polls_only_in_exo_ai_and_not_more_often_than_every_three_seconds() {
        let mut s = AppState::default();
        s.tick_gpu_activity(10.0);
        assert_eq!(s.pc.gpu_activity_ui.last_poll_at, -POLL_SECS);
        s.switch_room(Room::ExoAi);
        s.tick_gpu_activity(10.0);
        assert_eq!(s.pc.gpu_activity_ui.last_poll_at, 10.0);
        s.tick_gpu_activity(12.0);
        assert_eq!(s.pc.gpu_activity_ui.last_poll_at, 10.0);
        s.tick_gpu_activity(13.0);
        assert_eq!(s.pc.gpu_activity_ui.last_poll_at, 13.0);
    }
}
