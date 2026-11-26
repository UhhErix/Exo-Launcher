use crate::discord::{avatar_box, status_color, status_label};
use crate::discord_state::DiscordSettingsPage;
use crate::state::{AppState, HostStatus};
use exo_kit::{
    BG, ExoGlass, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    RADIUS_SMALL, SPRING_QUICK, TRANSPARENT, apply_glass, bad, fg_faint, fg_muted, filter_pill,
    glass_button, glass_still_control_fill, good, hairline, hover_fill, icon_check, icon_close,
    icon_mic, icon_mic_off, icon_settings, icon_volume, modal_card_transition, pressed_fill,
    primary_button, scrim_fill, selected_fill, type_body, type_control, type_label, type_section,
    type_small, type_title, warn,
};
use exo_rpc::{
    ActionResult, DiscordAccount, DiscordAccountConnection, DiscordAccountReply,
    DiscordAccountSettings, DiscordAuthorizedApp, DiscordClientSettings, DiscordColorsReply,
    DiscordCustomStatus, DiscordFontReply, DiscordKeybind, DiscordMicLevel, DiscordMicMonitorReply,
    DiscordServerTagOption, DiscordSession, DiscordUserSettingsReply, METHOD_DISCORD_ACCOUNT,
    METHOD_DISCORD_CLIENT_SETTINGS, METHOD_DISCORD_COLORS, METHOD_DISCORD_FONT,
    METHOD_DISCORD_MIC_MONITOR, METHOD_DISCORD_MIC_TEST, METHOD_DISCORD_MY_VOICE,
    METHOD_DISCORD_SET_STATUS, METHOD_DISCORD_SOUND, METHOD_DISCORD_USER_SETTINGS,
};
use rune::{Cursor, El, ScrollSpec, TextEdit, col, edit_access, el, row, text, text_input};
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};

pub const FONT_SIZES: [i32; 7] = [12, 14, 15, 16, 18, 20, 24];
pub const ZOOM_LEVELS: [i32; 11] = [50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200];
pub const VOICE_LUFS_MIN: i32 = -26;
pub const VOICE_LUFS_MAX: i32 = -14;

pub fn clamp_voice_target_lufs(lufs: i32) -> i32 {
    lufs.clamp(VOICE_LUFS_MIN, VOICE_LUFS_MAX)
}

pub fn format_voice_lufs(lufs: i32) -> String {
    format!("{} LUFS", clamp_voice_target_lufs(lufs))
}

pub const KEYBIND_ACTIONS: [(&str, &str); 7] = [
    ("PUSH_TO_TALK", "Push to talk"),
    ("PUSH_TO_MUTE", "Push to mute"),
    ("TOGGLE_MUTE", "Toggle mute"),
    ("TOGGLE_DEAFEN", "Toggle deafen"),
    ("TOGGLE_VOICE_MODE", "Toggle voice activity mode"),
    ("TOGGLE_STREAMER_MODE", "Toggle Streamer Mode"),
    ("DISCONNECT_FROM_VOICE", "Disconnect from voice"),
];

pub const CALL_SOUNDS: [(&str, &str); 13] = [
    ("call_ringing", "Incoming ring"),
    ("call_calling", "Outgoing ring"),
    ("mute", "Mute"),
    ("unmute", "Unmute"),
    ("deafen", "Deafen"),
    ("undeafen", "Undeafen"),
    ("camera_on", "Camera on"),
    ("camera_off", "Camera off"),
    ("ptt_start", "Push to talk on"),
    ("ptt_stop", "Push to talk off"),
    ("user_join", "Someone joins"),
    ("user_leave", "Someone leaves"),
    ("disconnect", "Disconnected"),
];

pub const CLEAR_AFTER_OPTIONS: [(Option<i64>, &str); 5] = [
    (Some(86_400_000), "24 hours"),
    (Some(14_400_000), "4 hours"),
    (Some(3_600_000), "1 hour"),
    (Some(1_800_000), "30 minutes"),
    (None, "Don't clear"),
];

pub fn key_name(vk: u32) -> String {
    if (48..=57).contains(&vk) || (65..=90).contains(&vk) {
        return ((vk as u8) as char).to_string();
    }
    if (96..=105).contains(&vk) {
        return format!("Numpad {}", vk - 96);
    }
    if (112..=135).contains(&vk) {
        return format!("F{}", vk - 111);
    }
    match vk {
        4 => "Mouse 3",
        5 => "Mouse 4",
        6 => "Mouse 5",
        8 => "Backspace",
        9 => "Tab",
        13 => "Enter",
        16 => "Shift",
        17 => "Ctrl",
        18 => "Alt",
        19 => "Pause",
        20 => "Caps lock",
        27 => "Esc",
        32 => "Space",
        33 => "Page up",
        34 => "Page down",
        35 => "End",
        36 => "Home",
        37 => "Left",
        38 => "Up",
        39 => "Right",
        40 => "Down",
        45 => "Insert",
        46 => "Delete",
        91 | 92 => "Win",
        106 => "Numpad *",
        107 => "Numpad +",
        109 => "Numpad -",
        110 => "Numpad .",
        111 => "Numpad /",
        144 => "Num lock",
        145 => "Scroll lock",
        186 => ";",
        187 => "=",
        188 => ",",
        189 => "-",
        190 => ".",
        191 => "/",
        192 => "`",
        219 => "[",
        220 => "\\",
        221 => "]",
        222 => "'",
        _ => return format!("Key {vk}"),
    }
    .to_string()
}

pub fn keybind_label(keys: &[u32]) -> String {
    if keys.is_empty() {
        return "No keys".into();
    }
    let order = |k: u32| match k {
        17 => 0,
        16 => 1,
        18 => 2,
        91 | 92 => 3,
        _ => 4,
    };
    let mut sorted = keys.to_vec();
    sorted.sort_by_key(|&k| order(k));
    sorted
        .into_iter()
        .map(key_name)
        .collect::<Vec<_>>()
        .join(" + ")
}

pub fn discord_font_family(id: i32) -> Option<&'static str> {
    match id {
        3 => Some("sakura"),
        4 => Some("jellybean"),
        6 => Some("modern"),
        7 => Some("medieval"),
        8 => Some("bit"),
        10 => Some("vampyre"),
        12 => Some("tempo"),
        13 => Some("monkey"),
        14 => Some("mainframe"),
        15 => Some("headbang"),
        16 => Some("journal"),
        _ => None,
    }
}

pub fn speech_time(seconds: f64) -> String {
    let s = seconds.max(0.0).floor() as u64;
    if s < 60 {
        format!("{s} s")
    } else if s.is_multiple_of(60) {
        format!("{} min", s / 60)
    } else {
        format!("{} min {} s", s / 60, s % 60)
    }
}

pub fn mic_advice(level: &DiscordMicLevel) -> Option<&'static str> {
    let heard = level.heard.as_ref()?;
    heard.speech_db?;
    if heard.clipping {
        Some("Clipping: turn its gain down")
    } else if heard.gated {
        Some("Gated before Exo: turn that gate off")
    } else if heard.quiet {
        Some("Quiet: turn its gain up")
    } else {
        None
    }
}

pub fn suppression_detail(stage: &str) -> Option<&'static str> {
    match stage {
        "exo-v2" => Some("Exo AI Gaming active (low CPU)"),
        "exo-hq" => Some("Exo AI 48 kHz Studio active"),
        "voxlock" => Some("VoxLock voice isolation active"),
        "on" => Some("DeepFilterNet 3 fallback active"),
        "light" => Some("Low-load mode active"),
        "missing" => Some("Not available"),
        "mic" => Some("Your microphone already does this"),
        "windows" => Some("Windows is doing this"),
        _ => None,
    }
}

pub fn normalize_voice_tier(tier: &str) -> &'static str {
    match tier {
        "cpu" => "cpu",
        "hq" | "studio" | "48k" => "hq",
        _ => "auto",
    }
}

pub fn db_fill_ratio(db: f64) -> f32 {
    ((db + 100.0).clamp(0.0, 100.0) / 100.0) as f32
}

pub fn to_discord_volume_slider(volume: f64) -> f64 {
    if volume <= 0.0 {
        0.0
    } else if volume < 100.0 {
        (volume / 100.0).powf(1.0 / 2.8) * 100.0
    } else {
        (20.0 * (volume / 100.0).log10() / 6.0 + 1.0) * 100.0
    }
}

