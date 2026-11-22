use crate::state::{AppState, LibraryFilter, title_hue};
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_MOVE, SPRING_QUICK, TRANSPARENT, exo_play_button, fg_muted, glass_button,
    glass_control_fill, glass_icon_circle, glass_panel, glass_rim, glass_rim_bottom, glass_rim_top,
    good, hover_fill, icon_chevron_down, icon_chevron_left, icon_chevron_right, icon_download,
    icon_filter, icon_grid, icon_key, icon_more_horiz, icon_play, icon_plus, icon_refresh,
    icon_search, icon_sparkles, icon_stop, pressed_fill, primary_button, progress_bar, type_body,
    type_control, type_section, type_small,
};
use exo_rpc::Game;
use rune::{El, SurfaceFit, TextStyle, col, edit_access, el, oklch, row, text, text_input};

fn primary_verb(game: &Game) -> &'static str {
    if game.is_running {
        if game.can_stop { "Stop" } else { "Resume" }
    } else if game.update_available {
        "Update"
    } else if game.installed {
        "Play"
    } else {
        "Install"
    }
}

fn cover_surface(game: &Game, s: &AppState, scale: f32, w: f32, h: f32, dim_uninstalled: bool) -> El {
    let hue = title_hue(&game.title);
    let mut art = el()
        .size(w, h)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD);

    if let Some(img) = s.cover_image(game, scale) {
        art = art.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        art = art
            .linear_gradient(
                0.35,
                [
                    (0.0, oklch(0.38, 0.14, hue)),
                    (0.55, oklch(0.20, 0.08, (hue + 24.0) % 360.0)),
                    (1.0, oklch(0.08, 0.02, hue)),
                ],
            )
            .child(
                col()
                    .w_full()
                    .h_full()
                    .px(12.0)
                    .py(12.0)
                    .justify_end()
                    .child(text(game.title.clone()).font(type_section()).color(FG).wrap()),
            );
    }

    if dim_uninstalled && !game.installed {
        art = art
            .child(
                el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                    .fill(BG.alpha(0.54)),
            )
            .child(
                el().pin(None, Some(8.0), Some(8.0), None)
                    .size(24.0, 24.0)
                    .radius(12.0)
                    .center()
                    .fill(BG.alpha(0.68))
                    .border(1.0, FG.alpha(0.14))
                    .child(icon_download(12.0, FG.alpha(0.85))),
            );
    }

    if game.is_running {
        art = art.child(
            el().pin(Some(8.0), Some(8.0), None, None)
                .h(20.0)
                .px(8.0)
                .radius(RADIUS_PILL)
                .center()
                .fill(BG.alpha(0.76))
                .child(
                    row()
                        .gap(5.0)
                        .center_y()
                        .child(el().size(6.0, 6.0).radius(3.0).fill(good()))
                        .child(text("Playing").font(type_small()).color(FG)),
                ),
        );
    }

    art
}

fn deck_poster_card(game: &Game, active: bool, s: &AppState, scale: f32) -> El {
    let id_click = game.id.clone();
    let id_hover = game.id.clone();
    let art = cover_surface(game, s, scale, 154.0, 231.0, false);

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
        .continuous_corners()
        .border(
            if active {
                2.0
            } else if game.is_running {
                1.0
            } else {
                0.0
            },
            if active {
                FG
            } else if game.is_running {
                good()
            } else {
                TRANSPARENT
            },
        )
        .motion(SPRING_MOVE)
        .on_hover(move |s: &mut AppState| {
            if s.selected_game_id.as_deref() != Some(id_hover.as_str()) {
                s.selected_game_id = Some(id_hover.clone());
            }
        })
        .on_press(move |s: &mut AppState| {
            if s.selected_game_id.as_deref() == Some(id_click.as_str()) {
                s.open_game_page(&id_click);
            } else {
                s.selected_game_id = Some(id_click.clone());
            }
        })
        .child(art)
}

