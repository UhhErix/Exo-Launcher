use exo_display::edid::{parse_edid, strip_4096p_modes, synthetic_oled_edid_fixture};
use exo_display::edid_override::{DisplayInfo, MockDisplayBackend, SafeEdidOverrideSession};
use exo_display::mhc2_icc::{Mhc2Spec, build_mhc2_icc_profile, validate_mhc2_icc};
use exo_display::read_live_monitors_readonly;
use serde_json::json;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

fn main() {
    let t_edid = Instant::now();
    let live_monitors = read_live_monitors_readonly();
    let live_read_ms = t_edid.elapsed().as_secs_f64() * 1000.0;

    let t_icc = Instant::now();
    let spec = Mhc2Spec::true_black_hdr_22(0.0, 1000.0);
    let icc = build_mhc2_icc_profile("Exo OLED True Black 2.2", &spec);
    let icc_validation = validate_mhc2_icc(&icc).expect("valid MHC2 ICC");
    let icc_gen_ms = t_icc.elapsed().as_secs_f64() * 1000.0;

    let raw_fixture = synthetic_oled_edid_fixture(true);
    let scrubbed = strip_4096p_modes(&raw_fixture).expect("scrub 4096p");
    let scrubbed_parsed = parse_edid(&scrubbed).expect("re-parse scrubbed");

    let mock = MockDisplayBackend::new(vec![DisplayInfo {
        id: "mock:oled-0".into(),
        name: "Mock OLED Display".into(),
        is_internal_edp: false,
        max_pixel_clock_mhz: 1200,
    }]);
    let backend = Arc::new(Mutex::new(mock));
    let journal = Arc::new(Mutex::new(None));
    let t_wd = Instant::now();
    let mut sess = SafeEdidOverrideSession::apply_with_watchdog(
        Arc::clone(&backend),
        Arc::clone(&journal),
        "mock:oled-0",
        &scrubbed,
        Duration::from_millis(80),
    )
    .expect("apply mock override");
    let reverted = sess.wait_for_watchdog();
    let watchdog_ms = t_wd.elapsed().as_secs_f64() * 1000.0;

    let report = json!({
        "liveMonitorsReadOnlyCount": live_monitors.len(),
        "liveMonitorsReadOnlyMs": live_read_ms,
        "liveMonitors": live_monitors,
        "mhc2IccGenAndValidateMs": icc_gen_ms,
        "mhc2Validation": icc_validation,
        "edid4096pScrubValid": scrubbed_parsed.checksums_valid && !scrubbed_parsed.has_cinema_4096p,
        "mockWatchdogReverted": reverted,
        "mockWatchdogElapsedMs": watchdog_ms,
    });
    println!("{}", serde_json::to_string_pretty(&report).unwrap());
}
