use crate::pc_menus::{driver_row_view, menu_btn};
use crate::pc_state::{
    AutoTuneStageStatus, PcPartMenu, PcTab, compute_initial_auto_tune_stages,
    count_pending_auto_tune, format_bytes, format_gpu_process_metrics, format_gpu_process_subtitle,
    overview_health_items, trim_stop,
};
use crate::pc_storage::{exo_ai_storage_view, format_gb_short};
use crate::state::{AppState, HostStatus, title_hue};
use exo_kit::{
    BG, BadgeTone, FG, GAP, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    RADIUS_SMALL, SPRING_QUICK, TITLEBAR_HEIGHT, bad, fg_faint, fg_muted, float_line, good,
    hover_fill, icon_app_window, icon_check, icon_chevron_right, icon_cpu, icon_hard_drive,
    icon_monitor, icon_zap, pressed_fill, primary_button, progress_bar, scaled, status_badge,
    subtle_fill, surface_card, track_fill, type_body, type_control, type_page, type_section,
    type_small, type_title, warn,
};
use exo_rpc::{DriverEntry, RuntimeItem, RuntimesProgress, TunePart};
use rune::{Cursor, El, ScrollSpec, SurfaceFit, TextStyle, col, el, oklch, row, text};
use serde_json::json;

fn strip_trademarks(raw: &str) -> String {
    let mut out = String::with_capacity(raw.len());
    let chars: Vec<char> = raw.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == '®' || chars[i] == '™' {
            i += 1;
            continue;
        }
        if chars[i] == '(' {
            if i + 2 < chars.len()
                && chars[i + 2] == ')'
                && matches!(chars[i + 1].to_ascii_lowercase(), 'r' | 'c')
            {
                i += 3;
                continue;
            }
            if i + 3 < chars.len()
                && chars[i + 3] == ')'
                && chars[i + 1].eq_ignore_ascii_case(&'t')
                && chars[i + 2].eq_ignore_ascii_case(&'m')
            {
                i += 4;
                continue;
            }
        }
        out.push(chars[i]);
        i += 1;
    }
    out
}

fn is_numeric_token(s: &str) -> bool {
    !s.is_empty()
        && s.chars().all(|c| c.is_ascii_digit() || c == '.')
        && s.chars().any(|c| c.is_ascii_digit())
}

pub(crate) fn short_drive_model(raw: &str) -> String {
    let cleaned = strip_trademarks(raw);
    let mut parts = Vec::new();
    let tokens: Vec<&str> = cleaned.split_whitespace().collect();
    let mut i = 0;
    while i < tokens.len() {
        let t = tokens[i];
        let lower = t.to_ascii_lowercase();
        if lower == "with" && i + 1 < tokens.len() && tokens[i + 1].eq_ignore_ascii_case("heatsink")
        {
            i += 2;
            continue;
        }
        if lower == "solid"
            && i + 2 < tokens.len()
            && tokens[i + 1].eq_ignore_ascii_case("state")
            && tokens[i + 2].eq_ignore_ascii_case("drive")
        {
            i += 3;
            continue;
        }
        if matches!(lower.as_str(), "ssd" | "nvme" | "m.2" | "series") {
            i += 1;
            continue;
        }
        if (lower.ends_with("tb") || lower.ends_with("gb"))
            && lower.len() > 2
            && is_numeric_token(&lower[..lower.len() - 2])
        {
            i += 1;
            continue;
        }
        if is_numeric_token(&lower)
            && i + 1 < tokens.len()
            && matches!(tokens[i + 1].to_ascii_lowercase().as_str(), "tb" | "gb")
        {
            i += 2;
            continue;
        }
        parts.push(t);
        i += 1;
    }
    if parts.is_empty() {
        cleaned.trim().to_string()
    } else {
        parts.join(" ")
    }
}

fn short_windows_name(raw: &str) -> String {
    let trimmed = raw.trim();
    let lower = trimmed.to_ascii_lowercase();
    let base = if lower.starts_with("windows 11") {
        Some("Windows 11")
    } else if lower.starts_with("windows 10") {
        Some("Windows 10")
    } else {
        None
    };
    match base {
        Some(name) if lower.contains("insider") => format!("{name} Insider"),
        Some(name) => name.to_string(),
        None => trimmed.to_string(),
    }
}

fn format_windows_label(about: Option<&exo_rpc::SystemAbout>) -> String {
    let name = about
        .and_then(|a| a.windows.as_deref())
        .map(short_windows_name)
        .filter(|s| !s.is_empty());
    let version = about
        .and_then(|a| a.windows_version.as_deref())
        .map(str::trim)
        .filter(|s| !s.is_empty());
    match (name, version) {
        (Some(w), Some(v)) => format!("{w} {v}"),
        (Some(w), None) => w,
        _ => about
            .and_then(|a| a.os.as_deref())
            .map(short_windows_name)
            .filter(|s| !s.is_empty())
            .unwrap_or_else(|| "Windows 11".into()),
    }
}

const SHADER_ROUGH: f64 = 1.5;

#[derive(Debug, Clone, Copy)]
struct ShaderSpan {
    low: f64,
    high: f64,
    learned: bool,
}

fn shader_js_round(value: f64) -> i64 {
    (value + 0.5).floor() as i64
}

fn shader_span(est: &exo_rpc::ShaderEstimate, share: f64) -> ShaderSpan {
    let sure = est.learned && est.low_seconds.is_none();
    let low = est.low_seconds.unwrap_or(if sure {
        est.seconds
    } else {
        est.seconds / SHADER_ROUGH
    }) * share;
    let high = if est.low_seconds.is_some() || sure {
        est.seconds
    } else {
        est.seconds * SHADER_ROUGH
    } * share;
    ShaderSpan {
        low,
        high,
        learned: sure,
    }
}

fn shader_minute_text(minutes: i64) -> String {
    let h = minutes / 60;
    let m = minutes % 60;
    if h > 0 {
        if m > 0 {
            format!("{h} h {m} min")
        } else {
            format!("{h} h")
        }
    } else {
        format!("{m} min")
    }
}

fn shader_round_span(seconds: f64) -> String {
    if seconds < 60.0 {
        return "under a minute".into();
    }
    let minutes = seconds / 60.0;
    let step: i64 = if minutes < 10.0 {
        1
    } else if minutes < 60.0 {
        5
    } else if minutes < 600.0 {
        10
    } else {
        60
    };
    let rounded = (shader_js_round(minutes / step as f64) * step).max(step);
    shader_minute_text(rounded)
}

fn shader_range_text(low: f64, high: f64) -> String {
    if high < 60.0 {
        return "under a minute".into();
    }
    let minutes = high / 60.0;
    let step: i64 = if minutes < 10.0 {
        1
    } else if minutes < 60.0 {
        5
    } else if minutes < 600.0 {
        30
    } else {
        60
    };
    let top = (shader_js_round(minutes / step as f64) * step).max(step);
    let bottom = shader_js_round(low / 60.0 / step as f64) * step;
    if bottom <= 0 {
        return format!("up to {}", shader_minute_text(top));
    }
    if bottom >= top {
        return format!("about {}", shader_minute_text(top));
    }
    if top < 60 {
        return format!("{bottom}–{}", shader_minute_text(top));
    }
    if bottom % 60 == 0 && top % 60 == 0 {
        format!("{}–{} h", bottom / 60, top / 60)
    } else {
        format!("{}–{}", shader_minute_text(bottom), shader_minute_text(top))
    }
}

fn format_shader_estimate(est: &exo_rpc::ShaderEstimate) -> String {
    let span = shader_span(est, 1.0);
    if !span.learned {
        shader_range_text(span.low, span.high)
    } else if span.high < 60.0 {
        "under a minute".into()
    } else {
        format!("about {}", shader_round_span(span.high))
    }
}

fn resolve_shader_estimate(
    sg: &exo_rpc::ShaderSnapGame,
    shader_estimates: &std::collections::HashMap<String, f64>,
) -> Option<exo_rpc::ShaderEstimate> {
    sg.estimate.clone().or_else(|| {
        shader_estimates
            .get(&sg.id)
            .copied()
            .map(|seconds| exo_rpc::ShaderEstimate {
                seconds,
                low_seconds: None,
                learned: false,
            })
    })
}

fn shader_whole_percent(percent: f64) -> Option<u32> {
    if !percent.is_finite() {
        return None;
    }
    let share = (percent / 100.0).clamp(0.0, 1.0);
    let cap = if share < 1.0 { 99 } else { 100 };
    Some(((share * 100.0 + 1e-9).floor() as u32).min(cap))
}

fn shader_poster_subtitle(
    sg: &exo_rpc::ShaderSnapGame,
    place: Option<usize>,
    estimate: Option<&exo_rpc::ShaderEstimate>,
) -> String {
    if sg.compiling {
        if let Some(pct) = sg.percent.and_then(shader_whole_percent) {
            return format!("Compiling · {pct}%");
        }
        return sg
            .status_text
            .as_deref()
            .filter(|s| !s.is_empty())
            .unwrap_or("Compiling")
            .to_string();
    }
    let time = if place.is_some() || sg.in_add_all_ready {
        estimate.map(format_shader_estimate)
    } else {
        None
    };
    if let Some(pos) = place {
        let head = if pos == 1 {
            "Next".to_string()
        } else {
            format!("#{pos}")
        };
        return match time {
            Some(t) => format!("{head} · {t}"),
            None => head,
        };
    }
    time.or_else(|| sg.status_text.clone()).unwrap_or_default()
}

fn shader_prepare_subtitle(sg: &exo_rpc::ShaderPrepareGame) -> String {
    match sg.state.as_str() {
        "running" => "Compiling its first launch".into(),
        "stale" => "Driver changed".into(),
        _ => sg
            .message
            .clone()
            .filter(|m| !m.is_empty())
            .unwrap_or_else(|| "First launch".into()),
    }
}

fn shader_eta_text(span: ShaderSpan, floor: bool, running: bool) -> Option<String> {
    if span.high <= 0.0 {
        return None;
    }
    let left = if running { " left" } else { "" };
    if floor {
        return (span.low >= 60.0).then(|| format!("over {}{left}", shader_round_span(span.low)));
    }
    if !span.learned {
        return Some(format!("{}{left}", shader_range_text(span.low, span.high)));
    }
    if span.high < 60.0 {
        return Some(format!("under a minute{left}"));
    }
    Some(format!("about {}{left}", shader_round_span(span.high)))
}

const PAWNIO_URL: &str = "https://pawnio.eu";
const SONY_CTRL_URL: &str = "https://www.playstation.com/support/hardware/ps5-controller-update/";

