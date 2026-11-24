use crate::events::{EventRegistry, HostEventKind};
use crate::state::{AppState, MAX_NOTICES, NoticeItem, NoticeTone, Room};
use exo_kit::{
    FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PILL, SPRING_BASE, SPRING_MOVE, SPRING_QUICK,
    bad, danger_button_dyn, fg_muted, glass_button_dyn, glass_panel_opt, good, hover_fill,
    icon_alert, icon_bell, icon_check, icon_close, pressed_fill, primary_button_dyn, selected_fill,
    track_fill, type_body, type_section, type_small,
};
use exo_rpc::HostEvent;
use rune::{Cursor, El, Phase, Spring, SpringPair, SurfaceFit, Transition, col, el, row, text};
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet, VecDeque};

pub const TOAST_WIDTH: f32 = 340.0;
pub const SWIPE_DISMISS_DX: f32 = 106.0;
pub const SWIPE_DISMISS_VX: f32 = 420.0;
const SWIPE_MIN_DX: f32 = -24.0;
pub const QUEUE_MAX: usize = 8;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum NoticeActionKind {
    RepairAntiCheat {
        notice_key: String,
        title: String,
        dependency_id: String,
    },
    PlayStoreOffline {
        notice_key: String,
        title: String,
        store: String,
    },
    OpenPcPage {
        notice_key: String,
        page: String,
    },
    InstallAppUpdate,
    DownloadNewerUpscalers {
        notice_id: u64,
    },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NoticeActionSpec {
    pub label: String,
    pub primary: bool,
    pub danger: bool,
    pub busy: bool,
    pub confirm: Option<String>,
    pub kind: NoticeActionKind,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum NoticeOpenKind {
    OpenDiscord {
        guild_id: Option<String>,
        channel_id: String,
    },
}

#[derive(Debug, Clone, Default)]
pub struct RichNoticeSpec {
    pub key_id: Option<String>,
    pub title: Option<String>,
    pub where_label: Option<String>,
    pub text: String,
    pub tone: NoticeTone,
    pub sticky: Option<bool>,
    pub lasting: bool,
    pub count: u32,
    pub progress: Option<Option<f32>>,
    pub action: Option<NoticeActionSpec>,
    pub on_open: Option<NoticeOpenKind>,
    pub avatar_url: Option<String>,
    pub avatar_initial: Option<String>,
}

#[derive(Debug, Clone, Default)]
pub struct NoticeRuntimeState {
    pub queue: VecDeque<NoticeItem>,
    pub said_outages: HashSet<String>,
    pub shown_discord_notices: HashMap<String, String>,
}

#[derive(Debug, Clone, Default)]
pub struct StoreOutageWire {
    pub id: String,
    pub store: String,
    pub state: String,
    pub title: String,
    pub text: String,
    pub can_play_offline: bool,
}

impl StoreOutageWire {
    pub fn from_json(v: &Value) -> Self {
        Self {
            id: v
                .get("id")
                .and_then(|s| s.as_str())
                .unwrap_or("")
                .to_string(),
            store: v
                .get("store")
                .and_then(|s| s.as_str())
                .unwrap_or("")
                .to_string(),
            state: v
                .get("state")
                .and_then(|s| s.as_str())
                .unwrap_or("")
                .to_string(),
            title: v
                .get("title")
                .and_then(|s| s.as_str())
                .unwrap_or("")
                .to_string(),
            text: v
                .get("text")
                .and_then(|s| s.as_str())
                .unwrap_or("")
                .to_string(),
            can_play_offline: v
                .get("canPlayOffline")
                .and_then(|b| b.as_bool())
                .unwrap_or(false),
        }
    }
}

pub fn stay_for_secs(
    title: Option<&str>,
    where_label: Option<&str>,
    body: &str,
    has_action: bool,
) -> f64 {
    let chars = title.map_or(0, str::len) + where_label.map_or(0, str::len) + body.len();
    let min_ms = if has_action { 7000.0 } else { 4000.0 };
    let ms = (2000.0 + chars as f64 * 50.0).clamp(min_ms, 10_000.0);
    ms / 1000.0
}

pub fn register_events(reg: &mut EventRegistry) {
    reg.on_raw("anticheat.missing", |state, data| {
        let game_id = data.get("gameId").and_then(|v| v.as_str()).unwrap_or("");
        let title = data.get("title").and_then(|v| v.as_str()).unwrap_or("");
        let name = data.get("name").and_then(|v| v.as_str()).unwrap_or("");
        let removed = data.get("removed").and_then(|v| v.as_str()).unwrap_or("");
        let dep_id = data
            .get("dependencyId")
            .and_then(|v| v.as_str())
            .unwrap_or("");
        if game_id.is_empty() || name.is_empty() || dep_id.is_empty() {
            return false;
        }
        let id = format!("anticheat:{game_id}");
        state.push_rich_notice(RichNoticeSpec {
            key_id: Some(id.clone()),
            title: Some(format!("{name} went with {removed}")),
            text: format!("{title} needs it to start."),
            sticky: Some(true),
            action: Some(NoticeActionSpec {
                label: "Repair".into(),
                primary: true,
                danger: false,
                busy: false,
                confirm: None,
                kind: NoticeActionKind::RepairAntiCheat {
                    notice_key: id,
                    title: name.to_string(),
                    dependency_id: dep_id.to_string(),
                },
            }),
            ..RichNoticeSpec::default()
        });
        true
    });

    reg.on_raw("stores.outage", |state, data| {
        let outage = StoreOutageWire::from_json(data);
        state.apply_store_outage(outage)
    });

    reg.on_raw("discord.notify", |state, data| {
        let channel_id = data.get("channelId").and_then(|v| v.as_str()).unwrap_or("");
        let author = data.get("author").and_then(|v| v.as_str()).unwrap_or("");
        let body = data.get("body").and_then(|v| v.as_str()).unwrap_or("");
        if channel_id.is_empty() || author.is_empty() {
            return false;
        }
        let message_id = data
            .get("messageId")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();
        let guild_id = data
            .get("guildId")
            .and_then(|v| v.as_str())
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string());
        let place = data
            .get("place")
            .and_then(|v| v.as_str())
            .filter(|s| !s.is_empty());
        let server = data
            .get("server")
            .and_then(|v| v.as_str())
            .filter(|s| !s.is_empty());
        let where_label = match (place, server) {
            (Some(p), Some(s)) => Some(format!("{p} · {s}")),
            (Some(p), None) => Some(p.to_string()),
            (None, Some(s)) => Some(s.to_string()),
            (None, None) => None,
        };
        let avatar_url = data
            .get("avatar")
            .and_then(|v| v.as_str())
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string());
        let initial = author
            .chars()
            .next()
            .map(|c| c.to_uppercase().to_string())
            .unwrap_or_else(|| "?".into());

        let notice_key = format!("dc:{channel_id}");
        let prev_count = state
            .notices
            .iter()
            .find(|n| n.key_id.as_deref() == Some(notice_key.as_str()))
            .map(|n| n.count)
            .unwrap_or(0);
        state
            .notice_runtime
            .shown_discord_notices
            .insert(channel_id.to_string(), message_id);

        state.push_rich_notice(RichNoticeSpec {
            key_id: Some(notice_key),
            title: Some(author.to_string()),
            where_label,
            text: body.to_string(),
            tone: NoticeTone::Neutral,
            sticky: Some(false),
            count: prev_count.saturating_add(1),
            on_open: Some(NoticeOpenKind::OpenDiscord {
                guild_id,
                channel_id: channel_id.to_string(),
            }),
            avatar_url,
            avatar_initial: Some(initial),
            ..RichNoticeSpec::default()
        });
        true
    });

    reg.on_raw("discord.notifyClear", |state, data| {
        let channel_id = data.get("channelId").and_then(|v| v.as_str()).unwrap_or("");
        if channel_id.is_empty() {
            return false;
        }
        let message_id = data.get("messageId").and_then(|v| v.as_str());
        if let Some(mid) = message_id
            && state
                .notice_runtime
                .shown_discord_notices
                .get(channel_id)
                .is_some_and(|cur| cur != mid)
        {
            return false;
        }
        state
            .notice_runtime
            .shown_discord_notices
            .remove(channel_id);
        state.dismiss_notice_by_key(&format!("dc:{channel_id}"))
    });

    reg.on_raw("discord.dmLine", |state, data| {
        let channel_id = data.get("channelId").and_then(|v| v.as_str()).unwrap_or("");
        if channel_id.is_empty() {
            return false;
        }
        let line = data
            .get("line")
            .and_then(|v| v.as_str())
            .map(|s| s.to_string());
        let at = data.get("at").and_then(|v| v.as_i64());
        if let Some(dm) = state
            .discord
            .home
            .dms
            .iter_mut()
            .find(|d| d.id == channel_id)
        {
            dm.line = line;
            if let Some(ts) = at {
                dm.last_at = Some(ts);
            }
            return state.room == Room::Discord;
        }
        false
    });

    reg.on(HostEventKind::ComponentsFailed, |state, ev| {
        let HostEvent::ComponentsFailed(c) = ev else {
            return false;
        };
        let feature = c.feature.trim();
        if feature.is_empty() {
            return false;
        }
        state.push_notice(format!("{feature} couldn't be set up"), NoticeTone::Bad);
        true
    });

    reg.on(HostEventKind::BackupStopped, |state, ev| {
        let HostEvent::BackupStopped(b) = ev else {
            return false;
        };
        let sentence = b.sentence.trim();
        let since = b.since.trim();
        if sentence.is_empty() || since.is_empty() {
            return false;
        }
        state.push_rich_notice(RichNoticeSpec {
            key_id: Some(format!("backup-stopped-{since}")),
            title: Some("Backup stopped".into()),
            text: sentence.to_string(),
            tone: NoticeTone::Bad,
            lasting: true,
            ..RichNoticeSpec::default()
        });
        true
    });

    reg.on_raw("components.failed", |state, data| {
        let feature = data
            .get("feature")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .trim();
        if feature.is_empty() {
            return false;
        }
        state.push_notice(format!("{feature} couldn't be set up"), NoticeTone::Bad);
        true
    });

    reg.on_raw("backup.stopped", |state, data| {
        let sentence = data
            .get("sentence")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .trim();
        let since = data
            .get("since")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .trim();
        if sentence.is_empty() || since.is_empty() {
            return false;
        }
        state.push_rich_notice(RichNoticeSpec {
            key_id: Some(format!("backup-stopped-{since}")),
            title: Some("Backup stopped".into()),
            text: sentence.to_string(),
            tone: NoticeTone::Bad,
            lasting: true,
            ..RichNoticeSpec::default()
        });
        true
    });

    reg.on_raw("app.notify", |state, data| {
        let title = data
            .get("title")
            .and_then(|v| v.as_str())
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string());
        let body = data
            .get("body")
            .or_else(|| data.get("text"))
            .and_then(|v| v.as_str())
            .unwrap_or("");
        if body.is_empty() && title.is_none() {
            return false;
        }
        let tone = match data.get("tone").and_then(|v| v.as_str()) {
            Some("good") => NoticeTone::Good,
            Some("bad") => NoticeTone::Bad,
            _ => NoticeTone::Neutral,
        };
        let key_id = data
            .get("id")
            .and_then(|v| v.as_str())
            .map(|s| s.to_string());
        state.push_rich_notice(RichNoticeSpec {
            key_id,
            title,
            text: body.to_string(),
            tone,
            sticky: data.get("sticky").and_then(|v| v.as_bool()),
            ..RichNoticeSpec::default()
        });
        true
    });

    reg.on_raw("app.notifyClear", |state, data| {
        let Some(id) = data.get("id").and_then(|v| v.as_str()) else {
            return false;
        };
        state.dismiss_notice_by_key(id)
    });
}

