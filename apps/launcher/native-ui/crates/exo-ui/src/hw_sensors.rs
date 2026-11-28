use std::sync::{Arc, Mutex};
use std::thread;

use exo_gpu::{GpuControlReadonlyOverview, read_gpu_control_overview};
use exo_kit::{
    BadgeTone, FG, bad, fg_faint, fg_muted, float_line, good as good_color, icon_check,
    status_badge, type_control, type_section, type_small, warn,
};
use exo_sensors::adaptive::{HardwareAudit, PollMode, run_full_hardware_audit};
use exo_sensors::sentinel::{SentinelFinding, SentinelKind, SentinelSeverity};
use rune::{El, col, el, row, text};

use crate::pc_menus::menu_row;
use crate::state::{AppState, Room};

#[derive(Default)]
struct AuditSlot {
    audit: Option<HardwareAudit>,
    gpu_overview: Option<GpuControlReadonlyOverview>,
    busy: bool,
}

pub struct HwSensorsUiState {
    pub audit: Option<HardwareAudit>,
    pub gpu_overview: Option<GpuControlReadonlyOverview>,
    pub busy: bool,
    pub last_poll_at: f64,
    slot: Arc<Mutex<AuditSlot>>,
}

impl Default for HwSensorsUiState {
    fn default() -> Self {
        Self {
            audit: None,
            gpu_overview: None,
            busy: false,
            last_poll_at: -60.0,
            slot: Arc::new(Mutex::new(AuditSlot::default())),
        }
    }
}

impl AppState {
    pub fn tick_hw_sensors(&mut self, now: f64) -> bool {
        let mut changed = false;
        if let Ok(mut g) = self.pc.hw_sensors_ui.slot.lock() {
            if let Some(a) = g.audit.take() {
                self.pc.hw_sensors_ui.audit = Some(a);
                changed = true;
            }
            if let Some(ov) = g.gpu_overview.take() {
                self.pc.hw_sensors_ui.gpu_overview = Some(ov);
                changed = true;
            }
            if self.pc.hw_sensors_ui.busy != g.busy {
                self.pc.hw_sensors_ui.busy = g.busy;
                changed = true;
            }
        }

        let any_game_running = self.games.iter().any(|g| g.is_running);
        let mode = PollMode::resolve(self.room == Room::ExoAi, any_game_running);
        if mode == PollMode::Hidden {
            return changed;
        }
        if self.pc.hw_sensors_ui.audit.is_none()
            && !self.pc.hw_sensors_ui.busy
            && now - self.pc.hw_sensors_ui.last_poll_at >= 2.0
        {
            self.pc.hw_sensors_ui.last_poll_at = now;
            self.refresh_hw_sensors_async();
            return true;
        }
        changed
    }

    pub fn refresh_hw_sensors_async(&mut self) {
        let slot = Arc::clone(&self.pc.hw_sensors_ui.slot);
        if let Ok(mut g) = slot.lock() {
            if g.busy {
                return;
            }
            g.busy = true;
        }
        self.pc.hw_sensors_ui.busy = true;
        let _ = thread::Builder::new()
            .name("exo-hw-audit".into())
            .spawn(move || {
                let audit = run_full_hardware_audit();
                let gpu_overview = read_gpu_control_overview();
                if let Ok(mut g) = slot.lock() {
                    g.audit = Some(audit);
                    g.gpu_overview = Some(gpu_overview);
                    g.busy = false;
                }
            });
    }
}

