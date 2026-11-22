use crate::state::{AppState, HostStatus};
use crate::video_decoder::{
    VideoPacket, VideoStreamHandle, VideoStreamStats, make_demo_stream_pixels,
};
use exo_rpc::{
    ActionResult, DiscordAudioDevice, DiscordAudioReply, DiscordGoLiveReply,
    DiscordGoLiveStateEvent, DiscordGoLiveStatsEvent, DiscordMyVoiceReply, DiscordSoundItem,
    DiscordSoundboardReply, DiscordVideoEvent, DiscordVideoFrame, GoLiveSource,
    METHOD_DISCORD_AUDIO, METHOD_DISCORD_CAMERA, METHOD_DISCORD_GOLIVE, METHOD_DISCORD_MY_VOICE,
    METHOD_DISCORD_SOUNDBOARD, METHOD_DISCORD_STREAM, METHOD_DISCORD_VOICE,
};
use rune::Pixels;
use serde_json::json;
use std::collections::{HashMap, HashSet};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VoicePanel {
    None,
    GoLive,
    Soundboard,
    Audio,
}

#[derive(Debug, Clone)]
pub struct UserAudioMix {
    pub volume: f32,
    pub muted: bool,
    pub stream_muted: bool,
}

impl Default for UserAudioMix {
    fn default() -> Self {
        Self {
            volume: 100.0,
            muted: false,
            stream_muted: false,
        }
    }
}

pub struct DiscordVoiceUiState {
    pub panel: VoicePanel,
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
    pub golive_height: i32,
    pub golive_fps: i32,
    pub golive_sound: bool,
    pub golive_loading: bool,
    pub camera_on: bool,
    pub soundboard_defaults: Vec<DiscordSoundItem>,
    pub soundboard_guild: Vec<DiscordSoundItem>,
    pub soundboard_loading: bool,
    pub audio: Option<DiscordAudioReply>,
    pub my_voice_learned: bool,
    pub user_mix: HashMap<String, UserAudioMix>,
    pub mix_open_user: Option<String>,
    pub stage_requested: bool,
}

impl DiscordVoiceUiState {
    pub fn empty() -> Self {
        Self {
            panel: VoicePanel::None,
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
            golive_height: 1080,
            golive_fps: 60,
            golive_sound: true,
            golive_loading: false,
            camera_on: false,
            soundboard_defaults: Vec::new(),
            soundboard_guild: Vec::new(),
            soundboard_loading: false,
            audio: None,
            my_voice_learned: false,
            user_mix: HashMap::new(),
            mix_open_user: None,
            stage_requested: false,
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
                game: true,
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
                game: false,
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
        self.discord
            .voice_ui
            .decoders
            .entry(key)
            .or_insert_with(|| VideoStreamHandle::spawn("h264"));
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
            self.discord
                .voice_ui
                .decoders
                .remove(&("stream".to_string(), uid.clone()));
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
            VoicePanel::GoLive => self.load_golive_sources(),
            VoicePanel::Soundboard => self.load_discord_soundboard(),
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
                            .find(|src| src.game)
                            .or_else(|| rep.sources.first())
                            .map(|src| src.id.clone());
                        s.discord.voice_ui.golive_sources = rep.sources;
                        if s.discord.voice_ui.golive_selected_source.is_none() {
                            s.discord.voice_ui.golive_selected_source = first;
                        }
                    } else if let Some(msg) = rep.message {
                        s.notice = Some(msg);
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
                codec: Some("h265".into()),
                sound: if sound { Some("Game audio".into()) } else { None },
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
                    s.notice = rep.message.or_else(|| Some("Could not start screen share".into()));
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
            json!({ "guildId": gid }),
            |s, res: Result<DiscordSoundboardReply, String>| {
                s.discord.voice_ui.soundboard_loading = false;
                if let Ok(rep) = res
                    && rep.ok
                {
                    if let Some(d) = rep.defaults {
                        s.discord.voice_ui.soundboard_defaults = d;
                    }
                    if let Some(g) = rep.guild {
                        s.discord.voice_ui.soundboard_guild = g;
                    }
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

    pub fn toggle_discord_stage_speak(&mut self, guild_id: &str, channel_id: &str) {
        let request = !self.discord.voice_ui.stage_requested;
        self.discord.voice_ui.stage_requested = request;
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_DISCORD_VOICE,
            json!({
                "action": "stage",
                "guildId": guild_id,
                "channelId": channel_id,
                "requestToSpeak": request,
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

    pub fn forget_discord_my_voice(&mut self) {
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
            self.discord
                .voice_ui
                .decoders
                .entry(key)
                .or_insert_with(|| VideoStreamHandle::spawn("h264"));
        } else {
            self.discord.voice_ui.active_video_feeds.remove(&key);
            self.discord.voice_ui.decoders.remove(&key);
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
            .entry(key)
            .or_insert_with(|| VideoStreamHandle::spawn(&frame.codec));
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
}