fn tune_metrics(part: &TunePart) -> Vec<(&'static str, String)> {
    let mut out = Vec::new();
    if let Some(ref applied) = part.applied
        && !applied.is_empty()
    {
        out.push(("Applied", applied.clone()));
    }
    if let (Some(before), Some(after)) = (part.before.as_ref(), part.after.as_ref()) {
        if before.score > 0.0 {
            let delta = ((after.score / before.score - 1.0) * 100.0).round() as i64;
            let val = if delta > 0 {
                format!("+{delta}%")
            } else if delta < 0 {
                format!("{delta}%")
            } else {
                "Same".into()
            };
            out.push(("Score", val));
        }
        if let (Some(bp), Some(ap)) = (before.peak_c, after.peak_c) {
            out.push((
                "Peak temp",
                format!("{} to {} °C", bp.round() as i64, ap.round() as i64),
            ));
        }
    }
    out
}

fn auto_tune_hardware_row(
    part: &TunePart,
    name: String,
    error: Option<&String>,
    armed: Option<&str>,
) -> El {
    let is_cpu = part.id == "ryzen";
    let role_label = if is_cpu {
        "CPU undervolt (Curve Optimizer)"
    } else {
        "GPU overclock & undervolt"
    };
    let tune_label = if is_cpu { "Tune CPU" } else { "Tune GPU" };
    let ready = matches!(part.state.as_str(), "ready" | "crashed");
    let part_id = part.id.clone();

    let mut actions = row().gap(6.0).center_y();
    if part.state == "tuned" {
        actions = actions.child(status_badge("Tuned".into(), BadgeTone::Good));
    }
    if part.state == "needs-driver" {
        actions = actions.child(
            menu_btn(
                format!("tune-pawnio-{}", part.id),
                "Get PawnIO",
                false,
                false,
                false,
            )
            .on_press(|s: &mut AppState| s.open_external_url(PAWNIO_URL)),
        );
    }
    if ready {
        let warned = part.warning.as_ref().is_some_and(|w| !w.is_empty());
        let blocked = part.blocked.is_some();
        let confirm_key = format!("tune:{}", part.id);
        let is_armed = armed == Some(confirm_key.as_str());
        let lbl = if warned && is_armed {
            "Start at your own risk?"
        } else {
            tune_label
        };
        let pid = part_id.clone();
        actions = actions.child(
            menu_btn(
                format!("ai-tune-{}", if is_cpu { "cpu" } else { "gpu" }),
                lbl,
                true,
                false,
                blocked,
            )
            .on_press(move |s: &mut AppState| {
                if warned && s.pc.armed_confirm.as_deref() != Some(confirm_key.as_str()) {
                    s.pc.armed_confirm = Some(confirm_key.clone());
                } else {
                    s.run_system_tune_action(&pid, "start");
                }
            }),
        );
    }
    if part.state == "testing" {
        let pid = part_id.clone();
        actions = actions.child(
            menu_btn(
                format!("tune-stop-{}", part.id),
                "Stop",
                false,
                false,
                false,
            )
            .on_press(move |s: &mut AppState| s.run_system_tune_action(&pid, "stop")),
        );
    } else if part.can_undo {
        let pid = part_id.clone();
        actions = actions.child(
            menu_btn(
                format!("tune-undo-{}", part.id),
                "Undo",
                false,
                false,
                false,
            )
            .on_press(move |s: &mut AppState| s.run_system_tune_action(&pid, "undo")),
        );
    }

    let mut row_wrap = col().w_full().py(10.0).gap(6.0).child(
        row()
            .w_full()
            .min_h(36.0)
            .justify_between()
            .center_y()
            .gap(10.0)
            .child(
                row()
                    .gap(10.0)
                    .center_y()
                    .min_w(0.0)
                    .grow(1.0)
                    .child(el().size(16.0, 16.0).center().child(if is_cpu {
                        icon_cpu(15.0, fg_muted())
                    } else {
                        icon_monitor(15.0, fg_muted())
                    }))
                    .child(
                        col()
                            .gap(2.0)
                            .min_w(0.0)
                            .grow(1.0)
                            .child(text(role_label).font(type_section()).color(FG))
                            .child(text(name).font(type_small()).color(fg_muted()).truncate()),
                    ),
            )
            .child(actions),
    );

    if part.state == "testing" {
        let mins = part
            .minutes_left
            .filter(|&m| m > 0.0)
            .map(|m| format!(" · about {} min left", m.round() as i64))
            .unwrap_or_default();
        let lbl = format!("{}{mins}", part.testing.as_deref().unwrap_or("Testing"));
        row_wrap = row_wrap.child(
            col()
                .w_full()
                .gap(4.0)
                .child(
                    row()
                        .w_full()
                        .h(4.0)
                        .radius(2.0)
                        .fill(track_fill())
                        .child(el().grow(0.5).h_full().radius(2.0).fill(FG))
                        .child(el().grow(0.5).h_full()),
                )
                .child(text(lbl).font(type_small()).color(fg_muted())),
        );
    } else if part.state == "tuned" {
        let metrics = tune_metrics(part);
        if !metrics.is_empty() {
            let mut mrow = row().gap(16.0).center_y();
            for (k, v) in metrics {
                mrow = mrow.child(
                    row()
                        .gap(5.0)
                        .center_y()
                        .child(text(k).font(type_small()).color(fg_muted()))
                        .child(text(v).font(type_small()).color(FG)),
                );
            }
            row_wrap = row_wrap.child(mrow);
        }
    } else {
        let detail = part
            .blocked
            .as_deref()
            .or_else(|| (part.state == "crashed").then_some("Returned to stock after instability"));
        if let Some(d) = detail {
            row_wrap = row_wrap.child(text(d.to_string()).font(type_small()).color(warn()).wrap());
        }
    }

    if let Some(err) = error {
        row_wrap = row_wrap.child(text(err.clone()).font(type_small()).color(bad()).wrap());
    }

    row_wrap
}

fn runtime_mb(bytes: u64) -> String {
    if bytes >= 1_000_000_000 {
        format!("{:.1} GB", bytes as f64 / 1e9)
    } else {
        format!("{} MB", (bytes as f64 / 1e6).round().max(1.0) as u64)
    }
}

fn runtime_progress_text(prog: Option<&RuntimesProgress>, mode: &str) -> String {
    let Some(p) = prog else {
        return if mode == "repair" {
            "Getting Visual C++ ready".into()
        } else {
            "Getting ready".into()
        };
    };
    let name = p.name.as_deref().unwrap_or("runtime");
    let of = format!("{} of {}", p.index, p.count);
    match p.phase.as_str() {
        "download" => format!(
            "Downloading {name} · {of} · {} of {}",
            runtime_mb(p.done_bytes),
            runtime_mb(p.all_bytes)
        ),
        "verify" => format!("Checking {name}"),
        "install" => format!("Installing {name} · {of}"),
        _ => "Checking what installed".into(),
    }
}

fn runtimes_section_view(s: &AppState) -> Option<El> {
    let items = s.pc.runtimes.as_ref()?;
    let missing: Vec<&RuntimeItem> = items
        .iter()
        .filter(|i| i.state == "missing" && i.bulk)
        .collect();
    let outdated: Vec<&RuntimeItem> = items.iter().filter(|i| i.state == "outdated").collect();
    let any_work = !missing.is_empty() || !outdated.is_empty();
    if !any_work && s.pc.runtimes_running.is_none() && s.pc.runtimes_note.is_none() {
        return None;
    }

    let mut sec = col()
        .key("pc-runtimes-section")
        .w_full()
        .pt(14.0)
        .gap(6.0)
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(
            el().w_full()
                .pt(8.0)
                .child(text("Game runtimes").font(type_section()).color(FG)),
        );

    if let Some(ref mode) = s.pc.runtimes_running {
        let prog = s.pc.runtimes_progress.as_ref();
        let words = runtime_progress_text(prog, mode);
        let share = prog.and_then(|p| match p.phase.as_str() {
            "install" if p.count > 0 => Some((p.index.saturating_sub(1)) as f32 / p.count as f32),
            "check" => None,
            _ if p.all_bytes > 0 => Some((p.done_bytes as f64 / p.all_bytes as f64) as f32),
            _ => None,
        });
        let downloading = prog.is_none_or(|p| matches!(p.phase.as_str(), "download" | "verify"));
        let mut r = row().w_full().justify_between().center_y().gap(10.0).child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(4.0)
                .child(text(words).font(type_small()).color(fg_muted()).wrap())
                .child(progress_bar(360.0, share.unwrap_or(0.35))),
        );
        if downloading {
            r = r.child(
                menu_btn(
                    "runtimes-cancel".into(),
                    "Cancel",
                    false,
                    false,
                    s.pc.runtimes_canceling,
                )
                .on_press(|s: &mut AppState| s.run_runtimes_cancel()),
            );
        }
        sec = sec.child(r);
    } else if any_work {
        let mut parts = Vec::new();
        if !missing.is_empty() {
            parts.push(format!("{} to install", missing.len()));
        }
        if !outdated.is_empty() {
            parts.push(format!("{} to update", outdated.len()));
        }
        let dl_bytes: u64 = missing
            .iter()
            .chain(outdated.iter())
            .map(|i| if i.size > 0 { i.size } else { 60_000_000 })
            .sum();
        let status_txt = format!("{} · about {}", parts.join(", "), runtime_mb(dl_bytes));
        let action_word = if !missing.is_empty() && !outdated.is_empty() {
            "Install and update"
        } else if !missing.is_empty() {
            "Install"
        } else {
            "Update"
        };
        sec = sec.child(
            row()
                .w_full()
                .min_h(54.0)
                .py(10.0)
                .justify_between()
                .center_y()
                .gap(8.0)
                .child(text(status_txt).font(type_small()).color(fg_muted()))
                .child(
                    row()
                        .gap(6.0)
                        .child(
                            menu_btn("runtimes-install".into(), action_word, true, false, false)
                                .on_press(|s: &mut AppState| s.run_runtimes_install("update")),
                        )
                        .child(
                            menu_btn("runtimes-repair".into(), "Repair all", false, false, false)
                                .on_press(|s: &mut AppState| s.run_runtimes_install("repair")),
                        ),
                ),
        );
    }

    if let Some((ref note, is_err)) = s.pc.runtimes_note {
        sec = sec.child(
            text(note.clone())
                .font(type_small())
                .color(if is_err { bad() } else { fg_muted() })
                .wrap(),
        );
    }

    for item in items.iter().filter(|i| {
        (i.state == "missing" && i.bulk)
            || i.state == "outdated"
            || s.pc
                .runtimes_results
                .get(&i.id)
                .is_some_and(|r| r.outcome == "failed")
            || s.pc
                .runtimes_progress
                .as_ref()
                .and_then(|p| p.id.as_deref())
                == Some(i.id.as_str())
    }) {
        let right = s.pc.runtimes_results.get(&item.id).map(|res| {
            let word = match res.outcome.as_str() {
                "installed" => "Installed",
                "newer" => "Already newer",
                "restart" => "Restart to finish",
                _ => "Failed",
            };
            status_badge(
                word.into(),
                if res.outcome == "failed" {
                    BadgeTone::Bad
                } else {
                    BadgeTone::Good
                },
            )
        });
        let detail = if item.state == "missing" {
            "Missing".into()
        } else {
            format!(
                "{} → {}",
                item.installed.as_deref().unwrap_or(""),
                item.latest
            )
        };
        sec = sec.child(el().w_full().h(1.0).fill(float_line())).child(
            row()
                .w_full()
                .min_h(58.0)
                .py(10.0)
                .justify_between()
                .center_y()
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(2.0)
                        .child(text(item.name.clone()).font(type_control()).color(FG))
                        .child(text(detail).font(type_small()).color(fg_muted())),
                )
                .children(right),
        );
    }

    Some(sec)
}

