use crate::game_actions::{
    ArtAction, PendingChoice, PropsSectionId, ban_question, compress_shown, compress_stopped,
    compress_words, default_section_id, download_question, format_size, gate_reason, moved_share,
    sections_for, transfer_for_game, upscaler_view,
};
use crate::state::{AppState, HostStatus, InputMode, OpenDropdown};
use exo_kit::{
    BG, BadgeTone, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, SPRING_BASE,
    SPRING_MOVE, SPRING_QUICK, SPRING_SNAPPY, SelectDismissCb, SelectMenuSpec, SelectOption,
    SelectTrigger,
    TRANSPARENT, bad, fg_faint, fg_muted, glass_rim_bottom, glass_rim_top,
    glass_still_control_fill, good, hairline, hex, hover_fill, icon_refresh, icon_rotate_ccw,
    icon_upload, pressed_fill, progress_bar, scaled, select_menu_layers, select_trigger,
    status_badge, switch_pill, track_fill, type_body, type_control, type_small, type_title,
};
use exo_rpc::{Game, GameProperties};
use rune::{
    Align, Cursor, El, LiquidGlass, TextStyle, Tx, col, edit_access, el, row, text, text_input,
};
use std::rc::Rc;

fn switch_pill_owned(key: String, label: String, on: bool) -> El {
    let knob = el()
        .size(20.0, 20.0)
        .radius(10.0)
        .fill(FG)
        .shadow(1.0, 3.0, BG.alpha(0.35))
        .absolute(if on { 18.0 } else { 2.0 }, 2.0)
        .motion(SPRING_SNAPPY);
    el().key(key)
        .role("switch")
        .label(label)
        .checked(on)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(40.0, 24.0)
        .radius(RADIUS_PILL)
        .fill(if on { good() } else { track_fill() })
        .motion(SPRING_QUICK)
        .child(knob)
}

fn props_row(
    title: &str,
    detail: Option<&str>,
    note: Option<&crate::game_actions::RowNote>,
    leading: Option<El>,
    right: El,
) -> El {
    let title_row = if let Some(ic) = leading {
        row()
            .gap(8.0)
            .center_y()
            .child(ic)
            .child(text(title.to_string()).font(type_control()).color(FG))
    } else {
        row()
            .center_y()
            .child(text(title.to_string()).font(type_control()).color(FG))
    };
    let mut left = col().grow(1.0).min_w(0.0).gap(5.0).child(title_row);
    if let Some(d) = detail.filter(|s| !s.is_empty()) {
        left = left.child(
            text(d.to_string())
                .font(type_small())
                .color(hex(0xdcdcdc, 1.0))
                .truncate(),
        );
    }
    let mut box_col = col().w_full().child(
        row()
            .w_full()
            .min_h(44.0)
            .gap(16.0)
            .justify_between()
            .center_y()
            .child(left)
            .child(row().pt(2.0).gap(8.0).center_y().child(right)),
    );
    if let Some(n) = note
        && !n.text.is_empty()
    {
        box_col =
            box_col.child(el().w_full().pt(4.0).pb(2.0).child(
                text(n.text.clone()).font(type_small()).color(if n.bad {
                    bad()
                } else {
                    fg_muted()
                }),
            ));
    }
    el().w_full()
        .py(12.0)
        .px(20.0)
        .border_sides([1.0, 0.0, 0.0, 0.0], hairline())
        .child(box_col)
}

fn props_btn(
    key: &str,
    label: &str,
    icon: Option<El>,
    disabled: bool,
    danger_armed: bool,
    gamepad_focus: bool,
    on_press: impl Fn(&mut AppState, &mut Tx) + 'static,
) -> El {
    let mut btn = row()
        .key(key.to_string())
        .role("button")
        .label(label.to_string())
        .h(32.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if danger_armed { bad() } else { FG.alpha(0.03) })
        .hit()
        .disabled(disabled)
        .focusable(!disabled)
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .hold_landing_width()
        .motion(SPRING_BASE);
    if !danger_armed {
        btn = btn.border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
    }
    if !disabled {
        btn = btn
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .hover(if danger_armed {
                bad().alpha(0.85)
            } else {
                hover_fill()
            })
            .press(if danger_armed {
                bad().alpha(0.75)
            } else {
                pressed_fill()
            })
            .on_press_tx(on_press);
        if gamepad_focus {
            btn = btn.focus_ring(2.0, 3.0, FG);
        }
    }
    if let Some(ic) = icon {
        btn = btn.child(ic);
    }
    btn.child(
        text(label.to_string())
            .font(type_control())
            .color(if disabled {
                fg_faint()
            } else if danger_armed {
                PRIMARY_FG
            } else {
                FG
            }),
    )
}

fn props_danger_btn(
    key: &str,
    label: &str,
    disabled: bool,
    danger_armed: bool,
    gamepad_focus: bool,
    on_press: impl Fn(&mut AppState, &mut Tx) + 'static,
) -> El {
    let mut btn = row()
        .key(key.to_string())
        .role("button")
        .label(label.to_string())
        .h(32.0)
        .px(12.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if danger_armed { bad() } else { FG.alpha(0.03) })
        .hit()
        .disabled(disabled)
        .focusable(!disabled)
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .hold_landing_width()
        .motion(SPRING_BASE);
    if !danger_armed {
        btn = btn.border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
    }
    if !disabled {
        btn = btn
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .hover(if danger_armed {
                bad().alpha(0.85)
            } else {
                hover_fill()
            })
            .press(if danger_armed {
                bad().alpha(0.75)
            } else {
                pressed_fill()
            })
            .on_press_tx(on_press);
        if gamepad_focus {
            btn = btn.focus_ring(2.0, 3.0, FG);
        }
    }
    btn.child(
        text(label.to_string())
            .font(type_control())
            .color(if disabled {
                fg_faint()
            } else if danger_armed {
                PRIMARY_FG
            } else {
                hex(0xff7979, 1.0)
            }),
    )
}

