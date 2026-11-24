use crate::media_state::{
    ListenAlongFriend, ListenAlongStatus, MusicMenu, MusicNav, MusicPanel, NowPlayingTab,
    can_listen_along, format_ago_ms, lyrics_active_index, lyrics_gaps, now_epoch_ms,
};
use crate::music::{
    ROW_RADIUS, icon_chevron_up, render_art, render_art_image, scaled_body, scaled_small,
    scaled_title, shelf_section, track_bg_fill,
};
use crate::state::AppState;
use exo_kit::{
    BG, ContextMenuSpec, FG, MenuAnchor, MenuDismissCb, MenuEntry, MenuItem, MenuSubmenuCb,
    PRESS_SCALE, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK, TRANSPARENT,
    bad, context_menu_layers, danger_button, fg_faint, fg_muted, filter_pill, glass_button,
    glass_rim, good, hairline, hover_fill, icon_chevron_down, icon_close, icon_link, icon_maximize,
    pressed_fill, primary_button, qr_code_view, switch_pill,
};
use exo_rpc::{PlayerCard, PlayerTrack, SpotifyAudio};
use rune::{Align, El, Phase, ScrollSpec, Side, col, edit_access, el, row, text, text_input};
use serde_json::json;
use std::rc::Rc;

const SPOTIFY_QUALITIES: [(i32, &str); 6] = [
    (0, "Automatic"),
    (1, "Low"),
    (2, "Normal"),
    (3, "High"),
    (4, "Very high"),
    (5, "Lossless"),
];

const SPEEDS: [i32; 9] = [5, 8, 10, 12, 15, 20, 25, 30, 35];
const SLEEP_MINUTES: [i64; 5] = [15, 30, 45, 60, 90];

pub(crate) fn side_panel(s: &AppState, panel: MusicPanel, scale: f32) -> El {
    let title = match panel {
        MusicPanel::NowPlaying => "Now playing",
        MusicPanel::Queue => "Queue",
        MusicPanel::Devices => "Play on",
        MusicPanel::Friends => "Friend activity",
        MusicPanel::Jam => {
            if s.media.music_service.has_jam() {
                "Jam"
            } else {
                "Listen along"
            }
        }
        MusicPanel::Sound => "Sound",
        MusicPanel::Credits => "Credits",
    };

    let header = row()
        .w_full()
        .mb(14.0)
        .justify_between()
        .center_y()
        .child(text(title).font(scaled_title(s)).color(FG))
        .child(
            el().key("music-panel-close")
                .role("button")
                .label("Close panel")
                .focusable(true)
                .hit()
                .press_on_down()
                .size(28.0, 28.0)
                .radius(RADIUS_PILL)
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| s.media.music_panel = None)
                .child(icon_close(14.0, fg_muted())),
        );

    let content = match panel {
        MusicPanel::NowPlaying => now_playing_panel(s, scale),
        MusicPanel::Queue => queue_panel(s, scale),
        MusicPanel::Devices => devices_panel(s),
        MusicPanel::Friends => friends_panel(s, scale),
        MusicPanel::Jam => jam_panel(s, scale),
        MusicPanel::Sound => sound_panel(s),
        MusicPanel::Credits => credits_panel(s),
    };

    col()
        .key("music-side-panel")
        .w(360.0)
        .h_full()
        .min_h(0.0)
        .mb(86.0)
        .padding(16.0)
        .radius(RADIUS_PANEL)
        .clip_rounded(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, glass_rim())
        .child(header)
        .child(
            col()
                .key("music-panel-scroll")
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(content),
        )
}

fn now_playing_panel(s: &AppState, scale: f32) -> El {
    let pb = s.media.playback.as_ref().filter(|p| p.has_track());
    let title = s
        .media
        .music_player_title
        .as_deref()
        .or_else(|| pb.map(|p| p.title.as_str()))
        .unwrap_or("Nothing playing");
    let art = if let Some(ref url) = s.media.music_player_image {
        render_art(s, url, None, RADIUS_CARD, false, scale)
    } else {
        render_art_image(s.current_playback_art(scale), None, RADIUS_CARD, false)
    };

    let mut head_text = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(4.0)
        .child(text(title).font(scaled_title(s)).color(FG).wrap());
    head_text = head_text.child(song_artists_row(s, pb, false));

    let head = row().w_full().gap(10.0).center_y().child(head_text).child(
        el().key("music-now-immersive")
            .role("button")
            .label("Immersive")
            .focusable(true)
            .hit()
            .press_on_down()
            .size(32.0, 32.0)
            .radius(RADIUS_PILL)
            .border(1.0, glass_rim())
            .hover(hover_fill())
            .center()
            .on_press(|s: &mut AppState| {
                s.media.music_immersion = true;
                let uri = s.media.music_track_uri.clone();
                if !uri.is_empty() {
                    s.load_music_lyrics(&uri);
                }
            })
            .child(icon_maximize(15.0, FG)),
    );

    let mut body = col().w_full().gap(14.0).child(art).child(head);
    if !s.media.music_track_uri.is_empty() {
        if s.media.music_service.has_related() {
            let is_related = s.media.music_now_tab == NowPlayingTab::Related;
            body = body.child(
                row()
                    .gap(8.0)
                    .child(
                        filter_pill("music-now-tab-lyrics", "Lyrics", !is_related).on_press(
                            |s: &mut AppState| {
                                s.media.music_now_tab = NowPlayingTab::Lyrics;
                                let uri = s.media.music_track_uri.clone();
                                s.load_music_lyrics(&uri);
                            },
                        ),
                    )
                    .child(
                        filter_pill("music-now-tab-related", "Related", is_related).on_press(
                            |s: &mut AppState| {
                                s.media.music_now_tab = NowPlayingTab::Related;
                                let uri = s.media.music_track_uri.clone();
                                s.load_music_related(&uri);
                            },
                        ),
                    ),
            );
            if is_related {
                body = body.child(related_view(s, scale));
            } else {
                body = body.child(lyrics_view(s, false));
            }
        } else {
            body = body.child(lyrics_view(s, false));
        }
    }
    body
}

pub(crate) fn song_artists_row(
    s: &AppState,
    pb: Option<&exo_rpc::MusicPlayback>,
    leave_immersive: bool,
) -> El {
    if !s.media.music_player_artists.is_empty() {
        let mut r = row().gap(4.0).wrap();
        for (i, a) in s.media.music_player_artists.iter().enumerate() {
            let label = if i > 0 {
                format!(", {}", a.name)
            } else {
                a.name.clone()
            };
            if !a.uri.is_empty() {
                let uri = a.uri.clone();
                let name = a.name.clone();
                r = r.child(
                    el().role("button")
                        .label(name.clone())
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| {
                            if leave_immersive {
                                s.media.music_immersion = false;
                            }
                            s.music_open_nav(MusicNav::Collection {
                                uri: uri.clone(),
                                kind: "artist".into(),
                                title: name.clone(),
                                edit: false,
                            });
                        })
                        .child(text(label).font(scaled_body(s)).color(fg_muted())),
                );
            } else {
                r = r.child(text(label).font(scaled_body(s)).color(fg_muted()));
            }
        }
        return r;
    }
    let artist = pb.map(|p| p.artist.as_str()).unwrap_or("");
    text(artist)
        .font(scaled_body(s))
        .color(fg_muted())
        .truncate()
}

fn related_view(s: &AppState, scale: f32) -> El {
    if s.media.music_related_loading {
        return text("Loading related music…")
            .font(scaled_body(s))
            .color(fg_muted());
    }
    if s.media.music_related_sections.is_empty() {
        if let Some(ref err) = s.media.music_related_error {
            return text(err.clone()).font(scaled_small(s)).color(bad()).wrap();
        }
        return text("Nothing related to this song")
            .font(scaled_body(s))
            .color(fg_muted());
    }
    col().w_full().gap(12.0).children(
        s.media
            .music_related_sections
            .iter()
            .enumerate()
            .map(|(i, sec)| shelf_section(s, 700 + i, sec, scale)),
    )
}

