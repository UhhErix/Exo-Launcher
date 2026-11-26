use crate::state::AppState;
use exo_kit::{
    FG, RADIUS_PILL, bad, fg_muted, float_line, glass_still_panel_fill, good, icon_check,
    type_control, type_section, type_small,
};
use exo_rpc::{FrameGenRoute, Game, GameFrameGen};
use rune::{El, col, row, text};

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
        "mod_exo_fg_bridge" => "Exo FG Bridge".into(),
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
        "external_exo_flow" => {
            if route.active {
                "Exo Flow".into()
            } else {
                "Exo Flow · Off".into()
            }
        }
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

pub fn auto_route(fg: &GameFrameGen) -> Option<&FrameGenRoute> {
    if fg.best_route_id.is_empty()
        || fg.best_route_id == "unavailable"
        || fg.best_route_id == fg.selected_route_id
    {
        return None;
    }
    fg.routes.iter().find(|r| {
        r.id == fg.best_route_id
            && r.selectable
            && !r.requires_risk_confirm
            && matches!(r.tier.as_str(), "native" | "driver")
    })
}

pub fn view(s: &AppState, _game: &Game) -> El {
    let fg = s.game_frame_gen.as_ref();
    let (sub_text, sub_color) = summary_for(fg);
    let selected = fg.and_then(|d| {
        d.routes
            .iter()
            .find(|r| r.id == d.selected_route_id)
            .or_else(|| d.routes.first())
    });
    let tag = selected
        .filter(|r| r.selectable && r.kind != "unavailable")
        .map(|r| route_short_label(Some(r)));

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

    let right = tag.map(|label| {
        row()
            .key("gp-fg-auto")
            .h(28.0)
            .px(11.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_still_panel_fill())
            .border(1.0, float_line())
            .child(icon_check(12.0, good()))
            .child(text(label).font(type_control()).color(FG))
    });

    col()
        .key("gp-fg-row")
        .w_full()
        .py(6.0)
        .px(20.0)
        .border_sides([1.0, 0.0, 0.0, 0.0], float_line())
        .child(
            row()
                .w_full()
                .min_h(44.0)
                .py(6.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .child(left)
                .children(right),
        )
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
            requires_risk_confirm: false,
            risk_warning: None,
            anti_cheat: None,
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
    fn view_shows_the_route_exo_picked_without_a_menu() {
        let mut state = AppState::with_demo_games();
        state.game_frame_gen = Some(GameFrameGen {
            ok: true,
            best_route_id: "native_dlss".into(),
            selected_route_id: "native_dlss".into(),
            state: "native".into(),
            word: "Built into the game · DLSS".into(),
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
        let mut rt = rune::Runtime::new(
            state,
            |s, _| view(s, &s.games[0]),
            (600.0, 400.0),
            rune::Env::new(true, 1.0),
        );
        rt.frame(0.0);
        assert!(rt.presented("gp-fg-row").is_some());
        assert!(rt.presented("gp-fg-auto").is_some());
        assert!(rt.presented("gp-fg-menu-btn").is_none());
    }

    #[test]
    fn auto_route_picks_best_only_when_safe() {
        let mut fg = GameFrameGen {
            ok: true,
            best_route_id: "driver_smooth_motion".into(),
            selected_route_id: "off".into(),
            routes: vec![sample_route(
                "driver_smooth_motion",
                "driver",
                "driver_smooth_motion",
                "NVIDIA Smooth Motion",
                "NVIDIA Smooth Motion",
            )],
            ..Default::default()
        };
        assert_eq!(
            auto_route(&fg).map(|r| r.id.as_str()),
            Some("driver_smooth_motion")
        );
        fg.selected_route_id = "driver_smooth_motion".into();
        assert!(auto_route(&fg).is_none());
        fg.selected_route_id = "off".into();
        fg.routes[0].requires_risk_confirm = true;
        assert!(auto_route(&fg).is_none());
        fg.routes[0].requires_risk_confirm = false;
        fg.routes[0].tier = "mod".into();
        assert!(auto_route(&fg).is_none());
        fg.routes[0].auto_enable = true;
        assert!(auto_route(&fg).is_none());
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
        assert!(rt.presented("gp-fg-auto").is_none());
    }
}
