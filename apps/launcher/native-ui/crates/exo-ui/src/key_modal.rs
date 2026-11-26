use crate::events::EventRegistry;
use crate::settings::set_btn;
use crate::state::{AppState, NoticeTone};
use exo_kit::{
    FG, PRESS_SCALE, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, SPRING_BASE, SPRING_QUICK, TRANSPARENT,
    bad, fg_faint, fg_muted, glass_rim_bottom, glass_rim_top, glass_still_control_fill, good,
    hover_fill, modal_card_transition, pressed_fill, primary_button_dyn, scrim_fill, store_logo,
    subtle_fill, type_body, type_control, type_section, type_small,
};
use exo_rpc::{ActionResult, KeyPreview, METHOD_KEYS_ACTIVATE, METHOD_KEYS_PREVIEW};
use rune::{Cursor, El, Key, Transition, Tx, col, edit_access, el, row, text, text_input};
use serde_json::json;

const KEY_STORES: [(&str, &str); 3] = [("steam", "Steam"), ("epic", "Epic"), ("gog", "GOG")];

#[derive(Debug, Clone)]
pub struct KeyModalState {
    pub store: String,
    pub edited: bool,
    pub note: Option<String>,
    pub copied_until: Option<f64>,
}

impl Default for KeyModalState {
    fn default() -> Self {
        Self {
            store: "steam".into(),
            edited: false,
            note: None,
            copied_until: None,
        }
    }
}

pub fn store_label_for_key(store: &str) -> &'static str {
    match store {
        "epic" => "Epic",
        "gog" => "GOG",
        _ => "Steam",
    }
}

pub fn register_events(reg: &mut EventRegistry) {
    reg.on_raw("keys.found", |state, data| {
        let ok = data.get("ok").and_then(|v| v.as_bool()).unwrap_or(false);
        let game_id = data
            .get("gameId")
            .and_then(|v| v.as_str())
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string());
        let message = data
            .get("message")
            .and_then(|v| v.as_str())
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string());
        if ok && let Some(gid) = game_id {
            state.close_key_modal();
            state.push_notice(
                message.unwrap_or_else(|| "Key activated.".into()),
                NoticeTone::Good,
            );
            state.refresh_library();
            state.open_game_page(&gid);
        } else {
            state.push_notice(
                message.unwrap_or_else(|| "No new game was confirmed.".into()),
                NoticeTone::Bad,
            );
        }
        true
    });
}

impl AppState {
    pub fn reset_key_modal_fields(&mut self) {
        self.key_modal.store = "steam".into();
        self.key_modal.edited = false;
        self.key_modal.note = None;
        self.key_modal.copied_until = None;
    }

    pub fn preview_clipboard_key(&mut self) {
        #[cfg(windows)]
        self.call_host_async(
            METHOD_KEYS_PREVIEW,
            json!({}),
            |s, res: Result<KeyPreview, String>| {
                if !s.key_modal_open || s.key_modal.edited {
                    return;
                }
                if let Ok(preview) = res {
                    if preview.looks_like_key
                        && let Some(ref key) = preview.key
                    {
                        s.key_input.text = key.clone();
                        s.key_input.sync_invariants();
                        if let Some(ref st) = preview.store
                            && matches!(st.as_str(), "steam" | "epic" | "gog")
                        {
                            s.key_modal.store = st.clone();
                        }
                    }
                    s.key_preview = Some(preview);
                }
            },
        );
    }

    pub fn copy_key_to_clipboard(&mut self, tx: &mut Tx) {
        let trimmed = self.key_input.text.trim().to_string();
        if trimmed.is_empty() {
            return;
        }
        tx.copy(trimmed);
        self.key_modal.note = None;
        let until = tx.clock() + 1.8;
        self.key_modal.copied_until = Some(until);
        tx.wake_at(until);
    }

