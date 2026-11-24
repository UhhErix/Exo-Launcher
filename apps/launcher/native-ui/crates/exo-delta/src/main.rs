use exo_delta::{
    apply_delta_pack, build_delta_pack, build_full_pack, decode_hex, encode_hex, sign_pack,
    verify_directory_against_pack,
};
use std::env;
use std::fs;
use std::path::PathBuf;
use std::time::Instant;

fn arg_val(args: &[String], flag: &str) -> Option<String> {
    args.windows(2).find(|w| w[0] == flag).map(|w| w[1].clone())
}

fn read_sig_file(path: &str) -> Result<[u8; 64], String> {
    let raw = fs::read(path).map_err(|e| format!("read sig {path}: {e}"))?;
    if raw.len() == 64 {
        let mut out = [0u8; 64];
        out.copy_from_slice(&raw);
        return Ok(out);
    }
    let text = std::str::from_utf8(&raw).map_err(|_| "sig file not utf8".to_string())?;
    decode_hex::<64>(text).map_err(|e| e.to_string())
}

fn resolve_sign_seed(args: &[String]) -> Result<Option<[u8; 32]>, String> {
    if let Some(var_name) = arg_val(args, "--sign-key-env") {
        let hex = env::var(&var_name)
            .map_err(|_| format!("signing key env var '{var_name}' is not set"))?;
        let seed = decode_hex::<32>(hex.trim()).map_err(|e| e.to_string())?;
        return Ok(Some(seed));
    }
    if let Some(seed_hex) = arg_val(args, "--sign-key-hex") {
        let seed = decode_hex::<32>(&seed_hex).map_err(|e| e.to_string())?;
        return Ok(Some(seed));
    }
    Ok(None)
}

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 2 {
        eprintln!(
            "usage:\n  exo-delta bench <old_dir> <new_dir> [level]\n  exo-delta build-delta <old_dir> <new_dir> <out_pack> [--level N] [--sign-key-env VAR | --sign-key-hex HEX] [--sig-out PATH]\n  exo-delta build-full <new_dir> <out_pack> [--level N] [--sign-key-env VAR | --sign-key-hex HEX] [--sig-out PATH]\n  exo-delta sign <pack> (--sign-key-env VAR | --sign-key-hex HEX) [--sig-out PATH]\n  exo-delta apply <base_dir> <pack> <out_dir> [--sig PATH --pubkey-hex HEX]\n  exo-delta verify <dir> <pack> [--sig PATH --pubkey-hex HEX]"
        );
        std::process::exit(2);
    }

    let res = run(&args);
    if let Err(err) = res {
        eprintln!("exo-delta error: {err}");
        std::process::exit(1);
    }
}

