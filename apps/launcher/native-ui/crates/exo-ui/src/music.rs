use crate::media_state::MusicService;
use crate::state::{AppState, Room};
use exo_kit::{
    BG, BadgeTone, FG, GLASS_CONTROL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL,
    RADIUS_PILL, SPRING_QUICK, bad, fg_muted, filter_pill, glass_button, glass_control_fill,
    glass_rim, good, hairline, hover_fill, icon_music, icon_pause, icon_play, icon_refresh,
    icon_skip_back, icon_skip_forward, icon_sparkles, icon_volume, icon_volume_off,
    primary_button, progress_bar, status_badge, subtle_fill, type_body, type_page,
    type_section, type_small, type_title,
};
use exo_rpc::format_duration_ms;
use rune::color::oklch;
use rune::{El, SurfaceFit, col, el, row, text};

fn track_hue(title: &str) -> f32 {
    let mut h: u32 = 2166136261;
    for b in title.bytes() {
        h ^= b as u32;
        h = h.wrapping_mul(16777619);
    }
    (h % 360) as f32
}

fn provider_label(provider: &str) -> &'static str {
    match provider {
        "spotify" => "Spotify",
        "apple" => "Apple Music",
        "youtube" => "YouTube Music",
        _ => "System Media",
    }
}

pub fn view(s: &AppState, scale: f32) -> El {
    let pb = s.media.playback.as_ref().filter(|p| p.has_track());
    let hue = pb.map_or(220.0, |p| track_hue(&p.title));
    let art_img = pb
        .and_then(|p| p.artwork_url.as_deref())
        .and_then(|u| s.remote_image(u, scale));

    let mut ambient = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .w_full()
        .h(420.0);
    if let Some(ref img) = art_img {
        ambient = ambient
            .pixels_fit(img.pixels(), SurfaceFit::Fill)
            .blur(30.0)
            .opacity(0.32);
    } else {
        ambient = ambient.linear_gradient(
            0.3,
            [(0.0, oklch(0.34, 0.14, hue).alpha(0.55)), (1.0, BG.alpha(0.0))],
        );
    }
    let fade = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .w_full()
        .h(420.0)
        .linear_gradient(
            std::f32::consts::PI,
            [(0.0, BG.alpha(0.24)), (0.68, BG.alpha(0.86)), (1.0, BG)],
        );

    let service_pills = row()
        .gap(6.0)
        .flex_wrap()
        .children(MusicService::ALL.into_iter().map(|svc| {
            let active = s.media.music_service == svc;
            filter_pill(svc.key(), svc.label().into(), active)
                .on_press(move |s: &mut AppState| s.select_music_service(svc))
        }));

    let header = row()
        .w_full()
        .justify_between()
        .center_y()
        .flex_wrap()
        .gap(12.0)
        .child(text("Music").font(type_page()).color(FG))
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    glass_button(
                        "music-immersion",
                        if s.media.music_immersion {
                            "Exit immersion"
                        } else {
                            "Immersion"
                        },
                        Some(icon_sparkles(13.0, FG)),
                    )
                    .on_press(|s: &mut AppState| s.media.music_immersion = !s.media.music_immersion),
                )
                .child(
                    glass_button("music-refresh", "Refresh", Some(icon_refresh(13.0, FG)))
                        .on_press(|s: &mut AppState| s.load_music_room()),
                ),
        );

    let vol_pct = (s.media.music_volume * 100.0).round() as i32;
    let muted = s.media.music_muted;

    let stage = if let Some(track) = pb {
        let art_size = if s.media.music_immersion { 280.0 } else { 220.0 };
        let mut cover = el()
            .size(art_size, art_size)
            .radius(RADIUS_PANEL)
            .shadow(16.0, 40.0, BG.alpha(0.70));
        if let Some(ref img) = art_img {
            cover = cover.pixels_fit(img.pixels(), SurfaceFit::Fill);
        } else {
            cover = cover
                .linear_gradient(
                    0.35,
                    [(0.0, oklch(0.50, 0.16, hue)), (1.0, oklch(0.14, 0.04, hue))],
                )
                .center()
                .child(icon_music(54.0, FG.alpha(0.65)));
        }

        let pos = track.position_ms.unwrap_or(0) as f64;
        let dur = track.duration_ms.unwrap_or(0).max(1) as f64;
        let frac = (pos / dur).clamp(0.0, 1.0) as f32;
        let playing = track.is_playing();

        let transport = row()
            .gap(10.0)
            .center_y()
            .child(
                glass_button("music-prev", "Prev", Some(icon_skip_back(14.0, FG)))
                    .on_press(|s: &mut AppState| s.music_control("previous")),
            )
            .child(
                primary_button(
                    "music-toggle",
                    if playing { "Pause" } else { "Play" },
                    Some(if playing {
                        icon_pause(14.0, PRIMARY_FG)
                    } else {
                        icon_play(14.0, PRIMARY_FG)
                    }),
                )
                .on_press(|s: &mut AppState| s.music_control("toggle")),
            )
            .child(
                glass_button("music-next", "Next", Some(icon_skip_forward(14.0, FG)))
                    .on_press(|s: &mut AppState| s.music_control("next")),
            );

        let vol_row = row()
            .gap(8.0)
            .center_y()
            .child(if muted {
                icon_volume_off(15.0, bad())
            } else {
                icon_volume(15.0, fg_muted())
            })
            .child(text(format!("{vol_pct}%")).font(type_small()).color(fg_muted()))
            .child(
                glass_button("music-vol-dn", "−", None).on_press(move |s: &mut AppState| {
                    s.music_set_volume((s.media.music_volume - 0.10).max(0.0), None);
                }),
            )
            .child(
                glass_button("music-vol-up", "+", None).on_press(move |s: &mut AppState| {
                    s.music_set_volume((s.media.music_volume + 0.10).min(1.0), None);
                }),
            )
            .child(
                glass_button("music-mute", if muted { "Unmute" } else { "Mute" }, None)
                    .on_press(move |s: &mut AppState| s.music_set_volume(s.media.music_volume, Some(!muted))),
            );

        if s.media.music_immersion {
            col()
                .w_full()
                .py(26.0)
                .gap(24.0)
                .items_center()
                .child(cover)
                .child(
                    col()
                        .max_w(540.0)
                        .gap(12.0)
                        .items_center()
                        .child(text(track.title.clone()).font(type_page()).color(FG))
                        .child(text(track.artist.clone()).font(type_title()).color(fg_muted()))
                        .child(progress_bar(440.0, frac))
                        .child(transport)
                        .child(vol_row),
                )
        } else {
            let meta = col()
                .grow(1.0)
                .min_w(0.0)
                .gap(12.0)
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(status_badge(
                            provider_label(&track.provider).into(),
                            if playing { BadgeTone::Good } else { BadgeTone::Neutral },
                        ))
                        .child(status_badge(
                            if playing { "Now playing".into() } else { "Paused".into() },
                            BadgeTone::Neutral,
                        )),
                )
                .child(text(track.title.clone()).font(type_page()).color(FG).w_full().wrap())
                .child(
                    text(if track.album.is_empty() {
                        track.artist.clone()
                    } else {
                        format!("{} · {}", track.artist, track.album)
                    })
                    .font(type_title())
                    .color(fg_muted())
                    .w_full()
                    .wrap(),
                )
                .child(
                    col()
                        .w_full()
                        .max_w(480.0)
                        .gap(6.0)
                        .child(progress_bar(440.0, frac))
                        .child(
                            row()
                                .w(440.0)
                                .justify_between()
                                .child(text(format_duration_ms(pos)).font(type_small()).color(fg_muted()))
                                .child(text(format_duration_ms(dur)).font(type_small()).color(fg_muted())),
                        ),
                )
                .child(row().gap(18.0).flex_wrap().center_y().child(transport).child(vol_row));

            row()
                .w_full()
                .px(22.0)
                .py(22.0)
                .gap(26.0)
                .center_y()
                .radius(RADIUS_PANEL)
                .fill(subtle_fill())
                .border(1.0, hairline())
                .child(cover)
                .child(meta)
        }
    } else {
        col()
            .w_full()
            .px(24.0)
            .py(36.0)
            .gap(12.0)
            .items_center()
            .radius(RADIUS_PANEL)
            .fill(subtle_fill())
            .border(1.0, hairline())
            .child(icon_music(34.0, fg_muted()))
            .child(text("Nothing playing right now").font(type_title()).color(FG))
            .child(
                text("Start playback in Spotify, Apple Music, or YouTube Music — Exo controls active Windows media automatically without extra browser overhead.")
                    .font(type_body())
                    .color(fg_muted())
                    .max_w(520.0)
                    .wrap(),
            )
            .child(
                row()
                    .gap(8.0)
                    .child(
                        glass_button("music-empty-refresh", "Check now", Some(icon_refresh(13.0, FG)))
                            .on_press(|s: &mut AppState| s.load_music_room()),
                    )
                    .children((s.media.music_service != MusicService::System).then(|| {
                        glass_button("music-signin", "Sign in", None)
                            .on_press(|s: &mut AppState| s.music_sign_in())
                    })),
            )
    };

    let mut content = col()
        .w_full()
        .pt(74.0)
        .px(28.0)
        .pb(36.0)
        .gap(18.0)
        .child(header);
    if !s.media.music_immersion {
        content = content.child(service_pills);
    }
    content = content.child(stage);

    el().w_full().child(ambient).child(fade).child(content)
}

