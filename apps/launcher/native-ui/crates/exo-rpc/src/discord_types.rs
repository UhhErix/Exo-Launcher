use crate::discord_chat_types::{
    DiscordAppComponent, DiscordInteractionMeta, DiscordMessageCall, DiscordMessageSnapshot,
    DiscordMessageThread, DiscordPlaceRef, DiscordPoll, DiscordReactionCountDetails,
    DiscordStickerItem,
};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordStatus {
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub signed_in: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordStateEvent {
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordSignInStep {
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub user: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAccountItem {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub current: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAccountsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub accounts: Vec<DiscordAccountItem>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordDecoration {
    #[serde(default)]
    pub asset: String,
    #[serde(default)]
    pub sku_id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordNameplate {
    #[serde(default)]
    pub sku_id: String,
    #[serde(default)]
    pub asset: Option<String>,
    #[serde(default)]
    pub palette: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordNameStyle {
    #[serde(default)]
    pub font: i32,
    #[serde(default)]
    pub effect: i32,
    #[serde(default)]
    pub colors: Vec<u32>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGuildTag {
    #[serde(default)]
    pub guild_id: String,
    #[serde(default)]
    pub tag: String,
    #[serde(default)]
    pub badge: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordLook {
    #[serde(default)]
    pub decoration: Option<DiscordDecoration>,
    #[serde(default)]
    pub nameplate: Option<DiscordNameplate>,
    #[serde(default)]
    pub name_style: Option<DiscordNameStyle>,
    #[serde(default)]
    pub tag: Option<DiscordGuildTag>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordRestricted {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub username: Option<String>,
    #[serde(default)]
    pub avatar: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordRestrictedSet {
    #[serde(default)]
    pub blocked: Vec<DiscordRestricted>,
    #[serde(default)]
    pub ignored: Vec<DiscordRestricted>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMe {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default)]
    pub look: Option<DiscordLook>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordDmMember {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub username: Option<String>,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub status: String,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordVoiceMember {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub mute: bool,
    #[serde(default)]
    pub deaf: bool,
    #[serde(default)]
    pub video: bool,
    #[serde(default)]
    pub stream: bool,
    #[serde(default)]
    pub server_mute: bool,
    #[serde(default)]
    pub server_deaf: bool,
    #[serde(default)]
    pub suppress: bool,
    #[serde(default)]
    pub request_to_speak: Option<String>,
    #[serde(default)]
    pub look: Option<DiscordLook>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordDm {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub last_at: Option<i64>,
    #[serde(default)]
    pub line: Option<String>,
    #[serde(default)]
    pub look: Option<DiscordLook>,
    #[serde(default)]
    pub group: bool,
    #[serde(default)]
    pub owner_id: Option<String>,
    #[serde(default)]
    pub members: Option<Vec<DiscordDmMember>>,
    #[serde(default)]
    pub unread: bool,
    #[serde(default)]
    pub mentions: i32,
    #[serde(default)]
    pub muted: bool,
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default)]
    pub user_id: Option<String>,
    #[serde(default)]
    pub username: Option<String>,
    #[serde(default)]
    pub subtitle: Option<String>,
    #[serde(default)]
    pub call: Option<Vec<DiscordVoiceMember>>,
    #[serde(default)]
    pub request: bool,
    #[serde(default)]
    pub spam: bool,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGuild {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub icon: Option<String>,
    #[serde(default)]
    pub muted: bool,
    #[serde(default)]
    pub unread: bool,
    #[serde(default)]
    pub mentions: i32,
    #[serde(default)]
    pub banner: Option<String>,
    #[serde(default)]
    pub in_voice: i32,
    #[serde(default)]
    pub owner: bool,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordRailItem {
    #[serde(default)]
    pub id: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub color: Option<u32>,
    #[serde(default)]
    pub guild_ids: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordHome {
    #[serde(default)]
    pub me: Option<DiscordMe>,
    #[serde(default)]
    pub dms: Vec<DiscordDm>,
    #[serde(default)]
    pub guilds: Vec<DiscordGuild>,
    #[serde(default)]
    pub rail: Vec<DiscordRailItem>,
    #[serde(default)]
    pub blocked: Vec<String>,
    #[serde(default)]
    pub ignored: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordChannel {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(rename = "type", default)]
    pub channel_type: i32,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub topic: Option<String>,
    #[serde(default)]
    pub slowmode: i32,
    #[serde(default)]
    pub can_read: Option<bool>,
    #[serde(default)]
    pub can_send: Option<bool>,
    #[serde(default)]
    pub muted: bool,
    #[serde(default)]
    pub unread: bool,
    #[serde(default)]
    pub mentions: i32,
    #[serde(default)]
    pub can_connect: Option<bool>,
    #[serde(default)]
    pub can_manage_messages: bool,
    #[serde(default)]
    pub can_pin: bool,
    #[serde(default)]
    pub can_edit: bool,
    #[serde(default)]
    pub members: Option<Vec<DiscordVoiceMember>>,
    #[serde(default)]
    pub user_limit: Option<i32>,
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default)]
    pub stage_topic: Option<String>,
    #[serde(default)]
    pub activity: Option<String>,
    #[serde(default)]
    pub collapsed: bool,
    #[serde(default)]
    pub locked: bool,
    #[serde(default)]
    pub rules: bool,
    #[serde(default)]
    pub afk: bool,
    #[serde(default)]
    pub resource: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGuildRows {
    #[serde(default)]
    pub guide: bool,
    #[serde(default)]
    pub events: bool,
    #[serde(default)]
    pub browse: bool,
    #[serde(default)]
    pub members: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordThreadSummary {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub parent_id: String,
    #[serde(default)]
    pub locked: bool,
    #[serde(default)]
    pub joined: bool,
    #[serde(default)]
    pub archived: bool,
    #[serde(default)]
    pub unread: bool,
    #[serde(default)]
    pub mentions: i32,
    #[serde(default)]
    pub muted: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordChannels {
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub channels: Vec<DiscordChannel>,
    #[serde(default)]
    pub icon: Option<String>,
    #[serde(default)]
    pub banner: Option<String>,
    #[serde(default)]
    pub can_manage: bool,
    #[serde(default)]
    pub can_manage_events: bool,
    #[serde(default)]
    pub hide_muted: bool,
    #[serde(default)]
    pub events: i32,
    #[serde(default)]
    pub rows: Option<DiscordGuildRows>,
    #[serde(default)]
    pub threads: Vec<DiscordThreadSummary>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMember {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub color: Option<String>,
    #[serde(default)]
    pub bot: bool,
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub activity: Option<String>,
    #[serde(default)]
    pub owner: bool,
    #[serde(default)]
    pub boosting: Option<String>,
    #[serde(default)]
    pub in_voice: Option<String>,
    #[serde(default)]
    pub look: Option<Box<DiscordLook>>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(untagged)]
pub enum DiscordMemberItem {
    Group { group: String, count: i32 },
    Member(DiscordMember),
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMemberList {
    #[serde(default)]
    pub items: Vec<Option<DiscordMemberItem>>,
    #[serde(default)]
    pub online: i32,
    #[serde(default)]
    pub total: i32,
    #[serde(default)]
    pub rows: Option<i32>,
}

#[derive(Debug, Clone, Default, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordRich {
    #[serde(rename = "type", default)]
    pub activity_type: i32,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub details: Option<String>,
    #[serde(default)]
    pub state: Option<String>,
    #[serde(default)]
    pub start: Option<i64>,
    #[serde(default)]
    pub image: Option<String>,
    #[serde(default)]
    pub through_exo: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordPrep {
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub percent: Option<i32>,
}

pub type DiscordLookTag = DiscordGuildTag;
pub type DiscordLookNameplate = DiscordNameplate;

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordFriend {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub username: Option<String>,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub look: Option<DiscordLook>,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub activity: Option<String>,
    #[serde(default)]
    pub activity_type: Option<i32>,
    #[serde(default)]
    pub rich: Option<DiscordRich>,
    #[serde(default)]
    pub prep: Option<DiscordPrep>,
    #[serde(default)]
    pub dm_id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordFriendsReply {
    #[serde(default)]
    pub friends: Vec<DiscordFriend>,
    #[serde(default)]
    pub restricted: Option<DiscordRestrictedSet>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordFriendActionReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub channel_id: Option<String>,
    #[serde(default)]
    pub stranger: bool,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordUnreadChannel {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub guild_id: String,
    #[serde(default)]
    pub guild: String,
    #[serde(default)]
    pub icon: Option<String>,
    #[serde(default)]
    pub parent_id: Option<String>,
    #[serde(default)]
    pub last_read: Option<String>,
    #[serde(default)]
    pub last_message: Option<String>,
    #[serde(default)]
    pub mentions: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordUnreadsReply {
    #[serde(default)]
    pub channels: Vec<DiscordUnreadChannel>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordVoice {
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub guild_id: Option<String>,
    #[serde(default)]
    pub channel_id: Option<String>,
    #[serde(default)]
    pub muted: bool,
    #[serde(default)]
    pub deafened: bool,
    #[serde(default)]
    pub e2ee: bool,
    #[serde(default)]
    pub mic_silent: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub rtt_ms: Option<i32>,
    #[serde(default)]
    pub region: Option<String>,
    #[serde(default)]
    pub bitrate: Option<i32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordVoiceReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub voice: Option<DiscordVoice>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordChangedEvent {
    #[serde(default)]
    pub guild_id: Option<String>,
    #[serde(default)]
    pub voice: bool,
    #[serde(default)]
    pub in_voice: Option<i32>,
    #[serde(default)]
    pub friends: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordPresenceEvent {
    #[serde(default)]
    pub user_id: String,
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub activity: Option<String>,
    #[serde(default)]
    pub activity_type: Option<i32>,
    #[serde(default)]
    pub rich: Option<DiscordRich>,
    #[serde(default)]
    pub prep: Option<DiscordPrep>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordUnreadEvent {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub guild_id: Option<String>,
    #[serde(default)]
    pub unread: bool,
    #[serde(default)]
    pub mentions: i32,
    #[serde(default)]
    pub guild_unread: bool,
    #[serde(default)]
    pub guild_mentions: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordUser {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub username: String,
    #[serde(default)]
    pub global_name: Option<String>,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub bot: bool,
}

impl DiscordUser {
    pub fn avatar_url(&self) -> Option<String> {
        let hash = self.avatar.as_deref().filter(|s| !s.is_empty())?;
        if hash.starts_with("https://") {
            return Some(hash.to_string());
        }
        Some(format!(
            "https://cdn.discordapp.com/avatars/{}/{}.webp?size=128&quality=lossless",
            self.id, hash
        ))
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMessageMember {
    #[serde(default)]
    pub nick: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAttachment {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub filename: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub url: String,
    #[serde(default)]
    pub proxy_url: Option<String>,
    #[serde(default)]
    pub size: u64,
    #[serde(default)]
    pub width: Option<u32>,
    #[serde(default)]
    pub height: Option<u32>,
    #[serde(default)]
    pub content_type: Option<String>,
    #[serde(default)]
    pub flags: u32,
    #[serde(default)]
    pub duration_secs: Option<f64>,
    #[serde(default)]
    pub waveform: Option<String>,
}

impl DiscordAttachment {
    pub fn is_image(&self) -> bool {
        if let Some(ct) = self.content_type.as_deref()
            && ct.starts_with("image/")
        {
            return true;
        }
        let lower = self.filename.to_ascii_lowercase();
        lower.ends_with(".png")
            || lower.ends_with(".jpg")
            || lower.ends_with(".jpeg")
            || lower.ends_with(".webp")
            || lower.ends_with(".gif")
    }

    pub fn best_image_url(&self) -> &str {
        self.proxy_url
            .as_deref()
            .filter(|u| !u.is_empty())
            .unwrap_or(&self.url)
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordEmbedMedia {
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub proxy_url: Option<String>,
    #[serde(default)]
    pub width: Option<u32>,
    #[serde(default)]
    pub height: Option<u32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordEmbedAuthor {
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub url: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordEmbedProvider {
    #[serde(default)]
    pub name: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordEmbedFooter {
    #[serde(default)]
    pub text: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordEmbedField {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub value: String,
    #[serde(default)]
    pub inline: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordEmbed {
    #[serde(rename = "type", default)]
    pub kind: Option<String>,
    #[serde(default)]
    pub title: Option<String>,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub color: Option<u32>,
    #[serde(default)]
    pub author: Option<DiscordEmbedAuthor>,
    #[serde(default)]
    pub provider: Option<DiscordEmbedProvider>,
    #[serde(default)]
    pub footer: Option<DiscordEmbedFooter>,
    #[serde(default)]
    pub fields: Vec<DiscordEmbedField>,
    #[serde(default)]
    pub image: Option<DiscordEmbedMedia>,
    #[serde(default)]
    pub thumbnail: Option<DiscordEmbedMedia>,
    #[serde(default)]
    pub video: Option<DiscordEmbedMedia>,
    #[serde(default)]
    pub timestamp: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordReactionEmoji {
    #[serde(default)]
    pub id: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub animated: bool,
}

impl DiscordReactionEmoji {
    pub fn key(&self) -> String {
        match (self.name.as_deref(), self.id.as_deref()) {
            (Some(n), Some(id)) if !id.is_empty() => format!("{n}:{id}"),
            (Some(n), _) => n.to_string(),
            _ => String::new(),
        }
    }

    pub fn display(&self) -> String {
        match (self.name.as_deref(), self.id.as_deref()) {
            (Some(n), Some(id)) if !id.is_empty() => format!(":{n}:"),
            (Some(n), _) => n.to_string(),
            _ => "?".into(),
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordReaction {
    #[serde(default)]
    pub count: i32,
    #[serde(default)]
    pub me: bool,
    #[serde(default)]
    pub emoji: DiscordReactionEmoji,
    #[serde(default)]
    pub count_details: Option<DiscordReactionCountDetails>,
    #[serde(default)]
    pub me_burst: bool,
    #[serde(default)]
    pub burst_colors: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMessageRef {
    #[serde(rename = "type", default)]
    pub ref_type: Option<i32>,
    #[serde(default)]
    pub message_id: Option<String>,
    #[serde(default)]
    pub channel_id: Option<String>,
    #[serde(default)]
    pub guild_id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMessage {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub channel_id: String,
    #[serde(rename = "type", default)]
    pub msg_type: i32,
    #[serde(default)]
    pub author: DiscordUser,
    #[serde(default)]
    pub member: Option<DiscordMessageMember>,
    #[serde(default)]
    pub content: String,
    #[serde(default)]
    pub timestamp: String,
    #[serde(default)]
    pub edited_timestamp: Option<String>,
    #[serde(default)]
    pub attachments: Vec<DiscordAttachment>,
    #[serde(default)]
    pub embeds: Vec<DiscordEmbed>,
    #[serde(default)]
    pub sticker_items: Vec<DiscordStickerItem>,
    #[serde(default)]
    pub poll: Option<DiscordPoll>,
    #[serde(default)]
    pub call: Option<DiscordMessageCall>,
    #[serde(default)]
    pub mentions: Vec<DiscordUser>,
    #[serde(default)]
    pub mention_everyone: bool,
    #[serde(default)]
    pub mention_roles: Vec<String>,
    #[serde(default)]
    pub referenced_message: Option<Box<DiscordMessage>>,
    #[serde(default)]
    pub message_reference: Option<DiscordMessageRef>,
    #[serde(default)]
    pub message_snapshots: Vec<DiscordMessageSnapshot>,
    #[serde(default)]
    pub interaction_metadata: Option<DiscordInteractionMeta>,
    #[serde(default)]
    pub interaction: Option<DiscordInteractionMeta>,
    #[serde(default)]
    pub thread: Option<DiscordMessageThread>,
    #[serde(default)]
    pub flags: u32,
    #[serde(default)]
    pub components: Vec<DiscordAppComponent>,
    #[serde(default)]
    pub application_id: Option<String>,
    #[serde(default)]
    pub reactions: Vec<DiscordReaction>,
    #[serde(default)]
    pub pinned: bool,
    #[serde(default)]
    pub pending: bool,
    #[serde(default)]
    pub failed: Option<String>,
}

impl DiscordMessage {
    pub fn display_name(&self) -> &str {
        self.member
            .as_ref()
            .and_then(|m| m.nick.as_deref())
            .filter(|s| !s.is_empty())
            .or_else(|| self.author.global_name.as_deref().filter(|s| !s.is_empty()))
            .unwrap_or(&self.author.username)
    }

    pub fn snowflake_ms(&self) -> Option<i64> {
        let id = self.id.parse::<u64>().ok()?;
        Some(((id >> 22) as i64) + 1_420_070_400_000)
    }

    pub fn time_short(&self) -> String {
        if let Some(t_pos) = self.timestamp.find('T') {
            let rest = &self.timestamp[t_pos + 1..];
            if rest.len() >= 5 {
                return rest[..5].to_string();
            }
        }
        String::new()
    }

    pub fn day_key(&self) -> &str {
        self.timestamp.get(..10).unwrap_or("")
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMessagesReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub messages: Option<Vec<DiscordMessage>>,
    #[serde(default)]
    pub last_read: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordSendReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub sent: Option<DiscordMessage>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordPinsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub messages: Option<Vec<DiscordMessage>>,
    #[serde(default)]
    pub places: HashMap<String, Option<DiscordPlaceRef>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordSearchResult {
    #[serde(default)]
    pub messages: Vec<Vec<DiscordMessage>>,
    #[serde(default)]
    pub total_results: usize,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordSearchReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub result: Option<DiscordSearchResult>,
    #[serde(default)]
    pub places: HashMap<String, Option<DiscordPlaceRef>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordUploadReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub upload_id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMessageEvent {
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub message: Option<DiscordMessage>,
    #[serde(default)]
    pub id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordReactionEvent {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub message_id: String,
    #[serde(default)]
    pub add: bool,
    #[serde(default)]
    pub me: bool,
    #[serde(default)]
    pub emoji: DiscordReactionEmoji,
    #[serde(default)]
    pub user_id: Option<String>,
    #[serde(default)]
    pub burst: bool,
    #[serde(default)]
    pub burst_colors: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordTypingEvent {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub user_id: String,
    #[serde(default)]
    pub name: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAudioDevice {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAudioReply {
    #[serde(default)]
    pub inputs: Vec<DiscordAudioDevice>,
    #[serde(default)]
    pub outputs: Vec<DiscordAudioDevice>,
    #[serde(default)]
    pub input: Option<String>,
    #[serde(default)]
    pub output: Option<String>,
    #[serde(default)]
    pub input_volume: f64,
    #[serde(default)]
    pub output_volume: f64,
    #[serde(default)]
    pub soundboard_volume: f64,
    #[serde(default)]
    pub soundboard_muted: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordSoundItem {
    #[serde(default)]
    pub sound_id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub volume: f64,
    #[serde(default)]
    pub emoji_id: Option<String>,
    #[serde(default)]
    pub emoji_name: Option<String>,
    #[serde(default)]
    pub guild_id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordSoundboardReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub premium: bool,
    #[serde(default)]
    pub defaults: Option<Vec<DiscordSoundItem>>,
    #[serde(default)]
    pub guild: Option<Vec<DiscordSoundItem>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GoLiveSource {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub app: Option<String>,
    #[serde(default)]
    pub game: bool,
    #[serde(default)]
    pub width: i32,
    #[serde(default)]
    pub height: i32,
    #[serde(default)]
    pub hz: i32,
    #[serde(default)]
    pub primary: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGoLiveReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub capture_mode: Option<String>,
    #[serde(default)]
    pub service_disabled: bool,
    #[serde(default)]
    pub sources: Vec<GoLiveSource>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGoLiveStateEvent {
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub title: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGoLiveStatsEvent {
    #[serde(default)]
    pub width: i32,
    #[serde(default)]
    pub height: i32,
    #[serde(default)]
    pub fps: f64,
    #[serde(default)]
    pub bitrate: f64,
    #[serde(default)]
    pub codec: Option<String>,
    #[serde(default)]
    pub sound: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMyVoiceReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub learned: bool,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordVideoEvent {
    #[serde(default)]
    pub source: String,
    #[serde(default)]
    pub user_id: String,
    #[serde(default)]
    pub active: bool,
    #[serde(default)]
    pub state: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordVideoFrameMeta {
    #[serde(default)]
    pub source: String,
    #[serde(default)]
    pub user_id: String,
    #[serde(default)]
    pub timestamp: f64,
    #[serde(default)]
    pub keyframe: bool,
    #[serde(default)]
    pub rotation: i32,
    #[serde(default)]
    pub flip: bool,
    #[serde(default)]
    pub codec: Option<String>,
}

#[derive(Debug, Clone)]
pub struct DiscordVideoFrame {
    pub source: String,
    pub user_id: String,
    pub timestamp: f64,
    pub keyframe: bool,
    pub rotation: i32,
    pub flip: bool,
    pub codec: String,
    pub data: Vec<u8>,
}

pub const DISCORD_SNAPSHOT_VERSION: u32 = 1;

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordDiskSnapshot {
    pub version: u32,
    #[serde(default)]
    pub home: DiscordHome,
    #[serde(default)]
    pub friends: Vec<DiscordFriend>,
    #[serde(default)]
    pub blocked: Vec<DiscordRestricted>,
    #[serde(default)]
    pub accounts: Vec<DiscordAccountItem>,
    #[serde(default)]
    pub unreads: Vec<DiscordUnreadChannel>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn deserializes_discord_member_list_groups_and_members() {
        let raw = r##"{
            "items": [
                { "group": "Online", "count": 2 },
                { "id": "101", "name": "Ada", "avatar": null, "color": "#5eead4", "bot": false, "status": "online", "activity": "Playing VALORANT", "inVoice": "vc-1" },
                null
            ],
            "online": 2,
            "total": 10,
            "rows": 3
        }"##;
        let parsed: DiscordMemberList = serde_json::from_str(raw).expect("valid member list");
        assert_eq!(parsed.online, 2);
        assert_eq!(parsed.items.len(), 3);
        assert!(matches!(
            parsed.items[0],
            Some(DiscordMemberItem::Group { ref group, count: 2 }) if group == "Online"
        ));
        assert!(matches!(
            parsed.items[1],
            Some(DiscordMemberItem::Member(ref m)) if m.name == "Ada" && m.in_voice.as_deref() == Some("vc-1")
        ));
        assert!(parsed.items[2].is_none());
    }

    #[test]
    fn discord_disk_snapshot_round_trips_public_state() {
        let snap = DiscordDiskSnapshot {
            version: DISCORD_SNAPSHOT_VERSION,
            home: DiscordHome {
                me: Some(DiscordMe {
                    id: "100".into(),
                    name: "Erix".into(),
                    status: Some("online".into()),
                    ..Default::default()
                }),
                dms: vec![DiscordDm {
                    id: "dm-1".into(),
                    name: "Ada".into(),
                    unread: true,
                    mentions: 2,
                    ..Default::default()
                }],
                guilds: vec![DiscordGuild {
                    id: "g-1".into(),
                    name: "Exo".into(),
                    in_voice: 1,
                    ..Default::default()
                }],
                ..Default::default()
            },
            friends: vec![DiscordFriend {
                id: "200".into(),
                name: "Ada".into(),
                kind: "friend".into(),
                status: "online".into(),
                dm_id: Some("dm-1".into()),
                ..Default::default()
            }],
            blocked: vec![DiscordRestricted {
                id: "999".into(),
                name: "Spam".into(),
                ..Default::default()
            }],
            accounts: vec![DiscordAccountItem {
                id: "100".into(),
                name: "Erix".into(),
                current: true,
                ..Default::default()
            }],
            unreads: vec![DiscordUnreadChannel {
                channel_id: "ch-1".into(),
                name: "general".into(),
                guild_id: "g-1".into(),
                guild: "Exo".into(),
                mentions: 1,
                ..Default::default()
            }],
        };

        let bytes = serde_json::to_vec(&snap).expect("serialize snapshot");
        let parsed: DiscordDiskSnapshot =
            serde_json::from_slice(&bytes).expect("deserialize snapshot");
        assert_eq!(parsed.version, DISCORD_SNAPSHOT_VERSION);
        assert_eq!(
            parsed.home.me.as_ref().map(|m| m.name.as_str()),
            Some("Erix")
        );
        assert_eq!(parsed.home.dms.len(), 1);
        assert_eq!(parsed.home.guilds.len(), 1);
        assert_eq!(parsed.friends.len(), 1);
        assert_eq!(parsed.blocked.len(), 1);
        assert_eq!(parsed.accounts.len(), 1);
        assert_eq!(parsed.unreads.len(), 1);
    }
}
