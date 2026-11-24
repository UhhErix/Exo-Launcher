use exo_rpc::{
    Afmf3State, AppFind, AppsListReply, AudioEndpoint, AudioReadReply, AudioVoiceInfo,
    AutoTuneState, AutopilotLive, AutopilotMemoryIntegrity, AutopilotReport, AutopilotState,
    BackupStatus, BiosStaged, ClientsStatus, CompressList, CompressState, DevicesListReply,
    DisplayState, DriveFix, DriveGameEntry, DriveHealth, DriveInfo, DriveLink, DriveSpaceBreakdown,
    DriveTemp, DriveVerdict, DriveVolume, DriverProgress, DriverState, DriverStep, DriversState,
    DrivesReply, EqHeadphone, ExoAccountState, ExoAiFans, ExoAiGaming, ExoAiState, FpsCounterReply,
    GpuProfile, GpuProfileScreen, HdrState, InstalledAppItem, InstallerCacheReply, InterruptsState,
    JunkItem, KeepStatus, KeyboardBackupSummary, KeyboardProfileSlot, KeyboardReadReply,
    LatencyReport, LatencySample, LauncherSettings, LeftoverSummary, PcHealth, PcHealthItem,
    PeripheralCard, RuntimeItem, RuntimeResult, RuntimesProgress, ShaderPrepareGame, ShaderRebuild,
    ShaderSnapGame, ShadersAddonStatus, StartupItem, StartupListReply, SystemAbout,
    SystemTuningState, TunePart, TuningItem, UpdateCheckReply, UpdateProgress, WebClientOptions,
    WindowsApp,
};
use rune::TextEdit;
use std::collections::{HashMap, HashSet};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SettingsSection {
    General,
    Account,
    Appearance,
    Graphics,
    Updates,
    About,
}

impl SettingsSection {
    pub const ALL: [Self; 6] = [
        Self::General,
        Self::Account,
        Self::Appearance,
        Self::Graphics,
        Self::Updates,
        Self::About,
    ];

