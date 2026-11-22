use crate::discord_state::DiscordPlace;
use crate::state::{AppState, HostStatus};
use exo_rpc::{
    ActionResult, DiscordEmbed, DiscordEmbedAuthor, DiscordEmbedField, DiscordEmbedFooter,
    DiscordMessage, DiscordMessageEvent, DiscordMessagesReply, DiscordPinsReply, DiscordReaction,
    DiscordReactionEmoji, DiscordReactionEvent, DiscordSearchReply, DiscordSendReply,
    DiscordTypingEvent, DiscordUploadReply, DiscordUser, METHOD_DISCORD_ACK, METHOD_DISCORD_DELETE,
    METHOD_DISCORD_EDIT, METHOD_DISCORD_MESSAGES, METHOD_DISCORD_PINS, METHOD_DISCORD_REACT,
    METHOD_DISCORD_SEARCH, METHOD_DISCORD_SEND, METHOD_DISCORD_TYPING, METHOD_DISCORD_UPLOAD,
    base64,
};
use rune::TextEdit;
use serde_json::json;
use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::time::{Duration, Instant};

#[derive(Debug, Clone)]
pub struct PendingAttachment {
    pub local_id: String,
    pub name: String,
    pub size: u64,
    pub upload_id: Option<String>,
    pub uploading: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChatDrawer {
    None,
    Pins,
    Search,
}

pub struct DiscordChatState {
    pub messages_by_channel: HashMap<String, Vec<DiscordMessage>>,
    pub loading_channels: HashSet<String>,
    pub has_more_before: HashMap<String, bool>,
    pub composer: TextEdit,
    pub reply_to: Option<(String, String)>,
    pub editing: Option<(String, TextEdit)>,
    pub pending_uploads: Vec<PendingAttachment>,
    pub drop_hover: bool,
    pub emoji_picker_for: Option<String>,
    pub composer_emoji_open: bool,
    pub drawer: ChatDrawer,
    pub pins: Vec<DiscordMessage>,
    pub pins_loading: bool,
    pub search_input: TextEdit,
    pub search_results: Vec<DiscordMessage>,
    pub search_total: usize,
    pub search_loading: bool,
    pub typing_by_channel: HashMap<String, (String, Instant)>,
    pub last_typing_sent: Option<Instant>,
    pub next_local_seq: u64,
}

impl DiscordChatState {
    pub fn empty() -> Self {
        Self {
            messages_by_channel: HashMap::new(),
            loading_channels: HashSet::new(),
            has_more_before: HashMap::new(),
            composer: TextEdit::default(),
            reply_to: None,
            editing: None,
            pending_uploads: Vec::new(),
            drop_hover: false,
            emoji_picker_for: None,
            composer_emoji_open: false,
            drawer: ChatDrawer::None,
            pins: Vec::new(),
            pins_loading: false,
            search_input: TextEdit::default(),
            search_results: Vec::new(),
            search_total: 0,
            search_loading: false,
            typing_by_channel: HashMap::new(),
            last_typing_sent: None,
            next_local_seq: 1,
        }
    }

