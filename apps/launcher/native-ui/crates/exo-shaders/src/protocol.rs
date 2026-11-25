use crate::asd::{AsdStatus, probe_asd_status};
use crate::driver_cache::{
    DriverCacheInventory, DriverChange, backup_and_clear_old_driver_caches, check_driver_change,
    default_driver_cache_roots, default_per_driver_roots, find_stale_old_driver_caches,
    format_bytes, scan_driver_caches,
};
use crate::fossilize::{
    detect_default_steam_root, plan_fossilize_replay, run_fossilize_replay_with_scheduler,
};
use crate::game_cache::{
    GameShaderProfile, PrecompileInvalidationReason, PrecompileNotice, classify_status_for_game,
    evaluate_precompile_notice, scan_game_directory,
};
use crate::nvidia_drs::{
    NvidiaCacheLimitStatus, apply_nvidia_cache_limit, read_nvidia_cache_limit,
};
use crate::policy::{AntiCheatClass, RouteKind, evaluate_route, resolve_anti_cheat_class};
use crate::pso_dx12::{Dx12WarmContext, recommended_worker_threads};
use crate::pso_store::{
    DEFAULT_MAX_RECORDED_PSOS, RECORDER_SOURCE_DLL_NAME, RecorderConfig, RecorderMode,
    deploy_recorder, is_recorder_deployed, pipeline_library_path,
    read_pipeline_library_if_compatible, read_pso_records, recorded_pso_path, remove_recorder,
};
use crate::scheduler::{SchedulerLimits, TelemetrySample, system_idle_seconds};
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
    pub nvidia_cache_limit: NvidiaCacheLimitStatus,
    pub asd_status: AsdStatus,
    pub cleanup_guard_kept: bool,
    pub games: Vec<GameShaderProfile>,
    #[serde(skip)]
    pub data_dir: PathBuf,
    #[serde(skip)]
    pub recorder_dll_path: Option<PathBuf>,
    #[serde(skip)]
    pub per_driver_roots_override: Option<Vec<PathBuf>>,
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
        let nvidia_cache_limit = if inventory.gpu.vendor == "nvidia" {
            read_nvidia_cache_limit()
        } else {
            NvidiaCacheLimitStatus::unavailable()
        };
        let asd_status = probe_asd_status(&[]);
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
            nvidia_cache_limit,
            asd_status,
            cleanup_guard_kept: true,
            games: Vec::new(),
            data_dir,
            recorder_dll_path,
            per_driver_roots_override: None,
            last_activity: Instant::now(),
        }
    }

    pub fn with_recorder_dll(mut self, path: PathBuf) -> Self {
        self.recorder_dll_path = Some(path);
        self
    }

    pub fn with_per_driver_roots(mut self, roots: Vec<PathBuf>) -> Self {
        self.per_driver_roots_override = Some(roots);
        self
    }

    fn effective_per_driver_roots(&self) -> Vec<PathBuf> {
        self.per_driver_roots_override
            .clone()
            .unwrap_or_else(default_per_driver_roots)
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

    fn current_precompile_notices(&self) -> Vec<PrecompileNotice> {
        let reason = match self.driver_change {
            DriverChange::Updated { .. } => Some(PrecompileInvalidationReason::DriverUpdated),
            _ => None,
        };
        self.games
            .iter()
            .filter_map(|g| evaluate_precompile_notice(g, reason))
            .collect()
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
                    "precacheArtifacts": g.precache_artifacts,
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
        let per_drv_roots = self.effective_per_driver_roots();
        let stale_old = find_stale_old_driver_caches(
            &self.data_dir,
            &per_drv_roots,
            self.inventory.gpu.vendor_id,
            self.inventory.gpu.device_id,
            &self.inventory.gpu.driver_version,
        );
        let old_driver_bytes: u64 = stale_old.iter().map(|s| s.bytes).sum();
        let old_driver_note = if old_driver_bytes > 0 {
            Some(format_bytes(old_driver_bytes))
        } else {
            None
        };
        let precompile_notices = self.current_precompile_notices();
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
                    "shaderDelivery": {
                        "works": self.asd_status.os_supported && !self.asd_status.games_with_psdb.is_empty(),
                        "text": self.asd_status.summary,
                    },
                    "windowsCleanup": {
                        "kept": self.cleanup_guard_kept,
                        "canUndo": true,
                        "text": if self.cleanup_guard_kept { "Exo protects shader caches from cleaners" } else { "Cleanup guard off" },
                    },
                },
                "cache": {
                    "used": self.inventory.total_bytes,
                    "usedText": format_bytes(self.inventory.total_bytes),
                    "upperBound": false,
                    "limitText": self.nvidia_cache_limit.limit_current,
                    "limitCurrent": self.nvidia_cache_limit.limit_current,
                    "isDriverDefault": self.nvidia_cache_limit.is_driver_default,
                    "percent": 0,
                    "configurable": self.nvidia_cache_limit.configurable,
                    "largest": self.nvidia_cache_limit.largest,
                    "off": self.nvidia_cache_limit.off_warning,
                    "path": self.inventory.dirs.first().map(|d| d.path.as_str()).unwrap_or(""),
                    "oldDriver": old_driver_bytes,
                    "oldDriverNote": old_driver_note,
                    "intro": "Driver cache inventory and recorded D3D12 pipeline libraries",
                },
                "driverMode": "When the PC is idle",
                "driverModeNote": "Rebuilds recorded D3D12 libraries and Vulkan Fossilize caches while idle after GPU driver updates.",
                "precompileNotices": precompile_notices,
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
                | "fossilize.replay"
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
                    self.asd_status = probe_asd_status(&self.games);
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
            "driver.setCacheLimit" => {
                let game_running = params
                    .and_then(|p| p.get("gameRunning"))
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                if game_running {
                    return Ok(json!({
                        "ok": false,
                        "text": "Close your game before changing shader cache settings.",
                    }));
                }
                if self.inventory.gpu.vendor != "nvidia" {
                    return Ok(json!({
                        "ok": false,
                        "text": "NVIDIA driver profile cache cap is only supported on NVIDIA GPUs.",
                    }));
                }
                let index = params
                    .and_then(|p| p.get("index"))
                    .and_then(Value::as_u64)
                    .ok_or_else(|| "missing index".to_string())? as u32;
                let free_override = params
                    .and_then(|p| p.get("freeGbOverride"))
                    .and_then(Value::as_u64);
                match apply_nvidia_cache_limit(index, free_override) {
                    Ok(status) => {
                        self.nvidia_cache_limit = status.clone();
                        self.rev += 1;
                        Ok(json!({ "ok": true, "cacheLimit": status }))
                    }
                    Err(msg) => Ok(json!({ "ok": false, "text": msg })),
                }
            }
            "driver.setCleanupGuard" => {
                let on = params
                    .and_then(|p| p.get("on"))
                    .and_then(Value::as_bool)
                    .unwrap_or(true);
                self.cleanup_guard_kept = on;
                self.rev += 1;
                Ok(json!({ "ok": true, "kept": self.cleanup_guard_kept }))
            }
            "cache.freeOldDriver" => {
                let game_running = params
                    .and_then(|p| p.get("gameRunning"))
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                if game_running {
                    return Ok(json!({
                        "ok": false,
                        "text": "Close your game before clearing old driver caches.",
                    }));
                }
                let roots = self.effective_per_driver_roots();
                let report = backup_and_clear_old_driver_caches(
                    &self.data_dir,
                    &roots,
                    self.inventory.gpu.vendor_id,
                    self.inventory.gpu.device_id,
                    &self.inventory.gpu.driver_version,
                )?;
                self.rev += 1;
                Ok(json!({
                    "ok": true,
                    "inspected": report.inspected,
                    "keptValid": report.kept_valid,
                    "cleared": report.cleared,
                    "backedUpBytes": report.backed_up_bytes,
                    "backupPaths": report.backup_paths,
                }))
            }
            "asd.refresh" => {
                self.asd_status = probe_asd_status(&self.games);
                self.rev += 1;
                Ok(json!({
                    "ok": true,
                    "asd": self.asd_status,
                }))
            }
            "fossilize.plan" => {
                let app_id = params
                    .and_then(|p| p.get("appId"))
                    .and_then(Value::as_str)
                    .ok_or_else(|| "missing appId".to_string())?;
                let steam_root = params
                    .and_then(|p| p.get("steamRoot"))
                    .and_then(Value::as_str)
                    .map(PathBuf::from)
                    .or_else(detect_default_steam_root)
                    .ok_or_else(|| "Steam fossilize-replay executable not found".to_string())?;
                let steam_library = params
                    .and_then(|p| p.get("steamLibrary"))
                    .and_then(Value::as_str)
                    .map(PathBuf::from)
                    .unwrap_or_else(|| steam_root.clone());
                let max_threads = params
                    .and_then(|p| p.get("threads"))
                    .and_then(Value::as_u64)
                    .map(|n| n as usize)
                    .unwrap_or_else(recommended_worker_threads);
                let plan = plan_fossilize_replay(&steam_root, &steam_library, app_id, max_threads);
                Ok(json!({ "ok": true, "plan": plan }))
            }
            "fossilize.replay" => {
                let app_id = params
                    .and_then(|p| p.get("appId"))
                    .and_then(Value::as_str)
                    .ok_or_else(|| "missing appId".to_string())?;
                let steam_root = params
                    .and_then(|p| p.get("steamRoot"))
                    .and_then(Value::as_str)
                    .map(PathBuf::from)
                    .or_else(detect_default_steam_root)
                    .ok_or_else(|| "Steam fossilize-replay executable not found".to_string())?;
                let steam_library = params
                    .and_then(|p| p.get("steamLibrary"))
                    .and_then(Value::as_str)
                    .map(PathBuf::from)
                    .unwrap_or_else(|| steam_root.clone());
                let limits = SchedulerLimits::default();
                let Some(plan) =
                    plan_fossilize_replay(&steam_root, &steam_library, app_id, limits.max_threads)
                else {
                    return Ok(json!({
                        "ok": true,
                        "replayed": false,
                        "reason": "noValidFossilizeArchives",
                    }));
                };
                let outcome = run_fossilize_replay_with_scheduler(
                    &plan,
                    limits,
                    Duration::from_millis(200),
                    Duration::from_secs(300),
                    || TelemetrySample {
                        idle_secs: system_idle_seconds(),
                        game_running: false,
                        gpu_temp_c: None,
                    },
                );
                self.rev += 1;
                Ok(json!({
                    "ok": true,
                    "plan": plan,
                    "outcome": outcome,
                }))
            }
            "precompile.notices" => {
                let notices = self.current_precompile_notices();
                Ok(json!({
                    "ok": true,
                    "notices": notices,
                }))
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
    "fossilize.plan",
    "fossilize.replay",
    "precompile.notices",
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
        let mut st = ServiceState::new(data_dir.clone(), false).with_per_driver_roots(Vec::new());
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
    fn b2_zero_hook_methods_report_asd_notices_and_stale_cleanup() {
        let root = std::env::temp_dir().join(format!(
            "exo-proto-b2-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ));
        let _ = fs::remove_dir_all(&root);
        let data_dir = root.join("data");
        let ue_dir = root.join("Thymesia");
        fs::create_dir_all(ue_dir.join("Saved").join("PipelineCaches")).unwrap();
        fs::write(
            ue_dir
                .join("Saved")
                .join("PipelineCaches")
                .join("game.upipelinecache"),
            b"UE_CACHE",
        )
        .unwrap();

        let mut st = ServiceState::new(data_dir.clone(), false).with_per_driver_roots(Vec::new());
        st.driver_change = DriverChange::Updated {
            previous: crate::driver_cache::DriverStamp {
                adapter_name: st.inventory.gpu.name.clone(),
                driver_version: "566.36".into(),
                driver_raw: 1,
            },
            current: crate::driver_cache::DriverStamp {
                adapter_name: st.inventory.gpu.name.clone(),
                driver_version: st.inventory.gpu.driver_version.clone(),
                driver_raw: st.inventory.gpu.driver_raw,
            },
        };
        let _ = st
            .handle_method(
                "exo.library",
                Some(&json!({
                    "games": [{
                        "id": "steam:1343240",
                        "name": "Thymesia",
                        "installDir": ue_dir.display().to_string(),
                        "antiCheatClass": "offlineAntiCheat"
                    }]
                })),
            )
            .unwrap();

        let notices = st.handle_method("precompile.notices", None).unwrap();
        let arr = notices["notices"].as_array().unwrap();
        assert_eq!(arr.len(), 1);
        assert_eq!(
            arr[0]["prompt"],
            json!("Shaders need rebuilding for Thymesia: Exo can do it now while you're away")
        );

        let asd = st.handle_method("asd.refresh", None).unwrap();
        assert_eq!(asd["asd"]["d3dscrApiPublic"], json!(true));
        assert_eq!(asd["asd"]["thirdPartyPsdbDownloadAvailable"], json!(false));

        let blocked_cap = st
            .handle_method(
                "driver.setCacheLimit",
                Some(&json!({ "index": 1, "gameRunning": true })),
            )
            .unwrap();
        assert_eq!(blocked_cap["ok"], json!(false));

        let stale_lib = pipeline_library_path(&data_dir, "steam:old");
        crate::pso_format::write_pipeline_library(
            &stale_lib,
            st.inventory.gpu.vendor_id,
            st.inventory.gpu.device_id,
            "0.0.old",
            2,
            b"stale",
        )
        .unwrap();

        let blocked_clean = st
            .handle_method("cache.freeOldDriver", Some(&json!({ "gameRunning": true })))
            .unwrap();
        assert_eq!(blocked_clean["ok"], json!(false));
        assert!(stale_lib.exists());

        let freed = st.handle_method("cache.freeOldDriver", None).unwrap();
        assert_eq!(freed["cleared"], json!(1));
        assert!(!stale_lib.exists());

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

        let mut st = ServiceState::new(data_dir, false).with_per_driver_roots(Vec::new());
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