fn all_games_poster_tile(game: &Game, s: &AppState, scale: f32) -> El {
    let id = game.id.clone();
    let art = cover_surface(game, s, scale, 146.0, 219.0, true);
    el().key(format!("tile-{}", game.id))
        .role("button")
        .label(game.title.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .border(if game.is_running { 1.5 } else { 0.0 }, good())
        .motion(SPRING_MOVE)
        .on_press(move |s: &mut AppState| s.open_game_page(&id))
        .child(art)
}

fn install_banner(s: &AppState) -> Option<El> {
    let prog = s.install_progress.as_ref()?;
    if !prog.is_active {
        return None;
    }
    let gid = prog.game_id.clone().unwrap_or_default();
    let title = s
        .games
        .iter()
        .find(|g| g.id == gid)
        .map(|g| g.title.clone())
        .unwrap_or_else(|| "Downloading".into());
    let pct = (prog.percent.clamp(0.0, 100.0)).round() as u32;
    let status = prog
        .status
        .clone()
        .unwrap_or_else(|| format!("{} · {pct}%", prog.phase));
    let gid_pause = gid.clone();
    let gid_cancel = gid;

    Some(
        row()
            .w_full()
            .px(14.0)
            .py(10.0)
            .justify_between()
            .center_y()
            .radius(RADIUS_PANEL)
            .continuous_corners()
            .fill(glass_control_fill())
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .child(
                col()
                    .gap(5.0)
                    .child(
                        row()
                            .gap(10.0)
                            .center_y()
                            .child(text(title).font(type_control()).color(FG))
                            .child(text(status).font(type_small()).color(fg_muted())),
                    )
                    .child(progress_bar(240.0, (prog.percent / 100.0) as f32)),
            )
            .child(
                row()
                    .gap(8.0)
                    .center_y()
                    .child(
                        glass_button("install-pause", "Pause", None)
                            .on_press(move |s: &mut AppState| s.pause_install(&gid_pause)),
                    )
                    .children(prog.can_cancel.then(|| {
                        glass_button("install-cancel", "Cancel", None)
                            .on_press(move |s: &mut AppState| s.cancel_install(&gid_cancel))
                    })),
            ),
    )
}

fn library_more_popover() -> El {
    let menu_item = |key: &'static str, label: &'static str, icon: El| {
        row()
            .key(key)
            .role("menuitem")
            .label(label)
            .focusable(true)
            .hit()
            .press_on_down()
            .w_full()
            .h(34.0)
            .px(10.0)
            .gap(10.0)
            .center_y()
            .radius(RADIUS_SMALL)
            .fill(TRANSPARENT)
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .child(icon)
            .child(text(label).font(type_control()).color(FG))
    };

    glass_panel("library-more-menu", 204.0)
        .px(6.0)
        .py(6.0)
        .gap(2.0)
        .child(
            menu_item("lib-menu-refresh", "Refresh library", icon_refresh(14.0, FG)).on_press(
                |s: &mut AppState| {
                    s.library_more_open = false;
                    s.refresh_library();
                },
            ),
        )
        .child(
            menu_item("lib-menu-key", "Activate a key", icon_key(14.0, FG)).on_press_tx(
                |s: &mut AppState, tx| {
                    s.library_more_open = false;
                    s.open_key_modal(tx);
                },
            ),
        )
        .child(
            menu_item("lib-menu-tour", "Guided tour", icon_sparkles(14.0, FG)).on_press(
                |s: &mut AppState| {
                    s.library_more_open = false;
                    s.start_tour();
                },
            ),
        )
}

fn view_home_deck(s: &AppState, scale: f32) -> El {
    let filtered = s.filtered_games();
    let active_game = s
        .selected_game()
        .or_else(|| filtered.first().copied());
    let active_id = active_game.map(|g| g.id.clone());

    let mut hero_art = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(210.0))
        .fill(BG);

    if let Some(game) = active_game {
        if let Some(img) = s.hero_image(game, scale) {
            hero_art = hero_art.pixels_fit(img.pixels(), SurfaceFit::Fill);
        } else {
            let hue = title_hue(&game.title);
            hero_art = hero_art.linear_gradient(
                0.35,
                [
                    (0.0, oklch(0.28, 0.12, hue)),
                    (0.55, oklch(0.14, 0.06, (hue + 28.0) % 360.0)),
                    (1.0, BG),
                ],
            );
        }
    }

    let hero_vignette_v = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, BG.alpha(0.32)),
                (0.18, BG.alpha(0.0)),
                (0.50, BG.alpha(0.06)),
                (0.80, BG.alpha(0.68)),
                (1.0, BG),
            ],
        );

    let hero_vignette_h = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::FRAC_PI_2,
            [
                (0.0, BG.alpha(0.64)),
                (0.34, BG.alpha(0.16)),
                (0.70, BG.alpha(0.0)),
                (1.0, BG.alpha(0.42)),
            ],
        );

    hero_art = hero_art.child(hero_vignette_v).child(hero_vignette_h);

    let hero_lockup = if let Some(game) = active_game {
        let verb = primary_verb(game);
        let gid_play = game.id.clone();
        let gid_more = game.id.clone();
        let is_stop = game.is_running && game.can_stop;
        let is_install = !game.installed && !game.is_running;
        let is_update = game.update_available && game.installed && !game.is_running;

        let identity = if let Some(logo) = s.logo_image(game, scale) {
            el().w(400.0)
                .h(88.0)
                .pixels_fit(logo.pixels(), SurfaceFit::Fit)
        } else {
            text(game.title.clone())
                .font(TextStyle::new(30.0, 700.0))
                .color(FG)
                .truncate()
        };

        let play_icon = if is_stop {
            icon_stop(13.0, PRIMARY_FG)
        } else if is_install || is_update {
            icon_download(14.0, PRIMARY_FG)
        } else {
            icon_play(13.0, PRIMARY_FG)
        };

        let play_btn = exo_play_button("play-selected", verb, Some(play_icon)).on_press(
            move |s: &mut AppState| {
                if is_stop {
                    s.stop_game(&gid_play);
                } else if is_install {
                    s.install_game(&gid_play);
                } else if is_update {
                    s.update_game(&gid_play);
                } else {
                    s.launch_game(&gid_play);
                }
            },
        );

        let more_btn = glass_icon_circle(
            "home-game-details",
            "Game details",
            40.0,
            icon_more_horiz(16.0, FG),
        )
        .on_press(move |s: &mut AppState| s.open_game_page(&gid_more));

        col()
            .gap(14.0)
            .max_w(580.0)
            .child(identity)
            .child(row().gap(10.0).center_y().child(play_btn).child(more_btn))
    } else {
        col().gap(10.0).child(
            text(if s.refreshing {
                "Scanning installed stores…"
            } else {
                "No games found yet"
            })
            .font(TextStyle::new(28.0, 700.0))
            .color(FG),
        )
    };

    let inline_search = if s.library_search_open || !s.library_search.text.is_empty() {
        row()
            .key("home-inline-search")
            .w(210.0)
            .h(34.0)
            .px(12.0)
            .gap(8.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_control_fill())
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .child(icon_search(14.0, fg_muted()))
            .child(
                text_input(&s.library_search, type_body())
                    .key("library-search-input")
                    .label("Search library")
                    .grow(1.0)
                    .single_line()
                    .color(FG)
                    .placeholder("Search…", fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| &mut s.library_search)),
            )
    } else {
        glass_icon_circle("home-search-btn", "Search library", 34.0, icon_search(15.0, FG))
            .on_press_tx(|s: &mut AppState, tx| {
                s.library_search_open = true;
                tx.focus("library-search-input", false);
            })
    };

    let right_tools = row()
        .gap(8.0)
        .center_y()
        .child(inline_search)
        .child(
            glass_icon_circle("home-all-games-btn", "All games", 34.0, icon_grid(15.0, FG))
                .on_press(|s: &mut AppState| {
                    s.library_all_games = true;
                    s.library_more_open = false;
                }),
        )
        .child(
            glass_icon_circle(
                "home-more-btn",
                "Library options",
                34.0,
                icon_more_horiz(15.0, FG),
            )
            .on_press(|s: &mut AppState| {
                s.library_more_open = !s.library_more_open;
            }),
        )
        .child(
            glass_icon_circle(
                "activate-key-btn",
                "Activate key",
                34.0,
                icon_plus(15.0, FG),
            )
            .on_press_tx(|s: &mut AppState, tx| s.open_key_modal(tx)),
        );

    let deck_total = filtered.len();
    let deck_start = s.deck_start.min(deck_total.saturating_sub(1));
    let deck_cards: Vec<El> = filtered
        .iter()
        .skip(deck_start)
        .take(12)
        .map(|g| deck_poster_card(g, active_id.as_deref() == Some(g.id.as_str()), s, scale))
        .collect();

    let mut deck_rail = el().w_full().h(252.0).child(
        row()
            .key("home-deck-track")
            .w_full()
            .h_full()
            .px(24.0)
            .py(6.0)
            .gap(12.0)
            .center_y()
            .scroll_x()
            .children(deck_cards),
    );

    if deck_start > 0 {
        deck_rail = deck_rail.child(
            el().pin(Some(10.0), Some(108.0), None, None).child(
                glass_icon_circle(
                    "deck-prev",
                    "Previous games",
                    34.0,
                    icon_chevron_left(15.0, FG),
                )
                .on_press(|s: &mut AppState| {
                    s.deck_start = s.deck_start.saturating_sub(4);
                }),
            ),
        );
    }

    if deck_start + 7 < deck_total {
        deck_rail = deck_rail.child(
            el().pin(None, Some(108.0), Some(10.0), None).child(
                glass_icon_circle(
                    "deck-next",
                    "More games",
                    34.0,
                    icon_chevron_right(15.0, FG),
                )
                .on_press(move |s: &mut AppState| {
                    let max_start = deck_total.saturating_sub(6);
                    s.deck_start = (s.deck_start + 4).min(max_start);
                }),
            ),
        );
    }

    let foot = row()
        .w_full()
        .px(26.0)
        .pb(12.0)
        .justify_between()
        .items_end()
        .child(hero_lockup)
        .child(right_tools);

    let mut page = el()
        .key("library-home-deck")
        .w_full()
        .h_full()
        .fill(BG)
        .child(hero_art)
        .child(
            col()
                .w_full()
                .h_full()
                .justify_end()
                .pb(10.0)
                .children(install_banner(s).map(|b| el().w_full().px(26.0).pb(10.0).child(b)))
                .child(foot)
                .child(deck_rail),
        );

    if s.library_more_open {
        page = page.child(
            el().pin(None, None, Some(68.0), Some(316.0))
                .child(library_more_popover()),
        );
    }

    page
}

