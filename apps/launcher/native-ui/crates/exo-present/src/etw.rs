use crate::metrics::{FrameMetrics, FrameSample, RollingMetrics};
use crate::state_machine::{
    PresentEventKind, PresentStateMachine, RawPresentEvent, classify_event,
    parse_dxgi_present_fields, wants_kernel_fallback,
};
use parking_lot::Mutex;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};

pub const DEFAULT_SESSION_NAME: &str = "ExoPresentTrace";

#[derive(Clone, Debug)]
pub struct TraceConfig {
    pub session_name: String,
    pub enable_kernel_display: bool,
    pub flush_timer_secs: u32,
    pub record_raw_samples: bool,
    pub record_diag: bool,
}

impl Default for TraceConfig {
    fn default() -> Self {
        Self {
            session_name: DEFAULT_SESSION_NAME.to_string(),
            enable_kernel_display: false,
            flush_timer_secs: 1,
            record_raw_samples: false,
            record_diag: false,
        }
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub struct ProcessResourceUsage {
    pub cpu_kernel_100ns: u64,
    pub cpu_user_100ns: u64,
    pub working_set_bytes: u64,
    pub private_bytes: u64,
    pub logical_cpus: u32,
}

struct SharedState {
    sm: Mutex<PresentStateMachine>,
    metrics: Mutex<RollingMetrics>,
    raw_samples: Mutex<Vec<FrameSample>>,
    diag_counts: Mutex<std::collections::HashMap<(u32, u16), u64>>,
    record_raw: AtomicBool,
    record_diag: AtomicBool,
    active: AtomicBool,
    events_seen: AtomicU64,
    callback_hits: AtomicU64,
    resumed_at_qpc: AtomicI64,
}

impl SharedState {
    fn new(qpc_freq: i64, record_raw: bool, record_diag: bool) -> Self {
        Self {
            sm: Mutex::new(PresentStateMachine::new(qpc_freq)),
            metrics: Mutex::new(RollingMetrics::default()),
            raw_samples: Mutex::new(if record_raw {
                Vec::with_capacity(8192)
            } else {
                Vec::new()
            }),
            diag_counts: Mutex::new(std::collections::HashMap::new()),
            record_raw: AtomicBool::new(record_raw),
            record_diag: AtomicBool::new(record_diag),
            active: AtomicBool::new(true),
            events_seen: AtomicU64::new(0),
            callback_hits: AtomicU64::new(0),
            resumed_at_qpc: AtomicI64::new(qpc_now()),
        }
    }

    fn ingest_raw(&self, ev: RawPresentEvent) {
        self.events_seen.fetch_add(1, Ordering::Relaxed);
        let sample = {
            let mut sm = self.sm.lock();
            sm.ingest(ev)
        };
        if let Some(s) = sample {
            self.metrics.lock().push(s);
            if self.record_raw.load(Ordering::Relaxed) {
                self.raw_samples.lock().push(s);
            }
        }
    }
}

#[cfg(windows)]
mod win {
    use super::*;
    use std::ffi::c_void;
    use std::mem::{size_of, zeroed};
    use std::ptr::null_mut;
    use std::thread::{self, JoinHandle};
    use windows_sys::Win32::Foundation::{
        CloseHandle, ERROR_ALREADY_EXISTS, ERROR_SUCCESS, FILETIME,
    };
    use windows_sys::Win32::Graphics::Gdi::{MONITOR_DEFAULTTONEAREST, MonitorFromWindow};
    use windows_sys::Win32::System::Diagnostics::Etw::{
        CONTROLTRACE_HANDLE, CloseTrace, ControlTraceW, ENABLE_TRACE_PARAMETERS,
        ENABLE_TRACE_PARAMETERS_VERSION_2, EVENT_CONTROL_CODE_DISABLE_PROVIDER,
        EVENT_CONTROL_CODE_ENABLE_PROVIDER, EVENT_ENABLE_PROPERTY_IGNORE_KEYWORD_0,
        EVENT_FILTER_DESCRIPTOR, EVENT_FILTER_TYPE_EVENT_ID, EVENT_FILTER_TYPE_PID,
        EVENT_HEADER_FLAG_32_BIT_HEADER, EVENT_RECORD, EVENT_TRACE_CONTROL_FLUSH,
        EVENT_TRACE_CONTROL_QUERY, EVENT_TRACE_CONTROL_STOP, EVENT_TRACE_LOGFILEW,
        EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING, EVENT_TRACE_PROPERTIES, EVENT_TRACE_REAL_TIME_MODE,
        EnableTraceEx2, OpenTraceW, PROCESS_TRACE_MODE_EVENT_RECORD,
        PROCESS_TRACE_MODE_RAW_TIMESTAMP, PROCESS_TRACE_MODE_REAL_TIME, PROCESSTRACE_HANDLE,
        ProcessTrace, StartTraceW, TRACE_LEVEL_INFORMATION,
    };
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW,
        TH32CS_SNAPPROCESS,
    };
    use windows_sys::Win32::System::Performance::{
        QueryPerformanceCounter, QueryPerformanceFrequency,
    };
    use windows_sys::Win32::System::ProcessStatus::{
        K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX,
    };
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        GetForegroundWindow, GetWindowThreadProcessId,
    };
    use windows_sys::core::GUID;

