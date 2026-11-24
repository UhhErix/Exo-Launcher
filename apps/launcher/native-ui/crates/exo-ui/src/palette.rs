use crate::shell::room_icon;
use crate::state::{AppState, NoticeTone, PaletteItem, PaletteTarget, Room};
use exo_kit::{
    FG, RADIUS_SHEET, RADIUS_SMALL, SPRING_BASE, SPRING_QUICK, TRANSPARENT, fg_muted,
    glass_panel_opt, hairline, hover_fill, icon_chat, icon_close, icon_download, icon_gamepad,
    icon_key, icon_minimize, icon_music, icon_notes, icon_pause, icon_play, icon_plus,
    icon_refresh, icon_search, icon_server, icon_settings, icon_skip_back, icon_skip_forward,
    icon_sparkles, modal_card_transition, modal_scrim_transition, scaled, scrim_fill, type_body,
    type_control, type_small,
};
use exo_rpc::{DiscordChannels, DiscordHome};
use rune::{
    Cursor, El, Key, KeyEvent, ScrollSpec, Spring, TextStyle, Transition, Tx, col, edit_access, el,
    row, text, text_input,
};
use serde_json::{Value, json};

#[derive(Debug, Clone, Default)]
pub struct PaletteNoteItem {
    pub id: String,
    pub title: String,
    pub snippet: String,
}

#[derive(Debug, Clone, Default)]
pub struct PaletteChannelItem {
    pub id: String,
    pub guild_id: String,
    pub name: String,
    pub server: String,
}

#[derive(Debug, Clone, Default)]
pub struct PaletteExtrasState {
    pub notes: Vec<PaletteNoteItem>,
    pub dms: Vec<(String, String)>,
    pub servers: Vec<(String, String)>,
    pub channels: Vec<PaletteChannelItem>,
}

impl AppState {
    pub fn load_palette_extras(&mut self) {
        self.sync_palette_discord_from_cache();
        #[cfg(windows)]
        {
            self.call_host_async("notes.list", json!({}), |s, res: Result<Value, String>| {
                if let Ok(val) = res
                    && val.get("ok").and_then(|v| v.as_bool()).unwrap_or(false)
                    && let Some(arr) = val.get("notes").and_then(|v| v.as_array())
                {
                    s.palette_extras.notes = arr
                        .iter()
                        .map(|n| PaletteNoteItem {
                            id: n
                                .get("id")
                                .and_then(|v| v.as_str())
                                .unwrap_or("")
                                .to_string(),
                            title: n
                                .get("title")
                                .and_then(|v| v.as_str())
                                .unwrap_or("")
                                .to_string(),
                            snippet: n
                                .get("snippet")
                                .and_then(|v| v.as_str())
                                .unwrap_or("")
                                .to_string(),
                        })
                        .filter(|n| !n.id.is_empty())
                        .collect();
                }
            });
            self.call_host_async(
                "discord.home",
                json!({}),
                |s, res: Result<DiscordHome, String>| {
                    if let Ok(home) = res {
                        s.palette_extras.dms = home
                            .dms
                            .iter()
                            .map(|d| (d.id.clone(), d.name.clone()))
                            .collect();
                        s.palette_extras.servers = home
                            .guilds
                            .iter()
                            .map(|g| (g.id.clone(), g.name.clone()))
                            .collect();
                        s.palette_extras.channels.clear();
                        for g in home.guilds {
                            let gid = g.id.clone();
                            let gname = g.name.clone();
                            s.call_host_async(
                                "discord.channels",
                                json!({ "guildId": gid }),
                                move |s2, ch_res: Result<DiscordChannels, String>| {
                                    if let Ok(list) = ch_res {
                                        s2.palette_extras.channels.retain(|c| c.guild_id != gid);
                                        for c in list.channels {
                                            if c.kind == "category" {
                                                continue;
                                            }
                                            s2.palette_extras.channels.push(PaletteChannelItem {
                                                id: c.id,
                                                guild_id: gid.clone(),
                                                name: c.name,
                                                server: gname.clone(),
                                            });
                                        }
                                    }
                                },
                            );
                        }
                    }
                },
            );
        }
    }

