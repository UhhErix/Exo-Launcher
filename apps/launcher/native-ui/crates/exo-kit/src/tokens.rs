use rune::color::srgb_decode;
use rune::{Color, Spring, TextStyle};

pub const GOEIST_FONT: &[u8] =
    include_bytes!("../../../../ExoLauncher/Assets/Fonts/Goeist-Variable.ttf");

pub const FONTS: &[&[u8]] = &[GOEIST_FONT];
pub const FONT_FAMILIES: (&str, &str) = ("Goeist", "ui-monospace");

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
    BG.alpha(0.36)
}

pub fn glass_panel_fill() -> Color {
    BG.alpha(0.72)
}

pub fn glass_rim_top() -> Color {
    Color::WHITE.alpha(0.17)
}

pub fn glass_rim_bottom() -> Color {
    Color::WHITE.alpha(0.03)
}

pub fn glass_rim() -> Color {
    Color::WHITE.alpha(0.09)
}

pub fn card_border() -> Color {
    Color::WHITE.alpha(0.08)
}

pub fn hairline() -> Color {
    Color::WHITE.alpha(0.07)
}

pub fn hover_fill() -> Color {
    Color::WHITE.alpha(0.08)
}

pub fn pressed_fill() -> Color {
    Color::WHITE.alpha(0.14)
}

pub fn subtle_fill() -> Color {
    Color::WHITE.alpha(0.06)
}

pub const RADIUS_SMALL: f32 = 8.0;
pub const RADIUS_CARD: f32 = 14.0;
pub const RADIUS_PANEL: f32 = 18.0;
pub const RADIUS_SHEET: f32 = 24.0;
pub const RADIUS_PILL: f32 = 999.0;
pub const GAP: f32 = 10.0;
pub const TITLEBAR_HEIGHT: f32 = 52.0;

pub const GLASS_CONTROL_SIGMA: f32 = 14.0;
pub const GLASS_PANEL_SIGMA: f32 = 20.0;
pub const PRESS_SCALE: f32 = 0.97;

pub const SPRING_PRESS: Spring = Spring::new(0.09, 0.0);
pub const SPRING_QUICK: Spring = Spring::new(0.12, 0.0);
pub const SPRING_BASE: Spring = Spring::new(0.16, 0.0);
pub const SPRING_MOVE: Spring = Spring::new(0.22, 0.0);
pub const SPRING_FLIGHT: Spring = Spring::new(0.30, 0.0);

pub fn type_label() -> TextStyle {
    TextStyle::new(10.0, 500.0)
}

pub fn type_small() -> TextStyle {
    TextStyle::new(12.0, 400.0)
}

pub fn type_body() -> TextStyle {
    TextStyle::new(13.0, 400.0)
}

pub fn type_control() -> TextStyle {
    TextStyle::new(13.0, 500.0)
}

pub fn type_section() -> TextStyle {
    TextStyle::new(13.0, 600.0)
}

pub fn type_title() -> TextStyle {
    TextStyle::new(20.0, 650.0)
}

pub fn type_page() -> TextStyle {
    TextStyle::new(28.0, 650.0)
}
