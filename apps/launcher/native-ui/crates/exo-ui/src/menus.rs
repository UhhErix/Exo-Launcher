use crate::discord_admin::{MemberDialogKind, ReportTarget, ServerSection, TIMEOUT_DURATIONS};
use crate::discord_state::DiscordSettingsPage;
use crate::discord_voice_state::VoicePanel;
use crate::shell::room_icon;
use crate::state::{
    AppState, ContextMenuTarget, InputMode, LibrarySort, NoticeTone, OpenDropdown, Room,
};
use exo_kit::{
    ContextMenuSpec, FG, MenuAnchor, MenuEntry, MenuItem, SelectMenuSpec, SelectOption, bad,
    context_menu_layers, context_menu_layers_live, fg_faint, icon_bell_off, icon_camera, icon_chat,
    icon_check, icon_copy, icon_download, icon_edit, icon_flag, icon_folder, icon_forward,
    icon_gamepad, icon_key, icon_link, icon_pin, icon_play, icon_record_dot, icon_refresh,
    icon_replay, icon_reply, icon_scissors, icon_search, icon_settings, icon_sliders, icon_smile,
    icon_sparkles, icon_stop, icon_store, icon_trash, icon_user, icon_user_plus, icon_volume,
    icon_wrench, select_menu_layers, store_logo,
};
use rune::{Align, El, Side, TextEdit, Tx};
use serde_json::json;
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

const STATUS_DURATIONS: [(i32, &str); 6] = [
    (15, "For 15 minutes"),
    (60, "For 1 hour"),
    (480, "For 8 hours"),
    (1440, "For 24 hours"),
    (4320, "For 3 days"),
    (0, "Forever"),
];

const MUTE_DURATIONS: [(i32, &str); 6] = [
    (15, "For 15 minutes"),
    (60, "For 1 hour"),
    (180, "For 3 hours"),
    (480, "For 8 hours"),
    (1440, "For 24 hours"),
    (0, "Until I turn it back on"),
];

fn status_duration_submenu(code: &'static str) -> Vec<MenuEntry<AppState>> {
    STATUS_DURATIONS
        .into_iter()
        .map(|(mins, label)| {
            MenuEntry::item(
                MenuItem::new(format!("dc-status-{code}-{mins}"), label)
                    .on_select(move |s: &mut AppState, _| s.set_discord_status_for(code, mins)),
            )
        })
        .collect()
}

fn channel_mute_submenu(channel_id: &str) -> Vec<MenuEntry<AppState>> {
    MUTE_DURATIONS
        .into_iter()
        .map(|(mins, label)| {
            let cid = channel_id.to_string();
            MenuEntry::item(
                MenuItem::new(format!("dc-ch-mute-{cid}-{mins}"), label).on_select(
                    move |s: &mut AppState, _| {
                        s.discord_channel_notify(&cid, Some(true), (mins > 0).then_some(mins), None)
                    },
                ),
            )
        })
        .collect()
}

fn channel_notify_submenu(channel_id: &str) -> Vec<MenuEntry<AppState>> {
    [
        (3, "Use category default"),
        (0, "All messages"),
        (1, "Only @mentions"),
        (2, "Nothing"),
    ]
    .into_iter()
    .map(|(lvl, label)| {
        let cid = channel_id.to_string();
        MenuEntry::item(
            MenuItem::new(format!("dc-ch-notify-{cid}-{lvl}"), label).on_select(
                move |s: &mut AppState, _| s.discord_channel_notify(&cid, None, None, Some(lvl)),
            ),
        )
    })
    .collect()
}

fn server_mute_submenu(guild_id: &str) -> Vec<MenuEntry<AppState>> {
    MUTE_DURATIONS
        .into_iter()
        .map(|(mins, label)| {
            let gid = guild_id.to_string();
            MenuEntry::item(
                MenuItem::new(format!("dc-srv-mute-{gid}-{mins}"), label).on_select(
                    move |s: &mut AppState, _| {
                        let body = if mins > 0 {
                            json!({ "guildId": gid, "muted": true, "minutes": mins })
                        } else {
                            json!({ "guildId": gid, "muted": true })
                        };
                        s.discord_server_action("mute", body);
                    },
                ),
            )
        })
        .collect()
}

fn server_notify_submenu(guild_id: &str) -> Vec<MenuEntry<AppState>> {
    [(0, "All messages"), (1, "Only @mentions"), (2, "Nothing")]
        .into_iter()
        .map(|(lvl, label)| {
            let gid = guild_id.to_string();
            MenuEntry::item(
                MenuItem::new(format!("dc-srv-notify-{gid}-{lvl}"), label).on_select(
                    move |s: &mut AppState, _| {
                        s.discord_server_action("notify", json!({ "guildId": gid, "level": lvl }));
                    },
                ),
            )
        })
        .collect()
}

fn voice_volume_submenu(user_id: &str, current: f32) -> Vec<MenuEntry<AppState>> {
    [25.0, 50.0, 75.0, 100.0, 150.0, 200.0]
        .into_iter()
        .map(|vol| {
            let uid = user_id.to_string();
            MenuEntry::item(
                MenuItem::new(
                    format!("dc-vol-{uid}-{}", vol as i32),
                    format!("{}%", vol as i32),
                )
                .checked((current - vol).abs() < 1.0)
                .on_select(move |s: &mut AppState, _| {
                    s.set_discord_user_mix(&uid, Some(vol), None, None)
                }),
            )
        })
        .collect()
}

