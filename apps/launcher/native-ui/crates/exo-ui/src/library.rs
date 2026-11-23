use crate::state::{
    AppState, ContextMenuTarget, InputMode, LibraryChip, OpenDropdown, friends_playing,
    library_chip_options, now_unix_ms, title_hue,
};
use exo_kit::{
    BG, FG, GLASS_CONTROL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    SPRING_FLIGHT, SPRING_MOVE, SPRING_QUICK, SelectTrigger, TRANSPARENT, exo_play_button,
    fg_faint, fg_muted, glass_button, glass_control_fill, glass_icon_circle, glass_icon_circle_opt,
    glass_panel_opt, glass_rim, glass_rim_bottom, glass_rim_top, glass_still_control_fill, good,
    hover_fill, icon_chevron_left, icon_chevron_right, icon_download, icon_filter, icon_grid,
    icon_more_horiz, icon_play, icon_plus, icon_search, icon_stop, popover_transition,
    pressed_fill, primary_button, progress_bar, scrim_fill, select_trigger, store_logo,
    surface_card, type_body, type_control, type_first_run, type_section, type_small, type_title,
};
use exo_rpc::Game;
use rune::{
    Align, Cursor, El, Side, SurfaceFit, TextStyle, Tx, col, edit_access, el, oklch, row, text,
    text_input,
};

const HOME_EDGE: f32 = 32.0;
const DECK_CARD_W: f32 = 224.0;
const DECK_CARD_H: f32 = 336.0;
const ALL_GRID_COLS: usize = 11;
const ALL_CARD_W: f32 = 163.63;
const ALL_CARD_H: f32 = 245.45;

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

fn cover_surface(
    game: &Game,
    s: &AppState,
    scale: f32,
    size: [f32; 2],
    dim_uninstalled: bool,
    load_cover: bool,
    inactive_scrim: bool,
) -> El {
    let [w, h] = size;
    let hue = title_hue(&game.title);
    let dim = dim_uninstalled && !game.installed;
    let mut art = el()
        .size(w, h)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD)
        .fill(if dim { BG } else { surface_card() });

    let cover = if load_cover {
        s.cover_image(game, scale)
    } else {
        None
    };

    if let Some(img) = cover {
        if dim {
            art = art.child(
                el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                    .pixels_fit(img.pixels(), SurfaceFit::Fill)
                    .saturate(0.45)
                    .opacity(0.42),
            );
        } else {
            art = art.pixels_fit(img.pixels(), SurfaceFit::Fill);
        }
    } else {
        let mut fallback = el()
            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
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
                    .child(
                        text(game.title.clone())
                            .font(type_section())
                            .color(FG)
                            .wrap(),
                    ),
            );
        if dim {
            fallback = fallback.opacity(0.42);
        }
        art = art.child(fallback);
    }

    if inactive_scrim {
        art = art.child(
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(BG.alpha(0.12)),
        );
    }

    if dim {
        art = art.child(
            el().pin(None, Some(8.0), Some(8.0), None)
                .size(22.0, 22.0)
                .radius(11.0)
                .center()
                .fill(scrim_fill())
                .child(icon_download(13.0, FG.alpha(0.9))),
        );
    }

    if game.is_running {
        art = art.child(
            el().pin(Some(10.0), Some(10.0), None, None)
                .h(24.0)
                .px(9.0)
                .radius(RADIUS_PILL)
                .center()
                .fill(good())
                .child(
                    text("Playing")
                        .font(TextStyle::new(type_small().size, 600.0))
                        .color(PRIMARY_FG),
                ),
        );
    }

    art
}

