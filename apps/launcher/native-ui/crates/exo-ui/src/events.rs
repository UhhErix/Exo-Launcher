use crate::state::{AppState, HostStatus, Room};
use exo_rpc::HostEvent;
use serde_json::Value;
use std::collections::HashMap;
use std::rc::Rc;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum HostEventKind {
    LibraryUpdated,
    InstallProgress,
    UpdateProgress,
    MusicPlayback,
    DiscordState,
    DiscordSignIn,
    DiscordChanged,
    DiscordMembers,
    DiscordPresence,
    DiscordUnread,
    DiscordVoice,
    DiscordSpeaking,
    DiscordMessage,
    DiscordReaction,
    DiscordTyping,
    DiscordPollVote,
    DiscordInteraction,
    DiscordColors,
    DiscordOpen,
    DiscordDmLine,
    DiscordNotify,
    DiscordNotifyClear,
    FriendsBadge,
    DiscordVideo,
    DiscordVideoFrame,
    DiscordGoLive,
    DiscordGoLiveStats,
    DiscordGoLiveViewers,
    DiscordCall,
    DiscordCallSong,
    DiscordCamera,
    DiscordKeybind,
    DiscordMicLevel,
    DiscordTts,
    DiscordUserSettings,
    DiscordVideoKeyframe,
    DevicesChanged,
    ControllerState,
    HidChanged,
    HidInput,
    AppsChanged,
    MusicVolume,
    MusicStatus,
    CaptureState,
    CaptureSaved,
    CaptureReel,
    CaptureShrunk,
    NotesOpen,
    InstallQueue,
    GameRunState,
    LaunchStatus,
    AppUpdateState,
    ExoAccountChanged,
    AutopilotReport,
    AutopilotLive,
    GameCapLab,
    ShadersChanged,
    ShadersEvent,
    AccountsProgress,
    BackupStopped,
    ComponentsFailed,
    GiftsChanged,
    StoreChanged,
    StoreSale,
    GameAchievements,
    GameOptimizedAgain,
    GameOptimizeHeld,
    CompressProgress,
    GpuDriver,
    GpuDriverInstall,
    GpuTuningChanged,
    GpuTuningTestProgress,
    HdrCalibrationProgress,
    HdrWarning,
    DriversStep,
    BiosProgress,
    RuntimesProgress,
    ShadersRebuild,
    Raw,
    Disconnected,
}

