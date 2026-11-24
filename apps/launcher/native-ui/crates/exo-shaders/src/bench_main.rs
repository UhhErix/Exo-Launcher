use exo_shaders::driver_cache::{default_driver_cache_roots, scan_driver_caches};
use exo_shaders::game_cache::scan_game_directory;
use exo_shaders::policy::AntiCheatClass;
use exo_shaders::pso_dx12::{Dx12WarmContext, recommended_worker_threads};
use exo_shaders::scheduler::{SchedulerLimits, measure_simulated_pause_latency_ms};
use serde_json::json;
use std::path::Path;
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

    let offline_candidates = [
        (
            "steam:1343240",
            "Thymesia",
            r"C:\Program Files (x86)\Steam\steamapps\common\Thymesia",
        ),
        (
            "steam:1110910",
            "Mortal Shell",
            r"C:\Program Files (x86)\Steam\steamapps\common\Mortal Shell",
        ),
        (
            "steam:860510",
            "Little Nightmares II",
            r"C:\Program Files (x86)\Steam\steamapps\common\Little Nightmares II",
        ),
        (
            "steam:1501750",
            "Lords of the Fallen",
            r"C:\Program Files (x86)\Steam\steamapps\common\Lords of the Fallen",
        ),
        (
            "steam:990080",
            "Hogwarts Legacy",
            r"C:\Program Files (x86)\Steam\steamapps\common\Hogwarts Legacy",
        ),
    ];
    let mut game_scans = Vec::new();
    for (id, name, dir) in offline_candidates {
        let p = Path::new(dir);
        if p.exists() {
            game_scans.push(scan_game_directory(
                id,
                name,
                p,
                Some(AntiCheatClass::Offline),
                None,
            ));
        }
    }

    let ctx = Dx12WarmContext::new().expect("DX12 hardware adapter required");
    let pso_count = 500usize;
    let threads = recommended_worker_threads();
    let (blobs, hlsl_ms) =
        Dx12WarmContext::compile_hlsl_corpus(pso_count, threads).expect("HLSL compile");

    let cancel_st = Arc::new(AtomicBool::new(false));
    let mut st_summary = ctx
        .warm_compute_psos(&blobs, 1, &cancel_st, false)
        .expect("single-thread PSO warm");
    st_summary.hlsl_compile_ms = hlsl_ms;

    let cancel_mt = Arc::new(AtomicBool::new(false));
    let mut mt_summary = ctx
        .warm_compute_psos(&blobs, threads, &cancel_mt, true)
        .expect("multi-thread PSO warm");
    mt_summary.hlsl_compile_ms = hlsl_ms;

    let pause_latency_ms = measure_simulated_pause_latency_ms(SchedulerLimits::default());
    mt_summary.pause_latency_ms = Some(pause_latency_ms);

    let scaling = if mt_summary.pso_create_ms > 0.0 {
        st_summary.pso_create_ms / mt_summary.pso_create_ms
    } else {
        0.0
    };
    let warm_speedup = if mt_summary.warm_load_ms > 0.0 {
        mt_summary.pso_create_ms / mt_summary.warm_load_ms
    } else {
        0.0
    };

    let report = json!({
        "idleRssBytesBefore": idle_rss_before,
        "rssBytesAfter": current_rss_bytes(),
        "driverCache": inventory,
        "offlineGameScans": game_scans,
        "dx12SingleThread": st_summary,
        "dx12MultiThread": mt_summary,
        "multiThreadScaling": scaling,
        "warmLibrarySpeedup": warm_speedup,
        "schedulerPauseLatencyMs": pause_latency_ms,
    });
    println!("{}", serde_json::to_string_pretty(&report).unwrap());
}
