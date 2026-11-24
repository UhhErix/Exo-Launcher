use crate::pc_ai::short_drive_model;
use crate::pc_state::{compress_words, format_bytes};
use crate::state::AppState;
use exo_kit::{
    BG, BadgeTone, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, SPRING_MOVE,
    SPRING_QUICK, amoled_card, bad, fg_muted, float_line, glass_button, good, hover_fill,
    icon_chevron_left, icon_hard_drive, icon_refresh, pressed_fill, progress_bar, scaled,
    status_badge, subtle_fill, track_fill, type_body, type_control, type_section, type_small,
    type_title, warn,
};
use exo_rpc::{CompressListGame, DriveInfo, DrivesReply};
use rune::{Cursor, El, Key, ScrollSpec, TextStyle, col, el, row, text};

const GIB: f64 = 1024.0 * 1024.0 * 1024.0;
const TIB: f64 = GIB * 1024.0;

pub(crate) fn format_gb_short(gb: f64) -> String {
    if gb >= 1000.0 {
        format!("{:.1} TB", gb / 1024.0)
    } else {
        format!("{:.0} GB", gb.round().max(1.0))
    }
}

fn format_capacity_bytes(bytes: u64) -> String {
    let n = bytes as f64;
    if n >= 1000.0 * GIB {
        format!("{:.1} TB", n / TIB)
    } else {
        format!("{} GB", (n / GIB).round().max(1.0) as u64)
    }
}

fn drive_totals(d: &DriveInfo) -> (u64, u64) {
    if let Some(ref sp) = d.space
        && sp.total > 0
    {
        return (sp.total, sp.free);
    }
    let total: f64 = d.volumes.iter().map(|v| v.total_gb).sum::<f64>() * GIB;
    let free: f64 = d.volumes.iter().map(|v| v.free_gb).sum::<f64>() * GIB;
    (total.max(0.0) as u64, free.max(0.0) as u64)
}

fn summary_line(drives: Option<&DrivesReply>, saved: u64) -> Option<String> {
    let drives = drives?;
    if drives.drives.is_empty() {
        return None;
    }
    let mut total = 0u64;
    let mut free = 0u64;
    for d in &drives.drives {
        let (t, f) = drive_totals(d);
        total = total.saturating_add(t);
        free = free.saturating_add(f);
    }
    let mut parts = Vec::new();
    if total > 0 {
        parts.push(format!(
            "{} free of {}",
            format_capacity_bytes(free),
            format_capacity_bytes(total)
        ));
    }
    let count = drives.drives.len();
    parts.push(format!(
        "{count} {}",
        if count == 1 { "drive" } else { "drives" }
    ));
    if saved > 0 {
        parts.push(format!("{} saved", format_bytes(saved)));
    }
    Some(parts.join(" · "))
}

