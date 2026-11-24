use crate::events::EventRegistry;
use crate::gamepad::GamepadKind;
use crate::state::{AppState, ContextMenuTarget, HostStatus, InputMode, MiniBarSlot, Room};
use crate::{
    boot, captures, clients_gate, discord, discord_voice, downloads, game_page, gamepad, island,
    key_modal, library, menus, music, notice, palette, pc, report, settings, settings_account,
    setup, store, store_media, whats_new,
};
use exo_kit::{
    BG, CAPTION_BUTTON_GAP, CAPTION_BUTTON_HEIGHT, CAPTION_BUTTON_WIDTH, CAPTION_CAPSULE_PAD,
    CAPTION_TRAILING_INSET, FG, GAP, GLASS_CONTROL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD,
    RADIUS_PILL, SPRING_MOVE, SPRING_QUICK, TRANSPARENT, bad, exo_mark, exo_titlebar, fg_muted,
    glass_capsule_opt, glass_control_fill, glass_enter, glass_panel_opt, glass_rim_bottom,
    glass_rim_top, glass_still_control_fill, good, hover_fill, icon_camera, icon_chat, icon_close,
    icon_grid, icon_library, icon_minimize, icon_mouse, icon_music, icon_settings, icon_sparkles,
    icon_store, icon_volume, icon_volume_low, icon_volume_off, icon_win_maximize, icon_win_restore,
    modal_scrim_transition, popover_transition, pressed_fill, scrim_fill, selected_fill,
    set_text_scale, slider_track, type_body, type_section, type_small,
};
use rune::{
    Cursor, Cx, El, Phase, ScrollSpec, SpringPair, TextStyle, Transition, col, el, row, text,
};
use serde_json::{Value, json};

#[derive(Debug, Clone)]
pub struct ShellChromeState {
    pub discord_badge_count: u32,
    pub discord_badge_revision: u64,
    pub window_maximized: bool,
    pub window_maximize_hover: bool,
    pub gamepad_kind: GamepadKind,
    pub gamepad_paused: bool,
    pub mini_volume_open: bool,
    pub mini_dragging_seek: bool,
    pub mini_dragging_vol: bool,
    pub mini_seek_preview: Option<f32>,
    pub mini_last_audible: f32,
    pub room_history: Vec<Room>,
    pub room_history_index: usize,
    pub last_heartbeat_at: Option<f64>,
    pub shell_ready_sent: bool,
    pub usable_logged: bool,
    pub settings_open_count: u32,
    pub last_telemetry_room: Option<Room>,
    pub last_chrome_layout_key: Option<String>,
    pub top_bar_width: f32,
}

impl Default for ShellChromeState {
    fn default() -> Self {
        Self {
            discord_badge_count: 0,
            discord_badge_revision: 0,
            window_maximized: false,
            window_maximize_hover: false,
            gamepad_kind: GamepadKind::Xbox,
            gamepad_paused: false,
            mini_volume_open: false,
            mini_dragging_seek: false,
            mini_dragging_vol: false,
            mini_seek_preview: None,
            mini_last_audible: 0.25,
            room_history: vec![Room::Library],
            room_history_index: 0,
            last_heartbeat_at: None,
            shell_ready_sent: false,
            usable_logged: false,
            settings_open_count: 0,
            last_telemetry_room: None,
            last_chrome_layout_key: None,
            top_bar_width: 0.0,
        }
    }
}

pub fn register_events(reg: &mut EventRegistry) {
    reg.on_raw("friends.badge", |state, data| {
        let Some(raw) = data.get("count").and_then(|v| v.as_f64()) else {
            return false;
        };
        if !raw.is_finite() {
            return false;
        }
        state.chrome.discord_badge_revision = state.chrome.discord_badge_revision.saturating_add(1);
        let next = raw.max(0.0).floor() as u32;
        if state.chrome.discord_badge_count != next {
            state.chrome.discord_badge_count = next;
            true
        } else {
            false
        }
    });

    reg.on_raw("shell.window", |state, data| {
        let mut changed = false;
        if let Some(max) = data.get("maximized").and_then(|v| v.as_bool())
            && state.chrome.window_maximized != max
        {
            state.chrome.window_maximized = max;
            changed = true;
        }
        let hover = data
            .get("maximizeHover")
            .and_then(|v| v.as_bool())
            .unwrap_or(false);
        if state.chrome.window_maximize_hover != hover {
            state.chrome.window_maximize_hover = hover;
            changed = true;
        }
        changed
    });

    reg.on_raw("discord.open", |state, data| {
        state.switch_room(Room::Discord);
        let guild_id = data
            .get("guildId")
            .and_then(|v| v.as_str())
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string());
        let channel_id = data
            .get("channelId")
            .and_then(|v| v.as_str())
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string());
        if let Some(gid) = guild_id {
            state.open_discord_guild(&gid);
            if let Some(cid) = channel_id {
                state.open_discord_channel(&cid);
            }
        } else if let Some(cid) = channel_id {
            state.open_discord_dm(&cid);
        }
        true
    });

    reg.on_raw("app.open", |state, data| state.apply_app_open(data));

    reg.on_raw("notes.open", |state, data| {
        if let Some(id) = data.get("id").and_then(|v| v.as_str())
            && !id.is_empty()
        {
            state.open_captures(None);
            return true;
        }
        if let Some(msg) = data.get("message").and_then(|v| v.as_str())
            && !msg.is_empty()
        {
            state.push_notice(msg, crate::state::NoticeTone::Bad);
            return true;
        }
        false
    });
}

fn room_telemetry_name(room: Room) -> &'static str {
    match room {
        Room::Library => "library",
        Room::Store => "store",
        Room::Discord => "friends",
        Room::Music => "music",
        Room::Devices => "devices",
        Room::ExoAi => "exo-ai",
        Room::Apps => "apps",
        Room::Settings => "settings",
    }
}

