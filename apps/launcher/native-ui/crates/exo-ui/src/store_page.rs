use crate::state::{AppState, HostStatus, InputMode, NoticeTone, Room};
use crate::store::{render_price, store_shelf_row};
use crate::store_media::{ShownMedia, media_viewer, media_wait};
use exo_kit::{
    ExoGlass, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PILL, SPRING_QUICK, TRANSPARENT,
    bad, fg_faint, fg_muted, float_line, glass_button, glass_control_fill, glass_rim_bottom,
    glass_rim_top, good, hover_fill, icon_arrow_right, icon_bookmark, icon_check,
    icon_chevron_left, icon_close, icon_external, pressed_fill, scaled, type_body, type_control,
    type_small,
};
#[cfg(windows)]
use exo_rpc::{
    ActionResult, METHOD_SHELL_OPEN_URL, METHOD_STORE_GAME, METHOD_STORE_GAME_OFFERS,
    METHOD_STORE_GAME_REVIEWS, METHOD_STORE_OPEN, METHOD_STORE_SEARCH, METHOD_STORE_WISHLIST,
    StoreSearchReply, StoreWishlistReply,
};
use exo_rpc::{
    Game, StoreArt, StoreGame, StoreMedia, StoreOffer, StoreOtherOffers, StoreRecentReviews,
    StoreSaleEvent, StoreSpec, StoreTile, types::format_cents,
};
use rune::{
    Cursor, El, ScrollPos, SpringPair, SurfaceFit, TextStyle, Transition, Tx, Waker, col, el, row,
    text,
};
#[cfg(windows)]
use serde_json::json;
use std::cell::Cell;
use std::collections::{HashMap, HashSet, VecDeque};
use std::rc::Rc;

pub const HERO_ROTATE_SECS: f64 = 8.0;
const SEARCH_DEBOUNCE_SECS: f64 = 0.25;
const DONE_FLASH_SECS: f64 = 1.6;
const GAME_KEEP_SECS: f64 = 600.0;
const KEEP_MAX_ENTRIES: usize = 96;
const WARMED_MAX_ENTRIES: usize = 1024;
const WARM_DWELL_SECS: f64 = 0.5;
const HOVER_WARM_SECS: f64 = 0.06;
const WARM_CONCURRENCY: usize = 3;
const VISIBLE_WARM_COUNT: usize = 10;

#[derive(Clone)]
pub struct CachedStoreGame {
    pub at: f64,
    pub game: StoreGame,
}
const SPEC_PARTS: [&str; 7] = [
    "OS",
    "Processor",
    "Graphics",
    "Memory",
    "Storage",
    "DirectX",
    "Network",
];

pub struct StoreExtraState {
    pub waker: Option<Waker>,
    pub seed: Option<StoreTile>,
    pub from_shelf: Option<String>,
    pub game_error: Option<String>,
    pub game_loading: bool,
    pub recent_reviews: Option<StoreRecentReviews>,
    pub reviews_loading: bool,
    pub shown_media: Option<ShownMedia>,
    pub trailer_player: Option<rune_media::Player>,
    pub trailer_mf: Option<Box<crate::native_media::NativeMediaSession>>,
    pub trailer_url: Option<String>,
    pub trailer_playing: bool,
    pub trailer_muted: bool,
    pub lightbox_index: Option<usize>,
    pub wishlist: Vec<StoreTile>,
    pub wishlist_loaded: bool,
    pub wishlist_error: Option<String>,
    pub wishlist_checking: bool,
    pub wishlist_checked_until: f64,
    pub wishlist_flash_until: f64,
    pub wishlist_seq: u64,
    pub home_error: Option<String>,
    pub home_refreshing: bool,
    pub search_query_sent: String,
    pub search_observed_query: String,
    pub search_pending_query: String,
    pub search_due_at: Option<f64>,
    pub search_error: Option<String>,
    pub search_pending: bool,
    pub hero_hovered: bool,
    pub hero_timer_start: f64,
    pub hero_elapsed: f64,
    pub offer_busy: HashSet<String>,
    pub offer_errors: HashMap<String, String>,
    pub gg_opening: bool,
    pub more_scroll: Rc<Cell<ScrollPos>>,
    pub more_scroll_x: f32,
    pub extra_shelf_scrolls: Vec<Rc<Cell<ScrollPos>>>,
    pub extra_shelf_x: Vec<f32>,
    pub game_cache: HashMap<String, CachedStoreGame>,
    pub game_cache_order: VecDeque<String>,
    pub warmed_ids: HashSet<String>,
    pub warmed_order: VecDeque<String>,
    pub warm_queue: VecDeque<String>,
    pub warm_inflight: HashSet<String>,
    pub room_visible_since: Option<f64>,
    pub hover_warm: Option<(String, f64)>,
    pub native_engine: crate::store_engine::NativeStoreEngineBridge,
}

impl Default for StoreExtraState {
    fn default() -> Self {
        Self {
            waker: None,
            seed: None,
            from_shelf: None,
            game_error: None,
            game_loading: false,
            recent_reviews: None,
            reviews_loading: false,
            shown_media: None,
            trailer_player: None,
            trailer_mf: None,
            trailer_url: None,
            trailer_playing: false,
            trailer_muted: true,
            lightbox_index: None,
            wishlist: Vec::new(),
            wishlist_loaded: false,
            wishlist_error: None,
            wishlist_checking: false,
            wishlist_checked_until: 0.0,
            wishlist_flash_until: 0.0,
            wishlist_seq: 0,
            home_error: None,
            home_refreshing: false,
            search_query_sent: String::new(),
            search_observed_query: String::new(),
            search_pending_query: String::new(),
            search_due_at: None,
            search_error: None,
            search_pending: false,
            hero_hovered: false,
            hero_timer_start: 0.0,
            hero_elapsed: 0.0,
            offer_busy: HashSet::new(),
            offer_errors: HashMap::new(),
            gg_opening: false,
            more_scroll: Rc::new(Cell::new(ScrollPos::default())),
            more_scroll_x: 0.0,
            extra_shelf_scrolls: Vec::new(),
            extra_shelf_x: Vec::new(),
            game_cache: HashMap::new(),
            game_cache_order: VecDeque::new(),
            warmed_ids: HashSet::new(),
            warmed_order: VecDeque::new(),
            warm_queue: VecDeque::new(),
            warm_inflight: HashSet::new(),
            room_visible_since: None,
            hover_warm: None,
            native_engine: crate::store_engine::NativeStoreEngineBridge::default(),
        }
    }
}

pub fn title_identity(value: &str) -> String {
    value
        .chars()
        .filter(|c| c.is_alphanumeric())
        .flat_map(char::to_lowercase)
        .collect()
}

pub fn find_owned_game<'a>(games: &'a [Game], app_id: &str, title: &str) -> Option<&'a Game> {
    let want_title = title_identity(title);
    let mut title_match: Option<&'a Game> = None;
    for g in games {
        if g.is_add_portable {
            continue;
        }
        if g.includes
            .iter()
            .any(|inc| inc.app_id.as_deref() == Some(app_id))
        {
            return Some(g);
        }
        if g.variants.is_empty() {
            if !g.owned && !g.installed {
                continue;
            }
            if let Some(sid) = g.id.strip_prefix("steam:") {
                if sid == app_id {
                    return Some(g);
                }
            } else if !want_title.is_empty() && title_identity(&g.title) == want_title {
                title_match = Some(g);
            }
        } else {
            for v in &g.variants {
                if !v.owned && !v.installed && !g.owned && !g.installed {
                    continue;
                }
                if let Some(sid) = v.id.strip_prefix("steam:") {
                    if sid == app_id {
                        return Some(g);
                    }
                } else if !want_title.is_empty() && title_identity(&g.title) == want_title {
                    title_match = Some(g);
                }
            }
        }
    }
    title_match
}

pub fn gg_deals_url(title: &str) -> String {
    let clean: String = title
        .chars()
        .filter(|&c| !matches!(c, '™' | '®' | '©'))
        .collect();
    let mut slug = String::new();
    let mut prev_dash = true;
    for ch in clean.chars() {
        if matches!(ch, '\'' | '’') {
            continue;
        }
        if ch.is_ascii_alphanumeric() {
            slug.push(ch.to_ascii_lowercase());
            prev_dash = false;
        } else if !prev_dash {
            slug.push('-');
            prev_dash = true;
        }
    }
    while slug.ends_with('-') {
        slug.pop();
    }
    if slug.is_empty() {
        let mut encoded = String::new();
        for b in clean.trim().bytes() {
            if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
                encoded.push(b as char);
            } else {
                encoded.push_str(&format!("%{b:02X}"));
            }
        }
        format!("https://gg.deals/search/?title={encoded}")
    } else {
        format!("https://gg.deals/game/{slug}/")
    }
}

