use crate::glass::{ExoGlass, apply_glass, apply_glass_opt};
use crate::icons::{icon_close, icon_minimize, icon_win_maximize, icon_win_restore};
use crate::tokens::{
    BG, FG, GAP, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, SPRING_BASE,
    SPRING_POPOVER, SPRING_QUICK, SPRING_SMOOTH, SPRING_SNAPPY, TITLEBAR_HEIGHT, TRANSPARENT, bad,
    card_border, fg_muted, glass_rim, good, hover_fill, pressed_fill, subtle_fill, surface_card,
    track_fill, type_control, type_section, type_small, warn,
};
use rune::{
    Align, CaptionGlyph, CaptionStyle, Color, Cursor, Effect, El, Side, SpringPair, Titlebar,
    Transition, Vec2, col, el, row, text,
};

pub const CAPTION_BUTTON_WIDTH: f32 = 34.0;
pub const CAPTION_BUTTON_HEIGHT: f32 = 30.0;
pub const CAPTION_BUTTON_GAP: f32 = 2.0;
pub const CAPTION_CAPSULE_PAD: f32 = 3.0;
pub const CAPTION_TRAILING_INSET: f32 = GAP + CAPTION_CAPSULE_PAD;

pub fn caption_glyph(glyph: CaptionGlyph, color: Color) -> El {
    match glyph {
        CaptionGlyph::Minimize => icon_minimize(16.0, color),
        CaptionGlyph::Maximize => icon_win_maximize(16.0, color),
        CaptionGlyph::Restore => icon_win_restore(16.0, color),
        CaptionGlyph::Close => icon_close(16.0, color),
    }
}

pub fn caption_style() -> CaptionStyle {
    CaptionStyle::new()
        .button_width(CAPTION_BUTTON_WIDTH)
        .button_height(CAPTION_BUTTON_HEIGHT)
        .gap(CAPTION_BUTTON_GAP)
        .trailing_inset(CAPTION_TRAILING_INSET)
        .radius(RADIUS_PILL)
        .hover_fill(hover_fill())
        .press_fill(pressed_fill())
        .hover_symbol(FG)
        .press_symbol(FG)
        .close_hover_fill(hover_fill())
        .close_press_fill(pressed_fill())
        .close_hover_symbol(bad())
        .close_press_symbol(bad())
        .glyph(caption_glyph)
}

pub fn exo_titlebar() -> Titlebar {
    Titlebar::new(TITLEBAR_HEIGHT).caption_style(caption_style())
}

pub fn popover_origin(side: Side, align: Align) -> Vec2 {
    let along = match align {
        Align::Start => 0.0,
        Align::Center => 0.5,
        Align::End => 1.0,
    };
    match side {
        Side::Bottom => Vec2::new(along, 0.0),
        Side::Top => Vec2::new(along, 1.0),
        Side::Right => Vec2::new(0.0, along),
        Side::Left => Vec2::new(1.0, along),
    }
}

pub fn popover_transition() -> Transition {
    popover_transition_for(Side::Bottom, Align::End)
}

pub fn popover_transition_for(side: Side, align: Align) -> Transition {
    popover_transition_from(popover_origin(side, align))
}

pub fn glass_enter(scale: f32, anchor: Vec2) -> Transition {
    Transition {
        insertion: Effect {
            scale,
            anchor,
            ..Effect::IDENTITY
        },
        ..Transition::scale_anchor(scale, anchor)
    }
}

pub fn popover_transition_from(anchor: Vec2) -> Transition {
    let slide = Vec2::new((0.5 - anchor.x) * 4.0, (0.5 - anchor.y) * 8.0);
    let mut enter = glass_enter(0.965, anchor);
    enter.insertion.offset = slide;
    let exit =
        Transition::scale_anchor(0.965, anchor).combined(Transition::offset(slide.x, slide.y));
    Transition::asymmetric(enter, exit).springs(SpringPair::new(SPRING_POPOVER, SPRING_QUICK))
}

pub fn modal_scrim_transition() -> Transition {
    Transition::asymmetric(Transition::opacity(), Transition::opacity())
        .springs(SpringPair::new(SPRING_BASE, SPRING_QUICK))
}

pub fn modal_card_transition() -> Transition {
    let mut enter = glass_enter(0.965, Vec2::new(0.5, 0.5));
    enter.insertion.offset = Vec2::new(0.0, 10.0);
    let exit = Transition::scale(0.965).combined(Transition::offset(0.0, 6.0));
    Transition::asymmetric(enter, exit).springs(SpringPair::new(SPRING_SMOOTH, SPRING_QUICK))
}

pub fn glass_capsule(key: &'static str) -> El {
    glass_capsule_opt(key, true)
}