fn list_btn(
    key: String,
    label: impl Into<String>,
    primary: bool,
    danger: bool,
    disabled: bool,
) -> El {
    let lbl = label.into();
    let (fill, hover_bg, press_bg, fg, border_col) = if danger {
        (
            bad(),
            bad().alpha(0.90),
            bad().alpha(0.80),
            PRIMARY_FG,
            bad(),
        )
    } else if primary {
        (FG, FG.alpha(0.90), FG.alpha(0.82), PRIMARY_FG, FG)
    } else {
        (
            subtle_fill(),
            hover_fill(),
            pressed_fill(),
            FG,
            float_line(),
        )
    };
    el().key(key)
        .role("button")
        .label(lbl.clone())
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .focusable(!disabled)
        .disabled(disabled)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(28.0)
        .px(12.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(fill)
        .hover(hover_bg)
        .press(press_bg)
        .border(1.0, border_col)
        .motion(SPRING_QUICK)
        .child(text(lbl).font(type_small()).color(fg))
}

fn inline_switch(key: String, label: impl Into<String>, on: bool) -> El {
    let knob = el()
        .size(18.0, 18.0)
        .radius(9.0)
        .fill(FG)
        .absolute(if on { 18.0 } else { 2.0 }, 2.0)
        .motion(SPRING_MOVE);
    el().key(key)
        .role("switch")
        .label(label)
        .checked(on)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .size(38.0, 22.0)
        .radius(RADIUS_PILL)
        .fill(if on { good() } else { track_fill() })
        .motion(SPRING_QUICK)
        .child(knob)
}

fn drive_card(d: &DriveInfo) -> El {
    let letters: String = d
        .volumes
        .iter()
        .map(|v| {
            if v.letter.ends_with(':') {
                v.letter.clone()
            } else {
                format!("{}:", v.letter)
            }
        })
        .collect::<Vec<_>>()
        .join(", ");
    let name = if d.model.is_empty() {
        d.kind.clone()
    } else {
        short_drive_model(&d.model)
    };
    let title = if letters.is_empty() {
        name
    } else {
        format!("{letters} · {name}")
    };
    let (total, free) = drive_totals(d);
    let share = (total > 0).then(|| (1.0 - (free as f64 / total as f64)).clamp(0.0, 1.0) as f32);
    let verdicts: Vec<_> = d
        .verdicts
        .iter()
        .filter(|v| !v.key.starts_with("bypass:"))
        .collect();
    let is_bad = verdicts.iter().any(|v| v.bad);

    let mut spec_parts: Vec<String> = Vec::new();
    if !d.kind.is_empty() {
        spec_parts.push(d.kind.clone());
    }
    if let Some(ref t) = d.temperature {
        spec_parts.push(format!("{} °C", t.now));
    }
    if let Some(ref link) = d.link {
        spec_parts.push(format!("PCIe {}.0 ×{}", link.pcie_gen, link.width));
    }
    if let Some(ref h) = d.health {
        if let Some(used) = h.life_used {
            spec_parts.push(format!("{}% health", (100 - used).max(0)));
        }
        if let Some(tb) = h.written_tb {
            spec_parts.push(format!("{} TB written", tb.round() as i64));
        }
    }
    if let Some(ref b) = d.build
        && !b.is_empty()
    {
        spec_parts.push(b.clone());
    }
    let specs = spec_parts.join(" · ");

    let breakdown = d.space.as_ref().and_then(|sp| {
        let caches = sp.caches.saturating_add(sp.old_caches);
        let other = sp.other.saturating_add(sp.exo);
        let mut p = Vec::new();
        if sp.games > 0 {
            p.push(format!("Games {}", format_bytes(sp.games)));
        }
        if caches > 0 {
            p.push(format!("Caches {}", format_bytes(caches)));
        }
        if sp.leftovers > 0 {
            p.push(format!("Leftovers {}", format_bytes(sp.leftovers)));
        }
        if other > 0 {
            p.push(format!("Other {}", format_bytes(other)));
        }
        (!p.is_empty()).then(|| p.join(" · "))
    });

    let top_games: String = d
        .games
        .iter()
        .take(3)
        .map(|g| format!("{} {}", g.title, format_bytes(g.bytes)))
        .collect::<Vec<_>>()
        .join(" · ");

    let mut card = amoled_card(RADIUS_PANEL)
        .key(format!("storage-drive-{}", d.id))
        .w_full()
        .px(18.0)
        .py(14.0)
        .gap(8.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(12.0)
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .min_w(0.0)
                        .grow(1.0)
                        .child(icon_hard_drive(16.0, FG))
                        .child(text(title).font(type_section()).color(FG).truncate()),
                )
                .child(
                    row()
                        .gap(10.0)
                        .center_y()
                        .children((total > 0).then(|| {
                            text(format!(
                                "{} free of {}",
                                format_capacity_bytes(free),
                                format_capacity_bytes(total)
                            ))
                            .font(type_small())
                            .color(fg_muted())
                        }))
                        .child(status_badge(
                            if is_bad {
                                "Check".into()
                            } else {
                                "Healthy".into()
                            },
                            if is_bad {
                                BadgeTone::Bad
                            } else {
                                BadgeTone::Good
                            },
                        )),
                ),
        );

    if let Some(sh) = share {
        let bar_color = if sh >= 0.95 {
            bad()
        } else if sh >= 0.85 {
            warn()
        } else {
            FG
        };
        card = card.child(
            el().w_full().h(4.0).radius(2.0).fill(FG.alpha(0.14)).child(
                el().w_full().h(4.0).child(
                    el().w((520.0 * sh).max(4.0))
                        .h(4.0)
                        .radius(2.0)
                        .fill(bar_color),
                ),
            ),
        );
    }
    if !specs.is_empty() {
        card = card.child(text(specs).font(type_small()).color(fg_muted()).wrap());
    }
    if let Some(bd) = breakdown {
        card = card.child(text(bd).font(type_small()).color(fg_muted()).wrap());
    }
    if !top_games.is_empty() {
        card = card.child(
            text(format!("Largest: {top_games}"))
                .font(type_small())
                .color(fg_muted())
                .wrap(),
        );
    }
    if !verdicts.is_empty() {
        let mut vc = col().w_full().gap(6.0).pt(4.0);
        for v in verdicts {
            vc = vc.child(
                text(v.text.clone())
                    .font(type_small())
                    .color(if v.bad { bad() } else { warn() })
                    .wrap(),
            );
        }
        if let Some(ref vendor) = d.vendor {
            let url = vendor.url.clone();
            let tool = vendor.tool.clone();
            vc = vc.child(
                list_btn(
                    format!("storage-vendor-{}", d.id),
                    format!("Open {tool}"),
                    false,
                    false,
                    false,
                )
                .on_press(move |s: &mut AppState| s.open_external_url(&url)),
            );
        }
        card = card.child(vc);
    }
    card
}

