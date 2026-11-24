use crate::state::AppState;
use exo_kit::{
    FG, PRESS_SCALE, RADIUS_CARD, RADIUS_PILL, SPRING_QUICK, bad, fg_muted, float_line,
    glass_control_fill, glass_rim_bottom, glass_rim_top, glass_still_panel_fill, good, hairline,
    hover_fill, icon_check, icon_chevron_down, pressed_fill, type_control, type_section,
    type_small,
};
use exo_rpc::{FrameGenRoute, Game, GameFrameGen};
use rune::{El, col, el, row, text};

fn curly(text: &str) -> String {
    text.replace('\'', "\u{2019}")
}

pub fn route_short_label(route: Option<&FrameGenRoute>) -> String {
    let Some(route) = route else {
        return "Not available".into();
    };
    match route.kind.as_str() {
        "native_dlss" => "Built in · DLSS".into(),
        "native_fsr" => "Built in · FSR 3".into(),
        "native_xess" => "Built in · XeSS".into(),
        "mod_optiscaler" => {
            if route.label.contains("OptiScaler FG") {
                "OptiScaler FG".into()
            } else {
                "OptiScaler".into()
            }
        }
        "mod_dlssg" => "DLSSG SM86".into(),
        "mod_mfg" => "MFG Unlocker".into(),
        "mod_other" => curly(&route.label),
        "driver_smooth_motion" => {
            if route.active {
                "Smooth Motion".into()
            } else {
                "Smooth Motion · Off".into()
            }
        }
        "driver_afmf" => {
            if route.active {
                "AFMF".into()
            } else {
                "AFMF · Off".into()
            }
        }
        "driver_xess_override" => match route.choice_value.as_deref() {
            Some(c) if c != "game" => format!("XeSS {c}"),
            _ => "XeSS override".into(),
        },
        _ => "Not available".into(),
    }
}

pub fn summary_for(state: Option<&GameFrameGen>) -> (String, rune::Color) {
    let Some(fg) = state else {
        return ("Checking frame generation...".into(), fg_muted());
    };
    if !fg.ok && fg.routes.is_empty() {
        let msg = fg
            .message
            .as_deref()
            .filter(|m| !m.is_empty())
            .unwrap_or("Could not check");
        return (curly(msg), bad());
    }
    let selected = fg
        .routes
        .iter()
        .find(|r| r.id == fg.selected_route_id)
        .or_else(|| fg.routes.first());
    let word = if !fg.word.is_empty() {
        fg.word.as_str()
    } else {
        selected
            .map(|r| r.summary.as_str())
            .unwrap_or("Not available")
    };
    let color = if fg.bad {
        bad()
    } else if fg.good {
        good()
    } else {
        fg_muted()
    };
    (curly(word), color)
}

fn tier_tag(tier: &str) -> &'static str {
    match tier {
        "native" => "Built in",
        "mod" => "Mod",
        "driver" => "Driver",
        _ => "",
    }
}

fn xess_choice_label(choice: &str) -> &str {
    match choice {
        "game" => "Game\u{2019}s choice",
        other => other,
    }
}