    pub fn sync_palette_discord_from_cache(&mut self) {
        if self.palette_extras.dms.is_empty() && !self.discord.home.dms.is_empty() {
            self.palette_extras.dms = self
                .discord
                .home
                .dms
                .iter()
                .map(|d| (d.id.clone(), d.name.clone()))
                .collect();
        }
        if self.palette_extras.servers.is_empty() && !self.discord.home.guilds.is_empty() {
            self.palette_extras.servers = self
                .discord
                .home
                .guilds
                .iter()
                .map(|g| (g.id.clone(), g.name.clone()))
                .collect();
        }
        if self.palette_extras.channels.is_empty() && !self.discord.channels_by_guild.is_empty() {
            for (gid, list) in &self.discord.channels_by_guild {
                for c in &list.channels {
                    if c.kind == "category" {
                        continue;
                    }
                    self.palette_extras.channels.push(PaletteChannelItem {
                        id: c.id.clone(),
                        guild_id: gid.clone(),
                        name: c.name.clone(),
                        server: list.name.clone().unwrap_or_default(),
                    });
                }
            }
        }
    }

    pub fn append_palette_extras(&self, query: &str, items: &mut Vec<PaletteItem>) {
        if let Some(sel) = self.selected_game() {
            let play_title = format!("Play {}", sel.title);
            if query.is_empty()
                || play_title.to_lowercase().contains(query)
                || "play".contains(query)
            {
                items.push(PaletteItem {
                    id: "pal-action-play-selected".into(),
                    group: "Actions",
                    title: play_title,
                    detail: None,
                    target: PaletteTarget::PlaySelected(sel.id.clone()),
                });
            }
            let install_title = format!("Install {}", sel.title);
            if query.is_empty()
                || install_title.to_lowercase().contains(query)
                || "install".contains(query)
            {
                items.push(PaletteItem {
                    id: "pal-action-install-selected".into(),
                    group: "Actions",
                    title: install_title,
                    detail: None,
                    target: PaletteTarget::InstallSelected(sel.id.clone()),
                });
            }
        }

        let extra_actions: [(&str, &str, &[&str], PaletteTarget); 2] = [
            (
                "pal-action-downloads",
                "Downloads",
                &["queue", "install", "updates", "pause"],
                PaletteTarget::OpenDownloads,
            ),
            (
                "pal-action-whats-new",
                "What's new",
                &["changelog", "release notes", "updates"],
                PaletteTarget::OpenWhatsNew,
            ),
        ];
        for (id, title, keywords, target) in extra_actions {
            if query.is_empty()
                || title.to_lowercase().contains(query)
                || keywords.iter().any(|k| k.contains(query))
            {
                items.push(PaletteItem {
                    id: id.into(),
                    group: "Actions",
                    title: title.into(),
                    detail: None,
                    target,
                });
            }
        }

        let note_actions: [(&str, &str, &[&str], PaletteTarget); 2] = [
            (
                "pal-note-new",
                "New note",
                &["notes", "write", "scratchpad"],
                PaletteTarget::NoteNew,
            ),
            (
                "pal-note-open",
                "Notes",
                &["note", "text", "markdown"],
                PaletteTarget::NoteOpen,
            ),
        ];
        for (id, title, keywords, target) in note_actions {
            if query.is_empty()
                || title.to_lowercase().contains(query)
                || keywords.iter().any(|k| k.contains(query))
            {
                items.push(PaletteItem {
                    id: id.into(),
                    group: "Notes",
                    title: title.into(),
                    detail: None,
                    target,
                });
            }
        }
        if !query.is_empty() {
            for note in &self.palette_extras.notes {
                if note.title.to_lowercase().contains(query)
                    || note.snippet.to_lowercase().contains(query)
                {
                    items.push(PaletteItem {
                        id: format!("pal-note-{}", note.id),
                        group: "Notes",
                        title: note.title.clone(),
                        detail: (!note.snippet.trim().is_empty()).then(|| note.snippet.clone()),
                        target: PaletteTarget::NoteItem(note.id.clone()),
                    });
                }
            }
        }

        for (id, name) in &self.palette_extras.dms {
            if query.is_empty()
                || name.to_lowercase().contains(query)
                || "dm".contains(query)
                || "discord".contains(query)
            {
                items.push(PaletteItem {
                    id: format!("pal-dm-{id}"),
                    group: "Discord",
                    title: name.clone(),
                    detail: None,
                    target: PaletteTarget::DiscordDm(id.clone()),
                });
            }
        }
        for (id, name) in &self.palette_extras.servers {
            if query.is_empty()
                || name.to_lowercase().contains(query)
                || "server".contains(query)
                || "discord".contains(query)
            {
                items.push(PaletteItem {
                    id: format!("pal-server-{id}"),
                    group: "Discord",
                    title: name.clone(),
                    detail: None,
                    target: PaletteTarget::DiscordServer(id.clone()),
                });
            }
        }
        if !query.is_empty() {
            for ch in &self.palette_extras.channels {
                if ch.name.to_lowercase().contains(query)
                    || ch.server.to_lowercase().contains(query)
                {
                    items.push(PaletteItem {
                        id: format!("pal-ch-{}-{}", ch.guild_id, ch.id),
                        group: "Discord",
                        title: ch.name.clone(),
                        detail: Some(ch.server.clone()),
                        target: PaletteTarget::DiscordChannel {
                            guild_id: ch.guild_id.clone(),
                            channel_id: ch.id.clone(),
                        },
                    });
                }
            }
        }

        let music_cmds: [(&str, &str, &[&str], PaletteTarget); 4] = [
            (
                "pal-music-toggle",
                "Play or pause",
                &["music", "song", "audio"],
                PaletteTarget::MusicToggle,
            ),
            (
                "pal-music-next",
                "Next song",
                &["music", "skip", "track"],
                PaletteTarget::MusicNext,
            ),
            (
                "pal-music-prev",
                "Previous song",
                &["music", "back", "track"],
                PaletteTarget::MusicPrevious,
            ),
            (
                "pal-music-open",
                "Open Music",
                &["player", "spotify", "songs"],
                PaletteTarget::MusicOpen,
            ),
        ];
        for (id, title, keywords, target) in music_cmds {
            if query.is_empty()
                || title.to_lowercase().contains(query)
                || keywords.iter().any(|k| k.contains(query))
            {
                items.push(PaletteItem {
                    id: id.into(),
                    group: "Music",
                    title: title.into(),
                    detail: None,
                    target,
                });
            }
        }
    }

