use crate::pc_state::{
    AppsSortBy, AppsSubTab, RemovedAppEntry, format_bytes, is_unused_app, leftover_cleared_line,
    leftover_said, today_epoch_days,
};
use crate::pc_wp9_actions::UndoTarget;
use crate::state::AppState;
use exo_kit::{
    BG, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK,
    TRANSPARENT, bad, fg_muted, float_line, glass_rim_bottom, glass_rim_top, good, hex, hover_fill,
    icon_check, icon_chevron_down, icon_chevron_up, icon_search, pressed_fill, scaled,
    switch_pill_keyed, type_body, type_control, type_section, type_small, warn,
};
use exo_rpc::{
    AppFind, AppLeftover, InstalledAppItem, LeftoverSummary, StartupItem, StartupRemovedItem,
};
use rune::{
    Color, Cursor, El, Key, ScrollSpec, SurfaceFit, TextStyle, Transition, col, edit_access, el,
    row, text, text_input,
};

const JUNK_WORTH: u64 = 100_000_000;
const CACHE_WORTH: u64 = 50_000_000;

fn apps_secondary() -> Color {
    hex(0xdcdcdc, 1.0)
}

fn apps_danger_fg() -> Color {
    hex(0xff7979, 1.0)
}

fn apps_pill(key: &'static str, label: &'static str, active: bool) -> El {
    el().key(key)
        .role("tab")
        .label(label)
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(28.0)
        .px(12.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(if active { FG } else { TRANSPARENT })
        .hover(if active { FG } else { hover_fill() })
        .press(if active { FG } else { pressed_fill() })
        .motion(SPRING_QUICK)
        .child(text(label).font(type_control()).color(if active {
            PRIMARY_FG
        } else {
            apps_secondary()
        }))
}

fn flat_btn(
    key: String,
    label: impl Into<String>,
    primary: bool,
    danger: bool,
    disabled: bool,
) -> El {
    let lbl = label.into();
    let (fill, hover_bg, press_bg, fg) = if danger {
        (
            FG.alpha(0.03),
            hover_fill(),
            pressed_fill(),
            apps_danger_fg(),
        )
    } else if primary {
        (FG, FG.alpha(0.90), FG.alpha(0.82), PRIMARY_FG)
    } else {
        (FG.alpha(0.03), hover_fill(), pressed_fill(), FG)
    };
    let btn = el()
        .key(key)
        .role("button")
        .label(lbl.clone())
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .focusable(!disabled)
        .disabled(disabled)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(26.0)
        .px(10.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(fill)
        .hover(hover_bg)
        .press(press_bg)
        .motion(SPRING_QUICK)
        .child(text(lbl).font(type_control()).color(fg));
    if primary {
        btn.border(1.0, FG)
    } else {
        btn.border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
    }
}

fn names_summary(list: &[String]) -> String {
    let mut unique: Vec<&str> = Vec::new();
    for item in list {
        if !item.is_empty() && !unique.contains(&item.as_str()) {
            unique.push(item.as_str());
        }
    }
    match unique.len() {
        0 => String::new(),
        1 => unique[0].to_string(),
        2 => format!("{} and {}", unique[0], unique[1]),
        3 => format!("{}, {} and {}", unique[0], unique[1], unique[2]),
        n => format!("{}, {} and {} more", unique[0], unique[1], n - 2),
    }
}

fn is_system_part(app: &InstalledAppItem) -> bool {
    !app.removable || app.care.as_deref() == Some("runtime")
}

fn is_pickable(app: &InstalledAppItem) -> bool {
    app.removable && app.uninstaller && app.replaces.is_none()
}

fn summarize_leftovers(items: &[AppLeftover]) -> LeftoverSummary {
    let mut out = LeftoverSummary::default();
    for item in items {
        match item.kind.as_str() {
            "folder" => out.folders += 1,
            "file" => out.files += 1,
            "shortcut" => out.shortcuts += 1,
            "registry" | "service" | "task" | "driver" => out.registry += 1,
            _ => {}
        }
        out.bytes = out.bytes.saturating_add(item.bytes);
    }
    out
}

fn find_detail(find: &AppFind, show_names: bool, waiting: bool) -> String {
    let whose = if show_names {
        let froms: Vec<String> = find
            .items
            .iter()
            .filter(|i| !i.from.is_empty())
            .map(|i| i.from.clone())
            .collect();
        names_summary(&froms)
    } else {
        String::new()
    };
    let what = leftover_said(&summarize_leftovers(&find.items), true);
    let unsure = find
        .items
        .iter()
        .filter(|i| i.certainty != "certain")
        .count();
    let mut notes: Vec<String> = Vec::new();
    for i in &find.items {
        if let Some(ref n) = i.note
            && !n.is_empty()
            && !notes.contains(n)
        {
            notes.push(n.clone());
        }
    }
    if unsure > 0 {
        notes.push(format!("{unsure} only likely or possible: not picked"));
    }
    let why = notes.join(" · ");
    let when = if waiting && find.items.iter().any(|i| i.selected && i.machine) {
        "Goes with your next uninstall, or now"
    } else {
        ""
    };
    let mut parts = Vec::new();
    if !whose.is_empty() {
        parts.push(format!("{whose}: {what}"));
    } else if !what.is_empty() {
        parts.push(what);
    }
    if !why.is_empty() {
        parts.push(why);
    }
    if !when.is_empty() {
        parts.push(when.into());
    }
    parts.join(" · ")
}

fn ready_row(
    title: String,
    detail: Option<String>,
    done: bool,
    error: Option<String>,
    actions: El,
) -> El {
    col()
        .w_full()
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(
            row()
                .w_full()
                .min_h(60.0)
                .pt(11.0)
                .pb(9.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(5.0)
                        .child(
                            text(title)
                                .font(type_control())
                                .color(if done { good() } else { FG })
                                .wrap(),
                        )
                        .children(
                            detail
                                .map(|d| text(d).font(type_small()).color(apps_secondary()).wrap()),
                        )
                        .children(error.map(|e| text(e).font(type_small()).color(bad()).wrap())),
                )
                .child(actions),
        )
}

fn defaults_row(name: &str, types: &[String], key_prefix: &str) -> Option<El> {
    if types.is_empty() {
        return None;
    }
    let title = format!(
        "{} {} with {name}",
        types.len(),
        if types.len() == 1 {
            "file type still opens"
        } else {
            "file types still open"
        }
    );
    let detail = format!(
        "{} · Choose another app for {} in Default apps",
        types.join(", "),
        if types.len() == 1 { "it" } else { "each" }
    );
    Some(ready_row(
        title,
        Some(detail),
        false,
        None,
        flat_btn(
            format!("apps-defaults-{key_prefix}"),
            "Default apps",
            false,
            false,
            false,
        )
        .on_press(|s: &mut AppState| s.open_default_apps()),
    ))
}

fn find_entry_rows(entry: &RemovedAppEntry, is_tidy: bool, busy: bool) -> El {
    let mut wrap = col().w_full();
    let entry_id_opt = (!is_tidy).then(|| entry.id.clone());
    let key_pfx = if is_tidy {
        "tidy".to_string()
    } else {
        entry.id.clone()
    };

    if entry.undone {
        return wrap.child(ready_row("Put back".into(), None, true, None, row()));
    }

    if let Some(ref line) = entry.cleared_line {
        let mut acts = row().gap(8.0).center_y();
        if let Some(ref tok) = entry.undo_token {
            let tok_c = tok.clone();
            let tgt = if is_tidy {
                UndoTarget::Tidy
            } else {
                UndoTarget::Removed(entry.id.clone())
            };
            acts = acts.child(
                flat_btn(
                    format!("apps-undo-{key_pfx}"),
                    "Put back",
                    false,
                    false,
                    false,
                )
                .on_press(move |s: &mut AppState| s.undo_app_cleanup(tgt.clone(), tok_c.clone())),
            );
        }
        wrap = wrap.child(ready_row(
            line.clone(),
            None,
            true,
            entry.error.clone(),
            acts,
        ));
        if let Some(ref f) = entry.find
            && let Some(def) = defaults_row(
                if is_tidy {
                    "apps that are gone"
                } else {
                    &f.name
                },
                &f.defaults,
                &key_pfx,
            )
        {
            wrap = wrap.child(def);
        }
        return wrap;
    }

    let Some(ref find) = entry.find else {
        let title = entry
            .message
            .clone()
            .unwrap_or_else(|| format!("{} is gone", entry.name));
        return wrap.child(ready_row(title, None, true, entry.error.clone(), row()));
    };

    if !is_tidy && find.auto {
        let went = find
            .cleared
            .as_ref()
            .filter(|c| c.folders + c.files + c.registry + c.shortcuts > 0);
        let line = if let Some(w) = went {
            format!("{} is gone. {}", entry.name, leftover_cleared_line(w))
        } else if !find.items.is_empty() {
            format!("{} is gone.", entry.name)
        } else {
            format!("{} is gone, and left nothing behind.", entry.name)
        };
        let mut acts = row().gap(8.0).center_y();
        if let Some(ref tok) = find.undo {
            let tok_c = tok.clone();
            let rid = entry.id.clone();
            acts = acts.child(
                flat_btn(
                    format!("apps-undo-{key_pfx}"),
                    "Put back",
                    false,
                    false,
                    false,
                )
                .on_press(move |s: &mut AppState| {
                    s.undo_app_cleanup(UndoTarget::Removed(rid.clone()), tok_c.clone())
                }),
            );
        }
        wrap = wrap.child(ready_row(line, None, true, entry.error.clone(), acts));
        if find.items.is_empty() {
            if let Some(def) = defaults_row(&entry.name, &find.defaults, &key_pfx) {
                wrap = wrap.child(def);
            }
            return wrap;
        }
    }

    let title = if is_tidy {
        "Left by programs that are gone".into()
    } else if find.auto {
        if find.declined {
            "Windows’ permission was declined".into()
        } else {
            "Remaining files".into()
        }
    } else {
        format!("What’s left of {}", entry.name)
    };

    if find.items.is_empty() {
        wrap = wrap.child(ready_row(title, None, true, entry.error.clone(), row()));
        if let Some(def) = defaults_row(&entry.name, &find.defaults, &key_pfx) {
            wrap = wrap.child(def);
        }
        return wrap;
    }

    let chosen_count = find
        .items
        .iter()
        .filter(|i| entry.picks.contains(&i.id))
        .count();
    let mut acts = row().gap(6.0).center_y();
    if chosen_count > 0 {
        let eid = entry_id_opt.clone();
        acts = acts.child(
            flat_btn(
                format!("apps-clean-{key_pfx}"),
                format!("Clean up {chosen_count}"),
                true,
                false,
                busy,
            )
            .on_press(move |s: &mut AppState| s.clean_app_leftovers(eid.clone())),
        );
    }
    let eid_rev = entry_id_opt.clone();
    acts = acts.child(
        flat_btn(
            format!("apps-review-{key_pfx}"),
            if entry.review_open { "Hide" } else { "Review" },
            false,
            false,
            false,
        )
        .on_press(move |s: &mut AppState| {
            let target = match eid_rev.as_deref() {
                None => s.pc.apps_tidy.as_mut(),
                Some(id) => s.pc.apps_removed.iter_mut().find(|r| r.id == id),
            };
            if let Some(e) = target {
                e.review_open = !e.review_open;
            }
        }),
    );
    let eid_lv = entry_id_opt.clone();
    acts = acts.child(
        flat_btn(
            format!("apps-leave-{key_pfx}"),
            "Leave",
            false,
            false,
            false,
        )
        .on_press(move |s: &mut AppState| s.leave_app_leftovers(eid_lv.clone())),
    );

    wrap = wrap.child(ready_row(
        title,
        Some(find_detail(find, is_tidy, is_tidy)),
        false,
        entry.error.clone(),
        acts,
    ));

    if entry.review_open {
        let mut items_col = col().w_full().pl(16.0).pb(8.0).gap(4.0);
        for item in &find.items {
            let picked = entry.picks.contains(&item.id);
            let item_id = item.id;
            let eid_pick = entry_id_opt.clone();
            let mut note_parts: Vec<&str> = Vec::new();
            if !item.because.is_empty() {
                note_parts.push(&item.because);
            }
            if item.store {
                note_parts.push("With its driver store package, saved first");
            }
            if let Some(ref n) = item.note
                && !n.is_empty()
            {
                note_parts.push(n);
            }
            items_col = items_col.child(
                row()
                    .w_full()
                    .py(6.0)
                    .gap(10.0)
                    .center_y()
                    .child(
                        el().key(format!("apps-pick-{key_pfx}-{}", item.id))
                            .role("checkbox")
                            .label(format!("Clear {}", item.path))
                            .checked(picked)
                            .cursor(Cursor::Pointer)
                            .focusable(true)
                            .hit()
                            .size(18.0, 18.0)
                            .radius(4.0)
                            .center()
                            .fill(if picked { FG } else { BG })
                            .border(1.0, if picked { FG } else { float_line() })
                            .on_press(move |s: &mut AppState| {
                                let target = match eid_pick.as_deref() {
                                    None => s.pc.apps_tidy.as_mut(),
                                    Some(id) => s.pc.apps_removed.iter_mut().find(|r| r.id == id),
                                };
                                if let Some(e) = target
                                    && !e.picks.remove(&item_id)
                                {
                                    e.picks.insert(item_id);
                                }
                            })
                            .children(picked.then(|| icon_check(11.0, PRIMARY_FG))),
                    )
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .gap(1.0)
                            .child(
                                text(item.path.clone())
                                    .font(type_small())
                                    .color(FG)
                                    .truncate(),
                            )
                            .children((!note_parts.is_empty()).then(|| {
                                text(note_parts.join(" · "))
                                    .font(type_small())
                                    .color(fg_muted())
                                    .truncate()
                            })),
                    )
                    .children((item.bytes > 0).then(|| {
                        text(format_bytes(item.bytes))
                            .font(type_small())
                            .color(fg_muted())
                    })),
            );
        }
        wrap = wrap.child(items_col);
    }

    if let Some(def) = defaults_row(
        if is_tidy {
            "apps that are gone"
        } else {
            &find.name
        },
        &find.defaults,
        &key_pfx,
    ) {
        wrap = wrap.child(def);
    }
    wrap
}

fn ready_section(s: &AppState, programs: &[InstalledAppItem]) -> El {
    let busy = s.pc.apps_busy_id.is_some() || s.pc.apps_run.is_some();
    let armed = s.pc.armed_confirm.as_deref();
    let today = today_epoch_days();

    let mut sec = col().key("apps-ready-section").w_full();

    for entry in &s.pc.apps_removed {
        sec = sec.child(find_entry_rows(entry, false, busy));
    }

    for ac in &s.pc.apps_auto_cleared {
        let mut acts = row().gap(8.0).center_y();
        if !ac.undone {
            let id_c = ac.id.clone();
            let id_u = ac.id.clone();
            acts = acts.child(
                flat_btn(
                    format!("apps-ac-undo-{}", ac.id),
                    "Put back",
                    false,
                    false,
                    false,
                )
                .on_press(move |s: &mut AppState| {
                    s.undo_app_cleanup(UndoTarget::AutoCleared(id_c.clone()), id_u.clone())
                }),
            );
        }
        sec = sec.child(ready_row(
            if ac.undone {
                "Put back".into()
            } else {
                ac.line.clone()
            },
            None,
            true,
            ac.error.clone(),
            acts,
        ));
    }

    if let Some(ref tidy) = s.pc.apps_tidy {
        sec = sec.child(find_entry_rows(tidy, true, busy));
    }

    let replaced: Vec<&InstalledAppItem> = programs
        .iter()
        .filter(|a| a.replaces.is_some() && a.removable)
        .collect();
    if !replaced.is_empty() {
        let rep_names: Vec<String> = replaced.iter().map(|a| a.name.clone()).collect();
        let rep_bytes: u64 = replaced.iter().filter_map(|a| a.bytes).sum();
        let rep_ids: Vec<String> = replaced.iter().map(|a| a.id.clone()).collect();
        let is_armed = armed == Some("apps-replaced");
        let btn_lbl = if is_armed {
            format!("Remove {}?", replaced.len())
        } else {
            format!("Remove {}", replaced.len())
        };
        sec = sec.child(ready_row(
            format!("Exo does the job of {}", names_summary(&rep_names)),
            (rep_bytes > 0).then(|| format_bytes(rep_bytes)),
            false,
            None,
            flat_btn("apps-remove-replaced".into(), btn_lbl, false, true, busy).on_press(
                move |s: &mut AppState| {
                    if s.pc.armed_confirm.as_deref() == Some("apps-replaced") {
                        s.uninstall_many_apps(rep_ids.clone());
                    } else {
                        s.pc.armed_confirm = Some("apps-replaced".into());
                    }
                },
            ),
        ));
    }

    let bloat: Vec<&InstalledAppItem> = programs
        .iter()
        .filter(|a| a.bloat.is_some() && is_pickable(a))
        .collect();
    if !bloat.is_empty() {
        let b_names: Vec<String> = bloat.iter().map(|a| a.name.clone()).collect();
        let b_ids: Vec<String> = bloat.iter().map(|a| a.id.clone()).collect();
        let is_armed = armed == Some("apps-bloat");
        let btn_lbl = if is_armed {
            format!("Uninstall {}?", bloat.len())
        } else {
            format!("Remove {}", bloat.len())
        };
        sec = sec.child(ready_row(
            format!(
                "{} {}",
                bloat.len(),
                if bloat.len() == 1 {
                    "app you didn’t ask for"
                } else {
                    "apps you didn’t ask for"
                }
            ),
            Some(names_summary(&b_names)),
            false,
            None,
            flat_btn("apps-remove-bloat".into(), btn_lbl, false, true, busy).on_press(
                move |s: &mut AppState| {
                    if s.pc.armed_confirm.as_deref() == Some("apps-bloat") {
                        s.uninstall_many_apps(b_ids.clone());
                    } else {
                        s.pc.armed_confirm = Some("apps-bloat".into());
                    }
                },
            ),
        ));
    }

    let unneeded_items: Vec<String> =
        s.pc.startup_list
            .as_ref()
            .map(|st| {
                st.items
                    .iter()
                    .filter(|i| i.unneeded)
                    .map(|i| i.name.clone())
                    .collect()
            })
            .unwrap_or_default();
    let unneeded_count = unneeded_items.len()
        + s.pc
            .startup_list
            .as_ref()
            .map(|st| st.extras.max(0) as usize)
            .unwrap_or(0);
    if unneeded_count > 0 {
        sec = sec.child(ready_row(
            format!(
                "{unneeded_count} {} starting with Windows can go",
                if unneeded_count == 1 {
                    "thing"
                } else {
                    "things"
                }
            ),
            (!unneeded_items.is_empty()).then(|| names_summary(&unneeded_items)),
            false,
            s.pc.startup_errors.get("clean").cloned(),
            flat_btn(
                "apps-startup-clean".into(),
                format!("Turn off {unneeded_count}"),
                false,
                false,
                busy,
            )
            .on_press(|s: &mut AppState| s.startup_clean_unneeded()),
        ));
    }

    if let Some(ref junk) = s.pc.junk_items {
        let total: u64 = junk.iter().map(|i| i.bytes).sum();
        if let Some((Some(ref done_line), ref err)) = s.pc.junk_cleaned
            && total < JUNK_WORTH
        {
            sec = sec.child(ready_row(done_line.clone(), None, true, err.clone(), row()));
        } else if total >= JUNK_WORTH {
            let j_names: Vec<String> = junk
                .iter()
                .filter(|i| i.bytes > 0)
                .map(|i| i.name.to_lowercase())
                .collect();
            let junk_detail = (!j_names.is_empty()).then(|| {
                let s = names_summary(&j_names);
                let mut chars = s.chars();
                match chars.next() {
                    Some(first) => format!("{}{}", first.to_uppercase(), chars.as_str()),
                    None => s,
                }
            });
            let is_armed = armed == Some("junk-clean");
            let lbl = if is_armed {
                format!("Delete {}?", format_bytes(total))
            } else {
                "Clean".into()
            };
            sec = sec.child(ready_row(
                format!("{} of junk", format_bytes(total)),
                junk_detail,
                false,
                s.pc.junk_cleaned.as_ref().and_then(|(_, e)| e.clone()),
                flat_btn("apps-junk-clean".into(), lbl, false, true, busy).on_press(
                    |s: &mut AppState| {
                        if s.pc.armed_confirm.as_deref() == Some("junk-clean") {
                            s.clean_apps_junk();
                        } else {
                            s.pc.armed_confirm = Some("junk-clean".into());
                        }
                    },
                ),
            ));
        }
    }

    if let Some((ref line, ref undo_tok, undone)) = s.pc.installer_cache_done {
        let mut acts = row().gap(8.0).center_y();
        if !undone && let Some(tok) = undo_tok {
            let tok_c = tok.clone();
            acts = acts.child(
                flat_btn("apps-ic-undo".into(), "Put back", false, false, false).on_press(
                    move |s: &mut AppState| {
                        s.undo_app_cleanup(UndoTarget::InstallerCache, tok_c.clone())
                    },
                ),
            );
        }
        sec = sec.child(ready_row(
            if undone {
                "Put back".into()
            } else {
                line.clone()
            },
            None,
            true,
            s.pc.installer_cache_error.clone(),
            acts,
        ));
    } else if let Some(ref cache) = s.pc.installer_cache
        && cache.bytes >= CACHE_WORTH
    {
        let is_armed = armed == Some("inst-cache");
        let lbl = if is_armed {
            format!("Move {}?", format_bytes(cache.bytes))
        } else {
            "Clear".into()
        };
        sec = sec.child(ready_row(
            format!(
                "{} of Windows Installer files no app needs",
                format_bytes(cache.bytes)
            ),
            Some(format!(
                "{} {} of removed installers · To the Recycle Bin",
                cache.files,
                if cache.files == 1 { "copy" } else { "copies" }
            )),
            false,
            s.pc.installer_cache_error.clone(),
            flat_btn("apps-inst-cache-clean".into(), lbl, false, true, busy).on_press(
                |s: &mut AppState| {
                    if s.pc.armed_confirm.as_deref() == Some("inst-cache") {
                        s.clean_installer_cache();
                    } else {
                        s.pc.armed_confirm = Some("inst-cache".into());
                    }
                },
            ),
        ));
    }

    let idle: Vec<&InstalledAppItem> = programs
        .iter()
        .filter(|a| is_unused_app(a, today))
        .collect();
    if !idle.is_empty() {
        let idle_bytes: u64 = idle.iter().filter_map(|a| a.bytes).sum();
        let idle_names: Vec<String> = idle.iter().map(|a| a.name.clone()).collect();
        sec = sec.child(ready_row(
            format!(
                "{} {} not opened in three months",
                idle.len(),
                if idle.len() == 1 { "app" } else { "apps" }
            ),
            Some(format!(
                "{}: {}",
                format_bytes(idle_bytes),
                names_summary(&idle_names)
            )),
            false,
            None,
            flat_btn("apps-show-unused".into(), "Show", false, false, false).on_press(
                |s: &mut AppState| {
                    s.pc.apps_only_unused = true;
                    s.pc.apps_sort_by = AppsSortBy::Size;
                    s.pc.apps_sort_back = false;
                },
            ),
        ));
    }

    sec
}

fn app_table_row(app: &InstalledAppItem, s: &AppState, show_last_used: bool) -> El {
    let id = app.id.clone();
    let is_armed =
        s.pc.armed_uninstall
            .as_ref()
            .is_some_and(|(armed_id, _)| armed_id == &app.id);
    let working = s.pc.apps_busy_id.as_deref() == Some(app.id.as_str());
    let disabled = (s.pc.apps_busy_id.is_some() && !working) || s.pc.apps_run.is_some();
    let select_mode = s.pc.apps_select_mode;
    let pickable = is_pickable(app);
    let picked = s.pc.apps_picked.contains(&app.id);
    let row_err = s.pc.apps_errors.get(&app.id);

    let leading = if select_mode && pickable {
        let id_pick = app.id.clone();
        el().key(format!("app-pick-{}", app.id))
            .role("checkbox")
            .label(format!("Select {}", app.name))
            .checked(picked)
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .size(28.0, 28.0)
            .center()
            .on_press(move |s: &mut AppState| {
                if !s.pc.apps_picked.remove(&id_pick) {
                    s.pc.apps_picked.insert(id_pick.clone());
                }
            })
            .child(
                el().size(18.0, 18.0)
                    .radius(4.0)
                    .center()
                    .fill(if picked { FG } else { BG })
                    .border(1.0, if picked { FG } else { float_line() })
                    .children(picked.then(|| icon_check(11.0, PRIMARY_FG))),
            )
    } else if let Some(img) = s.app_icon_image(&app.id) {
        el().size(28.0, 28.0)
            .radius(RADIUS_SMALL)
            .center()
            .fill(FG.alpha(0.06))
            .child(
                el().size(20.0, 20.0)
                    .pixels_fit(img.pixels(), SurfaceFit::Fit),
            )
    } else {
        let initial = app
            .name
            .chars()
            .next()
            .map(|c| c.to_string())
            .unwrap_or_default();
        el().size(28.0, 28.0)
            .radius(RADIUS_SMALL)
            .center()
            .fill(FG.alpha(0.06))
            .child(
                text(initial)
                    .font(TextStyle::new(scaled(12.0), 600.0))
                    .color(fg_muted()),
            )
    };

    let sub = app
        .kept
        .as_deref()
        .or(app.caution.as_deref())
        .or(app.publisher.as_deref())
        .unwrap_or("")
        .to_string();
    let size_txt = app.bytes.map(format_bytes).unwrap_or_else(|| "—".into());
    let last_used = app
        .quiet_since
        .as_deref()
        .map(|q| format!("Before {q}"))
        .or_else(|| app.last_opened.clone())
        .unwrap_or_default();

    let action_word = if app.replaces.is_some() {
        "Remove"
    } else if app.uninstaller {
        "Uninstall"
    } else {
        "Clean up"
    };

    let action_slot = if !app.removable || select_mode {
        el()
    } else if working {
        row()
            .w_full()
            .h_full()
            .justify_end()
            .center_y()
            .child(text("Removing…").font(type_small()).color(fg_muted()))
    } else if is_armed {
        let confirm_btn = el()
            .key(format!("app-confirm-{}", app.id))
            .role("button")
            .label(format!("Confirm uninstall {}", app.name))
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(26.0)
            .px(10.0)
            .center()
            .radius(RADIUS_PILL)
            .fill(bad())
            .border(1.0, bad())
            .hover(bad().alpha(0.90))
            .press(bad().alpha(0.80))
            .on_press(move |s: &mut AppState| s.arm_or_confirm_uninstall(&id))
            .on_key(|s: &mut AppState, ev, _| {
                if ev.key == Key::Escape && s.pc.armed_uninstall.is_some() {
                    s.disarm_uninstall();
                    true
                } else {
                    false
                }
            })
            .child(
                text("Confirm uninstall")
                    .font(type_control())
                    .color(PRIMARY_FG),
            );
        row()
            .key(format!("app-confirm-slot-{}", app.id))
            .absolute(0.0, 0.0)
            .w_full()
            .h_full()
            .pr(1.0)
            .justify_end()
            .center_y()
            .transition(Transition::opacity())
            .child(confirm_btn)
    } else {
        let uninstall_btn = el()
            .key(format!("app-uninst-{}", app.id))
            .role("button")
            .label(format!("{action_word} {}", app.name))
            .cursor(if disabled {
                Cursor::NotAllowed
            } else {
                Cursor::Pointer
            })
            .focusable(!disabled)
            .disabled(disabled)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(26.0)
            .px(10.0)
            .center()
            .radius(RADIUS_PILL)
            .fill(FG.alpha(0.03))
            .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
            .hover(hover_fill())
            .press(pressed_fill())
            .on_press(move |s: &mut AppState| s.arm_or_confirm_uninstall(&id))
            .child(
                text(action_word)
                    .font(type_control())
                    .color(apps_danger_fg()),
            );
        row()
            .key(format!("app-uninst-slot-{}", app.id))
            .absolute(0.0, 0.0)
            .w_full()
            .h_full()
            .pr(1.0)
            .justify_end()
            .center_y()
            .transition(Transition::opacity())
            .child(uninstall_btn)
    };

    let mut content_row = row()
        .w_full()
        .min_h(51.5)
        .py(6.0)
        .gap(12.0)
        .center_y()
        .hover_group()
        .hover_motion(SPRING_QUICK, SPRING_QUICK)
        .child(leading)
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .pt(1.0)
                .gap(6.0)
                .child(
                    text(app.name.clone())
                        .font(type_control())
                        .color(FG)
                        .truncate(),
                )
                .children((!sub.is_empty()).then(|| {
                    text(sub)
                        .font(type_small())
                        .color(apps_secondary())
                        .truncate()
                }))
                .children(row_err.map(|e| text(e.clone()).font(type_small()).color(bad()).wrap())),
        );
    if show_last_used {
        content_row = content_row.child(
            el().w(scaled(112.0)).child(
                text(last_used)
                    .font(type_small())
                    .color(apps_secondary())
                    .truncate(),
            ),
        );
    }
    content_row = content_row
        .child(
            row()
                .w(scaled(72.0))
                .pt(1.0)
                .justify_end()
                .child(text(size_txt).font(type_small()).color(apps_secondary())),
        )
        .child(el().w(scaled(108.0)).h(28.0).pt(2.0).child(action_slot));

    col()
        .key(format!("app-row-{}", app.id))
        .w_full()
        .min_h(52.5)
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(content_row)
}