fn pending_confirm_pill(pending: &PendingChoice, gid: String, gamepad_focus: bool) -> El {
    let key_s = pending.key.clone();
    let val_s = pending.value.clone();
    let row_s = pending.row.clone();
    let danger = pending.danger;
    props_btn(
        &format!("gp-confirm-{}", pending.row),
        &pending.question,
        None,
        false,
        danger,
        gamepad_focus,
        move |st, _| {
            st.set_game_property_ex(&gid, &key_s, &val_s, None, &row_s);
        },
    )
}

pub fn artwork_tools(s: &AppState, game: &Game, disabled: bool) -> El {
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let action = s.game_page.art_action;
    let busy = action.is_some();
    let custom = game.cover_source.as_deref() == Some("custom");
    let gid_rep = game.id.clone();
    let gid_sec = game.id.clone();

    let replace_btn = props_btn(
        "gp-art-replace",
        if action == Some(ArtAction::Replace) {
            "Replacing…"
        } else {
            "Replace cover"
        },
        Some(icon_upload(
            14.0,
            if disabled || busy { fg_faint() } else { FG },
        )),
        disabled || busy,
        false,
        gamepad_focus,
        move |st, _| st.art_action_game(&gid_rep, ArtAction::Replace),
    );

    let second_btn = if custom {
        props_btn(
            "gp-art-reset",
            if action == Some(ArtAction::Reset) {
                "Resetting…"
            } else {
                "Reset cover"
            },
            Some(icon_rotate_ccw(
                14.0,
                if disabled || busy { fg_faint() } else { FG },
            )),
            disabled || busy,
            false,
            gamepad_focus,
            move |st, _| st.art_action_game(&gid_sec, ArtAction::Reset),
        )
    } else {
        props_btn(
            "gp-art-refresh",
            if action == Some(ArtAction::Refetch) {
                "Refreshing…"
            } else {
                "Refresh art"
            },
            Some(icon_refresh(
                14.0,
                if disabled || busy { fg_faint() } else { FG },
            )),
            disabled || busy,
            false,
            gamepad_focus,
            move |st, _| st.art_action_game(&gid_sec, ArtAction::Refetch),
        )
    };

    row()
        .gap(8.0)
        .center_y()
        .child(replace_btn)
        .child(second_btn)
}

