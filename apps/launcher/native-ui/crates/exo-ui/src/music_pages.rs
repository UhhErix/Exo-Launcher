use crate::media_state::{
    MusicMenu, MusicNav, MusicRoomState, PlaylistSheetState, format_commas, page_for_card,
};
use crate::music::{
    ROW_RADIUS, art_surface_fill, icon_heart, render_art, render_art_image, scaled_body,
    scaled_small, scaled_title, shelf_grid, shelf_section,
};
use crate::state::AppState;
use exo_kit::{
    BG, FG, GAP, GLASS_PANEL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL,
    RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK, TITLEBAR_HEIGHT, TRANSPARENT, bad, fg_faint, fg_muted,
    filter_pill, glass_button, glass_control_fill, glass_panel, glass_rim, glass_rim_bottom,
    glass_rim_top, good, hairline, hover_fill, icon_chevron_down, icon_close, icon_download,
    icon_external, icon_more_horiz, icon_play, icon_search, pressed_fill, primary_button,
    store_logo,
};
use exo_rpc::{
    ArtistAbout, Concert, PlayerCard, PlayerHeader, PlayerLink, PlayerSection, PlayerTrack,
    PlayerViewData, format_duration_ms,
};
use rune::{El, FileDialogOptions, ScrollSpec, col, edit_access, el, row, text, text_input};
use serde_json::json;

pub(crate) fn sign_in_view(s: &AppState, not_premium: bool) -> El {
    let svc = s.media.music_service;
    let upgrade_url = not_premium.then(|| svc.premium_url()).flatten();
    let btn_label: &'static str = match (upgrade_url.is_some(), svc) {
        (true, _) => "Get Spotify Premium",
        (false, crate::media_state::MusicService::Spotify) => "Sign in to Spotify",
        (false, crate::media_state::MusicService::Apple) => "Sign in to Apple Music",
        (false, crate::media_state::MusicService::YouTube) => "Sign in to YouTube Music",
    };
    let busy = s.media.music_sign_in_busy;
    let mut body = col().items_center().gap(20.0);
    if let Some(logo) = store_logo(svc.logo_id(), 56.0) {
        body = body.child(logo);
    }
    if let Some(ref note) = s.media.music_room_message
        && !note.is_empty()
    {
        body = body.child(
            text(note.clone())
                .font(scaled_body(s))
                .color(fg_muted())
                .wrap()
                .max_w(420.0),
        );
    }
    let sign_btn = primary_button(
        if not_premium {
            "music-switch-acct"
        } else {
            "music-sign-in-btn"
        },
        btn_label,
        None,
    )
    .disabled(busy)
    .on_press(move |s: &mut AppState| {
        if let Some(url) = upgrade_url {
            s.music_open_external_url(url);
        } else {
            s.music_sign_in();
        }
    });
    body = body.child(sign_btn);

    if let Some(ref err) = s.media.music_sign_in_message {
        body = body.child(
            text(err.clone())
                .font(scaled_small(s))
                .color(bad())
                .wrap()
                .max_w(420.0),
        );
    } else if s.media.music_room_message.is_none()
        && let Some(ref status) = s.media.music_sign_in_status
    {
        body = body.child(
            text(status.clone())
                .font(scaled_body(s))
                .color(fg_muted())
                .wrap()
                .max_w(420.0),
        );
    }

    body = body.child(change_service_button(s));

    el().key("music-signin-screen")
        .w_full()
        .h_full()
        .fill(BG)
        .center()
        .child(body)
}

pub(crate) fn error_view(s: &AppState, msg: &str) -> El {
    let svc = s.media.music_service;
    let detail = if msg.is_empty() {
        format!("Exo could not open {}.", svc.label())
    } else {
        msg.to_string()
    };
    el().key("music-error-screen")
        .w_full()
        .h_full()
        .fill(BG)
        .center()
        .child(
            col()
                .items_center()
                .gap(12.0)
                .child(
                    text(format!("{} unavailable", svc.label()))
                        .font(scaled_title(s))
                        .color(FG),
                )
                .child(
                    text(detail)
                        .font(scaled_body(s))
                        .color(fg_muted())
                        .wrap()
                        .max_w(420.0),
                )
                .child(
                    row()
                        .gap(10.0)
                        .mt(8.0)
                        .child(
                            primary_button("music-retry-btn", "Try again", None)
                                .on_press(|s: &mut AppState| s.load_music_room()),
                        )
                        .child(change_service_button(s)),
                ),
        )
}

pub(crate) fn wait_view(s: &AppState) -> El {
    let bone = || el().fill(art_surface_fill()).radius(RADIUS_SMALL);
    let recent_bones = el()
        .w_full()
        .mb(32.0)
        .equal_columns(4)
        .gap(10.0)
        .children((0..8).map(|i| {
            row()
                .key(format!("music-wait-recent-{i}"))
                .w_full()
                .h(52.0)
                .px(4.0)
                .py(4.0)
                .gap(12.0)
                .radius(RADIUS_CARD)
                .border(1.0, glass_rim())
                .center_y()
                .child(el().size(44.0, 44.0).radius(8.0).fill(art_surface_fill()))
                .child(bone().h(12.0).grow(1.0))
        }));

    let shelf_bones = |idx: usize| {
        col()
            .w_full()
            .mb(28.0)
            .gap(12.0)
            .child(bone().w(160.0).h(18.0))
            .child(
                el().w_full()
                    .equal_columns(8)
                    .gap(24.0)
                    .children((0..8).map(move |j| {
                        col()
                            .key(format!("music-wait-card-{idx}-{j}"))
                            .w_full()
                            .gap(8.0)
                            .child(
                                el().w_full()
                                    .aspect_ratio(1.0)
                                    .radius(RADIUS_CARD)
                                    .fill(art_surface_fill()),
                            )
                            .child(bone().w(92.0).h(12.0))
                            .child(bone().w(64.0).h(10.0))
                    })),
            )
    };

    col()
        .key("music-wait-bones")
        .w_full()
        .child(
            row()
                .w_full()
                .mb(16.0)
                .justify_between()
                .center_y()
                .child(
                    text(format!("Opening {}…", s.media.music_service.label()))
                        .font(scaled_body(s))
                        .color(fg_muted()),
                )
                .child(change_service_button(s)),
        )
        .child(recent_bones)
        .child(shelf_bones(0))
        .child(shelf_bones(1))
}

