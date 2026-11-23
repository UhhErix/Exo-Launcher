use crate::discord::avatar_box;
use crate::discord_chat;
use crate::discord_state::DiscordSettingsPage;
use crate::discord_voice_state::{GoLiveTab, VoicePanel};
use crate::state::{AppState, ContextMenuTarget};
use exo_kit::{
    BG, FG, GLASS_CONTROL_SIGMA, GLASS_PANEL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD,
    RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL, SPRING_MOVE, SPRING_QUICK, bad, fg_faint, fg_muted,
    filter_pill, glass_button, glass_control_fill, glass_panel_fill, glass_rim_bottom,
    glass_rim_top, glass_still_control_fill, glass_still_panel_fill, good, hairline, hover_fill,
    icon_app_window, icon_chat, icon_close, icon_hand, icon_headphones, icon_headphones_off,
    icon_lock, icon_maximize, icon_mic, icon_mic_off, icon_monitor, icon_music, icon_phone_off,
    icon_screen_share, icon_settings, icon_video, icon_volume, icon_volume_off,
    modal_scrim_transition, pressed_fill, primary_button, scrim_fill, subtle_fill, type_body,
    type_control, type_label, type_section, type_small, type_title, warn,
};
use exo_rpc::{DiscordChannel, DiscordVoiceMember};
use rune::{Color, Cursor, El, ScrollSpec, SpringPair, SurfaceFit, Transition, col, el, row, text};
use serde_json::json;

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
        .px(14.0)
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
    solo: bool,
    scale: f32,
) -> El {
    let speaking = s.discord.speaking.contains(&m.id);
    let is_watching = s.discord.voice_ui.watching_stream.as_deref() == Some(m.id.as_str());
    let uid_watch = m.id.clone();
    let ctx_target = ContextMenuTarget::DiscordMember {
        guild_id: guild_id.map(str::to_string),
        user_id: m.id.clone(),
        name: m.name.clone(),
        username: None,
        in_voice: true,
    };

    let (tile_w, tile_h) = if compact {
        (216.0, 122.0)
    } else if solo {
        (1120.0, 630.0)
    } else {
        (320.0, 180.0)
    };
    let av_size = if compact { 46.0 } else { 72.0 };

    let mut tile = el()
        .key(format!("dc-stage-tile-{}", m.id))
        .radius(RADIUS_PANEL)
        .fill(if speaking { good().alpha(0.06) } else { BG })
        .motion(SPRING_QUICK)
        .hit()
        .cursor(Cursor::Pointer)
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.open_context_menu(ctx_target.clone(), p.x, p.y);
        });

    tile = tile.size(tile_w, tile_h);

    tile = if speaking {
        tile.border(2.0, good())
    } else {
        tile.border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
    };

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
        .gap(6.0)
        .center_y()
        .child(text(m.name.clone()).font(type_small()).color(FG).truncate());

    if m.deaf || m.server_deaf {
        badge = badge.child(icon_headphones_off(12.0, bad()));
    } else if m.mute || m.server_mute {
        badge = badge.child(icon_mic_off(12.0, warn()));
    }

    tile.child(badge)
}

