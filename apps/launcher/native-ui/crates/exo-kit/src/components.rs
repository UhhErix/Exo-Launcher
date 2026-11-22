use crate::tokens::{
    BG, FG, GLASS_CONTROL_SIGMA, GLASS_PANEL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD,
    RADIUS_PANEL, RADIUS_PILL, SPRING_QUICK, TRANSPARENT, bad, fg_muted, glass_control_fill,
    glass_panel_fill, glass_rim, good, hover_fill, pressed_fill, subtle_fill, type_control,
    type_section, type_small,
};
use rune::{Color, El, col, el, row, text};

pub fn glass_capsule(key: &'static str) -> El {
    row()
        .key(key)
        .h(40.0)
        .px(4.0)
        .gap(2.0)
        .center_y()
        .radius(RADIUS_PILL)
        .backdrop_blur(GLASS_CONTROL_SIGMA)
        .fill(glass_control_fill())
        .border(1.0, glass_rim())
}

pub fn glass_panel(key: &'static str, width: f32) -> El {
    rune::col()
        .key(key)
        .w(width)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .hit()
        .backdrop_blur(GLASS_PANEL_SIGMA)
        .fill(glass_panel_fill())
        .border(1.0, glass_rim())
        .shadow(16.0, 42.0, BG.alpha(0.65))
}

pub fn primary_button(key: &'static str, label: &'static str, icon: Option<El>) -> El {
    let mut btn = row()
        .key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(34.0)
        .px(16.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(FG)
        .hover(FG.alpha(0.92))
        .press(FG.alpha(0.84))
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label).font(type_control()).color(PRIMARY_FG))
}

pub fn glass_button(key: &'static str, label: &'static str, icon: Option<El>) -> El {
    let mut btn = row()
        .key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(34.0)
        .px(14.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border(1.0, glass_rim())
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label).font(type_control()).color(FG))
}

pub fn filter_pill(key: &'static str, label: String, active: bool) -> El {
    el().key(key)
        .role("tab")
        .label(label.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(30.0)
        .px(12.0)
        .radius(RADIUS_PILL)
        .center()
        .fill(if active { FG } else { subtle_fill() })
        .hover(if active { FG } else { hover_fill() })
        .press(if active { FG } else { pressed_fill() })
        .motion(SPRING_QUICK)
        .child(
            text(label)
                .font(type_control())
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
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(12.0)
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
                .font(type_control())
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
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(34.0)
        .px(14.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(bad())
        .hover(bad().alpha(0.90))
        .press(bad().alpha(0.80))
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
        .child(text(format!("-{discount}%")).font(type_small()).color(PRIMARY_FG))
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
    let knob = el()
        .size(16.0, 16.0)
        .radius(8.0)
        .fill(if on { PRIMARY_FG } else { FG })
        .absolute(if on { 18.0 } else { 2.0 }, 2.0)
        .motion(SPRING_QUICK);

    el().key(key)
        .role("switch")
        .label(label)
        .checked(on)
        .focusable(true)
        .hit()
        .press_on_down()
        .size(36.0, 20.0)
        .radius(10.0)
        .fill(if on { FG } else { Color::WHITE.alpha(0.18) })
        .motion(SPRING_QUICK)
        .child(knob)
}

pub fn progress_bar(width: f32, fraction: f32) -> El {
    let fill_w = (width * fraction.clamp(0.0, 1.0)).max(2.0);
    el().w(width)
        .h(4.0)
        .radius(2.0)
        .fill(Color::WHITE.alpha(0.16))
        .child(el().w(fill_w).h(4.0).radius(2.0).fill(FG))
}

