use crate::discord::avatar_box;
use crate::discord_chat;
use crate::state::{AppState, ContextMenuTarget, HostStatus};
use exo_kit::{
    BG, ExoGlass, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    RADIUS_SMALL, SPRING_QUICK, TRANSPARENT, apply_glass, bad, fg_faint, fg_muted, filter_pill,
    glass_button, glass_button_opt, glass_still_control_fill, good, hairline, hover_fill,
    icon_check, icon_chevron_right, icon_close, icon_plus, icon_search, icon_settings,
    modal_card_transition, modal_scrim_transition, pressed_fill, primary_button, scrim_fill,
    selected_fill, type_body, type_control, type_label, type_section, type_small, type_title,
};
use exo_rpc::{
    ActionResult, DiscordAdminReply, DiscordChannel, DiscordChannels, DiscordEventEntry,
    DiscordForumTag, DiscordMessage, DiscordModeration, DiscordPaymentChoice, DiscordRailItem,
    DiscordReportMenu, DiscordReportNode, DiscordReportReply, DiscordServerReply,
    DiscordShopCatalog, DiscordShopItem, DiscordShopPrice, DiscordShopProduct, DiscordShopReply,
    DiscordThread, DiscordThreadCreateReply, DiscordThreadsReply, METHOD_DISCORD_ACK,
    METHOD_DISCORD_ADMIN, METHOD_DISCORD_CHANNEL, METHOD_DISCORD_FRIEND, METHOD_DISCORD_REPORT,
    METHOD_DISCORD_SERVER, METHOD_DISCORD_SHOP, METHOD_DISCORD_THREAD, METHOD_DISCORD_THREADS,
    METHOD_DISCORD_USER_SETTINGS,
};
use rune::{Cursor, El, ScrollSpec, TextEdit, col, edit_access, el, row, text, text_input};
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};

pub const PAGE_GUIDE: &str = "page:guide";
pub const PAGE_BROWSE: &str = "page:browse";
pub const PAGE_MEMBERS: &str = "page:members";

pub const SLOWMODE_STOPS: [(i32, &str); 14] = [
    (0, "Off"),
    (5, "5s"),
    (10, "10s"),
    (15, "15s"),
    (30, "30s"),
    (60, "1m"),
    (120, "2m"),
    (300, "5m"),
    (600, "10m"),
    (900, "15m"),
    (1800, "30m"),
    (3600, "1h"),
    (7200, "2h"),
    (21600, "6h"),
];

pub const TIMEOUT_DURATIONS: [(i32, &str); 6] = [
    (60, "60 secs"),
    (300, "5 mins"),
    (600, "10 mins"),
    (3600, "1 hour"),
    (86400, "1 day"),
    (604800, "1 week"),
];

pub const CHANNEL_PERMISSIONS: [(&str, u32, &str); 16] = [
    ("General", 10, "View channel"),
    ("General", 4, "Manage channel"),
    ("General", 28, "Manage permissions"),
    ("General", 29, "Manage webhooks"),
    ("General", 0, "Create invite"),
    ("Text", 11, "Send messages"),
    ("Text", 14, "Embed links"),
    ("Text", 15, "Attach files"),
    ("Text", 6, "Add reactions"),
    ("Text", 17, "Mention @everyone"),
    ("Text", 13, "Manage messages"),
    ("Text", 16, "Read message history"),
    ("Voice", 20, "Connect"),
    ("Voice", 21, "Speak"),
    ("Voice", 9, "Video"),
    ("Voice", 22, "Mute members"),
];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ShopTab {
    Featured,
    Decoration,
    Effect,
    Nameplate,
    Collection,
}

impl ShopTab {
    pub const ALL: [Self; 5] = [
        Self::Featured,
        Self::Decoration,
        Self::Effect,
        Self::Nameplate,
        Self::Collection,
    ];

    pub fn label(self) -> &'static str {
        match self {
            Self::Featured => "Featured",
            Self::Decoration => "Avatar decorations",
            Self::Effect => "Profile effects",
            Self::Nameplate => "Nameplates",
            Self::Collection => "Your collection",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChannelSettingsTab {
    Overview,
    Permissions,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MemberDialogKind {
    Nick,
    Kick,
    Ban,
    Transfer,
}

#[derive(Debug, Clone)]
pub struct MemberAdminDialog {
    pub kind: MemberDialogKind,
    pub guild_id: String,
    pub user_id: String,
    pub name: String,
}

#[derive(Debug, Clone)]
pub struct CreateChannelDialog {
    pub guild_id: String,
    pub parent_id: Option<String>,
    pub as_category: bool,
    pub kind: String,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct InviteDialogState {
    pub guild_name: String,
    pub channel_id: String,
    pub channel_name: String,
    pub code: Option<String>,
    pub max_age: i32,
    pub max_uses: i32,
    pub temporary: bool,
    pub loading: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct VoiceStatusDialog {
    pub channel_id: String,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct ReportTarget {
    pub kind: String,
    pub ids: HashMap<String, String>,
    pub preview_name: String,
    pub preview_text: Option<String>,
    pub user_id: Option<String>,
    pub can_delete: bool,
}

#[derive(Debug, Clone)]
pub struct ReportModalState {
    pub target: ReportTarget,
    pub menu: Option<DiscordReportMenu>,
    pub path: Vec<(i64, Option<String>)>,
    pub answers: HashMap<String, Vec<String>>,
    pub sending: bool,
    pub result_ok: Option<bool>,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ServerSection {
    Profile,
    Tag,
    Engagement,
    Boost,
    Emoji,
    Stickers,
    Soundboard,
    Members,
    Roles,
    Invites,
    Access,
    Integrations,
    Directory,
    Safety,
    Audit,
    Bans,
    AutoMod,
    Community,
    Onboarding,
    Insights,
    Welcome,
    Subscriptions,
    Template,
}

impl ServerSection {
    pub const ALL: [Self; 23] = [
        Self::Profile,
        Self::Tag,
        Self::Engagement,
        Self::Boost,
        Self::Emoji,
        Self::Stickers,
        Self::Soundboard,
        Self::Members,
        Self::Roles,
        Self::Invites,
        Self::Access,
        Self::Integrations,
        Self::Directory,
        Self::Safety,
        Self::Audit,
        Self::Bans,
        Self::AutoMod,
        Self::Community,
        Self::Onboarding,
        Self::Insights,
        Self::Welcome,
        Self::Subscriptions,
        Self::Template,
    ];

    pub fn id(self) -> &'static str {
        match self {
            Self::Profile => "profile",
            Self::Tag => "tag",
            Self::Engagement => "engagement",
            Self::Boost => "boost",
            Self::Emoji => "emoji",
            Self::Stickers => "stickers",
            Self::Soundboard => "soundboard",
            Self::Members => "members",
            Self::Roles => "roles",
            Self::Invites => "invites",
            Self::Access => "access",
            Self::Integrations => "integrations",
            Self::Directory => "directory",
            Self::Safety => "safety",
            Self::Audit => "audit",
            Self::Bans => "bans",
            Self::AutoMod => "automod",
            Self::Community => "community",
            Self::Onboarding => "onboarding",
            Self::Insights => "insights",
            Self::Welcome => "welcome",
            Self::Subscriptions => "subscriptions",
            Self::Template => "template",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Profile => "Server profile",
            Self::Tag => "Server tag",
            Self::Engagement => "Engagement",
            Self::Boost => "Boost perks",
            Self::Emoji => "Emoji",
            Self::Stickers => "Stickers",
            Self::Soundboard => "Soundboard",
            Self::Members => "Members",
            Self::Roles => "Roles",
            Self::Invites => "Invites",
            Self::Access => "Access",
            Self::Integrations => "Integrations",
            Self::Directory => "App directory",
            Self::Safety => "Safety setup",
            Self::Audit => "Audit log",
            Self::Bans => "Bans",
            Self::AutoMod => "AutoMod",
            Self::Community => "Community",
            Self::Onboarding => "Onboarding",
            Self::Insights => "Server insights",
            Self::Welcome => "Welcome screen",
            Self::Subscriptions => "Server subscriptions",
            Self::Template => "Server template",
        }
    }

    pub fn shown(self, m: &DiscordModeration, features: &[String]) -> bool {
        let community = features.iter().any(|f| f == "COMMUNITY");
        let has_guide = features.iter().any(|f| f == "GUILD_SERVER_GUIDE");
        match self {
            Self::Profile | Self::Engagement | Self::Boost => true,
            Self::Tag
            | Self::Invites
            | Self::Access
            | Self::Directory
            | Self::Safety
            | Self::AutoMod
            | Self::Community
            | Self::Template => m.manage_guild,
            Self::Emoji | Self::Stickers | Self::Soundboard => m.manage_expressions,
            Self::Members => m.kick || m.ban || m.timeout || m.manage_roles || m.manage_guild,
            Self::Roles => m.manage_roles,
            Self::Integrations => m.manage_guild || m.manage_webhooks,
            Self::Audit => m.audit_log,
            Self::Bans => m.ban,
            Self::Onboarding => m.manage_guild && m.manage_roles && community,
            Self::Insights => (m.insights == Some(true) || m.manage_guild) && community,
            Self::Welcome => m.manage_guild && community && !has_guide,
            Self::Subscriptions => m.owner,
        }
    }
}

pub fn perm_bit_tri(allow: &str, deny: &str, bit: u32) -> i8 {
    let a = allow.parse::<u128>().unwrap_or(0);
    let d = deny.parse::<u128>().unwrap_or(0);
    let mask = 1u128 << bit;
    if (a & mask) != 0 {
        1
    } else if (d & mask) != 0 {
        -1
    } else {
        0
    }
}

pub fn with_perm_bit_tri(allow: &str, deny: &str, bit: u32, tri: i8) -> (String, String) {
    let mut a = allow.parse::<u128>().unwrap_or(0);
    let mut d = deny.parse::<u128>().unwrap_or(0);
    let mask = 1u128 << bit;
    a &= !mask;
    d &= !mask;
    if tri > 0 {
        a |= mask;
    } else if tri < 0 {
        d |= mask;
    }
    (a.to_string(), d.to_string())
}

pub fn move_server_in_rail(
    rail: &[DiscordRailItem],
    guild_id: &str,
    mode: &str,
) -> Option<Vec<DiscordRailItem>> {
    let mut next: Vec<DiscordRailItem> = rail.to_vec();
    let from_idx = next
        .iter()
        .position(|item| item.guild_ids.iter().any(|g| g == guild_id))?;
    let in_folder = next[from_idx].id.is_some();
    match mode {
        "folderFirst" if in_folder => {
            let ids = &mut next[from_idx].guild_ids;
            let pos = ids.iter().position(|g| g == guild_id)?;
            if pos == 0 {
                return None;
            }
            let id = ids.remove(pos);
            ids.insert(0, id);
            Some(next)
        }
        "folderLast" if in_folder => {
            let ids = &mut next[from_idx].guild_ids;
            let pos = ids.iter().position(|g| g == guild_id)?;
            if pos + 1 == ids.len() {
                return None;
            }
            let id = ids.remove(pos);
            ids.push(id);
            Some(next)
        }
        "noFolder" if in_folder => {
            let ids = &mut next[from_idx].guild_ids;
            let pos = ids.iter().position(|g| g == guild_id)?;
            let id = ids.remove(pos);
            let insert_at = if ids.is_empty() {
                next.remove(from_idx);
                from_idx
            } else {
                from_idx + 1
            };
            next.insert(
                insert_at,
                DiscordRailItem {
                    id: None,
                    name: None,
                    color: None,
                    guild_ids: vec![id],
                },
            );
            Some(next)
        }
        "listFirst" => {
            if !in_folder && from_idx == 0 {
                return None;
            }
            let ids = &mut next[from_idx].guild_ids;
            let pos = ids.iter().position(|g| g == guild_id)?;
            let id = ids.remove(pos);
            if ids.is_empty() {
                next.remove(from_idx);
            }
            next.insert(
                0,
                DiscordRailItem {
                    id: None,
                    name: None,
                    color: None,
                    guild_ids: vec![id],
                },
            );
            Some(next)
        }
        "listLast" => {
            if !in_folder && from_idx + 1 == next.len() {
                return None;
            }
            let ids = &mut next[from_idx].guild_ids;
            let pos = ids.iter().position(|g| g == guild_id)?;
            let id = ids.remove(pos);
            if ids.is_empty() {
                next.remove(from_idx);
            }
            next.push(DiscordRailItem {
                id: None,
                name: None,
                color: None,
                guild_ids: vec![id],
            });
            Some(next)
        }
        _ => None,
    }
}

pub fn format_shop_price(amount: i64, currency: &str, exponent: i32) -> String {
    let div = 10_f64.powi(exponent.clamp(0, 4));
    let val = (amount as f64) / div;
    let sym = match currency.to_lowercase().as_str() {
        "usd" => "$",
        "eur" => "€",
        "gbp" => "£",
        _ => "$",
    };
    format!("{sym}{val:.2}")
}

pub fn product_items(p: &DiscordShopProduct) -> &[DiscordShopItem] {
    if !p.items.is_empty() {
        &p.items
    } else if let Some(v) = p.variants.first() {
        &v.items
    } else {
        &[]
    }
}

pub fn product_kind(p: &DiscordShopProduct) -> Option<ShopTab> {
    let first = product_items(p).first()?;
    match first.kind {
        0 => Some(ShopTab::Decoration),
        1 => Some(ShopTab::Effect),
        2 => Some(ShopTab::Nameplate),
        _ => None,
    }
}

pub struct DiscordAdminState {
    pub forum_channel_id: Option<String>,
    pub forum_threads: Vec<DiscordThread>,
    pub forum_first_messages: HashMap<String, DiscordMessage>,
    pub forum_tags: Vec<DiscordForumTag>,
    pub forum_has_more: bool,
    pub forum_archived_offset: usize,
    pub forum_loading: bool,
    pub forum_error: Option<String>,
    pub forum_query: TextEdit,
    pub forum_selected_tag: Option<String>,
    pub forum_new_post_open: bool,
    pub forum_post_title: TextEdit,
    pub forum_post_body: TextEdit,
    pub forum_post_tags: HashSet<String>,
    pub forum_post_busy: bool,
    pub active_server_page: Option<String>,
    pub guide_guild_id: Option<String>,
    pub guide_reply: Option<DiscordServerReply>,
    pub guide_loading: bool,
    pub guide_error: Option<String>,
    pub guide_open_resource: Option<String>,
    pub events_open: bool,
    pub events_guild_id: Option<String>,
    pub events_list: Vec<DiscordEventEntry>,
    pub events_loading: bool,
    pub events_create_open: bool,
    pub event_name: TextEdit,
    pub event_desc: TextEdit,
    pub event_location: TextEdit,
    pub event_start: TextEdit,
    pub event_error: Option<String>,
    pub members_guild_id: Option<String>,
    pub members_reply: Option<DiscordServerReply>,
    pub members_query: TextEdit,
    pub members_loading: bool,
    pub prune_days: i32,
    pub prune_count: Option<i32>,
    pub person_cache: HashMap<(String, String), Value>,
    pub member_dialog: Option<MemberAdminDialog>,
    pub member_input: TextEdit,
    pub member_reason: TextEdit,
    pub member_ban_delete_secs: i32,
    pub member_error: Option<String>,
    pub create_channel: Option<CreateChannelDialog>,
    pub create_channel_name: TextEdit,
    pub server_privacy_guild: Option<String>,
    pub invite_dialog: Option<InviteDialogState>,
    pub voice_status_dialog: Option<VoiceStatusDialog>,
    pub voice_status_input: TextEdit,
    pub channel_settings_id: Option<String>,
    pub channel_settings_tab: ChannelSettingsTab,
    pub channel_settings_data: Option<Value>,
    pub channel_settings_loading: bool,
    pub channel_settings_error: Option<String>,
    pub ch_name: TextEdit,
    pub ch_topic: TextEdit,
    pub ch_slowmode: i32,
    pub ch_nsfw: bool,
    pub ch_bitrate: i32,
    pub ch_user_limit: i32,
    pub ch_dirty: bool,
    pub ch_delete_confirm: bool,
    pub ch_perm_target_id: Option<String>,
    pub ch_perm_edits: HashMap<String, (i32, String, String)>,
    pub server_settings_guild_id: Option<String>,
    pub server_settings_name: String,
    pub server_settings_section: ServerSection,
    pub server_settings_guild: Option<Value>,
    pub server_settings_loading: bool,
    pub server_settings_error: Option<String>,
    pub server_section_data: HashMap<String, Value>,
    pub srv_name: TextEdit,
    pub srv_desc: TextEdit,
    pub srv_verification: i32,
    pub srv_notify: i32,
    pub srv_explicit: i32,
    pub srv_dirty: bool,
    pub srv_new_role_name: TextEdit,
    pub srv_delete_confirm: bool,
    pub srv_delete_name: TextEdit,
    pub shop_catalog: Option<DiscordShopCatalog>,
    pub shop_owned: Vec<DiscordShopProduct>,
    pub shop_loading: bool,
    pub shop_error: Option<String>,
    pub shop_tab: ShopTab,
    pub shop_query: TextEdit,
    pub shop_selected: Option<DiscordShopProduct>,
    pub shop_worn_decoration: Option<String>,
    pub shop_worn_effect: Option<String>,
    pub shop_worn_nameplate: Option<String>,
    pub shop_payments: Vec<DiscordPaymentChoice>,
    pub shop_selected_payment: Option<String>,
    pub shop_price: Option<DiscordShopPrice>,
    pub shop_buy_busy: bool,
    pub shop_status: Option<String>,
    pub report: Option<ReportModalState>,
    pub report_free_text: TextEdit,
}

impl DiscordAdminState {
    pub fn empty() -> Self {
        Self {
            forum_channel_id: None,
            forum_threads: Vec::new(),
            forum_first_messages: HashMap::new(),
            forum_tags: Vec::new(),
            forum_has_more: false,
            forum_archived_offset: 0,
            forum_loading: false,
            forum_error: None,
            forum_query: TextEdit::default(),
            forum_selected_tag: None,
            forum_new_post_open: false,
            forum_post_title: TextEdit::default(),
            forum_post_body: TextEdit::default(),
            forum_post_tags: HashSet::new(),
            forum_post_busy: false,
            active_server_page: None,
            guide_guild_id: None,
            guide_reply: None,
            guide_loading: false,
            guide_error: None,
            guide_open_resource: None,
            events_open: false,
            events_guild_id: None,
            events_list: Vec::new(),
            events_loading: false,
            events_create_open: false,
            event_name: TextEdit::default(),
            event_desc: TextEdit::default(),
            event_location: TextEdit::default(),
            event_start: TextEdit::default(),
            event_error: None,
            members_guild_id: None,
            members_reply: None,
            members_query: TextEdit::default(),
            members_loading: false,
            prune_days: 7,
            prune_count: None,
            person_cache: HashMap::new(),
            member_dialog: None,
            member_input: TextEdit::default(),
            member_reason: TextEdit::default(),
            member_ban_delete_secs: 0,
            member_error: None,
            create_channel: None,
            create_channel_name: TextEdit::default(),
            server_privacy_guild: None,
            invite_dialog: None,
            voice_status_dialog: None,
            voice_status_input: TextEdit::default(),
            channel_settings_id: None,
            channel_settings_tab: ChannelSettingsTab::Overview,
            channel_settings_data: None,
            channel_settings_loading: false,
            channel_settings_error: None,
            ch_name: TextEdit::default(),
            ch_topic: TextEdit::default(),
            ch_slowmode: 0,
            ch_nsfw: false,
            ch_bitrate: 64_000,
            ch_user_limit: 0,
            ch_dirty: false,
            ch_delete_confirm: false,
            ch_perm_target_id: None,
            ch_perm_edits: HashMap::new(),
            server_settings_guild_id: None,
            server_settings_name: String::new(),
            server_settings_section: ServerSection::Profile,
            server_settings_guild: None,
            server_settings_loading: false,
            server_settings_error: None,
            server_section_data: HashMap::new(),
            srv_name: TextEdit::default(),
            srv_desc: TextEdit::default(),
            srv_verification: 0,
            srv_notify: 0,
            srv_explicit: 0,
            srv_dirty: false,
            srv_new_role_name: TextEdit::default(),
            srv_delete_confirm: false,
            srv_delete_name: TextEdit::default(),
            shop_catalog: None,
            shop_owned: Vec::new(),
            shop_loading: false,
            shop_error: None,
            shop_tab: ShopTab::Featured,
            shop_query: TextEdit::default(),
            shop_selected: None,
            shop_worn_decoration: None,
            shop_worn_effect: None,
            shop_worn_nameplate: None,
            shop_payments: Vec::new(),
            shop_selected_payment: None,
            shop_price: None,
            shop_buy_busy: false,
            shop_status: None,
            report: None,
            report_free_text: TextEdit::default(),
        }
    }

    pub fn dismiss_top(&mut self) -> bool {
        if self.report.take().is_some() {
            return true;
        }
        if self.shop_selected.take().is_some() {
            return true;
        }
        if self.member_dialog.take().is_some() {
            return true;
        }
        if self.voice_status_dialog.take().is_some() {
            return true;
        }
        if self.invite_dialog.take().is_some() {
            return true;
        }
        if self.server_privacy_guild.take().is_some() {
            return true;
        }
        if self.create_channel.take().is_some() {
            return true;
        }
        if self.events_open {
            self.events_open = false;
            return true;
        }
        if self.channel_settings_id.take().is_some() {
            return true;
        }
        if self.server_settings_guild_id.take().is_some() {
            return true;
        }
        false
    }
}

impl AppState {
    pub fn load_discord_forum(&mut self, channel_id: &str, archived: bool) {
        let reset = !archived || self.discord.admin.forum_channel_id.as_deref() != Some(channel_id);
        if reset {
            self.discord.admin.forum_channel_id = Some(channel_id.to_string());
            self.discord.admin.forum_threads.clear();
            self.discord.admin.forum_first_messages.clear();
            self.discord.admin.forum_archived_offset = 0;
            self.discord.admin.forum_has_more = false;
            self.discord.admin.forum_error = None;
            self.discord.admin.forum_selected_tag = None;
            self.discord.admin.forum_new_post_open = false;
        }
        if self.status != HostStatus::Connected {
            return;
        }
        let cid = channel_id.to_string();
        let offset = if archived {
            self.discord.admin.forum_archived_offset
        } else {
            0
        };
        self.discord.admin.forum_loading = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_THREADS,
            json!({ "channelId": cid, "archived": archived, "offset": offset }),
            move |s, res: Result<DiscordThreadsReply, String>| {
                s.discord.admin.forum_loading = false;
                if s.discord.admin.forum_channel_id.as_deref() != Some(cid.as_str()) {
                    return;
                }
                match res {
                    Ok(rep) if rep.ok => {
                        if !rep.tags.is_empty() {
                            s.discord.admin.forum_tags = rep.tags;
                        }
                        for msg in rep.first_messages {
                            s.discord
                                .admin
                                .forum_first_messages
                                .insert(msg.channel_id.clone(), msg);
                        }
                        let added_len = rep.threads.len();
                        for th in rep.threads {
                            if !s.discord.admin.forum_threads.iter().any(|x| x.id == th.id) {
                                s.discord.admin.forum_threads.push(th);
                            }
                        }
                        if archived {
                            s.discord.admin.forum_archived_offset += added_len;
                            s.discord.admin.forum_has_more = rep.has_more;
                        } else {
                            if let Some(top) =
                                s.discord.admin.forum_threads.first().map(|t| t.id.clone())
                            {
                                let ack_cid = cid.clone();
                                s.call_host_async(
                                    METHOD_DISCORD_ACK,
                                    json!({ "channelId": ack_cid, "messageId": top }),
                                    |_s, _r: Result<ActionResult, String>| {},
                                );
                            }
                            s.load_discord_forum(&cid, true);
                        }
                    }
                    Ok(rep) => {
                        s.discord.admin.forum_error = rep
                            .message
                            .or_else(|| Some("Discord didn't send the posts.".into()));
                    }
                    Err(e) => {
                        s.discord.admin.forum_error = Some(e);
                    }
                }
            },
        );
    }

    pub fn create_discord_forum_post(&mut self) {
        let Some(cid) = self.discord.admin.forum_channel_id.clone() else {
            return;
        };
        let name = self.discord.admin.forum_post_title.text.trim().to_string();
        let content = self.discord.admin.forum_post_body.text.trim().to_string();
        if name.is_empty() || content.is_empty() || self.discord.admin.forum_post_busy {
            return;
        }
        let tags: Vec<String> = self.discord.admin.forum_post_tags.iter().cloned().collect();
        self.discord.admin.forum_post_busy = true;
        if self.status != HostStatus::Connected {
            self.discord.admin.forum_post_busy = false;
            self.discord.admin.forum_new_post_open = false;
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_THREAD,
            json!({ "channelId": cid, "name": name, "content": content, "tags": tags }),
            |s, res: Result<DiscordThreadCreateReply, String>| {
                s.discord.admin.forum_post_busy = false;
                match res {
                    Ok(rep) if rep.ok => {
                        s.discord.admin.forum_new_post_open = false;
                        s.discord.admin.forum_post_title = TextEdit::default();
                        s.discord.admin.forum_post_body = TextEdit::default();
                        s.discord.admin.forum_post_tags.clear();
                        if let Some(th) = rep.thread {
                            s.open_discord_thread(&th.id, &th.name, false);
                        }
                    }
                    Ok(rep) => {
                        s.discord.admin.forum_error = rep
                            .message
                            .or_else(|| Some("Discord didn't take the post.".into()));
                    }
                    Err(e) => {
                        s.discord.admin.forum_error = Some(e);
                    }
                }
            },
        );
    }

    pub fn open_discord_thread(&mut self, thread_id: &str, name: &str, locked: bool) {
        self.discord.admin.active_server_page = None;
        if let Some(gid) = self.discord.active_guild_id.clone()
            && let Some(chans) = self.discord.channels_by_guild.get_mut(&gid)
            && !chans.channels.iter().any(|c| c.id == thread_id)
        {
            chans.channels.push(DiscordChannel {
                id: thread_id.to_string(),
                name: name.to_string(),
                channel_type: 11,
                kind: "text".into(),
                can_read: Some(true),
                can_send: Some(!locked),
                ..Default::default()
            });
        }
        self.open_discord_channel(thread_id);
    }

    pub fn open_discord_server_page(&mut self, page_id: &str) {
        self.discord.admin.active_server_page = Some(page_id.to_string());
        self.discord.admin.guide_open_resource = None;
        let Some(gid) = self.discord.active_guild_id.clone() else {
            return;
        };
        match page_id {
            PAGE_GUIDE => self.load_discord_server_guide(&gid),
            PAGE_MEMBERS => self.load_discord_server_members(&gid),
            _ => {}
        }
    }

    pub fn load_discord_server_guide(&mut self, guild_id: &str) {
        self.discord.admin.guide_guild_id = Some(guild_id.to_string());
        self.discord.admin.guide_error = None;
        if self.status != HostStatus::Connected {
            return;
        }
        let gid = guild_id.to_string();
        self.discord.admin.guide_loading = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            json!({ "action": "guide", "guildId": gid }),
            |s, res: Result<DiscordServerReply, String>| {
                s.discord.admin.guide_loading = false;
                match res {
                    Ok(rep) if rep.ok => s.discord.admin.guide_reply = Some(rep),
                    Ok(rep) => s.discord.admin.guide_error = rep.message,
                    Err(e) => s.discord.admin.guide_error = Some(e),
                }
            },
        );
    }

    pub fn open_discord_events(&mut self, guild_id: &str, create: bool) {
        self.discord.admin.events_open = true;
        self.discord.admin.events_guild_id = Some(guild_id.to_string());
        self.discord.admin.events_create_open = create;
        self.discord.admin.event_error = None;
        if self.status != HostStatus::Connected {
            return;
        }
        let gid = guild_id.to_string();
        self.discord.admin.events_loading = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            json!({ "action": "events", "guildId": gid }),
            |s, res: Result<DiscordServerReply, String>| {
                s.discord.admin.events_loading = false;
                if let Ok(rep) = res
                    && rep.ok
                    && let Some(evs) = rep.events
                {
                    s.discord.admin.events_list = evs;
                }
            },
        );
    }

    pub fn toggle_discord_event_rsvp(&mut self, event_id: &str, interested: bool) {
        let Some(gid) = self.discord.admin.events_guild_id.clone() else {
            return;
        };
        for item in &mut self.discord.admin.events_list {
            if item.event.id == event_id {
                item.interested = interested;
            }
        }
        if self.status != HostStatus::Connected {
            return;
        }
        let eid = event_id.to_string();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            json!({ "action": "rsvp", "guildId": gid, "eventId": eid, "interested": interested }),
            |_s, _res: Result<DiscordServerReply, String>| {},
        );
    }

    pub fn set_discord_event_status(&mut self, event_id: &str, status: i32) {
        let Some(gid) = self.discord.admin.events_guild_id.clone() else {
            return;
        };
        if self.status != HostStatus::Connected {
            return;
        }
        let eid = event_id.to_string();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            json!({ "action": "eventStatus", "guildId": gid, "eventId": eid, "status": status }),
            move |s, res: Result<DiscordServerReply, String>| {
                if let Ok(rep) = res {
                    if rep.ok {
                        s.open_discord_events(&gid, false);
                    } else {
                        s.discord.admin.event_error = rep.message;
                    }
                }
            },
        );
    }

    pub fn create_discord_event(&mut self) {
        let Some(gid) = self.discord.admin.events_guild_id.clone() else {
            return;
        };
        let name = self.discord.admin.event_name.text.trim().to_string();
        let location = self.discord.admin.event_location.text.trim().to_string();
        let desc = self.discord.admin.event_desc.text.trim().to_string();
        let start = self.discord.admin.event_start.text.trim().to_string();
        if name.is_empty() {
            self.discord.admin.event_error = Some("Enter an event name.".into());
            return;
        }
        if self.status != HostStatus::Connected {
            self.discord.admin.events_create_open = false;
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            json!({
                "action": "createEvent",
                "guildId": gid,
                "name": name,
                "description": if desc.is_empty() { Value::Null } else { json!(desc) },
                "location": if location.is_empty() { json!("Discord") } else { json!(location) },
                "start": if start.is_empty() { Value::Null } else { json!(start) },
            }),
            move |s, res: Result<DiscordServerReply, String>| {
                if let Ok(rep) = res {
                    if rep.ok {
                        s.discord.admin.events_create_open = false;
                        s.discord.admin.event_name = TextEdit::default();
                        s.discord.admin.event_desc = TextEdit::default();
                        s.discord.admin.event_location = TextEdit::default();
                        s.open_discord_events(&gid, false);
                    } else {
                        s.discord.admin.event_error = rep.message;
                    }
                }
            },
        );
    }

    pub fn load_discord_server_members(&mut self, guild_id: &str) {
        self.discord.admin.members_guild_id = Some(guild_id.to_string());
        if self.status != HostStatus::Connected {
            return;
        }
        let gid = guild_id.to_string();
        let query = self.discord.admin.members_query.text.trim().to_string();
        self.discord.admin.members_loading = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            json!({
                "action": "members",
                "guildId": gid,
                "query": if query.is_empty() { Value::Null } else { json!(query) },
            }),
            |s, res: Result<DiscordServerReply, String>| {
                s.discord.admin.members_loading = false;
                if let Ok(rep) = res
                    && rep.ok
                {
                    s.discord.admin.members_reply = Some(rep);
                }
            },
        );
    }

