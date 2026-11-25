use crate::devices_audio::{device_btn, device_icon_btn, device_panel, device_row, device_switch};
use crate::media_state::CaptureFilter;
use crate::notes;
use crate::pc_state::format_bytes;
use crate::snip_editor;
use crate::state::{AppState, HostStatus, InputMode};
use exo_kit::{
    BG, BadgeTone, ExoGlass, FG, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL, RADIUS_PILL,
    RADIUS_SHEET, RADIUS_SMALL, SPRING_MOVE, SPRING_QUICK, bad, danger_button, exo_glass, fg_faint,
    fg_muted, filter_pill, glass_control_fill, glass_panel_fill, glass_rim, good, hairline,
    hover_fill, icon_back, icon_camera, icon_check, icon_close, icon_external, icon_film,
    icon_folder, icon_gallery, icon_more_horiz, icon_notes, icon_pause, icon_play, icon_plus,
    icon_record_dot, icon_replay, icon_scissors, icon_settings, icon_sparkles, icon_star,
    icon_trash, icon_volume, icon_volume_off, popover_transition_for, pressed_fill, primary_button,
    progress_bar, scaled, scrim_fill, slider_track, status_badge, type_body, type_control,
    type_page, type_section, type_small, type_title, warn,
};
use exo_rpc::{
    ActionResult, CaptureEditReply, CaptureItem, CaptureItemReply, CaptureListReply,
    CaptureReelEvent, CaptureReelReply, CaptureSendReply, CaptureShrunkEvent, CaptureStatusReply,
    DiscordChannels, DiscordHome, METHOD_CAPTURE_ACT, METHOD_CAPTURE_EDIT, METHOD_CAPTURE_ITEM,
    METHOD_CAPTURE_LIST, METHOD_CAPTURE_REEL, METHOD_CAPTURE_REEL_CANCEL, METHOD_CAPTURE_SEND,
    METHOD_CAPTURE_SETTINGS, METHOD_CAPTURE_STATUS, METHOD_DISCORD_CHANNELS, METHOD_DISCORD_HOME,
    METHOD_SHELL_PICK_FOLDER, PickFolderReply, format_duration_ms,
};
use rune::color::oklch;
use rune::{
    Align, Color, Cursor, El, Key, KeyEvent, LightboxItem, LiquidGlass, Phase, ScrollSpec, Side,
    Spring, SurfaceFit, TextEdit, TextStyle, Tx, col, edit_access, el, lightbox, row, text,
    text_input,
};
use serde_json::json;
use std::collections::{HashMap, HashSet};

const SESSION_GAP_SECS: i64 = 3 * 60 * 60;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ReelPhase {
    #[default]
    Idle,
    Waiting,
    Working,
}

#[derive(Debug, Clone, Default)]
pub struct ReelUiState {
    pub phase: ReelPhase,
    pub percent: f64,
    pub ids: Vec<String>,
    pub message: Option<String>,
    pub can_wait: bool,
}

#[derive(Debug, Clone, Default)]
pub struct SendTarget {
    pub id: String,
    pub name: String,
    pub place: Option<String>,
    pub dm: bool,
}

#[derive(Debug, Clone, Default)]
pub struct SendToDialogState {
    pub open: bool,
    pub item_id: String,
    pub loading: bool,
    pub targets: Option<Vec<SendTarget>>,
    pub query: TextEdit,
    pub picked: Vec<String>,
    pub note: TextEdit,
    pub busy: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ClipEditorTab {
    #[default]
    Trim,
    Export,
}

#[derive(Debug, Clone, Default)]
pub struct CapturesUiState {
    pub list_failed: bool,
    pub filter_favorites: bool,
    pub selection: Option<HashSet<String>>,
    pub bulk_deleting: bool,
    pub sheet_note: Option<String>,
    pub empty_note: Option<String>,
    pub reel: ReelUiState,
    pub viewer_info_open: bool,
    pub viewer_share_open: bool,
    pub viewer_more_open: bool,
    pub viewer_shrinking: bool,
    pub send_dialog: SendToDialogState,
    pub clip_tab: ClipEditorTab,
    pub clip_speed: f32,
    pub clip_loop: bool,
    pub clip_export_busy: Option<String>,
    pub clip_note: Option<(String, bool)>,
    pub settings_open_choice: Option<String>,
    pub settings_row_error: Option<(String, String)>,
    pub settings_quality_drag: Option<i32>,
    pub listening_hotkey: Option<String>,
    pub picking_folder: bool,
}

fn clip_hue(name: &str) -> f32 {
    let mut h: u32 = 2166136261;
    for b in name.bytes() {
        h ^= b as u32;
        h = h.wrapping_mul(16777619);
    }
    (h % 360) as f32
}

fn keys_label(raw: &str) -> String {
    raw.split('+')
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .collect::<Vec<_>>()
        .join(" + ")
}

pub fn format_size_mb_gb(bytes: u64) -> String {
    if bytes >= 1024 * 1024 * 1024 {
        format!("{:.1} GB", bytes as f64 / (1024.0 * 1024.0 * 1024.0))
    } else {
        format!("{:.1} MB", (bytes as f64 / (1024.0 * 1024.0)).max(0.1))
    }
}

pub fn parse_iso_ymd(iso: &str) -> Option<(i32, u32, u32)> {
    let date = iso.get(..10)?;
    let mut parts = date.split('-');
    let y = parts.next()?.parse::<i32>().ok()?;
    let m = parts.next()?.parse::<u32>().ok()?;
    let d = parts.next()?.parse::<u32>().ok()?;
    Some((y, m, d))
}

pub fn parse_iso_epoch_secs(iso: &str) -> Option<i64> {
    let (y, m, d) = parse_iso_ymd(iso)?;
    let time = iso.get(11..19).unwrap_or("00:00:00");
    let mut tp = time.split(':');
    let hh = tp.next()?.parse::<i64>().ok()?;
    let mm = tp.next()?.parse::<i64>().ok()?;
    let ss = tp.next()?.parse::<i64>().ok()?;
    let days = days_from_civil(y, m, d);
    Some(days * 86_400 + hh * 3600 + mm * 60 + ss)
}

fn days_from_civil(y: i32, m: u32, d: u32) -> i64 {
    let y = y as i64 - if m <= 2 { 1 } else { 0 };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let mp = m as i64 + if m > 2 { -3 } else { 9 };
    let doy = (153 * mp + 2) / 5 + d as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

pub fn on_this_day_items(
    items: &[CaptureItem],
    now_ymd: (i32, u32, u32),
) -> Vec<(&CaptureItem, i32)> {
    let mut matched: Vec<(&CaptureItem, i32)> = items
        .iter()
        .filter_map(|item| {
            let (y, m, d) = parse_iso_ymd(&item.created)?;
            if y < now_ymd.0 && m == now_ymd.1 && d == now_ymd.2 {
                Some((item, now_ymd.0 - y))
            } else {
                None
            }
        })
        .collect();
    matched.sort_by(|a, b| b.0.created.cmp(&a.0.created));
    matched.truncate(6);
    matched
}

pub fn reel_of_game(items: &[&CaptureItem]) -> Vec<String> {
    let mut videos: Vec<&CaptureItem> = items
        .iter()
        .copied()
        .filter(|it| it.kind == "video" && !it.derived)
        .collect();
    videos.sort_by(|a, b| b.created.cmp(&a.created));
    let mut session: Vec<&CaptureItem> = Vec::new();
    for item in videos {
        if let Some(prev) = session.last()
            && let (Some(t_prev), Some(t_cur)) = (
                parse_iso_epoch_secs(&prev.created),
                parse_iso_epoch_secs(&item.created),
            )
            && t_prev - t_cur > SESSION_GAP_SECS
        {
            break;
        }
        session.push(item);
    }
    let starred: Vec<&CaptureItem> = session.iter().copied().filter(|it| it.favourite).collect();
    let picked = if starred.len() >= 2 { starred } else { session };
    if picked.len() >= 2 {
        picked.into_iter().map(|it| it.id.clone()).collect()
    } else {
        Vec::new()
    }
}

pub fn reel_of_picked(items: &[CaptureItem], selected: &HashSet<String>) -> Vec<String> {
    items
        .iter()
        .filter(|it| it.kind == "video" && selected.contains(&it.id))
        .map(|it| it.id.clone())
        .collect()
}

pub fn month_title(ym: &str) -> String {
    let mut parts = ym.split('-');
    let Some(y) = parts.next() else {
        return ym.to_string();
    };
    let month = match parts.next().and_then(|m| m.parse::<u32>().ok()) {
        Some(1) => "January",
        Some(2) => "February",
        Some(3) => "March",
        Some(4) => "April",
        Some(5) => "May",
        Some(6) => "June",
        Some(7) => "July",
        Some(8) => "August",
        Some(9) => "September",
        Some(10) => "October",
        Some(11) => "November",
        Some(12) => "December",
        _ => return ym.to_string(),
    };
    format!("{month} {y}")
}

fn current_ymd() -> (i32, u32, u32) {
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);
    let z = secs / 86_400 + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = y + if m <= 2 { 1 } else { 0 };
    (year as i32, m as u32, d as u32)
}

fn encoder_line(state: Option<&exo_rpc::CaptureLiveState>) -> Option<String> {
    let st = state?;
    let codec = st.codec.as_deref().filter(|s| !s.is_empty())?;
    let gpu = st.encoder.as_deref().map(|enc| {
        let mut s = enc.to_string();
        for suffix in [
            " HEVC Encoder MFT",
            " H.264 Encoder MFT",
            " H264 Encoder MFT",
            " AV1 Encoder MFT",
        ] {
            if let Some(trimmed) = s.strip_suffix(suffix) {
                s = trimmed.to_string();
                break;
            }
        }
        s
    });
    match gpu.filter(|g| !g.is_empty()) {
        Some(g) => Some(format!("{codec} on {g}")),
        None => Some(codec.to_string()),
    }
}

fn combo_from_key_event(ev: &KeyEvent) -> Option<String> {
    let name = match &ev.key {
        Key::Text(t) if t == " " => "Space".to_string(),
        Key::Text(t) if t.len() == 1 && t.chars().all(|c| c.is_ascii_alphanumeric()) => {
            t.to_ascii_uppercase()
        }
        Key::Command(c) if c.is_ascii_alphanumeric() => c.to_ascii_uppercase().to_string(),
        Key::Function(n) => format!("F{n}"),
        Key::PrintScreen => "PrintScreen".to_string(),
        Key::Insert => "Insert".to_string(),
        Key::Delete => "Delete".to_string(),
        Key::Home => "Home".to_string(),
        Key::End => "End".to_string(),
        Key::PageUp => "PageUp".to_string(),
        Key::PageDown => "PageDown".to_string(),
        Key::Pause => "Pause".to_string(),
        _ => return None,
    };
    let mut parts = Vec::new();
    if ev.mods.ctrl {
        parts.push("Ctrl");
    }
    if ev.mods.alt {
        parts.push("Alt");
    }
    if ev.mods.shift {
        parts.push("Shift");
    }
    parts.push(&name);
    Some(parts.join("+"))
}

impl AppState {
    pub fn apply_capture_shrunk(&mut self, ev: CaptureShrunkEvent) {
        self.media.captures_ui.viewer_shrinking = false;
        if let Some(next_item) = ev.item {
            if let Some(slot) = self
                .media
                .capture_items
                .iter_mut()
                .find(|c| c.id == next_item.id)
            {
                *slot = next_item;
            }
            let msg = format!(
                "Smaller now: {} to {}.",
                format_size_mb_gb(ev.before),
                format_size_mb_gb(ev.after)
            );
            if self.media.open_capture_id.as_deref() == Some(ev.id.as_str()) {
                self.media.viewer_note = Some((msg.clone(), false));
            }
            self.notice = Some(msg);
        } else {
            let msg = ev.message.unwrap_or_else(|| "Left as it was.".into());
            if self.media.open_capture_id.as_deref() == Some(ev.id.as_str()) {
                self.media.viewer_note = Some((msg.clone(), false));
            }
            self.notice = Some(msg);
        }
    }

