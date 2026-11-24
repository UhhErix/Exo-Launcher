use std::env;
use std::time::Instant;

use exo_sensors::adaptive::run_full_hardware_audit;
use exo_sensors::gpu::discover_primary_gpu;
use exo_sensors::win32::{current_process_cpu_ms, current_process_memory};

fn main() {
    let cmd = env::args().nth(1).unwrap_or_else(|| "audit".into());
    match cmd.as_str() {
        "audit" => cmd_audit(),
        "bench" => cmd_bench(),
        other => {
            eprintln!("Usage: exo-sensors [audit | bench] (unknown: {other})");
            std::process::exit(2);
        }
    }
}

fn cmd_audit() {
    let mem_before = current_process_memory();
    let audit = run_full_hardware_audit();
    let mem_after = current_process_memory();

    println!("=== EXO-SENSORS FULL HARDWARE AUDIT ===");
    println!("audit_cold_ms={:.2}", audit.audit_duration_ms);
    if let (Some(b), Some(a)) = (mem_before, mem_after) {
        println!(
            "memory_mb before_ws={:.2} after_ws={:.2} after_private={:.2}",
            b.working_set_mb, a.working_set_mb, a.private_mb
        );
    }
    if let Some(sb) = &audit.smbios {
        println!(
            "smbios board=\"{} {}\" bios=\"{}\" cpu=\"{}\" ram={}GB dimms={} single_channel={} xmp_mismatch={:?}",
            sb.board_maker.as_deref().unwrap_or("?"),
            sb.board_product.as_deref().unwrap_or("?"),
            sb.bios_version.as_deref().unwrap_or("?"),
            sb.cpu_model.as_deref().unwrap_or("?"),
            sb.total_ram_gb(),
            sb.dimms.len(),
            sb.is_single_channel_desktop(),
            sb.xmp_expo_mismatch()
        );
        for d in &sb.dimms {
            println!(
                "  dimm loc=\"{}\" bank=\"{}\" {} {}MB rated={}MT/s configured={}MT/s",
                d.device_locator,
                d.bank_locator,
                d.memory_type,
                d.size_mb,
                d.speed_mts,
                d.configured_mts
            );
        }
    }
    if let Some(g) = &audit.gpu {
        println!(
            "gpu name=\"{}\" backend={} driver={:?} temp={:?}C power={:?}W/{:?}W pcie=Gen{:?}/{:?} x{:?}/{:?} throttle=0x{:X}",
            g.name,
            g.backend.as_str(),
            g.driver_version,
            g.core_temp_c,
            g.board_power_w,
            g.enforced_power_limit_w,
            g.pcie_gen_current,
            g.pcie_gen_max,
            g.pcie_width_current,
            g.pcie_width_max,
            g.throttle_flags
        );
    }
    for d in &audit.drives {
        println!(
            "drive #{} model=\"{}\" nvme={} temp={:?}C warn={:?}C life_used={:?}% spare={:?}% throttle_events={} throttle_s={} media_err={}",
            d.disk_number,
            d.model,
            d.is_nvme,
            d.temp_c,
            d.warn_temp_c,
            d.life_used_pct,
            d.spare_pct,
            d.throttle_events,
            d.throttle_seconds,
            d.media_errors
        );
    }
    println!(
        "stability_24h whea={} gpu_tdr={} kp41={} bugcheck={}",
        audit.stability_24h.whea_errors,
        audit.stability_24h.gpu_tdr_resets,
        audit.stability_24h.kernel_power_41,
        audit.stability_24h.bugcheck_1001
    );
    println!(
        "pawnio installed={} loadable={} note=\"{}\"",
        audit.pawnio.installed, audit.pawnio.dll_loadable, audit.pawnio.note
    );
    println!("sentinel_findings={}", audit.findings.len());
    for f in &audit.findings {
        println!(
            "  [{}] {}: {} ({})",
            f.severity.as_str(),
            f.kind.as_str(),
            f.detail,
            f.fix_hint
        );
    }
}

fn cmd_bench() {
    let Some(mut gpu) = discover_primary_gpu() else {
        eprintln!("No GPU backend detected.");
        std::process::exit(1);
    };
    let _ = gpu.read();
    let iters = 200usize;
    let mut times_us = Vec::with_capacity(iters);
    let cpu0 = current_process_cpu_ms().unwrap_or(0.0);
    for _ in 0..iters {
        let t0 = Instant::now();
        let _ = gpu.read();
        times_us.push(t0.elapsed().as_secs_f64() * 1_000_000.0);
    }
    let cpu1 = current_process_cpu_ms().unwrap_or(cpu0);
    times_us.sort_by(|a, b| a.total_cmp(b));
    let mean = times_us.iter().sum::<f64>() / iters as f64;
    let p50 = times_us[iters / 2];
    let p99 = times_us[(iters * 99) / 100];
    println!(
        "gpu_poll_bench backend={} iters={} mean_us={:.1} p50_us={:.1} p99_us={:.1} cpu_1hz_pct={:.4}% proc_cpu_ms={:.2}",
        gpu.kind().as_str(),
        iters,
        mean,
        p50,
        p99,
        (mean / 1_000_000.0) * 100.0,
        (cpu1 - cpu0).max(0.0)
    );
    if let Some(m) = current_process_memory() {
        println!(
            "process_memory_mb ws={:.2} private={:.2}",
            m.working_set_mb, m.private_mb
        );
    }
}
