use exo_delta::{apply_delta_pack, build_delta_pack};
use std::env;
use std::path::PathBuf;
use std::time::Instant;

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 3 {
        eprintln!("usage: exo-delta <old_dir> <new_dir> [level]");
        std::process::exit(2);
    }
    let old_dir = PathBuf::from(&args[1]);
    let new_dir = PathBuf::from(&args[2]);
    let level: i32 = args.get(3).and_then(|s| s.parse().ok()).unwrap_or(12);

    let t_build = Instant::now();
    let (pack, stats) = build_delta_pack(&old_dir, &new_dir, level).expect("build_delta_pack");
    let build_ms = t_build.elapsed().as_secs_f64() * 1000.0;

    let t_apply = Instant::now();
    let verified = apply_delta_pack(&old_dir, &pack, None).expect("apply_delta_pack");
    let apply_ms = t_apply.elapsed().as_secs_f64() * 1000.0;

    let savings = if stats.file_level_zstd_bytes > 0 {
        100.0 * (1.0 - (stats.delta_wire_bytes as f64 / stats.file_level_zstd_bytes as f64))
    } else {
        0.0
    };

    println!(
        "files={} (reuse={} patch={} full={}) | raw_total={:.2} MB | changed_raw={:.2} MB",
        stats.total_files,
        stats.reused_files,
        stats.patched_files,
        stats.full_files,
        stats.total_raw_bytes as f64 / 1_048_576.0,
        stats.changed_raw_bytes as f64 / 1_048_576.0
    );
    println!(
        "file_level_zstd(L{})={:.2} MB | exo_delta_wire={:.2} MB ({:.1}% reduction!) | build={:.0} ms | apply+BLAKE3_verify({} files)={:.0} ms",
        level,
        stats.file_level_zstd_bytes as f64 / 1_048_576.0,
        stats.delta_wire_bytes as f64 / 1_048_576.0,
        savings,
        build_ms,
        verified,
        apply_ms
    );
}
