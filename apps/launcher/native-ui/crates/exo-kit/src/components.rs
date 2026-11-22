use crate::tokens::{
    BG, FG, GLASS_CONTROL_SIGMA, GLASS_PANEL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD,
    RADIUS_PANEL, RADIUS_PILL, SPRING_MOVE, SPRING_QUICK, TRANSPARENT, bad, fg_muted,
    glass_control_fill, glass_outer_edge, glass_panel_fill, glass_rim, glass_rim_bottom,
    glass_rim_top, glass_specular_sheen, glass_still_control_fill, glass_still_panel_fill, good,
    hairline, hover_fill, pressed_fill, subtle_fill, track_fill, type_control, type_section,
    type_small, warn,
};
use rune::{Color, El, Pixels, SurfaceFit, col, el, row, text};

pub fn glass_capsule(key: &'static str) -> El {
    glass_capsule_opt(key, true)
}

pub fn glass_capsule_opt(key: &'static str, blur: bool) -> El {
    let mut capsule = row()
        .key(key)
        .h(36.0)
        .px(3.0)
        .gap(2.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if blur {
            glass_control_fill()
        } else {
            glass_still_control_fill()
        })
        .linear_gradient(std::f32::consts::PI, glass_specular_sheen())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .shadow(1.0, 3.0, glass_outer_edge())
        .shadow(6.0, 18.0, BG.alpha(0.45));
    if blur {
        capsule = capsule.backdrop_blur(GLASS_CONTROL_SIGMA);
    }
    capsule
}

pub fn glass_panel(key: &'static str, width: f32) -> El {
    glass_panel_opt(key, width, true)
}

pub fn glass_panel_opt(key: &'static str, width: f32, blur: bool) -> El {
    let mut panel = rune::col()
        .key(key)
        .w(width)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .hit()
        .fill(if blur {
            glass_panel_fill()
        } else {
            glass_still_panel_fill()
        })
        .linear_gradient(std::f32::consts::PI, glass_specular_sheen())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .shadow(1.0, 3.0, glass_outer_edge())
        .shadow(12.0, 32.0, BG.alpha(0.55));
    if blur {
        panel = panel.backdrop_blur(GLASS_PANEL_SIGMA);
    }
    panel
}

pub fn amoled_card(radius: f32) -> El {
    col()
        .radius(radius)
        .continuous_corners()
        .fill(BG)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
}

pub fn exo_play_button(key: &'static str, label: &'static str, icon: Option<El>) -> El {
    let mut btn = row()
        .key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
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
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label).font(type_section()).color(PRIMARY_FG))
}

pub fn glass_icon_circle(key: &'static str, label: &'static str, size: f32, icon: El) -> El {
    el().key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(size, size)
        .center()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK)
        .child(icon)
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
        .h(32.0)
        .px(12.0)
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
        .h(32.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
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

pub fn nav_tab(key: &'static str, label: &'static str, active: bool, icon: El) -> El {
    row()
        .key(key)
        .role("tab")
        .label(label)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(30.0)
        .px(13.0)
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
    let knob = el()
        .size(20.0, 20.0)
        .radius(10.0)
        .fill(FG)
        .shadow(1.0, 3.0, BG.alpha(0.35))
        .absolute(if on { 18.0 } else { 2.0 }, 2.0)
        .motion(SPRING_MOVE);

    el().key(key)
        .role("switch")
        .label(label)
        .checked(on)
        .focusable(true)
        .hit()
        .press_on_down()
        .size(40.0, 24.0)
        .radius(RADIUS_PILL)
        .fill(if on { good() } else { track_fill() })
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
        .border(1.0, hairline())
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

pub fn cover_image(
    pixels: &Pixels,
    container_w: f32,
    container_h: f32,
    anchor_x: f32,
    anchor_y: f32,
) -> El {
    let (iw, ih) = pixels.size();
    if iw == 0 || ih == 0 || container_w <= 0.0 || container_h <= 0.0 {
        return el()
            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
            .pixels_fit(pixels, SurfaceFit::Fill);
    }
    let iw = iw as f32;
    let ih = ih as f32;
    let fit = (container_w / iw).max(container_h / ih);
    let draw_w = iw * fit;
    let draw_h = ih * fit;
    let left = (container_w - draw_w) * anchor_x;
    let top = (container_h - draw_h) * anchor_y;
    el().pin(Some(left), Some(top), None, None)
        .size(draw_w, draw_h)
        .pixels_fit(pixels, SurfaceFit::Fill)
}