impl HostEventKind {
    pub fn of(ev: &HostEvent) -> Self {
        match ev {
            HostEvent::LibraryUpdated(_) => Self::LibraryUpdated,
            HostEvent::InstallProgress(_) => Self::InstallProgress,
            HostEvent::UpdateProgress(_) => Self::UpdateProgress,
            HostEvent::MusicPlayback(_) => Self::MusicPlayback,
            HostEvent::DiscordState(_) => Self::DiscordState,
            HostEvent::DiscordSignIn(_) => Self::DiscordSignIn,
            HostEvent::DiscordChanged(_) => Self::DiscordChanged,
            HostEvent::DiscordMembers { .. } => Self::DiscordMembers,
            HostEvent::DiscordPresence(_) => Self::DiscordPresence,
            HostEvent::DiscordUnread(_) => Self::DiscordUnread,
            HostEvent::DiscordVoice(_) => Self::DiscordVoice,
            HostEvent::DiscordSpeaking { .. } => Self::DiscordSpeaking,
            HostEvent::DiscordMessage(_) => Self::DiscordMessage,
            HostEvent::DiscordReaction(_) => Self::DiscordReaction,
            HostEvent::DiscordTyping(_) => Self::DiscordTyping,
            HostEvent::DiscordPollVote(_) => Self::DiscordPollVote,
            HostEvent::DiscordInteraction(_) => Self::DiscordInteraction,
            HostEvent::DiscordColors { .. } => Self::DiscordColors,
            HostEvent::DiscordOpen(_) => Self::DiscordOpen,
            HostEvent::DiscordDmLine(_) => Self::DiscordDmLine,
            HostEvent::DiscordNotify(_) => Self::DiscordNotify,
            HostEvent::DiscordNotifyClear(_) => Self::DiscordNotifyClear,
            HostEvent::FriendsBadge { .. } => Self::FriendsBadge,
            HostEvent::DiscordVideo(_) => Self::DiscordVideo,
            HostEvent::DiscordVideoFrame(_) => Self::DiscordVideoFrame,
            HostEvent::DiscordGoLive(_) => Self::DiscordGoLive,
            HostEvent::DiscordGoLiveStats(_) => Self::DiscordGoLiveStats,
            HostEvent::DiscordGoLiveViewers(_) => Self::DiscordGoLiveViewers,
            HostEvent::DiscordCall(_) => Self::DiscordCall,
            HostEvent::DiscordCallSong(_) => Self::DiscordCallSong,
            HostEvent::DiscordCamera { .. } => Self::DiscordCamera,
            HostEvent::DiscordKeybind { .. } => Self::DiscordKeybind,
            HostEvent::DiscordMicLevel(_) => Self::DiscordMicLevel,
            HostEvent::DiscordTts(_) => Self::DiscordTts,
            HostEvent::DiscordUserSettings(_) => Self::DiscordUserSettings,
            HostEvent::DiscordVideoKeyframe { .. } => Self::DiscordVideoKeyframe,
            HostEvent::DevicesChanged => Self::DevicesChanged,
            HostEvent::ControllerState(_) => Self::ControllerState,
            HostEvent::HidChanged => Self::HidChanged,
            HostEvent::HidInput(_) => Self::HidInput,
            HostEvent::AppsChanged => Self::AppsChanged,
            HostEvent::MusicVolume(_) => Self::MusicVolume,
            HostEvent::MusicStatus(_) => Self::MusicStatus,
            HostEvent::CaptureState(_) => Self::CaptureState,
            HostEvent::CaptureSaved(_) => Self::CaptureSaved,
            HostEvent::CaptureReel(_) => Self::CaptureReel,
            HostEvent::CaptureShrunk(_) => Self::CaptureShrunk,
            HostEvent::NotesOpen(_) => Self::NotesOpen,
            HostEvent::InstallQueue(_) => Self::InstallQueue,
            HostEvent::GameRunState(_) => Self::GameRunState,
            HostEvent::LaunchStatus(_) => Self::LaunchStatus,
            HostEvent::AppUpdateState(_) => Self::AppUpdateState,
            HostEvent::ExoAccountChanged => Self::ExoAccountChanged,
            HostEvent::AutopilotReport(_) => Self::AutopilotReport,
            HostEvent::AutopilotLive(_) => Self::AutopilotLive,
            HostEvent::GameCapLab(_) => Self::GameCapLab,
            HostEvent::ShadersChanged => Self::ShadersChanged,
            HostEvent::ShadersEvent(_) => Self::ShadersEvent,
            HostEvent::AccountsProgress(_) => Self::AccountsProgress,
            HostEvent::BackupStopped(_) => Self::BackupStopped,
            HostEvent::ComponentsFailed(_) => Self::ComponentsFailed,
            HostEvent::GiftsChanged(_) => Self::GiftsChanged,
            HostEvent::StoreChanged(_) => Self::StoreChanged,
            HostEvent::StoreSale(_) => Self::StoreSale,
            HostEvent::GameAchievements(_) => Self::GameAchievements,
            HostEvent::GameOptimizedAgain(_) => Self::GameOptimizedAgain,
            HostEvent::GameOptimizeHeld(_) => Self::GameOptimizeHeld,
            HostEvent::CompressProgress(_) => Self::CompressProgress,
            HostEvent::GpuDriver(_) => Self::GpuDriver,
            HostEvent::GpuDriverInstall(_) => Self::GpuDriverInstall,
            HostEvent::GpuTuningChanged(_) => Self::GpuTuningChanged,
            HostEvent::GpuTuningTestProgress(_) => Self::GpuTuningTestProgress,
            HostEvent::HdrCalibrationProgress(_) => Self::HdrCalibrationProgress,
            HostEvent::HdrWarning(_) => Self::HdrWarning,
            HostEvent::DriversStep(_) => Self::DriversStep,
            HostEvent::BiosProgress { .. } => Self::BiosProgress,
            HostEvent::RuntimesProgress(_) => Self::RuntimesProgress,
            HostEvent::ShadersRebuild(_) => Self::ShadersRebuild,
            HostEvent::Raw { .. } => Self::Raw,
            HostEvent::Disconnected(_) => Self::Disconnected,
        }
    }
}

pub type EventHandler = fn(&mut AppState, HostEvent) -> bool;
pub type RawEventHandler = fn(&mut AppState, &Value) -> bool;

