use std::env;
use std::fs;
use std::thread;
use std::time::{Duration, Instant};

use exo_power_guard::backends::{discover_all_backends, discover_primary_backend};
use exo_power_guard::guard::{GuardPolicy, MockActionExecutor, PowerGuard, dispatch_action};
use exo_power_guard::per_pin::wireview::{decode_wireview_frame, encode_wireview_frame_for_test};
use exo_power_guard::recorder::SessionRecorder;
use exo_power_guard::sidecar::{SidecarClient, run_sidecar_loop};
use exo_power_guard::types::{THROTTLE_HW_POWER_BRAKE, THROTTLE_HW_THERMAL};
use exo_power_guard::win32::current_process_memory;

fn main() {
    let args: Vec<String> = env::args().collect();
    let cmd = args.get(1).map(String::as_str).unwrap_or("probe");

    match cmd {
        "probe" => {
            let filter = parse_flag_str(&args, "--backend");
            cmd_probe(filter.as_deref());
        }
        "sidecar" => {
            let interval = parse_flag_u64(&args, "--interval-ms").unwrap_or(1_000);
            run_sidecar_loop(interval);
        }
        "bench" => {
            let iters = parse_flag_usize(&args, "--iters").unwrap_or(500);
            cmd_bench(iters);
        }
        "bench-sidecar" => cmd_bench_sidecar(),
        "record" => {
            let secs = parse_flag_u64(&args, "--seconds").unwrap_or(3);
            let out = parse_flag_str(&args, "--out");
            cmd_record(secs, out.as_deref());
        }
        "simulate-fault" => cmd_simulate_fault(),
        other => {
            eprintln!("Unknown command: {other}");
            eprintln!(
                "Usage: exo-power-guard [probe | sidecar --interval-ms N | bench --iters N | bench-sidecar | record --seconds S --out FILE | simulate-fault]"
            );
            std::process::exit(2);
        }
    }
}

fn cmd_probe(filter: Option<&str>) {
    let mut backends: Vec<Box<dyn exo_power_guard::backends::TelemetryBackend>> = match filter {
        Some("d3dkmt") => exo_power_guard::backends::d3dkmt::D3dkmtBackend::open()
            .map(|b| Box::new(b) as Box<dyn exo_power_guard::backends::TelemetryBackend>)
            .into_iter()
            .collect(),
        _ => discover_all_backends(),
    };
    let mut guard = PowerGuard::new(GuardPolicy::default());
    println!("Detected {} backend(s):", backends.len());
    for backend in &mut backends {
        let _ = backend.read();
        thread::sleep(Duration::from_millis(50));
        if let Some(snap) = backend.read() {
            let eval = guard.evaluate(&snap);
            let caps = snap.capabilities();
            println!("--- backend: {} ---", snap.backend.as_str());
            println!("{}", snap.to_json());
            println!(
                "guard_severity={} alerts={} caps=\"{}\" note={:?}",
                eval.severity.as_str(),
                eval.alerts.len(),
                caps.summary,
                caps.limitation_note
            );
        }
    }
    if let Some(mem) = current_process_memory() {
        println!(
            "process_memory_mb working_set={:.2} private={:.2} peak_working_set={:.2}",
            mem.working_set_mb, mem.private_mb, mem.peak_working_set_mb
        );
    }
}

fn cmd_bench_sidecar() {
    let before = current_process_memory();
    let t0 = Instant::now();
    let Some(client) = SidecarClient::spawn_beside_exe(250) else {
        eprintln!("Could not spawn sidecar beside current exe.");
        std::process::exit(1);
    };
    let mut first_ms = None;
    let mut snap = None;
    for _ in 0..80 {
        if let Some(s) = client.latest() {
            first_ms = Some(t0.elapsed().as_secs_f64() * 1000.0);
            snap = Some(s);
            break;
        }
        thread::sleep(Duration::from_millis(10));
    }
    let active_parent = current_process_memory();
    let drop_start = Instant::now();
    drop(client);
    let stop_ms = drop_start.elapsed().as_secs_f64() * 1000.0;
    let after_parent = current_process_memory();

    println!(
        "sidecar_first_frame_ms={:.2} sidecar_clean_stop_ms={:.2}",
        first_ms.unwrap_or(-1.0),
        stop_ms
    );
    if let (Some(b), Some(a), Some(e)) = (before, active_parent, after_parent) {
        println!(
            "parent_ram_mb before={:.2} active={:.2} after_stop={:.2} (delta_active={:.2})",
            b.private_mb,
            a.private_mb,
            e.private_mb,
            a.private_mb - b.private_mb
        );
    }
    if let Some(s) = snap {
        println!("first_snapshot={}", s.to_json());
    }
}