pub(crate) fn lyrics_view(s: &AppState, large: bool) -> El {
    if s.media.music_lyrics_loading {
        return text("Loading lyrics…")
            .font(scaled_body(s))
            .color(fg_muted())
            .py(12.0);
    }
    if s.media.music_lyrics_lines.is_empty() {
        if let Some(ref err) = s.media.music_lyrics_error {
            return text(err.clone()).font(scaled_small(s)).color(bad()).wrap();
        }
        return text("No lyrics for this song")
            .font(scaled_body(s))
            .color(fg_muted())
            .py(12.0);
    }
    let lines = &s.media.music_lyrics_lines;
    let synced = s.media.music_lyrics_synced;
    let pos_ms = s.media.current_position_ms();
    let active = lyrics_active_index(lines, synced, pos_ms);
    let timed = lines.iter().any(|l| !l.words.is_empty());
    let gaps = if timed {
        lyrics_gaps(lines)
    } else {
        vec![None; lines.len()]
    };
    let lead_agent = lines.iter().find_map(|l| l.agent.as_deref());
    let line_font = if large {
        scaled_title(s)
    } else {
        scaled_body(s)
    };

    let mut list = col()
        .key("music-lyrics-list")
        .w_full()
        .gap(if large { 14.0 } else { 8.0 });
    for (i, line) in lines.iter().enumerate() {
        if let Some((from, to)) = gaps[i] {
            let active_gap = pos_ms.is_some_and(|p| p >= from && p < to);
            let share = pos_ms
                .map(|p| ((p - from) / (to - from).max(1.0)).clamp(0.0, 1.0))
                .unwrap_or(0.0);
            let dots = row().gap(6.0).py(4.0).children((0..3).map(|d| {
                let lit = active_gap && share > (d as f64 / 3.0);
                el().size(6.0, 6.0)
                    .radius(3.0)
                    .fill(if lit { FG } else { fg_faint() })
            }));
            list = list.child(dots);
        }
        let is_active = active == Some(i);
        let is_past = active.is_some_and(|a| i < a);
        let color = if !synced || is_active {
            FG
        } else if is_past {
            fg_muted()
        } else {
            fg_faint()
        };
        let other_singer =
            lead_agent.is_some() && line.agent.as_deref().is_some_and(|a| Some(a) != lead_agent);
        let display = if line.text.is_empty() {
            " ".to_string()
        } else {
            line.text.clone()
        };
        let mut line_el = text(display).font(line_font).color(color).wrap();
        if synced && !line.text.is_empty() {
            let start = line.start;
            let mut btn = el()
                .key(format!("music-lyric-{i}"))
                .role("button")
                .label(line.text.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .w_full()
                .px(6.0)
                .py(4.0)
                .radius(RADIUS_SMALL)
                .hover(hover_fill())
                .on_press(move |s: &mut AppState| s.music_seek_ms(start))
                .child(line_el);
            if other_singer {
                btn = btn.items_end();
            }
            line_el = btn;
        }
        list = list.child(line_el);
    }
    list
}

fn queue_panel(s: &AppState, scale: f32) -> El {
    let svc = s.media.music_service;
    let mut body = col().w_full().gap(6.0);
    if let Some(ref err) = s.media.music_queue_error {
        body = body.child(text(err.clone()).font(scaled_small(s)).color(bad()).wrap());
    }
    if s.media.music_queue_failed {
        body = body.child(
            text("Didn't work. Try again.")
                .font(scaled_small(s))
                .color(bad()),
        );
    }

    let Some(ref q) = s.media.music_queue_data else {
        if s.media.music_queue_error.is_none() {
            body = body.child(
                text("Loading the queue…")
                    .font(scaled_body(s))
                    .color(fg_muted())
                    .py(20.0),
            );
        }
        return body;
    };

    if let Some(ref cur) = q.current {
        body = body
            .child(
                text("Now playing")
                    .font(scaled_small(s))
                    .color(fg_muted())
                    .mb(2.0),
            )
            .child(queue_compact_row(s, "music-q-cur", cur, None, scale));
    }

    if !q.queued.is_empty() {
        let moves = svc.has_edit_queue();
        let can_dequeue = svc.has_dequeue();
        let mut head = row()
            .w_full()
            .mt(10.0)
            .mb(2.0)
            .justify_between()
            .center_y()
            .child(
                text(if moves {
                    "Next in queue"
                } else {
                    "Playing next"
                })
                .font(scaled_small(s))
                .color(fg_muted()),
            );
        if moves || can_dequeue {
            head = head.child(
                el().key("music-q-clear")
                    .role("button")
                    .label("Clear")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .px(8.0)
                    .py(3.0)
                    .radius(RADIUS_SMALL)
                    .hover(hover_fill())
                    .on_press(|s: &mut AppState| s.music_queue_edit("clearQueue", json!({})))
                    .child(text("Clear").font(scaled_small(s)).color(fg_muted())),
            );
        }
        body = body.child(head);

        for (i, track) in q.queued.iter().enumerate() {
            let skip_uri = track.uri.clone();
            let skip_uid = track.uid.clone();
            let mut tools = row().gap(2.0).center_y();
            if moves {
                let up_disabled = i == 0;
                let down_disabled = i + 1 >= q.queued.len();
                let prev_ctx = i
                    .checked_sub(1)
                    .and_then(|p| q.queued.get(p))
                    .map(|t| format!("before|{}|{}", t.uri, t.uid))
                    .unwrap_or_default();
                let next_ctx = q
                    .queued
                    .get(i + 1)
                    .map(|t| format!("after|{}|{}", t.uri, t.uid))
                    .unwrap_or_default();
                let u_uri = track.uri.clone();
                let u_uid = track.uid.clone();
                let d_uri = track.uri.clone();
                let d_uid = track.uid.clone();
                tools = tools
                    .child(
                        el().key(format!("music-q-up-{i}"))
                            .role("button")
                            .label(format!("Move {} up", track.title))
                            .focusable(!up_disabled)
                            .disabled(up_disabled)
                            .hit()
                            .press_on_down()
                            .size(24.0, 24.0)
                            .radius(RADIUS_PILL)
                            .hover(hover_fill())
                            .opacity(if up_disabled { 0.3 } else { 1.0 })
                            .center()
                            .on_press(move |s: &mut AppState| {
                                if !up_disabled {
                                    s.music_queue_edit(
                                        "queueMove",
                                        json!({ "uri": u_uri, "uid": u_uid, "context": prev_ctx }),
                                    );
                                }
                            })
                            .child(icon_chevron_up(13.0, fg_muted())),
                    )
                    .child(
                        el().key(format!("music-q-dn-{i}"))
                            .role("button")
                            .label(format!("Move {} down", track.title))
                            .focusable(!down_disabled)
                            .disabled(down_disabled)
                            .hit()
                            .press_on_down()
                            .size(24.0, 24.0)
                            .radius(RADIUS_PILL)
                            .hover(hover_fill())
                            .opacity(if down_disabled { 0.3 } else { 1.0 })
                            .center()
                            .on_press(move |s: &mut AppState| {
                                if !down_disabled {
                                    s.music_queue_edit(
                                        "queueMove",
                                        json!({ "uri": d_uri, "uid": d_uid, "context": next_ctx }),
                                    );
                                }
                            })
                            .child(icon_chevron_down(13.0, fg_muted())),
                    );
            }
            if moves || can_dequeue {
                let rm_uri = track.uri.clone();
                let rm_uid = track.uid.clone();
                tools = tools.child(
                    el().key(format!("music-q-rm-{i}"))
                        .role("button")
                        .label(format!("Remove {} from the queue", track.title))
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .size(24.0, 24.0)
                        .radius(RADIUS_PILL)
                        .hover(hover_fill())
                        .center()
                        .on_press(move |s: &mut AppState| {
                            s.music_queue_edit("dequeue", json!({ "uri": rm_uri, "uid": rm_uid }));
                        })
                        .child(icon_close(12.0, fg_muted())),
                );
            }

            let song_btn = col()
                .key(format!("music-q-item-{i}"))
                .role("button")
                .label(track.title.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .grow(1.0)
                .min_w(0.0)
                .on_press(move |s: &mut AppState| {
                    s.music_queue_edit("skipTo", json!({ "uri": skip_uri, "uid": skip_uid }));
                })
                .child(
                    text(track.title.clone())
                        .font(scaled_body(s))
                        .color(FG)
                        .truncate(),
                )
                .child(
                    text(track.artist_line())
                        .font(scaled_small(s))
                        .color(fg_muted())
                        .truncate(),
                );

            body = body.child(
                row()
                    .w_full()
                    .min_h(44.0)
                    .px(6.0)
                    .py(4.0)
                    .gap(8.0)
                    .radius(ROW_RADIUS)
                    .hover(hover_fill())
                    .center_y()
                    .child(song_btn)
                    .child(tools),
            );
        }
    }

    let switchable = svc.has_autoplay();
    let autoplay = q.autoplay != Some(false);
    if switchable {
        body = body.child(
            row()
                .w_full()
                .mt(10.0)
                .py(6.0)
                .justify_between()
                .center_y()
                .child(text("Autoplay").font(scaled_body(s)).color(FG))
                .child(
                    switch_pill("music-q-autoplay", "Autoplay", autoplay).on_press(
                        move |s: &mut AppState| {
                            s.music_queue_edit("autoplay", json!({ "on": !autoplay }));
                        },
                    ),
                ),
        );
    }

    if (autoplay || !switchable) && !q.next.is_empty() {
        body = body.child(
            text("Next up")
                .font(scaled_small(s))
                .color(fg_muted())
                .mt(10.0)
                .mb(2.0),
        );
        for (i, t) in q.next.iter().enumerate() {
            let uri = t.uri.clone();
            let uid = t.uid.clone();
            body = body.child(queue_compact_row(
                s,
                &format!("music-q-next-{i}"),
                t,
                Some(Rc::new(move |s: &mut AppState| {
                    s.music_queue_edit("skipTo", json!({ "uri": uri, "uid": uid }));
                })),
                scale,
            ));
        }
    }

    if autoplay && !q.autoplayed.is_empty() {
        body = body.child(
            text("Autoplay")
                .font(scaled_small(s))
                .color(fg_muted())
                .mt(10.0)
                .mb(2.0),
        );
        for (i, t) in q.autoplayed.iter().enumerate() {
            let uri = t.uri.clone();
            let uid = t.uid.clone();
            body = body.child(queue_compact_row(
                s,
                &format!("music-q-auto-{i}"),
                t,
                Some(Rc::new(move |s: &mut AppState| {
                    s.music_queue_edit("skipTo", json!({ "uri": uri, "uid": uid }));
                })),
                scale,
            ));
        }
    }

    if q.current.is_none() && q.queued.is_empty() && q.next.is_empty() && q.autoplayed.is_empty() {
        body = body.child(
            text("Nothing queued")
                .font(scaled_body(s))
                .color(fg_muted())
                .py(20.0),
        );
    }

    if svc.has_history() && !s.media.music_queue_history.is_empty() {
        body = body.child(
            text("History")
                .font(scaled_small(s))
                .color(fg_muted())
                .mt(14.0)
                .mb(2.0),
        );
        for (i, t) in s.media.music_queue_history.iter().enumerate() {
            let uri = t.uri.clone();
            body = body.child(queue_compact_row(
                s,
                &format!("music-q-hist-{i}"),
                t,
                Some(Rc::new(move |s: &mut AppState| {
                    s.music_play_uri(&uri, None, None);
                })),
                scale,
            ));
        }
    }

    body
}

type OnRowClick = Rc<dyn Fn(&mut AppState)>;

fn queue_compact_row(
    s: &AppState,
    key: &str,
    track: &PlayerTrack,
    on_click: Option<OnRowClick>,
    scale: f32,
) -> El {
    let mut r = row()
        .key(key)
        .w_full()
        .min_h(44.0)
        .px(6.0)
        .py(4.0)
        .gap(10.0)
        .radius(ROW_RADIUS)
        .center_y()
        .child(render_art(
            s,
            &track.image,
            Some(36.0),
            RADIUS_SMALL,
            false,
            scale,
        ))
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .child(
                    text(track.title.clone())
                        .font(scaled_body(s))
                        .color(FG)
                        .truncate(),
                )
                .child(
                    text(track.artist_line())
                        .font(scaled_small(s))
                        .color(fg_muted())
                        .truncate(),
                ),
        );
    if let Some(cb) = on_click {
        r = r
            .role("button")
            .label(track.title.clone())
            .focusable(true)
            .hit()
            .press_on_down()
            .hover(hover_fill())
            .on_press(move |s: &mut AppState| cb(s));
    }
    r
}

fn devices_panel(s: &AppState) -> El {
    let mut body = col().w_full().gap(6.0);
    if let Some(ref err) = s.media.music_devices_error {
        body = body.child(text(err.clone()).font(scaled_small(s)).color(bad()).wrap());
    }
    if !s.media.music_devices_loaded && s.media.music_devices_error.is_none() {
        return body.child(
            text("Finding devices…")
                .font(scaled_body(s))
                .color(fg_muted())
                .py(20.0),
        );
    }
    if s.media.music_devices.is_empty() {
        return body.child(
            text("No devices found")
                .font(scaled_body(s))
                .color(fg_muted())
                .py(20.0),
        );
    }
    for (i, dev) in s.media.music_devices.iter().enumerate() {
        let name = if dev.local {
            "This computer".to_string()
        } else {
            dev.name.clone()
        };
        let sub = if dev.active {
            "Playing".to_string()
        } else {
            dev.r#type.replace('_', " ")
        };
        let id = dev.id.clone();
        let mut r = row()
            .key(format!("music-device-{i}"))
            .role("button")
            .label(name.clone())
            .focusable(true)
            .hit()
            .press_on_down()
            .w_full()
            .px(10.0)
            .py(8.0)
            .radius(ROW_RADIUS)
            .hover(hover_fill())
            .on_press(move |s: &mut AppState| s.music_transfer_device(&id))
            .child(
                col()
                    .grow(1.0)
                    .min_w(0.0)
                    .gap(2.0)
                    .child(
                        text(name)
                            .font(scaled_body(s))
                            .color(if dev.active { good() } else { FG })
                            .truncate(),
                    )
                    .child(text(sub).font(scaled_small(s)).color(fg_muted()).truncate()),
            );
        if dev.active {
            r = r.fill(pressed_fill());
        }
        body = body.child(r);
    }
    body
}

fn friends_panel(s: &AppState, scale: f32) -> El {
    let mut body = col().w_full().gap(8.0);
    if let Some(ref err) = s.media.music_friends_error {
        body = body.child(text(err.clone()).font(scaled_small(s)).color(bad()).wrap());
    }
    if !s.media.music_friends_loaded && s.media.music_friends_error.is_none() {
        return body.child(
            text("Loading friend activity…")
                .font(scaled_body(s))
                .color(fg_muted())
                .py(20.0),
        );
    }
    if s.media.music_friends.is_empty() {
        return body.child(
            text("No friend activity")
                .font(scaled_body(s))
                .color(fg_muted())
                .py(20.0),
        );
    }
    let now = now_epoch_ms();
    for (i, f) in s.media.music_friends.iter().enumerate() {
        let is_now = now - f.at < 15 * 60_000;
        let when = if is_now {
            "Now".to_string()
        } else {
            format_ago_ms(f.at, now)
        };
        let track_uri = f.track.uri.clone();
        let ctx_uri = f.track.context.as_ref().map(|c| c.uri.clone());

        let mut text_col = col().grow(1.0).min_w(0.0).gap(2.0).child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    text(f.user.name.clone())
                        .font(scaled_body(s))
                        .color(FG)
                        .truncate(),
                )
                .child(text(when).font(scaled_small(s)).color(if is_now {
                    good()
                } else {
                    fg_muted()
                })),
        );

        let mut track_row = row().gap(4.0).wrap().child(
            el().role("button")
                .label(f.track.title.clone())
                .hit()
                .press_on_down()
                .on_press(move |s: &mut AppState| {
                    s.music_play_uri(&track_uri, ctx_uri.as_deref(), None);
                })
                .child(
                    text(f.track.title.clone())
                        .font(scaled_small(s))
                        .color(fg_muted())
                        .truncate(),
                ),
        );
        if let Some(ref artist) = f.track.artist {
            let a_uri = artist.uri.clone();
            let a_name = artist.name.clone();
            track_row = track_row
                .child(text("·").font(scaled_small(s)).color(fg_faint()))
                .child(
                    el().role("button")
                        .label(a_name.clone())
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| {
                            if !a_uri.is_empty() {
                                s.music_open_nav(MusicNav::Collection {
                                    uri: a_uri.clone(),
                                    kind: "artist".into(),
                                    title: a_name.clone(),
                                    edit: false,
                                });
                            }
                        })
                        .child(
                            text(artist.name.clone())
                                .font(scaled_small(s))
                                .color(fg_muted())
                                .truncate(),
                        ),
                );
        }
        text_col = text_col.child(track_row);

        if let Some(ref ctx) = f.track.context
            && let Some(kind) = ctx
                .uri
                .strip_prefix("spotify:")
                .and_then(|r| r.split(':').next())
                .filter(|k| matches!(*k, "playlist" | "album" | "artist"))
        {
            let c_uri = ctx.uri.clone();
            let c_kind = kind.to_string();
            let c_name = ctx.name.clone();
            text_col = text_col.child(
                el().role("button")
                    .label(c_name.clone())
                    .hit()
                    .press_on_down()
                    .on_press(move |s: &mut AppState| {
                        s.music_open_nav(MusicNav::Collection {
                            uri: c_uri.clone(),
                            kind: c_kind.clone(),
                            title: c_name.clone(),
                            edit: false,
                        });
                    })
                    .child(
                        text(ctx.name.clone())
                            .font(scaled_small(s))
                            .color(fg_faint())
                            .truncate(),
                    ),
            );
        }

        body = body.child(
            row()
                .key(format!("music-friend-{i}"))
                .w_full()
                .gap(10.0)
                .py(6.0)
                .center_y()
                .child(render_art(
                    s,
                    &f.user.image,
                    Some(36.0),
                    RADIUS_PILL,
                    false,
                    scale,
                ))
                .child(text_col),
        );
    }
    body
}