fn deck_poster_card(game: &Game, active: bool, load_cover: bool, s: &AppState, scale: f32) -> El {
    let id_click = game.id.clone();
    let id_hover = game.id.clone();
    let id_ctx = game.id.clone();
    let flight_id = format!("deck:{}", game.id);
    let hover_key = format!("tile-{}", game.id);
    let hovered = s.hovered_cover_key.as_deref() == Some(hover_key.as_str());
    let hover_key_cb = hover_key.clone();
    let art = cover_surface(
        game,
        s,
        scale,
        [DECK_CARD_W, DECK_CARD_H],
        false,
        load_cover,
        !active,
    )
    .matched(flight_id.clone())
    .motion(SPRING_FLIGHT);

    let mut card = el()
        .key(format!("tile-{}", game.id))
        .role("button")
        .label(game.title.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_scale(PRESS_SCALE)
        .press(pressed_fill())
        .hover(hover_fill())
        .size(DECK_CARD_W, DECK_CARD_H)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .opacity(if active || hovered { 1.0 } else { 0.88 })
        .offset(
            0.0,
            if active {
                -6.0
            } else if hovered {
                -4.0
            } else {
                0.0
            },
        )
        .offset_motion(SPRING_MOVE)
        .motion(SPRING_MOVE)
        .on_hover(move |s: &mut AppState| {
            if s.selected_game_id.as_deref() != Some(id_hover.as_str()) {
                s.selected_game_id = Some(id_hover.clone());
            }
        })
        .on_hover_change(move |s: &mut AppState, inside, _| {
            if inside {
                s.hovered_cover_key = Some(hover_key_cb.clone());
            } else if s.hovered_cover_key.as_deref() == Some(hover_key_cb.as_str()) {
                s.hovered_cover_key = None;
            }
        })
        .on_press_tx(move |s: &mut AppState, tx| {
            s.input_mode = InputMode::Pointer;
            if s.selected_game_id.as_deref() == Some(id_click.as_str()) {
                s.open_game_page_from(&id_click, Some(flight_id.clone()), tx);
            } else {
                s.selected_game_id = Some(id_click.clone());
            }
        })
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.input_mode = InputMode::Pointer;
            s.selected_game_id = Some(id_ctx.clone());
            s.open_context_menu(ContextMenuTarget::GameCard(id_ctx.clone()), p.x, p.y);
        })
        .child(art);

    if active {
        card = card.outline(2.0, 3.0, FG);
    } else if game.is_running {
        card = card.outline(1.0, 2.0, good());
    }
    if s.input_mode == InputMode::Gamepad {
        card = card.focus_ring(2.0, 3.0, FG);
    }

    card
}

pub fn library_filter_overlay(s: &AppState, blur: bool) -> Vec<El> {
    if !s.library_filter_open {
        return Vec::new();
    }
    let options = library_chip_options(&s.games, now_unix_ms());
    let friends_idle = friends_playing(&s.games, &s.discord.friends).is_empty();
    let mut sections: Vec<El> = Vec::new();
    for section in ["Show", "Store", "Genre", "Mode"] {
        let chips: Vec<&LibraryChip> = options.iter().filter(|c| c.section() == section).collect();
        if chips.is_empty() {
            continue;
        }
        let rows: Vec<El> = chips
            .chunks(3)
            .map(|group| {
                let pills: Vec<El> = group
                    .iter()
                    .map(|chip| {
                        let idle = friends_idle && **chip == LibraryChip::Friends;
                        filter_chip(chip, s.filters.contains(chip), idle)
                    })
                    .collect();
                row().gap(6.0).children(pills)
            })
            .collect();
        sections.push(
            col()
                .key(format!("library-filter-section-{}", section.to_lowercase()))
                .gap(6.0)
                .child(text(section).font(type_small()).color(fg_muted()))
                .children(rows),
        );
    }
    if !s.filters.is_empty() {
        sections.push(
            glass_button("library-filter-clear", "Clear all", None)
                .on_press(|s: &mut AppState| s.filters.clear()),
        );
    }
    let panel = glass_panel_opt("library-filter-pop", 300.0, blur)
        .transition(popover_transition())
        .anchored_fit("all-games-filter", Side::Bottom, Align::End, 8.0)
        .px(12.0)
        .py(12.0)
        .gap(12.0)
        .children(sections);
    vec![
        el().key("library-filter-root")
            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
            .modal_tx(true, |s: &mut AppState, _: &mut Tx| {
                s.library_filter_open = false
            })
            .child(panel),
    ]
}

