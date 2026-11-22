pub mod base64;
pub mod discord_types;
pub mod media_types;
pub mod pc_types;
pub mod pipe;
pub mod remote_art;
pub mod types;

use serde::Serialize;
pub use serde::de::DeserializeOwned;
use serde_json::{Value, json};
use std::collections::HashMap;
use std::fmt;
use std::io;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::time::Duration;
pub use discord_types::{
    DiscordAccountItem, DiscordAccountsReply, DiscordAttachment, DiscordAudioDevice,
    DiscordAudioReply, DiscordChangedEvent, DiscordChannel, DiscordChannels, DiscordDm,
    DiscordDmMember, DiscordEmbed, DiscordEmbedAuthor, DiscordEmbedField, DiscordEmbedFooter,
    DiscordEmbedMedia, DiscordEmbedProvider, DiscordFriend, DiscordFriendActionReply,
    DiscordFriendsReply, DiscordGoLiveReply, DiscordGoLiveStateEvent, DiscordGoLiveStatsEvent,
    DiscordGuild, DiscordHome, DiscordMe, DiscordMember, DiscordMemberItem, DiscordMemberList,
    DiscordMessage, DiscordMessageEvent, DiscordMessageMember, DiscordMessageRef,
    DiscordMessagesReply, DiscordMyVoiceReply, DiscordPinsReply, DiscordPrep, DiscordPresenceEvent,
    DiscordRailItem, DiscordReaction, DiscordReactionEmoji, DiscordReactionEvent, DiscordRich,
    DiscordSearchReply, DiscordSearchResult, DiscordSendReply, DiscordSignInStep, DiscordSoundItem,
    DiscordSoundboardReply, DiscordStateEvent, DiscordStatus, DiscordThreadSummary,
    DiscordTypingEvent, DiscordUnreadChannel, DiscordUnreadEvent, DiscordUnreadsReply,
    DiscordUploadReply, DiscordUser, DiscordVideoEvent, DiscordVideoFrame, DiscordVideoFrameMeta,
    DiscordVoice, DiscordVoiceMember, DiscordVoiceReply, GoLiveSource,
};
pub use media_types::{
    CaptureItem, CaptureListReply, CaptureLiveState, CaptureSettingsDto, CaptureStatusReply,
    MusicPlayback, MusicPlaybackReply, MusicVolumeReply, PlayerAlbumRef, PlayerArtist, PlayerCard,
    PlayerHeader, PlayerSection, PlayerStartReply, PlayerTrack, PlayerViewData, PlayerViewReply,
    format_duration_ms,
};
pub use pc_types::{
    AppsIconsReply, AppsListReply, AudioEndpoint, AudioReadReply, AudioVoiceInfo, AutopilotLive,
    AutopilotMemoryIntegrity, AutopilotReport, AutopilotState, DevicesImageReply, DevicesListReply,
    DriveFix, DriveGameEntry, DriveHealth, DriveInfo, DriveLink, DriveSpaceBreakdown, DriveTemp,
    DriveVerdict, DriveVolume, DrivesReply, EqHeadphone, EqStateReply, ExoAiFans, ExoAiGaming,
    ExoAiState, HdrState, InstalledAppItem, KeyboardBackupSummary, KeyboardBackupsReply,
    KeyboardProfileSlot, KeyboardReadReply, LatencyReport, LatencySample, PeripheralCard,
    PeripheralPort, ShaderEstimate, ShaderPrepareGame, ShaderSnapGame, ShadersAddonStatus,
    ShadersEstimatesReply, ShadersPrepareGamesReply, ShadersSnapshot, StartupItem,
    StartupListReply, StartupRemovedItem, SystemAbout, SystemTuningState, TuningItem,
    UpdateCheckReply, UpdateProgress,
};
pub use types::{
    ActionResult, ArtBase, ExoAccountState, Game, GameExtras, GameGetReply, GameProperties,
    GamePropertySetReply, HostContract, HostEvent, InstallProgress, KeyPreview, LauncherSettings,
    LibraryPayload, StoreArt, StoreGame, StoreHome, StoreLowest, StoreOffer, StoreOtherOffers,
    StoreSearchReply, StoreShelf, StoreTile,
};

pub const PROTOCOL_VERSION: u32 = 1;
const MAX_INBOUND_BYTES: usize = 32 * 1024 * 1024;

