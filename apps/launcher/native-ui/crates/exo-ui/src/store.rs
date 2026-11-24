use crate::state::{AppState, InputMode, title_hue};
use crate::store_page::{HERO_ROTATE_SECS, find_owned_game};
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_FLIGHT, SPRING_MOVE, SPRING_QUICK, TRANSPARENT, bad, fg_faint, fg_muted, glass_button,
    glass_control_fill, glass_icon_circle, glass_rim_bottom, glass_rim_top, good, hover_fill,
    icon_arrow_right, icon_bookmark_filled, icon_check, icon_chevron_left, icon_chevron_right,
    icon_close, icon_refresh, icon_search, pressed_fill, scaled, selected_fill, type_body,
    type_control, type_section, type_small, type_title,
};
use exo_rpc::{StoreArt, StoreTile, types::format_cents};
use rune::{
    Cursor, El, ScrollPos, SpringPair, SurfaceFit, TextStyle, Transition, col, edit_access, el,
    oklch, row, text, text_input,
};
use std::cell::Cell;
use std::rc::Rc;

fn review_line(tile: &StoreTile) -> String {
    match (&tile.review, tile.review_percent) {
        (Some(w), Some(p)) if !w.is_empty() => format!("{w} · {p}%"),
        (Some(w), None) if !w.is_empty() => w.clone(),
        _ => String::new(),
    }
}

pub fn hero_when(when: Option<&str>) -> String {
    let Some(w) = when.filter(|s| !s.is_empty()) else {
        return "Coming soon".into();
    };
    let looks_dated = is_short_month_date(w)
        || (w.len() == 4 && w.chars().all(|c| c.is_ascii_digit()))
        || (w.len() == 7
            && w.starts_with('Q')
            && matches!(w.as_bytes()[1], b'1'..=b'4')
            && w.as_bytes()[2] == b' '
            && w[3..].chars().all(|c| c.is_ascii_digit()));
    if looks_dated {
        format!("Coming {w}")
    } else {
        w.to_string()
    }
}

fn is_short_month_date(w: &str) -> bool {
    const MONTHS: [&str; 12] = [
        "Jan ", "Feb ", "Mar ", "Apr ", "May ", "Jun ", "Jul ", "Aug ", "Sep ", "Oct ", "Nov ",
        "Dec ",
    ];
    let Some(rest) = MONTHS.iter().find_map(|m| w.strip_prefix(m)) else {
        return false;
    };
    if rest.len() == 4 && rest.chars().all(|c| c.is_ascii_digit()) {
        return true;
    }
    let mut parts = rest.split(", ");
    let Some(day) = parts.next() else {
        return false;
    };
    if !(1..=2).contains(&day.len()) || !day.chars().all(|c| c.is_ascii_digit()) {
        return false;
    }
    match parts.next() {
        None => true,
        Some(year) => {
            year.len() == 4 && year.chars().all(|c| c.is_ascii_digit()) && parts.next().is_none()
        }
    }
}

fn best_poster_image(s: &AppState, tile: &StoreTile, scale: f32) -> Option<rune::Image> {
    let mut candidates: Vec<String> = Vec::with_capacity(4);
    if let Some(ref art) = tile.art
        && let Some(ref u) = art.poster
    {
        candidates.push(u.clone());
    }
    candidates.push(StoreArt::steam_url(&tile.app_id, "poster"));
    if let Some(ref art) = tile.art
        && let Some(ref u) = art.header
    {
        candidates.push(u.clone());
    }
    candidates.push(StoreArt::steam_url(&tile.app_id, "header"));
    for u in candidates {
        if let Some(img) = s.remote_image_sized(&u, 360.0, scale) {
            return Some(img);
        }
    }
    None
}

