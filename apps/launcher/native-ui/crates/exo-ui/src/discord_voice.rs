use crate::discord::avatar_box;
use crate::discord_voice_state::VoicePanel;
use crate::state::AppState;
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, SPRING_MOVE,
    SPRING_QUICK, bad, fg_muted, filter_pill, glass_button, glass_rim, good, hairline, hex,
    hover_fill, icon_close, icon_hand, icon_headphones, icon_headphones_off, icon_maximize,
    icon_mic, icon_mic_off, icon_monitor, icon_music, icon_phone, icon_phone_off,
    icon_screen_share, icon_sliders, icon_video, icon_volume, icon_volume_off, pressed_fill,
    primary_button, subtle_fill, type_body, type_control, type_label, type_section, type_small,
    type_title, warn,
};
use exo_rpc::{DiscordChannel, DiscordVoiceMember};
use rune::{Color, El, ScrollSpec, SpringPair, SurfaceFit, Transition, col, el, row, text};
use serde_json::json;

fn voice_panel_transition() -> Transition {
    let enter = Transition::scale(0.98).combined(Transition::opacity());
    Transition::asymmetric(enter, Transition::opacity())
        .springs(SpringPair::new(SPRING_MOVE, SPRING_QUICK))
}

fn pane_surface() -> Color {
    hex(0x0b0b0d, 1.0)
}

fn card_surface() -> Color {
    hex(0x0e0e11, 1.0)
}

fn control_pill_btn(
    key: &'static str,
    label: &'static str,
    active: bool,
    danger: bool,
    icon: El,
) -> El {
    let fill = if danger {
        bad().alpha(0.20)
    } else if active {
        FG
    } else {
        subtle_fill()
    };
    let fg = if danger {
        bad()
    } else if active {
        PRIMARY_FG
    } else {
        FG
    };
    row()
        .key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(10.0)
        .gap(5.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(fill)
        .border(1.0, if active { FG } else { glass_rim() })
        .hover(if danger {
            bad().alpha(0.30)
        } else if active {
            FG.alpha(0.92)
        } else {
            hover_fill()
        })
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .child(icon)
        .child(text(label).font(type_control()).color(fg))
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
        let mut s_el = el()
            .w_full()
            .radius(RADIUS_CARD)
            .clip_rounded(RADIUS_CARD)
            .fill(BG)
            .pixels_fit(&pixels, SurfaceFit::Fit);
        s_el = if fs {
            s_el.grow(1.0).min_h(380.0)
        } else {
            s_el.h(254.0)
        };
        (s_el, meta)
    } else {
        (
            col()
                .w_full()
                .h(if fs { 380.0 } else { 254.0 })
                .radius(RADIUS_CARD)
                .fill(BG)
                .center()
                .gap(8.0)
                .child(icon_screen_share(24.0, fg_muted()))
                .child(
                    text(format!("Waiting for {streamer_name}'s keyframe…"))
                        .font(type_body())
                        .color(fg_muted()),
                ),
            "Connecting stream…".into(),
        )
    };

    let overlay_top = row()
        .w_full()
        .justify_between()
        .center_y()
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    el().px(7.0)
                        .py(2.0)
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
                        if s.discord.voice_ui.fullscreen_stream {
                            "Compact"
                        } else {
                            "Expand"
                        },
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

    let mut card = col()
        .key("dc-stream-theater")
        .w_full()
        .px(14.0)
        .py(10.0)
        .gap(8.0)
        .radius(RADIUS_PANEL)
        .fill(card_surface())
        .border(1.0, glass_rim())
        .child(overlay_top)
        .child(surface);
    if fs {
        card = card.grow(1.0);
    }
    card
}

