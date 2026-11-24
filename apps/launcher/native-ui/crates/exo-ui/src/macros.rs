use crate::state::AppState;
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, SPRING_QUICK,
    TRANSPARENT, amoled_card, fg_muted, float_line, glass_button, glass_rim, good, hover_fill,
    icon_check, icon_chevron_left, icon_gamepad, icon_keyboard, icon_mouse, icon_play, icon_shield,
    icon_stop, icon_zap, pressed_fill, primary_button, type_body, type_control, type_section,
    type_small, type_title, warn,
};
use exo_macros::model::{DeviceKind, MacroAction, MacroDef, MacroDelivery, MacroProfile};
use exo_macros::policy::{AntiCheatClass, MacroGamePolicy};
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
    let cp = &s.pc.macros;
    let selected_profile = cp
        .doc
        .profiles
        .iter()
        .find(|p| p.id == s.pc.macros_selected_profile)
        .or_else(|| cp.doc.profiles.first());

    let selected_binding = selected_profile.and_then(|p| {
        s.pc.macros_selected_binding
            .as_deref()
            .and_then(|id| p.bindings.iter().find(|b| b.id == id))
            .or_else(|| p.bindings.first())
    });

    let library_policies: Vec<MacroGamePolicy> = s
        .games
        .iter()
        .filter(|g| g.installed)
        .take(8)
        .map(|g| cp.policy_for_game(&g.id, &g.title, g.anti_cheat_class, None))
        .collect();

    let worker_status = if cp.should_spawn_worker() {
        if cp.requires_ll_hooks() {
            "Sidecar active · LL hook + Raw Input"
        } else {
            "Sidecar active · Raw Input only (0 hooks)"
        }
    } else {
        "Sidecar idle · 0 MB RAM · 0 hooks"
    };

    let latency_label = if cp.doc.latency.samples > 0 {
        format!(
            "P50 {:.1} us · P99 {:.1} us",
            cp.doc.latency.p50_us, cp.doc.latency.p99_us
        )
    } else {
        "Target < 500 us".to_string()
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
                .child(text("Exo Macros & Input Hub").font(type_title()).color(FG)),
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
                        .child(text(worker_status).font(type_small()).color(good())),
                )
                .child(
                    el().px(10.0)
                        .py(6.0)
                        .radius(RADIUS_PILL)
                        .border(1.0, float_line())
                        .child(text(latency_label).font(type_small()).color(fg_muted())),
                )
                .child(
                    glass_button(
                        "macros-engine-toggle",
                        if cp.doc.enabled {
                            "Engine on"
                        } else {
                            "Engine off"
                        },
                        Some(icon_zap(
                            14.0,
                            if cp.doc.enabled { good() } else { fg_muted() },
                        )),
                    )
                    .on_press(|s: &mut AppState| {
                        s.pc.macros.doc.enabled = !s.pc.macros.doc.enabled;
                    }),
                ),
        );

    let rail = col()
        .w(320.0)
        .gap(12.0)
        .child(profiles_panel(s, selected_profile))
        .child(controller_hub_panel(s))
        .child(anticheat_panel(&library_policies));

    let main_col = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(12.0)
        .children(selected_profile.map(|p| bindings_panel(p, selected_binding)))
        .children(selected_binding.map(|b| {
            let pid = selected_profile.map(|p| p.id.clone()).unwrap_or_default();
            timeline_panel(cp.recording, pid, b)
        }));

    col()
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
            text("Online, competitive anti-cheat, and unknown games allow Tier 1 onboard hardware macros & Exo IPC only. Offline games keep Tier 2 software macros with a one-time note.")
                .font(type_small())
                .color(fg_muted()),
        )
        .child(row().w_full().gap(16.0).child(rail).child(main_col))
}

