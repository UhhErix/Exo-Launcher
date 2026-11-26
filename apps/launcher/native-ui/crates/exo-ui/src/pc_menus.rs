use crate::pc_state::{
    AutoTuneStageStatus, PcPartMenu, any_restart_required, autopilot_active_summary,
    best_steps_list, bios_vendor_hints, trim_stop,
};
use crate::state::{AppState, Room};
use exo_kit::{
    BG, BadgeTone, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    SPRING_MOVE, SPRING_QUICK, bad, fg_muted, float_line, glass_panel_opt, glass_rim_bottom,
    glass_rim_top, good, hover_fill, icon_check, icon_chevron_down, icon_close, icon_cpu,
    icon_hard_drive, icon_monitor, modal_card_transition, modal_scrim_transition,
    popover_transition_from, pressed_fill, progress_bar, scaled, slider_track, status_badge,
    subtle_fill, track_fill, type_body, type_control, type_page, type_section, type_small,
    type_title, warn,
};
use exo_rpc::{AutopilotLive, AutopilotReport, DriverProgress, DriverState, OutputColor};
use rune::{
    Cursor, El, Key, Phase, ScrollSpec, TextStyle, col, el, row, text,
};
use serde_json::json;

const NVIDIA_LICENSE: &str = "https://www.nvidia.com/en-us/drivers/nvidia-license/";
const AMD_LICENSE: &str = "https://www.amd.com/en/legal/eula.html";

pub(crate) fn menu_btn(
    key: String,
    label: impl Into<String>,
    primary: bool,
    danger: bool,
    disabled: bool,
) -> El {
    let lbl = label.into();
    let (fill, hover_bg, press_bg, fg, border_col) = if danger {
        (
            bad(),
            bad().alpha(0.90),
            bad().alpha(0.80),
            PRIMARY_FG,
            bad(),
        )
    } else if primary {
        (FG, FG.alpha(0.90), FG.alpha(0.82), PRIMARY_FG, FG)
    } else {
        (
            subtle_fill(),
            hover_fill(),
            pressed_fill(),
            FG,
            float_line(),
        )
    };
    el().key(key)
        .role("button")
        .label(lbl.clone())
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .focusable(!disabled)
        .disabled(disabled)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(28.0)
        .px(12.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(fill)
        .hover(hover_bg)
        .press(press_bg)
        .border(1.0, border_col)
        .motion(SPRING_QUICK)
        .child(text(lbl).font(type_small()).color(fg))
}

pub(crate) fn menu_switch(key: String, label: impl Into<String>, on: bool, disabled: bool) -> El {
    let knob = el()
        .size(18.0, 18.0)
        .radius(9.0)
        .fill(FG)
        .absolute(if on { 18.0 } else { 2.0 }, 2.0)
        .motion(SPRING_MOVE);
    el().key(key)
        .role("switch")
        .label(label)
        .checked(on)
        .focusable(!disabled)
        .disabled(disabled)
        .hit()
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .press_on_down()
        .size(38.0, 22.0)
        .radius(RADIUS_PILL)
        .fill(if on { good() } else { track_fill() })
        .motion(SPRING_QUICK)
        .child(knob)
}

pub(crate) fn menu_row(
    label: impl Into<String>,
    detail: Option<String>,
    error: Option<String>,
    right: El,
) -> El {
    col()
        .w_full()
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(
            row()
                .w_full()
                .min_h(56.0)
                .py(10.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(2.0)
                        .child(text(label.into()).font(type_control()).color(FG))
                        .children(
                            detail.map(|d| text(d).font(type_small()).color(fg_muted()).wrap()),
                        )
                        .children(error.map(|e| text(e).font(type_small()).color(bad()).wrap())),
                )
                .child(right),
        )
}

pub(crate) fn group_head(title: &'static str) -> El {
    el().w_full().pt(14.0).pb(6.0).child(
        text(title)
            .font(TextStyle::new(scaled(12.0), 600.0))
            .color(fg_muted()),
    )
}

fn format_driver_size(bytes: u64) -> String {
    if bytes >= 1_000_000_000 {
        format!("{:.1} GB", bytes as f64 / 1e9)
    } else {
        format!("{} MB", (bytes as f64 / 1e6).round() as u64)
    }
}

fn driver_vendor_name(vendor: &str) -> &'static str {
    match vendor {
        "nvidia" => "NVIDIA",
        "amd" => "AMD",
        "intel" => "Intel",
        _ => "GPU",
    }
}

fn is_driver_working(p: Option<&DriverProgress>) -> bool {
    p.is_some_and(|x| {
        matches!(
            x.stage.as_str(),
            "downloading" | "unpacking" | "waiting" | "installing"
        )
    })
}

fn driver_detail_text(
    state: &DriverState,
    vendor: &str,
    prog: Option<&DriverProgress>,
    starting: bool,
) -> String {
    if starting {
        return "Starting the update".into();
    }
    if let Some(p) = prog {
        if is_driver_working(Some(p)) {
            let stage_word = match p.stage.as_str() {
                "downloading" => "Downloading",
                "unpacking" => "Unpacking",
                "waiting" => "Waiting",
                "installing" => "Installing",
                _ => "",
            };
            let base = p.message.as_deref().unwrap_or(stage_word);
            if p.stage == "downloading"
                && let Some(ref latest) = state.latest
                && latest.size > 0
            {
                let got = (latest.size as f64 * p.percent / 100.0) as u64;
                return format!(
                    "{base} · {} of {} · {}%",
                    format_driver_size(got),
                    format_driver_size(latest.size),
                    p.percent.round() as i32
                );
            }
            return base.to_string();
        }
        if p.stage == "done" || p.stage == "failed" {
            let fallback = if p.stage == "done" {
                "Installed"
            } else {
                "Stopped"
            };
            let msg = p
                .message
                .as_deref()
                .map(trim_stop)
                .filter(|m| !m.is_empty())
                .unwrap_or_else(|| fallback.into());
            if let Some(ref inst) = state.installed
                && !msg.contains(inst.as_str())
            {
                return format!("{inst} · {msg}");
            }
            return msg;
        }
    }
    let inst = state.installed.as_deref();
    if let Some(ref adv) = state.advised
        && !adv.why.is_empty()
    {
        return match inst {
            Some(i) => format!("{i} · {}", adv.why),
            None => adv.why.clone(),
        };
    }
    let Some(installed) = inst else {
        return "No driver".into();
    };
    if let Some(ref back) = state.advised.as_ref().and_then(|a| a.back.as_ref()) {
        return format!("{installed} → {back}");
    }
    if state.latest.is_none() && state.skipped.is_none() {
        let who = if vendor == "nvidia" {
            "NVIDIA's driver list couldn't be reached".to_string()
        } else {
            format!(
                "{}'s newest driver for this card couldn't be found",
                driver_vendor_name(vendor)
            )
        };
        return format!("{installed} · {who}");
    }
    if let Some(ref latest) = state.latest {
        let tag = match latest.kind.as_str() {
            "hotfix" => " (hotfix)",
            "optional" => " (optional)",
            _ => "",
        };
        return if state.update {
            format!("{installed} → {}{tag}", latest.version)
        } else {
            format!("{installed} · Up to date")
        };
    }
    installed.to_string()
}

pub(crate) fn driver_row_view(s: &AppState) -> Option<El> {
    let state = s.pc.gpu_driver.as_ref()?;
    let vendor = state.vendor.as_deref()?;
    let prog =
        s.pc.gpu_driver_progress
            .as_ref()
            .or(state.progress.as_ref());
    let working = s.pc.gpu_driver_starting || is_driver_working(prog);
    let detail = driver_detail_text(state, vendor, prog, s.pc.gpu_driver_starting);
    let err =
        s.pc.gpu_driver_error
            .clone()
            .or_else(|| (!working).then(|| state.blocked.clone()).flatten());

    let goal_word: Option<(String, bool)> =
        if let Some(ref back) = state.advised.as_ref().and_then(|a| a.back.as_ref()) {
            Some((format!("Go back to {back}"), true))
        } else if state.latest.is_some() {
            if state.update {
                Some(("Update".into(), true))
            } else {
                Some(("Reinstall".into(), false))
            }
        } else {
            None
        };

    let mut right = row().gap(6.0).center_y();
    if vendor == "nvidia" && !state.workstation {
        let is_studio = s.pc.gpu_driver_branch == "studio";
        let branch_lbl = if is_studio { "Studio" } else { "Game Ready" };
        right = right.child(
            row()
                .key("gpu-driver-branch")
                .role("button")
                .label(branch_lbl)
                .cursor(if working {
                    Cursor::NotAllowed
                } else {
                    Cursor::Pointer
                })
                .focusable(!working)
                .disabled(working)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .h(28.0)
                .px(12.0)
                .gap(5.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(subtle_fill())
                .hover(hover_fill())
                .press(pressed_fill())
                .border(1.0, float_line())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    s.set_gpu_driver_branch(if is_studio { "game" } else { "studio" });
                })
                .child(text(branch_lbl).font(type_small()).color(FG))
                .child(icon_chevron_down(12.0, fg_muted())),
        );
    }
    if working
        && let Some(p) = prog
        && p.stage == "downloading"
    {
        right = right.child(progress_bar(72.0, (p.percent / 100.0) as f32));
    }
    if let Some((word, primary)) = goal_word {
        let blocked = state.blocked.is_some();
        right = right.child(
            menu_btn(
                "gpu-driver-action".into(),
                if working {
                    "Updating…"
                } else {
                    word.as_str()
                },
                primary,
                false,
                working || blocked,
            )
            .on_press(|s: &mut AppState| {
                s.pc.gpu_driver_consent_open = true;
            }),
        );
    }

    Some(menu_row(
        format!("{} driver", driver_vendor_name(vendor)),
        Some(detail),
        err,
        right,
    ))
}