fn best_hero_image(s: &AppState, tile: &StoreTile, scale: f32) -> Option<rune::Image> {
    let mut candidates: Vec<String> = Vec::with_capacity(5);
    if let Some(ref art) = tile.art {
        if let Some(ref u) = art.hero {
            candidates.push(u.clone());
        }
        if let Some(ref u) = art.header {
            candidates.push(u.clone());
        }
        if let Some(ref u) = art.capsule {
            candidates.push(u.clone());
        }
    }
    candidates.push(StoreArt::steam_url(&tile.app_id, "hero"));
    candidates.push(StoreArt::steam_url(&tile.app_id, "header"));
    for u in candidates {
        if let Some(img) = s.remote_image_sized(&u, 1440.0, scale) {
            return Some(img);
        }
    }
    None
}

fn best_pick_image(s: &AppState, tile: &StoreTile, scale: f32) -> Option<rune::Image> {
    let mut candidates: Vec<String> = Vec::with_capacity(4);
    if let Some(ref art) = tile.art {
        if let Some(ref u) = art.capsule {
            candidates.push(u.clone());
        }
        if let Some(ref u) = art.header {
            candidates.push(u.clone());
        }
    }
    candidates.push(StoreArt::steam_url(&tile.app_id, "capsule"));
    candidates.push(StoreArt::steam_url(&tile.app_id, "header"));
    for u in candidates {
        if let Some(img) = s.remote_image_sized(&u, 220.0, scale) {
            return Some(img);
        }
    }
    None
}

#[allow(clippy::too_many_arguments)]
pub fn render_price(
    free: bool,
    final_cents: Option<i64>,
    initial: Option<i64>,
    discount: i32,
    currency: Option<&str>,
    price_text: Option<&str>,
    was_text: Option<&str>,
    on_primary: bool,
) -> Option<El> {
    let now_fg = if on_primary { PRIMARY_FG } else { FG };
    let was_fg = if on_primary {
        PRIMARY_FG.alpha(0.72)
    } else {
        fg_faint()
    };
    if free {
        return Some(
            row()
                .gap(8.0)
                .center_y()
                .child(text("Free").font(type_body()).color(now_fg)),
        );
    }
    let clean_price = price_text.filter(|s| !s.is_empty());
    if clean_price.is_none()
        && (final_cents.is_none() || (final_cents == Some(0) && initial.is_none()))
    {
        return None;
    }
    let now_str = clean_price
        .map(ToString::to_string)
        .or_else(|| final_cents.map(|c| format_cents(c, currency)))
        .unwrap_or_default();

    let mut r = row().gap(8.0).center_y();
    if discount > 0 {
        r = r.child(
            el().px(5.0)
                .py(1.0)
                .radius(RADIUS_PILL)
                .fill(if on_primary {
                    good()
                } else {
                    good().alpha(0.18)
                })
                .child(
                    text(format!("-{discount}%"))
                        .font(type_small())
                        .color(if on_primary { PRIMARY_FG } else { good() }),
                ),
        );
    }
    if !now_str.is_empty() {
        r = r.child(text(now_str).font(type_body()).color(now_fg));
    }
    if discount > 0 {
        let was = was_text
            .filter(|s| !s.is_empty())
            .map(ToString::to_string)
            .or_else(|| {
                initial
                    .filter(|&v| v > 0)
                    .map(|v| format_cents(v, currency))
            });
        if let Some(w) = was {
            r = r.child(text(w).font(type_small()).color(was_fg));
        }
    }
    Some(r)
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
                .child(text(label).font(type_small()).color(fg_muted()).truncate()),
        );
    }
    if let Some(price_el) = render_price(
        tile.free,
        tile.final_cents.or(tile.final_price),
        tile.initial,
        tile.discount,
        tile.currency.as_deref(),
        tile.price_text.as_deref(),
        tile.was_text.as_deref(),
        false,
    ) {
        return Some(row().w_full().h(scaled(18.0)).center_y().child(price_el));
    }
    if tile.discount > 0 {
        return Some(
            row().w_full().h(scaled(18.0)).center_y().child(
                el().px(5.0)
                    .py(1.0)
                    .radius(RADIUS_PILL)
                    .fill(good().alpha(0.18))
                    .child(
                        text(format!("-{}%", tile.discount))
                            .font(type_small())
                            .color(good()),
                    ),
            ),
        );
    }
    None
}

