use crate::media_state::CaptureFilter;
use crate::pc_state::format_bytes;
use crate::state::AppState;
use exo_kit::{
    BG, BadgeTone, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    RADIUS_SHEET, RADIUS_SMALL, SPRING_MOVE, SPRING_QUICK, bad, fg_faint, fg_muted, filter_pill,
    glass_button, glass_panel, glass_rim, hairline, hover_fill, icon_back, icon_camera, icon_close,
    icon_film, icon_folder, icon_pause, icon_play, icon_plus, icon_record_dot, icon_replay,
    icon_scissors, icon_settings, icon_star, icon_trash, icon_volume, icon_volume_off,
    primary_button, section_card, status_badge, subtle_fill, type_body, type_control, type_page,
    type_section, type_small, type_title, warn,
};
use exo_rpc::{CaptureItem, format_duration_ms};
use rune::color::oklch;
use rune::{Color, El, ScrollSpec, Shape, SurfaceFit, TextStyle, canvas, col, el, row, text};

fn clip_hue(name: &str) -> f32 {
    let mut h: u32 = 2166136261;
    for b in name.bytes() {
        h ^= b as u32;
        h = h.wrapping_mul(16777619);
    }
    (h % 360) as f32
}

fn keys_label(raw: &str) -> String {
    raw.split('+')
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .collect::<Vec<_>>()
        .join(" + ")
}

fn circle_pts(cx: f32, cy: f32, r: f32, steps: usize) -> Vec<[f32; 2]> {
    (0..=steps)
        .map(|i| {
            let a = (i as f32 / steps as f32) * std::f32::consts::TAU;
            [cx + r * a.cos(), cy + r * a.sin()]
        })
        .collect()
}

fn icon_gallery(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(
            vec![
                [4.0, 5.0],
                [20.0, 5.0],
                [20.0, 19.0],
                [4.0, 19.0],
                [4.0, 5.0],
            ],
            1.8,
            color,
        )
        .fill(circle_pts(9.0, 10.0, 1.5, 10), color)
        .stroke(
            vec![
                [5.0, 17.0],
                [10.0, 12.5],
                [14.0, 16.0],
                [16.5, 13.5],
                [19.5, 17.0],
            ],
            1.8,
            color,
        );
    canvas(shape).size(size, size)
}

fn icon_notes(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(
            vec![
                [5.0, 3.5],
                [19.0, 3.5],
                [19.0, 20.5],
                [5.0, 20.5],
                [5.0, 3.5],
            ],
            1.8,
            color,
        )
        .stroke(vec![[8.5, 8.5], [15.5, 8.5]], 1.8, color)
        .stroke(vec![[8.5, 12.5], [15.5, 12.5]], 1.8, color)
        .stroke(vec![[8.5, 16.5], [12.5, 16.5]], 1.8, color);
    canvas(shape).size(size, size)
}

fn popover_row(key: &str, label: &str, icon: El, hint: Option<String>, disabled: bool) -> El {
    let mut r = row()
        .key(key)
        .role("menuitem")
        .label(label)
        .focusable(!disabled)
        .hit()
        .press_on_down()
        .w_full()
        .min_h(38.0)
        .px(12.0)
        .gap(10.0)
        .radius(RADIUS_SMALL)
        .center_y()
        .hover(hover_fill())
        .motion(SPRING_QUICK)
        .child(icon)
        .child(
            text(label)
                .font(type_control())
                .color(if disabled { fg_faint() } else { FG })
                .grow(1.0)
                .min_w(0.0),
        );
    if let Some(h) = hint
        && !h.is_empty()
    {
        r = r.child(text(h).font(type_small()).color(fg_faint()));
    }
    r
}

