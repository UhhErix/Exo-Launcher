use exo_gpu::nvidia_drs::read_global_drs_readonly;
use exo_gpu::read_gpu_control_overview;
use exo_gpu::undervolt::{MockGpuBackend, TuneLimits, run_opt_in_undervolt_search};

fn main() {
    let ov = read_gpu_control_overview();
    println!("=== EXO-GPU READ-ONLY OVERVIEW ===");
    if let Some(g) = &ov.gpu {
        println!(
            "gpu=\"{}\" backend={} driver={:?} clocks={:?}/{:?}MHz power={:?}W/{:?}W temp={:?}C pcie=Gen{:?}/{:?} x{:?}/{:?}",
            g.name,
            g.backend.as_str(),
            g.driver_version,
            g.gpu_clock_mhz,
            g.mem_clock_mhz,
            g.board_power_w,
            g.enforced_power_limit_w,
            g.core_temp_c,
            g.pcie_gen_current,
            g.pcie_gen_max,
            g.pcie_width_current,
            g.pcie_width_max
        );
    }
    if let Some(drs) = &ov.nvidia_drs {
        println!("drs_first_read_ms={:.2}", drs.read_latency_ms);
        for s in &drs.settings {
            println!(
                "  0x{:08X} {}: {} (override={})",
                s.id, s.label, s.display_value, s.is_user_override
            );
        }
        let mut times = Vec::new();
        for _ in 0..10 {
            if let Some(d) = read_global_drs_readonly() {
                times.push(d.read_latency_ms);
            }
        }
        if !times.is_empty() {
            let avg = times.iter().sum::<f64>() / times.len() as f64;
            println!("drs_avg_10_reads_ms={:.2}", avg);
        }
    }
    println!("boot_crash_guard={:?}", ov.boot_crash_guard);

    let mut mock = MockGpuBackend::default();
    let uv = run_opt_in_undervolt_search(&mut mock, TuneLimits::default(), true, 15).unwrap();
    println!(
        "mock_undervolt applied={}mV watts={:.1}W->{:.1}W temp={:.1}C->{:.1}C backoff={:?}",
        uv.applied_voltage_offset_mv,
        uv.before_watts,
        uv.after_watts,
        uv.before_temp_c,
        uv.after_temp_c,
        uv.aborted_reason
    );
}
