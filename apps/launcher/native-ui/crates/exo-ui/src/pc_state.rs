use exo_rpc::{
    AppsListReply, AudioEndpoint, AudioReadReply, AudioVoiceInfo, AutopilotLive,
    AutopilotMemoryIntegrity, AutopilotReport, AutopilotState, DevicesListReply, DriveFix,
    DriveGameEntry, DriveHealth, DriveInfo, DriveLink, DriveSpaceBreakdown, DriveTemp,
    DriveVerdict, DriveVolume, DrivesReply, EqHeadphone, ExoAccountState, ExoAiFans, ExoAiGaming,
    ExoAiState, HdrState, InstalledAppItem, KeyboardBackupSummary, KeyboardProfileSlot,
    KeyboardReadReply, LatencyReport, LatencySample, PeripheralCard, ShaderPrepareGame,
    ShaderSnapGame, ShadersAddonStatus, StartupItem, StartupListReply, SystemAbout,
    SystemTuningState, TuningItem, UpdateCheckReply, UpdateProgress,
};
use rune::TextEdit;
use std::collections::HashMap;

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

pub struct SettingsAndPcState {
    pub settings_section: SettingsSection,
    pub settings_query: TextEdit,
    pub account: ExoAccountState,
    pub update_check: Option<UpdateCheckReply>,
    pub update_progress: Option<UpdateProgress>,
    pub checking_update: bool,
    pub installing_update: bool,
    pub system_about: Option<SystemAbout>,

    pub pc_tab: PcTab,
    pub exo_ai: Option<ExoAiState>,
    pub autopilot: Option<AutopilotState>,
    pub tuning: Option<SystemTuningState>,
    pub hdr: Option<HdrState>,
    pub shaders: Option<ShadersAddonStatus>,
    pub latency_running: bool,

    pub apps_sub_tab: AppsSubTab,
    pub apps_query: TextEdit,
    pub apps_list: Option<AppsListReply>,
    pub startup_list: Option<StartupListReply>,
    pub apps_loading: bool,

    pub drives: Option<DrivesReply>,
    pub drives_loading: bool,

    pub devices: Option<DevicesListReply>,
    pub audio: Option<AudioReadReply>,
    pub audio_output_id: Option<String>,
    pub audio_mic_id: Option<String>,
    pub keyboard: Option<KeyboardReadReply>,
    pub keyboard_backups: Vec<KeyboardBackupSummary>,
    pub devices_loading: bool,
    pub device_images: HashMap<String, String>,
    pub eq_headphone: Option<EqHeadphone>,
    pub selected_device_id: Option<String>,
    pub app_icons: HashMap<String, String>,
    pub shader_games: Vec<ShaderPrepareGame>,
    pub shader_snap_games: Vec<ShaderSnapGame>,
    pub shader_estimates: HashMap<String, f64>,
    pub gpu_short_name: Option<String>,
    pub screen_label: Option<String>,
    pub compress_potential_bytes: u64,
    pub compress_saved_bytes: u64,
    pub exo_ai_storage_open: bool,
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
                windows: Some("Windows 11".into()),
                windows_version: Some("24H2".into()),
                os: Some("Windows 11 Pro 24H2".into()),
                network: Some("Ethernet".into()),
                network_mbps: Some(2500.0),
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
                memory_integrity: AutopilotMemoryIntegrity {
                    status: "off".into(),
                    reason: "Off for +5–10% FPS. None of your installed games require it.".into(),
                    needs_restart: false,
                },
                live: Some(AutopilotLive {
                    game: "demo:cyberpunk".into(),
                    title: "Cyberpunk 2077".into(),
                    seconds: 1840.0,
                    fps: 118.4,
                    low1: 91.2,
                    low01: 78.6,
                    stutters: 1,
                    cpu: Some(46.0),
                    gpu: Some(98.0),
                    gpu_temp: Some(64.0),
                    vram_gb: Some(6.8),
                    vram_total_gb: Some(8.0),
                    ram_gb: Some(15.4),
                    ram_total_gb: Some(32.0),
                    dpc: Some(0.3),
                }),
                reports: vec![
                    AutopilotReport {
                        id: "r1".into(),
                        game: "demo:cs2".into(),
                        title: "Counter-Strike 2".into(),
                        minutes: 52.0,
                        fps: 342.0,
                        low1: 238.0,
                        stutters: 0,
                    },
                    AutopilotReport {
                        id: "r2".into(),
                        game: "demo:elden".into(),
                        title: "Elden Ring".into(),
                        minutes: 84.0,
                        fps: 60.0,
                        low1: 58.4,
                        stutters: 2,
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
                        wooting: true,
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
                        wooting: false,
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
                        wooting: false,
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
                        wooting: false,
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
                hall_effect: true,
                max_scan_hz: Some(8000),
                firmware: Some("v2.10.1".into()),
                layout: Some("ANSI 60%".into()),
                active_profile: 0,
                profiles: vec![
                    KeyboardProfileSlot { index: 0, name: "Competitive FPS".into() },
                    KeyboardProfileSlot { index: 1, name: "Typing".into() },
                    KeyboardProfileSlot { index: 2, name: "Analog Racing".into() },
                    KeyboardProfileSlot { index: 3, name: "Custom".into() },
                ],
                actuation_mm: Some(0.4),
                rapid_trigger: true,
                rapid_trigger_mm: Some(0.15),
                tachyon: true,
                can_write: true,
                wootility_installed: false,
            }),
            keyboard_backups: vec![KeyboardBackupSummary {
                file_name: "wooting-60he-2026-11-20.json".into(),
                model: "Wooting 60HE+".into(),
                created_utc: "2026-11-20".into(),
                profile_count: 4,
            }],
            devices_loading: false,
            device_images: HashMap::new(),
            eq_headphone: None,
            selected_device_id: None,
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
            pc_tab: PcTab::ExoAi,
            exo_ai: None,
            autopilot: None,
            tuning: None,
            hdr: None,
            shaders: None,
            latency_running: false,
            apps_sub_tab: AppsSubTab::Installed,
            apps_query: TextEdit::default(),
            apps_list: None,
            startup_list: None,
            apps_loading: false,
            drives: None,
            drives_loading: false,
            devices: None,
            audio: None,
            audio_output_id: None,
            audio_mic_id: None,
            keyboard: None,
            keyboard_backups: Vec::new(),
            devices_loading: false,
            device_images: HashMap::new(),
            eq_headphone: None,
            selected_device_id: None,
            app_icons: HashMap::new(),
            shader_games: Vec::new(),
            shader_snap_games: Vec::new(),
            shader_estimates: HashMap::new(),
            gpu_short_name: None,
            screen_label: None,
            compress_potential_bytes: 0,
            compress_saved_bytes: 0,
            exo_ai_storage_open: false,
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