impl AppState {
    pub fn load_store_outages(&mut self) {
        #[cfg(windows)]
        self.call_host_async(
            "stores.outages",
            json!({}),
            |s, res: Result<Value, String>| {
                if let Ok(val) = res
                    && let Some(arr) = val.get("outages").and_then(|v| v.as_array())
                {
                    for item in arr {
                        s.apply_store_outage(StoreOutageWire::from_json(item));
                    }
                }
            },
        );
    }

    pub fn apply_store_outage(&mut self, outage: StoreOutageWire) -> bool {
        if outage.store.is_empty() {
            return false;
        }
        let dedup_key = format!("{}:{}", outage.id, outage.state);
        if !self.notice_runtime.said_outages.insert(dedup_key) {
            return false;
        }
        let id = format!("outage:{}", outage.store);
        if outage.state == "down" && outage.can_play_offline && outage.store == "steam" {
            self.push_rich_notice(RichNoticeSpec {
                key_id: Some(id.clone()),
                title: Some(outage.title.clone()),
                text: outage.text,
                sticky: Some(true),
                action: Some(NoticeActionSpec {
                    label: "Play offline".into(),
                    primary: true,
                    danger: false,
                    busy: false,
                    confirm: Some("Restart Steam offline?".into()),
                    kind: NoticeActionKind::PlayStoreOffline {
                        notice_key: id,
                        title: outage.title,
                        store: outage.store,
                    },
                }),
                ..RichNoticeSpec::default()
            });
            return true;
        }
        self.push_rich_notice(RichNoticeSpec {
            key_id: Some(id),
            title: Some(outage.title),
            text: outage.text,
            tone: if outage.state == "back" {
                NoticeTone::Good
            } else {
                NoticeTone::Neutral
            },
            sticky: Some(outage.state == "down"),
            ..RichNoticeSpec::default()
        });
        true
    }