fn profiles_panel(s: &AppState, selected: Option<&MacroProfile>) -> El {
    let cp = &s.pc.macros;
    let mut list = col().w_full().gap(4.0);
    for p in &cp.doc.profiles {
        let is_selected = selected.is_some_and(|cur| cur.id == p.id);
        let is_active = cp.doc.active_profile_id == p.id;
        let pid = p.id.clone();
        let mut item = row()
            .key(format!("macros-profile-{}", p.id))
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
                s.pc.macros.doc.active_profile_id = pid.clone();
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

fn controller_hub_panel(s: &AppState) -> El {
    let probe = &s.pc.controller_hub.probe;
    let backend = if probe.runtime_available {
        format!("{} · sub-ms callbacks", probe.backend)
    } else {
        probe.backend.clone()
    };

    amoled_card(RADIUS_PANEL)
        .w_full()
        .px(16.0)
        .py(14.0)
        .gap(8.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Controller Hub").font(type_section()).color(FG))
                .child(icon_gamepad(14.0, good())),
        )
        .child(text(backend).font(type_small()).color(FG))
        .child(
            text("DualSense Edge & Xbox Elite onboard profiles, Steam Input auto-fix, and stick-drift warning (>4%).")
                .font(type_small())
                .color(fg_muted()),
        )
}

fn anticheat_panel(policies: &[MacroGamePolicy]) -> El {
    let mut list = col().w_full().gap(8.0);
    if policies.is_empty() {
        list = list.child(
            text("No installed library games loaded from host yet.")
                .font(type_small())
                .color(fg_muted()),
        );
    }
    for gp in policies.iter().take(6) {
        let gid = gp.game_id.clone();
        let class = gp.class;
        let software_allowed = gp.software_macros_allowed;
        let needs_note = gp.requires_offline_note && !gp.offline_note_acknowledged;
        let subtitle = match class {
            AntiCheatClass::CompetitivePvpOnline => "Competitive PvP · hardware onboard only",
            AntiCheatClass::OnlineNonCompetitivePve => "Online PvE · hardware onboard only",
            AntiCheatClass::Unknown => "Unknown (treated as online) · hardware onboard only",
            AntiCheatClass::OfflineAntiCheat => {
                if gp.offline_note_acknowledged {
                    "Offline · Tier 1 + Tier 2 enabled"
                } else {
                    "Offline · Tier 2 available (one-time note)"
                }
            }
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
                        let _ = s.pc.macros.acknowledge_offline_note(&gid, class);
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
        .child(list)
}

fn bindings_panel(profile: &MacroProfile, selected: Option<&MacroDef>) -> El {
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
                if let Some(p) = s.pc.macros.doc.profiles.iter_mut().find(|x| x.id == pid) {
                    p.active_layer_id = lid.clone();
                }
            }),
        );
    }

    let mut list = col().w_full().gap(6.0);
    for b in &profile.bindings {
        let is_selected = selected.is_some_and(|cur| cur.id == b.id);
        let bid = b.id.clone();
        let chord = b
            .trigger
            .chord
            .iter()
            .map(|c| c.code.as_str())
            .collect::<Vec<_>>()
            .join(" + ");
        let icon = match b.trigger.chord.first().map(|c| c.device_kind) {
            Some(DeviceKind::Mouse) => icon_mouse(14.0, fg_muted()),
            Some(DeviceKind::Controller) => icon_gamepad(14.0, fg_muted()),
            _ => icon_keyboard(14.0, fg_muted()),
        };
        let (delivery_label, delivery_color) = match b.delivery {
            MacroDelivery::OnboardHardware => ("Tier 1 hardware", good()),
            MacroDelivery::ExoInternal => ("Exo IPC (AC-safe)", good()),
            MacroDelivery::SoftwareSidecar => ("Tier 2 offline only", warn()),
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
                        text(delivery_label)
                            .font(type_small())
                            .color(delivery_color),
                    )
                    .child(text(chord).font(type_small()).color(fg_muted())),
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

fn action_label(action: &MacroAction) -> String {
    match action {
        MacroAction::KeyDown { code } => format!("keyDown · {code}"),
        MacroAction::KeyUp { code } => format!("keyUp · {code}"),
        MacroAction::MouseDown { button } => format!("mouseDown · {button}"),
        MacroAction::MouseUp { button } => format!("mouseUp · {button}"),
        MacroAction::MouseMove { dx, dy } => format!("mouseMove · ({dx}, {dy})"),
        MacroAction::MouseWheel { delta } => format!("mouseWheel · {delta}"),
        MacroAction::PadButtonDown { button } => format!("padDown · {button}"),
        MacroAction::PadButtonUp { button } => format!("padUp · {button}"),
        MacroAction::PadAxis { axis, value } => format!("padAxis · {axis}={value:.2}"),
        MacroAction::Delay => "delay".to_string(),
        MacroAction::Text { text } => format!("text · {text}"),
        MacroAction::LaunchApp { path } => format!("launch · {path}"),
        MacroAction::LayerSwitch { layer_id } => format!("layer · {layer_id}"),
        MacroAction::ExoInternal { command } => format!("exoIpc · {command}"),
    }
}

fn timeline_panel(recording: bool, profile_id: String, binding: &MacroDef) -> El {
    let mut steps = col().w_full().gap(6.0);
    for step in &binding.steps {
        steps = steps.child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .px(12.0)
                .py(8.0)
                .radius(RADIUS_CARD)
                .border(1.0, float_line())
                .child(text(action_label(&step.action)).font(type_body()).color(FG))
                .child(
                    text(format!("+{:.1} ms", step.delay_ms))
                        .font(type_small())
                        .color(fg_muted()),
                ),
        );
    }

    let bid = binding.id.clone();
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
                                s.pc.macros.recording = !s.pc.macros.recording;
                            }),
                        )
                        .child(
                            primary_button("macros-test", "Test timing", Some(icon_play(14.0, BG)))
                                .on_press(move |s: &mut AppState| {
                                    if let Some(rep) = s.pc.macros.dry_run(&profile_id, &bid) {
                                        s.notice = Some(format!(
                                            "Dry-run timing: P50 {:.1} us · P99 {:.1} us",
                                            rep.latency.p50_us, rep.latency.p99_us
                                        ));
                                    }
                                }),
                        ),
                ),
        )
        .child(steps)
}