pub fn from_discord_volume_slider(at: f64) -> f64 {
    if at <= 0.0 {
        0.0
    } else if (at - 100.0).abs() < 1.0 {
        100.0
    } else if at < 100.0 {
        (at / 100.0).powf(2.8) * 100.0
    } else {
        10.0_f64.powf((at / 100.0 - 1.0) * 6.0 / 20.0) * 100.0
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeybindRecordTarget {
    PushToTalk,
    Existing(usize),
    New,
}

pub struct DiscordSettingsState {
    pub account: Option<DiscordAccount>,
    pub account_loading: bool,
    pub account_error: Option<String>,
    pub client: DiscordClientSettings,
    pub client_loaded: bool,
    pub client_error: Option<String>,
    pub user: DiscordAccountSettings,
    pub user_loaded: bool,
    pub sessions: Vec<DiscordSession>,
    pub sessions_loading: bool,
    pub devices_view: bool,
    pub apps: Vec<DiscordAuthorizedApp>,
    pub connections: Vec<DiscordAccountConnection>,
    pub apps_loaded: bool,
    pub server_tags: Vec<DiscordServerTagOption>,
    pub active_tag_guild: Option<String>,
    pub profile_guild_id: Option<String>,
    pub profile_name_input: TextEdit,
    pub profile_pronouns_input: TextEdit,
    pub profile_bio_input: TextEdit,
    pub profile_nick_input: TextEdit,
    pub profile_dirty: bool,
    pub custom_status_clear_ms: Option<i64>,
    pub mic_level: Option<DiscordMicLevel>,
    pub mic_test_active: bool,
    pub mic_monitor_on: bool,
    pub mic_monitor_speakers_warn: bool,
    pub recording_keybind: Option<KeybindRecordTarget>,
    pub new_keybind_action: Option<String>,
    pub chat_font_size: i32,
    pub chat_zoom: i32,
    pub role_style: String,
    pub streamer_mode: bool,
    pub reduce_motion: bool,
    pub loaded_fonts: HashSet<String>,
    pub guild_colors: HashMap<String, DiscordColorsReply>,
    pub logout_confirm: bool,
    pub error_note: Option<String>,
}

impl DiscordSettingsState {
    pub fn empty() -> Self {
        Self {
            account: None,
            account_loading: false,
            account_error: None,
            client: DiscordClientSettings::default(),
            client_loaded: false,
            client_error: None,
            user: DiscordAccountSettings::default(),
            user_loaded: false,
            sessions: Vec::new(),
            sessions_loading: false,
            devices_view: false,
            apps: Vec::new(),
            connections: Vec::new(),
            apps_loaded: false,
            server_tags: Vec::new(),
            active_tag_guild: None,
            profile_guild_id: None,
            profile_name_input: TextEdit::default(),
            profile_pronouns_input: TextEdit::default(),
            profile_bio_input: TextEdit::default(),
            profile_nick_input: TextEdit::default(),
            profile_dirty: false,
            custom_status_clear_ms: Some(86_400_000),
            mic_level: None,
            mic_test_active: false,
            mic_monitor_on: false,
            mic_monitor_speakers_warn: false,
            recording_keybind: None,
            new_keybind_action: None,
            chat_font_size: 16,
            chat_zoom: 100,
            role_style: "username".into(),
            streamer_mode: false,
            reduce_motion: false,
            loaded_fonts: HashSet::new(),
            guild_colors: HashMap::new(),
            logout_confirm: false,
            error_note: None,
        }
    }

    pub fn streamer_hides(&self) -> bool {
        self.streamer_mode
    }

    pub fn apply_user_settings_patch(&mut self, patch: &Value) {
        let Ok(mut current) = serde_json::to_value(&self.user) else {
            return;
        };
        if let (Some(dst), Some(src)) = (current.as_object_mut(), patch.as_object()) {
            for (k, v) in src {
                dst.insert(k.clone(), v.clone());
            }
            if let Ok(updated) = serde_json::from_value::<DiscordAccountSettings>(current) {
                self.user = updated;
                self.user_loaded = true;
            }
        }
    }

    pub fn guild_member_color(&self, guild_id: &str, user_id: &str) -> Option<&str> {
        self.guild_colors
            .get(guild_id)
            .and_then(|c| c.colors.get(user_id))
            .and_then(|v| v.as_deref())
    }
}

impl AppState {
    pub fn load_discord_client_settings(&mut self) {
        if self.status != HostStatus::Connected {
            self.discord.settings_ext.client_loaded = true;
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CLIENT_SETTINGS,
            json!({}),
            |s, res: Result<DiscordClientSettings, String>| match res {
                Ok(cfg) => {
                    s.discord.settings_ext.client = cfg;
                    s.discord.settings_ext.client_loaded = true;
                    s.discord.settings_ext.client_error = None;
                    s.discord.voice_ui.my_voice_learned = s
                        .discord
                        .settings_ext
                        .client
                        .my_voice_learned
                        .unwrap_or(false);
                }
                Err(e) => {
                    s.discord.settings_ext.client_error = Some(e);
                }
            },
        );
    }

    pub fn save_discord_client_settings(&mut self, patch: Value) {
        if let Ok(mut cur) = serde_json::to_value(&self.discord.settings_ext.client)
            && let (Some(dst), Some(src)) = (cur.as_object_mut(), patch.as_object())
        {
            for (k, v) in src {
                dst.insert(k.clone(), v.clone());
            }
            if let Ok(next) = serde_json::from_value::<DiscordClientSettings>(cur) {
                self.discord.settings_ext.client = next;
            }
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CLIENT_SETTINGS,
            patch,
            |s, res: Result<DiscordClientSettings, String>| match res {
                Ok(cfg) => {
                    s.discord.settings_ext.client = cfg;
                    s.discord.settings_ext.client_loaded = true;
                    s.discord.settings_ext.client_error = None;
                }
                Err(e) => {
                    s.discord.settings_ext.error_note = Some(e);
                }
            },
        );
    }

    pub fn load_discord_user_settings(&mut self) {
        if self.status != HostStatus::Connected {
            self.discord.settings_ext.user_loaded = true;
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_USER_SETTINGS,
            json!({}),
            |s, res: Result<DiscordUserSettingsReply, String>| {
                if let Ok(rep) = res
                    && rep.ok
                    && let Some(vals) = rep.values
                {
                    s.discord.chat_compact = vals.message_display_compact;
                    s.discord.show_embeds = vals.render_embeds;
                    s.discord.developer_mode = vals.developer_mode;
                    s.discord.custom_status = vals
                        .custom_status
                        .as_ref()
                        .map(|c| c.text.clone())
                        .filter(|t| !t.is_empty());
                    s.discord.settings_ext.user = vals;
                    s.discord.settings_ext.user_loaded = true;
                }
            },
        );
    }

    pub fn save_discord_user_settings(&mut self, changes: Value) {
        self.discord
            .settings_ext
            .apply_user_settings_patch(&changes);
        self.discord.chat_compact = self.discord.settings_ext.user.message_display_compact;
        self.discord.show_embeds = self.discord.settings_ext.user.render_embeds;
        self.discord.developer_mode = self.discord.settings_ext.user.developer_mode;
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_USER_SETTINGS,
            changes,
            |s, res: Result<DiscordUserSettingsReply, String>| {
                if let Ok(rep) = res {
                    if rep.ok {
                        if let Some(vals) = rep.values {
                            s.discord.chat_compact = vals.message_display_compact;
                            s.discord.show_embeds = vals.render_embeds;
                            s.discord.developer_mode = vals.developer_mode;
                            s.discord.settings_ext.user = vals;
                        }
                        s.discord.settings_ext.error_note = None;
                    } else if let Some(msg) = rep.message {
                        s.discord.settings_ext.error_note = Some(msg);
                    }
                }
            },
        );
    }

    pub fn load_discord_account(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.settings_ext.account_loading = true;
        #[cfg(windows)]
        {
            self.call_host_async(
                METHOD_DISCORD_ACCOUNT,
                json!({ "action": "me" }),
                |s, res: Result<DiscordAccountReply, String>| {
                    s.discord.settings_ext.account_loading = false;
                    match res {
                        Ok(rep) if rep.ok => {
                            if let Some(me) = rep.me {
                                if !s.discord.settings_ext.profile_dirty {
                                    s.discord.settings_ext.profile_name_input = TextEdit::new(
                                        me.global_name.as_deref().unwrap_or_default(),
                                    );
                                    s.discord.settings_ext.profile_pronouns_input =
                                        TextEdit::new(&me.pronouns);
                                    s.discord.settings_ext.profile_bio_input =
                                        TextEdit::new(&me.bio);
                                }
                                s.discord.settings_ext.account = Some(me);
                                s.discord.settings_ext.account_error = None;
                            }
                        }
                        Ok(rep) => {
                            s.discord.settings_ext.account_error = rep
                                .message
                                .or_else(|| Some("Discord didn't send your account.".into()));
                        }
                        Err(e) => {
                            s.discord.settings_ext.account_error = Some(e);
                        }
                    }
                },
            );
            self.call_host_async(
                METHOD_DISCORD_ACCOUNT,
                json!({ "action": "tags" }),
                |s, res: Result<DiscordAccountReply, String>| {
                    if let Ok(rep) = res
                        && rep.ok
                    {
                        s.discord.settings_ext.active_tag_guild = rep.mine;
                        s.discord.settings_ext.server_tags = rep.servers;
                    }
                },
            );
        }
    }

    pub fn load_discord_sessions(&mut self) {
        self.discord.settings_ext.devices_view = true;
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.settings_ext.sessions_loading = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ACCOUNT,
            json!({ "action": "sessions" }),
            |s, res: Result<DiscordAccountReply, String>| {
                s.discord.settings_ext.sessions_loading = false;
                if let Ok(rep) = res
                    && rep.ok
                    && let Some(data) = rep.data
                    && let Ok(list) = serde_json::from_value::<Vec<DiscordSession>>(data)
                {
                    s.discord.settings_ext.sessions = list;
                }
            },
        );
    }

    pub fn logout_discord_sessions(&mut self, hashes: Vec<String>) {
        if hashes.is_empty() {
            return;
        }
        self.discord
            .settings_ext
            .sessions
            .retain(|sess| !sess.id_hash.as_ref().is_some_and(|h| hashes.contains(h)));
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ACCOUNT,
            json!({ "action": "logoutSessions", "idHashes": hashes }),
            |s, res: Result<DiscordAccountReply, String>| {
                if let Ok(rep) = res {
                    if rep.ok {
                        s.load_discord_sessions();
                    } else if let Some(m) = rep.message {
                        s.discord.settings_ext.error_note = Some(m);
                    }
                }
            },
        );
    }

    pub fn load_discord_connected_apps(&mut self) {
        if self.status != HostStatus::Connected {
            self.discord.settings_ext.apps_loaded = true;
            return;
        }
        #[cfg(windows)]
        {
            self.call_host_async(
                METHOD_DISCORD_ACCOUNT,
                json!({ "action": "connections" }),
                |s, res: Result<DiscordAccountReply, String>| {
                    s.discord.settings_ext.apps_loaded = true;
                    if let Ok(rep) = res
                        && rep.ok
                        && let Some(data) = rep.data
                        && let Ok(list) =
                            serde_json::from_value::<Vec<DiscordAccountConnection>>(data)
                    {
                        s.discord.settings_ext.connections = list;
                    }
                },
            );
            self.call_host_async(
                METHOD_DISCORD_ACCOUNT,
                json!({ "action": "apps" }),
                |s, res: Result<DiscordAccountReply, String>| {
                    s.discord.settings_ext.apps_loaded = true;
                    if let Ok(rep) = res
                        && rep.ok
                        && let Some(data) = rep.data
                        && let Ok(list) = serde_json::from_value::<Vec<DiscordAuthorizedApp>>(data)
                    {
                        s.discord.settings_ext.apps = list;
                    }
                },
            );
        }
    }

    pub fn deauthorize_discord_app(&mut self, id: &str) {
        self.discord.settings_ext.apps.retain(|a| a.id != id);
        if self.status != HostStatus::Connected {
            return;
        }
        let id = id.to_string();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ACCOUNT,
            json!({ "action": "deauthorize", "id": id }),
            |s, res: Result<DiscordAccountReply, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                {
                    s.discord.settings_ext.error_note = rep.message;
                }
            },
        );
    }

    pub fn save_discord_profile(&mut self) {
        let name = self
            .discord
            .settings_ext
            .profile_name_input
            .text
            .trim()
            .to_string();
        let pronouns = self
            .discord
            .settings_ext
            .profile_pronouns_input
            .text
            .trim()
            .to_string();
        let bio = self
            .discord
            .settings_ext
            .profile_bio_input
            .text
            .trim()
            .to_string();
        let gid = self.discord.settings_ext.profile_guild_id.clone();
        self.discord.settings_ext.profile_dirty = false;
        if self.status != HostStatus::Connected {
            self.notice = Some("Profile saved".into());
            return;
        }
        #[cfg(windows)]
        {
            if let Some(guild_id) = gid {
                let nick = self
                    .discord
                    .settings_ext
                    .profile_nick_input
                    .text
                    .trim()
                    .to_string();
                self.call_host_async(
                    METHOD_DISCORD_ACCOUNT,
                    json!({
                        "action": "profile",
                        "guildId": guild_id,
                        "globalName": nick,
                        "bio": bio,
                        "pronouns": pronouns,
                    }),
                    |s, res: Result<DiscordAccountReply, String>| {
                        if let Ok(rep) = res {
                            if rep.ok {
                                s.notice = Some("Server profile saved".into());
                            } else {
                                s.discord.settings_ext.error_note = rep.message;
                            }
                        }
                    },
                );
            } else {
                self.call_host_async(
                    METHOD_DISCORD_ACCOUNT,
                    json!({ "action": "name", "globalName": name }),
                    |_s, _res: Result<DiscordAccountReply, String>| {},
                );
                self.call_host_async(
                    METHOD_DISCORD_ACCOUNT,
                    json!({
                        "action": "profile",
                        "bio": bio,
                        "pronouns": pronouns,
                    }),
                    |s, res: Result<DiscordAccountReply, String>| {
                        if let Ok(rep) = res {
                            if rep.ok {
                                s.notice = Some("Profile saved".into());
                                s.load_discord_account();
                            } else {
                                s.discord.settings_ext.error_note = rep.message;
                            }
                        }
                    },
                );
            }
        }
    }

    pub fn set_discord_server_tag(&mut self, guild_id: Option<String>) {
        self.discord.settings_ext.active_tag_guild = guild_id.clone();
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_ACCOUNT,
            json!({ "action": "setTag", "guildId": guild_id }),
            |s, res: Result<DiscordAccountReply, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                {
                    s.discord.settings_ext.error_note = rep.message;
                }
            },
        );
    }

    pub fn set_discord_mic_test(&mut self, on: bool) {
        if self.discord.settings_ext.mic_test_active == on {
            return;
        }
        self.discord.settings_ext.mic_test_active = on;
        if !on {
            self.discord.settings_ext.mic_level = None;
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_MIC_TEST,
            json!({ "on": on }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn set_discord_mic_monitor(&mut self, on: bool) {
        self.discord.settings_ext.mic_monitor_on = on;
        if !on {
            self.discord.settings_ext.mic_monitor_speakers_warn = false;
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_MIC_MONITOR,
            json!({ "on": on }),
            |s, res: Result<DiscordMicMonitorReply, String>| {
                if let Ok(rep) = res {
                    s.discord.settings_ext.mic_monitor_on = rep.on;
                    s.discord.settings_ext.mic_monitor_speakers_warn = rep.on && !rep.on_head;
                }
            },
        );
    }

    pub fn forget_discord_my_voice(&mut self) {
        self.discord.voice_ui.my_voice_learned = false;
        self.discord.settings_ext.client.my_voice_learned = Some(false);
        self.discord.settings_ext.client.my_voice_seconds = Some(0.0);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_MY_VOICE,
            json!({ "action": "forget" }),
            |s, _res: Result<Value, String>| {
                s.load_discord_client_settings();
            },
        );
    }

    pub fn play_discord_ui_sound(&mut self, name: &str, preview: bool) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SOUND,
            json!({ "name": name, "preview": preview }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn set_discord_streamer_mode(&mut self, on: bool) {
        self.discord.settings_ext.streamer_mode = on;
        self.save_discord_client_settings(json!({
            "streamerQuiet": on,
            "streamerSilent": on,
        }));
    }

    pub fn load_discord_guild_colors(&mut self, guild_id: &str) {
        if self.status != HostStatus::Connected {
            return;
        }
        let gid = guild_id.to_string();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_COLORS,
            json!({ "guildId": gid }),
            move |s, res: Result<DiscordColorsReply, String>| {
                if let Ok(rep) = res {
                    s.discord.settings_ext.guild_colors.insert(gid, rep);
                }
            },
        );
    }

    pub fn load_discord_font(&mut self, family: &str) {
        if self.discord.settings_ext.loaded_fonts.contains(family) {
            return;
        }
        self.discord
            .settings_ext
            .loaded_fonts
            .insert(family.to_string());
        if self.status != HostStatus::Connected {
            return;
        }
        let fam = family.to_string();
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_FONT,
            json!({ "family": fam }),
            |_s, _res: Result<DiscordFontReply, String>| {},
        );
    }

    pub fn handle_discord_mic_level(&mut self, level: DiscordMicLevel) -> bool {
        self.discord.settings_ext.mic_level = Some(level);
        self.discord.settings_open && self.discord.settings_page == DiscordSettingsPage::Voice
    }

    pub fn handle_discord_keybind(&mut self, action: &str) -> bool {
        if action == "TOGGLE_STREAMER_MODE" {
            let next = !self.discord.settings_ext.streamer_mode;
            self.set_discord_streamer_mode(next);
            return true;
        }
        false
    }

    pub fn handle_discord_user_settings_event(&mut self, patch: &Value) -> bool {
        self.discord.settings_ext.apply_user_settings_patch(patch);
        self.discord.chat_compact = self.discord.settings_ext.user.message_display_compact;
        self.discord.show_embeds = self.discord.settings_ext.user.render_embeds;
        self.discord.developer_mode = self.discord.settings_ext.user.developer_mode;
        true
    }

    pub fn handle_discord_colors_event(&mut self, guild_id: &str) -> bool {
        if self.discord.active_guild_id.as_deref() == Some(guild_id)
            || self.discord.channels_by_guild.contains_key(guild_id)
        {
            self.load_discord_guild_colors(guild_id);
            return true;
        }
        false
    }

    pub fn record_discord_keybind_vk(&mut self, vk: u32) -> bool {
        let Some(target) = self.discord.settings_ext.recording_keybind.take() else {
            return false;
        };
        if vk == 27 {
            return true;
        }
        let keys = vec![vk];
        let mut binds = self.discord.settings_ext.client.keybinds.clone();
        match target {
            KeybindRecordTarget::PushToTalk => {
                binds.retain(|b| b.action != "PUSH_TO_TALK");
                binds.insert(
                    0,
                    DiscordKeybind {
                        action: "PUSH_TO_TALK".into(),
                        keys,
                    },
                );
            }
            KeybindRecordTarget::Existing(idx) => {
                if let Some(b) = binds.get_mut(idx) {
                    b.keys = keys;
                }
            }
            KeybindRecordTarget::New => {
                let action = self
                    .discord
                    .settings_ext
                    .new_keybind_action
                    .take()
                    .unwrap_or_else(|| "PUSH_TO_TALK".into());
                binds.push(DiscordKeybind { action, keys });
            }
        }
        self.save_discord_client_settings(json!({ "keybinds": binds }));
        true
    }

    pub fn open_discord_settings_on(
        &mut self,
        page: DiscordSettingsPage,
        guild_profile: Option<String>,
    ) {
        self.discord.settings_open = true;
        self.discord.settings_page = page;
        self.discord.settings_ext.devices_view = false;
        self.discord.settings_ext.profile_guild_id = guild_profile;
        self.discord.settings_ext.error_note = None;
        self.load_discord_audio();
        self.load_discord_client_settings();
        self.load_discord_user_settings();
        self.load_discord_account();
        if page == DiscordSettingsPage::Voice {
            self.set_discord_mic_test(true);
        } else {
            self.set_discord_mic_test(false);
            self.set_discord_mic_monitor(false);
        }
        if page == DiscordSettingsPage::ConnectedApps {
            self.load_discord_connected_apps();
        }
    }

    pub fn select_discord_settings_page(&mut self, page: DiscordSettingsPage) {
        self.discord.settings_page = page;
        self.discord.settings_ext.devices_view = false;
        self.discord.settings_ext.recording_keybind = None;
        self.discord.settings_ext.logout_confirm = false;
        if page == DiscordSettingsPage::Voice {
            self.load_discord_audio();
            self.set_discord_mic_test(true);
        } else {
            self.set_discord_mic_test(false);
            self.set_discord_mic_monitor(false);
        }
        if page == DiscordSettingsPage::ConnectedApps {
            self.load_discord_connected_apps();
        }
    }

    pub fn close_discord_settings_clean(&mut self) {
        self.discord.settings_open = false;
        self.discord.settings_ext.recording_keybind = None;
        self.discord.settings_ext.devices_view = false;
        self.discord.settings_ext.logout_confirm = false;
        self.set_discord_mic_test(false);
        self.set_discord_mic_monitor(false);
    }

    pub fn save_discord_custom_status_ext(&mut self, clear: bool) {
        let words = if clear {
            String::new()
        } else {
            self.discord.custom_status_input.text.trim().to_string()
        };
        let next_custom = if words.is_empty() {
            None
        } else {
            let expires_at_ms = self.discord.settings_ext.custom_status_clear_ms.map(|dur| {
                let now = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map(|d| d.as_millis() as i64)
                    .unwrap_or(0);
                now + dur
            });
            Some(DiscordCustomStatus {
                text: words.clone(),
                emoji_id: None,
                emoji_name: None,
                expires_at_ms,
            })
        };
        self.discord.custom_status = if words.is_empty() { None } else { Some(words) };
        self.discord.custom_status_open = false;
        self.save_discord_user_settings(json!({ "customStatus": next_custom }));
        if self.status == HostStatus::Connected {
            let status = self
                .discord
                .home
                .me
                .as_ref()
                .and_then(|m| m.status.clone())
                .unwrap_or_else(|| "online".into());
            #[cfg(windows)]
            self.call_host_async(
                METHOD_DISCORD_SET_STATUS,
                json!({ "status": status, "customStatus": self.discord.custom_status }),
                |_s, _res: Result<ActionResult, String>| {},
            );
        }
    }
}

