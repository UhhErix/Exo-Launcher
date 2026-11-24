use exo_present::{
    LiveEtwSession, TraceConfig, current_process_usage, find_pids_by_name,
    foreground_pid_and_monitor, run_offline_accuracy_suite,
};
use serde::Serialize;
use std::fs::File;
use std::io::{BufWriter, Write};
use std::thread;
use std::time::{Duration, Instant};

#[derive(Serialize)]
struct BenchReport {
    session_name: String,
    target_process: Option<String>,
    watched_pids: Vec<u32>,
    duration_secs: f64,
    callback_hits: u64,
    events_seen: u64,
    buffers_written: u32,
    etw_events_lost: u32,
    frames: u64,
    fps: f64,
    avg_ms: f64,
    median_ms: f64,
    p95_ms: f64,
    p99_ms: f64,
    p999_ms: f64,
    max_ms: f64,
    low_1pct_fps: f64,
    low_01pct_fps: f64,
    avg_cpu_present_ms: f64,
    avg_gpu_busy_ms: Option<f64>,
    avg_display_latency_ms: Option<f64>,
    stutter_count: u64,
    cpu_pct_total: f64,
    cpu_pct_single_core: f64,
    cpu_time_ms: f64,
    working_set_mb: f64,
    private_mb: f64,
    logical_cpus: u32,
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let mut process_name: Option<String> = None;
    let mut pid_arg: Option<u32> = None;
    let mut duration_secs: f64 = 15.0;
    let mut session_name = "ExoPresentBench".to_string();
    let mut out_json: Option<String> = None;
    let mut out_csv: Option<String> = None;
    let mut kernel_display = false;
    let mut all_pids = false;
    let mut diag = false;
    let mut offline_accuracy = false;

    let mut i = 1;
    while i < args.len() {
        match args[i].as_str() {
            "--process-name" if i + 1 < args.len() => {
                process_name = Some(args[i + 1].clone());
                i += 2;
            }
            "--pid" if i + 1 < args.len() => {
                pid_arg = args[i + 1].parse().ok();
                i += 2;
            }
            "--duration" if i + 1 < args.len() => {
                duration_secs = args[i + 1].parse().unwrap_or(15.0);
                i += 2;
            }
            "--session-name" if i + 1 < args.len() => {
                session_name = args[i + 1].clone();
                i += 2;
            }
            "--out" if i + 1 < args.len() => {
                out_json = Some(args[i + 1].clone());
                i += 2;
            }
            "--csv" if i + 1 < args.len() => {
                out_csv = Some(args[i + 1].clone());
                i += 2;
            }
            "--kernel-display" => {
                kernel_display = true;
                i += 1;
            }
            "--all-pids" => {
                all_pids = true;
                i += 1;
            }
            "--diag" => {
                diag = true;
                i += 1;
            }
            "--offline-accuracy" => {
                offline_accuracy = true;
                i += 1;
            }
            _ => {
                i += 1;
            }
        }
    }

    if offline_accuracy {
        let report = run_offline_accuracy_suite();
        let json = serde_json::to_string_pretty(&report).unwrap();
        if let Some(ref path) = out_json {
            let _ = std::fs::write(path, &json);
        }
        println!("{json}");
        if !report.all_passed {
            std::process::exit(1);
        }
        return;
    }

    let mut pids = Vec::new();
    if !all_pids {
        if let Some(p) = pid_arg {
            pids.push(p);
        } else if let Some(ref name) = process_name {
            pids = find_pids_by_name(name);
        } else {
            let (fg_pid, _) = foreground_pid_and_monitor();
            if fg_pid != 0 {
                pids.push(fg_pid);
            }
        }
    }

    let cfg = TraceConfig {
        session_name: session_name.clone(),
        enable_kernel_display: kernel_display,
        flush_timer_secs: 1,
        record_raw_samples: out_csv.is_some(),
        record_diag: diag,
    };
    let mut sess = match LiveEtwSession::start(cfg) {
        Ok(s) => s,
        Err(code) => {
            eprintln!("exo-present-bench: StartTraceW failed with Win32 error {code}");
            std::process::exit(2);
        }
    };
    if !pids.is_empty() {
        sess.watch(&pids);
    }