pub fn mini_player(s: &AppState, scale: f32) -> Option<El> {
    if s.room == Room::Music
        || s.tour_step.is_some()
        || s.palette_open
        || s.key_modal_open
        || s.props_open
        || s.media.open_capture_id.is_some()
    {
        return None;
    }
    let track = s.media.playback.as_ref().filter(|p| p.has_track())?;
    let hue = track_hue(&track.title);
    let playing = track.is_playing();
    let art_img = track
        .artwork_url
        .as_deref()
        .and_then(|u| s.remote_image(u, scale));

    let mut thumb = el().size(34.0, 34.0).radius(8.0);
    if let Some(img) = art_img {
        thumb = thumb.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        thumb = thumb
            .linear_gradient(
                0.35,
                [(0.0, oklch(0.50, 0.16, hue)), (1.0, oklch(0.16, 0.04, hue))],
            )
            .center()
            .child(icon_music(14.0, FG));
    }

    Some(
        row()
            .key("music-mini-player")
            .pin(None, None, Some(16.0), Some(16.0))
            .h(52.0)
            .px(10.0)
            .gap(10.0)
            .center_y()
            .radius(RADIUS_CARD)
            .backdrop_blur(GLASS_CONTROL_SIGMA)
            .fill(glass_control_fill())
            .border(1.0, glass_rim())
            .shadow(10.0, 28.0, BG.alpha(0.55))
            .child(
                row()
                    .key("mini-open-music")
                    .role("button")
                    .label("Open Music")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .gap(10.0)
                    .center_y()
                    .on_press(|s: &mut AppState| s.switch_room(Room::Music))
                    .child(thumb)
                    .child(
                        col()
                            .w(148.0)
                            .gap(1.0)
                            .child(text(track.title.clone()).font(type_section()).color(FG).truncate())
                            .child(text(track.artist.clone()).font(type_small()).color(fg_muted()).truncate()),
                    ),
            )
            .child(
                el().key("mini-toggle")
                    .role("button")
                    .label(if playing { "Pause" } else { "Play" })
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .size(32.0, 32.0)
                    .radius(RADIUS_PILL)
                    .center()
                    .fill(FG)
                    .motion(SPRING_QUICK)
                    .on_press(|s: &mut AppState| s.music_control("toggle"))
                    .child(if playing {
                        icon_pause(13.0, PRIMARY_FG)
                    } else {
                        icon_play(13.0, PRIMARY_FG)
                    }),
            )
            .child(
                el().key("mini-next")
                    .role("button")
                    .label("Next track")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .size(30.0, 30.0)
                    .radius(RADIUS_PILL)
                    .center()
                    .hover(hover_fill())
                    .motion(SPRING_QUICK)
                    .on_press(|s: &mut AppState| s.music_control("next"))
                    .child(icon_skip_forward(13.0, FG)),
            )
            .child(
                el().size(6.0, 6.0)
                    .radius(3.0)
                    .fill(if playing { good() } else { fg_muted() }),
            ),
    )
}