fn filter_chip(chip: &LibraryChip, active: bool, idle: bool) -> El {
    let label = chip.label();
    let target = chip.clone();
    let color = if idle {
        fg_faint()
    } else if active {
        PRIMARY_FG
    } else {
        FG
    };
    let pill = el()
        .key(format!("library-filter-chip-{}", chip.key()))
        .role("option")
        .label(label.clone())
        .focusable(!idle)
        .hit()
        .disabled(idle)
        .cursor(if idle {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .h(30.0)
        .px(13.0)
        .radius(RADIUS_PILL)
        .center()
        .fill(if active { FG } else { TRANSPARENT })
        .border(if active { 0.0 } else { 1.0 }, glass_rim())
        .motion(SPRING_QUICK)
        .child(
            text(label)
                .font(if active {
                    type_section()
                } else {
                    type_control()
                })
                .color(color),
        );
    if idle {
        return pill;
    }
    pill.press_on_down()
        .press_scale(PRESS_SCALE)
        .hover(if active { FG } else { hover_fill() })
        .press(if active { FG } else { pressed_fill() })
        .on_press(
            move |s: &mut AppState| match s.filters.iter().position(|c| *c == target) {
                Some(i) => {
                    s.filters.remove(i);
                }
                None => s.filters.push(target.clone()),
            },
        )
}

fn all_games_poster_tile(game: &Game, s: &AppState, scale: f32) -> El {
    let id = game.id.clone();
    let id_ctx = game.id.clone();
    let flight_id = format!("grid:{}", game.id);
    let hover_key = format!("tile-{}", game.id);
    let hovered = s.hovered_cover_key.as_deref() == Some(hover_key.as_str());
    let hover_key_cb = hover_key.clone();
    let art = cover_surface(game, s, scale, [ALL_CARD_W, ALL_CARD_H], true, true, false)
        .matched(flight_id.clone())
        .motion(SPRING_FLIGHT);
    let mut tile = el()
        .key(hover_key)
        .role("button")
        .label(game.title.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_scale(PRESS_SCALE)
        .size(ALL_CARD_W, ALL_CARD_H)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .offset(0.0, if hovered { -3.0 } else { 0.0 })
        .offset_motion(SPRING_MOVE)
        .motion(SPRING_MOVE)
        .on_hover_change(move |s: &mut AppState, inside, _| {
            if inside {
                s.hovered_cover_key = Some(hover_key_cb.clone());
            } else if s.hovered_cover_key.as_deref() == Some(hover_key_cb.as_str()) {
                s.hovered_cover_key = None;
            }
        })
        .on_press_tx(move |s: &mut AppState, tx| {
            s.input_mode = InputMode::Pointer;
            s.open_game_page_from(&id, Some(flight_id.clone()), tx);
        })
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.input_mode = InputMode::Pointer;
            s.open_context_menu(ContextMenuTarget::GameCard(id_ctx.clone()), p.x, p.y);
        })
        .child(art);
    if game.is_running {
        tile = tile.outline(1.0, 2.0, good());
    }
    if s.input_mode == InputMode::Gamepad {
        tile = tile.focus_ring(2.0, 3.0, FG);
    }
    tile
}

fn install_banner(s: &AppState) -> Option<El> {
    let prog = s.install_progress.as_ref()?;
    if !prog.is_active {
        return None;
    }
    let gid = prog.game_id.clone().unwrap_or_default();
    let found = s.games.iter().find(|g| g.id == gid);
    let title = found
        .map(|g| g.title.clone())
        .unwrap_or_else(|| "Downloading".into());
    let store_badge = found.and_then(|g| store_logo(&g.store, 15.0));
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
            .backdrop_blur(GLASS_CONTROL_SIGMA)
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .child(
                col()
                    .gap(5.0)
                    .child(
                        row()
                            .gap(8.0)
                            .center_y()
                            .children(store_badge)
                            .child(text(title).font(type_control()).color(FG))
                            .child(text(status).font(type_small()).color(fg_muted())),
                    )
                    .child(progress_bar(
                        240.0,
                        (prog.percent as f32 / 100.0).clamp(0.0, 1.0),
                    )),
            )
            .child(
                row()
                    .gap(8.0)
                    .child(
                        glass_button("dl-pause", "Pause", None)
                            .on_press(move |s: &mut AppState| s.pause_install(&gid_pause)),
                    )
                    .child(
                        glass_button("dl-cancel", "Cancel", None)
                            .on_press(move |s: &mut AppState| s.cancel_install(&gid_cancel)),
                    ),
            ),
    )
}

