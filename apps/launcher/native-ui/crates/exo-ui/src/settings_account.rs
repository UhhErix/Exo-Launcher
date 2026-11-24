use crate::settings::{set_btn, set_danger_btn, set_primary_btn, settings_card};
use crate::settings_state::{
    AccountScreen, AccountSecurityForm, AccountSignedOutMode, RECOVERY_CODE_LENGTH,
    format_unix_timestamp, held_sentences, normalize_recovery_code, recovery_code_confirmed,
};
use crate::state::AppState;
use exo_kit::{
    BG, FG, RADIUS_SMALL, SPRING_BASE, bad, exo_mark, fg_muted, glass_panel_opt, glass_rim_bottom,
    glass_rim_top, glass_still_control_fill, hairline, modal_card_transition, scaled, type_body,
    type_section, type_small, type_title,
};
use rune::{
    Cursor, El, PasswordInput, TextStyle, Transition, col, edit_access, el, row, text, text_input,
};

fn field_box(input: El) -> El {
    row()
        .w_full()
        .h(38.0)
        .px(12.0)
        .center_y()
        .cursor(Cursor::Text)
        .radius(RADIUS_SMALL)
        .fill(glass_still_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(input)
}

fn link_btn(
    key: &'static str,
    label: &'static str,
    on_press: impl Fn(&mut AppState) + 'static,
) -> El {
    el().key(key)
        .role("button")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .py(4.0)
        .px(4.0)
        .on_press(on_press)
        .child(text(label).font(type_small()).color(fg_muted()))
}

fn sec_head(title: &str) -> El {
    row().w_full().min_h(28.0).px(2.0).center_y().child(
        text(title.to_string())
            .key(format!("set-sec-head-{title}"))
            .font(type_section())
            .color(FG),
    )
}

fn set_row(
    key: &str,
    title: &str,
    detail: Option<&str>,
    error: Option<&str>,
    highlight: bool,
    control: El,
) -> El {
    let mut left = col().grow(1.0).min_w(0.0).gap(3.0).child(
        text(title.to_string())
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

pub fn recovery_code_view(s: &AppState, code: &str, for_settings: bool) -> El {
    let typed = &s.wp4.account_recovery_typed.text;
    let norm_len = normalize_recovery_code(typed).len();
    let confirmed = recovery_code_confirmed(typed, code);
    let finishing = s.wp4.account_recovery_finishing;
    let code_copy = code.to_string();
    let code_save = code.to_string();

    let mut groups = row().w_full().gap(8.0).flex_wrap();
    for (idx, group) in code.split('-').enumerate() {
        groups = groups.child(
            el().key(format!("recovery-grp-{idx}"))
                .px(10.0)
                .py(6.0)
                .radius(RADIUS_SMALL)
                .fill(BG)
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(
                    text(group.to_string())
                        .font(TextStyle::new(scaled(14.0), 600.0))
                        .color(FG),
                ),
        );
    }

    let copy_btn =
        set_btn("recovery-copy", "Copy", None).on_press_tx(move |s: &mut AppState, tx| {
            tx.copy(code_copy.clone());
            s.wp4.account_recovery_note = Some("Copied.".into());
        });
    let save_btn = set_btn("recovery-save", "Save as text file", None)
        .on_press(move |s: &mut AppState| s.wp4_save_recovery_code_file(code_save.clone()));

    let confirm_input = field_box(
        text_input(&s.wp4.account_recovery_typed, type_body())
            .key("recovery-confirm-input")
            .label("Type the code to confirm")
            .w_full()
            .single_line()
            .color(FG)
            .placeholder("Type the recovery code", fg_muted())
            .on_edit(edit_access(|s: &mut AppState| {
                &mut s.wp4.account_recovery_typed
            }))
            .on_submit(move |s: &mut AppState, _| s.wp4_confirm_recovery_code(for_settings)),
    );

    let can_finish = confirmed && !finishing;
    let mut finish_btn = set_primary_btn(
        "recovery-continue",
        if s.wp4.account_recovery_failed {
            "Try again"
        } else {
            "Continue"
        },
        None,
    )
    .disabled(!can_finish)
    .opacity(if can_finish { 1.0 } else { 0.45 });
    if can_finish {
        finish_btn =
            finish_btn.on_press(move |s: &mut AppState| s.wp4_confirm_recovery_code(for_settings));
    }

    let mut out = col()
        .w_full()
        .gap(12.0)
        .child(
            text("Save your recovery code")
                .font(type_section())
                .color(FG),
        )
        .child(
            text("This is the only way back in if you forget your password. Exo doesn’t keep it, and you won’t see it again.")
                .font(type_small())
                .color(fg_muted())
                .wrap(),
        )
        .child(groups)
        .child(row().gap(8.0).center_y().child(copy_btn).child(save_btn));

    if let Some(ref note) = s.wp4.account_recovery_note {
        out = out.child(text(note.clone()).font(type_small()).color(fg_muted()));
    }

    out = out
        .child(
            text("Type the code to confirm")
                .font(TextStyle::new(scaled(13.0), 500.0))
                .color(FG),
        )
        .child(confirm_input)
        .child(
            text(format!(
                "{norm_len}/{RECOVERY_CODE_LENGTH} characters. Dashes and spaces don’t matter."
            ))
            .font(type_small())
            .color(fg_muted()),
        );

    if s.wp4.account_recovery_failed {
        out = out.child(
            text("That didn’t go through. Try again.")
                .font(type_small())
                .color(bad()),
        );
    }

    out.child(row().w_full().justify_end().child(finish_btn))
}

pub fn account_section(s: &AppState) -> El {
    if let Some(ref code) = s.wp4.account_recovery_code {
        return col().w_full().gap(8.0).child(sec_head("Account")).child(
            settings_card()
                .padding(18.0)
                .child(recovery_code_view(s, code, true)),
        );
    }

    let Some(ref overview) = s.wp4.account_overview else {
        return signed_out_section(s);
    };

    if !overview.signed_in {
        return signed_out_section(s);
    }

    let hi = |id: &str| s.wp4.highlight_row.is_some_and(|(rid, _)| rid == id);
    let email_label = overview.email.as_deref().unwrap_or("Exo account");

    let sign_out_ctrl = if s.wp4.account_sign_out_confirm {
        row()
            .gap(8.0)
            .center_y()
            .child(
                set_btn("set-acct-keep", "Keep me signed in", None)
                    .on_press(|s: &mut AppState| s.wp4.account_sign_out_confirm = false),
            )
            .child(
                set_primary_btn("set-acct-signout-go", "Sign out", None)
                    .on_press(|s: &mut AppState| s.wp4_account_sign_out()),
            )
    } else {
        set_btn("set-acct-signout", "Sign out", None)
            .on_press(|s: &mut AppState| s.wp4.account_sign_out_confirm = true)
    };

    let mut acct_card = settings_card().child(set_row(
        "about-account-email",
        email_label,
        None,
        None,
        false,
        sign_out_ctrl,
    ));

    let sync_problem = if overview.sync.signed_out {
        Some("Your session ended. Sign in again to keep syncing.")
    } else if overview.sync.push_stopped {
        Some("Your sync storage is full. Your data stays on this PC.")
    } else {
        None
    };

    if let Some(prob) = sync_problem {
        acct_card = acct_card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(set_row(
                "about-account-sync",
                "Sync",
                Some(prob),
                s.wp4.account_sync_error.as_deref(),
                hi("about-account-sync"),
                set_btn("set-acct-sync-now", "Sync now", None)
                    .on_press(|s: &mut AppState| s.wp4_account_sync_now()),
            ));
    }

    if !overview.sync.held.is_empty() {
        let held_detail = held_sentences(&overview.sync.held).join(" ");
        acct_card = acct_card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(set_row(
                "about-account-held",
                &format!("Held on this PC · {}", overview.sync.held.len()),
                Some(&held_detail),
                None,
                false,
                el(),
            ));
    }

    let sec_form = s.wp4.account_security_form;
    let mut sec_card = settings_card().child(set_row(
        "about-account-password",
        "Change password",
        None,
        None,
        hi("about-account-password"),
        set_btn(
            "set-acct-pass-toggle",
            if sec_form == AccountSecurityForm::Password {
                "Cancel"
            } else {
                "Change"
            },
            None,
        )
        .on_press(move |s: &mut AppState| {
            s.wp4.account_security_error = None;
            s.wp4.account_security_form = if sec_form == AccountSecurityForm::Password {
                AccountSecurityForm::None
            } else {
                AccountSecurityForm::Password
            };
        }),
    ));

    if sec_form == AccountSecurityForm::Password {
        let can_submit = !s.wp4.account_current_pass.text.is_empty()
            && !s.wp4.account_new_pass.text.is_empty()
            && !s.wp4.account_security_busy;
        let mut submit_btn = set_primary_btn("set-acct-pass-submit", "Change password", None)
            .disabled(!can_submit)
            .opacity(if can_submit { 1.0 } else { 0.45 });
        if can_submit {
            submit_btn = submit_btn.on_press(|s: &mut AppState| s.wp4_account_change_password());
        }
        let mut form = col()
            .w_full()
            .px(18.0)
            .pb(16.0)
            .gap(10.0)
            .child(
                text("Current password")
                    .font(type_small())
                    .color(fg_muted()),
            )
            .child(field_box(
                PasswordInput::new(&s.wp4.account_current_pass, type_body())
                    .key("set-acct-cur-pass")
                    .label("Current password")
                    .color(FG)
                    .disabled(s.wp4.account_security_busy)
                    .on_edit(|s: &mut AppState| &mut s.wp4.account_current_pass)
                    .build()
                    .w_full(),
            ))
            .child(text("New password").font(type_small()).color(fg_muted()))
            .child(field_box(
                PasswordInput::new(&s.wp4.account_new_pass, type_body())
                    .key("set-acct-new-pass")
                    .label("New password")
                    .color(FG)
                    .placeholder("At least 12 characters", fg_muted())
                    .disabled(s.wp4.account_security_busy)
                    .on_edit(|s: &mut AppState| &mut s.wp4.account_new_pass)
                    .on_submit(|s: &mut AppState, _| s.wp4_account_change_password())
                    .build()
                    .w_full(),
            ));
        if let Some(ref err) = s.wp4.account_security_error {
            form = form.child(text(err.clone()).font(type_small()).color(bad()).wrap());
        }
        sec_card = sec_card.child(form.child(row().justify_end().child(submit_btn)));
    }

    if let Some(ref pk) = s.wp4.account_passkeys
        && pk.supported
    {
        let pk_detail = if pk.passkeys.is_empty() {
            "Optional Windows Hello or security key sign-in. Password and recovery code always stay active.".to_string()
        } else {
            format!(
                "{} passkey{} registered. Password and recovery code always stay active.",
                pk.passkeys.len(),
                if pk.passkeys.len() == 1 { "" } else { "s" }
            )
        };
        let add_btn = set_btn(
            "set-acct-passkey-add",
            if s.wp4.account_passkey_reauth_needed {
                "Confirm & add"
            } else {
                "Add passkey"
            },
            None,
        )
        .disabled(s.wp4.account_passkey_busy)
        .on_press(|s: &mut AppState| s.wp4_account_passkey_register());
        sec_card = sec_card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(set_row(
                "about-account-passkeys",
                "Passkeys",
                Some(s.wp4.account_passkey_note.as_deref().unwrap_or(&pk_detail)),
                s.wp4.account_passkey_error.as_deref(),
                hi("about-account-passkeys"),
                add_btn,
            ));
        if s.wp4.account_passkey_reauth_needed {
            sec_card = sec_card.child(
                col()
                    .w_full()
                    .px(18.0)
                    .pb(12.0)
                    .gap(6.0)
                    .child(
                        text("Confirm account password")
                            .font(type_small())
                            .color(fg_muted()),
                    )
                    .child(field_box(
                        PasswordInput::new(&s.wp4.account_passkey_reauth_pw, type_body())
                            .key("set-acct-passkey-reauth-pw")
                            .label("Account password")
                            .color(FG)
                            .disabled(s.wp4.account_passkey_busy)
                            .on_edit(|s: &mut AppState| &mut s.wp4.account_passkey_reauth_pw)
                            .on_submit(|s: &mut AppState, _| s.wp4_account_passkey_register())
                            .build()
                            .w_full(),
                    )),
            );
        }
        for item in &pk.passkeys {
            let pid = item.id.clone();
            let used = if item.last_used > 0 {
                format!("Last used {}", format_unix_timestamp(item.last_used))
            } else {
                "Added".to_string()
            };
            let warn = if item.flagged {
                Some("Unusual sign-in counter detected — consider replacing this passkey.")
            } else {
                None
            };
            sec_card = sec_card
                .child(el().w_full().h(1.0).fill(hairline()))
                .child(set_row(
                    &format!("about-account-passkey-{}", item.id),
                    if item.label.is_empty() {
                        "Windows passkey"
                    } else {
                        &item.label
                    },
                    Some(&used),
                    warn,
                    false,
                    set_btn("set-acct-passkey-remove", "Remove", None)
                        .disabled(s.wp4.account_passkey_busy)
                        .on_press(move |s: &mut AppState| {
                            s.wp4_account_passkey_delete(pid.clone())
                        }),
                ));
        }
    }

    sec_card = sec_card
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "about-account-recovery",
            "Replace recovery code",
            None,
            None,
            hi("about-account-recovery"),
            set_btn(
                "set-acct-rec-toggle",
                if sec_form == AccountSecurityForm::Recovery {
                    "Cancel"
                } else {
                    "Replace"
                },
                None,
            )
            .on_press(move |s: &mut AppState| {
                s.wp4.account_security_error = None;
                s.wp4.account_security_form = if sec_form == AccountSecurityForm::Recovery {
                    AccountSecurityForm::None
                } else {
                    AccountSecurityForm::Recovery
                };
            }),
        ));

    if sec_form == AccountSecurityForm::Recovery {
        let can_submit = !s.wp4.account_code_pass.text.is_empty() && !s.wp4.account_security_busy;
        let mut submit_btn = set_primary_btn("set-acct-rec-submit", "Make a new code", None)
            .disabled(!can_submit)
            .opacity(if can_submit { 1.0 } else { 0.45 });
        if can_submit {
            submit_btn = submit_btn.on_press(|s: &mut AppState| s.wp4_account_new_recovery_code());
        }
        let mut form = col()
            .w_full()
            .px(18.0)
            .pb(16.0)
            .gap(10.0)
            .child(text("Password").font(type_small()).color(fg_muted()))
            .child(field_box(
                PasswordInput::new(&s.wp4.account_code_pass, type_body())
                    .key("set-acct-code-pass")
                    .label("Password")
                    .color(FG)
                    .disabled(s.wp4.account_security_busy)
                    .on_edit(|s: &mut AppState| &mut s.wp4.account_code_pass)
                    .on_submit(|s: &mut AppState, _| s.wp4_account_new_recovery_code())
                    .build()
                    .w_full(),
            ));
        if let Some(ref err) = s.wp4.account_security_error {
            form = form.child(text(err.clone()).font(type_small()).color(bad()).wrap());
        }
        sec_card = sec_card.child(form.child(row().justify_end().child(submit_btn)));
    }

    if let Some(ref note) = s.wp4.account_security_note {
        sec_card = sec_card.child(el().w_full().h(1.0).fill(hairline())).child(
            el().w_full().px(18.0).py(10.0).child(
                text(note.clone())
                    .font(type_small())
                    .color(fg_muted())
                    .wrap(),
            ),
        );
    }

    let sessions_open = s.wp4.account_sessions_open;
    let mut sessions_card = settings_card().child(set_row(
        "about-account-sessions",
        "Signed-in PCs",
        None,
        None,
        hi("about-account-sessions"),
        set_btn(
            "set-acct-sessions-toggle",
            if sessions_open { "Hide" } else { "Show" },
            None,
        )
        .on_press(|s: &mut AppState| {
            s.wp4.account_sessions_open = !s.wp4.account_sessions_open;
            s.wp4.account_sessions_note = None;
            if s.wp4.account_sessions_open {
                s.wp4_load_account_sessions();
            }
        }),
    ));

    if sessions_open {
        let body = match s.wp4.account_sessions.as_ref() {
            None => el()
                .w_full()
                .px(18.0)
                .pb(14.0)
                .child(text("Loading…").font(type_small()).color(fg_muted())),
            Some(list) if list.is_empty() => el().w_full().px(18.0).pb(14.0).child(
                text("No PCs are signed in.")
                    .font(type_small())
                    .color(fg_muted()),
            ),
            Some(list) => {
                let mut rows = col().w_full().px(18.0).pb(14.0).gap(8.0);
                for sess in list {
                    let dev = sess.device.clone();
                    let label = if sess.this_pc {
                        "This PC"
                    } else {
                        "Another PC"
                    };
                    let used = format!("Last used {}", format_unix_timestamp(sess.last_used));
                    let mut r = row().w_full().justify_between().center_y().gap(12.0).child(
                        row()
                            .gap(12.0)
                            .center_y()
                            .child(text(label).font(type_body()).color(FG))
                            .child(text(used).font(type_small()).color(fg_muted())),
                    );
                    if !sess.this_pc {
                        r = r.child(
                            set_btn(format!("set-acct-rev-{dev}"), "Remove", None).on_press(
                                move |s: &mut AppState| s.wp4_revoke_account_session(dev.clone()),
                            ),
                        );
                    }
                    rows = rows.child(r);
                }
                rows
            }
        };
        sessions_card = sessions_card.child(body);
    }

    if let Some(ref note) = s.wp4.account_sessions_note {
        sessions_card = sessions_card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(
                el().w_full().px(18.0).py(10.0).child(
                    text(note.clone())
                        .font(type_small())
                        .color(fg_muted())
                        .wrap(),
                ),
            );
    }

    let del_open = s.wp4.account_delete_open;
    let mut data_card = settings_card()
        .child(set_row(
            "about-account-export",
            "Export my data",
            s.wp4.account_export_note.as_deref(),
            None,
            hi("about-account-export"),
            set_btn("set-acct-export", "Save a copy", None)
                .on_press(|s: &mut AppState| s.wp4_export_account_data()),
        ))
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(set_row(
            "about-account-delete",
            "Delete account",
            None,
            None,
            hi("about-account-delete"),
            set_danger_btn(
                "set-acct-delete-toggle",
                if del_open { "Cancel" } else { "Delete" },
                None,
            )
            .on_press(|s: &mut AppState| {
                s.wp4.account_delete_open = !s.wp4.account_delete_open;
                s.wp4.account_delete_error = None;
            }),
        ));

    if del_open {
        let can_del = !s.wp4.account_delete_pass.text.is_empty() && !s.wp4.account_delete_busy;
        let mut del_btn = set_danger_btn("set-acct-delete-go", "Delete my account", None)
            .disabled(!can_del)
            .opacity(if can_del { 1.0 } else { 0.45 });
        if can_del {
            del_btn = del_btn.on_press(|s: &mut AppState| s.wp4_delete_account());
        }
        let mut form = col()
            .w_full()
            .px(18.0)
            .pb(16.0)
            .gap(10.0)
            .child(text("Password").font(type_small()).color(fg_muted()))
            .child(field_box(
                PasswordInput::new(&s.wp4.account_delete_pass, type_body())
                    .key("set-acct-del-pass")
                    .label("Password")
                    .color(FG)
                    .disabled(s.wp4.account_delete_busy)
                    .on_edit(|s: &mut AppState| &mut s.wp4.account_delete_pass)
                    .on_submit(|s: &mut AppState, _| s.wp4_delete_account())
                    .build()
                    .w_full(),
            ))
            .child(
                text("This deletes the account and every item synced to it. Your games, playtime and settings stay on this PC.")
                    .font(type_small())
                    .color(fg_muted())
                    .wrap(),
            );
        if let Some(ref err) = s.wp4.account_delete_error {
            form = form.child(text(err.clone()).font(type_small()).color(bad()).wrap());
        }
        data_card = data_card.child(form.child(row().justify_end().child(del_btn)));
    }

    col()
        .w_full()
        .gap(24.0)
        .child(
            col()
                .w_full()
                .gap(8.0)
                .child(sec_head("Account"))
                .child(acct_card),
        )
        .child(
            col()
                .w_full()
                .gap(8.0)
                .child(sec_head("Security"))
                .child(sec_card),
        )
        .child(
            col()
                .w_full()
                .gap(8.0)
                .child(sec_head("Sessions"))
                .child(sessions_card),
        )
        .child(
            col()
                .w_full()
                .gap(8.0)
                .child(sec_head("Your data"))
                .child(data_card),
        )
}

fn signed_out_section(s: &AppState) -> El {
    let mode = s.wp4.account_signed_out_mode;
    let heading = match mode {
        AccountSignedOutMode::Create => "Create your Exo account",
        AccountSignedOutMode::Reset => "Reset your password",
        AccountSignedOutMode::SignIn => "Sign in to Exo",
    };
    let submit_label = match mode {
        AccountSignedOutMode::Create => "Create account",
        AccountSignedOutMode::Reset => "Reset password",
        AccountSignedOutMode::SignIn => "Sign in",
    };
    let can_submit = !s.wp4.account_email.text.trim().is_empty()
        && !s.wp4.account_password.text.is_empty()
        && (mode != AccountSignedOutMode::Reset
            || !s.wp4.account_reset_code.text.trim().is_empty())
        && !s.wp4.account_form_busy;

    let mut form = col()
        .w_full()
        .padding(18.0)
        .gap(10.0)
        .child(
            text(heading)
                .font(TextStyle::new(scaled(14.0), 600.0))
                .color(FG),
        )
        .child(text("Email").font(type_small()).color(fg_muted()))
        .child(field_box(
            text_input(&s.wp4.account_email, type_body())
                .key("set-acct-email")
                .label("Email")
                .w_full()
                .single_line()
                .color(FG)
                .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.account_email)),
        ));

    if mode == AccountSignedOutMode::Reset {
        form = form
            .child(text("Recovery code").font(type_small()).color(fg_muted()))
            .child(field_box(
                text_input(&s.wp4.account_reset_code, type_body())
                    .key("set-acct-reset-code")
                    .label("Recovery code")
                    .w_full()
                    .single_line()
                    .color(FG)
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.wp4.account_reset_code
                    })),
            ));
    }

    let pass_placeholder = if mode == AccountSignedOutMode::SignIn {
        ""
    } else {
        "At least 12 characters"
    };
    let mut pass_input = PasswordInput::new(&s.wp4.account_password, type_body())
        .key("set-acct-pass")
        .label("Password")
        .color(FG)
        .placeholder(pass_placeholder, fg_muted())
        .disabled(s.wp4.account_form_busy)
        .on_edit(|s: &mut AppState| &mut s.wp4.account_password)
        .on_submit(|s: &mut AppState, _| s.wp4_account_signed_out_submit());
    if mode == AccountSignedOutMode::SignIn {
        pass_input = pass_input
            .revealed(s.wp4.account_show_pass)
            .on_reveal_toggle(|s: &mut AppState, next, _| {
                s.wp4.account_show_pass = next;
                s.wp4.account_password.set_revealed(next);
            })
            .reveal_button(|revealed| {
                text(if revealed { "Hide" } else { "Show" })
                    .font(type_small())
                    .color(fg_muted())
            });
    }
    form = form
        .child(text("Password").font(type_small()).color(fg_muted()))
        .child(field_box(pass_input.build().w_full()));

    if let Some(ref err) = s.wp4.account_form_error {
        form = form.child(text(err.clone()).font(type_small()).color(bad()).wrap());
    }
    if s.wp4.account_form_offline {
        form = form.child(
            text("Exo works without an account for now.")
                .font(type_small())
                .color(fg_muted()),
        );
    }

    let mut submit_btn = set_primary_btn("set-acct-submit", submit_label, None)
        .disabled(!can_submit)
        .opacity(if can_submit { 1.0 } else { 0.45 });
    if can_submit {
        submit_btn = submit_btn.on_press(|s: &mut AppState| s.wp4_account_signed_out_submit());
    }

    let mut actions = row()
        .w_full()
        .pt(4.0)
        .gap(12.0)
        .center_y()
        .child(submit_btn)
        .child(link_btn(
            "set-acct-toggle-mode",
            if mode == AccountSignedOutMode::Create {
                "I already have one"
            } else {
                "Create an account"
            },
            move |s: &mut AppState| {
                s.wp4.account_form_error = None;
                s.wp4.account_signed_out_mode = if mode == AccountSignedOutMode::Create {
                    AccountSignedOutMode::SignIn
                } else {
                    AccountSignedOutMode::Create
                };
            },
        ));

    if mode == AccountSignedOutMode::SignIn {
        if s.wp4
            .account_passkeys
            .as_ref()
            .is_some_and(|pk| pk.supported)
        {
            actions = actions.child(
                set_btn("set-acct-passkey-signin", "Sign in with passkey", None)
                    .disabled(s.wp4.account_form_busy)
                    .on_press(|s: &mut AppState| s.wp4_account_passkey_sign_in()),
            );
        }
        actions = actions.child(link_btn(
            "set-acct-forgot",
            "Forgot your password?",
            |s: &mut AppState| {
                s.wp4.account_form_error = None;
                s.wp4.account_signed_out_mode = AccountSignedOutMode::Reset;
            },
        ));
    }

    col()
        .w_full()
        .gap(8.0)
        .child(sec_head("Account"))
        .child(settings_card().child(form.child(actions)))
}