    const WNODE_FLAG_TRACED_GUID: u32 = 0x0002_0000;
    const INVALID_PROCESSTRACE_HANDLE: u64 = u64::MAX;

    const DXGI_GUID: GUID = GUID {
        data1: 0xCA11_C036,
        data2: 0x0102,
        data3: 0x4A2D,
        data4: [0xA6, 0xAD, 0xF0, 0x3C, 0xFE, 0xD5, 0xD3, 0xC9],
    };
    const D3D9_GUID: GUID = GUID {
        data1: 0x783A_CA0A,
        data2: 0x790E,
        data3: 0x4D7F,
        data4: [0x84, 0x51, 0xAA, 0x85, 0x05, 0x11, 0xC6, 0xB9],
    };
    const DXGKRNL_GUID: GUID = GUID {
        data1: 0x802E_C45A,
        data2: 0x1E99,
        data3: 0x4B83,
        data4: [0x99, 0x20, 0x87, 0xC9, 0x82, 0x77, 0xBA, 0x9D],
    };
    const DXGI_KEYWORDS: u64 = 0x8000_0000_0000_0002;
    const D3D9_KEYWORDS: u64 = 0x8000_0000_0000_0002;
    const DXGKRNL_KEYWORDS: u64 = 0x0800_0000;
    const DXGI_EVENT_IDS: [u16; 4] = [42, 43, 55, 56];
    const D3D9_EVENT_IDS: [u16; 2] = [1, 2];
    const DXGKRNL_EVENT_IDS: [u16; 8] = [166, 168, 171, 184, 215, 252, 17, 273];
    const MAX_FILTERED_PIDS: usize = 8;

    #[repr(C)]
    struct TracePropertiesBuf {
        props: EVENT_TRACE_PROPERTIES,
        name: [u16; 128],
    }

    fn make_session_guid(name: &str) -> GUID {
        let mut h1: u64 = 0xcbf2_9ce4_8422_2325;
        let mut h2: u64 = 0x8422_2325_cbf2_9ce4;
        for b in name.bytes() {
            h1 ^= b as u64;
            h1 = h1.wrapping_mul(0x0000_0100_0000_01b3);
            h2 = h2.wrapping_mul(0x0000_0100_0000_01b3) ^ (b as u64).rotate_left(3);
        }
        let b1 = h1.to_le_bytes();
        let b2 = h2.to_le_bytes();
        GUID {
            data1: u32::from_le_bytes([b1[0], b1[1], b1[2], b1[3]]),
            data2: u16::from_le_bytes([b1[4], b1[5]]),
            data3: u16::from_le_bytes([b1[6], b1[7]]),
            data4: b2,
        }
    }

    fn build_properties(session_name: &str, flush_secs: u32) -> TracePropertiesBuf {
        let mut buf: TracePropertiesBuf = unsafe { zeroed() };
        buf.props.Wnode.BufferSize = size_of::<TracePropertiesBuf>() as u32;
        buf.props.Wnode.Guid = make_session_guid(session_name);
        buf.props.Wnode.ClientContext = 1;
        buf.props.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
        buf.props.BufferSize = 64;
        buf.props.MinimumBuffers = 4;
        buf.props.MaximumBuffers = 32;
        buf.props.LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING;
        buf.props.FlushTimer = flush_secs.max(1);
        buf.props.LoggerNameOffset = size_of::<EVENT_TRACE_PROPERTIES>() as u32;
        buf
    }

    fn encode_wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    pub fn qpc_freq() -> i64 {
        let mut freq = 10_000_000i64;
        unsafe {
            QueryPerformanceFrequency(&mut freq);
        }
        freq.max(1)
    }