fn discord_status_entries(s: &AppState) -> Vec<MenuEntry<AppState>> {
    let cur_status = s
        .discord
        .home
        .me
        .as_ref()
        .and_then(|m| m.status.as_deref())
        .unwrap_or("online");
    let me_id = s
        .discord
        .home
        .me
        .as_ref()
        .map(|m| m.id.clone())
        .unwrap_or_default();
    let has_custom = s
        .discord
        .custom_status
        .as_deref()
        .is_some_and(|t| !t.trim().is_empty());

    let mut acc_entries: Vec<MenuEntry<AppState>> = s
        .discord
        .accounts
        .iter()
        .map(|acc| {
            let aid = acc.id.clone();
            MenuEntry::item(
                MenuItem::new(format!("dc-switch-acc-{}", acc.id), acc.name.clone())
                    .checked(acc.current)
                    .disabled(acc.current)
                    .on_select(move |s: &mut AppState, _| s.switch_discord_account(&aid)),
            )
        })
        .collect();
    if !acc_entries.is_empty() {
        acc_entries.push(MenuEntry::Separator);
    }
    acc_entries.push(MenuEntry::item(
        MenuItem::new("dc-add-account", "Add an account")
            .disabled(s.discord.accounts.len() >= 5)
            .on_select(|s: &mut AppState, _| s.start_discord_signin()),
    ));

    vec![
        MenuEntry::item(
            MenuItem::new("dc-set-status-online", "Online")
                .checked(cur_status == "online")
                .on_select(|s: &mut AppState, _| s.set_discord_status("online")),
        ),
        MenuEntry::item(
            MenuItem::new("dc-set-status-idle", "Idle")
                .checked(cur_status == "idle")
                .submenu(status_duration_submenu("idle")),
        ),
        MenuEntry::item(
            MenuItem::new("dc-set-status-dnd", "Do not disturb")
                .checked(cur_status == "dnd")
                .submenu(status_duration_submenu("dnd")),
        ),
        MenuEntry::item(
            MenuItem::new("dc-set-status-invisible", "Invisible")
                .checked(cur_status == "invisible")
                .submenu(status_duration_submenu("invisible")),
        ),
        MenuEntry::Separator,
        MenuEntry::item(
            MenuItem::new(
                "dc-custom-status",
                if has_custom {
                    "Edit custom status"
                } else {
                    "Set a custom status"
                },
            )
            .on_select(|s: &mut AppState, _| {
                let existing = s.discord.custom_status.clone().unwrap_or_default();
                s.discord.custom_status_input = TextEdit::new(&existing);
                s.discord.custom_status_open = true;
            }),
        ),
        MenuEntry::item(MenuItem::new("dc-edit-profile", "Edit profile").on_select(
            |s: &mut AppState, _| {
                s.open_discord_settings(DiscordSettingsPage::Account);
            },
        )),
        MenuEntry::item(
            MenuItem::new("dc-switch-accounts", "Switch accounts").submenu(acc_entries),
        ),
        MenuEntry::Separator,
        MenuEntry::item(
            MenuItem::new("dc-voice-settings", "Voice settings").on_select(
                |s: &mut AppState, _| {
                    s.open_discord_settings(DiscordSettingsPage::Voice);
                },
            ),
        ),
        MenuEntry::item(
            MenuItem::new("dc-open-shop", "Shop")
                .icon(icon_store(15.0, FG))
                .on_select(|s: &mut AppState, _| {
                    s.discord.shop_open = true;
                    s.discord.inbox_open = false;
                }),
        ),
        MenuEntry::Separator,
        MenuEntry::item(
            MenuItem::new("dc-copy-my-id", "Copy user ID")
                .icon(icon_copy(15.0, FG))
                .disabled(me_id.is_empty())
                .on_select(move |s: &mut AppState, tx| {
                    tx.copy(me_id.clone());
                    s.notice = Some("Copied user ID".into());
                }),
        ),
    ]
}

fn discord_audio_quick_entries(s: &AppState, input: bool) -> Vec<MenuEntry<AppState>> {
    let mut items = Vec::new();
    items.push(MenuEntry::Section(
        if input {
            "INPUT DEVICE"
        } else {
            "OUTPUT DEVICE"
        }
        .into(),
    ));
    let (devices, chosen) = if let Some(ref a) = s.discord.voice_ui.audio {
        if input {
            (a.inputs.as_slice(), a.input.as_deref().unwrap_or(""))
        } else {
            (a.outputs.as_slice(), a.output.as_deref().unwrap_or(""))
        }
    } else {
        (&[][..], "")
    };
    items.push(MenuEntry::item(
        MenuItem::new(
            if input {
                "dc-aq-in-default"
            } else {
                "dc-aq-out-default"
            },
            "Default",
        )
        .checked(chosen.is_empty())
        .on_select(move |s: &mut AppState, _| {
            if input {
                s.set_discord_audio(json!({ "input": "" }));
            } else {
                s.set_discord_audio(json!({ "output": "" }));
            }
        }),
    ));
    for d in devices {
        let did = d.id.clone();
        items.push(MenuEntry::item(
            MenuItem::new(
                format!("dc-aq-{}-{}", if input { "in" } else { "out" }, d.id),
                d.name.clone(),
            )
            .checked(chosen == d.id)
            .on_select(move |s: &mut AppState, _| {
                if input {
                    s.set_discord_audio(json!({ "input": did }));
                } else {
                    s.set_discord_audio(json!({ "output": did }));
                }
            }),
        ));
    }
    items.push(MenuEntry::Separator);
    items.push(MenuEntry::item(
        MenuItem::new("dc-aq-voice-settings", "Voice settings")
            .icon(icon_settings(15.0, FG))
            .on_select(|s: &mut AppState, _| s.open_discord_settings(DiscordSettingsPage::Voice)),
    ));
    items
}