#[derive(Clone, Default)]
pub struct EventRegistry {
    typed: HashMap<HostEventKind, Vec<EventHandler>>,
    raw: HashMap<&'static str, Vec<RawEventHandler>>,
}

impl EventRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn with_defaults() -> Self {
        let mut reg = Self::new();
        register_shell(&mut reg);
        register_library(&mut reg);
        register_media(&mut reg);
        register_discord(&mut reg);
        register_pc(&mut reg);
        register_store(&mut reg);
        register_game(&mut reg);
        crate::settings_state::register_wp4_events(&mut reg);
        crate::shell::register_events(&mut reg);
        crate::downloads::register_events(&mut reg);
        crate::island::register_events(&mut reg);
        crate::whats_new::register_events(&mut reg);
        crate::boot::register_events(&mut reg);
        crate::notice::register_events(&mut reg);
        crate::key_modal::register_events(&mut reg);
        reg
    }

    pub fn on(&mut self, kind: HostEventKind, handler: EventHandler) {
        self.typed.entry(kind).or_default().push(handler);
    }

    pub fn on_raw(&mut self, event: &'static str, handler: RawEventHandler) {
        self.raw.entry(event).or_default().push(handler);
    }

    pub fn dispatch(&self, state: &mut AppState, ev: HostEvent) -> bool {
        let mut redraw = false;
        if let HostEvent::Raw {
            ref event,
            ref data,
        } = ev
            && let Some(handlers) = self.raw.get(event.as_str())
        {
            for handler in handlers {
                redraw |= handler(state, data);
            }
        }
        let kind = HostEventKind::of(&ev);
        if let Some(handlers) = self.typed.get(&kind)
            && let Some((last, rest)) = handlers.split_last()
        {
            for handler in rest {
                redraw |= handler(state, ev.clone());
            }
            redraw |= last(state, ev);
        }
        redraw
    }
}

impl AppState {
    #[allow(dead_code)]
    pub fn register_event(&mut self, kind: HostEventKind, handler: EventHandler) {
        Rc::make_mut(&mut self.events).on(kind, handler);
    }

    #[allow(dead_code)]
    pub fn register_raw_event(&mut self, event: &'static str, handler: RawEventHandler) {
        Rc::make_mut(&mut self.events).on_raw(event, handler);
    }
}

fn register_shell(reg: &mut EventRegistry) {
    reg.on(HostEventKind::InstallProgress, |state, ev| {
        let HostEvent::InstallProgress(p) = ev else {
            return false;
        };
        let next = p.is_active.then_some(p);
        let changed = state.install_progress.is_some() || next.is_some();
        state.install_progress = next;
        changed
    });
    reg.on(HostEventKind::UpdateProgress, |state, ev| {
        let HostEvent::UpdateProgress(p) = ev else {
            return false;
        };
        state.pc.update_progress = Some(p);
        true
    });
    reg.on(HostEventKind::InstallQueue, |state, ev| {
        let HostEvent::InstallQueue(q) = ev else {
            return false;
        };
        state.apply_install_queue(q);
        true
    });
    reg.on(HostEventKind::AppUpdateState, |state, ev| {
        let HostEvent::AppUpdateState(s) = ev else {
            return false;
        };
        state.apply_app_update_state(s);
        true
    });
    reg.on(HostEventKind::ExoAccountChanged, |state, _| {
        state.apply_exo_account_changed();
        true
    });
    reg.on(HostEventKind::Disconnected, |state, ev| {
        let HostEvent::Disconnected(why) = ev else {
            return false;
        };
        state.discord.voice_ui.decoders.clear();
        state.video_router.clear();
        state.pc.mouse_panel_open = false;
        state.pc.mouse_panel_loading = false;
        state.pc.mouse_panel_error = None;
        state.pc.mouse_panel_rect = None;
        state.pc.mouse_panel_visible = false;
        state.status = HostStatus::Error(why);
        state.notice = Some("Disconnected from Exo host".into());
        true
    });
}

fn register_library(reg: &mut EventRegistry) {
    reg.on(HostEventKind::LibraryUpdated, |state, ev| {
        let HostEvent::LibraryUpdated(lib) = ev else {
            return false;
        };
        state.apply_library(lib);
        true
    });
    reg.on(HostEventKind::GameRunState, |state, ev| {
        let HostEvent::GameRunState(rs) = ev else {
            return false;
        };
        state.apply_game_run_state(rs);
        true
    });
    reg.on(HostEventKind::LaunchStatus, |state, ev| {
        let HostEvent::LaunchStatus(ls) = ev else {
            return false;
        };
        state.apply_launch_status(ls);
        true
    });
}

