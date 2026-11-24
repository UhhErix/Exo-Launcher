use crate::settings::{set_btn, set_danger_btn, set_primary_btn};
use crate::settings_state::{
    RIOT_PRIVACY_URL, SteamSignInMode, StoreActionKind, account_name, available_store_rows,
    baseline_summary_parts, can_connect_store, can_open_store_client, is_valid_riot_id,
    parse_hours_input, settings_store_rows, signs_in_in_own_app, store_client_download_url,
    store_layout_note, store_state,
};
use crate::state::AppState;
use exo_kit::{
    BG, FG, RADIUS_CARD, RADIUS_SMALL, amoled_card, bad, fg_muted, glass_rim_bottom, glass_rim_top,
    glass_still_control_fill, good, hairline, hover_fill, icon_check, icon_chevron_down, scaled,
    store_logo_or_fallback, switch_pill, type_body, type_section, type_small,
};
use exo_rpc::{
    METHOD_ACCOUNTS_ADD, METHOD_ACCOUNTS_EPIC_ADD, METHOD_ACCOUNTS_EPIC_IMPORT,
    METHOD_ACCOUNTS_EPIC_NEW_LAUNCHER, METHOD_ACCOUNTS_REMOVE, METHOD_ACCOUNTS_SIGN_OUT,
    METHOD_ACCOUNTS_SWITCH, StoreAccounts, StoreStatus,
};
use rune::{
    Cursor, El, OtpInput, PasswordInput, QrCode, QrView, TextEdit, TextStyle, col, edit_access, el,
    row, text, text_input,
};
use serde_json::{Value, json};

fn sec_head_with_action(title: &str, action: Option<El>) -> El {
    let mut r = row()
        .w_full()
        .min_h(28.0)
        .px(2.0)
        .center_y()
        .justify_between()
        .child(
            text(title.to_string())
                .key(format!("set-sec-head-{title}"))
                .font(type_section())
                .color(FG),
        );
    if let Some(act) = action {
        r = r.child(act);
    }
    r
}

fn store_row_box(
    key: &str,
    icon: Option<El>,
    title: &str,
    detail: Option<String>,
    error: Option<String>,
    highlight: bool,
    control: El,
) -> El {
    let mut text_col = col().grow(1.0).min_w(0.0).gap(3.0).child(
        text(title.to_string())
            .font(TextStyle::new(scaled(13.0), 500.0))
            .color(FG),
    );
    if let Some(sub) = detail {
        text_col = text_col.child(
            text(sub)
                .font(TextStyle::new(scaled(12.0), 400.0))
                .color(fg_muted())
                .wrap(),
        );
    }
    if let Some(err) = error {
        text_col = text_col.child(
            text(err)
                .font(TextStyle::new(scaled(12.0), 400.0))
                .color(bad())
                .wrap(),
        );
    }
    let mut left = row().grow(1.0).min_w(0.0).gap(12.0).center_y();
    if let Some(ic) = icon {
        left = left.child(ic);
    }
    left = left.child(text_col);
    let mut wrap = el().key(key.to_string()).w_full().py(12.0).px(18.0);
    if highlight {
        wrap = wrap.fill(FG.alpha(0.06));
    }
    wrap.child(
        row()
            .w_full()
            .min_h(38.0)
            .gap(14.0)
            .justify_between()
            .center_y()
            .child(left)
            .child(control),
    )
}