    pub fn key(self) -> &'static str {
        match self {
            Self::General => "set-sec-general",
            Self::Account => "set-sec-account",
            Self::Appearance => "set-sec-appearance",
            Self::Graphics => "set-sec-graphics",
            Self::Updates => "set-sec-updates",
            Self::About => "set-sec-about",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PcTab {
    ExoAi,
    Apps,
    Storage,
    Devices,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AppsSubTab {
    Installed,
    Startup,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PcPartMenu {
    Graphics,
    Display,
    Board,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AppsSortBy {
    Name,
    Used,
    Size,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AutoTuneStageId {
    Cooling,
    Graphics,
    System,
    Bloat,
    Cleanup,
    Drivers,
}

impl AutoTuneStageId {
    pub const ALL: [Self; 6] = [
        Self::Cooling,
        Self::Graphics,
        Self::System,
        Self::Bloat,
        Self::Cleanup,
        Self::Drivers,
    ];

    pub fn key(self) -> &'static str {
        match self {
            Self::Cooling => "cooling",
            Self::Graphics => "graphics",
            Self::System => "system",
            Self::Bloat => "bloat",
            Self::Cleanup => "cleanup",
            Self::Drivers => "drivers",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Cooling => "Power & Autopilot",
            Self::Graphics => "Graphics & display",
            Self::System => "System, interrupts & network",
            Self::Bloat => "Preinstalled apps",
            Self::Cleanup => "Temporary caches",
            Self::Drivers => "Drivers & updates",
        }
    }

    pub fn short_label(self) -> &'static str {
        match self {
            Self::Cooling => "Power",
            Self::Graphics => "Graphics",
            Self::System => "System",
            Self::Bloat => "Apps",
            Self::Cleanup => "Cleanup",
            Self::Drivers => "Drivers",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AutoTuneStageStatus {
    Idle,
    Active,
    Done,
    Warn,
}

#[derive(Debug, Clone)]
pub struct AutoTuneStageState {
    pub id: AutoTuneStageId,
    pub label: &'static str,
    pub status: AutoTuneStageStatus,
    pub detail: String,
}

#[derive(Debug, Clone)]
pub struct BloatAppInfo {
    pub id: String,
    #[allow(dead_code)]
    pub name: String,
}

#[derive(Debug, Clone)]
pub struct RemovedAppEntry {
    pub id: String,
    pub name: String,
    pub find: Option<AppFind>,
    pub message: Option<String>,
    pub cleared_line: Option<String>,
    pub undo_token: Option<String>,
    pub undone: bool,
    pub review_open: bool,
    pub picks: HashSet<i64>,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub struct AutoClearedEntry {
    pub id: String,
    pub line: String,
    pub undone: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Copy)]
pub struct BiosVendorHints {
    pub secure_boot: &'static str,
    pub tpm: &'static str,
}

pub fn bios_vendor_hints(motherboard: Option<&str>) -> Option<BiosVendorHints> {
    let lower = motherboard?.to_ascii_lowercase();
    if lower.contains("asus") || lower.contains("asustek") {
        Some(BiosVendorHints {
            secure_boot: "Boot → Secure Boot",
            tpm: "Advanced → PCH-FW Configuration / AMD fTPM",
        })
    } else if lower.contains("msi") || lower.contains("micro-star") {
        Some(BiosVendorHints {
            secure_boot: "Settings → Security → Secure Boot",
            tpm: "Settings → Security → Trusted Computing",
        })
    } else if lower.contains("gigabyte") {
        Some(BiosVendorHints {
            secure_boot: "Settings → Miscellaneous → Secure Boot",
            tpm: "Settings → Miscellaneous → Intel PTT / AMD CPU fTPM",
        })
    } else if lower.contains("asrock") {
        Some(BiosVendorHints {
            secure_boot: "Security → Secure Boot",
            tpm: "Advanced → CPU / Security → fTPM / PTT",
        })
    } else {
        None
    }
}

#[derive(Debug, Clone)]
pub struct BestStepInfo {
    pub key: &'static str,
    pub what: String,
    pub done: bool,
}

#[derive(Debug, Clone, Default)]
pub struct DriverInstallOverlayState {
    pub kind: String,
    pub phase: String,
    pub name: Option<String>,
    pub version: Option<String>,
    pub share: Option<f64>,
    pub size: Option<u64>,
    pub message: Option<String>,
    pub restored: Vec<String>,
    pub not_restored: Vec<String>,
}

pub struct SettingsAndPcState {
    pub settings_section: SettingsSection,
    pub settings_query: TextEdit,
    pub account: ExoAccountState,
    pub update_check: Option<UpdateCheckReply>,
    pub update_progress: Option<UpdateProgress>,
    pub checking_update: bool,
    pub installing_update: bool,
    pub system_about: Option<SystemAbout>,
    pub backup: Option<BackupStatus>,
    pub clients: Option<ClientsStatus>,

    pub pc_tab: PcTab,
    pub exo_ai: Option<ExoAiState>,
    pub autopilot: Option<AutopilotState>,
    pub tuning: Option<SystemTuningState>,
    pub hdr: Option<HdrState>,
    pub shaders: Option<ShadersAddonStatus>,
    pub latency_running: bool,

    pub auto_tune: Option<AutoTuneState>,
    pub tune_errors: HashMap<String, String>,
    pub bloat_apps: Vec<BloatAppInfo>,
    pub windows_packages: Vec<WindowsApp>,
    pub auto_tune_stages: Vec<AutoTuneStageState>,
    pub auto_tune_running: bool,
    pub auto_tune_finished: bool,
    pub auto_tune_completed_at: Option<u64>,
    pub auto_tune_cinema_error: Option<String>,
    pub auto_tune_restarting: bool,

    pub health: Option<PcHealth>,
    pub interrupts: Option<InterruptsState>,
    pub interrupts_devices_open: bool,
    pub interrupts_error: Option<String>,
    pub gpu_profile: Option<GpuProfile>,
    pub gpu_profile_error: Option<String>,
    pub gpu_low_latency_choice: Option<String>,
    pub gpu_driver: Option<DriverState>,
    pub gpu_driver_branch: String,
    pub gpu_driver_progress: Option<DriverProgress>,
    pub gpu_driver_starting: bool,
    pub gpu_driver_consent_open: bool,
    pub gpu_driver_error: Option<String>,
    pub driver_install_overlay: Option<DriverInstallOverlayState>,
    pub display_state: Option<DisplayState>,
    pub selected_display_id: Option<String>,
    pub display_busy: Option<String>,
    pub display_error: Option<(String, String)>,
    pub afmf3: Option<Afmf3State>,
    pub afmf3_busy: bool,
    pub afmf3_error: Option<String>,
    pub keep_status: Option<KeepStatus>,
    pub drivers_state: Option<DriversState>,
    pub driver_steps: HashMap<String, DriverStep>,
    pub drivers_updating_all: bool,
    pub drivers_error: Option<String>,
    pub bios_staged: Option<BiosStaged>,
    pub bios_progress: Option<f64>,
    pub bios_busy: Option<String>,
    pub bios_error: Option<String>,
    pub board_restart_error: Option<(String, String)>,
    pub armed_confirm: Option<String>,
    pub autopilot_expanded_report: Option<String>,
    pub autopilot_error: Option<String>,
    pub best_setup_running: Option<String>,
    pub best_setup_error: Option<String>,
    pub network_busy: Option<String>,
    pub network_error: Option<(String, String)>,
    pub tuning_busy: Option<String>,
    pub tuning_error: Option<(String, String)>,
    pub runtimes: Option<Vec<RuntimeItem>>,
    pub runtimes_running: Option<String>,
    pub runtimes_canceling: bool,
    pub runtimes_progress: Option<RuntimesProgress>,
    pub runtimes_results: HashMap<String, RuntimeResult>,
    pub runtimes_note: Option<(String, bool)>,
    pub controller_firmwares: Vec<(String, String, bool)>,
    pub shaders_rebuild: Option<ShaderRebuild>,
    pub shaders_queue_running: bool,
    pub shaders_queue_paused: bool,
    pub shaders_queue_can_start: bool,
    pub shaders_queue_waiting: Vec<String>,
    pub shaders_queue_current_id: Option<String>,
    pub shaders_queue_current_pct: Option<f64>,
    pub shaders_cache_off: Option<String>,
    pub shaders_old_driver_note: Option<String>,
    pub shaders_scanning: bool,
    pub shaders_busy: Option<String>,
    pub shaders_error: Option<String>,
    pub compress_list: Option<CompressList>,
    pub compress_error: Option<String>,
    pub storage_scanning: bool,
    pub drive_fix_busy: Option<String>,

    pub apps_sub_tab: AppsSubTab,
    pub apps_query: TextEdit,
    pub apps_list: Option<AppsListReply>,
    pub startup_list: Option<StartupListReply>,
    pub apps_loading: bool,
    pub apps_sort_by: AppsSortBy,
    pub apps_sort_back: bool,
    pub apps_only_unused: bool,
    pub apps_parts_open: bool,
    pub apps_select_mode: bool,
    pub apps_picked: HashSet<String>,
    pub apps_run: Option<(usize, usize)>,
    pub apps_busy_id: Option<String>,
    pub apps_errors: HashMap<String, String>,
    pub apps_removed: Vec<RemovedAppEntry>,
    pub apps_tidy: Option<RemovedAppEntry>,
    pub apps_auto_cleared: Vec<AutoClearedEntry>,
    pub junk_items: Option<Vec<JunkItem>>,
    pub junk_cleaned: Option<(Option<String>, Option<String>)>,
    pub installer_cache: Option<InstallerCacheReply>,
    pub installer_cache_done: Option<(String, Option<String>, bool)>,
    pub installer_cache_error: Option<String>,
    pub startup_errors: HashMap<String, String>,
    pub apps_packages_error: Option<String>,

    pub fps_counter_state: Option<FpsCounterReply>,
    pub web_preferences: Option<WebClientOptions>,

    pub drives: Option<DrivesReply>,
    pub drives_loading: bool,

    pub devices: Option<DevicesListReply>,
    pub audio: Option<AudioReadReply>,
    pub audio_output_id: Option<String>,
    pub audio_mic_id: Option<String>,
    pub keyboard: Option<KeyboardReadReply>,
    pub keyboard_id: Option<String>,
    pub keyboard_error: Option<String>,
    pub keyboard_selected_key: Option<String>,
    pub keyboard_backups: Vec<KeyboardBackupSummary>,
    pub devices_loading: bool,
    pub device_images: HashMap<String, String>,
    pub eq_headphone: Option<EqHeadphone>,
    pub selected_device_id: Option<String>,
    pub mouse_panel_open: bool,
    pub mouse_panel_loading: bool,
    pub mouse_panel_error: Option<String>,
    pub mouse_panel_rect: Option<[f32; 4]>,
    pub mouse_panel_visible: bool,
    pub mouse_panel_window_visible: bool,
    pub owner_hwnd: u64,
    pub app_icons: HashMap<String, String>,
    pub shader_games: Vec<ShaderPrepareGame>,
    pub shader_snap_games: Vec<ShaderSnapGame>,
    pub shader_estimates: HashMap<String, f64>,
    pub gpu_short_name: Option<String>,
    pub screen_label: Option<String>,
    pub compress_potential_bytes: u64,
    pub compress_saved_bytes: u64,
    pub exo_ai_storage_open: bool,
    pub part_menu: Option<PcPartMenu>,
    pub armed_uninstall: Option<(String, u64)>,
    pub armed_uninstall_seq: u64,
}

impl SettingsAndPcState {
    pub fn demo() -> Self {
        Self {
            settings_section: SettingsSection::General,
            settings_query: TextEdit::default(),
            account: ExoAccountState {
                signed_in: true,
                email: Some("player@getexo.app".into()),
                account_asked: true,
            },
            update_check: Some(UpdateCheckReply {
                ok: true,
                already_latest: true,
                update_available: false,
                latest: Some("3.29.0".into()),
                current: Some("3.29.0".into()),
                message: Some("Exo Launcher is up to date.".into()),
                state: Some("idle".into()),
                staged: None,
            }),
            update_progress: None,
            checking_update: false,
            installing_update: false,
            backup: None,
            clients: None,
            system_about: Some(SystemAbout {
                ok: true,
                name: Some("Exo PC".into()),
                processor: Some("AMD Ryzen 7 7800X3D".into()),
                cpu: Some("AMD Ryzen 7 7800X3D".into()),
                graphics: Some("NVIDIA GeForce RTX 3070".into()),
                gpu: Some("NVIDIA GeForce RTX 3070".into()),
                memory_gb: Some(32.0),
                memory_type: Some("DDR5".into()),
                ram_gb: Some(32.0),
                motherboard: Some("ASUS ROG STRIX B650E".into()),
                bios: Some("3057".into()),
                bios_date: Some("2026-09-14".into()),
                windows: Some("Windows 11".into()),
                windows_version: Some("24H2".into()),
                build: Some("26100.2033".into()),
                os: Some("Windows 11 Pro 24H2".into()),
                secure_boot: Some(true),
                tpm: Some("2.0".into()),
                memory_integrity: Some(false),
                network: Some("Ethernet".into()),
                network_mbps: Some(2500.0),
                network_adapter: Some("Intel Ethernet Controller I226-V".into()),
                windows_release: None,
                display: Some("240 Hz".into()),
                version: Some("3.29.0".into()),
            }),
            pc_tab: PcTab::ExoAi,
            exo_ai: Some(ExoAiState {
                ok: true,
                message: None,
                auto_apply_best_settings: true,
                background_drift_check: true,
                last_check_utc: Some("2026-11-22T09:00:00Z".into()),
                auto_applied: vec![
                    "Power plan".into(),
                    "Game Mode".into(),
                    "Game Bar off".into(),
                    "Mouse accel off".into(),
                    "Timer pacing".into(),
                    "HAGS".into(),
                ],
                needs_attention: vec![],
                gaming: ExoAiGaming {
                    active: false,
                    game_id: None,
                    game_title: None,
                    demoted_count: 0,
                    timer_res_active: false,
                    alt_tabbed: false,
                },
                fans: ExoAiFans {
                    supported: true,
                    preset: "balanced".into(),
                    temp_c: Some(41),
                    fan_percent: Some(30),
                    target_percent: Some(30),
                    fan_count: 2,
                    note: None,
                },
            }),
            autopilot: Some(AutopilotState {
                on: true,
                cpu: "1 CCD · 8 cores / 16 threads (3D V-Cache)".into(),
                always_demote: vec![],
                suspend: vec![],
                game_mode: Default::default(),
                mouse_accel: None,
                now: None,
                memory_integrity: AutopilotMemoryIntegrity {
                    status: "off".into(),
                    reason: "Off for +5–10% FPS. None of your installed games require it.".into(),
                    can_undo: true,
                    needs_restart: false,
                },
                standby_purge: None,
                live: Some(AutopilotLive {
                    game: "demo:cyberpunk".into(),
                    title: "Cyberpunk 2077".into(),
                    seconds: 1840.0,
                    fps: 118.4,
                    low1: 91.2,
                    low01: 78.6,
                    stutters: 1,
                    cpu: Some(46.0),
                    clock: Some(112.0),
                    gpu: Some(98.0),
                    gpu_temp: Some(64.0),
                    vram_gb: Some(6.8),
                    vram_total_gb: Some(8.0),
                    ram_gb: Some(15.4),
                    ram_total_gb: Some(32.0),
                    standby_gb: Some(4.2),
                    free_gb: Some(12.4),
                    dpc: Some(0.3),
                }),
                reports: vec![
                    AutopilotReport {
                        id: "r1".into(),
                        game: "demo:cs2".into(),
                        title: "Counter-Strike 2".into(),
                        started: "2026-11-20T18:00:00Z".into(),
                        minutes: 52.0,
                        fps: 342.0,
                        low1: 238.0,
                        low01: 194.0,
                        stutters: 0,
                        ..Default::default()
                    },
                    AutopilotReport {
                        id: "r2".into(),
                        game: "demo:elden".into(),
                        title: "Elden Ring".into(),
                        started: "2026-11-19T21:00:00Z".into(),
                        minutes: 84.0,
                        fps: 60.0,
                        low1: 58.4,
                        low01: 54.0,
                        stutters: 2,
                        ..Default::default()
                    },
                ],
            }),
            tuning: Some(SystemTuningState {
                ok: true,
                message: None,
                applied: true,
                restart: false,
                latency: Some(LatencyReport {
                    before: Some(LatencySample {
                        dpc_us: 842.0,
                        dpc_driver: Some("nvlddmkm.sys".into()),
                        isr_us: 118.0,
                        isr_driver: Some("HDAudBus.sys".into()),
                    }),
                    after: Some(LatencySample {
                        dpc_us: 214.0,
                        dpc_driver: Some("nvlddmkm.sys".into()),
                        isr_us: 48.0,
                        isr_driver: Some("USBXHCI.SYS".into()),
                    }),
                    said: Some("DPC latency dropped 74% after Exo Best Settings.".into()),
                }),
                items: vec![
                    TuningItem {
                        key: "timer".into(),
                        label: "High-precision frame pacing".into(),
                        detail: "Locks Windows timer resolution during play for tighter 1% lows.".into(),
                        on: true,
                        can_undo: true,
                        ..Default::default()
                    },
                    TuningItem {
                        key: "power".into(),
                        label: "Exo Performance Power Plan".into(),
                        detail: "Eliminates PCIe and USB suspend stalls without raising idle draw.".into(),
                        on: true,
                        can_undo: true,
                        ..Default::default()
                    },
                    TuningItem {
                        key: "gamebar".into(),
                        label: "Disable Game Bar capture hook".into(),
                        detail: "Stops background DVR hooks from adding input latency.".into(),
                        on: true,
                        can_undo: true,
                        ..Default::default()
                    },
                    TuningItem {
                        key: "hags".into(),
                        label: "Hardware-accelerated GPU scheduling".into(),
                        detail: "Required for DLSS Frame Generation and lower render queue lag.".into(),
                        on: true,
                        can_undo: false,
                        ..Default::default()
                    },
                ],
            }),
            hdr: Some(HdrState {
                supported: true,
                on: false,
                name: Some("LG OLED 27GR95QE".into()),
                peak_nits: Some(980),
                calibrated: true,
                verdict: Some("great".into()),
            }),
            shaders: Some(ShadersAddonStatus {
                ok: true,
                present: true,
                state: Some("idle".into()),
                message: Some("16 games precompiled".into()),
                snapshot: None,
            }),
            latency_running: false,
            auto_tune: Some(AutoTuneState {
                ok: true,
                message: None,
                parts: vec![
                    TunePart {
                        id: "nvidia".into(),
                        state: "ready".into(),
                        modes: true,
                        mode: "faster".into(),
                        minutes: 12.0,
                        ..Default::default()
                    },
                    TunePart {
                        id: "ryzen".into(),
                        state: "ready".into(),
                        modes: false,
                        mode: "faster".into(),
                        minutes: 15.0,
                        ..Default::default()
                    },
                ],
            }),
            tune_errors: HashMap::new(),
            bloat_apps: Vec::new(),
            windows_packages: Vec::new(),
            auto_tune_stages: Vec::new(),
            auto_tune_running: false,
            auto_tune_finished: false,
            auto_tune_completed_at: Some(1_763_800_000),
            auto_tune_cinema_error: None,
            auto_tune_restarting: false,
            health: Some(PcHealth {
                ok: true,
                message: None,
                items: vec![PcHealthItem {
                    key: "power".into(),
                    label: "Power plan".into(),
                    detail: "Exo power plan active".into(),
                    applied: true,
                    cpu: Some("Ryzen 7 7800X3D".into()),
                    ..Default::default()
                }],
                check: None,
            }),
            interrupts: None,
            interrupts_devices_open: false,
            interrupts_error: None,
            gpu_profile: Some(GpuProfile {
                ok: true,
                vendor: Some("nvidia".into()),
                gpu: Some("NVIDIA GeForce RTX 3070".into()),
                series: 30,
                screen: GpuProfileScreen {
                    name: Some("LG OLED 27GR95QE".into()),
                    hz: 240.0,
                    vrr: true,
                },
                status: "applied".into(),
                can_undo: true,
                rebar: Some(true),
                ..Default::default()
            }),
            gpu_profile_error: None,
            gpu_low_latency_choice: None,
            gpu_driver: None,
            gpu_driver_branch: "game".into(),
            gpu_driver_progress: None,
            gpu_driver_starting: false,
            gpu_driver_consent_open: false,
            gpu_driver_error: None,
            driver_install_overlay: None,
            display_state: None,
            selected_display_id: None,
            display_busy: None,
            display_error: None,
            afmf3: None,
            afmf3_busy: false,
            afmf3_error: None,
            keep_status: None,
            drivers_state: None,
            driver_steps: HashMap::new(),
            drivers_updating_all: false,
            drivers_error: None,
            bios_staged: None,
            bios_progress: None,
            bios_busy: None,
            bios_error: None,
            board_restart_error: None,
            armed_confirm: None,
            autopilot_expanded_report: None,
            autopilot_error: None,
            best_setup_running: None,
            best_setup_error: None,
            network_busy: None,
            network_error: None,
            tuning_busy: None,
            tuning_error: None,
            runtimes: None,
            runtimes_running: None,
            runtimes_canceling: false,
            runtimes_progress: None,
            runtimes_results: HashMap::new(),
            runtimes_note: None,
            controller_firmwares: Vec::new(),
            shaders_rebuild: None,
            shaders_queue_running: false,
            shaders_queue_paused: false,
            shaders_queue_can_start: false,
            shaders_queue_waiting: Vec::new(),
            shaders_queue_current_id: None,
            shaders_queue_current_pct: None,
            shaders_cache_off: None,
            shaders_old_driver_note: None,
            shaders_scanning: false,
            shaders_busy: None,
            shaders_error: None,
            compress_list: None,
            compress_error: None,
            storage_scanning: false,
            drive_fix_busy: None,
            apps_sub_tab: AppsSubTab::Installed,
            apps_query: TextEdit::default(),
            apps_list: Some(AppsListReply {
                ok: true,
                edge_removed: false,
                programs: vec![
                    InstalledAppItem {
                        id: "app:obs".into(),
                        name: "OBS Studio".into(),
                        publisher: Some("OBS Project".into()),
                        version: Some("31.0.2".into()),
                        bytes: Some(412 * 1024 * 1024),
                        store: false,
                        removable: true,
                        care: None,
                        kept: None,
                        bloat: None,
                        last_opened: Some("2026-11-21".into()),
                        uninstaller: true,
                        ..Default::default()
                    },
                    InstalledAppItem {
                        id: "app:armoury".into(),
                        name: "ASUS Armoury Crate".into(),
                        publisher: Some("ASUSTeK".into()),
                        version: Some("5.9.4".into()),
                        bytes: Some(1_180 * 1024 * 1024),
                        store: false,
                        removable: true,
                        care: None,
                        kept: None,
                        bloat: Some("Heavy background telemetry services; Exo replaces lighting & fan curves.".into()),
                        last_opened: Some("2026-07-04".into()),
                        uninstaller: true,
                        ..Default::default()
                    },
                    InstalledAppItem {
                        id: "app:vscode".into(),
                        name: "Visual Studio Code".into(),
                        publisher: Some("Microsoft".into()),
                        version: Some("1.98.0".into()),
                        bytes: Some(465 * 1024 * 1024),
                        store: false,
                        removable: true,
                        care: None,
                        kept: None,
                        bloat: None,
                        last_opened: Some("2026-11-22".into()),
                        uninstaller: true,
                        ..Default::default()
                    },
                    InstalledAppItem {
                        id: "app:vcredist".into(),
                        name: "Microsoft Visual C++ 2022 Redistributable".into(),
                        publisher: Some("Microsoft".into()),
                        version: Some("14.42".into()),
                        bytes: Some(48 * 1024 * 1024),
                        store: false,
                        removable: false,
                        care: Some("runtime".into()),
                        kept: Some("Required by 19 installed games.".into()),
                        bloat: None,
                        last_opened: None,
                        uninstaller: true,
                        ..Default::default()
                    },
                ],
            }),
            startup_list: Some(StartupListReply {
                ok: true,
                message: None,
                extras: 0,
                items: vec![
                    StartupItem {
                        id: "st:steam".into(),
                        source: "run".into(),
                        machine: false,
                        name: "Steam Client Bootstrapper".into(),
                        publisher: Some("Valve".into()),
                        enabled: true,
                        does: Some("Starts Steam in the tray; Exo can launch it only when needed.".into()),
                        verdict: Some("unneeded".into()),
                        unneeded: true,
                        memory: 284 * 1024 * 1024,
                        ..Default::default()
                    },
                    StartupItem {
                        id: "st:security".into(),
                        source: "run".into(),
                        machine: true,
                        name: "Windows Security notification icon".into(),
                        publisher: Some("Microsoft".into()),
                        enabled: true,
                        does: Some("System security status tray icon.".into()),
                        verdict: Some("locked".into()),
                        locked: true,
                        status: Some("Protected system service".into()),
                        memory: 18 * 1024 * 1024,
                        ..Default::default()
                    },
                    StartupItem {
                        id: "st:broken".into(),
                        source: "task".into(),
                        machine: false,
                        name: "Old RGB Updater".into(),
                        publisher: None,
                        enabled: true,
                        does: Some("Target executable is missing on disk.".into()),
                        missing: true,
                        unneeded: true,
                        ..Default::default()
                    },
                ],
                removed: vec![],
            }),
            apps_loading: false,
            apps_sort_by: AppsSortBy::Size,
            apps_sort_back: false,
            apps_only_unused: false,
            apps_parts_open: false,
            apps_select_mode: false,
            apps_picked: HashSet::new(),
            apps_run: None,
            apps_busy_id: None,
            apps_errors: HashMap::new(),
            apps_removed: Vec::new(),
            apps_tidy: None,
            apps_auto_cleared: Vec::new(),
            junk_items: None,
            junk_cleaned: None,
            installer_cache: None,
            installer_cache_done: None,
            installer_cache_error: None,
            startup_errors: HashMap::new(),
            apps_packages_error: None,
            fps_counter_state: None,
            web_preferences: None,
            drives: Some(DrivesReply {
                ok: true,
                message: None,
                fixes: vec![
                    DriveFix {
                        key: "cache".into(),
                        label: "NVMe write caching".into(),
                        detail: "Enabled on all SSDs for faster shader and patch writes.".into(),
                        applied: true,
                        restart: false,
                    },
                    DriveFix {
                        key: "optimize".into(),
                        label: "Scheduled TRIM".into(),
                        detail: "Weekly TRIM keeps flash write speed consistent.".into(),
                        applied: true,
                        restart: false,
                    },
                ],
                drives: vec![
                    DriveInfo {
                        id: "0".into(),
                        model: "Samsung SSD 990 PRO 2TB".into(),
                        kind: "NVMe SSD".into(),
                        link: Some(DriveLink {
                            pcie_gen: 4,
                            width: 4,
                            max_gen: 4,
                            max_width: 4,
                        }),
                        vendor: None,
                        temperature: Some(DriveTemp {
                            now: 39,
                            warn: Some(70),
                        }),
                        health: Some(DriveHealth {
                            life_used: Some(3),
                            spare: Some(100),
                            written_tb: Some(24.6),
                            hours: Some(1840),
                        }),
                        volumes: vec![DriveVolume {
                            letter: "C:".into(),
                            total_gb: 1862.0,
                            free_gb: 614.0,
                        }],
                        write_cache: Some(true),
                        build: Some("TLC + DRAM".into()),
                        space: Some(DriveSpaceBreakdown {
                            total: 2_000_000_000_000,
                            free: 660_000_000_000,
                            games: 840_000_000_000,
                            caches: 52_000_000_000,
                            old_caches: 0,
                            exo: 4_000_000_000,
                            leftovers: 8_000_000_000,
                            other: 436_000_000_000,
                        }),
                        games: vec![
                            DriveGameEntry {
                                id: "demo:bg3".into(),
                                title: "Baldur's Gate 3".into(),
                                bytes: 141 * 1024 * 1024 * 1024,
                            },
                            DriveGameEntry {
                                id: "demo:cyberpunk".into(),
                                title: "Cyberpunk 2077".into(),
                                bytes: 68 * 1024 * 1024 * 1024,
                            },
                            DriveGameEntry {
                                id: "demo:elden".into(),
                                title: "Elden Ring".into(),
                                bytes: 64 * 1024 * 1024 * 1024,
                            },
                        ],
                        verdicts: vec![DriveVerdict {
                            key: "ok".into(),
                            text: "Running at full PCIe Gen4 ×4 with 33% free headroom.".into(),
                            bad: false,
                        }],
                    },
                ],
            }),
            drives_loading: false,
            devices: Some(DevicesListReply {
                ok: true,
                devices: vec![
                    PeripheralCard {
                        id: "dev:wooting".into(),
                        kind: "keyboard".into(),
                        name: "Wooting 60HE+".into(),
                        brand: Some("Wooting".into()),
                        role: Some("Hall-Effect 8000 Hz".into()),
                        connection: Some("usb".into()),
                        battery: None,
                        polling_hz: Some(8000),
                        lab: true,
                        ..Default::default()
                    },
                    PeripheralCard {
                        id: "dev:mouse".into(),
                        kind: "mouse".into(),
                        name: "Logitech G PRO X SUPERLIGHT 2".into(),
                        brand: Some("Logitech".into()),
                        role: Some("Gaming mouse".into()),
                        connection: Some("wireless".into()),
                        battery: Some(0.82),
                        polling_hz: Some(4000),
                        lab: false,
                        ..Default::default()
                    },
                    PeripheralCard {
                        id: "dev:out".into(),
                        kind: "audio".into(),
                        name: "Sennheiser HD 660S2 (DAC)".into(),
                        brand: Some("Sennheiser".into()),
                        role: Some("output".into()),
                        connection: Some("jack".into()),
                        battery: None,
                        polling_hz: None,
                        lab: false,
                        ..Default::default()
                    },
                    PeripheralCard {
                        id: "dev:mic".into(),
                        kind: "audio".into(),
                        name: "Shure MV7 USB Microphone".into(),
                        brand: Some("Shure".into()),
                        role: Some("mic".into()),
                        connection: Some("usb".into()),
                        battery: None,
                        polling_hz: None,
                        lab: false,
                        ..Default::default()
                    },
                ],
            }),
            audio: Some(AudioReadReply {
                ok: true,
                output: Some(AudioEndpoint {
                    volume: Some(74),
                    muted: false,
                    format: Some("24-bit · 48 kHz".into()),
                }),
                mic: Some(AudioEndpoint {
                    volume: Some(88),
                    muted: false,
                    format: Some("24-bit · 48 kHz".into()),
                }),
                voice: AudioVoiceInfo {
                    noise: "rnnoise".into(),
                    push_to_talk: false,
                    focus: true,
                },
            }),
            audio_output_id: Some("dev:out".into()),
            audio_mic_id: Some("dev:mic".into()),
            keyboard: Some(KeyboardReadReply {
                ok: true,
                message: None,
                model: Some("Wooting 60HE+".into()),
                form_factor: Some("60".into()),
                hall_effect: true,
                has_rapid_trigger: true,
                has_rgb: true,
                max_scan_hz: Some(8000),
                min_actuation_mm: Some(0.1),
                max_actuation_mm: Some(4.0),
                firmware: Some("v2.10.1".into()),
                layout: Some("ANSI 60%".into()),
                layout_code: Some("ansi".into()),
                active_profile: 0,
                profiles: vec![
                    KeyboardProfileSlot { index: 0, name: "Competitive FPS".into() },
                    KeyboardProfileSlot { index: 1, name: "Typing".into() },
                    KeyboardProfileSlot { index: 2, name: "Analog Racing".into() },
                    KeyboardProfileSlot { index: 3, name: "Custom".into() },
                ],
                actuation_mm: Some(0.4),
                rapid_trigger: Some(true),
                rapid_trigger_mm: Some(0.15),
                tachyon: Some(true),
                rgb_brightness: Some(80),
                can_write: true,
                wootility_installed: false,
                background_service_running: false,
            }),
            keyboard_id: Some("dev:wooting".into()),
            keyboard_error: None,
            keyboard_selected_key: None,
            keyboard_backups: vec![KeyboardBackupSummary {
                file_name: "wooting-60he-2026-11-20.json".into(),
                model: "Wooting 60HE+".into(),
                product_id: Some(0x1320),
                firmware: Some("v2.10.1".into()),
                created_utc: "2026-11-20".into(),
                profile_count: 4,
            }],
            devices_loading: false,
            device_images: HashMap::new(),
            eq_headphone: None,
            selected_device_id: None,
            mouse_panel_open: false,
            mouse_panel_loading: false,
            mouse_panel_error: None,
            mouse_panel_rect: None,
            mouse_panel_visible: false,
            mouse_panel_window_visible: true,
            owner_hwnd: 0,
            app_icons: HashMap::new(),
            shader_games: vec![
                ShaderPrepareGame {
                    id: "demo:cyberpunk".into(),
                    title: "Cyberpunk 2077".into(),
                    state: "ready".into(),
                    message: Some("1–3 min".into()),
                },
                ShaderPrepareGame {
                    id: "demo:elden".into(),
                    title: "Elden Ring".into(),
                    state: "ready".into(),
                    message: Some("2–5 min".into()),
                },
                ShaderPrepareGame {
                    id: "demo:bg3".into(),
                    title: "Baldur's Gate 3".into(),
                    state: "ready".into(),
                    message: Some("5–15 min".into()),
                },
                ShaderPrepareGame {
                    id: "demo:hd2".into(),
                    title: "Helldivers 2".into(),
                    state: "ready".into(),
                    message: Some("up to 15 min".into()),
                },
            ],
            shader_snap_games: Vec::new(),
            shader_estimates: HashMap::new(),
            gpu_short_name: None,
            screen_label: None,
            compress_potential_bytes: 0,
            compress_saved_bytes: 0,
            exo_ai_storage_open: false,
            part_menu: None,
            armed_uninstall: None,
            armed_uninstall_seq: 0,
        }
    }

    pub fn empty_for_host() -> Self {
        Self {
            settings_section: SettingsSection::General,
            settings_query: TextEdit::default(),
            account: ExoAccountState::default(),
            update_check: None,
            update_progress: None,
            checking_update: false,
            installing_update: false,
            system_about: None,
            backup: None,
            clients: None,
            pc_tab: PcTab::ExoAi,
            exo_ai: None,
            autopilot: None,
            tuning: None,
            hdr: None,
            shaders: None,
            latency_running: false,
            auto_tune: None,
            tune_errors: HashMap::new(),
            bloat_apps: Vec::new(),
            windows_packages: Vec::new(),
            auto_tune_stages: Vec::new(),
            auto_tune_running: false,
            auto_tune_finished: false,
            auto_tune_completed_at: None,
            auto_tune_cinema_error: None,
            auto_tune_restarting: false,
            health: None,
            interrupts: None,
            interrupts_devices_open: false,
            interrupts_error: None,
            gpu_profile: None,
            gpu_profile_error: None,
            gpu_low_latency_choice: None,
            gpu_driver: None,
            gpu_driver_branch: "game".into(),
            gpu_driver_progress: None,
            gpu_driver_starting: false,
            gpu_driver_consent_open: false,
            gpu_driver_error: None,
            driver_install_overlay: None,
            display_state: None,
            selected_display_id: None,
            display_busy: None,
            display_error: None,
            afmf3: None,
            afmf3_busy: false,
            afmf3_error: None,
            keep_status: None,
            drivers_state: None,
            driver_steps: HashMap::new(),
            drivers_updating_all: false,
            drivers_error: None,
            bios_staged: None,
            bios_progress: None,
            bios_busy: None,
            bios_error: None,
            board_restart_error: None,
            armed_confirm: None,
            autopilot_expanded_report: None,
            autopilot_error: None,
            best_setup_running: None,
            best_setup_error: None,
            network_busy: None,
            network_error: None,
            tuning_busy: None,
            tuning_error: None,
            runtimes: None,
            runtimes_running: None,
            runtimes_canceling: false,
            runtimes_progress: None,
            runtimes_results: HashMap::new(),
            runtimes_note: None,
            controller_firmwares: Vec::new(),
            shaders_rebuild: None,
            shaders_queue_running: false,
            shaders_queue_paused: false,
            shaders_queue_can_start: false,
            shaders_queue_waiting: Vec::new(),
            shaders_queue_current_id: None,
            shaders_queue_current_pct: None,
            shaders_cache_off: None,
            shaders_old_driver_note: None,
            shaders_scanning: false,
            shaders_busy: None,
            shaders_error: None,
            compress_list: None,
            compress_error: None,
            storage_scanning: false,
            drive_fix_busy: None,
            apps_sub_tab: AppsSubTab::Installed,
            apps_query: TextEdit::default(),
            apps_list: None,
            startup_list: None,
            apps_loading: false,
            apps_sort_by: AppsSortBy::Size,
            apps_sort_back: false,
            apps_only_unused: false,
            apps_parts_open: false,
            apps_select_mode: false,
            apps_picked: HashSet::new(),
            apps_run: None,
            apps_busy_id: None,
            apps_errors: HashMap::new(),
            apps_removed: Vec::new(),
            apps_tidy: None,
            apps_auto_cleared: Vec::new(),
            junk_items: None,
            junk_cleaned: None,
            installer_cache: None,
            installer_cache_done: None,
            installer_cache_error: None,
            startup_errors: HashMap::new(),
            apps_packages_error: None,
            fps_counter_state: None,
            web_preferences: None,
            drives: None,
            drives_loading: false,
            devices: None,
            audio: None,
            audio_output_id: None,
            audio_mic_id: None,
            keyboard: None,
            keyboard_id: None,
            keyboard_error: None,
            keyboard_selected_key: None,
            keyboard_backups: Vec::new(),
            devices_loading: false,
            device_images: HashMap::new(),
            eq_headphone: None,
            selected_device_id: None,
            mouse_panel_open: false,
            mouse_panel_loading: false,
            mouse_panel_error: None,
            mouse_panel_rect: None,
            mouse_panel_visible: false,
            mouse_panel_window_visible: true,
            owner_hwnd: 0,
            app_icons: HashMap::new(),
            shader_games: Vec::new(),
            shader_snap_games: Vec::new(),
            shader_estimates: HashMap::new(),
            gpu_short_name: None,
            screen_label: None,
            compress_potential_bytes: 0,
            compress_saved_bytes: 0,
            exo_ai_storage_open: false,
            part_menu: None,
            armed_uninstall: None,
            armed_uninstall_seq: 0,
        }
    }
}

pub fn format_bytes(bytes: u64) -> String {
    const KB: f64 = 1_000.0;
    const MB: f64 = 1_000_000.0;
    const GB: f64 = 1_000_000_000.0;
    const TB: f64 = 1_000_000_000_000.0;
    let b = bytes as f64;
    if b >= TB {
        format!("{:.1} TB", b / TB)
    } else if b >= GB {
        format!("{:.1} GB", b / GB)
    } else if b >= MB {
        format!("{:.0} MB", (b / MB).round())
    } else if b >= KB {
        format!("{:.0} KB", (b / KB).round())
    } else {
        format!("{bytes} B")
    }
}

pub fn trim_stop(raw: &str) -> String {
    raw.trim().trim_end_matches('.').to_string()
}

pub fn is_playbook_item(item: &TuningItem) -> bool {
    item.key != "vrr" && item.key != "mouseAccel" && !item.off && !item.blocked.is_some()
}

pub fn has_interrupts_work(interrupts: Option<&InterruptsState>) -> bool {
    let Some(ir) = interrupts else {
        return false;
    };
    if ir.restart {
        return false;
    }
    ir.reset || ir.devices.iter().any(|d| d.plan == "change")
}

pub fn best_steps_list(pc: &SettingsAndPcState, settings: &LauncherSettings) -> Vec<BestStepInfo> {
    let mut steps = Vec::new();
    if let Some(power) = pc
        .health
        .as_ref()
        .and_then(|h| h.items.iter().find(|i| i.key == "power"))
    {
        let cpu = power.cpu.as_deref().unwrap_or("this PC");
        steps.push(BestStepInfo {
            key: "power",
            what: format!("Exo’s power plan for {cpu}"),
            done: power.applied,
        });
    }
    if let Some(profile) = pc.gpu_profile.as_ref()
        && profile.ok
        && let Some(vendor) = profile.vendor.as_deref()
        && matches!(profile.status.as_str(), "applied" | "ready" | "changed")
    {
        let maker = match vendor {
            "nvidia" => "NVIDIA",
            "amd" => "Radeon",
            _ => "Intel",
        };
        steps.push(BestStepInfo {
            key: "profile",
            what: format!("Exo’s {maker} driver profile"),
            done: profile.status == "applied",
        });
    }
    if let Some(network) = pc
        .health
        .as_ref()
        .and_then(|h| h.items.iter().find(|i| i.key == "network"))
    {
        steps.push(BestStepInfo {
            key: "network",
            what: "Exo’s network settings".into(),
            done: network.applied,
        });
    }
    if let Some(tuning) = pc.tuning.as_ref() {
        let items: Vec<&TuningItem> = tuning
            .items
            .iter()
            .filter(|i| !i.opt_in && !i.off && i.blocked.is_none())
            .collect();
        if !items.is_empty() {
            let restart = items.iter().any(|i| i.needs_restart && !i.on);
            let done = items.iter().all(|i| i.on);
            steps.push(BestStepInfo {
                key: "system",
                what: if restart {
                    "Windows’ own defaults for games, after a restart".into()
                } else {
                    "Windows’ own defaults for games".into()
                },
                done,
            });
        }
    }
    if let Some(drives) = pc.drives.as_ref() {
        let fixes: Vec<&DriveFix> = ["cache", "flush", "optimize"]
            .iter()
            .filter_map(|k| drives.fixes.iter().find(|f| f.key == *k))
            .collect();
        if !fixes.is_empty() {
            let names: Vec<&str> = fixes
                .iter()
                .map(|f| match f.key.as_str() {
                    "cache" => "write caching",
                    "flush" => "write-cache flushing",
                    _ => "weekly optimize",
                })
                .collect();
            let listed = if names.len() == 1 {
                format!("{} for your drives", names[0])
            } else {
                format!(
                    "{} and {}",
                    names[..names.len() - 1].join(", "),
                    names[names.len() - 1]
                )
            };
            let mut chars = listed.chars();
            let what = match chars.next() {
                Some(first) => format!("{}{}", first.to_uppercase(), chars.as_str()),
                None => listed,
            };
            let restart = fixes.iter().any(|f| f.restart);
            steps.push(BestStepInfo {
                key: "drives",
                what: if restart {
                    format!("{what}, after a restart")
                } else {
                    what
                },
                done: fixes.iter().all(|f| f.applied),
            });
        }
    }
    if let Some(hdr) = pc.hdr.as_ref()
        && hdr.supported
        && hdr.verdict.as_deref() != Some("weak")
    {
        steps.push(BestStepInfo {
            key: "hdr",
            what: "HDR while an HDR game runs".into(),
            done: settings.hdr_for_games,
        });
    }
    if settings.frame_gen_everywhere_card {
        steps.push(BestStepInfo {
            key: "frameGen",
            what: "Frame generation in games without anti-cheat".into(),
            done: settings.frame_gen_everywhere,
        });
    }
    steps
}

pub fn count_pending_auto_tune(
    pc: &SettingsAndPcState,
    settings: &LauncherSettings,
) -> Option<usize> {
    if pc.gpu_profile.is_none() || pc.health.is_none() {
        return None;
    }
    let steps = best_steps_list(pc, settings)
        .into_iter()
        .filter(|s| !s.done)
        .count();
    let screens = pc
        .display_state
        .as_ref()
        .map(|d| d.displays.iter().filter(|s| !s.is_best).count())
        .unwrap_or(0);
    Some(steps + screens)
}

pub fn compute_initial_auto_tune_stages(
    pc: &SettingsAndPcState,
    settings: &LauncherSettings,
) -> Vec<AutoTuneStageState> {
    let power_done = pc
        .health
        .as_ref()
        .and_then(|h| h.items.iter().find(|i| i.key == "power"))
        .is_some_and(|i| i.applied);
    let fan_ready = pc.exo_ai.as_ref().is_some_and(|a| a.fans.supported);
    let profile_todo = pc
        .gpu_profile
        .as_ref()
        .is_some_and(|p| matches!(p.status.as_str(), "ready" | "changed"));
    let screen_todo = pc
        .display_state
        .as_ref()
        .is_some_and(|d| d.displays.iter().any(|s| !s.is_best));
    let hdr_todo = pc
        .hdr
        .as_ref()
        .is_some_and(|h| h.supported && h.verdict.as_deref() != Some("weak"))
        && !settings.hdr_for_games;
    let fg_todo = settings.frame_gen_everywhere_card && !settings.frame_gen_everywhere;
    let graphics_pending = profile_todo || screen_todo || hdr_todo || fg_todo;

    let sys_todo = pc
        .tuning
        .as_ref()
        .is_some_and(|t| t.items.iter().any(|i| is_playbook_item(i) && !i.on));
    let net_todo = pc
        .health
        .as_ref()
        .and_then(|h| h.items.iter().find(|i| i.key == "network"))
        .is_some_and(|i| !i.applied);
    let drive_todo = pc
        .drives
        .as_ref()
        .is_some_and(|d| d.fixes.iter().any(|f| !f.applied));
    let system_pending =
        sys_todo || net_todo || drive_todo || has_interrupts_work(pc.interrupts.as_ref());

    let bloat_count = pc.bloat_apps.len();
    let bloat_summary = if bloat_count == 0 {
        "No bloatware found".to_string()
    } else if bloat_count == 1 {
        "Remove 1 preinstalled bloat app".to_string()
    } else {
        format!("Remove {bloat_count} preinstalled bloat apps")
    };

    let driver_version = pc
        .gpu_driver
        .as_ref()
        .filter(|d| d.update)
        .and_then(|d| d.latest.as_ref())
        .map(|l| l.version.as_str());

    let cooling_detail = if power_done && fan_ready {
        "Exo power plan, 0.5 ms timer & Autopilot active".to_string()
    } else {
        "Exo power plan, 0.5 ms timer, standby purge & Autopilot".to_string()
    };
    let graphics_detail = if graphics_pending {
        "Driver profile, refresh rate, HDR & frame generation".to_string()
    } else {
        "Graphics profile and display at best defaults".to_string()
    };
    let system_detail = if system_pending {
        "Windows 11 gaming tweaks, MSI interrupts & network".to_string()
    } else {
        "Windows 11 tweaks, MSI interrupts & network optimal".to_string()
    };
    let drivers_detail = match driver_version {
        Some(v) => format!("Driver update ready ({v})"),
        None => "Graphics & chipset drivers up to date".to_string(),
    };

    vec![
        AutoTuneStageState {
            id: AutoTuneStageId::Cooling,
            label: AutoTuneStageId::Cooling.label(),
            status: AutoTuneStageStatus::Idle,
            detail: cooling_detail,
        },
        AutoTuneStageState {
            id: AutoTuneStageId::Graphics,
            label: AutoTuneStageId::Graphics.label(),
            status: AutoTuneStageStatus::Idle,
            detail: graphics_detail,
        },
        AutoTuneStageState {
            id: AutoTuneStageId::System,
            label: AutoTuneStageId::System.label(),
            status: AutoTuneStageStatus::Idle,
            detail: system_detail,
        },
        AutoTuneStageState {
            id: AutoTuneStageId::Bloat,
            label: AutoTuneStageId::Bloat.label(),
            status: AutoTuneStageStatus::Idle,
            detail: bloat_summary,
        },
        AutoTuneStageState {
            id: AutoTuneStageId::Cleanup,
            label: AutoTuneStageId::Cleanup.label(),
            status: AutoTuneStageStatus::Idle,
            detail: "Windows Update, browser, crash dump & launcher caches".to_string(),
        },
        AutoTuneStageState {
            id: AutoTuneStageId::Drivers,
            label: AutoTuneStageId::Drivers.label(),
            status: AutoTuneStageStatus::Idle,
            detail: drivers_detail,
        },
    ]
}

pub fn any_restart_required(pc: &SettingsAndPcState) -> bool {
    if pc
        .auto_tune_stages
        .iter()
        .any(|s| s.detail.to_ascii_lowercase().contains("restart"))
    {
        return true;
    }
    if pc.interrupts.as_ref().is_some_and(|i| i.restart) {
        return true;
    }
    pc.tuning
        .as_ref()
        .is_some_and(|t| t.items.iter().any(|i| i.restart))
}

pub fn compress_words(c: &CompressState) -> String {
    let pct = c
        .percent
        .map(|p| format!(" {}%", (p * 100.0).round() as i64))
        .unwrap_or_default();
    match c.state.as_str() {
        "compressing" => c
            .held
            .clone()
            .unwrap_or_else(|| format!("Compressing{pct}")),
        "uncompressing" => c
            .held
            .clone()
            .unwrap_or_else(|| format!("Putting back{pct}")),
        "queued" => "Waiting its turn".into(),
        "paused" => "Paused".into(),
        "compressed" => format!("Saved {}", format_bytes(c.saved)),
        "changed" => format!(
            "Saved {} · an update added {}",
            format_bytes(c.saved),
            format_bytes(c.changed)
        ),
        "needsConsent" => c
            .anti_cheat
            .as_ref()
            .map(|ac| format!("Uses {ac}"))
            .or_else(|| c.message.clone())
            .unwrap_or_else(|| "Not compressed".into()),
        "refused" => c.message.clone().unwrap_or_else(|| "Not compressed".into()),
        _ => match c.estimate {
            Some(est) if est > 0 => format!(
                "{} about {}",
                if c.sampled { "Saves" } else { "Could save" },
                format_bytes(est)
            ),
            _ => "Not compressed".into(),
        },
    }
}

pub fn leftover_said(left: &LeftoverSummary, sized: bool) -> String {
    let mut parts: Vec<String> = Vec::new();
    if left.folders > 0 {
        parts.push(format!(
            "{} {}",
            left.folders,
            if left.folders == 1 {
                "folder"
            } else {
                "folders"
            }
        ));
    }
    if left.files > 0 {
        parts.push(format!(
            "{} {}",
            left.files,
            if left.files == 1 { "file" } else { "files" }
        ));
    }
    if sized
        && left.bytes > 0
        && let Some(last) = parts.last_mut()
    {
        last.push_str(&format!(" ({})", format_bytes(left.bytes)));
    }
    if left.registry > 0 {
        parts.push(format!(
            "{} {}",
            left.registry,
            if left.registry == 1 {
                "registry entry"
            } else {
                "registry entries"
            }
        ));
    }
    if left.shortcuts > 0 {
        parts.push(format!(
            "{} {}",
            left.shortcuts,
            if left.shortcuts == 1 {
                "shortcut"
            } else {
                "shortcuts"
            }
        ));
    }
    match parts.len() {
        0 => String::new(),
        1 => parts[0].clone(),
        _ => format!(
            "{} and {}",
            parts[..parts.len() - 1].join(", "),
            parts[parts.len() - 1]
        ),
    }
}

pub fn leftover_cleared_line(what: &LeftoverSummary) -> String {
    let body = leftover_said(what, false);
    if what.bytes > 0 {
        format!("Cleared {body}: {} back.", format_bytes(what.bytes))
    } else {
        format!("Cleared {body}.")
    }
}

pub fn is_unused_app(app: &InstalledAppItem, now_days_epoch: i64) -> bool {
    const UNUSED_BYTES: u64 = 200_000_000;
    if app.bytes.unwrap_or(0) < UNUSED_BYTES || !app.removable || app.replaces.is_some() {
        return false;
    }
    if app.quiet_since.is_some() {
        return true;
    }
    app.last_opened
        .as_deref()
        .and_then(parse_ymd_days)
        .is_some_and(|d| now_days_epoch - d > 90)
}

pub fn parse_ymd_days(ymd: &str) -> Option<i64> {
    let mut parts = ymd.split('-');
    let y: i64 = parts.next()?.parse().ok()?;
    let m: i64 = parts.next()?.parse().ok()?;
    let d: i64 = parts.next()?.parse().ok()?;
    let y_adj = y - if m <= 2 { 1 } else { 0 };
    let era = y_adj.div_euclid(400);
    let yoe = y_adj - era * 400;
    let doy = (153 * (m + if m > 2 { -3 } else { 9 }) + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    Some(era * 146_097 + doe - 719_468)
}

pub fn today_epoch_days() -> i64 {
    use std::time::{SystemTime, UNIX_EPOCH};
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| (d.as_secs() / 86_400) as i64)
        .unwrap_or(20_780)
}

#[cfg(test)]
mod wp9_tests {
    use super::*;
    use crate::shell;
    use crate::state::{AppState, Room};
    use exo_rpc::{
        Afmf3State, AppFind, AppLeftover, CompressListGame, DisplayBest, DisplayScreen,
        DriverLatest, DriverState, DriversState, HostEvent, InstallerCacheReply, InterruptDevice,
        KeepStatus, PcHealthItem, RuntimeItem,
    };
    use rune::{Env, Runtime};
    use serde_json::json;

    #[test]
    fn auto_tune_stages_reflect_real_host_state_not_static_copy() {
        let mut s = AppState::default();
        s.pc = SettingsAndPcState::empty_for_host();
        s.pc.bloat_apps.push(BloatAppInfo {
            id: "pkg.ad".into(),
            name: "Candy".into(),
        });
        s.pc.gpu_driver = Some(DriverState {
            ok: true,
            vendor: Some("nvidia".into()),
            installed: Some("560.00".into()),
            update: true,
            latest: Some(DriverLatest {
                version: "566.14".into(),
                size: 700_000_000,
                kind: "game".into(),
                ..Default::default()
            }),
            ..Default::default()
        });
        s.pc.health = Some(PcHealth {
            ok: true,
            items: vec![PcHealthItem {
                key: "network".into(),
                label: "Network".into(),
                applied: false,
                ..Default::default()
            }],
            ..Default::default()
        });
        s.pc.gpu_profile = Some(GpuProfile {
            ok: true,
            vendor: Some("nvidia".into()),
            status: "ready".into(),
            ..Default::default()
        });

        let stages = compute_initial_auto_tune_stages(&s.pc, &s.settings);
        assert_eq!(stages.len(), 6);
        assert_eq!(stages[3].detail, "Remove 1 preinstalled bloat app");
        assert_eq!(stages[5].detail, "Driver update ready (566.14)");
        assert_eq!(count_pending_auto_tune(&s.pc, &s.settings), Some(2));
    }

    #[test]
    fn auto_tune_pawnio_and_unsupported_notice_render() {
        let mut s = AppState::default();
        s.pc.auto_tune = Some(AutoTuneState {
            ok: true,
            message: None,
            parts: vec![TunePart {
                id: "ryzen".into(),
                state: "needs-driver".into(),
                ..Default::default()
            }],
        });
        s.switch_room(Room::ExoAi);
        let mut rt = Runtime::new(s, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.find("tune-pawnio-ryzen").is_some());

        rt.state.pc.auto_tune = Some(AutoTuneState {
            ok: true,
            message: None,
            parts: Vec::new(),
        });
        rt.invalidate();
        rt.frame(1.0 / 60.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.find("exo-ai-tune-hw-wrap").is_some());
        assert!(rt.find("tune-pawnio-ryzen").is_none());
    }

    #[test]
    fn push_events_update_gpu_driver_bios_compress_runtimes_and_shaders() {
        let mut s = AppState::default();
        s.switch_room(Room::ExoAi);
        s.pc.compress_list = Some(CompressList {
            ok: true,
            games: vec![CompressListGame {
                id: "steam:730".into(),
                title: "Counter-Strike 2".into(),
                compress: CompressState {
                    state: "none".into(),
                    ..Default::default()
                },
                eligible: true,
            }],
            saved: 0,
            potential: 10_000_000_000,
            settings: Default::default(),
        });
        s.pc.runtimes_running = Some("update".into());
        s.pc.drivers_state = Some(DriversState {
            ok: true,
            running: true,
            ..Default::default()
        });

        assert!(s.apply_host_event(HostEvent::GpuDriver(DriverProgress {
            stage: "downloading".into(),
            percent: 42.0,
            message: Some("Downloading".into()),
        })));
        assert_eq!(
            s.pc.gpu_driver_progress.as_ref().map(|p| p.percent),
            Some(42.0)
        );
        assert_eq!(
            s.pc.driver_install_overlay
                .as_ref()
                .map(|o| o.phase.as_str()),
            Some("downloading")
        );

        assert!(s.apply_host_event(HostEvent::GpuDriverInstall(
            exo_rpc::GpuDriverInstallEvent {
                phase: "restoring".into(),
                version: Some("621.35".into()),
                message: None,
                restored: vec!["profile".into()],
                not_restored: Vec::new(),
            }
        )));
        assert_eq!(
            s.pc.driver_install_overlay
                .as_ref()
                .map(|o| o.phase.as_str()),
            Some("restoring")
        );

        assert!(s.apply_host_event(HostEvent::DriversStep(DriverStep {
            id: "drv1".into(),
            working: Some("Installing".into()),
            ok: None,
            message: None,
            restart: false,
        })));
        assert!(s.pc.driver_steps.contains_key("drv1"));

        assert!(s.apply_host_event(HostEvent::BiosProgress { share: 0.65 }));
        assert_eq!(s.pc.bios_progress, Some(0.65));

        assert!(s.apply_host_event(HostEvent::CompressProgress(
            exo_rpc::CompressProgressEvent {
                id: "steam:730".into(),
                compress: CompressState {
                    state: "compressing".into(),
                    percent: Some(0.5),
                    sampled: true,
                    ..Default::default()
                },
            }
        )));
        assert_eq!(
            compress_words(&s.pc.compress_list.as_ref().unwrap().games[0].compress),
            "Compressing 50%"
        );

        assert!(
            s.apply_host_event(HostEvent::RuntimesProgress(RuntimesProgress {
                phase: "download".into(),
                id: Some("vc2022".into()),
                name: Some("Visual C++".into()),
                index: 1,
                count: 2,
                bytes: 1000,
                total: Some(2000),
                done_bytes: 5_000_000,
                all_bytes: 10_000_000,
            }))
        );
        assert_eq!(
            s.pc.runtimes_progress
                .as_ref()
                .and_then(|p| p.name.as_deref()),
            Some("Visual C++")
        );

        assert!(s.apply_host_event(HostEvent::ShadersRebuild(ShaderRebuild {
            ok: true,
            state: "running".into(),
            ready: 3,
            total: 8,
            ..Default::default()
        })));
        assert_eq!(s.pc.shaders_rebuild.as_ref().map(|r| r.ready), Some(3));

        assert!(s.apply_host_event(HostEvent::ShadersEvent(
            exo_rpc::ShadersEventPayload {
                name: "progress".into(),
                data: json!({ "rev": 2, "queue": { "running": true, "paused": false, "canStart": false, "waiting": [] } }),
            }
        )));
        assert!(s.pc.shaders_queue_running);
    }

    #[test]
    fn pc_part_menus_and_storage_render_interactive_host_controls() {
        let mut s = AppState::default();
        s.pc.keep_status = Some(KeepStatus {
            pc: "putBackAfterDriverUpdate".into(),
            ..Default::default()
        });
        s.pc.interrupts = Some(InterruptsState {
            ok: true,
            can_undo: true,
            devices: vec![InterruptDevice {
                id: "pci1".into(),
                name: "RTX 3070".into(),
                kind: "GPU".into(),
                mode: "line".into(),
                plan: "change".into(),
                ..Default::default()
            }],
            ..Default::default()
        });
        s.pc.display_state = Some(DisplayState {
            ok: true,
            message: None,
            displays: vec![DisplayScreen {
                id: "disp1".into(),
                name: "LG OLED".into(),
                main: true,
                hdr: false,
                hdr_supported: true,
                vendor: Some("nvidia".into()),
                refresh: 120,
                refreshes: vec![60, 120, 240],
                best: DisplayBest {
                    refresh: 240,
                    ..Default::default()
                },
                is_best: false,
                ..Default::default()
            }],
        });
        s.pc.afmf3 = Some(Afmf3State {
            ok: true,
            offer: true,
            on: false,
            ..Default::default()
        });
        s.pc.runtimes = Some(vec![RuntimeItem {
            id: "vc".into(),
            name: "Visual C++ 2022".into(),
            group: "vc".into(),
            state: "missing".into(),
            bulk: true,
            latest: "14.42".into(),
            ..Default::default()
        }]);
        s.switch_room(Room::ExoAi);
        s.pc.part_menu = Some(PcPartMenu::Board);

        let mut rt = Runtime::new(s, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.find("board-restart-bios").is_some());
        assert!(rt.find("ir-apply").is_some());
        assert!(rt.find("runtimes-install").is_some());

        rt.state.pc.part_menu = Some(PcPartMenu::Display);
        rt.invalidate();
        rt.frame(1.0 / 60.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.find("disp-best-disp1").is_some());
        assert!(rt.find("disp-refresh").is_some());

        rt.state.pc.part_menu = None;
        rt.state.pc.exo_ai_storage_open = true;
        rt.invalidate();
        rt.frame(1.0 / 60.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.find("storage-room").is_some());
        assert!(rt.find("storage-scan").is_some());
    }

    #[test]
    fn apps_leftovers_unused_filter_and_installer_cache_helpers() {
        let summary = LeftoverSummary {
            folders: 2,
            files: 3,
            registry: 5,
            shortcuts: 1,
            bytes: 120_000_000,
        };
        assert_eq!(
            leftover_said(&summary, true),
            "2 folders, 3 files (120 MB), 5 registry entries and 1 shortcut"
        );
        assert_eq!(
            leftover_cleared_line(&summary),
            "Cleared 2 folders, 3 files, 5 registry entries and 1 shortcut: 120 MB back."
        );

        let old_app = InstalledAppItem {
            id: "old".into(),
            name: "Old App".into(),
            bytes: Some(350_000_000),
            removable: true,
            last_opened: Some("2025-01-10".into()),
            uninstaller: true,
            ..Default::default()
        };
        let today = parse_ymd_days("2026-11-23").unwrap();
        assert!(is_unused_app(&old_app, today));

        let mut s = AppState::default();
        s.pc.installer_cache = Some(InstallerCacheReply {
            ok: true,
            files: 4,
            bytes: 180_000_000,
        });
        s.pc.apps_tidy = Some(RemovedAppEntry {
            id: "tidy".into(),
            name: "Left by programs that are gone".into(),
            find: Some(AppFind {
                ok: true,
                token: "tok1".into(),
                name: "OldTool".into(),
                items: vec![AppLeftover {
                    id: 1,
                    kind: "folder".into(),
                    path: "AppData\\Local\\OldTool".into(),
                    bytes: 40_000_000,
                    selected: true,
                    certainty: "certain".into(),
                    because: "Certain: matched folder".into(),
                    ..Default::default()
                }],
                defaults: vec![".xyz".into()],
                ..Default::default()
            }),
            message: None,
            cleared_line: None,
            undo_token: None,
            undone: false,
            review_open: false,
            picks: HashSet::from([1]),
            error: None,
        });
        s.switch_room(Room::Apps);
        let mut rt = Runtime::new(s, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.find("apps-clean-tidy").is_some());
        assert!(rt.find("apps-defaults-tidy").is_some());
        assert!(rt.find("apps-inst-cache-clean").is_some());
        assert!(rt.find("apps-select-toggle").is_some());
    }

    #[test]
    fn fps_counter_options_and_web_preferences_render_in_settings() {
        let mut s = AppState::default();
        s.settings.fps_counter = true;
        s.pc.web_preferences = Some(WebClientOptions {
            music_service: Some("youtube".into()),
            notifications: "ask".into(),
            ..Default::default()
        });
        s.switch_room(Room::Settings);
        s.pc.settings_section = SettingsSection::Graphics;
        let mut rt = Runtime::new(s, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.find("set-fps-position").is_some());
        assert!(rt.find("set-fps-low").is_some());

        rt.state.pc.settings_section = SettingsSection::Updates;
        rt.invalidate();
        rt.frame(1.0 / 60.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        assert!(rt.find("set-yt-notifications").is_some());
    }
}
