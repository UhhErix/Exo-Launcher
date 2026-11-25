use crate::events::{EventRegistry, HostEventKind};
use crate::state::{AppState, HostStatus, NoticeTone};
use exo_kit::{
    BG, FG, RADIUS_PANEL, RADIUS_PILL, bad, exo_mark, fg_muted, glass_button, glass_panel_opt,
    good, icon_alert, icon_check, icon_cpu, icon_monitor, primary_button, progress_bar, scaled,
    track_fill, type_body, type_small, type_title,
};
use exo_rpc::HostEvent;
use rune::{El, col, el, row, text};
use serde_json::{Value, json};
use std::panic::{AssertUnwindSafe, catch_unwind};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DriverInstallKind {
    Gpu,
    Chipset,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DriverInstallPhase {
    Starting,
    Downloading,
    Unpacking,
    Waiting,
    Installing,
    Reloading,
    Restoring,
    Done,
    Failed,
}

impl DriverInstallPhase {
    pub fn from_wire(s: &str) -> Option<Self> {
        match s {
            "starting" => Some(Self::Starting),
            "downloading" => Some(Self::Downloading),
            "unpacking" => Some(Self::Unpacking),
            "waiting" => Some(Self::Waiting),
            "installing" => Some(Self::Installing),
            "reloading" => Some(Self::Reloading),
            "restoring" => Some(Self::Restoring),
            "done" => Some(Self::Done),
            "failed" => Some(Self::Failed),
            _ => None,
        }
    }

    pub fn order(self) -> usize {
        match self {
            Self::Starting => 0,
            Self::Downloading => 1,
            Self::Unpacking => 2,
            Self::Waiting => 3,
            Self::Installing => 4,
            Self::Reloading => 5,
            Self::Restoring => 6,
            Self::Done => 7,
            Self::Failed => 8,
        }
    }

    pub fn ended(self) -> bool {
        matches!(self, Self::Done | Self::Failed)
    }
}

#[derive(Debug, Clone)]
pub struct DriverInstall {
    pub kind: DriverInstallKind,
    pub phase: DriverInstallPhase,
    pub name: Option<String>,
    pub version: Option<String>,
    pub share: Option<f32>,
    pub size_bytes: Option<u64>,
    pub message: Option<String>,
    pub restored: Vec<String>,
    pub not_restored: Vec<String>,
    pub aside: bool,
    pub restart_busy: bool,
    pub restart_error: Option<String>,
}

impl DriverInstall {
    pub fn working(&self) -> bool {
        !self.phase.ended()
    }

    pub fn visible(&self) -> bool {
        !(self.aside && self.phase == DriverInstallPhase::Waiting)
    }

    pub fn named(&self) -> String {
        let mut parts = Vec::new();
        if let Some(ref n) = self.name
            && !n.trim().is_empty()
        {
            parts.push(n.as_str());
        }
        if let Some(ref v) = self.version
            && !v.trim().is_empty()
        {
            parts.push(v.as_str());
        }
        parts.join(" ")
    }

    pub fn step_words(&self) -> String {
        if self.phase == DriverInstallPhase::Waiting
            && let Some(ref msg) = self.message
            && !msg.trim().is_empty()
        {
            return msg.clone();
        }
        let base = match (self.kind, self.phase) {
            (DriverInstallKind::Gpu, DriverInstallPhase::Starting) => "Starting the update",
            (DriverInstallKind::Gpu, DriverInstallPhase::Downloading) => "Downloading the driver",
            (DriverInstallKind::Gpu, DriverInstallPhase::Unpacking) => "Unpacking the driver",
            (DriverInstallKind::Gpu, DriverInstallPhase::Waiting) => "Waiting to install",
            (DriverInstallKind::Gpu, DriverInstallPhase::Installing) => "Installing the driver",
            (DriverInstallKind::Gpu, DriverInstallPhase::Reloading) => {
                "Reloading the graphics driver"
            }
            (DriverInstallKind::Gpu, DriverInstallPhase::Restoring) => "Putting your settings back",
            (DriverInstallKind::Chipset, DriverInstallPhase::Starting) => {
                "Starting the chipset update"
            }
            (DriverInstallKind::Chipset, DriverInstallPhase::Downloading) => {
                "Downloading the chipset package"
            }
            (DriverInstallKind::Chipset, DriverInstallPhase::Unpacking) => {
                "Unpacking the chipset installer"
            }
            (DriverInstallKind::Chipset, DriverInstallPhase::Waiting) => "Waiting to install",
            (DriverInstallKind::Chipset, DriverInstallPhase::Installing) => {
                "Installing the CPU & chipset drivers"
            }
            (DriverInstallKind::Chipset, DriverInstallPhase::Reloading) => {
                "Applying chipset updates"
            }
            (DriverInstallKind::Chipset, DriverInstallPhase::Restoring) => {
                "Verifying system devices"
            }
            (_, DriverInstallPhase::Done) => "Done",
            (_, DriverInstallPhase::Failed) => "Failed",
        };
        if self.phase == DriverInstallPhase::Downloading {
            if let (Some(share), Some(size)) = (self.share, self.size_bytes)
                && size > 0
            {
                let fmt_bytes = |b: f64| -> String {
                    if b >= 1e9 {
                        format!("{:.1} GB", b / 1e9)
                    } else {
                        format!("{} MB", (b / 1e6).round() as u64)
                    }
                };
                let pct = (share.clamp(0.0, 1.0) * 100.0).round() as i32;
                return format!(
                    "{base} · {} of {} · {pct}%",
                    fmt_bytes(size as f64 * share as f64),
                    fmt_bytes(size as f64)
                );
            }
            if let Some(share) = self.share {
                let pct = (share.clamp(0.0, 1.0) * 100.0).round() as i32;
                return format!("{base} · {pct}%");
            }
        }
        base.to_string()
    }
}

#[derive(Debug, Clone, Default)]
pub struct BootState {
    pub painted_sent: bool,
    pub room_crash: Option<(String, String)>,
    pub driver_install: Option<DriverInstall>,
}

pub fn register_events(reg: &mut EventRegistry) {
    reg.on(HostEventKind::GpuDriver, |state, ev| {
        let HostEvent::GpuDriver(p) = ev else {
            return false;
        };
        if state
            .boot
            .driver_install
            .as_ref()
            .is_some_and(|d| d.kind == DriverInstallKind::Chipset)
        {
            return false;
        }
        let stage = p.stage.as_str();
        let pct = Some((p.percent / 100.0).clamp(0.0, 1.0) as f32);
        match stage {
            "downloading" | "unpacking" | "waiting" | "installing" => {
                if let Some(phase) = DriverInstallPhase::from_wire(stage) {
                    state.advance_driver_install(
                        DriverInstallKind::Gpu,
                        phase,
                        None,
                        None,
                        if stage == "downloading" { pct } else { None },
                        p.message,
                        None,
                        None,
                    );
                    return true;
                }
            }
            "failed" if state.boot.driver_install.is_some() => {
                state.advance_driver_install(
                    DriverInstallKind::Gpu,
                    DriverInstallPhase::Failed,
                    None,
                    None,
                    None,
                    p.message,
                    None,
                    None,
                );
                return true;
            }
            _ => {}
        }
        false
    });

    reg.on(HostEventKind::GpuDriverInstall, |state, ev| {
        let HostEvent::GpuDriverInstall(g) = ev else {
            return false;
        };
        if state
            .boot
            .driver_install
            .as_ref()
            .is_some_and(|d| d.kind == DriverInstallKind::Chipset)
        {
            return false;
        }
        let Some(phase) = DriverInstallPhase::from_wire(&g.phase) else {
            return false;
        };
        let restored = (!g.restored.is_empty()).then_some(g.restored);
        let not_restored = (!g.not_restored.is_empty()).then_some(g.not_restored);
        state.advance_driver_install(
            DriverInstallKind::Gpu,
            phase,
            None,
            g.version,
            None,
            g.message,
            restored,
            not_restored,
        );
        true
    });

    reg.on_raw("gpu.driver", |state, data| {
        if state
            .boot
            .driver_install
            .as_ref()
            .is_some_and(|d| d.kind == DriverInstallKind::Chipset)
        {
            return false;
        }
        let stage = data.get("stage").and_then(|v| v.as_str()).unwrap_or("");
        let msg = data
            .get("message")
            .and_then(|v| v.as_str())
            .map(|s| s.to_string());
        let pct = data
            .get("percent")
            .and_then(|v| v.as_f64())
            .map(|p| (p / 100.0).clamp(0.0, 1.0) as f32);
        match stage {
            "downloading" | "unpacking" | "waiting" | "installing" => {
                if let Some(phase) = DriverInstallPhase::from_wire(stage) {
                    state.advance_driver_install(
                        DriverInstallKind::Gpu,
                        phase,
                        None,
                        None,
                        if stage == "downloading" { pct } else { None },
                        msg,
                        None,
                        None,
                    );
                    return true;
                }
            }
            "failed" if state.boot.driver_install.is_some() => {
                state.advance_driver_install(
                    DriverInstallKind::Gpu,
                    DriverInstallPhase::Failed,
                    None,
                    None,
                    None,
                    msg,
                    None,
                    None,
                );
                return true;
            }
            _ => {}
        }
        false
    });

    reg.on_raw("gpu.driverInstall", |state, data| {
        if state
            .boot
            .driver_install
            .as_ref()
            .is_some_and(|d| d.kind == DriverInstallKind::Chipset)
        {
            return false;
        }
        let phase_str = data.get("phase").and_then(|v| v.as_str()).unwrap_or("");
        let Some(phase) = DriverInstallPhase::from_wire(phase_str) else {
            return false;
        };
        let version = data
            .get("version")
            .and_then(|v| v.as_str())
            .map(|s| s.to_string());
        let name = data
            .get("name")
            .and_then(|v| v.as_str())
            .map(|s| s.to_string());
        let message = data
            .get("message")
            .and_then(|v| v.as_str())
            .map(|s| s.to_string());
        let restored = data.get("restored").and_then(|v| v.as_array()).map(|arr| {
            arr.iter()
                .filter_map(|x| x.as_str().map(|s| s.to_string()))
                .collect()
        });
        let not_restored = data
            .get("notRestored")
            .and_then(|v| v.as_array())
            .map(|arr| {
                arr.iter()
                    .filter_map(|x| x.as_str().map(|s| s.to_string()))
                    .collect()
            });
        state.advance_driver_install(
            DriverInstallKind::Gpu,
            phase,
            name,
            version,
            None,
            message,
            restored,
            not_restored,
        );
        true
    });
}

impl AppState {
    #[allow(clippy::too_many_arguments)]
    pub fn advance_driver_install(
        &mut self,
        kind: DriverInstallKind,
        phase: DriverInstallPhase,
        name: Option<String>,
        version: Option<String>,
        share: Option<f32>,
        message: Option<String>,
        restored: Option<Vec<String>>,
        not_restored: Option<Vec<String>>,
    ) {
        if let Some(ref cur) = self.boot.driver_install
            && phase != DriverInstallPhase::Failed
            && phase.order() < cur.phase.order()
        {
            return;
        }
        let prev = self.boot.driver_install.take();
        let mut next = prev.unwrap_or(DriverInstall {
            kind,
            phase,
            name: None,
            version: None,
            share: None,
            size_bytes: None,
            message: None,
            restored: Vec::new(),
            not_restored: Vec::new(),
            aside: false,
            restart_busy: false,
            restart_error: None,
        });
        next.phase = phase;
        if phase != DriverInstallPhase::Waiting {
            next.aside = false;
        }
        if name.is_some() {
            next.name = name;
        }
        if version.is_some() {
            next.version = version;
        }
        next.share = share;
        if message.is_some() {
            next.message = message;
        }
        if let Some(r) = restored {
            next.restored = r;
        }
        if let Some(nr) = not_restored {
            next.not_restored = nr;
        }
        self.boot.driver_install = Some(next);
    }

    pub fn driver_install_overlay_visible(&self) -> bool {
        self.boot
            .driver_install
            .as_ref()
            .is_some_and(DriverInstall::visible)
    }

    pub fn dismiss_driver_install(&mut self) {
        self.boot.driver_install = None;
    }

    pub fn hide_driver_install_while_waiting(&mut self) {
        if let Some(ref mut d) = self.boot.driver_install
            && d.phase == DriverInstallPhase::Waiting
        {
            d.aside = true;
        }
    }

    pub fn restart_windows_for_driver(&mut self) {
        if let Some(ref mut d) = self.boot.driver_install {
            d.restart_busy = true;
            d.restart_error = None;
        }
        #[cfg(windows)]
        self.call_host_async(
            "bios.update",
            json!({ "action": "windows" }),
            |s, res: Result<Value, String>| {
                let ok = res
                    .as_ref()
                    .ok()
                    .and_then(|v| v.get("ok").and_then(|b| b.as_bool()))
                    .unwrap_or(false);
                if !ok && let Some(ref mut d) = s.boot.driver_install {
                    d.restart_busy = false;
                    let msg = res
                        .ok()
                        .and_then(|v| {
                            v.get("message")
                                .and_then(|m| m.as_str())
                                .map(|s| s.to_string())
                        })
                        .filter(|m| !m.trim().is_empty())
                        .unwrap_or_else(|| {
                            "Windows didn’t restart. Restart from the Start menu.".into()
                        });
                    d.restart_error = Some(msg);
                }
            },
        );
    }

    pub fn copy_error_logs(&mut self, error: String, area: String) {
        #[cfg(windows)]
        self.call_host_async(
            "app.logsFor",
            json!({ "error": error, "area": area }),
            |s, res: Result<Value, String>| {
                let ok = res
                    .as_ref()
                    .ok()
                    .and_then(|v| v.get("ok").and_then(|b| b.as_bool()))
                    .unwrap_or(false);
                if ok {
                    s.push_notice("Copied logs to clipboard", NoticeTone::Good);
                } else {
                    s.push_notice("Could not copy logs.", NoticeTone::Bad);
                }
            },
        );
    }

    pub fn retry_boot_bootstrap(&mut self) {
        self.status = HostStatus::Connecting;
        self.boot.room_crash = None;
        #[cfg(windows)]
        self.call_host_async(
            exo_rpc::METHOD_SHELL_BOOTSTRAP,
            json!({}),
            |s, res: Result<exo_rpc::ShellBootstrap, String>| match res {
                Ok(boot) => {
                    s.apply_bootstrap(boot);
                    s.status = HostStatus::Connected;
                    s.check_system_eq_health_on_launch();
                }
                Err(why) => {
                    s.status = HostStatus::Error(why);
                }
            },
        );
    }

    pub fn notify_shell_painted_once(&mut self) {
        if self.boot.painted_sent || self.status != HostStatus::Connected {
            return;
        }
        self.boot.painted_sent = true;
        #[cfg(windows)]
        self.call_host_async("shell.painted", json!({}), |_, _: Result<Value, String>| {});
    }

    pub fn should_show_boot_gate(&self) -> bool {
        matches!(self.status, HostStatus::Connecting | HostStatus::Error(_))
            || self.whats_new.holds_boot()
    }
}

pub fn boot_splash_if_needed(s: &AppState, blur: bool) -> Option<El> {
    if !s.should_show_boot_gate() {
        return None;
    }
    if let HostStatus::Error(ref err) = s.status {
        let err_copy = err.clone();
        let card = glass_panel_opt("boot-error-card", scaled(448.0), blur)
            .role("alert")
            .label("Settings could not be loaded")
            .radius(RADIUS_PANEL)
            .px(24.0)
            .py(24.0)
            .child(
                col()
                    .w_full()
                    .items_center()
                    .gap(10.0)
                    .child(
                        text("Settings could not be loaded")
                            .font(type_title())
                            .color(FG),
                    )
                    .child(
                        text(err.clone())
                            .font(type_small())
                            .color(fg_muted())
                            .wrap(),
                    )
                    .child(
                        row()
                            .mt(10.0)
                            .gap(8.0)
                            .center_y()
                            .child(
                                primary_button("boot-retry", "Try again", None)
                                    .on_press(|s: &mut AppState| s.retry_boot_bootstrap()),
                            )
                            .child(
                                glass_button("boot-open-logs", "Open logs", None)
                                    .on_press(|s: &mut AppState| s.open_app_logs()),
                            )
                            .child(glass_button("boot-copy-logs", "Copy logs", None).on_press(
                                move |s: &mut AppState| {
                                    s.copy_error_logs(err_copy.clone(), "Boot".into());
                                },
                            )),
                    ),
            );
        return Some(
            col()
                .key("boot-error-gate")
                .w_full()
                .h_full()
                .fill(BG)
                .center()
                .px(24.0)
                .child(card),
        );
    }

    if matches!(s.status, HostStatus::Connecting) || s.whats_new.holds_boot() {
        let aria = if s.whats_new.holds_boot() {
            "Checking for updates"
        } else {
            "Preparing library and game tools"
        };
        return Some(
            col()
                .key("boot-splash")
                .role("status")
                .label(aria)
                .w_full()
                .h_full()
                .fill(BG)
                .center()
                .child(exo_mark(128.0, FG)),
        );
    }
    None
}

pub fn render_room_guarded(s: &AppState, label: &str, build: impl FnOnce() -> El) -> El {
    if let Some((ref crashed_label, ref crashed_msg)) = s.boot.room_crash
        && crashed_label == label
    {
        return error_boundary_card(label, crashed_msg);
    }
    match catch_unwind(AssertUnwindSafe(build)) {
        Ok(view) => view,
        Err(payload) => {
            let msg = payload
                .downcast_ref::<&str>()
                .map(|s| (*s).to_string())
                .or_else(|| payload.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "an error".into());
            error_boundary_card(label, &msg)
        }
    }
}

pub fn error_boundary_card(label: &str, message: &str) -> El {
    let body = if label == "Exo" {
        "Exo hit an error. Try again, or open the logs to see what happened.".to_string()
    } else {
        format!("{label} hit an error. The rest of Exo is still running.")
    };
    let err_for_copy = format!("{label} stopped: {message}");
    let area = label.to_string();

    col()
        .key(format!("error-boundary-{label}"))
        .role("alert")
        .label("This part of Exo stopped")
        .w_full()
        .h_full()
        .center()
        .gap(12.0)
        .px(24.0)
        .child(
            text("This part of Exo stopped")
                .font(type_title())
                .color(FG),
        )
        .child(text(body).font(type_body()).color(fg_muted()))
        .child(
            row()
                .mt(8.0)
                .gap(8.0)
                .center_y()
                .child(
                    primary_button("boundary-retry", "Try again", None).on_press(
                        |s: &mut AppState| {
                            s.boot.room_crash = None;
                        },
                    ),
                )
                .child(
                    glass_button("boundary-logs", "Open logs", None)
                        .on_press(|s: &mut AppState| s.open_app_logs()),
                )
                .child(
                    glass_button("boundary-copy-logs", "Copy logs", None).on_press(
                        move |s: &mut AppState| {
                            s.copy_error_logs(err_for_copy.clone(), area.clone());
                        },
                    ),
                ),
        )
}

pub fn driver_install_overlay(s: &AppState) -> Option<El> {
    let install = s.boot.driver_install.as_ref()?;
    if !install.visible() {
        return None;
    }
    let working = install.working();
    let stage = if install.phase == DriverInstallPhase::Done {
        el().size(72.0, 72.0)
            .radius(RADIUS_PILL)
            .border(2.0, good())
            .center()
            .child(icon_check(36.0, good()))
    } else if install.kind == DriverInstallKind::Chipset {
        el().size(96.0, 96.0).center().child(icon_cpu(
            56.0,
            if install.phase == DriverInstallPhase::Failed {
                bad()
            } else {
                FG
            },
        ))
    } else {
        el().size(96.0, 96.0).center().child(icon_monitor(
            56.0,
            if install.phase == DriverInstallPhase::Failed {
                bad()
            } else {
                FG
            },
        ))
    };

    let mut body = col()
        .w(scaled(420.0))
        .items_center()
        .gap(12.0)
        .child(el().size(124.0, 124.0).center().child(stage));

    let named = install.named();
    if working {
        let title = if !named.is_empty() {
            named
        } else if install.kind == DriverInstallKind::Chipset {
            "CPU & chipset drivers".into()
        } else {
            "Graphics driver".into()
        };
        let step_words = install.step_words();
        let bar_frac = if install.phase == DriverInstallPhase::Downloading {
            install.share.unwrap_or(0.25)
        } else {
            (install.phase.order() as f32 / 7.0).clamp(0.15, 0.95)
        };
        body = body
            .child(text(title).font(type_title()).color(FG))
            .child(text(step_words).font(type_body()).color(fg_muted()))
            .child(
                el().w_full()
                    .mt(8.0)
                    .child(progress_bar(scaled(420.0), bar_frac)),
            );

        let milestones: &[(DriverInstallPhase, &str)] = match install.kind {
            DriverInstallKind::Gpu => &[
                (DriverInstallPhase::Downloading, "Download"),
                (DriverInstallPhase::Unpacking, "Prepare"),
                (DriverInstallPhase::Installing, "Install"),
                (DriverInstallPhase::Restoring, "Restore"),
            ],
            DriverInstallKind::Chipset => &[
                (DriverInstallPhase::Starting, "Prepare"),
                (DriverInstallPhase::Installing, "Install"),
                (DriverInstallPhase::Done, "Verify"),
            ],
        };
        let at = install.phase.order();
        let mut rail = row().mt(6.0).gap(16.0).center_y();
        for &(m_phase, m_label) in milestones {
            let m_at = m_phase.order();
            let done = at > m_at;
            let active = at == m_at
                || (install.kind == DriverInstallKind::Gpu
                    && install.phase == DriverInstallPhase::Reloading
                    && m_phase == DriverInstallPhase::Installing);
            let dot_color = if done {
                good()
            } else if active {
                FG
            } else {
                track_fill()
            };
            let label_color = if done || active { FG } else { fg_muted() };
            rail = rail.child(
                row()
                    .gap(6.0)
                    .center_y()
                    .child(el().size(7.0, 7.0).radius(3.5).fill(dot_color))
                    .child(text(m_label).font(type_small()).color(label_color)),
            );
        }
        body = body.child(rail);
    } else if install.phase == DriverInstallPhase::Failed {
        let title = if install.kind == DriverInstallKind::Chipset {
            "The chipset drivers didn’t install"
        } else {
            "The driver didn’t install"
        };
        let retry_hint = if install.kind == DriverInstallKind::Chipset {
            "Windows kept the driver you had. Try the update again from PC › Updates."
        } else {
            "Windows kept the driver you had. Try the update again from PC › Graphics."
        };
        body = body.child(text(title).font(type_title()).color(FG));
        if !named.is_empty() {
            body = body.child(text(named).font(type_body()).color(fg_muted()));
        }
        body = body.child(
            text(
                install
                    .message
                    .clone()
                    .filter(|m| !m.trim().is_empty())
                    .unwrap_or_else(|| retry_hint.into()),
            )
            .font(type_body())
            .color(bad())
            .w_full()
            .wrap(),
        );
    } else {
        let title = if install.kind == DriverInstallKind::Chipset {
            "Chipset drivers updated"
        } else {
            "Driver updated"
        };
        body = body.child(text(title).font(type_title()).color(FG));
        if !named.is_empty() {
            body = body.child(text(named).font(type_body()).color(fg_muted()));
        }
        let settings_line = if install.restored.is_empty() {
            None
        } else if !install.not_restored.is_empty() {
            Some("Exo put the rest of your settings back.")
        } else {
            Some("Exo put your settings back on the new driver.")
        };
        let summary = match settings_line {
            Some(s) => format!("{s} Restart your PC to finish."),
            None => "Restart your PC to finish.".into(),
        };
        body = body.child(text(summary).font(type_body()).color(fg_muted()).wrap());

        if !install.not_restored.is_empty() {
            let mut missed_col = col().w_full().gap(8.0).mt(4.0);
            for item in &install.not_restored {
                missed_col = missed_col.child(
                    row()
                        .w_full()
                        .gap(10.0)
                        .center_y()
                        .child(icon_alert(16.0, bad()))
                        .child(text(item.clone()).font(type_body()).color(bad()).wrap()),
                );
            }
            body = body.child(missed_col);
        }
    }

    let mut foot = row().mt(8.0).gap(8.0).center_y();
    match install.phase {
        DriverInstallPhase::Waiting => {
            foot = foot.child(
                glass_button("driver-hide", "Hide until it installs", None)
                    .on_press(|s: &mut AppState| s.hide_driver_install_while_waiting()),
            );
        }
        DriverInstallPhase::Done => {
            if let Some(ref err) = install.restart_error {
                body = body.child(text(err.clone()).font(type_body()).color(bad()).wrap());
            }
            let busy = install.restart_busy;
            let mut not_now = glass_button("driver-not-now", "Not now", None)
                .disabled(busy)
                .opacity(if busy { 0.45 } else { 1.0 });
            if !busy {
                not_now = not_now.on_press(|s: &mut AppState| s.dismiss_driver_install());
            }
            let mut restart = primary_button(
                "driver-restart-now",
                if busy { "Restarting…" } else { "Restart now" },
                None,
            )
            .disabled(busy)
            .opacity(if busy { 0.45 } else { 1.0 });
            if !busy {
                restart = restart.on_press(|s: &mut AppState| s.restart_windows_for_driver());
            }
            foot = foot.child(not_now).child(restart);
        }
        DriverInstallPhase::Failed => {
            let err_copy = install
                .message
                .clone()
                .unwrap_or_else(|| "Driver install failed".into());
            foot = foot
                .child(
                    glass_button("driver-copy-logs", "Copy logs", None).on_press(
                        move |s: &mut AppState| {
                            s.copy_error_logs(err_copy.clone(), "Driver install".into());
                        },
                    ),
                )
                .child(
                    primary_button("driver-close", "Close", None)
                        .on_press(|s: &mut AppState| s.dismiss_driver_install()),
                );
        }
        _ => {}
    }
    body = body.child(foot);

    Some(
        col()
            .key("driver-install-overlay")
            .role("alertdialog")
            .label("Driver update")
            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
            .fill(BG)
            .hit()
            .center()
            .child(body),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::HostEvent;

    #[test]
    fn driver_install_phase_never_moves_backwards_except_failed() {
        let mut s = AppState::default();
        s.apply_host_event(HostEvent::Raw {
            event: "gpu.driver".into(),
            data: json!({ "stage": "downloading", "percent": 42.0 }),
        });
        assert_eq!(
            s.boot.driver_install.as_ref().unwrap().phase,
            DriverInstallPhase::Downloading
        );

        s.apply_host_event(HostEvent::Raw {
            event: "gpu.driverInstall".into(),
            data: json!({ "phase": "installing", "version": "572.16" }),
        });
        assert_eq!(
            s.boot.driver_install.as_ref().unwrap().phase,
            DriverInstallPhase::Installing
        );

        s.apply_host_event(HostEvent::Raw {
            event: "gpu.driver".into(),
            data: json!({ "stage": "downloading", "percent": 99.0 }),
        });
        assert_eq!(
            s.boot.driver_install.as_ref().unwrap().phase,
            DriverInstallPhase::Installing
        );

        s.apply_host_event(HostEvent::Raw {
            event: "gpu.driverInstall".into(),
            data: json!({
                "phase": "done",
                "restored": ["Low Latency Mode"],
                "notRestored": ["Custom color profile"]
            }),
        });
        let d = s.boot.driver_install.as_ref().unwrap();
        assert_eq!(d.phase, DriverInstallPhase::Done);
        assert_eq!(d.not_restored, vec!["Custom color profile"]);
        assert!(driver_install_overlay(&s).is_some());
    }

    #[test]
    fn error_boundary_catches_room_panic_and_renders_fallback() {
        let s = AppState::default();
        let _el = render_room_guarded(&s, "Library", || {
            panic!("boom");
        });
    }

    #[test]
    fn boot_gate_shows_splash_while_connecting_and_error_card_on_failure() {
        let mut s = AppState::for_host_pipe();
        assert!(boot_splash_if_needed(&s, true).is_some());
        s.status = HostStatus::Error("pipe timeout".into());
        assert!(boot_splash_if_needed(&s, true).is_some());
        s.status = HostStatus::Connected;
        s.whats_new.boot_hold_active = false;
        assert!(boot_splash_if_needed(&s, true).is_none());
    }
}
