use crate::devices_audio::{
    DevicePageHeader, device_btn, device_page_shell_columns_no_rescan, device_panel, device_row,
    device_row_compact, device_switch,
};
use crate::state::{AppState, HostStatus};
use exo_hid_devices::auto_profiles::{
    AntiCheatClass, AutoProfileEngine, AutoSwitchDecision, GameProfileRule, MouseRemapMode,
};
use exo_hid_devices::drivers::{ClickMode, DpiStage, LodLevel, MouseConfig, WritePersistence};
use exo_hid_devices::irq_core::{CpuCoreDescriptor, IrqAffinityPlan, plan_xhci_irq_affinity};
use exo_hid_devices::poll_diag::{PollingAnalyzer, PollingHealth, PollingReport};
use exo_hid_devices::transport::HidWriteRateLimiter;
use exo_hid_devices::usb_topology::UsbPortClass;
use exo_hid_devices::{
    ManagedMouseDevice, NamedMouseProfile, default_game_rules, default_named_profiles,
    dev_mock_devices,
};
use exo_kit::{
    BadgeTone, FG, PRESS_SCALE, RADIUS_PANEL, SPRING_QUICK, amoled_card, bad, card_border,
    fg_faint, fg_muted, good, hover_fill, icon_mouse, status_badge, type_body, type_control,
    type_small, warn,
};
use exo_rpc::{
    ActionResult, METHOD_MOUSE_PROFILES_GET, METHOD_MOUSE_PROFILES_SET, MouseDataReply,
    PeripheralCard,
};
use rune::{Cursor, El, SurfaceFit, col, el, row, text};
use serde_json::json;
use std::time::{Duration, Instant};

const RECEIVER_LED_CHOICES: &[(&str, &str)] = &[
    ("#FFFFFF", "White"),
    ("#3DD68C", "Green"),
    ("#3B82F6", "Blue"),
    ("#FFB340", "Amber"),
    ("#FF5C5C", "Red"),
    ("#A855F7", "Violet"),
];

const STAGE_COLORS: &[&str] = &[
    "#FF5C5C", "#3DD68C", "#3B82F6", "#FFB340", "#A855F7", "#00C8FF",
];

#[derive(Clone, Debug)]
pub struct NativeMouseUiState {
    pub devices: Vec<ManagedMouseDevice>,
    pub selected_index: usize,
    pub profiles: Vec<NamedMouseProfile>,
    pub active_profile_id: String,
    pub game_rules: Vec<GameProfileRule>,
    pub auto_engine: AutoProfileEngine,
    pub rate_limiter: HidWriteRateLimiter,
    pub irq_plan: Option<IrqAffinityPlan>,
    pub last_polling: Option<PollingReport>,
    pub write_error: Option<String>,
    pub notice: Option<String>,
    pub total_writes: usize,
    pub skipped_redundant_writes: usize,
    pub rate_limited_count: usize,
    pub dirty_flash: bool,
    pub profiles_loaded: bool,
    pub mock_active_game: Option<String>,
}

impl Default for NativeMouseUiState {
    fn default() -> Self {
        Self::demo()
    }
}

impl NativeMouseUiState {
    pub fn demo() -> Self {
        let devices = dev_mock_devices();
        let Some(first) = devices.first().cloned() else {
            return Self {
                devices: Vec::new(),
                selected_index: 0,
                profiles: Vec::new(),
                active_profile_id: String::new(),
                game_rules: Vec::new(),
                auto_engine: AutoProfileEngine::new(MouseConfig::default(), 500),
                rate_limiter: HidWriteRateLimiter::default(),
                irq_plan: None,
                last_polling: None,
                write_error: None,
                notice: None,
                total_writes: 0,
                skipped_redundant_writes: 0,
                rate_limited_count: 0,
                dirty_flash: false,
                profiles_loaded: false,
                mock_active_game: None,
            };
        };
        let profiles = default_named_profiles(&first.config, &first.capabilities);
        let rules = default_game_rules(&first.config, &first.capabilities);
        let mut auto_engine = AutoProfileEngine::new(first.config.clone(), 500);
        auto_engine.set_applied_state(first.config.clone());
        for r in &rules {
            auto_engine.upsert_rule(r.clone());
        }
        let irq_plan = plan_xhci_irq_affinity(
            first
                .usb_topology
                .controller_id
                .as_deref()
                .unwrap_or("PCI\\VEN_1022&DEV_149C"),
            &default_cpu_cores(),
        );
        let mut analyzer = PollingAnalyzer::new();
        let nominal_us = 1_000_000.0 / first.config.polling_hz.max(125) as f64;
        for i in 0..160 {
            let jitter = if i % 7 == 0 { 6.0 } else { -2.0 };
            analyzer.push_interval_us((nominal_us + jitter).max(60.0));
        }
        Self {
            devices,
            selected_index: 0,
            profiles,
            active_profile_id: "profile-default".into(),
            game_rules: rules,
            auto_engine,
            rate_limiter: HidWriteRateLimiter::default(),
            irq_plan,
            last_polling: analyzer.report(),
            write_error: None,
            notice: None,
            total_writes: 0,
            skipped_redundant_writes: 0,
            rate_limited_count: 0,
            dirty_flash: false,
            profiles_loaded: false,
            mock_active_game: None,
        }
    }

