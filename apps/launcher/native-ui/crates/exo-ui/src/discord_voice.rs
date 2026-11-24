use crate::discord::avatar_box;
use crate::discord_chat;
use crate::discord_state::DiscordSettingsPage;
use crate::discord_voice_state::{GoLiveTab, VoicePanel};
use crate::state::{AppState, ContextMenuTarget, Room};
use exo_kit::{
    BG, FG, GLASS_CONTROL_SIGMA, GLASS_PANEL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD,
    RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL, SPRING_MOVE, SPRING_QUICK, bad, fg_faint, fg_muted,
    filter_pill, glass_button, glass_control_fill, glass_panel_fill, glass_rim_bottom,
    glass_rim_top, glass_still_control_fill, glass_still_panel_fill, good, hairline, hover_fill,
    icon_app_window, icon_chat, icon_chevron_left, icon_close, icon_hand, icon_headphones,
    icon_headphones_off, icon_lock, icon_maximize, icon_mic, icon_mic_off, icon_monitor,
    icon_music, icon_phone, icon_phone_off, icon_screen_share, icon_search, icon_settings,
    icon_video, icon_volume, icon_volume_off, pressed_fill, primary_button, subtle_fill, type_body,
    type_control, type_label, type_section, type_small, type_title, warn,
};
use exo_rpc::{
    DiscordChannel, DiscordGoLiveStatsEvent, DiscordSoundItem, DiscordVoiceMember, GoLiveSource,
};
use rune::{
    Color, Cursor, El, ScrollSpec, SpringPair, SurfaceFit, TextEdit, Transition, col, edit_access,
    el, row, text, text_input,
};
use serde_json::json;

pub fn fit_tiles(width: f32, height: f32, count: usize, gap: f32) -> f32 {
    if count == 0 || width <= 0.0 || height <= 0.0 {
        return 320.0;
    }
    let mut best = 0.0_f32;
    for cols in 1..=count {
        let rows = count.div_ceil(cols);
        let cw = (width - gap * (cols.saturating_sub(1) as f32)) / (cols as f32);
        let ch = (height - gap * (rows.saturating_sub(1) as f32)) / (rows as f32) * (16.0 / 9.0);
        best = best.max(cw.min(ch));
    }
    best.min(1280.0).floor().max(120.0)
}

fn format_golive_stats(st: &DiscordGoLiveStatsEvent) -> String {
    let codec = st
        .codec
        .as_deref()
        .filter(|c| !c.is_empty())
        .map(|c| format!("{c} · "))
        .unwrap_or_default();
    let rate = if let Some(max) = st.max.filter(|&m| m > 0.0) {
        format!("up to {:.1} Mbps", max / 1_000_000.0)
    } else {
        format!("{:.1} Mbps", st.bitrate / 1_000_000.0)
    };
    let sound = st
        .sound
        .as_deref()
        .filter(|x| !x.is_empty())
        .map(|x| format!(" · {x}"))
        .unwrap_or_default();
    format!(
        "{}×{} · {:.0} fps · {codec}{rate}{sound}",
        st.width, st.height, st.fps
    )
}

fn screen_meta(src: &GoLiveSource) -> Option<String> {
    if src.kind != "screen" {
        return None;
    }
    let mut parts = Vec::new();
    if src.width > 0 && src.height > 0 {
        parts.push(format!("{}×{}", src.width, src.height));
    }
    if src.hz > 0 {
        parts.push(format!("{} Hz", src.hz));
    }
    if src.primary {
        parts.push("Primary".into());
    }
    (!parts.is_empty()).then(|| parts.join(" · "))
}

fn overlay_transition() -> Transition {
    let enter = Transition::offset(0.0, 10.0)
        .combined(Transition::scale(0.97))
        .combined(Transition::opacity());
    let exit = Transition::offset(0.0, 6.0)
        .combined(Transition::scale(0.98))
        .combined(Transition::opacity());
    Transition::asymmetric(enter, exit).springs(SpringPair::new(SPRING_MOVE, SPRING_QUICK))
}

/// A call control with its word beside the icon, as the call bar draws Mute, Deafen, Camera, Share, Soundboard, Leave.
fn bar_word_btn(
    key: &'static str,
    word: &'static str,
    label: &'static str,
    active: bool,
    danger: bool,
    icon: El,
    width: f32,
) -> El {
    let (fill, hover) = if danger {
        (bad().alpha(0.24), bad().alpha(0.36))
    } else if active {
        (FG, FG.alpha(0.92))
    } else {
        (glass_still_control_fill(), hover_fill())
    };
    row()
        .key(key)
        .role("button")
        .label(label)
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(40.0)
        .w(width)
        .gap(8.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(fill)
        .hover(hover)
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .child(icon)
        .child(text(word).font(type_control()).color(if active && !danger {
            PRIMARY_FG
        } else {
            FG
        }))
}

/// The call bar's width and the Share button's offset inside it, as React lays the bar out at 1920 px.
const CALL_BAR_W: f32 = 696.0;
const SHARE_OFFSET_IN_BAR: f32 = 340.0;

/// A 1 px divider inside a 4 px slot, so the call bar's groups keep the React spacing.
fn bar_divider() -> El {
    row()
        .w(4.0)
        .h(24.0)
        .center()
        .child(el().w(1.0).h(24.0).fill(hairline()))
}

fn dock_action_btn(key: &'static str, label: &'static str, active: bool, icon: El) -> El {
    row()
        .key(key)
        .role("button")
        .label(label)
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .grow(1.0)
        .h(30.0)
        .gap(5.0)
        .center()
        .radius(RADIUS_SMALL)
        .fill(if active { FG } else { Color::TRANSPARENT })
        .border_gradient(
            1.0,
            if active { FG } else { glass_rim_top() },
            if active { FG } else { glass_rim_bottom() },
        )
        .hover(if active { FG.alpha(0.92) } else { hover_fill() })
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .child(icon)
}

fn switch_pill(key: &'static str, label: &'static str, on: bool) -> El {
    row()
        .key(key)
        .role("switch")
        .label(label)
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .w(38.0)
        .h(22.0)
        .px(2.0)
        .center_y()
        .justify_between()
        .radius(RADIUS_PILL)
        .fill(if on { good() } else { subtle_fill() })
        .border(1.0, if on { good() } else { hairline() })
        .motion(SPRING_QUICK)
        .child(if on { el().grow(1.0) } else { el() })
        .child(el().size(16.0, 16.0).radius(8.0).fill(FG))
        .child(if on { el() } else { el().grow(1.0) })
}

fn stream_theater(s: &AppState, user_id: &str, members: &[DiscordVoiceMember]) -> El {
    let streamer_name = members
        .iter()
        .find(|m| m.id == user_id)
        .map(|m| m.name.clone())
        .unwrap_or_else(|| "Stream".into());

    let feed = s.discord.voice_ui.stream_pixels_for(user_id);
    let mix = s
        .discord
        .voice_ui
        .user_mix
        .get(user_id)
        .cloned()
        .unwrap_or_default();
    let uid_mute = user_id.to_string();

    let fs = s.discord.voice_ui.fullscreen_stream;
    let stage_h = if fs { 520.0 } else { 360.0 };
    let (surface, telemetry) = if let Some((pixels, stats)) = feed {
        let codec_label = if stats.codec.eq_ignore_ascii_case("h265") {
            "H.265 HW"
        } else if stats.hardware {
            "H.264 HW"
        } else {
            "H.264"
        };
        let meta = if stats.width > 0 && stats.height > 0 {
            format!(
                "{}×{} · {:.0} fps · {codec_label}",
                stats.width,
                stats.height,
                stats.fps.max(1.0)
            )
        } else {
            format!("Decoding {codec_label}…")
        };
        (
            el().w_full()
                .h(stage_h)
                .radius(RADIUS_PANEL)
                .clip_rounded(RADIUS_PANEL)
                .fill(BG)
                .pixels_fit(&pixels, SurfaceFit::Fit),
            meta,
        )
    } else {
        (
            col()
                .w_full()
                .h(stage_h)
                .radius(RADIUS_PANEL)
                .fill(BG)
                .center()
                .gap(10.0)
                .child(
                    el().size(52.0, 52.0)
                        .radius(26.0)
                        .border(1.0, hairline())
                        .center()
                        .child(icon_screen_share(22.0, fg_muted())),
                )
                .child(
                    text(format!("Waiting for {streamer_name}'s stream…"))
                        .font(type_body())
                        .color(fg_muted()),
                ),
            "Connecting stream…".into(),
        )
    };

    let overlay_top = row()
        .w_full()
        .px(14.0)
        .py(10.0)
        .justify_between()
        .center_y()
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    el().px(8.0)
                        .py(3.0)
                        .radius(RADIUS_PILL)
                        .fill(bad().alpha(0.24))
                        .border(1.0, bad().alpha(0.45))
                        .child(text("LIVE").font(type_label()).color(bad())),
                )
                .child(text(streamer_name).font(type_section()).color(FG))
                .child(text(telemetry).font(type_label()).color(fg_muted())),
        )
        .child(
            row()
                .gap(6.0)
                .center_y()
                .child(
                    glass_button(
                        "dc-stream-mute",
                        if mix.stream_muted {
                            "Unmute stream"
                        } else {
                            "Mute stream"
                        },
                        Some(if mix.stream_muted {
                            icon_volume_off(13.0, bad())
                        } else {
                            icon_volume(13.0, FG)
                        }),
                    )
                    .on_press(move |s: &mut AppState| {
                        s.set_discord_user_mix(&uid_mute, None, None, Some(!mix.stream_muted))
                    }),
                )
                .child(
                    glass_button(
                        "dc-stream-fs",
                        if fs { "Compact" } else { "Expand" },
                        Some(icon_maximize(13.0, FG)),
                    )
                    .on_press(|s: &mut AppState| {
                        s.discord.voice_ui.fullscreen_stream = !s.discord.voice_ui.fullscreen_stream
                    }),
                )
                .child(
                    glass_button(
                        "dc-stream-stop",
                        "Stop watching",
                        Some(icon_close(13.0, FG)),
                    )
                    .on_press(|s: &mut AppState| s.stop_watching_discord_stream()),
                ),
        );

    col()
        .key("dc-stream-theater")
        .w_full()
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(overlay_top)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(el().w_full().padding(10.0).child(surface))
}

