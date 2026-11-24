mod hook_correlator;
mod output_sink;
mod raw_input;

use exo_macros::model::{MacroAction, MacroDef, MacroDelivery, MacroStep, MacroTrigger};
use exo_macros::policy::{AntiCheatClass, GameAntiCheatInput, evaluate_game_policy};
use exo_macros::scheduler::{MacroOutputSink, MacroScheduler};
use hook_correlator::HookCorrelator;
use output_sink::GatedOutputSink;
use raw_input::RawDeviceRegistry;
use serde::{Deserialize, Serialize};
use std::io::{self, BufRead, Write};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

#[derive(Deserialize)]
#[serde(tag = "cmd", rename_all = "camelCase")]
enum WorkerCommand {
    FocusChanged {
        game_id: Option<String>,
        class: Option<AntiCheatClass>,
    },
    RegisterDevice {
        handle: usize,
        raw_path: String,
    },
    RecordRawInput {
        timestamp_us: u64,
        is_mouse: bool,
        code: u16,
        is_down: bool,
        handle: usize,
    },
    CorrelateHook {
        timestamp_us: u64,
        is_mouse: bool,
        code: u16,
        is_down: bool,
    },
    ExecuteDryRun {
        binding: MacroDef,
    },
    PanicRelease,
    Status,
    Shutdown,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct WorkerReply {
    ok: bool,
    ac_paused: bool,
    working_set_kb: u64,
    emitted: u64,
    dropped_by_ac: u64,
    hooks_installed: bool,
    raw_devices: usize,
    correlated_device_id: Option<String>,
    focus_pause_us: Option<f64>,
    qpc_p50_us: Option<f64>,
    qpc_p99_us: Option<f64>,
    qpc_max_us: Option<f64>,
    correlate_ns: Option<f64>,
}

fn main() {
    if std::env::args().any(|a| a == "--self-bench") {
        run_self_bench();
        return;
    }

    let ac_paused = Arc::new(AtomicBool::new(false));
    let mut sink = GatedOutputSink::new(Arc::clone(&ac_paused), true);
    let mut devices = RawDeviceRegistry::default();
    let correlator = HookCorrelator::new();
    let stdin = io::stdin();
    let mut stdout = io::stdout();

    for line in stdin.lock().lines().map_while(Result::ok) {
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        let Ok(cmd) = serde_json::from_str::<WorkerCommand>(trimmed) else {
            continue;
        };
        let mut focus_pause_us = None;
        let mut qpc_p50_us = None;
        let mut qpc_p99_us = None;
        let mut qpc_max_us = None;
        let mut correlated_device_id = None;

        match cmd {
            WorkerCommand::FocusChanged { game_id, class } => {
                let start = Instant::now();
                let paused = match (game_id, class) {
                    (Some(gid), Some(cls)) => {
                        let p = evaluate_game_policy(&GameAntiCheatInput {
                            game_id: gid,
                            title: String::new(),
                            class: cls,
                            local_hint: None,
                            offline_note_acknowledged: false,
                        });
                        !p.software_macros_allowed
                    }
                    (Some(_), None) => true,
                    (None, _) => false,
                };
                ac_paused.store(paused, Ordering::Release);
                if paused {
                    let _ = sink.release_all_held();
                }
                focus_pause_us = Some(start.elapsed().as_secs_f64() * 1_000_000.0);
            }
            WorkerCommand::RegisterDevice { handle, raw_path } => {
                let id = devices.register_path(handle, raw_path).device_id.clone();
                correlated_device_id = Some(id);
            }
            WorkerCommand::RecordRawInput {
                timestamp_us,
                is_mouse,
                code,
                is_down,
                handle,
            } => {
                let packed = HookCorrelator::pack_event(is_mouse, code, is_down);
                correlator.record_raw_input(timestamp_us, packed, handle);
            }
            WorkerCommand::CorrelateHook {
                timestamp_us,
                is_mouse,
                code,
                is_down,
            } => {
                let packed = HookCorrelator::pack_event(is_mouse, code, is_down);
                if let Some(h) = correlator.correlate(timestamp_us, packed) {
                    correlated_device_id = devices.lookup(h).map(|d| d.device_id.clone());
                }
            }
            WorkerCommand::ExecuteDryRun { binding } => {
                let cancel = AtomicBool::new(false);
                let report = MacroScheduler.execute(&binding, &mut sink, &cancel);
                qpc_p50_us = Some(report.latency.p50_us);
                qpc_p99_us = Some(report.latency.p99_us);
                qpc_max_us = Some(report.latency.max_us);
            }
            WorkerCommand::PanicRelease => {
                let _ = sink.release_all_held();
            }
            WorkerCommand::Status => {
                #[cfg(windows)]
                {
                    let _ = RawDeviceRegistry::query_device_name(std::ptr::null_mut());
                    let _ = RawDeviceRegistry::register_input_sink(std::ptr::null_mut());
                }
            }
            WorkerCommand::Shutdown => {
                let _ = sink.release_all_held();
                break;
            }
        }
        let reply = WorkerReply {
            ok: true,
            ac_paused: ac_paused.load(Ordering::Acquire),
            working_set_kb: current_working_set_kb(),
            emitted: sink.emitted_count(),
            dropped_by_ac: sink.dropped_by_ac_count(),
            hooks_installed: false,
            raw_devices: devices.len(),
            correlated_device_id,
            focus_pause_us,
            qpc_p50_us,
            qpc_p99_us,
            qpc_max_us,
            correlate_ns: None,
        };
        if let Ok(json) = serde_json::to_string(&reply) {
            let _ = writeln!(stdout, "{json}");
            let _ = stdout.flush();
        }
    }
}

fn run_self_bench() {
    let ac_paused = Arc::new(AtomicBool::new(false));
    let mut sink = GatedOutputSink::new(Arc::clone(&ac_paused), true);
    let mut devices = RawDeviceRegistry::default();
    let correlator = HookCorrelator::new();

    let dev = devices
        .register_path(
            0x1001,
            r"\\?\HID#VID_046D&PID_C094&MI_00#7&1a2b3c4d&0&0000#{378de44c}".into(),
        )
        .device_id
        .clone();
    let packed = HookCorrelator::pack_event(false, 0x12, true);
    let c0 = Instant::now();
    for i in 0..1_000u64 {
        let ts = 100_000 + i * 100;
        correlator.record_raw_input(ts, packed, 0x1001);
        let _ = correlator.correlate(ts + 50, packed);
    }
    let correlate_ns = (c0.elapsed().as_nanos() as f64) / 1_000.0;
    let correlated_id = correlator
        .correlate(199_950, packed)
        .and_then(|h| devices.lookup(h))
        .map(|d| d.device_id.clone());

    let binding = MacroDef {
        id: "self-bench".into(),
        name: "10ms x 30".into(),
        enabled: true,
        delivery: MacroDelivery::SoftwareSidecar,
        onboard_device_id: None,
        trigger: MacroTrigger::default(),
        steps: (0..30)
            .map(|i| MacroStep {
                id: format!("b{i}"),
                action: if i % 2 == 0 {
                    MacroAction::KeyDown { code: "E".into() }
                } else {
                    MacroAction::KeyUp { code: "E".into() }
                },
                delay_ms: 10.0,
            })
            .collect(),
    };
    let cancel = AtomicBool::new(false);
    let report = MacroScheduler.execute(&binding, &mut sink, &cancel);

    let t0 = Instant::now();
    ac_paused.store(true, Ordering::Release);
    let _ = sink.release_all_held();
    let teardown_us = t0.elapsed().as_secs_f64() * 1_000_000.0;

    sink.emit(
        &MacroAction::KeyDown { code: "E".into() },
        MacroDelivery::SoftwareSidecar,
    );

    let out = WorkerReply {
        ok: report.ok
            && sink.dropped_by_ac_count() == 1
            && correlated_id.as_deref() == Some(dev.as_str()),
        ac_paused: true,
        working_set_kb: current_working_set_kb(),
        emitted: sink.emitted_count(),
        dropped_by_ac: sink.dropped_by_ac_count(),
        hooks_installed: false,
        raw_devices: devices.len(),
        correlated_device_id: correlated_id,
        focus_pause_us: Some((teardown_us * 100.0).round() / 100.0),
        qpc_p50_us: Some(report.latency.p50_us),
        qpc_p99_us: Some(report.latency.p99_us),
        qpc_max_us: Some(report.latency.max_us),
        correlate_ns: Some((correlate_ns * 10.0).round() / 10.0),
    };
    println!("{}", serde_json::to_string(&out).unwrap_or_default());
}

#[cfg(windows)]
fn current_working_set_kb() -> u64 {
    use windows_sys::Win32::System::ProcessStatus::{
        K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS,
    };
    use windows_sys::Win32::System::Threading::GetCurrentProcess;

    let mut pmc = PROCESS_MEMORY_COUNTERS {
        cb: size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
        PageFaultCount: 0,
        PeakWorkingSetSize: 0,
        WorkingSetSize: 0,
        QuotaPeakPagedPoolUsage: 0,
        QuotaPagedPoolUsage: 0,
        QuotaPeakNonPagedPoolUsage: 0,
        QuotaNonPagedPoolUsage: 0,
        PagefileUsage: 0,
        PeakPagefileUsage: 0,
    };
    let ok = unsafe {
        K32GetProcessMemoryInfo(
            GetCurrentProcess(),
            &raw mut pmc,
            size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
        )
    };
    if ok != 0 {
        (pmc.WorkingSetSize / 1024) as u64
    } else {
        0
    }
}

#[cfg(not(windows))]
fn current_working_set_kb() -> u64 {
    0
}
