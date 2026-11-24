use crate::backend::FanDomain;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackendCapability {
    pub domain: FanDomain,
    pub api_name: &'static str,
    pub driver_dependency: &'static str,
    pub anti_cheat_safe: bool,
    pub requires_opt_in: bool,
}

pub fn supported_backends() -> Vec<BackendCapability> {
    #[allow(unused_mut)]
    let mut caps = vec![
        BackendCapability {
            domain: FanDomain::GpuNvidia,
            api_name: "NVML (nvml.dll)",
            driver_dependency: "Official NVIDIA driver",
            anti_cheat_safe: true,
            requires_opt_in: false,
        },
        BackendCapability {
            domain: FanDomain::GpuAmd,
            api_name: "ADLX (amdadlx64.dll)",
            driver_dependency: "Official AMD Adrenalin driver",
            anti_cheat_safe: true,
            requires_opt_in: false,
        },
        BackendCapability {
            domain: FanDomain::GpuIntel,
            api_name: "IGCL (ControlLib.dll)",
            driver_dependency: "Official Intel Arc driver",
            anti_cheat_safe: true,
            requires_opt_in: false,
        },
        BackendCapability {
            domain: FanDomain::UsbAio,
            api_name: "Standard USB HID (Corsair/NZXT/Aquacomputer)",
            driver_dependency: "Windows inbox HID",
            anti_cheat_safe: true,
            requires_opt_in: false,
        },
    ];
    #[cfg(feature = "superio-pawnio")]
    caps.push(BackendCapability {
        domain: FanDomain::SuperIo,
        api_name: "WHQL-signed PawnIO SuperIO (NCT67xx/IT86xx)",
        driver_dependency: "Opt-in signed PawnIO",
        anti_cheat_safe: true,
        requires_opt_in: true,
    });
    caps
}