fn sort_head_btn(key: &'static str, label: &'static str, by: AppsSortBy, s: &AppState) -> El {
    let active = s.pc.apps_sort_by == by;
    let mut inner = row().gap(4.0).center_y().child(
        text(label)
            .font(TextStyle::new(scaled(12.0), 500.0))
            .color(if active { FG } else { apps_secondary() }),
    );
    if active {
        inner = inner.child(if s.pc.apps_sort_back {
            icon_chevron_up(12.0, FG)
        } else {
            icon_chevron_down(12.0, FG)
        });
    }
    el().key(key)
        .role("button")
        .label(format!("Sort by {label}"))
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .on_press(move |s: &mut AppState| {
            if s.pc.apps_sort_by == by {
                s.pc.apps_sort_back = !s.pc.apps_sort_back;
            } else {
                s.pc.apps_sort_by = by;
                s.pc.apps_sort_back = false;
            }
        })
        .child(inner)
}

fn startup_detail(item: &StartupItem) -> String {
    if let Some(ref st) = item.status
        && !st.is_empty()
    {
        return st.clone();
    }
    if item.missing {
        return "Its program is gone: safe to turn off".into();
    }
    let word = match item.verdict.as_deref() {
        Some("remove") => Some("Safe to turn off"),
        Some("keep") => Some("Worth keeping"),
        _ => None,
    };
    if item.verdict.as_deref() == Some("remove") {
        return word.unwrap_or("").into();
    }
    if let Some(ref without) = item.without
        && !without.is_empty()
    {
        return without.clone();
    }
    if let Some(ref does) = item.does
        && !does.is_empty()
    {
        return does.clone();
    }
    if let Some(w) = word {
        return w.into();
    }
    if !item.command.is_empty() {
        return format!("Runs {}", item.command);
    }
    item.source.clone()
}

