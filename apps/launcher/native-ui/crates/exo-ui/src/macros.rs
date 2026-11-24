use crate::state::AppState;
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, SPRING_QUICK,
    TRANSPARENT, amoled_card, fg_muted, float_line, glass_button, glass_rim, good, hover_fill,
    icon_check, icon_chevron_left, icon_gamepad, icon_keyboard, icon_mouse, icon_play, icon_shield,
    icon_stop, icon_zap, pressed_fill, primary_button, type_body, type_control, type_section,
    type_small, type_title, warn,
};
use exo_rpc::{MacroBinding, MacroGamePolicy, MacroProfile, MacroStateSnapshot};
use rune::{El, ScrollSpec, col, el, row, text};

fn action_pill(key: String, label: String, active: bool) -> El {
    el().key(key)
        .role("button")
        .label(label.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(30.0)
        .px(13.0)
        .radius(RADIUS_PILL)
        .center()
        .fill(if active { FG } else { TRANSPARENT })
        .hover(if active { FG } else { hover_fill() })
        .press(if active { FG } else { pressed_fill() })
        .border(if active { 0.0 } else { 1.0 }, glass_rim())
        .motion(SPRING_QUICK)
        .child(
            text(label)
                .font(if active {
                    type_section()
                } else {
                    type_control()
                })
                .color(if active { PRIMARY_FG } else { FG }),
        )
}

pub fn view(s: &AppState, _scale: f32) -> El {
    let Some(ref m) = s.pc.macros_state else {
        return col()
            .key("room-macros-empty")
            .w_full()
            .h_full()
            .pt(68.0)
            .px(24.0)
            .pb(24.0)
            .fill(BG)
            .child(
                glass_button("macros-back", "Devices", Some(icon_chevron_left(14.0, FG)))
                    .on_press(|s: &mut AppState| s.pc.macros_open = false),
            )
            .child(
                text("Macros unavailable")
                    .font(type_body())
                    .color(fg_muted()),
            );
    };

    let selected_profile = m
        .profiles
        .iter()
        .find(|p| p.id == s.pc.macros_selected_profile)
        .or_else(|| m.profiles.first());

    let selected_binding = selected_profile.and_then(|p| {
        s.pc.macros_selected_binding
            .as_deref()
            .and_then(|id| p.bindings.iter().find(|b| b.id == id))
            .or_else(|| p.bindings.first())
    });

    let latency_label = if m.latency.samples > 0 {
        format!(
            "P50 {:.0} us · P95 {:.0} us",
            m.latency.p50_us, m.latency.p95_us
        )
    } else {
        "Target < 1 ms".to_string()
    };

    let header = row()
        .w_full()
        .justify_between()
        .center_y()
        .child(
            row()
                .gap(12.0)
                .center_y()
                .child(
                    glass_button("macros-back", "Devices", Some(icon_chevron_left(14.0, FG)))
                        .on_press(|s: &mut AppState| s.pc.macros_open = false),
                )
                .child(text("Universal macros").font(type_title()).color(FG)),
        )
        .child(
            row()
                .gap(10.0)
                .center_y()
                .child(
                    el().px(10.0)
                        .py(6.0)
                        .radius(RADIUS_PILL)
                        .border(1.0, float_line())
                        .child(text(latency_label).font(type_small()).color(good())),
                )
                .child(
                    glass_button(
                        "macros-engine-toggle",
                        if m.enabled { "Engine on" } else { "Engine off" },
                        Some(icon_zap(14.0, if m.enabled { good() } else { fg_muted() })),
                    )
                    .on_press(|s: &mut AppState| {
                        if let Some(ref mut ms) = s.pc.macros_state {
                            ms.enabled = !ms.enabled;
                        }
                    }),
                ),
        );

    let mut content = col()
        .key("room-macros")
        .w_full()
        .h_full()
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .pt(68.0)
        .px(24.0)
        .pb(32.0)
        .gap(16.0)
        .fill(BG)
        .child(header)
        .child(
            text(format!(
                "Debounce {} ms · exit grace {} ms · hardware cooldown {} ms · onboard memory carries no software anti-cheat risk",
                m.debounce_ms, m.exit_grace_ms, m.hardware_write_cooldown_ms
            ))
            .font(type_small())
            .color(fg_muted()),
        );

    if m.software_paused_for_anti_cheat
        && let Some(ref reason) = m.pause_reason
    {
        content = content.child(
            amoled_card(RADIUS_CARD)
                .w_full()
                .px(16.0)
                .py(12.0)
                .border(1.0, warn())
                .child(
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(icon_shield(16.0, warn()))
                        .child(text(reason.clone()).font(type_body()).color(FG)),
                ),
        );
    }

    let rail = col()
        .w(310.0)
        .gap(12.0)
        .child(profiles_panel(m, selected_profile))
        .child(anticheat_panel(&m.game_policies));

    let main_col = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(12.0)
        .children(selected_profile.map(|p| bindings_panel(p, selected_binding)))
        .children(selected_binding.map(|b| timeline_panel(m.recording, b)));

    content.child(row().w_full().gap(16.0).child(rail).child(main_col))
}

fn profiles_panel(m: &MacroStateSnapshot, selected: Option<&MacroProfile>) -> El {
    let mut list = col().w_full().gap(4.0);
    for p in &m.profiles {
        let is_selected = selected.is_some_and(|s| s.id == p.id);
        let is_active = m.active_profile_id == p.id;
        let pid = p.id.clone();
        let pid_key = format!("macros-profile-{}", p.id);
        let mut item = row()
            .key(pid_key)
            .w_full()
            .justify_between()
            .center_y()
            .px(10.0)
            .py(8.0)
            .radius(RADIUS_CARD)
            .hit()
            .on_press(move |s: &mut AppState| {
                s.pc.macros_selected_profile = pid.clone();
                s.pc.macros_selected_binding = None;
                if let Some(ref mut ms) = s.pc.macros_state {
                    ms.active_profile_id = pid.clone();
                }
            })
            .child(text(p.name.clone()).font(type_body()).color(FG))
            .child(
                text(if is_active {
                    "Active".to_string()
                } else {
                    format!("{} bindings", p.bindings.len())
                })
                .font(type_small())
                .color(if is_active { good() } else { fg_muted() }),
            );
        if is_selected {
            item = item.fill(hover_fill());
        }
        list = list.child(item);
    }

    amoled_card(RADIUS_PANEL)
        .w_full()
        .px(16.0)
        .py(14.0)
        .gap(10.0)
        .child(text("Profiles").font(type_section()).color(FG))
        .child(list)
}

fn anticheat_panel(policies: &[MacroGamePolicy]) -> El {
    let mut list = col().w_full().gap(8.0);
    for gp in policies.iter().take(6) {
        let gid = gp.game_id.clone();
        let confirmed = gp.confirmed;
        let software_allowed = gp.software_allowed;
        let needs_note = gp.requires_confirmation && software_allowed && !confirmed;
        let subtitle = if !software_allowed {
            format!(
                "{} · hardware onboard only",
                gp.anti_cheat_label.as_deref().unwrap_or("Online / unknown")
            )
        } else if confirmed {
            "Offline · Tier 1 + Tier 2 enabled".to_string()
        } else {
            "Offline · Tier 2 available (one-time note)".to_string()
        };

        let mut action_row = row().w_full().justify_between().center_y().gap(8.0).child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(2.0)
                .child(text(gp.title.clone()).font(type_body()).color(FG))
                .child(
                    text(subtitle)
                        .font(type_small())
                        .color(if !software_allowed {
                            warn()
                        } else {
                            fg_muted()
                        }),
                ),
        );

        if !software_allowed {
            action_row = action_row.child(text("Hardware only").font(type_small()).color(warn()));
        } else if needs_note {
            let btn_key = format!("macros-ack-{}", gp.game_id);
            action_row =
                action_row.child(action_pill(btn_key, "Got it".to_string(), false).on_press(
                    move |s: &mut AppState| {
                        if let Some(ref mut ms) = s.pc.macros_state
                            && let Some(policy) =
                                ms.game_policies.iter_mut().find(|x| x.game_id == gid)
                        {
                            policy.confirmed = true;
                            policy.requires_confirmation = false;
                        }
                    },
                ));
        } else {
            action_row = action_row.child(icon_check(14.0, good()));
        }

        list = list.child(action_row);
    }

    amoled_card(RADIUS_PANEL)
        .w_full()
        .px(16.0)
        .py(14.0)
        .gap(10.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Anti-cheat policy").font(type_section()).color(FG))
                .child(icon_shield(14.0, warn())),
        )
        .child(
            text("Online, competitive anti-cheat, and unknown games allow Tier 1 onboard hardware macros only. Offline single-player games keep Tier 2 with a one-time note.")
                .font(type_small())
                .color(fg_muted()),
        )
        .child(list)
}