#[allow(dead_code)]
pub fn popover(s: &AppState) -> El {
    let status = s.media.capture_status.as_ref();
    let live = status.and_then(|st| st.state.as_ref());
    let cfg = status.and_then(|st| st.settings.as_ref());
    let present = live.map_or(status.is_none_or(|st| st.present), |l| l.present);
    let buffering = live.is_some_and(|l| l.buffering || l.replay);
    let recording = live.is_some_and(|l| l.recording);

    let replay_hint = cfg
        .map(|c| keys_label(&c.save_replay_keys))
        .filter(|k| !k.is_empty())
        .or_else(|| Some("Ctrl + Alt + F10".into()));
    let record_hint = cfg
        .map(|c| keys_label(&c.record_keys))
        .filter(|k| !k.is_empty())
        .or_else(|| Some("Ctrl + Alt + F9".into()));
    let snip_hint = cfg
        .map(|c| keys_label(&c.snip_keys))
        .filter(|k| !k.is_empty())
        .or_else(|| Some("Ctrl + Alt + F8".into()));

    let mut menu = glass_panel("capture-popover-menu", 240.0)
        .role("menu")
        .label("Capture")
        .pin(None, Some(48.0), Some(168.0), None)
        .padding(8.0)
        .gap(2.0)
        .radius(RADIUS_CARD)
        .clip_rounded(RADIUS_CARD);

    if present {
        menu = menu
            .child(
                popover_row(
                    "cap-menu-replay",
                    "Save replay",
                    icon_replay(16.0, if buffering { FG } else { fg_faint() }),
                    replay_hint,
                    !buffering,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.capture_act("saveReplay");
                }),
            )
            .child(
                popover_row(
                    "cap-menu-record",
                    if recording {
                        "Stop recording"
                    } else {
                        "Record"
                    },
                    icon_record_dot(16.0, if recording { bad() } else { FG }),
                    record_hint,
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.capture_act("toggleRecord");
                }),
            )
            .child(
                popover_row(
                    "cap-menu-snip",
                    "Snip",
                    icon_scissors(16.0, FG),
                    snip_hint,
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.capture_act("snip");
                }),
            )
            .child(
                popover_row(
                    "cap-menu-note",
                    "New note",
                    icon_plus(16.0, FG),
                    None,
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.open_captures(None);
                }),
            )
            .child(
                popover_row(
                    "cap-menu-captures",
                    "Captures",
                    icon_gallery(16.0, FG),
                    None,
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.open_captures(None);
                }),
            );
    } else {
        menu = menu
            .child(
                popover_row(
                    "cap-menu-note",
                    "New note",
                    icon_plus(16.0, FG),
                    None,
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.open_captures(None);
                }),
            )
            .child(
                popover_row("cap-menu-notes", "Notes", icon_notes(16.0, FG), None, false).on_press(
                    |s: &mut AppState| {
                        s.capture_menu_open = false;
                        s.open_captures(None);
                    },
                ),
            );
    }

    menu
}

