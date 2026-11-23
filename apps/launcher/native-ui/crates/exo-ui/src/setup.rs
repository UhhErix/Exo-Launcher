use crate::state::{AppState, HostStatus, Room};
use exo_kit::{
    BG, FG, PRIMARY_FG, RADIUS_CARD, RADIUS_PILL, RADIUS_SMALL, SPRING_QUICK, amoled_card,
    exo_mark, fg_faint, fg_muted, glass_control_fill, glass_panel_opt, glass_rim_bottom,
    glass_rim_top, good, hairline, hover_fill, icon_check, icon_gamepad, icon_headphones,
    icon_keyboard, icon_mouse, icon_shield, pressed_fill, scaled, store_logo_or_fallback,
    type_body, type_page, type_small, warn,
};
use rune::{Align, Color, Cursor, El, Side, SurfaceFit, TextStyle, col, el, row, text};
use serde_json::json;
use std::collections::VecDeque;

use exo_rpc::METHOD_SETTINGS_SET;
#[cfg(windows)]
use exo_rpc::{
    METHOD_BACKUP_STATUS, METHOD_CLIENTS_STATUS, METHOD_SYSTEM_TUNING, SystemTuningState,
};

pub const STAGES: usize = 6;
pub const STAGE_TITLES: [&str; STAGES] = [
    "Welcome",
    "Accounts & stores",
    "Devices",
    "PC defaults",
    "Live tour",
    "Done",
];
const TOUR_STAGE: usize = 4;
const DONE_STAGE: usize = 5;
const TRACK_H: f32 = 64.0;
const BEAT_HOLD_SECS: f64 = 0.45;
const COACH_STEPS: [&str; 3] = ["1. Ctrl+K", "2. Open game", "3. Devices"];
const BEATS: [(&str, &str); 3] = [
    (
        "Try instant search right here",
        "The Library is live and unblocked. Press Ctrl+K or click the Search capsule to jump to any game, room, or device setting.",
    ),
    (
        "Open a game",
        "Click any cover to see its page, playtime and Play. Your library is in this room.",
    ),
    (
        "Open a device",
        "Pick a device to open its settings. Exo writes nothing there until you confirm.",
    ),
];
const KNOWN_STORES: [&str; 9] = [
    "steam",
    "epic",
    "gog",
    "riot",
    "xbox",
    "ea",
    "ubisoft",
    "battlenet",
    "roblox",
];

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum StageState {
    #[default]
    Pending,
    Done,
    Skipped,
}