pub fn store_tile_card(
    tile: &StoreTile,
    scope: &str,
    from_shelf: Option<&str>,
    s: &AppState,
    scale: f32,
) -> El {
    let hue = title_hue(&tile.name);
    let img = best_poster_image(s, tile, scale);
    let card_key = format!("store-tile-{scope}-{}", tile.app_id);
    let flight_id = format!("store:{scope}:{}", tile.app_id);
    let hovered = s.hovered_cover_key.as_deref() == Some(card_key.as_str());
    let hover_key = card_key.clone();
    let wished = s.store_is_wished(&tile.app_id, tile.wished);
    let owned = find_owned_game(&s.games, &tile.app_id, &tile.name);
    let gamepad = s.input_mode == InputMode::Gamepad;

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

    if wished {
        poster = poster.child(
            el().pin(None, Some(8.0), Some(8.0), None)
                .size(28.0, 28.0)
                .radius(RADIUS_PILL)
                .center()
                .fill(BG.alpha(0.64))
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(icon_bookmark_filled(14.0, FG)),
        );
    }

    let rev = review_line(tile);
    let tile_owned = tile.clone();
    let shelf_owned = from_shelf.map(ToString::to_string);

    let mut card = col()
        .key(card_key)
        .role("button")
        .label(tile.name.clone())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
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
            s.store_open_tile_from(
                tile_owned.clone(),
                shelf_owned.clone(),
                Some(flight_id.clone()),
                tx,
            );
        })
        .child(poster)
        .child(
            text(tile.name.clone())
                .font(type_control())
                .color(FG)
                .truncate(),
        );

    if owned.is_none() {
        card = card.children(tile_meta_row(tile)).children(
            (!rev.is_empty()).then(|| text(rev).font(type_small()).color(fg_muted()).truncate()),
        );
    }
    if gamepad {
        card = card.focus_ring(2.0, 3.0, FG);
    }

    if let Some(lib_game) = owned {
        let gid = lib_game.id.clone();
        let mut lib_btn = row()
            .key(format!("store-tile-lib-{scope}-{}", tile.app_id))
            .role("button")
            .label(format!("Open {} in Library", tile.name))
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .mt(6.0)
            .h(28.0)
            .px(12.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_control_fill())
            .hover(hover_fill())
            .press(pressed_fill())
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .motion(SPRING_QUICK)
            .on_press_tx(move |s: &mut AppState, tx| {
                s.input_mode = InputMode::Pointer;
                s.store_open_owned_in_library(&gid, tx);
            })
            .child(text("Library").font(type_control()).color(FG))
            .child(icon_arrow_right(14.0, FG));
        if gamepad {
            lib_btn = lib_btn.focus_ring(2.0, 3.0, FG);
        }
        col().w(168.0).items_start().child(card).child(lib_btn)
    } else {
        card
    }
}