fn golive_modal(s: &AppState, blur: bool) -> El {
    let mut modal = col()
        .key("dc-golive-panel")
        .w(560.0)
        .max_h(640.0)
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

    let header = row()
        .w_full()
        .justify_between()
        .center_y()
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(icon_screen_share(18.0, FG))
                .child(text("Share your screen").font(type_title()).color(FG)),
        )
        .child(
            el().key("dc-gl-close")
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
                .child(icon_close(15.0, fg_muted())),
        );

    let tab = s.discord.voice_ui.golive_tab;
    let tabs = row()
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
        );

    let want_kind = match tab {
        GoLiveTab::Applications => "window",
        GoLiveTab::Screens => "screen",
    };
    let filtered: Vec<_> = s
        .discord
        .voice_ui
        .golive_sources
        .iter()
        .filter(|src| src.kind == want_kind)
        .collect();

    let mut grid = row().w_full().gap(10.0).flex_wrap();
    if s.discord.voice_ui.golive_loading {
        grid = grid.child(
            el().w_full().py(28.0).center().child(
                text("Scanning windows and screens…")
                    .font(type_small())
                    .color(fg_muted()),
            ),
        );
    } else if filtered.is_empty() {
        grid = grid.child(
            el().w_full().py(28.0).center().child(
                text(if tab == GoLiveTab::Applications {
                    "No capturable application windows found."
                } else {
                    "No screens found."
                })
                .font(type_small())
                .color(fg_muted()),
            ),
        );
    } else {
        for src in filtered {
            let selected =
                s.discord.voice_ui.golive_selected_source.as_deref() == Some(src.id.as_str());
            let sid = src.id.clone();
            let mut card = col()
                .key(format!("dc-gl-src-{}", src.id))
                .role("button")
                .label(src.title.clone())
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .w(166.0)
                .padding(12.0)
                .gap(8.0)
                .radius(RADIUS_CARD)
                .fill(if selected {
                    FG.alpha(0.12)
                } else {
                    BG.alpha(0.56)
                })
                .hover(hover_fill())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    s.discord.voice_ui.golive_selected_source = Some(sid.clone());
                });
            card = if selected {
                card.border(1.5, FG)
            } else {
                card.border(1.0, hairline())
            };
            card = card
                .child(
                    el().w_full()
                        .h(68.0)
                        .radius(RADIUS_SMALL)
                        .fill(BG)
                        .border(1.0, hairline())
                        .center()
                        .child(if src.kind == "screen" {
                            icon_monitor(22.0, if selected { FG } else { fg_muted() })
                        } else {
                            icon_app_window(22.0, if selected { FG } else { fg_muted() })
                        }),
                )
                .child(
                    text(src.title.clone())
                        .font(type_control())
                        .color(FG)
                        .truncate(),
                )
                .child(
                    text(src.app.clone().unwrap_or_else(|| {
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

    let cur_h = s.discord.voice_ui.golive_height;
    let cur_fps = s.discord.voice_ui.golive_fps;
    let sound_on = s.discord.voice_ui.golive_sound;

    let quality_row = row()
        .w_full()
        .justify_between()
        .center_y()
        .gap(12.0)
        .child(
            col()
                .gap(6.0)
                .child(text("Resolution").font(type_label()).color(fg_muted()))
                .child(
                    row()
                        .gap(6.0)
                        .child(
                            filter_pill("dc-gl-720", "720p", cur_h == 720).on_press(
                                |s: &mut AppState| s.discord.voice_ui.golive_height = 720,
                            ),
                        )
                        .child(
                            filter_pill("dc-gl-1080", "1080p", cur_h == 1080).on_press(
                                |s: &mut AppState| s.discord.voice_ui.golive_height = 1080,
                            ),
                        )
                        .child(
                            filter_pill("dc-gl-src", "Source", cur_h == 0)
                                .on_press(|s: &mut AppState| s.discord.voice_ui.golive_height = 0),
                        ),
                ),
        )
        .child(
            col()
                .gap(6.0)
                .child(text("Frame rate").font(type_label()).color(fg_muted()))
                .child(
                    row()
                        .gap(6.0)
                        .child(
                            filter_pill("dc-gl-30", "30 fps", cur_fps == 30)
                                .on_press(|s: &mut AppState| s.discord.voice_ui.golive_fps = 30),
                        )
                        .child(
                            filter_pill("dc-gl-60", "60 fps", cur_fps == 60)
                                .on_press(|s: &mut AppState| s.discord.voice_ui.golive_fps = 60),
                        ),
                ),
        );

    let audio_row = row()
        .w_full()
        .py(6.0)
        .justify_between()
        .center_y()
        .child(
            col()
                .gap(2.0)
                .child(text("Share system audio").font(type_body()).color(FG))
                .child(
                    text("Include audio from the shared window or screen")
                        .font(type_label())
                        .color(fg_muted()),
                ),
        )
        .child(
            switch_pill("dc-gl-sound", "Share system audio", sound_on).on_press(
                |s: &mut AppState| {
                    s.discord.voice_ui.golive_sound = !s.discord.voice_ui.golive_sound
                },
            ),
        );

    let active = matches!(
        s.discord.voice_ui.golive_state.as_str(),
        "live" | "connecting" | "starting"
    );
    let footer = row()
        .w_full()
        .justify_between()
        .center_y()
        .child(if active {
            glass_button(
                "dc-gl-stop",
                "Stop streaming",
                Some(icon_close(13.0, bad())),
            )
            .on_press(|s: &mut AppState| s.stop_discord_golive())
        } else {
            el()
        })
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    glass_button("dc-gl-cancel", "Cancel", None)
                        .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::None)),
                )
                .child(
                    primary_button(
                        "dc-gl-start",
                        if active { "Switch stream" } else { "Go Live" },
                        Some(icon_screen_share(14.0, PRIMARY_FG)),
                    )
                    .on_press(|s: &mut AppState| s.start_discord_golive()),
                ),
        );

    modal
        .child(header)
        .child(tabs)
        .child(
            col()
                .w_full()
                .max_h(260.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(grid),
        )
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(quality_row)
        .child(audio_row)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(footer)
}