fn view_all_games(s: &AppState, scale: f32) -> El {
    let filtered = s.filtered_games();
    let total_filtered = filtered.len();

    let back_btn = glass_button(
        "all-games-back",
        "Library",
        Some(icon_chevron_left(14.0, FG)),
    )
    .on_press(|s: &mut AppState| {
        s.library_all_games = false;
        s.library_more_open = false;
    });

    let search_bar = row()
        .key("all-games-search")
        .w(360.0)
        .h(34.0)
        .px(12.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(icon_search(14.0, fg_muted()))
        .child(
            text_input(&s.library_search, type_body())
                .key("all-games-search-input")
                .label("Search your games")
                .grow(1.0)
                .single_line()
                .color(FG)
                .placeholder("Search your games", fg_muted())
                .on_edit(edit_access(|s: &mut AppState| &mut s.library_search)),
        );

    let filter_label = match s.filter {
        LibraryFilter::All => "Filter",
        LibraryFilter::Installed => "Installed",
        LibraryFilter::Recent => "Recent",
        LibraryFilter::Running => "Running",
    };

    let filter_btn = glass_button("all-games-filter", filter_label, Some(icon_filter(14.0, FG)))
        .on_press(|s: &mut AppState| {
            s.filter = match s.filter {
                LibraryFilter::All => LibraryFilter::Installed,
                LibraryFilter::Installed => LibraryFilter::Recent,
                LibraryFilter::Recent => LibraryFilter::Running,
                LibraryFilter::Running => LibraryFilter::All,
            };
        });

    let sort_btn = row()
        .key("sort-cycle")
        .role("button")
        .label(s.sort.label())
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.sort = s.sort.next())
        .child(text(s.sort.label()).font(type_control()).color(FG))
        .child(icon_chevron_down(13.0, fg_muted()));

    let more_btn = glass_icon_circle(
        "all-games-more-btn",
        "Library options",
        32.0,
        icon_more_horiz(14.0, FG),
    )
    .on_press(|s: &mut AppState| s.library_more_open = !s.library_more_open);

    let add_btn = primary_button("activate-key-btn", "Add", Some(icon_plus(14.0, PRIMARY_FG)))
        .on_press_tx(|s: &mut AppState, tx| s.open_key_modal(tx));

    let head = row()
        .w_full()
        .justify_between()
        .center_y()
        .child(back_btn)
        .child(search_bar)
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(filter_btn)
                .child(sort_btn)
                .child(more_btn)
                .child(add_btn),
        );

    let visible = filtered.into_iter().take(s.visible_limit);
    let tiles: Vec<El> = visible
        .map(|g| all_games_poster_tile(g, s, scale))
        .collect();

    let grid = if tiles.is_empty() {
        col()
            .w_full()
            .py(64.0)
            .center()
            .gap(10.0)
            .child(
                text("No games match this filter")
                    .font(type_section())
                    .color(FG),
            )
            .child(
                glass_button("reset-filters", "Show all games", None).on_press(
                    |s: &mut AppState| {
                        s.filter = LibraryFilter::All;
                        s.library_search.text.clear();
                        s.library_search.sync_invariants();
                    },
                ),
            )
    } else {
        row().w_full().gap(10.0).flex_wrap().children(tiles)
    };

    let mut page = col()
        .key("library-all-games")
        .w_full()
        .px(12.0)
        .pt(60.0)
        .pb(36.0)
        .gap(14.0)
        .child(head)
        .children(install_banner(s))
        .child(
            text("All games")
                .font(TextStyle::new(20.0, 650.0))
                .color(FG),
        )
        .child(grid);

    if total_filtered > s.visible_limit {
        let remaining = total_filtered - s.visible_limit;
        page = page.child(
            row().w_full().justify_center().pt(10.0).child(
                row()
                    .key("load-more-games")
                    .role("button")
                    .label("Show more games")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .h(34.0)
                    .px(16.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(glass_control_fill())
                    .hover(hover_fill())
                    .border(1.0, glass_rim())
                    .on_press(|s: &mut AppState| s.visible_limit += 96)
                    .child(
                        text(format!("Show more ({remaining} remaining)"))
                            .font(type_control())
                            .color(FG),
                    ),
            ),
        );
    }

    if s.library_more_open {
        page = page.child(
            el().pin(None, Some(98.0), Some(76.0), None)
                .child(library_more_popover()),
        );
    }

    page
}

pub fn view(s: &AppState, scale: f32) -> El {
    if s.library_all_games {
        view_all_games(s, scale)
    } else {
        view_home_deck(s, scale)
    }
}