fn describe_output_color(c: &OutputColor) -> String {
    let fmt = match c.format.as_str() {
        "rgb" => "RGB",
        "444" => "YCbCr 4:4:4",
        "422" => "YCbCr 4:2:2",
        "420" => "YCbCr 4:2:0",
        other => other,
    };
    let range = if c.format == "rgb" {
        if c.full {
            ", full range"
        } else {
            ", limited range"
        }
    } else {
        ""
    };
    format!("{fmt} {}-bit{range}", c.bpc)
}

fn graphics_menu_body(s: &AppState) -> El {
    let mut body = col().w_full();
    if let Some(drv) = driver_row_view(s) {
        body = body.child(drv);
    }

    if let Some(ref prof) = s.pc.gpu_profile
        && !(prof.status == "none" && prof.ok)
    {
        let vendor = prof.vendor.as_deref().unwrap_or("");
        let is_manual = prof.status == "manual";
        let is_intel = vendor == "intel" && !is_manual;
        let saved_ll = prof.low_latency.as_deref().unwrap_or("on");
        let current_ll = s.pc.gpu_low_latency_choice.as_deref().unwrap_or(saved_ll);
        let ll_changed = is_intel && current_ll != saved_ll;

        if is_manual {
            for item in &prof.items {
                let matches = item
                    .current
                    .as_deref()
                    .is_some_and(|c| c.eq_ignore_ascii_case(&item.value));
                let val_txt = match item.current.as_deref() {
                    Some(cur) if !matches => format!("{} (now {cur})", item.value),
                    _ => item.value.clone(),
                };
                body = body.child(menu_row(
                    item.label.clone(),
                    None,
                    None,
                    status_badge(
                        val_txt,
                        if matches {
                            BadgeTone::Good
                        } else {
                            BadgeTone::Neutral
                        },
                    ),
                ));
            }
        } else {
            let apply_word = if prof.status == "changed" && !ll_changed {
                Some("Re-apply")
            } else if prof.status == "ready" || prof.status == "changed" || ll_changed {
                Some("Use")
            } else {
                None
            };
            let detail = if !ll_changed && prof.status == "changed" {
                Some(if prof.driver_updated {
                    "Driver changed some settings".into()
                } else {
                    "Some settings changed".into()
                })
            } else {
                None
            };
            let err = s.pc.gpu_profile_error.clone().or_else(|| {
                (!prof.ok).then(|| {
                    prof.message
                        .as_deref()
                        .map(trim_stop)
                        .filter(|m| !m.is_empty())
                        .unwrap_or_else(|| "The driver did not answer. Try again.".into())
                })
            });

            let mut acts = row().gap(6.0).center_y();
            if prof.status == "applied" && !ll_changed {
                acts = acts.child(status_badge("Applied".into(), BadgeTone::Good));
            }
            if is_intel {
                let next_ll = match current_ll {
                    "on" => "boost",
                    "boost" => "off",
                    _ => "on",
                };
                let ll_label = match current_ll {
                    "boost" => "On with boost",
                    "off" => "Off",
                    _ => "On",
                };
                acts = acts.child(
                    menu_btn(
                        "gpu-profile-ll".into(),
                        format!("Low latency: {ll_label}"),
                        false,
                        false,
                        false,
                    )
                    .on_press(move |s: &mut AppState| {
                        s.pc.gpu_low_latency_choice = Some(next_ll.into());
                    }),
                );
            }
            if let Some(word) = apply_word {
                acts = acts.child(
                    menu_btn("gpu-profile-apply".into(), word, true, false, false)
                        .on_press(|s: &mut AppState| s.run_gpu_profile_action("apply")),
                );
            }
            if prof.can_undo {
                acts = acts.child(
                    menu_btn("gpu-profile-undo".into(), "Undo", false, false, false)
                        .on_press(|s: &mut AppState| s.run_gpu_profile_action("undo")),
                );
            }

            body = body.child(menu_row("Exo’s driver profile", detail, err, acts));

            if vendor == "nvidia" {
                if let Some(rebar) = prof.rebar {
                    body = body.child(menu_row(
                        "Resizable BAR",
                        None,
                        None,
                        status_badge(
                            if rebar { "On".into() } else { "Off".into() },
                            if rebar {
                                BadgeTone::Good
                            } else {
                                BadgeTone::Neutral
                            },
                        ),
                    ));
                }
                for feat in prof.features.iter().filter(|f| f.state != "unavailable") {
                    let is_on = feat.state == "on";
                    body = body.child(menu_row(
                        feat.label.clone(),
                        None,
                        None,
                        status_badge(
                            if is_on { "On".into() } else { "Off".into() },
                            if is_on {
                                BadgeTone::Good
                            } else {
                                BadgeTone::Neutral
                            },
                        ),
                    ));
                }
            }
        }

        if vendor == "amd"
            && let Some(ref afmf) = s.pc.afmf3
        {
            if !afmf.offer
                && let Some(ref note) = afmf.note
            {
                body = body.child(menu_row(note.clone(), None, None, row()));
            } else if afmf.offer {
                let next_on = !afmf.on;
                body = body.child(menu_row(
                    "Fluid Motion Frames 3 preview",
                    Some(
                        "AMD’s unreleased preview: nobody outside AMD knows all it changes".into(),
                    ),
                    s.pc.afmf3_error.clone().or_else(|| afmf.note.clone()),
                    menu_switch(
                        "gpu-afmf3-switch".into(),
                        "Fluid Motion Frames 3 preview",
                        afmf.on,
                        s.pc.afmf3_busy,
                    )
                    .on_press(move |s: &mut AppState| s.toggle_gpu_afmf3(next_on)),
                ));
            }
        }

        if matches!(vendor, "amd" | "intel") {
            let panel_name = if vendor == "amd" {
                "AMD Software"
            } else {
                "Intel Graphics"
            };
            body = body.child(menu_row(
                panel_name,
                Some("Open your GPU maker's own control panel".into()),
                None,
                menu_btn("gpu-open-panel".into(), "Open", false, false, false)
                    .on_press(|s: &mut AppState| s.open_gpu_control_panel()),
            ));
        }
    } else {
        body = body.child(menu_row(
            "Graphics profile",
            Some("Reading graphics driver state…".into()),
            None,
            row(),
        ));
    }

    body = body
        .child(group_head("Manual tuning"))
        .child(crate::gpu_tuning::view(s))
        .child(group_head("Power guard"))
        .child(crate::power_guard::view(s))
        .child(group_head("Hardware telemetry & DRS"))
        .child(crate::hw_sensors::graphics_drs_view(s));

    if s.settings.frame_gen_everywhere_card {
        let on = s.settings.frame_gen_everywhere;
        body = body.child(menu_row(
            "Frame generation in games without anti-cheat",
            None,
            None,
            menu_switch(
                "set-frame-gen-everywhere".into(),
                "Frame generation in games without anti-cheat",
                on,
                false,
            )
            .on_press(move |s: &mut AppState| {
                s.set_bool_setting("frameGenEverywhere", !on);
            }),
        ));
    }

    body
}

