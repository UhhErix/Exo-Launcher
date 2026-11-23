use crate::shell::room_icon;
use crate::state::{AppState, ContextMenuTarget, InputMode, LibrarySort, OpenDropdown, Room};
use exo_kit::{
    ContextMenuSpec, FG, MenuAnchor, MenuEntry, MenuItem, SelectMenuSpec, SelectOption, bad,
    context_menu_layers, fg_faint, icon_camera, icon_key, icon_record_dot, icon_refresh,
    icon_replay, icon_scissors, icon_search, icon_settings, icon_sliders, icon_sparkles,
    select_menu_layers, store_logo,
};
use rune::{Align, El, Side, Tx};
use std::rc::Rc;

fn room_submenu(current: Room) -> Vec<MenuEntry<AppState>> {
    Room::NAV_TABS
        .into_iter()
        .enumerate()
        .map(|(idx, r)| {
            MenuEntry::item(
                MenuItem::new(format!("ctx-sub-room-{}", r.key()), r.label())
                    .icon(room_icon(r, false))
                    .hint(format!("Ctrl+{}", idx + 1))
                    .checked(r == current)
                    .on_select(move |s: &mut AppState, _| {
                        s.input_mode = InputMode::Pointer;
                        s.switch_room(r);
                    }),
            )
        })
        .collect()
}

fn music_service_submenu(current_id: &str) -> Vec<MenuEntry<AppState>> {
    [
        ("spotify", "Spotify", "spotify"),
        ("apple", "Apple Music", "apple-music"),
        ("youtube", "YouTube Music", "youtube"),
    ]
    .into_iter()
    .map(|(id, label, logo_id)| {
        let mut item = MenuItem::new(format!("ctx-sub-music-{id}"), label)
            .checked(current_id == id)
            .on_select(move |s: &mut AppState, _| s.set_music_service(id));
        if let Some(logo) = store_logo(logo_id, 16.0) {
            item = item.icon(logo);
        }
        MenuEntry::item(item)
    })
    .collect()
}

fn text_size_submenu(current: &str) -> Vec<MenuEntry<AppState>> {
    [("small", "Small"), ("normal", "Normal"), ("large", "Large")]
        .into_iter()
        .map(|(val, label)| {
            MenuEntry::item(
                MenuItem::new(format!("ctx-sub-text-{val}"), label)
                    .checked(current == val)
                    .on_select(move |s: &mut AppState, _| s.set_text_size(val)),
            )
        })
        .collect()
}

