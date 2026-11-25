pub mod backend;
pub mod router;

pub use backend::{
    AutopilotStatusPayload, DisplayMonitorsPayload, GpuOverviewPayload, HostBackend,
    LiveReadOnlyHostBackend, MockHostBackend, NetLatencyStatusPayload, PowerGuardStatusPayload,
    SensorsAuditPayload, ThermalStatusPayload,
};
pub use router::{HostRouter, PHASE2_ROUTES, RouteError, RouteOutcome};