impl StageState {
    fn pref(self) -> &'static str {
        match self {
            StageState::Pending => "pending",
            StageState::Done => "done",
            StageState::Skipped => "skipped",
        }
    }

    fn parse(raw: &str) -> Self {
        match raw {
            "done" => StageState::Done,
            "skipped" => StageState::Skipped,
            _ => StageState::Pending,
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct SetupFlow {
    pub loaded: bool,
    pub open: bool,
    pub stage: usize,
    pub states: [StageState; STAGES],
    pub beat: Option<usize>,
    pub beat_met_at: Option<f64>,
    pub tour_from_setup: bool,
    pub return_to: Option<usize>,
    pub picks: Option<Vec<String>>,
    pub applying: bool,
}

impl SetupFlow {
    pub fn first_run() -> Self {
        Self {
            loaded: true,
            open: true,
            ..Self::default()
        }
    }

    pub fn apply_pref(&mut self, raw: &str) {
        for (i, part) in raw.split(',').take(STAGES).enumerate() {
            self.states[i] = StageState::parse(part.trim());
        }
        self.stage = self.resume_index();
    }

    fn to_pref(&self) -> String {
        self.states
            .iter()
            .map(|st| st.pref())
            .collect::<Vec<_>>()
            .join(",")
    }

    pub fn resume_index(&self) -> usize {
        self.states[..DONE_STAGE]
            .iter()
            .position(|st| *st != StageState::Done)
            .unwrap_or(DONE_STAGE)
    }

    pub fn summary(&self) -> String {
        let done = self.states[..DONE_STAGE]
            .iter()
            .filter(|st| **st == StageState::Done)
            .count();
        let skipped = self.states[..DONE_STAGE]
            .iter()
            .filter(|st| **st == StageState::Skipped)
            .count();
        if done == 0 && skipped == 0 {
            return "Not started".into();
        }
        if skipped == 0 {
            format!("{done} of {DONE_STAGE} steps done")
        } else {
            format!("{done} of {DONE_STAGE} done · {skipped} skipped")
        }
    }
}

impl AppState {
    pub fn setup_resume(&mut self) {
        self.palette_open = false;
        self.setup.open = true;
        self.setup.beat = None;
        self.setup.beat_met_at = None;
        self.setup.return_to = None;
        self.setup.stage = self.setup.resume_index();
        self.setup_enter_stage();
        self.setup_persist(false);
    }

    pub(crate) fn setup_enter_stage(&mut self) {
        match self.setup.stage {
            1 => {
                self.load_pc_devices();
                self.load_settings_room();
                self.load_backup_and_clients();
            }
            0 => {
                self.load_pc_devices();
                self.load_settings_room();
            }
            2 => self.load_pc_devices(),
            3 => {
                self.load_pc_exo_ai();
                self.load_pc_drives();
                self.load_settings_room();
            }
            _ => {}
        }
    }

    fn setup_go_stage(&mut self, stage: usize) {
        self.setup.stage = stage.min(DONE_STAGE);
        self.setup_enter_stage();
        self.setup_persist(false);
    }

    pub fn setup_go(&mut self, stage: usize) {
        self.setup.open = true;
        self.setup_go_stage(stage);
    }

    pub fn setup_go_complete(&mut self) {
        self.setup.states[..TOUR_STAGE + 1].fill(StageState::Done);
        self.setup_go(DONE_STAGE);
    }

    pub fn setup_back(&mut self) {
        self.setup_go_stage(self.setup.stage.saturating_sub(1));
    }

    pub fn setup_next(&mut self) {
        let stage = self.setup.stage;
        if stage == TOUR_STAGE {
            self.setup_start_beats();
            return;
        }
        if stage < DONE_STAGE {
            self.setup.states[stage] = StageState::Done;
        }
        self.setup_go_stage(stage + 1);
    }

    pub fn setup_skip_stage(&mut self) {
        let stage = self.setup.stage;
        if stage == TOUR_STAGE {
            self.setup.states[TOUR_STAGE] = StageState::Skipped;
            self.setup_go_stage(DONE_STAGE);
            return;
        }
        if stage < TOUR_STAGE {
            self.setup.states[stage] = StageState::Skipped;
        }
        self.setup_go_stage(stage + 1);
    }

    pub fn setup_skip_all(&mut self) {
        for st in self.setup.states[..DONE_STAGE].iter_mut() {
            if *st == StageState::Pending {
                *st = StageState::Skipped;
            }
        }
        self.setup.open = false;
        self.setup.beat = None;
        self.room = Room::Library;
        self.setup_persist(true);
    }

    pub fn setup_finish(&mut self, room: Room) {
        self.setup.open = false;
        self.room = room;
        self.setup_persist(true);
    }

    pub fn setup_return(&mut self) {
        self.setup.open = true;
        self.setup.stage = self.setup.return_to.take().unwrap_or(2);
        self.setup_enter_stage();
    }

    pub fn setup_open_settings(&mut self, stage: usize) {
        self.setup.return_to = Some(stage);
        self.setup.open = false;
        self.switch_room(Room::Settings);
    }

    fn load_backup_and_clients(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, exo_rpc::BackupStatus>(METHOD_BACKUP_STATUS, &json!({}), move |res| {
                if let Ok(backup) = res {
                    h1.update(move |s, _| s.pc.backup = Some(backup));
                }
            });
            rpc.call::<_, exo_rpc::ClientsStatus>(METHOD_CLIENTS_STATUS, &json!({}), move |res| {
                if let Ok(clients) = res {
                    handle.update(move |s, _| s.pc.clients = Some(clients));
                }
            });
        }
    }

    pub fn setup_open_device(&mut self, id: String) {
        self.setup.return_to = Some(2);
        self.setup.open = false;
        self.pc.selected_device_id = Some(id);
        self.switch_room(Room::Devices);
        self.load_pc_devices();
    }

    fn setup_start_beats(&mut self) {
        self.setup.tour_from_setup = true;
        self.begin_tour();
    }

    pub fn start_tour(&mut self) {
        self.setup.tour_from_setup = false;
        self.begin_tour();
    }

    fn begin_tour(&mut self) {
        self.palette_open = false;
        self.key_modal_open = false;
        self.open_game_id = None;
        self.store_game = None;
        self.setup.open = false;
        self.setup.return_to = None;
        self.setup.beat_met_at = None;
        self.setup.beat = Some(0);
        self.switch_room(Room::Library);
    }

    pub fn setup_beat_skip(&mut self) {
        self.advance_beat();
    }

    fn advance_beat(&mut self) {
        let Some(beat) = self.setup.beat else {
            return;
        };
        self.setup.beat_met_at = None;
        if beat + 1 >= BEATS.len() {
            self.finish_tour();
            return;
        }
        self.setup.beat = Some(beat + 1);
        if beat + 1 == 2 {
            self.pc.selected_device_id = None;
            self.switch_room(Room::Devices);
            self.load_pc_devices();
        }
    }

    pub fn finish_tour(&mut self) {
        let completed_all = self.setup.beat.is_some_and(|b| b + 1 >= BEATS.len());
        self.setup.beat = None;
        self.setup.beat_met_at = None;
        if std::mem::take(&mut self.setup.tour_from_setup) {
            self.setup.states[TOUR_STAGE] = if completed_all {
                StageState::Done
            } else {
                StageState::Skipped
            };
            self.setup.open = true;
            self.setup.stage = DONE_STAGE;
        }
        self.setup_persist(true);
    }

    pub fn setup_tick(&mut self, now: f64) -> bool {
        let mut changed = false;
        if self.setup.stage == 3
            && self.setup.picks.is_none()
            && let Some(tuning) = self.pc.tuning.as_ref()
        {
            self.setup.picks = Some(
                tuning
                    .items
                    .iter()
                    .filter(|item| !item.opt_in)
                    .map(|item| item.key.clone())
                    .collect(),
            );
            changed = true;
        }
        if let Some(beat) = self.setup.beat {
            let met = match beat {
                0 => self.palette_open,
                1 => self.open_game_id.is_some(),
                _ => self.room == Room::Devices && self.pc.selected_device_id.is_some(),
            };
            match (met, self.setup.beat_met_at) {
                (true, None) => {
                    self.setup.beat_met_at = Some(now);
                    changed = true;
                }
                (true, Some(at)) if now - at >= BEAT_HOLD_SECS => {
                    self.advance_beat();
                    changed = true;
                }
                (false, Some(_)) => self.setup.beat_met_at = None,
                _ => {}
            }
        }
        changed
    }

    pub fn toggle_setup_pick(&mut self, key: &str) {
        let picks = self.setup.picks.get_or_insert_with(Vec::new);
        if let Some(pos) = picks.iter().position(|k| k == key) {
            picks.remove(pos);
        } else {
            picks.push(key.to_string());
        }
    }

    pub fn apply_setup_picks(&mut self) {
        if self.setup.applying {
            return;
        }
        let Some(picks) = self.setup.picks.clone() else {
            return;
        };
        let Some(tuning) = self.pc.tuning.as_ref() else {
            return;
        };
        let queue: VecDeque<(String, bool)> = tuning
            .items
            .iter()
            .filter(|item| picks.contains(&item.key) != item.on)
            .map(|item| (item.key.clone(), picks.contains(&item.key)))
            .collect();
        if queue.is_empty() {
            self.notice = Some("Nothing to change: these match what's already on.".into());
            return;
        }
        self.setup.applying = true;
        self.run_setup_tuning(queue);
    }

    fn run_setup_tuning(&mut self, mut queue: VecDeque<(String, bool)>) {
        let Some((key, on)) = queue.pop_front() else {
            self.setup.applying = false;
            self.notice = Some("Windows settings applied from setup.".into());
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let action = if on { "on" } else { "off" };
            rpc.call::<_, SystemTuningState>(
                METHOD_SYSTEM_TUNING,
                &json!({ "action": action, "item": key }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(tuning) => {
                            s.pc.tuning = Some(tuning);
                            s.run_setup_tuning(queue);
                        }
                        Err(err) => {
                            s.setup.applying = false;
                            s.notice = Some(format!("Setup tuning stopped: {err}"));
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        {
            let _ = (key, on, queue);
            self.setup.applying = false;
        }
    }

    pub fn setup_persist(&self, tour_seen: bool) {
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            let mut prefs = json!({ "setupProgress": self.setup.to_pref() });
            if tour_seen {
                prefs["tourSeen"] = json!(1);
            }
            rpc.call_raw(METHOD_SETTINGS_SET, &json!({ "uiPrefs": prefs }), |_| {});
        }
        #[cfg(not(windows))]
        let _ = tour_seen;
    }
}

fn gpu_label(s: &AppState) -> String {
    s.pc.system_about
        .as_ref()
        .and_then(|a| a.gpu.clone())
        .or_else(|| s.pc.gpu_short_name.clone())
        .unwrap_or_else(|| "This PC".into())
}

fn store_count(s: &AppState, id: &str) -> usize {
    s.games
        .iter()
        .filter(|g| {
            g.store.eq_ignore_ascii_case(id)
                || g.stores.iter().any(|st| st.eq_ignore_ascii_case(id))
        })
        .count()
}

fn store_ids(s: &AppState) -> Vec<String> {
    let mut ids: Vec<String> = Vec::new();
    for game in &s.games {
        let id = if game.store.is_empty() {
            game.stores.first().cloned().unwrap_or_default()
        } else {
            game.store.clone()
        };
        if !id.is_empty() && !ids.iter().any(|x| x.eq_ignore_ascii_case(&id)) {
            ids.push(id.to_ascii_lowercase());
        }
    }
    ids
}

fn installed_count(s: &AppState) -> usize {
    s.games.iter().filter(|g| g.installed).count()
}

fn device_count(s: &AppState) -> usize {
    s.pc.devices.as_ref().map_or(0, |d| d.devices.len())
}

fn stage_sub(s: &AppState, stage: usize) -> String {
    match stage {
        0 => "Instant local scan".into(),
        1 => format!("{} stores · {} games", store_ids(s).len(), s.games.len()),
        2 => match device_count(s) {
            0 => "Looking for devices".into(),
            n => format!("{n} devices"),
        },
        3 => gpu_label(s),
        4 => "3 hands-on actions".into(),
        _ => "Summary".into(),
    }
}

fn track_cell(s: &AppState, i: usize) -> El {
    let state = s.setup.states[i];
    let current = i == s.setup.stage;
    let pill = if current {
        Some(("Current", FG, PRIMARY_FG))
    } else {
        match state {
            StageState::Done => Some(("Done", good().alpha(0.16), good())),
            StageState::Skipped => Some(("Skipped", fg_faint().alpha(0.2), fg_muted())),
            StageState::Pending => None,
        }
    };
    let (dot_fill, dot_text) = match state {
        StageState::Done => (good(), BG),
        _ if current => (FG, PRIMARY_FG),
        _ => (fg_faint().alpha(0.18), fg_muted()),
    };
    let dot = el()
        .size(22.0, 22.0)
        .radius(11.0)
        .fill(dot_fill)
        .center()
        .child(if state == StageState::Done {
            icon_check(12.0, dot_text)
        } else {
            text(format!("{}", i + 1))
                .font(type_small())
                .color(dot_text)
        });
    let title_color = if current || state == StageState::Done {
        FG
    } else {
        fg_muted()
    };
    let mut labels = col().w_full().grow(1.0).min_w(0.0).gap(2.0).child(
        text(STAGE_TITLES[i])
            .font(type_body())
            .color(title_color)
            .truncate(),
    );
    if let Some((label, fill, color)) = pill {
        labels = labels.child(
            row().child(
                row()
                    .h(18.0)
                    .px(7.0)
                    .radius(RADIUS_PILL)
                    .fill(fill)
                    .center_y()
                    .child(text(label).font(type_small()).color(color)),
            ),
        );
    }
    labels = labels.child(
        text(stage_sub(s, i))
            .font(type_small())
            .color(fg_muted())
            .truncate(),
    );
    let content = row()
        .w_full()
        .grow(1.0)
        .gap(10.0)
        .px(14.0)
        .center_y()
        .child(dot)
        .child(labels);
    let underline_fill = if current {
        FG
    } else if state == StageState::Done {
        good()
    } else {
        hairline()
    };
    col()
        .key(format!("setup-stage-{i}"))
        .role("button")
        .label(STAGE_TITLES[i])
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .grow(1.0)
        .min_w(0.0)
        .h(TRACK_H)
        .hover(hover_fill())
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| s.setup_go(i))
        .child(content)
        .child(el().w_full().h(2.0).fill(underline_fill))
}

fn track(s: &AppState) -> El {
    let mut bar = row()
        .w_full()
        .h(TRACK_H)
        .pl(24.0)
        .pr(150.0)
        .gap(6.0)
        .center_y()
        .fill(BG)
        .child(
            row()
                .w(150.0)
                .gap(8.0)
                .center_y()
                .child(exo_mark(18.0, FG))
                .child(text("Exo").font(type_body()).color(FG))
                .child(text("Setup").font(type_body()).color(fg_muted())),
        );
    for i in 0..STAGES {
        bar = bar.child(track_cell(s, i));
    }
    bar = bar.child(
        button(
            "setup-track-skip",
            "Skip setup",
            false,
            |s: &mut AppState| s.setup_skip_all(),
        )
        .ml(12.0),
    );
    col()
        .w_full()
        .child(bar)
        .child(el().w_full().h(1.0).fill(hairline()))
}

fn button(
    key: &'static str,
    label: &str,
    primary: bool,
    on_press: impl Fn(&mut AppState) + 'static,
) -> El {
    let (fill, color) = if primary {
        (FG, PRIMARY_FG)
    } else {
        (glass_control_fill(), FG)
    };
    row()
        .key(key)
        .role("button")
        .label(label.to_string())
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .h(36.0)
        .px(16.0)
        .gap(6.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(fill)
        .hover(hover_fill())
        .press(pressed_fill())
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .motion(SPRING_QUICK)
        .on_press(on_press)
        .child(text(label.to_string()).font(type_body()).color(color))
}

fn pill(label: impl Into<String>, tint: Color, color: Color) -> El {
    row().child(
        row()
            .h(22.0)
            .px(9.0)
            .radius(RADIUS_PILL)
            .fill(tint)
            .center_y()
            .child(text(label.into()).font(type_small()).color(color)),
    )
}

fn device_glyph(kind: &str, size: f32) -> El {
    match kind {
        "keyboard" => icon_keyboard(size, fg_muted()),
        "controller" => icon_gamepad(size, fg_muted()),
        "audio" => icon_headphones(size, fg_muted()),
        _ => icon_mouse(size, fg_muted()),
    }
}

fn device_maker_model(dev: &exo_rpc::PeripheralCard) -> String {
    [dev.maker.as_deref(), dev.model.as_deref()]
        .into_iter()
        .flatten()
        .filter(|v| !v.is_empty())
        .collect::<Vec<_>>()
        .join(" · ")
}

fn bay_facts(dev: &exo_rpc::PeripheralCard) -> Vec<(&'static str, String)> {
    let mut facts = Vec::new();
    if let Some(c) = dev.connection.clone().filter(|c| !c.is_empty()) {
        facts.push(("Connection", c));
    }
    if let Some(b) = dev.battery {
        facts.push((
            "Battery",
            format!("{}%", (b * if b <= 1.0 { 100.0 } else { 1.0 }).round()),
        ));
    }
    if let Some(hz) = dev.polling_hz {
        facts.push(("Polling rate", format!("{hz} Hz")));
    }
    if let Some(l) = dev.layout.clone().filter(|v| !v.is_empty()) {
        facts.push(("Layout", l));
    }
    if let Some(c) = dev.color.clone().filter(|v| !v.is_empty()) {
        facts.push(("Color", c));
    }
    facts
}

fn hero_style() -> TextStyle {
    TextStyle::new(scaled(64.0), 650.0).letter_spacing(-0.03)
}

fn stat_cell(value: String, caption: String) -> El {
    col()
        .grow(1.0)
        .min_w(0.0)
        .px(18.0)
        .py(14.0)
        .gap(4.0)
        .child(
            text(value)
                .font(TextStyle::new(scaled(40.0), 650.0).letter_spacing(-0.02))
                .color(FG)
                .wrap(),
        )
        .child(text(caption).font(type_small()).color(fg_muted()).wrap())
}

fn cover_pool(s: &AppState, scale: f32, installed_only: bool) -> Vec<rune::Image> {
    let mut games: Vec<_> = s
        .games
        .iter()
        .filter(|g| g.cover_url.is_some() && (!installed_only || g.installed))
        .collect();
    games.sort_by_key(|g| !g.installed);
    games
        .into_iter()
        .filter_map(|g| s.cover_image(g, scale))
        .take(24)
        .collect()
}

fn mosaic(s: &AppState, scale: f32) -> El {
    let covers = cover_pool(s, scale, false);
    let mut columns = row()
        .pin(Some(-160.0), Some(-160.0), Some(-160.0), Some(-160.0))
        .gap(12.0)
        .rotate(-0.14);
    for col_idx in 0..8usize {
        let mut column = col().grow(1.0).min_w(0.0).gap(12.0);
        for row_idx in 0..3usize {
            let idx = col_idx * 3 + row_idx;
            let mut cover = el()
                .w_full()
                .h(380.0)
                .radius(RADIUS_SMALL)
                .fill(fg_faint().alpha(0.12));
            if !covers.is_empty() {
                cover = cover.pixels_fit(covers[idx % covers.len()].pixels(), SurfaceFit::Fill);
            }
            column = column.child(cover);
        }
        columns = columns.child(column);
    }
    columns
}

fn welcome(s: &AppState, scale: f32) -> El {
    let installed = installed_count(s);
    let stores = store_ids(s);
    let devices =
        s.pc.devices
            .as_ref()
            .map(|d| d.devices.clone())
            .unwrap_or_default();
    let device_names = devices
        .iter()
        .take(4)
        .map(|d| d.name.clone())
        .collect::<Vec<_>>()
        .join(" · ");
    let gpu = gpu_label(s);
    let cpu =
        s.pc.system_about
            .as_ref()
            .and_then(|a| a.cpu.clone().or_else(|| a.processor.clone()))
            .unwrap_or_default();

    let shade = el()
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG.alpha(0.62));

    let stats = col()
        .w(560.0)
        .child(
            row()
                .w_full()
                .child(stat_cell(
                    format!("{installed} installed"),
                    format!(
                        "{} games indexed across {} stores",
                        s.games.len(),
                        stores.len()
                    ),
                ))
                .child(stat_cell(
                    format!("{} devices", devices.len()),
                    if device_names.is_empty() {
                        "Checking USB and audio".into()
                    } else {
                        device_names.clone()
                    },
                )),
        )
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(row().w_full().child(stat_cell(gpu, cpu)).child(stat_cell(
            "~90 seconds".into(),
            "Guided setup · 5 seconds if skipped".into(),
        )))
        .child(el().w_full().h(1.0).fill(hairline()));

    let copy = col()
        .pin(Some(56.0), None, None, Some(56.0))
        .w(640.0)
        .gap(18.0)
        .child(
            row()
                .gap(10.0)
                .center_y()
                .child(exo_mark(26.0, FG))
                .child(pill("Local scan complete", good().alpha(0.16), good())),
        )
        .child(text("Welcome to Exo").font(hero_style()).color(FG))
        .child(
            text("Your installed games, store accounts, connected gear and PC defaults, ready in one continuous flow.")
                .font(type_body())
                .color(fg_muted())
                .wrap(),
        )
        .child(stats)
        .child(
            row()
                .gap(10.0)
                .pt(6.0)
                .child(button("setup-start", "Start setup", true, |s: &mut AppState| {
                    s.setup_next()
                }))
                .child(button("setup-welcome-skip", "Skip setup", false, |s: &mut AppState| {
                    s.setup_skip_all()
                })),
        );

    let mut found_chips = row().gap(8.0).wrap();
    for id in stores.iter().take(6) {
        found_chips = found_chips.child(
            row()
                .h(30.0)
                .px(10.0)
                .gap(6.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(glass_control_fill())
                .child(store_logo_or_fallback(id, 14.0))
                .child(
                    text(format!(
                        "{} · {}",
                        exo_rpc::types::store_label(id),
                        store_count(s, id)
                    ))
                    .font(type_small())
                    .color(FG),
                ),
        );
    }
    let mut device_row = row().w_full().gap(10.0);
    for dev in devices.iter().take(4) {
        let photo =
            s.pc.device_images
                .get(&dev.id)
                .and_then(|u| s.device_image(u, scale));
        let media = match photo {
            Some(img) => el()
                .w_full()
                .h(64.0)
                .pixels_fit(img.pixels(), SurfaceFit::Fit),
            None => el()
                .w_full()
                .h(64.0)
                .center()
                .child(device_glyph(&dev.kind, 28.0)),
        };
        device_row = device_row.child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .padding(10.0)
                .gap(6.0)
                .radius(RADIUS_SMALL)
                .fill(glass_control_fill())
                .child(media)
                .child(text(dev.name.clone()).font(type_small()).color(FG).wrap()),
        );
    }
    let found = glass_panel_opt("setup-found", 470.0, true)
        .pin(None, None, Some(24.0), Some(24.0))
        .padding(20.0)
        .gap(14.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Found on this PC").font(type_body()).color(FG))
                .child(pill("Read-only", fg_faint().alpha(0.2), fg_muted())),
        )
        .child(found_chips)
        .child(device_row)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(
            row()
                .w_full()
                .gap(8.0)
                .center_y()
                .child(icon_shield(14.0, good()))
                .child(
                    text("Nothing downloads, installs, or changes on your PC or hardware until you confirm.")
                        .font(type_small())
                        .color(fg_muted())
                        .wrap()
                        .grow(1.0)
                        .min_w(0.0),
                ),
        );

    el().w_full()
        .h_full()
        .child(mosaic(s, scale))
        .child(shade)
        .child(copy)
        .child(found)
}

fn store_row(s: &AppState, id: &str) -> El {
    let count = store_count(s, id);
    let state = if count > 0 {
        pill(format!("Found · {count} games"), good().alpha(0.16), good())
    } else {
        pill("Not found on this PC", fg_faint().alpha(0.2), fg_muted())
    };
    row()
        .w_full()
        .h(56.0)
        .px(4.0)
        .gap(12.0)
        .center_y()
        .child(store_logo_or_fallback(id, 22.0))
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .child(
                    text(exo_rpc::types::store_label(id))
                        .font(type_body())
                        .color(FG),
                )
                .child(
                    text(if count > 0 {
                        if count == 1 {
                            "1 game on this PC".into()
                        } else {
                            format!("{count} games on this PC")
                        }
                    } else {
                        "Install or sign in there, then run setup again".into()
                    })
                    .font(type_small())
                    .color(fg_muted())
                    .wrap(),
                ),
        )
        .child(state)
}

fn accounts(s: &AppState) -> El {
    let found = store_ids(s);
    let mut list = col().w_full();
    let mut seen: Vec<String> = Vec::new();
    for id in found.iter().map(String::as_str).chain(KNOWN_STORES) {
        if seen.iter().any(|x| x == id) {
            continue;
        }
        seen.push(id.to_string());
        list = list
            .child(store_row(s, id))
            .child(el().w_full().h(1.0).fill(hairline()));
    }

    let account = match s.pc.account.signed_in {
        true => {
            s.pc.account
                .email
                .clone()
                .unwrap_or_else(|| "Signed in".into())
        }
        false => {
            "Not signed in. Optional: sync settings across PCs, staying local until you sign in."
                .into()
        }
    };
    let account_action = if s.pc.account.signed_in {
        button(
            "setup-account-sync",
            "Sync now",
            false,
            |s: &mut AppState| s.sync_exo_account(),
        )
    } else {
        button(
            "setup-account-open",
            "Sign in",
            false,
            |s: &mut AppState| s.setup_open_settings(1),
        )
    };
    let backup_line = match s.pc.backup.as_ref() {
        Some(b) if b.offer.is_some() => {
            let o = b.offer.as_ref();
            format!(
                "Backup from {} found · {} files",
                o.map_or("another PC", |o| o.device.as_str()),
                o.map_or(0, |o| o.files)
            )
        }
        Some(b) => match b.last.as_ref() {
            Some(last) => format!("{} snapshot · folders, covers & notes", last.day),
            None => "No backup on this PC yet".into(),
        },
        None => "Checking backups".into(),
    };
    let (clients_pill, clients_tint, clients_ink) = match s.pc.clients.as_ref() {
        None => ("Checking", fg_faint().alpha(0.2), fg_muted()),
        Some(c) if c.needs_fix => ("Needs fix", warn().alpha(0.16), warn()),
        Some(c) if c.problem.is_some() => ("Unknown", fg_faint().alpha(0.2), fg_muted()),
        Some(_) => ("Healthy", good().alpha(0.16), good()),
    };
    let backup_action = button(
        "setup-restore-backup",
        "Restore",
        false,
        |s: &mut AppState| s.setup_open_settings(1),
    );

    let side = col()
        .w(380.0)
        .gap(14.0)
        .child(
            amoled_card(RADIUS_CARD)
                .w_full()
                .padding(18.0)
                .gap(10.0)
                .child(text("Exo account").font(type_body()).color(FG))
                .child(text(account).font(type_small()).color(fg_muted()).wrap())
                .child(account_action),
        )
        .child(
            amoled_card(RADIUS_CARD)
                .w_full()
                .padding(18.0)
                .gap(12.0)
                .child(text("Backups & clients").font(type_body()).color(FG))
                .child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .gap(10.0)
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(text("Restore local backup").font(type_small()).color(FG))
                                .child(
                                    text(backup_line)
                                        .font(type_small())
                                        .color(fg_muted())
                                        .wrap(),
                                ),
                        )
                        .child(backup_action),
                )
                .child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .gap(10.0)
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(text("Game clients").font(type_small()).color(FG))
                                .child(
                                    text("Steam, Riot & Epic stay untouched as backends")
                                        .font(type_small())
                                        .color(fg_muted())
                                        .wrap(),
                                ),
                        )
                        .child(pill(clients_pill, clients_tint, clients_ink)),
                ),
        )
        .child(
            amoled_card(RADIUS_CARD)
                .w_full()
                .padding(18.0)
                .gap(12.0)
                .child(text("Music & chat").font(type_body()).color(FG))
                .child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .child(
                            text("Spotify, Apple Music, YouTube Music")
                                .font(type_small())
                                .color(fg_muted())
                                .wrap(),
                        )
                        .child(button(
                            "setup-open-music",
                            "Open Music",
                            false,
                            |s: &mut AppState| s.switch_room(Room::Music),
                        )),
                )
                .child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .child(
                            text("Discord, Exo's own client")
                                .font(type_small())
                                .color(fg_muted())
                                .wrap(),
                        )
                        .child(button(
                            "setup-open-discord",
                            "Open Discord",
                            false,
                            |s: &mut AppState| s.switch_room(Room::Discord),
                        )),
                ),
        );

    row()
        .w_full()
        .h_full()
        .px(32.0)
        .pt(28.0)
        .gap(32.0)
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(12.0)
                .child(text("Accounts & stores").font(type_page()).color(FG))
                .child(
                    text("Exo reads each store's library from this PC. Sign-ins happen only when you press their button.")
                        .font(type_body())
                        .color(fg_muted())
                        .wrap(),
                )
                .child(list),
        )
        .child(side)
}

