use rune::color::srgb_decode;
use rune::{Color, Spring, TextStyle};
use std::cell::Cell;

pub const GEIST_FONT: &[u8] = include_bytes!("../assets/fonts/Geist-Variable.ttf");
pub const GEIST_ITALIC_FONT: &[u8] = include_bytes!("../assets/fonts/Geist-Italic-Variable.ttf");
pub const GEIST_MONO_FONT: &[u8] = include_bytes!("../assets/fonts/GeistMono-Variable.ttf");
pub const GEIST_MONO_ITALIC_FONT: &[u8] =
    include_bytes!("../assets/fonts/GeistMono-Italic-Variable.ttf");

pub const FONTS: &[&[u8]] = &[
    GEIST_FONT,
    GEIST_ITALIC_FONT,
    GEIST_MONO_FONT,
    GEIST_MONO_ITALIC_FONT,
];
pub const FONT_FAMILIES: (&str, &str) = ("Geist", "Geist Mono");

thread_local! {
    static TEXT_SCALE: Cell<f32> = const { Cell::new(1.0) };
}

pub fn set_text_scale(scale: f32) {
    let clamped = scale.clamp(0.75, 1.5);
    TEXT_SCALE.with(|cell| cell.set(clamped));
}

pub fn text_scale() -> f32 {
    TEXT_SCALE.with(Cell::get)
}

pub fn scaled(px: f32) -> f32 {
    px * text_scale()
}

pub fn srgb(r: u8, g: u8, b: u8, a: f32) -> Color {
    Color {
        r: srgb_decode(r as f32 / 255.0),
        g: srgb_decode(g as f32 / 255.0),
        b: srgb_decode(b as f32 / 255.0),
        a,
    }
}

pub fn hex(rgb: u32, a: f32) -> Color {
    srgb(
        ((rgb >> 16) & 0xff) as u8,
        ((rgb >> 8) & 0xff) as u8,
        (rgb & 0xff) as u8,
        a,
    )
}

pub const BG: Color = Color::BLACK;
pub const FG: Color = Color::WHITE;
pub const PRIMARY_FG: Color = Color::BLACK;
pub const TRANSPARENT: Color = Color::TRANSPARENT;

pub fn fg_muted() -> Color {
    hex(0xb4b4b4, 1.0)
}

pub fn fg_faint() -> Color {
    hex(0x9a9a9a, 1.0)
}

pub fn good() -> Color {
    hex(0x3dd68c, 1.0)
}

pub fn warn() -> Color {
    hex(0xffb340, 1.0)
}

pub fn bad() -> Color {
    hex(0xff5c5c, 1.0)
}

pub fn glass_control_fill() -> Color {
    BG.alpha(0.54)
}

pub fn glass_bar_tint() -> Color {
    BG.alpha(0.36)
}

pub fn glass_panel_tint() -> Color {
    BG.alpha(0.54)
}

pub fn glass_panel_fill() -> Color {
    BG.alpha(0.76)
}

pub fn glass_plate_fill() -> Color {
    BG.alpha(0.72)
}

pub fn glass_still_control_fill() -> Color {
    srgb(8, 8, 8, 0.94)
}

pub fn glass_still_panel_fill() -> Color {
    srgb(4, 4, 4, 0.97)
}

pub fn glass_panel_shadow_color() -> Color {
    BG.alpha(0.64)
}

pub fn glass_rim_top() -> Color {
    Color::WHITE.alpha(0.11)
}

pub fn glass_rim_bottom() -> Color {
    Color::WHITE.alpha(0.03)
}

pub fn glass_rim() -> Color {
    Color::WHITE.alpha(0.075)
}

pub fn card_border() -> Color {
    Color::WHITE.alpha(0.08)
}

pub fn hairline() -> Color {
    Color::WHITE.alpha(0.065)
}

pub fn float_line() -> Color {
    Color::WHITE.alpha(0.055)
}

pub fn hover_fill() -> Color {
    Color::WHITE.alpha(0.07)
}

pub fn pressed_fill() -> Color {
    Color::WHITE.alpha(0.10)
}

pub fn selected_fill() -> Color {
    Color::WHITE.alpha(0.105)
}

pub fn track_fill() -> Color {
    Color::WHITE.alpha(0.15)
}

pub fn scrim_fill() -> Color {
    BG.alpha(0.76)
}

pub fn surface_card() -> Color {
    Color::WHITE.alpha(0.02)
}

pub fn surface_elevated() -> Color {
    BG
}

pub fn surface_elevated_hi() -> Color {
    srgb(10, 10, 10, 1.0)
}

pub fn surface_sidebar() -> Color {
    BG
}

pub fn art_surface_fill() -> Color {
    Color::WHITE.alpha(0.03)
}

pub fn subtle_fill() -> Color {
    Color::WHITE.alpha(0.04)
}

pub const RADIUS_SMALL: f32 = 8.0;
pub const RADIUS_CARD: f32 = 14.0;
pub const RADIUS_PANEL: f32 = 18.0;
pub const RADIUS_SHEET: f32 = 24.0;
pub const RADIUS_PILL: f32 = 999.0;
pub const GAP: f32 = 10.0;
pub const TITLEBAR_HEIGHT: f32 = 52.0;

pub const GLASS_CONTROL_SIGMA: f32 = 10.0;
pub const GLASS_PANEL_SIGMA: f32 = 16.0;
pub const GLASS_CONTAINER_SPACING: f32 = 10.0;
pub const EXO_MAX_BACKDROP_BLURS: usize = 16;
pub const PRESS_SCALE: f32 = 0.97;

