use exo_apo::endpoint_reg::{
    KeepBackupLedger, MockRegistryStore, RegistryStore, one_click_rollback,
    register_with_explicit_consent, uninstall_endpoint,
};
use std::env;
use std::fs;
use std::path::{Path, PathBuf};

fn has_flag(args: &[String], flag: &str) -> bool {
    args.iter().any(|a| a == flag)
}

fn arg_val(args: &[String], flag: &str) -> Option<String> {
    args.windows(2).find(|w| w[0] == flag).map(|w| w[1].clone())
}

#[cfg(windows)]
fn is_process_elevated() -> bool {
    use windows::Win32::Foundation::{CloseHandle, HANDLE};
    use windows::Win32::Security::{
        GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation,
    };
    use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

    unsafe {
        let mut token = HANDLE::default();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).is_err() {
            return false;
        }
        let mut elevation = TOKEN_ELEVATION::default();
        let mut ret_len = 0u32;
        let ok = GetTokenInformation(
            token,
            TokenElevation,
            Some((&mut elevation as *mut TOKEN_ELEVATION).cast()),
            core::mem::size_of::<TOKEN_ELEVATION>() as u32,
            &mut ret_len,
        )
        .is_ok();
        let _ = CloseHandle(token);
        ok && elevation.TokenIsElevated != 0
    }
}

#[cfg(not(windows))]
fn is_process_elevated() -> bool {
    false
}

fn load_ledger(path: &Path) -> Result<KeepBackupLedger, String> {
    if !path.exists() {
        return Ok(KeepBackupLedger::new());
    }
    let text = fs::read_to_string(path).map_err(|e| format!("read ledger: {e}"))?;
    KeepBackupLedger::from_json(&text).map_err(|e| format!("parse ledger: {e}"))
}

fn save_ledger(path: &Path, ledger: &KeepBackupLedger) -> Result<(), String> {
    if let Some(parent) = path.parent()
        && !parent.as_os_str().is_empty()
    {
        fs::create_dir_all(parent).map_err(|e| format!("create ledger dir: {e}"))?;
    }
    let json = ledger
        .to_json()
        .map_err(|e| format!("serialize ledger: {e}"))?;
    fs::write(path, json).map_err(|e| format!("write ledger: {e}"))
}

fn validate_dll_path(dll: &str) -> Result<String, String> {
    let p = Path::new(dll);
    let name = p
        .file_name()
        .and_then(|s| s.to_str())
        .ok_or_else(|| "invalid dll path".to_string())?;
    if !name.eq_ignore_ascii_case("exo_apo.dll") {
        return Err("dll path must point to exo_apo.dll".to_string());
    }
    if !p.exists() {
        return Err(format!("exo_apo.dll not found at {}", p.display()));
    }
    Ok(p.to_string_lossy().into_owned())
}

fn run_with_store<R: RegistryStore>(args: &[String], store: &mut R) -> Result<String, String> {
    let cmd = args.get(1).map(String::as_str).unwrap_or("");
    let ledger_path = PathBuf::from(arg_val(args, "--ledger").ok_or("--ledger is required")?);

    match cmd {
        "register" => {
            let consent = has_flag(args, "--explicit-user-consent");
            if !consent {
                return Err(
                    "registration refused: explicit user action (--explicit-user-consent) is required"
                        .to_string(),
                );
            }
            let endpoint = arg_val(args, "--endpoint").ok_or("--endpoint is required")?;
            let dll = validate_dll_path(&arg_val(args, "--dll").ok_or("--dll is required")?)?;
            let mut ledger = load_ledger(&ledger_path)?;
            let outcome = register_with_explicit_consent(store, &mut ledger, &endpoint, &dll, true)
                .map_err(|e| e.to_string())?;
            save_ledger(&ledger_path, &ledger)?;
            Ok(format!(
                "registered endpoint={} oem_preserved={} sysfx_disabled={}",
                outcome.endpoint_guid,
                outcome.preserved_oem_clsids.len(),
                outcome.sysfx_was_disabled
            ))
        }
        "rollback" => {
            let mut ledger = load_ledger(&ledger_path)?;
            if let Some(endpoint) = arg_val(args, "--endpoint") {
                let restored = uninstall_endpoint(store, &mut ledger, &endpoint)
                    .map_err(|e| e.to_string())?;
                save_ledger(&ledger_path, &ledger)?;
                Ok(format!(
                    "rolled_back endpoint={endpoint} restored={restored}"
                ))
            } else {
                let outcome = one_click_rollback(store, &mut ledger).map_err(|e| e.to_string())?;
                save_ledger(&ledger_path, &ledger)?;
                Ok(format!(
                    "rolled_back endpoints={} com_clsids={}",
                    outcome.restored_endpoints, outcome.removed_com_clsids
                ))
            }
        }
        "status" => {
            let ledger = load_ledger(&ledger_path)?;
            Ok(format!(
                "status endpoints={} com_clsids={} dll={}",
                ledger.endpoints.len(),
                ledger.com_clsids_registered.len(),
                ledger.registered_dll_path.as_deref().unwrap_or("none")
            ))
        }
        _ => Err(
            "usage: exo-apo-helper <register|rollback|status> --ledger <PATH> [--endpoint <GUID> --dll <PATH> --explicit-user-consent] [--mock-store <PATH>]"
                .to_string(),
        ),
    }
}