fn bay(s: &AppState, i: usize, dev: &exo_rpc::PeripheralCard, fact_rows: usize, scale: f32) -> El {
    let photo =
        s.pc.device_images
            .get(&dev.id)
            .and_then(|u| s.device_image(u, scale));
    let media = match photo {
        Some(img) => el()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .pixels_fit(img.pixels(), SurfaceFit::Fit),
        None => el()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .center()
            .child(device_glyph(&dev.kind, 140.0)),
    };
    let mut facts = col()
        .w_full()
        .gap(8.0)
        .min_h(fact_rows as f32 * 22.0 + fact_rows.saturating_sub(1) as f32 * 8.0);
    for (label, value) in bay_facts(dev) {
        facts = facts.child(
            row()
                .w_full()
                .justify_between()
                .gap(12.0)
                .center_y()
                .child(text(label).font(type_small()).color(fg_muted()))
                .child(text(value).font(type_small()).color(FG)),
        );
    }
    let maker_model = device_maker_model(dev);
    let mut head = row().w_full().justify_between().center_y().child(
        text(format!("{:02} · {}", i + 1, dev.kind))
            .font(type_small())
            .color(fg_faint()),
    );
    if let Some(conn) = dev.connection.clone().filter(|c| !c.is_empty()) {
        head = head.child(pill(conn, fg_faint().alpha(0.2), fg_muted()));
    }
    let id = dev.id.clone();
    col()
        .key(format!("setup-bay-{i}"))
        .role("button")
        .label(format!("Open {}", dev.name))
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .grow(1.0)
        .min_w(0.0)
        .padding(18.0)
        .gap(10.0)
        .radius(RADIUS_CARD)
        .fill(BG)
        .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
        .hover(hover_fill())
        .press(pressed_fill())
        .motion(SPRING_QUICK)
        .on_press(move |s: &mut AppState| s.setup_open_device(id.clone()))
        .child(head)
        .child(media)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(text(dev.name.clone()).font(type_body()).color(FG).wrap())
        .child(
            text(maker_model)
                .font(type_small())
                .color(fg_muted())
                .wrap()
                .min_h(18.0),
        )
        .child(facts)
}

