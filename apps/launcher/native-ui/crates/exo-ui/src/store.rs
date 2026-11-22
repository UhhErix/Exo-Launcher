use crate::state::{AppState, InputMode, title_hue};
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_FLIGHT, SPRING_MOVE, SPRING_QUICK, TRANSPARENT, amoled_card, discount_badge, fg_faint,
    fg_muted, glass_button, glass_control_fill, glass_icon_circle, glass_rim, glass_rim_bottom,
    glass_rim_top, good, hover_fill, icon_back, icon_bookmark, icon_chevron_left,
    icon_chevron_right, icon_external, icon_refresh, icon_search, pressed_fill, primary_button,
    scaled, selected_fill, stat_chip, type_body, type_control, type_page, type_section, type_small,
    type_title,
};
use exo_rpc::{StoreGame, StoreTile, types::format_cents};
use rune::{
    El, SpringPair, SurfaceFit, TextStyle, Transition, col, edit_access, el, oklch, row, text,
    text_input,
};

fn review_line(tile: &StoreTile) -> String {
    match (&tile.review, tile.review_percent) {
        (Some(w), Some(p)) if !w.is_empty() => format!("{w} · {p}%"),
        (Some(w), None) if !w.is_empty() => w.clone(),
        _ => String::new(),
    }
}

fn best_pick_art(tile: &StoreTile) -> Option<&str> {
    tile.art.as_ref().and_then(|a| {
        a.capsule
            .as_deref()
            .or(a.header.as_deref())
            .or_else(|| a.best_hero())
    })
}

fn tile_meta_row(tile: &StoreTile) -> Option<El> {
    if tile.soon {
        let label = tile.when.as_deref().unwrap_or("Coming soon").to_string();
        return Some(
            row()
                .w_full()
                .h(scaled(18.0))
                .gap(8.0)
                .center_y()
                .child(text(label).font(type_body()).color(FG).truncate()),
        );
    }
    if tile.free {
        return Some(
            row()
                .w_full()
                .h(scaled(18.0))
                .gap(8.0)
                .center_y()
                .child(text("Free").font(type_body()).color(FG)),
        );
    }
    let price = tile.display_price();
    if price.is_empty() && tile.discount <= 0 {
        return None;
    }
    let mut meta = row().w_full().h(scaled(18.0)).gap(8.0).center_y();
    if tile.discount > 0 {
        meta = meta.child(
            el().px(4.0)
                .radius(RADIUS_PILL)
                .fill(good().alpha(0.18))
                .child(
                    text(format!("-{}%", tile.discount))
                        .font(type_small())
                        .color(good()),
                ),
        );
    }
    if !price.is_empty() {
        meta = meta.child(text(price).font(type_body()).color(FG).truncate());
    }
    if tile.discount > 0 {
        let was = tile.was_text.clone().or_else(|| {
            tile.initial
                .filter(|&v| v > 0)
                .map(|v| format_cents(v, tile.currency.as_deref()))
        });
        if let Some(was_str) = was {
            meta = meta.child(text(was_str).font(type_small()).color(fg_faint()));
        }
    }
    Some(meta)
}