    pub fn discord_prune_members(&mut self, guild_id: &str, count_only: bool) {
        if self.status != HostStatus::Connected {
            return;
        }
        let gid = guild_id.to_string();
        let days = self.discord.admin.prune_days;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            json!({ "action": "prune", "guildId": gid, "days": days, "countOnly": count_only }),
            move |s, res: Result<DiscordServerReply, String>| {
                if let Ok(rep) = res
                    && rep.ok
                {
                    s.discord.admin.prune_count = rep.pruned;
                    if !count_only {
                        s.notice = Some(format!(
                            "Pruned {} inactive members",
                            rep.pruned.unwrap_or(0)
                        ));
                        s.load_discord_server_members(&gid);
                    }
                }
            },
        );
    }

    pub fn fetch_discord_person(&mut self, guild_id: &str, user_id: &str) {
        if self.status != HostStatus::Connected {
            return;
        }
        let gid = guild_id.to_string();
        let uid = user_id.to_string();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ADMIN,
            json!({ "action": "person", "guildId": gid, "userId": uid }),
            move |s, res: Result<DiscordAdminReply, String>| {
                if let Ok(rep) = res
                    && rep.ok
                    && let Some(data) = rep.data
                {
                    s.discord.admin.person_cache.insert((gid, uid), data);
                }
            },
        );
    }

    pub fn open_member_admin_dialog(
        &mut self,
        kind: MemberDialogKind,
        guild_id: &str,
        user_id: &str,
        name: &str,
    ) {
        let initial_nick = self
            .discord
            .admin
            .person_cache
            .get(&(guild_id.to_string(), user_id.to_string()))
            .and_then(|v| v.get("nick").and_then(|n| n.as_str()))
            .unwrap_or(name)
            .to_string();
        self.discord.admin.member_dialog = Some(MemberAdminDialog {
            kind,
            guild_id: guild_id.to_string(),
            user_id: user_id.to_string(),
            name: name.to_string(),
        });
        self.discord.admin.member_input = TextEdit::new(if kind == MemberDialogKind::Nick {
            &initial_nick
        } else {
            ""
        });
        self.discord.admin.member_reason = TextEdit::default();
        self.discord.admin.member_ban_delete_secs = 0;
        self.discord.admin.member_error = None;
    }

    pub fn submit_member_admin_dialog(&mut self) {
        let Some(dlg) = self.discord.admin.member_dialog.clone() else {
            return;
        };
        let (action, params) = match dlg.kind {
            MemberDialogKind::Nick => {
                let nick = self.discord.admin.member_input.text.trim().to_string();
                (
                    "nick",
                    json!({
                        "guildId": dlg.guild_id,
                        "userId": dlg.user_id,
                        "nick": if nick.is_empty() { Value::Null } else { json!(nick) },
                    }),
                )
            }
            MemberDialogKind::Kick => (
                "kick",
                json!({
                    "guildId": dlg.guild_id,
                    "userId": dlg.user_id,
                    "reason": self.discord.admin.member_reason.text.trim(),
                }),
            ),
            MemberDialogKind::Ban => (
                "ban",
                json!({
                    "guildId": dlg.guild_id,
                    "userId": dlg.user_id,
                    "reason": self.discord.admin.member_reason.text.trim(),
                    "deleteSeconds": self.discord.admin.member_ban_delete_secs,
                }),
            ),
            MemberDialogKind::Transfer => (
                "transfer",
                json!({
                    "guildId": dlg.guild_id,
                    "userId": dlg.user_id,
                }),
            ),
        };
        if self.status != HostStatus::Connected {
            self.discord.admin.member_dialog = None;
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ADMIN,
            json!({ "action": action, "params": params, "guildId": dlg.guild_id, "userId": dlg.user_id, "nick": params.get("nick"), "reason": params.get("reason"), "deleteSeconds": params.get("deleteSeconds") }),
            |s, res: Result<DiscordAdminReply, String>| match res {
                Ok(rep) if rep.ok => {
                    s.discord.admin.member_dialog = None;
                    s.notice = Some("Updated member".into());
                }
                Ok(rep) => {
                    s.discord.admin.member_error =
                        rep.message.or_else(|| Some("Discord didn't take that.".into()));
                }
                Err(e) => s.discord.admin.member_error = Some(e),
            },
        );
    }

    pub fn run_discord_member_mod(
        &mut self,
        action: &str,
        guild_id: &str,
        user_id: &str,
        extra: Value,
    ) {
        if self.status != HostStatus::Connected {
            return;
        }
        let mut body = json!({
            "action": action,
            "guildId": guild_id,
            "userId": user_id,
        });
        if let (Some(dst), Some(src)) = (body.as_object_mut(), extra.as_object()) {
            for (k, v) in src {
                dst.insert(k.clone(), v.clone());
            }
        }
        let gid = guild_id.to_string();
        let uid = user_id.to_string();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ADMIN,
            body,
            move |s, res: Result<DiscordAdminReply, String>| {
                if let Ok(rep) = res {
                    if rep.ok {
                        s.fetch_discord_person(&gid, &uid);
                    } else if let Some(msg) = rep.message {
                        s.notice = Some(msg);
                    }
                }
            },
        );
    }

    pub fn open_create_channel_dialog(
        &mut self,
        guild_id: &str,
        parent_id: Option<String>,
        as_category: bool,
    ) {
        self.discord.admin.create_channel_name = TextEdit::default();
        self.discord.admin.create_channel = Some(CreateChannelDialog {
            guild_id: guild_id.to_string(),
            parent_id,
            as_category,
            kind: if as_category { "category" } else { "text" }.into(),
            error: None,
        });
    }

    pub fn submit_create_channel(&mut self) {
        let Some(dlg) = self.discord.admin.create_channel.clone() else {
            return;
        };
        let name = self
            .discord
            .admin
            .create_channel_name
            .text
            .trim()
            .to_string();
        if name.is_empty() {
            if let Some(ref mut d) = self.discord.admin.create_channel {
                d.error = Some("Enter a name.".into());
            }
            return;
        }
        if self.status != HostStatus::Connected {
            self.discord.admin.create_channel = None;
            return;
        }
        let gid = dlg.guild_id.clone();
        let as_cat = dlg.as_category;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            json!({
                "action": "createChannel",
                "guildId": dlg.guild_id,
                "parentId": dlg.parent_id,
                "name": name,
                "kind": dlg.kind,
            }),
            move |s, res: Result<DiscordServerReply, String>| match res {
                Ok(rep) if rep.ok => {
                    s.discord.admin.create_channel = None;
                    s.load_discord_guild(&gid, false);
                    if !as_cat && let Some(cid) = rep.channel_id {
                        s.open_discord_channel(&cid);
                    }
                }
                Ok(rep) => {
                    if let Some(ref mut d) = s.discord.admin.create_channel {
                        d.error = rep
                            .message
                            .or_else(|| Some("Discord didn't create that.".into()));
                    }
                }
                Err(e) => {
                    if let Some(ref mut d) = s.discord.admin.create_channel {
                        d.error = Some(e);
                    }
                }
            },
        );
    }

    pub fn open_discord_invite_dialog(
        &mut self,
        guild_name: &str,
        channel_id: &str,
        channel_name: &str,
    ) {
        self.discord.admin.invite_dialog = Some(InviteDialogState {
            guild_name: guild_name.to_string(),
            channel_id: channel_id.to_string(),
            channel_name: channel_name.to_string(),
            code: None,
            max_age: 604_800,
            max_uses: 0,
            temporary: false,
            loading: true,
            error: None,
        });
        self.generate_discord_invite();
    }

    pub fn generate_discord_invite(&mut self) {
        let Some(dlg) = self.discord.admin.invite_dialog.clone() else {
            return;
        };
        if self.status != HostStatus::Connected {
            if let Some(ref mut d) = self.discord.admin.invite_dialog {
                d.loading = false;
                d.code = Some("https://discord.gg/exo-demo".into());
            }
            return;
        }
        if let Some(ref mut d) = self.discord.admin.invite_dialog {
            d.loading = true;
            d.error = None;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CHANNEL,
            json!({
                "channelId": dlg.channel_id,
                "action": "invite",
                "maxAge": dlg.max_age,
                "maxUses": dlg.max_uses,
                "temporary": dlg.temporary,
            }),
            |s, res: Result<Value, String>| {
                if let Some(ref mut d) = s.discord.admin.invite_dialog {
                    d.loading = false;
                    match res {
                        Ok(v) if v.get("ok").and_then(|x| x.as_bool()).unwrap_or(false) => {
                            let url = v
                                .get("url")
                                .or_else(|| v.get("code"))
                                .and_then(|x| x.as_str())
                                .map(|c| {
                                    if c.starts_with("http") {
                                        c.to_string()
                                    } else {
                                        format!("https://discord.gg/{c}")
                                    }
                                });
                            d.code = url;
                        }
                        Ok(v) => {
                            d.error = v
                                .get("message")
                                .and_then(|x| x.as_str())
                                .map(str::to_string)
                                .or_else(|| Some("Couldn't create invite.".into()));
                        }
                        Err(e) => d.error = Some(e),
                    }
                }
            },
        );
    }

    pub fn open_voice_status_dialog(&mut self, channel_id: &str, current: Option<&str>) {
        self.discord.admin.voice_status_input = TextEdit::new(current.unwrap_or(""));
        self.discord.admin.voice_status_dialog = Some(VoiceStatusDialog {
            channel_id: channel_id.to_string(),
            error: None,
        });
    }

    pub fn save_discord_voice_status(&mut self, channel_id: &str, text: &str) {
        if self.status != HostStatus::Connected {
            self.discord.admin.voice_status_dialog = None;
            return;
        }
        let cid = channel_id.to_string();
        let body = text.trim().to_string();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            json!({ "action": "voiceStatus", "channelId": cid, "text": body }),
            |s, res: Result<DiscordServerReply, String>| {
                if let Ok(rep) = res {
                    if rep.ok {
                        s.discord.admin.voice_status_dialog = None;
                        if let Some(gid) = s.discord.active_guild_id.clone() {
                            s.load_discord_guild(&gid, false);
                        }
                    } else if let Some(ref mut d) = s.discord.admin.voice_status_dialog {
                        d.error = rep.message;
                    }
                }
            },
        );
    }

    pub fn discord_move_server(&mut self, guild_id: &str, mode: &str) {
        if let Some(next) = move_server_in_rail(&self.discord.home.rail, guild_id, mode) {
            self.discord.home.rail = next.clone();
            if self.status == HostStatus::Connected {
                #[cfg(windows)]
                self.call_host_async(
                    METHOD_DISCORD_USER_SETTINGS,
                    json!({ "guildFolders": next }),
                    |_s, _res: Result<Value, String>| {},
                );
            }
        }
    }

    pub fn discord_show_all_channels(&mut self, guild_id: &str, on: bool) {
        if let Some(chans) = self.discord.channels_by_guild.get_mut(guild_id) {
            chans.opt_in = !on;
        }
        if self.status != HostStatus::Connected {
            return;
        }
        let gid = guild_id.to_string();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            json!({ "action": "showAll", "guildId": gid, "on": on }),
            move |s, _res: Result<DiscordServerReply, String>| {
                s.load_discord_guild(&gid, false);
            },
        );
    }

    pub fn discord_channel_optin(&mut self, channel_id: &str, on: bool) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CHANNEL,
            json!({ "channelId": channel_id, "action": "optin", "value": on }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn open_channel_settings(&mut self, channel_id: &str) {
        self.discord.admin.channel_settings_id = Some(channel_id.to_string());
        self.discord.admin.channel_settings_tab = ChannelSettingsTab::Overview;
        self.discord.admin.channel_settings_data = None;
        self.discord.admin.channel_settings_error = None;
        self.discord.admin.ch_dirty = false;
        self.discord.admin.ch_delete_confirm = false;
        self.discord.admin.ch_perm_edits.clear();
        if self.status != HostStatus::Connected {
            return;
        }
        let cid = channel_id.to_string();
        self.discord.admin.channel_settings_loading = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ADMIN,
            json!({ "action": "channelSettings", "channelId": cid }),
            |s, res: Result<DiscordAdminReply, String>| {
                s.discord.admin.channel_settings_loading = false;
                match res {
                    Ok(rep) if rep.ok => {
                        if let Some(data) = rep.data {
                            let name = data.get("name").and_then(|x| x.as_str()).unwrap_or("");
                            let topic = data.get("topic").and_then(|x| x.as_str()).unwrap_or("");
                            s.discord.admin.ch_name = TextEdit::new(name);
                            s.discord.admin.ch_topic = TextEdit::new(topic);
                            s.discord.admin.ch_slowmode =
                                data.get("rateLimit").and_then(|x| x.as_i64()).unwrap_or(0) as i32;
                            s.discord.admin.ch_nsfw =
                                data.get("nsfw").and_then(|x| x.as_bool()).unwrap_or(false);
                            s.discord.admin.ch_bitrate =
                                data.get("bitrate")
                                    .and_then(|x| x.as_i64())
                                    .unwrap_or(64_000) as i32;
                            s.discord.admin.ch_user_limit =
                                data.get("userLimit").and_then(|x| x.as_i64()).unwrap_or(0) as i32;
                            s.discord.admin.ch_perm_target_id = data
                                .get("guildId")
                                .and_then(|x| x.as_str())
                                .map(str::to_string);
                            if !data
                                .get("canManage")
                                .and_then(|x| x.as_bool())
                                .unwrap_or(true)
                            {
                                s.discord.admin.channel_settings_tab =
                                    ChannelSettingsTab::Permissions;
                            }
                            s.discord.admin.channel_settings_data = Some(data);
                        }
                    }
                    Ok(rep) => s.discord.admin.channel_settings_error = rep.message,
                    Err(e) => s.discord.admin.channel_settings_error = Some(e),
                }
            },
        );
    }

    pub fn save_channel_settings_overview(&mut self) {
        let Some(cid) = self.discord.admin.channel_settings_id.clone() else {
            return;
        };
        let changes = json!({
            "name": self.discord.admin.ch_name.text.trim(),
            "topic": self.discord.admin.ch_topic.text.trim(),
            "rate_limit_per_user": self.discord.admin.ch_slowmode,
            "nsfw": self.discord.admin.ch_nsfw,
            "bitrate": self.discord.admin.ch_bitrate,
            "user_limit": self.discord.admin.ch_user_limit,
        });
        if self.status != HostStatus::Connected {
            self.discord.admin.ch_dirty = false;
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ADMIN,
            json!({ "action": "channelSave", "channelId": cid, "changes": changes }),
            move |s, res: Result<DiscordAdminReply, String>| match res {
                Ok(rep) if rep.ok => {
                    s.discord.admin.ch_dirty = false;
                    s.open_channel_settings(&cid);
                }
                Ok(rep) => s.discord.admin.channel_settings_error = rep.message,
                Err(e) => s.discord.admin.channel_settings_error = Some(e),
            },
        );
    }

    pub fn save_channel_permission_edits(&mut self) {
        let Some(cid) = self.discord.admin.channel_settings_id.clone() else {
            return;
        };
        let edits: Vec<(String, i32, String, String)> = self
            .discord
            .admin
            .ch_perm_edits
            .drain()
            .map(|(id, (t, a, d))| (id, t, a, d))
            .collect();
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        for (target_id, kind, allow, deny) in edits {
            let cid_c = cid.clone();
            self.call_host_async(
                METHOD_DISCORD_ADMIN,
                json!({
                    "action": "channelOverwrite",
                    "channelId": cid_c,
                    "targetId": target_id,
                    "type": kind,
                    "allow": allow,
                    "deny": deny,
                }),
                |_s, _res: Result<DiscordAdminReply, String>| {},
            );
        }
    }

    pub fn open_server_settings(&mut self, guild_id: &str, name: &str, section: ServerSection) {
        self.discord.admin.server_settings_guild_id = Some(guild_id.to_string());
        self.discord.admin.server_settings_name = name.to_string();
        self.discord.admin.server_settings_section = section;
        self.discord.admin.server_settings_error = None;
        self.discord.admin.srv_dirty = false;
        self.discord.admin.srv_delete_confirm = false;
        self.discord.admin.srv_delete_name = TextEdit::default();
        if self.status != HostStatus::Connected {
            return;
        }
        let gid = guild_id.to_string();
        self.discord.admin.server_settings_loading = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ADMIN,
            json!({ "action": "guild", "guildId": gid }),
            move |s, res: Result<DiscordAdminReply, String>| {
                s.discord.admin.server_settings_loading = false;
                match res {
                    Ok(rep) if rep.ok => {
                        if let Some(data) = rep.data {
                            let n = data.get("name").and_then(|x| x.as_str()).unwrap_or("");
                            let desc = data
                                .get("description")
                                .and_then(|x| x.as_str())
                                .unwrap_or("");
                            s.discord.admin.srv_name = TextEdit::new(n);
                            s.discord.admin.srv_desc = TextEdit::new(desc);
                            s.discord.admin.srv_verification =
                                data.get("verification_level")
                                    .and_then(|x| x.as_i64())
                                    .unwrap_or(0) as i32;
                            s.discord.admin.srv_notify =
                                data.get("default_message_notifications")
                                    .and_then(|x| x.as_i64())
                                    .unwrap_or(0) as i32;
                            s.discord.admin.srv_explicit =
                                data.get("explicit_content_filter")
                                    .and_then(|x| x.as_i64())
                                    .unwrap_or(0) as i32;
                            s.discord.admin.server_settings_guild = Some(data);
                        }
                    }
                    Ok(rep) => s.discord.admin.server_settings_error = rep.message,
                    Err(e) => s.discord.admin.server_settings_error = Some(e),
                }
            },
        );
        self.load_server_section_data(guild_id, section);
    }

    pub fn select_server_settings_section(&mut self, section: ServerSection) {
        self.discord.admin.server_settings_section = section;
        self.discord.admin.server_settings_error = None;
        if let Some(gid) = self.discord.admin.server_settings_guild_id.clone() {
            self.load_server_section_data(&gid, section);
        }
    }

    pub fn load_server_section_data(&mut self, guild_id: &str, section: ServerSection) {
        if self.status != HostStatus::Connected {
            return;
        }
        let action = match section {
            ServerSection::Roles => "roles",
            ServerSection::Emoji => "emojis",
            ServerSection::Stickers => "stickers",
            ServerSection::Soundboard => "soundboard",
            ServerSection::Invites => "invites",
            ServerSection::Integrations => "integrations",
            ServerSection::AutoMod => "automod",
            ServerSection::Audit => "auditLog",
            ServerSection::Bans => "bans",
            ServerSection::Onboarding => "onboarding",
            ServerSection::Welcome => "welcomeScreen",
            ServerSection::Template => "template",
            ServerSection::Insights => "insights",
            ServerSection::Access => "access",
            ServerSection::Tag => "tag",
            ServerSection::Members => {
                self.load_discord_server_members(guild_id);
                return;
            }
            _ => return,
        };
        let gid = guild_id.to_string();
        let key = section.id().to_string();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ADMIN,
            json!({ "action": action, "guildId": gid }),
            move |s, res: Result<DiscordAdminReply, String>| {
                if let Ok(rep) = res
                    && rep.ok
                    && let Some(data) = rep.data
                {
                    s.discord.admin.server_section_data.insert(key, data);
                }
            },
        );
    }

    pub fn save_server_overview(&mut self) {
        let Some(gid) = self.discord.admin.server_settings_guild_id.clone() else {
            return;
        };
        let name = self.discord.admin.srv_name.text.trim().to_string();
        if name.len() < 2 || name.len() > 100 {
            self.discord.admin.server_settings_error =
                Some("Use a server name of 2 to 100 characters.".into());
            return;
        }
        let desc = self.discord.admin.srv_desc.text.trim().to_string();
        let changes = json!({
            "name": name,
            "description": if desc.is_empty() { Value::Null } else { json!(desc) },
            "verification_level": self.discord.admin.srv_verification,
            "default_message_notifications": self.discord.admin.srv_notify,
            "explicit_content_filter": self.discord.admin.srv_explicit,
        });
        if self.status != HostStatus::Connected {
            self.discord.admin.srv_dirty = false;
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ADMIN,
            json!({ "action": "saveGuild", "guildId": gid, "changes": changes }),
            move |s, res: Result<DiscordAdminReply, String>| match res {
                Ok(rep) if rep.ok => {
                    s.discord.admin.srv_dirty = false;
                    s.notice = Some("Server settings saved".into());
                    let nm = s.discord.admin.server_settings_name.clone();
                    let sec = s.discord.admin.server_settings_section;
                    s.open_server_settings(&gid, &nm, sec);
                }
                Ok(rep) => s.discord.admin.server_settings_error = rep.message,
                Err(e) => s.discord.admin.server_settings_error = Some(e),
            },
        );
    }

    pub fn run_server_admin_action(&mut self, action: &str, extra: Value) {
        let Some(gid) = self.discord.admin.server_settings_guild_id.clone() else {
            return;
        };
        if self.status != HostStatus::Connected {
            return;
        }
        let mut payload = json!({ "action": action, "guildId": gid });
        if let (Some(dst), Some(src)) = (payload.as_object_mut(), extra.as_object()) {
            for (k, v) in src {
                dst.insert(k.clone(), v.clone());
            }
        }
        let sec = self.discord.admin.server_settings_section;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ADMIN,
            payload,
            move |s, res: Result<DiscordAdminReply, String>| match res {
                Ok(rep) if rep.ok => s.load_server_section_data(&gid, sec),
                Ok(rep) => s.discord.admin.server_settings_error = rep.message,
                Err(e) => s.discord.admin.server_settings_error = Some(e),
            },
        );
    }

    pub fn load_discord_shop(&mut self, force: bool) {
        if self.discord.admin.shop_catalog.is_some() && !force {
            return;
        }
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.admin.shop_loading = true;
        self.discord.admin.shop_error = None;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SHOP,
            json!({ "action": "catalog" }),
            |s, res: Result<DiscordShopReply, String>| {
                s.discord.admin.shop_loading = false;
                match res {
                    Ok(rep) if rep.ok => {
                        s.discord.admin.shop_catalog = rep.catalog;
                        s.discord.admin.shop_owned = rep.owned.unwrap_or_default();
                    }
                    Ok(rep) => {
                        s.discord.admin.shop_error = rep
                            .message
                            .or_else(|| Some("Discord didn't send the Shop.".into()));
                    }
                    Err(e) => s.discord.admin.shop_error = Some(e),
                }
            },
        );
    }

    pub fn open_shop_product(&mut self, product: DiscordShopProduct) {
        let sku_id = product.sku_id.clone();
        self.discord.admin.shop_selected = Some(product);
        self.discord.admin.shop_price = None;
        self.discord.admin.shop_status = None;
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SHOP,
            json!({ "action": "payment" }),
            move |s, res: Result<DiscordShopReply, String>| {
                if let Ok(rep) = res
                    && rep.ok
                {
                    let def = rep
                        .payment
                        .iter()
                        .find(|p| p.default && p.usable)
                        .or_else(|| rep.payment.iter().find(|p| p.usable))
                        .map(|p| p.id.clone());
                    s.discord.admin.shop_payments = rep.payment;
                    s.discord.admin.shop_selected_payment = def.clone();
                    if let Some(pid) = def {
                        s.preview_shop_price(&sku_id, &pid);
                    }
                }
            },
        );
    }

    pub fn preview_shop_price(&mut self, sku_id: &str, payment_id: &str) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SHOP,
            json!({ "action": "preview", "skuId": sku_id, "paymentId": payment_id }),
            |s, res: Result<DiscordShopReply, String>| {
                if let Ok(rep) = res
                    && rep.ok
                {
                    s.discord.admin.shop_price = rep.price;
                }
            },
        );
    }

    pub fn equip_shop_item(&mut self, kind: &str, sku_id: Option<String>) {
        match kind {
            "decoration" => self.discord.admin.shop_worn_decoration = sku_id.clone(),
            "effect" => self.discord.admin.shop_worn_effect = sku_id.clone(),
            "nameplate" => self.discord.admin.shop_worn_nameplate = sku_id.clone(),
            _ => {}
        }
        if self.status != HostStatus::Connected {
            self.discord.admin.shop_status = Some("Updated collectible".into());
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SHOP,
            json!({ "action": "equip", "kind": kind, "skuId": sku_id }),
            |s, res: Result<DiscordShopReply, String>| match res {
                Ok(rep) if rep.ok => s.discord.admin.shop_status = Some("Saved".into()),
                Ok(rep) => s.discord.admin.shop_status = rep.message,
                Err(e) => s.discord.admin.shop_status = Some(e),
            },
        );
    }

    pub fn buy_shop_selected(&mut self) {
        let Some(prod) = self.discord.admin.shop_selected.clone() else {
            return;
        };
        let Some(pay_id) = self.discord.admin.shop_selected_payment.clone() else {
            self.discord.admin.shop_status = Some("No usable payment method on file.".into());
            return;
        };
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.admin.shop_buy_busy = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SHOP,
            json!({
                "action": "buy",
                "skuId": prod.sku_id,
                "paymentId": pay_id,
                "expectedAmount": self.discord.admin.shop_price.as_ref().and_then(|p| p.amount),
                "expectedCurrency": self.discord.admin.shop_price.as_ref().and_then(|p| p.currency.clone()),
            }),
            |s, res: Result<DiscordShopReply, String>| {
                s.discord.admin.shop_buy_busy = false;
                match res {
                    Ok(rep) if rep.ok => {
                        s.discord.admin.shop_status = Some("Purchased!".into());
                        s.load_discord_shop(true);
                    }
                    Ok(rep) if rep.declined => {}
                    Ok(rep) => {
                        s.discord.admin.shop_status =
                            rep.message.or_else(|| Some("Purchase did not complete.".into()));
                    }
                    Err(e) => s.discord.admin.shop_status = Some(e),
                }
            },
        );
    }

    pub fn open_discord_report(&mut self, target: ReportTarget) {
        let kind = target.kind.clone();
        self.discord.admin.report_free_text = TextEdit::default();
        self.discord.admin.report = Some(ReportModalState {
            target,
            menu: None,
            path: Vec::new(),
            answers: HashMap::new(),
            sending: false,
            result_ok: None,
            error: None,
        });
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_REPORT,
            json!({ "kind": kind, "action": "menu" }),
            |s, res: Result<DiscordReportReply, String>| {
                if let Some(ref mut st) = s.discord.admin.report {
                    match res {
                        Ok(rep) if rep.ok => {
                            if let Some(menu) = rep.menu {
                                let root = menu.root_node_id;
                                st.path = vec![(root, None)];
                                st.menu = Some(menu);
                            } else {
                                st.error = Some("Discord didn't send its report questions.".into());
                            }
                        }
                        Ok(rep) => {
                            st.error = rep.message.or_else(|| {
                                Some("Discord didn't send its report questions.".into())
                            });
                        }
                        Err(e) => st.error = Some(e),
                    }
                }
            },
        );
    }

    pub fn report_advance(&mut self, to_node: i64, picked: Option<String>) {
        let should_auto = {
            let Some(ref mut st) = self.discord.admin.report else {
                return;
            };
            let Some(ref menu) = st.menu else {
                return;
            };
            if !menu.nodes.contains_key(&to_node.to_string()) {
                return;
            }
            if let Some(last) = st.path.last_mut() {
                last.1 = picked;
            }
            st.path.push((to_node, None));
            menu.nodes
                .get(&to_node.to_string())
                .is_some_and(|n| n.is_auto_submit)
                && st.result_ok.is_none()
        };
        if should_auto {
            self.submit_discord_report();
        }
    }

    pub fn report_go_back(&mut self) {
        if let Some(ref mut st) = self.discord.admin.report
            && st.path.len() > 1
            && st.result_ok.is_none()
        {
            st.path.pop();
            if let Some(last) = st.path.last_mut() {
                last.1 = None;
            }
        }
    }

    pub fn submit_discord_report(&mut self) {
        let payload = {
            let free_txt = self.discord.admin.report_free_text.text.trim().to_string();
            let Some(ref mut st) = self.discord.admin.report else {
                return;
            };
            if st.sending || st.result_ok.is_some() {
                return;
            }
            let Some(ref menu) = st.menu else {
                return;
            };
            st.sending = true;
            st.error = None;
            let breadcrumbs: Vec<i64> = st.path.iter().map(|(id, _)| *id).collect();
            let mut elements = serde_json::Map::new();
            for (nid, _) in &st.path {
                if let Some(node) = menu.nodes.get(&nid.to_string()) {
                    for el_item in &node.elements {
                        if el_item.should_submit_data {
                            if el_item.kind == "free_text" {
                                if !free_txt.is_empty() {
                                    elements.insert(el_item.name.clone(), json!([free_txt]));
                                }
                            } else if let Some(vals) = st.answers.get(&el_item.name)
                                && !vals.is_empty()
                            {
                                elements.insert(el_item.name.clone(), json!(vals));
                            }
                        }
                    }
                }
            }
            json!({
                "kind": st.target.kind,
                "action": "send",
                "version": menu.version,
                "variant": menu.variant,
                "language": menu.language.clone().unwrap_or_else(|| "en".into()),
                "breadcrumbs": breadcrumbs,
                "elements": elements,
                "ids": st.target.ids,
            })
        };
        if self.status != HostStatus::Connected {
            if let Some(ref mut st) = self.discord.admin.report {
                st.sending = false;
                st.result_ok = Some(true);
            }
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_REPORT,
            payload,
            |s, res: Result<DiscordReportReply, String>| {
                if let Some(ref mut st) = s.discord.admin.report {
                    st.sending = false;
                    let ok = res.as_ref().is_ok_and(|r| r.ok);
                    if !ok {
                        st.error = res
                            .ok()
                            .and_then(|r| r.message)
                            .or_else(|| Some("Discord didn't take the report.".into()));
                    }
                    st.result_ok = Some(ok);
                    if let Some(ref menu) = st.menu {
                        let next = if ok {
                            menu.success_node_id
                        } else {
                            menu.fail_node_id
                        };
                        if menu.nodes.contains_key(&next.to_string()) {
                            st.path.push((next, None));
                        }
                    }
                }
            },
        );
    }
}

