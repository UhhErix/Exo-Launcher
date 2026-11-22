use crate::media_state::CaptureFilter;
use crate::pc_state::format_bytes;
use crate::state::AppState;
use exo_kit::{
    BG, BadgeTone, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, SPRING_MOVE, bad,
    fg_muted, filter_pill, glass_button, glass_rim, hairline, icon_back, icon_camera, icon_film,
    icon_folder, icon_pause, icon_play, icon_refresh, icon_scissors, icon_star, icon_trash,
    icon_volume, icon_volume_off, primary_button, section_card, stat_chip, status_badge,
    subtle_fill, type_body, type_page, type_section, type_small, type_title, warn,
};
use exo_rpc::{CaptureItem, format_duration_ms};
use rune::color::oklch;
use rune::{El, SurfaceFit, col, el, row, text};

fn clip_hue(name: &str) -> f32 {
    let mut h: u32 = 2166136261;
    for b in name.bytes() {
        h ^= b as u32;
        h = h.wrapping_mul(16777619);
    }
    (h % 360) as f32
}

fn clip_card(item: &CaptureItem, s: &AppState, scale: f32) -> El {
    let hue = clip_hue(&item.game);
    let thumb = item
        .thumb
        .as_deref()
        .and_then(|u| s.capture_thumb_image(u, scale));
    let mut preview = el()
        .w(284.0)
        .h(160.0)
        .radius(RADIUS_CARD)
        .clip_rounded(RADIUS_CARD);
    if let Some(img) = thumb {
        preview = preview.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        preview = preview
            .linear_gradient(
                0.35,
                [(0.0, oklch(0.40, 0.13, hue)), (1.0, oklch(0.12, 0.03, hue))],
            )
            .center()
            .child(if item.kind == "video" {
                icon_film(28.0, FG.alpha(0.55))
            } else {
                icon_camera(28.0, FG.alpha(0.55))
            });
    }

    let badge = if let Some(dur) = item.duration_ms {
        format_duration_ms(dur)
    } else {
        "PNG".into()
    };

    let overlay = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::PI,
            [(0.0, BG.alpha(0.05)), (0.55, BG.alpha(0.25)), (1.0, BG.alpha(0.90))],
        )
        .child(
            col()
                .w_full()
                .h_full()
                .px(12.0)
                .py(10.0)
                .justify_between()
                .child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .child(status_badge(badge, BadgeTone::Neutral))
                        .children(item.favourite.then(|| icon_star(14.0, warn()))),
                )
                .child(
                    col()
                        .gap(2.0)
                        .child(text(item.name.clone()).font(type_section()).color(FG).truncate())
                        .child(
                            text(format!("{} · {}", item.game, format_bytes(item.size)))
                                .font(type_small())
                                .color(fg_muted())
                                .truncate(),
                        ),
                ),
        );

    let id = item.id.clone();
    el().key(format!("cap-card-{}", item.id))
        .role("button")
        .label(item.name.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .radius(RADIUS_CARD)
        .border(1.0, hairline())
        .motion(SPRING_MOVE)
        .on_press(move |s: &mut AppState| s.open_capture_item(&id))
        .child(preview.child(overlay))
}