pub fn context_menu_overlay(s: &AppState, blur: bool, submenu_blur: bool) -> Vec<El> {
    let Some(cm) = s.context_menu else {
        return Vec::new();
    };
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let return_focus = match cm.target {
        ContextMenuTarget::SearchCapsule => Some("search-capsule".to_string()),
        ContextMenuTarget::NavTab(r) => Some(r.key().to_string()),
        ContextMenuTarget::CaptureButton => Some(
            if s.media
                .capture_status
                .as_ref()
                .and_then(|st| st.state.as_ref())
                .is_some_and(|l| l.recording)
            {
                "top-capture-rec".to_string()
            } else {
                "top-capture-btn".to_string()
            },
        ),
        ContextMenuTarget::SettingsButton => Some("nav-settings".to_string()),
    };

    let entries = match cm.target {
        ContextMenuTarget::SearchCapsule => vec![
            MenuEntry::item(
                MenuItem::new("ctx-search-open", "Open Search")
                    .icon(icon_search(15.0, FG))
                    .hint("Ctrl+K")
                    .on_select(|s: &mut AppState, tx| s.open_palette(tx)),
            ),
            MenuEntry::item(
                MenuItem::new("ctx-search-key", "Activate a key")
                    .icon(icon_key(15.0, FG))
                    .on_select(|s: &mut AppState, tx| s.open_key_modal(tx)),
            ),
            MenuEntry::Separator,
            MenuEntry::item(
                MenuItem::new("ctx-search-refresh", "Refresh library")
                    .icon(icon_refresh(15.0, FG))
                    .on_select(|s: &mut AppState, _| s.refresh_library()),
            ),
            MenuEntry::item(
                MenuItem::new("ctx-search-tour", "Guided tour")
                    .icon(icon_sparkles(15.0, FG))
                    .on_select(|s: &mut AppState, _| s.start_tour()),
            ),
        ],
        ContextMenuTarget::NavTab(room) => {
            let mut items = vec![
                MenuEntry::item(
                    MenuItem::new(
                        format!("ctx-nav-open-{}", room.key()),
                        format!("Open {}", room.label()),
                    )
                    .icon(room_icon(room, false))
                    .checked(s.room == room && !s.media.captures_open)
                    .on_select(move |s: &mut AppState, _| {
                        s.input_mode = InputMode::Pointer;
                        s.switch_room(room);
                    }),
                ),
                MenuEntry::item(
                    MenuItem::new("ctx-nav-rooms", "Go to room")
                        .icon(icon_sliders(15.0, FG))
                        .submenu(room_submenu(s.room)),
                ),
                MenuEntry::Separator,
            ];
            match room {
                Room::Library => {
                    items.push(MenuEntry::item(
                        MenuItem::new(
                            "ctx-nav-all-games",
                            if s.library_all_games {
                                "Back to Home deck"
                            } else {
                                "Show all games"
                            },
                        )
                        .icon(room_icon(Room::Library, false))
                        .on_select(|s: &mut AppState, _| {
                            s.switch_room(Room::Library);
                            s.library_all_games = !s.library_all_games;
                        }),
                    ));
                    items.push(MenuEntry::item(
                        MenuItem::new("ctx-nav-refresh-lib", "Refresh library")
                            .icon(icon_refresh(15.0, FG))
                            .on_select(|s: &mut AppState, _| s.refresh_library()),
                    ));
                }
                Room::Store => {
                    items.push(MenuEntry::item(
                        MenuItem::new("ctx-nav-refresh-store", "Refresh Store")
                            .icon(icon_refresh(15.0, FG))
                            .on_select(|s: &mut AppState, _| s.load_store_home(true)),
                    ));
                }
                Room::Music => {
                    let cur_id = s.media.music_service.id();
                    items.push(MenuEntry::item(
                        MenuItem::new("ctx-nav-music-svc", "Music service")
                            .icon(room_icon(Room::Music, false))
                            .submenu(music_service_submenu(cur_id)),
                    ));
                }
                _ => {
                    items.push(MenuEntry::item(
                        MenuItem::new("ctx-nav-search", "Command palette")
                            .icon(icon_search(15.0, FG))
                            .hint("Ctrl+K")
                            .on_select(|s: &mut AppState, tx| s.open_palette(tx)),
                    ));
                }
            }
            items.push(MenuEntry::Separator);
            items.push(MenuEntry::item(
                MenuItem::new("ctx-nav-settings", "Settings")
                    .icon(icon_settings(15.0, FG))
                    .hint("Ctrl+,")
                    .on_select(|s: &mut AppState, _| s.switch_room(Room::Settings)),
            ));
            items
        }
        ContextMenuTarget::CaptureButton => {
            let live = s
                .media
                .capture_status
                .as_ref()
                .and_then(|st| st.state.as_ref());
            let buffering = live.is_some_and(|l| l.buffering || l.replay);
            let recording = live.is_some_and(|l| l.recording);
            let mut rec_item = MenuItem::new(
                "ctx-cap-record",
                if recording {
                    "Stop recording"
                } else {
                    "Start recording"
                },
            )
            .icon(icon_record_dot(15.0, if recording { bad() } else { FG }))
            .hint("Ctrl+Alt+F9")
            .on_select(|s: &mut AppState, _| s.capture_act("record"));
            if recording {
                rec_item = rec_item.danger();
            }
            vec![
                MenuEntry::item(
                    MenuItem::new("ctx-cap-replay", "Save replay")
                        .icon(icon_replay(15.0, if buffering { FG } else { fg_faint() }))
                        .hint("Ctrl+Alt+F10")
                        .disabled(!buffering)
                        .on_select(|s: &mut AppState, _| s.capture_act("saveReplay")),
                ),
                MenuEntry::item(rec_item),
                MenuEntry::item(
                    MenuItem::new("ctx-cap-snip", "Snip screen")
                        .icon(icon_scissors(15.0, FG))
                        .hint("Ctrl+Alt+F8")
                        .on_select(|s: &mut AppState, _| s.capture_act("snip")),
                ),
                MenuEntry::Separator,
                MenuEntry::item(
                    MenuItem::new("ctx-cap-open", "Open Captures")
                        .icon(icon_camera(15.0, FG))
                        .on_select(|s: &mut AppState, _| s.open_captures(None)),
                ),
                MenuEntry::item(
                    MenuItem::new("ctx-cap-settings", "Capture settings")
                        .icon(icon_settings(15.0, FG))
                        .on_select(|s: &mut AppState, _| s.switch_room(Room::Settings)),
                ),
            ]
        }
        ContextMenuTarget::SettingsButton => {
            let text_size = s.settings.text_size.as_deref().unwrap_or("normal");
            let reduce_motion = s.settings.reduce_motion;
            vec![
                MenuEntry::item(
                    MenuItem::new("ctx-set-open", "Open Settings")
                        .icon(icon_settings(15.0, FG))
                        .hint("Ctrl+,")
                        .on_select(|s: &mut AppState, _| s.switch_room(Room::Settings)),
                ),
                MenuEntry::item(
                    MenuItem::new("ctx-set-text-size", "Text size")
                        .icon(icon_sliders(15.0, FG))
                        .submenu(text_size_submenu(text_size)),
                ),
                MenuEntry::item(
                    MenuItem::new("ctx-set-reduce-motion", "Reduce motion")
                        .icon(icon_sparkles(15.0, FG))
                        .checked(reduce_motion)
                        .on_select(move |s: &mut AppState, _| {
                            s.set_bool_setting("reduceMotion", !reduce_motion);
                        }),
                ),
                MenuEntry::Separator,
                MenuEntry::item(
                    MenuItem::new("ctx-set-updates", "Check for updates")
                        .icon(icon_refresh(15.0, FG))
                        .on_select(|s: &mut AppState, _| s.check_app_update()),
                ),
                MenuEntry::item(
                    MenuItem::new("ctx-set-tour", "Guided tour")
                        .icon(icon_sparkles(15.0, FG))
                        .on_select(|s: &mut AppState, _| s.start_tour()),
                ),
            ]
        }
    };

    context_menu_layers(ContextMenuSpec {
        id: "exo-ctx".into(),
        anchor: MenuAnchor::Point(cm.x, cm.y),
        width: 240.0,
        blur,
        submenu_blur,
        open_submenu: cm.open_submenu,
        return_focus,
        gamepad_focus,
        entries,
        on_dismiss: Rc::new(|s: &mut AppState, _: &mut Tx| s.close_context_menu()),
        on_submenu: Rc::new(|s: &mut AppState, idx: Option<usize>, _: &mut Tx| {
            if let Some(ref mut cm) = s.context_menu {
                cm.open_submenu = idx;
            }
        }),
    })
}