pub(crate) fn music_wait_room(s: &AppState) -> El {
    let bone = || el().fill(art_surface_fill()).radius(RADIUS_SMALL);
    let side_nav = col().w_full().gap(6.0).children((0..2).map(|i| {
        row()
            .key(format!("music-wait-nav-{i}"))
            .w_full()
            .h(36.0)
            .px(10.0)
            .gap(10.0)
            .center_y()
            .child(bone().size(16.0, 16.0))
            .child(bone().w(if i == 0 { 52.0 } else { 78.0 }).h(11.0))
    }));
    let side_rows = col().w_full().gap(6.0).children((0..12).map(|i| {
        let w1 = 76.0 + ((i * 23) % 72) as f32;
        let w2 = 44.0 + ((i * 19) % 52) as f32;
        row()
            .key(format!("music-wait-lib-{i}"))
            .w_full()
            .h(46.0)
            .px(6.0)
            .gap(10.0)
            .center_y()
            .child(el().size(36.0, 36.0).radius(8.0).fill(art_surface_fill()))
            .child(
                col()
                    .grow(1.0)
                    .min_w(0.0)
                    .gap(6.0)
                    .child(bone().w(w1).h(10.0))
                    .child(bone().w(w2).h(8.0)),
            )
    }));
    let sidebar = col()
        .key("music-wait-sidebar")
        .w(272.0)
        .h_full()
        .px(10.0)
        .py(12.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .border(1.0, hairline())
        .clip_rounded(RADIUS_PANEL)
        .child(side_nav)
        .child(side_rows);

    let recent_bones = el()
        .w_full()
        .mb(32.0)
        .equal_columns(4)
        .gap(10.0)
        .children((0..8).map(|i| {
            let tw = 70.0 + ((i * 37) % 130) as f32;
            row()
                .key(format!("music-wait-room-recent-{i}"))
                .w_full()
                .h(52.0)
                .px(4.0)
                .py(4.0)
                .gap(12.0)
                .radius(RADIUS_CARD)
                .border(1.0, glass_rim())
                .center_y()
                .child(el().size(44.0, 44.0).radius(8.0).fill(art_surface_fill()))
                .child(bone().w(tw).h(11.0))
        }));

    let shelf_bones = |idx: usize| {
        col()
            .w_full()
            .mb(28.0)
            .gap(12.0)
            .child(bone().w(if idx == 0 { 130.0 } else { 220.0 }).h(16.0))
            .child(
                el().w_full()
                    .equal_columns(8)
                    .gap(20.0)
                    .children((0..8).map(move |j| {
                        let tw = 64.0 + (((idx * 7 + j) * 29) % 68) as f32;
                        let sw = 40.0 + (((idx * 5 + j) * 23) % 48) as f32;
                        col()
                            .key(format!("music-wait-room-card-{idx}-{j}"))
                            .w_full()
                            .gap(8.0)
                            .child(
                                el().w_full()
                                    .aspect_ratio(1.0)
                                    .radius(RADIUS_CARD)
                                    .fill(art_surface_fill()),
                            )
                            .child(bone().w(tw).h(11.0))
                            .child(bone().w(sw).h(9.0))
                    })),
            )
    };

    let main_pane = col()
        .key("music-wait-main")
        .grow(1.0)
        .min_w(0.0)
        .h_full()
        .px(24.0)
        .py(18.0)
        .radius(RADIUS_PANEL)
        .border(1.0, hairline())
        .clip_rounded(RADIUS_PANEL)
        .child(
            row().w_full().mb(22.0).justify_end().center_y().child(
                el().w(260.0)
                    .h(34.0)
                    .radius(RADIUS_PILL)
                    .fill(art_surface_fill()),
            ),
        )
        .child(recent_bones)
        .child(shelf_bones(0))
        .child(shelf_bones(1));

    let bar = row()
        .key("music-wait-bar")
        .pin(Some(288.0), None, Some(16.0), Some(18.0))
        .h(64.0)
        .justify_center()
        .center_y()
        .child(
            row()
                .w(1040.0)
                .max_w(1040.0)
                .h(64.0)
                .px(12.0)
                .radius(32.0)
                .fill(glass_control_fill())
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .center_y()
                .child(
                    row()
                        .w(340.0)
                        .gap(12.0)
                        .center_y()
                        .child(el().size(44.0, 44.0).radius(22.0).fill(art_surface_fill()))
                        .child(
                            col()
                                .gap(6.0)
                                .child(bone().w(84.0).h(11.0))
                                .child(bone().w(62.0).h(9.0)),
                        ),
                )
                .child(
                    row()
                        .grow(1.0)
                        .justify_center()
                        .center_y()
                        .child(el().size(40.0, 40.0).radius(20.0).fill(art_surface_fill())),
                )
                .child(el().w(340.0)),
        );

    let _ = s;
    el().key("music-room")
        .w_full()
        .h_full()
        .fill(BG)
        .child(
            row()
                .w_full()
                .h_full()
                .pt(TITLEBAR_HEIGHT)
                .px(GAP)
                .pb(GAP)
                .gap(GAP)
                .child(sidebar)
                .child(main_pane),
        )
        .child(bar)
}

pub(crate) fn change_service_button(s: &AppState) -> El {
    el().key("music-change-service")
        .role("button")
        .label("Change music service")
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(14.0)
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .hover(hover_fill())
        .center()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.open_music_settings())
        .child(
            text("Change music service")
                .font(scaled_small(s))
                .color(fg_muted()),
        )
}

pub(crate) fn songs_page(s: &AppState, scale: f32) -> El {
    let mut tracks: Vec<&PlayerTrack> = s
        .media
        .music_songs_tracks
        .iter()
        .filter(|t| {
            let k = if t.uid.is_empty() { &t.uri } else { &t.uid };
            !s.media.music_songs_removed.contains(k)
        })
        .collect();
    let total = (s.media.music_songs_total as usize)
        .saturating_sub(s.media.music_songs_removed.len())
        .max(tracks.len());
    let first_playable = tracks.first().copied().filter(|t| t.playable);
    let sort_label = s
        .media
        .music_songs_sorts
        .iter()
        .find(|o| o.id == s.media.music_songs_sort)
        .map(|o| o.name.as_str())
        .unwrap_or("Sort");

    let play_btn = primary_button(
        "music-songs-play",
        "Play",
        Some(icon_play(14.0, PRIMARY_FG)),
    )
    .disabled(first_playable.is_none())
    .on_press(|s: &mut AppState| {
        if let Some(first) = s.media.music_songs_tracks.first().cloned() {
            let ctx = s.media.music_songs_context_uri.clone();
            let q = format!(
                "{}|{}",
                s.media.music_songs_sort, s.media.music_songs_applied_filter
            );
            s.music_play_uri_ordered(&first.uri, Some(&ctx), Some(0), Some(&q));
        }
    });

    let filter_input = text_input(&s.media.music_songs_filter, scaled_body(s))
        .key("music-songs-filter-input")
        .label("Filter your songs")
        .grow(1.0)
        .single_line()
        .color(FG)
        .placeholder("Filter", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| {
            &mut s.media.music_songs_filter
        }))
        .on_submit(|s: &mut AppState, _| s.load_music_songs(false));

    let filter_box = row()
        .key("music-songs-filter")
        .w(220.0)
        .h(32.0)
        .px(12.0)
        .gap(8.0)
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .border(1.0, glass_rim())
        .center_y()
        .child(icon_search(15.0, fg_muted()))
        .child(filter_input);

    let mut head = row()
        .w_full()
        .pb(12.0)
        .gap(12.0)
        .center_y()
        .child(text("Songs").font(scaled_title(s)).color(FG))
        .child(el().grow(1.0))
        .child(play_btn)
        .child(filter_box);

    if !s.media.music_songs_sorts.is_empty() {
        head = head.child(
            row()
                .key("music-songs-sort-btn")
                .role("button")
                .label("Sort")
                .focusable(true)
                .hit()
                .press_on_down()
                .h(32.0)
                .px(12.0)
                .gap(6.0)
                .radius(RADIUS_PILL)
                .border(1.0, glass_rim())
                .hover(hover_fill())
                .center_y()
                .on_press(|s: &mut AppState| {
                    s.media.music_menu = Some(MusicMenu::SongsSort);
                })
                .child(text(sort_label).font(scaled_small(s)).color(FG))
                .child(icon_chevron_down(14.0, fg_muted())),
        );
    }

    let mut body = col()
        .w_full()
        .child(head)
        .child(el().w_full().h(1.0).mb(14.0).fill(hairline()));
    if let Some(ref err) = s.media.music_songs_error {
        body = body.child(
            text(err.clone())
                .font(scaled_small(s))
                .color(bad())
                .mb(12.0),
        );
    } else if !s.media.music_songs_loaded {
        let bone = || el().fill(art_surface_fill()).radius(RADIUS_SMALL);
        body = body.child(col().w_full().gap(6.0).children((0..5).map(|i| {
            let w1 = 160.0 + ((i * 37) % 120) as f32;
            let w2 = 96.0 + ((i * 29) % 80) as f32;
            row()
                .key(format!("music-songs-wait-{i}"))
                .w_full()
                .h(52.0)
                .px(8.0)
                .gap(12.0)
                .center_y()
                .child(el().size(40.0, 40.0).radius(8.0).fill(art_surface_fill()))
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(6.0)
                        .child(bone().w(w1).h(12.0))
                        .child(bone().w(w2).h(10.0)),
                )
        })));
    } else if total == 0 {
        let msg = if !s.media.music_songs_applied_filter.is_empty() {
            format!("No songs match “{}”", s.media.music_songs_applied_filter)
        } else {
            "No songs in your library yet".into()
        };
        body = body.child(text(msg).font(scaled_body(s)).color(fg_muted()).py(24.0));
    }

    if !tracks.is_empty() {
        let ctx = s.media.music_songs_context_uri.clone();
        let query_order = format!(
            "{}|{}",
            s.media.music_songs_sort, s.media.music_songs_applied_filter
        );
        let liked_page = s.media.music_songs_liked;
        let shown_len = tracks.len();
        let list = col()
            .w_full()
            .gap(2.0)
            .children(tracks.drain(..).enumerate().map(|(i, t)| {
                track_row_full(
                    s,
                    TrackRowArgs {
                        idx: i,
                        total: shown_len,
                        track: t,
                        context: Some(ctx.as_str()),
                        query_order: Some(query_order.as_str()),
                        playlist: None,
                        show_album: true,
                        numbered: false,
                        liked_page,
                        can_move: false,
                        whole: shown_len >= total,
                        scale,
                        key_prefix: "music-song-row",
                    },
                )
            }));
        body = body.child(list);

        if s.media.music_songs_tracks.len() < total {
            body = body.child(
                row().w_full().mt(16.0).child(
                    glass_button(
                        "music-songs-more",
                        if s.media.music_songs_loading_more {
                            "Loading…"
                        } else {
                            "Show more"
                        },
                        None,
                    )
                    .disabled(s.media.music_songs_loading_more)
                    .on_press(|s: &mut AppState| s.load_music_songs(true)),
                ),
            );
        }
    }

    body
}