#[allow(clippy::too_many_arguments)]
pub fn store_shelf_row(
    s: &AppState,
    id: &str,
    title: &str,
    items: &[StoreTile],
    from_shelf: Option<&str>,
    can_prev: bool,
    can_next: bool,
    scroll_slot: Rc<Cell<ScrollPos>>,
    scale: f32,
    shelf_idx: Option<usize>,
) -> El {
    let gamepad = s.input_mode == InputMode::Gamepad;
    let visible_count = items.len();
    let prev_key = match shelf_idx {
        Some(0) => "shelf-prev-0",
        Some(1) => "shelf-prev-1",
        Some(2) => "shelf-prev-2",
        Some(3) => "shelf-prev-3",
        Some(_) => "shelf-prev-extra",
        None => "shelf-prev-more",
    };
    let next_key = match shelf_idx {
        Some(0) => "shelf-next-0",
        Some(1) => "shelf-next-1",
        Some(2) => "shelf-next-2",
        Some(3) => "shelf-next-3",
        Some(_) => "shelf-next-extra",
        None => "shelf-next-more",
    };

    let mut header = row()
        .w_full()
        .pt(4.0)
        .px(10.0)
        .pb(6.0)
        .justify_between()
        .center_y()
        .child(text(title.to_string()).font(type_title()).color(FG));

    if can_prev || can_next {
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
                if let Some(i) = shelf_idx {
                    s.store_page_any_shelf(i, -1.0, visible_count, tx);
                } else {
                    s.store_page_more(-1.0, visible_count, tx);
                }
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
                if let Some(i) = shelf_idx {
                    s.store_page_any_shelf(i, 1.0, visible_count, tx);
                } else {
                    s.store_page_more(1.0, visible_count, tx);
                }
            });
        }
        if gamepad {
            next_btn = next_btn.focus_ring(2.0, 3.0, FG);
        }

        header = header.child(row().gap(4.0).center_y().child(prev_btn).child(next_btn));
    }

    let scope = format!("shelf-{id}");
    let track_key = match shelf_idx {
        Some(i) => format!("store-shelf-track-{i}"),
        None => "store-shelf-track-more".into(),
    };
    let mut seen = std::collections::HashSet::new();
    let track = row()
        .key(track_key)
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
            items
                .iter()
                .filter(|t| seen.insert(t.app_id.as_str()))
                .map(|t| store_tile_card(t, &scope, from_shelf, s, scale)),
        );

    col().w_full().mt(24.0).child(header).child(track)
}

fn tab_pill(key: &'static str, label: &'static str, active: bool, gamepad: bool) -> El {
    let mut tab = el()
        .key(key)
        .role("tab")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
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

fn unreachable_view(message: &str, retry_key: &'static str, on_retry: fn(&mut AppState)) -> El {
    col()
        .key("store-unreachable")
        .w_full()
        .h(260.0)
        .center()
        .gap(10.0)
        .child(
            text("Store unavailable")
                .font(TextStyle::new(scaled(15.0), 650.0))
                .color(FG),
        )
        .child(
            text("Exo could not reach the store.")
                .font(type_body())
                .color(fg_muted()),
        )
        .child(text(message.to_string()).font(type_small()).color(bad()))
        .child(glass_button(retry_key, "Try again", None).on_press(on_retry))
}

fn retry_home(s: &mut AppState) {
    s.load_store_home(false);
}

fn retry_wishlist(s: &mut AppState) {
    s.load_store_wishlist();
}

fn retry_search(s: &mut AppState) {
    let q = s.store_query.text.trim().to_string();
    s.store_run_search_now(q);
}

fn wishlist_view(s: &AppState, scale: f32) -> El {
    let mut body = col()
        .key("store-wishlist-body")
        .w_full()
        .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)));

    if !s.store_ext.wishlist_loaded {
        if let Some(ref err) = s.store_ext.wishlist_error {
            return body.child(unreachable_view(
                err,
                "store-wishlist-retry",
                retry_wishlist,
            ));
        }
        return body.child(
            el().w_full().h(220.0).center().child(
                text("Loading your wishlist…")
                    .font(type_section())
                    .color(fg_muted()),
            ),
        );
    }

    let items = &s.store_ext.wishlist;
    if items.is_empty() {
        return body.child(
            el().w_full().h(260.0).center().child(
                text("Your wishlist is empty")
                    .font(type_control())
                    .color(FG),
            ),
        );
    }

    let mut sale: Vec<&StoreTile> = Vec::new();
    let mut rest: Vec<&StoreTile> = Vec::new();
    for item in items {
        if item.discount > 0 && find_owned_game(&s.games, &item.app_id, &item.name).is_none() {
            sale.push(item);
        } else {
            rest.push(item);
        }
    }

    let checking = s.store_ext.wishlist_checking;
    let checked = !checking && s.store_ext.wishlist_checked_until > s.clock_now();
    let gamepad = s.input_mode == InputMode::Gamepad;

    let mut check_btn = row()
        .key("store-check-prices")
        .role("button")
        .label("Check prices")
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(14.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .opacity(if checking { 0.65 } else { 1.0 })
        .motion(SPRING_QUICK)
        .child(if checked {
            icon_check(14.0, good())
        } else {
            text(if checking {
                "Checking…"
            } else {
                "Check prices"
            })
            .font(type_control())
            .color(FG)
        });
    if !checking {
        check_btn = check_btn.on_press(|s: &mut AppState| s.store_check_wishlist_prices());
    }
    if gamepad {
        check_btn = check_btn.focus_ring(2.0, 3.0, FG);
    }

    let mut head = row()
        .w_full()
        .pt(4.0)
        .px(10.0)
        .pb(6.0)
        .justify_between()
        .center_y();
    if !sale.is_empty() {
        head = head.child(text("On sale now").font(type_title()).color(FG));
    } else {
        head = head.child(el().w(1.0).h(1.0));
    }
    head = head.child(check_btn);

    let first_items: Vec<&StoreTile> = if sale.is_empty() {
        items.iter().collect()
    } else {
        sale.clone()
    };
    body = body.child(head).child(
        row()
            .w_full()
            .pt(8.0)
            .px(10.0)
            .flex_wrap()
            .gap(16.0)
            .children(
                first_items
                    .into_iter()
                    .map(|t| store_tile_card(t, "wish-sale", None, s, scale)),
            ),
    );

    if !sale.is_empty() && !rest.is_empty() {
        body = body
            .child(
                row()
                    .w_full()
                    .mt(32.0)
                    .pt(4.0)
                    .px(10.0)
                    .pb(6.0)
                    .child(text("Not on sale").font(type_title()).color(FG)),
            )
            .child(
                row()
                    .w_full()
                    .pt(8.0)
                    .px(10.0)
                    .flex_wrap()
                    .gap(16.0)
                    .children(
                        rest.into_iter()
                            .map(|t| store_tile_card(t, "wish-rest", None, s, scale)),
                    ),
            );
    }
    body
}

