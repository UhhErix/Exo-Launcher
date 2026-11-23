use crate::state::{AppState, HostStatus, Room};
use exo_rpc::HostEvent;
use std::collections::HashMap;
use std::sync::OnceLock;

pub type Handler = fn(&mut AppState, HostEvent) -> bool;

#[derive(Default)]
pub struct Registry {
    handlers: HashMap<&'static str, Handler>,
}

impl Registry {
    pub fn on(&mut self, kind: &'static str, handler: Handler) {
        let prev = self.handlers.insert(kind, handler);
        assert!(prev.is_none(), "push event {kind} registered twice");
    }

    pub fn dispatch(&self, state: &mut AppState, ev: HostEvent) -> bool {
        match self.handlers.get(ev.kind()) {
            Some(handler) => handler(state, ev),
            None => false,
        }
    }
}

static REGISTRY: OnceLock<Registry> = OnceLock::new();

pub fn registry() -> &'static Registry {
    REGISTRY.get_or_init(build)
}

fn build() -> Registry {
    let mut r = Registry::default();
    register_shell(&mut r);
    register_discord(&mut r);
    register_media_and_pc(&mut r);
    r
}

macro_rules! host_handler {
    ($name:ident, $s:ident, $variant:ident($bind:pat) => $body:block) => {
        fn $name($s: &mut AppState, ev: HostEvent) -> bool {
            let HostEvent::$variant($bind) = ev else {
                return false;
            };
            $body
        }
    };
}

host_handler!(library_updated, s, LibraryUpdated(lib) => {
    s.apply_library(lib);
    true
});

host_handler!(install_progress, s, InstallProgress(p) => {
    let next = p.is_active.then_some(p);
    let changed = s.install_progress.is_some() || next.is_some();
    s.install_progress = next;
    changed
});

host_handler!(update_progress, s, UpdateProgress(p) => {
    s.pc.update_progress = Some(p);
    true
});

host_handler!(music_playback, s, MusicPlayback(pb) => {
    s.apply_music_playback(pb)
});

host_handler!(discord_state, s, DiscordState(ev) => {
    s.handle_discord_state(ev);
    s.room == Room::Discord
});

host_handler!(discord_sign_in, s, DiscordSignIn(step) => {
    s.handle_discord_signin(step);
    s.room == Room::Discord
});

host_handler!(discord_changed, s, DiscordChanged(ev) => {
    s.handle_discord_changed(ev)
});

host_handler!(discord_members, s, DiscordMembers { guild_id } => {
    s.handle_discord_members(&guild_id)
});

host_handler!(discord_presence, s, DiscordPresence(ev) => {
    s.handle_discord_presence(ev)
});

host_handler!(discord_unread, s, DiscordUnread(ev) => {
    s.handle_discord_unread(ev)
});

host_handler!(discord_voice, s, DiscordVoice(v) => {
    s.handle_discord_voice(v);
    s.room == Room::Discord
});

host_handler!(discord_speaking, s, DiscordSpeaking { user_id, speaking } => {
    s.handle_discord_speaking(&user_id, speaking);
    s.room == Room::Discord
});

host_handler!(discord_message, s, DiscordMessage(ev) => {
    s.handle_discord_message(*ev)
});

host_handler!(discord_reaction, s, DiscordReaction(ev) => {
    s.handle_discord_reaction(ev)
});

host_handler!(discord_typing, s, DiscordTyping(ev) => {
    s.handle_discord_typing(ev)
});

host_handler!(discord_video, s, DiscordVideo(ev) => {
    s.handle_discord_video_event(ev);
    s.room == Room::Discord
});

host_handler!(discord_video_frame, s, DiscordVideoFrame(f) => {
    if s.room != Room::Discord {
        return false;
    }
    s.handle_discord_video_frame(f);
    true
});

host_handler!(discord_golive, s, DiscordGoLive(ev) => {
    s.handle_discord_golive_state(ev);
    s.room == Room::Discord
});

host_handler!(discord_golive_stats, s, DiscordGoLiveStats(ev) => {
    s.handle_discord_golive_stats(ev);
    s.room == Room::Discord
});

host_handler!(discord_golive_viewers, s, DiscordGoLiveViewers(ids) => {
    s.handle_discord_golive_viewers(ids);
    s.room == Room::Discord
});

host_handler!(devices_changed, s, DevicesChanged => {
    if s.room == Room::Devices {
        s.load_pc_devices();
        true
    } else {
        false
    }
});