fn participant_tile(
    s: &AppState,
    guild_id: Option<&str>,
    m: &DiscordVoiceMember,
    compact: bool,
    tile_w: f32,
    can_demote: Option<(String, String)>,
    scale: f32,
) -> El {
    let muted_by_you = s
        .discord
        .voice_ui
        .user_mix
        .get(&m.id)
        .is_some_and(|mix| mix.muted);
    let speaking = s.discord.speaking.contains(&m.id) && !muted_by_you;
    let is_watching = s.discord.voice_ui.watching_stream.as_deref() == Some(m.id.as_str());
    let uid_watch = m.id.clone();
    let ctx_target = ContextMenuTarget::DiscordMember {
        guild_id: guild_id.map(str::to_string),
        user_id: m.id.clone(),
        name: m.name.clone(),
        username: None,
        in_voice: true,
    };

    let (w, h) = if compact {
        (216.0, 122.0)
    } else {
        (tile_w, (tile_w * 9.0 / 16.0).round())
    };
    let av_size = if compact {
        46.0
    } else if w >= 600.0 {
        112.0
    } else {
        72.0
    };

    let mut tile = el()
        .key(format!("dc-stage-tile-{}", m.id))
        .size(w, h)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .fill(BG)
        .motion(SPRING_QUICK)
        .hit()
        .cursor(Cursor::Pointer)
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.open_context_menu(ctx_target.clone(), p.x, p.y);
        });

    if let Some(av_url) = m.avatar.as_deref()
        && let Some(img) = s.discord_remote_image_sized(av_url, 128.0, scale)
    {
        tile = tile
            .child(
                el().pin(Some(-24.0), Some(-24.0), Some(-24.0), Some(-24.0))
                    .pixels_fit(img.pixels(), SurfaceFit::Fill)
                    .blur(36.0)
                    .opacity(0.42),
            )
            .child(
                el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                    .fill(BG.alpha(0.48)),
            );
    }

    tile = tile.border_gradient(1.0, glass_rim_top(), glass_rim_bottom());

    let mut center_col = col().w_full().h_full().center().gap(10.0).child(avatar_box(
        s,
        m.avatar.as_deref(),
        &m.name,
        av_size,
        av_size * 0.5,
        None,
        speaking,
        scale,
    ));

    if m.stream {
        center_col = center_col.child(
            row()
                .key(format!("dc-watch-{}", m.id))
                .role("button")
                .label(if is_watching {
                    "Stop watching stream"
                } else {
                    "Watch stream"
                })
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .h(28.0)
                .px(12.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(if is_watching {
                    good().alpha(0.20)
                } else {
                    glass_still_control_fill()
                })
                .border_gradient(
                    1.0,
                    if is_watching {
                        good().alpha(0.55)
                    } else {
                        glass_rim_top()
                    },
                    if is_watching {
                        good().alpha(0.35)
                    } else {
                        glass_rim_bottom()
                    },
                )
                .hover(hover_fill())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    if is_watching {
                        s.stop_watching_discord_stream();
                    } else {
                        s.watch_discord_stream(&uid_watch);
                    }
                })
                .child(icon_screen_share(
                    12.0,
                    if is_watching { good() } else { FG },
                ))
                .child(
                    text(if is_watching {
                        "Watching"
                    } else {
                        "Watch stream"
                    })
                    .font(type_label())
                    .color(if is_watching { good() } else { FG }),
                ),
        );
    }

    if let Some((gid_d, cid_d)) = can_demote {
        let uid_d = m.id.clone();
        center_col = center_col.child(
            glass_button("dc-demote", "Move to audience", None)
                .key(format!("dc-demote-{}", m.id))
                .on_press(move |s: &mut AppState| {
                    s.set_discord_stage_state(&gid_d, &cid_d, Some(&uid_d), Some(true), None);
                }),
        );
    }

    tile = tile.child(center_col);

    if m.stream {
        tile = tile.child(
            row()
                .pin(None, Some(10.0), Some(10.0), None)
                .px(8.0)
                .py(3.0)
                .radius(RADIUS_PILL)
                .fill(bad())
                .child(text("LIVE").font(type_label()).color(FG)),
        );
    }

    let mut badge = row()
        .pin(Some(14.0), None, None, Some(12.0))
        .h(22.0)
        .px(8.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(BG.alpha(0.60))
        .child(text(m.name.clone()).font(type_small()).color(FG).truncate());

    if m.deaf || m.server_deaf {
        badge = badge.child(icon_headphones_off(12.0, bad()));
    } else if m.mute || m.server_mute || muted_by_you {
        badge = badge.child(icon_mic_off(12.0, warn()));
    }

    tile = tile.child(badge);

    if speaking {
        tile = tile.child(
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .radius(RADIUS_PANEL)
                .border(2.0, good()),
        );
    }

    tile
}

fn my_stream_tile(s: &AppState, members: &[DiscordVoiceMember], tile_w: f32, scale: f32) -> El {
    let tile_h = (tile_w * 9.0 / 16.0).round();
    let title = s
        .discord
        .voice_ui
        .golive_title
        .clone()
        .unwrap_or_else(|| "Your stream".into());
    let stats_line = s
        .discord
        .voice_ui
        .golive_stats
        .as_ref()
        .map(format_golive_stats);
    let viewers: Vec<&DiscordVoiceMember> = s
        .discord
        .voice_ui
        .golive_viewers
        .iter()
        .filter_map(|id| members.iter().find(|m| &m.id == id))
        .collect();

    let mut tile = el()
        .key("dc-my-stream-tile")
        .size(tile_w, tile_h)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, good().alpha(0.45));

    if let Some(me_id) = s.discord.home.me.as_ref().map(|m| m.id.as_str())
        && let Some((px, _)) = s.discord.voice_ui.stream_pixels_for(me_id)
    {
        tile = tile.child(
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .pixels_fit(&px, SurfaceFit::Fit),
        );
    } else {
        tile = tile.child(
            col()
                .w_full()
                .h_full()
                .center()
                .gap(8.0)
                .child(icon_screen_share(28.0, good()))
                .child(text(title.clone()).font(type_section()).color(FG)),
        );
    }

    tile = tile.child(
        row()
            .pin(None, Some(10.0), Some(10.0), None)
            .gap(6.0)
            .child(
                glass_button("dc-mystream-hide", "Hide preview", None).on_press(
                    |s: &mut AppState| {
                        s.discord.voice_ui.golive_preview_shown = false;
                    },
                ),
            )
            .child(
                glass_button(
                    "dc-mystream-stop",
                    "Stop streaming",
                    Some(icon_close(12.0, bad())),
                )
                .on_press(|s: &mut AppState| s.stop_discord_golive()),
            ),
    );

    let badge_text = match stats_line {
        Some(st) => format!("{title} · {st}"),
        None => title,
    };
    tile = tile.child(
        row()
            .pin(Some(12.0), None, None, Some(10.0))
            .px(10.0)
            .py(4.0)
            .radius(RADIUS_PILL)
            .fill(BG.alpha(0.72))
            .child(text(badge_text).font(type_label()).color(FG).truncate()),
    );

    if !viewers.is_empty() {
        let mut vrow = row()
            .pin(None, None, Some(12.0), Some(10.0))
            .px(8.0)
            .py(4.0)
            .gap(4.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(BG.alpha(0.72));
        for v in viewers.iter().take(3) {
            vrow = vrow.child(avatar_box(
                s,
                v.avatar.as_deref(),
                &v.name,
                20.0,
                10.0,
                None,
                false,
                scale,
            ));
        }
        if viewers.len() > 3 {
            vrow = vrow.child(
                text(format!("+{}", viewers.len() - 3))
                    .font(type_label())
                    .color(fg_muted()),
            );
        }
        tile = tile.child(vrow);
    }

    tile
}