fn is_windows_startup_id(id: &str) -> bool {
    id.starts_with("run:")
        || id.starts_with("run32:")
        || id.starts_with("runonce:")
        || id.starts_with("runonce32:")
        || id.starts_with("folder:")
        || id.starts_with("package:")
}

fn format_turned_off_day(iso: &str) -> String {
    let date_part = iso.split('T').next().unwrap_or(iso);
    let mut parts = date_part.split('-');
    let (Some(y), Some(m), Some(d)) = (parts.next(), parts.next(), parts.next()) else {
        return format!("Turned off {date_part}");
    };
    let month = match m.parse::<u32>().ok() {
        Some(1) => "Jan",
        Some(2) => "Feb",
        Some(3) => "Mar",
        Some(4) => "Apr",
        Some(5) => "May",
        Some(6) => "Jun",
        Some(7) => "Jul",
        Some(8) => "Aug",
        Some(9) => "Sep",
        Some(10) => "Oct",
        Some(11) => "Nov",
        Some(12) => "Dec",
        _ => return format!("Turned off {date_part}"),
    };
    let day = d.parse::<u32>().unwrap_or(1);
    format!("Turned off {month} {day}, {y}")
}

fn startup_tag_pill(label: &str, tone: Option<&str>) -> El {
    let color = match tone {
        Some("good") => good(),
        Some("warn") => warn(),
        _ => fg_muted(),
    };
    el().px(8.0)
        .py(2.0)
        .radius(RADIUS_PILL)
        .fill(FG.alpha(0.06))
        .center()
        .child(
            text(label.to_string())
                .font(TextStyle::new(scaled(12.0), 500.0))
                .color(color),
        )
}

