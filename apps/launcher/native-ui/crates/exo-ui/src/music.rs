use crate::media_state::{MusicNav, MusicPanel, MusicRoomState};
use crate::state::{AppState, Room};
use exo_kit::{
    BG, FG, GAP, GLASS_CONTROL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL,
    RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK, TITLEBAR_HEIGHT, TRANSPARENT, bad, fg_faint, fg_muted,
    glass_capsule, glass_control_fill, glass_rim, glass_rim_bottom, glass_rim_top, good,
    hover_fill, icon_back, icon_close, icon_home, icon_maximize, icon_more_horiz, icon_music,
    icon_pause, icon_play, icon_plus, icon_search, icon_skip_back, icon_skip_forward,
    icon_sparkles, icon_volume, icon_volume_off, pressed_fill, primary_button,
};
use exo_rpc::{PlayerCard, PlayerSection, PlayerTrack, format_duration_ms};
use rune::{
    Color, El, ScrollSpec, Shape, SurfaceFit, TextStyle, canvas, col, edit_access, el, row, text,
    text_input,
};

const SIDEBAR_W: f32 = 272.0;
const ROW_RADIUS: f32 = 12.0;
const RECENT_ART_RADIUS: f32 = 9.0;

fn text_scale(s: &AppState) -> f32 {
    match s.settings.text_size.as_deref() {
        Some("small") => 0.9,
        Some("large") => 1.14,
        _ => 1.0,
    }
}

fn scaled_title(s: &AppState) -> TextStyle {
    let size = 20.0 * text_scale(s);
    TextStyle::new(size, 600.0).lh(size * 1.5)
}

fn scaled_body(s: &AppState) -> TextStyle {
    let size = 13.0 * text_scale(s);
    TextStyle::new(size, 400.0).lh(size * 1.3)
}

fn scaled_small(s: &AppState) -> TextStyle {
    let size = 12.0 * text_scale(s);
    TextStyle::new(size, 400.0).lh(size * 1.3)
}

fn scaled_mini_title(s: &AppState) -> TextStyle {
    let size = 12.0 * text_scale(s);
    TextStyle::new(size, 600.0).lh(size * 1.3)
}

fn art_surface_fill() -> Color {
    Color::WHITE.alpha(0.05)
}

fn track_bg_fill() -> Color {
    Color::WHITE.alpha(0.18)
}

fn strokes(size: f32, lines: &[&[[f32; 2]]], color: Color) -> El {
    let width = if size <= 16.0 { 1.8 } else { 2.0 };
    let mut shape = Shape::new().view_box(24.0, 24.0);
    for pts in lines {
        shape = shape.stroke(pts.to_vec(), width, color);
    }
    canvas(shape).size(size, size)
}

fn circle_pts(cx: f32, cy: f32, r: f32, steps: usize) -> Vec<[f32; 2]> {
    (0..=steps)
        .map(|i| {
            let a = (i as f32 / steps as f32) * std::f32::consts::TAU;
            [cx + r * a.cos(), cy + r * a.sin()]
        })
        .collect()
}

fn arc_pts(cx: f32, cy: f32, r: f32, start_deg: f32, end_deg: f32, steps: usize) -> Vec<[f32; 2]> {
    (0..=steps)
        .map(|i| {
            let t = i as f32 / steps as f32;
            let a = (start_deg + (end_deg - start_deg) * t).to_radians();
            [cx + r * a.cos(), cy + r * a.sin()]
        })
        .collect()
}

fn icon_list_music(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(vec![[4.0, 6.0], [16.0, 6.0]], 1.8, color)
        .stroke(vec![[4.0, 12.0], [12.0, 12.0]], 1.8, color)
        .stroke(vec![[4.0, 18.0], [10.0, 18.0]], 1.8, color)
        .stroke(vec![[16.0, 18.0], [16.0, 10.0], [21.0, 9.0]], 1.8, color)
        .stroke(circle_pts(14.5, 18.0, 2.0, 12), 1.8, color);
    canvas(shape).size(size, size)
}

fn icon_radio(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .fill(circle_pts(12.0, 12.0, 2.2, 12), color)
        .stroke(arc_pts(12.0, 12.0, 5.5, -45.0, 45.0, 10), 1.8, color)
        .stroke(arc_pts(12.0, 12.0, 5.5, 135.0, 225.0, 10), 1.8, color)
        .stroke(arc_pts(12.0, 12.0, 9.2, -45.0, 45.0, 12), 1.8, color)
        .stroke(arc_pts(12.0, 12.0, 9.2, 135.0, 225.0, 12), 1.8, color);
    canvas(shape).size(size, size)
}

fn icon_compass(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(circle_pts(12.0, 12.0, 9.0, 20), 1.8, color)
        .stroke(
            vec![
                [15.5, 8.5],
                [13.5, 13.5],
                [8.5, 15.5],
                [10.5, 10.5],
                [15.5, 8.5],
            ],
            1.6,
            color,
        );
    canvas(shape).size(size, size)
}

fn icon_shuffle(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[4.0, 17.0], [8.0, 17.0], [16.0, 7.0], [20.0, 7.0]],
            &[[17.0, 4.5], [20.0, 7.0], [17.0, 9.5]],
            &[[4.0, 7.0], [8.0, 7.0], [10.5, 10.2]],
            &[[13.5, 13.8], [16.0, 17.0], [20.0, 17.0]],
            &[[17.0, 14.5], [20.0, 17.0], [17.0, 19.5]],
        ],
        color,
    )
}

fn icon_repeat(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[17.0, 3.0], [20.0, 6.0], [17.0, 9.0]],
            &[[4.0, 11.0], [4.0, 8.0], [6.0, 6.0], [20.0, 6.0]],
            &[[7.0, 21.0], [4.0, 18.0], [7.0, 15.0]],
            &[[20.0, 13.0], [20.0, 16.0], [18.0, 18.0], [4.0, 18.0]],
        ],
        color,
    )
}

fn icon_disc(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(circle_pts(12.0, 12.0, 9.0, 20), 1.8, color)
        .stroke(circle_pts(12.0, 12.0, 2.5, 12), 1.8, color);
    canvas(shape).size(size, size)
}