    pub fn demo() -> Self {
        let mut c = Self::empty();
        let erix = DiscordUser {
            id: "100001".into(),
            username: "erix".into(),
            global_name: Some("Erix".into()),
            avatar: None,
            bot: false,
        };
        let ada = DiscordUser {
            id: "200001".into(),
            username: "ada".into(),
            global_name: Some("Ada".into()),
            avatar: None,
            bot: false,
        };
        let kai = DiscordUser {
            id: "200002".into(),
            username: "kai".into(),
            global_name: Some("Kai".into()),
            avatar: None,
            bot: false,
        };

        let first_general = DiscordMessage {
            id: "1309000000000000001".into(),
            channel_id: "ch-general".into(),
            author: erix.clone(),
            content: "Just landed **Phase 5b** — virtualized `El::virtual_rows` chat timeline + native GPU Discord Markdown with zero extra crates.".into(),
            timestamp: "2026-11-22T18:10:00.000Z".into(),
            reactions: vec![
                DiscordReaction {
                    count: 4,
                    me: true,
                    emoji: DiscordReactionEmoji {
                        id: None,
                        name: Some("🔥".into()),
                    },
                },
                DiscordReaction {
                    count: 2,
                    me: false,
                    emoji: DiscordReactionEmoji {
                        id: None,
                        name: Some("🚀".into()),
                    },
                },
            ],
            pinned: true,
            ..Default::default()
        };

        c.messages_by_channel.insert(
            "ch-general".into(),
            vec![
                first_general.clone(),
                DiscordMessage {
                    id: "1309000000000000002".into(),
                    channel_id: "ch-general".into(),
                    author: erix.clone(),
                    content: "```rust\ncol().growth(GrowthSpec::default())\n    .virtual_rows(msgs.len(), 68.0, key_fn, build_fn)\n```".into(),
                    timestamp: "2026-11-22T18:11:10.000Z".into(),
                    ..Default::default()
                },
                DiscordMessage {
                    id: "1309000000000000003".into(),
                    channel_id: "ch-general".into(),
                    author: ada.clone(),
                    content: "> Zero WebView2 overhead on scrolling channels\n<@100001> that scroll smoothness at 120Hz is unreal! Also testing ~~old parser~~ ||secret benchmark: 0.4ms frame CPU||".into(),
                    timestamp: "2026-11-22T18:12:45.000Z".into(),
                    mentions: vec![erix.clone()],
                    referenced_message: Some(Box::new(first_general.clone())),
                    reactions: vec![DiscordReaction {
                        count: 3,
                        me: true,
                        emoji: DiscordReactionEmoji {
                            id: None,
                            name: Some("👀".into()),
                        },
                    }],
                    ..Default::default()
                },
                DiscordMessage {
                    id: "1309000000000000004".into(),
                    channel_id: "ch-general".into(),
                    author: kai.clone(),
                    content: "Build telemetry from tonight's DX12 pass:".into(),
                    timestamp: "2026-11-22T18:14:20.000Z".into(),
                    embeds: vec![DiscordEmbed {
                        title: Some("Exo Native UI · DX12 Run".into()),
                        description: Some("All 6 rooms live in Rust + wgpu with contract-verified host RPC.".into()),
                        color: Some(0x3dd68c),
                        author: Some(DiscordEmbedAuthor {
                            name: Some("Exo CI".into()),
                            url: None,
                        }),
                        fields: vec![
                            DiscordEmbedField {
                                name: "RPC Contract".into(),
                                value: "102 verified methods".into(),
                                inline: true,
                            },
                            DiscordEmbedField {
                                name: "Backdrop Blurs".into(),
                                value: "Strictly <= 4".into(),
                                inline: true,
                            },
                        ],
                        footer: Some(DiscordEmbedFooter {
                            text: Some("Rune DX12 · zero-bloat pipeline".into()),
                        }),
                        ..Default::default()
                    }],
                    ..Default::default()
                },
            ],
        );

        c.messages_by_channel.insert(
            "dm-ada".into(),
            vec![
                DiscordMessage {
                    id: "1309000000000001001".into(),
                    channel_id: "dm-ada".into(),
                    author: ada.clone(),
                    content: "Queueing Premier in 5? Mirage is live.".into(),
                    timestamp: "2026-11-22T18:02:00.000Z".into(),
                    ..Default::default()
                },
                DiscordMessage {
                    id: "1309000000000001002".into(),
                    channel_id: "dm-ada".into(),
                    author: erix.clone(),
                    content: "Joining `Main Lounge` right after this **DX12** test finishes!"
                        .into(),
                    timestamp: "2026-11-22T18:03:15.000Z".into(),
                    reactions: vec![DiscordReaction {
                        count: 1,
                        me: false,
                        emoji: DiscordReactionEmoji {
                            id: None,
                            name: Some("👍".into()),
                        },
                    }],
                    ..Default::default()
                },
            ],
        );

        c.pins = vec![first_general];
        c
    }

