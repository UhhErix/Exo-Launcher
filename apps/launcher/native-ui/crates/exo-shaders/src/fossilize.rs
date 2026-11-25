use crate::scheduler::{SchedulerAction, SchedulerLimits, TelemetrySample, evaluate_scheduler};
use serde::{Deserialize, Serialize};
use std::fs::{self, File};
use std::io::Read;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

const FOSSILIZE_MAGIC: &[u8; 10] = b"\x81FOSSILIZE";
const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const BELOW_NORMAL_PRIORITY_CLASS: u32 = 0x0000_4000;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FossilizeArchiveInfo {
    pub path: String,
    pub file_bytes: u64,
    pub format_version: u8,
    pub valid: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FossilizeReplayPlan {
    pub app_id: String,
    pub replay_exe: String,
    pub input_archives: Vec<String>,
    pub pipeline_cache_out: String,
    pub num_threads: usize,
    pub args: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "status")]
pub enum FossilizeReplayOutcome {
    Completed {
        elapsed_ms: u64,
        threads_used: usize,
        archives_replayed: usize,
        output_cache_bytes: u64,
    },
    Deferred {
        action: SchedulerAction,
    },
    Aborted {
        action: SchedulerAction,
        elapsed_ms: u64,
    },
    TimedOut {
        elapsed_ms: u64,
    },
    Failed {
        exit_code: Option<i32>,
        reason: String,
    },
}

pub fn inspect_fossilize_archive(path: &Path) -> Option<FossilizeArchiveInfo> {
    let mut file = File::open(path).ok()?;
    let file_bytes = file.metadata().ok()?.len();
    let mut header = [0u8; 16];
    let n = file.read(&mut header).ok()?;
    if n < 16 {
        return None;
    }
    let valid = &header[0..10] == FOSSILIZE_MAGIC;
    let format_version = header[15];
    Some(FossilizeArchiveInfo {
        path: path.display().to_string(),
        file_bytes,
        format_version,
        valid,
    })
}

pub fn detect_default_steam_root() -> Option<PathBuf> {
    for candidate in [r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam"] {
        let p = PathBuf::from(candidate);
        if find_steam_fossilize_replay(&p).is_some() {
            return Some(p);
        }
    }
    None
}

pub fn find_steam_fossilize_replay(steam_root: &Path) -> Option<PathBuf> {
    for rel in [
        r"bin\fossilize-replay64.exe",
        r"bin\fossilize-replay.exe",
        "fossilize-replay64.exe",
    ] {
        let candidate = steam_root.join(rel);
        if candidate.is_file() {
            return Some(candidate);
        }
    }
    None
}

pub fn discover_steam_fossilize_archives(
    steam_library: &Path,
    app_id: &str,
) -> Vec<FossilizeArchiveInfo> {
    let foz_dir = steam_library
        .join("steamapps")
        .join("shadercache")
        .join(app_id)
        .join("fozpipelinesv6");
    let Ok(entries) = fs::read_dir(&foz_dir) else {
        return Vec::new();
    };
    let mut archives = Vec::new();
    for entry in entries.flatten() {
        let path = entry.path();
        if path
            .extension()
            .and_then(|e| e.to_str())
            .is_some_and(|e| e.eq_ignore_ascii_case("foz"))
            && let Some(info) = inspect_fossilize_archive(&path)
            && info.valid
        {
            archives.push(info);
        }
    }
    archives.sort_by(|a, b| a.path.cmp(&b.path));
    archives
}

pub fn plan_fossilize_replay(
    steam_root: &Path,
    steam_library: &Path,
    app_id: &str,
    max_threads: usize,
) -> Option<FossilizeReplayPlan> {
    let replay_exe = find_steam_fossilize_replay(steam_root)?;
    let archives = discover_steam_fossilize_archives(steam_library, app_id);
    if archives.is_empty() {
        return None;
    }
    let foz_dir = steam_library
        .join("steamapps")
        .join("shadercache")
        .join(app_id)
        .join("fozpipelinesv6");
    let pipeline_cache_out = foz_dir.join("steamapp_pipeline_cache.foz");
    let num_threads = max_threads.max(1);
    let input_archives: Vec<String> = archives.into_iter().map(|a| a.path).collect();
    let mut args = vec![
        "--num-threads".to_string(),
        num_threads.to_string(),
        "--on-disk-pipeline-cache".to_string(),
        pipeline_cache_out.display().to_string(),
        "--progress".to_string(),
    ];
    args.extend(input_archives.iter().cloned());
    Some(FossilizeReplayPlan {
        app_id: app_id.to_string(),
        replay_exe: replay_exe.display().to_string(),
        input_archives,
        pipeline_cache_out: pipeline_cache_out.display().to_string(),
        num_threads,
        args,
    })
}