fn section_head(title: &str) -> El {
    text(title)
        .font(type_label())
        .color(fg_muted())
        .pt(10.0)
        .pb(4.0)
}

fn toggle_row(
    key: impl Into<String>,
    label: &str,
    detail: Option<&str>,
    on: bool,
    on_toggle: impl Fn(&mut AppState) + 'static,
) -> El {
    let mut left = col()
        .gap(2.0)
        .grow(1.0)
        .min_w(0.0)
        .child(text(label).font(type_body()).color(FG));
    if let Some(d) = detail {
        left = left.child(text(d).font(type_label()).color(fg_muted()));
    }
    let pill = el()
        .w(40.0)
        .h(22.0)
        .radius(RADIUS_PILL)
        .fill(if on { FG } else { selected_fill() })
        .border(1.0, if on { FG } else { hairline() })
        .motion(SPRING_QUICK)
        .child(
            el().pin(
                if on { Some(20.0) } else { Some(2.0) },
                Some(2.0),
                None,
                None,
            )
            .size(16.0, 16.0)
            .radius(RADIUS_PILL)
            .fill(if on { PRIMARY_FG } else { fg_muted() })
            .motion(SPRING_QUICK),
        );
    row()
        .key(key)
        .role("switch")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .w_full()
        .min_h(40.0)
        .py(8.0)
        .gap(12.0)
        .center_y()
        .justify_between()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .on_press(on_toggle)
        .child(left)
        .child(pill)
}

fn choice_row<T: Clone + PartialEq + 'static>(
    prefix: &str,
    label: &str,
    detail: Option<&str>,
    value: T,
    choices: Vec<(T, &'static str)>,
    on_change: impl Fn(&mut AppState, T) + Clone + 'static,
) -> El {
    let mut left = col()
        .gap(2.0)
        .grow(1.0)
        .min_w(0.0)
        .child(text(label).font(type_body()).color(FG));
    if let Some(d) = detail {
        left = left.child(text(d).font(type_label()).color(fg_muted()));
    }
    let mut pills = row().gap(6.0).center_y().wrap();
    for (idx, (v, lbl)) in choices.into_iter().enumerate() {
        let active = v == value;
        let cb = on_change.clone();
        let val = v.clone();
        pills = pills.child(
            filter_pill(format!("{prefix}-{idx}"), lbl, active)
                .on_press(move |s: &mut AppState| cb(s, val.clone())),
        );
    }
    row()
        .w_full()
        .min_h(40.0)
        .py(8.0)
        .gap(12.0)
        .center_y()
        .justify_between()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(left)
        .child(pills)
}

fn value_row(label: &str, value: impl Into<String>, trailing: Option<El>) -> El {
    let mut right = row()
        .gap(8.0)
        .center_y()
        .child(text(value.into()).font(type_body()).color(fg_muted()));
    if let Some(t) = trailing {
        right = right.child(t);
    }
    row()
        .w_full()
        .min_h(40.0)
        .py(8.0)
        .gap(12.0)
        .center_y()
        .justify_between()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(text(label).font(type_body()).color(FG))
        .child(right)
}

fn step_row(
    prefix: &str,
    label: &str,
    value_text: String,
    can_dec: bool,
    can_inc: bool,
    on_dec: impl Fn(&mut AppState) + 'static,
    on_inc: impl Fn(&mut AppState) + 'static,
) -> El {
    let dec = el()
        .key(format!("{prefix}-dec"))
        .role("button")
        .label(format!("Decrease {label}"))
        .cursor(Cursor::Pointer)
        .hit()
        .size(28.0, 26.0)
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .border(1.0, hairline())
        .hover(hover_fill())
        .center()
        .on_press(on_dec)
        .child(
            text("−")
                .font(type_control())
                .color(if can_dec { FG } else { fg_faint() }),
        );
    let inc = el()
        .key(format!("{prefix}-inc"))
        .role("button")
        .label(format!("Increase {label}"))
        .cursor(Cursor::Pointer)
        .hit()
        .size(28.0, 26.0)
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .border(1.0, hairline())
        .hover(hover_fill())
        .center()
        .on_press(on_inc)
        .child(
            text("+")
                .font(type_control())
                .color(if can_inc { FG } else { fg_faint() }),
        );
    row()
        .w_full()
        .min_h(40.0)
        .py(8.0)
        .gap(12.0)
        .center_y()
        .justify_between()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(text(label).font(type_body()).color(FG))
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(dec)
                .child(text(value_text).font(type_control()).color(FG).w(64.0))
                .child(inc),
        )
}