fn run_cli(args: &[String]) -> Result<String, String> {
    if let Some(mock_path) = arg_val(args, "--mock-store") {
        let p = PathBuf::from(mock_path);
        let mut store: MockRegistryStore = if p.exists() {
            let text = fs::read_to_string(&p).map_err(|e| format!("read mock store: {e}"))?;
            serde_json::from_str(&text).map_err(|e| format!("parse mock store: {e}"))?
        } else {
            MockRegistryStore::new()
        };
        let out = run_with_store(args, &mut store)?;
        let saved = serde_json::to_string_pretty(&store)
            .map_err(|e| format!("serialize mock store: {e}"))?;
        fs::write(&p, saved).map_err(|e| format!("write mock store: {e}"))?;
        return Ok(out);
    }

    let cmd = args.get(1).map(String::as_str).unwrap_or("");
    if matches!(cmd, "register" | "rollback") && !is_process_elevated() {
        return Err(
            "elevation required: launch exo-apo-helper via explicit UAC action to modify Windows audio endpoints"
                .to_string(),
        );
    }

    #[cfg(windows)]
    {
        let mut store = exo_apo::endpoint_reg::Win32RegistryStore;
        run_with_store(args, &mut store)
    }
    #[cfg(not(windows))]
    {
        Err("Win32RegistryStore requires Windows".to_string())
    }
}

fn main() {
    let args: Vec<String> = env::args().collect();
    match run_cli(&args) {
        Ok(line) => println!("{line}"),
        Err(err) => {
            eprintln!("exo-apo-helper error: {err}");
            std::process::exit(1);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_apo::endpoint_reg::{
        AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE, PKEY_FX_ENDPOINT_EFFECT_CLSID,
        RegValue, is_exo_registered_on_endpoint,
    };
    use std::collections::BTreeMap;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn tmp_dir(tag: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let p = env::temp_dir().join(format!(
            "exo-apo-helper-test-{tag}-{}-{nanos}",
            std::process::id()
        ));
        fs::create_dir_all(&p).unwrap();
        p
    }

    #[test]
    fn cli_mock_flow_refuses_without_consent_registers_and_rolls_back() {
        let dir = tmp_dir("cli");
        let mock_path = dir.join("mock-reg.json");
        let ledger_path = dir.join("ledger.json");
        let dll_path = dir.join("exo_apo.dll");
        fs::write(&dll_path, b"mock-apo-pe").unwrap();

        let guid = "{9709e4ab-1234-5678-abcd-0123456789ab}";
        let oem_clsid = "{11111111-2222-3333-4444-555555555555}";
        let mut initial_store = MockRegistryStore::new();
        let mut fx = BTreeMap::new();
        fx.insert(
            PKEY_FX_ENDPOINT_EFFECT_CLSID.to_string(),
            RegValue::String(oem_clsid.to_string()),
        );
        initial_store.seed_endpoint(guid, fx.clone());
        fs::write(
            &mock_path,
            serde_json::to_string_pretty(&initial_store).unwrap(),
        )
        .unwrap();

        let no_consent_args = vec![
            "exo-apo-helper".into(),
            "register".into(),
            "--endpoint".into(),
            guid.into(),
            "--dll".into(),
            dll_path.to_string_lossy().into_owned(),
            "--ledger".into(),
            ledger_path.to_string_lossy().into_owned(),
            "--mock-store".into(),
            mock_path.to_string_lossy().into_owned(),
        ];
        let err = run_cli(&no_consent_args).unwrap_err();
        assert!(err.contains("explicit user action"));

        let mut consent_args = no_consent_args;
        consent_args.push("--explicit-user-consent".into());
        let reg_out = run_cli(&consent_args).unwrap();
        assert!(reg_out.contains("registered endpoint="));
        assert!(reg_out.contains("oem_preserved=1"));

        let after_reg: MockRegistryStore =
            serde_json::from_str(&fs::read_to_string(&mock_path).unwrap()).unwrap();
        assert!(is_exo_registered_on_endpoint(&after_reg, guid));
        assert!(
            after_reg
                .read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)
                .unwrap()
                .is_none()
        );

        let status_args = vec![
            "exo-apo-helper".into(),
            "status".into(),
            "--ledger".into(),
            ledger_path.to_string_lossy().into_owned(),
            "--mock-store".into(),
            mock_path.to_string_lossy().into_owned(),
        ];
        let status_out = run_cli(&status_args).unwrap();
        assert!(status_out.contains("endpoints=1 com_clsids=3"));

        let rollback_args = vec![
            "exo-apo-helper".into(),
            "rollback".into(),
            "--ledger".into(),
            ledger_path.to_string_lossy().into_owned(),
            "--mock-store".into(),
            mock_path.to_string_lossy().into_owned(),
        ];
        let rb_out = run_cli(&rollback_args).unwrap();
        assert!(rb_out.contains("rolled_back endpoints=1 com_clsids=3"));

        let after_rb: MockRegistryStore =
            serde_json::from_str(&fs::read_to_string(&mock_path).unwrap()).unwrap();
        assert_eq!(after_rb, initial_store);

        let _ = fs::remove_dir_all(dir);
    }
}
