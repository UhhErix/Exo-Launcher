use crate::model::{MacroAction, MacroDef, MacroDelivery, PlaybackMode};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

#[cfg(windows)]
const SPIN_THRESHOLD_MS: f64 = 2.0;

pub trait MacroOutputSink {
    fn emit(&mut self, action: &MacroAction, delivery: MacroDelivery);
}

#[derive(Clone, Debug, Default)]
pub struct DryRunSink {
    pub events: Vec<(MacroAction, MacroDelivery, f64)>,
    origin: Option<Instant>,
}

impl DryRunSink {
    pub fn new() -> Self {
        Self {
            events: Vec::new(),
            origin: Some(Instant::now()),
        }
    }
}

impl MacroOutputSink for DryRunSink {
    fn emit(&mut self, action: &MacroAction, delivery: MacroDelivery) {
        let t = self
            .origin
            .map(|o| o.elapsed().as_secs_f64() * 1000.0)
            .unwrap_or(0.0);
        self.events.push((action.clone(), delivery, t));
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LatencySummary {
    pub samples: usize,
    pub p50_us: f64,
    pub p95_us: f64,
    pub p99_us: f64,
    pub max_us: f64,
    pub target_us: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct StepTrace {
    pub step_id: String,
    pub target_offset_ms: f64,
    pub actual_offset_ms: f64,
    pub error_us: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ExecutionReport {
    pub ok: bool,
    pub aborted: bool,
    pub total_ms: f64,
    pub latency: LatencySummary,
    pub trace: Vec<StepTrace>,
    pub released_stuck_keys: usize,
}

pub struct StuckKeyGuard<'a, S: MacroOutputSink> {
    sink: &'a mut S,
    delivery: MacroDelivery,
    held_keys: HashSet<String>,
    held_mouse: HashSet<String>,
    released_count: usize,
}

impl<'a, S: MacroOutputSink> StuckKeyGuard<'a, S> {
    pub fn new(sink: &'a mut S, delivery: MacroDelivery) -> Self {
        Self {
            sink,
            delivery,
            held_keys: HashSet::new(),
            held_mouse: HashSet::new(),
            released_count: 0,
        }
    }

    pub fn emit(&mut self, action: &MacroAction) {
        match action {
            MacroAction::KeyDown { code } => {
                self.held_keys.insert(code.clone());
            }
            MacroAction::KeyUp { code } => {
                self.held_keys.remove(code);
            }
            MacroAction::MouseDown { button } => {
                self.held_mouse.insert(button.clone());
            }
            MacroAction::MouseUp { button } => {
                self.held_mouse.remove(button);
            }
            _ => {}
        }
        self.sink.emit(action, self.delivery);
    }

    pub fn release_all(&mut self) -> usize {
        let mut released = 0;
        let keys: Vec<String> = self.held_keys.drain().collect();
        for code in keys {
            self.sink.emit(&MacroAction::KeyUp { code }, self.delivery);
            released += 1;
        }
        let buttons: Vec<String> = self.held_mouse.drain().collect();
        for button in buttons {
            self.sink
                .emit(&MacroAction::MouseUp { button }, self.delivery);
            released += 1;
        }
        self.released_count += released;
        released
    }
}

impl<S: MacroOutputSink> Drop for StuckKeyGuard<'_, S> {
    fn drop(&mut self) {
        let _ = self.release_all();
    }
}

#[derive(Default)]
pub struct MacroScheduler;

impl MacroScheduler {
    pub fn execute<S: MacroOutputSink>(
        &self,
        binding: &MacroDef,
        sink: &mut S,
        cancel: &AtomicBool,
    ) -> ExecutionReport {
        let repeats = match binding.trigger.mode {
            PlaybackMode::Repeat => binding.trigger.repeat_count.clamp(1, 100),
            _ => 1,
        };

        let mut trace = Vec::with_capacity(binding.steps.len() * repeats as usize);
        let mut errors_us = Vec::with_capacity(binding.steps.len() * repeats as usize);
        let mut aborted = false;

        let released_stuck_keys;
        let total_ms;

        #[cfg(windows)]
        let _timer_guard = HighResTimerScope::enter();

        {
            let mut guard = StuckKeyGuard::new(sink, binding.delivery);
            let start = Instant::now();
            let mut target_offset_ms = 0.0;

            'outer: for _ in 0..repeats {
                for step in &binding.steps {
                    if cancel.load(Ordering::Acquire) {
                        aborted = true;
                        break 'outer;
                    }

                    let delay_ms = step.delay_ms.max(0.0);
                    target_offset_ms += delay_ms;

                    if delay_ms > 0.0 {
                        let target = start + Duration::from_secs_f64(target_offset_ms / 1000.0);
                        if !wait_until(
                            target,
                            cancel,
                            #[cfg(windows)]
                            _timer_guard.handle,
                        ) {
                            aborted = true;
                            break 'outer;
                        }
                    }

                    let actual_offset_ms = start.elapsed().as_secs_f64() * 1000.0;
                    let error_us = (actual_offset_ms - target_offset_ms).abs() * 1000.0;

                    if !matches!(step.action, MacroAction::Delay) {
                        guard.emit(&step.action);
                    }

                    trace.push(StepTrace {
                        step_id: step.id.clone(),
                        target_offset_ms: round3(target_offset_ms),
                        actual_offset_ms: round3(actual_offset_ms),
                        error_us: round1(error_us),
                    });

                    if delay_ms > 0.0 {
                        errors_us.push(error_us);
                    }
                }
            }

            released_stuck_keys = if aborted { guard.release_all() } else { 0 };
            total_ms = round3(start.elapsed().as_secs_f64() * 1000.0);
        }

        ExecutionReport {
            ok: !aborted,
            aborted,
            total_ms,
            latency: summarize(&errors_us),
            trace,
            released_stuck_keys,
        }
    }
}

pub fn summarize(samples: &[f64]) -> LatencySummary {
    if samples.is_empty() {
        return LatencySummary {
            samples: 0,
            p50_us: 0.0,
            p95_us: 0.0,
            p99_us: 0.0,
            max_us: 0.0,
            target_us: 500.0,
        };
    }
    let mut sorted = samples.to_vec();
    sorted.sort_by(|a, b| a.total_cmp(b));
    LatencySummary {
        samples: sorted.len(),
        p50_us: round1(percentile(&sorted, 0.50)),
        p95_us: round1(percentile(&sorted, 0.95)),
        p99_us: round1(percentile(&sorted, 0.99)),
        max_us: round1(*sorted.last().unwrap_or(&0.0)),
        target_us: 500.0,
    }
}

fn percentile(sorted: &[f64], p: f64) -> f64 {
    if sorted.len() == 1 {
        return sorted[0];
    }
    let idx = ((p * sorted.len() as f64).ceil() as usize)
        .saturating_sub(1)
        .min(sorted.len() - 1);
    sorted[idx]
}

fn round1(v: f64) -> f64 {
    (v * 10.0).round() / 10.0
}

fn round3(v: f64) -> f64 {
    (v * 1000.0).round() / 1000.0
}

#[cfg(windows)]
struct HighResTimerScope {
    handle: windows_sys::Win32::Foundation::HANDLE,
    period_set: bool,
    prev_priority: i32,
}

#[cfg(windows)]
#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreateWaitableTimerExW(
        lp_timer_attributes: *const core::ffi::c_void,
        lp_timer_name: *const u16,
        dw_flags: u32,
        dw_desired_access: u32,
    ) -> windows_sys::Win32::Foundation::HANDLE;
}