fn cmd_bench(iters: usize) {
    let mut backends = discover_all_backends();
    if backends.is_empty() {
        eprintln!("No telemetry backend available.");
        std::process::exit(1);
    }
    let mut guard = PowerGuard::new(GuardPolicy::default());

    for backend in &mut backends {
        let kind = backend.kind();
        let _ = backend.read();
        let mut samples_us = Vec::with_capacity(iters);
        let mut last_snap = None;
        let total_start = Instant::now();
        for _ in 0..iters {
            let t0 = Instant::now();
            if let Some(snap) = backend.read() {
                let _ = guard.evaluate(&snap);
                last_snap = Some(snap);
            }
            samples_us.push(t0.elapsed().as_secs_f64() * 1_000_000.0);
        }
        let total_ms = total_start.elapsed().as_secs_f64() * 1_000.0;
        samples_us.sort_by(|a, b| a.total_cmp(b));
        let p50 = percentile(&samples_us, 0.50);
        let p95 = percentile(&samples_us, 0.95);
        let p99 = percentile(&samples_us, 0.99);
        let mean = samples_us.iter().sum::<f64>() / samples_us.len().max(1) as f64;
        let cpu_1hz_pct = (mean / 1_000_000.0) * 100.0;

        println!("=== BENCH {} ({} iters) ===", kind.as_str(), iters);
        println!(
            "latency_us: mean={mean:.1} p50={p50:.1} p95={p95:.1} p99={p99:.1} total_ms={total_ms:.2}"
        );
        println!("estimated_cpu_at_1hz_pct: {cpu_1hz_pct:.5}%");
        if let Some(snap) = last_snap {
            println!("last_snapshot: {}", snap.to_json());
        }
    }
    if let Some(mem) = current_process_memory() {
        println!(
            "process_memory_mb: working_set={:.2} private={:.2} peak_working_set={:.2}",
            mem.working_set_mb, mem.private_mb, mem.peak_working_set_mb
        );
    }
}

fn cmd_record(secs: u64, out_path: Option<&str>) {
    let Some(mut backend) = discover_primary_backend() else {
        eprintln!("No telemetry backend available.");
        std::process::exit(1);
    };
    let mut rec = SessionRecorder::new((secs as usize).saturating_mul(4).max(16));
    let mut guard = PowerGuard::new(GuardPolicy::default());
    let ticks = (secs * 2).max(2);
    for _ in 0..ticks {
        if let Some(snap) = backend.read() {
            let _ = guard.evaluate(&snap);
            rec.push(snap);
        }
        thread::sleep(Duration::from_millis(500));
    }
    if let Some(sum) = rec.summarize() {
        println!(
            "Recorded {} samples over {} ms on {} ({})",
            sum.samples, sum.duration_ms, sum.gpu_name, sum.backend
        );
        if let Some(p) = &sum.power_w {
            println!(
                "power_w: min={:.2} avg={:.2} max={:.2}",
                p.min, p.avg, p.max
            );
        }
        if let Some(t) = &sum.core_temp_c {
            println!(
                "core_temp_c: min={:.1} avg={:.1} max={:.1}",
                t.min, t.avg, t.max
            );
        }
    }
    if let Some(path) = out_path {
        let html = rec.export_html();
        fs::write(path, html).expect("write html report");
        println!("Exported HTML report to {path}");
    }
}

fn cmd_simulate_fault() {
    let Some(mut backend) = discover_primary_backend() else {
        eprintln!("No telemetry backend available.");
        std::process::exit(1);
    };
    let mut base = backend.read().expect("live snapshot");
    let policy = GuardPolicy {
        opt_in_lower_power_limit: true,
        opt_in_close_game: true,
        opt_in_shutdown_pc: true,
        alert_sustained_ms: 2_000,
        stage_step_ms: 4_000,
        ..GuardPolicy::default()
    };
    let mut guard = PowerGuard::new(policy);
    let mut mock_exec = MockActionExecutor::default();

    let bad_frame = encode_wireview_frame_for_test(
        [11950, 11920, 12050, 12060, 11980, 11990],
        [10400, 10100, 1800, 1900, 6200, 6100],
        [962, 915, 430, 440],
        0,
        0x0002,
        0x0002,
    );
    let bad_conn = decode_wireview_frame(&bad_frame).unwrap().connector;

    let t0 = base.timestamp_ms;
    for step in 0..=12u64 {
        let mut s = base.clone();
        s.timestamp_ms = t0 + step * 1_000;
        s.connector = Some(bad_conn.clone());
        s.throttle_flags = THROTTLE_HW_POWER_BRAKE | THROTTLE_HW_THERMAL;
        let ev = guard.evaluate(&s);
        let _ = dispatch_action(&ev.action, &mut mock_exec);
        println!(
            "t=+{step}s severity={} alerts={} action={:?}",
            ev.severity.as_str(),
            ev.alerts.len(),
            ev.action
        );
    }
    base.timestamp_ms = t0 + 13_000;
    let recovered = guard.evaluate(&base);
    println!(
        "t=+13s (recovered) severity={} alerts={} action={:?} mock_exec={mock_exec:?}",
        recovered.severity.as_str(),
        recovered.alerts.len(),
        recovered.action
    );
}

fn percentile(sorted: &[f64], p: f64) -> f64 {
    if sorted.is_empty() {
        return 0.0;
    }
    let idx = ((sorted.len() - 1) as f64 * p).round() as usize;
    sorted[idx.min(sorted.len() - 1)]
}

fn parse_flag_usize(args: &[String], flag: &str) -> Option<usize> {
    args.windows(2)
        .find(|w| w[0] == flag)
        .and_then(|w| w[1].parse().ok())
}

fn parse_flag_u64(args: &[String], flag: &str) -> Option<u64> {
    args.windows(2)
        .find(|w| w[0] == flag)
        .and_then(|w| w[1].parse().ok())
}

fn parse_flag_str(args: &[String], flag: &str) -> Option<String> {
    args.windows(2).find(|w| w[0] == flag).map(|w| w[1].clone())
}