fn jam_panel(s: &AppState, scale: f32) -> El {
    let spotify = s.media.music_service.has_jam();
    let mut body = col().w_full().gap(14.0);
    if spotify {
        body = body.child(spotify_jam_section(s, scale));
    }
    body.child(listen_along_section(s, spotify, scale))
}

fn spotify_jam_section(s: &AppState, scale: f32) -> El {
    let mut c = col().w_full().gap(10.0);
    if let Some(ref err) = s.media.music_jam_error {
        c = c.child(text(err.clone()).font(scaled_small(s)).color(bad()).wrap());
    }
    if !s.media.music_jam_loaded && s.media.music_jam_error.is_none() {
        return c.child(text("Loading Jam…").font(scaled_body(s)).color(fg_muted()));
    }

    if let Some(ref session) = s.media.music_jam_session {
        let copied = s.media.music_clock_s < s.media.music_jam_copied_until_s;
        let mut actions = row().gap(8.0).wrap();
        if !session.link.is_empty() {
            let link = session.link.clone();
            actions = actions.child(
                glass_button(
                    "music-jam-copy",
                    if copied { "Copied" } else { "Copy invite link" },
                    Some(icon_link(14.0, FG)),
                )
                .on_press_tx(move |s: &mut AppState, tx| {
                    tx.copy(link.clone());
                    s.media.music_jam_copied_until_s = s.media.music_clock_s + 2.0;
                }),
            );
        }
        if session.owner {
            if s.media.music_jam_end_armed {
                actions = actions.child(
                    danger_button("music-jam-end-confirm", "End it for everyone?", None)
                        .disabled(s.media.music_jam_busy)
                        .on_press(|s: &mut AppState| s.music_jam_action("jamEnd", json!({}))),
                );
            } else {
                actions = actions.child(
                    glass_button("music-jam-end", "End", None)
                        .disabled(s.media.music_jam_busy)
                        .on_press(|s: &mut AppState| s.media.music_jam_end_armed = true),
                );
            }
        } else {
            actions = actions.child(
                glass_button("music-jam-leave", "Leave", None)
                    .disabled(s.media.music_jam_busy)
                    .on_press(|s: &mut AppState| s.music_jam_action("jamLeave", json!({}))),
            );
        }
        c = c.child(actions);

        if !session.link.is_empty() {
            c = c.child(
                el().w_full()
                    .py(6.0)
                    .center()
                    .child(qr_code_view(&session.link, 160.0)),
            );
        }

        if session.owner
            && let Some(gp) = session.guests_play
        {
            c = c.child(sound_row(
                s,
                "Let others change what's playing",
                None,
                switch_pill(
                    "music-jam-guests-play",
                    "Let others change what's playing",
                    gp,
                )
                .on_press(move |s: &mut AppState| {
                    s.music_jam_action("jamGuestsPlay", json!({ "on": !gp }));
                }),
            ));
        }
        if session.owner
            && let Some(gv) = session.guest_volume
        {
            c = c.child(sound_row(
                s,
                "Let others change the volume",
                None,
                switch_pill("music-jam-guest-vol", "Let others change the volume", gv).on_press(
                    move |s: &mut AppState| {
                        s.music_jam_action("jamGuestVolume", json!({ "on": !gv }));
                    },
                ),
            ));
        }

        let count_lbl = if session.members.len() == 1 {
            "Just you".to_string()
        } else {
            format!("{} listening", session.members.len())
        };
        c = c.child(
            text(count_lbl)
                .font(scaled_small(s))
                .color(fg_muted())
                .mt(4.0),
        );
        for (i, m) in session.members.iter().enumerate() {
            let role = if m.host {
                "Host"
            } else if m.me {
                "You"
            } else {
                "Guest"
            };
            let mut mrow = row()
                .key(format!("music-jam-member-{i}"))
                .w_full()
                .gap(10.0)
                .py(4.0)
                .center_y()
                .child(render_art(
                    s,
                    &m.image,
                    Some(32.0),
                    RADIUS_PILL,
                    false,
                    scale,
                ))
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .child(
                            text(m.name.clone())
                                .font(scaled_body(s))
                                .color(FG)
                                .truncate(),
                        )
                        .child(text(role).font(scaled_small(s)).color(fg_muted())),
                );
            if session.can_remove && !m.me && !m.host {
                let mid = m.id.clone();
                mrow = mrow.child(
                    el().key(format!("music-jam-rm-{i}"))
                        .role("button")
                        .label(format!("Remove {}", m.name))
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .size(26.0, 26.0)
                        .radius(RADIUS_PILL)
                        .hover(hover_fill())
                        .center()
                        .on_press(move |s: &mut AppState| {
                            s.music_jam_action("jamRemove", json!({ "uid": mid }));
                        })
                        .child(icon_close(13.0, fg_muted())),
                );
            }
            c = c.child(mrow);
        }
    } else if s.media.music_jam_experience.as_deref() == Some("NONE") {
        c = c.child(
            text("Not available on this account")
                .font(scaled_body(s))
                .color(fg_muted()),
        );
    } else {
        c = c
            .child(
                primary_button("music-jam-start", "Start a Jam", None)
                    .disabled(s.media.music_jam_busy)
                    .on_press(|s: &mut AppState| s.music_jam_action("jamStart", json!({}))),
            )
            .child(
                text("Join a Jam")
                    .font(scaled_small(s))
                    .color(fg_muted())
                    .mt(6.0),
            )
            .child(
                row()
                    .w_full()
                    .gap(8.0)
                    .center_y()
                    .child(
                        row()
                            .grow(1.0)
                            .h(34.0)
                            .px(10.0)
                            .gap(6.0)
                            .radius(RADIUS_PILL)
                            .border(1.0, glass_rim())
                            .center_y()
                            .child(icon_link(14.0, fg_muted()))
                            .child(
                                text_input(&s.media.music_jam_join_edit, scaled_body(s))
                                    .key("music-jam-join-input")
                                    .label("Jam link")
                                    .grow(1.0)
                                    .single_line()
                                    .color(FG)
                                    .placeholder("Paste a Jam link", fg_muted())
                                    .on_edit(edit_access(|s: &mut AppState| {
                                        s.media.music_jam_join_wrong = false;
                                        &mut s.media.music_jam_join_edit
                                    }))
                                    .on_submit(|s: &mut AppState, _| s.music_jam_submit_join()),
                            ),
                    )
                    .child(
                        glass_button("music-jam-join-btn", "Join", None)
                            .disabled(
                                s.media.music_jam_busy
                                    || s.media.music_jam_join_edit.text.trim().is_empty(),
                            )
                            .on_press(|s: &mut AppState| s.music_jam_submit_join()),
                    ),
            );
        if s.media.music_jam_join_wrong {
            c = c.child(
                text("That is not a Jam link. Paste the whole link someone shared.")
                    .font(scaled_small(s))
                    .color(bad())
                    .wrap(),
            );
        }
    }
    c
}

