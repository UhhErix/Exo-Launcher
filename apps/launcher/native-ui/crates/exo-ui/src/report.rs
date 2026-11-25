use crate::settings::{set_btn, set_primary_btn};
use crate::settings_state::{
    MAX_REPORT_CONTACT, MAX_REPORT_MESSAGE, ReportPhase, bug_report_url, build_report_payload,
};
use crate::state::AppState;
use exo_kit::{
    BG, FG, RADIUS_SMALL, SPRING_BASE, bad, fg_muted, glass_panel_opt, glass_rim_bottom,
    glass_rim_top, glass_still_control_fill, good, hairline, icon_check, modal_card_transition,
    scaled, switch_pill, type_body, type_small, type_title,
};
use rune::{
    Cursor, El, Key, ScrollSpec, TextStyle, Transition, col, edit_access, el, row, text, text_input,
};

pub fn layers(s: &AppState, blur: bool) -> Vec<El> {
    let sent = matches!(s.wp4.report_phase, ReportPhase::Sent(_));
    let sending = matches!(s.wp4.report_phase, ReportPhase::Sending);
    let version = s.settings.app_version.as_deref().or_else(|| {
        s.pc.system_about
            .as_ref()
            .and_then(|a| a.version.as_deref())
    });

    let payload_ready = build_report_payload(
        &s.wp4.report_message.text,
        s.wp4.report_attach_logs,
        &s.wp4.report_contact.text,
        s.wp4.report_excerpt.as_deref(),
    )
    .is_some();
    let can_send = payload_ready
        && !sent
        && !sending
        && (!s.wp4.report_attach_logs || s.wp4.report_excerpt.is_some());

    let msg_input = text_input(&s.wp4.report_message, type_body())
        .key("report-message-input")
        .label("What happened?")
        .w_full()
        .min_h(scaled(112.0))
        .color(FG)
        .placeholder(
            "What you did, what you expected and what happened instead",
            fg_muted(),
        )
        .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.report_message))
        .on_input_change(|s: &mut AppState, _| {
            if s.wp4.report_message.text.chars().count() > MAX_REPORT_MESSAGE {
                s.wp4.report_message.text = s
                    .wp4
                    .report_message
                    .text
                    .chars()
                    .take(MAX_REPORT_MESSAGE)
                    .collect();
                s.wp4.report_message.sync_invariants();
            }
        })
        .on_input_key(|s: &mut AppState, ev, _| {
            if ev.key == Key::Escape {
                s.wp4_close_report();
                return true;
            }
            false
        });

    let msg_box = col()
        .w_full()
        .px(12.0)
        .py(10.0)
        .cursor(Cursor::Text)
        .radius(RADIUS_SMALL)
        .fill(glass_still_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(msg_input);

    let attach = s.wp4.report_attach_logs;
    let showing = s.wp4.report_showing_excerpt;
    let toggle_link = el()
        .key("report-toggle-preview")
        .role("button")
        .label(if showing {
            "Hide what’s sent"
        } else {
            "Show what’s sent"
        })
        .focusable(attach)
        .disabled(!attach)
        .hit()
        .cursor(if attach {
            Cursor::Pointer
        } else {
            Cursor::NotAllowed
        })
        .opacity(if attach { 1.0 } else { 0.4 })
        .on_press(|s: &mut AppState| {
            if s.wp4.report_attach_logs {
                s.wp4.report_showing_excerpt = !s.wp4.report_showing_excerpt;
            }
        })
        .child(
            text(if showing {
                "Hide what’s sent"
            } else {
                "Show what’s sent"
            })
            .font(type_small())
            .color(fg_muted()),
        );

    let attach_row = row()
        .w_full()
        .justify_between()
        .center_y()
        .gap(12.0)
        .child(
            row()
                .gap(12.0)
                .center_y()
                .child(
                    text("Attach logs")
                        .font(TextStyle::new(scaled(13.0), 500.0))
                        .color(FG),
                )
                .child(toggle_link),
        )
        .child(
            switch_pill("report-attach-logs", "Attach logs", attach).on_press(
                move |s: &mut AppState| {
                    if matches!(s.wp4.report_phase, ReportPhase::Sent(_)) {
                        return;
                    }
                    s.wp4.report_attach_logs = !attach;
                    if !s.wp4.report_attach_logs {
                        s.wp4.report_showing_excerpt = false;
                    }
                },
            ),
        );

    let contact_input = text_input(&s.wp4.report_contact, type_body())
        .key("report-contact-input")
        .label("Contact (optional)")
        .w_full()
        .single_line()
        .color(FG)
        .placeholder("An email or a name, if you’d like a reply", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.report_contact))
        .on_input_change(|s: &mut AppState, _| {
            if s.wp4.report_contact.text.chars().count() > MAX_REPORT_CONTACT {
                s.wp4.report_contact.text = s
                    .wp4
                    .report_contact
                    .text
                    .chars()
                    .take(MAX_REPORT_CONTACT)
                    .collect();
                s.wp4.report_contact.sync_invariants();
            }
        });

    let contact_box = row()
        .w_full()
        .h(38.0)
        .px(12.0)
        .center_y()
        .cursor(Cursor::Text)
        .radius(RADIUS_SMALL)
        .fill(glass_still_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(contact_input);

    let mut content = col()
        .w_full()
        .px(22.0)
        .py(20.0)
        .gap(14.0)
        .child(text("Report a problem").font(type_title()).color(FG))
        .child(
            col()
                .w_full()
                .gap(6.0)
                .child(
                    text("What happened?")
                        .font(TextStyle::new(scaled(13.0), 500.0))
                        .color(FG),
                )
                .child(msg_box),
        )
        .child(attach_row);

    if showing && attach {
        let preview_text = match s.wp4.report_excerpt.as_deref() {
            None => "Reading the log…",
            Some("") => "Nothing in Exo’s log to attach.",
            Some(ex) => ex,
        };
        content = content.child(
            col()
                .key("report-log-preview")
                .w_full()
                .max_h(160.0)
                .padding(10.0)
                .radius(RADIUS_SMALL)
                .fill(BG)
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(
                    text(preview_text.to_string())
                        .font(TextStyle::new(scaled(12.0), 400.0))
                        .color(fg_muted())
                        .wrap(),
                ),
        );
    }

    content = content
        .child(
            col()
                .w_full()
                .gap(6.0)
                .child(
                    text("Contact (optional)")
                        .font(TextStyle::new(scaled(13.0), 500.0))
                        .color(FG),
                )
                .child(contact_box),
        )
        .child(
            text(
                "Send goes privately to Exo’s team. Open on GitHub posts in public, without logs.",
            )
            .font(type_small())
            .color(fg_muted())
            .wrap(),
        );

    if let Some(ref err) = s.wp4.report_error {
        content = content.child(text(err.clone()).font(type_small()).color(bad()).wrap());
    }

    let gh_url = bug_report_url(version, &s.wp4.report_message.text);
    let github_btn = set_btn("report-github", "Open on GitHub", None)
        .on_press(move |s: &mut AppState| s.wp4_open_url(&gh_url));

    let cancel_btn = set_btn("report-cancel", if sent { "Done" } else { "Cancel" }, None)
        .on_press(|s: &mut AppState| s.wp4_close_report());

    let send_btn = match &s.wp4.report_phase {
        ReportPhase::Sent(words) => {
            set_primary_btn("report-send", words, Some(icon_check(14.0, good()))).disabled(true)
        }
        ReportPhase::Sending => set_primary_btn("report-send", "Sending…", None)
            .disabled(true)
            .opacity(0.6),
        ReportPhase::Editing => {
            let mut btn = set_primary_btn("report-send", "Send", None)
                .disabled(!can_send)
                .cursor(if can_send {
                    Cursor::Pointer
                } else {
                    Cursor::NotAllowed
                })
                .opacity(if can_send { 1.0 } else { 0.45 });
            if can_send {
                btn = btn.on_press(|s: &mut AppState| s.wp4_send_report());
            }
            btn
        }
    };

    let footer = row()
        .w_full()
        .px(22.0)
        .py(14.0)
        .justify_between()
        .center_y()
        .gap(8.0)
        .child(github_btn)
        .child(row().gap(8.0).center_y().child(cancel_btn).child(send_btn));

    let card = glass_panel_opt("report-problem-modal", 560.0, blur)
        .motion(SPRING_BASE)
        .transition(modal_card_transition())
        .modal(true, |s: &mut AppState| s.wp4_close_report())
        .modal_scrim(BG.alpha(0.56))
        .child(content)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(footer);

    let host = col()
        .key("report-modal-host")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .transition(Transition::none())
        .child(card);

    vec![host]
}