    pub fn activate_key_with_store(&mut self, tx: &mut Tx) {
        let trimmed = self.key_input.text.trim().to_string();
        if trimmed.is_empty() || self.key_submitting {
            return;
        }
        let store = self.key_modal.store.clone();
        if store == "epic" {
            tx.copy(trimmed.clone());
        }
        self.key_submitting = true;
        self.key_modal.note = None;
        #[cfg(windows)]
        {
            let store_name = store_label_for_key(&store);
            self.call_host_async(
                METHOD_KEYS_ACTIVATE,
                json!({ "key": trimmed, "store": store }),
                move |s, res: Result<ActionResult, String>| {
                    s.key_submitting = false;
                    match res {
                        Ok(reply) if reply.ok => {
                            let msg = reply
                                .message
                                .filter(|m| !m.trim().is_empty())
                                .unwrap_or_else(|| format!("Finish in {store_name}."));
                            s.push_notice(msg, NoticeTone::Good);
                            s.close_key_modal();
                        }
                        Ok(reply) => {
                            s.key_modal.note = Some(
                                reply
                                    .message
                                    .filter(|m| !m.trim().is_empty())
                                    .unwrap_or_else(|| {
                                        "Exo couldn’t open the redeem page. Try again.".into()
                                    }),
                            );
                        }
                        Err(e) => {
                            s.key_modal.note = Some(if e.trim().is_empty() {
                                "Exo couldn’t open the redeem page. Try again.".into()
                            } else {
                                e
                            });
                        }
                    }
                },
            );
        }
        #[cfg(not(windows))]
        {
            self.key_submitting = false;
            self.push_notice(
                format!("Finish in {}.", store_label_for_key(&store)),
                NoticeTone::Good,
            );
            self.close_key_modal();
        }
    }
}

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
        .label("Product key")
        .w_full()
        .single_line()
        .color(FG)
        .placeholder("Product key", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.key_input))
        .on_input_change(|s: &mut AppState, _| {
            s.key_modal.edited = true;
        })
        .on_input_key(|s: &mut AppState, ev, _| {
            if ev.key == Key::Escape {
                s.close_key_modal();
                return true;
            }
            false
        })
        .on_submit(|s: &mut AppState, tx| s.activate_key_with_store(tx));

    let input_box = row()
        .w_full()
        .h(32.0)
        .px(12.0)
        .center_y()
        .cursor(Cursor::Text)
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(input);

    let mut chips = row().gap(6.0).center_y();
    for (id, label) in KEY_STORES {
        let active = s.key_modal.store == id;
        let disabled = s.key_submitting;
        let mut chip = el()
            .key(format!("key-store-{id}"))
            .role("button")
            .label(label)
            .checked(active)
            .focusable(!disabled)
            .disabled(disabled)
            .hit()
            .cursor(if disabled {
                Cursor::NotAllowed
            } else {
                Cursor::Pointer
            })
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(30.0)
            .px(12.0)
            .radius(RADIUS_PILL)
            .center()
            .fill(if active { FG } else { TRANSPARENT })
            .hover(if active { FG } else { hover_fill() })
            .press(if active { FG } else { pressed_fill() })
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .motion(SPRING_QUICK)
            .child(
                text(label)
                    .font(type_section())
                    .color(if active { PRIMARY_FG } else { FG }),
            );
        if !disabled {
            chip = chip.on_press(move |s: &mut AppState| {
                s.key_modal.edited = true;
                s.key_modal.store = id.to_string();
            });
        }
        chips = chips.child(chip);
    }

    let mut content = col()
        .w_full()
        .padding(20.0)
        .gap(14.0)
        .child(text("Activate key").font(type_section()).color(FG))
        .child(input_box)
        .child(chips);

    if let Some((store_id, note)) = preview_note {
        let mut badge = row().gap(8.0).center_y();
        if let Some(logo) = store_id.and_then(|id| store_logo(id, 16.0)) {
            badge = badge.child(logo);
        } else {
            badge = badge.child(el().size(7.0, 7.0).radius(3.5).fill(good()));
        }
        content = content.child(badge.child(text(note).font(type_small()).color(FG)));
    }

    if let Some(ref err_note) = s.key_modal.note {
        content = content.child(
            text(err_note.clone())
                .font(type_small())
                .color(bad())
                .w_full()
                .wrap(),
        );
    }

    let trimmed_nonempty = !s.key_input.text.trim().is_empty();
    let can_submit = trimmed_nonempty && !s.key_submitting;
    let copied_active = s
        .key_modal
        .copied_until
        .is_some_and(|until| s.clock_secs() < until);
    let mut copy_btn = set_btn(
        "key-copy",
        if copied_active { "Copied" } else { "Copy key" },
        None,
    )
    .disabled(!can_submit)
    .cursor(if can_submit {
        Cursor::Pointer
    } else {
        Cursor::Default
    })
    .opacity(if can_submit { 1.0 } else { 0.4 });
    if can_submit {
        copy_btn = copy_btn.on_press_tx(|s: &mut AppState, tx| s.copy_key_to_clipboard(tx));
    }

    let open_label = if s.key_submitting {
        "Opening…".to_string()
    } else {
        format!("Open {}", store_label_for_key(&s.key_modal.store))
    };
    let submit_btn = if can_submit {
        primary_button_dyn("key-submit", open_label, None)
            .on_press_tx(|s: &mut AppState, tx| s.activate_key_with_store(tx))
    } else {
        row()
            .key("key-submit")
            .role("button")
            .label(open_label.clone())
            .disabled(true)
            .hit()
            .cursor(Cursor::NotAllowed)
            .h(32.0)
            .px(14.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(subtle_fill())
            .child(text(open_label).font(type_control()).color(fg_faint()))
    };

    let footer = row()
        .w_full()
        .justify_end()
        .gap(8.0)
        .child(copy_btn)
        .child(
            set_btn("key-cancel", "Cancel", None).on_press(|s: &mut AppState| s.close_key_modal()),
        )
        .child(submit_btn);

    content = content.child(footer);

    let _ = blur;
    let card = col()
        .key("activate-key-modal")
        .w(420.0)
        .role("dialog")
        .label("Activate key")
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .hit()
        .cursor(Cursor::Default)
        .liquid_glass(exo_kit::exo_glass(exo_kit::ExoGlass::Panel, false))
        .motion(SPRING_BASE)
        .transition(modal_card_transition())
        .modal(true, |s: &mut AppState| s.close_key_modal())
        .modal_scrim(scrim_fill())
        .child(content);

    let host = col()
        .key("key-modal-host")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .transition(Transition::none())
        .child(card);

    vec![host]
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::HostEvent;

    #[test]
    fn keys_found_event_closes_modal_opens_game_and_notifies() {
        let mut s = AppState::default();
        s.key_modal_open = true;
        let first_id = s.games[0].id.clone();
        assert!(s.apply_host_event(HostEvent::Raw {
            event: "keys.found".into(),
            data: json!({
                "ok": true,
                "gameId": first_id,
                "message": "Added to library"
            }),
        }));
        assert!(!s.key_modal_open);
        assert_eq!(s.open_game_id.as_deref(), Some(first_id.as_str()));
        assert_eq!(
            s.notices.back().map(|n| n.text.as_str()),
            Some("Added to library")
        );
    }

    #[test]
    fn keys_found_failure_pushes_bad_notice() {
        let mut s = AppState::default();
        assert!(s.apply_host_event(HostEvent::Raw {
            event: "keys.found".into(),
            data: json!({ "ok": false }),
        }));
        let n = s.notices.back().expect("failure notice");
        assert_eq!(n.tone, NoticeTone::Bad);
        assert_eq!(n.text, "No new game was confirmed.");
    }
}