#[cfg(test)]
mod tests {
    use crate::shell;
    use crate::state::{AppState, Room};
    use exo_rpc::types::Game;
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Runtime};

    #[test]
    fn macros_hub_reads_rpc_library_classes_enforces_blur_cap_and_online_hardware_only() {
        let mut state = AppState::default();
        state.games = serde_json::from_str::<Vec<Game>>(
            r#"[
                {"id":"riot:valorant","title":"VALORANT","installed":true,"antiCheatClass":"competitivePvpOnline"},
                {"id":"steam:3513350","title":"Wuthering Waves","installed":true,"antiCheatClass":"onlineNonCompetitivePve"},
                {"id":"local:unknown","title":"Unknown Game","installed":true,"antiCheatClass":"unknown"},
                {"id":"local:missing","title":"Missing Field","installed":true},
                {"id":"steam:1245620","title":"ELDEN RING","installed":true,"antiCheatClass":"offlineAntiCheat"}
            ]"#,
        )
        .expect("rpc library fixture");
        state.switch_room(Room::Devices);
        assert!(!state.pc.macros.should_spawn_worker());

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

        assert!(rt.find("macros-ack-riot:valorant").is_none());
        assert!(rt.find("macros-ack-steam:3513350").is_none());
        assert!(rt.find("macros-ack-local:unknown").is_none());
        assert!(rt.find("macros-ack-local:missing").is_none());

        let (ax, ay) = rt
            .find("macros-ack-steam:1245620")
            .expect("one-time note button for offline ELDEN RING");
        rt.pointer_down(ax, ay);
        rt.pointer_up(ax, ay);
        rt.frame(1.0 / 60.0);

        assert!(
            rt.state
                .pc
                .macros
                .doc
                .offline_note_acked
                .contains("steam:1245620")
        );

        let (tx, ty) = rt.find("macros-test").expect("macros-test button");
        rt.pointer_down(tx, ty);
        rt.pointer_up(tx, ty);
        rt.frame(1.0 / 60.0);
        assert!(rt.state.pc.macros.doc.latency.samples > 0);
    }
}
