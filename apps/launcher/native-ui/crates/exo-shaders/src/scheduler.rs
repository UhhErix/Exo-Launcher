use serde::{Deserialize, Serialize};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};
use windows::Win32::System::SystemInformation::GetTickCount64;
use windows::Win32::UI::Input::KeyboardAndMouse::{GetLastInputInfo, LASTINPUTINFO};

#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SchedulerLimits {
    pub pause_gpu_temp_c: u32,
    pub abort_gpu_temp_c: u32,
    pub min_idle_secs: u64,
    pub max_threads: usize,
}

impl Default for SchedulerLimits {
    fn default() -> Self {
        let cores = std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(4);
        Self {
            pause_gpu_temp_c: 78,
            abort_gpu_temp_c: 80,
            min_idle_secs: 60,
            max_threads: cores.saturating_sub(2).max(1),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SchedulerAction {
    Run,
    PauseUserActive,
    PauseGameRunning,
    PauseThermal,
    AbortThermal,
}

#[derive(Debug, Clone, Copy)]
pub struct TelemetrySample {
    pub idle_secs: u64,
    pub game_running: bool,
    pub gpu_temp_c: Option<u32>,
}

pub fn evaluate_scheduler(sample: TelemetrySample, limits: SchedulerLimits) -> SchedulerAction {
    if let Some(temp) = sample.gpu_temp_c {
        if temp >= limits.abort_gpu_temp_c {
            return SchedulerAction::AbortThermal;
        }
        if temp > limits.pause_gpu_temp_c {
            return SchedulerAction::PauseThermal;
        }
    }
    if sample.game_running {
        return SchedulerAction::PauseGameRunning;
    }
    if sample.idle_secs < limits.min_idle_secs {
        return SchedulerAction::PauseUserActive;
    }
    SchedulerAction::Run
}

pub fn system_idle_seconds() -> u64 {
    unsafe {
        let mut info = LASTINPUTINFO {
            cbSize: std::mem::size_of::<LASTINPUTINFO>() as u32,
            dwTime: 0,
        };
        if GetLastInputInfo(&mut info).as_bool() {
            let now = GetTickCount64();
            let last = info.dwTime as u64;
            let now32 = now & 0xFFFF_FFFF;
            let diff_ms = now32.wrapping_sub(last) & 0xFFFF_FFFF;
            diff_ms / 1000
        } else {
            0
        }
    }
}

pub fn watch_cancel_signal<F>(
    cancel: Arc<AtomicBool>,
    stop_watcher: Arc<AtomicBool>,
    limits: SchedulerLimits,
    poll_interval: Duration,
    mut sample_fn: F,
) -> std::thread::JoinHandle<Option<SchedulerAction>>
where
    F: FnMut() -> TelemetrySample + Send + 'static,
{
    std::thread::spawn(move || {
        while !stop_watcher.load(Ordering::Acquire) {
            let action = evaluate_scheduler(sample_fn(), limits);
            if action != SchedulerAction::Run {
                cancel.store(true, Ordering::Release);
                return Some(action);
            }
            std::thread::sleep(poll_interval);
        }
        None
    })
}

pub fn measure_simulated_pause_latency_ms(limits: SchedulerLimits) -> f64 {
    let cancel = Arc::new(AtomicBool::new(false));
    let ready = Arc::new((std::sync::Mutex::new(false), std::sync::Condvar::new()));
    let state = Arc::new((std::sync::Mutex::new(false), std::sync::Condvar::new()));
    let ready_bg = Arc::clone(&ready);
    let state_bg = Arc::clone(&state);
    let cancel_bg = Arc::clone(&cancel);

    let handle = std::thread::spawn(move || {
        let (rlock, rcvar) = &*ready_bg;
        {
            let mut is_ready = rlock.lock().unwrap();
            *is_ready = true;
            rcvar.notify_one();
        }
        let (lock, cvar) = &*state_bg;
        let mut started = lock.lock().unwrap();
        while !*started {
            started = cvar.wait(started).unwrap();
        }
        let action = evaluate_scheduler(
            TelemetrySample {
                idle_secs: 120,
                game_running: true,
                gpu_temp_c: Some(62),
            },
            limits,
        );
        if action != SchedulerAction::Run {
            cancel_bg.store(true, Ordering::Release);
        }
    });

    {
        let (rlock, rcvar) = &*ready;
        let mut is_ready = rlock.lock().unwrap();
        while !*is_ready {
            is_ready = rcvar.wait(is_ready).unwrap();
        }
    }

    let t0 = Instant::now();
    {
        let (lock, cvar) = &*state;
        let mut started = lock.lock().unwrap();
        *started = true;
        cvar.notify_one();
    }
    let _ = handle.join();
    assert!(cancel.load(Ordering::Acquire));
    t0.elapsed().as_secs_f64() * 1000.0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn enforces_thermal_and_game_pause_thresholds() {
        let limits = SchedulerLimits::default();
        assert_eq!(
            evaluate_scheduler(
                TelemetrySample {
                    idle_secs: 120,
                    game_running: false,
                    gpu_temp_c: Some(70),
                },
                limits
            ),
            SchedulerAction::Run
        );
        assert_eq!(
            evaluate_scheduler(
                TelemetrySample {
                    idle_secs: 120,
                    game_running: false,
                    gpu_temp_c: Some(79),
                },
                limits
            ),
            SchedulerAction::PauseThermal
        );
        assert_eq!(
            evaluate_scheduler(
                TelemetrySample {
                    idle_secs: 120,
                    game_running: false,
                    gpu_temp_c: Some(80),
                },
                limits
            ),
            SchedulerAction::AbortThermal
        );
        assert_eq!(
            evaluate_scheduler(
                TelemetrySample {
                    idle_secs: 120,
                    game_running: true,
                    gpu_temp_c: Some(65),
                },
                limits
            ),
            SchedulerAction::PauseGameRunning
        );
    }

    #[test]
    fn simulated_pause_latency_is_under_50ms() {
        let latency = measure_simulated_pause_latency_ms(SchedulerLimits::default());
        assert!(
            latency < 50.0,
            "scheduler pause latency {latency:.2} ms exceeded 50 ms"
        );
    }
}