fn clip_card(item: &CaptureItem, s: &AppState, scale: f32) -> El {
    let hue = clip_hue(&item.game);
    let thumb = item
        .thumb
        .as_deref()
        .and_then(|u| s.capture_thumb_image(u, scale));
    let mut preview = el()
        .w_full()
        .aspect_ratio(16.0 / 9.0)
        .radius(4.0)
        .clip_rounded(4.0);
    if let Some(img) = thumb {
        preview = preview.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        preview = preview
            .linear_gradient(
                0.35,
                [(0.0, oklch(0.32, 0.10, hue)), (1.0, oklch(0.10, 0.03, hue))],
            )
            .center()
            .child(if item.kind == "video" {
                icon_film(24.0, FG.alpha(0.5))
            } else {
                icon_camera(24.0, FG.alpha(0.5))
            });
    }

    let mut overlay = el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0));
    if item.favourite {
        overlay = overlay.child(
            el().pin(None, Some(6.0), Some(6.0), None)
                .size(24.0, 24.0)
                .radius(RADIUS_PILL)
                .fill(BG.alpha(0.65))
                .center()
                .child(icon_star(12.0, warn())),
        );
    }
    if let Some(dur) = item.duration_ms {
        overlay = overlay.child(
            el().pin(None, None, Some(6.0), Some(6.0))
                .px(6.0)
                .py(2.0)
                .radius(RADIUS_PILL)
                .fill(BG.alpha(0.72))
                .child(
                    text(format_duration_ms(dur))
                        .font(TextStyle::new(11.0, 500.0))
                        .color(FG),
                ),
        );
    }

    let id = item.id.clone();
    el().key(format!("cap-card-{}", item.id))
        .role("button")
        .label(item.name.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .w_full()
        .min_w(0.0)
        .radius(4.0)
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
                    [(0.0, oklch(0.30, 0.10, hue)), (1.0, oklch(0.10, 0.03, hue))],
                )
                .border(1.0, glass_rim())
                .center()
                .child(
                    col()
                        .gap(8.0)
                        .items_center()
                        .child(icon_film(36.0, FG.alpha(0.7)))
                        .child(text(item.name.clone()).font(type_section()).color(FG)),
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
                [(0.0, oklch(0.32, 0.10, hue)), (1.0, oklch(0.10, 0.03, hue))],
            )
            .border(1.0, glass_rim())
            .center()
            .child(icon_camera(40.0, FG.alpha(0.65)))
    };

    let dur_ms = item.duration_ms.unwrap_or(30_000.0);
    let trim_start_ms = dur_ms * s.media.trim_start_pct as f64;
    let trim_end_ms = dur_ms * s.media.trim_end_pct as f64;
    let id_fav = item.id.clone();
    let id_del = item.id.clone();
    let id_show = item.id.clone();
    let id_trim = item.id.clone();
    let id_discord = item.id.clone();
    let is_fav = item.favourite;

    let trim_bar = if item.kind == "video" {
        let track_w = 520.0_f32;
        let left_px = track_w * s.media.trim_start_pct;
        let sel_w = (track_w * (s.media.trim_end_pct - s.media.trim_start_pct)).max(12.0);
        Some(
            section_card("Trim & export", None)
                .child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .child(
                            text(format!(
                                "{} - {} (total {})",
                                format_duration_ms(trim_start_ms),
                                format_duration_ms(trim_end_ms),
                                format_duration_ms(dur_ms)
                            ))
                            .font(type_small())
                            .color(fg_muted()),
                        )
                        .child(
                            row()
                                .gap(6.0)
                                .child(glass_button("cap-trim-in", "In +5%", None).on_press(
                                    |s: &mut AppState| {
                                        s.media.trim_start_pct = (s.media.trim_start_pct + 0.05)
                                            .min(s.media.trim_end_pct - 0.05)
                                            .max(0.0);
                                    },
                                ))
                                .child(glass_button("cap-trim-out", "Out -5%", None).on_press(
                                    |s: &mut AppState| {
                                        s.media.trim_end_pct = (s.media.trim_end_pct - 0.05)
                                            .max(s.media.trim_start_pct + 0.05)
                                            .min(1.0);
                                    },
                                ))
                                .child(glass_button("cap-trim-reset", "Reset", None).on_press(
                                    |s: &mut AppState| {
                                        s.media.trim_start_pct = 0.0;
                                        s.media.trim_end_pct = 1.0;
                                    },
                                )),
                        ),
                )
                .child(
                    el().w(track_w)
                        .h(12.0)
                        .radius(6.0)
                        .fill(subtle_fill())
                        .border(1.0, hairline())
                        .child(
                            el().absolute(left_px, 0.0)
                                .w(sel_w)
                                .h(12.0)
                                .radius(6.0)
                                .fill(FG.alpha(0.75)),
                        ),
                )
                .child(
                    row()
                        .gap(8.0)
                        .child(
                            primary_button(
                                "cap-trim-go",
                                "Export clip",
                                Some(icon_scissors(13.0, PRIMARY_FG)),
                            )
                            .on_press(move |s: &mut AppState| {
                                s.capture_export_trim(&id_trim, "trim")
                            }),
                        )
                        .child(
                            glass_button("cap-trim-discord", "Export for Discord (10 MB)", None)
                                .on_press(move |s: &mut AppState| {
                                    s.capture_export_trim(&id_discord, "discord");
                                }),
                        ),
                ),
        )
    } else {
        None
    };

    col()
        .w_full()
        .gap(16.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    glass_button("cap-back", "All captures", Some(icon_back(13.0, FG)))
                        .on_press(|s: &mut AppState| s.close_capture_item()),
                )
                .child(
                    row()
                        .gap(8.0)
                        .child(
                            glass_button(
                                "cap-fav",
                                if is_fav { "Favorited" } else { "Favorite" },
                                Some(icon_star(13.0, if is_fav { warn() } else { FG })),
                            )
                            .on_press(move |s: &mut AppState| {
                                s.capture_item_action(
                                    &id_fav,
                                    if is_fav { "unfavourite" } else { "favourite" },
                                );
                            }),
                        )
                        .child(
                            glass_button("cap-show", "Show in folder", Some(icon_folder(13.0, FG)))
                                .on_press(move |s: &mut AppState| {
                                    s.capture_item_action(&id_show, "show");
                                }),
                        )
                        .child(
                            glass_button("cap-del", "Delete", Some(icon_trash(13.0, bad())))
                                .on_press(move |s: &mut AppState| {
                                    s.capture_item_action(&id_del, "delete");
                                }),
                        ),
                ),
        )
        .child(stage)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    col()
                        .gap(3.0)
                        .child(text(item.name.clone()).font(type_title()).color(FG))
                        .child(
                            text(format!(
                                "{} · {} · {}",
                                item.game,
                                format_bytes(item.size),
                                item.created.get(..10).unwrap_or(&item.created)
                            ))
                            .font(type_small())
                            .color(fg_muted()),
                        ),
                )
                .children((item.kind == "video").then(|| {
                    row()
                        .gap(8.0)
                        .child(
                            primary_button(
                                "cap-play-toggle",
                                if s.media.clip_playing {
                                    "Pause"
                                } else {
                                    "Play"
                                },
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
                                "cap-mute",
                                if s.media.clip_muted { "Unmute" } else { "Mute" },
                                Some(if s.media.clip_muted {
                                    icon_volume_off(13.0, FG)
                                } else {
                                    icon_volume(13.0, FG)
                                }),
                            )
                            .on_press(|s: &mut AppState| s.toggle_clip_mute()),
                        )
                })),
        )
        .children(trim_bar)
}

