use exo_host::backend::{LiveReadOnlyHostBackend, MockHostBackend, classify_process_protection};
use exo_host::router::{HostRouter, RouteOutcome, is_readonly_hardware_call};
use exo_host::shadow::{
    ShadowComparison, ShadowFamily, ShadowHarness, classify_readonly_shadow,
    compare_shadow_response,
};
use serde_json::{Value, json};
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::Duration;

fn load_golden() -> Value {
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let fixture = manifest_dir
        .join("tests")
        .join("fixtures")
        .join("phase2-slice2-golden.json");
    let raw = std::fs::read_to_string(&fixture)
        .unwrap_or_else(|e| panic!("failed to read {}: {e}", fixture.display()));
    serde_json::from_str(&raw).expect("valid slice 2 golden json")
}

fn recorded_backend_from_golden(golden: &Value) -> MockHostBackend {
    let mut map = HashMap::new();
    let families = golden["families"].as_object().expect("families object");
    for entry in families.values() {
        let method = entry["method"].as_str().expect("method");
        let reply = entry["recordedDotnet"].clone();
        map.insert(method.to_owned(), reply);
    }
    MockHostBackend::from_recorded_responses(map)
}

#[test]
fn all_five_readonly_families_match_recorded_dotnet_golden_with_zero_mismatches() {
    let golden = load_golden();
    let backend = recorded_backend_from_golden(&golden);
    let mismatches: Arc<Mutex<Vec<ShadowComparison>>> = Arc::new(Mutex::new(Vec::new()));
    let mismatch_sink = Arc::clone(&mismatches);
    let harness = ShadowHarness::with_logger(HostRouter::new(backend), move |cmp| {
        mismatch_sink.lock().unwrap().push(cmp.clone());
    });

    let cases = [
        ("sensors", ShadowFamily::Sensors),
        ("gpuProfile", ShadowFamily::GpuTelemetry),
        ("gpuProcesses", ShadowFamily::GpuTelemetry),
        ("gpuTuningState", ShadowFamily::GpuTelemetry),
        ("systemInfo", ShadowFamily::SystemInfo),
        ("displayInfo", ShadowFamily::DisplayInfo),
        ("hdrState", ShadowFamily::DisplayInfo),
        ("driveInfo", ShadowFamily::DriveInfo),
    ];

    for (key, expected_family) in cases {
        let entry = &golden["families"][key];
        let method = entry["method"].as_str().unwrap();
        let params = &entry["params"];
        let dotnet = &entry["recordedDotnet"];

        assert_eq!(
            classify_readonly_shadow(method, params),
            Some(expected_family),
            "family classification for {method}"
        );

        let cmp = harness
            .observe_and_compare(method, params, dotnet, Duration::from_millis(25))
            .unwrap_or_else(|| panic!("expected shadow comparison for {method}"));
        assert_eq!(cmp.family, expected_family);
        assert!(
            cmp.matched,
            "unexpected diffs for {key} ({method}): {:?}",
            cmp.diffs
        );
    }

    assert!(mismatches.lock().unwrap().is_empty());
    let stats = harness.stats_snapshot();
    for family in ShadowFamily::ALL {
        let entry = stats.get(family).expect("family stats recorded");
        assert!(entry.calls >= 1);
        assert_eq!(entry.mismatches, 0);
        assert_eq!(entry.matches, entry.calls);
    }
}