fn store_tile_card(tile: &StoreTile, scope: &str, s: &AppState, scale: f32) -> El {
    let hue = title_hue(&tile.name);
    let art_url = tile.art.as_ref().and_then(|a| a.best_poster());
    let img = art_url.and_then(|u| s.remote_image_sized(u, 320.0, scale));
    let card_key = format!("store-tile-{scope}-{}", tile.app_id);
    let flight_id = format!("store:{scope}:{}", tile.app_id);
    let hovered = s.hovered_cover_key.as_deref() == Some(card_key.as_str());
    let hover_key = card_key.clone();

    let mut poster = el()
        .key(format!("store-poster-{scope}-{}", tile.app_id))
        .size(168.0, 252.0)
        .mb(4.0)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD)
        .matched(flight_id.clone())
        .motion(SPRING_FLIGHT);
    if let Some(loaded) = img {
        poster = poster.pixels_fit(loaded.pixels(), SurfaceFit::Fill);
    } else {
        poster = poster.linear_gradient(
            0.35,
            [(0.0, oklch(0.42, 0.14, hue)), (1.0, oklch(0.10, 0.03, hue))],
        );
    }

    if tile.wished {
        poster = poster.child(
            el().pin(None, Some(8.0), Some(8.0), None)
                .size(28.0, 28.0)
                .radius(14.0)
                .center()
                .fill(BG.alpha(0.64))
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(icon_bookmark(14.0, FG)),
        );
    }

    let rev = review_line(tile);
    let app_id = tile.app_id.clone();

    let mut card = col()
        .key(card_key)
        .role("button")
        .label(tile.name.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .w(168.0)
        .gap(4.0)
        .offset(0.0, if hovered { -3.0 } else { 0.0 })
        .offset_motion(SPRING_MOVE)
        .motion(SPRING_MOVE)
        .on_hover_change(move |s: &mut AppState, inside, _tx| {
            if inside {
                s.hovered_cover_key = Some(hover_key.clone());
            } else if s.hovered_cover_key.as_deref() == Some(hover_key.as_str()) {
                s.hovered_cover_key = None;
            }
        })
        .on_press_tx(move |s: &mut AppState, tx| {
            s.input_mode = InputMode::Pointer;
            s.open_store_game_from(&app_id, Some(flight_id.clone()), tx);
        })
        .child(poster)
        .child(
            text(tile.name.clone())
                .font(type_control())
                .color(FG)
                .truncate(),
        )
        .children(tile_meta_row(tile))
        .children(
            (!rev.is_empty()).then(|| text(rev).font(type_small()).color(fg_muted()).truncate()),
        );

    if s.input_mode == InputMode::Gamepad {
        card = card.focus_ring(2.0, 3.0, FG);
    }

    card
}

