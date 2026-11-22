use serde::Deserialize;

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

#[derive(Debug, Clone, Default, Deserialize)]
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

#[derive(Debug, Clone, Default, Deserialize)]
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
}

#[derive(Debug, Clone, Default, Deserialize)]
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

#[derive(Debug, Clone, Default, Deserialize)]
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
}

#[derive(Debug, Clone, Default, Deserialize)]
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

#[derive(Debug, Clone, Default, Deserialize)]
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

#[derive(Debug, Clone, Default, Deserialize)]
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

#[derive(Debug, Clone, Default, Deserialize)]
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
    pub hide_muted: bool,
    #[serde(default)]
    pub events: i32,
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

#[derive(Debug, Clone, Default, Deserialize)]
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

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordPrep {
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub percent: Option<i32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
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

#[derive(Debug, Clone, Default, Deserialize)]
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
}
