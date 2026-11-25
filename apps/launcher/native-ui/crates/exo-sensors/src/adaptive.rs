use std::time::{Duration, Instant};

use crate::gpu::{GpuSnapshot, discover_primary_gpu};
use crate::nvme_smart::{NvmeDriveHealth, read_all_nvme_drives};
use crate::pawnio_imc::{PawnIoStatus, probe_pawnio_readonly};
use crate::sentinel::{SentinelFinding, evaluate_sentinel};
use crate::smbios::{SmbiosReport, read_live_smbios};
use crate::whea::{StabilityEventSummary, query_stability_events};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PollMode {
    Hidden,
    GamingBackground,
    RoomActive,
}

impl PollMode {
    pub fn interval(self) -> Option<Duration> {
        match self {
            Self::Hidden => None,
            Self::GamingBackground => Some(Duration::from_secs(10)),
            Self::RoomActive => Some(Duration::from_secs(1)),
        }
    }

    pub fn resolve(room_visible: bool, any_game_running: bool) -> Self {
        if room_visible {
            Self::RoomActive
        } else if any_game_running {
            Self::GamingBackground
        } else {
            Self::Hidden
        }
    }
}

#[derive(Debug, Clone)]
pub struct HardwareAudit {
    pub audit_duration_ms: f64,
    pub smbios: Option<SmbiosReport>,
    pub gpu: Option<GpuSnapshot>,
    pub drives: Vec<NvmeDriveHealth>,
    pub stability_24h: StabilityEventSummary,
    pub pawnio: PawnIoStatus,
    pub findings: Vec<SentinelFinding>,
}

pub fn run_full_hardware_audit() -> HardwareAudit {
    let t0 = Instant::now();
    let smbios = read_live_smbios();
    let gpu = discover_primary_gpu().and_then(|mut b| b.read());
    let drives = read_all_nvme_drives();
    let stability_24h = query_stability_events(24);
    let pawnio = probe_pawnio_readonly();
    let findings = evaluate_sentinel(smbios.as_ref(), gpu.as_ref(), &drives, &stability_24h);
    let audit_duration_ms = t0.elapsed().as_secs_f64() * 1000.0;

    HardwareAudit {
        audit_duration_ms,
        smbios,
        gpu,
        drives,
        stability_24h,
        pawnio,
        findings,
    }
}