fn store_game_view(s: &AppState, game: &StoreGame, scale: f32) -> El {
    let hue = title_hue(&game.name);
    let art_url = game.header.as_deref().or_else(|| {
        game.art
            .as_ref()
            .and_then(exo_rpc::types::StoreArt::best_hero)
    });
    let hero_img = art_url.and_then(|u| s.remote_image_sized(u, 1280.0, scale));
    let flight_id = s
        .cover_flight_id
        .clone()
        .unwrap_or_else(|| format!("store:hero:{}", game.app_id));

    let mut banner = el()
        .key(format!("sg-banner-{}", game.app_id))
        .w(440.0)
        .h(210.0)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD)
        .matched(flight_id)
        .motion(SPRING_FLIGHT);
    if let Some(img) = hero_img {
        banner = banner.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        banner = banner.linear_gradient(
            0.35,
            [(0.0, oklch(0.45, 0.15, hue)), (1.0, oklch(0.12, 0.03, hue))],
        );
    }

    let primary_offer = game.offers.first();
    let best_discount = if game.discount > 0 {
        game.discount
    } else {
        primary_offer.map_or(0, |o| o.discount)
    };
    let price_val = if game.free {
        "Free".into()
    } else {
        game.price_text
            .clone()
            .or_else(|| {
                primary_offer
                    .map(exo_rpc::types::StoreOffer::display_price)
                    .filter(|p| !p.is_empty())
            })
            .unwrap_or_else(|| {
                if game.coming_soon {
                    "Coming soon".into()
                } else {
                    "—".into()
                }
            })
    };
    let review_val = match (&game.review_word, game.review_percent) {
        (Some(w), Some(pct)) => format!("{w} · {pct}%"),
        (Some(w), None) => w.clone(),
        _ => "—".into(),
    };

    let open_url = game
        .url
        .clone()
        .or_else(|| {
            primary_offer
                .map(|o| o.url.clone())
                .filter(|u| !u.is_empty())
        })
        .unwrap_or_else(|| format!("https://store.steampowered.com/app/{}/", game.app_id));

    let mut open_btn = primary_button(
        "sg-open",
        "Open in store",
        Some(icon_external(13.0, PRIMARY_FG)),
    )
    .on_press(move |s: &mut AppState| s.open_store_url(&open_url));
    if s.input_mode == InputMode::Gamepad {
        open_btn = open_btn.focus_ring(2.0, 3.0, FG);
    }

    let mut details = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(12.0)
        .child(
            text(game.name.clone())
                .font(type_page())
                .color(FG)
                .w_full()
                .wrap(),
        )
        .child(
            row()
                .gap(8.0)
                .center_y()
                .children((best_discount > 0).then(|| discount_badge(best_discount)))
                .child(open_btn),
        )
        .child(
            row()
                .gap(10.0)
                .flex_wrap()
                .child(stat_chip("Price", price_val))
                .child(stat_chip("Reviews", review_val))
                .children(
                    game.metacritic
                        .map(|m| stat_chip("Metacritic", format!("{m}/100"))),
                )
                .children(
                    game.fit
                        .as_ref()
                        .filter(|f| !f.is_empty())
                        .map(|f| stat_chip("Your PC", f.clone())),
                )
                .children(
                    game.released
                        .as_ref()
                        .map(|r| stat_chip("Released", r.clone())),
                ),
        );

    if let Some(ref desc) = game.description {
        details = details.child(
            text(desc.clone())
                .font(type_body())
                .color(fg_muted())
                .w_full()
                .wrap(),
        );
    }

    let mut back_btn = glass_button("sg-back", "Store", Some(icon_back(14.0, FG))).on_press_tx(
        |s: &mut AppState, tx| {
            s.input_mode = InputMode::Pointer;
            s.close_store_game_tx(tx);
        },
    );
    if s.input_mode == InputMode::Gamepad {
        back_btn = back_btn.focus_ring(2.0, 3.0, FG);
    }

    let mut page = col()
        .key(format!("store-game-page-{}", game.app_id))
        .w_full()
        .pt(64.0)
        .px(24.0)
        .pb(36.0)
        .gap(20.0)
        .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)))
        .child(row().child(back_btn))
        .child(
            row()
                .w_full()
                .gap(24.0)
                .items_start()
                .child(banner)
                .child(details),
        );

    let mut all_offers = game.offers.clone();
    if let Some(ref extra) = s.store_offers {
        for off in &extra.offers {
            if !all_offers.iter().any(|o| o.store == off.store) {
                all_offers.push(off.clone());
            }
        }
    }

    if !all_offers.is_empty() {
        let mut offer_card = amoled_card(RADIUS_PANEL)
            .w_full()
            .px(18.0)
            .py(16.0)
            .gap(10.0)
            .child(
                text("Official stores & pricing")
                    .font(type_title())
                    .color(FG),
            );

        if let Some(low) = s.store_offers.as_ref().and_then(|o| o.lowest.as_ref()) {
            let low_price = format_cents(low.final_cents, Some("USD"));
            let low_label = if low.at.is_empty() {
                format!("Historical low: {low_price}")
            } else {
                format!("Historical low: {low_price} ({})", low.at)
            };
            offer_card = offer_card.child(text(low_label).font(type_small()).color(fg_muted()));
        }

        let gamepad = s.input_mode == InputMode::Gamepad;
        let rows = col()
            .w_full()
            .gap(6.0)
            .children(all_offers.into_iter().map(|off| {
                let url = off.url.clone();
                let store_name = if off.store.is_empty() {
                    "Official store".into()
                } else {
                    off.store.clone()
                };
                let price_s = off.display_price();
                row()
                    .key(format!("offer-{store_name}"))
                    .w_full()
                    .h(42.0)
                    .px(14.0)
                    .justify_between()
                    .center_y()
                    .radius(RADIUS_CARD)
                    .fill(BG)
                    .border(1.0, glass_rim())
                    .child(text(store_name).font(type_section()).color(FG))
                    .child(
                        row()
                            .gap(10.0)
                            .center_y()
                            .children((off.discount > 0).then(|| discount_badge(off.discount)))
                            .child(text(price_s).font(type_control()).color(FG))
                            .children((!url.is_empty()).then(|| {
                                let mut btn = glass_button(
                                    "offer-buy",
                                    "Open",
                                    Some(icon_external(12.0, FG)),
                                )
                                .on_press(move |s: &mut AppState| s.open_store_url(&url));
                                if gamepad {
                                    btn = btn.focus_ring(2.0, 3.0, FG);
                                }
                                btn
                            })),
                    )
            }));
        page = page.child(offer_card.child(rows));
    }

    page
}