fn golive_modal(s: &AppState, blur: bool) -> El {
    let mut modal = col()
        .key("dc-golive-panel")
        .w(900.0)
        .h(620.0)
        .padding(20.0)
        .gap(14.0)
        .radius(22.0)
        .fill(if blur {
            glass_panel_fill()
        } else {
            glass_still_panel_fill()
        })
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .transition(overlay_transition())
        .hit();
    if blur {
        modal = modal.backdrop_blur(GLASS_PANEL_SIGMA);
    }

    let close = el()
        .key("dc-gl-close")
        .role("button")
        .label("Close")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .size(30.0, 30.0)
        .radius(RADIUS_PILL)
        .hover(hover_fill())
        .center()
        .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::None))
        .child(icon_close(15.0, fg_muted()));

    if let Some(chosen) = s.discord.voice_ui.golive_step_chosen.as_ref() {
        let title = chosen.game.as_deref().unwrap_or(&chosen.title).to_string();
        let cur_h = s.discord.voice_ui.golive_height;
        let max_fps = if chosen.hz >= 15 { chosen.hz + 1 } else { 240 };
        let rates: Vec<i32> = [15, 30, 60, 120]
            .into_iter()
            .filter(|&r| r <= max_fps)
            .collect();
        let cur_fps = s
            .discord
            .voice_ui
            .golive_fps
            .min(*rates.last().unwrap_or(&60));
        let sound_on = s.discord.voice_ui.golive_sound;
        let res_note = if cur_h > 0 && chosen.height > 0 && cur_h > chosen.height {
            let what = if chosen.kind == "screen" {
                "screen"
            } else {
                "window"
            };
            Some(format!(
                "Your {what} is {}p, so this streams at {}p.",
                chosen.height, chosen.height
            ))
        } else {
            None
        };
        let sound_desc = if chosen.kind == "window" {
            format!(
                "Sends {}'s sound only.",
                chosen
                    .app
                    .as_deref()
                    .filter(|a| !a.is_empty())
                    .unwrap_or("this app")
            )
        } else {
            "Sends everything your PC plays, except Exo and the call.".into()
        };

        let head = row()
            .w_full()
            .gap(10.0)
            .center_y()
            .child(
                el().key("dc-gl-back")
                    .role("button")
                    .label("Pick something else")
                    .cursor(Cursor::Pointer)
                    .hit()
                    .size(30.0, 30.0)
                    .radius(RADIUS_PILL)
                    .hover(hover_fill())
                    .center()
                    .on_press(|s: &mut AppState| s.discord.voice_ui.golive_step_chosen = None)
                    .child(icon_chevron_left(16.0, FG)),
            )
            .child(if chosen.kind == "screen" {
                icon_monitor(18.0, FG)
            } else {
                icon_app_window(18.0, FG)
            })
            .child(
                text(title)
                    .font(type_section())
                    .color(FG)
                    .grow(1.0)
                    .truncate(),
            )
            .child(close);

        let mut res_row = row().gap(6.0).wrap();
        for (h, lbl) in [
            (720, "720p"),
            (1080, "1080p"),
            (1440, "1440p"),
            (0, "Source"),
        ] {
            res_row = res_row.child(
                filter_pill(format!("dc-gl-res-{h}"), lbl, cur_h == h)
                    .on_press(move |s: &mut AppState| s.discord.voice_ui.golive_height = h),
            );
        }

        let mut fps_row = row().gap(6.0).wrap();
        for r in rates {
            fps_row = fps_row.child(
                filter_pill(format!("dc-gl-fps-{r}"), format!("{r} fps"), cur_fps == r)
                    .on_press(move |s: &mut AppState| s.discord.voice_ui.golive_fps = r),
            );
        }

        let preview_box = el()
            .grow(1.0)
            .h(360.0)
            .radius(RADIUS_CARD)
            .fill(BG)
            .border(1.0, hairline())
            .center()
            .child(
                col()
                    .center()
                    .gap(8.0)
                    .child(if chosen.kind == "screen" {
                        icon_monitor(36.0, fg_muted())
                    } else {
                        icon_app_window(36.0, fg_muted())
                    })
                    .child(
                        text(chosen.title.clone())
                            .font(type_body())
                            .color(fg_muted()),
                    )
                    .children(
                        screen_meta(chosen).map(|m| text(m).font(type_label()).color(fg_faint())),
                    ),
            );

        let options = col()
            .w(340.0)
            .gap(14.0)
            .child(
                col()
                    .gap(6.0)
                    .child(text("Resolution").font(type_control()).color(FG))
                    .child(res_row)
                    .children(res_note.map(|n| text(n).font(type_label()).color(fg_muted()))),
            )
            .child(
                col()
                    .gap(6.0)
                    .child(text("Frame rate").font(type_control()).color(FG))
                    .child(fps_row),
            )
            .child(
                row()
                    .w_full()
                    .py(6.0)
                    .justify_between()
                    .center_y()
                    .gap(12.0)
                    .child(
                        col()
                            .grow(1.0)
                            .gap(2.0)
                            .child(text("Stream sound").font(type_control()).color(FG))
                            .child(text(sound_desc).font(type_label()).color(fg_muted())),
                    )
                    .child(
                        switch_pill("dc-gl-sound", "Stream sound", sound_on).on_press(
                            |s: &mut AppState| {
                                s.discord.voice_ui.golive_sound = !s.discord.voice_ui.golive_sound;
                            },
                        ),
                    ),
            )
            .children(
                s.discord
                    .voice_ui
                    .golive_error
                    .as_ref()
                    .map(|e| text(e.clone()).font(type_small()).color(bad())),
            )
            .child(
                primary_button(
                    "dc-gl-start",
                    "Go Live",
                    Some(icon_screen_share(14.0, PRIMARY_FG)),
                )
                .on_press(|s: &mut AppState| {
                    s.discord.voice_ui.golive_preview_shown = true;
                    s.start_discord_golive();
                }),
            );

        return modal.child(head).child(
            row()
                .w_full()
                .grow(1.0)
                .gap(18.0)
                .child(preview_box)
                .child(options),
        );
    }

    let tab = s.discord.voice_ui.golive_tab;
    let tabs = row()
        .w_full()
        .gap(6.0)
        .center_y()
        .child(
            filter_pill(
                "dc-gl-tab-apps",
                "Applications",
                tab == GoLiveTab::Applications,
            )
            .on_press(|s: &mut AppState| s.discord.voice_ui.golive_tab = GoLiveTab::Applications),
        )
        .child(
            filter_pill("dc-gl-tab-screens", "Screens", tab == GoLiveTab::Screens)
                .on_press(|s: &mut AppState| s.discord.voice_ui.golive_tab = GoLiveTab::Screens),
        )
        .child(el().grow(1.0))
        .child(
            glass_button("dc-gl-refresh", "Refresh", None)
                .on_press(|s: &mut AppState| s.load_golive_sources()),
        )
        .child(close);

    let game_src = s
        .discord
        .voice_ui
        .golive_sources
        .iter()
        .find(|src| src.game.is_some());
    let want_kind = match tab {
        GoLiveTab::Applications => "window",
        GoLiveTab::Screens => "screen",
    };
    let filtered: Vec<&GoLiveSource> = s
        .discord
        .voice_ui
        .golive_sources
        .iter()
        .filter(|src| {
            src.kind == want_kind
                && !(tab == GoLiveTab::Applications && game_src.is_some_and(|g| g.id == src.id))
        })
        .collect();

    let mut body = col().w_full().gap(12.0);
    if s.discord.voice_ui.golive_service_disabled
        || s.discord.voice_ui.golive_capture_mode.as_deref() == Some("dxgi")
    {
        body = body.child(
            text(if s.discord.voice_ui.golive_service_disabled {
                "Using screen compatibility mode (Windows Capture Service is disabled)."
            } else {
                "Using screen compatibility mode."
            })
            .font(type_label())
            .color(fg_muted()),
        );
    }

    if tab == GoLiveTab::Applications
        && let Some(game) = game_src
    {
        let g_clone = game.clone();
        let g_name = game.game.clone().unwrap_or_else(|| game.title.clone());
        body = body.child(
            row()
                .key("dc-gl-game-banner")
                .role("button")
                .label(format!("Stream {g_name}"))
                .cursor(Cursor::Pointer)
                .hit()
                .w_full()
                .padding(12.0)
                .gap(12.0)
                .center_y()
                .radius(RADIUS_CARD)
                .fill(BG.alpha(0.60))
                .border(1.0, hairline())
                .hover(hover_fill())
                .on_press(move |s: &mut AppState| s.pick_golive_source(g_clone.clone()))
                .child(icon_app_window(22.0, FG))
                .child(
                    col()
                        .grow(1.0)
                        .gap(2.0)
                        .child(text("Game").font(type_label()).color(fg_muted()))
                        .child(text(g_name).font(type_section()).color(FG)),
                )
                .child(
                    row()
                        .h(28.0)
                        .px(12.0)
                        .center()
                        .radius(RADIUS_PILL)
                        .fill(FG)
                        .child(text("Stream").font(type_control()).color(PRIMARY_FG)),
                ),
        );
    }

    let mut grid = row().w_full().gap(10.0).flex_wrap();
    if s.discord.voice_ui.golive_loading && s.discord.voice_ui.golive_sources.is_empty() {
        grid = grid.child(
            el().w_full().py(28.0).center().child(
                text("Finding your windows…")
                    .font(type_small())
                    .color(fg_muted()),
            ),
        );
    } else if filtered.is_empty() {
        grid = grid.child(
            el().w_full().py(28.0).center().child(
                text(s.discord.voice_ui.golive_error.as_deref().unwrap_or(
                    if tab == GoLiveTab::Applications {
                        if game_src.is_some() {
                            "No other windows to share."
                        } else {
                            "No windows to share."
                        }
                    } else {
                        "No screens found."
                    },
                ))
                .font(type_small())
                .color(fg_muted()),
            ),
        );
    } else {
        for src in filtered {
            let src_clone = src.clone();
            let meta = screen_meta(src);
            let card = col()
                .key(format!("dc-gl-src-{}", src.id))
                .role("button")
                .label(format!("Share {}", src.title))
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .w(204.0)
                .padding(10.0)
                .gap(8.0)
                .radius(RADIUS_CARD)
                .fill(BG.alpha(0.56))
                .border(1.0, hairline())
                .hover(hover_fill())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| s.pick_golive_source(src_clone.clone()))
                .child(
                    el().w_full()
                        .h(96.0)
                        .radius(RADIUS_SMALL)
                        .fill(BG)
                        .border(1.0, hairline())
                        .center()
                        .child(if src.kind == "screen" {
                            icon_monitor(26.0, fg_muted())
                        } else {
                            icon_app_window(26.0, fg_muted())
                        }),
                )
                .child(
                    text(src.title.clone())
                        .font(type_control())
                        .color(FG)
                        .truncate(),
                )
                .child(
                    text(meta.or_else(|| src.app.clone()).unwrap_or_else(|| {
                        if src.kind == "screen" {
                            "Display"
                        } else {
                            "Window"
                        }
                        .into()
                    }))
                    .font(type_label())
                    .color(fg_muted())
                    .truncate(),
                );
            grid = grid.child(card);
        }
    }

    modal.child(tabs).child(
        col()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(body.child(grid)),
    )
}

