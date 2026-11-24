use crate::InstallError;
use exo_delta::{ApplySummary, apply_delta_pack};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub const APP_USER_MODEL_ID: &str = "ImAvgErix.ExoLauncher";
pub const MAX_UNHEALTHY_ATTEMPTS: u32 = 2;

#[derive(Debug, Clone)]
pub struct InstallRoot {
    pub base_dir: PathBuf,
    pub is_test_root: bool,
    pub registry_subkey: String,
    pub shortcuts_dir: Option<PathBuf>,
}

impl InstallRoot {
    pub fn production() -> Result<Self, InstallError> {
        let local_app_data = std::env::var_os("LOCALAPPDATA")
            .ok_or_else(|| InstallError::InvalidArg("LOCALAPPDATA not set".into()))?;
        Ok(Self {
            base_dir: PathBuf::from(local_app_data).join("ExoLauncher"),
            is_test_root: false,
            registry_subkey: r"Software\Microsoft\Windows\CurrentVersion\Uninstall\ExoLauncher"
                .to_string(),
            shortcuts_dir: None,
        })
    }

    pub fn isolated_test(root: PathBuf) -> Result<Self, InstallError> {
        if let Some(local) = std::env::var_os("LOCALAPPDATA") {
            let prod = PathBuf::from(local).join("ExoLauncher");
            let norm_root = normalize_cmp(&root);
            let norm_prod = normalize_cmp(&prod);
            if norm_root == norm_prod || norm_root.starts_with(&format!("{norm_prod}\\")) {
                return Err(InstallError::SafetyRefusal(
                    "refusing to use owner's %LOCALAPPDATA%\\ExoLauncher as test root".into(),
                ));
            }
        }
        let id = blake3::hash(root.to_string_lossy().as_bytes()).to_hex();
        let short_id = &id.as_str()[..16];
        Ok(Self {
            shortcuts_dir: Some(root.join("shortcuts")),
            base_dir: root,
            is_test_root: true,
            registry_subkey: format!(r"Software\ExoLauncherTest\{short_id}"),
        })
    }

    pub fn app_dir(&self) -> PathBuf {
        self.base_dir.join("app")
    }

    pub fn health_path(&self) -> PathBuf {
        self.base_dir.join("update-health.json")
    }
}

