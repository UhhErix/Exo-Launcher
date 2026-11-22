use crate::state::{AppState, LibraryFilter, title_hue};
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PILL, SPRING_MOVE, fg_muted, filter_pill,
    glass_button, good, icon_play, icon_refresh, icon_stop, primary_button, type_page,
    type_section, type_small,
};
use exo_rpc::Game;
use rune::{El, SurfaceFit, col, el, oklch, row, text};

fn poster_tile(game: &Game, selected: bool, s: &AppState, scale: f32) -> El {
    let hue = title_hue(&game.title);
    let hours = game.hours_label();
    let sub = if game.is_running {
        "Running".to_string()
    } else if !hours.is_empty() {
        hours
    } else if game.installed {
        game.store_display().to_string()
    } else {
        format!("{} · Not installed", game.store_display())
    };

    let mut art = el().w(168.0).h(238.0).radius(RADIUS_CARD);
    if let Some(img) = s.cover_image(game, scale) {
        art = art.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        art = art.linear_gradient(
            0.35,
            [
                (0.0, oklch(0.48, 0.16, hue)),
                (0.55, oklch(0.24, 0.10, (hue + 26.0) % 360.0)),
                (1.0, oklch(0.10, 0.03, hue)),
            ],
        );
    }

    let scrim = el()
        .w_full()
        .h_full()
        .radius(RADIUS_CARD)
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, BG.alpha(0.0)),
                (0.52, BG.alpha(0.06)),
                (0.82, BG.alpha(0.68)),
                (1.0, BG.alpha(0.90)),
            ],
        )
        .child(
            col()
                .w_full()
                .h_full()
                .px(11.0)
                .py(11.0)
                .justify_end()
                .gap(3.0)
                .child(text(game.title.clone()).font(type_section()).color(FG).truncate())
                .child(
                    row()
                        .gap(6.0)
                        .center_y()
                        .children(game.is_running.then(|| el().size(6.0, 6.0).radius(3.0).fill(good())))
                        .child(text(sub).font(type_small()).color(fg_muted()).truncate()),
                ),
        );

    let id = game.id.clone();
    el().key(format!("tile-{}", game.id))
        .role("button")
        .label(game.title.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .px(3.0)
        .py(3.0)
        .radius(RADIUS_CARD + 3.0)
        .border(if selected { 1.5 } else { 0.0 }, FG.alpha(0.88))
        .motion(SPRING_MOVE)
        .on_press(move |s: &mut AppState| s.selected_game_id = Some(id.clone()))
        .child(art.child(scrim))
}

pub fn view(s: &AppState, scale: f32) -> El {
    let filtered = s.filtered_games();
    let total_filtered = filtered.len();
    let selected = s.selected_game();
    let selected_id = selected.map(|g| g.id.clone());

    let subtitle = if let Some(g) = selected {
        let mut parts = vec![g.title.clone(), g.store_display().to_string()];
        let hrs = g.hours_label();
        if !hrs.is_empty() {
            parts.push(hrs);
        }
        if let Some(sz) = g.size_label() {
            parts.push(sz);
        }
        parts.join(" · ")
    } else if s.refreshing {
        "Scanning installed stores…".into()
    } else {
        format!("{} games · {} installed", s.games.len(), s.installed_count())
    };

    let mut actions = row().gap(8.0).center_y();
    if let Some(g) = selected {
        let id = g.id.clone();
        if g.is_running && g.can_stop {
            actions = actions.child(
                primary_button("stop-selected", "Stop", Some(icon_stop(13.0, PRIMARY_FG)))
                    .on_press(move |s: &mut AppState| s.stop_game(&id)),
            );
        } else if g.installed {
            actions = actions.child(
                primary_button("play-selected", "Play", Some(icon_play(13.0, PRIMARY_FG)))
                    .on_press(move |s: &mut AppState| s.launch_game(&id)),
            );
        }
    }
    actions = actions.child(
        glass_button("refresh-library", "Refresh", Some(icon_refresh(14.0, FG)))
            .on_press(|s: &mut AppState| s.refresh_library()),
    );

    let filter_bar = row().gap(6.0).center_y().children(
        LibraryFilter::ALL.into_iter().map(|f| {
            let count = match f {
                LibraryFilter::All => s.games.len(),
                LibraryFilter::Installed => s.installed_count(),
                LibraryFilter::Recent => s
                    .games
                    .iter()
                    .filter(|g| g.last_played_utc.is_some() || g.playtime_minutes > 0)
                    .count(),
                LibraryFilter::Running => s.running_count(),
            };
            filter_pill(f.key(), format!("{} ({count})", f.label()), s.filter == f)
                .on_press(move |s: &mut AppState| s.filter = f)
        }),
    );

    let grid = if filtered.is_empty() {
        el().w_full()
            .h(220.0)
            .center()
            .child(
                text(if s.refreshing {
                    "Loading your games from Exo host…"
                } else {
                    "No games match this filter."
                })
                .font(type_section())
                .color(fg_muted()),
            )
    } else {
        row()
            .w_full()
            .flex_wrap()
            .gap(10.0)
            .children(filtered.into_iter().take(s.visible_limit).map(|g| {
                let is_sel = selected_id.as_deref() == Some(g.id.as_str());
                poster_tile(g, is_sel, s, scale)
            }))
    };

    let mut body = col()
        .w_full()
        .pt(72.0)
        .px(24.0)
        .pb(36.0)
        .gap(18.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    col()
                        .gap(4.0)
                        .child(text("Library").font(type_page()).color(FG))
                        .child(text(subtitle).font(type_small()).color(fg_muted())),
                )
                .child(actions),
        )
        .child(filter_bar)
        .child(grid);

    if total_filtered > s.visible_limit {
        let remaining = total_filtered - s.visible_limit;
        body = body.child(
            row().w_full().justify_center().pt(6.0).child(
                glass_button("show-more-games", "Show all games", None)
                    .label(format!("Show {remaining} more games"))
                    .on_press(move |s: &mut AppState| s.visible_limit = total_filtered),
            ),
        );
    }

    if let Some(notice) = &s.notice {
        body = body.child(
            row().w_full().justify_center().child(
                el().px(14.0)
                    .py(7.0)
                    .radius(RADIUS_PILL)
                    .fill(exo_kit::glass_control_fill())
                    .border(1.0, exo_kit::glass_rim())
                    .child(text(notice.clone()).font(type_small()).color(FG)),
            ),
        );
    }

    body
}