const SEARCH_GROUPS: [(&str, &str); 7] = [
    ("artists", "Artists"),
    ("albums", "Albums"),
    ("playlists", "Playlists"),
    ("stations", "Stations"),
    ("videos", "Music videos"),
    ("shows", "Podcasts"),
    ("audiobooks", "Audiobooks"),
];

fn search_group_cards<'a>(data: &'a PlayerViewData, key: &str) -> &'a [PlayerCard] {
    match key {
        "artists" => &data.artists,
        "albums" => &data.albums,
        "playlists" => &data.playlists,
        "stations" => &data.stations,
        "videos" => &data.videos,
        "shows" => &data.shows,
        "audiobooks" => &data.audiobooks,
        _ => &[],
    }
}

fn kind_display(kind: &str) -> &'static str {
    match kind {
        "artist" => "Artist",
        "album" => "Album",
        "playlist" => "Playlist",
        "show" => "Podcast",
        "episode" => "Episode",
        "track" => "Song",
        "station" => "Station",
        _ => "",
    }
}

pub(crate) fn search_page(s: &AppState, data: &PlayerViewData, scale: f32) -> El {
    let query = s.media.music_search_edit.text.trim().to_string();
    let only = s.media.music_search_only.as_str();
    let filter = data.filter.as_deref().unwrap_or("");
    let groups: Vec<(&str, &str)> = SEARCH_GROUPS
        .iter()
        .copied()
        .filter(|(k, _)| !search_group_cards(data, k).is_empty())
        .collect();
    let has_songs = !data.tracks.is_empty() || !data.episodes.is_empty();
    let top = data.top.first();

    let mut body = col().w_full();

    if !data.filters.is_empty() {
        let mut chips = row().w_full().mb(18.0).gap(8.0).wrap();
        chips = chips.child(
            filter_pill("music-search-f-all", "All", filter.is_empty())
                .on_press(|s: &mut AppState| s.music_set_search_filter(String::new())),
        );
        for opt in &data.filters {
            let oid = opt.id.clone();
            chips = chips.child(
                filter_pill(
                    format!("music-search-f-{}", opt.id),
                    &opt.name,
                    filter == opt.id,
                )
                .on_press(move |s: &mut AppState| s.music_set_search_filter(oid.clone())),
            );
        }
        body = body.child(chips);
    } else {
        let mut kinds: Vec<(&str, &str)> = Vec::new();
        if !data.tracks.is_empty() {
            kinds.push(("tracks", "Songs"));
        }
        kinds.extend(groups.iter().copied());
        if !data.episodes.is_empty() {
            kinds.push(("episodes", "Episodes"));
        }
        if kinds.len() > 1 {
            let mut chips = row().w_full().mb(18.0).gap(8.0).wrap();
            chips = chips.child(
                filter_pill("music-search-k-all", "All", only.is_empty())
                    .on_press(|s: &mut AppState| s.media.music_search_only.clear()),
            );
            for (kid, kname) in kinds {
                let kid_owned = kid.to_string();
                chips = chips.child(
                    filter_pill(format!("music-search-k-{kid}"), kname, only == kid).on_press(
                        move |s: &mut AppState| s.media.music_search_only = kid_owned.clone(),
                    ),
                );
            }
            body = body.child(chips);
        }
    }

    if top.is_none() && groups.is_empty() && !has_songs {
        return body.child(
            text(format!("No results for “{query}”"))
                .font(scaled_body(s))
                .color(fg_muted())
                .py(24.0),
        );
    }

    let whole = !filter.is_empty() || !only.is_empty();
    let songs_title = data
        .filters
        .iter()
        .find(|o| o.id == filter)
        .map(|o| o.name.as_str())
        .unwrap_or("Songs");

    if only.is_empty()
        && let Some(top_card) = top
        && !data.tracks.is_empty()
        && !whole
    {
        let top_el = top_result_card(s, top_card, scale);
        let songs_el = search_songs_section(s, "Songs", &data.tracks, false, false, 4, scale);
        body = body.child(
            row()
                .w_full()
                .mb(28.0)
                .gap(24.0)
                .child(el().w(380.0).child(top_el))
                .child(el().grow(1.0).min_w(0.0).child(songs_el)),
        );
    } else {
        if only.is_empty()
            && let Some(top_card) = top
        {
            body = body.child(
                el().w(420.0)
                    .mb(24.0)
                    .child(top_result_card(s, top_card, scale)),
            );
        }
        if (only.is_empty() || only == "tracks") && !data.tracks.is_empty() {
            body = body.child(search_songs_section(
                s,
                songs_title,
                &data.tracks,
                whole,
                true,
                6,
                scale,
            ));
        }
    }

    for (g_idx, (key, name)) in groups.iter().enumerate() {
        if !only.is_empty() && only != *key {
            continue;
        }
        let cards = search_group_cards(data, key);
        if cards.is_empty() {
            continue;
        }
        if !only.is_empty() {
            body = body.child(shelf_grid(s, 100 + g_idx, name, cards, None, scale));
        } else {
            let sec = PlayerSection {
                title: (*name).into(),
                more: None,
                items: cards.to_vec(),
            };
            body = body.child(shelf_section(s, 100 + g_idx, &sec, scale));
        }
    }

    if (only.is_empty() || only == "episodes") && !data.episodes.is_empty() {
        body = body.child(search_songs_section(
            s,
            "Episodes",
            &data.episodes,
            whole,
            false,
            6,
            scale,
        ));
    }

    body
}

fn top_result_card(s: &AppState, card: &PlayerCard, scale: f32) -> El {
    let is_artist = card.kind == "artist";
    let art_r = if is_artist { RADIUS_PILL } else { RADIUS_CARD };
    let kind = kind_display(&card.kind);
    let sub = [kind, card.subtitle.as_str()]
        .into_iter()
        .filter(|x| !x.is_empty())
        .collect::<Vec<_>>()
        .join(" · ");
    let card_open = card.clone();
    let card_play = card.clone();

    col()
        .w_full()
        .gap(12.0)
        .child(text("Top result").font(scaled_title(s)).color(FG))
        .child(
            row()
                .key("music-search-top")
                .role("button")
                .label(card.title.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .w_full()
                .px(16.0)
                .py(16.0)
                .gap(16.0)
                .radius(RADIUS_PANEL)
                .fill(TRANSPARENT)
                .border(1.0, glass_rim())
                .hover(hover_fill())
                .center_y()
                .on_press(move |s: &mut AppState| {
                    if let Some(nav) = page_for_card(&card_open) {
                        s.music_open_nav(nav);
                    } else {
                        s.music_play_card(&card_open);
                    }
                })
                .child(render_art(s, &card.image, Some(88.0), art_r, false, scale))
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(4.0)
                        .child(
                            text(card.title.clone())
                                .font(scaled_title(s))
                                .color(FG)
                                .truncate(),
                        )
                        .child(text(sub).font(scaled_small(s)).color(fg_muted()).truncate()),
                )
                .child(
                    el().key("music-search-top-play")
                        .role("button")
                        .label(format!("Play {}", card.title))
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .size(40.0, 40.0)
                        .radius(RADIUS_PILL)
                        .fill(FG)
                        .center()
                        .on_press(move |s: &mut AppState| s.music_play_card(&card_play))
                        .child(icon_play(16.0, PRIMARY_FG)),
                ),
        )
}

