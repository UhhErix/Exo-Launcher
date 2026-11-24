use crate::media_state::{
    MusicMenu, MusicNav, MusicPanel, MusicRoomState, MusicService, format_time_clock,
};
use crate::music_pages::{
    browse_page, discography_page, error_view, list_page, music_wait_room, playlist_sheet_overlay,
    room_state_replaces_chrome, search_page, sign_in_view, songs_page, wait_view,
};
use crate::music_sound::{lyrics_view, music_menus_overlay, side_panel, song_artists_row};
use crate::state::{AppState, Room};
use exo_kit::{
    BG, ExoGlass, FG, GAP, GLASS_PANEL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL,
    RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK, TITLEBAR_HEIGHT, TRANSPARENT, apply_glass, bad,
    fg_faint, fg_muted, glass_button, glass_capsule, glass_control_fill, glass_panel, glass_rim,
    glass_rim_bottom, glass_rim_top, good, hover_fill, icon_back, icon_chevron_left, icon_close,
    icon_folder, icon_home, icon_maximize, icon_more_horiz, icon_music, icon_pause, icon_pin,
    icon_play, icon_plus, icon_search, icon_skip_back, icon_skip_forward, icon_sparkles,
    icon_video, icon_volume, icon_volume_low, icon_volume_off, pressed_fill,
};
use exo_rpc::{PlayerCard, PlayerLink, PlayerSection};
use rune::{
    Align, Color, Cursor, El, Key, KeyEvent, Phase, ScrollSpec, Shape, Side, SurfaceFit, TextStyle,
    Tx, canvas, col, edit_access, el, row, text, text_input,
};

const SIDEBAR_W: f32 = 272.0;
pub(crate) const ROW_RADIUS: f32 = 12.0;
const RECENT_ART_RADIUS: f32 = 9.0;

fn text_scale(s: &AppState) -> f32 {
    match s.settings.text_size.as_deref() {
        Some("small") => 0.9,
        Some("large") => 1.14,
        _ => 1.0,
    }
}

pub(crate) fn scaled_title(s: &AppState) -> TextStyle {
    let size = 20.0 * text_scale(s);
    TextStyle::new(size, 600.0).lh(size * 1.5)
}

pub(crate) fn scaled_body(s: &AppState) -> TextStyle {
    let size = 13.0 * text_scale(s);
    TextStyle::new(size, 400.0).lh(size * 1.3)
}

pub(crate) fn scaled_small(s: &AppState) -> TextStyle {
    let size = 12.0 * text_scale(s);
    TextStyle::new(size, 400.0).lh(size * 1.3)
}

fn scaled_mini_title(s: &AppState) -> TextStyle {
    let size = 12.0 * text_scale(s);
    TextStyle::new(size, 600.0).lh(size * 1.3)
}

pub(crate) fn art_surface_fill() -> Color {
    Color::WHITE.alpha(0.05)
}

pub(crate) fn track_bg_fill() -> Color {
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

fn icon_repeat_one(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[17.0, 3.0], [20.0, 6.0], [17.0, 9.0]],
            &[[4.0, 11.0], [4.0, 8.0], [6.0, 6.0], [20.0, 6.0]],
            &[[7.0, 21.0], [4.0, 18.0], [7.0, 15.0]],
            &[[20.0, 13.0], [20.0, 16.0], [18.0, 18.0], [4.0, 18.0]],
            &[[12.0, 9.5], [12.0, 14.5]],
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

pub(crate) fn icon_chevron_up(size: f32, color: Color) -> El {
    strokes(size, &[&[[6.0, 15.0], [12.0, 9.0], [18.0, 15.0]]], color)
}

pub(crate) fn icon_heart(size: f32, color: Color, filled: bool) -> El {
    let pts = vec![
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
        [12.0, 20.0],
    ];
    let shape = if filled {
        Shape::new().view_box(24.0, 24.0).fill(pts, color)
    } else {
        Shape::new().view_box(24.0, 24.0).stroke(pts, 1.8, color)
    };
    canvas(shape).size(size, size)
}

fn icon_thumbs_down(size: f32, color: Color, filled: bool) -> El {
    let pts = vec![
        [17.0, 14.0],
        [17.0, 4.0],
        [7.0, 4.0],
        [4.5, 11.5],
        [6.0, 14.0],
        [11.0, 14.0],
        [10.0, 19.5],
        [12.5, 20.5],
        [17.0, 14.0],
    ];
    let shape = if filled {
        Shape::new().view_box(24.0, 24.0).fill(pts, color).stroke(
            vec![[20.0, 4.0], [20.0, 14.0]],
            2.0,
            color,
        )
    } else {
        Shape::new()
            .view_box(24.0, 24.0)
            .stroke(pts, 1.8, color)
            .stroke(vec![[20.0, 4.0], [20.0, 14.0]], 1.8, color)
    };
    canvas(shape).size(size, size)
}

pub(crate) fn render_art(
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
        s.remote_image_sized(url, size.unwrap_or(360.0), scale)
    };
    render_art_image(img, size, radius, is_liked)
}

