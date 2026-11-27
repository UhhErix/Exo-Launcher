use crate::state::AppState;
use exo_kit::{
    BG, ExoGlass, FG, RADIUS_CARD, apply_glass, fg_faint, fg_muted, float_line, glass_button_opt,
    glass_icon_circle_opt, icon_close, icon_external, icon_refresh, modal_card_transition,
    modal_scrim_transition, scrim_fill, type_body, type_section, type_small,
};
#[cfg(not(windows))]
use rune::NativeSurface;
use rune::{El, SurfaceFit, Tx, Waker, col, el, row, text};
#[cfg(windows)]
use rune::{ExternalHostEvent, NativeSurface};
use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WebProfile {
    MediaDrm,
    AuthSignIn,
}

impl WebProfile {
    pub fn browser_args(self) -> &'static str {
        match self {
            Self::MediaDrm => {
                "--renderer-process-limit=1 --disable-extensions --disable-background-networking --disable-sync --disable-component-extensions-with-background-pages --no-first-run"
            }
            Self::AuthSignIn => {
                "--renderer-process-limit=1 --disable-gpu-compositing --disable-extensions --disable-background-networking --disable-sync --disable-component-extensions-with-background-pages --no-first-run"
            }
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum WebSurfaceKind {
    SpotifyPlayer,
    #[allow(dead_code)]
    AppleMusicPlayer,
    AppleMusicSignIn,
    GogSignIn,
    StorePage {
        title: String,
        url: String,
    },
}

impl WebSurfaceKind {
    pub fn title(&self) -> &str {
        match self {
            Self::SpotifyPlayer => "Spotify",
            Self::AppleMusicPlayer => "Apple Music",
            Self::AppleMusicSignIn => "Sign in to Apple Music",
            Self::GogSignIn => "Sign in to GOG",
            Self::StorePage { title, .. } => title.as_str(),
        }
    }

    pub fn url(&self) -> &str {
        match self {
            Self::SpotifyPlayer => "https://open.spotify.com/",
            Self::AppleMusicPlayer | Self::AppleMusicSignIn => "https://music.apple.com/",
            Self::GogSignIn => "https://www.gog.com/account",
            Self::StorePage { url, .. } => url.as_str(),
        }
    }

    pub fn profile(&self) -> WebProfile {
        match self {
            Self::GogSignIn => WebProfile::AuthSignIn,
            Self::SpotifyPlayer
            | Self::AppleMusicPlayer
            | Self::AppleMusicSignIn
            | Self::StorePage { .. } => WebProfile::MediaDrm,
        }
    }

    pub fn is_ephemeral_on_room_leave(&self) -> bool {
        true
    }
}

#[derive(Clone, Debug, Default)]
pub struct ProcessTreeMetrics {
    pub root_pid: u32,
    pub total_processes: usize,
    pub child_processes: usize,
    pub webview2_processes: usize,
    pub total_working_set_mb: f64,
    pub total_private_mb: f64,
    pub root_working_set_mb: f64,
    pub root_private_mb: f64,
}

#[cfg(windows)]
pub fn snapshot_process_tree() -> ProcessTreeMetrics {
    use windows::Win32::Foundation::CloseHandle;
    use windows::Win32::System::Diagnostics::ToolHelp::*;
    use windows::Win32::System::ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX};
    use windows::Win32::System::Threading::{
        GetCurrentProcessId, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_VM_READ,
    };

    let root_pid = unsafe { GetCurrentProcessId() };
    let mut entries: Vec<(u32, u32, String)> = Vec::new();
    unsafe {
        if let Ok(snap) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) {
            let mut pe = PROCESSENTRY32W {
                dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
                ..Default::default()
            };
            if Process32FirstW(snap, &mut pe).is_ok() {
                loop {
                    let end = pe
                        .szExeFile
                        .iter()
                        .position(|&c| c == 0)
                        .unwrap_or(pe.szExeFile.len());
                    let name = String::from_utf16_lossy(&pe.szExeFile[..end]);
                    entries.push((pe.th32ProcessID, pe.th32ParentProcessID, name));
                    if Process32NextW(snap, &mut pe).is_err() {
                        break;
                    }
                }
            }
            let _ = CloseHandle(snap);
        }
    }

    let mut tree_pids = vec![root_pid];
    let mut changed = true;
    while changed {
        changed = false;
        for &(pid, ppid, _) in &entries {
            if tree_pids.contains(&ppid) && !tree_pids.contains(&pid) {
                tree_pids.push(pid);
                changed = true;
            }
        }
    }

    let mut total_ws = 0.0f64;
    let mut total_priv = 0.0f64;
    let mut root_ws = 0.0f64;
    let mut root_priv = 0.0f64;
    let mut wv2_count = 0usize;

