use exo_delta::{decode_hex, encode_hex};
use exo_installer::payload::{read_embedded_payload, write_self_contained_installer};
use exo_installer::shell_reg::{register_install, uninstall_clean};
use exo_installer::swap::{
    HealthAction, InstallRoot, mark_healthy, rollback_now, stage_and_swap,
    watch_launch_with_rollback,
};
use exo_installer::ui_d2d::bench_d2d_first_paint;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};

pub const EMBEDDED_RELEASE_PUBLIC_KEY_HEX: Option<&str> =
    option_env!("EXO_DELTA_RELEASE_PUBKEY_HEX");

fn resolve_trusted_pubkey(args: &[String], root: &InstallRoot) -> Result<[u8; 32], String> {
    if root.is_test_root
        && let Some(p) = arg_val(args, "--pubkey-hex")
    {
        return decode_hex::<32>(&p).map_err(|e| e.to_string());
    }
    let hex = EMBEDDED_RELEASE_PUBLIC_KEY_HEX.ok_or_else(|| {
        "no embedded EXO_DELTA_RELEASE_PUBKEY_HEX configured for production install".to_string()
    })?;
    decode_hex::<32>(hex).map_err(|e| e.to_string())
}

fn has_flag(args: &[String], flag: &str) -> bool {
    args.iter().any(|a| a == flag)
}

fn arg_val(args: &[String], flag: &str) -> Option<String> {
    args.windows(2).find(|w| w[0] == flag).map(|w| w[1].clone())
}

fn read_sig(path: &str) -> Result<[u8; 64], String> {
    let raw = fs::read(path).map_err(|e| e.to_string())?;
    if raw.len() == 64 {
        let mut out = [0u8; 64];
        out.copy_from_slice(&raw);
        return Ok(out);
    }
    let text = std::str::from_utf8(&raw).map_err(|_| "sig not utf8".to_string())?;
    decode_hex::<64>(text).map_err(|e| e.to_string())
}

fn resolve_root(args: &[String]) -> Result<InstallRoot, String> {
    if let Some(dir) = arg_val(args, "--test-root") {
        InstallRoot::isolated_test(PathBuf::from(dir)).map_err(|e| e.to_string())
    } else {
        InstallRoot::production().map_err(|e| e.to_string())
    }
}