fn display_menu_body(s: &AppState) -> El {
    let mut body = col().w_full();
    let busy = s.pc.display_busy.is_some();
    let err_for = |k: &str| -> Option<String> {
        s.pc.display_error
            .as_ref()
            .filter(|(ek, _)| ek == k)
            .map(|(_, msg)| msg.clone())
    };

    if let Some(ref disp) = s.pc.display_state
        && !disp.displays.is_empty()
    {
        let several = disp.displays.len() > 1;
        for screen in disp.displays.iter().filter(|d| !d.is_best) {
            let mut parts = vec![format!("{} Hz", screen.best.refresh)];
            if let Some(ref c) = screen.best.color {
                parts.push(describe_output_color(c));
            }
            if screen.best.scaling {
                parts.push("aspect ratio on the GPU".into());
            }
            if screen.best.video_hdr {
                parts.push("RTX Video HDR on".into());
            }
            let mut line = parts.join(", ");
            if let Some(ref why) = screen.best.why
                && screen
                    .best
                    .color
                    .as_ref()
                    .is_some_and(|c| c.format != "rgb")
            {
                line = format!("{line}. {why}");
            }
            let sid = screen.id.clone();
            let lbl = if several {
                format!("Best for {}", screen.name)
            } else {
                "Best for this screen".into()
            };
            body = body.child(menu_row(
                lbl,
                Some(line),
                err_for("best"),
                menu_btn(
                    format!("disp-best-{}", screen.id),
                    "Use best settings",
                    true,
                    false,
                    busy,
                )
                .on_press(move |s: &mut AppState| {
                    let mut m = serde_json::Map::new();
                    m.insert("display".into(), json!(sid));
                    m.insert("best".into(), json!(true));
                    s.run_gpu_display_change("set", "best", m);
                }),
            ));
        }

        let screen = disp
            .displays
            .iter()
            .find(|d| s.pc.selected_display_id.as_deref() == Some(d.id.as_str()))
            .or_else(|| disp.displays.iter().find(|d| d.main))
            .unwrap_or(&disp.displays[0]);

        if several {
            let cur_idx = disp
                .displays
                .iter()
                .position(|d| d.id == screen.id)
                .unwrap_or(0);
            let next_id = disp.displays[(cur_idx + 1) % disp.displays.len()]
                .id
                .clone();
            body = body.child(menu_row(
                "Screen",
                None,
                None,
                menu_btn(
                    format!("disp-pick-{}", screen.id),
                    screen.name.clone(),
                    false,
                    false,
                    false,
                )
                .child(icon_chevron_down(12.0, fg_muted()))
                .on_press(move |s: &mut AppState| {
                    s.pc.selected_display_id = Some(next_id.clone());
                    s.pc.display_error = None;
                }),
            ));
        }

        if screen.refreshes.len() > 1 {
            let idx = screen
                .refreshes
                .iter()
                .position(|&hz| hz == screen.refresh)
                .unwrap_or(0);
            let next_hz = screen.refreshes[(idx + 1) % screen.refreshes.len()];
            let cur_lbl = screen
                .refresh_label
                .clone()
                .unwrap_or_else(|| format!("{} Hz", screen.refresh));
            let sid = screen.id.clone();
            body = body.child(menu_row(
                "Refresh rate",
                None,
                err_for("refresh"),
                menu_btn("disp-refresh".into(), cur_lbl, false, false, busy)
                    .child(icon_chevron_down(12.0, fg_muted()))
                    .on_press(move |s: &mut AppState| {
                        let mut m = serde_json::Map::new();
                        m.insert("display".into(), json!(sid));
                        m.insert("refresh".into(), json!(next_hz));
                        s.run_gpu_display_change("set", "refresh", m);
                    }),
            ));
        }

        if screen.vendor.is_some()
            && let Some(ref color) = screen.color
        {
            let fmts: Vec<(&str, &str)> = [
                ("rgb", "RGB"),
                ("444", "4:4:4"),
                ("422", "4:2:2"),
                ("420", "4:2:0"),
            ]
            .into_iter()
            .filter(|(id, _)| screen.supported.iter().any(|c| c.format == *id))
            .collect();
            if fmts.len() > 1 {
                let cur_idx = fmts
                    .iter()
                    .position(|(id, _)| *id == color.format)
                    .unwrap_or(0);
                let (next_fmt, _) = fmts[(cur_idx + 1) % fmts.len()];
                let cur_label = fmts[cur_idx].1;
                let opts: Vec<&OutputColor> = screen
                    .supported
                    .iter()
                    .filter(|c| c.format == next_fmt)
                    .collect();
                let bpc = if opts.iter().any(|c| c.bpc == color.bpc) {
                    color.bpc
                } else {
                    opts.iter().map(|c| c.bpc).max().unwrap_or(color.bpc)
                };
                let full = next_fmt == "rgb"
                    && (if opts.iter().any(|c| c.bpc == bpc && c.full == color.full) {
                        color.full
                    } else {
                        opts.iter().any(|c| c.bpc == bpc && c.full)
                    });
                let sid = screen.id.clone();
                body = body.child(menu_row(
                    "Color format",
                    None,
                    err_for("format"),
                    menu_btn("disp-format".into(), cur_label, false, false, busy)
                        .child(icon_chevron_down(12.0, fg_muted()))
                        .on_press(move |s: &mut AppState| {
                            let mut m = serde_json::Map::new();
                            m.insert("display".into(), json!(sid));
                            m.insert("format".into(), json!(next_fmt));
                            m.insert("bpc".into(), json!(bpc));
                            m.insert("full".into(), json!(full));
                            s.run_gpu_display_change("set", "format", m);
                        }),
                ));
            }

            let mut depths: Vec<i32> = screen
                .supported
                .iter()
                .filter(|c| c.format == color.format)
                .map(|c| c.bpc)
                .collect();
            depths.sort_unstable();
            depths.dedup();
            if depths.len() > 1 {
                let idx = depths.iter().position(|&d| d == color.bpc).unwrap_or(0);
                let next_bpc = depths[(idx + 1) % depths.len()];
                let fmt = color.format.clone();
                let full = fmt == "rgb"
                    && (if screen
                        .supported
                        .iter()
                        .any(|c| c.format == fmt && c.bpc == next_bpc && c.full == color.full)
                    {
                        color.full
                    } else {
                        !color.full
                    });
                let sid = screen.id.clone();
                body = body.child(menu_row(
                    "Color depth",
                    None,
                    err_for("bpc"),
                    menu_btn(
                        "disp-bpc".into(),
                        format!("{}-bit", color.bpc),
                        false,
                        false,
                        busy,
                    )
                    .child(icon_chevron_down(12.0, fg_muted()))
                    .on_press(move |s: &mut AppState| {
                        let mut m = serde_json::Map::new();
                        m.insert("display".into(), json!(sid));
                        m.insert("format".into(), json!(fmt));
                        m.insert("bpc".into(), json!(next_bpc));
                        m.insert("full".into(), json!(full));
                        s.run_gpu_display_change("set", "bpc", m);
                    }),
                ));
            }

            if color.format == "rgb" {
                let has_full = screen
                    .supported
                    .iter()
                    .any(|c| c.format == "rgb" && c.bpc == color.bpc && c.full);
                let has_lim = screen
                    .supported
                    .iter()
                    .any(|c| c.format == "rgb" && c.bpc == color.bpc && !c.full);
                if has_full && has_lim {
                    let next_full = !color.full;
                    let bpc = color.bpc;
                    let sid = screen.id.clone();
                    body = body.child(menu_row(
                        "Dynamic range",
                        None,
                        err_for("range"),
                        menu_btn(
                            "disp-range".into(),
                            if color.full { "Full" } else { "Limited" },
                            false,
                            false,
                            busy,
                        )
                        .child(icon_chevron_down(12.0, fg_muted()))
                        .on_press(move |s: &mut AppState| {
                            let mut m = serde_json::Map::new();
                            m.insert("display".into(), json!(sid));
                            m.insert("format".into(), json!("rgb"));
                            m.insert("bpc".into(), json!(bpc));
                            m.insert("full".into(), json!(next_full));
                            s.run_gpu_display_change("set", "range", m);
                        }),
                    ));
                }
            }
        }

        if screen.hdr_supported {
            let sid = screen.id.clone();
            let next_hdr = !screen.hdr;
            body = body.child(menu_row(
                "HDR",
                None,
                err_for("hdr"),
                menu_switch(
                    format!("display-hdr-{}", screen.id),
                    "HDR",
                    screen.hdr,
                    busy,
                )
                .on_press(move |s: &mut AppState| {
                    let mut m = serde_json::Map::new();
                    m.insert("display".into(), json!(sid));
                    m.insert("hdr".into(), json!(next_hdr));
                    s.run_gpu_display_change("set", "hdr", m);
                }),
            ));
        }

        if let Some(ref ddc) = screen.ddc {
            if let Some(br) = ddc.brightness {
                let sid = screen.id.clone();
                let lo_b = ddc.brightness_min;
                let hi_b = ddc.brightness_max.max(lo_b + 1);
                let ratio = ((br - lo_b) as f32 / (hi_b - lo_b) as f32).clamp(0.0, 1.0);
                body = body.child(menu_row(
                    "Monitor brightness",
                    None,
                    err_for("ddcBrightness"),
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(
                            slider_track(
                                "ddc-br-slider",
                                "Monitor brightness",
                                180.0,
                                ratio,
                                false,
                                busy,
                            )
                            .on_drag(move |s: &mut AppState, d, _| {
                                if busy || d.phase != Phase::Up || d.cancelled() {
                                    return;
                                }
                                let r = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
                                let val = lo_b + ((hi_b - lo_b) as f32 * r).round() as i32;
                                let mut m = serde_json::Map::new();
                                m.insert("display".into(), json!(sid));
                                m.insert("brightness".into(), json!(val));
                                s.run_gpu_display_change("ddc", "ddcBrightness", m);
                            }),
                        )
                        .child(
                            el().w(40.0)
                                .justify_end()
                                .child(text(format!("{br}%")).font(type_small()).color(fg_muted())),
                        ),
                ));
            }
            if let Some(ct) = ddc.contrast {
                let sid = screen.id.clone();
                let lo_c = ddc.contrast_min;
                let hi_c = ddc.contrast_max.max(lo_c + 1);
                let ratio = ((ct - lo_c) as f32 / (hi_c - lo_c) as f32).clamp(0.0, 1.0);
                body = body.child(menu_row(
                    "Monitor contrast",
                    None,
                    err_for("ddcContrast"),
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(
                            slider_track(
                                "ddc-ct-slider",
                                "Monitor contrast",
                                180.0,
                                ratio,
                                false,
                                busy,
                            )
                            .on_drag(move |s: &mut AppState, d, _| {
                                if busy || d.phase != Phase::Up || d.cancelled() {
                                    return;
                                }
                                let r = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
                                let val = lo_c + ((hi_c - lo_c) as f32 * r).round() as i32;
                                let mut m = serde_json::Map::new();
                                m.insert("display".into(), json!(sid));
                                m.insert("contrast".into(), json!(val));
                                s.run_gpu_display_change("ddc", "ddcContrast", m);
                            }),
                        )
                        .child(
                            el().w(40.0)
                                .justify_end()
                                .child(text(format!("{ct}%")).font(type_small()).color(fg_muted())),
                        ),
                ));
            }
        }

        if let Some(ref adj) = screen.adjust {
            let changed = adj
                .controls
                .iter()
                .any(|c| (c.value - c.normal).abs() > f64::EPSILON);
            for ctrl in &adj.controls {
                let val_txt = if ctrl.key == "gamma" {
                    format!("{:.2}", ctrl.value / 100.0)
                } else {
                    format!("{}{}", ctrl.value.round() as i64, ctrl.unit)
                };
                let span = (ctrl.max - ctrl.min).max(1.0);
                let ratio = ((ctrl.value - ctrl.min) / span).clamp(0.0, 1.0) as f32;
                let step = ctrl.step.max(1.0);
                let cmin = ctrl.min;
                let cmax = ctrl.max;
                let sid = screen.id.clone();
                let key_s = ctrl.key.clone();
                body = body.child(menu_row(
                    ctrl.label.clone(),
                    None,
                    err_for(&ctrl.key),
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(
                            slider_track(
                                format!("col-slider-{}", ctrl.key),
                                ctrl.label.clone(),
                                180.0,
                                ratio,
                                false,
                                busy,
                            )
                            .on_drag(move |s: &mut AppState, d, _| {
                                if busy || d.phase != Phase::Up || d.cancelled() {
                                    return;
                                }
                                let r = (d.x / d.w.max(1.0)).clamp(0.0, 1.0) as f64;
                                let raw = cmin + r * (cmax - cmin);
                                let snapped = (raw / step).round() * step;
                                let val = snapped.clamp(cmin, cmax);
                                let mut m = serde_json::Map::new();
                                m.insert("display".into(), json!(sid));
                                m.insert("key".into(), json!(key_s));
                                m.insert("value".into(), json!(val));
                                s.run_gpu_display_change("adjust", &key_s, m);
                            }),
                        )
                        .child(
                            el().w(40.0)
                                .justify_end()
                                .child(text(val_txt).font(type_small()).color(fg_muted())),
                        ),
                ));
            }
            if changed || adj.nvidia == Some(true) {
                let sid = screen.id.clone();
                let is_amd = adj.nvidia.is_none();
                body = body.child(menu_row(
                    "Default color",
                    None,
                    err_for("reset"),
                    menu_btn("disp-color-reset".into(), "Restore", false, false, busy).on_press(
                        move |s: &mut AppState| {
                            let mut m = serde_json::Map::new();
                            m.insert("display".into(), json!(sid));
                            if is_amd {
                                m.insert("reset".into(), json!(true));
                            } else {
                                m.insert("nvidia".into(), json!(false));
                            }
                            s.run_gpu_display_change("adjust", "reset", m);
                        },
                    ),
                ));
            }
        }
    }

    if let Some(hdr_el) = crate::pc_hdr::hdr_rows_view(s) {
        body = body.child(hdr_el);
    }

    body
}