fn devices(s: &AppState, scale: f32) -> El {
    let devices =
        s.pc.devices
            .as_ref()
            .map(|d| d.devices.clone())
            .unwrap_or_default();
    let body = if devices.is_empty() {
        el().w_full().grow(1.0).center().child(
            text(if s.pc.devices_loading {
                "Looking for devices…"
            } else {
                "No devices found on this PC yet. Connect one, then press Continue."
            })
            .font(type_body())
            .color(fg_muted()),
        )
    } else {
        let mut bays = row().w_full().grow(1.0).gap(16.0);
        let fact_rows = devices
            .iter()
            .take(5)
            .map(|d| bay_facts(d).len())
            .max()
            .unwrap_or(0);
        for (i, dev) in devices.iter().take(5).enumerate() {
            bays = bays.child(bay(s, i, dev, fact_rows, scale));
        }
        bays
    };
    col()
        .w_full()
        .h_full()
        .px(32.0)
        .pt(28.0)
        .pb(24.0)
        .gap(18.0)
        .child(text("Devices").font(type_page()).color(FG))
        .child(
            text("Exo checked these on this PC. Each one opens its own settings, where nothing is written until you confirm.")
                .font(type_body())
                .color(fg_muted())
                .wrap(),
        )
        .child(body)
}