pub fn context_menu_overlay(s: &AppState, blur: bool, submenu_blur: bool) -> Vec<El> {
    let Some(ref cm) = s.context_menu else {
        return Vec::new();
    };
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let return_focus = match &cm.target {
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
        ContextMenuTarget::GameCard(id) => Some(format!("tile-{id}")),
        ContextMenuTarget::DiscordStatusMenu => Some("dc-me-trigger".to_string()),
        ContextMenuTarget::DiscordAudioQuick { input } => {
            Some(if *input { "dc-me-mute" } else { "dc-me-deafen" }.to_string())
        }
        _ => None,
    };

    let (menu_id, anchor, width) = match &cm.target {
        ContextMenuTarget::DiscordStatusMenu => (
            "dc-status-menu".to_string(),
            MenuAnchor::Element {
                key: "dc-me-trigger".to_string(),
                side: Side::Top,
                align: Align::Start,
                gap: 8.0,
            },
            240.0,
        ),
        ContextMenuTarget::DiscordAudioQuick { input } => (
            "dc-audio-quick-menu".to_string(),
            MenuAnchor::Element {
                key: if *input { "dc-me-mute" } else { "dc-me-deafen" }.to_string(),
                side: Side::Top,
                align: Align::Start,
                gap: 8.0,
            },
            240.0,
        ),
        ContextMenuTarget::DiscordGoLiveMenu => (
            "dc-golive-menu".to_string(),
            MenuAnchor::Element {
                key: "dc-voice-share".to_string(),
                side: Side::Top,
                align: Align::Center,
                gap: 10.0,
            },
            240.0,
        ),
        _ => (
            "exo-ctx".to_string(),
            match cm.anchor_key.clone() {
                Some(key) => MenuAnchor::Element {
                    key,
                    side: Side::Bottom,
                    align: Align::End,
                    gap: 6.0,
                },
                None => MenuAnchor::Point(cm.x, cm.y),
            },
            240.0,
        ),
    };

    let entries = match cm.target.clone() {
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
        ContextMenuTarget::GameCard(game_id) => {
            let game = s.games.iter().find(|g| g.id == *game_id);
            let is_running = game.is_some_and(|g| g.is_running);
            let is_stop = game.is_some_and(|g| g.is_running && g.can_stop);
            let is_installed = game.is_some_and(|g| g.installed);
            let is_update =
                game.is_some_and(|g| g.update_available && g.installed && !g.is_running);
            let is_hidden = s
                .settings
                .hidden_games
                .iter()
                .any(|h| h.eq_ignore_ascii_case(&game_id));
            let primary_label = if is_stop {
                "Stop"
            } else if is_running {
                "Running"
            } else if is_update {
                "Update"
            } else if is_installed {
                "Play"
            } else {
                "Install"
            };
            let primary_icon = if is_stop {
                icon_stop(15.0, FG)
            } else if is_installed {
                icon_play(15.0, FG)
            } else {
                icon_download(15.0, FG)
            };
            let gid_primary = game_id.clone();
            let mut items = vec![MenuEntry::item(
                MenuItem::new("ctx-game-primary", primary_label)
                    .icon(primary_icon)
                    .disabled(is_running && !is_stop)
                    .on_select(move |s: &mut AppState, _| {
                        if is_stop {
                            s.stop_game(&gid_primary);
                        } else if !is_installed {
                            s.install_game(&gid_primary);
                        } else {
                            s.launch_game(&gid_primary);
                        }
                    }),
            )];
            if is_installed {
                let gid_folder = game_id.clone();
                items.push(MenuEntry::item(
                    MenuItem::new("ctx-game-folder", "Open folder")
                        .icon(icon_folder(15.0, FG))
                        .on_select(move |s: &mut AppState, _| s.open_game_folder(&gid_folder)),
                ));
                let gid_props = game_id.clone();
                items.push(MenuEntry::item(
                    MenuItem::new("ctx-game-props", "Properties")
                        .icon(icon_wrench(15.0, FG))
                        .on_select(move |s: &mut AppState, _| {
                            s.open_game_page(&gid_props);
                            s.props_open = true;
                        }),
                ));
            }
            let gid_hide = game_id.clone();
            items.push(MenuEntry::item(
                MenuItem::new("ctx-game-hide", if is_hidden { "Unhide" } else { "Hide" })
                    .icon(icon_sliders(15.0, FG))
                    .on_select(move |s: &mut AppState, _| s.toggle_hide_game(&gid_hide)),
            ));
            items.push(MenuEntry::item(
                MenuItem::new("ctx-game-new-folder", "New folder")
                    .icon(icon_folder(15.0, FG))
                    .on_select(|s: &mut AppState, _| {
                        s.library_all_games = true;
                        s.push_notice("Folder management coming soon", NoticeTone::Neutral);
                    }),
            ));
            items
        }
        ContextMenuTarget::DiscordStatusMenu => discord_status_entries(s),
        ContextMenuTarget::DiscordAudioQuick { input } => discord_audio_quick_entries(s, input),
        ContextMenuTarget::DiscordDm {
            dm_id,
            user_id,
            name,
            username,
            unread,
            muted,
            group,
        } => {
            let pinned = s.is_dm_pinned(&dm_id);
            let mut items = vec![
                MenuEntry::item(
                    MenuItem::new("dc-dm-read", "Mark as read")
                        .disabled(!unread)
                        .on_select({
                            let cid = dm_id.clone();
                            move |s: &mut AppState, _| s.discord_channel_action(&cid, "markRead")
                        }),
                ),
                MenuEntry::item(
                    MenuItem::new("dc-dm-pin", if pinned { "Unpin DM" } else { "Pin DM" })
                        .icon(icon_pin(15.0, FG))
                        .on_select({
                            let cid = dm_id.clone();
                            move |s: &mut AppState, _| s.toggle_pin_dm(&cid)
                        }),
                ),
                MenuEntry::Separator,
            ];

            if !group {
                if let Some(uid) = user_id {
                    let is_friend = s
                        .discord
                        .friends
                        .iter()
                        .any(|f| f.id == uid && f.kind == "friend");
                    let is_ignored = s.discord.ignored_users.contains(&uid);
                    let handle = username
                        .as_deref()
                        .filter(|u| !u.is_empty())
                        .unwrap_or(&name)
                        .to_string();

                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-profile", "Profile")
                            .icon(icon_user(15.0, FG))
                            .on_select({
                                let cid = dm_id.clone();
                                move |s: &mut AppState, _| {
                                    s.open_discord_dm(&cid);
                                    s.discord.show_members = true;
                                }
                            }),
                    ));
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-mention", "Mention").on_select({
                            let cid = dm_id.clone();
                            let u = uid.clone();
                            move |s: &mut AppState, _| {
                                s.open_discord_dm(&cid);
                                s.mention_user_in_composer(&u);
                            }
                        }),
                    ));
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-message", "Message")
                            .icon(icon_chat(15.0, FG))
                            .on_select({
                                let cid = dm_id.clone();
                                move |s: &mut AppState, _| s.open_discord_dm(&cid)
                            }),
                    ));
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-call", "Start a call")
                            .icon(icon_volume(15.0, FG))
                            .on_select({
                                let cid = dm_id.clone();
                                move |s: &mut AppState, _| {
                                    s.open_discord_dm(&cid);
                                    s.join_discord_voice(None, &cid);
                                    s.discord_ring_dm(&cid);
                                }
                            }),
                    ));
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-close", "Close DM").on_select({
                            let cid = dm_id.clone();
                            move |s: &mut AppState, _| s.discord_channel_action(&cid, "closeDm")
                        }),
                    ));
                    items.push(MenuEntry::Separator);
                    items.push(MenuEntry::item(
                        MenuItem::new(
                            "dc-dm-friend",
                            if is_friend {
                                "Remove friend"
                            } else {
                                "Add friend"
                            },
                        )
                        .icon(icon_user_plus(15.0, FG))
                        .on_select({
                            let u = uid.clone();
                            move |s: &mut AppState, _| {
                                if is_friend {
                                    s.remove_discord_friend(&u);
                                } else {
                                    s.discord_friend_action("add", &u);
                                }
                            }
                        }),
                    ));
                    items.push(MenuEntry::item(
                        MenuItem::new(
                            "dc-dm-ignore",
                            if is_ignored { "Unignore" } else { "Ignore" },
                        )
                        .on_select({
                            let u = uid.clone();
                            move |s: &mut AppState, _| s.toggle_ignore_user(&u)
                        }),
                    ));
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-block", "Block").danger().on_select({
                            let u = uid.clone();
                            move |s: &mut AppState, _| s.discord_friend_action("block", &u)
                        }),
                    ));
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-report", "Report")
                            .icon(icon_flag(15.0, bad()))
                            .danger()
                            .on_select({
                                let u = uid.clone();
                                move |s: &mut AppState, tx| {
                                    tx.copy(u.clone());
                                    s.notice = Some("Copied user ID for reporting".into());
                                }
                            }),
                    ));
                    items.push(MenuEntry::Separator);
                    if muted {
                        items.push(MenuEntry::item(
                            MenuItem::new("dc-dm-unmute", format!("Unmute @{handle}"))
                                .icon(icon_bell_off(15.0, FG))
                                .on_select({
                                    let cid = dm_id.clone();
                                    move |s: &mut AppState, _| {
                                        s.discord_channel_notify(&cid, Some(false), None, None)
                                    }
                                }),
                        ));
                    } else {
                        items.push(MenuEntry::item(
                            MenuItem::new("dc-dm-mute", format!("Mute @{handle}"))
                                .icon(icon_bell_off(15.0, FG))
                                .submenu(channel_mute_submenu(&dm_id)),
                        ));
                    }
                    items.push(MenuEntry::Separator);
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-copy-user", "Copy username")
                            .icon(icon_copy(15.0, FG))
                            .on_select(move |s: &mut AppState, tx| {
                                tx.copy(handle.clone());
                                s.notice = Some("Copied username".into());
                            }),
                    ));
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-copy-uid", "Copy user ID")
                            .icon(icon_copy(15.0, FG))
                            .on_select(move |s: &mut AppState, tx| {
                                tx.copy(uid.clone());
                                s.notice = Some("Copied user ID".into());
                            }),
                    ));
                }
            } else {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-dm-group-call", "Start a call")
                        .icon(icon_volume(15.0, FG))
                        .on_select({
                            let cid = dm_id.clone();
                            move |s: &mut AppState, _| {
                                s.open_discord_dm(&cid);
                                s.join_discord_voice(None, &cid);
                                s.discord_ring_dm(&cid);
                            }
                        }),
                ));
                items.push(MenuEntry::item(
                    MenuItem::new("dc-dm-group-leave", "Leave group")
                        .danger()
                        .on_select({
                            let cid = dm_id.clone();
                            move |s: &mut AppState, _| s.discord_channel_action(&cid, "closeDm")
                        }),
                ));
                items.push(MenuEntry::Separator);
                if muted {
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-group-unmute", "Unmute group")
                            .icon(icon_bell_off(15.0, FG))
                            .on_select({
                                let cid = dm_id.clone();
                                move |s: &mut AppState, _| {
                                    s.discord_channel_notify(&cid, Some(false), None, None)
                                }
                            }),
                    ));
                } else {
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-dm-group-mute", "Mute group")
                            .icon(icon_bell_off(15.0, FG))
                            .submenu(channel_mute_submenu(&dm_id)),
                    ));
                }
                items.push(MenuEntry::Separator);
            }

            items.push(MenuEntry::item(
                MenuItem::new("dc-dm-copy-cid", "Copy channel ID")
                    .icon(icon_copy(15.0, FG))
                    .on_select(move |s: &mut AppState, tx| {
                        tx.copy(dm_id.clone());
                        s.notice = Some("Copied channel ID".into());
                    }),
            ));
            items
        }
        ContextMenuTarget::DiscordChannel {
            guild_id,
            channel_id,
            name: _,
            kind,
            unread,
            muted,
            can_edit,
            can_manage,
        } => {
            let link = format!("https://discord.com/channels/{guild_id}/{channel_id}");
            let is_voice = matches!(kind.as_str(), "voice" | "stage");
            let ch_info = s
                .discord
                .channels_by_guild
                .get(&guild_id)
                .and_then(|c| c.channels.iter().find(|x| x.id == channel_id));
            let ch_status = ch_info.and_then(|c| c.status.clone());
            let can_set_status = ch_info.is_some_and(|c| c.can_set_status);
            let ch_name = ch_info
                .map(|c| c.name.clone())
                .unwrap_or_else(|| "channel".into());
            let guild_name = s
                .discord
                .home
                .guilds
                .iter()
                .find(|g| g.id == guild_id)
                .map(|g| g.name.clone())
                .unwrap_or_else(|| "Server".into());
            let mut items = vec![
                MenuEntry::item(
                    MenuItem::new("dc-ch-read", "Mark as read")
                        .disabled(!unread)
                        .on_select({
                            let cid = channel_id.clone();
                            move |s: &mut AppState, _| s.discord_channel_action(&cid, "read")
                        }),
                ),
                MenuEntry::item(MenuItem::new("dc-ch-invite", "Invite people").on_select({
                    let cid = channel_id.clone();
                    let gname = guild_name.clone();
                    let cname = ch_name.clone();
                    move |s: &mut AppState, _| s.open_discord_invite_dialog(&gname, &cid, &cname)
                })),
                MenuEntry::item(MenuItem::new("dc-ch-link", "Copy link").on_select(
                    move |s: &mut AppState, tx| {
                        tx.copy(link.clone());
                        s.notice = Some("Copied channel link".into());
                    },
                )),
                MenuEntry::Separator,
            ];
            if is_voice && (can_set_status || ch_status.is_some()) {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-ch-vstatus", "Set channel status").on_select({
                        let cid = channel_id.clone();
                        let cur = ch_status.clone();
                        move |s: &mut AppState, _| {
                            s.open_voice_status_dialog(&cid, cur.as_deref());
                        }
                    }),
                ));
                if ch_status.is_some() {
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-ch-vstatus-clear", "Clear channel status").on_select({
                            let cid = channel_id.clone();
                            move |s: &mut AppState, _| s.save_discord_voice_status(&cid, "")
                        }),
                    ));
                }
                items.push(MenuEntry::Separator);
            }
            if muted {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-ch-unmute", "Unmute channel").on_select({
                        let cid = channel_id.clone();
                        move |s: &mut AppState, _| {
                            s.discord_channel_notify(&cid, Some(false), None, None)
                        }
                    }),
                ));
            } else {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-ch-mute", "Mute channel")
                        .submenu(channel_mute_submenu(&channel_id)),
                ));
            }
            if !is_voice {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-ch-notify", "Notification settings")
                        .submenu(channel_notify_submenu(&channel_id)),
                ));
            }
            if can_edit || can_manage {
                items.push(MenuEntry::Separator);
                if can_edit {
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-ch-edit", "Edit channel").on_select({
                            let cid = channel_id.clone();
                            move |s: &mut AppState, _| s.open_channel_settings(&cid)
                        }),
                    ));
                }
                if can_manage {
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-ch-delete", "Delete channel")
                            .danger()
                            .on_select({
                                let cid = channel_id.clone();
                                move |s: &mut AppState, _| s.discord_channel_action(&cid, "delete")
                            }),
                    ));
                }
            }
            if s.discord.developer_mode {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-ch-copy-id", "Copy channel ID")
                        .icon(icon_copy(15.0, FG))
                        .on_select(move |s: &mut AppState, tx| {
                            tx.copy(channel_id.clone());
                            s.notice = Some("Copied channel ID".into());
                        }),
                ));
            }
            items
        }
        ContextMenuTarget::DiscordCategory {
            guild_id,
            category_id,
            name: _,
        } => {
            let collapsed = s.discord.collapsed_categories.contains(&category_id);
            vec![
                MenuEntry::item(MenuItem::new("dc-cat-read", "Mark as read").on_select({
                    let gid = guild_id.clone();
                    move |s: &mut AppState, _| s.discord_mark_guild_read(&gid)
                })),
                MenuEntry::item(
                    MenuItem::new(
                        "dc-cat-toggle",
                        if collapsed {
                            "Expand category"
                        } else {
                            "Collapse category"
                        },
                    )
                    .on_select({
                        let cid = category_id.clone();
                        move |s: &mut AppState, _| s.toggle_discord_category(&cid)
                    }),
                ),
                MenuEntry::item(
                    MenuItem::new("dc-cat-collapse-all", "Collapse all categories").on_select({
                        let gid = guild_id.clone();
                        move |s: &mut AppState, _| s.collapse_all_categories(&gid)
                    }),
                ),
                MenuEntry::Separator,
                MenuEntry::item(
                    MenuItem::new("dc-cat-copy-id", "Copy category ID")
                        .icon(icon_copy(15.0, FG))
                        .on_select(move |s: &mut AppState, tx| {
                            tx.copy(category_id.clone());
                            s.notice = Some("Copied category ID".into());
                        }),
                ),
            ]
        }
        ContextMenuTarget::DiscordServer {
            guild_id,
            name,
            muted,
            owner,
            icon: _,
        } => {
            let hide_muted = s.discord.hide_muted_guilds.contains(&guild_id);
            let chans = s.discord.channels_by_guild.get(&guild_id);
            let can_manage = chans.is_some_and(|c| c.can_manage);
            let can_events = chans.is_some_and(|c| c.can_manage_events);
            let show_all = chans.is_some_and(|c| !c.opt_in);
            let has_guide = chans.and_then(|c| c.rows.as_ref()).is_some_and(|r| r.guide);
            let invite_ch = chans.and_then(|c| {
                c.channels
                    .iter()
                    .find(|x| {
                        s.discord.active_channel_id.as_deref() == Some(x.id.as_str())
                            && x.kind != "category"
                    })
                    .or_else(|| c.channels.iter().find(|x| x.kind == "text"))
                    .cloned()
            });
            let mut items = vec![
                MenuEntry::item(MenuItem::new("dc-srv-read", "Mark as read").on_select({
                    let gid = guild_id.clone();
                    move |s: &mut AppState, _| s.discord_mark_guild_read(&gid)
                })),
                MenuEntry::item(
                    MenuItem::new("dc-srv-invite", "Invite to server")
                        .icon(icon_user_plus(15.0, FG))
                        .on_select({
                            let gid = guild_id.clone();
                            let gname = name.clone();
                            let ich = invite_ch.clone();
                            move |s: &mut AppState, _| {
                                if let Some(ref c) = ich {
                                    s.open_discord_invite_dialog(&gname, &c.id, &c.name);
                                } else {
                                    s.discord_server_action("invite", json!({ "guildId": gid }));
                                }
                            }
                        }),
                ),
                MenuEntry::Separator,
            ];
            if muted {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-srv-unmute", "Unmute server")
                        .icon(icon_bell_off(15.0, FG))
                        .on_select({
                            let gid = guild_id.clone();
                            move |s: &mut AppState, _| {
                                s.discord_server_action(
                                    "mute",
                                    json!({ "guildId": gid, "muted": false }),
                                );
                            }
                        }),
                ));
            } else {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-srv-mute", "Mute server")
                        .icon(icon_bell_off(15.0, FG))
                        .submenu(server_mute_submenu(&guild_id)),
                ));
            }
            items.push(MenuEntry::item(
                MenuItem::new("dc-srv-notify", "Notification settings")
                    .submenu(server_notify_submenu(&guild_id)),
            ));
            items.push(MenuEntry::item(
                MenuItem::new("dc-srv-hide-muted", "Hide muted channels")
                    .checked(hide_muted)
                    .on_select({
                        let gid = guild_id.clone();
                        move |s: &mut AppState, _| s.toggle_hide_muted_guild(&gid)
                    }),
            ));
            if has_guide {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-srv-show-all", "Show all channels")
                        .checked(show_all)
                        .on_select({
                            let gid = guild_id.clone();
                            move |s: &mut AppState, _| s.discord_show_all_channels(&gid, !show_all)
                        }),
                ));
            }
            items.push(MenuEntry::Separator);
            if let Some(mod_flags) = chans.and_then(|c| c.moderation.as_ref()) {
                let feats = chans.map(|c| c.features.as_slice()).unwrap_or(&[]);
                let sec_entries: Vec<MenuEntry<AppState>> = ServerSection::ALL
                    .into_iter()
                    .filter(|sec| sec.shown(mod_flags, feats))
                    .map(|sec| {
                        let gid = guild_id.clone();
                        let gname = name.clone();
                        MenuEntry::item(
                            MenuItem::new(format!("dc-srv-sec-{}", sec.id()), sec.label())
                                .on_select(move |s: &mut AppState, _| {
                                    s.open_server_settings(&gid, &gname, sec);
                                }),
                        )
                    })
                    .collect();
                if !sec_entries.is_empty() {
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-srv-settings", "Server settings")
                            .icon(icon_settings(15.0, FG))
                            .submenu(sec_entries),
                    ));
                }
            }
            items.push(MenuEntry::item(
                MenuItem::new("dc-srv-privacy", "Privacy settings").on_select({
                    let gid = guild_id.clone();
                    move |s: &mut AppState, _| {
                        s.discord.admin.server_privacy_guild = Some(gid.clone());
                    }
                }),
            ));
            items.push(MenuEntry::item(
                MenuItem::new("dc-srv-profile", "Edit per-server profile").on_select({
                    let gid = guild_id.clone();
                    move |s: &mut AppState, _| {
                        s.open_discord_settings_on(
                            DiscordSettingsPage::Profiles,
                            Some(gid.clone()),
                        );
                    }
                }),
            ));
            if can_manage || can_events {
                items.push(MenuEntry::Separator);
                if can_manage {
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-srv-new-ch", "Create channel").on_select({
                            let gid = guild_id.clone();
                            move |s: &mut AppState, _| {
                                s.open_create_channel_dialog(&gid, None, false);
                            }
                        }),
                    ));
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-srv-new-cat", "Create category").on_select({
                            let gid = guild_id.clone();
                            move |s: &mut AppState, _| {
                                s.open_create_channel_dialog(&gid, None, true);
                            }
                        }),
                    ));
                }
                if can_events {
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-srv-new-ev", "Create event").on_select({
                            let gid = guild_id.clone();
                            move |s: &mut AppState, _| s.open_discord_events(&gid, true)
                        }),
                    ));
                }
            }
            let in_folder = s
                .discord
                .home
                .rail
                .iter()
                .any(|r| r.id.is_some() && r.guild_ids.iter().any(|g| g == &guild_id));
            let move_modes: Vec<(&'static str, &'static str)> = if in_folder {
                vec![
                    ("folderFirst", "First in folder"),
                    ("folderLast", "Last in folder"),
                    ("listFirst", "First in list"),
                    ("listLast", "Last in list"),
                    ("noFolder", "No folder"),
                ]
            } else {
                vec![("listFirst", "First in list"), ("listLast", "Last in list")]
            };
            let move_entries = move_modes
                .into_iter()
                .map(|(m, lbl)| {
                    let gid = guild_id.clone();
                    MenuEntry::item(
                        MenuItem::new(format!("dc-srv-mv-{m}"), lbl)
                            .on_select(move |s: &mut AppState, _| s.discord_move_server(&gid, m)),
                    )
                })
                .collect();
            items.push(MenuEntry::Separator);
            items.push(MenuEntry::item(
                MenuItem::new("dc-srv-move", "Move to").submenu(move_entries),
            ));
            if !owner {
                items.push(MenuEntry::Separator);
                items.push(MenuEntry::item(
                    MenuItem::new("dc-srv-leave", "Leave server")
                        .danger()
                        .on_select({
                            let gid = guild_id.clone();
                            move |s: &mut AppState, _| {
                                s.discord_server_action("leave", json!({ "guildId": gid }));
                            }
                        }),
                ));
                items.push(MenuEntry::item(
                    MenuItem::new("dc-srv-report", "Report server")
                        .icon(icon_flag(15.0, bad()))
                        .danger()
                        .on_select({
                            let gid = guild_id.clone();
                            let gname = name.clone();
                            move |s: &mut AppState, _| {
                                let mut ids = std::collections::HashMap::new();
                                ids.insert("guild_id".into(), gid.clone());
                                s.open_discord_report(ReportTarget {
                                    kind: "guild".into(),
                                    ids,
                                    preview_name: gname.clone(),
                                    preview_text: None,
                                    user_id: None,
                                    can_delete: false,
                                });
                            }
                        }),
                ));
            }
            if s.discord.developer_mode {
                items.push(MenuEntry::Separator);
                items.push(MenuEntry::item(
                    MenuItem::new("dc-srv-copy-id", "Copy server ID")
                        .icon(icon_copy(15.0, FG))
                        .on_select(move |s: &mut AppState, tx| {
                            tx.copy(guild_id.clone());
                            s.notice = Some("Copied server ID".into());
                        }),
                ));
            }
            items
        }
        ContextMenuTarget::DiscordMessage {
            channel_id,
            guild_id,
            message_id,
            author_id,
            author_name,
            content,
            mine,
            pinned,
        } => {
            let link = match guild_id {
                Some(ref gid) => {
                    format!("https://discord.com/channels/{gid}/{channel_id}/{message_id}")
                }
                None => format!("https://discord.com/channels/@me/{channel_id}/{message_id}"),
            };
            let mut items = vec![
                MenuEntry::item(
                    MenuItem::new("dc-msg-react", "Add reaction")
                        .icon(icon_smile(15.0, FG))
                        .on_select({
                            let mid = message_id.clone();
                            move |s: &mut AppState, _| {
                                s.discord.chat.emoji_picker_for = Some(mid.clone());
                            }
                        }),
                ),
                MenuEntry::item(
                    MenuItem::new("dc-msg-reply", "Reply")
                        .icon(icon_reply(15.0, FG))
                        .on_select({
                            let mid = message_id.clone();
                            let aname = author_name.clone();
                            move |s: &mut AppState, _| {
                                s.discord.chat.reply_to = Some((mid.clone(), aname.clone()));
                            }
                        }),
                ),
            ];
            if mine {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-msg-edit", "Edit message")
                        .icon(icon_edit(15.0, FG))
                        .on_select({
                            let mid = message_id.clone();
                            let body = content.clone();
                            move |s: &mut AppState, _| s.begin_edit_discord_message(&mid, &body)
                        }),
                ));
            }
            items.push(MenuEntry::item(
                MenuItem::new(
                    "dc-msg-pin",
                    if pinned {
                        "Unpin message"
                    } else {
                        "Pin message"
                    },
                )
                .icon(icon_pin(15.0, FG))
                .on_select({
                    let cid = channel_id.clone();
                    let mid = message_id.clone();
                    move |s: &mut AppState, _| s.toggle_discord_pin(&cid, &mid, pinned)
                }),
            ));
            items.push(MenuEntry::item(
                MenuItem::new("dc-msg-copy-text", "Copy text")
                    .icon(icon_copy(15.0, FG))
                    .disabled(content.trim().is_empty())
                    .on_select({
                        let copy_body = content.clone();
                        move |s: &mut AppState, tx| {
                            tx.copy(copy_body.clone());
                            s.notice = Some("Copied message text".into());
                        }
                    }),
            ));
            items.push(MenuEntry::item(
                MenuItem::new("dc-msg-copy-link", "Copy message link")
                    .icon(icon_link(15.0, FG))
                    .on_select(move |s: &mut AppState, tx| {
                        tx.copy(link.clone());
                        s.notice = Some("Copied message link".into());
                    }),
            ));
            items.push(MenuEntry::item(
                MenuItem::new("dc-msg-forward", "Forward")
                    .icon(icon_forward(15.0, FG))
                    .on_select({
                        let cid = channel_id.clone();
                        let mid = message_id.clone();
                        move |s: &mut AppState, _| s.discord_message_action(&cid, &mid, "forward")
                    }),
            ));
            items.push(MenuEntry::Separator);
            if mine {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-msg-delete", "Delete message")
                        .icon(icon_trash(15.0, bad()))
                        .danger()
                        .on_select({
                            let cid = channel_id.clone();
                            let mid = message_id.clone();
                            move |s: &mut AppState, _| s.delete_discord_message(&cid, &mid)
                        }),
                ));
            }
            if !mine {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-msg-report", "Report message")
                        .icon(icon_flag(15.0, bad()))
                        .danger()
                        .on_select({
                            let cid = channel_id.clone();
                            let mid = message_id.clone();
                            let aid = author_id.clone();
                            let aname = author_name.clone();
                            let body = content.clone();
                            move |s: &mut AppState, _| {
                                let mut ids = std::collections::HashMap::new();
                                ids.insert("channel_id".into(), cid.clone());
                                ids.insert("message_id".into(), mid.clone());
                                s.open_discord_report(ReportTarget {
                                    kind: "message".into(),
                                    ids,
                                    preview_name: aname.clone(),
                                    preview_text: Some(body.clone()),
                                    user_id: Some(aid.clone()),
                                    can_delete: false,
                                });
                            }
                        }),
                ));
            }
            items.push(MenuEntry::item(
                MenuItem::new("dc-msg-copy-id", "Copy message ID")
                    .icon(icon_copy(15.0, FG))
                    .on_select(move |s: &mut AppState, tx| {
                        tx.copy(message_id.clone());
                        s.notice = Some("Copied message ID".into());
                    }),
            ));
            items
        }
        ContextMenuTarget::DiscordMember {
            guild_id,
            user_id,
            name,
            username,
            in_voice,
        } => {
            let is_me = s.discord.home.me.as_ref().is_some_and(|m| m.id == user_id);
            let is_friend = s
                .discord
                .friends
                .iter()
                .any(|f| f.id == user_id && f.kind == "friend");
            let is_ignored = s.discord.ignored_users.contains(&user_id);
            let mix = s
                .discord
                .voice_ui
                .user_mix
                .get(&user_id)
                .cloned()
                .unwrap_or_default();
            let handle = username
                .as_deref()
                .filter(|u| !u.is_empty())
                .unwrap_or(&name)
                .to_string();
            let mod_flags = guild_id
                .as_deref()
                .and_then(|gid| s.discord.channels_by_guild.get(gid))
                .and_then(|c| c.moderation.as_ref());

            let mut items = vec![
                MenuEntry::item(
                    MenuItem::new("dc-mem-profile", "Profile")
                        .icon(icon_user(15.0, FG))
                        .on_select({
                            let u = user_id.clone();
                            move |s: &mut AppState, _| {
                                s.open_dm_with_friend(&u);
                                s.discord.show_members = true;
                            }
                        }),
                ),
                MenuEntry::item(MenuItem::new("dc-mem-mention", "Mention").on_select({
                    let u = user_id.clone();
                    move |s: &mut AppState, _| s.mention_user_in_composer(&u)
                })),
                MenuEntry::item(
                    MenuItem::new("dc-mem-message", "Message")
                        .icon(icon_chat(15.0, FG))
                        .on_select({
                            let u = user_id.clone();
                            move |s: &mut AppState, _| s.open_dm_with_friend(&u)
                        }),
                ),
                MenuEntry::item(
                    MenuItem::new("dc-mem-call", "Call")
                        .icon(icon_volume(15.0, FG))
                        .on_select({
                            let u = user_id.clone();
                            move |s: &mut AppState, _| s.open_dm_with_friend(&u)
                        }),
                ),
            ];
            if in_voice {
                items.push(MenuEntry::Separator);
                items.push(MenuEntry::item(
                    MenuItem::new("dc-mem-vol", "User volume")
                        .icon(icon_volume(15.0, FG))
                        .submenu(voice_volume_submenu(&user_id, mix.volume)),
                ));
                items.push(MenuEntry::item(
                    MenuItem::new("dc-mem-mute", "Mute")
                        .checked(mix.muted)
                        .on_select({
                            let u = user_id.clone();
                            let next = !mix.muted;
                            move |s: &mut AppState, _| {
                                s.set_discord_user_mix(&u, None, Some(next), None)
                            }
                        }),
                ));
                items.push(MenuEntry::item(
                    MenuItem::new("dc-mem-sb-mute", "Mute soundboard")
                        .checked(mix.soundboard_muted)
                        .on_select({
                            let u = user_id.clone();
                            let next = !mix.soundboard_muted;
                            move |s: &mut AppState, _| s.set_user_soundboard_mute(&u, next)
                        }),
                ));
            }
            items.push(MenuEntry::Separator);
            items.push(MenuEntry::item(
                MenuItem::new(
                    "dc-mem-friend",
                    if is_friend {
                        "Remove friend"
                    } else {
                        "Add friend"
                    },
                )
                .icon(icon_user_plus(15.0, FG))
                .on_select({
                    let u = user_id.clone();
                    move |s: &mut AppState, _| {
                        if is_friend {
                            s.remove_discord_friend(&u);
                        } else {
                            s.discord_friend_action("add", &u);
                        }
                    }
                }),
            ));
            items.push(MenuEntry::item(
                MenuItem::new(
                    "dc-mem-ignore",
                    if is_ignored { "Unignore" } else { "Ignore" },
                )
                .on_select({
                    let u = user_id.clone();
                    move |s: &mut AppState, _| s.toggle_ignore_user(&u)
                }),
            ));
            items.push(MenuEntry::item(
                MenuItem::new("dc-mem-block", "Block").danger().on_select({
                    let u = user_id.clone();
                    move |s: &mut AppState, _| s.discord_friend_action("block", &u)
                }),
            ));
            if !is_me {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-mem-report", "Report")
                        .icon(icon_flag(15.0, bad()))
                        .danger()
                        .on_select({
                            let u = user_id.clone();
                            let nm = name.clone();
                            let h = handle.clone();
                            move |s: &mut AppState, _| {
                                let mut ids = std::collections::HashMap::new();
                                ids.insert("reported_user_id".into(), u.clone());
                                s.open_discord_report(ReportTarget {
                                    kind: "user".into(),
                                    ids,
                                    preview_name: nm.clone(),
                                    preview_text: Some(format!("@{h}")),
                                    user_id: Some(u.clone()),
                                    can_delete: false,
                                });
                            }
                        }),
                ));
            }
            if let Some(ref gid) = guild_id
                && let Some(mf) = mod_flags
            {
                let has_mod = mf.manage_nicknames || mf.timeout || mf.kick || mf.ban || mf.owner;
                if has_mod {
                    items.push(MenuEntry::Separator);
                }
                if mf.manage_nicknames || is_me {
                    let g = gid.clone();
                    let u = user_id.clone();
                    let nm = name.clone();
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-mem-nick", "Change nickname").on_select(
                            move |s: &mut AppState, _| {
                                s.open_member_admin_dialog(MemberDialogKind::Nick, &g, &u, &nm);
                            },
                        ),
                    ));
                }
                if !is_me && mf.timeout {
                    let to_entries: Vec<MenuEntry<AppState>> = TIMEOUT_DURATIONS
                        .into_iter()
                        .map(|(secs, lbl)| {
                            let g = gid.clone();
                            let u = user_id.clone();
                            MenuEntry::item(
                                MenuItem::new(format!("dc-mem-to-{secs}"), lbl).on_select(
                                    move |s: &mut AppState, _| {
                                        s.run_discord_member_mod(
                                            "timeout",
                                            &g,
                                            &u,
                                            json!({ "seconds": secs }),
                                        );
                                    },
                                ),
                            )
                        })
                        .chain(std::iter::once(MenuEntry::item(
                            MenuItem::new("dc-mem-to-clear", "Remove timeout").on_select({
                                let g = gid.clone();
                                let u = user_id.clone();
                                move |s: &mut AppState, _| {
                                    s.run_discord_member_mod(
                                        "timeout",
                                        &g,
                                        &u,
                                        json!({ "seconds": 0 }),
                                    );
                                }
                            }),
                        )))
                        .collect();
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-mem-timeout", "Timeout")
                            .danger()
                            .submenu(to_entries),
                    ));
                }
                if !is_me && mf.kick {
                    let g = gid.clone();
                    let u = user_id.clone();
                    let nm = name.clone();
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-mem-kick", "Kick").danger().on_select(
                            move |s: &mut AppState, _| {
                                s.open_member_admin_dialog(MemberDialogKind::Kick, &g, &u, &nm);
                            },
                        ),
                    ));
                }
                if !is_me && mf.ban {
                    let g = gid.clone();
                    let u = user_id.clone();
                    let nm = name.clone();
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-mem-ban", "Ban").danger().on_select(
                            move |s: &mut AppState, _| {
                                s.open_member_admin_dialog(MemberDialogKind::Ban, &g, &u, &nm);
                            },
                        ),
                    ));
                }
                if !is_me && mf.owner {
                    let g = gid.clone();
                    let u = user_id.clone();
                    let nm = name.clone();
                    items.push(MenuEntry::item(
                        MenuItem::new("dc-mem-transfer", "Transfer ownership")
                            .danger()
                            .on_select(move |s: &mut AppState, _| {
                                s.open_member_admin_dialog(MemberDialogKind::Transfer, &g, &u, &nm);
                            }),
                    ));
                }
            }
            items.push(MenuEntry::Separator);
            items.push(MenuEntry::item(
                MenuItem::new("dc-mem-copy-user", "Copy username")
                    .icon(icon_copy(15.0, FG))
                    .on_select(move |s: &mut AppState, tx| {
                        tx.copy(handle.clone());
                        s.notice = Some("Copied username".into());
                    }),
            ));
            if s.discord.developer_mode {
                items.push(MenuEntry::item(
                    MenuItem::new("dc-mem-copy-id", "Copy user ID")
                        .icon(icon_copy(15.0, FG))
                        .on_select(move |s: &mut AppState, tx| {
                            tx.copy(user_id.clone());
                            s.notice = Some("Copied user ID".into());
                        }),
                ));
            }
            items
        }
        ContextMenuTarget::DiscordGoLiveMenu => {
            let active = matches!(
                s.discord.voice_ui.golive_state.as_str(),
                "live" | "connecting" | "starting"
            );
            let shown = s.discord.voice_ui.golive_preview_shown;
            let mut items = Vec::new();
            if active {
                items.push(MenuEntry::item(
                    MenuItem::new(
                        "dc-gl-preview-toggle",
                        if shown {
                            "Hide preview"
                        } else {
                            "Show preview"
                        },
                    )
                    .on_select(move |s: &mut AppState, _| {
                        s.discord.voice_ui.golive_preview_shown = !shown;
                    }),
                ));
            }
            items.push(MenuEntry::item(
                MenuItem::new(
                    "dc-gl-picker",
                    if active {
                        "Change what you share"
                    } else {
                        "Share screen"
                    },
                )
                .icon(icon_camera(15.0, FG))
                .on_select(|s: &mut AppState, _| s.open_voice_panel(VoicePanel::GoLive)),
            ));
            if active {
                items.push(MenuEntry::Separator);
                items.push(MenuEntry::item(
                    MenuItem::new("dc-gl-stop", "Stop streaming")
                        .danger()
                        .on_select(|s: &mut AppState, _| s.stop_discord_golive()),
                ));
            }
            items
        }
    };

    context_menu_layers_live(
        ContextMenuSpec {
            id: menu_id,
            anchor,
            width,
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
        },
        &["top-bar"],
    )
}