fn live_detail_line(live: &AutopilotLive) -> String {
    let mut parts = Vec::new();
    if let Some(cpu) = live.cpu {
        parts.push(format!("CPU {}%", cpu.round() as i64));
    }
    if let Some(clock) = live.clock {
        parts.push(format!("Clock {}% of base", clock.round() as i64));
    }
    if let Some(gpu) = live.gpu {
        let temp = live
            .gpu_temp
            .map(|t| format!(" {} °C", t.round() as i64))
            .unwrap_or_default();
        parts.push(format!("GPU {}%{temp}", gpu.round() as i64));
    }
    if let Some(vram) = live.vram_gb {
        if let Some(tot) = live.vram_total_gb {
            parts.push(format!("VRAM {vram:.1} of {tot:.0} GB"));
        } else {
            parts.push(format!("VRAM {vram:.1} GB"));
        }
    }
    if let Some(ram) = live.ram_gb {
        if let Some(tot) = live.ram_total_gb {
            parts.push(format!("RAM {ram:.1} of {tot:.0} GB"));
        } else {
            parts.push(format!("RAM {ram:.1} GB"));
        }
    }
    if let Some(sb) = live.standby_gb {
        parts.push(format!("Standby {sb:.1} GB"));
    }
    if let Some(fr) = live.free_gb {
        parts.push(format!("Free {fr:.1} GB"));
    }
    if let Some(dpc) = live.dpc {
        parts.push(format!("DPC {dpc:.1}%"));
    }
    parts.join(" · ")
}

fn autopilot_report_view(item: &AutopilotReport, expanded: bool) -> El {
    let id = item.id.clone();
    let stutters = if item.stutters == 0 {
        "no stutters".to_string()
    } else if item.stutters == 1 {
        "1 stutter".to_string()
    } else {
        format!("{} stutters", item.stutters)
    };
    let head = menu_row(
        format!("{} · {} min", item.title, item.minutes.round() as i64),
        Some(format!(
            "{} fps, 1% low {}, 0.1% low {} · {stutters}",
            item.fps.round() as i64,
            item.low1.round() as i64,
            item.low01.round() as i64
        )),
        None,
        menu_btn(
            format!("ap-rep-{}", item.id),
            if expanded { "Less" } else { "Details" },
            false,
            false,
            false,
        )
        .on_press(move |s: &mut AppState| {
            if s.pc.autopilot_expanded_report.as_deref() == Some(id.as_str()) {
                s.pc.autopilot_expanded_report = None;
            } else {
                s.pc.autopilot_expanded_report = Some(id.clone());
            }
        }),
    );

    if !expanded {
        return head;
    }

    let mut det = col().w_full().pl(12.0).pb(8.0).gap(4.0);
    if !item.findings.is_empty() {
        det = det.child(text("What Autopilot found").font(type_small()).color(FG));
        for f in &item.findings {
            let line = if f.suggestion.is_empty() {
                f.detail.clone()
            } else {
                format!("{} — {}", f.detail, f.suggestion)
            };
            det = det.child(
                text(format!("• {line}"))
                    .font(type_small())
                    .color(fg_muted())
                    .wrap(),
            );
        }
    }
    if !item.did.is_empty() {
        det = det.child(text("What Autopilot did").font(type_small()).color(FG));
        for d in &item.did {
            det = det.child(
                text(format!("• {d}"))
                    .font(type_small())
                    .color(fg_muted())
                    .wrap(),
            );
        }
    }
    if !item.learned.is_empty() {
        det = det.child(text("Learned for next time").font(type_small()).color(FG));
        for l in &item.learned {
            det = det.child(
                text(format!("• {l}"))
                    .font(type_small())
                    .color(fg_muted())
                    .wrap(),
            );
        }
    }
    col().w_full().child(head).child(det)
}