fn fact(label: &str, value: String) -> El {
    col()
        .grow(1.0)
        .min_w(0.0)
        .gap(3.0)
        .child(text(label.to_string()).font(type_small()).color(fg_muted()))
        .child(text(value).font(type_body()).color(FG).wrap())
}

fn spec_card(key: &'static str, title: &str, name: String, tag: String, facts: Vec<El>) -> El {
    let mut facts_row = row().w_full().gap(16.0);
    for f in facts {
        facts_row = facts_row.child(f);
    }
    amoled_card(RADIUS_CARD)
        .key(key)
        .grow(1.0)
        .min_w(0.0)
        .padding(18.0)
        .gap(12.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text(title.to_string()).font(type_body()).color(FG))
                .child(pill(tag, fg_faint().alpha(0.2), fg_muted())),
        )
        .child(
            text(name)
                .font(TextStyle::new(scaled(20.0), 650.0))
                .color(FG)
                .wrap(),
        )
        .child(facts_row)
}

fn pc_cards(s: &AppState) -> El {
    let about = s.pc.system_about.clone().unwrap_or_default();
    let display = about.display.clone().or_else(|| s.pc.screen_label.clone());
    let gpu_name = about
        .gpu
        .clone()
        .or_else(|| s.pc.gpu_short_name.clone())
        .unwrap_or_else(|| "Checking graphics".into());
    let cpu_name = about
        .cpu
        .clone()
        .or_else(|| about.processor.clone())
        .unwrap_or_else(|| "Checking processor".into());
    let gpu = spec_card(
        "setup-card-gpu",
        "Graphics",
        gpu_name,
        display.clone().unwrap_or_default(),
        vec![
            fact(
                "Memory",
                about
                    .memory_gb
                    .map(|g| format!("{g:.0} GB"))
                    .unwrap_or_else(|| "Checking".into()),
            ),
            fact(
                "Display",
                display.clone().unwrap_or_else(|| "Checking".into()),
            ),
            fact(
                "Windows",
                about
                    .windows_version
                    .clone()
                    .unwrap_or_else(|| "Checking".into()),
            ),
        ],
    );
    let cpu = spec_card(
        "setup-card-cpu",
        "Processor",
        cpu_name,
        "CPU".into(),
        vec![
            fact(
                "RAM",
                match (about.memory_gb.or(about.ram_gb), about.memory_type.clone()) {
                    (Some(g), Some(t)) => format!("{g:.0} GB {t}"),
                    (Some(g), None) => format!("{g:.0} GB"),
                    _ => "Checking".into(),
                },
            ),
            fact(
                "Motherboard",
                about
                    .motherboard
                    .clone()
                    .unwrap_or_else(|| "Checking".into()),
            ),
            fact(
                "Windows",
                about.windows.clone().unwrap_or_else(|| "Checking".into()),
            ),
        ],
    );
    let drive = s.pc.drives.as_ref().and_then(|d| d.drives.first().cloned());
    let (disk_name, disk_tag) = match &drive {
        Some(d) => (d.model.clone(), d.kind.clone()),
        None => ("Checking drives".into(), String::new()),
    };
    let volumes = drive
        .as_ref()
        .map(|d| d.volumes.clone())
        .unwrap_or_default();
    let total: f64 = volumes.iter().map(|v| v.total_gb).sum();
    let free: f64 = volumes.iter().map(|v| v.free_gb).sum();
    let used_frac = if total > 0.0 {
        ((total - free) / total).clamp(0.0, 1.0) as f32
    } else {
        0.0
    };
    let disk_bar = el()
        .w_full()
        .h(6.0)
        .radius(3.0)
        .fill(fg_faint().alpha(0.18))
        .child(el().w(0.0).h(6.0).radius(3.0).fill(FG).grow(used_frac));
    let disk = amoled_card(RADIUS_CARD)
        .key("setup-card-disk")
        .grow(1.0)
        .min_w(0.0)
        .padding(18.0)
        .gap(12.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Storage").font(type_body()).color(FG))
                .child(pill(disk_tag, fg_faint().alpha(0.2), fg_muted())),
        )
        .child(
            text(disk_name)
                .font(TextStyle::new(scaled(20.0), 650.0))
                .color(FG)
                .wrap(),
        )
        .child(disk_bar)
        .child(
            text(format!("{free:.0} GB free of {total:.0} GB"))
                .font(type_small())
                .color(fg_muted()),
        );
    let net_name = about
        .network
        .clone()
        .unwrap_or_else(|| "Checking network".into());
    let net = spec_card(
        "setup-card-net",
        "Network & Windows",
        net_name,
        about
            .network_mbps
            .map(|m| format!("{m:.0} Mbps"))
            .unwrap_or_default(),
        vec![
            fact(
                "Windows",
                about.windows.clone().unwrap_or_else(|| "Checking".into()),
            ),
            fact(
                "Version",
                about
                    .version
                    .clone()
                    .or_else(|| s.pc.update_check.as_ref().and_then(|u| u.current.clone()))
                    .unwrap_or_else(|| "Checking".into()),
            ),
        ],
    );

    col()
        .min_w(0.0)
        .gap(14.0)
        .child(row().w_full().gap(14.0).child(gpu).child(cpu))
        .child(row().w_full().gap(14.0).child(disk).child(net))
}

