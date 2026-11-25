use exo_shaders::driver_cache::{default_driver_cache_roots, scan_driver_caches};
use exo_shaders::pso_dx12::{Dx12WarmContext, recommended_worker_threads};
use exo_shaders::pso_store::{RecordedPsoKind, RecordedPsoRecord, compute_pso_key};
use exo_shaders::scheduler::{SchedulerLimits, measure_simulated_pause_latency_ms};
use serde_json::json;
use std::sync::Arc;
use std::sync::atomic::AtomicBool;
use windows::Win32::System::ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
use windows::Win32::System::Threading::GetCurrentProcess;

fn current_rss_bytes() -> u64 {
    unsafe {
        let mut pmc = PROCESS_MEMORY_COUNTERS::default();
        let size = std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32;
        if GetProcessMemoryInfo(GetCurrentProcess(), &mut pmc, size).is_ok() {
            pmc.WorkingSetSize as u64
        } else {
            0
        }
    }
}

fn main() {
    let idle_rss_before = current_rss_bytes();
    let roots = default_driver_cache_roots();
    let inventory = scan_driver_caches(&roots);

    let ctx = Dx12WarmContext::new().expect("DX12 hardware adapter required");
    let root_sig = Dx12WarmContext::benchmark_root_signature_blob().expect("root sig");
    let pso_count = 120usize;
    let threads = recommended_worker_threads();
    let (blobs, hlsl_ms) =
        Dx12WarmContext::compile_hlsl_corpus(pso_count, threads).expect("HLSL compile");
    let records: Vec<RecordedPsoRecord> = blobs
        .into_iter()
        .map(|cs| RecordedPsoRecord {
            key_hex: compute_pso_key(&root_sig, &cs, 0),
            kind: RecordedPsoKind::Compute,
            root_sig_blob: root_sig.clone(),
            vs: Vec::new(),
            ps: Vec::new(),
            ds: Vec::new(),
            hs: Vec::new(),
            gs: Vec::new(),
            cs,
            graphics_state: None,
            node_mask: 0,
        })
        .collect();

    let st_lib = std::env::temp_dir().join(format!("exo-bench-st-{}.exoplib", std::process::id()));
    let mt_lib = std::env::temp_dir().join(format!("exo-bench-mt-{}.exoplib", std::process::id()));
    let cancel_st = Arc::new(AtomicBool::new(false));
    let mut st_summary = ctx
        .compile_recorded_corpus(
            &records,
            &st_lib,
            inventory.gpu.vendor_id,
            inventory.gpu.device_id,
            &inventory.gpu.driver_version,
            1,
            &cancel_st,
            false,
        )
        .expect("single-thread recorded PSO library compile");
    st_summary.hlsl_compile_ms = hlsl_ms;

    let cancel_mt = Arc::new(AtomicBool::new(false));
    let mut mt_summary = ctx
        .compile_recorded_corpus(
            &records,
            &mt_lib,
            inventory.gpu.vendor_id,
            inventory.gpu.device_id,
            &inventory.gpu.driver_version,
            threads,
            &cancel_mt,
            true,
        )
        .expect("multi-thread recorded PSO library compile");
    mt_summary.hlsl_compile_ms = hlsl_ms;

    let _ = std::fs::remove_file(&st_lib);
    let _ = std::fs::remove_file(&mt_lib);

    let pause_latency_ms = measure_simulated_pause_latency_ms(SchedulerLimits::default());
    mt_summary.pause_latency_ms = Some(pause_latency_ms);

    let scaling = if mt_summary.pso_create_ms > 0.0 {
        st_summary.pso_create_ms / mt_summary.pso_create_ms
    } else {
        0.0
    };

    let report = json!({
        "helperImplicitWarmSupported": false,
        "deliveryMechanism": "In-process ID3D12PipelineLibrary (.exoplib) for offline games; zero-hook driver cache hygiene and Vulkan Fossilize replay for all classes.",
        "idleRssBytesBefore": idle_rss_before,
        "rssBytesAfter": current_rss_bytes(),
        "driverCache": inventory,
        "recordedLibrarySingleThread": st_summary,
        "recordedLibraryMultiThread": mt_summary,
        "multiThreadScaling": scaling,
        "schedulerPauseLatencyMs": pause_latency_ms,
    });
    println!("{}", serde_json::to_string_pretty(&report).unwrap());
}