fn sheet_action_btn(key: impl Into<String>, label: impl Into<String>) -> El {
    let lbl = label.into();
    row()
        .key(key.into())
        .role("button")
        .label(lbl.clone())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(32.0)
        .px(12.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .border(1.0, hairline())
        .hover(hover_fill())
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .child(text(lbl).font(type_control()).color(FG))
}

fn account_page(s: &AppState) -> El {
    let ext = &s.discord.settings_ext;
    let hides = ext.streamer_hides();
    if ext.devices_view {
        return devices_subpage(s);
    }
    let me_home = s.discord.home.me.as_ref();
    let cur_status = me_home
        .and_then(|m| m.status.as_deref())
        .unwrap_or("online")
        .to_string();
    let display_name = ext
        .account
        .as_ref()
        .and_then(|a| a.global_name.clone())
        .or_else(|| me_home.map(|m| m.name.clone()))
        .unwrap_or_else(|| "None".into());
    let username = ext
        .account
        .as_ref()
        .and_then(|a| a.username.clone())
        .unwrap_or_else(|| "—".into());
    let email = if hides {
        "Hidden".into()
    } else {
        ext.account
            .as_ref()
            .and_then(|a| a.email.clone())
            .unwrap_or_else(|| "None".into())
    };
    let phone = if hides {
        "Hidden".into()
    } else {
        ext.account
            .as_ref()
            .and_then(|a| a.phone.clone())
            .unwrap_or_else(|| "None".into())
    };
    let mfa_on = ext.account.as_ref().is_some_and(|a| a.mfa);

    let mut page = col()
        .w_full()
        .gap(0.0)
        .child(text("Account").font(type_title()).color(FG).pb(16.0));

    if let Some(ref err) = ext.account_error {
        page = page.child(text(err.clone()).font(type_small()).color(bad()).pb(6.0));
    }

    if let Some(me) = me_home {
        page = page.child(
            col()
                .w(300.0)
                .min_h(244.0)
                .padding(18.0)
                .mb(22.0)
                .gap(8.0)
                .radius(RADIUS_CARD)
                .fill(BG)
                .border(1.0, hairline())
                .child(avatar_box(
                    s,
                    me.avatar.as_deref(),
                    &me.name,
                    72.0,
                    36.0,
                    me.status.as_deref(),
                    false,
                    1.0,
                ))
                .child(
                    text(display_name.clone())
                        .font(type_section())
                        .color(FG)
                        .pt(6.0),
                )
                .child(text(username.clone()).font(type_small()).color(fg_muted()))
                .children(crate::discord::format_snowflake_date(&me.id).map(|since| {
                    col()
                        .pt(12.0)
                        .gap(4.0)
                        .child(text("Member since").font(type_small()).color(fg_muted()))
                        .child(text(since).font(type_label()).color(FG))
                })),
        );
    }

    let account_row = |label: &str, value: String, trailing: Option<El>| -> El {
        let mut right = row()
            .gap(10.0)
            .center_y()
            .child(text(value).font(type_body()).color(fg_muted()));
        if let Some(t) = trailing {
            right = right.child(t);
        }
        row()
            .w_full()
            .h(62.0)
            .gap(12.0)
            .center_y()
            .justify_between()
            .child(text(label).font(type_body()).color(FG))
            .child(right)
    };

    let next_status = match cur_status.as_str() {
        "online" => "idle",
        "idle" => "dnd",
        "dnd" => "invisible",
        _ => "online",
    };

    page = page
        .child(account_row(
            "Display name",
            display_name,
            Some(
                sheet_action_btn("dc-set-edit-dname", "Edit")
                    .label("Edit display name")
                    .on_press(|s: &mut AppState| {
                        s.select_discord_settings_page(DiscordSettingsPage::Profiles);
                    }),
            ),
        ))
        .child(account_row(
            "Username",
            username,
            Some(
                sheet_action_btn("dc-set-edit-uname", "Edit")
                    .label("Edit: Username")
                    .on_press(|s: &mut AppState| {
                        s.open_external_url("https://discord.com/settings")
                    }),
            ),
        ))
        .child(account_row(
            "Email",
            email.clone(),
            Some(
                sheet_action_btn(
                    "dc-set-edit-email",
                    if email != "None" { "Edit" } else { "Add" },
                )
                .label("Edit: Email")
                .on_press(|s: &mut AppState| s.open_external_url("https://discord.com/settings")),
            ),
        ))
        .child(account_row(
            "Phone number",
            phone.clone(),
            Some(
                sheet_action_btn(
                    "dc-set-edit-phone",
                    if phone != "None" { "Edit" } else { "Add" },
                )
                .label("Edit: Phone number")
                .on_press(|s: &mut AppState| s.open_external_url("https://discord.com/settings")),
            ),
        ))
        .child(el().h(20.0))
        .child(
            row()
                .w_full()
                .h(62.0)
                .gap(12.0)
                .center_y()
                .justify_between()
                .child(text("Status").font(type_body()).color(FG))
                .child(
                    row()
                        .key("dc-set-status-trigger")
                        .role("button")
                        .label("Status")
                        .focusable(true)
                        .hit()
                        .cursor(Cursor::Pointer)
                        .h(32.0)
                        .px(12.0)
                        .gap(8.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(glass_still_control_fill())
                        .border(1.0, hairline())
                        .hover(hover_fill())
                        .on_press(move |s: &mut AppState| s.set_discord_status(next_status))
                        .child(
                            el().size(7.0, 7.0)
                                .radius(4.0)
                                .fill(status_color(&cur_status)),
                        )
                        .child(
                            text(status_label(&cur_status))
                                .font(type_control())
                                .color(FG),
                        ),
                ),
        )
        .child(account_row(
            "Custom status",
            s.discord
                .custom_status
                .clone()
                .unwrap_or_else(|| "None".into()),
            Some(
                row()
                    .gap(6.0)
                    .center_y()
                    .children(s.discord.custom_status.is_some().then(|| {
                        sheet_action_btn("dc-set-clear-cstatus", "Clear")
                            .on_press(|s: &mut AppState| s.save_discord_custom_status_ext(true))
                    }))
                    .child(
                        sheet_action_btn(
                            "dc-set-open-cstatus",
                            if s.discord.custom_status.is_some() {
                                "Edit"
                            } else {
                                "Set"
                            },
                        )
                        .on_press(|s: &mut AppState| {
                            s.discord.custom_status_input = TextEdit::new(
                                s.discord.custom_status.as_deref().unwrap_or_default(),
                            );
                            s.discord.custom_status_open = true;
                        }),
                    ),
            ),
        ))
        .child(section_head("Password and security"))
        .child(account_row(
            "Password",
            String::new(),
            Some(
                sheet_action_btn("dc-set-edit-pass", "Edit")
                    .label("Edit: Password")
                    .on_press(|s: &mut AppState| {
                        s.open_external_url("https://discord.com/settings")
                    }),
            ),
        ))
        .child(account_row(
            "Multi-factor authentication",
            if mfa_on { "On" } else { "Off" }.into(),
            Some(
                sheet_action_btn("dc-set-edit-mfa", if mfa_on { "Manage" } else { "Set up" })
                    .on_press(|s: &mut AppState| {
                        s.open_external_url("https://discord.com/settings")
                    }),
            ),
        ))
        .child(account_row(
            "Logged-in devices",
            String::new(),
            Some(
                sheet_action_btn("dc-set-devices", "View")
                    .on_press(|s: &mut AppState| s.load_discord_sessions()),
            ),
        ))
        .child(section_head("Session"))
        .child(
            row()
                .w_full()
                .py(10.0)
                .justify_between()
                .center_y()
                .child(text("Log out").font(type_body()).color(FG))
                .child(if ext.logout_confirm {
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(sheet_action_btn("dc-set-logout-no", "Cancel").on_press(
                            |s: &mut AppState| s.discord.settings_ext.logout_confirm = false,
                        ))
                        .child(
                            row()
                                .key("dc-set-logout-yes")
                                .role("button")
                                .label("Confirm log out")
                                .cursor(Cursor::Pointer)
                                .hit()
                                .h(30.0)
                                .px(12.0)
                                .center()
                                .radius(RADIUS_PILL)
                                .fill(bad())
                                .on_press(|s: &mut AppState| {
                                    s.close_discord_settings_clean();
                                    s.sign_out_discord();
                                })
                                .child(text("Log out?").font(type_control()).color(FG)),
                        )
                } else {
                    sheet_action_btn("dc-set-logout", "Log out")
                        .on_press(|s: &mut AppState| s.discord.settings_ext.logout_confirm = true)
                }),
        );

    page
}

fn devices_subpage(s: &AppState) -> El {
    let ext = &s.discord.settings_ext;
    let hides = ext.streamer_hides();
    let mut page = col().w_full().gap(8.0).child(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .pb(6.0)
            .child(text("Logged-in devices").font(type_title()).color(FG))
            .child(
                glass_button("dc-dev-back", "Back", None)
                    .on_press(|s: &mut AppState| s.discord.settings_ext.devices_view = false),
            ),
    );

    if ext.sessions_loading && ext.sessions.is_empty() {
        return page.child(text("Loading devices…").font(type_body()).color(fg_muted()));
    }

    let others: Vec<String> = ext
        .sessions
        .iter()
        .filter(|x| !x.current)
        .filter_map(|x| x.id_hash.clone())
        .collect();

    for (idx, sess) in ext.sessions.iter().enumerate() {
        let title = sess.os.clone().unwrap_or_else(|| "Unknown".into());
        let mut detail_parts = Vec::new();
        if let Some(ref p) = sess.platform {
            detail_parts.push(p.clone());
        }
        if !hides
            && let Some(ref loc) = sess.location
            && !loc.is_empty()
        {
            detail_parts.push(loc.clone());
        }
        if sess.current {
            detail_parts.push("This device".into());
        }
        let hash = sess.id_hash.clone();
        page = page.child(
            row()
                .key(format!("dc-dev-{idx}"))
                .w_full()
                .py(8.0)
                .justify_between()
                .center_y()
                .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                .child(
                    col()
                        .gap(2.0)
                        .child(text(title).font(type_body()).color(FG))
                        .child(
                            text(detail_parts.join(" · "))
                                .font(type_label())
                                .color(fg_muted()),
                        ),
                )
                .children((!sess.current && hash.is_some()).then(|| {
                    let h = hash.unwrap();
                    glass_button("dc-dev-out", "Log out", None)
                        .key(format!("dc-dev-out-{idx}"))
                        .on_press(move |s: &mut AppState| {
                            s.logout_discord_sessions(vec![h.clone()])
                        })
                })),
        );
    }

    if !others.is_empty() {
        page = page.child(
            row().pt(8.0).justify_end().child(
                glass_button("dc-dev-out-all", "Log out all other devices", None)
                    .on_press(move |s: &mut AppState| s.logout_discord_sessions(others.clone())),
            ),
        );
    }
    page
}

fn profiles_page(s: &AppState) -> El {
    let ext = &s.discord.settings_ext;
    let is_server = ext.profile_guild_id.is_some();
    let mut page = col()
        .w_full()
        .gap(10.0)
        .child(text("Profiles").font(type_title()).color(FG))
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    filter_pill("dc-prof-tab-user", "User profile", !is_server).on_press(
                        |s: &mut AppState| {
                            s.discord.settings_ext.profile_guild_id = None;
                        },
                    ),
                )
                .child(
                    filter_pill("dc-prof-tab-srv", "Server profile", is_server).on_press(
                        |s: &mut AppState| {
                            if s.discord.settings_ext.profile_guild_id.is_none() {
                                s.discord.settings_ext.profile_guild_id =
                                    s.discord.active_guild_id.clone().or_else(|| {
                                        s.discord.home.guilds.first().map(|g| g.id.clone())
                                    });
                            }
                        },
                    ),
                ),
        );

    if is_server {
        let cur_gid = ext.profile_guild_id.clone().unwrap_or_default();
        let mut guild_pills = row().gap(6.0).wrap();
        for g in s.discord.home.guilds.iter().take(12) {
            let gid = g.id.clone();
            let active = cur_gid == g.id;
            guild_pills = guild_pills.child(
                filter_pill(format!("dc-prof-g-{}", g.id), g.name.clone(), active).on_press(
                    move |s: &mut AppState| {
                        s.discord.settings_ext.profile_guild_id = Some(gid.clone());
                    },
                ),
            );
        }
        page = page
            .child(section_head("Server"))
            .child(guild_pills)
            .child(section_head("Server nickname"))
            .child(
                text_input(&ext.profile_nick_input, type_body())
                    .key("dc-prof-nick")
                    .placeholder("Nickname in this server", fg_muted())
                    .w_full()
                    .h(36.0)
                    .px(12.0)
                    .radius(RADIUS_SMALL)
                    .fill(BG)
                    .border(1.0, hairline())
                    .color(FG)
                    .single_line()
                    .on_edit(edit_access(|s: &mut AppState| {
                        s.discord.settings_ext.profile_dirty = true;
                        &mut s.discord.settings_ext.profile_nick_input
                    })),
            );
    } else {
        page = page.child(section_head("Display name")).child(
            text_input(&ext.profile_name_input, type_body())
                .key("dc-prof-dname")
                .placeholder("Display name", fg_muted())
                .w_full()
                .h(36.0)
                .px(12.0)
                .radius(RADIUS_SMALL)
                .fill(BG)
                .border(1.0, hairline())
                .color(FG)
                .single_line()
                .on_edit(edit_access(|s: &mut AppState| {
                    s.discord.settings_ext.profile_dirty = true;
                    &mut s.discord.settings_ext.profile_name_input
                })),
        );
    }

    page = page
        .child(section_head("Pronouns"))
        .child(
            text_input(&ext.profile_pronouns_input, type_body())
                .key("dc-prof-pronouns")
                .placeholder("Add your pronouns", fg_muted())
                .w_full()
                .h(36.0)
                .px(12.0)
                .radius(RADIUS_SMALL)
                .fill(BG)
                .border(1.0, hairline())
                .color(FG)
                .single_line()
                .on_edit(edit_access(|s: &mut AppState| {
                    s.discord.settings_ext.profile_dirty = true;
                    &mut s.discord.settings_ext.profile_pronouns_input
                })),
        )
        .child(section_head("About me"))
        .child(
            text_input(&ext.profile_bio_input, type_body())
                .key("dc-prof-bio")
                .placeholder("Tell people about yourself", fg_muted())
                .w_full()
                .min_h(72.0)
                .px(12.0)
                .py(8.0)
                .radius(RADIUS_SMALL)
                .fill(BG)
                .border(1.0, hairline())
                .color(FG)
                .on_edit(edit_access(|s: &mut AppState| {
                    s.discord.settings_ext.profile_dirty = true;
                    &mut s.discord.settings_ext.profile_bio_input
                })),
        );

    if !is_server && !ext.server_tags.is_empty() {
        let mut tag_row = row().gap(6.0).wrap().child(
            filter_pill("dc-prof-tag-none", "None", ext.active_tag_guild.is_none())
                .on_press(|s: &mut AppState| s.set_discord_server_tag(None)),
        );
        for t in &ext.server_tags {
            let active = ext.active_tag_guild.as_deref() == Some(t.id.as_str());
            let tid = t.id.clone();
            tag_row = tag_row.child(
                filter_pill(
                    format!("dc-prof-tag-{}", t.id),
                    format!("{} ({})", t.tag, t.name),
                    active,
                )
                .on_press(move |s: &mut AppState| s.set_discord_server_tag(Some(tid.clone()))),
            );
        }
        page = page.child(section_head("Server tag")).child(tag_row);
    }

    page.child(
        row().pt(8.0).justify_end().child(
            primary_button("dc-prof-save", "Save changes", None)
                .on_press(|s: &mut AppState| s.save_discord_profile()),
        ),
    )
}