    pub fn execute_palette_extra(&mut self, target: PaletteTarget) {
        match target {
            PaletteTarget::PlaySelected(id) => self.launch_game(&id),
            PaletteTarget::InstallSelected(id) => self.install_game(&id),
            PaletteTarget::OpenDownloads => self.open_downloads(),
            PaletteTarget::OpenWhatsNew => self.open_whats_new_all(),
            PaletteTarget::NoteNew => {
                self.open_captures(None);
                #[cfg(windows)]
                self.call_host_async(
                    "notes.act",
                    json!({ "action": "new" }),
                    |s, res: Result<serde_json::Value, String>| {
                        if res.is_err() {
                            s.push_notice("Could not create note.", NoticeTone::Bad);
                        }
                    },
                );
            }
            PaletteTarget::NoteOpen => self.open_captures(None),
            PaletteTarget::NoteItem(id) => {
                self.open_captures(None);
                #[cfg(windows)]
                self.call_host_async(
                    "notes.read",
                    json!({ "id": id }),
                    |s, res: Result<serde_json::Value, String>| {
                        if res.is_err() {
                            s.push_notice("Could not open note.", NoticeTone::Bad);
                        }
                    },
                );
                #[cfg(not(windows))]
                let _ = id;
            }
            PaletteTarget::DiscordDm(dm_id) => {
                self.switch_room(Room::Discord);
                self.open_discord_dm(&dm_id);
            }
            PaletteTarget::DiscordServer(guild_id) => {
                self.switch_room(Room::Discord);
                self.open_discord_guild(&guild_id);
            }
            PaletteTarget::DiscordChannel {
                guild_id,
                channel_id,
            } => {
                self.switch_room(Room::Discord);
                self.open_discord_guild(&guild_id);
                self.open_discord_channel(&channel_id);
            }
            PaletteTarget::MusicToggle => self.music_control("toggle"),
            PaletteTarget::MusicNext => self.music_control("next"),
            PaletteTarget::MusicPrevious => self.music_control("previous"),
            PaletteTarget::MusicOpen => self.switch_room(Room::Music),
            _ => {}
        }
    }
}

