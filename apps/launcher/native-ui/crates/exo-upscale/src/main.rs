use exo_upscale::catalog::{generate_local_manifest, official_upstream_sources};
use exo_upscale::drs_override::{
    DlssDrsPreset, build_drs_override_plan, probe_nvidia_drs_readonly,
};
use exo_upscale::policy::{ModActionKind, PolicyContext};
use exo_upscale::scanner::{create_mock_50_game_tree, scan_library_parallel};
use exo_upscale::session::SessionMods;
use exo_upscale::verify::{Vendor, inspect_dll, verify_vendor_dll};
use serde::Serialize;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Instant;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct BenchReport {
    nvidia_drs_probe: exo_upscale::drs_override::NvidiaDrsProbe,
    drs_plan_competitive_pvp: exo_upscale::drs_override::DrsOverridePlan,
    mock_50_game_scan_ms: f64,
    mock_50_game_dlls_found: usize,
    real_signed_dll_checked: Option<String>,
    real_signed_dll_publisher: Option<String>,
    real_signed_dll_version: Option<String>,
    real_signed_dll_verify_ms: Option<f64>,
    real_signed_dll_valid: bool,
    tampered_1byte_rejected: bool,
    crash_orphan_recovery_100pct: bool,
    local_manifest_integrity_ok: bool,
    upstream_sources: Vec<exo_upscale::catalog::UpstreamReleaseSource>,
}

fn run_bench(out_dir: &Path) -> Result<BenchReport, String> {
    fs::create_dir_all(out_dir).map_err(|e| format!("create_dir_all: {e}"))?;
    let nvidia_drs_probe = probe_nvidia_drs_readonly();
    let drs_plan_competitive_pvp = build_drs_override_plan(
        PolicyContext::competitive_pvp(),
        DlssDrsPreset::TransformerK,
        true,
        true,
        false,
    );

    let tmp_root = std::env::temp_dir().join(format!("exo-upscale-bench-{}", std::process::id()));
    let _ = fs::remove_dir_all(&tmp_root);
    let mock_roots = create_mock_50_game_tree(&tmp_root.join("mock-50-games"))?;
    let scan = scan_library_parallel(&mock_roots);

    let nvapi = PathBuf::from(r"C:\Windows\System32\nvapi64.dll");
    let (checked, publisher, version, verify_ms, valid, tampered_rejected) = if nvapi.exists() {
        let t0 = Instant::now();
        let res = verify_vendor_dll(&nvapi, Vendor::Nvidia, None);
        let ms = (t0.elapsed().as_secs_f64() * 1000.0 * 100.0).round() / 100.0;
        let (ok, pub_name, ver) = match res {
            Ok(v) => (v.authenticode_valid, v.publisher, v.file_version),
            Err(_) => (false, None, None),
        };
        let copy_path = tmp_root.join("nvapi64_tamper.dll");
        let mut bytes = fs::read(&nvapi).map_err(|e| e.to_string())?;
        let half = bytes.len() / 2;
        if let Some(mid) = bytes.get_mut(half) {
            *mid ^= 0xFF;
        }
        fs::write(&copy_path, &bytes).map_err(|e| e.to_string())?;
        let rej = verify_vendor_dll(&copy_path, Vendor::Nvidia, None).is_err();
        (
            Some(nvapi.display().to_string()),
            pub_name,
            ver,
            Some(ms),
            ok,
            rej,
        )
    } else {
        (None, None, None, None, false, false)
    };

    let sess_dir = tmp_root.join("crash-harness");
    let game_dir = sess_dir.join("game");
    let stage_dir = sess_dir.join("stage");
    fs::create_dir_all(&game_dir).map_err(|e| e.to_string())?;
    fs::create_dir_all(&stage_dir).map_err(|e| e.to_string())?;
    let orig_dll = game_dir.join("nvngx_dlss.dll");
    let orig_bytes = b"ORIGINAL-BENCH-DLSS-BYTES-0123456789";
    fs::write(&orig_dll, orig_bytes).map_err(|e| e.to_string())?;
    let stage_dll = stage_dir.join("nvngx_dlss.dll");
    let stage_proxy = stage_dir.join("version.dll");
    fs::write(&stage_dll, b"NEW-BENCH-DLSS").map_err(|e| e.to_string())?;
    fs::write(&stage_proxy, b"NEW-BENCH-PROXY").map_err(|e| e.to_string())?;
    let proxy_dst = game_dir.join("version.dll");

    let mut child = Command::new("cmd")
        .args(["/C", "ping -n 30 127.0.0.1 > nul"])
        .spawn()
        .map_err(|e| format!("spawn harness child: {e}"))?;
    let mods = SessionMods::new(sess_dir.join("sessions"), sess_dir.join("backups"))?;
    let _ = mods.begin_session(
        "bench-crash",
        "steam:814380",
        child.id(),
        PolicyContext::no_anti_cheat(),
        ModActionKind::FgBridgeProxy,
        &[
            (stage_dll, orig_dll.clone()),
            (stage_proxy, proxy_dst.clone()),
        ],
    )?;
    let _ = child.kill();
    let _ = child.wait();
    let rec = mods.recover_orphaned_sessions();
    let crash_ok = rec.journals_recovered == 1
        && rec.files_removed == 1
        && rec.originals_restored == 1
        && rec.errors.is_empty()
        && !proxy_dst.exists()
        && fs::read(&orig_dll)
            .map(|b| b == orig_bytes)
            .unwrap_or(false);

    let local_manifest_path = out_dir.join("local-manifest.json");
    let manifest = generate_local_manifest(
        &tmp_root.join("empty-sdk"),
        &local_manifest_path,
        "local-v1",
    )?;
    let manifest_ok = manifest.verify_integrity();

    let _ = fs::remove_dir_all(&tmp_root);

    let report = BenchReport {
        nvidia_drs_probe,
        drs_plan_competitive_pvp,
        mock_50_game_scan_ms: scan.elapsed_ms,
        mock_50_game_dlls_found: scan.dlls_found,
        real_signed_dll_checked: checked,
        real_signed_dll_publisher: publisher,
        real_signed_dll_version: version,
        real_signed_dll_verify_ms: verify_ms,
        real_signed_dll_valid: valid,
        tampered_1byte_rejected: tampered_rejected,
        crash_orphan_recovery_100pct: crash_ok,
        local_manifest_integrity_ok: manifest_ok,
        upstream_sources: official_upstream_sources(),
    };
    let json = serde_json::to_string_pretty(&report).map_err(|e| e.to_string())?;
    fs::write(out_dir.join("bench.json"), json.as_bytes()).map_err(|e| e.to_string())?;
    Ok(report)
}