    pub fn qpc_now() -> i64 {
        let mut now = 0i64;
        unsafe {
            QueryPerformanceCounter(&mut now);
        }
        now
    }

    pub fn foreground_pid_and_monitor() -> (u32, isize) {
        unsafe {
            let hwnd = GetForegroundWindow();
            if hwnd.is_null() {
                return (0, 0);
            }
            let mut pid = 0u32;
            GetWindowThreadProcessId(hwnd, &mut pid);
            let hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            (pid, hmon as isize)
        }
    }

    pub fn find_pids_by_name(exe_name: &str) -> Vec<u32> {
        let target = exe_name.trim().to_ascii_lowercase();
        let mut pids = Vec::new();
        if target.is_empty() {
            return pids;
        }
        unsafe {
            let snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if snap.is_null() || snap == (-1isize as *mut c_void) {
                return pids;
            }
            let mut entry: PROCESSENTRY32W = zeroed();
            entry.dwSize = size_of::<PROCESSENTRY32W>() as u32;
            if Process32FirstW(snap, &mut entry) != 0 {
                loop {
                    let end = entry
                        .szExeFile
                        .iter()
                        .position(|&c| c == 0)
                        .unwrap_or(entry.szExeFile.len());
                    let name = String::from_utf16_lossy(&entry.szExeFile[..end]);
                    if name.eq_ignore_ascii_case(&target) {
                        pids.push(entry.th32ProcessID);
                    }
                    if Process32NextW(snap, &mut entry) == 0 {
                        break;
                    }
                }
            }
            CloseHandle(snap);
        }
        pids
    }

    pub fn current_process_usage() -> ProcessResourceUsage {
        unsafe {
            let proc = GetCurrentProcess();
            let mut creation: FILETIME = zeroed();
            let mut exit: FILETIME = zeroed();
            let mut kernel: FILETIME = zeroed();
            let mut user: FILETIME = zeroed();
            let _ = GetProcessTimes(proc, &mut creation, &mut exit, &mut kernel, &mut user);
            let k = ((kernel.dwHighDateTime as u64) << 32) | (kernel.dwLowDateTime as u64);
            let u = ((user.dwHighDateTime as u64) << 32) | (user.dwLowDateTime as u64);

            let mut mem: PROCESS_MEMORY_COUNTERS_EX = zeroed();
            mem.cb = size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32;
            let _ = K32GetProcessMemoryInfo(
                proc,
                (&mut mem as *mut PROCESS_MEMORY_COUNTERS_EX).cast(),
                size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
            );
            let logical = std::thread::available_parallelism()
                .map(|n| n.get() as u32)
                .unwrap_or(1);
            ProcessResourceUsage {
                cpu_kernel_100ns: k,
                cpu_user_100ns: u,
                working_set_bytes: mem.WorkingSetSize as u64,
                private_bytes: mem.PrivateUsage as u64,
                logical_cpus: logical,
            }
        }
    }

    pub fn stop_session_by_name(session_name: &str) {
        let wide = encode_wide(session_name);
        let mut buf = build_properties(session_name, 1);
        unsafe {
            ControlTraceW(
                CONTROLTRACE_HANDLE { Value: 0 },
                wide.as_ptr(),
                &mut buf.props,
                EVENT_TRACE_CONTROL_STOP,
            );
        }
    }

    unsafe extern "system" fn on_event_record(record: *mut EVENT_RECORD) {
        if record.is_null() {
            return;
        }
        let rec = unsafe { &*record };
        if rec.UserContext.is_null() {
            return;
        }
        let shared = unsafe { &*(rec.UserContext.cast::<SharedState>()) };
        shared.callback_hits.fetch_add(1, Ordering::Relaxed);
        let p1 = rec.EventHeader.ProviderId.data1;
        let eid = rec.EventHeader.EventDescriptor.Id;
        if shared.record_diag.load(Ordering::Relaxed) {
            *shared.diag_counts.lock().entry((p1, eid)).or_insert(0) += 1;
        }
        let kind = classify_event(p1, eid);
        if kind == PresentEventKind::None {
            return;
        }
        let (swapchain_ptr, present_flags) = if kind == PresentEventKind::DxgiStart
            && !rec.UserData.is_null()
            && rec.UserDataLength >= 8
        {
            let is_32 = (rec.EventHeader.Flags & (EVENT_HEADER_FLAG_32_BIT_HEADER as u16)) != 0;
            let slice = unsafe {
                std::slice::from_raw_parts(rec.UserData.cast::<u8>(), rec.UserDataLength as usize)
            };
            parse_dxgi_present_fields(is_32, slice)
        } else {
            (0, 0)
        };

        shared.ingest_raw(RawPresentEvent {
            pid: rec.EventHeader.ProcessId,
            tid: rec.EventHeader.ThreadId,
            qpc_ticks: rec.EventHeader.TimeStamp,
            kind,
            event_id: eid,
            present_flags,
            swapchain_ptr,
        });
    }

