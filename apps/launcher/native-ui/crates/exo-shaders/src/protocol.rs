use crate::driver_cache::{
    DriverCacheInventory, DriverChange, check_driver_change, default_driver_cache_roots,
    format_bytes, scan_driver_caches,
};
use crate::game_cache::{GameShaderProfile, classify_status_for_game, scan_game_directory};
use crate::policy::{AntiCheatClass, RouteKind, evaluate_route, resolve_anti_cheat_class};
use crate::pso_dx12::{Dx12WarmContext, recommended_worker_threads};
use crate::pso_store::{
    DEFAULT_MAX_RECORDED_PSOS, RECORDER_SOURCE_DLL_NAME, RecorderConfig, RecorderMode,
    deploy_recorder, is_recorder_deployed, pipeline_library_path,
    read_pipeline_library_if_compatible, read_pso_records, recorded_pso_path, remove_recorder,
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::AtomicBool;
use std::time::{Duration, Instant};

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostGameInput {
    pub id: String,
    #[serde(alias = "title")]
    pub name: String,
    #[serde(default)]
    pub install_dir: Option<String>,
    #[serde(default)]
    pub store: Option<String>,
    #[serde(default)]
    pub anti_cheat_class: Option<AntiCheatClass>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ServiceState {
    pub rev: u64,
    pub safe: bool,
    pub ui_visible: bool,
    pub exit_when_idle_secs: Option<u64>,
    pub scanning: bool,
    pub compiling: bool,
    pub paused: bool,
    pub recorder_auto: bool,
    pub queue: Vec<String>,
    pub inventory: DriverCacheInventory,
    pub driver_change: DriverChange,
    pub games: Vec<GameShaderProfile>,
    #[serde(skip)]
    pub data_dir: PathBuf,
    #[serde(skip)]
    pub recorder_dll_path: Option<PathBuf>,
    #[serde(skip)]
    pub last_activity: Instant,
}

impl ServiceState {
    pub fn new(data_dir: PathBuf, safe: bool) -> Self {
        let roots = default_driver_cache_roots();
        let inventory = scan_driver_caches(&roots);
        let driver_change = if safe {
            DriverChange::Unchanged {
                current: crate::driver_cache::DriverStamp {
                    adapter_name: inventory.gpu.name.clone(),
                    driver_version: inventory.gpu.driver_version.clone(),
                    driver_raw: inventory.gpu.driver_raw,
                },
            }
        } else {
            check_driver_change(&data_dir, &inventory.gpu)
        };
        let recorder_dll_path = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(|dir| dir.join(RECORDER_SOURCE_DLL_NAME)))
            .filter(|p| p.is_file());
        Self {
            rev: 1,
            safe,
            ui_visible: true,
            exit_when_idle_secs: None,
            scanning: false,
            compiling: false,
            paused: false,
            recorder_auto: false,
            queue: Vec::new(),
            inventory,
            driver_change,
            games: Vec::new(),
            data_dir,
            recorder_dll_path,
            last_activity: Instant::now(),
        }
    }

    pub fn with_recorder_dll(mut self, path: PathBuf) -> Self {
        self.recorder_dll_path = Some(path);
        self
    }

    pub fn touch(&mut self) {
        self.last_activity = Instant::now();
    }

    pub fn idle_expired(&self) -> bool {
        let Some(secs) = self.exit_when_idle_secs else {
            return false;
        };
        !self.compiling
            && !self.scanning
            && self.queue.is_empty()
            && self.last_activity.elapsed() >= Duration::from_secs(secs.max(5))
    }

    pub fn snapshot_json(&self, at_rest: bool) -> Value {
        let threads = recommended_worker_threads();
        let cores = std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(4);
        let status = if at_rest {
            "idle"
        } else if self.scanning {
            "scanning"
        } else if self.compiling {
            if self.paused { "paused" } else { "compiling" }
        } else {
            "idle"
        };
        let mut ready_count = 0usize;
        let games_json: Vec<Value> = self
            .games
            .iter()
            .map(|g| {
                let has_recorded = recorded_pso_path(&self.data_dir, &g.id).is_file();
                let can_compile_recorded = has_recorded
                    && evaluate_route(
                        g.anti_cheat.class,
                        RouteKind::RecordedLibraryCompile,
                        false,
                        true,
                    )
                    .is_permitted();
                let (st, reason) = classify_status_for_game(g, has_recorded);
                if can_compile_recorded {
                    ready_count += 1;
                }
                let tone = match st {
                    "ready" => "primary",
                    "record" => "warn",
                    _ => "muted",
                };
                json!({
                    "id": g.id,
                    "name": g.name,
                    "installDir": g.install_dir,
                    "status": st,
                    "statusText": reason,
                    "statusTitle": reason,
                    "statusReason": reason,
                    "tone": tone,
                    "inAddAllReady": can_compile_recorded,
                    "actions": {
                        "compile": can_compile_recorded,
                        "compileText": if can_compile_recorded { "Compile" } else { reason },
                    },
                    "pipelines": g.estimated_pipelines,
                    "engine": g.engine,
                    "antiCheatClass": g.anti_cheat.class,
                    "antiCheatMarkers": g.anti_cheat.markers,
                })
            })
            .collect();
        let queue_items: Vec<Value> = if at_rest {
            Vec::new()
        } else {
            self.queue.iter().map(|id| json!({ "id": id })).collect()
        };
        json!({
            "rev": if at_rest { 0 } else { self.rev },
            "app": {
                "status": status,
                "statusLine": if status == "idle" { "Idle" } else { status },
                "scanning": !at_rest && self.scanning,
                "helperImplicitWarm": false,
                "summary": format!("{} games scanned · {} driver cache", self.games.len(), format_bytes(self.inventory.total_bytes)),
                "readyCount": ready_count,
                "recommendedCount": ready_count,
                "waitingCount": queue_items.len(),
                "speedNote": format!("Native MIT Rust · {threads} of {cores} threads"),
                "librarySort": "status",
                "gpu": {
                    "name": self.inventory.gpu.name,
                    "vendor": self.inventory.gpu.vendor,
                    "vendorName": self.inventory.gpu.vendor_name,
                    "driver": self.inventory.gpu.driver_version,
                    "driverLine": format!("Driver {}", self.inventory.gpu.driver_version),
                },
                "cache": {
                    "used": self.inventory.total_bytes,
                    "usedText": format_bytes(self.inventory.total_bytes),
                    "upperBound": false,
                    "limitText": "managed by driver",
                    "limitCurrent": "Auto",
                    "isDriverDefault": false,
                    "percent": 0,
                    "configurable": self.inventory.gpu.vendor == "nvidia",
                    "largest": false,
                    "path": self.inventory.dirs.first().map(|d| d.path.as_str()).unwrap_or(""),
                    "oldDriver": 0,
                    "intro": "Driver cache inventory and recorded D3D12 pipeline libraries",
                },
                "driverMode": "When the PC is idle",
                "driverModeNote": "Rebuilds recorded D3D12 libraries and Vulkan Fossilize caches while idle after GPU driver updates.",
                "recording": { "count": "0", "space": "0 B", "games": [] },
                "launchers": [],
            },
            "games": games_json,
            "hidden": [],
            "queue": {
                "running": !at_rest && self.compiling,
                "paused": !at_rest && self.paused,
                "canStart": ready_count > 0,
                "items": queue_items,
            },
            "settings": {},
            "recorder": { "auto": self.recorder_auto },
            "sharing": { "upload": false },
            "policy": {
                "on": !self.safe,
                "summary": "Clean-room MIT Rust engine. Pauses within 50 ms on input, game launch, or GPU >78 C.",
                "threads": threads,
                "processorCount": cores,
                "memoryGB": 4,
                "ramGB": 16,
                "lines": [
                    format!("Compiles recorded D3D12 PSOs with {threads} of {cores} CPU threads at below-normal priority"),
                    "Driver cache hygiene and Vulkan Fossilize work zero-hook across all games",
                ],
            },
            "manualFolders": [],
        })
    }

    pub fn write_at_rest_snapshot(&self) {
        if self.safe {
            return;
        }
        let path = self.data_dir.join("exo-snapshot.json");
        if let Ok(bytes) = serde_json::to_vec_pretty(&self.snapshot_json(true)) {
            let _ = fs::create_dir_all(&self.data_dir);
            let _ = fs::write(path, bytes);
        }
    }

    pub fn handle_method(&mut self, method: &str, params: Option<&Value>) -> Result<Value, String> {
        self.touch();
        let mutating = matches!(
            method,
            "game.compile"
                | "queue.add"
                | "queue.move"
                | "queue.remove"
                | "queue.start"
                | "queue.pause"
                | "queue.resume"
                | "queue.stop"
                | "recorder.auto"
                | "recorder.set"
                | "driver.setCacheLimit"
                | "driver.setCleanupGuard"
                | "cache.freeOldDriver"
                | "asd.refresh"
                | "game.verify"
        );
        if self.safe && mutating {
            return Err("not available in safe mode".into());
        }

        match method {
            "snapshot" => Ok(self.snapshot_json(false)),
            "ui.visible" => {
                if let Some(v) = params
                    .and_then(|p| p.get("visible"))
                    .and_then(Value::as_bool)
                {
                    self.ui_visible = v;
                }
                Ok(json!({ "ok": true }))
            }
            "exitWhenIdle" => {
                let secs = params
                    .and_then(|p| p.get("seconds"))
                    .and_then(Value::as_u64)
                    .unwrap_or(15);
                self.exit_when_idle_secs = Some(secs.max(5));
                Ok(json!({ "ok": true }))
            }
            "exo.library" => {
                if let Some(arr) = params
                    .and_then(|p| p.get("games"))
                    .and_then(Value::as_array)
                {
                    let mut scanned = Vec::new();
                    let mut seen = HashSet::new();
                    for item in arr {
                        if let Ok(g) = serde_json::from_value::<HostGameInput>(item.clone())
                            && seen.insert(g.id.clone())
                        {
                            let dir = g
                                .install_dir
                                .as_deref()
                                .map(Path::new)
                                .unwrap_or_else(|| Path::new(""));
                            scanned.push(scan_game_directory(
                                &g.id,
                                &g.name,
                                dir,
                                g.anti_cheat_class,
                                None,
                            ));
                        }
                    }
                    self.games = scanned;
                    self.rev += 1;
                }
                Ok(json!({ "ok": true, "count": self.games.len() }))
            }
            "scan" => {
                let roots = default_driver_cache_roots();
                self.inventory = scan_driver_caches(&roots);
                self.rev += 1;
                Ok(json!({
                    "ok": true,
                    "files": self.inventory.total_files,
                    "bytes": self.inventory.total_bytes,
                    "scanUs": self.inventory.scan_us,
                }))
            }
            "queue.add" => {
                if let Some(ids) = params.and_then(|p| p.get("ids")).and_then(Value::as_array) {
                    for id in ids.iter().filter_map(Value::as_str) {
                        if !self.queue.iter().any(|q| q == id) {
                            self.queue.push(id.to_string());
                        }
                    }
                } else if let Some(id) = params.and_then(|p| p.get("id")).and_then(Value::as_str)
                    && !self.queue.iter().any(|q| q == id)
                {
                    self.queue.push(id.to_string());
                }
                self.rev += 1;
                Ok(json!({ "ok": true }))
            }
            "queue.move" => {
                if let (Some(id), Some(index)) = (
                    params.and_then(|p| p.get("id")).and_then(Value::as_str),
                    params.and_then(|p| p.get("index")).and_then(Value::as_u64),
                ) && let Some(pos) = self.queue.iter().position(|q| q == id)
                {
                    let item = self.queue.remove(pos);
                    let dst = (index as usize).min(self.queue.len());
                    self.queue.insert(dst, item);
                    self.rev += 1;
                }
                Ok(json!({ "ok": true }))
            }
            "queue.remove" => {
                if let Some(id) = params.and_then(|p| p.get("id")).and_then(Value::as_str) {
                    self.queue.retain(|q| q != id);
                    self.rev += 1;
                }
                Ok(json!({ "ok": true }))
            }
            "queue.start" => {
                let queued = std::mem::take(&mut self.queue);
                let mut compiled_games = 0usize;
                let mut compiled_psos = 0usize;
                let mut skipped_no_psos = Vec::new();
                let mut skipped_anti_cheat = Vec::new();
                let mut dx12: Option<Dx12WarmContext> = None;
                self.paused = false;
                for id in queued {
                    let class = self
                        .games
                        .iter()
                        .find(|g| g.id == id)
                        .map(|g| g.anti_cheat.class)
                        .unwrap_or(AntiCheatClass::Unknown);
                    if !evaluate_route(class, RouteKind::RecordedLibraryCompile, false, true)
                        .is_permitted()
                    {
                        skipped_anti_cheat.push(id);
                        continue;
                    }
                    let rec_path = recorded_pso_path(&self.data_dir, &id);
                    let records = read_pso_records(&rec_path).unwrap_or_default();
                    if records.is_empty() {
                        skipped_no_psos.push(id);
                        continue;
                    }
                    if dx12.is_none() {
                        dx12 = Some(Dx12WarmContext::new()?);
                    }
                    let lib_path = pipeline_library_path(&self.data_dir, &id);
                    let cancel = Arc::new(AtomicBool::new(false));
                    let summary = dx12.as_ref().unwrap().compile_recorded_corpus(
                        &records,
                        &lib_path,
                        self.inventory.gpu.vendor_id,
                        self.inventory.gpu.device_id,
                        &self.inventory.gpu.driver_version,
                        recommended_worker_threads(),
                        &cancel,
                        true,
                    )?;
                    compiled_games += 1;
                    compiled_psos += summary.compiled;
                }
                self.compiling = false;
                self.rev += 1;
                Ok(json!({
                    "ok": true,
                    "compiledGames": compiled_games,
                    "compiledPsos": compiled_psos,
                    "skippedNoRecordedPsos": skipped_no_psos,
                    "skippedAntiCheat": skipped_anti_cheat,
                }))
            }
            "queue.pause" => {
                self.paused = true;
                self.rev += 1;
                Ok(json!({ "ok": true }))
            }
            "queue.resume" => {
                self.paused = false;
                self.rev += 1;
                Ok(json!({ "ok": true }))
            }
            "queue.stop" => {
                self.compiling = false;
                self.paused = false;
                self.queue.clear();
                self.rev += 1;
                Ok(json!({ "ok": true }))
            }
            "game.compile" => {
                let id = params
                    .and_then(|p| p.get("id"))
                    .and_then(Value::as_str)
                    .ok_or_else(|| "missing game id".to_string())?;
                let class = self
                    .games
                    .iter()
                    .find(|g| g.id == id)
                    .map(|g| g.anti_cheat.class)
                    .unwrap_or(AntiCheatClass::OfflineAntiCheat);
                if !evaluate_route(class, RouteKind::RecordedLibraryCompile, false, true)
                    .is_permitted()
                {
                    self.compiling = false;
                    self.paused = false;
                    self.queue.retain(|q| q != id);
                    self.rev += 1;
                    return Ok(json!({
                        "ok": true,
                        "compiled": 0,
                        "reason": "antiCheatProtected",
                        "note": "Driver cache managed (no proxy — online anti-cheat). Helper-only implicit warming is disabled."
                    }));
                }
                if !self.queue.iter().any(|q| q == id) {
                    self.queue.push(id.to_string());
                }
                let rec_path = recorded_pso_path(&self.data_dir, id);
                let records = read_pso_records(&rec_path).unwrap_or_default();
                if records.is_empty() {
                    self.compiling = false;
                    self.paused = false;
                    self.queue.retain(|q| q != id);
                    self.rev += 1;
                    return Ok(json!({
                        "ok": true,
                        "compiled": 0,
                        "reason": "noRecordedPsos",
                        "note": "No recorded D3D12 PSOs for this game yet. Play one session with the recorder enabled first."
                    }));
                }
                self.compiling = true;
                self.paused = false;
                let lib_path = pipeline_library_path(&self.data_dir, id);
                let cancel = Arc::new(AtomicBool::new(false));
                let summary = Dx12WarmContext::new()?.compile_recorded_corpus(
                    &records,
                    &lib_path,
                    self.inventory.gpu.vendor_id,
                    self.inventory.gpu.device_id,
                    &self.inventory.gpu.driver_version,
                    recommended_worker_threads(),
                    &cancel,
                    true,
                )?;
                self.compiling = false;
                self.queue.retain(|q| q != id);
                self.rev += 1;
                Ok(json!({
                    "ok": true,
                    "compiled": summary.compiled,
                    "failed": summary.failed,
                    "libraryBytes": summary.library_serialized_bytes,
                    "psoCreateMs": summary.pso_create_ms,
                }))
            }
            "game.verify" => {
                let id = params
                    .and_then(|p| p.get("id"))
                    .and_then(Value::as_str)
                    .unwrap_or("");
                let lib_path = pipeline_library_path(&self.data_dir, id);
                let verified = read_pipeline_library_if_compatible(
                    &lib_path,
                    self.inventory.gpu.vendor_id,
                    self.inventory.gpu.device_id,
                    &self.inventory.gpu.driver_version,
                )
                .ok()
                .flatten();
                Ok(json!({
                    "started": false,
                    "verified": verified.is_some(),
                    "psoCount": verified.as_ref().map(|(h, _)| h.pso_count).unwrap_or(0),
                    "reason": if verified.is_some() { "exoplibCompatible" } else { "noCompatibleExoplib" },
                }))
            }
            "recorder.auto" => {
                if let Some(on) = params.and_then(|p| p.get("on")).and_then(Value::as_bool) {
                    self.recorder_auto = on;
                    self.rev += 1;
                }
                Ok(json!({ "ok": true }))
            }
            "recorder.set" => {
                let id = params
                    .and_then(|p| p.get("id"))
                    .and_then(Value::as_str)
                    .ok_or_else(|| "missing game id".to_string())?;
                let on = params
                    .and_then(|p| p.get("on"))
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                let game_running = params
                    .and_then(|p| p.get("gameRunning"))
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                let host_class = params
                    .and_then(|p| p.get("antiCheatClass"))
                    .and_then(|v| serde_json::from_value::<AntiCheatClass>(v.clone()).ok());
                let target_dir = params
                    .and_then(|p| p.get("gameDir"))
                    .and_then(Value::as_str)
                    .map(PathBuf::from)
                    .or_else(|| {
                        self.games
                            .iter()
                            .find(|g| g.id == id)
                            .map(|g| PathBuf::from(&g.install_dir))
                    });

                if !on {
                    if let Some(dir) = target_dir {
                        remove_recorder(&dir, game_running)?;
                    }
                    self.rev += 1;
                    return Ok(json!({ "ok": true, "deployed": false }));
                }

                let dir =
                    target_dir.ok_or_else(|| "game install directory not known".to_string())?;
                let existing_class = self
                    .games
                    .iter()
                    .find(|g| g.id == id)
                    .map(|g| g.anti_cheat.class);
                let merged_host = match (existing_class, host_class) {
                    (Some(a), Some(b)) => Some(a.stricter_of(b)),
                    (Some(a), None) => Some(a),
                    (None, Some(b)) => Some(b),
                    (None, None) => None,
                };
                let resolved = resolve_anti_cheat_class(merged_host, &dir);
                let dll_path = self
                    .recorder_dll_path
                    .as_deref()
                    .ok_or_else(|| "exo_d3d12_recorder.dll is not installed".to_string())?;
                let cfg = RecorderConfig {
                    version: 1,
                    enabled: true,
                    game_id: id.to_string(),
                    mode: RecorderMode::RecordAndWarm,
                    record_path: recorded_pso_path(&self.data_dir, id).display().to_string(),
                    library_path: pipeline_library_path(&self.data_dir, id)
                        .display()
                        .to_string(),
                    expected_adapter_vendor: self.inventory.gpu.vendor_id,
                    expected_adapter_device: self.inventory.gpu.device_id,
                    expected_driver_version: self.inventory.gpu.driver_version.clone(),
                    max_recorded_psos: DEFAULT_MAX_RECORDED_PSOS,
                    stats_path: None,
                };
                deploy_recorder(&dir, dll_path, &cfg, resolved.class, game_running)?;
                self.rev += 1;
                Ok(json!({
                    "ok": true,
                    "deployed": is_recorder_deployed(&dir),
                }))
            }
            "cache.freeOldDriver" => {
                let current = crate::cache_guard::CacheProvenance {
                    adapter_vendor: self.inventory.gpu.vendor_id,
                    adapter_device: self.inventory.gpu.device_id,
                    driver_version: self.inventory.gpu.driver_version.clone(),
                    game_build: None,
                };
                let summary =
                    crate::cache_guard::prune_old_driver_libraries(&self.data_dir, &current)?;
                self.rev += 1;
                Ok(json!({
                    "ok": true,
                    "inspected": summary.inspected,
                    "keptValid": summary.kept_valid,
                    "cleared": summary.cleared,
                    "backedUpBytes": summary.backed_up_bytes,
                    "backupPaths": summary.backup_paths,
                }))
            }
            "driver.setCacheLimit" | "driver.setCleanupGuard" | "asd.refresh" => {
                self.rev += 1;
                Ok(json!({ "ok": true }))
            }
            "shutdown" => Ok(json!({ "ok": true })),
            other => Err(format!("unknown method '{other}'")),
        }
    }
}

pub const SUPPORTED_METHODS: &[&str] = &[
    "snapshot",
    "ui.visible",
    "scan",
    "queue.add",
    "queue.move",
    "queue.remove",
    "queue.start",
    "queue.pause",
    "queue.resume",
    "queue.stop",
    "game.compile",
    "game.verify",
    "recorder.auto",
    "recorder.set",
    "driver.setCacheLimit",
    "driver.setCleanupGuard",
    "cache.freeOldDriver",
    "asd.refresh",
    "exo.library",
    "exitWhenIdle",
    "shutdown",
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn safe_mode_allows_reads_and_blocks_mutations() {
        let dir = std::env::temp_dir().join(format!("exo-proto-{}", std::process::id()));
        let mut st = ServiceState::new(dir, true);
        let snap = st.handle_method("snapshot", None).expect("snapshot ok");
        assert!(snap.get("games").is_some());
        assert!(snap.get("queue").is_some());
        let err = st
            .handle_method("game.compile", Some(&json!({"id": "steam:1"})))
            .unwrap_err();
        assert_eq!(err, "not available in safe mode");
    }

    #[test]
    fn unknown_method_returns_error() {
        let dir = std::env::temp_dir().join(format!("exo-proto2-{}", std::process::id()));
        let mut st = ServiceState::new(dir, true);
        let err = st.handle_method("nope", None).unwrap_err();
        assert_eq!(err, "unknown method 'nope'");
    }

    #[test]
    fn recorder_set_enforces_anticheat_and_removes_cleanly() {
        let root = std::env::temp_dir().join(format!(
            "exo-proto-rec-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ));
        let data_dir = root.join("data");
        let game_dir = root.join("offline_game");
        let eac_dir = root.join("eac_game");
        let _ = fs::create_dir_all(&game_dir);
        let _ = fs::create_dir_all(eac_dir.join("EasyAntiCheat"));
        let dll = root.join(RECORDER_SOURCE_DLL_NAME);
        fs::write(&dll, b"EXO_RECORDER_TEST_DLL").unwrap();

        let mut st = ServiceState::new(data_dir, false).with_recorder_dll(dll);
        let _ = st
            .handle_method(
                "exo.library",
                Some(&json!({
                    "games": [
                        {
                            "id": "steam:offline",
                            "name": "Offline",
                            "installDir": game_dir.display().to_string(),
                            "antiCheatClass": "offlineAntiCheat"
                        },
                        {
                            "id": "steam:eac",
                            "name": "OnlineEAC",
                            "installDir": eac_dir.display().to_string(),
                            "antiCheatClass": "offlineAntiCheat"
                        }
                    ]
                })),
            )
            .unwrap();

        assert!(
            st.handle_method(
                "recorder.set",
                Some(&json!({ "id": "steam:eac", "on": true }))
            )
            .is_err()
        );
        assert!(
            st.handle_method(
                "recorder.set",
                Some(&json!({ "id": "steam:offline", "on": true, "gameRunning": true }))
            )
            .is_err()
        );

        let deployed = st
            .handle_method(
                "recorder.set",
                Some(&json!({ "id": "steam:offline", "on": true })),
            )
            .unwrap();
        assert_eq!(deployed["deployed"], json!(true));
        assert!(is_recorder_deployed(&game_dir));

        let compile_empty = st
            .handle_method("game.compile", Some(&json!({ "id": "steam:offline" })))
            .unwrap();
        assert_eq!(compile_empty["compiled"], json!(0));
        assert_eq!(compile_empty["reason"], json!("noRecordedPsos"));

        let removed = st
            .handle_method(
                "recorder.set",
                Some(&json!({ "id": "steam:offline", "on": false })),
            )
            .unwrap();
        assert_eq!(removed["deployed"], json!(false));
        assert!(!is_recorder_deployed(&game_dir));
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn cache_free_old_driver_keeps_valid_and_backs_up_stale_libraries() {
        let root = std::env::temp_dir().join(format!("exo-proto-prune-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let data_dir = root.join("data");
        let mut st = ServiceState::new(data_dir.clone(), false);
        let vendor = st.inventory.gpu.vendor_id;
        let device = st.inventory.gpu.device_id;
        let current_drv = st.inventory.gpu.driver_version.clone();

        let valid_lib = pipeline_library_path(&data_dir, "steam:valid");
        let stale_lib = pipeline_library_path(&data_dir, "steam:stale");
        crate::pso_format::write_pipeline_library(
            &valid_lib,
            vendor,
            device,
            &current_drv,
            2,
            b"valid",
        )
        .unwrap();
        crate::pso_format::write_pipeline_library(
            &stale_lib, vendor, device, "0.0.old", 2, b"stale",
        )
        .unwrap();

        let res = st.handle_method("cache.freeOldDriver", None).unwrap();
        assert_eq!(res["keptValid"], json!(1));
        assert_eq!(res["cleared"], json!(1));
        assert!(valid_lib.exists());
        assert!(!stale_lib.exists());
        let backup = res["backupPaths"][0].as_str().unwrap();
        assert!(Path::new(backup).exists());

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn queue_start_and_game_compile_never_fake_implicit_warming() {
        let root = std::env::temp_dir().join(format!(
            "exo-proto-b1-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ));
        let data_dir = root.join("data");
        let offline_dir = root.join("offline_game");
        let eac_dir = root.join("eac_game");
        let _ = fs::create_dir_all(&offline_dir);
        let _ = fs::create_dir_all(eac_dir.join("EasyAntiCheat"));

        let mut st = ServiceState::new(data_dir, false);
        let _ = st
            .handle_method(
                "exo.library",
                Some(&json!({
                    "games": [
                        {
                            "id": "steam:offline",
                            "name": "Offline",
                            "installDir": offline_dir.display().to_string(),
                            "antiCheatClass": "offlineAntiCheat"
                        },
                        {
                            "id": "steam:eac",
                            "name": "Competitive",
                            "installDir": eac_dir.display().to_string(),
                            "antiCheatClass": "competitivePvpOnline"
                        }
                    ]
                })),
            )
            .unwrap();

        let snap = st.handle_method("snapshot", None).unwrap();
        assert_eq!(snap["app"]["helperImplicitWarm"], json!(false));
        assert_eq!(snap["app"]["readyCount"], json!(0));
        assert_eq!(snap["queue"]["canStart"], json!(false));
        let games = snap["games"].as_array().unwrap();
        let eac_game = games.iter().find(|g| g["id"] == "steam:eac").unwrap();
        assert_eq!(eac_game["status"], json!("managed"));
        assert_eq!(
            eac_game["statusText"],
            json!("Driver cache managed (no proxy — online anti-cheat)")
        );
        assert_eq!(eac_game["inAddAllReady"], json!(false));

        let ac_compile = st
            .handle_method("game.compile", Some(&json!({ "id": "steam:eac" })))
            .unwrap();
        assert_eq!(ac_compile["compiled"], json!(0));
        assert_eq!(ac_compile["reason"], json!("antiCheatProtected"));

        let _ = st
            .handle_method(
                "queue.add",
                Some(&json!({ "ids": ["steam:offline", "steam:eac"] })),
            )
            .unwrap();
        let qs = st.handle_method("queue.start", None).unwrap();
        assert_eq!(qs["compiledGames"], json!(0));
        assert_eq!(qs["skippedNoRecordedPsos"], json!(["steam:offline"]));
        assert_eq!(qs["skippedAntiCheat"], json!(["steam:eac"]));
        assert!(!st.compiling);

        let _ = fs::remove_dir_all(&root);
    }
}