fn golive_panel(s: &AppState) -> El {
    let mut sources_col = col().w_full().gap(6.0);
    if s.discord.voice_ui.golive_loading {
        sources_col = sources_col.child(
            text("Scanning screens and games…")
                .font(type_small())
                .color(fg_muted()),
        );
    } else if s.discord.voice_ui.golive_sources.is_empty() {
        sources_col = sources_col.child(
            text("No shareable screens or windows found.")
                .font(type_small())
                .color(fg_muted()),
        );
    } else {
        for src in &s.discord.voice_ui.golive_sources {
            let sid = src.id.clone();
            let selected =
                s.discord.voice_ui.golive_selected_source.as_deref() == Some(src.id.as_str());
            sources_col = sources_col.child(
                row()
                    .key(format!("dc-gl-src-{}", src.id))
                    .w_full()
                    .px(12.0)
                    .py(10.0)
                    .gap(10.0)
                    .center_y()
                    .radius(RADIUS_CARD)
                    .fill(if selected {
                        good().alpha(0.14)
                    } else {
                        subtle_fill()
                    })
                    .border(
                        1.0,
                        if selected {
                            good().alpha(0.5)
                        } else {
                            hairline()
                        },
                    )
                    .hover(hover_fill())
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        s.discord.voice_ui.golive_selected_source = Some(sid.clone())
                    })
                    .child(icon_monitor(
                        16.0,
                        if selected { good() } else { fg_muted() },
                    ))
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .gap(2.0)
                            .child(
                                text(src.title.clone())
                                    .font(type_control())
                                    .color(FG)
                                    .truncate(),
                            )
                            .child(
                                text(format!(
                                    "{}×{} · {} Hz{}",
                                    src.width,
                                    src.height,
                                    src.hz,
                                    if src.game { " · Game" } else { "" }
                                ))
                                .font(type_label())
                                .color(if src.game {
                                    good()
                                } else {
                                    fg_muted()
                                }),
                            ),
                    ),
            );
        }
    }

    let res_row = row()
        .gap(6.0)
        .center_y()
        .child(text("Quality").font(type_small()).color(fg_muted()))
        .child(
            filter_pill(
                "dc-gl-720",
                "720p".into(),
                s.discord.voice_ui.golive_height == 720,
            )
            .on_press(|s: &mut AppState| s.discord.voice_ui.golive_height = 720),
        )
        .child(
            filter_pill(
                "dc-gl-1080",
                "1080p".into(),
                s.discord.voice_ui.golive_height == 1080,
            )
            .on_press(|s: &mut AppState| s.discord.voice_ui.golive_height = 1080),
        )
        .child(
            filter_pill(
                "dc-gl-src",
                "Source".into(),
                s.discord.voice_ui.golive_height == 0,
            )
            .on_press(|s: &mut AppState| s.discord.voice_ui.golive_height = 0),
        );

    let fps_row = row()
        .gap(6.0)
        .center_y()
        .child(text("Frame rate").font(type_small()).color(fg_muted()))
        .child(
            filter_pill(
                "dc-gl-30fps",
                "30 fps".into(),
                s.discord.voice_ui.golive_fps == 30,
            )
            .on_press(|s: &mut AppState| s.discord.voice_ui.golive_fps = 30),
        )
        .child(
            filter_pill(
                "dc-gl-60fps",
                "60 fps".into(),
                s.discord.voice_ui.golive_fps == 60,
            )
            .on_press(|s: &mut AppState| s.discord.voice_ui.golive_fps = 60),
        )
        .child(
            filter_pill(
                "dc-gl-sound",
                if s.discord.voice_ui.golive_sound {
                    "Audio on".into()
                } else {
                    "Audio off".into()
                },
                s.discord.voice_ui.golive_sound,
            )
            .on_press(|s: &mut AppState| {
                s.discord.voice_ui.golive_sound = !s.discord.voice_ui.golive_sound
            }),
        );

    let is_live = s.discord.voice_ui.golive_state == "live"
        || s.discord.voice_ui.golive_state == "connecting";

    col()
        .key("dc-golive-panel")
        .w_full()
        .px(16.0)
        .py(14.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .fill(card_surface())
        .border(1.0, glass_rim())
        .transition(voice_panel_transition())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    text("Share screen · Go Live")
                        .font(type_section())
                        .color(FG),
                )
                .child(
                    glass_button("dc-gl-close", "Close", Some(icon_close(12.0, fg_muted())))
                        .on_press(|s: &mut AppState| s.discord.voice_ui.panel = VoicePanel::None),
                ),
        )
        .child(sources_col)
        .child(
            row()
                .w_full()
                .justify_between()
                .flex_wrap()
                .gap(10.0)
                .child(res_row)
                .child(fps_row),
        )
        .child(row().w_full().justify_end().gap(8.0).child(if is_live {
            glass_button("dc-gl-stop", "Stop sharing", Some(icon_close(13.0, bad())))
                .on_press(|s: &mut AppState| s.stop_discord_golive())
        } else {
            primary_button(
                "dc-gl-start",
                "Go Live",
                Some(icon_screen_share(13.0, PRIMARY_FG)),
            )
            .on_press(|s: &mut AppState| s.start_discord_golive())
        }))
}

