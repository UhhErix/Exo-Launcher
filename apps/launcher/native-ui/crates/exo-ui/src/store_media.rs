use crate::state::{AppState, HostStatus, InputMode, title_hue};
use exo_kit::{
    BG, FG, PRESS_SCALE, RADIUS_CARD, RADIUS_PILL, RADIUS_SMALL, SPRING_FLIGHT, SPRING_MOVE,
    SPRING_QUICK, fg_muted, float_line, glass_control_fill, glass_icon_circle_opt, glass_rim,
    glass_rim_bottom, glass_rim_top, hover_fill, icon_chevron_left, icon_chevron_right, icon_close,
    icon_maximize, icon_pause, icon_play, icon_volume, icon_volume_off, modal_card_transition,
    modal_scrim_transition, pressed_fill, scrim_fill, track_fill, type_control, type_small,
};
use exo_rpc::{StoreArt, StoreGame};
use rune::{Cursor, El, SurfaceFit, col, el, oklch, row, text};
use rune_media::{Player, PlayerState};

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ShownMedia {
    Trailer(usize),
    Screenshot(usize),
}

pub fn thumb_of_screenshot(url: &str) -> String {
    let lower = url.to_ascii_lowercase();
    if let Some(pos) = lower.rfind(".1920x1080.jpg") {
        let mut out = String::with_capacity(url.len());
        out.push_str(&url[..pos]);
        out.push_str(".600x338.jpg");
        out.push_str(&url[pos + ".1920x1080.jpg".len()..]);
        out
    } else {
        url.to_string()
    }
}

pub fn default_shown_media(game: &StoreGame) -> Option<ShownMedia> {
    if let Some(idx) = game
        .media
        .iter()
        .position(|m| m.hls.as_deref().is_some_and(|u| !u.is_empty()))
    {
        return Some(ShownMedia::Trailer(idx));
    }
    if !game.screenshots.is_empty() {
        return Some(ShownMedia::Screenshot(0));
    }
    None
}

fn format_media_time(secs: f64) -> String {
    if !secs.is_finite() || secs < 0.0 {
        return "0:00".into();
    }
    let total = secs.floor() as u64;
    let mins = total / 60;
    let rem = total % 60;
    format!("{mins}:{rem:02}")
}

impl AppState {
    pub fn store_stop_trailer(&mut self) {
        if let Some(ref p) = self.store_ext.trailer_player {
            p.pause();
        }
        if let Some(ref mf) = self.store_ext.trailer_mf {
            mf.stop();
        }
        self.store_ext.trailer_player = None;
        self.store_ext.trailer_mf = None;
        self.store_ext.trailer_url = None;
        self.store_ext.trailer_playing = false;
    }

    pub fn store_sync_trailer_for_game(&mut self) {
        self.store_stop_trailer();
        self.store_ext.lightbox_index = None;
        let shown = self.store_game.as_ref().and_then(default_shown_media);
        self.store_ext.shown_media = shown.clone();
        if let Some(ShownMedia::Trailer(idx)) = shown {
            self.store_start_trailer(idx);
        }
    }

    pub fn store_select_media(&mut self, shown: ShownMedia) {
        if self.store_ext.shown_media.as_ref() == Some(&shown) {
            return;
        }
        self.store_stop_trailer();
        self.store_ext.shown_media = Some(shown.clone());
        if let ShownMedia::Trailer(idx) = shown {
            self.store_start_trailer(idx);
        }
    }

