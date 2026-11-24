pub mod cap_planner;
pub mod etw;
pub mod hud;
pub mod metrics;
pub mod replay;
pub mod session;
pub mod state_machine;
pub mod stutter;

pub use cap_planner::{
    CapTier, FrameCapPlan, base_cap_for_fg, plan_frame_cap, refresh_locked_caps, vrr_cap,
};
pub use etw::{
    DEFAULT_SESSION_NAME, LiveEtwSession, ProcessResourceUsage, TraceConfig, current_process_usage,
    find_pids_by_name, foreground_pid_and_monitor, qpc_freq, qpc_now, stop_session_by_name,
};
pub use hud::{
    HUD_HEIGHT_PT, HUD_MARGIN_PT, HUD_PADDING_X_PT, HudExtras, HudItem, HudLine, HudPosition,
    HudState, estimate_pill_width_pt, format_hud_line, oled_shift_for_tick, same_parts,
};
pub use metrics::{FrameMetrics, FrameSample, PresentMode, RollingMetrics, percentile_sorted};
pub use replay::{
    OfflineAccuracyReport, ReplayScenarioReport, SyntheticEvent, run_offline_accuracy_suite,
};
pub use session::{SessionComparison, SessionSummary, compare_sessions};
pub use state_machine::{
    DXGI_PRESENT_TEST_FLAG, KERNEL_FALLBACK_SECS, PresentEventKind, PresentStateMachine,
    RawPresentEvent, classify_event, is_present_test, parse_dxgi_present_fields,
    wants_kernel_fallback,
};
pub use stutter::{AppLoad, StutterAnalyzer, StutterCause, StutterFinding, SystemSecond};