pub fn glass_capsule_opt(key: &'static str, _blur: bool) -> El {
    let capsule = row()
        .key(key)
        .h(36.0)
        .px(3.0)
        .gap(2.0)
        .center_y()
        .radius(RADIUS_PILL);
    apply_glass(capsule, ExoGlass::Bar, false)
}

pub fn glass_panel(key: impl Into<String>, width: f32) -> El {
    glass_panel_opt(key, width, true).transition(popover_transition())
}

pub fn glass_panel_opt(key: impl Into<String>, width: f32, _blur: bool) -> El {
    let panel = rune::col()
        .key(key)
        .w(width)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .hit()
        .cursor(Cursor::Default);
    apply_glass(panel, ExoGlass::Panel, false)
}

pub fn glass_plate(key: impl Into<String>, _blur: bool) -> El {
    let plate = row().key(key).center_y().radius(RADIUS_PILL);
    apply_glass(plate, ExoGlass::ClearMedia, false)
}

pub fn glass_input(key: impl Into<String>) -> El {
    let input = row()
        .key(key)
        .h(34.0)
        .px(12.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .cursor(Cursor::Text);
    apply_glass(input, ExoGlass::Control, false)
}

pub fn glass_chip(key: impl Into<String>, label: impl Into<String>, icon: Option<El>) -> El {
    let mut chip = apply_glass(
        row()
            .key(key)
            .h(28.0)
            .px(10.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL),
        ExoGlass::Control,
        false,
    );
    if let Some(ic) = icon {
        chip = chip.child(ic);
    }
    chip.child(text(label.into()).font(type_small()).color(FG))
}

pub fn amoled_card(radius: f32) -> El {
    col()
        .radius(radius)
        .continuous_corners()
        .fill(surface_card())
        .border(1.0, card_border())
}

pub fn exo_play_button(key: &'static str, label: &'static str, icon: Option<El>) -> El {
    let mut btn = row()
        .key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(40.0)
        .min_w(112.0)
        .px(24.0)
        .gap(8.0)
        .justify_center()
        .center_y()
        .radius(RADIUS_PILL)
        .fill(FG)
        .hover(FG.alpha(0.92))
        .press(FG.alpha(0.84))
        .shadow(2.0, 10.0, BG.alpha(0.35))
        .hover_motion(SPRING_QUICK, SPRING_BASE)
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label).font(type_section()).color(PRIMARY_FG))
}

pub fn glass_icon_circle(key: &'static str, label: &'static str, size: f32, icon: El) -> El {
    glass_icon_circle_opt(key, label, size, icon, true)
}

pub fn glass_icon_circle_opt(
    key: &'static str,
    label: &'static str,
    size: f32,
    icon: El,
    blur: bool,
) -> El {
    let btn = el()
        .key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(size, size)
        .center()
        .radius(RADIUS_PILL)
        .hover(hover_fill())
        .press(pressed_fill())
        .hover_motion(SPRING_QUICK, SPRING_BASE)
        .motion(SPRING_QUICK)
        .child(icon);
    apply_glass_opt(btn, ExoGlass::Control, true, blur)
}

pub fn primary_button(key: &'static str, label: &'static str, icon: Option<El>) -> El {
    primary_button_dyn(key, label, icon)
}

pub fn primary_button_dyn(
    key: impl Into<String>,
    label: impl Into<String>,
    icon: Option<El>,
) -> El {
    let label_str = label.into();
    let mut btn = row()
        .key(key)
        .role("button")
        .label(label_str.clone())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(FG)
        .hover(FG.alpha(0.92))
        .press(FG.alpha(0.84))
        .hover_motion(SPRING_QUICK, SPRING_BASE)
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label_str).font(type_control()).color(PRIMARY_FG))
}

pub fn glass_button(key: &'static str, label: &'static str, icon: Option<El>) -> El {
    glass_button_opt(key, label, icon, true)
}

pub fn glass_button_dyn(
    key: impl Into<String>,
    label: impl Into<String>,
    icon: Option<El>,
    blur: bool,
) -> El {
    let label_str = label.into();
    let mut btn = apply_glass_opt(
        row()
            .key(key)
            .role("button")
            .label(label_str.clone())
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(32.0)
            .px(12.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .hover(hover_fill())
            .press(pressed_fill())
            .hover_motion(SPRING_QUICK, SPRING_BASE)
            .motion(SPRING_QUICK),
        ExoGlass::Control,
        true,
        blur,
    );
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label_str).font(type_control()).color(FG))
}