const MISSING_FROM_INSTALL: &str = "Missing from this install. Reinstall Exo to repair it.";

fn missing_detail(ready: Option<bool>) -> Option<&'static str> {
    (ready == Some(false)).then_some(MISSING_FROM_INSTALL)
}

fn voice_page(s: &AppState) -> El {
    let ext = &s.discord.settings_ext;
    let client = &ext.client;
    let audio = s.discord.voice_ui.audio.as_ref();
    let in_vol = audio.map(|a| a.input_volume.round() as i32).unwrap_or(100);
    let out_vol = audio.map(|a| a.output_volume.round() as i32).unwrap_or(100);
    let sb_muted = audio.is_some_and(|a| a.soundboard_muted);
    let sb_vol = if sb_muted {
        0
    } else {
        audio
            .map(|a| a.soundboard_volume.round() as i32)
            .unwrap_or(100)
    };
    let exo_on =
        client.noise_suppression && client.noise_suppression_kind.as_deref() != Some("standard");
    let sup_val = if !client.noise_suppression {
        "none"
    } else if client.noise_suppression_kind.as_deref() == Some("standard") {
        "standard"
    } else if client.noise_suppression_kind.as_deref() == Some("exo-dfn3") {
        "exo-dfn3"
    } else {
        "exo"
    };
    let tier_val = normalize_voice_tier(&client.voice_tier);

    let mut page = col()
        .w_full()
        .gap(4.0)
        .child(text("Voice and video").font(type_title()).color(FG).pb(6.0))
        .child(section_head("Devices"));

    if let Some(a) = audio {
        let mut in_pills = row().gap(6.0).wrap().child(
            filter_pill("dc-in-default", "Default", a.input.is_none())
                .on_press(|s: &mut AppState| s.set_discord_audio(json!({ "input": "" }))),
        );
        for d in &a.inputs {
            let active = a.input.as_deref() == Some(d.id.as_str());
            let id = d.id.clone();
            in_pills = in_pills.child(
                filter_pill(format!("dc-in-{}", d.id), d.name.clone(), active)
                    .on_press(move |s: &mut AppState| s.set_discord_audio(json!({ "input": id }))),
            );
        }
        page = page
            .child(
                col()
                    .w_full()
                    .py(6.0)
                    .gap(6.0)
                    .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                    .child(text("Microphone").font(type_body()).color(FG))
                    .child(in_pills),
            )
            .child(step_row(
                "dc-in-vol",
                "Microphone volume",
                format!("{in_vol}%"),
                in_vol > 0,
                in_vol < 200,
                move |s: &mut AppState| {
                    let cur = to_discord_volume_slider(in_vol as f64);
                    let next = from_discord_volume_slider((cur - 10.0).max(0.0)).round() as i32;
                    s.set_discord_audio(json!({ "inputVolume": next }));
                },
                move |s: &mut AppState| {
                    let cur = to_discord_volume_slider(in_vol as f64);
                    let next = from_discord_volume_slider((cur + 10.0).min(200.0)).round() as i32;
                    s.set_discord_audio(json!({ "inputVolume": next }));
                },
            ));

        let mut out_pills = row().gap(6.0).wrap().child(
            filter_pill("dc-out-default", "Default", a.output.is_none())
                .on_press(|s: &mut AppState| s.set_discord_audio(json!({ "output": "" }))),
        );
        for d in &a.outputs {
            let active = a.output.as_deref() == Some(d.id.as_str());
            let id = d.id.clone();
            out_pills = out_pills.child(
                filter_pill(format!("dc-out-{}", d.id), d.name.clone(), active)
                    .on_press(move |s: &mut AppState| s.set_discord_audio(json!({ "output": id }))),
            );
        }
        page = page
            .child(
                col()
                    .w_full()
                    .py(6.0)
                    .gap(6.0)
                    .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                    .child(text("Speaker").font(type_body()).color(FG))
                    .child(out_pills),
            )
            .child(step_row(
                "dc-out-vol",
                "Speaker volume",
                format!("{out_vol}%"),
                out_vol > 0,
                out_vol < 200,
                move |s: &mut AppState| {
                    let cur = to_discord_volume_slider(out_vol as f64);
                    let next = from_discord_volume_slider((cur - 10.0).max(0.0)).round() as i32;
                    s.set_discord_audio(json!({ "outputVolume": next }));
                },
                move |s: &mut AppState| {
                    let cur = to_discord_volume_slider(out_vol as f64);
                    let next = from_discord_volume_slider((cur + 10.0).min(200.0)).round() as i32;
                    s.set_discord_audio(json!({ "outputVolume": next }));
                },
            ))
            .child(step_row(
                "dc-sb-vol",
                "Soundboard volume",
                if sb_vol == 0 {
                    "Off".into()
                } else {
                    format!("{sb_vol}%")
                },
                sb_vol > 0,
                sb_vol < 100,
                move |s: &mut AppState| {
                    let next = (sb_vol - 10).max(0);
                    if next == 0 {
                        s.set_discord_audio(json!({ "soundboardMuted": true }));
                    } else {
                        s.set_discord_audio(
                            json!({ "soundboardVolume": next, "soundboardMuted": false }),
                        );
                    }
                },
                move |s: &mut AppState| {
                    let next = (sb_vol + 10).min(100);
                    s.set_discord_audio(
                        json!({ "soundboardVolume": next, "soundboardMuted": false }),
                    );
                },
            ));
    }

    page = page.child(section_head("Microphone"));

    let meter_fill = ext
        .mic_level
        .as_ref()
        .map(|l| db_fill_ratio(l.after))
        .unwrap_or(0.0);
    let meter_voiced = ext.mic_level.as_ref().is_some_and(|l| l.voiced);
    let meter_db = ext
        .mic_level
        .as_ref()
        .map(|l| format!("{:.0} dBFS", l.after.clamp(-100.0, 0.0)))
        .unwrap_or_else(|| "Idle".into());
    let advice = ext.mic_level.as_ref().and_then(mic_advice);
    page = page.child(
        col()
            .key("dc-mic-meter")
            .w_full()
            .py(8.0)
            .gap(6.0)
            .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
            .child(
                row()
                    .w_full()
                    .justify_between()
                    .center_y()
                    .child(text("Input level").font(type_body()).color(FG))
                    .child(
                        row()
                            .gap(10.0)
                            .center_y()
                            .child(text(meter_db).font(type_label()).color(fg_muted()))
                            .child(
                                text(if let Some(a) = advice {
                                    a.to_string()
                                } else if meter_voiced {
                                    "Speaking".into()
                                } else {
                                    "Listening".into()
                                })
                                .font(type_label())
                                .color(if advice.is_some() {
                                    warn()
                                } else if meter_voiced {
                                    good()
                                } else {
                                    fg_muted()
                                }),
                            ),
                    ),
            )
            .child(
                el().w_full()
                    .h(8.0)
                    .radius(4.0)
                    .fill(selected_fill())
                    .clip_rounded(4.0)
                    .child(
                        el().h_full()
                            .w(420.0 * meter_fill)
                            .radius(4.0)
                            .fill(if meter_voiced { good() } else { fg_muted() })
                            .motion(SPRING_QUICK),
                    ),
            ),
    );

    let monitor_on = ext.mic_monitor_on;
    page = page
        .child(toggle_row(
            "dc-set-mic-monitor",
            "Test microphone",
            ext.mic_monitor_speakers_warn
                .then_some("Speakers echo into the mic: use headphones"),
            monitor_on,
            move |s: &mut AppState| s.set_discord_mic_monitor(!monitor_on),
        ))
        .child(choice_row(
            "dc-set-ns",
            "Voice cleanup",
            (sup_val == "exo")
                .then(|| missing_detail(client.voice_model_ready))
                .flatten()
                .or_else(|| {
                    ext.mic_level
                        .as_ref()
                        .and_then(|l| suppression_detail(&l.stages.suppression))
                }),
            sup_val,
            vec![
                ("exo", "Exo AI"),
                ("exo-dfn3", "DeepFilterNet 3"),
                ("standard", "Standard"),
                ("none", "None"),
            ],
            |s, next| {
                if next == "none" {
                    s.save_discord_client_settings(json!({ "noiseSuppression": false }));
                } else {
                    s.save_discord_client_settings(
                        json!({ "noiseSuppression": true, "noiseSuppressionKind": next }),
                    );
                }
            },
        ));

    if sup_val == "exo" {
        page = page.child(choice_row(
            "dc-set-voice-tier",
            "Processing tier",
            (tier_val != "cpu")
                .then(|| missing_detail(client.voice_hq_model_ready))
                .flatten()
                .or(Some(match tier_val {
                    "cpu" => "Pinned off game cores, under 20% of one core",
                    "hq" => "48 kHz Studio; steps down if a game needs the GPU",
                    _ => "48 kHz Studio when idle, low CPU in games",
                })),
            tier_val,
            vec![
                ("auto", "Auto"),
                ("cpu", "Low CPU"),
                ("hq", "48 kHz Studio"),
            ],
            |s, next| {
                s.save_discord_client_settings(json!({ "voiceTier": next }));
            },
        ));
    }

    let only_my = client.only_my_voice;
    let loopback_ec = client.loopback_echo_cancellation;
    let learned = client
        .my_voice_learned
        .unwrap_or(s.discord.voice_ui.my_voice_learned);
    let secs = client.my_voice_seconds.unwrap_or(0.0);
    page = page.child(toggle_row(
        "dc-set-only-my-voice",
        "Only my voice",
        missing_detail(client.my_voice_model_ready).or(Some(
            "Uses your TitaNet voiceprint to block other voices in the room",
        )),
        only_my,
        move |s: &mut AppState| {
            s.save_discord_client_settings(json!({ "onlyMyVoice": !only_my }));
        },
    ));
    if only_my || learned {
        page = page.child(value_row(
            "Your voice",
            if learned {
                speech_time(secs)
            } else {
                "Learning".into()
            },
            learned.then(|| {
                glass_button(
                    "dc-set-myvoice-reset",
                    if only_my { "Start over" } else { "Forget" },
                    None,
                )
                .on_press(|s: &mut AppState| s.forget_discord_my_voice())
            }),
        ));
    }
    page = page.child(toggle_row(
        "dc-set-loopback-echo",
        "Game audio echo cancellation",
        Some("Removes speaker bleed using system audio loopback"),
        loopback_ec,
        move |s: &mut AppState| {
            s.save_discord_client_settings(json!({ "loopbackEchoCancellation": !loopback_ec }));
        },
    ));

    let even_v = client.even_voices;
    let target_lufs = clamp_voice_target_lufs(client.voice_target_lufs);
    let mic_agc = client.mic_auto_gain;
    page = page.child(toggle_row(
        "dc-set-even-voices",
        "Level voices",
        None,
        even_v,
        move |s: &mut AppState| {
            s.save_discord_client_settings(json!({ "evenVoices": !even_v }));
        },
    ));
    if even_v {
        page = page.child(step_row(
            "dc-set-voice-lufs",
            "Voice target loudness",
            format_voice_lufs(target_lufs),
            target_lufs > VOICE_LUFS_MIN,
            target_lufs < VOICE_LUFS_MAX,
            move |s: &mut AppState| {
                let next = clamp_voice_target_lufs(target_lufs - 1);
                s.save_discord_client_settings(json!({ "voiceTargetLufs": next }));
            },
            move |s: &mut AppState| {
                let next = clamp_voice_target_lufs(target_lufs + 1);
                s.save_discord_client_settings(json!({ "voiceTargetLufs": next }));
            },
        ));
    }
    page = page.child(toggle_row(
        "dc-set-mic-agc",
        "Mic auto gain",
        None,
        mic_agc,
        move |s: &mut AppState| {
            s.save_discord_client_settings(json!({ "micAutoGain": !mic_agc }));
        },
    ));

    if !exo_on {
        let others_ns = client.others_noise_suppression;
        let echo = client.echo_cancellation;
        let auto_sens = client.auto_sensitivity;
        let sens = client.sensitivity.round() as i32;
        page = page
            .child(toggle_row(
                "dc-set-others-ns",
                "Noise suppression for others",
                None,
                others_ns,
                move |s: &mut AppState| {
                    s.save_discord_client_settings(json!({ "othersNoiseSuppression": !others_ns }));
                },
            ))
            .child(toggle_row(
                "dc-set-echo",
                "Echo cancellation",
                None,
                echo,
                move |s: &mut AppState| {
                    s.save_discord_client_settings(json!({ "echoCancellation": !echo }));
                },
            ))
            .child(toggle_row(
                "dc-set-auto-sens",
                "Automatically determine input sensitivity",
                None,
                auto_sens,
                move |s: &mut AppState| {
                    s.save_discord_client_settings(json!({ "autoSensitivity": !auto_sens }));
                },
            ));
        if !auto_sens {
            page = page.child(step_row(
                "dc-set-sens",
                "Input sensitivity",
                format!("{sens} dB"),
                sens > -100,
                sens < 0,
                move |s: &mut AppState| {
                    s.save_discord_client_settings(json!({ "sensitivity": (sens - 5).max(-100) }));
                },
                move |s: &mut AppState| {
                    s.save_discord_client_settings(json!({ "sensitivity": (sens + 5).min(0) }));
                },
            ));
        }
    }

    let ptt = client.push_to_talk;
    let ptt_delay = client.ptt_delay;
    let ptt_keys = client
        .keybinds
        .iter()
        .find(|k| k.action == "PUSH_TO_TALK")
        .map(|k| k.keys.as_slice())
        .unwrap_or(&[]);
    let recording_ptt = ext.recording_keybind == Some(KeybindRecordTarget::PushToTalk);
    page = page.child(toggle_row(
        "dc-set-ptt",
        "Push to talk",
        None,
        ptt,
        move |s: &mut AppState| {
            s.save_discord_client_settings(json!({ "pushToTalk": !ptt }));
        },
    ));
    if ptt {
        page = page
            .child(value_row(
                "Push to talk keys",
                if recording_ptt {
                    "Press a key…".into()
                } else {
                    keybind_label(ptt_keys)
                },
                Some(
                    glass_button(
                        "dc-set-ptt-rec",
                        if recording_ptt { "Stop" } else { "Record" },
                        None,
                    )
                    .on_press(move |s: &mut AppState| {
                        s.discord.settings_ext.recording_keybind = if recording_ptt {
                            None
                        } else {
                            Some(KeybindRecordTarget::PushToTalk)
                        };
                    }),
                ),
            ))
            .child(step_row(
                "dc-set-ptt-delay",
                "Release delay",
                format!("{ptt_delay} ms"),
                ptt_delay > 0,
                ptt_delay < 2000,
                move |s: &mut AppState| {
                    s.save_discord_client_settings(json!({ "pttDelay": (ptt_delay - 50).max(0) }));
                },
                move |s: &mut AppState| {
                    s.save_discord_client_settings(
                        json!({ "pttDelay": (ptt_delay + 50).min(2000) }),
                    );
                },
            ));
    }

    let atten = client.attenuation;
    let atten_self = client.attenuate_self;
    let atten_others = client.attenuate_others;
    page = page
        .child(section_head("Attenuation"))
        .child(step_row(
            "dc-set-atten",
            "Attenuation",
            format!("{atten}%"),
            atten > 0,
            atten < 100,
            move |s: &mut AppState| {
                s.save_discord_client_settings(json!({ "attenuation": (atten - 10).max(0) }));
            },
            move |s: &mut AppState| {
                s.save_discord_client_settings(json!({ "attenuation": (atten + 10).min(100) }));
            },
        ))
        .child(toggle_row(
            "dc-set-atten-self",
            "When I speak",
            None,
            atten_self,
            move |s: &mut AppState| {
                s.save_discord_client_settings(json!({ "attenuateSelf": !atten_self }));
            },
        ))
        .child(toggle_row(
            "dc-set-atten-others",
            "When others speak",
            None,
            atten_others,
            move |s: &mut AppState| {
                s.save_discord_client_settings(json!({ "attenuateOthers": !atten_others }));
            },
        ))
        .child(section_head("Screen share"))
        .child(choice_row(
            "dc-set-gl-res",
            "Default resolution",
            None,
            client.go_live_height.unwrap_or(1080),
            vec![
                (720, "720p"),
                (1080, "1080p"),
                (1440, "1440p"),
                (0, "Source"),
            ],
            |s, v| {
                s.discord.voice_ui.golive_height = v;
                s.save_discord_client_settings(json!({ "goLiveHeight": v }));
            },
        ))
        .child(choice_row(
            "dc-set-gl-fps",
            "Default frame rate",
            None,
            client.go_live_fps.unwrap_or(60),
            vec![
                (15, "15 fps"),
                (30, "30 fps"),
                (60, "60 fps"),
                (120, "120 fps"),
            ],
            |s, v| {
                s.discord.voice_ui.golive_fps = v;
                s.save_discord_client_settings(json!({ "goLiveFps": v }));
            },
        ));

    let hevc = client.go_live_hevc.unwrap_or(true);
    page.child(toggle_row(
        "dc-set-gl-hevc",
        "HEVC encoding",
        client.capture_mode.as_deref().map(|m| {
            if m == "dxgi" {
                "Using screen compatibility mode"
            } else {
                "Windows Graphics Capture"
            }
        }),
        hevc,
        move |s: &mut AppState| {
            s.save_discord_client_settings(json!({ "goLiveHevc": !hevc }));
        },
    ))
}

