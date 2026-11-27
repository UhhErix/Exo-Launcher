use crate::discord_admin::{self, PAGE_BROWSE, PAGE_GUIDE, PAGE_MEMBERS};
use crate::discord_chat;
use crate::discord_md;
use crate::discord_settings;
use crate::discord_state::{DiscordFriendsTab, DiscordPlace, DiscordSettingsPage};
use crate::discord_threads::parse_hex_color;
use crate::discord_voice;
use crate::state::{AppState, ContextMenuTarget};
use exo_kit::{
    BG, FG, GAP, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_BASE, SPRING_QUICK, TRANSPARENT, bad, fg_faint, fg_muted, glass_button, glass_rim,
    glass_rim_bottom, glass_rim_top, good, hairline, hex, hover_fill, icon_bookmark, icon_chat,
    icon_check, icon_chevron_down, icon_chevron_right, icon_close, icon_folder, icon_gamepad,
    icon_grid, icon_hash, icon_headphones, icon_headphones_off, icon_home, icon_lock, icon_mic,
    icon_mic_off, icon_more_horiz, icon_music, icon_phone, icon_phone_off, icon_plus, icon_refresh,
    icon_search, icon_settings, icon_sparkles, icon_trophy, icon_user_plus, icon_users, icon_video,
    icon_volume, pressed_fill, primary_button, qr_code_view, selected_fill, srgb, subtle_fill,
    type_body, type_control, type_label, type_page, type_section, type_small, type_title, warn,
};
use exo_rpc::discord_types::{DiscordGuildTag, DiscordLook, DiscordNameplate, DiscordRestricted};
use exo_rpc::{
    DiscordChannel, DiscordDm, DiscordFriend, DiscordGuild, DiscordMemberItem, DiscordRailItem,
};
use rune::{
    Color, Cursor, El, GrowthSpec, ScrollSpec, Shape, Spring, SpringPair, SurfaceFit, TextStyle,
    Transition, Tx, canvas, col, edit_access, el, row, text, text_input,
};
use serde_json::json;
use std::borrow::Cow;
use std::collections::{HashMap, HashSet};
use std::f32::consts::FRAC_PI_2;
use std::rc::Rc;
use std::time::{SystemTime, UNIX_EPOCH};

fn inline_bar_transition(dy: f32) -> Transition {
    let enter = Transition::offset(0.0, dy).combined(Transition::opacity());
    Transition::asymmetric(enter, Transition::opacity())
        .springs(SpringPair::new(SPRING_BASE, SPRING_QUICK))
}

pub(crate) fn ui_scale(s: &AppState) -> f32 {
    match s.settings.text_size.as_deref() {
        Some("small") => 0.9,
        Some("large") => 1.14,
        _ => 1.0,
    }
}

fn font_px(size: f32, weight: f32) -> TextStyle {
    TextStyle::new(size, weight)
}

fn font_px_tnum(size: f32, weight: f32) -> TextStyle {
    let mut ts = TextStyle::new(size, weight);
    ts.features = "'tnum' 1";
    ts
}

fn glassy_ring(node: El) -> El {
    node.border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
}

fn panel_frame(node: El) -> El {
    node.radius(RADIUS_PANEL)
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .fill(exo_kit::surface_card())
        .border(1.0, exo_kit::card_border())
}

pub(crate) fn status_color(status: &str) -> Color {
    match status {
        "online" => good(),
        "idle" => warn(),
        "dnd" => bad(),
        _ => hex(0x6e6e73, 1.0),
    }
}

pub(crate) fn status_label(status: &str) -> &'static str {
    match status {
        "online" => "Online",
        "idle" => "Idle",
        "dnd" => "Do not disturb",
        "invisible" => "Invisible",
        _ => "Offline",
    }
}

fn initials(name: &str) -> String {
    let mut out = String::new();
    for word in name.split_whitespace().take(2) {
        if let Some(ch) = word.chars().next() {
            out.push(ch.to_ascii_uppercase());
        }
    }
    if out.is_empty() { "D".into() } else { out }
}

fn monogram_fill(seed: &str) -> Color {
    let mut h = 2166136261u32;
    for b in seed.bytes() {
        h ^= b as u32;
        h = h.wrapping_mul(16777619);
    }
    let palette = [0x1d232b, 0x221f29, 0x1a2622, 0x27221b, 0x241d22, 0x1c2529];
    hex(palette[(h as usize) % palette.len()], 1.0)
}

fn lift_rgb_f32(v: u32) -> [f32; 3] {
    let mut r = ((v >> 16) & 0xff) as f32 / 255.0;
    let mut g = ((v >> 8) & 0xff) as f32 / 255.0;
    let mut b = (v & 0xff) as f32 / 255.0;
    let lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
    if lum < 0.38 {
        let boost = ((0.38 - lum) / (1.0 - lum).max(0.01)).clamp(0.0, 0.78);
        r += (1.0 - r) * boost;
        g += (1.0 - g) * boost;
        b += (1.0 - b) * boost;
    }
    [r, g, b]
}

fn rgb_to_color([r, g, b]: [f32; 3]) -> Color {
    srgb(
        (r * 255.0).round() as u8,
        (g * 255.0).round() as u8,
        (b * 255.0).round() as u8,
        1.0,
    )
}

fn lift_rgb(v: u32) -> Color {
    rgb_to_color(lift_rgb_f32(v))
}

fn tint_toward_white([r, g, b]: [f32; 3], amount: f32) -> Color {
    rgb_to_color([
        r + (1.0 - r) * amount,
        g + (1.0 - g) * amount,
        b + (1.0 - b) * amount,
    ])
}

fn lift_role_color(raw: Option<&str>, offline: bool) -> Color {
    if offline {
        return fg_faint();
    }
    let Some(h) = raw.and_then(|s| s.strip_prefix('#')) else {
        return FG;
    };
    if h.len() != 6 {
        return FG;
    }
    let Ok(v) = u32::from_str_radix(h, 16) else {
        return FG;
    };
    lift_rgb(v)
}

fn styled_name_text(node: El, look: Option<&DiscordLook>, fallback: Color) -> El {
    let Some(ns) = look.and_then(|l| l.name_style.as_ref()) else {
        return node.color(fallback);
    };
    if ns.colors.is_empty() {
        return node.color(fallback);
    }
    let lifted: Vec<Color> = ns.colors.iter().take(15).map(|&c| lift_rgb(c)).collect();
    let main = lifted[0];
    match ns.effect {
        2 | 6 if lifted.len() >= 2 => {
            let denom = (lifted.len() - 1) as f32;
            let stops: Vec<(f32, Color)> = lifted
                .iter()
                .enumerate()
                .map(|(i, &c)| (0.10 + 0.80 * (i as f32) / denom, c))
                .collect();
            node.color(main)
                .text_gradient(3.0 * std::f32::consts::FRAC_PI_4, stops)
        }
        7 if lifted.len() >= 2 => {
            let n = lifted.len();
            let stops: Vec<(f32, Color)> = (0..=n)
                .map(|i| (i as f32 / n as f32, lifted[i % n]))
                .collect();
            node.color(main)
                .text_gradient(std::f32::consts::FRAC_PI_2, stops)
        }
        4 => {
            let base = lift_rgb_f32(ns.colors[0]);
            let light1 = tint_toward_white(base, 0.25);
            let light2 = tint_toward_white(base, 0.60);
            node.color(main).text_gradient(
                std::f32::consts::PI,
                [(0.0, FG), (0.32, light2), (0.60, light1), (1.0, main)],
            )
        }
        5 => {
            let base = lift_rgb_f32(ns.colors[0]);
            let light1 = tint_toward_white(base, 0.25);
            node.color(main).text_gradient(
                5.0 * std::f32::consts::FRAC_PI_4,
                [(0.0, light1), (0.12, light1), (0.40, main), (1.0, main)],
            )
        }
        _ => node.color(main),
    }
}

fn nameplate_palette_rgb(palette: &str) -> Option<u32> {
    match palette {
        "crimson" => Some(0x900007),
        "berry" => Some(0x893A99),
        "sky" => Some(0x0080B7),
        "teal" => Some(0x086460),
        "forest" => Some(0x2D5401),
        "bubble_gum" => Some(0xDC3E97),
        "violet" => Some(0x730BC8),
        "cobalt" => Some(0x0131C2),
        "clover" => Some(0x047B20),
        "lemon" => Some(0xF6CD12),
        "white" => Some(0xFFFFFF),
        "black" => Some(0x000000),
        _ => None,
    }
}

pub(crate) fn decoration_url(look: Option<&DiscordLook>) -> Option<String> {
    let dec = look?.decoration.as_ref()?;
    if let Some(ref sku) = dec.sku_id
        && !sku.is_empty()
    {
        return Some(format!(
            "https://cdn.discordapp.com/media/v1/collectibles-shop/{sku}/static"
        ));
    }
    if dec.asset.is_empty() {
        return None;
    }
    Some(format!(
        "https://cdn.discordapp.com/avatar-decoration-presets/{}.png?size=64&passthrough=false",
        dec.asset
    ))
}

pub(crate) fn nameplate_static_url(plate: &DiscordNameplate) -> Option<String> {
    if plate.sku_id.is_empty() {
        return None;
    }
    Some(format!(
        "https://cdn.discordapp.com/media/v1/collectibles-shop/{}/static",
        plate.sku_id
    ))
}

pub(crate) fn clan_badge_url(tag: &DiscordGuildTag) -> Option<String> {
    let badge = tag.badge.as_deref()?;
    if tag.guild_id.is_empty() || badge.is_empty() {
        return None;
    }
    Some(format!(
        "https://cdn.discordapp.com/clan-badges/{}/{badge}.png?size=16",
        tag.guild_id
    ))
}

fn icon_strokes(size: f32, lines: &[&[[f32; 2]]], color: Color) -> El {
    let width = 1.6;
    let mut shape = Shape::new().view_box(24.0, 24.0);
    for pts in lines {
        shape = shape.stroke(pts.to_vec(), width, color);
    }
    canvas(shape).size(size, size)
}

fn icon_shop(size: f32, color: Color) -> El {
    icon_strokes(
        size,
        &[
            &[
                [6.0, 2.0],
                [3.0, 6.0],
                [3.0, 20.0],
                [3.6, 21.4],
                [5.0, 22.0],
                [19.0, 22.0],
                [20.4, 21.4],
                [21.0, 20.0],
                [21.0, 6.0],
                [18.0, 2.0],
                [6.0, 2.0],
            ],
            &[[3.0, 6.0], [21.0, 6.0]],
            &[
                [16.0, 10.0],
                [15.6, 12.0],
                [14.2, 13.4],
                [12.0, 14.0],
                [9.8, 13.4],
                [8.4, 12.0],
                [8.0, 10.0],
            ],
        ],
        color,
    )
}

fn icon_inbox(size: f32, color: Color) -> El {
    icon_strokes(
        size,
        &[
            &[
                [22.0, 12.0],
                [18.5, 5.1],
                [17.2, 4.0],
                [6.8, 4.0],
                [5.5, 5.1],
                [2.0, 12.0],
                [2.0, 18.0],
                [3.0, 19.5],
                [4.0, 20.0],
                [20.0, 20.0],
                [21.0, 19.5],
                [22.0, 18.0],
                [22.0, 12.0],
            ],
            &[
                [2.0, 12.0],
                [8.0, 12.0],
                [9.5, 15.0],
                [14.5, 15.0],
                [16.0, 12.0],
                [22.0, 12.0],
            ],
        ],
        color,
    )
}

fn icon_tv(size: f32, color: Color) -> El {
    icon_strokes(
        size,
        &[
            &[
                [2.0, 7.0],
                [22.0, 7.0],
                [22.0, 20.0],
                [2.0, 20.0],
                [2.0, 7.0],
            ],
            &[[17.0, 2.0], [12.0, 7.0], [7.0, 2.0]],
        ],
        color,
    )
}

fn activity_icon(kind: Option<i32>, size: f32, color: Color) -> Option<El> {
    match kind {
        Some(0) => Some(icon_gamepad(size, color)),
        Some(1) => Some(icon_video(size, color)),
        Some(2) => Some(icon_music(size, color)),
        Some(3) => Some(icon_tv(size, color)),
        Some(5) => Some(icon_trophy(size, color)),
        _ => None,
    }
}

fn activity_words(friend: &DiscordFriend) -> String {
    if friend.status == "offline" {
        return status_label(&friend.status).into();
    }
    if let Some(ref act) = friend.activity {
        if friend.activity_type == Some(2)
            && let Some(ref rich) = friend.rich
            && let Some(ref details) = rich.details
        {
            let first_artist = rich
                .state
                .as_deref()
                .and_then(|s| s.split(';').next())
                .map(str::trim)
                .filter(|s| !s.is_empty());
            if let Some(artist) = first_artist {
                return format!("{details} · {artist}");
            }
            return details.clone();
        }
        return act.clone();
    }
    if let Some(ref prep) = friend.prep {
        return match prep.kind.as_str() {
            "installing" => match prep.percent {
                Some(pct) => format!("Installing {} {}%", prep.title, pct),
                None => format!("Installing {}", prep.title),
            },
            "compiling" => format!("Compiling shaders for {}", prep.title),
            _ => format!("Ready to play {}", prep.title),
        };
    }
    status_label(&friend.status).into()
}

#[derive(Clone, Copy)]
struct LocalDate {
    year: i32,
    month: u32,
    day: u32,
    wday: u32,
    hour: u32,
    min: u32,
    day_index: i64,
}

#[cfg(target_env = "msvc")]
fn to_local_date(secs: i64) -> LocalDate {
    #[repr(C)]
    #[derive(Default)]
    struct Tm {
        tm_sec: i32,
        tm_min: i32,
        tm_hour: i32,
        tm_mday: i32,
        tm_mon: i32,
        tm_year: i32,
        tm_wday: i32,
        tm_yday: i32,
        tm_isdst: i32,
    }
    unsafe extern "C" {
        fn _localtime64_s(tm: *mut Tm, time: *const i64) -> i32;
    }
    let mut tm = Tm::default();
    let ok = unsafe { _localtime64_s(&mut tm, &secs) == 0 };
    if !ok {
        return to_utc_date(secs);
    }
    let year = tm.tm_year + 1900;
    let month = (tm.tm_mon + 1).clamp(1, 12) as u32;
    let day = tm.tm_mday.clamp(1, 31) as u32;
    LocalDate {
        year,
        month,
        day,
        wday: tm.tm_wday.rem_euclid(7) as u32,
        hour: tm.tm_hour.clamp(0, 23) as u32,
        min: tm.tm_min.clamp(0, 59) as u32,
        day_index: civil_to_days(year, month, day),
    }
}

#[cfg(not(target_env = "msvc"))]
fn to_local_date(secs: i64) -> LocalDate {
    to_utc_date(secs)
}

fn civil_to_days(year: i32, month: u32, day: u32) -> i64 {
    let y = year as i64 - if month <= 2 { 1 } else { 0 };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = (y - era * 400) as u64;
    let m = month as u64;
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + day as u64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe as i64 - 719468
}