    pub fn active_device(&self) -> Option<&ManagedMouseDevice> {
        self.devices
            .get(self.selected_index)
            .or_else(|| self.devices.first())
    }

    pub fn active_device_mut(&mut self) -> Option<&mut ManagedMouseDevice> {
        let idx = if self.selected_index < self.devices.len() {
            self.selected_index
        } else {
            0
        };
        self.devices.get_mut(idx)
    }

    pub fn select_device(&mut self, index: usize) {
        if index >= self.devices.len() {
            return;
        }
        self.selected_index = index;
        self.write_error = None;
        self.notice = None;
        self.dirty_flash = false;
        if let Some(dev) = self.devices.get(index).cloned() {
            self.profiles = default_named_profiles(&dev.config, &dev.capabilities);
            self.active_profile_id = "profile-default".into();
            self.game_rules = default_game_rules(&dev.config, &dev.capabilities);
            self.auto_engine = AutoProfileEngine::new(dev.config.clone(), 500);
            self.auto_engine.set_applied_state(dev.config.clone());
            for r in &self.game_rules {
                self.auto_engine.upsert_rule(r.clone());
            }
            self.irq_plan = plan_xhci_irq_affinity(
                dev.usb_topology
                    .controller_id
                    .as_deref()
                    .unwrap_or("PCI\\VEN_1022&DEV_149C"),
                &default_cpu_cores(),
            );
            self.run_mock_polling_sample();
        }
    }

    pub fn run_mock_polling_sample(&mut self) {
        let Some(dev) = self.active_device() else {
            return;
        };
        let mut analyzer = PollingAnalyzer::new();
        let nominal_us = 1_000_000.0 / dev.config.polling_hz.max(125) as f64;
        let spread = match dev.usb_topology.port_class {
            UsbPortClass::CpuDirect => 0.03,
            UsbPortClass::Chipset => 0.17,
            UsbPortClass::ExternalHub => 0.28,
            UsbPortClass::Unknown => 0.08,
        };
        for i in 0..240usize {
            let sign = if i % 2 == 0 { 1.0 } else { -1.0 };
            let delta = nominal_us * (1.0 + sign * spread);
            analyzer.push_interval_us(delta.max(55.0));
        }
        self.last_polling = analyzer.report();
    }

    pub fn apply_edit(
        &mut self,
        now: Instant,
        persistence: WritePersistence,
        edit: impl FnOnce(&mut MouseConfig),
    ) {
        let Some(dev) = self.active_device() else {
            return;
        };
        let key = dev.key.clone();
        let caps = dev.capabilities.clone();
        let current = dev.config.clone();
        let mut target = current.clone();
        edit(&mut target);
        target.clamp_to(&caps);

        if target == current && persistence == WritePersistence::VolatileRam {
            self.skipped_redundant_writes = self.skipped_redundant_writes.saturating_add(1);
            return;
        }

        if let Err(err) = self.rate_limiter.check_and_record(&key, now) {
            self.rate_limited_count = self.rate_limited_count.saturating_add(1);
            self.write_error = Some(err.to_string());
            return;
        }

        self.write_error = None;
        if let Some(dev_mut) = self.active_device_mut() {
            dev_mut.config = target.clone();
        }
        self.auto_engine.set_applied_state(target);
        self.total_writes = self.total_writes.saturating_add(1);
        match persistence {
            WritePersistence::VolatileRam => {
                self.dirty_flash = true;
                self.notice = None;
            }
            WritePersistence::CommitFlash => {
                self.dirty_flash = false;
                self.notice = Some("Saved profile to onboard mouse memory.".into());
            }
        }
    }

    pub fn activate_named_profile(&mut self, profile_id: &str, now: Instant) {
        let Some(prof) = self.profiles.iter().find(|p| p.id == profile_id).cloned() else {
            return;
        };
        self.active_profile_id = prof.id.clone();
        let cfg = prof.config.clone();
        self.apply_edit(now, WritePersistence::VolatileRam, |c| *c = cfg);
        self.run_mock_polling_sample();
    }