    pub fn apply_capture_reel_update(&mut self, ev: CaptureReelEvent) {
        match ev.state.as_str() {
            "waiting" => {
                self.media.captures_ui.reel.phase = ReelPhase::Waiting;
                self.media.captures_ui.reel.message = None;
                self.media.captures_ui.reel.can_wait = false;
            }
            "working" => {
                self.media.captures_ui.reel.phase = ReelPhase::Working;
                self.media.captures_ui.reel.percent = ev.percent.clamp(0.0, 100.0);
                self.media.captures_ui.reel.message = None;
                self.media.captures_ui.reel.can_wait = false;
            }
            "done" => {
                self.media.captures_ui.reel.phase = ReelPhase::Idle;
                self.media.captures_ui.reel.percent = 0.0;
                self.media.captures_ui.reel.message = None;
                self.media.captures_ui.reel.can_wait = false;
                if let Some(item) = ev.item {
                    let id = item.id.clone();
                    let name = item.name.clone();
                    self.media.capture_items.retain(|it| it.id != id);
                    self.media.capture_items.insert(0, item);
                    if self.media.captures_open && !self.media.notes.open {
                        self.open_capture_item(&id);
                    } else if !name.is_empty() {
                        self.notice = Some(format!("{name} is ready."));
                    }
                }
            }
            "stopped" => {
                self.media.captures_ui.reel.phase = ReelPhase::Idle;
                self.media.captures_ui.reel.percent = 0.0;
                self.media.captures_ui.reel.message = None;
                self.media.captures_ui.reel.can_wait = false;
            }
            "failed" => {
                self.media.captures_ui.reel.phase = ReelPhase::Idle;
                self.media.captures_ui.reel.percent = 0.0;
                self.media.captures_ui.reel.can_wait = ev.wait;
                let msg = ev
                    .message
                    .filter(|m| !m.trim().is_empty())
                    .unwrap_or_else(|| "Exo couldn’t make the reel. Try again.".into());
                self.media.captures_ui.reel.message = Some(msg.clone());
                self.notice = Some(msg);
            }
            _ => {}
        }
    }