    for &(pid, _, ref name) in &entries {
        if !tree_pids.contains(&pid) {
            continue;
        }
        if name.eq_ignore_ascii_case("msedgewebview2.exe") {
            wv2_count += 1;
        }
        unsafe {
            if let Ok(h) = OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ,
                false,
                pid,
            ) {
                let mut pmc = PROCESS_MEMORY_COUNTERS_EX {
                    cb: std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
                    ..Default::default()
                };
                if GetProcessMemoryInfo(
                    h,
                    &mut pmc as *mut _ as *mut _,
                    std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
                )
                .is_ok()
                {
                    let ws = pmc.WorkingSetSize as f64 / (1024.0 * 1024.0);
                    let priv_b = pmc.PrivateUsage as f64 / (1024.0 * 1024.0);
                    total_ws += ws;
                    total_priv += priv_b;
                    if pid == root_pid {
                        root_ws = ws;
                        root_priv = priv_b;
                    }
                }
                let _ = CloseHandle(h);
            }
        }
    }

    let round2 = |v: f64| (v * 100.0).round() / 100.0;
    ProcessTreeMetrics {
        root_pid,
        total_processes: tree_pids.len(),
        child_processes: tree_pids.len().saturating_sub(1),
        webview2_processes: wv2_count,
        total_working_set_mb: round2(total_ws),
        total_private_mb: round2(total_priv),
        root_working_set_mb: round2(root_ws),
        root_private_mb: round2(root_priv),
    }
}

#[cfg(not(windows))]
pub fn snapshot_process_tree() -> ProcessTreeMetrics {
    ProcessTreeMetrics {
        root_pid: std::process::id(),
        total_processes: 1,
        ..Default::default()
    }
}

#[derive(Clone, Debug)]
enum WebEvent {
    Ready,
    NavCompleted(bool),
    Suspended(bool),
    Error(String),
}

#[derive(Clone, Debug)]
struct BenchState {
    out_path: String,
    phase: u8,
    phase_started: Instant,
    idle: Option<ProcessTreeMetrics>,
    spotify_open: Option<ProcessTreeMetrics>,
    suspended: Option<ProcessTreeMetrics>,
    cold_start_ms: Option<f64>,
    reclaim_start: Option<Instant>,
}

pub struct LazyWebHostState {
    pub active: Option<WebSurfaceKind>,
    pub loading: bool,
    pub suspended: bool,
    pub error: Option<String>,
    pub surface: NativeSurface,
    waker: Option<Waker>,
    events: Arc<Mutex<VecDeque<WebEvent>>>,
    bench: Option<Box<BenchState>>,
    #[cfg(windows)]
    runtime: Arc<Mutex<Option<win_wv2::RuntimeHost>>>,
    #[cfg(windows)]
    last_slot_rect: Arc<Mutex<[i32; 4]>>,
}

impl Default for LazyWebHostState {
    fn default() -> Self {
        let surface = NativeSurface::new();
        Self {
            active: None,
            loading: false,
            suspended: false,
            error: None,
            surface,
            waker: None,
            events: Arc::new(Mutex::new(VecDeque::new())),
            bench: None,
            #[cfg(windows)]
            runtime: Arc::new(Mutex::new(None)),
            #[cfg(windows)]
            last_slot_rect: Arc::new(Mutex::new([0; 4])),
        }
    }
}

impl LazyWebHostState {
    pub fn is_open(&self) -> bool {
        self.active.is_some()
    }

    #[allow(dead_code)]
    pub fn has_active_runtime(&self) -> bool {
        #[cfg(windows)]
        {
            self.runtime.lock().unwrap().is_some()
        }
        #[cfg(not(windows))]
        {
            false
        }
    }

    pub fn set_waker(&mut self, waker: Waker) {
        self.surface.set_waker(waker.clone());
        self.waker = Some(waker);
    }

    pub fn start_lifecycle_bench(&mut self, out_path: String) {
        self.bench = Some(Box::new(BenchState {
            out_path,
            phase: 0,
            phase_started: Instant::now(),
            idle: None,
            spotify_open: None,
            suspended: None,
            cold_start_ms: None,
            reclaim_start: None,
        }));
    }

    pub fn open(&mut self, kind: WebSurfaceKind) {
        let next_profile = kind.profile();
        let next_url = kind.url().to_string();
        let profile_changed = self
            .active
            .as_ref()
            .is_some_and(|prev| prev.profile() != next_profile);
        if profile_changed {
            self.close();
        }

        self.active = Some(kind);
        self.loading = true;
        self.suspended = false;
        self.error = None;

        #[cfg(windows)]
        {
            self.surface.seed_opaque_mask(16, 9);
            if let Some(rt) = self.runtime.lock().unwrap().as_mut() {
                rt.resume_and_navigate(&next_url);
                return;
            }
            let rt_slot = Arc::clone(&self.runtime);
            let rect_slot = Arc::clone(&self.last_slot_rect);
            let ev_slot = Arc::clone(&self.events);
            let waker_cb = self.waker.clone();
            let initial_url = next_url;
            self.surface
                .set_external_host(Some(Arc::new(move |ev: ExternalHostEvent| {
                    *rect_slot.lock().unwrap() = ev.rect;
                    let mut guard = rt_slot.lock().unwrap();
                    if let Some(rt) = guard.as_mut() {
                        rt.sync_bounds(ev.dcomp_device, ev.slot_visual, ev.rect);
                        return;
                    }
                    let rt = win_wv2::RuntimeHost::start(
                        ev.hwnd,
                        ev.dcomp_device,
                        ev.slot_visual,
                        ev.rect,
                        next_profile,
                        initial_url.clone(),
                        Arc::clone(&ev_slot),
                        waker_cb.clone(),
                    );
                    *guard = Some(rt);
                })));
        }
    }