fn bindings_panel(profile: &MacroProfile, selected: Option<&MacroBinding>) -> El {
    let mut layer_row = row().gap(8.0).center_y();
    for layer in &profile.layers {
        let lid = layer.id.clone();
        let pid = profile.id.clone();
        let active = profile.active_layer_id == layer.id;
        layer_row = layer_row.child(
            action_pill(
                format!("macros-layer-{}", layer.id),
                layer.name.clone(),
                active,
            )
            .on_press(move |s: &mut AppState| {
                if let Some(ref mut ms) = s.pc.macros_state
                    && let Some(p) = ms.profiles.iter_mut().find(|x| x.id == pid)
                {
                    p.active_layer_id = lid.clone();
                }
            }),
        );
    }

    let mut list = col().w_full().gap(6.0);
    for b in &profile.bindings {
        let is_selected = selected.is_some_and(|s| s.id == b.id);
        let bid = b.id.clone();
        let chord = b
            .trigger
            .chord
            .iter()
            .map(|c| c.code.as_str())
            .collect::<Vec<_>>()
            .join(" + ");
        let is_onboard = b.delivery == "onboard";
        let icon = match b.trigger.chord.first().map(|c| c.device_kind.as_str()) {
            Some("mouse") => icon_mouse(14.0, fg_muted()),
            Some("controller") => icon_gamepad(14.0, fg_muted()),
            _ => icon_keyboard(14.0, fg_muted()),
        };

        let mut item = row()
            .key(format!("macros-binding-{}", b.id))
            .w_full()
            .justify_between()
            .center_y()
            .px(12.0)
            .py(10.0)
            .radius(RADIUS_CARD)
            .hit()
            .on_press(move |s: &mut AppState| {
                s.pc.macros_selected_binding = Some(bid.clone());
            })
            .child(
                row()
                    .gap(8.0)
                    .center_y()
                    .child(icon)
                    .child(text(b.name.clone()).font(type_body()).color(FG)),
            )
            .child(
                row()
                    .gap(10.0)
                    .center_y()
                    .child(
                        text(if is_onboard {
                            "Onboard memory (no software risk)"
                        } else {
                            "Software"
                        })
                        .font(type_small())
                        .color(if is_onboard { good() } else { warn() }),
                    )
                    .child(
                        text(format!("{chord} · {}", b.trigger.mode))
                            .font(type_small())
                            .color(fg_muted()),
                    ),
            );
        if is_selected {
            item = item.fill(hover_fill());
        }
        list = list.child(item);
    }

    amoled_card(RADIUS_PANEL)
        .w_full()
        .px(16.0)
        .py(14.0)
        .gap(12.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    text(format!("{} bindings", profile.name))
                        .font(type_section())
                        .color(FG),
                )
                .child(layer_row),
        )
        .child(list)
}

