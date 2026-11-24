use crate::pc_state::{AppsSubTab, format_bytes};
use crate::state::AppState;
use exo_kit::{
    BG, FG, GLASS_CONTROL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL,
    SPRING_QUICK, TRANSPARENT, bad, fg_muted, float_line, glass_button, glass_control_fill,
    glass_rim_bottom, glass_rim_top, hover_fill, icon_app_window, icon_chevron_down, icon_search,
    icon_trash, pressed_fill, scaled, type_body, type_control, type_small, warn,
};
use exo_rpc::InstalledAppItem;
use rune::{
    Cursor, El, Key, ScrollSpec, SurfaceFit, TextStyle, Transition, col, edit_access, el, row,
    text, text_input,
};

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
        .child(
            text(label)
                .font(type_control())
                .color(if active { PRIMARY_FG } else { fg_muted() }),
        )
}

fn app_table_row(app: &InstalledAppItem, s: &AppState, show_last_used: bool) -> El {
    let id = app.id.clone();
    let is_armed =
        s.pc.armed_uninstall
            .as_ref()
            .is_some_and(|(armed_id, _)| armed_id == &app.id);
    let icon_box = if let Some(img) = s.app_icon_image(&app.id) {
        el().size(28.0, 28.0)
            .radius(RADIUS_SMALL)
            .center()
            .fill(FG.alpha(0.05))
            .child(
                el().size(20.0, 20.0)
                    .pixels_fit(img.pixels(), SurfaceFit::Fit),
            )
    } else {
        el().size(28.0, 28.0)
            .radius(RADIUS_SMALL)
            .center()
            .fill(FG.alpha(0.05))
            .child(icon_app_window(15.0, fg_muted()))
    };

    let sub = app
        .kept
        .as_deref()
        .or(app.publisher.as_deref())
        .unwrap_or("")
        .to_string();
    let size_txt = app.bytes.map(format_bytes).unwrap_or_else(|| "—".into());
    let last_used = app.last_opened.clone().unwrap_or_default();

    let action_slot = if is_armed {
        let confirm_btn = el()
            .key(format!("app-confirm-{}", app.id))
            .role("button")
            .label(format!("Confirm uninstall {}", app.name))
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(30.0)
            .px(12.0)
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
                    .font(TextStyle::new(scaled(12.0), 500.0))
                    .color(PRIMARY_FG),
            );
        row()
            .key(format!("app-confirm-slot-{}", app.id))
            .absolute(0.0, 0.0)
            .w_full()
            .h_full()
            .justify_end()
            .center_y()
            .transition(Transition::opacity())
            .child(confirm_btn)
    } else {
        let uninstall_btn = el()
            .key(format!("app-uninst-{}", app.id))
            .role("button")
            .label(format!("Uninstall {}", app.name))
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(30.0)
            .px(12.0)
            .center()
            .radius(RADIUS_PILL)
            .fill(BG)
            .border(1.0, float_line())
            .hover(hover_fill())
            .press(pressed_fill())
            .group_opacity(0.0, 1.0)
            .on_press(move |s: &mut AppState| s.arm_or_confirm_uninstall(&id))
            .child(
                text("Uninstall")
                    .font(TextStyle::new(scaled(12.0), 500.0))
                    .color(bad()),
            );
        row()
            .key(format!("app-uninst-slot-{}", app.id))
            .absolute(0.0, 0.0)
            .w_full()
            .h_full()
            .justify_end()
            .center_y()
            .transition(Transition::opacity())
            .child(uninstall_btn)
    };

    let mut content_row = row()
        .w_full()
        .h(51.0)
        .gap(12.0)
        .center_y()
        .hover_group()
        .hover_motion(SPRING_QUICK, SPRING_QUICK)
        .child(icon_box)
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(2.0)
                .child(
                    text(app.name.clone())
                        .font(type_control())
                        .color(FG)
                        .truncate(),
                )
                .children(
                    (!sub.is_empty())
                        .then(|| text(sub).font(type_small()).color(fg_muted()).truncate()),
                ),
        );
    if show_last_used {
        content_row = content_row.child(
            el().w(scaled(112.0)).child(
                text(last_used)
                    .font(type_small())
                    .color(fg_muted())
                    .truncate(),
            ),
        );
    }
    content_row = content_row
        .child(
            row()
                .w(scaled(80.0))
                .justify_end()
                .child(text(size_txt).font(type_small()).color(fg_muted())),
        )
        .child(el().w(scaled(140.0)).h(30.0).child(action_slot));

    col()
        .key(format!("app-row-{}", app.id))
        .w_full()
        .h(52.0)
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(content_row)
}