fn register_media(reg: &mut EventRegistry) {
    reg.on(HostEventKind::MusicPlayback, |state, ev| {
        let HostEvent::MusicPlayback(pb) = ev else {
            return false;
        };
        let changed = state.apply_music_playback(pb);
        state.on_music_playback_changed();
        changed
    });
    reg.on(HostEventKind::MusicVolume, |state, ev| {
        let HostEvent::MusicVolume(vol) = ev else {
            return false;
        };
        let prev_vol = state.media.music_volume;
        let prev_muted = state.media.music_muted;
        state.apply_music_volume(vol);
        state.media.music_volume_known = true;
        if state.media.music_volume > 0.0 {
            state.media.music_last_audible_volume = state.media.music_volume;
        }
        (prev_vol - state.media.music_volume).abs() > 0.001 || prev_muted != state.media.music_muted
    });
    reg.on(HostEventKind::MusicStatus, |state, ev| {
        let HostEvent::MusicStatus(st) = ev else {
            return false;
        };
        state.on_music_status(st)
    });
    reg.on(HostEventKind::DiscordCallSong, |state, ev| {
        let HostEvent::DiscordCallSong(song) = ev else {
            return false;
        };
        state.on_discord_call_song(song.clone())
    });
    reg.on(HostEventKind::CaptureState, |state, ev| {
        let HostEvent::CaptureState(live) = ev else {
            return false;
        };
        state.apply_capture_state(live);
        true
    });
    reg.on(HostEventKind::CaptureSaved, |state, ev| {
        let HostEvent::CaptureSaved(saved) = ev else {
            return false;
        };
        state.apply_capture_saved(*saved);
        true
    });
    reg.on(HostEventKind::CaptureReel, |state, ev| {
        let HostEvent::CaptureReel(reel) = ev else {
            return false;
        };
        state.apply_capture_reel_update(*reel);
        true
    });
    reg.on(HostEventKind::CaptureShrunk, |state, ev| {
        let HostEvent::CaptureShrunk(shrunk) = ev else {
            return false;
        };
        state.apply_capture_shrunk(*shrunk);
        true
    });
    reg.on(HostEventKind::NotesOpen, |state, ev| {
        let HostEvent::NotesOpen(note_ev) = ev else {
            return false;
        };
        state.apply_notes_open(note_ev);
        true
    });
}