#[test]
fn shadow_harness_logs_mismatch_per_family_without_mutating_dotnet_authority() {
    let golden = load_golden();
    type DriftCase = (&'static str, ShadowFamily, fn(&mut Value));
    let drift_cases: [DriftCase; 5] = [
        ("sensors", ShadowFamily::Sensors, |v: &mut Value| {
            v["items"][0]["applied"] = json!(false);
        }),
        (
            "gpuTuningState",
            ShadowFamily::GpuTelemetry,
            |v: &mut Value| {
                v["live"]["tempC"] = json!(99.0);
            },
        ),
        ("systemInfo", ShadowFamily::SystemInfo, |v: &mut Value| {
            v["cores"] = json!(999);
        }),
        ("displayInfo", ShadowFamily::DisplayInfo, |v: &mut Value| {
            v["displays"][0]["refresh"] = json!(360);
        }),
        ("driveInfo", ShadowFamily::DriveInfo, |v: &mut Value| {
            v["drives"][0]["health"]["lifeUsed"] = json!(99);
        }),
    ];

    for (key, expected_family, mutate) in drift_cases {
        let entry = &golden["families"][key];
        let method = entry["method"].as_str().unwrap();
        let params = &entry["params"];
        let dotnet = entry["recordedDotnet"].clone();
        let mut drifted_rust = dotnet.clone();
        mutate(&mut drifted_rust);

        let diffs = compare_shadow_response(expected_family, method, &dotnet, &drifted_rust);
        assert!(
            !diffs.is_empty(),
            "expected mismatch detection for {key} ({method})"
        );

        let mut map = HashMap::new();
        map.insert(method.to_owned(), drifted_rust);
        let logged: Arc<Mutex<Vec<String>>> = Arc::new(Mutex::new(Vec::new()));
        let sink = Arc::clone(&logged);
        let harness = ShadowHarness::with_logger(
            HostRouter::new(MockHostBackend::from_recorded_responses(map)),
            move |cmp| {
                sink.lock().unwrap().push(format!(
                    "{}:{}",
                    cmp.family.as_str(),
                    cmp.diffs[0].field
                ));
            },
        );
        let cmp = harness
            .observe_and_compare(method, params, &dotnet, Duration::from_millis(10))
            .unwrap();
        assert!(!cmp.matched);
        assert_eq!(logged.lock().unwrap().len(), 1);
    }
}

#[test]
fn write_families_and_write_actions_are_strictly_excluded_from_slice2() {
    let router = HostRouter::new(MockHostBackend::default());
    let forbidden = [
        ("gpu.tuning.apply", json!({ "mode": "manual" })),
        ("gpu.tuning.reset", json!({})),
        ("gpu.tuning.test", json!({ "action": "start" })),
        ("gpu.tuning.undervolt", json!({ "action": "start" })),
        ("gpu.tuning.waiver", json!({ "accept": true })),
        ("hdr.calibrate", json!({ "action": "start" })),
        ("hdr.game", json!({ "id": "steam:730", "on": true })),
        ("hid.write", json!({})),
        ("mouse.apply", json!({})),
        ("keyboard.apply", json!({})),
        ("audio.setVolume", json!({ "volume": 50 })),
        ("audio.setMute", json!({ "muted": true })),
        ("audio.setFormat", json!({})),
        ("system.health", json!({ "action": "apply" })),
        ("system.health", json!({ "action": "network" })),
        ("system.health", json!({ "action": "chipset" })),
        ("gpu.profile", json!({ "action": "apply" })),
        ("gpu.profile", json!({ "action": "undo" })),
        ("gpu.display", json!({ "action": "set" })),
        ("gpu.display", json!({ "action": "adjust" })),
        ("gpu.display", json!({ "action": "ddc" })),
        ("gpu.display", json!({ "action": "override" })),
        ("gpu.display", json!({ "action": "video" })),
        ("system.drives", json!({ "action": "cache" })),
        ("system.drives", json!({ "action": "flush" })),
        ("system.drives", json!({ "action": "optimize" })),
        ("system.drives", json!({ "action": "trim" })),
        ("system.drives", json!({ "action": "all" })),
    ];
    for (method, params) in forbidden {
        assert!(
            !is_readonly_hardware_call(method, &params),
            "{method} {params} must not be classified read-only"
        );
        assert!(
            classify_readonly_shadow(method, &params).is_none(),
            "{method} {params} must never enter shadow mode"
        );
        assert_eq!(
            router.dispatch(method, &params).unwrap(),
            RouteOutcome::FallbackToDotnet,
            "{method} {params} must fall back to .NET"
        );
    }
}

#[test]
fn exo_policy_hard_stops_gate_process_inspection_in_gpu_processes() {
    for ac in [
        "vgc.exe",
        "EasyAntiCheat_EOS.exe",
        "BEService_x64.exe",
        "EAAntiCheat.GameServiceLauncher.exe",
        "SGuard64.exe",
    ] {
        assert_eq!(classify_process_protection(ac), Some("anti-cheat"));
    }
    assert_eq!(classify_process_protection("dwm"), Some("part of Windows"));
    assert_eq!(classify_process_protection("exo-ui"), None);

    let mut map = HashMap::new();
    map.insert(
        "gpu.processes".to_owned(),
        json!({
            "ok": true,
            "adapters": [],
            "processes": [
                { "pid": 1001, "name": "EasyAntiCheat_EOS.exe", "totalPercent": 0.0, "engines": [], "localBytes": 0, "nonLocalBytes": 0 },
                { "pid": 1002, "name": "dwm", "totalPercent": 0.0, "engines": [], "localBytes": 0, "nonLocalBytes": 0 },
                { "pid": 1003, "name": "blender", "totalPercent": 5.0, "engines": [], "localBytes": 1024, "nonLocalBytes": 0 }
            ],
            "answer": { "summary": "", "hogs": [] }
        }),
    );
    let router = HostRouter::new(MockHostBackend::from_recorded_responses(map));
    let RouteOutcome::Handled(res) = router
        .dispatch("gpu.processes", &json!({ "limit": 10 }))
        .unwrap()
    else {
        panic!("expected Handled");
    };
    let procs = res["processes"].as_array().unwrap();
    assert_eq!(procs[0]["protectedReason"], "anti-cheat");
    assert_eq!(procs[1]["protectedReason"], "part of Windows");
    assert!(procs[2].get("protectedReason").is_none());
}

#[test]
fn live_readonly_backend_reads_all_five_families_without_panicking() {
    let router = HostRouter::new(LiveReadOnlyHostBackend);
    for (method, params) in [
        ("system.health", json!({})),
        ("gpu.profile", json!({})),
        ("gpu.processes", json!({ "limit": 5 })),
        ("gpu.tuning.state", json!({})),
        ("system.about", json!({})),
        ("gpu.display", json!({ "action": "state" })),
        ("hdr.state", json!({})),
        ("system.drives", json!({})),
    ] {
        let RouteOutcome::Handled(val) = router.dispatch(method, &params).unwrap() else {
            panic!("expected Handled for live read-only {method}");
        };
        assert!(val.is_object(), "expected JSON object from {method}");
    }
}

#[cfg(windows)]
#[test]
fn rpc_client_shadow_observer_keeps_dotnet_authoritative_while_logging_rust_mismatch() {
    use std::io::{BufRead, BufReader, Write};
    use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::Storage::FileSystem::PIPE_ACCESS_DUPLEX;
    use windows_sys::Win32::System::Pipes::{
        ConnectNamedPipe, CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE, PIPE_WAIT,
    };

    let golden = load_golden();
    let dotnet_about = golden["families"]["systemInfo"]["recordedDotnet"].clone();
    let pipe_short = format!("exo-shadow-test-{}", std::process::id());
    let full_pipe = format!("\\\\.\\pipe\\{pipe_short}");
    let wide: Vec<u16> = full_pipe.encode_utf16().chain(std::iter::once(0)).collect();

    let server_reply = dotnet_about.clone();
    let server = std::thread::spawn(move || {
        let h = unsafe {
            CreateNamedPipeW(
                wide.as_ptr(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                65536,
                65536,
                0,
                std::ptr::null(),
            )
        };
        assert!(h != INVALID_HANDLE_VALUE);
        let _ = unsafe { ConnectNamedPipe(h, std::ptr::null_mut()) };
        use std::os::windows::io::FromRawHandle;
        let file = unsafe { std::fs::File::from_raw_handle(h as _) };
        let mut reader = BufReader::new(&file);
        let mut writer = &file;
        let mut line = String::new();
        if reader.read_line(&mut line).unwrap_or(0) > 0 {
            let req: Value = serde_json::from_str(line.trim()).unwrap();
            let resp = json!({
                "version": 1,
                "id": req["id"],
                "ok": true,
                "result": server_reply,
            });
            let _ = writeln!(writer, "{resp}");
            let _ = writer.flush();
        }
        std::mem::forget(file);
        unsafe {
            CloseHandle(h);
        }
    });

    std::thread::sleep(Duration::from_millis(40));
    let client = exo_rpc::RpcClient::connect_with_preference(
        &pipe_short,
        Duration::from_secs(3),
        exo_rpc::WirePreference::Json,
        |_| {},
    )
    .expect("connect test pipe");

    let mut drifted = dotnet_about.clone();
    drifted["cores"] = json!(99);
    let mut map = HashMap::new();
    map.insert("system.about".to_owned(), drifted);
    let logged: Arc<Mutex<Vec<String>>> = Arc::new(Mutex::new(Vec::new()));
    let sink = Arc::clone(&logged);
    let harness = Arc::new(ShadowHarness::with_logger(
        HostRouter::new(MockHostBackend::from_recorded_responses(map)),
        move |cmp| {
            sink.lock().unwrap().push(cmp.diffs[0].field.clone());
        },
    ));
    let h_clone = Arc::clone(&harness);
    client.set_shadow_observer(Arc::new(move |method, params, raw_dotnet, elapsed| {
        if let Ok(val) = serde_json::from_str::<Value>(&raw_dotnet) {
            let _ = h_clone.observe_and_compare(method, &params, &val, elapsed);
        }
    }));

    let reply: Value = client
        .call_blocking("system.about", &json!({}), Duration::from_secs(3))
        .expect("call system.about");
    assert_eq!(reply["cores"], dotnet_about["cores"]);
    assert_eq!(logged.lock().unwrap().as_slice(), &["cores".to_owned()]);

    client.close();
    let _ = server.join();
}