fn listen_along_section(s: &AppState, titled: bool, scale: f32) -> El {
    let mut c = col().w_full().gap(8.0);
    if titled {
        c = c.child(
            text("Listen along")
                .font(scaled_small(s))
                .color(fg_muted())
                .mt(4.0),
        );
    }

    let along = &s.media.music_listen_along;
    if along.status != ListenAlongStatus::Off
        && let Some(ref friend) = along.friend
    {
        let status_text = match along.status {
            ListenAlongStatus::Finding => format!("Finding {}", along.title),
            ListenAlongStatus::Missing => {
                format!("{} isn't on {}", along.title, along.service_name)
            }
            ListenAlongStatus::Failed => {
                format!("{} couldn't play on {}", along.title, along.service_name)
            }
            ListenAlongStatus::Paused => "Paused".into(),
            ListenAlongStatus::Playing => [along.title.as_str(), along.artist.as_str()]
                .into_iter()
                .filter(|x| !x.is_empty())
                .collect::<Vec<_>>()
                .join(" · "),
            ListenAlongStatus::Off => String::new(),
        };
        c = c.child(
            row()
                .key("music-listen-along-active")
                .w_full()
                .gap(10.0)
                .py(6.0)
                .center_y()
                .child(render_art(
                    s,
                    friend.avatar.as_deref().unwrap_or(""),
                    Some(34.0),
                    RADIUS_PILL,
                    false,
                    scale,
                ))
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .child(
                            text(friend.name.clone())
                                .font(scaled_body(s))
                                .color(FG)
                                .truncate(),
                        )
                        .child(
                            text(status_text)
                                .font(scaled_small(s))
                                .color(fg_muted())
                                .truncate(),
                        ),
                )
                .child(
                    glass_button("music-listen-along-stop", "Stop", None)
                        .on_press(|s: &mut AppState| s.stop_listen_along(None)),
                ),
        );
    }

    let mut listening: Vec<(ListenAlongFriend, exo_rpc::DiscordRich)> = Vec::new();
    for cs in &s.media.music_call_songs {
        if can_listen_along(cs.rich.as_ref())
            && let Some(ref r) = cs.rich
        {
            listening.push((
                ListenAlongFriend {
                    id: cs.user_id.clone(),
                    name: cs.name.clone(),
                    avatar: cs.avatar.clone(),
                },
                r.clone(),
            ));
        }
    }
    for fr in &s.discord.friends {
        if fr.kind == "friend"
            && fr.status != "offline"
            && !listening.iter().any(|(f, _)| f.id == fr.id)
            && can_listen_along(fr.rich.as_ref())
            && let Some(ref r) = fr.rich
        {
            listening.push((
                ListenAlongFriend {
                    id: fr.id.clone(),
                    name: fr.name.clone(),
                    avatar: fr.avatar.clone(),
                },
                r.clone(),
            ));
        }
    }

    let following_id = along.friend.as_ref().map(|f| f.id.as_str());
    let others: Vec<_> = listening
        .into_iter()
        .filter(|(f, _)| Some(f.id.as_str()) != following_id)
        .collect();

    if !s.discord.status.signed_in {
        c = c.child(
            text("Sign in to Discord in Exo to listen along with friends")
                .font(scaled_body(s))
                .color(fg_muted())
                .wrap(),
        );
    } else if others.is_empty() && along.status == ListenAlongStatus::Off {
        c = c.child(
            text("No friends are listening right now")
                .font(scaled_body(s))
                .color(fg_muted())
                .wrap(),
        );
    } else {
        for (i, (friend, rich)) in others.into_iter().enumerate() {
            let sub = [
                rich.details.as_deref().unwrap_or(""),
                rich.state.as_deref().unwrap_or(""),
                rich.name.as_str(),
            ]
            .into_iter()
            .filter(|x| !x.is_empty())
            .collect::<Vec<_>>()
            .join(" · ");
            let f_click = friend.clone();
            let r_click = rich.clone();
            c = c.child(
                row()
                    .key(format!("music-along-friend-{i}"))
                    .w_full()
                    .gap(10.0)
                    .py(6.0)
                    .center_y()
                    .child(render_art(
                        s,
                        friend.avatar.as_deref().unwrap_or(""),
                        Some(34.0),
                        RADIUS_PILL,
                        false,
                        scale,
                    ))
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .child(
                                text(friend.name.clone())
                                    .font(scaled_body(s))
                                    .color(FG)
                                    .truncate(),
                            )
                            .child(text(sub).font(scaled_small(s)).color(fg_muted()).truncate()),
                    )
                    .child(
                        glass_button("music-along-listen", "Listen", None)
                            .key(format!("music-along-listen-{i}"))
                            .on_press(move |s: &mut AppState| {
                                s.start_listen_along(f_click.clone(), r_click.clone());
                            }),
                    ),
            );
        }
    }
    c
}

fn credits_panel(s: &AppState) -> El {
    let mut body = col().w_full().gap(12.0);
    if let Some(ref err) = s.media.music_credits_error {
        body = body.child(text(err.clone()).font(scaled_small(s)).color(bad()).wrap());
    }
    if s.media.music_credits_loading {
        return body.child(
            text("Loading credits…")
                .font(scaled_body(s))
                .color(fg_muted())
                .py(16.0),
        );
    }
    let Some(ref data) = s.media.music_credits_data else {
        return body.child(
            text("No credits for this song")
                .font(scaled_body(s))
                .color(fg_muted())
                .py(16.0),
        );
    };
    if let Some(ref t) = data.title
        && !t.is_empty()
    {
        body = body.child(text(t.clone()).font(scaled_small(s)).color(fg_muted()));
    }
    if data.groups.is_empty() {
        body = body.child(
            text("No credits for this song")
                .font(scaled_body(s))
                .color(fg_muted()),
        );
    }
    for group in &data.groups {
        let mut sec = col().w_full().gap(4.0).child(
            text(group.role.clone())
                .font(scaled_small(s))
                .color(fg_muted()),
        );
        for p in &group.people {
            let mut r = row().w_full().gap(4.0).wrap();
            if p.uri.starts_with("spotify:artist:") {
                let u = p.uri.clone();
                let n = p.name.clone();
                r = r.child(
                    el().role("button")
                        .label(n.clone())
                        .hit()
                        .press_on_down()
                        .on_press(move |s: &mut AppState| {
                            s.music_open_nav(MusicNav::Collection {
                                uri: u.clone(),
                                kind: "artist".into(),
                                title: n.clone(),
                                edit: false,
                            });
                        })
                        .child(text(p.name.clone()).font(scaled_body(s)).color(FG)),
                );
            } else {
                r = r.child(text(p.name.clone()).font(scaled_body(s)).color(FG));
            }
            if let Some(ref roles) = p.roles
                && !roles.is_empty()
            {
                r = r.child(
                    text(format!("· {roles}"))
                        .font(scaled_small(s))
                        .color(fg_muted()),
                );
            }
            sec = sec.child(r);
        }
        body = body.child(sec);
    }
    if let Some(ref src) = data.sources
        && !src.is_empty()
    {
        body = body.child(
            text(format!("Sources: {src}"))
                .font(scaled_small(s))
                .color(fg_faint())
                .wrap(),
        );
    }
    body
}

fn sound_panel(s: &AppState) -> El {
    let svc = s.media.music_service;
    let mut body = col().w_full().gap(2.0);
    if let Some(ref err) = s.media.music_sound_failed {
        body = body.child(text(err.clone()).font(scaled_small(s)).color(bad()).mb(6.0));
    }
    if svc.has_audio() {
        body = body.child(spotify_sound_section(s));
    } else if svc.has_quality() {
        body = body.child(service_quality_section(s));
    }
    if svc.has_speed() {
        let now_tenths = (s.media.music_speed * 10.0).round() as i32;
        let shown = if SPEEDS.contains(&now_tenths) {
            now_tenths
        } else {
            10
        };
        body = body.child(sound_row(
            s,
            "Playback speed",
            None,
            choice_btn(
                s,
                "music-sound-speed-btn",
                &format!("{}×", shown as f32 / 10.0),
                MusicMenu::SoundSpeed,
            ),
        ));
    }
    let sleep_left = s.media.sleep_minutes_left();
    let sleep_detail = sleep_left.map(|m| format!("Pauses in {m} min"));
    let sleep_btn_label = sleep_left
        .map(|m| format!("{m} min left"))
        .unwrap_or_else(|| "Off".into());
    body = body.child(sound_row(
        s,
        "Sleep timer",
        sleep_detail.as_deref(),
        choice_btn(
            s,
            "music-sound-sleep-btn",
            &sleep_btn_label,
            MusicMenu::SoundSleep,
        ),
    ));
    body
}