pub const METHOD_APP_CHECK_UPDATE: &str = "app.checkUpdate";
pub const METHOD_APP_INSTALL_UPDATE: &str = "app.installUpdate";
pub const METHOD_APP_OPEN_LOGS: &str = "app.openLogs";
pub const METHOD_APP_REPAIR_CACHE: &str = "app.repairCache";
pub const METHOD_APPLEMUSIC_ACT: &str = "applemusic.act";
pub const METHOD_APPLEMUSIC_START: &str = "applemusic.start";
pub const METHOD_APPLEMUSIC_VIEW: &str = "applemusic.view";
pub const METHOD_APPS_ICONS: &str = "apps.icons";
pub const METHOD_APPS_LIST: &str = "apps.list";
pub const METHOD_APPS_TIDY: &str = "apps.tidy";
pub const METHOD_APPS_UNINSTALL: &str = "apps.uninstall";
pub const METHOD_ART_BASE: &str = "art.base";
pub const METHOD_AUDIO_READ: &str = "audio.read";
pub const METHOD_AUDIO_SET_MUTE: &str = "audio.setMute";
pub const METHOD_AUDIO_SET_VOLUME: &str = "audio.setVolume";
pub const METHOD_AUTOPILOT_STATE: &str = "autopilot.state";
pub const METHOD_CAPTURE_ACT: &str = "capture.act";
pub const METHOD_CAPTURE_EDIT: &str = "capture.edit";
pub const METHOD_CAPTURE_ITEM: &str = "capture.item";
pub const METHOD_CAPTURE_LIST: &str = "capture.list";
pub const METHOD_CAPTURE_REEL: &str = "capture.reel";
pub const METHOD_CAPTURE_REEL_CANCEL: &str = "capture.reel.cancel";
pub const METHOD_CAPTURE_SETTINGS: &str = "capture.settings";
pub const METHOD_CAPTURE_STATUS: &str = "capture.status";
pub const METHOD_DEVICES_IMAGE: &str = "devices.image";
pub const METHOD_DEVICES_LIST: &str = "devices.list";
pub const METHOD_DISCORD_ACCOUNTS: &str = "discord.accounts";
pub const METHOD_DISCORD_ACK: &str = "discord.ack";
pub const METHOD_DISCORD_AUDIO: &str = "discord.audio";
pub const METHOD_DISCORD_CAMERA: &str = "discord.camera";
pub const METHOD_DISCORD_CHANNELS: &str = "discord.channels";
pub const METHOD_DISCORD_DELETE: &str = "discord.delete";
pub const METHOD_DISCORD_EDIT: &str = "discord.edit";
pub const METHOD_DISCORD_FRIEND: &str = "discord.friend";
pub const METHOD_DISCORD_FRIENDS: &str = "discord.friends";
pub const METHOD_DISCORD_GOLIVE: &str = "discord.golive";
pub const METHOD_DISCORD_HOME: &str = "discord.home";
pub const METHOD_DISCORD_MARK_READ: &str = "discord.markRead";
pub const METHOD_DISCORD_MEMBERS: &str = "discord.members";
pub const METHOD_DISCORD_MESSAGES: &str = "discord.messages";
pub const METHOD_DISCORD_MY_VOICE: &str = "discord.myVoice";
pub const METHOD_DISCORD_PINS: &str = "discord.pins";
pub const METHOD_DISCORD_REACT: &str = "discord.react";
pub const METHOD_DISCORD_SEARCH: &str = "discord.search";
pub const METHOD_DISCORD_SEND: &str = "discord.send";
pub const METHOD_DISCORD_SET_STATUS: &str = "discord.setStatus";
pub const METHOD_DISCORD_SIGN_IN: &str = "discord.signIn";
pub const METHOD_DISCORD_SIGN_OUT: &str = "discord.signOut";
pub const METHOD_DISCORD_SOUNDBOARD: &str = "discord.soundboard";
pub const METHOD_DISCORD_STATUS: &str = "discord.status";
pub const METHOD_DISCORD_STREAM: &str = "discord.stream";
pub const METHOD_DISCORD_TYPING: &str = "discord.typing";
pub const METHOD_DISCORD_UNREADS: &str = "discord.unreads";
pub const METHOD_DISCORD_UPLOAD: &str = "discord.upload";
pub const METHOD_DISCORD_VIEW: &str = "discord.view";
pub const METHOD_DISCORD_VOICE: &str = "discord.voice";
pub const METHOD_EQ: &str = "eq";
pub const METHOD_EXO_ACCOUNT_SIGN_OUT: &str = "exoAccount.signOut";
pub const METHOD_EXO_ACCOUNT_STATE: &str = "exoAccount.state";
pub const METHOD_EXO_ACCOUNT_SYNC: &str = "exoAccount.sync";
pub const METHOD_EXO_AI_SET: &str = "exoAi.set";
pub const METHOD_EXO_AI_STATE: &str = "exoAi.state";
pub const METHOD_HDR_STATE: &str = "hdr.state";
pub const METHOD_JUNK_CLEAN: &str = "junk.clean";
pub const METHOD_JUNK_SCAN: &str = "junk.scan";
pub const METHOD_KEYBOARD_BACKUP: &str = "keyboard.backup";
pub const METHOD_KEYBOARD_BACKUPS: &str = "keyboard.backups";
pub const METHOD_KEYBOARD_OPEN_APP: &str = "keyboard.openApp";
pub const METHOD_KEYBOARD_PROFILE: &str = "keyboard.profile";
pub const METHOD_KEYBOARD_READ: &str = "keyboard.read";
pub const METHOD_KEYBOARD_RESTORE: &str = "keyboard.restore";
pub const METHOD_KEYBOARD_SETTINGS: &str = "keyboard.settings";
pub const METHOD_LIBRARY_GET: &str = "library.get";
pub const METHOD_LIBRARY_REFRESH: &str = "library.refresh";
pub const METHOD_GAME_GET: &str = "game.get";
pub const METHOD_GAME_EXTRAS: &str = "game.extras";
pub const METHOD_GAME_LAUNCH: &str = "game.launch";
pub const METHOD_GAME_STOP: &str = "game.stop";
pub const METHOD_GAME_INSTALL: &str = "game.install";
pub const METHOD_GAME_UPDATE: &str = "game.update";
pub const METHOD_GAME_UNINSTALL: &str = "game.uninstall";
pub const METHOD_GAME_REPAIR: &str = "game.repair";
pub const METHOD_GAME_OPEN_FOLDER: &str = "game.openFolder";
pub const METHOD_GAME_CANCEL_INSTALL: &str = "game.cancelInstall";
pub const METHOD_GAME_PAUSE_INSTALL: &str = "game.pauseInstall";
pub const METHOD_GAME_PROPERTIES: &str = "game.properties";
pub const METHOD_GAME_PROPERTIES_SET: &str = "game.properties.set";
pub const METHOD_KEYS_PREVIEW: &str = "keys.preview";
pub const METHOD_KEYS_ACTIVATE: &str = "keys.activate";
pub const METHOD_MUSIC_CONTROL: &str = "music.control";
pub const METHOD_MUSIC_PLAYBACK: &str = "music.playback";
pub const METHOD_MUSIC_SIGN_IN: &str = "music.signIn";
pub const METHOD_MUSIC_VOLUME: &str = "music.volume";
pub const METHOD_PLAYTIME_RIOT_REFRESH: &str = "playtime.riotRefresh";
pub const METHOD_SETTINGS_GET: &str = "settings.get";
pub const METHOD_SETTINGS_SET: &str = "settings.set";
pub const METHOD_SHADERS_ADDON: &str = "shaders.addon";
pub const METHOD_SHADERS_CALL: &str = "shaders.call";
pub const METHOD_SHADERS_ESTIMATES: &str = "shaders.estimates";
pub const METHOD_SHADERS_PREPARE_GAMES: &str = "shaders.prepare.games";
pub const METHOD_SHADERS_PREPARE_START: &str = "shaders.prepare.start";
pub const METHOD_SHELL_READY: &str = "shell.ready";
pub const METHOD_SPOTIFY_ACT: &str = "spotify.act";
pub const METHOD_SPOTIFY_START: &str = "spotify.start";
pub const METHOD_SPOTIFY_VIEW: &str = "spotify.view";
pub const METHOD_STARTUP_CLEAN: &str = "startup.clean";
pub const METHOD_STARTUP_LIST: &str = "startup.list";
pub const METHOD_STARTUP_REMOVE: &str = "startup.remove";
pub const METHOD_STARTUP_RESTORE: &str = "startup.restore";
pub const METHOD_STORE_HOME: &str = "store.home";
pub const METHOD_STORE_SEARCH: &str = "store.search";
pub const METHOD_STORE_GAME: &str = "store.game";
pub const METHOD_STORE_GAME_OFFERS: &str = "store.gameOffers";
pub const METHOD_STORE_OPEN: &str = "store.open";
pub const METHOD_STORE_WISHLIST: &str = "store.wishlist";
pub const METHOD_SYSTEM_ABOUT: &str = "system.about";
pub const METHOD_SYSTEM_DRIVES: &str = "system.drives";
pub const METHOD_SYSTEM_TUNING: &str = "system.tuning";
pub const METHOD_YTMUSIC_ACT: &str = "ytmusic.act";
pub const METHOD_YTMUSIC_START: &str = "ytmusic.start";
pub const METHOD_YTMUSIC_VIEW: &str = "ytmusic.view";