fn search_songs_section(
    s: &AppState,
    title: &str,
    tracks: &[PlayerTrack],
    whole: bool,
    show_album: bool,
    limit: usize,
    scale: f32,
) -> El {
    let slice = if whole {
        tracks
    } else {
        &tracks[..tracks.len().min(limit)]
    };
    col()
        .w_full()
        .mb(24.0)
        .gap(10.0)
        .child(text(title).font(scaled_title(s)).color(FG))
        .child(
            col()
                .w_full()
                .gap(2.0)
                .children(slice.iter().enumerate().map(|(i, t)| {
                    track_row_full(
                        s,
                        TrackRowArgs {
                            idx: i,
                            total: slice.len(),
                            track: t,
                            context: None,
                            query_order: None,
                            playlist: None,
                            show_album,
                            numbered: false,
                            liked_page: false,
                            can_move: false,
                            whole: true,
                            scale,
                            key_prefix: "music-search-track",
                        },
                    )
                })),
        )
}

pub(crate) fn browse_page(s: &AppState, data: &PlayerViewData, scale: f32) -> El {
    let title = match &s.media.music_nav {
        MusicNav::Explore => Some("Explore".to_string()),
        MusicNav::Browse {
            uri: Some(_),
            title,
        } => data
            .header
            .as_ref()
            .map(|h| h.title.clone())
            .filter(|t| !t.is_empty())
            .or_else(|| title.clone()),
        _ => None,
    };
    let empty = match &s.media.music_nav {
        MusicNav::Radio => "No stations yet",
        MusicNav::Browse { uri: None, .. } => "Nothing new yet",
        _ => "Nothing here yet",
    };

    let mut body = col().w_full();
    if let Some(t) = title {
        body = body.child(text(t).font(scaled_title(s)).color(FG).mb(16.0));
    }
    if !data.tracks.is_empty() {
        body = body.child(col().w_full().mb(24.0).gap(2.0).children(
            data.tracks.iter().enumerate().map(|(i, t)| {
                track_row_full(
                    s,
                    TrackRowArgs {
                        idx: i,
                        total: data.tracks.len(),
                        track: t,
                        context: None,
                        query_order: None,
                        playlist: None,
                        show_album: true,
                        numbered: false,
                        liked_page: false,
                        can_move: false,
                        whole: true,
                        scale,
                        key_prefix: "music-browse-track",
                    },
                )
            }),
        ));
    }

    let mut any_section = false;
    for (idx, section) in data.sections.iter().enumerate() {
        if section.items.is_empty() {
            continue;
        }
        any_section = true;
        if section.items.iter().all(|c| c.kind == "mood") {
            body = body.child(mood_section(s, idx, section));
        } else {
            body = body.child(shelf_section(s, 200 + idx, section, scale));
        }
    }

    if !any_section && data.tracks.is_empty() {
        body = body.child(text(empty).font(scaled_body(s)).color(fg_muted()).py(24.0));
    }
    body
}

fn mood_section(s: &AppState, idx: usize, section: &PlayerSection) -> El {
    let mut c = col().w_full().mb(28.0).gap(12.0);
    if !section.title.is_empty() {
        c = c.child(text(section.title.clone()).font(scaled_title(s)).color(FG));
    }
    c.child(
        row()
            .w_full()
            .gap(8.0)
            .wrap()
            .children(section.items.iter().enumerate().map(|(j, card)| {
                let uri = card.uri.clone();
                let title = card.title.clone();
                el().key(format!("music-mood-{idx}-{j}"))
                    .role("button")
                    .label(card.title.clone())
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .h(34.0)
                    .px(14.0)
                    .radius(RADIUS_PILL)
                    .fill(TRANSPARENT)
                    .border(1.0, glass_rim())
                    .hover(hover_fill())
                    .center()
                    .on_press(move |s: &mut AppState| {
                        s.music_open_nav(MusicNav::Browse {
                            uri: Some(uri.clone()),
                            title: Some(title.clone()),
                        });
                    })
                    .child(text(card.title.clone()).font(scaled_body(s)).color(FG))
            })),
    )
}

pub(crate) fn discography_page(s: &AppState, data: &PlayerViewData, scale: f32) -> El {
    let artist_name = match &s.media.music_nav {
        MusicNav::Discography { title, .. } => title.clone(),
        _ => String::new(),
    };
    let mut body = col().w_full().child(
        col()
            .w_full()
            .mb(20.0)
            .gap(4.0)
            .child(text("Discography").font(scaled_title(s)).color(FG))
            .children(
                (!artist_name.is_empty())
                    .then(|| text(artist_name).font(scaled_body(s)).color(fg_muted())),
            ),
    );
    if !data.items.is_empty() {
        body = body.child(shelf_grid(s, 300, "", &data.items, None, scale));
        let total = data.total.unwrap_or(0) as usize;
        if data.items.len() < total {
            body = body.child(
                row().w_full().mt(12.0).child(
                    glass_button(
                        "music-discog-more",
                        if s.media.music_page_more_loading {
                            "Loading…"
                        } else {
                            "Show more"
                        },
                        None,
                    )
                    .disabled(s.media.music_page_more_loading)
                    .on_press(|s: &mut AppState| s.load_music_page_more()),
                ),
            );
        }
    } else {
        body = body.child(
            text("No releases yet")
                .font(scaled_body(s))
                .color(fg_muted())
                .py(24.0),
        );
    }
    if let Some(ref err) = s.media.music_page_failed {
        body = body.child(text(err.clone()).font(scaled_small(s)).color(bad()).mt(8.0));
    }
    body
}

pub(crate) fn list_page(s: &AppState, data: &PlayerViewData, scale: f32) -> El {
    let (page_uri, page_kind, page_title) = match &s.media.music_nav {
        MusicNav::Collection {
            uri, kind, title, ..
        } => (uri.clone(), kind.clone(), title.clone()),
        _ => (String::new(), String::new(), String::new()),
    };
    let is_artist = page_kind == "artist";
    let is_album = page_kind == "album";
    let is_show = page_kind == "show";
    let is_collection = page_kind == "collection";
    let context = data
        .context_uri
        .as_deref()
        .filter(|u| !u.is_empty())
        .unwrap_or(&page_uri);
    let editable_uri = data
        .header
        .as_ref()
        .filter(|h| h.editable.unwrap_or(false))
        .map(|h| h.uri.as_str());
    let tracks: Vec<&PlayerTrack> = data
        .tracks
        .iter()
        .filter(|t| {
            let k = if t.uid.is_empty() { &t.uri } else { &t.uid };
            !s.media.music_page_removed.contains(k)
        })
        .collect();
    let total = data.total.unwrap_or(0) as usize;
    let can_move = editable_uri.is_some()
        && s.media.music_service.has_playlists()
        && s.media.music_page_removed.is_empty();

    let mut body = col().w_full();
    if let Some(ref hdr) = data.header {
        body = body.child(page_header(
            s,
            &page_kind,
            &page_title,
            hdr,
            !tracks.is_empty() || is_artist,
            context,
            scale,
        ));
    }

    if !tracks.is_empty() {
        let mut section = col().w_full().mb(24.0);
        if is_artist {
            section = section.child(section_heading(s, "Top songs", data.more.as_ref(), 400));
        }
        let whole = tracks.len() >= total;
        let shown = tracks.len();
        section = section.child(
            col()
                .w_full()
                .gap(2.0)
                .children(tracks.iter().enumerate().map(|(i, t)| {
                    track_row_full(
                        s,
                        TrackRowArgs {
                            idx: i,
                            total: shown,
                            track: t,
                            context: (!context.is_empty()).then_some(context),
                            query_order: None,
                            playlist: editable_uri,
                            show_album: !is_album && !is_show,
                            numbered: is_album,
                            liked_page: is_collection,
                            can_move,
                            whole,
                            scale,
                            key_prefix: "music-page-track",
                        },
                    )
                })),
        );

        if !is_artist {
            let page_size = if is_show { 50 } else { 100 };
            if shown < total && data.tracks.len() >= page_size.min(total) {
                section = section.child(
                    row().w_full().mt(14.0).child(
                        glass_button(
                            "music-page-show-more",
                            if s.media.music_page_more_loading {
                                "Loading…"
                            } else {
                                "Show more"
                            },
                            None,
                        )
                        .disabled(s.media.music_page_more_loading)
                        .on_press(|s: &mut AppState| s.load_music_page_more()),
                    ),
                );
            }
            if let Some(ref hdr) = data.header {
                let facts = build_page_facts(hdr, total.max(shown));
                if !facts.is_empty() {
                    section = section.child(facts_list(s, &facts));
                }
            }
        }
        body = body.child(section);
    }

    if is_artist {
        body = body.child(artist_sections(s, &page_uri, &page_title, data, scale));
    } else {
        body = body.child(page_shelves(s, data, scale));
    }

    body
}

