use exo_kit::{
    EXO_GLASS_POLICY, ExoGlass, FG, RADIUS_PILL, TRANSPARENT, apply_glass_opt, fg_muted, glass_rim,
    type_section, type_small, warn,
};
use exo_present::{
    HUD_HEIGHT_PT, HUD_MARGIN_PT, HUD_PADDING_X_PT, HudLine, HudPosition, HudState,
    estimate_pill_width_pt,
};
use rune::{
    Align, ConfiguredOverlay, Cx, DisplayTarget, El, KeyPolicy, NativeDisplayId, OverlayHandle,
    OverlayInput, OverlayLevel, OverlayOptions, OverlayPlacement, OverlaySpec, ScreenEdge, el, row,
    text,
};

pub fn align_for_position(position: HudPosition) -> Align {
    match position {
        HudPosition::Left => Align::Start,
        HudPosition::Middle => Align::Center,
        HudPosition::Right => Align::End,
    }
}

pub fn configured_overlay() -> ConfiguredOverlay<HudState> {
    let spec = OverlaySpec {
        name: "ExoPresentHud",
        size: (120.0, HUD_HEIGHT_PT + 6.0),
        edge: ScreenEdge::Top,
        margin: HUD_MARGIN_PT,
        align: Align::End,
        inset: HUD_MARGIN_PT,
        interactive: false,
        view,
    };
    let options = OverlayOptions {
        placement: Some(OverlayPlacement::Edge {
            edge: ScreenEdge::Top,
            margin: HUD_MARGIN_PT,
            align: Align::End,
            inset: HUD_MARGIN_PT,
        }),
        display: DisplayTarget::Primary,
        follow_pointer: false,
        level: OverlayLevel::AboveOverlays,
        exclude_from_capture: true,
        keys: KeyPolicy::None,
        input: OverlayInput::None,
        prewarm: false,
        xwayland_input: false,
    };
    spec.options(options)
        .glass(EXO_GLASS_POLICY)
        .max_backdrop_blurs(1)
        .release_gpu_when_hidden()
}

pub fn sync_overlay(
    handle: &OverlayHandle<HudState>,
    visible: bool,
    position: HudPosition,
    line: HudLine,
    hmonitor: isize,
    elapsed_secs: u64,
) {
    if !visible {
        handle.update(|s, _| s.visible = false);
        handle.hide();
        return;
    }

    let measured = estimate_pill_width_pt(&line);
    let align = align_for_position(position);
    let inset = if position == HudPosition::Middle {
        0.0
    } else {
        HUD_MARGIN_PT
    };
    if hmonitor != 0 {
        handle.set_display(DisplayTarget::Native(NativeDisplayId::Windows(hmonitor)));
    }
    handle.set_placement(Some(OverlayPlacement::Edge {
        edge: ScreenEdge::Top,
        margin: HUD_MARGIN_PT,
        align,
        inset,
    }));
    handle.resize((measured + 8.0, HUD_HEIGHT_PT + 6.0));
    handle.update(move |s, _| {
        s.visible = true;
        s.position = position;
        s.elapsed_secs = elapsed_secs;
        s.apply_line(line, measured);
    });
    handle.show();
}

pub fn view(state: &HudState, cx: &mut Cx<HudState>) -> El {
    let reduced_transparency = state.reduced_transparency || cx.system_reduced_transparency();
    let high_contrast =
        state.high_contrast || cx.system_increased_contrast() || cx.system_contrast().is_some();
    let allow_glass = !reduced_transparency && !high_contrast;
    let (dx, dy) = state.oled_shift_offset();

    let fps_label = format!("{} FPS", state.line.fps);
    let mut content = row()
        .center_y()
        .gap(0.0)
        .child(text(fps_label).font(type_section()).color(FG));

    if let Some(ref extra) = state.line.items_text {
        content = content.child(text(extra.clone()).font(type_small()).color(fg_muted()));
    }

    if state.line.stutter_alert {
        content = content.child(
            el().pl(6.0).center_y().child(
                el().size(6.0, 6.0)
                    .radius(RADIUS_PILL)
                    .fill(warn())
                    .key("hud-stutter-dot"),
            ),
        );
    }

    let pill_w = state.min_width_pt.max(estimate_pill_width_pt(&state.line));
    let pill = el()
        .key("exo-present-hud-pill")
        .min_w(pill_w)
        .h(HUD_HEIGHT_PT)
        .px(HUD_PADDING_X_PT)
        .radius(RADIUS_PILL)
        .border(1.0, glass_rim())
        .center()
        .child(content);
    let styled_pill = apply_glass_opt(pill, ExoGlass::Control, allow_glass);

    el().w_full()
        .h_full()
        .fill(TRANSPARENT)
        .pt(dy.max(0.0))
        .pr((-dx).max(0.0))
        .pb((-dy).max(0.0))
        .pl(dx.max(0.0))
        .center()
        .child(styled_pill)
}

#[cfg(test)]
mod tests {
    use super::*;
    use rune::{
        Env, Runtime,
        render::{KIND_BACKDROP_BLUR, KIND_LIQUID_GLASS},
    };

    #[test]
    fn hud_renders_liquid_glass_and_a11y_solid_fallback() {
        let mut state = HudState {
            visible: true,
            position: HudPosition::Right,
            oled_pixel_shift: true,
            elapsed_secs: 360,
            ..Default::default()
        };
        let line = HudLine {
            fps: 144,
            items_text: Some(" · 122 LOW · 6.9 MS".into()),
            stutter_alert: true,
        };
        let w = estimate_pill_width_pt(&line);
        state.apply_line(line, w);

        let mut rt = Runtime::new(
            state,
            view,
            (w + 16.0, HUD_HEIGHT_PT + 8.0),
            Env::new(true, 1.0),
        );
        rt.set_max_backdrop_blurs(1);
        rt.set_glass_policy(EXO_GLASS_POLICY);
        rt.frame(0.0);

        let adhoc = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert_eq!(adhoc, 0);
        let glass = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_LIQUID_GLASS)
            .count();
        assert_eq!(glass, 1);
        assert!(rt.find("exo-present-hud-pill").is_some());
        assert!(rt.find("hud-stutter-dot").is_some());

        rt.update(|s, _| s.reduced_transparency = true);
        rt.frame(1.0 / 60.0);
        let glass_off = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_LIQUID_GLASS)
            .count();
        assert_eq!(glass_off, 0);
    }
}