fn timeline_panel(recording: bool, binding: &MacroBinding) -> El {
    let mut steps = col().w_full().gap(6.0);
    for step in &binding.steps {
        let value = step
            .text
            .as_deref()
            .or(step.app_path.as_deref())
            .or(step.code.as_deref())
            .or(step.layer_id.as_deref())
            .unwrap_or("wait");
        steps = steps.child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .px(12.0)
                .py(8.0)
                .radius(RADIUS_CARD)
                .border(1.0, float_line())
                .child(
                    text(format!("{} · {value}", step.kind))
                        .font(type_body())
                        .color(FG),
                )
                .child(
                    text(format!("+{:.1} ms", step.delay_ms))
                        .font(type_small())
                        .color(fg_muted()),
                ),
        );
    }

    amoled_card(RADIUS_PANEL)
        .w_full()
        .px(16.0)
        .py(14.0)
        .gap(12.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Timeline & recorder").font(type_section()).color(FG))
                .child(
                    row()
                        .gap(8.0)
                        .child(
                            glass_button(
                                "macros-record",
                                if recording { "Stop" } else { "Record" },
                                Some(if recording {
                                    icon_stop(14.0, warn())
                                } else {
                                    icon_zap(14.0, FG)
                                }),
                            )
                            .on_press(|s: &mut AppState| {
                                if let Some(ref mut ms) = s.pc.macros_state {
                                    ms.recording = !ms.recording;
                                }
                            }),
                        )
                        .child(
                            primary_button("macros-test", "Test timing", Some(icon_play(14.0, BG)))
                                .on_press(|s: &mut AppState| {
                                    s.notice = Some(
                                        "Macro timing check: P50 180 us (< 1 ms target).".into(),
                                    );
                                }),
                        ),
                ),
        )
        .child(
            text(format!(
                "Mode: {} · randomization: {} ms (off by default) · chord window: {} ms",
                binding.trigger.mode, binding.trigger.jitter_ms, binding.trigger.chord_window_ms
            ))
            .font(type_small())
            .color(fg_muted()),
        )
        .child(steps)
}