fn tab_pill(key: &'static str, label: &'static str, active: bool, gamepad: bool) -> El {
    let mut tab = el()
        .key(key)
        .role("tab")
        .label(label)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(28.0)
        .px(12.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(TRANSPARENT)
        .hover(if active { TRANSPARENT } else { hover_fill() })
        .press(if active { TRANSPARENT } else { pressed_fill() })
        .motion(SPRING_QUICK)
        .child(text(label).font(type_control()).color(if active {
            TRANSPARENT
        } else {
            fg_muted()
        }));
    if active {
        tab = tab.child(
            row()
                .key("store-tab-pill")
                .matched("store-tab-pill")
                .matched_crop()
                .motion(SPRING_MOVE)
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .px(12.0)
                .center()
                .radius(RADIUS_PILL)
                .fill(FG)
                .child(text(label).font(type_control()).color(PRIMARY_FG)),
        );
    }
    if gamepad {
        tab = tab.focus_ring(2.0, 3.0, FG);
    }
    tab
}

pub fn view(s: &AppState, scale: f32) -> El {
    if let Some(ref game) = s.store_game {
        return store_game_view(s, game, scale);
    }

    let gamepad = s.input_mode == InputMode::Gamepad;

    let search_input = text_input(&s.store_query, type_body())
        .key("store-search-input")
        .label("Search the store")
        .grow(1.0)
        .single_line()
        .color(FG)
        .placeholder("Search the store", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.store_query))
        .on_submit(|s: &mut AppState, _| s.search_store());

    let search_bar = row()
        .w(360.0)
        .h(32.0)
        .px(12.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(icon_search(14.0, fg_muted()))
        .child(search_input);

    let tab_group = row()
        .h(32.0)
        .px(2.0)
        .py(2.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(
            tab_pill("store-tab-discover", "Discover", s.store_tab == 0, gamepad).on_press(
                |s: &mut AppState| {
                    s.input_mode = InputMode::Pointer;
                    s.store_tab = 0;
                },
            ),
        )
        .child(
            tab_pill("store-tab-wishlist", "Wishlist", s.store_tab == 1, gamepad).on_press(
                |s: &mut AppState| {
                    s.input_mode = InputMode::Pointer;
                    s.store_tab = 1;
                },
            ),
        );

    let sub_bar = row()
        .w_full()
        .pt(10.0)
        .px(10.0)
        .pb(14.0)
        .gap(12.0)
        .justify_between()
        .center_y()
        .child(tab_group)
        .child(
            row().gap(12.0).center_y().child(search_bar).child(
                glass_icon_circle(
                    "store-refresh",
                    "Refresh store",
                    32.0,
                    icon_refresh(14.0, FG),
                )
                .on_press(|s: &mut AppState| s.load_store_home(true)),
            ),
        );

    let page = col()
        .key("store-browse-page")
        .w_full()
        .pt(52.0)
        .pb(56.0)
        .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)))
        .child(sub_bar);

    if let Some(ref results) = s.store_search_results {
        let body = col()
            .key("store-search-body")
            .w_full()
            .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)))
            .child(
                row()
                    .w_full()
                    .pt(4.0)
                    .px(10.0)
                    .pb(6.0)
                    .justify_between()
                    .center_y()
                    .child(
                        text(format!("Search results ({})", results.len()))
                            .font(type_title())
                            .color(FG),
                    )
                    .child(glass_button("store-clear-search", "Clear", None).on_press(
                        |s: &mut AppState| {
                            s.store_search_results = None;
                            s.store_query.text.clear();
                            s.store_query.sync_invariants();
                        },
                    )),
            )
            .child(
                row()
                    .w_full()
                    .pt(8.0)
                    .px(10.0)
                    .flex_wrap()
                    .gap(16.0)
                    .children(
                        results
                            .iter()
                            .take(24)
                            .map(|t| store_tile_card(t, "search", s, scale)),
                    ),
            );
        return page.child(body);
    }

    let Some(ref home) = s.store_home else {
        let msg = if s.store_loading {
            "Loading Store picks and deals…".to_string()
        } else {
            s.notice
                .clone()
                .unwrap_or_else(|| "Store is offline. Click Refresh to try again.".into())
        };
        return page.child(
            el().key("store-empty-body")
                .w_full()
                .h(220.0)
                .center()
                .child(text(msg).font(type_section()).color(fg_muted())),
        );
    };

    if s.store_tab == 1 {
        let mut seen = std::collections::HashSet::new();
        let wished: Vec<&StoreTile> = home
            .shelves
            .iter()
            .flat_map(|sh| sh.items.iter())
            .filter(|t| t.wished && seen.insert(t.app_id.as_str()))
            .collect();
        let mut body = col()
            .key("store-wishlist-body")
            .w_full()
            .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)));
        if wished.is_empty() {
            body = body.child(
                el().w_full().h(220.0).center().child(
                    text("Games you bookmark in Discover show up here.")
                        .font(type_section())
                        .color(fg_muted()),
                ),
            );
        } else {
            body = body
                .child(
                    row()
                        .w_full()
                        .pt(4.0)
                        .px(10.0)
                        .pb(6.0)
                        .child(text("Wishlist").font(type_title()).color(FG)),
                )
                .child(
                    row()
                        .w_full()
                        .pt(8.0)
                        .px(10.0)
                        .flex_wrap()
                        .gap(16.0)
                        .children(
                            wished
                                .into_iter()
                                .map(|t| store_tile_card(t, "wish", s, scale)),
                        ),
                );
        }
        return page.child(body);
    }

    let mut discover = col()
        .key("store-discover-body")
        .w_full()
        .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)));

    if !home.hero.is_empty() {
        let active_idx = s.store_hero_index % home.hero.len();
        let hero = &home.hero[active_idx];
        let hue = title_hue(&hero.name);
        let hero_img = hero
            .art
            .as_ref()
            .and_then(|a| a.best_hero())
            .and_then(|u| s.remote_image_sized(u, 1600.0, scale));

        let app_id = hero.app_id.clone();
        let hero_flight = format!("store:hero:{}", hero.app_id);
        let mut main_banner = el()
            .key("store-hero-open")
            .role("button")
            .label(hero.name.clone())
            .focusable(true)
            .hit()
            .press_on_down()
            .grow(1.0)
            .min_w(0.0)
            .h(512.0)
            .radius(RADIUS_PANEL)
            .continuous_corners()
            .clip_rounded(RADIUS_PANEL)
            .matched(hero_flight.clone())
            .motion(SPRING_FLIGHT)
            .on_press_tx(move |s: &mut AppState, tx| {
                s.input_mode = InputMode::Pointer;
                s.open_store_game_from(&app_id, Some(hero_flight.clone()), tx);
            });
        if gamepad {
            main_banner = main_banner.focus_ring(2.0, 3.0, FG);
        }

        if let Some(img) = hero_img {
            main_banner = main_banner.pixels_fit(img.pixels(), SurfaceFit::Fill);
        } else {
            main_banner = main_banner.linear_gradient(
                0.35,
                [(0.0, oklch(0.42, 0.16, hue)), (1.0, oklch(0.11, 0.03, hue))],
            );
        }

        let name_plate = row()
            .pin(Some(32.0), None, None, Some(24.0))
            .pt(7.0)
            .pb(8.0)
            .px(14.0)
            .center_y()
            .radius(12.0)
            .fill(BG.alpha(0.56))
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .child(
                text(hero.name.clone())
                    .font(TextStyle::new(scaled(20.0), 650.0))
                    .color(FG),
            );

        main_banner = main_banner.child(name_plate);

        let side_list = col()
            .key("store-hero-side-list")
            .w(300.0)
            .h(512.0)
            .gap(6.4)
            .children(home.hero.iter().take(6).enumerate().map(|(i, item)| {
                let is_on = i == active_idx;
                let item_hue = title_hue(&item.name);
                let thumb_img =
                    best_pick_art(item).and_then(|u| s.remote_image_sized(u, 220.0, scale));
                let mut thumb = el()
                    .key(format!("hero-thumb-{i}"))
                    .size(115.0, 68.0)
                    .radius(RADIUS_SMALL)
                    .continuous_corners()
                    .clip_rounded(RADIUS_SMALL);
                if let Some(img) = thumb_img {
                    thumb = thumb.pixels_fit(img.pixels(), SurfaceFit::Fill);
                } else {
                    thumb = thumb.linear_gradient(
                        0.35,
                        [
                            (0.0, oklch(0.38, 0.14, item_hue)),
                            (1.0, oklch(0.12, 0.03, item_hue)),
                        ],
                    );
                }

                let mut pick = row()
                    .key(format!("hero-dot-{i}"))
                    .role("button")
                    .label(item.name.clone())
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .w_full()
                    .h(80.0)
                    .px(8.0)
                    .py(6.0)
                    .gap(12.0)
                    .center_y()
                    .radius(RADIUS_CARD)
                    .continuous_corners()
                    .fill(if is_on { selected_fill() } else { TRANSPARENT })
                    .hover(if is_on { selected_fill() } else { hover_fill() })
                    .press(pressed_fill())
                    .motion(SPRING_QUICK)
                    .on_press(move |s: &mut AppState| {
                        s.input_mode = InputMode::Pointer;
                        s.store_hero_index = i;
                    })
                    .child(thumb)
                    .child(
                        text(item.name.clone())
                            .font(TextStyle::new(scaled(13.0), 500.0))
                            .color(if is_on { FG } else { fg_muted() })
                            .grow(1.0)
                            .min_w(0.0)
                            .wrap(),
                    );
                if gamepad {
                    pick = pick.focus_ring(2.0, 3.0, FG);
                }
                pick
            }));

        discover = discover.child(
            row()
                .w_full()
                .px(10.0)
                .gap(16.0)
                .items_start()
                .child(main_banner)
                .child(side_list),
        );
    }

    for (idx, shelf) in home.shelves.iter().take(4).enumerate() {
        if shelf.items.is_empty() {
            continue;
        }
        let visible_count = shelf.items.len().min(24);
        let (can_prev, can_next) = s.store_shelf_can_page(idx, visible_count);
        let prev_key = match idx {
            0 => "shelf-prev-0",
            1 => "shelf-prev-1",
            2 => "shelf-prev-2",
            _ => "shelf-prev-3",
        };
        let next_key = match idx {
            0 => "shelf-next-0",
            1 => "shelf-next-1",
            2 => "shelf-next-2",
            _ => "shelf-next-3",
        };

        let mut prev_btn = glass_icon_circle(
            prev_key,
            "Previous",
            32.0,
            icon_chevron_left(14.0, if can_prev { FG } else { fg_muted() }),
        )
        .opacity(if can_prev { 1.0 } else { 0.35 });
        if can_prev {
            prev_btn = prev_btn.on_press_tx(move |s: &mut AppState, tx| {
                s.input_mode = InputMode::Pointer;
                s.page_store_shelf(idx, -1.0, visible_count, tx);
            });
        }
        if gamepad {
            prev_btn = prev_btn.focus_ring(2.0, 3.0, FG);
        }

        let mut next_btn = glass_icon_circle(
            next_key,
            "Next",
            32.0,
            icon_chevron_right(14.0, if can_next { FG } else { fg_muted() }),
        )
        .opacity(if can_next { 1.0 } else { 0.35 });
        if can_next {
            next_btn = next_btn.on_press_tx(move |s: &mut AppState, tx| {
                s.input_mode = InputMode::Pointer;
                s.page_store_shelf(idx, 1.0, visible_count, tx);
            });
        }
        if gamepad {
            next_btn = next_btn.focus_ring(2.0, 3.0, FG);
        }

        let header = row()
            .w_full()
            .pt(4.0)
            .px(10.0)
            .pb(6.0)
            .justify_between()
            .center_y()
            .child(text(shelf.title.clone()).font(type_title()).color(FG))
            .child(row().gap(4.0).center_y().child(prev_btn).child(next_btn));

        let scroll_slot = s.store_shelf_scrolls[idx.min(3)].clone();
        let scope = format!("shelf-{idx}");
        let mut seen = std::collections::HashSet::new();
        let track = row()
            .key(format!("store-shelf-track-{idx}"))
            .w_full()
            .pt(8.0)
            .px(10.0)
            .pb(4.0)
            .gap(16.0)
            .scroll_x()
            .clip_fade_linear(
                if can_prev { 44.0 } else { 0.0 },
                0.0,
                if can_next { 44.0 } else { 0.0 },
                0.0,
            )
            .on_scroll_offset(move |pos| scroll_slot.set(pos))
            .children(
                shelf
                    .items
                    .iter()
                    .filter(|t| seen.insert(t.app_id.as_str()))
                    .take(24)
                    .map(|t| store_tile_card(t, &scope, s, scale)),
            );

        discover = discover.child(col().w_full().mt(24.0).child(header).child(track));
    }

    page.child(discover)
}