fn field_box(input: El, w: Option<f32>) -> El {
    let mut r = row()
        .h(34.0)
        .px(10.0)
        .center_y()
        .cursor(Cursor::Text)
        .radius(RADIUS_SMALL)
        .fill(glass_still_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(input);
    if let Some(width) = w {
        r = r.w(width);
    } else {
        r = r.grow(1.0).min_w(0.0);
    }
    r
}

struct AccountMenuEntry {
    id: String,
    label: String,
    checked: bool,
    danger: bool,
    confirm: Option<String>,
    action: AccountMenuAction,
}

enum AccountMenuAction {
    None,
    Rpc {
        store: String,
        method: &'static str,
        params: Value,
        line: Option<String>,
    },
    SteamLibraryBegin,
    SteamLibrarySignOut,
    GiftsSteamToggle(bool),
}

fn build_account_menu_items(
    s: &AppState,
    store: &str,
    accounts: &StoreAccounts,
) -> Vec<AccountMenuEntry> {
    let mut items = Vec::new();
    match store {
        "steam" => {
            if let Some(ref st) = accounts.steam {
                for acc in &st.accounts {
                    let action = if acc.current {
                        AccountMenuAction::None
                    } else {
                        AccountMenuAction::Rpc {
                            store: "steam".into(),
                            method: METHOD_ACCOUNTS_SWITCH,
                            params: json!({ "store": "steam", "key": acc.key }),
                            line: Some(format!("Steam restarts to sign in as {}…", acc.name)),
                        }
                    };
                    items.push(AccountMenuEntry {
                        id: format!("switch:{}", acc.key),
                        label: acc.name.clone(),
                        checked: acc.current,
                        danger: false,
                        confirm: None,
                        action,
                    });
                }
            }
            items.push(AccountMenuEntry {
                id: "add".into(),
                label: "Add account".into(),
                checked: false,
                danger: false,
                confirm: None,
                action: AccountMenuAction::Rpc {
                    store: "steam".into(),
                    method: METHOD_ACCOUNTS_ADD,
                    params: json!({ "store": "steam" }),
                    line: Some("Steam closes and opens its sign-in".into()),
                },
            });
            if let Some(ref lib) = s.wp4.steam_account {
                if lib.signed_in {
                    items.push(AccountMenuEntry {
                        id: "library-out".into(),
                        label: "Sign out of Steam library".into(),
                        checked: false,
                        danger: true,
                        confirm: Some("Sign out of the Steam library?".into()),
                        action: AccountMenuAction::SteamLibrarySignOut,
                    });
                } else {
                    items.push(AccountMenuEntry {
                        id: "library-in".into(),
                        label: "Sign in to Steam…".into(),
                        checked: false,
                        danger: false,
                        confirm: None,
                        action: AccountMenuAction::SteamLibraryBegin,
                    });
                }
            }
            if let Some(web_in) = s.wp4.gifts_steam_signed_in {
                items.push(AccountMenuEntry {
                    id: "steam-web".into(),
                    label: if web_in {
                        "Sign out of Steam's website".into()
                    } else {
                        "Sign in to Steam's website".into()
                    },
                    checked: false,
                    danger: false,
                    confirm: None,
                    action: AccountMenuAction::GiftsSteamToggle(!web_in),
                });
            }
            if let Some(ref st) = accounts.steam {
                for acc in st.accounts.iter().filter(|a| !a.current) {
                    items.push(AccountMenuEntry {
                        id: format!("forget:{}", acc.key),
                        label: format!("Forget {}", acc.name),
                        checked: false,
                        danger: true,
                        confirm: Some(format!("Forget {}?", acc.name)),
                        action: AccountMenuAction::Rpc {
                            store: "steam".into(),
                            method: METHOD_ACCOUNTS_REMOVE,
                            params: json!({ "store": "steam", "key": acc.key }),
                            line: Some(format!("Forgetting {}", acc.name)),
                        },
                    });
                }
            }
        }
        "riot" => {
            if let Some(ref rt) = accounts.riot {
                for acc in &rt.accounts {
                    let action = if acc.current {
                        AccountMenuAction::None
                    } else {
                        AccountMenuAction::Rpc {
                            store: "riot".into(),
                            method: METHOD_ACCOUNTS_SWITCH,
                            params: json!({ "store": "riot", "key": acc.key }),
                            line: Some(format!("Riot Client restarts to sign in as {}…", acc.name)),
                        }
                    };
                    items.push(AccountMenuEntry {
                        id: format!("switch:{}", acc.key),
                        label: acc.name.clone(),
                        checked: acc.current,
                        danger: false,
                        confirm: None,
                        action,
                    });
                }
            }
            items.push(AccountMenuEntry {
                id: "add".into(),
                label: "Add account".into(),
                checked: false,
                danger: false,
                confirm: None,
                action: AccountMenuAction::Rpc {
                    store: "riot".into(),
                    method: METHOD_ACCOUNTS_ADD,
                    params: json!({ "store": "riot" }),
                    line: Some("Riot Client closes and opens its sign-in".into()),
                },
            });
            if let Some(ref rt) = accounts.riot {
                for acc in rt.accounts.iter().filter(|a| !a.current && a.saved) {
                    items.push(AccountMenuEntry {
                        id: format!("forget:{}", acc.key),
                        label: format!("Forget {}", acc.name),
                        checked: false,
                        danger: true,
                        confirm: Some(format!("Forget {}?", acc.name)),
                        action: AccountMenuAction::Rpc {
                            store: "riot".into(),
                            method: METHOD_ACCOUNTS_REMOVE,
                            params: json!({ "store": "riot", "key": acc.key }),
                            line: Some(format!("Forgetting {}", acc.name)),
                        },
                    });
                }
            }
        }
        "epic" | "gog" | "amazon" => {
            let name = match store {
                "epic" => "Epic",
                "gog" => "GOG",
                _ => "Amazon",
            };
            items.push(AccountMenuEntry {
                id: "switch".into(),
                label: "Switch account".into(),
                checked: false,
                danger: false,
                confirm: Some(format!("Sign out of {name}?")),
                action: AccountMenuAction::Rpc {
                    store: store.into(),
                    method: METHOD_ACCOUNTS_SWITCH,
                    params: json!({ "store": store }),
                    line: Some(format!("Exo signs out of {name}, then opens its sign-in")),
                },
            });
            if store == "epic" && accounts.epic.can_import {
                items.push(AccountMenuEntry {
                    id: "import".into(),
                    label: "Use the Epic Games Launcher's account".into(),
                    checked: false,
                    danger: false,
                    confirm: Some("Sign the launcher out?".into()),
                    action: AccountMenuAction::Rpc {
                        store: "epic".into(),
                        method: METHOD_ACCOUNTS_EPIC_IMPORT,
                        params: json!({}),
                        line: Some(
                            "The Epic Games Launcher signs out and Exo uses its account".into(),
                        ),
                    },
                });
            }
            if store == "epic" {
                items.push(AccountMenuEntry {
                    id: "epic-add".into(),
                    label: "Add another account".into(),
                    checked: false,
                    danger: false,
                    confirm: None,
                    action: AccountMenuAction::Rpc {
                        store: "epic".into(),
                        method: METHOD_ACCOUNTS_EPIC_ADD,
                        params: json!({}),
                        line: None,
                    },
                });
            }
            if store == "epic" && accounts.epic.can_switch_launcher {
                items.push(AccountMenuEntry {
                    id: "newlauncher".into(),
                    label: "Switch to Epic's new launcher".into(),
                    checked: false,
                    danger: false,
                    confirm: Some("Download it and switch?".into()),
                    action: AccountMenuAction::Rpc {
                        store: "epic".into(),
                        method: METHOD_ACCOUNTS_EPIC_NEW_LAUNCHER,
                        params: json!({}),
                        line: Some("Downloading Epic's new launcher".into()),
                    },
                });
            }
            let can_sign_out = match store {
                "epic" => accounts.epic.signed_in,
                "gog" => accounts.gog.can_sign_out,
                _ => accounts.amazon.signed_in,
            };
            if can_sign_out {
                items.push(AccountMenuEntry {
                    id: "signout".into(),
                    label: "Sign out".into(),
                    checked: false,
                    danger: true,
                    confirm: Some(format!("Sign out of {name}?")),
                    action: AccountMenuAction::Rpc {
                        store: store.into(),
                        method: METHOD_ACCOUNTS_SIGN_OUT,
                        params: json!({ "store": store }),
                        line: Some(format!("Exo signs out of {name}")),
                    },
                });
            }
        }
        _ => {}
    }
    items
}

fn account_menu_panel(s: &AppState, store: &str, accounts: &StoreAccounts) -> El {
    let entries = build_account_menu_items(s, store, accounts);
    let mut panel = col()
        .key(format!("acct-menu-panel-{store}"))
        .w_full()
        .px(18.0)
        .pb(12.0)
        .gap(4.0);
    for entry in entries {
        let confirm_id = entry.id.clone();
        let is_confirming = s.wp4.account_menu_confirm_id.as_deref() == Some(entry.id.as_str());
        if is_confirming && let Some(ref q) = entry.confirm {
            let action = entry.action;
            panel = panel.child(
                row()
                    .w_full()
                    .px(12.0)
                    .py(8.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                    .justify_between()
                    .center_y()
                    .gap(10.0)
                    .child(text(q.clone()).font(type_small()).color(FG))
                    .child(
                        row()
                            .gap(8.0)
                            .center_y()
                            .child(
                                set_btn(
                                    format!("acct-cancel-{store}-{}", entry.id),
                                    "Cancel",
                                    None,
                                )
                                .on_press(|s: &mut AppState| s.wp4.account_menu_confirm_id = None),
                            )
                            .child(
                                if entry.danger {
                                    set_danger_btn(
                                        format!("acct-go-{store}-{}", entry.id),
                                        "Confirm",
                                        None,
                                    )
                                } else {
                                    set_primary_btn(
                                        format!("acct-go-{store}-{}", entry.id),
                                        "Confirm",
                                        None,
                                    )
                                }
                                .on_press(
                                    move |s: &mut AppState| run_account_menu_action(s, &action),
                                ),
                            ),
                    ),
            );
            continue;
        }
        let action = entry.action;
        let has_confirm = entry.confirm.is_some();
        let label_color = if entry.danger { bad() } else { FG };
        let mut r = row()
            .key(format!("acct-item-{store}-{}", entry.id))
            .role("menuitem")
            .label(entry.label.clone())
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .w_full()
            .min_h(32.0)
            .px(12.0)
            .py(6.0)
            .radius(RADIUS_SMALL)
            .justify_between()
            .center_y()
            .hover(hover_fill())
            .child(text(entry.label).font(type_small()).color(label_color));
        if entry.checked {
            r = r.child(icon_check(14.0, good()));
        }
        r = r.on_press(move |s: &mut AppState| {
            if has_confirm {
                s.wp4.account_menu_confirm_id = Some(confirm_id.clone());
            } else {
                run_account_menu_action(s, &action);
            }
        });
        panel = panel.child(r);
    }
    panel
}

fn run_account_menu_action(s: &mut AppState, action: &AccountMenuAction) {
    match action {
        AccountMenuAction::None => {
            s.wp4.account_menu_store = None;
            s.wp4.account_menu_confirm_id = None;
        }
        AccountMenuAction::Rpc {
            store,
            method,
            params,
            line,
        } => {
            s.wp4_run_store_account_action(store.clone(), method, params.clone(), line.clone());
        }
        AccountMenuAction::SteamLibraryBegin => s.wp4_steam_begin_sign_in(),
        AccountMenuAction::SteamLibrarySignOut => s.wp4_steam_sign_out(),
        AccountMenuAction::GiftsSteamToggle(sign_in) => s.wp4_gifts_steam_toggle(*sign_in),
    }
}

fn steam_library_panel(s: &AppState) -> El {
    let mode = s.wp4.steam_mode;
    let step = s.wp4.steam_step.as_ref();
    let busy = s.wp4.steam_busy;
    let ended = step.is_some_and(|st| st.state == "expired" || st.state == "failed");

    let body = match mode {
        SteamSignInMode::Qr => {
            let qr_slot = if let Some(url) = step
                .filter(|st| st.state == "waiting")
                .and_then(|st| st.challenge_url.as_deref())
            {
                match QrCode::encode(url) {
                    Ok(code) => QrView::new(&code)
                        .size(160.0)
                        .label("Steam sign-in code")
                        .build()
                        .radius(RADIUS_SMALL),
                    Err(_) => el()
                        .size(160.0, 160.0)
                        .radius(RADIUS_SMALL)
                        .fill(BG)
                        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                        .center()
                        .child(text("Invalid QR").font(type_small()).color(fg_muted())),
                }
            } else if ended {
                el().size(160.0, 160.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                    .center()
                    .child(
                        set_primary_btn("steam-qr-new", "New code", None)
                            .disabled(busy)
                            .on_press(|s: &mut AppState| s.wp4_steam_start_qr()),
                    )
            } else {
                el().size(160.0, 160.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                    .center()
                    .child(text("Getting code…").font(type_small()).color(fg_muted()))
            };

            let mut steps = col()
                .grow(1.0)
                .min_w(0.0)
                .gap(8.0)
                .child(
                    text("Scan with the Steam app")
                        .font(type_section())
                        .color(FG),
                )
                .child(
                    text("1. Open the Steam app on your phone.")
                        .font(type_small())
                        .color(fg_muted()),
                )
                .child(
                    text("2. Tap the scan button in the app.")
                        .font(type_small())
                        .color(fg_muted()),
                )
                .child(
                    text("3. Point it at this code and approve the sign-in.")
                        .font(type_small())
                        .color(fg_muted()),
                );
            if ended {
                let err = step
                    .and_then(|st| st.message.clone())
                    .unwrap_or_else(|| "Could not start the sign-in. Try again.".into());
                steps = steps.child(text(err).font(type_small()).color(bad()).wrap());
            }

            row()
                .w_full()
                .gap(20.0)
                .items_start()
                .child(qr_slot)
                .child(steps)
        }
        SteamSignInMode::Password => {
            if step.is_some_and(|st| st.state == "guard") {
                let can = !s.wp4.steam_guard.is_empty() && !busy;
                let mut btn = set_primary_btn("steam-guard-submit", "Continue", None)
                    .disabled(!can)
                    .opacity(if can { 1.0 } else { 0.45 });
                if can {
                    btn = btn.on_press(|s: &mut AppState| s.wp4_steam_submit_guard());
                }
                col()
                    .w_full()
                    .gap(10.0)
                    .child(text("Steam Guard code").font(type_section()).color(FG))
                    .child(
                        row()
                            .w_full()
                            .gap(12.0)
                            .center_y()
                            .child(
                                OtpInput::new(&s.wp4.steam_guard)
                                    .key("steam-guard-input")
                                    .label("Steam Guard code")
                                    .box_size(40.0, 44.0)
                                    .gap(8.0)
                                    .radius(RADIUS_SMALL)
                                    .fill(glass_still_control_fill())
                                    .border(glass_rim_top(), FG)
                                    .color(FG)
                                    .text_style(TextStyle::new(scaled(16.0), 600.0))
                                    .disabled(busy)
                                    .on_state(|s: &mut AppState| &mut s.wp4.steam_guard)
                                    .on_complete(|s: &mut AppState, _, _| {
                                        s.wp4_steam_submit_guard()
                                    })
                                    .build(),
                            )
                            .child(btn),
                    )
            } else if step.is_some_and(|st| st.state == "approve") {
                text("Approve the sign-in in the Steam app on your phone.")
                    .font(type_section())
                    .color(FG)
                    .wrap()
            } else if step.is_none() || ended {
                let can = !s.wp4.steam_user.text.trim().is_empty()
                    && !s.wp4.steam_pass.text.is_empty()
                    && !busy;
                let mut btn = set_primary_btn("steam-pass-submit", "Sign in", None)
                    .disabled(!can)
                    .opacity(if can { 1.0 } else { 0.45 });
                if can {
                    btn = btn.on_press(|s: &mut AppState| s.wp4_steam_submit_password());
                }
                let mut form = col().w_full().gap(10.0).child(
                    row()
                        .w_full()
                        .gap(12.0)
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(4.0)
                                .child(text("Username").font(type_small()).color(fg_muted()))
                                .child(field_box(
                                    text_input(&s.wp4.steam_user, type_body())
                                        .key("steam-user-input")
                                        .label("Username")
                                        .w_full()
                                        .single_line()
                                        .color(FG)
                                        .on_edit(edit_access(|s: &mut AppState| {
                                            &mut s.wp4.steam_user
                                        })),
                                    None,
                                )),
                        )
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(4.0)
                                .child(text("Password").font(type_small()).color(fg_muted()))
                                .child(field_box(
                                    PasswordInput::new(&s.wp4.steam_pass, type_body())
                                        .key("steam-pass-input")
                                        .label("Password")
                                        .color(FG)
                                        .disabled(busy)
                                        .on_edit(|s: &mut AppState| &mut s.wp4.steam_pass)
                                        .on_submit(|s: &mut AppState, _| {
                                            s.wp4_steam_submit_password()
                                        })
                                        .build()
                                        .w_full(),
                                    None,
                                )),
                        ),
                );
                if ended {
                    let err = step
                        .and_then(|st| st.message.clone())
                        .unwrap_or_else(|| "Could not start the sign-in. Try again.".into());
                    form = form.child(text(err).font(type_small()).color(bad()).wrap());
                }
                form.child(row().child(btn))
            } else {
                text("Signing in to Steam…")
                    .font(type_small())
                    .color(fg_muted())
            }
        }
    };

    let mode_switch = if mode == SteamSignInMode::Qr {
        set_btn("steam-use-pass", "Use a password instead", None)
            .on_press(|s: &mut AppState| s.wp4_steam_switch_mode(SteamSignInMode::Password))
    } else {
        set_btn("steam-use-qr", "Scan a code instead", None)
            .on_press(|s: &mut AppState| s.wp4_steam_switch_mode(SteamSignInMode::Qr))
    };

    col()
        .key("steam-library-panel")
        .w_full()
        .px(18.0)
        .py(14.0)
        .gap(14.0)
        .fill(BG)
        .child(body)
        .child(
            row().gap(8.0).center_y().child(mode_switch).child(
                set_btn("steam-panel-cancel", "Cancel", None)
                    .on_press(|s: &mut AppState| s.wp4_steam_close_panel()),
            ),
        )
}

fn riot_id_row(s: &AppState, hi: bool) -> El {
    let saved_id = s
        .settings
        .riot_id
        .as_deref()
        .unwrap_or("")
        .trim()
        .to_string();
    let editing = s.wp4.riot_editing || saved_id.is_empty();
    let draft = s.wp4.riot_draft.text.trim().to_string();
    let ready = !draft.is_empty() && draft != saved_id && is_valid_riot_id(&draft);
    let busy = s.wp4.riot_busy;
    let detail = s
        .wp4
        .riot_note
        .as_ref()
        .filter(|n| !n.error)
        .map(|n| n.text.clone());
    let error = s
        .wp4
        .riot_note
        .as_ref()
        .filter(|n| n.error)
        .map(|n| n.text.clone());

    let control = if editing {
        let next_save = draft.clone();
        let saved_cancel = saved_id.clone();
        let mut save_btn = set_btn("set-riot-save", "Save", None)
            .disabled(!ready || busy)
            .opacity(if ready && !busy { 1.0 } else { 0.45 });
        if ready && !busy {
            save_btn = save_btn.on_press(move |s: &mut AppState| {
                s.wp4_save_riot_id(next_save.clone());
            });
        }
        let mut r = row()
            .gap(8.0)
            .center_y()
            .child(field_box(
                text_input(&s.wp4.riot_draft, type_body())
                    .key("set-riot-id-input")
                    .label("Riot ID")
                    .w_full()
                    .single_line()
                    .color(FG)
                    .placeholder("Name#TAG", fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.riot_draft)),
                Some(180.0),
            ))
            .child(save_btn);
        if !saved_id.is_empty() {
            r = r.child(set_btn("set-riot-cancel", "Cancel", None).on_press(
                move |s: &mut AppState| {
                    s.wp4.riot_draft = TextEdit::new(&saved_cancel);
                    s.wp4.riot_editing = false;
                },
            ));
        }
        r
    } else {
        row()
            .gap(8.0)
            .center_y()
            .child(text(saved_id.clone()).font(type_small()).color(good()))
            .child(
                set_btn("set-riot-replace", "Replace", None)
                    .disabled(busy)
                    .on_press(move |s: &mut AppState| {
                        s.wp4.riot_draft = TextEdit::new(&saved_id);
                        s.wp4.riot_editing = true;
                    }),
            )
            .child(
                set_btn("set-riot-remove", "Remove", None)
                    .disabled(busy)
                    .on_press(|s: &mut AppState| s.wp4_save_riot_id(String::new())),
            )
    };

    store_row_box(
        "library-riot-id",
        None,
        "Riot ID",
        detail,
        error,
        hi,
        control,
    )
}

fn riot_baseline_row(s: &AppState, hi: bool) -> El {
    let summary = baseline_summary_parts(s.settings.riot_baseline.as_ref());
    let editing = s.wp4.riot_hours_editing;
    let busy = s.wp4.riot_hours_busy;
    let note = s.wp4.riot_hours_note.as_ref();
    let detail = if let Some(n) = note.filter(|n| !n.error) {
        Some(n.text.clone())
    } else if editing {
        Some("Need day-one history? Request Riot data export at privacy.riotgames.com".into())
    } else if !summary.is_empty() {
        Some(summary.join(" · "))
    } else {
        None
    };
    let error = note.filter(|n| n.error).map(|n| n.text.clone());

    let control = if editing {
        let v = parse_hours_input(&s.wp4.riot_val_input.text);
        let l = parse_hours_input(&s.wp4.riot_lol_input.text);
        let t = parse_hours_input(&s.wp4.riot_tft_input.text);
        let ready =
            matches!((v, l, t), (Ok(a), Ok(b), Ok(c)) if a.is_some() || b.is_some() || c.is_some());
        let mut save_btn = set_btn("set-riot-base-save", "Save", None)
            .disabled(!ready || busy)
            .opacity(if ready && !busy { 1.0 } else { 0.45 });
        if ready && !busy {
            save_btn = save_btn.on_press(|s: &mut AppState| s.wp4_save_riot_baseline());
        }
        row()
            .gap(6.0)
            .center_y()
            .child(field_box(
                text_input(&s.wp4.riot_val_input, type_small())
                    .key("set-riot-val")
                    .label("VALORANT starting hours")
                    .w_full()
                    .single_line()
                    .color(FG)
                    .placeholder("VALORANT (h)", fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.riot_val_input)),
                Some(96.0),
            ))
            .child(field_box(
                text_input(&s.wp4.riot_lol_input, type_small())
                    .key("set-riot-lol")
                    .label("League of Legends starting hours")
                    .w_full()
                    .single_line()
                    .color(FG)
                    .placeholder("League (h)", fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.riot_lol_input)),
                Some(88.0),
            ))
            .child(field_box(
                text_input(&s.wp4.riot_tft_input, type_small())
                    .key("set-riot-tft")
                    .label("Teamfight Tactics starting hours")
                    .w_full()
                    .single_line()
                    .color(FG)
                    .placeholder("TFT (h)", fg_muted())
                    .on_edit(edit_access(|s: &mut AppState| &mut s.wp4.riot_tft_input)),
                Some(76.0),
            ))
            .child(save_btn)
            .child(
                set_btn("set-riot-base-privacy", "Request export", None)
                    .on_press(|s: &mut AppState| s.wp4_open_url(RIOT_PRIVACY_URL)),
            )
            .child(
                set_btn("set-riot-base-cancel", "Cancel", None)
                    .on_press(|s: &mut AppState| s.wp4.riot_hours_editing = false),
            )
    } else {
        row()
            .gap(8.0)
            .center_y()
            .child(
                set_btn("set-riot-sync-hours", "Sync hours", None)
                    .disabled(busy)
                    .on_press(|s: &mut AppState| s.wp4_sync_riot_hours()),
            )
            .child(
                set_btn("set-riot-import-export", "Import export", None)
                    .disabled(busy)
                    .on_press(|s: &mut AppState| s.wp4_import_riot_export()),
            )
            .child(
                set_btn("set-riot-edit-hours", "Edit", None)
                    .disabled(busy)
                    .on_press(|s: &mut AppState| s.wp4_begin_riot_hours_edit()),
            )
    };

    store_row_box(
        "library-riot-hours",
        None,
        "Riot hours",
        detail,
        error,
        hi,
        control,
    )
}

