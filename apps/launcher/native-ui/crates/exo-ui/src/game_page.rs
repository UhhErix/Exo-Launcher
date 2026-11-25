use crate::game_actions::{
    PrimaryActionKind, format_played_opt, format_relative_last_played, format_size, hosted_buy_url,
    install_verb, is_installing_game, is_noise_status, primary_cta_label, resolve_primary_action,
    transfer_for_game, transfer_meta_line, transfer_share, unavailable_reason,
};
use crate::game_perf::{graphics_features_card, performance_card};
use crate::game_props::{artwork_tools, properties_card};
use crate::state::{AppState, InputMode, title_hue};
use exo_kit::{
    BG, ExoGlass, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    SPRING_FLIGHT, SPRING_QUICK, TRANSPARENT, apply_glass, bad, fg_faint, fg_muted, float_line,
    glass_control_fill, glass_icon_circle, glass_rim_bottom, glass_rim_top, hex, hover_fill,
    icon_chevron_left, icon_download, icon_external, icon_folder, icon_pause, icon_play, icon_stop,
    icon_users, pressed_fill, scaled, store_logo, store_logo_or_fallback, surface_card,
    type_control, type_first_run, type_section, type_small, type_title, warn,
};
use exo_rpc::{Game, GameVariant, InstallProgress};
use rune::{Cursor, El, LiquidGlass, SurfaceFit, TextStyle, col, el, oklch, row, text};

const GAME_EDGE: f32 = 32.0;

fn store_display_name(store: &str) -> String {
    match store.to_ascii_lowercase().as_str() {
        "steam" => "Steam".into(),
        "epic" => "Epic Games".into(),
        "gog" => "GOG".into(),
        "ea" => "EA".into(),
        "riot" => "Riot Games".into(),
        "xbox" => "Xbox".into(),
        "ubisoft" => "Ubisoft Connect".into(),
        "battlenet" => "Battle.net".into(),
        "amazon" => "Amazon Games".into(),
        "itch" => "itch.io".into(),
        "local" => "Added here".into(),
        other => other.to_string(),
    }
}