fn pc_defaults(s: &AppState) -> El {
    let tuning_items =
        s.pc.tuning
            .as_ref()
            .map(|t| t.items.clone())
            .unwrap_or_default();
    let picks = s.setup.picks.clone().unwrap_or_default();
    let mut list = col().w_full().gap(0.0);
    if tuning_items.is_empty() {
        list = list.child(
            text("Checking Windows settings…")
                .font(type_small())
                .color(fg_muted()),
        );
    }
    for item in tuning_items.iter() {
        let on = picks.contains(&item.key);
        let key = item.key.clone();
        list = list.child(
            row()
                .w_full()
                .py(12.0)
                .gap(12.0)
                .center_y()
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(2.0)
                        .child(text(item.label.clone()).font(type_body()).color(FG).wrap())
                        .child(
                            text(item.detail.clone())
                                .font(type_small())
                                .color(fg_muted())
                                .wrap(),
                        ),
                )
                .child(setup_switch(
                    format!("setup-pick-{}", item.key),
                    on,
                    move |s: &mut AppState| s.toggle_setup_pick(&key),
                )),
        );
        list = list.child(el().w_full().h(1.0).fill(hairline()));
    }
    let count = picks.len();
    let total = tuning_items.len();
    let panel = glass_panel_opt("setup-picks", 560.0, true)
        .padding(22.0)
        .gap(12.0)
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(text("Selected optimizations").font(type_body()).color(FG))
                .child(pill(
                    format!("{count} of {total}"),
                    fg_faint().alpha(0.2),
                    FG,
                )),
        )
        .child(
            text("Toggle each item. Nothing runs until you click Apply.")
                .font(type_small())
                .color(fg_muted())
                .wrap(),
        )
        .child(list)
        .child(
            row()
                .w_full()
                .pt(6.0)
                .justify_between()
                .center_y()
                .child(button(
                    "setup-picks-later",
                    "Skip for now",
                    false,
                    |s: &mut AppState| s.setup_skip_stage(),
                ))
                .child(button(
                    "setup-picks-apply",
                    &format!("Apply selected ({count})"),
                    true,
                    |s: &mut AppState| s.apply_setup_picks(),
                )),
        );
    row()
        .w_full()
        .h_full()
        .px(32.0)
        .pt(28.0)
        .gap(24.0)
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(16.0)
                .child(
                    text("Best defaults for this PC")
                        .font(type_page())
                        .color(FG),
                )
                .child(
                    text("Live readings from this PC. Every change is reversible in the PC room.")
                        .font(type_body())
                        .color(fg_muted())
                        .wrap(),
                )
                .child(pc_cards(s)),
        )
        .child(panel)
}

fn setup_switch(key: String, on: bool, on_press: impl Fn(&mut AppState) + 'static) -> El {
    let knob = el()
        .size(20.0, 20.0)
        .radius(10.0)
        .fill(FG)
        .absolute(if on { 18.0 } else { 2.0 }, 2.0)
        .motion(exo_kit::SPRING_MOVE);
    el().key(key)
        .role("switch")
        .label("Toggle")
        .checked(on)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .size(40.0, 24.0)
        .radius(RADIUS_PILL)
        .fill(if on { good() } else { fg_faint().alpha(0.3) })
        .motion(SPRING_QUICK)
        .on_press(on_press)
        .child(knob)
}

