use crate::settings::{set_btn, set_danger_btn, settings_card};
use crate::settings_state::{BackupOp, backup_day_label, today_utc_ymd};
use crate::state::AppState;
use exo_kit::{
    BG, FG, RADIUS_SMALL, bad, fg_muted, glass_rim_bottom, glass_rim_top, hairline, scaled,
    type_body, type_section, type_small,
};
use rune::{Cursor, El, ScrollSpec, TextStyle, col, el, row, text};

fn row_box(
    key: &str,
    title: &str,
    detail: Option<&str>,
    error: Option<&str>,
    highlight: bool,
    control: El,
) -> El {
    let mut left = col().grow(1.0).min_w(0.0).gap(3.0).child(
        text(title.to_string())
            .key(format!("set-label-{title}"))
            .font(TextStyle::new(scaled(13.0), 500.0))
            .color(FG),
    );
    if let Some(sub) = detail {
        left = left.child(
            text(sub.to_string())
                .font(TextStyle::new(scaled(12.0), 400.0))
                .color(fg_muted())
                .wrap(),
        );
    }
    if let Some(err) = error {
        left = left.child(
            text(err.to_string())
                .font(TextStyle::new(scaled(12.0), 400.0))
                .color(bad())
                .wrap(),
        );
    }
    let mut wrap = el().key(key.to_string()).w_full().py(12.0).px(18.0);
    if highlight {
        wrap = wrap.fill(FG.alpha(0.06));
    }
    wrap.child(
        row()
            .w_full()
            .min_h(38.0)
            .gap(16.0)
            .justify_between()
            .center_y()
            .child(left)
            .child(control),
    )
}

fn sec_head(title: &str) -> El {
    row().w_full().min_h(28.0).px(2.0).center_y().child(
        text(title.to_string())
            .key(format!("set-sec-head-{title}"))
            .font(type_section())
            .color(FG),
    )
}