fn game_shared_badge(game: &Game) -> Option<El> {
    if !game.family_shared {
        return None;
    }
    let label = match game.shared_by.as_deref().filter(|s| !s.is_empty()) {
        Some(who) => format!("Shared by {who}"),
        None => "Steam Family".into(),
    };
    Some(
        row()
            .key("gp-shared-badge")
            .h(30.0)
            .px(12.0)
            .gap(5.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(surface_card())
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .child(icon_users(12.0, fg_muted()))
            .child(
                text(label)
                    .font(TextStyle::new(scaled(12.0), 500.0))
                    .color(fg_muted()),
            ),
    )
}

fn game_store_pill(
    game: &Game,
    variants: &[GameVariant],
    locked: bool,
    gamepad_focus: bool,
) -> Option<El> {
    if variants.len() > 1 {
        let pills = variants.iter().map(|v| {
            let is_on = v.id == game.id;
            let vid = v.id.clone();
            let mut btn = row()
                .key(format!("gp-source-{}", v.id))
                .role("button")
                .label(store_display_name(&v.store))
                .h(26.0)
                .px(10.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(if is_on { FG } else { TRANSPARENT })
                .hit()
                .disabled(locked)
                .focusable(!locked)
                .cursor(if locked {
                    Cursor::NotAllowed
                } else {
                    Cursor::Pointer
                })
                .motion(SPRING_QUICK);
            if !locked && !is_on {
                btn = btn
                    .press_on_down()
                    .hover(hover_fill())
                    .press(pressed_fill())
                    .on_press(move |st: &mut AppState| st.select_game_source(&vid));
                if gamepad_focus {
                    btn = btn.focus_ring(2.0, 3.0, FG);
                }
            }
            if let Some(logo) = store_logo(&v.store, 14.0) {
                btn = btn.child(logo);
            }
            btn = btn.child(
                text(store_display_name(&v.store))
                    .font(type_small())
                    .color(if is_on { PRIMARY_FG } else { fg_muted() }),
            );
            if v.update_available {
                btn = btn.child(el().size(5.0, 5.0).radius(RADIUS_PILL).fill(if is_on {
                    PRIMARY_FG
                } else {
                    FG
                }));
            }
            btn
        });
        return Some(apply_glass(
            row()
                .key("gp-sources-pill")
                .h(34.0)
                .px(4.0)
                .gap(2.0)
                .center_y()
                .radius(RADIUS_PILL)
                .children(pills),
            ExoGlass::ClearMedia,
            false,
        ));
    }

    if game.is_add_portable {
        return None;
    }

    Some(apply_glass(
        row()
            .h(34.0)
            .px(12.0)
            .gap(8.0)
            .center_y()
            .radius(RADIUS_PILL)
            .child(store_logo_or_fallback(&game.store, 15.0))
            .child(
                text(game.store_display())
                    .font(type_control())
                    .color(fg_muted()),
            ),
        ExoGlass::ClearMedia,
        false,
    ))
}

fn primary_pill(key: &'static str, label: String, icon: Option<El>) -> El {
    let mut btn = row()
        .key(key)
        .role("button")
        .label(label.clone())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .min_w(196.0)
        .h(44.0)
        .px(22.0)
        .gap(8.0)
        .justify_center()
        .center_y()
        .radius(RADIUS_PILL)
        .fill(FG)
        .hover(FG.alpha(0.92))
        .press(FG.alpha(0.84))
        .motion(SPRING_QUICK);
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(text(label).font(type_section()).color(PRIMARY_FG))
}

fn primary_action_view(
    s: &AppState,
    game: &Game,
    progress: Option<&InstallProgress>,
    queued: bool,
    paused: bool,
    gamepad_focus: bool,
) -> El {
    let action = resolve_primary_action(game);
    let buy_url = hosted_buy_url(game);
    let busy = s.game_page.primary_busy;
    let waiting = queued && progress.is_none() && !busy;
    let share = progress.and_then(transfer_share);
    let percent = share.map(|v| (v * 100.0).round() as u64);

    let cancel_armed = progress.is_some() && s.is_confirm_armed("gp-cancel-transfer");
    let dequeue_armed = waiting && s.is_confirm_armed("gp-dequeue-transfer");
    let cancel_q = if action == PrimaryActionKind::Update {
        "Stop the update?"
    } else {
        "Stop the download?"
    };

    let (btn_key, label, icon, disabled, danger_fill) = if let Some(prog) = progress {
        let verb = install_verb(prog, action);
        let text_label = if cancel_armed {
            cancel_q.to_string()
        } else if let Some(pct) = percent {
            format!("{verb} · {pct}%")
        } else {
            format!("{verb}…")
        };
        (
            "gp-transfer",
            text_label,
            None,
            !prog.can_cancel,
            cancel_armed,
        )
    } else if paused {
        (
            "gp-resume",
            "Resume".into(),
            Some(icon_play(14.0, PRIMARY_FG)),
            busy,
            false,
        )
    } else if waiting {
        (
            "gp-queued",
            if dequeue_armed {
                "Remove from the queue?".into()
            } else {
                "Queued".into()
            },
            None,
            false,
            false,
        )
    } else if game.can_stop {
        (
            "gp-stop",
            if busy {
                "Closing…".into()
            } else {
                "Stop".into()
            },
            Some(icon_stop(14.0, PRIMARY_FG)),
            busy,
            false,
        )
    } else if action == PrimaryActionKind::None && buy_url.is_some() {
        (
            "gp-buy",
            primary_cta_label(game, action, buy_url),
            Some(icon_external(14.0, PRIMARY_FG)),
            busy,
            false,
        )
    } else {
        let cta = if busy {
            "Preparing…".into()
        } else {
            primary_cta_label(game, action, buy_url)
        };
        let ic = match action {
            PrimaryActionKind::Install => Some(icon_download(14.0, PRIMARY_FG)),
            PrimaryActionKind::Play => Some(icon_play(14.0, PRIMARY_FG)),
            PrimaryActionKind::Update | PrimaryActionKind::None => None,
        };
        let dis = busy || action == PrimaryActionKind::None;
        let key = match action {
            PrimaryActionKind::Update => "gp-update",
            PrimaryActionKind::Install => "gp-install",
            PrimaryActionKind::Play => "gp-play",
            PrimaryActionKind::None => "gp-unavailable",
        };
        (key, cta, ic, dis, false)
    };

    let gid = game.id.clone();
    let buy_target = buy_url.map(str::to_string);
    let has_prog = progress.is_some();
    let can_cancel = progress.is_some_and(|p| p.can_cancel);
    let can_stop = game.can_stop;

    let mut primary_btn = if disabled {
        row()
            .key(btn_key)
            .role("button")
            .label(label.clone())
            .min_w(196.0)
            .h(44.0)
            .px(22.0)
            .gap(8.0)
            .center()
            .radius(RADIUS_PILL)
            .fill(glass_control_fill())
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .cursor(Cursor::NotAllowed)
            .child(text(label.clone()).font(type_control()).color(fg_faint()))
    } else {
        let mut btn = primary_pill(btn_key, label.clone(), icon);
        if danger_fill {
            btn = btn
                .fill(bad())
                .hover(bad().alpha(0.85))
                .press(bad().alpha(0.75));
        }
        btn = btn.on_press_tx(move |st: &mut AppState, tx| {
            if has_prog {
                if can_cancel && st.arm_game_confirm("gp-cancel-transfer", tx) {
                    st.cancel_install(&gid);
                }
                return;
            }
            if paused {
                st.resume_download_game(&gid);
                return;
            }
            if waiting {
                if st.arm_game_confirm("gp-dequeue-transfer", tx) {
                    st.dequeue_game(&gid);
                }
                return;
            }
            if can_stop {
                st.stop_game_page(&gid);
                return;
            }
            if action == PrimaryActionKind::None
                && let Some(ref url) = buy_target
            {
                st.open_external_url(url);
                return;
            }
            match action {
                PrimaryActionKind::Update => st.update_game(&gid),
                PrimaryActionKind::Install => st.install_game(&gid),
                PrimaryActionKind::Play => st.launch_game(&gid),
                PrimaryActionKind::None => {}
            }
        });
        if gamepad_focus {
            btn = btn.focus_ring(2.0, 3.0, FG);
        }
        btn
    };

    if let Some(sh) = share {
        let fill_w = (168.0 * sh as f32).max(if sh > 0.0 { 4.0 } else { 0.0 });
        let edge = el()
            .pin(None, Some(14.0), Some(0.0), Some(14.0))
            .h(3.0)
            .radius(RADIUS_PILL)
            .fill(PRIMARY_FG.alpha(0.22))
            .child(el().w(fill_w).h(3.0).radius(RADIUS_PILL).fill(PRIMARY_FG));
        primary_btn = el()
            .clip_rounded(RADIUS_PILL)
            .radius(RADIUS_PILL)
            .child(primary_btn)
            .child(edge);
    }

    let mut action_row = row().gap(10.0).center_y().child(primary_btn);

    if let Some(prog) = progress
        && prog.can_cancel
    {
        let gid_pause = game.id.clone();
        let mut pause_btn = glass_icon_circle(
            "gp-pause-transfer",
            "Pause download",
            44.0,
            icon_pause(16.0, FG),
        )
        .on_press(move |st: &mut AppState| st.pause_install(&gid_pause));
        if gamepad_focus {
            pause_btn = pause_btn.focus_ring(2.0, 3.0, FG);
        }
        action_row = action_row.child(pause_btn);
    }

    if game.installed && !game.is_add_portable {
        let id_folder = game.id.clone();
        let mut folder_btn =
            glass_icon_circle("gp-folder", "Open folder", 44.0, icon_folder(16.0, FG))
                .on_press(move |st: &mut AppState| st.open_game_folder(&id_folder));
        if gamepad_focus {
            folder_btn = folder_btn.focus_ring(2.0, 3.0, FG);
        }
        action_row = action_row.child(folder_btn);
    }

    let mut stack = col().gap(8.0).child(action_row);

    if let Some(prog) = progress {
        let meta = transfer_meta_line(prog, true);
        if !meta.is_empty() {
            stack = stack.child(
                text(meta)
                    .key("gp-transfer-meta")
                    .font(type_small())
                    .color(fg_muted()),
            );
        }
    } else if !waiting
        && !paused
        && !busy
        && let Some(reason) = unavailable_reason(game, action, buy_url)
    {
        stack = stack.child(
            text(reason)
                .key("gp-action-reason")
                .font(type_small())
                .color(fg_muted()),
        );
    }

    stack
}

pub fn view(s: &AppState, base_game: &Game, scale: f32) -> El {
    let game = s.active_game_view(base_game);
    let hue = title_hue(&game.title);
    let cover = s.cover_image(&game, scale);
    let hero = s.hero_image(&game, scale);
    let gamepad_focus = s.input_mode == InputMode::Gamepad;

    let portable = game.is_add_portable;
    let store_page = !game.owned && !game.installed && !portable;
    let installed_actions = game.installed && !portable;
    let artwork_enabled = !store_page && !portable && (game.owned || game.installed);

    let active_progress = transfer_for_game(s.install_progress.as_ref(), &game);
    let queued = s.install_queue.as_ref().is_some_and(|q| {
        q.queued_game_ids
            .iter()
            .any(|id| is_installing_game(&game, Some(id.as_str())))
    });
    let paused = active_progress.is_none()
        && s.install_queue.as_ref().is_some_and(|q| {
            q.paused_downloads
                .iter()
                .any(|p| p.can_resume && is_installing_game(&game, Some(p.game_id.as_str())))
        });
    let host_locked = s.game_page.primary_busy || active_progress.is_some();

    let mut hero_bg = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG);
    if let Some(ref img) = hero {
        hero_bg = hero_bg
            .pixels_fit(img.pixels(), SurfaceFit::Fill)
            .surface_anchor_fraction(0.5, 0.22);
    } else {
        hero_bg =
            hero_bg.linear_gradient(0.3, [(0.0, oklch(0.32, 0.14, hue).alpha(0.65)), (1.0, BG)]);
    }

    let hero_fade_v = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, BG.alpha(0.30)),
                (0.20, BG.alpha(0.0)),
                (0.72, BG.alpha(0.48)),
                (1.0, BG),
            ],
        );

    let hero_fade_h = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::FRAC_PI_2,
            [
                (0.0, BG.alpha(0.62)),
                (0.42, BG.alpha(0.20)),
                (0.76, BG.alpha(0.0)),
            ],
        );

    let mut back_btn = apply_glass(
        row()
            .key("gp-back")
            .role("button")
            .label("Library")
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .h(34.0)
            .pl(10.0)
            .pr(14.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press_tx(|st: &mut AppState, tx| {
                st.input_mode = InputMode::Pointer;
                st.close_game_page_tx(tx);
            })
            .child(icon_chevron_left(16.0, FG))
            .child(text("Library").font(type_control()).color(FG)),
        ExoGlass::ClearMedia,
        true,
    );
    if gamepad_focus {
        back_btn = back_btn.focus_ring(2.0, 3.0, FG);
    }

    let right_meta = row()
        .gap(8.0)
        .center_y()
        .children(game_shared_badge(&game))
        .children(game_store_pill(
            &game,
            &base_game.variants,
            host_locked || s.game_page.art_action.is_some(),
            gamepad_focus,
        ));

    let top_row = row()
        .w_full()
        .justify_between()
        .center_y()
        .gap(12.0)
        .child(back_btn)
        .child(right_meta);

    let flight_id = s
        .cover_flight_id
        .clone()
        .unwrap_or_else(|| format!("deck:{}", base_game.id));
    let mut poster = el()
        .key(format!("gp-poster-{}", base_game.id))
        .matched(flight_id)
        .motion(SPRING_FLIGHT)
        .w(148.0)
        .h(222.0)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD)
        .fill(surface_card())
        .shadow(0.0, 20.0, BG.alpha(0.75));
    if let Some(ref img) = cover {
        poster = poster.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        poster = poster.linear_gradient(
            0.35,
            [(0.0, oklch(0.46, 0.16, hue)), (1.0, oklch(0.12, 0.03, hue))],
        );
    }

    let mut cover_col = col().gap(10.0).child(poster);
    if artwork_enabled && !installed_actions {
        cover_col = cover_col.child(artwork_tools(s, &game, host_locked));
    }

    let identity = if let Some(logo) = s.logo_image(&game, scale) {
        let (lw, lh) = if let Some([pw, ph]) = logo.size()
            && pw > 0
            && ph > 0
        {
            let aspect = pw as f32 / ph as f32;
            let w = (112.0 * aspect).min(440.0);
            let h = w / aspect;
            (w, h)
        } else {
            (400.0, 112.0)
        };
        row()
            .h(112.0)
            .items_end()
            .child(el().w(lw).h(lh).pixels_fit(logo.pixels(), SurfaceFit::Fit))
    } else {
        row().items_end().pb(4.0).child(
            text(game.title.clone())
                .font(type_first_run())
                .color(FG)
                .truncate(),
        )
    };

    let left_identity =
        row()
            .gap(24.0)
            .items_end()
            .child(cover_col)
            .child(col().gap(16.0).child(identity).child(primary_action_view(
                s,
                &game,
                active_progress,
                queued,
                paused,
                gamepad_focus,
            )));

    let played_opt = format_played_opt(game.playtime_minutes);
    let last_opt = format_relative_last_played(game.last_played_utc.as_deref());
    let size_opt = game.size_bytes.filter(|&b| b > 0).map(format_size);
    let ach_opt = s.game_extras.as_ref().and_then(|ex| {
        let total = ex.achievements_total.filter(|&t| t > 0)?;
        let unlocked = ex.achievements_unlocked?;
        Some((
            format!("{unlocked} / {total}"),
            (unlocked as f32 / total as f32).clamp(0.0, 1.0),
        ))
    });
    let drive_slowed = s
        .game_extras
        .as_ref()
        .and_then(|e| e.drive_heat.as_ref())
        .is_some_and(|h| h.slowed_seconds > 0.0);

    let stat_col = |val: String, label: &'static str, bar: Option<f32>, divider: bool| {
        let mut c = col()
            .px(23.0)
            .gap(3.0)
            .child(
                row()
                    .key(format!("gp-stat-val-{label}"))
                    .h(25.0)
                    .center_y()
                    .child(text(val).font(type_title()).color(FG)),
            )
            .child(
                row().h(16.0).center_y().child(
                    text(label)
                        .font(TextStyle::new(scaled(12.0), 500.0))
                        .color(hex(0xdcdcdc, 1.0)),
                ),
            );
        if let Some(p) = bar {
            let fill_w = (82.0 * p).max(if p > 0.0 { 4.0 } else { 0.0 });
            c = c.child(
                el().w(82.0)
                    .h(3.0)
                    .mt(4.0)
                    .radius(2.0)
                    .fill(float_line())
                    .child(el().w(fill_w).h(3.0).radius(2.0).fill(FG)),
            );
        }
        if divider {
            c = c.border_sides([0.0, 0.0, 0.0, 1.0], float_line());
        }
        c
    };

    let mut stat_items: Vec<El> = Vec::new();
    if let Some(v) = played_opt {
        stat_items.push(stat_col(v, "Played", None, !stat_items.is_empty()));
    }
    if let Some(v) = last_opt {
        stat_items.push(stat_col(v, "Last played", None, !stat_items.is_empty()));
    }
    if let Some(v) = size_opt {
        let lbl = if game.installed { "Storage" } else { "Size" };
        stat_items.push(stat_col(v, lbl, None, !stat_items.is_empty()));
    }
    if let Some((v, pct)) = ach_opt {
        stat_items.push(stat_col(
            v,
            "Achievements",
            Some(pct),
            !stat_items.is_empty(),
        ));
    }

    let stats_area = if !stat_items.is_empty() || drive_slowed {
        let mut c = col().gap(10.0).items_end();
        if !stat_items.is_empty() {
            c = c.child(
                row()
                    .pt(13.0)
                    .pb(15.0)
                    .px(4.0)
                    .items_start()
                    .radius(RADIUS_PANEL)
                    .continuous_corners()
                    .clip_rounded(RADIUS_PANEL)
                    .liquid_glass(LiquidGlass::control())
                    .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                    .children(stat_items),
            );
        }
        if drive_slowed {
            c = c.child(
                row()
                    .key("gp-drive-heat")
                    .py(6.0)
                    .px(12.0)
                    .radius(RADIUS_PILL)
                    .fill(warn().alpha(0.14))
                    .child(
                        text("Your drive ran hot and slowed this game.")
                            .font(type_small())
                            .color(warn()),
                    ),
            );
        }
        Some(c)
    } else {
        None
    };

    let hero_bar = row()
        .w_full()
        .justify_between()
        .items_end()
        .gap(32.0)
        .child(left_identity)
        .children(stats_area);

    let stage = el()
        .w_full()
        .min_h(460.0)
        .clip_rounded(0.0)
        .fill(BG)
        .child(hero_bg)
        .child(hero_fade_v)
        .child(hero_fade_h)
        .child(
            col()
                .w_full()
                .min_h(460.0)
                .pt(56.0)
                .px(GAME_EDGE)
                .pb(24.0)
                .justify_between()
                .gap(20.0)
                .child(top_row)
                .child(hero_bar),
        );

    let mut page = col()
        .key(format!("game-page-{}", base_game.id))
        .w_full()
        .gap(14.0)
        .pb(24.0)
        .fill(BG)
        .child(stage);

    if installed_actions {
        let left_col = col()
            .grow(1.15)
            .min_w(0.0)
            .gap(16.0)
            .child(performance_card(s, &game))
            .child(graphics_features_card(s, &game));
        let lower_cards = row()
            .w_full()
            .px(GAME_EDGE)
            .gap(16.0)
            .items_start()
            .child(left_col)
            .child(properties_card(s, &game));
        page = page.child(lower_cards);
    }

    if let Some(ref msg) = s.game_page.status_msg
        && !msg.trim().is_empty()
        && !is_noise_status(msg)
    {
        page = page.child(
            row().w_full().justify_center().pt(4.0).child(
                row()
                    .key("gp-live-status")
                    .py(8.0)
                    .px(16.0)
                    .radius(RADIUS_PILL)
                    .fill(glass_control_fill())
                    .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                    .child(text(msg.clone()).font(type_small()).color(FG)),
            ),
        );
    }

    if s.armed_confirm.is_some() || s.game_page.pending_choice.is_some() {
        page = page.hit().on_press(|st: &mut AppState| {
            st.disarm_confirm();
            st.game_page.pending_choice = None;
        });
    }

    page
}

