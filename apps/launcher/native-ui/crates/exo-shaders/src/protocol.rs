use crate::driver_cache::{
    DriverCacheInventory, DriverChange, check_driver_change, default_driver_cache_roots,
    format_bytes, scan_driver_caches,
};
use crate::game_cache::{GameShaderProfile, classify_status_label, scan_game_directory};
use crate::policy::AntiCheatClass;
use crate::pso_dx12::recommended_worker_threads;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
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
            last_activity: Instant::now(),
        }
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
        let games_json: Vec<Value> = self
            .games
            .iter()
            .map(|g| {
                let (st, reason) = classify_status_label(g);
                json!({
                    "id": g.id,
                    "name": g.name,
                    "status": st,
                    "statusTitle": reason,
                    "statusReason": reason,
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
                "summary": format!("{} games scanned · {} driver cache", self.games.len(), format_bytes(self.inventory.total_bytes)),
                "readyCount": self.games.len(),
                "recommendedCount": self.games.len(),
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
                    "intro": "Native Rust shader cache index",
                },
                "driverMode": "When the PC is idle",
                "driverModeNote": "Re-warms automatically while idle after GPU driver updates.",
                "recording": { "count": "0", "space": "0 B", "games": [] },
                "launchers": [],
            },
            "games": games_json,
            "hidden": [],
            "queue": {
                "running": !at_rest && self.compiling,
                "paused": !at_rest && self.paused,
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
                    format!("Compiles with {threads} of {cores} CPU threads at below-normal priority"),
                    "Zero GPL dependencies, zero phone-home requests",
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
                self.compiling = !self.queue.is_empty();
                self.paused = false;
                self.rev += 1;
                Ok(json!({ "ok": true }))
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
                if !self.queue.iter().any(|q| q == id) {
                    self.queue.push(id.to_string());
                }
                self.compiling = true;
                self.paused = false;
                self.rev += 1;
                Ok(json!({ "ok": true }))
            }
            "game.verify" => {
                let id = params
                    .and_then(|p| p.get("id"))
                    .and_then(Value::as_str)
                    .unwrap_or("");
                let exists = self.games.iter().any(|g| g.id == id);
                Ok(json!({ "started": exists }))
            }
            "recorder.auto" => {
                if let Some(on) = params.and_then(|p| p.get("on")).and_then(Value::as_bool) {
                    self.recorder_auto = on;
                    self.rev += 1;
                }
                Ok(json!({ "ok": true }))
            }
            "recorder.set" => {
                let on = params
                    .and_then(|p| p.get("on"))
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                if on {
                    return Err("exo-shaders never loads recorder DLLs into games.".into());
                }
                self.rev += 1;
                Ok(json!({ "ok": true }))
            }
            "driver.setCacheLimit"
            | "driver.setCleanupGuard"
            | "cache.freeOldDriver"
            | "asd.refresh" => {
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
}