pub const USED_METHODS: &[&str] = &[
    METHOD_APP_CHECK_UPDATE,
    METHOD_APP_INSTALL_UPDATE,
    METHOD_APP_OPEN_LOGS,
    METHOD_APP_REPAIR_CACHE,
    METHOD_APPLEMUSIC_ACT,
    METHOD_APPLEMUSIC_START,
    METHOD_APPLEMUSIC_VIEW,
    METHOD_APPS_ICONS,
    METHOD_APPS_LIST,
    METHOD_APPS_TIDY,
    METHOD_APPS_UNINSTALL,
    METHOD_ART_BASE,
    METHOD_AUDIO_READ,
    METHOD_AUDIO_SET_MUTE,
    METHOD_AUDIO_SET_VOLUME,
    METHOD_AUTOPILOT_STATE,
    METHOD_CAPTURE_ACT,
    METHOD_CAPTURE_EDIT,
    METHOD_CAPTURE_ITEM,
    METHOD_CAPTURE_LIST,
    METHOD_CAPTURE_REEL,
    METHOD_CAPTURE_REEL_CANCEL,
    METHOD_CAPTURE_SETTINGS,
    METHOD_CAPTURE_STATUS,
    METHOD_DEVICES_IMAGE,
    METHOD_DEVICES_LIST,
    METHOD_DISCORD_ACCOUNTS,
    METHOD_DISCORD_ACK,
    METHOD_DISCORD_AUDIO,
    METHOD_DISCORD_CAMERA,
    METHOD_DISCORD_CHANNELS,
    METHOD_DISCORD_DELETE,
    METHOD_DISCORD_EDIT,
    METHOD_DISCORD_FRIEND,
    METHOD_DISCORD_FRIENDS,
    METHOD_DISCORD_GOLIVE,
    METHOD_DISCORD_HOME,
    METHOD_DISCORD_MARK_READ,
    METHOD_DISCORD_MEMBERS,
    METHOD_DISCORD_MESSAGES,
    METHOD_DISCORD_MY_VOICE,
    METHOD_DISCORD_PINS,
    METHOD_DISCORD_REACT,
    METHOD_DISCORD_SEARCH,
    METHOD_DISCORD_SEND,
    METHOD_DISCORD_SET_STATUS,
    METHOD_DISCORD_SIGN_IN,
    METHOD_DISCORD_SIGN_OUT,
    METHOD_DISCORD_SOUNDBOARD,
    METHOD_DISCORD_STATUS,
    METHOD_DISCORD_STREAM,
    METHOD_DISCORD_TYPING,
    METHOD_DISCORD_UNREADS,
    METHOD_DISCORD_UPLOAD,
    METHOD_DISCORD_VIEW,
    METHOD_DISCORD_VOICE,
    METHOD_EQ,
    METHOD_EXO_ACCOUNT_SIGN_OUT,
    METHOD_EXO_ACCOUNT_STATE,
    METHOD_EXO_ACCOUNT_SYNC,
    METHOD_EXO_AI_SET,
    METHOD_EXO_AI_STATE,
    METHOD_HDR_STATE,
    METHOD_JUNK_CLEAN,
    METHOD_JUNK_SCAN,
    METHOD_KEYBOARD_BACKUP,
    METHOD_KEYBOARD_BACKUPS,
    METHOD_KEYBOARD_OPEN_APP,
    METHOD_KEYBOARD_PROFILE,
    METHOD_KEYBOARD_READ,
    METHOD_KEYBOARD_RESTORE,
    METHOD_KEYBOARD_SETTINGS,
    METHOD_LIBRARY_GET,
    METHOD_LIBRARY_REFRESH,
    METHOD_GAME_GET,
    METHOD_GAME_EXTRAS,
    METHOD_GAME_LAUNCH,
    METHOD_GAME_STOP,
    METHOD_GAME_INSTALL,
    METHOD_GAME_UPDATE,
    METHOD_GAME_UNINSTALL,
    METHOD_GAME_REPAIR,
    METHOD_GAME_OPEN_FOLDER,
    METHOD_GAME_CANCEL_INSTALL,
    METHOD_GAME_PAUSE_INSTALL,
    METHOD_GAME_PROPERTIES,
    METHOD_GAME_PROPERTIES_SET,
    METHOD_KEYS_PREVIEW,
    METHOD_KEYS_ACTIVATE,
    METHOD_MUSIC_CONTROL,
    METHOD_MUSIC_PLAYBACK,
    METHOD_MUSIC_SIGN_IN,
    METHOD_MUSIC_VOLUME,
    METHOD_PLAYTIME_RIOT_REFRESH,
    METHOD_SETTINGS_GET,
    METHOD_SETTINGS_SET,
    METHOD_SHADERS_ADDON,
    METHOD_SHADERS_CALL,
    METHOD_SHADERS_ESTIMATES,
    METHOD_SHADERS_PREPARE_GAMES,
    METHOD_SHADERS_PREPARE_START,
    METHOD_SHELL_READY,
    METHOD_SPOTIFY_ACT,
    METHOD_SPOTIFY_START,
    METHOD_SPOTIFY_VIEW,
    METHOD_STARTUP_CLEAN,
    METHOD_STARTUP_LIST,
    METHOD_STARTUP_REMOVE,
    METHOD_STARTUP_RESTORE,
    METHOD_STORE_HOME,
    METHOD_STORE_SEARCH,
    METHOD_STORE_GAME,
    METHOD_STORE_GAME_OFFERS,
    METHOD_STORE_OPEN,
    METHOD_STORE_WISHLIST,
    METHOD_SYSTEM_ABOUT,
    METHOD_SYSTEM_DRIVES,
    METHOD_SYSTEM_TUNING,
    METHOD_YTMUSIC_ACT,
    METHOD_YTMUSIC_START,
    METHOD_YTMUSIC_VIEW,
];

