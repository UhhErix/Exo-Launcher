use exo_autopilot::os::{AutopilotOs, Win32Os};
use exo_autopilot::timer_res::TARGET_TIMER_100NS;
use exo_autopilot::{AntiCheatClass, AutopilotConfig, AutopilotEngine};
use serde::{Deserialize, Serialize};
use std::process::{Child, Command};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

#[derive(Debug, Clone, Serialize, Deserialize)]
struct FramePacingStats {
    samples: usize,
    avg_ms: f64,
    stddev_ms: f64,
    p50_ms: f64,
    p99_ms: f64,
    p999_ms: f64,
    max_ms: f64,
    avg_fps: f64,
    low_1_fps: f64,
    low_01_fps: f64,
}

#[derive(Debug, Clone, Serialize)]
struct SleepStats {
    timer_100ns_reported: u32,
    samples: usize,
    avg_ms: f64,
    p50_ms: f64,
    p99_ms: f64,
    stddev_ms: f64,
}

#[derive(Debug, Clone, Serialize)]
struct LiveOsProbes {
    total_phys_gib: f64,
    avail_phys_gib: f64,
    standby_gib: Option<f64>,
    active_bits_jobs: usize,
    bits_service_detected: bool,
    wsearch_service_detected: bool,
    on_battery: bool,
    active_power_scheme_detected: bool,
    mouse_accel_detected: bool,
}

#[derive(Debug, Clone, Serialize)]
struct BenchReport {
    cpu_brand: String,
    topology_kind: String,
    topology_threads: usize,
    background_cpu_set_ids: Vec<u32>,
    topology_summary: String,
    engine_delta_ws_mb: f64,
    engine_delta_priv_mb: f64,
    resident_ws_mb: f64,
    resident_priv_mb: f64,
    idle_cpu_pct: f64,
    timer_before_100ns: u32,
    timer_active_100ns: u32,
    timer_after_100ns: u32,
    timer_restore_ms: f64,
    sleep_stock: SleepStats,
    sleep_500us: SleepStats,
    sleep_507us: SleepStats,
    game_clean: FramePacingStats,
    game_with_hog: FramePacingStats,
    game_with_hog_and_autopilot_steady: FramePacingStats,
    game_with_hog_and_autopilot_learned: FramePacingStats,
    steady_low_1_recovery_pct: f64,
    learned_low_1_recovery_pct: f64,
    steady_p99_improvement_pct: f64,
    autopilot_restraints: u64,
    corralled_count_session1: usize,
    corralled_count_session2_instant: usize,
    hog_cpu_sets_applied_ok: bool,
    hog_restored_priority_ok: bool,
    hog_restored_cpu_sets_ok: bool,
    live_probes: LiveOsProbes,
}

fn round2(v: f64) -> f64 {
    (v * 100.0).round() / 100.0
}

fn summarize_frames(mut samples: Vec<f64>) -> FramePacingStats {
    if samples.is_empty() {
        return FramePacingStats {
            samples: 0,
            avg_ms: 0.0,
            stddev_ms: 0.0,
            p50_ms: 0.0,
            p99_ms: 0.0,
            p999_ms: 0.0,
            max_ms: 0.0,
            avg_fps: 0.0,
            low_1_fps: 0.0,
            low_01_fps: 0.0,
        };
    }
    let n = samples.len();
    let mean = samples.iter().sum::<f64>() / n as f64;
    let var = samples.iter().map(|v| (v - mean).powi(2)).sum::<f64>() / n as f64;
    samples.sort_by(|a, b| a.total_cmp(b));
    let pct = |p: f64| -> f64 {
        let idx = ((n as f64 - 1.0) * p).round().clamp(0.0, (n - 1) as f64) as usize;
        samples[idx]
    };
    let p99 = pct(0.99);
    let p999 = pct(0.999);
    let worst_1_start = ((n as f64) * 0.99).floor() as usize;
    let worst_1_avg = samples[worst_1_start.min(n - 1)..].iter().sum::<f64>()
        / (n - worst_1_start.min(n - 1)) as f64;
    let worst_01_start = ((n as f64) * 0.999).floor() as usize;
    let worst_01_avg = samples[worst_01_start.min(n - 1)..].iter().sum::<f64>()
        / (n - worst_01_start.min(n - 1)) as f64;
    FramePacingStats {
        samples: n,
        avg_ms: round2(mean),
        stddev_ms: round2(var.sqrt()),
        p50_ms: round2(pct(0.50)),
        p99_ms: round2(p99),
        p999_ms: round2(p999),
        max_ms: round2(samples[n - 1]),
        avg_fps: round2(1000.0 / mean.max(0.001)),
        low_1_fps: round2(1000.0 / worst_1_avg.max(0.001)),
        low_01_fps: round2(1000.0 / worst_01_avg.max(0.001)),
    }
}