fn to_utc_date(secs: i64) -> LocalDate {
    let z = secs.div_euclid(86_400) + 719468;
    let rem = secs.rem_euclid(86_400) as u32;
    let era = if z >= 0 { z } else { z - 146096 } / 146097;
    let doe = (z - era * 146097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let month = (if mp < 10 { mp + 3 } else { mp - 9 }) as u32;
    let year = (y + if month <= 2 { 1 } else { 0 }) as i32;
    let day_index = secs.div_euclid(86_400);
    let wday = (day_index + 4).rem_euclid(7) as u32;
    LocalDate {
        year,
        month,
        day,
        wday,
        hour: rem / 3600,
        min: (rem % 3600) / 60,
        day_index,
    }
}

fn format_row_time(at_ms: i64) -> String {
    let now_secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(at_ms / 1000);
    let now = to_local_date(now_secs);
    let then = to_local_date(at_ms / 1000);
    let days = now.day_index - then.day_index;
    if days <= 0 {
        let (h12, suffix) = match then.hour {
            0 => (12, "AM"),
            1..=11 => (then.hour, "AM"),
            12 => (12, "PM"),
            h => (h - 12, "PM"),
        };
        return format!("{h12}:{:02} {suffix}", then.min);
    }
    if days == 1 {
        return "Yesterday".into();
    }
    const WDAYS: [&str; 7] = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    const MONTHS: [&str; 12] = [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];
    if days < 7 {
        return WDAYS[(then.wday as usize) % 7].into();
    }
    let mon = MONTHS[((then.month.saturating_sub(1)) as usize) % 12];
    if then.year == now.year {
        format!("{mon} {}", then.day)
    } else {
        format!("{mon} {}, {}", then.day, then.year)
    }
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn avatar_box(
    s: &AppState,
    url: Option<&str>,
    name: &str,
    size: f32,
    radius: f32,
    status: Option<&str>,
    speaking: bool,
    scale: f32,
) -> El {
    avatar_with_look(
        s, url, None, name, size, radius, status, speaking, BG, scale,
    )
}

#[allow(clippy::too_many_arguments)]
fn avatar_with_look(
    s: &AppState,
    url: Option<&str>,
    look: Option<&DiscordLook>,
    name: &str,
    size: f32,
    radius: f32,
    status: Option<&str>,
    speaking: bool,
    ring_bg: Color,
    scale: f32,
) -> El {
    let mut inner = el()
        .size(size, size)
        .radius(radius)
        .clip_rounded(radius)
        .fill(monogram_fill(name))
        .center();
    let avatar_dim = if size <= 56.0 { 64.0 } else { 128.0 };
    if let Some(img) = url.and_then(|u| s.discord_remote_image_sized(u, avatar_dim, scale)) {
        inner = inner.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        let style = if size >= 40.0 {
            font_px(13.0, 600.0)
        } else if size >= 28.0 {
            font_px(12.0, 600.0)
        } else {
            font_px(10.0, 600.0)
        };
        let first = name
            .chars()
            .next()
            .map(|c| c.to_string())
            .unwrap_or_else(|| "?".into());
        inner = inner.child(text(first).font(style).color(FG));
    }
    if speaking {
        inner = inner.border(2.0, good());
    }
    let mut wrap = el().size(size, size).child(inner);
    if let Some(dec_url) = decoration_url(look)
        && let Some(img) = s.discord_remote_image_sized(&dec_url, avatar_dim, scale)
    {
        let dec_sz = (size * 1.2).round();
        let off = -((dec_sz - size) * 0.5);
        wrap = wrap.child(
            el().pin(Some(off), Some(off), None, None)
                .size(dec_sz, dec_sz)
                .pixels_fit(img.pixels(), SurfaceFit::Fit),
        );
    }
    if let Some(st) = status
        && st != "offline"
        && st != "invisible"
    {
        let dot_sz = if size >= 32.0 {
            12.0
        } else {
            (size * 0.34).clamp(9.0, 12.0)
        };
        let border_w = if size >= 32.0 { 3.0 } else { 2.0 };
        wrap = wrap.child(
            el().pin(None, None, Some(-2.0), Some(-2.0))
                .size(dot_sz, dot_sz)
                .radius(dot_sz * 0.5)
                .fill(status_color(st))
                .border(border_w, ring_bg),
        );
    }
    wrap
}

fn mention_badge(count: i32, border_color: Option<Color>, sc: f32) -> El {
    let label = if count > 99 {
        "99+".into()
    } else {
        count.to_string()
    };
    let mut pill = el()
        .h(18.0)
        .min_w(18.0)
        .px(5.0)
        .radius(RADIUS_PILL)
        .fill(bad())
        .center()
        .child(
            text(label)
                .font(font_px_tnum(11.0 * sc, 700.0))
                .color(PRIMARY_FG),
        );
    if let Some(bc) = border_color {
        pill = pill.border(2.0, bc);
    }
    pill
}

fn unread_dot_band(border_color: Color) -> El {
    el().size(12.0, 12.0)
        .radius(6.0)
        .fill(fg_muted())
        .border(2.0, border_color)
}

fn unread_dot_row(color: Color) -> El {
    el().size(8.0, 8.0).radius(4.0).fill(color)
}

fn icon_btn(key: String, label: &'static str, active: bool, danger: bool, icon: El) -> El {
    let fill = if danger {
        bad().alpha(0.20)
    } else if active {
        hover_fill()
    } else {
        TRANSPARENT
    };
    el().key(key)
        .role("button")
        .label(label)
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(32.0, 32.0)
        .radius(RADIUS_PILL)
        .fill(fill)
        .hover(if danger {
            bad().alpha(0.32)
        } else {
            hover_fill()
        })
        .press(pressed_fill())
        .center()
        .motion(SPRING_QUICK)
        .child(icon)
}

fn card_surface() -> Color {
    exo_kit::surface_elevated_hi()
}

fn signed_out_view(s: &AppState) -> El {
    let state = s.discord.status.state.as_str();
    let mut card = panel_frame(
        col()
            .key("dc-signin-card")
            .w(440.0)
            .px(28.0)
            .py(28.0)
            .gap(16.0)
            .center(),
    )
    .child(
        el().size(48.0, 48.0)
            .radius(14.0)
            .fill(subtle_fill())
            .border(1.0, glass_rim())
            .center()
            .child(icon_chat(22.0, FG)),
    )
    .child(text("Discord in Exo").font(type_page()).color(FG))
    .child(
        text("Native client built in Exo's AMOLED shape — friends' games first, voice rooms, DMs, and servers.")
            .font(type_body())
            .color(fg_muted())
            .w_full()
            .wrap(),
    );

    if let Some(ref step) = s.discord.signin {
        match step.state.as_str() {
            "waiting" => {
                if let Some(ref url) = step.url {
                    card = card.child(qr_code_view(url, 208.0)).child(
                        text("Scan with the Discord app on your phone")
                            .font(type_control())
                            .color(FG),
                    );
                } else {
                    card = card.child(
                        text(
                            step.message
                                .clone()
                                .unwrap_or_else(|| "Generating sign-in code…".into()),
                        )
                        .font(type_body())
                        .color(fg_muted()),
                    );
                }
                card = card.child(
                    glass_button("dc-signin-cancel", "Cancel", None)
                        .on_press(|s: &mut AppState| s.cancel_discord_signin()),
                );
            }
            "scanned" => {
                let who = step.user.as_deref().unwrap_or("your account");
                card = card
                    .child(
                        row()
                            .px(14.0)
                            .py(10.0)
                            .gap(8.0)
                            .radius(RADIUS_CARD)
                            .fill(good().alpha(0.14))
                            .border(1.0, good().alpha(0.35))
                            .center_y()
                            .child(el().size(8.0, 8.0).radius(4.0).fill(good()))
                            .child(
                                text(format!("Confirm sign-in on your phone for {who}"))
                                    .font(type_control())
                                    .color(FG)
                                    .wrap(),
                            ),
                    )
                    .child(
                        glass_button("dc-signin-cancel", "Cancel", None)
                            .on_press(|s: &mut AppState| s.cancel_discord_signin()),
                    );
            }
            _ => {
                let msg = step
                    .message
                    .clone()
                    .unwrap_or_else(|| "Sign-in did not complete.".into());
                card = card
                    .child(text(msg).font(type_body()).color(bad()).w_full().wrap())
                    .child(
                        primary_button("dc-signin-retry", "Try again", None)
                            .on_press(|s: &mut AppState| s.start_discord_signin()),
                    );
            }
        }
    } else if state == "connecting" || (s.discord.status.signed_in && state != "error") {
        card = card
            .child(
                row()
                    .px(14.0)
                    .py(10.0)
                    .gap(8.0)
                    .radius(RADIUS_PILL)
                    .fill(subtle_fill())
                    .center_y()
                    .child(el().size(8.0, 8.0).radius(4.0).fill(warn()))
                    .child(
                        text("Connecting to Discord…")
                            .font(type_control())
                            .color(FG),
                    ),
            )
            .child(
                glass_button(
                    "dc-reconnect",
                    "Refresh status",
                    Some(icon_refresh(14.0, FG)),
                )
                .on_press(|s: &mut AppState| s.load_discord_room()),
            );
    } else {
        if let Some(ref err) = s.discord.status.message {
            card = card.child(
                text(err.clone())
                    .font(type_small())
                    .color(bad())
                    .w_full()
                    .wrap(),
            );
        }
        let mut actions = row().gap(10.0).center_y().child(
            primary_button("dc-signin-start", "Sign in with QR", None)
                .on_press(|s: &mut AppState| s.start_discord_signin()),
        );
        if state == "error" || state == "off" {
            actions = actions.child(
                glass_button("dc-connect-btn", "Reconnect", Some(icon_refresh(14.0, FG)))
                    .on_press(|s: &mut AppState| s.load_discord_room()),
            );
        }
        card = card.child(actions);

        if !s.discord.accounts.is_empty() {
            let mut acc_list = col()
                .w_full()
                .gap(6.0)
                .pt(6.0)
                .child(text("Saved accounts").font(type_small()).color(fg_muted()));
            for acc in &s.discord.accounts {
                let aid = acc.id.clone();
                acc_list = acc_list.child(
                    row()
                        .key(format!("dc-saved-acc-{}", acc.id))
                        .role("button")
                        .label(acc.name.clone())
                        .focusable(true)
                        .w_full()
                        .h(40.0)
                        .px(12.0)
                        .gap(10.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .fill(subtle_fill())
                        .hover(hover_fill())
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .on_press(move |s: &mut AppState| s.switch_discord_account(&aid))
                        .child(
                            text(acc.name.clone())
                                .font(type_control())
                                .color(FG)
                                .grow(1.0),
                        )
                        .child(text("Switch").font(type_small()).color(fg_muted())),
                );
            }
            card = card.child(acc_list);
        }
    }

    col()
        .key("dc-signed-out")
        .w_full()
        .h_full()
        .pt(64.0)
        .fill(BG)
        .center()
        .child(card)
}

fn guild_face(s: &AppState, guild: &DiscordGuild, face_sz: f32, sc: f32, scale: f32) -> El {
    let mut face = el()
        .size(face_sz, face_sz)
        .radius(face_sz * 0.5)
        .clip_rounded(face_sz * 0.5)
        .fill(monogram_fill(&guild.name))
        .center();
    if let Some(img) = guild
        .icon
        .as_deref()
        .and_then(|u| s.discord_remote_image_sized(u, 64.0, scale))
    {
        face = face.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        face = glassy_ring(face.fill(BG)).child(
            text(initials(&guild.name))
                .font(font_px(12.0 * sc, 600.0))
                .color(FG),
        );
    }
    face
}

fn band_guild_button(s: &AppState, guild: &DiscordGuild, sc: f32, scale: f32) -> El {
    let row_h = 52.0 * sc;
    let face_sz = 44.0 * sc;
    let active = s.discord.place == DiscordPlace::Servers
        && s.discord.active_guild_id.as_deref() == Some(guild.id.as_str());
    let gid = guild.id.clone();
    let srv_ctx = ContextMenuTarget::DiscordServer {
        guild_id: guild.id.clone(),
        name: guild.name.clone(),
        muted: guild.muted,
        owner: false,
        icon: guild.icon.clone(),
    };

    let mut btn = row()
        .key(format!("dc-guild-tile-{}", guild.id))
        .role("button")
        .label(guild.name.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(row_h)
        .radius(RADIUS_PILL)
        .center_y()
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| s.open_discord_guild(&gid))
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.open_context_menu(srv_ctx.clone(), p.x, p.y);
        });

    if active {
        btn = btn
            .pl(4.0)
            .pr(16.0)
            .gap(10.0)
            .fill(selected_fill())
            .border(1.0, exo_kit::card_border())
            .child(guild_face(s, guild, face_sz, sc, scale))
            .child(
                text(guild.name.clone())
                    .font(font_px(13.0 * sc, 600.0))
                    .color(FG)
                    .max_w(180.0 * sc)
                    .truncate(),
            );
    } else {
        btn = btn
            .padding(4.0)
            .fill(TRANSPARENT)
            .hover(hover_fill())
            .opacity(if guild.muted { 0.5 } else { 1.0 })
            .child(guild_face(s, guild, face_sz, sc, scale));
    }

    let badge_border = BG;
    if guild.mentions > 0 {
        btn = btn.child(
            el().pin(Some(4.0 + face_sz - 14.0), Some(1.0), None, None)
                .child(mention_badge(guild.mentions, Some(badge_border), sc)),
        );
    } else if guild.unread && !guild.muted {
        btn = btn.child(
            el().pin(Some(4.0 + face_sz - 12.0), Some(1.0), None, None)
                .child(unread_dot_band(badge_border)),
        );
    }

    let my_voice_here = s.discord.voice.state != "off"
        && s.discord.voice.guild_id.as_deref() == Some(guild.id.as_str());
    if guild.in_voice > 0 || my_voice_here {
        btn = btn.child(
            el().pin(Some(0.0), None, None, Some(0.0))
                .size(20.0, 20.0)
                .radius(10.0)
                .fill(good())
                .border(2.0, badge_border)
                .center()
                .child(icon_volume(11.0, PRIMARY_FG)),
        );
    }

    btn
}

fn band_folder_tile(
    s: &AppState,
    item: &DiscordRailItem,
    guilds: &[&DiscordGuild],
    idx: usize,
    sc: f32,
    scale: f32,
) -> El {
    let row_h = 52.0 * sc;
    let face_sz = 44.0 * sc;
    let folder_key = item.id.clone().unwrap_or_else(|| format!("folder-{idx}"));
    let contains_active = s.discord.place == DiscordPlace::Servers
        && s.discord
            .active_guild_id
            .as_deref()
            .is_some_and(|aid| guilds.iter().any(|g| g.id == aid));
    let expanded = contains_active || s.discord.expanded_folders.contains(&folder_key);

    if expanded {
        let fk = folder_key.clone();
        let mut group = row()
            .key(format!("dc-folder-open-{folder_key}"))
            .gap(4.0)
            .center_y()
            .pl(2.0)
            .pr(4.0)
            .radius(RADIUS_PILL)
            .fill(Color::WHITE.alpha(0.04))
            .child(
                el().key(format!("dc-folder-toggle-{folder_key}"))
                    .role("button")
                    .label(item.name.clone().unwrap_or_else(|| "Folder".into()))
                    .cursor(Cursor::Pointer)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .size(36.0, face_sz)
                    .radius(RADIUS_PILL)
                    .hover(hover_fill())
                    .center()
                    .on_press(move |s: &mut AppState| s.toggle_discord_folder(&fk))
                    .child(icon_folder(16.0, fg_muted())),
            );
        for g in guilds {
            group = group.child(band_guild_button(s, g, sc, scale));
        }
        return group;
    }

    let mentions: i32 = guilds.iter().map(|g| g.mentions).sum();
    let unread = guilds.iter().any(|g| g.unread && !g.muted);
    let in_voice = guilds.iter().any(|g| g.in_voice > 0);
    let all_muted = guilds.iter().all(|g| g.muted);
    let mini_sz = ((face_sz - 12.0) * 0.5).floor().max(12.0);
    let mut mini_grid = el().w(mini_sz * 2.0 + 2.0).equal_columns(2).gaps(2.0, 2.0);
    for g in guilds.iter().take(4) {
        let mut cell = el()
            .size(mini_sz, mini_sz)
            .radius(mini_sz * 0.5)
            .clip_rounded(mini_sz * 0.5)
            .fill(monogram_fill(&g.name))
            .center();
        if let Some(img) = g
            .icon
            .as_deref()
            .and_then(|u| s.discord_remote_image_sized(u, 64.0, scale))
        {
            cell = cell.pixels_fit(img.pixels(), SurfaceFit::Fill);
        } else {
            cell = cell.child(
                text(initials(&g.name))
                    .font(font_px(7.5 * sc, 600.0))
                    .color(FG),
            );
        }
        mini_grid = mini_grid.child(cell);
    }

    let fk = folder_key.clone();
    let face = glassy_ring(
        el().size(face_sz, face_sz)
            .radius(face_sz * 0.5)
            .clip_rounded(face_sz * 0.5)
            .fill(Color::WHITE.alpha(0.04))
            .center()
            .child(mini_grid),
    );
    let mut btn = row()
        .key(format!("dc-folder-{folder_key}"))
        .role("button")
        .label(item.name.clone().unwrap_or_else(|| "Folder".into()))
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(row_h)
        .padding(4.0)
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .hover(hover_fill())
        .opacity(if all_muted { 0.5 } else { 1.0 })
        .center_y()
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| s.toggle_discord_folder(&fk))
        .child(face);

    if mentions > 0 {
        btn = btn.child(
            el().pin(Some(4.0 + face_sz - 14.0), Some(1.0), None, None)
                .child(mention_badge(mentions, Some(BG), sc)),
        );
    } else if unread {
        btn = btn.child(
            el().pin(Some(4.0 + face_sz - 12.0), Some(1.0), None, None)
                .child(unread_dot_band(BG)),
        );
    }

    if in_voice {
        btn = btn.child(
            el().pin(Some(0.0), None, None, Some(0.0))
                .size(20.0, 20.0)
                .radius(10.0)
                .fill(good())
                .border(2.0, BG)
                .center()
                .child(icon_volume(11.0, PRIMARY_FG)),
        );
    }

    btn
}

