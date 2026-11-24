use serde::{Deserialize, Serialize};

pub const VENDOR_NVIDIA: u32 = 0x10DE;
pub const VENDOR_AMD: u32 = 0x1002;
pub const VENDOR_INTEL: u32 = 0x8086;

pub const NVAPI_ID_INITIALIZE: u32 = 0x0150_E828;
pub const NVAPI_ID_UNLOAD: u32 = 0xD22B_DD7E;
pub const NVAPI_ID_GET_DRIVER_AND_BRANCH: u32 = 0x2926_AAAD;
pub const NVAPI_ID_D3D12_IS_NV_SHADER_EXTN_SUPPORTED: u32 = 0x13D6_8892;

pub const SYNTHETIC_DRIVER_VERSION: u32 = 57200;
pub const SYNTHETIC_DRIVER_BRANCH: &str = "r572_00";

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FakeNvapiDecision {
    pub active: bool,
    pub real_vendor_id: u32,
    pub reported_vendor_id: u32,
    pub reported_driver_version: u32,
    pub reason: String,
}

pub fn evaluate_shim(
    real_vendor_id: u32,
    requested: bool,
    bridge_permitted: bool,
) -> FakeNvapiDecision {
    if !bridge_permitted {
        return FakeNvapiDecision {
            active: false,
            real_vendor_id,
            reported_vendor_id: real_vendor_id,
            reported_driver_version: 0,
            reason: "Blocked by anti-cheat class policy; shim stays inactive.".into(),
        };
    }
    if real_vendor_id == VENDOR_NVIDIA {
        return FakeNvapiDecision {
            active: false,
            real_vendor_id,
            reported_vendor_id: VENDOR_NVIDIA,
            reported_driver_version: 0,
            reason: "Real NVIDIA GPU detected; pass through native nvapi64.dll untouched.".into(),
        };
    }
    if !requested {
        return FakeNvapiDecision {
            active: false,
            real_vendor_id,
            reported_vendor_id: real_vendor_id,
            reported_driver_version: 0,
            reason: "FakeNVAPI capability shim disabled in bridge config.".into(),
        };
    }
    FakeNvapiDecision {
        active: true,
        real_vendor_id,
        reported_vendor_id: VENDOR_NVIDIA,
        reported_driver_version: SYNTHETIC_DRIVER_VERSION,
        reason: "Non-NVIDIA GPU in permitted offline/opt-in session; exposing minimal capability queries for DLSS-G UI unlock.".into(),
    }
}

pub fn query_synthetic_interface_supported(interface_id: u32) -> bool {
    matches!(
        interface_id,
        NVAPI_ID_INITIALIZE
            | NVAPI_ID_UNLOAD
            | NVAPI_ID_GET_DRIVER_AND_BRANCH
            | NVAPI_ID_D3D12_IS_NV_SHADER_EXTN_SUPPORTED
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shim_bypasses_real_nvidia_and_activates_only_for_permitted_non_nvidia() {
        let nv = evaluate_shim(VENDOR_NVIDIA, true, true);
        assert!(!nv.active);
        assert_eq!(nv.reported_vendor_id, VENDOR_NVIDIA);

        let amd_ok = evaluate_shim(VENDOR_AMD, true, true);
        assert!(amd_ok.active);
        assert_eq!(amd_ok.reported_vendor_id, VENDOR_NVIDIA);
        assert_eq!(amd_ok.reported_driver_version, SYNTHETIC_DRIVER_VERSION);

        let intel_blocked = evaluate_shim(VENDOR_INTEL, true, false);
        assert!(!intel_blocked.active);
        assert_eq!(intel_blocked.reported_vendor_id, VENDOR_INTEL);

        assert!(query_synthetic_interface_supported(NVAPI_ID_INITIALIZE));
        assert!(query_synthetic_interface_supported(
            NVAPI_ID_GET_DRIVER_AND_BRANCH
        ));
        assert!(!query_synthetic_interface_supported(0xDEAD_BEEF));
    }
}