fn soundboard_panel(s: &AppState) -> El {
    let mut grid = row().w_full().gap(8.0).flex_wrap();
    for snd in s
        .discord
        .voice_ui
        .soundboard_guild
        .iter()
        .chain(s.discord.voice_ui.soundboard_defaults.iter())
    {
        let item = snd.clone();
        let leading = match snd.emoji_name.as_deref().filter(|e| !e.is_empty()) {
            Some(em) => text(em.to_string()).font(type_body()).color(FG),
            None => icon_volume(14.0, fg_muted()),
        };
        grid = grid.child(
            row()
                .key(format!("dc-snd-{}", snd.sound_id))
                .role("button")
                .label(snd.name.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .h(38.0)
                .px(12.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_CARD)
                .fill(subtle_fill())
                .border(1.0, hairline())
                .hover(hover_fill())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| s.play_discord_sound(&item))
                .child(leading)
                .child(text(snd.name.clone()).font(type_control()).color(FG)),
        );
    }

    col()
        .key("dc-soundboard-panel")
        .w_full()
        .px(16.0)
        .py(14.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .fill(card_surface())
        .border(1.0, glass_rim())
        .transition(voice_panel_transition())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(icon_music(14.0, FG))
                        .child(text("Soundboard").font(type_section()).color(FG)),
                )
                .child(
                    glass_button("dc-sb-close", "Close", Some(icon_close(12.0, fg_muted())))
                        .on_press(|s: &mut AppState| s.discord.voice_ui.panel = VoicePanel::None),
                ),
        )
        .child(if s.discord.voice_ui.soundboard_loading {
            text("Loading soundboard…")
                .font(type_small())
                .color(fg_muted())
        } else {
            grid
        })
}

fn audio_mixer_panel(s: &AppState) -> El {
    let mut body = col()
        .key("dc-audio-panel")
        .w_full()
        .px(16.0)
        .py(14.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .fill(card_surface())
        .border(1.0, glass_rim())
        .transition(voice_panel_transition())
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(icon_sliders(14.0, FG))
                        .child(
                            text("Voice audio & Exo Voice")
                                .font(type_section())
                                .color(FG),
                        ),
                )
                .child(
                    glass_button("dc-aud-close", "Close", Some(icon_close(12.0, fg_muted())))
                        .on_press(|s: &mut AppState| s.discord.voice_ui.panel = VoicePanel::None),
                ),
        );

    if let Some(ref aud) = s.discord.voice_ui.audio {
        let mut in_pills = row().w_full().gap(6.0).flex_wrap();
        for d in &aud.inputs {
            let id = d.id.clone();
            let active = aud.input.as_deref() == Some(d.id.as_str());
            in_pills = in_pills.child(
                filter_pill("dc-aud-in", d.name.clone(), active)
                    .on_press(move |s: &mut AppState| s.set_discord_audio(json!({ "input": id }))),
            );
        }
        let mut out_pills = row().w_full().gap(6.0).flex_wrap();
        for d in &aud.outputs {
            let id = d.id.clone();
            let active = aud.output.as_deref() == Some(d.id.as_str());
            out_pills = out_pills
                .child(filter_pill("dc-aud-out", d.name.clone(), active).on_press(
                    move |s: &mut AppState| s.set_discord_audio(json!({ "output": id })),
                ));
        }
        let in_vol = aud.input_volume.round() as i32;
        let out_vol = aud.output_volume.round() as i32;
        let sb_vol = aud.soundboard_volume.round() as i32;
        let sb_muted = aud.soundboard_muted;

        body = body
            .child(
                col()
                    .w_full()
                    .gap(6.0)
                    .child(text("Microphone").font(type_label()).color(fg_muted()))
                    .child(in_pills),
            )
            .child(
                col()
                    .w_full()
                    .gap(6.0)
                    .child(text("Output").font(type_label()).color(fg_muted()))
                    .child(out_pills),
            )
            .child(
                row()
                    .w_full()
                    .gap(16.0)
                    .flex_wrap()
                    .child(
                        row()
                            .gap(6.0)
                            .center_y()
                            .child(text(format!("Mic {in_vol}%")).font(type_small()).color(FG))
                            .child(glass_button("dc-mic-dn", "−", None).on_press(
                                move |s: &mut AppState| {
                                    s.set_discord_audio(
                                        json!({ "inputVolume": (in_vol - 10).clamp(0, 200) }),
                                    )
                                },
                            ))
                            .child(glass_button("dc-mic-up", "+", None).on_press(
                                move |s: &mut AppState| {
                                    s.set_discord_audio(
                                        json!({ "inputVolume": (in_vol + 10).clamp(0, 200) }),
                                    )
                                },
                            )),
                    )
                    .child(
                        row()
                            .gap(6.0)
                            .center_y()
                            .child(
                                text(format!("Output {out_vol}%"))
                                    .font(type_small())
                                    .color(FG),
                            )
                            .child(glass_button("dc-out-dn", "−", None).on_press(
                                move |s: &mut AppState| {
                                    s.set_discord_audio(
                                        json!({ "outputVolume": (out_vol - 10).clamp(0, 200) }),
                                    )
                                },
                            ))
                            .child(glass_button("dc-out-up", "+", None).on_press(
                                move |s: &mut AppState| {
                                    s.set_discord_audio(
                                        json!({ "outputVolume": (out_vol + 10).clamp(0, 200) }),
                                    )
                                },
                            )),
                    )
                    .child(
                        row()
                            .gap(6.0)
                            .center_y()
                            .child(
                                text(format!("Soundboard {sb_vol}%"))
                                    .font(type_small())
                                    .color(FG),
                            )
                            .child(
                                glass_button(
                                    "dc-sb-mute",
                                    if sb_muted { "Unmute" } else { "Mute" },
                                    None,
                                )
                                .on_press(
                                    move |s: &mut AppState| {
                                        s.set_discord_audio(json!({ "soundboardMuted": !sb_muted }))
                                    },
                                ),
                            ),
                    ),
            );
    } else {
        body = body.child(
            text("Reading audio devices…")
                .font(type_small())
                .color(fg_muted()),
        );
    }

    body = body.child(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .pt(4.0)
            .child(
                col()
                    .gap(2.0)
                    .child(text("Exo Only My Voice").font(type_control()).color(FG))
                    .child(
                        text(if s.discord.voice_ui.my_voice_learned {
                            "Voiceprint active — background voices filtered locally"
                        } else {
                            "Not trained yet"
                        })
                        .font(type_label())
                        .color(if s.discord.voice_ui.my_voice_learned {
                            good()
                        } else {
                            fg_muted()
                        }),
                    ),
            )
            .child(if s.discord.voice_ui.my_voice_learned {
                glass_button("dc-myvoice-forget", "Reset voiceprint", None)
                    .on_press(|s: &mut AppState| s.forget_discord_my_voice())
            } else {
                el()
            }),
    );
    body
}