pub fn danger_button_dyn(key: impl Into<String>, label: impl Into<String>, icon: Option<El>) -> El {
    let label_str = label.into();
    let mut btn = row()
        .key(key)
        .role("button")
        .label(label_str.clone())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(bad().alpha(0.14))
        .hover(bad().alpha(0.24))
        .press(bad().alpha(0.32))
        .border(1.0, bad().alpha(0.36))
        .hover_motion(SPRING_QUICK, SPRING_BASE)
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label_str).font(type_control()).color(bad()))
}

pub fn glass_button_opt(
    key: &'static str,
    label: &'static str,
    icon: Option<El>,
    blur: bool,
) -> El {
    let mut btn = apply_glass_opt(
        row()
            .key(key)
            .role("button")
            .label(label)
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(32.0)
            .px(12.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .hover(hover_fill())
            .press(pressed_fill())
            .hover_motion(SPRING_QUICK, SPRING_BASE)
            .motion(SPRING_QUICK),
        ExoGlass::Control,
        true,
        blur,
    );
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label).font(type_control()).color(FG))
}

pub fn filter_pill(key: impl Into<String>, label: impl Into<String>, active: bool) -> El {
    let label: String = label.into();
    el().key(key.into())
        .role("tab")
        .label(label.clone())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
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
        .hover_motion(SPRING_QUICK, SPRING_BASE)
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

pub fn nav_tab(key: &'static str, label: &'static str, active: bool, icon: El) -> El {
    row()
        .key(key)
        .role("tab")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(30.0)
        .px(10.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if active { FG } else { TRANSPARENT })
        .hover(if active { FG } else { hover_fill() })
        .press(if active { FG } else { pressed_fill() })
        .motion(SPRING_QUICK)
        .child(icon)
        .child(
            text(label)
                .font(type_section())
                .color(if active { PRIMARY_FG } else { FG }),
        )
}

pub fn danger_button(key: &'static str, label: &'static str, icon: Option<El>) -> El {
    let mut btn = row()
        .key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(bad())
        .hover(bad().alpha(0.90))
        .press(bad().alpha(0.80))
        .hover_motion(SPRING_QUICK, SPRING_BASE)
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label).font(type_control()).color(PRIMARY_FG))
}

pub fn discount_badge(discount: i32) -> El {
    el().h(22.0)
        .px(8.0)
        .radius(RADIUS_PILL)
        .center()
        .fill(good())
        .child(
            text(format!("-{discount}%"))
                .font(type_small())
                .color(PRIMARY_FG),
        )
}

pub fn stat_chip(label: &'static str, value: String) -> El {
    col()
        .px(14.0)
        .py(10.0)
        .gap(2.0)
        .radius(RADIUS_CARD)
        .fill(subtle_fill())
        .border(1.0, glass_rim())
        .child(text(label).font(type_small()).color(fg_muted()))
        .child(text(value).font(type_section()).color(FG))
}

pub fn switch_pill(key: &'static str, label: &'static str, on: bool) -> El {
    switch_pill_keyed(key, label, on)
}

pub fn switch_track(on: bool) -> El {
    let knob = el()
        .size(20.0, 20.0)
        .radius(10.0)
        .fill(FG)
        .shadow(1.0, 3.0, BG.alpha(0.35))
        .absolute(if on { 18.0 } else { 2.0 }, 2.0)
        .motion(SPRING_SNAPPY);

    el().size(40.0, 24.0)
        .radius(RADIUS_PILL)
        .fill(if on { good() } else { track_fill() })
        .motion(SPRING_QUICK)
        .child(knob)
}

pub fn switch_pill_keyed(key: impl Into<String>, label: impl Into<String>, on: bool) -> El {
    switch_track(on)
        .key(key)
        .role("switch")
        .label(label)
        .checked(on)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
}

pub fn slider_track(
    key: impl Into<String>,
    label: impl Into<String>,
    width: f32,
    fraction: f32,
    _dragging: bool,
    disabled: bool,
) -> El {
    let frac = fraction.clamp(0.0, 1.0);
    let fill_w = (width * frac).max(4.0);
    let knob = 14.0_f32;
    let knob_x = ((width - knob) * frac).clamp(0.0, (width - knob).max(0.0));
    let cursor = if disabled {
        Cursor::NotAllowed
    } else {
        Cursor::Pointer
    };
    el().key(key)
        .role("slider")
        .label(label)
        .focusable(!disabled)
        .disabled(disabled)
        .hit()
        .cursor(cursor)
        .w(width)
        .h(20.0)
        .child(
            el().absolute(0.0, 8.0)
                .w(width)
                .h(4.0)
                .radius(2.0)
                .fill(Color::WHITE.alpha(0.16))
                .child(el().w(fill_w).h(4.0).radius(2.0).fill(FG)),
        )
        .child(
            el().absolute(knob_x, 3.0)
                .size(knob, knob)
                .radius(knob * 0.5)
                .fill(FG)
                .shadow(1.0, 3.0, BG.alpha(0.35)),
        )
}