fn compress_game_row(game: &CompressListGame, armed: Option<&str>) -> El {
    let c = &game.compress;
    let working = matches!(c.state.as_str(), "compressing" | "uncompressing") && c.held.is_none();
    let detail = compress_words(c);
    let stopped_err = c
        .message
        .as_deref()
        .filter(|_| !matches!(c.state.as_str(), "refused" | "needsConsent"));

    let mut actions = row().gap(8.0).center_y();
    if working && let Some(pct) = c.percent {
        actions = actions.child(progress_bar(96.0, pct as f32));
    }

    let id = game.id.clone();
    let uncompress_btn = |s_id: String, saved: u64, armed: Option<&str>| -> El {
        let confirm_key = format!("uncompress:{s_id}");
        let is_armed = armed == Some(confirm_key.as_str());
        let label = if is_armed {
            if saved > 0 {
                format!("Use {} again?", format_bytes(saved))
            } else {
                "Put it back?".into()
            }
        } else {
            "Uncompress".into()
        };
        let s_id2 = s_id.clone();
        let ck = confirm_key.clone();
        list_btn(format!("comp-uncomp-{s_id}"), label, is_armed, false, false).on_press(
            move |s: &mut AppState| {
                if s.pc.armed_confirm.as_deref() == Some(ck.as_str()) {
                    s.run_compress_act(&s_id2, "uncompress", None);
                } else {
                    s.pc.armed_confirm = Some(ck.clone());
                }
            },
        )
    };

    match c.state.as_str() {
        "compressing" => {
            let id_p = id.clone();
            let id_c = id.clone();
            actions = actions
                .child(
                    list_btn(format!("comp-pause-{id}"), "Pause", false, false, false)
                        .on_press(move |s: &mut AppState| s.run_compress_act(&id_p, "pause", None)),
                )
                .child(
                    list_btn(format!("comp-stop-{id}"), "Stop", false, false, false).on_press(
                        move |s: &mut AppState| s.run_compress_act(&id_c, "cancel", None),
                    ),
                );
        }
        "uncompressing" => {
            let id_c = id.clone();
            actions = actions.child(
                list_btn(format!("comp-stop-{id}"), "Stop", false, false, false)
                    .on_press(move |s: &mut AppState| s.run_compress_act(&id_c, "cancel", None)),
            );
        }
        "queued" => {
            let id_c = id.clone();
            actions = actions.child(
                list_btn(format!("comp-cancel-{id}"), "Cancel", false, false, false)
                    .on_press(move |s: &mut AppState| s.run_compress_act(&id_c, "cancel", None)),
            );
        }
        "paused" => {
            let id_r = id.clone();
            actions = actions
                .child(
                    list_btn(format!("comp-resume-{id}"), "Resume", false, false, false).on_press(
                        move |s: &mut AppState| s.run_compress_act(&id_r, "resume", None),
                    ),
                )
                .child(uncompress_btn(id, c.saved, armed));
        }
        "compressed" => {
            actions = actions.child(uncompress_btn(id, c.saved, armed));
        }
        "changed" => {
            let id_a = id.clone();
            actions = actions
                .child(
                    list_btn(
                        format!("comp-again-{id}"),
                        "Compress again",
                        false,
                        false,
                        false,
                    )
                    .on_press(move |s: &mut AppState| s.run_compress_act(&id_a, "compress", None)),
                )
                .child(uncompress_btn(id, c.saved, armed));
        }
        "needsConsent" => {
            if let Some(ref ac) = c.anti_cheat {
                let confirm_key = format!("allow:{id}");
                let is_armed = armed == Some(confirm_key.as_str());
                let lbl = if is_armed {
                    "Allow? Its programs stay as they are."
                } else {
                    "Allow"
                };
                let ac_c = ac.clone();
                let id_a = id.clone();
                actions = actions.child(
                    list_btn(format!("comp-allow-{id}"), lbl, is_armed, false, false).on_press(
                        move |s: &mut AppState| {
                            if s.pc.armed_confirm.as_deref() == Some(confirm_key.as_str()) {
                                s.run_compress_act(&id_a, "allow", Some(ac_c.clone()));
                            } else {
                                s.pc.armed_confirm = Some(confirm_key.clone());
                            }
                        },
                    ),
                );
            }
        }
        _ => {
            let confirm_key = format!("compress:{id}");
            let is_armed = armed == Some(confirm_key.as_str());
            let lbl = if is_armed {
                match c.size {
                    Some(sz) if sz > 0 => format!("Compress {}?", format_bytes(sz)),
                    _ => "Compress it?".into(),
                }
            } else {
                "Compress".into()
            };
            let id_a = id.clone();
            actions = actions.child(
                list_btn(format!("comp-go-{id}"), lbl, is_armed, false, false).on_press(
                    move |s: &mut AppState| {
                        if s.pc.armed_confirm.as_deref() == Some(confirm_key.as_str()) {
                            s.run_compress_act(&id_a, "compress", None);
                        } else {
                            s.pc.armed_confirm = Some(confirm_key.clone());
                        }
                    },
                ),
            );
        }
    }

    col()
        .w_full()
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(
            row()
                .w_full()
                .py(10.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(2.0)
                        .child(
                            text(game.title.clone())
                                .font(type_control())
                                .color(FG)
                                .truncate(),
                        )
                        .child(text(detail).font(type_small()).color(fg_muted()).wrap())
                        .children(stopped_err.map(|err| {
                            text(err.to_string()).font(type_small()).color(bad()).wrap()
                        })),
                )
                .child(actions),
        )
}