fn driver_row_name(item: &DriverEntry) -> String {
    if item.kind == "ssd" {
        format!("{} firmware", short_drive_model(&item.name))
    } else {
        strip_trademarks(&item.name)
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ")
    }
}

fn updates_card_view(s: &AppState) -> El {
    let drivers_state = s.pc.drivers_state.as_ref();
    let health = s.pc.health.as_ref();
    let gpu_drv = s.pc.gpu_driver.as_ref();
    let armed = s.pc.armed_confirm.as_deref();

    let rows: Vec<&DriverEntry> = drivers_state
        .map(|ds| {
            ds.items
                .iter()
                .filter(|i| i.how != "none" || s.pc.driver_steps.contains_key(&i.id))
                .collect()
        })
        .unwrap_or_default();
    let chipset_item = health.and_then(|h| {
        h.items
            .iter()
            .find(|i| i.key == "chipset" && !i.current && !i.held)
    });
    let chipset_needs = chipset_item.is_some_and(|i| !i.applied && !i.busy);
    let gpu_needs =
        gpu_drv.is_some_and(|d| d.update || d.advised.as_ref().is_some_and(|a| a.back.is_some()));
    let gpu_shown = gpu_needs
        || gpu_drv
            .and_then(|d| s.pc.gpu_driver_progress.as_ref().or(d.progress.as_ref()))
            .is_some_and(|p| !matches!(p.stage.as_str(), "idle" | "done"));
    let runtime_waiting =
        s.pc.runtimes
            .as_ref()
            .map(|items| {
                items
                    .iter()
                    .filter(|i| (i.state == "missing" && i.bulk) || i.state == "outdated")
                    .count()
            })
            .unwrap_or(0);
    let installable =
        rows.iter().any(|i| i.how == "install" && !i.app) || chipset_needs || gpu_needs;
    let waiting = rows.iter().filter(|i| !i.app && i.how != "none").count()
        + usize::from(chipset_needs)
        + usize::from(gpu_needs)
        + runtime_waiting;
    let working = s.pc.drivers_updating_all || drivers_state.is_some_and(|d| d.running);
    let busy_reason = drivers_state.and_then(|d| d.busy.as_deref());

    let mut head_right = row().gap(8.0).center_y();
    if (drivers_state.is_none() && s.pc.drivers_error.is_none()) || (waiting == 0 && !working) {
        head_right = head_right.child(text("Up to date").font(type_body()).color(fg_muted()));
    }
    if installable {
        head_right = head_right.child(
            menu_btn(
                "drivers-update-all".into(),
                if working { "Updating…" } else { "Update all" },
                true,
                false,
                working || busy_reason.is_some(),
            )
            .on_press(|s: &mut AppState| s.run_drivers_update_all()),
        );
    }

    let mut card = col()
        .key("exo-ai-updates-card")
        .w_full()
        .pt(16.0)
        .px(24.0)
        .pb(16.0)
        .gap(4.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(exo_kit::surface_elevated())
        .border_gradient(1.0, exo_kit::glass_rim_top(), exo_kit::glass_rim_bottom())
        .child(
            row()
                .w_full()
                .min_h(32.0)
                .justify_between()
                .center_y()
                .child(text("Updates").font(type_title()).color(FG))
                .child(head_right),
        );

    let restart = drivers_state.is_some_and(|d| d.restart);
    let blocked_err = s.pc.drivers_error.clone().or_else(|| {
        (installable && !working)
            .then(|| busy_reason.map(str::to_string))
            .flatten()
    });
    if working || restart || blocked_err.is_some() {
        let lbl = if working {
            "Updating"
        } else if restart {
            "Restart to finish"
        } else {
            "Updates"
        };
        card = card.child(
            col()
                .w_full()
                .pt(6.0)
                .gap(2.0)
                .child(
                    text(lbl)
                        .font(type_control())
                        .color(if restart { warn() } else { FG }),
                )
                .children(blocked_err.map(|e| text(e).font(type_small()).color(bad()).wrap())),
        );
    }

    if gpu_shown && let Some(drv_row) = driver_row_view(s) {
        card = card.child(drv_row);
    }

    if let Some(chip) = chipset_item {
        let ctrl = if chip.busy {
            menu_btn("chipset-busy".into(), "Updating…", false, false, true)
        } else if let Some(ref url) = chip.url {
            let u = url.clone();
            menu_btn("chipset-url".into(), "Update", false, false, false)
                .on_press(move |s: &mut AppState| s.open_external_url(&u))
        } else if chip.applied {
            status_badge("Restart to finish".into(), BadgeTone::Warn)
        } else {
            menu_btn("chipset-upd".into(), "Update", false, false, false)
                .on_press(|s: &mut AppState| s.run_health_action("chipset"))
        };
        card = card.child(
            col()
                .w_full()
                .child(el().w_full().h(1.0).fill(float_line()))
                .child(
                    row()
                        .w_full()
                        .py(8.0)
                        .justify_between()
                        .center_y()
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(text(chip.label.clone()).font(type_control()).color(FG))
                                .children(
                                    ((chip.busy || chip.applied) && !chip.detail.is_empty()).then(
                                        || {
                                            text(chip.detail.clone())
                                                .font(type_small())
                                                .color(fg_muted())
                                                .wrap()
                                        },
                                    ),
                                ),
                        )
                        .child(ctrl),
                ),
        );
    }

    for item in rows {
        if item.kind == "bios"
            && item.source.as_deref() == Some("ASUS")
            && (item.how != "none" || item.advised.is_some())
        {
            let ready =
                s.pc.bios_staged
                    .as_ref()
                    .is_some_and(|st| st.version == item.latest.as_deref().unwrap_or(&st.version));
            let busy = s.pc.bios_busy.as_deref();
            let det = if busy == Some("prepare") {
                match s.pc.bios_progress {
                    Some(sh) => {
                        format!("Getting the BIOS ready · {}%", (sh * 100.0).round() as i32)
                    }
                    None => "Getting the BIOS ready".into(),
                }
            } else if ready && let Some(ref st) = s.pc.bios_staged {
                format!(
                    "{} is ready. In the BIOS: Tool › ASUS EZ Flash 3, pick {} and keep the power on; if it asks to reset fTPM, choose No.",
                    st.version, st.file
                )
            } else {
                item.advised
                    .as_ref()
                    .map(|a| a.why.clone())
                    .unwrap_or_default()
            };
            let ctrl = if ready {
                let is_armed = armed == Some("bios-update");
                menu_btn(
                    "bios-restart-upd".into(),
                    if is_armed {
                        "Restart now?"
                    } else {
                        "Restart to update"
                    },
                    false,
                    true,
                    busy.is_some(),
                )
                .on_press(|s: &mut AppState| {
                    if s.pc.armed_confirm.as_deref() == Some("bios-update") {
                        s.run_bios_update("restart");
                    } else {
                        s.pc.armed_confirm = Some("bios-update".into());
                    }
                })
            } else {
                let ver = item.latest.as_deref().unwrap_or("it");
                menu_btn(
                    "bios-prepare".into(),
                    format!("Get {ver} ready"),
                    false,
                    false,
                    busy.is_some(),
                )
                .on_press(|s: &mut AppState| s.run_bios_update("prepare"))
            };
            card = card.child(
                col()
                    .w_full()
                    .child(el().w_full().h(1.0).fill(float_line()))
                    .child(
                        row()
                            .w_full()
                            .py(8.0)
                            .gap(10.0)
                            .justify_between()
                            .center_y()
                            .child(
                                col()
                                    .grow(1.0)
                                    .min_w(0.0)
                                    .gap(2.0)
                                    .child(text("BIOS").font(type_control()).color(FG))
                                    .children((!det.is_empty()).then(|| {
                                        text(det).font(type_small()).color(fg_muted()).wrap()
                                    }))
                                    .children(s.pc.bios_error.as_ref().map(|e| {
                                        text(e.clone()).font(type_small()).color(bad()).wrap()
                                    })),
                            )
                            .child(ctrl),
                    ),
            );
            continue;
        }

        let step = s.pc.driver_steps.get(&item.id);
        let detail = step
            .and_then(|st| st.working.clone())
            .or_else(|| {
                step.filter(|st| st.ok == Some(true)).map(|st| {
                    st.message
                        .as_deref()
                        .map(trim_stop)
                        .filter(|m| !m.is_empty())
                        .unwrap_or_else(|| "Updated".into())
                })
            })
            .or_else(|| item.advised.as_ref().map(|a| a.why.clone()));
        let err = step.filter(|st| st.ok == Some(false)).map(|st| {
            st.message
                .as_deref()
                .map(trim_stop)
                .filter(|m| !m.is_empty())
                .unwrap_or_else(|| "Didn’t update".into())
        });
        let ctrl = if item.how == "page"
            && let Some(ref page) = item.page
        {
            let url = page.clone();
            let word = item
                .advised
                .as_ref()
                .filter(|a| a.to.is_some() && a.action != "skip")
                .map(|a| {
                    if a.action == "back" {
                        format!("Go back to {}", a.to.as_deref().unwrap_or(""))
                    } else {
                        let target = if item.skipped.is_some() {
                            a.to.as_deref().unwrap_or("")
                        } else {
                            item.latest.as_deref().or(a.to.as_deref()).unwrap_or("")
                        };
                        format!("Update to {target}")
                    }
                })
                .unwrap_or_else(|| {
                    item.source
                        .as_deref()
                        .map(|src| format!("Open {src}"))
                        .unwrap_or_else(|| "Open".into())
                });
            menu_btn(format!("drv-page-{}", item.id), word, false, false, false)
                .on_press(move |s: &mut AppState| s.open_external_url(&url))
        } else if item.how == "install" && item.app {
            let ck = format!("driver-app:{}", item.id);
            let is_armed = armed == Some(ck.as_str());
            let did = item.id.clone();
            menu_btn(
                format!("drv-inst-{}", item.id),
                if is_armed {
                    format!("Install {}?", item.name)
                } else {
                    "Install".into()
                },
                is_armed,
                false,
                working
                    || busy_reason.is_some()
                    || step.and_then(|st| st.working.as_ref()).is_some(),
            )
            .on_press(move |s: &mut AppState| {
                if s.pc.armed_confirm.as_deref() == Some(ck.as_str()) {
                    s.run_driver_install(&did);
                } else {
                    s.pc.armed_confirm = Some(ck.clone());
                }
            })
        } else if item.how == "install" {
            let did = item.id.clone();
            menu_btn(
                format!("drv-upd-{}", item.id),
                if step.and_then(|st| st.working.as_ref()).is_some() {
                    "Updating…"
                } else {
                    "Install"
                },
                false,
                false,
                working || busy_reason.is_some(),
            )
            .on_press(move |s: &mut AppState| s.run_driver_install(&did))
        } else {
            row()
        };

        card = card.child(
            col()
                .w_full()
                .child(el().w_full().h(1.0).fill(float_line()))
                .child(
                    row()
                        .w_full()
                        .py(8.0)
                        .gap(10.0)
                        .justify_between()
                        .center_y()
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(text(driver_row_name(item)).font(type_control()).color(FG))
                                .children(
                                    detail.map(|d| {
                                        text(d).font(type_small()).color(fg_muted()).wrap()
                                    }),
                                )
                                .children(
                                    err.map(|e| text(e).font(type_small()).color(bad()).wrap()),
                                ),
                        )
                        .child(ctrl),
                ),
        );
    }

    for (id, name, current) in &s.pc.controller_firmwares {
        if !current {
            let clean = name.trim_end_matches(" Wireless Controller");
            card = card.child(
                col()
                    .w_full()
                    .child(el().w_full().h(1.0).fill(float_line()))
                    .child(
                        row()
                            .w_full()
                            .py(8.0)
                            .justify_between()
                            .center_y()
                            .child(
                                text(format!("{clean} firmware"))
                                    .font(type_control())
                                    .color(FG),
                            )
                            .child(
                                menu_btn(format!("ctrl-fw-{id}"), "Open Sony", false, false, false)
                                    .on_press(|s: &mut AppState| {
                                        s.open_external_url(SONY_CTRL_URL)
                                    }),
                            ),
                    ),
            );
        }
    }

    if let Some(rel) =
        s.pc.system_about
            .as_ref()
            .and_then(|a| a.windows_release.as_ref())
        && !rel.insider
        && rel.up_to_date == Some(false)
    {
        let lbl = match rel.newest.as_deref() {
            Some(n) => format!("{} {n}", rel.name),
            None => rel.name.clone(),
        };
        card = card.child(
            col()
                .w_full()
                .child(el().w_full().h(1.0).fill(float_line()))
                .child(
                    row()
                        .w_full()
                        .py(8.0)
                        .justify_between()
                        .center_y()
                        .child(text(lbl).font(type_control()).color(FG))
                        .child(
                            menu_btn(
                                "win-update-open".into(),
                                "Open Windows Update",
                                false,
                                false,
                                false,
                            )
                            .on_press(|s: &mut AppState| {
                                s.open_external_url("ms-settings:windowsupdate");
                            }),
                        ),
                ),
        );
    }

    if let Some(rt_sec) = runtimes_section_view(s) {
        card = card.child(rt_sec);
    }

    card
}