pub(crate) fn render_art_image(
    img: Option<rune::Image>,
    size: Option<f32>,
    radius: f32,
    is_liked: bool,
) -> El {
    let mut box_el = el().radius(radius).clip_rounded(radius);
    if let Some(px) = size {
        box_el = box_el.size(px, px);
    } else {
        box_el = box_el.w_full().aspect_ratio(1.0);
    }
    if let Some(loaded) = img {
        box_el.pixels_fit(loaded.pixels(), SurfaceFit::Fill)
    } else if is_liked {
        box_el.fill(pressed_fill()).center().child(icon_heart(
            size.map_or(28.0, |v| (v * 0.42).max(14.0)),
            FG,
            true,
        ))
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
        .on_press(move |s: &mut AppState| s.music_reset_nav(target.clone()))
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
            matches!(s.media.music_nav, MusicNav::Browse { uri: None, .. }),
            MusicNav::Browse {
                uri: None,
                title: None,
            },
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

    let filters = if s.media.music_library_top_filters.is_empty() {
        &s.media.music_library_filters
    } else {
        &s.media.music_library_top_filters
    };
    let shown = filters
        .iter()
        .find(|f| f.id == s.media.music_library_filter)
        .map(|f| f.name.as_str());
    let order = s
        .media
        .music_library_sorts
        .iter()
        .find(|so| so.id == s.media.music_library_sort)
        .map(|so| so.name.as_str());
    let filter_label = match (shown, order) {
        (Some(a), Some(b)) => format!("{a} · {b}"),
        (Some(a), None) => a.to_string(),
        (None, Some(b)) => b.to_string(),
        (None, None) => "All".to_string(),
    };

    let filter_btn = el()
        .key("music-lib-filter")
        .role("button")
        .label("Show and sort your library")
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
            s.media.music_menu = Some(MusicMenu::LibraryShowSort);
        })
        .child(
            text(filter_label)
                .font(sub_font)
                .color(fg_muted())
                .truncate(),
        );

    let plus_btn = el()
        .key("music-lib-create")
        .role("button")
        .label(if svc.has_folders() {
            "Create"
        } else {
            "New playlist"
        })
        .focusable(true)
        .hit()
        .press_on_down()
        .size(28.0, 28.0)
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .border(1.0, glass_rim())
        .hover(hover_fill())
        .center()
        .on_press(|s: &mut AppState| {
            if s.media.music_service.has_folders() {
                s.media.music_menu = Some(MusicMenu::LibraryCreate);
            } else {
                s.music_start_new_playlist(None);
            }
        })
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

    let in_folder = !s.media.music_folder_stack.is_empty();
    let mut lib_rows: Vec<El> = Vec::new();

    if let Some(ref err) = s.media.music_library_error {
        lib_rows.push(
            col()
                .w_full()
                .px(8.0)
                .py(6.0)
                .gap(6.0)
                .child(text(err.clone()).font(sub_font).color(bad()).wrap())
                .child(
                    glass_button("music-lib-retry", "Try again", None)
                        .on_press(|s: &mut AppState| s.load_music_library()),
                ),
        );
    } else if let Some(ref fail) = s.media.music_library_failed {
        lib_rows.push(
            text(fail.clone())
                .font(sub_font)
                .color(bad())
                .px(8.0)
                .py(4.0)
                .wrap(),
        );
    } else if let Some((ref said, _)) = s.media.music_library_said {
        lib_rows.push(
            text(said.clone())
                .font(sub_font)
                .color(good())
                .px(8.0)
                .py(4.0),
        );
    }

    if let Some(top_folder) = s.media.music_folder_stack.last() {
        let folder_title = s
            .media
            .music_folder_ref
            .as_ref()
            .map(|f| f.title.as_str())
            .filter(|t| !t.is_empty())
            .unwrap_or(top_folder.title.as_str())
            .to_string();
        lib_rows.push(
            row()
                .key("music-folder-back")
                .role("button")
                .label(folder_title.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .w_full()
                .min_h(40.0)
                .px(8.0)
                .gap(8.0)
                .radius(ROW_RADIUS)
                .hover(hover_fill())
                .center_y()
                .on_press(|s: &mut AppState| s.music_folder_back())
                .child(icon_chevron_left(16.0, FG))
                .child(text(folder_title).font(row_font).color(FG).truncate()),
        );
    }

    if s.media.music_folder_naming.as_deref() == Some("new") {
        lib_rows.push(folder_name_row(s, "music-folder-new-input"));
    }

    for (i, card) in s.media.music_library.iter().enumerate() {
        let is_folder = card.kind == "folder";
        if s.media.music_folder_naming.as_deref() == Some(card.uri.as_str()) {
            lib_rows.push(folder_name_row(s, &format!("music-folder-rename-{i}")));
            continue;
        }
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
        let card_menu = card.clone();
        let more_key = format!("music-lib-{i}-more");
        let more_key_r = more_key.clone();

        let art_el = if is_folder {
            el().size(36.0, 36.0)
                .radius(RECENT_ART_RADIUS)
                .fill(art_surface_fill())
                .center()
                .child(icon_folder(16.0, fg_muted()))
        } else {
            render_art(s, &card.image, Some(36.0), art_r, is_liked, scale)
        };

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
            .child(art_el)
            .child(text_col);

        if card.pinned {
            item_row = item_row.child(icon_pin(12.0, fg_muted()));
        }

        if svc.has_folders() {
            let cm1 = card_menu.clone();
            let cm2 = card_menu;
            item_row = item_row
                .on_secondary_press(move |s: &mut AppState, _, _| {
                    s.media.music_menu = Some(MusicMenu::LibraryRow {
                        card: cm1.clone(),
                        in_folder,
                        anchor_key: more_key_r.clone(),
                    });
                })
                .child(
                    el().key(more_key.clone())
                        .role("button")
                        .label(format!("More for {}", card.title))
                        .focusable(false)
                        .hit()
                        .press_on_down()
                        .size(24.0, 24.0)
                        .radius(RADIUS_PILL)
                        .hover(hover_fill())
                        .center()
                        .on_press(move |s: &mut AppState| {
                            s.media.music_menu = Some(MusicMenu::LibraryRow {
                                card: cm2.clone(),
                                in_folder,
                                anchor_key: more_key.clone(),
                            });
                        })
                        .child(icon_more_horiz(14.0, fg_muted())),
                );
        }

        if selected {
            item_row = item_row.fill(pressed_fill());
        }
        lib_rows.push(item_row);
    }

    let lib_list = col()
        .key("music-library-scroll")
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .pb(12.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .children(lib_rows);

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

fn folder_name_row(s: &AppState, key: &str) -> El {
    row()
        .w_full()
        .min_h(44.0)
        .px(8.0)
        .py(4.0)
        .gap(10.0)
        .center_y()
        .child(
            el().size(36.0, 36.0)
                .radius(RECENT_ART_RADIUS)
                .fill(art_surface_fill())
                .center()
                .child(icon_folder(16.0, fg_muted())),
        )
        .child(
            el().grow(1.0)
                .h(30.0)
                .px(8.0)
                .radius(RADIUS_SMALL)
                .border(1.0, glass_rim())
                .center_y()
                .child(
                    text_input(&s.media.music_folder_name_edit, scaled_body(s))
                        .key(key)
                        .label("Folder name")
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .on_edit(edit_access(|s: &mut AppState| {
                            &mut s.media.music_folder_name_edit
                        }))
                        .on_submit(|s: &mut AppState, _| s.music_commit_folder_name()),
                ),
        )
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

pub(crate) fn shelf_section(s: &AppState, idx: usize, section: &PlayerSection, scale: f32) -> El {
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
        .map(|(j, card)| shelf_card(s, idx, j, card, scale))
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

pub(crate) fn shelf_grid(
    s: &AppState,
    idx: usize,
    title: &str,
    items: &[PlayerCard],
    more: Option<&PlayerLink>,
    scale: f32,
) -> El {
    let mut container = col().w_full().mb(32.0);
    if !title.is_empty() {
        let mut head = row()
            .w_full()
            .mb(12.0)
            .justify_between()
            .center_y()
            .child(text(title).font(scaled_title(s)).color(FG));
        if let Some(m) = more {
            let uri = m.uri.clone();
            let kind = m.kind.clone();
            let t = title.to_string();
            head = head.child(
                el().key(format!("music-grid-more-{idx}"))
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
                        s.music_open_card(uri.clone(), kind.clone(), t.clone());
                    })
                    .child(text("Show all").font(scaled_small(s)).color(fg_muted())),
            );
        }
        container = container.child(head);
    }
    let cards: Vec<El> = items
        .iter()
        .enumerate()
        .map(|(j, card)| shelf_card(s, idx, j, card, scale))
        .collect();
    container.child(el().w_full().equal_columns(6).gap(24.0).children(cards))
}

fn shelf_card(s: &AppState, idx: usize, j: usize, card: &PlayerCard, scale: f32) -> El {
    let is_artist = card.kind == "artist";
    let is_liked = card.kind == "collection" || card.title == s.media.music_service.liked_name();
    let art_r = if is_artist { RADIUS_PILL } else { RADIUS_CARD };
    let uri = card.uri.clone();
    let kind = card.kind.clone();
    let title = card.title.clone();
    let sub = if !card.subtitle.is_empty() {
        card.subtitle.as_str()
    } else if is_artist {
        "Artist"
    } else {
        ""
    };
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
            is_liked,
            scale,
        )))
        .child(
            text(card.title.clone())
                .font(scaled_body(s))
                .color(FG)
                .truncate(),
        );
    if !sub.is_empty() {
        card_col = card_col.child(text(sub).font(scaled_small(s)).color(fg_muted()).truncate());
    }
    card_col
}