fn server_band(s: &AppState, sc: f32, scale: f32) -> El {
    let row_h = 52.0 * sc;
    let face_sz = 44.0 * sc;
    let home_active = s.discord.place != DiscordPlace::Servers && !s.discord.shop_open;
    let shop_active = s.discord.shop_open;

    let dm_mentions = s.discord.total_dm_mentions();
    let dm_unread = s.discord.unread_dm_count() > 0;

    let mut home_btn = row()
        .key("dc-place-home")
        .role("button")
        .label("Home")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(row_h)
        .radius(RADIUS_PILL)
        .center_y()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.open_discord_place(DiscordPlace::Home));

    if home_active {
        home_btn = home_btn
            .pl(4.0)
            .pr(16.0)
            .gap(10.0)
            .fill(selected_fill())
            .border(1.0, exo_kit::card_border())
            .child(
                el().size(face_sz, face_sz)
                    .radius(face_sz * 0.5)
                    .fill(BG)
                    .center()
                    .child(icon_home(20.0, FG)),
            )
            .child(text("Home").font(font_px(13.0 * sc, 600.0)).color(FG));
    } else {
        home_btn = home_btn
            .padding(4.0)
            .fill(TRANSPARENT)
            .hover(hover_fill())
            .child(
                glassy_ring(
                    el().size(face_sz, face_sz)
                        .radius(face_sz * 0.5)
                        .fill(BG)
                        .center(),
                )
                .child(icon_home(20.0, FG)),
            );
    }

    let home_badge_border = BG;
    if dm_mentions > 0 {
        home_btn = home_btn.child(
            el().pin(Some(4.0 + face_sz - 14.0), Some(1.0), None, None)
                .child(mention_badge(dm_mentions, Some(home_badge_border), sc)),
        );
    } else if dm_unread {
        home_btn = home_btn.child(
            el().pin(Some(4.0 + face_sz - 9.0), Some(3.0), None, None)
                .child(unread_dot_band(home_badge_border)),
        );
    }

    let mut shop_btn = row()
        .key("dc-band-shop")
        .role("button")
        .label("Shop")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(row_h)
        .radius(RADIUS_PILL)
        .center_y()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| {
            s.discord.shop_open = !s.discord.shop_open;
            if s.discord.shop_open {
                s.discord.place = DiscordPlace::Home;
                s.discord.active_guild_id = None;
                s.load_discord_shop(false);
            }
        });

    if shop_active {
        shop_btn = shop_btn
            .pl(4.0)
            .pr(16.0)
            .gap(10.0)
            .fill(selected_fill())
            .border(1.0, exo_kit::card_border())
            .child(
                el().size(face_sz, face_sz)
                    .radius(face_sz * 0.5)
                    .fill(BG)
                    .center()
                    .child(icon_shop(20.0, FG)),
            )
            .child(text("Shop").font(font_px(13.0 * sc, 600.0)).color(FG));
    } else {
        shop_btn = shop_btn
            .padding(4.0)
            .fill(TRANSPARENT)
            .hover(hover_fill())
            .child(
                glassy_ring(
                    el().size(face_sz, face_sz)
                        .radius(face_sz * 0.5)
                        .fill(BG)
                        .center(),
                )
                .child(icon_shop(20.0, FG)),
            );
    }

    let mut strip = row()
        .key("dc-band-strip")
        .grow(1.0)
        .min_w(0.0)
        .scroll_x()
        .scroll_edge_fade(24.0)
        .px(8.0)
        .py(6.0)
        .gap(8.0)
        .center_y()
        .child(home_btn)
        .child(shop_btn)
        .child(el().w(1.0).h(28.0).mx(2.0).fill(hairline()));

    let guild_map: HashMap<&str, &DiscordGuild> = s
        .discord
        .home
        .guilds
        .iter()
        .map(|g| (g.id.as_str(), g))
        .collect();
    let mut placed: HashSet<&str> = HashSet::new();

    for (idx, item) in s.discord.home.rail.iter().enumerate() {
        let members: Vec<&DiscordGuild> = item
            .guild_ids
            .iter()
            .filter_map(|id| guild_map.get(id.as_str()).copied())
            .collect();
        if members.is_empty() {
            continue;
        }
        for g in &members {
            placed.insert(g.id.as_str());
        }
        if item.id.is_some() && members.len() > 1 {
            strip = strip.child(band_folder_tile(s, item, &members, idx, sc, scale));
        } else {
            for g in members {
                strip = strip.child(band_guild_button(s, g, sc, scale));
            }
        }
    }

    for g in &s.discord.home.guilds {
        if !placed.contains(g.id.as_str()) {
            strip = strip.child(band_guild_button(s, g, sc, scale));
        }
    }

    let add_btn = row()
        .key("dc-band-add-server")
        .role("button")
        .label("Add a server")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(row_h)
        .padding(4.0)
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .hover(hover_fill())
        .center_y()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| {
            s.notice = Some("Join or create a server in Discord.".into());
        })
        .child(
            glassy_ring(
                el().size(face_sz, face_sz)
                    .radius(face_sz * 0.5)
                    .fill(BG)
                    .center(),
            )
            .child(icon_plus(20.0, FG)),
        );
    strip = strip.child(add_btn);

    let inbox_btn = icon_btn(
        "dc-band-inbox".into(),
        "Inbox",
        s.discord.inbox_open,
        false,
        icon_inbox(18.0, if s.discord.inbox_open { FG } else { fg_muted() }),
    )
    .on_press(|s: &mut AppState| {
        if s.discord.inbox_open {
            s.discord.inbox_open = false;
        } else {
            s.open_discord_inbox(crate::discord_chat_state::InboxTab::ForYou);
        }
    });

    let scroll_right_btn = el()
        .key("dc-band-scroll-right")
        .role("button")
        .label("More servers")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .size(32.0, 32.0)
        .radius(16.0)
        .fill(subtle_fill())
        .border(1.0, hairline())
        .hover(hover_fill())
        .center()
        .child(icon_chevron_right(14.0, fg_muted()));

    panel_frame(
        row()
            .key("dc-server-band")
            .w_full()
            .py(10.0)
            .px(12.0)
            .gap(8.0)
            .center_y(),
    )
    .child(strip)
    .child(
        row()
            .pl(4.0)
            .gap(6.0)
            .center_y()
            .child(scroll_right_btn)
            .child(inbox_btn),
    )
}

fn classic_rail_pill(active: bool, unread: bool) -> Option<El> {
    if active {
        Some(
            el().pin(Some(0.0), None, None, None)
                .size(3.5, 32.0)
                .radius(2.0)
                .fill(FG)
                .inert(),
        )
    } else if unread {
        Some(
            el().pin(Some(0.0), None, None, None)
                .size(3.5, 8.0)
                .radius(2.0)
                .fill(FG.alpha(0.75))
                .inert(),
        )
    } else {
        None
    }
}

fn classic_guild_button(s: &AppState, guild: &DiscordGuild, sc: f32, scale: f32) -> El {
    let face_sz = 44.0 * sc;
    let active = s.discord.place == DiscordPlace::Servers
        && s.discord.active_guild_id.as_deref() == Some(guild.id.as_str());
    let unread = guild.unread && !guild.muted;
    let gid = guild.id.clone();
    let srv_ctx = ContextMenuTarget::DiscordServer {
        guild_id: guild.id.clone(),
        name: guild.name.clone(),
        muted: guild.muted,
        owner: false,
        icon: guild.icon.clone(),
    };

    let radius = if active { 14.0 } else { face_sz * 0.5 };
    let mut face = el()
        .size(face_sz, face_sz)
        .radius(radius)
        .continuous_corners()
        .clip_rounded(radius)
        .fill(if active {
            selected_fill()
        } else {
            monogram_fill(&guild.name)
        })
        .border(
            1.0,
            if active {
                FG.alpha(0.28)
            } else {
                exo_kit::card_border()
            },
        )
        .center();
    if let Some(img) = guild
        .icon
        .as_deref()
        .and_then(|u| s.discord_remote_image_sized(u, 96.0, scale))
    {
        face = face.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        face = face.child(
            text(initials(&guild.name))
                .font(font_px(13.0 * sc, 600.0))
                .color(FG),
        );
    }

    let mut btn = el()
        .key(format!("dc-guild-tile-{}", guild.id))
        .role("button")
        .label(guild.name.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(face_sz, face_sz)
        .radius(radius)
        .opacity(if guild.muted && !active { 0.55 } else { 1.0 })
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| s.open_discord_guild(&gid))
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.open_context_menu(srv_ctx.clone(), p.x, p.y);
        })
        .child(face);

    if guild.mentions > 0 {
        btn = btn.child(
            el().pin(None, Some(-2.0), Some(-2.0), None)
                .child(mention_badge(guild.mentions, Some(BG), sc)),
        );
    }
    let my_voice_here = s.discord.voice.state != "off"
        && s.discord.voice.guild_id.as_deref() == Some(guild.id.as_str());
    if guild.in_voice > 0 || my_voice_here {
        btn = btn.child(
            el().pin(None, None, Some(-2.0), Some(-2.0))
                .size(18.0, 18.0)
                .radius(9.0)
                .fill(good())
                .border(2.0, BG)
                .center()
                .child(icon_volume(10.0, PRIMARY_FG)),
        );
    }

    row()
        .w_full()
        .h(face_sz + 4.0)
        .center()
        .children(classic_rail_pill(active, unread))
        .child(btn)
}

fn classic_folder_tile(
    s: &AppState,
    item: &DiscordRailItem,
    guilds: &[&DiscordGuild],
    idx: usize,
    sc: f32,
    scale: f32,
) -> El {
    let face_sz = 44.0 * sc;
    let folder_key = item.id.clone().unwrap_or_else(|| format!("folder-{idx}"));
    let contains_active = s.discord.place == DiscordPlace::Servers
        && s.discord
            .active_guild_id
            .as_deref()
            .is_some_and(|aid| guilds.iter().any(|g| g.id == aid));
    let expanded = contains_active || s.discord.expanded_folders.contains(&folder_key);

    if expanded {
        let fk = folder_key.clone();
        let mut group = col()
            .key(format!("dc-folder-open-{folder_key}"))
            .w(54.0)
            .py(4.0)
            .gap(4.0)
            .items_center()
            .radius(RADIUS_PANEL)
            .fill(Color::WHITE.alpha(0.04))
            .border(1.0, hairline())
            .child(
                el().key(format!("dc-folder-toggle-{folder_key}"))
                    .role("button")
                    .label(item.name.clone().unwrap_or_else(|| "Folder".into()))
                    .cursor(Cursor::Pointer)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .size(40.0, 32.0)
                    .radius(RADIUS_SMALL)
                    .hover(hover_fill())
                    .center()
                    .on_press(move |s: &mut AppState| s.toggle_discord_folder(&fk))
                    .child(icon_folder(16.0, fg_muted())),
            );
        for g in guilds {
            group = group.child(classic_guild_button(s, g, sc, scale));
        }
        return group;
    }

    let mentions: i32 = guilds.iter().map(|g| g.mentions).sum();
    let unread = guilds.iter().any(|g| g.unread && !g.muted);
    let all_muted = guilds.iter().all(|g| g.muted);
    let mini_sz = ((face_sz - 12.0) * 0.5).floor().max(12.0);
    let mut mini_grid = el().w(mini_sz * 2.0 + 2.0).equal_columns(2).gaps(2.0, 2.0);
    for g in guilds.iter().take(4) {
        let mut cell = el()
            .size(mini_sz, mini_sz)
            .radius(mini_sz * 0.5)
            .clip_rounded(mini_sz * 0.5)
            .fill(monogram_fill(&g.name))
            .center();
        if let Some(img) = g
            .icon
            .as_deref()
            .and_then(|u| s.discord_remote_image_sized(u, 64.0, scale))
        {
            cell = cell.pixels_fit(img.pixels(), SurfaceFit::Fill);
        } else {
            cell = cell.child(
                text(initials(&g.name))
                    .font(font_px(7.5 * sc, 600.0))
                    .color(FG),
            );
        }
        mini_grid = mini_grid.child(cell);
    }

    let fk = folder_key.clone();
    let mut btn = el()
        .key(format!("dc-folder-tile-{folder_key}"))
        .role("button")
        .label(item.name.clone().unwrap_or_else(|| "Folder".into()))
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(face_sz, face_sz)
        .radius(14.0)
        .fill(subtle_fill())
        .border(1.0, exo_kit::card_border())
        .hover(hover_fill())
        .opacity(if all_muted { 0.55 } else { 1.0 })
        .center()
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| s.toggle_discord_folder(&fk))
        .child(mini_grid);

    if mentions > 0 {
        btn = btn.child(
            el().pin(None, Some(-2.0), Some(-2.0), None)
                .child(mention_badge(mentions, Some(BG), sc)),
        );
    }

    row()
        .w_full()
        .h(face_sz + 4.0)
        .center()
        .children(classic_rail_pill(false, unread))
        .child(btn)
}

fn classic_server_rail(s: &AppState, sc: f32, scale: f32) -> El {
    let face_sz = 44.0 * sc;
    let home_active = s.discord.place != DiscordPlace::Servers && !s.discord.shop_open;
    let shop_active = s.discord.shop_open;
    let dm_mentions = s.discord.total_dm_mentions();
    let dm_unread = s.discord.unread_dm_count() > 0;

    let home_r = if home_active { 14.0 } else { face_sz * 0.5 };
    let mut home_btn = el()
        .key("dc-place-home")
        .role("button")
        .label("Home")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(face_sz, face_sz)
        .radius(home_r)
        .continuous_corners()
        .fill(if home_active {
            selected_fill()
        } else {
            subtle_fill()
        })
        .border(
            1.0,
            if home_active {
                FG.alpha(0.28)
            } else {
                exo_kit::card_border()
            },
        )
        .hover(hover_fill())
        .center()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.open_discord_place(DiscordPlace::Home))
        .child(icon_home(20.0, FG));
    if dm_mentions > 0 {
        home_btn = home_btn.child(
            el().pin(None, Some(-2.0), Some(-2.0), None)
                .child(mention_badge(dm_mentions, Some(BG), sc)),
        );
    }
    let home_slot = row()
        .w_full()
        .h(face_sz + 4.0)
        .center()
        .children(classic_rail_pill(home_active, dm_unread))
        .child(home_btn);

    let shop_r = if shop_active { 14.0 } else { face_sz * 0.5 };
    let shop_slot = row()
        .w_full()
        .h(face_sz + 4.0)
        .center()
        .children(classic_rail_pill(shop_active, false))
        .child(
            el().key("dc-band-shop")
                .role("button")
                .label("Shop")
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .size(face_sz, face_sz)
                .radius(shop_r)
                .continuous_corners()
                .fill(if shop_active {
                    selected_fill()
                } else {
                    TRANSPARENT
                })
                .border(
                    1.0,
                    if shop_active {
                        FG.alpha(0.28)
                    } else {
                        exo_kit::card_border()
                    },
                )
                .hover(hover_fill())
                .center()
                .motion(SPRING_QUICK)
                .on_press(|s: &mut AppState| {
                    s.discord.shop_open = !s.discord.shop_open;
                    if s.discord.shop_open {
                        s.discord.place = DiscordPlace::Home;
                        s.discord.active_guild_id = None;
                        s.load_discord_shop(false);
                    }
                })
                .child(icon_shop(19.0, if shop_active { FG } else { fg_muted() })),
        );

    let guild_map: HashMap<&str, &DiscordGuild> = s
        .discord
        .home
        .guilds
        .iter()
        .map(|g| (g.id.as_str(), g))
        .collect();
    let mut placed: HashSet<&str> = HashSet::new();
    let mut list = col()
        .key("dc-classic-server-list")
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .py(4.0)
        .gap(6.0)
        .items_center()
        .scroll(ScrollSpec {
            stick_bottom: false,
        });

    for (idx, item) in s.discord.home.rail.iter().enumerate() {
        let members: Vec<&DiscordGuild> = item
            .guild_ids
            .iter()
            .filter_map(|id| guild_map.get(id.as_str()).copied())
            .collect();
        if members.is_empty() {
            continue;
        }
        for g in &members {
            placed.insert(g.id.as_str());
        }
        if item.id.is_some() && members.len() > 1 {
            list = list.child(classic_folder_tile(s, item, &members, idx, sc, scale));
        } else {
            for g in members {
                list = list.child(classic_guild_button(s, g, sc, scale));
            }
        }
    }
    for g in &s.discord.home.guilds {
        if !placed.contains(g.id.as_str()) {
            list = list.child(classic_guild_button(s, g, sc, scale));
        }
    }

    let add_btn = el()
        .key("dc-band-add-server")
        .role("button")
        .label("Add a server")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(40.0, 40.0)
        .radius(20.0)
        .fill(subtle_fill())
        .border(1.0, exo_kit::card_border())
        .hover(hover_fill())
        .center()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| {
            s.notice = Some("Join or create a server in Discord.".into());
        })
        .child(icon_plus(18.0, fg_muted()));

    let inbox_btn = icon_btn(
        "dc-band-inbox".into(),
        "Inbox",
        s.discord.inbox_open,
        false,
        icon_inbox(18.0, if s.discord.inbox_open { FG } else { fg_muted() }),
    )
    .on_press(|s: &mut AppState| {
        if s.discord.inbox_open {
            s.discord.inbox_open = false;
        } else {
            s.open_discord_inbox(crate::discord_chat_state::InboxTab::ForYou);
        }
    });

    panel_frame(
        col()
            .key("dc-classic-rail")
            .w(68.0)
            .h_full()
            .py(10.0)
            .gap(6.0)
            .items_center(),
    )
    .child(home_slot)
    .child(shop_slot)
    .child(el().w(32.0).h(1.0).my(2.0).fill(hairline()))
    .child(list)
    .child(el().w(32.0).h(1.0).my(2.0).fill(hairline()))
    .child(add_btn)
    .child(inbox_btn)
}