fn icon_queue(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[9.0, 6.0], [20.0, 6.0]],
            &[[9.0, 12.0], [20.0, 12.0]],
            &[[9.0, 18.0], [20.0, 18.0]],
            &[[4.0, 6.0], [5.5, 6.0]],
            &[[4.0, 12.0], [5.5, 12.0]],
            &[[4.0, 18.0], [5.5, 18.0]],
        ],
        color,
    )
}

fn icon_heart(size: f32, color: Color) -> El {
    let shape = Shape::new().view_box(24.0, 24.0).fill(
        vec![
            [12.0, 20.0],
            [5.0, 13.5],
            [4.0, 9.5],
            [5.5, 6.0],
            [8.5, 5.0],
            [12.0, 7.5],
            [15.5, 5.0],
            [18.5, 6.0],
            [20.0, 9.5],
            [19.0, 13.5],
        ],
        color,
    );
    canvas(shape).size(size, size)
}

fn render_art(
    s: &AppState,
    url: &str,
    size: Option<f32>,
    radius: f32,
    is_liked: bool,
    scale: f32,
) -> El {
    let img = if url.is_empty() {
        None
    } else {
        s.remote_image(url, scale)
    };
    let mut box_el = el().radius(radius).clip_rounded(radius);
    if let Some(px) = size {
        box_el = box_el.size(px, px);
    } else {
        box_el = box_el.w_full().aspect_ratio(1.0);
    }
    if let Some(loaded) = img {
        box_el.pixels_fit(loaded.pixels(), SurfaceFit::Fill)
    } else if is_liked {
        box_el
            .fill(pressed_fill())
            .center()
            .child(icon_heart(size.map_or(28.0, |v| (v * 0.42).max(14.0)), FG))
    } else {
        box_el.fill(art_surface_fill()).center().child(icon_music(
            size.map_or(28.0, |v| (v * 0.36).max(14.0)),
            fg_faint(),
        ))
    }
}

fn nav_row(
    key: &str,
    label: &str,
    icon: El,
    active: bool,
    target: MusicNav,
    font: TextStyle,
) -> El {
    let mut r = row()
        .key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .press_on_down()
        .w_full()
        .min_h(44.0)
        .px(8.0)
        .py(4.0)
        .gap(10.0)
        .radius(ROW_RADIUS)
        .center_y()
        .hover(hover_fill())
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| s.music_open_nav(target.clone()))
        .child(el().size(36.0, 36.0).center().child(icon))
        .child(
            col()
                .key(format!("{key}-text"))
                .grow(1.0)
                .min_w(0.0)
                .child(text(label).font(font).color(FG).truncate()),
        );
    if active {
        r = r.fill(pressed_fill());
    }
    r
}

fn library_sidebar(s: &AppState, scale: f32) -> El {
    let svc = s.media.music_service;
    let row_font = scaled_body(s);
    let sub_font = scaled_small(s);
    let mut nav = col().w_full().gap(4.0).child(nav_row(
        "music-nav-home",
        "Home",
        icon_home(16.0, FG),
        s.media.music_nav == MusicNav::Home,
        MusicNav::Home,
        row_font,
    ));
    nav = nav.child(nav_row(
        "music-nav-songs",
        "Songs",
        icon_list_music(16.0, FG),
        s.media.music_nav == MusicNav::Songs,
        MusicNav::Songs,
        row_font,
    ));
    if svc.has_explore() {
        nav = nav.child(nav_row(
            "music-nav-explore",
            "Explore",
            icon_compass(16.0, FG),
            s.media.music_nav == MusicNav::Explore,
            MusicNav::Explore,
            row_font,
        ));
    }
    if svc.has_browse() {
        nav = nav.child(nav_row(
            "music-nav-new",
            "New",
            icon_sparkles(16.0, FG),
            s.media.music_nav == MusicNav::Browse,
            MusicNav::Browse,
            row_font,
        ));
    }
    if svc.has_radio() {
        nav = nav.child(nav_row(
            "music-nav-radio",
            "Radio",
            icon_radio(16.0, FG),
            s.media.music_nav == MusicNav::Radio,
            MusicNav::Radio,
            row_font,
        ));
    }

    let filter_label = s
        .media
        .music_library_filters
        .iter()
        .find(|f| f.id == s.media.music_library_filter)
        .map(|f| f.name.as_str())
        .unwrap_or("All");

    let filter_btn = el()
        .key("music-lib-filter")
        .role("button")
        .label("Filter library")
        .focusable(true)
        .hit()
        .press_on_down()
        .h(28.0)
        .px(12.0)
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .border(1.0, glass_rim())
        .hover(hover_fill())
        .center()
        .on_press(|s: &mut AppState| {
            let filters = &s.media.music_library_filters;
            if filters.len() > 1 {
                let idx = filters
                    .iter()
                    .position(|f| f.id == s.media.music_library_filter)
                    .unwrap_or(0);
                s.media.music_library_filter = filters[(idx + 1) % filters.len()].id.clone();
            }
        })
        .child(text(filter_label).font(sub_font).color(fg_muted()));

    let plus_btn = el()
        .key("music-lib-create")
        .role("button")
        .label("New playlist")
        .focusable(true)
        .hit()
        .press_on_down()
        .size(28.0, 28.0)
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .border(1.0, glass_rim())
        .hover(hover_fill())
        .center()
        .on_press(|s: &mut AppState| s.load_music_views())
        .child(icon_plus(14.0, FG));

    let side_head = row()
        .w_full()
        .mt(12.0)
        .px(8.0)
        .justify_between()
        .center_y()
        .gap(8.0)
        .child(filter_btn)
        .child(plus_btn);

    let active_uri = match &s.media.music_nav {
        MusicNav::Collection { uri, .. } => Some(uri.as_str()),
        _ => None,
    };

    let filtered_items =
        s.media
            .music_library
            .iter()
            .filter(|card| match s.media.music_library_filter.as_str() {
                "playlists" => card.kind == "playlist" || card.kind == "collection",
                "albums" => card.kind == "album",
                "artists" => card.kind == "artist",
                _ => true,
            });

    let lib_list = col()
        .key("music-library-scroll")
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .pb(12.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .children(filtered_items.enumerate().map(|(i, card)| {
            let is_artist = card.kind == "artist";
            let is_liked = card.kind == "collection" || card.title == svc.liked_name();
            let selected = active_uri == Some(card.uri.as_str());
            let art_r = if is_artist {
                RADIUS_PILL
            } else {
                RECENT_ART_RADIUS
            };
            let uri = card.uri.clone();
            let kind = card.kind.clone();
            let title = card.title.clone();
            let mut text_col = col()
                .key(format!("music-lib-{i}-text"))
                .grow(1.0)
                .min_w(0.0)
                .child(text(card.title.clone()).font(row_font).color(FG).truncate());
            if !is_artist && !card.subtitle.is_empty() {
                text_col = text_col.child(
                    text(card.subtitle.clone())
                        .font(sub_font)
                        .color(fg_muted())
                        .truncate(),
                );
            }
            let mut item_row = row()
                .key(format!("music-lib-{i}"))
                .role("button")
                .label(card.title.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .w_full()
                .min_h(44.0)
                .px(8.0)
                .py(4.0)
                .gap(10.0)
                .radius(ROW_RADIUS)
                .center_y()
                .hover(hover_fill())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    s.music_open_card(uri.clone(), kind.clone(), title.clone());
                })
                .child(render_art(
                    s,
                    &card.image,
                    Some(36.0),
                    art_r,
                    is_liked,
                    scale,
                ))
                .child(text_col);
            if selected {
                item_row = item_row.fill(pressed_fill());
            }
            item_row
        }));

    col()
        .key("music-sidebar")
        .w(SIDEBAR_W)
        .h_full()
        .min_h(0.0)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .fill(BG)
        .px(6.0)
        .pt(6.0)
        .gap(8.0)
        .child(nav)
        .child(side_head)
        .child(lib_list)
}

