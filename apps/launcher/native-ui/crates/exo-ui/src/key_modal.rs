use crate::state::AppState;
use exo_kit::{
    BG, FG, GLASS_CONTROL_SIGMA, RADIUS_SMALL, SPRING_BASE, fg_muted, glass_button,
    glass_control_fill, glass_panel_opt, glass_rim_bottom, glass_rim_top, good, hairline, icon_key,
    modal_card_transition, modal_scrim_transition, primary_button, store_logo, type_body,
    type_small, type_title,
};
use rune::{Cursor, El, Key, Transition, col, edit_access, el, row, text, text_input};

pub fn layers(s: &AppState, blur: bool) -> Vec<El> {
    let preview_note = if let Some(ref preview) = s.key_preview {
        if let Some(ref store) = preview.store {
            Some((
                Some(store.as_str()),
                format!("Detected {} key", exo_rpc::types::store_label(store)),
            ))
        } else if preview.looks_like_key {
            Some((None, "Valid key format detected".into()))
        } else {
            None
        }
    } else {
        None
    };

    let input = text_input(&s.key_input, type_body())
        .key("activate-key-input")
        .label("Game product key")
        .w_full()
        .single_line()
        .color(FG)
        .placeholder("XXXXX-XXXXX-XXXXX", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.key_input))
        .on_input_key(|s: &mut AppState, ev, _| {
            if ev.key == Key::Escape {
                s.close_key_modal();
                return true;
            }
            false
        })
        .on_submit(|s: &mut AppState, _| s.submit_key_activation());

    let input_box = row()
        .w_full()
        .h(42.0)
        .px(12.0)
        .gap(10.0)
        .center_y()
        .cursor(Cursor::Text)
        .radius(RADIUS_SMALL)
        .fill(glass_control_fill())
        .backdrop_blur(GLASS_CONTROL_SIGMA)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(icon_key(15.0, fg_muted()))
        .child(input);

    let mut content = col()
        .w_full()
        .px(20.0)
        .py(18.0)
        .gap(12.0)
        .child(text("Activate a key").font(type_title()).color(FG))
        .child(
            text("Paste a Steam, Epic, GOG, Xbox, or EA key to redeem it directly.")
                .font(type_small())
                .color(fg_muted())
                .w_full()
                .wrap(),
        )
        .child(input_box);

    if let Some((store_id, note)) = preview_note {
        let mut badge = row().gap(8.0).center_y();
        if let Some(logo) = store_id.and_then(|id| store_logo(id, 16.0)) {
            badge = badge.child(logo);
        } else {
            badge = badge.child(el().size(7.0, 7.0).radius(3.5).fill(good()));
        }
        content = content.child(badge.child(text(note).font(type_small()).color(FG)));
    }

    let can_submit = !s.key_input.text.trim().is_empty() && !s.key_submitting;
    let mut submit_btn = primary_button(
        "key-submit",
        if s.key_submitting {
            "Activating…"
        } else {
            "Activate"
        },
        None,
    )
    .disabled(!can_submit)
    .cursor(if can_submit {
        Cursor::Pointer
    } else {
        Cursor::NotAllowed
    })
    .opacity(if can_submit { 1.0 } else { 0.45 });
    if can_submit {
        submit_btn = submit_btn.on_press(|s: &mut AppState| s.submit_key_activation());
    }

    let footer = row()
        .w_full()
        .px(20.0)
        .py(14.0)
        .justify_end()
        .gap(8.0)
        .child(
            glass_button("key-cancel", "Cancel", None)
                .on_press(|s: &mut AppState| s.close_key_modal()),
        )
        .child(submit_btn);

    let card = glass_panel_opt("activate-key-modal", 460.0, blur)
        .motion(SPRING_BASE)
        .transition(modal_card_transition())
        .child(content)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(footer);

    let scrim = el()
        .key("key-modal-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG.alpha(0.52))
        .hit()
        .cursor(Cursor::Default)
        .transition(modal_scrim_transition())
        .on_press(|s: &mut AppState| s.close_key_modal());

    let host = col()
        .key("key-modal-host")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .transition(Transition::none())
        .child(card);

    vec![scrim, host]
}