impl AppState {
    pub fn apply_app_open(&mut self, data: &Value) -> bool {
        let room = data.get("room").and_then(|v| v.as_str());
        let part = data.get("part").and_then(|v| v.as_str());
        match room {
            Some("pc") => {
                match part {
                    Some("devices") => self.switch_room(Room::Devices),
                    Some("apps" | "startup" | "runtimes") => self.switch_room(Room::Apps),
                    Some("storage") => {
                        self.switch_room(Room::ExoAi);
                        self.pc.exo_ai_storage_open = true;
                        self.load_pc_drives();
                    }
                    _ => self.switch_room(Room::ExoAi),
                }
                true
            }
            Some("library") => {
                self.switch_room(Room::Library);
                true
            }
            _ => false,
        }
    }

    pub fn track_room_telemetry(&mut self, room: Room) {
        if self.chrome.last_telemetry_room == Some(room) {
            return;
        }
        self.chrome.last_telemetry_room = Some(room);
        if self.status != crate::state::HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            "telemetry.track",
            json!({ "room": room_telemetry_name(room) }),
            |_, _: Result<Value, String>| {},
        );
    }

    pub fn request_pending_open(&mut self) {
        if self.status != crate::state::HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            "app.pendingOpen",
            json!({}),
            |s, res: Result<Value, String>| {
                if let Ok(val) = res {
                    s.apply_app_open(&val);
                }
            },
        );
    }

    pub fn sync_chrome_layout(&mut self) {
        if self.status != crate::state::HostStatus::Connected {
            return;
        }
        let w = if self.chrome.top_bar_width > 0.0 {
            self.chrome.top_bar_width.round() as i32
        } else {
            1920
        };
        let modal = self.palette_open
            || self.key_modal_open
            || self.downloads.open
            || self.whats_new.mode.is_some()
            || self.whats_new.sheet_open
            || self.capture_menu_open
            || self.library_more_open
            || self.library_add_open
            || self.library_filter_open
            || self.context_menu.is_some()
            || self.open_dropdown.is_some()
            || self.chrome.mini_volume_open
            || self.island.person_menu.is_some();
        let payload = if modal {
            json!({
                "interactiveRegions": [{ "x": 0, "y": 6, "width": w, "height": 46 }]
            })
        } else {
            json!({
                "interactiveRegions": [
                    { "x": 10, "y": 8, "width": 200, "height": 36 },
                    { "x": (w / 2 - 300).max(220), "y": 8, "width": 600, "height": 36 },
                    { "x": (w - 203).max(0), "y": 8, "width": 193, "height": 36 }
                ],
                "maximizeButton": { "x": (w - 86).max(0), "y": 11, "width": 34, "height": 30 }
            })
        };
        let key = format!("{w}:{modal}:{}", room_telemetry_name(self.room));
        if self.chrome.last_chrome_layout_key.as_deref() == Some(key.as_str()) {
            return;
        }
        self.chrome.last_chrome_layout_key = Some(key);
        #[cfg(windows)]
        self.call_host_async(
            "shell.setChromeLayout",
            payload,
            |_, _: Result<Value, String>| {},
        );
    }

    pub fn tick_shell_heartbeat(&mut self, now: f64, tx: &mut rune::Tx) -> bool {
        if self.status != crate::state::HostStatus::Connected {
            return false;
        }
        let due = match self.chrome.last_heartbeat_at {
            None => true,
            Some(last) => now - last >= 1.0,
        };
        if due {
            self.chrome.last_heartbeat_at = Some(now);
            #[cfg(windows)]
            self.call_host_async(
                "shell.heartbeat",
                json!({ "visible": true }),
                |_, _: Result<Value, String>| {},
            );
        }
        if let Some(last) = self.chrome.last_heartbeat_at {
            tx.wake_at(last + 1.0);
        }
        false
    }

    pub fn on_room_switched(&mut self, prev: Room, next: Room) {
        if self.status != crate::state::HostStatus::Connected {
            return;
        }
        self.track_room_telemetry(next);
        self.request_pending_open();
        if next == Room::Settings && prev != Room::Settings {
            self.chrome.settings_open_count = self.chrome.settings_open_count.saturating_add(1);
            let open = self.chrome.settings_open_count;
            let from = room_telemetry_name(prev);
            #[cfg(windows)]
            self.call_host_async(
                "app.log",
                json!({
                    "level": "info",
                    "message": format!("PERF settings-open paintMs=0 open={open} from={from}")
                }),
                |_, _: Result<Value, String>| {},
            );
        }
        if next == Room::Library {
            self.sync_chrome_layout();
        }
    }

    pub fn load_shell_chrome_state(&mut self) {
        #[cfg(windows)]
        {
            self.call_host_async(
                "friends.badge",
                json!({}),
                |s, res: Result<Value, String>| {
                    if s.chrome.discord_badge_revision == 0
                        && let Ok(val) = res
                        && let Some(count) = val.get("count").and_then(|v| v.as_f64())
                        && count.is_finite()
                    {
                        s.chrome.discord_badge_count = count.max(0.0).floor() as u32;
                    }
                },
            );
            self.call_host_async(
                "shell.windowState",
                json!({}),
                |s, res: Result<Value, String>| {
                    if let Ok(val) = res
                        && let Some(max) = val.get("maximized").and_then(|v| v.as_bool())
                    {
                        s.chrome.window_maximized = max;
                    }
                },
            );
            self.call_host_async(
                "shell.heartbeat",
                json!({ "visible": true }),
                |_, _: Result<Value, String>| {},
            );
            if !self.chrome.shell_ready_sent {
                self.chrome.shell_ready_sent = true;
                self.call_host_async("shell.ready", json!({}), |_, _: Result<Value, String>| {});
            }
            self.notify_shell_painted_once();
            self.call_host_async(
                "music.volume",
                json!({}),
                |s, res: Result<Value, String>| {
                    if let Ok(val) = res
                        && let Some(level) = val.get("level").and_then(|v| v.as_f64())
                        && level.is_finite()
                    {
                        s.media.music_volume = level.clamp(0.0, 1.0);
                        s.media.music_muted = val
                            .get("muted")
                            .and_then(|v| v.as_bool())
                            .unwrap_or(s.media.music_volume <= 0.001);
                    }
                },
            );
            self.call_host_async("shell.prepare", json!({}), |_, _: Result<Value, String>| {});
            self.call_host_async(
                "notes.pending",
                json!({}),
                |s, res: Result<Value, String>| {
                    if let Ok(val) = res
                        && let Some(id) = val.get("id").and_then(|v| v.as_str())
                        && !id.is_empty()
                    {
                        s.open_captures(None);
                    }
                },
            );
            self.call_host_async(
                "art.base",
                json!({}),
                |s, res: Result<exo_rpc::ArtBase, String>| {
                    if let Ok(base) = res {
                        s.apply_art_base(base);
                    }
                },
            );
            self.call_host_async(
                exo_rpc::METHOD_CAPTURE_STATUS,
                json!({}),
                |s, res: Result<exo_rpc::CaptureStatusReply, String>| {
                    if let Ok(st) = res {
                        s.media.capture_status = Some(st);
                    }
                },
            );
            if !self.chrome.usable_logged {
                self.chrome.usable_logged = true;
                self.call_host_async(
                    "app.log",
                    json!({ "level": "info", "message": "Library usable" }),
                    |_, _: Result<Value, String>| {},
                );
            }
        }
        self.request_pending_open();
        self.track_room_telemetry(self.room);
        self.sync_chrome_layout();
        self.load_store_outages();
        self.hydrate_version_from_settings_json();
        self.startup_check_update();
    }

    pub fn record_room_history(&mut self, room: Room) {
        if self.chrome.room_history.get(self.chrome.room_history_index) == Some(&room) {
            return;
        }
        let next_idx = self.chrome.room_history_index + 1;
        self.chrome.room_history.truncate(next_idx);
        self.chrome.room_history.push(room);
        if self.chrome.room_history.len() > 32 {
            self.chrome.room_history.remove(0);
        }
        self.chrome.room_history_index = self.chrome.room_history.len().saturating_sub(1);
    }

    pub fn navigate_history(&mut self, delta: isize) -> bool {
        let cur = self.chrome.room_history_index as isize;
        let next = cur + delta;
        if next < 0 || next as usize >= self.chrome.room_history.len() {
            return false;
        }
        self.chrome.room_history_index = next as usize;
        let target = self.chrome.room_history[self.chrome.room_history_index];
        self.switch_room_without_history(target);
        true
    }

    pub fn seek_music_ms(&mut self, position_ms: i64) {
        let clamped = position_ms.max(0);
        if let Some(ref mut pb) = self.media.playback {
            pb.position_ms = Some(clamped);
        }
        #[cfg(windows)]
        self.call_host_async(
            "music.control",
            json!({ "action": "seek", "positionMs": clamped }),
            |s, res: Result<exo_rpc::ActionResult, String>| {
                if let Ok(r) = res
                    && !r.ok
                {
                    let msg = r
                        .message
                        .filter(|m| !m.trim().is_empty())
                        .unwrap_or_else(|| "This song can’t move to another time.".into());
                    s.push_notice(msg, crate::state::NoticeTone::Bad);
                }
            },
        );
    }

    pub fn toggle_mini_mute(&mut self) {
        let currently_silent = self.media.music_muted || self.media.music_volume <= 0.001;
        if currently_silent {
            let restore = (self.chrome.mini_last_audible as f64).clamp(0.05, 1.0);
            self.music_set_volume(restore, Some(false));
        } else {
            self.chrome.mini_last_audible = (self.media.music_volume as f32).clamp(0.05, 1.0);
            self.music_set_volume(0.0, Some(true));
        }
    }
}