fn recent_grid(s: &AppState, scale: f32) -> El {
    let svc = s.media.music_service;
    let items: Vec<&PlayerCard> = s.media.music_recent.iter().take(8).collect();
    el().w_full()
        .mb(32.0)
        .equal_columns(4)
        .gap(10.0)
        .children(items.into_iter().enumerate().map(|(i, card)| {
            let is_artist = card.kind == "artist";
            let is_liked = card.kind == "collection" || card.title == svc.liked_name();
            let art_r = if is_artist {
                RADIUS_PILL
            } else {
                RECENT_ART_RADIUS
            };
            let uri = card.uri.clone();
            let kind = card.kind.clone();
            let title = card.title.clone();
            row()
                .key(format!("music-recent-{i}"))
                .role("button")
                .label(card.title.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .w_full()
                .min_w(0.0)
                .h(52.0)
                .pl(4.0)
                .pr(48.0)
                .py(4.0)
                .gap(12.0)
                .radius(RADIUS_CARD)
                .fill(TRANSPARENT)
                .border(1.0, glass_rim())
                .hover(hover_fill())
                .center_y()
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    s.music_open_card(uri.clone(), kind.clone(), title.clone());
                })
                .child(render_art(
                    s,
                    &card.image,
                    Some(44.0),
                    art_r,
                    is_liked,
                    scale,
                ))
                .child(
                    text(card.title.clone())
                        .font(scaled_body(s))
                        .color(FG)
                        .truncate()
                        .grow(1.0)
                        .min_w(0.0),
                )
        }))
}