fn measure_sleep1(os: &Win32Os, count: usize) -> SleepStats {
    let mut samples = Vec::with_capacity(count);
    for _ in 0..count {
        let t0 = Instant::now();
        std::thread::sleep(Duration::from_millis(1));
        samples.push(t0.elapsed().as_secs_f64() * 1000.0);
    }
    let n = samples.len();
    let mean = samples.iter().sum::<f64>() / n as f64;
    let var = samples.iter().map(|v| (v - mean).powi(2)).sum::<f64>() / n as f64;
    samples.sort_by(|a, b| a.total_cmp(b));
    let p50 = samples[n / 2];
    let p99 = samples[((n as f64 - 1.0) * 0.99) as usize];
    let cur = os
        .query_timer_resolution()
        .map(|t| t.current_100ns)
        .unwrap_or(0);
    SleepStats {
        timer_100ns_reported: cur,
        samples: n,
        avg_ms: round2(mean),
        p50_ms: round2(p50),
        p99_ms: round2(p99),
        stddev_ms: round2(var.sqrt()),
    }
}

fn run_hog_mode() {
    let threads = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(8);
    let stop = Arc::new(AtomicBool::new(false));
    let mut handles = Vec::new();
    for t in 0..threads {
        let s = stop.clone();
        handles.push(std::thread::spawn(move || {
            let mut x: u64 = 0x9e37_79b9_7f4a_7c15u64.wrapping_add(t as u64);
            while !s.load(Ordering::Relaxed) {
                for _ in 0..50_000 {
                    x = x.wrapping_mul(6364136223846793005).wrapping_add(1);
                }
                std::hint::black_box(x);
            }
        }));
    }
    std::thread::sleep(Duration::from_secs(60));
    stop.store(true, Ordering::Relaxed);
    for h in handles {
        let _ = h.join();
    }
}

fn do_frame_work(seed: u64, iters: u64) -> u64 {
    let mut acc = seed ^ 0x517c_c1b7_2722_0a95;
    for _ in 0..iters {
        acc = acc
            .wrapping_mul(2862933555777941757)
            .wrapping_add(3037000493);
        acc ^= acc >> 17;
    }
    std::hint::black_box(acc)
}

fn run_game_sim_mode(warmup_ms: u64, measure_ms: u64) {
    let total_ms = warmup_ms + measure_ms;
    let start = Instant::now();
    let mut frames = Vec::with_capacity(2000);
    let mut frame_idx = 0u64;
    let work_iters = 2_800_000u64;
    let mut main_seed = 42u64;

    while start.elapsed() < Duration::from_millis(total_ms) {
        let f0 = Instant::now();
        main_seed = do_frame_work(main_seed.wrapping_add(frame_idx), work_iters);
        let dt_ms = f0.elapsed().as_secs_f64() * 1000.0;
        if start.elapsed() >= Duration::from_millis(warmup_ms) {
            frames.push(dt_ms);
        }
        frame_idx += 1;
    }

    let stats = summarize_frames(frames);
    println!("{}", serde_json::to_string(&stats).unwrap());
}