    pub struct LiveEtwSession {
        session_name: String,
        session_handle: CONTROLTRACE_HANDLE,
        consumer_handle: PROCESSTRACE_HANDLE,
        shared: Arc<SharedState>,
        ctx_ptr: *const SharedState,
        thread: Option<JoinHandle<()>>,
        watched_pids: Vec<u32>,
        kernel_enabled: bool,
        want_kernel_display: bool,
        flush_secs: u32,
        qpc_freq: i64,
    }

    unsafe impl Send for LiveEtwSession {}

    impl LiveEtwSession {
        pub fn start(cfg: TraceConfig) -> Result<Self, u32> {
            stop_session_by_name(&cfg.session_name);
            let wide = encode_wide(&cfg.session_name);
            let mut props = build_properties(&cfg.session_name, cfg.flush_timer_secs);
            let mut session_handle = CONTROLTRACE_HANDLE { Value: 0 };
            let mut err =
                unsafe { StartTraceW(&mut session_handle, wide.as_ptr(), &mut props.props) };
            if err == ERROR_ALREADY_EXISTS {
                stop_session_by_name(&cfg.session_name);
                props = build_properties(&cfg.session_name, cfg.flush_timer_secs);
                err = unsafe { StartTraceW(&mut session_handle, wide.as_ptr(), &mut props.props) };
            }
            if err != ERROR_SUCCESS {
                return Err(err);
            }

            let freq = qpc_freq();
            let shared = Arc::new(SharedState::new(
                freq,
                cfg.record_raw_samples,
                cfg.record_diag,
            ));
            let ctx_ptr = Arc::into_raw(Arc::clone(&shared));

            let mut logfile: EVENT_TRACE_LOGFILEW = unsafe { zeroed() };
            let mut name_mut = wide.clone();
            logfile.LoggerName = name_mut.as_mut_ptr();
            logfile.Anonymous1.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME
                | PROCESS_TRACE_MODE_EVENT_RECORD
                | PROCESS_TRACE_MODE_RAW_TIMESTAMP;
            logfile.Anonymous2.EventRecordCallback = Some(on_event_record);
            logfile.Context = ctx_ptr as *mut c_void;

            let consumer_handle = unsafe { OpenTraceW(&mut logfile) };
            if consumer_handle.Value == INVALID_PROCESSTRACE_HANDLE {
                let last_err = unsafe { windows_sys::Win32::Foundation::GetLastError() };
                unsafe {
                    let _ = Arc::from_raw(ctx_ptr);
                }
                stop_session_by_name(&cfg.session_name);
                return Err(last_err);
            }

            let handle_val = consumer_handle.Value;
            let thread = thread::Builder::new()
                .name("exo-present-etw".into())
                .spawn(move || {
                    let h = PROCESSTRACE_HANDLE { Value: handle_val };
                    unsafe {
                        let _ = ProcessTrace(&h, 1, null_mut(), null_mut());
                    }
                })
                .map_err(|_| 8u32)?;

            let mut sess = Self {
                session_name: cfg.session_name,
                session_handle,
                consumer_handle,
                shared,
                ctx_ptr,
                thread: Some(thread),
                watched_pids: Vec::new(),
                kernel_enabled: false,
                want_kernel_display: cfg.enable_kernel_display,
                flush_secs: cfg.flush_timer_secs,
                qpc_freq: freq,
            };
            sess.enable_api_providers();
            if sess.want_kernel_display {
                sess.set_kernel_enabled(true);
            }
            Ok(sess)
        }

        pub fn watch(&mut self, pids: &[u32]) {
            if self.watched_pids == pids {
                return;
            }
            self.watched_pids.clear();
            self.watched_pids.extend_from_slice(pids);
            self.shared.sm.lock().watch(pids);
            self.shared
                .resumed_at_qpc
                .store(qpc_now(), Ordering::Relaxed);
            self.enable_api_providers();
        }