    pub fn reload(&mut self) {
        if let Some(ref active) = self.active {
            self.loading = true;
            self.error = None;
            #[cfg(windows)]
            if let Some(rt) = self.runtime.lock().unwrap().as_mut() {
                rt.navigate(active.url());
            }
            #[cfg(not(windows))]
            let _ = active;
        }
    }

    pub fn suspend(&mut self) {
        if !self.is_open() || self.suspended {
            return;
        }
        self.suspended = true;
        #[cfg(windows)]
        if let Some(rt) = self.runtime.lock().unwrap().as_mut() {
            rt.suspend(Arc::clone(&self.events), self.waker.clone());
        }
    }

    pub fn resume(&mut self) {
        if !self.is_open() || !self.suspended {
            return;
        }
        self.suspended = false;
        #[cfg(windows)]
        if let Some(rt) = self.runtime.lock().unwrap().as_mut() {
            rt.resume();
        }
    }

    pub fn close(&mut self) {
        self.active = None;
        self.loading = false;
        self.suspended = false;
        self.error = None;
        #[cfg(windows)]
        {
            self.surface.set_external_host(None);
            self.surface.set_external_layer(None);
            if let Some(mut rt) = self.runtime.lock().unwrap().take() {
                rt.destroy();
            }
            *self.last_slot_rect.lock().unwrap() = [0; 4];
        }
    }

    pub fn poll(&mut self) -> bool {
        let mut changed = false;
        #[cfg(windows)]
        if let Some(rt) = self.runtime.lock().unwrap().as_mut() {
            rt.drain_pending();
        }
        while let Some(ev) = self.events.lock().unwrap().pop_front() {
            changed = true;
            match ev {
                WebEvent::Ready => {}
                WebEvent::NavCompleted(ok) => {
                    self.loading = false;
                    if !ok {
                        self.error = Some("Page could not finish loading.".into());
                    }
                }
                WebEvent::Suspended(ok) => {
                    self.suspended = ok;
                }
                WebEvent::Error(msg) => {
                    self.loading = false;
                    self.error = Some(msg);
                }
            }
        }
        changed
    }

    #[cfg(windows)]
    pub fn forward_pointer_move(&self, x: f32, y: f32, scale: f32) {
        if let Some(rt) = self.runtime.lock().unwrap().as_ref() {
            rt.send_mouse_move((x * scale).round() as i32, (y * scale).round() as i32);
        }
    }

    #[cfg(windows)]
    pub fn forward_pointer_drag(&self, x: f32, y: f32, scale: f32, phase: rune::Phase) {
        if let Some(rt) = self.runtime.lock().unwrap().as_ref() {
            let px = (x * scale).round() as i32;
            let py = (y * scale).round() as i32;
            match phase {
                rune::Phase::Down => {
                    rt.focus();
                    rt.send_mouse_button(px, py, false, true);
                }
                rune::Phase::Move => {
                    rt.send_mouse_move(px, py);
                }
                rune::Phase::Up => {
                    rt.send_mouse_button(px, py, false, false);
                }
            }
        }
    }

    #[cfg(windows)]
    pub fn forward_pointer_press(&self, x: f32, y: f32, scale: f32, secondary: bool) {
        if let Some(rt) = self.runtime.lock().unwrap().as_ref() {
            let px = (x * scale).round() as i32;
            let py = (y * scale).round() as i32;
            rt.focus();
            rt.send_mouse_button(px, py, secondary, true);
            rt.send_mouse_button(px, py, secondary, false);
        }
    }

    #[cfg(windows)]
    pub fn forward_wheel(&self, dx: f32, dy: f32) {
        if let Some(rt) = self.runtime.lock().unwrap().as_ref() {
            rt.send_wheel(dx, dy);
        }
    }
}

impl Drop for LazyWebHostState {
    fn drop(&mut self) {
        self.close();
    }
}

impl AppState {
    pub fn open_web_surface(&mut self, kind: WebSurfaceKind) {
        self.web_host.open(kind);
    }

    pub fn close_web_surface(&mut self) {
        self.web_host.close();
    }

    pub fn set_web_host_window_visible(&mut self, visible: bool) {
        if visible {
            self.web_host.resume();
        } else {
            self.web_host.suspend();
        }
    }

    pub fn poll_web_host(&mut self) -> bool {
        self.web_host.poll()
    }