fn me_panel(s: &AppState, sc: f32, scale: f32) -> El {
    let me = s.discord.home.me.as_ref();
    let name = me.map(|m| m.name.as_str()).unwrap_or("Discord");
    let st = me.and_then(|m| m.status.as_deref()).unwrap_or("online");
    let look = me.and_then(|m| m.look.as_ref());
    let muted = s.discord.voice.muted;
    let deaf = s.discord.voice.deafened;
    let subtitle = s
        .discord
        .custom_status
        .as_deref()
        .filter(|t| !t.trim().is_empty())
        .map(str::to_string)
        .unwrap_or_else(|| status_label(st).to_string());

    let mut stack = col()
        .w_full()
        .border_sides([1.0, 0.0, 0.0, 0.0], hairline());
    if let Some(dock) = discord_voice::call_dock(s) {
        stack = stack.child(dock);
    }

    let identity = row()
        .key("dc-me-trigger")
        .role("button")
        .label("Account status")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .grow(1.0)
        .min_w(0.0)
        .h(48.0)
        .px(8.0)
        .gap(12.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(subtle_fill())
        .border(1.0, hairline())
        .hover(hover_fill())
        .hover_motion(Spring::NONE, SPRING_QUICK)
        .hit()
        .press_on_down()
        .on_press(|s: &mut AppState| s.open_discord_status_menu())
        .on_secondary_press(|s: &mut AppState, _x, _y| s.open_discord_status_menu())
        .child(avatar_with_look(
            s,
            me.and_then(|m| m.avatar.as_deref()),
            look,
            name,
            36.0,
            18.0,
            Some(st),
            me.is_some_and(|m| s.discord.speaking.contains(&m.id)),
            BG,
            scale,
        ))
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(1.0)
                .child(styled_name_text(
                    text(name.to_string())
                        .font(font_px(13.0 * sc, 600.0))
                        .truncate(),
                    look,
                    FG,
                ))
                .child(
                    text(subtitle)
                        .font(font_px(12.0 * sc, 400.0))
                        .color(fg_muted())
                        .truncate(),
                ),
        );

    let mic_caret = el()
        .key("dc-me-mic-caret")
        .role("button")
        .label("Input device")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .size(16.0, 32.0)
        .radius(RADIUS_PILL)
        .hover(hover_fill())
        .center()
        .on_press(|s: &mut AppState| {
            s.load_discord_audio();
            s.open_context_menu(
                ContextMenuTarget::DiscordAudioQuick { input: true },
                0.0,
                0.0,
            );
        })
        .child(icon_chevron_down(11.0, fg_muted()));

    let deaf_caret = el()
        .key("dc-me-deaf-caret")
        .role("button")
        .label("Output device")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .size(16.0, 32.0)
        .radius(RADIUS_PILL)
        .hover(hover_fill())
        .center()
        .on_press(|s: &mut AppState| {
            s.load_discord_audio();
            s.open_context_menu(
                ContextMenuTarget::DiscordAudioQuick { input: false },
                0.0,
                0.0,
            );
        })
        .child(icon_chevron_down(11.0, fg_muted()));

    let mute_btn = el()
        .key("dc-me-mute")
        .role("button")
        .label(if muted { "Unmute" } else { "Mute" })
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(28.0, 32.0)
        .radius(RADIUS_PILL)
        .fill(if muted {
            bad().alpha(0.18)
        } else {
            TRANSPARENT
        })
        .hover(if muted {
            bad().alpha(0.28)
        } else {
            hover_fill()
        })
        .center()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.toggle_discord_mute())
        .on_secondary_press(|s: &mut AppState, _x, _y| {
            s.load_discord_audio();
            s.open_context_menu(
                ContextMenuTarget::DiscordAudioQuick { input: true },
                0.0,
                0.0,
            );
        })
        .child(if muted {
            icon_mic_off(17.0, bad())
        } else {
            icon_mic(17.0, fg_muted())
        });

    let deaf_btn = el()
        .key("dc-me-deafen")
        .role("button")
        .label(if deaf { "Undeafen" } else { "Deafen" })
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(28.0, 32.0)
        .radius(RADIUS_PILL)
        .fill(if deaf { bad().alpha(0.18) } else { TRANSPARENT })
        .hover(if deaf {
            bad().alpha(0.28)
        } else {
            hover_fill()
        })
        .center()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.toggle_discord_deafen())
        .on_secondary_press(|s: &mut AppState, _x, _y| {
            s.load_discord_audio();
            s.open_context_menu(
                ContextMenuTarget::DiscordAudioQuick { input: false },
                0.0,
                0.0,
            );
        })
        .child(if deaf {
            icon_headphones_off(17.0, bad())
        } else {
            icon_headphones(17.0, fg_muted())
        });

    let settings_btn = icon_btn(
        "dc-me-settings".into(),
        "Discord settings",
        s.discord.settings_open,
        false,
        icon_settings(
            17.0,
            if s.discord.settings_open {
                FG
            } else {
                fg_muted()
            },
        ),
    )
    .on_press(|s: &mut AppState| s.open_discord_settings(DiscordSettingsPage::Account));

    let bar = row()
        .key("dc-me-panel")
        .w_full()
        .h(60.0 * sc)
        .px(8.0)
        .gap(2.0)
        .center_y()
        .child(identity)
        .child(row().center_y().child(mute_btn).child(mic_caret))
        .child(row().center_y().child(deaf_btn).child(deaf_caret))
        .child(settings_btn);

    stack.child(bar)
}

fn pinned_dm_ids(s: &AppState) -> Vec<String> {
    let mut ids: Vec<String> = s
        .settings
        .ui_prefs
        .get("discordPinnedDms")
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|v| v.as_str().map(String::from))
                .filter(|id| !s.discord.unpinned_dm_overrides.contains(id))
                .collect()
        })
        .unwrap_or_default();
    for id in &s.discord.pinned_dm_overrides {
        if !ids.contains(id) {
            ids.push(id.clone());
        }
    }
    ids
}

fn dm_subtitle(s: &AppState, dm: &DiscordDm) -> Option<String> {
    if let Some(ref line) = dm.line
        && !line.is_empty()
    {
        return Some(line.clone());
    }
    if let Some(last) = s
        .discord
        .chat
        .messages_by_channel
        .get(&dm.id)
        .and_then(|v| v.last())
        && let Some(preview) = crate::discord_chat_state::dm_preview_from_message(last)
    {
        return Some(preview);
    }
    if let Some(ref sub) = dm.subtitle
        && !sub.is_empty()
    {
        return Some(sub.clone());
    }
    if dm.group {
        let count = dm.members.as_ref().map(|m| m.len() + 1).unwrap_or(2);
        return Some(format!("{count} Members"));
    }
    None
}

fn dm_row(s: &AppState, dm: &DiscordDm, active: bool, sc: f32, scale: f32) -> El {
    let dm_id = dm.id.clone();
    let row_h = 52.0 * sc;
    let is_unread = dm.unread && !dm.muted;
    let name_color = if active {
        FG
    } else if dm.muted {
        fg_faint()
    } else {
        FG
    };
    let sub_color = if active {
        FG.alpha(0.76)
    } else if is_unread {
        FG
    } else {
        fg_muted()
    };
    let time_color = if active { FG.alpha(0.64) } else { fg_faint() };

    let mut body = col().grow(1.0).min_w(0.0).gap(1.0).child(
        text(dm.name.clone())
            .font(font_px(
                13.0 * sc,
                if active || is_unread { 600.0 } else { 500.0 },
            ))
            .color(name_color)
            .truncate(),
    );
    if let Some(sub) = dm_subtitle(s, dm) {
        body = body.child(
            text(sub)
                .font(font_px(12.0 * sc, 400.0))
                .color(sub_color)
                .truncate(),
        );
    }

    let mut meta = col().items_end().gap(4.0);
    if let Some(ts) = dm.last_at {
        meta = meta.child(
            text(format_row_time(ts))
                .font(font_px_tnum(11.0 * sc, 500.0))
                .color(time_color),
        );
    }
    if dm.mentions > 0 {
        meta = meta.child(mention_badge(dm.mentions, None, sc));
    } else if is_unread {
        meta = meta.child(unread_dot_row(FG));
    }

    let dm_ctx = ContextMenuTarget::DiscordDm {
        dm_id: dm.id.clone(),
        user_id: dm.user_id.clone(),
        name: dm.name.clone(),
        username: dm.username.clone(),
        unread: is_unread,
        muted: dm.muted,
        group: dm.group,
    };

    let mut r = row()
        .key(format!("dc-dm-{}", dm.id))
        .role("button")
        .label(dm.name.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .w_full()
        .h(row_h)
        .pl(8.0)
        .pr(14.0)
        .gap(12.0)
        .center_y()
        .radius(RADIUS_CARD)
        .continuous_corners()
        .fill(if active {
            exo_kit::surface_elevated_hi()
        } else {
            TRANSPARENT
        })
        .hover(if active {
            exo_kit::surface_elevated_hi()
        } else {
            hover_fill()
        })
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .hover_motion(Spring::NONE, SPRING_QUICK)
        .on_press(move |s: &mut AppState| s.open_discord_dm(&dm_id))
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.open_context_menu(dm_ctx.clone(), p.x, p.y);
        })
        .child(avatar_with_look(
            s,
            dm.avatar.as_deref(),
            dm.look.as_ref(),
            &dm.name,
            36.0,
            18.0,
            if dm.group { None } else { dm.status.as_deref() },
            false,
            if active {
                exo_kit::surface_elevated_hi()
            } else {
                exo_kit::surface_elevated()
            },
            scale,
        ))
        .child(body);
    if active {
        r = r.border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
    }

    if dm.last_at.is_some() || dm.mentions > 0 || is_unread {
        r = r.child(meta);
    }

    el().w_full().pb(2.0).child(r)
}

fn dm_sidebar(s: &AppState, sc: f32, scale: f32) -> El {
    let pane_w = 400.0 * sc;
    let head_h = 48.0 * sc;
    let q = s.discord.dm_search_query.text.trim().to_lowercase();

    let visible_dms: Vec<&DiscordDm> = s
        .discord
        .home
        .dms
        .iter()
        .filter(|d| !d.request && !d.spam)
        .filter(|d| q.is_empty() || d.name.to_lowercase().contains(&q))
        .collect();

    let unread_count = s
        .discord
        .home
        .dms
        .iter()
        .filter(|d| d.unread && !d.muted && !d.request && !d.spam)
        .count();

    let pinned_order = pinned_dm_ids(s);
    let pinned_set: HashSet<&str> = pinned_order.iter().map(String::as_str).collect();
    let dm_by_id: HashMap<&str, &DiscordDm> =
        visible_dms.iter().map(|d| (d.id.as_str(), *d)).collect();
    let pinned: Vec<&DiscordDm> = pinned_order
        .iter()
        .filter_map(|id| dm_by_id.get(id.as_str()).copied())
        .collect();
    let unpinned: Vec<&DiscordDm> = visible_dms
        .into_iter()
        .filter(|d| !pinned_set.contains(d.id.as_str()))
        .collect();

    let head = if s.discord.dm_search_open || !q.is_empty() {
        row()
            .w_full()
            .h(head_h)
            .pl(12.0)
            .pr(8.0)
            .gap(6.0)
            .center_y()
            .child(
                text_input(&s.discord.dm_search_query, font_px(13.0 * sc, 400.0))
                    .key("dc-dm-find-input")
                    .label("Find a conversation")
                    .cursor(Cursor::Text)
                    .grow(1.0)
                    .single_line()
                    .color(FG)
                    .placeholder("Find a conversation", fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.discord.dm_search_query
                    })),
            )
            .child(
                icon_btn(
                    "dc-dm-find-close".into(),
                    "Close search",
                    false,
                    false,
                    icon_close(14.0, fg_muted()),
                )
                .on_press(|s: &mut AppState| {
                    s.discord.dm_search_open = false;
                    s.discord.dm_search_query = rune::TextEdit::default();
                }),
            )
    } else {
        let mut h = row()
            .w_full()
            .h(head_h)
            .pl(16.0)
            .pr(8.0)
            .gap(4.0)
            .center_y()
            .child(
                text("Direct messages")
                    .font(font_px(15.0 * sc, 600.0))
                    .color(FG)
                    .grow(1.0),
            );
        if unread_count > 0 {
            h = h.child(
                text(format!("{unread_count} unread"))
                    .font(font_px(12.0 * sc, 500.0))
                    .color(fg_muted())
                    .pr(4.0),
            );
        }
        h.child(
            icon_btn(
                "dc-dm-find-btn".into(),
                "Find a conversation",
                false,
                false,
                icon_search(16.0, fg_muted()),
            )
            .on_press(|s: &mut AppState| s.discord.dm_search_open = true),
        )
        .child(
            icon_btn(
                "dc-dm-new-btn".into(),
                "New conversation",
                false,
                false,
                icon_plus(16.0, fg_muted()),
            )
            .on_press(|s: &mut AppState| s.open_create_dm_dialog()),
        )
    };

    let active_dm_id = if s.discord.place == DiscordPlace::Messages {
        s.discord.active_dm_id.as_deref()
    } else {
        None
    };

    let mut list = col().w_full().pt(4.0).px(8.0).pb(8.0).gap(2.0);
    if !pinned.is_empty() {
        list = list.child(
            row()
                .w_full()
                .h(16.0)
                .center_y()
                .mt(6.0)
                .mb(2.0)
                .px(8.0)
                .child(
                    text("Pinned")
                        .font(font_px(12.0 * sc, 600.0))
                        .color(fg_muted()),
                ),
        );
        for dm in &pinned {
            let active = active_dm_id == Some(dm.id.as_str());
            list = list.child(dm_row(s, dm, active, sc, scale));
        }
        if !unpinned.is_empty() {
            list = list.child(
                row()
                    .w_full()
                    .h(16.0)
                    .center_y()
                    .mt(6.0)
                    .mb(2.0)
                    .px(8.0)
                    .child(
                        text("Direct messages")
                            .font(font_px(12.0 * sc, 600.0))
                            .color(fg_muted()),
                    ),
            );
        }
    }
    let unpinned_ids: Vec<String> = unpinned.iter().map(|d| d.id.clone()).collect();
    if !unpinned_ids.is_empty() {
        let count = unpinned_ids.len();
        let ids_key = Rc::new(unpinned_ids);
        let ids_build = Rc::clone(&ids_key);
        list = list.child(
            col()
                .key("dc-dm-unpinned")
                .w_full()
                .gap(2.0)
                .growth(GrowthSpec::default())
                .virtual_rows(
                    count,
                    52.0 * sc,
                    move |_s: &AppState, i| Cow::Owned(format!("dc-dm-{}", ids_key[i])),
                    move |s: &AppState, i| {
                        let Some(id) = ids_build.get(i) else {
                            return col();
                        };
                        let Some(dm) = s.discord.home.dms.iter().find(|d| d.id == *id) else {
                            return col();
                        };
                        let active = s.discord.place == DiscordPlace::Messages
                            && s.discord.active_dm_id.as_deref() == Some(dm.id.as_str());
                        dm_row(s, dm, active, sc, scale)
                    },
                ),
        );
    }
    if pinned.is_empty() && unpinned.is_empty() {
        list = list.child(
            text("No direct messages")
                .font(font_px(13.0 * sc, 400.0))
                .color(fg_muted())
                .px(8.0)
                .py(12.0),
        );
    }

    panel_frame(col().key("dc-dm-sidebar").w(pane_w).h_full())
        .child(head)
        .child(
            col()
                .key("dc-dm-scroll")
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(list),
        )
        .child(me_panel(s, sc, scale))
}

fn guild_channel_row(
    s: &AppState,
    guild_id: &str,
    ch: &DiscordChannel,
    active: bool,
    can_manage: bool,
    sc: f32,
    scale: f32,
) -> El {
    let cid = ch.id.clone();
    let gid = guild_id.to_string();
    let is_voice = matches!(ch.kind.as_str(), "voice" | "stage");
    let in_this_voice = is_voice
        && s.discord.voice.channel_id.as_deref() == Some(ch.id.as_str())
        && s.discord.voice.state != "off";
    let is_unread = ch.unread && !ch.muted;

    let name_color = if active {
        FG
    } else if in_this_voice {
        good()
    } else if ch.muted {
        fg_faint()
    } else if is_unread {
        FG
    } else {
        fg_muted()
    };

    let icon_color = if active {
        FG
    } else if in_this_voice {
        good()
    } else if is_unread {
        FG
    } else {
        fg_faint()
    };
    let icon = if is_voice {
        if ch.locked {
            icon_lock(14.0, icon_color)
        } else {
            icon_volume(
                15.0,
                if in_this_voice {
                    good()
                } else if active {
                    FG
                } else {
                    fg_muted()
                },
            )
        }
    } else if ch.rules {
        icon_bookmark(14.0, icon_color)
    } else if ch.kind == "forum" {
        icon_chat(14.0, icon_color)
    } else if ch.channel_type == 5 {
        icon_sparkles(14.0, icon_color)
    } else if ch.locked {
        icon_lock(14.0, if active { FG } else { fg_muted() })
    } else {
        icon_hash(15.0, icon_color)
    };

    let ch_ctx = ContextMenuTarget::DiscordChannel {
        guild_id: guild_id.to_string(),
        channel_id: ch.id.clone(),
        name: ch.name.clone(),
        kind: ch.kind.clone(),
        unread: ch.unread || ch.mentions > 0,
        muted: ch.muted,
        can_edit: ch.can_edit,
        can_manage,
    };

    let mut head = row()
        .key(format!("dc-ch-{}", ch.id))
        .role("button")
        .label(ch.name.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .w_full()
        .h(34.0 * sc)
        .px(10.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_SMALL)
        .fill(if active { selected_fill() } else { TRANSPARENT })
        .hover(if active {
            selected_fill()
        } else {
            hover_fill()
        })
        .press(if active {
            selected_fill()
        } else {
            pressed_fill()
        })
        .hover_motion(Spring::NONE, SPRING_QUICK)
        .hit()
        .press_on_down()
        .on_press(move |s: &mut AppState| {
            s.open_discord_channel(&cid);
            if is_voice && !in_this_voice {
                s.join_discord_voice(Some(&gid), &cid);
            }
        })
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.open_context_menu(ch_ctx.clone(), p.x, p.y);
        })
        .child(el().size(16.0, 16.0).center().child(icon))
        .child(
            text(ch.name.clone())
                .font(font_px(
                    13.0 * sc,
                    if active || is_unread { 600.0 } else { 500.0 },
                ))
                .color(name_color)
                .grow(1.0)
                .truncate(),
        );

    if is_voice {
        let count = ch.members.as_ref().map(|m| m.len()).unwrap_or(0);
        if let Some(lim) = ch.user_limit
            && lim > 0
        {
            head = head.child(
                text(format!("{count}/{lim}"))
                    .font(font_px_tnum(11.0 * sc, 500.0))
                    .color(if active { fg_muted() } else { fg_faint() }),
            );
        } else if count > 0 {
            head = head.child(
                text(count.to_string())
                    .font(font_px_tnum(11.0 * sc, 600.0))
                    .color(good()),
            );
        }
    } else if ch.mentions > 0 {
        head = head.child(mention_badge(ch.mentions, None, sc));
    } else if is_unread && !active {
        head = head.child(unread_dot_row(FG));
    }

    let mut block = col().w_full().gap(2.0).child(head);
    if is_voice
        && let Some(ref members) = ch.members
        && !members.is_empty()
    {
        for m in members {
            let speaking = s.discord.speaking.contains(&m.id);
            let mem_ctx = ContextMenuTarget::DiscordMember {
                guild_id: Some(guild_id.to_string()),
                user_id: m.id.clone(),
                name: m.name.clone(),
                username: None,
                in_voice: true,
            };
            let mut mrow = row()
                .key(format!("dc-vc-mem-{}-{}", ch.id, m.id))
                .cursor(Cursor::Pointer)
                .w_full()
                .h(28.0 * sc)
                .pl(28.0)
                .pr(8.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .hover(hover_fill())
                .hit()
                .on_secondary_press(move |s: &mut AppState, p, _| {
                    s.open_context_menu(mem_ctx.clone(), p.x, p.y);
                })
                .child(avatar_box(
                    s,
                    m.avatar.as_deref(),
                    &m.name,
                    20.0,
                    10.0,
                    None,
                    speaking,
                    scale,
                ))
                .child(
                    text(m.name.clone())
                        .font(font_px(12.0 * sc, 400.0))
                        .color(if speaking { FG } else { fg_muted() })
                        .grow(1.0)
                        .truncate(),
                );
            if m.stream {
                mrow = mrow.child(
                    el().px(5.0)
                        .py(1.0)
                        .radius(4.0)
                        .fill(bad().alpha(0.22))
                        .child(text("LIVE").font(type_label()).color(bad())),
                );
            }
            if m.deaf || m.server_deaf {
                mrow = mrow.child(icon_headphones_off(11.0, fg_faint()));
            } else if m.mute || m.server_mute {
                mrow = mrow.child(icon_mic_off(11.0, fg_faint()));
            }
            block = block.child(mrow);
        }
    }
    block
}

