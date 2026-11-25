use std::collections::HashSet;
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::sync::Mutex;
use std::sync::atomic::{AtomicI64, AtomicUsize, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const HARD_KEPT_SEGMENTS: &[&[&str]] = &[
    &[".ssh"],
    &["Credentials"],
    &["Vault"],
    &["SaveGames"],
    &["Saved Games"],
    &["My Games"],
];

pub const BROWSER_SESSION_SEGMENTS: &[&[&str]] = &[&["Cookies"], &["Login Data"], &["Local State"]];

pub const KEPT_SEGMENTS: &[&[&str]] = &[
    &[".ssh"],
    &["Credentials"],
    &["Vault"],
    &["SaveGames"],
    &["Saved Games"],
    &["My Games"],
    &["Cookies"],
    &["Login Data"],
    &["Local State"],
];

pub const SHADER_SEGMENTS: &[&[&str]] = &[&["steamapps", "shadercache"]];

#[derive(Debug, Clone)]
pub struct DeletionPolicy {
    pub kept_roots: Vec<PathBuf>,
    pub exo_data: Option<PathBuf>,
    pub shader_roots: Vec<PathBuf>,
    pub running_processes: Option<Vec<String>>,
    pub shader_busy: bool,
}

impl Default for DeletionPolicy {
    fn default() -> Self {
        Self {
            kept_roots: Vec::new(),
            exo_data: None,
            shader_roots: Vec::new(),
            running_processes: Some(Vec::new()),
            shader_busy: false,
        }
    }
}

pub fn refusal(
    path: &str,
    clean: bool,
    owners: &[&str],
    policy: &DeletionPolicy,
    allow_app_browser_files: bool,
) -> Option<String> {
    let trimmed = path.trim();
    if trimmed.is_empty() {
        return Some("Exo could not check this path, so it kept it.".into());
    }
    let full = normalize_path(Path::new(trimmed));
    if full.as_os_str().is_empty() {
        return Some("Exo could not check this path, so it kept it.".into());
    }

    if policy
        .kept_roots
        .iter()
        .any(|root| within(&full, root) || within(root, &full))
    {
        return Some("Exo keeps its own backups in Documents.".into());
    }

    let patterns = if allow_app_browser_files {
        HARD_KEPT_SEGMENTS
    } else {
        KEPT_SEGMENTS
    };
    if clean && kept_by(&full, patterns) {
        return Some("Exo keeps this: it holds sign-in data, a credential or a game save.".into());
    }

    let shader = policy.shader_roots.iter().any(|root| within(&full, root))
        || kept_by(&full, SHADER_SEGMENTS);
    if shader {
        if clean {
            return Some("Exo Shaders owns shader caches, so Exo never deletes them.".into());
        }
        if policy.shader_busy {
            return Some(
                "Exo Shaders is compiling a game now, so it keeps the shader caches.".into(),
            );
        }
    }

    if !clean {
        return None;
    }

    if let Some(ref data) = policy.exo_data
        && within(&full, data)
        && !is_exo_backup(&full, data)
    {
        return Some("Exo keeps its own data; only its old backups are cleaned.".into());
    }

    if !owners.is_empty() {
        let Some(ref running) = policy.running_processes else {
            return Some("Exo could not check which programs are running, so it kept this.".into());
        };
        let running_set: HashSet<String> = running.iter().map(|s| s.to_ascii_lowercase()).collect();
        for &owner in owners {
            if running_set.contains(&owner.to_ascii_lowercase()) {
                return Some(format!(
                    "{owner} is running and has these files; close it first."
                ));
            }
        }
    }

    None
}

pub fn check(
    path: &str,
    owners: &[&str],
    policy: &DeletionPolicy,
    allow_app_browser_files: bool,
) -> Option<String> {
    if let Some(why) = refusal(path, true, owners, policy, allow_app_browser_files) {
        return Some(why);
    }
    let p = Path::new(path);
    if p.is_dir() && subtree_kept(p, allow_app_browser_files) {
        return Some(
            "Exo keeps part of this: a sign-in file, a credential or a save is inside it.".into(),
        );
    }
    None
}

pub fn is_backup_name(name: &str) -> bool {
    name.starts_with("exo-launcher-")
        && (name.ends_with(".json")
            || (name.starts_with("exo-launcher-picture-")
                && (name.ends_with(".png") || name.ends_with(".jpg"))))
}

pub fn validate_backup_delete(path: &Path, folder: &Path) -> Result<(), String> {
    let full = normalize_path(path);
    let target_folder = normalize_path(folder);
    let Some(parent) = full.parent() else {
        return Err("Exo deletes only its own old backups.".into());
    };
    let Some(name) = full.file_name().and_then(|s| s.to_str()) else {
        return Err("Exo deletes only its own old backups.".into());
    };
    if !eq_path_ci(parent, &target_folder) || !is_backup_name(name) {
        return Err("Exo deletes only its own old backups.".into());
    }
    Ok(())
}

pub fn is_exo_backup(full: &Path, data: &Path) -> bool {
    let Some(parent) = full.parent() else {
        return false;
    };
    let Some(name) = full.file_name().and_then(|s| s.to_str()) else {
        return false;
    };
    let data_norm = normalize_path(data);
    let lower = name.to_ascii_lowercase();
    eq_path_ci(parent, &data_norm)
        && (lower.starts_with("app.previous") || lower.starts_with("app.incoming"))
}

fn within(full: &Path, root: &Path) -> bool {
    let root_norm = normalize_path(root);
    let full_norm = normalize_path(full);
    if covers(&root_norm, &full_norm) {
        return true;
    }
    let root_res = resolve_deepest(&root_norm);
    let full_res = resolve_deepest(&full_norm);
    covers(&root_res, &full_res)
}

fn covers(root: &Path, full: &Path) -> bool {
    let r = trim_sep(&root.to_string_lossy());
    let f = trim_sep(&full.to_string_lossy());
    if r.is_empty() || f.is_empty() {
        return false;
    }
    if f.eq_ignore_ascii_case(&r) {
        return true;
    }
    let prefix = format!("{r}\\");
    f.len() > prefix.len() && f[..prefix.len()].eq_ignore_ascii_case(&prefix)
}

fn trim_sep(raw: &str) -> String {
    let replaced = raw.replace('/', "\\");
    let stripped = replaced.strip_prefix(r"\\?\").unwrap_or(&replaced);
    if stripped.len() == 3 && stripped.as_bytes()[1] == b':' && stripped.as_bytes()[2] == b'\\' {
        stripped.to_string()
    } else {
        stripped.trim_end_matches('\\').to_string()
    }
}

fn eq_path_ci(a: &Path, b: &Path) -> bool {
    trim_sep(&a.to_string_lossy()).eq_ignore_ascii_case(&trim_sep(&b.to_string_lossy()))
}

fn normalize_path(path: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for comp in path.components() {
        match comp {
            Component::CurDir => {}
            Component::ParentDir => {
                out.pop();
            }
            other => out.push(other.as_os_str()),
        }
    }
    out
}

fn resolve_deepest(path: &Path) -> PathBuf {
    let mut rest: Vec<std::ffi::OsString> = Vec::new();
    let mut cur = Some(path);
    while let Some(step) = cur {
        if let Ok(canon) = fs::canonicalize(step) {
            let mut result = PathBuf::from(trim_sep(&canon.to_string_lossy()));
            for part in rest.iter().rev() {
                result.push(part);
            }
            return result;
        }
        if let Some(name) = step.file_name() {
            rest.push(name.to_os_string());
        }
        cur = step.parent();
    }
    path.to_path_buf()
}

pub fn kept_by(full: &Path, patterns: &[&[&str]]) -> bool {
    let text = full.to_string_lossy();
    let parts: Vec<&str> = text.split(['\\', '/']).filter(|s| !s.is_empty()).collect();
    for pattern in patterns {
        if pattern.is_empty() || pattern.len() > parts.len() {
            continue;
        }
        for start in 0..=parts.len() - pattern.len() {
            if pattern
                .iter()
                .enumerate()
                .all(|(i, pat)| parts[start + i].eq_ignore_ascii_case(pat))
            {
                return true;
            }
        }
    }
    false
}

fn subtree_kept(folder: &Path, allow_app_browser_files: bool) -> bool {
    let patterns = if allow_app_browser_files {
        HARD_KEPT_SEGMENTS
    } else {
        KEPT_SEGMENTS
    };
    let mut stack = vec![folder.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let Ok(entries) = fs::read_dir(&dir) else {
            return true;
        };
        for entry in entries {
            let Ok(entry) = entry else {
                return true;
            };
            let entry_path = entry.path();
            if kept_by(&entry_path, patterns) {
                return true;
            }
            let Ok(meta) = entry.metadata() else {
                continue;
            };
            if meta.file_type().is_symlink() {
                continue;
            }
            if meta.is_dir() {
                stack.push(entry_path);
            }
        }
    }
    false
}

pub const ENGINE_SILENCE: Duration = Duration::from_secs(120);

#[derive(Default)]
struct EngineFlags {
    compiling: bool,
    deleting: bool,
    compiling_at_ms: i64,
    deleting_at_ms: i64,
}

pub struct ShaderCacheLock {
    holds: AtomicUsize,
    flags: Mutex<EngineFlags>,
    last_activity_ms: AtomicI64,
}

impl Default for ShaderCacheLock {
    fn default() -> Self {
        Self::new()
    }
}

impl ShaderCacheLock {
    pub const fn new() -> Self {
        Self {
            holds: AtomicUsize::new(0),
            flags: Mutex::new(EngineFlags {
                compiling: false,
                deleting: false,
                compiling_at_ms: 0,
                deleting_at_ms: 0,
            }),
            last_activity_ms: AtomicI64::new(0),
        }
    }

    pub fn held_at(&self, now_ms: i64) -> bool {
        let silence_ms = ENGINE_SILENCE.as_millis() as i64;
        let guard = self.flags.lock().unwrap_or_else(|e| e.into_inner());
        let compiling =
            guard.compiling && now_ms.saturating_sub(guard.compiling_at_ms) < silence_ms;
        let deleting = guard.deleting && now_ms.saturating_sub(guard.deleting_at_ms) < silence_ms;
        self.holds.load(Ordering::Acquire) > 0 || compiling || deleting
    }

    pub fn report_engine_compiling(&self, compiling: bool, now_ms: i64) {
        let mut guard = self.flags.lock().unwrap_or_else(|e| e.into_inner());
        if compiling {
            self.last_activity_ms.store(now_ms, Ordering::Release);
        }
        guard.compiling = compiling;
        guard.compiling_at_ms = now_ms;
    }

    pub fn report_engine_deleting(&self, deleting: bool, now_ms: i64) {
        let mut guard = self.flags.lock().unwrap_or_else(|e| e.into_inner());
        if deleting || guard.deleting {
            self.last_activity_ms.store(now_ms, Ordering::Release);
        }
        guard.deleting = deleting;
        guard.deleting_at_ms = now_ms;
    }

    pub fn hold(&self) -> ShaderLockGuard<'_> {
        self.holds.fetch_add(1, Ordering::AcqRel);
        self.last_activity_ms
            .store(now_epoch_ms(), Ordering::Release);
        ShaderLockGuard {
            lock: self,
            released: false,
        }
    }

    pub fn last_activity_ms(&self) -> i64 {
        self.last_activity_ms.load(Ordering::Acquire)
    }
}

pub struct ShaderLockGuard<'a> {
    lock: &'a ShaderCacheLock,
    released: bool,
}