fn shelf_section(s: &AppState, idx: usize, section: &PlayerSection, scale: f32) -> El {
    let mut head = row()
        .w_full()
        .mb(12.0)
        .justify_between()
        .center_y()
        .child(text(section.title.clone()).font(scaled_title(s)).color(FG));
    if let Some(ref more) = section.more {
        let uri = more.uri.clone();
        let kind = more.kind.clone();
        let title = section.title.clone();
        head = head.child(
            el().key(format!("music-shelf-more-{idx}"))
                .role("button")
                .label("Show all")
                .focusable(true)
                .hit()
                .press_on_down()
                .px(8.0)
                .py(4.0)
                .radius(RADIUS_SMALL)
                .hover(hover_fill())
                .on_press(move |s: &mut AppState| {
                    s.music_open_card(uri.clone(), kind.clone(), title.clone());
                })
                .child(text("Show all").font(scaled_small(s)).color(fg_muted())),
        );
    }

    let mut cards: Vec<El> = section
        .items
        .iter()
        .take(8)
        .enumerate()
        .map(|(j, card)| {
            let is_artist = card.kind == "artist";
            let art_r = if is_artist { RADIUS_PILL } else { RADIUS_CARD };
            let uri = card.uri.clone();
            let kind = card.kind.clone();
            let title = card.title.clone();
            let mut card_col = col()
                .key(format!("music-card-{idx}-{j}"))
                .role("button")
                .label(card.title.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .w_full()
                .min_w(0.0)
                .gap(4.0)
                .radius(RADIUS_CARD)
                .on_press(move |s: &mut AppState| {
                    s.music_open_card(uri.clone(), kind.clone(), title.clone());
                })
                .child(el().w_full().mb(8.0).child(render_art(
                    s,
                    &card.image,
                    None,
                    art_r,
                    false,
                    scale,
                )))
                .child(
                    text(card.title.clone())
                        .font(scaled_body(s))
                        .color(FG)
                        .truncate(),
                );
            if !card.subtitle.is_empty() {
                card_col = card_col.child(
                    text(card.subtitle.clone())
                        .font(scaled_small(s))
                        .color(fg_muted())
                        .truncate(),
                );
            }
            card_col
        })
        .collect();

    while cards.len() < 8 {
        cards.push(el().w_full().min_w(0.0));
    }

    col().w_full().mb(32.0).child(head).child(
        el().w_full()
            .pt(8.0)
            .pb(8.0)
            .equal_columns(8)
            .gap(32.0)
            .children(cards),
    )
}

fn track_row(
    s: &AppState,
    idx: usize,
    track: &PlayerTrack,
    context_uri: Option<&str>,
    scale: f32,
) -> El {
    let active = !s.media.music_track_uri.is_empty() && s.media.music_track_uri == track.uri;
    let uri = track.uri.clone();
    let ctx = context_uri.map(String::from);
    let artist = track.artist_line();
    let album = track
        .album
        .as_ref()
        .map(|a| a.name.clone())
        .unwrap_or_default();
    let dur = if track.duration_ms > 0.0 {
        format_duration_ms(track.duration_ms)
    } else {
        String::new()
    };

    row()
        .key(format!("music-track-{idx}"))
        .role("button")
        .label(track.title.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .w_full()
        .min_h(52.0)
        .px(8.0)
        .py(4.0)
        .gap(12.0)
        .radius(ROW_RADIUS)
        .center_y()
        .hover(hover_fill())
        .on_press(move |s: &mut AppState| {
            s.music_play_uri(&uri, ctx.as_deref(), Some(idx));
        })
        .child(render_art(
            s,
            &track.image,
            Some(44.0),
            RADIUS_SMALL,
            false,
            scale,
        ))
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .child(
                    text(track.title.clone())
                        .font(scaled_body(s))
                        .color(if active { good() } else { FG })
                        .truncate(),
                )
                .child(
                    text(artist)
                        .font(scaled_small(s))
                        .color(fg_muted())
                        .truncate(),
                ),
        )
        .child(
            text(album)
                .font(scaled_small(s))
                .color(fg_muted())
                .truncate()
                .w(240.0),
        )
        .child(text(dur).font(scaled_small(s)).color(fg_muted()).w(48.0))
}

fn main_panel_body(s: &AppState, scale: f32) -> El {
    match &s.media.music_room_state {
        MusicRoomState::SignedOut => {
            return col()
                .w_full()
                .py(120.0)
                .gap(12.0)
                .items_center()
                .child(
                    text(format!("Sign in to {}", s.media.music_service.label()))
                        .font(scaled_title(s))
                        .color(FG),
                )
                .child(
                    text("Connect your account to browse and play music inside Exo.")
                        .font(scaled_body(s))
                        .color(fg_muted()),
                )
                .child(
                    primary_button("music-sign-in-btn", "Sign in", None)
                        .on_press(|s: &mut AppState| s.music_sign_in()),
                );
        }
        MusicRoomState::NotPremium => {
            return col()
                .w_full()
                .py(120.0)
                .gap(10.0)
                .items_center()
                .child(
                    text("Subscription required")
                        .font(scaled_title(s))
                        .color(FG),
                )
                .child(
                    text("Sign in with an eligible account to play in Exo.")
                        .font(scaled_body(s))
                        .color(fg_muted()),
                )
                .child(
                    primary_button("music-switch-acct", "Sign in", None)
                        .on_press(|s: &mut AppState| s.music_sign_in()),
                );
        }
        MusicRoomState::Error(msg) => {
            return col()
                .w_full()
                .py(120.0)
                .gap(10.0)
                .items_center()
                .child(
                    text(if msg.is_empty() {
                        "Unable to reach music service"
                    } else {
                        msg.as_str()
                    })
                    .font(scaled_body(s))
                    .color(bad()),
                )
                .child(
                    primary_button("music-retry-btn", "Retry", None)
                        .on_press(|s: &mut AppState| s.load_music_room()),
                );
        }
        MusicRoomState::Loading => {
            if s.media.music_home_sections.is_empty() && s.media.music_recent.is_empty() {
                return col().w_full().py(140.0).items_center().child(
                    text(format!("Starting {}…", s.media.music_service.label()))
                        .font(scaled_body(s))
                        .color(fg_muted()),
                );
            }
        }
        MusicRoomState::Running => {}
    }

    match &s.media.music_nav {
        MusicNav::Home => {
            if s.media.music_home_loading
                && s.media.music_home_sections.is_empty()
                && s.media.music_recent.is_empty()
            {
                return col().w_full().py(140.0).items_center().child(
                    text(format!("Loading {}…", s.media.music_service.label()))
                        .font(scaled_body(s))
                        .color(fg_muted()),
                );
            }
            let mut body = col().w_full();
            if !s.media.music_recent.is_empty() {
                body = body.child(recent_grid(s, scale));
            }
            for (idx, section) in s.media.music_home_sections.iter().enumerate() {
                if !section.items.is_empty() {
                    body = body.child(shelf_section(s, idx, section, scale));
                }
            }
            body
        }
        MusicNav::Songs
        | MusicNav::Browse
        | MusicNav::Radio
        | MusicNav::Explore
        | MusicNav::Search
        | MusicNav::Collection { .. } => {
            let mut body = col().w_full();
            if let Some(ref data) = s.media.music_page_data {
                if let Some(ref hdr) = data.header {
                    let mut h_row = row().w_full().mb(24.0).gap(20.0).center_y();
                    if !hdr.image.is_empty() {
                        h_row = h_row.child(render_art(
                            s,
                            &hdr.image,
                            Some(160.0),
                            RADIUS_CARD,
                            false,
                            scale,
                        ));
                    }
                    let mut meta = col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(6.0)
                        .child(text(hdr.title.clone()).font(scaled_title(s)).color(FG));
                    if !hdr.subtitle.is_empty() {
                        meta = meta.child(
                            text(hdr.subtitle.clone())
                                .font(scaled_body(s))
                                .color(fg_muted()),
                        );
                    }
                    if let Some(ref desc) = hdr.description
                        && !desc.is_empty()
                    {
                        meta = meta.child(
                            text(desc.clone())
                                .font(scaled_small(s))
                                .color(fg_faint())
                                .wrap(),
                        );
                    }
                    body = body.child(h_row.child(meta));
                }
                for (idx, section) in data.sections.iter().enumerate() {
                    if !section.items.is_empty() {
                        body = body.child(shelf_section(s, idx, section, scale));
                    }
                }
                if !data.tracks.is_empty() {
                    let ctx = data.context_uri.as_deref();
                    body = body.child(
                        col().w_full().gap(2.0).children(
                            data.tracks
                                .iter()
                                .take(80)
                                .enumerate()
                                .map(|(i, t)| track_row(s, i, t, ctx, scale)),
                        ),
                    );
                }
            } else {
                body = body.child(text("Loading…").font(scaled_body(s)).color(fg_muted()));
            }
            body
        }
    }
}

fn section_title(s: &AppState) -> Option<&str> {
    match &s.media.music_nav {
        MusicNav::Home => Some("Home"),
        MusicNav::Songs => Some("Songs"),
        MusicNav::Browse => Some("New"),
        MusicNav::Radio => Some("Radio"),
        MusicNav::Explore => Some("Explore"),
        MusicNav::Search => Some("Search"),
        MusicNav::Collection { title, .. } => {
            if s.media
                .music_page_data
                .as_ref()
                .and_then(|d| d.header.as_ref())
                .is_some()
            {
                None
            } else {
                Some(title.as_str())
            }
        }
    }
}

fn main_panel(s: &AppState, scale: f32) -> El {
    let search_input = text_input(&s.media.music_search_edit, scaled_body(s))
        .key("music-search-input")
        .label("Search music")
        .grow(1.0)
        .single_line()
        .color(FG)
        .placeholder("Search", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| {
            &mut s.media.music_search_edit
        }))
        .on_submit(|s: &mut AppState, _| s.music_submit_search());

    let search_box = row()
        .key("music-search-box")
        .w(260.0)
        .h(32.0)
        .px(12.0)
        .gap(8.0)
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .border(1.0, glass_rim())
        .center_y()
        .child(icon_search(16.0, fg_muted()))
        .child(search_input);

    let mut head = row().w_full().h(32.0).mb(16.0).gap(12.0).center_y();
    if !s.media.music_history.is_empty() {
        head = head.child(
            el().key("music-back-btn")
                .role("button")
                .label("Back")
                .focusable(true)
                .hit()
                .press_on_down()
                .size(32.0, 32.0)
                .radius(RADIUS_PILL)
                .fill(TRANSPARENT)
                .border(1.0, glass_rim())
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| s.music_go_back())
                .child(icon_back(16.0, FG)),
        );
    }
    if let Some(title) = section_title(s) {
        head = head.child(
            text(title)
                .key("music-section-title")
                .font(scaled_title(s))
                .color(FG)
                .truncate(),
        );
    }
    head = head.child(el().grow(1.0)).child(search_box);

    el().key("music-main-panel")
        .grow(1.0)
        .min_w(0.0)
        .h_full()
        .min_h(0.0)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, glass_rim())
        .child(
            col()
                .key("music-main-scroll")
                .w_full()
                .h_full()
                .pt(20.0)
                .px(24.0)
                .pb(120.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(head)
                .child(main_panel_body(s, scale)),
        )
}