fn health_check_cell(
    key: &'static str,
    label: &'static str,
    finding: Option<&SentinelFinding>,
    good: String,
) -> El {
    let (mark, line, tone) = match finding {
        Some(f) => {
            let tone = if f.severity == SentinelSeverity::Critical {
                bad()
            } else {
                warn()
            };
            let hint = if f.fix_hint.is_empty() {
                f.title.clone()
            } else {
                format!("{} · {}", f.title, f.fix_hint)
            };
            (el().size(7.0, 7.0).radius(3.5).fill(tone), hint, tone)
        }
        None => (icon_check(13.0, good_color()), good, fg_muted()),
    };
    row()
        .key(key)
        .grow(1.0)
        .min_w(0.0)
        .py(6.0)
        .gap(9.0)
        .items_start()
        .child(el().size(14.0, 16.0).center().child(mark))
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(1.0)
                .child(text(label).font(type_control()).color(FG))
                .child(text(line).font(type_small()).color(tone).wrap()),
        )
}

pub(crate) fn health_view(s: &AppState) -> El {
    let hw = &s.pc.hw_sensors_ui;
    let Some(ref a) = hw.audit else {
        return row()
            .key("exo-ai-health-checking")
            .w_full()
            .pt(10.0)
            .gap(8.0)
            .center_y()
            .child(el().size(6.0, 6.0).radius(3.0).fill(fg_faint()))
            .child(
                text("Checking memory, graphics link, drives and stability…")
                    .font(type_small())
                    .color(fg_muted()),
            );
    };
    let find = |kinds: &[SentinelKind]| {
        a.findings
            .iter()
            .filter(|f| kinds.contains(&f.kind))
            .max_by_key(|f| f.severity)
    };

    let memory_good = a
        .smbios
        .as_ref()
        .map(|sb| {
            let speed = sb
                .dimms
                .first()
                .filter(|d| d.configured_mts > 0)
                .map(|d| format!(" at {} MT/s", d.configured_mts))
                .unwrap_or_default();
            let channel = if sb.is_single_channel_desktop() {
                "single channel"
            } else {
                "dual channel"
            };
            format!("{} GB{speed} · {channel}", sb.total_ram_gb())
        })
        .unwrap_or_else(|| "Running at its rated speed".into());
    let link_good = a
        .gpu
        .as_ref()
        .and_then(|g| match (g.pcie_gen_current, g.pcie_width_current) {
            (Some(pcie_gen), Some(w)) => Some(format!("Full speed · PCIe {pcie_gen}.0 x{w}")),
            _ => None,
        })
        .unwrap_or_else(|| "Full speed".into());
    let drive_good = a
        .drives
        .first()
        .map(|d| {
            let mut parts = Vec::new();
            if let Some(t) = d.temp_c {
                parts.push(format!("{t} °C"));
            }
            if let Some(l) = d.life_used_pct {
                parts.push(format!("{l}% worn"));
            }
            if parts.is_empty() {
                "Healthy".to_string()
            } else {
                format!("Healthy · {}", parts.join(" · "))
            }
        })
        .unwrap_or_else(|| "Healthy".into());
    let stable_good = "No crashes or driver resets today".to_string();

    let issues = a.findings.len();
    let summary = if issues == 0 {
        text("All good").font(type_small()).color(good_color())
    } else {
        text(if issues == 1 {
            "1 thing to check".to_string()
        } else {
            format!("{issues} things to check")
        })
        .font(type_small())
        .color(warn())
    };

    col()
        .key("exo-ai-health")
        .w_full()
        .pt(6.0)
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(
            row()
                .w_full()
                .pt(10.0)
                .pb(2.0)
                .justify_between()
                .center_y()
                .child(text("Hardware check").font(type_section()).color(FG))
                .child(summary),
        )
        .child(
            row()
                .w_full()
                .gap(16.0)
                .items_start()
                .child(health_check_cell(
                    "exo-ai-health-memory",
                    "Memory",
                    find(&[SentinelKind::XmpExpoOff, SentinelKind::SingleChannelRam]),
                    memory_good,
                ))
                .child(health_check_cell(
                    "exo-ai-health-link",
                    "Graphics link",
                    find(&[SentinelKind::PcieLinkDrop]),
                    link_good,
                )),
        )
        .child(
            row()
                .w_full()
                .gap(16.0)
                .items_start()
                .child(health_check_cell(
                    "exo-ai-health-drives",
                    "Drives",
                    find(&[
                        SentinelKind::NvmeThrottling,
                        SentinelKind::NvmeCriticalHealth,
                    ]),
                    drive_good,
                ))
                .child(health_check_cell(
                    "exo-ai-health-stability",
                    "Stability",
                    find(&[SentinelKind::WheaErrors, SentinelKind::GpuTdrBurst]),
                    stable_good,
                )),
        )
}