pub fn library_more_overlay(s: &AppState, blur: bool) -> Vec<El> {
    if !s.library_more_open {
        return Vec::new();
    }
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let entries = vec![
        MenuEntry::item(
            MenuItem::new("lib-menu-refresh", "Refresh library")
                .icon(icon_refresh(15.0, FG))
                .on_select(|s: &mut AppState, _| s.refresh_library()),
        ),
        MenuEntry::item(
            MenuItem::new("lib-menu-key", "Activate a key")
                .icon(icon_key(15.0, FG))
                .on_select(|s: &mut AppState, tx| s.open_key_modal(tx)),
        ),
        MenuEntry::item(
            MenuItem::new("lib-menu-tour", "Guided tour")
                .icon(icon_sparkles(15.0, FG))
                .on_select(|s: &mut AppState, _| s.start_tour()),
        ),
    ];

    let (anchor_key, side) = if s.library_all_games {
        ("all-games-more-btn", Side::Bottom)
    } else {
        ("home-more-btn", Side::Top)
    };

    context_menu_layers(ContextMenuSpec {
        id: "library-more".into(),
        anchor: MenuAnchor::Element {
            key: anchor_key.into(),
            side,
            align: Align::End,
            gap: 8.0,
        },
        width: 240.0,
        blur,
        submenu_blur: false,
        open_submenu: None,
        return_focus: Some(anchor_key.into()),
        gamepad_focus,
        entries,
        on_dismiss: Rc::new(|s: &mut AppState, _: &mut Tx| s.library_more_open = false),
        on_submenu: Rc::new(|_: &mut AppState, _: Option<usize>, _: &mut Tx| {}),
    })
}