fn tour_stage(s: &AppState, scale: f32) -> El {
    let games: Vec<_> = s.games.iter().filter(|g| g.installed).take(16).collect();
    let covers = cover_pool(s, scale, false);
    let hero_name = games
        .first()
        .map_or("Your library".to_string(), |g| g.title.clone());
    let mut grid = col().w_full().grow(1.0).min_h(0.0).gap(12.0);
    for line_idx in 0..2usize {
        let mut line = row().w_full().grow(1.0).min_h(0.0).gap(12.0);
        for col_idx in 0..8usize {
            let mut cover = el()
                .grow(1.0)
                .min_w(0.0)
                .h_full()
                .radius(RADIUS_SMALL)
                .fill(fg_faint().alpha(0.12));
            if !covers.is_empty() {
                let img = &covers[(line_idx * 8 + col_idx) % covers.len()];
                cover = cover.pixels_fit(img.pixels(), SurfaceFit::Fill);
            }
            line = line.child(cover);
        }
        grid = grid.child(line);
    }
    let room = col()
        .w_full()
        .h_full()
        .child(
            col()
                .w_full()
                .px(32.0)
                .pt(28.0)
                .gap(12.0)
                .child(
                    row().child(
                        row()
                            .h(24.0)
                            .px(10.0)
                            .radius(RADIUS_PILL)
                            .fill(FG)
                            .center_y()
                            .child(text("Last played").font(type_small()).color(BG)),
                    ),
                )
                .child(text(hero_name).font(hero_style()).color(FG))
                .child(
                    text(format!(
                        "{} installed · {} stores",
                        installed_count(s),
                        stores_count(s)
                    ))
                    .font(type_small())
                    .color(fg_muted()),
                ),
        )
        .child(
            col()
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .px(32.0)
                .pt(20.0)
                .pb(20.0)
                .child(grid),
        );
    let (title, body) = BEATS[0];
    let card = glass_panel_opt("setup-tour-coach", 480.0, true)
        .pin(None, Some(20.0), Some(24.0), None)
        .padding(20.0)
        .gap(14.0)
        .child(coach_steps(0))
        .child(text(title).font(type_page()).color(FG).wrap())
        .child(text(body).font(type_body()).color(fg_muted()).wrap())
        .child(
            row()
                .w_full()
                .h(40.0)
                .px(12.0)
                .radius(RADIUS_SMALL)
                .fill(glass_control_fill())
                .center_y()
                .justify_between()
                .child(
                    text("Waiting for your action…")
                        .font(type_small())
                        .color(fg_muted()),
                )
                .child(pill(
                    format!("Step 1 of {}", BEATS.len()),
                    fg_faint().alpha(0.2),
                    fg_muted(),
                )),
        )
        .child(row().w_full().justify_end().center_y().child(button(
            "setup-tour-skip",
            "Skip tour",
            false,
            |s: &mut AppState| s.setup_skip_stage(),
        )));
    el().w_full().h_full().child(room).child(card)
}

fn state_word(st: StageState) -> &'static str {
    match st {
        StageState::Done => "Done",
        StageState::Skipped => "Skipped",
        StageState::Pending => "Not started",
    }
}

fn done_fact(label: &str, value: String) -> El {
    col()
        .w_full()
        .gap(6.0)
        .child(text(label.to_string()).font(type_small()).color(fg_muted()))
        .child(
            row()
                .w_full()
                .gap(8.0)
                .center_y()
                .child(el().size(6.0, 6.0).radius(3.0).fill(good()))
                .child(text(value).font(type_body()).color(FG).wrap()),
        )
}

fn done(s: &AppState, scale: f32) -> El {
    let all_done = s.setup.states[..5].iter().all(|st| *st == StageState::Done);
    let title = if all_done {
        "You're ready to play"
    } else {
        "Setup saved"
    };
    let has_skipped = s.setup.states[..5].contains(&StageState::Skipped);
    let devices =
        s.pc.devices
            .as_ref()
            .map(|d| d.devices.clone())
            .unwrap_or_default();
    let mut strip = row().w_full().grow(1.0).min_h(0.0).gap(12.0);
    for dev in devices.iter().take(5) {
        let photo =
            s.pc.device_images
                .get(&dev.id)
                .and_then(|u| s.device_image(u, scale));
        let media = match photo {
            Some(img) => el()
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .pixels_fit(img.pixels(), SurfaceFit::Fit),
            None => el()
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .center()
                .child(device_glyph(&dev.kind, 96.0)),
        };
        let mut name_row = row().w_full().gap(8.0).center_y().child(
            text(dev.name.clone())
                .font(type_body())
                .color(FG)
                .wrap()
                .grow(1.0)
                .min_w(0.0),
        );
        if let Some(conn) = dev.connection.clone().filter(|c| !c.is_empty()) {
            name_row = name_row.child(pill(conn, fg_faint().alpha(0.2), fg_muted()));
        }
        let maker_model = device_maker_model(dev);
        let sub = if maker_model.is_empty() {
            dev.kind.clone()
        } else {
            maker_model
        };
        strip = strip.child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .padding(16.0)
                .gap(8.0)
                .radius(RADIUS_CARD)
                .fill(BG)
                .border_gradient(1.0, glass_rim_top(), glass_rim_bottom())
                .child(media)
                .child(name_row)
                .child(text(sub).font(type_small()).color(fg_muted()).wrap()),
        );
    }
    let facts = glass_panel_opt("setup-done-facts", 470.0, true)
        .padding(20.0)
        .gap(18.0)
        .child(
            row()
                .w_full()
                .gap(24.0)
                .child(done_fact(
                    "Stores & library",
                    format!("{} stores · {} games", stores_count(s), s.games.len()),
                ))
                .child(done_fact(
                    "Devices",
                    format!("{} set up or found", device_count(s)),
                )),
        )
        .child(
            row()
                .w_full()
                .gap(24.0)
                .child(done_fact(
                    "PC defaults",
                    match s.setup.states[3] {
                        StageState::Skipped => "Skipped".into(),
                        _ => format!("{} applied", s.setup.picks.as_ref().map_or(0, Vec::len)),
                    },
                ))
                .child(done_fact(
                    "Live tour",
                    match s.setup.states[4] {
                        StageState::Done => "3 of 3 actions completed".into(),
                        other => state_word(other).to_string(),
                    },
                )),
        );
    let copy = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(16.0)
        .child(pill(
            if all_done {
                "Setup complete"
            } else {
                "Setup paused"
            },
            good().alpha(0.16),
            good(),
        ))
        .child(text(title).font(hero_style()).color(FG))
        .child(
            text(done_summary(s))
                .font(type_body())
                .color(fg_muted())
                .wrap(),
        )
        .child(
            row()
                .gap(10.0)
                .pt(6.0)
                .child(button(
                    "setup-done-library",
                    "Open Library",
                    true,
                    |s: &mut AppState| s.setup_finish(Room::Library),
                ))
                .child(button(
                    "setup-done-devices",
                    "Open Devices",
                    false,
                    |s: &mut AppState| s.setup_finish(Room::Devices),
                )),
        );
    let hero = row()
        .w_full()
        .grow(1.2)
        .min_h(0.0)
        .px(56.0)
        .gap(32.0)
        .center_y()
        .child(copy)
        .child(facts);
    let mut footer = row()
        .w_full()
        .h(64.0)
        .px(24.0)
        .gap(12.0)
        .center_y()
        .justify_between()
        .child(
            text(if has_skipped {
                "Skipped steps stay quietly available under Settings › Setup."
            } else {
                "Nothing is left to set up. Settings › Setup stays available."
            })
            .font(type_small())
            .color(fg_muted()),
        )
        .child(
            row()
                .gap(10.0)
                .center_y()
                .child(button(
                    "setup-done-replay",
                    "Replay interactive tour",
                    false,
                    |s: &mut AppState| s.start_tour(),
                ))
                .child(button(
                    "setup-done-settings",
                    "Settings › Setup",
                    false,
                    |s: &mut AppState| s.setup_finish(Room::Settings),
                )),
        );
    if has_skipped {
        footer = footer.child(button(
            "setup-done-resume",
            "Resume skipped steps",
            false,
            |s: &mut AppState| s.setup_resume(),
        ));
    }
    el().w_full()
        .h_full()
        .child(
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .child(mosaic(s, scale)),
        )
        .child(
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(BG.alpha(0.72)),
        )
        .child(
            col()
                .w_full()
                .h_full()
                .child(hero)
                .child(
                    col()
                        .w_full()
                        .grow(1.0)
                        .min_h(0.0)
                        .px(56.0)
                        .pb(20.0)
                        .child(strip),
                )
                .child(el().w_full().h(1.0).fill(hairline()))
                .child(footer),
        )
}

