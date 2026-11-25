use crate::game_actions::{
    LineWord, SHADERS_RELEASES_URL, frame_gen_word, optimize_word, shader_word, trim_stop,
    upscaler_line_word, upscaler_view,
};
use crate::state::{AppState, InputMode, OpenDropdown};
use exo_kit::{
    FG, PRESS_SCALE, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK,
    SelectTrigger, TRANSPARENT, bad, fg_faint, fg_muted, float_line, glass_rim_bottom,
    glass_rim_top, glass_still_control_fill, good, hex, hover_fill, icon_chevron_right,
    icon_external, pressed_fill, select_trigger, switch_pill, switch_pill_keyed, type_control,
    type_section, type_small, type_title,
};
use exo_rpc::{Game, GraphicsFeatureRow};
use rune::{Cursor, El, LiquidGlass, Transition, col, el, row, text};

fn panel_secondary() -> rune::Color {
    hex(0xdcdcdc, 1.0)
}

fn game_glass_card() -> El {
    col()
        .w_full()
        .min_w(0.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .liquid_glass(LiquidGlass::panel())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
}

fn word_color(word: &LineWord) -> rune::Color {
    if word.bad {
        bad()
    } else if word.good {
        good()
    } else {
        panel_secondary()
    }
}

fn word_text(word: &LineWord) -> String {
    if word.working {
        format!("{}…", word.text)
    } else {
        word.text.clone()
    }
}

fn perf_reading_row(name: &str, word: Option<LineWord>, right: Option<El>) -> El {
    let mut copy = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(3.0)
        .child(text(name.to_string()).font(type_section()).color(FG));
    if let Some(w) = word {
        let txt = word_text(&w);
        copy = copy.child(
            text(txt.clone())
                .key(format!("{name}-word-{txt}"))
                .font(type_small())
                .color(word_color(&w))
                .truncate()
                .transition(Transition::opacity()),
        );
    }
    el().w_full()
        .py(6.0)
        .px(20.0)
        .border_sides([1.0, 0.0, 0.0, 0.0], float_line())
        .child(
            row()
                .w_full()
                .min_h(49.0)
                .py(6.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .child(copy)
                .children(right),
        )
}

struct PerfActionSpec<'a> {
    key: &'a str,
    name: &'a str,
    word: Option<LineWord>,
    disabled: bool,
    armed_question: Option<&'a str>,
    trailing_icon: Option<El>,
    control: Option<El>,
    gamepad_focus: bool,
}

fn perf_action_row(
    spec: PerfActionSpec<'_>,
    on_press: impl Fn(&mut AppState, &mut rune::Tx) + 'static,
) -> El {
    let PerfActionSpec {
        key,
        name,
        word,
        disabled,
        armed_question,
        trailing_icon,
        control,
        gamepad_focus,
    } = spec;
    let mut copy = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(3.0)
        .child(text(name.to_string()).font(type_section()).color(FG));
    if let Some(q) = armed_question {
        copy = copy.child(text(q.to_string()).font(type_small()).color(FG).truncate());
    } else if let Some(w) = word {
        copy = copy.child(
            text(word_text(&w))
                .font(type_small())
                .color(word_color(&w))
                .truncate(),
        );
    }
    let mut btn = row()
        .key(key.to_string())
        .role("button")
        .label(name.to_string())
        .grow(1.0)
        .min_w(0.0)
        .min_h(49.0)
        .py(6.0)
        .px(if armed_question.is_some() { 10.0 } else { 0.0 })
        .gap(12.0)
        .justify_between()
        .center_y()
        .radius(RADIUS_SMALL)
        .fill(if armed_question.is_some() {
            hover_fill()
        } else {
            TRANSPARENT
        })
        .hit()
        .disabled(disabled)
        .focusable(!disabled)
        .cursor(if disabled {
            Cursor::Default
        } else {
            Cursor::Pointer
        })
        .motion(SPRING_QUICK);
    if !disabled {
        btn = btn
            .press_on_down()
            .hover(hover_fill())
            .press(pressed_fill())
            .on_press_tx(on_press);
        if gamepad_focus {
            btn = btn.focus_ring(2.0, 3.0, FG);
        }
    }
    btn = btn.child(copy).children(trailing_icon);
    el().w_full()
        .pt(7.0)
        .pb(5.0)
        .px(20.0)
        .border_sides([1.0, 0.0, 0.0, 0.0], float_line())
        .child(
            row()
                .w_full()
                .gap(12.0)
                .center_y()
                .child(btn)
                .children(control),
        )
}

fn compact_still_button(
    key: &str,
    label: &str,
    disabled: bool,
    gamepad_focus: bool,
    on_press: impl Fn(&mut AppState) + 'static,
) -> El {
    let mut btn = row()
        .key(key.to_string())
        .role("button")
        .label(label.to_string())
        .h(30.0)
        .px(12.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .hit()
        .disabled(disabled)
        .focusable(!disabled)
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .motion(SPRING_QUICK)
        .child(
            text(label.to_string())
                .font(type_control())
                .color(if disabled { fg_faint() } else { FG }),
        );
    if !disabled {
        btn = btn
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .hover(hover_fill())
            .press(pressed_fill())
            .on_press(on_press);
        if gamepad_focus {
            btn = btn.focus_ring(2.0, 3.0, FG);
        }
    }
    btn
}

pub fn performance_card(s: &AppState, game: &Game) -> El {
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let mut rows: Vec<El> = Vec::new();

    if s.game_page.dlss_loading && s.game_page.dlss.is_none() {
        rows.push(perf_action_row(
            PerfActionSpec {
                key: "gp-perf-upscalers",
                name: "Upscalers",
                word: Some(LineWord::working("Checking")),
                disabled: false,
                armed_question: None,
                trailing_icon: Some(icon_chevron_right(14.0, fg_muted())),
                control: None,
                gamepad_focus,
            },
            |st, _| st.open_game_system_page("upscalers"),
        ));
    } else if let Some(ref dlss) = s.game_page.dlss {
        let failed = dlss.ok == Some(false);
        let scan_err = if failed {
            let trimmed = trim_stop(dlss.message.as_deref());
            Some(if trimmed.is_empty() {
                "Could not check the files".to_string()
            } else {
                trimmed
            })
        } else {
            None
        };
        let view = upscaler_view(Some(dlss));
        let word = upscaler_line_word(&view, scan_err.as_deref());
        rows.push(perf_action_row(
            PerfActionSpec {
                key: "gp-perf-upscalers",
                name: "Upscalers",
                word: Some(word),
                disabled: false,
                armed_question: None,
                trailing_icon: Some(icon_chevron_right(14.0, fg_muted())),
                control: None,
                gamepad_focus,
            },
            |st, _| st.open_game_system_page("upscalers"),
        ));
    }

    if let Some(ref sh) = s.game_page.shaders
        && sh.state != "unsupported"
    {
        let no_app = sh.state == "noApp";
        let icon = if no_app {
            icon_external(14.0, fg_muted())
        } else {
            icon_chevron_right(14.0, fg_muted())
        };
        rows.push(perf_action_row(
            PerfActionSpec {
                key: "gp-perf-shaders",
                name: "Shaders",
                word: shader_word(sh),
                disabled: false,
                armed_question: None,
                trailing_icon: Some(icon),
                control: None,
                gamepad_focus,
            },
            move |st, _| {
                if no_app {
                    st.open_external_url(SHADERS_RELEASES_URL);
                } else {
                    st.open_game_system_page("shaders");
                }
            },
        ));
    }

    if s.game_frame_gen.is_some() || game.installed {
        rows.push(
            el().w_full()
                .min_h(61.0)
                .child(crate::frame_gen_row::view(s, game)),
        );
    } else if let Some(ref fg) = s.game_page.frame_gen {
        if !fg.ok {
            rows.push(perf_reading_row(
                "Frame generation",
                Some(LineWord::bad("Could not check")),
                None,
            ));
        } else if fg.state != "hidden" {
            rows.push(perf_reading_row(
                "Frame generation",
                frame_gen_word(fg),
                None,
            ));
        }
    }
    if s.game_frame_cap.is_some() || game.installed {
        rows.push(
            el().w_full()
                .min_h(61.0)
                .child(crate::frame_cap_row::view(s, game)),
        );
    }

    if let Some(ref screen) = s.pc.hdr
        && screen.supported
        && let Some(ref hdr) = s.game_page.hdr_game
    {
        if !hdr.ok {
            rows.push(perf_reading_row(
                "HDR",
                Some(LineWord::bad("Could not check")),
                None,
            ));
        } else {
            let mode = hdr.mode.as_deref().unwrap_or("auto");
            let off = !s.settings.hdr_for_games && mode != "off";
            let word = crate::pc_hdr::build_game_hdr_word(
                hdr,
                s.settings.hdr_for_games,
                s.game_page.hdr_error.as_deref(),
            )
            .map(|(txt, is_bad)| {
                if is_bad {
                    LineWord::bad(txt)
                } else {
                    LineWord::plain(txt)
                }
            });
            let right = if off {
                compact_still_button(
                    "gp-hdr-turn-on",
                    "Turn on",
                    s.game_page.hdr_busy,
                    gamepad_focus,
                    |st| st.turn_on_hdr_for_games(),
                )
            } else {
                let mode_label = match mode {
                    "on" => "Always",
                    "off" => "Never",
                    _ => "Auto",
                };
                select_trigger(
                    SelectTrigger::new("gp-hdr-select", "HDR while playing", mode_label)
                        .open(s.open_dropdown == Some(OpenDropdown::GameHdrMode))
                        .disabled(s.game_page.hdr_busy)
                        .flat(true)
                        .gamepad_focus(gamepad_focus),
                    |st: &mut AppState, _| st.toggle_dropdown(OpenDropdown::GameHdrMode),
                )
            };
            rows.push(perf_reading_row("HDR", word, Some(right)));

            if crate::pc_hdr::show_cursor_fix(screen, hdr) {
                let gid = game.id.clone();
                let next_fix = !hdr.cursor_fix;
                let busy = s.game_page.hdr_busy;
                let toggle = switch_pill_keyed(
                    "gp-hdr-cursor-fix",
                    "Match HDR cursor brightness",
                    hdr.cursor_fix,
                )
                .disabled(busy)
                .on_press(move |st: &mut AppState| {
                    if !busy {
                        st.set_game_hdr_cursor_fix(&gid, next_fix);
                    }
                });
                rows.push(perf_reading_row(
                    "Match HDR cursor brightness",
                    Some(LineWord::plain(crate::pc_hdr::cursor_fix_word(hdr))),
                    Some(toggle),
                ));
            }
        }
    }

    if let Some(ref gs) = s.game_page.settings {
        if !gs.ok {
            rows.push(perf_reading_row(
                "Optimize",
                Some(LineWord::bad("Could not check")),
                None,
            ));
        } else if gs.supported == Some(true) {
            let applied = matches!(gs.standing.as_deref(), Some("applied" | "partly"));
            let running = gs.running == Some(true);
            let disabled = s.game_page.settings_working
                || running
                || gs.standing.as_deref() == Some("unfound");
            let kept = s
                .game_page
                .keep_status
                .as_ref()
                .and_then(|k| k.games.get(&game.id))
                .map(|v| v.as_str());
            let word = optimize_word(
                gs,
                s.game_page.settings_working,
                s.game_page.settings_work_on,
                s.game_page.settings_work_failed,
                kept,
            );
            let armed = applied && s.is_confirm_armed("gp-optimize-putback");
            let gid_opt = game.id.clone();

            let window_ctrl = gs.window_mode.as_deref().map(|wm| {
                let label = match wm {
                    "fullscreen" => "Fullscreen",
                    "borderless" => "Borderless",
                    _ => "Set elsewhere",
                };
                select_trigger(
                    SelectTrigger::new("gp-display-mode-select", "Window mode", label)
                        .open(s.open_dropdown == Some(OpenDropdown::GameDisplayMode))
                        .disabled(s.game_page.settings_working || running)
                        .gamepad_focus(gamepad_focus),
                    |st: &mut AppState, _| st.toggle_dropdown(OpenDropdown::GameDisplayMode),
                )
            });

            rows.push(perf_action_row(
                PerfActionSpec {
                    key: "gp-optimize-row",
                    name: "Optimize",
                    word: Some(word),
                    disabled,
                    armed_question: armed.then_some("Put back your settings?"),
                    trailing_icon: None,
                    control: window_ctrl,
                    gamepad_focus,
                },
                move |st, tx| {
                    if applied {
                        if st.arm_game_confirm("gp-optimize-putback", tx) {
                            st.toggle_game_optimize(&gid_opt, false);
                        }
                    } else {
                        st.toggle_game_optimize(&gid_opt, true);
                    }
                },
            ));

            if let Some(sm) = gs.smooth_motion {
                let gid_sm = game.id.clone();
                let sm_word = if sm {
                    LineWord::good("On (without in-game DLSS-G)")
                } else {
                    LineWord::plain("Off")
                };
                let mut sw = switch_pill("gp-smooth-motion-switch", "Smooth Motion", sm);
                if !(s.game_page.settings_working || running) {
                    sw = sw.on_press(move |st: &mut AppState| {
                        st.toggle_game_smooth_motion(&gid_sm, !sm);
                    });
                }
                rows.push(perf_reading_row("Smooth Motion", Some(sm_word), Some(sw)));
            }
        }
    }

    if let Some(ref gg) = s.game_page.gpu_game
        && gg.ok
        && gg.supported == Some(true)
    {
        if gg.vendor.as_deref() != Some("intel") {
            let word = if gg.afmf != Some(true) {
                LineWord::plain("Off")
            } else if gg.amd_software == Some(true) {
                LineWord::plain("On")
            } else {
                LineWord::bad("AMD Software isn’t running")
            };
            rows.push(perf_reading_row("Fluid Motion Frames", Some(word), None));
        } else {
            let cur = gg.frame_gen.as_deref().unwrap_or("game");
            let label = match cur {
                "2x" => "2x",
                "3x" => "3x",
                "4x" => "4x",
                "game" => "Game’s choice",
                _ => "Set elsewhere",
            };
            let word = s
                .game_page
                .gpu_error
                .as_ref()
                .map(|e| LineWord::bad(e.clone()));
            let trigger = select_trigger(
                SelectTrigger::new("gp-intel-fg-select", "XeSS frame generation", label)
                    .open(s.open_dropdown == Some(OpenDropdown::GameIntelFrameGen))
                    .disabled(s.game_page.gpu_busy)
                    .flat(true)
                    .gamepad_focus(gamepad_focus),
                |st: &mut AppState, _| st.toggle_dropdown(OpenDropdown::GameIntelFrameGen),
            );
            rows.push(perf_reading_row(
                "XeSS frame generation",
                word,
                Some(trigger),
            ));
        }
    }

    if rows.is_empty() {
        let msg = if s.game_page.settings.is_none()
            && s.game_page.dlss.is_none()
            && s.game_page.frame_gen.is_none()
            && s.game_page.shaders.is_none()
        {
            "Checking performance options…"
        } else {
            "No tuning options available for this game."
        };
        rows.push(perf_reading_row(msg, None, None));
    }

    let _ = PRIMARY_FG;
    game_glass_card()
        .key("gp-perf-card")
        .child(
            el().w_full().pt(18.0).px(20.0).pb(14.0).child(
                text("Performance & graphics")
                    .font(type_title().letter_spacing(-0.015))
                    .color(FG),
            ),
        )
        .children(rows)
}

fn default_graphics_feature_rows() -> Vec<GraphicsFeatureRow> {
    [
        ("upscalers", "Upscalers"),
        ("reflex", "Reflex & low latency"),
        ("rayTracing", "Ray tracing"),
        ("pathTracing", "Path tracing"),
        ("hiddenSettings", "Hidden settings"),
    ]
    .into_iter()
    .map(|(id, name)| GraphicsFeatureRow {
        id: id.into(),
        name: name.into(),
        state: "off".into(),
        route: "Checking".into(),
        route_kind: "info".into(),
        can_toggle: false,
        needs_anti_cheat_confirm: false,
        anti_cheat_warning: None,
        closest_option: None,
        details: None,
    })
    .collect()
}

pub fn graphics_features_card(s: &AppState, game: &Game) -> El {
    let state = s.game_page.graphics_features.as_ref();
    let running = state.and_then(|st| st.running).unwrap_or(false);
    let busy_id = s.game_page.graphics_features_busy.as_deref();
    let features = state
        .and_then(|st| st.features.as_ref())
        .filter(|list| !list.is_empty())
        .cloned()
        .unwrap_or_else(default_graphics_feature_rows);

    let mut rows = Vec::with_capacity(features.len());
    for feat in features {
        let is_busy = busy_id == Some(feat.id.as_str());
        let is_on = feat.state == "on";
        let word = if is_busy {
            LineWord::working(if is_on { "Reverting" } else { "Applying" })
        } else if let Some(err) = s.game_page.graphics_features_errors.get(&feat.id) {
            LineWord::bad(err.clone())
        } else {
            let status = match feat.state.as_str() {
                "on" => "On",
                "inGame" => "Set in game",
                _ => "Off",
            };
            let label = if feat.route.trim().is_empty() {
                status.to_string()
            } else {
                format!("{status} · {}", feat.route.trim())
            };
            if is_on {
                LineWord::good(label)
            } else {
                LineWord::plain(label)
            }
        };

        let disabled = state.is_none() || running || busy_id.is_some();
        let right = if feat.can_toggle {
            let gid = game.id.clone();
            let fid = feat.id.clone();
            let accept_risk = feat.needs_anti_cheat_confirm;
            let key = format!("gp-gfx-switch-{}", feat.id);
            let mut sw = switch_pill_keyed(key, feat.name.clone(), is_on);
            if !disabled {
                sw = sw.on_press(move |st: &mut AppState| {
                    st.toggle_game_graphics_feature(&gid, &fid, !is_on, accept_risk);
                });
            }
            Some(sw)
        } else {
            None
        };

        rows.push(perf_reading_row(&feat.name, Some(word), right));
    }

    game_glass_card()
        .key("gp-graphics-features-card")
        .child(
            el().w_full().pt(18.0).px(20.0).pb(15.0).child(
                text("Graphics features")
                    .font(type_title().letter_spacing(-0.015))
                    .color(FG),
            ),
        )
        .children(rows)
}