fn soundboard_popover(s: &AppState, blur: bool) -> El {
    let mut card = col()
        .key("dc-soundboard-panel")
        .w(380.0)
        .max_h(440.0)
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

    let header = row()
        .w_full()
        .justify_between()
        .center_y()
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(icon_music(16.0, FG))
                .child(text("Soundboard").font(type_section()).color(FG)),
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

    let mut pads = row().w_full().gap(8.0).flex_wrap();
    if s.discord.voice_ui.soundboard_loading {
        pads = pads.child(
            el().w_full()
                .py(24.0)
                .center()
                .child(text("Loading sounds…").font(type_small()).color(fg_muted())),
        );
    } else {
        let all: Vec<_> = s
            .discord
            .voice_ui
            .soundboard_guild
            .iter()
            .chain(s.discord.voice_ui.soundboard_defaults.iter())
            .collect();
        if all.is_empty() {
            pads = pads.child(
                el().w_full().py(24.0).center().child(
                    text("No soundboard sounds available.")
                        .font(type_small())
                        .color(fg_muted()),
                ),
            );
        } else {
            for sound in all {
                let s_clone = sound.clone();
                let label = sound.name.clone();
                pads = pads.child(
                    row()
                        .key(format!("dc-sb-{}", sound.sound_id))
                        .role("button")
                        .label(label.clone())
                        .cursor(Cursor::Pointer)
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .w(108.0)
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
                        .on_press(move |s: &mut AppState| s.play_discord_sound(&s_clone))
                        .child(icon_music(12.0, fg_muted()))
                        .child(text(label).font(type_small()).color(FG).truncate()),
                );
            }
        }
    }

    card.child(header).child(
        col()
            .w_full()
            .max_h(340.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(pads),
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
            el().key("dc-golive-scrim")
                .pin(Some(0.0), Some(52.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .transition(modal_scrim_transition())
                .hit()
                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::None)),
            el().key("dc-golive-wrap")
                .pin(Some(0.0), Some(52.0), Some(0.0), Some(0.0))
                .center()
                .child(golive_modal(s, blur)),
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
    let is_stage = ch.channel_type == 13 || ch.stage_topic.is_some();
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
        .pb(92.0)
        .gap(14.0);

    if is_stage {
        let topic = ch
            .stage_topic
            .clone()
            .unwrap_or_else(|| "Stage channel".into());
        let gid_s = guild_id.unwrap_or("").to_string();
        let cid_s = ch.id.clone();
        stage_inner = stage_inner.child(
            row()
                .w_full()
                .px(14.0)
                .py(10.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_CARD)
                .fill(BG)
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(
                    col()
                        .gap(2.0)
                        .child(text(topic).font(type_section()).color(FG))
                        .child(
                            text(format!("{} in stage", members.len()))
                                .font(type_small())
                                .color(fg_muted()),
                        ),
                )
                .child(
                    glass_button(
                        "dc-stage-rts",
                        if s.discord.voice_ui.stage_requested {
                            "Lower hand"
                        } else {
                            "Request to speak"
                        },
                        Some(icon_hand(14.0, FG)),
                    )
                    .on_press(move |s: &mut AppState| s.toggle_discord_stage_speak(&gid_s, &cid_s)),
                ),
        );
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
    if members.is_empty() && !watching_any {
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
                    text("No one is here yet.")
                        .font(type_body())
                        .color(fg_muted()),
                ),
        );
    } else if !s.discord.voice_ui.fullscreen_stream {
        let solo = members.len() == 1 && !watching_any;
        let mut tiles = row().w_full().gap(14.0).flex_wrap().justify_center();
        for m in members {
            tiles = tiles.child(participant_tile(s, guild_id, m, watching_any, solo, scale));
        }
        stage_inner = stage_inner.child(
            col()
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(
                    col()
                        .w_full()
                        .min_h(if watching_any { 0.0 } else { 420.0 })
                        .center()
                        .child(tiles),
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
            .px(10.0)
            .gap(6.0)
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
                )
                .on_press(|s: &mut AppState| s.toggle_discord_camera()),
            )
            .child(el().w(1.0).h(24.0).mx(2.0).fill(hairline()))
            .child(
                bar_word_btn(
                    "dc-voice-share",
                    "Share",
                    if is_golive {
                        "Stream options"
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
                )
                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::GoLive))
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
                )
                .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::Soundboard)),
            )
            .child(el().w(1.0).h(24.0).mx(2.0).fill(hairline()))
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
                    .px(14.0)
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