    pub fn push_rich_notice(&mut self, spec: RichNoticeSpec) -> u64 {
        let sticky = spec.sticky.unwrap_or_else(|| {
            spec.tone == NoticeTone::Bad || spec.action.is_some() || spec.progress.is_some()
        });
        let remaining = stay_for_secs(
            spec.title.as_deref(),
            spec.where_label.as_deref(),
            &spec.text,
            spec.action.is_some(),
        );

        if let Some(ref key_id) = spec.key_id {
            if let Some(existing) = self
                .notices
                .iter_mut()
                .find(|n| n.key_id.as_deref() == Some(key_id.as_str()))
            {
                existing.title = spec.title;
                existing.where_label = spec.where_label;
                existing.text = spec.text;
                existing.tone = spec.tone;
                existing.sticky = sticky;
                existing.lasting = spec.lasting;
                existing.count = spec.count;
                existing.progress = spec.progress;
                existing.action = spec.action;
                existing.on_open = spec.on_open;
                existing.avatar_url = spec.avatar_url;
                existing.avatar_initial = spec.avatar_initial;
                existing.remaining_secs = remaining;
                existing.last_tick = None;
                return existing.id;
            }
            if let Some(waiting) = self
                .notice_runtime
                .queue
                .iter_mut()
                .find(|n| n.key_id.as_deref() == Some(key_id.as_str()))
            {
                waiting.title = spec.title;
                waiting.where_label = spec.where_label;
                waiting.text = spec.text;
                waiting.tone = spec.tone;
                waiting.sticky = sticky;
                waiting.lasting = spec.lasting;
                waiting.count = spec.count;
                waiting.progress = spec.progress;
                waiting.action = spec.action;
                waiting.on_open = spec.on_open;
                waiting.avatar_url = spec.avatar_url;
                waiting.avatar_initial = spec.avatar_initial;
                waiting.remaining_secs = remaining;
                return waiting.id;
            }
        }

        let id = self.alloc_notice_id();
        let item = NoticeItem {
            id,
            key_id: spec.key_id,
            title: spec.title,
            where_label: spec.where_label,
            text: spec.text.clone(),
            tone: spec.tone,
            sticky,
            lasting: spec.lasting,
            count: spec.count,
            progress: spec.progress,
            action: spec.action,
            on_open: spec.on_open,
            avatar_url: spec.avatar_url,
            avatar_initial: spec.avatar_initial,
            remaining_secs: remaining,
            last_tick: None,
            hovered: false,
            dragging: false,
            drag_x: 0.0,
            drag_prev_x: 0.0,
            drag_prev_t: self.clock_secs(),
            drag_vx: 0.0,
        };

        if self.notices.len() >= MAX_NOTICES
            && let Some(pos) = self
                .notices
                .iter()
                .position(|n| !n.sticky && n.action.is_none())
        {
            self.notices.remove(pos);
        }
        if self.notices.len() >= MAX_NOTICES {
            self.notice_runtime.queue.push_back(item);
            if self.notice_runtime.queue.len() > QUEUE_MAX {
                if let Some(pos) = self.notice_runtime.queue.iter().position(|n| !n.sticky) {
                    self.notice_runtime.queue.remove(pos);
                } else {
                    self.notice_runtime.queue.pop_front();
                }
            }
        } else {
            self.notices.push_back(item);
        }
        id
    }