fn register_discord(reg: &mut EventRegistry) {
    reg.on(HostEventKind::DiscordState, |state, ev| {
        let HostEvent::DiscordState(ds) = ev else {
            return false;
        };
        state.handle_discord_state(ds);
        state.room == Room::Discord
    });
    reg.on(HostEventKind::DiscordSignIn, |state, ev| {
        let HostEvent::DiscordSignIn(step) = ev else {
            return false;
        };
        state.handle_discord_signin(step);
        state.room == Room::Discord
    });
    reg.on(HostEventKind::DiscordChanged, |state, ev| {
        let HostEvent::DiscordChanged(dc) = ev else {
            return false;
        };
        state.handle_discord_changed(dc)
    });
    reg.on(HostEventKind::DiscordMembers, |state, ev| {
        let HostEvent::DiscordMembers { guild_id } = ev else {
            return false;
        };
        state.handle_discord_members(&guild_id)
    });
    reg.on(HostEventKind::DiscordPresence, |state, ev| {
        let HostEvent::DiscordPresence(dp) = ev else {
            return false;
        };
        let uid = dp.user_id.clone();
        let rich = dp.rich.clone();
        let changed = state.handle_discord_presence(dp);
        state.on_listen_along_presence(&uid, rich.as_ref());
        changed
    });
    reg.on(HostEventKind::DiscordUnread, |state, ev| {
        let HostEvent::DiscordUnread(du) = ev else {
            return false;
        };
        state.handle_discord_unread(du)
    });
    reg.on(HostEventKind::DiscordVoice, |state, ev| {
        let HostEvent::DiscordVoice(v) = ev else {
            return false;
        };
        state.handle_discord_voice(v);
        true
    });
    reg.on(HostEventKind::DiscordSpeaking, |state, ev| {
        let HostEvent::DiscordSpeaking { user_id, speaking } = ev else {
            return false;
        };
        state.handle_discord_speaking(&user_id, speaking);
        state.room == Room::Discord || state.discord.voice.state != "off"
    });
    reg.on(HostEventKind::DiscordMessage, |state, ev| {
        let HostEvent::DiscordMessage(msg) = ev else {
            return false;
        };
        state.handle_discord_message(*msg)
    });
    reg.on(HostEventKind::DiscordReaction, |state, ev| {
        let HostEvent::DiscordReaction(rx) = ev else {
            return false;
        };
        state.handle_discord_reaction(rx)
    });
    reg.on(HostEventKind::DiscordTyping, |state, ev| {
        let HostEvent::DiscordTyping(tp) = ev else {
            return false;
        };
        state.handle_discord_typing(tp)
    });
    reg.on(HostEventKind::DiscordPollVote, |state, ev| {
        let HostEvent::DiscordPollVote(pv) = ev else {
            return false;
        };
        state.handle_discord_poll_vote(pv)
    });
    reg.on(HostEventKind::DiscordInteraction, |state, ev| {
        let HostEvent::DiscordInteraction(ix) = ev else {
            return false;
        };
        state.handle_discord_interaction(*ix)
    });
    reg.on(HostEventKind::DiscordColors, |state, ev| {
        let HostEvent::DiscordColors { guild_id } = ev else {
            return false;
        };
        state.handle_discord_colors(&guild_id) | state.handle_discord_colors_event(&guild_id)
    });
    reg.on(HostEventKind::DiscordOpen, |state, ev| {
        let HostEvent::DiscordOpen(op) = ev else {
            return false;
        };
        state.handle_discord_open(op)
    });
    reg.on(HostEventKind::DiscordDmLine, |state, ev| {
        let HostEvent::DiscordDmLine(dl) = ev else {
            return false;
        };
        state.handle_discord_dm_line(dl)
    });
    reg.on(HostEventKind::DiscordNotify, |state, ev| {
        let HostEvent::DiscordNotify(nt) = ev else {
            return false;
        };
        state.handle_discord_notify(*nt)
    });
    reg.on(HostEventKind::DiscordNotifyClear, |state, ev| {
        let HostEvent::DiscordNotifyClear(nc) = ev else {
            return false;
        };
        state.handle_discord_notify_clear(nc)
    });
    reg.on(HostEventKind::FriendsBadge, |state, ev| {
        let HostEvent::FriendsBadge { count } = ev else {
            return false;
        };
        state.handle_friends_badge(count)
    });
    reg.on(HostEventKind::DiscordVideo, |state, ev| {
        let HostEvent::DiscordVideo(vid) = ev else {
            return false;
        };
        state.handle_discord_video_event(vid);
        state.room == Room::Discord
    });
    reg.on(HostEventKind::DiscordVideoFrame, |state, ev| {
        let HostEvent::DiscordVideoFrame(frame) = ev else {
            return false;
        };
        if state.room != Room::Discord {
            return false;
        }
        state.handle_discord_video_frame(frame);
        true
    });
    reg.on(HostEventKind::DiscordGoLive, |state, ev| {
        let HostEvent::DiscordGoLive(gl) = ev else {
            return false;
        };
        state.handle_discord_golive_state(gl);
        state.room == Room::Discord
    });
    reg.on(HostEventKind::DiscordGoLiveStats, |state, ev| {
        let HostEvent::DiscordGoLiveStats(stats) = ev else {
            return false;
        };
        state.handle_discord_golive_stats(stats);
        state.room == Room::Discord
    });
    reg.on(HostEventKind::DiscordGoLiveViewers, |state, ev| {
        let HostEvent::DiscordGoLiveViewers(ids) = ev else {
            return false;
        };
        state.handle_discord_golive_viewers(ids);
        state.room == Room::Discord
    });
    reg.on(HostEventKind::DiscordCall, |state, ev| {
        let HostEvent::DiscordCall(call) = ev else {
            return false;
        };
        state.handle_discord_call_event(call);
        true
    });
    reg.on(HostEventKind::DiscordCallSong, |state, ev| {
        let HostEvent::DiscordCallSong(song) = ev else {
            return false;
        };
        state.handle_discord_call_song(song);
        state.room == Room::Discord || state.discord.voice.state != "off"
    });
    reg.on(HostEventKind::DiscordCamera, |state, ev| {
        let HostEvent::DiscordCamera { state: cam_state } = ev else {
            return false;
        };
        state.handle_discord_camera_event(&cam_state);
        state.room == Room::Discord
    });
    reg.on(HostEventKind::DiscordKeybind, |state, ev| {
        let HostEvent::DiscordKeybind { action } = ev else {
            return false;
        };
        state.handle_discord_keybind(&action)
    });
    reg.on(HostEventKind::DiscordMicLevel, |state, ev| {
        let HostEvent::DiscordMicLevel(lvl) = ev else {
            return false;
        };
        state.handle_discord_mic_level(lvl)
    });
    reg.on(HostEventKind::DiscordTts, |state, ev| {
        let HostEvent::DiscordTts(t) = ev else {
            return false;
        };
        state.handle_discord_tts(t)
    });
    reg.on(HostEventKind::DiscordUserSettings, |state, ev| {
        let HostEvent::DiscordUserSettings(patch) = ev else {
            return false;
        };
        state.handle_discord_user_settings_event(&patch)
    });
    reg.on(HostEventKind::DiscordVideoKeyframe, |_state, _ev| false);
}