pub(crate) fn exo_ai_room(s: &AppState, scale: f32) -> El {
    if s.pc.exo_ai_storage_open || s.pc.pc_tab == PcTab::Storage {
        return exo_ai_storage_view(s);
    }

    let about = s.pc.system_about.as_ref();
    let health = s.pc.health.as_ref();
    let pc_name = about
        .and_then(|a| a.name.as_deref())
        .filter(|n| !n.is_empty())
        .or_else(|| s.pc.gpu_profile.as_ref().and_then(|p| p.gpu.as_deref()))
        .unwrap_or("This PC");
    let cpu_name = about
        .and_then(|a| a.processor.as_deref().or(a.cpu.as_deref()))
        .unwrap_or("Processor");
    let gpu_name =
        s.pc.gpu_profile
            .as_ref()
            .and_then(|p| p.gpu.as_deref())
            .or(s.pc.gpu_short_name.as_deref())
            .or_else(|| about.and_then(|a| a.graphics.as_deref().or(a.gpu.as_deref())))
            .unwrap_or("Graphics");
    let ram_label = match (
        about.and_then(|a| a.memory_gb.or(a.ram_gb)),
        about
            .and_then(|a| a.memory_type.as_deref())
            .filter(|t| !t.is_empty()),
    ) {
        (Some(gb), Some(t)) => format!("{} GB {t}", gb.round() as u32),
        (Some(gb), None) => format!("{} GB", gb.round() as u32),
        _ => "Memory".into(),
    };
    let display_label =
        s.pc.screen_label
            .as_deref()
            .or_else(|| about.and_then(|a| a.display.as_deref()))
            .unwrap_or("Primary display");
    let board_label = about
        .and_then(|a| a.motherboard.as_deref())
        .unwrap_or("Motherboard");
    let net_label = {
        let base = about.and_then(|a| a.network.as_deref());
        let speed = about
            .and_then(|a| a.network_mbps)
            .filter(|&m| m > 0.0)
            .map(|mbps| {
                if mbps >= 1000.0 {
                    let gbps = (mbps / 100.0).round() / 10.0;
                    if (gbps - gbps.round()).abs() < 0.01 {
                        format!("{:.0} Gbps", gbps)
                    } else {
                        format!("{gbps:.1} Gbps")
                    }
                } else {
                    format!("{:.0} Mbps", mbps.round())
                }
            });
        match (base, speed) {
            (Some(b), Some(sp)) if !b.contains("bps") => format!("{b} · {sp}"),
            (Some(b), _) => b.to_string(),
            (None, Some(sp)) => sp,
            (None, None) => about
                .and_then(|a| a.network_adapter.as_deref())
                .map(strip_trademarks)
                .unwrap_or_else(|| "Network".into()),
        }
    };
    let win_label = format_windows_label(about);

    let kept_pc =
        s.pc.keep_status
            .as_ref()
            .map(|k| k.pc.as_str())
            .unwrap_or("none");
    let pcie_warn = health
        .and_then(|h| h.items.iter().find(|i| i.key == "pcie"))
        .map(|i| i.detail.as_str())
        .filter(|d| !d.is_empty());
    let cable_warn = health
        .and_then(|h| h.items.iter().find(|i| i.key == "gpucable"))
        .map(|i| i.detail.as_str())
        .filter(|d| !d.is_empty());
    let netlink_warn = health
        .and_then(|h| h.items.iter().find(|i| i.key == "netlink"))
        .map(|i| i.detail.as_str())
        .filter(|d| !d.is_empty());

    let gpu_sub_note: Option<(String, bool)> = match kept_pc {
        "putBackAfterDriverUpdate" => {
            Some(("Settings put back after the driver update".into(), false))
        }
        "needsYourOk" => Some(("A setting needs your OK".into(), true)),
        _ => pcie_warn.map(|w| (w.to_string(), true)),
    };

    let board_warn: Option<String> = {
        let mut p = Vec::new();
        if about.and_then(|a| a.secure_boot) == Some(false) {
            p.push("Secure Boot off");
        }
        if about.and_then(|a| a.tpm.as_deref()) == Some("None") {
            p.push("No TPM");
        }
        (!p.is_empty()).then(|| p.join(" · "))
    };

    let sys_drive = s.pc.drives.as_ref().and_then(|d| d.drives.first());
    let (drive_title, drive_sub, drive_pct, drive_note) = if let Some(d) = sys_drive {
        let letters = d
            .volumes
            .iter()
            .map(|v| v.letter.as_str())
            .filter(|l| !l.is_empty())
            .collect::<Vec<_>>()
            .join(" ");
        let total_gb: f64 = d.volumes.iter().map(|v| v.total_gb).sum::<f64>().max(1.0);
        let free_gb: f64 = d.volumes.iter().map(|v| v.free_gb).sum();
        let used = ((total_gb - free_gb) / total_gb).clamp(0.0, 1.0) as f32;
        let model = short_drive_model(&d.model);
        let title = if letters.is_empty() {
            model
        } else if model.is_empty() {
            letters
        } else {
            format!("{letters} {model}")
        };
        let wrong = d
            .verdicts
            .iter()
            .filter(|v| !v.key.starts_with("bypass:") && v.key != "ok")
            .max_by_key(|v| v.bad);
        let hot = d.temperature.as_ref().and_then(|t| {
            let warn_t = t.warn?;
            if t.now >= warn_t {
                Some(("Running hot".to_string(), true))
            } else if t.now >= warn_t - 5 {
                Some(("Running hot".to_string(), false))
            } else {
                None
            }
        });
        let note = wrong.map(|v| (v.text.clone(), v.bad)).or(hot);
        (
            title,
            format!(
                "{} free of {}",
                format_gb_short(free_gb),
                format_gb_short(total_gb)
            ),
            used,
            note,
        )
    } else {
        (
            "C: System drive".into(),
            "Reading drives…".into(),
            0.0,
            None,
        )
    };

    let needs_drive_fix =
        s.pc.drives
            .as_ref()
            .is_some_and(|d| d.fixes.iter().any(|f| !f.applied));
    let has_bad_drive =
        s.pc.drives
            .as_ref()
            .is_some_and(|d| d.drives.iter().any(|dr| dr.verdicts.iter().any(|v| v.bad)));
    let potential =
        s.pc.compress_list
            .as_ref()
            .map(|c| c.potential)
            .unwrap_or(s.pc.compress_potential_bytes);
    let saved =
        s.pc.compress_list
            .as_ref()
            .map(|c| c.saved)
            .unwrap_or(s.pc.compress_saved_bytes);

    let (storage_label, storage_fg) = if has_bad_drive || needs_drive_fix {
        ("Storage · Needs check".to_string(), bad())
    } else if potential > 0 {
        (format!("Storage · {}", format_bytes(potential)), FG)
    } else if saved > 0 {
        (format!("Storage · {}", format_bytes(saved)), good())
    } else {
        ("Storage".to_string(), FG)
    };

    let gaming = s.pc.exo_ai.as_ref().is_some_and(|a| a.gaming.active);
    let testing =
        s.pc.auto_tune
            .as_ref()
            .is_some_and(|t| t.parts.iter().any(|p| p.state == "testing"));
    let autopilot_on = s.pc.autopilot.as_ref().is_none_or(|a| a.on);
    let live_text = if gaming {
        match s
            .pc
            .exo_ai
            .as_ref()
            .and_then(|a| a.gaming.game_title.as_deref())
        {
            Some(title) if !title.is_empty() => format!("Boosting {title}"),
            _ => "Boosting game".into(),
        }
    } else if testing {
        "Calibrating hardware".into()
    } else if autopilot_on {
        "Autopilot active".into()
    } else {
        "Autopilot paused".into()
    };

    let pending = count_pending_auto_tune(&s.pc, &s.settings);
    let is_complete = !s.pc.auto_tune_running
        && (s.pc.auto_tune_completed_at.is_some()
            || (pending == Some(0) && s.pc.gpu_profile.is_some() && s.pc.health.is_some()));

    let header_action = if is_complete {
        row()
            .gap(8.0)
            .center_y()
            .child(
                row()
                    .key("ai-complete-badge")
                    .h(36.0)
                    .px(14.0)
                    .gap(7.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(good().alpha(0.10))
                    .border(1.0, good().alpha(0.28))
                    .child(icon_check(14.0, good()))
                    .child(
                        text("Auto Tune complete")
                            .font(type_section())
                            .color(good()),
                    ),
            )
            .child(
                el().key("ai-run-again")
                    .role("button")
                    .label("Run again")
                    .cursor(Cursor::Pointer)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .h(36.0)
                    .px(14.0)
                    .center()
                    .radius(RADIUS_PILL)
                    .fill(subtle_fill())
                    .border(1.0, float_line())
                    .hover(hover_fill())
                    .press(pressed_fill())
                    .motion(SPRING_QUICK)
                    .on_press(|s: &mut AppState| s.run_auto_tune_pipeline())
                    .child(text("Run again").font(type_control()).color(FG)),
            )
    } else {
        let lbl = if s.pc.auto_tune_running {
            "Optimizing PC"
        } else {
            "Run Auto Tune"
        };
        primary_button("ai-run-auto-tune", lbl, Some(icon_zap(14.0, PRIMARY_FG)))
            .h(36.0)
            .px(18.0)
            .on_press(|s: &mut AppState| s.run_auto_tune_pipeline())
    };

    let header = row()
        .key("exo-ai-head")
        .w_full()
        .min_h(36.0)
        .justify_between()
        .center_y()
        .child(
            row()
                .gap(12.0)
                .center_y()
                .child(text("Exo AI").font(type_page()).color(FG))
                .child(
                    row()
                        .key("exo-ai-live-pill")
                        .h(26.0)
                        .px(11.0)
                        .gap(7.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(surface_card())
                        .border(1.0, float_line())
                        .child(el().size(6.0, 6.0).radius(3.0).fill(good()))
                        .child(
                            text(live_text)
                                .font(TextStyle::new(scaled(12.0), 600.0))
                                .color(FG),
                        ),
                ),
        )
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    row()
                        .key("pc-tab-storage")
                        .role("button")
                        .label("Storage")
                        .cursor(Cursor::Pointer)
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .h(36.0)
                        .pl(12.0)
                        .pr(14.0)
                        .gap(7.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(subtle_fill())
                        .border(1.0, float_line())
                        .hover(hover_fill())
                        .press(pressed_fill())
                        .motion(SPRING_QUICK)
                        .on_press(|s: &mut AppState| {
                            s.pc.exo_ai_storage_open = true;
                            s.pc.part_menu = None;
                            s.scan_storage_space();
                        })
                        .child(icon_hard_drive(14.0, storage_fg))
                        .child(
                            text(storage_label)
                                .font(TextStyle::new(scaled(12.0), 600.0))
                                .color(storage_fg),
                        ),
                )
                .child(header_action),
        );

    let stages = if s.pc.auto_tune_stages.is_empty() {
        compute_initial_auto_tune_stages(&s.pc, &s.settings)
    } else {
        s.pc.auto_tune_stages.clone()
    };

    let playbook_badge: Option<El> = if s.pc.auto_tune_running {
        Some(
            text("Optimizing…")
                .key("exo-ai-auto-tune-status")
                .font(TextStyle::new(scaled(12.0), 500.0))
                .color(FG),
        )
    } else if is_complete {
        Some(
            row()
                .gap(6.0)
                .center_y()
                .child(icon_check(13.0, good()))
                .child(
                    text("Auto Tune complete")
                        .key("exo-ai-auto-tune-status")
                        .font(TextStyle::new(scaled(12.0), 500.0))
                        .color(good()),
                ),
        )
    } else if let Some(n) = pending
        && n > 0
    {
        Some(
            text(format!("{n} ready"))
                .key("exo-ai-auto-tune-status")
                .font(TextStyle::new(scaled(12.0), 500.0))
                .color(warn()),
        )
    } else {
        None
    };

    let mut auto_tune_card = col()
        .key("exo-ai-auto-tune-card")
        .w_full()
        .pt(14.0)
        .px(20.0)
        .pb(6.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(exo_kit::surface_elevated())
        .border_gradient(1.0, exo_kit::glass_rim_top(), exo_kit::glass_rim_bottom())
        .child(
            row()
                .w_full()
                .min_h(30.0)
                .pb(8.0)
                .justify_between()
                .center_y()
                .child(
                    text("Auto Tune")
                        .key("exo-ai-auto-tune-title")
                        .font(type_title())
                        .color(FG),
                )
                .children(playbook_badge),
        )
        .child(el().w_full().h(1.0).fill(float_line()));

    let mut stages_col = col().w_full();
    for (row_idx, pair) in stages.chunks(2).enumerate() {
        if row_idx > 0 {
            stages_col = stages_col.child(el().w_full().h(1.0).fill(float_line()));
        }
        let mut pair_row = row().w_full().gap(16.0).items_start();
        for stage in pair {
            let eff_status = if is_complete && stage.status == AutoTuneStageStatus::Idle {
                AutoTuneStageStatus::Done
            } else {
                stage.status
            };
            let marker = match eff_status {
                AutoTuneStageStatus::Done => el()
                    .size(16.0, 16.0)
                    .mt(2.0)
                    .center()
                    .child(icon_check(14.0, good())),
                AutoTuneStageStatus::Warn => el()
                    .size(16.0, 16.0)
                    .mt(2.0)
                    .center()
                    .child(el().size(8.0, 8.0).radius(4.0).fill(warn())),
                AutoTuneStageStatus::Active => el()
                    .size(16.0, 16.0)
                    .mt(2.0)
                    .center()
                    .child(el().size(8.0, 8.0).radius(4.0).fill(FG)),
                AutoTuneStageStatus::Idle => el()
                    .size(16.0, 16.0)
                    .mt(2.0)
                    .center()
                    .child(el().size(6.0, 6.0).radius(3.0).fill(fg_faint())),
            };
            pair_row = pair_row.child(
                row()
                    .key(format!("auto-tune-stage-{}", stage.id.key()))
                    .grow(1.0)
                    .min_w(0.0)
                    .py(8.0)
                    .gap(9.0)
                    .items_start()
                    .child(marker)
                    .child(
                        col()
                            .gap(2.0)
                            .min_w(0.0)
                            .grow(1.0)
                            .child(text(stage.label).font(type_section()).color(FG))
                            .child(
                                text(stage.detail.clone())
                                    .font(type_small())
                                    .color(if eff_status == AutoTuneStageStatus::Warn {
                                        warn()
                                    } else {
                                        fg_muted()
                                    })
                                    .wrap(),
                            ),
                    ),
            );
        }
        if pair.len() == 1 {
            pair_row = pair_row.child(el().grow(1.0).min_w(0.0));
        }
        stages_col = stages_col.child(pair_row);
    }

    auto_tune_card = auto_tune_card.child(stages_col);

    if let Some(ref tune) = s.pc.auto_tune
        && !tune.parts.is_empty()
    {
        let mut hw_wrap = col()
            .key("exo-ai-tune-hw-wrap")
            .w_full()
            .pt(4.0)
            .child(el().w_full().h(1.0).fill(float_line()));
        for (idx, part) in tune.parts.iter().enumerate() {
            if idx > 0 {
                hw_wrap = hw_wrap.child(el().w_full().h(1.0).fill(float_line()));
            }
            let name = if part.id == "ryzen" {
                cpu_name.to_string()
            } else {
                gpu_name.to_string()
            };
            hw_wrap = hw_wrap.child(auto_tune_hardware_row(
                part,
                name,
                s.pc.tune_errors.get(&part.id),
                s.pc.armed_confirm.as_deref(),
            ));
        }
        auto_tune_card = auto_tune_card.child(hw_wrap);
    }

    let spec_cell = |icon: El, label: String, note: Option<(String, bool)>| {
        col()
            .grow(1.0)
            .min_w(0.0)
            .child(el().w_full().h(1.0).fill(float_line()))
            .child(
                row()
                    .w_full()
                    .pt(12.0)
                    .px(8.0)
                    .pb(12.0)
                    .gap(10.0)
                    .items_start()
                    .child(el().size(16.0, 16.0).center().child(icon))
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .gap(2.0)
                            .child(text(label).font(type_control()).color(FG).wrap_anywhere())
                            .children(note.map(|(n, is_warn)| {
                                text(n)
                                    .font(type_small())
                                    .color(if is_warn { warn() } else { fg_muted() })
                                    .wrap()
                            })),
                    ),
            )
    };

    let spec_action_cell = |key: &'static str,
                            aria: &'static str,
                            icon: El,
                            label: String,
                            note: Option<(String, bool)>,
                            menu: PcPartMenu| {
        let active = s.pc.part_menu == Some(menu);
        col()
            .key(key)
            .role("button")
            .label(format!("{aria} {label}"))
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .radius(RADIUS_SMALL)
            .grow(1.0)
            .min_w(0.0)
            .on_press(move |s: &mut AppState| {
                let opening = s.pc.part_menu != Some(menu);
                s.pc.part_menu = if opening { Some(menu) } else { None };
                s.pc.armed_confirm = None;
                if opening && menu == PcPartMenu::Board {
                    s.load_system_qos();
                    s.load_system_overlays();
                } else if opening && menu == PcPartMenu::Graphics {
                    s.load_gpu_manual_tuning();
                } else if opening && menu == PcPartMenu::Display {
                    s.refresh_hdr_state();
                }
            })
            .child(el().w_full().h(1.0).fill(float_line()))
            .child(
                row()
                    .w_full()
                    .pt(12.0)
                    .px(8.0)
                    .pb(12.0)
                    .gap(8.0)
                    .items_start()
                    .child(el().size(16.0, 16.0).center().child(icon))
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .gap(2.0)
                            .child(text(label).font(type_control()).color(FG).wrap_anywhere())
                            .children(note.map(|(n, is_warn)| {
                                text(n)
                                    .font(type_small())
                                    .color(if is_warn { warn() } else { fg_muted() })
                                    .wrap()
                            })),
                    )
                    .child(el().size(14.0, 16.0).center().child(icon_chevron_right(
                        13.0,
                        if active { FG } else { fg_faint() },
                    ))),
            )
    };

    let drive_used = drive_pct.clamp(0.0, 1.0);
    let drive_cell = col()
        .grow(1.0)
        .min_w(0.0)
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(
            row()
                .w_full()
                .pt(12.0)
                .px(8.0)
                .pb(12.0)
                .gap(10.0)
                .items_start()
                .child(
                    el().size(16.0, 16.0)
                        .center()
                        .child(icon_hard_drive(16.0, fg_muted())),
                )
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(3.0)
                        .child(text(drive_title).font(type_control()).color(FG).truncate())
                        .child(
                            text(drive_sub)
                                .font(type_small())
                                .color(fg_muted())
                                .truncate(),
                        )
                        .child(
                            el().w_full().pt(4.0).pb(2.0).child(
                                row()
                                    .w_full()
                                    .h(3.0)
                                    .radius(2.0)
                                    .fill(track_fill())
                                    .child(el().grow(drive_used).h_full().radius(2.0).fill(FG))
                                    .child(el().grow(1.0 - drive_used).h_full()),
                            ),
                        )
                        .children(drive_note.map(|(n, is_bad)| {
                            text(n)
                                .font(type_small())
                                .color(if is_bad { bad() } else { warn() })
                                .wrap()
                        })),
                ),
        );

    let health_detail = |key: &str| -> Option<String> {
        s.pc.health
            .as_ref()
            .and_then(|h| h.items.iter().find(|i| i.key == key))
            .map(|i| i.detail.clone())
            .filter(|d| !d.is_empty())
    };
    let cpu_note = health_detail("cpumicrocode")
        .map(|d| (d, true))
        .or_else(|| health_detail("ryzenbranch").map(|d| (d, true)));
    let ram_note = health_detail("ramchannel")
        .or_else(|| health_detail("ram"))
        .map(|d| (d, true));

    let mut hw_card = col()
        .key("exo-ai-hw-card")
        .w_full()
        .pt(16.0)
        .px(24.0)
        .pb(10.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(exo_kit::surface_elevated())
        .border_gradient(1.0, exo_kit::glass_rim_top(), exo_kit::glass_rim_bottom())
        .child(
            text(pc_name.to_string())
                .key("exo-ai-hw-title")
                .font(type_title())
                .color(FG),
        )
        .child(
            row()
                .w_full()
                .mt(10.0)
                .gap(8.0)
                .child(spec_cell(
                    icon_cpu(16.0, fg_muted()),
                    cpu_name.to_string(),
                    cpu_note,
                ))
                .child(spec_action_cell(
                    "pc-cell-graphics",
                    "Graphics",
                    icon_monitor(16.0, fg_muted()),
                    gpu_name.to_string(),
                    gpu_sub_note,
                    PcPartMenu::Graphics,
                ))
                .child(spec_cell(
                    icon_hard_drive(16.0, fg_muted()),
                    ram_label,
                    ram_note,
                ))
                .child(spec_action_cell(
                    "pc-cell-display",
                    "Display",
                    icon_monitor(16.0, fg_muted()),
                    display_label.to_string(),
                    cable_warn.map(|w| (w.to_string(), true)),
                    PcPartMenu::Display,
                )),
        )
        .child(
            row()
                .w_full()
                .gap(8.0)
                .items_start()
                .child(spec_action_cell(
                    "pc-cell-board",
                    "Motherboard",
                    icon_cpu(16.0, fg_muted()),
                    board_label.to_string(),
                    board_warn.map(|w| (w, true)),
                    PcPartMenu::Board,
                ))
                .child(spec_cell(
                    icon_app_window(16.0, fg_muted()),
                    net_label,
                    netlink_warn.map(|w| (w.to_string(), true)),
                ))
                .child(spec_cell(
                    icon_app_window(16.0, fg_muted()),
                    win_label,
                    None,
                ))
                .child(drive_cell),
        );

    let ov_health = overview_health_items(s.pc.health.as_ref());
    if !ov_health.is_empty() {
        let mut h_col = col()
            .key("exo-ai-health-rows")
            .w_full()
            .mt(8.0)
            .pt(4.0)
            .child(el().w_full().h(1.0).fill(float_line()));
        for item in ov_health {
            let ctrl = match item.key.as_str() {
                "gpupref" => {
                    if item.applied && item.can_undo {
                        menu_btn("ov-hp-gpupref-undo".into(), "Undo", false, false, false)
                            .on_press(|s: &mut AppState| s.run_health_action("gpupref-undo"))
                    } else if !item.applied {
                        menu_btn(
                            "ov-hp-gpupref".into(),
                            "Use the fast card",
                            true,
                            false,
                            false,
                        )
                        .on_press(|s: &mut AppState| s.run_health_action("gpupref"))
                    } else {
                        row()
                    }
                }
                "cpumicrocode" => {
                    if let Some(url) = item.url.clone() {
                        menu_btn(
                            "ov-hp-cpumicrocode".into(),
                            "BIOS page",
                            false,
                            false,
                            false,
                        )
                        .on_press(move |s: &mut AppState| s.open_external_url(&url))
                    } else {
                        row()
                    }
                }
                "ryzenbranch" => menu_btn(
                    "ov-hp-winupdate".into(),
                    "Windows Update",
                    false,
                    false,
                    false,
                )
                .on_press(|s: &mut AppState| s.run_health_action("winupdate")),
                "gpusched" => menu_btn(
                    "ov-hp-gpusched".into(),
                    "Open Settings",
                    false,
                    false,
                    false,
                )
                .on_press(|s: &mut AppState| s.run_health_action("gpusched")),
                _ => row(),
            };
            h_col = h_col.child(
                row()
                    .w_full()
                    .py(8.0)
                    .gap(12.0)
                    .justify_between()
                    .center_y()
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .gap(2.0)
                            .child(text(item.label.clone()).font(type_control()).color(FG))
                            .children((!item.detail.is_empty()).then(|| {
                                text(item.detail.clone())
                                    .font(type_small())
                                    .color(fg_muted())
                                    .wrap()
                            })),
                    )
                    .child(ctrl),
            );
        }
        hw_card = hw_card.child(h_col);
    }

    let updates_card = updates_card_view(s);

    let left_col = col()
        .key("exo-ai-left-col")
        .grow(1.12)
        .min_w(0.0)
        .gap(10.0)
        .child(auto_tune_card)
        .child(hw_card);

    let shaders_working = s.pc.shaders_queue_running
        || s.pc.shaders_queue_current_id.is_some()
        || s.pc.shader_snap_games.iter().any(|g| g.compiling);
    let ready_ids: Vec<String> =
        s.pc.shader_snap_games
            .iter()
            .filter(|g| g.in_add_all_ready && !g.queued && !g.compiling)
            .map(|g| g.id.clone())
            .collect();

    let shaders_absent = s.pc.shaders.as_ref().is_some_and(|sh| !sh.present);
    let (shader_list, base_shader_summary) = if shaders_absent {
        (Vec::new(), String::new())
    } else if !s.pc.shader_snap_games.is_empty() {
        let current = s.pc.shader_snap_games.iter().find(|g| g.compiling);
        let current_id = current.map(|g| g.id.as_str());
        let waiting: Vec<&exo_rpc::ShaderSnapGame> =
            s.pc.shader_snap_games
                .iter()
                .filter(|g| g.queued && Some(g.id.as_str()) != current_id)
                .collect();
        let mut ready: Vec<&exo_rpc::ShaderSnapGame> =
            s.pc.shader_snap_games
                .iter()
                .filter(|g| g.in_add_all_ready && !g.queued && Some(g.id.as_str()) != current_id)
                .collect();
        ready.sort_by(|a, b| {
            let ta = resolve_shader_estimate(a, &s.pc.shader_estimates)
                .map(|e| e.seconds)
                .unwrap_or(f64::INFINITY);
            let tb = resolve_shader_estimate(b, &s.pc.shader_estimates)
                .map(|e| e.seconds)
                .unwrap_or(f64::INFINITY);
            ta.partial_cmp(&tb).unwrap_or(std::cmp::Ordering::Equal)
        });
        let mut rest: Vec<&exo_rpc::ShaderSnapGame> = s
            .pc
            .shader_snap_games
            .iter()
            .filter(|g| {
                Some(g.id.as_str()) != current_id
                    && !g.queued
                    && !g.in_add_all_ready
                    && (g.error.is_some() || matches!(g.tone.as_deref(), Some("warn" | "primary")))
            })
            .collect();
        rest.sort_by_key(|g| match g.tone.as_deref() {
            Some("warn" | "primary") => 1000_u32,
            Some("good") => 2000,
            _ => 3000,
        });

        let working = current.is_some();
        let mut total = ShaderSpan {
            low: 0.0,
            high: 0.0,
            learned: true,
        };
        let mut unknown = 0usize;
        if let Some(cur) = current {
            if let Some(est) = resolve_shader_estimate(cur, &s.pc.shader_estimates) {
                let share = 1.0 - (cur.percent.unwrap_or(0.0).clamp(0.0, 100.0) / 100.0);
                let span = shader_span(&est, share);
                total.low += span.low;
                total.high += span.high;
                total.learned &= span.learned;
            } else {
                unknown += 1;
            }
        }
        let counted: Vec<&exo_rpc::ShaderSnapGame> = if working {
            waiting.clone()
        } else {
            waiting.iter().chain(ready.iter()).copied().collect()
        };
        for sg in &counted {
            if let Some(est) = resolve_shader_estimate(sg, &s.pc.shader_estimates) {
                let span = shader_span(&est, 1.0);
                total.low += span.low;
                total.high += span.high;
                total.learned &= span.learned;
            } else {
                unknown += 1;
            }
        }
        let left_count = if working {
            waiting.len() + usize::from(current.is_some())
        } else {
            waiting.len() + ready.len()
        };
        let eta = if working || left_count > 0 {
            shader_eta_text(total, unknown > 0, working)
        } else {
            None
        };
        let games_label = (left_count > 0).then(|| {
            format!(
                "{left_count} {}",
                if left_count == 1 { "game" } else { "games" }
            )
        });
        let summary = match (games_label, eta) {
            (Some(g), Some(t)) => format!("{g} · {t}"),
            (Some(g), None) => g,
            (None, Some(t)) => t,
            (None, None) => String::new(),
        };

        let mut ordered: Vec<(&exo_rpc::ShaderSnapGame, Option<usize>)> = Vec::new();
        if let Some(cur) = current {
            ordered.push((cur, None));
        }
        for (i, sg) in waiting.iter().enumerate() {
            ordered.push((sg, Some(i + 1)));
        }
        for sg in &ready {
            ordered.push((sg, None));
        }
        for sg in &rest {
            ordered.push((sg, None));
        }

        let list = ordered
            .into_iter()
            .take(8)
            .map(|(sg, place)| {
                let est = resolve_shader_estimate(sg, &s.pc.shader_estimates);
                let sub = shader_poster_subtitle(sg, place, est.as_ref());
                let g = s
                    .games
                    .iter()
                    .find(|g| g.id == sg.id || g.title.eq_ignore_ascii_case(&sg.name));
                (sg.id.clone(), sg.name.clone(), sub, g, sg.queued, false)
            })
            .collect();
        (list, summary)
    } else if !s.pc.shader_games.is_empty() {
        let list: Vec<_> =
            s.pc.shader_games
                .iter()
                .take(8)
                .map(|sg| {
                    let g = s
                        .games
                        .iter()
                        .find(|g| g.id == sg.id || g.title.eq_ignore_ascii_case(&sg.title));
                    (
                        sg.id.clone(),
                        sg.title.clone(),
                        shader_prepare_subtitle(sg),
                        g,
                        sg.state == "running",
                        true,
                    )
                })
                .collect();
        let n = list.len();
        let summary = format!("{n} {}", if n == 1 { "game" } else { "games" });
        (list, summary)
    } else if s.status == HostStatus::Standalone {
        let list: Vec<_> = s
            .games
            .iter()
            .filter(|g| g.installed)
            .take(8)
            .map(|g| {
                (
                    g.id.clone(),
                    g.title.clone(),
                    "Ready".into(),
                    Some(g),
                    false,
                    true,
                )
            })
            .collect();
        let n = list.len();
        let summary = format!("{n} {}", if n == 1 { "game" } else { "games" });
        (list, summary)
    } else {
        (Vec::new(), String::new())
    };

    let rebuild_summary = s.pc.shaders_rebuild.as_ref().and_then(|rb| {
        (!matches!(rb.state.as_str(), "idle" | "done" | "")).then(|| {
            format!(
                "Getting ready for the new driver · {} of {} games",
                rb.ready, rb.total
            )
        })
    });
    let shader_summary = rebuild_summary.unwrap_or(base_shader_summary);

    let mut shader_rows = col().w_full().pt(12.0).gap(12.0);
    for chunk in shader_list.chunks(4) {
        let mut r = row().w_full().gap(10.0).items_start();
        for (id, title, est, game_opt, is_queued, is_prepare) in chunk {
            let hue = title_hue(title);
            let mut art = el()
                .w_full()
                .h(148.0)
                .radius(10.0)
                .continuous_corners()
                .clip_rounded(10.0);
            if let Some(img) = game_opt.and_then(|g| s.cover_image(g, scale)) {
                art = art.pixels_fit(img.pixels(), SurfaceFit::Fill);
            } else {
                art = art.linear_gradient(
                    0.35,
                    [(0.0, oklch(0.38, 0.14, hue)), (1.0, oklch(0.10, 0.03, hue))],
                );
            }
            let gid = id.clone();
            let queued = *is_queued;
            let prep = *is_prepare;
            r = r.child(
                col()
                    .key(format!("shader-game-{id}"))
                    .role("button")
                    .label(title.clone())
                    .cursor(Cursor::Pointer)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .hover(hover_fill())
                    .press(pressed_fill())
                    .motion(SPRING_QUICK)
                    .px(4.0)
                    .py(4.0)
                    .radius(RADIUS_CARD)
                    .grow(1.0)
                    .min_w(0.0)
                    .gap(6.0)
                    .on_press(move |s: &mut AppState| {
                        if prep {
                            if queued {
                                s.cancel_shader_prepare(&gid);
                            } else {
                                s.compile_shaders_all();
                            }
                        } else if queued {
                            s.run_shaders_steps(
                                &format!("game:{gid}"),
                                vec![("queue.remove", json!({ "id": gid }))],
                            );
                        } else {
                            s.run_shaders_steps(
                                &format!("game:{gid}"),
                                vec![("game.compile", json!({ "id": gid }))],
                            );
                        }
                    })
                    .child(art)
                    .child(
                        col()
                            .w_full()
                            .gap(1.0)
                            .child(
                                text(title.clone())
                                    .font(type_control())
                                    .color(FG)
                                    .truncate(),
                            )
                            .child(
                                text(est.clone())
                                    .font(type_small())
                                    .color(fg_muted())
                                    .truncate(),
                            ),
                    ),
            );
        }
        for _ in chunk.len()..4 {
            r = r.child(el().grow(1.0).min_w(0.0));
        }
        shader_rows = shader_rows.child(r);
    }

    let mut shader_actions = row().gap(8.0).center_y();
    if !shaders_absent {
        shader_actions = shader_actions
            .child(text(shader_summary).font(type_body()).color(fg_muted()))
            .child(
                menu_btn(
                    "shaders-refresh".into(),
                    if s.pc.shaders_scanning || s.pc.shaders_busy.as_deref() == Some("scan") {
                        "Scanning…"
                    } else {
                        "Refresh"
                    },
                    false,
                    false,
                    false,
                )
                .on_press(|s: &mut AppState| {
                    if s.pc.shaders.as_ref().is_some_and(|sh| sh.present) {
                        s.run_shaders_steps("scan", vec![("scan", json!({}))]);
                    } else {
                        s.load_pc_exo_ai();
                    }
                }),
            );

        if shaders_working {
            let paused = s.pc.shaders_queue_paused;
            shader_actions = shader_actions
                .child(
                    menu_btn(
                        "shaders-pause".into(),
                        if paused { "Resume" } else { "Pause" },
                        false,
                        false,
                        false,
                    )
                    .on_press(move |s: &mut AppState| {
                        let m = if paused {
                            "queue.resume"
                        } else {
                            "queue.pause"
                        };
                        s.run_shaders_steps("pause", vec![(m, json!({}))]);
                    }),
                )
                .child(
                    menu_btn("shaders-stop".into(), "Stop", false, false, false).on_press(
                        |s: &mut AppState| {
                            s.run_shaders_steps("stop", vec![("queue.stop", json!({}))]);
                        },
                    ),
                );
        } else {
            let ids_c = ready_ids.clone();
            shader_actions =
                shader_actions.child(primary_button("shaders-compile", "Compile", None).on_press(
                    move |s: &mut AppState| {
                        if !ids_c.is_empty() || s.pc.shaders_queue_can_start {
                            let mut steps = Vec::new();
                            if !ids_c.is_empty() {
                                steps.push(("queue.add", json!({ "ids": ids_c })));
                            }
                            steps.push(("queue.start", json!({})));
                            s.run_shaders_steps("compile", steps);
                        } else {
                            s.compile_shaders_all();
                        }
                    },
                ));
        }
    }

    let mut shaders_card = col()
        .key("exo-ai-shaders-card")
        .w_full()
        .pt(16.0)
        .px(24.0)
        .pb(16.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(exo_kit::surface_elevated())
        .border_gradient(1.0, exo_kit::glass_rim_top(), exo_kit::glass_rim_bottom())
        .child(
            row()
                .w_full()
                .min_h(32.0)
                .pb(10.0)
                .justify_between()
                .center_y()
                .child(
                    text("Shaders")
                        .key("exo-ai-shaders-title")
                        .font(type_title())
                        .color(FG),
                )
                .child(shader_actions),
        )
        .child(el().w_full().h(1.0).fill(float_line()));

    if let Some(ref off_msg) = s.pc.shaders_cache_off {
        shaders_card = shaders_card.child(
            row()
                .w_full()
                .py(8.0)
                .justify_between()
                .center_y()
                .child(
                    text(off_msg.clone())
                        .font(type_small())
                        .color(warn())
                        .wrap(),
                )
                .child(
                    menu_btn(
                        "shaders-cache-unlimited".into(),
                        "Set Unlimited",
                        true,
                        false,
                        false,
                    )
                    .on_press(|s: &mut AppState| {
                        s.run_shaders_steps(
                            "cache-limit",
                            vec![("driver.setCacheLimit", json!({ "index": 12 }))],
                        );
                    }),
                ),
        );
    }
    if let Some(ref old_note) = s.pc.shaders_old_driver_note {
        shaders_card = shaders_card.child(
            row().w_full().py(6.0).justify_end().child(
                menu_btn(
                    "shaders-free-old".into(),
                    format!("Free old cache ({old_note})"),
                    false,
                    false,
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.run_shaders_steps("free-old", vec![("cache.freeOldDriver", json!({}))]);
                }),
            ),
        );
    }
    if let Some(ref err) = s.pc.shaders_error {
        shaders_card = shaders_card.child(
            el().w_full()
                .py(4.0)
                .child(text(err.clone()).font(type_small()).color(bad()).wrap()),
        );
    }
    if s.pc.shaders.as_ref().is_some_and(|sh| !sh.present) && shader_list.is_empty() {
        shaders_card = shaders_card.child(
            el().w_full().py(16.0).child(
                text("Exo Shaders isn't installed")
                    .font(type_body())
                    .color(fg_muted()),
            ),
        );
    } else if shader_list.is_empty() && !s.pc.shaders_scanning {
        shaders_card = shaders_card.child(
            el().w_full().py(16.0).child(
                text(if !s.pc.shader_snap_games.is_empty() {
                    "Every game is compiled"
                } else {
                    "No games found"
                })
                .font(type_body())
                .color(fg_muted()),
            ),
        );
    } else {
        shaders_card = shaders_card.child(el().w_full().pt(12.0).child(shader_rows));
    }

    let mut right_col = col()
        .key("exo-ai-right-col")
        .grow(1.0)
        .min_w(0.0)
        .gap(10.0)
        .child(updates_card);

    if s.pc.gpu_processes.is_some() || s.status != HostStatus::Standalone {
        let gpu_data = s.pc.gpu_processes.as_ref();
        let summary = gpu_data
            .map(|d| d.answer.summary.as_str())
            .filter(|t| !t.is_empty())
            .unwrap_or("Reading GPU activity…");
        let multi_adapter = gpu_data.is_some_and(|d| d.adapters.len() > 1);
        let mut gpu_card = col()
            .key("exo-ai-gpu-usage-card")
            .w_full()
            .pt(14.0)
            .px(20.0)
            .pb(12.0)
            .gap(6.0)
            .radius(RADIUS_PANEL)
            .continuous_corners()
            .fill(exo_kit::surface_elevated())
            .border_gradient(1.0, exo_kit::glass_rim_top(), exo_kit::glass_rim_bottom())
            .child(
                row()
                    .w_full()
                    .min_h(32.0)
                    .justify_between()
                    .center_y()
                    .child(text("GPU activity").font(type_title()).color(FG))
                    .child(
                        menu_btn(
                            "gpu-proc-refresh".into(),
                            "Refresh",
                            false,
                            false,
                            s.pc.gpu_processes_busy,
                        )
                        .on_press(|s: &mut AppState| s.load_gpu_processes()),
                    ),
            )
            .child(
                text(summary.to_string())
                    .font(type_body())
                    .color(fg_muted())
                    .wrap(),
            );
        if let Some(d) = gpu_data
            && !d.processes.is_empty()
        {
            let mut list = col().w_full();
            for proc in d.processes.iter().take(5) {
                let sub = format_gpu_process_subtitle(proc, multi_adapter);
                let metrics = format_gpu_process_metrics(proc);
                list = list.child(el().w_full().h(1.0).fill(float_line())).child(
                    row()
                        .key(format!("gpu-proc-{}-{}", proc.pid, proc.adapter_luid))
                        .w_full()
                        .min_h(38.0)
                        .py(6.0)
                        .gap(12.0)
                        .justify_between()
                        .center_y()
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(
                                    text(proc.name.clone())
                                        .font(type_control())
                                        .color(FG)
                                        .truncate(),
                                )
                                .children(sub.map(|st| {
                                    text(st).font(type_small()).color(fg_muted()).truncate()
                                })),
                        )
                        .child(text(metrics).font(type_small()).color(FG)),
                );
            }
            gpu_card = gpu_card.child(list);
        }
        right_col = right_col.child(gpu_card);
    }

    right_col = right_col.child(shaders_card);

    col()
        .key("room-exo-ai")
        .w_full()
        .h_full()
        .pt(TITLEBAR_HEIGHT)
        .fill(BG)
        .child(
            col()
                .key("exo-ai-scroll")
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .pt(12.0)
                .px(GAP)
                .pb(20.0)
                .gap(12.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(header)
                .child(
                    row()
                        .key("exo-ai-grid")
                        .w_full()
                        .items_start()
                        .gap(GAP)
                        .child(left_col)
                        .child(right_col),
                )
                .child(
                    row()
                        .key("exo-ai-telemetry-row")
                        .w_full()
                        .items_stretch()
                        .gap(GAP)
                        .child(
                            el().grow(1.12)
                                .min_w(0.0)
                                .child(crate::power_guard::card_view(s)),
                        )
                        .child(
                            el().grow(1.0)
                                .min_w(0.0)
                                .child(crate::hw_sensors::card_view(s)),
                        ),
                ),
        )
}