pub fn forum_view(s: &AppState, ch: &DiscordChannel, scale: f32) -> El {
    let ad = &s.discord.admin;
    let q = ad.forum_query.text.trim().to_lowercase();
    let active_tag = ad.forum_selected_tag.as_deref();
    let shown: Vec<&DiscordThread> = ad
        .forum_threads
        .iter()
        .filter(|t| {
            (q.is_empty() || t.name.to_lowercase().contains(&q))
                && active_tag.is_none_or(|tg| t.applied_tags.iter().any(|x| x == tg))
        })
        .collect();

    let header = row()
        .w_full()
        .h(52.0)
        .px(16.0)
        .gap(12.0)
        .center_y()
        .justify_between()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(text(ch.name.clone()).font(type_section()).color(FG))
                .children(ch.topic.as_ref().filter(|t| !t.is_empty()).map(|t| {
                    text(t.clone())
                        .font(type_small())
                        .color(fg_muted())
                        .truncate()
                })),
        )
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    row()
                        .w(200.0)
                        .h(30.0)
                        .px(10.0)
                        .gap(6.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(BG)
                        .border(1.0, hairline())
                        .child(icon_search(13.0, fg_muted()))
                        .child(
                            text_input(&ad.forum_query, type_body())
                                .key("dc-forum-search")
                                .placeholder("Search posts", fg_muted())
                                .grow(1.0)
                                .color(FG)
                                .single_line()
                                .on_edit(edit_access(|s: &mut AppState| {
                                    &mut s.discord.admin.forum_query
                                })),
                        ),
                )
                .children((ch.can_send != Some(false)).then(|| {
                    primary_button(
                        "dc-forum-new-btn",
                        "New post",
                        Some(icon_plus(13.0, PRIMARY_FG)),
                    )
                    .on_press(|s: &mut AppState| {
                        s.discord.admin.forum_new_post_open = !s.discord.admin.forum_new_post_open;
                    })
                })),
        );

    let mut body = col().w_full().padding(16.0).gap(10.0);

    if !ad.forum_tags.is_empty() {
        let mut tag_row = row().gap(6.0).wrap();
        for tag in &ad.forum_tags {
            let selected = active_tag == Some(tag.id.as_str());
            let tid = tag.id.clone();
            let label = match &tag.emoji_name {
                Some(em) if !em.is_empty() => format!("{em} {}", tag.name),
                _ => tag.name.clone(),
            };
            tag_row = tag_row.child(
                filter_pill(format!("dc-ftag-{}", tag.id), label, selected).on_press(
                    move |s: &mut AppState| {
                        if s.discord.admin.forum_selected_tag.as_deref() == Some(tid.as_str()) {
                            s.discord.admin.forum_selected_tag = None;
                        } else {
                            s.discord.admin.forum_selected_tag = Some(tid.clone());
                        }
                    },
                ),
            );
        }
        body = body.child(tag_row);
    }

    if ad.forum_new_post_open {
        let mut post_tags = row().gap(6.0).wrap();
        for tag in &ad.forum_tags {
            let picked = ad.forum_post_tags.contains(&tag.id);
            let tid = tag.id.clone();
            post_tags = post_tags.child(
                filter_pill(format!("dc-fnew-tag-{}", tag.id), tag.name.clone(), picked).on_press(
                    move |s: &mut AppState| {
                        if !s.discord.admin.forum_post_tags.remove(&tid) {
                            s.discord.admin.forum_post_tags.insert(tid.clone());
                        }
                    },
                ),
            );
        }
        body = body.child(
            col()
                .key("dc-forum-new-box")
                .w_full()
                .padding(14.0)
                .gap(10.0)
                .radius(RADIUS_CARD)
                .fill(glass_still_control_fill())
                .border(1.0, hairline())
                .child(
                    text_input(&ad.forum_post_title, type_body())
                        .key("dc-fnew-title")
                        .placeholder("Post title", fg_muted())
                        .w_full()
                        .h(36.0)
                        .px(12.0)
                        .radius(RADIUS_SMALL)
                        .fill(BG)
                        .border(1.0, hairline())
                        .color(FG)
                        .single_line()
                        .on_edit(edit_access(|s: &mut AppState| {
                            &mut s.discord.admin.forum_post_title
                        })),
                )
                .child(
                    text_input(&ad.forum_post_body, type_body())
                        .key("dc-fnew-body")
                        .placeholder("Write the first message", fg_muted())
                        .w_full()
                        .min_h(72.0)
                        .px(12.0)
                        .py(8.0)
                        .radius(RADIUS_SMALL)
                        .fill(BG)
                        .border(1.0, hairline())
                        .color(FG)
                        .on_edit(edit_access(|s: &mut AppState| {
                            &mut s.discord.admin.forum_post_body
                        })),
                )
                .children((!ad.forum_tags.is_empty()).then_some(post_tags))
                .child(
                    row()
                        .w_full()
                        .justify_end()
                        .gap(8.0)
                        .child(glass_button("dc-fnew-cancel", "Cancel", None).on_press(
                            |s: &mut AppState| s.discord.admin.forum_new_post_open = false,
                        ))
                        .child(
                            primary_button("dc-fnew-submit", "Post", None)
                                .on_press(|s: &mut AppState| s.create_discord_forum_post()),
                        ),
                ),
        );
    }

    if let Some(ref err) = ad.forum_error {
        body = body.child(text(err.clone()).font(type_small()).color(bad()));
    }

    if shown.is_empty() && !ad.forum_loading {
        body = body.child(
            text(if !q.is_empty() || active_tag.is_some() {
                "No posts match."
            } else {
                "No posts yet."
            })
            .font(type_body())
            .color(fg_muted())
            .py(16.0),
        );
    } else {
        for th in shown {
            let first = ad.forum_first_messages.get(&th.id);
            let author = first
                .map(|m| m.display_name().to_string())
                .or_else(|| {
                    th.owner.as_ref().and_then(|o| {
                        o.nick
                            .clone()
                            .or_else(|| o.user.as_ref().and_then(|u| u.global_name.clone()))
                            .or_else(|| o.user.as_ref().map(|u| u.username.clone()))
                    })
                })
                .unwrap_or_default();
            let preview = first.map(|m| m.content.clone()).unwrap_or_default();
            let locked = th.thread_metadata.as_ref().is_some_and(|m| m.locked);
            let tid = th.id.clone();
            let tname = th.name.clone();
            let count = th.message_count.unwrap_or(0);
            let mut card = col()
                .key(format!("dc-fpost-{}", th.id))
                .role("button")
                .label(th.name.clone())
                .cursor(Cursor::Pointer)
                .hit()
                .w_full()
                .padding(14.0)
                .gap(6.0)
                .radius(RADIUS_CARD)
                .fill(BG)
                .border(1.0, hairline())
                .hover(hover_fill())
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| s.open_discord_thread(&tid, &tname, locked))
                .child(text(th.name.clone()).font(type_section()).color(FG));
            if !author.is_empty() || !preview.is_empty() {
                card = card.child(
                    text(if author.is_empty() {
                        preview
                    } else {
                        format!("{author}: {preview}")
                    })
                    .font(type_small())
                    .color(fg_muted())
                    .truncate(),
                );
            }
            card = card.child(
                row()
                    .gap(10.0)
                    .child(
                        text(format!("{count} replies"))
                            .font(type_label())
                            .color(fg_faint()),
                    )
                    .children(locked.then(|| text("Locked").font(type_label()).color(fg_faint()))),
            );
            body = body.child(card);
        }
    }

    if ad.forum_has_more && !ad.forum_loading {
        let cid = ch.id.clone();
        body = body.child(
            glass_button("dc-forum-more", "Load older posts", None)
                .on_press(move |s: &mut AppState| s.load_discord_forum(&cid, true)),
        );
    }

    let _ = scale;
    col()
        .key(format!("dc-forum-{}", ch.id))
        .grow(1.0)
        .h_full()
        .min_w(0.0)
        .min_h(0.0)
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, hairline())
        .child(header)
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(body),
        )
}

