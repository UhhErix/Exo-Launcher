use crate::discord_chat_state::{ChatDrawer, DiscordChatState};
use crate::discord_voice_state::DiscordVoiceUiState;
use crate::state::{AppState, HostStatus};
use exo_rpc::{
    ActionResult, DiscordAccountItem, DiscordAccountsReply, DiscordChangedEvent, DiscordChannel,
    DiscordChannels, DiscordDm, DiscordDmMember, DiscordFriend, DiscordFriendActionReply,
    DiscordFriendsReply, DiscordGuild, DiscordHome, DiscordMe, DiscordMember, DiscordMemberItem,
    DiscordMemberList, DiscordPresenceEvent, DiscordRailItem, DiscordRich, DiscordSignInStep,
    DiscordStateEvent, DiscordStatus, DiscordUnreadChannel, DiscordUnreadEvent,
    DiscordUnreadsReply, DiscordVoice, DiscordVoiceMember, DiscordVoiceReply,
    METHOD_DISCORD_ACCOUNTS, METHOD_DISCORD_CHANNELS, METHOD_DISCORD_FRIEND,
    METHOD_DISCORD_FRIENDS, METHOD_DISCORD_HOME, METHOD_DISCORD_MARK_READ, METHOD_DISCORD_MEMBERS,
    METHOD_DISCORD_SET_STATUS, METHOD_DISCORD_SIGN_IN, METHOD_DISCORD_SIGN_OUT,
    METHOD_DISCORD_STATUS, METHOD_DISCORD_UNREADS, METHOD_DISCORD_VIEW, METHOD_DISCORD_VOICE,
};
use rune::TextEdit;
use serde_json::json;
use std::collections::{HashMap, HashSet};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DiscordPlace {
    Home,
    Messages,
    Servers,
}

impl DiscordPlace {
    pub const ALL: [Self; 3] = [Self::Home, Self::Messages, Self::Servers];