pub fn stores_section(s: &AppState) -> El {
    let hi = |id: &str| s.wp4.highlight_row.is_some_and(|(rid, _)| rid == id);
    let installed = settings_store_rows(&s.wp4.stores);
    let missing = available_store_rows(&s.wp4.stores);
    let accounts = s.wp4.store_accounts.as_ref();
    let check_busy = s.wp4.store_check_busy;

    let check_btn = set_btn(
        "set-stores-check",
        if check_busy {
            "Checking…"
        } else {
            "Check again"
        },
        None,
    )
    .disabled(check_busy)
    .on_press(|s: &mut AppState| s.wp4_check_stores());

    let mut card = amoled_card(RADIUS_CARD).w_full();

    if installed.is_empty() {
        card = card.child(store_row_box(
            "library-stores",
            None,
            if check_busy {
                "Looking for stores…"
            } else {
                "No store apps found on this PC"
            },
            None,
            s.wp4.store_check_copy.clone(),
            hi("library-stores"),
            el(),
        ));
    } else {
        for (idx, store) in installed.iter().enumerate() {
            if idx > 0 {
                card = card.child(el().w_full().h(1.0).fill(hairline()));
            }
            card = render_installed_store(s, card, store, accounts, hi("library-stores"));
        }
    }

    if !installed.is_empty()
        && let Some(ref copy) = s.wp4.store_check_copy
    {
        card = card.child(el().w_full().h(1.0).fill(hairline())).child(
            el().w_full()
                .px(18.0)
                .py(10.0)
                .child(text(copy.clone()).font(type_small()).color(bad()).wrap()),
        );
    }

    let has_accounts = installed
        .iter()
        .any(|st| st.store == "steam" || st.store == "epic");

    if has_accounts && let Some(accts) = accounts {
        for added in &accts.epic.added {
            let key = added.key.clone();
            let confirming = s.wp4.epic_confirm_remove.as_deref() == Some(added.key.as_str());
            let removing = s.wp4.epic_removing_key.as_deref() == Some(added.key.as_str());
            let err = s
                .wp4
                .epic_remove_error
                .as_ref()
                .filter(|(k, _)| k == &added.key)
                .map(|(_, m)| m.clone());
            let ctrl = if confirming {
                row()
                    .gap(8.0)
                    .center_y()
                    .child(
                        set_btn(format!("epic-rem-cancel-{key}"), "Cancel", None)
                            .on_press(|s: &mut AppState| s.wp4.epic_confirm_remove = None),
                    )
                    .child(
                        set_danger_btn(format!("epic-rem-go-{key}"), "Remove?", None)
                            .disabled(removing)
                            .on_press(move |s: &mut AppState| s.wp4_epic_remove_added(key.clone())),
                    )
            } else {
                set_btn(format!("epic-rem-{key}"), "Remove", None)
                    .disabled(removing)
                    .on_press(move |s: &mut AppState| s.wp4.epic_confirm_remove = Some(key.clone()))
            };
            card = card
                .child(el().w_full().h(1.0).fill(hairline()))
                .child(store_row_box(
                    &format!("epic-added-{}", added.key),
                    Some(store_logo_or_fallback("epic", 20.0)),
                    &added.name,
                    None,
                    err,
                    false,
                    ctrl,
                ));
        }
    }

    if has_accounts {
        let quiet = s.settings.store_clients_quiet;
        card = card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(store_row_box(
                "library-quiet-stores",
                None,
                "Keep store apps quiet",
                None,
                None,
                hi("library-quiet-stores"),
                switch_pill("set-quiet-clients", "Keep store apps quiet", quiet).on_press(
                    move |s: &mut AppState| s.set_bool_setting("storeClientsQuiet", !quiet),
                ),
            ));
    }

    let has_riot = installed.iter().any(|st| st.store == "riot")
        || s.settings
            .riot_id
            .as_deref()
            .is_some_and(|id| !id.trim().is_empty());
    if has_riot {
        card = card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(riot_id_row(s, hi("library-riot-id")));
        if s.settings
            .riot_id
            .as_deref()
            .is_some_and(|id| !id.trim().is_empty())
        {
            card = card
                .child(el().w_full().h(1.0).fill(hairline()))
                .child(riot_baseline_row(s, hi("library-riot-hours")));
        }
    }

    if !missing.is_empty() {
        let mut chips = row().w_full().gap(8.0).flex_wrap();
        for store in missing {
            if let Some(url) = store_client_download_url(&store.store) {
                chips = chips.child(
                    set_btn(
                        format!("set-get-store-{}", store.store),
                        &store.display_name,
                        Some(store_logo_or_fallback(&store.store, 15.0)),
                    )
                    .on_press(move |s: &mut AppState| s.wp4_open_url(url)),
                );
            }
        }
        let mut get_box = col()
            .key("library-get-store")
            .w_full()
            .px(18.0)
            .py(12.0)
            .gap(10.0)
            .child(
                text("Get another store")
                    .font(TextStyle::new(scaled(13.0), 500.0))
                    .color(FG),
            )
            .child(chips);
        if hi("library-get-store") {
            get_box = get_box.fill(FG.alpha(0.06));
        }
        if let Some(ref err) = s.wp4.store_get_error {
            get_box = get_box.child(text(err.clone()).font(type_small()).color(bad()).wrap());
        }
        card = card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(get_box);
    }

    col()
        .w_full()
        .gap(8.0)
        .child(sec_head_with_action("Game stores", Some(check_btn)))
        .child(card)
}