fn board_menu_body(s: &AppState) -> El {
    let mut body = col().w_full();
    let about = s.pc.system_about.as_ref();
    let hints = bios_vendor_hints(about.and_then(|a| a.motherboard.as_deref()));
    let armed = s.pc.armed_confirm.as_deref();

    let bios_detail = about.and_then(|a| {
        let mut p = Vec::new();
        if let Some(ref b) = a.bios
            && !b.is_empty()
        {
            p.push(b.clone());
        }
        if let Some(ref d) = a.bios_date
            && !d.is_empty()
        {
            p.push(d.clone());
        }
        (!p.is_empty()).then(|| p.join(", "))
    });
    let bios_armed = armed == Some("board-bios");
    body = body.child(menu_row(
        "BIOS",
        bios_detail,
        s.pc.board_restart_error
            .as_ref()
            .filter(|(k, _)| k == "bios")
            .map(|(_, e)| e.clone()),
        menu_btn(
            "board-restart-bios".into(),
            if bios_armed {
                "Restart now?"
            } else {
                "Restart"
            },
            bios_armed,
            false,
            false,
        )
        .on_press(|s: &mut AppState| {
            if s.pc.armed_confirm.as_deref() == Some("board-bios") {
                s.run_bios_update("bios");
            } else {
                s.pc.armed_confirm = Some("board-bios".into());
            }
        }),
    ));

    let rec_armed = armed == Some("board-recovery");
    body = body.child(menu_row(
        "Recovery",
        None,
        s.pc.board_restart_error
            .as_ref()
            .filter(|(k, _)| k == "recovery")
            .map(|(_, e)| e.clone()),
        menu_btn(
            "board-restart-recovery".into(),
            if rec_armed { "Restart now?" } else { "Restart" },
            rec_armed,
            false,
            false,
        )
        .on_press(|s: &mut AppState| {
            if s.pc.armed_confirm.as_deref() == Some("board-recovery") {
                s.run_bios_update("recovery");
            } else {
                s.pc.armed_confirm = Some("board-recovery".into());
            }
        }),
    ));

    if let Some(sb) = about.and_then(|a| a.secure_boot) {
        let det = (!sb)
            .then(|| hints.map(|h| format!("BIOS: {}", h.secure_boot)))
            .flatten();
        body = body.child(menu_row(
            "Secure Boot",
            det,
            None,
            status_badge(
                if sb { "On".into() } else { "Off".into() },
                if sb { BadgeTone::Good } else { BadgeTone::Warn },
            ),
        ));
    }
    if let Some(tpm) = about
        .and_then(|a| a.tpm.as_deref())
        .filter(|t| !t.is_empty())
    {
        let has_tpm = tpm != "None";
        let det = (!has_tpm)
            .then(|| hints.map(|h| format!("BIOS: {}", h.tpm)))
            .flatten();
        body = body.child(menu_row(
            "TPM",
            det,
            None,
            status_badge(
                tpm.to_string(),
                if has_tpm {
                    BadgeTone::Good
                } else {
                    BadgeTone::Warn
                },
            ),
        ));
    }
    if let Some(mi) = about.and_then(|a| a.memory_integrity) {
        body = body.child(menu_row(
            "Memory integrity",
            None,
            None,
            status_badge(
                if mi { "On".into() } else { "Off".into() },
                if mi {
                    BadgeTone::Good
                } else {
                    BadgeTone::Neutral
                },
            ),
        ));
    }

    if let Some(ref ap) = s.pc.autopilot {
        body = body.child(group_head("Autopilot"));
        body = body.child(menu_row(
            "Autopilot",
            Some(autopilot_active_summary(ap)),
            s.pc.autopilot_error.clone(),
            status_badge(
                if ap.on { "Active".into() } else { "Off".into() },
                if ap.on {
                    BadgeTone::Good
                } else {
                    BadgeTone::Neutral
                },
            ),
        ));

        if let Some(ref now) = ap.now
            && !now.did.is_empty()
        {
            body = body.child(menu_row(
                "Doing now",
                Some(now.did.join(" · ")),
                None,
                row(),
            ));
        }

        if let Some(ref dl) = ap.downloads_paused
            && dl.active
        {
            let mut parts = Vec::new();
            if dl.jobs > 0 {
                parts.push(format!(
                    "{} background download job{}",
                    dl.jobs,
                    if dl.jobs == 1 { "" } else { "s" }
                ));
            }
            if !dl.services.is_empty() {
                parts.push(dl.services.join(", "));
            }
            let detail = if parts.is_empty() {
                "Background Windows downloads paused while playing.".into()
            } else {
                format!("Paused while playing: {}.", parts.join(" · "))
            };
            body = body.child(menu_row(
                "Background downloads",
                Some(detail),
                None,
                status_badge("Paused".into(), BadgeTone::Good),
            ));
        }

        if let Some(ref sp) = ap.standby_purge {
            let detail = if sp.available {
                "Clears cached standby memory during play if free memory drops under 1 GB.".into()
            } else {
                sp.reason
                    .clone()
                    .unwrap_or_else(|| "Unavailable without administrator permission.".into())
            };
            body = body.child(menu_row(
                "Standby memory purge",
                Some(detail),
                None,
                status_badge(
                    if sp.available {
                        "Ready".into()
                    } else {
                        "Unavailable".into()
                    },
                    if sp.available {
                        BadgeTone::Good
                    } else {
                        BadgeTone::Neutral
                    },
                ),
            ));
        }

        let mi = &ap.memory_integrity;
        if !mi.status.is_empty() {
            let mi_ctrl = match mi.status.as_str() {
                "off" => {
                    let mut r = row()
                        .gap(6.0)
                        .center_y()
                        .child(status_badge("Off".into(), BadgeTone::Good));
                    if mi.can_undo {
                        r = r.child(
                            menu_btn("ap-mi-restore".into(), "Restore", false, false, false)
                                .on_press(|s: &mut AppState| {
                                    s.autopilot_toggle_memory_integrity(false)
                                }),
                        );
                    }
                    r
                }
                "unlocked" => {
                    let is_armed = armed == Some("ap-mi-on");
                    menu_btn(
                        "ap-mi-opt".into(),
                        if is_armed {
                            "Turn off Memory Integrity?"
                        } else {
                            "Optimize"
                        },
                        true,
                        false,
                        false,
                    )
                    .on_press(|s: &mut AppState| {
                        if s.pc.armed_confirm.as_deref() == Some("ap-mi-on") {
                            s.autopilot_toggle_memory_integrity(true);
                        } else {
                            s.pc.armed_confirm = Some("ap-mi-on".into());
                        }
                    })
                }
                "pending-off" | "pending-on" => status_badge("Restart".into(), BadgeTone::Warn),
                "required" => status_badge("Protected".into(), BadgeTone::Good),
                "conflict" => {
                    if mi.can_undo {
                        let is_armed = armed == Some("ap-mi-back");
                        menu_btn(
                            "ap-mi-back".into(),
                            if is_armed {
                                "Turn Memory Integrity back on?"
                            } else {
                                "Turn back on"
                            },
                            true,
                            false,
                            false,
                        )
                        .on_press(|s: &mut AppState| {
                            if s.pc.armed_confirm.as_deref() == Some("ap-mi-back") {
                                s.autopilot_toggle_memory_integrity(false);
                            } else {
                                s.pc.armed_confirm = Some("ap-mi-back".into());
                            }
                        })
                    } else {
                        status_badge("Required".into(), BadgeTone::Warn)
                    }
                }
                _ => status_badge("Managed".into(), BadgeTone::Neutral),
            };
            body = body.child(menu_row(
                "Memory Integrity",
                (!mi.reason.is_empty()).then(|| mi.reason.clone()),
                None,
                mi_ctrl,
            ));
        }

        if !ap.always_demote.is_empty() {
            let mut chips = row().gap(6.0).center_y();
            for name in &ap.always_demote {
                let nm = name.clone();
                chips = chips.child(
                    menu_btn(
                        format!("ap-demote-{name}"),
                        format!("{name} ×"),
                        false,
                        false,
                        false,
                    )
                    .on_press(move |s: &mut AppState| {
                        s.autopilot_remove_rule("alwaysDemote", &nm);
                    }),
                );
            }
            body = body.child(menu_row(
                "Always held back while playing",
                None,
                None,
                chips,
            ));
        }
        if !ap.suspend.is_empty() {
            let mut chips = row().gap(6.0).center_y();
            for name in &ap.suspend {
                let nm = name.clone();
                chips = chips.child(
                    menu_btn(
                        format!("ap-susp-{name}"),
                        format!("{name} ×"),
                        false,
                        false,
                        false,
                    )
                    .on_press(move |s: &mut AppState| {
                        s.autopilot_remove_rule("suspend", &nm);
                    }),
                );
            }
            body = body.child(menu_row("Paused while playing", None, None, chips));
        }

        if let Some(ref live) = ap.live {
            let stutters = if live.stutters == 1 {
                "1 stutter".to_string()
            } else {
                format!("{} stutters", live.stutters)
            };
            let line1 = format!(
                "{} fps, 1% low {}, 0.1% low {} · {stutters}",
                live.fps.round() as i64,
                live.low1.round() as i64,
                live.low01.round() as i64
            );
            let line2 = live_detail_line(live);
            let full = if line2.is_empty() {
                line1
            } else {
                format!("{line1} · {line2}")
            };
            body = body.child(menu_row(
                format!("Now · {}", live.title),
                Some(full),
                None,
                row(),
            ));
        } else if ap.reports.is_empty() {
            body = body.child(menu_row(
                "No sessions yet",
                Some("Play a game and what Autopilot saw shows here afterwards.".into()),
                None,
                row(),
            ));
        }

        for rep in &ap.reports {
            let expanded = s.pc.autopilot_expanded_report.as_deref() == Some(rep.id.as_str());
            body = body.child(autopilot_report_view(rep, expanded));
        }
    }

    body = body.child(group_head("System & undo"));

    let pending_steps: Vec<_> = best_steps_list(&s.pc, &s.settings)
        .into_iter()
        .filter(|st| !st.done)
        .collect();
    if !pending_steps.is_empty() || s.pc.best_setup_error.is_some() {
        let names: Vec<String> = pending_steps.iter().map(|st| st.what.clone()).collect();
        let running = s.pc.best_setup_running.is_some();
        body = body.child(menu_row(
            "Best settings",
            (!names.is_empty()).then(|| names.join(" · ")),
            s.pc.best_setup_error.clone(),
            if pending_steps.is_empty() {
                row()
            } else {
                menu_btn(
                    "board-best-setup".into(),
                    if running {
                        "Applying…"
                    } else {
                        "Use best settings"
                    },
                    true,
                    false,
                    running,
                )
                .on_press(|s: &mut AppState| s.run_best_setup_all())
            },
        ));
    }

    if let Some(ref hp) = s.pc.health {
        for item in hp.items.iter().filter(|i| {
            !matches!(
                i.key.as_str(),
                "chipset" | "power" | "network" | "net-adapter" | "net-dns" | "net-tcp" | "netlink"
            ) && ((!i.applied && !i.current) || (i.applied && i.can_undo))
        }) {
            let ctrl = match item.key.as_str() {
                "gpupref" => {
                    if item.applied && item.can_undo {
                        menu_btn("hp-gpupref-undo".into(), "Undo", false, false, false)
                            .on_press(|s: &mut AppState| s.run_health_action("gpupref-undo"))
                    } else if !item.applied {
                        menu_btn("hp-gpupref".into(), "Use the fast card", true, false, false)
                            .on_press(|s: &mut AppState| s.run_health_action("gpupref"))
                    } else {
                        row()
                    }
                }
                "cpumicrocode" => {
                    if let Some(url) = item.url.clone() {
                        menu_btn("hp-cpumicrocode".into(), "BIOS page", false, false, false)
                            .on_press(move |s: &mut AppState| s.open_external_url(&url))
                    } else {
                        row()
                    }
                }
                "ryzenbranch" => {
                    menu_btn("hp-winupdate".into(), "Windows Update", false, false, false)
                        .on_press(|s: &mut AppState| s.run_health_action("winupdate"))
                }
                "gpusched" => menu_btn("hp-gpusched".into(), "Open Settings", false, false, false)
                    .on_press(|s: &mut AppState| s.run_health_action("gpusched")),
                _ => row(),
            };
            body = body.child(menu_row(
                item.label.clone(),
                (!item.detail.is_empty()).then(|| item.detail.clone()),
                None,
                ctrl,
            ));
        }
    }

    if let Some(ref ir) = s.pc.interrupts
        && !ir.devices.is_empty()
    {
        if ir.reset {
            body = body.child(menu_row(
                "A driver update reset your interrupts",
                None,
                s.pc.interrupts_error.clone(),
                menu_btn("ir-reapply".into(), "Switch back", true, false, false)
                    .on_press(|s: &mut AppState| s.run_interrupts_action("reapply", None)),
            ));
        } else {
            let needs = ir.devices.iter().any(|d| d.plan == "change") && !ir.restart;
            if needs || ir.restart || ir.can_undo {
                let sharing = ir
                    .devices
                    .iter()
                    .filter(|d| d.plan == "change" && d.mode != "msi")
                    .count();
                let detail = if ir.restart {
                    None
                } else {
                    ir.result.clone().or_else(|| {
                        (sharing > 0).then(|| {
                            if sharing == 1 {
                                "A device shares an interrupt line".into()
                            } else {
                                format!("{sharing} devices share an interrupt line")
                            }
                        })
                    })
                };
                let mut acts = row().gap(6.0).center_y();
                if ir.restart {
                    acts = acts.child(status_badge("Restart to finish".into(), BadgeTone::Warn));
                }
                if needs {
                    acts = acts.child(
                        menu_btn("ir-apply".into(), "Optimize", true, false, false)
                            .on_press(|s: &mut AppState| s.run_interrupts_action("apply", None)),
                    );
                }
                if ir.can_undo {
                    acts = acts.child(
                        menu_btn("ir-undo".into(), "Undo", false, false, false)
                            .on_press(|s: &mut AppState| s.run_interrupts_action("undo", None)),
                    );
                }
                let open = s.pc.interrupts_devices_open;
                acts = acts.child(
                    menu_btn(
                        "ir-devices-toggle".into(),
                        if open { "Hide" } else { "Devices" },
                        false,
                        false,
                        false,
                    )
                    .on_press(|s: &mut AppState| {
                        s.pc.interrupts_devices_open = !s.pc.interrupts_devices_open
                    }),
                );
                body = body.child(menu_row(
                    "Hardware interrupts",
                    detail,
                    s.pc.interrupts_error.clone(),
                    acts,
                ));
                if open {
                    for dev in &ir.devices {
                        let plan_word = match dev.plan.as_str() {
                            "keep" | "skip" => Some("Left alone"),
                            "restart" => Some("After a restart"),
                            _ => None,
                        };
                        let mode_word = match dev.mode.as_str() {
                            "msi" => "MSI mode",
                            "line" => "Shared line",
                            _ => "Not running",
                        };
                        let det = match plan_word {
                            Some(pw) => format!("{} · {pw}", dev.kind),
                            None => dev.kind.clone(),
                        };
                        let dev_id = dev.id.clone();
                        let ctrl = if dev.changed {
                            menu_btn(format!("ir-rev-{}", dev.id), "Undo", false, false, false)
                                .on_press(move |s: &mut AppState| {
                                    s.run_interrupts_action("revert", Some(&dev_id))
                                })
                        } else {
                            status_badge(
                                mode_word.into(),
                                if dev.mode == "msi" {
                                    BadgeTone::Good
                                } else {
                                    BadgeTone::Neutral
                                },
                            )
                        };
                        body = body.child(menu_row(dev.name.clone(), Some(det), None, ctrl));
                    }
                }
            }
        }
    }

    if let Some(ref hp) = s.pc.health {
        let net_items: Vec<_> = hp
            .items
            .iter()
            .filter(|i| matches!(i.key.as_str(), "net-adapter" | "net-dns" | "net-tcp"))
            .collect();
        if !net_items.is_empty() {
            body = body.child(group_head("Network"));
            let net_busy = s.pc.network_busy.is_some();
            for item in net_items {
                let api_item = match item.key.as_str() {
                    "net-adapter" => "adapter",
                    "net-dns" => "dns",
                    _ => "tcp",
                };
                let key_c = item.key.clone();
                let next_on = !item.applied;
                let err =
                    s.pc.network_error
                        .as_ref()
                        .filter(|(k, _)| k == &item.key)
                        .map(|(_, e)| e.clone());
                body = body.child(menu_row(
                    item.label.clone(),
                    (!item.off && !item.detail.is_empty()).then(|| item.detail.clone()),
                    err,
                    menu_switch(
                        format!("net-sw-{}", item.key),
                        item.label.clone(),
                        item.applied,
                        net_busy,
                    )
                    .on_press(move |s: &mut AppState| {
                        s.run_network_item_toggle(&key_c, api_item, next_on)
                    }),
                ));
            }
            let check_detail = hp.check.as_ref().map(|chk| {
                let fmt_sample = |sm: &exo_rpc::NetSample| -> String {
                    let mut p = Vec::new();
                    if let Some(ping) = sm.ping {
                        p.push(format!("{} ms ping", ping.round() as i64));
                    }
                    if let Some(jit) = sm.jitter {
                        p.push(format!("{} ms jitter", jit.round() as i64));
                    }
                    if let Some(lk) = sm.lookup {
                        p.push(format!("{} ms lookup", lk.round() as i64));
                    }
                    if p.is_empty() {
                        "no answer".into()
                    } else {
                        p.join(", ")
                    }
                };
                match chk.before.as_ref() {
                    Some(b) => format!(
                        "Before: {}. After: {}. {}",
                        fmt_sample(b),
                        fmt_sample(&chk.after),
                        chk.said
                    ),
                    None => fmt_sample(&chk.after),
                }
            });
            body = body.child(menu_row(
                "Connection check",
                check_detail,
                s.pc.network_error
                    .as_ref()
                    .filter(|(k, _)| k == "check")
                    .map(|(_, e)| e.clone()),
                menu_btn("net-check-btn".into(), "Check", false, false, net_busy)
                    .on_press(|s: &mut AppState| s.run_health_action("netcheck")),
            ));
        }
    }

    if let Some(ref qos) = s.pc.qos_state
        && !qos.games.is_empty()
    {
        body = body.child(group_head("Per-game QoS (DSCP 46)"));
        if !qos.note.is_empty() {
            body = body.child(
                el().w_full().pb(6.0).child(
                    text(qos.note.clone())
                        .font(type_small())
                        .color(fg_muted())
                        .wrap(),
                ),
            );
        }
        let qbusy = s.pc.qos_busy.is_some();
        for game in &qos.games {
            let gid = game.game_id.clone();
            let next_on = !game.enabled;
            let det = if game.enabled {
                format!("DSCP 46 active for {}", game.exe_name)
            } else {
                format!("Opt-in DSCP 46 policy for {}", game.exe_name)
            };
            let err =
                s.pc.qos_error
                    .as_ref()
                    .filter(|(k, _)| k == &game.game_id)
                    .map(|(_, e)| e.clone());
            body = body.child(menu_row(
                game.title.clone(),
                Some(det),
                err,
                menu_switch(
                    format!("pc-best-qos-{}", game.game_id),
                    game.title.clone(),
                    game.enabled,
                    qbusy,
                )
                .on_press(move |s: &mut AppState| s.toggle_system_qos(&gid, next_on)),
            ));
        }
    }

    if let Some(ref ov) = s.pc.overlays_state
        && !ov.items.is_empty()
    {
        body = body.child(group_head("Overlays"));
        let obusy = s.pc.overlays_busy.is_some();
        for item in &ov.items {
            let mut det_parts = Vec::new();
            if !item.cost.is_empty() {
                det_parts.push(item.cost.as_str());
            }
            if !item.detail.is_empty() {
                det_parts.push(item.detail.as_str());
            }
            if let Some(ref steps) = item.manual_steps
                && !steps.is_empty()
            {
                det_parts.push(steps.as_str());
            }
            let detail = (!det_parts.is_empty()).then(|| det_parts.join(" · "));
            let err =
                s.pc.overlays_error
                    .as_ref()
                    .filter(|(k, _)| k == &item.id)
                    .map(|(_, e)| e.clone());
            let mut ctrl = row().gap(6.0).center_y();
            if item.enabled && item.can_toggle {
                let oid = item.id.clone();
                ctrl = ctrl.child(
                    menu_btn(
                        format!("ov-off-{}", item.id),
                        "Turn off",
                        false,
                        false,
                        obusy,
                    )
                    .on_press(move |s: &mut AppState| s.run_system_overlay_action(&oid, "off")),
                );
            }
            if !item.enabled && item.can_undo {
                let oid = item.id.clone();
                ctrl = ctrl.child(
                    menu_btn(
                        format!("ov-restore-{}", item.id),
                        "Put back",
                        false,
                        false,
                        obusy,
                    )
                    .on_press(move |s: &mut AppState| s.run_system_overlay_action(&oid, "restore")),
                );
            }
            let badge_label = if item.enabled {
                "On"
            } else if item.status == "unknown" {
                "Check"
            } else {
                "Off"
            };
            ctrl = ctrl.child(status_badge(
                badge_label.into(),
                if !item.enabled {
                    BadgeTone::Good
                } else {
                    BadgeTone::Neutral
                },
            ));
            body = body.child(menu_row(item.name.clone(), detail, err, ctrl));
        }
    }

    if let Some(ref tuning) = s.pc.tuning {
        body = body.child(group_head("System"));
        let busy = s.pc.tuning_busy.is_some();
        if let Some(ref mig) = tuning.migration {
            if mig.pending {
                let err =
                    s.pc.tuning_error
                        .as_ref()
                        .filter(|(k, _)| k == "migrate")
                        .map(|(_, e)| e.clone());
                body = body.child(menu_row(
                    "Retired playbook tweaks",
                    (!mig.summary.is_empty()).then(|| mig.summary.clone()),
                    err,
                    menu_btn(
                        "sys-tuning-migrate".into(),
                        if s.pc.tuning_busy.as_deref() == Some("migrate") {
                            "Reverting…"
                        } else {
                            "Revert now"
                        },
                        true,
                        false,
                        busy,
                    )
                    .on_press(|s: &mut AppState| s.run_system_tuning_migrate()),
                ));
            } else if !mig.reverted.is_empty() && !mig.summary.is_empty() {
                body = body.child(
                    el().w_full().py(6.0).child(
                        text(mig.summary.clone())
                            .font(type_small())
                            .color(fg_muted())
                            .wrap(),
                    ),
                );
            }
        }
        for item in tuning.items.iter().filter(|i| {
            i.key != "integrity" && i.key != "mouseAccel" && (i.key != "vrr" || i.can_undo)
        }) {
            let det = if item.off {
                "Off: Use best settings leaves it as it is.".to_string()
            } else if item.opt_in && !item.on && item.blocked.is_none() {
                format!("{} Only this switch sets it.", item.detail)
            } else {
                item.detail.clone()
            };
            let err =
                s.pc.tuning_error
                    .as_ref()
                    .filter(|(k, _)| k == &item.key)
                    .map(|(_, e)| e.clone());
            let mut ctrl = row().gap(6.0).center_y();
            if item.on && item.can_undo {
                let k_undo = item.key.clone();
                ctrl = ctrl.child(
                    menu_btn(
                        format!("tune-undo-{}", item.key),
                        "Put back",
                        false,
                        false,
                        busy,
                    )
                    .on_press(move |s: &mut AppState| s.run_system_tuning_item(&k_undo, false)),
                );
            }
            if item.key != "vrr" {
                let k_sw = item.key.clone();
                let next_on = !item.on;
                let sw_disabled = busy || (!item.on && item.blocked.is_some());
                ctrl = ctrl.child(
                    menu_switch(
                        format!("tune-sw-{}", item.key),
                        item.label.clone(),
                        item.on,
                        sw_disabled,
                    )
                    .on_press(move |s: &mut AppState| s.run_system_tuning_item(&k_sw, next_on)),
                );
            }
            body = body.child(menu_row(item.label.clone(), Some(det), err, ctrl));
        }

        let lat_detail = match tuning.latency.as_ref().and_then(|l| l.after.as_ref()) {
            Some(after) => {
                let dpc = format!(
                    "Longest DPC {} µs{}",
                    after.dpc_us.round() as i64,
                    after
                        .dpc_driver
                        .as_deref()
                        .map(|d| format!(" ({d})"))
                        .unwrap_or_default()
                );
                let sample = if after.isr_us > 0.0 {
                    format!(
                        "{dpc}, interrupt {} µs{}",
                        after.isr_us.round() as i64,
                        after
                            .isr_driver
                            .as_deref()
                            .map(|d| format!(" ({d})"))
                            .unwrap_or_default()
                    )
                } else {
                    dpc
                };
                match tuning
                    .latency
                    .as_ref()
                    .and_then(|l| l.said.as_deref())
                    .filter(|s| !s.is_empty())
                {
                    Some(said) => format!("{sample}. {said}"),
                    None => sample,
                }
            }
            None => {
                "How long drivers hold the processor, measured for ten seconds. Changes nothing."
                    .into()
            }
        };
        body = body.child(menu_row(
            "Latency check",
            Some(lat_detail),
            None,
            menu_btn(
                "sys-latency-btn".into(),
                if s.pc.latency_running {
                    "Measuring…"
                } else {
                    "Check"
                },
                false,
                false,
                busy || s.pc.latency_running,
            )
            .on_press(|s: &mut AppState| s.run_latency_check()),
        ));

        if tuning.restart {
            body = body.child(
                el().w_full().pt(8.0).child(
                    text("Restart Windows to finish.")
                        .font(type_small())
                        .color(warn()),
                ),
            );
        }
    }

    body
}