#[cfg(test)]
mod tests {
    use crate::game_actions::{
        PrimaryActionKind, ban_question, buy_label, compress_shown, compress_words,
        default_section_id, download_question, format_eta, format_size, format_speed,
        frame_gen_word, hosted_buy_url, install_verb, is_noise_status, optimize_word,
        primary_cta_label, resolve_primary_action, sections_for, shader_word, transfer_meta_line,
        transfer_percent, unavailable_reason, upscaler_line_word, upscaler_view,
    };
    use crate::shell;
    use crate::state::{AppState, HostStatus};
    use exo_rpc::{
        AntiCheatLaunch, CompressProgressEvent, CompressState, DlssStatus, DlssStatusItem,
        FrameGenMod, Game, GameAchievementsEvent, GameContent, GameProperties, GameSettingsState,
        GameVariant, HostEvent, InstallProgress, PropertyChoice, ShaderStatus,
    };
    use rune::motion_gate::Run;
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Runtime};

    #[test]
    fn game_page_equal_cards_stat_baselines_and_blur_cap() {
        let mut state = AppState::with_demo_games();
        state.games[0].last_played_utc = Some("2026-11-22T18:00:00Z".into());
        state.open_game_page("demo:cyberpunk");
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.set_glass_policy(exo_kit::EXO_GLASS_POLICY);
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert_eq!(blurs, 0);
        let glass_shapes = rt.liquid_glass_frame().shapes.len();
        assert!(
            glass_shapes <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "Game page exceeded EXO_MAX_BACKDROP_BLURS: {glass_shapes}"
        );
        assert!(
            glass_shapes >= 8,
            "Game page should render real Liquid Glass across top bar and hero controls, got {glass_shapes}"
        );

        let (perf_rect, _) = rt
            .presented("gp-perf-card")
            .expect("gp-perf-card presented");
        let (props_rect, _) = rt
            .presented("gp-props-card")
            .expect("gp-props-card presented");
        let (gfx_rect, _) = rt
            .presented("gp-graphics-features-card")
            .expect("gp-graphics-features-card presented");
        assert!(
            (perf_rect[1] - props_rect[1]).abs() <= 1.0,
            "top cards not aligned: perf_y={} props_y={}",
            perf_rect[1],
            props_rect[1]
        );
        assert!(
            (gfx_rect[0] - perf_rect[0]).abs() <= 1.0 && (gfx_rect[2] - perf_rect[2]).abs() <= 1.0,
            "graphics features card must sit in left column under perf card: perf={perf_rect:?} gfx={gfx_rect:?}"
        );
        let expected_gfx_y = perf_rect[1] + perf_rect[3] + 16.0;
        assert!(
            (gfx_rect[1] - expected_gfx_y).abs() <= 1.0,
            "graphics features card gap mismatch: gfx_y={} expected={expected_gfx_y}",
            gfx_rect[1]
        );

        let stat_keys = [
            "gp-stat-val-Played",
            "gp-stat-val-Last played",
            "gp-stat-val-Storage",
            "gp-stat-val-Achievements",
        ];
        let first_y = rt
            .presented(stat_keys[0])
            .expect("Played stat value presented")
            .0[1];
        for key in &stat_keys[1..] {
            let (rect, _) = rt.presented(key).expect(key);
            assert!(
                (rect[1] - first_y).abs() <= 1.0,
                "stat baseline {key} y={} missed Played y={first_y}",
                rect[1]
            );
        }
    }

    #[test]
    fn game_page_properties_pill_and_confirm_button_motion_gate() {
        for reduce_motion in [false, true] {
            let mut state = AppState::with_demo_games();
            state.settings.reduce_motion = reduce_motion;
            state.open_game_page("demo:cyberpunk");
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();

            run.act("switch to Installed files tab", |rt| {
                Run::tap(rt, "gp-prop-tab-1")
            });
            run.play(0.4);
            run.act("arm uninstall confirm button", |rt| {
                Run::tap(rt, "gp-uninstall")
            });
            run.play(0.3);
            run.act("switch to Betas tab", |rt| Run::tap(rt, "gp-prop-tab-2"));
            run.play(0.4);
            run.act("switch back to Installed files tab", |rt| {
                Run::tap(rt, "gp-prop-tab-1")
            });
            run.play(0.4);

            let findings: Vec<_> = run
                .finish()
                .into_iter()
                .filter(|f| {
                    if f.kind == "pop-out" && f.element.contains("gp-props-pill") {
                        return false;
                    }
                    if reduce_motion && matches!(f.kind, "jump" | "text-swap") {
                        return false;
                    }
                    true
                })
                .collect();
            assert!(
                findings.is_empty(),
                "game_page motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }

    #[test]
    fn entitlement_and_primary_action_match_react() {
        let mut g = Game {
            id: "steam:10".into(),
            title: "Test".into(),
            store: "steam".into(),
            installed: true,
            owned: true,
            ..Default::default()
        };
        assert_eq!(resolve_primary_action(&g), PrimaryActionKind::Play);
        assert_eq!(primary_cta_label(&g, PrimaryActionKind::Play, None), "Play");

        g.update_available = true;
        assert_eq!(resolve_primary_action(&g), PrimaryActionKind::Update);
        assert_eq!(
            primary_cta_label(&g, PrimaryActionKind::Update, None),
            "Update"
        );

        g.entitlement_state = Some("notOwned".into());
        g.buy_url = Some("https://store.steampowered.com/app/10".into());
        assert_eq!(resolve_primary_action(&g), PrimaryActionKind::None);
        assert_eq!(
            hosted_buy_url(&g),
            Some("https://store.steampowered.com/app/10")
        );
        assert_eq!(
            primary_cta_label(&g, PrimaryActionKind::None, hosted_buy_url(&g)),
            "Buy again"
        );

        g.entitlement_state = Some("unverified".into());
        assert_eq!(hosted_buy_url(&g), None);
        assert_eq!(
            unavailable_reason(&g, PrimaryActionKind::None, None),
            Some("Exo could not confirm you own this game. Open it from its store app.")
        );

        assert_eq!(buy_label("steam"), "Buy on Steam");
        assert_eq!(buy_label("gog"), "Buy on GOG");
        assert_eq!(buy_label("epic"), "Buy in browser");

        let unowned = Game {
            id: "steam:20".into(),
            title: "Store Game".into(),
            store: "steam".into(),
            installed: false,
            owned: false,
            buy_url: Some("https://store.steampowered.com/app/20".into()),
            ..Default::default()
        };
        assert_eq!(
            primary_cta_label(&unowned, PrimaryActionKind::None, hosted_buy_url(&unowned)),
            "Buy on Steam"
        );
    }

    #[test]
    fn transfer_progress_speed_and_eta_format_correctly() {
        let prog = InstallProgress {
            game_id: Some("steam:10".into()),
            phase: "downloading".into(),
            percent: 50.0,
            bytes_downloaded: 1_073_741_824,
            bytes_to_download: 2_147_483_648,
            bytes_per_second: 10_485_760,
            status: None,
            can_cancel: true,
            is_active: true,
        };
        assert_eq!(transfer_percent(&prog), Some(50.0));
        assert_eq!(
            install_verb(&prog, PrimaryActionKind::Install),
            "Downloading"
        );
        assert_eq!(install_verb(&prog, PrimaryActionKind::Update), "Updating");
        assert_eq!(format_size(1_073_741_824), "1.0 GB");
        assert_eq!(format_speed(10_485_760).as_deref(), Some("10.0 MB/s"));
        assert!(format_eta(&prog).is_some());
        let meta = transfer_meta_line(&prog, true);
        assert!(meta.contains("1.0 GB / 2.0 GB"));
        assert!(meta.contains("10.0 MB/s"));
    }

    #[test]
    fn performance_words_match_react_for_all_states() {
        let dlss = DlssStatus {
            ok: Some(true),
            items: vec![DlssStatusItem {
                key: "1".into(),
                file_name: "nvngx_dlss.dll".into(),
                display_name: Some("DLSS".into()),
                current_version: Some("310.1.0".into()),
                latest_version: Some("310.2.1".into()),
                state: Some("outdated".into()),
                can_update: Some(true),
                ..Default::default()
            }],
            ..Default::default()
        };
        let v = upscaler_view(Some(&dlss));
        assert_eq!(upscaler_line_word(&v, None).text, "1 update");

        let ac_dlss = DlssStatus {
            ok: Some(true),
            anti_cheat: Some("Easy Anti-Cheat".into()),
            consent: Some("available".into()),
            items: dlss.items.clone(),
            ..Default::default()
        };
        let ac_view = upscaler_view(Some(&ac_dlss));
        assert_eq!(
            upscaler_line_word(&ac_view, None).text,
            "Uses Easy Anti-Cheat"
        );
        assert_eq!(
            ban_question("Easy Anti-Cheat"),
            "Risk an Easy Anti-Cheat ban?"
        );
        assert_eq!(ban_question("BattlEye"), "Risk a BattlEye ban?");

        let fg = FrameGenMod {
            ok: true,
            state: "broken".into(),
            reason: Some("replaced".into()),
            mod_name: Some("OptiScaler".into()),
            ..Default::default()
        };
        let w = frame_gen_word(&fg).unwrap();
        assert!(w.bad);
        assert_eq!(w.text, "OptiScaler replaced Exo’s loader");

        let gs = GameSettingsState {
            ok: true,
            supported: Some(true),
            found: Some(true),
            standing: Some("applied".into()),
            ..Default::default()
        };
        assert_eq!(
            optimize_word(&gs, false, false, false, Some("yourChangesKept")).text,
            "Applied, your changes kept"
        );
        assert_eq!(
            optimize_word(&gs, false, false, false, Some("putBackAfterUpdate")).text,
            "Applied again after an update"
        );
        assert_eq!(
            optimize_word(&gs, true, false, false, None).text,
            "Putting back"
        );

        let sh = ShaderStatus {
            ok: true,
            state: "noApp".into(),
            ..Default::default()
        };
        assert_eq!(shader_word(&sh).unwrap().text, "Get Exo Shaders");
    }

    #[test]
    fn compress_and_properties_sections_match_react() {
        let c = CompressState {
            state: "compressed".into(),
            saved: 19_649_475_379,
            ..Default::default()
        };
        assert!(compress_shown(&c));
        assert_eq!(compress_words(&c), "Saved 18 GB");

        let refused = CompressState {
            state: "refused".into(),
            ..Default::default()
        };
        assert!(!compress_shown(&refused));

        let props = GameProperties {
            ok: true,
            anti_cheat_launch: Some(AntiCheatLaunch {
                value: "with".into(),
                detail: "Offline".into(),
            }),
            beta: Some("".into()),
            beta_choices: vec![PropertyChoice {
                value: "".into(),
                label: "None".into(),
                ..Default::default()
            }],
            content_label: "DLC".into(),
            content: Some(vec![GameContent {
                id: "1".into(),
                name: "Pack".into(),
                enabled: false,
                size_bytes: Some(2_147_483_648),
                toggle: true,
                state: None,
            }]),
            ..Default::default()
        };
        let secs = sections_for(&props);
        assert_eq!(secs.len(), 5);
        assert_eq!(
            default_section_id(&secs),
            crate::game_actions::PropsSectionId::General
        );
        assert_eq!(download_question(Some(2_147_483_648)), "Download 2.0 GB?");
        assert!(is_noise_status("Uninstalled."));
        assert!(!is_noise_status("Could not open folder."));
    }

    #[test]
    fn connected_mode_has_no_fake_perf_data_and_handles_events() {
        let mut s = AppState::for_host_pipe();
        s.status = HostStatus::Connected;
        s.games.push(Game {
            id: "steam:1091500".into(),
            title: "Cyberpunk 2077".into(),
            store: "steam".into(),
            installed: true,
            owned: true,
            variants: vec![
                GameVariant {
                    id: "steam:1091500".into(),
                    store: "steam".into(),
                    installed: true,
                    owned: true,
                    ..Default::default()
                },
                GameVariant {
                    id: "gog:1423049311".into(),
                    store: "gog".into(),
                    installed: false,
                    owned: true,
                    ..Default::default()
                },
            ],
            ..Default::default()
        });
        s.open_game_page("steam:1091500");
        assert!(s.game_page.settings.is_none());
        assert!(s.game_page.dlss.is_none());
        assert!(s.game_page.frame_gen.is_none());
        assert!(s.game_page.shaders.is_none());
        assert!(s.game_page.hdr_game.is_none());
        assert!(s.game_page.compress.is_none());

        let redrew_ach = s.apply_host_event(HostEvent::GameAchievements(GameAchievementsEvent {
            id: "steam:1091500".into(),
            unlocked: 15,
            total: 50,
        }));
        assert!(redrew_ach);
        assert_eq!(
            s.game_extras.as_ref().and_then(|e| e.achievements_unlocked),
            Some(15)
        );
        assert_eq!(
            s.game_extras.as_ref().and_then(|e| e.achievements_total),
            Some(50)
        );

        let redrew_comp = s.apply_host_event(HostEvent::CompressProgress(CompressProgressEvent {
            id: "steam:1091500".into(),
            compress: CompressState {
                state: "compressing".into(),
                percent: Some(0.55),
                ..Default::default()
            },
        }));
        assert!(redrew_comp);
        assert_eq!(
            s.game_page.compress.as_ref().map(|c| c.state.as_str()),
            Some("compressing")
        );
    }

    #[test]
    fn game_page_interactive_perf_props_and_hero_states() {
        let mut state = AppState::with_demo_games();
        state.games[0].family_shared = true;
        state.games[0].shared_by = Some("Alex".into());
        state.games[0].variants = vec![
            GameVariant {
                id: "demo:cyberpunk".into(),
                store: "steam".into(),
                installed: true,
                owned: true,
                ..Default::default()
            },
            GameVariant {
                id: "gog:cyberpunk".into(),
                store: "gog".into(),
                installed: false,
                owned: true,
                update_available: true,
                ..Default::default()
            },
        ];
        if let Some(ref mut p) = state.game_props {
            p.anti_cheat_launch = Some(AntiCheatLaunch {
                value: "with".into(),
                detail: "Play without EAC (offline only)".into(),
            });
            p.through_steam = Some(false);
            p.shader_cache_bytes = Some(420_000_000);
        }
        if let Some(ref mut gs) = state.game_page.settings {
            gs.smooth_motion = Some(false);
        }
        state.open_game_page("demo:cyberpunk");

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        assert!(rt.presented("gp-shared-badge").is_some());
        assert!(rt.presented("gp-sources-pill").is_some());
        assert!(rt.presented("gp-perf-upscalers").is_some());
        assert!(rt.presented("gp-perf-shaders").is_some());
        assert!(rt.presented("gp-hdr-select").is_some());
        assert!(rt.presented("gp-optimize-row").is_some());
        assert!(rt.presented("gp-smooth-motion-switch").is_some());
        assert!(rt.presented("gp-props-cloud-sync").is_some());
        assert!(rt.presented("props-through-steam-switch").is_some());
        assert!(rt.presented("gp-anticheat-select").is_some());

        let (sx, sy) = rt
            .find("gp-smooth-motion-switch")
            .expect("gp-smooth-motion-switch");
        rt.pointer_down(sx, sy);
        rt.pointer_up(sx, sy);
        rt.frame(1.0 / 60.0);
        assert_eq!(
            rt.state
                .game_page
                .settings
                .as_ref()
                .and_then(|g| g.smooth_motion),
            Some(true)
        );

        let (ox, oy) = rt.find("gp-optimize-row").expect("gp-optimize-row");
        rt.pointer_down(ox, oy);
        rt.pointer_up(ox, oy);
        rt.frame(1.0 / 60.0);
        assert!(rt.state.is_confirm_armed("gp-optimize-putback"));
        rt.pointer_down(ox, oy);
        rt.pointer_up(ox, oy);
        rt.frame(1.0 / 60.0);
        assert_eq!(
            rt.state
                .game_page
                .settings
                .as_ref()
                .and_then(|g| g.standing.as_deref()),
            Some("off")
        );

        let (t1x, t1y) = rt.find("gp-prop-tab-1").expect("Installed files tab");
        rt.pointer_down(t1x, t1y);
        rt.pointer_up(t1x, t1y);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.presented("gp-compress-start").is_some());
        assert!(rt.presented("gp-rebuild-shaders").is_some());
        assert!(rt.presented("gp-folder-select").is_some());

        let (t2x, t2y) = rt.find("gp-prop-tab-2").expect("Betas tab");
        rt.pointer_down(t2x, t2y);
        rt.pointer_up(t2x, t2y);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.presented("gp-beta-select").is_some());
        assert!(rt.presented("gp-beta-code-input").is_some());

        let (t3x, t3y) = rt.find("gp-prop-tab-3").expect("DLC tab");
        rt.pointer_down(t3x, t3y);
        rt.pointer_up(t3x, t3y);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.presented("gp-dlc-2138330").is_some());

        let (t4x, t4y) = rt.find("gp-prop-tab-4").expect("Artwork tab");
        rt.pointer_down(t4x, t4y);
        rt.pointer_up(t4x, t4y);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.presented("gp-art-replace").is_some());
        assert!(rt.presented("gp-art-refresh").is_some());
    }

    #[test]
    fn game_page_graphics_features_card_renders_all_five_rows_and_controls() {
        let mut state = AppState::with_demo_games();
        state.open_game_page("demo:cyberpunk");
        state.game_page.graphics_features = Some(exo_rpc::GameGraphicsFeaturesState {
            ok: true,
            message: None,
            engine: Some("REDengine 4".into()),
            running: Some(false),
            anti_cheat: None,
            features: Some(vec![
                exo_rpc::GraphicsFeatureRow {
                    id: "upscalers".into(),
                    name: "Upscalers".into(),
                    state: "inGame".into(),
                    route: "DLSS / FSR / XeSS · NVIDIA DRS DLSS 4/4.5 override".into(),
                    route_kind: "driver".into(),
                    can_toggle: true,
                    needs_anti_cheat_confirm: false,
                    anti_cheat_warning: None,
                    closest_option: None,
                    details: None,
                },
                exo_rpc::GraphicsFeatureRow {
                    id: "reflex".into(),
                    name: "Reflex & low latency".into(),
                    state: "on".into(),
                    route: "NVIDIA Reflex + driver Low Latency Mode Ultra".into(),
                    route_kind: "driver".into(),
                    can_toggle: true,
                    needs_anti_cheat_confirm: false,
                    anti_cheat_warning: None,
                    closest_option: None,
                    details: None,
                },
                exo_rpc::GraphicsFeatureRow {
                    id: "rayTracing".into(),
                    name: "Ray tracing".into(),
                    state: "off".into(),
                    route: "REDengine UserSettings.json".into(),
                    route_kind: "config".into(),
                    can_toggle: true,
                    needs_anti_cheat_confirm: false,
                    anti_cheat_warning: None,
                    closest_option: None,
                    details: None,
                },
                exo_rpc::GraphicsFeatureRow {
                    id: "pathTracing".into(),
                    name: "Path tracing".into(),
                    state: "off".into(),
                    route: "REDengine RT Overdrive (UserSettings.json)".into(),
                    route_kind: "config".into(),
                    can_toggle: true,
                    needs_anti_cheat_confirm: false,
                    anti_cheat_warning: None,
                    closest_option: None,
                    details: None,
                },
                exo_rpc::GraphicsFeatureRow {
                    id: "hiddenSettings".into(),
                    name: "Hidden settings".into(),
                    state: "off".into(),
                    route: "Ultra decals, 16x AF & crowd (UserSettings.json)".into(),
                    route_kind: "config".into(),
                    can_toggle: true,
                    needs_anti_cheat_confirm: false,
                    anti_cheat_warning: None,
                    closest_option: None,
                    details: None,
                },
            ]),
        });

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        assert!(rt.presented("gp-graphics-features-card").is_some());
        assert!(rt.presented("gp-gfx-switch-upscalers").is_some());
        assert!(rt.presented("gp-gfx-switch-reflex").is_some());
        assert!(rt.presented("gp-gfx-switch-rayTracing").is_some());
        assert!(rt.presented("gp-gfx-switch-pathTracing").is_some());
        assert!(rt.presented("gp-gfx-switch-hiddenSettings").is_some());
    }
}