#[derive(Debug)]
pub enum RpcError {
    Io(io::Error),
    Json(serde_json::Error),
    Host(String),
    Disconnected,
    Timeout,
}

impl fmt::Display for RpcError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(e) => write!(f, "pipe io: {e}"),
            Self::Json(e) => write!(f, "json: {e}"),
            Self::Host(msg) => f.write_str(msg),
            Self::Disconnected => f.write_str("host disconnected"),
            Self::Timeout => f.write_str("rpc timed out"),
        }
    }
}

impl std::error::Error for RpcError {}

impl From<io::Error> for RpcError {
    fn from(value: io::Error) -> Self {
        Self::Io(value)
    }
}

impl From<serde_json::Error> for RpcError {
    fn from(value: serde_json::Error) -> Self {
        Self::Json(value)
    }
}

type PendingCallback = Box<dyn FnOnce(Result<Value, RpcError>) + Send + 'static>;

pub fn load_host_contract(json: &str) -> Result<HostContract, serde_json::Error> {
    serde_json::from_str(json)
}

#[cfg(windows)]
pub struct RpcClient {
    pipe: Arc<pipe::PipeHandle>,
    outbound: mpsc::Sender<String>,
    pending: Arc<Mutex<HashMap<String, PendingCallback>>>,
    next_id: AtomicU64,
    closed: Arc<AtomicBool>,
}