fn auto_tune_cinema_layer(s: &AppState, blur: bool) -> Option<El> {
    let running = s.pc.auto_tune_running;
    let finished = s.pc.auto_tune_finished;
    if !running && !finished {
        return None;
    }
    let stages = &s.pc.auto_tune_stages;
    let total = stages.len().max(1);
    let completed = stages
        .iter()
        .filter(|st| {
            matches!(
                st.status,
                AutoTuneStageStatus::Done | AutoTuneStageStatus::Warn
            )
        })
        .count();
    let has_warn = stages
        .iter()
        .any(|st| st.status == AutoTuneStageStatus::Warn);
    let active_idx = stages
        .iter()
        .position(|st| st.status == AutoTuneStageStatus::Active);
    let active_stage = active_idx.and_then(|i| stages.get(i));
    let needs_restart = any_restart_required(&s.pc);
    let percent = if finished {
        100
    } else {
        ((completed as f64 / total as f64) * 100.0).round() as i32
    };
    let stage_counter = if running && let Some(i) = active_idx {
        format!("Stage {} of {total} · {percent}%", i + 1)
    } else {
        format!("{completed} of {total} stages · {percent}%")
    };
    let headline = if running {
        active_stage.map(|st| st.label).unwrap_or("Optimizing PC")
    } else if has_warn {
        "Auto Tune finished"
    } else {
        "PC tuned for peak performance"
    };
    let detail = if running {
        active_stage
            .map(|st| st.detail.as_str())
            .unwrap_or("Applying performance playbook...")
    } else if needs_restart {
        "Deep kernel and system tweaks are staged. Restart to finish."
    } else {
        "All stages applied and verified."
    };

    let progress_val = if running {
        completed as f32 / total as f32
    } else {
        1.0
    };

    let mut milestones = row().gap(10.0).center_y();
    for st in stages {
        let dot_color = match st.status {
            AutoTuneStageStatus::Done => good(),
            AutoTuneStageStatus::Warn => warn(),
            AutoTuneStageStatus::Active => FG,
            AutoTuneStageStatus::Idle => fg_muted().alpha(0.4),
        };
        milestones = milestones.child(
            row()
                .gap(5.0)
                .center_y()
                .child(el().size(6.0, 6.0).radius(3.0).fill(dot_color))
                .child(text(st.id.short_label()).font(type_small()).color(
                    if st.status == AutoTuneStageStatus::Active {
                        FG
                    } else {
                        fg_muted()
                    },
                )),
        );
    }

    let hero_icon = if finished {
        el().size(64.0, 64.0)
            .radius(32.0)
            .center()
            .fill(if has_warn {
                warn().alpha(0.16)
            } else {
                good().alpha(0.16)
            })
            .border(1.5, if has_warn { warn() } else { good() })
            .child(icon_check(28.0, if has_warn { warn() } else { good() }))
    } else {
        let icon = match active_stage.map(|st| st.id.key()) {
            Some("graphics") => icon_monitor(28.0, FG),
            Some("cleanup") => icon_hard_drive(28.0, FG),
            _ => icon_cpu(28.0, FG),
        };
        el().size(64.0, 64.0)
            .radius(32.0)
            .center()
            .fill(FG.alpha(0.08))
            .border(1.5, float_line())
            .child(icon)
    };

    let mut card_body = col()
        .w_full()
        .px(28.0)
        .py(28.0)
        .gap(14.0)
        .center()
        .child(hero_icon)
        .child(text(stage_counter).font(type_small()).color(fg_muted()))
        .child(text(headline).font(type_page()).color(FG))
        .child(text(detail).font(type_body()).color(fg_muted()))
        .child(progress_bar(480.0, progress_val))
        .child(milestones);

    if finished {
        let mut summary = col()
            .w_full()
            .pt(8.0)
            .gap(6.0)
            .px(14.0)
            .py(14.0)
            .radius(RADIUS_CARD)
            .fill(BG.alpha(0.5))
            .border(1.0, float_line());
        for st in stages {
            let is_warn = st.status == AutoTuneStageStatus::Warn;
            summary = summary.child(
                row()
                    .w_full()
                    .gap(10.0)
                    .center_y()
                    .child(icon_check(14.0, if is_warn { warn() } else { good() }))
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .gap(1.0)
                            .child(text(st.label).font(type_control()).color(FG))
                            .child(
                                text(st.detail.clone())
                                    .font(type_small())
                                    .color(if is_warn { warn() } else { fg_muted() })
                                    .wrap(),
                            ),
                    ),
            );
        }
        card_body = card_body.child(summary);

        if let Some(ref err) = s.pc.auto_tune_cinema_error {
            card_body = card_body.child(text(err.clone()).font(type_small()).color(bad()).wrap());
        }

        let foot = if needs_restart {
            row()
                .gap(10.0)
                .center_y()
                .child(
                    menu_btn(
                        "cine-not-now".into(),
                        "Not now",
                        false,
                        false,
                        s.pc.auto_tune_restarting,
                    )
                    .on_press(|s: &mut AppState| {
                        s.pc.auto_tune_finished = false;
                        s.pc.auto_tune_cinema_error = None;
                    }),
                )
                .child(
                    menu_btn(
                        "cine-restart-now".into(),
                        if s.pc.auto_tune_restarting {
                            "Restarting..."
                        } else {
                            "Restart now"
                        },
                        true,
                        false,
                        s.pc.auto_tune_restarting,
                    )
                    .on_press(|s: &mut AppState| s.run_bios_update("windows")),
                )
        } else {
            row()
                .gap(10.0)
                .center_y()
                .child(
                    menu_btn("cine-run-again".into(), "Run again", false, false, false)
                        .on_press(|s: &mut AppState| s.run_auto_tune_pipeline()),
                )
                .child(
                    menu_btn("cine-done".into(), "Done", true, false, false).on_press(
                        |s: &mut AppState| {
                            s.pc.auto_tune_finished = false;
                            s.pc.auto_tune_cinema_error = None;
                        },
                    ),
                )
        };
        card_body = card_body.child(foot);
    }

    let card = glass_panel_opt("exo-at-cinema-card", 620.0, blur)
        .role("alertdialog")
        .label(headline)
        .transition(modal_card_transition())
        .child(card_body);

    let scrim = el()
        .key("exo-at-cinema-scrim")
        .w_full()
        .h_full()
        .center()
        .fill(BG.alpha(0.76))
        .transition(modal_scrim_transition())
        .modal(true, move |s: &mut AppState| {
            if !s.pc.auto_tune_running {
                s.pc.auto_tune_finished = false;
            }
        })
        .on_key(|s: &mut AppState, ev, _| {
            if ev.key == Key::Escape && !s.pc.auto_tune_running {
                s.pc.auto_tune_finished = false;
                true
            } else {
                false
            }
        })
        .child(card);

    Some(scrim)
}