fn register_pc(reg: &mut EventRegistry) {
    reg.on(HostEventKind::DevicesChanged, |state, _| {
        if state.room == Room::Devices {
            state.load_pc_devices();
            true
        } else {
            false
        }
    });
    reg.on(HostEventKind::ControllerState, |state, ev| {
        let HostEvent::ControllerState(cs) = ev else {
            return false;
        };
        state.apply_controller_state(cs)
    });
    reg.on(HostEventKind::HidChanged, |state, _| {
        state.apply_hid_changed()
    });
    reg.on(HostEventKind::HidInput, |state, ev| {
        let HostEvent::HidInput(hi) = ev else {
            return false;
        };
        state.apply_hid_input(hi)
    });
    reg.on(HostEventKind::AppsChanged, |state, _| {
        if state.room == Room::Apps {
            state.load_pc_apps();
            true
        } else {
            false
        }
    });
    reg.on(HostEventKind::AutopilotReport, |state, ev| {
        let HostEvent::AutopilotReport(r) = ev else {
            return false;
        };
        state.apply_autopilot_report(r);
        true
    });
    reg.on(HostEventKind::AutopilotLive, |state, ev| {
        let HostEvent::AutopilotLive(live) = ev else {
            return false;
        };
        state.apply_autopilot_live(live);
        state.room == Room::ExoAi || state.room == Room::Devices
    });
    reg.on(HostEventKind::ShadersChanged, |state, _| {
        state.apply_shaders_changed();
        state.room == Room::Library || state.room == Room::ExoAi
    });
    reg.on(HostEventKind::GpuDriver, |state, ev| {
        let HostEvent::GpuDriver(prog) = ev else {
            return false;
        };
        state.handle_gpu_driver_progress(prog)
    });
    reg.on(HostEventKind::GpuDriverInstall, |state, ev| {
        let HostEvent::GpuDriverInstall(step) = ev else {
            return false;
        };
        state.handle_gpu_driver_install_event(step)
    });
    reg.on(HostEventKind::DriversStep, |state, ev| {
        let HostEvent::DriversStep(step) = ev else {
            return false;
        };
        state.handle_driver_step_event(step)
    });
    reg.on(HostEventKind::BiosProgress, |state, ev| {
        let HostEvent::BiosProgress { share } = ev else {
            return false;
        };
        state.handle_bios_progress_event(share)
    });
    reg.on(HostEventKind::CompressProgress, |state, ev| {
        let HostEvent::CompressProgress(cp) = ev else {
            return false;
        };
        state.handle_compress_progress_event(&cp.id, cp.compress)
    });
    reg.on(HostEventKind::RuntimesProgress, |state, ev| {
        let HostEvent::RuntimesProgress(prog) = ev else {
            return false;
        };
        state.handle_runtimes_progress_event(prog)
    });
    reg.on(HostEventKind::ShadersEvent, |state, ev| {
        let HostEvent::ShadersEvent(se) = ev else {
            return false;
        };
        state.handle_shaders_event(&se.name, &se.data)
    });
    reg.on(HostEventKind::ShadersRebuild, |state, ev| {
        let HostEvent::ShadersRebuild(rb) = ev else {
            return false;
        };
        state.handle_shaders_rebuild_event(rb)
    });
    reg.on(HostEventKind::GameRunState, |state, _| {
        state.on_pc_blocker_changed()
    });
    reg.on(HostEventKind::DiscordVoice, |state, _| {
        state.on_pc_blocker_changed()
    });
}

