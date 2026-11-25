use exo_apo::endpoint_reg::{
    ApoSigningMode, AudioServiceController, EnableSystemEqRequest, KeepBackupLedger,
    MockAudioServiceController, MockRegistryStore, RegistryStore, check_system_eq_health,
    disable_system_eq, enable_system_eq, uninstall_endpoint,
};
use std::env;
use std::fs;
use std::io::Write;
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

struct LoggingMockService {
    inner: MockAudioServiceController,
    log_path: Option<PathBuf>,
}

impl AudioServiceController for LoggingMockService {
    fn restart_audio_service(&mut self) -> std::io::Result<()> {
        self.inner.restart_audio_service()?;
        if let Some(ref p) = self.log_path {
            let mut file = fs::OpenOptions::new().create(true).append(true).open(p)?;
            writeln!(file, "restart:Audiosrv")?;
        }
        Ok(())
    }
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

fn run_with_backends<R: RegistryStore, S: AudioServiceController>(
    args: &[String],
    store: &mut R,
    service: &mut S,
) -> Result<String, String> {
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
            let signed_mode = has_flag(args, "--signed");
            let mode = if signed_mode {
                ApoSigningMode::Signed
            } else {
                ApoSigningMode::UnsignedFreeRoute
            };
            let drm_accepted = has_flag(args, "--drm-disclosure-accepted");
            let endpoint = arg_val(args, "--endpoint").ok_or("--endpoint is required")?;
            let dll = validate_dll_path(&arg_val(args, "--dll").ok_or("--dll is required")?)?;
            let mut ledger = load_ledger(&ledger_path)?;
            let outcome = enable_system_eq(
                store,
                service,
                &mut ledger,
                EnableSystemEqRequest {
                    endpoint_id: &endpoint,
                    dll_path: &dll,
                    mode,
                    explicit_user_consent: true,
                    drm_disclosure_accepted: drm_accepted,
                },
            )
            .map_err(|e| e.to_string())?;
            save_ledger(&ledger_path, &ledger)?;
            Ok(format!(
                "registered endpoint={} mode={:?} oem_preserved={} sysfx_disabled={}",
                outcome.endpoint_guid,
                mode,
                outcome.preserved_oem_clsids.len(),
                outcome.sysfx_was_disabled
            ))
        }
        "rollback" => {
            let mut ledger = load_ledger(&ledger_path)?;
            if let Some(endpoint) = arg_val(args, "--endpoint") {
                let restored = uninstall_endpoint(store, &mut ledger, &endpoint)
                    .map_err(|e| e.to_string())?;
                service.restart_audio_service().map_err(|e| e.to_string())?;
                save_ledger(&ledger_path, &ledger)?;
                Ok(format!(
                    "rolled_back endpoint={endpoint} restored={restored}"
                ))
            } else {
                let outcome =
                    disable_system_eq(store, service, &mut ledger).map_err(|e| e.to_string())?;
                save_ledger(&ledger_path, &ledger)?;
                Ok(format!(
                    "rolled_back endpoints={} com_clsids={} prior_protected_dg={:?}",
                    outcome.restored_endpoints,
                    outcome.removed_com_clsids,
                    outcome.restored_protected_audio_dg
                ))
            }
        }
        "health" => {
            let ledger = load_ledger(&ledger_path)?;
            let audiodg_rejected = has_flag(args, "--audiodg-rejected");
            let report = check_system_eq_health(store, &ledger, audiodg_rejected);
            serde_json::to_string(&report).map_err(|e| format!("serialize health: {e}"))
        }
        "status" => {
            let ledger = load_ledger(&ledger_path)?;
            Ok(format!(
                "status enabled={} endpoints={} com_clsids={} dll={}",
                ledger.system_eq_enabled,
                ledger.endpoints.len(),
                ledger.com_clsids_registered.len(),
                ledger.registered_dll_path.as_deref().unwrap_or("none")
            ))
        }
        _ => Err(
            "usage: exo-apo-helper <register|rollback|health|status> --ledger <PATH> [--endpoint <GUID> --dll <PATH> --explicit-user-consent [--drm-disclosure-accepted | --signed]] [--mock-store <PATH>] [--mock-service-log <PATH>]"
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
        let mut service = LoggingMockService {
            inner: MockAudioServiceController::new(),
            log_path: arg_val(args, "--mock-service-log").map(PathBuf::from),
        };
        let out = run_with_backends(args, &mut store, &mut service)?;
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
        let mut service = exo_apo::endpoint_reg::Win32AudioServiceController;
        run_with_backends(args, &mut store, &mut service)
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
        ReenableReason, RegValue, SystemEqHealthReport, is_exo_registered_on_endpoint,
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
    fn cli_mock_flow_unsigned_route_snapshots_restarts_detects_reset_and_rolls_back() {
        let dir = tmp_dir("cli-unsigned");
        let mock_path = dir.join("mock-reg.json");
        let ledger_path = dir.join("ledger.json");
        let svc_log_path = dir.join("svc.log");
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
        initial_store.seed_endpoint(guid, fx);
        initial_store.ensure_key(AUDIO_POLICY_ROOT).unwrap();
        initial_store
            .write_value(
                AUDIO_POLICY_ROOT,
                DISABLE_PROTECTED_AUDIO_DG_VALUE,
                &RegValue::Dword(0),
            )
            .unwrap();
        fs::write(
            &mock_path,
            serde_json::to_string_pretty(&initial_store).unwrap(),
        )
        .unwrap();

        let base_reg = vec![
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
            "--mock-service-log".into(),
            svc_log_path.to_string_lossy().into_owned(),
        ];

        let err_no_consent = run_cli(&base_reg).unwrap_err();
        assert!(err_no_consent.contains("explicit user action"));

        let mut no_disclosure = base_reg.clone();
        no_disclosure.push("--explicit-user-consent".into());
        let err_no_disclosure = run_cli(&no_disclosure).unwrap_err();
        assert!(err_no_disclosure.contains("DRM protected-audio disclosure"));
        assert!(!svc_log_path.exists());

        let mut full_reg = no_disclosure;
        full_reg.push("--drm-disclosure-accepted".into());
        let reg_out = run_cli(&full_reg).unwrap();
        assert!(reg_out.contains("registered endpoint="));
        assert!(reg_out.contains("oem_preserved=1"));

        let after_reg: MockRegistryStore =
            serde_json::from_str(&fs::read_to_string(&mock_path).unwrap()).unwrap();
        assert!(is_exo_registered_on_endpoint(&after_reg, guid));
        assert_eq!(
            after_reg
                .read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)
                .unwrap(),
            Some(RegValue::Dword(1))
        );
        let restarts_after_reg = fs::read_to_string(&svc_log_path)
            .unwrap()
            .lines()
            .filter(|l| *l == "restart:Audiosrv")
            .count();
        assert_eq!(restarts_after_reg, 1);

        let health_args = vec![
            "exo-apo-helper".into(),
            "health".into(),
            "--ledger".into(),
            ledger_path.to_string_lossy().into_owned(),
            "--mock-store".into(),
            mock_path.to_string_lossy().into_owned(),
        ];
        let healthy: SystemEqHealthReport =
            serde_json::from_str(&run_cli(&health_args).unwrap()).unwrap();
        assert!(healthy.system_eq_enabled);
        assert!(!healthy.needs_reenable);

        let mut wiped = after_reg;
        wiped
            .write_value(
                AUDIO_POLICY_ROOT,
                DISABLE_PROTECTED_AUDIO_DG_VALUE,
                &RegValue::Dword(0),
            )
            .unwrap();
        fs::write(&mock_path, serde_json::to_string_pretty(&wiped).unwrap()).unwrap();

        let reset_report: SystemEqHealthReport =
            serde_json::from_str(&run_cli(&health_args).unwrap()).unwrap();
        assert!(reset_report.needs_reenable);
        assert_eq!(
            reset_report.reason,
            Some(ReenableReason::ProtectedAudioDgReset)
        );

        let rollback_args = vec![
            "exo-apo-helper".into(),
            "rollback".into(),
            "--ledger".into(),
            ledger_path.to_string_lossy().into_owned(),
            "--mock-store".into(),
            mock_path.to_string_lossy().into_owned(),
            "--mock-service-log".into(),
            svc_log_path.to_string_lossy().into_owned(),
        ];
        let rb_out = run_cli(&rollback_args).unwrap();
        assert!(rb_out.contains("rolled_back endpoints=1 com_clsids=3"));

        let after_rb: MockRegistryStore =
            serde_json::from_str(&fs::read_to_string(&mock_path).unwrap()).unwrap();
        assert_eq!(after_rb, initial_store);
        assert_eq!(
            after_rb
                .read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)
                .unwrap(),
            Some(RegValue::Dword(0))
        );
        let restarts_after_rb = fs::read_to_string(&svc_log_path)
            .unwrap()
            .lines()
            .filter(|l| *l == "restart:Audiosrv")
            .count();
        assert_eq!(restarts_after_rb, 2);

        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn cli_mock_signed_route_leaves_protected_audiodg_untouched() {
        let dir = tmp_dir("cli-signed");
        let mock_path = dir.join("mock-reg.json");
        let ledger_path = dir.join("ledger.json");
        let svc_log_path = dir.join("svc.log");
        let dll_path = dir.join("exo_apo.dll");
        fs::write(&dll_path, b"mock-apo-pe").unwrap();

        let guid = "{9709e4ab-1234-5678-abcd-0123456789ab}";
        let mut initial_store = MockRegistryStore::new();
        initial_store.seed_endpoint(guid, BTreeMap::new());
        fs::write(
            &mock_path,
            serde_json::to_string_pretty(&initial_store).unwrap(),
        )
        .unwrap();

        let signed_args = vec![
            "exo-apo-helper".into(),
            "register".into(),
            "--endpoint".into(),
            guid.into(),
            "--dll".into(),
            dll_path.to_string_lossy().into_owned(),
            "--ledger".into(),
            ledger_path.to_string_lossy().into_owned(),
            "--explicit-user-consent".into(),
            "--signed".into(),
            "--mock-store".into(),
            mock_path.to_string_lossy().into_owned(),
            "--mock-service-log".into(),
            svc_log_path.to_string_lossy().into_owned(),
        ];
        run_cli(&signed_args).unwrap();
        let after_reg: MockRegistryStore =
            serde_json::from_str(&fs::read_to_string(&mock_path).unwrap()).unwrap();
        assert!(
            after_reg
                .read_value(AUDIO_POLICY_ROOT, DISABLE_PROTECTED_AUDIO_DG_VALUE)
                .unwrap()
                .is_none()
        );
        let _ = fs::remove_dir_all(dir);
    }
}
