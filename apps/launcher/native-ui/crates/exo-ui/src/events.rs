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
    DiscordVideo,
    DiscordVideoFrame,
    DiscordGoLive,
    DiscordGoLiveStats,
    DiscordGoLiveViewers,
    DevicesChanged,
    ControllerState,
    HidChanged,
    HidInput,
    AppsChanged,
    MusicVolume,
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
    ShadersChanged,
    StoreChanged,
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
            HostEvent::DiscordVideo(_) => Self::DiscordVideo,
            HostEvent::DiscordVideoFrame(_) => Self::DiscordVideoFrame,
            HostEvent::DiscordGoLive(_) => Self::DiscordGoLive,
            HostEvent::DiscordGoLiveStats(_) => Self::DiscordGoLiveStats,
            HostEvent::DiscordGoLiveViewers(_) => Self::DiscordGoLiveViewers,
            HostEvent::DevicesChanged => Self::DevicesChanged,
            HostEvent::ControllerState(_) => Self::ControllerState,
            HostEvent::HidChanged => Self::HidChanged,
            HostEvent::HidInput(_) => Self::HidInput,
            HostEvent::AppsChanged => Self::AppsChanged,
            HostEvent::MusicVolume(_) => Self::MusicVolume,
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
            HostEvent::ShadersChanged => Self::ShadersChanged,
            HostEvent::StoreChanged(_) => Self::StoreChanged,
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
        state.apply_music_playback(pb)
    });
    reg.on(HostEventKind::MusicVolume, |state, ev| {
        let HostEvent::MusicVolume(vol) = ev else {
            return false;
        };
        let prev_vol = state.media.music_volume;
        let prev_muted = state.media.music_muted;
        state.apply_music_volume(vol);
        (prev_vol - state.media.music_volume).abs() > 0.001 || prev_muted != state.media.music_muted
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
        state.handle_discord_presence(dp)
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
        state.room == Room::Discord
    });
    reg.on(HostEventKind::DiscordSpeaking, |state, ev| {
        let HostEvent::DiscordSpeaking { user_id, speaking } = ev else {
            return false;
        };
        state.handle_discord_speaking(&user_id, speaking);
        state.room == Room::Discord
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
}

fn register_store(reg: &mut EventRegistry) {
    reg.on(HostEventKind::StoreChanged, |state, ev| {
        let HostEvent::StoreChanged(sc) = ev else {
            return false;
        };
        state.apply_store_changed(sc);
        state.room == Room::Store
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