pub fn run_fossilize_replay_with_scheduler<F>(
    plan: &FossilizeReplayPlan,
    limits: SchedulerLimits,
    poll_interval: Duration,
    timeout: Duration,
    mut sample_fn: F,
) -> FossilizeReplayOutcome
where
    F: FnMut() -> TelemetrySample,
{
    let initial = evaluate_scheduler(sample_fn(), limits);
    if initial != SchedulerAction::Run {
        return FossilizeReplayOutcome::Deferred { action: initial };
    }

    let started = Instant::now();
    let mut child = match Command::new(&plan.replay_exe)
        .args(&plan.args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .creation_flags(CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS)
        .spawn()
    {
        Ok(c) => c,
        Err(e) => {
            return FossilizeReplayOutcome::Failed {
                exit_code: None,
                reason: format!("spawn {} failed: {e}", plan.replay_exe),
            };
        }
    };

    loop {
        match child.try_wait() {
            Ok(Some(status)) => {
                let elapsed_ms = started.elapsed().as_millis() as u64;
                if status.success() {
                    let output_cache_bytes = fs::metadata(&plan.pipeline_cache_out)
                        .map(|m| m.len())
                        .unwrap_or(0);
                    return FossilizeReplayOutcome::Completed {
                        elapsed_ms,
                        threads_used: plan.num_threads,
                        archives_replayed: plan.input_archives.len(),
                        output_cache_bytes,
                    };
                }
                return FossilizeReplayOutcome::Failed {
                    exit_code: status.code(),
                    reason: format!("fossilize-replay exited with status {status}"),
                };
            }
            Ok(None) => {}
            Err(e) => {
                let _ = child.kill();
                let _ = child.wait();
                return FossilizeReplayOutcome::Failed {
                    exit_code: None,
                    reason: format!("wait failed: {e}"),
                };
            }
        }

        let action = evaluate_scheduler(sample_fn(), limits);
        if action != SchedulerAction::Run {
            let _ = child.kill();
            let _ = child.wait();
            return FossilizeReplayOutcome::Aborted {
                action,
                elapsed_ms: started.elapsed().as_millis() as u64,
            };
        }

        if started.elapsed() >= timeout {
            let _ = child.kill();
            let _ = child.wait();
            return FossilizeReplayOutcome::TimedOut {
                elapsed_ms: started.elapsed().as_millis() as u64,
            };
        }

        std::thread::sleep(poll_interval);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    #[test]
    fn validates_fossilize_magic() {
        let p = std::env::temp_dir().join(format!("exo-foz-test-{}.foz", std::process::id()));
        let mut buf = [0u8; 32];
        buf[0..10].copy_from_slice(FOSSILIZE_MAGIC);
        buf[15] = 6;
        std::fs::write(&p, buf).unwrap();
        let info = inspect_fossilize_archive(&p).expect("parsed");
        assert!(info.valid);
        assert_eq!(info.format_version, 6);
        let _ = std::fs::remove_file(&p);
    }

    #[test]
    fn plans_steam_fossilize_replay_with_valid_archives_only() {
        let root = std::env::temp_dir().join(format!("exo-foz-plan-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let bin_dir = root.join("bin");
        let foz_dir = root
            .join("steamapps")
            .join("shadercache")
            .join("1620730")
            .join("fozpipelinesv6");
        fs::create_dir_all(&bin_dir).unwrap();
        fs::create_dir_all(&foz_dir).unwrap();
        fs::write(bin_dir.join("fossilize-replay64.exe"), b"MZ").unwrap();

        let mut valid_foz = [0u8; 32];
        valid_foz[0..10].copy_from_slice(FOSSILIZE_MAGIC);
        valid_foz[15] = 6;
        fs::write(foz_dir.join("steamapp_shader_cache.foz"), valid_foz).unwrap();
        fs::write(foz_dir.join("corrupt.foz"), b"not-a-valid-fossilize-header").unwrap();

        let plan = plan_fossilize_replay(&root, &root, "1620730", 8).unwrap();
        assert_eq!(plan.app_id, "1620730");
        assert_eq!(plan.num_threads, 8);
        assert_eq!(plan.input_archives.len(), 1);
        assert!(plan.args.contains(&"--num-threads".to_string()));
        assert!(plan.args.contains(&"--on-disk-pipeline-cache".to_string()));
        assert!(plan.args.contains(&"--progress".to_string()));

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn defers_replay_when_user_active_or_gpu_hot() {
        let plan = FossilizeReplayPlan {
            app_id: "570".into(),
            replay_exe: "cmd.exe".into(),
            input_archives: vec!["a.foz".into()],
            pipeline_cache_out: "out.foz".into(),
            num_threads: 4,
            args: vec!["/C".into(), "exit".into(), "0".into()],
        };
        let limits = SchedulerLimits::default();

        let active = run_fossilize_replay_with_scheduler(
            &plan,
            limits,
            Duration::from_millis(20),
            Duration::from_secs(5),
            || TelemetrySample {
                idle_secs: 5,
                game_running: false,
                gpu_temp_c: Some(55),
            },
        );
        assert_eq!(
            active,
            FossilizeReplayOutcome::Deferred {
                action: SchedulerAction::PauseUserActive
            }
        );

        let hot = run_fossilize_replay_with_scheduler(
            &plan,
            limits,
            Duration::from_millis(20),
            Duration::from_secs(5),
            || TelemetrySample {
                idle_secs: 180,
                game_running: false,
                gpu_temp_c: Some(82),
            },
        );
        assert_eq!(
            hot,
            FossilizeReplayOutcome::Deferred {
                action: SchedulerAction::AbortThermal
            }
        );
    }

    #[test]
    fn aborts_running_replay_process_on_thermal_spike() {
        let plan = FossilizeReplayPlan {
            app_id: "570".into(),
            replay_exe: "ping.exe".into(),
            input_archives: vec!["a.foz".into()],
            pipeline_cache_out: "out.foz".into(),
            num_threads: 4,
            args: vec!["127.0.0.1".into(), "-n".into(), "10".into()],
        };
        let limits = SchedulerLimits::default();
        let ticks = AtomicUsize::new(0);

        let outcome = run_fossilize_replay_with_scheduler(
            &plan,
            limits,
            Duration::from_millis(25),
            Duration::from_secs(10),
            || {
                let n = ticks.fetch_add(1, Ordering::Relaxed);
                TelemetrySample {
                    idle_secs: 180,
                    game_running: false,
                    gpu_temp_c: Some(if n >= 2 { 81 } else { 62 }),
                }
            },
        );
        match outcome {
            FossilizeReplayOutcome::Aborted { action, elapsed_ms } => {
                assert_eq!(action, SchedulerAction::AbortThermal);
                assert!(elapsed_ms < 2000);
            }
            other => panic!("expected Aborted on thermal spike, got {other:?}"),
        }
    }

    #[test]
    fn completes_mock_replay_at_below_normal_priority() {
        let out_file = std::env::temp_dir().join(format!("exo-foz-out-{}.foz", std::process::id()));
        fs::write(&out_file, b"cache-bytes").unwrap();
        let plan = FossilizeReplayPlan {
            app_id: "570".into(),
            replay_exe: "cmd.exe".into(),
            input_archives: vec!["a.foz".into()],
            pipeline_cache_out: out_file.display().to_string(),
            num_threads: 4,
            args: vec!["/C".into(), "exit".into(), "0".into()],
        };
        let limits = SchedulerLimits::default();
        let outcome = run_fossilize_replay_with_scheduler(
            &plan,
            limits,
            Duration::from_millis(20),
            Duration::from_secs(5),
            || TelemetrySample {
                idle_secs: 180,
                game_running: false,
                gpu_temp_c: Some(58),
            },
        );
        match outcome {
            FossilizeReplayOutcome::Completed {
                threads_used,
                archives_replayed,
                output_cache_bytes,
                ..
            } => {
                assert_eq!(threads_used, 4);
                assert_eq!(archives_replayed, 1);
                assert_eq!(output_cache_bytes, 11);
            }
            other => panic!("expected Completed, got {other:?}"),
        }
        let _ = fs::remove_file(&out_file);
    }

    #[test]
    fn discovers_real_steam_fossilize_replay_when_installed() {
        if let Some(root) = detect_default_steam_root() {
            let exe = find_steam_fossilize_replay(&root).expect("replay exe");
            let status = Command::new(&exe)
                .arg("--help")
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .creation_flags(CREATE_NO_WINDOW)
                .status()
                .expect("fossilize-replay --help");
            assert!(status.success());
        }
    }
}