    pub fn store_start_trailer(&mut self, idx: usize) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let hls = self
            .store_game
            .as_ref()
            .and_then(|g| g.media.get(idx))
            .and_then(|m| m.hls.as_deref())
            .filter(|u| u.starts_with("https://"))
            .map(ToString::to_string);
        let Some(url) = hls else {
            return;
        };
        let source = crate::native_media::NativeMediaSource::classify(&url, None);
        if source.uses_mf_dcomp() {
            let session = crate::native_media::NativeMediaSession::start(
                source,
                self.store_ext.trailer_muted,
                self.store_ext.waker.clone(),
            );
            self.store_ext.trailer_playing = !self.settings.reduce_motion;
            if self.settings.reduce_motion {
                session.pause();
            }
            self.store_ext.trailer_url = Some(url);
            self.store_ext.trailer_mf = Some(Box::new(session));
            return;
        }
        let player = Player::url(url.clone());
        player.set_muted(self.store_ext.trailer_muted);
        if let Some(ref waker) = self.store_ext.waker {
            player.set_waker(waker.clone());
        }
        if !self.settings.reduce_motion {
            player.play();
            self.store_ext.trailer_playing = true;
        } else {
            self.store_ext.trailer_playing = false;
        }
        self.store_ext.trailer_url = Some(url);
        self.store_ext.trailer_player = Some(player);
    }

    pub fn store_toggle_trailer_play(&mut self) {
        if let Some(ref p) = self.store_ext.trailer_player {
            if self.store_ext.trailer_playing {
                p.pause();
                self.store_ext.trailer_playing = false;
            } else {
                p.play();
                self.store_ext.trailer_playing = true;
            }
        } else if let Some(ref mf) = self.store_ext.trailer_mf {
            if self.store_ext.trailer_playing {
                mf.pause();
                self.store_ext.trailer_playing = false;
            } else {
                mf.play();
                self.store_ext.trailer_playing = true;
            }
        }
    }

    pub fn store_toggle_trailer_mute(&mut self) {
        self.store_ext.trailer_muted = !self.store_ext.trailer_muted;
        if let Some(ref p) = self.store_ext.trailer_player {
            p.set_muted(self.store_ext.trailer_muted);
        }
        if let Some(ref mf) = self.store_ext.trailer_mf {
            mf.set_muted(self.store_ext.trailer_muted);
        }
    }

    pub fn store_open_lightbox(&mut self, index: usize) {
        let len = self.store_game.as_ref().map_or(0, |g| g.screenshots.len());
        if len == 0 {
            return;
        }
        self.store_stop_trailer();
        let clamped = index.min(len - 1);
        self.store_ext.shown_media = Some(ShownMedia::Screenshot(clamped));
        self.store_ext.lightbox_index = Some(clamped);
    }

    pub fn store_close_lightbox(&mut self) {
        self.store_ext.lightbox_index = None;
    }

    pub fn store_step_lightbox(&mut self, delta: isize) {
        let len = self.store_game.as_ref().map_or(0, |g| g.screenshots.len());
        if len == 0 {
            return;
        }
        let Some(cur) = self.store_ext.lightbox_index else {
            return;
        };
        let next = (cur as isize + delta).rem_euclid(len as isize) as usize;
        self.store_ext.lightbox_index = Some(next);
        self.store_ext.shown_media = Some(ShownMedia::Screenshot(next));
    }
}

pub fn media_wait() -> El {
    let stage = el()
        .key("sg-media-wait-stage")
        .w_full()
        .aspect_ratio(16.0 / 9.0)
        .radius(RADIUS_CARD)
        .fill(glass_control_fill())
        .border(1.0, float_line());
    let strip = row().w_full().gap(8.0).children((0..8).map(|i| {
        el().key(format!("sg-thumb-wait-{i}"))
            .grow(1.0)
            .min_w(0.0)
            .h(72.0)
            .radius(RADIUS_SMALL)
            .fill(glass_control_fill())
    }));
    col().w_full().gap(10.0).child(stage).child(strip)
}