fn startup_icon_box(s: &AppState, name: &str, icon_key: Option<&str>) -> El {
    if let Some(key) = icon_key
        && let Some(img) = s.app_icon_image(key)
    {
        return el()
            .size(28.0, 28.0)
            .radius(RADIUS_SMALL)
            .center()
            .fill(FG.alpha(0.06))
            .child(
                el().size(20.0, 20.0)
                    .pixels_fit(img.pixels(), SurfaceFit::Fit),
            );
    }
    let initial = name
        .chars()
        .next()
        .map(|c| c.to_string())
        .unwrap_or_default();
    el().size(28.0, 28.0)
        .radius(RADIUS_SMALL)
        .center()
        .fill(FG.alpha(0.06))
        .child(
            text(initial)
                .font(TextStyle::new(scaled(12.0), 600.0))
                .color(apps_secondary()),
        )
}

struct GroupedStartupItem<'a> {
    item: &'a StartupItem,
    ids: Vec<String>,
}

struct GroupedStartupRemoved<'a> {
    item: &'a StartupRemovedItem,
    ids: Vec<String>,
}

struct StartupOffRow {
    id: String,
    ids: Vec<String>,
    name: String,
    icon: Option<String>,
    detail: String,
    amount: String,
    locked: bool,
}

fn group_startup_items<'a>(
    items: impl Iterator<Item = &'a StartupItem>,
) -> Vec<GroupedStartupItem<'a>> {
    let mut out: Vec<GroupedStartupItem<'a>> = Vec::new();
    let mut idx_by_name: std::collections::HashMap<&'a str, usize> =
        std::collections::HashMap::new();
    for item in items {
        if let Some(&idx) = idx_by_name.get(item.name.as_str()) {
            out[idx].ids.push(item.id.clone());
        } else {
            idx_by_name.insert(item.name.as_str(), out.len());
            out.push(GroupedStartupItem {
                item,
                ids: vec![item.id.clone()],
            });
        }
    }
    out
}

