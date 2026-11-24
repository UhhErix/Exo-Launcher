use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuOverviewPayload {
    pub detected: bool,
    pub vendor: Option<String>,
    pub name: Option<String>,
    pub temp_c: Option<f64>,
    pub power_w: Option<f64>,
    pub boot_crash_paused: bool,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SensorsAuditPayload {
    pub audit_duration_ms: f64,
    pub drive_count: usize,
    pub finding_count: usize,
    pub whea_24h_count: u32,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DisplayMonitorsPayload {
    pub monitor_count: usize,
    pub models: Vec<String>,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ThermalStatusPayload {
    pub active_curves: usize,
    pub watchdog_armed: bool,
    pub bios_fallback_ready: bool,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PowerGuardStatusPayload {
    pub monitoring: bool,
    pub auto_actions_opt_in: bool,
    pub active_alerts: usize,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotStatusPayload {
    pub active: bool,
    pub active_game: Option<String>,
    pub timer_resolution_us: Option<u32>,
    pub read_only: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NetLatencyStatusPayload {
    pub dscp_qos_available: bool,
    pub packet_hooks_forbidden: bool,
    pub read_only: bool,
}

pub trait HostBackend: Send + Sync {
    fn read_gpu_overview(&self) -> GpuOverviewPayload;
    fn read_sensors_audit(&self) -> SensorsAuditPayload;
    fn read_display_monitors(&self) -> DisplayMonitorsPayload;
    fn read_thermal_status(&self) -> ThermalStatusPayload;
    fn read_power_guard_status(&self) -> PowerGuardStatusPayload;
    fn read_autopilot_status(&self) -> AutopilotStatusPayload;
    fn read_net_latency_status(&self) -> NetLatencyStatusPayload;
}

#[derive(Debug, Default, Clone, Copy)]
pub struct LiveReadOnlyHostBackend;

impl HostBackend for LiveReadOnlyHostBackend {
    fn read_gpu_overview(&self) -> GpuOverviewPayload {
        let ov = exo_gpu::read_gpu_control_overview();
        let (detected, vendor, name, temp_c, power_w) = match ov.gpu {
            Some(g) => (
                true,
                Some(format!("{:?}", g.vendor)),
                Some(g.name),
                g.core_temp_c,
                g.board_power_w,
            ),
            None => (false, None, None, None, None),
        };
        GpuOverviewPayload {
            detected,
            vendor,
            name,
            temp_c,
            power_w,
            boot_crash_paused: matches!(
                ov.boot_crash_guard,
                exo_gpu::undervolt::CrashGuardDecision::PauseAndKeepStock { .. }
            ),
            read_only: true,
        }
    }

    fn read_sensors_audit(&self) -> SensorsAuditPayload {
        let audit = exo_sensors::adaptive::run_full_hardware_audit();
        SensorsAuditPayload {
            audit_duration_ms: audit.audit_duration_ms,
            drive_count: audit.drives.len(),
            finding_count: audit.findings.len(),
            whea_24h_count: audit.stability_24h.whea_errors,
            read_only: true,
        }
    }

    fn read_display_monitors(&self) -> DisplayMonitorsPayload {
        let monitors = exo_display::read_live_monitors_readonly();
        let models = monitors
            .iter()
            .map(|m| {
                m.parsed
                    .monitor_name
                    .clone()
                    .unwrap_or_else(|| m.parsed.manufacturer.clone())
            })
            .collect();
        DisplayMonitorsPayload {
            monitor_count: monitors.len(),
            models,
            read_only: true,
        }
    }

    fn read_thermal_status(&self) -> ThermalStatusPayload {
        ThermalStatusPayload {
            active_curves: 0,
            watchdog_armed: false,
            bios_fallback_ready: true,
            read_only: true,
        }
    }

    fn read_power_guard_status(&self) -> PowerGuardStatusPayload {
        PowerGuardStatusPayload {
            monitoring: false,
            auto_actions_opt_in: false,
            active_alerts: 0,
            read_only: true,
        }
    }

    fn read_autopilot_status(&self) -> AutopilotStatusPayload {
        AutopilotStatusPayload {
            active: false,
            active_game: None,
            timer_resolution_us: None,
            read_only: true,
        }
    }

    fn read_net_latency_status(&self) -> NetLatencyStatusPayload {
        NetLatencyStatusPayload {
            dscp_qos_available: true,
            packet_hooks_forbidden: true,
            read_only: true,
        }
    }
}

#[derive(Debug, Clone)]
pub struct MockHostBackend {
    pub gpu: GpuOverviewPayload,
    pub sensors: SensorsAuditPayload,
    pub displays: DisplayMonitorsPayload,
    pub thermal: ThermalStatusPayload,
    pub power_guard: PowerGuardStatusPayload,
    pub autopilot: AutopilotStatusPayload,
    pub net_latency: NetLatencyStatusPayload,
}

impl Default for MockHostBackend {
    fn default() -> Self {
        Self {
            gpu: GpuOverviewPayload {
                detected: true,
                vendor: Some("Nvidia".into()),
                name: Some("Mock RTX GPU".into()),
                temp_c: Some(41.0),
                power_w: Some(18.5),
                boot_crash_paused: false,
                read_only: true,
            },
            sensors: SensorsAuditPayload {
                audit_duration_ms: 1.2,
                drive_count: 2,
                finding_count: 0,
                whea_24h_count: 0,
                read_only: true,
            },
            displays: DisplayMonitorsPayload {
                monitor_count: 1,
                models: vec!["Mock 240Hz OLED".into()],
                read_only: true,
            },
            thermal: ThermalStatusPayload {
                active_curves: 1,
                watchdog_armed: true,
                bios_fallback_ready: true,
                read_only: true,
            },
            power_guard: PowerGuardStatusPayload {
                monitoring: true,
                auto_actions_opt_in: false,
                active_alerts: 0,
                read_only: true,
            },
            autopilot: AutopilotStatusPayload {
                active: false,
                active_game: None,
                timer_resolution_us: Some(507),
                read_only: true,
            },
            net_latency: NetLatencyStatusPayload {
                dscp_qos_available: true,
                packet_hooks_forbidden: true,
                read_only: true,
            },
        }
    }
}

impl HostBackend for MockHostBackend {
    fn read_gpu_overview(&self) -> GpuOverviewPayload {
        self.gpu.clone()
    }

    fn read_sensors_audit(&self) -> SensorsAuditPayload {
        self.sensors.clone()
    }

    fn read_display_monitors(&self) -> DisplayMonitorsPayload {
        self.displays.clone()
    }

    fn read_thermal_status(&self) -> ThermalStatusPayload {
        self.thermal.clone()
    }

    fn read_power_guard_status(&self) -> PowerGuardStatusPayload {
        self.power_guard.clone()
    }

    fn read_autopilot_status(&self) -> AutopilotStatusPayload {
        self.autopilot.clone()
    }

    fn read_net_latency_status(&self) -> NetLatencyStatusPayload {
        self.net_latency.clone()
    }
}