    pub fn typing_label(&self, channel_id: &str) -> Option<String> {
        let (name, at) = self.typing_by_channel.get(channel_id)?;
        if at.elapsed() < Duration::from_secs(8) {
            Some(format!("{name} is typing…"))
        } else {
            None
        }
    }
}

impl AppState {
    pub fn active_chat_channel_id(&self) -> Option<String> {
        match self.discord.place {
            DiscordPlace::Messages => self.discord.active_dm_id.clone(),
            DiscordPlace::Servers => self.discord.active_channel_id.clone(),
            DiscordPlace::Home => None,
        }
    }

    pub fn load_discord_messages(&mut self, channel_id: &str, before: Option<&str>) {
        if self.status != HostStatus::Connected {
            return;
        }
        if self.discord.chat.loading_channels.contains(channel_id) {
            return;
        }
        self.discord
            .chat
            .loading_channels
            .insert(channel_id.to_string());
        #[cfg(windows)]
        {
            let cid = channel_id.to_string();
            let is_older = before.is_some();
            let mut params = json!({ "channelId": cid });
            if let Some(b) = before {
                params["before"] = json!(b);
            }
            self.call_host_async(
                METHOD_DISCORD_MESSAGES,
                params,
                move |s, res: Result<DiscordMessagesReply, String>| {
                    s.discord.chat.loading_channels.remove(&cid);
                    if let Ok(rep) = res
                        && rep.ok
                        && let Some(mut page) = rep.messages
                    {
                        let page_len = page.len();
                        page.reverse();
                        s.discord
                            .chat
                            .has_more_before
                            .insert(cid.clone(), page_len >= 50);
                        let newest_id = page.last().map(|m| m.id.clone());
                        let entry = s
                            .discord
                            .chat
                            .messages_by_channel
                            .entry(cid.clone())
                            .or_default();
                        if is_older {
                            let existing_ids: HashSet<String> =
                                entry.iter().map(|m| m.id.clone()).collect();
                            let mut merged: Vec<DiscordMessage> = page
                                .into_iter()
                                .filter(|m| !existing_ids.contains(&m.id))
                                .collect();
                            merged.append(entry);
                            *entry = merged;
                        } else {
                            *entry = page;
                        }
                        if !is_older && let Some(mid) = newest_id {
                            s.ack_discord_channel(&cid, &mid);
                        }
                    }
                },
            );
        }
    }