    pub fn capture_make_reel(&mut self, ids: Vec<String>, wait: bool) {
        if ids.len() < 2 {
            return;
        }
        self.media.captures_ui.reel.ids = ids.clone();
        self.media.captures_ui.reel.message = None;
        self.media.captures_ui.reel.can_wait = false;
        self.media.captures_ui.selection = None;
        if self.status == HostStatus::Standalone {
            self.media.captures_ui.reel.phase = ReelPhase::Working;
            self.media.captures_ui.reel.percent = 45.0;
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CaptureReelReply>(
                METHOD_CAPTURE_REEL,
                &json!({ "ids": ids, "wait": wait }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(r) if r.ok => {}
                        Ok(r) => {
                            s.media.captures_ui.reel.can_wait = r.wait;
                            s.media.captures_ui.reel.message =
                                Some(r.message.unwrap_or_else(|| {
                                    "Exo couldn’t make the reel. Try again.".into()
                                }));
                        }
                        Err(_) => {
                            s.media.captures_ui.reel.message =
                                Some("Exo couldn’t make the reel. Try again.".into());
                        }
                    });
                },
            );
        }
    }

    pub fn capture_cancel_reel(&mut self) {
        self.media.captures_ui.reel.phase = ReelPhase::Idle;
        self.media.captures_ui.reel.percent = 0.0;
        self.media.captures_ui.reel.message = None;
        self.media.captures_ui.reel.can_wait = false;
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(METHOD_CAPTURE_REEL_CANCEL, &json!({}), |_| {});
        }
    }

    pub fn capture_bulk_delete(&mut self, ids: Vec<String>) {
        if ids.is_empty() {
            return;
        }
        self.media.captures_ui.sheet_note = None;
        self.media.captures_ui.bulk_deleting = true;
        if self.status == HostStatus::Standalone {
            self.media.capture_items.retain(|it| !ids.contains(&it.id));
            self.media.captures_ui.selection = None;
            self.media.captures_ui.bulk_deleting = false;
            return;
        }
        let total = ids.len();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let remaining = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(total));
            for id in ids {
                let rem = remaining.clone();
                let h = handle.clone();
                let id_c = id.clone();
                rpc.call::<_, CaptureItemReply>(
                    METHOD_CAPTURE_ITEM,
                    &json!({ "id": id, "action": "delete" }),
                    move |res| {
                        let left = rem.fetch_sub(1, std::sync::atomic::Ordering::SeqCst) - 1;
                        h.update(move |s, _| {
                            match res {
                                Ok(r) if r.ok => {
                                    s.media.capture_items.retain(|it| it.id != id_c);
                                    if let Some(ref mut sel) = s.media.captures_ui.selection {
                                        sel.remove(&id_c);
                                    }
                                }
                                Ok(r) => {
                                    s.media.captures_ui.sheet_note = Some(r.message.unwrap_or_else(
                                        || {
                                            "Windows could not move it to the Recycle Bin. Try again."
                                                .into()
                                        },
                                    ));
                                }
                                Err(_) => {
                                    s.media.captures_ui.sheet_note = Some(
                                        "Windows could not move it to the Recycle Bin. Try again."
                                            .into(),
                                    );
                                }
                            }
                            if left == 0 {
                                s.media.captures_ui.bulk_deleting = false;
                                if s.media.captures_ui.sheet_note.is_none() {
                                    s.media.captures_ui.selection = None;
                                }
                            }
                        });
                    },
                );
            }
        }
        #[cfg(not(windows))]
        let _ = total;
    }

    pub fn capture_viewer_action(&mut self, id: &str, action: &'static str) {
        self.media.captures_ui.viewer_share_open = false;
        self.media.captures_ui.viewer_more_open = false;
        self.media.viewer_note = None;
        if action == "shrink" {
            self.media.captures_ui.viewer_shrinking = true;
            self.media.viewer_note = Some(("Making it smaller…".into(), false));
        }
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
        if self.status == HostStatus::Standalone {
            if action == "copy" {
                self.media.viewer_note = Some(("Copied".into(), false));
            }
            return;
        }
        let id_owned = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CaptureItemReply>(
                METHOD_CAPTURE_ITEM,
                &json!({ "id": id_owned, "action": action }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(r) if r.ok => {
                            if let Some(updated) = r.item
                                && let Some(slot) = s
                                    .media
                                    .capture_items
                                    .iter_mut()
                                    .find(|c| c.id == updated.id)
                            {
                                *slot = updated;
                            }
                            if action == "copy" {
                                s.media.viewer_note = Some(("Copied".into(), false));
                            } else if action == "shrink" {
                                s.media.viewer_note = Some((
                                    r.message.unwrap_or_else(|| "Making it smaller…".into()),
                                    false,
                                ));
                            }
                        }
                        Ok(r) => {
                            if action == "shrink" {
                                s.media.captures_ui.viewer_shrinking = false;
                            }
                            let fallback = match action {
                                "delete" => {
                                    "Windows could not move it to the Recycle Bin. Try again."
                                }
                                "reveal" => "Exo could not open its folder. Try again.",
                                "copy" => "Exo could not copy it. Try again.",
                                "shrink" => "Exo Capture could not make it smaller. Try again.",
                                _ => "Exo Capture didn’t answer. Try again.",
                            };
                            s.media.viewer_note =
                                Some((r.message.unwrap_or_else(|| fallback.into()), true));
                        }
                        Err(_) => {
                            if action == "shrink" {
                                s.media.captures_ui.viewer_shrinking = false;
                            }
                            s.media.viewer_note =
                                Some(("Exo Capture didn’t answer. Try again.".into(), true));
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = id_owned;
    }

    pub fn capture_open_send_dialog(&mut self, item_id: &str) {
        self.media.captures_ui.viewer_share_open = false;
        self.media.captures_ui.send_dialog = SendToDialogState {
            open: true,
            item_id: item_id.to_string(),
            loading: self.status != HostStatus::Standalone,
            targets: if self.status == HostStatus::Standalone {
                Some(vec![
                    SendTarget {
                        id: "dm-1".into(),
                        name: "Alex".into(),
                        place: None,
                        dm: true,
                    },
                    SendTarget {
                        id: "ch-1".into(),
                        name: "clips".into(),
                        place: Some("Squad".into()),
                        dm: false,
                    },
                ])
            } else {
                None
            },
            ..Default::default()
        };
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let rpc_ch = rpc.clone();
            rpc.call::<_, DiscordHome>(METHOD_DISCORD_HOME, &json!({}), move |res| {
                let Ok(home) = res else {
                    handle.update(|s, _| {
                        s.media.captures_ui.send_dialog.loading = false;
                        s.media.captures_ui.send_dialog.targets = Some(Vec::new());
                    });
                    return;
                };
                let dms: Vec<SendTarget> = home
                    .dms
                    .iter()
                    .map(|d| SendTarget {
                        id: d.id.clone(),
                        name: d.name.clone(),
                        place: None,
                        dm: true,
                    })
                    .collect();
                let guilds = home.guilds.clone();
                let h_dms = handle.clone();
                h_dms.update(move |s, _| {
                    s.media.captures_ui.send_dialog.loading = false;
                    s.media.captures_ui.send_dialog.targets = Some(dms);
                });
                for g in guilds {
                    let g_name = g.name.clone();
                    let h_g = handle.clone();
                    rpc_ch.call::<_, DiscordChannels>(
                        METHOD_DISCORD_CHANNELS,
                        &json!({ "guildId": g.id }),
                        move |ch_res| {
                            if let Ok(list) = ch_res {
                                let chs: Vec<SendTarget> = list
                                    .channels
                                    .into_iter()
                                    .filter(|c| c.kind == "text")
                                    .map(|c| SendTarget {
                                        id: c.id,
                                        name: c.name,
                                        place: Some(g_name.clone()),
                                        dm: false,
                                    })
                                    .collect();
                                h_g.update(move |s, _| {
                                    let vec = s
                                        .media
                                        .captures_ui
                                        .send_dialog
                                        .targets
                                        .get_or_insert_with(Vec::new);
                                    vec.extend(chs);
                                });
                            }
                        },
                    );
                }
            });
        }
    }

    pub fn capture_send_to_discord(&mut self) {
        let dlg = &mut self.media.captures_ui.send_dialog;
        if dlg.busy || dlg.picked.is_empty() {
            return;
        }
        let item_id = dlg.item_id.clone();
        let ids = dlg.picked.clone();
        let content = dlg.note.text.trim().to_string();
        let names: Vec<String> = ids
            .iter()
            .filter_map(|id| {
                dlg.targets
                    .as_ref()
                    .and_then(|t| t.iter().find(|x| x.id == *id))
                    .map(|t| t.name.clone())
            })
            .collect();
        dlg.busy = true;
        dlg.error = None;

        if self.status == HostStatus::Standalone {
            dlg.busy = false;
            dlg.open = false;
            let msg = if names.is_empty() {
                "Sent.".into()
            } else {
                format!("Sent to {}.", names.join(", "))
            };
            self.media.viewer_note = Some((msg, false));
            return;
        }

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CaptureSendReply>(
                METHOD_CAPTURE_SEND,
                &json!({
                    "id": item_id,
                    "channelIds": ids,
                    "content": content,
                    "startMs": 0,
                    "endMs": 0
                }),
                move |res| {
                    handle.update(move |s, _| {
                        s.media.captures_ui.send_dialog.busy = false;
                        match res {
                            Ok(reply) if reply.ok => {
                                if let Some(new_item) = reply.item {
                                    s.media.capture_items.retain(|it| it.id != new_item.id);
                                    s.media.capture_items.insert(0, new_item);
                                }
                                s.media.captures_ui.send_dialog.open = false;
                                let msg = if names.is_empty() {
                                    "Sent.".into()
                                } else {
                                    format!("Sent to {}.", names.join(", "))
                                };
                                s.media.viewer_note = Some((msg, false));
                            }
                            Ok(reply) => {
                                if !reply.sent.is_empty() {
                                    s.media
                                        .captures_ui
                                        .send_dialog
                                        .picked
                                        .retain(|id| !reply.sent.contains(id));
                                }
                                s.media.captures_ui.send_dialog.error =
                                    Some(reply.message.unwrap_or_else(|| {
                                        "Discord didn’t take it. Try again.".into()
                                    }));
                            }
                            Err(_) => {
                                s.media.captures_ui.send_dialog.error =
                                    Some("Discord didn’t take it. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (item_id, ids, content, names);
    }

    pub fn capture_step_viewer(&mut self, by: isize) {
        let Some(cur_id) = self.media.open_capture_id.clone() else {
            return;
        };
        let list: Vec<String> = self
            .media
            .capture_items
            .iter()
            .map(|c| c.id.clone())
            .collect();
        let Some(idx) = list.iter().position(|id| id == &cur_id) else {
            return;
        };
        let next = idx as isize + by;
        if next >= 0 && (next as usize) < list.len() {
            let next_id = list[next as usize].clone();
            self.media.snip_editor = None;
            self.media.clip_editor_open = false;
            self.media.viewer_note = None;
            self.open_capture_item(&next_id);
        }
    }

    pub fn capture_save_settings_patch(&mut self, row_key: &'static str, patch: serde_json::Value) {
        self.media.captures_ui.settings_row_error = None;
        self.media.captures_ui.settings_open_choice = None;
        if let Some(ref mut st) = self.media.capture_status
            && let Some(ref mut cfg) = st.settings
        {
            if let Some(v) = patch.get("replayMode").and_then(|v| v.as_str()) {
                cfg.replay_mode = v.to_string();
            }
            if let Some(v) = patch.get("replaySeconds").and_then(|v| v.as_i64()) {
                cfg.replay_seconds = v as i32;
            }
            if let Some(v) = patch.get("maxFps").and_then(|v| v.as_i64()) {
                cfg.max_fps = v as i32;
            }
            if let Some(v) = patch.get("quality").and_then(|v| v.as_i64()) {
                cfg.quality = v as i32;
            }
            if let Some(v) = patch.get("callVoices").and_then(|v| v.as_bool()) {
                cfg.call_voices = v;
            }
            if let Some(v) = patch.get("printScreen").and_then(|v| v.as_bool()) {
                cfg.print_screen = v;
            }
            if let Some(v) = patch.get("keepGb").and_then(|v| v.as_i64()) {
                cfg.keep_gb = v as i32;
            }
            if let Some(v) = patch.get("keepDays").and_then(|v| v.as_i64()) {
                cfg.keep_days = v as i32;
            }
            if let Some(v) = patch.get("saveReplayKeys").and_then(|v| v.as_str()) {
                cfg.save_replay_keys = v.to_string();
            }
            if let Some(v) = patch.get("recordKeys").and_then(|v| v.as_str()) {
                cfg.record_keys = v.to_string();
            }
            if let Some(v) = patch.get("snipKeys").and_then(|v| v.as_str()) {
                cfg.snip_keys = v.to_string();
            }
            if let Some(v) = patch.get("folder").and_then(|v| v.as_str()) {
                cfg.folder = v.to_string();
            }
        }
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CaptureStatusReply>(METHOD_CAPTURE_SETTINGS, &patch, move |res| {
                handle.update(move |s, _| match res {
                    Ok(st) => {
                        if !st.ok {
                            s.media.captures_ui.settings_row_error = Some((
                                row_key.into(),
                                st.message
                                    .clone()
                                    .unwrap_or_else(|| "Exo couldn’t save that. Try again.".into()),
                            ));
                        }
                        if st.settings.is_some() || st.state.is_some() {
                            s.media.capture_status = Some(st);
                        }
                    }
                    Err(_) => {
                        s.media.captures_ui.settings_row_error =
                            Some((row_key.into(), "Exo couldn’t save that. Try again.".into()));
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = row_key;
    }

    pub fn capture_pick_folder(&mut self) {
        if self.media.captures_ui.picking_folder || self.status == HostStatus::Standalone {
            return;
        }
        self.media.captures_ui.picking_folder = true;
        self.media.captures_ui.settings_row_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PickFolderReply>(
                METHOD_SHELL_PICK_FOLDER,
                &json!({ "title": "Choose where captures are saved" }),
                move |res| {
                    handle.update(move |s, _| {
                        s.media.captures_ui.picking_folder = false;
                        match res {
                            Ok(r) if r.ok => {
                                if let Some(path) = r.path {
                                    s.capture_save_settings_patch(
                                        "folder",
                                        json!({ "folder": path }),
                                    );
                                }
                            }
                            Ok(r) if r.cancelled => {}
                            Ok(r) => {
                                s.media.captures_ui.settings_row_error = Some((
                                    "folder".into(),
                                    r.message.unwrap_or_else(|| {
                                        "Exo couldn’t open the folder picker. Try again.".into()
                                    }),
                                ));
                            }
                            Err(_) => {
                                s.media.captures_ui.settings_row_error = Some((
                                    "folder".into(),
                                    "Exo couldn’t open the folder picker. Try again.".into(),
                                ));
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn capture_open_folder(&mut self) {
        self.media.captures_ui.settings_row_error = None;
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_CAPTURE_ACT,
                &json!({ "action": "openFolder" }),
                move |res| {
                    if let Ok(r) = res
                        && !r.ok
                    {
                        handle.update(move |s, _| {
                            s.media.captures_ui.settings_row_error = Some((
                                "folder".into(),
                                r.message.unwrap_or_else(|| {
                                    "Exo couldn’t open the captures folder.".into()
                                }),
                            ));
                        });
                    }
                },
            );
        }
    }

    pub fn capture_clip_export(&mut self, id: &str, preset: &'static str) {
        if self.media.captures_ui.clip_export_busy.is_some() {
            return;
        }
        let dur_ms = self
            .media
            .capture_items
            .iter()
            .find(|c| c.id == id)
            .and_then(|c| c.duration_ms)
            .unwrap_or(30_000.0);
        let start_ms = (dur_ms * self.media.trim_start_pct as f64).round();
        let end_ms = (dur_ms * self.media.trim_end_pct as f64).round();
        let trimmed = dur_ms > 0.0 && (start_ms > 250.0 || end_ms < dur_ms - 250.0);
        let send_end = if trimmed { end_ms } else { 0.0 };

        self.media.captures_ui.clip_export_busy = Some(preset.into());
        self.media.captures_ui.clip_note = None;

        let saved_msg = match preset {
            "trim" => "Saved the trim as a new clip.",
            "discord" => "Saved a copy for Discord, under 10 MB.",
            "youtube" => "Saved a full-quality copy.",
            "vertical" => "Saved a vertical copy.",
            "gif" => "Saved a GIF copy.",
            "webm" => "Saved a WebM copy.",
            _ => "Saved copy.",
        };

        if self.status == HostStatus::Standalone {
            self.media.captures_ui.clip_export_busy = None;
            self.media.captures_ui.clip_note = Some((saved_msg.into(), false));
            return;
        }

        let id_owned = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CaptureEditReply>(
                METHOD_CAPTURE_EDIT,
                &json!({ "id": id_owned, "preset": preset, "startMs": start_ms, "endMs": send_end }),
                move |res| {
                    handle.update(move |s, _| {
                        s.media.captures_ui.clip_export_busy = None;
                        match res {
                            Ok(r) if r.ok => {
                                s.media.captures_ui.clip_note = Some((saved_msg.into(), false));
                                if let Some(item) = r.item {
                                    s.media.capture_items.retain(|it| it.id != item.id);
                                    s.media.capture_items.insert(0, item);
                                }
                            }
                            Ok(r) => {
                                s.media.captures_ui.clip_note = Some((
                                    r.message.unwrap_or_else(|| {
                                        "Exo Capture couldn’t save the copy. Try again.".into()
                                    }),
                                    true,
                                ));
                            }
                            Err(_) => {
                                s.media.captures_ui.clip_note = Some((
                                    "Exo Capture couldn’t save the copy. Try again.".into(),
                                    true,
                                ));
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id_owned, send_end);
    }

    pub fn handle_captures_key(&mut self, ev: &KeyEvent, tx: &mut Tx) -> bool {
        if !self.media.captures_open {
            return false;
        }
        if self.media.notes.open {
            return self.handle_notes_key(ev, tx);
        }
        if self.media.snip_editor.is_some() {
            return self.handle_snip_editor_key(ev, tx);
        }
        if let Some(field) = self.media.captures_ui.listening_hotkey.clone() {
            if ev.key == Key::Escape {
                self.media.captures_ui.listening_hotkey = None;
                return true;
            }
            if let Some(combo) = combo_from_key_event(ev) {
                self.media.captures_ui.listening_hotkey = None;
                let patch = match field.as_str() {
                    "saveReplayKeys" => json!({ "saveReplayKeys": combo }),
                    "recordKeys" => json!({ "recordKeys": combo }),
                    "snipKeys" => json!({ "snipKeys": combo }),
                    _ => return true,
                };
                self.capture_save_settings_patch("hotkey", patch);
                return true;
            }
            return true;
        }
        if self.media.captures_ui.send_dialog.open {
            if ev.key == Key::Escape {
                self.media.captures_ui.send_dialog.open = false;
                return true;
            }
            return false;
        }
        if self.media.clip_editor_open {
            if ev.key == Key::Escape {
                self.media.clip_editor_open = false;
                return true;
            }
            if matches!(&ev.key, Key::Text(t) if t == " ") {
                self.toggle_clip_play();
                return true;
            }
            if matches!(&ev.key, Key::Text(t) if t == "l" || t == "L") {
                self.media.captures_ui.clip_loop = !self.media.captures_ui.clip_loop;
                if let Some(ref p) = self.media.clip_player {
                    p.set_looping(self.media.captures_ui.clip_loop);
                }
                return true;
            }
            if matches!(&ev.key, Key::Text(t) if t == "[" || t == "]") {
                let speeds = [0.5_f32, 1.0, 2.0];
                let cur = speeds
                    .iter()
                    .position(|&v| (v - self.media.captures_ui.clip_speed).abs() < 0.05)
                    .unwrap_or(1);
                let next = if matches!(&ev.key, Key::Text(t) if t == "[") {
                    cur.saturating_sub(1)
                } else {
                    (cur + 1).min(speeds.len() - 1)
                };
                self.media.captures_ui.clip_speed = speeds[next];
                return true;
            }
            return false;
        }
        if self.media.open_capture_id.is_some() {
            match ev.key {
                Key::Escape => {
                    self.close_capture_item();
                    return true;
                }
                Key::Left => {
                    self.capture_step_viewer(-1);
                    return true;
                }
                Key::Right => {
                    self.capture_step_viewer(1);
                    return true;
                }
                Key::Text(ref t) if t == " " => {
                    self.toggle_clip_play();
                    return true;
                }
                _ => {}
            }
        }
        false
    }
}

fn popover_row(
    key: &str,
    label: &str,
    icon: El,
    hint: Option<String>,
    disabled: bool,
    gamepad_focus: bool,
) -> El {
    let mut r = row()
        .key(key)
        .role("menuitem")
        .label(label)
        .disabled(disabled)
        .focusable(!disabled)
        .hit()
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .w_full()
        .min_h(38.0)
        .px(12.0)
        .gap(10.0)
        .radius(RADIUS_SMALL)
        .continuous_corners()
        .center_y()
        .hover_motion(Spring::NONE, SPRING_QUICK)
        .motion(SPRING_QUICK);
    if !disabled {
        r = r
            .press_on_down()
            .press_scale(0.985)
            .focus_highlight(hover_fill())
            .press(pressed_fill());
        if gamepad_focus {
            r = r.focus_ring(2.0, 3.0, FG);
        }
    }
    r = r.child(icon).child(
        text(label)
            .font(type_control())
            .color(if disabled { fg_faint() } else { FG })
            .grow(1.0)
            .min_w(0.0),
    );
    if let Some(h) = hint
        && !h.is_empty()
    {
        r = r.child(text(h).font(type_small()).color(fg_faint()));
    }
    r
}

pub fn popover_layers(s: &AppState, blur: bool) -> Vec<El> {
    let status = s.media.capture_status.as_ref();
    let live = status.and_then(|st| st.state.as_ref());
    let cfg = status.and_then(|st| st.settings.as_ref());
    let present = live.map_or(status.is_none_or(|st| st.present), |l| l.present);
    let buffering = live.is_some_and(|l| l.buffering || l.replay);
    let recording = live.is_some_and(|l| l.recording);
    let gamepad_focus = s.input_mode == InputMode::Gamepad;

    let replay_hint = cfg
        .map(|c| keys_label(&c.save_replay_keys))
        .filter(|k| !k.is_empty())
        .or_else(|| Some("Ctrl + Alt + F10".into()));
    let record_hint = cfg
        .map(|c| keys_label(&c.record_keys))
        .filter(|k| !k.is_empty())
        .or_else(|| Some("Ctrl + Alt + F9".into()));
    let snip_hint = cfg
        .map(|c| keys_label(&c.snip_keys))
        .filter(|k| !k.is_empty())
        .or_else(|| Some("Ctrl + Alt + F8".into()));

    let anchor_key = if recording {
        "top-capture-rec"
    } else {
        "top-capture-btn"
    };

    let _ = blur;
    let mut menu = col()
        .key("capture-popover-menu")
        .w(240.0)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD)
        .hit()
        .cursor(Cursor::Default)
        .liquid_glass(LiquidGlass::panel())
        .transition(popover_transition_for(Side::Bottom, Align::End))
        .role("menu")
        .label("Capture")
        .focus_group()
        .modal(true, |s: &mut AppState| s.capture_menu_open = false)
        .modal_live(["top-bar"])
        .focus_return(anchor_key)
        .anchored_fit(anchor_key, Side::Bottom, Align::End, 8.0)
        .padding(8.0)
        .gap(2.0);

    if present {
        let replay_row = popover_row(
            "cap-menu-replay",
            "Save replay",
            icon_replay(16.0, if buffering { FG } else { fg_faint() }),
            replay_hint,
            !buffering,
            gamepad_focus,
        );
        let replay_row = if buffering {
            replay_row.on_press(|s: &mut AppState| {
                s.capture_menu_open = false;
                s.capture_act("saveReplay");
            })
        } else {
            replay_row
        };
        let divider = el().w_full().h(1.0).my(4.0).fill(hairline());
        menu = menu
            .child(replay_row)
            .child(
                popover_row(
                    "cap-menu-record",
                    if recording {
                        "Stop recording"
                    } else {
                        "Record"
                    },
                    icon_record_dot(16.0, if recording { bad() } else { FG }),
                    record_hint,
                    false,
                    gamepad_focus,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.capture_act("record");
                }),
            )
            .child(
                popover_row(
                    "cap-menu-snip",
                    "Snip",
                    icon_scissors(16.0, FG),
                    snip_hint,
                    false,
                    gamepad_focus,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.capture_act("snip");
                }),
            )
            .child(divider)
            .child(
                popover_row(
                    "cap-menu-note",
                    "New note",
                    icon_plus(16.0, FG),
                    None,
                    false,
                    gamepad_focus,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.open_notes(None, true);
                }),
            )
            .child(
                popover_row(
                    "cap-menu-notes",
                    "Notes",
                    icon_notes(16.0, FG),
                    None,
                    false,
                    gamepad_focus,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.open_notes(None, false);
                }),
            )
            .child(
                popover_row(
                    "cap-menu-captures",
                    "Captures",
                    icon_gallery(16.0, FG),
                    None,
                    false,
                    gamepad_focus,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.media.notes.open = false;
                    s.open_captures(None);
                }),
            )
            .child(
                popover_row(
                    "cap-menu-settings",
                    "Capture settings",
                    icon_settings(16.0, FG),
                    None,
                    false,
                    gamepad_focus,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.media.notes.open = false;
                    s.open_captures(None);
                    s.media.captures_settings_open = true;
                }),
            );
    } else {
        menu = menu
            .child(
                popover_row(
                    "cap-menu-note",
                    "New note",
                    icon_plus(16.0, FG),
                    None,
                    false,
                    gamepad_focus,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.open_notes(None, true);
                }),
            )
            .child(
                popover_row(
                    "cap-menu-notes",
                    "Notes",
                    icon_notes(16.0, FG),
                    None,
                    false,
                    gamepad_focus,
                )
                .on_press(|s: &mut AppState| {
                    s.capture_menu_open = false;
                    s.open_notes(None, false);
                }),
            );
    }

    vec![menu]
}

fn clip_card(item: &CaptureItem, s: &AppState, scale: f32, badge: Option<String>) -> El {
    let hue = clip_hue(&item.game);
    let thumb = item
        .thumb
        .as_deref()
        .and_then(|u| s.capture_thumb_image(u, scale));
    let selecting = s.media.captures_ui.selection.as_ref();
    let is_selected = selecting.is_some_and(|set| set.contains(&item.id));

    let mut preview = el()
        .w_full()
        .aspect_ratio(16.0 / 9.0)
        .radius(4.0)
        .clip_rounded(4.0)
        .border(
            if is_selected { 2.0 } else { 0.0 },
            if is_selected { FG } else { Color::TRANSPARENT },
        );
    if let Some(img) = thumb {
        preview = preview.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        preview = preview
            .linear_gradient(
                0.35,
                [(0.0, oklch(0.32, 0.10, hue)), (1.0, oklch(0.10, 0.03, hue))],
            )
            .center()
            .child(if item.kind == "video" {
                icon_film(24.0, FG.alpha(0.5))
            } else {
                icon_camera(24.0, FG.alpha(0.5))
            });
    }

    let mut overlay = el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0));
    if let Some(b) = badge {
        overlay = overlay.child(
            el().pin(Some(6.0), Some(6.0), None, None)
                .px(8.0)
                .py(2.0)
                .radius(RADIUS_PILL)
                .fill(BG.alpha(0.75))
                .child(text(b).font(TextStyle::new(11.0, 500.0)).color(FG)),
        );
    }
    if selecting.is_some() {
        overlay = overlay.child(
            el().pin(Some(6.0), None, None, Some(6.0))
                .size(22.0, 22.0)
                .radius(RADIUS_PILL)
                .fill(if is_selected { FG } else { BG.alpha(0.65) })
                .border(1.0, glass_rim())
                .center()
                .children(is_selected.then(|| icon_check(12.0, PRIMARY_FG))),
        );
    } else if item.favourite {
        let id_fav = item.id.clone();
        overlay = overlay.child(
            row().pin(Some(6.0), None, None, Some(6.0)).gap(4.0).child(
                el().key(format!("cap-card-fav-{}", item.id))
                    .role("button")
                    .cursor(Cursor::Pointer)
                    .hit()
                    .size(24.0, 24.0)
                    .radius(RADIUS_PILL)
                    .fill(BG.alpha(0.68))
                    .hover(hover_fill())
                    .center()
                    .on_press(move |s: &mut AppState| {
                        s.capture_viewer_action(&id_fav, "unfavourite");
                    })
                    .child(icon_star(12.0, warn())),
            ),
        );
    }
    if let Some(dur) = item.duration_ms {
        overlay = overlay.child(
            el().pin(None, None, Some(6.0), Some(6.0))
                .px(6.0)
                .py(2.0)
                .radius(RADIUS_PILL)
                .fill(BG.alpha(0.72))
                .child(
                    text(format_duration_ms(dur))
                        .font(TextStyle::new(11.0, 500.0))
                        .color(FG),
                ),
        );
    }

    let id = item.id.clone();
    el().key(format!("cap-card-{}", item.id))
        .role("button")
        .label(item.name.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .grow(1.0)
        .min_w(0.0)
        .radius(4.0)
        .motion(SPRING_MOVE)
        .on_press(move |s: &mut AppState| {
            if let Some(ref mut set) = s.media.captures_ui.selection {
                if !set.remove(&id) {
                    set.insert(id.clone());
                }
            } else {
                s.media.viewer_note = None;
                s.media.clip_editor_open = false;
                s.media.snip_editor = None;
                s.open_capture_item(&id);
            }
        })
        .child(preview.child(overlay))
}

fn send_to_dialog_overlay(s: &AppState) -> Option<El> {
    let dlg = &s.media.captures_ui.send_dialog;
    if !dlg.open {
        return None;
    }
    let q = dlg.query.text.trim().to_lowercase();
    let targets = dlg.targets.as_deref();
    let shown: Vec<&SendTarget> = targets
        .unwrap_or(&[])
        .iter()
        .filter(|t| {
            q.is_empty()
                || t.name.to_lowercase().contains(&q)
                || t.place
                    .as_deref()
                    .is_some_and(|p| p.to_lowercase().contains(&q))
        })
        .take(60)
        .collect();

    let mut list = col()
        .key("cap-send-list")
        .w_full()
        .h(260.0)
        .gap(2.0)
        .scroll(ScrollSpec {
            stick_bottom: false,
        });

    if targets.is_none() || dlg.loading {
        list = list.child(
            text("Loading conversations…")
                .font(type_small())
                .color(fg_muted()),
        );
    } else if targets.is_some_and(|t| t.is_empty()) {
        list = list.child(
            text("Nowhere to send to. Sign in to Discord in Exo first.")
                .font(type_small())
                .color(fg_muted()),
        );
    } else {
        for t in shown {
            let on = dlg.picked.contains(&t.id);
            let tid = t.id.clone();
            list = list.child(
                row()
                    .key(format!("cap-send-row-{}", t.id))
                    .role("option")
                    .focusable(true)
                    .hit()
                    .press_on_down()
                    .cursor(Cursor::Pointer)
                    .w_full()
                    .px(10.0)
                    .py(8.0)
                    .gap(8.0)
                    .radius(RADIUS_SMALL)
                    .fill(if on { hover_fill() } else { Color::TRANSPARENT })
                    .hover(hover_fill())
                    .justify_between()
                    .center_y()
                    .on_press(move |s: &mut AppState| {
                        let picked = &mut s.media.captures_ui.send_dialog.picked;
                        if let Some(pos) = picked.iter().position(|x| x == &tid) {
                            picked.remove(pos);
                        } else if picked.len() < 5 {
                            picked.push(tid.clone());
                        }
                    })
                    .child(
                        col()
                            .grow(1.0)
                            .min_w(0.0)
                            .child(
                                text(if t.dm {
                                    t.name.clone()
                                } else {
                                    format!("#{}", t.name)
                                })
                                .font(type_body())
                                .color(FG)
                                .truncate(),
                            )
                            .children(
                                t.place
                                    .as_ref()
                                    .map(|p| text(p.clone()).font(type_small()).color(fg_muted())),
                            ),
                    )
                    .child(
                        el().size(18.0, 18.0)
                            .radius(4.0)
                            .fill(if on { good() } else { glass_control_fill() })
                            .border(1.0, glass_rim())
                            .center()
                            .children(on.then(|| icon_check(11.0, PRIMARY_FG))),
                    ),
            );
        }
    }

    let send_label = if dlg.picked.len() > 1 {
        format!("Send to {}", dlg.picked.len())
    } else {
        "Send".into()
    };

    let card = col()
        .key("cap-send-modal")
        .w(440.0)
        .padding(20.0)
        .gap(12.0)
        .radius(RADIUS_SHEET)
        .liquid_glass(exo_glass(ExoGlass::Sheet, false))
        .child(text("Send to").font(type_title()).color(FG))
        .child(
            row()
                .w_full()
                .h(34.0)
                .px(10.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(
                    text_input(&dlg.query, type_body())
                        .key("cap-send-search")
                        .label("Search conversations")
                        .single_line()
                        .grow(1.0)
                        .color(FG)
                        .placeholder("Search", fg_faint())
                        .on_edit(edit_access(|s: &mut AppState| {
                            &mut s.media.captures_ui.send_dialog.query
                        })),
                ),
        )
        .child(list)
        .child(
            row()
                .w_full()
                .h(34.0)
                .px(10.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(
                    text_input(&dlg.note, type_body())
                        .key("cap-send-note")
                        .label("Optional message")
                        .single_line()
                        .grow(1.0)
                        .color(FG)
                        .placeholder("Add an optional message", fg_faint())
                        .on_edit(edit_access(|s: &mut AppState| {
                            &mut s.media.captures_ui.send_dialog.note
                        }))
                        .on_submit(|s: &mut AppState, _| s.capture_send_to_discord()),
                ),
        )
        .children(
            dlg.error
                .as_ref()
                .map(|err| text(err.clone()).font(type_small()).color(bad())),
        )
        .child(
            row()
                .w_full()
                .justify_end()
                .gap(8.0)
                .child(
                    device_btn("cap-send-cancel", "Cancel", dlg.busy)
                        .on_press(|s: &mut AppState| s.media.captures_ui.send_dialog.open = false),
                )
                .child(if dlg.picked.is_empty() || dlg.busy {
                    device_btn("cap-send-go", send_label, true)
                } else {
                    primary_button("cap-send-go", "Send", None)
                        .on_press(|s: &mut AppState| s.capture_send_to_discord())
                }),
        );

    Some(
        el().key("cap-send-scrim")
            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
            .fill(scrim_fill())
            .center()
            .child(card),
    )
}

fn clip_editor_view(s: &AppState, item: &CaptureItem) -> El {
    let dur_ms = item.duration_ms.unwrap_or(30_000.0).max(1000.0);
    let start_ms = dur_ms * s.media.trim_start_pct as f64;
    let end_ms = dur_ms * s.media.trim_end_pct as f64;
    let kept_ms = (end_ms - start_ms).max(0.0);
    let trimmed = start_ms > 250.0 || end_ms < dur_ms - 250.0;
    let ui = &s.media.captures_ui;
    let busy = ui.clip_export_busy.is_some();
    let speed = if ui.clip_speed > 0.1 {
        ui.clip_speed
    } else {
        1.0
    };
    let dirty = trimmed || (speed - 1.0).abs() > 0.05 || ui.clip_loop;

    let id_trim = item.id.clone();
    let top_bar = row()
        .w_full()
        .px(20.0)
        .py(12.0)
        .gap(8.0)
        .center_y()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(
            device_btn("clip-ed-revert", "Revert", !dirty || busy).on_press(|s: &mut AppState| {
                s.media.trim_start_pct = 0.0;
                s.media.trim_end_pct = 1.0;
                s.media.captures_ui.clip_speed = 1.0;
                s.media.captures_ui.clip_loop = false;
            }),
        )
        .child(el().grow(1.0))
        .child(
            device_btn("clip-ed-cancel", "Cancel", busy)
                .on_press(|s: &mut AppState| s.media.clip_editor_open = false),
        )
        .child(if !trimmed || busy {
            device_btn(
                "clip-ed-save",
                if ui.clip_export_busy.as_deref() == Some("trim") {
                    "Saving…"
                } else {
                    "Save trim"
                },
                true,
            )
        } else {
            primary_button("clip-ed-save", "Save trim", None)
                .on_press(move |s: &mut AppState| s.capture_clip_export(&id_trim, "trim"))
        });

    let stage = if let Some(ref player) = s.media.clip_player {
        el().w_full()
            .grow(1.0)
            .min_h(0.0)
            .radius(RADIUS_PANEL)
            .clip_rounded(RADIUS_PANEL)
            .fill(BG)
            .border(1.0, glass_rim())
            .video(player, SurfaceFit::Fit)
    } else {
        el().w_full()
            .grow(1.0)
            .min_h(0.0)
            .radius(RADIUS_PANEL)
            .fill(BG)
            .border(1.0, glass_rim())
            .center()
            .child(icon_film(40.0, FG.alpha(0.6)))
    };

    let bar_w = 720.0_f32;
    let left_px = bar_w * s.media.trim_start_pct;
    let sel_w = (bar_w * (s.media.trim_end_pct - s.media.trim_start_pct)).max(12.0);
    let trim_strip = el()
        .key("clip-trim-bar")
        .w(bar_w)
        .h(36.0)
        .radius(RADIUS_SMALL)
        .fill(glass_control_fill())
        .border(1.0, glass_rim())
        .cursor(Cursor::Grab)
        .hit()
        .on_drag(move |s: &mut AppState, d, _| {
            let ratio = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
            match d.phase {
                Phase::Down => {
                    let dist_start = (ratio - s.media.trim_start_pct).abs();
                    let dist_end = (ratio - s.media.trim_end_pct).abs();
                    d.data = if dist_start <= dist_end { 0.0 } else { 1.0 };
                }
                Phase::Move | Phase::Up if !d.cancelled() => {
                    if d.data < 0.5 {
                        s.media.trim_start_pct = ratio.min((s.media.trim_end_pct - 0.03).max(0.0));
                    } else {
                        s.media.trim_end_pct = ratio.max((s.media.trim_start_pct + 0.03).min(1.0));
                    }
                }
                _ => {}
            }
        })
        .child(
            el().absolute(0.0, 0.0)
                .w(left_px)
                .h(36.0)
                .fill(BG.alpha(0.65)),
        )
        .child(
            el().absolute(left_px + sel_w, 0.0)
                .w((bar_w - left_px - sel_w).max(0.0))
                .h(36.0)
                .fill(BG.alpha(0.65)),
        )
        .child(
            el().absolute(left_px, 0.0)
                .w(sel_w)
                .h(36.0)
                .radius(4.0)
                .border(2.0, FG),
        );

    let transport = row()
        .gap(8.0)
        .center_y()
        .child(
            device_icon_btn(
                "clip-ed-play",
                if s.media.clip_playing {
                    "Pause"
                } else {
                    "Play"
                },
                Some(if s.media.clip_playing {
                    icon_pause(14.0, FG)
                } else {
                    icon_play(14.0, FG)
                }),
                false,
            )
            .on_press(|s: &mut AppState| s.toggle_clip_play()),
        )
        .child(
            device_icon_btn(
                "clip-ed-mute",
                if s.media.clip_muted { "Muted" } else { "Audio" },
                Some(if s.media.clip_muted {
                    icon_volume_off(14.0, FG)
                } else {
                    icon_volume(14.0, FG)
                }),
                false,
            )
            .on_press(|s: &mut AppState| s.toggle_clip_mute()),
        )
        .child(
            text(format!(
                "{} – {} ({})",
                format_duration_ms(start_ms),
                format_duration_ms(end_ms),
                format_duration_ms(dur_ms)
            ))
            .font(type_small())
            .color(fg_muted()),
        );

    let left_col = col()
        .grow(1.0)
        .min_w(0.0)
        .h_full()
        .padding(16.0)
        .gap(12.0)
        .items_center()
        .child(stage)
        .child(trim_strip)
        .child(transport);

    let tabs = row()
        .gap(6.0)
        .child(
            filter_pill("clip-tab-trim", "Trim", ui.clip_tab == ClipEditorTab::Trim)
                .on_press(|s: &mut AppState| s.media.captures_ui.clip_tab = ClipEditorTab::Trim),
        )
        .child(
            filter_pill(
                "clip-tab-export",
                "Export",
                ui.clip_tab == ClipEditorTab::Export,
            )
            .on_press(|s: &mut AppState| s.media.captures_ui.clip_tab = ClipEditorTab::Export),
        );

    let mut panel = col().w_full().gap(12.0);
    if ui.clip_tab == ClipEditorTab::Trim {
        let speeds = row().gap(6.0).children(
            [(0.5_f32, "0.5×"), (1.0, "1×"), (2.0, "2×")]
                .into_iter()
                .map(|(rate, lbl)| {
                    filter_pill(format!("clip-spd-{lbl}"), lbl, (speed - rate).abs() < 0.05)
                        .on_press(move |s: &mut AppState| s.media.captures_ui.clip_speed = rate)
                }),
        );
        panel = panel
            .child(
                row()
                    .w_full()
                    .justify_between()
                    .child(text("Kept").font(type_small()).color(fg_muted()))
                    .child(
                        text(format!(
                            "{} of {}",
                            format_duration_ms(kept_ms),
                            format_duration_ms(dur_ms)
                        ))
                        .font(type_body())
                        .color(FG),
                    ),
            )
            .child(
                text("Drag the ends of the strip. Save keeps the trim as a new clip; the original stays.")
                    .font(type_small())
                    .color(fg_muted())
                    .wrap(),
            )
            .child(speeds)
            .child(
                filter_pill("clip-loop", "Loop", ui.clip_loop).on_press(|s: &mut AppState| {
                    s.media.captures_ui.clip_loop = !s.media.captures_ui.clip_loop;
                    if let Some(ref p) = s.media.clip_player {
                        p.set_looping(s.media.captures_ui.clip_loop);
                    }
                }),
            );
    } else {
        for (preset, label, detail) in [
            ("discord", "Discord", "Under 10 MB"),
            ("youtube", "16:9", "Full quality"),
            ("vertical", "9:16", "Shorts and Reels"),
        ] {
            let cid = item.id.clone();
            let this_busy = ui.clip_export_busy.as_deref() == Some(preset);
            panel = panel.child(device_row(
                label,
                Some(detail.into()),
                None,
                Some(
                    device_btn(
                        format!("clip-exp-{preset}"),
                        if this_busy { "Saving…" } else { "Save copy" },
                        busy,
                    )
                    .on_press(move |s: &mut AppState| s.capture_clip_export(&cid, preset)),
                ),
            ));
        }
        panel = panel.child(
            text(if trimmed {
                format!("A copy of the {} you kept.", format_duration_ms(kept_ms))
            } else {
                "A copy of the whole clip; trim it first to keep less.".into()
            })
            .font(type_small())
            .color(fg_muted())
            .wrap(),
        );
    }

    if let Some((ref msg, is_bad)) = ui.clip_note {
        panel = panel.child(text(msg.clone()).font(type_small()).color(if is_bad {
            bad()
        } else {
            good()
        }));
    }

    let inspector = col()
        .w(300.0)
        .h_full()
        .padding(16.0)
        .gap(16.0)
        .border_sides([0.0, 0.0, 0.0, 1.0], hairline())
        .child(tabs)
        .child(panel);

    col()
        .key("clip-editor-root")
        .w_full()
        .h_full()
        .child(top_bar)
        .child(
            row()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .child(left_col)
                .child(inspector),
        )
}

fn viewer_view(s: &AppState, item: &CaptureItem, scale: f32) -> El {
    if item.kind == "image" && s.media.snip_editor.is_some() {
        return snip_editor::view(s, item);
    }
    if item.kind == "video" && s.media.clip_editor_open {
        return clip_editor_view(s, item);
    }

    let ui = &s.media.captures_ui;
    let busy = ui.send_dialog.open || ui.viewer_shrinking;
    let id_edit = item.id.clone();
    let id_fav = item.id.clone();
    let id_del = item.id.clone();
    let is_img = item.kind == "image";
    let is_fav = item.favourite;

    let bar = row()
        .w_full()
        .px(20.0)
        .py(12.0)
        .gap(8.0)
        .center_y()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(
            device_icon_btn("cap-viewer-back", "Back", Some(icon_back(14.0, FG)), false)
                .on_press(|s: &mut AppState| s.close_capture_item()),
        )
        .child(
            col()
                .gap(2.0)
                .child(text(item.game.clone()).font(type_section()).color(FG))
                .child(
                    text(
                        item.created
                            .get(..16)
                            .unwrap_or(&item.created)
                            .replace('T', " "),
                    )
                    .font(type_small())
                    .color(fg_muted()),
                ),
        )
        .child(el().grow(1.0))
        .child(
            primary_button(
                if is_img {
                    "cap-viewer-edit"
                } else {
                    "cap-trim-go"
                },
                "Edit",
                Some(icon_scissors(14.0, PRIMARY_FG)),
            )
            .on_press(move |s: &mut AppState| {
                if is_img {
                    s.open_snip_editor(&id_edit);
                } else {
                    s.media.captures_ui.clip_note = None;
                    s.media.captures_ui.clip_speed = 1.0;
                    s.media.clip_editor_open = true;
                }
            }),
        )
        .child(
            device_icon_btn(
                "cap-viewer-share",
                "Share",
                Some(icon_external(14.0, FG)),
                busy,
            )
            .on_press(|s: &mut AppState| {
                s.media.captures_ui.viewer_share_open = !s.media.captures_ui.viewer_share_open;
                s.media.captures_ui.viewer_more_open = false;
            }),
        )
        .child(
            el().key("cap-viewer-fav")
                .role("button")
                .label(if is_fav {
                    "Remove from favorites"
                } else {
                    "Favorite"
                })
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .size(32.0, 32.0)
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .hover(hover_fill())
                .center()
                .on_press(move |s: &mut AppState| {
                    s.capture_viewer_action(
                        &id_fav,
                        if is_fav { "unfavourite" } else { "favourite" },
                    );
                })
                .child(icon_star(14.0, if is_fav { warn() } else { FG })),
        )
        .child(
            el().key("cap-viewer-info")
                .role("button")
                .label("Info")
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .size(32.0, 32.0)
                .radius(RADIUS_PILL)
                .fill(if ui.viewer_info_open {
                    hover_fill()
                } else {
                    glass_control_fill()
                })
                .border(1.0, glass_rim())
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| {
                    s.media.captures_ui.viewer_info_open = !s.media.captures_ui.viewer_info_open;
                })
                .child(icon_sparkles(14.0, FG)),
        )
        .child(
            el().key("cap-viewer-del")
                .role("button")
                .label("Delete")
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .size(32.0, 32.0)
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .hover(hover_fill())
                .center()
                .on_press(move |s: &mut AppState| s.capture_viewer_action(&id_del, "delete"))
                .child(icon_trash(14.0, bad())),
        )
        .child(
            el().key("cap-viewer-more")
                .role("button")
                .label("More")
                .cursor(if busy {
                    Cursor::NotAllowed
                } else {
                    Cursor::Pointer
                })
                .focusable(!busy)
                .hit()
                .press_on_down()
                .size(32.0, 32.0)
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| {
                    s.media.captures_ui.viewer_more_open = !s.media.captures_ui.viewer_more_open;
                    s.media.captures_ui.viewer_share_open = false;
                })
                .child(icon_more_horiz(16.0, FG)),
        );

    let hue = clip_hue(&item.game);
    let stage = if item.kind == "video" {
        if let Some(ref player) = s.media.clip_player {
            col()
                .grow(1.0)
                .h_full()
                .padding(16.0)
                .gap(10.0)
                .items_center()
                .child(
                    el().w_full()
                        .grow(1.0)
                        .min_h(0.0)
                        .radius(RADIUS_PANEL)
                        .clip_rounded(RADIUS_PANEL)
                        .fill(BG)
                        .border(1.0, glass_rim())
                        .video(player, SurfaceFit::Fit),
                )
                .child(
                    row()
                        .gap(8.0)
                        .center_y()
                        .child(
                            device_icon_btn(
                                "cap-play-toggle",
                                if s.media.clip_playing {
                                    "Pause"
                                } else {
                                    "Play"
                                },
                                Some(if s.media.clip_playing {
                                    icon_pause(14.0, FG)
                                } else {
                                    icon_play(14.0, FG)
                                }),
                                false,
                            )
                            .on_press(|s: &mut AppState| s.toggle_clip_play()),
                        )
                        .child(
                            device_icon_btn(
                                "cap-mute-toggle",
                                if s.media.clip_muted { "Muted" } else { "Audio" },
                                Some(if s.media.clip_muted {
                                    icon_volume_off(14.0, FG)
                                } else {
                                    icon_volume(14.0, FG)
                                }),
                                false,
                            )
                            .on_press(|s: &mut AppState| s.toggle_clip_mute()),
                        ),
                )
        } else {
            el().grow(1.0).h_full().padding(16.0).child(
                el().w_full()
                    .h_full()
                    .radius(RADIUS_PANEL)
                    .linear_gradient(
                        0.35,
                        [(0.0, oklch(0.30, 0.10, hue)), (1.0, oklch(0.10, 0.03, hue))],
                    )
                    .border(1.0, glass_rim())
                    .center()
                    .child(icon_film(36.0, FG.alpha(0.7))),
            )
        }
    } else if let Some(img) = s.capture_full_image(&item.url, scale) {
        let cur_url = item.url.clone();
        el().grow(1.0).h_full().px(12.0).pb(12.0).child(
            el().key("cap-viewer-img-zoom")
                .role("button")
                .cursor(Cursor::Default)
                .hit()
                .w_full()
                .h_full()
                .fill(BG)
                .pixels_fit(img.pixels(), SurfaceFit::Fit)
                .on_press(move |s: &mut AppState| {
                    let urls: Vec<String> = s
                        .media
                        .capture_items
                        .iter()
                        .filter(|c| c.kind == "image" && !c.url.is_empty())
                        .map(|c| c.url.clone())
                        .collect();
                    let idx = urls.iter().position(|u| u == &cur_url).unwrap_or(0);
                    s.media.lightbox_urls = if urls.is_empty() {
                        vec![cur_url.clone()]
                    } else {
                        urls
                    };
                    s.media.lightbox.open(idx);
                }),
        )
    } else {
        el().grow(1.0).h_full().padding(16.0).child(
            el().w_full()
                .h_full()
                .radius(RADIUS_PANEL)
                .linear_gradient(
                    0.35,
                    [(0.0, oklch(0.32, 0.10, hue)), (1.0, oklch(0.10, 0.03, hue))],
                )
                .border(1.0, glass_rim())
                .center()
                .child(icon_camera(40.0, FG.alpha(0.65))),
        )
    };

    let info_pane = ui.viewer_info_open.then(|| {
        let mut facts = vec![
            ("Game", item.game.clone()),
            (
                "Taken",
                item.created
                    .get(..16)
                    .unwrap_or(&item.created)
                    .replace('T', " "),
            ),
            (
                "Kind",
                if item.kind == "image" {
                    "Screenshot".into()
                } else if item.derived {
                    "Clip, made from another".into()
                } else {
                    "Clip".into()
                },
            ),
        ];
        if let Some(dur) = item.duration_ms {
            facts.push(("Length", format_duration_ms(dur)));
        }
        facts.push(("Size", format_size_mb_gb(item.size)));
        facts.push(("Folder", format!("Exo Captures › {}", item.game)));

        col()
            .w(280.0)
            .h_full()
            .padding(20.0)
            .gap(12.0)
            .border_sides([0.0, 0.0, 0.0, 1.0], hairline())
            .child(text("Info").font(type_section()).color(FG))
            .children(facts.into_iter().map(|(k, v)| {
                col()
                    .gap(2.0)
                    .child(text(k).font(type_small()).color(fg_muted()))
                    .child(text(v).font(type_body()).color(FG))
            }))
    });

    let mut overlays = Vec::new();
    if ui.viewer_share_open {
        let id_send = item.id.clone();
        let id_copy = item.id.clone();
        overlays.push(
            col()
                .key("cap-viewer-share-menu")
                .pin(Some(58.0), None, None, Some(180.0))
                .w(200.0)
                .padding(6.0)
                .gap(2.0)
                .radius(RADIUS_PANEL)
                .liquid_glass(exo_glass(ExoGlass::Menu, false))
                .child(
                    row()
                        .key("cap-share-discord")
                        .role("menuitem")
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .cursor(Cursor::Pointer)
                        .w_full()
                        .px(10.0)
                        .py(8.0)
                        .radius(RADIUS_SMALL)
                        .hover(hover_fill())
                        .on_press(move |s: &mut AppState| s.capture_open_send_dialog(&id_send))
                        .child(text("Send to Discord").font(type_control()).color(FG)),
                )
                .child(
                    row()
                        .key("cap-share-copy")
                        .role("menuitem")
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .cursor(Cursor::Pointer)
                        .w_full()
                        .px(10.0)
                        .py(8.0)
                        .radius(RADIUS_SMALL)
                        .hover(hover_fill())
                        .on_press(move |s: &mut AppState| s.capture_viewer_action(&id_copy, "copy"))
                        .child(
                            text(if is_img { "Copy picture" } else { "Copy file" })
                                .font(type_control())
                                .color(FG),
                        ),
                ),
        );
    }

    if ui.viewer_more_open {
        let id_extra = item.id.clone();
        let id_rev = item.id.clone();
        overlays.push(
            col()
                .key("cap-viewer-more-menu")
                .pin(Some(58.0), None, None, Some(20.0))
                .w(200.0)
                .padding(6.0)
                .gap(2.0)
                .radius(RADIUS_PANEL)
                .liquid_glass(exo_glass(ExoGlass::Menu, false))
                .child(if is_img {
                    row()
                        .key("cap-more-ocr")
                        .role("menuitem")
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .cursor(Cursor::Pointer)
                        .w_full()
                        .px(10.0)
                        .py(8.0)
                        .radius(RADIUS_SMALL)
                        .hover(hover_fill())
                        .on_press(move |s: &mut AppState| s.capture_viewer_copy_text(&id_extra))
                        .child(text("Copy text").font(type_control()).color(FG))
                } else {
                    row()
                        .key("cap-more-shrink")
                        .role("menuitem")
                        .focusable(!ui.viewer_shrinking)
                        .hit()
                        .press_on_down()
                        .cursor(Cursor::Pointer)
                        .w_full()
                        .px(10.0)
                        .py(8.0)
                        .radius(RADIUS_SMALL)
                        .hover(hover_fill())
                        .on_press(move |s: &mut AppState| {
                            if !s.media.captures_ui.viewer_shrinking {
                                s.capture_viewer_action(&id_extra, "shrink");
                            }
                        })
                        .child(text("Make smaller").font(type_control()).color(FG))
                })
                .child(
                    row()
                        .key("cap-more-reveal")
                        .role("menuitem")
                        .focusable(true)
                        .hit()
                        .press_on_down()
                        .cursor(Cursor::Pointer)
                        .w_full()
                        .px(10.0)
                        .py(8.0)
                        .radius(RADIUS_SMALL)
                        .hover(hover_fill())
                        .on_press(move |s: &mut AppState| {
                            s.capture_viewer_action(&id_rev, "reveal")
                        })
                        .child(text("Show in folder").font(type_control()).color(FG)),
                ),
        );
    }

    if let Some(send_ov) = send_to_dialog_overlay(s) {
        overlays.push(send_ov);
    }

    el().key("cap-viewer-root")
        .w_full()
        .h_full()
        .child(
            col()
                .w_full()
                .h_full()
                .child(bar)
                .child(
                    row()
                        .grow(1.0)
                        .w_full()
                        .min_h(0.0)
                        .child(stage)
                        .children(info_pane),
                )
                .children(s.media.viewer_note.as_ref().map(|(msg, is_bad)| {
                    el().w_full().px(20.0).py(10.0).child(
                        text(msg.clone()).font(type_small()).color(if *is_bad {
                            bad()
                        } else {
                            good()
                        }),
                    )
                })),
        )
        .children(overlays)
}

fn empty_state(s: &AppState) -> El {
    let status = s.media.capture_status.as_ref();
    let live = status.and_then(|st| st.state.as_ref());
    let cfg = status.and_then(|st| st.settings.as_ref());
    let present = live.map_or(status.is_none_or(|st| st.present), |l| l.present);

    if !present {
        return el().w_full().grow(1.0).center().child(
            text("Exo Capture isn't installed")
                .font(type_title())
                .color(FG),
        );
    }

    let way_btn = |key: &'static str,
                   label: &'static str,
                   icon: El,
                   hint: Option<String>,
                   action: &'static str| {
        row()
            .key(key)
            .role("button")
            .cursor(Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(PRESS_SCALE)
            .h(42.0)
            .px(16.0)
            .gap(10.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_control_fill())
            .border(1.0, glass_rim())
            .hover(hover_fill())
            .motion(SPRING_QUICK)
            .on_press(move |s: &mut AppState| {
                s.media.captures_ui.empty_note = None;
                let st = s.media.capture_status.as_ref();
                let buffering = st
                    .and_then(|x| x.state.as_ref())
                    .is_some_and(|l| l.buffering);
                let mode = st
                    .and_then(|x| x.settings.as_ref())
                    .map(|c| c.replay_mode.as_str())
                    .unwrap_or("games");
                if action == "saveReplay" && !buffering {
                    let msg = match mode {
                        "off" => "Instant replay is off. Turn it on in Capture settings.",
                        "games" => "Instant replay runs only while a game is open.",
                        _ => "Instant replay isn't running.",
                    };
                    s.media.captures_ui.empty_note = Some(msg.into());
                    return;
                }
                s.capture_act(action);
            })
            .child(icon)
            .child(text(label).font(type_control()).color(FG))
            .children(hint.map(|h| {
                el().px(6.0)
                    .py(2.0)
                    .radius(4.0)
                    .fill(BG.alpha(0.5))
                    .border(1.0, hairline())
                    .child(text(h).font(type_small()).color(fg_muted()))
            }))
    };

    let mode_off = cfg.is_some_and(|c| c.replay_mode == "off");
    col()
        .w_full()
        .grow(1.0)
        .center()
        .gap(16.0)
        .child(text("No captures yet").font(type_title()).color(FG))
        .children(
            s.media
                .captures_ui
                .empty_note
                .as_ref()
                .map(|m| text(m.clone()).font(type_small()).color(bad())),
        )
        .children(
            (s.media.captures_ui.empty_note.is_some() && mode_off).then(|| {
                device_icon_btn(
                    "cap-empty-settings",
                    "Capture settings",
                    Some(icon_settings(14.0, FG)),
                    false,
                )
                .on_press(|s: &mut AppState| s.media.captures_settings_open = true)
            }),
        )
        .child(
            row()
                .gap(10.0)
                .flex_wrap()
                .child(way_btn(
                    "cap-empty-replay",
                    "Save replay",
                    icon_replay(16.0, FG),
                    cfg.map(|c| keys_label(&c.save_replay_keys))
                        .filter(|k| !k.is_empty())
                        .or_else(|| Some("Ctrl + Alt + F10".into())),
                    "saveReplay",
                ))
                .child(way_btn(
                    "cap-empty-record",
                    "Record",
                    icon_record_dot(16.0, FG),
                    cfg.map(|c| keys_label(&c.record_keys))
                        .filter(|k| !k.is_empty())
                        .or_else(|| Some("Ctrl + Alt + F9".into())),
                    "record",
                ))
                .child(way_btn(
                    "cap-empty-snip",
                    "Snip",
                    icon_scissors(16.0, FG),
                    cfg.map(|c| keys_label(&c.snip_keys))
                        .filter(|k| !k.is_empty())
                        .or_else(|| Some("Ctrl + Alt + F8".into())),
                    "snip",
                )),
        )
}

fn settings_err_for(s: &AppState, row_key: &str) -> Option<String> {
    s.media
        .captures_ui
        .settings_row_error
        .as_ref()
        .filter(|(k, _)| k == row_key)
        .map(|(_, m)| m.clone())
}

struct SettingsChoiceSpec<'a> {
    row_key: &'static str,
    label: &'static str,
    detail: Option<String>,
    current: &'a str,
    choices: &'static [(&'static str, &'static str)],
    disabled: bool,
}

fn settings_choice_rows(
    s: &AppState,
    spec: SettingsChoiceSpec<'_>,
    on_pick: impl Fn(&mut AppState, &'static str) + Clone + 'static,
) -> Vec<El> {
    let row_key = spec.row_key;
    let is_open = s.media.captures_ui.settings_open_choice.as_deref() == Some(row_key);
    let cur_lbl = spec
        .choices
        .iter()
        .find(|(v, _)| *v == spec.current)
        .map(|(_, l)| *l)
        .unwrap_or(spec.current);
    let mut out = vec![device_row(
        spec.label,
        spec.detail,
        settings_err_for(s, row_key),
        Some(
            device_btn(format!("cap-set-ch-{row_key}"), cur_lbl, spec.disabled).on_press(
                move |s: &mut AppState| {
                    s.media.captures_ui.settings_open_choice =
                        if is_open { None } else { Some(row_key.into()) };
                },
            ),
        ),
    )];
    if is_open {
        let mut pills = row().w_full().flex_wrap().gap(6.0).py(4.0);
        for (val, lbl) in spec.choices {
            let v = *val;
            let f = on_pick.clone();
            pills = pills.child(
                filter_pill(
                    format!("cap-set-opt-{row_key}-{val}"),
                    *lbl,
                    *val == spec.current,
                )
                .on_press(move |s: &mut AppState| f(s, v)),
            );
        }
        out.push(pills);
    }
    out
}

fn settings_body(s: &AppState) -> El {
    let status = s.media.capture_status.as_ref();
    let live = status.and_then(|st| st.state.as_ref());
    let cfg = status.and_then(|st| st.settings.as_ref());
    if live.is_some_and(|l| !l.present) {
        return col().w_full().max_w(760.0).child(device_panel(
            "Exo Capture",
            vec![device_row(
                "Exo Capture",
                None,
                None,
                Some(status_badge("Not installed".into(), BadgeTone::Neutral)),
            )],
        ));
    }

    let disabled = cfg.is_none();
    let mode = cfg.map(|c| c.replay_mode.as_str()).unwrap_or("games");
    let replay_secs = cfg.map(|c| c.replay_seconds).unwrap_or(60).to_string();
    let fps = cfg.map(|c| c.max_fps).unwrap_or(60).to_string();
    let quality = s
        .media
        .captures_ui
        .settings_quality_drag
        .unwrap_or_else(|| cfg.map(|c| c.quality).unwrap_or(50))
        .clamp(0, 100);
    let call_voices = cfg.is_some_and(|c| c.call_voices);
    let print_screen = cfg.is_some_and(|c| c.print_screen);
    let keep_gb = cfg.map(|c| c.keep_gb).unwrap_or(0).to_string();
    let keep_days = cfg.map(|c| c.keep_days).unwrap_or(0).to_string();

    let mut rec_rows = Vec::new();
    rec_rows.extend(settings_choice_rows(
        s,
        SettingsChoiceSpec {
            row_key: "mode",
            label: "Instant replay",
            detail: None,
            current: mode,
            choices: &[
                ("games", "While playing"),
                ("always", "Always"),
                ("off", "Off"),
            ],
            disabled,
        },
        |s, v| s.capture_save_settings_patch("mode", json!({ "replayMode": v })),
    ));

    if mode != "off" {
        let mut detail_parts = Vec::new();
        if let Some(l) = live
            && l.buffering
        {
            match l.replay_retained_ms {
                Some(ms) => detail_parts.push(format!("{} available", format_duration_ms(ms))),
                None => detail_parts.push("Available time not reported".into()),
            }
        }
        if let (Some(c), Some(l)) = (cfg, live)
            && let Some(lim) = l.replay_limit_seconds
            && lim < c.replay_seconds as f64
        {
            detail_parts.push(format!(
                "Up to {} on this PC",
                format_duration_ms(lim * 1000.0)
            ));
        }
        let detail = (!detail_parts.is_empty()).then(|| detail_parts.join(" · "));
        rec_rows.extend(settings_choice_rows(
            s,
            SettingsChoiceSpec {
                row_key: "length",
                label: "Replay length",
                detail,
                current: &replay_secs,
                choices: &[
                    ("30", "30 s"),
                    ("60", "1 min"),
                    ("120", "2 min"),
                    ("300", "5 min"),
                    ("600", "10 min"),
                ],
                disabled,
            },
            |s, v| {
                let n = v.parse::<i32>().unwrap_or(60);
                s.capture_save_settings_patch("length", json!({ "replaySeconds": n }));
            },
        ));
    }

    rec_rows.extend(settings_choice_rows(
        s,
        SettingsChoiceSpec {
            row_key: "fps",
            label: "Frame rate",
            detail: None,
            current: &fps,
            choices: &[("60", "60 fps"), ("120", "120 fps")],
            disabled,
        },
        |s, v| {
            let n = v.parse::<i32>().unwrap_or(60);
            s.capture_save_settings_patch("fps", json!({ "maxFps": n }));
        },
    ));

    let quality_slider = slider_track(
        "cap-quality-slider",
        "Quality",
        160.0,
        quality as f32 / 100.0,
        s.media.captures_ui.settings_quality_drag.is_some(),
        disabled,
    )
    .on_drag(|s: &mut AppState, d, _| {
        let ratio = (d.x / d.w.max(1.0)).clamp(0.0, 1.0);
        let stepped = ((ratio * 20.0).round() as i32 * 5).clamp(0, 100);
        match d.phase {
            Phase::Down | Phase::Move => s.media.captures_ui.settings_quality_drag = Some(stepped),
            Phase::Up => {
                s.media.captures_ui.settings_quality_drag = None;
                if !d.cancelled() {
                    s.capture_save_settings_patch("quality", json!({ "quality": stepped }));
                }
            }
        }
    });
    rec_rows.push(device_row(
        "Quality",
        encoder_line(live),
        settings_err_for(s, "quality"),
        Some(
            row()
                .gap(8.0)
                .center_y()
                .child(text("Smaller files").font(type_small()).color(fg_muted()))
                .child(quality_slider)
                .child(text("Best quality").font(type_small()).color(fg_muted())),
        ),
    ));

    rec_rows.push(device_row(
        "Record voice chat",
        None,
        settings_err_for(s, "callVoices"),
        Some(
            device_switch("cap-set-call", "Record voice chat", call_voices, disabled).on_press(
                move |s: &mut AppState| {
                    s.capture_save_settings_patch(
                        "callVoices",
                        json!({ "callVoices": !call_voices }),
                    );
                },
            ),
        ),
    ));

    for (label, field, keys, ok) in [
        (
            "Save replay",
            "saveReplayKeys",
            cfg.map(|c| c.save_replay_keys.as_str()).unwrap_or(""),
            live.is_none_or(|l| l.hotkeys.save_replay),
        ),
        (
            "Record",
            "recordKeys",
            cfg.map(|c| c.record_keys.as_str()).unwrap_or(""),
            live.is_none_or(|l| l.hotkeys.record),
        ),
        (
            "Snip",
            "snipKeys",
            cfg.map(|c| c.snip_keys.as_str()).unwrap_or(""),
            live.is_none_or(|l| l.hotkeys.snip),
        ),
    ] {
        let listening = s.media.captures_ui.listening_hotkey.as_deref() == Some(field);
        let btn_lbl = if listening {
            "Press keys".to_string()
        } else {
            let k = keys_label(keys);
            if k.is_empty() { "Set".into() } else { k }
        };
        let err = (!ok).then(|| "Another app already uses these keys. Pick others.".into());
        rec_rows.push(device_row(
            label,
            None,
            err,
            Some(
                device_btn(format!("cap-hk-{field}"), btn_lbl, disabled).on_press(
                    move |s: &mut AppState| {
                        s.media.captures_ui.listening_hotkey =
                            if listening { None } else { Some(field.into()) };
                    },
                ),
            ),
        ));
    }

    let print_blocked = live.is_some_and(|l| !l.hotkeys.print_screen) && print_screen;
    rec_rows.push(device_row(
        "Print Screen snips",
        None,
        settings_err_for(s, "printScreen").or_else(|| {
            print_blocked.then(|| {
                "Windows keeps Print Screen for Snipping Tool. Turn that off in Windows Settings > Accessibility > Keyboard."
                    .into()
            })
        }),
        Some(
            device_switch("cap-set-prtsc", "Print Screen snips", print_screen, disabled).on_press(
                move |s: &mut AppState| {
                    s.capture_save_settings_patch(
                        "printScreen",
                        json!({ "printScreen": !print_screen }),
                    );
                },
            ),
        ),
    ));

    let folder_detail = cfg
        .map(|c| c.folder.clone())
        .filter(|f| !f.is_empty())
        .or_else(|| status.and_then(|st| st.folder.clone()));
    let mut storage_rows = vec![device_row(
        "Save to",
        folder_detail,
        settings_err_for(s, "folder"),
        Some(
            row()
                .gap(8.0)
                .child(
                    device_btn("cap-set-folder-open", "Open", disabled)
                        .on_press(|s: &mut AppState| s.capture_open_folder()),
                )
                .child(
                    device_btn(
                        "cap-set-folder-change",
                        "Change",
                        disabled || s.media.captures_ui.picking_folder,
                    )
                    .on_press(|s: &mut AppState| s.capture_pick_folder()),
                ),
        ),
    )];

    storage_rows.extend(settings_choice_rows(
        s,
        SettingsChoiceSpec {
            row_key: "gb",
            label: "Max storage",
            detail: None,
            current: &keep_gb,
            choices: &[
                ("0", "No limit"),
                ("25", "25 GB"),
                ("50", "50 GB"),
                ("100", "100 GB"),
            ],
            disabled,
        },
        |s, v| {
            let n = v.parse::<i32>().unwrap_or(0);
            s.capture_save_settings_patch("gb", json!({ "keepGb": n }));
        },
    ));

    storage_rows.extend(settings_choice_rows(
        s,
        SettingsChoiceSpec {
            row_key: "days",
            label: "Auto-delete after",
            detail: None,
            current: &keep_days,
            choices: &[
                ("0", "Never"),
                ("7", "7 days"),
                ("30", "30 days"),
                ("90", "90 days"),
            ],
            disabled,
        },
        |s, v| {
            let n = v.parse::<i32>().unwrap_or(0);
            s.capture_save_settings_patch("days", json!({ "keepDays": n }));
        },
    ));

    col()
        .w_full()
        .max_w(760.0)
        .gap(16.0)
        .child(device_panel("Recording", rec_rows))
        .child(device_panel("Storage", storage_rows))
}

fn reel_banner(s: &AppState) -> Option<El> {
    let reel = &s.media.captures_ui.reel;
    match reel.phase {
        ReelPhase::Working => Some(
            row()
                .w_full()
                .padding(12.0)
                .gap(12.0)
                .center_y()
                .radius(RADIUS_PANEL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(
                    col()
                        .grow(1.0)
                        .gap(6.0)
                        .child(
                            text(format!("Making your reel · {:.0}%", reel.percent))
                                .font(type_body())
                                .color(FG),
                        )
                        .child(progress_bar(260.0, (reel.percent / 100.0) as f32)),
                )
                .child(
                    device_btn("cap-reel-cancel", "Cancel", false)
                        .on_press(|s: &mut AppState| s.capture_cancel_reel()),
                ),
        ),
        ReelPhase::Waiting => Some(
            row()
                .w_full()
                .padding(12.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .radius(RADIUS_PANEL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(
                    text("Waiting for your game to end")
                        .font(type_body())
                        .color(FG),
                )
                .child(
                    device_btn("cap-reel-cancel", "Cancel", false)
                        .on_press(|s: &mut AppState| s.capture_cancel_reel()),
                ),
        ),
        ReelPhase::Idle => {
            let msg = reel.message.as_ref()?;
            if reel.can_wait {
                let ids = reel.ids.clone();
                Some(
                    row()
                        .w_full()
                        .padding(12.0)
                        .gap(12.0)
                        .justify_between()
                        .center_y()
                        .radius(RADIUS_PANEL)
                        .fill(glass_control_fill())
                        .border(1.0, glass_rim())
                        .child(text(msg.clone()).font(type_small()).color(FG))
                        .child(device_btn("cap-reel-wait", "Wait", false).on_press(
                            move |s: &mut AppState| s.capture_make_reel(ids.clone(), true),
                        )),
                )
            } else {
                Some(
                    el().w_full()
                        .py(6.0)
                        .child(text(msg.clone()).font(type_small()).color(bad())),
                )
            }
        }
    }
}

pub fn view(s: &AppState, scale: f32) -> El {
    let sheet_inner = if s.media.notes.open {
        notes::view(s, scale)
    } else if let Some(item) = s
        .media
        .open_capture_id
        .as_deref()
        .and_then(|id| s.media.capture_items.iter().find(|c| c.id == id))
        && !s.media.captures_settings_open
    {
        viewer_view(s, item, scale)
    } else {
        let setting = s.media.captures_settings_open;
        let ui = &s.media.captures_ui;
        let present = s
            .media
            .capture_status
            .as_ref()
            .and_then(|st| st.state.as_ref())
            .is_none_or(|l| l.present);
        let any_items = !s.media.capture_items.is_empty();
        let selecting = ui.selection.as_ref();

        let top_month = s
            .media
            .capture_items
            .first()
            .and_then(|it| it.created.get(..7))
            .map(month_title);

        let mut head_actions = row().gap(8.0).center_y();
        if setting {
            head_actions = head_actions.child(
                device_icon_btn(
                    "cap-settings-back",
                    "All captures",
                    Some(icon_back(14.0, FG)),
                    false,
                )
                .on_press(|s: &mut AppState| s.media.captures_settings_open = false),
            );
        } else if let Some(sel) = selecting {
            let picked_videos = reel_of_picked(&s.media.capture_items, sel);
            let reel_n = picked_videos.len();
            let reel_disabled = reel_n < 2 || ui.bulk_deleting || ui.reel.phase != ReelPhase::Idle;
            let del_n = sel.len();
            let sel_ids: Vec<String> = sel.iter().cloned().collect();
            head_actions = head_actions
                .child(
                    device_btn(
                        "cap-sel-reel",
                        if reel_n > 1 {
                            format!("Make reel {reel_n}")
                        } else {
                            "Make reel".into()
                        },
                        reel_disabled,
                    )
                    .on_press(move |s: &mut AppState| {
                        s.capture_make_reel(picked_videos.clone(), false);
                    }),
                )
                .child(if del_n == 0 || ui.bulk_deleting {
                    device_btn(
                        "cap-sel-delete",
                        if del_n > 0 {
                            format!("Delete {del_n}")
                        } else {
                            "Delete".into()
                        },
                        true,
                    )
                } else {
                    danger_button("cap-sel-delete", "Delete", Some(icon_trash(14.0, bad())))
                        .on_press(move |s: &mut AppState| s.capture_bulk_delete(sel_ids.clone()))
                })
                .child(
                    device_btn("cap-sel-cancel", "Cancel", ui.bulk_deleting).on_press(
                        |s: &mut AppState| {
                            s.media.captures_ui.selection = None;
                        },
                    ),
                );
        } else {
            if any_items {
                head_actions = head_actions.child(
                    device_btn("cap-select-btn", "Select", false).on_press(|s: &mut AppState| {
                        s.media.captures_ui.selection = Some(HashSet::new());
                        s.media.captures_ui.sheet_note = None;
                    }),
                );
            }
            if present {
                head_actions = head_actions.child(
                    device_icon_btn(
                        "cap-open-settings",
                        "Settings",
                        Some(icon_settings(14.0, FG)),
                        false,
                    )
                    .on_press(|s: &mut AppState| s.media.captures_settings_open = true),
                );
            }
            head_actions = head_actions.child(
                device_icon_btn("cap-open-notes", "Notes", Some(icon_notes(14.0, FG)), false)
                    .on_press(|s: &mut AppState| s.open_notes(None, false)),
            );
            if present {
                head_actions = head_actions.child(
                    primary_button(
                        "cap-act-snip",
                        "Snip",
                        Some(icon_scissors(14.0, PRIMARY_FG)),
                    )
                    .on_press(|s: &mut AppState| {
                        s.close_captures();
                        s.capture_act("snip");
                    }),
                );
            }
        }
        head_actions = head_actions.child(
            el().key("cap-close")
                .role("button")
                .label("Close Captures")
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .size(32.0, 32.0)
                .radius(RADIUS_PILL)
                .liquid_glass(LiquidGlass::control())
                .hover(hover_fill())
                .center()
                .on_press(|s: &mut AppState| s.close_captures())
                .child(icon_close(16.0, FG)),
        );

        let header = row()
            .w_full()
            .px(20.0)
            .pt(20.0)
            .pb(14.0)
            .justify_between()
            .center_y()
            .child(
                row()
                    .gap(10.0)
                    .center_y()
                    .child(
                        text(if setting {
                            "Capture settings"
                        } else {
                            "Captures"
                        })
                        .font(type_page())
                        .color(FG),
                    )
                    .children(
                        (!setting && any_items)
                            .then(|| top_month.map(|m| status_badge(m, BadgeTone::Neutral)))
                            .flatten(),
                    ),
            )
            .child(head_actions);

        let body = if setting {
            col()
                .key("cap-settings-scroll")
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .px(20.0)
                .pb(20.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(settings_body(s))
        } else if s.media.capture_items.is_empty() && !ui.list_failed {
            empty_state(s)
        } else {
            let mut game_groups: Vec<(String, String)> = Vec::new();
            let mut seen_games: HashSet<String> = HashSet::new();
            for item in &s.media.capture_items {
                let key = item.game_id.clone().unwrap_or_else(|| item.game.clone());
                if seen_games.insert(key.clone()) {
                    let title = item
                        .game_id
                        .as_deref()
                        .and_then(|gid| s.games.iter().find(|g| g.id == gid))
                        .map(|g| g.title.clone())
                        .unwrap_or_else(|| item.game.clone());
                    game_groups.push((key, title));
                }
            }

            let in_game = |it: &CaptureItem| -> bool {
                match s.media.capture_game_filter.as_deref() {
                    None => true,
                    Some(gf) => {
                        it.game_id.as_deref() == Some(gf) || it.game.eq_ignore_ascii_case(gf)
                    }
                }
            };

            let filtered: Vec<&CaptureItem> = s
                .media
                .capture_items
                .iter()
                .filter(|c| {
                    let kind_ok = match s.media.capture_filter {
                        CaptureFilter::All => true,
                        CaptureFilter::Videos => c.kind == "video",
                        CaptureFilter::Screenshots => c.kind == "image",
                        CaptureFilter::Favorites => c.favourite,
                    };
                    let fav_ok = !ui.filter_favorites || c.favourite;
                    kind_ok && fav_ok && in_game(c)
                })
                .collect();

            let game_reel_ids = if s.media.capture_game_filter.is_some() && selecting.is_none() {
                let game_items: Vec<&CaptureItem> = s
                    .media
                    .capture_items
                    .iter()
                    .filter(|c| in_game(c))
                    .collect();
                reel_of_game(&game_items)
            } else {
                Vec::new()
            };

            let mut chips = row()
                .w_full()
                .pb(12.0)
                .gap(6.0)
                .flex_wrap()
                .center_y()
                .child(
                    filter_pill(
                        "cap-filter-clips",
                        "Clips",
                        s.media.capture_filter == CaptureFilter::Videos,
                    )
                    .on_press(|s: &mut AppState| {
                        s.media.capture_filter = if s.media.capture_filter == CaptureFilter::Videos
                        {
                            CaptureFilter::All
                        } else {
                            CaptureFilter::Videos
                        };
                    }),
                )
                .child(
                    filter_pill(
                        "cap-filter-shots",
                        "Screenshots",
                        s.media.capture_filter == CaptureFilter::Screenshots,
                    )
                    .on_press(|s: &mut AppState| {
                        s.media.capture_filter =
                            if s.media.capture_filter == CaptureFilter::Screenshots {
                                CaptureFilter::All
                            } else {
                                CaptureFilter::Screenshots
                            };
                    }),
                )
                .child(
                    filter_pill(
                        "cap-filter-favs",
                        "Favorites",
                        ui.filter_favorites || s.media.capture_filter == CaptureFilter::Favorites,
                    )
                    .on_press(|s: &mut AppState| {
                        s.media.captures_ui.filter_favorites =
                            !s.media.captures_ui.filter_favorites;
                        if s.media.capture_filter == CaptureFilter::Favorites {
                            s.media.capture_filter = CaptureFilter::All;
                        }
                    }),
                );

            if game_groups.len() > 1 || s.media.capture_game_filter.is_some() {
                for (gk, title) in &game_groups {
                    let on = s.media.capture_game_filter.as_deref() == Some(gk.as_str());
                    let gk_c = gk.clone();
                    chips = chips.child(
                        filter_pill(format!("cap-game-{gk}"), title.clone(), on).on_press(
                            move |s: &mut AppState| {
                                s.media.capture_game_filter =
                                    if on { None } else { Some(gk_c.clone()) };
                            },
                        ),
                    );
                }
            }

            if game_reel_ids.len() >= 2 {
                let n = game_reel_ids.len();
                let busy = ui.reel.phase != ReelPhase::Idle;
                chips = chips.child(
                    device_btn("cap-game-reel", format!("Make reel {n}"), busy).on_press(
                        move |s: &mut AppState| s.capture_make_reel(game_reel_ids.clone(), false),
                    ),
                );
            }

            let plain_filter = s.media.capture_filter == CaptureFilter::All
                && !ui.filter_favorites
                && s.media.capture_game_filter.is_none();
            let resurfaced = if plain_filter {
                on_this_day_items(&s.media.capture_items, current_ymd())
            } else {
                Vec::new()
            };

            let mut days: Vec<(String, Vec<&CaptureItem>)> = Vec::new();
            for item in &filtered {
                let day = item.created.get(..10).unwrap_or("Recent").to_string();
                if let Some(last) = days.last_mut()
                    && last.0 == day
                {
                    last.1.push(*item);
                } else {
                    days.push((day, vec![*item]));
                }
            }

            let mut scroll_col = col()
                .key("cap-grid-scroll")
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .px(20.0)
                .pb(20.0)
                .gap(16.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(chips);

            if let Some(ref note) = ui.sheet_note {
                scroll_col = scroll_col.child(text(note.clone()).font(type_small()).color(bad()));
            }
            if ui.list_failed {
                scroll_col = scroll_col.child(
                    row()
                        .gap(10.0)
                        .center_y()
                        .child(
                            text("Exo could not read the captures folder.")
                                .font(type_small())
                                .color(bad()),
                        )
                        .child(
                            device_btn("cap-retry-list", "Try again", false)
                                .on_press(|s: &mut AppState| s.load_captures()),
                        ),
                );
            }
            if let Some(rb) = reel_banner(s) {
                scroll_col = scroll_col.child(rb);
            }

            if !resurfaced.is_empty() {
                let mut otd_rows = col().w_full().gap(6.0);
                for chunk in resurfaced.chunks(4) {
                    let mut r = row().w_full().gap(6.0);
                    for (it, years) in chunk {
                        let badge = if *years == 1 {
                            "1 year ago".into()
                        } else {
                            format!("{years} years ago")
                        };
                        r = r.child(clip_card(it, s, scale, Some(badge)));
                    }
                    for _ in chunk.len()..4 {
                        r = r.child(el().grow(1.0).min_w(0.0));
                    }
                    otd_rows = otd_rows.child(r);
                }
                scroll_col = scroll_col.child(
                    col()
                        .w_full()
                        .gap(8.0)
                        .child(text("On this day").font(type_section()).color(FG))
                        .child(otd_rows),
                );
            }

            if filtered.is_empty() && any_items {
                scroll_col = scroll_col.child(
                    text("Nothing here with those chips")
                        .font(type_body())
                        .color(fg_muted()),
                );
            } else {
                for (day, list) in days {
                    let mut day_col = col()
                        .w_full()
                        .gap(8.0)
                        .child(text(day).font(type_section()).color(fg_muted()));
                    for chunk in list.chunks(5) {
                        let mut r = row().w_full().gap(6.0);
                        for it in chunk {
                            r = r.child(clip_card(it, s, scale, None));
                        }
                        for _ in chunk.len()..5 {
                            r = r.child(el().grow(1.0).min_w(0.0));
                        }
                        day_col = day_col.child(r);
                    }
                    scroll_col = scroll_col.child(day_col);
                }
            }
            scroll_col
        };

        col().w_full().h_full().child(header).child(body)
    };

    let _ = (
        CaptureListReply::default,
        METHOD_CAPTURE_LIST,
        METHOD_CAPTURE_STATUS,
        HashMap::<String, String>::new,
        format_bytes,
        icon_folder,
        scaled,
    );

    let lb_overlay = if s.media.lightbox.is_open() {
        let slides: Vec<LightboxItem> = s
            .media
            .lightbox_urls
            .iter()
            .filter_map(|u| s.capture_thumb_image(u, scale))
            .map(|img| LightboxItem::new(img.pixels().clone()))
            .collect();
        if slides.is_empty() {
            None
        } else {
            lightbox(
                "captures-lightbox",
                slides,
                |s: &AppState| &s.media.lightbox,
                |s: &mut AppState| &mut s.media.lightbox,
            )
            .backdrop_color(BG.alpha(0.92))
            .surface_color(glass_panel_fill())
            .text_color(FG)
            .border_color(glass_rim())
            .build(s)
        }
    } else {
        None
    };

    el().key("captures-sheet-backdrop")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .pt(76.0)
        .pr(24.0)
        .pb(24.0)
        .pl(24.0)
        .fill(scrim_fill())
        .center()
        .child(
            el().key("captures-sheet")
                .w_full()
                .max_w(1280.0)
                .h_full()
                .max_h(900.0)
                .radius(RADIUS_SHEET)
                .continuous_corners()
                .clip_rounded(RADIUS_SHEET)
                .border(1.0, hairline())
                .liquid_glass(LiquidGlass::panel())
                .child(sheet_inner),
        )
        .children(lb_overlay)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mk_item(id: &str, kind: &str, created: &str, fav: bool, derived: bool) -> CaptureItem {
        CaptureItem {
            id: id.into(),
            kind: kind.into(),
            game: "CS2".into(),
            game_id: Some("steam:730".into()),
            name: id.into(),
            created: created.into(),
            size: 10_000_000,
            duration_ms: (kind == "video").then_some(30_000.0),
            favourite: fav,
            url: String::new(),
            thumb: None,
            derived,
        }
    }

    #[test]
    fn on_this_day_matches_earlier_years_only() {
        let items = vec![
            mk_item("a", "video", "2026-11-23T12:00:00Z", false, false),
            mk_item("b", "video", "2025-11-23T15:00:00Z", false, false),
            mk_item("c", "image", "2024-11-23T08:00:00Z", true, false),
            mk_item("d", "video", "2025-11-22T15:00:00Z", false, false),
        ];
        let hits = on_this_day_items(&items, (2026, 11, 23));
        assert_eq!(hits.len(), 2);
        assert_eq!(hits[0].0.id, "b");
        assert_eq!(hits[0].1, 1);
        assert_eq!(hits[1].0.id, "c");
        assert_eq!(hits[1].1, 2);
    }

    #[test]
    fn reel_of_game_picks_newest_session_and_prefers_two_starred() {
        let items = [
            mk_item("v1", "video", "2026-11-23T20:00:00Z", true, false),
            mk_item("v2", "video", "2026-11-23T19:00:00Z", false, false),
            mk_item("v3", "video", "2026-11-23T18:30:00Z", true, false),
            mk_item("old", "video", "2026-11-23T10:00:00Z", true, false),
            mk_item("drv", "video", "2026-11-23T19:30:00Z", true, true),
        ];
        let refs: Vec<&CaptureItem> = items.iter().collect();
        let ids = reel_of_game(&refs);
        assert_eq!(ids, vec!["v1".to_string(), "v3".to_string()]);
    }

    #[test]
    fn format_size_mb_gb_matches_react() {
        assert_eq!(format_size_mb_gb(2_000_000_000), "1.9 GB");
        assert_eq!(format_size_mb_gb(50 * 1024 * 1024), "50.0 MB");
    }
}