fn mini_player_transition() -> Transition {
    let enter =
        Transition::offset(0.0, -8.0).combined(glass_enter(0.96, rune::Vec2::new(0.5, 0.5)));
    let exit = Transition::offset(0.0, -6.0).combined(Transition::scale(0.98));
    Transition::asymmetric(enter, exit).springs(SpringPair::new(SPRING_MOVE, SPRING_QUICK))
}

fn room_transition(dir: f32) -> Transition {
    let enter = Transition::offset(12.0 * dir, 0.0).combined(Transition::opacity());
    let exit = Transition::offset(-12.0 * dir, 0.0).combined(Transition::opacity());
    Transition::asymmetric(enter, exit).springs(SpringPair::new(SPRING_MOVE, SPRING_QUICK))
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BlurBudget {
    pub top_bar_search: bool,
    pub top_bar_nav: bool,
    pub top_bar_tools: bool,
    pub island: bool,
    pub mini_player: bool,
    pub discord_voice_bar: bool,
    pub overlay: bool,
    pub submenu: bool,
    pub toast: bool,
}

impl BlurBudget {
    pub fn compute(s: &AppState, has_toast: bool) -> Self {
        let submenu_open = s
            .context_menu
            .as_ref()
            .is_some_and(|cm| cm.open_submenu.is_some());
        let mini_visible = s.mini_bar_slot != MiniBarSlot::Hidden
            && s.room != Room::Music
            && s.media.open_capture_id.is_none()
            && s.media.playback.as_ref().is_some_and(|p| p.has_track());
        let art_under_bar = matches!(
            s.room,
            Room::Library | Room::Store | Room::Discord | Room::Music
        ) || s.media.captures_open
            || s.open_game_id.is_some()
            || s.store_game.is_some();
        let discord_active = s.room == Room::Discord && !s.media.captures_open;
        let voice_bar_visible = discord_active
            && (s
                .discord
                .active_channel()
                .is_some_and(|c| matches!(c.kind.as_str(), "voice" | "stage"))
                || s.discord
                    .voice
                    .channel_id
                    .as_deref()
                    .is_some_and(|vc| s.discord.active_dm_id.as_deref() == Some(vc)));
        let island_visible = s.island_visible();

        Self {
            top_bar_search: art_under_bar,
            top_bar_nav: art_under_bar,
            top_bar_tools: art_under_bar,
            island: island_visible && art_under_bar,
            mini_player: mini_visible,
            discord_voice_bar: voice_bar_visible,
            overlay: true,
            submenu: submenu_open,
            toast: has_toast,
        }
    }
}

pub fn room_icon(room: Room, active: bool) -> El {
    let color = if active { PRIMARY_FG } else { FG };
    match room {
        Room::Library => icon_library(16.0, color),
        Room::Store => icon_store(16.0, color),
        Room::Discord => icon_chat(16.0, color),
        Room::Music => icon_music(16.0, color),
        Room::Devices => icon_mouse(16.0, color),
        Room::ExoAi => icon_sparkles(16.0, color),
        Room::Apps => icon_grid(16.0, color),
        Room::Settings => icon_settings(16.0, color),
    }
}

fn discord_badge_el(count: u32) -> Option<El> {
    if count == 0 {
        return None;
    }
    let label = if count > 99 {
        "99+".to_string()
    } else {
        count.to_string()
    };
    Some(
        el().key("discord-nav-badge")
            .h(16.0)
            .min_w(16.0)
            .px(5.0)
            .radius(RADIUS_PILL)
            .fill(bad())
            .center()
            .child(text(label).font(TextStyle::new(10.5, 650.0)).color(FG)),
    )
}

fn mini_volume_popover_layers(s: &AppState, blur: bool) -> Vec<El> {
    if !s.chrome.mini_volume_open {
        return Vec::new();
    }
    let muted = s.media.music_muted || s.media.music_volume <= 0.001;
    let vol_pct = (s.media.music_volume.clamp(0.0, 1.0) * 100.0).round() as i32;
    let vol_w = 124.0;
    let slider = slider_track(
        "mini-vol-pop-slider",
        "Music volume",
        vol_w,
        if muted {
            0.0
        } else {
            s.media.music_volume as f32
        },
        s.chrome.mini_dragging_vol,
        false,
    )
    .on_drag(move |s: &mut AppState, d, _| {
        let frac = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
        if frac > 0.01 {
            s.chrome.mini_last_audible = frac;
        }
        match d.phase {
            Phase::Down | Phase::Move => {
                s.chrome.mini_dragging_vol = true;
                s.music_set_volume(frac as f64, Some(frac <= 0.001));
            }
            Phase::Up => {
                s.chrome.mini_dragging_vol = false;
                s.music_set_volume(frac as f64, Some(frac <= 0.001));
            }
        }
    });

    let mute_icon_btn = el()
        .key("mini-vol-pop-mute")
        .role("button")
        .label(if muted { "Unmute music" } else { "Mute music" })
        .hit()
        .cursor(Cursor::Pointer)
        .size(28.0, 28.0)
        .radius(RADIUS_PILL)
        .center()
        .hover(hover_fill())
        .on_press(|s: &mut AppState| s.toggle_mini_mute())
        .child(if muted {
            icon_volume_off(16.0, fg_muted())
        } else if vol_pct < 50 {
            icon_volume_low(16.0, fg_muted())
        } else {
            icon_volume(16.0, fg_muted())
        });

    let card = glass_panel_opt("mini-volume-popover", 216.0, blur)
        .pin(None, Some(52.0), Some(212.0), None)
        .radius(RADIUS_CARD)
        .px(10.0)
        .py(8.0)
        .transition(popover_transition())
        .child(
            row()
                .w_full()
                .gap(8.0)
                .center_y()
                .child(mute_icon_btn)
                .child(slider)
                .child(
                    text(format!("{vol_pct}%"))
                        .font(type_small())
                        .color(fg_muted()),
                ),
        );

    let scrim = el()
        .key("mini-vol-pop-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG.alpha(0.01))
        .hit()
        .on_press(|s: &mut AppState| s.chrome.mini_volume_open = false);

    vec![scrim, card]
}

pub fn view(s: &AppState, cx: &mut Cx<AppState>) -> El {
    cx.reduced_motion(s.settings.reduce_motion);
    set_text_scale(s.text_scale());
    let has_modal_scrim = s.palette_open
        || s.key_modal_open
        || s.downloads.open
        || s.whats_new.mode.is_some()
        || s.whats_new.sheet_open
        || s.store_ext.lightbox_index.is_some()
        || s.wp4.report_open
        || s.wp4_is_account_sheet_open()
        || s.media.captures_open
        || s.discord.settings_open
        || s.setup.beat.is_some();
    let budget = BlurBudget::compute(s, !s.notices.is_empty());
    cx.set_titlebar(exo_titlebar());
    cx.caption_color(if has_modal_scrim {
        fg_muted().alpha(0.28)
    } else {
        fg_muted()
    });

    let insets = cx.chrome_insets();
    let scale = cx.env().scale;

    if let Some(driver_screen) = boot::driver_install_overlay(s) {
        return el().w_full().h_full().fill(BG).child(driver_screen);
    }

    if let Some(gate) = boot::boot_splash_if_needed(s, budget.overlay) {
        return gate;
    }

    if s.setup.open {
        let mut root = setup::view(s, scale);
        if s.whats_new.sheet_open {
            root = root.children(whats_new::update_sheet_layers(s, budget.overlay));
        }
        if !s.notices.is_empty() {
            root = root.child(notice::view(s, scale, budget.toast));
        }
        return root;
    }
    if s.wp4_is_clients_gate_open() {
        let mut root = clients_gate::clients_start_view(s);
        if !s.notices.is_empty() {
            root = root.child(notice::view(s, scale, budget.toast));
        }
        return root;
    }

    let mut search = row()
        .key("search-capsule")
        .role("button")
        .label("Search")
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_scale(PRESS_SCALE)
        .w(200.0)
        .h(36.0)
        .pl(10.0)
        .pr(12.0)
        .gap(10.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if budget.top_bar_search {
            glass_control_fill()
        } else {
            glass_still_control_fill()
        })
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK)
        .on_press_tx(|s: &mut AppState, tx| {
            s.capture_menu_open = false;
            s.chrome.mini_volume_open = false;
            s.close_context_menu();
            s.close_dropdown();
            if s.palette_open {
                s.close_palette();
            } else {
                s.open_palette(tx);
            }
        })
        .on_secondary_press(|s: &mut AppState, p, _| {
            s.open_context_menu(ContextMenuTarget::SearchCapsule, p.x, p.y);
        })
        .child(exo_mark(18.0, FG))
        .child(text("Search").font(type_body()).color(FG));
    if budget.top_bar_search {
        search = search.backdrop_blur(GLASS_CONTROL_SIGMA);
    }

    let gamepad_focus = s.input_mode == InputMode::Gamepad;
    let discord_badge_count = s.chrome.discord_badge_count;
    let nav_px = if s.chrome.top_bar_width > 0.0 && s.chrome.top_bar_width < 1260.0 {
        10.0
    } else {
        12.5
    };
    let nav = glass_capsule_opt("nav-capsule", budget.top_bar_nav).children(
        Room::NAV_TABS.into_iter().map(|room| {
            let active = s.room == room;
            let badge = (room == Room::Discord)
                .then(|| discord_badge_el(discord_badge_count))
                .flatten();
            let badge_active = (room == Room::Discord)
                .then(|| discord_badge_el(discord_badge_count))
                .flatten();
            let base = row()
                .h(30.0)
                .px(nav_px)
                .gap(6.0)
                .center_y()
                .child(room_icon(room, false))
                .child(text(room.label()).font(type_section()).color(FG))
                .children(badge);
            let mut tab = el()
                .key(room.key())
                .role("tab")
                .label(room.label())
                .focusable(true)
                .hit()
                .cursor(Cursor::Pointer)
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .h(30.0)
                .radius(RADIUS_PILL)
                .fill(TRANSPARENT)
                .hover(if active { TRANSPARENT } else { hover_fill() })
                .press(if active { TRANSPARENT } else { pressed_fill() })
                .motion(SPRING_QUICK)
                .on_press(move |s: &mut AppState| {
                    s.input_mode = InputMode::Pointer;
                    s.switch_room(room);
                })
                .on_secondary_press(move |s: &mut AppState, p, _| {
                    s.open_context_menu(ContextMenuTarget::NavTab(room), p.x, p.y);
                })
                .child(base);
            if active {
                tab = tab.child(
                    row()
                        .key("nav-active-pill")
                        .matched("nav-active-pill")
                        .matched_crop()
                        .motion(SPRING_MOVE)
                        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                        .h(30.0)
                        .px(nav_px)
                        .gap(6.0)
                        .center_y()
                        .radius(RADIUS_PILL)
                        .fill(FG)
                        .child(room_icon(room, true))
                        .child(text(room.label()).font(type_section()).color(PRIMARY_FG))
                        .children(badge_active),
                );
            }
            if gamepad_focus {
                tab = tab.focus_ring(2.0, 3.0, FG);
            }
            tab
        }),
    );

    let live_cap = s
        .media
        .capture_status
        .as_ref()
        .and_then(|st| st.state.as_ref());
    let replay_on = live_cap.is_some_and(|l| l.buffering);
    let recording = live_cap.is_some_and(|l| l.recording);

    let capture_btn = if recording {
        row()
            .key("top-capture-rec")
            .role("button")
            .label("Stop recording")
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(28.0)
            .px(11.0)
            .mr(4.0)
            .gap(7.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_control_fill())
            .hover(hover_fill())
            .border(1.0, bad().alpha(0.40))
            .motion(SPRING_QUICK)
            .on_press(|s: &mut AppState| s.capture_act("record"))
            .on_secondary_press(|s: &mut AppState, p, _| {
                s.open_context_menu(ContextMenuTarget::CaptureButton, p.x, p.y);
            })
            .child(el().size(8.0, 8.0).radius(4.0).fill(bad()))
            .child(text("REC").font(type_small()).color(FG))
    } else {
        el().key("top-capture-btn")
            .role("button")
            .label("Capture")
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .size(32.0, 28.0)
            .mr(2.0)
            .center()
            .radius(RADIUS_PILL)
            .fill(if s.capture_menu_open {
                selected_fill()
            } else if s.media.captures_open {
                hover_fill()
            } else {
                TRANSPARENT
            })
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press(|s: &mut AppState| {
                s.close_context_menu();
                s.close_dropdown();
                s.chrome.mini_volume_open = false;
                s.capture_menu_open = !s.capture_menu_open;
            })
            .on_secondary_press(|s: &mut AppState, p, _| {
                s.open_context_menu(ContextMenuTarget::CaptureButton, p.x, p.y);
            })
            .child(icon_camera(
                16.0,
                if s.capture_menu_open { FG } else { fg_muted() },
            ))
            .children(replay_on.then(|| {
                el().pin(None, Some(5.0), Some(6.0), None)
                    .size(6.0, 6.0)
                    .radius(3.0)
                    .fill(good())
                    .outline(2.0, 0.0, BG)
            }))
    };

    let settings_active = s.room == Room::Settings;
    let settings_btn = el()
        .key("nav-settings")
        .role("button")
        .label("Settings")
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .size(34.0, 30.0)
        .center()
        .radius(RADIUS_PILL)
        .fill(if settings_active { FG } else { TRANSPARENT })
        .hover(if settings_active { FG } else { hover_fill() })
        .press(if settings_active { FG } else { pressed_fill() })
        .motion(SPRING_QUICK)
        .on_press(|s: &mut AppState| s.toggle_settings())
        .on_secondary_press(|s: &mut AppState, p, _| {
            s.open_context_menu(ContextMenuTarget::SettingsButton, p.x, p.y);
        })
        .child(icon_settings(
            16.0,
            if settings_active {
                PRIMARY_FG
            } else {
                fg_muted()
            },
        ));

    let divider = el().size(1.0, 16.0).mx(3.0).fill(selected_fill());

    let mut tools = row()
        .key("titlebar-tools")
        .h(36.0)
        .pl(CAPTION_CAPSULE_PAD)
        .pr(CAPTION_CAPSULE_PAD)
        .gap(CAPTION_BUTTON_GAP)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(if budget.top_bar_tools {
            glass_control_fill()
        } else {
            glass_still_control_fill()
        })
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .child(capture_btn)
        .child(settings_btn)
        .child(divider);
    if budget.top_bar_tools {
        tools = tools.backdrop_blur(GLASS_CONTROL_SIGMA);
    }

    if insets.trailing > 0.0 {
        let reserve = (insets.trailing - CAPTION_TRAILING_INSET).max(0.0);
        tools = tools.child(el().size(reserve, CAPTION_BUTTON_HEIGHT));
    } else {
        let maximized = s.chrome.window_maximized;
        let max_hover = s.chrome.window_maximize_hover;
        let win_min = el()
            .key("win-min")
            .role("button")
            .label("Minimize")
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .size(CAPTION_BUTTON_WIDTH, CAPTION_BUTTON_HEIGHT)
            .center()
            .radius(RADIUS_PILL)
            .fill(TRANSPARENT)
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press_tx(|_s: &mut AppState, tx| tx.minimize())
            .child(icon_minimize(16.0, fg_muted()));

        let win_max = el()
            .key("win-max")
            .role("button")
            .label(if maximized { "Restore" } else { "Maximize" })
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .size(CAPTION_BUTTON_WIDTH, CAPTION_BUTTON_HEIGHT)
            .center()
            .radius(RADIUS_PILL)
            .fill(if max_hover { hover_fill() } else { TRANSPARENT })
            .hover(hover_fill())
            .press(pressed_fill())
            .motion(SPRING_QUICK)
            .on_press_tx(|s: &mut AppState, tx| {
                s.chrome.window_maximized = !s.chrome.window_maximized;
                tx.toggle_maximized();
            })
            .child(if maximized {
                icon_win_restore(16.0, fg_muted())
            } else {
                icon_win_maximize(16.0, fg_muted())
            });

        let win_close = el()
            .key("win-close")
            .role("button")
            .label("Close")
            .focusable(true)
            .hit()
            .cursor(Cursor::Pointer)
            .press_on_down()
            .size(CAPTION_BUTTON_WIDTH, CAPTION_BUTTON_HEIGHT)
            .center()
            .radius(RADIUS_PILL)
            .fill(TRANSPARENT)
            .hover(bad())
            .press(bad().alpha(0.82))
            .motion(SPRING_QUICK)
            .on_press_tx(|_s: &mut AppState, tx| tx.close_window())
            .child(icon_close(16.0, fg_muted()));

        tools = tools.child(win_min).child(win_max).child(win_close);
    }

    let mini = (s.mini_bar_slot != MiniBarSlot::Hidden)
        .then(|| music::mini_player(s, scale, s.mini_bar_slot))
        .flatten()
        .map(|m| {
            let m = if budget.mini_player {
                m
            } else {
                m.backdrop_blur(0.0).fill(glass_still_control_fill())
            };
            m.transition(mini_player_transition())
        });

    let island_cap = island::capsule(s, scale, budget.island);

    let right_pin = GAP;

    let top_fade = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .h(74.0)
        .linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, BG.alpha(0.42)),
                (0.58, BG.alpha(0.16)),
                (1.0, BG.alpha(0.0)),
            ],
        );

    let island_call = discord_voice::island_call_capsule(s, budget.top_bar_nav);
    let top_bar = el()
        .key("top-bar")
        .window_drag()
        .pin(Some(0.0), Some(0.0), Some(0.0), None)
        .h(52.0)
        .on_layout(|s: &mut AppState, r| {
            if r[2] > 0.0 {
                if (s.chrome.top_bar_width - r[2]).abs() > 0.5 {
                    s.chrome.top_bar_width = r[2];
                    s.sync_chrome_layout();
                }
                let slot = MiniBarSlot::from_width(r[2]);
                if s.mini_bar_slot != slot {
                    s.mini_bar_slot = slot;
                }
            }
        })
        .child(
            row()
                .pin(Some(GAP), Some(8.0), None, None)
                .h(36.0)
                .center_y()
                .gap(8.0)
                .child(search)
                .children(island_cap),
        )
        .child(
            row()
                .pin(Some(0.0), Some(8.0), Some(0.0), None)
                .h(36.0)
                .gap(8.0)
                .justify_center()
                .center_y()
                .children(island_call)
                .child(nav),
        )
        .child(
            row()
                .pin(None, Some(8.0), Some(right_pin), None)
                .h(36.0)
                .center_y()
                .gap(8.0)
                .children(mini)
                .child(tools),
        )
        .children(has_modal_scrim.then(|| {
            el().key("top-bar-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .transition(modal_scrim_transition())
        }));

    let room_key = if s.media.captures_open {
        "room-captures"
    } else {
        match s.room {
            Room::Library => "room-library",
            Room::Store => "room-store",
            Room::Discord => "room-discord",
            Room::Music => "room-music",
            Room::Devices => "room-devices",
            Room::ExoAi => "room-exo-ai",
            Room::Apps => "room-apps",
            Room::Settings => "room-settings",
        }
    };

    let full_bleed_home =
        s.room == Room::Library && s.open_game_id.is_none() && !s.library_all_games;

    let content = boot::render_room_guarded(s, s.room.label(), || {
        if s.room == Room::Discord {
            discord::view(s, scale, budget)
        } else if s.room == Room::Settings {
            settings::view(s)
        } else if matches!(s.room, Room::Devices | Room::ExoAi | Room::Apps) {
            pc::view(s, scale)
        } else if full_bleed_home {
            library::view(s, scale)
        } else {
            let page = match s.room {
                Room::Library => {
                    if let Some(game) = s.open_game() {
                        game_page::view(s, game, scale)
                    } else {
                        library::view(s, scale)
                    }
                }
                Room::Store => store::view(s, scale),
                Room::Music => music::view(s, scale),
                Room::Devices | Room::ExoAi | Room::Apps => pc::view(s, scale),
                Room::Settings => settings::view(s),
                Room::Discord => discord::view(s, scale, budget),
            };

            col()
                .key(format!("{room_key}-scroll"))
                .w_full()
                .h_full()
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(page)
        }
    });

    let room_stage = el()
        .key(room_key)
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .transition(room_transition(s.room_dir))
        .child(content);

    let mut root = el()
        .w_full()
        .h_full()
        .fill(BG)
        .child(room_stage)
        .child(top_fade)
        .child(top_bar);

    if !s.media.captures_open && s.room == Room::Discord {
        root = root.children(discord::overlay_layers(s, budget.overlay));
    }
    if let Some(ring) = discord_voice::incoming_call_overlay(s, budget.overlay) {
        root = root.child(ring);
    }
    if s.media.captures_open {
        root = root.child(captures::view(s, scale));
    }
    if s.capture_menu_open {
        root = root.children(captures::popover_layers(s, budget.overlay));
    }
    if s.chrome.mini_volume_open {
        root = root.children(mini_volume_popover_layers(s, budget.overlay));
    }
    if s.island.person_menu.is_some() {
        root = root.children(island::person_menu_layers(s, budget.overlay));
    }
    if s.library_more_open {
        root = root.children(menus::library_more_overlay(s, budget.overlay));
    }
    if s.library_add_open {
        root = root.children(menus::library_add_overlay(s, budget.overlay));
    }
    if s.library_filter_open {
        root = root.children(crate::library::library_filter_overlay(s, budget.overlay));
    }
    if s.lib.gifts_open {
        root = root.children(crate::gifts::gifts_popover_overlay(
            s,
            scale,
            budget.overlay,
        ));
    }
    if s.lib.selecting && s.room == Room::Library && s.library_all_games {
        root = root.children(crate::select_bar::select_bar_overlay(s, budget.overlay));
    }
    if s.lib.open_folder_id.is_some() && s.room == Room::Library && s.open_game_id.is_none() {
        root = root.children(crate::folders::folder_panel_overlay(
            s,
            scale,
            budget.overlay,
        ));
    }
    if s.lib.folder_sheet.is_some() && s.room == Room::Library && s.open_game_id.is_none() {
        root = root.children(crate::folders::folder_sheet_overlay(
            s,
            scale,
            budget.overlay,
        ));
    }
    if s.pc.part_menu.is_some() {
        root = root.children(pc::part_menu_layers(s, budget.overlay));
    }
    if s.open_dropdown.is_some() {
        root = root.children(menus::dropdown_overlay(s, budget.overlay));
    }
    if s.context_menu.is_some() {
        root = root.children(menus::context_menu_overlay(
            s,
            budget.overlay,
            budget.submenu,
        ));
    }
    if s.gamepad_connected {
        root = root.child(gamepad::bar_view(s));
    }
    if s.setup.return_to.is_some() && !s.setup.open {
        root = root.child(setup::return_bar(s));
    }
    if s.downloads.open {
        root = root.children(downloads::layers(s, budget.overlay));
    }
    if s.store_ext.lightbox_index.is_some() {
        root = root.children(store_media::lightbox_layers(s, scale, budget.overlay));
    }
    if s.key_modal_open {
        root = root.children(key_modal::layers(s, budget.overlay));
    }
    if s.wp4.report_open {
        root = root.children(report::layers(s, budget.overlay));
    }
    if s.wp4_is_account_sheet_open() {
        root = root.children(settings_account::account_sheet_layers(s, budget.overlay));
    }
    if matches!(s.status, HostStatus::Error(_)) && !s.wp4.report_open {
        root = root.child(
            settings::set_btn("host-error-report", "Report a problem", None)
                .pin(None, None, Some(GAP), Some(GAP))
                .on_press(|s: &mut AppState| s.wp4_open_report()),
        );
    }
    if s.palette_open {
        root = root.children(palette::layers(s, budget.overlay));
    }
    if s.whats_new.mode.is_some() {
        root = root.children(whats_new::whats_new_layers(s, budget.overlay));
    }
    if s.whats_new.sheet_open {
        root = root.children(whats_new::update_sheet_layers(s, budget.overlay));
    }
    if s.setup.beat.is_some() {
        root = root.children(setup::tour_layers(s, budget.overlay));
    }
    if !s.notices.is_empty() {
        root = root.child(notice::view(s, scale, budget.toast));
    }
    root
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::HostEvent;
    use rune::{Env, Runtime};

    #[test]
    fn titlebar_tools_capsule_matches_react_geometry() {
        let mut rt = Runtime::new(
            AppState::default(),
            view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.frame(0.0);
        let (tools, _) = rt.presented("titlebar-tools").expect("titlebar-tools");
        assert!((tools[0] - 1717.0).abs() < 1.0, "tools x={}", tools[0]);
        assert!((tools[1] - 8.0).abs() < 0.5, "tools y={}", tools[1]);
        assert!((tools[2] - 193.0).abs() < 1.0, "tools w={}", tools[2]);
        assert!((tools[3] - 36.0).abs() < 0.5, "tools h={}", tools[3]);
        assert!(
            ((tools[0] + tools[2]) - (1920.0 - GAP)).abs() < 1.0,
            "tools right={} expected {}",
            tools[0] + tools[2],
            1920.0 - GAP
        );
    }

    #[test]
    fn discord_badge_and_window_state_events_update_chrome() {
        let mut s = AppState::default();
        assert!(s.apply_host_event(HostEvent::Raw {
            event: "friends.badge".into(),
            data: json!({ "count": 12 }),
        }));
        assert_eq!(s.chrome.discord_badge_count, 12);

        assert!(s.apply_host_event(HostEvent::Raw {
            event: "shell.window".into(),
            data: json!({ "maximized": true, "maximizeHover": true }),
        }));
        assert!(s.chrome.window_maximized);
        assert!(s.chrome.window_maximize_hover);

        let mut rt = Runtime::new(s, view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        assert!(rt.presented("discord-nav-badge").is_some());
    }

    #[test]
    fn app_open_event_and_heartbeat_respect_connection_state() {
        let mut s = AppState::default();
        assert!(s.apply_host_event(HostEvent::Raw {
            event: "app.open".into(),
            data: json!({ "room": "pc", "part": "devices" }),
        }));
        assert_eq!(s.room, Room::Devices);

        assert!(s.apply_host_event(HostEvent::Raw {
            event: "app.open".into(),
            data: json!({ "room": "pc", "part": "apps" }),
        }));
        assert_eq!(s.room, Room::Apps);

        assert!(s.apply_host_event(HostEvent::Raw {
            event: "app.open".into(),
            data: json!({ "room": "library" }),
        }));
        assert_eq!(s.room, Room::Library);

        let mut tx = rune::Tx::default();
        assert!(!s.tick_shell_heartbeat(5.0, &mut tx));
        assert!(s.chrome.last_heartbeat_at.is_none());
    }

    #[test]
    fn search_capsule_press_does_not_open_palette_until_release() {
        let mut rt = Runtime::new(
            AppState::default(),
            view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.frame(0.0);
        let (x, y) = rt.find("search-capsule").expect("search-capsule");
        rt.pointer_move(x, y);
        rt.pointer_down(x, y);
        rt.frame(1.0 / 60.0);
        assert!(
            !rt.state.palette_open,
            "palette must stay closed while Search capsule is held down"
        );
        rt.pointer_up(x, y);
        rt.frame(2.0 / 60.0);
        assert!(rt.state.palette_open, "palette must open on click release");
    }

    #[test]
    fn nav_capsule_centered_at_react_width_on_1080p() {
        let mut rt = Runtime::new(
            AppState::default(),
            view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.frame(0.0);
        let (nav, _) = rt.presented("nav-capsule").expect("nav-capsule");
        let center_x = nav[0] + nav[2] * 0.5;
        assert!(
            (center_x - 960.0).abs() < 1.5,
            "nav center_x={center_x} expected ~960"
        );
        assert!(
            nav[2] >= 624.0 && nav[2] <= 648.0,
            "nav width={} expected ~636",
            nav[2]
        );
    }

    #[test]
    fn tour_modal_opens_centered_nine_step_welcome_and_steps_through_rooms() {
        let mut s = AppState::default();
        s.start_tour();
        assert_eq!(s.setup.beat, Some(0));
        let mut rt = Runtime::new(s, view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        let (card, _) = rt.presented("exo-tour-card").expect("exo-tour-card");
        let cx = card[0] + card[2] * 0.5;
        let cy = card[1] + card[3] * 0.5;
        assert!((cx - 960.0).abs() < 4.0, "tour card cx={cx}");
        assert!((cy - 540.0).abs() < 4.0, "tour card cy={cy}");
        assert!(rt.presented("exo-tour-next").is_some());
        assert!(rt.presented("exo-tour-skip").is_some());
        assert!(rt.presented("exo-tour-close").is_some());
        assert!(rt.presented("exo-tour-back").is_none());

        rt.update(|s, _| s.tour_go(3));
        rt.frame(1.0 / 60.0);
        assert_eq!(rt.state.room, Room::Store);
        assert!(rt.presented("exo-tour-back").is_some());

        rt.update(|s, _| s.tour_back());
        rt.frame(2.0 / 60.0);
        assert_eq!(rt.state.setup.beat, Some(2));
        assert_eq!(rt.state.room, Room::Library);

        rt.update(|s, _| s.finish_tour());
        rt.frame(3.0 / 60.0);
        assert!(rt.state.setup.beat.is_none());
    }
}