fn empty_state(s: &AppState) -> El {
    let status = s.media.capture_status.as_ref();
    let present = status
        .and_then(|st| st.state.as_ref())
        .map_or(status.is_none_or(|st| st.present), |l| l.present);
    if !present {
        return col().grow(1.0).w_full().center().child(
            text("Exo Capture isn't installed")
                .font(TextStyle::new(20.0, 600.0))
                .color(FG),
        );
    }

    let cfg = status.and_then(|st| st.settings.as_ref());
    let way_pill =
        |key: &str, label: &str, icon: El, keys: Option<String>, action: &'static str| {
            let mut r = row()
                .key(key)
                .role("button")
                .label(label)
                .focusable(true)
                .hit()
                .press_on_down()
                .h(36.0)
                .px(14.0)
                .gap(8.0)
                .radius(RADIUS_PILL)
                .fill(subtle_fill())
                .border(1.0, glass_rim())
                .hover(hover_fill())
                .center_y()
                .on_press(move |s: &mut AppState| s.capture_act(action))
                .child(icon)
                .child(text(label).font(type_section()).color(FG));
            if let Some(k) = keys
                && !k.is_empty()
            {
                r = r.child(text(k).font(type_small()).color(fg_faint()));
            }
            r
        };

    col()
        .grow(1.0)
        .w_full()
        .center()
        .gap(8.0)
        .child(
            text("No captures yet")
                .font(TextStyle::new(20.0, 600.0))
                .color(FG),
        )
        .child(
            row()
                .mt(14.0)
                .gap(8.0)
                .flex_wrap()
                .justify_center()
                .child(way_pill(
                    "cap-way-replay",
                    "Save replay",
                    icon_replay(16.0, FG),
                    cfg.map(|c| keys_label(&c.save_replay_keys))
                        .or_else(|| Some("Ctrl + Alt + F10".into())),
                    "saveReplay",
                ))
                .child(way_pill(
                    "cap-way-record",
                    "Record",
                    icon_record_dot(16.0, FG),
                    cfg.map(|c| keys_label(&c.record_keys))
                        .or_else(|| Some("Ctrl + Alt + F9".into())),
                    "record",
                ))
                .child(way_pill(
                    "cap-way-snip",
                    "Snip",
                    icon_scissors(16.0, FG),
                    cfg.map(|c| keys_label(&c.snip_keys))
                        .or_else(|| Some("Ctrl + Alt + F8".into())),
                    "snip",
                )),
        )
}