fn service_quality_section(s: &AppState) -> El {
    if s.media.music_sound_qualities.is_empty() {
        if let Some(ref err) = s.media.music_sound_error {
            return text(err.clone()).font(scaled_small(s)).color(bad()).wrap();
        }
        return text("Loading audio quality…")
            .font(scaled_body(s))
            .color(fg_muted())
            .py(8.0);
    }
    let cur = s
        .media
        .music_sound_qualities
        .iter()
        .find(|q| q.id == s.media.music_sound_quality);
    let label = cur.map(|q| q.name.as_str()).unwrap_or("Audio quality");
    let detail = cur.and_then(|q| q.detail.as_deref());
    sound_row(
        s,
        "Audio quality",
        detail,
        choice_btn(s, "music-sound-quality-btn", label, MusicMenu::SoundQuality),
    )
}

fn spotify_sound_section(s: &AppState) -> El {
    let Some(ref audio) = s.media.music_sound_spotify else {
        if let Some(ref err) = s.media.music_sound_error {
            return text(err.clone()).font(scaled_small(s)).color(bad()).wrap();
        }
        if s.media.music_sound_loading {
            return text("Loading sound settings…")
                .font(scaled_body(s))
                .color(fg_muted())
                .py(8.0);
        }
        return el();
    };
    let Some(q_val) = audio.quality else {
        return el();
    };
    let q_label = SPOTIFY_QUALITIES
        .iter()
        .find(|(v, _)| *v == q_val)
        .map(|(_, l)| *l)
        .unwrap_or("Automatic");
    let q_detail = (q_val == 5).then_some("Up to 24-bit/44.1 kHz FLAC");

    let mut c = col().w_full().child(sound_row(
        s,
        "Audio quality",
        q_detail,
        choice_btn(
            s,
            "music-sound-quality-btn",
            q_label,
            MusicMenu::SoundQuality,
        ),
    ));

    if let Some(ex) = audio.exclusive {
        c = c.child(sound_row(
            s,
            "Exclusive mode",
            None,
            switch_pill("music-sound-exclusive", "Exclusive mode", ex).on_press(
                move |s: &mut AppState| {
                    if let Some(ref mut a) = s.media.music_sound_spotify {
                        a.exclusive = Some(!ex);
                    }
                    s.music_set_spotify_audio("exclusive", json!({ "on": !ex }));
                },
            ),
        ));
    }

    let cf_on = audio.crossfade;
    let cf_sec = (audio.crossfade_ms as f32 / 1000.0)
        .round()
        .clamp(1.0, 12.0) as i32;
    let cf_detail = cf_on.then(|| format!("{cf_sec} s between songs"));
    c = c.child(sound_row(
        s,
        "Crossfade",
        cf_detail.as_deref(),
        switch_pill("music-sound-crossfade", "Crossfade", cf_on).on_press(
            move |s: &mut AppState| {
                if let Some(ref mut a) = s.media.music_sound_spotify {
                    a.crossfade = !cf_on;
                }
                s.music_set_spotify_audio("crossfade", json!({ "on": !cf_on }));
            },
        ),
    ));
    if cf_on {
        c = c.child(crossfade_slider(cf_sec));
    }

    let gapless = audio.gapless;
    c = c.child(sound_row(
        s,
        "Gapless playback",
        None,
        switch_pill("music-sound-gapless", "Gapless playback", gapless).on_press(
            move |s: &mut AppState| {
                if let Some(ref mut a) = s.media.music_sound_spotify {
                    a.gapless = !gapless;
                }
                s.music_set_spotify_audio("gapless", json!({ "on": !gapless }));
            },
        ),
    ));

    let automix = audio.auto_mix;
    c = c.child(sound_row(
        s,
        "AutoMix",
        None,
        switch_pill("music-sound-automix", "AutoMix", automix).on_press(move |s: &mut AppState| {
            if let Some(ref mut a) = s.media.music_sound_spotify {
                a.auto_mix = !automix;
                if !automix {
                    a.crossfade = true;
                }
            }
            s.music_set_spotify_audio("autoMix", json!({ "on": !automix }));
        }),
    ));

    let norm = audio.normalize;
    c = c.child(sound_row(
        s,
        "Normalize volume",
        None,
        switch_pill("music-sound-normalize", "Normalize volume", norm).on_press(
            move |s: &mut AppState| {
                if let Some(ref mut a) = s.media.music_sound_spotify {
                    a.normalize = !norm;
                }
                s.music_set_spotify_audio("normalize", json!({ "on": !norm }));
            },
        ),
    ));

    if let Some(smart) = audio.smart_shuffle {
        c = c.child(sound_row(
            s,
            "Smart Shuffle",
            None,
            switch_pill("music-sound-smart-shuffle", "Smart Shuffle", smart).on_press(
                move |s: &mut AppState| {
                    if let Some(ref mut a) = s.media.music_sound_spotify {
                        a.smart_shuffle = Some(!smart);
                    }
                    s.music_set_spotify_audio("smartShuffle", json!({ "on": !smart }));
                },
            ),
        ));
    }

    let mono = audio.mono;
    c = c.child(sound_row(
        s,
        "Mono audio",
        None,
        switch_pill("music-sound-mono", "Mono audio", mono).on_press(move |s: &mut AppState| {
            if let Some(ref mut a) = s.media.music_sound_spotify {
                a.mono = !mono;
            }
            s.music_set_spotify_audio("mono", json!({ "on": !mono }));
        }),
    ));

    if audio.equalizer.supported
        && let Some(eq_on) = audio.equalizer.enabled
        && let Some(ref filters) = audio.equalizer.filters
    {
        c = c.child(sound_row(
            s,
            "Equalizer",
            None,
            switch_pill("music-sound-eq", "Equalizer", eq_on).on_press(move |s: &mut AppState| {
                if let Some(ref mut a) = s.media.music_sound_spotify {
                    a.equalizer.enabled = Some(!eq_on);
                }
                s.music_set_spotify_audio("equalizer", json!({ "on": !eq_on }));
            }),
        ));
        if eq_on {
            c = c.child(spotify_eq_bands(s, audio, filters));
        }
    }

    c
}

fn crossfade_slider(sec: i32) -> El {
    let frac = ((sec - 1) as f32 / 11.0).clamp(0.0, 1.0);
    let width = 300.0;
    let fill_w = (width * frac).clamp(4.0, width);
    el().key("music-sound-crossfade-slider")
        .role("slider")
        .label("Crossfade length in seconds")
        .focusable(true)
        .hit()
        .w(width)
        .h(24.0)
        .my(4.0)
        .center_y()
        .on_drag(|s: &mut AppState, d, _| {
            let f = if d.w > 0.0 {
                (d.x / d.w).clamp(0.0, 1.0)
            } else {
                0.0
            };
            let secs = (1.0 + f * 11.0).round() as i32;
            if let Some(ref mut a) = s.media.music_sound_spotify {
                a.crossfade_ms = secs * 1000;
            }
            if d.phase == Phase::Up && !d.cancelled() {
                s.music_set_spotify_audio("crossfadeMs", json!({ "index": secs * 1000 }));
            }
        })
        .child(
            el().w(width)
                .h(4.0)
                .radius(2.0)
                .fill(track_bg_fill())
                .child(el().w(fill_w).h(4.0).radius(2.0).fill(FG)),
        )
        .child(
            el().absolute((fill_w - 6.0).clamp(0.0, width - 12.0), 6.0)
                .size(12.0, 12.0)
                .radius(6.0)
                .fill(FG),
        )
}

fn spotify_eq_bands(
    s: &AppState,
    _audio: &SpotifyAudio,
    filters: &[exo_rpc::SpotifyEqFilter],
) -> El {
    row()
        .w_full()
        .py(10.0)
        .justify_between()
        .children(filters.iter().enumerate().map(|(i, band)| {
            let gain = band.gain.round() as i32;
            let gain_txt = if gain > 0 {
                format!("+{gain}")
            } else {
                format!("{gain}")
            };
            let hz_txt = if band.frequency >= 1000.0 {
                format!("{} kHz", band.frequency / 1000.0)
            } else {
                format!("{} Hz", band.frequency.round() as i32)
            };
            let track_h = 96.0;
            let norm = ((gain as f32 + 12.0) / 24.0).clamp(0.0, 1.0);
            let fill_h = (track_h * norm).clamp(4.0, track_h);
            let key_id = band.key.clone();
            col()
                .key(format!("music-eq-band-{i}"))
                .items_center()
                .gap(6.0)
                .child(text(gain_txt).font(scaled_small(s)).color(fg_muted()))
                .child(
                    el().key(format!("music-eq-slider-{i}"))
                        .role("slider")
                        .label(format!("{hz_txt} gain"))
                        .focusable(true)
                        .hit()
                        .w(24.0)
                        .h(track_h)
                        .center()
                        .on_drag(move |s: &mut AppState, d, _| {
                            let f: f32 = if d.h > 0.0 {
                                (1.0 - d.y / d.h).clamp(0.0, 1.0)
                            } else {
                                0.5
                            };
                            let next_gain = (-12.0_f32 + f * 24.0_f32).round() as i32;
                            if let Some(ref mut a) = s.media.music_sound_spotify
                                && let Some(ref mut fs) = a.equalizer.filters
                                && let Some(b) = fs.iter_mut().find(|x| x.key == key_id)
                            {
                                b.gain = next_gain as f64;
                            }
                            if d.phase == Phase::Up && !d.cancelled() {
                                s.music_set_spotify_audio(
                                    "equalizerGain",
                                    json!({ "index": next_gain, "uid": key_id }),
                                );
                            }
                        })
                        .child(
                            el().w(4.0)
                                .h(track_h)
                                .radius(2.0)
                                .fill(track_bg_fill())
                                .child(
                                    el().absolute(0.0, track_h - fill_h)
                                        .w(4.0)
                                        .h(fill_h)
                                        .radius(2.0)
                                        .fill(FG),
                                ),
                        ),
                )
                .child(text(hz_txt).font(scaled_small(s)).color(fg_muted()))
        }))
}