fn guild_nav_row(key: &'static str, label: impl Into<String>, icon: El, sc: f32) -> El {
    let label = label.into();
    row()
        .key(key)
        .role("button")
        .label(label.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .w_full()
        .h(34.0 * sc)
        .px(8.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .hover(hover_fill())
        .hover_motion(Spring::NONE, SPRING_QUICK)
        .hit()
        .press_on_down()
        .child(el().size(16.0, 16.0).center().child(icon))
        .child(
            text(label)
                .font(font_px(13.0 * sc, 500.0))
                .color(fg_muted())
                .grow(1.0)
                .truncate(),
        )
}

fn server_left_rail(s: &AppState, guild: &DiscordGuild, sc: f32, scale: f32) -> El {
    let pane_w = 300.0 * sc;
    let head_h = 48.0 * sc;

    let mut tree = col().w_full().pt(4.0).px(8.0).pb(8.0).gap(2.0);
    if let Some(ref banner_url) = guild.banner
        && let Some(img) = s.discord_remote_image_sized(banner_url, 640.0, scale)
    {
        let banner_h = ((pane_w - 16.0) * 7.0 / 16.0).round();
        tree = tree.child(
            el().w_full()
                .h(banner_h)
                .mb(6.0)
                .radius(RADIUS_CARD)
                .clip_rounded(RADIUS_CARD)
                .pixels_fit(img.pixels(), SurfaceFit::Fill),
        );
    }

    if let Some(chans) = s.discord.channels_by_guild.get(&guild.id) {
        if let Some(ref rows) = chans.rows {
            let any_row = rows.guide || rows.events || rows.browse || rows.members;
            if rows.guide {
                tree = tree.child(
                    guild_nav_row(
                        "dc-guild-guide",
                        "Server Guide",
                        icon_bookmark(15.0, fg_muted()),
                        sc,
                    )
                    .on_press(|s: &mut AppState| s.open_discord_server_page(PAGE_GUIDE)),
                );
            }
            if rows.events {
                let gid_ev = guild.id.clone();
                let ev_label = if chans.events > 0 {
                    format!(
                        "{} {}",
                        chans.events,
                        if chans.events == 1 { "Event" } else { "Events" }
                    )
                } else {
                    "Events".into()
                };
                tree = tree.child(
                    guild_nav_row(
                        "dc-guild-events",
                        &ev_label,
                        icon_sparkles(15.0, fg_muted()),
                        sc,
                    )
                    .on_press(move |s: &mut AppState| s.open_discord_events(&gid_ev, false)),
                );
            }
            if rows.browse {
                tree = tree.child(
                    guild_nav_row(
                        "dc-guild-browse",
                        "Browse channels",
                        icon_grid(15.0, fg_muted()),
                        sc,
                    )
                    .on_press(|s: &mut AppState| s.open_discord_server_page(PAGE_BROWSE)),
                );
            }
            if rows.members {
                tree = tree.child(
                    guild_nav_row(
                        "dc-guild-members",
                        "Members",
                        icon_users(15.0, fg_muted()),
                        sc,
                    )
                    .on_press(|s: &mut AppState| s.open_discord_server_page(PAGE_MEMBERS)),
                );
            }
            if any_row {
                tree = tree.child(el().w_full().h(1.0).my(11.0).fill(hairline()));
            }
        }

        let hide_muted = s.discord.hide_muted_guilds.contains(&guild.id);
        let mut current_cat_collapsed = false;
        for ch in &chans.channels {
            if ch.resource {
                continue;
            }
            if ch.kind == "category" {
                let cid = ch.id.clone();
                let collapsed = s.discord.collapsed_categories.contains(&ch.id);
                current_cat_collapsed = collapsed;
                let cat_ctx = ContextMenuTarget::DiscordCategory {
                    guild_id: guild.id.clone(),
                    category_id: ch.id.clone(),
                    name: ch.name.clone(),
                };
                let gid_add = guild.id.clone();
                let cid_add = ch.id.clone();
                tree = tree.child(
                    row()
                        .key(format!("dc-cat-{}", ch.id))
                        .role("button")
                        .label(ch.name.clone())
                        .focusable(true)
                        .cursor(Cursor::Pointer)
                        .w_full()
                        .h(34.0)
                        .mt(14.0)
                        .pt(3.0)
                        .px(8.0)
                        .gap(4.0)
                        .center_y()
                        .hover(hover_fill())
                        .radius(RADIUS_SMALL)
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| s.toggle_discord_category(&cid))
                        .on_secondary_press(move |s: &mut AppState, p, _| {
                            s.open_context_menu(cat_ctx.clone(), p.x, p.y);
                        })
                        .child(if collapsed {
                            icon_chevron_right(11.0, fg_faint())
                        } else {
                            icon_chevron_down(11.0, fg_faint())
                        })
                        .child(
                            text(ch.name.to_uppercase())
                                .font(font_px(11.0 * sc, 600.0))
                                .color(fg_faint())
                                .grow(1.0)
                                .truncate(),
                        )
                        .children(chans.can_manage.then(|| {
                            el().key(format!("dc-cat-add-{}", ch.id))
                                .role("button")
                                .label("Create channel")
                                .focusable(true)
                                .cursor(Cursor::Pointer)
                                .hit()
                                .size(22.0, 22.0)
                                .radius(RADIUS_SMALL)
                                .hover(hover_fill())
                                .center()
                                .on_press(move |s: &mut AppState| {
                                    s.open_create_channel_dialog(
                                        &gid_add,
                                        Some(cid_add.clone()),
                                        false,
                                    );
                                })
                                .child(icon_plus(13.0, fg_faint()))
                        })),
                );
                continue;
            }
            let active = s.discord.active_channel_id.as_deref() == Some(ch.id.as_str());
            if hide_muted && ch.muted && !active {
                continue;
            }
            let keep_visible = active
                || ch.unread
                || ch.mentions > 0
                || ch.members.as_ref().is_some_and(|m| !m.is_empty());
            if current_cat_collapsed && !keep_visible {
                continue;
            }
            tree = tree.child(guild_channel_row(
                s,
                &guild.id,
                ch,
                active,
                chans.can_manage,
                sc,
                scale,
            ));
            for th in chans
                .threads
                .iter()
                .filter(|t| t.joined && !t.archived && t.parent_id == ch.id)
            {
                let t_active = s.discord.active_channel_id.as_deref() == Some(th.id.as_str());
                let tid = th.id.clone();
                let tname = th.name.clone();
                let tlocked = th.locked;
                tree = tree.child(
                    row()
                        .key(format!("dc-thr-{}", th.id))
                        .role("button")
                        .label(th.name.clone())
                        .focusable(true)
                        .cursor(Cursor::Pointer)
                        .w_full()
                        .h(30.0 * sc)
                        .pl(28.0)
                        .pr(8.0)
                        .gap(6.0)
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .fill(if t_active {
                            selected_fill()
                        } else {
                            TRANSPARENT
                        })
                        .hover(hover_fill())
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| {
                            s.open_discord_thread(&tid, &tname, tlocked)
                        })
                        .child(text("↳").font(font_px(12.0 * sc, 400.0)).color(fg_faint()))
                        .child(
                            text(th.name.clone())
                                .font(font_px(
                                    12.5 * sc,
                                    if t_active || th.unread { 600.0 } else { 400.0 },
                                ))
                                .color(if t_active || th.unread {
                                    FG
                                } else {
                                    fg_muted()
                                })
                                .grow(1.0)
                                .truncate(),
                        ),
                );
            }
        }
    } else {
        tree = tree.child(
            text("Loading channels…")
                .font(font_px(12.0 * sc, 400.0))
                .color(fg_muted())
                .px(12.0)
                .py(12.0),
        );
    }

    let srv_ctx = ContextMenuTarget::DiscordServer {
        guild_id: guild.id.clone(),
        name: guild.name.clone(),
        muted: guild.muted,
        owner: guild.owner,
        icon: guild.icon.clone(),
    };
    let srv_ctx_btn = srv_ctx.clone();
    let gid_inv = guild.id.clone();
    let gname_inv = guild.name.clone();

    let srv_mark = avatar_box(
        s,
        guild.icon.as_deref(),
        &guild.name,
        24.0,
        6.0,
        None,
        false,
        scale,
    );

    let head = row()
        .key("dc-server-head")
        .role("button")
        .label(guild.name.clone())
        .focusable(true)
        .cursor(Cursor::Pointer)
        .w_full()
        .h(head_h)
        .pl(12.0)
        .pr(8.0)
        .gap(8.0)
        .center_y()
        .hit()
        .on_press(move |s: &mut AppState| {
            s.open_context_menu_at_element(srv_ctx_btn.clone(), "dc-server-head");
        })
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.open_context_menu(srv_ctx.clone(), p.x, p.y);
        })
        .child(srv_mark)
        .child(
            text(guild.name.clone())
                .font(font_px(15.0 * sc, 600.0))
                .color(FG)
                .grow(1.0)
                .truncate(),
        )
        .child(icon_chevron_down(13.0, fg_muted()))
        .child(
            icon_btn(
                "dc-server-invite".into(),
                "Invite people",
                false,
                false,
                icon_user_plus(15.0, fg_muted()),
            )
            .on_press(move |s: &mut AppState| {
                if let Some(ch) = s
                    .discord
                    .channels_by_guild
                    .get(&gid_inv)
                    .and_then(|c| {
                        c.channels
                            .iter()
                            .find(|x| {
                                s.discord.active_channel_id.as_deref() == Some(x.id.as_str())
                                    && x.kind != "category"
                            })
                            .or_else(|| c.channels.iter().find(|x| x.kind == "text"))
                    })
                    .cloned()
                {
                    s.open_discord_invite_dialog(&gname_inv, &ch.id, &ch.name);
                } else {
                    s.discord_server_action("invite", json!({ "guildId": gid_inv }));
                }
            }),
        );

    panel_frame(col().key("dc-server-rail").w(pane_w).h_full())
        .child(head)
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(tree),
        )
        .child(me_panel(s, sc, scale))
}

fn clan_tag_chip(s: &AppState, tag: &DiscordGuildTag, sc: f32, scale: f32) -> El {
    let mut chip = row()
        .h(18.0)
        .px(5.0)
        .gap(3.0)
        .center_y()
        .radius(4.0)
        .fill(Color::WHITE.alpha(0.05))
        .border(1.0, glass_rim());
    if let Some(url) = clan_badge_url(tag)
        && let Some(img) = s.discord_remote_image_sized(&url, 64.0, scale)
    {
        chip = chip.child(
            el().size(12.0, 12.0)
                .pixels_fit(img.pixels(), SurfaceFit::Fit),
        );
    }
    chip.child(
        text(tag.tag.clone())
            .font(font_px(11.0 * sc, 600.0))
            .color(FG),
    )
}

fn nameplate_backdrop(s: &AppState, plate: &DiscordNameplate, scale: f32) -> El {
    let mut layer = el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0));
    if let Some(rgb) = plate.palette.as_deref().and_then(nameplate_palette_rgb) {
        let base = hex(rgb, 1.0);
        layer = layer.linear_gradient(
            FRAC_PI_2,
            [
                (0.0, TRANSPARENT),
                (0.20, base.alpha(0.051)),
                (0.50, base.alpha(0.051)),
                (1.0, base.alpha(0.102)),
            ],
        );
    }
    if let Some(url) = nameplate_static_url(plate)
        && let Some(img) = s.discord_remote_image(&url, scale)
    {
        layer = layer.child(
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .opacity(0.30)
                .clip_fade_linear(160.0, 0.0, 0.0, 0.0)
                .pixels_fit(img.pixels(), SurfaceFit::Fill),
        );
    }
    layer
}

fn friend_card(s: &AppState, friend: &DiscordFriend, sc: f32, scale: f32) -> El {
    let uid = friend.id.clone();
    let look = friend.look.as_ref();
    let is_friend = friend.kind == "friend";

    let mut top_row = row()
        .w_full()
        .min_w(0.0)
        .gap(6.0)
        .center_y()
        .child(styled_name_text(
            text(friend.name.clone())
                .font(font_px(13.0 * sc, 600.0))
                .truncate(),
            look,
            FG,
        ));
    if let Some(tag) = look.and_then(|l| l.tag.as_ref())
        && !tag.tag.is_empty()
    {
        top_row = top_row.child(clan_tag_chip(s, tag, sc, scale));
    }

    let mut sub_row = row().w_full().min_w(0.0).gap(4.0).center_y();
    let sub_text = if friend.kind == "incoming" {
        "Incoming friend request".into()
    } else if friend.kind == "outgoing" {
        "Outgoing friend request".into()
    } else {
        if friend.status != "offline"
            && friend.activity.is_some()
            && let Some(ic) = activity_icon(friend.activity_type, 13.0, fg_muted())
        {
            sub_row = sub_row.child(ic);
        }
        activity_words(friend)
    };
    sub_row = sub_row.child(
        text(sub_text)
            .font(font_px(12.0 * sc, 400.0))
            .color(fg_muted())
            .grow(1.0)
            .truncate(),
    );

    let f_id_ctx = friend.id.clone();
    let f_name_ctx = friend.name.clone();
    let f_user_ctx = friend.username.clone();
    let mut card = glassy_ring(
        row()
            .key(format!("dc-friend-{}", friend.id))
            .role("button")
            .label(friend.name.clone())
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .on_secondary_press(move |s: &mut AppState, pos, _| {
                s.open_context_menu(
                    ContextMenuTarget::DiscordMember {
                        guild_id: None,
                        user_id: f_id_ctx.clone(),
                        name: f_name_ctx.clone(),
                        username: f_user_ctx.clone(),
                        in_voice: false,
                    },
                    pos.x,
                    pos.y,
                );
            })
            .w_full()
            .min_w(0.0)
            .h(60.0)
            .pl(12.0)
            .pr(10.0)
            .py(8.0)
            .gap(12.0)
            .center_y()
            .radius(RADIUS_CARD)
            .clip_rounded(RADIUS_CARD)
            .fill(card_surface())
            .hover(hover_fill())
            .hover_motion(Spring::NONE, SPRING_QUICK)
            .hover_group(),
    );

    if is_friend {
        let uid_open = uid.clone();
        card = card.on_press(move |s: &mut AppState| s.open_dm_with_friend(&uid_open));
    }

    if let Some(plate) = look.and_then(|l| l.nameplate.as_ref()) {
        card = card.child(nameplate_backdrop(s, plate, scale));
    }

    card = card
        .child(avatar_with_look(
            s,
            friend.avatar.as_deref(),
            look,
            &friend.name,
            40.0,
            20.0,
            Some(&friend.status),
            false,
            BG,
            scale,
        ))
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(2.0)
                .child(top_row)
                .child(sub_row),
        );

    if is_friend {
        let uid_dm = uid.clone();
        let uid_more = friend.id.clone();
        let name_more = friend.name.clone();
        let user_more = friend.username.clone();
        card = card.child(
            row()
                .pin(None, Some(12.0), Some(10.0), None)
                .px(4.0)
                .py(2.0)
                .gap(4.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(exo_kit::surface_elevated_hi().alpha(0.95))
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .group_opacity(0.0, 1.0)
                .child(
                    icon_btn(
                        format!("dc-fr-msg-{}", friend.id),
                        "Message",
                        false,
                        false,
                        icon_chat(16.0, FG),
                    )
                    .on_press(move |s: &mut AppState| s.open_dm_with_friend(&uid_dm)),
                )
                .child(
                    icon_btn(
                        format!("dc-fr-more-{}", friend.id),
                        "More options",
                        false,
                        false,
                        icon_more_horiz(16.0, FG),
                    )
                    .on_press({
                        let more_key = format!("dc-fr-more-{}", friend.id);
                        move |s: &mut AppState| {
                            s.open_context_menu_at_element(
                                ContextMenuTarget::DiscordMember {
                                    guild_id: None,
                                    user_id: uid_more.clone(),
                                    name: name_more.clone(),
                                    username: user_more.clone(),
                                    in_voice: false,
                                },
                                &more_key,
                            );
                        }
                    }),
                ),
        );
    } else if friend.kind == "incoming" {
        let uid_acc = uid.clone();
        let uid_dec = uid.clone();
        card = card.child(
            row()
                .gap(4.0)
                .center_y()
                .child(
                    icon_btn(
                        format!("dc-fr-acc-{}", friend.id),
                        "Accept",
                        false,
                        false,
                        icon_check(15.0, good()),
                    )
                    .on_press(move |s: &mut AppState| s.accept_discord_friend(&uid_acc)),
                )
                .child(
                    icon_btn(
                        format!("dc-fr-dec-{}", friend.id),
                        "Decline",
                        false,
                        true,
                        icon_close(15.0, bad()),
                    )
                    .on_press(move |s: &mut AppState| s.remove_discord_friend(&uid_dec)),
                ),
        );
    } else {
        let uid_can = uid.clone();
        card = card.child(
            icon_btn(
                format!("dc-fr-can-{}", friend.id),
                "Cancel request",
                false,
                true,
                icon_close(15.0, bad()),
            )
            .on_press(move |s: &mut AppState| s.remove_discord_friend(&uid_can)),
        );
    }

    card
}