pub fn voice_stage_view(
    s: &AppState,
    guild_id: Option<&str>,
    ch: &DiscordChannel,
    scale: f32,
) -> El {
    let members = ch.members.as_deref().unwrap_or(&[]);
    let in_this_voice = s.discord.voice.channel_id.as_deref() == Some(ch.id.as_str())
        && s.discord.voice.state != "off";
    let is_stage = ch.channel_type == 13 || ch.stage_topic.is_some();
    let is_golive = s.discord.voice_ui.golive_state == "live"
        || s.discord.voice_ui.golive_state == "connecting";

    let mut stage = col()
        .key("dc-voice-stage")
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .px(16.0)
        .py(14.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .fill(pane_surface())
        .border(1.0, hairline());

    if is_stage {
        let topic = ch
            .stage_topic
            .clone()
            .unwrap_or_else(|| "Stage channel".into());
        let gid_s = guild_id.unwrap_or("").to_string();
        let cid_s = ch.id.clone();
        stage = stage.child(
            row()
                .w_full()
                .px(14.0)
                .py(10.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_CARD)
                .fill(card_surface())
                .border(1.0, glass_rim())
                .child(
                    col()
                        .gap(2.0)
                        .child(text(topic).font(type_title()).color(FG))
                        .child(
                            text(format!("{} in stage", members.len()))
                                .font(type_small())
                                .color(fg_muted()),
                        ),
                )
                .child(
                    control_pill_btn(
                        "dc-stage-rts",
                        if s.discord.voice_ui.stage_requested {
                            "Lower hand"
                        } else {
                            "Request to speak"
                        },
                        s.discord.voice_ui.stage_requested,
                        false,
                        icon_hand(
                            14.0,
                            if s.discord.voice_ui.stage_requested {
                                PRIMARY_FG
                            } else {
                                FG
                            },
                        ),
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
        stage = stage.child(
            row()
                .key("dc-golive-banner")
                .w_full()
                .px(14.0)
                .py(10.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_CARD)
                .fill(good().alpha(0.12))
                .border(1.0, good().alpha(0.38))
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

    let mut scroll_body = col().w_full().gap(14.0).pb(8.0);

    if let Some(ref watch_uid) = s.discord.voice_ui.watching_stream {
        scroll_body = scroll_body.child(stream_theater(s, watch_uid, members));
    }

    if !s.discord.voice_ui.fullscreen_stream {
        let mut tiles = row().w_full().gap(12.0).flex_wrap().justify_center();
        for m in members {
            let speaking = s.discord.speaking.contains(&m.id);
            let uid_watch = m.id.clone();
            let uid_mix = m.id.clone();
            let is_watching = s.discord.voice_ui.watching_stream.as_deref() == Some(m.id.as_str());
            let mix_open = s.discord.voice_ui.mix_open_user.as_deref() == Some(m.id.as_str());
            let mix = s
                .discord
                .voice_ui
                .user_mix
                .get(&m.id)
                .cloned()
                .unwrap_or_default();

            let watching_any = s.discord.voice_ui.watching_stream.is_some();
            let mut tile = col()
                .key(format!("dc-stage-tile-{}", m.id))
                .w(if watching_any { 188.0 } else { 196.0 })
                .px(12.0)
                .py(if watching_any { 10.0 } else { 14.0 })
                .gap(if watching_any { 7.0 } else { 10.0 })
                .center()
                .radius(RADIUS_PANEL)
                .fill(card_surface())
                .border(
                    if speaking { 2.0 } else { 1.0 },
                    if speaking { good() } else { glass_rim() },
                )
                .child(avatar_box(
                    s,
                    m.avatar.as_deref(),
                    &m.name,
                    if watching_any { 42.0 } else { 58.0 },
                    if watching_any { 21.0 } else { 29.0 },
                    None,
                    speaking,
                    scale,
                ))
                .child(
                    row()
                        .gap(6.0)
                        .center_y()
                        .child(
                            text(m.name.clone())
                                .font(type_control())
                                .color(FG)
                                .truncate(),
                        )
                        .child(if m.deaf || m.server_deaf {
                            icon_headphones_off(12.0, bad())
                        } else if m.mute || m.server_mute {
                            icon_mic_off(12.0, warn())
                        } else {
                            el()
                        }),
                );

            let mut row_btns = row().gap(6.0).center_y();
            if m.stream {
                row_btns = row_btns.child(
                    row()
                        .key(format!("dc-watch-{}", m.id))
                        .role("button")
                        .label("Watch stream")
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .press_scale(PRESS_SCALE)
                        .h(26.0)
                        .px(9.0)
                        .gap(5.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(if is_watching {
                            good().alpha(0.20)
                        } else {
                            bad().alpha(0.22)
                        })
                        .border(
                            1.0,
                            if is_watching {
                                good().alpha(0.5)
                            } else {
                                bad().alpha(0.45)
                            },
                        )
                        .on_press(move |s: &mut AppState| {
                            if is_watching {
                                s.stop_watching_discord_stream();
                            } else {
                                s.watch_discord_stream(&uid_watch);
                            }
                        })
                        .child(
                            text(if is_watching {
                                "Watching"
                            } else {
                                "Watch LIVE"
                            })
                            .font(type_label())
                            .color(if is_watching {
                                good()
                            } else {
                                bad()
                            }),
                        ),
                );
            }
            row_btns = row_btns.child(
                row()
                    .key(format!("dc-mix-btn-{}", m.id))
                    .role("button")
                    .label("User volume")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .h(26.0)
                    .px(8.0)
                    .gap(4.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(if mix.muted {
                        bad().alpha(0.18)
                    } else {
                        subtle_fill()
                    })
                    .hover(hover_fill())
                    .on_press(move |s: &mut AppState| {
                        if s.discord.voice_ui.mix_open_user.as_deref() == Some(uid_mix.as_str()) {
                            s.discord.voice_ui.mix_open_user = None;
                        } else {
                            s.discord.voice_ui.mix_open_user = Some(uid_mix.clone());
                        }
                    })
                    .child(if mix.muted {
                        icon_volume_off(11.0, bad())
                    } else {
                        icon_volume(11.0, fg_muted())
                    })
                    .child(
                        text(format!("{}%", mix.volume.round() as i32))
                            .font(type_label())
                            .color(if mix.muted { bad() } else { fg_muted() }),
                    ),
            );
            tile = tile.child(row_btns);

            if mix_open {
                let u_dn = m.id.clone();
                let u_up = m.id.clone();
                let u_mt = m.id.clone();
                let v = mix.volume;
                let mt = mix.muted;
                tile = tile.child(
                    row()
                        .key(format!("dc-mix-row-{}", m.id))
                        .gap(4.0)
                        .center_y()
                        .child(glass_button("dc-umix-dn", "−", None).on_press(
                            move |s: &mut AppState| {
                                s.set_discord_user_mix(&u_dn, Some(v - 20.0), None, None)
                            },
                        ))
                        .child(glass_button("dc-umix-up", "+", None).on_press(
                            move |s: &mut AppState| {
                                s.set_discord_user_mix(&u_up, Some(v + 20.0), None, None)
                            },
                        ))
                        .child(
                            glass_button("dc-umix-mute", if mt { "Unmute" } else { "Mute" }, None)
                                .on_press(move |s: &mut AppState| {
                                    s.set_discord_user_mix(&u_mt, None, Some(!mt), None)
                                }),
                        ),
                );
            }
            tiles = tiles.child(tile);
        }
        scroll_body = scroll_body.child(tiles);
    }

    match s.discord.voice_ui.panel {
        VoicePanel::GoLive => scroll_body = scroll_body.child(golive_panel(s)),
        VoicePanel::Soundboard => scroll_body = scroll_body.child(soundboard_panel(s)),
        VoicePanel::Audio => scroll_body = scroll_body.child(audio_mixer_panel(s)),
        VoicePanel::None => {}
    }

    let muted = s.discord.voice.muted;
    let deaf = s.discord.voice.deafened;
    let cam = s.discord.voice_ui.camera_on;
    let gid_btn = guild_id.map(str::to_string);
    let cid_btn = ch.id.clone();

    let controls_bar = row()
        .key("dc-voice-controls")
        .w_full()
        .pt(4.0)
        .gap(6.0)
        .justify_center()
        .flex_wrap()
        .child(
            control_pill_btn(
                "dc-vc-mute",
                if muted { "Unmute" } else { "Mute" },
                false,
                muted,
                if muted {
                    icon_mic_off(14.0, bad())
                } else {
                    icon_mic(14.0, FG)
                },
            )
            .on_press(|s: &mut AppState| s.toggle_discord_mute()),
        )
        .child(
            control_pill_btn(
                "dc-vc-deafen",
                if deaf { "Undeafen" } else { "Deafen" },
                false,
                deaf,
                if deaf {
                    icon_headphones_off(14.0, bad())
                } else {
                    icon_headphones(14.0, FG)
                },
            )
            .on_press(|s: &mut AppState| s.toggle_discord_deafen()),
        )
        .child(
            control_pill_btn(
                "dc-vc-share",
                if is_golive { "Live" } else { "Share" },
                s.discord.voice_ui.panel == VoicePanel::GoLive || is_golive,
                false,
                icon_screen_share(
                    14.0,
                    if s.discord.voice_ui.panel == VoicePanel::GoLive || is_golive {
                        PRIMARY_FG
                    } else {
                        FG
                    },
                ),
            )
            .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::GoLive)),
        )
        .child(
            control_pill_btn(
                "dc-vc-cam",
                if cam { "Camera on" } else { "Camera" },
                cam,
                false,
                icon_video(14.0, if cam { PRIMARY_FG } else { FG }),
            )
            .on_press(|s: &mut AppState| s.toggle_discord_camera()),
        )
        .child(
            control_pill_btn(
                "dc-vc-sb",
                "Soundboard",
                s.discord.voice_ui.panel == VoicePanel::Soundboard,
                false,
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
        )
        .child(
            control_pill_btn(
                "dc-vc-audio",
                "Audio",
                s.discord.voice_ui.panel == VoicePanel::Audio,
                false,
                icon_sliders(
                    14.0,
                    if s.discord.voice_ui.panel == VoicePanel::Audio {
                        PRIMARY_FG
                    } else {
                        FG
                    },
                ),
            )
            .on_press(|s: &mut AppState| s.open_voice_panel(VoicePanel::Audio)),
        )
        .child(if in_this_voice {
            control_pill_btn(
                "dc-vc-disconnect",
                "Disconnect",
                false,
                true,
                icon_phone_off(14.0, bad()),
            )
            .on_press(|s: &mut AppState| s.leave_discord_voice())
        } else {
            primary_button(
                "dc-vc-join",
                "Join voice",
                Some(icon_phone(14.0, PRIMARY_FG)),
            )
            .on_press(move |s: &mut AppState| s.join_discord_voice(gid_btn.as_deref(), &cid_btn))
        });

    stage
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(scroll_body),
        )
        .child(controls_bar)
}
