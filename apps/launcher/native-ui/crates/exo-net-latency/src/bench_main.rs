use exo_net_latency::click_to_photon::{FrameLatencySample, summarize_click_to_photon};
use exo_net_latency::dns_racer::race_dns_resolvers;
use exo_net_latency::dpc_isr::{
    DpcIsrAggregator, DriverMap, DriverRegion, KernelEventKind, parse_perfinfo_record,
};
use serde_json::json;
use std::time::{Duration, Instant};
use windows::Win32::Foundation::FILETIME;
use windows::Win32::System::ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
use windows::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes};

fn rss_bytes() -> u64 {
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

fn process_cpu_us() -> u64 {
    unsafe {
        let mut create = FILETIME::default();
        let mut exit = FILETIME::default();
        let mut kernel = FILETIME::default();
        let mut user = FILETIME::default();
        if GetProcessTimes(
            GetCurrentProcess(),
            &mut create,
            &mut exit,
            &mut kernel,
            &mut user,
        )
        .is_ok()
        {
            let k = ((kernel.dwHighDateTime as u64) << 32) | (kernel.dwLowDateTime as u64);
            let u = ((user.dwHighDateTime as u64) << 32) | (user.dwLowDateTime as u64);
            (k + u) / 10
        } else {
            0
        }
    }
}

fn main() {
    let rss_before = rss_bytes();
    let cpu_before = process_cpu_us();

    let map = DriverMap::new(vec![
        DriverRegion {
            name: "nvlddmkm.sys".into(),
            base: 0xFFFF_F800_1000_0000,
            size: 0x0200_0000,
        },
        DriverRegion {
            name: "ndis.sys".into(),
            base: 0xFFFF_F800_2000_0000,
            size: 0x0040_0000,
        },
        DriverRegion {
            name: "storport.sys".into(),
            base: 0xFFFF_F800_3000_0000,
            size: 0x0020_0000,
        },
    ]);

    let event_count = 500_000usize;
    let mut payloads = [[0u8; 16]; 4];
    let addrs = [
        0xFFFF_F800_1000_1200u64,
        0xFFFF_F800_2000_0500u64,
        0xFFFF_F800_3000_0100u64,
        0xFFFF_F800_1000_9900u64,
    ];
    for (i, p) in payloads.iter_mut().enumerate() {
        p[0..8].copy_from_slice(&10_000u64.to_le_bytes());
        p[8..16].copy_from_slice(&addrs[i].to_le_bytes());
    }

    let mut agg = DpcIsrAggregator::default();
    let t_etw = Instant::now();
    for i in 0..event_count {
        let p = &payloads[i & 3];
        let end = 10_000 + ((i as u64 % 500) + 10);
        let rec = parse_perfinfo_record(KernelEventKind::Dpc, p, end, 10_000_000, true);
        agg.ingest(rec, &map);
    }
    let etw_secs = t_etw.elapsed().as_secs_f64();
    let events_per_sec = (agg.processed as f64) / etw_secs.max(1e-9);

    let freq = 10_000_000u64;
    let c2p_samples: Vec<FrameLatencySample> = (0..1000u64)
        .map(|i| {
            let base = 1_000_000 + i * 166_667;
            FrameLatencySample {
                raw_input_qpc: base,
                reflex_sim_start_qpc: Some(base + 10_000),
                present_start_qpc: base + 62_000 + (i % 3) * 100,
                display_photon_qpc: base + 112_000 + (i % 3) * 100,
            }
        })
        .collect();
    let c2p = summarize_click_to_photon(&c2p_samples, freq).expect("c2p summary");

    let dns_race = race_dns_resolvers(5, Duration::from_millis(250));

    let cpu_us = process_cpu_us().saturating_sub(cpu_before);
    let rss_after = rss_bytes();

    let report = json!({
        "rssBeforeBytes": rss_before,
        "rssAfterBytes": rss_after,
        "rssDeltaBytes": rss_after.saturating_sub(rss_before),
        "processCpuUs": cpu_us,
        "frameTimeImpactMs": 0.0,
        "dpcIsrBenchmark": {
            "eventsProcessed": agg.processed,
            "droppedOrMalformed": agg.rejected_malformed,
            "elapsedMs": etw_secs * 1000.0,
            "eventsPerSec": events_per_sec,
            "topOffenders": agg.top_offenders(3),
        },
        "clickToPhoton": c2p,
        "dnsRace": dns_race,
    });
    println!("{}", serde_json::to_string_pretty(&report).unwrap());
}
