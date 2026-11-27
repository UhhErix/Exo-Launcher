use crate::glass::{ExoGlass, exo_glass};
use crate::tokens::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SHEET,
    RADIUS_SMALL, SPRING_BASE, SPRING_FLIGHT, SPRING_MOVE, SPRING_PRESS, SPRING_QUICK, bad,
    fg_faint, fg_muted, float_line, glass_panel_fill, glass_still_control_fill, good, hairline,
    hex, hover_fill, pressed_fill, scrim_fill, selected_fill, subtle_fill, surface_card, type_body,
    type_control, type_small, warn,
};
use rune_kit::Kit;
use rune_ui::{
    ChartPalette, EffectGate, FocusRingPolicy, GlassTokens, UiRadii, UiSprings, UiTheme,
};

pub fn exo_rune_kit() -> Kit {
    let mut k = Kit::neutral(true);
    k.c.page = BG;
    k.c.bg_base = BG;
    k.c.bg_deep = surface_card();
    k.c.layer_01 = glass_panel_fill();
    k.c.layer_02 = subtle_fill();
    k.c.bg_inverse = FG;
    k.c.bg_contrast = FG;
    k.c.bg_button_neutral = glass_still_control_fill();
    k.c.bg_accent = FG;
    k.c.submit_fg = PRIMARY_FG;
    k.c.submit_icon = PRIMARY_FG;
    k.c.text_base = FG;
    k.c.text_muted = fg_muted();
    k.c.text_faint = fg_faint();
    k.c.text_inverse = PRIMARY_FG;
    k.c.text_accent = FG;
    k.c.icon_base = FG;
    k.c.icon_muted = fg_muted();
    k.c.icon_faint = fg_faint();
    k.c.icon_inverse = PRIMARY_FG;
    k.c.border_muted = hairline();
    k.c.border_base = float_line();
    k.c.border_strong = FG.alpha(0.24);
    k.c.border_focus = selected_fill();
    k.c.hover = hover_fill();
    k.c.pressed = pressed_fill();
    k.c.scrim = scrim_fill();
    k.c.surface_base = BG;
    k.c.surface_raised = surface_card();
    k.c.surface_hover = hover_fill();
    k.c.knob = FG;
    k.c.knob_on = PRIMARY_FG;
    k.c.success.icon = good();
    k.c.success.fg = good();
    k.c.success.bg = good().alpha(0.16);
    k.c.success.border = good().alpha(0.36);
    k.c.warning.icon = warn();
    k.c.warning.fg = warn();
    k.c.warning.bg = warn().alpha(0.16);
    k.c.warning.border = warn().alpha(0.36);
    k.c.danger.icon = bad();
    k.c.danger.fg = bad();
    k.c.danger.bg = bad().alpha(0.16);
    k.c.danger.border = bad().alpha(0.36);
    k.ty.ui = type_body();
    k.ty.ui_medium = type_control();
    k.ty.ui_compact = type_body();
    k.ty.small = type_small();
    k.ty.small_medium = type_small();
    k
}

pub fn exo_effect_gate(game_running: bool, gpu_enabled: bool) -> EffectGate {
    EffectGate {
        reduced_motion: false,
        game_running,
        gpu_enabled,
    }
}

pub fn exo_ui_theme(game_running: bool) -> UiTheme {
    let gate = exo_effect_gate(game_running, true);
    let glass = GlassTokens {
        control: Some(exo_glass(ExoGlass::Control)),
        panel: Some(exo_glass(ExoGlass::Panel)),
        overlay: Some(exo_glass(ExoGlass::Menu)),
        capsule: Some(exo_glass(ExoGlass::Bar)),
    };
    let radii = UiRadii {
        control: RADIUS_SMALL,
        card: RADIUS_CARD,
        menu: RADIUS_CARD,
        panel: RADIUS_PANEL,
        dialog: RADIUS_PANEL,
        sheet: RADIUS_SHEET,
        pill: RADIUS_PILL,
    };
    let springs = UiSprings {
        press: SPRING_PRESS,
        quick: SPRING_QUICK,
        base: SPRING_BASE,
        move_: SPRING_MOVE,
        flight: SPRING_FLIGHT,
        press_scale: PRESS_SCALE,
    };
    let chart = ChartPalette {
        series: [FG, good(), warn(), hex(0x60a5fa, 1.0), bad(), fg_muted()],
        grid: hairline(),
        axis: fg_muted(),
        crosshair: float_line(),
    };
    let mut theme = UiTheme::from_kit(exo_rune_kit())
        .with_focus_ring(FocusRingPolicy::SubtleFill {
            fill: selected_fill(),
            border: float_line(),
        })
        .with_glass(glass)
        .with_radii(radii)
        .with_springs(springs)
        .with_effect_gate(gate)
        .with_chart_palette(chart);
    theme.hover_fill = hover_fill();
    theme.pressed_fill = pressed_fill();
    theme.selected_fill = selected_fill();
    theme.hairline = hairline();
    theme.float_line = float_line();
    theme
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exo_theme_enforces_void_no_white_outline_and_game_pause() {
        let idle = exo_ui_theme(false);
        assert_eq!(idle.kit.c.page, BG);
        assert!(matches!(
            idle.focus_ring,
            FocusRingPolicy::SubtleFill { .. }
        ));
        assert_eq!(idle.radii.card, RADIUS_CARD);
        assert_eq!(idle.radii.panel, RADIUS_PANEL);
        assert_eq!(idle.radii.sheet, RADIUS_SHEET);
        assert_eq!(idle.radii.pill, RADIUS_PILL);
        assert!(idle.glass.control.is_some());
        assert!(idle.glass.overlay.is_some());

        let gaming = exo_ui_theme(true);
        assert!(!gaming.effect_gate.allows_gpu_fx());
        assert!(!gaming.effect_gate.allows_animation());
    }
}