fn soundboard_section(heading: &str, items: &[&DiscordSoundItem]) -> El {
    let mut grid = row().w_full().gap(8.0).flex_wrap();
    for sound in items {
        let s_clone = (*sound).clone();
        let label = sound.name.clone();
        let disabled = sound.available == Some(false);
        grid = grid.child(
            row()
                .key(format!("dc-sb-{}", sound.sound_id))
                .role("button")
                .label(label.clone())
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .w(112.0)
                .h(38.0)
                .px(10.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_CARD)
                .fill(BG.alpha(0.56))
                .border(1.0, hairline())
                .hover(hover_fill())
                .press(pressed_fill())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    if !disabled {
                        s.play_discord_sound(&s_clone);
                    }
                })
                .child(icon_music(12.0, fg_muted()))
                .child(
                    text(label)
                        .font(type_small())
                        .color(if disabled { fg_faint() } else { FG })
                        .truncate(),
                ),
        );
    }
    col()
        .w_full()
        .gap(6.0)
        .child(text(heading).font(type_label()).color(fg_muted()))
        .child(grid)
}

fn soundboard_popover(s: &AppState, blur: bool) -> El {
    let mut card = col()
        .key("dc-soundboard-panel")
        .w(420.0)
        .max_h(480.0)
        .padding(16.0)
        .gap(10.0)
        .radius(20.0)
        .fill(if blur {
            glass_panel_fill()
        } else {
            glass_still_panel_fill()
        })
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .transition(overlay_transition())
        .hit();
    if blur {
        card = card.backdrop_blur(GLASS_PANEL_SIGMA);
    }

    let sb_muted = s
        .discord
        .voice_ui
        .audio
        .as_ref()
        .is_some_and(|a| a.soundboard_muted);
    let sb_vol = if sb_muted {
        0
    } else {
        s.discord
            .voice_ui
            .audio
            .as_ref()
            .map(|a| a.soundboard_volume.round() as i32)
            .unwrap_or(100)
    };

    let header = row()
        .w_full()
        .gap(8.0)
        .center_y()
        .child(
            row()
                .grow(1.0)
                .h(32.0)
                .px(10.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(BG)
                .border(1.0, hairline())
                .child(icon_search(13.0, fg_muted()))
                .child(
                    text_input(&s.discord.voice_ui.soundboard_query, type_small())
                        .key("dc-sb-search")
                        .placeholder("Filter sounds", fg_muted())
                        .grow(1.0)
                        .color(FG)
                        .single_line()
                        .on_edit(edit_access(|s: &mut AppState| {
                            &mut s.discord.voice_ui.soundboard_query
                        })),
                ),
        )
        .child(
            row()
                .gap(4.0)
                .center_y()
                .child(
                    el().key("dc-sb-mute")
                        .role("button")
                        .label(if sb_muted {
                            "Unmute soundboard"
                        } else {
                            "Mute soundboard"
                        })
                        .cursor(Cursor::Pointer)
                        .hit()
                        .size(28.0, 28.0)
                        .radius(RADIUS_PILL)
                        .hover(hover_fill())
                        .center()
                        .on_press(move |s: &mut AppState| {
                            s.set_discord_audio(json!({ "soundboardMuted": !sb_muted }));
                        })
                        .child(if sb_muted || sb_vol == 0 {
                            icon_volume_off(14.0, fg_muted())
                        } else {
                            icon_volume(14.0, FG)
                        }),
                )
                .child(
                    el().key("dc-sb-vol-dn")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .size(22.0, 22.0)
                        .radius(RADIUS_PILL)
                        .hover(hover_fill())
                        .center()
                        .on_press(move |s: &mut AppState| {
                            let next = (sb_vol - 15).max(0);
                            if next == 0 {
                                s.set_discord_audio(json!({ "soundboardMuted": true }));
                            } else {
                                s.set_discord_audio(
                                    json!({ "soundboardVolume": next, "soundboardMuted": false }),
                                );
                            }
                        })
                        .child(text("−").font(type_small()).color(FG)),
                )
                .child(
                    text(format!("{sb_vol}%"))
                        .font(type_label())
                        .color(fg_muted()),
                )
                .child(
                    el().key("dc-sb-vol-up")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .size(22.0, 22.0)
                        .radius(RADIUS_PILL)
                        .hover(hover_fill())
                        .center()
                        .on_press(move |s: &mut AppState| {
                            let next = (sb_vol + 15).min(100);
                            s.set_discord_audio(
                                json!({ "soundboardVolume": next, "soundboardMuted": false }),
                            );
                        })
                        .child(text("+").font(type_small()).color(FG)),
                ),
        )
        .child(
            el().key("dc-sb-close")
                .role("button")
                .label("Close soundboard")
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .size(28.0, 28.0)
                .radius(RADIUS_PILL)
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::None))
                .child(icon_close(14.0, fg_muted())),
        );

    let q = s
        .discord
        .voice_ui
        .soundboard_query
        .text
        .trim()
        .to_lowercase();
    let keep = |item: &&DiscordSoundItem| q.is_empty() || item.name.to_lowercase().contains(&q);
    let guild_items: Vec<_> = s
        .discord
        .voice_ui
        .soundboard_guild
        .iter()
        .filter(keep)
        .collect();
    let default_items: Vec<_> = s
        .discord
        .voice_ui
        .soundboard_defaults
        .iter()
        .filter(keep)
        .collect();
    let other_guilds: Vec<(&str, Vec<&DiscordSoundItem>)> = s
        .discord
        .voice_ui
        .soundboard_guilds
        .iter()
        .map(|g| {
            (
                g.name.as_str(),
                g.items.iter().filter(keep).collect::<Vec<_>>(),
            )
        })
        .filter(|(_, items)| !items.is_empty())
        .collect();

    let mut content = col().w_full().gap(12.0);
    if let Some(ref err) = s.discord.voice_ui.soundboard_error {
        content = content.child(text(err.clone()).font(type_small()).color(bad()));
    }
    if s.discord.voice_ui.soundboard_loading
        && guild_items.is_empty()
        && default_items.is_empty()
        && other_guilds.is_empty()
    {
        content = content.child(
            el().w_full()
                .py(24.0)
                .center()
                .child(text("Loading sounds…").font(type_small()).color(fg_muted())),
        );
    } else {
        if !guild_items.is_empty() {
            content = content.child(soundboard_section("This server", &guild_items));
        }
        for (gname, items) in &other_guilds {
            content = content.child(soundboard_section(gname, items));
        }
        if !default_items.is_empty() {
            content = content.child(soundboard_section("Discord", &default_items));
        }
        if guild_items.is_empty() && other_guilds.is_empty() && default_items.is_empty() {
            content = content.child(
                el().w_full().py(24.0).center().child(
                    text(if !q.is_empty() {
                        "No sounds match that filter."
                    } else {
                        "No sounds available here."
                    })
                    .font(type_small())
                    .color(fg_muted()),
                ),
            );
        }
    }

    card.child(header).child(
        col()
            .w_full()
            .max_h(360.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(content),
    )
}

fn audio_diag_popover(s: &AppState, blur: bool) -> El {
    let mut card = col()
        .key("dc-audio-panel")
        .w(380.0)
        .max_h(520.0)
        .padding(16.0)
        .gap(12.0)
        .radius(20.0)
        .fill(if blur {
            glass_panel_fill()
        } else {
            glass_still_panel_fill()
        })
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .transition(overlay_transition())
        .hit();
    if blur {
        card = card.backdrop_blur(GLASS_PANEL_SIGMA);
    }

    let connected = s.discord.voice.state == "connected";
    let header = row()
        .w_full()
        .justify_between()
        .center_y()
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(el().size(8.0, 8.0).radius(4.0).fill(if connected {
                    good()
                } else {
                    warn()
                }))
                .child(
                    text(if connected {
                        "Voice connected"
                    } else {
                        "Audio & diagnostics"
                    })
                    .font(type_section())
                    .color(if connected { good() } else { FG }),
                ),
        )
        .child(
            el().key("dc-audio-close")
                .role("button")
                .label("Close audio panel")
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .size(28.0, 28.0)
                .radius(RADIUS_PILL)
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::None))
                .child(icon_close(14.0, fg_muted())),
        );

    let mut body = col().w_full().gap(10.0);

    if connected {
        let dur = s.voice_duration_label().unwrap_or_else(|| "00:00".into());
        body = body.child(
            row()
                .w_full()
                .px(12.0)
                .py(10.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_CARD)
                .fill(BG.alpha(0.56))
                .border(1.0, hairline())
                .child(
                    col()
                        .gap(2.0)
                        .child(
                            text(s.discord.voice_channel_label())
                                .font(type_control())
                                .color(FG),
                        )
                        .child(
                            row()
                                .gap(6.0)
                                .center_y()
                                .child(icon_lock(11.0, good()))
                                .child(
                                    text(format!("End-to-end encrypted · {dur}"))
                                        .font(type_label())
                                        .color(fg_muted()),
                                ),
                        ),
                ),
        );
    }

    if let Some(ref audio) = s.discord.voice_ui.audio {
        let cur_in = audio.input.as_deref().unwrap_or("");
        let cur_out = audio.output.as_deref().unwrap_or("");

        let mut in_row = row().w_full().gap(6.0).flex_wrap();
        for d in audio.inputs.iter().take(4) {
            let active = cur_in == d.id;
            let did = d.id.clone();
            in_row = in_row.child(
                filter_pill(format!("dc-aud-in-{}", d.id), d.name.clone(), active)
                    .on_press(move |s: &mut AppState| s.set_discord_audio(json!({ "input": did }))),
            );
        }
        let mut out_row = row().w_full().gap(6.0).flex_wrap();
        for d in audio.outputs.iter().take(4) {
            let active = cur_out == d.id;
            let did = d.id.clone();
            out_row = out_row.child(
                filter_pill(format!("dc-aud-out-{}", d.id), d.name.clone(), active).on_press(
                    move |s: &mut AppState| s.set_discord_audio(json!({ "output": did })),
                ),
            );
        }

        body = body
            .child(
                col()
                    .gap(6.0)
                    .child(text("Input device").font(type_label()).color(fg_muted()))
                    .child(in_row),
            )
            .child(
                col()
                    .gap(6.0)
                    .child(text("Output device").font(type_label()).color(fg_muted()))
                    .child(out_row),
            );
    } else {
        body = body.child(
            text("Loading audio devices…")
                .font(type_small())
                .color(fg_muted()),
        );
    }

    body = body.child(el().w_full().h(1.0).fill(hairline())).child(
        row().w_full().justify_end().child(
            glass_button(
                "dc-aud-open-settings",
                "Voice & video settings",
                Some(icon_settings(13.0, FG)),
            )
            .on_press(|s: &mut AppState| {
                s.discord.voice_ui.panel = VoicePanel::None;
                s.open_discord_settings(DiscordSettingsPage::Voice);
            }),
        ),
    );

    card.child(header).child(
        col()
            .w_full()
            .max_h(430.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(body),
    )
}