fn group_startup_removed<'a>(
    items: impl Iterator<Item = &'a StartupRemovedItem>,
) -> Vec<GroupedStartupRemoved<'a>> {
    let mut out: Vec<GroupedStartupRemoved<'a>> = Vec::new();
    let mut idx_by_name: std::collections::HashMap<&'a str, usize> =
        std::collections::HashMap::new();
    for item in items {
        if let Some(&idx) = idx_by_name.get(item.name.as_str()) {
            out[idx].ids.push(item.id.clone());
        } else {
            idx_by_name.insert(item.name.as_str(), out.len());
            out.push(GroupedStartupRemoved {
                item,
                ids: vec![item.id.clone()],
            });
        }
    }
    out
}

#[allow(clippy::too_many_arguments)]
fn startup_line_row(
    s: &AppState,
    key: String,
    name: &str,
    icon_key: Option<&str>,
    tag_label: &str,
    tag_tone: Option<&str>,
    detail: String,
    amount: String,
    error: Option<String>,
    control: El,
) -> El {
    let leading = startup_icon_box(s, name, icon_key);
    let title_row = row()
        .w_full()
        .gap(8.0)
        .center_y()
        .child(
            text(name.to_string())
                .font(type_control())
                .color(FG)
                .truncate(),
        )
        .child(startup_tag_pill(tag_label, tag_tone));
    let copy = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(5.0)
        .child(title_row)
        .children((!detail.is_empty()).then(|| {
            text(detail)
                .font(type_small())
                .color(apps_secondary())
                .truncate()
        }))
        .children(error.map(|e| text(e).font(type_small()).color(bad()).wrap()));

    col()
        .key(key)
        .w_full()
        .min_h(55.0)
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(
            row()
                .w_full()
                .min_h(54.0)
                .py(6.0)
                .gap(12.0)
                .center_y()
                .hover(hover_fill())
                .radius(RADIUS_SMALL)
                .child(leading)
                .child(copy)
                .child(
                    row()
                        .w(scaled(72.0))
                        .justify_end()
                        .child(text(amount).font(type_small()).color(apps_secondary())),
                )
                .child(
                    row()
                        .w(scaled(108.0))
                        .justify_end()
                        .center_y()
                        .child(control),
                ),
        )
}

fn startup_group_heading(title: String) -> El {
    row()
        .w_full()
        .h(26.0)
        .px(8.0)
        .pb(6.0)
        .center_y()
        .child(text(title).font(type_section()).color(FG))
}