pub(crate) fn exo_ai_storage_view(s: &AppState) -> El {
    let drives_reply = s.pc.drives.as_ref();
    let drives = drives_reply.map(|d| d.drives.as_slice()).unwrap_or(&[]);
    let fixes = drives_reply.map(|d| d.fixes.as_slice()).unwrap_or(&[]);
    let compress = s.pc.compress_list.as_ref();
    let saved = compress
        .map(|c| c.saved)
        .unwrap_or(s.pc.compress_saved_bytes);
    let sub = summary_line(drives_reply, saved);
    let armed = s.pc.armed_confirm.as_deref();

    let mut drives_panel = col()
        .key("storage-drives-panel")
        .grow(1.0)
        .min_w(0.0)
        .px(20.0)
        .py(20.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border(1.0, float_line())
        .child(text("Drives").font(type_title()).color(FG));

    if drives.is_empty() {
        drives_panel =
            drives_panel.child(text("Reading drives…").font(type_body()).color(fg_muted()));
    } else {
        for d in drives {
            drives_panel = drives_panel.child(drive_card(d));
        }
    }

    if !fixes.is_empty() {
        let mut fix_col = col()
            .w_full()
            .pt(8.0)
            .gap(4.0)
            .child(text("Drive settings").font(type_section()).color(FG));
        for fix in fixes {
            let key_s = fix.key.clone();
            let busy = s.pc.drive_fix_busy.as_deref() == Some(fix.key.as_str());
            let ctrl = if fix.applied {
                status_badge(
                    if fix.restart {
                        "After restart".into()
                    } else {
                        "Optimal".into()
                    },
                    BadgeTone::Good,
                )
            } else {
                list_btn(
                    format!("storage-fix-{}", fix.key),
                    if busy { "Applying…" } else { "Apply" },
                    false,
                    false,
                    busy,
                )
                .on_press(move |s: &mut AppState| s.apply_drive_fix(&key_s))
            };
            fix_col = fix_col
                .child(el().w_full().h(1.0).fill(float_line()))
                .child(
                    row()
                        .w_full()
                        .py(10.0)
                        .gap(12.0)
                        .justify_between()
                        .center_y()
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(text(fix.label.clone()).font(type_control()).color(FG))
                                .children((!fix.detail.is_empty()).then(|| {
                                    text(fix.detail.clone())
                                        .font(type_small())
                                        .color(fg_muted())
                                        .wrap()
                                })),
                        )
                        .child(ctrl),
                );
        }
        drives_panel = drives_panel.child(fix_col);
    }

    let mut compress_panel = col()
        .key("storage-compress-panel")
        .grow(1.0)
        .min_w(0.0)
        .px(20.0)
        .py(20.0)
        .gap(8.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border(1.0, float_line())
        .child(text("Compress games").font(type_title()).color(FG));

    if let Some(list) = compress {
        let eligible_count = list.games.iter().filter(|g| g.eligible).count();
        let all_armed = armed == Some("compress-all");
        let mut saved_right = row().gap(8.0).center_y().child(status_badge(
            format_bytes(list.saved),
            if list.saved > 0 {
                BadgeTone::Good
            } else {
                BadgeTone::Neutral
            },
        ));
        if list.potential > 0 {
            let lbl = if all_armed {
                format!("Compress {eligible_count} games?")
            } else {
                "Compress all".into()
            };
            saved_right = saved_right.child(
                list_btn("storage-compress-all".into(), lbl, all_armed, false, false).on_press(
                    |s: &mut AppState| {
                        if s.pc.armed_confirm.as_deref() == Some("compress-all") {
                            s.run_compress_all();
                        } else {
                            s.pc.armed_confirm = Some("compress-all".into());
                        }
                    },
                ),
            );
        }

        let while_idle = list.settings.while_idle;
        let after_updates = list.settings.after_updates;

        compress_panel = compress_panel
            .child(
                row()
                    .w_full()
                    .py(8.0)
                    .justify_between()
                    .center_y()
                    .child(
                        col()
                            .gap(2.0)
                            .child(text("Saved").font(type_control()).color(FG))
                            .children((list.potential > 0).then(|| {
                                text(format!("{} more to save", format_bytes(list.potential)))
                                    .font(type_small())
                                    .color(fg_muted())
                            }))
                            .children(s.pc.compress_error.as_ref().map(|err| {
                                text(err.clone()).font(type_small()).color(bad()).wrap()
                            })),
                    )
                    .child(saved_right),
            )
            .child(el().w_full().h(1.0).fill(float_line()))
            .child(
                row()
                    .w_full()
                    .py(10.0)
                    .justify_between()
                    .center_y()
                    .child(text("Compress while idle").font(type_control()).color(FG))
                    .child(
                        inline_switch("compress-idle".into(), "Compress while idle", while_idle)
                            .on_press(move |s: &mut AppState| {
                                s.set_compress_settings(Some(!while_idle), None);
                            }),
                    ),
            )
            .child(el().w_full().h(1.0).fill(float_line()))
            .child(
                row()
                    .w_full()
                    .py(10.0)
                    .justify_between()
                    .center_y()
                    .child(
                        text("Compress again after updates")
                            .font(type_control())
                            .color(FG),
                    )
                    .child(
                        inline_switch(
                            "compress-again".into(),
                            "Compress again after updates",
                            after_updates,
                        )
                        .on_press(move |s: &mut AppState| {
                            s.set_compress_settings(None, Some(!after_updates));
                        }),
                    ),
            );

        for game in list.games.iter().filter(|g| g.compress.state != "refused") {
            compress_panel = compress_panel.child(compress_game_row(game, armed));
        }
    } else {
        compress_panel = compress_panel.child(
            text("Reading your games")
                .font(type_body())
                .color(fg_muted()),
        );
    }

    col()
        .key("storage-room")
        .w_full()
        .h_full()
        .pt(60.0)
        .px(16.0)
        .pb(28.0)
        .gap(16.0)
        .on_key(|s: &mut AppState, ev, _| {
            if ev.key == Key::Escape {
                if s.pc.armed_confirm.is_some() {
                    s.pc.armed_confirm = None;
                } else {
                    s.pc.exo_ai_storage_open = false;
                }
                true
            } else {
                false
            }
        })
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    row()
                        .gap(14.0)
                        .center_y()
                        .child(
                            glass_button(
                                "storage-back",
                                "Exo AI",
                                Some(icon_chevron_left(14.0, FG)),
                            )
                            .on_press(|s: &mut AppState| {
                                s.pc.exo_ai_storage_open = false;
                                s.pc.armed_confirm = None;
                            }),
                        )
                        .child(
                            col()
                                .gap(2.0)
                                .child(
                                    text("Storage")
                                        .font(TextStyle::new(scaled(22.0), 650.0))
                                        .color(FG),
                                )
                                .children(
                                    sub.map(|line| text(line).font(type_small()).color(fg_muted())),
                                ),
                        ),
                )
                .child(
                    glass_button(
                        "storage-scan",
                        if s.pc.storage_scanning {
                            "Scanning…"
                        } else {
                            "Scan space"
                        },
                        Some(icon_refresh(14.0, FG)),
                    )
                    .on_press(|s: &mut AppState| s.scan_storage_space()),
                ),
        )
        .child(
            col()
                .key("storage-scroll")
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(
                    row()
                        .w_full()
                        .items_start()
                        .gap(14.0)
                        .child(drives_panel)
                        .child(compress_panel),
                ),
        )
}