pub fn voice_overlay_layers(s: &AppState, blur: bool) -> Vec<El> {
    match s.discord.voice_ui.panel {
        VoicePanel::None => Vec::new(),
        VoicePanel::GoLive => vec![
            el().key("dc-golive-dismiss")
                .pin(Some(0.0), Some(52.0), Some(0.0), Some(78.0))
                .hit()
                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::None)),
            col()
                .key("dc-golive-wrap")
                .pin(Some(0.0), Some(52.0), Some(0.0), Some(76.0))
                .justify_end()
                .child(
                    row().w_full().justify_center().child(
                        row()
                            .w(CALL_BAR_W)
                            .child(el().w(SHARE_OFFSET_IN_BAR))
                            .child(golive_modal(s, blur)),
                    ),
                ),
        ],
        VoicePanel::Soundboard => vec![
            el().key("dc-sb-dismiss")
                .pin(Some(0.0), Some(52.0), Some(0.0), Some(0.0))
                .hit()
                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::None)),
            el().key("dc-sb-wrap")
                .pin(Some(0.0), None, Some(0.0), Some(90.0))
                .center()
                .child(soundboard_popover(s, blur)),
        ],
        VoicePanel::Audio => vec![
            el().key("dc-aud-dismiss")
                .pin(Some(0.0), Some(52.0), Some(0.0), Some(0.0))
                .hit()
                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::None)),
            el().key("dc-aud-wrap")
                .pin(Some(0.0), None, Some(0.0), Some(90.0))
                .center()
                .child(audio_diag_popover(s, blur)),
        ],
    }
}

pub fn call_dock(s: &AppState) -> Option<El> {
    if s.discord.voice.state == "off" {
        return None;
    }
    let connected = s.discord.voice.state == "connected";
    let label = s.discord.voice_channel_label();
    let dur = s.voice_duration_label();
    let is_golive = matches!(
        s.discord.voice_ui.golive_state.as_str(),
        "live" | "connecting" | "starting"
    );
    let cam = s.discord.voice_ui.camera_on;

    let mut dock = col()
        .key("dc-call-dock")
        .w_full()
        .px(10.0)
        .py(9.0)
        .gap(8.0)
        .fill(BG)
        .border_sides([1.0, 0.0, 0.0, 0.0], hairline())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(6.0)
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(2.0)
                        .child(
                            row()
                                .key("dc-dock-status")
                                .role("button")
                                .label("Voice connection details")
                                .cursor(Cursor::Pointer)
                                .hit()
                                .press_on_down()
                                .gap(6.0)
                                .center_y()
                                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::Audio))
                                .child(el().size(7.0, 7.0).radius(3.5).fill(if connected {
                                    good()
                                } else {
                                    warn()
                                }))
                                .child(
                                    text(if connected {
                                        match dur {
                                            Some(ref d) => format!("Voice connected · {d}"),
                                            None => "Voice connected".into(),
                                        }
                                    } else {
                                        "Connecting".into()
                                    })
                                    .font(type_label())
                                    .color(if connected { good() } else { warn() })
                                    .truncate(),
                                ),
                        )
                        .child(
                            row()
                                .key("dc-dock-channel")
                                .role("button")
                                .label("Open voice channel")
                                .cursor(Cursor::Pointer)
                                .hit()
                                .press_on_down()
                                .on_press(|s: &mut AppState| s.open_active_voice_channel())
                                .child(text(label).font(type_small()).color(fg_muted()).truncate()),
                        ),
                )
                .child(
                    el().key("dc-dock-leave")
                        .role("button")
                        .label("Disconnect")
                        .cursor(Cursor::Pointer)
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .size(30.0, 30.0)
                        .radius(RADIUS_PILL)
                        .fill(bad().alpha(0.16))
                        .hover(bad().alpha(0.28))
                        .center()
                        .motion(SPRING_QUICK)
                        .on_press(|s: &mut AppState| s.leave_discord_voice())
                        .child(icon_phone_off(14.0, bad())),
                ),
        );
    if connected {
        dock = dock.child(
            row()
                .w_full()
                .gap(6.0)
                .child(
                    dock_action_btn(
                        "dc-dock-cam",
                        "Camera",
                        cam,
                        icon_video(14.0, if cam { PRIMARY_FG } else { FG }),
                    )
                    .on_press(|s: &mut AppState| s.toggle_discord_camera()),
                )
                .child(
                    dock_action_btn(
                        "dc-dock-share",
                        "Share screen",
                        is_golive || s.discord.voice_ui.panel == VoicePanel::GoLive,
                        icon_screen_share(
                            14.0,
                            if is_golive || s.discord.voice_ui.panel == VoicePanel::GoLive {
                                PRIMARY_FG
                            } else {
                                FG
                            },
                        ),
                    )
                    .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::GoLive)),
                )
                .child(
                    dock_action_btn(
                        "dc-dock-sb",
                        "Soundboard",
                        s.discord.voice_ui.panel == VoicePanel::Soundboard,
                        icon_music(
                            14.0,
                            if s.discord.voice_ui.panel == VoicePanel::Soundboard {
                                PRIMARY_FG
                            } else {
                                FG
                            },
                        ),
                    )
                    .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::Soundboard)),
                ),
        );
    }
    Some(dock)
}

