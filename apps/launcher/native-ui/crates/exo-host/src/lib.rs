pub mod backend;
pub mod router;
pub mod shadow;

pub use backend::{
    AutopilotStatusPayload, DisplayMonitorsPayload, GpuOverviewPayload, HostBackend,
    LiveReadOnlyHostBackend, MockHostBackend, NetLatencyStatusPayload, PowerGuardStatusPayload,
    SensorsAuditPayload, ThermalStatusPayload, classify_process_protection,
    enforce_process_policy_on_payload,
};
pub use router::{
    HostRouter, PHASE2_ROUTES, PHASE2_SLICE2_READONLY_ROUTES, RouteError, RouteOutcome,
    is_readonly_hardware_call,
};
pub use shadow::{
    ShadowComparison, ShadowFamily, ShadowFamilyStats, ShadowHarness, ShadowParityDiff,
    classify_readonly_shadow, compare_shadow_response,
};