fn clip_theater(s: &AppState, item: &CaptureItem, scale: f32) -> El {
    let hue = clip_hue(&item.game);
    let stage = if item.kind == "video" {
        if let Some(ref player) = s.media.clip_player {
            el().w_full()
                .h(410.0)
                .radius(RADIUS_PANEL)
                .clip_rounded(RADIUS_PANEL)
                .fill(BG)
                .border(1.0, glass_rim())
                .video(player, SurfaceFit::Fit)
        } else {
            el().w_full()
                .h(410.0)
                .radius(RADIUS_PANEL)
                .linear_gradient(
                    0.35,
                    [(0.0, oklch(0.36, 0.13, hue)), (1.0, oklch(0.10, 0.03, hue))],
                )
                .border(1.0, glass_rim())
                .center()
                .child(
                    col()
                        .gap(8.0)
                        .items_center()
                        .child(icon_film(40.0, FG.alpha(0.75)))
                        .child(
                            text("Hardware Media Foundation D3D12 player ready")
                                .font(type_section())
                                .color(FG),
                        )
                        .child(
                            text("Plays local MP4/WebM clips zero-copy when connected to the Exo host.")
                                .font(type_small())
                                .color(fg_muted()),
                        ),
                )
        }
    } else if let Some(img) = s.capture_thumb_image(&item.url, scale) {
        el().w_full()
            .h(410.0)
            .radius(RADIUS_PANEL)
            .clip_rounded(RADIUS_PANEL)
            .fill(BG)
            .border(1.0, glass_rim())
            .pixels_fit(img.pixels(), SurfaceFit::Fit)
    } else {
        el().w_full()
            .h(410.0)
            .radius(RADIUS_PANEL)
            .linear_gradient(
                0.35,
                [(0.0, oklch(0.40, 0.13, hue)), (1.0, oklch(0.12, 0.03, hue))],
            )
            .border(1.0, glass_rim())
            .center()
            .child(icon_camera(42.0, FG.alpha(0.70)))
    };

    let id_fav = item.id.clone();
    let id_reveal = item.id.clone();
    let id_del = item.id.clone();
    let next_fav = if item.favourite { "unfavourite" } else { "favourite" };

    let mut controls = row().w_full().gap(8.0).flex_wrap().center_y();
    if item.kind == "video" {
        controls = controls
            .child(
                primary_button(
                    "cap-play-toggle",
                    if s.media.clip_playing { "Pause" } else { "Play" },
                    Some(if s.media.clip_playing {
                        icon_pause(13.0, PRIMARY_FG)
                    } else {
                        icon_play(13.0, PRIMARY_FG)
                    }),
                )
                .on_press(|s: &mut AppState| s.toggle_clip_play()),
            )
            .child(
                glass_button(
                    "cap-mute-toggle",
                    if s.media.clip_muted { "Unmute" } else { "Mute" },
                    Some(if s.media.clip_muted {
                        icon_volume_off(13.0, bad())
                    } else {
                        icon_volume(13.0, FG)
                    }),
                )
                .on_press(|s: &mut AppState| s.toggle_clip_mute()),
            );
    }
    controls = controls
        .child(
            glass_button(
                "cap-fav",
                if item.favourite { "Favorited" } else { "Favorite" },
                Some(icon_star(13.0, if item.favourite { warn() } else { FG })),
            )
            .on_press(move |s: &mut AppState| s.capture_item_action(&id_fav, next_fav)),
        )
        .child(
            glass_button("cap-reveal", "Show in Explorer", Some(icon_folder(13.0, FG)))
                .on_press(move |s: &mut AppState| s.capture_item_action(&id_reveal, "reveal")),
        )
        .child(
            glass_button("cap-del", "Delete", Some(icon_trash(13.0, bad())))
                .on_press(move |s: &mut AppState| s.capture_item_action(&id_del, "delete")),
        );

    let mut page = col()
        .w_full()
        .pt(74.0)
        .px(28.0)
        .pb(36.0)
        .gap(16.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    glass_button("cap-back-gallery", "All captures", Some(icon_back(14.0, FG)))
                        .on_press(|s: &mut AppState| s.close_capture_item()),
                )
                .child(
                    row()
                        .gap(8.0)
                        .child(stat_chip("Game", item.game.clone()))
                        .child(stat_chip("Size", format_bytes(item.size)))
                        .children(
                            item.duration_ms
                                .map(|d| stat_chip("Length", format_duration_ms(d))),
                        ),
                ),
        )
        .child(text(item.name.clone()).font(type_title()).color(FG))
        .child(stage)
        .child(controls);

    if item.kind == "video" {
        let dur_ms = item.duration_ms.unwrap_or(30_000.0);
        let start_str = format_duration_ms(dur_ms * s.media.trim_start_pct as f64);
        let end_str = format_duration_ms(dur_ms * s.media.trim_end_pct as f64);
        let id_trim = item.id.clone();
        let id_disc = item.id.clone();
        let id_yt = item.id.clone();
        let id_vert = item.id.clone();

        let trim_card = section_card(
            "Lossless Trim & Export",
            Some(format!("Selection: {start_str} → {end_str}")),
        )
        .child(
            row()
                .w_full()
                .gap(8.0)
                .flex_wrap()
                .center_y()
                .child(
                    glass_button("trim-in-dn", "In −5%", None).on_press(|s: &mut AppState| {
                        s.media.trim_start_pct = (s.media.trim_start_pct - 0.05).max(0.0);
                    }),
                )
                .child(
                    glass_button("trim-in-up", "In +5%", None).on_press(|s: &mut AppState| {
                        s.media.trim_start_pct =
                            (s.media.trim_start_pct + 0.05).min(s.media.trim_end_pct - 0.05);
                    }),
                )
                .child(
                    glass_button("trim-out-dn", "Out −5%", None).on_press(|s: &mut AppState| {
                        s.media.trim_end_pct =
                            (s.media.trim_end_pct - 0.05).max(s.media.trim_start_pct + 0.05);
                    }),
                )
                .child(
                    glass_button("trim-out-up", "Out +5%", None).on_press(|s: &mut AppState| {
                        s.media.trim_end_pct = (s.media.trim_end_pct + 0.05).min(1.0);
                    }),
                )
                .child(
                    primary_button("cap-trim-go", "Trim lossless", Some(icon_scissors(13.0, PRIMARY_FG)))
                        .on_press(move |s: &mut AppState| s.capture_export_trim(&id_trim, "trim")),
                )
                .child(
                    glass_button("cap-exp-discord", "Discord 10 MB", None)
                        .on_press(move |s: &mut AppState| s.capture_export_trim(&id_disc, "discord")),
                )
                .child(
                    glass_button("cap-exp-yt", "YouTube", None)
                        .on_press(move |s: &mut AppState| s.capture_export_trim(&id_yt, "youtube")),
                )
                .child(
                    glass_button("cap-exp-vert", "Vertical 9:16", None)
                        .on_press(move |s: &mut AppState| s.capture_export_trim(&id_vert, "vertical")),
                ),
        );
        page = page.child(trim_card);
    }

    page
}