    pub fn tick_web_lifecycle_bench(&mut self, _now: f64, tx: &mut Tx) -> bool {
        let Some(phase) = self.web_host.bench.as_ref().map(|b| b.phase) else {
            return false;
        };
        tx.wake_after(Duration::from_millis(100));
        let elapsed = self
            .web_host
            .bench
            .as_ref()
            .map(|b| b.phase_started.elapsed().as_secs_f64())
            .unwrap_or(0.0);
        match phase {
            0 if elapsed >= 2.0 => {
                let idle = snapshot_process_tree();
                if let Some(b) = self.web_host.bench.as_mut() {
                    b.idle = Some(idle);
                    b.phase = 1;
                    b.phase_started = Instant::now();
                }
                self.open_web_surface(WebSurfaceKind::SpotifyPlayer);
                true
            }
            1 => {
                let nav_done = !self.web_host.loading;
                if nav_done
                    && self
                        .web_host
                        .bench
                        .as_ref()
                        .is_some_and(|b| b.cold_start_ms.is_none())
                    && let Some(b) = self.web_host.bench.as_mut()
                {
                    let ms =
                        (b.phase_started.elapsed().as_secs_f64() * 1000.0 * 100.0).round() / 100.0;
                    b.cold_start_ms = Some(ms);
                }
                let settle_ready = (nav_done && elapsed >= 5.0) || elapsed >= 10.0;
                if settle_ready {
                    let active = snapshot_process_tree();
                    if let Some(b) = self.web_host.bench.as_mut() {
                        b.spotify_open = Some(active);
                        b.phase = 2;
                        b.phase_started = Instant::now();
                    }
                    self.set_web_host_window_visible(false);
                    return true;
                }
                false
            }
            2 if elapsed >= 2.0 => {
                let susp = snapshot_process_tree();
                if let Some(b) = self.web_host.bench.as_mut() {
                    b.suspended = Some(susp);
                    b.phase = 3;
                    b.phase_started = Instant::now();
                    b.reclaim_start = Some(Instant::now());
                }
                self.close_web_surface();
                true
            }
            3 => {
                let after = snapshot_process_tree();
                if after.webview2_processes == 0 || elapsed >= 8.0 {
                    if let Some(b) = self.web_host.bench.take() {
                        let reclaim_ms = b
                            .reclaim_start
                            .map(|t| (t.elapsed().as_secs_f64() * 1000.0 * 100.0).round() / 100.0);
                        let metrics_json = |m: &ProcessTreeMetrics| {
                            serde_json::json!({
                                "root_pid": m.root_pid,
                                "total_processes": m.total_processes,
                                "child_processes": m.child_processes,
                                "webview2_processes": m.webview2_processes,
                                "total_working_set_mb": m.total_working_set_mb,
                                "total_private_mb": m.total_private_mb,
                                "root_working_set_mb": m.root_working_set_mb,
                                "root_private_mb": m.root_private_mb,
                            })
                        };
                        let report = serde_json::json!({
                            "idle": b.idle.as_ref().map(metrics_json),
                            "spotify_open": b.spotify_open.as_ref().map(metrics_json),
                            "suspended": b.suspended.as_ref().map(metrics_json),
                            "after_close": metrics_json(&after),
                            "spotify_cold_start_ms": b.cold_start_ms,
                            "reclaim_ms": reclaim_ms,
                        });
                        if let Some(parent) = std::path::Path::new(&b.out_path).parent() {
                            let _ = std::fs::create_dir_all(parent);
                        }
                        let _ = std::fs::write(
                            &b.out_path,
                            serde_json::to_string_pretty(&report).unwrap_or_default(),
                        );
                    }
                    std::process::exit(0);
                }
                false
            }
            _ => false,
        }
    }
}