fn normalize_cmp(p: &Path) -> String {
    p.to_string_lossy()
        .replace('/', "\\")
        .trim_end_matches('\\')
        .to_ascii_lowercase()
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct UpdateHealthState {
    pub version: String,
    pub previous_dir: Option<String>,
    pub attempts: u32,
    pub healthy: bool,
    pub rolled_back: bool,
    pub swapped_at_ms: u64,
}

#[derive(Debug, Clone)]
pub struct SwapResult {
    pub version: String,
    pub previous_dir: Option<PathBuf>,
    pub apply_summary: ApplySummary,
    pub swap_ms: f64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HealthAction {
    NoPendingUpdate,
    ContinueLaunch {
        attempt: u32,
    },
    RolledBack {
        restored_from: PathBuf,
        elapsed_ms: u64,
    },
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}

fn unique_suffix() -> String {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    format!("{}-{nanos}", std::process::id())
}

pub fn stage_and_swap(
    root: &InstallRoot,
    pack: &[u8],
    version: &str,
    verify_sig: Option<(&[u8; 64], &[u8; 32])>,
) -> Result<SwapResult, InstallError> {
    let (sig, pubkey) = verify_sig.ok_or_else(|| {
        InstallError::SafetyRefusal("unsigned pack refused: Ed25519 signature required".into())
    })?;
    if *pubkey == [0u8; 32] {
        return Err(InstallError::SafetyRefusal(
            "unconfigured release signing public key".into(),
        ));
    }
    fs::create_dir_all(&root.base_dir)?;
    let app_dir = root.app_dir();
    let incoming_dir = root
        .base_dir
        .join(format!("app.incoming-{}", unique_suffix()));
    let base_opt = if app_dir.exists() {
        Some(app_dir.as_path())
    } else {
        None
    };

    let apply_summary =
        match apply_delta_pack(base_opt, pack, Some(&incoming_dir), Some((sig, pubkey))) {
            Ok(s) => s,
            Err(e) => {
                let _ = fs::remove_dir_all(&incoming_dir);
                return Err(InstallError::Delta(e));
            }
        };

    if app_dir.exists() {
        graceful_close_in_dir(&app_dir, 15_000)?;
    }

    let t_swap = Instant::now();
    let previous_dir = if app_dir.exists() {
        let prev = root
            .base_dir
            .join(format!("app.previous-{}", unique_suffix()));
        fs::rename(&app_dir, &prev)?;
        Some(prev)
    } else {
        None
    };

    if let Err(err) = fs::rename(&incoming_dir, &app_dir) {
        if let Some(prev) = &previous_dir {
            let _ = fs::rename(prev, &app_dir);
        }
        let _ = fs::remove_dir_all(&incoming_dir);
        return Err(InstallError::Io(err));
    }

    let swap_ms = t_swap.elapsed().as_secs_f64() * 1000.0;
    let health = UpdateHealthState {
        version: version.to_string(),
        previous_dir: previous_dir
            .as_ref()
            .and_then(|p| p.file_name())
            .map(|n| n.to_string_lossy().into_owned()),
        attempts: 0,
        healthy: false,
        rolled_back: false,
        swapped_at_ms: now_ms(),
    };
    write_health(root, &health)?;

    Ok(SwapResult {
        version: version.to_string(),
        previous_dir,
        apply_summary,
        swap_ms,
    })
}

pub fn read_health(root: &InstallRoot) -> Result<Option<UpdateHealthState>, InstallError> {
    let path = root.health_path();
    if !path.exists() {
        return Ok(None);
    }
    let text = fs::read_to_string(&path)?;
    let state: UpdateHealthState =
        serde_json::from_str(&text).map_err(|e| InstallError::Health(e.to_string()))?;
    Ok(Some(state))
}

pub fn write_health(root: &InstallRoot, state: &UpdateHealthState) -> Result<(), InstallError> {
    let path = root.health_path();
    let tmp = root.base_dir.join("update-health.json.tmp");
    let json =
        serde_json::to_string_pretty(state).map_err(|e| InstallError::Health(e.to_string()))?;
    fs::write(&tmp, json)?;
    fs::rename(&tmp, &path)?;
    Ok(())
}

pub fn mark_healthy(root: &InstallRoot) -> Result<(), InstallError> {
    if let Some(mut state) = read_health(root)? {
        state.healthy = true;
        if let Some(prev_name) = state.previous_dir.take() {
            let prev_path = root.base_dir.join(prev_name);
            if prev_path.exists() {
                let _ = fs::remove_dir_all(prev_path);
            }
        }
        write_health(root, &state)?;
    }
    Ok(())
}

pub fn rollback_now(root: &InstallRoot) -> Result<(PathBuf, u64), InstallError> {
    let t0 = Instant::now();
    let prev_path = resolve_previous_dir(root)?;
    let app_dir = root.app_dir();
    if app_dir.exists() {
        graceful_close_in_dir(&app_dir, 10_000)?;
        let broken = root
            .base_dir
            .join(format!("app.failed-{}", unique_suffix()));
        fs::rename(&app_dir, &broken)?;
        if let Err(err) = fs::rename(&prev_path, &app_dir) {
            let _ = fs::rename(&broken, &app_dir);
            return Err(InstallError::Io(err));
        }
        let _ = fs::remove_dir_all(&broken);
    } else {
        fs::rename(&prev_path, &app_dir)?;
    }
    let elapsed_ms = t0.elapsed().as_millis() as u64;
    if let Some(mut state) = read_health(root)? {
        state.rolled_back = true;
        state.previous_dir = None;
        write_health(root, &state)?;
    }
    Ok((prev_path, elapsed_ms))
}

fn resolve_previous_dir(root: &InstallRoot) -> Result<PathBuf, InstallError> {
    if let Some(state) = read_health(root)?
        && let Some(name) = state.previous_dir
    {
        let p = root.base_dir.join(&name);
        if p.is_dir() {
            return Ok(p);
        }
    }
    let mut candidates: Vec<PathBuf> = fs::read_dir(&root.base_dir)?
        .filter_map(|e| e.ok().map(|x| x.path()))
        .filter(|p| {
            p.is_dir()
                && p.file_name()
                    .and_then(|n| n.to_str())
                    .is_some_and(|n| n.starts_with("app.previous-"))
        })
        .collect();
    candidates.sort();
    candidates
        .pop()
        .ok_or_else(|| InstallError::Health("no app.previous backup directory found".into()))
}

pub fn record_launch_attempt(root: &InstallRoot) -> Result<HealthAction, InstallError> {
    let Some(mut state) = read_health(root)? else {
        return Ok(HealthAction::NoPendingUpdate);
    };
    if state.healthy || state.rolled_back || state.previous_dir.is_none() {
        return Ok(HealthAction::NoPendingUpdate);
    }
    state.attempts = state.attempts.saturating_add(1);
    write_health(root, &state)?;
    if state.attempts >= MAX_UNHEALTHY_ATTEMPTS {
        let (restored_from, elapsed_ms) = rollback_now(root)?;
        return Ok(HealthAction::RolledBack {
            restored_from,
            elapsed_ms,
        });
    }
    Ok(HealthAction::ContinueLaunch {
        attempt: state.attempts,
    })
}

pub fn watch_launch_with_rollback(
    root: &InstallRoot,
    exe_rel_or_abs: &Path,
    args: &[String],
    health_wait_ms: u64,
) -> Result<HealthAction, InstallError> {
    let exe = if exe_rel_or_abs.is_absolute() {
        exe_rel_or_abs.to_path_buf()
    } else {
        root.app_dir().join(exe_rel_or_abs)
    };
    let mut child = Command::new(&exe)
        .args(args)
        .current_dir(&root.base_dir)
        .spawn()?;
    let deadline = Instant::now() + Duration::from_millis(health_wait_ms);
    loop {
        if let Some(state) = read_health(root)?
            && state.healthy
        {
            return Ok(HealthAction::NoPendingUpdate);
        }
        if let Some(status) = child.try_wait()? {
            if status.success()
                && let Some(state) = read_health(root)?
                && state.healthy
            {
                return Ok(HealthAction::NoPendingUpdate);
            }
            return record_launch_attempt(root);
        }
        if Instant::now() >= deadline {
            return record_launch_attempt(root);
        }
        std::thread::sleep(Duration::from_millis(25));
    }
}

#[cfg(windows)]
pub fn graceful_close_in_dir(app_dir: &Path, timeout_ms: u64) -> Result<(), InstallError> {
    use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM};
    use windows::Win32::System::ProcessStatus::EnumProcesses;
    use windows::Win32::System::Threading::{
        OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
        QueryFullProcessImageNameW, WaitForSingleObject,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        EnumWindows, GetWindowThreadProcessId, PostMessageW, WM_CLOSE,
    };
    use windows::core::{BOOL, PWSTR};

    let needle = normalize_cmp(app_dir);
    let prefix = format!("{needle}\\");
    let my_pid = std::process::id();

    let mut pids = vec![0u32; 4096];
    let mut bytes_returned = 0u32;
    let ok = unsafe {
        EnumProcesses(
            pids.as_mut_ptr(),
            (pids.len() * std::mem::size_of::<u32>()) as u32,
            &mut bytes_returned,
        )
    };
    if ok.is_err() {
        return Ok(());
    }
    let count = (bytes_returned as usize) / std::mem::size_of::<u32>();
    let mut matching_pids = Vec::new();
    let mut name_buf = [0u16; 1024];

    for &pid in &pids[..count] {
        if pid == 0 || pid == my_pid {
            continue;
        }
        let Ok(handle) = (unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                false,
                pid,
            )
        }) else {
            continue;
        };
        let mut len = name_buf.len() as u32;
        let queried = unsafe {
            QueryFullProcessImageNameW(
                handle,
                PROCESS_NAME_WIN32,
                PWSTR(name_buf.as_mut_ptr()),
                &mut len,
            )
        };
        if queried.is_ok() {
            let full = String::from_utf16_lossy(&name_buf[..len as usize]).to_ascii_lowercase();
            if full.starts_with(&prefix) {
                matching_pids.push((pid, handle));
                continue;
            }
        }
        unsafe {
            let _ = CloseHandle(handle);
        }
    }

    if matching_pids.is_empty() {
        return Ok(());
    }

    struct EnumCtx {
        pids: Vec<u32>,
    }
    let ctx = EnumCtx {
        pids: matching_pids.iter().map(|(p, _)| *p).collect(),
    };
    unsafe extern "system" fn enum_cb(hwnd: HWND, lparam: LPARAM) -> BOOL {
        let ctx = unsafe { &*(lparam.0 as *const EnumCtx) };
        let mut wpid = 0u32;
        unsafe {
            GetWindowThreadProcessId(hwnd, Some(&mut wpid));
        }
        if ctx.pids.contains(&wpid) {
            unsafe {
                let _ = PostMessageW(Some(hwnd), WM_CLOSE, Default::default(), Default::default());
            }
        }
        BOOL(1)
    }
    unsafe {
        let _ = EnumWindows(Some(enum_cb), LPARAM(&ctx as *const EnumCtx as isize));
    }

    let deadline = Instant::now() + Duration::from_millis(timeout_ms);
    for (_, handle) in matching_pids {
        let rem = deadline
            .saturating_duration_since(Instant::now())
            .as_millis()
            .min(u32::MAX as u128) as u32;
        unsafe {
            let _ = WaitForSingleObject(handle, rem);
            let _ = CloseHandle(handle);
        }
    }
    Ok(())
}

#[cfg(not(windows))]
pub fn graceful_close_in_dir(_app_dir: &Path, _timeout_ms: u64) -> Result<(), InstallError> {
    Ok(())
}