pub fn format_commas(n: u64) -> String {
    let s = n.to_string();
    let bytes = s.as_bytes();
    let mut out = String::with_capacity(s.len() + s.len() / 3);
    for (i, &b) in bytes.iter().enumerate() {
        if i > 0 && (bytes.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(b as char);
    }
    out
}

pub fn score_text(word: Option<&str>, percent: Option<i32>, count: u64) -> String {
    let Some(w) = word.filter(|s| !s.is_empty()) else {
        return String::new();
    };
    let base = match percent {
        Some(p) => format!("{w} · {p}%"),
        None => w.to_string(),
    };
    if count > 0 {
        format!("{base} of {}", format_commas(count))
    } else {
        base
    }
}

pub fn controller_text(game: &StoreGame) -> String {
    let support = match game.controller.as_deref() {
        Some("full") => Some("Full"),
        Some("partial") => Some("Partial"),
        _ => None,
    };
    match support {
        None => {
            if game.play_station {
                "PlayStation".into()
            } else {
                String::new()
            }
        }
        Some(sup) => {
            if game.play_station {
                format!("{sup} · Xbox and PlayStation")
            } else {
                format!("{sup} · Xbox")
            }
        }
    }
}

pub fn languages_text(languages: &[String]) -> String {
    if languages.is_empty() {
        return String::new();
    }
    if languages.len() <= 3 {
        languages.join(", ")
    } else {
        format!("{} languages", languages.len())
    }
}

pub fn format_sale_notice(ev: &StoreSaleEvent) -> String {
    let price = ev
        .price
        .as_deref()
        .filter(|s| !s.is_empty())
        .map(ToString::to_string)
        .or_else(|| {
            ev.final_cents
                .filter(|_| ev.currency.as_deref().unwrap_or("USD") == "USD")
                .map(|c| format_cents(c, Some("USD")))
        })
        .unwrap_or_else(|| "a lower price".into());
    format!(
        "{} is on sale on Steam: {price} (-{}%).",
        ev.name, ev.discount
    )
}

pub fn seed_offer(seed: &StoreTile) -> StoreOffer {
    StoreOffer {
        store: "1".into(),
        name: "Steam".into(),
        final_cents: seed.final_cents.or(seed.final_price),
        initial: seed.initial,
        discount: seed.discount,
        currency: seed.currency.clone(),
        price_text: seed.price_text.clone(),
        was_text: seed.was_text.clone(),
        url: format!("https://store.steampowered.com/app/{}", seed.app_id),
    }
}

pub fn more_like_this_items(s: &AppState, active_app_id: &str) -> Vec<StoreTile> {
    let Some(ref home) = s.store_home else {
        return Vec::new();
    };
    if home.shelves.is_empty() {
        return Vec::new();
    }
    let from = s.store_ext.from_shelf.as_deref();
    let mut ordered = Vec::with_capacity(home.shelves.len());
    if let Some(fid) = from
        && let Some(primary) = home.shelves.iter().find(|sh| sh.id == fid)
    {
        ordered.push(primary);
    }
    for sh in &home.shelves {
        if from.is_some_and(|fid| sh.id == fid) {
            continue;
        }
        ordered.push(sh);
    }
    let mut seen = HashSet::new();
    seen.insert(active_app_id.to_string());
    let mut out = Vec::with_capacity(12);
    for sh in ordered {
        for item in &sh.items {
            if !seen.insert(item.app_id.clone()) {
                continue;
            }
            out.push(item.clone());
            if out.len() >= 12 {
                return out;
            }
        }
    }
    out
}

pub fn demo_store_game(
    app_id: &str,
    tile: Option<StoreTile>,
) -> (StoreGame, StoreOtherOffers, StoreRecentReviews) {
    let name = tile
        .as_ref()
        .map(|t| t.name.clone())
        .unwrap_or_else(|| "Cyberpunk 2077".into());
    let price_text = tile.as_ref().map(StoreTile::display_price);
    let discount = tile.as_ref().map_or(40, |t| t.discount);
    let game = StoreGame {
        ok: true,
        app_id: app_id.into(),
        name: name.clone(),
        description: Some(
            "An open-world, action-adventure RPG set in Night City, a megalopolis obsessed with power, glamour and body modification."
                .into(),
        ),
        header: Some(StoreArt::steam_url(app_id, "header")),
        released: Some("Dec 10, 2020".into()),
        developers: vec!["CD PROJEKT RED".into()],
        publishers: vec!["CD PROJEKT RED".into()],
        genres: vec!["RPG".into(), "Action".into()],
        metacritic: Some(86),
        review_word: Some("Very Positive".into()),
        review_percent: Some(85),
        review_count: 712_450,
        price_text: price_text.clone(),
        was_text: Some("$59.99".into()),
        discount,
        tags: vec![
            "Cyberpunk".into(),
            "Open World".into(),
            "RPG".into(),
            "Sci-fi".into(),
            "Singleplayer".into(),
            "Story Rich".into(),
        ],
        controller: Some("full".into()),
        play_station: true,
        languages: vec![
            "English".into(),
            "French".into(),
            "German".into(),
            "Spanish".into(),
            "Japanese".into(),
        ],
        offers: vec![StoreOffer {
            store: "1".into(),
            name: "Steam".into(),
            final_cents: Some(3599),
            initial: Some(5999),
            discount,
            currency: Some("USD".into()),
            price_text,
            was_text: Some("$59.99".into()),
            url: format!("https://store.steampowered.com/app/{app_id}"),
        }],
        media: vec![StoreMedia {
            thumb: StoreArt::steam_url(app_id, "header"),
            hls: Some(format!(
                "https://video.fastly.steamstatic.com/store_trailers/{app_id}/movie.m3u8"
            )),
            av1: None,
        }],
        screenshots: vec![
            StoreArt::steam_url(app_id, "hero"),
            StoreArt::steam_url(app_id, "header"),
            StoreArt::steam_url(app_id, "capsule"),
        ],
        specs: vec![
            StoreSpec {
                label: "OS".into(),
                minimum: Some("Windows 10 64-bit".into()),
                recommended: Some("Windows 10 64-bit".into()),
                mine: Some("Windows 11".into()),
                meets: Some("recommended".into()),
            },
            StoreSpec {
                label: "Processor".into(),
                minimum: Some("Core i7-6700 or Ryzen 5 1600".into()),
                recommended: Some("Core i7-12700 or Ryzen 7 7800X3D".into()),
                mine: Some("Ryzen 7 9800X3D".into()),
                meets: Some("recommended".into()),
            },
            StoreSpec {
                label: "Graphics".into(),
                minimum: Some("RTX 2060 or RX 5700 XT".into()),
                recommended: Some("RTX 3080 or RX 6800 XT".into()),
                mine: Some("RTX 4080".into()),
                meets: Some("recommended".into()),
            },
            StoreSpec {
                label: "Memory".into(),
                minimum: Some("12 GB RAM".into()),
                recommended: Some("16 GB RAM".into()),
                mine: Some("32 GB".into()),
                meets: Some("recommended".into()),
            },
            StoreSpec {
                label: "Storage".into(),
                minimum: Some("70 GB SSD".into()),
                recommended: Some("70 GB NVMe".into()),
                mine: Some("420 GB free".into()),
                meets: Some("recommended".into()),
            },
            StoreSpec {
                label: "DirectX".into(),
                minimum: Some("Version 12".into()),
                recommended: Some("Version 12".into()),
                mine: Some("DirectX 12".into()),
                meets: Some("recommended".into()),
            },
            StoreSpec {
                label: "Network".into(),
                minimum: None,
                recommended: None,
                mine: None,
                meets: None,
            },
        ],
        art: tile.and_then(|t| t.art),
        fit: Some("recommended".into()),
        ..Default::default()
    };
    let offers = StoreOtherOffers {
        ok: true,
        offers: vec![
            StoreOffer {
                store: "25".into(),
                name: "Epic".into(),
                final_cents: Some(3599),
                initial: Some(5999),
                discount: 40,
                currency: Some("USD".into()),
                price_text: Some("$35.99".into()),
                was_text: None,
                url: "https://store.epicgames.com/".into(),
            },
            StoreOffer {
                store: "7".into(),
                name: "GOG".into(),
                final_cents: Some(3299),
                initial: Some(5999),
                discount: 45,
                currency: Some("USD".into()),
                price_text: Some("$32.99".into()),
                was_text: None,
                url: "https://www.gog.com/".into(),
            },
        ],
        lowest: Some(exo_rpc::StoreLowest {
            final_cents: 2999,
            at: "2026-11-19T20:55:23.0000000+00:00".into(),
        }),
    };
    let reviews = StoreRecentReviews {
        ok: true,
        word: Some("Overwhelmingly Positive".into()),
        percent: Some(95),
        count: 8_420,
    };
    (game, offers, reviews)
}

impl AppState {
    pub fn store_is_wished(&self, app_id: &str, tile_wished: bool) -> bool {
        if self.store_ext.wishlist_loaded {
            self.store_ext.wishlist.iter().any(|w| w.app_id == app_id)
        } else {
            tile_wished
        }
    }

    pub fn store_has_page_open(&self) -> bool {
        self.store_game.is_some() || self.store_ext.seed.is_some()
    }

    pub fn store_shelf_slot(&self, idx: usize) -> Rc<Cell<ScrollPos>> {
        if idx < self.store_shelf_scrolls.len() {
            self.store_shelf_scrolls[idx].clone()
        } else if let Some(slot) = self.store_ext.extra_shelf_scrolls.get(idx - 4) {
            slot.clone()
        } else {
            Rc::new(Cell::new(ScrollPos::default()))
        }
    }

    pub fn store_ensure_shelf_slots(&mut self, count: usize) {
        let extra_needed = count.saturating_sub(4);
        while self.store_ext.extra_shelf_scrolls.len() < extra_needed {
            self.store_ext
                .extra_shelf_scrolls
                .push(Rc::new(Cell::new(ScrollPos::default())));
            self.store_ext.extra_shelf_x.push(0.0);
        }
    }

    pub fn store_any_shelf_can_page(&self, idx: usize, count: usize) -> (bool, bool) {
        if idx < 4 {
            return self.store_shelf_can_page(idx, count);
        }
        let pos = self
            .store_ext
            .extra_shelf_scrolls
            .get(idx - 4)
            .map(|c| c.get())
            .unwrap_or_default();
        let current = if pos.max[0] > 0.0 {
            pos.offset[0]
        } else {
            self.store_ext
                .extra_shelf_x
                .get(idx - 4)
                .copied()
                .unwrap_or(0.0)
        };
        let can_prev = current > 2.0;
        let can_next = if pos.max[0] > 0.0 {
            current + 2.0 < pos.max[0]
        } else {
            let approx_track = (count as f32 * 194.0 - 14.0).max(0.0);
            let approx_view = (1920.0_f32 - 104.0).max(360.0);
            approx_track > approx_view + 2.0 && current + 2.0 < approx_track - approx_view
        };
        (can_prev, can_next)
    }

    pub fn store_page_any_shelf(&mut self, idx: usize, dir: f32, count: usize, tx: &mut Tx) {
        if idx < 4 {
            self.page_store_shelf(idx, dir, count, tx);
            return;
        }
        self.store_ensure_shelf_slots(idx + 1);
        let slot_idx = idx - 4;
        let pos = self.store_ext.extra_shelf_scrolls[slot_idx].get();
        let viewport = if pos.viewport[0] > 1.0 {
            pos.viewport[0]
        } else {
            (1920.0_f32 - 104.0).max(360.0)
        };
        let max_x = if pos.max[0] > 0.0 {
            pos.max[0]
        } else {
            ((count as f32 * 194.0 - 14.0) - viewport).max(0.0)
        };
        let cur = if pos.max[0] > 0.0 {
            pos.offset[0]
        } else {
            self.store_ext.extra_shelf_x[slot_idx]
        };
        let step = (viewport * 0.85).max(320.0);
        let next = (cur + dir * step).clamp(0.0, max_x);
        self.store_ext.extra_shelf_x[slot_idx] = next;
        tx.scroll_to(format!("store-shelf-track-{idx}"), next, 0.0);
    }

    pub fn store_more_can_page(&self, count: usize) -> (bool, bool) {
        let pos = self.store_ext.more_scroll.get();
        let current = if pos.max[0] > 0.0 {
            pos.offset[0]
        } else {
            self.store_ext.more_scroll_x
        };
        let can_prev = current > 2.0;
        let can_next = if pos.max[0] > 0.0 {
            current + 2.0 < pos.max[0]
        } else {
            let approx_track = (count as f32 * 194.0 - 14.0).max(0.0);
            let approx_view = (1920.0_f32 - 104.0).max(360.0);
            approx_track > approx_view + 2.0 && current + 2.0 < approx_track - approx_view
        };
        (can_prev, can_next)
    }

    pub fn store_page_more(&mut self, dir: f32, count: usize, tx: &mut Tx) {
        let pos = self.store_ext.more_scroll.get();
        let viewport = if pos.viewport[0] > 1.0 {
            pos.viewport[0]
        } else {
            (1920.0_f32 - 104.0).max(360.0)
        };
        let max_x = if pos.max[0] > 0.0 {
            pos.max[0]
        } else {
            ((count as f32 * 194.0 - 14.0) - viewport).max(0.0)
        };
        let cur = if pos.max[0] > 0.0 {
            pos.offset[0]
        } else {
            self.store_ext.more_scroll_x
        };
        let step = (viewport * 0.85).max(320.0);
        let next = (cur + dir * step).clamp(0.0, max_x);
        self.store_ext.more_scroll_x = next;
        tx.scroll_to("store-shelf-track-more", next, 0.0);
    }

    pub fn load_store_wishlist(&mut self) {
        if self.status == HostStatus::Standalone {
            if !self.store_ext.wishlist_loaded {
                let items: Vec<StoreTile> = self
                    .store_home
                    .as_ref()
                    .map(|h| {
                        h.shelves
                            .iter()
                            .flat_map(|sh| sh.items.iter())
                            .filter(|t| t.wished)
                            .cloned()
                            .collect()
                    })
                    .unwrap_or_default();
                self.store_ext.wishlist = items;
                self.store_ext.wishlist_loaded = true;
                self.store_ext.wishlist_error = None;
            }
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StoreWishlistReply>(
                METHOD_STORE_WISHLIST,
                &json!({ "action": "get" }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(reply) if reply.message.is_none() => {
                            s.store_ext.wishlist = reply.items;
                            s.store_ext.wishlist_loaded = true;
                            s.store_ext.wishlist_error = None;
                        }
                        Ok(reply) => {
                            s.store_ext.wishlist_error = reply.message;
                        }
                        Err(_) => {
                            s.store_ext.wishlist_error =
                                Some("Your wishlist could not be read. Try again.".into());
                        }
                    });
                },
            );
        }
    }

    pub fn store_toggle_wishlist(&mut self, tile: StoreTile, on: bool) {
        self.store_ext.wishlist_seq = self.store_ext.wishlist_seq.wrapping_add(1);
        let mine = self.store_ext.wishlist_seq;
        let before = self.store_ext.wishlist.clone();
        if on {
            self.store_ext.wishlist_flash_until = self.clock_now() + DONE_FLASH_SECS;
            let mut next = Vec::with_capacity(before.len() + 1);
            let mut added = tile.clone();
            added.wished = true;
            next.push(added);
            for w in &before {
                if w.app_id != tile.app_id {
                    next.push(w.clone());
                }
            }
            self.store_ext.wishlist = next;
        } else {
            self.store_ext.wishlist.retain(|w| w.app_id != tile.app_id);
        }
        self.store_ext.wishlist_loaded = true;
        self.store_ext.wishlist_error = None;

        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let app_id = tile.app_id.clone();
            let name = tile.name.clone();
            let action = if on { "add" } else { "remove" };
            rpc.call::<_, StoreWishlistReply>(
                METHOD_STORE_WISHLIST,
                &json!({ "action": action, "appId": app_id, "name": name }),
                move |res| {
                    handle.update(move |s, _| {
                        let undo = |s: &mut AppState, msg: String| {
                            let was = before.iter().position(|w| w.app_id == tile.app_id);
                            s.store_ext.wishlist.retain(|w| w.app_id != tile.app_id);
                            if let Some(pos) = was {
                                let at = pos.min(s.store_ext.wishlist.len());
                                s.store_ext.wishlist.insert(at, before[pos].clone());
                            }
                            s.store_ext.wishlist_error = Some(msg);
                        };
                        match res {
                            Ok(reply) => {
                                if let Some(msg) = reply.message {
                                    undo(s, msg);
                                } else if mine == s.store_ext.wishlist_seq {
                                    s.store_ext.wishlist = reply.items;
                                    s.store_ext.wishlist_loaded = true;
                                    s.store_ext.wishlist_error = None;
                                }
                            }
                            Err(_) => {
                                undo(s, "The wishlist could not be changed. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn store_check_wishlist_prices(&mut self) {
        if self.store_ext.wishlist_checking {
            return;
        }
        if self.status == HostStatus::Standalone {
            self.store_ext.wishlist_checked_until = self.clock_now() + DONE_FLASH_SECS;
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.store_ext.wishlist_checking = true;
            rpc.call::<_, StoreWishlistReply>(
                METHOD_STORE_WISHLIST,
                &json!({ "action": "check" }),
                move |res| {
                    handle.update(move |s, _| {
                        s.store_ext.wishlist_checking = false;
                        match res {
                            Ok(reply) => {
                                if let Some(msg) = reply.message {
                                    s.store_ext.wishlist_error = Some(msg);
                                } else {
                                    s.store_ext.wishlist = reply.items;
                                    s.store_ext.wishlist_loaded = true;
                                    if reply.ok {
                                        s.store_ext.wishlist_error = None;
                                        s.store_ext.wishlist_checked_until =
                                            s.clock_now() + DONE_FLASH_SECS;
                                    } else {
                                        s.store_ext.wishlist_error =
                                            Some("Prices could not be checked. Try again.".into());
                                    }
                                }
                            }
                            Err(_) => {
                                s.store_ext.wishlist_error =
                                    Some("Prices could not be checked. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn store_open_tile_from(
        &mut self,
        tile: StoreTile,
        from_shelf: Option<String>,
        flight_id: Option<String>,
        tx: &mut Tx,
    ) {
        self.cover_flight_id = flight_id;
        tx.unclipped_flights();
        self.store_ext.seed = Some(tile.clone());
        self.store_ext.from_shelf = from_shelf;
        self.store_ext.more_scroll_x = 0.0;
        self.store_ext.more_scroll.set(ScrollPos::default());
        self.store_ext.offer_busy.clear();
        self.store_ext.offer_errors.clear();
        self.store_ext.gg_opening = false;
        self.open_store_game(&tile.app_id);
        tx.scroll_to("room-store-scroll", 0.0, 0.0);
    }

    pub fn store_load_game_full(&mut self, app_id: &str) {
        self.store_stop_trailer();
        self.store_offers = None;
        self.store_ext.recent_reviews = None;
        self.store_ext.game_error = None;
        self.store_ext.shown_media = None;
        self.store_ext.lightbox_index = None;
        self.store_ext.offer_busy.clear();
        self.store_ext.offer_errors.clear();
        if self.store_ext.seed.as_ref().map(|t| t.app_id.as_str()) != Some(app_id) {
            let found = self.store_home.as_ref().and_then(|h| {
                h.hero
                    .iter()
                    .chain(h.shelves.iter().flat_map(|sh| sh.items.iter()))
                    .find(|t| t.app_id == app_id)
                    .cloned()
            });
            self.store_ext.seed = found.or_else(|| {
                Some(StoreTile {
                    app_id: app_id.into(),
                    name: app_id.into(),
                    ..Default::default()
                })
            });
        }

        if self.status == HostStatus::Standalone {
            let seed = self.store_ext.seed.clone();
            let (g, offers, reviews) = demo_store_game(app_id, seed);
            self.store_game = Some(g);
            self.store_offers = Some(offers);
            self.store_ext.recent_reviews = Some(reviews);
            self.store_ext.game_loading = false;
            self.store_ext.reviews_loading = false;
            self.store_sync_trailer_for_game();
            return;
        }

        let now = self.clock_now();
        let cached = self.store_peek_cached_game(app_id, now);
        self.store_game = cached.clone();
        self.store_ext.game_loading = cached.is_none();
        self.store_ext.reviews_loading = true;
        if cached.is_some() {
            self.store_sync_trailer_for_game();
        }

        #[cfg(windows)]
        {
            if cached.is_none() {
                self.call_host_replaceable(
                    "store.game",
                    METHOD_STORE_GAME,
                    json!({ "appId": app_id }),
                    |s, res: Result<StoreGame, String>| {
                        s.store_ext.game_loading = false;
                        match res {
                            Ok(g) if g.ok => {
                                let now = s.clock_now();
                                s.store_keep_cached_game(g.clone(), now);
                                s.store_game = Some(g);
                                s.store_ext.game_error = None;
                                s.store_sync_trailer_for_game();
                            }
                            Ok(g) => {
                                s.store_game = None;
                                s.store_ext.game_error =
                                    Some(g.message.filter(|m| !m.is_empty()).unwrap_or_else(
                                        || "Exo couldn't reach the store. Try again.".into(),
                                    ));
                            }
                            Err(_) => {
                                s.store_game = None;
                                s.store_ext.game_error =
                                    Some("The store could not be reached.".into());
                            }
                        }
                    },
                );
            }

            self.call_host_replaceable(
                "store.gameOffers",
                METHOD_STORE_GAME_OFFERS,
                json!({ "appId": app_id }),
                |s, res: Result<StoreOtherOffers, String>| {
                    if let Ok(offers) = res
                        && offers.ok
                    {
                        s.store_offers = Some(offers);
                    } else {
                        s.store_offers = Some(StoreOtherOffers::default());
                    }
                },
            );

            self.call_host_replaceable(
                "store.gameReviews",
                METHOD_STORE_GAME_REVIEWS,
                json!({ "appId": app_id }),
                |s, res: Result<StoreRecentReviews, String>| {
                    s.store_ext.reviews_loading = false;
                    if let Ok(rev) = res
                        && rev.ok
                    {
                        s.store_ext.recent_reviews = Some(rev);
                    } else {
                        s.store_ext.recent_reviews = None;
                    }
                },
            );
        }
    }

    pub fn store_close_game_full(&mut self) {
        #[cfg(windows)]
        {
            self.cancel_inflight("store.game");
            self.cancel_inflight("store.gameOffers");
            self.cancel_inflight("store.gameReviews");
        }
        self.store_stop_trailer();
        self.store_ext.native_engine.leave_store_room();
        self.store_game = None;
        self.store_offers = None;
        self.store_ext.seed = None;
        self.store_ext.from_shelf = None;
        self.store_ext.game_error = None;
        self.store_ext.game_loading = false;
        self.store_ext.recent_reviews = None;
        self.store_ext.reviews_loading = false;
        self.store_ext.shown_media = None;
        self.store_ext.lightbox_index = None;
        self.store_ext.offer_busy.clear();
        self.store_ext.offer_errors.clear();
        self.store_ext.gg_opening = false;
    }

    pub fn store_open_offer(&mut self, store_key: String, store_name: String, url: String) {
        if self.store_ext.offer_busy.contains(&store_key) {
            return;
        }
        if url.starts_with("https://") {
            self.open_web_surface(crate::web_host::WebSurfaceKind::StorePage {
                title: store_name,
                url,
            });
            return;
        }
        if self.status == HostStatus::Standalone {
            self.notice = Some(format!("Open: {url}"));
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.store_ext.offer_busy.insert(store_key.clone());
            self.store_ext.offer_errors.remove(&store_key);
            let failed = format!("{store_name}'s page could not be opened.");
            rpc.call::<_, ActionResult>(METHOD_STORE_OPEN, &json!({ "url": url }), move |res| {
                handle.update(move |s, _| {
                    s.store_ext.offer_busy.remove(&store_key);
                    match res {
                        Ok(r) if r.ok => {}
                        Ok(r) => {
                            let msg = r.message.filter(|m| !m.is_empty()).unwrap_or(failed);
                            s.store_ext.offer_errors.insert(store_key, msg);
                        }
                        Err(_) => {
                            s.store_ext.offer_errors.insert(store_key, failed);
                        }
                    }
                });
            });
        }
    }

    pub fn store_open_external_url(&mut self, url: String) {
        if self.store_ext.gg_opening {
            return;
        }
        let _ = self.store_ext.native_engine.route_store_page(
            exo_stores::webview_fallback::StoreOperation::DrmCheckoutPage,
            "Store",
            Some(&url),
        );
        if self.status == HostStatus::Standalone {
            self.notice = Some(format!("Open: {url}"));
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.store_ext.gg_opening = true;
            rpc.call::<_, ActionResult>(
                METHOD_SHELL_OPEN_URL,
                &json!({ "url": url }),
                move |res| {
                    handle.update(move |s, _| {
                        s.store_ext.gg_opening = false;
                        if let Ok(r) = res
                            && !r.ok
                            && let Some(msg) = r.message
                        {
                            s.notice = Some(msg);
                        }
                    });
                },
            );
        }
    }

    pub fn store_open_owned_in_library(&mut self, game_id: &str, tx: &mut Tx) {
        self.store_close_game_full();
        self.switch_room(Room::Library);
        self.open_game_page_from(game_id, None, tx);
    }

    pub fn store_on_query_edited(&mut self) {
        let q = self.store_query.text.trim().to_string();
        self.store_ext.search_error = None;
        if q.len() < 2 {
            self.store_search_results = None;
            self.store_ext.search_query_sent.clear();
            self.store_ext.search_pending_query.clear();
            self.store_ext.search_due_at = None;
            self.store_ext.search_pending = false;
            return;
        }
        self.store_ext.search_pending_query = q;
        self.store_ext.search_due_at = Some(self.clock_now() + SEARCH_DEBOUNCE_SECS);
        self.store_ext.search_pending = true;
    }

    pub fn store_clear_search(&mut self) {
        self.store_query.text.clear();
        self.store_query.sync_invariants();
        self.store_search_results = None;
        self.store_ext.search_query_sent.clear();
        self.store_ext.search_observed_query.clear();
        self.store_ext.search_pending_query.clear();
        self.store_ext.search_due_at = None;
        self.store_ext.search_error = None;
        self.store_ext.search_pending = false;
    }

    pub fn store_run_search_now(&mut self, query: String) {
        let q = query.trim().to_string();
        if q.len() < 2 {
            self.store_clear_search();
            return;
        }
        self.store_ext.search_due_at = None;
        self.store_ext.search_pending_query = q.clone();
        self.store_ext.search_error = None;
        self.store_ext.search_pending = true;

        if self.status == HostStatus::Standalone {
            let lower = q.to_lowercase();
            let hits: Vec<StoreTile> = self
                .store_home
                .as_ref()
                .map(|h| {
                    h.hero
                        .iter()
                        .chain(h.shelves.iter().flat_map(|sh| sh.items.iter()))
                        .filter(|t| t.name.to_lowercase().contains(&lower))
                        .cloned()
                        .collect()
                })
                .unwrap_or_default();
            self.store_search_results = Some(hits);
            self.store_ext.search_query_sent = q;
            self.store_ext.search_pending = false;
            return;
        }

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let req_q = q.clone();
            rpc.call::<_, StoreSearchReply>(
                METHOD_STORE_SEARCH,
                &json!({ "query": req_q, "typed": true }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.store_query.text.trim() != req_q {
                            return;
                        }
                        s.store_ext.search_pending = false;
                        match res {
                            Ok(reply) if reply.ok => {
                                s.store_search_results = Some(reply.items);
                                s.store_ext.search_query_sent = req_q;
                                s.store_ext.search_error = None;
                            }
                            Ok(reply) => {
                                s.store_ext.search_error =
                                    Some(reply.message.filter(|m| !m.is_empty()).unwrap_or_else(
                                        || "Search didn't work. Try again.".into(),
                                    ));
                            }
                            Err(_) => {
                                s.store_ext.search_error =
                                    Some("Search could not reach the store. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn apply_store_sale(&mut self, ev: StoreSaleEvent) {
        let msg = format_sale_notice(&ev);
        self.push_notice(msg, NoticeTone::Neutral);
    }

    pub fn store_select_hero(&mut self, idx: usize) {
        self.store_hero_index = idx;
        self.store_ext.hero_elapsed = 0.0;
        self.store_ext.hero_timer_start = self.clock_now();
    }

    pub fn store_wake(&self) {
        if let Some(waker) = &self.store_ext.waker {
            waker();
        }
    }

    pub fn warm_store_on_startup(&mut self) {
        if !self.store_loading {
            self.load_store_home(false);
        }
        self.load_store_wishlist();
    }

    pub fn store_on_room_enter(&mut self) {
        self.store_ext.room_visible_since = Some(self.clock_now());
        self.store_ext.hover_warm = None;
        self.store_wake();
    }

    pub fn store_on_room_leave(&mut self) {
        self.store_stop_trailer();
        self.store_ext.room_visible_since = None;
        self.store_ext.hover_warm = None;
    }

    pub fn store_hover_tile(&mut self, app_id: Option<String>) {
        match app_id {
            Some(id) if !id.is_empty() => {
                self.store_ext.hover_warm = Some((id, self.clock_now() + HOVER_WARM_SECS));
                self.store_wake();
            }
            _ => {
                self.store_ext.hover_warm = None;
            }
        }
    }

    pub fn store_peek_cached_game(&self, app_id: &str, now: f64) -> Option<StoreGame> {
        let entry = self.store_ext.game_cache.get(app_id)?;
        if (now - entry.at) < GAME_KEEP_SECS {
            Some(entry.game.clone())
        } else {
            None
        }
    }

    pub fn store_keep_cached_game(&mut self, game: StoreGame, now: f64) {
        if !game.ok || game.app_id.is_empty() {
            return;
        }
        let app_id = game.app_id.clone();
        if !self.store_ext.game_cache.contains_key(&app_id) {
            while self.store_ext.game_cache.len() >= KEEP_MAX_ENTRIES {
                if let Some(oldest) = self.store_ext.game_cache_order.pop_front() {
                    self.store_ext.game_cache.remove(&oldest);
                } else {
                    break;
                }
            }
            self.store_ext.game_cache_order.push_back(app_id.clone());
        }
        self.store_ext
            .game_cache
            .insert(app_id, CachedStoreGame { at: now, game });
    }

    pub fn store_collect_visible_warm_ids(&self) -> Vec<String> {
        let mut seen = HashSet::new();
        let mut out = Vec::with_capacity(VISIBLE_WARM_COUNT);
        let mut push_id = |id: &str| -> bool {
            if out.len() < VISIBLE_WARM_COUNT && !id.is_empty() && seen.insert(id.to_string()) {
                out.push(id.to_string());
            }
            out.len() >= VISIBLE_WARM_COUNT
        };
        let searching = self.store_query.text.trim().len() >= 2;
        if searching {
            if let Some(items) = &self.store_search_results {
                for tile in items {
                    if push_id(&tile.app_id) {
                        break;
                    }
                }
            }
            return out;
        }
        if self.store_tab == 1 {
            for tile in &self.store_ext.wishlist {
                if push_id(&tile.app_id) {
                    break;
                }
            }
            return out;
        }
        if let Some(home) = &self.store_home {
            if let Some(hero) = home
                .hero
                .get(self.store_hero_index)
                .or_else(|| home.hero.first())
            {
                let _ = push_id(&hero.app_id);
            }
            for shelf in &home.shelves {
                for tile in &shelf.items {
                    if push_id(&tile.app_id) {
                        return out;
                    }
                }
            }
            for hero in &home.hero {
                if push_id(&hero.app_id) {
                    break;
                }
            }
        }
        out
    }

    pub fn store_enqueue_warm(&mut self, app_id: String, now: f64) {
        if app_id.is_empty()
            || self.store_peek_cached_game(&app_id, now).is_some()
            || self.store_ext.warmed_ids.contains(&app_id)
            || self.store_ext.warm_inflight.contains(&app_id)
        {
            return;
        }
        if self.store_ext.warmed_ids.len() >= WARMED_MAX_ENTRIES
            && let Some(oldest) = self.store_ext.warmed_order.pop_front()
        {
            self.store_ext.warmed_ids.remove(&oldest);
        }
        self.store_ext.warmed_ids.insert(app_id.clone());
        self.store_ext.warmed_order.push_back(app_id.clone());
        self.store_ext.warm_queue.push_back(app_id);
    }

    pub fn store_pump_warm(&mut self) {
        while self.store_ext.warm_inflight.len() < WARM_CONCURRENCY {
            let Some(app_id) = self.store_ext.warm_queue.pop_front() else {
                break;
            };
            if self.status == HostStatus::Standalone {
                let now = self.clock_now();
                let seed = self.store_home.as_ref().and_then(|h| {
                    h.hero
                        .iter()
                        .chain(h.shelves.iter().flat_map(|sh| sh.items.iter()))
                        .find(|t| t.app_id == app_id)
                        .cloned()
                });
                let (game, _, _) = demo_store_game(&app_id, seed);
                self.store_keep_cached_game(game, now);
                continue;
            }
            #[cfg(windows)]
            if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
                let hero_url = StoreArt::steam_url(&app_id, "hero");
                let logo_url = StoreArt::steam_url(&app_id, "logo");
                let _ = self.remote_image_sized(&hero_url, 1440.0, 1.0);
                let _ = self.remote_image_sized(&logo_url, 480.0, 1.0);
                self.store_ext.warm_inflight.insert(app_id.clone());
                let req_id = app_id.clone();
                rpc.call::<_, StoreGame>(
                    METHOD_STORE_GAME,
                    &json!({ "appId": req_id }),
                    move |res| {
                        handle.update(move |s, _| {
                            s.store_ext.warm_inflight.remove(&app_id);
                            if let Ok(g) = res
                                && g.ok
                            {
                                if let Some(first_thumb) = g
                                    .media
                                    .first()
                                    .map(|m| m.thumb.as_str())
                                    .filter(|u| !u.is_empty())
                                    .or_else(|| {
                                        g.screenshots
                                            .first()
                                            .map(String::as_str)
                                            .filter(|u| !u.is_empty())
                                    })
                                {
                                    let _ = s.remote_image_sized(first_thumb, 600.0, 1.0);
                                }
                                let now = s.clock_now();
                                s.store_keep_cached_game(g.clone(), now);
                                if s.store_game.is_none()
                                    && s.store_ext.seed.as_ref().map(|t| t.app_id.as_str())
                                        == Some(app_id.as_str())
                                {
                                    s.store_game = Some(g);
                                    s.store_ext.game_loading = false;
                                    s.store_ext.game_error = None;
                                    s.store_sync_trailer_for_game();
                                }
                            }
                            s.store_pump_warm();
                        });
                    },
                );
            } else {
                break;
            }
        }
    }

    pub fn store_tick(&mut self, now: f64) -> bool {
        let mut changed = false;
        let running_class = self
            .games
            .iter()
            .find(|g| g.is_running)
            .map(|g| g.anti_cheat_class);
        self.store_ext
            .native_engine
            .sync_running_game_throttle(running_class);

        let current_q = self.store_query.text.trim().to_string();
        if current_q != self.store_ext.search_observed_query {
            self.store_ext.search_observed_query = current_q;
            self.store_on_query_edited();
            changed = true;
        }

        if let Some(due) = self.store_ext.search_due_at
            && now >= due
        {
            let q = self.store_ext.search_pending_query.clone();
            self.store_run_search_now(q);
            changed = true;
        }

        if let Some((_, due)) = &self.store_ext.hover_warm {
            if now >= *due {
                if let Some((app_id, _)) = self.store_ext.hover_warm.take() {
                    self.store_enqueue_warm(app_id, now);
                    self.store_pump_warm();
                }
                changed = true;
            } else {
                changed = true;
            }
        }

        if self.room == Room::Store
            && !self.store_has_page_open()
            && self.store_home.is_some()
            && let Some(since) = self.store_ext.room_visible_since
        {
            if (now - since) >= WARM_DWELL_SECS {
                self.store_ext.room_visible_since = None;
                for app_id in self.store_collect_visible_warm_ids() {
                    self.store_enqueue_warm(app_id, now);
                }
                self.store_pump_warm();
                changed = true;
            } else {
                changed = true;
            }
        }

        let hero_count = self.store_home.as_ref().map_or(0, |h| h.hero.len().min(6));
        let searching = self.store_query.text.trim().len() >= 2;
        let can_rotate = self.room == Room::Store
            && self.store_tab == 0
            && !searching
            && !self.store_has_page_open()
            && !self.store_ext.hero_hovered
            && !self.settings.reduce_motion
            && hero_count > 1;

        if can_rotate {
            if self.store_ext.hero_timer_start <= 0.0 {
                self.store_ext.hero_timer_start = now;
            }
            let dt = (now - self.store_ext.hero_timer_start).max(0.0);
            self.store_ext.hero_timer_start = now;
            self.store_ext.hero_elapsed += dt;
            if self.store_ext.hero_elapsed >= HERO_ROTATE_SECS {
                self.store_ext.hero_elapsed = 0.0;
                self.store_hero_index = (self.store_hero_index + 1) % hero_count;
            }
            changed = true;
        } else {
            self.store_ext.hero_timer_start = now;
        }

        if self.store_ext.wishlist_checked_until > 0.0
            && now >= self.store_ext.wishlist_checked_until
        {
            self.store_ext.wishlist_checked_until = 0.0;
            changed = true;
        }
        if self.store_ext.wishlist_flash_until > 0.0 && now >= self.store_ext.wishlist_flash_until {
            self.store_ext.wishlist_flash_until = 0.0;
            changed = true;
        }

        changed
    }
}

fn hero_logo_slot(s: &AppState, app_id: &str, name: &str, scale: f32) -> El {
    let logo_url = StoreArt::steam_url(app_id, "logo");
    let logo_img = s.remote_image_sized(&logo_url, 640.0, scale);
    let mut slot = row().w_full().h(80.0).items_end();
    if let Some(img) = logo_img {
        slot = slot.child(
            el().key(format!("sg-logo-{app_id}"))
                .w(440.0)
                .h(80.0)
                .pixels_fit(img.pixels(), SurfaceFit::Fit),
        );
    } else {
        slot = slot.child(
            text(name.to_string())
                .font(exo_kit::type_first_run())
                .color(FG)
                .w_full()
                .wrap(),
        );
    }
    slot
}

fn tag_pills_row(tags: Option<&[String]>) -> El {
    let mut bar = row().w_full().h(26.0).gap(8.0).clip_rounded(0.0).center_y();
    if let Some(list) = tags {
        for tag in list.iter().take(8) {
            bar = bar.child(
                el().h(26.0)
                    .px(11.0)
                    .center()
                    .radius(RADIUS_PILL)
                    .fill(exo_kit::surface_elevated_hi().alpha(0.85))
                    .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                    .child(
                        text(tag.clone())
                            .font(TextStyle::new(scaled(12.0), 500.0))
                            .color(FG.alpha(0.92)),
                    ),
            );
        }
    }
    bar
}

fn offer_row_button(
    s: &AppState,
    offer: &StoreOffer,
    primary: bool,
    free: bool,
    soon: bool,
    gamepad: bool,
) -> El {
    let store_key = if offer.store.is_empty() {
        offer.display_name()
    } else {
        offer.store.clone()
    };
    let display_name = offer.display_name();
    let label = if primary {
        if soon {
            format!("Wishlist on {display_name}")
        } else {
            format!("Get on {display_name}")
        }
    } else {
        display_name.clone()
    };
    let busy = s.store_ext.offer_busy.contains(&store_key);
    let err = s.store_ext.offer_errors.get(&store_key).cloned();
    let url = offer.url.clone();
    let key_for_click = store_key.clone();
    let name_for_click = display_name.clone();

    let price_el = if soon {
        None
    } else if primary {
        render_price(
            free,
            offer.final_cents,
            offer.initial,
            offer.discount,
            offer.currency.as_deref(),
            offer.price_text.as_deref(),
            offer.was_text.as_deref(),
            true,
        )
    } else {
        render_price(
            false,
            offer.final_cents,
            None,
            0,
            offer.currency.as_deref(),
            offer.price_text.as_deref(),
            None,
            false,
        )
    };

    let fg = if primary { PRIMARY_FG } else { FG };
    let mut btn = row()
        .key(format!("sg-offer-{store_key}"))
        .role("button")
        .label(label.clone())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .min_w(0.0)
        .h(36.0)
        .px(16.0)
        .gap(12.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if primary { FG } else { glass_control_fill() })
        .hover(if primary {
            FG.alpha(0.92)
        } else {
            hover_fill()
        })
        .press(if primary {
            FG.alpha(0.84)
        } else {
            pressed_fill()
        })
        .motion(SPRING_QUICK)
        .child(
            text(label)
                .font(type_control())
                .color(fg)
                .grow(1.0)
                .min_w(0.0)
                .truncate(),
        )
        .children(price_el)
        .child(icon_external(14.0, fg));

    if busy {
        btn = btn.opacity(0.7);
    }
    if primary {
        btn = btn.w_full();
    } else {
        btn = btn
            .grow(1.0)
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
    }
    if !busy && !url.is_empty() {
        btn = btn.on_press(move |s: &mut AppState| {
            s.store_open_offer(key_for_click.clone(), name_for_click.clone(), url.clone());
        });
    }
    if gamepad {
        btn = btn.focus_ring(2.0, 3.0, FG);
    }

    if let Some(msg) = err {
        col()
            .w_full()
            .min_w(0.0)
            .gap(4.0)
            .child(btn)
            .child(text(msg).font(type_small()).color(bad()).w_full().wrap())
    } else {
        btn
    }
}

fn buy_panel(
    s: &AppState,
    seed: &StoreTile,
    game: Option<&StoreGame>,
    owned: Option<&Game>,
    wished: bool,
    gamepad: bool,
) -> El {
    let soon = game.map_or(seed.soon, |g| g.coming_soon);
    let loading = game.is_none() && s.store_ext.game_error.is_none();
    let tile_for_wish = seed.clone();
    let wish_next = !wished;

    let mut wish_btn = row()
        .key("sg-wishlist")
        .role("button")
        .label(if wished { "On wishlist" } else { "Wishlist" })
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
        .liquid_glass(exo_kit::exo_glass(ExoGlass::Control))
        .hover(hover_fill())
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| {
            s.store_toggle_wishlist(tile_for_wish.clone(), wish_next);
        })
        .child(if wished {
            icon_check(14.0, good())
        } else {
            icon_bookmark(14.0, FG)
        })
        .child(
            text(if wished { "On wishlist" } else { "Wishlist" })
                .font(type_control())
                .color(FG),
        );
    if gamepad {
        wish_btn = wish_btn.focus_ring(2.0, 3.0, FG);
    }

    if let Some(lib_game) = owned {
        let gid = lib_game.id.clone();
        let mut lib_btn = row()
            .key("sg-open-library")
            .role("button")
            .label("Library")
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .w_full()
            .h(36.0)
            .px(16.0)
            .gap(12.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(FG)
            .hover(FG.alpha(0.92))
            .press(FG.alpha(0.84))
            .motion(SPRING_QUICK)
            .on_press_tx(move |s: &mut AppState, tx| {
                s.store_open_owned_in_library(&gid, tx);
            })
            .child(
                text("Library")
                    .font(type_control())
                    .color(PRIMARY_FG)
                    .grow(1.0)
                    .min_w(0.0),
            )
            .child(icon_arrow_right(14.0, PRIMARY_FG));
        if gamepad {
            lib_btn = lib_btn.focus_ring(2.0, 3.0, FG);
        }
        let mut panel = col().w(360.0).gap(8.0).child(lib_btn);
        if wished {
            panel = panel.child(row().mt(4.0).gap(8.0).child(wish_btn));
        }
        return panel;
    }

    let steam_offers: Vec<StoreOffer> = if let Some(g) = game {
        g.offers.clone()
    } else if loading {
        vec![seed_offer(seed)]
    } else {
        Vec::new()
    };
    let free = game.is_some_and(|g| g.free) || (game.is_none() && seed.free);
    let game_name = game.map_or(seed.name.as_str(), |g| g.name.as_str());
    let gg_url = gg_deals_url(game_name);

    let mut gg_btn = row()
        .key("sg-ggdeals")
        .role("button")
        .label("GG.deals")
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
        .liquid_glass(exo_kit::exo_glass(ExoGlass::Control))
        .hover(hover_fill())
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| s.store_open_external_url(gg_url.clone()))
        .child(icon_external(14.0, FG))
        .child(text("GG.deals").font(type_control()).color(FG));
    if s.store_ext.gg_opening {
        gg_btn = gg_btn.opacity(0.65);
    }
    if gamepad {
        gg_btn = gg_btn.focus_ring(2.0, 3.0, FG);
    }

    let mut panel = col().w(360.0).gap(8.0);
    let others: Vec<&StoreOffer> = s
        .store_offers
        .as_ref()
        .map(|o| {
            o.offers
                .iter()
                .filter(|off| off.store != "1" && !off.display_name().eq_ignore_ascii_case("steam"))
                .collect()
        })
        .unwrap_or_default();
    if !others.is_empty() {
        let mut others_row = row()
            .key("sg-other-offers")
            .w_full()
            .gap(8.0)
            .transition(Transition::opacity());
        for off in others {
            others_row = others_row.child(offer_row_button(s, off, false, false, soon, gamepad));
        }
        panel = panel.child(others_row);
    }
    for off in &steam_offers {
        panel = panel.child(offer_row_button(s, off, true, free, soon, gamepad));
    }
    panel.child(
        row()
            .mt(4.0)
            .gap(8.0)
            .flex_wrap()
            .child(wish_btn)
            .child(gg_btn),
    )
}

fn about_card(game: Option<&StoreGame>) -> El {
    let mut card = exo_kit::amoled_card(RADIUS_CARD)
        .key("sg-about")
        .w_full()
        .px(20.0)
        .py(18.0)
        .gap(10.0)
        .child(
            text("About")
                .font(TextStyle::new(scaled(13.0), 650.0).lh(scaled(18.0)))
                .color(FG),
        );
    match game {
        None => {
            card = card.child(
                el().w_full()
                    .h(48.0)
                    .radius(RADIUS_CARD)
                    .fill(glass_control_fill()),
            );
        }
        Some(g) => {
            let desc = g.description.as_deref().unwrap_or("");
            if !desc.is_empty() {
                card = card.child(
                    text(desc.to_string())
                        .font(type_body().lh(scaled(20.0)))
                        .color(FG)
                        .w_full()
                        .wrap(),
                );
            }
        }
    }
    card
}

fn facts_card(
    game: Option<&StoreGame>,
    recent: Option<&StoreRecentReviews>,
    reviews_loading: bool,
) -> El {
    let release_label = if game.is_some_and(|g| g.coming_soon) {
        "Release"
    } else {
        "Released"
    };
    let fact = |f: fn(&StoreGame) -> String| -> Option<String> { game.map(f) };
    let recent_val = if reviews_loading && recent.is_none() {
        None
    } else {
        Some(
            recent
                .map(|r| score_text(r.word.as_deref(), r.percent, r.count))
                .unwrap_or_default(),
        )
    };
    let cells: [(&str, Option<String>); 10] = [
        (
            "Reviews",
            fact(|g| score_text(g.review_word.as_deref(), g.review_percent, g.review_count)),
        ),
        (
            release_label,
            fact(|g| g.released.clone().unwrap_or_default()),
        ),
        ("Developer", fact(|g| g.developers.join(", "))),
        ("Publisher", fact(|g| g.publishers.join(", "))),
        (
            "Genres",
            fact(|g| {
                g.genres
                    .iter()
                    .take(4)
                    .cloned()
                    .collect::<Vec<_>>()
                    .join(", ")
            }),
        ),
        ("Recent reviews", recent_val),
        ("Controller", fact(controller_text)),
        ("Languages", fact(|g| languages_text(&g.languages))),
        (
            "Other account",
            fact(|g| g.account.clone().unwrap_or_default()),
        ),
        ("DRM", fact(|g| g.drm.clone().unwrap_or_default())),
    ];

    let visible_cells: Vec<(&str, Option<String>)> = cells
        .into_iter()
        .filter(|(_, v)| !matches!(v, Some(val) if val.is_empty()))
        .collect();

    let mut grid = col().w_full().gap(10.0);
    for pair in visible_cells.chunks(2) {
        let mut r = row().w_full().gap(16.0).items_start();
        for (k, v) in pair {
            let mut cell = col().grow(1.0).min_w(0.0).gap(2.0);
            match v {
                None => {
                    cell = cell
                        .child(text(*k).font(type_small()).color(fg_muted()))
                        .child(el().w(96.0).h(14.0).radius(4.0).fill(glass_control_fill()));
                }
                Some(val) => {
                    cell = cell
                        .child(text(*k).font(type_small()).color(fg_muted()))
                        .child(
                            text(val.clone())
                                .font(type_small())
                                .color(FG)
                                .w_full()
                                .wrap(),
                        );
                }
            }
            r = r.child(cell);
        }
        if pair.len() == 1 {
            r = r.child(el().grow(1.0).min_w(0.0));
        }
        grid = grid.child(r);
    }

    exo_kit::amoled_card(RADIUS_CARD)
        .key("sg-facts")
        .w_full()
        .px(18.0)
        .py(16.0)
        .child(grid)
}

fn specs_card(game: Option<&StoreGame>) -> El {
    let specs = game.map(|g| g.specs.as_slice());
    let fit = game.and_then(|g| g.fit.as_deref());
    let both = specs.is_none_or(|list| list.iter().any(|s| s.recommended.is_some()));

    let verdict = fit.and_then(|v| match v {
        "recommended" => Some(("Runs at Recommended", good(), good().alpha(0.16))),
        "minimum" => Some((
            if both {
                "Meets Minimum"
            } else {
                "Meets the requirements"
            },
            FG,
            glass_control_fill(),
        )),
        "below" => Some(("Below Minimum", bad(), bad().alpha(0.16))),
        _ => None,
    });

    let mut head = row().w_full().justify_between().center_y().gap(8.0).child(
        text("System requirements")
            .font(TextStyle::new(scaled(13.0), 650.0))
            .color(FG),
    );
    if let Some((label, color, bg)) = verdict {
        head = head.child(
            el().px(10.0)
                .py(2.0)
                .radius(RADIUS_PILL)
                .fill(bg)
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(
                    text(label)
                        .font(TextStyle::new(scaled(12.0), 650.0))
                        .color(color),
                ),
        );
    }

    let mut rows = col().w_full();
    let mut rendered_any = false;
    for part in SPEC_PARTS {
        let found = specs.and_then(|list| list.iter().find(|s| s.label == part));
        if specs.is_some()
            && found.is_none_or(|s| {
                s.minimum.as_deref().is_none_or(str::is_empty)
                    && s.recommended.as_deref().is_none_or(str::is_empty)
                    && s.mine.as_deref().is_none_or(str::is_empty)
            })
        {
            continue;
        }
        rendered_any = true;
        let mut item = col().w_full().py(7.0).gap(2.0).border(0.0, TRANSPARENT);
        item = item.child(el().w_full().h(1.0).mb(5.0).fill(float_line()));

        if specs.is_none() {
            item = item.child(
                row()
                    .w_full()
                    .justify_between()
                    .center_y()
                    .child(text(part).font(type_body()).color(fg_muted()))
                    .child(el().w(120.0).h(14.0).radius(4.0).fill(glass_control_fill())),
            );
        } else {
            let mine_text = found.and_then(|s| s.mine.as_deref());
            let meets = found.and_then(|s| s.meets.as_deref());
            let mine_color = match meets {
                Some("recommended") => good(),
                Some("minimum") if !both => good(),
                Some("below") => bad(),
                Some(_) => FG,
                None => fg_muted(),
            };
            let mark = match meets {
                Some("below") => Some(icon_close(12.0, bad())),
                Some("recommended") | Some("minimum") => Some(icon_check(12.0, mine_color)),
                _ => None,
            };
            let mut top_line = row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(8.0)
                .child(text(part).font(type_body()).color(fg_muted()));
            if let Some(m) = mine_text {
                top_line = top_line.child(
                    row().gap(6.0).center_y().children(mark).child(
                        text(m.to_string())
                            .font(TextStyle::new(
                                scaled(13.0),
                                if meets.is_some() { 650.0 } else { 500.0 },
                            ))
                            .color(mine_color)
                            .truncate(),
                    ),
                );
            }
            let min_val = found
                .and_then(|s| s.minimum.as_deref())
                .filter(|v| !v.is_empty());
            let rec_val = found
                .and_then(|s| s.recommended.as_deref())
                .filter(|v| !v.is_empty() && Some(*v) != min_val);

            item = item.child(top_line);
            if let Some(min_str) = min_val {
                item = item.child(
                    row()
                        .w_full()
                        .gap(8.0)
                        .items_start()
                        .child(text("Minimum").font(type_small()).color(fg_faint()).w(92.0))
                        .child(
                            text(min_str.to_string())
                                .font(type_small())
                                .color(fg_muted())
                                .grow(1.0)
                                .min_w(0.0)
                                .wrap(),
                        ),
                );
            }
            if let Some(rec_str) = rec_val {
                item = item.child(
                    row()
                        .w_full()
                        .gap(8.0)
                        .items_start()
                        .child(
                            text("Recommended")
                                .font(type_small())
                                .color(fg_faint())
                                .w(92.0),
                        )
                        .child(
                            text(rec_str.to_string())
                                .font(type_small())
                                .color(fg_muted())
                                .grow(1.0)
                                .min_w(0.0)
                                .wrap(),
                        ),
                );
            }
        }
        rows = rows.child(item);
    }
    if specs.is_some() && !rendered_any {
        rows = rows.child(
            text("System requirements haven’t been announced yet.")
                .font(type_small())
                .color(fg_muted())
                .pt(4.0)
                .wrap(),
        );
    }

    exo_kit::amoled_card(RADIUS_CARD)
        .key("sg-specs")
        .w_full()
        .px(20.0)
        .py(18.0)
        .gap(10.0)
        .child(head)
        .child(rows)
}

pub fn view(s: &AppState, scale: f32) -> El {
    let gamepad = s.input_mode == InputMode::Gamepad;
    let game = s.store_game.as_ref();
    let fallback_seed = game.map(|g| StoreTile {
        app_id: g.app_id.clone(),
        name: g.name.clone(),
        price_text: g.price_text.clone(),
        was_text: g.was_text.clone(),
        discount: g.discount,
        free: g.free,
        soon: g.coming_soon,
        art: g.art.clone(),
        ..Default::default()
    });
    let seed = s
        .store_ext
        .seed
        .as_ref()
        .or(fallback_seed.as_ref())
        .cloned()
        .unwrap_or_default();

    let app_id = game.map_or(seed.app_id.as_str(), |g| g.app_id.as_str());
    let name = game.map_or(seed.name.as_str(), |g| g.name.as_str());
    let owned = find_owned_game(&s.games, app_id, name);
    let wished = s.store_is_wished(app_id, seed.wished);

    let mut back_btn = exo_kit::apply_glass(
        row()
            .key("sg-back")
            .role("button")
            .label("Store")
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
            .on_press_tx(|s: &mut AppState, tx| {
                s.input_mode = InputMode::Pointer;
                s.close_store_game_tx(tx);
            })
            .child(icon_chevron_left(16.0, FG))
            .child(text("Store").font(type_control()).color(FG)),
        ExoGlass::ClearMedia,
    );
    if gamepad {
        back_btn = back_btn.focus_ring(2.0, 3.0, FG);
    }

    let hue = crate::state::title_hue(name);
    let hero_candidates = [
        game.and_then(|g| g.art.as_ref().and_then(StoreArt::best_hero))
            .map(ToString::to_string),
        seed.art
            .as_ref()
            .and_then(StoreArt::best_hero)
            .map(ToString::to_string),
        Some(StoreArt::steam_url(app_id, "hero")),
        game.and_then(|g| g.header.clone()),
        Some(StoreArt::steam_url(app_id, "header")),
    ];
    let hero_img = hero_candidates
        .into_iter()
        .flatten()
        .find_map(|u| s.remote_image_sized(&u, 1920.0, scale));

    let mut hero_bg = el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0));
    if let Some(ref img) = hero_img {
        hero_bg = hero_bg
            .pixels_fit(img.pixels(), SurfaceFit::Fill)
            .surface_anchor_fraction(0.5, 0.22);
    } else {
        hero_bg = hero_bg.linear_gradient(
            0.3,
            [
                (0.0, rune::oklch(0.32, 0.14, hue).alpha(0.68)),
                (1.0, exo_kit::BG),
            ],
        );
    }

    let hero_fade_v = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, exo_kit::BG.alpha(0.0)),
                (0.36, exo_kit::BG.alpha(0.05)),
                (0.68, exo_kit::BG.alpha(0.54)),
                (0.90, exo_kit::BG.alpha(0.88)),
                (1.0, exo_kit::BG),
            ],
        );

    let hero_fade_h = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .linear_gradient(
            std::f32::consts::FRAC_PI_2,
            [
                (0.0, exo_kit::BG.alpha(0.46)),
                (0.38, exo_kit::BG.alpha(0.14)),
                (0.72, exo_kit::BG.alpha(0.0)),
            ],
        );

    let header = row()
        .w_full()
        .gap(24.0)
        .items_end()
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(12.0)
                .child(hero_logo_slot(s, app_id, name, scale))
                .child(tag_pills_row(game.map(|g| g.tags.as_slice()))),
        )
        .child(buy_panel(s, &seed, game, owned, wished, gamepad));

    let hero_h = (s.chrome.window_height * 0.5).clamp(360.0, 560.0);
    let hero_stage = el()
        .w_full()
        .min_h(hero_h)
        .clip_rounded(0.0)
        .fill(exo_kit::BG)
        .child(hero_bg)
        .child(hero_fade_v)
        .child(hero_fade_h)
        .child(
            col()
                .w_full()
                .min_h(hero_h)
                .pt(exo_kit::TITLEBAR_HEIGHT + 10.0)
                .px(20.0)
                .pb(20.0)
                .justify_between()
                .gap(24.0)
                .child(row().w_full().child(back_btn))
                .child(header),
        );

    let media_block = if let Some(g) = game {
        media_viewer(s, g, scale)
    } else if let Some(ref err) = s.store_ext.game_error {
        let retry_id = app_id.to_string();
        col()
            .key("sg-error")
            .w_full()
            .h(360.0)
            .center()
            .gap(12.0)
            .radius(RADIUS_CARD)
            .fill(exo_kit::BG)
            .border(1.0, float_line())
            .child(
                text("This page didn't load")
                    .font(TextStyle::new(scaled(15.0), 650.0))
                    .color(FG),
            )
            .child(
                text("Exo could not load this game's page.")
                    .font(type_body())
                    .color(fg_muted()),
            )
            .child(text(err.clone()).font(type_small()).color(bad()))
            .child(
                glass_button("sg-retry", "Try again", None)
                    .on_press(move |s: &mut AppState| s.store_load_game_full(&retry_id)),
            )
    } else {
        media_wait()
    };

    let left_col = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(20.0)
        .child(media_block)
        .child(about_card(game));

    let right_col = col()
        .w(360.0)
        .gap(16.0)
        .child(facts_card(
            game,
            s.store_ext.recent_reviews.as_ref(),
            s.store_ext.reviews_loading,
        ))
        .child(specs_card(game));

    let body = row()
        .w_full()
        .px(20.0)
        .gap(20.0)
        .items_start()
        .child(left_col)
        .child(right_col);

    let mut page = col()
        .key(format!("store-game-page-{app_id}"))
        .w_full()
        .fill(exo_kit::BG)
        .pb(34.0)
        .gap(16.0)
        .transition(
            Transition::opacity()
                .springs(SpringPair::new(exo_kit::SPRING_MOVE, exo_kit::SPRING_MOVE)),
        )
        .child(hero_stage)
        .child(body);

    let more_items = more_like_this_items(s, app_id);
    if !more_items.is_empty() {
        let (can_prev, can_next) = s.store_more_can_page(more_items.len());
        let more_row = store_shelf_row(
            s,
            "more",
            "More like this",
            &more_items,
            s.store_ext.from_shelf.as_deref(),
            can_prev,
            can_next,
            s.store_ext.more_scroll.clone(),
            scale,
            None,
        );
        page = page.child(el().w_full().pt(12.0).child(more_row));
    }

    page
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::StoreLowest;
    use exo_rpc::types::{GameInclude, GameVariant};

    #[test]
    fn gg_deals_slug_matches_react() {
        assert_eq!(
            gg_deals_url("Baldur's Gate 3"),
            "https://gg.deals/game/baldurs-gate-3/"
        );
        assert_eq!(
            gg_deals_url("Cyberpunk 2077™"),
            "https://gg.deals/game/cyberpunk-2077/"
        );
        assert_eq!(gg_deals_url("   "), "https://gg.deals/search/?title=");
    }

    #[test]
    fn owned_matching_uses_steam_id_and_non_steam_title() {
        let games = vec![
            Game {
                id: "steam:1091500".into(),
                title: "Cyberpunk 2077".into(),
                store: "steam".into(),
                owned: true,
                ..Default::default()
            },
            Game {
                id: "epic:Hades2".into(),
                title: "Hades II".into(),
                store: "epic".into(),
                installed: true,
                ..Default::default()
            },
            Game {
                id: "steam:999".into(),
                title: "Call of Duty HQ".into(),
                store: "steam".into(),
                owned: true,
                includes: vec![GameInclude {
                    app_id: Some("1938090".into()),
                    title: Some("MW3".into()),
                }],
                ..Default::default()
            },
            Game {
                id: "steam:254700".into(),
                title: "Resident Evil 4".into(),
                store: "steam".into(),
                owned: true,
                variants: vec![GameVariant {
                    id: "steam:254700".into(),
                    launch_target: None,
                    installed: false,
                    owned: true,
                    ..Default::default()
                }],
                ..Default::default()
            },
        ];

        assert!(find_owned_game(&games, "1091500", "Cyberpunk 2077").is_some());
        assert!(find_owned_game(&games, "1145350", "Hades II").is_some());
        assert!(find_owned_game(&games, "1938090", "MW3").is_some());
        assert!(find_owned_game(&games, "2050650", "Resident Evil 4").is_none());
    }

    #[test]
    fn offer_display_name_and_lowest_date_never_leak_raw_ids_or_iso() {
        let steam = StoreOffer {
            store: "1".into(),
            name: String::new(),
            ..Default::default()
        };
        let epic = StoreOffer {
            store: "25".into(),
            name: String::new(),
            ..Default::default()
        };
        let named = StoreOffer {
            store: "7".into(),
            name: "GOG".into(),
            ..Default::default()
        };
        assert_eq!(steam.display_name(), "Steam");
        assert_eq!(epic.display_name(), "Epic");
        assert_eq!(named.display_name(), "GOG");

        let low = StoreLowest {
            final_cents: 1999,
            at: "2026-11-19T20:55:23.0000000+00:00".into(),
        };
        assert_eq!(low.formatted_date().as_deref(), Some("Nov 2026"));
    }

    #[test]
    fn score_controller_languages_and_sale_formatting_match_react() {
        assert_eq!(
            score_text(Some("Very Positive"), Some(94), 1025),
            "Very Positive · 94% of 1,025"
        );
        assert_eq!(score_text(None, Some(90), 10), "");

        let g_full = StoreGame {
            controller: Some("full".into()),
            play_station: true,
            ..Default::default()
        };
        assert_eq!(controller_text(&g_full), "Full · Xbox and PlayStation");
        let g_ps = StoreGame {
            controller: None,
            play_station: true,
            ..Default::default()
        };
        assert_eq!(controller_text(&g_ps), "PlayStation");

        assert_eq!(
            languages_text(&["English".into(), "French".into()]),
            "English, French"
        );
        assert_eq!(
            languages_text(&["A".into(), "B".into(), "C".into(), "D".into()]),
            "4 languages"
        );

        let ev = StoreSaleEvent {
            app_id: "1091500".into(),
            name: "Cyberpunk 2077".into(),
            final_cents: Some(2999),
            discount: 50,
            currency: Some("USD".into()),
            price: Some("$29.99".into()),
        };
        assert_eq!(
            format_sale_notice(&ev),
            "Cyberpunk 2077 is on sale on Steam: $29.99 (-50%)."
        );
    }

    #[test]
    fn wishlist_optimistic_toggle_and_hero_rotation_work() {
        let mut s = AppState::with_demo_games();
        s.switch_room(Room::Store);
        let tile = StoreTile {
            app_id: "1145350".into(),
            name: "Hades II".into(),
            discount: 20,
            ..Default::default()
        };
        assert!(!s.store_is_wished("1145350", false));
        s.store_toggle_wishlist(tile.clone(), true);
        assert!(s.store_is_wished("1145350", false));
        s.store_toggle_wishlist(tile, false);
        assert!(!s.store_is_wished("1145350", false));

        s.settings.reduce_motion = false;
        s.store_hero_index = 0;
        s.store_ext.hero_timer_start = 1.0;
        s.store_ext.hero_elapsed = 0.0;
        s.store_tick(5.0);
        assert_eq!(s.store_hero_index, 0);
        s.store_ext.hero_hovered = true;
        s.store_tick(15.0);
        assert_eq!(s.store_hero_index, 0);
        s.store_ext.hero_hovered = false;
        s.store_tick(15.0);
        s.store_tick(20.0);
        assert_eq!(s.store_hero_index, 1);
    }

    #[test]
    fn store_game_cache_respects_ttl_and_lru_cap() {
        let mut s = AppState::with_demo_games();
        for i in 0..(KEEP_MAX_ENTRIES + 4) {
            let g = StoreGame {
                ok: true,
                app_id: format!("{i}"),
                name: format!("Game {i}"),
                ..Default::default()
            };
            s.store_keep_cached_game(g, 100.0);
        }
        assert_eq!(s.store_ext.game_cache.len(), KEEP_MAX_ENTRIES);
        assert!(s.store_peek_cached_game("0", 120.0).is_none());
        assert!(s.store_peek_cached_game("99", 120.0).is_some());
        assert!(
            s.store_peek_cached_game("99", 100.0 + GAME_KEEP_SECS + 1.0)
                .is_none()
        );
    }

    #[test]
    fn store_warm_dwell_enqueues_visible_cards_once_and_respects_hover() {
        let mut s = AppState::with_demo_games();
        s.switch_room(Room::Store);
        assert!(s.store_ext.room_visible_since.is_some());
        let start = s.store_ext.room_visible_since.unwrap();
        assert!(s.store_ext.warmed_ids.is_empty());

        s.store_tick(start + 0.2);
        assert!(s.store_ext.warmed_ids.is_empty());

        s.store_tick(start + WARM_DWELL_SECS + 0.05);
        assert!(!s.store_ext.warmed_ids.is_empty());
        assert!(s.store_ext.warmed_ids.len() <= VISIBLE_WARM_COUNT);
        assert!(s.store_ext.room_visible_since.is_none());

        s.store_hover_tile(Some("999001".into()));
        let due = s.store_ext.hover_warm.as_ref().unwrap().1;
        s.store_tick(due + 0.01);
        assert!(s.store_ext.warmed_ids.contains("999001"));
        assert!(s.store_peek_cached_game("999001", due + 1.0).is_some());
    }
}
