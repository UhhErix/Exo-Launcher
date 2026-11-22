use crate::state::{AppState, HostStatus};
use exo_rpc::{
    ActionResult, CaptureItem, CaptureListReply, CaptureLiveState, CaptureSettingsDto,
    CaptureStatusReply, METHOD_APPLEMUSIC_START, METHOD_CAPTURE_ACT, METHOD_CAPTURE_EDIT,
    METHOD_CAPTURE_ITEM, METHOD_CAPTURE_LIST, METHOD_CAPTURE_SETTINGS, METHOD_CAPTURE_STATUS,
    METHOD_MUSIC_CONTROL, METHOD_MUSIC_PLAYBACK, METHOD_MUSIC_SIGN_IN, METHOD_MUSIC_VOLUME,
    METHOD_SPOTIFY_START, METHOD_YTMUSIC_START, MusicPlayback, MusicPlaybackReply,
    MusicVolumeReply,
};
use rune::Player;
use serde_json::json;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MusicService {
    System,
    Spotify,
    Apple,
    YouTube,
}

impl MusicService {
    pub const ALL: [Self; 4] = [Self::System, Self::Spotify, Self::Apple, Self::YouTube];

    pub fn key(self) -> &'static str {
        match self {
            Self::System => "music-svc-system",
            Self::Spotify => "music-svc-spotify",
            Self::Apple => "music-svc-apple",
            Self::YouTube => "music-svc-youtube",
        }
    }

    pub fn id(self) -> &'static str {
        match self {
            Self::System => "system",
            Self::Spotify => "spotify",
            Self::Apple => "apple",
            Self::YouTube => "youtube",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::System => "System Media",
            Self::Spotify => "Spotify",
            Self::Apple => "Apple Music",
            Self::YouTube => "YouTube Music",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureFilter {
    All,
    Videos,
    Screenshots,
    Favorites,
}

pub struct MediaState {
    pub music_service: MusicService,
    pub music_immersion: bool,
    pub playback: Option<MusicPlayback>,
    pub music_volume: f64,
    pub music_muted: bool,

    pub captures_open: bool,
    pub capture_filter: CaptureFilter,
    pub capture_game_filter: Option<String>,
    pub capture_status: Option<CaptureStatusReply>,
    pub capture_items: Vec<CaptureItem>,
    pub captures_loading: bool,
    pub open_capture_id: Option<String>,
    pub clip_player: Option<Player>,
    pub clip_playing: bool,
    pub clip_muted: bool,
    pub trim_start_pct: f32,
    pub trim_end_pct: f32,
}

impl MediaState {
    pub fn demo() -> Self {
        Self {
            music_service: MusicService::Spotify,
            music_immersion: false,
            playback: Some(MusicPlayback {
                provider: "spotify".into(),
                source: "session".into(),
                status: "playing".into(),
                title: "Midnight City".into(),
                artist: "M83".into(),
                album: "Hurry Up, We're Dreaming".into(),
                artwork_url: None,
                artwork_data_url: None,
                position_ms: Some(114_000),
                duration_ms: Some(243_000),
                can_play: true,
                can_pause: true,
                can_prev: true,
                can_next: true,
                can_seek: true,
            }),
            music_volume: 0.78,
            music_muted: false,
            captures_open: false,
            capture_filter: CaptureFilter::All,
            capture_game_filter: None,
            capture_status: Some(CaptureStatusReply {
                ok: true,
                present: true,
                folder: Some("Videos\\Exo Captures".into()),
                state: Some(CaptureLiveState {
                    replay: true,
                    recording: false,
                    present: true,
                }),
                settings: Some(CaptureSettingsDto {
                    quality: 60,
                    replay_seconds: 60,
                    max_fps: 120,
                    replay_mode: "games".into(),
                    cursor: true,
                    save_replay_keys: "Ctrl+Alt+F10".into(),
                    record_keys: "Ctrl+Alt+F9".into(),
                    snip_keys: "Ctrl+Alt+F8".into(),
                    auto_shrink: true,
                }),
            }),
            capture_items: vec![
                CaptureItem {
                    id: "cap0000000000001".into(),
                    kind: "video".into(),
                    game: "Counter-Strike 2".into(),
                    game_id: Some("demo:cs2".into()),
                    name: "Mirage 1v4 Clutch Replay".into(),
                    created: "2026-11-22T02:14:00Z".into(),
                    size: 64 * 1024 * 1024,
                    duration_ms: Some(42_000.0),
                    favourite: true,
                    url: String::new(),
                    thumb: None,
                    derived: false,
                },
                CaptureItem {
                    id: "cap0000000000002".into(),
                    kind: "video".into(),
                    game: "Cyberpunk 2077".into(),
                    game_id: Some("demo:cyberpunk".into()),
                    name: "Night City Ray-Traced Drift".into(),
                    created: "2026-11-21T23:40:00Z".into(),
                    size: 118 * 1024 * 1024,
                    duration_ms: Some(60_000.0),
                    favourite: false,
                    url: String::new(),
                    thumb: None,
                    derived: false,
                },
                CaptureItem {
                    id: "cap0000000000003".into(),
                    kind: "image".into(),
                    game: "Elden Ring".into(),
                    game_id: Some("demo:elden".into()),
                    name: "Leyndell Golden Vista".into(),
                    created: "2026-11-21T20:10:00Z".into(),
                    size: 4_800_000,
                    duration_ms: None,
                    favourite: true,
                    url: String::new(),
                    thumb: None,
                    derived: false,
                },
                CaptureItem {
                    id: "cap0000000000004".into(),
                    kind: "video".into(),
                    game: "Valorant".into(),
                    game_id: Some("demo:valorant".into()),
                    name: "Ascent Operator Ace".into(),
                    created: "2026-11-20T19:05:00Z".into(),
                    size: 51 * 1024 * 1024,
                    duration_ms: Some(28_000.0),
                    favourite: false,
                    url: String::new(),
                    thumb: None,
                    derived: false,
                },
            ],
            captures_loading: false,
            open_capture_id: None,
            clip_player: None,
            clip_playing: false,
            clip_muted: false,
            trim_start_pct: 0.0,
            trim_end_pct: 1.0,
        }
    }

    pub fn empty_for_host() -> Self {
        Self {
            music_service: MusicService::System,
            music_immersion: false,
            playback: None,
            music_volume: 0.75,
            music_muted: false,
            captures_open: false,
            capture_filter: CaptureFilter::All,
            capture_game_filter: None,
            capture_status: None,
            capture_items: Vec::new(),
            captures_loading: false,
            open_capture_id: None,
            clip_player: None,
            clip_playing: false,
            clip_muted: false,
            trim_start_pct: 0.0,
            trim_end_pct: 1.0,
        }
    }
}

impl AppState {
    pub fn load_music_room(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, MusicPlaybackReply>(METHOD_MUSIC_PLAYBACK, &json!({}), move |res| {
                if let Ok(reply) = res {
                    h1.update(move |s, _| s.media.playback = reply.playback);
                }
            });
            rpc.call::<_, MusicVolumeReply>(METHOD_MUSIC_VOLUME, &json!({}), move |res| {
                if let Ok(v) = res {
                    handle.update(move |s, _| {
                        if let Some(lvl) = v.level {
                            s.media.music_volume = lvl;
                        }
                        s.media.music_muted = v.muted;
                    });
                }
            });
        }
    }

    pub fn select_music_service(&mut self, svc: MusicService) {
        self.media.music_service = svc;
        if self.status == HostStatus::Standalone {
            return;
        }
        let method = match svc {
            MusicService::Spotify => Some(METHOD_SPOTIFY_START),
            MusicService::Apple => Some(METHOD_APPLEMUSIC_START),
            MusicService::YouTube => Some(METHOD_YTMUSIC_START),
            MusicService::System => None,
        };
        #[cfg(windows)]
        if let (Some(m), Some(rpc), Some(handle)) = (method, self.rpc.clone(), self.handle.clone()) {
            rpc.call_raw(m, &json!({}), move |_| {
                handle.update(|s, _| s.load_music_room());
            });
        } else {
            self.load_music_room();
        }
    }

    pub fn music_control(&mut self, action: &str) {
        if let Some(ref mut pb) = self.media.playback {
            match action {
                "toggle" => {
                    pb.status = if pb.is_playing() { "paused".into() } else { "playing".into() };
                }
                "play" => pb.status = "playing".into(),
                "pause" => pb.status = "paused".into(),
                _ => {}
            }
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_MUSIC_CONTROL,
                &json!({ "action": action }),
                move |res| {
                    handle.update(move |s, _| {
                        if let Ok(r) = res
                            && !r.ok
                            && let Some(msg) = r.message
                        {
                            s.notice = Some(msg);
                        }
                        s.load_music_room();
                    });
                },
            );
        }
    }

    pub fn music_set_volume(&mut self, level: f64, muted: Option<bool>) {
        let clamped = level.clamp(0.0, 1.0);
        self.media.music_volume = clamped;
        if let Some(m) = muted {
            self.media.music_muted = m;
        } else {
            self.media.music_muted = clamped <= 0.0;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            let params = match muted {
                Some(m) => json!({ "muted": m }),
                None => json!({ "level": clamped }),
            };
            rpc.call_raw(METHOD_MUSIC_VOLUME, &params, |_| {});
        }
    }

    pub fn music_sign_in(&mut self) {
        let svc = self.media.music_service.id();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_MUSIC_SIGN_IN, &json!({ "service": svc }), move |res| {
                if let Ok(r) = res
                    && let Some(msg) = r.message
                {
                    handle.update(move |s, _| s.notice = Some(msg));
                }
            });
        }
    }

    pub fn open_captures(&mut self, game_id: Option<String>) {
        self.media.captures_open = true;
        self.media.capture_game_filter = game_id;
        self.media.open_capture_id = None;
        self.media.clip_player = None;
        self.load_captures();
    }

    pub fn close_captures(&mut self) {
        self.media.captures_open = false;
        self.media.open_capture_id = None;
        self.media.clip_player = None;
    }

    pub fn load_captures(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.media.captures_loading = true;
            let h1 = handle.clone();
            rpc.call::<_, CaptureStatusReply>(METHOD_CAPTURE_STATUS, &json!({}), move |res| {
                if let Ok(st) = res {
                    h1.update(move |s, _| s.media.capture_status = Some(st));
                }
            });
            let params = match &self.media.capture_game_filter {
                Some(gid) => json!({ "gameId": gid, "limit": 60 }),
                None => json!({ "limit": 60 }),
            };
            rpc.call::<_, CaptureListReply>(METHOD_CAPTURE_LIST, &params, move |res| {
                handle.update(move |s, _| {
                    s.media.captures_loading = false;
                    if let Ok(list) = res {
                        s.media.capture_items = list.items;
                    }
                });
            });
        }
    }

    pub fn open_capture_item(&mut self, id: &str) {
        self.media.open_capture_id = Some(id.to_string());
        self.media.trim_start_pct = 0.0;
        self.media.trim_end_pct = 1.0;
        self.media.clip_player = None;
        self.media.clip_playing = false;

        if let Some(item) = self.media.capture_items.iter().find(|c| c.id == id)
            && item.kind == "video"
            && let Some(path) = self.art_base.resolve_capture_file(&item.url)
        {
            let player = Player::file(path);
            player.set_muted(self.media.clip_muted);
            player.set_looping(true);
            player.play();
            self.media.clip_player = Some(player);
            self.media.clip_playing = true;
        }
    }

    pub fn close_capture_item(&mut self) {
        if let Some(ref p) = self.media.clip_player {
            p.pause();
        }
        self.media.clip_player = None;
        self.media.clip_playing = false;
        self.media.open_capture_id = None;
    }

    pub fn toggle_clip_play(&mut self) {
        if let Some(ref p) = self.media.clip_player {
            if self.media.clip_playing {
                p.pause();
                self.media.clip_playing = false;
            } else {
                p.play();
                self.media.clip_playing = true;
            }
        }
    }

    pub fn toggle_clip_mute(&mut self) {
        self.media.clip_muted = !self.media.clip_muted;
        if let Some(ref p) = self.media.clip_player {
            p.set_muted(self.media.clip_muted);
        }
    }

    pub fn capture_act(&mut self, action: &str) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_CAPTURE_ACT, &json!({ "action": action }), move |res| {
                handle.update(move |s, _| {
                    if let Ok(r) = res
                        && let Some(msg) = r.message
                    {
                        s.notice = Some(msg);
                    }
                    s.load_captures();
                });
            });
        }
    }

    pub fn capture_set_replay_mode(&mut self, mode: &str) {
        if let Some(ref mut st) = self.media.capture_status
            && let Some(ref mut cfg) = st.settings
        {
            cfg.replay_mode = mode.to_string();
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CaptureStatusReply>(
                METHOD_CAPTURE_SETTINGS,
                &json!({ "replayMode": mode }),
                move |res| {
                    if let Ok(st) = res {
                        handle.update(move |s, _| s.media.capture_status = Some(st));
                    }
                },
            );
        }
    }

    pub fn capture_item_action(&mut self, id: &str, action: &str) {
        if action == "favourite" || action == "unfavourite" {
            let fav = action == "favourite";
            if let Some(item) = self.media.capture_items.iter_mut().find(|c| c.id == id) {
                item.favourite = fav;
            }
        } else if action == "delete" {
            self.media.capture_items.retain(|c| c.id != id);
            if self.media.open_capture_id.as_deref() == Some(id) {
                self.close_capture_item();
            }
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_CAPTURE_ITEM,
                &json!({ "id": id, "action": action }),
                move |res| {
                    handle.update(move |s, _| {
                        if let Ok(r) = res
                            && let Some(msg) = r.message
                        {
                            s.notice = Some(msg);
                        }
                    });
                },
            );
        }
    }

    pub fn capture_export_trim(&mut self, id: &str, preset: &str) {
        let dur_ms = self
            .media
            .capture_items
            .iter()
            .find(|c| c.id == id)
            .and_then(|c| c.duration_ms)
            .unwrap_or(30_000.0);
        let start_ms = (dur_ms * self.media.trim_start_pct as f64).round();
        let end_ms = (dur_ms * self.media.trim_end_pct as f64).round();
        self.notice = Some(format!("Exporting {preset} clip…"));
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_CAPTURE_EDIT,
                &json!({ "id": id, "preset": preset, "startMs": start_ms, "endMs": end_ms }),
                move |res| {
                    handle.update(move |s, _| {
                        s.notice = Some(match res {
                            Ok(r) if r.ok => "Clip exported.".into(),
                            Ok(r) => r.message.unwrap_or_else(|| "Export failed.".into()),
                            Err(e) => format!("Export failed: {e}"),
                        });
                        s.load_captures();
                    });
                },
            );
        }
    }
}