#[cfg(windows)]
impl RpcClient {
    pub fn connect(
        pipe_name: &str,
        timeout: Duration,
        on_event: impl Fn(HostEvent) + Send + Sync + 'static,
    ) -> Result<Arc<Self>, RpcError> {
        let pipe = pipe::PipeHandle::connect(pipe_name, timeout)?;
        let (tx, rx) = mpsc::channel::<String>();
        let pending: Arc<Mutex<HashMap<String, PendingCallback>>> =
            Arc::new(Mutex::new(HashMap::new()));
        let closed = Arc::new(AtomicBool::new(false));
        let on_event: Arc<dyn Fn(HostEvent) + Send + Sync> = Arc::new(on_event);

        let write_pipe = Arc::clone(&pipe);
        let write_closed = Arc::clone(&closed);
        std::thread::Builder::new()
            .name("exo-rpc-writer".into())
            .spawn(move || {
                let Ok(event) = pipe::EventHandle::new() else {
                    return;
                };
                while let Ok(mut line) = rx.recv() {
                    if write_closed.load(Ordering::Acquire) {
                        break;
                    }
                    line.push('\n');
                    if write_pipe
                        .write_all_overlapped(event.raw(), line.as_bytes())
                        .is_err()
                    {
                        break;
                    }
                }
            })?;

        let read_pipe = Arc::clone(&pipe);
        let read_pending = Arc::clone(&pending);
        let read_closed = Arc::clone(&closed);
        let read_events = Arc::clone(&on_event);
        std::thread::Builder::new()
            .name("exo-rpc-reader".into())
            .spawn(move || {
                let reason = read_loop(&read_pipe, &read_pending, &*read_events);
                read_closed.store(true, Ordering::Release);
                let mut map = read_pending.lock().unwrap_or_else(|e| e.into_inner());
                for (_, cb) in map.drain() {
                    cb(Err(RpcError::Disconnected));
                }
                drop(map);
                read_events(HostEvent::Disconnected(reason));
            })?;

        Ok(Arc::new(Self {
            pipe,
            outbound: tx,
            pending,
            next_id: AtomicU64::new(1),
            closed,
        }))
    }

