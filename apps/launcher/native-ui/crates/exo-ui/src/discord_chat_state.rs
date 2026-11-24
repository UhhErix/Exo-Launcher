use crate::discord_state::DiscordPlace;
use crate::state::{AppState, HostStatus, Room};
use exo_rpc::{
    ActionResult, DiscordAdminReply, DiscordAppChoice, DiscordAppCommand, DiscordAppInfo,
    DiscordAppModal, DiscordAppsReply, DiscordChannelInviteReply, DiscordColorsReply,
    DiscordDmLineEvent, DiscordEmbed, DiscordEmbedAuthor, DiscordEmbedField, DiscordEmbedFooter,
    DiscordEmojiFavoritesReply, DiscordEmojisReply, DiscordFavoriteGif, DiscordFileTextReply,
    DiscordForYouItem, DiscordForYouReply, DiscordFriendActionReply, DiscordGif,
    DiscordGifFavoritesReply, DiscordGifsReply, DiscordGiftCard, DiscordImageReply,
    DiscordInteractionEvent, DiscordInviteCard, DiscordMentionChannel, DiscordMentionRole,
    DiscordMentionableReply, DiscordMentionsReply, DiscordMessage, DiscordMessageEvent,
    DiscordMessagesReply, DiscordNotifyClearEvent, DiscordNotifyEvent, DiscordOpenEvent,
    DiscordPinsReply, DiscordPlaceRef, DiscordPollVoteEvent, DiscordProfile, DiscordProfileExtras,
    DiscordProfileReply, DiscordReaction, DiscordReactionCountDetails, DiscordReactionEmoji,
    DiscordReactionEvent, DiscordReactorsReply, DiscordReportReply, DiscordRolePerson,
    DiscordSavedMessage, DiscordSavedReply, DiscordSearchReply, DiscordSendReply,
    DiscordServerCardReply, DiscordStickerGroup, DiscordStickersReply, DiscordThreadCreateReply,
    DiscordTypingEvent, DiscordUploadReply, DiscordUser, FriendsBadgeReply, METHOD_DISCORD_ACK,
    METHOD_DISCORD_ADMIN, METHOD_DISCORD_APPS, METHOD_DISCORD_CHANNEL, METHOD_DISCORD_COLORS,
    METHOD_DISCORD_DELETE, METHOD_DISCORD_EDIT, METHOD_DISCORD_EMOJI_FAVORITES,
    METHOD_DISCORD_EMOJIS, METHOD_DISCORD_FILE_TEXT, METHOD_DISCORD_FOR_YOU, METHOD_DISCORD_FRIEND,
    METHOD_DISCORD_GIF_FAVORITES, METHOD_DISCORD_GIFS, METHOD_DISCORD_IMAGE,
    METHOD_DISCORD_MARK_READ, METHOD_DISCORD_MENTIONABLE, METHOD_DISCORD_MENTIONS,
    METHOD_DISCORD_MESSAGE_ACTION, METHOD_DISCORD_MESSAGES, METHOD_DISCORD_PINS,
    METHOD_DISCORD_POLL, METHOD_DISCORD_PROFILE, METHOD_DISCORD_REACT, METHOD_DISCORD_REACTORS,
    METHOD_DISCORD_REPORT, METHOD_DISCORD_SAVED, METHOD_DISCORD_SEARCH, METHOD_DISCORD_SEND,
    METHOD_DISCORD_SERVER, METHOD_DISCORD_STICKER, METHOD_DISCORD_STICKERS, METHOD_DISCORD_THREAD,
    METHOD_DISCORD_TYPING, METHOD_DISCORD_UNREADS, METHOD_DISCORD_UPLOAD, METHOD_FRIENDS_BADGE,
    METHOD_SHELL_OPEN_URL, METHOD_SPOTIFY_LINK, OpenUrlReply, SpotifyLinkReply, base64,
};
use rune::TextEdit;
use serde_json::{Value, json};
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
    pub spoiler: bool,
    pub description: String,
    pub is_media: bool,
    pub raw_bytes: Option<Vec<u8>>,
}