pub fn dropdown_overlay(s: &AppState, blur: bool) -> Vec<El> {
    let Some(which) = s.open_dropdown else {
        return Vec::new();
    };
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let dismiss = Rc::new(|s: &mut AppState, _: &mut Tx| s.close_dropdown());

    match which {
        OpenDropdown::TextSize => {
            let current = s
                .settings
                .text_size
                .as_deref()
                .unwrap_or("normal")
                .to_string();
            select_menu_layers(SelectMenuSpec {
                id: "dd-text-size".into(),
                trigger_key: "set-text-size-select".into(),
                selected: current,
                options: vec![
                    SelectOption::new("small", "Small"),
                    SelectOption::new("normal", "Normal"),
                    SelectOption::new("large", "Large"),
                ],
                width: 170.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(|s: &mut AppState, val: &str, _: &mut Tx| s.set_text_size(val)),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::MusicService => {
            let current = s.media.music_service.id().to_string();
            let options = [
                ("spotify", "Spotify", "spotify"),
                ("apple", "Apple Music", "apple-music"),
                ("youtube", "YouTube Music", "youtube"),
            ]
            .into_iter()
            .map(|(val, label, logo)| {
                let mut opt = SelectOption::new(val, label);
                if let Some(ic) = store_logo(logo, 16.0) {
                    opt = opt.icon(ic);
                }
                opt
            })
            .collect();
            select_menu_layers(SelectMenuSpec {
                id: "dd-music-service".into(),
                trigger_key: "set-music-service-select".into(),
                selected: current,
                options,
                width: 210.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(|s: &mut AppState, val: &str, _: &mut Tx| {
                    s.set_music_service(val);
                }),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::LibrarySort => {
            let current = s.sort.id().to_string();
            let options = LibrarySort::ALL
                .into_iter()
                .map(|sort| SelectOption::new(sort.id(), sort.label()))
                .collect();
            select_menu_layers(SelectMenuSpec {
                id: "dd-library-sort".into(),
                trigger_key: "sort-cycle".into(),
                selected: current,
                options,
                width: 190.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(|s: &mut AppState, val: &str, _: &mut Tx| {
                    s.sort = LibrarySort::from_id(val);
                }),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::GameDisplayMode => {
            let current = s.game_display_mode.clone();
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
                on_select: Rc::new(|s: &mut AppState, val: &str, _: &mut Tx| {
                    s.set_game_display_mode(val);
                }),
                on_dismiss: dismiss,
            })
        }
        OpenDropdown::GameLanguage => {
            let current = s
                .game_props
                .as_ref()
                .and_then(|p| p.language.as_deref())
                .filter(|v| !v.is_empty())
                .unwrap_or("english")
                .to_ascii_lowercase();
            let game_id = s.open_game_id.clone().unwrap_or_default();
            let mut choices = vec![
                ("english", "English"),
                ("spanish", "Spanish"),
                ("french", "French"),
                ("german", "German"),
                ("italian", "Italian"),
                ("portuguese", "Portuguese"),
                ("japanese", "Japanese"),
                ("koreana", "Korean"),
                ("schinese", "Chinese (Simplified)"),
            ];
            let mut options: Vec<SelectOption> = choices
                .drain(..)
                .map(|(v, l)| SelectOption::new(v, l))
                .collect();
            if !options.iter().any(|o| o.value == current) {
                let mut chars = current.chars();
                let pretty = match chars.next() {
                    Some(c) => format!("{}{}", c.to_uppercase(), chars.as_str()),
                    None => current.clone(),
                };
                options.insert(0, SelectOption::new(current.clone(), pretty));
            }
            select_menu_layers(SelectMenuSpec {
                id: "dd-game-language".into(),
                trigger_key: "gp-language-select".into(),
                selected: current,
                options,
                width: 210.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(move |s: &mut AppState, val: &str, _: &mut Tx| {
                    if !game_id.is_empty() {
                        s.set_game_property(&game_id, "language", val);
                    }
                }),
                on_dismiss: dismiss,
            })
        }
    }
}
