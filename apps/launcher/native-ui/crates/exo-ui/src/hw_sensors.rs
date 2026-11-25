use std::sync::{Arc, Mutex};
use std::thread;

use exo_gpu::{GpuControlReadonlyOverview, read_gpu_control_overview};
use exo_kit::{
    BG, BadgeTone, FG, RADIUS_PANEL, bad, fg_muted, float_line, status_badge, type_control,
    type_small, type_title, warn,
};
use exo_sensors::adaptive::{HardwareAudit, PollMode, run_full_hardware_audit};
use exo_sensors::sentinel::SentinelSeverity;
use rune::{El, col, row, text};

use crate::pc_menus::{menu_btn, menu_row};
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

pub(crate) fn card_view(s: &AppState) -> El {
    let hw = &s.pc.hw_sensors_ui;

    let (badge_word, badge_tone) = match hw.audit.as_ref() {
        None if hw.busy => ("Scanning".into(), BadgeTone::Neutral),
        None => ("Standby".into(), BadgeTone::Neutral),
        Some(a) if a.findings.is_empty() => ("All clear".into(), BadgeTone::Good),
        Some(a)
            if a.findings
                .iter()
                .any(|f| f.severity == SentinelSeverity::Critical) =>
        {
            (format!("{} critical", a.findings.len()), BadgeTone::Bad)
        }
        Some(a) => (format!("{} check(s)", a.findings.len()), BadgeTone::Warn),
    };

    let audit_meta = hw
        .audit
        .as_ref()
        .map(|a| format!("Audit {:.0} ms · read-only", a.audit_duration_ms))
        .unwrap_or_else(|| "0 Hz while hidden".into());

    let header = row()
        .w_full()
        .justify_between()
        .center_y()
        .gap(10.0)
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    text("Hardware sentinel")
                        .key("exo-ai-sentinel-title")
                        .font(type_title())
                        .color(FG),
                )
                .child(status_badge(badge_word, badge_tone))
                .child(text(audit_meta).font(type_small()).color(fg_muted())),
        )
        .child(
            menu_btn(
                "hw-sentinel-refresh".into(),
                "Refresh",
                false,
                false,
                hw.busy,
            )
            .on_press(|s: &mut AppState| s.refresh_hw_sensors_async()),
        );

    let mut left = col().grow(1.15).min_w(0.0).gap(5.0);
    let mut right = col().grow(1.0).min_w(0.0).gap(5.0);

    if let Some(ref a) = hw.audit {
        if let Some(ref sb) = a.smbios {
            let speed_txt = sb
                .dimms
                .first()
                .map(|d| {
                    if d.configured_mts > 0 {
                        format!("{} {} MT/s", d.memory_type, d.configured_mts)
                    } else {
                        d.memory_type.clone()
                    }
                })
                .unwrap_or_else(|| "RAM".into());
            let ch_txt = if sb.is_single_channel_desktop() {
                "Single-channel"
            } else {
                "Dual-channel"
            };
            left = left.child(
                text(format!(
                    "Memory: {} GB · {} · {ch_txt} ({} DIMM)",
                    sb.total_ram_gb(),
                    speed_txt,
                    sb.dimms.len()
                ))
                .font(type_small())
                .color(FG),
            );
        }
        if let Some(ref g) = a.gpu {
            let pcie_txt = match (g.pcie_gen_current, g.pcie_width_current, g.pcie_width_max) {
                (Some(pcie_gen), Some(cw), Some(mw)) => format!("PCIe Gen{pcie_gen} x{cw}/x{mw}"),
                _ => "PCIe n/a".into(),
            };
            let pwr_txt = g
                .board_power_w
                .map(|w| format!("{w:.0} W"))
                .unwrap_or_else(|| "n/a".into());
            let tmp_txt = g
                .core_temp_c
                .map(|c| format!("{c:.0} °C"))
                .unwrap_or_else(|| "n/a".into());
            left = left.child(
                text(format!(
                    "GPU: {} · {pwr_txt} · {tmp_txt} · {pcie_txt}",
                    g.name
                ))
                .font(type_small())
                .color(fg_muted())
                .truncate(),
            );
        }
        if let Some(d) = a.drives.first() {
            let t = d
                .temp_c
                .map(|c| format!("{c} °C"))
                .unwrap_or_else(|| "n/a".into());
            let life = d
                .life_used_pct
                .map(|l| format!("{}% wear", l))
                .unwrap_or_else(|| "SMART ok".into());
            left = left.child(
                text(format!(
                    "NVMe: {} · {t} · {life} · {} throttle events",
                    d.model, d.throttle_events
                ))
                .font(type_small())
                .color(fg_muted())
                .truncate(),
            );
        }
        left = left.child(
            text(format!(
                "24h stability: {} WHEA · {} GPU TDR · {}",
                a.stability_24h.whea_errors, a.stability_24h.gpu_tdr_resets, a.pawnio.note
            ))
            .font(type_small())
            .color(fg_muted())
            .wrap(),
        );

        if a.findings.is_empty() {
            right = right.child(
                text("No hardware bottlenecks detected (XMP/EXPO, RAM channel, PCIe link, NVMe thermals, and WHEA all clear).")
                    .font(type_small())
                    .color(fg_muted())
                    .wrap(),
            );
        } else {
            for f in a.findings.iter().take(3) {
                let col_tone = if f.severity == SentinelSeverity::Critical {
                    bad()
                } else {
                    warn()
                };
                right = right.child(
                    col()
                        .w_full()
                        .gap(1.0)
                        .child(text(f.title.clone()).font(type_control()).color(col_tone))
                        .child(
                            text(format!("{} {}", f.detail, f.fix_hint))
                                .font(type_small())
                                .color(fg_muted())
                                .wrap(),
                        ),
                );
            }
        }
    } else {
        left = left.child(
            text("Checks SMBIOS RAM speed & channels, GPU PCIe link width, NVMe SMART throttling, and 24h WHEA/TDR logs (100% read-only, zero WinRing0).")
                .font(type_small())
                .color(fg_muted())
                .wrap(),
        );
    }

    col()
        .key("exo-ai-sentinel-card")
        .w_full()
        .h_full()
        .pt(16.0)
        .px(24.0)
        .pb(16.0)
        .gap(10.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border(1.0, float_line())
        .child(header)
        .child(
            row()
                .w_full()
                .items_start()
                .gap(16.0)
                .child(left)
                .child(right),
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
        let _ = card_view(&s);
        let _ = graphics_drs_view(&s);
    }
}