    let start_usage = current_process_usage();
    let t0 = Instant::now();
    let deadline = Duration::from_secs_f64(duration_secs.max(1.0));
    while t0.elapsed() < deadline {
        thread::sleep(Duration::from_millis(250));
        if !all_pids
            && pids.is_empty()
            && let Some(ref name) = process_name
        {
            let found = find_pids_by_name(name);
            if !found.is_empty() {
                pids = found;
                sess.watch(&pids);
            }
        }
        sess.check_kernel_fallback();
    }
    sess.flush();
    thread::sleep(Duration::from_millis(120));

    let elapsed = t0.elapsed().as_secs_f64();
    let end_usage = current_process_usage();
    let m = sess.snapshot();
    let (buffers_written, etw_events_lost) = sess.query_buffers_and_lost();
    let callback_hits = sess.callback_hits();
    let events_seen = sess.events_seen();
    let freq = sess.qpc_frequency() as f64;

    if let Some(ref csv_path) = out_csv {
        let samples = sess.drain_raw_samples();
        if let Ok(file) = File::create(csv_path) {
            let mut w = BufWriter::new(file);
            let _ = writeln!(
                w,
                "pid,qpc_ms,frame_time_ms,cpu_present_ms,gpu_busy_ms,display_latency_ms,dropped,kernel"
            );
            for s in samples {
                let qpc_ms = (s.qpc_ticks as f64 * 1000.0) / freq;
                let gb = s.gpu_busy_ms.map(|v| format!("{v:.4}")).unwrap_or_default();
                let dl = s
                    .display_latency_ms
                    .map(|v| format!("{v:.4}"))
                    .unwrap_or_default();
                let _ = writeln!(
                    w,
                    "{},{:.4},{:.4},{:.4},{},{},{},{}",
                    s.pid,
                    qpc_ms,
                    s.frame_time_ms,
                    s.cpu_present_ms,
                    gb,
                    dl,
                    if s.dropped { 1 } else { 0 },
                    if s.kernel_source { 1 } else { 0 }
                );
            }
        }
    }

    let cpu_100ns = (end_usage.cpu_kernel_100ns - start_usage.cpu_kernel_100ns)
        + (end_usage.cpu_user_100ns - start_usage.cpu_user_100ns);
    let cpu_secs = cpu_100ns as f64 / 10_000_000.0;
    let logical = end_usage.logical_cpus.max(1) as f64;
    let cpu_pct_total = (cpu_secs / elapsed / logical) * 100.0;
    let cpu_pct_single_core = (cpu_secs / elapsed) * 100.0;

    let report = BenchReport {
        session_name,
        target_process: process_name,
        watched_pids: pids,
        duration_secs: elapsed,
        callback_hits,
        events_seen,
        buffers_written,
        etw_events_lost,
        frames: m.frame_count,
        fps: m.fps,
        avg_ms: m.avg_ms,
        median_ms: m.median_ms,
        p95_ms: m.p95_ms,
        p99_ms: m.p99_ms,
        p999_ms: m.p999_ms,
        max_ms: m.max_ms,
        low_1pct_fps: m.low_1pct_fps,
        low_01pct_fps: m.low_01pct_fps,
        avg_cpu_present_ms: m.avg_cpu_present_ms,
        avg_gpu_busy_ms: m.avg_gpu_busy_ms,
        avg_display_latency_ms: m.avg_display_latency_ms,
        stutter_count: m.stutter_count,
        cpu_pct_total,
        cpu_pct_single_core,
        cpu_time_ms: cpu_secs * 1000.0,
        working_set_mb: end_usage.working_set_bytes as f64 / (1024.0 * 1024.0),
        private_mb: end_usage.private_bytes as f64 / (1024.0 * 1024.0),
        logical_cpus: end_usage.logical_cpus,
    };

    let json = serde_json::to_string_pretty(&report).unwrap();
    if let Some(ref path) = out_json {
        let _ = std::fs::write(path, &json);
    }
    println!("{json}");
    if diag {
        eprintln!("DIAG TOP EVENTS (provider_data1, event_id, count):");
        for (p, id, c) in sess.diag_top_events(25) {
            eprintln!("  0x{p:08X}  id={id:<5}  count={c}");
        }
    }
}