fn page_header(
    s: &AppState,
    page_kind: &str,
    page_title: &str,
    hdr: &PlayerHeader,
    can_play: bool,
    context: &str,
    scale: f32,
) -> El {
    let svc = s.media.music_service;
    let is_artist = page_kind == "artist";
    let is_collection = page_kind == "collection";
    let art_r = if is_artist { RADIUS_PILL } else { RADIUS_CARD };
    let title = if hdr.title.is_empty() {
        page_title
    } else {
        hdr.title.as_str()
    };

    let mut head_col = col().w_full().mb(24.0).gap(16.0);
    if is_artist
        && let Some(ref banner) = hdr.banner
        && !banner.is_empty()
    {
        head_col = head_col.child(
            el().w_full()
                .h(200.0)
                .radius(RADIUS_PANEL)
                .clip_rounded(RADIUS_PANEL)
                .fill(art_surface_fill())
                .child(render_art(s, banner, None, RADIUS_PANEL, false, scale)),
        );
    }

    let mut meta = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(6.0)
        .child(text(title).font(scaled_title(s)).color(FG).wrap());
    if !hdr.subtitle.is_empty() {
        meta = meta.child(
            text(hdr.subtitle.clone())
                .font(scaled_body(s))
                .color(fg_muted())
                .wrap(),
        );
    }
    if !hdr.badges.is_empty() {
        meta = meta.child(row().gap(6.0).wrap().children(hdr.badges.iter().map(|b| {
            el().px(8.0)
                .py(2.0)
                .radius(RADIUS_SMALL)
                .border(1.0, glass_rim())
                .child(text(b.clone()).font(scaled_small(s)).color(fg_muted()))
        })));
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

    let ctx_owned = context.to_string();
    let mut actions = row().mt(8.0).gap(8.0).center_y().wrap().child(
        primary_button("music-page-play", "Play", Some(icon_play(14.0, PRIMARY_FG)))
            .disabled(!can_play)
            .on_press(move |s: &mut AppState| s.music_play_context(&ctx_owned)),
    );

    if svc.has_station()
        && let Some(ref station_uri) = hdr.station
        && !station_uri.is_empty()
    {
        let st_uri = station_uri.clone();
        actions = actions.child(
            glass_button("music-page-station", "Station", None).on_press(
                move |s: &mut AppState| s.music_act_simple("station", json!({ "uri": st_uri })),
            ),
        );
    }

    if let Some(save_act) = save_action_for(page_kind, hdr, svc) {
        let saved = s
            .media
            .music_page_saved
            .unwrap_or_else(|| hdr.saved.unwrap_or(false));
        let label = if is_artist {
            if svc.has_station() {
                if saved { "Favorited" } else { "Favorite" }
            } else if saved {
                "Following"
            } else {
                "Follow"
            }
        } else if svc.has_library() {
            if saved {
                "In library"
            } else {
                "Add to library"
            }
        } else if saved {
            "Saved"
        } else {
            "Save"
        };
        let hdr_uri = hdr.uri.clone();
        actions = actions.child(glass_button("music-page-save", label, None).on_press(
            move |s: &mut AppState| {
                let next = !saved;
                s.media.music_page_saved = Some(next);
                s.music_act_simple(save_act, json!({ "uri": hdr_uri, "on": next }));
            },
        ));
    }

    actions = actions.child(
        el().key("music-page-more-btn")
            .role("button")
            .label(format!("More for {title}"))
            .focusable(true)
            .hit()
            .press_on_down()
            .size(34.0, 34.0)
            .radius(RADIUS_PILL)
            .border(1.0, glass_rim())
            .hover(hover_fill())
            .center()
            .on_press(|s: &mut AppState| {
                s.media.music_menu = Some(MusicMenu::PageMore {
                    anchor_key: "music-page-more-btn".into(),
                });
            })
            .child(icon_more_horiz(16.0, FG)),
    );

    if let Some(ref dl) = hdr.downloaded {
        let dl_label = if dl.as_bool() == Some(true) {
            Some("Downloaded")
        } else if dl.as_str() == Some("downloading") {
            Some("Downloading")
        } else {
            None
        };
        if let Some(lbl) = dl_label {
            actions = actions.child(
                row()
                    .gap(4.0)
                    .center_y()
                    .child(icon_download(14.0, good()))
                    .child(text(lbl).font(scaled_small(s)).color(good())),
            );
        }
    }

    if let Some((ref status, _)) = s.media.music_page_status {
        actions = actions.child(text(status.clone()).font(scaled_small(s)).color(good()));
    }

    meta = meta.child(actions);
    if let Some(ref err) = s.media.music_page_failed {
        meta = meta.child(text(err.clone()).font(scaled_small(s)).color(bad()));
    }

    let mut header_row = row().w_full().gap(20.0).center_y();
    if !hdr.image.is_empty() || !is_artist {
        header_row = header_row.child(render_art(
            s,
            &hdr.image,
            Some(168.0),
            art_r,
            is_collection,
            scale,
        ));
    }
    head_col.child(header_row.child(meta))
}

fn save_action_for(
    page_kind: &str,
    hdr: &PlayerHeader,
    svc: crate::media_state::MusicService,
) -> Option<&'static str> {
    if matches!(page_kind, "album" | "show" | "artist") {
        return Some("like");
    }
    if page_kind != "playlist" || hdr.editable.unwrap_or(false) || hdr.owned.unwrap_or(false) {
        return None;
    }
    if svc.has_library() {
        if hdr.uri.contains(":library-") {
            return None;
        }
        return Some("library");
    }
    if hdr.saved.is_some()
        || (svc.has_playlists() && !hdr.uri.ends_with(":VLLM") && !hdr.uri.ends_with(":VLSE"))
    {
        Some("like")
    } else {
        None
    }
}

fn section_heading(s: &AppState, title: &str, more: Option<&PlayerLink>, idx: usize) -> El {
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
            el().key(format!("music-head-more-{idx}"))
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
    head
}