    pub fn promote_waiting_notices(&mut self) {
        while self.notices.len() < MAX_NOTICES {
            let Some(next) = self.notice_runtime.queue.pop_front() else {
                break;
            };
            self.notices.push_back(next);
        }
    }

    pub fn dismiss_notice_by_key(&mut self, key_id: &str) -> bool {
        let mut removed = false;
        let before_q = self.notice_runtime.queue.len();
        self.notice_runtime
            .queue
            .retain(|n| n.key_id.as_deref() != Some(key_id));
        if self.notice_runtime.queue.len() != before_q {
            removed = true;
        }
        if let Some(pos) = self
            .notices
            .iter()
            .position(|n| n.key_id.as_deref() == Some(key_id))
        {
            self.notices.remove(pos);
            self.promote_waiting_notices();
            removed = true;
        }
        removed
    }

    pub fn execute_notice_open(&mut self, id: u64, kind: NoticeOpenKind) {
        self.dismiss_notice(id);
        match kind {
            NoticeOpenKind::OpenDiscord {
                guild_id,
                channel_id,
            } => {
                self.switch_room(Room::Discord);
                if let Some(gid) = guild_id {
                    self.open_discord_guild(&gid);
                    self.open_discord_channel(&channel_id);
                } else {
                    self.open_discord_dm(&channel_id);
                }
            }
        }
    }

