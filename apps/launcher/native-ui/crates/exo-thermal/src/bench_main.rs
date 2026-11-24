use exo_thermal::ThermalDaemon;
use exo_thermal::backend::{FanChannelInfo, FanDomain, MockFanBackend, SensorKind};
use exo_thermal::curve::{FanCurveConfig, MixMode};
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

fn main() {
    let rss_before = rss_bytes();
    let channels = vec![
        FanChannelInfo {
            id: "gpu:0".into(),
            name: "Mock RTX 3070 Fan 0".into(),
            domain: FanDomain::GpuNvidia,
            supports_zero_rpm: true,
            is_cpu_domain: false,
            calibrated_min_rpm: 600,
        },
        FanChannelInfo {
            id: "case:intake".into(),
            name: "Mock Case Intake".into(),
            domain: FanDomain::UsbAio,
            supports_zero_rpm: false,
            is_cpu_domain: true,
            calibrated_min_rpm: 500,
        },
    ];
    let mock = MockFanBackend::new(channels, false);
    let state = mock.state_handle();

    let cpu_start = process_cpu_us();
    let wall_start = Instant::now();
    let mut false_rev_ups = 0usize;
    let mut sustained_ramp_ticks = 0usize;

    {
        let mut daemon = ThermalDaemon::new(mock);
        daemon.bind_curve("gpu:0", FanCurveConfig::default());
        daemon.bind_curve(
            "case:intake",
            FanCurveConfig {
                mix_mode: MixMode::Max,
                sensors: vec![(SensorKind::CpuTctl, 1.0), (SensorKind::GpuHotspot, 1.0)],
                is_cpu_domain: true,
                spike_hold_ticks: 3,
                ..Default::default()
            },
        );

        for t in 0..4u64 {
            let ms = t * 1000;
            {
                let mut st = state.lock().unwrap();
                st.set_sensor(SensorKind::GpuCore, 42.0, ms);
                st.set_sensor(SensorKind::GpuHotspot, 50.0, ms);
                st.set_sensor(SensorKind::CpuTctl, 45.0, ms);
            }
            daemon.tick_at(ms).unwrap();
        }
        let baseline_case = state.lock().unwrap().current_pwm["case:intake"];

        for tick in 0..300usize {
            let ms = (tick as u64 + 5) * 1000;
            {
                let mut st = state.lock().unwrap();
                st.set_sensor(SensorKind::GpuHotspot, 50.0, ms);
                if tick == 20 || tick == 21 {
                    st.set_sensor(SensorKind::CpuTctl, 60.0, ms);
                    st.set_sensor(SensorKind::GpuCore, 42.0, ms);
                } else if (100..120).contains(&tick) {
                    st.set_sensor(SensorKind::CpuTctl, 74.0, ms);
                    st.set_sensor(SensorKind::GpuCore, 72.0, ms);
                } else {
                    st.set_sensor(SensorKind::CpuTctl, 45.0, ms);
                    st.set_sensor(SensorKind::GpuCore, 42.0, ms);
                }
            }
            daemon.tick_at(ms).unwrap();
            let cur = state.lock().unwrap().current_pwm["case:intake"];
            if (tick == 20 || tick == 21) && cur > baseline_case {
                false_rev_ups += 1;
            }
            if (100..120).contains(&tick) && sustained_ramp_ticks == 0 && cur > baseline_case {
                sustained_ramp_ticks = tick - 100 + 1;
            }
        }
    }

    let wall_us = wall_start.elapsed().as_micros() as u64;
    let cpu_us = process_cpu_us().saturating_sub(cpu_start);
    let rss_after = rss_bytes();
    let restored = state.lock().unwrap().restored_channels.clone();

    let report = json!({
        "rssBeforeBytes": rss_before,
        "rssAfterBytes": rss_after,
        "rssDeltaBytes": rss_after.saturating_sub(rss_before),
        "simulatedTicks": 300,
        "simulatedSeconds": 300,
        "wallUs": wall_us,
        "cpuUs": cpu_us,
        "cpuPctOver300s": (cpu_us as f64 / 300_000_000.0) * 100.0,
        "falseRevUpsOn2s15CSpike": false_rev_ups,
        "sustainedLoadRampSeconds": sustained_ramp_ticks,
        "biosRestoredChannelsOnDrop": restored,
    });
    println!("{}", serde_json::to_string_pretty(&report).unwrap());
}