    pub fn call_raw<P: Serialize>(
        &self,
        method: &str,
        params: &P,
        callback: impl FnOnce(Result<Value, RpcError>) + Send + 'static,
    ) {
        if self.closed.load(Ordering::Acquire) {
            callback(Err(RpcError::Disconnected));
            return;
        }
        let id = self.next_id.fetch_add(1, Ordering::Relaxed).to_string();
        let envelope = json!({
            "version": PROTOCOL_VERSION,
            "id": id,
            "method": method,
            "params": params,
        });
        let payload = match serde_json::to_string(&envelope) {
            Ok(s) => s,
            Err(e) => {
                callback(Err(RpcError::Json(e)));
                return;
            }
        };
        {
            let mut map = self.pending.lock().unwrap_or_else(|e| e.into_inner());
            map.insert(id.clone(), Box::new(callback));
        }
        if self.outbound.send(payload).is_err() {
            let mut map = self.pending.lock().unwrap_or_else(|e| e.into_inner());
            if let Some(cb) = map.remove(&id) {
                cb(Err(RpcError::Disconnected));
            }
        }
    }

    pub fn call<P: Serialize, R: DeserializeOwned + Send + 'static>(
        &self,
        method: &str,
        params: &P,
        callback: impl FnOnce(Result<R, RpcError>) + Send + 'static,
    ) {
        self.call_raw(method, params, move |res| {
            let typed = res.and_then(|v| serde_json::from_value::<R>(v).map_err(RpcError::Json));
            callback(typed);
        });
    }

    pub fn call_blocking<P: Serialize, R: DeserializeOwned + Send + 'static>(
        &self,
        method: &str,
        params: &P,
        timeout: Duration,
    ) -> Result<R, RpcError> {
        let (tx, rx) = mpsc::channel();
        self.call(method, params, move |res| {
            let _ = tx.send(res);
        });
        rx.recv_timeout(timeout).map_err(|_| RpcError::Timeout)?
    }

    pub fn close(&self) {
        self.closed.store(true, Ordering::Release);
        self.pipe.cancel();
    }
}