fn chat_page(s: &AppState) -> El {
    let ext = &s.discord.settings_ext;
    let u = &ext.user;
    let font_idx = FONT_SIZES
        .iter()
        .position(|&x| x == ext.chat_font_size)
        .unwrap_or(3);
    let zoom_idx = ZOOM_LEVELS
        .iter()
        .position(|&x| x == ext.chat_zoom)
        .unwrap_or(5);
    let inline_embed = u.inline_embed_media;
    let inline_att = u.inline_attachment_media;
    let render_embeds = u.render_embeds;
    let render_react = u.render_reactions;
    let dev_mode = u.developer_mode;
    let compact = u.message_display_compact;
    let gif_auto = u.gif_auto_play;
    let anim_emoji = u.animate_emoji;
    let tts = u.enable_tts_command;
    let streamer = ext.streamer_mode;
    let reduce_motion = ext.reduce_motion;

    let layout_val = s
        .settings
        .discord_layout
        .as_deref()
        .unwrap_or("exo")
        .to_string();

    col()
        .w_full()
        .gap(4.0)
        .child(text("Chat").font(type_title()).color(FG).pb(6.0))
        .child(section_head("Layout"))
        .child(choice_row(
            "dc-set-layout",
            "Discord layout",
            Some("Switch between Exo's top server bar and classic Discord's left server rail."),
            layout_val,
            vec![("exo".into(), "Exo"), ("classic".into(), "Classic Discord")],
            |s, v| s.set_discord_layout(&v),
        ))
        .child(section_head("Messages"))
        .child(toggle_row(
            "dc-set-inline-embed",
            "Show images from links",
            None,
            inline_embed,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "inlineEmbedMedia": !inline_embed }));
            },
        ))
        .child(toggle_row(
            "dc-set-inline-att",
            "Show uploaded images",
            None,
            inline_att,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "inlineAttachmentMedia": !inline_att }));
            },
        ))
        .child(toggle_row(
            "dc-set-embeds",
            "Show link previews",
            None,
            render_embeds,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "renderEmbeds": !render_embeds }));
            },
        ))
        .child(toggle_row(
            "dc-set-reactions",
            "Show reactions",
            None,
            render_react,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "renderReactions": !render_react }));
            },
        ))
        .child(choice_row(
            "dc-set-spoilers",
            "Show spoilers",
            None,
            u.render_spoilers.clone(),
            vec![
                ("ON_CLICK".into(), "On click"),
                ("ALWAYS".into(), "Always"),
                ("IF_MODERATOR".into(), "On servers I moderate"),
            ],
            |s, v| s.save_discord_user_settings(json!({ "renderSpoilers": v })),
        ))
        .child(toggle_row(
            "dc-set-devmode",
            "Developer Mode",
            None,
            dev_mode,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "developerMode": !dev_mode }));
            },
        ))
        .child(section_head("Display"))
        .child(step_row(
            "dc-set-font",
            "Text size in chat",
            format!("{}px", ext.chat_font_size),
            font_idx > 0,
            font_idx + 1 < FONT_SIZES.len(),
            move |s: &mut AppState| {
                if font_idx > 0 {
                    s.discord.settings_ext.chat_font_size = FONT_SIZES[font_idx - 1];
                }
            },
            move |s: &mut AppState| {
                if font_idx + 1 < FONT_SIZES.len() {
                    s.discord.settings_ext.chat_font_size = FONT_SIZES[font_idx + 1];
                }
            },
        ))
        .child(step_row(
            "dc-set-zoom",
            "Zoom",
            format!("{}%", ext.chat_zoom),
            zoom_idx > 0,
            zoom_idx + 1 < ZOOM_LEVELS.len(),
            move |s: &mut AppState| {
                if zoom_idx > 0 {
                    s.discord.settings_ext.chat_zoom = ZOOM_LEVELS[zoom_idx - 1];
                }
            },
            move |s: &mut AppState| {
                if zoom_idx + 1 < ZOOM_LEVELS.len() {
                    s.discord.settings_ext.chat_zoom = ZOOM_LEVELS[zoom_idx + 1];
                }
            },
        ))
        .child(choice_row(
            "dc-set-density",
            "Message density",
            None,
            compact,
            vec![(false, "Default"), (true, "Compact")],
            |s, v| s.save_discord_user_settings(json!({ "messageDisplayCompact": v })),
        ))
        .child(choice_row(
            "dc-set-role-style",
            "Role colors",
            None,
            ext.role_style.clone(),
            vec![
                ("username".into(), "In names"),
                ("dot".into(), "Next to names"),
                ("hidden".into(), "Hidden"),
            ],
            |s, v| s.discord.settings_ext.role_style = v,
        ))
        .child(choice_row(
            "dc-set-timefmt",
            "Time format",
            None,
            u.timestamp_hour_cycle,
            vec![(0, "Auto"), (1, "12-hour"), (2, "24-hour")],
            |s, v| s.save_discord_user_settings(json!({ "timestampHourCycle": v })),
        ))
        .child(section_head("Motion"))
        .child(toggle_row(
            "dc-set-redmotion",
            "Reduce motion",
            None,
            reduce_motion,
            move |s: &mut AppState| {
                s.discord.settings_ext.reduce_motion = !reduce_motion;
            },
        ))
        .child(toggle_row(
            "dc-set-gifauto",
            "Play GIFs",
            None,
            gif_auto,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "gifAutoPlay": !gif_auto }));
            },
        ))
        .child(toggle_row(
            "dc-set-animemoji",
            "Play animated emoji",
            None,
            anim_emoji,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "animateEmoji": !anim_emoji }));
            },
        ))
        .child(choice_row(
            "dc-set-animsticker",
            "Play stickers",
            None,
            u.animate_stickers,
            vec![(0, "Always"), (1, "On interaction"), (2, "Never")],
            |s, v| s.save_discord_user_settings(json!({ "animateStickers": v })),
        ))
        .child(section_head("Text-to-speech"))
        .child(toggle_row(
            "dc-set-tts",
            "/tts command",
            None,
            tts,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "enableTtsCommand": !tts }));
            },
        ))
        .child(section_head("Streamer Mode"))
        .child(toggle_row(
            "dc-set-streamer",
            "Streamer Mode",
            Some("Hides personal info, invite links, sounds, and notifications while streaming"),
            streamer,
            move |s: &mut AppState| s.set_discord_streamer_mode(!streamer),
        ))
}

