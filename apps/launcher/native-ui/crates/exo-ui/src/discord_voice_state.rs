use crate::state::{AppState, HostStatus};
use crate::video_decoder::{
    VideoPacket, VideoStreamHandle, VideoStreamStats, make_demo_stream_pixels,
};
use exo_rpc::{
    ActionResult, DiscordAudioDevice, DiscordAudioReply, DiscordCallEvent, DiscordCallSong,
    DiscordCallSongsReply, DiscordGoLiveReply, DiscordGoLiveStateEvent, DiscordGoLiveStatsEvent,
    DiscordGuildSoundboard, DiscordMyVoiceReply, DiscordSoundItem, DiscordSoundboardReply,
    DiscordVideoEvent, DiscordVideoFrame, GoLiveSource, METHOD_DISCORD_AUDIO,
    METHOD_DISCORD_CALL_SONGS, METHOD_DISCORD_CAMERA, METHOD_DISCORD_GOLIVE,
    METHOD_DISCORD_MY_VOICE, METHOD_DISCORD_RING, METHOD_DISCORD_SOUNDBOARD, METHOD_DISCORD_STREAM,
    METHOD_DISCORD_VOICE,
};
use rune::{Pixels, TextEdit};
use serde_json::json;
use std::collections::{HashMap, HashSet};
use std::time::Instant;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VoicePanel {
    None,
    GoLive,
    Soundboard,
    Audio,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GoLiveTab {
    Applications,
    Screens,
}

#[derive(Debug, Clone)]
pub struct UserAudioMix {
    pub volume: f32,
    pub muted: bool,
    pub stream_muted: bool,
    pub soundboard_muted: bool,
}

impl Default for UserAudioMix {
    fn default() -> Self {
        Self {
            volume: 100.0,
            muted: false,
            stream_muted: false,
            soundboard_muted: false,
        }
    }
}

pub struct DiscordVoiceUiState {
    pub panel: VoicePanel,
    pub show_voice_chat: bool,
    pub watching_stream: Option<String>,
    pub fullscreen_stream: bool,
    pub active_video_feeds: HashSet<(String, String)>,
    pub decoders: HashMap<(String, String), VideoStreamHandle>,
    pub demo_stream_pixels: Option<Pixels>,
    pub golive_state: String,
    pub golive_title: Option<String>,
    pub golive_stats: Option<DiscordGoLiveStatsEvent>,
    pub golive_viewers: Vec<String>,
    pub golive_sources: Vec<GoLiveSource>,
    pub golive_selected_source: Option<String>,
    pub golive_step_chosen: Option<GoLiveSource>,
    pub golive_preview_thumb: Option<String>,
    pub golive_preview_shown: bool,
    pub golive_capture_mode: Option<String>,
    pub golive_service_disabled: bool,
    pub golive_error: Option<String>,
    pub golive_tab: GoLiveTab,
    pub golive_height: i32,
    pub golive_fps: i32,
    pub golive_sound: bool,
    pub golive_loading: bool,
    pub camera_on: bool,
    pub soundboard_defaults: Vec<DiscordSoundItem>,
    pub soundboard_guild: Vec<DiscordSoundItem>,
    pub soundboard_guilds: Vec<DiscordGuildSoundboard>,
    pub soundboard_query: TextEdit,
    pub soundboard_error: Option<String>,
    pub soundboard_loading: bool,
    pub call_songs: Vec<DiscordCallSong>,
    pub ringing_channel_id: Option<String>,
    pub audio: Option<DiscordAudioReply>,
    pub my_voice_learned: bool,
    pub user_mix: HashMap<String, UserAudioMix>,
    pub stage_requested: bool,
    pub stage_editing_mode: Option<&'static str>,
    pub stage_topic_input: TextEdit,
    pub stage_end_confirm: bool,
    pub stage_error: Option<String>,
    pub voice_error: Option<String>,
    pub stage_size: (f32, f32),
    pub voice_connected_since: Option<Instant>,
}

impl DiscordVoiceUiState {
    pub fn empty() -> Self {
        Self {
            panel: VoicePanel::None,
            show_voice_chat: true,
            watching_stream: None,
            fullscreen_stream: false,
            active_video_feeds: HashSet::new(),
            decoders: HashMap::new(),
            demo_stream_pixels: None,
            golive_state: "off".into(),
            golive_title: None,
            golive_stats: None,
            golive_viewers: Vec::new(),
            golive_sources: Vec::new(),
            golive_selected_source: None,
            golive_step_chosen: None,
            golive_preview_thumb: None,
            golive_preview_shown: true,
            golive_capture_mode: None,
            golive_service_disabled: false,
            golive_error: None,
            golive_tab: GoLiveTab::Applications,
            golive_height: 1080,
            golive_fps: 60,
            golive_sound: true,
            golive_loading: false,
            camera_on: false,
            soundboard_defaults: Vec::new(),
            soundboard_guild: Vec::new(),
            soundboard_guilds: Vec::new(),
            soundboard_query: TextEdit::default(),
            soundboard_error: None,
            soundboard_loading: false,
            call_songs: Vec::new(),
            ringing_channel_id: None,
            audio: None,
            my_voice_learned: false,
            user_mix: HashMap::new(),
            stage_requested: false,
            stage_editing_mode: None,
            stage_topic_input: TextEdit::default(),
            stage_end_confirm: false,
            stage_error: None,
            voice_error: None,
            stage_size: (0.0, 0.0),
            voice_connected_since: None,
        }
    }

    pub fn demo() -> Self {
        let mut s = Self::empty();
        s.watching_stream = Some("200002".into());
        s.demo_stream_pixels = Some(make_demo_stream_pixels(640, 360));
        s.golive_sources = vec![
            GoLiveSource {
                id: "game:cs2".into(),
                kind: "window".into(),
                title: "Counter-Strike 2".into(),
                app: Some("cs2.exe".into()),
                icon: None,
                thumb: None,
                game: Some("Counter-Strike 2".into()),
                width: 2560,
                height: 1440,
                hz: 240,
                primary: true,
            },
            GoLiveSource {
                id: "screen:0".into(),
                kind: "screen".into(),
                title: "Display 1 (Primary)".into(),
                app: None,
                icon: None,
                thumb: None,
                game: None,
                width: 2560,
                height: 1440,
                hz: 240,
                primary: true,
            },
        ];
        s.golive_selected_source = Some("game:cs2".into());
        s.soundboard_defaults = vec![
            DiscordSoundItem {
                sound_id: "1".into(),
                name: "Airhorn".into(),
                volume: 0.8,
                emoji_name: Some("📯".into()),
                ..Default::default()
            },
            DiscordSoundItem {
                sound_id: "2".into(),
                name: "Ba Dum Tss".into(),
                volume: 0.8,
                emoji_name: Some("🥁".into()),
                ..Default::default()
            },
            DiscordSoundItem {
                sound_id: "3".into(),
                name: "Golf Clap".into(),
                volume: 0.8,
                emoji_name: Some("👏".into()),
                ..Default::default()
            },
            DiscordSoundItem {
                sound_id: "4".into(),
                name: "Quack".into(),
                volume: 0.8,
                emoji_name: Some("🦆".into()),
                ..Default::default()
            },
        ];
        s.soundboard_guild = vec![
            DiscordSoundItem {
                sound_id: "101".into(),
                name: "Ace Clutch".into(),
                volume: 0.9,
                emoji_name: Some("🔥".into()),
                guild_id: Some("g-exo".into()),
                ..Default::default()
            },
            DiscordSoundItem {
                sound_id: "102".into(),
                name: "GG EZ".into(),
                volume: 0.9,
                emoji_name: Some("🏆".into()),
                guild_id: Some("g-exo".into()),
                ..Default::default()
            },
        ];
        s.audio = Some(DiscordAudioReply {
            inputs: vec![
                DiscordAudioDevice {
                    id: "mic-1".into(),
                    name: "Shure MV7 USB".into(),
                },
                DiscordAudioDevice {
                    id: "mic-2".into(),
                    name: "Default Microphone".into(),
                },
            ],
            outputs: vec![
                DiscordAudioDevice {
                    id: "out-1".into(),
                    name: "Sennheiser HD 660S2".into(),
                },
                DiscordAudioDevice {
                    id: "out-2".into(),
                    name: "Monitor Audio".into(),
                },
            ],
            input: Some("mic-1".into()),
            output: Some("out-1".into()),
            input_volume: 100.0,
            output_volume: 100.0,
            soundboard_volume: 80.0,
            soundboard_muted: false,
        });
        s.my_voice_learned = true;
        s
    }

    pub fn stream_pixels_for(&self, user_id: &str) -> Option<(Pixels, VideoStreamStats)> {
        let key = ("stream".to_string(), user_id.to_string());
        if let Some(dec) = self.decoders.get(&key) {
            return Some((dec.pixels.clone(), dec.snapshot_stats()));
        }
        if self.watching_stream.as_deref() == Some(user_id)
            && let Some(ref demo) = self.demo_stream_pixels
        {
            return Some((
                demo.clone(),
                VideoStreamStats {
                    codec: "h265".into(),
                    width: 1920,
                    height: 1080,
                    decoded_frames: 3600,
                    dropped_frames: 0,
                    fps: 60.0,
                    hardware: true,
                    error: None,
                },
            ));
        }
        None
    }
}

impl AppState {
    pub fn announce_hardware_video_decode(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_STREAM,
            json!({ "action": "decode", "hevc": true }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn watch_discord_stream(&mut self, user_id: &str) {
        let uid = user_id.to_string();
        self.discord.voice_ui.watching_stream = Some(uid.clone());
        let key = ("stream".to_string(), uid.clone());
        let handle = self
            .discord
            .voice_ui
            .decoders
            .entry(key.clone())
            .or_insert_with(|| VideoStreamHandle::spawn("h264"))
            .clone();
        self.video_router.register(key, handle);
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            self.call_host_async(
                METHOD_DISCORD_STREAM,
                json!({ "action": "frames", "on": true }),
                |_s, _res: Result<ActionResult, String>| {},
            );
            self.call_host_async(
                METHOD_DISCORD_STREAM,
                json!({ "action": "watch", "userId": uid }),
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
    }

    pub fn stop_watching_discord_stream(&mut self) {
        let prev = self.discord.voice_ui.watching_stream.take();
        self.discord.voice_ui.fullscreen_stream = false;
        if let Some(ref uid) = prev {
            let key = ("stream".to_string(), uid.clone());
            self.discord.voice_ui.decoders.remove(&key);
            self.video_router.remove(&key);
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        {
            self.call_host_async(
                METHOD_DISCORD_STREAM,
                json!({ "action": "stop", "userId": prev }),
                |_s, _res: Result<ActionResult, String>| {},
            );
            if self.discord.voice_ui.decoders.is_empty() {
                self.call_host_async(
                    METHOD_DISCORD_STREAM,
                    json!({ "action": "frames", "on": false }),
                    |_s, _res: Result<ActionResult, String>| {},
                );
            }
        }
    }

    pub fn open_voice_panel(&mut self, panel: VoicePanel) {
        if self.discord.voice_ui.panel == panel {
            self.discord.voice_ui.panel = VoicePanel::None;
            return;
        }
        self.discord.voice_ui.panel = panel;
        match panel {
            VoicePanel::GoLive => {
                self.discord.voice_ui.golive_step_chosen = None;
                self.discord.voice_ui.golive_error = None;
                self.load_golive_sources();
            }
            VoicePanel::Soundboard => {
                self.load_discord_soundboard();
                self.load_discord_audio();
            }
            VoicePanel::Audio => self.load_discord_audio(),
            VoicePanel::None => {}
        }
    }

    pub fn load_golive_sources(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.voice_ui.golive_loading = true;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_GOLIVE,
            json!({ "action": "sources" }),
            |s, res: Result<DiscordGoLiveReply, String>| {
                s.discord.voice_ui.golive_loading = false;
                if let Ok(rep) = res {
                    if rep.ok {
                        let first = rep
                            .sources
                            .iter()
                            .find(|src| src.game.is_some())
                            .or_else(|| rep.sources.first())
                            .map(|src| src.id.clone());
                        s.discord.voice_ui.golive_capture_mode = rep.capture_mode;
                        s.discord.voice_ui.golive_service_disabled = rep.service_disabled;
                        s.discord.voice_ui.golive_sources = rep.sources;
                        s.discord.voice_ui.golive_error = None;
                        if s.discord.voice_ui.golive_selected_source.is_none() {
                            s.discord.voice_ui.golive_selected_source = first;
                        }
                    } else if let Some(msg) = rep.message {
                        s.discord.voice_ui.golive_error = Some(msg);
                    }
                }
            },
        );
    }

    pub fn start_discord_golive(&mut self) {
        let Some(source_id) = self.discord.voice_ui.golive_selected_source.clone() else {
            self.notice = Some("Pick a screen or window to share".into());
            return;
        };
        let height = self.discord.voice_ui.golive_height;
        let fps = self.discord.voice_ui.golive_fps;
        let sound = self.discord.voice_ui.golive_sound;
        if self.status != HostStatus::Connected {
            let title = self
                .discord
                .voice_ui
                .golive_sources
                .iter()
                .find(|src| src.id == source_id)
                .map(|src| src.title.clone())
                .unwrap_or_else(|| "Screen".into());
            self.discord.voice_ui.golive_state = "live".into();
            self.discord.voice_ui.golive_title = Some(title);
            self.discord.voice_ui.golive_stats = Some(DiscordGoLiveStatsEvent {
                width: if height == 720 { 1280 } else { 1920 },
                height: if height == 0 { 1440 } else { height },
                fps: fps as f64,
                bitrate: 6200.0,
                max: Some(8_000_000.0),
                codec: Some("h265".into()),
                sound: if sound {
                    Some("Game audio".into())
                } else {
                    None
                },
            });
            self.discord.voice_ui.panel = VoicePanel::None;
            return;
        }
        self.discord.voice_ui.golive_state = "connecting".into();
        self.discord.voice_ui.panel = VoicePanel::None;
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_GOLIVE,
            json!({
                "action": "start",
                "source": source_id,
                "height": height,
                "fps": fps,
                "sound": sound,
            }),
            |s, res: Result<ActionResult, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                {
                    s.discord.voice_ui.golive_state = "off".into();
                    s.notice = rep
                        .message
                        .or_else(|| Some("Could not start screen share".into()));
                }
            },
        );
    }

    pub fn stop_discord_golive(&mut self) {
        self.discord.voice_ui.golive_state = "off".into();
        self.discord.voice_ui.golive_title = None;
        self.discord.voice_ui.golive_stats = None;
        self.discord.voice_ui.golive_viewers.clear();
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_GOLIVE,
            json!({ "action": "stop" }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn toggle_discord_camera(&mut self) {
        let next = !self.discord.voice_ui.camera_on;
        self.discord.voice_ui.camera_on = next;
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CAMERA,
            if next {
                json!({ "action": "start", "width": 1280, "height": 720, "fps": 30 })
            } else {
                json!({ "action": "stop" })
            },
            |s, res: Result<ActionResult, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                {
                    s.discord.voice_ui.camera_on = false;
                    s.notice = rep.message;
                }
            },
        );
    }

    pub fn load_discord_soundboard(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        self.discord.voice_ui.soundboard_loading = true;
        let gid = self
            .discord
            .voice
            .guild_id
            .clone()
            .or_else(|| self.discord.active_guild_id.clone());
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SOUNDBOARD,
            json!({ "action": "list", "guildId": gid }),
            |s, res: Result<DiscordSoundboardReply, String>| {
                s.discord.voice_ui.soundboard_loading = false;
                match res {
                    Ok(rep) if rep.ok => {
                        s.discord.voice_ui.soundboard_defaults = rep.defaults;
                        s.discord.voice_ui.soundboard_guild = rep.guild;
                        s.discord.voice_ui.soundboard_guilds = rep
                            .guilds
                            .into_iter()
                            .filter(|g| !g.items.is_empty())
                            .collect();
                        s.discord.voice_ui.soundboard_error = None;
                    }
                    Ok(rep) => {
                        s.discord.voice_ui.soundboard_error = rep
                            .message
                            .or_else(|| Some("Discord didn't answer.".into()));
                    }
                    Err(e) => s.discord.voice_ui.soundboard_error = Some(e),
                }
            },
        );
    }

    pub fn play_discord_sound(&mut self, sound: &DiscordSoundItem) {
        let Some(cid) = self.discord.voice.channel_id.clone() else {
            self.notice = Some("Join a voice channel to play soundboard clips".into());
            return;
        };
        if self.status != HostStatus::Connected {
            self.notice = Some(format!("Played sound: {}", sound.name));
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_SOUNDBOARD,
            json!({
                "action": "play",
                "channelId": cid,
                "soundId": sound.sound_id,
                "guildId": sound.guild_id,
                "emojiId": sound.emoji_id,
                "emojiName": sound.emoji_name,
                "volume": sound.volume.clamp(0.0, 1.0),
            }),
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

    pub fn load_discord_audio(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_AUDIO,
            json!({}),
            |s, res: Result<DiscordAudioReply, String>| {
                if let Ok(rep) = res {
                    s.discord.voice_ui.audio = Some(rep);
                }
            },
        );
    }

    pub fn set_discord_audio(&mut self, payload: serde_json::Value) {
        if let Some(ref mut a) = self.discord.voice_ui.audio {
            if let Some(v) = payload.get("input").and_then(|v| v.as_str()) {
                a.input = Some(v.to_string());
            }
            if let Some(v) = payload.get("output").and_then(|v| v.as_str()) {
                a.output = Some(v.to_string());
            }
            if let Some(v) = payload.get("inputVolume").and_then(|v| v.as_f64()) {
                a.input_volume = v;
            }
            if let Some(v) = payload.get("outputVolume").and_then(|v| v.as_f64()) {
                a.output_volume = v;
            }
            if let Some(v) = payload.get("soundboardVolume").and_then(|v| v.as_f64()) {
                a.soundboard_volume = v;
            }
            if let Some(v) = payload.get("soundboardMuted").and_then(|v| v.as_bool()) {
                a.soundboard_muted = v;
            }
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_AUDIO,
            payload,
            |s, res: Result<DiscordAudioReply, String>| {
                if let Ok(rep) = res {
                    s.discord.voice_ui.audio = Some(rep);
                }
            },
        );
    }

    pub fn set_discord_user_mix(
        &mut self,
        user_id: &str,
        volume: Option<f32>,
        muted: Option<bool>,
        stream_muted: Option<bool>,
    ) {
        let mix = self
            .discord
            .voice_ui
            .user_mix
            .entry(user_id.to_string())
            .or_default();
        if let Some(v) = volume {
            mix.volume = v.clamp(0.0, 200.0);
        }
        if let Some(m) = muted {
            mix.muted = m;
        }
        if let Some(sm) = stream_muted {
            mix.stream_muted = sm;
        }
        let cur_vol = mix.volume;
        let cur_muted = mix.muted;
        let is_stream = stream_muted.is_some();
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_VOICE,
            json!({
                "action": "user",
                "userId": user_id,
                "volume": cur_vol,
                "muted": if is_stream { stream_muted.unwrap_or(false) } else { cur_muted },
                "stream": is_stream,
            }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    fn _forget_discord_my_voice_unused(&mut self) {
        self.discord.voice_ui.my_voice_learned = false;
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_MY_VOICE,
            json!({ "action": "forget" }),
            |s, res: Result<DiscordMyVoiceReply, String>| {
                if let Ok(rep) = res {
                    s.discord.voice_ui.my_voice_learned = rep.learned;
                }
            },
        );
    }

    pub fn handle_discord_video_event(&mut self, ev: DiscordVideoEvent) {
        let key = (ev.source.clone(), ev.user_id.clone());
        if ev.active {
            self.discord.voice_ui.active_video_feeds.insert(key.clone());
            let handle = self
                .discord
                .voice_ui
                .decoders
                .entry(key.clone())
                .or_insert_with(|| VideoStreamHandle::spawn("h264"))
                .clone();
            self.video_router.register(key, handle);
        } else {
            self.discord.voice_ui.active_video_feeds.remove(&key);
            self.discord.voice_ui.decoders.remove(&key);
            self.video_router.remove(&key);
            if ev.source == "stream"
                && self.discord.voice_ui.watching_stream.as_deref() == Some(ev.user_id.as_str())
            {
                self.discord.voice_ui.watching_stream = None;
                self.discord.voice_ui.fullscreen_stream = false;
            }
        }
    }

    pub fn handle_discord_video_frame(&mut self, frame: DiscordVideoFrame) {
        let key = (frame.source.clone(), frame.user_id.clone());
        let handle = self
            .discord
            .voice_ui
            .decoders
            .entry(key.clone())
            .or_insert_with(|| VideoStreamHandle::spawn(&frame.codec))
            .clone();
        self.video_router.register(key, handle.clone());
        handle.push(VideoPacket {
            data: frame.data,
            timestamp_ms: frame.timestamp,
            keyframe: frame.keyframe,
            flip: frame.flip,
            codec: frame.codec,
        });
    }

    pub fn handle_discord_golive_state(&mut self, ev: DiscordGoLiveStateEvent) {
        self.discord.voice_ui.golive_state = ev.state;
        self.discord.voice_ui.golive_title = ev.title;
        if let Some(msg) = ev.message {
            self.notice = Some(msg);
        }
    }

    pub fn handle_discord_golive_stats(&mut self, ev: DiscordGoLiveStatsEvent) {
        self.discord.voice_ui.golive_stats = Some(ev);
    }

    pub fn handle_discord_golive_viewers(&mut self, ids: Vec<String>) {
        self.discord.voice_ui.golive_viewers = ids;
    }

    pub fn toggle_voice_chat(&mut self) {
        self.discord.voice_ui.show_voice_chat = !self.discord.voice_ui.show_voice_chat;
        if self.discord.voice_ui.show_voice_chat
            && let Some(cid) = self.discord.active_channel_id.clone()
            && !self.discord.chat.messages_by_channel.contains_key(&cid)
        {
            self.load_discord_messages(&cid, None);
        }
    }

    pub fn set_user_soundboard_mute(&mut self, user_id: &str, muted: bool) {
        let entry = self
            .discord
            .voice_ui
            .user_mix
            .entry(user_id.to_string())
            .or_default();
        entry.soundboard_muted = muted;
    }

    pub fn open_active_voice_channel(&mut self) {
        let gid = self.discord.voice.guild_id.clone();
        let cid = self.discord.voice.channel_id.clone();
        self.switch_room(crate::state::Room::Discord);
        if let Some(cid) = cid {
            if let Some(gid) = gid {
                self.open_discord_guild(&gid);
                self.open_discord_channel(&cid);
            } else {
                self.open_discord_dm(&cid);
            }
        }
    }

    pub fn pick_golive_source(&mut self, source: GoLiveSource) {
        self.discord.voice_ui.golive_selected_source = Some(source.id.clone());
        self.discord.voice_ui.golive_preview_thumb = source.thumb.clone();
        let src_id = source.id.clone();
        self.discord.voice_ui.golive_step_chosen = Some(source);
        self.discord.voice_ui.golive_error = None;
        if self.status == HostStatus::Connected {
            #[cfg(windows)]
            self.call_host_async(
                METHOD_DISCORD_GOLIVE,
                json!({ "action": "preview", "source": src_id, "width": 960 }),
                |s, res: Result<DiscordGoLiveReply, String>| {
                    if let Ok(rep) = res
                        && rep.ok
                        && rep.thumb.is_some()
                    {
                        s.discord.voice_ui.golive_preview_thumb = rep.thumb;
                    }
                },
            );
        }
    }

    pub fn load_discord_call_songs(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_CALL_SONGS,
            json!({}),
            |s, res: Result<DiscordCallSongsReply, String>| {
                if let Ok(rep) = res {
                    s.discord.voice_ui.call_songs = rep
                        .songs
                        .into_iter()
                        .filter(|song| song.rich.is_some() || song.paused)
                        .collect();
                }
            },
        );
    }

    pub fn handle_discord_call_event(&mut self, ev: DiscordCallEvent) {
        if ev.ringing {
            self.discord.voice_ui.ringing_channel_id = Some(ev.channel_id);
        } else if self.discord.voice_ui.ringing_channel_id.as_deref()
            == Some(ev.channel_id.as_str())
        {
            self.discord.voice_ui.ringing_channel_id = None;
        }
    }

    pub fn handle_discord_call_song(&mut self, song: DiscordCallSong) {
        let keep = song.rich.is_some() || song.paused;
        self.discord
            .voice_ui
            .call_songs
            .retain(|x| x.user_id != song.user_id);
        if keep {
            self.discord.voice_ui.call_songs.push(song);
        }
    }

    pub fn handle_discord_camera_event(&mut self, cam_state: &str) {
        self.discord.voice_ui.camera_on = matches!(cam_state, "live" | "connecting");
        if cam_state == "live" {
            self.play_discord_ui_sound("camera_on", false);
        } else if cam_state == "off" {
            self.play_discord_ui_sound("camera_off", false);
        }
    }

    pub fn decline_discord_call(&mut self, channel_id: &str) {
        if self.discord.voice_ui.ringing_channel_id.as_deref() == Some(channel_id) {
            self.discord.voice_ui.ringing_channel_id = None;
        }
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_RING,
            json!({ "channelId": channel_id, "decline": true }),
            |_s, _res: Result<ActionResult, String>| {},
        );
    }

    pub fn set_discord_stage_state(
        &mut self,
        guild_id: &str,
        channel_id: &str,
        user_id: Option<&str>,
        suppress: Option<bool>,
        request_to_speak: Option<bool>,
    ) {
        if let Some(req) = request_to_speak {
            self.discord.voice_ui.stage_requested = req;
        }
        self.discord.voice_ui.stage_error = None;
        if self.status != HostStatus::Connected {
            return;
        }
        let mut body = json!({
            "action": "stage",
            "guildId": guild_id,
            "channelId": channel_id,
        });
        if let Some(uid) = user_id {
            body["userId"] = json!(uid);
        }
        if let Some(sup) = suppress {
            body["suppress"] = json!(sup);
        }
        if let Some(req) = request_to_speak {
            body["requestToSpeak"] = json!(req);
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_VOICE,
            body,
            |s, res: Result<ActionResult, String>| {
                if let Ok(rep) = res
                    && !rep.ok
                {
                    s.discord.voice_ui.stage_error = rep
                        .message
                        .or_else(|| Some("Discord didn't update the stage.".into()));
                }
            },
        );
    }

    pub fn manage_discord_stage(&mut self, action: &str, channel_id: &str, topic: Option<String>) {
        self.discord.voice_ui.stage_error = None;
        self.discord.voice_ui.stage_end_confirm = false;
        if self.status != HostStatus::Connected {
            self.discord.voice_ui.stage_editing_mode = None;
            return;
        }
        let mut body = json!({ "action": action, "channelId": channel_id });
        if let Some(t) = topic {
            body["topic"] = json!(t);
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_VOICE,
            body,
            |s, res: Result<ActionResult, String>| match res {
                Ok(rep) if rep.ok => {
                    s.discord.voice_ui.stage_editing_mode = None;
                }
                Ok(rep) => {
                    s.discord.voice_ui.stage_error = rep
                        .message
                        .or_else(|| Some("Discord didn't update the stage.".into()));
                }
                Err(e) => s.discord.voice_ui.stage_error = Some(e),
            },
        );
    }

    pub fn voice_duration_label(&self) -> Option<String> {
        let since = self.discord.voice_ui.voice_connected_since?;
        let total = since.elapsed().as_secs();
        let h = total / 3600;
        let m = (total / 60) % 60;
        let s = total % 60;
        if h > 0 {
            Some(format!("{h}:{m:02}:{s:02}"))
        } else {
            Some(format!("{m}:{s:02}"))
        }
    }
}