host_handler!(apps_changed, s, AppsChanged => {
    if s.room == Room::Apps {
        s.load_pc_apps();
        true
    } else {
        false
    }
});

host_handler!(music_volume, s, MusicVolume(ev) => {
    let prev_vol = s.media.music_volume;
    let prev_muted = s.media.music_muted;
    s.apply_music_volume(ev);
    (prev_vol - s.media.music_volume).abs() > 0.001 || prev_muted != s.media.music_muted
});

host_handler!(capture_state, s, CaptureState(live) => {
    s.apply_capture_state(live);
    true
});

host_handler!(capture_saved, s, CaptureSaved(ev) => {
    s.apply_capture_saved(*ev);
    true
});

host_handler!(capture_reel, s, CaptureReel(ev) => {
    s.apply_capture_reel(*ev);
    true
});

host_handler!(install_queue, s, InstallQueue(ev) => {
    s.apply_install_queue(ev);
    true
});

host_handler!(game_run_state, s, GameRunState(ev) => {
    s.apply_game_run_state(ev);
    true
});

host_handler!(launch_status, s, LaunchStatus(ev) => {
    s.apply_launch_status(ev);
    true
});

host_handler!(app_update_state, s, AppUpdateState(ev) => {
    s.apply_app_update_state(ev);
    true
});

host_handler!(exo_account_changed, s, ExoAccountChanged => {
    s.apply_exo_account_changed();
    true
});

host_handler!(autopilot_report, s, AutopilotReport(ev) => {
    s.apply_autopilot_report(ev);
    true
});

host_handler!(autopilot_live, s, AutopilotLive(live) => {
    s.apply_autopilot_live(live);
    s.room == Room::ExoAi || s.room == Room::Devices
});

host_handler!(shaders_changed, s, ShadersChanged => {
    s.apply_shaders_changed();
    s.room == Room::Library || s.room == Room::ExoAi
});

host_handler!(store_changed, s, StoreChanged(ev) => {
    s.apply_store_changed(ev);
    s.room == Room::Store
});

host_handler!(disconnected, s, Disconnected(why) => {
    s.discord.voice_ui.decoders.clear();
    s.video_router.clear();
    s.pc.mouse_panel_open = false;
    s.pc.mouse_panel_loading = false;
    s.pc.mouse_panel_error = None;
    s.pc.mouse_panel_rect = None;
    s.pc.mouse_panel_visible = false;
    s.status = HostStatus::Error(why);
    s.notice = Some("Disconnected from Exo host".into());
    true
});

fn register_shell(r: &mut Registry) {
    r.on("library.updated", library_updated);
    r.on("install.progress", install_progress);
    r.on("app.updateProgress", update_progress);
    r.on("disconnected", disconnected);
    r.on("install.queue", install_queue);
    r.on("game.runState", game_run_state);
    r.on("launch.status", launch_status);
    r.on("app.updateState", app_update_state);
    r.on("exoAccount.changed", exo_account_changed);
    r.on("autopilot.report", autopilot_report);
    r.on("autopilot.live", autopilot_live);
    r.on("shaders.changed", shaders_changed);
    r.on("store.changed", store_changed);
}

fn register_discord(r: &mut Registry) {
    r.on("discord.state", discord_state);
    r.on("discord.signIn", discord_sign_in);
    r.on("discord.changed", discord_changed);
    r.on("discord.members", discord_members);
    r.on("discord.presence", discord_presence);
    r.on("discord.unread", discord_unread);
    r.on("discord.voice", discord_voice);
    r.on("discord.speaking", discord_speaking);
    r.on("discord.message", discord_message);
    r.on("discord.reaction", discord_reaction);
    r.on("discord.typing", discord_typing);
    r.on("discord.video", discord_video);
    r.on("discord.videoFrame", discord_video_frame);
    r.on("discord.goLive", discord_golive);
    r.on("discord.goLiveStats", discord_golive_stats);
    r.on("discord.goLiveViewers", discord_golive_viewers);
}

fn register_media_and_pc(r: &mut Registry) {
    r.on("music.playback", music_playback);
    r.on("devices.changed", devices_changed);
    r.on("apps.changed", apps_changed);
    r.on("music.volume", music_volume);
    r.on("capture.state", capture_state);
    r.on("capture.saved", capture_saved);
    r.on("capture.reel", capture_reel);
}