fn sound_row(s: &AppState, label: &str, detail: Option<&str>, control: El) -> El {
    let mut copy = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(2.0)
        .child(text(label).font(scaled_body(s)).color(FG));
    if let Some(d) = detail
        && !d.is_empty()
    {
        copy = copy.child(text(d).font(scaled_small(s)).color(fg_muted()).wrap());
    }
    col()
        .w_full()
        .child(
            row()
                .w_full()
                .py(10.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .child(copy)
                .child(control),
        )
        .child(el().w_full().h(1.0).fill(hairline()))
}

fn choice_btn(s: &AppState, key: &str, label: &str, menu: MusicMenu) -> El {
    row()
        .key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .press_on_down()
        .h(30.0)
        .px(12.0)
        .gap(6.0)
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .border(1.0, glass_rim())
        .hover(hover_fill())
        .center_y()
        .on_press(move |s: &mut AppState| s.media.music_menu = Some(menu.clone()))
        .child(text(label).font(scaled_small(s)).color(FG))
        .child(icon_chevron_down(13.0, fg_muted()))
}

pub(crate) fn music_menus_overlay(s: &AppState) -> Vec<El> {
    let Some(ref menu) = s.media.music_menu else {
        return Vec::new();
    };
    let svc = s.media.music_service;
    let dismiss: MenuDismissCb<AppState> = Rc::new(|s: &mut AppState, _| {
        s.media.music_menu = None;
        s.media.music_delete_confirm_uri = None;
    });
    let no_sub: MenuSubmenuCb<AppState> = Rc::new(|_, _, _| {});

    match menu {
        MusicMenu::LibraryShowSort => {
            let filters = if s.media.music_library_top_filters.is_empty() {
                &s.media.music_library_filters
            } else {
                &s.media.music_library_top_filters
            };
            let mut entries = Vec::new();
            if !filters.is_empty() {
                entries.push(MenuEntry::Section("Show".into()));
                entries.push(MenuEntry::item(
                    MenuItem::new("music-lib-show-all", "All")
                        .checked(s.media.music_library_filter.is_empty())
                        .on_select(|s: &mut AppState, _| s.music_set_library_filter(String::new())),
                ));
                for f in filters {
                    let fid = f.id.clone();
                    entries.push(MenuEntry::item(
                        MenuItem::new(format!("music-lib-show-{}", f.id), f.name.clone())
                            .checked(s.media.music_library_filter == f.id)
                            .on_select(move |s: &mut AppState, _| {
                                s.music_set_library_filter(fid.clone());
                            }),
                    ));
                }
            }
            if !s.media.music_library_sorts.is_empty() {
                if !entries.is_empty() {
                    entries.push(MenuEntry::Separator);
                }
                entries.push(MenuEntry::Section("Sort by".into()));
                for so in &s.media.music_library_sorts {
                    let sid = so.id.clone();
                    entries.push(MenuEntry::item(
                        MenuItem::new(format!("music-lib-sort-{}", so.id), so.name.clone())
                            .checked(s.media.music_library_sort == so.id)
                            .on_select(move |s: &mut AppState, _| {
                                s.music_set_library_sort(sid.clone());
                            }),
                    ));
                }
            }
            build_music_menu(
                "music-menu-lib-sort",
                MenuAnchor::Element {
                    key: "music-lib-filter".into(),
                    side: Side::Bottom,
                    align: Align::Start,
                    gap: 4.0,
                },
                210.0,
                None,
                entries,
                dismiss,
                no_sub,
            )
        }
        MusicMenu::LibraryCreate => {
            let entries = vec![
                MenuEntry::item(
                    MenuItem::new("music-create-pl", "Create playlist")
                        .on_select(|s: &mut AppState, _| s.music_start_new_playlist(None)),
                ),
                MenuEntry::item(
                    MenuItem::new("music-create-folder", "Create folder")
                        .on_select(|s: &mut AppState, _| s.music_start_create_folder()),
                ),
            ];
            build_music_menu(
                "music-menu-create",
                MenuAnchor::Element {
                    key: "music-lib-create".into(),
                    side: Side::Bottom,
                    align: Align::End,
                    gap: 4.0,
                },
                180.0,
                None,
                entries,
                dismiss,
                no_sub,
            )
        }
        MusicMenu::LibraryRow {
            card,
            in_folder,
            anchor_key,
        } => {
            let entries = build_library_row_entries(s, card, *in_folder);
            build_music_menu(
                "music-menu-lib-row",
                MenuAnchor::Element {
                    key: anchor_key.clone(),
                    side: Side::Bottom,
                    align: Align::End,
                    gap: 4.0,
                },
                220.0,
                None,
                entries,
                dismiss,
                no_sub,
            )
        }
        MusicMenu::PageMore { anchor_key } => {
            let entries = build_page_more_entries(s);
            build_music_menu(
                "music-menu-page-more",
                MenuAnchor::Element {
                    key: anchor_key.clone(),
                    side: Side::Bottom,
                    align: Align::Start,
                    gap: 4.0,
                },
                220.0,
                None,
                entries,
                dismiss,
                no_sub,
            )
        }
        MusicMenu::TrackMore {
            track,
            index,
            total,
            playlist,
            show_album,
            liked_page,
            can_move,
            whole,
            anchor_key,
            point,
            open_submenu,
            ..
        } => {
            let entries = build_track_menu_entries(
                s,
                track,
                *index,
                *total,
                playlist.as_deref(),
                *show_album,
                *liked_page,
                *can_move,
                *whole,
            );
            let anchor = match point {
                Some((x, y)) => MenuAnchor::Point(*x, *y),
                None => MenuAnchor::Element {
                    key: anchor_key.clone(),
                    side: Side::Bottom,
                    align: Align::End,
                    gap: 4.0,
                },
            };
            let on_sub: MenuSubmenuCb<AppState> = Rc::new(|s: &mut AppState, idx, _| {
                if let Some(MusicMenu::TrackMore {
                    ref mut open_submenu,
                    ..
                }) = s.media.music_menu
                {
                    *open_submenu = idx;
                }
            });
            build_music_menu(
                "music-menu-track",
                anchor,
                230.0,
                *open_submenu,
                entries,
                dismiss,
                on_sub,
            )
        }
        MusicMenu::MorePanels => {
            let mut panels = vec![MusicPanel::Jam];
            if svc.has_friends() {
                panels.push(MusicPanel::Friends);
            }
            if svc.has_devices() {
                panels.push(MusicPanel::Devices);
            }
            panels.push(MusicPanel::Sound);
            let entries = panels
                .into_iter()
                .map(|p| {
                    let lbl = if p == MusicPanel::Jam && !svc.has_jam() {
                        "Listen along"
                    } else {
                        p.label()
                    };
                    MenuEntry::item(
                        MenuItem::new(format!("music-more-panel-{p:?}"), lbl)
                            .checked(s.media.music_panel == Some(p))
                            .on_select(move |s: &mut AppState, _| s.music_toggle_panel(p)),
                    )
                })
                .collect();
            build_music_menu(
                "music-menu-more-panels",
                MenuAnchor::Element {
                    key: "music-more".into(),
                    side: Side::Top,
                    align: Align::End,
                    gap: 6.0,
                },
                180.0,
                None,
                entries,
                dismiss,
                no_sub,
            )
        }
        MusicMenu::SongsSort => {
            let entries = s
                .media
                .music_songs_sorts
                .iter()
                .map(|o| {
                    let id = o.id.clone();
                    MenuEntry::item(
                        MenuItem::new(format!("music-songs-sort-{}", o.id), o.name.clone())
                            .checked(s.media.music_songs_sort == o.id)
                            .on_select(move |s: &mut AppState, _| {
                                s.media.music_songs_sort = id.clone();
                                s.media.music_menu = None;
                                s.load_music_songs(false);
                            }),
                    )
                })
                .collect();
            build_music_menu(
                "music-menu-songs-sort",
                MenuAnchor::Element {
                    key: "music-songs-sort-btn".into(),
                    side: Side::Bottom,
                    align: Align::End,
                    gap: 4.0,
                },
                200.0,
                None,
                entries,
                dismiss,
                no_sub,
            )
        }
        MusicMenu::SoundQuality => {
            let entries: Vec<MenuEntry<AppState>> = if svc.has_audio() {
                let lossless = s
                    .media
                    .music_sound_spotify
                    .as_ref()
                    .is_some_and(|a| a.lossless);
                let cur = s.media.music_sound_spotify.as_ref().and_then(|a| a.quality);
                SPOTIFY_QUALITIES
                    .iter()
                    .filter(|(v, _)| *v != 5 || lossless)
                    .map(|&(v, lbl)| {
                        MenuEntry::item(
                            MenuItem::new(format!("music-qual-{v}"), lbl)
                                .checked(cur == Some(v))
                                .on_select(move |s: &mut AppState, _| {
                                    if let Some(ref mut a) = s.media.music_sound_spotify {
                                        a.quality = Some(v);
                                    }
                                    s.media.music_menu = None;
                                    s.music_set_spotify_audio("quality", json!({ "index": v }));
                                }),
                        )
                    })
                    .collect()
            } else {
                s.media
                    .music_sound_qualities
                    .iter()
                    .map(|q| {
                        let qid = q.id.clone();
                        MenuEntry::item(
                            MenuItem::new(format!("music-qual-{}", q.id), q.name.clone())
                                .checked(s.media.music_sound_quality == q.id)
                                .on_select(move |s: &mut AppState, _| {
                                    s.music_set_service_quality(qid.clone());
                                }),
                        )
                    })
                    .collect()
            };
            build_music_menu(
                "music-menu-sound-quality",
                MenuAnchor::Element {
                    key: "music-sound-quality-btn".into(),
                    side: Side::Bottom,
                    align: Align::End,
                    gap: 4.0,
                },
                180.0,
                None,
                entries,
                dismiss,
                no_sub,
            )
        }
        MusicMenu::SoundSpeed => {
            let cur = (s.media.music_speed * 10.0).round() as i32;
            let entries = SPEEDS
                .iter()
                .map(|&v| {
                    MenuEntry::item(
                        MenuItem::new(format!("music-speed-{v}"), format!("{}×", v as f32 / 10.0))
                            .checked(cur == v)
                            .on_select(move |s: &mut AppState, _| s.music_set_speed(v)),
                    )
                })
                .collect();
            build_music_menu(
                "music-menu-sound-speed",
                MenuAnchor::Element {
                    key: "music-sound-speed-btn".into(),
                    side: Side::Bottom,
                    align: Align::End,
                    gap: 4.0,
                },
                140.0,
                None,
                entries,
                dismiss,
                no_sub,
            )
        }
        MusicMenu::SoundSleep => {
            let left = s.media.sleep_minutes_left();
            let mut entries = vec![MenuEntry::item(
                MenuItem::new(
                    "music-sleep-off",
                    if left.is_some() { "Turn off" } else { "Off" },
                )
                .checked(left.is_none())
                .on_select(|s: &mut AppState, _| s.music_set_sleep_minutes(None)),
            )];
            for m in SLEEP_MINUTES {
                entries.push(MenuEntry::item(
                    MenuItem::new(format!("music-sleep-{m}"), format!("{m} min"))
                        .on_select(move |s: &mut AppState, _| s.music_set_sleep_minutes(Some(m))),
                ));
            }
            build_music_menu(
                "music-menu-sound-sleep",
                MenuAnchor::Element {
                    key: "music-sound-sleep-btn".into(),
                    side: Side::Bottom,
                    align: Align::End,
                    gap: 4.0,
                },
                160.0,
                None,
                entries,
                dismiss,
                no_sub,
            )
        }
        MusicMenu::MiniVolume => Vec::new(),
    }
}

fn build_music_menu(
    id: &str,
    anchor: MenuAnchor,
    width: f32,
    open_submenu: Option<usize>,
    entries: Vec<MenuEntry<AppState>>,
    on_dismiss: MenuDismissCb<AppState>,
    on_submenu: MenuSubmenuCb<AppState>,
) -> Vec<El> {
    if entries.is_empty() {
        return Vec::new();
    }
    context_menu_layers(ContextMenuSpec {
        id: id.into(),
        anchor,
        width,
        blur: true,
        submenu_blur: true,
        open_submenu,
        return_focus: None,
        gamepad_focus: false,
        entries,
        on_dismiss,
        on_submenu,
    })
}

fn build_library_row_entries(
    s: &AppState,
    card: &PlayerCard,
    in_folder: bool,
) -> Vec<MenuEntry<AppState>> {
    let svc = s.media.music_service;
    let is_folder = card.kind == "folder";
    let own = is_folder || card.owned;
    let listed = card.kind != "collection" && card.kind != "episodes";
    let mut entries = Vec::new();

    if !is_folder {
        let c = card.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-lib-act-play", "Play")
                .on_select(move |s: &mut AppState, _| s.music_play_card(&c)),
        ));
    }
    if listed {
        let uri = card.uri.clone();
        let next_pin = !card.pinned;
        entries.push(MenuEntry::item(
            MenuItem::new(
                "music-lib-act-pin",
                if card.pinned { "Unpin" } else { "Pin" },
            )
            .on_select(move |s: &mut AppState, _| {
                s.music_run_library_action("pin", json!({ "uri": uri, "on": next_pin }));
            }),
        ));
    }
    if is_folder {
        let uri = card.uri.clone();
        let title = card.title.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-lib-act-rename", "Rename").on_select(
                move |s: &mut AppState, _| s.music_start_rename_folder(uri.clone(), title.clone()),
            ),
        ));
    }
    if let Some(link) = svc.share_url(&card.uri, card.url.as_deref()) {
        entries.push(MenuEntry::item(
            MenuItem::new("music-lib-act-link", "Copy link").on_select(
                move |s: &mut AppState, tx| {
                    tx.copy(link.clone());
                    s.media.music_library_said =
                        Some(("Link copied".into(), s.media.music_clock_s + 2.0));
                },
            ),
        ));
    }
    if card.kind == "playlist" || is_folder {
        let mut move_items = Vec::new();
        for other in &s.media.music_library {
            if other.kind == "folder" && other.uri != card.uri {
                let u = card.uri.clone();
                let target = other.uri.clone();
                move_items.push(MenuEntry::item(
                    MenuItem::new(format!("music-lib-mv-{}", other.uri), other.title.clone())
                        .on_select(move |s: &mut AppState, _| {
                            s.music_run_library_action(
                                "moveToFolder",
                                json!({ "uri": u, "context": target }),
                            );
                        }),
                ));
            }
        }
        if in_folder {
            let u = card.uri.clone();
            let parent = s
                .media
                .music_folder_ref
                .as_ref()
                .map(|f| f.parent.clone())
                .filter(|p| !p.is_empty());
            move_items.push(MenuEntry::item(
                MenuItem::new("music-lib-mv-out", "Out of this folder").on_select(
                    move |s: &mut AppState, _| {
                        let mut body = json!({ "uri": u });
                        if let Some(ref p) = parent {
                            body["context"] = json!(p);
                        }
                        s.music_run_library_action("moveToFolder", body);
                    },
                ),
            ));
        }
        if !move_items.is_empty() {
            entries.push(MenuEntry::Separator);
            entries.push(MenuEntry::Section("Move to folder".into()));
            entries.extend(move_items);
        }
    }
    if own || listed {
        entries.push(MenuEntry::Separator);
        let uri = card.uri.clone();
        let armed = s.media.music_delete_confirm_uri.as_deref() == Some(card.uri.as_str());
        let label = if armed {
            if is_folder {
                "Delete with its playlists?"
            } else if own {
                "Delete?"
            } else {
                "Remove?"
            }
        } else if own {
            "Delete"
        } else {
            "Remove from your library"
        };
        entries.push(MenuEntry::item(
            MenuItem::new("music-lib-act-del", label)
                .danger()
                .on_select(move |s: &mut AppState, _| {
                    if !armed {
                        s.media.music_delete_confirm_uri = Some(uri.clone());
                        return;
                    }
                    s.media.music_delete_confirm_uri = None;
                    if is_folder {
                        s.music_run_library_action("deleteFolder", json!({ "uri": uri }));
                    } else {
                        s.music_run_library_action("like", json!({ "uri": uri, "on": false }));
                    }
                }),
        ));
    }
    entries
}

