use crate::discord_chat_state::{ChatDrawer, DiscordChatState};
use crate::discord_voice_state::DiscordVoiceUiState;
use crate::state::{AppState, ContextMenuTarget, HostStatus, Room};
use exo_rpc::discord_types::{DISCORD_SNAPSHOT_VERSION, DiscordDiskSnapshot, DiscordRestricted};
use exo_rpc::{
    ActionResult, DiscordAccountItem, DiscordAccountsReply, DiscordChangedEvent, DiscordChannel,
    DiscordChannels, DiscordDm, DiscordDmMember, DiscordFriend, DiscordFriendActionReply,
    DiscordFriendsReply, DiscordGuild, DiscordHome, DiscordMe, DiscordMember, DiscordMemberItem,
    DiscordMemberList, DiscordPresenceEvent, DiscordRailItem, DiscordRich, DiscordSignInStep,
    DiscordStateEvent, DiscordStatus, DiscordUnreadChannel, DiscordUnreadEvent,
    DiscordUnreadsReply, DiscordVoice, DiscordVoiceMember, DiscordVoiceReply,
    METHOD_DISCORD_ACCOUNTS, METHOD_DISCORD_CHANNEL, METHOD_DISCORD_CHANNELS,
    METHOD_DISCORD_CLIENT_SETTINGS, METHOD_DISCORD_FRIEND, METHOD_DISCORD_FRIENDS,
    METHOD_DISCORD_HOME, METHOD_DISCORD_MARK_READ, METHOD_DISCORD_MEMBERS, METHOD_DISCORD_RING,
    METHOD_DISCORD_SERVER, METHOD_DISCORD_SET_STATUS, METHOD_DISCORD_SIGN_IN,
    METHOD_DISCORD_SIGN_OUT, METHOD_DISCORD_STATUS, METHOD_DISCORD_UNREADS, METHOD_DISCORD_VIEW,
    METHOD_DISCORD_VOICE,
};
use rune::TextEdit;
use serde_json::json;
use std::collections::{HashMap, HashSet, VecDeque};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const DISCORD_SNAPSHOT_FILENAME: &str = "rune-discord-snapshot.json";
const DISCORD_READY_BURST_SUPPRESS: Duration = Duration::from_millis(250);
const DISCORD_PREFETCH_WORKERS: usize = 6;

pub(crate) fn read_discord_snapshot(path: &Path) -> Option<DiscordDiskSnapshot> {
    let bytes = std::fs::read(path).ok()?;
    let snap: DiscordDiskSnapshot = serde_json::from_slice(&bytes).ok()?;
    if snap.version != DISCORD_SNAPSHOT_VERSION || snap.home.me.is_none() {
        return None;
    }
    Some(snap)
}