fn settings_body(s: &AppState) -> El {
    let cfg = s
        .media
        .capture_status
        .as_ref()
        .and_then(|st| st.settings.as_ref());
    let mode = cfg.map(|c| c.replay_mode.as_str()).unwrap_or("off");

    let mode_row = row().gap(6.0).children(
        [
            ("cap-mode-off", "off", "Off"),
            ("cap-mode-games", "games", "In games"),
            ("cap-mode-always", "always", "Always"),
        ]
        .into_iter()
        .map(|(key, id, label)| {
            filter_pill(key, label.into(), mode == id)
                .on_press(move |s: &mut AppState| s.capture_set_replay_mode(id))
        }),
    );

    col().w_full().max_w(760.0).gap(16.0).child(
        section_card(
            "Instant replay",
            Some("Keep a rolling buffer of recent gameplay.".into()),
        )
        .child(mode_row)
        .children(cfg.map(|c| {
            row()
                .gap(16.0)
                .flex_wrap()
                .child(status_badge(
                    format!("{}s buffer", c.replay_seconds),
                    BadgeTone::Neutral,
                ))
                .child(status_badge(
                    format!("{} fps", c.max_fps),
                    BadgeTone::Neutral,
                ))
                .child(status_badge(
                    format!("Quality {}", c.quality),
                    BadgeTone::Neutral,
                ))
        })),
    )
}

