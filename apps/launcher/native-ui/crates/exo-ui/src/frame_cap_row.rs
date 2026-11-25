use crate::events::HostEventKind;
use crate::state::AppState;
use exo_kit::{
    ExoGlass, FG, PRESS_SCALE, RADIUS_CARD, RADIUS_PILL, SPRING_QUICK, apply_glass, bad, fg_muted,
    float_line, glass_control_fill, glass_rim_bottom, glass_rim_top, good, hairline, hover_fill,
    icon_check, icon_chevron_down, pressed_fill, type_control, type_section, type_small,
};
use exo_rpc::{Game, GameFrameCap, HostEvent};
use rune::{El, col, el, row, text};

fn curly(text: &str) -> String {
    text.replace('\'', "\u{2019}")
}

pub fn register_events(state: &mut AppState) {
    state.register_event(HostEventKind::GameCapLab, |s, ev| {
        let HostEvent::GameCapLab(ab) = ev else {
            return false;
        };
        let Some(ref mut fc) = s.game_frame_cap else {
            return false;
        };
        if !ab.game_id.is_empty() && !fc.game_id.eq_ignore_ascii_case(&ab.game_id) {
            return false;
        }
        fc.ab = *ab;
        true
    });
}

pub fn trigger_label(fc: Option<&GameFrameCap>) -> String {
    let Some(fc) = fc else {
        return "Off".into();
    };
    if fc.ab.status == "running" {
        return format!("A/B · {}/{}s", fc.ab.elapsed_seconds, fc.ab.total_seconds);
    }
    if fc.selected_tier == "uncapped" {
        return "Uncapped".into();
    }
    if let Some(fps) = fc.active_cap_fps
        && fps > 0
    {
        if let Some(tier) = fc.tiers.iter().find(|t| t.id == fc.selected_tier) {
            return format!("{} · {fps} fps", curly(&tier.label));
        }
        return format!("{fps} fps");
    }
    "Off".into()
}

pub fn summary_for(state: Option<&GameFrameCap>, error: Option<&str>) -> (String, rune::Color) {
    if let Some(err) = error
        && !err.is_empty()
    {
        return (curly(err), bad());
    }
    let Some(fc) = state else {
        return ("Checking frame cap...".into(), fg_muted());
    };
    if !fc.ok && fc.tiers.is_empty() {
        let msg = fc
            .message
            .as_deref()
            .filter(|m| !m.is_empty())
            .unwrap_or("Could not check");
        return (curly(msg), bad());
    }
    if fc.ab.status == "running" {
        let arm = fc.ab.current_arm.as_deref().unwrap_or("A");
        return (
            format!(
                "A/B test {} vs {} fps · Arm {arm}",
                fc.ab.cap_a_fps, fc.ab.cap_b_fps
            ),
            fg_muted(),
        );
    }
    if fc.ab.status == "completed"
        && let Some(ref verdict) = fc.ab.verdict
        && !verdict.is_empty()
    {
        let word = if fc.word.is_empty() {
            verdict.clone()
        } else {
            format!("{} · {verdict}", fc.word)
        };
        let color = if fc.good || matches!(fc.ab.winner.as_deref(), Some("a" | "b")) {
            good()
        } else {
            fg_muted()
        };
        return (curly(&word), color);
    }
    let word = if fc.word.is_empty() {
        "Off"
    } else {
        fc.word.as_str()
    };
    let color = if fc.bad {
        bad()
    } else if fc.good {
        good()
    } else {
        fg_muted()
    };
    (curly(word), color)
}