fn notifications_page(s: &AppState) -> El {
    let ext = &s.discord.settings_ext;
    let c = &ext.client;
    let u = &ext.user;
    let desk = c.desktop_notifications;
    let flash = c.taskbar_flash;
    let badge = c.unread_badge;
    let mute_all = c.disable_all_sounds;
    let sound_vol = c.sound_volume;
    let sel_sound = c.selected_channel_sound;
    let stream_notif = u.stream_notifications_enabled;
    let anniv_notif = u.enable_friend_anniversary_notifications;
    let online_notif = u.enable_friend_online_notifications;
    let prof_notif = u.enable_profile_updates_notifications;

    let mut page = col()
        .w_full()
        .gap(4.0)
        .child(text("Notifications").font(type_title()).color(FG).pb(6.0))
        .child(toggle_row(
            "dc-set-desknotif",
            "Desktop notifications",
            None,
            desk,
            move |s: &mut AppState| {
                s.save_discord_client_settings(json!({ "desktopNotifications": !desk }));
            },
        ))
        .child(toggle_row(
            "dc-set-flash",
            "Flash the taskbar",
            None,
            flash,
            move |s: &mut AppState| {
                s.save_discord_client_settings(json!({ "taskbarFlash": !flash }));
            },
        ))
        .child(toggle_row(
            "dc-set-badge",
            "Unread badge",
            None,
            badge,
            move |s: &mut AppState| {
                s.save_discord_client_settings(json!({ "unreadBadge": !badge }));
            },
        ))
        .child(section_head("Notify me about"))
        .child(toggle_row(
            "dc-set-n-live",
            "People I know going live",
            None,
            stream_notif,
            move |s: &mut AppState| {
                s.save_discord_user_settings(
                    json!({ "streamNotificationsEnabled": !stream_notif }),
                );
            },
        ))
        .child(toggle_row(
            "dc-set-n-anniv",
            "Friendship anniversaries",
            None,
            anniv_notif,
            move |s: &mut AppState| {
                s.save_discord_user_settings(
                    json!({ "enableFriendAnniversaryNotifications": !anniv_notif }),
                );
            },
        ))
        .child(toggle_row(
            "dc-set-n-online",
            "Friends coming online",
            None,
            online_notif,
            move |s: &mut AppState| {
                s.save_discord_user_settings(
                    json!({ "enableFriendOnlineNotifications": !online_notif }),
                );
            },
        ))
        .child(toggle_row(
            "dc-set-n-prof",
            "Friends' profile updates",
            None,
            prof_notif,
            move |s: &mut AppState| {
                s.save_discord_user_settings(
                    json!({ "enableProfileUpdatesNotifications": !prof_notif }),
                );
            },
        ))
        .child(choice_row(
            "dc-set-n-react",
            "Reactions to my messages",
            None,
            u.reaction_notifications,
            vec![
                (0, "All messages"),
                (1, "Only direct messages"),
                (2, "Never"),
            ],
            |s, v| s.save_discord_user_settings(json!({ "reactionNotifications": v })),
        ))
        .child(section_head("Sounds"))
        .child(toggle_row(
            "dc-set-mute-sounds",
            "Mute all sounds",
            None,
            mute_all,
            move |s: &mut AppState| {
                s.save_discord_client_settings(json!({ "disableAllSounds": !mute_all }));
            },
        ))
        .child(step_row(
            "dc-set-sound-vol",
            "Sound volume",
            format!("{sound_vol}%"),
            sound_vol > 0,
            sound_vol < 100,
            move |s: &mut AppState| {
                s.save_discord_client_settings(json!({ "soundVolume": (sound_vol - 10).max(0) }));
            },
            move |s: &mut AppState| {
                s.save_discord_client_settings(json!({ "soundVolume": (sound_vol + 10).min(100) }));
            },
        ))
        .child(sound_toggle_row("message1", "New message", c))
        .child(
            row()
                .w_full()
                .min_h(40.0)
                .py(8.0)
                .justify_between()
                .center_y()
                .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                .child(
                    text("New message in the channel I'm reading")
                        .font(type_body())
                        .color(FG),
                )
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(
                            glass_button("dc-snd-prev-message3", "Preview", None).on_press(
                                |s: &mut AppState| s.play_discord_ui_sound("message3", true),
                            ),
                        )
                        .child(
                            filter_pill(
                                "dc-snd-sel-ch",
                                if sel_sound && !mute_all { "On" } else { "Off" },
                                sel_sound && !mute_all,
                            )
                            .on_press(move |s: &mut AppState| {
                                s.save_discord_client_settings(
                                    json!({ "selectedChannelSound": !sel_sound }),
                                );
                            }),
                        ),
                ),
        );

    for (snd, label) in CALL_SOUNDS {
        page = page.child(sound_toggle_row(snd, label, c));
    }
    page
}

fn sound_toggle_row(sound: &'static str, label: &'static str, c: &DiscordClientSettings) -> El {
    let off = c.disabled_sounds.iter().any(|x| x == sound);
    let enabled = !off && !c.disable_all_sounds;
    row()
        .key(format!("dc-snd-row-{sound}"))
        .w_full()
        .min_h(40.0)
        .py(8.0)
        .justify_between()
        .center_y()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(text(label).font(type_body()).color(FG))
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    glass_button("dc-snd-prev", "Preview", None)
                        .key(format!("dc-snd-prev-{sound}"))
                        .on_press(move |s: &mut AppState| s.play_discord_ui_sound(sound, true)),
                )
                .child(
                    filter_pill(
                        format!("dc-snd-tog-{sound}"),
                        if enabled { "On" } else { "Off" },
                        enabled,
                    )
                    .on_press(move |s: &mut AppState| {
                        let mut list = s.discord.settings_ext.client.disabled_sounds.clone();
                        if list.iter().any(|x| x == sound) {
                            list.retain(|x| x != sound);
                        } else {
                            list.push(sound.to_string());
                        }
                        s.save_discord_client_settings(json!({ "disabledSounds": list }));
                    }),
                ),
        )
}

fn privacy_page(s: &AppState) -> El {
    let u = &s.discord.settings_ext.user;
    let dms_restricted = u
        .default_guilds_restricted_v2
        .unwrap_or(u.default_guilds_restricted);
    let flags = u.friend_source_flags;
    let everyone = (flags & 8) != 0;
    let fof = everyone || (flags & 2) != 0;
    let srv_m = everyone || (flags & 4) != 0;
    let show_notes = !u.hide_friend_request_notes;
    let online_share = u.notify_friends_on_come_online;
    let live_share = u.notify_server_members_on_go_live;
    let join_friends = u.allow_activity_party_privacy_friends;
    let join_vc = u.allow_activity_party_privacy_voice_channel;
    let act_default = if u.default_guilds_activity_restricted_v2 != 0 {
        u.default_guilds_activity_restricted_v2
    } else {
        match u.default_guilds_activity_restricted {
            0 => 1,
            1 => 2,
            _ => 3,
        }
    };
    let spam_val = if u.dm_spam_filter_v2 != 0 {
        u.dm_spam_filter_v2
    } else {
        match u.explicit_content_filter {
            0 => 1,
            1 => 2,
            _ => 3,
        }
    };

    col()
        .w_full()
        .gap(4.0)
        .child(text("Privacy").font(type_title()).color(FG).pb(6.0))
        .child(section_head("Activity"))
        .child(toggle_row(
            "dc-priv-online",
            "Share when I come online",
            None,
            online_share,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "notifyFriendsOnComeOnline": !online_share }));
            },
        ))
        .child(toggle_row(
            "dc-priv-live",
            "Share when I start streaming",
            None,
            live_share,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "notifyServerMembersOnGoLive": !live_share }));
            },
        ))
        .child(choice_row(
            "dc-priv-act-def",
            "Share my activity in new servers",
            None,
            act_default,
            vec![(1, "All servers"), (2, "Up to 200 members"), (3, "None")],
            |s, v| s.save_discord_user_settings(json!({ "defaultGuildsActivityRestrictedV2": v })),
        ))
        .child(toggle_row(
            "dc-priv-join-f",
            "Friends can join my game",
            None,
            join_friends,
            move |s: &mut AppState| {
                s.save_discord_user_settings(
                    json!({ "allowActivityPartyPrivacyFriends": !join_friends }),
                );
            },
        ))
        .child(toggle_row(
            "dc-priv-join-vc",
            "Voice channel members can join my game",
            None,
            join_vc,
            move |s: &mut AppState| {
                s.save_discord_user_settings(
                    json!({ "allowActivityPartyPrivacyVoiceChannel": !join_vc }),
                );
            },
        ))
        .child(section_head("Direct messages"))
        .child(toggle_row(
            "dc-priv-dms",
            "Allow DMs from server members",
            None,
            !dms_restricted,
            move |s: &mut AppState| {
                s.save_discord_user_settings(
                    json!({ "defaultGuildsRestrictedV2": !dms_restricted }),
                );
            },
        ))
        .child(choice_row(
            "dc-priv-spam",
            "Filter spam",
            None,
            spam_val,
            vec![(3, "All"), (2, "From non-friends"), (1, "Off")],
            |s, v| s.save_discord_user_settings(json!({ "dmSpamFilterV2": v })),
        ))
        .child(section_head("Friend requests from"))
        .child(toggle_row(
            "dc-priv-fr-all",
            "Everyone",
            None,
            everyone,
            move |s: &mut AppState| {
                let next = if everyone { 14 & !8 } else { 14 };
                s.save_discord_user_settings(json!({ "friendSourceFlags": next }));
            },
        ))
        .child(toggle_row(
            "dc-priv-fr-fof",
            "Friends of friends",
            None,
            fof,
            move |s: &mut AppState| {
                let next = if fof { flags & !2 & !8 } else { flags | 2 };
                s.save_discord_user_settings(json!({ "friendSourceFlags": next }));
            },
        ))
        .child(toggle_row(
            "dc-priv-fr-srv",
            "Server members",
            None,
            srv_m,
            move |s: &mut AppState| {
                let next = if srv_m { flags & !4 & !8 } else { flags | 4 };
                s.save_discord_user_settings(json!({ "friendSourceFlags": next }));
            },
        ))
        .child(toggle_row(
            "dc-priv-fr-notes",
            "Show notes on friend requests",
            None,
            show_notes,
            move |s: &mut AppState| {
                s.save_discord_user_settings(json!({ "hideFriendRequestNotes": show_notes }));
            },
        ))
        .child(section_head("Your data"))
        .child(choice_row(
            "dc-priv-vis",
            "Share my full profile with",
            None,
            if u.profile_visibility == 0 {
                3
            } else {
                u.profile_visibility
            },
            vec![
                (3, "Friends and all servers"),
                (2, "Friends and small servers"),
                (1, "Friends only"),
            ],
            |s, v| s.save_discord_user_settings(json!({ "profileVisibility": v })),
        ))
}

fn keybinds_page(s: &AppState) -> El {
    let ext = &s.discord.settings_ext;
    let binds = &ext.client.keybinds;
    let mut page = col()
        .w_full()
        .gap(8.0)
        .child(text("Keybinds").font(type_title()).color(FG));

    if binds.is_empty() && ext.new_keybind_action.is_none() {
        page = page.child(
            text("Set a key to mute, deafen or push to talk from any game.")
                .font(type_body())
                .color(fg_muted())
                .py(12.0),
        );
    }

    for (idx, bind) in binds.iter().enumerate() {
        let recording = ext.recording_keybind == Some(KeybindRecordTarget::Existing(idx));
        let action_label = KEYBIND_ACTIONS
            .iter()
            .find(|(v, _)| *v == bind.action)
            .map(|(_, l)| *l)
            .unwrap_or(bind.action.as_str());
        page = page.child(
            row()
                .key(format!("dc-kb-{idx}"))
                .w_full()
                .py(8.0)
                .gap(8.0)
                .justify_between()
                .center_y()
                .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                .child(
                    col()
                        .gap(2.0)
                        .child(text(action_label).font(type_body()).color(FG))
                        .child(
                            text(if recording {
                                "Press keys…".into()
                            } else {
                                keybind_label(&bind.keys)
                            })
                            .font(type_label())
                            .color(if recording {
                                good()
                            } else {
                                fg_muted()
                            }),
                        ),
                )
                .child(
                    row()
                        .gap(6.0)
                        .center_y()
                        .child(
                            glass_button(
                                "dc-kb-rec",
                                if recording { "Stop" } else { "Edit" },
                                None,
                            )
                            .key(format!("dc-kb-rec-{idx}"))
                            .on_press(move |s: &mut AppState| {
                                s.discord.settings_ext.recording_keybind = if recording {
                                    None
                                } else {
                                    Some(KeybindRecordTarget::Existing(idx))
                                };
                            }),
                        )
                        .child(
                            glass_button("dc-kb-del", "Delete", None)
                                .key(format!("dc-kb-del-{idx}"))
                                .on_press(move |s: &mut AppState| {
                                    let mut next = s.discord.settings_ext.client.keybinds.clone();
                                    if idx < next.len() {
                                        next.remove(idx);
                                        s.save_discord_client_settings(json!({ "keybinds": next }));
                                    }
                                }),
                        ),
                ),
        );
    }

    if let Some(ref cur_act) = ext.new_keybind_action {
        let mut act_pills = row().gap(6.0).wrap();
        for (val, lbl) in KEYBIND_ACTIONS {
            let active = cur_act == val;
            act_pills = act_pills.child(
                filter_pill(format!("dc-kb-act-{val}"), lbl, active).on_press(
                    move |s: &mut AppState| {
                        s.discord.settings_ext.new_keybind_action = Some(val.to_string());
                    },
                ),
            );
        }
        let rec_new = ext.recording_keybind == Some(KeybindRecordTarget::New);
        page = page.child(
            col()
                .w_full()
                .padding(12.0)
                .gap(8.0)
                .radius(RADIUS_CARD)
                .fill(BG)
                .border(1.0, hairline())
                .child(text("New keybind").font(type_section()).color(FG))
                .child(act_pills)
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(
                            primary_button(
                                "dc-kb-new-rec",
                                if rec_new {
                                    "Press a key…"
                                } else {
                                    "Record key"
                                },
                                None,
                            )
                            .on_press(|s: &mut AppState| {
                                s.discord.settings_ext.recording_keybind =
                                    Some(KeybindRecordTarget::New);
                            }),
                        )
                        .child(glass_button("dc-kb-new-cancel", "Cancel", None).on_press(
                            |s: &mut AppState| {
                                s.discord.settings_ext.new_keybind_action = None;
                                s.discord.settings_ext.recording_keybind = None;
                            },
                        )),
                ),
        );
    } else {
        page = page.child(row().pt(6.0).child(
            glass_button("dc-kb-add", "Add keybind", None).on_press(|s: &mut AppState| {
                s.discord.settings_ext.new_keybind_action = Some("PUSH_TO_TALK".into());
            }),
        ));
    }
    page
}