fn spawn_self(args: &[&str]) -> Child {
    let exe = std::env::current_exe().expect("current_exe");
    Command::new(exe)
        .args(args)
        .stdout(std::process::Stdio::piped())
        .spawn()
        .expect("spawn child")
}

fn spawn_hog() -> (Child, std::path::PathBuf) {
    let exe = std::env::current_exe().expect("current_exe");
    let hog_exe = std::env::temp_dir().join("exo-bench-hog.exe");
    let _ = std::fs::copy(&exe, &hog_exe);
    let child = Command::new(&hog_exe)
        .arg("--hog")
        .stdout(std::process::Stdio::null())
        .spawn()
        .expect("spawn hog");
    (child, hog_exe)
}

#[cfg(windows)]
fn sample_self_mem_mb() -> (f64, f64) {
    use windows::Win32::System::ProcessStatus::{
        K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX,
    };
    use windows::Win32::System::Threading::GetCurrentProcess;
    unsafe {
        let mut pmc = PROCESS_MEMORY_COUNTERS_EX {
            cb: std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
            ..Default::default()
        };
        if K32GetProcessMemoryInfo(
            GetCurrentProcess(),
            (&mut pmc as *mut PROCESS_MEMORY_COUNTERS_EX).cast(),
            pmc.cb,
        )
        .as_bool()
        {
            (
                round2(pmc.WorkingSetSize as f64 / (1024.0 * 1024.0)),
                round2(pmc.PrivateUsage as f64 / (1024.0 * 1024.0)),
            )
        } else {
            (0.0, 0.0)
        }
    }
}

#[cfg(windows)]
fn sample_self_cpu_ms() -> f64 {
    use windows::Win32::Foundation::FILETIME;
    use windows::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes};
    unsafe {
        let mut c = FILETIME::default();
        let mut e = FILETIME::default();
        let mut k = FILETIME::default();
        let mut u = FILETIME::default();
        if GetProcessTimes(GetCurrentProcess(), &mut c, &mut e, &mut k, &mut u).is_ok() {
            let kt = ((k.dwHighDateTime as u64) << 32) | (k.dwLowDateTime as u64);
            let ut = ((u.dwHighDateTime as u64) << 32) | (u.dwLowDateTime as u64);
            (kt + ut) as f64 / 10_000.0
        } else {
            0.0
        }
    }
}

fn run_game_child(warmup_ms: u64, measure_ms: u64) -> FramePacingStats {
    let w = warmup_ms.to_string();
    let m = measure_ms.to_string();
    let child = spawn_self(&["--game-sim", &w, &m]);
    let out = child.wait_with_output().expect("game sim wait");
    let s = String::from_utf8_lossy(&out.stdout);
    serde_json::from_str(s.trim()).expect("parse game sim stats")
}