    pub fn ack_discord_channel(&mut self, channel_id: &str, message_id: &str) {
        if let Some(dm) = self
            .discord
            .home
            .dms
            .iter_mut()
            .find(|d| d.id == channel_id)
        {
            dm.unread = false;
            dm.mentions = 0;
        }
        for chans in self.discord.channels_by_guild.values_mut() {
            if let Some(c) = chans.channels.iter_mut().find(|c| c.id == channel_id) {
                c.unread = false;
                c.mentions = 0;
            }
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ACK,
            json!({ "channelId": channel_id, "messageId": message_id }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn notify_discord_typing(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        let Some(cid) = self.active_chat_channel_id() else {
            return;
        };
        if let Some(last) = self.discord.chat.last_typing_sent
            && last.elapsed() < Duration::from_secs(6)
        {
            return;
        }
        self.discord.chat.last_typing_sent = Some(Instant::now());
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_TYPING,
            json!({ "channelId": cid }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn send_discord_message(&mut self) {
        let Some(cid) = self.active_chat_channel_id() else {
            return;
        };
        let content = self.discord.chat.composer.text.trim().to_string();
        let upload_ids: Vec<String> = self
            .discord
            .chat
            .pending_uploads
            .iter()
            .filter_map(|u| u.upload_id.clone())
            .collect();
        if content.is_empty() && upload_ids.is_empty() {
            return;
        }
        if self
            .discord
            .chat
            .pending_uploads
            .iter()
            .any(|u| u.uploading)
        {
            self.notice = Some("Wait for attachments to finish uploading".into());
            return;
        }

        let reply_id = self
            .discord
            .chat
            .reply_to
            .as_ref()
            .map(|(id, _)| id.clone());
        let seq = self.discord.chat.next_local_seq;
        self.discord.chat.next_local_seq += 1;
        let local_id = format!("local-{seq}");
        let me = self.discord.home.me.clone();
        let optimistic = DiscordMessage {
            id: local_id.clone(),
            channel_id: cid.clone(),
            author: DiscordUser {
                id: me
                    .as_ref()
                    .map(|m| m.id.clone())
                    .unwrap_or_else(|| "me".into()),
                username: me
                    .as_ref()
                    .map(|m| m.name.clone())
                    .unwrap_or_else(|| "You".into()),
                global_name: me.as_ref().map(|m| m.name.clone()),
                avatar: me.and_then(|m| m.avatar),
                bot: false,
            },
            content: content.clone(),
            timestamp: "2026-11-22T18:15:00.000Z".into(),
            pending: self.status == HostStatus::Connected,
            ..Default::default()
        };

        self.discord
            .chat
            .messages_by_channel
            .entry(cid.clone())
            .or_default()
            .push(optimistic);
        self.discord.chat.composer = TextEdit::default();
        self.discord.chat.reply_to = None;
        self.discord.chat.pending_uploads.clear();
        self.discord.chat.composer_emoji_open = false;

        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let mut params = json!({
                "channelId": cid,
                "content": content,
            });
            if let Some(ref r) = reply_id {
                params["replyTo"] = json!(r);
            }
            if !upload_ids.is_empty() {
                params["uploads"] = json!(upload_ids);
            }
            let cid_c = cid.clone();
            let lid_c = local_id.clone();
            self.call_host_async(
                METHOD_DISCORD_SEND,
                params,
                move |s, res: Result<DiscordSendReply, String>| {
                    let list = s.discord.chat.messages_by_channel.entry(cid_c).or_default();
                    match res {
                        Ok(rep) if rep.ok => {
                            if let Some(sent) = rep.sent {
                                if let Some(pos) = list.iter().position(|m| m.id == lid_c) {
                                    if list.iter().any(|m| m.id == sent.id) {
                                        list.remove(pos);
                                    } else {
                                        list[pos] = sent;
                                    }
                                }
                            } else if let Some(m) = list.iter_mut().find(|m| m.id == lid_c) {
                                m.pending = false;
                            }
                        }
                        Ok(rep) => {
                            if let Some(m) = list.iter_mut().find(|m| m.id == lid_c) {
                                m.pending = false;
                                m.failed = rep.message.or_else(|| Some("Send failed".into()));
                            }
                        }
                        Err(err) => {
                            if let Some(m) = list.iter_mut().find(|m| m.id == lid_c) {
                                m.pending = false;
                                m.failed = Some(err);
                            }
                        }
                    }
                },
            );
        }
    }

    pub fn begin_edit_discord_message(&mut self, message_id: &str, content: &str) {
        let mut edit = TextEdit::default();
        edit.text = content.to_string();
        self.discord.chat.editing = Some((message_id.to_string(), edit));
    }

    pub fn cancel_edit_discord_message(&mut self) {
        self.discord.chat.editing = None;
    }

    pub fn save_discord_edit(&mut self) {
        let Some(cid) = self.active_chat_channel_id() else {
            return;
        };
        let Some((mid, edit)) = self.discord.chat.editing.take() else {
            return;
        };
        let content = edit.text.trim().to_string();
        if content.is_empty() {
            return;
        }
        if let Some(list) = self.discord.chat.messages_by_channel.get_mut(&cid)
            && let Some(m) = list.iter_mut().find(|m| m.id == mid)
        {
            m.content = content.clone();
            m.edited_timestamp = Some("edited".into());
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_EDIT,
            json!({ "channelId": cid, "messageId": mid, "content": content }),
            |s, res: Result<ActionResult, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                    && let Some(msg) = rep.message
                {
                    s.notice = Some(msg);
                }
            },
        );
    }

    pub fn delete_discord_message(&mut self, channel_id: &str, message_id: &str) {
        if let Some(list) = self.discord.chat.messages_by_channel.get_mut(channel_id) {
            list.retain(|m| m.id != message_id);
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_DELETE,
            json!({ "channelId": channel_id, "messageId": message_id }),
            |s, res: Result<ActionResult, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                    && let Some(msg) = rep.message
                {
                    s.notice = Some(msg);
                }
            },
        );
    }

    pub fn toggle_discord_reaction(
        &mut self,
        channel_id: &str,
        message_id: &str,
        emoji_key: &str,
        currently_me: bool,
    ) {
        self.discord.chat.emoji_picker_for = None;
        let add = !currently_me;
        if let Some(list) = self.discord.chat.messages_by_channel.get_mut(channel_id)
            && let Some(m) = list.iter_mut().find(|m| m.id == message_id)
        {
            if let Some(pos) = m.reactions.iter().position(|r| r.emoji.key() == emoji_key) {
                if add {
                    if !m.reactions[pos].me {
                        m.reactions[pos].count += 1;
                        m.reactions[pos].me = true;
                    }
                } else {
                    m.reactions[pos].count = (m.reactions[pos].count - 1).max(0);
                    m.reactions[pos].me = false;
                    if m.reactions[pos].count == 0 {
                        m.reactions.remove(pos);
                    }
                }
            } else if add {
                m.reactions.push(DiscordReaction {
                    count: 1,
                    me: true,
                    emoji: DiscordReactionEmoji {
                        id: None,
                        name: Some(emoji_key.to_string()),
                    },
                });
            }
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_REACT,
            json!({
                "channelId": channel_id,
                "messageId": message_id,
                "emoji": emoji_key,
                "add": add,
            }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn toggle_discord_pin(
        &mut self,
        channel_id: &str,
        message_id: &str,
        currently_pinned: bool,
    ) {
        let pin = !currently_pinned;
        if let Some(list) = self.discord.chat.messages_by_channel.get_mut(channel_id)
            && let Some(m) = list.iter_mut().find(|m| m.id == message_id)
        {
            m.pinned = pin;
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_PINS,
            json!({ "channelId": channel_id, "messageId": message_id, "pin": pin }),
            |s, res: Result<ActionResult, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                    && let Some(msg) = rep.message
                {
                    s.notice = Some(msg);
                }
            },
        );
    }

    pub fn toggle_discord_pins_drawer(&mut self) {
        if self.discord.chat.drawer == ChatDrawer::Pins {
            self.discord.chat.drawer = ChatDrawer::None;
            return;
        }
        self.discord.chat.drawer = ChatDrawer::Pins;
        let Some(cid) = self.active_chat_channel_id() else {
            return;
        };
        if self.status != HostStatus::Connected {
            self.discord.chat.pins = self
                .discord
                .chat
                .messages_by_channel
                .get(&cid)
                .map(|list| list.iter().filter(|m| m.pinned).cloned().collect())
                .unwrap_or_default();
            return;
        }
        self.discord.chat.pins_loading = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_PINS,
            json!({ "channelId": cid }),
            |s, res: Result<DiscordPinsReply, String>| {
                s.discord.chat.pins_loading = false;
                if let Ok(rep) = res
                    && rep.ok
                    && let Some(msgs) = rep.messages
                {
                    s.discord.chat.pins = msgs;
                }
            },
        );
    }

    pub fn toggle_discord_search_drawer(&mut self) {
        if self.discord.chat.drawer == ChatDrawer::Search {
            self.discord.chat.drawer = ChatDrawer::None;
        } else {
            self.discord.chat.drawer = ChatDrawer::Search;
        }
    }

    pub fn run_discord_search(&mut self) {
        let Some(cid) = self.active_chat_channel_id() else {
            return;
        };
        let query = self.discord.chat.search_input.text.trim().to_string();
        if query.is_empty() {
            self.discord.chat.search_results.clear();
            self.discord.chat.search_total = 0;
            return;
        }
        if self.status != HostStatus::Connected {
            let low = query.to_lowercase();
            let hits: Vec<DiscordMessage> = self
                .discord
                .chat
                .messages_by_channel
                .get(&cid)
                .map(|list| {
                    list.iter()
                        .filter(|m| {
                            m.content.to_lowercase().contains(&low)
                                || m.display_name().to_lowercase().contains(&low)
                        })
                        .cloned()
                        .collect()
                })
                .unwrap_or_default();
            self.discord.chat.search_total = hits.len();
            self.discord.chat.search_results = hits;
            return;
        }
        self.discord.chat.search_loading = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SEARCH,
            json!({
                "channelId": cid,
                "filters": { "content": query, "sort": "newest" },
            }),
            |s, res: Result<DiscordSearchReply, String>| {
                s.discord.chat.search_loading = false;
                if let Ok(rep) = res
                    && rep.ok
                    && let Some(r) = rep.result
                {
                    s.discord.chat.search_total = r.total_results;
                    s.discord.chat.search_results = r
                        .messages
                        .into_iter()
                        .filter_map(|group| group.into_iter().next())
                        .collect();
                }
            },
        );
    }

    pub fn attach_discord_files(&mut self, paths: &[PathBuf]) {
        self.discord.chat.drop_hover = false;
        for path in paths.iter().take(4) {
            let Ok(bytes) = std::fs::read(path) else {
                continue;
            };
            if bytes.is_empty() || bytes.len() > 25 * 1024 * 1024 {
                self.notice = Some("Files must be under 25 MB".into());
                continue;
            }
            let name = path
                .file_name()
                .and_then(|n| n.to_str())
                .unwrap_or("attachment.bin")
                .to_string();
            let size = bytes.len() as u64;
            let seq = self.discord.chat.next_local_seq;
            self.discord.chat.next_local_seq += 1;
            let local_id = format!("up-{seq}");

            if self.status != HostStatus::Connected {
                self.discord.chat.pending_uploads.push(PendingAttachment {
                    local_id: local_id.clone(),
                    name,
                    size,
                    upload_id: Some(local_id),
                    uploading: false,
                    error: None,
                });
                continue;
            }

            self.discord.chat.pending_uploads.push(PendingAttachment {
                local_id: local_id.clone(),
                name: name.clone(),
                size,
                upload_id: None,
                uploading: true,
                error: None,
            });
            #[cfg(windows)]
            if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
                let mime = guess_mime(&name).to_string();
                std::thread::Builder::new()
                    .name("exo-dc-upload".into())
                    .spawn(move || {
                        let mut upload_id: Option<String> = None;
                        let mut error: Option<String> = None;
                        for chunk in bytes.chunks(512 * 1024) {
                            let encoded = base64::encode(chunk);
                            let mut payload = json!({
                                "name": name,
                                "type": mime,
                                "data": encoded,
                            });
                            if let Some(ref uid) = upload_id {
                                payload["uploadId"] = json!(uid);
                            }
                            match rpc.call_blocking::<_, DiscordUploadReply>(
                                METHOD_DISCORD_UPLOAD,
                                &payload,
                                Duration::from_secs(15),
                            ) {
                                Ok(rep) if rep.ok => {
                                    if let Some(uid) = rep.upload_id {
                                        upload_id = Some(uid);
                                    }
                                }
                                Ok(rep) => {
                                    error = rep.message.or_else(|| Some("Upload failed".into()));
                                    break;
                                }
                                Err(e) => {
                                    error = Some(e.to_string());
                                    break;
                                }
                            }
                        }
                        handle.update(move |s, _| {
                            if let Some(item) = s
                                .discord
                                .chat
                                .pending_uploads
                                .iter_mut()
                                .find(|u| u.local_id == local_id)
                            {
                                item.uploading = false;
                                item.upload_id = upload_id;
                                item.error = error;
                            }
                        });
                    })
                    .ok();
            }
        }
    }

    pub fn remove_pending_upload(&mut self, local_id: &str) {
        let mut cancel_id = None;
        self.discord.chat.pending_uploads.retain(|u| {
            if u.local_id == local_id {
                cancel_id = u.upload_id.clone();
                false
            } else {
                true
            }
        });
        #[cfg(windows)]
        if let Some(uid) = cancel_id
            && self.status == HostStatus::Connected
        {
            self.call_host_async(
                METHOD_DISCORD_UPLOAD,
                json!({ "uploadId": uid, "cancel": true }),
                |_s, _res: Result<ActionResult, String>| {},
            );
        }
    }

    pub fn handle_discord_message(&mut self, ev: DiscordMessageEvent) {
        let cid = ev.channel_id;
        match ev.kind.as_str() {
            "create" => {
                let Some(msg) = ev.message else { return };
                let list = self
                    .discord
                    .chat
                    .messages_by_channel
                    .entry(cid.clone())
                    .or_default();
                if let Some(pos) = list.iter().position(|m| m.id == msg.id) {
                    list[pos] = msg;
                } else {
                    if let Some(ppos) = list.iter().position(|m| {
                        m.pending && m.author.id == msg.author.id && m.content == msg.content
                    }) {
                        list[ppos] = msg;
                    } else {
                        list.push(msg);
                    }
                }
                self.discord.chat.typing_by_channel.remove(&cid);
            }
            "update" => {
                let Some(msg) = ev.message else { return };
                if let Some(list) = self.discord.chat.messages_by_channel.get_mut(&cid)
                    && let Some(existing) = list.iter_mut().find(|m| m.id == msg.id)
                {
                    *existing = msg;
                }
            }
            "delete" => {
                let Some(mid) = ev.id else { return };
                if let Some(list) = self.discord.chat.messages_by_channel.get_mut(&cid) {
                    list.retain(|m| m.id != mid);
                }
            }
            _ => {}
        }
    }

    pub fn handle_discord_reaction(&mut self, ev: DiscordReactionEvent) {
        let Some(list) = self
            .discord
            .chat
            .messages_by_channel
            .get_mut(&ev.channel_id)
        else {
            return;
        };
        let Some(msg) = list.iter_mut().find(|m| m.id == ev.message_id) else {
            return;
        };
        let key = ev.emoji.key();
        if let Some(pos) = msg.reactions.iter().position(|r| r.emoji.key() == key) {
            if ev.add {
                msg.reactions[pos].count += 1;
                if ev.me {
                    msg.reactions[pos].me = true;
                }
            } else {
                msg.reactions[pos].count = (msg.reactions[pos].count - 1).max(0);
                if ev.me {
                    msg.reactions[pos].me = false;
                }
                if msg.reactions[pos].count == 0 {
                    msg.reactions.remove(pos);
                }
            }
        } else if ev.add {
            msg.reactions.push(DiscordReaction {
                count: 1,
                me: ev.me,
                emoji: ev.emoji,
            });
        }
    }

    pub fn handle_discord_typing(&mut self, ev: DiscordTypingEvent) {
        if self
            .discord
            .home
            .me
            .as_ref()
            .is_some_and(|m| m.id == ev.user_id)
        {
            return;
        }
        self.discord
            .chat
            .typing_by_channel
            .insert(ev.channel_id, (ev.name, Instant::now()));
    }
}

#[cfg(windows)]
fn guess_mime(name: &str) -> &'static str {
    let lower = name.to_ascii_lowercase();
    if lower.ends_with(".png") {
        "image/png"
    } else if lower.ends_with(".jpg") || lower.ends_with(".jpeg") {
        "image/jpeg"
    } else if lower.ends_with(".webp") {
        "image/webp"
    } else if lower.ends_with(".gif") {
        "image/gif"
    } else if lower.ends_with(".mp4") {
        "video/mp4"
    } else if lower.ends_with(".txt") || lower.ends_with(".log") {
        "text/plain"
    } else {
        "application/octet-stream"
    }
}