pub fn server_page_view(
    s: &AppState,
    guild_id: &str,
    chans: Option<&DiscordChannels>,
    page_id: &str,
    scale: f32,
) -> El {
    let title = match page_id {
        PAGE_GUIDE => "Server Guide",
        PAGE_BROWSE => "Browse channels",
        PAGE_MEMBERS => "Members",
        _ => "Server",
    };
    let head = row()
        .w_full()
        .h(52.0)
        .px(16.0)
        .center_y()
        .justify_between()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(text(title).font(type_section()).color(FG));

    let content = match page_id {
        PAGE_GUIDE => server_guide_body(s, guild_id, chans, scale),
        PAGE_BROWSE => server_browse_body(guild_id, chans),
        PAGE_MEMBERS => server_members_body(s, guild_id, chans, scale),
        _ => el(),
    };

    col()
        .key(format!("dc-srvpage-{page_id}"))
        .grow(1.0)
        .h_full()
        .min_w(0.0)
        .min_h(0.0)
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, hairline())
        .child(head)
        .child(content)
}

fn server_guide_body(
    s: &AppState,
    guild_id: &str,
    chans: Option<&DiscordChannels>,
    scale: f32,
) -> El {
    let ad = &s.discord.admin;
    let me_name = s
        .discord
        .home
        .me
        .as_ref()
        .map(|m| m.name.as_str())
        .unwrap_or("you");
    let mut main_col = col().grow(1.0).padding(20.0).gap(12.0);
    if ad.guide_loading && ad.guide_reply.is_none() {
        main_col = main_col.child(
            text("Loading Server Guide…")
                .font(type_body())
                .color(fg_muted()),
        );
    } else if let Some(ref err) = ad.guide_error {
        main_col = main_col.child(text(err.clone()).font(type_body()).color(bad()));
    } else if let Some(ref rep) = ad.guide_reply {
        main_col = main_col.child(
            text(
                chans
                    .and_then(|c| c.name.clone())
                    .unwrap_or_else(|| "Server".into()),
            )
            .font(type_title())
            .color(FG),
        );
        if let Some(msg) = rep
            .guide
            .as_ref()
            .and_then(|g| g.welcome_message.as_ref())
            .and_then(|w| w.message.as_ref())
            .filter(|m| !m.is_empty())
        {
            let formatted = msg.replace("[@username]", &format!("@{me_name}"));
            main_col = main_col.child(text(formatted).font(type_body()).color(fg_muted()));
        }
        if let Some(guide) = &rep.guide
            && !guide.new_member_actions.is_empty()
        {
            main_col = main_col.child(text("Get started").font(type_section()).color(FG).pt(6.0));
            for act in &guide.new_member_actions {
                let cid = act.channel_id.clone();
                main_col = main_col.child(
                    col()
                        .key(format!("dc-guide-act-{}", act.channel_id))
                        .role("button")
                        .label(act.title.clone())
                        .cursor(Cursor::Pointer)
                        .hit()
                        .w_full()
                        .padding(12.0)
                        .gap(4.0)
                        .radius(RADIUS_CARD)
                        .fill(glass_still_control_fill())
                        .border(1.0, hairline())
                        .hover(hover_fill())
                        .on_press(move |s: &mut AppState| {
                            s.discord.admin.active_server_page = None;
                            s.open_discord_channel(&cid);
                        })
                        .child(text(act.title.clone()).font(type_control()).color(FG))
                        .children(
                            act.description
                                .as_ref()
                                .filter(|d| !d.is_empty())
                                .map(|d| text(d.clone()).font(type_label()).color(fg_muted())),
                        ),
                );
            }
        }
        if !rep.resources.is_empty() {
            main_col = main_col.child(text("Resources").font(type_section()).color(FG).pt(6.0));
            for res in &rep.resources {
                let cid = res.channel_id.clone();
                let title = res.title.clone().unwrap_or_else(|| "Resource".into());
                let desc = res
                    .description
                    .clone()
                    .or_else(|| res.preview.as_ref().map(|p| p.content.clone()))
                    .unwrap_or_default();
                main_col =
                    main_col.child(
                        col()
                            .key(format!("dc-guide-res-{}", res.channel_id))
                            .role("button")
                            .label(title.clone())
                            .cursor(Cursor::Pointer)
                            .hit()
                            .w_full()
                            .padding(12.0)
                            .gap(4.0)
                            .radius(RADIUS_CARD)
                            .fill(glass_still_control_fill())
                            .border(1.0, hairline())
                            .hover(hover_fill())
                            .on_press(move |s: &mut AppState| {
                                s.discord.admin.guide_open_resource = Some(cid.clone());
                                s.load_discord_messages(&cid, None);
                            })
                            .child(text(title).font(type_control()).color(FG))
                            .children((!desc.is_empty()).then(|| {
                                text(desc).font(type_label()).color(fg_muted()).truncate()
                            })),
                    );
            }
        }
        let online = rep.online.unwrap_or(0);
        let total = rep.total.unwrap_or(0);
        main_col = main_col.child(
            row()
                .pt(8.0)
                .gap(16.0)
                .child(
                    text(format!("{online} Online"))
                        .font(type_small())
                        .color(good()),
                )
                .child(
                    text(format!("{total} Members"))
                        .font(type_small())
                        .color(fg_muted()),
                ),
        );
    }

    let left = col()
        .grow(1.0)
        .h_full()
        .min_h(0.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .child(main_col);

    if let Some(ref res_cid) = ad.guide_open_resource
        && let Some(ch) = chans.and_then(|c| c.channels.iter().find(|x| x.id == *res_cid))
    {
        let side = col()
            .w(420.0)
            .h_full()
            .min_h(0.0)
            .border_sides([0.0, 0.0, 0.0, 1.0], hairline())
            .child(
                row()
                    .w_full()
                    .h(44.0)
                    .px(14.0)
                    .justify_between()
                    .center_y()
                    .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                    .child(text(ch.name.clone()).font(type_control()).color(FG))
                    .child(
                        el().key("dc-guide-res-close")
                            .cursor(Cursor::Pointer)
                            .hit()
                            .size(26.0, 26.0)
                            .center()
                            .on_press(|s: &mut AppState| {
                                s.discord.admin.guide_open_resource = None;
                            })
                            .child(icon_close(13.0, fg_muted())),
                    ),
            )
            .child(discord_chat::timeline_and_composer(
                s,
                &ch.id,
                format!("Message #{}", ch.name),
                format!("#{}", ch.name),
                ch.topic.clone().unwrap_or_default(),
                ch.can_send != Some(false),
                scale,
            ));
        return row().grow(1.0).w_full().min_h(0.0).child(left).child(side);
    }
    let _ = guild_id;
    left
}

fn server_browse_body(guild_id: &str, chans: Option<&DiscordChannels>) -> El {
    let mut list = col().w_full().padding(18.0).gap(6.0);
    let Some(chans) = chans else {
        return list.child(
            text("No channels loaded.")
                .font(type_body())
                .color(fg_muted()),
        );
    };
    for ch in &chans.channels {
        if ch.kind == "category" {
            list = list.child(
                text(ch.name.clone())
                    .font(type_label())
                    .color(fg_muted())
                    .pt(10.0)
                    .pb(2.0),
            );
            continue;
        }
        let opted = ch.opted_in.unwrap_or(true);
        let cid = ch.id.clone();
        let gid = guild_id.to_string();
        list = list.child(
            row()
                .key(format!("dc-browse-{}", ch.id))
                .w_full()
                .py(8.0)
                .px(12.0)
                .radius(RADIUS_SMALL)
                .border(1.0, hairline())
                .justify_between()
                .center_y()
                .child(
                    col()
                        .gap(2.0)
                        .child(text(format!("# {}", ch.name)).font(type_body()).color(FG))
                        .children(ch.topic.as_ref().filter(|t| !t.is_empty()).map(|t| {
                            text(t.clone())
                                .font(type_label())
                                .color(fg_muted())
                                .truncate()
                        })),
                )
                .child(
                    filter_pill(
                        format!("dc-browse-opt-{}", ch.id),
                        if opted { "Following" } else { "Follow" },
                        opted,
                    )
                    .on_press(move |s: &mut AppState| {
                        s.discord_channel_optin(&cid, !opted);
                        s.load_discord_guild(&gid, false);
                    }),
                ),
        );
    }
    col()
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .child(list)
}

fn server_members_body(
    s: &AppState,
    guild_id: &str,
    chans: Option<&DiscordChannels>,
    scale: f32,
) -> El {
    let ad = &s.discord.admin;
    let can_prune = chans
        .and_then(|c| c.moderation.as_ref())
        .is_some_and(|m| m.prune);
    let gid_search = guild_id.to_string();
    let gid_prune = guild_id.to_string();
    let mut wrap = col().w_full().padding(18.0).gap(10.0).child(
        row()
            .w_full()
            .gap(8.0)
            .center_y()
            .justify_between()
            .child(
                row()
                    .w(280.0)
                    .h(32.0)
                    .px(10.0)
                    .gap(6.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(BG)
                    .border(1.0, hairline())
                    .child(icon_search(13.0, fg_muted()))
                    .child(
                        text_input(&ad.members_query, type_body())
                            .key("dc-srvmem-search")
                            .placeholder("Search members", fg_muted())
                            .grow(1.0)
                            .color(FG)
                            .single_line()
                            .on_edit(edit_access(|s: &mut AppState| {
                                &mut s.discord.admin.members_query
                            }))
                            .on_submit(move |s: &mut AppState, _| {
                                s.load_discord_server_members(&gid_search);
                            }),
                    ),
            )
            .children(can_prune.then(|| {
                let g1 = gid_prune.clone();
                let g2 = gid_prune.clone();
                row()
                    .gap(6.0)
                    .center_y()
                    .children(ad.prune_count.map(|n| {
                        text(format!("{n} inactive"))
                            .font(type_label())
                            .color(fg_muted())
                    }))
                    .child(
                        glass_button("dc-srvmem-prune-chk", "Check prune", None)
                            .on_press(move |s: &mut AppState| s.discord_prune_members(&g1, true)),
                    )
                    .child(
                        glass_button("dc-srvmem-prune-go", "Prune", None)
                            .on_press(move |s: &mut AppState| s.discord_prune_members(&g2, false)),
                    )
            })),
    );

    if ad.members_loading && ad.members_reply.is_none() {
        wrap = wrap.child(text("Loading members…").font(type_body()).color(fg_muted()));
    } else if let Some(ref rep) = ad.members_reply
        && let Some(ref search) = rep.members
    {
        for mrow in &search.members {
            let u = &mrow.member.user;
            let name = mrow
                .member
                .nick
                .clone()
                .or_else(|| u.global_name.clone())
                .unwrap_or_else(|| u.username.clone());
            let av = u.avatar_url();
            let role_names: Vec<&str> = mrow
                .member
                .roles
                .iter()
                .filter_map(|rid| rep.roles.get(rid).map(|r| r.name.as_str()))
                .take(4)
                .collect();
            let ctx = ContextMenuTarget::DiscordMember {
                guild_id: Some(guild_id.to_string()),
                user_id: u.id.clone(),
                name: name.clone(),
                username: Some(u.username.clone()),
                in_voice: false,
            };
            wrap = wrap.child(
                row()
                    .key(format!("dc-srvmem-{}", u.id))
                    .w_full()
                    .py(8.0)
                    .px(12.0)
                    .gap(10.0)
                    .center_y()
                    .justify_between()
                    .radius(RADIUS_SMALL)
                    .border(1.0, hairline())
                    .hover(hover_fill())
                    .hit()
                    .cursor(Cursor::Pointer)
                    .on_secondary_press(move |s: &mut AppState, p, _| {
                        s.open_context_menu(ctx.clone(), p.x, p.y);
                    })
                    .child(
                        row()
                            .gap(10.0)
                            .center_y()
                            .child(avatar_box(
                                s,
                                av.as_deref(),
                                &name,
                                32.0,
                                16.0,
                                None,
                                false,
                                scale,
                            ))
                            .child(
                                col()
                                    .gap(2.0)
                                    .child(text(name).font(type_body()).color(FG))
                                    .child(
                                        text(format!("@{}", u.username))
                                            .font(type_label())
                                            .color(fg_muted()),
                                    ),
                            ),
                    )
                    .child(
                        text(role_names.join(" · "))
                            .font(type_label())
                            .color(fg_muted()),
                    ),
            );
        }
    }

    col()
        .grow(1.0)
        .w_full()
        .min_h(0.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .child(wrap)
}

pub fn shop_view(s: &AppState, scale: f32) -> El {
    let ad = &s.discord.admin;
    let nitro = s
        .discord
        .settings_ext
        .account
        .as_ref()
        .is_some_and(|a| a.premium);
    let owned_skus: HashSet<&str> = ad
        .shop_owned
        .iter()
        .flat_map(|p| {
            std::iter::once(p.sku_id.as_str())
                .chain(product_items(p).iter().map(|i| i.sku_id.as_str()))
        })
        .collect();
    let q = ad.shop_query.text.trim().to_lowercase();

    let mut tabs = row().gap(6.0).center_y();
    for t in ShopTab::ALL {
        tabs = tabs.child(
            filter_pill(
                format!("dc-shop-tab-{}", t.label()),
                t.label(),
                ad.shop_tab == t,
            )
            .on_press(move |s: &mut AppState| s.discord.admin.shop_tab = t),
        );
    }

    let head = row()
        .w_full()
        .h(52.0)
        .px(16.0)
        .gap(12.0)
        .center_y()
        .justify_between()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(
            row()
                .gap(12.0)
                .center_y()
                .child(text("Shop").font(type_section()).color(FG))
                .child(tabs),
        )
        .child(
            row()
                .w(210.0)
                .h(30.0)
                .px(10.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(BG)
                .border(1.0, hairline())
                .child(icon_search(13.0, fg_muted()))
                .child(
                    text_input(&ad.shop_query, type_body())
                        .key("dc-shop-search")
                        .placeholder("Search the shop", fg_muted())
                        .grow(1.0)
                        .color(FG)
                        .single_line()
                        .on_edit(edit_access(|s: &mut AppState| {
                            &mut s.discord.admin.shop_query
                        })),
                ),
        );

    let product_card = |prod: &DiscordShopProduct| -> El {
        let is_owned = owned_skus.contains(prod.sku_id.as_str());
        let price_str = if is_owned {
            "Owned".into()
        } else {
            let tier_key = if nitro { "2" } else { "0" };
            prod.prices
                .get(tier_key)
                .or_else(|| prod.prices.get("0"))
                .and_then(|t| t.country_prices.prices.first())
                .map(|pe| format_shop_price(pe.amount, &pe.currency, pe.exponent))
                .unwrap_or_else(|| "—".into())
        };
        let p_clone = prod.clone();
        col()
            .key(format!("dc-shop-card-{}", prod.sku_id))
            .role("button")
            .label(prod.name.clone())
            .cursor(Cursor::Pointer)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .w(210.0)
            .padding(14.0)
            .gap(8.0)
            .radius(RADIUS_CARD)
            .fill(glass_still_control_fill())
            .border(1.0, hairline())
            .hover(hover_fill())
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| s.open_shop_product(p_clone.clone()))
            .child(
                el().w_full()
                    .h(96.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border(1.0, hairline())
                    .center()
                    .child(
                        text(
                            product_kind(prod)
                                .map(|k| k.label())
                                .unwrap_or("Collectible"),
                        )
                        .font(type_label())
                        .color(fg_muted()),
                    ),
            )
            .child(
                text(prod.name.clone())
                    .font(type_control())
                    .color(FG)
                    .truncate(),
            )
            .child(text(price_str).font(type_small()).color(if is_owned {
                good()
            } else {
                fg_muted()
            }))
    };

    let mut body = col().w_full().padding(18.0).gap(16.0);
    if let Some(ref err) = ad.shop_error {
        body = body.child(
            row()
                .gap(10.0)
                .center_y()
                .child(text(err.clone()).font(type_body()).color(bad()))
                .child(
                    glass_button("dc-shop-retry", "Try again", None)
                        .on_press(|s: &mut AppState| s.load_discord_shop(true)),
                ),
        );
    } else if ad.shop_loading && ad.shop_catalog.is_none() {
        body = body.child(
            text("Loading the shop…")
                .font(type_body())
                .color(fg_muted()),
        );
    } else if ad.shop_tab == ShopTab::Collection {
        let items: Vec<&DiscordShopProduct> = ad
            .shop_owned
            .iter()
            .filter(|p| q.is_empty() || p.name.to_lowercase().contains(&q))
            .collect();
        if items.is_empty() {
            body = body.child(
                text("Collectibles you own show here.")
                    .font(type_body())
                    .color(fg_muted()),
            );
        } else {
            let mut grid = row().w_full().gap(12.0).wrap();
            for p in items {
                grid = grid.child(product_card(p));
            }
            body = body.child(grid);
        }
    } else if let Some(ref cat) = ad.shop_catalog {
        if ad.shop_tab == ShopTab::Featured {
            for category in &cat.categories {
                let prods: Vec<&DiscordShopProduct> = category
                    .products
                    .iter()
                    .filter(|p| {
                        product_kind(p).is_some()
                            && (q.is_empty() || p.name.to_lowercase().contains(&q))
                    })
                    .collect();
                if prods.is_empty() {
                    continue;
                }
                let mut grid = row().w_full().gap(12.0).wrap();
                for p in prods {
                    grid = grid.child(product_card(p));
                }
                body = body.child(
                    col()
                        .w_full()
                        .gap(10.0)
                        .child(text(category.name.clone()).font(type_section()).color(FG))
                        .child(grid),
                );
            }
        } else {
            let mut seen = HashSet::new();
            let mut grid = row().w_full().gap(12.0).wrap();
            for category in &cat.categories {
                for p in &category.products {
                    if product_kind(p) == Some(ad.shop_tab)
                        && (q.is_empty() || p.name.to_lowercase().contains(&q))
                        && seen.insert(p.sku_id.as_str())
                    {
                        grid = grid.child(product_card(p));
                    }
                }
            }
            body = body.child(grid);
        }
    }

    let _ = scale;
    col()
        .key("dc-shop-view")
        .grow(1.0)
        .h_full()
        .min_w(0.0)
        .min_h(0.0)
        .radius(RADIUS_PANEL)
        .fill(BG)
        .border(1.0, hairline())
        .child(head)
        .child(
            col()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(body),
        )
}

pub fn overlay_layers(s: &AppState) -> Vec<El> {
    let ad = &s.discord.admin;
    let mut out = Vec::new();
    if ad.events_open {
        out.push(events_modal(s));
    }
    if ad.create_channel.is_some() {
        out.push(create_channel_modal(s));
    }
    if ad.invite_dialog.is_some() {
        out.push(invite_modal(s));
    }
    if ad.server_privacy_guild.is_some() {
        out.push(server_privacy_modal(s));
    }
    if ad.voice_status_dialog.is_some() {
        out.push(voice_status_modal(s));
    }
    if ad.member_dialog.is_some() {
        out.push(member_dialog_modal(s));
    }
    if ad.channel_settings_id.is_some() {
        out.push(channel_settings_modal(s));
    }
    if ad.server_settings_guild_id.is_some() {
        out.push(server_settings_modal(s));
    }
    if ad.shop_selected.is_some() {
        out.push(shop_product_modal(s));
    }
    if ad.report.is_some() {
        out.push(report_modal(s));
    }
    out
}

fn glass_dialog_frame(key: &str, w: f32) -> El {
    apply_glass(
        col()
            .key(key)
            .w(w)
            .padding(20.0)
            .gap(12.0)
            .radius(RADIUS_PANEL)
            .transition(modal_card_transition())
            .hit(),
        ExoGlass::Sheet,
    )
}

fn modal_header(title: &str, close_key: &str, on_close: impl Fn(&mut AppState) + 'static) -> El {
    row()
        .w_full()
        .justify_between()
        .center_y()
        .child(text(title).font(type_section()).color(FG))
        .child(
            el().key(close_key)
                .role("button")
                .label("Close")
                .cursor(Cursor::Pointer)
                .hit()
                .size(28.0, 28.0)
                .radius(RADIUS_PILL)
                .hover(hover_fill())
                .center()
                .on_press(on_close)
                .child(icon_close(14.0, FG)),
        )
}

fn events_modal(s: &AppState) -> El {
    let ad = &s.discord.admin;
    let gid = ad.events_guild_id.clone().unwrap_or_default();
    let can_manage = s
        .discord
        .channels_by_guild
        .get(&gid)
        .is_some_and(|c| c.can_manage_events);

    let mut panel = glass_dialog_frame("dc-events-modal", 560.0).child(modal_header(
        "Events",
        "dc-events-x",
        |s: &mut AppState| s.discord.admin.events_open = false,
    ));

    if let Some(ref err) = ad.event_error {
        panel = panel.child(text(err.clone()).font(type_small()).color(bad()));
    }

    if ad.events_create_open {
        panel = panel
            .child(
                text_input(&ad.event_name, type_body())
                    .key("dc-ev-name")
                    .placeholder("Event topic", fg_muted())
                    .w_full()
                    .h(36.0)
                    .px(12.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border(1.0, hairline())
                    .color(FG)
                    .single_line()
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.discord.admin.event_name
                    })),
            )
            .child(
                text_input(&ad.event_location, type_body())
                    .key("dc-ev-loc")
                    .placeholder("Location", fg_muted())
                    .w_full()
                    .h(36.0)
                    .px(12.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border(1.0, hairline())
                    .color(FG)
                    .single_line()
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.discord.admin.event_location
                    })),
            )
            .child(
                text_input(&ad.event_desc, type_body())
                    .key("dc-ev-desc")
                    .placeholder("Description (optional)", fg_muted())
                    .w_full()
                    .min_h(64.0)
                    .px(12.0)
                    .py(8.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border(1.0, hairline())
                    .color(FG)
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.discord.admin.event_desc
                    })),
            )
            .child(
                row()
                    .w_full()
                    .justify_end()
                    .gap(8.0)
                    .child(
                        glass_button_opt("dc-ev-cancel", "Back", None, false).on_press(
                            |s: &mut AppState| {
                                s.discord.admin.events_create_open = false;
                            },
                        ),
                    )
                    .child(
                        primary_button("dc-ev-create-go", "Create event", None)
                            .on_press(|s: &mut AppState| s.create_discord_event()),
                    ),
            );
    } else {
        if can_manage {
            panel = panel.child(
                row().justify_end().child(
                    primary_button(
                        "dc-ev-new",
                        "Create event",
                        Some(icon_plus(13.0, PRIMARY_FG)),
                    )
                    .on_press(|s: &mut AppState| s.discord.admin.events_create_open = true),
                ),
            );
        }
        let mut list = col().w_full().max_h(380.0).gap(8.0).scroll(ScrollSpec {
            stick_bottom: false,
        });
        if ad.events_loading && ad.events_list.is_empty() {
            list = list.child(text("Loading events…").font(type_body()).color(fg_muted()));
        } else if ad.events_list.is_empty() {
            list = list.child(
                text("No upcoming events.")
                    .font(type_body())
                    .color(fg_muted()),
            );
        } else {
            for entry in &ad.events_list {
                let ev = &entry.event;
                let eid = ev.id.clone();
                let eid_start = ev.id.clone();
                let eid_end = ev.id.clone();
                let interested = entry.interested;
                let live = ev.status == 2;
                list = list.child(
                    col()
                        .key(format!("dc-ev-card-{}", ev.id))
                        .w_full()
                        .padding(12.0)
                        .gap(6.0)
                        .radius(RADIUS_CARD)
                        .fill(BG)
                        .border(1.0, hairline())
                        .child(
                            row()
                                .w_full()
                                .justify_between()
                                .center_y()
                                .child(text(ev.name.clone()).font(type_control()).color(FG))
                                .child(
                                    text(if live { "Live" } else { "Scheduled" })
                                        .font(type_label())
                                        .color(if live { good() } else { fg_muted() }),
                                ),
                        )
                        .children(
                            ev.description
                                .as_ref()
                                .filter(|d| !d.is_empty())
                                .map(|d| text(d.clone()).font(type_small()).color(fg_muted())),
                        )
                        .child(
                            row()
                                .gap(8.0)
                                .center_y()
                                .child(
                                    filter_pill(
                                        format!("dc-ev-rsvp-{}", ev.id),
                                        if interested { "Interested" } else { "Interest" },
                                        interested,
                                    )
                                    .on_press(
                                        move |s: &mut AppState| {
                                            s.toggle_discord_event_rsvp(&eid, !interested);
                                        },
                                    ),
                                )
                                .children((can_manage && !live).then(|| {
                                    glass_button_opt("dc-ev-start", "Start", None, false)
                                        .key(format!("dc-ev-start-{}", ev.id))
                                        .on_press(move |s: &mut AppState| {
                                            s.set_discord_event_status(&eid_start, 2);
                                        })
                                }))
                                .children(can_manage.then(|| {
                                    glass_button_opt(
                                        "dc-ev-end",
                                        if live { "End" } else { "Cancel" },
                                        None,
                                        false,
                                    )
                                    .key(format!("dc-ev-end-{}", ev.id))
                                    .on_press(
                                        move |s: &mut AppState| {
                                            s.set_discord_event_status(
                                                &eid_end,
                                                if live { 3 } else { 4 },
                                            );
                                        },
                                    )
                                })),
                        ),
                );
            }
        }
        panel = panel.child(list);
    }

    el().key("dc-events-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(
            el().key("dc-events-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .hit()
                .press_on_down()
                .transition(modal_scrim_transition())
                .on_press(|s: &mut AppState| s.discord.admin.events_open = false),
        )
        .child(panel)
}