fn side_panel(s: &AppState, panel: MusicPanel, scale: f32) -> El {
    let title = match panel {
        MusicPanel::NowPlaying => "Now playing",
        MusicPanel::Queue => "Queue",
    };

    let header = row()
        .w_full()
        .mb(14.0)
        .justify_between()
        .center_y()
        .child(text(title).font(scaled_title(s)).color(FG))
        .child(
            el().key("music-panel-close")
                .role("button")
                .label("Close panel")
                .focusable(true)
                .hit()
                .press_on_down()
                .size(28.0, 28.0)
                .radius(RADIUS_PILL)
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| s.media.music_panel = None)
                .child(icon_close(14.0, fg_muted())),
        );

    let content = match panel {
        MusicPanel::NowPlaying => {
            if let Some(pb) = s.media.playback.as_ref().filter(|p| p.has_track()) {
                let art_url = pb.artwork_url.as_deref().unwrap_or("");
                col()
                    .w_full()
                    .gap(12.0)
                    .child(render_art(s, art_url, None, RADIUS_CARD, false, scale))
                    .child(text(pb.title.clone()).font(scaled_title(s)).color(FG))
                    .child(
                        text(pb.artist.clone())
                            .font(scaled_body(s))
                            .color(fg_muted()),
                    )
                    .children((!pb.album.is_empty()).then(|| {
                        text(pb.album.clone())
                            .font(scaled_small(s))
                            .color(fg_faint())
                    }))
            } else {
                col().w_full().py(40.0).items_center().child(
                    text("Nothing playing")
                        .font(scaled_body(s))
                        .color(fg_muted()),
                )
            }
        }
        MusicPanel::Queue => {
            if let Some(ref q) = s.media.music_queue_data {
                let mut list = col().w_full().gap(4.0);
                if let Some(ref cur) = q.current {
                    list = list
                        .child(
                            text("Now playing")
                                .font(scaled_small(s))
                                .color(fg_muted())
                                .mb(4.0),
                        )
                        .child(track_row(s, 0, cur, None, scale));
                }
                let upcoming: Vec<&PlayerTrack> =
                    q.queued.iter().chain(q.next.iter()).take(30).collect();
                if !upcoming.is_empty() {
                    list = list
                        .child(
                            text("Next")
                                .font(scaled_small(s))
                                .color(fg_muted())
                                .mt(12.0)
                                .mb(4.0),
                        )
                        .children(
                            upcoming
                                .into_iter()
                                .enumerate()
                                .map(|(i, t)| track_row(s, i + 1, t, None, scale)),
                        );
                }
                list
            } else {
                col().w_full().py(40.0).items_center().child(
                    text("Queue is empty")
                        .font(scaled_body(s))
                        .color(fg_muted()),
                )
            }
        }
    };

    col()
        .key("music-side-panel")
        .w(340.0)
        .h_full()
        .min_h(0.0)
        .mb(86.0)
        .padding(16.0)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, glass_rim())
        .child(header)
        .child(
            col()
                .key("music-panel-scroll")
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(content),
        )
}

fn bar_icon_btn(key: &str, label: &str, icon: El, size: f32, active: bool, disabled: bool) -> El {
    let mut b = el()
        .key(key)
        .role("button")
        .label(label)
        .focusable(!disabled)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(size, size)
        .radius(RADIUS_PILL)
        .center()
        .hover(hover_fill())
        .motion(SPRING_QUICK)
        .child(icon);
    if active {
        b = b.fill(pressed_fill());
    }
    if disabled {
        b = b.opacity(0.35);
    }
    b
}

fn volume_slider(key_prefix: &str, width: f32, level: f64, muted: bool) -> El {
    let pct = if muted {
        0.0
    } else {
        (level.clamp(0.0, 1.0)) as f32
    };
    let fill_w = (width * pct).clamp(0.0, width);
    let step_down = (level - 0.1).clamp(0.0, 1.0);
    let step_up = (level + 0.1).clamp(0.0, 1.0);

    el().key(format!("{key_prefix}-vol-slider"))
        .role("slider")
        .label("Volume")
        .focusable(true)
        .hit()
        .press_on_down()
        .w(width)
        .h(20.0)
        .center_y()
        .on_press(move |s: &mut AppState| {
            let next = if s.media.music_volume > 0.85 {
                step_down
            } else {
                step_up
            };
            s.music_set_volume(next, None);
        })
        .child(
            el().w(width)
                .h(3.0)
                .radius(2.0)
                .fill(track_bg_fill())
                .child(el().w(fill_w).h(3.0).radius(2.0).fill(FG)),
        )
        .child(
            el().absolute((fill_w - 5.0).clamp(0.0, width - 10.0), 5.0)
                .size(10.0, 10.0)
                .radius(5.0)
                .fill(FG),
        )
}

