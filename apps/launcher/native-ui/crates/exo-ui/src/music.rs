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
    BG, ExoGlass, FG, GAP, GLASS_CONTAINER_SPACING, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD,
    RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK, SPRING_SMOOTH, TITLEBAR_HEIGHT,
    TRANSPARENT, apply_glass, bad, fg_faint, fg_muted, glass_button, glass_capsule, glass_rim,
    good, hover_fill, icon_back, icon_chevron_left, icon_compass, icon_folder,
    icon_four_point_sparkle, icon_home, icon_list_music, icon_maximize, icon_more_horiz,
    icon_music, icon_pause, icon_pin, icon_play, icon_plus, icon_radio, icon_search,
    icon_skip_back, icon_skip_forward, icon_video, icon_volume, icon_volume_low, icon_volume_off,
    pressed_fill, slider_track,
};
use exo_rpc::{PlayerCard, PlayerLink, PlayerSection};
use rune::{
    Color, Cursor, El, Key, KeyEvent, Phase, ScrollSpec, SpringPair, SurfaceFit, TextStyle,
    Transition, Tx, col, edit_access, el, oklch, row, text, text_input,
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

fn icon_shuffle(size: f32, color: Color) -> El {
    exo_kit::icon_shuffle(size, color)
}

fn icon_repeat(size: f32, color: Color) -> El {
    exo_kit::icon_repeat(size, color)
}

const ICON_REPEAT_ONE_SVG: rune::Icon = rune::Icon {
    name: "exo-repeat-one-130",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12V9C3 7.34315 4.34315 6 6 6H21M18 3L21 6L18 9M21 12V15C21 16.65685 19.65685 18 18 18H3M6 21L3 18L6 15M12 9.5V14.5M10.7 10.6L12 9.5"/></g>"#,
};

fn icon_repeat_one(size: f32, color: Color) -> El {
    rune::icon(&ICON_REPEAT_ONE_SVG, size).color(color)
}

fn icon_disc(size: f32, color: Color) -> El {
    exo_kit::icon_disc(size, color)
}

fn icon_queue(size: f32, color: Color) -> El {
    exo_kit::icon_queue(size, color)
}

pub(crate) fn icon_chevron_up(size: f32, color: Color) -> El {
    exo_kit::icon_chevron_up(size, color)
}

const ICON_HEART_OUTLINE_SVG: rune::Icon = rune::Icon {
    name: "exo-heart-outline-130",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20.2C12 20.2 3.5 14.9 3.5 8.9C3.5 6.1 5.7 4 8.3 4C10 4 11.4 4.9 12 6.2C12.6 4.9 14 4 15.7 4C18.3 4 20.5 6.1 20.5 8.9C20.5 14.9 12 20.2 12 20.2Z"/></g>"#,
};

pub(crate) fn icon_heart(size: f32, color: Color, filled: bool) -> El {
    if filled {
        exo_kit::icon_heart(size, color)
    } else {
        rune::icon(&ICON_HEART_OUTLINE_SVG, size).color(color)
    }
}

const ICON_THUMBS_DOWN_OUTLINE_SVG: rune::Icon = rune::Icon {
    name: "exo-thumbs-down-outline-130",
    view: 24.0,
    body: r#"<g fill="none" stroke="currentColor" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"><path d="M17 14V4H8.2C7.2 4 6.3 4.6 6 5.6L4.2 11.4C3.8 12.7 4.8 14 6.1 14H11L10.3 18.4C10.1 19.3 10.8 20.2 11.7 20.2C12.3 20.2 12.8 19.9 13.1 19.4L17 14ZM20 4V14"/></g>"#,
};

const ICON_THUMBS_DOWN_SOLID_SVG: rune::Icon = rune::Icon {
    name: "exo-thumbs-down-solid",
    view: 24.0,
    body: r#"<g fill="currentColor" stroke="none"><path d="M17 14V4H8.2C7.2 4 6.3 4.6 6 5.6L4.2 11.4C3.8 12.7 4.8 14 6.1 14H11L10.3 18.4C10.1 19.3 10.8 20.2 11.7 20.2C12.3 20.2 12.8 19.9 13.1 19.4L17 14Z"/><path d="M19.2 4C19.2 3.55 19.55 3.2 20 3.2C20.45 3.2 20.8 3.55 20.8 4V14C20.8 14.45 20.45 14.8 20 14.8C19.55 14.8 19.2 14.45 19.2 14V4Z"/></g>"#,
};

fn icon_thumbs_down(size: f32, color: Color, filled: bool) -> El {
    let glyph = if filled {
        &ICON_THUMBS_DOWN_SOLID_SVG
    } else {
        &ICON_THUMBS_DOWN_OUTLINE_SVG
    };
    rune::icon(glyph, size).color(color)
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
            size.map_or(20.0, |v| (v * 0.36).clamp(14.0, 22.0)),
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
            icon_four_point_sparkle(16.0, FG),
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

        let card_play = card.clone();
        let art_el = if is_folder {
            el().size(36.0, 36.0)
                .radius(RECENT_ART_RADIUS)
                .fill(art_surface_fill())
                .center()
                .child(icon_folder(16.0, fg_muted()))
        } else {
            el().size(36.0, 36.0)
                .radius(art_r)
                .clip_rounded(art_r)
                .child(render_art(
                    s,
                    &card.image,
                    Some(36.0),
                    art_r,
                    is_liked,
                    scale,
                ))
                .child(
                    el().key(format!("music-lib-play-{i}"))
                        .role("button")
                        .label(format!("Play {}", card.title))
                        .focusable(false)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                        .radius(art_r)
                        .fill(Color::BLACK.alpha(0.62))
                        .center()
                        .group_opacity(0.0, 1.0)
                        .on_press(move |s: &mut AppState| s.music_play_card(&card_play))
                        .child(icon_play(13.0, FG)),
                )
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
            .hover_group()
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
            item_row = item_row
                .fill(exo_kit::surface_elevated_hi())
                .border_gradient(1.0, exo_kit::glass_rim_top(), exo_kit::glass_rim_bottom());
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
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .fill(exo_kit::surface_card())
        .border(1.0, exo_kit::card_border())
        .px(8.0)
        .pt(12.0)
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

/// Round play button overlaid on album/playlist art and recent tiles — rendered as a sibling above
/// the card open button so pressing Play always starts playback instead of opening the card.
fn card_play_button(key: String, card: &PlayerCard, size: f32) -> El {
    let play = card.clone();
    el().key(key)
        .role("button")
        .label(format!("Play {}", card.title))
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(size, size)
        .radius(RADIUS_PILL)
        .fill(Color::BLACK.alpha(0.76))
        .hover(Color::BLACK.alpha(0.92))
        .border_gradient(1.0, Color::WHITE.alpha(0.30), Color::WHITE.alpha(0.10))
        .center()
        .group_opacity(0.92, 1.0)
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| s.music_play_card(&play))
        .child(el().pl(1.5).child(icon_play(size * 0.40, FG)))
}

fn recent_grid(s: &AppState, scale: f32) -> El {
    let svc = s.media.music_service;
    let items: Vec<&PlayerCard> = s.media.music_recent.iter().take(8).collect();
    el().w_full()
        .mb(28.0)
        .equal_columns(4)
        .gap(12.0)
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
            el().hover_group()
                .w_full()
                .min_w(0.0)
                .h(56.0)
                .child(
                    row()
                        .key(format!("music-recent-{i}"))
                        .role("button")
                        .label(card.title.clone())
                        .focusable(true)
                        .hit()
                        .cursor(Cursor::Pointer)
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .w_full()
                        .min_w(0.0)
                        .h(56.0)
                        .pl(6.0)
                        .pr(50.0)
                        .py(6.0)
                        .gap(12.0)
                        .radius(RADIUS_CARD)
                        .continuous_corners()
                        .fill(exo_kit::surface_elevated_hi())
                        .border_gradient(1.0, exo_kit::glass_rim_top(), exo_kit::glass_rim_bottom())
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
                        ),
                )
                .child(
                    el().pin(None, Some(12.0), Some(10.0), None)
                        .child(card_play_button(
                            format!("music-recent-play-{i}"),
                            card,
                            32.0,
                        )),
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
                .cursor(Cursor::Pointer)
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

    let cols = if section.items.len() >= 6 { 6 } else { 5 };
    let mut cards: Vec<El> = section
        .items
        .iter()
        .take(cols)
        .enumerate()
        .map(|(j, card)| shelf_card(s, idx, j, card, scale))
        .collect();

    while cards.len() < cols {
        cards.push(el().w_full().min_w(0.0));
    }

    col().w_full().mb(30.0).child(head).child(
        el().w_full()
            .pt(4.0)
            .pb(6.0)
            .equal_columns(cols)
            .gap(20.0)
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
    let uri_meta = card.uri.clone();
    let kind_meta = card.kind.clone();
    let title_meta = card.title.clone();
    let sub = if !card.subtitle.is_empty() {
        card.subtitle.as_str()
    } else if is_artist {
        "Artist"
    } else {
        ""
    };
    let mut meta = col()
        .role("button")
        .label(card.title.clone())
        .focusable(false)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .w_full()
        .min_w(0.0)
        .gap(4.0)
        .on_press(move |s: &mut AppState| {
            s.music_open_card(uri_meta.clone(), kind_meta.clone(), title_meta.clone());
        })
        .child(
            text(card.title.clone())
                .font(scaled_body(s))
                .color(FG)
                .truncate(),
        );
    if !sub.is_empty() {
        meta = meta.child(text(sub).font(scaled_small(s)).color(fg_muted()).truncate());
    }
    col()
        .key(format!("music-card-{idx}-{j}"))
        .hover_group()
        .w_full()
        .min_w(0.0)
        .gap(4.0)
        .radius(RADIUS_CARD)
        .child(
            el().w_full()
                .mb(8.0)
                .child(
                    el().role("button")
                        .label(card.title.clone())
                        .focusable(true)
                        .hit()
                        .cursor(Cursor::Pointer)
                        .press_on_down()
                        .w_full()
                        .on_press(move |s: &mut AppState| {
                            s.music_open_card(uri.clone(), kind.clone(), title.clone());
                        })
                        .child(render_art(s, &card.image, None, art_r, is_liked, scale)),
                )
                .child(
                    el().pin(None, None, Some(10.0), Some(10.0))
                        .child(card_play_button(
                            format!("music-card-play-{idx}-{j}"),
                            card,
                            44.0,
                        )),
                ),
        )
        .child(meta)
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
        MusicNav::Collection { .. } => None,
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
        .h(34.0)
        .px(12.0)
        .gap(8.0)
        .radius(RADIUS_PILL)
        .fill(exo_kit::surface_elevated_hi())
        .border_gradient(1.0, exo_kit::glass_rim_top(), exo_kit::glass_rim_bottom())
        .center_y()
        .child(icon_search(15.0, fg_muted()))
        .child(search_input);

    let mut head = row().w_full().h(34.0).mb(18.0).gap(12.0).center_y();
    if !s.media.music_history.is_empty() {
        head = head.child(
            el().key("music-back-btn")
                .role("button")
                .label("Back")
                .focusable(true)
                .hit()
                .cursor(Cursor::Pointer)
                .press_on_down()
                .size(32.0, 32.0)
                .radius(RADIUS_PILL)
                .fill(exo_kit::surface_elevated_hi())
                .border_gradient(1.0, exo_kit::glass_rim_top(), exo_kit::glass_rim_bottom())
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
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .fill(exo_kit::surface_elevated())
        .border_gradient(1.0, exo_kit::glass_rim_top(), exo_kit::glass_rim_bottom())
        .child(
            col()
                .key("music-main-scroll")
                .w_full()
                .h_full()
                .pt(22.0)
                .px(26.0)
                .pb(128.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .chain_scroll_offset(crate::shell::bar_scroll_watch)
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
        b = b.fill(good().alpha(0.14));
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
    let show_thumb = !muted && pct > 0.001;

    let mut slider = el()
        .key(format!("{key_prefix}-vol-slider"))
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
                .child(
                    el().w(if muted {
                        (width * level.clamp(0.0, 1.0) as f32).clamp(0.0, width)
                    } else {
                        fill_w
                    })
                    .h(3.0)
                    .radius(2.0)
                    .fill(if muted { FG.alpha(0.22) } else { FG }),
                ),
        );
    if show_thumb {
        slider = slider.child(
            el().absolute((fill_w - 5.0).clamp(0.0, width - 10.0), 5.0)
                .size(10.0, 10.0)
                .radius(5.0)
                .fill(FG),
        );
    }
    exo_kit::slider_range(
        slider,
        f64::from(pct),
        0.0,
        1.0,
        0.05,
        true,
        |s: &mut AppState, v| s.music_set_volume(v, None),
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
        s_el = exo_kit::slider_range(
            s_el,
            pos_ms.unwrap_or(0.0).clamp(0.0, dur as f64),
            0.0,
            dur as f64,
            5000.0,
            true,
            |s: &mut AppState, v| s.music_seek_ms(v),
        );
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

    let play_size = if prefix == "music-immersion" {
        44.0
    } else {
        40.0
    };
    let mut play_btn = el()
        .key(format!("{prefix}-toggle"))
        .role("button")
        .label(if playing { "Pause" } else { "Play" })
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(play_size, play_size)
        .radius(RADIUS_PILL)
        .fill(FG)
        .center()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.music_control("toggle"))
        .child(if playing {
            icon_pause(play_size * 0.45, PRIMARY_FG)
        } else {
            icon_play(play_size * 0.45, PRIMARY_FG)
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

    let title_style = TextStyle::new(13.0 * text_scale(s), 500.0).lh(13.0 * text_scale(s) * 1.3);
    let mut song_text = col()
        .grow(1.0)
        .min_w(0.0)
        .child(text(title).font(title_style).color(FG).truncate());
    let _ = has_track;
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
        .gap(12.0)
        .center_y()
        .child(
            el().size(48.0, 48.0)
                .radius(RADIUS_SMALL + 2.0)
                .continuous_corners()
                .clip_rounded(RADIUS_SMALL + 2.0)
                .border(1.0, Color::WHITE.alpha(0.12))
                .child(render_art_image(
                    s.current_playback_art(scale),
                    Some(48.0),
                    RADIUS_SMALL + 2.0,
                    false,
                )),
        )
        .child(song_text)
        .child(verdict_controls(s, "music-bar"));

    let pos_ms = s.media.current_position_ms();
    let dur_ms = pb.and_then(|p| p.duration_ms);
    let can_seek = pb.is_some_and(|p| p.can_seek);
    let time_font = TextStyle::new(11.0 * text_scale(s), 500.0).features("'tnum' 1");
    let bar_progress = row()
        .gap(8.0)
        .center_y()
        .child(
            text(format_time_clock(pos_ms))
                .font(time_font)
                .color(fg_muted())
                .w(34.0),
        )
        .child(seek_slider(
            "music-bar-seek",
            240.0,
            pos_ms,
            dur_ms,
            can_seek,
        ))
        .child(
            text(format_time_clock(dur_ms.map(|d| d as f64)))
                .font(time_font)
                .color(fg_muted())
                .w(34.0),
        );
    let center = col()
        .items_center()
        .gap(3.0)
        .child(playback_controls(s, "music"))
        .child(bar_progress);

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
                good()
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
                good()
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
                good()
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
        s.media.music_lyrics_scrolled_idx = None;
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
        icon_more_horiz(16.0, if more_active { good() } else { fg_muted() }),
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
                icon_video(15.0, if on { good() } else { fg_muted() }),
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

    let pill = apply_glass(
        row()
            .key("music-transport-bar")
            .w_full()
            .max_w(1080.0)
            .h(76.0)
            .pl(14.0)
            .pr(16.0)
            .gap(18.0)
            .radius(exo_kit::RADIUS_SHEET)
            .continuous_corners()
            .center_y()
            .child(left)
            .child(center)
            .child(right),
        ExoGlass::Bar,
    );

    row()
        .pin(Some(SIDEBAR_W + 2.0 * GAP), None, Some(GAP), Some(18.0))
        .justify_center()
        .px(14.0)
        .child(pill)
}

fn immersion_eq_bars(playing: bool, t: f32, reduce_motion: bool) -> El {
    const PHASES: [(f32, f32); 4] = [(4.8, 0.0), (6.3, 1.4), (5.5, 2.7), (7.1, 0.9)];
    row()
        .key("music-immersion-eq")
        .h(16.0)
        .gap(3.0)
        .items_end()
        .children(PHASES.into_iter().enumerate().map(|(i, (freq, phase))| {
            let bar_h = if playing && !reduce_motion {
                let wave = 0.5 + 0.5 * (t * freq + phase).sin();
                let harm = 0.5 + 0.5 * (t * (freq * 1.61) + phase * 1.7).cos();
                4.0 + 12.0 * (0.65 * wave + 0.35 * harm)
            } else if playing {
                match i {
                    0 => 9.0,
                    1 => 14.0,
                    2 => 11.0,
                    _ => 7.0,
                }
            } else {
                3.5
            };
            el().w(3.0)
                .h(bar_h)
                .radius(1.5)
                .fill(if playing { FG.alpha(0.90) } else { fg_faint() })
                .motion(SPRING_QUICK)
        }))
}

fn immersion_view(s: &AppState, scale: f32) -> El {
    let pb = s.media.playback.as_ref().filter(|p| p.has_track());
    let playing = pb.is_some_and(|p| p.is_playing());
    let reduce_motion = s.settings.reduce_motion;
    let t = s.media.music_clock_s as f32;
    let title = s
        .media
        .music_player_title
        .as_deref()
        .or_else(|| pb.map(|p| p.title.as_str()))
        .unwrap_or("Nothing playing");
    let track_key = if !s.media.music_track_uri.is_empty() {
        s.media.music_track_uri.as_str()
    } else {
        title
    };
    let pos_ms = s.media.current_position_ms();
    let dur_ms = pb.and_then(|p| p.duration_ms);
    let can_seek = pb.is_some_and(|p| p.can_seek);
    let has_lyrics = !s.media.music_track_uri.is_empty()
        && (!s.media.music_lyrics_lines.is_empty() || s.media.music_lyrics_loading);

    // Responsive sizing from the live viewport so album art, controls, volume, and lyrics never
    // clip or collide on 720p, scaled 1080p, or ultrawide windows.
    let vp_w = if s.chrome.top_bar_width > 0.0 {
        s.chrome.top_bar_width
    } else {
        1280.0
    };
    let vp_h = if s.chrome.window_height > 0.0 {
        s.chrome.window_height
    } else {
        720.0
    };
    let stacked = has_lyrics && vp_w < 880.0;
    let stage_px = if vp_w < 1100.0 { 28.0 } else { 56.0 };
    let stage_gap = if vp_w < 1100.0 { 32.0 } else { 56.0 };
    let stage_pt = TITLEBAR_HEIGHT + 20.0;
    let stage_pb = 28.0;
    let avail_h = (vp_h - stage_pt - stage_pb).max(360.0);
    // Reserve ~220 pt vertically for title/artist + seek + primary controls + volume pill + gaps.
    let max_art_for_h = (avail_h - 224.0).max(160.0);
    let art_size: f32 = if stacked {
        (avail_h * 0.28).clamp(140.0, 200.0)
    } else if has_lyrics {
        max_art_for_h.min(vp_w * 0.27).clamp(180.0, 360.0)
    } else {
        max_art_for_h.min(vp_w * 0.34).clamp(200.0, 420.0)
    };
    let control_w: f32 = if stacked {
        (vp_w - stage_px * 2.0).clamp(340.0, 460.0)
    } else if has_lyrics {
        ((vp_w - stage_px * 2.0 - stage_gap) * 0.44)
            .clamp(368.0, 460.0)
            .max(art_size)
    } else {
        (vp_w - stage_px * 2.0).min(500.0).max(art_size.max(380.0))
    };

    let title_sz = if avail_h < 580.0 { 22.0 } else { 26.0 } * text_scale(s);
    let title_font = TextStyle::new(title_sz, 700.0)
        .letter_spacing(-0.025)
        .lh(title_sz * 1.2);
    let time_font = TextStyle::new(12.0 * text_scale(s), 500.0).features("'tnum' 1");
    let hue = crate::state::title_hue(title);
    let art_img = s.current_playback_art(scale);

    // True-AMOLED dark atmospheric backdrop tinted softly by the track.
    let backdrop = el()
        .key(format!("music-immerse-bg-{track_key}"))
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::PI * 0.78,
            [
                (0.0, oklch(0.14, 0.065, hue)),
                (0.38, oklch(0.08, 0.035, (hue + 22.0) % 360.0)),
                (0.72, oklch(0.035, 0.015, (hue + 48.0) % 360.0)),
                (1.0, BG),
            ],
        )
        .children(art_img.as_ref().map(|img| {
            let drift = if reduce_motion || !playing { 0.0 } else { t };
            el().key(format!("music-immerse-art-glow-{track_key}"))
                .pin(Some(-80.0), Some(-80.0), Some(-80.0), Some(-80.0))
                .pixels_fit(img.pixels(), SurfaceFit::Fill)
                .scale(1.18 + 0.04 * (drift * 0.09).sin())
                .offset(36.0 * (drift * 0.05).sin(), 24.0 * (drift * 0.04).cos())
                .blur(64.0)
                .opacity(0.42)
        }))
        .child(
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .linear_gradient(
                    std::f32::consts::PI,
                    [
                        (0.0, oklch(0.24, 0.08, hue).alpha(0.14)),
                        (0.45, BG.alpha(0.22)),
                        (1.0, BG.alpha(0.82)),
                    ],
                ),
        )
        .transition(Transition::opacity().springs(SpringPair::new(SPRING_SMOOTH, SPRING_SMOOTH)));

    let leave_btn = bar_icon_btn(
        "music-immersion-close",
        "Leave full window",
        icon_maximize(16.0, FG.alpha(0.86)),
        32.0,
        false,
        false,
    )
    .on_press(|s: &mut AppState| {
        s.media.music_immersion = false;
        s.media.music_lyrics_scrolled_idx = None;
    });

    let progress = row()
        .w(control_w)
        .gap(12.0)
        .center_y()
        .child(
            text(format_time_clock(pos_ms))
                .font(time_font)
                .color(FG.alpha(0.72))
                .w(40.0),
        )
        .child(seek_slider(
            "music-immersion-seek",
            (control_w - 104.0).max(160.0),
            pos_ms,
            dur_ms,
            can_seek,
        ))
        .child(
            text(format_time_clock(dur_ms.map(|d| d as f64)))
                .font(time_font)
                .color(FG.alpha(0.72))
                .w(40.0),
        );

    let muted = s.media.music_muted;
    let vol_pct = format!(
        "{}%",
        if muted {
            0
        } else {
            (s.media.music_volume * 100.0).round() as i32
        }
    );

    // Primary floating Liquid Glass transport pill: verdicts on the left, dead-center playback
    // controls in the middle, and exit on the right — guaranteed breathing room at every width.
    let controls_dock = apply_glass(
        row()
            .key("music-immersion-controls")
            .w(control_w)
            .h(56.0)
            .px(14.0)
            .gap(8.0)
            .radius(RADIUS_PILL)
            .center_y()
            .child(
                row()
                    .grow(1.0)
                    .min_w(0.0)
                    .center_y()
                    .child(verdict_controls(s, "music-immersion")),
            )
            .child(playback_controls(s, "music-immersion"))
            .child(
                row()
                    .grow(1.0)
                    .min_w(0.0)
                    .justify_end()
                    .center_y()
                    .child(leave_btn),
            ),
        ExoGlass::ClearMedia,
    );

    // Dedicated compact Liquid Glass volume capsule centered beneath the transport dock.
    let volume_dock = apply_glass(
        row()
            .key("music-immersion-volume")
            .h(36.0)
            .px(12.0)
            .gap(8.0)
            .radius(RADIUS_PILL)
            .center_y()
            .child(
                bar_icon_btn(
                    "music-immersion-mute",
                    if muted { "Unmute music" } else { "Mute music" },
                    if muted {
                        icon_volume_off(15.0, fg_muted())
                    } else {
                        icon_volume(15.0, FG.alpha(0.88))
                    },
                    26.0,
                    false,
                    false,
                )
                .on_press(|s: &mut AppState| s.music_toggle_mute()),
            )
            .child(volume_slider(
                "music-immersion",
                116.0,
                s.media.music_volume,
                muted,
            ))
            .child(text(vol_pct).font(time_font).color(FG.alpha(0.72)).w(34.0)),
        ExoGlass::ClearMedia,
    );

    // Living album art card: elevated with crisp hairline rim and zero muddy drop shadow.
    let art_radius = exo_kit::RADIUS_SHEET;
    let art_content = if art_img.is_some() {
        render_art_image(art_img, Some(art_size), art_radius, false)
    } else {
        el().size(art_size, art_size)
            .radius(art_radius)
            .continuous_corners()
            .linear_gradient(
                std::f32::consts::PI * 0.85,
                [
                    (0.0, oklch(0.20, 0.08, hue)),
                    (0.65, oklch(0.09, 0.035, (hue + 28.0) % 360.0)),
                    (1.0, BG),
                ],
            )
            .center()
            .child(icon_music(56.0, FG.alpha(0.42)))
    };
    let art_card = el()
        .key(format!("music-immerse-art-{track_key}"))
        .size(art_size, art_size)
        .radius(art_radius)
        .continuous_corners()
        .clip_rounded(art_radius)
        .border_gradient(1.0, Color::WHITE.alpha(0.22), Color::WHITE.alpha(0.06))
        .scale(if playing { 1.0 } else { 0.94 })
        .motion(SPRING_SMOOTH)
        .transition(Transition::scale(0.95).springs(SpringPair::new(SPRING_SMOOTH, SPRING_QUICK)))
        .child(art_content);

    let title_row = row()
        .w(control_w)
        .gap(16.0)
        .justify_between()
        .center_y()
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(4.0)
                .child(text(title).font(title_font).color(FG).truncate())
                .child(song_artists_row(s, pb, false)),
        )
        .child(immersion_eq_bars(playing, t, reduce_motion));

    let song_col = col()
        .w(control_w)
        .items_center()
        .gap(if avail_h < 580.0 { 14.0 } else { 18.0 })
        .glass_container(GLASS_CONTAINER_SPACING)
        .child(art_card)
        .child(col().w_full().gap(10.0).child(title_row).child(progress))
        .child(controls_dock)
        .child(volume_dock);

    let stage = if stacked {
        let mut stacked_stage = col()
            .w_full()
            .h_full()
            .pt(stage_pt)
            .pb(stage_pb)
            .px(stage_px)
            .gap(20.0)
            .items_center()
            .child(song_col);
        if has_lyrics {
            stacked_stage = stacked_stage.child(
                col()
                    .key("music-immersion-lyrics")
                    .w_full()
                    .max_w(640.0)
                    .grow(1.0)
                    .min_h(0.0)
                    .clip_fade(0.0, 36.0, 0.0, 36.0)
                    .scroll(ScrollSpec {
                        stick_bottom: false,
                    })
                    .child(lyrics_view(s, true)),
            );
        }
        stacked_stage
    } else {
        let mut wide_stage = row()
            .w_full()
            .h_full()
            .pt(stage_pt)
            .pb(stage_pb)
            .px(stage_px)
            .gap(stage_gap)
            .justify_center()
            .center_y()
            .child(song_col);
        if has_lyrics {
            wide_stage = wide_stage.child(
                col()
                    .key("music-immersion-lyrics")
                    .grow(1.0)
                    .min_w(0.0)
                    .max_w(640.0)
                    .h_full()
                    .min_h(0.0)
                    .clip_fade(0.0, 56.0, 0.0, 56.0)
                    .scroll(ScrollSpec {
                        stick_bottom: false,
                    })
                    .child(lyrics_view(s, true)),
            );
        }
        wide_stage
    };

    el().key("music-immersion-room")
        .w_full()
        .h_full()
        .fill(BG)
        .transition(Transition::scale(0.97).springs(SpringPair::new(SPRING_SMOOTH, SPRING_QUICK)))
        .child(backdrop)
        .child(stage)
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
        seek_bar = exo_kit::slider_range(
            seek_bar,
            duration_ms as f64 * f64::from(progress.clamp(0.0, 1.0)),
            0.0,
            duration_ms as f64,
            5000.0,
            true,
            |s: &mut AppState, v| s.seek_music_ms(v.round() as i64),
        );
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
        let vol_slider = crate::shell::mini_volume_range(s, vol_slider);
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

pub fn handle_key(s: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
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
            (c0.0 - c1.0).abs() <= 1.0
                && (c1.0 - c2.0).abs() <= 1.0
                && (c0.1 - 492.25).abs() <= 2.0
                && (shelf_01 - 421.45).abs() <= 2.0
                && (shelf_12 - shelf_01).abs() <= 1.0,
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

            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
            rt.frame(0.0);
            assert!(rt.find("music-mini-player").is_some());
            let glass_draws = rt
                .instances
                .iter()
                .filter(|i| i.params[1] == rune::render::KIND_LIQUID_GLASS)
                .count();
            assert!(
                glass_draws <= 10,
                "mini-player with a menu open: {glass_draws} Liquid Glass draws, bound 10"
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
            crate::pc_state::SettingsSection::General
        );
    }

    #[test]
    fn immersion_view_renders_living_eq_karaoke_and_zero_backdrop_blur() {
        use exo_rpc::{LyricsLine, LyricsWord, MusicPlayback};
        use rune::render::KIND_BACKDROP_BLUR;

        let mut state = AppState::default();
        state.room = Room::Music;
        state.media.music_room_state = MusicRoomState::Running;
        state.media.music_immersion = true;
        state.media.music_track_uri = "am:songs:1".into();
        state.media.music_player_title = Some("Starlight".into());
        state.media.music_clock_s = 1.5;
        state.media.music_playback_anchor_s = 0.0;
        state.media.playback = Some(MusicPlayback {
            title: "Starlight".into(),
            artist: "Muse".into(),
            status: "playing".into(),
            position_ms: Some(2500),
            duration_ms: Some(240_000),
            can_seek: true,
            can_play: true,
            can_pause: true,
            ..Default::default()
        });
        state.media.music_lyrics_synced = true;
        state.media.music_lyrics_lines = vec![
            LyricsLine {
                start: 1000.0,
                text: "Far away".into(),
                words: vec![
                    LyricsWord {
                        start: 1000.0,
                        end: 2000.0,
                        text: "Far".into(),
                        bg: false,
                    },
                    LyricsWord {
                        start: 2000.0,
                        end: 3500.0,
                        text: "away".into(),
                        bg: false,
                    },
                ],
                agent: None,
            },
            LyricsLine {
                start: 6000.0,
                text: "This ship is taking me".into(),
                words: vec![],
                agent: None,
            },
        ];

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        assert!(rt.presented("music-immersion-room").is_some());
        assert!(rt.presented("music-immersion-eq").is_some());
        assert!(rt.presented("music-immersion-controls").is_some());
        assert!(rt.presented("music-lyric-0").is_some());
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert_eq!(
            blurs, 0,
            "immersive music player should use zero backdrop blurs"
        );
        let glass_count = rt.liquid_glass_frame().shapes.len();
        assert!(
            (2..=exo_kit::EXO_MAX_BACKDROP_BLURS).contains(&glass_count),
            "expected real Liquid Glass controls in immersive mode, got {glass_count}"
        );
    }

    #[test]
    fn home_card_play_buttons_play_without_opening_card() {
        let mut state = AppState::default();
        state.room = Room::Music;
        state.media.music_room_state = MusicRoomState::Running;
        state.media.music_nav = MusicNav::Home;
        state.media.music_recent = vec![PlayerCard {
            uri: "am:playlists:pl.recent-0".into(),
            kind: "playlist".into(),
            title: "Midnight Focus".into(),
            subtitle: "Playlist".into(),
            image: String::new(),
            ..Default::default()
        }];
        state.media.music_home_sections = vec![PlayerSection {
            title: "Made For You".into(),
            more: None,
            items: vec![
                PlayerCard {
                    uri: "am:albums:1440935467".into(),
                    kind: "album".into(),
                    title: "Currents".into(),
                    subtitle: "Tame Impala".into(),
                    image: String::new(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:stations:ra.12345".into(),
                    kind: "station".into(),
                    title: "Discovery Station".into(),
                    subtitle: "Station".into(),
                    image: String::new(),
                    ..Default::default()
                },
            ],
        }];

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        // Clicking the recent tile's play button must trigger playback and stay on Home.
        let (rx, ry) = rt
            .find("music-recent-play-0")
            .expect("music-recent-play-0 button");
        rt.pointer_move(rx, ry);
        rt.pointer_down(rx, ry);
        rt.pointer_up(rx, ry);
        rt.frame(2.0 / 60.0);
        assert_eq!(rt.state.media.music_nav, MusicNav::Home);
        assert_eq!(
            rt.state.media.music_last_play_request.as_deref(),
            Some("am:playlists:pl.recent-0")
        );

        // Clicking a shelf card's play button must trigger playback and stay on Home.
        let (cx, cy) = rt
            .find("music-card-play-0-0")
            .expect("music-card-play-0-0 button");
        rt.pointer_move(cx, cy);
        rt.pointer_down(cx, cy);
        rt.pointer_up(cx, cy);
        rt.frame(3.0 / 60.0);
        assert_eq!(rt.state.media.music_nav, MusicNav::Home);
        assert_eq!(
            rt.state.media.music_last_play_request.as_deref(),
            Some("am:albums:1440935467")
        );

        // Clicking a station card's play button routes via music_play_context with station URI.
        let (sx, sy) = rt
            .find("music-card-play-0-1")
            .expect("music-card-play-0-1 button");
        rt.pointer_move(sx, sy);
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        rt.frame(4.0 / 60.0);
        assert_eq!(rt.state.media.music_nav, MusicNav::Home);
        assert_eq!(
            rt.state.media.music_last_play_request.as_deref(),
            Some("am:stations:ra.12345")
        );

        // Clicking the left side of the recent tile body opens the collection page.
        let (rect, _) = rt.presented("music-recent-0").expect("music-recent-0 rect");
        let open_x = rect[0] + 24.0;
        let open_y = rect[1] + rect[3] * 0.5;
        rt.pointer_move(open_x, open_y);
        rt.pointer_down(open_x, open_y);
        rt.pointer_up(open_x, open_y);
        rt.frame(5.0 / 60.0);
        assert!(
            matches!(rt.state.media.music_nav, MusicNav::Collection { ref uri, .. } if uri == "am:playlists:pl.recent-0"),
            "clicking recent tile body should open collection, got {:?}",
            rt.state.media.music_nav
        );
    }

    #[test]
    fn immersion_view_never_clips_buttons_or_lyrics_at_720p_and_1080p() {
        use exo_rpc::{LyricsLine, MusicPlayback};

        for (w, h) in [(1280.0f32, 720.0f32), (1920.0f32, 1080.0f32)] {
            let mut state = AppState::default();
            state.room = Room::Music;
            state.media.music_service = MusicService::YouTube;
            state.media.music_room_state = MusicRoomState::Running;
            state.media.music_immersion = true;
            state.media.music_can_shuffle = true;
            state.media.music_can_repeat = true;
            state.media.music_track_uri = "yt:video:abc123".into();
            state.media.music_player_title = Some("A Very Long Immersive Track Title".into());
            state.media.playback = Some(MusicPlayback {
                title: "A Very Long Immersive Track Title".into(),
                artist: "Artist Name".into(),
                status: "playing".into(),
                position_ms: Some(15_000),
                duration_ms: Some(210_000),
                can_seek: true,
                can_play: true,
                can_pause: true,
                can_prev: true,
                can_next: true,
                ..Default::default()
            });
            state.media.music_lyrics_synced = true;
            state.media.music_lyrics_lines = (0..18)
                .map(|i| LyricsLine {
                    start: (i as f64) * 4000.0,
                    text: format!(
                        "Line {i}: This is a longer lyric line that wraps cleanly inside the lyrics column without clipping"
                    ),
                    words: vec![],
                    agent: None,
                })
                .collect();

            let mut rt = Runtime::new(state, shell::view, (w, h), Env::new(true, 1.0));
            rt.frame(0.0);
            rt.frame(1.0 / 60.0);
            rt.settle_all();
            rt.frame(2.0 / 60.0);

            let (controls, _) = rt
                .presented("music-immersion-controls")
                .expect("controls dock");
            let (volume, _) = rt.presented("music-immersion-volume").expect("volume dock");
            let (lyrics, _) = rt
                .presented("music-immersion-lyrics")
                .expect("lyrics viewport");

            // Docks and lyrics viewport must stay cleanly inside the live viewport.
            assert!(
                controls[0] >= 0.0
                    && controls[0] + controls[2] <= w + 0.5
                    && controls[1] + controls[3] <= h + 0.5,
                "{w}x{h}: controls dock clipped outside viewport: {controls:?}"
            );
            assert!(
                volume[0] >= 0.0
                    && volume[0] + volume[2] <= w + 0.5
                    && volume[1] + volume[3] <= h + 0.5,
                "{w}x{h}: volume dock clipped outside viewport: {volume:?}"
            );
            assert!(
                volume[1] >= controls[1] + controls[3] - 0.5,
                "{w}x{h}: volume dock overlaps controls dock: controls={controls:?} volume={volume:?}"
            );
            assert!(
                lyrics[0] >= controls[0] + controls[2] - 0.5
                    && lyrics[0] + lyrics[2] <= w + 0.5
                    && lyrics[1] >= TITLEBAR_HEIGHT
                    && lyrics[1] + lyrics[3] <= h + 0.5
                    && lyrics[3] >= 360.0,
                "{w}x{h}: lyrics viewport mis-sized or clipped: lyrics={lyrics:?} controls={controls:?}"
            );

            // Every control button inside the transport pill must fit strictly inside the pill with zero horizontal overlap.
            let button_keys = [
                "music-immersion-like",
                "music-immersion-dislike",
                "music-immersion-shuffle",
                "music-immersion-prev",
                "music-immersion-toggle",
                "music-immersion-next",
                "music-immersion-repeat",
                "music-immersion-close",
            ];
            let mut rects = Vec::new();
            for key in button_keys {
                let (r, _) = rt
                    .presented(key)
                    .unwrap_or_else(|| panic!("{w}x{h}: missing button {key}"));
                assert!(
                    r[0] >= controls[0] - 0.5
                        && r[0] + r[2] <= controls[0] + controls[2] + 0.5
                        && r[1] >= controls[1] - 0.5
                        && r[1] + r[3] <= controls[1] + controls[3] + 0.5,
                    "{w}x{h}: button {key} clipped outside controls pill: btn={r:?} pill={controls:?}"
                );
                rects.push((key, r));
            }
            for pair in rects.windows(2) {
                let (k0, r0) = pair[0];
                let (k1, r1) = pair[1];
                assert!(
                    r0[0] + r0[2] <= r1[0] + 0.5,
                    "{w}x{h}: buttons overlap: {k0}={r0:?} vs {k1}={r1:?}"
                );
            }

            // Lyric rows must fit inside the lyrics column width (never horizontally clipped).
            let (lyric0, _) = rt.presented("music-lyric-0").expect("music-lyric-0");
            assert!(
                lyric0[0] >= lyrics[0] - 0.5
                    && lyric0[0] + lyric0[2] <= lyrics[0] + lyrics[2] + 0.5,
                "{w}x{h}: lyric row clipped horizontally: row={lyric0:?} viewport={lyrics:?}"
            );
        }
    }

    #[test]
    #[ignore = "writes real GPU proof PNGs to EXO_PROOF_DIR when requested"]
    fn render_visual_proof_screenshots() {
        use exo_rpc::{LyricsLine, LyricsWord, MusicPlayback};
        use rune::render::{Gpu, OFFSCREEN_FORMAT, Offscreen, Renderer};
        use std::path::PathBuf;

        let out_dir = PathBuf::from(
            std::env::var("EXO_PROOF_DIR")
                .unwrap_or_else(|_| std::env::temp_dir().to_string_lossy().into_owned()),
        );
        std::fs::create_dir_all(&out_dir).expect("create EXO_PROOF_DIR");

        let gpu = Gpu::headless();
        let mut renderer = Renderer::new(&gpu.device, OFFSCREEN_FORMAT);

        let mut capture = |name: &str, state: AppState, w: u32, h: u32| {
            let mut rt = Runtime::new(
                state,
                shell::view,
                (w as f32, h as f32),
                Env::new(true, 1.0),
            );
            rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
            rt.set_glass_policy(rune::GlassPolicy::Glass);
            rt.frame(0.0);
            rt.frame(1.0 / 60.0);
            rt.settle_all();
            rt.frame(2.0 / 60.0);

            renderer.set_srgb_blend(rt.srgb_blend());
            renderer.set_fade_stops(rt.fade_stops());
            renderer.set_max_backdrop_blurs(rt.max_backdrop_blurs());
            renderer.set_liquid_glass(rt.liquid_glass_frame());
            renderer.prepare(
                &gpu.device,
                &gpu.queue,
                &mut rt.text.atlas,
                &rt.instances,
                (w, h),
            );
            let target = Offscreen::with_format(&gpu.device, w, h, renderer.format());
            let mut enc = gpu.device.create_command_encoder(&Default::default());
            renderer.draw(&mut enc, &target.view, rt.clear_color());
            target.copy(&mut enc);
            gpu.queue.submit([enc.finish()]);
            let pixels = target.read(&gpu.device);
            image::save_buffer(out_dir.join(name), &pixels, w, h, image::ColorType::Rgba8)
                .expect("save proof png");
        };

        // 1. Library Home: pure #000000 AMOLED + shadow-free upgraded Liquid Glass top pills
        capture(
            "proof-v4-library-home.png",
            AppState::with_demo_games(),
            1280,
            720,
        );

        // 2. Game Page: Optimize (with Graphics Features integrated), zero Frame Cap, zero standalone Graphics Features card
        let mut game_state = AppState::with_demo_games();
        game_state.games[0].last_played_utc = Some("2026-11-25T18:00:00Z".into());
        game_state.open_game_page("demo:cyberpunk");
        capture("proof-v4-game-page.png", game_state, 1280, 900);

        // 3. Music Home: working sibling Play buttons on Recent tiles + Shelf cards
        let mut music_home = AppState::default();
        music_home.room = Room::Music;
        music_home.media.music_room_state = MusicRoomState::Running;
        music_home.media.music_nav = MusicNav::Home;
        music_home.media.playback = Some(MusicPlayback {
            title: "Starlight".into(),
            artist: "Muse".into(),
            status: "playing".into(),
            position_ms: Some(48_000),
            duration_ms: Some(240_000),
            can_seek: true,
            can_play: true,
            can_pause: true,
            can_prev: true,
            can_next: true,
            ..Default::default()
        });
        capture("proof-v4-music-home.png", music_home, 1280, 720);

        // 4 & 5. Music Immersive Mode with synced lyrics at both 720p and 1080p
        let make_immersion = || {
            let mut st = AppState::default();
            st.room = Room::Music;
            st.media.music_service = MusicService::YouTube;
            st.media.music_room_state = MusicRoomState::Running;
            st.media.music_immersion = true;
            st.media.music_can_shuffle = true;
            st.media.music_can_repeat = true;
            st.media.music_track_uri = "yt:video:starlight".into();
            st.media.music_player_title = Some("Starlight".into());
            st.media.music_clock_s = 1.5;
            st.media.music_playback_anchor_s = 0.0;
            st.media.playback = Some(MusicPlayback {
                title: "Starlight".into(),
                artist: "Muse".into(),
                status: "playing".into(),
                position_ms: Some(12_500),
                duration_ms: Some(240_000),
                can_seek: true,
                can_play: true,
                can_pause: true,
                can_prev: true,
                can_next: true,
                ..Default::default()
            });
            st.media.music_lyrics_synced = true;
            st.media.music_lyrics_lines = vec![
                LyricsLine {
                    start: 2000.0,
                    text: "Far away, this ship is taking me far away".into(),
                    words: vec![],
                    agent: None,
                },
                LyricsLine {
                    start: 7000.0,
                    text: "Far away from the memories of the people who care if I live or die"
                        .into(),
                    words: vec![],
                    agent: None,
                },
                LyricsLine {
                    start: 11_000.0,
                    text: "Starlight, I will be chasing a starlight".into(),
                    words: vec![
                        LyricsWord {
                            start: 11_000.0,
                            end: 12_000.0,
                            text: "Starlight,".into(),
                            bg: false,
                        },
                        LyricsWord {
                            start: 12_000.0,
                            end: 12_400.0,
                            text: "I".into(),
                            bg: false,
                        },
                        LyricsWord {
                            start: 12_400.0,
                            end: 12_800.0,
                            text: "will".into(),
                            bg: false,
                        },
                        LyricsWord {
                            start: 12_800.0,
                            end: 13_100.0,
                            text: "be".into(),
                            bg: false,
                        },
                        LyricsWord {
                            start: 13_100.0,
                            end: 13_800.0,
                            text: "chasing".into(),
                            bg: false,
                        },
                        LyricsWord {
                            start: 13_800.0,
                            end: 14_100.0,
                            text: "a".into(),
                            bg: false,
                        },
                        LyricsWord {
                            start: 14_100.0,
                            end: 15_000.0,
                            text: "starlight".into(),
                            bg: false,
                        },
                    ],
                    agent: None,
                },
                LyricsLine {
                    start: 16_000.0,
                    text: "Until the end of my life, I don't know if it's worth it anymore".into(),
                    words: vec![],
                    agent: None,
                },
                LyricsLine {
                    start: 22_000.0,
                    text: "Hold you in my arms, I just wanted to hold you in my arms".into(),
                    words: vec![],
                    agent: None,
                },
                LyricsLine {
                    start: 28_000.0,
                    text: "My life, you electrify my life".into(),
                    words: vec![],
                    agent: None,
                },
            ];
            st
        };
        capture(
            "proof-v4-music-immersion-720p.png",
            make_immersion(),
            1280,
            720,
        );
        capture(
            "proof-v4-music-immersion-1080p.png",
            make_immersion(),
            1920,
            1080,
        );
    }
}