fn startup_tab_body(s: &AppState, q: &str) -> El {
    let searching = !q.is_empty();
    let items =
        s.pc.startup_list
            .as_ref()
            .map(|l| l.items.as_slice())
            .unwrap_or(&[]);
    let removed_items =
        s.pc.startup_list
            .as_ref()
            .map(|l| l.removed.as_slice())
            .unwrap_or(&[]);

    let grouped_items = group_startup_items(items.iter().filter(|i| {
        if !searching {
            return true;
        }
        let hay = format!(
            "{} {} {}",
            i.name,
            i.publisher.as_deref().unwrap_or(""),
            i.command
        )
        .to_lowercase();
        hay.contains(q)
    }));
    let grouped_removed = group_startup_removed(
        removed_items
            .iter()
            .filter(|r| !searching || r.name.to_lowercase().contains(q)),
    );

    let mut broken: Vec<&GroupedStartupItem> = Vec::new();
    let mut on_items: Vec<&GroupedStartupItem> = Vec::new();
    let mut bg_on_items: Vec<&GroupedStartupItem> = Vec::new();
    let mut off_rows: Vec<StartupOffRow> = Vec::new();
    let mut bg_off_rows: Vec<StartupOffRow> = Vec::new();

    for g in &grouped_items {
        if g.item.missing {
            broken.push(g);
        } else if g.item.source == "task" {
            if g.item.enabled {
                bg_on_items.push(g);
            } else {
                bg_off_rows.push(StartupOffRow {
                    id: g.item.id.clone(),
                    ids: g.ids.clone(),
                    name: g.item.name.clone(),
                    icon: g.item.icon.clone(),
                    detail: startup_detail(g.item),
                    amount: if g.item.memory > 0 {
                        format_bytes(g.item.memory)
                    } else {
                        String::new()
                    },
                    locked: g.item.locked || g.item.read_only,
                });
            }
        } else if g.item.enabled {
            on_items.push(g);
        } else {
            off_rows.push(StartupOffRow {
                id: g.item.id.clone(),
                ids: g.ids.clone(),
                name: g.item.name.clone(),
                icon: g.item.icon.clone(),
                detail: startup_detail(g.item),
                amount: if g.item.memory > 0 {
                    format_bytes(g.item.memory)
                } else {
                    String::new()
                },
                locked: g.item.locked || g.item.read_only,
            });
        }
    }

    for g in &grouped_removed {
        let row = StartupOffRow {
            id: g.item.id.clone(),
            ids: g.ids.clone(),
            name: g.item.name.clone(),
            icon: g.item.icon.clone(),
            detail: format_turned_off_day(&g.item.at),
            amount: String::new(),
            locked: g.item.locked,
        };
        if is_windows_startup_id(&g.item.id) {
            off_rows.push(row);
        } else {
            bg_off_rows.push(row);
        }
    }

    off_rows.sort_by_key(|a| a.name.to_lowercase());
    bg_off_rows.sort_by_key(|a| a.name.to_lowercase());

    let bg_count = bg_on_items.len() + bg_off_rows.len();

    let mut list_col = col()
        .key("pc-startup-scroll")
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .gap(17.0)
        .pb(20.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        });

    if broken.is_empty() && on_items.is_empty() && off_rows.is_empty() && bg_count == 0 {
        let empty = if searching {
            "Nothing matches"
        } else {
            "Nothing starts with Windows"
        };
        return list_col.child(
            col()
                .w_full()
                .child(el().w_full().h(1.0).fill(float_line()))
                .child(
                    el().w_full()
                        .py(16.0)
                        .px(8.0)
                        .child(text(empty).font(type_body()).color(fg_muted())),
                ),
        );
    }

    let render_on_row = |g: &GroupedStartupItem| -> El {
        let required = g.item.locked || g.item.read_only;
        let ids = g.ids.clone();
        let err = g
            .ids
            .iter()
            .find_map(|id| s.pc.startup_errors.get(id).cloned());
        let amount = if g.item.memory > 0 {
            format_bytes(g.item.memory)
        } else {
            String::new()
        };
        let ctrl = if required {
            startup_tag_pill("Required", None)
        } else {
            switch_pill_keyed(
                format!("startup-sw-{}", g.item.id),
                format!("Turn off {}", g.item.name),
                true,
            )
            .on_press(move |s: &mut AppState| s.toggle_startup_ids(ids.clone(), false))
        };
        startup_line_row(
            s,
            format!("startup-row-{}", g.item.id),
            &g.item.name,
            g.item.icon.as_deref(),
            if required { "Required" } else { "On" },
            if required { None } else { Some("good") },
            startup_detail(g.item),
            amount,
            err,
            ctrl,
        )
    };

    let render_off_row = |r: &StartupOffRow| -> El {
        let ids = r.ids.clone();
        let err = r
            .ids
            .iter()
            .find_map(|id| s.pc.startup_errors.get(id).cloned());
        let ctrl = if r.locked {
            startup_tag_pill("Required", None)
        } else {
            switch_pill_keyed(
                format!("startup-sw-{}", r.id),
                format!("Turn on {}", r.name),
                false,
            )
            .on_press(move |s: &mut AppState| s.toggle_startup_ids(ids.clone(), true))
        };
        startup_line_row(
            s,
            format!("startup-row-{}", r.id),
            &r.name,
            r.icon.as_deref(),
            "Off",
            None,
            r.detail.clone(),
            r.amount.clone(),
            err,
            ctrl,
        )
    };

    if !broken.is_empty() {
        let mut head = row()
            .w_full()
            .px(8.0)
            .pb(6.0)
            .justify_between()
            .center_y()
            .child(
                text(format!("Broken ({})", broken.len()))
                    .font(type_section())
                    .color(FG),
            );
        if broken.len() > 1 {
            let all_ids: Vec<String> = broken.iter().flat_map(|g| g.ids.clone()).collect();
            head = head.child(
                flat_btn(
                    "startup-clean-broken".into(),
                    "Clean up broken",
                    false,
                    false,
                    false,
                )
                .on_press(move |s: &mut AppState| s.toggle_startup_ids(all_ids.clone(), false)),
            );
        }
        let mut sec = col().w_full().child(head);
        for g in &broken {
            let ids = g.ids.clone();
            let err = g
                .ids
                .iter()
                .find_map(|id| s.pc.startup_errors.get(id).cloned());
            sec = sec.child(startup_line_row(
                s,
                format!("startup-row-{}", g.item.id),
                &g.item.name,
                g.item.icon.as_deref(),
                "App removed",
                Some("warn"),
                "Its app was uninstalled · safe to remove".into(),
                String::new(),
                err,
                flat_btn(
                    format!("startup-rm-{}", g.item.id),
                    "Remove",
                    false,
                    false,
                    false,
                )
                .on_press(move |s: &mut AppState| s.toggle_startup_ids(ids.clone(), false)),
            ));
        }
        list_col = list_col.child(sec);
    }

    if !on_items.is_empty() {
        let mut sec = col().w_full().child(startup_group_heading(format!(
            "Starts with Windows ({})",
            on_items.len()
        )));
        for g in &on_items {
            sec = sec.child(render_on_row(g));
        }
        list_col = list_col.child(sec);
    }

    if !off_rows.is_empty() {
        let mut sec = col().w_full().child(startup_group_heading(format!(
            "Turned off ({})",
            off_rows.len()
        )));
        for r in &off_rows {
            sec = sec.child(render_off_row(r));
        }
        list_col = list_col.child(sec);
    }

    if bg_count > 0 {
        let mut sec = col().w_full();
        if searching {
            sec = sec.child(startup_group_heading(format!(
                "Background tasks & services ({bg_count})"
            )));
        } else {
            let open = s.pc.startup_bg_open;
            sec = sec.child(el().w_full().h(1.0).fill(float_line())).child(
                row()
                    .key("startup-bg-toggle")
                    .role("button")
                    .label(format!("Background tasks & services ({bg_count})"))
                    .cursor(Cursor::Pointer)
                    .focusable(true)
                    .hit()
                    .w_full()
                    .min_h(44.0)
                    .px(8.0)
                    .justify_between()
                    .center_y()
                    .radius(RADIUS_SMALL)
                    .hover(hover_fill())
                    .on_press(|s: &mut AppState| s.pc.startup_bg_open = !s.pc.startup_bg_open)
                    .child(
                        text(format!("Background tasks & services ({bg_count})"))
                            .font(type_control())
                            .color(apps_secondary()),
                    )
                    .child(if open {
                        icon_chevron_up(14.0, apps_secondary())
                    } else {
                        icon_chevron_down(14.0, apps_secondary())
                    }),
            );
        }
        if s.pc.startup_bg_open || searching {
            for g in &bg_on_items {
                sec = sec.child(render_on_row(g));
            }
            for r in &bg_off_rows {
                sec = sec.child(render_off_row(r));
            }
        }
        list_col = list_col.child(sec);
    }

    list_col
}