pub(crate) fn graphics_drs_view(s: &AppState) -> El {
    let mut body = col().w_full();
    let Some(ref ov) = s.pc.hw_sensors_ui.gpu_overview else {
        return body.child(menu_row(
            "Driver profile telemetry",
            Some("Read-only NVAPI DRS / ADLX / IGCL snapshot".into()),
            None,
            status_badge("Idle".into(), BadgeTone::Neutral),
        ));
    };

    if let Some(ref drs) = ov.nvidia_drs {
        body = body.child(menu_row(
            "NVAPI DRS read latency",
            Some("Direct Rust FFI · 100% read-only".into()),
            None,
            status_badge(format!("{:.2} ms", drs.read_latency_ms), BadgeTone::Good),
        ));
        for item in drs.settings.iter().take(5) {
            body = body.child(menu_row(
                item.label,
                None,
                None,
                status_badge(
                    item.display_value.clone(),
                    if item.is_user_override {
                        BadgeTone::Good
                    } else {
                        BadgeTone::Neutral
                    },
                ),
            ));
        }
    }
    body
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_sensors::nvme_smart::NvmeDriveHealth;
    use exo_sensors::pawnio_imc::PawnIoStatus;
    use exo_sensors::sentinel::evaluate_sentinel;
    use exo_sensors::smbios::{DimmInfo, SmbiosReport};
    use exo_sensors::whea::StabilityEventSummary;

    #[test]
    fn hw_sensors_adaptive_poll_mode_is_zero_when_hidden() {
        assert_eq!(PollMode::resolve(false, false), PollMode::Hidden);
        assert_eq!(PollMode::resolve(true, false), PollMode::RoomActive);
        assert_eq!(PollMode::resolve(false, true), PollMode::GamingBackground);
    }

    #[test]
    fn hw_sensors_card_renders_sentinel_findings_and_drs_readout() {
        let mut s = AppState::with_demo_games();
        let smbios = SmbiosReport {
            dimms: vec![DimmInfo {
                size_mb: 16384,
                speed_mts: 6000,
                configured_mts: 4800,
                form_factor: 0x09,
                memory_type: "DDR5".into(),
                device_locator: "DIMM_A2".into(),
                bank_locator: "Channel A".into(),
            }],
            ..Default::default()
        };
        let drives = vec![NvmeDriveHealth {
            disk_number: 0,
            model: "Samsung 990 PRO".into(),
            is_nvme: true,
            temp_c: Some(44),
            warn_temp_c: Some(75),
            critical_temp_c: Some(85),
            life_used_pct: Some(2),
            spare_pct: Some(100),
            spare_threshold_pct: Some(10),
            critical_warning: 0,
            media_errors: 0,
            unsafe_shutdowns: 0,
            throttle_events: 0,
            throttle_seconds: 0,
        }];
        let stab = StabilityEventSummary::default();
        let findings = evaluate_sentinel(Some(&smbios), None, &drives, &stab);
        s.pc.hw_sensors_ui.audit = Some(HardwareAudit {
            audit_duration_ms: 14.2,
            smbios: Some(smbios),
            gpu: None,
            drives,
            stability_24h: stab,
            pawnio: PawnIoStatus {
                installed: false,
                dll_loadable: false,
                note: "Optional PawnIO not installed".into(),
                timings: None,
            },
            findings,
        });
        let _ = health_view(&s);
        let _ = graphics_drs_view(&s);
    }
}