pub fn view(s: &AppState, game: &Game) -> El {
    let fc = s.game_frame_cap.as_ref();
    let (sub_text, sub_color) = summary_for(fc, s.frame_cap_error.as_deref());
    let has_menu = fc.is_some_and(|d| !d.tiers.is_empty() || !d.backends.is_empty());

    let right_control = if has_menu {
        let label = trigger_label(fc);
        let pill = row()
            .key("gp-fc-menu-btn")
            .role("button")
            .label(format!("Frame cap, {label}"))
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(32.0)
            .px(12.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_control_fill())
            .hover(hover_fill())
            .press(pressed_fill())
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .motion(SPRING_QUICK)
            .on_press(|s: &mut AppState| {
                if !s.frame_cap_busy {
                    s.frame_cap_menu_open = !s.frame_cap_menu_open;
                }
            })
            .child(text(label).font(type_control()).color(FG))
            .child(icon_chevron_down(12.0, fg_muted()));
        Some(pill)
    } else {
        None
    };

    let left = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(3.0)
        .child(text("Frame cap").font(type_section()).color(FG))
        .child(
            text(sub_text)
                .key("gp-fc-word")
                .font(type_small())
                .color(sub_color)
                .truncate(),
        );

    let main_row = row()
        .w_full()
        .min_h(44.0)
        .py(6.0)
        .gap(12.0)
        .justify_between()
        .center_y()
        .child(left)
        .children(right_control);

    let mut container = col()
        .key("gp-fc-row")
        .w_full()
        .py(6.0)
        .px(20.0)
        .border_sides([1.0, 0.0, 0.0, 0.0], float_line())
        .child(main_row);

    if s.frame_cap_menu_open
        && let Some(fc) = fc
    {
        container = container.child(build_menu(fc, game));
    }

    container
}

fn section_heading(title: &'static str) -> El {
    el().w_full()
        .px(12.0)
        .pt(6.0)
        .pb(2.0)
        .child(text(title).font(type_small()).color(fg_muted()))
}