pub(crate) fn apps_room(s: &AppState) -> El {
    let q = s.pc.apps_query.text.trim().to_lowercase();
    let searching = !q.is_empty();
    let programs =
        s.pc.apps_list
            .as_ref()
            .map(|l| l.programs.as_slice())
            .unwrap_or(&[]);
    let main_count = programs.iter().filter(|p| !is_system_part(p)).count();
    let total_bytes: u64 = programs
        .iter()
        .filter(|p| !is_system_part(p))
        .filter_map(|p| p.bytes)
        .sum();
    let today = today_epoch_days();

    let header = row()
        .w_full()
        .h(32.0)
        .justify_between()
        .center_y()
        .gap(16.0)
        .child(
            row()
                .gap(16.0)
                .center_y()
                .child(
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(
                            text("Apps")
                                .font(TextStyle::new(scaled(20.0), 650.0))
                                .color(FG),
                        )
                        .child(
                            text(format!("{main_count} apps · {}", format_bytes(total_bytes)))
                                .font(type_body())
                                .color(apps_secondary()),
                        ),
                )
                .child(
                    row()
                        .gap(4.0)
                        .center_y()
                        .child(
                            apps_pill(
                                "apps-sub-installed",
                                "Apps",
                                s.pc.apps_sub_tab == AppsSubTab::Installed,
                            )
                            .on_press(|s: &mut AppState| s.pc.apps_sub_tab = AppsSubTab::Installed),
                        )
                        .child(
                            apps_pill(
                                "apps-sub-startup",
                                "Startup",
                                s.pc.apps_sub_tab == AppsSubTab::Startup,
                            )
                            .on_press(|s: &mut AppState| s.pc.apps_sub_tab = AppsSubTab::Startup),
                        ),
                ),
        )
        .child(
            row()
                .hit()
                .cursor(Cursor::Text)
                .w(240.0)
                .h(32.0)
                .pl(10.0)
                .pr(8.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(TRANSPARENT)
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(icon_search(14.0, fg_muted()))
                .child(
                    text_input(&s.pc.apps_query, type_body())
                        .key("apps-search-input")
                        .label("Search apps")
                        .grow(1.0)
                        .single_line()
                        .color(FG)
                        .placeholder("Search", fg_muted())
                        .on_edit(edit_access(|s: &mut AppState| &mut s.pc.apps_query)),
                ),
        );

    let body = if s.pc.apps_sub_tab == AppsSubTab::Startup {
        startup_tab_body(s, &q)
    } else {
        let mut filtered: Vec<&InstalledAppItem> = programs
            .iter()
            .filter(|p| {
                q.is_empty()
                    || p.name.to_lowercase().contains(&q)
                    || p.publisher
                        .as_deref()
                        .unwrap_or("")
                        .to_lowercase()
                        .contains(&q)
            })
            .filter(|p| !s.pc.apps_only_unused || is_unused_app(p, today))
            .collect();

        filtered.sort_by(|a, b| {
            let ord = match s.pc.apps_sort_by {
                AppsSortBy::Name => a.name.to_lowercase().cmp(&b.name.to_lowercase()),
                AppsSortBy::Size => b.bytes.unwrap_or(0).cmp(&a.bytes.unwrap_or(0)),
                AppsSortBy::Used => {
                    let la = a
                        .quiet_since
                        .as_deref()
                        .or(a.last_opened.as_deref())
                        .unwrap_or("9999");
                    let lb = b
                        .quiet_since
                        .as_deref()
                        .or(b.last_opened.as_deref())
                        .unwrap_or("9999");
                    la.cmp(lb)
                        .then_with(|| b.bytes.unwrap_or(0).cmp(&a.bytes.unwrap_or(0)))
                }
            };
            if s.pc.apps_sort_back {
                ord.reverse()
            } else {
                ord
            }
        });

        let main_apps: Vec<&InstalledAppItem> = filtered
            .iter()
            .copied()
            .filter(|p| !is_system_part(p))
            .collect();
        let parts: Vec<&InstalledAppItem> = filtered
            .iter()
            .copied()
            .filter(|p| is_system_part(p))
            .collect();

        let visible_main = &main_apps[..main_apps.len().min(120)];
        let visible_parts = &parts[..parts.len().min(60)];
        let show_last_used = programs.iter().any(|a| {
            a.last_opened
                .as_deref()
                .is_some_and(|t| !t.trim().is_empty())
                || a.quiet_since.is_some()
        });
        let wide_select = s.pc.apps_run.is_some() || !s.pc.apps_picked.is_empty();

        let mut select_controls = row().gap(8.0).center_y().justify_end();
        if let Some((at, of)) = s.pc.apps_run {
            let status_txt = if at == 0 {
                "Preparing…".to_string()
            } else {
                format!("{at} of {of}")
            };
            select_controls = select_controls.child(
                text(status_txt)
                    .key("apps-run-status")
                    .font(type_small())
                    .color(warn()),
            );
        } else {
            if !s.pc.apps_picked.is_empty() {
                let picked_ids: Vec<String> = s.pc.apps_picked.iter().cloned().collect();
                let count = picked_ids.len();
                let is_armed = s.pc.armed_confirm.as_deref() == Some("apps-many");
                let lbl = if is_armed {
                    format!("Uninstall {count}?")
                } else {
                    format!("Uninstall {count}")
                };
                select_controls = select_controls.child(
                    flat_btn(
                        "apps-uninstall-picked".into(),
                        lbl,
                        false,
                        true,
                        s.pc.apps_busy_id.is_some(),
                    )
                    .on_press(move |s: &mut AppState| {
                        if s.pc.armed_confirm.as_deref() == Some("apps-many") {
                            s.uninstall_many_apps(picked_ids.clone());
                        } else {
                            s.pc.armed_confirm = Some("apps-many".into());
                        }
                    }),
                );
            }
            select_controls = select_controls.child(
                flat_btn(
                    "apps-select-toggle".into(),
                    if s.pc.apps_select_mode {
                        "Cancel"
                    } else {
                        "Select"
                    },
                    false,
                    false,
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.pc.apps_select_mode = !s.pc.apps_select_mode;
                    if !s.pc.apps_select_mode {
                        s.pc.apps_picked.clear();
                        s.pc.armed_confirm = None;
                    }
                }),
            );
        }

        let mut table_head = row()
            .key("apps-table-head")
            .w_full()
            .h(32.0)
            .gap(12.0)
            .center_y()
            .child(el().w(28.0))
            .child(el().grow(1.0).min_w(0.0).child(sort_head_btn(
                "apps-head-name",
                "Name",
                AppsSortBy::Name,
                s,
            )));
        if show_last_used && !wide_select {
            table_head = table_head.child(el().key("apps-head-last-used").w(scaled(112.0)).child(
                sort_head_btn("apps-sort-used", "Last used", AppsSortBy::Used, s),
            ));
        }
        if !wide_select {
            table_head =
                table_head.child(row().w(scaled(72.0)).justify_end().child(sort_head_btn(
                    "apps-sort-size",
                    "Size",
                    AppsSortBy::Size,
                    s,
                )));
        }
        table_head = table_head.child(
            row()
                .min_w(scaled(108.0))
                .justify_end()
                .child(select_controls),
        );

        let mut list_col = col()
            .key("pc-apps-scroll")
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .gap(18.0)
            .pb(20.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            });

        if !searching && !s.pc.apps_only_unused {
            list_col = list_col.child(ready_section(s, programs));
        }

        if s.pc.apps_only_unused {
            list_col = list_col.child(
                row()
                    .key("apps-unused-banner")
                    .w_full()
                    .py(10.0)
                    .justify_between()
                    .center_y()
                    .child(
                        text("Not opened in three months")
                            .font(type_control())
                            .color(FG),
                    )
                    .child(
                        flat_btn("apps-show-all".into(), "Show all", false, false, false)
                            .on_press(|s: &mut AppState| s.pc.apps_only_unused = false),
                    ),
            );
        }

        let mut rows_col = col().w_full();
        for app in visible_main {
            rows_col = rows_col.child(app_table_row(app, s, show_last_used));
        }
        if visible_main.is_empty() && visible_parts.is_empty() {
            rows_col = rows_col.child(
                el().w_full()
                    .py(24.0)
                    .child(text("No apps match").font(type_body()).color(fg_muted())),
            );
        }
        list_col = list_col.child(rows_col);
        if !visible_parts.is_empty() || s.pc.apps_list.as_ref().is_some_and(|l| l.edge_removed) {
            let show_parts = searching || s.pc.apps_parts_open;
            let mut parts_sec = col().w_full().child(
                row()
                    .key("apps-parts-toggle")
                    .role("button")
                    .label("Needed by Windows, games or Exo")
                    .cursor(Cursor::Pointer)
                    .focusable(true)
                    .hit()
                    .w_full()
                    .h(40.0)
                    .pt(12.0)
                    .gap(6.0)
                    .center_y()
                    .on_press(|s: &mut AppState| s.pc.apps_parts_open = !s.pc.apps_parts_open)
                    .child(icon_chevron_down(12.0, apps_secondary()))
                    .child(
                        text(format!("Needed by Windows, games or Exo ({})", parts.len()))
                            .font(TextStyle::new(scaled(12.0), 500.0))
                            .color(apps_secondary()),
                    ),
            );
            if show_parts {
                for app in visible_parts {
                    parts_sec = parts_sec.child(app_table_row(app, s, show_last_used));
                }
                if s.pc.apps_list.as_ref().is_some_and(|l| l.edge_removed) {
                    let busy = s.pc.apps_busy_id.is_some();
                    parts_sec = parts_sec.child(ready_row(
                        "Microsoft Edge is blocked from returning".into(),
                        Some("Allow Windows to install Microsoft Edge again.".into()),
                        false,
                        None,
                        flat_btn("apps-allow-edge".into(), "Allow Edge", false, false, busy)
                            .on_press(|s: &mut AppState| s.allow_edge_browser()),
                    ));
                }
                let add_back: Vec<_> =
                    s.pc.windows_packages
                        .iter()
                        .filter(|p| p.add_back)
                        .collect();
                for pkg in add_back {
                    let pid = pkg.id.clone();
                    let busy = s.pc.apps_busy_id.is_some();
                    parts_sec = parts_sec.child(ready_row(
                        format!("{} (removed)", pkg.name),
                        pkg.publisher.clone(),
                        false,
                        s.pc.apps_packages_error.clone(),
                        flat_btn(
                            format!("apps-pkg-addback-{}", pkg.id),
                            "Add back",
                            false,
                            false,
                            busy,
                        )
                        .on_press(move |s: &mut AppState| s.add_back_windows_package(&pid)),
                    ));
                }
            }
            list_col = list_col.child(parts_sec);
        }

        col()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .gap(12.0)
            .child(table_head)
            .children(s.pc.apps_errors.get("many").map(|err| {
                el().w_full()
                    .py(6.0)
                    .child(text(err.clone()).font(type_small()).color(bad()).wrap())
            }))
            .child(list_col)
    };

    let surface = col()
        .key("apps-surface")
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .pt(20.0)
        .px(24.0)
        .pb(8.0)
        .gap(12.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(header)
        .child(body);

    col()
        .key("apps-room")
        .w_full()
        .h_full()
        .pt(64.0)
        .px(10.0)
        .pb(20.0)
        .fill(BG)
        .on_key(|s: &mut AppState, ev, _| {
            if ev.key == Key::Escape {
                if s.pc.armed_uninstall.is_some() {
                    s.disarm_uninstall();
                    return true;
                }
                if s.pc.armed_confirm.is_some() {
                    s.pc.armed_confirm = None;
                    return true;
                }
            }
            false
        })
        .child(surface)
}