fn build_page_more_entries(s: &AppState) -> Vec<MenuEntry<AppState>> {
    let svc = s.media.music_service;
    let Some(ref data) = s.media.music_page_data else {
        return Vec::new();
    };
    let Some(ref hdr) = data.header else {
        return Vec::new();
    };
    let page_kind = match &s.media.music_nav {
        MusicNav::Collection { kind, .. } => kind.as_str(),
        _ => "",
    };
    let own = page_kind == "playlist"
        && hdr.owned.or(hdr.editable).unwrap_or(false)
        && svc.has_playlists();
    let lists = matches!(page_kind, "album" | "playlist" | "show");
    let mix = svc.has_mix() && (hdr.radio.unwrap_or(false) || page_kind == "artist");
    let mut entries = Vec::new();

    if mix {
        let u1 = hdr.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-page-shuffle", "Shuffle play").on_select(
                move |s: &mut AppState, _| s.music_act_simple("shufflePlay", json!({ "uri": u1 })),
            ),
        ));
        let u2 = hdr.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-page-mix", "Start mix").on_select(move |s: &mut AppState, _| {
                s.music_act_simple("radio", json!({ "uri": u2 }))
            }),
        ));
    }
    if lists && svc.has_queue_lists() && svc.has_play_next() {
        let u = hdr.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-page-next", "Play next").on_select(move |s: &mut AppState, _| {
                s.music_act_simple("playNext", json!({ "uri": u }))
            }),
        ));
    }
    if lists && svc.has_queue_lists() && svc.has_enqueue() {
        let u = hdr.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-page-queue", "Add to queue").on_select(
                move |s: &mut AppState, _| s.music_act_simple("enqueue", json!({ "uri": u })),
            ),
        ));
    }
    if own {
        let u = hdr.uri.clone();
        let t = hdr.title.clone();
        let d = hdr.description.clone().unwrap_or_default();
        let p = hdr.privacy.clone();
        let img = hdr.image.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-page-edit", "Edit details").on_select(
                move |s: &mut AppState, _| {
                    s.music_open_edit_playlist_sheet(
                        u.clone(),
                        t.clone(),
                        d.clone(),
                        p.clone(),
                        img.clone(),
                    );
                },
            ),
        ));
        if let Some(pub_now) = hdr.public {
            let u = hdr.uri.clone();
            entries.push(MenuEntry::item(
                MenuItem::new(
                    "music-page-privacy",
                    if pub_now {
                        "Make private"
                    } else {
                        "Make public"
                    },
                )
                .on_select(move |s: &mut AppState, _| {
                    s.music_act_simple("privacy", json!({ "uri": u, "on": !pub_now }));
                }),
            ));
        }
        if let Some(collab) = hdr.collaborative {
            let u = hdr.uri.clone();
            entries.push(MenuEntry::item(
                MenuItem::new(
                    "music-page-collab",
                    if collab {
                        "Stop letting others add songs"
                    } else {
                        "Let others add songs"
                    },
                )
                .on_select(move |s: &mut AppState, _| {
                    s.music_act_simple("collaborative", json!({ "uri": u, "on": !collab }));
                }),
            ));
        }
    }
    if let Some(ref dl) = hdr.downloaded
        && (dl.is_boolean() || dl.as_str() == Some("downloading"))
    {
        let is_dl = dl.as_bool() == Some(true) || dl.as_str() == Some("downloading");
        let u = hdr.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new(
                "music-page-download",
                if is_dl { "Remove download" } else { "Download" },
            )
            .on_select(move |s: &mut AppState, _| {
                s.music_act_simple("download", json!({ "uri": u, "on": !is_dl }));
            }),
        ));
    }
    if svc.has_song_radio() {
        let from_artist = if page_kind == "artist" {
            Some((hdr.uri.clone(), hdr.title.clone()))
        } else if page_kind == "album" {
            hdr.artists
                .iter()
                .find(|a| !a.uri.is_empty())
                .map(|a| (a.uri.clone(), a.name.clone()))
        } else {
            None
        };
        if let Some((a_uri, a_name)) = from_artist {
            entries.push(MenuEntry::item(
                MenuItem::new("music-page-radio", "Go to artist radio").on_select(
                    move |s: &mut AppState, _| {
                        s.music_open_radio(&a_uri, format!("{a_name} Radio"));
                    },
                ),
            ));
        }
    }
    if let Some(link) = svc.share_url(&hdr.uri, hdr.url.as_deref()) {
        entries.push(MenuEntry::item(
            MenuItem::new("music-page-link", "Copy link").on_select(move |s: &mut AppState, tx| {
                tx.copy(link.clone());
                s.media.music_page_status = Some(("Copied".into(), s.media.music_clock_s + 2.0));
            }),
        ));
    }
    if own {
        entries.push(MenuEntry::Separator);
        let u = hdr.uri.clone();
        let armed = s.media.music_delete_confirm_uri.as_deref() == Some(hdr.uri.as_str());
        entries.push(MenuEntry::item(
            MenuItem::new(
                "music-page-delete",
                if armed { "Delete?" } else { "Delete" },
            )
            .danger()
            .on_select(move |s: &mut AppState, _| {
                if !armed {
                    s.media.music_delete_confirm_uri = Some(u.clone());
                    return;
                }
                s.media.music_delete_confirm_uri = None;
                s.music_act_simple("deletePlaylist", json!({ "uri": u }));
                s.music_playlist_deleted(&u);
            }),
        ));
    }
    entries
}