fn build_menu(fc: &GameFrameCap, game: &Game) -> El {
    let mut menu_col = col().w_full().py(6.0).gap(2.0);
    let game_id = game.id.clone();

    menu_col = menu_col.child(section_heading("Cap tiers"));

    for tier in &fc.tiers {
        let is_selected = fc.selected_tier == tier.id;
        let is_suggested = fc.suggestion.tier == tier.id;
        let label = if is_suggested {
            format!("{} (Suggested)", curly(&tier.label))
        } else {
            curly(&tier.label)
        };
        let hint = match (tier.cap_fps, tier.output_fps, fc.fg_multiplier > 1) {
            (Some(base), Some(out), true) => {
                format!(
                    "{base} base / {out} out · ±{:.1} ms",
                    tier.predicted_std_dev_ms
                )
            }
            (Some(fps), _, _) => format!("{fps} fps · ±{:.1} ms", tier.predicted_std_dev_ms),
            _ => format!("±{:.1} ms", tier.predicted_std_dev_ms),
        };
        let mut item = row()
            .key(format!("gp-fc-tier-{}", tier.id))
            .w_full()
            .min_h(34.0)
            .px(12.0)
            .py(6.0)
            .gap(8.0)
            .center_y()
            .justify_between()
            .radius(8.0)
            .child(
                row()
                    .gap(8.0)
                    .center_y()
                    .grow(1.0)
                    .min_w(0.0)
                    .children(is_selected.then(|| icon_check(13.0, good())))
                    .child(
                        text(label.clone())
                            .font(type_control())
                            .color(if fc.backend_available { FG } else { fg_muted() })
                            .truncate(),
                    ),
            )
            .child(text(hint).font(type_small()).color(fg_muted()));

        if fc.backend_available {
            let gid = game_id.clone();
            let tid = tier.id.clone();
            item = item
                .role("button")
                .label(label)
                .focusable(true)
                .hit()
                .press_on_down()
                .hover(hover_fill())
                .press(pressed_fill())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    s.frame_cap_menu_open = false;
                    s.set_game_frame_cap(&gid, Some(&tid), None, None);
                });
        }
        menu_col = menu_col.child(item);
    }

    if !fc.in_vrr_range {
        for &fps in &fc.refresh_locked_caps {
            if fc.tiers.iter().any(|t| t.cap_fps == Some(fps)) {
                continue;
            }
            let is_selected = fc.selected_tier == "refresh" && fc.active_cap_fps == Some(fps);
            let label = format!("Refresh lock · {fps} fps");
            let hint = format!("{} Hz divisor", fc.refresh_hz.round() as i32);
            let mut item = row()
                .key(format!("gp-fc-refresh-{fps}"))
                .w_full()
                .min_h(34.0)
                .px(12.0)
                .py(6.0)
                .gap(8.0)
                .center_y()
                .justify_between()
                .radius(8.0)
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .grow(1.0)
                        .min_w(0.0)
                        .children(is_selected.then(|| icon_check(13.0, good())))
                        .child(
                            text(label.clone())
                                .font(type_control())
                                .color(if fc.backend_available { FG } else { fg_muted() })
                                .truncate(),
                        ),
                )
                .child(text(hint).font(type_small()).color(fg_muted()));
            if fc.backend_available {
                let gid = game_id.clone();
                item = item
                    .role("button")
                    .label(label)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .hover(hover_fill())
                    .press(pressed_fill())
                    .motion(SPRING_QUICK)
                    .on_press(move |s: &mut AppState| {
                        s.frame_cap_menu_open = false;
                        s.set_game_frame_cap(&gid, Some("refresh"), Some(fps), None);
                    });
            }
            menu_col = menu_col.child(item);
        }
    }

    if !fc.in_vrr_range
        && let Some(ref uncapped) = fc.uncapped_option
    {
        let is_selected = fc.selected_tier == "uncapped";
        let gid = game_id.clone();
        let label = curly(&uncapped.label);
        let uncapped_row = row()
            .key("gp-fc-tier-uncapped")
            .role("button")
            .label(label.clone())
            .focusable(true)
            .hit()
            .press_on_down()
            .w_full()
            .min_h(34.0)
            .px(12.0)
            .py(6.0)
            .gap(8.0)
            .center_y()
            .justify_between()
            .radius(8.0)
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| {
                s.frame_cap_menu_open = false;
                s.set_game_frame_cap(&gid, Some("uncapped"), None, None);
            })
            .child(
                row()
                    .gap(8.0)
                    .center_y()
                    .children(is_selected.then(|| icon_check(13.0, good())))
                    .child(text(label).font(type_control()).color(FG)),
            )
            .child(text("Lowest lag").font(type_small()).color(fg_muted()));
        menu_col = menu_col.child(uncapped_row);
    }

    let gid_off = game_id.clone();
    let off_selected = fc.selected_tier == "off" || fc.selected_tier.is_empty();
    let off_row = row()
        .key("gp-fc-tier-off")
        .role("button")
        .label("Off")
        .focusable(true)
        .hit()
        .press_on_down()
        .w_full()
        .min_h(34.0)
        .px(12.0)
        .py(6.0)
        .gap(8.0)
        .center_y()
        .radius(8.0)
        .hover(hover_fill())
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| {
            s.frame_cap_menu_open = false;
            s.set_game_frame_cap(&gid_off, Some("off"), None, None);
        })
        .children(off_selected.then(|| icon_check(13.0, good())))
        .child(text("Off").font(type_control()).color(FG));
    menu_col = menu_col.child(off_row);

    if !fc.backends.is_empty() {
        menu_col = menu_col
            .child(el().w_full().h(1.0).my(4.0).fill(hairline()))
            .child(section_heading("Limiter"));
        let target_tier = if !fc.selected_tier.is_empty() && fc.selected_tier != "off" {
            fc.selected_tier.clone()
        } else {
            "max".into()
        };
        for b in &fc.backends {
            let is_active = b.id == fc.backend;
            let label = curly(&b.label);
            let hint = if b.available {
                if b.per_game_native {
                    "Per-game"
                } else {
                    "Session"
                }
            } else {
                "Unavailable"
            };
            let mut item = row()
                .key(format!("gp-fc-backend-{}", b.id))
                .w_full()
                .min_h(34.0)
                .px(12.0)
                .py(6.0)
                .gap(8.0)
                .center_y()
                .justify_between()
                .radius(8.0)
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .grow(1.0)
                        .min_w(0.0)
                        .children(is_active.then(|| icon_check(13.0, good())))
                        .child(
                            text(label.clone())
                                .font(type_control())
                                .color(if b.available { FG } else { fg_muted() })
                                .truncate(),
                        ),
                )
                .child(text(hint).font(type_small()).color(fg_muted()));
            if b.available {
                let gid = game_id.clone();
                let bid = b.id.clone();
                let tier_for_b = target_tier.clone();
                item = item
                    .role("button")
                    .label(label)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .hover(hover_fill())
                    .press(pressed_fill())
                    .motion(SPRING_QUICK)
                    .on_press(move |s: &mut AppState| {
                        s.frame_cap_menu_open = false;
                        s.set_game_frame_cap(&gid, Some(&tier_for_b), None, Some(&bid));
                    });
            }
            menu_col = menu_col.child(item);
        }
    }

    let has_ab_items = fc.ab.status == "running"
        || (fc.backend_available && fc.tiers.len() >= 2)
        || (fc.ab.status == "completed" && fc.ab.verdict.as_deref().is_some_and(|v| !v.is_empty()))
        || (fc.ab.status == "cancelled" && fc.ab.reason.as_deref().is_some_and(|r| !r.is_empty()));
    if has_ab_items {
        menu_col = menu_col
            .child(el().w_full().h(1.0).my(4.0).fill(hairline()))
            .child(section_heading("A/B cap lab"));
    }
    if fc.ab.status == "running" {
        let gid = game_id.clone();
        let cancel_label = format!(
            "Cancel A/B test ({}/{}s)",
            fc.ab.elapsed_seconds, fc.ab.total_seconds
        );
        let cancel_row = row()
            .key("gp-fc-ab-cancel")
            .role("button")
            .label(cancel_label.clone())
            .focusable(true)
            .hit()
            .press_on_down()
            .w_full()
            .min_h(34.0)
            .px(12.0)
            .py(6.0)
            .center_y()
            .radius(8.0)
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| {
                s.frame_cap_menu_open = false;
                s.cancel_cap_lab(&gid);
            })
            .child(text(cancel_label).font(type_control()).color(bad()));
        menu_col = menu_col.child(cancel_row);
    } else if fc.backend_available && fc.tiers.len() >= 2 {
        let cap_a = fc
            .active_cap_fps
            .or_else(|| fc.tiers.last().and_then(|t| t.cap_fps))
            .unwrap_or(138);
        let cap_b = fc
            .tiers
            .iter()
            .find(|t| t.id == "balanced" && t.cap_fps != Some(cap_a))
            .and_then(|t| t.cap_fps)
            .or_else(|| {
                fc.tiers
                    .iter()
                    .find(|t| t.id == "smooth" && t.cap_fps != Some(cap_a))
                    .and_then(|t| t.cap_fps)
            })
            .unwrap_or((cap_a - 12).max(30));
        let gid = game_id.clone();
        let run_label = format!("Run 20 s A/B test ({cap_a} vs {cap_b} fps)");
        let run_row = row()
            .key("gp-fc-ab-run")
            .role("button")
            .label(run_label.clone())
            .focusable(true)
            .hit()
            .press_on_down()
            .w_full()
            .min_h(34.0)
            .px(12.0)
            .py(6.0)
            .gap(8.0)
            .center_y()
            .justify_between()
            .radius(8.0)
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| {
                s.frame_cap_menu_open = false;
                s.run_cap_lab(&gid, cap_a, cap_b);
            })
            .child(text(run_label).font(type_control()).color(FG))
            .child(text("5 s blocks").font(type_small()).color(fg_muted()));
        menu_col = menu_col.child(run_row);
    }

    if fc.ab.status == "completed"
        && let Some(ref verdict) = fc.ab.verdict
        && !verdict.is_empty()
    {
        menu_col = menu_col.child(
            el().px(12.0)
                .py(3.0)
                .child(text(curly(verdict)).font(type_small()).color(fg_muted())),
        );
    } else if fc.ab.status == "cancelled"
        && let Some(ref reason) = fc.ab.reason
        && !reason.is_empty()
    {
        menu_col = menu_col.child(
            el().px(12.0)
                .py(3.0)
                .child(text(curly(reason)).font(type_small()).color(fg_muted())),
        );
    }

    if !fc.suggestion.reason.is_empty() {
        menu_col = menu_col
            .child(el().w_full().h(1.0).my(4.0).fill(hairline()))
            .child(section_heading("Exo AI suggestion"))
            .child(
                el().px(12.0).py(3.0).child(
                    text(curly(&fc.suggestion.reason))
                        .font(type_small())
                        .color(fg_muted()),
                ),
            );
    }
    if !fc.stutter.summary.is_empty() {
        menu_col = menu_col
            .child(section_heading("Stutter attribution"))
            .child(
                el().px(12.0).py(3.0).child(
                    text(curly(&fc.stutter.summary))
                        .font(type_small())
                        .color(fg_muted()),
                ),
            );
    }
    if !fc.backend_available
        && let Some(ref note) = fc.backend_note
        && !note.is_empty()
    {
        menu_col = menu_col.child(section_heading("Limiter note")).child(
            el().px(12.0)
                .py(3.0)
                .child(text(curly(note)).font(type_small()).color(fg_muted())),
        );
    }

    apply_glass(
        el().key("gp-fc-menu")
            .w_full()
            .mt(6.0)
            .px(4.0)
            .py(4.0)
            .radius(RADIUS_CARD)
            .continuous_corners()
            .clip_rounded(RADIUS_CARD)
            .child(menu_col),
        ExoGlass::Menu,
        false,
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::{CapLabStatus, FrameCapTier};

    #[test]
    fn register_events_updates_ab_status_for_open_game() {
        let mut s = AppState::with_demo_games();
        let game_id = s
            .game_frame_cap
            .as_ref()
            .map(|fc| fc.game_id.clone())
            .unwrap_or_else(|| "steam:1091500".into());

        let redrew = s.apply_host_event(HostEvent::GameCapLab(Box::new(CapLabStatus {
            game_id: game_id.clone(),
            status: "completed".into(),
            cap_a_fps: 138,
            cap_b_fps: 120,
            elapsed_seconds: 20,
            total_seconds: 20,
            verdict: Some("B is smoother by 11.5%".into()),
            winner: Some("b".into()),
            delta_percent: 11.5,
            noise_threshold_percent: 3.0,
            ..Default::default()
        })));

        assert!(redrew);
        let fc = s.game_frame_cap.as_ref().expect("frame cap state");
        assert_eq!(fc.ab.verdict.as_deref(), Some("B is smoother by 11.5%"));
        let (word, _) = summary_for(Some(fc), None);
        assert!(word.contains("B is smoother by 11.5%"));
        let (err_word, err_col) = summary_for(Some(fc), Some("Could not save frame cap."));
        assert_eq!(err_word, "Could not save frame cap.");
        assert_eq!(err_col, bad());
    }

    #[test]
    fn trigger_label_formats_tiers_and_running_ab() {
        let mut fc = GameFrameCap {
            ok: true,
            selected_tier: "max".into(),
            active_cap_fps: Some(138),
            tiers: vec![FrameCapTier {
                id: "max".into(),
                label: "Max (VRR)".into(),
                cap_fps: Some(138),
                ..Default::default()
            }],
            ..Default::default()
        };
        assert_eq!(trigger_label(Some(&fc)), "Max (VRR) · 138 fps");

        fc.ab.status = "running".into();
        fc.ab.elapsed_seconds = 10;
        fc.ab.total_seconds = 20;
        assert_eq!(trigger_label(Some(&fc)), "A/B · 10/20s");
    }
}