    pub fn execute_notice_action(&mut self, kind: NoticeActionKind) {
        match kind {
            NoticeActionKind::RepairAntiCheat {
                notice_key,
                title,
                dependency_id,
            } => {
                self.push_rich_notice(RichNoticeSpec {
                    key_id: Some(notice_key.clone()),
                    title: Some(title.clone()),
                    text: "Installing…".into(),
                    progress: Some(None),
                    sticky: Some(true),
                    lasting: true,
                    ..RichNoticeSpec::default()
                });
                #[cfg(windows)]
                self.call_host_async(
                    "runtimes.install",
                    json!({ "mode": "missing", "ids": [dependency_id] }),
                    move |s, res: Result<Value, String>| {
                        let ok = res
                            .as_ref()
                            .ok()
                            .and_then(|v| v.get("ok").and_then(|b| b.as_bool()))
                            .unwrap_or(false);
                        if ok {
                            s.push_rich_notice(RichNoticeSpec {
                                key_id: Some(notice_key),
                                title: Some(title),
                                text: "Installed".into(),
                                tone: NoticeTone::Good,
                                sticky: Some(false),
                                ..RichNoticeSpec::default()
                            });
                        } else {
                            let failed_msg = res
                                .ok()
                                .and_then(|v| {
                                    v.get("results")
                                        .and_then(|r| r.as_array())
                                        .and_then(|arr| {
                                            arr.iter().find_map(|item| {
                                                (item.get("outcome").and_then(|o| o.as_str())
                                                    == Some("failed"))
                                                .then(|| {
                                                    item.get("message")
                                                        .and_then(|m| m.as_str())
                                                        .map(|s| s.to_string())
                                                })
                                                .flatten()
                                            })
                                        })
                                        .or_else(|| {
                                            v.get("message")
                                                .and_then(|m| m.as_str())
                                                .map(|s| s.to_string())
                                        })
                                })
                                .unwrap_or_else(|| "It couldn’t be installed. Try again.".into());
                            s.push_rich_notice(RichNoticeSpec {
                                key_id: Some(notice_key),
                                title: Some(title),
                                text: failed_msg,
                                tone: NoticeTone::Bad,
                                sticky: Some(true),
                                ..RichNoticeSpec::default()
                            });
                        }
                    },
                );
                #[cfg(not(windows))]
                let _ = dependency_id;
            }
            NoticeActionKind::PlayStoreOffline {
                notice_key,
                title,
                store,
            } => {
                self.push_rich_notice(RichNoticeSpec {
                    key_id: Some(notice_key.clone()),
                    title: Some(title.clone()),
                    text: "Restarting in offline mode…".into(),
                    progress: Some(None),
                    sticky: Some(true),
                    lasting: true,
                    ..RichNoticeSpec::default()
                });
                #[cfg(windows)]
                self.call_host_async(
                    "stores.playOffline",
                    json!({ "store": store }),
                    move |s, res: Result<Value, String>| {
                        let ok = res
                            .as_ref()
                            .ok()
                            .and_then(|v| v.get("ok").and_then(|b| b.as_bool()))
                            .unwrap_or(false);
                        if ok {
                            s.push_rich_notice(RichNoticeSpec {
                                key_id: Some(notice_key),
                                title: Some(title),
                                text: "Offline mode is on".into(),
                                tone: NoticeTone::Good,
                                sticky: Some(false),
                                ..RichNoticeSpec::default()
                            });
                        } else {
                            let msg = res
                                .ok()
                                .and_then(|v| {
                                    v.get("message")
                                        .and_then(|m| m.as_str())
                                        .map(|s| s.to_string())
                                })
                                .unwrap_or_else(|| {
                                    "Could not switch to offline mode. Try again.".into()
                                });
                            s.push_rich_notice(RichNoticeSpec {
                                key_id: Some(notice_key),
                                title: Some(title),
                                text: msg,
                                tone: NoticeTone::Bad,
                                sticky: Some(true),
                                ..RichNoticeSpec::default()
                            });
                        }
                    },
                );
                #[cfg(not(windows))]
                let _ = store;
            }
            NoticeActionKind::OpenPcPage { notice_key, page } => {
                self.dismiss_notice_by_key(&notice_key);
                if page == "apps" {
                    self.switch_room(Room::Apps);
                } else {
                    self.switch_room(Room::ExoAi);
                    if page == "storage" {
                        self.pc.exo_ai_storage_open = true;
                        self.load_pc_drives();
                    }
                }
            }
            NoticeActionKind::InstallAppUpdate => {
                self.start_app_update_install();
            }
            NoticeActionKind::DownloadNewerUpscalers { notice_id } => {
                self.run_notice_action(crate::state::NoticeAction::DownloadNewerUpscalers);
                self.dismiss_notice(notice_id);
            }
        }
    }
}