fn main() {
    let mut args = std::env::args().skip(1);
    let Some(cmd) = args.next() else {
        eprintln!(
            "usage: exo-upscale [--bench --out-dir <dir> | --inspect-drs | --verify <dll> | --generate-manifest <scan-dir> <out-json> | --recover-orphans <state-dir> <backup-dir>]"
        );
        std::process::exit(2);
    };
    match cmd.as_str() {
        "--bench" => {
            let mut out_dir = PathBuf::from("improve/lab-exo-upscale");
            while let Some(a) = args.next() {
                if a == "--out-dir"
                    && let Some(v) = args.next()
                {
                    out_dir = PathBuf::from(v);
                }
            }
            match run_bench(&out_dir) {
                Ok(r) => println!("{}", serde_json::to_string_pretty(&r).unwrap()),
                Err(e) => {
                    eprintln!("bench failed: {e}");
                    std::process::exit(1);
                }
            }
        }
        "--inspect-drs" => {
            let probe = probe_nvidia_drs_readonly();
            println!("{}", serde_json::to_string_pretty(&probe).unwrap());
        }
        "--verify" => {
            let Some(p) = args.next() else {
                eprintln!("missing dll path");
                std::process::exit(2);
            };
            match inspect_dll(Path::new(&p)) {
                Ok(v) => println!("{}", serde_json::to_string_pretty(&v).unwrap()),
                Err(e) => {
                    eprintln!("verify failed: {e}");
                    std::process::exit(1);
                }
            }
        }
        "--generate-manifest" => {
            let (Some(scan_dir), Some(out_json)) = (args.next(), args.next()) else {
                eprintln!("usage: --generate-manifest <scan-dir> <out-json>");
                std::process::exit(2);
            };
            match generate_local_manifest(Path::new(&scan_dir), Path::new(&out_json), "local") {
                Ok(m) => println!("{}", serde_json::to_string_pretty(&m).unwrap()),
                Err(e) => {
                    eprintln!("manifest generation failed: {e}");
                    std::process::exit(1);
                }
            }
        }
        "--recover-orphans" => {
            let (Some(state_dir), Some(backup_dir)) = (args.next(), args.next()) else {
                eprintln!("usage: --recover-orphans <state-dir> <backup-dir>");
                std::process::exit(2);
            };
            match SessionMods::new(state_dir, backup_dir) {
                Ok(m) => {
                    let rep = m.recover_orphaned_sessions();
                    println!("{}", serde_json::to_string_pretty(&rep).unwrap());
                }
                Err(e) => {
                    eprintln!("recover-orphans failed: {e}");
                    std::process::exit(1);
                }
            }
        }
        other => {
            eprintln!("unknown command: {other}");
            std::process::exit(2);
        }
    }
}