fn main() {
    let args: Vec<String> = env::args().collect();

    let res: Result<(), String> = (|| {
        if has_flag(&args, "--bench-d2d") {
            let b = bench_d2d_first_paint().map_err(|e| e.to_string())?;
            println!(
                "d2d_bench hwnd_ms={:.2} paint_ms={:.2} total_ms={:.2}",
                b.hwnd_create_ms, b.first_paint_ms, b.total_ms
            );
            return Ok(());
        }

        if has_flag(&args, "--embed") {
            let stub = arg_val(&args, "--stub").ok_or("--stub required")?;
            let pack = arg_val(&args, "--pack").ok_or("--pack required")?;
            let sig = arg_val(&args, "--sig").ok_or("--sig required")?;
            let pubkey = arg_val(&args, "--pubkey-hex").ok_or("--pubkey-hex required")?;
            let out = arg_val(&args, "--out").ok_or("--out required")?;
            let pack_bytes = fs::read(&pack).map_err(|e| e.to_string())?;
            let sig_bytes = read_sig(&sig)?;
            let pk_bytes = decode_hex::<32>(&pubkey).map_err(|e| e.to_string())?;
            let written = write_self_contained_installer(
                &PathBuf::from(stub),
                &pack_bytes,
                &sig_bytes,
                &pk_bytes,
                &PathBuf::from(&out),
            )
            .map_err(|e| e.to_string())?;
            println!("embedded_installer={out} bytes={written}");
            return Ok(());
        }

        if has_flag(&args, "--uninstall") {
            let root = resolve_root(&args)?;
            let keep_data = has_flag(&args, "--keep-data");
            uninstall_clean(&root, keep_data).map_err(|e| e.to_string())?;
            println!("uninstalled root={}", root.base_dir.display());
            return Ok(());
        }

        if has_flag(&args, "--rollback") {
            let root = resolve_root(&args)?;
            let (restored, elapsed_ms) = rollback_now(&root).map_err(|e| e.to_string())?;
            println!(
                "rolled_back from={} elapsed_ms={elapsed_ms}",
                restored.display()
            );
            return Ok(());
        }

        if has_flag(&args, "--mark-healthy") {
            let root = resolve_root(&args)?;
            mark_healthy(&root).map_err(|e| e.to_string())?;
            println!("marked_healthy root={}", root.base_dir.display());
            return Ok(());
        }

        if has_flag(&args, "--watch-health") {
            let root = resolve_root(&args)?;
            let exe = arg_val(&args, "--exe").unwrap_or_else(|| "ExoLauncher.exe".to_string());
            let wait_ms = arg_val(&args, "--wait-ms")
                .and_then(|s| s.parse().ok())
                .unwrap_or(30_000);
            let action = watch_launch_with_rollback(&root, &PathBuf::from(exe), &[], wait_ms)
                .map_err(|e| e.to_string())?;
            println!("watch_health={action:?}");
            return Ok(());
        }

        if has_flag(&args, "--install") || has_flag(&args, "--update") {
            let root = resolve_root(&args)?;
            let version = arg_val(&args, "--version").unwrap_or_else(|| "4.0.0".to_string());
            let (pack_bytes, sig_pair) = if let Some(pack_path) = arg_val(&args, "--pack") {
                let bytes = fs::read(&pack_path).map_err(|e| e.to_string())?;
                let sig_path = arg_val(&args, "--sig")
                    .ok_or("unsigned pack refused: --sig is required".to_string())?;
                let sig = read_sig(&sig_path)?;
                let pk = resolve_trusted_pubkey(&args, &root)?;
                (bytes, Some((sig, pk)))
            } else {
                let exe = env::current_exe().map_err(|e| e.to_string())?;
                let emb = read_embedded_payload(&exe)
                    .map_err(|e| e.to_string())?
                    .ok_or("no --pack provided and no embedded EXOPACK1 payload found")?;
                let trusted_pk = if root.is_test_root && arg_val(&args, "--pubkey-hex").is_none() {
                    emb.public_key
                } else {
                    resolve_trusted_pubkey(&args, &root)?
                };
                (emb.pack, Some((emb.signature, trusted_pk)))
            };

            let swap = stage_and_swap(
                &root,
                &pack_bytes,
                &version,
                sig_pair.as_ref().map(|(s, p)| (s, p)),
            )
            .map_err(|e| e.to_string())?;

            let installed_bytes: u64 = walk_size(&root.app_dir());
            let self_exe =
                env::current_exe().unwrap_or_else(|_| PathBuf::from("exo-installer.exe"));
            let _ = register_install(&root, &version, installed_bytes, &self_exe)
                .map_err(|e| e.to_string())?;

            println!(
                "installed version={} files={} hardlinked={} patched={} full={} swap_ms={:.2} tree_blake3={}",
                swap.version,
                swap.apply_summary.files_applied,
                swap.apply_summary.hardlinked_files,
                swap.apply_summary.patched_files,
                swap.apply_summary.full_files,
                swap.swap_ms,
                encode_hex(&swap.apply_summary.tree_blake3)
            );
            return Ok(());
        }

        let b = bench_d2d_first_paint().map_err(|e| e.to_string())?;
        println!(
            "exo-installer ready (d2d_first_paint={:.2} ms); pass --install, --update, --rollback, --uninstall, --watch-health, --embed, or --bench-d2d",
            b.total_ms
        );
        let _ = HealthAction::NoPendingUpdate;
        Ok(())
    })();

    if let Err(err) = res {
        eprintln!("exo-installer error: {err}");
        std::process::exit(1);
    }
}

