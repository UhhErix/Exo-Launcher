use serde::{Deserialize, Deserializer, Serialize};
use serde_json::Value;
use std::collections::HashMap;

fn de_optional_game_name<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let val: Option<Value> = Option::deserialize(deserializer)?;
    Ok(match val {
        Some(Value::String(s)) if !s.is_empty() => Some(s),
        Some(Value::Bool(true)) => Some("Game".into()),
        _ => None,
    })
}

fn de_sound_items_flexible<'de, D>(deserializer: D) -> Result<Vec<DiscordSoundItem>, D::Error>
where
    D: Deserializer<'de>,
{
    let val: Option<Value> = Option::deserialize(deserializer)?;
    let Some(v) = val else {
        return Ok(Vec::new());
    };
    if let Ok(items) = serde_json::from_value::<Vec<DiscordSoundItem>>(v.clone()) {
        return Ok(items);
    }
    if let Some(arr) = v.get("items")
        && let Ok(items) = serde_json::from_value::<Vec<DiscordSoundItem>>(arr.clone())
    {
        return Ok(items);
    }
    Ok(Vec::new())
}

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
    #[serde(default)]
    pub muted_until: Option<i64>,
    #[serde(default)]
    pub notify: Option<i32>,
    #[serde(default)]
    pub suppress_everyone: bool,
    #[serde(default)]
    pub suppress_roles: bool,
    #[serde(default)]
    pub suppress_highlights: bool,
    #[serde(default)]
    pub mute_events: bool,
    #[serde(default)]
    pub mobile_push: bool,
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
    pub can_set_status: bool,
    #[serde(default)]
    pub stage_topic: Option<String>,
    #[serde(default)]
    pub can_moderate_stage: bool,
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
    #[serde(default)]
    pub opted_in: Option<bool>,
    #[serde(default)]
    pub private: bool,
    #[serde(default)]
    pub notify: Option<i32>,
    #[serde(default)]
    pub notify_default: Option<i32>,
    #[serde(default)]
    pub muted_until: Option<i64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordModeration {
    #[serde(default)]
    pub kick: bool,
    #[serde(default)]
    pub ban: bool,
    #[serde(default)]
    pub manage_guild: bool,
    #[serde(default)]
    pub audit_log: bool,
    #[serde(default)]
    pub manage_roles: bool,
    #[serde(default)]
    pub timeout: bool,
    #[serde(default)]
    pub manage_channels: bool,
    #[serde(default)]
    pub manage_expressions: bool,
    #[serde(default)]
    pub manage_webhooks: bool,
    #[serde(default)]
    pub manage_nicknames: bool,
    #[serde(default)]
    pub prune: bool,
    #[serde(default)]
    pub owner: bool,
    #[serde(default)]
    pub administrator: Option<bool>,
    #[serde(default)]
    pub insights: Option<bool>,
    #[serde(default)]
    pub mfa: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordRoleBrief {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub color: Option<String>,
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
    pub opt_in: bool,
    #[serde(default)]
    pub features: Vec<String>,
    #[serde(default)]
    pub events: i32,
    #[serde(default)]
    pub rows: Option<DiscordGuildRows>,
    #[serde(default)]
    pub moderation: Option<DiscordModeration>,
    #[serde(default)]
    pub threads: Vec<DiscordThreadSummary>,
    #[serde(default)]
    pub roles: HashMap<String, DiscordRoleBrief>,
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
    pub ringing: Option<String>,
    #[serde(default)]
    pub recorded: bool,
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
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordReactionEmoji {
    #[serde(default)]
    pub id: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
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
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMessageRef {
    #[serde(default)]
    pub message_id: Option<String>,
    #[serde(default)]
    pub channel_id: Option<String>,
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
    pub mentions: Vec<DiscordUser>,
    #[serde(default)]
    pub mention_everyone: bool,
    #[serde(default)]
    pub referenced_message: Option<Box<DiscordMessage>>,
    #[serde(default)]
    pub message_reference: Option<DiscordMessageRef>,
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
    #[serde(default)]
    pub available: Option<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGuildSoundboard {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub items: Vec<DiscordSoundItem>,
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
    #[serde(default, deserialize_with = "de_sound_items_flexible")]
    pub defaults: Vec<DiscordSoundItem>,
    #[serde(default, deserialize_with = "de_sound_items_flexible")]
    pub guild: Vec<DiscordSoundItem>,
    #[serde(default)]
    pub guilds: Vec<DiscordGuildSoundboard>,
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
    pub icon: Option<String>,
    #[serde(default)]
    pub thumb: Option<String>,
    #[serde(default, deserialize_with = "de_optional_game_name")]
    pub game: Option<String>,
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
    #[serde(default)]
    pub thumb: Option<String>,
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
    pub max: Option<f64>,
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

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordCallEvent {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub ringing: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordCallSong {
    #[serde(default)]
    pub user_id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub rich: Option<DiscordRich>,
    #[serde(default)]
    pub paused: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordCallSongsReply {
    #[serde(default)]
    pub songs: Vec<DiscordCallSong>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordRoleIcon {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub emoji: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordColorsReply {
    #[serde(default)]
    pub colors: HashMap<String, Option<String>>,
    #[serde(default)]
    pub avatars: HashMap<String, String>,
    #[serde(default)]
    pub icons: HashMap<String, DiscordRoleIcon>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordDmLineEvent {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub line: Option<String>,
    #[serde(default)]
    pub at: Option<i64>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DiscordKeybind {
    #[serde(default)]
    pub action: String,
    #[serde(default)]
    pub keys: Vec<u32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordVoiceStages {
    #[serde(default)]
    pub profile: String,
    #[serde(default)]
    pub suppression: String,
    #[serde(default)]
    pub suppression_limit_db: f64,
    #[serde(default)]
    pub focus: bool,
    #[serde(default)]
    pub gain: String,
    #[serde(default)]
    pub echo: String,
    #[serde(default)]
    pub tuned: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMicHeard {
    #[serde(default)]
    pub speech_db: Option<f64>,
    #[serde(default)]
    pub quiet_db: Option<f64>,
    #[serde(default)]
    pub clipping: bool,
    #[serde(default)]
    pub quiet: bool,
    #[serde(default)]
    pub gated: bool,
    #[serde(default)]
    pub cleaned: bool,
    #[serde(default)]
    pub added_db: f64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMicLevel {
    #[serde(default)]
    pub level: f64,
    #[serde(default)]
    pub gate_level: f64,
    #[serde(default)]
    pub after: f64,
    #[serde(default)]
    pub voiced: bool,
    #[serde(default)]
    pub stages: DiscordVoiceStages,
    #[serde(default)]
    pub heard: Option<DiscordMicHeard>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMicMonitorReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub on: bool,
    #[serde(default)]
    pub on_head: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordNoticeEvent {
    #[serde(default)]
    pub message_id: String,
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub guild_id: Option<String>,
    #[serde(default)]
    pub parent_id: Option<String>,
    #[serde(default)]
    pub author_id: String,
    #[serde(default)]
    pub author: String,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub place: Option<String>,
    #[serde(default)]
    pub server: Option<String>,
    #[serde(default)]
    pub body: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordNotifyClearEvent {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub message_id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordOpenEvent {
    #[serde(default)]
    pub guild_id: Option<String>,
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub parent_id: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordTtsEvent {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub text: String,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordClientSettings {
    #[serde(default)]
    pub input_profile: String,
    #[serde(default = "default_true")]
    pub noise_suppression: bool,
    #[serde(default)]
    pub noise_suppression_kind: Option<String>,
    #[serde(default)]
    pub others_noise_suppression: bool,
    #[serde(default)]
    pub only_my_voice: bool,
    #[serde(default)]
    pub my_voice_learned: Option<bool>,
    #[serde(default)]
    pub my_voice_seconds: Option<f64>,
    #[serde(default = "default_true")]
    pub even_voices: bool,
    #[serde(default = "default_true")]
    pub echo_cancellation: bool,
    #[serde(default = "default_true")]
    pub voice_focus: bool,
    #[serde(default)]
    pub push_to_talk: bool,
    #[serde(default = "default_ptt_delay")]
    pub ptt_delay: i32,
    #[serde(default = "default_true")]
    pub auto_sensitivity: bool,
    #[serde(default = "default_sensitivity")]
    pub sensitivity: f64,
    #[serde(default)]
    pub attenuation: i32,
    #[serde(default)]
    pub attenuate_self: bool,
    #[serde(default)]
    pub attenuate_others: bool,
    #[serde(default)]
    pub keybinds: Vec<DiscordKeybind>,
    #[serde(default = "default_true")]
    pub desktop_notifications: bool,
    #[serde(default = "default_true")]
    pub unread_badge: bool,
    #[serde(default = "default_true")]
    pub taskbar_flash: bool,
    #[serde(default)]
    pub disabled_sounds: Vec<String>,
    #[serde(default)]
    pub disable_all_sounds: bool,
    #[serde(default)]
    pub selected_channel_sound: bool,
    #[serde(default = "default_sound_volume")]
    pub sound_volume: i32,
    #[serde(default)]
    pub go_live_height: Option<i32>,
    #[serde(default)]
    pub go_live_fps: Option<i32>,
    #[serde(default)]
    pub go_live_hevc: Option<bool>,
    #[serde(default)]
    pub capture_mode: Option<String>,
    #[serde(default)]
    pub service_disabled: Option<bool>,
}

fn default_true() -> bool {
    true
}

fn default_ptt_delay() -> i32 {
    200
}

fn default_sensitivity() -> f64 {
    -60.0
}

fn default_sound_volume() -> i32 {
    60
}

impl Default for DiscordClientSettings {
    fn default() -> Self {
        Self {
            input_profile: "exo".into(),
            noise_suppression: true,
            noise_suppression_kind: Some("exo".into()),
            others_noise_suppression: false,
            only_my_voice: false,
            my_voice_learned: None,
            my_voice_seconds: None,
            even_voices: true,
            echo_cancellation: true,
            voice_focus: true,
            push_to_talk: false,
            ptt_delay: 200,
            auto_sensitivity: true,
            sensitivity: -60.0,
            attenuation: 0,
            attenuate_self: false,
            attenuate_others: false,
            keybinds: Vec::new(),
            desktop_notifications: true,
            unread_badge: true,
            taskbar_flash: true,
            disabled_sounds: Vec::new(),
            disable_all_sounds: false,
            selected_channel_sound: false,
            sound_volume: 60,
            go_live_height: Some(1080),
            go_live_fps: Some(60),
            go_live_hevc: Some(true),
            capture_mode: None,
            service_disabled: None,
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAudioUser {
    #[serde(default)]
    pub muted: bool,
    #[serde(default = "default_user_volume")]
    pub volume: f64,
    #[serde(default)]
    pub soundboard_muted: Option<bool>,
}

fn default_user_volume() -> f64 {
    100.0
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordCustomStatus {
    #[serde(default)]
    pub text: String,
    #[serde(default)]
    pub emoji_id: Option<String>,
    #[serde(default)]
    pub emoji_name: Option<String>,
    #[serde(default)]
    pub expires_at_ms: Option<i64>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAccountSettings {
    #[serde(default = "default_true")]
    pub stream_notifications_enabled: bool,
    #[serde(default = "default_render_spoilers")]
    pub render_spoilers: String,
    #[serde(default)]
    pub view_image_descriptions: bool,
    #[serde(default = "default_true")]
    pub inline_attachment_media: bool,
    #[serde(default = "default_true")]
    pub inline_embed_media: bool,
    #[serde(default = "default_true")]
    pub gif_auto_play: bool,
    #[serde(default = "default_true")]
    pub render_embeds: bool,
    #[serde(default = "default_true")]
    pub render_reactions: bool,
    #[serde(default = "default_true")]
    pub animate_emoji: bool,
    #[serde(default)]
    pub animate_stickers: i32,
    #[serde(default = "default_true")]
    pub enable_tts_command: bool,
    #[serde(default)]
    pub message_display_compact: bool,
    #[serde(default = "default_one")]
    pub explicit_content_filter: i32,
    #[serde(default)]
    pub view_nsfw_guilds: bool,
    #[serde(default = "default_true")]
    pub convert_emoticons: bool,
    #[serde(default)]
    pub view_nsfw_commands: bool,
    #[serde(default)]
    pub dm_spam_filter_v2: i32,
    #[serde(default)]
    pub explicit_content_guilds: i32,
    #[serde(default)]
    pub explicit_content_friend_dm: i32,
    #[serde(default)]
    pub explicit_content_non_friend_dm: i32,
    #[serde(default)]
    pub gore_content_guilds: i32,
    #[serde(default)]
    pub gore_content_friend_dm: i32,
    #[serde(default)]
    pub gore_content_non_friend_dm: i32,
    #[serde(default)]
    pub reaction_notifications: i32,
    #[serde(default = "default_true")]
    pub enable_friend_online_notifications: bool,
    #[serde(default = "default_true")]
    pub enable_friend_anniversary_notifications: bool,
    #[serde(default = "default_true")]
    pub enable_profile_updates_notifications: bool,
    #[serde(default = "default_true")]
    pub notify_friends_on_come_online: bool,
    #[serde(default = "default_true")]
    pub notify_server_members_on_go_live: bool,
    #[serde(default = "default_true")]
    pub allow_activity_party_privacy_friends: bool,
    #[serde(default = "default_true")]
    pub allow_activity_party_privacy_voice_channel: bool,
    #[serde(default)]
    pub restricted_guild_ids: Vec<String>,
    #[serde(default)]
    pub default_guilds_restricted: bool,
    #[serde(default = "default_friend_source_flags")]
    pub friend_source_flags: i32,
    #[serde(default)]
    pub default_guilds_activity_restricted: i32,
    #[serde(default)]
    pub message_request_restricted_guild_ids: Vec<String>,
    #[serde(default)]
    pub activity_restricted_guild_ids: Vec<String>,
    #[serde(default)]
    pub activity_joining_restricted_guild_ids: Vec<String>,
    #[serde(default)]
    pub default_message_request_restricted: bool,
    #[serde(default)]
    pub default_guilds_restricted_v2: Option<bool>,
    #[serde(default)]
    pub default_guilds_activity_restricted_v2: i32,
    #[serde(default)]
    pub status_expires_at_ms: Option<i64>,
    #[serde(default)]
    pub profile_visibility: i32,
    #[serde(default)]
    pub hide_friend_request_notes: bool,
    #[serde(default)]
    pub locale: String,
    #[serde(default)]
    pub developer_mode: bool,
    #[serde(default)]
    pub timestamp_hour_cycle: i32,
    #[serde(default)]
    pub audio_users: HashMap<String, DiscordAudioUser>,
    #[serde(default)]
    pub audio_streams: HashMap<String, DiscordAudioUser>,
    #[serde(default)]
    pub custom_status: Option<DiscordCustomStatus>,
}

fn default_render_spoilers() -> String {
    "ON_CLICK".into()
}

fn default_one() -> i32 {
    1
}

fn default_friend_source_flags() -> i32 {
    14
}

impl Default for DiscordAccountSettings {
    fn default() -> Self {
        Self {
            stream_notifications_enabled: true,
            render_spoilers: "ON_CLICK".into(),
            view_image_descriptions: false,
            inline_attachment_media: true,
            inline_embed_media: true,
            gif_auto_play: true,
            render_embeds: true,
            render_reactions: true,
            animate_emoji: true,
            animate_stickers: 0,
            enable_tts_command: true,
            message_display_compact: false,
            explicit_content_filter: 1,
            view_nsfw_guilds: false,
            convert_emoticons: true,
            view_nsfw_commands: false,
            dm_spam_filter_v2: 0,
            explicit_content_guilds: 0,
            explicit_content_friend_dm: 0,
            explicit_content_non_friend_dm: 0,
            gore_content_guilds: 0,
            gore_content_friend_dm: 0,
            gore_content_non_friend_dm: 0,
            reaction_notifications: 0,
            enable_friend_online_notifications: true,
            enable_friend_anniversary_notifications: true,
            enable_profile_updates_notifications: true,
            notify_friends_on_come_online: true,
            notify_server_members_on_go_live: true,
            allow_activity_party_privacy_friends: true,
            allow_activity_party_privacy_voice_channel: true,
            restricted_guild_ids: Vec::new(),
            default_guilds_restricted: false,
            friend_source_flags: 14,
            default_guilds_activity_restricted: 0,
            message_request_restricted_guild_ids: Vec::new(),
            activity_restricted_guild_ids: Vec::new(),
            activity_joining_restricted_guild_ids: Vec::new(),
            default_message_request_restricted: false,
            default_guilds_restricted_v2: None,
            default_guilds_activity_restricted_v2: 0,
            status_expires_at_ms: None,
            profile_visibility: 0,
            hide_friend_request_notes: false,
            locale: String::new(),
            developer_mode: false,
            timestamp_hour_cycle: 0,
            audio_users: HashMap::new(),
            audio_streams: HashMap::new(),
            custom_status: None,
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordUserSettingsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub values: Option<DiscordAccountSettings>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAccount {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub username: Option<String>,
    #[serde(default)]
    pub global_name: Option<String>,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub banner: Option<String>,
    #[serde(default)]
    pub accent_color: Option<u32>,
    #[serde(default)]
    pub bio: String,
    #[serde(default)]
    pub pronouns: String,
    #[serde(default)]
    pub email: Option<String>,
    #[serde(default)]
    pub phone: Option<String>,
    #[serde(default)]
    pub verified: bool,
    #[serde(default)]
    pub mfa: bool,
    #[serde(default)]
    pub nsfw_allowed: bool,
    #[serde(default)]
    pub premium: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordServerTagOption {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub tag: String,
    #[serde(default)]
    pub badge: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordSession {
    #[serde(default)]
    pub id_hash: Option<String>,
    #[serde(default)]
    pub os: Option<String>,
    #[serde(default)]
    pub platform: Option<String>,
    #[serde(default)]
    pub location: Option<String>,
    #[serde(default)]
    pub last_used: Option<String>,
    #[serde(default)]
    pub current: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAuthorizedApp {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub icon: Option<String>,
    #[serde(default)]
    pub scopes: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordConnection {
    #[serde(default)]
    pub r#type: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub verified: bool,
    #[serde(default)]
    pub revoked: bool,
    #[serde(default)]
    pub visible: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAccountReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub me: Option<DiscordAccount>,
    #[serde(default)]
    pub data: Option<Value>,
    #[serde(default)]
    pub mine: Option<String>,
    #[serde(default)]
    pub servers: Vec<DiscordServerTagOption>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordThreadMetadata {
    #[serde(default)]
    pub archive_timestamp: Option<String>,
    #[serde(default)]
    pub create_timestamp: Option<String>,
    #[serde(default)]
    pub locked: bool,
    #[serde(default)]
    pub archived: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordThreadOwner {
    #[serde(default)]
    pub nick: Option<String>,
    #[serde(default)]
    pub user: Option<DiscordUser>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordThread {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub parent_id: String,
    #[serde(default)]
    pub message_count: Option<i32>,
    #[serde(default)]
    pub last_message_id: Option<String>,
    #[serde(default)]
    pub applied_tags: Vec<String>,
    #[serde(default)]
    pub owner: Option<DiscordThreadOwner>,
    #[serde(default)]
    pub thread_metadata: Option<DiscordThreadMetadata>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordForumTag {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub emoji_id: Option<String>,
    #[serde(default)]
    pub emoji_name: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordThreadsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub threads: Vec<DiscordThread>,
    #[serde(default)]
    pub first_messages: Vec<DiscordMessage>,
    #[serde(default)]
    pub has_more: bool,
    #[serde(default)]
    pub tags: Vec<DiscordForumTag>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordThreadCreated {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordThreadCreateReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub thread: Option<DiscordThreadCreated>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordReportElement {
    #[serde(default)]
    pub name: String,
    #[serde(rename = "type", default)]
    pub kind: String,
    #[serde(default)]
    pub data: Value,
    #[serde(default)]
    pub should_submit_data: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordReportButton {
    #[serde(rename = "type", default)]
    pub kind: String,
    #[serde(default)]
    pub target: Option<i64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordReportNode {
    #[serde(default)]
    pub id: i64,
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub header: String,
    #[serde(default)]
    pub subheader: Option<String>,
    #[serde(default)]
    pub info: Option<String>,
    #[serde(default)]
    pub children: Vec<(String, i64)>,
    #[serde(default)]
    pub elements: Vec<DiscordReportElement>,
    #[serde(default)]
    pub button: Option<DiscordReportButton>,
    #[serde(default)]
    pub is_multi_select_required: bool,
    #[serde(default)]
    pub is_auto_submit: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordReportMenu {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub variant: String,
    #[serde(default)]
    pub language: Option<String>,
    #[serde(default)]
    pub root_node_id: i64,
    #[serde(default)]
    pub success_node_id: i64,
    #[serde(default)]
    pub fail_node_id: i64,
    #[serde(default)]
    pub nodes: HashMap<String, DiscordReportNode>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordReportReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub menu: Option<DiscordReportMenu>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordShopPriceEntry {
    #[serde(default)]
    pub amount: i64,
    #[serde(default)]
    pub currency: String,
    #[serde(default)]
    pub exponent: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordShopCountryPrices {
    #[serde(default)]
    pub country_code: String,
    #[serde(default)]
    pub prices: Vec<DiscordShopPriceEntry>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordShopTierPrices {
    #[serde(default)]
    pub country_prices: DiscordShopCountryPrices,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordShopItem {
    #[serde(rename = "type", default)]
    pub kind: i32,
    #[serde(default)]
    pub sku_id: String,
    #[serde(default)]
    pub asset: Option<String>,
    #[serde(default)]
    pub label: Option<String>,
    #[serde(default)]
    pub palette: Option<String>,
    #[serde(rename = "thumbnailPreviewSrc", default)]
    pub thumbnail_preview_src: Option<String>,
    #[serde(rename = "staticFrameSrc", default)]
    pub static_frame_src: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordShopStyles {
    #[serde(default)]
    pub background_colors: Vec<u32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordShopProduct {
    #[serde(default)]
    pub sku_id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub summary: Option<String>,
    #[serde(rename = "type", default)]
    pub kind: i32,
    #[serde(default)]
    pub premium_type: Option<i32>,
    #[serde(default)]
    pub prices: HashMap<String, DiscordShopTierPrices>,
    #[serde(default)]
    pub items: Vec<DiscordShopItem>,
    #[serde(default)]
    pub bundled_products: Vec<DiscordShopProduct>,
    #[serde(default)]
    pub variants: Vec<DiscordShopProduct>,
    #[serde(default)]
    pub purchased_at: Option<String>,
    #[serde(default)]
    pub expires_at: Option<String>,
    #[serde(default)]
    pub styles: Option<DiscordShopStyles>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordShopCategory {
    #[serde(default)]
    pub sku_id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub summary: Option<String>,
    #[serde(default)]
    pub products: Vec<DiscordShopProduct>,
    #[serde(default)]
    pub logo: Option<String>,
    #[serde(default)]
    pub banner: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordShopCatalog {
    #[serde(default)]
    pub categories: Vec<DiscordShopCategory>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordPaymentChoice {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub expires: Option<String>,
    #[serde(default)]
    pub default: bool,
    #[serde(default)]
    pub usable: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordShopPrice {
    #[serde(default)]
    pub amount: Option<i64>,
    #[serde(default)]
    pub subtotal: Option<i64>,
    #[serde(default)]
    pub tax: Option<i64>,
    #[serde(default)]
    pub currency: Option<String>,
    #[serde(default)]
    pub tax_inclusive: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordShopReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub catalog: Option<DiscordShopCatalog>,
    #[serde(default)]
    pub owned: Option<Vec<DiscordShopProduct>>,
    #[serde(default)]
    pub product: Option<DiscordShopProduct>,
    #[serde(default)]
    pub payment: Vec<DiscordPaymentChoice>,
    #[serde(default)]
    pub price: Option<DiscordShopPrice>,
    #[serde(default)]
    pub confirm: bool,
    #[serde(default)]
    pub declined: bool,
    #[serde(default)]
    pub client_secret: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordGuideWelcome {
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordGuideEmoji {
    #[serde(default)]
    pub name: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordGuideAction {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub emoji: Option<DiscordGuideEmoji>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordGuide {
    #[serde(default)]
    pub welcome_message: Option<DiscordGuideWelcome>,
    #[serde(default)]
    pub new_member_actions: Vec<DiscordGuideAction>,
    #[serde(default)]
    pub resource_channels: Vec<DiscordGuideAction>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordResource {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub title: Option<String>,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub icon: Option<String>,
    #[serde(default)]
    pub preview: Option<DiscordMessage>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordEventEntityMeta {
    #[serde(default)]
    pub location: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordEvent {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub channel_id: Option<String>,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub scheduled_start_time: String,
    #[serde(default)]
    pub scheduled_end_time: Option<String>,
    #[serde(default)]
    pub status: i32,
    #[serde(default)]
    pub user_count: Option<i32>,
    #[serde(default)]
    pub entity_metadata: Option<DiscordEventEntityMeta>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordEventEntry {
    #[serde(default)]
    pub event: DiscordEvent,
    #[serde(default)]
    pub interested: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMemberRowInner {
    #[serde(default)]
    pub user: DiscordUser,
    #[serde(default)]
    pub nick: Option<String>,
    #[serde(default)]
    pub roles: Vec<String>,
    #[serde(default)]
    pub joined_at: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMemberRow {
    #[serde(default)]
    pub member: DiscordMemberRowInner,
    #[serde(default)]
    pub join_source_type: Option<i32>,
    #[serde(default)]
    pub source_invite_code: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMemberSearch {
    #[serde(default)]
    pub members: Vec<DiscordMemberRow>,
    #[serde(default)]
    pub total_result_count: Option<i32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordRole {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub color: Option<String>,
    #[serde(default)]
    pub position: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordServerReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub guild_id: Option<String>,
    #[serde(default)]
    pub channel_id: Option<String>,
    #[serde(default)]
    pub guide: Option<DiscordGuide>,
    #[serde(default)]
    pub events: Option<Vec<DiscordEventEntry>>,
    #[serde(default)]
    pub members: Option<DiscordMemberSearch>,
    #[serde(default)]
    pub roles: HashMap<String, DiscordRole>,
    #[serde(default)]
    pub online: Option<i32>,
    #[serde(default)]
    pub total: Option<i32>,
    #[serde(default)]
    pub resources: Vec<DiscordResource>,
    #[serde(default)]
    pub pruned: Option<i32>,
    #[serde(default)]
    pub indexing: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAdminMfa {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub methods: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAdminReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub data: Option<Value>,
    #[serde(default)]
    pub mfa: Option<DiscordAdminMfa>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordFontReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub data: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordProfileUser {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub username: String,
    #[serde(default)]
    pub global_name: Option<String>,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub banner: Option<String>,
    #[serde(default)]
    pub accent_color: Option<u32>,
    #[serde(default)]
    pub bio: Option<String>,
    #[serde(default)]
    pub bot: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordProfileAbout {
    #[serde(default)]
    pub bio: Option<String>,
    #[serde(default)]
    pub pronouns: Option<String>,
    #[serde(default)]
    pub accent_color: Option<u32>,
    #[serde(default)]
    pub theme_colors: Option<Vec<u32>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordProfileBadge {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub icon: String,
    #[serde(default)]
    pub link: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordProfileConnection {
    #[serde(default, rename = "type")]
    pub kind: String,
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub verified: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMutualGuild {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub nick: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordProfileData {
    #[serde(default)]
    pub user: DiscordProfileUser,
    #[serde(default)]
    pub user_profile: Option<DiscordProfileAbout>,
    #[serde(default)]
    pub badges: Vec<DiscordProfileBadge>,
    #[serde(default)]
    pub guild_badges: Vec<DiscordProfileBadge>,
    #[serde(default)]
    pub connected_accounts: Vec<DiscordProfileConnection>,
    #[serde(default)]
    pub mutual_guilds: Vec<DiscordMutualGuild>,
    #[serde(default)]
    pub mutual_friends_count: Option<i32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordProfilePresence {
    #[serde(default)]
    pub activity_type: Option<i32>,
    #[serde(default)]
    pub activity: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordProfileExtras {
    #[serde(default)]
    pub presence: Option<DiscordProfilePresence>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordProfileReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub profile: Option<DiscordProfileData>,
    #[serde(default)]
    pub extras: Option<DiscordProfileExtras>,
    #[serde(default)]
    pub note: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMutualFriendsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub friends: Option<Vec<DiscordProfileUser>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FriendsBadgeReply {
    #[serde(default)]
    pub count: i32,
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