pub(crate) fn write_discord_snapshot(
    snap: &DiscordDiskSnapshot,
    path: &Path,
) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let data = serde_json::to_vec(snap)
        .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, &data)?;
    if std::fs::rename(&tmp, path).is_err() {
        let _ = std::fs::remove_file(path);
        std::fs::rename(&tmp, path)?;
    }
    Ok(())
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DiscordPlace {
    Home,
    Messages,
    Servers,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DiscordFriendsTab {
    Online,
    All,
    Pending,
    Blocked,
}

impl DiscordFriendsTab {
    pub const ALL: [Self; 4] = [Self::Online, Self::All, Self::Pending, Self::Blocked];

    pub fn key(self) -> &'static str {
        match self {
            Self::Online => "dc-friends-tab-online",
            Self::All => "dc-friends-tab-all",
            Self::Pending => "dc-friends-tab-pending",
            Self::Blocked => "dc-friends-tab-blocked",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Online => "Online",
            Self::All => "All",
            Self::Pending => "Pending",
            Self::Blocked => "Blocked",
        }
    }

    pub fn section_label(self) -> &'static str {
        match self {
            Self::Online => "Online",
            Self::All => "All friends",
            Self::Pending => "Pending",
            Self::Blocked => "Blocked",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DiscordSettingsPage {
    Voice,
    Chat,
    Notifications,
    Privacy,
    Account,
    Keybinds,
}

impl DiscordSettingsPage {
    pub const ALL: [Self; 6] = [
        Self::Voice,
        Self::Chat,
        Self::Notifications,
        Self::Privacy,
        Self::Account,
        Self::Keybinds,
    ];

    pub fn key(self) -> &'static str {
        match self {
            Self::Voice => "dc-set-nav-voice",
            Self::Chat => "dc-set-nav-chat",
            Self::Notifications => "dc-set-nav-notifications",
            Self::Privacy => "dc-set-nav-privacy",
            Self::Account => "dc-set-nav-account",
            Self::Keybinds => "dc-set-nav-keybinds",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Voice => "Voice and video",
            Self::Chat => "Chat",
            Self::Notifications => "Notifications",
            Self::Privacy => "Privacy",
            Self::Account => "Account",
            Self::Keybinds => "Keybinds",
        }
    }
}

pub struct DiscordState {
    pub status: DiscordStatus,
    pub signin: Option<DiscordSignInStep>,
    pub accounts: Vec<DiscordAccountItem>,
    pub home: DiscordHome,
    pub friends: Vec<DiscordFriend>,
    pub blocked: Vec<DiscordRestricted>,
    pub unreads: Vec<DiscordUnreadChannel>,
    pub voice: DiscordVoice,
    pub speaking: HashSet<String>,
    pub place: DiscordPlace,
    pub friends_tab: DiscordFriendsTab,
    pub active_dm_id: Option<String>,
    pub active_guild_id: Option<String>,
    pub active_channel_id: Option<String>,
    pub channels_by_guild: HashMap<String, DiscordChannels>,
    pub members_by_guild: HashMap<String, DiscordMemberList>,
    pub collapsed_categories: HashSet<String>,
    pub expanded_folders: HashSet<String>,
    pub show_members: bool,
    pub status_menu_open: bool,
    pub accounts_menu_open: bool,
    pub add_friend_open: bool,
    pub dm_search_open: bool,
    pub shop_open: bool,
    pub inbox_open: bool,
    pub settings_open: bool,
    pub settings_page: DiscordSettingsPage,
    pub custom_status: Option<String>,
    pub custom_status_open: bool,
    pub custom_status_input: TextEdit,
    pub pinned_dm_overrides: HashSet<String>,
    pub unpinned_dm_overrides: HashSet<String>,
    pub hide_muted_guilds: HashSet<String>,
    pub ignored_users: HashSet<String>,
    pub chat_compact: bool,
    pub show_embeds: bool,
    pub notify_desktop: bool,
    pub notify_sounds: bool,
    pub allow_server_dms: bool,
    pub developer_mode: bool,
    pub search_query: TextEdit,
    pub dm_search_query: TextEdit,
    pub add_friend_input: TextEdit,
    pub revealed_spoilers: HashSet<String>,
    pub chat: DiscordChatState,
    pub voice_ui: DiscordVoiceUiState,
    pub loaded: bool,
    pub warmed: bool,
    pub snapshot_hydrated: bool,
    pub from_snapshot: bool,
    pub refresh_pending: u8,
    pub refresh_queued: bool,
    pub last_ready_refresh_at: Option<Instant>,
    pub prefetched_urls: HashSet<String>,
    pub prefetching_urls: HashSet<String>,
    pub warmed_image_urls: HashSet<String>,
}

impl DiscordState {
    pub fn empty() -> Self {
        Self {
            status: DiscordStatus {
                state: "off".into(),
                message: None,
                signed_in: false,
            },
            signin: None,
            accounts: Vec::new(),
            home: DiscordHome::default(),
            friends: Vec::new(),
            blocked: Vec::new(),
            unreads: Vec::new(),
            voice: DiscordVoice {
                state: "off".into(),
                ..Default::default()
            },
            speaking: HashSet::new(),
            place: DiscordPlace::Home,
            friends_tab: DiscordFriendsTab::Online,
            active_dm_id: None,
            active_guild_id: None,
            active_channel_id: None,
            channels_by_guild: HashMap::new(),
            members_by_guild: HashMap::new(),
            collapsed_categories: HashSet::new(),
            expanded_folders: HashSet::new(),
            show_members: true,
            status_menu_open: false,
            accounts_menu_open: false,
            add_friend_open: false,
            dm_search_open: false,
            shop_open: false,
            inbox_open: false,
            settings_open: false,
            settings_page: DiscordSettingsPage::Account,
            custom_status: None,
            custom_status_open: false,
            custom_status_input: TextEdit::default(),
            pinned_dm_overrides: HashSet::new(),
            unpinned_dm_overrides: HashSet::new(),
            hide_muted_guilds: HashSet::new(),
            ignored_users: HashSet::new(),
            chat_compact: false,
            show_embeds: true,
            notify_desktop: true,
            notify_sounds: true,
            allow_server_dms: true,
            developer_mode: true,
            search_query: TextEdit::default(),
            dm_search_query: TextEdit::default(),
            add_friend_input: TextEdit::default(),
            revealed_spoilers: HashSet::new(),
            chat: DiscordChatState::empty(),
            voice_ui: DiscordVoiceUiState::empty(),
            loaded: false,
            warmed: false,
            snapshot_hydrated: false,
            from_snapshot: false,
            refresh_pending: 0,
            refresh_queued: false,
            last_ready_refresh_at: None,
            prefetched_urls: HashSet::new(),
            prefetching_urls: HashSet::new(),
            warmed_image_urls: HashSet::new(),
        }
    }

    pub fn demo() -> Self {
        let mut s = Self::empty();
        s.chat = DiscordChatState::demo();
        s.voice_ui = DiscordVoiceUiState::demo();
        s.status = DiscordStatus {
            state: "ready".into(),
            message: None,
            signed_in: true,
        };
        s.loaded = true;
        s.accounts = vec![
            DiscordAccountItem {
                id: "100001".into(),
                name: "Erix".into(),
                avatar: None,
                current: true,
            },
            DiscordAccountItem {
                id: "100002".into(),
                name: "ErixAlt".into(),
                avatar: None,
                current: false,
            },
        ];
        s.home = DiscordHome {
            me: Some(DiscordMe {
                id: "100001".into(),
                name: "Erix".into(),
                avatar: None,
                status: Some("online".into()),
                look: None,
            }),
            dms: vec![
                DiscordDm {
                    id: "dm-ada".into(),
                    name: "Ada".into(),
                    avatar: None,
                    last_at: Some(1_760_000_000_000),
                    line: Some("Queueing Premier in 5?".into()),
                    look: None,
                    group: false,
                    owner_id: None,
                    members: None,
                    unread: true,
                    mentions: 2,
                    muted: false,
                    status: Some("online".into()),
                    user_id: Some("200001".into()),
                    username: Some("ada".into()),
                    subtitle: Some("Playing Counter-Strike 2".into()),
                    call: None,
                    request: false,
                    spam: false,
                },
                DiscordDm {
                    id: "dm-squad".into(),
                    name: "Night Shift Squad".into(),
                    avatar: None,
                    last_at: Some(1_759_999_000_000),
                    line: Some("Kai: dropping hot on Mirage".into()),
                    look: None,
                    group: true,
                    owner_id: Some("100001".into()),
                    members: Some(vec![
                        DiscordDmMember {
                            id: "200001".into(),
                            name: "Ada".into(),
                            username: Some("ada".into()),
                            avatar: None,
                            status: "online".into(),
                        },
                        DiscordDmMember {
                            id: "200002".into(),
                            name: "Kai".into(),
                            username: Some("kai".into()),
                            avatar: None,
                            status: "online".into(),
                        },
                        DiscordDmMember {
                            id: "200003".into(),
                            name: "Mina".into(),
                            username: Some("mina".into()),
                            avatar: None,
                            status: "dnd".into(),
                        },
                    ]),
                    unread: true,
                    mentions: 0,
                    muted: false,
                    status: None,
                    user_id: None,
                    username: None,
                    subtitle: Some("4 members".into()),
                    call: Some(vec![
                        DiscordVoiceMember {
                            id: "200001".into(),
                            name: "Ada".into(),
                            ..Default::default()
                        },
                        DiscordVoiceMember {
                            id: "200002".into(),
                            name: "Kai".into(),
                            ..Default::default()
                        },
                    ]),
                    request: false,
                    spam: false,
                },
                DiscordDm {
                    id: "dm-mina".into(),
                    name: "Mina".into(),
                    avatar: None,
                    last_at: Some(1_759_995_000_000),
                    line: Some("Cyberpunk 2077 path tracing hits 110 FPS".into()),
                    look: None,
                    group: false,
                    owner_id: None,
                    members: None,
                    unread: false,
                    mentions: 0,
                    muted: false,
                    status: Some("dnd".into()),
                    user_id: Some("200003".into()),
                    username: Some("mina".into()),
                    subtitle: Some("Playing Cyberpunk 2077".into()),
                    call: None,
                    request: false,
                    spam: false,
                },
                DiscordDm {
                    id: "dm-sol".into(),
                    name: "Sol".into(),
                    avatar: None,
                    last_at: Some(1_759_980_000_000),
                    line: Some("Sent the Wooting profile".into()),
                    look: None,
                    group: false,
                    owner_id: None,
                    members: None,
                    unread: false,
                    mentions: 0,
                    muted: true,
                    status: Some("idle".into()),
                    user_id: Some("200004".into()),
                    username: Some("sol".into()),
                    subtitle: Some("Listening to Spotify".into()),
                    call: None,
                    request: false,
                    spam: false,
                },
            ],
            guilds: vec![
                DiscordGuild {
                    id: "g-exo".into(),
                    name: "Exo HQ".into(),
                    icon: None,
                    muted: false,
                    unread: true,
                    mentions: 3,
                    banner: None,
                    in_voice: 3,
                    owner: true,
                },
                DiscordGuild {
                    id: "g-fps".into(),
                    name: "Aim Lab & CS2".into(),
                    icon: None,
                    muted: false,
                    unread: true,
                    mentions: 0,
                    banner: None,
                    in_voice: 2,
                    owner: false,
                },
                DiscordGuild {
                    id: "g-hw".into(),
                    name: "Hardware & Tuning".into(),
                    icon: None,
                    muted: false,
                    unread: false,
                    mentions: 0,
                    banner: None,
                    in_voice: 0,
                    owner: false,
                },
                DiscordGuild {
                    id: "g-indie".into(),
                    name: "Co-op Nights".into(),
                    icon: None,
                    muted: true,
                    unread: false,
                    mentions: 0,
                    banner: None,
                    in_voice: 1,
                    owner: false,
                },
            ],
            rail: vec![
                DiscordRailItem {
                    id: None,
                    name: None,
                    color: None,
                    guild_ids: vec!["g-exo".into()],
                },
                DiscordRailItem {
                    id: Some("f-games".into()),
                    name: Some("Gaming".into()),
                    color: Some(0x30d158),
                    guild_ids: vec!["g-fps".into(), "g-indie".into()],
                },
                DiscordRailItem {
                    id: None,
                    name: None,
                    color: None,
                    guild_ids: vec!["g-hw".into()],
                },
            ],
            blocked: Vec::new(),
            ignored: Vec::new(),
        };
        s.friends = vec![
            DiscordFriend {
                id: "200001".into(),
                name: "Ada".into(),
                username: Some("ada".into()),
                avatar: None,
                look: None,
                kind: "friend".into(),
                status: "online".into(),
                activity: Some("Counter-Strike 2".into()),
                activity_type: Some(0),
                rich: Some(DiscordRich {
                    activity_type: 0,
                    name: "Counter-Strike 2".into(),
                    details: Some("Premier · Mirage (9–6)".into()),
                    state: Some("In a squad (2 of 5)".into()),
                    start: None,
                    image: None,
                    through_exo: true,
                }),
                prep: None,
                dm_id: Some("dm-ada".into()),
            },
            DiscordFriend {
                id: "200002".into(),
                name: "Kai".into(),
                username: Some("kai".into()),
                avatar: None,
                look: None,
                kind: "friend".into(),
                status: "online".into(),
                activity: Some("Counter-Strike 2".into()),
                activity_type: Some(0),
                rich: Some(DiscordRich {
                    activity_type: 0,
                    name: "Counter-Strike 2".into(),
                    details: Some("Premier · Mirage (9–6)".into()),
                    state: None,
                    start: None,
                    image: None,
                    through_exo: true,
                }),
                prep: None,
                dm_id: None,
            },
            DiscordFriend {
                id: "200003".into(),
                name: "Mina".into(),
                username: Some("mina".into()),
                avatar: None,
                look: None,
                kind: "friend".into(),
                status: "dnd".into(),
                activity: Some("Cyberpunk 2077".into()),
                activity_type: Some(0),
                rich: Some(DiscordRich {
                    activity_type: 0,
                    name: "Cyberpunk 2077".into(),
                    details: Some("Dogtown · Phantom Liberty".into()),
                    state: None,
                    start: None,
                    image: None,
                    through_exo: false,
                }),
                prep: None,
                dm_id: Some("dm-mina".into()),
            },
            DiscordFriend {
                id: "200004".into(),
                name: "Sol".into(),
                username: Some("sol".into()),
                avatar: None,
                look: None,
                kind: "friend".into(),
                status: "idle".into(),
                activity: Some("Midnight City — M83".into()),
                activity_type: Some(2),
                rich: None,
                prep: None,
                dm_id: Some("dm-sol".into()),
            },
            DiscordFriend {
                id: "200005".into(),
                name: "Devon".into(),
                username: Some("devon".into()),
                avatar: None,
                look: None,
                kind: "incoming".into(),
                status: "online".into(),
                activity: None,
                activity_type: None,
                rich: None,
                prep: None,
                dm_id: None,
            },
        ];
        s.channels_by_guild.insert(
            "g-exo".into(),
            DiscordChannels {
                name: Some("Exo HQ".into()),
                icon: None,
                banner: None,
                hide_muted: false,
                events: 1,
                can_manage: false,
                can_manage_events: false,
                rows: None,
                threads: Vec::new(),
                channels: vec![
                    DiscordChannel {
                        id: "cat-core".into(),
                        name: "Core".into(),
                        channel_type: 4,
                        kind: "category".into(),
                        ..Default::default()
                    },
                    DiscordChannel {
                        id: "ch-general".into(),
                        name: "general".into(),
                        channel_type: 0,
                        kind: "text".into(),
                        topic: Some("Daily builds, Rune DX12 progress, and game nights".into()),
                        unread: true,
                        mentions: 2,
                        can_read: Some(true),
                        can_send: Some(true),
                        ..Default::default()
                    },
                    DiscordChannel {
                        id: "ch-builds".into(),
                        name: "native-ui-builds".into(),
                        channel_type: 0,
                        kind: "text".into(),
                        topic: Some("Zero-bloat Rust + wgpu benchmarks and snapshots".into()),
                        unread: true,
                        mentions: 1,
                        can_read: Some(true),
                        can_send: Some(true),
                        ..Default::default()
                    },
                    DiscordChannel {
                        id: "ch-clips".into(),
                        name: "clips".into(),
                        channel_type: 15,
                        kind: "forum".into(),
                        topic: Some("Instant Replay highlights and 10 MB Discord exports".into()),
                        can_read: Some(true),
                        can_send: Some(true),
                        ..Default::default()
                    },
                    DiscordChannel {
                        id: "cat-voice".into(),
                        name: "Voice Channels".into(),
                        channel_type: 4,
                        kind: "category".into(),
                        ..Default::default()
                    },
                    DiscordChannel {
                        id: "vc-lounge".into(),
                        name: "Main Lounge".into(),
                        channel_type: 2,
                        kind: "voice".into(),
                        status: Some("Premier warmups".into()),
                        user_limit: Some(5),
                        can_connect: Some(true),
                        members: Some(vec![
                            DiscordVoiceMember {
                                id: "200001".into(),
                                name: "Ada".into(),
                                ..Default::default()
                            },
                            DiscordVoiceMember {
                                id: "200002".into(),
                                name: "Kai".into(),
                                stream: true,
                                ..Default::default()
                            },
                            DiscordVoiceMember {
                                id: "200004".into(),
                                name: "Sol".into(),
                                mute: true,
                                ..Default::default()
                            },
                        ]),
                        ..Default::default()
                    },
                    DiscordChannel {
                        id: "vc-duo".into(),
                        name: "Duo Queue".into(),
                        channel_type: 2,
                        kind: "voice".into(),
                        user_limit: Some(2),
                        can_connect: Some(true),
                        members: Some(Vec::new()),
                        ..Default::default()
                    },
                ],
            },
        );
        s.members_by_guild.insert(
            "g-exo".into(),
            DiscordMemberList {
                online: 4,
                total: 7,
                rows: Some(9),
                items: vec![
                    Some(DiscordMemberItem::Group {
                        group: "Founders".into(),
                        count: 2,
                    }),
                    Some(DiscordMemberItem::Member(DiscordMember {
                        id: "100001".into(),
                        name: "Erix".into(),
                        color: Some("#30d158".into()),
                        status: "online".into(),
                        activity: Some("Building Exo Launcher".into()),
                        owner: true,
                        ..Default::default()
                    })),
                    Some(DiscordMemberItem::Member(DiscordMember {
                        id: "200001".into(),
                        name: "Ada".into(),
                        color: Some("#64d2ff".into()),
                        status: "online".into(),
                        activity: Some("Playing Counter-Strike 2".into()),
                        in_voice: Some("vc-lounge".into()),
                        ..Default::default()
                    })),
                    Some(DiscordMemberItem::Group {
                        group: "Online".into(),
                        count: 2,
                    }),
                    Some(DiscordMemberItem::Member(DiscordMember {
                        id: "200002".into(),
                        name: "Kai".into(),
                        color: Some("#ffd60a".into()),
                        status: "online".into(),
                        activity: Some("Playing Counter-Strike 2".into()),
                        in_voice: Some("vc-lounge".into()),
                        ..Default::default()
                    })),
                    Some(DiscordMemberItem::Member(DiscordMember {
                        id: "200003".into(),
                        name: "Mina".into(),
                        color: Some("#ff9f0a".into()),
                        status: "dnd".into(),
                        activity: Some("Playing Cyberpunk 2077".into()),
                        ..Default::default()
                    })),
                    Some(DiscordMemberItem::Group {
                        group: "Offline".into(),
                        count: 3,
                    }),
                    Some(DiscordMemberItem::Member(DiscordMember {
                        id: "200006".into(),
                        name: "Exo Bot".into(),
                        bot: true,
                        status: "offline".into(),
                        ..Default::default()
                    })),
                ],
            },
        );
        s.speaking.insert("200001".into());
        s.active_dm_id = Some("dm-ada".into());
        s
    }

    pub fn unread_dm_count(&self) -> usize {
        self.home
            .dms
            .iter()
            .filter(|d| d.unread && !d.muted)
            .count()
    }

    pub fn total_dm_mentions(&self) -> i32 {
        self.home.dms.iter().map(|d| d.mentions).sum()
    }

    pub fn active_guild(&self) -> Option<&DiscordGuild> {
        let id = self.active_guild_id.as_deref()?;
        self.home.guilds.iter().find(|g| g.id == id)
    }

    pub fn active_dm(&self) -> Option<&DiscordDm> {
        let id = self.active_dm_id.as_deref()?;
        self.home.dms.iter().find(|d| d.id == id)
    }

    pub fn active_channel(&self) -> Option<&DiscordChannel> {
        let gid = self.active_guild_id.as_deref()?;
        let cid = self.active_channel_id.as_deref()?;
        self.channels_by_guild
            .get(gid)?
            .channels
            .iter()
            .find(|c| c.id == cid)
    }

    pub fn voice_channel_label(&self) -> String {
        if let Some(ref cid) = self.voice.channel_id {
            if let Some(ref gid) = self.voice.guild_id {
                if let Some(ch) = self
                    .channels_by_guild
                    .get(gid)
                    .and_then(|c| c.channels.iter().find(|ch| ch.id == *cid))
                {
                    let gname = self
                        .home
                        .guilds
                        .iter()
                        .find(|g| g.id == *gid)
                        .map(|g| g.name.as_str())
                        .unwrap_or("Server");
                    return format!("{} · {}", ch.name, gname);
                }
            } else if let Some(dm) = self.home.dms.iter().find(|d| d.id == *cid) {
                return format!("Call with {}", dm.name);
            }
        }
        "Voice channel".into()
    }
}

impl AppState {
    pub(crate) fn discord_snapshot_path(&self) -> Option<PathBuf> {
        self.remote_cache_dir
            .as_ref()
            .and_then(|d| d.parent())
            .map(|p| p.join(DISCORD_SNAPSHOT_FILENAME))
    }

    pub(crate) fn hydrate_discord_snapshot(&mut self) -> bool {
        if self.discord.snapshot_hydrated || self.discord.home.me.is_some() {
            return false;
        }
        self.discord.snapshot_hydrated = true;
        let Some(path) = self.discord_snapshot_path() else {
            return false;
        };
        let Some(snap) = read_discord_snapshot(&path) else {
            return false;
        };
        let first_dm = snap.home.dms.first().map(|d| d.id.clone());
        self.discord.home = snap.home;
        self.discord.friends = snap.friends;
        self.discord.blocked = snap.blocked;
        if self.discord.accounts.is_empty() {
            self.discord.accounts = snap.accounts;
        }
        self.discord.unreads = snap.unreads;
        if self.discord.active_dm_id.is_none() {
            self.discord.active_dm_id = first_dm;
        }
        self.discord.from_snapshot = true;
        self.discord.loaded = true;
        self.prefetch_discord_images();
        true
    }

    pub(crate) fn save_discord_snapshot(&self) {
        if self.discord.home.me.is_none() || self.discord.from_snapshot {
            return;
        }
        let Some(path) = self.discord_snapshot_path() else {
            return;
        };
        let snap = DiscordDiskSnapshot {
            version: DISCORD_SNAPSHOT_VERSION,
            home: self.discord.home.clone(),
            friends: self.discord.friends.clone(),
            blocked: self.discord.blocked.clone(),
            accounts: self.discord.accounts.clone(),
            unreads: self.discord.unreads.clone(),
        };
        std::thread::Builder::new()
            .name("exo-dc-snap".into())
            .spawn(move || {
                let _ = write_discord_snapshot(&snap, &path);
            })
            .ok();
    }

    pub(crate) fn clear_discord_snapshot(&mut self) {
        self.discord.from_snapshot = false;
        self.discord.home = DiscordHome::default();
        self.discord.friends.clear();
        self.discord.blocked.clear();
        self.discord.unreads.clear();
        self.discord.active_dm_id = None;
        self.discord.active_guild_id = None;
        self.discord.active_channel_id = None;
        self.discord.channels_by_guild.clear();
        self.discord.members_by_guild.clear();
        if let Some(path) = self.discord_snapshot_path() {
            let _ = std::fs::remove_file(path);
        }
    }

    #[cfg_attr(not(windows), allow(dead_code))]
    pub fn warm_discord(&mut self) {
        self.hydrate_discord_snapshot();
        if self.status != HostStatus::Connected {
            return;
        }
        if self.discord.warmed {
            return;
        }
        self.discord.warmed = true;
        let already_ready = self.discord.status.state == "ready";
        if already_ready {
            self.refresh_discord_home();
        }
        self.fetch_discord_bootstrap_wave(already_ready);
    }

    pub fn load_discord_room(&mut self) {
        self.hydrate_discord_snapshot();
        self.announce_hardware_video_decode();
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.warmed = true;
        let already_ready = self.discord.status.state == "ready";
        if already_ready {
            self.refresh_discord_home();
        }
        self.fetch_discord_bootstrap_wave(already_ready);
    }

    fn fetch_discord_bootstrap_wave(&mut self, already_refreshed: bool) {
        #[cfg(not(windows))]
        let _ = already_refreshed;
        #[cfg(windows)]
        {
            self.call_host_async(
                METHOD_DISCORD_STATUS,
                json!({ "connect": true }),
                move |s, res: Result<DiscordStatus, String>| {
                    if let Ok(st) = res {
                        let ready = st.state == "ready";
                        let signed_out = st.state == "signedOut" && !st.signed_in;
                        s.discord.status = st;
                        s.discord.loaded = true;
                        if signed_out {
                            s.clear_discord_snapshot();
                            if s.room == Room::Discord && s.discord.signin.is_none() {
                                s.start_discord_signin();
                            }
                        } else if ready && !already_refreshed {
                            s.refresh_discord_home();
                        }
                    }
                },
            );
            self.fetch_friends_badge();
            self.call_host_async(
                METHOD_DISCORD_CLIENT_SETTINGS,
                json!({}),
                |_s, _res: Result<serde_json::Value, String>| {},
            );
            self.call_host_async(
                METHOD_DISCORD_ACCOUNTS,
                json!({}),
                |s, res: Result<DiscordAccountsReply, String>| {
                    if let Ok(rep) = res {
                        s.discord.accounts = rep.accounts;
                    }
                },
            );
            self.call_host_async(
                METHOD_DISCORD_VOICE,
                json!({}),
                |s, res: Result<DiscordVoiceReply, String>| {
                    if let Ok(rep) = res
                        && let Some(v) = rep.voice
                    {
                        s.discord.voice = v;
                    }
                },
            );
        }
    }

    pub fn refresh_discord_home(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        if self.discord.refresh_pending > 0 {
            self.discord.refresh_queued = true;
            return;
        }
        #[cfg(windows)]
        {
            self.discord.refresh_pending = 3;
            self.call_host_async(
                METHOD_DISCORD_HOME,
                json!({}),
                |s, res: Result<DiscordHome, String>| {
                    if let Ok(home) = res {
                        let first_dm = home.dms.first().map(|d| d.id.clone());
                        let active_guild = s.discord.active_guild_id.clone();
                        let voice_guild_ids: Vec<String> = home
                            .guilds
                            .iter()
                            .filter(|g| g.in_voice > 0 && !g.muted)
                            .take(4)
                            .map(|g| g.id.clone())
                            .collect();
                        s.discord.home = home;
                        s.discord.from_snapshot = false;
                        s.discord.loaded = true;
                        if s.discord.active_dm_id.is_none() {
                            s.discord.active_dm_id = first_dm;
                        }
                        if let Some(gid) = active_guild {
                            s.load_discord_guild(&gid, false);
                        }
                        for gid in voice_guild_ids {
                            if !s.discord.channels_by_guild.contains_key(&gid) {
                                s.load_discord_guild(&gid, false);
                            }
                        }
                    }
                    s.finish_discord_refresh_call();
                },
            );
            self.call_host_async(
                METHOD_DISCORD_FRIENDS,
                json!({}),
                |s, res: Result<DiscordFriendsReply, String>| {
                    if let Ok(rep) = res {
                        s.discord.friends = rep.friends;
                        s.discord.blocked = rep.restricted.map(|r| r.blocked).unwrap_or_default();
                    }
                    s.finish_discord_refresh_call();
                },
            );
            self.call_host_async(
                METHOD_DISCORD_UNREADS,
                json!({}),
                |s, res: Result<DiscordUnreadsReply, String>| {
                    if let Ok(rep) = res {
                        s.discord.unreads = rep.channels;
                    }
                    s.finish_discord_refresh_call();
                },
            );
        }
    }

    pub(crate) fn finish_discord_refresh_call(&mut self) {
        self.discord.refresh_pending = self.discord.refresh_pending.saturating_sub(1);
        if self.discord.refresh_pending == 0 {
            self.save_discord_snapshot();
            self.prefetch_discord_images();
            if self.discord.refresh_queued {
                self.discord.refresh_queued = false;
                self.refresh_discord_home();
            }
        }
    }

    pub fn start_discord_signin(&mut self) {
        self.discord.signin = Some(DiscordSignInStep {
            state: "waiting".into(),
            url: None,
            user: None,
            message: Some("Contacting Discord…".into()),
        });
        if self.status != HostStatus::Connected {
            self.discord.signin = Some(DiscordSignInStep {
                state: "waiting".into(),
                url: Some("https://discord.com/ra/ExoRuneDemoFingerprint0123456789ABCDEFGH".into()),
                user: None,
                message: None,
            });
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SIGN_IN,
            json!({}),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn cancel_discord_signin(&mut self) {
        self.discord.signin = None;
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SIGN_IN,
            json!({ "cancel": true }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn sign_out_discord(&mut self) {
        self.discord.accounts_menu_open = false;
        self.discord.status_menu_open = false;
        self.clear_discord_snapshot();
        if self.status != HostStatus::Connected {
            self.discord.status.state = "signedOut".into();
            self.discord.status.signed_in = false;
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SIGN_OUT,
            json!({}),
            |s, _res: Result<ActionResult, String>| {
                s.discord.status.state = "signedOut".into();
                s.discord.status.signed_in = false;
                s.clear_discord_snapshot();
            },
        );
    }

    pub fn switch_discord_account(&mut self, account_id: &str) {
        self.discord.accounts_menu_open = false;
        let id = account_id.to_string();
        if self.status != HostStatus::Connected {
            for acc in &mut self.discord.accounts {
                acc.current = acc.id == id;
            }
            if let Some(acc) = self.discord.accounts.iter().find(|a| a.id == id)
                && let Some(ref mut me) = self.discord.home.me
            {
                me.id = acc.id.clone();
                me.name = acc.name.clone();
            }
            return;
        }
        self.clear_discord_snapshot();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ACCOUNTS,
            json!({ "action": "switch", "id": id }),
            |s, res: Result<DiscordAccountsReply, String>| {
                if let Ok(rep) = res {
                    if !rep.ok {
                        s.notice = rep.message;
                    } else {
                        s.load_discord_room();
                    }
                }
            },
        );
    }

    pub fn set_discord_status(&mut self, status: &str) {
        self.set_discord_status_for(status, 0);
    }

    pub fn set_discord_status_for(&mut self, status: &str, minutes: i32) {
        self.close_context_menu();
        if let Some(ref mut me) = self.discord.home.me {
            me.status = Some(status.to_string());
        }
        if self.status != HostStatus::Connected {
            return;
        }
        let body = if minutes > 0 {
            json!({ "status": status, "minutes": minutes })
        } else {
            json!({ "status": status })
        };
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SET_STATUS,
            body,
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

    pub fn open_discord_status_menu(&mut self) {
        if self.discord.status_menu_open
            && matches!(
                self.context_menu.as_ref().map(|c| &c.target),
                Some(ContextMenuTarget::DiscordStatusMenu)
            )
        {
            self.close_context_menu();
        } else {
            self.open_context_menu(ContextMenuTarget::DiscordStatusMenu, 0.0, 0.0);
        }
    }

    pub fn open_discord_settings(&mut self, page: DiscordSettingsPage) {
        self.close_context_menu();
        self.discord.settings_page = page;
        self.discord.settings_open = true;
        self.discord.custom_status_open = false;
        self.load_discord_audio();
    }

    pub fn close_discord_settings(&mut self) {
        self.discord.settings_open = false;
    }

    pub fn save_discord_custom_status(&mut self, text: String) {
        self.discord.custom_status_open = false;
        let trimmed = text.trim().to_string();
        self.discord.custom_status = if trimmed.is_empty() {
            None
        } else {
            Some(trimmed.clone())
        };
        if self.status != HostStatus::Connected {
            return;
        }
        let payload = if trimmed.is_empty() {
            json!({ "customStatus": null })
        } else {
            json!({ "customStatus": { "text": trimmed } })
        };
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CLIENT_SETTINGS,
            payload,
            |_s, _res: Result<serde_json::Value, String>| {},
        );
    }

    pub fn is_dm_pinned(&self, dm_id: &str) -> bool {
        if self.discord.pinned_dm_overrides.contains(dm_id) {
            return true;
        }
        if self.discord.unpinned_dm_overrides.contains(dm_id) {
            return false;
        }
        self.settings
            .ui_prefs
            .get("discordPinnedDms")
            .and_then(|v| v.as_array())
            .is_some_and(|arr| arr.iter().any(|v| v.as_str() == Some(dm_id)))
    }

    pub fn toggle_pin_dm(&mut self, dm_id: &str) {
        if self.is_dm_pinned(dm_id) {
            self.discord.pinned_dm_overrides.remove(dm_id);
            self.discord.unpinned_dm_overrides.insert(dm_id.to_string());
        } else {
            self.discord.unpinned_dm_overrides.remove(dm_id);
            self.discord.pinned_dm_overrides.insert(dm_id.to_string());
        }
    }

    pub fn discord_channel_action(&mut self, channel_id: &str, action: &str) {
        if action == "read" {
            for dm in &mut self.discord.home.dms {
                if dm.id == channel_id {
                    dm.unread = false;
                    dm.mentions = 0;
                }
            }
            for chans in self.discord.channels_by_guild.values_mut() {
                for ch in &mut chans.channels {
                    if ch.id == channel_id {
                        ch.unread = false;
                        ch.mentions = 0;
                    }
                }
            }
        } else if action == "close" {
            self.discord.home.dms.retain(|d| d.id != channel_id);
            if self.discord.active_dm_id.as_deref() == Some(channel_id) {
                self.discord.active_dm_id = self.discord.home.dms.first().map(|d| d.id.clone());
            }
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CHANNEL,
            json!({ "channelId": channel_id, "action": action }),
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

    pub fn discord_channel_notify(
        &mut self,
        channel_id: &str,
        muted: Option<bool>,
        minutes: Option<i32>,
        level: Option<i32>,
    ) {
        if let Some(m) = muted {
            for dm in &mut self.discord.home.dms {
                if dm.id == channel_id {
                    dm.muted = m;
                }
            }
            for chans in self.discord.channels_by_guild.values_mut() {
                for ch in &mut chans.channels {
                    if ch.id == channel_id {
                        ch.muted = m;
                    }
                }
            }
        }
        if self.status != HostStatus::Connected {
            return;
        }
        let body = if let Some(lvl) = level {
            json!({ "channelId": channel_id, "action": "notify", "level": lvl })
        } else if let Some(mins) = minutes {
            json!({ "channelId": channel_id, "action": "mute", "muted": muted.unwrap_or(true), "minutes": mins })
        } else {
            json!({ "channelId": channel_id, "action": "mute", "muted": muted.unwrap_or(true) })
        };
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CHANNEL,
            body,
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

    pub fn discord_mark_guild_read(&mut self, guild_id: &str) {
        if let Some(g) = self
            .discord
            .home
            .guilds
            .iter_mut()
            .find(|g| g.id == guild_id)
        {
            g.unread = false;
            g.mentions = 0;
        }
        if let Some(chans) = self.discord.channels_by_guild.get_mut(guild_id) {
            for ch in &mut chans.channels {
                ch.unread = false;
                ch.mentions = 0;
            }
        }
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

    pub fn discord_server_action(&mut self, action: &str, mut params: serde_json::Value) {
        if let Some(obj) = params.as_object_mut() {
            obj.insert("action".into(), json!(action));
        }
        if action == "mute"
            && let Some(gid) = params.get("guildId").and_then(|v| v.as_str())
            && let Some(m) = params.get("muted").and_then(|v| v.as_bool())
            && let Some(g) = self.discord.home.guilds.iter_mut().find(|g| g.id == gid)
        {
            g.muted = m;
        }
        if action == "leave"
            && let Some(gid) = params.get("guildId").and_then(|v| v.as_str())
        {
            self.discord.home.guilds.retain(|g| g.id != gid);
            if self.discord.active_guild_id.as_deref() == Some(gid) {
                self.discord.active_guild_id = None;
                self.discord.place = DiscordPlace::Home;
            }
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SERVER,
            params,
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

    pub fn discord_ring_dm(&mut self, dm_id: &str) {
        self.join_discord_voice(None, dm_id);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_RING,
            json!({ "channelId": dm_id }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn collapse_all_categories(&mut self, guild_id: &str) {
        if let Some(chans) = self.discord.channels_by_guild.get(guild_id) {
            for ch in &chans.channels {
                if ch.kind == "category" {
                    self.discord.collapsed_categories.insert(ch.id.clone());
                }
            }
        }
    }

    pub fn toggle_hide_muted_guild(&mut self, guild_id: &str) {
        if !self.discord.hide_muted_guilds.remove(guild_id) {
            self.discord.hide_muted_guilds.insert(guild_id.to_string());
        }
    }

    pub fn discord_friend_action(&mut self, action: &str, user_id: &str) {
        if action == "block" {
            self.discord.friends.retain(|f| f.id != user_id);
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_FRIEND,
            json!({ "action": action, "userId": user_id }),
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

    pub fn toggle_ignore_user(&mut self, user_id: &str) {
        let now_ignored = if self.discord.ignored_users.remove(user_id) {
            false
        } else {
            self.discord.ignored_users.insert(user_id.to_string());
            true
        };
        self.discord_friend_action(if now_ignored { "ignore" } else { "unignore" }, user_id);
    }

    pub fn mention_user_in_composer(&mut self, user_id: &str) {
        let mut cur = self.discord.chat.composer.text.clone();
        if !cur.is_empty() && !cur.ends_with(' ') {
            cur.push(' ');
        }
        cur.push_str(&format!("<@{user_id}> "));
        self.discord.chat.composer = TextEdit::new(&cur);
    }

    pub fn open_discord_place(&mut self, place: DiscordPlace) {
        self.discord.place = place;
        self.discord.status_menu_open = false;
        self.discord.accounts_menu_open = false;
        self.discord.shop_open = false;
        self.discord.inbox_open = false;
        self.discord.chat.drawer = ChatDrawer::None;
        if place == DiscordPlace::Home {
            self.discord.active_guild_id = None;
        }
        if place == DiscordPlace::Messages {
            self.discord.active_guild_id = None;
            if self.discord.active_dm_id.is_none() {
                self.discord.active_dm_id = self.discord.home.dms.first().map(|d| d.id.clone());
            }
            if let Some(dm_id) = self.discord.active_dm_id.clone()
                && !self.discord.chat.messages_by_channel.contains_key(&dm_id)
            {
                self.load_discord_messages(&dm_id, None);
            }
        }
    }

    pub fn open_discord_dm(&mut self, dm_id: &str) {
        let prev = self.active_chat_channel_id();
        self.discord.place = DiscordPlace::Messages;
        self.discord.active_guild_id = None;
        self.discord.active_dm_id = Some(dm_id.to_string());
        self.discord.shop_open = false;
        self.discord.inbox_open = false;
        self.switch_composer_draft(prev.as_deref(), Some(dm_id));
        self.discord.chat.editing = None;
        self.discord.chat.drawer = ChatDrawer::None;
        self.load_discord_messages(dm_id, None);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_VIEW,
            json!({ "channelId": dm_id }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn open_discord_guild(&mut self, guild_id: &str) {
        self.discord.place = DiscordPlace::Servers;
        self.discord.active_guild_id = Some(guild_id.to_string());
        self.discord.shop_open = false;
        self.discord.inbox_open = false;
        self.discord.chat.reply_to = None;
        self.discord.chat.editing = None;
        self.discord.chat.drawer = ChatDrawer::None;
        if let Some(existing) = self.discord.channels_by_guild.get(guild_id) {
            let first = existing
                .channels
                .iter()
                .find(|c| c.kind == "text" && c.can_read != Some(false))
                .or_else(|| existing.channels.iter().find(|c| c.kind != "category"))
                .map(|c| c.id.clone());
            self.discord.active_channel_id = first.clone();
            if let Some(cid) = first
                && !self.discord.chat.messages_by_channel.contains_key(&cid)
            {
                self.load_discord_messages(&cid, None);
            }
        } else {
            self.discord.active_channel_id = None;
        }
        self.load_discord_guild(guild_id, true);
    }

    pub fn load_discord_guild(&mut self, guild_id: &str, select_default: bool) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let gid = guild_id.to_string();
            let gid_m = gid.clone();
            self.call_host_async(
                METHOD_DISCORD_CHANNELS,
                json!({ "guildId": gid }),
                move |s, res: Result<DiscordChannels, String>| {
                    if let Ok(chans) = res {
                        let first = chans
                            .channels
                            .iter()
                            .find(|c| c.kind == "text" && c.can_read != Some(false))
                            .or_else(|| chans.channels.iter().find(|c| c.kind != "category"))
                            .map(|c| c.id.clone());
                        s.discord.channels_by_guild.insert(gid.clone(), chans);
                        if select_default
                            && s.discord.active_guild_id.as_deref() == Some(gid.as_str())
                        {
                            let valid = s.discord.active_channel_id.as_deref().is_some_and(|cid| {
                                s.discord
                                    .channels_by_guild
                                    .get(&gid)
                                    .is_some_and(|c| c.channels.iter().any(|x| x.id == cid))
                            });
                            if !valid {
                                s.discord.active_channel_id = first.clone();
                                if let Some(cid) = first {
                                    s.notify_discord_view(&cid);
                                    s.load_discord_messages(&cid, None);
                                }
                            }
                        }
                    }
                },
            );
            let open_ch = self.discord.active_channel_id.clone();
            self.call_host_async(
                METHOD_DISCORD_MEMBERS,
                json!({ "guildId": gid_m, "channelId": open_ch }),
                move |s, res: Result<DiscordMemberList, String>| {
                    if let Ok(list) = res {
                        s.discord.members_by_guild.insert(gid_m, list);
                    }
                },
            );
        }
    }

    pub fn open_discord_channel(&mut self, channel_id: &str) {
        let prev = self.active_chat_channel_id();
        self.discord.active_channel_id = Some(channel_id.to_string());
        self.switch_composer_draft(prev.as_deref(), Some(channel_id));
        self.discord.chat.editing = None;
        self.discord.chat.drawer = ChatDrawer::None;
        self.notify_discord_view(channel_id);
        self.load_discord_messages(channel_id, None);
        if let Some(gid) = self.discord.active_guild_id.clone() {
            self.refresh_discord_members(&gid);
        }
    }

    fn notify_discord_view(&mut self, channel_id: &str) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_VIEW,
            json!({ "channelId": channel_id }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn refresh_discord_members(&mut self, guild_id: &str) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            let gid = guild_id.to_string();
            let cid = self.discord.active_channel_id.clone();
            self.call_host_async(
                METHOD_DISCORD_MEMBERS,
                json!({ "guildId": gid, "channelId": cid }),
                move |s, res: Result<DiscordMemberList, String>| {
                    if let Ok(list) = res {
                        s.discord.members_by_guild.insert(gid, list);
                    }
                },
            );
        }
    }

    pub fn join_discord_voice(&mut self, guild_id: Option<&str>, channel_id: &str) {
        if self.status != HostStatus::Connected {
            self.discord.voice.state = "connected".into();
            self.discord.voice.guild_id = guild_id.map(str::to_string);
            self.discord.voice.channel_id = Some(channel_id.to_string());
            self.discord.voice.e2ee = true;
            self.discord.voice.rtt_ms = Some(19);
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_VOICE,
            json!({ "action": "join", "guildId": guild_id, "channelId": channel_id }),
            |s, res: Result<DiscordVoiceReply, String>| {
                if let Ok(rep) = res {
                    if let Some(v) = rep.voice {
                        s.discord.voice = v;
                    }
                    if !rep.ok
                        && let Some(msg) = rep.message
                    {
                        s.notice = Some(msg);
                    }
                }
            },
        );
    }

    pub fn leave_discord_voice(&mut self) {
        self.discord.voice_ui.watching_stream = None;
        self.discord.voice_ui.fullscreen_stream = false;
        self.discord.voice_ui.decoders.clear();
        self.discord.voice_ui.golive_state = "off".into();
        self.discord.voice_ui.golive_title = None;
        self.discord.voice_ui.golive_stats = None;
        self.discord.voice_ui.camera_on = false;
        if self.status != HostStatus::Connected {
            self.discord.voice.state = "off".into();
            self.discord.voice.guild_id = None;
            self.discord.voice.channel_id = None;
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_VOICE,
            json!({ "action": "leave" }),
            |s, res: Result<DiscordVoiceReply, String>| {
                if let Ok(rep) = res
                    && let Some(v) = rep.voice
                {
                    s.discord.voice = v;
                }
            },
        );
    }

    pub fn toggle_discord_mute(&mut self) {
        let next = !self.discord.voice.muted;
        self.discord.voice.muted = next;
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_VOICE,
            json!({ "action": "set", "muted": next }),
            |s, res: Result<DiscordVoiceReply, String>| {
                if let Ok(rep) = res
                    && let Some(v) = rep.voice
                {
                    s.discord.voice = v;
                }
            },
        );
    }

    pub fn toggle_discord_deafen(&mut self) {
        let next = !self.discord.voice.deafened;
        self.discord.voice.deafened = next;
        if next {
            self.discord.voice.muted = true;
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_VOICE,
            json!({ "action": "set", "deafened": next }),
            |s, res: Result<DiscordVoiceReply, String>| {
                if let Ok(rep) = res
                    && let Some(v) = rep.voice
                {
                    s.discord.voice = v;
                }
            },
        );
    }

    pub fn open_dm_with_friend(&mut self, user_id: &str) {
        if let Some(dm) = self
            .discord
            .home
            .dms
            .iter()
            .find(|d| !d.group && d.user_id.as_deref() == Some(user_id))
        {
            let id = dm.id.clone();
            self.open_discord_dm(&id);
            return;
        }
        if self.status != HostStatus::Connected {
            self.discord.place = DiscordPlace::Messages;
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_FRIEND,
            json!({ "action": "dm", "userId": user_id }),
            |s, res: Result<DiscordFriendActionReply, String>| {
                if let Ok(rep) = res {
                    if let Some(cid) = rep.channel_id {
                        s.refresh_discord_home();
                        s.open_discord_dm(&cid);
                    } else if let Some(msg) = rep.message {
                        s.notice = Some(msg);
                    }
                }
            },
        );
    }

    pub fn accept_discord_friend(&mut self, user_id: &str) {
        if self.status != HostStatus::Connected {
            if let Some(f) = self.discord.friends.iter_mut().find(|f| f.id == user_id) {
                f.kind = "friend".into();
            }
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_FRIEND,
            json!({ "action": "accept", "userId": user_id }),
            |s, res: Result<DiscordFriendActionReply, String>| {
                if let Ok(rep) = res {
                    if rep.ok {
                        s.refresh_discord_home();
                    } else if let Some(msg) = rep.message {
                        s.notice = Some(msg);
                    }
                }
            },
        );
    }

    pub fn remove_discord_friend(&mut self, user_id: &str) {
        if self.status != HostStatus::Connected {
            self.discord.friends.retain(|f| f.id != user_id);
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_FRIEND,
            json!({ "action": "remove", "userId": user_id }),
            |s, res: Result<DiscordFriendActionReply, String>| {
                if let Ok(rep) = res {
                    if rep.ok {
                        s.refresh_discord_home();
                    } else if let Some(msg) = rep.message {
                        s.notice = Some(msg);
                    }
                }
            },
        );
    }

    pub fn submit_add_discord_friend(&mut self) {
        let username = self.discord.add_friend_input.text.trim().to_string();
        if username.len() < 2 {
            self.notice = Some("Enter a Discord username".into());
            return;
        }
        self.discord.add_friend_input = TextEdit::default();
        self.discord.add_friend_open = false;
        if self.status != HostStatus::Connected {
            self.notice = Some(format!("Friend request sent to @{username}"));
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_FRIEND,
            json!({ "action": "add", "username": username }),
            |s, res: Result<DiscordFriendActionReply, String>| {
                if let Ok(rep) = res {
                    if rep.ok {
                        s.notice = Some("Friend request sent".into());
                        s.refresh_discord_home();
                    } else {
                        s.notice = rep
                            .message
                            .or_else(|| Some("Could not send request".into()));
                    }
                }
            },
        );
    }

    pub fn toggle_discord_category(&mut self, cat_id: &str) {
        if !self.discord.collapsed_categories.remove(cat_id) {
            self.discord.collapsed_categories.insert(cat_id.to_string());
        }
    }

    pub fn toggle_discord_folder(&mut self, folder_id: &str) {
        if !self.discord.expanded_folders.remove(folder_id) {
            self.discord.expanded_folders.insert(folder_id.to_string());
        }
    }

    pub fn handle_discord_state(&mut self, ev: DiscordStateEvent) {
        let became_ready = ev.state == "ready" && self.discord.status.state != "ready";
        self.discord.status.state = ev.state.clone();
        self.discord.status.message = ev.message;
        if ev.state == "ready" {
            self.discord.status.signed_in = true;
            self.discord.signin = None;
        } else if ev.state == "signedOut" {
            self.discord.status.signed_in = false;
            self.clear_discord_snapshot();
        }
        if became_ready {
            if self.room == Room::Discord {
                self.discord.last_ready_refresh_at = Some(Instant::now());
                self.refresh_discord_home();
            } else {
                self.discord_dirty_while_closed = true;
            }
        }
    }

    pub fn handle_discord_signin(&mut self, step: DiscordSignInStep) {
        if step.state == "done" {
            self.discord.signin = None;
            self.load_discord_room();
        } else {
            self.discord.signin = Some(step);
        }
    }

    pub fn handle_discord_changed(&mut self, ev: DiscordChangedEvent) -> bool {
        if let Some(ref gid) = ev.guild_id
            && let Some(cnt) = ev.in_voice
            && let Some(g) = self.discord.home.guilds.iter_mut().find(|g| g.id == *gid)
        {
            g.in_voice = cnt;
        }
        if self.room != Room::Discord {
            self.discord_dirty_while_closed = true;
            return false;
        }
        if let Some(ref gid) = ev.guild_id {
            if self.discord.active_guild_id.as_deref() == Some(gid.as_str())
                || self.discord.channels_by_guild.contains_key(gid)
            {
                self.load_discord_guild(gid, false);
            }
        } else {
            let recent_ready = self
                .discord
                .last_ready_refresh_at
                .is_some_and(|at| at.elapsed() < DISCORD_READY_BURST_SUPPRESS);
            if !recent_ready {
                self.refresh_discord_home();
            }
        }
        if ev.friends {
            #[cfg(windows)]
            if self.status == HostStatus::Connected {
                self.call_host_async(
                    METHOD_DISCORD_FRIENDS,
                    json!({}),
                    |s, res: Result<DiscordFriendsReply, String>| {
                        if let Ok(rep) = res {
                            s.discord.friends = rep.friends;
                        }
                    },
                );
            }
        }
        true
    }

    pub fn handle_discord_members(&mut self, guild_id: &str) -> bool {
        if self.room != Room::Discord {
            self.discord_dirty_while_closed = true;
            return false;
        }
        if self.discord.active_guild_id.as_deref() == Some(guild_id) {
            self.refresh_discord_members(guild_id);
            return true;
        }
        false
    }

    pub fn handle_discord_presence(&mut self, ev: DiscordPresenceEvent) -> bool {
        let mut changed = false;
        if let Some(ref mut me) = self.discord.home.me
            && me.id == ev.user_id
            && me.status.as_deref() != Some(ev.status.as_str())
        {
            me.status = Some(ev.status.clone());
            changed = true;
        }
        for dm in &mut self.discord.home.dms {
            if dm.user_id.as_deref() == Some(ev.user_id.as_str()) {
                if dm.status.as_deref() != Some(ev.status.as_str()) {
                    dm.status = Some(ev.status.clone());
                    changed = true;
                }
                if ev.activity.is_some() && dm.subtitle != ev.activity {
                    dm.subtitle = ev.activity.clone();
                    changed = true;
                }
            }
        }
        for f in &mut self.discord.friends {
            if f.id == ev.user_id {
                let diff = f.status != ev.status
                    || f.activity != ev.activity
                    || f.activity_type != ev.activity_type
                    || f.rich != ev.rich
                    || f.prep != ev.prep;
                if diff {
                    f.status = ev.status.clone();
                    f.activity = ev.activity.clone();
                    f.activity_type = ev.activity_type;
                    f.rich = ev.rich.clone();
                    f.prep = ev.prep.clone();
                    changed = true;
                }
            }
        }
        changed && self.room == Room::Discord
    }

    pub fn handle_discord_unread(&mut self, ev: DiscordUnreadEvent) -> bool {
        let mut changed = false;
        if let Some(ref gid) = ev.guild_id {
            if let Some(g) = self.discord.home.guilds.iter_mut().find(|g| g.id == *gid)
                && (g.unread != ev.guild_unread || g.mentions != ev.guild_mentions)
            {
                g.unread = ev.guild_unread;
                g.mentions = ev.guild_mentions;
                changed = true;
            }
            if let Some(chans) = self.discord.channels_by_guild.get_mut(gid)
                && let Some(c) = chans.channels.iter_mut().find(|c| c.id == ev.channel_id)
                && (c.unread != ev.unread || c.mentions != ev.mentions)
            {
                c.unread = ev.unread;
                c.mentions = ev.mentions;
                changed = true;
            }
        } else if let Some(dm) = self
            .discord
            .home
            .dms
            .iter_mut()
            .find(|d| d.id == ev.channel_id)
            && (dm.unread != ev.unread || dm.mentions != ev.mentions)
        {
            dm.unread = ev.unread;
            dm.mentions = ev.mentions;
            changed = true;
        }
        changed && self.room == Room::Discord
    }

    pub fn handle_discord_voice(&mut self, voice: DiscordVoice) {
        self.discord.voice = voice;
    }

    pub fn handle_discord_speaking(&mut self, user_id: &str, speaking: bool) {
        if speaking {
            self.discord.speaking.insert(user_id.to_string());
        } else {
            self.discord.speaking.remove(user_id);
        }
    }

    pub fn collect_discord_image_urls(&self) -> Vec<String> {
        let mut urls: Vec<String> = Vec::new();
        if let Some(ref me) = self.discord.home.me {
            if let Some(ref u) = me.avatar {
                urls.push(u.clone());
            }
            if let Some(u) = crate::discord::decoration_url(me.look.as_ref()) {
                urls.push(u);
            }
        }
        let mut by_id: HashMap<&str, &DiscordGuild> = HashMap::new();
        for g in &self.discord.home.guilds {
            by_id.insert(g.id.as_str(), g);
        }
        for item in self.discord.home.rail.iter().take(60) {
            for gid in item.guild_ids.iter().take(4) {
                if let Some(g) = by_id.get(gid.as_str())
                    && let Some(ref u) = g.icon
                {
                    urls.push(u.clone());
                }
            }
        }
        for g in self.discord.home.guilds.iter().take(48) {
            if let Some(ref u) = g.icon {
                urls.push(u.clone());
            }
        }
        for dm in self.discord.home.dms.iter().take(32) {
            if let Some(ref u) = dm.avatar {
                urls.push(u.clone());
            }
            if let Some(u) = crate::discord::decoration_url(dm.look.as_ref()) {
                urls.push(u);
            }
        }
        for f in self
            .discord
            .friends
            .iter()
            .filter(|f| f.kind == "friend" && f.status != "offline")
        {
            if let Some(ref u) = f.avatar {
                urls.push(u.clone());
            }
            if let Some(ref look) = f.look {
                if let Some(u) = crate::discord::decoration_url(Some(look)) {
                    urls.push(u);
                }
                if let Some(ref plate) = look.nameplate
                    && let Some(u) = crate::discord::nameplate_static_url(plate)
                {
                    urls.push(u);
                }
                if let Some(ref tag) = look.tag
                    && let Some(u) = crate::discord::clan_badge_url(tag)
                {
                    urls.push(u);
                }
            }
        }
        for msgs in self.discord.chat.messages_by_channel.values() {
            for m in msgs.iter().rev().take(30) {
                if let Some(u) = m.author.avatar_url() {
                    urls.push(u);
                }
            }
        }
        for ml in self.discord.members_by_guild.values() {
            for item in ml.items.iter().flatten().take(30) {
                if let DiscordMemberItem::Member(m) = item {
                    if let Some(ref u) = m.avatar {
                        urls.push(u.clone());
                    }
                    if let Some(u) = crate::discord::decoration_url(m.look.as_deref()) {
                        urls.push(u);
                    }
                }
            }
        }
        let mut seen: HashSet<String> = HashSet::with_capacity(urls.len());
        urls.retain(|u| seen.insert(u.clone()));
        urls
    }

    pub fn discord_remote_image(&self, url: &str, scale: f32) -> Option<rune::Image> {
        self.discord_remote_image_sized(url, 360.0, scale)
    }

    pub fn discord_remote_image_sized(
        &self,
        url: &str,
        max_dim: f32,
        scale: f32,
    ) -> Option<rune::Image> {
        if self.discord.prefetching_urls.contains(url) {
            return None;
        }
        self.remote_image_sized(url, max_dim, scale)
    }

    pub fn prefetch_discord_images(&mut self) {
        let Some(dir) = self.remote_cache_dir.clone() else {
            return;
        };
        let urls = self.collect_discord_image_urls();
        let mut missing: VecDeque<String> = VecDeque::new();
        let has_cache = self.image_cache.is_some();
        for url in urls {
            if exo_rpc::remote_art::parse_allowed_https(&url).is_none() {
                continue;
            }
            if self.discord.warmed_image_urls.contains(&url) {
                continue;
            }
            if exo_rpc::remote_art::cache_path_for(&dir, &url).is_file() {
                if has_cache {
                    let _ = self.remote_image(&url, 1.0);
                    self.discord.warmed_image_urls.insert(url);
                }
            } else if self.discord.prefetched_urls.insert(url.clone()) {
                missing.push_back(url);
            }
        }
        if missing.is_empty() {
            return;
        }
        #[cfg(windows)]
        if let Some(handle) = self.handle.clone() {
            for url in &missing {
                self.discord.prefetching_urls.insert(url.clone());
            }
            let workers = DISCORD_PREFETCH_WORKERS.min(missing.len());
            let queue = Arc::new(Mutex::new(missing));
            for i in 0..workers {
                let queue = Arc::clone(&queue);
                let dir = dir.clone();
                let handle = handle.clone();
                std::thread::Builder::new()
                    .name(format!("exo-dc-art-{i}"))
                    .spawn(move || {
                        let mut done_batch: Vec<(String, bool)> = Vec::with_capacity(4);
                        loop {
                            let next = queue.lock().ok().and_then(|mut q| q.pop_front());
                            let Some(url) = next else {
                                break;
                            };
                            let ok = exo_rpc::remote_art::fetch_cached_image(&url, &dir).is_some();
                            done_batch.push((url, ok));
                            if done_batch.len() >= 4 {
                                let batch = std::mem::take(&mut done_batch);
                                handle.update(move |s, _| {
                                    for (u, ok) in batch {
                                        s.discord.prefetching_urls.remove(&u);
                                        if ok {
                                            let _ = s.remote_image(&u, 1.0);
                                            s.discord.warmed_image_urls.insert(u);
                                        }
                                    }
                                });
                            }
                        }
                        if !done_batch.is_empty() {
                            handle.update(move |s, _| {
                                for (u, ok) in done_batch {
                                    s.discord.prefetching_urls.remove(&u);
                                    if ok {
                                        let _ = s.remote_image(&u, 1.0);
                                        s.discord.warmed_image_urls.insert(u);
                                    }
                                }
                            });
                        }
                    })
                    .ok();
            }
        }
    }

    pub fn preload_discord_images_sync(&self, scale: f32) {
        if self.image_cache.is_none() || !self.discord.loaded {
            return;
        }
        let images: Vec<rune::Image> = self
            .collect_discord_image_urls()
            .into_iter()
            .filter_map(|u| self.remote_image_sized(&u, 64.0, scale))
            .collect();
        for img in images {
            let _ = img.wait();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::discord::can_show_room;
    use std::sync::atomic::{AtomicUsize, Ordering};

    static TEST_COUNTER: AtomicUsize = AtomicUsize::new(0);

    fn temp_cache_dir(label: &str) -> PathBuf {
        let n = TEST_COUNTER.fetch_add(1, Ordering::Relaxed);
        let dir = std::env::temp_dir()
            .join(format!("exo-dc-test-{}-{label}-{n}", std::process::id()))
            .join("rune-art");
        let _ = std::fs::create_dir_all(&dir);
        dir
    }

    fn realistic_snapshot() -> DiscordDiskSnapshot {
        let dms = (0..50)
            .map(|i| DiscordDm {
                id: format!("dm-{i}"),
                name: format!("Friend {i}"),
                avatar: Some(format!(
                    "https://cdn.discordapp.com/avatars/100{i}/hash{i}.webp?size=64"
                )),
                unread: i < 3,
                mentions: if i == 0 { 1 } else { 0 },
                status: Some(if i < 19 { "online" } else { "offline" }.into()),
                user_id: Some(format!("100{i}")),
                ..Default::default()
            })
            .collect();
        let guilds = (0..27)
            .map(|i| DiscordGuild {
                id: format!("g-{i}"),
                name: format!("Guild {i}"),
                icon: Some(format!(
                    "https://cdn.discordapp.com/icons/200{i}/icon{i}.webp?size=64"
                )),
                in_voice: if i == 0 { 2 } else { 0 },
                ..Default::default()
            })
            .collect();
        let friends = (0..60)
            .map(|i| DiscordFriend {
                id: format!("100{i}"),
                name: format!("Friend {i}"),
                avatar: Some(format!(
                    "https://cdn.discordapp.com/avatars/100{i}/hash{i}.webp?size=64"
                )),
                kind: "friend".into(),
                status: if i < 19 { "online" } else { "offline" }.into(),
                dm_id: Some(format!("dm-{i}")),
                ..Default::default()
            })
            .collect();
        DiscordDiskSnapshot {
            version: DISCORD_SNAPSHOT_VERSION,
            home: DiscordHome {
                me: Some(DiscordMe {
                    id: "900".into(),
                    name: "Erix".into(),
                    avatar: Some("https://cdn.discordapp.com/avatars/900/me.webp?size=64".into()),
                    status: Some("online".into()),
                    ..Default::default()
                }),
                dms,
                guilds,
                ..Default::default()
            },
            friends,
            blocked: Vec::new(),
            accounts: vec![DiscordAccountItem {
                id: "900".into(),
                name: "Erix".into(),
                current: true,
                ..Default::default()
            }],
            unreads: Vec::new(),
        }
    }

    #[test]
    fn snapshot_hydrates_cached_room_before_ready_and_clears_on_sign_out() {
        let dir = temp_cache_dir("hydrate");
        let mut app = AppState::for_host_pipe();
        app.remote_cache_dir = Some(dir.clone());
        let snap_path = app.discord_snapshot_path().expect("snapshot path");
        write_discord_snapshot(&realistic_snapshot(), &snap_path).expect("write snapshot");

        assert!(app.hydrate_discord_snapshot());
        assert!(app.discord.from_snapshot);
        assert!(app.discord.loaded);
        assert_eq!(app.discord.home.dms.len(), 50);
        assert_eq!(app.discord.home.guilds.len(), 27);
        assert_eq!(app.discord.friends.len(), 60);
        assert_eq!(app.discord.active_dm_id.as_deref(), Some("dm-0"));
        assert!(can_show_room(&app));

        app.handle_discord_state(DiscordStateEvent {
            state: "signedOut".into(),
            message: None,
        });
        assert!(!can_show_room(&app));
        assert!(app.discord.home.me.is_none());
        assert!(app.discord.friends.is_empty());
        assert!(!snap_path.exists());

        let _ = std::fs::remove_dir_all(dir.parent().unwrap());
    }

    #[test]
    fn snapshot_ignores_corrupt_or_wrong_version_files() {
        let dir = temp_cache_dir("corrupt");
        let mut app = AppState::for_host_pipe();
        app.remote_cache_dir = Some(dir.clone());
        let snap_path = app.discord_snapshot_path().expect("snapshot path");
        std::fs::write(&snap_path, b"{not valid json").expect("write corrupt");
        assert!(read_discord_snapshot(&snap_path).is_none());

        let mut wrong = realistic_snapshot();
        wrong.version = DISCORD_SNAPSHOT_VERSION + 99;
        write_discord_snapshot(&wrong, &snap_path).expect("write wrong version");
        assert!(read_discord_snapshot(&snap_path).is_none());

        let mut no_me = realistic_snapshot();
        no_me.home.me = None;
        write_discord_snapshot(&no_me, &snap_path).expect("write empty me");
        assert!(read_discord_snapshot(&snap_path).is_none());

        let _ = std::fs::remove_dir_all(dir.parent().unwrap());
    }

    #[test]
    fn can_show_room_handles_connecting_ready_signed_out_and_error() {
        let mut app = AppState::for_host_pipe();
        assert!(!can_show_room(&app));

        app.discord.home.me = Some(DiscordMe {
            id: "1".into(),
            name: "Erix".into(),
            ..Default::default()
        });
        app.discord.status.state = "connecting".into();
        assert!(can_show_room(&app));

        app.discord.status.state = "ready".into();
        assert!(can_show_room(&app));

        app.discord.status.state = "error".into();
        assert!(!can_show_room(&app));

        app.discord.status.state = "signedOut".into();
        assert!(!can_show_room(&app));
    }

    #[test]
    fn warm_discord_hydrates_offline_and_marks_warmed_once_connected() {
        let dir = temp_cache_dir("warm");
        let mut app = AppState::for_host_pipe();
        app.remote_cache_dir = Some(dir.clone());
        let snap_path = app.discord_snapshot_path().expect("snapshot path");
        write_discord_snapshot(&realistic_snapshot(), &snap_path).expect("write snapshot");

        app.warm_discord();
        assert!(app.discord.home.me.is_some());
        assert!(!app.discord.warmed);

        app.status = HostStatus::Connected;
        app.warm_discord();
        assert!(app.discord.warmed);
        app.warm_discord();
        assert!(app.discord.warmed);

        let _ = std::fs::remove_dir_all(dir.parent().unwrap());
    }

    #[test]
    fn refresh_coalesces_waves_and_suppresses_ready_changed_burst() {
        let mut app = AppState::for_host_pipe();
        app.status = HostStatus::Connected;
        app.discord.refresh_pending = 2;

        app.refresh_discord_home();
        assert_eq!(app.discord.refresh_pending, 2);
        assert!(app.discord.refresh_queued);

        app.discord.refresh_queued = false;
        app.discord.refresh_pending = 0;
        app.discord.last_ready_refresh_at = Some(Instant::now());
        app.handle_discord_changed(DiscordChangedEvent {
            guild_id: None,
            friends: false,
            in_voice: None,
            ..Default::default()
        });
        assert_eq!(app.discord.refresh_pending, 0);
    }

    #[test]
    fn collect_discord_image_urls_preserves_priority_order_and_deduplicates() {
        let mut app = AppState::for_host_pipe();
        let snap = realistic_snapshot();
        app.discord.home = snap.home;
        app.discord.friends = snap.friends;
        let urls = app.collect_discord_image_urls();
        assert_eq!(
            urls.first().map(String::as_str),
            Some("https://cdn.discordapp.com/avatars/900/me.webp?size=64")
        );
        let unique: HashSet<&str> = urls.iter().map(String::as_str).collect();
        assert_eq!(unique.len(), urls.len());
    }

    #[test]
    fn bench_snapshot_hydrate_and_discord_view_build() {
        let dir = temp_cache_dir("bench");
        let mut app = AppState::for_host_pipe();
        app.remote_cache_dir = Some(dir.clone());
        let snap_path = app.discord_snapshot_path().expect("snapshot path");
        write_discord_snapshot(&realistic_snapshot(), &snap_path).expect("write snapshot");

        let t0 = Instant::now();
        assert!(app.hydrate_discord_snapshot());
        let hydrate_us = t0.elapsed().as_micros();

        let t1 = Instant::now();
        let budget = crate::shell::BlurBudget::compute(&app, false);
        let _root = crate::discord::view(&app, 1.0, budget);
        let first_view_us = t1.elapsed().as_micros();

        let mut samples_us = Vec::with_capacity(50);
        for _ in 0..50 {
            let start = Instant::now();
            let _root = crate::discord::view(&app, 1.0, budget);
            samples_us.push(start.elapsed().as_micros());
        }
        samples_us.sort_unstable();
        let p50_us = samples_us[samples_us.len() / 2];
        let p95_us = samples_us[(samples_us.len() * 95) / 100];
        println!(
            "PERF native discord snapshot_hydrate={:.3}ms first_view={:.3}ms warm_view_p50={:.3}ms warm_view_p95={:.3}ms",
            hydrate_us as f64 / 1000.0,
            first_view_us as f64 / 1000.0,
            p50_us as f64 / 1000.0,
            p95_us as f64 / 1000.0,
        );
        assert!(hydrate_us < 100_000);
        assert!(first_view_us < 100_000);

        let _ = std::fs::remove_dir_all(dir.parent().unwrap());
    }
}