fn transport_bar(s: &AppState, scale: f32) -> El {
    let pb = s.media.playback.as_ref().filter(|p| p.has_track());
    let has_track = pb.is_some();
    let playing = pb.is_some_and(|p| p.is_playing());
    let art_url = pb.and_then(|p| p.artwork_url.as_deref()).unwrap_or("");
    let title = pb.map(|p| p.title.as_str()).unwrap_or("Nothing playing");
    let artist = pb.map(|p| p.artist.as_str()).unwrap_or("");

    let mut song_text = col().grow(1.0).min_w(0.0).child(
        text(title)
            .font(scaled_body(s))
            .color(if has_track { FG } else { fg_muted() })
            .truncate(),
    );
    if !artist.is_empty() {
        song_text = song_text.child(
            text(artist)
                .font(scaled_small(s))
                .color(fg_muted())
                .truncate(),
        );
    }

    let left = row()
        .grow(1.0)
        .min_w(0.0)
        .gap(12.0)
        .center_y()
        .child(render_art(
            s,
            art_url,
            Some(48.0),
            RADIUS_PILL,
            false,
            scale,
        ))
        .child(song_text);

    let shuffle_btn = bar_icon_btn(
        "music-shuffle",
        "Shuffle",
        icon_shuffle(
            16.0,
            if s.media.music_shuffle {
                FG
            } else {
                fg_muted()
            },
        ),
        32.0,
        s.media.music_shuffle,
        !s.media.music_can_shuffle && !has_track,
    )
    .on_press(|s: &mut AppState| s.music_toggle_shuffle());

    let prev_btn = bar_icon_btn(
        "music-prev",
        "Previous",
        icon_skip_back(18.0, FG),
        32.0,
        false,
        !has_track,
    )
    .on_press(|s: &mut AppState| s.music_control("prev"));

    let mut play_btn = el()
        .key("music-toggle")
        .role("button")
        .label(if playing { "Pause" } else { "Play" })
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(40.0, 40.0)
        .radius(RADIUS_PILL)
        .fill(FG)
        .center()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.music_control("toggle"))
        .child(if playing {
            icon_pause(18.0, PRIMARY_FG)
        } else {
            icon_play(18.0, PRIMARY_FG)
        });
    if !has_track {
        play_btn = play_btn.opacity(0.35);
    }

    let next_btn = bar_icon_btn(
        "music-next",
        "Next",
        icon_skip_forward(18.0, FG),
        32.0,
        false,
        !has_track,
    )
    .on_press(|s: &mut AppState| s.music_control("next"));

    let repeat_on = s.media.music_repeat > 0;
    let repeat_btn = bar_icon_btn(
        "music-repeat",
        "Repeat",
        icon_repeat(16.0, if repeat_on { FG } else { fg_muted() }),
        32.0,
        repeat_on,
        !s.media.music_can_repeat && !has_track,
    )
    .on_press(|s: &mut AppState| s.music_cycle_repeat());

    let center = row()
        .gap(8.0)
        .center_y()
        .child(shuffle_btn)
        .child(prev_btn)
        .child(play_btn)
        .child(next_btn)
        .child(repeat_btn);

    let muted = s.media.music_muted;
    let mute_btn = bar_icon_btn(
        "music-mute",
        if muted { "Unmute" } else { "Mute" },
        if muted {
            icon_volume_off(16.0, fg_muted())
        } else {
            icon_volume(16.0, fg_muted())
        },
        28.0,
        false,
        false,
    )
    .on_press(|s: &mut AppState| {
        let next = !s.media.music_muted;
        s.music_set_volume(s.media.music_volume, Some(next));
    });

    let vol_group = row()
        .gap(4.0)
        .center_y()
        .child(mute_btn)
        .child(volume_slider(
            "music-bar",
            96.0,
            s.media.music_volume,
            muted,
        ));

    let np_btn = bar_icon_btn(
        "music-panel-np",
        "Now playing",
        icon_disc(
            16.0,
            if s.media.music_panel == Some(MusicPanel::NowPlaying) {
                FG
            } else {
                fg_muted()
            },
        ),
        32.0,
        s.media.music_panel == Some(MusicPanel::NowPlaying),
        false,
    )
    .on_press(|s: &mut AppState| s.music_toggle_panel(MusicPanel::NowPlaying));

    let queue_btn = bar_icon_btn(
        "music-panel-queue",
        "Queue",
        icon_queue(
            16.0,
            if s.media.music_panel == Some(MusicPanel::Queue) {
                FG
            } else {
                fg_muted()
            },
        ),
        32.0,
        s.media.music_panel == Some(MusicPanel::Queue),
        false,
    )
    .on_press(|s: &mut AppState| s.music_toggle_panel(MusicPanel::Queue));

    let immerse_btn = bar_icon_btn(
        "music-immersion",
        "Full screen",
        icon_maximize(
            16.0,
            if s.media.music_immersion {
                FG
            } else {
                fg_muted()
            },
        ),
        32.0,
        s.media.music_immersion,
        false,
    )
    .on_press(|s: &mut AppState| s.media.music_immersion = !s.media.music_immersion);

    let more_btn = bar_icon_btn(
        "music-more",
        "More",
        icon_more_horiz(16.0, fg_muted()),
        32.0,
        false,
        false,
    )
    .on_press(|s: &mut AppState| s.load_music_room());

    let right = row()
        .grow(1.0)
        .min_w(0.0)
        .justify_end()
        .gap(8.0)
        .center_y()
        .child(vol_group)
        .child(np_btn)
        .child(queue_btn)
        .child(immerse_btn)
        .child(more_btn);

    let overlay_open = s.capture_menu_open
        || s.palette_open
        || s.key_modal_open
        || s.props_open
        || s.tour_step.is_some();

    let mut pill = row()
        .key("music-transport-bar")
        .w_full()
        .max_w(1040.0)
        .h(64.0)
        .pl(10.0)
        .pr(14.0)
        .gap(16.0)
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .center_y()
        .child(left)
        .child(center)
        .child(right);
    if !overlay_open {
        pill = pill.backdrop_blur(GLASS_CONTROL_SIGMA);
    }

    row()
        .pin(Some(SIDEBAR_W + 2.0 * GAP), None, Some(GAP), Some(18.0))
        .justify_center()
        .px(14.0)
        .child(pill)
}