pub fn view(s: &AppState, game: &Game) -> El {
    let fg = s.game_frame_gen.as_ref();
    let (sub_text, sub_color) = summary_for(fg);
    let selected = fg.and_then(|d| {
        d.routes
            .iter()
            .find(|r| r.id == d.selected_route_id)
            .or_else(|| d.routes.first())
    });

    let selectable_count = fg
        .map(|d| d.routes.iter().filter(|r| r.selectable).count())
        .unwrap_or(0);
    let xess_route = fg.and_then(|d| d.routes.iter().find(|r| r.kind == "driver_xess_override"));
    let how_to_enable = fg
        .and_then(|d| d.how_to_enable.as_deref())
        .or_else(|| selected.and_then(|r| r.how_to_enable.as_deref()));
    let has_menu =
        fg.is_some() && (selectable_count > 0 || xess_route.is_some() || how_to_enable.is_some());

    let right_control = if has_menu {
        let label = route_short_label(selected);
        let pill = row()
            .key("gp-fg-menu-btn")
            .role("button")
            .label(format!("Frame generation route, {label}"))
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
                if !s.frame_gen_busy {
                    s.frame_gen_menu_open = !s.frame_gen_menu_open;
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
        .child(text("Frame generation").font(type_section()).color(FG))
        .child(
            text(sub_text)
                .key("gp-fg-word")
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
        .key("gp-fg-row")
        .w_full()
        .py(6.0)
        .px(20.0)
        .border_sides([1.0, 0.0, 0.0, 0.0], float_line())
        .child(main_row);

    if s.frame_gen_menu_open
        && let Some(fg) = fg
    {
        let mut menu_col = col().w_full().py(6.0).gap(2.0);
        let game_id = game.id.clone();

        for route in &fg.routes {
            if route.kind == "driver_xess_override" {
                continue;
            }
            let is_selected = route.id == fg.selected_route_id && route.selectable;
            let tag = tier_tag(&route.tier);
            let mut item_row = row()
                .key(format!("gp-fg-opt-{}", route.id))
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
                            text(curly(&route.label))
                                .font(type_control())
                                .color(if route.selectable { FG } else { fg_muted() })
                                .truncate(),
                        ),
                )
                .children(
                    (!tag.is_empty()).then(|| text(tag).font(type_small()).color(fg_muted())),
                );

            if route.selectable {
                let gid = game_id.clone();
                let rid = route.id.clone();
                item_row = item_row
                    .role("button")
                    .label(curly(&route.label))
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .hover(hover_fill())
                    .press(pressed_fill())
                    .motion(SPRING_QUICK)
                    .on_press(move |s: &mut AppState| {
                        s.frame_gen_menu_open = false;
                        s.set_game_frame_gen(&gid, Some(&rid), None);
                    });
            }
            menu_col = menu_col.child(item_row);
        }

        if fg.routes.iter().any(|r| r.selectable && r.tier != "native") {
            let gid = game_id.clone();
            let off_row = row()
                .key("gp-fg-opt-off")
                .role("button")
                .label("Off")
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
                    s.frame_gen_menu_open = false;
                    s.set_game_frame_gen(&gid, Some("off"), None);
                })
                .child(text("Off").font(type_control()).color(FG));
            menu_col = menu_col.child(off_row);
        }

        if let Some(xess) = xess_route
            && !xess.choices.is_empty()
        {
            menu_col = menu_col
                .child(el().w_full().h(1.0).my(4.0).fill(hairline()))
                .child(
                    el().px(12.0)
                        .py(2.0)
                        .child(text("XeSS override").font(type_small()).color(fg_muted())),
                );
            let active_choice = xess.choice_value.as_deref().unwrap_or("game");
            for choice in &xess.choices {
                let is_on = active_choice == choice.as_str();
                let gid = game_id.clone();
                let ch = choice.clone();
                let ch_row = row()
                    .key(format!("gp-fg-xess-{choice}"))
                    .role("button")
                    .label(format!("XeSS override {}", xess_choice_label(choice)))
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .w_full()
                    .min_h(32.0)
                    .px(12.0)
                    .py(5.0)
                    .gap(8.0)
                    .center_y()
                    .radius(8.0)
                    .hover(hover_fill())
                    .press(pressed_fill())
                    .motion(SPRING_QUICK)
                    .on_press(move |s: &mut AppState| {
                        s.frame_gen_menu_open = false;
                        s.set_game_frame_gen(&gid, None, Some(&ch));
                    })
                    .children(is_on.then(|| icon_check(13.0, good())))
                    .child(
                        text(xess_choice_label(choice))
                            .font(type_control())
                            .color(FG),
                    );
                menu_col = menu_col.child(ch_row);
            }
        }

        if let Some(sel) = selected {
            menu_col = menu_col.child(el().w_full().h(1.0).my(4.0).fill(hairline()));
            if !sel.latency_note.is_empty() {
                menu_col = menu_col.child(
                    el().px(12.0).py(3.0).child(
                        text(curly(&sel.latency_note))
                            .font(type_small())
                            .color(fg_muted()),
                    ),
                );
            }
            if !sel.anti_cheat_note.is_empty() {
                menu_col = menu_col.child(
                    el().px(12.0).py(3.0).child(
                        text(curly(&sel.anti_cheat_note))
                            .font(type_small())
                            .color(fg_muted()),
                    ),
                );
            }
        }

        if let Some(how) = how_to_enable {
            menu_col = menu_col.child(
                el().px(12.0)
                    .py(3.0)
                    .child(text(curly(how)).font(type_small()).color(FG)),
            );
        }

        container = container.child(
            col()
                .key("gp-fg-menu")
                .w_full()
                .mt(6.0)
                .mb(4.0)
                .radius(RADIUS_CARD)
                .fill(glass_still_panel_fill())
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(menu_col),
        );
    }

    container
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_route(id: &str, tier: &str, kind: &str, label: &str, summary: &str) -> FrameGenRoute {
        FrameGenRoute {
            id: id.into(),
            tier: tier.into(),
            kind: kind.into(),
            label: label.into(),
            status: "active".into(),
            summary: summary.into(),
            gpu_supported: true,
            gpu_note: "RTX 4070".into(),
            anti_cheat_verdict: "safe".into(),
            anti_cheat_note: "Safe: built into the game".into(),
            auto_enable: false,
            latency_note: "Lowest latency".into(),
            selectable: tier != "unavailable",
            active: tier != "unavailable",
            reason: None,
            how_to_enable: None,
            choice_value: None,
            choices: Vec::new(),
        }
    }

    #[test]
    fn summary_never_hardcodes_built_in_when_loading_or_unavailable() {
        let (loading_text, _) = summary_for(None);
        assert_ne!(loading_text, "Built into the game");
        assert!(loading_text.contains("Checking"));

        let unavail = GameFrameGen {
            ok: true,
            best_route_id: "unavailable".into(),
            selected_route_id: "unavailable".into(),
            state: "unavailable".into(),
            word: "Mods would get you banned".into(),
            good: false,
            bad: false,
            reason: Some("Mods would get you banned".into()),
            how_to_enable: Some("Anti-Cheat Expert blocks DLL mods; only built-in or signed driver frame generation can run safely here.".into()),
            routes: vec![sample_route(
                "unavailable",
                "unavailable",
                "unavailable",
                "Not available",
                "Mods would get you banned",
            )],
            ..Default::default()
        };
        let (text, _) = summary_for(Some(&unavail));
        assert_eq!(text, "Mods would get you banned");
    }

    #[test]
    fn view_renders_row_and_menu_for_wuwa_routes() {
        let make_state = |menu_open: bool| {
            let mut state = AppState::with_demo_games();
            state.frame_gen_menu_open = menu_open;
            state.game_frame_gen = Some(GameFrameGen {
                ok: true,
                best_route_id: "native_dlss".into(),
                selected_route_id: "native_dlss".into(),
                state: "native".into(),
                word: "Built into the game · DLSS".into(),
                good: false,
                bad: false,
                routes: vec![
                    sample_route(
                        "native_dlss",
                        "native",
                        "native_dlss",
                        "Built into the game (DLSS)",
                        "Built into the game · DLSS",
                    ),
                    sample_route(
                        "driver_smooth_motion",
                        "driver",
                        "driver_smooth_motion",
                        "NVIDIA Smooth Motion",
                        "NVIDIA Smooth Motion · Off",
                    ),
                ],
                ..Default::default()
            });
            state
        };

        let mut rt = rune::Runtime::new(
            make_state(false),
            |s, _| view(s, &s.games[0]),
            (600.0, 400.0),
            rune::Env::new(true, 1.0),
        );
        rt.frame(0.0);
        assert!(rt.presented("gp-fg-row").is_some());
        assert!(rt.presented("gp-fg-menu-btn").is_some());
        assert!(rt.presented("gp-fg-menu").is_none());

        let mut rt_open = rune::Runtime::new(
            make_state(true),
            |s, _| view(s, &s.games[0]),
            (600.0, 400.0),
            rune::Env::new(true, 1.0),
        );
        rt_open.frame(0.0);
        assert!(rt_open.presented("gp-fg-menu").is_some());
        assert!(rt_open.presented("gp-fg-opt-native_dlss").is_some());
        assert!(
            rt_open
                .presented("gp-fg-opt-driver_smooth_motion")
                .is_some()
        );
    }

    #[test]
    fn route_short_label_covers_mfg_and_optifg() {
        let mfg = sample_route("mod_mfg", "mod", "mod_mfg", "MFG Unlocker", "MFG Unlocker");
        assert_eq!(route_short_label(Some(&mfg)), "MFG Unlocker");
        let optifg = sample_route(
            "mod_optiscaler",
            "mod",
            "mod_optiscaler",
            "OptiScaler FG",
            "OptiScaler FG",
        );
        assert_eq!(route_short_label(Some(&optifg)), "OptiScaler FG");
    }

    #[test]
    fn view_shows_mods_would_get_you_banned_for_online_anti_cheat_unavailable() {
        let mut state = AppState::with_demo_games();
        state.game_frame_gen = Some(GameFrameGen {
            ok: true,
            best_route_id: "unavailable".into(),
            selected_route_id: "unavailable".into(),
            state: "unavailable".into(),
            word: "Mods would get you banned".into(),
            good: false,
            bad: false,
            reason: Some("Mods would get you banned".into()),
            how_to_enable: Some("Riot Vanguard blocks DLL mods; only built-in or signed driver frame generation can run safely here.".into()),
            routes: vec![sample_route(
                "unavailable",
                "unavailable",
                "unavailable",
                "Not available",
                "Mods would get you banned",
            )],
            ..Default::default()
        });
        let mut rt = rune::Runtime::new(
            state,
            |s, _| view(s, &s.games[0]),
            (600.0, 400.0),
            rune::Env::new(true, 1.0),
        );
        rt.frame(0.0);
        assert!(rt.presented("gp-fg-row").is_some());
        assert!(rt.presented("gp-fg-word").is_some());
        assert!(rt.presented("gp-fg-confirm").is_none());
    }
}
