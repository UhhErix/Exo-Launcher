use crate::gpu::GpuSnapshot;
use crate::nvme_smart::NvmeDriveHealth;
use crate::smbios::SmbiosReport;
use crate::whea::StabilityEventSummary;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum SentinelSeverity {
    Info,
    Warning,
    Critical,
}

impl SentinelSeverity {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Info => "info",
            Self::Warning => "warning",
            Self::Critical => "critical",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SentinelKind {
    XmpExpoOff,
    SingleChannelRam,
    PcieLinkDrop,
    NvmeThrottling,
    NvmeCriticalHealth,
    WheaErrors,
    GpuTdrBurst,
}

impl SentinelKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::XmpExpoOff => "xmp-expo-off",
            Self::SingleChannelRam => "single-channel-ram",
            Self::PcieLinkDrop => "pcie-link-drop",
            Self::NvmeThrottling => "nvme-throttling",
            Self::NvmeCriticalHealth => "nvme-critical-health",
            Self::WheaErrors => "whea-errors",
            Self::GpuTdrBurst => "gpu-tdr-burst",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SentinelFinding {
    pub kind: SentinelKind,
    pub severity: SentinelSeverity,
    pub title: String,
    pub detail: String,
    pub fix_hint: String,
}

pub fn evaluate_sentinel(
    smbios: Option<&SmbiosReport>,
    gpu: Option<&GpuSnapshot>,
    drives: &[NvmeDriveHealth],
    stability: &StabilityEventSummary,
) -> Vec<SentinelFinding> {
    let mut findings = Vec::new();

    if let Some(sb) = smbios {
        if let Some((configured, rated)) = sb.xmp_expo_mismatch() {
            let mem_type = sb
                .dimms
                .first()
                .map(|d| d.memory_type.as_str())
                .unwrap_or("RAM");
            findings.push(SentinelFinding {
                kind: SentinelKind::XmpExpoOff,
                severity: SentinelSeverity::Warning,
                title: "XMP / EXPO is off".into(),
                detail: format!(
                    "Your {mem_type} is running at {configured} MT/s instead of its rated {rated} MT/s."
                ),
                fix_hint: "Enable EXPO or XMP in BIOS memory settings.".into(),
            });
        }

        if sb.is_single_channel_desktop() {
            let count = sb
                .dimms
                .iter()
                .filter(|d| d.size_mb > 0 && d.is_desktop_dimm())
                .count();
            findings.push(SentinelFinding {
                kind: SentinelKind::SingleChannelRam,
                severity: SentinelSeverity::Warning,
                title: "RAM is running in single-channel mode".into(),
                detail: if count == 1 {
                    "Only one desktop DIMM is populated, halving memory bandwidth.".into()
                } else {
                    format!(
                        "{count} DIMMs are populated on the same memory channel instead of dual-channel slots."
                    )
                },
                fix_hint: "Populate matched slots (typically A2 + B2) for dual-channel bandwidth."
                    .into(),
            });
        }
    }

    if let Some(g) = gpu {
        if let (Some(cur_w), Some(max_w)) = (g.pcie_width_current, g.pcie_width_max)
            && cur_w > 0
            && max_w >= 8
            && cur_w < max_w
        {
            findings.push(SentinelFinding {
                kind: SentinelKind::PcieLinkDrop,
                severity: SentinelSeverity::Warning,
                title: "GPU PCIe link width dropped".into(),
                detail: format!(
                    "{} is running at PCIe x{cur_w} instead of x{max_w}.",
                    g.name
                ),
                fix_hint: "Reseat the GPU in the top x16 slot or check M.2 PCIe lane sharing."
                    .into(),
            });
        } else if let (Some(cur_g), Some(max_g)) = (g.pcie_gen_current, g.pcie_gen_max)
            && cur_g > 0
            && max_g >= 4
            && cur_g < max_g
            && g.gpu_util_pct.is_some_and(|u| u >= 50)
        {
            findings.push(SentinelFinding {
                kind: SentinelKind::PcieLinkDrop,
                severity: SentinelSeverity::Info,
                title: "GPU PCIe link below max generation under load".into(),
                detail: format!(
                    "{} is running at PCIe Gen {cur_g} instead of Gen {max_g} under load.",
                    g.name
                ),
                fix_hint: "Check motherboard BIOS PCIe slot generation setting.".into(),
            });
        }
    }

    for drive in drives {
        if drive.critical_warning != 0 || drive.media_errors > 0 {
            findings.push(SentinelFinding {
                kind: SentinelKind::NvmeCriticalHealth,
                severity: SentinelSeverity::Critical,
                title: format!("{} SMART health warning", drive.model),
                detail: format!(
                    "Critical warning 0x{:02X}, media errors: {}, spare: {}%.",
                    drive.critical_warning,
                    drive.media_errors,
                    drive.spare_pct.unwrap_or(100)
                ),
                fix_hint: "Back up important data and inspect drive health.".into(),
            });
        }
        let warn_c = drive.warn_temp_c.unwrap_or(72);
        if drive.temp_c.is_some_and(|t| t >= warn_c) || drive.throttle_events > 0 {
            let temp_txt = drive
                .temp_c
                .map(|t| format!("{t} C"))
                .unwrap_or_else(|| "elevated".into());
            findings.push(SentinelFinding {
                kind: SentinelKind::NvmeThrottling,
                severity: if drive.temp_c.is_some_and(|t| t >= warn_c) {
                    SentinelSeverity::Warning
                } else {
                    SentinelSeverity::Info
                },
                title: format!("{} thermal throttling", drive.model),
                detail: format!(
                    "Current temp {temp_txt} (warn {warn_c} C), {} recorded throttle event(s) ({} s total).",
                    drive.throttle_events, drive.throttle_seconds
                ),
                fix_hint: "Verify motherboard M.2 heatsink contact and case airflow.".into(),
            });
        }
    }

    if stability.whea_errors > 0 {
        findings.push(SentinelFinding {
            kind: SentinelKind::WheaErrors,
            severity: if stability.whea_errors >= 3 {
                SentinelSeverity::Critical
            } else {
                SentinelSeverity::Warning
            },
            title: "WHEA hardware errors detected".into(),
            detail: format!(
                "{} WHEA-Logger hardware error(s) in the last {} hours.",
                stability.whea_errors, stability.window_hours
            ),
            fix_hint: "Check CPU Curve Optimizer / undervolt and RAM stability.".into(),
        });
    }

    if stability.gpu_tdr_resets > 0 {
        findings.push(SentinelFinding {
            kind: SentinelKind::GpuTdrBurst,
            severity: SentinelSeverity::Warning,
            title: "GPU driver resets (TDR) detected".into(),
            detail: format!(
                "{} Display 4101 driver reset(s) in the last {} hours.",
                stability.gpu_tdr_resets, stability.window_hours
            ),
            fix_hint: "Back off GPU overclock/undervolt or check driver advisories.".into(),
        });
    }

    findings
}