fn immersion_view(s: &AppState, scale: f32) -> El {
    let pb = s.media.playback.as_ref().filter(|p| p.has_track());
    let art_url = pb.and_then(|p| p.artwork_url.as_deref()).unwrap_or("");
    let title = pb.map(|p| p.title.as_str()).unwrap_or("Nothing playing");
    let artist = pb.map(|p| p.artist.as_str()).unwrap_or("");
    let album = pb.map(|p| p.album.as_str()).unwrap_or("");

    let close_btn = el()
        .key("music-immersion-close")
        .role("button")
        .label("Exit full screen")
        .focusable(true)
        .hit()
        .press_on_down()
        .pin(None, Some(64.0), Some(24.0), None)
        .size(36.0, 36.0)
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .hover(hover_fill())
        .center()
        .on_press(|s: &mut AppState| s.media.music_immersion = false)
        .child(icon_close(16.0, FG));

    let stage = col()
        .w_full()
        .h_full()
        .pb(96.0)
        .center()
        .gap(20.0)
        .child(render_art(
            s,
            art_url,
            Some(340.0),
            RADIUS_PANEL,
            false,
            scale,
        ))
        .child(
            col()
                .items_center()
                .gap(4.0)
                .child(text(title).font(scaled_title(s)).color(FG))
                .children(
                    (!artist.is_empty())
                        .then(|| text(artist).font(scaled_body(s)).color(fg_muted())),
                )
                .children(
                    (!album.is_empty())
                        .then(|| text(album).font(scaled_small(s)).color(fg_faint())),
                ),
        );

    el().key("music-immersion-room")
        .w_full()
        .h_full()
        .fill(BG)
        .child(stage)
        .child(close_btn)
        .child(
            row()
                .pin(Some(GAP), None, Some(GAP), Some(18.0))
                .justify_center()
                .child(transport_bar(s, scale)),
        )
}

pub fn view(s: &AppState, scale: f32) -> El {
    if s.media.music_immersion {
        return immersion_view(s, scale);
    }

    let mut columns = row()
        .w_full()
        .h_full()
        .pt(TITLEBAR_HEIGHT)
        .px(GAP)
        .pb(GAP)
        .gap(GAP)
        .child(library_sidebar(s, scale))
        .child(main_panel(s, scale));

    if let Some(panel) = s.media.music_panel {
        columns = columns.child(side_panel(s, panel, scale));
    }

    el().key("music-room")
        .w_full()
        .h_full()
        .fill(BG)
        .child(columns)
        .child(transport_bar(s, scale))
}