#[cfg(windows)]
impl Drop for RpcClient {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(windows)]
fn read_loop(
    pipe: &pipe::PipeHandle,
    pending: &Mutex<HashMap<String, PendingCallback>>,
    on_event: &(dyn Fn(HostEvent) + Send + Sync),
) -> String {
    let event = match pipe::EventHandle::new() {
        Ok(e) => e,
        Err(e) => return e.to_string(),
    };
    let mut buf = vec![0u8; 32 * 1024];
    let mut line = Vec::with_capacity(4096);

    loop {
        let n = match pipe.read_overlapped(event.raw(), &mut buf) {
            Ok(0) => return "eof".into(),
            Ok(n) => n,
            Err(e) => return e.to_string(),
        };
        let mut start = 0;
        for i in 0..n {
            if buf[i] != b'\n' {
                continue;
            }
            if line.len() + (i - start) > MAX_INBOUND_BYTES {
                return "inbound message exceeded limit".into();
            }
            line.extend_from_slice(&buf[start..i]);
            if line.last() == Some(&b'\r') {
                line.pop();
            }
            if !line.is_empty() {
                dispatch_line(&line, pending, on_event);
            }
            line.clear();
            start = i + 1;
        }
        if start < n {
            if line.len() + (n - start) > MAX_INBOUND_BYTES {
                return "inbound message exceeded limit".into();
            }
            line.extend_from_slice(&buf[start..n]);
        }
    }
}