fn walk_size(dir: &Path) -> u64 {
    let mut total = 0u64;
    let mut stack = vec![dir.to_path_buf()];
    while let Some(d) = stack.pop() {
        if let Ok(rd) = fs::read_dir(d) {
            for entry in rd.flatten() {
                let p = entry.path();
                if let Ok(m) = fs::symlink_metadata(&p) {
                    if m.is_dir() {
                        stack.push(p);
                    } else if m.is_file() {
                        total += m.len();
                    }
                }
            }
        }
    }
    total
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_delta::{build_delta_pack, build_full_pack, sign_pack, tree_blake3};
    use exo_installer::fetch::parse_and_validate_url;
    use exo_installer::shell_reg::{registry_key_exists, shortcut_paths};
    use exo_installer::swap::record_launch_attempt;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn tmp_dir(tag: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let p = std::env::temp_dir().join(format!(
            "exo-inst-test-{tag}-{}-{nanos}",
            std::process::id()
        ));
        fs::create_dir_all(&p).unwrap();
        p
    }

    #[test]
    fn isolated_root_full_lifecycle_install_delta_crash_rollback_uninstall() {
        let v1_src = tmp_dir("v1");
        let v2_src = tmp_dir("v2");
        let test_root_dir = tmp_dir("root");
        let root = InstallRoot::isolated_test(test_root_dir.clone()).unwrap();

        let shared = vec![0xA5u8; 64 * 1024];
        let mut dll_v1 = vec![0u8; 128 * 1024];
        let mut s: u64 = 0x1234_5678_9abc_def0;
        for b in &mut dll_v1 {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            *b = (s & 0xff) as u8;
        }
        let mut dll_v2 = dll_v1.clone();
        for b in &mut dll_v2[40_000..40_512] {
            *b ^= 0xFF;
        }

        fs::write(v1_src.join("ExoLauncher.exe"), b"v1-exe").unwrap();
        fs::write(v1_src.join("ExoLauncher.dll"), &dll_v1).unwrap();
        fs::write(v1_src.join("shared.bin"), &shared).unwrap();

        fs::write(v2_src.join("ExoLauncher.exe"), b"v2-exe").unwrap();
        fs::write(v2_src.join("ExoLauncher.dll"), &dll_v2).unwrap();
        fs::write(v2_src.join("shared.bin"), &shared).unwrap();

        let seed = [0x77u8; 32];
        let (full_pack, full_stats) = build_full_pack(&v1_src, 6).unwrap();
        let (full_sig, pubkey) = sign_pack(&full_pack, &seed).unwrap();

        let swap1 = stage_and_swap(&root, &full_pack, "4.0.0", Some((&full_sig, &pubkey))).unwrap();
        assert_eq!(swap1.apply_summary.files_applied, 3);
        assert_eq!(
            tree_blake3(&root.app_dir()).unwrap(),
            full_stats.tree_blake3
        );
        mark_healthy(&root).unwrap();

        let self_exe = env::current_exe().unwrap();
        let reg = register_install(&root, "4.0.0", full_stats.total_raw_bytes, &self_exe).unwrap();
        assert!(reg.start_menu_shortcut.exists());
        assert!(reg.desktop_shortcut.exists());
        assert!(registry_key_exists(&root.registry_subkey));

        let (delta_pack, delta_stats) = build_delta_pack(&v1_src, &v2_src, 9).unwrap();
        assert_eq!(delta_stats.reused_files, 1);
        assert_eq!(delta_stats.patched_files, 1);
        let (delta_sig, _) = sign_pack(&delta_pack, &seed).unwrap();

        let mut bad_sig = delta_sig;
        bad_sig[3] ^= 1;
        assert!(stage_and_swap(&root, &delta_pack, "4.0.1", Some((&bad_sig, &pubkey))).is_err());
        assert_eq!(
            tree_blake3(&root.app_dir()).unwrap(),
            full_stats.tree_blake3
        );

        let swap2 =
            stage_and_swap(&root, &delta_pack, "4.0.1", Some((&delta_sig, &pubkey))).unwrap();
        assert_eq!(swap2.apply_summary.hardlinked_files, 1);
        assert_eq!(swap2.apply_summary.patched_files, 1);
        assert_eq!(
            tree_blake3(&root.app_dir()).unwrap(),
            delta_stats.tree_blake3
        );
        assert!(swap2.previous_dir.is_some());

        let a1 = record_launch_attempt(&root).unwrap();
        assert_eq!(a1, HealthAction::ContinueLaunch { attempt: 1 });
        let a2 = record_launch_attempt(&root).unwrap();
        match a2 {
            HealthAction::RolledBack { elapsed_ms, .. } => {
                assert!(elapsed_ms < 5000);
            }
            other => panic!("expected automatic rollback on 2nd crash, got {other:?}"),
        }
        assert_eq!(
            tree_blake3(&root.app_dir()).unwrap(),
            full_stats.tree_blake3
        );
        assert_eq!(
            fs::read(root.app_dir().join("ExoLauncher.exe")).unwrap(),
            b"v1-exe"
        );

        let embedded_exe = tmp_dir("embed").join("ExoLauncher-Setup.exe");
        write_self_contained_installer(&self_exe, &full_pack, &full_sig, &pubkey, &embedded_exe)
            .unwrap();
        let read_back = read_embedded_payload(&embedded_exe).unwrap().unwrap();
        assert_eq!(read_back.pack, full_pack);
        assert_eq!(read_back.signature, full_sig);
        assert_eq!(read_back.public_key, pubkey);
        let _ = fs::remove_dir_all(embedded_exe.parent().unwrap());

        uninstall_clean(&root, false).unwrap();
        let (sm, dk) = shortcut_paths(&root).unwrap();
        assert!(!sm.exists());
        assert!(!dk.exists());
        assert!(!root.base_dir.exists());
        assert!(!registry_key_exists(&root.registry_subkey));

        let _ = fs::remove_dir_all(v1_src);
        let _ = fs::remove_dir_all(v2_src);
    }

    #[test]
    fn refuses_owner_localappdata_as_test_root_and_disallowed_urls() {
        if let Some(local) = env::var_os("LOCALAPPDATA") {
            let real = PathBuf::from(local).join("ExoLauncher");
            assert!(InstallRoot::isolated_test(real).is_err());
        }
        assert!(
            parse_and_validate_url("https://updates.getexo.app/v1/launcher/latest", false).is_ok()
        );
        assert!(parse_and_validate_url("https://evil.example.com/delta.exopack", false).is_err());
        assert!(
            parse_and_validate_url("http://updates.getexo.app/v1/launcher/latest", false).is_err()
        );
        assert!(parse_and_validate_url("https://updates.getexo.app/../secret", false).is_err());
    }

    #[test]
    fn direct2d_installer_window_paints_cleanly() {
        let b = bench_d2d_first_paint().unwrap();
        assert!(b.total_ms >= 0.0);
    }

    #[test]
    fn unsigned_and_wrong_key_packs_are_refused_and_keep_current_version() {
        let v1_src = tmp_dir("trust-v1");
        let v2_src = tmp_dir("trust-v2");
        let test_root_dir = tmp_dir("trust-root");
        let root = InstallRoot::isolated_test(test_root_dir).unwrap();

        fs::write(v1_src.join("ExoLauncher.exe"), b"v1-trusted-binary").unwrap();
        fs::write(v2_src.join("ExoLauncher.exe"), b"v2-untrusted-binary").unwrap();

        let trusted_seed = [0x33u8; 32];
        let wrong_seed = [0x99u8; 32];

        let (v1_pack, v1_stats) = build_full_pack(&v1_src, 6).unwrap();
        let (v1_sig, trusted_pubkey) = sign_pack(&v1_pack, &trusted_seed).unwrap();
        stage_and_swap(&root, &v1_pack, "4.0.0", Some((&v1_sig, &trusted_pubkey))).unwrap();
        assert_eq!(tree_blake3(&root.app_dir()).unwrap(), v1_stats.tree_blake3);

        let (v2_delta, _) = build_delta_pack(&v1_src, &v2_src, 6).unwrap();
        let (wrong_sig, _) = sign_pack(&v2_delta, &wrong_seed).unwrap();

        let unsigned_err = stage_and_swap(&root, &v2_delta, "4.0.1", None).unwrap_err();
        assert!(matches!(
            unsigned_err,
            exo_installer::InstallError::SafetyRefusal(_)
        ));
        assert_eq!(tree_blake3(&root.app_dir()).unwrap(), v1_stats.tree_blake3);
        assert_eq!(
            fs::read(root.app_dir().join("ExoLauncher.exe")).unwrap(),
            b"v1-trusted-binary"
        );

        let wrong_key_err = stage_and_swap(
            &root,
            &v2_delta,
            "4.0.1",
            Some((&wrong_sig, &trusted_pubkey)),
        )
        .unwrap_err();
        assert!(matches!(
            wrong_key_err,
            exo_installer::InstallError::Delta(exo_delta::DeltaError::SignatureInvalid(_))
        ));
        assert_eq!(tree_blake3(&root.app_dir()).unwrap(), v1_stats.tree_blake3);
        assert_eq!(
            fs::read(root.app_dir().join("ExoLauncher.exe")).unwrap(),
            b"v1-trusted-binary"
        );

        let zero_pk = [0u8; 32];
        let zero_key_err =
            stage_and_swap(&root, &v2_delta, "4.0.1", Some((&v1_sig, &zero_pk))).unwrap_err();
        assert!(matches!(
            zero_key_err,
            exo_installer::InstallError::SafetyRefusal(_)
        ));
        assert_eq!(tree_blake3(&root.app_dir()).unwrap(), v1_stats.tree_blake3);

        uninstall_clean(&root, false).unwrap();
        let _ = fs::remove_dir_all(v1_src);
        let _ = fs::remove_dir_all(v2_src);
    }
}