fn search_view(s: &AppState, scale: f32) -> El {
    let body = col()
        .key("store-search-body")
        .w_full()
        .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)));

    let Some(ref results) = s.store_search_results else {
        if let Some(ref err) = s.store_ext.search_error {
            return body.child(unreachable_view(err, "store-search-retry", retry_search));
        }
        return body.child(
            el().w_full().h(220.0).center().child(
                text("Searching the store…")
                    .font(type_section())
                    .color(fg_muted()),
            ),
        );
    };

    if results.is_empty() {
        let shown_q = if s.store_ext.search_query_sent.is_empty() {
            s.store_query.text.trim().to_string()
        } else {
            s.store_ext.search_query_sent.clone()
        };
        return body.child(
            col()
                .w_full()
                .py(96.0)
                .center()
                .gap(16.0)
                .child(
                    text(format!("Nothing on the store matches “{shown_q}”"))
                        .font(type_control())
                        .color(FG),
                )
                .child(
                    glass_button("store-clear-search", "Clear search", None)
                        .on_press(|s: &mut AppState| s.store_clear_search()),
                ),
        );
    }

    body.child(
        row()
            .w_full()
            .pt(8.0)
            .px(10.0)
            .flex_wrap()
            .gap(16.0)
            .children(
                results
                    .iter()
                    .map(|t| store_tile_card(t, "search", None, s, scale)),
            ),
    )
}