fn blocked_card(s: &AppState, person: &DiscordRestricted, sc: f32, scale: f32) -> El {
    let uid = person.id.clone();
    glassy_ring(
        row()
            .key(format!("dc-blocked-{}", person.id))
            .w_full()
            .min_w(0.0)
            .h(60.0)
            .pl(12.0)
            .pr(10.0)
            .py(8.0)
            .gap(12.0)
            .center_y()
            .radius(RADIUS_CARD)
            .clip_rounded(RADIUS_CARD)
            .fill(card_surface()),
    )
    .child(avatar_box(
        s,
        person.avatar.as_deref(),
        &person.name,
        40.0,
        20.0,
        None,
        false,
        scale,
    ))
    .child(
        col()
            .grow(1.0)
            .min_w(0.0)
            .gap(2.0)
            .child(
                text(person.name.clone())
                    .font(font_px(13.0 * sc, 600.0))
                    .color(FG)
                    .truncate(),
            )
            .child(
                text(
                    person
                        .username
                        .as_deref()
                        .map(|u| format!("@{u}"))
                        .unwrap_or_else(|| "Blocked".into()),
                )
                .font(font_px(12.0 * sc, 400.0))
                .color(fg_muted())
                .truncate(),
            ),
    )
    .child(
        glassy_ring(
            row()
                .key(format!("dc-unblock-{}", person.id))
                .role("button")
                .label("Unblock")
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .h(30.0)
                .px(12.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(TRANSPARENT)
                .hover(hover_fill())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| s.remove_discord_friend(&uid)),
        )
        .child(text("Unblock").font(font_px(12.0 * sc, 500.0)).color(FG)),
    )
}