pub fn progress_bar(width: f32, fraction: f32) -> El {
    let fill_w = (width * fraction.clamp(0.0, 1.0)).max(2.0);
    el().w(width)
        .h(4.0)
        .radius(2.0)
        .fill(Color::WHITE.alpha(0.16))
        .child(el().w(fill_w).h(4.0).radius(2.0).fill(FG))
}

#[derive(Clone, Copy)]
pub enum BadgeTone {
    Good,
    Warn,
    Bad,
    Neutral,
}

pub fn status_badge(label: String, tone: BadgeTone) -> El {
    let (bg, fg) = match tone {
        BadgeTone::Good => (good().alpha(0.18), good()),
        BadgeTone::Warn => (warn().alpha(0.18), warn()),
        BadgeTone::Bad => (bad().alpha(0.18), bad()),
        BadgeTone::Neutral => (subtle_fill(), fg_muted()),
    };
    el().h(22.0)
        .px(9.0)
        .radius(RADIUS_PILL)
        .center()
        .fill(bg)
        .child(text(label).font(type_small()).color(fg))
}

pub fn section_card(title: &'static str, subtitle: Option<String>) -> El {
    let mut header = col()
        .w_full()
        .gap(2.0)
        .child(text(title).font(type_section()).color(FG));
    if let Some(sub) = subtitle {
        header = header.child(
            text(sub)
                .font(type_small())
                .color(fg_muted())
                .w_full()
                .wrap(),
        );
    }
    col()
        .w_full()
        .px(18.0)
        .py(16.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .fill(subtle_fill())
        .child(header)
}

pub fn setting_row(title: &'static str, description: &'static str, control: El) -> El {
    row()
        .w_full()
        .py(10.0)
        .gap(16.0)
        .justify_between()
        .center_y()
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(2.0)
                .child(text(title).font(type_control()).color(FG))
                .child(
                    text(description)
                        .font(type_small())
                        .color(fg_muted())
                        .w_full()
                        .wrap(),
                ),
        )
        .child(control)
}

pub fn segmented_bar(segments: &[(f32, Color)]) -> El {
    row()
        .w_full()
        .h(8.0)
        .radius(4.0)
        .clip_rounded(4.0)
        .fill(Color::WHITE.alpha(0.10))
        .gap(1.5)
        .children(
            segments
                .iter()
                .filter(|(f, _)| *f > 0.004)
                .map(|(frac, color)| el().grow(*frac).h(8.0).fill(*color)),
        )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn caption_style_matches_exo_tokens() {
        let style = caption_style();
        let expected = CaptionStyle::new()
            .button_width(34.0)
            .button_height(30.0)
            .gap(2.0)
            .trailing_inset(13.0)
            .radius(RADIUS_PILL)
            .hover_fill(hover_fill())
            .press_fill(pressed_fill())
            .hover_symbol(FG)
            .press_symbol(FG)
            .close_hover_fill(hover_fill())
            .close_press_fill(pressed_fill())
            .close_hover_symbol(bad())
            .close_press_symbol(bad())
            .glyph(caption_glyph);
        assert_eq!(style, expected);
        assert_ne!(style, CaptionStyle::default());

        let tb = exo_titlebar();
        assert_eq!(tb.height(), TITLEBAR_HEIGHT);
        assert_eq!(tb.caption(), style);

        for kind in [
            CaptionGlyph::Minimize,
            CaptionGlyph::Maximize,
            CaptionGlyph::Restore,
            CaptionGlyph::Close,
        ] {
            let _ = caption_glyph(kind, fg_muted());
        }
    }

    #[test]
    fn popover_origin_matches_side_and_align() {
        assert_eq!(
            popover_origin(Side::Bottom, Align::Start),
            Vec2::new(0.0, 0.0)
        );
        assert_eq!(
            popover_origin(Side::Bottom, Align::Center),
            Vec2::new(0.5, 0.0)
        );
        assert_eq!(
            popover_origin(Side::Bottom, Align::End),
            Vec2::new(1.0, 0.0)
        );
        assert_eq!(popover_origin(Side::Top, Align::Start), Vec2::new(0.0, 1.0));
        assert_eq!(popover_origin(Side::Top, Align::End), Vec2::new(1.0, 1.0));
        assert_eq!(
            popover_origin(Side::Right, Align::Start),
            Vec2::new(0.0, 0.0)
        );
        assert_eq!(popover_origin(Side::Left, Align::End), Vec2::new(1.0, 1.0));
    }
}