#[allow(clippy::too_many_arguments)]
fn build_track_menu_entries(
    s: &AppState,
    track: &PlayerTrack,
    index: usize,
    total: usize,
    playlist: Option<&str>,
    show_album: bool,
    liked_page: bool,
    can_move: bool,
    whole: bool,
) -> Vec<MenuEntry<AppState>> {
    let svc = s.media.music_service;
    let is_ep = track.is_episode();
    let is_liked = s
        .media
        .music_track_likes
        .get(&track.uri)
        .copied()
        .or(track.liked)
        .unwrap_or(liked_page);
    let is_less = s
        .media
        .music_track_less
        .get(&track.uri)
        .copied()
        .or(track.disliked)
        .unwrap_or(false);
    let in_lib = s
        .media
        .music_track_in_lib
        .get(&track.uri)
        .copied()
        .or(track.in_library)
        .unwrap_or(false);

    let set_fb = |s: &mut AppState, uri: String, word: &str| {
        s.media.music_track_feedback = Some((uri, word.into(), false, s.media.music_clock_s + 2.4));
    };

    let mut entries = Vec::new();
    if svc.has_mix() {
        let u = track.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-tr-mix", "Start mix").on_select(move |s: &mut AppState, _| {
                s.music_act_simple("radio", json!({ "uri": u }));
                set_fb(s, u.clone(), "Mix");
            }),
        ));
    }
    if svc.has_play_next() {
        let u = track.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-tr-next", "Play next").on_select(move |s: &mut AppState, _| {
                s.music_act_simple("playNext", json!({ "uri": u }));
                set_fb(s, u.clone(), "Plays next");
            }),
        ));
    }
    if svc.has_enqueue() {
        let u = track.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-tr-queue", "Add to queue").on_select(
                move |s: &mut AppState, _| {
                    s.music_act_simple("enqueue", json!({ "uri": u }));
                    set_fb(s, u.clone(), "Queued");
                },
            ),
        ));
    }
    {
        let u = track.uri.clone();
        let uid = track.uid.clone();
        let target_name = if is_ep && svc.has_shows() {
            "Your Episodes"
        } else {
            svc.liked_name()
        };
        let lbl = format!(
            "{} {target_name}",
            if is_liked { "Remove from" } else { "Save to" }
        );
        entries.push(MenuEntry::item(
            MenuItem::new("music-tr-like", lbl).on_select(move |s: &mut AppState, _| {
                let next = !is_liked;
                s.media.music_track_likes.insert(u.clone(), next);
                s.music_act_simple("like", json!({ "uri": u, "on": next }));
                if liked_page && is_liked {
                    let key = if uid.is_empty() {
                        u.clone()
                    } else {
                        uid.clone()
                    };
                    s.media.music_page_removed.push(key.clone());
                    s.media.music_songs_removed.push(key);
                } else {
                    set_fb(s, u.clone(), if is_liked { "Removed" } else { "Saved" });
                }
            }),
        ));
    }
    if svc.has_suggest_less() {
        let u = track.uri.clone();
        let next = !is_less;
        entries.push(MenuEntry::item(
            MenuItem::new(
                "music-tr-less",
                if is_less {
                    "Undo suggest less"
                } else {
                    "Suggest less"
                },
            )
            .on_select(move |s: &mut AppState, _| {
                s.media.music_track_less.insert(u.clone(), next);
                if next {
                    s.media.music_track_likes.insert(u.clone(), false);
                }
                s.music_act_simple("suggestLess", json!({ "uri": u, "on": next }));
                set_fb(s, u.clone(), if next { "Noted" } else { "Undone" });
            }),
        ));
    }
    if svc.has_library() {
        let u = track.uri.clone();
        let uid = track.uid.clone();
        let next = !in_lib;
        entries.push(MenuEntry::item(
            MenuItem::new(
                "music-tr-lib",
                if in_lib {
                    "Remove from library"
                } else {
                    "Add to library"
                },
            )
            .on_select(move |s: &mut AppState, _| {
                s.media.music_track_in_lib.insert(u.clone(), next);
                s.music_act_simple("library", json!({ "uri": u, "on": next }));
                if !next && u.contains(":library-songs:") {
                    let key = if uid.is_empty() {
                        u.clone()
                    } else {
                        uid.clone()
                    };
                    s.media.music_page_removed.push(key.clone());
                    s.media.music_songs_removed.push(key);
                } else {
                    set_fb(s, u.clone(), if next { "Added" } else { "Removed" });
                }
            }),
        ));
    }
    if svc.has_station() {
        let u = track.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-tr-station", "Create station").on_select(
                move |s: &mut AppState, _| {
                    s.music_act_simple("station", json!({ "uri": u }));
                    set_fb(s, u.clone(), "Station");
                },
            ),
        ));
    }
    if is_ep && let Some(played_now) = track.played {
        let u = track.uri.clone();
        let next = !played_now;
        entries.push(MenuEntry::item(
            MenuItem::new(
                "music-tr-played",
                if played_now {
                    "Mark as unplayed"
                } else {
                    "Mark as played"
                },
            )
            .on_select(move |s: &mut AppState, _| {
                s.music_act_simple("played", json!({ "uri": u, "on": next }));
                set_fb(s, u.clone(), if next { "Played" } else { "Unplayed" });
            }),
        ));
    }
    if let Some(pl) = playlist
        && !track.uid.is_empty()
    {
        let pl_owned = pl.to_string();
        let u = track.uri.clone();
        let uid = track.uid.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-tr-remove", "Remove from this playlist").on_select(
                move |s: &mut AppState, _| {
                    s.music_act_simple(
                        "removeFromPlaylist",
                        json!({ "uri": u, "context": pl_owned, "uid": uid }),
                    );
                    s.media.music_page_removed.push(uid.clone());
                },
            ),
        ));
        if can_move && index > 0 {
            let pl_u = pl.to_string();
            let u_fb = track.uri.clone();
            entries.push(MenuEntry::item(
                MenuItem::new("music-tr-up", "Move up").on_select(move |s: &mut AppState, _| {
                    s.music_move_playlist_track(&pl_u, index, -1);
                    set_fb(s, u_fb.clone(), "Moved");
                }),
            ));
        }
        let max_down = if whole {
            total.saturating_sub(1)
        } else {
            total.saturating_sub(2)
        };
        if can_move && index < max_down {
            let pl_d = pl.to_string();
            let u_fb = track.uri.clone();
            entries.push(MenuEntry::item(
                MenuItem::new("music-tr-down", "Move down").on_select(
                    move |s: &mut AppState, _| {
                        s.music_move_playlist_track(&pl_d, index, 1);
                        set_fb(s, u_fb.clone(), "Moved");
                    },
                ),
            ));
        }
    }

    if !is_ep && svc.has_song_radio() {
        let u = track.uri.clone();
        let t = track.title.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-tr-radio", "Go to song radio")
                .on_select(move |s: &mut AppState, _| s.music_open_radio(&u, format!("{t} Radio"))),
        ));
    }
    if let Some(artist) = track.artists.first()
        && !artist.uri.is_empty()
    {
        let u = artist.uri.clone();
        let n = artist.name.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-tr-artist", "Go to artist").on_select(
                move |s: &mut AppState, _| {
                    s.music_open_nav(MusicNav::Collection {
                        uri: u.clone(),
                        kind: "artist".into(),
                        title: n.clone(),
                        edit: false,
                    });
                },
            ),
        ));
    }
    if show_album && let Some(ref album) = track.album {
        let u = album.uri.clone();
        let n = album.name.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-tr-album", "Go to album").on_select(move |s: &mut AppState, _| {
                s.music_open_nav(MusicNav::Collection {
                    uri: u.clone(),
                    kind: "album".into(),
                    title: n.clone(),
                    edit: false,
                });
            }),
        ));
    }
    if !is_ep && svc.has_credits() {
        let u = track.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new("music-tr-credits", "Show credits")
                .on_select(move |s: &mut AppState, _| s.music_show_credits(u.clone())),
        ));
    }
    if let Some(link) = svc.share_url(&track.uri, track.url.as_deref()) {
        let u = track.uri.clone();
        entries.push(MenuEntry::item(
            MenuItem::new(
                "music-tr-link",
                if is_ep {
                    "Copy episode link"
                } else {
                    "Copy song link"
                },
            )
            .on_select(move |s: &mut AppState, tx| {
                tx.copy(link.clone());
                set_fb(s, u.clone(), "Copied");
            }),
        ));
    }

    if svc.has_add_to_playlist() {
        let mut sub_entries = Vec::new();
        if svc.has_playlists() {
            let t_uri = track.uri.clone();
            sub_entries.push(MenuEntry::item(
                MenuItem::new("music-tr-new-pl", "New playlist").on_select(
                    move |s: &mut AppState, _| s.music_start_new_playlist(Some(t_uri.clone())),
                ),
            ));
        }
        for pl_card in &s.media.music_user_playlists {
            if Some(pl_card.uri.as_str()) == playlist {
                continue;
            }
            let t_uri = track.uri.clone();
            let p_uri = pl_card.uri.clone();
            sub_entries.push(MenuEntry::item(
                MenuItem::new(
                    format!("music-tr-add-{}", pl_card.uri),
                    pl_card.title.clone(),
                )
                .on_select(move |s: &mut AppState, _| {
                    s.music_act_simple("addToPlaylist", json!({ "uri": t_uri, "context": p_uri }));
                    set_fb(s, t_uri.clone(), "Added");
                }),
            ));
        }
        if !sub_entries.is_empty() {
            entries.push(MenuEntry::Separator);
            entries.push(MenuEntry::item(
                MenuItem::new("music-tr-add-pl", "Add to playlist").submenu(sub_entries),
            ));
        }
    }

    let _ = (PRESS_SCALE, SPRING_QUICK, BG);
    entries
}