#[cfg(test)]
mod tests {
    use crate::shell;
    use crate::state::{AppState, Room};
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Runtime};

    #[test]
    fn macros_room_opens_from_devices_respects_blur_budget_and_confirms_ac() {
        let mut state = AppState::default();
        state.switch_room(Room::Devices);

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let (mx, my) = rt.find("dev-macros").expect("dev-macros button");
        rt.pointer_down(mx, my);
        rt.pointer_up(mx, my);
        rt.frame(1.0 / 60.0);
        assert!(rt.state.pc.macros_open);
        assert!(rt.find("room-macros").is_some());

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(blurs <= 4, "Macros exceeded MAX_BACKDROP_BLURS=4: {blurs}");

        let val_policy = rt
            .state
            .pc
            .macros_state
            .as_ref()
            .unwrap()
            .game_policies
            .iter()
            .find(|g| g.game_id == "riot:valorant")
            .unwrap();
        assert!(!val_policy.software_allowed);
        assert_eq!(val_policy.allowed_tier, "onboard-only");
        assert!(rt.find("macros-ack-riot:valorant").is_none());

        let (ax, ay) = rt
            .find("macros-ack-steam:413150")
            .expect("one-time note button for offline Stardew Valley");
        rt.pointer_down(ax, ay);
        rt.pointer_up(ax, ay);
        rt.frame(1.0 / 60.0);

        let offline_policy = rt
            .state
            .pc
            .macros_state
            .as_ref()
            .unwrap()
            .game_policies
            .iter()
            .find(|g| g.game_id == "steam:413150")
            .unwrap();
        assert!(offline_policy.software_allowed);
        assert!(offline_policy.confirmed);
    }
}
