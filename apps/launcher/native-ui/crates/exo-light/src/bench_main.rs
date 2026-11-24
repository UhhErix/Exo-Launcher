use exo_light::LightManager;
use exo_light::effects::EffectMode;
use exo_light::transport::{DeviceTier, LightDeviceInfo, MockLampTransport, Rgb8};
use serde_json::json;
use std::time::Instant;
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

fn build_mock_rig() -> LightManager {
    let mut mgr = LightManager::new(
        EffectMode::Static {
            color: Rgb8::EXO_ACCENT,
        },
        80,
    );
    mgr.add_device(Box::new(MockLampTransport::new(
        LightDeviceInfo {
            id: "mock:lamparray-kb".into(),
            name: "Mock LampArray 68-key".into(),
            vendor: "MockVendor".into(),
            tier: DeviceTier::Win11LampArray,
            zone_count: 68,
            wireless: false,
        },
        Rgb8::EXO_ACCENT,
        80,
    )));
    mgr.add_device(Box::new(MockLampTransport::new(
        LightDeviceInfo {
            id: "mock:hid-mouse".into(),
            name: "Mock Wireless Mouse".into(),
            vendor: "MockVendor".into(),
            tier: DeviceTier::UsbHid,
            zone_count: 2,
            wireless: true,
        },
        Rgb8::EXO_ACCENT,
        80,
    )));
    mgr.add_device(Box::new(MockLampTransport::new(
        LightDeviceInfo {
            id: "mock:smbus-ram".into(),
            name: "Mock DDR5 RGB (PawnIO)".into(),
            vendor: "MockVendor".into(),
            tier: DeviceTier::SmbusPawnIo,
            zone_count: 16,
            wireless: false,
        },
        Rgb8::EXO_ACCENT,
        80,
    )));
    mgr
}

fn main() {
    let rss_before = rss_bytes();
    let mut mgr = build_mock_rig();

    let static_commit_writes = mgr.tick(0).expect("static commit");
    let static_cpu_start = process_cpu_us();
    let static_wall_start = Instant::now();
    let mut static_extra_writes = 0usize;
    for frame in 1..=1800u64 {
        static_extra_writes += mgr.tick(frame * 33).expect("static dormant tick");
    }
    let static_wall_us = static_wall_start.elapsed().as_micros() as u64;
    let static_cpu_us = process_cpu_us().saturating_sub(static_cpu_start);

    mgr.set_mode(EffectMode::Breathing {
        color: Rgb8::EXO_ACCENT,
        period_ms: 3000,
    });
    let anim_cpu_start = process_cpu_us();
    let anim_wall_start = Instant::now();
    let mut anim_writes = 0usize;
    for frame in 0..1800u64 {
        anim_writes += mgr.tick(frame * 33).expect("animated tick");
    }
    let anim_wall_us = anim_wall_start.elapsed().as_micros() as u64;
    let anim_cpu_us = process_cpu_us().saturating_sub(anim_cpu_start);
    let simulated_60s_cpu_pct = (anim_cpu_us as f64 / 60_000_000.0) * 100.0;

    mgr.shutdown_restore().expect("restore baseline");
    let rss_after = rss_bytes();

    let out = json!({
        "rssBeforeBytes": rss_before,
        "rssAfterBytes": rss_after,
        "rssDeltaBytes": rss_after.saturating_sub(rss_before),
        "staticMode": {
            "simulatedSeconds": 60,
            "commitWrites": static_commit_writes,
            "subsequentWrites": static_extra_writes,
            "dormantTicks": mgr.dormant_ticks(),
            "wallUs": static_wall_us,
            "cpuUs": static_cpu_us,
            "cpuPctOver60s": (static_cpu_us as f64 / 60_000_000.0) * 100.0,
        },
        "animated30FpsMode": {
            "simulatedSeconds": 60,
            "frames": 1800,
            "deviceWrites": anim_writes,
            "wallUs": anim_wall_us,
            "cpuUs": anim_cpu_us,
            "cpuPctOver60s": simulated_60s_cpu_pct,
        },
    });
    println!("{}", serde_json::to_string_pretty(&out).unwrap());
}