fn register_store(reg: &mut EventRegistry) {
    reg.on(HostEventKind::StoreChanged, |state, ev| {
        let HostEvent::StoreChanged(sc) = ev else {
            return false;
        };
        state.apply_store_changed(sc);
        state.room == Room::Store
    });
    reg.on(HostEventKind::StoreSale, |state, ev| {
        let HostEvent::StoreSale(sale) = ev else {
            return false;
        };
        state.apply_store_sale(sale);
        true
    });
}

fn register_game(reg: &mut EventRegistry) {
    reg.on(HostEventKind::GameAchievements, |state, ev| {
        let HostEvent::GameAchievements(ga) = ev else {
            return false;
        };
        state.handle_game_achievements(ga)
    });
    reg.on(HostEventKind::GameOptimizedAgain, |state, ev| {
        let HostEvent::GameOptimizedAgain(go) = ev else {
            return false;
        };
        state.handle_game_optimized_again(go)
    });
    reg.on(HostEventKind::GameOptimizeHeld, |state, ev| {
        let HostEvent::GameOptimizeHeld(go) = ev else {
            return false;
        };
        state.handle_game_optimize_held(go)
    });
    reg.on(HostEventKind::CompressProgress, |state, ev| {
        let HostEvent::CompressProgress(cp) = ev else {
            return false;
        };
        state.handle_compress_progress(cp)
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::{AppUpdateStateEvent, InstallProgress, MusicVolumeEvent};
    use serde_json::json;

    #[test]
    fn default_registry_dispatches_typed_events() {
        let mut s = AppState::with_demo_games();
        assert!(
            s.apply_host_event(HostEvent::AppUpdateState(AppUpdateStateEvent {
                state: "available".into(),
            }))
        );
        assert_eq!(s.app_update_state, "available");

        assert!(
            s.apply_host_event(HostEvent::InstallProgress(InstallProgress {
                game_id: Some("steam:10".into()),
                is_active: true,
                ..Default::default()
            }))
        );
        assert!(s.install_progress.is_some());

        assert!(
            s.apply_host_event(HostEvent::InstallProgress(InstallProgress {
                is_active: false,
                ..Default::default()
            }))
        );
        assert!(s.install_progress.is_none());
        assert!(
            !s.apply_host_event(HostEvent::InstallProgress(InstallProgress {
                is_active: false,
                ..Default::default()
            }))
        );
    }

    #[test]
    fn raw_event_handlers_match_exact_name_and_ignore_unregistered() {
        let mut s = AppState::with_demo_games();
        s.register_raw_event("discord.call", |state, data| {
            state.notice = data
                .get("caller")
                .and_then(|v| v.as_str())
                .map(|c| format!("Call from {c}"));
            true
        });

        assert!(!s.apply_host_event(HostEvent::Raw {
            event: "unknown.event".into(),
            data: json!({}),
        }));
        assert!(s.notice.is_none());

        assert!(s.apply_host_event(HostEvent::Raw {
            event: "discord.call".into(),
            data: json!({ "caller": "Alex" }),
        }));
        assert_eq!(s.notice.as_deref(), Some("Call from Alex"));
    }

    #[test]
    fn multiple_typed_handlers_all_run_and_combine_redraw() {
        let mut s = AppState::with_demo_games();
        s.register_event(HostEventKind::MusicVolume, |state, _| {
            state.notice = Some("secondary".into());
            false
        });

        let redrew = s.apply_host_event(HostEvent::MusicVolume(MusicVolumeEvent {
            level: 0.25,
            source: "host".into(),
        }));
        assert!(redrew);
        assert_eq!(s.notice.as_deref(), Some("secondary"));
    }

    #[test]
    fn disconnected_resets_mouse_panel_and_sets_error() {
        let mut s = AppState::with_demo_games();
        s.pc.mouse_panel_open = true;
        s.pc.mouse_panel_visible = true;
        assert!(s.apply_host_event(HostEvent::Disconnected("pipe closed".into())));
        assert!(!s.pc.mouse_panel_open);
        assert!(!s.pc.mouse_panel_visible);
        assert!(matches!(s.status, HostStatus::Error(ref why) if why == "pipe closed"));
        assert_eq!(s.notice.as_deref(), Some("Disconnected from Exo host"));
    }
}