fn toast_transition() -> Transition {
    let enter = Transition::offset(0.0, 8.0);
    let exit = Transition::offset(0.0, 4.0);
    Transition::asymmetric(enter, exit).springs(SpringPair::new(SPRING_MOVE, SPRING_BASE))
}

fn toast_card(s: &AppState, item: &NoticeItem, scale: f32, allow_blur: bool) -> El {
    let id = item.id;
    let drag_x = item.drag_x;
    let drag_opacity = (1.0 - drag_x.max(0.0) / 220.0).clamp(0.15, 1.0);

    let leading = if item.avatar_url.is_some() || item.avatar_initial.is_some() {
        if let Some(ref url) = item.avatar_url
            && let Some(img) = s.discord_remote_image_sized(url, 36.0, scale)
        {
            el().size(36.0, 36.0)
                .radius(RADIUS_PILL)
                .clip_rounded(RADIUS_PILL)
                .pixels_fit(img.pixels(), SurfaceFit::Fill)
        } else {
            let init = item.avatar_initial.clone().unwrap_or_else(|| "?".into());
            el().size(36.0, 36.0)
                .radius(RADIUS_PILL)
                .fill(selected_fill())
                .center()
                .child(text(init).font(type_section()).color(FG))
        }
    } else {
        let icon = match item.tone {
            NoticeTone::Good => icon_check(17.0, good()),
            NoticeTone::Bad => icon_alert(17.0, bad()),
            NoticeTone::Neutral => icon_bell(17.0, fg_muted()),
        };
        el().size(20.0, 20.0).center().child(icon)
    };

    let mut text_col = col().grow(1.0).min_w(0.0).gap(2.0);
    if let Some(ref title) = item.title {
        let mut head = row().w_full().gap(6.0).center_y().child(
            text(title.clone())
                .font(type_section())
                .color(FG)
                .truncate(),
        );
        if item.count > 1 {
            head = head.child(
                text(format!("{} new", item.count))
                    .font(type_small())
                    .color(fg_muted()),
            );
        }
        if let Some(ref wh) = item.where_label {
            head = head.child(
                text(wh.clone())
                    .font(type_small())
                    .color(fg_muted())
                    .grow(1.0)
                    .min_w(0.0)
                    .truncate(),
            );
        }
        text_col = text_col.child(head);
    }

    let body_color = if item.tone == NoticeTone::Bad {
        bad()
    } else if item.title.is_some() {
        fg_muted()
    } else {
        FG
    };
    text_col = text_col.child(
        text(item.text.clone())
            .font(type_body())
            .color(body_color)
            .w_full()
            .wrap(),
    );

    let mut main_area = row()
        .grow(1.0)
        .min_w(0.0)
        .gap(10.0)
        .center_y()
        .child(leading)
        .child(text_col);

    if let Some(ref open_kind) = item.on_open {
        let okind = open_kind.clone();
        main_area = main_area
            .key(format!("notice-open-{id}"))
            .role("button")
            .label(item.title.clone().unwrap_or_else(|| item.text.clone()))
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .radius(RADIUS_CARD)
            .hover(hover_fill())
            .press(pressed_fill())
            .on_press(move |s: &mut AppState| s.execute_notice_open(id, okind.clone()));
    }

    let mut end_col = row().gap(6.0).center_y();
    if let Some(ref action) = item.action {
        let confirm_key = format!("notice-act-{id}");
        let armed = action.confirm.is_some() && s.is_confirm_armed(&confirm_key);
        let label = if armed {
            action.confirm.as_deref().unwrap_or(action.label.as_str())
        } else {
            action.label.as_str()
        };
        let act_kind = action.kind.clone();
        let has_confirm = action.confirm.is_some();
        let mut act_btn = if action.danger {
            danger_button_dyn(confirm_key.clone(), label, None)
        } else if action.primary || armed {
            primary_button_dyn(confirm_key.clone(), label, None)
        } else {
            glass_button_dyn(confirm_key.clone(), label, None, false)
        }
        .h(28.0)
        .px(10.0)
        .disabled(action.busy)
        .opacity(if action.busy { 0.5 } else { 1.0 });

        if !action.busy {
            act_btn = act_btn.on_press_tx(move |s: &mut AppState, tx| {
                if has_confirm && !s.is_confirm_armed(&confirm_key) {
                    let deadline = tx.clock() + 3.0;
                    s.armed_confirm = Some((confirm_key.clone(), deadline));
                    tx.wake_at(deadline);
                } else {
                    s.disarm_confirm();
                    s.execute_notice_action(act_kind.clone());
                }
            });
        }
        end_col = end_col.child(act_btn);
    }

    if !item.lasting {
        let close_btn = el()
            .key(format!("notice-close-{id}"))
            .role("button")
            .label("Dismiss")
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .size(24.0, 24.0)
            .radius(RADIUS_PILL)
            .center()
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press_tx(move |s: &mut AppState, tx| {
                tx.animated();
                s.dismiss_notice(id);
            })
            .child(icon_close(13.0, fg_muted()));
        end_col = end_col.child(close_btn);
    }

    let content_row = row()
        .w_full()
        .gap(8.0)
        .center_y()
        .px(12.0)
        .py(10.0)
        .child(main_area)
        .child(end_col);

    let mut card_col = col().w_full().child(content_row);
    if let Some(prog) = item.progress {
        let frac = prog.unwrap_or(0.35).clamp(0.0, 1.0);
        let fill_color = if item.tone == NoticeTone::Good {
            good()
        } else if item.tone == NoticeTone::Bad {
            bad()
        } else {
            PRIMARY_FG
        };
        let bar_fg = if fill_color == PRIMARY_FG {
            FG
        } else {
            fill_color
        };
        card_col = card_col.child(
            el().w_full()
                .h(3.0)
                .fill(track_fill())
                .child(el().w(TOAST_WIDTH * frac).h(3.0).fill(bar_fg)),
        );
    }

    let mut card = glass_panel_opt(format!("notice-{id}"), TOAST_WIDTH, allow_blur)
        .role(if item.tone == NoticeTone::Bad {
            "alert"
        } else {
            "status"
        })
        .label(item.text.clone())
        .cursor(if item.dragging {
            Cursor::Grabbing
        } else {
            Cursor::Grab
        })
        .radius(RADIUS_CARD)
        .clip_rounded(RADIUS_CARD)
        .offset(drag_x, 0.0)
        .offset_motion(if item.dragging {
            Spring::NONE
        } else {
            SPRING_MOVE
        })
        .opacity(drag_opacity)
        .opacity_motion(if item.dragging {
            Spring::NONE
        } else {
            SPRING_BASE
        })
        .transition(toast_transition())
        .on_hover_change(move |s: &mut AppState, hovering, tx| {
            s.set_notice_hovered(id, hovering, tx);
        })
        .child(card_col);

    if !item.lasting {
        card = card
            .drag_threshold(6.0)
            .on_drag(move |s: &mut AppState, d, tx| match d.phase {
                Phase::Down => {
                    s.begin_notice_drag(id, tx.clock());
                }
                Phase::Move => {
                    let x = d.dx.max(SWIPE_MIN_DX);
                    s.update_notice_drag(id, x, tx.clock());
                }
                Phase::Up => {
                    let cancelled = d.cancelled();
                    tx.animated();
                    s.end_notice_drag(id, cancelled, tx);
                }
            });
    }
    card
}