fn hero_section(s: &AppState, hero_items: &[StoreTile], scale: f32) -> El {
    let gamepad = s.input_mode == InputMode::Gamepad;
    let count = hero_items.len().min(6);
    let active_idx = s.store_hero_index % count;
    let hero = &hero_items[active_idx];
    let hue = title_hue(&hero.name);
    let hero_img = best_hero_image(s, hero, scale);
    let hero_wished = s.store_is_wished(&hero.app_id, hero.wished);
    let logo_url = StoreArt::steam_url(&hero.app_id, "logo");
    let logo_img = s.remote_image_sized(&logo_url, 640.0, scale);

    let hero_tile = hero.clone();
    let hero_flight = format!("store:hero:{}", hero.app_id);
    let mut main_banner = el()
        .key("store-hero-open")
        .role("button")
        .label(format!("Open {}", hero.name))
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
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
            s.store_open_tile_from(hero_tile.clone(), None, Some(hero_flight.clone()), tx);
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

    let mut copy_col = col()
        .pin(Some(32.0), None, None, Some(24.0))
        .gap(16.0)
        .items_start();

    if hero.soon || hero_wished {
        let mut tags = row().gap(6.0).center_y();
        if hero.soon {
            tags = tags.child(
                el().px(12.0)
                    .py(4.0)
                    .radius(RADIUS_PILL)
                    .fill(glass_control_fill())
                    .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                    .child(
                        text(hero_when(hero.when.as_deref()))
                            .font(type_small())
                            .color(FG),
                    ),
            );
        }
        if hero_wished {
            tags = tags.child(
                row()
                    .px(12.0)
                    .py(4.0)
                    .gap(6.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(glass_control_fill())
                    .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                    .child(icon_bookmark_filled(12.0, FG))
                    .child(text("On your wishlist").font(type_small()).color(FG)),
            );
        }
        copy_col = copy_col.child(tags);
    }

    if let Some(logo) = logo_img {
        copy_col = copy_col.child(
            el().key(format!("store-hero-logo-{}", hero.app_id))
                .w(420.0)
                .h(120.0)
                .pixels_fit(logo.pixels(), SurfaceFit::Fit),
        );
    } else {
        copy_col = copy_col.child(
            row()
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
                ),
        );
    }

    main_banner = main_banner.child(copy_col);

    let mut hero_row = row()
        .key("store-hero-section")
        .w_full()
        .px(10.0)
        .gap(16.0)
        .items_start()
        .on_hover_change(|s: &mut AppState, inside, _tx| {
            s.store_ext.hero_hovered = inside;
        })
        .child(main_banner);

    if count > 1 {
        let turns = !s.settings.reduce_motion;
        let progress = (s.store_ext.hero_elapsed / HERO_ROTATE_SECS).clamp(0.0, 1.0) as f32;
        let side_list = col()
            .key("store-hero-side-list")
            .w(300.0)
            .h(512.0)
            .gap(6.4)
            .children(hero_items.iter().take(6).enumerate().map(|(i, item)| {
                let is_on = i == active_idx;
                let item_hue = title_hue(&item.name);
                let thumb_img = best_pick_image(s, item, scale);
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

                let item_tile = item.clone();
                let pick_flight = format!("store:hero:{}", item.app_id);
                let mut pick = row()
                    .key(format!("hero-dot-{i}"))
                    .role("button")
                    .label(item.name.clone())
                    .focusable(true)
                    .hit()
                    .cursor(Cursor::Pointer)
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
                    .clip_rounded(RADIUS_CARD)
                    .fill(if is_on { selected_fill() } else { TRANSPARENT })
                    .hover(if is_on { selected_fill() } else { hover_fill() })
                    .press(pressed_fill())
                    .motion(SPRING_QUICK)
                    .on_press_tx(move |s: &mut AppState, tx| {
                        s.input_mode = InputMode::Pointer;
                        if is_on {
                            s.store_open_tile_from(
                                item_tile.clone(),
                                None,
                                Some(pick_flight.clone()),
                                tx,
                            );
                        } else {
                            s.store_select_hero(i);
                        }
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
                if is_on && turns {
                    pick = pick.child(
                        el().pin(Some(8.0), None, Some(8.0), Some(0.0))
                            .h(2.0)
                            .radius(RADIUS_PILL)
                            .fill(TRANSPARENT)
                            .child(
                                el().h(2.0)
                                    .w_frac(progress)
                                    .radius(RADIUS_PILL)
                                    .fill(FG.alpha(0.45)),
                            ),
                    );
                }
                if gamepad {
                    pick = pick.focus_ring(2.0, 3.0, FG);
                }
                pick
            }));
        hero_row = hero_row.child(side_list);
    }

    hero_row
}

pub fn view(s: &AppState, scale: f32) -> El {
    if s.store_has_page_open() {
        return crate::store_page::view(s, scale);
    }

    let gamepad = s.input_mode == InputMode::Gamepad;
    let searching = s.store_query.text.trim().len() >= 2 || s.store_search_results.is_some();
    let active_home = !searching && s.store_tab == 0;
    let active_wish = !searching && s.store_tab == 1;

    let search_input = text_input(&s.store_query, type_body())
        .key("store-search-input")
        .label("Search the store")
        .grow(1.0)
        .single_line()
        .color(FG)
        .placeholder("Search the store", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.store_query))
        .on_submit(|s: &mut AppState, _| {
            let q = s.store_query.text.trim().to_string();
            s.store_run_search_now(q);
        });

    let mut search_bar = row()
        .w(260.0)
        .h(32.0)
        .px(12.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(icon_search(14.0, fg_muted()))
        .child(search_input);

    if !s.store_query.text.is_empty() {
        search_bar = search_bar.child(
            el().key("store-search-clear-icon")
                .role("button")
                .label("Clear search")
                .focusable(true)
                .hit()
                .cursor(Cursor::Pointer)
                .size(22.0, 22.0)
                .radius(RADIUS_PILL)
                .center()
                .hover(hover_fill())
                .on_press(|s: &mut AppState| s.store_clear_search())
                .child(icon_close(12.0, fg_faint())),
        );
    }

    let tab_group = row()
        .h(32.0)
        .px(2.0)
        .py(2.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(
            tab_pill("store-tab-discover", "Discover", active_home, gamepad).on_press(
                |s: &mut AppState| {
                    s.input_mode = InputMode::Pointer;
                    s.store_tab = 0;
                    s.store_clear_search();
                },
            ),
        )
        .child(
            tab_pill("store-tab-wishlist", "Wishlist", active_wish, gamepad).on_press(
                |s: &mut AppState| {
                    s.input_mode = InputMode::Pointer;
                    s.store_tab = 1;
                    s.store_clear_search();
                    if !s.store_ext.wishlist_loaded {
                        s.load_store_wishlist();
                    }
                },
            ),
        );

    let status_err = if searching {
        s.store_search_results
            .as_ref()
            .and(s.store_ext.search_error.as_deref())
    } else if s.store_tab == 1 {
        s.store_ext
            .wishlist_loaded
            .then_some(s.store_ext.wishlist_error.as_deref())
            .flatten()
    } else {
        s.store_home.as_ref().and(s.store_ext.home_error.as_deref())
    };

    let mut right_tools = row().gap(12.0).center_y();
    if let Some(msg) = status_err {
        right_tools = right_tools.child(
            text(msg.to_string())
                .font(type_small())
                .color(bad())
                .truncate(),
        );
    }
    right_tools = right_tools.child(search_bar);
    if active_home {
        let mut refresh_btn = glass_icon_circle(
            "store-refresh",
            if s.store_ext.home_refreshing {
                "Refreshing the store"
            } else {
                "Refresh the store"
            },
            32.0,
            icon_refresh(14.0, FG),
        )
        .opacity(if s.store_ext.home_refreshing {
            0.55
        } else {
            1.0
        });
        if !s.store_ext.home_refreshing {
            refresh_btn = refresh_btn.on_press(|s: &mut AppState| s.load_store_home(true));
        }
        right_tools = right_tools.child(refresh_btn);
    } else {
        right_tools = right_tools.child(el().size(32.0, 32.0));
    }

    let sub_bar = row()
        .w_full()
        .pt(10.0)
        .px(10.0)
        .pb(14.0)
        .gap(12.0)
        .justify_between()
        .center_y()
        .child(tab_group)
        .child(right_tools);

    let page = col()
        .key("store-browse-page")
        .w_full()
        .pt(52.0)
        .pb(56.0)
        .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)))
        .child(sub_bar);

    if searching {
        return page.child(search_view(s, scale));
    }

    if s.store_tab == 1 {
        return page.child(wishlist_view(s, scale));
    }

    let Some(ref home) = s.store_home else {
        if let Some(ref err) = s.store_ext.home_error {
            return page.child(unreachable_view(err, "store-home-retry", retry_home));
        }
        return page.child(
            el().key("store-empty-body")
                .w_full()
                .h(220.0)
                .center()
                .child(
                    text("Loading the store…")
                        .font(type_section())
                        .color(fg_muted()),
                ),
        );
    };

    let mut discover = col()
        .key("store-discover-body")
        .w_full()
        .transition(Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK)));

    if !home.hero.is_empty() {
        discover = discover.child(hero_section(s, &home.hero, scale));
    }

    for (idx, shelf) in home.shelves.iter().enumerate() {
        if shelf.items.is_empty() {
            continue;
        }
        let (can_prev, can_next) = s.store_any_shelf_can_page(idx, shelf.items.len());
        let slot = s.store_shelf_slot(idx);
        discover = discover.child(store_shelf_row(
            s,
            &shelf.id,
            &shelf.title,
            &shelf.items,
            Some(&shelf.id),
            can_prev,
            can_next,
            slot,
            scale,
            Some(idx),
        ));
    }

    page.child(discover)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::shell;
    use crate::state::{AppState, Room};
    use rune::motion_gate::Run;
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Runtime};

    #[test]
    fn hero_when_formats_dates_and_preserves_custom_copy() {
        assert_eq!(hero_when(None), "Coming soon");
        assert_eq!(hero_when(Some("Oct 23")), "Coming Oct 23");
        assert_eq!(hero_when(Some("Feb 2027")), "Coming Feb 2027");
        assert_eq!(hero_when(Some("Q1 2027")), "Coming Q1 2027");
        assert_eq!(hero_when(Some("2027")), "Coming 2027");
        assert_eq!(hero_when(Some("To be announced")), "To be announced");
    }

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

    #[test]
    fn store_game_page_renders_all_sections_and_lightbox() {
        let mut state = AppState::with_demo_games();
        state.switch_room(Room::Store);
        state.open_store_game("1091500");
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        assert!(rt.presented("sg-back").is_some());
        assert!(rt.presented("sg-wishlist").is_some());
        assert!(rt.presented("sg-ggdeals").is_some());
        assert!(rt.presented("sg-about").is_some());
        assert!(rt.presented("sg-facts").is_some());
        assert!(rt.presented("sg-specs").is_some());
        assert!(rt.presented("sg-media-strip").is_some());

        rt.state.store_open_lightbox(0);
        rt.settle_all();
        rt.frame(10.0);
        assert!(rt.presented("store-lightbox-close").is_some());
        assert!(rt.presented("store-lightbox-counter").is_some());
    }

    #[test]
    fn store_wishlist_splits_sale_and_owned_cards_show_library_button() {
        let mut state = AppState::with_demo_games();
        state.games.push(exo_rpc::Game {
            id: "steam:1145350".into(),
            title: "Hades II".into(),
            store: "steam".into(),
            owned: true,
            ..Default::default()
        });
        state.store_ext.wishlist = vec![
            StoreTile {
                app_id: "1145350".into(),
                name: "Hades II".into(),
                discount: 20,
                price_text: Some("$23.99".into()),
                wished: true,
                ..Default::default()
            },
            StoreTile {
                app_id: "999001".into(),
                name: "Silksong".into(),
                discount: 25,
                price_text: Some("$29.99".into()),
                wished: true,
                ..Default::default()
            },
            StoreTile {
                app_id: "999002".into(),
                name: "Hollow Knight: Voidheart".into(),
                discount: 0,
                price_text: Some("$14.99".into()),
                wished: true,
                ..Default::default()
            },
        ];
        state.store_ext.wishlist_loaded = true;
        state.switch_room(Room::Store);
        state.store_tab = 1;

        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        assert!(rt.presented("store-check-prices").is_some());
        assert!(rt.presented("store-tile-wish-sale-999001").is_some());
        assert!(rt.presented("store-tile-wish-rest-1145350").is_some());
        assert!(rt.presented("store-tile-wish-rest-999002").is_some());
        assert!(rt.presented("store-tile-lib-wish-rest-1145350").is_some());
    }
}