fn create_channel_modal(s: &AppState) -> El {
    let Some(dlg) = s.discord.admin.create_channel.as_ref() else {
        return el();
    };
    let title = if dlg.as_category {
        "Create category"
    } else {
        "Create channel"
    };
    let mut panel = glass_dialog_frame("dc-create-ch-modal", 420.0).child(modal_header(
        title,
        "dc-create-ch-x",
        |s: &mut AppState| s.discord.admin.create_channel = None,
    ));
    if !dlg.as_category {
        panel = panel.child(
            row()
                .gap(8.0)
                .child(
                    filter_pill("dc-cch-text", "Text", dlg.kind == "text").on_press(
                        |s: &mut AppState| {
                            if let Some(ref mut d) = s.discord.admin.create_channel {
                                d.kind = "text".into();
                            }
                        },
                    ),
                )
                .child(
                    filter_pill("dc-cch-voice", "Voice", dlg.kind == "voice").on_press(
                        |s: &mut AppState| {
                            if let Some(ref mut d) = s.discord.admin.create_channel {
                                d.kind = "voice".into();
                            }
                        },
                    ),
                ),
        );
    }
    panel = panel
        .child(
            text_input(&s.discord.admin.create_channel_name, type_body())
                .key("dc-cch-name")
                .placeholder(
                    if dlg.as_category {
                        "Category name"
                    } else {
                        "new-channel"
                    },
                    fg_muted(),
                )
                .w_full()
                .h(36.0)
                .px(12.0)
                .radius(RADIUS_SMALL)
                .fill(BG)
                .border(1.0, hairline())
                .color(FG)
                .single_line()
                .on_edit(edit_access(|s: &mut AppState| {
                    &mut s.discord.admin.create_channel_name
                }))
                .on_submit(|s: &mut AppState, _| s.submit_create_channel()),
        )
        .children(
            dlg.error
                .as_ref()
                .map(|e| text(e.clone()).font(type_small()).color(bad())),
        )
        .child(
            row()
                .w_full()
                .justify_end()
                .gap(8.0)
                .child(
                    glass_button_opt("dc-cch-cancel", "Cancel", None, false)
                        .on_press(|s: &mut AppState| s.discord.admin.create_channel = None),
                )
                .child(
                    primary_button("dc-cch-go", "Create", None)
                        .on_press(|s: &mut AppState| s.submit_create_channel()),
                ),
        );

    el().key("dc-cch-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(
            el().key("dc-cch-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .hit()
                .press_on_down()
                .transition(modal_scrim_transition())
                .on_press(|s: &mut AppState| s.discord.admin.create_channel = None),
        )
        .child(panel)
}

fn invite_modal(s: &AppState) -> El {
    let Some(dlg) = s.discord.admin.invite_dialog.as_ref() else {
        return el();
    };
    let hides = s.discord.settings_ext.streamer_hides();
    let link = dlg.code.clone().unwrap_or_default();
    let shown_link = if hides && !link.is_empty() {
        "Invite link hidden (Streamer Mode)".into()
    } else if dlg.loading {
        "Creating invite…".into()
    } else {
        link.clone()
    };

    let panel = glass_dialog_frame("dc-invite-modal", 440.0)
        .child(modal_header(
            &format!("Invite people to {}", dlg.guild_name),
            "dc-invite-x",
            |s: &mut AppState| s.discord.admin.invite_dialog = None,
        ))
        .child(
            text(format!("Channel: #{}", dlg.channel_name))
                .font(type_label())
                .color(fg_muted()),
        )
        .child(
            row()
                .w_full()
                .padding(10.0)
                .radius(RADIUS_SMALL)
                .fill(BG)
                .border(1.0, hairline())
                .justify_between()
                .center_y()
                .child(text(shown_link).font(type_body()).color(FG).truncate())
                .children((!link.is_empty()).then(|| {
                    let copy_url = link.clone();
                    primary_button("dc-invite-copy", "Copy", None).on_press_tx(
                        move |s: &mut AppState, tx| {
                            tx.copy(copy_url.clone());
                            s.notice = Some("Invite link copied".into());
                        },
                    )
                })),
        )
        .children(
            dlg.error
                .as_ref()
                .map(|e| text(e.clone()).font(type_small()).color(bad())),
        )
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(
                    glass_button_opt("dc-invite-new", "New link", None, false)
                        .on_press(|s: &mut AppState| s.generate_discord_invite()),
                )
                .child(
                    glass_button_opt("dc-invite-done", "Done", None, false)
                        .on_press(|s: &mut AppState| s.discord.admin.invite_dialog = None),
                ),
        );

    el().key("dc-invite-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(
            el().key("dc-invite-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .hit()
                .press_on_down()
                .transition(modal_scrim_transition())
                .on_press(|s: &mut AppState| s.discord.admin.invite_dialog = None),
        )
        .child(panel)
}

fn server_privacy_modal(s: &AppState) -> El {
    let Some(gid) = s.discord.admin.server_privacy_guild.clone() else {
        return el();
    };
    let u = &s.discord.settings_ext.user;
    let dms_off = u.restricted_guild_ids.iter().any(|x| x == &gid);
    let req_off = u
        .message_request_restricted_guild_ids
        .iter()
        .any(|x| x == &gid);
    let act_off = u.activity_restricted_guild_ids.iter().any(|x| x == &gid);
    let join_off = u
        .activity_joining_restricted_guild_ids
        .iter()
        .any(|x| x == &gid);

    let toggle_in_vec =
        |field: &'static str, mut list: Vec<String>, g: String, remove: bool| -> Value {
            if remove {
                list.retain(|x| x != &g);
            } else if !list.iter().any(|x| x == &g) {
                list.push(g);
            }
            json!({ field: list })
        };

    let g1 = gid.clone();
    let g2 = gid.clone();
    let g3 = gid.clone();
    let g4 = gid.clone();

    let panel = glass_dialog_frame("dc-srvpriv-modal", 460.0)
        .child(modal_header(
            "Privacy settings",
            "dc-srvpriv-x",
            |s: &mut AppState| s.discord.admin.server_privacy_guild = None,
        ))
        .child(simple_switch_row(
            "dc-sp-dm",
            "Direct messages",
            !dms_off,
            move |s: &mut AppState| {
                let v = toggle_in_vec(
                    "restrictedGuildIds",
                    s.discord.settings_ext.user.restricted_guild_ids.clone(),
                    g1.clone(),
                    dms_off,
                );
                s.save_discord_user_settings(v);
            },
        ))
        .child(simple_switch_row(
            "dc-sp-req",
            "Message requests",
            !req_off,
            move |s: &mut AppState| {
                let v = toggle_in_vec(
                    "messageRequestRestrictedGuildIds",
                    s.discord
                        .settings_ext
                        .user
                        .message_request_restricted_guild_ids
                        .clone(),
                    g2.clone(),
                    req_off,
                );
                s.save_discord_user_settings(v);
            },
        ))
        .child(simple_switch_row(
            "dc-sp-act",
            "Activity status",
            !act_off,
            move |s: &mut AppState| {
                let v = toggle_in_vec(
                    "activityRestrictedGuildIds",
                    s.discord
                        .settings_ext
                        .user
                        .activity_restricted_guild_ids
                        .clone(),
                    g3.clone(),
                    act_off,
                );
                s.save_discord_user_settings(v);
            },
        ))
        .child(simple_switch_row(
            "dc-sp-join",
            "Allow joining my activity",
            !join_off,
            move |s: &mut AppState| {
                let v = toggle_in_vec(
                    "activityJoiningRestrictedGuildIds",
                    s.discord
                        .settings_ext
                        .user
                        .activity_joining_restricted_guild_ids
                        .clone(),
                    g4.clone(),
                    join_off,
                );
                s.save_discord_user_settings(v);
            },
        ))
        .child(
            row().justify_end().child(
                primary_button("dc-sp-done", "Done", None)
                    .on_press(|s: &mut AppState| s.discord.admin.server_privacy_guild = None),
            ),
        );

    el().key("dc-srvpriv-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(
            el().key("dc-srvpriv-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .hit()
                .press_on_down()
                .transition(modal_scrim_transition())
                .on_press(|s: &mut AppState| s.discord.admin.server_privacy_guild = None),
        )
        .child(panel)
}

fn simple_switch_row(
    key: &str,
    label: &str,
    on: bool,
    on_press: impl Fn(&mut AppState) + 'static,
) -> El {
    row()
        .key(key)
        .role("switch")
        .label(label)
        .cursor(Cursor::Pointer)
        .hit()
        .w_full()
        .py(8.0)
        .justify_between()
        .center_y()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .on_press(on_press)
        .child(text(label).font(type_body()).color(FG))
        .child(
            text(if on { "On" } else { "Off" })
                .font(type_control())
                .color(if on { good() } else { fg_muted() }),
        )
}

fn voice_status_modal(s: &AppState) -> El {
    let Some(dlg) = s.discord.admin.voice_status_dialog.as_ref() else {
        return el();
    };
    let cid = dlg.channel_id.clone();
    let cid_clear = dlg.channel_id.clone();
    let panel = glass_dialog_frame("dc-vstatus-modal", 420.0)
        .child(modal_header(
            "Set channel status",
            "dc-vstatus-x",
            |s: &mut AppState| s.discord.admin.voice_status_dialog = None,
        ))
        .child(
            text_input(&s.discord.admin.voice_status_input, type_body())
                .key("dc-vstatus-input")
                .placeholder("What's happening in this channel?", fg_muted())
                .w_full()
                .h(36.0)
                .px(12.0)
                .radius(RADIUS_SMALL)
                .fill(BG)
                .border(1.0, hairline())
                .color(FG)
                .single_line()
                .on_edit(edit_access(|s: &mut AppState| {
                    &mut s.discord.admin.voice_status_input
                })),
        )
        .children(
            dlg.error
                .as_ref()
                .map(|e| text(e.clone()).font(type_small()).color(bad())),
        )
        .child(
            row()
                .w_full()
                .justify_end()
                .gap(8.0)
                .child(
                    glass_button_opt("dc-vstatus-clear", "Clear", None, false).on_press(
                        move |s: &mut AppState| {
                            s.save_discord_voice_status(&cid_clear, "");
                        },
                    ),
                )
                .child(primary_button("dc-vstatus-save", "Save", None).on_press(
                    move |s: &mut AppState| {
                        let txt = s.discord.admin.voice_status_input.text.to_string();
                        s.save_discord_voice_status(&cid, &txt);
                    },
                )),
        );

    el().key("dc-vstatus-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(
            el().key("dc-vstatus-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .hit()
                .press_on_down()
                .transition(modal_scrim_transition())
                .on_press(|s: &mut AppState| s.discord.admin.voice_status_dialog = None),
        )
        .child(panel)
}

fn member_dialog_modal(s: &AppState) -> El {
    let Some(dlg) = s.discord.admin.member_dialog.as_ref() else {
        return el();
    };
    let ad = &s.discord.admin;
    let title = match dlg.kind {
        MemberDialogKind::Nick => format!("Change nickname for {}", dlg.name),
        MemberDialogKind::Kick => format!("Kick {}", dlg.name),
        MemberDialogKind::Ban => format!("Ban {}", dlg.name),
        MemberDialogKind::Transfer => format!("Transfer ownership to {}", dlg.name),
    };
    let mut panel = glass_dialog_frame("dc-memdlg-modal", 440.0).child(modal_header(
        &title,
        "dc-memdlg-x",
        |s: &mut AppState| s.discord.admin.member_dialog = None,
    ));

    match dlg.kind {
        MemberDialogKind::Nick => {
            panel = panel.child(
                text_input(&ad.member_input, type_body())
                    .key("dc-memdlg-nick")
                    .placeholder("Nickname (leave blank to reset)", fg_muted())
                    .w_full()
                    .h(36.0)
                    .px(12.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border(1.0, hairline())
                    .color(FG)
                    .single_line()
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.discord.admin.member_input
                    }))
                    .on_submit(|s: &mut AppState, _| s.submit_member_admin_dialog()),
            );
        }
        MemberDialogKind::Kick | MemberDialogKind::Ban => {
            panel = panel.child(
                text_input(&ad.member_reason, type_body())
                    .key("dc-memdlg-reason")
                    .placeholder("Reason (optional)", fg_muted())
                    .w_full()
                    .h(36.0)
                    .px(12.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border(1.0, hairline())
                    .color(FG)
                    .single_line()
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.discord.admin.member_reason
                    })),
            );
            if dlg.kind == MemberDialogKind::Ban {
                let mut del_row = row().gap(6.0).wrap();
                for (secs, lbl) in [
                    (0, "Don't delete"),
                    (3600, "Previous hour"),
                    (86400, "Previous 24h"),
                    (604800, "Previous 7d"),
                ] {
                    del_row = del_row.child(
                        filter_pill(
                            format!("dc-ban-del-{secs}"),
                            lbl,
                            ad.member_ban_delete_secs == secs,
                        )
                        .on_press(move |s: &mut AppState| {
                            s.discord.admin.member_ban_delete_secs = secs;
                        }),
                    );
                }
                panel = panel
                    .child(
                        text("Delete message history")
                            .font(type_label())
                            .color(fg_muted()),
                    )
                    .child(del_row);
            }
        }
        MemberDialogKind::Transfer => {
            panel = panel.child(
                text("This transfers server ownership permanently.")
                    .font(type_body())
                    .color(fg_muted()),
            );
        }
    }

    if let Some(ref err) = ad.member_error {
        panel = panel.child(text(err.clone()).font(type_small()).color(bad()));
    }

    let action_label = match dlg.kind {
        MemberDialogKind::Nick => "Save",
        MemberDialogKind::Kick => "Kick",
        MemberDialogKind::Ban => "Ban",
        MemberDialogKind::Transfer => "Transfer",
    };

    panel = panel.child(
        row()
            .w_full()
            .justify_end()
            .gap(8.0)
            .child(
                glass_button_opt("dc-memdlg-cancel", "Cancel", None, false)
                    .on_press(|s: &mut AppState| s.discord.admin.member_dialog = None),
            )
            .child(
                primary_button("dc-memdlg-ok", action_label, None)
                    .on_press(|s: &mut AppState| s.submit_member_admin_dialog()),
            ),
    );

    el().key("dc-memdlg-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(
            el().key("dc-memdlg-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .hit()
                .press_on_down()
                .transition(modal_scrim_transition())
                .on_press(|s: &mut AppState| s.discord.admin.member_dialog = None),
        )
        .child(panel)
}

