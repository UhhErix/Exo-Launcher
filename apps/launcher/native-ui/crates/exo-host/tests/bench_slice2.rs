use exo_host::backend::MockHostBackend;
use exo_host::router::{HostRouter, RouteOutcome};
use serde_json::{Value, json};
use std::alloc::{GlobalAlloc, Layout, System};
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

struct CountingAlloc;

static ALLOC_COUNT: AtomicU64 = AtomicU64::new(0);
static ALLOC_BYTES: AtomicU64 = AtomicU64::new(0);

unsafe impl GlobalAlloc for CountingAlloc {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        ALLOC_COUNT.fetch_add(1, Ordering::Relaxed);
        ALLOC_BYTES.fetch_add(layout.size() as u64, Ordering::Relaxed);
        unsafe { System.alloc(layout) }
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        unsafe { System.dealloc(ptr, layout) }
    }

    unsafe fn realloc(&self, ptr: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        ALLOC_COUNT.fetch_add(1, Ordering::Relaxed);
        ALLOC_BYTES.fetch_add(new_size as u64, Ordering::Relaxed);
        unsafe { System.realloc(ptr, layout, new_size) }
    }
}

#[global_allocator]
static GLOBAL: CountingAlloc = CountingAlloc;

fn reset_alloc_counters() {
    ALLOC_COUNT.store(0, Ordering::SeqCst);
    ALLOC_BYTES.store(0, Ordering::SeqCst);
}

fn read_alloc_counters() -> (u64, u64) {
    (
        ALLOC_COUNT.load(Ordering::SeqCst),
        ALLOC_BYTES.load(Ordering::SeqCst),
    )
}

fn load_golden() -> Value {
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let fixture = manifest_dir
        .join("tests")
        .join("fixtures")
        .join("phase2-slice2-golden.json");
    let raw = std::fs::read_to_string(&fixture).expect("read golden");
    serde_json::from_str(&raw).expect("parse golden")
}

fn percentile(sorted: &[f64], p: usize) -> f64 {
    let idx = ((sorted.len() * p) / 100).min(sorted.len().saturating_sub(1));
    sorted[idx]
}

#[test]
#[ignore]
fn bench_slice2_per_family_latency_and_allocations() {
    let golden = load_golden();
    let mut map = HashMap::new();
    for entry in golden["families"].as_object().unwrap().values() {
        map.insert(
            entry["method"].as_str().unwrap().to_owned(),
            entry["recordedDotnet"].clone(),
        );
    }
    let router = HostRouter::new(MockHostBackend::from_recorded_responses(map.clone()));

    let families = [
        ("sensors", "system.health", json!({})),
        ("gpu.profile", "gpu.profile", json!({})),
        ("gpu.processes", "gpu.processes", json!({ "limit": 10 })),
        ("gpu.tuning.state", "gpu.tuning.state", json!({})),
        ("system.about", "system.about", json!({})),
        ("gpu.display", "gpu.display", json!({ "action": "state" })),
        ("hdr.state", "hdr.state", json!({})),
        ("system.drives", "system.drives", json!({})),
    ];

    let rust_runs = 2_000usize;
    println!("--- RUST IN-PROCESS (exo-host, Release, {rust_runs} runs) ---");
    for (label, method, params) in &families {
        for _ in 0..100 {
            let RouteOutcome::Handled(_) = router.dispatch(method, params).unwrap() else {
                panic!("expected Handled");
            };
        }
        let mut samples = Vec::with_capacity(rust_runs);
        reset_alloc_counters();
        for _ in 0..rust_runs {
            let t0 = Instant::now();
            let RouteOutcome::Handled(v) = router.dispatch(method, params).unwrap() else {
                panic!("expected Handled");
            };
            std::hint::black_box(&v);
            samples.push(t0.elapsed().as_secs_f64() * 1_000_000.0);
        }
        let (allocs, bytes) = read_alloc_counters();
        samples.sort_by(|a, b| a.total_cmp(b));
        println!(
            "RUST  {label:<18} p50={:>6.2}us  p99={:>6.2}us  allocs/call={:>6.1}  bytes/call={:>8.1}",
            percentile(&samples, 50),
            percentile(&samples, 99),
            (allocs as f64) / (rust_runs as f64),
            (bytes as f64) / (rust_runs as f64),
        );
    }

    #[cfg(windows)]
    if let Ok(live_pipe) = std::env::var("EXO_BENCH_LIVE_DOTNET_PIPE") {
        let client = exo_rpc::RpcClient::connect_with_preference(
            &live_pipe,
            Duration::from_secs(10),
            exo_rpc::WirePreference::Json,
            |_| {},
        )
        .expect("connect live dotnet host pipe");
        let pipe_runs = 50usize;
        println!("--- LIVE .NET NAMED PIPE (ExoLauncher.Host, {pipe_runs} runs) ---");
        for (label, method, params) in &families {
            for _ in 0..3 {
                let _: Value = client
                    .call_blocking(method, params, Duration::from_secs(15))
                    .expect("warmup dotnet pipe");
            }
            let mut samples = Vec::with_capacity(pipe_runs);
            reset_alloc_counters();
            for _ in 0..pipe_runs {
                let t0 = Instant::now();
                let v: Value = client
                    .call_blocking(method, params, Duration::from_secs(15))
                    .expect("dotnet pipe call");
                std::hint::black_box(&v);
                samples.push(t0.elapsed().as_secs_f64() * 1_000_000.0);
            }
            let (allocs, bytes) = read_alloc_counters();
            samples.sort_by(|a, b| a.total_cmp(b));
            println!(
                "PIPE  {label:<18} p50={:>8.2}us  p99={:>8.2}us  client_allocs/call={:>6.1}  client_bytes/call={:>8.1}",
                percentile(&samples, 50),
                percentile(&samples, 99),
                (allocs as f64) / (pipe_runs as f64),
                (bytes as f64) / (pipe_runs as f64),
            );
        }
        client.close();
    }
}