fn driver_consent_layer(s: &AppState, blur: bool) -> Option<El> {
    if !s.pc.gpu_driver_consent_open {
        return None;
    }
    let state = s.pc.gpu_driver.as_ref()?;
    let vendor = state.vendor.as_deref()?;
    let name = driver_vendor_name(vendor);
    let go_back = state.advised.as_ref().and_then(|a| a.back.as_ref());
    let (version, size_bytes, kind, title) = if let Some(back) = go_back {
        (
            back.clone(),
            0u64,
            "game",
            format!("Go back to {name} {back}?"),
        )
    } else {
        let latest = state.latest.as_ref()?;
        let word = if state.update { "Update" } else { "Reinstall" };
        (
            latest.version.clone(),
            latest.size,
            latest.kind.as_str(),
            format!("{word} the {name} driver?"),
        )
    };

    let (installs, leaves_out, restores) = match vendor {
        "nvidia" => (
            "The driver alone, as a clean install, with HDMI/DisplayPort audio while a screen plays sound",
            "NVIDIA app, telemetry, ShadowPlay, FrameView, PhysX",
            true,
        ),
        "amd" if state.software => (
            "The driver and AMD Software’s minimal view, which Fluid Motion Frames needs",
            "AMD Software’s overlay, recording, streaming and tuning",
            true,
        ),
        "amd" => (
            "The driver alone",
            "AMD Software, its overlay, recording and tuning",
            true,
        ),
        _ => (
            "The driver alone",
            "Intel Graphics Software, Driver & Support Assistant, GameAssist, data collection",
            false,
        ),
    };
    let size_txt = if size_bytes > 0 {
        format_driver_size(size_bytes)
    } else {
        "About 1 GB".into()
    };
    let license_url = match vendor {
        "nvidia" => NVIDIA_LICENSE.to_string(),
        "amd" => AMD_LICENSE.to_string(),
        _ => state
            .latest
            .as_ref()
            .and_then(|l| l.page.clone())
            .unwrap_or_else(|| "https://www.intel.com".into()),
    };
    let is_go_back = go_back.is_some();

    let mut content = col()
        .w_full()
        .px(24.0)
        .py(24.0)
        .gap(12.0)
        .child(text(title.clone()).font(type_title()).color(FG))
        .child(
            text(format!("{name} {kind} {version}"))
                .font(type_body())
                .color(fg_muted()),
        )
        .child(menu_row(
            format!("Downloads from {name}"),
            None,
            None,
            text(size_txt).font(type_small()).color(fg_muted()),
        ))
        .child(menu_row("Installs", Some(installs.into()), None, row()))
        .child(menu_row("Leaves out", Some(leaves_out.into()), None, row()));

    if restores {
        content = content.child(menu_row(
            "Afterwards",
            Some("Exo puts your GPU profile back".into()),
            None,
            row(),
        ));
    }

    content = content.child(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .pt(6.0)
            .child(
                menu_btn(
                    "driver-consent-license".into(),
                    "View vendor license",
                    false,
                    false,
                    false,
                )
                .on_press(move |s: &mut AppState| s.open_external_url(&license_url)),
            )
            .child(
                row()
                    .gap(8.0)
                    .child(
                        menu_btn(
                            "driver-consent-cancel".into(),
                            "Cancel",
                            false,
                            false,
                            false,
                        )
                        .on_press(|s: &mut AppState| {
                            s.pc.gpu_driver_consent_open = false;
                        }),
                    )
                    .child(
                        menu_btn(
                            "driver-consent-install".into(),
                            "Install",
                            true,
                            false,
                            false,
                        )
                        .on_press(move |s: &mut AppState| {
                            s.start_gpu_driver_install(is_go_back);
                        }),
                    ),
            ),
    );

    let card = glass_panel_opt("gpu-driver-consent-dialog", 540.0, blur)
        .role("dialog")
        .label(title)
        .transition(modal_card_transition())
        .child(content);

    Some(
        el().key("gpu-driver-consent-scrim")
            .w_full()
            .h_full()
            .center()
            .fill(BG.alpha(0.70))
            .transition(modal_scrim_transition())
            .modal(true, |s: &mut AppState| {
                s.pc.gpu_driver_consent_open = false
            })
            .on_key(|s: &mut AppState, ev, _| {
                if ev.key == Key::Escape {
                    s.pc.gpu_driver_consent_open = false;
                    true
                } else {
                    false
                }
            })
            .child(card),
    )
}