fn page_shelves(s: &AppState, data: &PlayerViewData, scale: f32) -> El {
    let by = data
        .header
        .as_ref()
        .and_then(|h| h.artists.first())
        .map(|a| a.name.as_str())
        .filter(|n| !n.is_empty());
    let mut c = col().w_full();
    if !data.albums.is_empty() {
        let t = by
            .map(|n| format!("More by {n}"))
            .unwrap_or_else(|| "Albums".into());
        c = c.child(shelf_section(
            s,
            500,
            &PlayerSection {
                title: t,
                more: None,
                items: data.albums.clone(),
            },
            scale,
        ));
    }
    if !data.singles.is_empty() {
        c = c.child(shelf_section(
            s,
            501,
            &PlayerSection {
                title: "Singles and EPs".into(),
                more: None,
                items: data.singles.clone(),
            },
            scale,
        ));
    }
    if !data.related.is_empty() {
        c = c.child(shelf_section(
            s,
            502,
            &PlayerSection {
                title: "Fans also like".into(),
                more: None,
                items: data.related.clone(),
            },
            scale,
        ));
    }
    for (idx, sec) in data.sections.iter().enumerate() {
        if !sec.items.is_empty() {
            c = c.child(shelf_section(s, 510 + idx, sec, scale));
        }
    }
    c
}

fn artist_sections(
    s: &AppState,
    page_uri: &str,
    page_title: &str,
    data: &PlayerViewData,
    scale: f32,
) -> El {
    let svc = s.media.music_service;
    let name = data
        .header
        .as_ref()
        .map(|h| h.title.as_str())
        .filter(|t| !t.is_empty())
        .unwrap_or(page_title);
    let all_link = (!page_uri.is_empty() && svc.has_discography()).then(|| PlayerLink {
        uri: page_uri.to_string(),
        kind: "discography".into(),
    });

    let mut c = col().w_full();
    if !data.albums.is_empty() {
        c = c.child(shelf_section(
            s,
            600,
            &PlayerSection {
                title: "Albums".into(),
                more: all_link.clone(),
                items: data.albums.clone(),
            },
            scale,
        ));
    }
    if !data.singles.is_empty() {
        c = c.child(shelf_section(
            s,
            601,
            &PlayerSection {
                title: "Singles and EPs".into(),
                more: if data.albums.is_empty() {
                    all_link
                } else {
                    None
                },
                items: data.singles.clone(),
            },
            scale,
        ));
    }
    if !data.compilations.is_empty() {
        c = c.child(shelf_section(
            s,
            602,
            &PlayerSection {
                title: "Compilations".into(),
                more: None,
                items: data.compilations.clone(),
            },
            scale,
        ));
    }
    if !data.concerts.is_empty() {
        c = c.child(tour_section(s, &data.concerts));
    }
    if !data.featuring.is_empty() {
        c = c.child(shelf_section(
            s,
            603,
            &PlayerSection {
                title: format!("Featuring {name}"),
                more: None,
                items: data.featuring.clone(),
            },
            scale,
        ));
    }
    if !data.related.is_empty() {
        c = c.child(shelf_section(
            s,
            604,
            &PlayerSection {
                title: "Fans also like".into(),
                more: None,
                items: data.related.clone(),
            },
            scale,
        ));
    }
    if !data.appears_on.is_empty() {
        c = c.child(shelf_section(
            s,
            605,
            &PlayerSection {
                title: "Appears on".into(),
                more: None,
                items: data.appears_on.clone(),
            },
            scale,
        ));
    }
    if !data.discovered_on.is_empty() {
        c = c.child(shelf_section(
            s,
            606,
            &PlayerSection {
                title: "Discovered on".into(),
                more: None,
                items: data.discovered_on.clone(),
            },
            scale,
        ));
    }
    if !data.playlists.is_empty() {
        c = c.child(shelf_section(
            s,
            607,
            &PlayerSection {
                title: format!("{name}'s playlists"),
                more: None,
                items: data.playlists.clone(),
            },
            scale,
        ));
    }
    for (idx, sec) in data.sections.iter().enumerate() {
        if !sec.items.is_empty() {
            c = c.child(shelf_section(s, 620 + idx, sec, scale));
        }
    }
    if let Some(ref about) = data.about {
        if !about.bio.is_empty() || about.listeners > 0 || about.followers > 0 {
            c = c.child(artist_about_section(s, about, scale));
        }
    } else if let Some(ref hdr) = data.header
        && (hdr.about.as_deref().is_some_and(|a| !a.is_empty()) || !hdr.facts.is_empty())
    {
        c = c.child(artist_biography_section(s, hdr));
    }
    c
}

fn tour_section(s: &AppState, concerts: &[Concert]) -> El {
    col()
        .w_full()
        .mb(28.0)
        .gap(10.0)
        .child(text("On tour").font(scaled_title(s)).color(FG))
        .child(
            col()
                .w_full()
                .gap(6.0)
                .children(concerts.iter().take(8).enumerate().map(|(i, c)| {
                    let day = c.date.get(..10).unwrap_or(c.date.as_str());
                    let title = if c.place.is_empty() {
                        c.title.as_str()
                    } else {
                        c.place.as_str()
                    };
                    row()
                        .key(format!("music-tour-{i}"))
                        .w_full()
                        .px(12.0)
                        .py(10.0)
                        .gap(14.0)
                        .radius(ROW_RADIUS)
                        .border(1.0, glass_rim())
                        .center_y()
                        .child(
                            el().w(84.0)
                                .child(text(day).font(scaled_small(s)).color(FG)),
                        )
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(text(title).font(scaled_body(s)).color(FG).truncate())
                                .children((!c.venue.is_empty()).then(|| {
                                    text(c.venue.clone())
                                        .font(scaled_small(s))
                                        .color(fg_muted())
                                        .truncate()
                                })),
                        )
                })),
        )
}

fn artist_about_section(s: &AppState, about: &ArtistAbout, scale: f32) -> El {
    let expanded = s.media.music_about_expanded;
    let mut stats = row().gap(16.0).wrap();
    if about.rank > 0 {
        stats = stats.child(
            text(format!("#{} in the world", format_commas(about.rank)))
                .font(scaled_body(s))
                .color(FG),
        );
    }
    if about.listeners > 0 {
        stats = stats.child(
            text(format!(
                "{} monthly listeners",
                format_commas(about.listeners)
            ))
            .font(scaled_body(s))
            .color(FG),
        );
    }
    if about.followers > 0 {
        stats = stats.child(
            text(format!("{} followers", format_commas(about.followers)))
                .font(scaled_body(s))
                .color(fg_muted()),
        );
    }

    let mut text_col = col().grow(1.0).min_w(0.0).gap(10.0).child(stats);
    if !about.bio.is_empty() {
        let long = about.bio.chars().count() > 320;
        let shown_bio = if long && !expanded {
            let mut clipped: String = about.bio.chars().take(320).collect();
            clipped.push('…');
            clipped
        } else {
            about.bio.clone()
        };
        text_col = text_col.child(
            text(shown_bio)
                .font(scaled_body(s))
                .color(fg_muted())
                .wrap(),
        );
        if long {
            text_col = text_col.child(
                el().key("music-about-toggle")
                    .role("button")
                    .label(if expanded { "Less" } else { "More" })
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .px(8.0)
                    .py(4.0)
                    .radius(RADIUS_SMALL)
                    .hover(hover_fill())
                    .on_press(|s: &mut AppState| {
                        s.media.music_about_expanded = !s.media.music_about_expanded;
                    })
                    .child(
                        text(if expanded { "Less" } else { "More" })
                            .font(scaled_small(s))
                            .color(FG),
                    ),
            );
        }
    }

    if !about.cities.is_empty() {
        text_col = text_col.child(col().w_full().gap(4.0).children(
            about.cities.iter().take(5).map(|city| {
                let place = if city.country.is_empty() {
                    city.city.clone()
                } else {
                    format!("{}, {}", city.city, city.country)
                };
                row()
                    .w_full()
                    .justify_between()
                    .child(text(place).font(scaled_small(s)).color(FG))
                    .child(
                        text(format!("{} listeners", format_commas(city.listeners)))
                            .font(scaled_small(s))
                            .color(fg_muted()),
                    )
            }),
        ));
    }

    if !about.links.is_empty() {
        text_col = text_col.child(row().gap(8.0).wrap().children(
            about.links.iter().enumerate().map(|(i, link)| {
                let url = link.url.clone();
                row()
                    .key(format!("music-about-link-{i}"))
                    .role("button")
                    .label(link.name.clone())
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .h(30.0)
                    .px(12.0)
                    .gap(6.0)
                    .radius(RADIUS_PILL)
                    .border(1.0, glass_rim())
                    .hover(hover_fill())
                    .center_y()
                    .on_press(move |s: &mut AppState| s.music_open_external_url(&url))
                    .child(text(link.name.clone()).font(scaled_small(s)).color(FG))
                    .child(icon_external(13.0, fg_muted()))
            }),
        ));
    }

    let mut body = row().w_full().gap(20.0);
    if !about.image.is_empty() {
        body = body.child(render_art(
            s,
            &about.image,
            Some(180.0),
            RADIUS_CARD,
            false,
            scale,
        ));
    }
    col()
        .w_full()
        .mb(28.0)
        .gap(12.0)
        .child(text("About").font(scaled_title(s)).color(FG))
        .child(body.child(text_col))
}