    pub fn simulate_game_focus(&mut self, game_id: Option<&str>) {
        let now = Instant::now();
        self.mock_active_game = game_id.map(str::to_string);
        self.auto_engine.on_foreground_changed(game_id, now);
        let settled = now + Duration::from_millis(self.auto_engine.debounce_ms() + 10);
        match self.auto_engine.poll(settled) {
            AutoSwitchDecision::Apply {
                profile_name,
                config,
                persistence,
                remap_decision,
                ..
            } => {
                let target = config.clone();
                self.apply_edit(settled, persistence, |c| *c = target);
                let suffix = if remap_decision.blocked_reason.is_some() {
                    " (onboard-only anti-cheat policy)"
                } else {
                    ""
                };
                self.notice = Some(format!(
                    "Auto-switched to {profile_name} in volatile RAM after {} ms debounce{suffix}.",
                    self.auto_engine.debounce_ms()
                ));
                self.run_mock_polling_sample();
            }
            AutoSwitchDecision::RateLimited { retry_after_ms } => {
                self.rate_limited_count = self.rate_limited_count.saturating_add(1);
                self.notice = Some(format!(
                    "Auto-switch rate-limited (at most 1 per 2 s; {retry_after_ms} ms left)."
                ));
            }
            AutoSwitchDecision::SkippedUnchanged => {
                self.skipped_redundant_writes = self.skipped_redundant_writes.saturating_add(1);
                self.notice =
                    Some("Skipped redundant HID write (hardware already matches).".into());
            }
            AutoSwitchDecision::PendingDebounce { remaining_ms } => {
                self.notice = Some(format!("Debouncing focus change ({remaining_ms} ms left)."));
            }
        }
    }

    pub fn remove_game_rule(&mut self, game_id: &str) {
        self.game_rules.retain(|r| r.game_id != game_id);
        self.auto_engine.remove_rule(game_id);
    }

    pub fn add_current_as_game_rule(&mut self, game_id: &str, game_name: &str) {
        let Some(dev) = self.active_device() else {
            return;
        };
        let rule = GameProfileRule {
            id: format!("rule-{}", game_id.replace(':', "-")),
            game_id: game_id.to_string(),
            game_name: game_name.to_string(),
            profile_name: self
                .profiles
                .iter()
                .find(|p| p.id == self.active_profile_id)
                .map(|p| p.name.clone())
                .unwrap_or_else(|| "Custom".into()),
            config: dev.config.clone(),
            anti_cheat_class: AntiCheatClass::Unknown,
            remap_mode: MouseRemapMode::HardwareOnboard,
        };
        self.game_rules.retain(|r| r.game_id != game_id);
        self.game_rules.push(rule.clone());
        self.auto_engine.upsert_rule(rule);
        self.notice = Some(format!("Linked {game_name} to current mouse profile."));
    }
}

fn default_cpu_cores() -> Vec<CpuCoreDescriptor> {
    (0..12u32)
        .map(|i| CpuCoreDescriptor {
            logical_index: i,
            physical_core: i / 2,
            ccd_index: 0,
            is_efficiency_core: false,
            is_smt_sibling: i % 2 == 1,
        })
        .collect()
}

impl AppState {
    pub fn open_mouse_page(&mut self, dev_id: String) {
        self.pc.selected_device_id = Some(dev_id.clone());
        let use_host_panel = self.status == HostStatus::Connected;
        self.pc.mouse_panel_open = use_host_panel;
        self.pc.mouse_panel_loading = use_host_panel;
        self.pc.mouse_panel_error = None;
        self.pc.mouse_panel_rect = None;
        self.pc.mouse_panel_visible = false;

        if let Some(dev_name) = self
            .pc
            .devices
            .as_ref()
            .and_then(|d| d.devices.iter().find(|item| item.id == dev_id))
            .map(|d| d.name.to_ascii_lowercase())
            && let Some(idx) = self.pc.mouse_ui.devices.iter().position(|m| {
                let model = m.model_name.to_ascii_lowercase();
                model.contains(&dev_name)
                    || dev_name.contains(&m.vendor.display_name().to_ascii_lowercase())
            })
        {
            self.pc.mouse_ui.select_device(idx);
        }

        if self.status != HostStatus::Standalone && !self.pc.mouse_ui.profiles_loaded {
            self.load_mouse_profiles_from_host();
        }
    }

    pub fn close_mouse_page(&mut self) {
        self.close_mouse_panel();
    }

    pub fn load_mouse_profiles_from_host(&mut self) {
        self.pc.mouse_ui.profiles_loaded = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, MouseDataReply>(METHOD_MOUSE_PROFILES_GET, &json!({}), move |res| {
                if let Ok(reply) = res
                    && reply.ok
                    && let Some(data) = reply.data
                {
                    let parsed: Option<Vec<GameProfileRule>> = data
                        .get("rules")
                        .and_then(|v| serde_json::from_value(v.clone()).ok());
                    if let Some(rules) = parsed {
                        handle.update(move |s: &mut AppState, _| {
                            if !rules.is_empty() {
                                s.pc.mouse_ui.game_rules = rules.clone();
                                for r in rules {
                                    s.pc.mouse_ui.auto_engine.upsert_rule(r);
                                }
                            }
                        });
                    }
                }
            });
        }
    }

    pub fn persist_mouse_profiles_to_host(&mut self) {
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            let payload = json!({
                "data": {
                    "profiles": self.pc.mouse_ui.profiles,
                    "rules": self.pc.mouse_ui.game_rules,
                }
            });
            rpc.call::<_, ActionResult>(METHOD_MOUSE_PROFILES_SET, &payload, |_| {});
        }
    }
}

fn format_hz(hz: u16) -> String {
    if hz >= 1000 && hz.is_multiple_of(1000) {
        format!("{} kHz", hz / 1000)
    } else {
        format!("{hz} Hz")
    }
}