#[derive(Debug, Clone)]
pub struct ModifyAttachmentState {
    pub local_id: String,
    pub name: TextEdit,
    pub description: TextEdit,
    pub spoiler: bool,
    pub is_media: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChatDrawer {
    None,
    Pins,
    Search,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ExpressionTab {
    Gif,
    Sticker,
    Emoji,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GifTab {
    Trending,
    Favorites,
}

#[derive(Debug, Clone)]
pub struct ExpressionPickerState {
    pub open_for_composer: bool,
    pub open_for_reaction: Option<String>,
    pub tab: ExpressionTab,
    pub query: TextEdit,
    pub gif_tab: GifTab,
    pub hovered_name: Option<String>,
    pub emojis: Option<DiscordEmojisReply>,
    pub emojis_guild_id: Option<String>,
    pub emojis_loading: bool,
    pub emoji_favorites: Vec<String>,
    pub emoji_frequent: Vec<String>,
    pub emoji_reactions: Vec<String>,
    pub emojis_favorites_loaded: bool,
    pub gifs: Vec<DiscordGif>,
    pub gifs_loading: bool,
    pub gif_favorites: Vec<DiscordFavoriteGif>,
    pub gif_favorites_loaded: bool,
    pub stickers: Vec<DiscordStickerGroup>,
    pub sticker_favorites: Vec<String>,
    pub stickers_guild_id: Option<String>,
    pub stickers_loading: bool,
}

impl Default for ExpressionPickerState {
    fn default() -> Self {
        Self {
            open_for_composer: false,
            open_for_reaction: None,
            tab: ExpressionTab::Emoji,
            query: TextEdit::default(),
            gif_tab: GifTab::Trending,
            hovered_name: None,
            emojis: None,
            emojis_guild_id: None,
            emojis_loading: false,
            emoji_favorites: Vec::new(),
            emoji_frequent: Vec::new(),
            emoji_reactions: Vec::new(),
            emojis_favorites_loaded: false,
            gifs: Vec::new(),
            gifs_loading: false,
            gif_favorites: Vec::new(),
            gif_favorites_loaded: false,
            stickers: Vec::new(),
            sticker_favorites: Vec::new(),
            stickers_guild_id: None,
            stickers_loading: false,
        }
    }
}

#[derive(Debug, Clone)]
pub struct ReactorsDialogState {
    pub channel_id: String,
    pub message_id: String,
    pub selected_key: String,
    pub selected_burst: bool,
    pub users: Vec<DiscordUser>,
    pub can_remove: bool,
    pub loading: bool,
    pub has_more: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct PollCreatorState {
    pub channel_id: String,
    pub question: TextEdit,
    pub answers: Vec<TextEdit>,
    pub hours: u32,
    pub allow_multiselect: bool,
    pub busy: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SearchFilterKey {
    From,
    In,
    Has,
    Mentions,
    Before,
    During,
    After,
    Pinned,
    AuthorType,
}

impl SearchFilterKey {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::From => "from",
            Self::In => "in",
            Self::Has => "has",
            Self::Mentions => "mentions",
            Self::Before => "before",
            Self::During => "during",
            Self::After => "after",
            Self::Pinned => "pinned",
            Self::AuthorType => "authorType",
        }
    }

    pub fn parse(raw: &str) -> Option<Self> {
        match raw.to_ascii_lowercase().as_str() {
            "from" => Some(Self::From),
            "in" => Some(Self::In),
            "has" => Some(Self::Has),
            "mentions" => Some(Self::Mentions),
            "before" => Some(Self::Before),
            "during" | "on" => Some(Self::During),
            "after" => Some(Self::After),
            "pinned" => Some(Self::Pinned),
            "authortype" => Some(Self::AuthorType),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SearchChip {
    pub key: SearchFilterKey,
    pub label: String,
    pub value: String,
    pub negated: bool,
    pub range_ms: Option<(i64, i64)>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SearchSort {
    Newest,
    Oldest,
    Relevance,
}

impl SearchSort {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Newest => "newest",
            Self::Oldest => "oldest",
            Self::Relevance => "relevance",
        }
    }
}

#[derive(Debug, Clone)]
pub struct SearchState {
    pub chips: Vec<SearchChip>,
    pub sort: SearchSort,
    pub offset: usize,
    pub places: HashMap<String, Option<DiscordPlaceRef>>,
    pub error: Option<String>,
    pub show_filters: bool,
}

impl Default for SearchState {
    fn default() -> Self {
        Self {
            chips: Vec::new(),
            sort: SearchSort::Newest,
            offset: 0,
            places: HashMap::new(),
            error: None,
            show_filters: false,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InboxTab {
    ForYou,
    Unreads,
    Mentions,
    Bookmarks,
    Reminders,
}

#[derive(Debug, Clone)]
pub struct InboxState {
    pub tab: InboxTab,
    pub for_you: Vec<DiscordForYouItem>,
    pub for_you_has_more: bool,
    pub for_you_loading: bool,
    pub for_you_error: Option<String>,
    pub mentions: Vec<DiscordMessage>,
    pub mentions_places: HashMap<String, Option<DiscordPlaceRef>>,
    pub mentions_loading: bool,
    pub mentions_error: Option<String>,
    pub mentions_everyone: bool,
    pub mentions_roles: bool,
    pub mentions_this_server: bool,
    pub bookmarks: Vec<DiscordSavedMessage>,
    pub bookmark_ids: HashSet<String>,
    pub reminders: Vec<DiscordSavedMessage>,
    pub saved_places: HashMap<String, Option<DiscordPlaceRef>>,
    pub saved_loading: bool,
    pub saved_error: Option<String>,
    pub unread_previews: HashMap<String, Vec<DiscordMessage>>,
}

impl Default for InboxState {
    fn default() -> Self {
        Self {
            tab: InboxTab::ForYou,
            for_you: Vec::new(),
            for_you_has_more: false,
            for_you_loading: false,
            for_you_error: None,
            mentions: Vec::new(),
            mentions_places: HashMap::new(),
            mentions_loading: false,
            mentions_error: None,
            mentions_everyone: true,
            mentions_roles: true,
            mentions_this_server: false,
            bookmarks: Vec::new(),
            bookmark_ids: HashSet::new(),
            reminders: Vec::new(),
            saved_places: HashMap::new(),
            saved_loading: false,
            saved_error: None,
            unread_previews: HashMap::new(),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProfileTab {
    About,
    Friends,
    Servers,
}

#[derive(Debug, Clone)]
pub struct ProfileDialogState {
    pub user_id: String,
    pub guild_id: Option<String>,
    pub full: bool,
    pub tab: ProfileTab,
    pub fallback_name: String,
    pub fallback_username: String,
    pub fallback_avatar: Option<String>,
    pub profile: Option<DiscordProfile>,
    pub extras: Option<DiscordProfileExtras>,
    pub note: TextEdit,
    pub saved_note: String,
    pub note_error: Option<String>,
    pub mutual_friends: Option<Vec<DiscordUser>>,
    pub role_person: Option<DiscordRolePerson>,
    pub role_picker_open: bool,
    pub role_query: TextEdit,
    pub role_busy: bool,
    pub role_error: Option<String>,
    pub loading: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct CreateDmState {
    pub query: TextEdit,
    pub picked: Vec<String>,
    pub busy: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct InviteDialogState {
    pub guild_name: String,
    pub channel_id: String,
    pub channel_name: Option<String>,
    pub url: Option<String>,
    pub max_age: u32,
    pub max_uses: u32,
    pub temporary: bool,
    pub editing: bool,
    pub query: TextEdit,
    pub sent_to: HashSet<String>,
    pub busy: bool,
    pub copied: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct ForwardDialogState {
    pub source_channel_id: String,
    pub source_message_id: String,
    pub query: TextEdit,
    pub picked: Vec<String>,
    pub note: TextEdit,
    pub busy: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct NewThreadDialogState {
    pub channel_id: String,
    pub message_id: String,
    pub name: TextEdit,
    pub busy: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct ReportDialogState {
    pub channel_id: String,
    pub message_id: String,
    pub author_name: String,
    pub preview: String,
    pub selected_reason: String,
    pub busy: bool,
    pub done: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Default)]
pub struct MentionAutocompleteState {
    pub users: Vec<DiscordUser>,
    pub roles: Vec<DiscordMentionRole>,
    pub channels: Vec<DiscordMentionChannel>,
    pub last_query: String,
    pub selected: usize,
    pub dismissed_token: Option<String>,
    pub mentions_map: HashMap<String, String>,
}

#[derive(Debug, Clone)]
pub struct SlashCommandEntry {
    pub command: DiscordAppCommand,
    pub path: Vec<String>,
    pub options: Vec<exo_rpc::DiscordAppOption>,
    pub description: String,
    pub app: Option<DiscordAppInfo>,
}

#[derive(Debug, Clone)]
pub struct ActiveSlashCommand {
    pub entry: SlashCommandEntry,
    pub used_options: Vec<String>,
    pub values: HashMap<String, TextEdit>,
    pub bool_values: HashMap<String, bool>,
    pub focused_option: Option<String>,
    pub autocomplete_choices: Vec<DiscordAppChoice>,
    pub busy: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct ActiveAppModal {
    pub modal: DiscordAppModal,
    pub text_values: HashMap<String, TextEdit>,
    pub choice_values: HashMap<String, Vec<String>>,
    pub bool_values: HashMap<String, bool>,
    pub busy: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Default)]
pub struct AppsState {
    pub commands_by_channel: HashMap<String, (Vec<DiscordAppCommand>, Vec<DiscordAppInfo>)>,
    pub loading_channel: Option<String>,
    pub active_command: Option<ActiveSlashCommand>,
    pub active_modal: Option<ActiveAppModal>,
    pub component_busy: Option<String>,
    pub component_error: Option<String>,
    pub app_status: Option<String>,
    pub pending_nonces: HashMap<String, String>,
    pub open_select_id: Option<String>,
}

#[derive(Debug, Clone)]
pub enum InviteCardEntry {
    Loading,
    Loaded(DiscordInviteCard),
    Invalid,
}

#[derive(Debug, Clone)]
pub enum GiftCardEntry {
    Loading,
    Loaded(DiscordGiftCard),
    Invalid,
}

#[derive(Debug, Clone)]
pub enum FileTextEntry {
    Loading,
    Loaded {
        text: String,
        left: u64,
        expanded: bool,
    },
    Failed,
}

#[derive(Debug, Clone)]
pub struct ImageMenuState {
    pub original_url: String,
    pub x: f32,
    pub y: f32,
}

#[derive(Debug, Clone)]
pub struct LightboxSlide {
    pub src: String,
    pub original_url: String,
    pub caption: String,
    pub alt: String,
}

#[derive(Debug, Clone, Copy)]
pub struct ProfileOpenRequest<'a> {
    pub user_id: &'a str,
    pub guild_id: Option<&'a str>,
    pub fallback_name: &'a str,
    pub fallback_username: &'a str,
    pub fallback_avatar: Option<&'a str>,
    pub full: bool,
    pub tab: ProfileTab,
}

#[derive(Debug, Clone)]
pub struct ComponentPress<'a> {
    pub channel_id: &'a str,
    pub message_id: &'a str,
    pub application_id: &'a str,
    pub message_flags: u32,
    pub component_type: i32,
    pub custom_id: &'a str,
    pub values: Option<Vec<String>>,
}

#[derive(Debug, Clone, Default)]
pub struct LinkCardsState {
    pub invites: HashMap<String, InviteCardEntry>,
    pub gifts: HashMap<String, GiftCardEntry>,
    pub jams: HashMap<String, bool>,
    pub file_texts: HashMap<String, FileTextEntry>,
    pub revealed_media: HashSet<String>,
    pub alt_shown: HashSet<String>,
    pub image_menu: Option<ImageMenuState>,
    pub lightbox: rune::LightboxState,
    pub lightbox_slides: Vec<LightboxSlide>,
    pub guild_colors: HashMap<String, HashMap<String, String>>,
    pub guild_colors_requested: HashSet<String>,
}

pub struct DiscordChatState {
    pub messages_by_channel: HashMap<String, Vec<DiscordMessage>>,
    pub loading_channels: HashSet<String>,
    pub has_more_before: HashMap<String, bool>,
    pub composer: TextEdit,
    pub drafts: HashMap<String, String>,
    pub reply_to: Option<(String, String)>,
    pub reply_ping: bool,
    pub editing: Option<(String, TextEdit)>,
    pub pending_uploads: Vec<PendingAttachment>,
    pub modifying_attachment: Option<ModifyAttachmentState>,
    pub drop_hover: bool,
    pub plus_menu_open: bool,
    pub emoji_picker_for: Option<String>,
    pub composer_emoji_open: bool,
    pub picker: ExpressionPickerState,
    pub reactors_dialog: Option<ReactorsDialogState>,
    pub poll_creator: Option<PollCreatorState>,
    pub poll_picks: HashMap<String, HashSet<i32>>,
    pub drawer: ChatDrawer,
    pub pins: Vec<DiscordMessage>,
    pub pins_loading: bool,
    pub search_input: TextEdit,
    pub search_results: Vec<DiscordMessage>,
    pub search_total: usize,
    pub search_loading: bool,
    pub search: SearchState,
    pub inbox: InboxState,
    pub profile_dialog: Option<ProfileDialogState>,
    pub dm_profile_cache: HashMap<String, DiscordProfileReply>,
    pub create_dm: Option<CreateDmState>,
    pub invite_dialog: Option<InviteDialogState>,
    pub forward_dialog: Option<ForwardDialogState>,
    pub new_thread_dialog: Option<NewThreadDialogState>,
    pub report_dialog: Option<ReportDialogState>,
    pub autocomplete: MentionAutocompleteState,
    pub apps: AppsState,
    pub links: LinkCardsState,
    pub char_limit: usize,
    pub too_long_prompt: bool,
    pub highlighted_message_id: Option<String>,
    pub friends_badge_count: u32,
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
            drafts: HashMap::new(),
            reply_to: None,
            reply_ping: true,
            editing: None,
            pending_uploads: Vec::new(),
            modifying_attachment: None,
            drop_hover: false,
            plus_menu_open: false,
            emoji_picker_for: None,
            composer_emoji_open: false,
            picker: ExpressionPickerState::default(),
            reactors_dialog: None,
            poll_creator: None,
            poll_picks: HashMap::new(),
            drawer: ChatDrawer::None,
            pins: Vec::new(),
            pins_loading: false,
            search_input: TextEdit::default(),
            search_results: Vec::new(),
            search_total: 0,
            search_loading: false,
            search: SearchState::default(),
            inbox: InboxState::default(),
            profile_dialog: None,
            dm_profile_cache: HashMap::new(),
            create_dm: None,
            invite_dialog: None,
            forward_dialog: None,
            new_thread_dialog: None,
            report_dialog: None,
            autocomplete: MentionAutocompleteState::default(),
            apps: AppsState::default(),
            links: LinkCardsState::default(),
            char_limit: 2000,
            too_long_prompt: false,
            highlighted_message_id: None,
            friends_badge_count: 0,
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
                        animated: false,
                    },
                    count_details: Some(DiscordReactionCountDetails { normal: 4, burst: 0 }),
                    me_burst: false,
                    burst_colors: Vec::new(),
                },
                DiscordReaction {
                    count: 2,
                    me: false,
                    emoji: DiscordReactionEmoji {
                        id: None,
                        name: Some("🚀".into()),
                        animated: false,
                    },
                    count_details: Some(DiscordReactionCountDetails { normal: 2, burst: 0 }),
                    me_burst: false,
                    burst_colors: Vec::new(),
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
                            animated: false,
                        },
                        count_details: Some(DiscordReactionCountDetails { normal: 3, burst: 0 }),
                        me_burst: false,
                        burst_colors: Vec::new(),
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
                                name: "Frame CPU".into(),
                                value: "0.42 ms".into(),
                                inline: true,
                            },
                            DiscordEmbedField {
                                name: "Memory".into(),
                                value: "68 MB RSS".into(),
                                inline: true,
                            },
                            DiscordEmbedField {
                                name: "Contract".into(),
                                value: "host-v1 · 100% checked".into(),
                                inline: true,
                            },
                        ],
                        footer: Some(DiscordEmbedFooter {
                            text: Some("wgpu DX12 · Goeist Variable".into()),
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
                    content: "Down for ranked once your build finishes?".into(),
                    timestamp: "2026-11-22T18:02:00.000Z".into(),
                    ..Default::default()
                },
                DiscordMessage {
                    id: "1309000000000001002".into(),
                    channel_id: "dm-ada".into(),
                    author: erix,
                    content: "Yep — 2 minutes, just checking the chat composer & file drop.".into(),
                    timestamp: "2026-11-22T18:03:15.000Z".into(),
                    ..Default::default()
                },
            ],
        );
        c.pins = vec![first_general];
        c
    }

    pub fn typing_label(&self, channel_id: &str) -> Option<String> {
        let (name, at) = self.typing_by_channel.get(channel_id)?;
        if at.elapsed() > Duration::from_secs(9) {
            return None;
        }
        Some(format!("{name} is typing…"))
    }
}

pub fn id_at_ms(ms: i64) -> String {
    let offset = (ms - 1_420_070_400_000).max(0) as u64;
    (offset << 22).to_string()
}

pub fn id_before(id: &str) -> String {
    id.parse::<u64>()
        .map(|n| n.saturating_sub(1).to_string())
        .unwrap_or_else(|_| id.to_string())
}

pub fn parse_date_range_ms(raw: &str, now_ms: i64) -> Option<(i64, i64)> {
    let t = raw.trim().to_ascii_lowercase();
    let day_ms: i64 = 86_400_000;
    let today_start = now_ms - now_ms.rem_euclid(day_ms);
    match t.as_str() {
        "today" => return Some((today_start, today_start + day_ms)),
        "yesterday" => return Some((today_start - day_ms, today_start)),
        "this week" => return Some((today_start - 6 * day_ms, today_start + day_ms)),
        "this month" => return Some((today_start - 30 * day_ms, today_start + day_ms)),
        "this year" => return Some((today_start - 365 * day_ms, today_start + day_ms)),
        _ => {}
    }
    let parts: Vec<&str> = t.split('-').collect();
    match parts.len() {
        1 if parts[0].len() == 4 => {
            let y: i32 = parts[0].parse().ok()?;
            if y < 2015 {
                return None;
            }
            Some((civil_ymd_ms(y, 1, 1)?, civil_ymd_ms(y + 1, 1, 1)?))
        }
        2 => {
            let y: i32 = parts[0].parse().ok()?;
            let m: u32 = parts[1].parse().ok()?;
            if !(1..=12).contains(&m) {
                return None;
            }
            let (ny, nm) = if m == 12 { (y + 1, 1) } else { (y, m + 1) };
            Some((civil_ymd_ms(y, m, 1)?, civil_ymd_ms(ny, nm, 1)?))
        }
        3 => {
            let y: i32 = parts[0].parse().ok()?;
            let m: u32 = parts[1].parse().ok()?;
            let d: u32 = parts[2].parse().ok()?;
            let start = civil_ymd_ms(y, m, d)?;
            Some((start, start + day_ms))
        }
        _ => None,
    }
}

fn civil_ymd_ms(year: i32, month: u32, day: u32) -> Option<i64> {
    if !(1..=12).contains(&month) || !(1..=31).contains(&day) {
        return None;
    }
    let y = i64::from(year) - if month <= 2 { 1 } else { 0 };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = (y - era * 400) as u64;
    let m = u64::from(month);
    let d = u64::from(day);
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    let days = era * 146_097 + (doe as i64) - 719_468;
    Some(days * 86_400_000)
}

pub fn build_search_filters_json(
    chips: &[SearchChip],
    raw_text: &str,
    sort: SearchSort,
    offset: usize,
) -> Value {
    let mut author_ids: Vec<String> = Vec::new();
    let mut mentions: Vec<String> = Vec::new();
    let mut has: Vec<String> = Vec::new();
    let mut channel_ids: Vec<String> = Vec::new();
    let mut author_types: Vec<String> = Vec::new();
    let mut pinned: Option<bool> = None;
    let mut min_id: Option<String> = None;
    let mut max_id: Option<String> = None;

    for chip in chips {
        match chip.key {
            SearchFilterKey::From => author_ids.push(chip.value.clone()),
            SearchFilterKey::Mentions => mentions.push(chip.value.clone()),
            SearchFilterKey::In => channel_ids.push(chip.value.clone()),
            SearchFilterKey::Has => {
                let prefix = if chip.negated { "-" } else { "" };
                has.push(format!("{prefix}{}", chip.value));
            }
            SearchFilterKey::AuthorType => {
                let prefix = if chip.negated { "-" } else { "" };
                author_types.push(format!("{prefix}{}", chip.value));
            }
            SearchFilterKey::Pinned => pinned = Some(chip.value == "true"),
            SearchFilterKey::Before => {
                if let Some((start, _)) = chip.range_ms {
                    max_id = Some(id_at_ms(start));
                }
            }
            SearchFilterKey::After => {
                if let Some((_, end)) = chip.range_ms {
                    min_id = Some(id_at_ms(end));
                }
            }
            SearchFilterKey::During => {
                if let Some((start, end)) = chip.range_ms {
                    min_id = Some(id_at_ms(start));
                    max_id = Some(id_at_ms(end));
                }
            }
        }
    }

    let mut obj = serde_json::Map::new();
    let content = raw_text.trim();
    if !content.is_empty() {
        obj.insert("content".into(), json!(content));
    }
    if !author_ids.is_empty() {
        obj.insert("authorIds".into(), json!(author_ids));
    }
    if !mentions.is_empty() {
        obj.insert("mentions".into(), json!(mentions));
    }
    if !has.is_empty() {
        obj.insert("has".into(), json!(has));
    }
    if !channel_ids.is_empty() {
        obj.insert("channelIds".into(), json!(channel_ids));
    }
    if !author_types.is_empty() {
        obj.insert("authorTypes".into(), json!(author_types));
    }
    if let Some(p) = pinned {
        obj.insert("pinned".into(), json!(p));
    }
    if let Some(min) = min_id {
        obj.insert("minId".into(), json!(min));
    }
    if let Some(max) = max_id {
        obj.insert("maxId".into(), json!(max));
    }
    obj.insert("sort".into(), json!(sort.as_str()));
    if offset > 0 {
        obj.insert("offset".into(), json!(offset));
    }
    Value::Object(obj)
}

pub fn extract_inline_search_filters(text: &str, now_ms: i64) -> (Vec<SearchChip>, String) {
    let mut chips = Vec::new();
    let mut rest_words = Vec::new();
    for word in text.split_whitespace() {
        let (negated, bare) = match word.strip_prefix('-') {
            Some(b) => (true, b),
            None => (false, word),
        };
        if let Some((k, v)) = bare.split_once(':')
            && let Some(key) = SearchFilterKey::parse(k)
            && !v.is_empty()
        {
            let vl = v.to_ascii_lowercase();
            match key {
                SearchFilterKey::Has => {
                    let mapped = match vl.as_str() {
                        "forwarded" => Some("snapshot"),
                        "link" | "embed" | "poll" | "file" | "video" | "image" | "sound"
                        | "sticker" => Some(vl.as_str()),
                        _ => None,
                    };
                    if let Some(val) = mapped {
                        chips.push(SearchChip {
                            key,
                            label: vl.clone(),
                            value: val.to_string(),
                            negated,
                            range_ms: None,
                        });
                        continue;
                    }
                }
                SearchFilterKey::AuthorType
                    if matches!(vl.as_str(), "user" | "bot" | "webhook") =>
                {
                    chips.push(SearchChip {
                        key,
                        label: vl.clone(),
                        value: vl,
                        negated,
                        range_ms: None,
                    });
                    continue;
                }
                SearchFilterKey::Pinned if matches!(vl.as_str(), "true" | "false") => {
                    chips.push(SearchChip {
                        key,
                        label: vl.clone(),
                        value: vl,
                        negated: false,
                        range_ms: None,
                    });
                    continue;
                }
                SearchFilterKey::Before | SearchFilterKey::During | SearchFilterKey::After => {
                    if let Some(range) = parse_date_range_ms(&vl, now_ms) {
                        chips.push(SearchChip {
                            key,
                            label: vl.clone(),
                            value: vl,
                            negated: false,
                            range_ms: Some(range),
                        });
                        continue;
                    }
                }
                _ => {}
            }
        }
        rest_words.push(word);
    }
    (chips, rest_words.join(" "))
}

pub fn extract_invite_codes(content: &str) -> Vec<String> {
    let mut out = Vec::new();
    for word in content.split_whitespace() {
        let trimmed = word.trim_matches(|c: char| "<>()[].,!?;:\"'".contains(c));
        let rest = trimmed
            .strip_prefix("https://discord.gg/")
            .or_else(|| trimmed.strip_prefix("http://discord.gg/"))
            .or_else(|| trimmed.strip_prefix("https://discord.com/invite/"))
            .or_else(|| trimmed.strip_prefix("https://discordapp.com/invite/"));
        if let Some(r) = rest {
            let code = r.split(['?', '#', '/']).next().unwrap_or("");
            if !code.is_empty()
                && code.len() <= 64
                && code.chars().all(|c| c.is_ascii_alphanumeric() || c == '-')
                && !out.iter().any(|x| x == code)
            {
                out.push(code.to_string());
            }
        }
    }
    out
}

pub fn extract_gift_codes(content: &str) -> Vec<String> {
    let mut out = Vec::new();
    for word in content.split_whitespace() {
        let trimmed = word.trim_matches(|c: char| "<>()[].,!?;:\"'".contains(c));
        let rest = trimmed
            .strip_prefix("https://discord.gift/")
            .or_else(|| trimmed.strip_prefix("https://discord.com/gifts/"));
        if let Some(r) = rest {
            let code = r.split(['?', '#', '/']).next().unwrap_or("");
            if !code.is_empty()
                && code.len() <= 64
                && code.chars().all(|c| c.is_ascii_alphanumeric() || c == '-')
                && !out.iter().any(|x| x == code)
            {
                out.push(code.to_string());
            }
        }
    }
    out
}

pub fn extract_spotify_jam_url(content: &str) -> Option<String> {
    for word in content.split_whitespace() {
        let trimmed = word.trim_matches(|c: char| "<>()[].,!?;:\"'".contains(c));
        let lower = trimmed.to_ascii_lowercase();
        if lower.starts_with("https://open.spotify.com/socialsession/")
            || lower.starts_with("https://spotify.link/")
            || lower.starts_with("https://spotify.app.link/")
        {
            return Some(trimmed.to_string());
        }
    }
    None
}

pub fn is_text_preview_filename(filename: &str, content_type: Option<&str>) -> bool {
    if let Some(ct) = content_type
        && ct.starts_with("text/")
    {
        return true;
    }
    let ext = filename
        .rsplit('.')
        .next()
        .unwrap_or("")
        .to_ascii_lowercase();
    matches!(
        ext.as_str(),
        "txt"
            | "text"
            | "md"
            | "markdown"
            | "csv"
            | "json"
            | "xml"
            | "html"
            | "css"
            | "js"
            | "jsx"
            | "ts"
            | "tsx"
            | "py"
            | "rb"
            | "rs"
            | "go"
            | "java"
            | "kt"
            | "c"
            | "h"
            | "cpp"
            | "hpp"
            | "cs"
            | "sh"
            | "ps1"
            | "bat"
            | "yml"
            | "yaml"
            | "toml"
            | "ini"
            | "cfg"
            | "conf"
            | "lua"
            | "sql"
            | "diff"
            | "patch"
            | "log"
    )
}

pub fn active_mention_token(text: &str) -> Option<(usize, char, &str)> {
    let trimmed_end = text;
    let start = trimmed_end
        .rfind(|c: char| c.is_whitespace())
        .map(|i| i + 1)
        .unwrap_or(0);
    let token = &trimmed_end[start..];
    let mut chars = token.chars();
    let sigil = chars.next()?;
    let rest = chars.as_str();
    match sigil {
        '@' | '#'
            if rest.len() <= 32
                && rest
                    .chars()
                    .all(|c| c.is_alphanumeric() || "._+-".contains(c)) =>
        {
            Some((start, sigil, rest))
        }
        ':' if rest.len() >= 2
            && rest.len() <= 32
            && rest
                .chars()
                .all(|c| c.is_alphanumeric() || "._+-".contains(c)) =>
        {
            Some((start, sigil, rest))
        }
        _ => None,
    }
}

pub fn flatten_slash_commands(
    commands: &[DiscordAppCommand],
    apps: &[DiscordAppInfo],
) -> Vec<SlashCommandEntry> {
    let mut out = Vec::new();
    for cmd in commands {
        if cmd.kind != 1 {
            continue;
        }
        let app = apps.iter().find(|a| a.id == cmd.application_id).cloned();
        walk_command_options(
            cmd,
            &cmd.options,
            Vec::new(),
            cmd.description.clone().unwrap_or_default(),
            app,
            &mut out,
        );
    }
    out
}

fn walk_command_options(
    cmd: &DiscordAppCommand,
    options: &[exo_rpc::DiscordAppOption],
    path: Vec<String>,
    description: String,
    app: Option<DiscordAppInfo>,
    out: &mut Vec<SlashCommandEntry>,
) {
    let subs: Vec<&exo_rpc::DiscordAppOption> = options
        .iter()
        .filter(|o| o.kind == 1 || o.kind == 2)
        .collect();
    if subs.is_empty() {
        out.push(SlashCommandEntry {
            command: cmd.clone(),
            path,
            options: options.to_vec(),
            description,
            app,
        });
        return;
    }
    for sub in subs {
        let mut next_path = path.clone();
        next_path.push(sub.name.clone());
        let desc = sub
            .description
            .clone()
            .filter(|d| !d.is_empty())
            .unwrap_or_else(|| description.clone());
        walk_command_options(cmd, &sub.options, next_path, desc, app.clone(), out);
    }
}

pub fn expand_mentions(content: &str, mentions_map: &HashMap<String, String>) -> String {
    let mut out = content.to_string();
    let mut pairs: Vec<(&String, &String)> = mentions_map.iter().collect();
    pairs.sort_by_key(|p| std::cmp::Reverse(p.0.len()));
    for (shown, raw) in pairs {
        if !shown.is_empty() {
            out = out.replace(shown.as_str(), raw.as_str());
        }
    }
    out
}

pub fn discord_deeplink_target(deeplink: &str) -> Option<(Option<String>, String, Option<String>)> {
    let idx = deeplink.find("/channels/")?;
    let rest = &deeplink[idx + "/channels/".len()..];
    let parts: Vec<&str> = rest.split('/').filter(|p| !p.is_empty()).collect();
    if parts.len() < 2 {
        return None;
    }
    let guild_id = if parts[0] == "@me" {
        None
    } else {
        Some(parts[0].to_string())
    };
    let channel_id = parts[1].to_string();
    let message_id = parts.get(2).map(|s| (*s).to_string());
    Some((guild_id, channel_id, message_id))
}

impl AppState {
    pub fn open_discord_lightbox(&mut self, slides: Vec<LightboxSlide>, index: usize) {
        if slides.is_empty() {
            return;
        }
        let clamped = index.min(slides.len() - 1);
        self.discord.chat.links.image_menu = None;
        self.discord.chat.links.lightbox_slides = slides;
        self.discord.chat.links.lightbox.open(clamped);
    }

    pub fn open_channel_image_lightbox(
        &mut self,
        channel_id: &str,
        clicked_src: &str,
        clicked_orig: &str,
        clicked_caption: &str,
        clicked_alt: &str,
    ) {
        let mut slides = Vec::new();
        let mut start = None;
        if let Some(msgs) = self.discord.chat.messages_by_channel.get(channel_id) {
            for msg in msgs {
                for att in &msg.attachments {
                    if !att.is_image() {
                        continue;
                    }
                    let src = att
                        .proxy_url
                        .clone()
                        .filter(|s| !s.is_empty())
                        .unwrap_or_else(|| att.url.clone());
                    let orig = if att.url.is_empty() {
                        src.clone()
                    } else {
                        att.url.clone()
                    };
                    if start.is_none() && (src == clicked_src || orig == clicked_orig) {
                        start = Some(slides.len());
                    }
                    slides.push(LightboxSlide {
                        src,
                        original_url: orig,
                        caption: att.filename.clone(),
                        alt: att.description.clone().unwrap_or_default(),
                    });
                }
                for emb in &msg.embeds {
                    if let Some(ref im) = emb.image {
                        let src = im
                            .proxy_url
                            .clone()
                            .or_else(|| im.url.clone())
                            .unwrap_or_default();
                        if src.is_empty() {
                            continue;
                        }
                        let orig = im.url.clone().unwrap_or_else(|| src.clone());
                        if start.is_none() && (src == clicked_src || orig == clicked_orig) {
                            start = Some(slides.len());
                        }
                        slides.push(LightboxSlide {
                            src,
                            original_url: orig,
                            caption: emb.title.clone().unwrap_or_default(),
                            alt: emb.title.clone().unwrap_or_default(),
                        });
                    }
                }
            }
        }
        if start.is_none() {
            start = Some(slides.len());
            slides.push(LightboxSlide {
                src: clicked_src.to_string(),
                original_url: clicked_orig.to_string(),
                caption: clicked_caption.to_string(),
                alt: clicked_alt.to_string(),
            });
        }
        self.open_discord_lightbox(slides, start.unwrap_or(0));
    }

    pub fn close_discord_lightbox(&mut self) {
        self.discord.chat.links.lightbox.close();
        self.discord.chat.links.lightbox_slides.clear();
    }

    pub fn dismiss_discord_chat_layer(&mut self) -> bool {
        if self.discord.chat.links.image_menu.is_some() {
            self.discord.chat.links.image_menu = None;
            return true;
        }
        if self.discord.chat.links.lightbox.is_open() {
            if self.discord.chat.links.lightbox.handle_escape() {
                self.discord.chat.links.lightbox_slides.clear();
            }
            return true;
        }
        if self.discord.chat.apps.active_modal.is_some() {
            self.discord.chat.apps.active_modal = None;
            return true;
        }
        if self.discord.chat.modifying_attachment.is_some() {
            self.discord.chat.modifying_attachment = None;
            return true;
        }
        if self.discord.chat.report_dialog.is_some() {
            self.discord.chat.report_dialog = None;
            return true;
        }
        if self.discord.chat.new_thread_dialog.is_some() {
            self.discord.chat.new_thread_dialog = None;
            return true;
        }
        if self.discord.chat.forward_dialog.is_some() {
            self.discord.chat.forward_dialog = None;
            return true;
        }
        if self.discord.chat.invite_dialog.is_some() {
            self.discord.chat.invite_dialog = None;
            return true;
        }
        if self.discord.chat.create_dm.is_some() {
            self.discord.chat.create_dm = None;
            return true;
        }
        if self.discord.chat.poll_creator.is_some() {
            self.discord.chat.poll_creator = None;
            return true;
        }
        if self.discord.chat.reactors_dialog.is_some() {
            self.discord.chat.reactors_dialog = None;
            return true;
        }
        if self.discord.chat.profile_dialog.is_some() {
            self.discord.chat.profile_dialog = None;
            return true;
        }
        if self.discord.chat.picker.open_for_composer
            || self.discord.chat.picker.open_for_reaction.is_some()
        {
            self.discord.chat.picker.open_for_composer = false;
            self.discord.chat.composer_emoji_open = false;
            self.discord.chat.picker.open_for_reaction = None;
            self.discord.chat.emoji_picker_for = None;
            return true;
        }
        if self.discord.chat.plus_menu_open {
            self.discord.chat.plus_menu_open = false;
            return true;
        }
        if self.discord.chat.apps.active_command.is_some() {
            self.discord.chat.apps.active_command = None;
            return true;
        }
        if self.discord.chat.editing.is_some() {
            self.discord.chat.editing = None;
            return true;
        }
        if self.discord.chat.reply_to.is_some() {
            self.discord.chat.reply_to = None;
            return true;
        }
        false
    }

    pub fn active_chat_channel_id(&self) -> Option<String> {
        match self.discord.place {
            DiscordPlace::Messages => self.discord.active_dm_id.clone(),
            DiscordPlace::Servers => self.discord.active_channel_id.clone(),
            DiscordPlace::Home => None,
        }
    }

    pub fn switch_composer_draft(
        &mut self,
        prev_channel: Option<&str>,
        next_channel: Option<&str>,
    ) {
        if let Some(prev) = prev_channel {
            if self.discord.chat.composer.text.trim().is_empty() {
                self.discord.chat.drafts.remove(prev);
            } else {
                self.discord
                    .chat
                    .drafts
                    .insert(prev.to_string(), self.discord.chat.composer.text.clone());
            }
        }
        let next_text = next_channel
            .and_then(|c| self.discord.chat.drafts.get(c))
            .cloned()
            .unwrap_or_default();
        self.discord.chat.composer = TextEdit::default();
        self.discord.chat.composer.text = next_text;
        self.discord.chat.reply_to = None;
        self.discord.chat.too_long_prompt = false;
        self.discord.chat.autocomplete.dismissed_token = None;
        self.discord.chat.apps.active_command = None;
    }

    pub fn fetch_friends_badge(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_FRIENDS_BADGE,
            json!({}),
            |s, res: Result<FriendsBadgeReply, String>| {
                if let Ok(rep) = res {
                    s.discord.chat.friends_badge_count = rep.count;
                }
            },
        );
    }

    pub fn open_external_url(&mut self, url: &str) {
        if self.status != HostStatus::Connected {
            self.notice = Some(format!("Link: {url}"));
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_SHELL_OPEN_URL,
            json!({ "url": url }),
            |s, res: Result<OpenUrlReply, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                    && let Some(lookalike) = rep.lookalike
                {
                    s.notice = Some(format!("Blocked lookalike link ({lookalike})"));
                }
            },
        );
    }

    pub fn load_discord_messages(&mut self, channel_id: &str, before: Option<&str>) {
        if self.status != HostStatus::Connected {
            return;
        }
        if self.discord.chat.loading_channels.contains(channel_id) {
            return;
        }
        #[cfg(windows)]
        if before.is_none()
            && let Some(prev_cid) = self
                .inflight_discord_channel
                .replace(channel_id.to_string())
            && prev_cid != channel_id
        {
            self.discord.chat.loading_channels.remove(&prev_cid);
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
            let on_done = move |s: &mut AppState, res: Result<DiscordMessagesReply, String>| {
                s.discord.chat.loading_channels.remove(&cid);
                if s.inflight_discord_channel.as_deref() == Some(cid.as_str()) {
                    s.inflight_discord_channel = None;
                }
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
                    let author_ids: Vec<String> = page
                        .iter()
                        .map(|m| m.author.id.clone())
                        .collect::<HashSet<_>>()
                        .into_iter()
                        .collect();
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
                    if let Some(gid) = s.discord.active_guild_id.clone() {
                        s.fetch_discord_colors(&gid, author_ids);
                    }
                    s.hydrate_message_cards(&cid);
                }
            };
            if is_older {
                self.call_host_async(METHOD_DISCORD_MESSAGES, params, on_done);
            } else {
                self.call_host_replaceable(
                    "discord.messages",
                    METHOD_DISCORD_MESSAGES,
                    params,
                    on_done,
                );
            }
        }
    }

    pub fn jump_to_discord_message(
        &mut self,
        guild_id: Option<&str>,
        channel_id: &str,
        message_id: &str,
    ) {
        if let Some(gid) = guild_id {
            self.open_discord_guild(gid);
            self.open_discord_channel(channel_id);
        } else {
            self.open_discord_dm(channel_id);
        }
        self.discord.chat.highlighted_message_id = Some(message_id.to_string());
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let cid = channel_id.to_string();
            let mid = message_id.to_string();
            self.call_host_async(
                METHOD_DISCORD_MESSAGES,
                json!({ "channelId": cid, "around": mid }),
                move |s, res: Result<DiscordMessagesReply, String>| {
                    if let Ok(rep) = res
                        && rep.ok
                        && let Some(mut page) = rep.messages
                    {
                        page.reverse();
                        s.discord.chat.messages_by_channel.insert(cid.clone(), page);
                        s.hydrate_message_cards(&cid);
                    }
                },
            );
        }
    }

    pub fn fetch_discord_colors(&mut self, guild_id: &str, user_ids: Vec<String>) {
        if self.status != HostStatus::Connected || user_ids.is_empty() {
            return;
        }
        let known = self.discord.chat.links.guild_colors.get(guild_id);
        let needed: Vec<String> = user_ids
            .into_iter()
            .filter(|uid| {
                let key = format!("{guild_id}:{uid}");
                known.is_none_or(|m| !m.contains_key(uid))
                    && !self
                        .discord
                        .chat
                        .links
                        .guild_colors_requested
                        .contains(&key)
            })
            .take(50)
            .collect();
        if needed.is_empty() {
            return;
        }
        for uid in &needed {
            self.discord
                .chat
                .links
                .guild_colors_requested
                .insert(format!("{guild_id}:{uid}"));
        }
        #[cfg(windows)]
        {
            let gid = guild_id.to_string();
            self.call_host_async(
                METHOD_DISCORD_COLORS,
                json!({ "guildId": gid, "userIds": needed }),
                move |s, res: Result<DiscordColorsReply, String>| {
                    if let Ok(rep) = res {
                        let entry = s.discord.chat.links.guild_colors.entry(gid).or_default();
                        for (uid, color) in rep.colors {
                            if let Some(c) = color {
                                entry.insert(uid, c);
                            }
                        }
                    }
                },
            );
        }
    }

    pub fn hydrate_message_cards(&mut self, channel_id: &str) {
        let Some(list) = self.discord.chat.messages_by_channel.get(channel_id) else {
            return;
        };
        let mut invites = Vec::new();
        let mut gifts = Vec::new();
        let mut jams = Vec::new();
        let mut text_files = Vec::new();
        for msg in list {
            for code in extract_invite_codes(&msg.content) {
                if !self.discord.chat.links.invites.contains_key(&code) {
                    invites.push(code);
                }
            }
            for code in extract_gift_codes(&msg.content) {
                if !self.discord.chat.links.gifts.contains_key(&code) {
                    gifts.push(code);
                }
            }
            if let Some(jam_url) = extract_spotify_jam_url(&msg.content)
                && !self.discord.chat.links.jams.contains_key(&jam_url)
            {
                jams.push(jam_url);
            }
            for att in &msg.attachments {
                if !att.is_image()
                    && att.waveform.is_none()
                    && is_text_preview_filename(&att.filename, att.content_type.as_deref())
                    && !self.discord.chat.links.file_texts.contains_key(&att.url)
                {
                    text_files.push(att.url.clone());
                }
            }
        }
        for code in invites.into_iter().take(6) {
            self.fetch_invite_card(&code);
        }
        for code in gifts.into_iter().take(6) {
            self.fetch_gift_card(&code);
        }
        for url in jams.into_iter().take(4) {
            self.resolve_spotify_jam(&url);
        }
        for url in text_files.into_iter().take(6) {
            self.fetch_file_text(&url);
        }
    }

    pub fn fetch_invite_card(&mut self, code: &str) {
        self.discord
            .chat
            .links
            .invites
            .insert(code.to_string(), InviteCardEntry::Loading);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let c = code.to_string();
            self.call_host_async(
                METHOD_DISCORD_SERVER,
                json!({ "action": "invite", "invite": c }),
                move |s, res: Result<DiscordServerCardReply, String>| {
                    let entry = match res {
                        Ok(rep) if rep.ok && rep.invite.is_some() => {
                            InviteCardEntry::Loaded(rep.invite.unwrap())
                        }
                        Ok(rep) if rep.invalid => InviteCardEntry::Invalid,
                        _ => {
                            s.discord.chat.links.invites.remove(&c);
                            return;
                        }
                    };
                    s.discord.chat.links.invites.insert(c, entry);
                },
            );
        }
    }

    pub fn join_invite_code(&mut self, code: &str) {
        if self.status != HostStatus::Connected {
            self.notice = Some(format!("Joined invite {code}"));
            return;
        }
        #[cfg(windows)]
        {
            let c = code.to_string();
            self.call_host_async(
                METHOD_DISCORD_SERVER,
                json!({ "action": "join", "invite": c }),
                move |s, res: Result<DiscordServerCardReply, String>| {
                    if let Ok(rep) = res {
                        if rep.ok {
                            s.discord.chat.links.invites.remove(&c);
                            s.refresh_discord_home();
                            if let Some(gid) = rep.guild_id {
                                s.open_discord_guild(&gid);
                            }
                        } else if let Some(msg) = rep.message {
                            s.notice = Some(msg);
                        }
                    }
                },
            );
        }
    }

    pub fn fetch_gift_card(&mut self, code: &str) {
        self.discord
            .chat
            .links
            .gifts
            .insert(code.to_string(), GiftCardEntry::Loading);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let c = code.to_string();
            self.call_host_async(
                METHOD_DISCORD_SERVER,
                json!({ "action": "gift", "code": c }),
                move |s, res: Result<DiscordServerCardReply, String>| {
                    let entry = match res {
                        Ok(rep) if rep.ok && rep.gift.is_some() => {
                            GiftCardEntry::Loaded(rep.gift.unwrap())
                        }
                        Ok(rep) if rep.invalid => GiftCardEntry::Invalid,
                        _ => {
                            s.discord.chat.links.gifts.remove(&c);
                            return;
                        }
                    };
                    s.discord.chat.links.gifts.insert(c, entry);
                },
            );
        }
    }

    pub fn redeem_gift_code(&mut self, code: &str, channel_id: Option<&str>) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let c = code.to_string();
            let mut params = json!({ "action": "redeemGift", "code": c });
            if let Some(cid) = channel_id {
                params["channelId"] = json!(cid);
            }
            self.call_host_async(
                METHOD_DISCORD_SERVER,
                params,
                move |s, res: Result<DiscordServerCardReply, String>| match res {
                    Ok(rep) if rep.ok => {
                        if let Some(GiftCardEntry::Loaded(g)) =
                            s.discord.chat.links.gifts.get_mut(&c)
                        {
                            g.redeemed = true;
                        }
                        s.notice = Some("Gift claimed".into());
                    }
                    Ok(rep) => {
                        s.notice = Some(
                            rep.message
                                .unwrap_or_else(|| "Could not claim this gift.".into()),
                        );
                    }
                    Err(e) => s.notice = Some(e),
                },
            );
        }
    }

    pub fn resolve_spotify_jam(&mut self, url: &str) {
        if url.to_ascii_lowercase().contains("/socialsession/") {
            self.discord.chat.links.jams.insert(url.to_string(), true);
            return;
        }
        self.discord.chat.links.jams.insert(url.to_string(), false);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let u = url.to_string();
            self.call_host_async(
                METHOD_SPOTIFY_LINK,
                json!({ "url": u }),
                move |s, res: Result<SpotifyLinkReply, String>| {
                    let is_jam = res.ok().is_some_and(|r| {
                        r.ok && r
                            .uri
                            .as_deref()
                            .is_some_and(|uri| uri.starts_with("spotify:socialsession:"))
                    });
                    s.discord.chat.links.jams.insert(u, is_jam);
                },
            );
        }
    }

    pub fn fetch_file_text(&mut self, url: &str) {
        self.discord
            .chat
            .links
            .file_texts
            .insert(url.to_string(), FileTextEntry::Loading);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let u = url.to_string();
            self.call_host_async(
                METHOD_DISCORD_FILE_TEXT,
                json!({ "url": u }),
                move |s, res: Result<DiscordFileTextReply, String>| {
                    let entry = match res {
                        Ok(rep) if rep.ok && rep.text.is_some() => FileTextEntry::Loaded {
                            text: rep.text.unwrap_or_default(),
                            left: rep.left,
                            expanded: false,
                        },
                        _ => FileTextEntry::Failed,
                    };
                    s.discord.chat.links.file_texts.insert(u, entry);
                },
            );
        }
    }

    pub fn toggle_file_text_expand(&mut self, url: &str) {
        if let Some(FileTextEntry::Loaded { expanded, .. }) =
            self.discord.chat.links.file_texts.get_mut(url)
        {
            *expanded = !*expanded;
        }
    }

    pub fn discord_image_action(&mut self, action: &str, url: &str) {
        self.discord.chat.links.image_menu = None;
        if self.status != HostStatus::Connected {
            self.notice = Some(if action == "copy" {
                "Copied image".into()
            } else {
                "Saved image".into()
            });
            return;
        }
        #[cfg(windows)]
        {
            let act = action.to_string();
            self.call_host_async(
                METHOD_DISCORD_IMAGE,
                json!({ "action": act, "url": url }),
                move |s, res: Result<DiscordImageReply, String>| match res {
                    Ok(rep) if rep.ok => {
                        s.notice = Some(if act == "copy" {
                            "Copied image".into()
                        } else if let Some(name) = rep.name {
                            format!("Saved {name} to Downloads")
                        } else {
                            "Saved to Downloads".into()
                        });
                    }
                    Ok(rep) => {
                        s.notice = Some(
                            rep.message
                                .unwrap_or_else(|| "Exo couldn't process that image.".into()),
                        );
                    }
                    Err(e) => s.notice = Some(e),
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

    pub fn mark_discord_message_unread(&mut self, channel_id: &str, message_id: &str) {
        let before_id = id_before(message_id);
        if self.status != HostStatus::Connected {
            self.notice = Some("Marked unread".into());
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ACK,
            json!({ "channelId": channel_id, "messageId": before_id, "unread": true }),
            |s, res: Result<ActionResult, String>| {
                if let Ok(rep) = res
                    && rep.ok
                {
                    s.notice = Some("Marked unread".into());
                }
            },
        );
    }

    pub fn notify_discord_typing(&mut self) {
        self.discord.chat.too_long_prompt = false;
        self.refresh_composer_autocomplete();
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

    pub fn refresh_composer_autocomplete(&mut self) {
        let text = self.discord.chat.composer.text.clone();
        if text.starts_with('/') && !text.contains(' ') {
            if let Some(cid) = self.active_chat_channel_id() {
                self.ensure_channel_commands_loaded(&cid);
            }
            return;
        }
        let Some((_, sigil, query)) = active_mention_token(&text) else {
            self.discord.chat.autocomplete.last_query.clear();
            return;
        };
        let cache_key = format!("{sigil}{query}");
        if self.discord.chat.autocomplete.last_query == cache_key {
            return;
        }
        self.discord.chat.autocomplete.last_query = cache_key;
        self.discord.chat.autocomplete.selected = 0;

        if sigil == ':' {
            let gid = self.discord.active_guild_id.clone();
            self.ensure_emojis_loaded(gid.as_deref());
            return;
        }

        let Some(gid) = self.discord.active_guild_id.clone() else {
            return;
        };
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let is_channels = sigil == '#';
            let q = query.to_string();
            let ask = !is_channels && !q.is_empty();
            let mut params = json!({
                "guildId": gid,
                "query": q,
                "ask": ask,
            });
            if is_channels {
                params["channels"] = json!(true);
            }
            self.call_host_async(
                METHOD_DISCORD_MENTIONABLE,
                params,
                move |s, res: Result<DiscordMentionableReply, String>| {
                    if let Ok(rep) = res {
                        if is_channels {
                            s.discord.chat.autocomplete.channels = rep.channels;
                        } else {
                            s.discord.chat.autocomplete.users = rep.users;
                            s.discord.chat.autocomplete.roles = rep.roles;
                        }
                    }
                },
            );
        }
    }

    pub fn apply_autocomplete_selection(&mut self, insert: &str, raw_mention: Option<&str>) {
        if let Some(raw) = raw_mention {
            self.discord
                .chat
                .autocomplete
                .mentions_map
                .insert(insert.to_string(), raw.to_string());
        }
        let text = self.discord.chat.composer.text.clone();
        if let Some((start, _, _)) = active_mention_token(&text) {
            let mut next = String::with_capacity(start + insert.len() + 1);
            next.push_str(&text[..start]);
            next.push_str(insert);
            next.push(' ');
            self.discord.chat.composer.text = next;
            let end = self.discord.chat.composer.text.len();
            self.discord.chat.composer.anchor = end;
            self.discord.chat.composer.focus = end;
        } else {
            self.discord.chat.composer.text.push_str(insert);
            self.discord.chat.composer.text.push(' ');
            let end = self.discord.chat.composer.text.len();
            self.discord.chat.composer.anchor = end;
            self.discord.chat.composer.focus = end;
        }
        self.discord.chat.autocomplete.selected = 0;
        self.discord.chat.autocomplete.last_query.clear();
    }

    pub fn upload_composer_text_as_file(&mut self) {
        let body = self.discord.chat.composer.text.clone();
        if body.trim().is_empty() {
            return;
        }
        self.discord.chat.composer = TextEdit::default();
        self.discord.chat.too_long_prompt = false;
        self.discord.chat.plus_menu_open = false;
        self.discord.chat.autocomplete.mentions_map.clear();
        self.attach_raw_bytes("message.txt".into(), "text/plain", body.into_bytes());
    }

    pub fn attach_raw_bytes(&mut self, name: String, mime: &'static str, bytes: Vec<u8>) {
        if bytes.is_empty() || bytes.len() > 25 * 1024 * 1024 {
            self.notice = Some("Files must be under 25 MB".into());
            return;
        }
        if self.discord.chat.pending_uploads.len() >= 10 {
            self.notice = Some("Up to 10 attachments per message".into());
            return;
        }
        let size = bytes.len() as u64;
        let is_media = mime.starts_with("image/") || mime.starts_with("video/");
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
                spoiler: false,
                description: String::new(),
                is_media,
                raw_bytes: Some(bytes),
            });
            return;
        }

        self.discord.chat.pending_uploads.push(PendingAttachment {
            local_id: local_id.clone(),
            name: name.clone(),
            size,
            upload_id: None,
            uploading: true,
            error: None,
            spoiler: false,
            description: String::new(),
            is_media,
            raw_bytes: Some(bytes.clone()),
        });
        self.spawn_upload_worker(local_id, name, mime.to_string(), bytes);
    }

    #[cfg(windows)]
    fn spawn_upload_worker(&self, local_id: String, name: String, mime: String, bytes: Vec<u8>) {
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
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

    #[cfg(not(windows))]
    fn spawn_upload_worker(
        &self,
        _local_id: String,
        _name: String,
        _mime: String,
        _bytes: Vec<u8>,
    ) {
    }

    pub fn open_modify_attachment(&mut self, local_id: &str) {
        let Some(att) = self
            .discord
            .chat
            .pending_uploads
            .iter()
            .find(|u| u.local_id == local_id)
            .cloned()
        else {
            return;
        };
        let mut name = TextEdit::default();
        name.text = att.name;
        let mut description = TextEdit::default();
        description.text = att.description;
        self.discord.chat.modifying_attachment = Some(ModifyAttachmentState {
            local_id: att.local_id,
            name,
            description,
            spoiler: att.spoiler,
            is_media: att.is_media,
        });
    }

    pub fn save_modify_attachment(&mut self) {
        let Some(draft) = self.discord.chat.modifying_attachment.take() else {
            return;
        };
        let mut reupload: Option<(String, String, Vec<u8>)> = None;
        if let Some(att) = self
            .discord
            .chat
            .pending_uploads
            .iter_mut()
            .find(|u| u.local_id == draft.local_id)
        {
            let next_name = draft.name.text.trim();
            let renamed = !next_name.is_empty() && next_name != att.name;
            let spoiler_changed = att.spoiler != draft.spoiler;
            if renamed {
                att.name = next_name.to_string();
            }
            att.description = draft.description.text.trim().to_string();
            att.spoiler = draft.spoiler;
            if (renamed || spoiler_changed)
                && self.status == HostStatus::Connected
                && let Some(bytes) = att.raw_bytes.clone()
            {
                let effective_name = if att.spoiler && !att.name.starts_with("SPOILER_") {
                    format!("SPOILER_{}", att.name)
                } else {
                    att.name.clone()
                };
                att.uploading = true;
                att.upload_id = None;
                att.error = None;
                reupload = Some((att.local_id.clone(), effective_name, bytes));
            }
        }
        if let Some((lid, fname, bytes)) = reupload {
            let mime = guess_mime(&fname).to_string();
            self.spawn_upload_worker(lid, fname, mime, bytes);
        }
    }

    pub fn toggle_attachment_spoiler(&mut self, local_id: &str) {
        let mut reupload: Option<(String, String, Vec<u8>)> = None;
        if let Some(att) = self
            .discord
            .chat
            .pending_uploads
            .iter_mut()
            .find(|u| u.local_id == local_id)
        {
            att.spoiler = !att.spoiler;
            if self.status == HostStatus::Connected
                && let Some(bytes) = att.raw_bytes.clone()
            {
                let effective_name = if att.spoiler && !att.name.starts_with("SPOILER_") {
                    format!("SPOILER_{}", att.name)
                } else {
                    att.name.clone()
                };
                att.uploading = true;
                att.upload_id = None;
                att.error = None;
                reupload = Some((att.local_id.clone(), effective_name, bytes));
            }
        }
        if let Some((lid, fname, bytes)) = reupload {
            let mime = guess_mime(&fname).to_string();
            self.spawn_upload_worker(lid, fname, mime, bytes);
        }
    }

    pub fn send_discord_message(&mut self) -> Option<String> {
        let cid = self.active_chat_channel_id()?;
        let expanded = expand_mentions(
            &self.discord.chat.composer.text,
            &self.discord.chat.autocomplete.mentions_map,
        );
        let content = expanded.trim().to_string();
        let upload_ids: Vec<String> = self
            .discord
            .chat
            .pending_uploads
            .iter()
            .filter_map(|u| u.upload_id.clone())
            .collect();
        if content.is_empty() && upload_ids.is_empty() {
            return None;
        }
        if content.chars().count() > self.discord.chat.char_limit {
            self.discord.chat.too_long_prompt = true;
            return None;
        }
        if self
            .discord
            .chat
            .pending_uploads
            .iter()
            .any(|u| u.uploading)
        {
            self.notice = Some("Wait for attachments to finish uploading".into());
            return None;
        }

        let descriptions: HashMap<String, String> = self
            .discord
            .chat
            .pending_uploads
            .iter()
            .filter_map(|u| {
                let uid = u.upload_id.as_ref()?;
                let d = u.description.trim();
                (!d.is_empty()).then(|| (uid.clone(), d.to_string()))
            })
            .collect();

        let reply_id = self
            .discord
            .chat
            .reply_to
            .as_ref()
            .map(|(id, _)| id.clone());
        let reply_ping = self.discord.chat.reply_ping;
        let seq = self.discord.chat.next_local_seq;
        self.discord.chat.next_local_seq += 1;
        let local_id = format!("local-{seq}");
        let launch_id = (!content.is_empty()).then(|| local_id.clone());
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
        self.discord.chat.drafts.remove(&cid);
        self.discord.chat.autocomplete.mentions_map.clear();
        self.discord.chat.reply_to = None;
        self.discord.chat.reply_ping = true;
        self.discord.chat.pending_uploads.clear();
        self.discord.chat.composer_emoji_open = false;
        self.discord.chat.picker.open_for_composer = false;
        self.discord.chat.too_long_prompt = false;

        if self.status != HostStatus::Connected {
            return launch_id;
        }
        #[cfg(windows)]
        {
            let mut params = json!({
                "channelId": cid,
                "content": content,
            });
            if let Some(ref r) = reply_id {
                params["replyTo"] = json!(r);
                if !reply_ping {
                    params["ping"] = json!(false);
                }
            }
            if !upload_ids.is_empty() {
                params["uploads"] = json!(upload_ids);
            }
            if !descriptions.is_empty() {
                params["descriptions"] = json!(descriptions);
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
        launch_id
    }

    pub fn send_direct_gif(&mut self, url: &str) {
        self.discord.chat.picker.open_for_composer = false;
        self.discord.chat.composer.text = url.to_string();
        let _ = self.send_discord_message();
    }

    pub fn send_discord_sticker(&mut self, sticker_id: &str) {
        self.discord.chat.picker.open_for_composer = false;
        let Some(cid) = self.active_chat_channel_id() else {
            return;
        };
        if self.status != HostStatus::Connected {
            self.notice = Some("Sent sticker".into());
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_STICKER,
            json!({ "channelId": cid, "stickerId": sticker_id }),
            |s, res: Result<DiscordSendReply, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                {
                    s.notice = Some(
                        rep.message
                            .unwrap_or_else(|| "Discord didn't send that sticker.".into()),
                    );
                }
            },
        );
    }

    pub fn open_expression_picker_for_composer(&mut self, tab: ExpressionTab) {
        let was_open =
            self.discord.chat.picker.open_for_composer && self.discord.chat.picker.tab == tab;
        self.discord.chat.picker.open_for_reaction = None;
        self.discord.chat.emoji_picker_for = None;
        self.discord.chat.plus_menu_open = false;
        if was_open {
            self.discord.chat.picker.open_for_composer = false;
            self.discord.chat.composer_emoji_open = false;
            return;
        }
        self.discord.chat.picker.open_for_composer = true;
        self.discord.chat.composer_emoji_open = true;
        self.discord.chat.picker.tab = tab;
        self.load_expression_tab_data(tab);
    }

    pub fn open_expression_picker_for_reaction(&mut self, message_id: &str) {
        self.discord.chat.picker.open_for_composer = false;
        self.discord.chat.picker.open_for_reaction = Some(message_id.to_string());
        self.discord.chat.picker.tab = ExpressionTab::Emoji;
        self.discord.chat.emoji_picker_for = Some(message_id.to_string());
        self.load_expression_tab_data(ExpressionTab::Emoji);
    }

    pub fn switch_expression_tab(&mut self, tab: ExpressionTab) {
        self.discord.chat.picker.tab = tab;
        self.discord.chat.picker.query = TextEdit::default();
        self.load_expression_tab_data(tab);
    }

    fn load_expression_tab_data(&mut self, tab: ExpressionTab) {
        let gid = self.discord.active_guild_id.clone();
        match tab {
            ExpressionTab::Emoji => {
                self.ensure_emojis_loaded(gid.as_deref());
                self.ensure_emoji_favorites_loaded();
            }
            ExpressionTab::Gif => {
                self.ensure_gif_favorites_loaded();
                let q = self.discord.chat.picker.query.text.trim().to_string();
                self.search_discord_gifs(if q.is_empty() { None } else { Some(&q) });
            }
            ExpressionTab::Sticker => {
                self.ensure_stickers_loaded(gid.as_deref());
            }
        }
    }

    pub fn ensure_emojis_loaded(&mut self, guild_id: Option<&str>) {
        let same_guild = self.discord.chat.picker.emojis_guild_id.as_deref() == guild_id
            && self.discord.chat.picker.emojis.is_some();
        if same_guild
            || self.discord.chat.picker.emojis_loading
            || self.status != HostStatus::Connected
        {
            return;
        }
        self.discord.chat.picker.emojis_loading = true;
        self.discord.chat.picker.emojis_guild_id = guild_id.map(str::to_string);
        #[cfg(windows)]
        {
            let params = match guild_id {
                Some(gid) => json!({ "guildId": gid }),
                None => json!({}),
            };
            self.call_host_async(
                METHOD_DISCORD_EMOJIS,
                params,
                |s, res: Result<DiscordEmojisReply, String>| {
                    s.discord.chat.picker.emojis_loading = false;
                    if let Ok(rep) = res {
                        s.discord.chat.char_limit = if rep.premium { 4000 } else { 2000 };
                        s.discord.chat.picker.emojis = Some(rep);
                    }
                },
            );
        }
    }

    pub fn ensure_emoji_favorites_loaded(&mut self) {
        if self.discord.chat.picker.emojis_favorites_loaded || self.status != HostStatus::Connected
        {
            return;
        }
        self.discord.chat.picker.emojis_favorites_loaded = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_EMOJI_FAVORITES,
            json!({ "action": "list" }),
            |s, res: Result<DiscordEmojiFavoritesReply, String>| {
                if let Ok(rep) = res
                    && rep.ok
                {
                    s.discord.chat.picker.emoji_favorites = rep.favorites.unwrap_or_default();
                    s.discord.chat.picker.emoji_frequent = rep.frequent.unwrap_or_default();
                    s.discord.chat.picker.emoji_reactions = rep.reactions.unwrap_or_default();
                }
            },
        );
    }

    pub fn toggle_emoji_favorite(&mut self, key: &str) {
        let already = self
            .discord
            .chat
            .picker
            .emoji_favorites
            .iter()
            .any(|k| k == key);
        if already {
            self.discord
                .chat
                .picker
                .emoji_favorites
                .retain(|k| k != key);
        } else {
            self.discord
                .chat
                .picker
                .emoji_favorites
                .insert(0, key.to_string());
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_EMOJI_FAVORITES,
            json!({ "action": if already { "remove" } else { "add" }, "emoji": key }),
            |s, res: Result<DiscordEmojiFavoritesReply, String>| {
                if let Ok(rep) = res
                    && rep.ok
                    && let Some(fav) = rep.favorites
                {
                    s.discord.chat.picker.emoji_favorites = fav;
                }
            },
        );
    }

    pub fn note_emoji_use(&mut self, key: &str, reaction: bool) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_EMOJI_FAVORITES,
            json!({ "action": "use", "emoji": key, "reaction": reaction }),
            |_s, _res: Result<DiscordEmojiFavoritesReply, String>| {},
        );
    }

    pub fn search_discord_gifs(&mut self, query: Option<&str>) {
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.chat.picker.gifs_loading = true;
        #[cfg(windows)]
        {
            let params = match query.filter(|q| !q.trim().is_empty()) {
                Some(q) => json!({ "query": q }),
                None => json!({}),
            };
            self.call_host_async(
                METHOD_DISCORD_GIFS,
                params,
                |s, res: Result<DiscordGifsReply, String>| {
                    s.discord.chat.picker.gifs_loading = false;
                    if let Ok(rep) = res
                        && rep.ok
                    {
                        s.discord.chat.picker.gifs = rep.gifs.unwrap_or_default();
                    }
                },
            );
        }
    }

    pub fn ensure_gif_favorites_loaded(&mut self) {
        if self.discord.chat.picker.gif_favorites_loaded || self.status != HostStatus::Connected {
            return;
        }
        self.discord.chat.picker.gif_favorites_loaded = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_GIF_FAVORITES,
            json!({ "action": "list" }),
            |s, res: Result<DiscordGifFavoritesReply, String>| {
                if let Ok(rep) = res
                    && rep.ok
                {
                    s.discord.chat.picker.gif_favorites = rep.gifs.unwrap_or_default();
                }
            },
        );
    }

    pub fn toggle_gif_favorite(&mut self, url: &str, src: &str, width: u32, height: u32) {
        let already = self
            .discord
            .chat
            .picker
            .gif_favorites
            .iter()
            .any(|g| g.url == url);
        if already {
            self.discord
                .chat
                .picker
                .gif_favorites
                .retain(|g| g.url != url);
        } else {
            self.discord.chat.picker.gif_favorites.insert(
                0,
                DiscordFavoriteGif {
                    url: url.to_string(),
                    src: src.to_string(),
                    format: 2,
                    width,
                    height,
                },
            );
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_GIF_FAVORITES,
            json!({
                "action": if already { "remove" } else { "add" },
                "url": url,
                "src": src,
                "width": width,
                "height": height,
            }),
            |s, res: Result<DiscordGifFavoritesReply, String>| {
                if let Ok(rep) = res
                    && rep.ok
                    && let Some(gifs) = rep.gifs
                {
                    s.discord.chat.picker.gif_favorites = gifs;
                }
            },
        );
    }

    pub fn ensure_stickers_loaded(&mut self, guild_id: Option<&str>) {
        let same = self.discord.chat.picker.stickers_guild_id.as_deref() == guild_id
            && !self.discord.chat.picker.stickers.is_empty();
        if same || self.discord.chat.picker.stickers_loading || self.status != HostStatus::Connected
        {
            return;
        }
        self.discord.chat.picker.stickers_loading = true;
        self.discord.chat.picker.stickers_guild_id = guild_id.map(str::to_string);
        #[cfg(windows)]
        {
            let params = match guild_id {
                Some(gid) => json!({ "guildId": gid }),
                None => json!({}),
            };
            self.call_host_async(
                METHOD_DISCORD_STICKERS,
                params,
                |s, res: Result<DiscordStickersReply, String>| {
                    s.discord.chat.picker.stickers_loading = false;
                    if let Ok(rep) = res {
                        s.discord.chat.picker.stickers = rep.groups.unwrap_or_default();
                        s.discord.chat.picker.sticker_favorites = rep.favorites.unwrap_or_default();
                    }
                },
            );
        }
    }

    pub fn toggle_sticker_favorite(&mut self, sticker_id: &str) {
        let already = self
            .discord
            .chat
            .picker
            .sticker_favorites
            .iter()
            .any(|id| id == sticker_id);
        if already {
            self.discord
                .chat
                .picker
                .sticker_favorites
                .retain(|id| id != sticker_id);
        } else {
            self.discord
                .chat
                .picker
                .sticker_favorites
                .insert(0, sticker_id.to_string());
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let mut params = json!({
                "action": if already { "remove" } else { "add" },
                "stickerId": sticker_id,
            });
            if let Some(ref gid) = self.discord.active_guild_id {
                params["guildId"] = json!(gid);
            }
            self.call_host_async(
                METHOD_DISCORD_STICKERS,
                params,
                |s, res: Result<DiscordStickersReply, String>| {
                    if let Ok(rep) = res
                        && let Some(favs) = rep.favorites
                    {
                        s.discord.chat.picker.sticker_favorites = favs;
                    }
                },
            );
        }
    }

    pub fn open_reactors_dialog(
        &mut self,
        channel_id: &str,
        message_id: &str,
        emoji_key: &str,
        burst: bool,
    ) {
        self.discord.chat.reactors_dialog = Some(ReactorsDialogState {
            channel_id: channel_id.to_string(),
            message_id: message_id.to_string(),
            selected_key: emoji_key.to_string(),
            selected_burst: burst,
            users: Vec::new(),
            can_remove: false,
            loading: self.status == HostStatus::Connected,
            has_more: false,
            error: None,
        });
        self.load_reactors_page(None);
    }

    pub fn select_reactors_tab(&mut self, emoji_key: &str, burst: bool) {
        if let Some(ref mut dlg) = self.discord.chat.reactors_dialog {
            dlg.selected_key = emoji_key.to_string();
            dlg.selected_burst = burst;
            dlg.users.clear();
            dlg.has_more = false;
            dlg.error = None;
            dlg.loading = self.status == HostStatus::Connected;
        }
        self.load_reactors_page(None);
    }

    pub fn load_reactors_page(&mut self, after: Option<&str>) {
        if self.status != HostStatus::Connected {
            return;
        }
        let Some((cid, mid, key, burst)) = self.discord.chat.reactors_dialog.as_ref().map(|d| {
            (
                d.channel_id.clone(),
                d.message_id.clone(),
                d.selected_key.clone(),
                d.selected_burst,
            )
        }) else {
            return;
        };
        #[cfg(windows)]
        {
            let mut params = json!({
                "channelId": cid,
                "messageId": mid,
                "emoji": key,
                "burst": burst,
            });
            let append = after.is_some();
            if let Some(a) = after {
                params["after"] = json!(a);
            }
            self.call_host_async(
                METHOD_DISCORD_REACTORS,
                params,
                move |s, res: Result<DiscordReactorsReply, String>| {
                    let Some(ref mut dlg) = s.discord.chat.reactors_dialog else {
                        return;
                    };
                    if dlg.message_id != mid || dlg.selected_key != key {
                        return;
                    }
                    dlg.loading = false;
                    match res {
                        Ok(rep) if rep.ok => {
                            let page = rep.users.unwrap_or_default();
                            dlg.has_more = page.len() >= 100;
                            dlg.can_remove = rep.can_remove;
                            if append {
                                dlg.users.extend(page);
                            } else {
                                dlg.users = page;
                            }
                            dlg.error = None;
                        }
                        Ok(rep) => {
                            dlg.error = Some(
                                rep.message
                                    .unwrap_or_else(|| "Discord didn't send who reacted.".into()),
                            );
                        }
                        Err(e) => dlg.error = Some(e),
                    }
                },
            );
        }
    }

    pub fn remove_user_reaction(&mut self, user_id: &str) {
        let Some((cid, mid, key, burst)) = self.discord.chat.reactors_dialog.as_ref().map(|d| {
            (
                d.channel_id.clone(),
                d.message_id.clone(),
                d.selected_key.clone(),
                d.selected_burst,
            )
        }) else {
            return;
        };
        if let Some(ref mut dlg) = self.discord.chat.reactors_dialog {
            dlg.users.retain(|u| u.id != user_id);
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_REACT,
            json!({
                "channelId": cid,
                "messageId": mid,
                "emoji": key,
                "add": false,
                "burst": burst,
                "userId": user_id,
            }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn open_poll_creator(&mut self) {
        let Some(cid) = self.active_chat_channel_id() else {
            return;
        };
        self.discord.chat.plus_menu_open = false;
        self.discord.chat.poll_creator = Some(PollCreatorState {
            channel_id: cid,
            question: TextEdit::default(),
            answers: vec![TextEdit::default(), TextEdit::default()],
            hours: 24,
            allow_multiselect: false,
            busy: false,
            error: None,
        });
    }

    pub fn submit_poll_creator(&mut self) {
        let Some(ref mut pc) = self.discord.chat.poll_creator else {
            return;
        };
        if pc.busy {
            return;
        }
        let question = pc.question.text.trim().to_string();
        let texts: Vec<String> = pc
            .answers
            .iter()
            .map(|a| a.text.trim().to_string())
            .filter(|s| !s.is_empty())
            .collect();
        if question.is_empty() || texts.len() < 2 {
            pc.error = Some("Enter a question and at least 2 answers.".into());
            return;
        }
        let cid = pc.channel_id.clone();
        let hours = pc.hours;
        let multiple = pc.allow_multiselect;
        if self.status != HostStatus::Connected {
            self.discord.chat.poll_creator = None;
            let me = self
                .discord
                .home
                .me
                .as_ref()
                .map(|m| DiscordUser {
                    id: m.id.clone(),
                    username: m.name.clone(),
                    global_name: Some(m.name.clone()),
                    avatar: m.avatar.clone(),
                    bot: false,
                })
                .unwrap_or_else(|| DiscordUser {
                    id: "me".into(),
                    username: "you".into(),
                    global_name: Some("You".into()),
                    avatar: None,
                    bot: false,
                });
            let answers = texts
                .iter()
                .enumerate()
                .map(|(idx, t)| exo_rpc::DiscordPollAnswer {
                    answer_id: (idx + 1) as i32,
                    poll_media: exo_rpc::DiscordPollMedia {
                        text: Some(t.clone()),
                        emoji: None,
                    },
                })
                .collect();
            let poll = exo_rpc::DiscordPoll {
                question: exo_rpc::DiscordPollMedia {
                    text: Some(question),
                    emoji: None,
                },
                answers,
                expiry: None,
                allow_multiselect: multiple,
                results: None,
            };
            self.discord.chat.next_local_seq += 1;
            let local_id = format!("local-poll-{}", self.discord.chat.next_local_seq);
            let msg = DiscordMessage {
                id: local_id,
                channel_id: cid.clone(),
                author: me,
                poll: Some(poll),
                ..Default::default()
            };
            self.discord
                .chat
                .messages_by_channel
                .entry(cid)
                .or_default()
                .push(msg);
            self.notice = Some("Poll posted".into());
            return;
        }
        pc.busy = true;
        pc.error = None;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_POLL,
            json!({
                "channelId": cid,
                "action": "create",
                "question": question,
                "texts": texts,
                "hours": hours,
                "multiple": multiple,
            }),
            |s, res: Result<ActionResult, String>| match res {
                Ok(rep) if rep.ok => {
                    s.discord.chat.poll_creator = None;
                }
                Ok(rep) => {
                    if let Some(ref mut pc) = s.discord.chat.poll_creator {
                        pc.busy = false;
                        pc.error = Some(
                            rep.message
                                .unwrap_or_else(|| "Discord didn't post the poll.".into()),
                        );
                    }
                }
                Err(e) => {
                    if let Some(ref mut pc) = s.discord.chat.poll_creator {
                        pc.busy = false;
                        pc.error = Some(e);
                    }
                }
            },
        );
    }

    pub fn toggle_poll_pick(&mut self, message_id: &str, answer_id: i32, multiselect: bool) {
        let set = self
            .discord
            .chat
            .poll_picks
            .entry(message_id.to_string())
            .or_default();
        if set.contains(&answer_id) {
            set.remove(&answer_id);
        } else {
            if !multiselect {
                set.clear();
            }
            set.insert(answer_id);
        }
    }

    pub fn submit_poll_vote(&mut self, channel_id: &str, message_id: &str, answers: Vec<i32>) {
        self.discord.chat.poll_picks.remove(message_id);
        if self.status != HostStatus::Connected {
            if let Some(list) = self.discord.chat.messages_by_channel.get_mut(channel_id)
                && let Some(msg) = list.iter_mut().find(|m| m.id == message_id)
                && let Some(ref mut poll) = msg.poll
            {
                if answers.is_empty() {
                    if let Some(ref mut res) = poll.results {
                        for c in &mut res.answer_counts {
                            if c.me_voted {
                                c.count = (c.count - 1).max(0);
                                c.me_voted = false;
                            }
                        }
                    }
                } else {
                    for aid in answers {
                        poll.apply_vote(aid, true, true);
                    }
                }
            }
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_POLL,
            json!({
                "channelId": channel_id,
                "messageId": message_id,
                "answers": answers,
            }),
            |s, res: Result<ActionResult, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                {
                    s.notice = Some(
                        rep.message
                            .unwrap_or_else(|| "Discord didn't take that vote.".into()),
                    );
                }
            },
        );
    }

    pub fn end_discord_poll(&mut self, channel_id: &str, message_id: &str) {
        if self.status != HostStatus::Connected {
            if let Some(list) = self.discord.chat.messages_by_channel.get_mut(channel_id)
                && let Some(msg) = list.iter_mut().find(|m| m.id == message_id)
                && let Some(ref mut poll) = msg.poll
            {
                poll.results
                    .get_or_insert_with(Default::default)
                    .is_finalized = true;
            }
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_POLL,
            json!({
                "channelId": channel_id,
                "messageId": message_id,
                "action": "end",
            }),
            |s, res: Result<ActionResult, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                {
                    s.notice = Some(
                        rep.message
                            .unwrap_or_else(|| "Discord didn't end that poll.".into()),
                    );
                }
            },
        );
    }

    pub fn open_discord_inbox(&mut self, tab: InboxTab) {
        self.discord.inbox_open = true;
        self.discord.chat.inbox.tab = tab;
        self.load_inbox_tab(tab);
    }

    pub fn select_inbox_tab(&mut self, tab: InboxTab) {
        self.discord.chat.inbox.tab = tab;
        self.load_inbox_tab(tab);
    }

    pub fn load_inbox_tab(&mut self, tab: InboxTab) {
        match tab {
            InboxTab::ForYou => self.load_discord_for_you(None),
            InboxTab::Unreads => self.load_discord_unreads(),
            InboxTab::Mentions => self.load_discord_mentions(None),
            InboxTab::Bookmarks => self.load_discord_saved_bookmarks(None),
            InboxTab::Reminders => self.load_discord_saved_state(),
        }
    }

    pub fn load_discord_for_you(&mut self, before: Option<&str>) {
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.chat.inbox.for_you_loading = true;
        self.discord.chat.inbox.for_you_error = None;
        #[cfg(windows)]
        {
            let append = before.is_some();
            let params = match before {
                Some(b) => json!({ "before": b }),
                None => json!({}),
            };
            self.call_host_async(
                METHOD_DISCORD_FOR_YOU,
                params,
                move |s, res: Result<DiscordForYouReply, String>| {
                    s.discord.chat.inbox.for_you_loading = false;
                    match res {
                        Ok(rep) if rep.ok => {
                            let page = rep.items.unwrap_or_default();
                            s.discord.chat.inbox.for_you_has_more = rep.has_more;
                            if append {
                                s.discord.chat.inbox.for_you.extend(page);
                            } else {
                                s.discord.chat.inbox.for_you = page;
                            }
                        }
                        Ok(rep) => {
                            s.discord.chat.inbox.for_you_error =
                                Some(rep.message.unwrap_or_else(|| {
                                    "Discord didn't send your notifications.".into()
                                }));
                        }
                        Err(e) => s.discord.chat.inbox.for_you_error = Some(e),
                    }
                },
            );
        }
    }

    pub fn load_discord_unreads(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_UNREADS,
            json!({}),
            |s, res: Result<exo_rpc::DiscordUnreadsReply, String>| {
                if let Ok(rep) = res {
                    let preview_targets: Vec<(String, Option<String>)> = rep
                        .channels
                        .iter()
                        .take(5)
                        .map(|c| (c.channel_id.clone(), c.last_read.clone()))
                        .collect();
                    s.discord.unreads = rep.channels;
                    for (cid, last_read) in preview_targets {
                        s.peek_unread_channel(&cid, last_read.as_deref());
                    }
                }
            },
        );
    }

    pub fn peek_unread_channel(&mut self, channel_id: &str, after: Option<&str>) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let cid = channel_id.to_string();
            let mut params = json!({ "channelId": cid, "peek": true });
            if let Some(a) = after {
                params["after"] = json!(a);
            }
            self.call_host_async(
                METHOD_DISCORD_MESSAGES,
                params,
                move |s, res: Result<DiscordMessagesReply, String>| {
                    if let Ok(rep) = res
                        && rep.ok
                        && let Some(mut msgs) = rep.messages
                    {
                        msgs.truncate(3);
                        msgs.reverse();
                        s.discord.chat.inbox.unread_previews.insert(cid, msgs);
                    }
                },
            );
        }
    }

    pub fn mark_unread_channel_read(&mut self, channel_id: &str) {
        self.discord.unreads.retain(|c| c.channel_id != channel_id);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CHANNEL,
            json!({ "channelId": channel_id, "action": "read" }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn mark_unread_server_read(&mut self, guild_id: &str) {
        self.discord.unreads.retain(|c| c.guild_id != guild_id);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_MARK_READ,
            json!({ "guildId": guild_id }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn load_discord_mentions(&mut self, before: Option<&str>) {
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.chat.inbox.mentions_loading = true;
        self.discord.chat.inbox.mentions_error = None;
        #[cfg(windows)]
        {
            let append = before.is_some();
            let mut params = json!({
                "roles": self.discord.chat.inbox.mentions_roles,
                "everyone": self.discord.chat.inbox.mentions_everyone,
            });
            if let Some(b) = before {
                params["before"] = json!(b);
            }
            if self.discord.chat.inbox.mentions_this_server
                && let Some(ref gid) = self.discord.active_guild_id
            {
                params["guildId"] = json!(gid);
            }
            self.call_host_async(
                METHOD_DISCORD_MENTIONS,
                params,
                move |s, res: Result<DiscordMentionsReply, String>| {
                    s.discord.chat.inbox.mentions_loading = false;
                    match res {
                        Ok(rep) if rep.ok => {
                            let page = rep.messages.unwrap_or_default();
                            if append {
                                s.discord.chat.inbox.mentions.extend(page);
                            } else {
                                s.discord.chat.inbox.mentions = page;
                                s.discord.chat.inbox.mentions_places.clear();
                            }
                            s.discord.chat.inbox.mentions_places.extend(rep.places);
                        }
                        Ok(rep) => {
                            s.discord.chat.inbox.mentions_error =
                                Some(rep.message.unwrap_or_else(|| {
                                    "Discord didn't send your mentions.".into()
                                }));
                        }
                        Err(e) => s.discord.chat.inbox.mentions_error = Some(e),
                    }
                },
            );
        }
    }

    pub fn dismiss_discord_mention(&mut self, message_id: &str) {
        self.discord
            .chat
            .inbox
            .mentions
            .retain(|m| m.id != message_id);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_MENTIONS,
            json!({ "remove": message_id }),
            |_s, _res: Result<DiscordMentionsReply, String>| {},
        );
    }

    pub fn load_discord_saved_state(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.chat.inbox.saved_loading = true;
        self.discord.chat.inbox.saved_error = None;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SAVED,
            json!({ "action": "state" }),
            |s, res: Result<DiscordSavedReply, String>| {
                s.discord.chat.inbox.saved_loading = false;
                match res {
                    Ok(rep) if rep.ok => {
                        if let Some(ids) = rep.bookmark_ids {
                            s.discord.chat.inbox.bookmark_ids = ids.into_iter().collect();
                        }
                        if let Some(rems) = rep.reminders {
                            s.discord.chat.inbox.reminders = rems;
                        }
                        s.discord.chat.inbox.saved_places.extend(rep.places);
                    }
                    Ok(rep) => {
                        s.discord.chat.inbox.saved_error = Some(
                            rep.message
                                .unwrap_or_else(|| "Discord didn't send what you saved.".into()),
                        );
                    }
                    Err(e) => s.discord.chat.inbox.saved_error = Some(e),
                }
            },
        );
    }

    pub fn load_discord_saved_bookmarks(&mut self, before: Option<&str>) {
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.chat.inbox.saved_loading = true;
        self.discord.chat.inbox.saved_error = None;
        #[cfg(windows)]
        {
            let append = before.is_some();
            let mut params = json!({ "action": "bookmarks" });
            if let Some(b) = before {
                params["before"] = json!(b);
            }
            self.call_host_async(
                METHOD_DISCORD_SAVED,
                params,
                move |s, res: Result<DiscordSavedReply, String>| {
                    s.discord.chat.inbox.saved_loading = false;
                    match res {
                        Ok(rep) if rep.ok => {
                            let page = rep.results.unwrap_or_default();
                            for item in &page {
                                s.discord
                                    .chat
                                    .inbox
                                    .bookmark_ids
                                    .insert(item.save_data.message_id.clone());
                            }
                            if append {
                                s.discord.chat.inbox.bookmarks.extend(page);
                            } else {
                                s.discord.chat.inbox.bookmarks = page;
                            }
                            s.discord.chat.inbox.saved_places.extend(rep.places);
                        }
                        Ok(rep) => {
                            s.discord.chat.inbox.saved_error =
                                Some(rep.message.unwrap_or_else(|| {
                                    "Discord didn't send what you saved.".into()
                                }));
                        }
                        Err(e) => s.discord.chat.inbox.saved_error = Some(e),
                    }
                },
            );
        }
    }

    pub fn save_discord_message(
        &mut self,
        channel_id: &str,
        message_id: &str,
        due_at_ms: Option<i64>,
    ) {
        if due_at_ms.is_none() {
            self.discord
                .chat
                .inbox
                .bookmark_ids
                .insert(message_id.to_string());
        }
        if self.status != HostStatus::Connected {
            self.notice = Some(if due_at_ms.is_some() {
                "Reminder set".into()
            } else {
                "Message bookmarked".into()
            });
            return;
        }
        #[cfg(windows)]
        {
            let mut params = json!({
                "action": "save",
                "channelId": channel_id,
                "messageId": message_id,
            });
            if let Some(due) = due_at_ms {
                params["dueAt"] = json!(due);
            }
            let is_rem = due_at_ms.is_some();
            self.call_host_async(
                METHOD_DISCORD_SAVED,
                params,
                move |s, res: Result<DiscordSavedReply, String>| match res {
                    Ok(rep) if rep.ok => {
                        s.notice = Some(if is_rem {
                            "Reminder set".into()
                        } else {
                            "Message bookmarked".into()
                        });
                    }
                    Ok(rep) => {
                        s.notice = Some(
                            rep.message
                                .unwrap_or_else(|| "Discord didn't save that message.".into()),
                        );
                    }
                    Err(e) => s.notice = Some(e),
                },
            );
        }
    }

    pub fn unsave_discord_message(&mut self, channel_id: &str, message_id: &str) {
        self.discord.chat.inbox.bookmark_ids.remove(message_id);
        self.discord
            .chat
            .inbox
            .bookmarks
            .retain(|b| b.save_data.message_id != message_id);
        self.discord
            .chat
            .inbox
            .reminders
            .retain(|r| r.save_data.message_id != message_id);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SAVED,
            json!({
                "action": "remove",
                "channelId": channel_id,
                "messageId": message_id,
            }),
            |s, res: Result<DiscordSavedReply, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                {
                    s.notice = Some(
                        rep.message
                            .unwrap_or_else(|| "Discord didn't remove that saved message.".into()),
                    );
                }
            },
        );
    }

    pub fn open_create_dm_dialog(&mut self) {
        self.discord.chat.create_dm = Some(CreateDmState {
            query: TextEdit::default(),
            picked: Vec::new(),
            busy: false,
            error: None,
        });
    }

    pub fn toggle_create_dm_friend(&mut self, friend_id: &str) {
        let Some(ref mut dlg) = self.discord.chat.create_dm else {
            return;
        };
        if let Some(pos) = dlg.picked.iter().position(|id| id == friend_id) {
            dlg.picked.remove(pos);
        } else if dlg.picked.len() < 9 {
            dlg.picked.push(friend_id.to_string());
        }
    }

    pub fn submit_create_dm(&mut self) {
        let Some(picked) = self
            .discord
            .chat
            .create_dm
            .as_ref()
            .filter(|d| !d.busy && !d.picked.is_empty())
            .map(|d| d.picked.clone())
        else {
            return;
        };
        if picked.len() == 1
            && let Some(existing_dm) = self
                .discord
                .friends
                .iter()
                .find(|f| f.id == picked[0])
                .and_then(|f| f.dm_id.clone())
        {
            self.discord.chat.create_dm = None;
            self.open_discord_dm(&existing_dm);
            return;
        }
        if self.status != HostStatus::Connected {
            self.discord.chat.create_dm = None;
            self.notice = Some("Created DM".into());
            return;
        }
        if let Some(ref mut dlg) = self.discord.chat.create_dm {
            dlg.busy = true;
            dlg.error = None;
        }
        #[cfg(windows)]
        {
            let params = if picked.len() == 1 {
                json!({ "action": "dm", "userId": picked[0] })
            } else {
                json!({ "action": "dm", "userIds": picked })
            };
            self.call_host_async(
                METHOD_DISCORD_FRIEND,
                params,
                |s, res: Result<DiscordFriendActionReply, String>| match res {
                    Ok(rep) if rep.ok && rep.channel_id.is_some() => {
                        let cid = rep.channel_id.unwrap();
                        s.discord.chat.create_dm = None;
                        s.refresh_discord_home();
                        s.open_discord_dm(&cid);
                    }
                    Ok(rep) => {
                        if let Some(ref mut dlg) = s.discord.chat.create_dm {
                            dlg.busy = false;
                            dlg.error = Some(
                                rep.message
                                    .unwrap_or_else(|| "Discord didn't make that DM.".into()),
                            );
                        }
                    }
                    Err(e) => {
                        if let Some(ref mut dlg) = s.discord.chat.create_dm {
                            dlg.busy = false;
                            dlg.error = Some(e);
                        }
                    }
                },
            );
        }
    }

    pub fn open_invite_dialog(
        &mut self,
        guild_name: &str,
        channel_id: &str,
        channel_name: Option<&str>,
    ) {
        self.discord.chat.invite_dialog = Some(InviteDialogState {
            guild_name: guild_name.to_string(),
            channel_id: channel_id.to_string(),
            channel_name: channel_name.map(str::to_string),
            url: None,
            max_age: 604_800,
            max_uses: 0,
            temporary: false,
            editing: false,
            query: TextEdit::default(),
            sent_to: HashSet::new(),
            busy: self.status == HostStatus::Connected,
            copied: false,
            error: None,
        });
        if self.status != HostStatus::Connected {
            if let Some(ref mut dlg) = self.discord.chat.invite_dialog {
                dlg.url = Some("https://discord.gg/exo-demo".into());
                dlg.busy = false;
            }
            return;
        }
        self.generate_invite_link();
    }

    pub fn generate_invite_link(&mut self) {
        let Some((cid, max_age, max_uses, temporary)) = self
            .discord
            .chat
            .invite_dialog
            .as_ref()
            .map(|d| (d.channel_id.clone(), d.max_age, d.max_uses, d.temporary))
        else {
            return;
        };
        if let Some(ref mut dlg) = self.discord.chat.invite_dialog {
            dlg.busy = true;
            dlg.error = None;
            dlg.copied = false;
        }
        if self.status != HostStatus::Connected {
            if let Some(ref mut dlg) = self.discord.chat.invite_dialog {
                dlg.busy = false;
                dlg.editing = false;
                dlg.url = Some("https://discord.gg/exo-demo".into());
            }
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CHANNEL,
            json!({
                "channelId": cid,
                "action": "invite",
                "maxAge": max_age,
                "maxUses": max_uses,
                "temporary": temporary,
            }),
            |s, res: Result<DiscordChannelInviteReply, String>| {
                let Some(ref mut dlg) = s.discord.chat.invite_dialog else {
                    return;
                };
                dlg.busy = false;
                match res {
                    Ok(rep) if rep.ok && rep.url.is_some() => {
                        dlg.url = rep.url;
                        dlg.editing = false;
                        dlg.error = None;
                    }
                    Ok(rep) => {
                        dlg.error = Some(
                            rep.message
                                .unwrap_or_else(|| "Discord didn't make an invite.".into()),
                        );
                    }
                    Err(e) => dlg.error = Some(e),
                }
            },
        );
    }

    pub fn send_invite_to_friend(&mut self, friend_id: &str) {
        let Some(url) = self
            .discord
            .chat
            .invite_dialog
            .as_ref()
            .and_then(|d| d.url.clone())
        else {
            return;
        };
        if let Some(ref mut dlg) = self.discord.chat.invite_dialog {
            dlg.sent_to.insert(friend_id.to_string());
        }
        if self.status != HostStatus::Connected {
            return;
        }
        let existing_dm = self
            .discord
            .friends
            .iter()
            .find(|f| f.id == friend_id)
            .and_then(|f| f.dm_id.clone());
        #[cfg(windows)]
        if let Some(dm_id) = existing_dm {
            self.call_host_async(
                METHOD_DISCORD_SEND,
                json!({ "channelId": dm_id, "content": url }),
                |_s, _res: Result<DiscordSendReply, String>| {},
            );
        } else {
            let fid = friend_id.to_string();
            self.call_host_async(
                METHOD_DISCORD_FRIEND,
                json!({ "action": "dm", "userId": fid }),
                move |s, res: Result<DiscordFriendActionReply, String>| {
                    if let Ok(rep) = res
                        && let Some(dm_id) = rep.channel_id
                    {
                        s.call_host_async(
                            METHOD_DISCORD_SEND,
                            json!({ "channelId": dm_id, "content": url }),
                            |_s, _res: Result<DiscordSendReply, String>| {},
                        );
                    }
                },
            );
        }
    }

    pub fn open_forward_dialog(&mut self, channel_id: &str, message_id: &str) {
        self.discord.chat.forward_dialog = Some(ForwardDialogState {
            source_channel_id: channel_id.to_string(),
            source_message_id: message_id.to_string(),
            query: TextEdit::default(),
            picked: Vec::new(),
            note: TextEdit::default(),
            busy: false,
            error: None,
        });
    }

    pub fn toggle_forward_target(&mut self, channel_id: &str) {
        let Some(ref mut dlg) = self.discord.chat.forward_dialog else {
            return;
        };
        if let Some(pos) = dlg.picked.iter().position(|id| id == channel_id) {
            dlg.picked.remove(pos);
        } else if dlg.picked.len() < 5 {
            dlg.picked.push(channel_id.to_string());
        }
    }

    pub fn submit_forward_dialog(&mut self) {
        let Some((src_cid, src_mid, targets, note)) =
            self.discord.chat.forward_dialog.as_ref().and_then(|d| {
                (!d.busy && !d.picked.is_empty()).then(|| {
                    (
                        d.source_channel_id.clone(),
                        d.source_message_id.clone(),
                        d.picked.clone(),
                        d.note.text.trim().to_string(),
                    )
                })
            })
        else {
            return;
        };
        if self.status != HostStatus::Connected {
            self.discord.chat.forward_dialog = None;
            self.notice = Some("Message forwarded".into());
            return;
        }
        if let Some(ref mut dlg) = self.discord.chat.forward_dialog {
            dlg.busy = true;
            dlg.error = None;
        }
        #[cfg(windows)]
        {
            let count = targets.len();
            for (idx, target_cid) in targets.into_iter().enumerate() {
                let is_last = idx + 1 == count;
                self.call_host_async(
                    METHOD_DISCORD_SEND,
                    json!({
                        "channelId": target_cid,
                        "content": note,
                        "forward": { "channelId": src_cid, "messageId": src_mid },
                    }),
                    move |s, res: Result<DiscordSendReply, String>| match res {
                        Ok(rep) if rep.ok => {
                            if is_last {
                                s.discord.chat.forward_dialog = None;
                                s.notice = Some("Message forwarded".into());
                            }
                        }
                        Ok(rep) => {
                            if let Some(ref mut dlg) = s.discord.chat.forward_dialog {
                                dlg.busy = false;
                                dlg.error = Some(
                                    rep.message
                                        .unwrap_or_else(|| "Discord didn't forward that.".into()),
                                );
                            }
                        }
                        Err(e) => {
                            if let Some(ref mut dlg) = s.discord.chat.forward_dialog {
                                dlg.busy = false;
                                dlg.error = Some(e);
                            }
                        }
                    },
                );
            }
        }
    }

    pub fn open_new_thread_dialog(&mut self, channel_id: &str, message_id: &str, preview: &str) {
        let mut name = TextEdit::default();
        let trimmed: String = preview
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ")
            .chars()
            .take(40)
            .collect();
        name.text = if trimmed.is_empty() {
            "New thread".into()
        } else {
            trimmed
        };
        self.discord.chat.new_thread_dialog = Some(NewThreadDialogState {
            channel_id: channel_id.to_string(),
            message_id: message_id.to_string(),
            name,
            busy: false,
            error: None,
        });
    }

    pub fn submit_new_thread_dialog(&mut self) {
        let Some((cid, mid, name)) = self.discord.chat.new_thread_dialog.as_ref().and_then(|d| {
            let trimmed = d.name.text.trim();
            (!d.busy && !trimmed.is_empty()).then(|| {
                (
                    d.channel_id.clone(),
                    d.message_id.clone(),
                    trimmed.to_string(),
                )
            })
        }) else {
            return;
        };
        if self.status != HostStatus::Connected {
            self.discord.chat.new_thread_dialog = None;
            self.notice = Some(format!("Created thread {name}"));
            return;
        }
        if let Some(ref mut dlg) = self.discord.chat.new_thread_dialog {
            dlg.busy = true;
            dlg.error = None;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_THREAD,
            json!({ "channelId": cid, "name": name, "messageId": mid }),
            |s, res: Result<DiscordThreadCreateReply, String>| match res {
                Ok(rep) if rep.ok && rep.thread.is_some() => {
                    let thread = rep.thread.unwrap();
                    s.discord.chat.new_thread_dialog = None;
                    s.notice = Some(format!("Created thread {}", thread.name));
                    s.open_discord_channel(&thread.id);
                }
                Ok(rep) => {
                    if let Some(ref mut dlg) = s.discord.chat.new_thread_dialog {
                        dlg.busy = false;
                        dlg.error = Some(
                            rep.message
                                .unwrap_or_else(|| "Discord didn't start the thread.".into()),
                        );
                    }
                }
                Err(e) => {
                    if let Some(ref mut dlg) = s.discord.chat.new_thread_dialog {
                        dlg.busy = false;
                        dlg.error = Some(e);
                    }
                }
            },
        );
    }

    pub fn open_report_message_dialog(
        &mut self,
        channel_id: &str,
        message_id: &str,
        author_name: &str,
        preview: &str,
    ) {
        self.discord.chat.report_dialog = Some(ReportDialogState {
            channel_id: channel_id.to_string(),
            message_id: message_id.to_string(),
            author_name: author_name.to_string(),
            preview: preview.chars().take(180).collect(),
            selected_reason: "Spam or unwanted content".into(),
            busy: false,
            done: false,
            error: None,
        });
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_REPORT,
            json!({ "kind": "message", "action": "menu" }),
            |_s, _res: Result<DiscordReportReply, String>| {},
        );
    }

    pub fn submit_report_message(&mut self) {
        let Some((cid, mid, reason)) = self.discord.chat.report_dialog.as_ref().and_then(|d| {
            (!d.busy && !d.done).then(|| {
                (
                    d.channel_id.clone(),
                    d.message_id.clone(),
                    d.selected_reason.clone(),
                )
            })
        }) else {
            return;
        };
        if self.status != HostStatus::Connected {
            if let Some(ref mut dlg) = self.discord.chat.report_dialog {
                dlg.done = true;
            }
            return;
        }
        if let Some(ref mut dlg) = self.discord.chat.report_dialog {
            dlg.busy = true;
            dlg.error = None;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_REPORT,
            json!({
                "kind": "message",
                "action": "send",
                "version": "1.0",
                "variant": "3",
                "language": "en-US",
                "breadcrumbs": [1],
                "elements": { "reason": [reason] },
                "ids": { "channel_id": cid, "message_id": mid },
            }),
            |s, res: Result<DiscordReportReply, String>| {
                let Some(ref mut dlg) = s.discord.chat.report_dialog else {
                    return;
                };
                dlg.busy = false;
                match res {
                    Ok(rep) if rep.ok => {
                        dlg.done = true;
                        dlg.error = None;
                    }
                    Ok(rep) => {
                        dlg.error = Some(
                            rep.message
                                .unwrap_or_else(|| "Discord didn't submit that report.".into()),
                        );
                    }
                    Err(e) => dlg.error = Some(e),
                }
            },
        );
    }

    pub fn open_user_profile_dialog(&mut self, req: ProfileOpenRequest<'_>) {
        let cache_key = format!("{}:{}", req.user_id, req.guild_id.unwrap_or(""));
        let cached = self.discord.chat.dm_profile_cache.get(&cache_key).cloned();
        let mut note_edit = TextEdit::default();
        let saved_note = cached
            .as_ref()
            .and_then(|c| c.note.clone())
            .unwrap_or_default();
        note_edit.text = saved_note.clone();

        self.discord.chat.profile_dialog = Some(ProfileDialogState {
            user_id: req.user_id.to_string(),
            guild_id: req.guild_id.map(str::to_string),
            full: req.full,
            tab: req.tab,
            fallback_name: req.fallback_name.to_string(),
            fallback_username: req.fallback_username.to_string(),
            fallback_avatar: req.fallback_avatar.map(str::to_string),
            profile: cached.as_ref().and_then(|c| c.profile.clone()),
            extras: cached.as_ref().and_then(|c| c.extras.clone()),
            note: note_edit,
            saved_note,
            note_error: None,
            mutual_friends: None,
            role_person: None,
            role_picker_open: false,
            role_query: TextEdit::default(),
            role_busy: false,
            role_error: None,
            loading: cached.is_none() && self.status == HostStatus::Connected,
            error: None,
        });
        self.fetch_user_profile(req.user_id, req.guild_id);
        if req.full {
            self.fetch_mutual_friends(req.user_id);
        }
        if let Some(gid) = req.guild_id {
            self.fetch_role_person(gid, req.user_id);
        }
    }

    pub fn fetch_user_profile(&mut self, user_id: &str, guild_id: Option<&str>) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let uid = user_id.to_string();
            let gid = guild_id.map(str::to_string);
            let cache_key = format!("{uid}:{}", gid.as_deref().unwrap_or(""));
            let mut params = json!({ "userId": uid });
            if let Some(ref g) = gid {
                params["guildId"] = json!(g);
            }
            self.call_host_async(
                METHOD_DISCORD_PROFILE,
                params,
                move |s, res: Result<DiscordProfileReply, String>| match res {
                    Ok(rep) if rep.ok && rep.profile.is_some() => {
                        s.discord
                            .chat
                            .dm_profile_cache
                            .insert(cache_key, rep.clone());
                        if let Some(ref mut dlg) = s.discord.chat.profile_dialog
                            && dlg.user_id == uid
                        {
                            dlg.loading = false;
                            dlg.profile = rep.profile;
                            dlg.extras = rep.extras;
                            let note_s = rep.note.unwrap_or_default();
                            dlg.saved_note = note_s.clone();
                            dlg.note.text = note_s;
                            dlg.error = None;
                        }
                    }
                    Ok(rep) => {
                        if let Some(ref mut dlg) = s.discord.chat.profile_dialog
                            && dlg.user_id == uid
                        {
                            dlg.loading = false;
                            if dlg.profile.is_none() {
                                dlg.error =
                                    Some(rep.message.unwrap_or_else(|| {
                                        "Discord didn't send this profile.".into()
                                    }));
                            }
                        }
                    }
                    Err(e) => {
                        if let Some(ref mut dlg) = s.discord.chat.profile_dialog
                            && dlg.user_id == uid
                        {
                            dlg.loading = false;
                            if dlg.profile.is_none() {
                                dlg.error = Some(e);
                            }
                        }
                    }
                },
            );
        }
    }

    pub fn fetch_mutual_friends(&mut self, user_id: &str) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let uid = user_id.to_string();
            self.call_host_async(
                METHOD_DISCORD_PROFILE,
                json!({ "userId": uid, "mutualFriends": true }),
                move |s, res: Result<DiscordProfileReply, String>| {
                    if let Some(ref mut dlg) = s.discord.chat.profile_dialog
                        && dlg.user_id == uid
                    {
                        dlg.mutual_friends =
                            Some(res.ok().and_then(|r| r.friends).unwrap_or_default());
                    }
                },
            );
        }
    }

    pub fn save_profile_note(&mut self) {
        let Some((uid, note)) = self.discord.chat.profile_dialog.as_ref().and_then(|d| {
            (d.note.text != d.saved_note).then(|| (d.user_id.clone(), d.note.text.clone()))
        }) else {
            return;
        };
        if self.status != HostStatus::Connected {
            if let Some(ref mut dlg) = self.discord.chat.profile_dialog {
                dlg.saved_note = note;
            }
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_PROFILE,
            json!({ "userId": uid, "note": note }),
            move |s, res: Result<ActionResult, String>| {
                if let Some(ref mut dlg) = s.discord.chat.profile_dialog
                    && dlg.user_id == uid
                {
                    match res {
                        Ok(rep) if rep.ok => {
                            dlg.saved_note = note;
                            dlg.note_error = None;
                        }
                        Ok(rep) => {
                            dlg.note_error = Some(
                                rep.message
                                    .unwrap_or_else(|| "Discord didn't save your note.".into()),
                            );
                        }
                        Err(e) => dlg.note_error = Some(e),
                    }
                }
            },
        );
    }

    pub fn fetch_role_person(&mut self, guild_id: &str, user_id: &str) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let gid = guild_id.to_string();
            let uid = user_id.to_string();
            self.call_host_async(
                METHOD_DISCORD_ADMIN,
                json!({ "action": "person", "guildId": gid, "userId": uid }),
                move |s, res: Result<DiscordAdminReply, String>| {
                    if let Ok(rep) = res
                        && rep.ok
                        && let Some(data) = rep.data
                        && let Ok(person) = serde_json::from_value::<DiscordRolePerson>(data)
                        && person.ok
                        && person.known
                        && let Some(ref mut dlg) = s.discord.chat.profile_dialog
                        && dlg.user_id == uid
                    {
                        dlg.role_person = Some(person);
                    }
                },
            );
        }
    }

    pub fn change_profile_role(&mut self, role_id: &str, add: bool) {
        let Some((gid, uid)) = self.discord.chat.profile_dialog.as_ref().and_then(|d| {
            (!d.role_busy)
                .then(|| d.guild_id.clone().map(|g| (g, d.user_id.clone())))
                .flatten()
        }) else {
            return;
        };
        if let Some(ref mut dlg) = self.discord.chat.profile_dialog {
            dlg.role_busy = true;
            dlg.role_error = None;
        }
        if self.status != HostStatus::Connected {
            if let Some(ref mut dlg) = self.discord.chat.profile_dialog {
                dlg.role_busy = false;
                if let Some(ref mut rp) = dlg.role_person {
                    if add {
                        if !rp.roles.iter().any(|r| r == role_id) {
                            rp.roles.push(role_id.to_string());
                        }
                    } else {
                        rp.roles.retain(|r| r != role_id);
                    }
                }
            }
            return;
        }
        #[cfg(windows)]
        {
            let rid = role_id.to_string();
            self.call_host_async(
                METHOD_DISCORD_ADMIN,
                json!({
                    "action": if add { "addRole" } else { "removeRole" },
                    "guildId": gid,
                    "userId": uid,
                    "roleId": rid,
                }),
                move |s, res: Result<DiscordAdminReply, String>| {
                    let Some(ref mut dlg) = s.discord.chat.profile_dialog else {
                        return;
                    };
                    dlg.role_busy = false;
                    match res {
                        Ok(rep) if rep.ok => {
                            if let Some(ref mut rp) = dlg.role_person {
                                if add {
                                    if !rp.roles.iter().any(|r| r == &rid) {
                                        rp.roles.push(rid);
                                    }
                                    dlg.role_picker_open = false;
                                    dlg.role_query = TextEdit::default();
                                } else {
                                    rp.roles.retain(|r| r != &rid);
                                }
                            }
                            dlg.role_error = None;
                        }
                        Ok(rep) => {
                            dlg.role_error = Some(
                                rep.message
                                    .unwrap_or_else(|| "Discord didn't change that role.".into()),
                            );
                        }
                        Err(e) => dlg.role_error = Some(e),
                    }
                },
            );
        }
    }

    pub fn ensure_channel_commands_loaded(&mut self, channel_id: &str) {
        if self
            .discord
            .chat
            .apps
            .commands_by_channel
            .contains_key(channel_id)
            || self.discord.chat.apps.loading_channel.as_deref() == Some(channel_id)
            || self.status != HostStatus::Connected
        {
            return;
        }
        self.discord.chat.apps.loading_channel = Some(channel_id.to_string());
        #[cfg(windows)]
        {
            let cid = channel_id.to_string();
            self.call_host_async(
                METHOD_DISCORD_APPS,
                json!({ "channelId": cid, "action": "commands" }),
                move |s, res: Result<DiscordAppsReply, String>| {
                    if s.discord.chat.apps.loading_channel.as_deref() == Some(cid.as_str()) {
                        s.discord.chat.apps.loading_channel = None;
                    }
                    if let Ok(rep) = res
                        && rep.ok
                    {
                        s.discord
                            .chat
                            .apps
                            .commands_by_channel
                            .insert(cid, (rep.commands, rep.applications));
                    }
                },
            );
        }
    }

    pub fn select_slash_command(&mut self, entry: SlashCommandEntry) {
        let required: Vec<String> = entry
            .options
            .iter()
            .filter(|o| o.required)
            .map(|o| o.name.clone())
            .collect();
        let focused = required.first().cloned();
        self.discord.chat.composer = TextEdit::default();
        self.discord.chat.apps.active_command = Some(ActiveSlashCommand {
            entry,
            used_options: required,
            values: HashMap::new(),
            bool_values: HashMap::new(),
            focused_option: focused,
            autocomplete_choices: Vec::new(),
            busy: false,
            error: None,
        });
    }

    pub fn run_active_slash_command(&mut self) {
        let Some(cid) = self.active_chat_channel_id() else {
            return;
        };
        let parsed = {
            let Some(cmd) = self.discord.chat.apps.active_command.as_ref() else {
                return;
            };
            if cmd.busy {
                return;
            }
            let mut options = Vec::new();
            let mut err: Option<String> = None;
            for opt in &cmd.entry.options {
                if !cmd.used_options.contains(&opt.name) {
                    continue;
                }
                if opt.kind == 5 {
                    if let Some(&b) = cmd.bool_values.get(&opt.name) {
                        options.push(json!({ "name": opt.name, "value": b }));
                    } else if opt.required {
                        err = Some(format!("Fill in {} first.", opt.name));
                        break;
                    }
                } else {
                    let raw = cmd
                        .values
                        .get(&opt.name)
                        .map(|t| t.text.trim().to_string())
                        .unwrap_or_default();
                    if raw.is_empty() {
                        if opt.required {
                            err = Some(format!("Fill in {} first.", opt.name));
                            break;
                        }
                        continue;
                    }
                    let val = match opt.kind {
                        4 => raw.parse::<i64>().map(|n| json!(n)).unwrap_or(json!(raw)),
                        10 => raw.parse::<f64>().map(|n| json!(n)).unwrap_or(json!(raw)),
                        _ => json!(raw),
                    };
                    options.push(json!({ "name": opt.name, "value": val }));
                }
            }
            if let Some(msg) = err {
                Err(msg)
            } else {
                Ok((
                    cmd.entry.command.id.clone(),
                    cmd.entry.path.clone(),
                    options,
                ))
            }
        };
        let (command_id, path, options) = match parsed {
            Ok(v) => v,
            Err(msg) => {
                if let Some(ref mut c) = self.discord.chat.apps.active_command {
                    c.error = Some(msg);
                }
                return;
            }
        };
        if self.status != HostStatus::Connected {
            self.discord.chat.apps.active_command = None;
            self.discord.chat.apps.app_status = Some("Command sent".into());
            return;
        }
        if let Some(ref mut c) = self.discord.chat.apps.active_command {
            c.busy = true;
            c.error = None;
        }
        self.discord.chat.apps.app_status = Some("Sending command…".into());
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_APPS,
            json!({
                "channelId": cid,
                "action": "run",
                "commandId": command_id,
                "path": path,
                "options": options,
            }),
            |s, res: Result<DiscordAppsReply, String>| match res {
                Ok(rep) if rep.ok => {
                    s.discord.chat.apps.active_command = None;
                    if let Some(nonce) = rep.nonce {
                        s.discord
                            .chat
                            .apps
                            .pending_nonces
                            .insert(nonce, "command".into());
                    } else {
                        s.discord.chat.apps.app_status = None;
                    }
                }
                Ok(rep) => {
                    s.discord.chat.apps.app_status = None;
                    if let Some(ref mut c) = s.discord.chat.apps.active_command {
                        c.busy = false;
                        c.error = Some(
                            rep.message
                                .unwrap_or_else(|| "The application did not respond.".into()),
                        );
                    }
                }
                Err(e) => {
                    s.discord.chat.apps.app_status = None;
                    if let Some(ref mut c) = s.discord.chat.apps.active_command {
                        c.busy = false;
                        c.error = Some(e);
                    }
                }
            },
        );
    }

    pub fn press_message_component(&mut self, req: ComponentPress<'_>) {
        if self.discord.chat.apps.component_busy.is_some() {
            return;
        }
        self.discord.chat.apps.open_select_id = None;
        self.discord.chat.apps.component_error = None;
        if self.status != HostStatus::Connected {
            self.notice = Some(format!("Pressed component {}", req.custom_id));
            return;
        }
        self.discord.chat.apps.component_busy = Some(req.custom_id.to_string());
        #[cfg(windows)]
        {
            let mut params = json!({
                "channelId": req.channel_id,
                "action": "component",
                "messageId": req.message_id,
                "applicationId": req.application_id,
                "messageFlags": req.message_flags,
                "componentType": req.component_type,
                "customId": req.custom_id,
            });
            if let Some(v) = req.values {
                params["values"] = json!(v);
            }
            self.call_host_async(
                METHOD_DISCORD_APPS,
                params,
                |s, res: Result<DiscordAppsReply, String>| match res {
                    Ok(rep) if rep.ok => {
                        if let Some(nonce) = rep.nonce {
                            s.discord
                                .chat
                                .apps
                                .pending_nonces
                                .insert(nonce, "component".into());
                        } else {
                            s.discord.chat.apps.component_busy = None;
                        }
                    }
                    Ok(rep) => {
                        s.discord.chat.apps.component_busy = None;
                        s.discord.chat.apps.component_error = Some(
                            rep.message
                                .unwrap_or_else(|| "This interaction failed".into()),
                        );
                    }
                    Err(e) => {
                        s.discord.chat.apps.component_busy = None;
                        s.discord.chat.apps.component_error = Some(e);
                    }
                },
            );
        }
    }

    pub fn dismiss_ephemeral_message(&mut self, channel_id: &str, message_id: &str) {
        if let Some(list) = self.discord.chat.messages_by_channel.get_mut(channel_id) {
            list.retain(|m| m.id != message_id);
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_APPS,
            json!({ "channelId": channel_id, "action": "dismiss", "messageId": message_id }),
            |_s, _res: Result<DiscordAppsReply, String>| {},
        );
    }

    pub fn submit_active_app_modal(&mut self) {
        let Some((channel_id, interaction_id, answers)) =
            self.discord.chat.apps.active_modal.as_ref().and_then(|m| {
                (!m.busy).then(|| {
                    let mut map = serde_json::Map::new();
                    for (k, v) in &m.text_values {
                        map.insert(k.clone(), json!({ "value": v.text }));
                    }
                    for (k, v) in &m.choice_values {
                        map.insert(k.clone(), json!({ "values": v }));
                    }
                    for (k, &v) in &m.bool_values {
                        map.insert(k.clone(), json!({ "checked": v }));
                    }
                    (
                        m.modal.channel_id.clone(),
                        m.modal.id.clone(),
                        Value::Object(map),
                    )
                })
            })
        else {
            return;
        };
        if self.status != HostStatus::Connected {
            self.discord.chat.apps.active_modal = None;
            return;
        }
        if let Some(ref mut m) = self.discord.chat.apps.active_modal {
            m.busy = true;
            m.error = None;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_APPS,
            json!({
                "channelId": channel_id,
                "action": "modal",
                "interactionId": interaction_id,
                "answers": answers,
            }),
            |s, res: Result<DiscordAppsReply, String>| match res {
                Ok(rep) if rep.ok => {
                    if let Some(nonce) = rep.nonce {
                        s.discord
                            .chat
                            .apps
                            .pending_nonces
                            .insert(nonce, "modal".into());
                    } else {
                        s.discord.chat.apps.active_modal = None;
                    }
                }
                Ok(rep) => {
                    if let Some(ref mut m) = s.discord.chat.apps.active_modal {
                        m.busy = false;
                        m.error = Some(
                            rep.message
                                .unwrap_or_else(|| "Discord didn't send the form.".into()),
                        );
                    }
                }
                Err(e) => {
                    if let Some(ref mut m) = s.discord.chat.apps.active_modal {
                        m.busy = false;
                        m.error = Some(e);
                    }
                }
            },
        );
    }

    pub fn begin_edit_discord_message(&mut self, message_id: &str, content: &str) {
        let mut edit = TextEdit::default();
        edit.text = content.to_string();
        let end = edit.text.len();
        edit.anchor = end;
        edit.focus = end;
        self.discord.chat.editing = Some((message_id.to_string(), edit));
    }

    pub fn begin_edit_last_own_message(&mut self) {
        let Some(cid) = self.active_chat_channel_id() else {
            return;
        };
        let Some(me_id) = self.discord.home.me.as_ref().map(|m| m.id.clone()) else {
            return;
        };
        let target = self
            .discord
            .chat
            .messages_by_channel
            .get(&cid)
            .and_then(|list| {
                list.iter()
                    .rev()
                    .find(|m| m.author.id == me_id && !m.pending)
                    .map(|m| (m.id.clone(), m.content.clone()))
            });
        if let Some((mid, content)) = target {
            self.begin_edit_discord_message(&mid, &content);
        }
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

    pub fn discord_message_special_action(
        &mut self,
        channel_id: &str,
        message_id: &str,
        action: &str,
        mine: bool,
        flags: u32,
    ) {
        if self.status != HostStatus::Connected {
            self.notice = Some(format!("Action {action} applied"));
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_MESSAGE_ACTION,
            json!({
                "channelId": channel_id,
                "messageId": message_id,
                "action": action,
                "mine": mine,
                "flags": flags,
            }),
            |s, res: Result<ActionResult, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                {
                    s.notice = Some(
                        rep.message
                            .unwrap_or_else(|| "Discord didn't take that action.".into()),
                    );
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
        self.toggle_discord_reaction_with_burst(
            channel_id,
            message_id,
            emoji_key,
            currently_me,
            false,
        );
    }

    pub fn toggle_discord_reaction_with_burst(
        &mut self,
        channel_id: &str,
        message_id: &str,
        emoji_key: &str,
        currently_me: bool,
        burst: bool,
    ) {
        self.discord.chat.emoji_picker_for = None;
        self.discord.chat.picker.open_for_reaction = None;
        let add = !currently_me;
        if add {
            self.note_emoji_use(emoji_key, true);
        }
        if let Some(list) = self.discord.chat.messages_by_channel.get_mut(channel_id)
            && let Some(m) = list.iter_mut().find(|m| m.id == message_id)
        {
            if let Some(pos) = m.reactions.iter().position(|r| r.emoji.key() == emoji_key) {
                let r = &mut m.reactions[pos];
                let mut details = r
                    .count_details
                    .clone()
                    .unwrap_or(DiscordReactionCountDetails {
                        normal: r.count,
                        burst: 0,
                    });
                if burst {
                    if add && !r.me_burst {
                        details.burst += 1;
                        r.me_burst = true;
                    } else if !add {
                        details.burst = (details.burst - 1).max(0);
                        r.me_burst = false;
                    }
                } else if add && !r.me {
                    details.normal += 1;
                    r.me = true;
                } else if !add {
                    details.normal = (details.normal - 1).max(0);
                    r.me = false;
                }
                r.count = details.normal + details.burst;
                r.count_details = Some(details);
                if r.count == 0 {
                    m.reactions.remove(pos);
                }
            } else if add {
                let (name, id) = match emoji_key.split_once(':') {
                    Some((n, i)) => (Some(n.to_string()), Some(i.to_string())),
                    None => (Some(emoji_key.to_string()), None),
                };
                m.reactions.push(DiscordReaction {
                    count: 1,
                    me: !burst,
                    me_burst: burst,
                    count_details: Some(DiscordReactionCountDetails {
                        normal: i32::from(!burst),
                        burst: i32::from(burst),
                    }),
                    burst_colors: Vec::new(),
                    emoji: DiscordReactionEmoji {
                        id,
                        name,
                        animated: false,
                    },
                });
            }
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let mut params = json!({
                "channelId": channel_id,
                "messageId": message_id,
                "emoji": emoji_key,
                "add": add,
            });
            if burst {
                params["burst"] = json!(true);
            }
            self.call_host_async(
                METHOD_DISCORD_REACT,
                params,
                |_s, _res: Result<ActionResult, String>| {},
            );
        }
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
        if !pin {
            self.discord.chat.pins.retain(|m| m.id != message_id);
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
        self.discord.inbox_open = false;
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
            self.discord.inbox_open = false;
            self.discord.chat.drawer = ChatDrawer::Search;
        }
    }

    pub fn add_search_chip(&mut self, chip: SearchChip) {
        if matches!(
            chip.key,
            SearchFilterKey::Before
                | SearchFilterKey::After
                | SearchFilterKey::During
                | SearchFilterKey::Pinned
        ) {
            self.discord.chat.search.chips.retain(|c| c.key != chip.key);
        }
        self.discord.chat.search.chips.push(chip);
        self.discord.chat.search.offset = 0;
        self.run_discord_search();
    }

    pub fn remove_search_chip(&mut self, idx: usize) {
        if idx < self.discord.chat.search.chips.len() {
            self.discord.chat.search.chips.remove(idx);
            self.discord.chat.search.offset = 0;
            self.run_discord_search();
        }
    }

    pub fn set_search_sort(&mut self, sort: SearchSort) {
        self.discord.chat.search.sort = sort;
        self.discord.chat.search.offset = 0;
        self.run_discord_search();
    }

    pub fn set_search_offset(&mut self, offset: usize) {
        self.discord.chat.search.offset = offset;
        self.run_discord_search();
    }

    pub fn run_discord_search(&mut self) {
        let Some(cid) = self.active_chat_channel_id() else {
            return;
        };
        let now_ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis() as i64)
            .unwrap_or(1_795_400_000_000);
        let raw_input = self.discord.chat.search_input.text.clone();
        let (extra_chips, remaining_text) = extract_inline_search_filters(&raw_input, now_ms);
        if !extra_chips.is_empty() {
            for chip in extra_chips {
                if matches!(
                    chip.key,
                    SearchFilterKey::Before
                        | SearchFilterKey::After
                        | SearchFilterKey::During
                        | SearchFilterKey::Pinned
                ) {
                    self.discord.chat.search.chips.retain(|c| c.key != chip.key);
                }
                self.discord.chat.search.chips.push(chip);
            }
            self.discord.chat.search_input.text = remaining_text.clone();
        }
        let query = remaining_text.trim().to_string();
        if query.is_empty() && self.discord.chat.search.chips.is_empty() {
            self.discord.chat.search_results.clear();
            self.discord.chat.search_total = 0;
            self.discord.chat.search.error = None;
            return;
        }
        self.discord.chat.search.show_filters = false;
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
                            low.is_empty()
                                || m.content.to_lowercase().contains(&low)
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
        let filters = build_search_filters_json(
            &self.discord.chat.search.chips,
            &query,
            self.discord.chat.search.sort,
            self.discord.chat.search.offset,
        );
        self.discord.chat.search_loading = true;
        self.discord.chat.search.error = None;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SEARCH,
            json!({
                "channelId": cid,
                "filters": filters,
            }),
            |s, res: Result<DiscordSearchReply, String>| {
                s.discord.chat.search_loading = false;
                match res {
                    Ok(rep) if rep.ok => {
                        if let Some(r) = rep.result {
                            s.discord.chat.search_total = r.total_results;
                            s.discord.chat.search_results = r
                                .messages
                                .into_iter()
                                .filter_map(|group| group.into_iter().next())
                                .collect();
                        }
                        s.discord.chat.search.places = rep.places;
                        s.discord.chat.search.error = None;
                    }
                    Ok(rep) => {
                        s.discord.chat.search.error = Some(
                            rep.message
                                .unwrap_or_else(|| "Discord didn't finish that search.".into()),
                        );
                    }
                    Err(e) => s.discord.chat.search.error = Some(e),
                }
            },
        );
    }

    pub fn attach_discord_files(&mut self, paths: &[PathBuf]) {
        self.discord.chat.drop_hover = false;
        let room = 10usize.saturating_sub(self.discord.chat.pending_uploads.len());
        for path in paths.iter().take(room) {
            let Ok(bytes) = std::fs::read(path) else {
                continue;
            };
            let name = path
                .file_name()
                .and_then(|n| n.to_str())
                .unwrap_or("attachment.bin")
                .to_string();
            let mime = guess_mime(&name);
            self.attach_raw_bytes(name, mime, bytes);
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

    pub fn handle_discord_message(&mut self, ev: DiscordMessageEvent) -> bool {
        const MAX_CACHED_CHANNELS: usize = 12;
        const MAX_MESSAGES_PER_CHANNEL: usize = 200;

        let cid = ev.channel_id;
        let is_active = self.room == Room::Discord
            && self.active_chat_channel_id().as_deref() == Some(cid.as_str());
        match ev.kind.as_str() {
            "create" => {
                let Some(msg) = ev.message else {
                    return false;
                };
                if self.room != Room::Discord
                    && !self.discord.chat.messages_by_channel.contains_key(&cid)
                {
                    return false;
                }
                let list = self
                    .discord
                    .chat
                    .messages_by_channel
                    .entry(cid.clone())
                    .or_default();
                if let Some(pos) = list.iter().position(|m| m.id == msg.id) {
                    list[pos] = msg;
                } else if let Some(ppos) = list.iter().position(|m| {
                    m.pending && m.author.id == msg.author.id && m.content == msg.content
                }) {
                    list[ppos] = msg;
                } else {
                    list.push(msg);
                }
                if list.len() > MAX_MESSAGES_PER_CHANNEL {
                    let excess = list.len() - MAX_MESSAGES_PER_CHANNEL;
                    list.drain(0..excess);
                }
                if self.discord.chat.messages_by_channel.len() > MAX_CACHED_CHANNELS {
                    let active = self.active_chat_channel_id();
                    let victim = self
                        .discord
                        .chat
                        .messages_by_channel
                        .keys()
                        .find(|k| {
                            k.as_str() != cid.as_str() && active.as_deref() != Some(k.as_str())
                        })
                        .cloned();
                    if let Some(k) = victim {
                        self.discord.chat.messages_by_channel.remove(&k);
                    }
                }
                self.discord.chat.typing_by_channel.remove(&cid);
                if is_active {
                    self.hydrate_message_cards(&cid);
                }
                is_active
            }
            "update" => {
                let Some(msg) = ev.message else {
                    return false;
                };
                if let Some(list) = self.discord.chat.messages_by_channel.get_mut(&cid)
                    && let Some(existing) = list.iter_mut().find(|m| m.id == msg.id)
                {
                    *existing = msg;
                    return is_active;
                }
                false
            }
            "delete" => {
                let Some(mid) = ev.id else {
                    return false;
                };
                if let Some(list) = self.discord.chat.messages_by_channel.get_mut(&cid) {
                    let before = list.len();
                    list.retain(|m| m.id != mid);
                    return is_active && list.len() != before;
                }
                false
            }
            _ => false,
        }
    }

    pub fn handle_discord_reaction(&mut self, ev: DiscordReactionEvent) -> bool {
        let is_active = self.room == Room::Discord
            && self.active_chat_channel_id().as_deref() == Some(ev.channel_id.as_str());
        let Some(list) = self
            .discord
            .chat
            .messages_by_channel
            .get_mut(&ev.channel_id)
        else {
            return false;
        };
        let Some(msg) = list.iter_mut().find(|m| m.id == ev.message_id) else {
            return false;
        };
        let key = ev.emoji.key();
        if let Some(pos) = msg.reactions.iter().position(|r| r.emoji.key() == key) {
            let r = &mut msg.reactions[pos];
            let mut details = r
                .count_details
                .clone()
                .unwrap_or(DiscordReactionCountDetails {
                    normal: r.count,
                    burst: 0,
                });
            let delta = if ev.add { 1 } else { -1 };
            if ev.burst {
                details.burst = (details.burst + delta).max(0);
                if ev.me {
                    r.me_burst = ev.add;
                }
                if !ev.burst_colors.is_empty() {
                    r.burst_colors = ev.burst_colors;
                }
            } else {
                details.normal = (details.normal + delta).max(0);
                if ev.me {
                    r.me = ev.add;
                }
            }
            r.count = details.normal + details.burst;
            r.count_details = Some(details);
            if r.count == 0 {
                msg.reactions.remove(pos);
            }
        } else if ev.add {
            msg.reactions.push(DiscordReaction {
                count: 1,
                me: !ev.burst && ev.me,
                me_burst: ev.burst && ev.me,
                count_details: Some(DiscordReactionCountDetails {
                    normal: i32::from(!ev.burst),
                    burst: i32::from(ev.burst),
                }),
                burst_colors: ev.burst_colors,
                emoji: ev.emoji,
            });
        }
        is_active
    }

    pub fn handle_discord_typing(&mut self, ev: DiscordTypingEvent) -> bool {
        const MAX_TYPING_CHANNELS: usize = 32;
        self.discord
            .chat
            .typing_by_channel
            .retain(|_, (_, at)| at.elapsed() < Duration::from_secs(10));
        if self
            .discord
            .home
            .me
            .as_ref()
            .is_some_and(|m| m.id == ev.user_id)
        {
            return false;
        }
        if self.room != Room::Discord {
            return false;
        }
        let is_active = self.active_chat_channel_id().as_deref() == Some(ev.channel_id.as_str());
        self.discord
            .chat
            .typing_by_channel
            .insert(ev.channel_id, (ev.name, Instant::now()));
        while self.discord.chat.typing_by_channel.len() > MAX_TYPING_CHANNELS {
            let oldest = self
                .discord
                .chat
                .typing_by_channel
                .iter()
                .min_by_key(|(_, (_, at))| *at)
                .map(|(k, _)| k.clone());
            match oldest {
                Some(k) => {
                    self.discord.chat.typing_by_channel.remove(&k);
                }
                None => break,
            }
        }
        is_active
    }

    pub fn handle_discord_poll_vote(&mut self, ev: DiscordPollVoteEvent) -> bool {
        let is_active = self.room == Room::Discord
            && self.active_chat_channel_id().as_deref() == Some(ev.channel_id.as_str());
        if let Some(list) = self
            .discord
            .chat
            .messages_by_channel
            .get_mut(&ev.channel_id)
            && let Some(msg) = list.iter_mut().find(|m| m.id == ev.message_id)
            && let Some(ref mut poll) = msg.poll
        {
            poll.apply_vote(ev.answer_id, ev.add, ev.me);
            return is_active;
        }
        false
    }

    pub fn handle_discord_interaction(&mut self, ev: DiscordInteractionEvent) -> bool {
        match ev.kind.as_str() {
            "modal" => {
                if let Some(modal) = ev.modal {
                    let mut text_values = HashMap::new();
                    for c in &modal.components {
                        if let Some(ref inner) = c.component
                            && inner.kind == 4
                            && let Some(ref cid) = inner.custom_id
                        {
                            let mut ed = TextEdit::default();
                            ed.text = inner.value.clone().unwrap_or_default();
                            text_values.insert(cid.clone(), ed);
                        }
                        for inner in &c.components {
                            if inner.kind == 4
                                && let Some(ref cid) = inner.custom_id
                            {
                                let mut ed = TextEdit::default();
                                ed.text = inner.value.clone().unwrap_or_default();
                                text_values.insert(cid.clone(), ed);
                            }
                        }
                    }
                    self.discord.chat.apps.component_busy = None;
                    self.discord.chat.apps.app_status = None;
                    self.discord.chat.apps.active_modal = Some(ActiveAppModal {
                        modal,
                        text_values,
                        choice_values: HashMap::new(),
                        bool_values: HashMap::new(),
                        busy: false,
                        error: None,
                    });
                    return true;
                }
                false
            }
            "choices" => {
                if let Some(ref mut cmd) = self.discord.chat.apps.active_command {
                    cmd.autocomplete_choices = ev.choices.unwrap_or_default();
                    return true;
                }
                false
            }
            "success" => {
                let kind = ev
                    .nonce
                    .as_deref()
                    .and_then(|n| self.discord.chat.apps.pending_nonces.remove(n));
                match kind.as_deref() {
                    Some("modal") => self.discord.chat.apps.active_modal = None,
                    Some("component") => {
                        self.discord.chat.apps.component_busy = None;
                        self.discord.chat.apps.component_error = None;
                    }
                    _ => {
                        self.discord.chat.apps.app_status = None;
                        self.discord.chat.apps.component_busy = None;
                    }
                }
                true
            }
            "failure" => {
                let msg = if ev.activity {
                    "This app opens an Activity, which Exo does not run.".to_string()
                } else {
                    "This interaction failed".to_string()
                };
                let kind = ev
                    .nonce
                    .as_deref()
                    .and_then(|n| self.discord.chat.apps.pending_nonces.remove(n));
                match kind.as_deref() {
                    Some("modal") => {
                        if let Some(ref mut m) = self.discord.chat.apps.active_modal {
                            m.busy = false;
                            m.error = Some(msg);
                        }
                    }
                    Some("component") => {
                        self.discord.chat.apps.component_busy = None;
                        self.discord.chat.apps.component_error = Some(msg);
                    }
                    _ => {
                        self.discord.chat.apps.app_status = None;
                        self.discord.chat.apps.component_busy = None;
                        self.notice = Some(msg);
                    }
                }
                true
            }
            _ => false,
        }
    }

    pub fn handle_discord_colors(&mut self, guild_id: &str) -> bool {
        let prefix = format!("{guild_id}:");
        self.discord
            .chat
            .links
            .guild_colors_requested
            .retain(|k| !k.starts_with(&prefix));
        if self.discord.active_guild_id.as_deref() == Some(guild_id) {
            if let Some(cid) = self.active_chat_channel_id()
                && let Some(list) = self.discord.chat.messages_by_channel.get(&cid)
            {
                let ids: Vec<String> = list
                    .iter()
                    .map(|m| m.author.id.clone())
                    .collect::<HashSet<_>>()
                    .into_iter()
                    .collect();
                self.fetch_discord_colors(guild_id, ids);
            }
            return self.room == Room::Discord;
        }
        false
    }

    pub fn handle_discord_open(&mut self, ev: DiscordOpenEvent) -> bool {
        self.room = Room::Discord;
        if let Some(gid) = ev.guild_id {
            self.open_discord_guild(&gid);
            self.open_discord_channel(&ev.channel_id);
        } else {
            self.open_discord_dm(&ev.channel_id);
        }
        true
    }

    pub fn handle_discord_dm_line(&mut self, ev: DiscordDmLineEvent) -> bool {
        if let Some(dm) = self
            .discord
            .home
            .dms
            .iter_mut()
            .find(|d| d.id == ev.channel_id)
        {
            dm.line = ev.line;
            if let Some(at) = ev.at {
                dm.last_at = Some(at);
            }
            return self.room == Room::Discord;
        }
        false
    }

    pub fn handle_discord_notify(&mut self, ev: DiscordNotifyEvent) -> bool {
        if self.room == Room::Discord
            && self.active_chat_channel_id().as_deref() == Some(ev.channel_id.as_str())
        {
            return false;
        }
        let place = ev
            .place
            .as_deref()
            .map(|p| format!(" in #{p}"))
            .unwrap_or_default();
        self.notice = Some(format!("{}{place}: {}", ev.author, ev.body));
        true
    }

    pub fn handle_discord_notify_clear(&mut self, _ev: DiscordNotifyClearEvent) -> bool {
        false
    }

    pub fn handle_friends_badge(&mut self, count: u32) -> bool {
        if self.discord.chat.friends_badge_count != count {
            self.discord.chat.friends_badge_count = count;
            true
        } else {
            false
        }
    }
}

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
    } else if lower.ends_with(".webm") {
        "video/webm"
    } else if lower.ends_with(".txt") || lower.ends_with(".log") || lower.ends_with(".md") {
        "text/plain"
    } else {
        "application/octet-stream"
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_inline_search_filters_and_builds_rpc_payload() {
        let now_ms = 1_795_400_000_000;
        let (chips, rest) = extract_inline_search_filters(
            "has:image -has:embed pinned:true before:2026-11-20 hello world",
            now_ms,
        );
        assert_eq!(rest, "hello world");
        assert_eq!(chips.len(), 4);
        let payload = build_search_filters_json(&chips, &rest, SearchSort::Oldest, 25);
        assert_eq!(payload["content"], "hello world");
        assert_eq!(payload["has"], json!(["image", "-embed"]));
        assert_eq!(payload["pinned"], json!(true));
        assert!(payload["maxId"].as_str().is_some());
        assert_eq!(payload["sort"], "oldest");
        assert_eq!(payload["offset"], 25);
    }

    #[test]
    fn extracts_invites_gifts_and_spotify_jam_links() {
        assert_eq!(
            extract_invite_codes(
                "join https://discord.gg/abc123 and https://discord.com/invite/xyz-9!"
            ),
            vec!["abc123".to_string(), "xyz-9".to_string()]
        );
        assert_eq!(
            extract_gift_codes("claim https://discord.gift/NitroGift99."),
            vec!["NitroGift99".to_string()]
        );
        assert_eq!(
            extract_spotify_jam_url("listen https://open.spotify.com/socialsession/jam123"),
            Some("https://open.spotify.com/socialsession/jam123".to_string())
        );
    }

    #[test]
    fn detects_active_mention_tokens_and_expands_mentions() {
        assert_eq!(active_mention_token("hey @ad"), Some((4, '@', "ad")));
        assert_eq!(active_mention_token("check #gen"), Some((6, '#', "gen")));
        assert_eq!(active_mention_token("cool :fir"), Some((5, ':', "fir")));
        assert_eq!(active_mention_token("short :a"), None);

        let mut map = HashMap::new();
        map.insert("@Ada".into(), "<@200001>".into());
        map.insert("#general".into(), "<#ch-1>".into());
        assert_eq!(
            expand_mentions("hi @Ada see #general", &map),
            "hi <@200001> see <#ch-1>"
        );
    }

    #[test]
    fn flattens_nested_slash_subcommands() {
        let cmd = DiscordAppCommand {
            id: "1".into(),
            application_id: "app1".into(),
            kind: 1,
            name: "queue".into(),
            description: Some("Manage queue".into()),
            options: vec![exo_rpc::DiscordAppOption {
                kind: 1,
                name: "add".into(),
                description: Some("Add song".into()),
                options: vec![exo_rpc::DiscordAppOption {
                    kind: 3,
                    name: "query".into(),
                    required: true,
                    ..Default::default()
                }],
                ..Default::default()
            }],
        };
        let app = DiscordAppInfo {
            id: "app1".into(),
            name: "MusicBot".into(),
            icon: None,
        };
        let flat = flatten_slash_commands(&[cmd], &[app]);
        assert_eq!(flat.len(), 1);
        assert_eq!(flat[0].path, vec!["add"]);
        assert_eq!(flat[0].options.len(), 1);
        assert_eq!(flat[0].description, "Add song");
    }

    #[test]
    fn poll_vote_event_updates_counts_and_me_voted() {
        let mut s = AppState::with_demo_games();
        if let Some(list) = s.discord.chat.messages_by_channel.get_mut("ch-general") {
            list[0].poll = Some(exo_rpc::DiscordPoll {
                question: exo_rpc::DiscordPollMedia {
                    text: Some("Ship it?".into()),
                    emoji: None,
                },
                answers: vec![exo_rpc::DiscordPollAnswer {
                    answer_id: 1,
                    poll_media: exo_rpc::DiscordPollMedia {
                        text: Some("Yes".into()),
                        emoji: None,
                    },
                }],
                expiry: None,
                allow_multiselect: false,
                results: None,
            });
        }
        s.handle_discord_poll_vote(DiscordPollVoteEvent {
            channel_id: "ch-general".into(),
            message_id: "1309000000000000001".into(),
            answer_id: 1,
            add: true,
            me: true,
        });
        let poll = s.discord.chat.messages_by_channel["ch-general"][0]
            .poll
            .as_ref()
            .unwrap();
        let c = &poll.results.as_ref().unwrap().answer_counts[0];
        assert_eq!(c.id, 1);
        assert_eq!(c.count, 1);
        assert!(c.me_voted);
    }

    #[test]
    fn create_dm_enforces_nine_friend_limit() {
        let mut s = AppState::with_demo_games();
        s.open_create_dm_dialog();
        for i in 0..12 {
            s.toggle_create_dm_friend(&format!("u-{i}"));
        }
        assert_eq!(s.discord.chat.create_dm.as_ref().unwrap().picked.len(), 9);
        s.toggle_create_dm_friend("u-0");
        assert_eq!(s.discord.chat.create_dm.as_ref().unwrap().picked.len(), 8);
    }

    #[test]
    fn event_registry_dispatches_all_wp6_push_events() {
        let mut s = AppState::with_demo_games();
        let reg = crate::events::EventRegistry::with_defaults();

        reg.dispatch(
            &mut s,
            exo_rpc::HostEvent::DiscordColors {
                guild_id: "g-exo".into(),
            },
        );
        assert!(
            !s.discord
                .chat
                .links
                .guild_colors_requested
                .contains("g-exo")
        );

        reg.dispatch(&mut s, exo_rpc::HostEvent::FriendsBadge { count: 4 });
        assert_eq!(s.discord.chat.friends_badge_count, 4);

        reg.dispatch(
            &mut s,
            exo_rpc::HostEvent::DiscordInteraction(Box::new(DiscordInteractionEvent {
                kind: "modal".into(),
                modal: Some(exo_rpc::DiscordAppModal {
                    id: "m-1".into(),
                    channel_id: "ch-general".into(),
                    custom_id: "feedback".into(),
                    title: "Feedback".into(),
                    components: vec![],
                    application: exo_rpc::DiscordAppModalApp {
                        id: "app-1".into(),
                        name: Some("Bot".into()),
                        icon: None,
                    },
                }),
                ..Default::default()
            })),
        );
        assert_eq!(
            s.discord
                .chat
                .apps
                .active_modal
                .as_ref()
                .map(|m| m.modal.title.as_str()),
            Some("Feedback")
        );

        reg.dispatch(
            &mut s,
            exo_rpc::HostEvent::DiscordDmLine(DiscordDmLineEvent {
                channel_id: "dm-ada".into(),
                line: Some("Ready for ranked?".into()),
                at: Some(999999),
            }),
        );
        if let Some(dm) = s.discord.home.dms.iter().find(|d| d.id == "dm-ada") {
            assert_eq!(dm.line.as_deref(), Some("Ready for ranked?"));
        }
    }

    #[test]
    fn expression_picker_and_attachment_modify_and_escape_order() {
        let mut s = AppState::with_demo_games();
        s.open_expression_picker_for_composer(ExpressionTab::Emoji);
        assert!(s.discord.chat.composer_emoji_open);
        s.toggle_emoji_favorite("🔥");
        assert!(
            s.discord
                .chat
                .picker
                .emoji_favorites
                .contains(&"🔥".to_string())
        );
        s.toggle_emoji_favorite("🔥");
        assert!(
            !s.discord
                .chat
                .picker
                .emoji_favorites
                .contains(&"🔥".to_string())
        );

        s.discord.chat.pending_uploads.push(PendingAttachment {
            local_id: "att-1".into(),
            name: "clip.png".into(),
            size: 3,
            upload_id: None,
            uploading: false,
            error: None,
            spoiler: false,
            description: String::new(),
            is_media: true,
            raw_bytes: Some(vec![1, 2, 3]),
        });
        s.open_modify_attachment("att-1");
        if let Some(ref mut m) = s.discord.chat.modifying_attachment {
            m.name.text = "renamed.png".into();
            m.description.text = "Boss fight".into();
            m.spoiler = true;
        }
        s.save_modify_attachment();
        let att = &s.discord.chat.pending_uploads[0];
        assert_eq!(att.name, "renamed.png");
        assert_eq!(att.description, "Boss fight");
        assert!(att.spoiler);

        s.open_user_profile_dialog(ProfileOpenRequest {
            user_id: "u-1",
            guild_id: Some("g-exo"),
            fallback_name: "Ada",
            fallback_username: "ada",
            fallback_avatar: None,
            full: true,
            tab: ProfileTab::About,
        });
        assert!(s.discord.chat.profile_dialog.is_some());
        assert!(s.dismiss_discord_chat_layer());
        assert!(s.discord.chat.profile_dialog.is_none());
        assert!(s.dismiss_discord_chat_layer());
        assert!(!s.discord.chat.composer_emoji_open);
    }

    #[test]
    fn poll_creator_validates_and_submits_and_deeplink_parses() {
        let mut s = AppState::with_demo_games();
        s.open_discord_guild("g-exo");
        s.open_discord_channel("ch-general");
        s.open_poll_creator();
        s.submit_poll_creator();
        assert!(
            s.discord
                .chat
                .poll_creator
                .as_ref()
                .unwrap()
                .error
                .is_some()
        );

        if let Some(ref mut p) = s.discord.chat.poll_creator {
            p.question.text = "Map pick?".into();
            p.answers[0].text = "Ascent".into();
            p.answers[1].text = "Bind".into();
        }
        s.submit_poll_creator();
        assert!(s.discord.chat.poll_creator.is_none());
        let last = s.discord.chat.messages_by_channel["ch-general"]
            .last()
            .unwrap();
        assert_eq!(
            last.poll.as_ref().and_then(|p| p.question.text.as_deref()),
            Some("Map pick?")
        );

        assert_eq!(
            discord_deeplink_target("https://discord.com/channels/@me/12345/67890"),
            Some((None, "12345".into(), Some("67890".into())))
        );
        assert_eq!(
            discord_deeplink_target("https://discord.com/channels/111/222"),
            Some((Some("111".into()), "222".into(), None))
        );
    }

    #[test]
    fn lightbox_gallery_navigation_zoom_and_escape() {
        let mut s = AppState::with_demo_games();
        s.open_discord_lightbox(
            vec![
                LightboxSlide {
                    src: "https://cdn.discordapp.com/a.png".into(),
                    original_url: "https://cdn.discordapp.com/a.png".into(),
                    caption: "a.png".into(),
                    alt: "Slide A".into(),
                },
                LightboxSlide {
                    src: "https://cdn.discordapp.com/b.png".into(),
                    original_url: "https://cdn.discordapp.com/b.png".into(),
                    caption: "b.png".into(),
                    alt: "Slide B".into(),
                },
            ],
            0,
        );
        assert!(s.discord.chat.links.lightbox.is_open());
        assert_eq!(s.discord.chat.links.lightbox.index(), Some(0));
        assert!(s.discord.chat.links.lightbox.next(2));
        assert_eq!(s.discord.chat.links.lightbox.index(), Some(1));
        s.discord.chat.links.lightbox.zoom_in();
        assert!(s.discord.chat.links.lightbox.is_zoomed());
        assert!(s.dismiss_discord_chat_layer());
        assert!(s.discord.chat.links.lightbox.is_open());
        assert!(!s.discord.chat.links.lightbox.is_zoomed());
        assert!(s.dismiss_discord_chat_layer());
        assert!(!s.discord.chat.links.lightbox.is_open());
        assert!(s.discord.chat.links.lightbox_slides.is_empty());
    }
}