pub fn voice_stage_view(
    s: &AppState,
    guild_id: Option<&str>,
    ch: &DiscordChannel,
    scale: f32,
    voice_bar_blur: bool,
) -> El {
    let in_this_voice = s.discord.voice.channel_id.as_deref() == Some(ch.id.as_str())
        && s.discord.voice.state != "off";
    let mut roster: Vec<DiscordVoiceMember> = ch.members.clone().unwrap_or_default();
    if in_this_voice
        && let Some(me) = s.discord.home.me.as_ref()
        && !roster.iter().any(|m| m.id == me.id)
    {
        roster.insert(
            0,
            DiscordVoiceMember {
                id: me.id.clone(),
                name: me.name.clone(),
                avatar: me.avatar.clone(),
                mute: s.discord.voice.muted,
                deaf: s.discord.voice.deafened,
                video: s.discord.voice_ui.camera_on,
                ..Default::default()
            },
        );
    }
    let members = roster.as_slice();
    let _is_stage = ch.channel_type == 13 || ch.stage_topic.is_some();
    let is_golive = matches!(
        s.discord.voice_ui.golive_state.as_str(),
        "live" | "connecting" | "starting"
    );
    let chat_open = s.discord.voice_ui.show_voice_chat;

    let count_text = if members.is_empty() {
        "Nobody here".to_string()
    } else if members.len() == 1 {
        "1 here".to_string()
    } else {
        format!("{} here", members.len())
    };

    let mut head_left = row()
        .gap(10.0)
        .center_y()
        .child(icon_volume(16.0, fg_muted()))
        .child(text(ch.name.clone()).font(type_title()).color(FG))
        .child(text(count_text).font(type_small()).color(fg_muted()));

    if in_this_voice {
        let connected = s.discord.voice.state == "connected";
        let tone = if connected { good() } else { warn() };
        let status_label = if connected {
            "Voice connected"
        } else {
            "Connecting"
        };
        head_left = head_left.child(
            row()
                .key("dc-voice-head-diag")
                .role("button")
                .label("Voice diagnostics")
                .cursor(Cursor::Pointer)
                .hit()
                .press_on_down()
                .px(8.0)
                .py(3.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(tone.alpha(0.12))
                .border(1.0, tone.alpha(0.32))
                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::Audio))
                .child(el().size(6.0, 6.0).radius(3.0).fill(tone))
                .child(text(status_label).font(type_label()).color(tone)),
        );
    }

    let head_right = row().gap(8.0).center_y().child(
        el().key("dc-voice-chat-toggle")
            .role("button")
            .label(if chat_open {
                "Hide channel chat"
            } else {
                "Show channel chat"
            })
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .size(34.0, 34.0)
            .radius(RADIUS_PILL)
            .fill(if chat_open { FG } else { Color::TRANSPARENT })
            .border_gradient(
                1.0,
                if chat_open { FG } else { glass_rim_top() },
                if chat_open { FG } else { glass_rim_bottom() },
            )
            .hover(if chat_open {
                FG.alpha(0.90)
            } else {
                hover_fill()
            })
            .center()
            .motion(SPRING_QUICK)
            .on_press(|s: &mut AppState| s.toggle_voice_chat())
            .child(icon_chat(15.0, if chat_open { PRIMARY_FG } else { FG })),
    );

    let voice_head = row()
        .w_full()
        .h(54.0)
        .px(18.0)
        .justify_between()
        .center_y()
        .child(head_left)
        .child(head_right);

    let mut stage_inner = col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .px(18.0)
        .pb(63.0)
        .gap(14.0);

    let is_stage = ch.channel_type == 13 || ch.kind == "stage" || ch.stage_topic.is_some();
    let speakers: Vec<&DiscordVoiceMember> = if is_stage {
        members.iter().filter(|m| !m.suppress).collect()
    } else {
        members.iter().collect()
    };
    let mut audience: Vec<&DiscordVoiceMember> = if is_stage {
        members.iter().filter(|m| m.suppress).collect()
    } else {
        Vec::new()
    };
    audience.sort_by_key(|m| if m.request_to_speak.is_some() { 0 } else { 1 });
    let me_id = s.discord.home.me.as_ref().map(|m| m.id.as_str());
    let me_member = me_id.and_then(|id| members.iter().find(|m| m.id == id));
    let raising_count = audience
        .iter()
        .filter(|m| m.request_to_speak.is_some())
        .count();
    let can_moderate = is_stage && ch.can_moderate_stage;

    if is_stage {
        let topic = ch
            .stage_topic
            .clone()
            .or_else(|| ch.topic.clone())
            .unwrap_or_else(|| ch.name.clone());
        let gid_s = guild_id.unwrap_or("").to_string();
        let cid_s = ch.id.clone();
        let mut banner = col()
            .key("dc-stage-banner")
            .w_full()
            .px(14.0)
            .py(10.0)
            .gap(8.0)
            .radius(RADIUS_CARD)
            .fill(BG)
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom());

        let info_row = row().w_full().justify_between().center_y().gap(10.0).child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    row()
                        .px(8.0)
                        .py(3.0)
                        .radius(RADIUS_PILL)
                        .fill(if ch.stage_topic.is_some() {
                            good().alpha(0.18)
                        } else {
                            glass_still_control_fill()
                        })
                        .child(
                            text(if ch.stage_topic.is_some() {
                                "Live stage"
                            } else {
                                "Stage"
                            })
                            .font(type_label())
                            .color(if ch.stage_topic.is_some() { good() } else { FG }),
                        ),
                )
                .child(text(topic).font(type_section()).color(FG).truncate())
                .children((raising_count > 0).then(|| {
                    text(format!("{raising_count} waiting to speak"))
                        .font(type_small())
                        .color(fg_muted())
                })),
        );

        if let Some(mode) = s.discord.voice_ui.stage_editing_mode {
            let cid_go = cid_s.clone();
            let act = if mode == "start" {
                "stageStart"
            } else {
                "stageEdit"
            };
            banner = banner.child(info_row).child(
                row()
                    .w_full()
                    .gap(8.0)
                    .center_y()
                    .child(
                        text_input(&s.discord.voice_ui.stage_topic_input, type_body())
                            .key("dc-stage-topic-in")
                            .placeholder("What's this stage about?", fg_muted())
                            .grow(1.0)
                            .h(34.0)
                            .px(10.0)
                            .radius(RADIUS_SMALL)
                            .fill(BG)
                            .border(1.0, hairline())
                            .color(FG)
                            .single_line()
                            .on_edit(edit_access(|s: &mut AppState| {
                                &mut s.discord.voice_ui.stage_topic_input
                            })),
                    )
                    .child(
                        primary_button(
                            "dc-stage-topic-go",
                            if mode == "start" { "Start" } else { "Save" },
                            None,
                        )
                        .on_press(move |s: &mut AppState| {
                            let val = s.discord.voice_ui.stage_topic_input.text.trim().to_string();
                            if !val.is_empty() {
                                s.manage_discord_stage(act, &cid_go, Some(val));
                            }
                        }),
                    )
                    .child(
                        glass_button("dc-stage-topic-cancel", "Cancel", None).on_press(
                            |s: &mut AppState| s.discord.voice_ui.stage_editing_mode = None,
                        ),
                    ),
            );
        } else {
            let mut actions = row().gap(8.0).center_y();
            if in_this_voice && let Some(me_m) = me_member {
                if me_m.suppress {
                    let req = me_m.request_to_speak.is_some() || s.discord.voice_ui.stage_requested;
                    let (g1, c1) = (gid_s.clone(), cid_s.clone());
                    actions = actions.child(
                        glass_button(
                            "dc-stage-rts",
                            if req {
                                "Lower hand"
                            } else {
                                "Request to speak"
                            },
                            Some(icon_hand(14.0, FG)),
                        )
                        .on_press(move |s: &mut AppState| {
                            s.set_discord_stage_state(&g1, &c1, None, None, Some(!req));
                        }),
                    );
                    if can_moderate {
                        let (g2, c2) = (gid_s.clone(), cid_s.clone());
                        actions = actions.child(
                            primary_button("dc-stage-join", "Join stage", None).on_press(
                                move |s: &mut AppState| {
                                    s.set_discord_stage_state(&g2, &c2, None, Some(false), None);
                                },
                            ),
                        );
                    }
                } else {
                    let (g3, c3) = (gid_s.clone(), cid_s.clone());
                    actions = actions.child(
                        glass_button("dc-stage-to-aud", "Move to audience", None).on_press(
                            move |s: &mut AppState| {
                                s.set_discord_stage_state(&g3, &c3, None, Some(true), None);
                            },
                        ),
                    );
                }
            }
            if can_moderate {
                if ch.stage_topic.is_some() {
                    let existing = ch.stage_topic.clone().unwrap_or_default();
                    let cid_end = cid_s.clone();
                    actions =
                        actions.child(glass_button("dc-stage-edit", "Edit topic", None).on_press(
                            move |s: &mut AppState| {
                                s.discord.voice_ui.stage_topic_input = TextEdit::new(&existing);
                                s.discord.voice_ui.stage_editing_mode = Some("edit");
                            },
                        ));
                    if s.discord.voice_ui.stage_end_confirm {
                        actions = actions.child(
                            primary_button("dc-stage-end-yes", "End stage?", None).on_press(
                                move |s: &mut AppState| {
                                    s.manage_discord_stage("stageEnd", &cid_end, None)
                                },
                            ),
                        );
                    } else {
                        actions = actions.child(
                            glass_button("dc-stage-end", "End stage", None).on_press(
                                |s: &mut AppState| s.discord.voice_ui.stage_end_confirm = true,
                            ),
                        );
                    }
                } else {
                    let init = ch.topic.clone().unwrap_or_default();
                    actions = actions.child(
                        primary_button("dc-stage-start", "Start stage", None).on_press(
                            move |s: &mut AppState| {
                                s.discord.voice_ui.stage_topic_input = TextEdit::new(&init);
                                s.discord.voice_ui.stage_editing_mode = Some("start");
                            },
                        ),
                    );
                }
            }
            banner = banner.child(info_row.child(actions));
        }

        stage_inner = stage_inner.child(banner);
    }

    if !is_stage
        && let Some(ref act) = ch.activity
        && !act.is_empty()
    {
        stage_inner = stage_inner.child(
            row()
                .w_full()
                .px(14.0)
                .py(8.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_CARD)
                .fill(BG)
                .border(1.0, hairline())
                .child(text("Activity").font(type_label()).color(fg_muted()))
                .child(text(act.clone()).font(type_control()).color(FG)),
        );
    }

    if !s.discord.voice_ui.call_songs.is_empty() && in_this_voice {
        let mut songs_row = row().w_full().gap(8.0).wrap().center_y();
        for cs in &s.discord.voice_ui.call_songs {
            if let Some(ref r) = cs.rich
                && let Some(ref details) = r.details
            {
                let artist = r.state.as_deref().unwrap_or("");
                let line = if artist.is_empty() {
                    format!("{}: {details}", cs.name)
                } else {
                    format!("{}: {details} — {artist}", cs.name)
                };
                songs_row = songs_row.child(
                    row()
                        .key(format!("dc-callsong-{}", cs.user_id))
                        .px(10.0)
                        .py(5.0)
                        .gap(6.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(glass_still_control_fill())
                        .border(1.0, hairline())
                        .child(icon_music(12.0, good()))
                        .child(text(line).font(type_label()).color(FG).truncate()),
                );
            }
        }
        stage_inner = stage_inner.child(songs_row);
    }

    if is_golive {
        let title = s
            .discord
            .voice_ui
            .golive_title
            .clone()
            .unwrap_or_else(|| "Sharing screen".into());
        let stats_txt = if let Some(ref st) = s.discord.voice_ui.golive_stats {
            format!(
                "{}×{} · {:.0} fps · {:.1} Mbps · {} viewers",
                st.width,
                st.height,
                st.fps,
                st.bitrate / 1000.0,
                s.discord.voice_ui.golive_viewers.len()
            )
        } else {
            "Starting capture…".into()
        };
        stage_inner = stage_inner.child(
            row()
                .key("dc-golive-banner")
                .w_full()
                .px(14.0)
                .py(10.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_CARD)
                .fill(BG)
                .border(1.0, good().alpha(0.45))
                .child(
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(el().size(8.0, 8.0).radius(4.0).fill(good()))
                        .child(
                            col()
                                .gap(1.0)
                                .child(
                                    text(format!("Live · {title}"))
                                        .font(type_control())
                                        .color(FG),
                                )
                                .child(text(stats_txt).font(type_label()).color(good())),
                        ),
                )
                .child(
                    glass_button(
                        "dc-golive-stop-top",
                        "Stop sharing",
                        Some(icon_close(13.0, bad())),
                    )
                    .on_press(|s: &mut AppState| s.stop_discord_golive()),
                ),
        );
    }

    if let Some(ref watch_uid) = s.discord.voice_ui.watching_stream {
        stage_inner = stage_inner.child(stream_theater(s, watch_uid, members));
    }

    let watching_any = s.discord.voice_ui.watching_stream.is_some();
    let show_my_stream = is_golive && in_this_voice && s.discord.voice_ui.golive_preview_shown;
    let total_tiles = speakers.len() + usize::from(show_my_stream);
    if total_tiles == 0 && !watching_any {
        stage_inner = stage_inner.child(
            col()
                .w_full()
                .grow(1.0)
                .center()
                .gap(10.0)
                .child(
                    el().size(56.0, 56.0)
                        .radius(28.0)
                        .fill(BG)
                        .border(1.0, hairline())
                        .center()
                        .child(icon_volume(24.0, FG)),
                )
                .child(text(ch.name.clone()).font(type_title()).color(FG))
                .child(
                    text(if is_stage && !audience.is_empty() {
                        "No one is speaking yet."
                    } else {
                        "No one is here yet."
                    })
                    .font(type_body())
                    .color(fg_muted()),
                ),
        );
    } else if !s.discord.voice_ui.fullscreen_stream {
        let (sw, sh) = s.discord.voice_ui.stage_size;
        let dyn_w = if sw > 0.0 && sh > 0.0 {
            fit_tiles(sw, sh, total_tiles.max(1), 12.0)
        } else if total_tiles == 1 && !watching_any {
            1240.0
        } else {
            320.0
        };
        let mut tiles = row().w_full().gap(12.0).flex_wrap().justify_center();
        for m in &speakers {
            let demote = if can_moderate && in_this_voice && Some(m.id.as_str()) != me_id {
                guild_id.map(|g| (g.to_string(), ch.id.clone()))
            } else {
                None
            };
            tiles = tiles.child(participant_tile(
                s,
                guild_id,
                m,
                watching_any,
                dyn_w,
                demote,
                scale,
            ));
        }
        if show_my_stream {
            tiles = tiles.child(my_stream_tile(s, members, dyn_w, scale));
        }
        stage_inner = stage_inner.child(
            col()
                .key("dc-stage-tiles-wrap")
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .on_layout(|s: &mut AppState, r| {
                    if r[2] > 0.0 && r[3] > 0.0 {
                        let next = (r[2], r[3]);
                        if (s.discord.voice_ui.stage_size.0 - next.0).abs() > 2.0
                            || (s.discord.voice_ui.stage_size.1 - next.1).abs() > 2.0
                        {
                            s.discord.voice_ui.stage_size = next;
                        }
                    }
                })
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(
                    col()
                        .w_full()
                        .grow(1.0)
                        .min_h(if watching_any { 0.0 } else { 420.0 })
                        .center()
                        .child(tiles),
                ),
        );
    }

    if is_stage && !audience.is_empty() {
        let mut aud_list = row().w_full().gap(8.0).flex_wrap();
        for m in &audience {
            let raising = m.request_to_speak.is_some();
            let uid = m.id.clone();
            let gid_i = guild_id.unwrap_or("").to_string();
            let cid_i = ch.id.clone();
            aud_list = aud_list.child(
                row()
                    .key(format!("dc-stage-aud-{}", m.id))
                    .px(10.0)
                    .py(6.0)
                    .gap(8.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(BG)
                    .border(
                        1.0,
                        if raising {
                            warn().alpha(0.55)
                        } else {
                            hairline()
                        },
                    )
                    .child(avatar_box(
                        s,
                        m.avatar.as_deref(),
                        &m.name,
                        24.0,
                        12.0,
                        None,
                        false,
                        scale,
                    ))
                    .child(text(m.name.clone()).font(type_small()).color(FG))
                    .children((can_moderate && in_this_voice).then(|| {
                        glass_button(
                            "dc-stage-inv",
                            if raising {
                                "Invite to speak"
                            } else {
                                "Bring to stage"
                            },
                            None,
                        )
                        .key(format!("dc-stage-inv-{}", m.id))
                        .on_press(move |s: &mut AppState| {
                            s.set_discord_stage_state(
                                &gid_i,
                                &cid_i,
                                Some(&uid),
                                Some(false),
                                None,
                            );
                        })
                    })),
            );
        }
        stage_inner = stage_inner.child(
            col()
                .key("dc-stage-audience")
                .w_full()
                .gap(6.0)
                .child(
                    text(format!("Audience · {}", audience.len()))
                        .font(type_label())
                        .color(fg_muted()),
                )
                .child(aud_list),
        );
    }

    if let Some(err) = s.discord.voice_ui.voice_error.as_ref().or(s
        .discord
        .voice_ui
        .stage_error
        .as_ref())
    {
        stage_inner = stage_inner.child(
            row()
                .w_full()
                .px(12.0)
                .py(8.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(bad().alpha(0.14))
                .border(1.0, bad().alpha(0.35))
                .child(text(err.clone()).font(type_small()).color(FG))
                .child(
                    el().key("dc-voice-err-x")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .size(22.0, 22.0)
                        .center()
                        .on_press(|s: &mut AppState| {
                            s.discord.voice_ui.voice_error = None;
                            s.discord.voice_ui.stage_error = None;
                        })
                        .child(icon_close(12.0, FG)),
                ),
        );
    }

    let muted = s.discord.voice.muted;
    let deaf = s.discord.voice.deafened;
    let cam = s.discord.voice_ui.camera_on;
    let gid_btn = guild_id.map(str::to_string);
    let cid_btn = ch.id.clone();

    let mut voice_bar = if !in_this_voice {
        row()
            .key("dc-voice-bar")
            .role("button")
            .label(if is_stage { "Join stage" } else { "Join voice" })
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(56.0)
            .px(28.0)
            .center()
            .radius(RADIUS_PILL)
            .fill(if voice_bar_blur {
                glass_control_fill()
            } else {
                glass_still_control_fill()
            })
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| s.join_discord_voice(gid_btn.as_deref(), &cid_btn))
            .child(
                text(if is_stage { "Join stage" } else { "Join voice" })
                    .font(type_control())
                    .color(FG),
            )
    } else {
        row()
            .key("dc-voice-bar")
            .h(56.0)
            .w(CALL_BAR_W)
            .px(8.0)
            .gap(8.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(if voice_bar_blur {
                glass_control_fill()
            } else {
                glass_still_control_fill()
            })
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .child(
                bar_word_btn(
                    "dc-voice-mute",
                    if muted { "Unmute" } else { "Mute" },
                    if muted { "Unmute" } else { "Mute" },
                    false,
                    muted,
                    if muted {
                        icon_mic_off(18.0, bad())
                    } else {
                        icon_mic(18.0, FG)
                    },
                    89.0,
                )
                .on_press(|s: &mut AppState| s.toggle_discord_mute())
                .on_secondary_press(|s: &mut AppState, p, _| {
                    s.open_context_menu(
                        ContextMenuTarget::DiscordAudioQuick { input: true },
                        p.x,
                        p.y,
                    );
                }),
            )
            .child(
                bar_word_btn(
                    "dc-voice-deafen",
                    if deaf { "Undeafen" } else { "Deafen" },
                    if deaf { "Undeafen" } else { "Deafen" },
                    false,
                    deaf,
                    if deaf {
                        icon_headphones_off(18.0, bad())
                    } else {
                        icon_headphones(18.0, FG)
                    },
                    102.0,
                )
                .on_press(|s: &mut AppState| s.toggle_discord_deafen())
                .on_secondary_press(|s: &mut AppState, p, _| {
                    s.open_context_menu(
                        ContextMenuTarget::DiscordAudioQuick { input: false },
                        p.x,
                        p.y,
                    );
                }),
            )
            .child(
                bar_word_btn(
                    "dc-voice-cam",
                    "Camera",
                    if cam {
                        "Turn off camera"
                    } else {
                        "Turn on camera"
                    },
                    cam,
                    false,
                    icon_video(18.0, if cam { PRIMARY_FG } else { FG }),
                    105.0,
                )
                .on_press(|s: &mut AppState| s.toggle_discord_camera()),
            )
            .child(bar_divider())
            .child(
                bar_word_btn(
                    "dc-voice-share",
                    if is_golive { "Live" } else { "Share" },
                    if is_golive {
                        "Your stream"
                    } else {
                        "Share your screen"
                    },
                    is_golive || s.discord.voice_ui.panel == VoicePanel::GoLive,
                    false,
                    icon_screen_share(
                        18.0,
                        if is_golive || s.discord.voice_ui.panel == VoicePanel::GoLive {
                            PRIMARY_FG
                        } else {
                            FG
                        },
                    ),
                    94.0,
                )
                .on_press(move |s: &mut AppState| {
                    if is_golive {
                        s.open_context_menu(ContextMenuTarget::DiscordGoLiveMenu, 952.0, 940.0);
                    } else {
                        s.open_voice_panel(VoicePanel::GoLive);
                    }
                })
                .on_secondary_press(|s: &mut AppState, p, _| {
                    s.open_context_menu(ContextMenuTarget::DiscordGoLiveMenu, p.x, p.y);
                }),
            )
            .child(
                bar_word_btn(
                    "dc-voice-sb",
                    "Soundboard",
                    "Soundboard",
                    s.discord.voice_ui.panel == VoicePanel::Soundboard,
                    false,
                    icon_music(
                        18.0,
                        if s.discord.voice_ui.panel == VoicePanel::Soundboard {
                            PRIMARY_FG
                        } else {
                            FG
                        },
                    ),
                    132.0,
                )
                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::Soundboard)),
            )
            .child(bar_divider())
            .child(
                row()
                    .key("dc-voice-leave")
                    .role("button")
                    .label("Disconnect")
                    .cursor(Cursor::Pointer)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .h(40.0)
                    .w(94.0)
                    .gap(8.0)
                    .radius(RADIUS_PILL)
                    .fill(bad())
                    .hover(bad().alpha(0.88))
                    .press(bad().alpha(0.75))
                    .center()
                    .motion(SPRING_QUICK)
                    .on_press(|s: &mut AppState| s.leave_discord_voice())
                    .child(icon_phone_off(18.0, FG))
                    .child(text("Leave").font(type_control()).color(FG)),
            )
    };

    if voice_bar_blur {
        voice_bar = voice_bar.backdrop_blur(GLASS_CONTROL_SIGMA);
    }

    let voice_pane = el()
        .key("dc-voice-stage")
        .grow(1.0)
        .h_full()
        .min_w(0.0)
        .min_h(0.0)
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, hairline())
        .child(
            col()
                .w_full()
                .h_full()
                .min_h(0.0)
                .child(voice_head)
                .child(stage_inner),
        )
        .child(
            row()
                .pin(Some(0.0), None, Some(0.0), Some(18.0))
                .justify_center()
                .child(voice_bar),
        );

    if !chat_open {
        return voice_pane;
    }

    let chat_head = row()
        .w_full()
        .h(54.0)
        .px(16.0)
        .justify_between()
        .center_y()
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(icon_volume(15.0, fg_muted()))
                .child(
                    text(ch.name.clone())
                        .font(type_section())
                        .color(FG)
                        .truncate(),
                ),
        )
        .child(
            el().key("dc-voice-chat-close")
                .role("button")
                .label("Close channel chat")
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .size(28.0, 28.0)
                .radius(RADIUS_PILL)
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| s.toggle_voice_chat())
                .child(icon_close(14.0, fg_faint())),
        );

    let chat_pane = col()
        .key("dc-voice-chat-pane")
        .w(420.0)
        .h_full()
        .min_h(0.0)
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, hairline())
        .child(chat_head)
        .child(discord_chat::timeline_and_composer(
            s,
            &ch.id,
            format!("Message #{}", ch.name),
            format!("Welcome to #{}", ch.name),
            ch.topic
                .clone()
                .unwrap_or_else(|| format!("Voice channel chat for {}.", ch.name)),
            ch.can_send != Some(false),
            scale,
        ));

    row()
        .grow(1.0)
        .h_full()
        .min_w(0.0)
        .min_h(0.0)
        .gap(12.0)
        .child(voice_pane)
        .child(chat_pane)
}