        pub fn flush(&self) {
            let wide = encode_wide(&self.session_name);
            let mut props = build_properties(&self.session_name, self.flush_secs);
            unsafe {
                ControlTraceW(
                    CONTROLTRACE_HANDLE { Value: 0 },
                    wide.as_ptr(),
                    &mut props.props,
                    EVENT_TRACE_CONTROL_FLUSH,
                );
            }
        }

        pub fn query_buffers_and_lost(&self) -> (u32, u32) {
            let wide = encode_wide(&self.session_name);
            let mut props = build_properties(&self.session_name, self.flush_secs);
            unsafe {
                ControlTraceW(
                    CONTROLTRACE_HANDLE { Value: 0 },
                    wide.as_ptr(),
                    &mut props.props,
                    EVENT_TRACE_CONTROL_QUERY,
                );
            }
            (props.props.BuffersWritten, props.props.EventsLost)
        }

        pub fn check_kernel_fallback(&mut self) {
            if self.want_kernel_display {
                return;
            }
            let now = qpc_now();
            let resumed = self.shared.resumed_at_qpc.load(Ordering::Relaxed);
            let elapsed_s = (now - resumed).max(0) as f64 / self.qpc_freq as f64;
            let api_recent = self.shared.sm.lock().api_seen_recently(now);
            let want = wants_kernel_fallback(api_recent, elapsed_s);
            if want != self.kernel_enabled {
                self.set_kernel_enabled(want);
                if want {
                    self.flush();
                }
            }
        }

        pub fn snapshot(&mut self) -> FrameMetrics {
            self.check_kernel_fallback();
            self.shared.metrics.lock().snapshot()
        }

        pub fn recent_frame_times(&self, max_count: usize) -> Vec<f64> {
            self.shared.metrics.lock().recent_frame_times(max_count)
        }

        pub fn drain_raw_samples(&self) -> Vec<FrameSample> {
            let mut guard = self.shared.raw_samples.lock();
            std::mem::take(&mut *guard)
        }

        pub fn events_seen(&self) -> u64 {
            self.shared.events_seen.load(Ordering::Relaxed)
        }

        pub fn callback_hits(&self) -> u64 {
            self.shared.callback_hits.load(Ordering::Relaxed)
        }

        pub fn qpc_frequency(&self) -> i64 {
            self.qpc_freq
        }

        pub fn diag_top_events(&self, limit: usize) -> Vec<(u32, u16, u64)> {
            let mut items: Vec<(u32, u16, u64)> = self
                .shared
                .diag_counts
                .lock()
                .iter()
                .map(|(&(p, id), &c)| (p, id, c))
                .collect();
            items.sort_by_key(|a| std::cmp::Reverse(a.2));
            items.truncate(limit);
            items
        }

        fn enable_api_providers(&self) {
            let pids: &[u32] = if self.watched_pids.len() <= MAX_FILTERED_PIDS {
                &self.watched_pids
            } else {
                &[]
            };
            self.enable_provider(
                &DXGI_GUID,
                TRACE_LEVEL_INFORMATION as u8,
                DXGI_KEYWORDS,
                DXGI_KEYWORDS,
                &DXGI_EVENT_IDS,
                pids,
            );
            self.enable_provider(
                &D3D9_GUID,
                TRACE_LEVEL_INFORMATION as u8,
                D3D9_KEYWORDS,
                D3D9_KEYWORDS,
                &D3D9_EVENT_IDS,
                pids,
            );
        }

        fn set_kernel_enabled(&mut self, on: bool) {
            self.kernel_enabled = on;
            if on {
                self.enable_provider(
                    &DXGKRNL_GUID,
                    TRACE_LEVEL_INFORMATION as u8,
                    DXGKRNL_KEYWORDS,
                    DXGKRNL_KEYWORDS,
                    &DXGKRNL_EVENT_IDS,
                    &[],
                );
            } else {
                unsafe {
                    EnableTraceEx2(
                        self.session_handle,
                        &DXGKRNL_GUID,
                        EVENT_CONTROL_CODE_DISABLE_PROVIDER,
                        0,
                        0,
                        0,
                        0,
                        null_mut(),
                    );
                }
            }
        }