impl Drop for ShaderLockGuard<'_> {
    fn drop(&mut self) {
        if !self.released {
            self.released = true;
            self.lock.holds.fetch_sub(1, Ordering::AcqRel);
            self.lock
                .last_activity_ms
                .store(now_epoch_ms(), Ordering::Release);
        }
    }
}

fn now_epoch_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn test_policy() -> DeletionPolicy {
        DeletionPolicy {
            kept_roots: vec![PathBuf::from(r"C:\Users\Test\Documents\Exo\Backups")],
            exo_data: Some(PathBuf::from(r"C:\Users\Test\AppData\Local\ExoLauncher")),
            shader_roots: vec![PathBuf::from(r"C:\Users\Test\AppData\Local\NVIDIA")],
            running_processes: Some(vec!["steam".into(), "discord".into()]),
            shader_busy: false,
        }
    }

    #[test]
    fn refuses_backups_ancestors_saves_credentials_shaders_and_running_owners() {
        let policy = test_policy();
        assert!(
            refusal(
                r"C:\Users\Test\Documents\Exo\Backups\daily.json",
                false,
                &[],
                &policy,
                false
            )
            .is_some()
        );
        assert!(refusal(r"C:\Users\Test\Documents\Exo", false, &[], &policy, false).is_some());
        assert!(refusal(r"C:\Users\Test\.ssh\id_ed25519", true, &[], &policy, false).is_some());
        assert!(refusal(r"C:\Games\SaveGames\slot1.sav", true, &[], &policy, false).is_some());
        assert!(refusal(r"C:\App\Profile\Cookies", true, &[], &policy, false).is_some());
        assert!(refusal(r"C:\App\Profile\Cookies", true, &[], &policy, true).is_none());
        assert!(
            refusal(
                r"C:\Users\Test\AppData\Local\NVIDIA\DXCache",
                true,
                &[],
                &policy,
                false
            )
            .is_some()
        );
        assert!(
            refusal(
                r"D:\SteamLibrary\steamapps\shadercache\730",
                true,
                &[],
                &policy,
                false
            )
            .is_some()
        );
        assert!(
            refusal(
                r"C:\Users\Test\AppData\Local\ExoLauncher\settings.json",
                true,
                &[],
                &policy,
                false
            )
            .is_some()
        );
        assert!(
            refusal(
                r"C:\Users\Test\AppData\Local\ExoLauncher\app.previous.1",
                true,
                &[],
                &policy,
                false
            )
            .is_none()
        );
        assert!(refusal(r"C:\Temp\cache", true, &["Steam"], &policy, false).is_some());
        assert!(
            refusal(
                r"C:\Temp\cache",
                true,
                &["EpicGamesLauncher"],
                &policy,
                false
            )
            .is_none()
        );

        let unknown_running = DeletionPolicy {
            running_processes: None,
            ..policy.clone()
        };
        assert!(
            refusal(
                r"C:\Temp\cache",
                true,
                &["EpicGamesLauncher"],
                &unknown_running,
                false
            )
            .is_some()
        );

        let busy_shader = DeletionPolicy {
            shader_busy: true,
            ..policy
        };
        assert!(
            refusal(
                r"C:\Users\Test\AppData\Local\NVIDIA\DXCache",
                false,
                &[],
                &busy_shader,
                false
            )
            .is_some()
        );
    }

    #[test]
    fn backup_delete_validation_and_shader_lock_expiry() {
        let dir = Path::new(r"C:\Users\Test\Documents\Exo\Backups");
        assert!(validate_backup_delete(&dir.join("exo-launcher-2026-11-24.json"), dir).is_ok());
        assert!(validate_backup_delete(&dir.join("exo-launcher-picture-1.png"), dir).is_ok());
        assert!(validate_backup_delete(&dir.join("notes.txt"), dir).is_err());
        assert!(
            validate_backup_delete(Path::new(r"C:\Other\exo-launcher-2026-11-24.json"), dir)
                .is_err()
        );

        let lock = ShaderCacheLock::new();
        assert!(!lock.held_at(1_000));
        lock.report_engine_compiling(true, 1_000);
        assert!(lock.held_at(60_000));
        assert!(!lock.held_at(122_000));
        {
            let _guard = lock.hold();
            assert!(lock.held_at(122_000));
        }
        assert!(!lock.held_at(122_000));
    }
}