#[cfg(test)]
mod tests {
    use crate::shell;
    use crate::state::{AppState, Room};
    use rune::motion_gate::Run;
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Runtime};

    #[test]
    fn store_hero_alignment_and_blur_cap() {
        let mut state = AppState::with_demo_games();
        state.switch_room(Room::Store);
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(blurs <= 4, "Store exceeded MAX_BACKDROP_BLURS=4: {blurs}");

        let (hero_rect, _) = rt
            .presented("store-hero-open")
            .expect("store-hero-open presented");
        let (side_rect, _) = rt
            .presented("store-hero-side-list")
            .expect("store-hero-side-list presented");
        assert!(
            (hero_rect[3] - 512.0).abs() <= 1.0,
            "expected 512px hero height, got {}",
            hero_rect[3]
        );
        assert!(
            (side_rect[1] - hero_rect[1]).abs() <= 1.0
                && (side_rect[3] - hero_rect[3]).abs() <= 1.0,
            "expected side-picker flush with hero banner: hero={hero_rect:?} side={side_rect:?}"
        );
    }

    #[test]
    fn store_tabs_hero_flight_and_shelf_paging_motion_gate() {
        for reduce_motion in [false, true] {
            let mut state = AppState::with_demo_games();
            state.settings.reduce_motion = reduce_motion;
            state.switch_room(Room::Store);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();

            run.act("switch to Wishlist tab", |rt| {
                Run::tap(rt, "store-tab-wishlist")
            });
            run.play(0.4);
            run.act("switch back to Discover tab", |rt| {
                Run::tap(rt, "store-tab-discover")
            });
            run.play(0.4);

            run.act("page Store shelf forward", |rt| {
                Run::tap(rt, "shelf-next-0")
            });
            run.play(0.4);
            run.act("page Store shelf back", |rt| Run::tap(rt, "shelf-prev-0"));
            run.play(0.4);

            run.act("open Store game from hero", |rt| {
                Run::tap(rt, "store-hero-open")
            });
            run.play(0.12);
            run.act("mid-flight reverse back to Store", |rt| {
                Run::tap(rt, "sg-back")
            });
            run.play(0.5);

            run.act("open Store game settled", |rt| {
                Run::tap(rt, "store-hero-open")
            });
            run.play(0.5);
            run.act("return to Store", |rt| Run::tap(rt, "sg-back"));
            run.play(0.5);

            let findings: Vec<_> = run
                .finish()
                .into_iter()
                .filter(|f| {
                    if f.kind == "pop-out"
                        && (f.element.contains("store-tab-pill")
                            || f.element.contains("store-hero-open")
                            || f.element.contains("sg-banner-"))
                    {
                        return false;
                    }
                    if f.kind == "jerk"
                        && (f.element.contains("store-browse-page")
                            || f.element.contains("store-hero-open")
                            || f.element.contains("sg-banner-"))
                    {
                        return false;
                    }
                    if reduce_motion
                        && matches!(f.kind, "jump" | "text-swap" | "pop-in" | "pop-out")
                    {
                        return false;
                    }
                    true
                })
                .collect();
            assert!(
                findings.is_empty(),
                "store motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }
}
