use crate::state::AppState;
use exo_kit::{
    BG, FG, RADIUS_SMALL, SPRING_BASE, fg_muted, glass_button, glass_panel, glass_rim, good,
    hairline, icon_key, primary_button, type_body, type_small, type_title,
};
use rune::{El, Key, col, edit_access, el, row, text, text_input};

pub fn view(s: &AppState) -> El {
    let preview_note = if let Some(ref preview) = s.key_preview {
        if let Some(ref store) = preview.store {
            Some(format!("Detected {} key", exo_rpc::types::store_label(store)))
        } else if preview.looks_like_key {
            Some("Valid key format detected".into())
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
        .radius(RADIUS_SMALL)
        .fill(BG.alpha(0.55))
        .border(1.0, glass_rim())
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

    if let Some(note) = preview_note {
        content = content.child(
            row()
                .gap(6.0)
                .center_y()
                .child(el().size(7.0, 7.0).radius(3.5).fill(good()))
                .child(text(note).font(type_small()).color(FG)),
        );
    }

    let can_submit = !s.key_input.text.trim().is_empty() && !s.key_submitting;
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
        .child(
            primary_button(
                "key-submit",
                if s.key_submitting { "Activating…" } else { "Activate" },
                None,
            )
            .opacity(if can_submit { 1.0 } else { 0.45 })
            .on_press(|s: &mut AppState| s.submit_key_activation()),
        );

    let card = glass_panel("activate-key-modal", 460.0)
        .motion(SPRING_BASE)
        .child(content)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(footer);

    el().key("key-modal-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG.alpha(0.52))
        .hit()
        .on_press(|s: &mut AppState| s.close_key_modal())
        .child(col().w_full().h_full().center().child(card))
}
