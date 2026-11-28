use crate::image_cache::CacheRoom;
use crate::library_state::gift_key;
use crate::state::AppState;
use exo_kit::{
    BG, ExoGlass, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    SPRING_QUICK, apply_glass, bad, fg_muted, glass_control_fill, glass_rim, good, hover_fill,
    icon_gift, popover_transition_for, pressed_fill, surface_card, type_control, type_section,
    type_small,
};
use exo_rpc::StoreGift;
use rune::{Align, Cursor, El, ScrollSpec, Side, SurfaceFit, Tx, col, el, row, text};

pub fn gifts_pill(s: &AppState) -> Option<El> {
    let shown = s.visible_gifts();
    if shown.is_empty() {
        return None;
    }
    let count = shown.len();
    let label = format!("{count} {}", if count == 1 { "Gift" } else { "Gifts" });
    Some(apply_glass(
        row()
            .key("home-gifts-pill")
            .role("button")
            .label(label.clone())
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .h(34.0)
            .px(12.0)
            .gap(7.0)
            .center_y()
            .radius(RADIUS_PILL)
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press(|s: &mut AppState| {
                s.lib.gifts_open = !s.lib.gifts_open;
                s.library_more_open = false;
                s.library_add_open = false;
            })
            .child(el().size(6.0, 6.0).radius(3.0).fill(good()))
            .child(icon_gift(14.0, FG))
            .child(text(label).font(type_small()).color(FG)),
        ExoGlass::Tinted(good().alpha(0.32)),
    ))
}

pub fn gifts_popover_overlay(s: &AppState, scale: f32) -> Vec<El> {
    if !s.lib.gifts_open {
        return Vec::new();
    }
    let shown = s.visible_gifts();
    if shown.is_empty() {
        return Vec::new();
    }

    let rows: Vec<El> = shown
        .into_iter()
        .map(|gift| gift_card_row(gift, s, scale))
        .collect();

    let panel = apply_glass(
        col()
            .key("gifts-popover-panel")
            .w(560.0)
            .radius(RADIUS_PANEL)
            .continuous_corners()
            .clip_rounded(RADIUS_PANEL)
            .hit()
            .cursor(Cursor::Default),
        ExoGlass::Popover,
    )
    .max_h(520.0)
    .transition(popover_transition_for(Side::Top, Align::End))
    .focus_group()
    .focus_return("home-gifts-pill")
    .anchored_fit("home-gifts-pill", Side::Top, Align::End, 8.0)
    .px(12.0)
    .pt(10.0)
    .pb(12.0)
    .gap(8.0)
    .child(text("Gifts waiting").font(type_small()).color(fg_muted()))
    .child(
        col()
            .w_full()
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .gap(8.0)
            .children(rows),
    );

    vec![
        el().key("gifts-popover-root")
            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
            .modal_tx(true, |s: &mut AppState, _: &mut Tx| {
                s.lib.gifts_open = false
            })
            .child(panel),
    ]
}

fn gift_card_row(gift: &StoreGift, s: &AppState, scale: f32) -> El {
    let key = gift_key(gift);
    let busy = s.lib.gifts_busy.contains(&key);
    let err = s.lib.gifts_errors.get(&key);
    let decline_confirm_key = format!("gift-decline-{key}");
    let decline_armed = s.is_confirm_armed(&decline_confirm_key);

    let art = if let Some(url) = gift.image.as_deref()
        && let Some(img) = s.remote_image_in_room(url, 224.0, scale, CacheRoom::Library)
    {
        el().size(112.0, 63.0)
            .radius(RADIUS_CARD)
            .clip_rounded(RADIUS_CARD)
            .pixels_fit(img.pixels(), SurfaceFit::Fill)
    } else {
        el().size(112.0, 63.0)
            .radius(RADIUS_CARD)
            .center()
            .fill(BG)
            .border(1.0, glass_rim())
            .child(icon_gift(24.0, fg_muted()))
    };

    let mut copy = col().grow(1.0).min_w(0.0).gap(2.0).child(
        text(gift.title.clone())
            .font(type_section())
            .color(FG)
            .truncate(),
    );
    if let Some(sender) = gift.sender.as_deref().filter(|v| !v.trim().is_empty()) {
        copy = copy.child(
            text(format!("From {sender}"))
                .font(type_small())
                .color(fg_muted())
                .truncate(),
        );
    }
    if let Some(msg) = gift.message.as_deref().filter(|v| !v.trim().is_empty()) {
        copy = copy.child(
            text(msg.to_string())
                .font(type_small())
                .color(fg_muted())
                .wrap(),
        );
    }
    if let Some(error_text) = err {
        copy = copy.child(
            text(error_text.clone())
                .font(type_small())
                .color(bad())
                .wrap(),
        );
    }

    let store_dec = gift.store.clone();
    let id_dec = gift.id.clone();
    let store_acc = gift.store.clone();
    let id_acc = gift.id.clone();
    let confirm_key_cb = decline_confirm_key.clone();

    let decline_label = if decline_armed {
        "Decline? They get a refund"
    } else {
        "Decline"
    };
    let decline_btn = row()
        .key(format!("gift-decline-{key}"))
        .role("button")
        .label(decline_label)
        .cursor(if busy {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .focusable(!busy)
        .disabled(busy)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(12.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if decline_armed {
            bad()
        } else {
            glass_control_fill()
        })
        .hover(if decline_armed {
            bad().alpha(0.9)
        } else {
            hover_fill()
        })
        .press(pressed_fill())
        .border(
            if decline_armed { 0.0 } else { 1.0 },
            if decline_armed { bad() } else { glass_rim() },
        )
        .motion(SPRING_QUICK)
        .on_press_tx(move |s: &mut AppState, tx| {
            if s.is_confirm_armed(&confirm_key_cb) {
                s.disarm_confirm();
                s.answer_gift(&store_dec, &id_dec, false);
            } else {
                s.arm_confirm_key(&confirm_key_cb, tx);
            }
        })
        .child(text(decline_label).font(type_control()).color(FG));

    let accept_label = if busy { "Accepting…" } else { "Accept" };
    let accept_btn = row()
        .key(format!("gift-accept-{key}"))
        .role("button")
        .label(accept_label)
        .cursor(if busy {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .focusable(!busy)
        .disabled(busy)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(12.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(FG)
        .hover(FG.alpha(0.92))
        .press(FG.alpha(0.84))
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| {
            s.answer_gift(&store_acc, &id_acc, true);
        })
        .child(text(accept_label).font(type_control()).color(PRIMARY_FG));

    row()
        .key(format!("gift-row-{key}"))
        .w_full()
        .min_h(76.0)
        .px(8.0)
        .py(8.0)
        .gap(12.0)
        .center_y()
        .radius(RADIUS_CARD)
        .fill(surface_card())
        .border(1.0, glass_rim())
        .child(art)
        .child(copy)
        .child(
            row()
                .gap(6.0)
                .center_y()
                .child(decline_btn)
                .child(accept_btn),
        )
}