fn palette_icon(target: &PaletteTarget) -> El {
    match target {
        PaletteTarget::Room(room) => room_icon(*room, false),
        PaletteTarget::SettingsSection(_) => icon_settings(16.0, fg_muted()),
        PaletteTarget::PcSection(_) => icon_sparkles(16.0, fg_muted()),
        PaletteTarget::OpenGame(_) | PaletteTarget::AddGame => icon_gamepad(16.0, fg_muted()),
        PaletteTarget::RefreshLibrary | PaletteTarget::CheckUpdates => {
            icon_refresh(16.0, fg_muted())
        }
        PaletteTarget::ActivateKey => icon_key(16.0, fg_muted()),
        PaletteTarget::StartTour | PaletteTarget::ResumeSetup | PaletteTarget::OpenWhatsNew => {
            icon_sparkles(16.0, fg_muted())
        }
        PaletteTarget::MinimizeWindow => icon_minimize(16.0, fg_muted()),
        PaletteTarget::CloseWindow => icon_close(16.0, fg_muted()),
        PaletteTarget::PlaySelected(_) => icon_play(16.0, fg_muted()),
        PaletteTarget::InstallSelected(_) | PaletteTarget::OpenDownloads => {
            icon_download(16.0, fg_muted())
        }
        PaletteTarget::NoteNew => icon_plus(16.0, fg_muted()),
        PaletteTarget::NoteOpen | PaletteTarget::NoteItem(_) => icon_notes(16.0, fg_muted()),
        PaletteTarget::DiscordDm(_) | PaletteTarget::DiscordChannel { .. } => {
            icon_chat(16.0, fg_muted())
        }
        PaletteTarget::DiscordServer(_) => icon_server(16.0, fg_muted()),
        PaletteTarget::MusicToggle => icon_pause(16.0, fg_muted()),
        PaletteTarget::MusicNext => icon_skip_forward(16.0, fg_muted()),
        PaletteTarget::MusicPrevious => icon_skip_back(16.0, fg_muted()),
        PaletteTarget::MusicOpen => icon_music(16.0, fg_muted()),
    }
}

fn palette_row(item: PaletteItem, index: usize, selected: bool) -> El {
    let target = item.target.clone();
    let icon = palette_icon(&target);
    let label = match item.detail {
        Some(ref detail) => format!("{}{}", item.title, detail),
        None => item.title.clone(),
    };
    let mut r = row()
        .key(format!("command-palette-{}", item.id))
        .role("option")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .w_full()
        .h(scaled(44.0))
        .px(scaled(10.0))
        .gap(scaled(12.0))
        .center_y()
        .radius(RADIUS_SMALL + 2.0)
        .fill(if selected { hover_fill() } else { TRANSPARENT })
        .hover(hover_fill())
        .hover_motion(Spring::NONE, SPRING_QUICK)
        .on_press_tx(move |s: &mut AppState, tx| {
            s.palette_index = index;
            s.execute_palette_target(target.clone(), tx);
        })
        .child(icon)
        .child(
            text(item.title)
                .font(type_control())
                .color(FG)
                .grow(1.0)
                .min_w(0.0)
                .truncate(),
        );
    if let Some(detail) = item.detail {
        r = r.child(
            text(detail)
                .font(type_small())
                .color(fg_muted())
                .max_w(scaled(180.0))
                .truncate(),
        );
    }
    r
}

pub fn handle_palette_key(s: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    match ev.key {
        Key::Escape => {
            tx.instant();
            s.close_palette();
            true
        }
        Key::Down => {
            let len = s.palette_results().len();
            if len > 0 {
                s.palette_index = (s.palette_index + 1) % len;
            }
            true
        }
        Key::Up => {
            let len = s.palette_results().len();
            if len > 0 {
                s.palette_index = (s.palette_index + len - 1) % len;
            }
            true
        }
        Key::Enter => {
            tx.instant();
            let items = s.palette_results();
            if let Some(item) = items.get(s.palette_index) {
                s.execute_palette_target(item.target.clone(), tx);
            } else {
                s.close_palette();
            }
            true
        }
        _ => false,
    }
}