pub fn view(s: &AppState, scale: f32, allow_blur: bool) -> El {
    let last_idx = s.notices.len().saturating_sub(1);
    let bottom_inset = if s.room == Room::Music { 88.0 } else { 20.0 };
    col()
        .key("notice-stack")
        .pin(None, None, Some(20.0), Some(bottom_inset))
        .w(TOAST_WIDTH)
        .gap(8.0)
        .children(
            s.notices
                .iter()
                .enumerate()
                .map(|(idx, item)| toast_card(s, item, scale, allow_blur && idx == last_idx)),
        )
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::HostEvent;

    #[test]
    fn anticheat_missing_event_creates_repair_notice() {
        let mut s = AppState::default();
        assert!(s.apply_host_event(HostEvent::Raw {
            event: "anticheat.missing".into(),
            data: json!({
                "gameId": "steam:730",
                "title": "Rust",
                "name": "Easy Anti-Cheat",
                "removed": "Apex Legends",
                "dependencyId": "eac-eos"
            }),
        }));
        let n = s.notices.back().expect("anticheat notice");
        assert_eq!(
            n.title.as_deref(),
            Some("Easy Anti-Cheat went with Apex Legends")
        );
        assert_eq!(n.text, "Rust needs it to start.");
        assert!(n.sticky);
        assert_eq!(n.action.as_ref().map(|a| a.label.as_str()), Some("Repair"));
    }

    #[test]
    fn store_outage_deduplicates_and_offers_steam_offline_mode() {
        let mut s = AppState::default();
        let ev = HostEvent::Raw {
            event: "stores.outage".into(),
            data: json!({
                "id": "out-1",
                "store": "steam",
                "state": "down",
                "title": "Steam is down",
                "text": "Steam servers are unreachable.",
                "canPlayOffline": true
            }),
        };
        assert!(s.apply_host_event(ev.clone()));
        assert!(!s.apply_host_event(ev));
        let n = s.notices.back().expect("outage notice");
        let act = n.action.as_ref().expect("offline action");
        assert_eq!(act.label, "Play offline");
        assert_eq!(act.confirm.as_deref(), Some("Restart Steam offline?"));
    }

    #[test]
    fn discord_notify_stacks_per_channel_and_clears_matching_message() {
        let mut s = AppState::default();
        assert!(s.apply_host_event(HostEvent::Raw {
            event: "discord.notify".into(),
            data: json!({
                "channelId": "c1",
                "guildId": "g1",
                "messageId": "m1",
                "author": "Alex",
                "place": "#general",
                "server": "Squad",
                "body": "Ready?"
            }),
        }));
        assert!(s.apply_host_event(HostEvent::Raw {
            event: "discord.notify".into(),
            data: json!({
                "channelId": "c1",
                "guildId": "g1",
                "messageId": "m2",
                "author": "Alex",
                "place": "#general",
                "server": "Squad",
                "body": "Hop on"
            }),
        }));
        assert_eq!(s.notices.len(), 1);
        assert_eq!(s.notices[0].count, 2);
        assert_eq!(
            s.notices[0].where_label.as_deref(),
            Some("#general · Squad")
        );

        assert!(!s.apply_host_event(HostEvent::Raw {
            event: "discord.notifyClear".into(),
            data: json!({ "channelId": "c1", "messageId": "m1" }),
        }));
        assert_eq!(s.notices.len(), 1);

        assert!(s.apply_host_event(HostEvent::Raw {
            event: "discord.notifyClear".into(),
            data: json!({ "channelId": "c1", "messageId": "m2" }),
        }));
        assert!(s.notices.is_empty());
    }

    #[test]
    fn components_failed_and_backup_stopped_push_notices() {
        let mut s = AppState::default();
        assert!(s.apply_host_event(HostEvent::Raw {
            event: "components.failed".into(),
            data: json!({ "feature": "Game Capture" }),
        }));
        assert_eq!(s.notices.len(), 1);
        assert_eq!(s.notices[0].text, "Game Capture couldn't be set up");
        assert_eq!(s.notices[0].tone, NoticeTone::Bad);

        assert!(s.apply_host_event(HostEvent::Raw {
            event: "backup.stopped".into(),
            data: json!({ "sentence": "Backup paused: drive full.", "since": "2026-11-23" }),
        }));
        assert_eq!(s.notices.len(), 2);
        assert_eq!(s.notices[1].title.as_deref(), Some("Backup stopped"));
        assert!(s.notices[1].lasting);
    }
}