pub const SPRING_PRESS: Spring = Spring::new(0.09, 0.0);
pub const SPRING_QUICK: Spring = Spring::new(0.12, 0.0);
pub const SPRING_BASE: Spring = Spring::new(0.16, 0.0);
pub const SPRING_SNAPPY: Spring = Spring::new(0.18, 0.12);
pub const SPRING_POPOVER: Spring = Spring::new(0.19, 0.08);
pub const SPRING_MOVE: Spring = Spring::new(0.22, 0.0);
pub const SPRING_SMOOTH: Spring = Spring::new(0.26, 0.0);
pub const SPRING_FLIGHT: Spring = Spring::new(0.30, 0.0);
pub const SPRING_INTRO: Spring = Spring::new(0.32, 0.12);

pub fn app_icon_top() -> Color {
    hex(0x222226, 1.0)
}

pub fn app_icon_mid() -> Color {
    hex(0x0c0c0e, 1.0)
}

pub fn app_icon_bottom() -> Color {
    hex(0x040405, 1.0)
}

pub fn type_label() -> TextStyle {
    TextStyle::new(scaled(10.0), 500.0)
}

pub fn type_small() -> TextStyle {
    TextStyle::new(scaled(12.0), 400.0)
}

pub fn type_body() -> TextStyle {
    TextStyle::new(scaled(13.0), 400.0)
}

pub fn type_control() -> TextStyle {
    TextStyle::new(scaled(13.0), 500.0)
}

pub fn type_section() -> TextStyle {
    TextStyle::new(scaled(13.0), 600.0)
}

pub fn type_read() -> TextStyle {
    let size = scaled(15.0);
    TextStyle::new(size, 400.0).lh(size * 1.375)
}

pub fn type_title() -> TextStyle {
    TextStyle::new(scaled(20.0), 600.0)
}

pub fn type_page() -> TextStyle {
    TextStyle::new(scaled(28.0), 650.0)
}

pub fn type_first_run() -> TextStyle {
    TextStyle::new(scaled(32.0), 700.0).letter_spacing(-0.025)
}

pub const FOLDER_COLOR_IDS: [&str; 8] = [
    "gray", "red", "orange", "yellow", "green", "teal", "blue", "pink",
];

pub fn normalize_folder_color(color: Option<&str>) -> &'static str {
    match color {
        Some("red") => "red",
        Some("orange") => "orange",
        Some("yellow") => "yellow",
        Some("green") => "green",
        Some("teal") => "teal",
        Some("blue") => "blue",
        Some("pink") => "pink",
        _ => "gray",
    }
}

pub fn folder_color_rgb(color: &str) -> (u8, u8, u8) {
    match color {
        "red" => (255, 69, 58),
        "orange" => (255, 159, 10),
        "yellow" => (255, 214, 10),
        "green" => (48, 209, 88),
        "teal" => (64, 200, 224),
        "blue" => (10, 132, 255),
        "pink" => (255, 55, 95),
        _ => (142, 142, 147),
    }
}

pub fn folder_color_swatch(color: &str, alpha: f32) -> Color {
    let (r, g, b) = folder_color_rgb(color);
    srgb(r, g, b, alpha)
}

pub fn folder_color_mix_black(color: &str, pct: f32) -> Color {
    let (r, g, b) = folder_color_rgb(color);
    let k = pct.clamp(0.0, 1.0);
    srgb(
        (r as f32 * k).round() as u8,
        (g as f32 * k).round() as u8,
        (b as f32 * k).round() as u8,
        1.0,
    )
}

pub fn folder_color_label(color: &str) -> &'static str {
    match color {
        "red" => "Red",
        "orange" => "Orange",
        "yellow" => "Yellow",
        "green" => "Green",
        "teal" => "Teal",
        "blue" => "Blue",
        "pink" => "Pink",
        _ => "Gray",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn embedded_geist_fonts_resolve_regular_italic_weights_and_mono() {
        let _guard = rune::text::with_fonts(FONTS, FONT_FAMILIES);
        let (reg_adv, _, reg_mono) =
            rune::text::ascii_metrics(TextStyle::new(14.0, 400.0)).expect("Geist regular");
        assert!(!reg_mono);
        let narrow = reg_adv[(b'i' - 32) as usize];
        let wide = reg_adv[(b'W' - 32) as usize];
        assert!(
            (wide - narrow).abs() > 1.0,
            "regular font should be proportional: i={narrow}, W={wide}"
        );

        let (italic_adv, _, italic_mono) =
            rune::text::ascii_metrics(TextStyle::new(14.0, 400.0).italic()).expect("Geist Italic");
        assert!(!italic_mono);
        assert!(italic_adv[(b'a' - 32) as usize] > 0.0);

        let (mono_adv, _, is_mono) =
            rune::text::ascii_metrics(TextStyle::new(14.0, 400.0).mono()).expect("Geist Mono");
        assert!(is_mono);
        let mono_i = mono_adv[(b'i' - 32) as usize];
        let mono_w = mono_adv[(b'W' - 32) as usize];
        assert!(
            (mono_i - mono_w).abs() < 0.05,
            "mono font should be fixed-pitch: i={mono_i}, W={mono_w}"
        );

        let (mono_italic_adv, _, is_mono_italic) =
            rune::text::ascii_metrics(TextStyle::new(14.0, 400.0).mono().italic())
                .expect("Geist Mono Italic");
        assert!(is_mono_italic);
        assert!(mono_italic_adv[(b'a' - 32) as usize] > 0.0);

        let light = rune::text::primary_extents(TextStyle::new(14.0, 300.0)).expect("weight 300");
        let bold = rune::text::primary_extents(TextStyle::new(14.0, 700.0)).expect("weight 700");
        assert!(light.0 > 0.0 && bold.0 > 0.0);
        assert!(type_read() == TextStyle::new(scaled(15.0), 400.0).lh(scaled(15.0) * 1.375));
    }
}
