use crate::events::{EventRegistry, HostEventKind};
use crate::state::AppState;
use exo_kit::{
    BG, FG, PRESS_SCALE, RADIUS_CARD, RADIUS_PILL, RADIUS_SHEET, SPRING_BASE, SPRING_QUICK,
    TRANSPARENT, bad, fg_muted, glass_button, glass_panel_opt, hover_fill, icon_chevron_down,
    icon_chevron_up, icon_close, icon_pause, icon_play, modal_card_transition, pressed_fill,
    primary_button, scaled, switch_pill_keyed, track_fill, type_body, type_section, type_small,
    type_title,
};
use exo_rpc::{ActionResult, HostEvent, InstallProgress};
use rune::{Cursor, El, ScrollSpec, Transition, col, el, row, text};
use serde_json::{Value, json};

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DownloadLine {
    pub game_id: String,
    pub title: String,
    pub store: String,
    pub state: String,
    pub can_pause: bool,
    pub auto_update: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DownloadsList {
    pub lines: Vec<DownloadLine>,
    pub paused_by_player: bool,
    pub held: Option<String>,
}

impl DownloadsList {
    pub fn from_json(v: &Value) -> Self {
        let lines = v
            .get("lines")
            .and_then(|x| x.as_array())
            .map(|arr| {
                arr.iter()
                    .map(|item| DownloadLine {
                        game_id: item
                            .get("gameId")
                            .and_then(|s| s.as_str())
                            .unwrap_or("")
                            .to_string(),
                        title: item
                            .get("title")
                            .and_then(|s| s.as_str())
                            .unwrap_or("")
                            .to_string(),
                        store: item
                            .get("store")
                            .and_then(|s| s.as_str())
                            .unwrap_or("")
                            .to_string(),
                        state: item
                            .get("state")
                            .and_then(|s| s.as_str())
                            .unwrap_or("")
                            .to_string(),
                        can_pause: item
                            .get("canPause")
                            .and_then(|b| b.as_bool())
                            .unwrap_or(false),
                        auto_update: item
                            .get("autoUpdate")
                            .and_then(|b| b.as_bool())
                            .unwrap_or(false),
                    })
                    .collect()
            })
            .unwrap_or_default();
        Self {
            lines,
            paused_by_player: v
                .get("pausedByPlayer")
                .and_then(|b| b.as_bool())
                .unwrap_or(false),
            held: v
                .get("held")
                .and_then(|s| s.as_str())
                .map(|s| s.to_string()),
        }
    }
}

#[derive(Debug, Clone, Default)]
pub struct DownloadsState {
    pub open: bool,
    pub loading: bool,
    pub list: Option<DownloadsList>,
    pub busy_key: Option<String>,
    pub error: Option<String>,
}

pub fn format_speed(bytes_per_second: Option<f64>) -> Option<String> {
    let bps = bytes_per_second?;
    if !bps.is_finite() || bps <= 0.0 {
        return None;
    }
    if bps >= 1_048_576.0 {
        Some(format!("{:.1} MB/s", bps / 1_048_576.0))
    } else {
        Some(format!("{} KB/s", (bps / 1024.0).round() as u64))
    }
}

pub fn store_display_name(store: &str) -> &str {
    match store {
        "steam" => "Steam",
        "epic" => "Epic",
        "gog" => "GOG",
        "amazon" => "Amazon",
        "riot" => "Riot",
        "xbox" => "Xbox",
        "ea" => "EA",
        "ubisoft" => "Ubisoft",
        "battlenet" => "Battle.net",
        other => other,
    }
}

pub fn register_events(reg: &mut EventRegistry) {
    reg.on(HostEventKind::InstallQueue, |state, _| {
        if state.downloads.open {
            state.load_downloads();
        }
        state.downloads.open
    });
    reg.on(HostEventKind::InstallProgress, |state, ev| {
        let HostEvent::InstallProgress(p) = ev else {
            return false;
        };
        if state.downloads.open && !p.is_active {
            state.load_downloads();
        }
        state.downloads.open
    });
}

impl AppState {
    pub fn open_downloads(&mut self) {
        self.close_context_menu();
        self.close_dropdown();
        self.library_more_open = false;
        self.library_add_open = false;
        self.library_filter_open = false;
        self.downloads.open = true;
        self.downloads.error = None;
        self.load_downloads();
        self.load_install_progress();
    }

    pub fn close_downloads(&mut self) {
        self.downloads.open = false;
    }

    pub fn load_downloads(&mut self) {
        self.downloads.loading = true;
        #[cfg(windows)]
        self.call_host_async(
            "downloads.list",
            json!({}),
            |s, res: Result<Value, String>| {
                s.downloads.loading = false;
                match res {
                    Ok(val) => {
                        s.downloads.list = Some(DownloadsList::from_json(&val));
                        s.downloads.error = None;
                    }
                    Err(_) => {
                        s.downloads.error =
                            Some("Exo couldn't load your downloads. Try again.".into());
                    }
                }
            },
        );
    }

    fn load_install_progress(&mut self) {
        #[cfg(windows)]
        self.call_host_async(
            "game.progress",
            json!({}),
            |s, res: Result<InstallProgress, String>| {
                if let Ok(progress) = res {
                    s.install_progress = progress.is_active.then_some(progress);
                }
            },
        );
    }

    fn run_download_action(&mut self, key: String, method: &'static str, params: Value) {
        self.downloads.busy_key = Some(key);
        self.downloads.error = None;
        #[cfg(windows)]
        self.call_host_async(method, params, |s, res: Result<ActionResult, String>| {
            s.downloads.busy_key = None;
            match res {
                Ok(reply) => {
                    if !reply.ok {
                        s.downloads.error = Some(
                            reply
                                .message
                                .filter(|m| !m.trim().is_empty())
                                .unwrap_or_else(|| {
                                    "That download action failed. Try again.".into()
                                }),
                        );
                    }
                }
                Err(_) => {
                    s.downloads.error = Some("That download action failed. Try again.".into());
                }
            }
            s.load_downloads();
        });
        #[cfg(not(windows))]
        let _ = (method, params);
    }

    pub fn downloads_pause_all(&mut self) {
        self.run_download_action("all".into(), "downloads.pauseAll", json!({}));
    }

    pub fn downloads_resume_all(&mut self) {
        self.run_download_action("all".into(), "downloads.resumeAll", json!({}));
    }

    pub fn downloads_move(&mut self, game_id: String, by: i32) {
        let key = game_id.clone();
        self.run_download_action(key, "downloads.move", json!({ "id": game_id, "by": by }));
    }

    pub fn downloads_dequeue(&mut self, game_id: String) {
        let key = game_id.clone();
        self.run_download_action(key, "game.dequeue", json!({ "id": game_id }));
    }

    pub fn downloads_resume(&mut self, game_id: String) {
        let key = game_id.clone();
        self.run_download_action(key, "game.resumeDownload", json!({ "id": game_id }));
    }

    pub fn downloads_pause(&mut self, game_id: String) {
        let key = game_id.clone();
        self.run_download_action(key, "game.pauseInstall", json!({ "id": game_id }));
    }

    pub fn downloads_cancel(&mut self, game_id: String) {
        let key = game_id.clone();
        self.run_download_action(key, "game.cancelInstall", json!({ "id": game_id }));
    }

    pub fn downloads_set_auto_update(&mut self, game_id: String, on: bool) {
        let key = game_id.clone();
        self.run_download_action(key, "game.autoUpdate", json!({ "id": game_id, "on": on }));
    }
}

fn icon_action_btn(
    key: String,
    label: String,
    icon: El,
    disabled: bool,
    on_press: impl Fn(&mut AppState) + 'static,
) -> El {
    let mut btn = el()
        .key(key)
        .role("button")
        .label(label)
        .focusable(!disabled)
        .disabled(disabled)
        .hit()
        .cursor(if disabled {
            Cursor::NotAllowed
        } else {
            Cursor::Pointer
        })
        .press_on_down()
        .press_scale(if disabled { 1.0 } else { PRESS_SCALE })
        .size(30.0, 30.0)
        .radius(RADIUS_PILL)
        .center()
        .fill(TRANSPARENT)
        .hover(if disabled { TRANSPARENT } else { hover_fill() })
        .press(if disabled {
            TRANSPARENT
        } else {
            pressed_fill()
        })
        .opacity(if disabled { 0.35 } else { 1.0 })
        .motion(SPRING_QUICK)
        .child(icon);
    if !disabled {
        btn = btn.on_press(on_press);
    }
    btn
}

fn download_row(
    line: &DownloadLine,
    busy: bool,
    progress: Option<&InstallProgress>,
    actions: Vec<El>,
) -> El {
    let pct = progress.map(|p| p.percent.clamp(0.0, 100.0));
    let detail = if let Some(p) = progress {
        let mut parts: Vec<String> = Vec::new();
        if let Some(ref st) = p.status
            && !st.trim().is_empty()
        {
            parts.push(st.clone());
        }
        if p.bytes_per_second > 0
            && let Some(spd) = format_speed(Some(p.bytes_per_second as f64))
        {
            parts.push(spd);
        }
        if parts.is_empty() {
            store_display_name(&line.store).to_string()
        } else {
            parts.join(" · ")
        }
    } else {
        store_display_name(&line.store).to_string()
    };

    let mut info = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(3.0)
        .child(
            text(line.title.clone())
                .font(type_section())
                .color(FG)
                .truncate(),
        )
        .child(text(detail).font(type_small()).color(fg_muted()).truncate());

    if let Some(percent) = pct {
        let frac = (percent / 100.0) as f32;
        info = info.child(
            el().w_full()
                .h(3.0)
                .mt(4.0)
                .radius(RADIUS_PILL)
                .fill(track_fill())
                .child(
                    el().w(scaled(220.0) * frac)
                        .h(3.0)
                        .radius(RADIUS_PILL)
                        .fill(FG),
                ),
        );
    }

    let gid_auto = line.game_id.clone();
    let next_auto = !line.auto_update;
    let mut toggle = switch_pill_keyed(
        format!("dl-auto-{}", line.game_id),
        format!("Update {} automatically", line.title),
        line.auto_update,
    )
    .disabled(busy)
    .opacity(if busy { 0.45 } else { 1.0 });
    if !busy {
        toggle = toggle.on_press(move |s: &mut AppState| {
            s.downloads_set_auto_update(gid_auto.clone(), next_auto);
        });
    }

    let auto_col = row()
        .gap(10.0)
        .center_y()
        .child(
            text("Update automatically")
                .font(type_small())
                .color(fg_muted()),
        )
        .child(toggle);

    let act_col = row()
        .w(scaled(180.0))
        .justify_end()
        .center_y()
        .gap(6.0)
        .children(actions);

    row()
        .key(format!("dl-row-{}", line.game_id))
        .w_full()
        .min_h(56.0)
        .px(12.0)
        .py(8.0)
        .gap(16.0)
        .center_y()
        .radius(RADIUS_CARD)
        .hover(hover_fill())
        .motion(SPRING_QUICK)
        .child(info)
        .child(auto_col)
        .child(act_col)
}

fn group_section(title: &'static str, rows: Vec<El>) -> El {
    col()
        .w_full()
        .gap(6.0)
        .child(text(title).font(type_small()).color(fg_muted()))
        .child(col().w_full().gap(2.0).children(rows))
}

pub fn layers(s: &AppState, blur: bool) -> Vec<El> {
    let list = s.downloads.list.as_ref();
    let lines = list.map(|l| l.lines.as_slice()).unwrap_or(&[]);
    let busy_all = s.downloads.busy_key.as_deref() == Some("all");
    let paused_by_player = list.is_some_and(|l| l.paused_by_player);

    let head_btn = if paused_by_player {
        let mut b = primary_button(
            "dl-resume-all",
            "Resume all",
            Some(icon_play(14.0, exo_kit::PRIMARY_FG)),
        )
        .disabled(busy_all)
        .opacity(if busy_all { 0.45 } else { 1.0 });
        if !busy_all {
            b = b.on_press(|s: &mut AppState| s.downloads_resume_all());
        }
        b
    } else {
        let dis = busy_all || lines.is_empty();
        let mut b = glass_button("dl-pause-all", "Pause all", Some(icon_pause(14.0, FG)))
            .disabled(dis)
            .cursor(if dis {
                Cursor::NotAllowed
            } else {
                Cursor::Pointer
            })
            .opacity(if dis { 0.45 } else { 1.0 });
        if !dis {
            b = b.on_press(|s: &mut AppState| s.downloads_pause_all());
        }
        b
    };

    let header = row()
        .w_full()
        .center_y()
        .justify_between()
        .gap(12.0)
        .child(text("Downloads").font(type_title()).color(FG))
        .child(head_btn);

    let mut body = col().w_full().px(28.0).py(20.0).gap(16.0).child(header);

    if let Some(ref err) = s.downloads.error {
        let mut err_row = row().w_full().gap(10.0).center_y().child(
            text(err.clone())
                .font(type_body())
                .color(bad())
                .grow(1.0)
                .wrap(),
        );
        if list.is_none() {
            err_row = err_row.child(
                glass_button("dl-retry", "Try again", None)
                    .on_press(|s: &mut AppState| s.load_downloads()),
            );
        }
        body = body.child(err_row);
    }

    if let Some(held) =
        list.and_then(|l| (!l.paused_by_player).then_some(l.held.as_deref()).flatten())
        && !held.trim().is_empty()
    {
        body = body.child(
            text(held.to_string())
                .font(type_body())
                .color(fg_muted())
                .wrap(),
        );
    }

    if list.is_some() && lines.is_empty() {
        body = body.child(
            text("Nothing is downloading.")
                .font(type_body())
                .color(fg_muted()),
        );
    }

    let running: Vec<&DownloadLine> = lines.iter().filter(|l| l.state == "running").collect();
    let queued: Vec<&DownloadLine> = lines.iter().filter(|l| l.state == "queued").collect();
    let paused: Vec<&DownloadLine> = lines.iter().filter(|l| l.state == "paused").collect();

    if !running.is_empty() {
        let rows = running
            .into_iter()
            .map(|line| {
                let busy = s.downloads.busy_key.as_deref() == Some(line.game_id.as_str());
                let prog = s
                    .install_progress
                    .as_ref()
                    .filter(|p| p.game_id.as_deref() == Some(line.game_id.as_str()));
                let mut acts = Vec::new();
                if line.can_pause {
                    let gid = line.game_id.clone();
                    let mut pb = glass_button("dl-pause", "Pause", Some(icon_pause(14.0, FG)))
                        .key(format!("dl-pause-{}", line.game_id))
                        .disabled(busy)
                        .opacity(if busy { 0.45 } else { 1.0 });
                    if !busy {
                        pb = pb.on_press(move |s: &mut AppState| s.downloads_pause(gid.clone()));
                    }
                    acts.push(pb);
                }
                let gid_stop = line.game_id.clone();
                let mut sb = glass_button("dl-stop", "Stop", None)
                    .key(format!("dl-stop-{}", line.game_id))
                    .disabled(busy)
                    .opacity(if busy { 0.45 } else { 1.0 });
                if !busy {
                    sb = sb.on_press(move |s: &mut AppState| s.downloads_cancel(gid_stop.clone()));
                }
                acts.push(sb);
                download_row(line, busy, prog, acts)
            })
            .collect();
        body = body.child(group_section("Now", rows));
    }

    if !queued.is_empty() {
        let total = queued.len();
        let rows = queued
            .into_iter()
            .enumerate()
            .map(|(i, line)| {
                let busy = s.downloads.busy_key.as_deref() == Some(line.game_id.as_str());
                let gid_up = line.game_id.clone();
                let gid_dn = line.game_id.clone();
                let gid_rm = line.game_id.clone();
                let acts = vec![
                    icon_action_btn(
                        format!("dl-up-{}", line.game_id),
                        format!("Move {} up", line.title),
                        icon_chevron_up(16.0, FG),
                        i == 0 || busy,
                        move |s| s.downloads_move(gid_up.clone(), -1),
                    ),
                    icon_action_btn(
                        format!("dl-dn-{}", line.game_id),
                        format!("Move {} down", line.title),
                        icon_chevron_down(16.0, FG),
                        i + 1 == total || busy,
                        move |s| s.downloads_move(gid_dn.clone(), 1),
                    ),
                    icon_action_btn(
                        format!("dl-rm-{}", line.game_id),
                        format!("Remove {} from the queue", line.title),
                        icon_close(15.0, FG),
                        busy,
                        move |s| s.downloads_dequeue(gid_rm.clone()),
                    ),
                ];
                download_row(line, busy, None, acts)
            })
            .collect();
        body = body.child(group_section("Up next", rows));
    }

    if !paused.is_empty() {
        let rows = paused
            .into_iter()
            .map(|line| {
                let busy = s.downloads.busy_key.as_deref() == Some(line.game_id.as_str());
                let gid_res = line.game_id.clone();
                let gid_rm = line.game_id.clone();
                let mut rb = glass_button("dl-resume", "Resume", Some(icon_play(14.0, FG)))
                    .key(format!("dl-resume-{}", line.game_id))
                    .disabled(busy)
                    .opacity(if busy { 0.45 } else { 1.0 });
                if !busy {
                    rb = rb.on_press(move |s: &mut AppState| s.downloads_resume(gid_res.clone()));
                }
                let acts = vec![
                    rb,
                    icon_action_btn(
                        format!("dl-prm-{}", line.game_id),
                        format!("Remove {}", line.title),
                        icon_close(15.0, FG),
                        busy,
                        move |s| s.downloads_dequeue(gid_rm.clone()),
                    ),
                ];
                download_row(line, busy, None, acts)
            })
            .collect();
        body = body.child(group_section("Paused", rows));
    }

    let card = glass_panel_opt("downloads-sheet", scaled(880.0), blur)
        .role("dialog")
        .label("Downloads")
        .max_h(scaled(760.0))
        .radius(RADIUS_SHEET)
        .clip_rounded(RADIUS_SHEET)
        .motion(SPRING_BASE)
        .transition(modal_card_transition())
        .modal(true, |s: &mut AppState| s.close_downloads())
        .modal_scrim(BG.alpha(0.56))
        .child(
            col()
                .w_full()
                .max_h(scaled(760.0))
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .child(body),
        );

    let host = col()
        .key("downloads-host")
        .pin(Some(0.0), Some(52.0), Some(0.0), Some(0.0))
        .center()
        .transition(Transition::none())
        .child(card);

    vec![host]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn speed_formatting_matches_react_thresholds() {
        assert_eq!(format_speed(None), None);
        assert_eq!(format_speed(Some(0.0)), None);
        assert_eq!(format_speed(Some(-5.0)), None);
        assert_eq!(format_speed(Some(757_760.0)).as_deref(), Some("740 KB/s"));
        assert_eq!(
            format_speed(Some(19_293_798.4)).as_deref(),
            Some("18.4 MB/s")
        );
    }

    #[test]
    fn downloads_sheet_opens_renders_groups_and_closes() {
        let mut s = AppState::default();
        s.downloads.open = true;
        s.downloads.list = Some(DownloadsList {
            lines: vec![
                DownloadLine {
                    game_id: "steam:10".into(),
                    title: "Half-Life".into(),
                    store: "steam".into(),
                    state: "running".into(),
                    can_pause: true,
                    auto_update: true,
                },
                DownloadLine {
                    game_id: "epic:abc".into(),
                    title: "Alan Wake 2".into(),
                    store: "epic".into(),
                    state: "queued".into(),
                    can_pause: false,
                    auto_update: false,
                },
                DownloadLine {
                    game_id: "gog:99".into(),
                    title: "Witcher 3".into(),
                    store: "gog".into(),
                    state: "paused".into(),
                    can_pause: false,
                    auto_update: true,
                },
            ],
            paused_by_player: false,
            held: None,
        });
        let els = layers(&s, true);
        assert_eq!(els.len(), 1);
        s.close_downloads();
        assert!(!s.downloads.open);
    }
}