pub fn overlay_layers(s: &AppState) -> Vec<El> {
    let Some(ref active) = s.web_host.active else {
        return Vec::new();
    };
    let title = active.title().to_string();
    let url_display = active.url().to_string();
    let open_url = url_display.clone();
    let status = if let Some(ref err) = s.web_host.error {
        err.clone()
    } else if s.web_host.loading {
        "Loading…".into()
    } else if s.web_host.suspended {
        "Suspended".into()
    } else {
        url_display
    };

    let scrim = el()
        .key("web-host-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(scrim_fill())
        .transition(modal_scrim_transition())
        .on_press(|s: &mut AppState| s.close_web_surface());

    let header = row()
        .w_full()
        .h(52.0)
        .px(16.0)
        .gap(12.0)
        .center_y()
        .child(
            col()
                .grow(1.0)
                .min_w(0.0)
                .gap(2.0)
                .child(text(title).font(type_section()).color(FG))
                .child(text(status).font(type_small()).color(fg_muted())),
        )
        .child(
            glass_icon_circle_opt(
                "web-host-reload",
                "Reload",
                30.0,
                icon_refresh(14.0, FG),
                false,
            )
            .on_press(|s: &mut AppState| s.web_host.reload()),
        )
        .child(
            glass_button_opt(
                "web-host-external",
                "Open in browser",
                Some(icon_external(13.0, FG)),
                false,
            )
            .on_press(move |s: &mut AppState| s.store_open_external_url(open_url.clone())),
        )
        .child(
            glass_icon_circle_opt("web-host-close", "Close", 30.0, icon_close(14.0, FG), false)
                .on_press(|s: &mut AppState| s.close_web_surface()),
        );

    let mut viewport = el()
        .key("web-host-viewport")
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .fill(BG)
        .native_surface(&s.web_host.surface, SurfaceFit::Fill);

    #[cfg(windows)]
    {
        viewport = viewport
            .on_pointer_move_local(|s: &mut AppState, ev, _| {
                s.web_host.forward_pointer_move(ev.x, ev.y, 1.0);
            })
            .on_drag(|s: &mut AppState, d, _| {
                s.web_host
                    .forward_pointer_drag(d.start_x + d.dx, d.start_y + d.dy, 1.0, d.phase);
            })
            .on_secondary_press_local(|s: &mut AppState, ev, _| {
                s.web_host.forward_pointer_press(ev.x, ev.y, 1.0, true);
            })
            .on_wheel(|s: &mut AppState, ev, _| {
                s.web_host.forward_wheel(ev.dx, ev.dy);
                true
            });
    }

    let card = apply_glass(
        col()
            .key("web-host-card")
            .pin(Some(48.0), Some(68.0), Some(48.0), Some(32.0))
            .radius(RADIUS_CARD)
            .continuous_corners()
            .clip_rounded(RADIUS_CARD)
            .transition(modal_card_transition()),
        ExoGlass::Sheet,
    )
    .child(header)
    .child(el().w_full().h(1.0).fill(float_line()))
    .child(viewport);

    if let Some(ref err) = s.web_host.error {
        let banner = row()
            .w_full()
            .px(16.0)
            .py(8.0)
            .child(text(err.clone()).font(type_body()).color(fg_faint()));
        vec![
            scrim,
            card.child(el().w_full().h(1.0).fill(float_line()))
                .child(banner),
        ]
    } else {
        vec![scrim, card]
    }
}

#[cfg(windows)]
mod win_wv2 {
    use super::*;
    use std::path::PathBuf;
    use webview2_com::Microsoft::Web::WebView2::Win32::*;
    use webview2_com::{
        CoreWebView2EnvironmentOptions, CreateCoreWebView2CompositionControllerCompletedHandler,
        CreateCoreWebView2EnvironmentCompletedHandler, NavigationCompletedEventHandler,
        TrySuspendCompletedHandler,
    };
    use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND, POINT, RECT};
    use windows::Win32::Graphics::DirectComposition::IDCompositionDevice;
    use windows::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation,
        SetInformationJobObject,
    };
    use windows::Win32::System::Threading::{
        OpenProcess, PROCESS_SET_QUOTA, PROCESS_SYNCHRONIZE, PROCESS_TERMINATE, WaitForSingleObject,
    };
    use windows::core::{HSTRING, IUnknown, Interface, PCWSTR};

    struct OwnedHandle(HANDLE);
    unsafe impl Send for OwnedHandle {}
    impl Drop for OwnedHandle {
        fn drop(&mut self) {
            if !self.0.is_invalid() {
                unsafe {
                    let _ = CloseHandle(self.0);
                }
            }
        }
    }

    unsafe fn addref_unknown(raw: usize) -> Option<IUnknown> {
        let ptr = raw as *mut core::ffi::c_void;
        if ptr.is_null() {
            return None;
        }
        unsafe { IUnknown::from_raw_borrowed(&ptr).cloned() }
    }

    unsafe fn assign_webview_job(pid: u32) -> (Option<OwnedHandle>, Option<OwnedHandle>) {
        if pid == 0 {
            return (None, None);
        }
        let Ok(proc) = (unsafe {
            OpenProcess(
                PROCESS_SET_QUOTA | PROCESS_TERMINATE | PROCESS_SYNCHRONIZE,
                false,
                pid,
            )
        }) else {
            return (None, None);
        };
        let proc = OwnedHandle(proc);
        let Ok(job) = (unsafe { CreateJobObjectW(None, None) }) else {
            return (None, Some(proc));
        };
        let job = OwnedHandle(job);
        let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        let ok_info = unsafe {
            SetInformationJobObject(
                job.0,
                JobObjectExtendedLimitInformation,
                (&info as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION).cast(),
                std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            )
            .is_ok()
        };
        if !ok_info {
            return (None, Some(proc));
        }
        let assigned = unsafe { AssignProcessToJobObject(job.0, proc.0).is_ok() };
        if assigned {
            (Some(job), Some(proc))
        } else {
            (None, Some(proc))
        }
    }

    enum PendingObj {
        Env(ICoreWebView2Environment),
        Comp(ICoreWebView2CompositionController),
    }

    struct SendPending(VecDeque<PendingObj>);
    unsafe impl Send for SendPending {}
    unsafe impl Sync for SendPending {}

    pub(super) struct RuntimeHost {
        hwnd: HWND,
        dcomp_device: Option<IDCompositionDevice>,
        slot_visual: Option<IUnknown>,
        rect: [i32; 4],
        pending_url: String,
        env: Option<ICoreWebView2Environment>,
        comp: Option<ICoreWebView2CompositionController>,
        ctrl: Option<ICoreWebView2Controller>,
        webview: Option<ICoreWebView2>,
        nav_token: Option<i64>,
        browser_job: Option<OwnedHandle>,
        browser_proc: Option<OwnedHandle>,
        pending: Arc<Mutex<SendPending>>,
        events: Arc<Mutex<VecDeque<WebEvent>>>,
        waker: Option<Waker>,
    }

    unsafe impl Send for RuntimeHost {}
    unsafe impl Sync for RuntimeHost {}

    fn user_data_dir(profile: WebProfile) -> PathBuf {
        let leaf = match profile {
            WebProfile::MediaDrm => "web-media",
            WebProfile::AuthSignIn => "web-auth",
        };
        let dir = crate::app_data_dir().join("native-web-host").join(leaf);
        let _ = std::fs::create_dir_all(&dir);
        dir
    }

    impl RuntimeHost {
        #[allow(clippy::too_many_arguments)]
        pub(super) fn start(
            hwnd_raw: isize,
            dcomp_device_raw: usize,
            slot_visual_raw: usize,
            rect: [i32; 4],
            profile: WebProfile,
            url: String,
            events: Arc<Mutex<VecDeque<WebEvent>>>,
            waker: Option<Waker>,
        ) -> Self {
            let dcomp_device = unsafe { addref_unknown(dcomp_device_raw) }
                .and_then(|u| u.cast::<IDCompositionDevice>().ok());
            let slot_visual = unsafe { addref_unknown(slot_visual_raw) };
            let pending = Arc::new(Mutex::new(SendPending(VecDeque::new())));
            let host = Self {
                hwnd: HWND(hwnd_raw as *mut _),
                dcomp_device,
                slot_visual,
                rect,
                pending_url: url,
                env: None,
                comp: None,
                ctrl: None,
                webview: None,
                nav_token: None,
                browser_job: None,
                browser_proc: None,
                pending: Arc::clone(&pending),
                events: Arc::clone(&events),
                waker: waker.clone(),
            };

            let opts = CoreWebView2EnvironmentOptions::default();
            unsafe {
                opts.set_additional_browser_arguments(profile.browser_args().to_string());
            }
            let opts_iface: ICoreWebView2EnvironmentOptions = opts.into();
            let udf_path = user_data_dir(profile);
            let udf = HSTRING::from(udf_path.to_string_lossy().as_ref());

            let pending_cb = Arc::clone(&pending);
            let events_cb = Arc::clone(&events);
            let waker_cb = waker;
            let env_handler = CreateCoreWebView2EnvironmentCompletedHandler::create(Box::new(
                move |hr, env_opt| {
                    match (hr, env_opt) {
                        (Ok(()), Some(env)) => {
                            pending_cb.lock().unwrap().0.push_back(PendingObj::Env(env));
                        }
                        (Err(e), _) => {
                            events_cb
                                .lock()
                                .unwrap()
                                .push_back(WebEvent::Error(format!("WebView2 env: {e}")));
                        }
                        (Ok(()), None) => {
                            events_cb
                                .lock()
                                .unwrap()
                                .push_back(WebEvent::Error("WebView2 env was empty".into()));
                        }
                    }
                    if let Some(ref w) = waker_cb {
                        w();
                    }
                    Ok(())
                },
            ));

            unsafe {
                if let Err(e) = CreateCoreWebView2EnvironmentWithOptions(
                    PCWSTR::null(),
                    PCWSTR(udf.as_ptr()),
                    &opts_iface,
                    &env_handler,
                ) {
                    events
                        .lock()
                        .unwrap()
                        .push_back(WebEvent::Error(format!("WebView2 init: {e}")));
                }
            }
            host
        }

        pub(super) fn drain_pending(&mut self) {
            loop {
                let item = self.pending.lock().unwrap().0.pop_front();
                let Some(obj) = item else { break };
                match obj {
                    PendingObj::Env(env) => self.on_env_ready(env),
                    PendingObj::Comp(comp) => self.on_comp_ready(comp),
                }
            }
        }

        fn on_env_ready(&mut self, env: ICoreWebView2Environment) {
            let Ok(env3) = env.cast::<ICoreWebView2Environment3>() else {
                self.events
                    .lock()
                    .unwrap()
                    .push_back(WebEvent::Error("WebView2 Environment3 unavailable".into()));
                return;
            };
            self.env = Some(env);
            let pending_cb = Arc::clone(&self.pending);
            let events_cb = Arc::clone(&self.events);
            let waker_cb = self.waker.clone();
            let comp_handler = CreateCoreWebView2CompositionControllerCompletedHandler::create(
                Box::new(move |hr, comp_opt| {
                    match (hr, comp_opt) {
                        (Ok(()), Some(comp)) => {
                            pending_cb
                                .lock()
                                .unwrap()
                                .0
                                .push_back(PendingObj::Comp(comp));
                        }
                        (Err(e), _) => {
                            events_cb
                                .lock()
                                .unwrap()
                                .push_back(WebEvent::Error(format!("WebView2 ctrl: {e}")));
                        }
                        (Ok(()), None) => {
                            events_cb
                                .lock()
                                .unwrap()
                                .push_back(WebEvent::Error("WebView2 ctrl was empty".into()));
                        }
                    }
                    if let Some(ref w) = waker_cb {
                        w();
                    }
                    Ok(())
                }),
            );
            unsafe {
                if let Err(e) =
                    env3.CreateCoreWebView2CompositionController(self.hwnd, &comp_handler)
                {
                    self.events
                        .lock()
                        .unwrap()
                        .push_back(WebEvent::Error(format!("CreateCompositionController: {e}")));
                }
            }
        }

        fn on_comp_ready(&mut self, comp: ICoreWebView2CompositionController) {
            let Ok(ctrl) = comp.cast::<ICoreWebView2Controller>() else {
                return;
            };
            let Ok(wv) = (unsafe { ctrl.CoreWebView2() }) else {
                return;
            };
            unsafe {
                if let Some(ref v) = self.slot_visual {
                    let _ = comp.SetRootVisualTarget(v);
                }
                if let Ok(ctrl2) = comp.cast::<ICoreWebView2Controller2>() {
                    let _ = ctrl2.SetDefaultBackgroundColor(COREWEBVIEW2_COLOR {
                        A: 255,
                        R: 12,
                        G: 13,
                        B: 16,
                    });
                }
                if let Ok(wv19) = wv.cast::<ICoreWebView2_19>() {
                    let _ =
                        wv19.SetMemoryUsageTargetLevel(COREWEBVIEW2_MEMORY_USAGE_TARGET_LEVEL_LOW);
                }
                let _ = ctrl.SetBounds(RECT {
                    left: 0,
                    top: 0,
                    right: self.rect[2].max(320),
                    bottom: self.rect[3].max(180),
                });
                let _ = ctrl.SetIsVisible(true);
            }
            let events_cb = Arc::clone(&self.events);
            let waker_cb = self.waker.clone();
            let nav_handler = NavigationCompletedEventHandler::create(Box::new(move |_, args| {
                let ok = args
                    .map(|a| {
                        let mut success = windows_core::BOOL(0);
                        unsafe {
                            let _ = a.IsSuccess(&mut success);
                        }
                        success.as_bool()
                    })
                    .unwrap_or(false);
                events_cb
                    .lock()
                    .unwrap()
                    .push_back(WebEvent::NavCompleted(ok));
                if let Some(ref w) = waker_cb {
                    w();
                }
                Ok(())
            }));
            let mut token = 0i64;
            let mut browser_pid = 0u32;
            unsafe {
                let _ = wv.add_NavigationCompleted(&nav_handler, &mut token);
                let _ = wv.BrowserProcessId(&mut browser_pid);
                let url_h = HSTRING::from(self.pending_url.as_str());
                let _ = wv.Navigate(PCWSTR(url_h.as_ptr()));
            }
            let (browser_job, browser_proc) = unsafe { assign_webview_job(browser_pid) };
            self.commit_dcomp();
            self.nav_token = Some(token);
            self.browser_job = browser_job;
            self.browser_proc = browser_proc;
            self.comp = Some(comp);
            self.ctrl = Some(ctrl);
            self.webview = Some(wv);
            self.events.lock().unwrap().push_back(WebEvent::Ready);
            if let Some(ref w) = self.waker {
                w();
            }
        }

        fn commit_dcomp(&self) {
            if let Some(ref dcomp) = self.dcomp_device {
                unsafe {
                    let _ = dcomp.Commit();
                }
            }
        }

        pub(super) fn sync_bounds(
            &mut self,
            dcomp_device_raw: usize,
            slot_visual_raw: usize,
            rect: [i32; 4],
        ) {
            if let Some(dev) = unsafe { addref_unknown(dcomp_device_raw) }
                .and_then(|u| u.cast::<IDCompositionDevice>().ok())
            {
                self.dcomp_device = Some(dev);
            }
            let next_visual = unsafe { addref_unknown(slot_visual_raw) };
            let visual_changed = next_visual.as_ref().map(|v| v.as_raw())
                != self.slot_visual.as_ref().map(|v| v.as_raw());
            if next_visual.is_some() {
                self.slot_visual = next_visual;
            }
            self.rect = rect;
            if visual_changed
                && let (Some(comp), Some(v)) = (self.comp.as_ref(), self.slot_visual.as_ref())
            {
                unsafe {
                    let _ = comp.SetRootVisualTarget(v);
                }
            }
            if let Some(ref ctrl) = self.ctrl {
                unsafe {
                    let _ = ctrl.SetBounds(RECT {
                        left: 0,
                        top: 0,
                        right: rect[2].max(16),
                        bottom: rect[3].max(16),
                    });
                }
                self.commit_dcomp();
            }
        }

        pub(super) fn navigate(&mut self, url: &str) {
            self.pending_url = url.to_string();
            if let Some(ref wv) = self.webview {
                let h = HSTRING::from(url);
                unsafe {
                    let _ = wv.Navigate(PCWSTR(h.as_ptr()));
                }
            }
        }

        pub(super) fn resume_and_navigate(&mut self, url: &str) {
            self.resume();
            self.navigate(url);
        }

        pub(super) fn suspend(
            &mut self,
            events: Arc<Mutex<VecDeque<WebEvent>>>,
            waker: Option<Waker>,
        ) {
            if let Some(ref ctrl) = self.ctrl {
                unsafe {
                    let _ = ctrl.SetIsVisible(false);
                }
            }
            if let Some(ref wv) = self.webview
                && let Ok(wv3) = wv.cast::<ICoreWebView2_3>()
            {
                let handler = TrySuspendCompletedHandler::create(Box::new(move |hr, ok| {
                    events
                        .lock()
                        .unwrap()
                        .push_back(WebEvent::Suspended(hr.is_ok() && ok));
                    if let Some(ref w) = waker {
                        w();
                    }
                    Ok(())
                }));
                unsafe {
                    let _ = wv3.TrySuspend(&handler);
                }
            }
        }

        pub(super) fn resume(&mut self) {
            if let Some(ref wv) = self.webview
                && let Ok(wv3) = wv.cast::<ICoreWebView2_3>()
            {
                unsafe {
                    let _ = wv3.Resume();
                }
            }
            if let Some(ref ctrl) = self.ctrl {
                unsafe {
                    let _ = ctrl.SetIsVisible(true);
                }
            }
            self.commit_dcomp();
        }

        pub(super) fn destroy(&mut self) {
            let job = self.browser_job.take();
            let proc = self.browser_proc.take();
            if let Some(ref wv) = self.webview {
                unsafe {
                    if let Some(tok) = self.nav_token.take() {
                        let _ = wv.remove_NavigationCompleted(tok);
                    }
                    let _ = wv.Stop();
                }
                if let Ok(wv3) = wv.cast::<ICoreWebView2_3>() {
                    unsafe {
                        let _ = wv3.Resume();
                    }
                }
            }
            if let Some(comp) = self.comp.take() {
                unsafe {
                    let _ = comp.SetRootVisualTarget(None);
                }
            }
            if let Some(ctrl) = self.ctrl.take() {
                unsafe {
                    let _ = ctrl.SetIsVisible(false);
                    let _ = ctrl.Close();
                }
            }
            self.webview = None;
            self.env = None;
            self.commit_dcomp();
            self.slot_visual = None;
            self.dcomp_device = None;
            if job.is_some() || proc.is_some() {
                let _ = std::thread::Builder::new()
                    .name("exo-wv2-reclaim".into())
                    .spawn(move || {
                        if let Some(ref p) = proc {
                            unsafe {
                                WaitForSingleObject(p.0, 3000);
                            }
                        }
                        drop(proc);
                        drop(job);
                    });
            }
        }

        pub(super) fn focus(&self) {
            if let Some(ref ctrl) = self.ctrl {
                unsafe {
                    let _ = ctrl.MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
                }
            }
        }

        pub(super) fn send_mouse_move(&self, x: i32, y: i32) {
            if let Some(ref comp) = self.comp {
                unsafe {
                    let _ = comp.SendMouseInput(
                        COREWEBVIEW2_MOUSE_EVENT_KIND_MOVE,
                        COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE,
                        0,
                        POINT { x, y },
                    );
                }
            }
        }

        pub(super) fn send_mouse_button(&self, x: i32, y: i32, secondary: bool, down: bool) {
            if let Some(ref comp) = self.comp {
                let (kind, key) = match (secondary, down) {
                    (true, true) => (
                        COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_DOWN,
                        COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_RIGHT_BUTTON,
                    ),
                    (true, false) => (
                        COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_UP,
                        COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE,
                    ),
                    (false, true) => (
                        COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_DOWN,
                        COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_LEFT_BUTTON,
                    ),
                    (false, false) => (
                        COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_UP,
                        COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE,
                    ),
                };
                unsafe {
                    let _ = comp.SendMouseInput(kind, key, 0, POINT { x, y });
                }
            }
        }

        pub(super) fn send_wheel(&self, dx: f32, dy: f32) {
            if let Some(ref comp) = self.comp {
                if dy.abs() > 0.01 {
                    let delta = (dy * 120.0).round() as i32 as u32;
                    unsafe {
                        let _ = comp.SendMouseInput(
                            COREWEBVIEW2_MOUSE_EVENT_KIND_WHEEL,
                            COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE,
                            delta,
                            POINT { x: 0, y: 0 },
                        );
                    }
                }
                if dx.abs() > 0.01 {
                    let delta = (dx * 120.0).round() as i32 as u32;
                    unsafe {
                        let _ = comp.SendMouseInput(
                            COREWEBVIEW2_MOUSE_EVENT_KIND_HORIZONTAL_WHEEL,
                            COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE,
                            delta,
                            POINT { x: 0, y: 0 },
                        );
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lazy_web_host_starts_completely_idle_with_zero_runtime() {
        let host = LazyWebHostState::default();
        assert!(!host.is_open());
        assert!(!host.has_active_runtime());
        let tree = snapshot_process_tree();
        assert_eq!(tree.webview2_processes, 0);
    }

    #[test]
    fn web_surface_profiles_match_drm_vs_auth_policy() {
        assert_eq!(
            WebSurfaceKind::SpotifyPlayer.profile(),
            WebProfile::MediaDrm
        );
        assert_eq!(
            WebSurfaceKind::AppleMusicSignIn.profile(),
            WebProfile::MediaDrm
        );
        assert_eq!(WebSurfaceKind::GogSignIn.profile(), WebProfile::AuthSignIn);
        assert!(
            WebProfile::AuthSignIn
                .browser_args()
                .contains("--disable-gpu-compositing")
        );
        assert!(
            !WebProfile::MediaDrm
                .browser_args()
                .contains("--disable-gpu-compositing")
        );
    }

    #[test]
    fn open_and_close_tracks_active_surface_and_reclaims_state() {
        let mut host = LazyWebHostState::default();
        host.open(WebSurfaceKind::SpotifyPlayer);
        assert!(host.is_open());
        assert_eq!(host.active, Some(WebSurfaceKind::SpotifyPlayer));
        host.close();
        assert!(!host.is_open());
        assert!(!host.has_active_runtime());
    }
}