#[cfg(windows)]
impl HighResTimerScope {
    fn enter() -> Self {
        use windows_sys::Win32::Media::timeBeginPeriod;
        use windows_sys::Win32::System::Threading::{
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, GetCurrentProcess, GetCurrentThread,
            GetThreadPriority, PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION, PROCESS_POWER_THROTTLING_STATE,
            ProcessPowerThrottling, SetProcessInformation, SetThreadPriority,
            THREAD_PRIORITY_HIGHEST, TIMER_ALL_ACCESS,
        };

        unsafe {
            let ppt = PROCESS_POWER_THROTTLING_STATE {
                Version: PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask: PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask: 0,
            };
            let _ = SetProcessInformation(
                GetCurrentProcess(),
                ProcessPowerThrottling,
                (&raw const ppt).cast(),
                size_of::<PROCESS_POWER_THROTTLING_STATE>() as u32,
            );
            let handle = CreateWaitableTimerExW(
                std::ptr::null(),
                std::ptr::null(),
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                TIMER_ALL_ACCESS,
            );
            let period_set = timeBeginPeriod(1) == 0;
            let thread = GetCurrentThread();
            let prev_priority = GetThreadPriority(thread);
            let _ = SetThreadPriority(thread, THREAD_PRIORITY_HIGHEST);
            Self {
                handle,
                period_set,
                prev_priority,
            }
        }
    }
}

#[cfg(windows)]
impl Drop for HighResTimerScope {
    fn drop(&mut self) {
        use windows_sys::Win32::Foundation::CloseHandle;
        use windows_sys::Win32::Media::timeEndPeriod;
        use windows_sys::Win32::System::Threading::{GetCurrentThread, SetThreadPriority};

        unsafe {
            let _ = SetThreadPriority(GetCurrentThread(), self.prev_priority);
            if self.period_set {
                let _ = timeEndPeriod(1);
            }
            if !self.handle.is_null() {
                let _ = CloseHandle(self.handle);
            }
        }
    }
}

#[cfg(windows)]
fn wait_until(
    target: Instant,
    cancel: &AtomicBool,
    timer: windows_sys::Win32::Foundation::HANDLE,
) -> bool {
    use windows_sys::Win32::System::Threading::{SetWaitableTimerEx, WaitForSingleObject};

    loop {
        if cancel.load(Ordering::Acquire) {
            return false;
        }
        let now = Instant::now();
        if now >= target {
            return true;
        }
        let rem_ms = target.duration_since(now).as_secs_f64() * 1000.0;
        if rem_ms > SPIN_THRESHOLD_MS && !timer.is_null() {
            let sleep_ms = rem_ms - SPIN_THRESHOLD_MS;
            let due_100ns = -((sleep_ms * 10_000.0) as i64);
            if due_100ns < 0 {
                let ok = unsafe {
                    SetWaitableTimerEx(
                        timer,
                        &raw const due_100ns,
                        0,
                        None,
                        std::ptr::null(),
                        std::ptr::null(),
                        0,
                    )
                };
                if ok != 0 {
                    unsafe {
                        let _ = WaitForSingleObject(timer, 50);
                    }
                    continue;
                }
            }
        }
        while Instant::now() < target {
            if cancel.load(Ordering::Acquire) {
                return false;
            }
            std::hint::spin_loop();
        }
        return true;
    }
}

#[cfg(not(windows))]
fn wait_until(target: Instant, cancel: &AtomicBool) -> bool {
    loop {
        if cancel.load(Ordering::Acquire) {
            return false;
        }
        let now = Instant::now();
        if now >= target {
            return true;
        }
        let rem = target.duration_since(now);
        if rem > Duration::from_millis(2) {
            std::thread::sleep(rem - Duration::from_millis(2));
        } else {
            std::hint::spin_loop();
        }
    }
}