#[cfg(test)]
mod tests {
    use crate::shell;
    use crate::state::{AppState, Room};
    use rune::motion_gate::Run;
    use rune::render::KIND_BACKDROP_BLUR;
    use rune::{Env, Key, KeyEvent, Mods, Runtime};

    #[test]
    fn apps_uninstall_hidden_at_rest_reveals_on_hover_and_confirms_in_place() {
        let mut state = AppState::default();
        if let Some(ref mut list) = state.pc.apps_list {
            for p in &mut list.programs {
                p.last_opened = None;
            }
        }
        state.switch_room(Room::Apps);
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.set_max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS);
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        assert!(
            rt.find("apps-head-last-used").is_none(),
            "Last used column should hide when no app has last_opened"
        );
        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "Apps exceeded blur cap: {blurs}"
        );

        let (_, rest_op) = rt
            .presented("app-uninst-app:obs")
            .expect("app-uninst-app:obs at rest");
        assert!(
            rest_op > 0.95,
            "Uninstall button must be visible at rest (opacity=1), got {rest_op}"
        );

        let (bx, by) = rt
            .find("app-uninst-app:obs")
            .expect("app-uninst-app:obs hit");
        rt.pointer_move(bx, by);
        rt.pointer_down(bx, by);
        rt.pointer_up(bx, by);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        assert!(
            rt.state
                .pc
                .armed_uninstall
                .as_ref()
                .is_some_and(|(id, _)| id == "app:obs"),
            "First click must arm in-place confirm without firing uninstall"
        );
        assert!(
            rt.state.notice.is_none(),
            "First click must not fire uninstall notice"
        );
        assert!(
            rt.find("app-confirm-app:obs").is_some(),
            "Confirm uninstall button must appear in place"
        );

        rt.key(KeyEvent {
            key: Key::Escape,
            mods: Mods::default(),
        });
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(
            rt.state.pc.armed_uninstall.is_none(),
            "Escape must disarm uninstall"
        );

        let (bx2, by2) = rt.find("app-uninst-app:obs").expect("re-arm hit");
        rt.pointer_move(bx2, by2);
        rt.pointer_down(bx2, by2);
        rt.pointer_up(bx2, by2);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        let (cx, cy) = rt.find("app-confirm-app:obs").expect("confirm hit");
        rt.pointer_move(cx, cy);
        rt.pointer_down(cx, cy);
        rt.pointer_up(cx, cy);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        assert!(
            rt.state.pc.armed_uninstall.is_none(),
            "Second click must disarm after firing"
        );
        assert_eq!(
            rt.state.notice.as_deref(),
            Some("Starting application uninstaller…")
        );
    }

    #[test]
    fn apps_shows_last_used_column_when_populated() {
        let mut state = AppState::default();
        state.switch_room(Room::Apps);
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(
            rt.find("apps-head-last-used").is_some(),
            "Last used column should render when at least one app has last_opened"
        );
    }

    #[test]
    fn apps_hover_and_confirm_motion_gate() {
        for reduce_motion in [false, true] {
            let mut state = AppState::default();
            state.settings.reduce_motion = reduce_motion;
            state.switch_room(Room::Apps);
            let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt, 120.0);
            run.idle(0.5);
            run.step();
            run.act("hover app row", |rt| {
                if let Some((x, y)) = rt.find("app-row-app:obs") {
                    rt.pointer_move(x, y);
                }
            });
            run.play(0.4);
            run.act("arm uninstall", |rt| Run::tap(rt, "app-uninst-app:obs"));
            run.play(0.4);
            run.act("disarm via escape", |rt| {
                rt.key(KeyEvent {
                    key: Key::Escape,
                    mods: Mods::default(),
                });
            });
            run.play(0.4);
            let findings = run.finish();
            assert!(
                findings.is_empty(),
                "motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }

    #[test]
    fn startup_format_turned_off_day_and_detail_precedence() {
        assert_eq!(
            super::format_turned_off_day("2026-11-19T14:22:00Z"),
            "Turned off Nov 19, 2026"
        );
        assert_eq!(
            super::format_turned_off_day("2026-01-05"),
            "Turned off Jan 5, 2026"
        );
        assert!(super::is_windows_startup_id("run:Discord"));
        assert!(super::is_windows_startup_id("folder:Wooting"));
        assert!(!super::is_windows_startup_id("task:EdgeUpdate"));

        let mut item = exo_rpc::StartupItem {
            id: "run:foo".into(),
            name: "Foo".into(),
            command: "C:\\Foo\\foo.exe".into(),
            source: "run".into(),
            enabled: true,
            ..Default::default()
        };
        assert_eq!(super::startup_detail(&item), "Runs C:\\Foo\\foo.exe");
        item.does = Some("Runs in background".into());
        assert_eq!(super::startup_detail(&item), "Runs in background");
        item.without = Some("Exo needs this one to keep WebView2 up to date.".into());
        assert_eq!(
            super::startup_detail(&item),
            "Exo needs this one to keep WebView2 up to date."
        );
        item.verdict = Some("remove".into());
        assert_eq!(super::startup_detail(&item), "Safe to turn off");
        item.missing = true;
        assert_eq!(
            super::startup_detail(&item),
            "Its program is gone: safe to turn off"
        );
        item.status = Some("Blocked by policy".into());
        assert_eq!(super::startup_detail(&item), "Blocked by policy");
    }

    #[test]
    fn startup_tab_groups_and_toggles_background_section() {
        let mut state = AppState::default();
        state.switch_room(Room::Apps);
        state.pc.apps_sub_tab = crate::pc_state::AppsSubTab::Startup;
        state.pc.startup_list = Some(exo_rpc::StartupListReply {
            ok: true,
            message: None,
            items: vec![
                exo_rpc::StartupItem {
                    id: "run:wooting-1".into(),
                    name: "Wooting Background Service".into(),
                    command: "wooting-bg-service.exe".into(),
                    source: "run".into(),
                    enabled: true,
                    memory: 6_291_456,
                    ..Default::default()
                },
                exo_rpc::StartupItem {
                    id: "run:wooting-2".into(),
                    name: "Wooting Background Service".into(),
                    command: "wooting-bg-service.exe".into(),
                    source: "run".into(),
                    enabled: true,
                    ..Default::default()
                },
                exo_rpc::StartupItem {
                    id: "run:broken".into(),
                    name: "GoneApp".into(),
                    source: "run".into(),
                    enabled: true,
                    missing: true,
                    ..Default::default()
                },
                exo_rpc::StartupItem {
                    id: "task:bg-1".into(),
                    name: "Background Sync".into(),
                    source: "task".into(),
                    enabled: true,
                    ..Default::default()
                },
            ],
            removed: vec![exo_rpc::StartupRemovedItem {
                id: "run:claude".into(),
                name: "Claude Remote Control".into(),
                at: "2026-11-19T00:00:00Z".into(),
                icon: None,
                locked: false,
            }],
            extras: 0,
        });
        let mut rt = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);

        assert!(rt.find("startup-row-run:wooting-1").is_some());
        assert!(
            rt.find("startup-row-run:wooting-2").is_none(),
            "Duplicate startup names should collapse into a single grouped row"
        );
        assert!(rt.find("startup-row-run:broken").is_some());
        assert!(rt.find("startup-row-run:claude").is_some());
        assert!(rt.find("startup-bg-toggle").is_some());
        assert!(
            rt.find("startup-row-task:bg-1").is_none(),
            "Background tasks should start collapsed"
        );

        let (bx, by) = rt.find("startup-bg-toggle").expect("bg toggle hit");
        rt.pointer_move(bx, by);
        rt.pointer_down(bx, by);
        rt.pointer_up(bx, by);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.state.pc.startup_bg_open);
        assert!(rt.find("startup-row-task:bg-1").is_some());
    }
}