fn artist_biography_section(s: &AppState, hdr: &PlayerHeader) -> El {
    let mut c = col().w_full().mb(28.0).gap(10.0).child(
        text(format!("About {}", hdr.title))
            .font(scaled_title(s))
            .color(FG),
    );
    if let Some(ref about) = hdr.about
        && !about.is_empty()
    {
        c = c.child(
            text(about.clone())
                .font(scaled_body(s))
                .color(fg_muted())
                .wrap(),
        );
    }
    if !hdr.facts.is_empty() {
        c = c.child(facts_list(s, &hdr.facts));
    }
    c
}

fn build_page_facts(hdr: &PlayerHeader, count: usize) -> Vec<String> {
    if !hdr.facts.is_empty() {
        return hdr.facts.clone();
    }
    if hdr.date.is_none() && hdr.label.is_none() && hdr.copyright.is_empty() {
        return Vec::new();
    }
    let mut out = Vec::new();
    let minutes = hdr
        .duration_ms
        .map(|ms| (ms / 60_000.0).round() as u64)
        .unwrap_or(0);
    let mut summary = Vec::new();
    if let Some(ref d) = hdr.date
        && !d.is_empty()
    {
        summary.push(d.clone());
    }
    summary.push(format!(
        "{count} {}",
        if count == 1 { "song" } else { "songs" }
    ));
    if minutes > 0 {
        summary.push(format!("{minutes} min"));
    }
    out.push(summary.join(" · "));
    if let Some(ref lbl) = hdr.label
        && !lbl.is_empty()
    {
        out.push(lbl.clone());
    }
    out.extend(hdr.copyright.iter().cloned());
    out
}

fn facts_list(s: &AppState, facts: &[String]) -> El {
    col()
        .w_full()
        .mt(16.0)
        .gap(4.0)
        .children(facts.iter().map(|f| {
            text(f.clone())
                .font(scaled_small(s))
                .color(fg_faint())
                .wrap()
        }))
}

pub(crate) struct TrackRowArgs<'a> {
    pub idx: usize,
    pub total: usize,
    pub track: &'a PlayerTrack,
    pub context: Option<&'a str>,
    pub query_order: Option<&'a str>,
    pub playlist: Option<&'a str>,
    pub show_album: bool,
    pub numbered: bool,
    pub liked_page: bool,
    pub can_move: bool,
    pub whole: bool,
    pub scale: f32,
    pub key_prefix: &'a str,
}

pub(crate) fn track_row_full(s: &AppState, a: TrackRowArgs<'_>) -> El {
    let active = !s.media.music_track_uri.is_empty() && s.media.music_track_uri == a.track.uri;
    let uri = a.track.uri.clone();
    let ctx = a.context.map(String::from);
    let q_ord = a.query_order.map(String::from);
    let idx = a.idx;
    let more_key = format!("{}-more-{}", a.key_prefix, a.idx);

    let feedback = s
        .media
        .music_track_feedback
        .as_ref()
        .filter(|(u, _, _, _)| u == &a.track.uri);
    let dur = if let Some((_, word, _, _)) = feedback {
        word.clone()
    } else if a.track.duration_ms > 0.0 {
        format_duration_ms(a.track.duration_ms)
    } else {
        String::new()
    };
    let dur_color = match feedback {
        Some((_, _, true, _)) => bad(),
        Some((_, _, false, _)) => good(),
        None => fg_muted(),
    };

    let lead = if a.numbered {
        el().w(32.0).center().child(
            text(format!("{}", a.idx + 1))
                .font(scaled_small(s))
                .color(if active { good() } else { fg_muted() }),
        )
    } else {
        render_art(s, &a.track.image, Some(40.0), RADIUS_SMALL, false, a.scale)
    };

    let mut title_row = row().w_full().gap(6.0).center_y().child(
        text(a.track.title.clone())
            .font(scaled_body(s))
            .color(if active { good() } else { FG })
            .truncate(),
    );
    if a.track.explicit {
        title_row = title_row.child(
            el().px(4.0)
                .py(1.0)
                .radius(3.0)
                .fill(pressed_fill())
                .child(text("E").font(scaled_small(s)).color(fg_muted())),
        );
    }

    let mut sub_row = row().w_full().gap(4.0).center_y();
    if a.track.is_episode() {
        let mut ep_parts = Vec::new();
        if let Some(ref d) = a.track.date
            && !d.is_empty()
        {
            ep_parts.push(d.clone());
        }
        if a.track.played == Some(true) {
            ep_parts.push("Played".into());
        } else if let Some(prog) = a.track.progress_ms
            && a.track.duration_ms > prog
            && prog > 0.0
        {
            let left = ((a.track.duration_ms - prog) / 60_000.0).round().max(1.0) as u64;
            ep_parts.push(format!("{left} min left"));
        }
        if !ep_parts.is_empty() {
            let ep_txt = ep_parts.join(" · ");
            sub_row = sub_row.child(text(ep_txt).font(scaled_small(s)).color(
                if a.track.played == Some(true) {
                    good()
                } else {
                    fg_muted()
                },
            ));
        }
    }

    if !a.track.artists.is_empty() {
        let first_artist = a.track.artists[0].clone();
        let artist_line = a.track.artist_line();
        if !first_artist.uri.is_empty() {
            sub_row = sub_row.child(
                el().role("button")
                    .label(artist_line.clone())
                    .focusable(false)
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        s.music_open_nav(MusicNav::Collection {
                            uri: first_artist.uri.clone(),
                            kind: "artist".into(),
                            title: first_artist.name.clone(),
                            edit: false,
                        });
                    })
                    .child(
                        text(artist_line)
                            .font(scaled_small(s))
                            .color(fg_muted())
                            .truncate(),
                    ),
            );
        } else {
            sub_row = sub_row.child(
                text(artist_line)
                    .font(scaled_small(s))
                    .color(fg_muted())
                    .truncate(),
            );
        }
    }

    let menu_track = a.track.clone();
    let menu_pl = a.playlist.map(String::from);
    let menu_anchor = more_key.clone();
    let menu_total = a.total;
    let menu_show_album = a.show_album;
    let menu_liked_page = a.liked_page;
    let menu_can_move = a.can_move;
    let menu_whole = a.whole;

    let menu_track_r = a.track.clone();
    let menu_pl_r = a.playlist.map(String::from);
    let menu_anchor_r = more_key.clone();

    let mut r = row()
        .key(format!("{}-{}", a.key_prefix, a.idx))
        .role("button")
        .label(a.track.title.clone())
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
            s.music_play_uri_ordered(&uri, ctx.as_deref(), Some(idx), q_ord.as_deref());
        })
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.load_music_user_playlists();
            s.media.music_menu = Some(MusicMenu::TrackMore {
                track: menu_track_r.clone(),
                index: idx,
                total: menu_total,
                playlist: menu_pl_r.clone(),
                show_album: menu_show_album,
                liked_page: menu_liked_page,
                can_move: menu_can_move,
                whole: menu_whole,
                anchor_key: menu_anchor_r.clone(),
                point: Some((p.x, p.y)),
                open_submenu: None,
            });
        })
        .child(lead)
        .child(col().grow(1.0).min_w(0.0).child(title_row).child(sub_row));

    if a.show_album {
        if let Some(ref alb) = a.track.album {
            let alb_uri = alb.uri.clone();
            let alb_name = alb.name.clone();
            r = r.child(
                el().w(200.0)
                    .role("button")
                    .label(alb_name.clone())
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        if !alb_uri.is_empty() {
                            s.music_open_nav(MusicNav::Collection {
                                uri: alb_uri.clone(),
                                kind: "album".into(),
                                title: alb_name.clone(),
                                edit: false,
                            });
                        }
                    })
                    .child(
                        text(alb.name.clone())
                            .font(scaled_small(s))
                            .color(fg_muted())
                            .truncate(),
                    ),
            );
        } else {
            r = r.child(el().w(200.0));
        }
    }

    let liked_now = s
        .media
        .music_track_likes
        .get(&a.track.uri)
        .copied()
        .or(a.track.liked)
        .unwrap_or(a.liked_page);
    if liked_now {
        r = r.child(icon_heart(13.0, FG, true));
    }

    r = r
        .child(
            el().key(more_key)
                .role("button")
                .label(format!("More for {}", a.track.title))
                .focusable(true)
                .hit()
                .press_on_down()
                .size(28.0, 28.0)
                .radius(RADIUS_PILL)
                .hover(hover_fill())
                .center()
                .on_press(move |s: &mut AppState| {
                    s.load_music_user_playlists();
                    s.media.music_menu = Some(MusicMenu::TrackMore {
                        track: menu_track.clone(),
                        index: idx,
                        total: menu_total,
                        playlist: menu_pl.clone(),
                        show_album: menu_show_album,
                        liked_page: menu_liked_page,
                        can_move: menu_can_move,
                        whole: menu_whole,
                        anchor_key: menu_anchor.clone(),
                        point: None,
                        open_submenu: None,
                    });
                })
                .child(icon_more_horiz(15.0, fg_muted())),
        )
        .child(text(dur).font(scaled_small(s)).color(dur_color).w(64.0));

    if !a.track.playable {
        r = r.opacity(0.45);
    }
    r
}