fn channel_settings_modal(s: &AppState) -> El {
    let ad = &s.discord.admin;
    let cid = ad.channel_settings_id.clone().unwrap_or_default();
    let data = ad.channel_settings_data.as_ref();
    let ch_type = data
        .and_then(|d| d.get("type").and_then(|x| x.as_i64()))
        .unwrap_or(0) as i32;
    let is_voice = ch_type == 2 || ch_type == 13;
    let is_cat = ch_type == 4;
    let can_manage = data
        .and_then(|d| d.get("canManage").and_then(|x| x.as_bool()))
        .unwrap_or(true);
    let can_perms = data
        .and_then(|d| d.get("canPermissions").and_then(|x| x.as_bool()))
        .unwrap_or(true);

    let mut nav = col()
        .w(196.0)
        .h_full()
        .py(18.0)
        .px(12.0)
        .gap(4.0)
        .border_sides([0.0, 1.0, 0.0, 0.0], hairline())
        .child(
            text("Edit channel")
                .font(type_section())
                .color(FG)
                .px(10.0)
                .pb(8.0),
        );
    if can_manage {
        let active = ad.channel_settings_tab == ChannelSettingsTab::Overview;
        nav = nav.child(
            row()
                .key("dc-chset-tab-ov")
                .cursor(Cursor::Pointer)
                .hit()
                .w_full()
                .h(32.0)
                .px(10.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(if active { selected_fill() } else { TRANSPARENT })
                .hover(hover_fill())
                .on_press(|s: &mut AppState| {
                    s.discord.admin.channel_settings_tab = ChannelSettingsTab::Overview;
                })
                .child(text("Overview").font(type_control()).color(FG)),
        );
    }
    if can_perms {
        let active = ad.channel_settings_tab == ChannelSettingsTab::Permissions;
        nav = nav.child(
            row()
                .key("dc-chset-tab-perm")
                .cursor(Cursor::Pointer)
                .hit()
                .w_full()
                .h(32.0)
                .px(10.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(if active { selected_fill() } else { TRANSPARENT })
                .hover(hover_fill())
                .on_press(|s: &mut AppState| {
                    s.discord.admin.channel_settings_tab = ChannelSettingsTab::Permissions;
                })
                .child(text("Permissions").font(type_control()).color(FG)),
        );
    }

    let mut body = col().w_full().gap(10.0);
    if ad.channel_settings_loading && data.is_none() {
        body = body.child(text("Loading channel…").font(type_body()).color(fg_muted()));
    } else if ad.channel_settings_tab == ChannelSettingsTab::Overview {
        body = body
            .child(text("Overview").font(type_title()).color(FG))
            .child(
                text(if is_cat {
                    "Category name"
                } else {
                    "Channel name"
                })
                .font(type_label())
                .color(fg_muted()),
            )
            .child(
                text_input(&ad.ch_name, type_body())
                    .key("dc-chset-name")
                    .w_full()
                    .h(36.0)
                    .px(12.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border(1.0, hairline())
                    .color(FG)
                    .single_line()
                    .on_edit(edit_access(|s: &mut AppState| {
                        s.discord.admin.ch_dirty = true;
                        &mut s.discord.admin.ch_name
                    })),
            );
        if !is_cat {
            if !is_voice {
                body = body
                    .child(text("Channel topic").font(type_label()).color(fg_muted()))
                    .child(
                        text_input(&ad.ch_topic, type_body())
                            .key("dc-chset-topic")
                            .w_full()
                            .min_h(64.0)
                            .px(12.0)
                            .py(8.0)
                            .radius(RADIUS_SMALL)
                            .fill(BG)
                            .border(1.0, hairline())
                            .color(FG)
                            .on_edit(edit_access(|s: &mut AppState| {
                                s.discord.admin.ch_dirty = true;
                                &mut s.discord.admin.ch_topic
                            })),
                    );
            }
            let mut slow_row = row().gap(6.0).wrap();
            for (secs, lbl) in SLOWMODE_STOPS {
                slow_row = slow_row.child(
                    filter_pill(format!("dc-ch-slow-{secs}"), lbl, ad.ch_slowmode == secs)
                        .on_press(move |s: &mut AppState| {
                            s.discord.admin.ch_slowmode = secs;
                            s.discord.admin.ch_dirty = true;
                        }),
                );
            }
            body = body
                .child(text("Slowmode").font(type_label()).color(fg_muted()))
                .child(slow_row)
                .child(simple_switch_row(
                    "dc-ch-nsfw",
                    "Age-restricted channel",
                    ad.ch_nsfw,
                    |s: &mut AppState| {
                        s.discord.admin.ch_nsfw = !s.discord.admin.ch_nsfw;
                        s.discord.admin.ch_dirty = true;
                    },
                ));
            if is_voice {
                let kbps = ad.ch_bitrate / 1000;
                let max_br = data
                    .and_then(|d| d.get("maxBitrate").and_then(|x| x.as_i64()))
                    .unwrap_or(96_000) as i32;
                let ulim = ad.ch_user_limit;
                body = body
                    .child(
                        row()
                            .w_full()
                            .justify_between()
                            .center_y()
                            .child(
                                text(format!("Bitrate: {kbps} kbps"))
                                    .font(type_body())
                                    .color(FG),
                            )
                            .child(
                                row()
                                    .gap(6.0)
                                    .child(
                                        glass_button_opt("dc-ch-br-dn", "−8k", None, false)
                                            .on_press(|s: &mut AppState| {
                                                s.discord.admin.ch_bitrate =
                                                    (s.discord.admin.ch_bitrate - 8000).max(8000);
                                                s.discord.admin.ch_dirty = true;
                                            }),
                                    )
                                    .child(
                                        glass_button_opt("dc-ch-br-up", "+8k", None, false)
                                            .on_press(move |s: &mut AppState| {
                                                s.discord.admin.ch_bitrate =
                                                    (s.discord.admin.ch_bitrate + 8000).min(max_br);
                                                s.discord.admin.ch_dirty = true;
                                            }),
                                    ),
                            ),
                    )
                    .child(
                        row()
                            .w_full()
                            .justify_between()
                            .center_y()
                            .child(
                                text(if ulim == 0 {
                                    "User limit: No limit".into()
                                } else {
                                    format!("User limit: {ulim}")
                                })
                                .font(type_body())
                                .color(FG),
                            )
                            .child(
                                row()
                                    .gap(6.0)
                                    .child(
                                        glass_button_opt("dc-ch-ul-dn", "−1", None, false)
                                            .on_press(|s: &mut AppState| {
                                                s.discord.admin.ch_user_limit =
                                                    (s.discord.admin.ch_user_limit - 1).max(0);
                                                s.discord.admin.ch_dirty = true;
                                            }),
                                    )
                                    .child(
                                        glass_button_opt("dc-ch-ul-up", "+1", None, false)
                                            .on_press(|s: &mut AppState| {
                                                s.discord.admin.ch_user_limit =
                                                    (s.discord.admin.ch_user_limit + 1).min(99);
                                                s.discord.admin.ch_dirty = true;
                                            }),
                                    ),
                            ),
                    );
            }
        }
        if ad.ch_dirty {
            body = body.child(
                row().pt(6.0).gap(8.0).justify_end().child(
                    primary_button("dc-chset-save", "Save changes", None)
                        .on_press(|s: &mut AppState| s.save_channel_settings_overview()),
                ),
            );
        }
        let cid_del = cid.clone();
        body = body.child(
            row()
                .pt(12.0)
                .w_full()
                .justify_between()
                .center_y()
                .border_sides([1.0, 0.0, 0.0, 0.0], hairline())
                .child(text("Delete channel").font(type_body()).color(bad()))
                .child(if ad.ch_delete_confirm {
                    row()
                        .gap(6.0)
                        .child(
                            glass_button_opt("dc-chdel-no", "Cancel", None, false).on_press(
                                |s: &mut AppState| s.discord.admin.ch_delete_confirm = false,
                            ),
                        )
                        .child(
                            primary_button("dc-chdel-yes", "Confirm delete", None).on_press(
                                move |s: &mut AppState| {
                                    s.discord_channel_action(&cid_del, "delete");
                                    s.discord.admin.channel_settings_id = None;
                                },
                            ),
                        )
                } else {
                    glass_button_opt("dc-chdel-ask", "Delete channel", None, false)
                        .on_press(|s: &mut AppState| s.discord.admin.ch_delete_confirm = true)
                }),
        );
    } else if let Some(d) = data {
        body = body.child(text("Permissions").font(type_title()).color(FG));
        let guild_id = d.get("guildId").and_then(|x| x.as_str()).unwrap_or("");
        let picked_id = ad
            .ch_perm_target_id
            .as_deref()
            .unwrap_or(guild_id)
            .to_string();
        if let Some(roles) = d.get("roles").and_then(|x| x.as_array()) {
            let mut target_row = row().gap(6.0).wrap();
            for r in roles.iter().take(12) {
                let rid = r
                    .get("id")
                    .and_then(|x| x.as_str())
                    .unwrap_or("")
                    .to_string();
                let rname = r
                    .get("name")
                    .and_then(|x| x.as_str())
                    .unwrap_or("Role")
                    .to_string();
                let active = rid == picked_id;
                let rid_c = rid.clone();
                target_row = target_row.child(
                    filter_pill(format!("dc-chp-role-{rid}"), rname, active).on_press(
                        move |s: &mut AppState| {
                            s.discord.admin.ch_perm_target_id = Some(rid_c.clone());
                        },
                    ),
                );
            }
            body = body.child(target_row);
        }
        let (allow, deny) = if let Some((_, a, dn)) = ad.ch_perm_edits.get(&picked_id) {
            (a.clone(), dn.clone())
        } else {
            d.get("overwrites")
                .and_then(|x| x.as_array())
                .and_then(|arr| {
                    arr.iter()
                        .find(|o| o.get("id").and_then(|x| x.as_str()) == Some(picked_id.as_str()))
                })
                .map(|o| {
                    (
                        o.get("allow")
                            .and_then(|x| x.as_str())
                            .unwrap_or("0")
                            .to_string(),
                        o.get("deny")
                            .and_then(|x| x.as_str())
                            .unwrap_or("0")
                            .to_string(),
                    )
                })
                .unwrap_or_else(|| ("0".into(), "0".into()))
        };
        for (_, bit, lbl) in CHANNEL_PERMISSIONS {
            let tri = perm_bit_tri(&allow, &deny, bit);
            let tid_c = picked_id.clone();
            let a_c = allow.clone();
            let d_c = deny.clone();
            body = body.child(
                row()
                    .w_full()
                    .py(6.0)
                    .justify_between()
                    .center_y()
                    .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                    .child(text(lbl).font(type_body()).color(FG))
                    .child(
                        row()
                            .gap(4.0)
                            .child(
                                filter_pill(format!("dc-p-{bit}-d"), "Deny", tri < 0).on_press({
                                    let (t, a, dn) = (tid_c.clone(), a_c.clone(), d_c.clone());
                                    move |s: &mut AppState| {
                                        let (na, nd) = with_perm_bit_tri(&a, &dn, bit, -1);
                                        s.discord
                                            .admin
                                            .ch_perm_edits
                                            .insert(t.clone(), (0, na, nd));
                                    }
                                }),
                            )
                            .child(
                                filter_pill(format!("dc-p-{bit}-i"), "/", tri == 0).on_press({
                                    let (t, a, dn) = (tid_c.clone(), a_c.clone(), d_c.clone());
                                    move |s: &mut AppState| {
                                        let (na, nd) = with_perm_bit_tri(&a, &dn, bit, 0);
                                        s.discord
                                            .admin
                                            .ch_perm_edits
                                            .insert(t.clone(), (0, na, nd));
                                    }
                                }),
                            )
                            .child(
                                filter_pill(format!("dc-p-{bit}-a"), "Allow", tri > 0).on_press(
                                    move |s: &mut AppState| {
                                        let (na, nd) = with_perm_bit_tri(&a_c, &d_c, bit, 1);
                                        s.discord
                                            .admin
                                            .ch_perm_edits
                                            .insert(tid_c.clone(), (0, na, nd));
                                    },
                                ),
                            ),
                    ),
            );
        }
        if !ad.ch_perm_edits.is_empty() {
            body = body.child(
                row().pt(8.0).justify_end().child(
                    primary_button("dc-chperm-save", "Save permissions", None)
                        .on_press(|s: &mut AppState| s.save_channel_permission_edits()),
                ),
            );
        }
    }

    if let Some(ref err) = ad.channel_settings_error {
        body = body.child(text(err.clone()).font(type_small()).color(bad()));
    }

    let mut sheet = row()
        .key("dc-chset-modal")
        .w(820.0)
        .h(600.0)
        .radius(RADIUS_PANEL)
        .transition(modal_card_transition())
        .modal(true, |s: &mut AppState| {
            s.discord.admin.channel_settings_id = None
        })
        .modal_scrim(scrim_fill())
        .hit()
        .child(nav)
        .child(
            el().grow(1.0)
                .h_full()
                .min_w(0.0)
                .min_h(0.0)
                .child(
                    col()
                        .w_full()
                        .h_full()
                        .padding(22.0)
                        .scroll(ScrollSpec {
                            stick_bottom: false,
                        })
                        .child(body),
                )
                .child(
                    el().key("dc-chset-x")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .pin(None, Some(14.0), Some(14.0), None)
                        .size(28.0, 28.0)
                        .radius(RADIUS_PILL)
                        .hover(hover_fill())
                        .center()
                        .on_press(|s: &mut AppState| s.discord.admin.channel_settings_id = None)
                        .child(icon_close(14.0, FG)),
                ),
        );
    sheet = apply_glass(sheet, ExoGlass::Sheet);

    el().key("dc-chset-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(sheet)
}

fn server_settings_modal(s: &AppState) -> El {
    let ad = &s.discord.admin;
    let gid = ad.server_settings_guild_id.clone().unwrap_or_default();
    let chans = s.discord.channels_by_guild.get(&gid);
    let mod_flags = chans.and_then(|c| c.moderation.clone()).unwrap_or_default();
    let features = chans.map(|c| c.features.as_slice()).unwrap_or(&[]);

    let mut nav = col()
        .w(212.0)
        .h_full()
        .py(18.0)
        .px(12.0)
        .gap(3.0)
        .border_sides([0.0, 1.0, 0.0, 0.0], hairline())
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .child(
            row()
                .px(10.0)
                .pb(8.0)
                .gap(8.0)
                .center_y()
                .child(icon_settings(15.0, FG))
                .child(
                    text(ad.server_settings_name.clone())
                        .font(type_section())
                        .color(FG)
                        .truncate(),
                ),
        );

    for sec in ServerSection::ALL {
        if !sec.shown(&mod_flags, features) {
            continue;
        }
        let active = ad.server_settings_section == sec;
        nav = nav.child(
            row()
                .key(format!("dc-srvset-nav-{}", sec.id()))
                .role("tab")
                .label(sec.label())
                .cursor(Cursor::Pointer)
                .hit()
                .w_full()
                .h(30.0)
                .px(10.0)
                .center_y()
                .radius(RADIUS_SMALL)
                .fill(if active { selected_fill() } else { TRANSPARENT })
                .hover(if active {
                    selected_fill()
                } else {
                    hover_fill()
                })
                .press(pressed_fill())
                .on_press(move |s: &mut AppState| s.select_server_settings_section(sec))
                .child(text(sec.label()).font(type_control()).color(if active {
                    FG
                } else {
                    fg_muted()
                })),
        );
    }

    let sec = ad.server_settings_section;
    let mut body = col()
        .w_full()
        .gap(10.0)
        .child(text(sec.label()).font(type_title()).color(FG));

    if let Some(ref err) = ad.server_settings_error {
        body = body.child(text(err.clone()).font(type_small()).color(bad()));
    }

    match sec {
        ServerSection::Profile | ServerSection::Engagement | ServerSection::Safety => {
            body = body
                .child(text("Server name").font(type_label()).color(fg_muted()))
                .child(
                    text_input(&ad.srv_name, type_body())
                        .key("dc-srv-name")
                        .w_full()
                        .h(36.0)
                        .px(12.0)
                        .radius(RADIUS_SMALL)
                        .fill(BG)
                        .border(1.0, hairline())
                        .color(FG)
                        .single_line()
                        .on_edit(edit_access(|s: &mut AppState| {
                            s.discord.admin.srv_dirty = true;
                            &mut s.discord.admin.srv_name
                        })),
                )
                .child(text("Description").font(type_label()).color(fg_muted()))
                .child(
                    text_input(&ad.srv_desc, type_body())
                        .key("dc-srv-desc")
                        .w_full()
                        .min_h(64.0)
                        .px(12.0)
                        .py(8.0)
                        .radius(RADIUS_SMALL)
                        .fill(BG)
                        .border(1.0, hairline())
                        .color(FG)
                        .on_edit(edit_access(|s: &mut AppState| {
                            s.discord.admin.srv_dirty = true;
                            &mut s.discord.admin.srv_desc
                        })),
                )
                .child(
                    text("Verification level")
                        .font(type_label())
                        .color(fg_muted()),
                )
                .child(
                    row().gap(6.0).wrap().children(
                        [
                            (0, "None"),
                            (1, "Low"),
                            (2, "Medium"),
                            (3, "High"),
                            (4, "Highest"),
                        ]
                        .into_iter()
                        .map(|(v, lbl)| {
                            filter_pill(format!("dc-srv-ver-{v}"), lbl, ad.srv_verification == v)
                                .on_press(move |s: &mut AppState| {
                                    s.discord.admin.srv_verification = v;
                                    s.discord.admin.srv_dirty = true;
                                })
                        }),
                    ),
                )
                .child(
                    text("Default notification settings")
                        .font(type_label())
                        .color(fg_muted()),
                )
                .child(
                    row()
                        .gap(6.0)
                        .child(
                            filter_pill("dc-srv-not-0", "All messages", ad.srv_notify == 0)
                                .on_press(|s: &mut AppState| {
                                    s.discord.admin.srv_notify = 0;
                                    s.discord.admin.srv_dirty = true;
                                }),
                        )
                        .child(
                            filter_pill("dc-srv-not-1", "Only @mentions", ad.srv_notify == 1)
                                .on_press(|s: &mut AppState| {
                                    s.discord.admin.srv_notify = 1;
                                    s.discord.admin.srv_dirty = true;
                                }),
                        ),
                );
            if ad.srv_dirty {
                body = body.child(
                    row().pt(8.0).justify_end().child(
                        primary_button("dc-srv-save", "Save changes", None)
                            .on_press(|s: &mut AppState| s.save_server_overview()),
                    ),
                );
            }
            if sec == ServerSection::Profile && mod_flags.owner {
                body = body.child(
                    col()
                        .pt(12.0)
                        .gap(8.0)
                        .border_sides([1.0, 0.0, 0.0, 0.0], hairline())
                        .child(text("Delete server").font(type_body()).color(bad()))
                        .child(if ad.srv_delete_confirm {
                            row()
                                .gap(8.0)
                                .child(
                                    glass_button_opt("dc-srv-del-no", "Cancel", None, false)
                                        .on_press(|s: &mut AppState| {
                                            s.discord.admin.srv_delete_confirm = false
                                        }),
                                )
                                .child(
                                    primary_button("dc-srv-del-yes", "Confirm delete", None)
                                        .on_press(|s: &mut AppState| {
                                            s.run_server_admin_action("deleteGuild", json!({}));
                                            s.discord.admin.server_settings_guild_id = None;
                                        }),
                                )
                        } else {
                            glass_button_opt("dc-srv-del-ask", "Delete server", None, false)
                                .on_press(|s: &mut AppState| {
                                    s.discord.admin.srv_delete_confirm = true
                                })
                        }),
                );
            }
        }
        ServerSection::Roles => {
            body = body.child(
                row()
                    .gap(8.0)
                    .child(
                        text_input(&ad.srv_new_role_name, type_body())
                            .key("dc-srv-new-role")
                            .placeholder("New role name", fg_muted())
                            .grow(1.0)
                            .h(34.0)
                            .px(10.0)
                            .radius(RADIUS_SMALL)
                            .fill(BG)
                            .border(1.0, hairline())
                            .color(FG)
                            .single_line()
                            .on_edit(edit_access(|s: &mut AppState| {
                                &mut s.discord.admin.srv_new_role_name
                            })),
                    )
                    .child(
                        primary_button("dc-srv-role-add", "Create role", None).on_press(
                            |s: &mut AppState| {
                                let nm = s.discord.admin.srv_new_role_name.text.trim().to_string();
                                if !nm.is_empty() {
                                    s.discord.admin.srv_new_role_name = TextEdit::default();
                                    s.run_server_admin_action("roleCreate", json!({ "name": nm }));
                                }
                            },
                        ),
                    ),
            );
            if let Some(arr) = ad
                .server_section_data
                .get("roles")
                .and_then(|v| v.as_array())
            {
                for r in arr {
                    let rid = r
                        .get("id")
                        .and_then(|x| x.as_str())
                        .unwrap_or("")
                        .to_string();
                    let rname = r
                        .get("name")
                        .and_then(|x| x.as_str())
                        .unwrap_or("Role")
                        .to_string();
                    let is_ev = rid == gid;
                    let rid_del = rid.clone();
                    body = body.child(
                        row()
                            .key(format!("dc-role-{rid}"))
                            .w_full()
                            .py(8.0)
                            .justify_between()
                            .center_y()
                            .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                            .child(text(rname).font(type_body()).color(FG))
                            .children((!is_ev).then(|| {
                                glass_button_opt("dc-role-del", "Delete", None, false)
                                    .key(format!("dc-role-del-{rid}"))
                                    .on_press(move |s: &mut AppState| {
                                        s.run_server_admin_action(
                                            "roleDelete",
                                            json!({ "roleId": rid_del }),
                                        );
                                    })
                            })),
                    );
                }
            }
        }
        ServerSection::Members => {
            body = body.child(server_members_body(s, &gid, chans, 1.0));
        }
        _ => {
            if let Some(val) = ad.server_section_data.get(sec.id()) {
                if let Some(arr) = val.as_array() {
                    if arr.is_empty() {
                        body = body.child(
                            text("Nothing here yet.")
                                .font(type_body())
                                .color(fg_muted()),
                        );
                    } else {
                        for (idx, item) in arr.iter().take(40).enumerate() {
                            let label = item
                                .get("name")
                                .or_else(|| item.get("code"))
                                .or_else(|| item.pointer("/user/username"))
                                .or_else(|| item.get("action_type"))
                                .map(|v| {
                                    v.as_str()
                                        .map(str::to_string)
                                        .unwrap_or_else(|| v.to_string())
                                })
                                .unwrap_or_else(|| format!("Item {}", idx + 1));
                            let id = item
                                .get("id")
                                .or_else(|| item.get("code"))
                                .or_else(|| item.pointer("/user/id"))
                                .and_then(|v| v.as_str())
                                .map(str::to_string);
                            let del_action = match sec {
                                ServerSection::Emoji => Some(("emojiDelete", "emojiId")),
                                ServerSection::Stickers => Some(("stickerDelete", "stickerId")),
                                ServerSection::Invites => Some(("inviteDelete", "code")),
                                ServerSection::Bans => Some(("unban", "userId")),
                                ServerSection::AutoMod => Some(("automodDelete", "ruleId")),
                                _ => None,
                            };
                            body = body.child(
                                row()
                                    .key(format!("dc-sec-item-{}-{idx}", sec.id()))
                                    .w_full()
                                    .py(8.0)
                                    .justify_between()
                                    .center_y()
                                    .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                                    .child(text(label).font(type_body()).color(FG))
                                    .children(del_action.zip(id).map(|((act, field), val_id)| {
                                        glass_button_opt(
                                            "dc-sec-del",
                                            if sec == ServerSection::Bans {
                                                "Unban"
                                            } else {
                                                "Delete"
                                            },
                                            None,
                                            false,
                                        )
                                        .key(format!("dc-sec-del-{idx}"))
                                        .on_press(
                                            move |s: &mut AppState| {
                                                s.run_server_admin_action(
                                                    act,
                                                    json!({ field: val_id }),
                                                );
                                            },
                                        )
                                    })),
                            );
                        }
                    }
                } else {
                    let summary = serde_json::to_string_pretty(val).unwrap_or_default();
                    let clipped: String = summary.chars().take(1200).collect();
                    body = body.child(text(clipped).font(type_small()).color(fg_muted()));
                }
            } else {
                body = body.child(text("Loading section…").font(type_body()).color(fg_muted()));
            }
        }
    }

    let mut sheet = row()
        .key("dc-srvset-modal")
        .w(880.0)
        .h(640.0)
        .radius(RADIUS_PANEL)
        .transition(modal_card_transition())
        .modal(true, |s: &mut AppState| {
            s.discord.admin.server_settings_guild_id = None
        })
        .modal_scrim(scrim_fill())
        .hit()
        .child(nav)
        .child(
            el().grow(1.0)
                .h_full()
                .min_w(0.0)
                .min_h(0.0)
                .child(
                    col()
                        .w_full()
                        .h_full()
                        .padding(22.0)
                        .scroll(ScrollSpec {
                            stick_bottom: false,
                        })
                        .child(body),
                )
                .child(
                    el().key("dc-srvset-x")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .pin(None, Some(14.0), Some(14.0), None)
                        .size(28.0, 28.0)
                        .radius(RADIUS_PILL)
                        .hover(hover_fill())
                        .center()
                        .on_press(|s: &mut AppState| {
                            s.discord.admin.server_settings_guild_id = None
                        })
                        .child(icon_close(14.0, FG)),
                ),
        );
    sheet = apply_glass(sheet, ExoGlass::Sheet);

    el().key("dc-srvset-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(sheet)
}

fn shop_product_modal(s: &AppState) -> El {
    let ad = &s.discord.admin;
    let Some(prod) = ad.shop_selected.as_ref() else {
        return el();
    };
    let is_owned = ad.shop_owned.iter().any(|p| p.sku_id == prod.sku_id);
    let first_item = product_items(prod).first();
    let kind_str = first_item.and_then(|i| match i.kind {
        0 => Some("decoration"),
        1 => Some("effect"),
        2 => Some("nameplate"),
        _ => None,
    });
    let item_sku = first_item.map(|i| i.sku_id.clone());

    let mut panel = glass_dialog_frame("dc-shop-dlg", 460.0)
        .child(modal_header(
            &prod.name,
            "dc-shop-dlg-x",
            |s: &mut AppState| s.discord.admin.shop_selected = None,
        ))
        .children(
            prod.summary
                .as_ref()
                .filter(|x| !x.is_empty())
                .map(|x| text(x.clone()).font(type_body()).color(fg_muted())),
        );

    if is_owned {
        if let (Some(k), Some(sku)) = (kind_str, item_sku) {
            let sku_eq = sku.clone();
            panel = panel.child(
                row()
                    .gap(8.0)
                    .child(
                        primary_button(
                            "dc-shop-equip",
                            "Use now",
                            Some(icon_check(14.0, PRIMARY_FG)),
                        )
                        .on_press(move |s: &mut AppState| {
                            s.equip_shop_item(k, Some(sku_eq.clone()))
                        }),
                    )
                    .child(
                        glass_button_opt("dc-shop-unequip", "Remove", None, false)
                            .on_press(move |s: &mut AppState| s.equip_shop_item(k, None)),
                    ),
            );
        }
    } else {
        if !ad.shop_payments.is_empty() {
            let mut pay_row = row().gap(6.0).wrap();
            for p in &ad.shop_payments {
                let active = ad.shop_selected_payment.as_deref() == Some(p.id.as_str());
                let pid = p.id.clone();
                let psku = prod.sku_id.clone();
                pay_row = pay_row.child(
                    filter_pill(format!("dc-pay-{}", p.id), p.label.clone(), active).on_press(
                        move |s: &mut AppState| {
                            s.discord.admin.shop_selected_payment = Some(pid.clone());
                            s.preview_shop_price(&psku, &pid);
                        },
                    ),
                );
            }
            panel = panel
                .child(text("Payment method").font(type_label()).color(fg_muted()))
                .child(pay_row);
        }
        if let Some(ref price) = ad.shop_price
            && let (Some(amt), Some(cur)) = (price.amount, price.currency.as_deref())
        {
            panel = panel.child(
                text(format!("Total: {}", format_shop_price(amt, cur, 2)))
                    .font(type_section())
                    .color(FG),
            );
        }
        panel = panel.child(
            row().justify_end().child(
                primary_button(
                    "dc-shop-buy",
                    if ad.shop_buy_busy { "Buying…" } else { "Buy" },
                    None,
                )
                .on_press(|s: &mut AppState| s.buy_shop_selected()),
            ),
        );
    }

    if let Some(ref st) = ad.shop_status {
        panel = panel.child(text(st.clone()).font(type_small()).color(fg_muted()));
    }

    el().key("dc-shop-dlg-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(
            el().key("dc-shop-dlg-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .hit()
                .press_on_down()
                .transition(modal_scrim_transition())
                .on_press(|s: &mut AppState| s.discord.admin.shop_selected = None),
        )
        .child(panel)
}

fn report_modal(s: &AppState) -> El {
    let Some(st) = s.discord.admin.report.as_ref() else {
        return el();
    };
    let current_node: Option<&DiscordReportNode> = st.menu.as_ref().and_then(|m| {
        st.path
            .last()
            .and_then(|(id, _)| m.nodes.get(&id.to_string()))
    });

    let header_title = current_node.map(|n| n.header.as_str()).unwrap_or("Report");

    let mut panel = glass_dialog_frame("dc-report-modal", 480.0).child(modal_header(
        header_title,
        "dc-report-x",
        |s: &mut AppState| s.discord.admin.report = None,
    ));

    if st.menu.is_none() {
        if let Some(ref err) = st.error {
            panel = panel.child(text(err.clone()).font(type_body()).color(bad()));
        } else {
            panel = panel.child(
                text("Loading Discord's report questions…")
                    .font(type_body())
                    .color(fg_muted()),
            );
        }
    } else if let Some(node) = current_node {
        if let Some(ref sub) = node.subheader
            && !sub.is_empty()
        {
            panel = panel.child(text(sub.clone()).font(type_small()).color(fg_muted()));
        }
        if let Some(ref info) = node.info
            && !info.is_empty()
        {
            panel = panel.child(text(info.clone()).font(type_body()).color(fg_muted()));
        }

        panel = panel.child(
            col()
                .w_full()
                .padding(10.0)
                .gap(4.0)
                .radius(RADIUS_SMALL)
                .fill(BG)
                .border(1.0, hairline())
                .child(
                    text(st.target.preview_name.clone())
                        .font(type_control())
                        .color(FG),
                )
                .children(
                    st.target
                        .preview_text
                        .as_ref()
                        .filter(|t| !t.is_empty())
                        .map(|t| {
                            text(t.clone())
                                .font(type_small())
                                .color(fg_muted())
                                .truncate()
                        }),
                ),
        );

        for elem in &node.elements {
            match elem.kind.as_str() {
                "free_text" => {
                    panel = panel.child(
                        text_input(&s.discord.admin.report_free_text, type_body())
                            .key("dc-report-free")
                            .placeholder("Tell Discord more", fg_muted())
                            .w_full()
                            .min_h(64.0)
                            .px(12.0)
                            .py(8.0)
                            .radius(RADIUS_SMALL)
                            .fill(BG)
                            .border(1.0, hairline())
                            .color(FG)
                            .on_edit(edit_access(|s: &mut AppState| {
                                &mut s.discord.admin.report_free_text
                            })),
                    );
                }
                "block_users" => {
                    if let Some(uid) = st.target.user_id.clone() {
                        panel = panel.child(
                            glass_button_opt("dc-report-block", "Block user", None, false)
                                .on_press(move |s: &mut AppState| {
                                    if s.status == HostStatus::Connected {
                                        #[cfg(windows)]
                                        s.call_host_async(
                                            METHOD_DISCORD_FRIEND,
                                            json!({ "action": "block", "userId": uid }),
                                            |_s, _r: Result<ActionResult, String>| {},
                                        );
                                    }
                                }),
                        );
                    }
                }
                "ignore_users" => {
                    if let Some(uid) = st.target.user_id.clone() {
                        panel = panel.child(
                            glass_button_opt("dc-report-ignore", "Ignore user", None, false)
                                .on_press(move |s: &mut AppState| {
                                    s.toggle_ignore_user(&uid);
                                }),
                        );
                    }
                }
                "delete_message" => {
                    if st.target.can_delete
                        && let (Some(cid), Some(mid)) = (
                            st.target.ids.get("channelId").cloned(),
                            st.target.ids.get("messageId").cloned(),
                        )
                    {
                        panel = panel.child(
                            glass_button_opt("dc-report-delmsg", "Delete message", None, false)
                                .on_press(move |s: &mut AppState| {
                                    s.delete_discord_message(&cid, &mid);
                                }),
                        );
                    }
                }
                _ => {}
            }
        }

        if !node.children.is_empty() {
            let mut choices = col().w_full().gap(6.0);
            for (idx, (label, target_id)) in node.children.iter().enumerate() {
                let lbl = label.clone();
                let to = *target_id;
                choices = choices.child(
                    row()
                        .key(format!("dc-rep-opt-{idx}"))
                        .role("button")
                        .label(lbl.clone())
                        .cursor(Cursor::Pointer)
                        .hit()
                        .w_full()
                        .h(38.0)
                        .px(12.0)
                        .justify_between()
                        .center_y()
                        .radius(RADIUS_SMALL)
                        .fill(BG)
                        .border(1.0, hairline())
                        .hover(hover_fill())
                        .on_press(move |s: &mut AppState| s.report_advance(to, Some(lbl.clone())))
                        .child(text(label.clone()).font(type_body()).color(FG))
                        .child(icon_chevron_right(14.0, fg_muted())),
                );
            }
            panel = panel.child(choices);
        }

        if let Some(ref err) = st.error {
            panel = panel.child(text(err.clone()).font(type_small()).color(bad()));
        }

        let ended = st.result_ok.is_some();
        let can_back = st.path.len() > 1 && !ended && !st.sending;
        let btn_kind = node.button.as_ref().map(|b| b.kind.as_str());
        let btn_target = node.button.as_ref().and_then(|b| b.target);
        panel = panel.child(
            row()
                .w_full()
                .justify_end()
                .gap(8.0)
                .children(can_back.then(|| {
                    glass_button_opt("dc-rep-back", "Back", None, false)
                        .on_press(|s: &mut AppState| s.report_go_back())
                }))
                .child(
                    glass_button_opt(
                        "dc-rep-close",
                        if ended || btn_kind == Some("done") {
                            "Done"
                        } else {
                            "Cancel"
                        },
                        None,
                        false,
                    )
                    .on_press(|s: &mut AppState| s.discord.admin.report = None),
                )
                .children(
                    (!ended && btn_kind == Some("next"))
                        .then_some(btn_target)
                        .flatten()
                        .map(|to| {
                            primary_button("dc-rep-next", "Next", None)
                                .on_press(move |s: &mut AppState| s.report_advance(to, None))
                        }),
                )
                .children((!ended && btn_kind == Some("submit")).then(|| {
                    primary_button(
                        "dc-rep-submit",
                        if st.sending { "Sending…" } else { "Report" },
                        None,
                    )
                    .on_press(|s: &mut AppState| s.submit_discord_report())
                })),
        );
    }

    el().key("dc-report-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(
            el().key("dc-report-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .hit()
                .press_on_down()
                .transition(modal_scrim_transition())
                .on_press(|s: &mut AppState| s.discord.admin.report = None),
        )
        .child(panel)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn perm_tri_state_round_trips_allow_inherit_deny() {
        let (a, d) = with_perm_bit_tri("0", "0", 11, 1);
        assert_eq!(perm_bit_tri(&a, &d, 11), 1);
        let (a2, d2) = with_perm_bit_tri(&a, &d, 11, -1);
        assert_eq!(perm_bit_tri(&a2, &d2, 11), -1);
        let (a3, d3) = with_perm_bit_tri(&a2, &d2, 11, 0);
        assert_eq!(perm_bit_tri(&a3, &d3, 11), 0);
    }

    #[test]
    fn move_server_in_rail_handles_list_and_folder_moves() {
        let rail = vec![
            DiscordRailItem {
                id: Some("f1".into()),
                name: Some("Games".into()),
                color: None,
                guild_ids: vec!["g1".into(), "g2".into()],
            },
            DiscordRailItem {
                id: None,
                name: None,
                color: None,
                guild_ids: vec!["g3".into()],
            },
        ];
        let moved = move_server_in_rail(&rail, "g2", "folderFirst").expect("moved");
        assert_eq!(moved[0].guild_ids, vec!["g2", "g1"]);

        let unfoldered = move_server_in_rail(&rail, "g1", "noFolder").expect("unfoldered");
        assert_eq!(unfoldered.len(), 3);
        assert_eq!(unfoldered[1].guild_ids, vec!["g1"]);

        let first = move_server_in_rail(&rail, "g3", "listFirst").expect("first");
        assert_eq!(first[0].guild_ids, vec!["g3"]);
    }

    #[test]
    fn server_section_visibility_respects_permissions_and_community() {
        let mut m = DiscordModeration::default();
        assert!(ServerSection::Profile.shown(&m, &[]));
        assert!(!ServerSection::Roles.shown(&m, &[]));
        m.manage_guild = true;
        m.manage_roles = true;
        assert!(ServerSection::Roles.shown(&m, &[]));
        assert!(!ServerSection::Onboarding.shown(&m, &[]));
        assert!(ServerSection::Onboarding.shown(&m, &["COMMUNITY".into()]));
        assert!(ServerSection::Welcome.shown(&m, &["COMMUNITY".into()]));
        assert!(
            !ServerSection::Welcome.shown(&m, &["COMMUNITY".into(), "GUILD_SERVER_GUIDE".into()])
        );
    }

    #[test]
    fn format_shop_price_formats_currency_and_exponent() {
        assert_eq!(format_shop_price(499, "usd", 2), "$4.99");
        assert_eq!(format_shop_price(999, "eur", 2), "€9.99");
    }
}