fn main_panel_body(s: &AppState, scale: f32) -> El {
    if s.media.music_room_state == MusicRoomState::Loading
        && s.media.music_home_sections.is_empty()
        && s.media.music_recent.is_empty()
    {
        return wait_view(s);
    }

    match &s.media.music_nav {
        MusicNav::Home => {
            if s.media.music_home_loading
                && s.media.music_home_sections.is_empty()
                && s.media.music_recent.is_empty()
            {
                return wait_view(s);
            }
            let mut body = col().w_full();
            let mut has_content = false;
            if !s.media.music_recent.is_empty() {
                has_content = true;
                body = body.child(recent_grid(s, scale));
            }
            for (idx, section) in s.media.music_home_sections.iter().enumerate() {
                if !section.items.is_empty() {
                    has_content = true;
                    body = body.child(shelf_section(s, idx, section, scale));
                }
            }
            if !has_content {
                body = body.child(
                    text("Nothing on Home yet")
                        .font(scaled_body(s))
                        .color(fg_muted())
                        .py(24.0),
                );
            }
            body
        }
        MusicNav::Songs => songs_page(s, scale),
        MusicNav::Search => {
            if let Some(ref err) = s.media.music_page_error {
                return text(err.clone())
                    .font(scaled_small(s))
                    .color(bad())
                    .py(16.0);
            }
            if let Some(ref data) = s.media.music_page_data {
                search_page(s, data, scale)
            } else if s.media.music_search_edit.text.trim().is_empty() {
                text(format!("Search {}", s.media.music_service.label()))
                    .font(scaled_body(s))
                    .color(fg_muted())
                    .py(24.0)
            } else {
                text("Searching…")
                    .font(scaled_body(s))
                    .color(fg_muted())
                    .py(24.0)
            }
        }
        MusicNav::Explore | MusicNav::Browse { .. } | MusicNav::Radio => {
            if let Some(ref err) = s.media.music_page_error {
                return text(err.clone())
                    .font(scaled_small(s))
                    .color(bad())
                    .py(16.0);
            }
            if let Some(ref data) = s.media.music_page_data {
                browse_page(s, data, scale)
            } else {
                text("Loading…")
                    .font(scaled_body(s))
                    .color(fg_muted())
                    .py(24.0)
            }
        }
        MusicNav::Discography { .. } => {
            if let Some(ref err) = s.media.music_page_error {
                return text(err.clone())
                    .font(scaled_small(s))
                    .color(bad())
                    .py(16.0);
            }
            if let Some(ref data) = s.media.music_page_data {
                discography_page(s, data, scale)
            } else {
                text("Loading…")
                    .font(scaled_body(s))
                    .color(fg_muted())
                    .py(24.0)
            }
        }
        MusicNav::Collection { .. } => {
            if let Some(ref err) = s.media.music_page_error {
                return text(err.clone())
                    .font(scaled_small(s))
                    .color(bad())
                    .py(16.0);
            }
            if let Some(ref data) = s.media.music_page_data {
                list_page(s, data, scale)
            } else {
                text("Loading…")
                    .font(scaled_body(s))
                    .color(fg_muted())
                    .py(24.0)
            }
        }
    }
}