pub fn backup_section(s: &AppState) -> El {
    let today = today_utc_ymd();
    let last_label = match s.pc.backup.as_ref() {
        None => String::new(),
        Some(status) => match status.last.as_ref() {
            None => "None yet".into(),
            Some(day) => backup_day_label(&day.day, &today),
        },
    };
    let busy = s.wp4.backup_busy;
    let held = s.wp4.backup_held.as_deref();
    let locked = busy.is_some() || held.is_some();
    let failed = |kind: BackupOp| -> Option<&str> {
        s.wp4
            .backup_outcome
            .as_ref()
            .filter(|o| o.kind == kind && !o.ok)
            .map(|o| o.text.as_str())
    };
    let hi = |id: &str| s.wp4.highlight_row.is_some_and(|(rid, _)| rid == id);

    let now_disabled = held.is_some() || busy.is_some_and(|b| b != BackupOp::Now);
    let mut now_btn = set_btn(
        "set-backup-now",
        if busy == Some(BackupOp::Now) {
            "Backing up…"
        } else {
            "Back up now"
        },
        None,
    )
    .disabled(now_disabled)
    .opacity(if now_disabled { 0.45 } else { 1.0 });
    if !now_disabled {
        now_btn = now_btn.on_press(|s: &mut AppState| s.wp4_run_backup(BackupOp::Now));
    }

    let mut export_btn = set_btn(
        "set-backup-export",
        if busy == Some(BackupOp::Export) {
            "Exporting…"
        } else {
            "Export"
        },
        None,
    )
    .disabled(locked)
    .opacity(if locked { 0.45 } else { 1.0 });
    if !locked {
        export_btn = export_btn.on_press(|s: &mut AppState| s.wp4_run_backup(BackupOp::Export));
    }

    let import_ctrl = if s.wp4.backup_confirm_import {
        row()
            .gap(8.0)
            .center_y()
            .child(
                set_btn("set-backup-import-cancel", "Cancel", None)
                    .on_press(|s: &mut AppState| s.wp4.backup_confirm_import = false),
            )
            .child(
                set_danger_btn(
                    "set-backup-import-confirm",
                    "Replace what's here and restart?",
                    None,
                )
                .on_press(|s: &mut AppState| s.wp4_run_backup(BackupOp::Import)),
            )
    } else {
        let mut btn = set_btn(
            "set-backup-import",
            if busy == Some(BackupOp::Import) {
                "Importing…"
            } else {
                "Import"
            },
            None,
        )
        .disabled(locked)
        .opacity(if locked { 0.45 } else { 1.0 });
        if !locked {
            btn = btn.on_press(|s: &mut AppState| s.wp4.backup_confirm_import = true);
        }
        btn
    };

    let mut card = settings_card()
        .child(row_box(
            "general-last-backup",
            "Last backup",
            None,
            failed(BackupOp::Now),
            hi("general-last-backup"),
            row()
                .gap(10.0)
                .center_y()
                .child(text(last_label).font(type_small()).color(fg_muted()))
                .child(now_btn),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(row_box(
            "general-backup-file",
            "Backup file",
            None,
            failed(BackupOp::Export).or_else(|| failed(BackupOp::Import)),
            hi("general-backup-file"),
            row()
                .gap(8.0)
                .center_y()
                .child(export_btn)
                .child(import_ctrl),
        ));

    let note = s
        .wp4
        .backup_note
        .as_deref()
        .or_else(|| {
            s.pc.backup
                .as_ref()
                .and_then(|b| b.note.as_ref())
                .map(|n| n.sentence.as_str())
        })
        .filter(|n| !n.is_empty());

    if let Some(line) = note {
        card = card.child(el().w_full().h(1.0).fill(hairline())).child(
            el().w_full()
                .px(18.0)
                .py(10.0)
                .child(text(line.to_string()).font(type_small()).color(FG).wrap()),
        );
    }
    if let Some(line) = held {
        card = card.child(el().w_full().h(1.0).fill(hairline())).child(
            el().w_full()
                .px(18.0)
                .py(10.0)
                .child(text(line.to_string()).font(type_small()).color(FG).wrap()),
        );
    } else if let Some(out) = s.wp4.backup_outcome.as_ref().filter(|o| o.ok) {
        card = card.child(el().w_full().h(1.0).fill(hairline())).child(
            el().w_full().px(18.0).py(10.0).child(
                text(out.text.clone())
                    .font(type_small())
                    .color(fg_muted())
                    .wrap(),
            ),
        );
    }

    col()
        .w_full()
        .gap(8.0)
        .child(sec_head("Backups"))
        .child(card)
}

pub fn privacy_section(s: &AppState) -> El {
    let hi = |id: &str| s.wp4.highlight_row.is_some_and(|(rid, _)| rid == id);
    let viewing = s.wp4.privacy_viewing;

    let mut card = settings_card().child(row_box(
        "about-privacy-sent",
        "What's sent",
        None,
        None,
        hi("about-privacy-sent"),
        set_btn(
            "set-privacy-view",
            if viewing { "Hide" } else { "View" },
            None,
        )
        .on_press(|s: &mut AppState| s.wp4_toggle_privacy_sent()),
    ));

    if viewing {
        let body = if s.wp4.privacy_failed {
            text("What was sent could not be read.")
                .font(type_small())
                .color(fg_muted())
                .wrap()
        } else {
            match s.wp4.privacy_payloads.as_ref() {
                None => text("Reading what was sent…")
                    .font(type_small())
                    .color(fg_muted()),
                Some(list) if list.is_empty() => text("Nothing has been sent from this PC.")
                    .font(type_small())
                    .color(fg_muted()),
                Some(list) => {
                    let mut items = col().w_full().gap(8.0);
                    for (idx, payload) in list.iter().enumerate() {
                        items = items.child(
                            el().key(format!("privacy-payload-{idx}"))
                                .w_full()
                                .padding(10.0)
                                .radius(RADIUS_SMALL)
                                .fill(BG)
                                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                                .child(
                                    text(payload.clone())
                                        .font(TextStyle::new(scaled(12.0), 400.0))
                                        .color(fg_muted())
                                        .wrap(),
                                ),
                        );
                    }
                    col()
                        .key("privacy-payloads-scroll")
                        .w_full()
                        .max_h(240.0)
                        .scroll(ScrollSpec {
                            stick_bottom: false,
                        })
                        .child(items)
                }
            }
        };
        card = card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(el().w_full().px(18.0).py(12.0).child(body));
    }

    let delete_ctrl = if s.wp4.privacy_confirming {
        row()
            .gap(8.0)
            .center_y()
            .child(
                set_btn("set-privacy-cancel", "Cancel", None)
                    .on_press(|s: &mut AppState| s.wp4.privacy_confirming = false),
            )
            .child(
                set_danger_btn("set-privacy-delete-go", "Delete", None)
                    .on_press(|s: &mut AppState| s.wp4_delete_telemetry_data()),
            )
    } else {
        set_btn("set-privacy-delete", "Delete", None)
            .cursor(Cursor::Pointer)
            .on_press(|s: &mut AppState| s.wp4.privacy_confirming = true)
    };

    card = card
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(row_box(
            "about-privacy-delete",
            "Delete my data",
            None,
            None,
            hi("about-privacy-delete"),
            delete_ctrl,
        ));

    if let Some(ref msg) = s.wp4.privacy_message {
        card = card.child(el().w_full().h(1.0).fill(hairline())).child(
            el().w_full()
                .px(18.0)
                .py(10.0)
                .child(text(msg.clone()).font(type_body()).color(fg_muted()).wrap()),
        );
    }

    col()
        .w_full()
        .gap(8.0)
        .child(sec_head("Privacy"))
        .child(card)
}