fn recovery_pct(clean: f64, hog: f64, auto: f64) -> f64 {
    let drop = (clean - hog).max(0.01);
    let rec = (auto - hog).max(0.0);
    round2((rec / drop * 100.0).min(100.0))
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.iter().any(|a| a == "--hog") {
        run_hog_mode();
        return;
    }
    if let Some(pos) = args.iter().position(|a| a == "--game-sim") {
        let warmup: u64 = args
            .get(pos + 1)
            .and_then(|s| s.parse().ok())
            .unwrap_or(2000);
        let measure: u64 = args
            .get(pos + 2)
            .and_then(|s| s.parse().ok())
            .unwrap_or(5000);
        run_game_sim_mode(warmup, measure);
        return;
    }

    let (ws_before, priv_before) = sample_self_mem_mb();
    let os = Arc::new(Win32Os::new());
    let brand = os.read_cpu_brand();
    let journal_path = std::env::temp_dir().join("exo-autopilot-bench-journal.json");
    let _ = std::fs::remove_file(&journal_path);

    let cfg = AutopilotConfig {
        journal_path: journal_path.clone(),
        enable_download_pause: false,
        enable_mouse_accel_guard: false,
        enable_standby_shield: false,
        ..Default::default()
    };
    let mut engine = AutopilotEngine::new(os.clone(), cfg.clone(), None);
    let topo = engine.topology().clone();
    let (ws_mb, priv_mb) = sample_self_mem_mb();
    let engine_delta_ws_mb = round2((ws_mb - ws_before).max(0.0));
    let engine_delta_priv_mb = round2((priv_mb - priv_before).max(0.0));

    let cpu0 = sample_self_cpu_ms();
    let wall0 = Instant::now();
    std::thread::sleep(Duration::from_millis(2000));
    let idle_cpu_pct = round2(
        ((sample_self_cpu_ms() - cpu0).max(0.0) / (wall0.elapsed().as_secs_f64() * 1000.0)) * 100.0,
    );

    let mem = os.memory_snapshot();
    let live_probes = LiveOsProbes {
        total_phys_gib: round2(mem.total_phys_bytes as f64 / (1024.0 * 1024.0 * 1024.0)),
        avail_phys_gib: round2(mem.avail_phys_bytes as f64 / (1024.0 * 1024.0 * 1024.0)),
        standby_gib: mem
            .standby_bytes
            .map(|b| round2(b as f64 / (1024.0 * 1024.0 * 1024.0))),
        active_bits_jobs: os.list_active_bits_jobs().len(),
        bits_service_detected: os.query_service("BITS").is_some(),
        wsearch_service_detected: os.query_service("WSearch").is_some(),
        on_battery: os.is_on_battery(),
        active_power_scheme_detected: os.active_power_scheme().is_some(),
        mouse_accel_detected: os.read_mouse_accel().is_some(),
    };

    let timer_before = os
        .query_timer_resolution()
        .map(|t| t.current_100ns)
        .unwrap_or(0);
    let sleep_stock = measure_sleep1(os.as_ref(), 60);

    let _ = os.set_timer_resolution(5000, true);
    let sleep_500us = measure_sleep1(os.as_ref(), 120);
    let _ = os.set_timer_resolution(5000, false);

    let _ = os.set_timer_resolution(TARGET_TIMER_100NS, true);
    let timer_active = os
        .query_timer_resolution()
        .map(|t| t.current_100ns)
        .unwrap_or(0);
    let sleep_507us = measure_sleep1(os.as_ref(), 120);
    let t_rel = Instant::now();
    let _ = os.set_timer_resolution(TARGET_TIMER_100NS, false);
    let timer_restore_ms = round2(t_rel.elapsed().as_secs_f64() * 1000.0);
    let timer_after = os
        .query_timer_resolution()
        .map(|t| t.current_100ns)
        .unwrap_or(0);

    let game_clean = run_game_child(2000, 5000);

    let (mut hog1, hog1_exe) = spawn_hog();
    std::thread::sleep(Duration::from_millis(400));
    let game_with_hog = run_game_child(2000, 5000);
    let _ = hog1.kill();
    let _ = hog1.wait();
    std::thread::sleep(Duration::from_millis(400));

    let (mut hog2, _) = spawn_hog();
    let hog2_pid = hog2.id();
    let orig_hog_prio = os.get_priority_class(hog2_pid).unwrap_or(0x20);
    std::thread::sleep(Duration::from_millis(300));

    let game_child1 = spawn_self(&["--game-sim", "2200", "5000"]);
    let game_pid1 = game_child1.id();

    engine.on_game_start(
        game_pid1,
        "synth-game-sim.exe",
        AntiCheatClass::OfflineAntiCheat,
        0,
    );
    let loop_start = Instant::now();
    let mut tick_ms = 0u64;
    let mut hog_cpu_sets_applied_ok = false;
    while loop_start.elapsed() < Duration::from_millis(7000) {
        std::thread::sleep(Duration::from_millis(500));
        tick_ms += 500;
        engine.tick(tick_ms, false);
        if !hog_cpu_sets_applied_ok
            && os
                .get_default_cpu_sets(hog2_pid)
                .is_some_and(|v| !v.is_empty())
        {
            hog_cpu_sets_applied_ok = true;
        }
    }

    let status_s1 = engine.status();
    let out1 = game_child1.wait_with_output().expect("game child 1");
    let game_with_hog_and_autopilot_steady: FramePacingStats =
        serde_json::from_str(String::from_utf8_lossy(&out1.stdout).trim()).expect("parse s1");

    engine.on_game_exit();
    let post_hog_prio = os.get_priority_class(hog2_pid).unwrap_or(0);
    let hog_restored_priority_ok = post_hog_prio == orig_hog_prio;
    let hog_restored_cpu_sets_ok = os
        .get_default_cpu_sets(hog2_pid)
        .is_some_and(|v| v.is_empty());

    let mut engine2 = AutopilotEngine::new(os.clone(), cfg, None);
    let game_child2 = spawn_self(&["--game-sim", "1200", "5000"]);
    let game_pid2 = game_child2.id();
    engine2.on_game_start(
        game_pid2,
        "synth-game-sim.exe",
        AntiCheatClass::OfflineAntiCheat,
        0,
    );
    let corralled_count_session2_instant = engine2.status().corralled_count;
    let loop_start2 = Instant::now();
    let mut tick_ms2 = 0u64;
    while loop_start2.elapsed() < Duration::from_millis(6000) {
        std::thread::sleep(Duration::from_millis(500));
        tick_ms2 += 500;
        engine2.tick(tick_ms2, false);
    }
    let out2 = game_child2.wait_with_output().expect("game child 2");
    let game_with_hog_and_autopilot_learned: FramePacingStats =
        serde_json::from_str(String::from_utf8_lossy(&out2.stdout).trim()).expect("parse s2");
    engine2.on_game_exit();

    let _ = hog2.kill();
    let _ = hog2.wait();
    let _ = std::fs::remove_file(&hog1_exe);
    let _ = std::fs::remove_file(&journal_path);

    let steady_low_1_recovery_pct = recovery_pct(
        game_clean.low_1_fps,
        game_with_hog.low_1_fps,
        game_with_hog_and_autopilot_steady.low_1_fps,
    );
    let learned_low_1_recovery_pct = recovery_pct(
        game_clean.low_1_fps,
        game_with_hog.low_1_fps,
        game_with_hog_and_autopilot_learned.low_1_fps,
    );
    let steady_p99_improvement_pct = round2(
        ((game_with_hog.p99_ms - game_with_hog_and_autopilot_steady.p99_ms)
            / game_with_hog.p99_ms.max(0.01))
            * 100.0,
    );

    let report = BenchReport {
        cpu_brand: brand,
        topology_kind: format!("{:?}", topo.kind),
        topology_threads: topo.total_threads,
        background_cpu_set_ids: topo.background_set_ids,
        topology_summary: topo.summary,
        engine_delta_ws_mb,
        engine_delta_priv_mb,
        resident_ws_mb: ws_mb,
        resident_priv_mb: priv_mb,
        idle_cpu_pct,
        timer_before_100ns: timer_before,
        timer_active_100ns: timer_active,
        timer_after_100ns: timer_after,
        timer_restore_ms,
        sleep_stock,
        sleep_500us,
        sleep_507us,
        game_clean,
        game_with_hog,
        game_with_hog_and_autopilot_steady,
        game_with_hog_and_autopilot_learned,
        steady_low_1_recovery_pct,
        learned_low_1_recovery_pct,
        steady_p99_improvement_pct,
        autopilot_restraints: status_s1.total_restraints,
        corralled_count_session1: status_s1.corralled_count,
        corralled_count_session2_instant,
        hog_cpu_sets_applied_ok,
        hog_restored_priority_ok,
        hog_restored_cpu_sets_ok,
        live_probes,
    };
    println!("{}", serde_json::to_string_pretty(&report).unwrap());
}