pub fn media_viewer(s: &AppState, game: &StoreGame, scale: f32) -> El {
    let shown = s
        .store_ext
        .shown_media
        .clone()
        .or_else(|| default_shown_media(game));
    let hue = title_hue(&game.name);
    let flight_id = s
        .cover_flight_id
        .clone()
        .unwrap_or_else(|| format!("store:hero:{}", game.app_id));
    let gamepad = s.input_mode == InputMode::Gamepad;

    let mut stage = el()
        .key(format!("sg-banner-{}", game.app_id))
        .w_full()
        .aspect_ratio(16.0 / 9.0)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD)
        .fill(BG)
        .border(1.0, float_line())
        .matched(flight_id)
        .motion(SPRING_FLIGHT);

    match &shown {
        Some(ShownMedia::Trailer(idx)) => {
            if let Some(m) = game.media.get(*idx) {
                let poster_img = s.remote_image_sized(&m.thumb, 1440.0, scale);
                let player_ref = s.store_ext.trailer_player.as_ref();
                let player_state = player_ref.map(Player::state);
                let failed = matches!(player_state, Some(PlayerState::Failed(_)));

                let trailer_scrim = || {
                    el().pin(Some(0.0), None, Some(0.0), Some(0.0))
                        .h(180.0)
                        .linear_gradient(
                            std::f32::consts::PI,
                            [(0.0, BG.alpha(0.0)), (1.0, BG.alpha(0.76))],
                        )
                };
                if let Some(mf) = s.store_ext.trailer_mf.as_ref()
                    && mf.state() != crate::native_media::NativeMediaState::Failed
                {
                    stage = stage
                        .child(
                            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                                .fill(BG)
                                .native_surface(mf.surface(), SurfaceFit::Fit),
                        )
                        .child(trailer_scrim());
                } else if let Some(player) = player_ref
                    && !failed
                {
                    stage = stage.child(
                        el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                            .fill(BG)
                            .video(player, SurfaceFit::Fit),
                    );
                    let show_poster = !matches!(player_state, Some(PlayerState::Playing))
                        || player.time() <= 0.01;
                    if show_poster && let Some(ref img) = poster_img {
                        stage = stage.child(
                            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                                .pixels_fit(img.pixels(), SurfaceFit::Fill),
                        );
                    }
                    stage = stage.child(trailer_scrim());
                    if matches!(
                        player_state,
                        Some(PlayerState::Playing | PlayerState::Ready | PlayerState::Ended)
                    ) {
                        let playing = s.store_ext.trailer_playing
                            && !matches!(player_state, Some(PlayerState::Ended));
                        let muted = s.store_ext.trailer_muted;
                        let cur_time = player.time();
                        let dur = player.duration().unwrap_or(0.0);
                        let frac = if dur > 0.0 {
                            (cur_time / dur).clamp(0.0, 1.0) as f32
                        } else {
                            0.0
                        };
                        let time_label = if dur > 0.0 {
                            format!(
                                "{} / {}",
                                format_media_time(cur_time),
                                format_media_time(dur)
                            )
                        } else {
                            format_media_time(cur_time)
                        };
                        let controls = row()
                            .key("sg-trailer-controls")
                            .pin(Some(12.0), None, Some(12.0), Some(12.0))
                            .h(40.0)
                            .px(10.0)
                            .gap(10.0)
                            .center_y()
                            .radius(RADIUS_PILL)
                            .liquid_glass(exo_kit::exo_glass(exo_kit::ExoGlass::ClearMedia, true))
                            .child(
                                glass_icon_circle_opt(
                                    "sg-trailer-play",
                                    if playing {
                                        "Pause trailer"
                                    } else {
                                        "Play trailer"
                                    },
                                    28.0,
                                    if playing {
                                        icon_pause(13.0, FG)
                                    } else {
                                        icon_play(13.0, FG)
                                    },
                                    false,
                                )
                                .on_press(|s: &mut AppState| s.store_toggle_trailer_play()),
                            )
                            .child(
                                el().grow(1.0)
                                    .min_w(0.0)
                                    .h(4.0)
                                    .radius(RADIUS_PILL)
                                    .fill(track_fill())
                                    .child(el().h(4.0).w_frac(frac).radius(RADIUS_PILL).fill(FG)),
                            )
                            .child(text(time_label).font(type_small()).color(fg_muted()))
                            .child(
                                glass_icon_circle_opt(
                                    "sg-trailer-mute",
                                    if muted {
                                        "Unmute trailer"
                                    } else {
                                        "Mute trailer"
                                    },
                                    28.0,
                                    if muted {
                                        icon_volume_off(13.0, FG)
                                    } else {
                                        icon_volume(13.0, FG)
                                    },
                                    false,
                                )
                                .on_press(|s: &mut AppState| s.store_toggle_trailer_mute()),
                            );
                        stage = stage.child(controls);
                    }
                } else if let Some(img) = poster_img {
                    stage = stage
                        .pixels_fit(img.pixels(), SurfaceFit::Fill)
                        .child(trailer_scrim());
                } else {
                    stage = stage.linear_gradient(
                        0.35,
                        [(0.0, oklch(0.36, 0.12, hue)), (1.0, oklch(0.10, 0.03, hue))],
                    );
                }
            }
        }
        Some(ShownMedia::Screenshot(idx)) => {
            let shot_idx = *idx;
            if let Some(url) = game.screenshots.get(shot_idx) {
                if let Some(img) = s.remote_image_sized(url, 1440.0, scale) {
                    stage = stage.pixels_fit(img.pixels(), SurfaceFit::Fill);
                } else {
                    stage = stage.linear_gradient(
                        0.35,
                        [(0.0, oklch(0.36, 0.12, hue)), (1.0, oklch(0.10, 0.03, hue))],
                    );
                }
                let expand_badge = el()
                    .pin(None, None, Some(12.0), Some(12.0))
                    .size(32.0, 32.0)
                    .radius(RADIUS_PILL)
                    .center()
                    .liquid_glass(exo_kit::exo_glass(exo_kit::ExoGlass::ClearMedia, true))
                    .child(icon_maximize(14.0, FG));
                stage = stage
                    .role("button")
                    .label("View screenshot fullscreen")
                    .focusable(true)
                    .hit()
                    .cursor(Cursor::Pointer)
                    .press_on_down()
                    .on_press(move |s: &mut AppState| s.store_open_lightbox(shot_idx))
                    .child(expand_badge);
                if gamepad {
                    stage = stage.focus_ring(2.0, 3.0, FG);
                }
            }
        }
        None => {
            let fallback_url = game
                .header
                .as_deref()
                .or_else(|| game.art.as_ref().and_then(StoreArt::best_hero))
                .map(ToString::to_string)
                .unwrap_or_else(|| StoreArt::steam_url(&game.app_id, "hero"));
            if let Some(img) = s.remote_image_sized(&fallback_url, 1440.0, scale) {
                stage = stage.pixels_fit(img.pixels(), SurfaceFit::Fill);
            } else {
                stage = stage.linear_gradient(
                    0.35,
                    [(0.0, oklch(0.42, 0.15, hue)), (1.0, oklch(0.11, 0.03, hue))],
                );
            }
        }
    }

    let mut thumbs: Vec<El> = Vec::new();
    for (i, m) in game.media.iter().enumerate() {
        if m.hls.as_deref().is_none_or(str::is_empty) {
            continue;
        }
        let selected = matches!(shown, Some(ShownMedia::Trailer(cur)) if cur == i);
        let thumb_img = s.remote_image_sized(&m.thumb, 320.0, scale);
        let mut btn = el()
            .key(format!("sg-thumb-trailer-{i}"))
            .role("option")
            .label(format!("Trailer {}", i + 1))
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .size(144.0, 81.0)
            .radius(RADIUS_SMALL)
            .continuous_corners()
            .clip_rounded(RADIUS_SMALL)
            .fill(BG)
            .opacity(if selected { 1.0 } else { 0.45 })
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| {
                s.input_mode = InputMode::Pointer;
                s.store_select_media(ShownMedia::Trailer(i));
            });
        if let Some(img) = thumb_img {
            btn = btn.pixels_fit(img.pixels(), SurfaceFit::Fill);
        } else {
            btn = btn.linear_gradient(
                0.35,
                [(0.0, oklch(0.32, 0.10, hue)), (1.0, oklch(0.10, 0.03, hue))],
            );
        }
        btn = btn.child(
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .center()
                .child(
                    el().size(28.0, 28.0)
                        .radius(RADIUS_PILL)
                        .center()
                        .fill(BG.alpha(0.58))
                        .child(icon_play(14.0, FG)),
                ),
        );
        if selected {
            btn = btn.border(1.0, glass_rim());
        }
        if gamepad {
            btn = btn.focus_ring(2.0, 3.0, FG);
        }
        thumbs.push(btn);
    }

    for (i, shot) in game.screenshots.iter().enumerate() {
        let selected = matches!(shown, Some(ShownMedia::Screenshot(cur)) if cur == i);
        let small = thumb_of_screenshot(shot);
        let thumb_img = s
            .remote_image_sized(&small, 320.0, scale)
            .or_else(|| s.remote_image_sized(shot, 320.0, scale));
        let mut btn = el()
            .key(format!("sg-thumb-shot-{i}"))
            .role("option")
            .label(format!("Screenshot {}", i + 1))
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .size(144.0, 81.0)
            .radius(RADIUS_SMALL)
            .continuous_corners()
            .clip_rounded(RADIUS_SMALL)
            .fill(BG)
            .opacity(if selected { 1.0 } else { 0.45 })
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| {
                s.input_mode = InputMode::Pointer;
                s.store_select_media(ShownMedia::Screenshot(i));
            });
        if let Some(img) = thumb_img {
            btn = btn.pixels_fit(img.pixels(), SurfaceFit::Fill);
        } else {
            btn = btn.linear_gradient(
                0.35,
                [(0.0, oklch(0.32, 0.10, hue)), (1.0, oklch(0.10, 0.03, hue))],
            );
        }
        if selected {
            btn = btn.border(1.0, glass_rim());
        }
        if gamepad {
            btn = btn.focus_ring(2.0, 3.0, FG);
        }
        thumbs.push(btn);
    }

    let mut wrap = col().w_full().gap(12.0).child(stage);
    if !thumbs.is_empty() {
        wrap = wrap.child(
            row()
                .key("sg-media-strip")
                .w_full()
                .gap(8.0)
                .scroll_x()
                .children(thumbs),
        );
    }
    wrap
}