pub fn layers(s: &AppState, blur: bool) -> Vec<El> {
    let items = s.palette_results();
    let clamped = if items.is_empty() {
        0
    } else {
        s.palette_index.min(items.len() - 1)
    };

    let input = text_input(&s.palette_query, type_body())
        .key("palette-input")
        .label("Search")
        .grow(1.0)
        .single_line()
        .color(FG)
        .placeholder("Search", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.palette_query))
        .on_input_change(|s: &mut AppState, _| s.palette_index = 0)
        .on_input_key(handle_palette_key)
        .on_submit(|s: &mut AppState, tx| {
            tx.instant();
            let list = s.palette_results();
            if let Some(item) = list.get(s.palette_index) {
                s.execute_palette_target(item.target.clone(), tx);
            }
        });

    let header_h = scaled(48.0);
    let header = row()
        .w_full()
        .h(header_h)
        .px(scaled(16.0))
        .gap(scaled(12.0))
        .center_y()
        .cursor(Cursor::Text)
        .child(icon_search(16.0, fg_muted()))
        .child(input);

    let max_palette_h = 820.8;
    let body = if items.is_empty() {
        el().w_full().h(72.0).center().child(
            text("No matching games or commands")
                .font(type_small())
                .color(fg_muted()),
        )
    } else {
        let mut rows: Vec<El> = Vec::new();
        let mut last_group: Option<&'static str> = None;
        for (i, item) in items.into_iter().enumerate() {
            if last_group != Some(item.group) {
                rows.push(
                    el().key(format!("pal-grp-{}", item.group))
                        .w_full()
                        .px(scaled(10.0))
                        .pt(if last_group.is_some() {
                            scaled(12.0)
                        } else {
                            scaled(8.0)
                        })
                        .pb(scaled(4.0))
                        .child(
                            text(item.group)
                                .font(TextStyle::new(scaled(12.0), 500.0))
                                .color(fg_muted()),
                        ),
                );
                last_group = Some(item.group);
            }
            rows.push(palette_row(item, i, i == clamped));
        }
        col()
            .key("command-palette-list")
            .role("listbox")
            .label("Commands")
            .w_full()
            .max_h(max_palette_h - header_h - 1.0)
            .px(scaled(6.0))
            .py(scaled(6.0))
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .clip_fade_linear(0.0, 0.0, 0.0, 28.0)
            .child(
                col()
                    .key("command-palette-rows")
                    .w_full()
                    .growth(rune::GrowthSpec::default())
                    .virtualize(scaled(40.0))
                    .reveal_selected(true)
                    .children(rows),
            )
    };

    let card = glass_panel_opt("command-palette", scaled(576.0), blur)
        .max_h(max_palette_h)
        .radius(RADIUS_SHEET)
        .clip_rounded(RADIUS_SHEET)
        .motion(SPRING_BASE)
        .transition(modal_card_transition())
        .child(header)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(body);

    let scrim = el()
        .key("palette-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(scrim_fill())
        .hit()
        .cursor(Cursor::Default)
        .transition(modal_scrim_transition())
        .on_press_tx(|s: &mut AppState, tx| {
            tx.instant();
            s.close_palette();
        });

    let host = col()
        .key("palette-host")
        .pin(Some(0.0), Some(68.0), Some(0.0), None)
        .items_center()
        .transition(Transition::none())
        .child(card);

    vec![scrim, host]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn palette_includes_selected_game_notes_discord_and_music_extras() {
        let mut s = AppState::default();
        s.palette_extras.notes.push(PaletteNoteItem {
            id: "n1".into(),
            title: "Build notes".into(),
            snippet: "FPS benchmark".into(),
        });
        s.palette_extras.dms.push(("dm1".into(), "Alex".into()));
        s.palette_extras.servers.push(("g1".into(), "Squad".into()));
        s.palette_extras.channels.push(PaletteChannelItem {
            id: "c1".into(),
            guild_id: "g1".into(),
            name: "clips".into(),
            server: "Squad".into(),
        });

        let all = s.palette_results();
        assert!(all.iter().any(|i| i.title == "Take the tour"));
        assert!(all.iter().any(|i| i.title == "Minimize window"));
        assert!(all.iter().any(|i| i.title == "Close window"));
        assert!(all.iter().any(|i| i.title.starts_with("Play ")));
        assert!(all.iter().any(|i| i.title.starts_with("Install ")));
        assert!(all.iter().any(|i| i.title == "New note"));
        assert!(all.iter().any(|i| i.title == "Notes"));
        assert!(
            all.iter()
                .any(|i| i.title == "Alex" && i.group == "Discord")
        );
        assert!(
            all.iter()
                .any(|i| i.title == "Squad" && i.group == "Discord")
        );
        assert!(
            all.iter()
                .any(|i| i.title == "Play or pause" && i.group == "Music")
        );
        assert!(
            all.iter()
                .any(|i| i.title == "Next song" && i.group == "Music")
        );
        assert!(
            all.iter()
                .any(|i| i.title == "Previous song" && i.group == "Music")
        );

        s.palette_query.text = "clips".into();
        let filtered = s.palette_results();
        assert!(
            filtered
                .iter()
                .any(|i| i.title == "clips" && i.detail.as_deref() == Some("Squad"))
        );

        s.palette_query.text = "benchmark".into();
        let note_filtered = s.palette_results();
        assert!(note_filtered.iter().any(|i| i.title == "Build notes"));
    }
}