fn run(args: &[String]) -> Result<(), String> {
    match args[1].as_str() {
        "bench" => {
            if args.len() < 4 {
                Err("bench requires <old_dir> <new_dir> [level]".into())
            } else {
                let old_dir = PathBuf::from(&args[2]);
                let new_dir = PathBuf::from(&args[3]);
                let level: i32 = args.get(4).and_then(|s| s.parse().ok()).unwrap_or(12);
                let t_build = Instant::now();
                let (pack, stats) =
                    build_delta_pack(&old_dir, &new_dir, level).map_err(|e| e.to_string())?;
                let build_ms = t_build.elapsed().as_secs_f64() * 1000.0;

                let t_apply = Instant::now();
                let summary = apply_delta_pack(Some(&old_dir), &pack, None, None)
                    .map_err(|e| e.to_string())?;
                let apply_ms = t_apply.elapsed().as_secs_f64() * 1000.0;

                let savings = if stats.file_level_zstd_bytes > 0 {
                    100.0
                        * (1.0
                            - (stats.delta_wire_bytes as f64 / stats.file_level_zstd_bytes as f64))
                } else {
                    0.0
                };
                println!(
                    "files={} (reuse={} patch={} full={}) | raw_total={:.2} MiB | changed_raw={:.2} MiB",
                    stats.total_files,
                    stats.reused_files,
                    stats.patched_files,
                    stats.full_files,
                    stats.total_raw_bytes as f64 / 1_048_576.0,
                    stats.changed_raw_bytes as f64 / 1_048_576.0
                );
                println!(
                    "file_level_zstd(L{})={:.2} MiB | exo_delta_wire={:.2} MiB ({:.1}% reduction) | build={:.0} ms | apply+BLAKE3_verify({} files)={:.0} ms",
                    level,
                    stats.file_level_zstd_bytes as f64 / 1_048_576.0,
                    stats.delta_wire_bytes as f64 / 1_048_576.0,
                    savings,
                    build_ms,
                    summary.files_applied,
                    apply_ms
                );
                Ok(())
            }
        }
        "build-delta" => {
            if args.len() < 5 {
                Err("build-delta requires <old_dir> <new_dir> <out_pack>".into())
            } else {
                let level = arg_val(args, "--level")
                    .and_then(|s| s.parse().ok())
                    .unwrap_or(12);
                let (pack, stats) =
                    build_delta_pack(&PathBuf::from(&args[2]), &PathBuf::from(&args[3]), level)
                        .map_err(|e| e.to_string())?;
                fs::write(&args[4], &pack).map_err(|e| e.to_string())?;
                if let Some(seed) = resolve_sign_seed(args)? {
                    let (sig, pubkey) = sign_pack(&pack, &seed).map_err(|e| e.to_string())?;
                    let sig_path =
                        arg_val(args, "--sig-out").unwrap_or_else(|| format!("{}.sig", args[4]));
                    fs::write(&sig_path, encode_hex(&sig)).map_err(|e| e.to_string())?;
                    println!("signed={} pubkey={}", sig_path, encode_hex(&pubkey));
                }
                println!(
                    "delta_pack={} bytes={} files={} reuse={} patch={} full={} tree_blake3={}",
                    args[4],
                    stats.delta_wire_bytes,
                    stats.total_files,
                    stats.reused_files,
                    stats.patched_files,
                    stats.full_files,
                    encode_hex(&stats.tree_blake3)
                );
                Ok(())
            }
        }
        "build-full" => {
            if args.len() < 4 {
                Err("build-full requires <new_dir> <out_pack>".into())
            } else {
                let level = arg_val(args, "--level")
                    .and_then(|s| s.parse().ok())
                    .unwrap_or(12);
                let (pack, stats) =
                    build_full_pack(&PathBuf::from(&args[2]), level).map_err(|e| e.to_string())?;
                fs::write(&args[3], &pack).map_err(|e| e.to_string())?;
                if let Some(seed) = resolve_sign_seed(args)? {
                    let (sig, pubkey) = sign_pack(&pack, &seed).map_err(|e| e.to_string())?;
                    let sig_path =
                        arg_val(args, "--sig-out").unwrap_or_else(|| format!("{}.sig", args[3]));
                    fs::write(&sig_path, encode_hex(&sig)).map_err(|e| e.to_string())?;
                    println!("signed={} pubkey={}", sig_path, encode_hex(&pubkey));
                }
                println!(
                    "full_pack={} bytes={} files={} tree_blake3={}",
                    args[3],
                    stats.delta_wire_bytes,
                    stats.total_files,
                    encode_hex(&stats.tree_blake3)
                );
                Ok(())
            }
        }
        "sign" => {
            if args.len() < 3 {
                Err("sign requires <pack> (--sign-key-env VAR | --sign-key-hex HEX)".into())
            } else {
                let seed = resolve_sign_seed(args)?.ok_or_else(|| {
                    "sign requires --sign-key-env VAR or --sign-key-hex HEX".to_string()
                })?;
                let pack = fs::read(&args[2]).map_err(|e| e.to_string())?;
                let (sig, pubkey) = sign_pack(&pack, &seed).map_err(|e| e.to_string())?;
                let sig_path =
                    arg_val(args, "--sig-out").unwrap_or_else(|| format!("{}.sig", args[2]));
                fs::write(&sig_path, encode_hex(&sig)).map_err(|e| e.to_string())?;
                println!("signed={} pubkey={}", sig_path, encode_hex(&pubkey));
                Ok(())
            }
        }
        "apply" => {
            if args.len() < 5 {
                Err("apply requires <base_dir> <pack> <out_dir>".into())
            } else {
                let pack = fs::read(&args[3]).map_err(|e| e.to_string())?;
                let sig_pair = match (arg_val(args, "--sig"), arg_val(args, "--pubkey-hex")) {
                    (Some(s), Some(p)) => {
                        let sig = read_sig_file(&s)?;
                        let pk = decode_hex::<32>(&p).map_err(|e| e.to_string())?;
                        Some((sig, pk))
                    }
                    _ => None,
                };
                let base_path = PathBuf::from(&args[2]);
                let base = if base_path.exists() {
                    Some(base_path.as_path())
                } else {
                    None
                };
                let out_path = PathBuf::from(&args[4]);
                let summary = apply_delta_pack(
                    base,
                    &pack,
                    Some(&out_path),
                    sig_pair.as_ref().map(|(s, p)| (s, p)),
                )
                .map_err(|e| e.to_string())?;
                println!(
                    "applied={} hardlinked={} patched={} full={} tree_blake3={}",
                    summary.files_applied,
                    summary.hardlinked_files,
                    summary.patched_files,
                    summary.full_files,
                    encode_hex(&summary.tree_blake3)
                );
                Ok(())
            }
        }
        "verify" => {
            if args.len() < 4 {
                Err("verify requires <dir> <pack>".into())
            } else {
                let pack = fs::read(&args[3]).map_err(|e| e.to_string())?;
                let sig_pair = match (arg_val(args, "--sig"), arg_val(args, "--pubkey-hex")) {
                    (Some(s), Some(p)) => {
                        let sig = read_sig_file(&s)?;
                        let pk = decode_hex::<32>(&p).map_err(|e| e.to_string())?;
                        Some((sig, pk))
                    }
                    _ => None,
                };
                let hash = verify_directory_against_pack(
                    &PathBuf::from(&args[2]),
                    &pack,
                    sig_pair.as_ref().map(|(s, p)| (s, p)),
                )
                .map_err(|e| e.to_string())?;
                println!("verified tree_blake3={}", encode_hex(&hash));
                Ok(())
            }
        }
        other => Err(format!("unknown subcommand: {other}")),
    }
}