pub fn view(s: &AppState, scale: f32) -> El {
    let setting = s.media.captures_settings_open;
    let present = s
        .media
        .capture_status
        .as_ref()
        .and_then(|st| st.state.as_ref())
        .is_none_or(|l| l.present);

    let mut head_actions = row().gap(8.0).center_y();
    if setting {
        head_actions = head_actions.child(
            glass_button(
                "cap-settings-back",
                "All captures",
                Some(icon_back(14.0, FG)),
            )
            .on_press(|s: &mut AppState| s.media.captures_settings_open = false),
        );
    } else if s.media.open_capture_id.is_none() {
        if present {
            head_actions = head_actions.child(
                glass_button(
                    "cap-open-settings",
                    "Settings",
                    Some(icon_settings(14.0, FG)),
                )
                .on_press(|s: &mut AppState| s.media.captures_settings_open = true),
            );
        }
        head_actions = head_actions.child(
            glass_button("cap-open-notes", "Notes", Some(icon_notes(14.0, FG)))
                .on_press(|s: &mut AppState| s.load_captures()),
        );
        if present {
            head_actions = head_actions.child(
                primary_button(
                    "cap-act-snip",
                    "Snip",
                    Some(icon_scissors(14.0, PRIMARY_FG)),
                )
                .on_press(|s: &mut AppState| s.capture_act("snip")),
            );
        }
    }
    head_actions = head_actions.child(
        el().key("cap-close")
            .role("button")
            .label("Close Captures")
            .focusable(true)
            .hit()
            .press_on_down()
            .size(32.0, 32.0)
            .radius(RADIUS_PILL)
            .fill(subtle_fill())
            .border(1.0, glass_rim())
            .hover(hover_fill())
            .center()
            .on_press(|s: &mut AppState| s.close_captures())
            .child(icon_close(16.0, FG)),
    );

    let header = row()
        .w_full()
        .px(20.0)
        .pt(20.0)
        .pb(14.0)
        .justify_between()
        .center_y()
        .child(
            text(if setting {
                "Capture settings"
            } else {
                "Captures"
            })
            .font(type_page())
            .color(FG),
        )
        .child(head_actions);

    let open_item = s
        .media
        .open_capture_id
        .as_deref()
        .and_then(|id| s.media.capture_items.iter().find(|c| c.id == id));

    let body = if setting {
        col()
            .key("cap-settings-scroll")
            .grow(1.0)
            .w_full()
            .min_h(0.0)
            .px(20.0)
            .pb(20.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(settings_body(s))
    } else if let Some(item) = open_item {
        col()
            .key("cap-theater-scroll")
            .grow(1.0)
            .w_full()
            .min_h(0.0)
            .px(20.0)
            .pb(20.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(clip_theater(s, item, scale))
    } else if s.media.capture_items.is_empty() {
        empty_state(s)
    } else {
        let filtered: Vec<&CaptureItem> = s
            .media
            .capture_items
            .iter()
            .filter(|c| match s.media.capture_filter {
                CaptureFilter::All => true,
                CaptureFilter::Videos => c.kind == "video",
                CaptureFilter::Screenshots => c.kind == "image",
                CaptureFilter::Favorites => c.favourite,
            })
            .collect();

        let chips = row().w_full().pb(12.0).gap(6.0).center_y().children(
            [
                (CaptureFilter::All, "cap-filter-all", "All"),
                (CaptureFilter::Videos, "cap-filter-clips", "Clips"),
                (
                    CaptureFilter::Screenshots,
                    "cap-filter-shots",
                    "Screenshots",
                ),
                (CaptureFilter::Favorites, "cap-filter-favs", "Favorites"),
            ]
            .into_iter()
            .map(|(f, key, label)| {
                filter_pill(key, label.into(), s.media.capture_filter == f)
                    .on_press(move |s: &mut AppState| s.media.capture_filter = f)
            }),
        );

        let mut days: Vec<(String, Vec<&CaptureItem>)> = Vec::new();
        for item in filtered {
            let day_key = item.created.get(..10).unwrap_or("Recent").to_string();
            if let Some(last) = days.last_mut()
                && last.0 == day_key
            {
                last.1.push(item);
            } else {
                days.push((day_key, vec![item]));
            }
        }

        let mut list = col().w_full().gap(20.0);
        if days.is_empty() {
            list = list.child(
                col().w_full().py(60.0).items_center().child(
                    text("No captures match this filter.")
                        .font(type_body())
                        .color(fg_muted()),
                ),
            );
        } else {
            for (day, items) in days {
                let mut tiles: Vec<El> = items
                    .into_iter()
                    .map(|item| clip_card(item, s, scale))
                    .collect();
                while tiles.len() < 5 {
                    tiles.push(el().w_full().min_w(0.0));
                }
                list = list.child(
                    col()
                        .w_full()
                        .gap(8.0)
                        .child(text(day).font(type_section()).color(FG))
                        .child(el().w_full().equal_columns(5).gap(3.0).children(tiles)),
                );
            }
        }

        col()
            .key("cap-gallery-scroll")
            .grow(1.0)
            .w_full()
            .min_h(0.0)
            .px(20.0)
            .pb(20.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(chips)
            .child(list)
    };

    let sheet = col()
        .key("captures-sheet")
        .w_full()
        .max_w(1280.0)
        .h_full()
        .max_h(900.0)
        .radius(RADIUS_SHEET)
        .clip_rounded(RADIUS_SHEET)
        .fill(BG)
        .border(1.0, glass_rim())
        .shadow(24.0, 64.0, BG.alpha(0.75))
        .child(header)
        .child(body);

    el().key("captures-room")
        .w_full()
        .h_full()
        .pt(60.0)
        .px(24.0)
        .pb(24.0)
        .center()
        .fill(BG.alpha(0.72))
        .child(sheet)
}