pub fn island_call_capsule(s: &AppState, blur: bool) -> Option<El> {
    if s.room == Room::Discord || s.discord.voice.state == "off" {
        return None;
    }
    let connected = s.discord.voice.state == "connected";
    let muted = s.discord.voice.muted;
    let deaf = s.discord.voice.deafened;
    let streaming = s.discord.voice_ui.golive_state != "off";
    let channel_label = s.discord.voice_channel_label();
    let members: Vec<DiscordVoiceMember> = if let Some(ref gid) = s.discord.voice.guild_id
        && let Some(ref cid) = s.discord.voice.channel_id
    {
        s.discord
            .channels_by_guild
            .get(gid)
            .and_then(|c| c.channels.iter().find(|x| &x.id == cid))
            .and_then(|c| c.members.clone())
            .unwrap_or_default()
    } else if let Some(ref cid) = s.discord.voice.channel_id {
        s.discord
            .home
            .dms
            .iter()
            .find(|d| &d.id == cid)
            .and_then(|d| d.call.clone())
            .unwrap_or_default()
    } else {
        Vec::new()
    };

    let mut faces = row().gap(3.0).center_y();
    for m in members.iter().take(4) {
        let sp = s.discord.speaking.contains(&m.id)
            && !s
                .discord
                .voice_ui
                .user_mix
                .get(&m.id)
                .is_some_and(|x| x.muted);
        faces = faces.child(avatar_box(
            s,
            m.avatar.as_deref(),
            &m.name,
            20.0,
            10.0,
            None,
            sp,
            1.0,
        ));
    }
    if members.len() > 4 {
        faces = faces.child(
            text(format!("+{}", members.len() - 4))
                .font(type_label())
                .color(fg_muted()),
        );
    }

    let mut pill = row()
        .key("exo-island-call")
        .h(36.0)
        .pl(10.0)
        .pr(6.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if blur {
            glass_control_fill()
        } else {
            glass_still_control_fill()
        })
        .border_gradient(
            1.0,
            if !s.discord.speaking.is_empty() {
                good().alpha(0.65)
            } else {
                glass_rim_top()
            },
            glass_rim_bottom(),
        )
        .child(
            row()
                .key("exo-island-call-open")
                .role("button")
                .label("Open Discord call")
                .cursor(Cursor::Pointer)
                .hit()
                .gap(6.0)
                .center_y()
                .on_press(|s: &mut AppState| s.open_active_voice_channel())
                .child(el().size(7.0, 7.0).radius(4.0).fill(if connected {
                    good()
                } else {
                    warn()
                }))
                .child(
                    text(if connected {
                        channel_label
                    } else {
                        "Connecting".into()
                    })
                    .font(type_control())
                    .color(FG)
                    .max_w(120.0)
                    .truncate(),
                ),
        )
        .children((!members.is_empty()).then_some(faces))
        .children(streaming.then(|| {
            row()
                .px(6.0)
                .py(2.0)
                .radius(RADIUS_PILL)
                .fill(bad())
                .child(text("Live").font(type_label()).color(FG))
        }))
        .children(
            s.voice_duration_label()
                .map(|d| text(d).font(type_label()).color(fg_muted())),
        )
        .child(
            el().key("exo-island-mute")
                .role("button")
                .label(if muted { "Unmute" } else { "Mute" })
                .cursor(Cursor::Pointer)
                .hit()
                .size(26.0, 26.0)
                .radius(RADIUS_PILL)
                .fill(if muted {
                    bad().alpha(0.22)
                } else {
                    Color::TRANSPARENT
                })
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| s.toggle_discord_mute())
                .child(if muted {
                    icon_mic_off(14.0, bad())
                } else {
                    icon_mic(14.0, FG)
                }),
        )
        .child(
            el().key("exo-island-deaf")
                .role("button")
                .label(if deaf { "Undeafen" } else { "Deafen" })
                .cursor(Cursor::Pointer)
                .hit()
                .size(26.0, 26.0)
                .radius(RADIUS_PILL)
                .fill(if deaf {
                    bad().alpha(0.22)
                } else {
                    Color::TRANSPARENT
                })
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| s.toggle_discord_deafen())
                .child(if deaf {
                    icon_headphones_off(14.0, bad())
                } else {
                    icon_headphones(14.0, FG)
                }),
        )
        .child(
            el().key("exo-island-leave")
                .role("button")
                .label("Leave call")
                .cursor(Cursor::Pointer)
                .hit()
                .size(26.0, 26.0)
                .radius(RADIUS_PILL)
                .fill(bad())
                .hover(bad().alpha(0.85))
                .center()
                .on_press(|s: &mut AppState| s.leave_discord_voice())
                .child(icon_phone_off(14.0, FG)),
        );

    if blur {
        pill = pill.backdrop_blur(GLASS_CONTROL_SIGMA);
    }
    Some(pill)
}