fn chip_button(key: impl Into<String>, label: impl Into<String>, active: bool) -> El {
    row()
        .key(key.into())
        .role("button")
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .cursor(Cursor::Pointer)
        .h(30.0)
        .px(9.0)
        .center_y()
        .radius(8.0)
        .fill(if active {
            FG.alpha(0.12)
        } else {
            FG.alpha(0.02)
        })
        .border(
            1.0,
            if active {
                FG.alpha(0.55)
            } else {
                card_border()
            },
        )
        .hover(hover_fill())
        .motion(SPRING_QUICK)
        .child(text(label.into()).font(type_control()).color(FG))
}

fn mouse_row(label: impl Into<String>, detail: Option<impl Into<String>>, control: El) -> El {
    device_row_compact(label, detail.map(Into::into), None, Some(control))
}

fn mouse_badge(label: impl Into<String>, tone: BadgeTone) -> El {
    status_badge(label.into(), tone)
}

pub(crate) fn mouse_page(
    s: &AppState,
    dev: &PeripheralCard,
    display_name: String,
    status_line: String,
    scale: f32,
) -> El {
    let ui = &s.pc.mouse_ui;
    let Some(active) = ui.active_device() else {
        return device_page_shell_columns_no_rescan(
            DevicePageHeader {
                key: "room-devices-mouse",
                name: display_name,
                status: status_line,
                media: icon_mouse(56.0, fg_faint()),
                stats: Vec::new(),
                credit: None,
            },
            vec![device_panel(
                "Mouse",
                vec![device_row(
                    "No supported mouse detected",
                    Some("Connect a supported mouse or use the dev mock profiles.".into()),
                    None,
                    None,
                )],
            )],
            Vec::new(),
            |s: &mut AppState| s.close_mouse_page(),
        );
    };

    let cfg = &active.config;
    let caps = &active.capabilities;
    let hero_img =
        s.pc.device_images
            .get(&dev.id)
            .and_then(|u| s.device_image(u, scale));
    let media = if let Some(photo) = hero_img {
        el().size(355.0, 236.0)
            .pixels_fit(photo.pixels(), SurfaceFit::Fit)
    } else {
        icon_mouse(56.0, fg_faint())
    };

    let mut stats = vec![
        ("DPI", format!("{}", cfg.active_dpi())),
        ("Polling", format_hz(cfg.polling_hz)),
        ("LOD", cfg.lod.label().into()),
    ];
    if let Some(b) = active.battery_pct.or(dev.battery.map(|x| x.round() as u8)) {
        stats.push(("Battery", format!("{b}%")));
    }
    if !active.firmware.is_empty() {
        stats.push(("Firmware", active.firmware.clone()));
    }
    stats.push((
        "Port",
        match active.usb_topology.port_class {
            UsbPortClass::CpuDirect => "CPU-Direct USB".into(),
            UsbPortClass::Chipset => "Chipset USB".into(),
            UsbPortClass::ExternalHub => "External Hub".into(),
            UsbPortClass::Unknown => "USB".into(),
        },
    ));

    let mut col_a = Vec::new();
    let mut col_b = Vec::new();

    if let Some(ref err) = ui.write_error {
        col_a.push(
            amoled_card(RADIUS_PANEL)
                .key("mouse-write-error")
                .w_full()
                .px(20.0)
                .py(14.0)
                .child(
                    row()
                        .w_full()
                        .justify_between()
                        .center_y()
                        .gap(12.0)
                        .child(
                            col()
                                .grow(1.0)
                                .min_w(0.0)
                                .gap(2.0)
                                .child(text("Mouse write paused").font(type_body()).color(bad()))
                                .child(
                                    text(err.clone())
                                        .font(type_small())
                                        .color(fg_muted())
                                        .wrap(),
                                ),
                        )
                        .child(device_btn("mouse-clear-err", "Dismiss", false).on_press(
                            |s: &mut AppState| {
                                s.pc.mouse_ui.write_error = None;
                            },
                        )),
                ),
        );
    }

    if let Some(ref note) = ui.notice {
        col_a.push(
            amoled_card(RADIUS_PANEL)
                .key("mouse-notice")
                .w_full()
                .px(20.0)
                .py(12.0)
                .child(text(note.clone()).font(type_small()).color(good()).wrap()),
        );
    }

    if active.is_mock {
        let model_chips =
            row()
                .w_full()
                .flex_wrap()
                .gap(6.0)
                .children(ui.devices.iter().enumerate().map(|(idx, m)| {
                    let is_sel = idx == ui.selected_index;
                    chip_button(format!("mouse-mock-{idx}"), m.model_name.clone(), is_sel).on_press(
                        move |s: &mut AppState| {
                            s.pc.mouse_ui.select_device(idx);
                        },
                    )
                }));
        col_a.push(device_panel(
            "Native HID engine",
            vec![
                mouse_row(
                    "Emulated multi-vendor HID devices",
                    Some("Tests every clean-room Rust driver without writing to real hardware."),
                    mouse_badge("Mock transport", BadgeTone::Good),
                ),
                model_chips,
            ],
        ));
    }

    let profile_chips = row()
        .w_full()
        .flex_wrap()
        .gap(6.0)
        .children(ui.profiles.iter().map(|p| {
            let pid = p.id.clone();
            let is_sel = ui.active_profile_id == p.id;
            chip_button(format!("mouse-prof-{}", p.id), p.name.clone(), is_sel).on_press(
                move |s: &mut AppState| {
                    s.pc.mouse_ui.activate_named_profile(&pid, Instant::now());
                },
            )
        }));
    let save_row = row()
        .w_full()
        .justify_between()
        .center_y()
        .gap(8.0)
        .child(
            text(if ui.dirty_flash {
                "Applied to volatile RAM — click Save to commit to mouse flash."
            } else {
                "Onboard profile in sync."
            })
            .font(type_small())
            .color(if ui.dirty_flash { warn() } else { fg_muted() })
            .wrap(),
        )
        .child(
            row()
                .gap(8.0)
                .child(
                    device_btn("mouse-save-flash", "Save to mouse", !ui.dirty_flash).on_press(
                        |s: &mut AppState| {
                            s.pc.mouse_ui.apply_edit(
                                Instant::now(),
                                WritePersistence::CommitFlash,
                                |_| {},
                            );
                            s.persist_mouse_profiles_to_host();
                        },
                    ),
                )
                .child(
                    device_btn("mouse-reset", "Reset", false).on_press(|s: &mut AppState| {
                        s.pc.mouse_ui
                            .activate_named_profile("profile-default", Instant::now());
                    }),
                ),
        );
    col_a.push(device_panel("Profiles", vec![profile_chips, save_row]));

    let mut dpi_rows = Vec::new();
    let stage_chips =
        row()
            .w_full()
            .flex_wrap()
            .gap(6.0)
            .children(cfg.dpi_stages.iter().enumerate().map(|(idx, st)| {
                let is_sel = cfg.active_dpi_stage as usize == idx;
                chip_button(
                    format!("mouse-dpi-stage-{idx}"),
                    format!("S{} · {}", idx + 1, st.dpi),
                    is_sel,
                )
                .on_press(move |s: &mut AppState| {
                    s.pc.mouse_ui
                        .apply_edit(Instant::now(), WritePersistence::VolatileRam, |c| {
                            c.active_dpi_stage = idx as u8;
                        });
                })
            }));
    dpi_rows.push(stage_chips);

    let step = caps.dpi_step.max(50);
    let active_dpi = cfg.active_dpi();
    dpi_rows.push(mouse_row(
        "Active stage DPI",
        Some("Stepped cleanly to sensor increments; volatile RAM write."),
        row()
            .gap(6.0)
            .center_y()
            .child(
                device_btn("mouse-dpi-minus-big", "-200", active_dpi <= caps.min_dpi).on_press(
                    move |s: &mut AppState| {
                        s.pc.mouse_ui.apply_edit(
                            Instant::now(),
                            WritePersistence::VolatileRam,
                            |c| {
                                let idx = c.active_dpi_stage as usize;
                                if let Some(st) = c.dpi_stages.get_mut(idx) {
                                    st.dpi = st.dpi.saturating_sub(200);
                                }
                            },
                        );
                    },
                ),
            )
            .child(
                device_btn(
                    "mouse-dpi-minus",
                    format!("-{step}"),
                    active_dpi <= caps.min_dpi,
                )
                .on_press(move |s: &mut AppState| {
                    s.pc.mouse_ui
                        .apply_edit(Instant::now(), WritePersistence::VolatileRam, |c| {
                            let idx = c.active_dpi_stage as usize;
                            if let Some(st) = c.dpi_stages.get_mut(idx) {
                                st.dpi = st.dpi.saturating_sub(step);
                            }
                        });
                }),
            )
            .child(
                text(format!("{active_dpi} DPI"))
                    .font(type_control())
                    .color(FG),
            )
            .child(
                device_btn(
                    "mouse-dpi-plus",
                    format!("+{step}"),
                    active_dpi >= caps.max_dpi,
                )
                .on_press(move |s: &mut AppState| {
                    s.pc.mouse_ui
                        .apply_edit(Instant::now(), WritePersistence::VolatileRam, |c| {
                            let idx = c.active_dpi_stage as usize;
                            if let Some(st) = c.dpi_stages.get_mut(idx) {
                                st.dpi = st.dpi.saturating_add(step);
                            }
                        });
                }),
            )
            .child(
                device_btn("mouse-dpi-plus-big", "+200", active_dpi >= caps.max_dpi).on_press(
                    move |s: &mut AppState| {
                        s.pc.mouse_ui.apply_edit(
                            Instant::now(),
                            WritePersistence::VolatileRam,
                            |c| {
                                let idx = c.active_dpi_stage as usize;
                                if let Some(st) = c.dpi_stages.get_mut(idx) {
                                    st.dpi = st.dpi.saturating_add(200);
                                }
                            },
                        );
                    },
                ),
            ),
    ));

    let can_add_stage = cfg.dpi_stages.len() < caps.max_dpi_stages as usize;
    let can_remove_stage = cfg.dpi_stages.len() > 1;
    dpi_rows.push(
        row()
            .w_full()
            .justify_end()
            .gap(8.0)
            .child(
                device_btn("mouse-dpi-add", "Add stage", !can_add_stage).on_press(
                    |s: &mut AppState| {
                        s.pc.mouse_ui.apply_edit(
                            Instant::now(),
                            WritePersistence::VolatileRam,
                            |c| {
                                let next_dpi = c.active_dpi().saturating_add(400);
                                let color = STAGE_COLORS[c.dpi_stages.len() % STAGE_COLORS.len()];
                                c.dpi_stages.push(DpiStage {
                                    dpi: next_dpi,
                                    color: color.into(),
                                });
                            },
                        );
                    },
                ),
            )
            .child(
                device_btn("mouse-dpi-remove", "Remove stage", !can_remove_stage).on_press(
                    |s: &mut AppState| {
                        s.pc.mouse_ui.apply_edit(
                            Instant::now(),
                            WritePersistence::VolatileRam,
                            |c| {
                                if c.dpi_stages.len() > 1 {
                                    let idx =
                                        (c.active_dpi_stage as usize).min(c.dpi_stages.len() - 1);
                                    c.dpi_stages.remove(idx);
                                    c.active_dpi_stage =
                                        c.active_dpi_stage.min((c.dpi_stages.len() - 1) as u8);
                                }
                            },
                        );
                    },
                ),
            ),
    );
    col_a.push(device_panel("DPI stages", dpi_rows));

    let mut sensor_rows = Vec::new();
    let poll_chips =
        row()
            .w_full()
            .flex_wrap()
            .gap(6.0)
            .children(caps.polling_rates_hz.iter().map(|&hz| {
                chip_button(
                    format!("mouse-poll-{hz}"),
                    format_hz(hz),
                    cfg.polling_hz == hz,
                )
                .on_press(move |s: &mut AppState| {
                    s.pc.mouse_ui
                        .apply_edit(Instant::now(), WritePersistence::VolatileRam, |c| {
                            c.polling_hz = hz;
                        });
                    s.pc.mouse_ui.run_mock_polling_sample();
                })
            }));
    sensor_rows.push(
        col()
            .w_full()
            .gap(8.0)
            .child(text("Polling rate").font(type_body()).color(FG))
            .child(poll_chips),
    );

    if caps.has_lod {
        sensor_rows.push(mouse_row(
            "Lift-off distance",
            None::<&str>,
            row()
                .gap(6.0)
                .child(
                    chip_button("mouse-lod-1", "1 mm", cfg.lod == LodLevel::Low1Mm).on_press(
                        |s: &mut AppState| {
                            s.pc.mouse_ui.apply_edit(
                                Instant::now(),
                                WritePersistence::VolatileRam,
                                |c| c.lod = LodLevel::Low1Mm,
                            );
                        },
                    ),
                )
                .child(
                    chip_button("mouse-lod-2", "2 mm", cfg.lod == LodLevel::High2Mm).on_press(
                        |s: &mut AppState| {
                            s.pc.mouse_ui.apply_edit(
                                Instant::now(),
                                WritePersistence::VolatileRam,
                                |c| c.lod = LodLevel::High2Mm,
                            );
                        },
                    ),
                ),
        ));
    }

    if caps.has_motion_sync {
        sensor_rows.push(mouse_row(
            "Motion Sync",
            Some("Aligns sensor reads with USB polling intervals."),
            device_switch("mouse-motion-sync", "Motion Sync", cfg.motion_sync, false).on_press(
                |s: &mut AppState| {
                    s.pc.mouse_ui
                        .apply_edit(Instant::now(), WritePersistence::VolatileRam, |c| {
                            c.motion_sync = !c.motion_sync;
                        });
                },
            ),
        ));
    }

    if caps.has_angle_snapping {
        sensor_rows.push(mouse_row(
            "Angle snapping",
            None::<&str>,
            device_switch(
                "mouse-angle-snap",
                "Angle snapping",
                cfg.angle_snapping,
                false,
            )
            .on_press(|s: &mut AppState| {
                s.pc.mouse_ui
                    .apply_edit(Instant::now(), WritePersistence::VolatileRam, |c| {
                        c.angle_snapping = !c.angle_snapping;
                    });
            }),
        ));
    }

    if caps.has_ripple_control {
        sensor_rows.push(mouse_row(
            "Ripple control",
            None::<&str>,
            device_switch("mouse-ripple", "Ripple control", cfg.ripple_control, false).on_press(
                |s: &mut AppState| {
                    s.pc.mouse_ui
                        .apply_edit(Instant::now(), WritePersistence::VolatileRam, |c| {
                            c.ripple_control = !c.ripple_control;
                        });
                },
            ),
        ));
    }

    if caps.has_sensor_rotation {
        let deg = cfg.sensor_rotation_deg;
        sensor_rows.push(mouse_row(
            "Sensor rotation",
            Some("Compensates natural wrist tilt (-30° to +30°)."),
            row()
                .gap(6.0)
                .center_y()
                .child(
                    device_btn("mouse-rot-minus", "-1°", deg <= caps.rotation_min_deg).on_press(
                        |s: &mut AppState| {
                            s.pc.mouse_ui.apply_edit(
                                Instant::now(),
                                WritePersistence::VolatileRam,
                                |c| c.sensor_rotation_deg = c.sensor_rotation_deg.saturating_sub(1),
                            );
                        },
                    ),
                )
                .child(text(format!("{deg:+}°")).font(type_control()).color(FG))
                .child(
                    device_btn("mouse-rot-plus", "+1°", deg >= caps.rotation_max_deg).on_press(
                        |s: &mut AppState| {
                            s.pc.mouse_ui.apply_edit(
                                Instant::now(),
                                WritePersistence::VolatileRam,
                                |c| c.sensor_rotation_deg = c.sensor_rotation_deg.saturating_add(1),
                            );
                        },
                    ),
                ),
        ));
    }

    if caps.has_debounce_ms {
        let ms = cfg.debounce_ms;
        sensor_rows.push(mouse_row(
            "Click debounce",
            None::<&str>,
            row()
                .gap(6.0)
                .center_y()
                .child(
                    device_btn("mouse-deb-minus", "-1 ms", ms <= caps.debounce_min_ms).on_press(
                        |s: &mut AppState| {
                            s.pc.mouse_ui.apply_edit(
                                Instant::now(),
                                WritePersistence::VolatileRam,
                                |c| c.debounce_ms = c.debounce_ms.saturating_sub(1),
                            );
                        },
                    ),
                )
                .child(text(format!("{ms} ms")).font(type_control()).color(FG))
                .child(
                    device_btn("mouse-deb-plus", "+1 ms", ms >= caps.debounce_max_ms).on_press(
                        |s: &mut AppState| {
                            s.pc.mouse_ui.apply_edit(
                                Instant::now(),
                                WritePersistence::VolatileRam,
                                |c| c.debounce_ms = c.debounce_ms.saturating_add(1),
                            );
                        },
                    ),
                ),
        ));
    }

    col_a.push(device_panel("Sensor and polling", sensor_rows));

    if caps.has_tmr_ds_click_modes {
        let mode_row =
            |id_prefix: &'static str, title: &'static str, current: ClickMode, left: bool| {
                mouse_row(
                    title,
                    None::<&str>,
                    row().gap(6.0).children(
                        [
                            ClickMode::Standard,
                            ClickMode::IntentTrigger,
                            ClickMode::UltraClick,
                        ]
                        .into_iter()
                        .map(move |m| {
                            chip_button(format!("{id_prefix}-{m:?}"), m.label(), current == m)
                                .on_press(move |s: &mut AppState| {
                                    s.pc.mouse_ui.apply_edit(
                                        Instant::now(),
                                        WritePersistence::VolatileRam,
                                        |c| {
                                            if left {
                                                c.click_mode_left = m;
                                            } else {
                                                c.click_mode_right = m;
                                            }
                                        },
                                    );
                                })
                        }),
                    ),
                )
            };
        col_b.push(device_panel(
            "TMR-DS Click Engine",
            vec![
                mode_row("mouse-tmr-l", "Left click mode", cfg.click_mode_left, true),
                mode_row(
                    "mouse-tmr-r",
                    "Right click mode",
                    cfg.click_mode_right,
                    false,
                ),
            ],
        ));
    }

    if caps.has_receiver_led {
        let led_chips =
            row()
                .w_full()
                .flex_wrap()
                .gap(6.0)
                .children(RECEIVER_LED_CHOICES.iter().map(|&(hex, name)| {
                    let active_led = cfg.receiver_led_hex.eq_ignore_ascii_case(hex);
                    chip_button(format!("mouse-led-{name}"), name, active_led).on_press(
                        move |s: &mut AppState| {
                            s.pc.mouse_ui.apply_edit(
                                Instant::now(),
                                WritePersistence::VolatileRam,
                                |c| c.receiver_led_hex = hex.into(),
                            );
                        },
                    )
                }));
        col_b.push(device_panel(
            "Wireless receiver",
            vec![
                col()
                    .w_full()
                    .gap(8.0)
                    .child(text("Receiver LED color").font(type_body()).color(FG))
                    .child(led_chips),
            ],
        ));
    }

    let mut auto_rows = vec![mouse_row(
        "Write-loop hysteresis & anti-cheat policy",
        Some(
            "500 ms debounce · max 1 volatile RAM switch per 2 s · onboard-only bindings for online and unknown games.",
        ),
        mouse_badge(
            format!(
                "{} writes · {} skipped",
                ui.total_writes, ui.skipped_redundant_writes
            ),
            BadgeTone::Good,
        ),
    )];

    for rule in &ui.game_rules {
        let gid = rule.game_id.clone();
        let gid_remove = rule.game_id.clone();
        let is_active = ui.mock_active_game.as_deref() == Some(rule.game_id.as_str());
        let summary = format!(
            "{} · {} DPI · {}",
            rule.profile_name,
            rule.config.active_dpi(),
            format_hz(rule.config.polling_hz)
        );
        auto_rows.push(mouse_row(
            rule.game_name.clone(),
            Some(summary),
            row()
                .gap(6.0)
                .child(
                    chip_button(
                        format!("mouse-rule-test-{}", rule.id),
                        if is_active { "Active" } else { "Switch" },
                        is_active,
                    )
                    .on_press(move |s: &mut AppState| {
                        s.pc.mouse_ui.simulate_game_focus(Some(&gid));
                    }),
                )
                .child(
                    device_btn(format!("mouse-rule-del-{}", rule.id), "Remove", false).on_press(
                        move |s: &mut AppState| {
                            s.pc.mouse_ui.remove_game_rule(&gid_remove);
                            s.persist_mouse_profiles_to_host();
                        },
                    ),
                ),
        ));
    }

    auto_rows.push(
        row()
            .w_full()
            .justify_between()
            .center_y()
            .gap(8.0)
            .child(
                device_btn("mouse-auto-desktop", "Return to Desktop", false).on_press(
                    |s: &mut AppState| {
                        s.pc.mouse_ui.simulate_game_focus(None);
                    },
                ),
            )
            .child(
                device_btn("mouse-rule-add-apex", "Link Apex Legends", false).on_press(
                    |s: &mut AppState| {
                        s.pc.mouse_ui
                            .add_current_as_game_rule("steam:1172470", "Apex Legends");
                        s.persist_mouse_profiles_to_host();
                    },
                ),
            ),
    );
    col_b.push(device_panel("Per-game auto-switch", auto_rows));

    let mut diag_rows = Vec::new();
    let port_tone = match active.usb_topology.port_class {
        UsbPortClass::CpuDirect => BadgeTone::Good,
        UsbPortClass::Chipset => BadgeTone::Warn,
        UsbPortClass::ExternalHub => BadgeTone::Bad,
        UsbPortClass::Unknown => BadgeTone::Neutral,
    };
    let port_label = match active.usb_topology.port_class {
        UsbPortClass::CpuDirect => "CPU-Direct USB",
        UsbPortClass::Chipset => "Chipset USB",
        UsbPortClass::ExternalHub => "External Hub",
        UsbPortClass::Unknown => "USB Port",
    };
    diag_rows.push(mouse_row(
        "USB controller topology",
        active
            .usb_topology
            .warning
            .clone()
            .or_else(|| active.usb_topology.controller_id.clone()),
        mouse_badge(port_label, port_tone),
    ));

    if let Some(ref poll) = ui.last_polling {
        let tone = match poll.health {
            PollingHealth::Good => BadgeTone::Good,
            PollingHealth::Warn => BadgeTone::Warn,
            PollingHealth::Bad => BadgeTone::Bad,
        };
        let desc = format!(
            "{:.0} Hz measured · p95 {:.0} µs · {:.1}% jitter ({} late)",
            poll.measured_hz, poll.p95_interval_us, poll.jitter_pct, poll.late_packets
        );
        diag_rows.push(mouse_row(
            "Polling stability",
            Some(desc),
            row()
                .gap(8.0)
                .center_y()
                .child(mouse_badge(format!("{:.1}% jitter", poll.jitter_pct), tone))
                .child(
                    device_btn("mouse-poll-remeasure", "Measure", false).on_press(
                        |s: &mut AppState| {
                            s.pc.mouse_ui.run_mock_polling_sample();
                        },
                    ),
                ),
        ));
    }

    if let Some(ref plan) = ui.irq_plan {
        diag_rows.push(mouse_row(
            "USB xHCI IRQ core affinity",
            Some(plan.reason.clone()),
            mouse_badge(
                format!(
                    "CPU {} (0x{:X})",
                    plan.recommended_logical_cpu, plan.affinity_mask
                ),
                BadgeTone::Good,
            ),
        ));
    }

    if let Some(suite) = active.vendor.retired_vendor_suite() {
        diag_rows.push(mouse_row(
            format!("{suite} not needed"),
            Some("Exo drives this mouse directly in native Rust with zero background daemon."),
            mouse_badge("Native MIT Rust", BadgeTone::Good),
        ));
    }

    col_b.push(device_panel("Hardware health & diagnostics", diag_rows));

    let resolved_status = if active.is_mock {
        match active.battery_pct {
            Some(b) => format!("Wireless · {b}%"),
            None => "Wired".into(),
        }
    } else {
        status_line
    };

    device_page_shell_columns_no_rescan(
        DevicePageHeader {
            key: "room-devices-mouse",
            name: if active.is_mock {
                active.model_name.clone()
            } else {
                display_name
            },
            status: resolved_status,
            media,
            stats,
            credit: None,
        },
        col_a,
        col_b,
        |s: &mut AppState| s.close_mouse_page(),
    )
}