fn stores_count(s: &AppState) -> usize {
    store_ids(s).len()
}

fn done_summary(s: &AppState) -> String {
    let defaults = if s.setup.states[3] == StageState::Skipped {
        "PC defaults skipped".to_string()
    } else {
        format!(
            "{} PC defaults applied",
            s.setup.picks.as_ref().map_or(0, Vec::len)
        )
    };
    format!(
        "{} installed games across {} stores, {} peripherals set up or found, and {}. Everything stays editable anytime.",
        installed_count(s),
        stores_count(s),
        device_count(s),
        defaults
    )
}

fn footer(s: &AppState) -> El {
    let stage = s.setup.stage;
    let next = if stage + 1 < STAGES {
        STAGE_TITLES[stage + 1]
    } else {
        "Library"
    };
    let note = if stage == DONE_STAGE {
        String::new()
    } else {
        format!("Stage {} of {STAGES} · Next: {next}", stage + 1)
    };
    let mut actions = row().gap(10.0).center_y();
    if stage > 0 && stage < DONE_STAGE {
        actions = actions.child(button("setup-back", "Back", false, |s: &mut AppState| {
            s.setup_back()
        }));
    }
    if stage < TOUR_STAGE {
        actions = actions.child(button(
            "setup-skip-step",
            "Skip step",
            false,
            |s: &mut AppState| s.setup_skip_stage(),
        ));
    }
    if stage == TOUR_STAGE {
        actions = actions.child(button(
            "setup-skip-tour-step",
            "Skip live tour",
            false,
            |s: &mut AppState| s.setup_skip_stage(),
        ));
    }
    if stage < DONE_STAGE {
        let label = match stage {
            0 => "Start setup",
            TOUR_STAGE => "Start tour",
            _ => "Continue",
        };
        actions = actions.child(button("setup-continue", label, true, |s: &mut AppState| {
            s.setup_next()
        }));
    }
    row()
        .w_full()
        .h(64.0)
        .px(24.0)
        .gap(12.0)
        .center_y()
        .justify_between()
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(text(note).font(type_small()).color(fg_muted())),
        )
        .child(actions)
}

pub fn view(s: &AppState, scale: f32) -> El {
    let body = match s.setup.stage {
        0 => welcome(s, scale),
        1 => accounts(s),
        2 => devices(s, scale),
        3 => pc_defaults(s),
        TOUR_STAGE => tour_stage(s, scale),
        _ => done(s, scale),
    };
    let mut root = col()
        .w_full()
        .h_full()
        .fill(BG)
        .child(track(s))
        .child(
            col()
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .clip_rounded(0.0)
                .child(body),
        )
        .child(el().w_full().h(1.0).fill(hairline()));
    if s.setup.stage != 0 && s.setup.stage != DONE_STAGE {
        root = root.child(footer(s));
    }
    if s.setup.stage == DONE_STAGE {
        root = root.child(el());
    }
    root
}

fn coach_anchor(s: &AppState, beat: usize) -> (String, Side, Align) {
    match beat {
        0 => ("search-capsule".into(), Side::Bottom, Align::End),
        1 => {
            let key = s
                .games
                .iter()
                .find(|g| g.installed)
                .map_or("nav-library".to_string(), |g| format!("tile-{}", g.id));
            (key, Side::Bottom, Align::Start)
        }
        _ => ("nav-devices".into(), Side::Bottom, Align::Center),
    }
}

fn coach_steps(active: usize) -> El {
    let mut steps = row()
        .w_full()
        .padding(4.0)
        .gap(4.0)
        .radius(RADIUS_PILL)
        .fill(glass_control_fill());
    for (i, label) in COACH_STEPS.iter().enumerate() {
        let on = i == active;
        steps = steps.child(
            row()
                .grow(1.0)
                .min_w(0.0)
                .h(28.0)
                .radius(RADIUS_PILL)
                .center()
                .fill(if on { FG.alpha(0.12) } else { FG.alpha(0.0) })
                .child(
                    text(*label)
                        .font(type_small())
                        .color(if on { FG } else { fg_muted() }),
                ),
        );
    }
    steps
}

pub fn coach(s: &AppState) -> El {
    let beat = s.setup.beat.unwrap_or(0).min(BEATS.len() - 1);
    let (title, body) = BEATS[beat];
    let met = s.setup.beat_met_at.is_some();
    let (anchor, side, align) = coach_anchor(s, beat);
    let steps = coach_steps(beat);
    let (status_text, status_ink) = if met {
        ("Done", good())
    } else {
        ("Waiting for your action…", fg_muted())
    };
    glass_panel_opt("setup-coach", 480.0, true)
        .anchored(anchor, side, align, 12.0)
        .padding(20.0)
        .gap(14.0)
        .child(steps)
        .child(text(title).font(type_page()).color(FG).wrap())
        .child(text(body).font(type_body()).color(fg_muted()).wrap())
        .child(
            row()
                .w_full()
                .h(40.0)
                .px(12.0)
                .radius(RADIUS_SMALL)
                .fill(glass_control_fill())
                .center_y()
                .justify_between()
                .child(text(status_text).font(type_small()).color(status_ink))
                .child(pill(
                    format!("Step {} of {}", beat + 1, BEATS.len()),
                    fg_faint().alpha(0.2),
                    fg_muted(),
                )),
        )
        .child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .child(button(
                    "setup-coach-skip-tour",
                    "Skip tour",
                    false,
                    |s: &mut AppState| s.finish_tour(),
                ))
                .child(button(
                    "setup-coach-skip-step",
                    "Skip step",
                    false,
                    |s: &mut AppState| s.setup_beat_skip(),
                )),
        )
}

pub fn return_bar(s: &AppState) -> El {
    let stage = s.setup.return_to.unwrap_or(2).min(STAGES - 1);
    glass_panel_opt("setup-return", 300.0, true)
        .pin(None, Some(76.0), Some(24.0), None)
        .h(48.0)
        .px(16.0)
        .gap(12.0)
        .center_y()
        .child(
            text(format!("Setup paused on {}", STAGE_TITLES[stage]))
                .font(type_small())
                .color(FG),
        )
        .child(button(
            "setup-return-btn",
            "Back to setup",
            true,
            |s: &mut AppState| s.setup_return(),
        ))
}