    pub fn key(self) -> &'static str {
        match self {
            Self::Home => "dc-place-home",
            Self::Messages => "dc-place-messages",
            Self::Servers => "dc-place-servers",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Home => "Home",
            Self::Messages => "Messages",
            Self::Servers => "Servers",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DiscordHomeTab {
    Now,
    Friends,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DiscordFriendsTab {
    Online,
    All,
    Pending,
    Blocked,
}

pub struct DiscordState {
    pub status: DiscordStatus,
    pub signin: Option<DiscordSignInStep>,
    pub accounts: Vec<DiscordAccountItem>,
    pub home: DiscordHome,
    pub friends: Vec<DiscordFriend>,
    pub unreads: Vec<DiscordUnreadChannel>,
    pub voice: DiscordVoice,
    pub speaking: HashSet<String>,
    pub place: DiscordPlace,
    pub home_tab: DiscordHomeTab,
    pub friends_tab: DiscordFriendsTab,
    pub dm_search_open: bool,
    pub active_dm_id: Option<String>,
    pub active_guild_id: Option<String>,
    pub active_channel_id: Option<String>,
    pub channels_by_guild: HashMap<String, DiscordChannels>,
    pub members_by_guild: HashMap<String, DiscordMemberList>,
    pub collapsed_categories: HashSet<String>,
    pub collapsed_folders: HashSet<String>,
    pub show_members: bool,
    pub status_menu_open: bool,
    pub accounts_menu_open: bool,
    pub add_friend_open: bool,
    pub search_query: TextEdit,
    pub add_friend_input: TextEdit,
    pub revealed_spoilers: HashSet<String>,
    pub chat: DiscordChatState,
    pub voice_ui: DiscordVoiceUiState,
    pub loaded: bool,
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
            unreads: Vec::new(),
            voice: DiscordVoice {
                state: "off".into(),
                ..Default::default()
            },
            speaking: HashSet::new(),
            place: DiscordPlace::Home,
            home_tab: DiscordHomeTab::Friends,
            friends_tab: DiscordFriendsTab::Online,
            dm_search_open: false,
            active_dm_id: None,
            active_guild_id: None,
            active_channel_id: None,
            channels_by_guild: HashMap::new(),
            members_by_guild: HashMap::new(),
            collapsed_categories: HashSet::new(),
            collapsed_folders: HashSet::new(),
            show_members: true,
            status_menu_open: false,
            accounts_menu_open: false,
            add_friend_open: false,
            search_query: TextEdit::default(),
            add_friend_input: TextEdit::default(),
            revealed_spoilers: HashSet::new(),
            chat: DiscordChatState::empty(),
            voice_ui: DiscordVoiceUiState::empty(),
            loaded: false,
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
            }),
            dms: vec![
                DiscordDm {
                    id: "dm-ada".into(),
                    name: "Ada".into(),
                    avatar: None,
                    last_at: Some(1_760_000_000_000),
                    line: Some("Queueing Premier in 5?".into()),
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
        self.home.dms.iter().filter(|d| d.unread && !d.muted).count()
    }

    pub fn total_dm_mentions(&self) -> i32 {
        self.home.dms.iter().map(|d| d.mentions).sum()
    }

    pub fn total_guild_mentions(&self) -> i32 {
        self.home.guilds.iter().map(|g| g.mentions).sum()
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
    pub fn load_discord_room(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        self.announce_hardware_video_decode();
        #[cfg(windows)]
        {
            self.call_host_async(
                METHOD_DISCORD_STATUS,
                json!({ "connect": true }),
                |s, res: Result<DiscordStatus, String>| {
                    if let Ok(st) = res {
                        let ready = st.state == "ready";
                        s.discord.status = st;
                        s.discord.loaded = true;
                        if ready {
                            s.refresh_discord_home();
                        }
                    }
                },
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
        #[cfg(windows)]
        {
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
                },
            );
            self.call_host_async(
                METHOD_DISCORD_FRIENDS,
                json!({}),
                |s, res: Result<DiscordFriendsReply, String>| {
                    if let Ok(rep) = res {
                        s.discord.friends = rep.friends;
                    }
                },
            );
            self.call_host_async(
                METHOD_DISCORD_UNREADS,
                json!({}),
                |s, res: Result<DiscordUnreadsReply, String>| {
                    if let Ok(rep) = res {
                        s.discord.unreads = rep.channels;
                    }
                },
            );
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
                url: Some(
                    "https://discord.com/ra/ExoRuneDemoFingerprint0123456789ABCDEFGH".into(),
                ),
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
        if self.status != HostStatus::Connected {
            self.discord.status.state = "signedOut".into();
            self.discord.status.signed_in = false;
            self.discord.home = DiscordHome::default();
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SIGN_OUT,
            json!({}),
            |s, _res: Result<ActionResult, String>| {
                s.discord.status.state = "signedOut".into();
                s.discord.status.signed_in = false;
                s.discord.home = DiscordHome::default();
                s.discord.friends.clear();
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
        self.discord.status_menu_open = false;
        if let Some(ref mut me) = self.discord.home.me {
            me.status = Some(status.to_string());
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SET_STATUS,
            json!({ "status": status }),
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

    pub fn open_discord_place(&mut self, place: DiscordPlace) {
        self.discord.place = place;
        self.discord.status_menu_open = false;
        self.discord.accounts_menu_open = false;
        self.discord.chat.drawer = ChatDrawer::None;
        if place == DiscordPlace::Messages {
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
        self.discord.place = DiscordPlace::Messages;
        self.discord.active_dm_id = Some(dm_id.to_string());
        self.discord.chat.reply_to = None;
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

    pub fn close_discord_guild(&mut self) {
        self.discord.active_guild_id = None;
        self.discord.active_channel_id = None;
        self.discord.place = DiscordPlace::Servers;
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
        self.discord.active_channel_id = Some(channel_id.to_string());
        self.discord.chat.reply_to = None;
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

    pub fn mark_discord_guild_read(&mut self, guild_id: &str) {
        if let Some(g) = self.discord.home.guilds.iter_mut().find(|g| g.id == guild_id) {
            g.unread = false;
            g.mentions = 0;
        }
        if let Some(chans) = self.discord.channels_by_guild.get_mut(guild_id) {
            for c in &mut chans.channels {
                c.unread = false;
                c.mentions = 0;
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
                        s.notice = rep.message.or_else(|| Some("Could not send request".into()));
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
        if !self.discord.collapsed_folders.remove(folder_id) {
            self.discord.collapsed_folders.insert(folder_id.to_string());
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
        }
        if became_ready {
            self.refresh_discord_home();
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

    pub fn handle_discord_changed(&mut self, ev: DiscordChangedEvent) {
        if let Some(ref gid) = ev.guild_id {
            if let Some(cnt) = ev.in_voice
                && let Some(g) = self.discord.home.guilds.iter_mut().find(|g| g.id == *gid)
            {
                g.in_voice = cnt;
            }
            if self.discord.active_guild_id.as_deref() == Some(gid.as_str())
                || self.discord.channels_by_guild.contains_key(gid)
            {
                self.load_discord_guild(gid, false);
            }
        } else {
            self.refresh_discord_home();
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
    }

    pub fn handle_discord_members(&mut self, guild_id: &str) {
        if self.discord.active_guild_id.as_deref() == Some(guild_id) {
            self.refresh_discord_members(guild_id);
        }
    }

    pub fn handle_discord_presence(&mut self, ev: DiscordPresenceEvent) {
        if let Some(ref mut me) = self.discord.home.me
            && me.id == ev.user_id
        {
            me.status = Some(ev.status.clone());
        }
        for dm in &mut self.discord.home.dms {
            if dm.user_id.as_deref() == Some(ev.user_id.as_str()) {
                dm.status = Some(ev.status.clone());
                if ev.activity.is_some() {
                    dm.subtitle = ev.activity.clone();
                }
            }
        }
        for f in &mut self.discord.friends {
            if f.id == ev.user_id {
                f.status = ev.status.clone();
                f.activity = ev.activity.clone();
                f.activity_type = ev.activity_type;
                f.rich = ev.rich.clone();
                f.prep = ev.prep.clone();
            }
        }
    }

    pub fn handle_discord_unread(&mut self, ev: DiscordUnreadEvent) {
        if let Some(ref gid) = ev.guild_id {
            if let Some(g) = self.discord.home.guilds.iter_mut().find(|g| g.id == *gid) {
                g.unread = ev.guild_unread;
                g.mentions = ev.guild_mentions;
            }
            if let Some(chans) = self.discord.channels_by_guild.get_mut(gid)
                && let Some(c) = chans.channels.iter_mut().find(|c| c.id == ev.channel_id)
            {
                c.unread = ev.unread;
                c.mentions = ev.mentions;
            }
        } else if let Some(dm) = self
            .discord
            .home
            .dms
            .iter_mut()
            .find(|d| d.id == ev.channel_id)
        {
            dm.unread = ev.unread;
            dm.mentions = ev.mentions;
        }
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
}