fn view_home_deck(s: &AppState, scale: f32) -> El {
    let filtered = s.filtered_games();
    let active_game = s.selected_game().or_else(|| filtered.first().copied());
    let active_id = active_game.map(|g| g.id.clone());

    let mut hero_art = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG);

    if let Some(game) = active_game {
        if let Some(img) = s.hero_image(game, scale) {
            hero_art = hero_art
                .pixels_fit(img.pixels(), SurfaceFit::Fill)
                .surface_anchor_fraction(0.5, 0.22);
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
                (0.0, BG.alpha(0.26)),
                (0.18, BG.alpha(0.0)),
                (0.48, BG.alpha(0.0)),
                (0.78, BG.alpha(0.56)),
                (1.0, BG),
            ],
        );

    let hero_vignette_h = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::FRAC_PI_2,
            [
                (0.0, BG.alpha(0.62)),
                (0.36, BG.alpha(0.20)),
                (0.70, BG.alpha(0.0)),
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
            row().h(112.0).items_end().child(
                el().max_w(460.0)
                    .max_h(112.0)
                    .w(400.0)
                    .h(96.0)
                    .pixels_fit(logo.pixels(), SurfaceFit::Fit),
            )
        } else {
            row().h(112.0).items_end().child(
                text(game.title.clone())
                    .font(type_first_run())
                    .color(FG)
                    .truncate(),
            )
        };

        let play_icon = if is_stop {
            icon_stop(14.0, PRIMARY_FG)
        } else if is_install || is_update {
            icon_download(14.0, PRIMARY_FG)
        } else {
            icon_play(14.0, PRIMARY_FG)
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
        .on_press_tx(move |s: &mut AppState, tx| {
            s.input_mode = InputMode::Pointer;
            s.open_game_page_from(&gid_more, Some(format!("deck:{gid_more}")), tx);
        });

        col()
            .gap(14.0)
            .max_w(600.0)
            .child(identity)
            .child(row().gap(10.0).center_y().child(play_btn).child(more_btn))
    } else {
        col().gap(10.0).child(
            text(if s.refreshing {
                "Scanning installed stores…"
            } else {
                "No games found yet"
            })
            .font(type_first_run())
            .color(FG),
        )
    };

    let inline_search = if s.library_search_open || !s.library_search.text.is_empty() {
        row()
            .key("home-inline-search")
            .cursor(Cursor::Text)
            .w(240.0)
            .h(34.0)
            .pl(12.0)
            .pr(8.0)
            .gap(8.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_control_fill())
            .backdrop_blur(GLASS_CONTROL_SIGMA)
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .child(icon_search(14.0, fg_muted()))
            .child(
                text_input(&s.library_search, type_small())
                    .key("library-search-input")
                    .label("Search library")
                    .grow(1.0)
                    .single_line()
                    .color(FG)
                    .placeholder("Filter games…", fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| &mut s.library_search)),
            )
    } else {
        glass_icon_circle(
            "home-search-btn",
            "Search library",
            34.0,
            icon_search(15.0, FG),
        )
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
                    s.library_add_open = false;
                }),
        )
        .child(
            glass_icon_circle("home-more-btn", "More", 34.0, icon_more_horiz(15.0, FG)).on_press(
                |s: &mut AppState| {
                    let next = !s.library_more_open;
                    s.library_add_open = false;
                    s.library_more_open = next;
                },
            ),
        )
        .child(
            glass_icon_circle("home-add-btn", "Add", 34.0, icon_plus(15.0, FG))
                .on_press(|s: &mut AppState| s.toggle_library_add()),
        );

    let foot = row()
        .w_full()
        .px(HOME_EDGE)
        .pb(16.0)
        .gap(20.0)
        .justify_between()
        .items_end()
        .child(hero_lockup)
        .child(right_tools);

    let stage = el()
        .w_full()
        .grow(1.0)
        .min_h(280.0)
        .clip_rounded(0.0)
        .fill(BG)
        .child(hero_art)
        .child(
            col()
                .w_full()
                .h_full()
                .justify_end()
                .children(install_banner(s).map(|b| el().w_full().px(HOME_EDGE).pb(12.0).child(b)))
                .child(foot),
        );

    let deck_games: Vec<&Game> = filtered.iter().copied().take(s.visible_limit).collect();
    let deck_total = deck_games.len();
    let scroll_x = s.deck_scroll.get().offset[0].max(0.0);
    let stride = DECK_CARD_W + 14.0;
    let first_visible = (scroll_x / stride).floor() as usize;
    let load_start = first_visible.saturating_sub(4);
    let load_end = (first_visible + 14).min(deck_total);
    let deck_cards: Vec<El> = deck_games
        .iter()
        .enumerate()
        .map(|(idx, g)| {
            let active = active_id.as_deref() == Some(g.id.as_str());
            let load_cover = active || (idx >= load_start && idx < load_end);
            deck_poster_card(g, active, load_cover, s, scale)
        })
        .collect();

    let (can_prev, can_next) = s.deck_can_page(deck_total);
    let deck_scroll = s.deck_scroll.clone();

    let track = row()
        .key("home-deck-track")
        .w_full()
        .h_full()
        .pt(12.0)
        .pb(6.0)
        .px(HOME_EDGE)
        .gap(14.0)
        .items_end()
        .scroll_x()
        .on_scroll_offset(move |pos| deck_scroll.set(pos))
        .clip_fade_linear(
            if can_prev { 44.0 } else { 0.0 },
            0.0,
            if can_next { 44.0 } else { 0.0 },
            0.0,
        )
        .children(deck_cards);

    let mut deck_rail = el().w_full().h(DECK_CARD_H + 18.0).child(track);

    if can_prev {
        deck_rail = deck_rail.child(
            el().pin(Some(10.0), Some(160.0), None, None).child(
                glass_icon_circle(
                    "deck-prev",
                    "Previous games",
                    34.0,
                    icon_chevron_left(16.0, FG),
                )
                .on_press_tx(move |s: &mut AppState, tx| {
                    s.input_mode = InputMode::Pointer;
                    s.page_deck(-1.0, deck_total, tx);
                }),
            ),
        );
    }

    if can_next {
        deck_rail = deck_rail.child(
            el().pin(None, Some(160.0), Some(10.0), None).child(
                glass_icon_circle(
                    "deck-next",
                    "More games",
                    34.0,
                    icon_chevron_right(16.0, FG),
                )
                .on_press_tx(move |s: &mut AppState, tx| {
                    s.input_mode = InputMode::Pointer;
                    s.page_deck(1.0, deck_total, tx);
                }),
            ),
        );
    }

    let lower = col()
        .w_full()
        .min_h(DECK_CARD_H + 30.0)
        .pb(12.0)
        .child(deck_rail);

    col()
        .key("library-home-deck")
        .w_full()
        .h_full()
        .pt(70.0)
        .gap(8.0)
        .fill(BG)
        .child(stage)
        .child(lower)
}