pub(crate) fn part_menu_layers(s: &AppState, blur: bool) -> Vec<El> {
    crate::pc_menus::part_menu_layers(s, blur)
}

#[cfg(test)]
mod tests {
    use crate::shell;
    use crate::state::{AppState, Room};
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Runtime};

    #[test]
    fn exo_ai_bento_balance_header_alignment_and_blur_cap() {
        let mut state = AppState::default();
        let extra: Vec<_> = state
            .pc
            .shader_games
            .iter()
            .enumerate()
            .map(|(i, g)| exo_rpc::ShaderPrepareGame {
                id: format!("{}-row2-{i}", g.id),
                title: g.title.clone(),
                state: g.state.clone(),
                message: g.message.clone(),
            })
            .collect();
        state.pc.shader_games.extend(extra);
        state.switch_room(Room::ExoAi);
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert_eq!(blurs, 0);
        let glass_shapes = rt.liquid_glass_frame().shapes.len();
        assert!(
            glass_shapes <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "Exo AI exceeded EXO_MAX_BACKDROP_BLURS: {glass_shapes}"
        );
        assert!(
            glass_shapes >= 3,
            "Exo AI should render Liquid Glass, got {glass_shapes}"
        );

        let (hw_rect, _) = rt.presented("exo-ai-hw-card").expect("exo-ai-hw-card");
        let (upd_rect, _) = rt
            .presented("exo-ai-updates-card")
            .expect("exo-ai-updates-card");
        let (auto_card, _) = rt
            .presented("exo-ai-auto-tune-title")
            .expect("exo-ai-auto-tune-title");
        assert!(
            upd_rect[0] > hw_rect[0],
            "Updates card should sit in the right column beside Auto Tune"
        );
        assert!(
            hw_rect[1] > auto_card[1],
            "Hardware card should sit below Auto Tune in the left column"
        );
        assert!(
            hw_rect[3] >= 120.0 && hw_rect[3] <= 320.0,
            "Hardware card should stay compact without dead space, got h={}",
            hw_rect[3]
        );
        assert!(
            rt.presented("exo-ai-telemetry-row").is_some(),
            "Power guard and hardware sensors should render in side-by-side telemetry row"
        );
    }

    #[test]
    fn exo_ai_spec_formatting_matches_react() {
        let about = exo_rpc::SystemAbout {
            windows: Some("Windows 11 Pro".into()),
            windows_version: Some("26H2".into()),
            ..Default::default()
        };
        assert_eq!(super::format_windows_label(Some(&about)), "Windows 11 26H2");

        let insider = exo_rpc::SystemAbout {
            windows: Some("Windows 11 Pro Insider Preview".into()),
            windows_version: Some("26300".into()),
            ..Default::default()
        };
        assert_eq!(
            super::format_windows_label(Some(&insider)),
            "Windows 11 Insider 26300"
        );

        assert_eq!(
            super::short_drive_model("Samsung SSD 990 PRO with Heatsink 2TB"),
            "Samsung 990 PRO"
        );
        assert_eq!(
            super::short_drive_model("WD_BLACK SN850X 2000GB"),
            "WD_BLACK SN850X"
        );
        assert_eq!(
            super::short_drive_model("Crucial® P5 Plus (TM) Solid State Drive 2 TB"),
            "Crucial P5 Plus"
        );
    }

    #[test]
    fn exo_ai_shader_subtitle_and_estimate_parity() {
        let est = |seconds: f64, low_seconds: Option<f64>, learned: bool| exo_rpc::ShaderEstimate {
            seconds,
            low_seconds,
            learned,
        };

        assert_eq!(
            super::format_shader_estimate(&est(30.0, None, false)),
            "under a minute"
        );
        assert_eq!(
            super::format_shader_estimate(&est(120.0, None, false)),
            "1–3 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(200.0, None, false)),
            "2–5 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(600.0, None, false)),
            "5–15 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(900.0, Some(10.0), true)),
            "up to 15 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(3600.0, Some(10.0), false)),
            "up to 1 h"
        );
        assert_eq!(
            super::format_shader_estimate(&est(12600.0, Some(10.0), false)),
            "up to 3 h 30 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(6000.0, None, true)),
            "about 1 h 40 min"
        );
        assert_eq!(
            super::format_shader_estimate(&est(45.0, None, true)),
            "under a minute"
        );
        assert_eq!(super::shader_range_text(7200.0, 25200.0), "2–7 h");
        assert_eq!(super::shader_range_text(5400.0, 10800.0), "1 h 30 min–3 h");
        assert_eq!(super::shader_range_text(850.0, 900.0), "about 15 min");

        let needs_rebuild = exo_rpc::ShaderSnapGame {
            id: "g1".into(),
            name: "CONTROL Resonant".into(),
            status_text: Some("Needs rebuilding".into()),
            tone: Some("warn".into()),
            in_add_all_ready: true,
            estimate: Some(est(120.0, None, false)),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(&needs_rebuild, None, needs_rebuild.estimate.as_ref()),
            "1–3 min"
        );

        let needs_rebuild_no_est = exo_rpc::ShaderSnapGame {
            estimate: None,
            ..needs_rebuild.clone()
        };
        assert_eq!(
            super::shader_poster_subtitle(&needs_rebuild_no_est, None, None),
            "Needs rebuilding"
        );

        let mut est_map = std::collections::HashMap::new();
        est_map.insert("roblox".to_string(), 600.0);
        let ready_from_map = exo_rpc::ShaderSnapGame {
            id: "roblox".into(),
            name: "Roblox".into(),
            status_text: Some("Ready to compile".into()),
            in_add_all_ready: true,
            ..Default::default()
        };
        let resolved = super::resolve_shader_estimate(&ready_from_map, &est_map);
        assert_eq!(
            super::shader_poster_subtitle(&ready_from_map, None, resolved.as_ref()),
            "5–15 min"
        );

        let warmed = exo_rpc::ShaderSnapGame {
            id: "warmed".into(),
            name: "Hades".into(),
            status_text: Some("Warmed".into()),
            tone: Some("good".into()),
            in_add_all_ready: false,
            estimate: Some(est(600.0, None, false)),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(&warmed, None, warmed.estimate.as_ref()),
            "Warmed"
        );

        let needs_recording = exo_rpc::ShaderSnapGame {
            id: "rec".into(),
            name: "New Game".into(),
            status_text: Some("Needs a 5-min recording".into()),
            tone: Some("warn".into()),
            in_add_all_ready: false,
            estimate: Some(est(600.0, None, false)),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(
                &needs_recording,
                None,
                needs_recording.estimate.as_ref()
            ),
            "Needs a 5-min recording"
        );

        let queued = exo_rpc::ShaderSnapGame {
            id: "q1".into(),
            name: "Thymesia".into(),
            status_text: Some("Needs rebuilding".into()),
            queued: true,
            in_add_all_ready: true,
            estimate: Some(est(200.0, None, false)),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(&queued, Some(1), queued.estimate.as_ref()),
            "Next · 2–5 min"
        );
        assert_eq!(
            super::shader_poster_subtitle(&queued, Some(2), queued.estimate.as_ref()),
            "#2 · 2–5 min"
        );
        assert_eq!(
            super::shader_poster_subtitle(&queued, Some(1), None),
            "Next"
        );

        let compiling_pct = exo_rpc::ShaderSnapGame {
            id: "c1".into(),
            name: "Wuthering Waves".into(),
            compiling: true,
            percent: Some(42.9),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(&compiling_pct, None, None),
            "Compiling · 42%"
        );

        let compiling_stage = exo_rpc::ShaderSnapGame {
            id: "c2".into(),
            name: "Wuthering Waves".into(),
            compiling: true,
            status_text: Some("Linking pipelines".into()),
            ..Default::default()
        };
        assert_eq!(
            super::shader_poster_subtitle(&compiling_stage, None, None),
            "Linking pipelines"
        );

        let prep = |state: &str, message: Option<&str>| exo_rpc::ShaderPrepareGame {
            id: "p".into(),
            title: "Game".into(),
            state: state.into(),
            message: message.map(str::to_string),
        };
        assert_eq!(
            super::shader_prepare_subtitle(&prep("running", None)),
            "Compiling its first launch"
        );
        assert_eq!(
            super::shader_prepare_subtitle(&prep("stale", None)),
            "Driver changed"
        );
        assert_eq!(
            super::shader_prepare_subtitle(&prep("ready", Some("1–3 min"))),
            "1–3 min"
        );
        assert_eq!(
            super::shader_prepare_subtitle(&prep("ready", None)),
            "First launch"
        );
    }
}
