use crate::state::{AppState, title_hue};
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    SPRING_MOVE, discount_badge, fg_muted, glass_button, glass_control_fill, glass_rim,
    icon_back, icon_external, icon_refresh, icon_search, primary_button, stat_chip, subtle_fill,
    type_body, type_control, type_page, type_section, type_small, type_title,
};
use exo_rpc::{StoreGame, StoreTile, types::format_cents};
use rune::{El, SurfaceFit, col, edit_access, el, oklch, row, text, text_input};

fn store_tile_card(tile: &StoreTile, s: &AppState, scale: f32) -> El {
    let hue = title_hue(&tile.name);
    let art_url = tile.art.as_ref().and_then(|a| a.best_poster());
    let img = art_url.and_then(|u| s.remote_image(u, scale));

    let mut art = el().w(188.0).h(256.0).radius(RADIUS_CARD);
    if let Some(loaded) = img {
        art = art.pixels_fit(loaded.pixels(), SurfaceFit::Fill);
    } else {
        art = art.linear_gradient(
            0.35,
            [
                (0.0, oklch(0.46, 0.16, hue)),
                (1.0, oklch(0.11, 0.03, hue)),
            ],
        );
    }

    let price = tile.display_price();
    let scrim = el()
        .w_full()
        .h_full()
        .radius(RADIUS_CARD)
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, BG.alpha(0.0)),
                (0.55, BG.alpha(0.08)),
                (0.82, BG.alpha(0.72)),
                (1.0, BG.alpha(0.92)),
            ],
        )
        .child(
            col()
                .w_full()
                .h_full()
                .px(11.0)
                .py(11.0)
                .justify_between()
                .child(
                    row().justify_end().children(
                        (tile.discount > 0).then(|| discount_badge(tile.discount)),
                    ),
                )
                .child(
                    col()
                        .gap(3.0)
                        .child(text(tile.name.clone()).font(type_section()).color(FG).truncate())
                        .child(text(price).font(type_small()).color(fg_muted()).truncate()),
                ),
        );

    let app_id = tile.app_id.clone();
    el().key(format!("store-tile-{}", tile.app_id))
        .role("button")
        .label(tile.name.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .radius(RADIUS_CARD)
        .motion(SPRING_MOVE)
        .on_press(move |s: &mut AppState| s.open_store_game(&app_id))
        .child(art.child(scrim))
}

fn store_game_view(s: &AppState, game: &StoreGame, scale: f32) -> El {
    let hue = title_hue(&game.name);
    let art_url = game
        .header
        .as_deref()
        .or_else(|| game.art.as_ref().and_then(exo_rpc::types::StoreArt::best_hero));
    let hero_img = art_url.and_then(|u| s.remote_image(u, scale));

    let mut banner = el().w(420.0).h(196.0).radius(RADIUS_CARD);
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
            .or_else(|| primary_offer.map(exo_rpc::types::StoreOffer::display_price).filter(|p| !p.is_empty()))
            .unwrap_or_else(|| if game.coming_soon { "Coming soon".into() } else { "—".into() })
    };
    let review_val = match (&game.review_word, game.review_percent) {
        (Some(w), Some(pct)) => format!("{w} ({pct}%)"),
        (Some(w), None) => w.clone(),
        _ => "—".into(),
    };

    let open_url = game
        .url
        .clone()
        .or_else(|| primary_offer.map(|o| o.url.clone()).filter(|u| !u.is_empty()))
        .unwrap_or_else(|| format!("https://store.steampowered.com/app/{}/", game.app_id));

    let mut details = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(12.0)
        .child(text(game.name.clone()).font(type_page()).color(FG).w_full().wrap())
        .child(
            row()
                .gap(8.0)
                .center_y()
                .children((best_discount > 0).then(|| discount_badge(best_discount)))
                .child(
                    primary_button("sg-open", "Open in store", Some(icon_external(13.0, PRIMARY_FG)))
                        .on_press(move |s: &mut AppState| s.open_store_url(&open_url)),
                ),
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

    if !game.genres.is_empty() {
        details = details.child(row().gap(6.0).flex_wrap().children(
            game.genres.iter().take(6).map(|g| {
                el().h(24.0)
                    .px(10.0)
                    .radius(RADIUS_PILL)
                    .center()
                    .fill(subtle_fill())
                    .child(text(g.clone()).font(type_small()).color(fg_muted()))
            }),
        ));
    }

    let mut page = col()
        .w_full()
        .pt(72.0)
        .px(28.0)
        .pb(36.0)
        .gap(20.0)
        .child(
            row().child(
                glass_button("sg-back", "Store", Some(icon_back(14.0, FG)))
                    .on_press(|s: &mut AppState| s.close_store_game()),
            ),
        )
        .child(row().w_full().gap(24.0).items_start().child(banner).child(details));

    if let Some(ref offers) = s.store_offers
        && !offers.offers.is_empty()
    {
        let mut offer_col = col()
            .w_full()
            .gap(10.0)
            .child(text("Official store prices").font(type_title()).color(FG));
        if let Some(ref low) = offers.lowest {
            offer_col = offer_col.child(
                text(format!(
                    "Historical low: {} ({})",
                    format_cents(low.final_cents, Some("USD")),
                    low.at
                ))
                .font(type_small())
                .color(fg_muted()),
            );
        }
        let rows = col().w(560.0).gap(6.0).children(offers.offers.iter().map(|o| {
            let url = o.url.clone();
            row()
                .key(format!("offer-{}-{}", o.store, o.url))
                .role("button")
                .label(o.name.clone())
                .focusable(true)
                .hit()
                .press_on_down()
                .w_full()
                .h(44.0)
                .px(14.0)
                .radius(RADIUS_CARD)
                .justify_between()
                .center_y()
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .on_press(move |s: &mut AppState| s.open_store_url(&url))
                .child(text(o.name.clone()).font(type_control()).color(FG))
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .children((o.discount > 0).then(|| discount_badge(o.discount)))
                        .child(text(o.display_price()).font(type_section()).color(FG)),
                )
        }));
        page = page.child(offer_col.child(rows));
    }

    page
}