fn driver_install_overlay_layer(s: &AppState, blur: bool) -> Option<El> {
    let install = s.pc.driver_install_overlay.as_ref()?;
    let title = match (install.name.as_deref(), install.version.as_deref()) {
        (Some(n), Some(v)) => format!("{n} {v}"),
        (Some(n), None) => format!("{n} driver"),
        _ => "Graphics driver update".into(),
    };
    let done = install.phase == "done";
    let failed = install.phase == "failed";
    let status_text = match install.phase.as_str() {
        "starting" => "Starting the update".into(),
        "downloading" => {
            if let (Some(sh), Some(sz)) = (install.share, install.size)
                && sz > 0
            {
                let got = (sz as f64 * sh) as u64;
                format!(
                    "Downloading the driver · {} of {}",
                    format_driver_size(got),
                    format_driver_size(sz)
                )
            } else {
                "Downloading the driver".into()
            }
        }
        "unpacking" => "Unpacking the driver".into(),
        "waiting" => "Waiting to install".into(),
        "installing" => "Installing the driver".into(),
        "reloading" => "Reloading the graphics driver".into(),
        "restoring" => "Putting your settings back".into(),
        "done" => "Driver updated".into(),
        "failed" => install
            .message
            .clone()
            .unwrap_or_else(|| "Driver update did not finish.".into()),
        other => other.into(),
    };
    let mut panel = glass_panel_opt("driver-install-overlay", 480.0, blur)
        .role("dialog")
        .label(title.clone())
        .px(24.0)
        .py(22.0)
        .gap(14.0)
        .transition(modal_card_transition())
        .child(text(title).font(type_title()).color(FG))
        .child(
            text(status_text)
                .font(type_body())
                .color(if failed { bad() } else { fg_muted() })
                .wrap(),
        );
    if !done && !failed {
        let share = install.share.unwrap_or(0.5) as f32;
        panel = panel.child(progress_bar(432.0, share));
    }
    if done && !install.restored.is_empty() {
        panel = panel.child(
            text(format!("Restored: {}", install.restored.join(", ")))
                .font(type_small())
                .color(good())
                .wrap(),
        );
    }
    if done && !install.not_restored.is_empty() {
        panel = panel.child(
            text(format!("Not restored: {}", install.not_restored.join(", ")))
                .font(type_small())
                .color(warn())
                .wrap(),
        );
    }
    if done || failed {
        panel = panel.child(
            row().w_full().justify_end().child(
                menu_btn("driver-install-dismiss".into(), "Done", true, false, false)
                    .on_press(|s: &mut AppState| s.dismiss_driver_install_overlay()),
            ),
        );
    }
    Some(
        el().key("driver-install-scrim")
            .w_full()
            .h_full()
            .center()
            .fill(BG.alpha(0.72))
            .transition(modal_scrim_transition())
            .child(panel),
    )
}

pub(crate) fn part_menu_layers(s: &AppState, blur: bool) -> Vec<El> {
    if s.room != Room::ExoAi {
        return Vec::new();
    }
    let mut layers = Vec::new();

    if let Some(which) = s.pc.part_menu {
        let (anchor_key, title) = match which {
            PcPartMenu::Graphics => ("pc-cell-graphics", "Graphics"),
            PcPartMenu::Display => ("pc-cell-display", "Display"),
            PcPartMenu::Board => ("pc-cell-board", "Motherboard"),
        };
        let close_label = match which {
            PcPartMenu::Graphics => "Close Graphics",
            PcPartMenu::Display => "Close Display",
            PcPartMenu::Board => "Close Motherboard",
        };
        let body_inner = match which {
            PcPartMenu::Graphics => graphics_menu_body(s),
            PcPartMenu::Display => display_menu_body(s),
            PcPartMenu::Board => board_menu_body(s),
        };
        let head = row()
            .w_full()
            .pt(14.0)
            .px(20.0)
            .pb(10.0)
            .justify_between()
            .center_y()
            .child(text(title).font(type_section()).color(FG))
            .child(
                el().key("pc-part-menu-close")
                    .role("button")
                    .label(close_label)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .cursor(Cursor::Pointer)
                    .size(28.0, 28.0)
                    .radius(14.0)
                    .center()
                    .hover(hover_fill())
                    .press(pressed_fill())
                    .motion(SPRING_QUICK)
                    .on_press(|s: &mut AppState| {
                        s.pc.part_menu = None;
                        s.pc.armed_confirm = None;
                    })
                    .child(icon_close(15.0, FG)),
            );
        let body = col()
            .key("pc-part-menu-scroll")
            .w_full()
            .max_h(620.0)
            .px(20.0)
            .pb(20.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .clip_fade_linear(0.0, 0.0, 0.0, 24.0)
            .child(body_inner);
        let mut card = col()
            .key("pc-part-menu")
            .w(580.0)
            .radius(RADIUS_PANEL)
            .continuous_corners()
            .clip_rounded(RADIUS_PANEL)
            .hit()
            .cursor(Cursor::Default);
        if blur {
            card = card.liquid_glass(exo_kit::exo_glass(exo_kit::ExoGlass::Sheet, false));
        } else {
            card = card
                .fill(exo_kit::surface_elevated())
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
        }
        let card = card
            .role("dialog")
            .label(title)
            .transition(popover_transition_from(rune::Vec2::new(0.5, 1.0)))
            .modal(true, |s: &mut AppState| {
                s.pc.part_menu = None;
                s.pc.armed_confirm = None;
            })
            .modal_scrim(exo_kit::scrim_fill())
            .modal_live(["top-bar"])
            .on_key(|s: &mut AppState, ev, _| {
                if ev.key == Key::Escape {
                    if s.pc.armed_confirm.is_some() {
                        s.pc.armed_confirm = None;
                    } else {
                        s.pc.part_menu = None;
                    }
                    true
                } else {
                    false
                }
            })
            .child(head)
            .child(body);
        let _ = anchor_key;
        layers.push(
            col()
                .key("pc-part-menu-host")
                .pin(Some(0.0), Some(exo_kit::TITLEBAR_HEIGHT + 16.0), Some(0.0), Some(20.0))
                .px(24.0)
                .center()
                .child(card),
        );
    }

    if let Some(cine) = auto_tune_cinema_layer(s, blur) {
        layers.push(cine);
    }
    if let Some(consent) = driver_consent_layer(s, blur) {
        layers.push(consent);
    }
    if let Some(overlay) = driver_install_overlay_layer(s, blur) {
        layers.push(overlay);
    }

    layers
}
