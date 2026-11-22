use crate::state::{AppState, Room};
use crate::{
    captures, discord, game_page, key_modal, library, music, palette, pc, settings, store, tour,
};
use exo_kit::{
    BG, FG, GAP, GLASS_CONTROL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PILL,
    RADIUS_SMALL, SPRING_QUICK, TRANSPARENT, bad, exo_mark, fg_faint, fg_muted, glass_capsule,
    glass_control_fill, glass_panel, glass_rim_bottom, glass_rim_top, good, hover_fill,
    icon_camera, icon_chat, icon_close, icon_film, icon_gamepad, icon_grid, icon_library,
    icon_maximize, icon_minimize, icon_mouse, icon_music, icon_plus, icon_record_dot, icon_replay,
    icon_scissors, icon_settings, icon_sparkles, icon_store, nav_tab, pressed_fill, scrim_fill,
    selected_fill, set_text_scale, type_body, type_control, type_small,
};
use rune::{Cx, El, ScrollSpec, col, el, row, text};

pub fn room_icon(room: Room, active: bool) -> El {
    let color = if active { PRIMARY_FG } else { FG };
    match room {
        Room::Library => icon_library(16.0, color),
        Room::Store => icon_store(16.0, color),
        Room::Discord => icon_chat(16.0, color),
        Room::Music => icon_music(16.0, color),
        Room::Devices => icon_mouse(16.0, color),
        Room::ExoAi => icon_sparkles(16.0, color),
        Room::Apps => icon_grid(16.0, color),
        Room::Settings => icon_settings(16.0, color),
    }
}

fn format_keys_hint(raw: &str, fallback: &'static str) -> String {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return fallback.to_string();
    }
    trimmed
        .split('+')
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .collect::<Vec<_>>()
        .join(" + ")
}

fn capture_menu_row(
    key: &'static str,
    label: &'static str,
    shortcut: String,
    icon: El,
    disabled: bool,
) -> El {
    let fg = if disabled { fg_faint() } else { FG };
    let mut item = row()
        .key(key)
        .role("menuitem")
        .label(label)
        .focusable(!disabled)
        .w_full()
        .min_h(38.0)
        .px(12.0)
        .gap(10.0)
        .justify_between()
        .center_y()
        .radius(RADIUS_SMALL)
        .fill(TRANSPARENT)
        .motion(SPRING_QUICK);
    if !disabled {
        item = item
            .hit()
            .press_on_down()
            .hover(hover_fill())
            .press(pressed_fill());
    }
    item.child(
        row()
            .gap(10.0)
            .center_y()
            .child(icon)
            .child(text(label).font(type_control()).color(fg)),
    )
    .children((!shortcut.is_empty()).then(|| text(shortcut).font(type_small()).color(fg_faint())))
}

fn capture_popover(s: &AppState) -> El {
    let status = s.media.capture_status.as_ref();
    let present = status.is_none_or(|st| st.present);
    let live = status.and_then(|st| st.state.as_ref());
    let settings = status.and_then(|st| st.settings.as_ref());
    let buffering = live.is_some_and(|l| l.replay);
    let recording = live.is_some_and(|l| l.recording);

    let replay_hint = settings
        .map(|st| format_keys_hint(&st.save_replay_keys, "Ctrl + Alt + F10"))
        .unwrap_or_else(|| "Ctrl + Alt + F10".to_string());
    let record_hint = settings
        .map(|st| format_keys_hint(&st.record_keys, "Ctrl + Alt + F9"))
        .unwrap_or_else(|| "Ctrl + Alt + F9".to_string());
    let snip_hint = settings
        .map(|st| format_keys_hint(&st.snip_keys, "Ctrl + Alt + F8"))
        .unwrap_or_else(|| "Ctrl + Alt + F8".to_string());

    let mut menu = glass_panel("capture-menu", 240.0)
        .radius(RADIUS_CARD)
        .clip_rounded(RADIUS_CARD)
        .px(8.0)
        .py(8.0)
        .gap(0.0);

    if present {
        let replay_row = capture_menu_row(
            "cap-menu-replay",
            "Save replay",
            replay_hint,
            icon_replay(16.0, if buffering { FG } else { fg_faint() }),
            !buffering,
        );
        let replay_row = if buffering {
            replay_row.on_press(|s: &mut AppState| {
                s.capture_menu_open = false;
                s.capture_act("saveReplay");
            })
        } else {
            replay_row
        };
        menu = menu
            .child(replay_row)
            .child(
                capture_menu_row(
                    "cap-menu-record",
                    if recording {
                        "Stop recording"
                    } else {
                        "Record"
                    },
                    record_hint,
                    icon_record_dot(16.0, FG),
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.capture_act("record");
                }),
            )
            .child(
                capture_menu_row(
                    "cap-menu-snip",
                    "Snip",
                    snip_hint,
                    icon_scissors(16.0, FG),
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.capture_act("snip");
                }),
            )
            .child(
                capture_menu_row(
                    "cap-menu-note",
                    "New note",
                    String::new(),
                    icon_plus(16.0, FG),
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.notice = Some("Quick note ready".into());
                }),
            )
            .child(
                capture_menu_row(
                    "cap-menu-open",
                    "Captures",
                    String::new(),
                    icon_film(16.0, FG),
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.open_captures(None);
                }),
            );
    } else {
        menu = menu
            .child(
                capture_menu_row(
                    "cap-menu-note",
                    "New note",
                    String::new(),
                    icon_plus(16.0, FG),
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.notice = Some("Quick note ready".into());
                }),
            )
            .child(
                capture_menu_row(
                    "cap-menu-notes",
                    "Notes",
                    String::new(),
                    icon_film(16.0, FG),
                    false,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.open_captures(None);
                }),
            );
    }

    el().key("capture-menu-backdrop")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .hit()
        .on_press(|s: &mut AppState| s.capture_menu_open = false)
        .child(
            el().pin(None, Some(52.0), Some(112.0), None)
                .radius(RADIUS_CARD)
                .child(menu),
        )
}