pub fn incoming_call_overlay(s: &AppState, blur: bool) -> Option<El> {
    let cid = s.discord.voice_ui.ringing_channel_id.clone()?;
    let dm = s.discord.home.dms.iter().find(|d| d.id == cid);
    let caller = dm
        .map(|d| d.name.clone())
        .unwrap_or_else(|| "Incoming call".into());
    let av = dm.and_then(|d| d.avatar.as_deref());
    let cid_ans = cid.clone();
    let cid_vid = cid.clone();
    let cid_dec = cid.clone();

    let mut card = row()
        .key("dc-incoming-call")
        .pin(None, Some(64.0), Some(18.0), None)
        .px(16.0)
        .py(12.0)
        .gap(12.0)
        .center_y()
        .radius(RADIUS_PANEL)
        .fill(if blur {
            glass_panel_fill()
        } else {
            glass_still_panel_fill()
        })
        .border_gradient(1.0, good().alpha(0.65), glass_rim_bottom())
        .transition(overlay_transition())
        .hit()
        .child(avatar_box(s, av, &caller, 40.0, 20.0, None, true, 1.0))
        .child(
            col()
                .gap(2.0)
                .child(text(caller).font(type_section()).color(FG))
                .child(text("Incoming call").font(type_label()).color(good())),
        )
        .child(
            row()
                .gap(6.0)
                .center_y()
                .child(
                    el().key("dc-ring-answer")
                        .role("button")
                        .label("Join call")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .size(34.0, 34.0)
                        .radius(RADIUS_PILL)
                        .fill(good())
                        .center()
                        .on_press(move |s: &mut AppState| {
                            s.discord.voice_ui.ringing_channel_id = None;
                            s.join_discord_voice(None, &cid_ans);
                            s.open_active_voice_channel();
                        })
                        .child(icon_phone(15.0, PRIMARY_FG)),
                )
                .child(
                    el().key("dc-ring-video")
                        .role("button")
                        .label("Join with video")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .size(34.0, 34.0)
                        .radius(RADIUS_PILL)
                        .fill(glass_still_control_fill())
                        .border(1.0, hairline())
                        .hover(hover_fill())
                        .center()
                        .on_press(move |s: &mut AppState| {
                            s.discord.voice_ui.ringing_channel_id = None;
                            s.join_discord_voice(None, &cid_vid);
                            s.toggle_discord_camera();
                            s.open_active_voice_channel();
                        })
                        .child(icon_video(15.0, FG)),
                )
                .child(
                    el().key("dc-ring-decline")
                        .role("button")
                        .label("Decline")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .size(34.0, 34.0)
                        .radius(RADIUS_PILL)
                        .fill(bad())
                        .center()
                        .on_press(move |s: &mut AppState| s.decline_discord_call(&cid_dec))
                        .child(icon_phone_off(15.0, FG)),
                ),
        );
    if blur {
        card = card.backdrop_blur(GLASS_PANEL_SIGMA);
    }
    Some(card)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fit_tiles_respects_16_9_and_max_1280() {
        let solo = fit_tiles(1400.0, 900.0, 1, 12.0);
        assert_eq!(solo, 1280.0);
        let quad = fit_tiles(1200.0, 700.0, 4, 12.0);
        assert!((550.0..=600.0).contains(&quad), "quad={quad}");
        let tiny = fit_tiles(100.0, 60.0, 9, 12.0);
        assert_eq!(tiny, 120.0);
    }

    #[test]
    fn format_golive_stats_matches_react_format() {
        let st = DiscordGoLiveStatsEvent {
            width: 1920,
            height: 1080,
            fps: 60.0,
            bitrate: 6_000_000.0,
            max: Some(8_000_000.0),
            codec: Some("H265".into()),
            sound: Some("Game audio".into()),
        };
        assert_eq!(
            format_golive_stats(&st),
            "1920×1080 · 60 fps · H265 · up to 8.0 Mbps · Game audio"
        );
    }
}