pub fn library_more_overlay(s: &AppState, blur: bool) -> Vec<El> {
    if !s.library_more_open {
        return Vec::new();
    }
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let mut entries = vec![
        MenuEntry::item(
            MenuItem::new("lib-menu-select", "Select games")
                .icon(icon_check(15.0, FG))
                .on_select(|s: &mut AppState, _| {
                    s.library_all_games = true;
                    s.push_notice("Select games in All Games", NoticeTone::Neutral);
                }),
        ),
        MenuEntry::item(
            MenuItem::new("lib-menu-downloads", "Downloads")
                .icon(icon_download(15.0, FG))
                .on_select(|s: &mut AppState, _| {
                    s.push_notice("No active downloads", NoticeTone::Neutral);
                }),
        ),
        MenuEntry::item(
            MenuItem::new("lib-menu-refresh", "Refresh library")
                .icon(icon_refresh(15.0, FG))
                .on_select(|s: &mut AppState, _| s.refresh_library()),
        ),
    ];
    if !s.library_all_games {
        entries.push(MenuEntry::item(
            MenuItem::new("lib-menu-hidden", "Hidden games")
                .checked(s.library_show_hidden)
                .on_select(|s: &mut AppState, _| {
                    s.library_show_hidden = !s.library_show_hidden;
                    s.library_all_games = true;
                }),
        ));
    }

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

pub fn library_add_overlay(s: &AppState, blur: bool) -> Vec<El> {
    if !s.library_add_open {
        return Vec::new();
    }
    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let entries = vec![
        MenuEntry::item(
            MenuItem::new("lib-add-folder", "New folder")
                .icon(icon_folder(15.0, FG))
                .on_select(|s: &mut AppState, _| {
                    s.library_all_games = true;
                    s.push_notice("Folder management coming soon", NoticeTone::Neutral);
                }),
        ),
        MenuEntry::item(
            MenuItem::new("lib-add-game", "Add a game")
                .icon(icon_gamepad(15.0, FG))
                .on_select(|s: &mut AppState, _| {
                    s.push_notice("Add a custom game from Settings", NoticeTone::Neutral);
                }),
        ),
        MenuEntry::item(
            MenuItem::new("lib-add-key", "Activate key")
                .icon(icon_key(15.0, FG))
                .on_select(|s: &mut AppState, tx| s.open_key_modal(tx)),
        ),
    ];

    let (anchor_key, side) = if s.library_all_games {
        ("all-games-add-btn", Side::Bottom)
    } else {
        ("home-add-btn", Side::Top)
    };

    context_menu_layers(ContextMenuSpec {
        id: "library-add".into(),
        anchor: MenuAnchor::Element {
            key: anchor_key.into(),
            side,
            align: Align::End,
            gap: 8.0,
        },
        width: 220.0,
        blur,
        submenu_blur: false,
        open_submenu: None,
        return_focus: Some(anchor_key.into()),
        gamepad_focus,
        entries,
        on_dismiss: Rc::new(|s: &mut AppState, _: &mut Tx| s.library_add_open = false),
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
            let mut options: Vec<SelectOption> = LibrarySort::ALL
                .into_iter()
                .map(|sort| SelectOption::new(sort.id(), sort.label()))
                .collect();
            options.push(SelectOption::new("all", "All games"));
            options.push(SelectOption::new("hidden", "Hidden games"));
            select_menu_layers(SelectMenuSpec {
                id: "dd-library-sort".into(),
                trigger_key: "sort-cycle".into(),
                selected: current,
                options,
                width: 200.0,
                align: Align::End,
                blur,
                gamepad_focus,
                on_select: Rc::new(|s: &mut AppState, val: &str, _: &mut Tx| match val {
                    "all" => s.library_show_hidden = false,
                    "hidden" => s.library_show_hidden = true,
                    other => s.sort = LibrarySort::from_id(other),
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