fn render_installed_store(
    s: &AppState,
    mut card: El,
    store: &StoreStatus,
    accounts: Option<&StoreAccounts>,
    hi: bool,
) -> El {
    let id = &store.store;
    let (state_label, state_on) = store_state(store);
    let who = account_name(id, accounts);
    let connect = can_connect_store(store) && who.is_none();
    let handoff = signs_in_in_own_app(store);
    let open = !handoff && can_open_store_client(store);
    let show_account = accounts.is_some()
        && matches!(id.as_str(), "steam" | "riot" | "epic" | "gog" | "amazon")
        && (who.is_some() || id == "steam" || id == "riot");

    let busy = s.wp4.store_busy.get(id).copied();
    let acct_busy = s.wp4.account_busy_store.as_deref() == Some(id.as_str());
    let note = s.wp4.store_notes.get(id);
    let lib_line = if id == "steam" {
        s.wp4
            .steam_account
            .as_ref()
            .filter(|a| a.signed_in)
            .map(|a| {
                format!(
                    "Library: {}. Signed in.",
                    a.account_name.as_deref().unwrap_or("Steam account")
                )
            })
    } else {
        None
    };
    let detail = s
        .wp4
        .account_line
        .get(id)
        .cloned()
        .or_else(|| note.filter(|n| !n.error).map(|n| n.text.clone()))
        .or(lib_line)
        .or_else(|| store_layout_note(store).map(str::to_string));
    let error = s
        .wp4
        .account_error
        .get(id)
        .cloned()
        .or_else(|| note.filter(|n| n.error).map(|n| n.text.clone()));

    let mut controls = row().gap(8.0).center_y().child(
        text(who.unwrap_or_else(|| state_label.to_string()))
            .font(type_small())
            .color(if state_on { good() } else { fg_muted() }),
    );

    if connect {
        let st_id = id.clone();
        let dn = store.display_name.clone();
        controls = controls.child(
            set_btn(
                format!("set-store-connect-{id}"),
                if busy == Some(StoreActionKind::Connect) {
                    "Signing in…"
                } else {
                    "Sign in"
                },
                None,
            )
            .disabled(busy.is_some())
            .on_press(move |s: &mut AppState| s.wp4_connect_store(st_id.clone(), dn.clone())),
        );
    }
    if handoff {
        let st_id = id.clone();
        let dn = store.display_name.clone();
        controls = controls.child(
            set_btn(
                format!("set-store-handoff-{id}"),
                format!("Open {}", store.display_name),
                None,
            )
            .disabled(busy.is_some())
            .on_press(move |s: &mut AppState| s.wp4_connect_store(st_id.clone(), dn.clone())),
        );
    }
    if open {
        let st_id = id.clone();
        let dn = store.display_name.clone();
        controls = controls.child(
            set_btn(
                format!("set-store-open-{id}"),
                if busy == Some(StoreActionKind::Open) {
                    "Opening…"
                } else {
                    "Open"
                },
                None,
            )
            .disabled(busy.is_some())
            .on_press(move |s: &mut AppState| s.wp4_open_store(st_id.clone(), dn.clone())),
        );
    }
    if show_account {
        let st_id = id.clone();
        let menu_open = s.wp4.account_menu_store.as_deref() == Some(id.as_str());
        controls = controls.child(
            set_btn(
                format!("set-store-acct-{id}"),
                if acct_busy { "Working…" } else { "Account" },
                Some(icon_chevron_down(12.0, FG)),
            )
            .disabled(busy.is_some() || acct_busy)
            .on_press(move |s: &mut AppState| {
                s.wp4.account_menu_confirm_id = None;
                s.wp4.account_menu_store = if menu_open { None } else { Some(st_id.clone()) };
            }),
        );
    }

    card = card.child(store_row_box(
        &format!("set-store-row-{id}"),
        Some(store_logo_or_fallback(id, 22.0)),
        &store.display_name,
        detail,
        error,
        hi && id == "steam",
        controls,
    ));

    if show_account
        && s.wp4.account_menu_store.as_deref() == Some(id.as_str())
        && let Some(accts) = accounts
    {
        card = card.child(account_menu_panel(s, id, accts));
    }

    if id == "steam" && s.wp4.steam_panel_open {
        card = card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(steam_library_panel(s));
    }

    if id == "steam"
        && let Some(steam_info) = accounts.and_then(|a| a.steam.as_ref())
        && (steam_info.can_offline || steam_info.offline)
    {
        let pending = s.wp4.steam_offline_pending;
        let checked = pending.unwrap_or(steam_info.offline);
        let offline_hi = s
            .wp4
            .highlight_row
            .is_some_and(|(rid, _)| rid == "library-steam-offline");
        card = card
            .child(el().w_full().h(1.0).fill(hairline()))
            .child(store_row_box(
                "library-steam-offline",
                Some(store_logo_or_fallback("steam", 20.0)),
                "Offline mode",
                s.wp4.steam_offline_line.clone(),
                s.wp4.steam_offline_error.clone(),
                offline_hi,
                switch_pill("set-steam-offline", "Steam offline mode", checked)
                    .disabled(pending.is_some())
                    .on_press(move |s: &mut AppState| s.wp4_steam_set_offline(!checked)),
            ));
    }

    card
}

pub fn shaders_section(s: &AppState) -> Option<El> {
    if !s.wp4.shaders_present {
        return None;
    }
    let hi = s
        .wp4
        .highlight_row
        .is_some_and(|(rid, _)| rid == "library-shaders");
    let on = s.wp4.shaders_auto;
    let card = amoled_card(RADIUS_CARD).w_full().child(store_row_box(
        "library-shaders",
        None,
        "Improve shader pre-compiling",
        Some("Adds a small d3d12.dll to supported games to cut shader stutter".into()),
        s.wp4.shaders_error.clone(),
        hi,
        switch_pill("set-shaders-auto", "Improve shader pre-compiling", on)
            .on_press(move |s: &mut AppState| s.wp4_set_shaders_auto(!on)),
    ));
    Some(
        col()
            .w_full()
            .gap(8.0)
            .child(sec_head_with_action("Shaders", None))
            .child(card),
    )
}