pub(crate) fn playlist_sheet_overlay(s: &AppState, sheet: &PlaylistSheetState, scale: f32) -> El {
    let creating = sheet.uri.is_none();
    let heading = if creating {
        "New playlist"
    } else {
        "Edit details"
    };
    let can_save = !sheet.busy && !sheet.name_edit.text.trim().is_empty();

    let mut form = col().w_full().gap(14.0).child(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .child(text(heading).font(scaled_title(s)).color(FG))
            .child(
                el().key("music-sheet-close")
                    .role("button")
                    .label("Close")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .size(28.0, 28.0)
                    .radius(RADIUS_PILL)
                    .hover(hover_fill())
                    .center()
                    .on_press(|s: &mut AppState| s.media.music_playlist_sheet = None)
                    .child(icon_close(14.0, fg_muted())),
            ),
    );

    if s.media.music_service.has_cover() {
        let preview = if let Some(ref img) = sheet.cover_preview {
            render_art_image(Some(img.clone()), Some(140.0), RADIUS_CARD, false)
        } else {
            render_art(s, &sheet.image_url, Some(140.0), RADIUS_CARD, false, scale)
        };
        form = form.child(row().w_full().gap(14.0).center_y().child(preview).child(
            glass_button("music-sheet-pick-cover", "Choose picture", None).on_press_tx(
                |_: &mut AppState, tx| {
                    let opts =
                        FileDialogOptions::default().filter("Pictures", &["jpg", "jpeg", "png"]);
                    tx.open_file(opts, |s: &mut AppState, res, _| {
                        if let Ok(Some(path)) = res
                            && let Ok(bytes) = std::fs::read(path)
                        {
                            s.music_playlist_sheet_pick_file(bytes);
                        }
                    });
                },
            ),
        ));
    }

    let name_field = el()
        .w_full()
        .h(36.0)
        .px(12.0)
        .radius(RADIUS_SMALL)
        .border(1.0, glass_rim())
        .center_y()
        .child(
            text_input(&sheet.name_edit, scaled_body(s))
                .key("music-sheet-name-input")
                .label("Name")
                .grow(1.0)
                .single_line()
                .color(FG)
                .placeholder("Playlist name", fg_faint())
                .on_edit(edit_access(|s: &mut AppState| {
                    match s.media.music_playlist_sheet.as_mut() {
                        Some(sheet) => &mut sheet.name_edit,
                        None => &mut s.ui_discard_edit,
                    }
                }))
                .on_submit(|s: &mut AppState, _| s.music_playlist_sheet_save()),
        );

    let desc_field = el()
        .w_full()
        .h(72.0)
        .px(10.0)
        .py(10.0)
        .radius(RADIUS_SMALL)
        .border(1.0, glass_rim())
        .child(
            text_input(&sheet.desc_edit, scaled_body(s))
                .key("music-sheet-desc-input")
                .label("Description")
                .w_full()
                .color(FG)
                .placeholder("Add an optional description", fg_faint())
                .on_edit(edit_access(|s: &mut AppState| {
                    match s.media.music_playlist_sheet.as_mut() {
                        Some(sheet) => &mut sheet.desc_edit,
                        None => &mut s.ui_discard_edit,
                    }
                })),
        );

    form = form
        .child(
            col()
                .w_full()
                .gap(6.0)
                .child(text("Name").font(scaled_small(s)).color(fg_muted()))
                .child(name_field),
        )
        .child(
            col()
                .w_full()
                .gap(6.0)
                .child(text("Description").font(scaled_small(s)).color(fg_muted()))
                .child(desc_field),
        );

    if let Some(ref cur_priv) = sheet.privacy {
        let mut priv_row = row().gap(8.0);
        for (id, lbl) in [
            ("PUBLIC", "Public"),
            ("UNLISTED", "Unlisted"),
            ("PRIVATE", "Private"),
        ] {
            let next_p = id.to_string();
            priv_row = priv_row.child(
                filter_pill(format!("music-sheet-priv-{id}"), lbl, cur_priv == id).on_press(
                    move |s: &mut AppState| {
                        if let Some(ref mut sh) = s.media.music_playlist_sheet {
                            sh.privacy = Some(next_p.clone());
                        }
                    },
                ),
            );
        }
        form = form.child(
            col()
                .w_full()
                .gap(6.0)
                .child(text("Privacy").font(scaled_small(s)).color(fg_muted()))
                .child(priv_row),
        );
    }

    if let Some(ref err) = sheet.error {
        form = form.child(text(err.clone()).font(scaled_small(s)).color(bad()).wrap());
    }

    let action_row = row()
        .w_full()
        .mt(6.0)
        .justify_end()
        .gap(8.0)
        .child(
            glass_button("music-sheet-cancel", "Cancel", None)
                .on_press(|s: &mut AppState| s.media.music_playlist_sheet = None),
        )
        .child(
            primary_button(
                "music-sheet-save",
                if sheet.busy {
                    if creating { "Creating…" } else { "Saving…" }
                } else if creating {
                    "Create"
                } else {
                    "Save"
                },
                None,
            )
            .disabled(!can_save)
            .on_press(|s: &mut AppState| s.music_playlist_sheet_save()),
        );
    form = form.child(action_row);

    el().key("music-playlist-sheet-backdrop")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG.alpha(0.68))
        .center()
        .child(
            glass_panel("music-playlist-sheet", GLASS_PANEL_SIGMA)
                .w(440.0)
                .px(20.0)
                .py(20.0)
                .child(
                    col()
                        .w_full()
                        .max_h(640.0)
                        .scroll(ScrollSpec {
                            stick_bottom: false,
                        })
                        .child(form),
                ),
        )
}

pub(crate) fn room_state_replaces_chrome(state: &MusicRoomState) -> bool {
    matches!(
        state,
        MusicRoomState::SignedOut | MusicRoomState::NotPremium | MusicRoomState::Error(_)
    )
}
