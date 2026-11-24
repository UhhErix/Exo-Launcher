use crate::state::{AppState, ContextMenuTarget, HostStatus, InputMode, MiniBarSlot, Room};
use crate::{
    captures, clients_gate, discord, game_page, key_modal, library, menus, music, notice, palette,
    pc, report, settings, settings_account, setup, store, store_media,
};
use exo_kit::{
    BG, CAPTION_BUTTON_GAP, CAPTION_BUTTON_HEIGHT, CAPTION_BUTTON_WIDTH, CAPTION_CAPSULE_PAD,
    CAPTION_TRAILING_INSET, FG, GAP, GLASS_CONTROL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_PILL,
    SPRING_MOVE, SPRING_QUICK, TRANSPARENT, bad, exo_mark, exo_titlebar, fg_muted,
    glass_capsule_opt, glass_control_fill, glass_enter, glass_rim_bottom, glass_rim_top,
    glass_still_control_fill, good, hover_fill, icon_camera, icon_chat, icon_close, icon_gamepad,
    icon_grid, icon_library, icon_minimize, icon_mouse, icon_music, icon_settings, icon_sparkles,
    icon_store, icon_win_maximize, modal_scrim_transition, pressed_fill, scrim_fill, selected_fill,
    set_text_scale, type_body, type_section, type_small,
};
use rune::{Cursor, Cx, El, ScrollSpec, SpringPair, Transition, col, el, row, text};

fn mini_player_transition() -> Transition {
    let enter =
        Transition::offset(0.0, -8.0).combined(glass_enter(0.96, rune::Vec2::new(0.5, 0.5)));
    let exit = Transition::offset(0.0, -6.0).combined(Transition::scale(0.98));
    Transition::asymmetric(enter, exit).springs(SpringPair::new(SPRING_MOVE, SPRING_QUICK))
}

fn room_transition(dir: f32) -> Transition {
    let enter = Transition::offset(12.0 * dir, 0.0).combined(Transition::opacity());
    let exit = Transition::offset(-12.0 * dir, 0.0).combined(Transition::opacity());
    Transition::asymmetric(enter, exit).springs(SpringPair::new(SPRING_MOVE, SPRING_QUICK))
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BlurBudget {
    pub top_bar_search: bool,
    pub top_bar_nav: bool,
    pub top_bar_tools: bool,
    pub mini_player: bool,
    pub discord_voice_bar: bool,
    pub overlay: bool,
    pub submenu: bool,
    pub toast: bool,
}

impl BlurBudget {
    pub fn compute(s: &AppState, has_toast: bool) -> Self {
        let submenu_open = s
            .context_menu
            .as_ref()
            .is_some_and(|cm| cm.open_submenu.is_some());
        let mini_visible = s.mini_bar_slot != MiniBarSlot::Hidden
            && s.room != Room::Music
            && s.media.open_capture_id.is_none()
            && s.media.playback.as_ref().is_some_and(|p| p.has_track());
        let art_under_bar = matches!(
            s.room,
            Room::Library | Room::Store | Room::Discord | Room::Music
        ) || s.media.captures_open
            || s.open_game_id.is_some()
            || s.store_game.is_some();
        let discord_active = s.room == Room::Discord && !s.media.captures_open;
        let voice_bar_visible = discord_active
            && (s
                .discord
                .active_channel()
                .is_some_and(|c| matches!(c.kind.as_str(), "voice" | "stage"))
                || s.discord
                    .voice
                    .channel_id
                    .as_deref()
                    .is_some_and(|vc| s.discord.active_dm_id.as_deref() == Some(vc)));

        Self {
            top_bar_search: art_under_bar,
            top_bar_nav: art_under_bar,
            top_bar_tools: art_under_bar,
            mini_player: mini_visible,
            discord_voice_bar: voice_bar_visible,
            overlay: true,
            submenu: submenu_open,
            toast: has_toast,
        }
    }
}

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
        .backdrop_blur(GLASS_CONTROL_SIGMA)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(icon_gamepad(14.0, FG))
        .child(
            text("A Open · B Back · X Play · Y Search · LB/RB Rooms")
                .font(type_small())
                .color(fg_muted()),
        )
}