fn connected_apps_page(s: &AppState) -> El {
    let ext = &s.discord.settings_ext;
    let hides = ext.streamer_hides();
    let mut page = col()
        .w_full()
        .gap(6.0)
        .child(text("Connected apps").font(type_title()).color(FG))
        .child(section_head("Connections"));

    if ext.connections.is_empty() {
        page = page.child(
            text("No connected accounts.")
                .font(type_body())
                .color(fg_muted())
                .py(6.0),
        );
    } else {
        for (i, c) in ext.connections.iter().enumerate() {
            let kind = c.r#type.clone().unwrap_or_else(|| "Account".into());
            let name = if hides {
                "Hidden".into()
            } else {
                c.name.clone().unwrap_or_default()
            };
            page = page.child(value_row(
                &format!("{kind} ({i})"),
                if c.revoked {
                    format!("{name} · Disconnected")
                } else {
                    name
                },
                None,
            ));
        }
    }

    page = page.child(section_head("Authorized apps"));
    if ext.apps.is_empty() {
        page = page.child(
            text("No authorized apps.")
                .font(type_body())
                .color(fg_muted())
                .py(6.0),
        );
    } else {
        for app in &ext.apps {
            let id = app.id.clone();
            page = page.child(
                row()
                    .key(format!("dc-app-{}", app.id))
                    .w_full()
                    .py(8.0)
                    .justify_between()
                    .center_y()
                    .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
                    .child(
                        col()
                            .gap(2.0)
                            .child(
                                text(app.name.clone().unwrap_or_else(|| "App".into()))
                                    .font(type_body())
                                    .color(FG),
                            )
                            .children(app.description.as_ref().filter(|d| !d.is_empty()).map(
                                |d| {
                                    text(d.clone())
                                        .font(type_label())
                                        .color(fg_muted())
                                        .truncate()
                                },
                            )),
                    )
                    .child(
                        glass_button("dc-app-deauth", "Deauthorize", None)
                            .key(format!("dc-app-deauth-{}", app.id))
                            .on_press(move |s: &mut AppState| s.deauthorize_discord_app(&id)),
                    ),
            );
        }
    }
    page
}

pub fn settings_modal(s: &AppState, blur: bool) -> El {
    let page = s.discord.settings_page;
    let mut nav = col().w(212.0).h_full().py(24.0).px(12.0).gap(5.0).child(
        row()
            .px(10.0)
            .pb(32.0)
            .gap(8.0)
            .center_y()
            .child(icon_settings(16.0, FG))
            .child(text("Discord settings").font(type_section()).color(FG)),
    );

    for p in DiscordSettingsPage::ALL {
        let active = p == page;
        let mut item = row()
            .key(p.key())
            .role("tab")
            .label(p.label())
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .w_full()
            .h(34.0)
            .px(12.0)
            .center_y()
            .radius(RADIUS_SMALL)
            .fill(if active { selected_fill() } else { TRANSPARENT })
            .hover(if active {
                selected_fill()
            } else {
                hover_fill()
            })
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| s.select_discord_settings_page(p))
            .child(text(p.label()).font(type_control()).color(if active {
                FG
            } else {
                fg_muted()
            }));
        if active {
            item = item.autofocus();
        }
        nav = nav.child(item);
    }

    let body_content = match page {
        DiscordSettingsPage::Account => account_page(s),
        DiscordSettingsPage::Profiles => profiles_page(s),
        DiscordSettingsPage::Voice => voice_page(s),
        DiscordSettingsPage::Chat => chat_page(s),
        DiscordSettingsPage::Notifications => notifications_page(s),
        DiscordSettingsPage::Privacy => privacy_page(s),
        DiscordSettingsPage::Keybinds => keybinds_page(s),
        DiscordSettingsPage::ConnectedApps => connected_apps_page(s),
    };

    let mut body_wrap = col().w_full().gap(6.0);
    if let Some(ref err) = s.discord.settings_ext.error_note {
        body_wrap = body_wrap.child(
            row()
                .w_full()
                .padding(10.0)
                .radius(RADIUS_SMALL)
                .fill(bad().alpha(0.14))
                .border(1.0, bad().alpha(0.35))
                .justify_between()
                .center_y()
                .child(text(err.clone()).font(type_small()).color(FG))
                .child(
                    el().key("dc-set-err-x")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .size(22.0, 22.0)
                        .center()
                        .on_press(|s: &mut AppState| s.discord.settings_ext.error_note = None)
                        .child(icon_close(12.0, FG)),
                ),
        );
    }
    body_wrap = body_wrap.child(body_content);

    let close_btn = el()
        .key("dc-set-close")
        .role("button")
        .label("Close Discord settings")
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .pin(None, Some(14.0), Some(14.0), None)
        .size(30.0, 30.0)
        .radius(RADIUS_PILL)
        .fill(glass_still_control_fill())
        .border(1.0, hairline())
        .hover(hover_fill())
        .center()
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.close_discord_settings_clean())
        .child(icon_close(14.0, FG));

    let sheet = row()
        .key("dc-settings-modal")
        .w(820.0)
        .h(880.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .transition(modal_card_transition())
        .modal(true, |s: &mut AppState| s.close_discord_settings_clean())
        .modal_live(["top-bar"])
        .modal_scrim(scrim_fill())
        .liquid_glass(exo_kit::exo_glass(exo_kit::ExoGlass::Sheet, false))
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
                        .px(32.0)
                        .pt(26.0)
                        .pb(32.0)
                        .scroll(ScrollSpec {
                            stick_bottom: false,
                        })
                        .child(body_wrap),
                )
                .child(close_btn),
        );
    let _ = blur;

    el().key("dc-settings-layer")
        .pin(Some(0.0), Some(52.0), Some(0.0), Some(0.0))
        .center()
        .child(sheet)
}

pub fn custom_status_modal(s: &AppState, blur: bool) -> El {
    let cur_clear = s.discord.settings_ext.custom_status_clear_ms;
    let mut clear_pills = row().gap(6.0).wrap();
    for (idx, (ms, lbl)) in CLEAR_AFTER_OPTIONS.into_iter().enumerate() {
        let active = cur_clear == ms;
        clear_pills = clear_pills.child(
            filter_pill(format!("dc-cstatus-clear-{idx}"), lbl, active).on_press(
                move |s: &mut AppState| s.discord.settings_ext.custom_status_clear_ms = ms,
            ),
        );
    }

    let panel = col()
        .key("dc-cstatus-modal")
        .w(420.0)
        .padding(20.0)
        .gap(14.0)
        .radius(RADIUS_PANEL)
        .transition(modal_card_transition())
        .modal(true, |s: &mut AppState| {
            s.discord.custom_status_open = false
        })
        .modal_scrim(scrim_fill())
        .hit()
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Set your status").font(type_section()).color(FG))
                .child(
                    el().key("dc-cstatus-x")
                        .cursor(Cursor::Pointer)
                        .hit()
                        .size(28.0, 28.0)
                        .radius(RADIUS_PILL)
                        .hover(hover_fill())
                        .center()
                        .on_press(|s: &mut AppState| s.discord.custom_status_open = false)
                        .child(icon_close(14.0, FG)),
                ),
        )
        .child(
            text_input(&s.discord.custom_status_input, type_body())
                .key("dc-cstatus-input")
                .placeholder("What's going on?", fg_muted())
                .w_full()
                .h(38.0)
                .px(12.0)
                .radius(RADIUS_SMALL)
                .fill(BG)
                .border(1.0, hairline())
                .color(FG)
                .single_line()
                .on_edit(edit_access(|s: &mut AppState| {
                    &mut s.discord.custom_status_input
                }))
                .on_submit(|s: &mut AppState, _| s.save_discord_custom_status_ext(false)),
        )
        .child(
            col()
                .gap(6.0)
                .child(text("Clear after").font(type_label()).color(fg_muted()))
                .child(clear_pills),
        )
        .child(
            row()
                .w_full()
                .justify_end()
                .gap(8.0)
                .children(s.discord.custom_status.is_some().then(|| {
                    glass_button("dc-cstatus-clear", "Clear", None)
                        .on_press(|s: &mut AppState| s.save_discord_custom_status_ext(true))
                }))
                .child(
                    glass_button("dc-cstatus-cancel", "Cancel", None)
                        .on_press(|s: &mut AppState| s.discord.custom_status_open = false),
                )
                .child(
                    primary_button(
                        "dc-cstatus-save",
                        "Save",
                        Some(icon_check(14.0, PRIMARY_FG)),
                    )
                    .on_press(|s: &mut AppState| s.save_discord_custom_status_ext(false)),
                ),
        );
    let _ = blur;
    let panel = apply_glass(panel, ExoGlass::Sheet, false);

    el().key("dc-cstatus-layer")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .center()
        .child(panel)
}

#[allow(dead_code)]
fn _unused_icons_anchor() -> (El, El, El) {
    (
        icon_mic(14.0, FG),
        icon_mic_off(14.0, FG),
        icon_volume(14.0, FG),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_missing_voice_model_says_the_install_needs_repair() {
        assert_eq!(missing_detail(Some(false)), Some(MISSING_FROM_INSTALL));
        assert_eq!(missing_detail(Some(true)), None);
        assert_eq!(missing_detail(None), None);
        assert_eq!(
            MISSING_FROM_INSTALL,
            "Missing from this install. Reinstall Exo to repair it."
        );
    }

    #[test]
    fn keybind_label_orders_modifiers_first() {
        assert_eq!(keybind_label(&[]), "No keys");
        assert_eq!(keybind_label(&[77, 17, 16]), "Ctrl + Shift + M");
        assert_eq!(keybind_label(&[112, 18]), "Alt + F1");
        assert_eq!(keybind_label(&[5]), "Mouse 4");
    }

    #[test]
    fn volume_curve_round_trips_unity_and_endpoints() {
        assert_eq!(to_discord_volume_slider(0.0), 0.0);
        assert_eq!(from_discord_volume_slider(0.0), 0.0);
        assert!((to_discord_volume_slider(100.0) - 100.0).abs() < 1e-6);
        assert_eq!(from_discord_volume_slider(100.0), 100.0);
        let mid = to_discord_volume_slider(50.0);
        assert!((from_discord_volume_slider(mid) - 50.0).abs() < 0.01);
        let hi = to_discord_volume_slider(200.0);
        assert!((from_discord_volume_slider(hi) - 200.0).abs() < 0.01);
    }

    #[test]
    fn mic_advice_and_speech_time_match_react() {
        assert_eq!(speech_time(45.0), "45 s");
        assert_eq!(speech_time(120.0), "2 min");
        assert_eq!(speech_time(150.0), "2 min 30 s");
        let lvl = DiscordMicLevel {
            heard: Some(exo_rpc::DiscordMicHeard {
                speech_db: Some(-6.0),
                clipping: true,
                ..Default::default()
            }),
            ..Default::default()
        };
        assert_eq!(mic_advice(&lvl), Some("Clipping: turn its gain down"));
        assert_eq!(suppression_detail("windows"), Some("Windows is doing this"));
    }

    #[test]
    fn user_settings_patch_merges_partially() {
        let mut st = DiscordSettingsState::empty();
        assert!(!st.user.developer_mode);
        st.apply_user_settings_patch(
            &json!({ "developerMode": true, "messageDisplayCompact": true }),
        );
        assert!(st.user.developer_mode);
        assert!(st.user.message_display_compact);
        assert!(st.user.render_embeds);
    }

    #[test]
    fn keybind_event_toggles_streamer_mode() {
        let mut app = AppState::default();
        assert!(!app.discord.settings_ext.streamer_mode);
        assert!(app.handle_discord_keybind("TOGGLE_STREAMER_MODE"));
        assert!(app.discord.settings_ext.streamer_mode);
        assert!(!app.handle_discord_keybind("UNKNOWN"));
    }

    #[test]
    fn voice_loudness_clamping_and_client_patch_round_trip() {
        assert_eq!(clamp_voice_target_lufs(-30), -26);
        assert_eq!(clamp_voice_target_lufs(-20), -20);
        assert_eq!(clamp_voice_target_lufs(-10), -14);
        assert_eq!(format_voice_lufs(-18), "-18 LUFS");

        let mut app = AppState::with_demo_games();
        app.save_discord_client_settings(json!({
            "evenVoices": true,
            "voiceTargetLufs": -16,
            "micAutoGain": true,
        }));
        assert!(app.discord.settings_ext.client.even_voices);
        assert_eq!(app.discord.settings_ext.client.voice_target_lufs, -16);
        assert!(app.discord.settings_ext.client.mic_auto_gain);
    }
}