pub fn view(s: &AppState, scale: f32) -> El {
    if let Some(ref game) = s.store_game {
        return store_game_view(s, game, scale);
    }

    let search_input = text_input(&s.store_query, type_body())
        .key("store-search-input")
        .label("Search Store")
        .grow(1.0)
        .single_line()
        .color(FG)
        .placeholder("Search Steam & official deals…", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.store_query))
        .on_submit(|s: &mut AppState, _| s.search_store());

    let search_bar = row()
        .w(340.0)
        .h(36.0)
        .px(12.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(subtle_fill())
        .border(1.0, glass_rim())
        .child(icon_search(14.0, fg_muted()))
        .child(search_input);

    let mut page = col()
        .w_full()
        .pt(72.0)
        .px(24.0)
        .pb(36.0)
        .gap(20.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Store").font(type_page()).color(FG))
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(search_bar)
                        .child(
                            glass_button("store-refresh", "Refresh", Some(icon_refresh(14.0, FG)))
                                .on_press(|s: &mut AppState| s.load_store_home(true)),
                        ),
                ),
        );

    if let Some(ref results) = s.store_search_results {
        page = page
            .child(
                row()
                    .justify_between()
                    .center_y()
                    .w_full()
                    .child(
                        text(format!("Search results ({})", results.len()))
                            .font(type_title())
                            .color(FG),
                    )
                    .child(
                        glass_button("store-clear-search", "Clear", None).on_press(
                            |s: &mut AppState| {
                                s.store_search_results = None;
                                s.store_query.text.clear();
                                s.store_query.sync_invariants();
                            },
                        ),
                    ),
            )
            .child(
                row()
                    .w_full()
                    .flex_wrap()
                    .gap(12.0)
                    .children(results.iter().take(24).map(|t| store_tile_card(t, s, scale))),
            );
        return page;
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
            el().w_full()
                .h(220.0)
                .center()
                .child(text(msg).font(type_section()).color(fg_muted())),
        );
    };

    if let Some(hero) = home.hero.get(s.store_hero_index % home.hero.len().max(1)) {
        let hue = title_hue(&hero.name);
        let hero_img = hero
            .art
            .as_ref()
            .and_then(|a| a.best_hero())
            .and_then(|u| s.remote_image(u, scale));
        let mut hero_card = el().w_full().h(260.0).radius(RADIUS_PANEL);
        if let Some(img) = hero_img {
            hero_card = hero_card.pixels_fit(img.pixels(), SurfaceFit::Fill);
        } else {
            hero_card = hero_card.linear_gradient(
                0.35,
                [(0.0, oklch(0.42, 0.16, hue)), (1.0, oklch(0.11, 0.03, hue))],
            );
        }

        let app_id = hero.app_id.clone();
        let hero_count = home.hero.len();
        let hero_overlay = el()
            .w_full()
            .h_full()
            .radius(RADIUS_PANEL)
            .linear_gradient(
                std::f32::consts::PI,
                [(0.0, BG.alpha(0.08)), (0.6, BG.alpha(0.55)), (1.0, BG.alpha(0.92))],
            )
            .child(
                col()
                    .w_full()
                    .h_full()
                    .px(24.0)
                    .py(22.0)
                    .justify_end()
                    .gap(10.0)
                    .child(text(hero.name.clone()).font(type_page()).color(FG).w_full().wrap())
                    .child(
                        row()
                            .w_full()
                            .justify_between()
                            .center_y()
                            .child(
                                row()
                                    .gap(8.0)
                                    .center_y()
                                    .children((hero.discount > 0).then(|| discount_badge(hero.discount)))
                                    .child(text(hero.display_price()).font(type_section()).color(FG))
                                    .child(
                                        primary_button("store-hero-open", "View game", None)
                                            .on_press(move |s: &mut AppState| s.open_store_game(&app_id)),
                                    ),
                            )
                            .children((hero_count > 1).then(|| {
                                row().gap(6.0).children((0..hero_count.min(6)).map(|i| {
                                    let active = i == s.store_hero_index % hero_count;
                                    el().key(format!("hero-dot-{i}"))
                                        .role("button")
                                        .label(format!("Featured {}", i + 1))
                                        .focusable(true)
                                        .hit()
                                        .size(if active { 18.0 } else { 8.0 }, 8.0)
                                        .radius(4.0)
                                        .fill(if active { FG } else { FG.alpha(0.35) })
                                        .on_press(move |s: &mut AppState| s.store_hero_index = i)
                                }))
                            })),
                    ),
            );
        page = page.child(hero_card.child(hero_overlay));
    }

    for shelf in home.shelves.iter().take(4) {
        if shelf.items.is_empty() {
            continue;
        }
        page = page
            .child(text(shelf.title.clone()).font(type_title()).color(FG))
            .child(
                row()
                    .w_full()
                    .flex_wrap()
                    .gap(12.0)
                    .children(shelf.items.iter().take(6).map(|t| store_tile_card(t, s, scale))),
            );
    }

    page
}
