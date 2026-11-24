use exo_autopilot::os::Win32Os;
use exo_autopilot::session_pause::{AutopilotJournal, recover_from_journal};
use exo_autopilot::{AutopilotConfig, AutopilotEngine};
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

pub fn default_journal_path() -> PathBuf {
    if let Ok(local_app_data) = std::env::var("LOCALAPPDATA")
        && !local_app_data.trim().is_empty()
    {
        return PathBuf::from(local_app_data)
            .join("ExoLauncher")
            .join("native-autopilot-journal.json");
    }
    std::env::temp_dir().join("exo-native-autopilot-journal.json")
}

pub fn recover_orphaned_session() -> usize {
    let path = default_journal_path();
    if !path.exists() {
        return 0;
    }
    let journal = AutopilotJournal::load(&path);
    if !journal.active_session {
        return 0;
    }
    let os = Win32Os::new();
    let (_, restored) = recover_from_journal(&os, &path);
    restored
}

pub struct NativeAutopilotGuard {
    stop: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

impl NativeAutopilotGuard {
    pub fn start() -> Self {
        let stop = Arc::new(AtomicBool::new(false));
        let stop_bg = stop.clone();
        let handle = std::thread::Builder::new()
            .name("exo-autopilot".into())
            .spawn(move || {
                let os = Arc::new(Win32Os::new());
                let cfg = AutopilotConfig {
                    journal_path: default_journal_path(),
                    ..Default::default()
                };
                let mut engine = AutopilotEngine::new(os, cfg, None);
                let start = Instant::now();
                while !stop_bg.load(Ordering::Relaxed) {
                    std::thread::sleep(Duration::from_millis(500));
                    if stop_bg.load(Ordering::Relaxed) {
                        break;
                    }
                    let now_ms = start.elapsed().as_millis() as u64;
                    engine.sync_focus_from_os(now_ms);
                    engine.tick(now_ms, false);
                }
                engine.on_game_exit();
            })
            .ok();
        Self { stop, handle }
    }
}

impl Drop for NativeAutopilotGuard {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}