pub fn view(s: &AppState, scale: f32) -> El {
    if let Some(ref open_id) = s.media.open_capture_id
        && let Some(item) = s.media.capture_items.iter().find(|c| &c.id == open_id)
    {
        return clip_theater(s, item, scale);
    }

    let replay_mode = s
        .media
        .capture_status
        .as_ref()
        .and_then(|st| st.settings.as_ref())
        .map(|cfg| cfg.replay_mode.as_str())
        .unwrap_or("games");

    let filtered: Vec<&CaptureItem> = s
        .media
        .capture_items
        .iter()
        .filter(|c| match s.media.capture_filter {
            CaptureFilter::All => true,
            CaptureFilter::Videos => c.kind == "video",
            CaptureFilter::Screenshots => c.kind != "video",
            CaptureFilter::Favorites => c.favourite,
        })
        .collect();

    let header = row()
        .w_full()
        .justify_between()
        .center_y()
        .flex_wrap()
        .gap(12.0)
        .child(
            row()
                .gap(12.0)
                .center_y()
                .child(
                    glass_button("cap-close", "Back", Some(icon_back(14.0, FG)))
                        .on_press(|s: &mut AppState| s.close_captures()),
                )
                .child(text("Captures").font(type_page()).color(FG)),
        )
        .child(
            row()
                .gap(8.0)
                .flex_wrap()
                .center_y()
                .child(
                    primary_button("cap-save-replay", "Save replay", Some(icon_film(13.0, PRIMARY_FG)))
                        .on_press(|s: &mut AppState| s.capture_act("saveReplay")),
                )
                .child(
                    glass_button("cap-record", "Record", None)
                        .on_press(|s: &mut AppState| s.capture_act("record")),
                )
                .child(
                    glass_button("cap-snip", "Snip", Some(icon_camera(13.0, FG)))
                        .on_press(|s: &mut AppState| s.capture_act("snip")),
                )
                .child(
                    glass_button("cap-open-folder", "Folder", Some(icon_folder(13.0, FG)))
                        .on_press(|s: &mut AppState| s.capture_act("openFolder")),
                )
                .child(
                    glass_button("cap-refresh", "Refresh", Some(icon_refresh(13.0, FG)))
                        .on_press(|s: &mut AppState| s.load_captures()),
                ),
        );

    let replay_bar = row()
        .w_full()
        .px(16.0)
        .py(12.0)
        .justify_between()
        .center_y()
        .flex_wrap()
        .gap(10.0)
        .radius(RADIUS_PANEL)
        .fill(subtle_fill())
        .border(1.0, hairline())
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(status_badge(
                    if replay_mode == "off" {
                        "Instant Replay off".into()
                    } else {
                        format!("Instant Replay · {replay_mode}")
                    },
                    if replay_mode == "off" {
                        BadgeTone::Neutral
                    } else {
                        BadgeTone::Good
                    },
                ))
                .children(
                    s.media
                        .capture_status
                        .as_ref()
                        .and_then(|st| st.settings.as_ref())
                        .map(|cfg| {
                            text(format!(
                                "{}s · {} fps · {}",
                                cfg.replay_seconds, cfg.max_fps, cfg.save_replay_keys
                            ))
                            .font(type_small())
                            .color(fg_muted())
                        }),
                ),
        )
        .child(
            row()
                .gap(6.0)
                .child(
                    filter_pill("cap-rm-games", "In games".into(), replay_mode == "games")
                        .on_press(|s: &mut AppState| s.capture_set_replay_mode("games")),
                )
                .child(
                    filter_pill("cap-rm-always", "Always".into(), replay_mode == "always")
                        .on_press(|s: &mut AppState| s.capture_set_replay_mode("always")),
                )
                .child(
                    filter_pill("cap-rm-off", "Off".into(), replay_mode == "off")
                        .on_press(|s: &mut AppState| s.capture_set_replay_mode("off")),
                ),
        );

    let filters = row()
        .gap(6.0)
        .flex_wrap()
        .child(
            filter_pill(
                "cap-f-all",
                format!("All ({})", s.media.capture_items.len()),
                s.media.capture_filter == CaptureFilter::All,
            )
            .on_press(|s: &mut AppState| s.media.capture_filter = CaptureFilter::All),
        )
        .child(
            filter_pill(
                "cap-f-vid",
                "Videos".into(),
                s.media.capture_filter == CaptureFilter::Videos,
            )
            .on_press(|s: &mut AppState| s.media.capture_filter = CaptureFilter::Videos),
        )
        .child(
            filter_pill(
                "cap-f-img",
                "Screenshots".into(),
                s.media.capture_filter == CaptureFilter::Screenshots,
            )
            .on_press(|s: &mut AppState| s.media.capture_filter = CaptureFilter::Screenshots),
        )
        .child(
            filter_pill(
                "cap-f-fav",
                "Favorites".into(),
                s.media.capture_filter == CaptureFilter::Favorites,
            )
            .on_press(|s: &mut AppState| s.media.capture_filter = CaptureFilter::Favorites),
        );

    let body = if filtered.is_empty() {
        el().w_full()
            .h(200.0)
            .radius(RADIUS_PANEL)
            .fill(subtle_fill())
            .border(1.0, hairline())
            .center()
            .child(
                col()
                    .gap(6.0)
                    .items_center()
                    .child(
                        text(if s.media.captures_loading {
                            "Loading captures…"
                        } else {
                            "No captures yet"
                        })
                        .font(type_section())
                        .color(FG),
                    )
                    .child(
                        text("Press Ctrl+Alt+F10 during any game to save an Instant Replay.")
                            .font(type_body())
                            .color(fg_muted()),
                    ),
            )
    } else {
        row()
            .w_full()
            .flex_wrap()
            .gap(14.0)
            .children(filtered.into_iter().take(48).map(|item| clip_card(item, s, scale)))
    };

    col()
        .w_full()
        .pt(74.0)
        .px(28.0)
        .pb(36.0)
        .gap(16.0)
        .child(header)
        .child(replay_bar)
        .child(filters)
        .child(body)
}