fn is_system_part(app: &InstalledAppItem) -> bool {
    !app.removable || app.care.as_deref() == Some("runtime")
}

pub(crate) fn apps_room(s: &AppState) -> El {
    let q = s.pc.apps_query.text.trim().to_lowercase();
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
                                .color(fg_muted()),
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
                .cursor(Cursor::Text)
                .w(240.0)
                .h(32.0)
                .pl(10.0)
                .pr(8.0)
                .gap(8.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .backdrop_blur(GLASS_CONTROL_SIGMA)
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
        let items =
            s.pc.startup_list
                .as_ref()
                .map(|l| l.items.as_slice())
                .unwrap_or(&[]);
        let mut list_col = col()
            .key("pc-startup-scroll")
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            });
        for item in items {
            let id = item.id.clone();
            list_col = list_col.child(
                col()
                    .w_full()
                    .h(52.0)
                    .child(el().w_full().h(1.0).fill(float_line()))
                    .child(
                        row()
                            .w_full()
                            .h(51.0)
                            .justify_between()
                            .center_y()
                            .child(
                                col()
                                    .gap(2.0)
                                    .child(text(item.name.clone()).font(type_control()).color(FG))
                                    .child(
                                        text(
                                            item.does
                                                .clone()
                                                .unwrap_or_else(|| item.source.clone()),
                                        )
                                        .font(type_small())
                                        .color(fg_muted()),
                                    ),
                            )
                            .child(
                                glass_button(
                                    "startup-rm",
                                    "Disable",
                                    Some(icon_trash(13.0, warn())),
                                )
                                .on_press(move |s: &mut AppState| s.remove_startup_item(&id)),
                            ),
                    ),
            );
        }
        list_col
    } else {
        let mut main_apps: Vec<&InstalledAppItem> = programs
            .iter()
            .filter(|p| !is_system_part(p))
            .filter(|p| {
                q.is_empty()
                    || p.name.to_lowercase().contains(&q)
                    || p.publisher
                        .as_deref()
                        .unwrap_or("")
                        .to_lowercase()
                        .contains(&q)
            })
            .collect();
        main_apps.sort_by_key(|a| std::cmp::Reverse(a.bytes.unwrap_or(0)));

        let mut parts: Vec<&InstalledAppItem> = programs
            .iter()
            .filter(|p| is_system_part(p))
            .filter(|p| {
                q.is_empty()
                    || p.name.to_lowercase().contains(&q)
                    || p.publisher
                        .as_deref()
                        .unwrap_or("")
                        .to_lowercase()
                        .contains(&q)
            })
            .collect();
        parts.sort_by_key(|a| std::cmp::Reverse(a.bytes.unwrap_or(0)));

        let visible_main = &main_apps[..main_apps.len().min(60)];
        let visible_parts = &parts[..parts.len().min(40)];
        let show_last_used = visible_main.iter().chain(visible_parts.iter()).any(|a| {
            a.last_opened
                .as_deref()
                .is_some_and(|t| !t.trim().is_empty())
        });

        let mut table_head = row()
            .key("apps-table-head")
            .w_full()
            .h(32.0)
            .gap(12.0)
            .center_y()
            .child(el().w(28.0))
            .child(
                el().grow(1.0).min_w(0.0).child(
                    text("Name")
                        .font(TextStyle::new(scaled(12.0), 500.0))
                        .color(fg_muted()),
                ),
            );
        if show_last_used {
            table_head = table_head.child(
                el().key("apps-head-last-used").w(scaled(112.0)).child(
                    text("Last used")
                        .font(TextStyle::new(scaled(12.0), 500.0))
                        .color(fg_muted()),
                ),
            );
        }
        table_head = table_head
            .child(
                row()
                    .w(scaled(80.0))
                    .justify_end()
                    .gap(4.0)
                    .center_y()
                    .child(icon_chevron_down(12.0, fg_muted()))
                    .child(
                        text("Size")
                            .font(TextStyle::new(scaled(12.0), 500.0))
                            .color(FG),
                    ),
            )
            .child(
                row().w(scaled(140.0)).justify_end().center_y().child(
                    el().cursor(Cursor::Pointer)
                        .h(28.0)
                        .px(12.0)
                        .center()
                        .radius(RADIUS_PILL)
                        .fill(glass_control_fill())
                        .backdrop_blur(GLASS_CONTROL_SIGMA)
                        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                        .child(
                            text("Select")
                                .font(TextStyle::new(scaled(12.0), 500.0))
                                .color(FG),
                        ),
                ),
            );

        let mut list = col()
            .key("pc-apps-scroll")
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            });
        for app in visible_main {
            list = list.child(app_table_row(app, s, show_last_used));
        }
        if !parts.is_empty() {
            list = list.child(
                el().w_full().pt(20.0).pb(8.0).child(
                    text(format!(
                        "What Windows, games and Exo need ({})",
                        parts.len()
                    ))
                    .font(TextStyle::new(scaled(12.0), 500.0))
                    .color(fg_muted()),
                ),
            );
            for app in visible_parts {
                list = list.child(app_table_row(app, s, show_last_used));
            }
        }

        col()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .gap(12.0)
            .child(table_head)
            .child(list)
    };

    let surface = col()
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
        .border(1.0, float_line())
        .clip_rounded(RADIUS_PANEL)
        .child(header)
        .child(body);

    col()
        .key("room-apps")
        .w_full()
        .h_full()
        .pt(64.0)
        .px(10.0)
        .pb(20.0)
        .fill(BG)
        .on_background_press(|s: &mut AppState| s.disarm_uninstall())
        .on_key(|s: &mut AppState, ev, _| {
            if ev.key == Key::Escape && s.pc.armed_uninstall.is_some() {
                s.disarm_uninstall();
                true
            } else {
                false
            }
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

        let blurs = rt
            .instances
            .iter()
            .filter(|i| i.params[1] == KIND_BACKDROP_BLUR)
            .count();
        assert!(
            blurs <= exo_kit::EXO_MAX_BACKDROP_BLURS,
            "Apps exceeded EXO_MAX_BACKDROP_BLURS: {blurs}"
        );
        assert!(blurs >= 3, "Apps should render Liquid Glass, got {blurs}");

        assert!(
            rt.find("apps-head-last-used").is_none(),
            "Last used column should be omitted when all apps have last_opened == None"
        );

        let (_, rest_opacity) = rt
            .presented("app-uninst-app:obs")
            .expect("app-uninst-app:obs");
        assert!(
            rest_opacity < 0.01,
            "Uninstall button must be hidden at rest, got opacity {rest_opacity}"
        );

        let (rx, ry) = rt.find("app-row-app:obs").expect("app-row-app:obs");
        rt.pointer_move(rx, ry);
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let (_, hovered_opacity) = rt
            .presented("app-uninst-app:obs")
            .expect("app-uninst-app:obs");
        assert!(
            hovered_opacity > 0.95,
            "Uninstall button must reveal on row hover, got opacity {hovered_opacity}"
        );

        let (bx, by) = rt.find("app-uninst-app:obs").expect("app-uninst-app:obs");
        rt.pointer_down(bx, by);
        rt.pointer_up(bx, by);
        rt.frame(1.0 / 60.0);
        assert_eq!(
            rt.state
                .pc
                .armed_uninstall
                .as_ref()
                .map(|(id, _)| id.as_str()),
            Some("app:obs")
        );
        assert!(rt.state.notice.is_none());

        rt.pointer_move(10.0, 10.0);
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let (_, armed_opacity) = rt
            .presented("app-confirm-app:obs")
            .expect("app-confirm-app:obs");
        assert!(
            armed_opacity > 0.95,
            "Armed confirm button must remain visible when pointer leaves, got {armed_opacity}"
        );

        rt.key(KeyEvent {
            key: Key::Escape,
            mods: Mods::default(),
        });
        rt.frame(1.0 / 60.0);
        assert!(rt.state.pc.armed_uninstall.is_none());

        rt.pointer_move(rx, ry);
        for _ in 0..30 {
            rt.frame(1.0 / 60.0);
        }
        let (bx2, by2) = rt.find("app-uninst-app:obs").expect("app-uninst-app:obs");
        rt.pointer_down(bx2, by2);
        rt.pointer_up(bx2, by2);
        rt.frame(1.0 / 60.0);
        assert!(rt.state.pc.armed_uninstall.is_some());

        let (bx3, by3) = rt.find("app-confirm-app:obs").expect("app-confirm-app:obs");
        rt.pointer_down(bx3, by3);
        rt.pointer_up(bx3, by3);
        rt.frame(1.0 / 60.0);
        assert!(rt.state.pc.armed_uninstall.is_none());
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
}