        fn enable_provider(
            &self,
            provider: &GUID,
            level: u8,
            match_any: u64,
            match_all: u64,
            event_ids: &[u16],
            pids: &[u32],
        ) {
            if self.shared.record_diag.load(Ordering::Relaxed) {
                unsafe {
                    let _ = EnableTraceEx2(
                        self.session_handle,
                        provider,
                        EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                        level,
                        match_any,
                        match_all,
                        0,
                        null_mut(),
                    );
                }
                return;
            }
            let mut id_buf = [0u16; 16];
            let count = event_ids.len().min(id_buf.len() - 2);
            id_buf[0] = 1;
            id_buf[1] = count as u16;
            id_buf[2..2 + count].copy_from_slice(&event_ids[..count]);

            let mut descs = [
                EVENT_FILTER_DESCRIPTOR {
                    Ptr: id_buf.as_ptr() as u64,
                    Size: ((2 + count) * size_of::<u16>()) as u32,
                    Type: EVENT_FILTER_TYPE_EVENT_ID,
                },
                EVENT_FILTER_DESCRIPTOR {
                    Ptr: pids.as_ptr() as u64,
                    Size: std::mem::size_of_val(pids) as u32,
                    Type: EVENT_FILTER_TYPE_PID,
                },
            ];
            let mut params: ENABLE_TRACE_PARAMETERS = unsafe { zeroed() };
            params.Version = ENABLE_TRACE_PARAMETERS_VERSION_2;
            params.EnableProperty = EVENT_ENABLE_PROPERTY_IGNORE_KEYWORD_0;
            params.EnableFilterDesc = descs.as_mut_ptr();
            params.FilterDescCount = if pids.is_empty() { 1 } else { 2 };

            unsafe {
                let _ = EnableTraceEx2(
                    self.session_handle,
                    provider,
                    EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                    level,
                    match_any,
                    match_all,
                    0,
                    &params,
                );
            }
        }
    }

    impl Drop for LiveEtwSession {
        fn drop(&mut self) {
            self.shared.active.store(false, Ordering::SeqCst);
            stop_session_by_name(&self.session_name);
            if let Some(handle) = self.thread.take() {
                let _ = handle.join();
            }
            if self.consumer_handle.Value != 0
                && self.consumer_handle.Value != INVALID_PROCESSTRACE_HANDLE
            {
                unsafe {
                    let _ = CloseTrace(self.consumer_handle);
                }
            }
            if !self.ctx_ptr.is_null() {
                unsafe {
                    let _ = Arc::from_raw(self.ctx_ptr);
                }
                self.ctx_ptr = std::ptr::null();
            }
        }
    }
}

#[cfg(not(windows))]
mod win {
    use super::*;

    pub fn qpc_freq() -> i64 {
        1_000_000_000
    }

    pub fn qpc_now() -> i64 {
        0
    }

    pub fn foreground_pid_and_monitor() -> (u32, isize) {
        (0, 0)
    }

    pub fn find_pids_by_name(_exe_name: &str) -> Vec<u32> {
        Vec::new()
    }

    pub fn current_process_usage() -> ProcessResourceUsage {
        ProcessResourceUsage::default()
    }

    pub fn stop_session_by_name(_session_name: &str) {}

    pub struct LiveEtwSession {
        shared: Arc<SharedState>,
    }

    impl LiveEtwSession {
        pub fn start(cfg: TraceConfig) -> Result<Self, u32> {
            Ok(Self {
                shared: Arc::new(SharedState::new(
                    qpc_freq(),
                    cfg.record_raw_samples,
                    cfg.record_diag,
                )),
            })
        }

        pub fn watch(&mut self, pids: &[u32]) {
            self.shared.sm.lock().watch(pids);
        }

        pub fn flush(&self) {}

        pub fn query_buffers_and_lost(&self) -> (u32, u32) {
            (0, 0)
        }

        pub fn check_kernel_fallback(&mut self) {}

        pub fn snapshot(&mut self) -> FrameMetrics {
            self.shared.metrics.lock().snapshot()
        }

        pub fn recent_frame_times(&self, max_count: usize) -> Vec<f64> {
            self.shared.metrics.lock().recent_frame_times(max_count)
        }

        pub fn drain_raw_samples(&self) -> Vec<FrameSample> {
            std::mem::take(&mut *self.shared.raw_samples.lock())
        }

        pub fn events_seen(&self) -> u64 {
            self.shared.events_seen.load(Ordering::Relaxed)
        }

        pub fn callback_hits(&self) -> u64 {
            self.shared.callback_hits.load(Ordering::Relaxed)
        }

        pub fn qpc_frequency(&self) -> i64 {
            qpc_freq()
        }

        pub fn diag_top_events(&self, _limit: usize) -> Vec<(u32, u16, u64)> {
            Vec::new()
        }
    }
}

pub use win::{
    LiveEtwSession, current_process_usage, find_pids_by_name, foreground_pid_and_monitor, qpc_freq,
    qpc_now, stop_session_by_name,
};