fn section_title(s: &AppState) -> Option<&str> {
    match &s.media.music_nav {
        MusicNav::Home => Some("Home"),
        MusicNav::Songs => None,
        MusicNav::Browse { uri: None, .. } => Some("New"),
        MusicNav::Browse { uri: Some(_), .. } => None,
        MusicNav::Radio => Some("Radio"),
        MusicNav::Explore => Some("Explore"),
        MusicNav::Search => Some("Search"),
        MusicNav::Discography { .. } => None,
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

    el().key(format!("{key_prefix}-vol-slider"))
        .role("slider")
        .label("Music volume")
        .focusable(true)
        .hit()
        .w(width)
        .h(20.0)
        .center_y()
        .on_drag(|s: &mut AppState, d, _| {
            let frac = if d.w > 0.0 {
                (d.x / d.w).clamp(0.0, 1.0) as f64
            } else {
                s.media.music_volume
            };
            s.media.music_volume_dragging = d.phase != Phase::Up;
            if !d.cancelled() {
                s.music_set_volume(frac, None);
            }
        })
        .on_wheel(|s: &mut AppState, w, _| {
            if w.dy.abs() > 0.01 && !w.mods.ctrl {
                s.music_step_volume_pct(if w.dy > 0.0 { 2 } else { -2 });
                return true;
            }
            false
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

fn seek_slider(
    key: &str,
    width: f32,
    pos_ms: Option<f64>,
    dur_ms: Option<i64>,
    enabled: bool,
) -> El {
    let frac = match (pos_ms, dur_ms) {
        (Some(p), Some(d)) if d > 0 => (p as f32 / d as f32).clamp(0.0, 1.0),
        _ => 0.0,
    };
    let fill_w = (width * frac).clamp(0.0, width);
    let mut s_el = el()
        .key(key)
        .role("slider")
        .label("Seek")
        .focusable(enabled)
        .hit()
        .w(width)
        .h(18.0)
        .center_y()
        .child(
            el().w(width)
                .h(4.0)
                .radius(2.0)
                .fill(track_bg_fill())
                .child(el().w(fill_w).h(4.0).radius(2.0).fill(FG)),
        );
    if enabled && let Some(dur) = dur_ms.filter(|&d| d > 0) {
        s_el = s_el.on_drag(move |s: &mut AppState, d, _| {
            let f = if d.w > 0.0 {
                (d.x / d.w).clamp(0.0, 1.0) as f64
            } else {
                0.0
            };
            let target = f * dur as f64;
            match d.phase {
                Phase::Down | Phase::Move => {
                    s.media.music_seek_dragging = true;
                    s.media.music_seek_preview_ms = Some(target);
                }
                Phase::Up => {
                    s.media.music_seek_dragging = false;
                    s.media.music_seek_preview_ms = None;
                    if !d.cancelled() {
                        s.music_seek_ms(target);
                    }
                }
            }
        });
    }
    s_el
}

fn verdict_controls(s: &AppState, prefix: &str) -> El {
    let mut r = row().gap(4.0).center_y();
    if !s.media.music_track_uri.is_empty() {
        let liked = s.media.music_liked;
        let lbl = if liked {
            format!("Remove from {}", s.media.music_service.liked_name())
        } else {
            format!("Save to {}", s.media.music_service.liked_name())
        };
        r = r.child(
            bar_icon_btn(
                &format!("{prefix}-like"),
                &lbl,
                icon_heart(15.0, if liked { FG } else { fg_muted() }, liked),
                28.0,
                liked,
                false,
            )
            .on_press(|s: &mut AppState| s.music_toggle_like()),
        );
        if s.media.music_service.has_dislike() {
            let disliked = s.media.music_disliked;
            r = r.child(
                bar_icon_btn(
                    &format!("{prefix}-dislike"),
                    if disliked {
                        "Remove dislike"
                    } else {
                        "Dislike"
                    },
                    icon_thumbs_down(15.0, if disliked { FG } else { fg_muted() }, disliked),
                    28.0,
                    disliked,
                    false,
                )
                .on_press(|s: &mut AppState| s.music_toggle_dislike()),
            );
        }
    }
    r
}

fn playback_controls(s: &AppState, prefix: &str) -> El {
    let pb = s.media.playback.as_ref().filter(|p| p.has_track());
    let has_track = pb.is_some();
    let playing = pb.is_some_and(|p| p.is_playing());

    let shuffle_btn = bar_icon_btn(
        &format!("{prefix}-shuffle"),
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
        &format!("{prefix}-prev"),
        "Previous",
        icon_skip_back(18.0, FG),
        32.0,
        false,
        !has_track,
    )
    .on_press(|s: &mut AppState| s.music_control("previous"));

    let mut play_btn = el()
        .key(format!("{prefix}-toggle"))
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
        &format!("{prefix}-next"),
        "Next",
        icon_skip_forward(18.0, FG),
        32.0,
        false,
        !has_track,
    )
    .on_press(|s: &mut AppState| s.music_control("next"));

    let repeat = s.media.music_repeat;
    let repeat_on = repeat > 0;
    let repeat_btn = bar_icon_btn(
        &format!("{prefix}-repeat"),
        if repeat == 2 {
            "Repeat this song"
        } else if repeat == 1 {
            "Repeat"
        } else {
            "Repeat off"
        },
        if repeat == 2 {
            icon_repeat_one(16.0, FG)
        } else {
            icon_repeat(16.0, if repeat_on { FG } else { fg_muted() })
        },
        32.0,
        repeat_on,
        !s.media.music_can_repeat && !has_track,
    )
    .on_press(|s: &mut AppState| s.music_cycle_repeat());

    let mut center = row()
        .gap(8.0)
        .center_y()
        .child(shuffle_btn)
        .child(prev_btn)
        .child(play_btn)
        .child(next_btn)
        .child(repeat_btn);

    if let Some(ref dev_id) = s.media.music_elsewhere_device_id {
        let id = dev_id.clone();
        center = center.child(
            glass_button("music-play-here", "Play here", None)
                .key(format!("{prefix}-play-here"))
                .on_press(move |s: &mut AppState| s.music_transfer_device(&id)),
        );
    }
    center
}

fn transport_bar(s: &AppState, scale: f32) -> El {
    let pb = s.media.playback.as_ref().filter(|p| p.has_track());
    let has_track = pb.is_some();
    let title = pb
        .map(|p| p.title.as_str())
        .or(s.media.music_player_title.as_deref())
        .unwrap_or("Nothing playing");
    let artist = pb.map(|p| p.artist.as_str()).unwrap_or("");

    let mut song_text = col().grow(1.0).min_w(0.0).child(
        text(title)
            .font(scaled_body(s))
            .color(if has_track { FG } else { fg_muted() })
            .truncate(),
    );
    if let Some((ref err, _)) = s.media.music_bar_failed {
        song_text = song_text.child(
            text(err.clone())
                .font(scaled_small(s))
                .color(bad())
                .truncate(),
        );
    } else if !artist.is_empty() {
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
        .gap(10.0)
        .center_y()
        .child(render_art_image(
            s.current_playback_art(scale),
            Some(48.0),
            RADIUS_PILL,
            false,
        ))
        .child(song_text)
        .child(verdict_controls(s, "music-bar"));

    let center = playback_controls(s, "music");

    let muted = s.media.music_muted;
    let mute_btn = bar_icon_btn(
        "music-mute",
        if muted { "Unmute music" } else { "Mute music" },
        if muted {
            icon_volume_off(16.0, fg_muted())
        } else {
            icon_volume(16.0, fg_muted())
        },
        28.0,
        false,
        false,
    )
    .on_press(|s: &mut AppState| s.music_toggle_mute());

    let vol_pct = if !s.media.music_volume_known && s.status != crate::state::HostStatus::Standalone
    {
        "—".to_string()
    } else {
        format!(
            "{}%",
            if muted {
                0
            } else {
                (s.media.music_volume * 100.0).round() as i32
            }
        )
    };
    let vol_group = row()
        .gap(4.0)
        .center_y()
        .child(mute_btn)
        .child(volume_slider(
            "music-bar",
            88.0,
            s.media.music_volume,
            muted,
        ))
        .child(
            text(vol_pct)
                .font(scaled_small(s))
                .color(fg_muted())
                .w(34.0),
        );

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
        "Immersive",
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
    .on_press(|s: &mut AppState| {
        s.media.music_immersion = !s.media.music_immersion;
        if s.media.music_immersion && !s.media.music_track_uri.is_empty() {
            let uri = s.media.music_track_uri.clone();
            s.load_music_lyrics(&uri);
        }
    });

    let more_active = matches!(
        s.media.music_panel,
        Some(MusicPanel::Jam | MusicPanel::Friends | MusicPanel::Devices | MusicPanel::Sound)
    );
    let more_btn = bar_icon_btn(
        "music-more",
        "More panels",
        icon_more_horiz(16.0, if more_active { FG } else { fg_muted() }),
        32.0,
        more_active,
        false,
    )
    .on_press(|s: &mut AppState| {
        s.media.music_menu = Some(MusicMenu::MorePanels);
    });

    let mut right = row()
        .grow(1.0)
        .min_w(0.0)
        .justify_end()
        .gap(6.0)
        .center_y()
        .child(vol_group);

    if s.media.music_can_video && s.media.music_service.has_video() {
        let on = s.media.music_video;
        right = right.child(
            bar_icon_btn(
                "music-video-toggle",
                if on {
                    "Play the song"
                } else {
                    "Play the music video"
                },
                icon_video(15.0, if on { FG } else { fg_muted() }),
                32.0,
                on,
                false,
            )
            .on_press(|s: &mut AppState| s.music_toggle_video()),
        );
    }

    right = right
        .child(np_btn)
        .child(queue_btn)
        .child(immerse_btn)
        .child(more_btn);

    let overlay_open = s.capture_menu_open
        || s.palette_open
        || s.key_modal_open
        || s.props_open
        || s.setup.beat.is_some()
        || s.media.music_playlist_sheet.is_some();

    let pill = apply_glass(
        row()
            .key("music-transport-bar")
            .w_full()
            .max_w(1040.0)
            .h(64.0)
            .pl(10.0)
            .pr(14.0)
            .gap(16.0)
            .radius(RADIUS_PILL)
            .center_y()
            .child(left)
            .child(center)
            .child(right),
        ExoGlass::Bar,
        overlay_open,
    );

    row()
        .pin(Some(SIDEBAR_W + 2.0 * GAP), None, Some(GAP), Some(18.0))
        .justify_center()
        .px(14.0)
        .child(pill)
}

fn immersion_view(s: &AppState, scale: f32) -> El {
    let pb = s.media.playback.as_ref().filter(|p| p.has_track());
    let title = s
        .media
        .music_player_title
        .as_deref()
        .or_else(|| pb.map(|p| p.title.as_str()))
        .unwrap_or("Nothing playing");
    let pos_ms = s.media.current_position_ms();
    let dur_ms = pb.and_then(|p| p.duration_ms);
    let can_seek = pb.is_some_and(|p| p.can_seek);
    let has_lyrics = !s.media.music_track_uri.is_empty()
        && (!s.media.music_lyrics_lines.is_empty() || s.media.music_lyrics_loading);

    let close_btn = el()
        .key("music-immersion-close")
        .role("button")
        .label("Leave full window")
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

    let progress = row()
        .w(360.0)
        .gap(10.0)
        .center_y()
        .child(
            text(format_time_clock(pos_ms))
                .font(scaled_small(s))
                .color(fg_muted())
                .w(40.0),
        )
        .child(seek_slider(
            "music-immersion-seek",
            260.0,
            pos_ms,
            dur_ms,
            can_seek,
        ))
        .child(
            text(format_time_clock(dur_ms.map(|d| d as f64)))
                .font(scaled_small(s))
                .color(fg_muted())
                .w(40.0),
        );

    let muted = s.media.music_muted;
    let vol = row()
        .gap(8.0)
        .center_y()
        .child(
            bar_icon_btn(
                "music-immersion-mute",
                if muted { "Unmute music" } else { "Mute music" },
                if muted {
                    icon_volume_off(16.0, fg_muted())
                } else {
                    icon_volume(16.0, fg_muted())
                },
                28.0,
                false,
                false,
            )
            .on_press(|s: &mut AppState| s.music_toggle_mute()),
        )
        .child(volume_slider(
            "music-immersion",
            160.0,
            s.media.music_volume,
            muted,
        ));

    let song_col = col()
        .w(400.0)
        .items_center()
        .gap(18.0)
        .child(render_art_image(
            s.current_playback_art(scale),
            Some(340.0),
            RADIUS_PANEL,
            false,
        ))
        .child(
            col()
                .w_full()
                .items_center()
                .gap(4.0)
                .child(text(title).font(scaled_title(s)).color(FG).truncate())
                .child(song_artists_row(s, pb, true)),
        )
        .child(progress)
        .child(
            row()
                .gap(10.0)
                .center_y()
                .child(verdict_controls(s, "music-immersion"))
                .child(playback_controls(s, "music-immersion")),
        )
        .child(vol);

    let mut stage = row()
        .w_full()
        .h_full()
        .pt(TITLEBAR_HEIGHT + 24.0)
        .pb(32.0)
        .px(64.0)
        .gap(64.0)
        .center()
        .child(song_col);

    if has_lyrics {
        stage = stage.child(
            col()
                .key("music-immersion-lyrics")
                .w(520.0)
                .max_h(760.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(lyrics_view(s, true)),
        );
    }

    el().key("music-immersion-room")
        .w_full()
        .h_full()
        .fill(BG)
        .child(stage)
        .child(close_btn)
}

pub fn view(s: &AppState, scale: f32) -> El {
    if room_state_replaces_chrome(&s.media.music_room_state) {
        let screen = match &s.media.music_room_state {
            MusicRoomState::SignedOut => sign_in_view(s, false),
            MusicRoomState::NotPremium => sign_in_view(s, true),
            MusicRoomState::Error(msg) => error_view(s, msg),
            _ => el(),
        };
        return el()
            .key("music-room")
            .w_full()
            .h_full()
            .pt(TITLEBAR_HEIGHT)
            .fill(BG)
            .child(screen);
    }

    if s.media.music_room_state == MusicRoomState::Loading
        && s.media.music_panel.is_none()
        && !s.media.music_immersion
        && matches!(s.media.music_nav, MusicNav::Home)
        && s.media.music_library.is_empty()
        && s.media.music_home_sections.is_empty()
    {
        return music_wait_room(s);
    }

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

    let mut root = el()
        .key("music-room")
        .w_full()
        .h_full()
        .fill(BG)
        .child(columns)
        .child(transport_bar(s, scale));

    if let Some(ref sheet) = s.media.music_playlist_sheet {
        root = root.child(playlist_sheet_overlay(s, sheet, scale));
    }

    root = root.children(music_menus_overlay(s));
    root
}

pub fn mini_player(s: &AppState, scale: f32, slot: crate::state::MiniBarSlot) -> Option<El> {
    use crate::state::MiniBarSlot;

    if slot == MiniBarSlot::Hidden
        || s.room == Room::Music
        || s.setup.beat.is_some()
        || s.palette_open
        || s.key_modal_open
        || s.downloads.open
        || s.whats_new.mode.is_some()
        || s.whats_new.sheet_open
        || s.props_open
        || s.media.open_capture_id.is_some()
    {
        return None;
    }
    let track = s.media.playback.as_ref().filter(|p| p.has_track())?;
    let playing = track.is_playing();
    let duration_ms = track.duration_ms.unwrap_or(0);
    let pos_ms = s.media.current_position_ms();
    let live_progress = match (pos_ms, track.duration_ms) {
        (Some(pos), Some(dur)) if dur > 0 => (pos as f32 / dur as f32).clamp(0.0, 1.0),
        _ => 0.0,
    };
    let progress = s.chrome.mini_seek_preview.unwrap_or(live_progress);
    let can_prev = track.can_prev;
    let can_next = track.can_next;
    let can_toggle = if playing {
        track.can_pause
    } else {
        track.can_play
    };
    let can_seek = track.can_seek && duration_ms > 0;

    let toggle_key = match slot {
        MiniBarSlot::Compact => "mini-toggle-compact",
        MiniBarSlot::Medium => "mini-toggle-medium",
        _ => "mini-toggle",
    };
    let mut toggle_btn = el()
        .key(toggle_key)
        .role("button")
        .label(if playing { "Pause music" } else { "Play music" })
        .focusable(can_toggle)
        .disabled(!can_toggle)
        .hit()
        .cursor(if can_toggle {
            Cursor::Pointer
        } else {
            Cursor::NotAllowed
        })
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(28.0, 28.0)
        .radius(RADIUS_PILL)
        .center()
        .hover(if can_toggle {
            hover_fill()
        } else {
            TRANSPARENT
        })
        .opacity(if can_toggle { 1.0 } else { 0.4 })
        .hover_motion(SPRING_QUICK, SPRING_QUICK)
        .child(if playing {
            icon_pause(15.0, FG)
        } else {
            icon_play(15.0, FG)
        });
    if can_toggle {
        toggle_btn = toggle_btn.on_press(|s: &mut AppState| s.music_control("toggle"));
    }

    let thumb = if let Some(img) = s.current_playback_art(scale) {
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
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .size(28.0, 28.0)
            .radius(RADIUS_SMALL)
            .center()
            .hover(hover_fill())
            .hover_motion(SPRING_QUICK, SPRING_QUICK)
            .on_press(|s: &mut AppState| {
                s.switch_room(Room::Music);
                s.music_open_panel(MusicPanel::NowPlaying);
            })
            .child(thumb);

        return Some(
            glass_capsule("music-mini-player")
                .px(4.0)
                .gap(4.0)
                .on_hover_change(|s: &mut AppState, inside, _| {
                    if inside {
                        s.warm_music();
                    }
                })
                .child(toggle_btn)
                .child(open_btn),
        );
    }

    let full = slot == MiniBarSlot::Inline;
    let muted = s.media.music_muted || s.media.music_volume <= 0.001;
    let vol_pct = (s.media.music_volume.clamp(0.0, 1.0) * 100.0).round() as i32;
    let vol_icon = if muted {
        icon_volume_off(15.0, fg_muted())
    } else if vol_pct < 50 {
        icon_volume_low(15.0, fg_muted())
    } else {
        icon_volume(15.0, fg_muted())
    };

    let mute_btn = el()
        .key("mini-mute")
        .role("button")
        .label(if muted { "Unmute music" } else { "Mute music" })
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .size(28.0, 28.0)
        .radius(RADIUS_PILL)
        .center()
        .hover(hover_fill())
        .on_press(move |s: &mut AppState| {
            if full {
                s.music_toggle_mute();
            } else {
                s.chrome.mini_volume_open = !s.chrome.mini_volume_open;
            }
        })
        .child(vol_icon);

    let controls = if full {
        let mut prev_btn = el()
            .key("mini-prev")
            .role("button")
            .label("Previous song")
            .focusable(can_prev)
            .disabled(!can_prev)
            .hit()
            .cursor(if can_prev {
                Cursor::Pointer
            } else {
                Cursor::NotAllowed
            })
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .size(28.0, 28.0)
            .radius(RADIUS_PILL)
            .center()
            .hover(if can_prev { hover_fill() } else { TRANSPARENT })
            .opacity(if can_prev { 1.0 } else { 0.4 })
            .motion(SPRING_QUICK)
            .child(icon_skip_back(15.0, FG));
        if can_prev {
            prev_btn = prev_btn.on_press(|s: &mut AppState| s.music_control("previous"));
        }

        let mut next_btn = el()
            .key("mini-next")
            .role("button")
            .label("Next song")
            .focusable(can_next)
            .disabled(!can_next)
            .hit()
            .cursor(if can_next {
                Cursor::Pointer
            } else {
                Cursor::NotAllowed
            })
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .size(28.0, 28.0)
            .radius(RADIUS_PILL)
            .center()
            .hover(if can_next { hover_fill() } else { TRANSPARENT })
            .opacity(if can_next { 1.0 } else { 0.4 })
            .motion(SPRING_QUICK)
            .child(icon_skip_forward(15.0, FG));
        if can_next {
            next_btn = next_btn.on_press(|s: &mut AppState| s.music_control("next"));
        }

        row()
            .gap(2.0)
            .center_y()
            .child(prev_btn)
            .child(toggle_btn)
            .child(next_btn)
    } else {
        row().center_y().child(toggle_btn)
    };

    let lcd_w: f32 = if full { 132.0 } else { 80.0 };
    let mut seek_bar = el()
        .key("mini-seek")
        .role("slider")
        .label("Seek song")
        .w(lcd_w)
        .h(7.0)
        .center_y()
        .child(
            el().w(lcd_w)
                .h(3.0)
                .radius(2.0)
                .fill(track_bg_fill())
                .child(
                    el().w((lcd_w * progress).clamp(0.0, lcd_w))
                        .h(3.0)
                        .radius(2.0)
                        .fill(FG),
                ),
        );
    if can_seek {
        seek_bar = seek_bar
            .hit()
            .cursor(Cursor::Pointer)
            .on_drag(move |s: &mut AppState, d, _| {
                let frac = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
                match d.phase {
                    Phase::Down | Phase::Move => {
                        s.chrome.mini_dragging_seek = true;
                        s.chrome.mini_seek_preview = Some(frac);
                    }
                    Phase::Up => {
                        s.chrome.mini_dragging_seek = false;
                        s.chrome.mini_seek_preview = None;
                        if !d.cancelled() {
                            let target = (duration_ms as f64 * frac as f64).round() as i64;
                            s.seek_music_ms(target);
                        }
                    }
                }
            });
    }

    let track_btn = row()
        .key("mini-open-music")
        .role("button")
        .label(format!("Open Music: {} by {}", track.title, track.artist))
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .h(22.0)
        .w(lcd_w)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_SMALL)
        .hover(hover_fill())
        .on_press(|s: &mut AppState| s.switch_room(Room::Music))
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
        }));

    let lcd = row()
        .h(30.0)
        .gap(8.0)
        .pr(if full { 6.0 } else { 4.0 })
        .center_y()
        .child(
            el().hit()
                .cursor(Cursor::Pointer)
                .on_press(|s: &mut AppState| s.switch_room(Room::Music))
                .child(thumb),
        )
        .child(col().w(lcd_w).gap(1.0).child(track_btn).child(seek_bar));

    let volume = if full {
        let vol_w = 52.0;
        let vol_slider = slider_track(
            "mini-volume-slider",
            "Music volume",
            vol_w,
            if muted {
                0.0
            } else {
                s.media.music_volume as f32
            },
            s.chrome.mini_dragging_vol,
            false,
        )
        .on_drag(move |s: &mut AppState, d, _| {
            let frac = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
            if frac > 0.01 {
                s.chrome.mini_last_audible = frac;
            }
            match d.phase {
                Phase::Down | Phase::Move => {
                    s.chrome.mini_dragging_vol = true;
                    s.music_set_volume(frac as f64, Some(frac <= 0.001));
                }
                Phase::Up => {
                    s.chrome.mini_dragging_vol = false;
                    s.music_set_volume(frac as f64, Some(frac <= 0.001));
                }
            }
        });
        row()
            .gap(4.0)
            .center_y()
            .child(mute_btn)
            .child(vol_slider)
            .child(
                text(format!("{vol_pct}%"))
                    .font(scaled_small(s))
                    .color(fg_muted())
                    .w(26.0),
            )
    } else {
        row().center_y().child(mute_btn)
    };

    Some(
        glass_capsule("music-mini-player")
            .px(if full { 6.0 } else { 5.0 })
            .gap(6.0)
            .on_hover_change(|s: &mut AppState, inside, _| {
                if inside {
                    s.warm_music();
                }
            })
            .child(controls)
            .child(lcd)
            .child(volume),
    )
}

pub fn mini_volume_popover(s: &AppState, blur: bool) -> Vec<El> {
    if !matches!(s.media.music_menu, Some(MusicMenu::MiniVolume)) {
        return Vec::new();
    }
    let muted = s.media.music_muted;
    let pct = format!(
        "{}%",
        if muted {
            0
        } else {
            (s.media.music_volume * 100.0).round() as i32
        }
    );
    let backdrop = el()
        .key("mini-vol-backdrop")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(Color::TRANSPARENT)
        .hit()
        .press_on_down()
        .on_press(|s: &mut AppState| s.media.music_menu = None);

    let panel = glass_panel(
        "mini-vol-popover",
        if blur { GLASS_PANEL_SIGMA } else { 0.0 },
    )
    .anchored_fit("mini-mute", Side::Bottom, Align::End, 8.0)
    .px(12.0)
    .py(8.0)
    .child(
        row()
            .gap(8.0)
            .center_y()
            .child(
                bar_icon_btn(
                    "mini-pop-mute",
                    if muted { "Unmute music" } else { "Mute music" },
                    if muted {
                        icon_volume_off(15.0, fg_muted())
                    } else {
                        icon_volume(15.0, fg_muted())
                    },
                    26.0,
                    false,
                    false,
                )
                .on_press(|s: &mut AppState| s.music_toggle_mute()),
            )
            .child(volume_slider(
                "mini-pop",
                120.0,
                s.media.music_volume,
                muted,
            ))
            .child(text(pct).font(scaled_small(s)).color(fg_muted()).w(34.0)),
    );

    vec![backdrop, panel]
}

pub fn handle_key(s: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    if ev.key == Key::Escape && matches!(s.media.music_menu, Some(MusicMenu::MiniVolume)) {
        s.media.music_menu = None;
        return true;
    }
    if s.room != Room::Music || s.media.music_room_state != MusicRoomState::Running {
        return false;
    }
    if ev.mods.logo {
        return false;
    }
    if ev.key == Key::Escape {
        if s.media.music_menu.is_some() {
            s.media.music_menu = None;
            return true;
        }
        if s.media.music_playlist_sheet.is_some() {
            s.media.music_playlist_sheet = None;
            return true;
        }
        if s.media.music_folder_naming.is_some() {
            s.media.music_folder_naming = None;
            return true;
        }
        if s.media.music_immersion {
            s.media.music_immersion = false;
            return true;
        }
        if s.media.music_panel.is_some() {
            s.media.music_panel = None;
            return true;
        }
        return false;
    }
    if s.media.music_playlist_sheet.is_some()
        || s.media.music_folder_naming.is_some()
        || s.media.music_menu.is_some()
    {
        return false;
    }

    if s.media.music_immersion && !ev.mods.ctrl && !ev.mods.alt && !ev.mods.shift {
        match ev.key {
            Key::Up => {
                s.music_step_volume_pct(5);
                return true;
            }
            Key::Down => {
                s.music_step_volume_pct(-5);
                return true;
            }
            _ => {}
        }
    }

    let ctrl = ev.mods.ctrl || ev.mods.cmd;
    let alt = ev.mods.alt;
    let shift = ev.mods.shift;
    let text_char = match &ev.key {
        Key::Text(t) if t.chars().count() == 1 => t.chars().next(),
        Key::Command(c) => Some(*c),
        _ => None,
    };
    let upper = text_char.map(|c| c.to_ascii_uppercase());

    if !ctrl && !alt && !shift && upper == Some('F') {
        s.media.music_immersion = !s.media.music_immersion;
        if s.media.music_immersion && !s.media.music_track_uri.is_empty() {
            let uri = s.media.music_track_uri.clone();
            s.load_music_lyrics(&uri);
        }
        return true;
    }

    if !ctrl && !alt && !shift && text_char == Some(' ') {
        s.music_control("toggle");
        return true;
    }

    if ctrl && !alt && !shift {
        match ev.key {
            Key::Right => {
                s.music_control("next");
                return true;
            }
            Key::Left => {
                s.music_control("previous");
                return true;
            }
            _ => {}
        }
    }

    match s.media.music_service {
        MusicService::Spotify => {
            if shift && !ctrl && !alt {
                match ev.key {
                    Key::Right => return s.music_seek_by_ms(5000.0),
                    Key::Left => return s.music_seek_by_ms(-5000.0),
                    _ => {}
                }
            }
            if ctrl && !alt && !shift {
                match ev.key {
                    Key::Up => {
                        s.music_step_volume_pct(10);
                        return true;
                    }
                    Key::Down => {
                        s.music_step_volume_pct(-10);
                        return true;
                    }
                    _ => {}
                }
                match upper {
                    Some('S') => {
                        s.music_toggle_shuffle();
                        return true;
                    }
                    Some('R') => {
                        s.music_cycle_repeat();
                        return true;
                    }
                    Some('N') => {
                        s.music_start_new_playlist(None);
                        return true;
                    }
                    Some('L') => {
                        tx.focus("music-search-input", true);
                        return true;
                    }
                    _ => {}
                }
            }
            if ctrl && shift && !alt && upper == Some('N') {
                s.music_start_create_folder();
                return true;
            }
            if alt && shift && !ctrl {
                match upper {
                    Some('B') => {
                        s.music_toggle_like();
                        return true;
                    }
                    Some('H') => {
                        s.music_reset_nav(MusicNav::Home);
                        return true;
                    }
                    Some('S') => {
                        s.music_reset_nav(MusicNav::Songs);
                        return true;
                    }
                    Some('Q') => {
                        s.music_open_panel(MusicPanel::Queue);
                        return true;
                    }
                    Some('J') => {
                        s.music_open_panel(MusicPanel::NowPlaying);
                        return true;
                    }
                    Some('D') => {
                        s.music_open_panel(MusicPanel::Devices);
                        return true;
                    }
                    _ => {}
                }
            }
            if alt && !ctrl && !shift && ev.key == Key::Left {
                s.music_go_back();
                return true;
            }
        }
        MusicService::Apple => {
            if ctrl && !alt && !shift {
                match upper {
                    Some('F') => {
                        tx.focus("music-search-input", true);
                        return true;
                    }
                    Some('L') => {
                        s.music_open_panel(MusicPanel::NowPlaying);
                        return true;
                    }
                    Some('[') => {
                        s.music_go_back();
                        return true;
                    }
                    _ => {}
                }
            }
        }
        MusicService::YouTube => {
            if ctrl && !alt && !shift {
                match upper {
                    Some('F') => {
                        tx.focus("music-search-input", true);
                        return true;
                    }
                    Some('L') => {
                        s.music_open_panel(MusicPanel::NowPlaying);
                        return true;
                    }
                    Some('[') => {
                        s.music_go_back();
                        return true;
                    }
                    _ => {}
                }
            }
            if !ctrl && !alt {
                match text_char {
                    Some('+' | '=') => {
                        s.music_toggle_like();
                        return true;
                    }
                    Some('-' | '_') => {
                        s.music_toggle_dislike();
                        return true;
                    }
                    Some('/') => {
                        tx.focus("music-search-input", true);
                        return true;
                    }
                    _ => {}
                }
                if !shift {
                    match upper {
                        Some('K') => {
                            s.music_control("toggle");
                            return true;
                        }
                        Some('N') => {
                            s.music_control("next");
                            return true;
                        }
                        Some('P') => {
                            s.music_control("previous");
                            return true;
                        }
                        Some('J') => return s.music_seek_by_ms(-10_000.0),
                        Some('L') => return s.music_seek_by_ms(10_000.0),
                        Some('S') => {
                            s.music_toggle_shuffle();
                            return true;
                        }
                        Some('R') => {
                            s.music_cycle_repeat();
                            return true;
                        }
                        _ => {}
                    }
                }
            }
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::shell;
    use rune::{Env, Mods, Runtime};

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
            assert!(budget.mini_player);

            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
            rt.frame(0.0);
            assert!(rt.find("music-mini-player").is_some());
            let blurs = rt
                .instances
                .iter()
                .filter(|i| i.params[1] == rune::render::KIND_BACKDROP_BLUR)
                .count();
            assert!(
                blurs <= exo_kit::EXO_MAX_BACKDROP_BLURS,
                "menu open exceeded EXO_MAX_BACKDROP_BLURS: {blurs}"
            );
        }
    }

    #[test]
    fn music_shortcuts_cover_spotify_youtube_and_immersive() {
        let mut state = AppState::default();
        state.room = Room::Music;
        state.media.music_service = MusicService::Spotify;
        state.media.music_room_state = MusicRoomState::Running;
        state.media.music_can_shuffle = true;
        state.media.music_can_repeat = true;

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.on_key = Some(handle_key);
        rt.frame(0.0);

        rt.key(KeyEvent {
            key: Key::Text("f".into()),
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert!(rt.state.media.music_immersion);

        rt.key(KeyEvent {
            key: Key::Escape,
            mods: Mods::default(),
        });
        rt.frame(2.0 / 60.0);
        assert!(!rt.state.media.music_immersion);

        rt.key(KeyEvent {
            key: Key::Command('s'),
            mods: Mods {
                ctrl: true,
                cmd: true,
                ..Mods::default()
            },
        });
        rt.frame(3.0 / 60.0);
        assert!(rt.state.media.music_shuffle);

        rt.key(KeyEvent {
            key: Key::Text("q".into()),
            mods: Mods {
                alt: true,
                shift: true,
                ..Mods::default()
            },
        });
        rt.frame(4.0 / 60.0);
        assert_eq!(rt.state.media.music_panel, Some(MusicPanel::Queue));

        rt.state.media.music_service = MusicService::YouTube;
        rt.key(KeyEvent {
            key: Key::Text("r".into()),
            mods: Mods::default(),
        });
        rt.frame(5.0 / 60.0);
        assert_eq!(rt.state.media.music_repeat, 1);
    }

    #[test]
    fn signed_out_music_room_shows_signin_and_change_service() {
        let mut state = AppState::default();
        state.room = Room::Music;
        state.media.music_service = MusicService::Apple;
        state.media.music_room_state = MusicRoomState::SignedOut;

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        assert!(rt.find("music-sign-in-btn").is_some());
        assert!(rt.find("music-change-service").is_some());

        let (x, y) = rt.find("music-change-service").expect("change service");
        rt.pointer_down(x, y);
        rt.pointer_up(x, y);
        rt.frame(0.0);
        assert_eq!(rt.state.room, Room::Settings);
        assert_eq!(
            rt.state.pc.settings_section,
            crate::pc_state::SettingsSection::Updates
        );
    }
}