#[cfg(windows)]
fn dispatch_line(
    raw: &[u8],
    pending: &Mutex<HashMap<String, PendingCallback>>,
    on_event: &(dyn Fn(HostEvent) + Send + Sync),
) {
    let Ok(val) = serde_json::from_slice::<Value>(raw) else {
        return;
    };
    if let Some(id) = val.get("id").and_then(Value::as_str) {
        let cb = pending
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .remove(id);
        if let Some(cb) = cb {
            let ok = val.get("ok").and_then(Value::as_bool).unwrap_or(false);
            if ok {
                cb(Ok(val.get("result").cloned().unwrap_or(Value::Null)));
            } else {
                let err = val
                    .get("error")
                    .and_then(Value::as_str)
                    .unwrap_or("Host request failed")
                    .to_string();
                cb(Err(RpcError::Host(err)));
            }
        }
        return;
    }
    if let Some(name) = val.get("event").and_then(Value::as_str) {
        let data = val.get("data").cloned().unwrap_or(Value::Null);
        let parsed = match name {
            "library.updated" => serde_json::from_value::<LibraryPayload>(data.clone())
                .map(HostEvent::LibraryUpdated)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "install.progress" => serde_json::from_value::<InstallProgress>(data.clone())
                .map(HostEvent::InstallProgress)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "app.updateProgress" => serde_json::from_value::<UpdateProgress>(data.clone())
                .map(HostEvent::UpdateProgress)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "music.playback" => {
                let pb = serde_json::from_value::<Option<MusicPlayback>>(
                    data.get("playback").cloned().unwrap_or(Value::Null),
                )
                .ok()
                .flatten();
                HostEvent::MusicPlayback(pb)
            }
            "discord.state" => serde_json::from_value::<DiscordStateEvent>(data.clone())
                .map(HostEvent::DiscordState)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.signin" => serde_json::from_value::<DiscordSignInStep>(data.clone())
                .map(HostEvent::DiscordSignIn)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.changed" => serde_json::from_value::<DiscordChangedEvent>(data.clone())
                .map(HostEvent::DiscordChanged)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.members" => data
                .get("guildId")
                .and_then(Value::as_str)
                .map(|g| HostEvent::DiscordMembers {
                    guild_id: g.to_string(),
                })
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.presence" => serde_json::from_value::<DiscordPresenceEvent>(data.clone())
                .map(HostEvent::DiscordPresence)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.unread" => serde_json::from_value::<DiscordUnreadEvent>(data.clone())
                .map(HostEvent::DiscordUnread)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.voice" => serde_json::from_value::<DiscordVoice>(data.clone())
                .map(HostEvent::DiscordVoice)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.speaking" => {
                if let Some(uid) = data.get("userId").and_then(Value::as_str) {
                    HostEvent::DiscordSpeaking {
                        user_id: uid.to_string(),
                        speaking: data
                            .get("speaking")
                            .and_then(Value::as_bool)
                            .unwrap_or(false),
                    }
                } else {
                    HostEvent::Raw {
                        event: name.to_string(),
                        data,
                    }
                }
            }
            "discord.message" => serde_json::from_value::<DiscordMessageEvent>(data.clone())
                .map(HostEvent::DiscordMessage)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.reaction" => serde_json::from_value::<DiscordReactionEvent>(data.clone())
                .map(HostEvent::DiscordReaction)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.typing" => serde_json::from_value::<DiscordTypingEvent>(data.clone())
                .map(HostEvent::DiscordTyping)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.video" => serde_json::from_value::<DiscordVideoEvent>(data.clone())
                .map(HostEvent::DiscordVideo)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.golive" => serde_json::from_value::<DiscordGoLiveStateEvent>(data.clone())
                .map(HostEvent::DiscordGoLive)
                .unwrap_or(HostEvent::Raw {
                    event: name.to_string(),
                    data,
                }),
            "discord.golive.stats" => {
                serde_json::from_value::<DiscordGoLiveStatsEvent>(data.clone())
                    .map(HostEvent::DiscordGoLiveStats)
                    .unwrap_or(HostEvent::Raw {
                        event: name.to_string(),
                        data,
                    })
            }
            "discord.golive.viewers" => {
                let ids = data
                    .get("ids")
                    .and_then(Value::as_array)
                    .map(|arr| {
                        arr.iter()
                            .filter_map(|v| v.as_str().map(str::to_string))
                            .collect()
                    })
                    .unwrap_or_default();
                HostEvent::DiscordGoLiveViewers(ids)
            }
            "binaryFrame" => {
                let meta_val = val.get("meta").cloned().unwrap_or(Value::Null);
                let b64 = val.get("data").and_then(Value::as_str).unwrap_or("");
                if let (Ok(meta), Some(bytes)) = (
                    serde_json::from_value::<DiscordVideoFrameMeta>(meta_val),
                    base64::decode(b64),
                ) {
                    HostEvent::DiscordVideoFrame(DiscordVideoFrame {
                        source: meta.source,
                        user_id: meta.user_id,
                        timestamp: meta.timestamp,
                        keyframe: meta.keyframe,
                        rotation: meta.rotation,
                        flip: meta.flip,
                        codec: meta.codec.unwrap_or_else(|| "h264".into()),
                        data: bytes,
                    })
                } else {
                    HostEvent::Raw {
                        event: name.to_string(),
                        data,
                    }
                }
            }
            "devices.changed" => HostEvent::DevicesChanged,
            "apps.changed" => HostEvent::AppsChanged,
            _ => HostEvent::Raw {
                event: name.to_string(),
                data,
            },
        };
        on_event(parsed);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const CONTRACT_JSON: &str = include_str!("../../../../ui/contracts/host-v1.json");

    #[test]
    fn host_contract_contains_every_rust_rpc_method() {
        let contract = load_host_contract(CONTRACT_JSON).expect("valid host-v1.json");
        assert_eq!(contract.version, PROTOCOL_VERSION);
        for method in USED_METHODS {
            assert!(
                contract.methods.contains_key(*method),
                "Rust calls undeclared host method: {method}"
            );
        }
    }

    #[test]
    fn art_base_rejects_traversal_and_unsafe_leaves() {
        let base = ArtBase {
            covers: "https://covers.exo-launcher.local/tok/".into(),
            covers_dir: Some(std::env::temp_dir().to_string_lossy().into_owned()),
            captures_dir: Some(std::env::temp_dir().to_string_lossy().into_owned()),
            ..Default::default()
        };
        assert!(
            base.resolve_local_cover("https://covers.exo-launcher.local/tok/..%2Fsecret.png")
                .is_none()
        );
        assert!(
            base.resolve_local_cover("https://evil.example/tok/a.png")
                .is_none()
        );
        assert!(
            base.resolve_capture_file("https://captures.exo-launcher.local/..%2F..%2Fwin.ini")
                .is_none()
        );
    }

    #[cfg(windows)]
    #[test]
    fn decodes_binary_video_frame_from_named_pipe_envelope() {
        let payload = br#"{"event":"binaryFrame","meta":{"source":"stream","userId":"200002","timestamp":1234.5,"keyframe":true,"codec":"h265"},"data":"AAABZ2Q="}"#;
        let pending = Mutex::new(HashMap::new());
        let received = Mutex::new(None);
        dispatch_line(payload, &pending, &|ev| {
            *received.lock().unwrap() = Some(ev);
        });
        let ev = received.lock().unwrap().take().expect("decoded event");
        match ev {
            HostEvent::DiscordVideoFrame(f) => {
                assert_eq!(f.source, "stream");
                assert_eq!(f.user_id, "200002");
                assert_eq!(f.codec, "h265");
                assert!(f.keyframe);
                assert_eq!(f.data, vec![0x00, 0x00, 0x01, 0x67, 0x64]);
            }
            other => panic!("unexpected event: {other:?}"),
        }
    }
}