pub fn view(s: &AppState, cx: &mut Cx<AppState>) -> El {
    cx.reduced_motion(s.settings.reduce_motion);
    set_text_scale(s.text_scale());
    let has_modal_scrim = s.palette_open
        || s.key_modal_open
        || s.store_ext.lightbox_index.is_some()
        || s.wp4.report_open
        || s.wp4_is_account_sheet_open();
    let budget = BlurBudget::compute(s, !s.notices.is_empty());
    cx.set_titlebar(exo_titlebar());
    cx.caption_color(if has_modal_scrim {
        fg_muted().alpha(0.28)
    } else {
        fg_muted()
    });

    let insets = cx.chrome_insets();
    let scale = cx.env().scale;
    if s.setup.open {
        let mut root = setup::view(s, scale);
        if !s.notices.is_empty() {
            root = root.child(notice::view(s, budget.toast));
        }
        return root;
    }
    if s.wp4_is_clients_gate_open() {
        let mut root = clients_gate::clients_start_view(s);
        if !s.notices.is_empty() {
            root = root.child(notice::view(s, budget.toast));
        }
        return root;
    }

    let mut search = row()
        .key("search-capsule")
        .role("button")
        .label("Search")
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .w(200.0)
        .h(36.0)
        .pl(10.0)
        .pr(12.0)
        .gap(10.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if budget.top_bar_search {
            glass_control_fill()
        } else {
            glass_still_control_fill()
        })
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK)
        .on_press_tx(|s: &mut AppState, tx| {
            s.capture_menu_open = false;
            s.close_context_menu();
            s.close_dropdown();
            if s.palette_open {
                s.close_palette();
            } else {
                s.open_palette(tx);
            }
        })
        .on_secondary_press(|s: &mut AppState, p, _| {
            s.open_context_menu(ContextMenuTarget::SearchCapsule, p.x, p.y);
        })
        .child(exo_mark(18.0, FG))
        .child(text("Search").font(type_body()).color(FG));
    if budget.top_bar_search {
        search = search.backdrop_blur(GLASS_CONTROL_SIGMA);
    }

    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let nav = glass_capsule_opt("nav-capsule", budget.top_bar_nav).children(
        Room::NAV_TABS.into_iter().map(|room| {
            let active = s.room == room && !s.media.captures_open;
            let base = row()
                .h(30.0)
                .px(10.0)
                .gap(6.0)
                .center_y()
                .child(room_icon(room, false))
                .child(text(room.label()).font(type_section()).color(FG));
            let mut tab = el()
                .key(room.key())
                .role("tab")
                .label(room.label())
                .focusable(true)
                .hit()
                .cursor(Cursor::Pointer)
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .h(30.0)
                .radius(RADIUS_PILL)
                .fill(TRANSPARENT)
                .hover(if active { TRANSPARENT } else { hover_fill() })
                .press(if active { TRANSPARENT } else { pressed_fill() })
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    s.input_mode = InputMode::Pointer;
                    s.switch_room(room);
                })
                .on_secondary_press(move |s: &mut AppState, p, _| {
                    s.open_context_menu(ContextMenuTarget::NavTab(room), p.x, p.y);
                })
                .child(base);
            if active {
                tab = tab.child(
                    row()
                        .key("nav-active-pill")
                        .matched("nav-active-pill")
                        .matched_crop()
                        .motion(SPRING_MOVE)
                        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                        .h(30.0)
                        .px(10.0)
                        .gap(6.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(FG)
                        .child(room_icon(room, true))
                        .child(text(room.label()).font(type_section()).color(PRIMARY_FG)),
                );
            }
            if gamepad_focus {
                tab = tab.focus_ring(2.0, 3.0, FG);
            }
            tab
        }),
    );

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
            .cursor(Cursor::Pointer)
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
            .on_secondary_press(|s: &mut AppState, p, _| {
                s.open_context_menu(ContextMenuTarget::CaptureButton, p.x, p.y);
            })
            .child(el().size(8.0, 8.0).radius(4.0).fill(bad()))
            .child(text("REC").font(type_small()).color(FG))
    } else {
        el().key("top-capture-btn")
            .role("button")
            .label("Capture")
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
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
                s.close_context_menu();
                s.close_dropdown();
                s.capture_menu_open = !s.capture_menu_open;
            })
            .on_secondary_press(|s: &mut AppState, p, _| {
                s.open_context_menu(ContextMenuTarget::CaptureButton, p.x, p.y);
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
        .cursor(Cursor::Pointer)
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
        .on_secondary_press(|s: &mut AppState, p, _| {
            s.open_context_menu(ContextMenuTarget::SettingsButton, p.x, p.y);
        })
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
        .pl(CAPTION_CAPSULE_PAD)
        .pr(CAPTION_CAPSULE_PAD)
        .gap(CAPTION_BUTTON_GAP)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if budget.top_bar_tools {
            glass_control_fill()
        } else {
            glass_still_control_fill()
        })
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(capture_btn)
        .child(settings_btn)
        .child(divider);
    if budget.top_bar_tools {
        tools = tools.backdrop_blur(GLASS_CONTROL_SIGMA);
    }

    if insets.trailing > 0.0 {
        let reserve = (insets.trailing - CAPTION_TRAILING_INSET).max(0.0);
        tools = tools.child(el().size(reserve, CAPTION_BUTTON_HEIGHT));
    } else {
        let win_btn = |key: &'static str, label: &'static str, icon: El| {
            el().key(key)
                .role("button")
                .label(label)
                .cursor(Cursor::Pointer)
                .size(CAPTION_BUTTON_WIDTH, CAPTION_BUTTON_HEIGHT)
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
                icon_win_maximize(16.0, fg_muted()),
            ))
            .child(win_btn("win-close", "Close", icon_close(16.0, fg_muted())));
    }

    let mini = (s.mini_bar_slot != MiniBarSlot::Hidden)
        .then(|| music::mini_player(s, scale, s.mini_bar_slot))
        .flatten()
        .map(|m| {
            let m = if budget.mini_player {
                m
            } else {
                m.backdrop_blur(0.0).fill(glass_still_control_fill())
            };
            m.transition(mini_player_transition())
        });

    let right_pin = GAP;

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
        .on_layout(|s: &mut AppState, r| {
            if r[2] > 0.0 {
                let slot = MiniBarSlot::from_width(r[2]);
                if s.mini_bar_slot != slot {
                    s.mini_bar_slot = slot;
                }
            }
        })
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
                .child(nav),
        )
        .child(
            row()
                .pin(None, Some(8.0), Some(right_pin), None)
                .h(36.0)
                .center_y()
                .gap(8.0)
                .children(mini)
                .child(tools),
        )
        .children(has_modal_scrim.then(|| {
            el().key("top-bar-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .transition(modal_scrim_transition())
        }));

    let room_key = if s.media.captures_open {
        "room-captures"
    } else {
        match s.room {
            Room::Library => "room-library",
            Room::Store => "room-store",
            Room::Discord => "room-discord",
            Room::Music => "room-music",
            Room::Devices => "room-devices",
            Room::ExoAi => "room-exo-ai",
            Room::Apps => "room-apps",
            Room::Settings => "room-settings",
        }
    };

    let full_bleed_home =
        s.room == Room::Library && s.open_game_id.is_none() && !s.library_all_games;

    let content = if s.room == Room::Discord {
        discord::view(s, scale, budget)
    } else if s.room == Room::Settings {
        settings::view(s)
    } else if matches!(s.room, Room::Devices | Room::ExoAi | Room::Apps) {
        pc::view(s, scale)
    } else if full_bleed_home {
        library::view(s, scale)
    } else {
        let page = match s.room {
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
            Room::Discord => discord::view(s, scale, budget),
        };

        col()
            .key(format!("{room_key}-scroll"))
            .w_full()
            .h_full()
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .child(page)
    };

    let room_stage = el()
        .key(room_key)
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .transition(room_transition(s.room_dir))
        .child(content);

    let mut root = el()
        .w_full()
        .h_full()
        .fill(BG)
        .child(room_stage)
        .child(top_fade)
        .child(top_bar);

    if !s.media.captures_open && s.room == Room::Discord {
        root = root.children(discord::overlay_layers(s, budget.overlay));
    }
    if s.media.captures_open {
        root = root.child(captures::view(s, scale));
    }
    if s.capture_menu_open {
        root = root.children(captures::popover_layers(s, budget.overlay));
    }
    if s.library_more_open {
        root = root.children(menus::library_more_overlay(s, budget.overlay));
    }
    if s.library_add_open {
        root = root.children(menus::library_add_overlay(s, budget.overlay));
    }
    if s.library_filter_open {
        root = root.children(crate::library::library_filter_overlay(s, budget.overlay));
    }
    if s.pc.part_menu.is_some() {
        root = root.children(pc::part_menu_layers(s, budget.overlay));
    }
    if s.open_dropdown.is_some() {
        root = root.children(menus::dropdown_overlay(s, budget.overlay));
    }
    if s.context_menu.is_some() {
        root = root.children(menus::context_menu_overlay(
            s,
            budget.overlay,
            budget.submenu,
        ));
    }
    if s.gamepad_connected {
        root = root.child(gamepad_prompt_bar());
    }
    if s.setup.beat.is_some() {
        root = root.child(setup::coach(s));
    }
    if s.setup.return_to.is_some() && !s.setup.open {
        root = root.child(setup::return_bar(s));
    }
    if s.store_ext.lightbox_index.is_some() {
        root = root.children(store_media::lightbox_layers(s, scale, budget.overlay));
    }
    if s.key_modal_open {
        root = root.children(key_modal::layers(s, budget.overlay));
    }
    if s.wp4.report_open {
        root = root.children(report::layers(s, budget.overlay));
    }
    if s.wp4_is_account_sheet_open() {
        root = root.children(settings_account::account_sheet_layers(s, budget.overlay));
    }
    if matches!(s.status, HostStatus::Error(_)) && !s.wp4.report_open {
        root = root.child(
            settings::set_btn("host-error-report", "Report a problem", None)
                .pin(None, None, Some(GAP), Some(GAP))
                .on_press(|s: &mut AppState| s.wp4_open_report()),
        );
    }
    if s.palette_open {
        root = root.children(palette::layers(s, budget.overlay));
    }
    if !s.notices.is_empty() {
        root = root.child(notice::view(s, budget.toast));
    }
    root
}

#[cfg(test)]
mod tests {
    use super::*;
    use rune::{Env, Runtime};

    #[test]
    fn titlebar_tools_capsule_matches_react_geometry() {
        let mut rt = Runtime::new(
            AppState::default(),
            view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.frame(0.0);
        let (tools, _) = rt.presented("titlebar-tools").expect("titlebar-tools");
        assert!((tools[0] - 1717.0).abs() < 1.0, "tools x={}", tools[0]);
        assert!((tools[1] - 8.0).abs() < 0.5, "tools y={}", tools[1]);
        assert!((tools[2] - 193.0).abs() < 1.0, "tools w={}", tools[2]);
        assert!((tools[3] - 36.0).abs() < 0.5, "tools h={}", tools[3]);
        assert!(
            ((tools[0] + tools[2]) - (1920.0 - GAP)).abs() < 1.0,
            "tools right={} expected {}",
            tools[0] + tools[2],
            1920.0 - GAP
        );
    }
}