fn general_section(s: &AppState, game: &Game, props: &GameProperties) -> Vec<El> {
    let mut rows = Vec::new();
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let locked = props.running;
    let busy = s.game_page.props_busy.as_deref();

    if props.launch_options.is_some() {
        let gid_save = game.id.clone();
        let initial = props.launch_options.clone().unwrap_or_default();
        let launch_input = row()
            .w(220.0)
            .h(32.0)
            .px(12.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(FG.alpha(0.03))
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .cursor(Cursor::Text)
            .child(
                text_input(&s.props_launch_opts, type_body())
                    .key("props-launch-input")
                    .label("Launch options")
                    .w_full()
                    .single_line()
                    .color(FG)
                    .placeholder("", fg_muted())
                    .on_edit(edit_access(|st: &mut AppState| &mut st.props_launch_opts))
                    .on_submit(move |st: &mut AppState, _| {
                        let val = st.props_launch_opts.text.clone();
                        if val.trim() != initial.trim() {
                            st.set_game_property_ex(
                                &gid_save,
                                "launchOptions",
                                &val,
                                None,
                                "launchOptions",
                            );
                        }
                    }),
            );
        let mut right = row().gap(8.0).center_y();
        if busy == Some("launchOptions") {
            right = right.child(text("Saving…").font(type_small()).color(fg_muted()));
        }
        right = right.child(launch_input);
        rows.push(props_row(
            "Launch options",
            None,
            s.game_page.props_notes.get("launchOptions"),
            None,
            right,
        ));
    } else if let Some(note) = props
        .launch_options_note
        .as_deref()
        .filter(|n| !n.is_empty())
    {
        rows.push(props_row("Launch options", Some(note), None, None, el()));
    }

    if props.language.is_some() && props.language_choices.len() > 1 {
        let cur_val = props.language.as_deref().unwrap_or("");
        let label = props
            .language_choices
            .iter()
            .find(|c| c.value == cur_val)
            .map(|c| c.label.as_str())
            .unwrap_or("Set elsewhere");
        let lang_pill = select_trigger(
            SelectTrigger::new("gp-language-select", "Language", label)
                .open(s.open_dropdown == Some(OpenDropdown::GameLanguage))
                .disabled(locked || busy.is_some())
                .flat(true)
                .gamepad_focus(gamepad_focus),
            |st: &mut AppState, _| st.toggle_dropdown(OpenDropdown::GameLanguage),
        );
        let mut right = row().gap(8.0).center_y();
        if busy == Some("language") {
            right = right.child(text("Switching…").font(type_small()).color(fg_muted()));
        }
        if let Some(ref p) = s.game_page.pending_choice
            && p.row == "language"
        {
            right = right.child(pending_confirm_pill(p, game.id.clone(), gamepad_focus));
        }
        right = right.child(lang_pill);
        rows.push(props_row(
            "Language",
            None,
            s.game_page.props_notes.get("language"),
            None,
            right,
        ));
    }

    if props.cloud_sync == Some(true) {
        let gid = game.id.clone();
        let btn = props_btn(
            "gp-props-cloud-sync",
            if busy == Some("cloudSync") {
                "Syncing…"
            } else {
                "Sync now"
            },
            None,
            busy.is_some() || locked,
            false,
            gamepad_focus,
            move |st, _| st.set_game_property_ex(&gid, "cloudSync", "", None, "cloudSync"),
        );
        rows.push(props_row(
            "Cloud saves",
            None,
            s.game_page.props_notes.get("cloudSync"),
            None,
            btn,
        ));
    }

    if let Some(through) = props.through_steam {
        let gid = game.id.clone();
        let mut sw = switch_pill("props-through-steam-switch", "Steam Input", through);
        if busy.is_none() {
            sw = sw.on_press(move |st: &mut AppState| {
                st.set_game_property_ex(
                    &gid,
                    "throughSteam",
                    if through { "off" } else { "on" },
                    None,
                    "throughSteam",
                );
            });
        }
        let mut right = row().gap(8.0).center_y();
        if busy == Some("throughSteam") {
            right = right.child(text("Saving…").font(type_small()).color(fg_muted()));
        }
        right = right.child(sw);
        rows.push(props_row(
            "Steam Input",
            None,
            s.game_page.props_notes.get("throughSteam"),
            None,
            right,
        ));
    }

    if let Some(overlay_on) = props.steam_overlay {
        let gid = game.id.clone();
        let mut sw = switch_pill("props-overlay-switch", "Steam overlay", overlay_on);
        if busy.is_none() {
            sw = sw.on_press(move |st: &mut AppState| {
                st.set_game_property_ex(
                    &gid,
                    "steamOverlay",
                    if overlay_on { "off" } else { "on" },
                    None,
                    "steamOverlay",
                );
            });
        }
        let mut right = row().gap(8.0).center_y();
        if busy == Some("steamOverlay") {
            right = right.child(text("Saving…").font(type_small()).color(fg_muted()));
        }
        right = right.child(sw);
        rows.push(props_row(
            "Steam overlay",
            props.steam_overlay_note.as_deref(),
            s.game_page.props_notes.get("steamOverlay"),
            None,
            right,
        ));
    }

    if let Some(ref ac) = props.anti_cheat_launch {
        let label = match ac.value.as_str() {
            "without" => "Launch without anti-cheat (offline)",
            "with" => "Launch with anti-cheat",
            _ => "Set elsewhere",
        };
        let trigger = select_trigger(
            SelectTrigger::new("gp-anticheat-select", "Anti-cheat", label)
                .open(s.open_dropdown == Some(OpenDropdown::GameAntiCheat))
                .disabled(locked || busy.is_some())
                .flat(true)
                .gamepad_focus(gamepad_focus),
            |st: &mut AppState, _| st.toggle_dropdown(OpenDropdown::GameAntiCheat),
        );
        let mut right = row().gap(8.0).center_y();
        if busy == Some("antiCheatLaunch") {
            right = right.child(text("Saving…").font(type_small()).color(fg_muted()));
        }
        if let Some(ref p) = s.game_page.pending_choice
            && p.row == "antiCheatLaunch"
        {
            right = right.child(pending_confirm_pill(p, game.id.clone(), gamepad_focus));
        }
        right = right.child(trigger);
        rows.push(props_row(
            "Anti-cheat",
            Some(ac.detail.as_str()),
            s.game_page.props_notes.get("antiCheatLaunch"),
            None,
            right,
        ));
    }

    if let Some(ref store_props) = props.store_properties
        && !store_props.is_empty()
    {
        let gid = game.id.clone();
        let btn = props_btn(
            "gp-props-store-ctrl",
            &format!("Open {store_props}"),
            None,
            busy.is_some(),
            false,
            gamepad_focus,
            move |st, _| {
                st.set_game_property_ex(&gid, "storeProperties", "", None, "storeProperties")
            },
        );
        rows.push(props_row(
            &format!("Controller in {store_props}"),
            None,
            s.game_page.props_notes.get("storeProperties"),
            None,
            btn,
        ));
    }

    rows
}

fn compress_row(s: &AppState, game: &Game, locked: bool) -> Option<El> {
    let c = s.game_page.compress.as_ref()?;
    if !compress_shown(c) {
        return None;
    }
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let working = matches!(c.state.as_str(), "compressing" | "uncompressing");
    let detail = compress_words(c);
    let err = s
        .game_page
        .compress_error
        .as_deref()
        .or_else(|| compress_stopped(c));
    let note = err.map(|m| crate::game_actions::RowNote {
        text: m.to_string(),
        bad: true,
    });

    let mut controls = row().gap(8.0).center_y();
    if working && c.held.is_none() {
        let pct = c.percent.unwrap_or(0.0).clamp(0.0, 1.0) as f32;
        controls = controls.child(progress_bar(84.0, pct));
    }

    let gid = game.id.clone();
    let uncompress_btn = |st: &AppState, gid_u: String| {
        let armed = st.is_confirm_armed("gp-compress-uncompress");
        let q = if c.saved > 0 {
            format!("Use {} again?", format_size(c.saved))
        } else {
            "Put it back?".into()
        };
        props_btn(
            "gp-compress-uncompress",
            if armed { &q } else { "Uncompress" },
            None,
            locked,
            armed,
            gamepad_focus,
            move |st, tx| {
                if st.arm_game_confirm("gp-compress-uncompress", tx) {
                    st.compress_game_act(&gid_u, "uncompress", None);
                }
            },
        )
    };

    match c.state.as_str() {
        "compressing" => {
            let gid_p = gid.clone();
            controls = controls
                .child(props_btn(
                    "gp-compress-pause",
                    "Pause",
                    None,
                    false,
                    false,
                    gamepad_focus,
                    move |st, _| st.compress_game_act(&gid_p, "pause", None),
                ))
                .child(props_btn(
                    "gp-compress-stop",
                    "Stop",
                    None,
                    false,
                    false,
                    gamepad_focus,
                    move |st, _| st.compress_game_act(&gid, "cancel", None),
                ));
        }
        "uncompressing" => {
            controls = controls.child(props_btn(
                "gp-compress-stop",
                "Stop",
                None,
                false,
                false,
                gamepad_focus,
                move |st, _| st.compress_game_act(&gid, "cancel", None),
            ));
        }
        "queued" => {
            controls = controls.child(props_btn(
                "gp-compress-cancel",
                "Cancel",
                None,
                false,
                false,
                gamepad_focus,
                move |st, _| st.compress_game_act(&gid, "cancel", None),
            ));
        }
        "paused" => {
            let gid_r = gid.clone();
            controls = controls
                .child(props_btn(
                    "gp-compress-resume",
                    "Resume",
                    None,
                    locked,
                    false,
                    gamepad_focus,
                    move |st, _| st.compress_game_act(&gid_r, "resume", None),
                ))
                .child(uncompress_btn(s, gid));
        }
        "compressed" => {
            controls = controls.child(uncompress_btn(s, gid));
        }
        "changed" => {
            let gid_a = gid.clone();
            controls = controls
                .child(props_btn(
                    "gp-compress-again",
                    "Compress again",
                    None,
                    locked,
                    false,
                    gamepad_focus,
                    move |st, _| st.compress_game_act(&gid_a, "compress", None),
                ))
                .child(uncompress_btn(s, gid));
        }
        "needsConsent" => {
            if let Some(ac) = c.anti_cheat.clone().filter(|v| !v.is_empty()) {
                let armed = s.is_confirm_armed("gp-compress-allow");
                controls = controls.child(props_btn(
                    "gp-compress-allow",
                    if armed {
                        "Allow? Its programs stay as they are."
                    } else {
                        "Allow"
                    },
                    None,
                    locked,
                    armed,
                    gamepad_focus,
                    move |st, tx| {
                        if st.arm_game_confirm("gp-compress-allow", tx) {
                            st.compress_game_act(&gid, "allow", Some(&ac));
                        }
                    },
                ));
            }
        }
        _ => {
            let armed = s.is_confirm_armed("gp-compress-start");
            let q = match c.size.filter(|&b| b > 0) {
                Some(sz) => format!("Compress {}?", format_size(sz)),
                None => "Compress it?".into(),
            };
            controls = controls.child(props_btn(
                "gp-compress-start",
                if armed { &q } else { "Compress" },
                None,
                locked,
                armed,
                gamepad_focus,
                move |st, tx| {
                    if st.arm_game_confirm("gp-compress-start", tx) {
                        st.compress_game_act(&gid, "compress", None);
                    }
                },
            ));
        }
    }

    Some(props_row(
        "Compress",
        Some(&detail),
        note.as_ref(),
        None,
        controls,
    ))
}

fn files_section(s: &AppState, game: &Game, props: &GameProperties) -> Vec<El> {
    let mut rows = Vec::new();
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let locked = props.running;
    let busy = s.game_page.props_busy.as_deref();
    let size_bytes = props.size_bytes.or(game.size_bytes);
    let size_text = size_bytes.filter(|&b| b > 0).map(format_size);

    let active_prog = transfer_for_game(s.install_progress.as_ref(), game);
    let host_locked = s.game_page.primary_busy || active_prog.is_some();

    let added_here = game.store == "local";
    let uninst_armed = s.is_confirm_armed("gp-uninstall");
    let uninst_question = if added_here {
        "Remove? Files stay".to_string()
    } else if let Some(ref sz) = size_text {
        format!("Uninstall? {sz}")
    } else {
        "Uninstall?".to_string()
    };
    let uninst_label = if uninst_armed {
        uninst_question
    } else if added_here {
        "Remove".to_string()
    } else {
        "Uninstall".to_string()
    };
    let gid_uninst = game.id.clone();
    let uninst_disabled = host_locked || game.can_stop || busy == Some("uninstall");
    let uninstall_btn = props_danger_btn(
        "gp-uninstall",
        &uninst_label,
        uninst_disabled,
        uninst_armed,
        gamepad_focus,
        move |st, tx| {
            st.input_mode = InputMode::Pointer;
            if st.arm_game_confirm("gp-uninstall", tx) {
                st.uninstall_game_page(&gid_uninst);
            }
        },
    );

    let mut size_right = row().gap(8.0).center_y().child(
        text(size_text.clone().unwrap_or_else(|| "Unknown".into()))
            .font(type_small())
            .color(hex(0xdcdcdc, 1.0)),
    );
    if props.can_browse {
        let gid_folder = game.id.clone();
        size_right = size_right.child(props_btn(
            "gp-props-browse",
            "Open folder",
            None,
            false,
            false,
            gamepad_focus,
            move |st, _| st.open_game_folder_props(&gid_folder),
        ));
    }
    size_right = size_right.child(uninstall_btn);
    rows.push(props_row(
        "Size on disk",
        None,
        s.game_page.props_notes.get("openFolder"),
        None,
        size_right,
    ));

    if let Some(comp_el) = compress_row(s, game, locked) {
        rows.push(comp_el);
    }

    if let Some(ref verify_label) = props.verify_label
        && !verify_label.is_empty()
    {
        let gid_verify = game.id.clone();
        let armed = s.is_confirm_armed("gp-verify");
        let q = match size_text.as_deref() {
            Some(sz) => format!("Check all {sz}?"),
            None => "Check every file?".into(),
        };
        let label = if busy == Some("verify") {
            "Checking…".to_string()
        } else if armed {
            q
        } else {
            verify_label.clone()
        };
        rows.push(props_row(
            "Damaged or missing files",
            None,
            s.game_page.props_notes.get("verify"),
            None,
            props_btn(
                "gp-repair",
                &label,
                None,
                locked || busy.is_some(),
                armed,
                gamepad_focus,
                move |st, tx| {
                    if st.arm_game_confirm("gp-verify", tx) {
                        st.repair_game_props(&gid_verify);
                    }
                },
            ),
        ));
    }

    if let Some(shader_bytes) = props.shader_cache_bytes {
        let gid_sh = game.id.clone();
        let armed = s.is_confirm_armed("gp-rebuild-shaders");
        let state_txt = if shader_bytes > 0 {
            format_size(shader_bytes)
        } else {
            "Builds at next start".into()
        };
        let mut right = row()
            .gap(8.0)
            .center_y()
            .child(text(state_txt).font(type_small()).color(fg_muted()));
        if busy == Some("shaders") {
            right = right.child(text("Clearing…").font(type_small()).color(fg_muted()));
        }
        right = right.child(props_btn(
            "gp-rebuild-shaders",
            if armed {
                "Rebuild? The next start takes minutes"
            } else {
                "Rebuild shaders"
            },
            None,
            locked || busy.is_some() || shader_bytes == 0,
            armed,
            gamepad_focus,
            move |st, tx| {
                if st.arm_game_confirm("gp-rebuild-shaders", tx) {
                    st.set_game_property_ex(&gid_sh, "shaders", "", None, "shaders");
                }
            },
        ));
        rows.push(props_row(
            "Shaders",
            None,
            s.game_page.props_notes.get("shaders"),
            None,
            right,
        ));
    }

    if props.folder.is_some() && props.folder_choices.len() > 1 {
        let right = if let Some(ref m) = props.moving {
            let share = moved_share(m.done, m.total);
            let label = match share {
                Some(v) => format!("Moving · {}%", (v * 100.0).round() as u64),
                None => "Moving…".into(),
            };
            row()
                .gap(8.0)
                .center_y()
                .child(text(label).font(type_small()).color(fg_muted()))
                .child(
                    el().w(84.0)
                        .child(progress_bar(84.0, share.unwrap_or(0.0) as f32)),
                )
        } else {
            let cur = props.folder.as_deref().unwrap_or("");
            let display = props
                .folder_choices
                .iter()
                .find(|c| c.value == cur)
                .map(|c| match c.detail.as_deref() {
                    Some(d) if !d.is_empty() => format!("{} · {d}", c.label),
                    _ => c.label.clone(),
                })
                .unwrap_or_else(|| "Set elsewhere".into());
            let mut r = row().gap(8.0).center_y();
            if let Some(ref p) = s.game_page.pending_choice
                && p.row == "folder"
            {
                r = r.child(pending_confirm_pill(p, game.id.clone(), gamepad_focus));
            }
            r.child(select_trigger(
                SelectTrigger::new("gp-folder-select", "Library folder", display)
                    .open(s.open_dropdown == Some(OpenDropdown::GameFolder))
                    .disabled(locked || busy.is_some())
                    .flat(true)
                    .gamepad_focus(gamepad_focus),
                |st: &mut AppState, _| st.toggle_dropdown(OpenDropdown::GameFolder),
            ))
        };
        rows.push(props_row(
            "Library folder",
            None,
            s.game_page.props_notes.get("folder"),
            None,
            right,
        ));
    }

    let uview = upscaler_view(s.game_page.dlss.as_ref());
    let gate = gate_reason(game.can_stop, active_prog.is_some());
    let restorable = uview.restorable > 0 && gate.is_none();

    if let Some(ref ac_name) = uview.anti_cheat_name
        && matches!(uview.consent.as_deref(), Some("available" | "given"))
    {
        let given = uview.consent.as_deref() == Some("given");
        let gid_c = game.id.clone();
        let mut right = row().gap(8.0).center_y();
        if busy == Some("consent") {
            right = right.child(text("Saving…").font(type_small()).color(fg_muted()));
        }
        right = right.child(
            text(if given { "Allowed" } else { "Not allowed" })
                .font(type_small())
                .color(fg_muted()),
        );
        if given {
            right = right.child(props_btn(
                "gp-upscaler-stop-allow",
                "Stop allowing",
                None,
                busy.is_some(),
                false,
                gamepad_focus,
                move |st, _| {
                    if restorable {
                        st.toggle_dropdown(OpenDropdown::GameUpscalerStop);
                    } else {
                        st.dlss_consent_game(&gid_c, false, false);
                    }
                },
            ));
        } else {
            let armed = s.is_confirm_armed("gp-upscaler-consent");
            let q = ban_question(ac_name);
            right = right.child(props_btn(
                "gp-upscaler-consent",
                if armed { &q } else { "Allow changes" },
                None,
                busy.is_some(),
                armed,
                gamepad_focus,
                move |st, tx| {
                    if st.arm_game_confirm("gp-upscaler-consent", tx) {
                        st.dlss_consent_game(&gid_c, true, false);
                    }
                },
            ));
        }
        rows.push(props_row(
            "Upscaler changes",
            None,
            s.game_page.props_notes.get("consent"),
            None,
            right,
        ));
    }

    if uview.restorable > 0 {
        let gid_r = game.id.clone();
        let mut right = row().gap(8.0).center_y();
        if busy == Some("restore") {
            right = right.child(text("Restoring…").font(type_small()).color(fg_muted()));
        }
        right = right.child(props_btn(
            "gp-upscaler-restore",
            "Restore originals",
            None,
            gate.is_some() || busy.is_some(),
            false,
            gamepad_focus,
            move |st, _| st.dlss_restore_game(&gid_r),
        ));
        rows.push(props_row(
            "Upscaler files",
            gate,
            s.game_page.props_notes.get("restore"),
            None,
            right,
        ));
    }

    rows
}

fn betas_section(s: &AppState, game: &Game, props: &GameProperties) -> Vec<El> {
    let mut rows = Vec::new();
    let Some(cur_beta) = props.beta.as_deref() else {
        return rows;
    };
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let locked = props.running;
    let busy = s.game_page.props_busy.as_deref();

    let label = props
        .beta_choices
        .iter()
        .find(|c| c.value == cur_beta)
        .map(|c| c.label.as_str())
        .unwrap_or("Set elsewhere");
    let mut beta_right = row().gap(8.0).center_y();
    if busy == Some("beta") {
        beta_right = beta_right.child(text("Switching…").font(type_small()).color(fg_muted()));
    }
    if let Some(ref p) = s.game_page.pending_choice
        && p.row == "beta"
    {
        beta_right = beta_right.child(pending_confirm_pill(p, game.id.clone(), gamepad_focus));
    }
    beta_right = beta_right.child(select_trigger(
        SelectTrigger::new("gp-beta-select", "Beta", label)
            .open(s.open_dropdown == Some(OpenDropdown::GameBeta))
            .disabled(locked || busy.is_some())
            .flat(true)
            .gamepad_focus(gamepad_focus),
        |st: &mut AppState, _| st.toggle_dropdown(OpenDropdown::GameBeta),
    ));
    rows.push(props_row(
        "Beta",
        None,
        s.game_page.props_notes.get("beta"),
        None,
        beta_right,
    ));

    if props.beta_code {
        let gid_submit = game.id.clone();
        let gid_click = game.id.clone();
        let has_code = !s.game_page.beta_code.text.trim().is_empty();
        let code_input = row()
            .w(220.0)
            .h(32.0)
            .px(12.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_still_control_fill())
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .cursor(Cursor::Text)
            .child(
                text_input(&s.game_page.beta_code, type_body())
                    .key("gp-beta-code-input")
                    .label("Access code")
                    .w_full()
                    .single_line()
                    .secure()
                    .color(FG)
                    .placeholder("", fg_muted())
                    .on_edit(edit_access(|st: &mut AppState| &mut st.game_page.beta_code))
                    .on_submit(move |st: &mut AppState, _| {
                        let code = st.game_page.beta_code.text.trim().to_string();
                        if !code.is_empty() {
                            st.set_game_property_ex(
                                &gid_submit,
                                "betaCode",
                                "",
                                Some(&code),
                                "betaCode",
                            );
                        }
                    }),
            );
        let check_btn = props_btn(
            "gp-beta-code-check",
            if busy == Some("betaCode") {
                "Checking…"
            } else {
                "Check"
            },
            None,
            locked || busy.is_some() || !has_code,
            false,
            gamepad_focus,
            move |st, _| {
                let code = st.game_page.beta_code.text.trim().to_string();
                if !code.is_empty() {
                    st.set_game_property_ex(&gid_click, "betaCode", "", Some(&code), "betaCode");
                }
            },
        );
        rows.push(props_row(
            "Access code",
            None,
            s.game_page.props_notes.get("betaCode"),
            None,
            row().gap(8.0).center_y().child(code_input).child(check_btn),
        ));
    }

    rows
}

fn content_section(s: &AppState, game: &Game, props: &GameProperties) -> Vec<El> {
    let mut rows = Vec::new();
    let Some(ref list) = props.content else {
        return rows;
    };
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let locked = props.running;
    let busy = s.game_page.props_busy.as_deref();

    for item in list {
        let row_key = format!("content:{}", item.id);
        let detail = if item.toggle {
            item.size_bytes.filter(|&b| b > 0).map(format_size)
        } else {
            None
        };
        let right = if item.toggle {
            let pending_this = s
                .game_page
                .pending_choice
                .as_ref()
                .filter(|p| p.row == row_key);
            let checked = if pending_this.is_some() {
                !item.enabled
            } else {
                item.enabled
            };
            let mut r = row().gap(8.0).center_y();
            if busy == Some(row_key.as_str()) {
                r = r.child(text("Changing…").font(type_small()).color(fg_muted()));
            }
            if let Some(p) = pending_this {
                r = r.child(pending_confirm_pill(p, game.id.clone(), gamepad_focus));
            }
            let item_id = item.id.clone();
            let item_enabled = item.enabled;
            let item_size = item.size_bytes;
            let row_key_cb = row_key.clone();
            let mut sw =
                switch_pill_owned(format!("gp-dlc-{}", item.id), item.name.clone(), checked);
            if !(locked || busy.is_some()) {
                sw = sw.on_press(move |st: &mut AppState| {
                    let already = st
                        .game_page
                        .pending_choice
                        .as_ref()
                        .is_some_and(|p| p.row == row_key_cb);
                    if already {
                        st.game_page.pending_choice = None;
                    } else {
                        let next_on = !item_enabled;
                        st.game_page.pending_choice = Some(PendingChoice {
                            row: row_key_cb.clone(),
                            key: "content".into(),
                            value: format!("{item_id}:{}", if next_on { "on" } else { "off" }),
                            question: if item_enabled {
                                "Remove it?".into()
                            } else {
                                download_question(item_size)
                            },
                            danger: item_enabled,
                        });
                    }
                });
            }
            r.child(sw)
        } else if let Some(ref state_txt) = item.state
            && !state_txt.is_empty()
        {
            text(state_txt.clone()).font(type_small()).color(fg_muted())
        } else {
            el()
        };

        rows.push(props_row(
            &item.name,
            detail.as_deref(),
            s.game_page.props_notes.get(&row_key),
            None,
            right,
        ));
    }

    rows
}

pub fn properties_card(s: &AppState, game: &Game) -> El {
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let props = s.game_props.as_ref();

    let mut head = row()
        .w_full()
        .pt(18.0)
        .px(20.0)
        .pb(15.0)
        .justify_between()
        .center_y()
        .child(
            text("Properties")
                .font(type_title().letter_spacing(-0.015))
                .color(FG),
        );
    if props.is_some_and(|p| p.running) {
        head = head.child(status_badge("Playing".to_string(), BadgeTone::Good));
    }

    let mut card = col()
        .key("gp-props-card")
        .grow(1.0)
        .min_w(340.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .liquid_glass(LiquidGlass::panel())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(head);

    let Some(props) = props else {
        let body = if let Some(ref err) = s.game_page.props_load_error {
            row()
                .w_full()
                .py(14.0)
                .px(20.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .border_sides([1.0, 0.0, 0.0, 0.0], hairline())
                .child(text(err.clone()).font(type_small()).color(bad()))
                .child(props_btn(
                    "gp-props-retry",
                    "Try again",
                    None,
                    false,
                    false,
                    gamepad_focus,
                    |st, _| st.reload_game_properties(),
                ))
        } else {
            el().w_full()
                .py(18.0)
                .px(20.0)
                .border_sides([1.0, 0.0, 0.0, 0.0], hairline())
                .child(
                    text("Reading properties…")
                        .font(type_small())
                        .color(fg_muted()),
                )
        };
        return card.child(body);
    };

    let sections = sections_for(props);
    let active_section = s
        .game_page
        .props_tab_id
        .filter(|id| sections.iter().any(|(sid, _)| sid == id))
        .or_else(|| sections.get(s.game_props_tab).map(|(sid, _)| *sid))
        .unwrap_or_else(|| default_section_id(&sections));
    let active_idx = sections
        .iter()
        .position(|(sid, _)| *sid == active_section)
        .unwrap_or(0);

    let tab_bar =
        row()
            .px(20.0)
            .pb(12.0)
            .gap(6.0)
            .flex_wrap()
            .children(sections.iter().enumerate().map(|(i, (sec_id, label))| {
                let active = i == active_idx;
                let sec_copy = *sec_id;
                let key = if i == 0 {
                    "gp-props".to_string()
                } else {
                    format!("gp-prop-tab-{i}")
                };
                let base = row().py(6.0).px(12.0).center().child(
                    text(label.clone())
                        .font(TextStyle::new(scaled(12.0), 600.0))
                        .color(hex(0xdcdcdc, 1.0)),
                );
                let mut btn = el()
                    .key(key)
                    .role("tab")
                    .label(label.clone())
                    .cursor(Cursor::Pointer)
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .press_scale(PRESS_SCALE)
                    .radius(RADIUS_PILL)
                    .fill(if active { TRANSPARENT } else { FG.alpha(0.03) })
                    .hover(if active { TRANSPARENT } else { hover_fill() })
                    .press(if active { TRANSPARENT } else { pressed_fill() })
                    .motion(SPRING_QUICK)
                    .on_press(move |st: &mut AppState| {
                        st.input_mode = InputMode::Pointer;
                        st.select_props_section(sec_copy, i);
                    })
                    .child(base);
                if !active {
                    btn = btn.border_gradient(1.0, glass_rim_top(), glass_rim_bottom());
                }
                if active {
                    btn = btn.child(
                        row()
                            .key("gp-props-pill")
                            .matched("gp-props-pill")
                            .matched_crop()
                            .motion(SPRING_MOVE)
                            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                            .py(6.0)
                            .px(12.0)
                            .center()
                            .radius(RADIUS_PILL)
                            .fill(FG)
                            .child(
                                text(label.clone())
                                    .font(TextStyle::new(scaled(12.0), 600.0))
                                    .color(PRIMARY_FG),
                            ),
                    );
                }
                if gamepad_focus {
                    btn = btn.focus_ring(2.0, 3.0, FG);
                }
                btn
            }));

    card = card.child(tab_bar);

    let section_rows = match active_section {
        PropsSectionId::General => general_section(s, game, props),
        PropsSectionId::Files => files_section(s, game, props),
        PropsSectionId::Betas => betas_section(s, game, props),
        PropsSectionId::Content => content_section(s, game, props),
        PropsSectionId::Artwork => vec![props_row(
            "Cover",
            None,
            None,
            None,
            artwork_tools(s, game, false),
        )],
    };

    let body = col()
        .key(format!("gp-props-body-{active_idx}"))
        .w_full()
        .children(section_rows);

    card.child(body)
}

pub fn dropdown_overlay(s: &AppState, which: OpenDropdown, blur: bool) -> Vec<El> {
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let dismiss: SelectDismissCb<AppState> = Rc::new(|st: &mut AppState, _| {
        st.close_dropdown();
    });
    let game_id = s.current_open_game_id().unwrap_or_default();

    match which {
        OpenDropdown::GameDisplayMode => {
            let current = s
                .game_page
                .settings
                .as_ref()
                .and_then(|gs| gs.window_mode.clone())
                .unwrap_or_else(|| s.game_display_mode.clone());
            select_menu_layers(SelectMenuSpec {
                id: "dd-game-display".into(),
                trigger_key: "gp-display-mode-select".into(),
                selected: current,
                options: vec![
                    SelectOption::new("borderless", "Borderless"),
                    SelectOption::new("fullscreen", "Fullscreen"),
                ],
                width: 180.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(|st: &mut AppState, val: &str, _: &mut Tx| {
                    st.set_game_window_mode(val);
                }),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::GameLanguage => {
            let current = s
                .game_props
                .as_ref()
                .and_then(|p| p.language.clone())
                .unwrap_or_default();
            let choices = s
                .game_props
                .as_ref()
                .map(|p| p.language_choices.clone())
                .unwrap_or_default();
            let options: Vec<SelectOption> = choices
                .iter()
                .map(|c| SelectOption::new(c.value.clone(), c.label.clone()))
                .collect();
            select_menu_layers(SelectMenuSpec {
                id: "dd-game-language".into(),
                trigger_key: "gp-language-select".into(),
                selected: current.clone(),
                options,
                width: 210.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(move |st: &mut AppState, val: &str, _: &mut Tx| {
                    if game_id.is_empty() || val == current {
                        return;
                    }
                    let size = choices
                        .iter()
                        .find(|c| c.value == val)
                        .and_then(|c| c.size_bytes);
                    if st.status == HostStatus::Standalone && size.is_none() {
                        st.set_game_property_ex(&game_id, "language", val, None, "language");
                    } else {
                        st.game_page.pending_choice = Some(PendingChoice {
                            row: "language".into(),
                            key: "language".into(),
                            value: val.to_string(),
                            question: download_question(size),
                            danger: false,
                        });
                    }
                }),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::GameHdrMode => {
            let current = s
                .game_page
                .hdr_game
                .as_ref()
                .and_then(|h| h.mode.clone())
                .unwrap_or_else(|| "auto".into());
            select_menu_layers(SelectMenuSpec {
                id: "dd-game-hdr".into(),
                trigger_key: "gp-hdr-select".into(),
                selected: current,
                options: vec![
                    SelectOption::new("auto", "Auto"),
                    SelectOption::new("on", "Always"),
                    SelectOption::new("off", "Never"),
                ],
                width: 160.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(move |st: &mut AppState, val: &str, _: &mut Tx| {
                    if !game_id.is_empty() {
                        st.set_game_hdr_mode(&game_id, val);
                    }
                }),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::GameIntelFrameGen => {
            let current = s
                .game_page
                .gpu_game
                .as_ref()
                .and_then(|g| g.frame_gen.clone())
                .unwrap_or_else(|| "game".into());
            select_menu_layers(SelectMenuSpec {
                id: "dd-game-intel-fg".into(),
                trigger_key: "gp-intel-fg-select".into(),
                selected: current,
                options: vec![
                    SelectOption::new("game", "Game’s choice"),
                    SelectOption::new("2x", "2x"),
                    SelectOption::new("3x", "3x"),
                    SelectOption::new("4x", "4x"),
                ],
                width: 180.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(move |st: &mut AppState, val: &str, _: &mut Tx| {
                    if !game_id.is_empty() {
                        st.set_game_intel_frame_gen(&game_id, val);
                    }
                }),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::GameBeta => {
            let current = s
                .game_props
                .as_ref()
                .and_then(|p| p.beta.clone())
                .unwrap_or_default();
            let options: Vec<SelectOption> = s
                .game_props
                .as_ref()
                .map(|p| {
                    p.beta_choices
                        .iter()
                        .map(|c| SelectOption::new(c.value.clone(), c.label.clone()))
                        .collect()
                })
                .unwrap_or_default();
            select_menu_layers(SelectMenuSpec {
                id: "dd-game-beta".into(),
                trigger_key: "gp-beta-select".into(),
                selected: current.clone(),
                options,
                width: 220.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(move |st: &mut AppState, val: &str, _: &mut Tx| {
                    if game_id.is_empty() || val == current {
                        return;
                    }
                    st.game_page.pending_choice = Some(PendingChoice {
                        row: "beta".into(),
                        key: "beta".into(),
                        value: val.to_string(),
                        question: if val.is_empty() {
                            "Download the release?".into()
                        } else {
                            "Download its build?".into()
                        },
                        danger: false,
                    });
                }),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::GameFolder => {
            let current = s
                .game_props
                .as_ref()
                .and_then(|p| p.folder.clone())
                .unwrap_or_default();
            let size_bytes = s.game_props.as_ref().and_then(|p| p.size_bytes);
            let options: Vec<SelectOption> = s
                .game_props
                .as_ref()
                .map(|p| {
                    p.folder_choices
                        .iter()
                        .map(|c| {
                            let label = match c.detail.as_deref() {
                                Some(d) if !d.is_empty() => format!("{} · {d}", c.label),
                                _ => c.label.clone(),
                            };
                            SelectOption::new(c.value.clone(), label)
                        })
                        .collect()
                })
                .unwrap_or_default();
            select_menu_layers(SelectMenuSpec {
                id: "dd-game-folder".into(),
                trigger_key: "gp-folder-select".into(),
                selected: current.clone(),
                options,
                width: 260.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(move |st: &mut AppState, val: &str, _: &mut Tx| {
                    if game_id.is_empty() || val == current {
                        return;
                    }
                    let q = match size_bytes.filter(|&b| b > 0) {
                        Some(sz) => format!("Move {}?", format_size(sz)),
                        None => "Move the game?".into(),
                    };
                    st.game_page.pending_choice = Some(PendingChoice {
                        row: "folder".into(),
                        key: "folder".into(),
                        value: val.to_string(),
                        question: q,
                        danger: false,
                    });
                }),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::GameAntiCheat => {
            let current = s
                .game_props
                .as_ref()
                .and_then(|p| p.anti_cheat_launch.as_ref())
                .map(|a| a.value.clone())
                .unwrap_or_else(|| "with".into());
            select_menu_layers(SelectMenuSpec {
                id: "dd-game-anticheat".into(),
                trigger_key: "gp-anticheat-select".into(),
                selected: current.clone(),
                options: vec![
                    SelectOption::new("with", "Launch with anti-cheat"),
                    SelectOption::new("without", "Launch without anti-cheat (offline)"),
                ],
                width: 260.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(move |st: &mut AppState, val: &str, _: &mut Tx| {
                    if game_id.is_empty() || val == current {
                        return;
                    }
                    if val == "without" {
                        st.game_page.pending_choice = Some(PendingChoice {
                            row: "antiCheatLaunch".into(),
                            key: "antiCheatLaunch".into(),
                            value: val.to_string(),
                            question: "Play offline only?".into(),
                            danger: false,
                        });
                    } else {
                        st.set_game_property_ex(
                            &game_id,
                            "antiCheatLaunch",
                            val,
                            None,
                            "antiCheatLaunch",
                        );
                    }
                }),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::GameUpscalerStop => select_menu_layers(SelectMenuSpec {
            id: "dd-game-upscaler-stop".into(),
            trigger_key: "gp-upscaler-stop-allow".into(),
            selected: String::new(),
            options: vec![
                SelectOption::new("stop", "Stop allowing"),
                SelectOption::new("restore", "Stop and restore"),
            ],
            width: 190.0,
            align: Align::End,
            blur,
            gamepad_focus,
            on_select: Rc::new(move |st: &mut AppState, val: &str, _: &mut Tx| {
                if !game_id.is_empty() {
                    st.dlss_consent_game(&game_id, false, val == "restore");
                }
            }),
            on_dismiss: dismiss,
        }),
        _ => Vec::new(),
    }
}