pub fn account_step_view(s: &AppState) -> El {
    let screen = s.wp4.flow.screen;
    let busy = s.wp4.flow.busy;
    let now = s.wp4_now_secs();
    let locked_secs = s
        .wp4
        .flow_locked_until
        .map(|until| (until - now).max(0.0).ceil() as u64)
        .unwrap_or(0);
    let locked = locked_secs > 0;

    if screen == AccountScreen::Code
        && let Some(ref code) = s.wp4.flow_code
    {
        return recovery_code_view(s, code, false);
    }

    match screen {
        AccountScreen::SignedIn => col()
            .w_full()
            .gap(10.0)
            .child(text("Signed in to Exo").font(type_section()).color(FG))
            .child(
                text(
                    s.wp4
                        .account_overview
                        .as_ref()
                        .and_then(|o| o.email.clone())
                        .unwrap_or_else(|| "Your Exo account is active on this PC.".into()),
                )
                .font(type_body())
                .color(fg_muted()),
            ),
        AccountScreen::Choose => {
            let ended = s
                .wp4
                .account_overview
                .as_ref()
                .is_some_and(|o| o.session_ended);
            col()
                .w_full()
                .gap(16.0)
                .child(
                    text(if ended {
                        "Your session ended on this PC. Sign in again to keep syncing."
                    } else {
                        "Exo syncs your library, settings and playtime across PCs."
                    })
                    .font(type_body())
                    .color(fg_muted())
                    .wrap(),
                )
                .child(
                    row()
                        .w_full()
                        .gap(12.0)
                        .child(
                            set_btn("flow-choose-create", "Create an account", None)
                                .grow(1.0)
                                .h(52.0)
                                .justify_center()
                                .on_press(|s: &mut AppState| {
                                    s.wp4_flow_open(AccountScreen::Create)
                                }),
                        )
                        .child(
                            set_btn("flow-choose-signin", "Sign in", None)
                                .grow(1.0)
                                .h(52.0)
                                .justify_center()
                                .on_press(|s: &mut AppState| {
                                    s.wp4_flow_open(AccountScreen::SignIn)
                                }),
                        ),
                )
        }
        AccountScreen::Offline => {
            let back_to =
                if s.wp4.flow_offline_from == AccountScreen::Create && s.wp4.flow_taken_sign_in {
                    AccountScreen::SignIn
                } else {
                    s.wp4.flow_offline_from
                };
            let mut actions = row().w_full().gap(10.0).justify_end().center_y().child(
                set_btn("flow-offline-later", "Continue without an account", None)
                    .on_press(|s: &mut AppState| s.wp4_continue_without_account()),
            );
            if s.wp4.flow_offline_from == AccountScreen::NewCode {
                actions = actions.child(
                    set_btn("flow-offline-signout", "Sign out of this PC", None)
                        .on_press(|s: &mut AppState| s.wp4_flow_sign_out()),
                );
            }
            actions = actions.child(
                set_primary_btn("flow-offline-retry", "Try again", None)
                    .on_press(move |s: &mut AppState| s.wp4_flow_open(back_to)),
            );
            col()
                .w_full()
                .gap(16.0)
                .child(
                    text("Can’t reach Exo’s account service")
                        .font(type_section())
                        .color(FG),
                )
                .child(actions)
        }
        AccountScreen::Create => {
            let show = s.wp4.flow_show_pass;
            let mut out = col()
                .w_full()
                .gap(10.0)
                .child(text("Create your account").font(type_section()).color(FG))
                .child(text("Email").font(type_small()).color(fg_muted()))
                .child(field_box(
                    text_input(&s.wp4.flow_email, type_body())
                        .key("flow-create-email")
                        .label("Email")
                        .w_full()
                        .single_line()
                        .color(FG)
                        .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.flow_email)),
                ));
            if let Some(ref err) = s.wp4.flow_email_error {
                let mut err_row = row()
                    .gap(8.0)
                    .center_y()
                    .child(text(err.clone()).font(type_small()).color(bad()).wrap());
                if s.wp4.flow_taken_sign_in {
                    err_row = err_row.child(link_btn(
                        "flow-taken-signin",
                        "Sign in instead",
                        |s: &mut AppState| s.wp4_flow_open(AccountScreen::SignIn),
                    ));
                }
                out = out.child(err_row);
            }
            out = out
                .child(text("Password").font(type_small()).color(fg_muted()))
                .child(field_box(
                    PasswordInput::new(&s.wp4.flow_password, type_body())
                        .key("flow-create-pass")
                        .label("Password")
                        .color(FG)
                        .revealed(show)
                        .disabled(busy)
                        .on_edit(|s: &mut AppState| &mut s.wp4.flow_password)
                        .on_submit(|s: &mut AppState, _| s.wp4_flow_submit())
                        .on_reveal_toggle(|s: &mut AppState, next, _| {
                            s.wp4.flow_show_pass = next;
                            s.wp4.flow_password.set_revealed(next);
                        })
                        .reveal_button(|revealed| {
                            text(if revealed { "Hide" } else { "Show" })
                                .font(type_small())
                                .color(fg_muted())
                        })
                        .build()
                        .w_full(),
                ));
            if let Some(ref err) = s.wp4.flow_password_error {
                out = out.child(text(err.clone()).font(type_small()).color(bad()).wrap());
            } else {
                out = out.child(
                    text("At least 12 characters")
                        .font(type_small())
                        .color(fg_muted()),
                );
            }
            if let Some(ref err) = s.wp4.flow_form_error {
                out = out.child(text(err.clone()).font(type_small()).color(bad()).wrap());
            }
            let can = !busy && !locked;
            let mut submit = set_primary_btn(
                "flow-create-submit",
                if busy {
                    "Creating your account"
                } else {
                    "Create account"
                },
                None,
            )
            .disabled(!can)
            .opacity(if can { 1.0 } else { 0.45 });
            if can {
                submit = submit.on_press(|s: &mut AppState| s.wp4_flow_submit());
            }
            out.child(
                row()
                    .w_full()
                    .pt(6.0)
                    .justify_between()
                    .child(
                        set_btn("flow-create-back", "Back", None)
                            .on_press(|s: &mut AppState| s.wp4_flow_open(AccountScreen::Choose)),
                    )
                    .child(submit),
            )
        }
        AccountScreen::SignIn => {
            let mut out = col()
                .w_full()
                .gap(10.0)
                .child(text("Sign in to Exo").font(type_section()).color(FG))
                .child(text("Email").font(type_small()).color(fg_muted()))
                .child(field_box(
                    text_input(&s.wp4.flow_email, type_body())
                        .key("flow-signin-email")
                        .label("Email")
                        .w_full()
                        .single_line()
                        .color(FG)
                        .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.flow_email)),
                ))
                .child(text("Password").font(type_small()).color(fg_muted()))
                .child(field_box(
                    PasswordInput::new(&s.wp4.flow_password, type_body())
                        .key("flow-signin-pass")
                        .label("Password")
                        .color(FG)
                        .disabled(busy)
                        .on_edit(|s: &mut AppState| &mut s.wp4.flow_password)
                        .on_submit(|s: &mut AppState, _| s.wp4_flow_submit())
                        .build()
                        .w_full(),
                ));
            if let Some(ref err) = s.wp4.flow_form_error {
                out = out.child(text(err.clone()).font(type_small()).color(bad()).wrap());
            }
            if locked {
                out = out.child(
                    text(format!(
                        "Too many tries for this email from this PC. Wait {}:{:02} and try again.",
                        locked_secs / 60,
                        locked_secs % 60
                    ))
                    .font(type_small())
                    .color(bad())
                    .wrap(),
                );
            }
            let can = !busy && !locked;
            let mut submit = set_primary_btn("flow-signin-submit", "Sign in", None)
                .disabled(!can)
                .opacity(if can { 1.0 } else { 0.45 });
            if can {
                submit = submit.on_press(|s: &mut AppState| s.wp4_flow_submit());
            }
            out.child(link_btn(
                "flow-signin-forgot",
                "Forgot your password?",
                |s: &mut AppState| s.wp4_flow_open(AccountScreen::Reset),
            ))
            .child(
                row()
                    .w_full()
                    .pt(6.0)
                    .justify_between()
                    .child(
                        set_btn("flow-signin-back", "Back", None)
                            .on_press(|s: &mut AppState| s.wp4_flow_open(AccountScreen::Choose)),
                    )
                    .child(submit),
            )
        }
        AccountScreen::Reset => {
            let back_to = s.wp4.flow_reset_from;
            let mut out = col()
                .w_full()
                .gap(10.0)
                .child(text("Reset your password").font(type_section()).color(FG))
                .child(text("Email").font(type_small()).color(fg_muted()))
                .child(field_box(
                    text_input(&s.wp4.flow_email, type_body())
                        .key("flow-reset-email")
                        .label("Email")
                        .w_full()
                        .single_line()
                        .color(FG)
                        .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.flow_email)),
                ))
                .child(text("Recovery code").font(type_small()).color(fg_muted()))
                .child(field_box(
                    text_input(&s.wp4.flow_reset_code, type_body())
                        .key("flow-reset-code")
                        .label("Recovery code")
                        .w_full()
                        .single_line()
                        .color(FG)
                        .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.flow_reset_code)),
                ))
                .child(text("New password").font(type_small()).color(fg_muted()))
                .child(field_box(
                    PasswordInput::new(&s.wp4.flow_password, type_body())
                        .key("flow-reset-pass")
                        .label("New password")
                        .color(FG)
                        .disabled(busy)
                        .on_edit(|s: &mut AppState| &mut s.wp4.flow_password)
                        .on_submit(|s: &mut AppState, _| s.wp4_flow_submit())
                        .build()
                        .w_full(),
                ))
                .child(
                    text("At least 12 characters. Every PC signed in to this account will sign in again.")
                        .font(type_small())
                        .color(fg_muted())
                        .wrap(),
                );
            if let Some(err) = s
                .wp4
                .flow_password_error
                .as_ref()
                .or(s.wp4.flow_form_error.as_ref())
            {
                out = out.child(text(err.clone()).font(type_small()).color(bad()).wrap());
            }
            let can = !busy
                && !s.wp4.flow_reset_code.text.trim().is_empty()
                && !s.wp4.flow_password.text.is_empty();
            let mut submit = set_primary_btn("flow-reset-submit", "Reset password", None)
                .disabled(!can)
                .opacity(if can { 1.0 } else { 0.45 });
            if can {
                submit = submit.on_press(|s: &mut AppState| s.wp4_flow_submit());
            }
            out.child(
                row()
                    .w_full()
                    .pt(6.0)
                    .justify_between()
                    .child(
                        set_btn("flow-reset-back", "Back", None)
                            .on_press(move |s: &mut AppState| s.wp4_flow_open(back_to)),
                    )
                    .child(submit),
            )
        }
        AccountScreen::NewCode | AccountScreen::Code => {
            let mut out = col()
                .w_full()
                .gap(10.0)
                .child(
                    text("Make a new recovery code")
                        .font(type_section())
                        .color(FG),
                )
                .child(
                    text("Your last recovery code wasn’t confirmed. Enter your password to make a new one.")
                        .font(type_small())
                        .color(fg_muted())
                        .wrap(),
                )
                .child(text("Password").font(type_small()).color(fg_muted()))
                .child(field_box(
                    PasswordInput::new(&s.wp4.flow_password, type_body())
                        .key("flow-newcode-pass")
                        .label("Password")
                        .color(FG)
                        .disabled(busy)
                        .on_edit(|s: &mut AppState| &mut s.wp4.flow_password)
                        .on_submit(|s: &mut AppState, _| s.wp4_flow_submit())
                        .build()
                        .w_full(),
                ));
            if let Some(ref err) = s.wp4.flow_form_error
                && !locked
            {
                out = out.child(text(err.clone()).font(type_small()).color(bad()).wrap());
            }
            if locked {
                out = out.child(
                    text(format!(
                        "Too many tries from this PC. Wait {}:{:02} and try again.",
                        locked_secs / 60,
                        locked_secs % 60
                    ))
                    .font(type_small())
                    .color(bad())
                    .wrap(),
                );
            }
            let can = !busy && !locked && !s.wp4.flow_password.text.is_empty();
            let mut submit = set_primary_btn(
                "flow-newcode-submit",
                if busy {
                    "Making a new code"
                } else {
                    "Make a new code"
                },
                None,
            )
            .disabled(!can)
            .opacity(if can { 1.0 } else { 0.45 });
            if can {
                submit = submit.on_press(|s: &mut AppState| s.wp4_flow_submit());
            }
            out.child(link_btn(
                "flow-newcode-forgot",
                "Forgot your password?",
                |s: &mut AppState| s.wp4_flow_open(AccountScreen::Reset),
            ))
            .child(
                row()
                    .w_full()
                    .pt(6.0)
                    .justify_between()
                    .child(
                        set_btn("flow-newcode-signout", "Sign out of this PC", None)
                            .disabled(busy)
                            .on_press(|s: &mut AppState| s.wp4_flow_sign_out()),
                    )
                    .child(submit),
            )
        }
    }
}

pub fn account_sheet_layers(s: &AppState, blur: bool) -> Vec<El> {
    let card = glass_panel_opt("exo-account-sheet", 480.0, blur)
        .padding(28.0)
        .gap(16.0)
        .motion(SPRING_BASE)
        .transition(modal_card_transition())
        .modal_trap()
        .modal_scrim(BG.alpha(0.76))
        .child(row().w_full().justify_center().child(exo_mark(48.0, FG)))
        .child(
            row()
                .w_full()
                .justify_center()
                .child(text("Your Exo account").font(type_title()).color(FG)),
        )
        .child(account_step_view(s));

    let host = col()
        .key("account-sheet-host")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .transition(Transition::none())
        .child(card);

    vec![host]
}