pub fn lightbox_layers(s: &AppState, scale: f32, blur: bool) -> Vec<El> {
    let Some(idx) = s.store_ext.lightbox_index else {
        return Vec::new();
    };
    let Some(ref game) = s.store_game else {
        return Vec::new();
    };
    let Some(url) = game.screenshots.get(idx) else {
        return Vec::new();
    };
    let total = game.screenshots.len();
    let img = s.remote_image_sized(url, 1920.0, scale);

    let scrim = el()
        .key("store-lightbox-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(scrim_fill())
        .hit()
        .transition(modal_scrim_transition())
        .on_press(|s: &mut AppState| s.store_close_lightbox());

    let mut image_box = el()
        .key(format!("store-lightbox-img-{idx}"))
        .w(1520.0)
        .h(855.0)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD)
        .fill(BG)
        .border(1.0, glass_rim())
        .transition(modal_card_transition());
    if let Some(loaded) = img {
        image_box = image_box.pixels_fit(loaded.pixels(), SurfaceFit::Fit);
    } else {
        image_box = image_box.center().child(
            text("Loading screenshot…")
                .font(type_control())
                .color(fg_muted()),
        );
    }

    let counter = el()
        .key("store-lightbox-counter")
        .pin(Some(24.0), Some(64.0), None, None)
        .h(34.0)
        .px(14.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(
            text(format!("{} / {}", idx + 1, total))
                .font(type_control())
                .color(FG),
        );

    let close_btn = glass_icon_circle_opt(
        "store-lightbox-close",
        "Close screenshot viewer",
        38.0,
        icon_close(16.0, FG),
        blur,
    )
    .pin(None, Some(64.0), Some(24.0), None)
    .on_press(|s: &mut AppState| s.store_close_lightbox());

    let mut modal = el()
        .key("store-lightbox-stage")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(image_box)
        .child(counter)
        .child(close_btn);

    if total > 1 {
        let prev = el()
            .key("store-lightbox-prev")
            .role("button")
            .label("Previous screenshot")
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .pin(Some(24.0), Some(516.0), None, None)
            .size(44.0, 44.0)
            .radius(RADIUS_PILL)
            .center()
            .fill(glass_control_fill())
            .hover(hover_fill())
            .press(pressed_fill())
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .motion(SPRING_MOVE)
            .on_press(|s: &mut AppState| s.store_step_lightbox(-1))
            .child(icon_chevron_left(20.0, FG));
        let next = el()
            .key("store-lightbox-next")
            .role("button")
            .label("Next screenshot")
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .pin(None, Some(516.0), Some(24.0), None)
            .size(44.0, 44.0)
            .radius(RADIUS_PILL)
            .center()
            .fill(glass_control_fill())
            .hover(hover_fill())
            .press(pressed_fill())
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .motion(SPRING_MOVE)
            .on_press(|s: &mut AppState| s.store_step_lightbox(1))
            .child(icon_chevron_right(20.0, FG));
        modal = modal.child(prev).child(next);
    }

    vec![scrim, modal]
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::StoreMedia;

    #[test]
    fn thumb_of_screenshot_rewrites_full_hd_suffix() {
        assert_eq!(
            thumb_of_screenshot(
                "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1091500/ss_abc.1920x1080.jpg?t=1"
            ),
            "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1091500/ss_abc.600x338.jpg?t=1"
        );
        assert_eq!(
            thumb_of_screenshot("https://shared.fastly.steamstatic.com/other.jpg"),
            "https://shared.fastly.steamstatic.com/other.jpg"
        );
    }

    #[test]
    fn default_shown_media_prefers_first_trailer_with_hls() {
        let game = StoreGame {
            media: vec![
                StoreMedia {
                    thumb: "t0".into(),
                    hls: None,
                    av1: None,
                },
                StoreMedia {
                    thumb: "t1".into(),
                    hls: Some("https://video.fastly.steamstatic.com/a.m3u8".into()),
                    av1: None,
                },
            ],
            screenshots: vec!["s0".into()],
            ..Default::default()
        };
        assert_eq!(default_shown_media(&game), Some(ShownMedia::Trailer(1)));

        let shots_only = StoreGame {
            screenshots: vec!["s0".into()],
            ..Default::default()
        };
        assert_eq!(
            default_shown_media(&shots_only),
            Some(ShownMedia::Screenshot(0))
        );
        assert_eq!(default_shown_media(&StoreGame::default()), None);
        assert_eq!(format_media_time(75.2), "1:15");
    }

    #[test]
    fn lightbox_steps_and_wraps() {
        let mut s = AppState::with_demo_games();
        s.store_game = Some(StoreGame {
            ok: true,
            app_id: "1091500".into(),
            screenshots: vec!["a.jpg".into(), "b.jpg".into(), "c.jpg".into()],
            ..Default::default()
        });
        s.store_open_lightbox(0);
        assert_eq!(s.store_ext.lightbox_index, Some(0));
        s.store_step_lightbox(-1);
        assert_eq!(s.store_ext.lightbox_index, Some(2));
        s.store_step_lightbox(1);
        assert_eq!(s.store_ext.lightbox_index, Some(0));
        s.store_close_lightbox();
        assert_eq!(s.store_ext.lightbox_index, None);
    }
}