fn gamepad_prompt_bar() -> El {
    row()
        .key("gamepad-bar")
        .pin(None, None, Some(GAP), Some(GAP))
        .h(36.0)
        .px(14.0)
        .gap(12.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(icon_gamepad(14.0, FG))
        .child(
            text("A Open · B Back · X Play · Y Search · LB/RB Rooms")
                .font(type_small())
                .color(fg_muted()),
        )
}

pub fn view(s: &AppState, cx: &mut Cx<AppState>) -> El {
    set_text_scale(s.text_scale());
    let has_modal_scrim = s.palette_open || s.key_modal_open;
    cx.caption_color(if has_modal_scrim {
        fg_muted().alpha(0.28)
    } else {
        fg_muted()
    });

    let insets = cx.chrome_insets();
    let scale = cx.env().scale;

    let search = row()
        .key("search-capsule")
        .role("button")
        .label("Search")
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .w(200.0)
        .h(36.0)
        .pl(10.0)
        .pr(12.0)
        .gap(10.0)
        .center_y()
        .radius(RADIUS_PILL)
        .backdrop_blur(GLASS_CONTROL_SIGMA)
        .fill(glass_control_fill())
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK)
        .on_press_tx(|s: &mut AppState, tx| {
            s.capture_menu_open = false;
            if s.palette_open {
                s.close_palette();
            } else {
                s.open_palette(tx);
            }
        })
        .child(exo_mark(18.0, FG))
        .child(text("Search").font(type_body()).color(FG));

    let nav = glass_capsule("nav-capsule").children(Room::NAV_TABS.into_iter().map(|room| {
        let active = s.room == room && !s.media.captures_open;
        nav_tab(room.key(), room.label(), active, room_icon(room, active))
            .on_press(move |s: &mut AppState| s.switch_room(room))
    }));

    let live_cap = s
        .media
        .capture_status
        .as_ref()
        .and_then(|st| st.state.as_ref());
    let replay_on = live_cap.is_some_and(|l| l.replay);
    let recording = live_cap.is_some_and(|l| l.recording);

    let capture_btn = if recording {
        row()
            .key("top-capture-rec")
            .role("button")
            .label("Stop recording")
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(28.0)
            .px(11.0)
            .mr(4.0)
            .gap(7.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_control_fill())
            .hover(hover_fill())
            .border(1.0, bad().alpha(0.40))
            .motion(SPRING_QUICK)
            .on_press(|s: &mut AppState| s.capture_act("record"))
            .child(el().size(8.0, 8.0).radius(4.0).fill(bad()))
            .child(text("REC").font(type_small()).color(FG))
    } else {
        el().key("top-capture-btn")
            .role("button")
            .label("Capture")
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .size(32.0, 28.0)
            .mr(2.0)
            .center()
            .radius(RADIUS_PILL)
            .fill(if s.capture_menu_open {
                selected_fill()
            } else if s.media.captures_open {
                hover_fill()
            } else {
                TRANSPARENT
            })
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press(|s: &mut AppState| {
                s.capture_menu_open = !s.capture_menu_open;
            })
            .child(icon_camera(
                16.0,
                if s.capture_menu_open { FG } else { fg_muted() },
            ))
            .children(replay_on.then(|| {
                el().pin(None, Some(5.0), Some(6.0), None)
                    .size(6.0, 6.0)
                    .radius(3.0)
                    .fill(good())
                    .outline(2.0, 0.0, BG)
            }))
    };

    let settings_active = s.room == Room::Settings;
    let settings_btn = el()
        .key("nav-settings")
        .role("button")
        .label("Settings")
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(34.0, 30.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(if settings_active { FG } else { TRANSPARENT })
        .hover(if settings_active { FG } else { hover_fill() })
        .press(if settings_active { FG } else { pressed_fill() })
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.toggle_settings())
        .child(icon_settings(
            16.0,
            if settings_active {
                PRIMARY_FG
            } else {
                fg_muted()
            },
        ));

    let divider = el().size(1.0, 16.0).mx(3.0).fill(selected_fill());

    let mut tools = row()
        .key("titlebar-tools")
        .h(36.0)
        .pl(3.0)
        .pr(if insets.trailing > 0.0 { 0.0 } else { 3.0 })
        .gap(2.0)
        .center_y()
        .radius(RADIUS_PILL)
        .backdrop_blur(GLASS_CONTROL_SIGMA)
        .fill(glass_control_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(capture_btn)
        .child(settings_btn)
        .child(divider);

    if insets.trailing > 0.0 {
        let reserve = (insets.trailing - 4.0).max(0.0);
        tools = tools.child(el().size(reserve, 30.0));
    } else {
        let win_btn = |key: &'static str, label: &'static str, icon: El| {
            el().key(key)
                .role("button")
                .label(label)
                .size(34.0, 30.0)
                .center()
                .radius(RADIUS_PILL)
                .child(icon)
        };
        tools = tools
            .child(win_btn(
                "win-min",
                "Minimize",
                icon_minimize(16.0, fg_muted()),
            ))
            .child(win_btn(
                "win-max",
                "Maximize",
                icon_maximize(16.0, fg_muted()),
            ))
            .child(win_btn("win-close", "Close", icon_close(16.0, fg_muted())));
    }

    let mini = music::mini_player(s, scale);

    let right_pin = if insets.trailing > 0.0 { 4.0 } else { GAP };

    let top_fade = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .h(74.0)
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, BG.alpha(0.42)),
                (0.58, BG.alpha(0.16)),
                (1.0, BG.alpha(0.0)),
            ],
        );

    let top_bar = el()
        .key("top-bar")
        .window_drag()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .h(52.0)
        .child(
            row()
                .pin(Some(GAP), Some(8.0), None, None)
                .h(36.0)
                .center_y()
                .child(search),
        )
        .child(
            row()
                .pin(Some(0.0), Some(8.0), Some(0.0), None)
                .h(36.0)
                .justify_center()
                .center_y()
                .gap(GAP)
                .child(nav)
                .children(mini),
        )
        .child(
            row()
                .pin(None, Some(8.0), Some(right_pin), None)
                .h(36.0)
                .center_y()
                .child(tools),
        )
        .children(has_modal_scrim.then(|| {
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
        }));

    let full_bleed_home = !s.media.captures_open
        && s.room == Room::Library
        && s.open_game_id.is_none()
        && !s.library_all_games;

    let content = if !s.media.captures_open && s.room == Room::Discord {
        discord::view(s, scale)
    } else if !s.media.captures_open && s.room == Room::Settings {
        settings::view(s)
    } else if !s.media.captures_open && matches!(s.room, Room::Devices | Room::ExoAi | Room::Apps) {
        pc::view(s, scale)
    } else if full_bleed_home {
        library::view(s, scale)
    } else {
        let page = if s.media.captures_open {
            captures::view(s, scale)
        } else {
            match s.room {
                Room::Library => {
                    if let Some(game) = s.open_game() {
                        game_page::view(s, game, scale)
                    } else {
                        library::view(s, scale)
                    }
                }
                Room::Store => store::view(s, scale),
                Room::Music => music::view(s, scale),
                Room::Devices | Room::ExoAi | Room::Apps => pc::view(s, scale),
                Room::Settings => settings::view(s),
                Room::Discord => discord::view(s, scale),
            }
        };

        col()
            .key("room-scroll")
            .w_full()
            .h_full()
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(page)
    };

    let mut root = el()
        .w_full()
        .h_full()
        .fill(BG)
        .child(content)
        .child(top_fade)
        .child(top_bar);

    if s.capture_menu_open {
        root = root.child(capture_popover(s));
    }
    if s.gamepad_connected {
        root = root.child(gamepad_prompt_bar());
    }
    if s.tour_step.is_some() {
        root = root.child(tour::view(s));
    }
    if s.key_modal_open {
        root = root.child(key_modal::view(s));
    }
    if s.palette_open {
        root = root.child(palette::view(s));
    }
    root
}