fn friends_panel(s: &AppState, sc: f32, scale: f32) -> El {
    let head_h = 48.0 * sc;
    let tab = s.discord.friends_tab;
    let q = s.discord.search_query.text.trim().to_lowercase();
    let incoming = s
        .discord
        .friends
        .iter()
        .filter(|f| f.kind == "incoming")
        .count() as i32;

    let mut tabs_row = row().gap(2.0).center_y();
    for t in DiscordFriendsTab::ALL {
        let active = tab == t;
        let mut pill = row()
            .key(t.key())
            .role("tab")
            .label(t.label())
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(32.0)
            .px(10.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(if active { FG } else { TRANSPARENT })
            .hover(if active { FG } else { hover_fill() })
            .hover_motion(Spring::NONE, SPRING_QUICK)
            .motion(SPRING_QUICK)
            .on_press_tx(move |s: &mut AppState, tx: &mut Tx| {
                if tx.by_keys() {
                    tx.instant();
                }
                s.discord.friends_tab = t;
            })
            .child(
                text(t.label())
                    .font(font_px(13.0 * sc, 500.0))
                    .color(if active { PRIMARY_FG } else { fg_muted() }),
            );
        if t == DiscordFriendsTab::Pending && incoming > 0 {
            pill = pill.child(mention_badge(incoming, None, sc));
        }
        tabs_row = tabs_row.child(pill);
    }

    let search_box = glassy_ring(
        row()
            .w(234.0)
            .h(32.0)
            .px(12.0)
            .gap(8.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(TRANSPARENT),
    )
    .child(icon_search(15.0, fg_muted()))
    .child(
        text_input(&s.discord.search_query, font_px(13.0 * sc, 400.0))
            .key("dc-friends-search")
            .label("Search friends")
            .cursor(Cursor::Text)
            .grow(1.0)
            .single_line()
            .color(FG)
            .placeholder("Search friends", fg_muted())
            .on_edit(edit_access(|s: &mut AppState| &mut s.discord.search_query)),
    );

    let add_btn = glassy_ring(
        row()
            .key("dc-add-friend-toggle")
            .role("button")
            .label("Add friend")
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(32.0)
            .px(12.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(if s.discord.add_friend_open {
                FG
            } else {
                TRANSPARENT
            })
            .hover(if s.discord.add_friend_open {
                FG
            } else {
                hover_fill()
            })
            .motion(SPRING_QUICK)
            .on_press(|s: &mut AppState| {
                s.discord.add_friend_open = !s.discord.add_friend_open;
            }),
    )
    .child(text("Add friend").font(font_px(13.0 * sc, 500.0)).color(
        if s.discord.add_friend_open {
            PRIMARY_FG
        } else {
            FG
        },
    ));

    let header = row()
        .w_full()
        .h(head_h)
        .pl(8.0)
        .gap(12.0)
        .center_y()
        .child(
            text("Friends")
                .key("dc-friends-title")
                .font(font_px(15.0 * sc, 600.0))
                .color(FG),
        )
        .child(tabs_row)
        .child(
            row()
                .grow(1.0)
                .justify_end()
                .gap(8.0)
                .center_y()
                .child(search_box)
                .child(add_btn),
        );

    let matches_name = |name: &str, username: Option<&str>| {
        q.is_empty()
            || name.to_lowercase().contains(&q)
            || username.is_some_and(|u| u.to_lowercase().contains(&q))
    };

    let cols = if sc > 1.15 { 2 } else { 3 };
    let add_bar = if s.discord.add_friend_open {
        Some(
            glassy_ring(
                row()
                    .key("dc-add-friend-bar")
                    .pin(None, Some(head_h + 4.0), Some(8.0), None)
                    .w(380.0)
                    .px(12.0)
                    .py(10.0)
                    .gap(8.0)
                    .center_y()
                    .radius(RADIUS_CARD)
                    .fill(BG)
                    .transition(inline_bar_transition(-6.0)),
            )
            .child(
                text_input(&s.discord.add_friend_input, font_px(13.0 * sc, 400.0))
                    .key("dc-add-friend-input")
                    .label("Enter a Discord username")
                    .cursor(Cursor::Text)
                    .grow(1.0)
                    .single_line()
                    .color(FG)
                    .placeholder("Enter a Discord username", fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.discord.add_friend_input
                    }))
                    .on_submit(|s: &mut AppState, _| s.submit_add_discord_friend()),
            )
            .child(
                primary_button("dc-add-friend-submit", "Send request", None)
                    .on_press(|s: &mut AppState| s.submit_add_discord_friend()),
            ),
        )
    } else {
        None
    };

    let mut pane = col()
        .key(format!("dc-friends-pane-{}", tab.key()))
        .w_full()
        .pt(4.0)
        .transition(Transition::opacity());

    if tab == DiscordFriendsTab::Blocked {
        let blocked_list: Vec<&DiscordRestricted> = s
            .discord
            .blocked
            .iter()
            .filter(|b| matches_name(&b.name, b.username.as_deref()))
            .collect();
        if !blocked_list.is_empty() {
            pane = pane.child(
                row()
                    .key("dc-friends-sec-blocked-row")
                    .w_full()
                    .h(16.0)
                    .center_y()
                    .mt(10.0)
                    .mb(4.0)
                    .mx(8.0)
                    .child(
                        text(format!("Blocked · {}", blocked_list.len()))
                            .font(font_px(12.0 * sc, 600.0))
                            .color(fg_muted()),
                    ),
            );
            let mut grid = el()
                .key("dc-friends-grid-blocked")
                .w_full()
                .equal_columns(cols)
                .gaps(8.0, 10.0);
            for person in blocked_list {
                grid = grid.child(blocked_card(s, person, sc, scale));
            }
            pane = pane.child(grid);
        } else {
            let empty_msg = if q.is_empty() {
                "No blocked users."
            } else {
                "No match by that name."
            };
            pane = pane.child(
                text(empty_msg)
                    .font(font_px(13.0 * sc, 400.0))
                    .color(fg_muted())
                    .px(8.0)
                    .py(12.0),
            );
        }
    } else {
        let listed: Vec<&DiscordFriend> = match tab {
            DiscordFriendsTab::Pending => s
                .discord
                .friends
                .iter()
                .filter(|f| f.kind != "friend" && matches_name(&f.name, f.username.as_deref()))
                .collect(),
            DiscordFriendsTab::All => s
                .discord
                .friends
                .iter()
                .filter(|f| f.kind == "friend" && matches_name(&f.name, f.username.as_deref()))
                .collect(),
            DiscordFriendsTab::Online => s
                .discord
                .friends
                .iter()
                .filter(|f| {
                    f.kind == "friend"
                        && f.status != "offline"
                        && matches_name(&f.name, f.username.as_deref())
                })
                .collect(),
            DiscordFriendsTab::Blocked => Vec::new(),
        };

        let (active, rest): (Vec<&DiscordFriend>, Vec<&DiscordFriend>) =
            if tab == DiscordFriendsTab::Online {
                listed.into_iter().partition(|f| f.activity.is_some())
            } else {
                (Vec::new(), listed)
            };

        if !active.is_empty() {
            pane = pane.child(
                row()
                    .key("dc-friends-sec-active-row")
                    .w_full()
                    .h(16.0)
                    .center_y()
                    .mt(10.0)
                    .mb(4.0)
                    .mx(8.0)
                    .child(
                        text("Active now")
                            .key("dc-friends-sec-active")
                            .font(font_px(12.0 * sc, 600.0))
                            .color(fg_muted()),
                    ),
            );
            let mut grid = el()
                .key("dc-friends-grid-active")
                .w_full()
                .equal_columns(cols)
                .gaps(8.0, 10.0);
            for f in &active {
                grid = grid.child(friend_card(s, f, sc, scale));
            }
            pane = pane.child(grid);
        }

        if !rest.is_empty() {
            pane = pane.child(
                row()
                    .key("dc-friends-sec-rest-row")
                    .w_full()
                    .h(16.0)
                    .center_y()
                    .mt(10.0)
                    .mb(4.0)
                    .mx(8.0)
                    .child(
                        text(tab.section_label())
                            .key("dc-friends-sec-rest")
                            .font(font_px(12.0 * sc, 600.0))
                            .color(fg_muted()),
                    ),
            );
            let mut grid = el()
                .key("dc-friends-grid-rest")
                .w_full()
                .equal_columns(cols)
                .gaps(8.0, 10.0);
            for f in &rest {
                grid = grid.child(friend_card(s, f, sc, scale));
            }
            pane = pane.child(grid);
        }

        if active.is_empty() && rest.is_empty() {
            let empty_msg = if !q.is_empty() {
                "No match by that name."
            } else if tab == DiscordFriendsTab::Pending {
                "No pending requests."
            } else {
                "No friends here yet."
            };
            pane = pane.child(
                text(empty_msg)
                    .font(font_px(13.0 * sc, 400.0))
                    .color(fg_muted())
                    .px(8.0)
                    .py(12.0),
            );
        }
    }

    let mut panel = panel_frame(
        col()
            .key("dc-friends-panel")
            .grow(1.0)
            .min_w(0.0)
            .h_full()
            .px(8.0)
            .pb(8.0),
    )
    .child(header)
    .child(
        col()
            .key("dc-friends-scroll")
            .grow(1.0)
            .w_full()
            .min_h(0.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(pane),
    );
    if let Some(bar) = add_bar {
        panel = panel.child(bar);
    }
    panel
}

fn channel_or_dm_stage(s: &AppState, sc: f32, scale: f32) -> El {
    let head_h = 48.0 * sc;
    match s.discord.place {
        DiscordPlace::Messages => {
            let Some(dm) = s.discord.active_dm() else {
                return panel_frame(
                    col().grow(1.0).h_full().center().child(
                        text("Select a conversation")
                            .font(type_section())
                            .color(fg_muted()),
                    ),
                );
            };
            let dm_id = dm.id.clone();
            let in_call = s.discord.voice.channel_id.as_deref() == Some(dm.id.as_str())
                && s.discord.voice.state != "off";

            let header = row()
                .w_full()
                .h(head_h)
                .px(16.0)
                .gap(10.0)
                .justify_between()
                .center_y()
                .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                .child(
                    row()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(10.0)
                        .center_y()
                        .child(avatar_with_look(
                            s,
                            dm.avatar.as_deref(),
                            dm.look.as_ref(),
                            &dm.name,
                            28.0,
                            14.0,
                            if dm.group { None } else { dm.status.as_deref() },
                            false,
                            BG,
                            scale,
                        ))
                        .child(
                            row()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(8.0)
                                .center_y()
                                .child(
                                    text(dm.name.clone())
                                        .font(font_px(14.0 * sc, 600.0))
                                        .color(FG)
                                        .truncate(),
                                )
                                .child(
                                    text(if dm.group {
                                        let count =
                                            dm.members.as_ref().map(|m| m.len() + 1).unwrap_or(2);
                                        format!("{count} Members")
                                    } else {
                                        dm.subtitle
                                            .clone()
                                            .filter(|sub| !sub.is_empty())
                                            .unwrap_or_else(|| {
                                                status_label(
                                                    dm.status.as_deref().unwrap_or("offline"),
                                                )
                                                .into()
                                            })
                                    })
                                    .font(font_px(12.0 * sc, 400.0))
                                    .color(fg_muted())
                                    .truncate(),
                                ),
                        ),
                )
                .child({
                    let dm_id_vid = dm.id.clone();
                    row()
                        .gap(4.0)
                        .center_y()
                        .child(
                            icon_btn(
                                if in_call {
                                    "dc-dm-leave".into()
                                } else {
                                    "dc-dm-call".into()
                                },
                                if in_call {
                                    "End call"
                                } else {
                                    "Start a voice call"
                                },
                                in_call,
                                in_call,
                                if in_call {
                                    icon_phone_off(16.0, bad())
                                } else {
                                    icon_phone(16.0, fg_muted())
                                },
                            )
                            .on_press(move |s: &mut AppState| {
                                if in_call {
                                    s.leave_discord_voice();
                                } else {
                                    s.join_discord_voice(None, &dm_id);
                                }
                            }),
                        )
                        .child(
                            icon_btn(
                                "dc-dm-video".into(),
                                "Start a video call",
                                false,
                                false,
                                exo_kit::icon_camera(16.0, fg_muted()),
                            )
                            .on_press(move |s: &mut AppState| {
                                if !in_call {
                                    s.join_discord_voice(None, &dm_id_vid);
                                }
                            }),
                        )
                        .child(discord_chat::header_tools(s))
                });

            let chat_body = discord_chat::timeline_and_composer(
                s,
                &dm.id,
                if dm.group {
                    format!("Message {}", dm.name)
                } else {
                    format!("Message @{}", dm.name)
                },
                dm.name.clone(),
                dm.subtitle
                    .clone()
                    .unwrap_or_else(|| "This is the start of your conversation.".into()),
                true,
                scale,
            );

            panel_frame(col().key("dc-dm-stage").grow(1.0).min_w(0.0).h_full())
                .child(header)
                .child(chat_body)
        }
        DiscordPlace::Servers => {
            if let Some(ref page_id) = s.discord.admin.active_server_page
                && let Some(ref gid) = s.discord.active_guild_id
            {
                let chans = s.discord.channels_by_guild.get(gid);
                return discord_admin::server_page_view(s, gid, chans, page_id, scale);
            }
            let Some(ch) = s.discord.active_channel() else {
                return panel_frame(
                    col().grow(1.0).h_full().center().child(
                        text("Pick a channel")
                            .font(type_section())
                            .color(fg_muted()),
                    ),
                );
            };
            let gid = s.discord.active_guild_id.clone();
            if ch.kind == "forum" || matches!(ch.channel_type, 15 | 16) {
                return discord_admin::forum_view(s, ch, scale);
            }
            let is_voice = matches!(ch.kind.as_str(), "voice" | "stage");
            if is_voice {
                return discord_voice::voice_stage_view(s, gid.as_deref(), ch, scale);
            }

            let guild_prefix = s
                .discord
                .active_guild()
                .map(|g| format!("{} · ", g.name))
                .unwrap_or_else(|| "· ".into());
            let header = row()
                .w_full()
                .h(head_h)
                .px(16.0)
                .gap(10.0)
                .justify_between()
                .center_y()
                .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                .child(
                    row()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(8.0)
                        .center_y()
                        .child(icon_hash(16.0, fg_muted()))
                        .child(
                            text(ch.name.clone())
                                .font(font_px(15.0 * sc, 600.0))
                                .color(FG),
                        )
                        .child(if let Some(ref topic) = ch.topic {
                            text(format!("{guild_prefix}{topic}"))
                                .font(font_px(12.0 * sc, 400.0))
                                .color(fg_muted())
                                .grow(1.0)
                                .truncate()
                        } else if let Some(ref st) = ch.status {
                            text(format!("{guild_prefix}{st}"))
                                .font(font_px(12.0 * sc, 400.0))
                                .color(fg_muted())
                                .grow(1.0)
                                .truncate()
                        } else {
                            el()
                        }),
                )
                .child(discord_chat::header_tools(s));

            let stage_pane = discord_chat::timeline_and_composer(
                s,
                &ch.id,
                format!("Message #{}", ch.name),
                format!("Welcome to #{}", ch.name),
                ch.topic
                    .clone()
                    .unwrap_or_else(|| format!("This is the start of the #{} channel.", ch.name)),
                ch.can_send != Some(false),
                scale,
            );

            panel_frame(col().key("dc-channel-stage").grow(1.0).min_w(0.0).h_full())
                .child(header)
                .child(stage_pane)
        }
        DiscordPlace::Home => el(),
    }
}

fn right_members_panel(s: &AppState, sc: f32, scale: f32) -> Option<El> {
    if !s.discord.show_members {
        return None;
    }
    let people_w = 300.0 * sc;
    let head_h = 48.0 * sc;
    match s.discord.place {
        DiscordPlace::Servers => {
            let gid = s.discord.active_guild_id.as_deref()?;
            let mut list = col().w_full().pt(4.0).px(8.0).pb(76.0).gap(2.0);
            let header_text = if let Some(ml) = s.discord.members_by_guild.get(gid) {
                for (idx, item) in ml.items.iter().enumerate() {
                    let Some(it) = item else { continue };
                    match it {
                        DiscordMemberItem::Group { group, count } => {
                            list = list.child(
                                text(format!("{group} ({count})"))
                                    .font(font_px(12.0 * sc, 600.0))
                                    .color(fg_muted())
                                    .pt(if idx == 0 { 6.0 } else { 12.0 })
                                    .px(8.0)
                                    .pb(4.0),
                            );
                        }
                        DiscordMemberItem::Member(m) => {
                            let offline = m.status == "offline";
                            let uid = m.id.clone();
                            let gid_open = gid.to_string();
                            let name_open = m.name.clone();
                            let av_open = m.avatar.clone();
                            let uid_ctx = m.id.clone();
                            let gid_ctx = gid.to_string();
                            let name_ctx = m.name.clone();
                            let in_voice_ctx = m.in_voice.is_some();
                            let role_hex = m
                                .color
                                .as_deref()
                                .or_else(|| s.discord.settings_ext.guild_member_color(gid, &m.id));
                            let role_style = s.discord.settings_ext.role_style.as_str();
                            let name_color = if role_style == "username" {
                                lift_role_color(role_hex, offline)
                            } else {
                                if offline { fg_muted() } else { FG }
                            };
                            let mut name_row = row().gap(5.0).center_y();
                            if role_style == "dot"
                                && let Some(hx) = role_hex
                            {
                                name_row = name_row.child(
                                    el().size(8.0, 8.0)
                                        .radius(4.0)
                                        .fill(lift_role_color(Some(hx), offline)),
                                );
                            }
                            name_row = name_row.child(styled_name_text(
                                text(m.name.clone())
                                    .font(font_px(13.0 * sc, 500.0))
                                    .truncate(),
                                (!offline).then_some(m.look.as_deref()).flatten(),
                                name_color,
                            ));
                            if m.bot {
                                name_row = name_row.child(icon_check(12.0, fg_muted()));
                            }
                            if let Some(tag) = m.look.as_deref().and_then(|l| l.tag.as_ref())
                                && !tag.tag.is_empty()
                            {
                                name_row = name_row.child(clan_tag_chip(s, tag, sc, scale));
                            }
                            let sub = if m.in_voice.is_some() {
                                Some("In voice".to_string())
                            } else {
                                m.activity.clone()
                            };
                            list = list.child(
                                row()
                                    .key(format!("dc-mem-{}", m.id))
                                    .role("button")
                                    .label(m.name.clone())
                                    .focusable(true)
                                    .cursor(Cursor::Pointer)
                                    .w_full()
                                    .h(44.0 * sc)
                                    .px(8.0)
                                    .gap(10.0)
                                    .center_y()
                                    .radius(RADIUS_PILL)
                                    .hover(hover_fill())
                                    .hover_motion(Spring::NONE, SPRING_QUICK)
                                    .opacity(if offline { 0.55 } else { 1.0 })
                                    .hit()
                                    .press_on_down()
                                    .on_press(move |s: &mut AppState| {
                                        s.open_user_profile_dialog(
                                            crate::discord_chat_state::ProfileOpenRequest {
                                                user_id: &uid,
                                                guild_id: Some(&gid_open),
                                                fallback_name: &name_open,
                                                fallback_username: "",
                                                fallback_avatar: av_open.as_deref(),
                                                full: false,
                                                tab: crate::discord_chat_state::ProfileTab::About,
                                            },
                                        );
                                    })
                                    .on_secondary_press(move |s: &mut AppState, pos, _| {
                                        s.open_context_menu(
                                            ContextMenuTarget::DiscordMember {
                                                guild_id: Some(gid_ctx.clone()),
                                                user_id: uid_ctx.clone(),
                                                name: name_ctx.clone(),
                                                username: None,
                                                in_voice: in_voice_ctx,
                                            },
                                            pos.x,
                                            pos.y,
                                        );
                                    })
                                    .child(avatar_with_look(
                                        s,
                                        m.avatar.as_deref(),
                                        m.look.as_deref(),
                                        &m.name,
                                        32.0,
                                        16.0,
                                        if offline { None } else { Some(&m.status) },
                                        s.discord.speaking.contains(&m.id),
                                        BG,
                                        scale,
                                    ))
                                    .child(
                                        col().grow(1.0).min_w(0.0).gap(1.0).child(name_row).child(
                                            if let Some(sub_txt) = sub {
                                                text(sub_txt)
                                                    .font(font_px(11.0 * sc, 400.0))
                                                    .color(if m.in_voice.is_some() {
                                                        good()
                                                    } else {
                                                        fg_faint()
                                                    })
                                                    .truncate()
                                            } else {
                                                el()
                                            },
                                        ),
                                    ),
                            );
                        }
                    }
                }
                "Members".to_string()
            } else {
                list = list.child(
                    text("Loading members…")
                        .font(font_px(12.0 * sc, 400.0))
                        .color(fg_muted())
                        .px(10.0)
                        .py(10.0),
                );
                "Members".into()
            };

            Some(
                panel_frame(col().key("dc-members-panel").w(people_w).h_full())
                    .child(
                        row()
                            .w_full()
                            .h(head_h)
                            .px(16.0)
                            .center_y()
                            .child(text(header_text).font(font_px(14.0 * sc, 600.0)).color(FG)),
                    )
                    .child(
                        col()
                            .grow(1.0)
                            .w_full()
                            .min_h(0.0)
                            .scroll(ScrollSpec {
                                stick_bottom: false,
                            })
                            .child(list),
                    ),
            )
        }
        DiscordPlace::Messages => {
            let dm = s.discord.active_dm()?;
            if dm.group {
                let mut panel = panel_frame(
                    col()
                        .key("dc-dm-info")
                        .w(people_w)
                        .h_full()
                        .px(16.0)
                        .py(16.0)
                        .gap(12.0),
                );
                let members = dm.members.as_deref().unwrap_or(&[]);
                panel = panel.child(
                    text(format!("Members · {}", members.len() + 1))
                        .font(font_px(14.0 * sc, 600.0))
                        .color(FG),
                );
                let mut list = col().w_full().gap(4.0);
                for m in members {
                    let uid = m.id.clone();
                    let uid_ctx = m.id.clone();
                    let name_ctx = m.name.clone();
                    let user_ctx = m.username.clone();
                    list = list.child(
                        row()
                            .key(format!("dc-grp-mem-{}", m.id))
                            .role("button")
                            .label(m.name.clone())
                            .focusable(true)
                            .cursor(Cursor::Pointer)
                            .w_full()
                            .h(40.0 * sc)
                            .px(8.0)
                            .gap(10.0)
                            .center_y()
                            .radius(RADIUS_PILL)
                            .hover(hover_fill())
                            .hit()
                            .press_on_down()
                            .on_press(move |s: &mut AppState| s.open_dm_with_friend(&uid))
                            .on_secondary_press(move |s: &mut AppState, pos, _| {
                                s.open_context_menu(
                                    ContextMenuTarget::DiscordMember {
                                        guild_id: None,
                                        user_id: uid_ctx.clone(),
                                        name: name_ctx.clone(),
                                        username: user_ctx.clone(),
                                        in_voice: false,
                                    },
                                    pos.x,
                                    pos.y,
                                );
                            })
                            .child(avatar_box(
                                s,
                                m.avatar.as_deref(),
                                &m.name,
                                30.0,
                                15.0,
                                Some(&m.status),
                                false,
                                scale,
                            ))
                            .child(
                                text(m.name.clone())
                                    .font(font_px(13.0 * sc, 500.0))
                                    .color(FG)
                                    .truncate(),
                            ),
                    );
                }
                panel = panel.child(list);
                Some(panel)
            } else {
                let uid_opt = dm.user_id.as_deref();
                let loaded = uid_opt.and_then(|u| s.discord.profiles_by_user.get(u));
                let prof = loaded.and_then(|r| r.profile.as_ref());
                let banner_url = prof.and_then(|p| {
                    p.user_profile
                        .as_ref()
                        .and_then(|up| up.banner.as_deref())
                        .or(p.user.banner.as_deref())
                        .filter(|b| !b.is_empty())
                        .map(|hash| {
                            if hash.starts_with("http") {
                                hash.to_string()
                            } else {
                                format!(
                                    "https://cdn.discordapp.com/banners/{}/{hash}.png?size=600",
                                    p.user.id
                                )
                            }
                        })
                });
                let accent_color = prof
                    .and_then(|p| {
                        p.user_profile
                            .as_ref()
                            .and_then(|up| up.accent_color)
                            .or(p.user.accent_color)
                            .or_else(|| {
                                p.user_profile
                                    .as_ref()
                                    .and_then(|up| up.theme_colors.as_ref())
                                    .and_then(|tc| tc.first().copied())
                            })
                    })
                    .filter(|&c| c != 0)
                    .map(|c| hex(c, 1.0))
                    .or_else(|| {
                        prof.and_then(|p| p.user.banner_color.as_deref())
                            .and_then(parse_hex_color)
                    });
                let banner_el = if let Some(ref burl) = banner_url
                    && let Some(img) = s.discord_remote_image_sized(burl, 600.0, scale)
                {
                    el().w_full()
                        .h(105.0)
                        .clip_rounded(0.0)
                        .pixels_fit(img.pixels(), SurfaceFit::Fill)
                } else {
                    el().w_full()
                        .h(105.0)
                        .fill(accent_color.unwrap_or_else(subtle_fill))
                };

                let avatar_wrap = el().w_full().h(46.0).child(
                    el().pin(Some(16.0), Some(-42.0), None, None)
                        .size(86.0, 86.0)
                        .radius(43.0)
                        .fill(BG)
                        .center()
                        .child(avatar_with_look(
                            s,
                            dm.avatar.as_deref(),
                            dm.look.as_ref(),
                            &dm.name,
                            78.0,
                            39.0,
                            dm.status.as_deref(),
                            false,
                            BG,
                            scale,
                        )),
                );

                let pronouns = prof
                    .and_then(|p| p.user_profile.as_ref())
                    .and_then(|up| up.pronouns.as_deref())
                    .filter(|s| !s.is_empty());
                let handle_str = dm
                    .username
                    .as_deref()
                    .or_else(|| prof.map(|p| p.user.username.as_str()))
                    .unwrap_or("");
                let sub_handle = match (handle_str.is_empty(), pronouns) {
                    (false, Some(pr)) => format!("{handle_str} · {pr}"),
                    (false, None) => handle_str.to_string(),
                    (true, Some(pr)) => pr.to_string(),
                    (true, None) => String::new(),
                };
                let custom_st = loaded
                    .and_then(|r| r.extras.as_ref())
                    .and_then(|e| e.presence.as_ref())
                    .filter(|pr| pr.activity_type == Some(4))
                    .and_then(|pr| pr.activity.as_deref())
                    .filter(|t| !t.is_empty());

                let mut body = col().w_full().px(16.0).pb(18.0).gap(12.0).child(
                    col()
                        .gap(2.0)
                        .child(styled_name_text(
                            text(dm.name.clone()).font(type_title()),
                            dm.look.as_ref(),
                            accent_color.unwrap_or(FG),
                        ))
                        .children(
                            (!sub_handle.is_empty())
                                .then(|| text(sub_handle).font(type_small()).color(fg_muted())),
                        )
                        .children(custom_st.map(|ct| {
                            text(ct.to_string())
                                .font(type_small())
                                .color(FG)
                                .pt(4.0)
                                .w_full()
                                .wrap()
                        })),
                );

                if let Some(p) = prof {
                    if !p.badges.is_empty() {
                        let mut badge_row = row().gap(6.0).wrap().center_y();
                        for b in &p.badges {
                            let url = if b.icon.starts_with("http") {
                                b.icon.clone()
                            } else {
                                format!("https://cdn.discordapp.com/badge-icons/{}.png", b.icon)
                            };
                            if let Some(img) = s.discord_remote_image_sized(&url, 48.0, scale) {
                                badge_row = badge_row.child(
                                    el().size(22.0, 22.0)
                                        .label(b.description.clone())
                                        .pixels_fit(img.pixels(), SurfaceFit::Fit),
                                );
                            }
                        }
                        body = body.child(badge_row);
                    }

                    let bio = p
                        .user_profile
                        .as_ref()
                        .and_then(|up| up.bio.as_deref())
                        .filter(|t| !t.trim().is_empty())
                        .or_else(|| p.user.bio.as_deref().filter(|t| !t.trim().is_empty()));
                    if let Some(bio_txt) = bio {
                        body =
                            body.child(discord_md::render_markdown(bio_txt, &[], &p.user.id, true));
                    }

                    if let Some(since) = format_snowflake_date(&p.user.id) {
                        body = body.child(el().w_full().h(1.0).fill(hairline())).child(
                            col()
                                .gap(4.0)
                                .child(text("Member since").font(type_label()).color(fg_muted()))
                                .child(text(since).font(type_small()).color(FG)),
                        );
                    }

                    if !p.connected_accounts.is_empty() {
                        let mut conns = col()
                            .w_full()
                            .gap(6.0)
                            .child(text("Connections").font(type_label()).color(fg_muted()));
                        for acc in &p.connected_accounts {
                            let mut crow = row()
                                .w_full()
                                .px(10.0)
                                .py(7.0)
                                .gap(8.0)
                                .center_y()
                                .radius(RADIUS_SMALL)
                                .fill(subtle_fill())
                                .border(1.0, hairline())
                                .child(
                                    text(acc.name.clone())
                                        .font(type_small())
                                        .color(FG)
                                        .grow(1.0)
                                        .truncate(),
                                );
                            if acc.verified {
                                crow = crow.child(icon_check(13.0, good()));
                            }
                            conns = conns.child(crow);
                        }
                        body = body
                            .child(el().w_full().h(1.0).fill(hairline()))
                            .child(conns);
                    }
                } else if let Some(ref sub) = dm.subtitle {
                    body = body.child(
                        col()
                            .gap(4.0)
                            .child(text("Activity").font(type_label()).color(fg_muted()))
                            .child(
                                text(sub.clone())
                                    .font(type_small())
                                    .color(FG)
                                    .w_full()
                                    .wrap(),
                            ),
                    );
                }

                if let Some(uid_s) = uid_opt.map(str::to_string) {
                    let uid_save = uid_s.clone();
                    body = body.child(el().w_full().h(1.0).fill(hairline())).child(
                        col()
                            .w_full()
                            .gap(4.0)
                            .child(text("Note").font(type_label()).color(fg_muted()))
                            .child(
                                text_input(&s.discord.profile_note_input, type_small())
                                    .key("dc-dm-profile-note")
                                    .label("Note")
                                    .placeholder("Click to add a note", fg_faint())
                                    .w_full()
                                    .min_h(32.0)
                                    .px(8.0)
                                    .py(6.0)
                                    .radius(RADIUS_SMALL)
                                    .fill(subtle_fill())
                                    .color(FG)
                                    .single_line()
                                    .on_edit(edit_access(|s: &mut AppState| {
                                        &mut s.discord.profile_note_input
                                    }))
                                    .on_submit(move |s: &mut AppState, _| {
                                        s.save_discord_profile_note(&uid_save);
                                    }),
                            ),
                    );
                }

                if let Some(p) = prof {
                    let mutual_g: Vec<&DiscordGuild> = p
                        .mutual_guilds
                        .iter()
                        .filter_map(|mg| s.discord.home.guilds.iter().find(|g| g.id == mg.id))
                        .collect();
                    if !mutual_g.is_empty() {
                        let mut mlist = col().w_full().gap(4.0).child(
                            text(format!("Mutual servers — {}", mutual_g.len()))
                                .font(type_label())
                                .color(fg_muted()),
                        );
                        for g in mutual_g.into_iter().take(6) {
                            let gid_open = g.id.clone();
                            mlist = mlist.child(
                                row()
                                    .key(format!("dc-pf-mg-{}", g.id))
                                    .role("button")
                                    .label(g.name.clone())
                                    .focusable(true)
                                    .cursor(Cursor::Pointer)
                                    .hit()
                                    .w_full()
                                    .h(34.0)
                                    .px(6.0)
                                    .gap(8.0)
                                    .center_y()
                                    .radius(RADIUS_SMALL)
                                    .hover(hover_fill())
                                    .on_press(move |s: &mut AppState| {
                                        s.open_discord_guild(&gid_open);
                                    })
                                    .child(avatar_box(
                                        s,
                                        g.icon.as_deref(),
                                        &g.name,
                                        24.0,
                                        6.0,
                                        None,
                                        false,
                                        scale,
                                    ))
                                    .child(
                                        text(g.name.clone())
                                            .font(type_small())
                                            .color(FG)
                                            .truncate(),
                                    ),
                            );
                        }
                        body = body
                            .child(el().w_full().h(1.0).fill(hairline()))
                            .child(mlist);
                    }

                    let mf_list = uid_opt.and_then(|u| s.discord.mutual_friends_by_user.get(u));
                    let mf_count = p
                        .mutual_friends_count
                        .unwrap_or_else(|| mf_list.map(|v| v.len() as u32).unwrap_or(0));
                    if mf_count > 0 || mf_list.is_some_and(|v| !v.is_empty()) {
                        let mut flist = col().w_full().gap(4.0).child(
                            text(format!("Mutual friends — {mf_count}"))
                                .font(type_label())
                                .color(fg_muted()),
                        );
                        if let Some(friends) = mf_list {
                            for f in friends.iter().take(6) {
                                let fname = f
                                    .global_name
                                    .as_deref()
                                    .filter(|n| !n.is_empty())
                                    .unwrap_or(&f.username);
                                let fav = f.avatar_url();
                                let fid = f.id.clone();
                                let show_handle = !f.username.is_empty() && f.username != fname;
                                flist = flist.child(
                                    row()
                                        .key(format!("dc-pf-mf-{}", f.id))
                                        .role("button")
                                        .label(fname.to_string())
                                        .focusable(true)
                                        .cursor(Cursor::Pointer)
                                        .hit()
                                        .w_full()
                                        .h(if show_handle { 40.0 } else { 34.0 })
                                        .px(6.0)
                                        .gap(8.0)
                                        .center_y()
                                        .radius(RADIUS_SMALL)
                                        .hover(hover_fill())
                                        .on_press(move |s: &mut AppState| {
                                            s.open_dm_with_friend(&fid);
                                        })
                                        .child(avatar_box(
                                            s,
                                            fav.as_deref(),
                                            fname,
                                            26.0,
                                            13.0,
                                            None,
                                            false,
                                            scale,
                                        ))
                                        .child(
                                            col()
                                                .grow(1.0)
                                                .min_w(0.0)
                                                .child(
                                                    text(fname.to_string())
                                                        .font(type_small())
                                                        .color(FG)
                                                        .truncate(),
                                                )
                                                .children(show_handle.then(|| {
                                                    text(f.username.clone())
                                                        .font(type_label())
                                                        .color(fg_muted())
                                                        .truncate()
                                                })),
                                        ),
                                );
                            }
                        }
                        body = body
                            .child(el().w_full().h(1.0).fill(hairline()))
                            .child(flist);
                    }
                }

                Some(
                    panel_frame(
                        col()
                            .key("dc-dm-info")
                            .w(people_w)
                            .h_full()
                            .clip_rounded(RADIUS_PANEL),
                    )
                    .child(
                        col()
                            .grow(1.0)
                            .w_full()
                            .min_h(0.0)
                            .scroll(ScrollSpec {
                                stick_bottom: false,
                            })
                            .child(banner_el)
                            .child(avatar_wrap)
                            .child(body),
                    ),
                )
            }
        }
        DiscordPlace::Home => None,
    }
}

pub(crate) fn format_snowflake_date(id: &str) -> Option<String> {
    let raw: u64 = id.parse().ok()?;
    let ms = (raw >> 22) + 1_420_070_400_000;
    let secs = (ms / 1000) as i64;
    let days = secs.div_euclid(86_400);
    let z = days + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = if m <= 2 { y + 1 } else { y };
    let mon = match m {
        1 => "Jan",
        2 => "Feb",
        3 => "Mar",
        4 => "Apr",
        5 => "May",
        6 => "Jun",
        7 => "Jul",
        8 => "Aug",
        9 => "Sep",
        10 => "Oct",
        11 => "Nov",
        12 => "Dec",
        _ => "Jan",
    };
    Some(format!("{mon} {d}, {year}"))
}

pub fn can_show_room(s: &AppState) -> bool {
    s.discord.home.me.is_some()
        && s.discord.status.state != "signedOut"
        && s.discord.status.state != "error"
        && (s.discord.status.state == "ready"
            || s.discord.status.state == "connecting"
            || s.discord.status.signed_in
            || s.discord.from_snapshot)
}

pub fn view(s: &AppState, scale: f32) -> El {
    if !can_show_room(s) {
        return signed_out_view(s);
    }

    let sc = ui_scale(s);
    let classic_layout = s.settings.discord_layout.as_deref() == Some("classic");

    let mut body = row().key("dc-body").grow(1.0).w_full().min_h(0.0).gap(GAP);
    if classic_layout {
        body = body.child(classic_server_rail(s, sc, scale));
    }

    if s.discord.place == DiscordPlace::Servers
        && let Some(guild) = s.discord.active_guild()
    {
        let on_voice = s
            .discord
            .active_channel()
            .is_some_and(|c| matches!(c.kind.as_str(), "voice" | "stage"));
        body = body
            .child(server_left_rail(s, guild, sc, scale))
            .child(channel_or_dm_stage(s, sc, scale));
        if s.discord.inbox_open {
            body = body.child(crate::discord_search::inbox_panel(s, sc, scale));
        } else if !on_voice || !s.discord.voice_ui.show_voice_chat {
            if let Some(drawer) = discord_chat::chat_drawer_panel(s, scale) {
                body = body.child(drawer);
            } else if let Some(panel) = right_members_panel(s, sc, scale) {
                body = body.child(panel);
            }
        }
    } else if s.discord.place == DiscordPlace::Messages {
        body = body
            .child(dm_sidebar(s, sc, scale))
            .child(channel_or_dm_stage(s, sc, scale));
        if s.discord.inbox_open {
            body = body.child(crate::discord_search::inbox_panel(s, sc, scale));
        } else if let Some(drawer) = discord_chat::chat_drawer_panel(s, scale) {
            body = body.child(drawer);
        } else if let Some(panel) = right_members_panel(s, sc, scale) {
            body = body.child(panel);
        }
    } else {
        body = body
            .child(dm_sidebar(s, sc, scale))
            .child(if s.discord.shop_open {
                discord_admin::shop_view(s, scale)
            } else {
                friends_panel(s, sc, scale)
            });
        if s.discord.inbox_open {
            body = body.child(crate::discord_search::inbox_panel(s, sc, scale));
        }
    }

    let mut room_col = col()
        .key("discord-room")
        .w_full()
        .h_full()
        .pt(52.0)
        .px(GAP)
        .pb(GAP)
        .gap(GAP)
        .fill(BG);
    if s.discord.settings_ext.streamer_mode {
        room_col = room_col.child(
            row()
                .key("dc-streamer-banner")
                .w_full()
                .h(34.0)
                .px(14.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_CARD)
                .fill(warn().alpha(0.14))
                .border(1.0, warn().alpha(0.40))
                .child(
                    text("Streamer Mode is enabled — personal info, invite links, sounds, and notifications are hidden.")
                        .font(type_small())
                        .color(FG)
                        .truncate(),
                )
                .child(
                    glass_button("dc-streamer-off", "Disable", None)
                        .on_press(|s: &mut AppState| s.set_discord_streamer_mode(false)),
                ),
        );
    }
    if classic_layout {
        room_col.child(body)
    } else {
        room_col.child(server_band(s, sc, scale)).child(body)
    }
}

pub fn overlay_layers(s: &AppState) -> Vec<El> {
    let mut out = discord_voice::voice_overlay_layers(s);
    out.extend(discord_admin::overlay_layers(s));
    out.extend(discord_chat::chat_overlay_layers(s, 1.0));
    if s.discord.settings_open {
        out.push(discord_settings::settings_modal(s));
    }
    if s.discord.custom_status_open {
        out.push(discord_settings::custom_status_modal(s));
    }
    out
}

#[cfg(test)]
mod tests {
    use crate::discord_state::{DiscordPlace, DiscordState};
    use crate::discord_voice_state::VoicePanel;
    use crate::shell;
    use crate::state::{AppState, Room};
    use exo_rpc::{DiscordFriend, DiscordRich};
    use rune::motion_gate::Run;
    use rune::render::KIND_LIQUID_GLASS;
    use rune::{Env, Runtime};

    #[test]
    fn discord_classic_layout_switches_to_vertical_left_rail() {
        let mut state = demo_discord_state(false);
        state.settings.discord_layout = Some("classic".into());
        state.switch_room(Room::Discord);
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let (rail_rect, _) = rt.presented("dc-classic-rail").expect("dc-classic-rail");
        assert!(
            (rail_rect[0] - 10.0).abs() <= 1.0 && (rail_rect[2] - 68.0).abs() <= 1.0,
            "expected 68px vertical classic server rail at x=10, got {:?}",
            rail_rect
        );
        assert!(
            rt.presented("dc-server-band").is_none(),
            "classic layout must hide the top horizontal server band"
        );
    }

    fn demo_discord_state(reduce_motion: bool) -> AppState {
        let mut state = AppState::default();
        state.settings.reduce_motion = reduce_motion;
        let mut dc = DiscordState::demo();
        for (idx, (name, game)) in [
            ("Vex", "Valorant"),
            ("Nori", "Balatro"),
            ("Juno", "Apex Legends"),
        ]
        .into_iter()
        .enumerate()
        {
            dc.friends.push(DiscordFriend {
                id: format!("20001{idx}"),
                name: name.into(),
                username: Some(name.to_lowercase()),
                avatar: None,
                look: None,
                kind: "friend".into(),
                status: "online".into(),
                activity: Some(game.into()),
                activity_type: Some(0),
                rich: Some(DiscordRich {
                    activity_type: 0,
                    name: game.into(),
                    details: None,
                    state: None,
                    start: None,
                    end: None,
                    sync_id: None,
                    uri: None,
                    image: None,
                    through_exo: false,
                }),
                prep: None,
                dm_id: None,
            });
        }
        dc.friends.push(DiscordFriend {
            id: "200019".into(),
            name: "Tess".into(),
            username: Some("tess".into()),
            avatar: None,
            look: None,
            kind: "friend".into(),
            status: "online".into(),
            activity: None,
            activity_type: None,
            rich: None,
            prep: None,
            dm_id: None,
        });
        state.discord = dc;
        state.switch_room(Room::Discord);
        state
    }

    fn glass_draws(rt: &Runtime<AppState>) -> usize {
        rt.instances
            .iter()
            .filter(|i| i.params[1] == KIND_LIQUID_GLASS)
            .count()
    }

    #[test]
    fn discord_friends_gridlines_five_cols_and_glass_bound() {
        let state = demo_discord_state(false);
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let glass = glass_draws(&rt);
        assert!(
            glass <= 3,
            "Discord friends: {glass} Liquid Glass draws, bound 3"
        );

        let (c0, _) = rt.presented("dc-friend-200001").expect("card 0");
        let (c1, _) = rt.presented("dc-friend-200002").expect("card 1");
        let (c2, _) = rt.presented("dc-friend-200003").expect("card 2");
        let (c3, _) = rt.presented("dc-friend-200004").expect("card 3");
        let (c4, _) = rt.presented("dc-friend-200010").expect("card 4");

        assert!((c0[1] - c1[1]).abs() <= 1.0, "card 1 not on row 0");
        assert!((c0[1] - c2[1]).abs() <= 1.0, "card 2 not on row 0");
        assert!(
            c3[1] > c0[1] + 40.0,
            "card 3 should wrap cleanly onto row 1"
        );
        assert!((c3[1] - c4[1]).abs() <= 1.0, "card 4 not on row 1");
        assert!(
            c0[2] >= 360.0,
            "expected spacious 3-column card width >= 360px, got {}",
            c0[2]
        );

        let (title, _) = rt.presented("dc-friends-title").expect("dc-friends-title");
        let (sec_active, _) = rt
            .presented("dc-friends-sec-active")
            .expect("dc-friends-sec-active");
        let (sec_rest, _) = rt
            .presented("dc-friends-sec-rest")
            .expect("dc-friends-sec-rest");
        assert!(
            (title[0] - sec_active[0]).abs() <= 1.0,
            "title x={} missed Active now x={}",
            title[0],
            sec_active[0]
        );
        assert!(
            (title[0] - sec_rest[0]).abs() <= 1.0,
            "title x={} missed Online x={}",
            title[0],
            sec_rest[0]
        );

        let (add_btn, _) = rt
            .presented("dc-add-friend-toggle")
            .expect("dc-add-friend-toggle");
        let (grid, _) = rt
            .presented("dc-friends-grid-active")
            .expect("dc-friends-grid-active");
        assert!(
            ((add_btn[0] + add_btn[2]) - (grid[0] + grid[2])).abs() <= 1.0,
            "Add friend right={} missed grid right={}",
            add_btn[0] + add_btn[2],
            grid[0] + grid[2]
        );

        let (me_trigger, _) = rt.presented("dc-me-trigger").expect("dc-me-trigger");
        let (dm_first, _) = rt.presented("dc-dm-dm-ada").expect("dc-dm-dm-ada");
        assert!(
            (me_trigger[0] - dm_first[0]).abs() <= 1.0,
            "me-trigger x={} missed first DM x={}",
            me_trigger[0],
            dm_first[0]
        );
    }

    #[test]
    fn discord_friends_tabs_and_add_bar_motion_gate() {
        for reduce_motion in [false, true] {
            let state = demo_discord_state(reduce_motion);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();
            run.act("switch to All", |rt| Run::tap(rt, "dc-friends-tab-all"));
            run.play(0.5);
            run.act("switch to Online", |rt| {
                Run::tap(rt, "dc-friends-tab-online")
            });
            run.play(0.5);
            run.act("open Add friend", |rt| Run::tap(rt, "dc-add-friend-toggle"));
            run.play(0.5);
            run.act("close Add friend", |rt| {
                Run::tap(rt, "dc-add-friend-toggle")
            });
            run.play(0.5);
            let findings = run.finish();
            assert!(
                findings.is_empty(),
                "friends motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }

    #[test]
    fn discord_chat_message_launch_motion_gate() {
        for reduce_motion in [false, true] {
            let mut state = demo_discord_state(reduce_motion);
            state.discord.place = DiscordPlace::Messages;
            state.discord.active_dm_id = Some("dm-ada".into());
            let initial_count = state
                .discord
                .chat
                .messages_by_channel
                .get("dm-ada")
                .map_or(0, Vec::len);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            rt.frame(0.0);
            rt.settle_all();
            rt.frame(1.0 / 60.0);
            let glass = glass_draws(&rt);
            assert!(
                glass <= 3,
                "Discord chat: {glass} Liquid Glass draws, bound 3"
            );

            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();
            run.act("send message", |rt| {
                rt.update(|s, _| s.discord.chat.composer.text = "Ready for queue".into());
                Run::tap(rt, "dc-composer-send");
            });
            run.play(0.5);
            run.act("open emoji bar", |rt| Run::tap(rt, "dc-composer-emoji"));
            run.play(0.5);
            run.act("close emoji bar", |rt| Run::tap(rt, "dc-composer-emoji"));
            run.play(0.5);
            assert_eq!(
                run.rt
                    .state
                    .discord
                    .chat
                    .messages_by_channel
                    .get("dm-ada")
                    .map_or(0, Vec::len),
                initial_count + 1
            );
            assert!(run.rt.state.discord.chat.composer.text.is_empty());
            let findings = run.finish();
            assert!(
                findings.is_empty(),
                "chat motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }

    #[test]
    fn discord_status_menu_and_voice_panels_motion_gate() {
        for reduce_motion in [false, true] {
            let state = demo_discord_state(reduce_motion);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();
            run.act("open status menu", |rt| Run::tap(rt, "dc-me-trigger"));
            run.play(0.5);
            run.act("close status menu", |rt| Run::tap(rt, "dc-me-trigger"));
            run.play(0.5);
            let findings = run.finish();
            assert!(
                findings.is_empty(),
                "status menu motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );

            let mut voice_state = demo_discord_state(reduce_motion);
            voice_state.discord.place = DiscordPlace::Servers;
            voice_state.discord.active_guild_id = voice_state
                .discord
                .home
                .guilds
                .first()
                .map(|g| g.id.clone());
            let mut vrt = Runtime::new(
                voice_state,
                shell::view,
                (1920.0, 1080.0),
                Env::new(true, 1.0),
            );
            vrt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
            vrt.set_system_reduced_motion(reduce_motion);
            let mut vrun = Run::new(vrt, 120.0);
            vrun.idle(0.5);
            vrun.step();
            vrun.act("open audio panel", |rt| {
                rt.update(|s, _| s.discord.voice_ui.panel = VoicePanel::Audio);
            });
            vrun.play(0.5);
            let glass = glass_draws(&vrun.rt);
            assert!(
                glass <= 4,
                "Discord voice audio panel: {glass} Liquid Glass draws, bound 4"
            );
            vrun.act("switch to GoLive panel", |rt| {
                rt.update(|s, _| s.discord.voice_ui.panel = VoicePanel::GoLive);
            });
            vrun.play(0.5);
            vrun.act("switch to Soundboard panel", |rt| {
                rt.update(|s, _| s.discord.voice_ui.panel = VoicePanel::Soundboard);
            });
            vrun.play(0.5);
            vrun.act("close voice panel", |rt| {
                rt.update(|s, _| s.discord.voice_ui.panel = VoicePanel::None);
            });
            vrun.play(0.5);
            let vfindings = vrun.finish();
            assert!(
                vfindings.is_empty(),
                "voice panels motion_gate findings (reduce_motion={reduce_motion}): {vfindings:?}"
            );
        }
    }
}