fn view_all_games(s: &AppState, scale: f32) -> El {
    let filtered = s.filtered_games();
    let total_filtered = filtered.len();

    let back_btn = row()
        .key("all-games-back")
        .role("button")
        .label("Library")
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(34.0)
        .pl(10.0)
        .pr(14.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .backdrop_blur(GLASS_CONTROL_SIGMA)
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| {
            s.library_all_games = false;
            s.library_more_open = false;
            s.library_add_open = false;
        })
        .child(icon_chevron_left(16.0, FG))
        .child(text("Library").font(type_control()).color(FG));

    let search_bar = row()
        .key("all-games-search")
        .cursor(Cursor::Text)
        .w(380.0)
        .h(32.0)
        .pl(12.0)
        .pr(6.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(icon_search(16.0, fg_muted()))
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

    let filter_label = if s.filters.is_empty() {
        "Filter".to_string()
    } else {
        format!("Filter · {}", s.filters.len())
    };

    let filter_btn = row()
        .key("all-games-filter")
        .role("button")
        .label(filter_label.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .pl(12.0)
        .pr(14.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.library_filter_open = !s.library_filter_open)
        .child(icon_filter(16.0, FG))
        .child(text(filter_label).font(type_control()).color(FG));

    let sort_btn = select_trigger(
        SelectTrigger::new("sort-cycle", "Sort library", s.sort.label())
            .open(s.open_dropdown == Some(OpenDropdown::LibrarySort))
            .gamepad_focus(s.input_mode == InputMode::Gamepad)
            .flat(true),
        |s: &mut AppState, _| s.toggle_dropdown(OpenDropdown::LibrarySort),
    );

    let more_btn = glass_icon_circle_opt(
        "all-games-more-btn",
        "More",
        32.0,
        icon_more_horiz(16.0, FG),
        false,
    )
    .on_press(|s: &mut AppState| {
        let next = !s.library_more_open;
        s.library_add_open = false;
        s.library_more_open = next;
    });

    let add_btn = primary_button(
        "all-games-add-btn",
        "Add",
        Some(icon_plus(14.0, PRIMARY_FG)),
    )
    .on_press(|s: &mut AppState| s.toggle_library_add());

    let head = row()
        .w_full()
        .pt(6.0)
        .px(10.0)
        .pb(16.0)
        .gap(16.0)
        .center_y()
        .child(row().grow(1.0).min_w(0.0).center_y().child(back_btn))
        .child(search_bar)
        .child(
            row()
                .grow(1.0)
                .min_w(0.0)
                .justify_end()
                .gap(8.0)
                .center_y()
                .child(filter_btn)
                .child(sort_btn)
                .child(more_btn)
                .child(add_btn),
        );

    let shelf_head = row()
        .w_full()
        .h(37.0)
        .pt(4.0)
        .px(10.0)
        .pb(6.0)
        .center_y()
        .child(
            text(if s.library_show_hidden {
                "Hidden games"
            } else {
                "All games"
            })
            .font(type_title())
            .color(FG),
        );

    let visible: Vec<&Game> = filtered.into_iter().take(s.visible_limit).collect();

    let grid = if visible.is_empty() {
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
                        s.filters.clear();
                        s.library_show_hidden = false;
                        s.library_search.text.clear();
                        s.library_search.sync_invariants();
                    },
                ),
            )
    } else {
        let rows: Vec<El> = visible
            .chunks(ALL_GRID_COLS)
            .map(|chunk| {
                row()
                    .w_full()
                    .gap(10.0)
                    .children(chunk.iter().map(|g| all_games_poster_tile(g, s, scale)))
            })
            .collect();
        col().w_full().pt(8.0).px(10.0).gap(10.0).children(rows)
    };

    let mut page = col()
        .key("library-all-games")
        .w_full()
        .pt(70.0)
        .pb(48.0)
        .child(head)
        .children(install_banner(s).map(|b| el().w_full().px(10.0).pb(12.0).child(b)))
        .child(shelf_head)
        .child(grid);

    if total_filtered > s.visible_limit {
        let remaining = total_filtered - s.visible_limit;
        page = page.child(
            row().w_full().justify_center().pt(16.0).child(
                row()
                    .key("load-more-games")
                    .role("button")
                    .label("Show more games")
                    .cursor(Cursor::Pointer)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .h(34.0)
                    .px(16.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(glass_control_fill())
                    .backdrop_blur(GLASS_CONTROL_SIGMA)
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

    page
}

pub fn view(s: &AppState, scale: f32) -> El {
    if s.library_all_games {
        view_all_games(s, scale)
    } else {
        view_home_deck(s, scale)
    }
}

#[cfg(test)]
mod tests {
    use crate::shell;
    use crate::state::AppState;
    use rune::motion_gate::Run;
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Runtime};

    #[test]
    fn library_cover_flight_nav_pill_and_deck_motion_gate() {
        for reduce_motion in [false, true] {
            let mut state = AppState::with_demo_games();
            state.settings.reduce_motion = reduce_motion;
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();

            if !reduce_motion {
                run.act("open game page from deck", |rt| {
                    Run::tap(rt, "home-game-details")
                });
                run.play(0.12);
                run.act("mid-flight reverse back to deck", |rt| {
                    Run::tap(rt, "gp-back")
                });
                run.play(0.5);
            }

            run.act("open game page settled", |rt| {
                Run::tap(rt, "home-game-details")
            });
            run.play(0.5);
            run.act("return to library", |rt| Run::tap(rt, "gp-back"));
            run.play(0.5);

            run.act("switch to all games", |rt| {
                Run::tap(rt, "home-all-games-btn")
            });
            run.play(0.4);
            run.act("open game page from grid", |rt| {
                Run::tap(rt, "tile-demo:cyberpunk")
            });
            run.play(0.5);
            run.act("return to grid", |rt| Run::tap(rt, "gp-back"));
            run.play(0.5);

            run.act("glide nav pill to Store", |rt| Run::tap(rt, "nav-store"));
            run.play(0.45);
            run.act("glide nav pill to Music", |rt| Run::tap(rt, "nav-music"));
            run.play(0.45);
            run.act("glide nav pill to Library", |rt| {
                Run::tap(rt, "nav-library")
            });
            run.play(0.45);

            let findings: Vec<_> = run
                .finish()
                .into_iter()
                .filter(|f| {
                    if f.kind == "pop-out"
                        && (f.element.contains("nav-active-pill")
                            || f.element.contains("gp-poster-")
                            || f.element.contains("tile-"))
                    {
                        return false;
                    }
                    if f.kind == "jerk" && f.element.contains("tile-") {
                        return false;
                    }
                    if reduce_motion
                        && matches!(
                            f.kind,
                            "jump" | "text-swap" | "pop-in" | "pop-out" | "flash"
                        )
                    {
                        return false;
                    }
                    true
                })
                .collect();
            assert!(
                findings.is_empty(),
                "library motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }

    #[test]
    fn library_and_captures_popover_geometry_and_blur_cap() {
        let mut rt = Runtime::new(
            AppState::with_demo_games(),
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let deck_blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            deck_blurs <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "Library HomeDeck exceeded EXO_MAX_BACKDROP_BLURS: {deck_blurs}"
        );
        assert!(
            deck_blurs >= 8,
            "Library HomeDeck should render real Liquid Glass across top bar and hero controls, got {deck_blurs}"
        );

        rt.update(|s, _| s.capture_menu_open = true);
        rt.frame(2.0 / 60.0);
        let frame0_blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            frame0_blurs > deck_blurs,
            "Captures popover must render Liquid Glass on frame 0 (deck={deck_blurs}, frame0={frame0_blurs})"
        );
        rt.settle_all();
        rt.frame(3.0 / 60.0);

        let cap_blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            cap_blurs <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "Captures popover exceeded EXO_MAX_BACKDROP_BLURS: {cap_blurs}"
        );

        let (btn_rect, _) = rt
            .presented("top-capture-btn")
            .expect("top-capture-btn presented");
        let (menu_rect, _) = rt
            .presented("capture-popover-menu")
            .expect("capture-popover-menu presented");
        assert!(
            (menu_rect[1] - 56.0).abs() <= 1.0,
            "expected captures popover y=56.0, got {}",
            menu_rect[1]
        );
        let btn_right = btn_rect[0] + btn_rect[2];
        let menu_right = menu_rect[0] + menu_rect[2];
        assert!(
            (menu_right - btn_right).abs() <= 1.0,
            "expected captures popover right ({menu_right}) aligned to capture button right ({btn_right})"
        );
    }
}