pub fn mini_player(s: &AppState, scale: f32, slot: crate::state::MiniBarSlot) -> Option<El> {
    use crate::state::MiniBarSlot;

    if slot == MiniBarSlot::Hidden
        || s.room == Room::Music
        || s.tour_step.is_some()
        || s.palette_open
        || s.key_modal_open
        || s.props_open
        || s.media.open_capture_id.is_some()
    {
        return None;
    }
    let track = s.media.playback.as_ref().filter(|p| p.has_track())?;
    let playing = track.is_playing();
    let art_url = track.artwork_url.as_deref().unwrap_or("");
    let progress = match (track.position_ms, track.duration_ms) {
        (Some(pos), Some(dur)) if dur > 0 => (pos as f32 / dur as f32).clamp(0.0, 1.0),
        _ => 0.0,
    };

    let toggle_btn = el()
        .key("mini-toggle")
        .role("button")
        .label(if playing { "Pause" } else { "Play" })
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(28.0, 28.0)
        .radius(RADIUS_PILL)
        .center()
        .hover(hover_fill())
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.music_control("toggle"))
        .child(if playing {
            icon_pause(15.0, FG)
        } else {
            icon_play(15.0, FG)
        });

    let thumb = if let Some(img) = if art_url.is_empty() {
        None
    } else {
        s.remote_image(art_url, scale)
    } {
        el().size(24.0, 24.0)
            .radius(6.0)
            .clip_rounded(6.0)
            .pixels_fit(img.pixels(), SurfaceFit::Fill)
    } else {
        el().size(24.0, 24.0)
            .radius(6.0)
            .border(1.0, glass_rim())
            .center()
            .child(icon_music(12.0, fg_faint()))
    };

    if slot == MiniBarSlot::Compact {
        let open_btn = el()
            .key("mini-open-music")
            .role("button")
            .label("Open Music")
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .size(28.0, 28.0)
            .radius(RADIUS_SMALL)
            .center()
            .hover(hover_fill())
            .motion(SPRING_QUICK)
            .on_press(|s: &mut AppState| s.switch_room(Room::Music))
            .child(thumb);

        return Some(
            glass_capsule("music-mini-player")
                .px(4.0)
                .gap(4.0)
                .child(toggle_btn)
                .child(open_btn),
        );
    }

    let muted = s.media.music_muted;
    let mute_btn = el()
        .key("mini-mute")
        .role("button")
        .label(if muted { "Unmute" } else { "Mute" })
        .focusable(true)
        .hit()
        .press_on_down()
        .size(28.0, 28.0)
        .radius(RADIUS_PILL)
        .center()
        .hover(hover_fill())
        .on_press(|s: &mut AppState| {
            let next = !s.media.music_muted;
            s.music_set_volume(s.media.music_volume, Some(next));
        })
        .child(if muted {
            icon_volume_off(15.0, fg_muted())
        } else {
            icon_volume(15.0, fg_muted())
        });

    let full = slot == MiniBarSlot::Inline;
    let controls = if full {
        row()
            .gap(2.0)
            .center_y()
            .child(
                el().key("mini-prev")
                    .role("button")
                    .label("Previous track")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .size(28.0, 28.0)
                    .radius(RADIUS_PILL)
                    .center()
                    .hover(hover_fill())
                    .motion(SPRING_QUICK)
                    .on_press(|s: &mut AppState| s.music_control("prev"))
                    .child(icon_skip_back(15.0, FG)),
            )
            .child(toggle_btn)
            .child(
                el().key("mini-next")
                    .role("button")
                    .label("Next track")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .size(28.0, 28.0)
                    .radius(RADIUS_PILL)
                    .center()
                    .hover(hover_fill())
                    .motion(SPRING_QUICK)
                    .on_press(|s: &mut AppState| s.music_control("next"))
                    .child(icon_skip_forward(15.0, FG)),
            )
    } else {
        row().center_y().child(toggle_btn)
    };

    let lcd_w: f32 = if full { 164.0 } else { 104.0 };
    let seek_w: f32 = lcd_w;
    let lcd = row()
        .key("mini-open-music")
        .role("button")
        .label("Open Music")
        .focusable(true)
        .hit()
        .press_on_down()
        .h(30.0)
        .gap(8.0)
        .pr(if full { 8.0 } else { 6.0 })
        .radius(RADIUS_SMALL)
        .hover(hover_fill())
        .center_y()
        .on_press(|s: &mut AppState| s.switch_room(Room::Music))
        .child(thumb)
        .child(
            col()
                .w(lcd_w)
                .gap(4.0)
                .child(
                    row()
                        .w_full()
                        .gap(6.0)
                        .center_y()
                        .child(
                            text(track.title.clone())
                                .font(scaled_mini_title(s))
                                .color(FG)
                                .truncate(),
                        )
                        .children((full && !track.artist.is_empty()).then(|| {
                            text(track.artist.clone())
                                .font(scaled_small(s))
                                .color(fg_muted())
                                .truncate()
                                .grow(1.0)
                                .min_w(0.0)
                        })),
                )
                .child(
                    el().w(seek_w)
                        .h(3.0)
                        .radius(2.0)
                        .fill(track_bg_fill())
                        .child(
                            el().w((seek_w * progress).clamp(0.0, seek_w))
                                .h(3.0)
                                .radius(2.0)
                                .fill(FG),
                        ),
                ),
        );

    let volume = if full {
        row()
            .gap(6.0)
            .center_y()
            .child(mute_btn)
            .child(volume_slider("mini", 72.0, s.media.music_volume, muted))
    } else {
        row().center_y().child(mute_btn)
    };

    Some(
        glass_capsule("music-mini-player")
            .px(if full { 6.0 } else { 5.0 })
            .gap(if full { 10.0 } else { 6.0 })
            .child(controls)
            .child(lcd)
            .child(volume),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::shell;
    use rune::{Env, Runtime};

    #[test]
    fn music_home_pills_equal_width_and_shelf_cadence() {
        let mut state = AppState::default();
        state.room = Room::Music;
        state.settings.text_size = Some("small".into());
        if let Some(pill) = state.media.music_recent.get_mut(5) {
            pill.title = "Ultimate Pitch Perfect (Original Motion Picture Soundtrack)".into();
        }
        let sample_section = state.media.music_home_sections[0].clone();
        state.media.music_home_sections = vec![
            PlayerSection {
                title: "Best New Songs".into(),
                ..sample_section.clone()
            },
            PlayerSection {
                title: "New This Week".into(),
                ..sample_section.clone()
            },
            PlayerSection {
                title: "Recent Releases".into(),
                ..sample_section
            },
        ];

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);

        let p0 = rt.find("music-recent-0").expect("pill 0");
        let p1 = rt.find("music-recent-1").expect("pill 1");
        let p2 = rt.find("music-recent-2").expect("pill 2");
        let p3 = rt.find("music-recent-3").expect("pill 3");
        let pitch_01 = p1.0 - p0.0;
        let pitch_12 = p2.0 - p1.0;
        let pitch_23 = p3.0 - p2.0;
        assert!(
            (p0.0 - 508.5).abs() <= 1.0
                && (pitch_01 - 395.0).abs() <= 1.0
                && (pitch_12 - 395.0).abs() <= 1.0
                && (pitch_23 - 395.0).abs() <= 1.0,
            "pills drifted horizontally: p0={p0:?} p1={p1:?} p2={p2:?} p3={p3:?}"
        );

        let c0 = rt.find("music-card-0-0").expect("shelf 0 card 0");
        let c1 = rt.find("music-card-1-0").expect("shelf 1 card 0");
        let c2 = rt.find("music-card-2-0").expect("shelf 2 card 0");
        let shelf_01 = c1.1 - c0.1;
        let shelf_12 = c2.1 - c1.1;
        assert!(
            (c0.1 - 419.75).abs() <= 1.5
                && (shelf_01 - 300.5).abs() <= 1.5
                && (shelf_12 - 300.5).abs() <= 1.5,
            "shelf vertical alignment drifted: c0={c0:?} c1={c1:?} c2={c2:?} d01={shelf_01} d12={shelf_12}"
        );
    }

    #[test]
    fn music_sidebar_labels_align_and_toolbar_shows_section_title() {
        let mut state = AppState::default();
        state.room = Room::Music;

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);

        let nav_text = rt.find("music-nav-home-text").expect("nav home text");
        let lib_text = rt.find("music-lib-0-text").expect("library item 0 text");
        assert!(
            (nav_text.0 - lib_text.0).abs() <= 0.5,
            "sidebar text column jumped: nav={nav_text:?} lib={lib_text:?}"
        );

        let title = rt.find("music-section-title").expect("section title");
        let search = rt.find("music-search-box").expect("search box");
        assert!(
            title.0 < search.0 && (title.1 - search.1).abs() <= 4.0,
            "toolbar title not aligned with search: title={title:?} search={search:?}"
        );
    }

    #[test]
    fn mini_player_stays_visible_when_capture_or_library_menu_opens() {
        for capture in [true, false] {
            let mut state = AppState::default();
            state.room = Room::Library;
            state.mini_bar_slot = crate::state::MiniBarSlot::Inline;
            state.media.playback = Some(exo_rpc::MusicPlayback {
                status: "playing".into(),
                title: "Track".into(),
                artist: "Artist".into(),
                ..Default::default()
            });
            state.capture_menu_open = capture;
            state.library_more_open = !capture;

            assert!(mini_player(&state, 1.0, crate::state::MiniBarSlot::Inline).is_some());
            let budget = shell::BlurBudget::compute(&state, true);
            assert!(!budget.mini_player);
            let active_blurs = usize::from(budget.top_bar_search)
                + usize::from(budget.top_bar_nav)
                + usize::from(budget.top_bar_tools)
                + usize::from(budget.mini_player)
                + usize::from(budget.overlay)
                + usize::from(budget.toast);
            assert!(active_blurs <= rune::MAX_BACKDROP_BLURS);

            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.frame(0.0);
            assert!(rt.find("music-mini-player").is_some());
        }
    }
}
