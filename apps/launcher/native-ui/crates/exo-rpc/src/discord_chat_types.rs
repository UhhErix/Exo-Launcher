use crate::discord_types::{
    DiscordAttachment, DiscordEmbed, DiscordMessage, DiscordReactionEmoji, DiscordUser,
};
use serde::Deserialize;
use serde_json::Value;
use std::collections::HashMap;

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordReactionCountDetails {
    #[serde(default)]
    pub normal: i32,
    #[serde(default)]
    pub burst: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordStickerItem {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub format_type: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordPollMedia {
    #[serde(default)]
    pub text: Option<String>,
    #[serde(default)]
    pub emoji: Option<DiscordReactionEmoji>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordPollAnswer {
    #[serde(default)]
    pub answer_id: i32,
    #[serde(default)]
    pub poll_media: DiscordPollMedia,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordPollAnswerCount {
    #[serde(default)]
    pub id: i32,
    #[serde(default)]
    pub count: i32,
    #[serde(default)]
    pub me_voted: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordPollResults {
    #[serde(default)]
    pub is_finalized: bool,
    #[serde(default)]
    pub answer_counts: Vec<DiscordPollAnswerCount>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordPoll {
    #[serde(default)]
    pub question: DiscordPollMedia,
    #[serde(default)]
    pub answers: Vec<DiscordPollAnswer>,
    #[serde(default)]
    pub expiry: Option<String>,
    #[serde(default)]
    pub allow_multiselect: bool,
    #[serde(default)]
    pub results: Option<DiscordPollResults>,
}

impl DiscordPoll {
    pub fn apply_vote(&mut self, answer_id: i32, add: bool, me: bool) {
        let results = self.results.get_or_insert_with(DiscordPollResults::default);
        if let Some(entry) = results.answer_counts.iter_mut().find(|c| c.id == answer_id) {
            entry.count = (entry.count + if add { 1 } else { -1 }).max(0);
            if me {
                entry.me_voted = add;
            }
        } else if add {
            results.answer_counts.push(DiscordPollAnswerCount {
                id: answer_id,
                count: 1,
                me_voted: me,
            });
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordPollVoteEvent {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub message_id: String,
    #[serde(default)]
    pub answer_id: i32,
    #[serde(default)]
    pub add: bool,
    #[serde(default)]
    pub me: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMessageCall {
    #[serde(default)]
    pub participants: Vec<String>,
    #[serde(default)]
    pub ended_timestamp: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMessageThread {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub message_count: Option<u32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMessageSnapshotInner {
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
    pub flags: u32,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMessageSnapshot {
    #[serde(default)]
    pub message: DiscordMessageSnapshotInner,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordInteractionMeta {
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub user: Option<DiscordUser>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGuildEmoji {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub animated: bool,
    #[serde(default)]
    pub native: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordEmojiGuild {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub icon: Option<String>,
    #[serde(default)]
    pub emojis: Vec<DiscordGuildEmoji>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordEmojisReply {
    #[serde(default)]
    pub premium: bool,
    #[serde(default)]
    pub guilds: Vec<DiscordEmojiGuild>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordEmojiFavoritesReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub favorites: Option<Vec<String>>,
    #[serde(default)]
    pub frequent: Option<Vec<String>>,
    #[serde(default)]
    pub reactions: Option<Vec<String>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordGif {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub title: Option<String>,
    #[serde(default)]
    pub url: String,
    #[serde(default)]
    pub src: String,
    #[serde(default)]
    pub gif_src: Option<String>,
    #[serde(default)]
    pub width: u32,
    #[serde(default)]
    pub height: u32,
    #[serde(default)]
    pub preview: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGifsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub gifs: Option<Vec<DiscordGif>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordFavoriteGif {
    #[serde(default)]
    pub url: String,
    #[serde(default)]
    pub src: String,
    #[serde(default)]
    pub format: i32,
    #[serde(default)]
    pub width: u32,
    #[serde(default)]
    pub height: u32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGifFavoritesReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub gifs: Option<Vec<DiscordFavoriteGif>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordSticker {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub format: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordStickerGroup {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub icon: Option<String>,
    #[serde(default)]
    pub usable: bool,
    #[serde(default)]
    pub stickers: Vec<DiscordSticker>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordStickersReply {
    #[serde(default)]
    pub premium: bool,
    #[serde(default)]
    pub groups: Option<Vec<DiscordStickerGroup>>,
    #[serde(default)]
    pub favorites: Option<Vec<String>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordReactorsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub users: Option<Vec<DiscordUser>>,
    #[serde(default)]
    pub can_remove: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordPlaceRef {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub guild_id: Option<String>,
    #[serde(default)]
    pub guild: Option<String>,
    #[serde(default)]
    pub parent_id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMentionsReply {
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
pub struct DiscordForYouItem {
    #[serde(default)]
    pub id: String,
    #[serde(rename = "type", default)]
    pub kind: String,
    #[serde(default)]
    pub body: Option<String>,
    #[serde(default)]
    pub icon_url: Option<String>,
    #[serde(default)]
    pub deeplink: Option<String>,
    #[serde(default)]
    pub acked: Option<bool>,
    #[serde(default)]
    pub other_user: Option<DiscordUser>,
    #[serde(default)]
    pub guild_id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordForYouReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub items: Option<Vec<DiscordForYouItem>>,
    #[serde(default)]
    pub has_more: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordSaveData {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub message_id: String,
    #[serde(default)]
    pub saved_at: String,
    #[serde(default)]
    pub due_at: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordSavedMessage {
    #[serde(default)]
    pub save_data: DiscordSaveData,
    #[serde(default)]
    pub message: Option<DiscordMessage>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordSavedReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub reminders: Option<Vec<DiscordSavedMessage>>,
    #[serde(default)]
    pub bookmark_ids: Option<Vec<String>>,
    #[serde(default)]
    pub results: Option<Vec<DiscordSavedMessage>>,
    #[serde(default)]
    pub has_more: bool,
    #[serde(default)]
    pub places: HashMap<String, Option<DiscordPlaceRef>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordBadge {
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
pub struct DiscordConnection {
    #[serde(rename = "type", default)]
    pub kind: String,
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub verified: bool,
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
    pub bio: Option<String>,
    #[serde(default)]
    pub accent_color: Option<u32>,
    #[serde(default)]
    pub banner: Option<String>,
    #[serde(default)]
    pub bot: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordProfileBio {
    #[serde(default)]
    pub bio: Option<String>,
    #[serde(default)]
    pub pronouns: Option<String>,
    #[serde(default)]
    pub accent_color: Option<u32>,
    #[serde(default)]
    pub banner: Option<String>,
    #[serde(default)]
    pub theme_colors: Option<Vec<u32>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordProfileGuildMember {
    #[serde(default)]
    pub nick: Option<String>,
    #[serde(default)]
    pub roles: Vec<String>,
    #[serde(default)]
    pub joined_at: Option<String>,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub banner: Option<String>,
    #[serde(default)]
    pub bio: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMutualGuild {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub nick: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordProfile {
    #[serde(default)]
    pub user: DiscordProfileUser,
    #[serde(default)]
    pub user_profile: Option<DiscordProfileBio>,
    #[serde(default)]
    pub guild_member: Option<DiscordProfileGuildMember>,
    #[serde(default)]
    pub guild_member_profile: Option<DiscordProfileBio>,
    #[serde(default)]
    pub badges: Vec<DiscordBadge>,
    #[serde(default)]
    pub guild_badges: Vec<DiscordBadge>,
    #[serde(default)]
    pub connected_accounts: Vec<DiscordConnection>,
    #[serde(default)]
    pub mutual_guilds: Vec<DiscordMutualGuild>,
    #[serde(default)]
    pub mutual_friends_count: Option<u32>,
    #[serde(default)]
    pub legacy_username: Option<String>,
    #[serde(default)]
    pub premium_since: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordProfileRole {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub color: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordProfilePresence {
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub activity: Option<String>,
    #[serde(default)]
    pub activity_type: Option<i32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordProfileVoice {
    #[serde(default)]
    pub guild_id: String,
    #[serde(default)]
    pub guild: String,
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub channel: String,
    #[serde(default)]
    pub count: u32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordProfileExtras {
    #[serde(default)]
    pub roles: Vec<DiscordProfileRole>,
    #[serde(default)]
    pub presence: Option<DiscordProfilePresence>,
    #[serde(default)]
    pub voice: Option<DiscordProfileVoice>,
    #[serde(default)]
    pub relationship: Option<i32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordProfileReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub profile: Option<DiscordProfile>,
    #[serde(default)]
    pub extras: Option<DiscordProfileExtras>,
    #[serde(default)]
    pub note: Option<String>,
    #[serde(default)]
    pub friends: Option<Vec<DiscordUser>>,
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
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordRoleChoice {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub color: Option<String>,
    #[serde(default)]
    pub assignable: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordRolePersonCan {
    #[serde(default)]
    pub roles: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordRolePerson {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub known: bool,
    #[serde(default)]
    pub roles: Vec<String>,
    #[serde(default)]
    pub choices: Vec<DiscordRoleChoice>,
    #[serde(default)]
    pub can: DiscordRolePersonCan,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMentionRole {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub color: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordMentionChannel {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(rename = "type", default)]
    pub ch_type: i32,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub category: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordMentionableReply {
    #[serde(default)]
    pub users: Vec<DiscordUser>,
    #[serde(default)]
    pub roles: Vec<DiscordMentionRole>,
    #[serde(default)]
    pub channels: Vec<DiscordMentionChannel>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordColorsReply {
    #[serde(default)]
    pub colors: HashMap<String, Option<String>>,
    #[serde(default)]
    pub avatars: HashMap<String, String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordInviteCard {
    #[serde(default)]
    pub guild_id: Option<String>,
    #[serde(default)]
    pub channel_id: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub icon: Option<String>,
    #[serde(default)]
    pub verified: bool,
    #[serde(default)]
    pub partnered: bool,
    #[serde(default)]
    pub online: Option<u32>,
    #[serde(default)]
    pub members: Option<u32>,
    #[serde(default)]
    pub joined: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordGiftCard {
    #[serde(default)]
    pub code: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub uses: u32,
    #[serde(default)]
    pub max_uses: u32,
    #[serde(default)]
    pub redeemed: bool,
    #[serde(default)]
    pub expires_at: Option<String>,
    #[serde(default)]
    pub gifter: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordServerCardReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub guild_id: Option<String>,
    #[serde(default)]
    pub channel_id: Option<String>,
    #[serde(default)]
    pub invite: Option<DiscordInviteCard>,
    #[serde(default)]
    pub gift: Option<DiscordGiftCard>,
    #[serde(default)]
    pub invalid: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordChannelInviteReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub url: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordImageReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordFileTextReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub text: Option<String>,
    #[serde(default)]
    pub left: u64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SpotifyLinkReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub uri: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
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
pub struct DiscordAppChoice {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub value: Value,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAppOption {
    #[serde(rename = "type", default)]
    pub kind: i32,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub required: bool,
    #[serde(default)]
    pub choices: Vec<DiscordAppChoice>,
    #[serde(default)]
    pub options: Vec<DiscordAppOption>,
    #[serde(default)]
    pub autocomplete: bool,
    #[serde(default)]
    pub channel_types: Vec<i32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAppCommand {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub application_id: String,
    #[serde(rename = "type", default)]
    pub kind: i32,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub options: Vec<DiscordAppOption>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAppInfo {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub icon: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAppMedia {
    #[serde(default)]
    pub url: String,
    #[serde(default)]
    pub proxy_url: Option<String>,
    #[serde(default)]
    pub width: Option<u32>,
    #[serde(default)]
    pub height: Option<u32>,
    #[serde(default)]
    pub content_type: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAppSelectOption {
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub value: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub emoji: Option<DiscordReactionEmoji>,
    #[serde(default)]
    pub default: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAppGalleryItem {
    #[serde(default)]
    pub media: DiscordAppMedia,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub spoiler: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAppFileRef {
    #[serde(default)]
    pub url: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAppComponent {
    #[serde(rename = "type", default)]
    pub kind: i32,
    #[serde(default)]
    pub id: Option<i64>,
    #[serde(default)]
    pub custom_id: Option<String>,
    #[serde(default)]
    pub style: Option<i32>,
    #[serde(default)]
    pub label: Option<String>,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub emoji: Option<DiscordReactionEmoji>,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub disabled: bool,
    #[serde(default)]
    pub components: Vec<DiscordAppComponent>,
    #[serde(default)]
    pub component: Option<Box<DiscordAppComponent>>,
    #[serde(default)]
    pub accessory: Option<Box<DiscordAppComponent>>,
    #[serde(default)]
    pub options: Vec<DiscordAppSelectOption>,
    #[serde(default)]
    pub placeholder: Option<String>,
    #[serde(default)]
    pub min_values: Option<usize>,
    #[serde(default)]
    pub max_values: Option<usize>,
    #[serde(default)]
    pub channel_types: Vec<i32>,
    #[serde(default)]
    pub content: Option<String>,
    #[serde(default)]
    pub media: Option<DiscordAppMedia>,
    #[serde(default)]
    pub items: Vec<DiscordAppGalleryItem>,
    #[serde(default)]
    pub file: Option<DiscordAppFileRef>,
    #[serde(default)]
    pub spoiler: bool,
    #[serde(default)]
    pub divider: Option<bool>,
    #[serde(default)]
    pub spacing: Option<i32>,
    #[serde(default)]
    pub accent_color: Option<u32>,
    #[serde(default)]
    pub value: Option<String>,
    #[serde(default)]
    pub required: Option<bool>,
    #[serde(default)]
    pub min_length: Option<usize>,
    #[serde(default)]
    pub max_length: Option<usize>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAppModalApp {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub icon: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct DiscordAppModal {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub custom_id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub components: Vec<DiscordAppComponent>,
    #[serde(default)]
    pub application: DiscordAppModalApp,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordAppsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub nonce: Option<String>,
    #[serde(default)]
    pub commands: Vec<DiscordAppCommand>,
    #[serde(default)]
    pub applications: Vec<DiscordAppInfo>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordInteractionEvent {
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub nonce: Option<String>,
    #[serde(default)]
    pub id: Option<String>,
    #[serde(default)]
    pub reason: Option<i32>,
    #[serde(default)]
    pub activity: bool,
    #[serde(default)]
    pub choices: Option<Vec<DiscordAppChoice>>,
    #[serde(default)]
    pub modal: Option<DiscordAppModal>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordReportReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub menu: Option<Value>,
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
pub struct DiscordDmLineEvent {
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub line: Option<String>,
    #[serde(default)]
    pub at: Option<i64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscordNotifyEvent {
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
pub struct FriendsBadgeReply {
    #[serde(default)]
    pub count: u32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OpenUrlReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub lookalike: Option<String>,
}
