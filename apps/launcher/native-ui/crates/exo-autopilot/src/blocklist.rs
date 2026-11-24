use exo_rpc::anticheat::{is_anti_cheat_process, normalize_process_name};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ProtectionReason {
    AntiCheat,
    ActiveGameTree,
    ExoSelf,
    SessionZero,
    WindowsCore,
    Audio,
    Voice,
    Capture,
    Input,
    Graphics,
}

const EXO_SELF: &[&str] = &[
    "exolauncher",
    "exo-ui",
    "exocapture",
    "exoshaders.service",
    "exomouse.service",
    "exo-autopilot",
];

const WINDOWS_CORE: &[&str] = &[
    "system",
    "idle",
    "system idle process",
    "registry",
    "smss",
    "csrss",
    "wininit",
    "winlogon",
    "services",
    "lsass",
    "lsaiso",
    "lsm",
    "svchost",
    "fontdrvhost",
    "dwm",
    "dwmcore",
    "memcompression",
    "memory compression",
    "secure system",
    "sihost",
    "ctfmon",
    "explorer",
    "taskhostw",
    "taskmgr",
    "runtimebroker",
    "startmenuexperiencehost",
    "shellexperiencehost",
    "shellhost",
    "searchhost",
    "searchindexer",
    "textinputhost",
    "lockapp",
    "logonui",
    "userinit",
    "conhost",
    "dllhost",
    "wudfhost",
    "spoolsv",
    "dashost",
    "wlanext",
    "smartscreen",
    "securityhealthservice",
    "securityhealthsystray",
    "msmpeng",
    "nissrv",
    "mpdefendercoreservice",
    "mssense",
    "sgrmbroker",
    "vmmem",
    "vmmemws",
    "vmcompute",
    "vmwp",
    "winmgmt",
    "wmiprvse",
    "trustedinstaller",
    "tiworker",
    "consent",
    "useroobebroker",
    "applicationframehost",
    "systemsettings",
    "widgets",
    "widgetservice",
    "gamebarpresencewriter",
];

const AUDIO_NAMES: &[&str] = &[
    "audiodg",
    "rtkauduservice64",
    "rtkaudioservice64",
    "ravbg64",
    "nahimicsvc64",
    "nahimicsvc32",
    "a-volute*",
    "voicemeeter*",
    "vb-audio*",
    "steelseriessonar",
    "steelseriesgg",
    "nvbroadcast",
    "nvidia broadcast",
    "krisp",
    "fxsound",
    "dolbydax*",
    "dtsapo*",
    "waves*",
    "maxxaudio*",
    "equalizerapo*",
    "peace*",
    "soundswitch",
    "sonic studio*",
    "senarytechnology*",
];

const VOICE_NAMES: &[&str] = &[
    "discord",
    "discordptb",
    "discordcanary",
    "discorddevelopment",
    "teamspeak",
    "ts3client_win64",
    "ts3client_win32",
    "mumble",
    "skype",
    "teams",
    "ms-teams",
    "msteams",
    "zoom",
    "slack",
    "guilded",
    "element",
    "telegram",
    "whatsapp",
    "signal",
    "vesktop",
    "webcord",
    "revolt",
    "ventrilo",
];

const CAPTURE_NAMES: &[&str] = &[
    "obs64",
    "obs32",
    "obs-browser-page",
    "streamlabs*",
    "xsplit*",
    "prismlivestudio",
    "meldstudio",
    "twitchstudio",
    "vmix*",
    "medal",
    "medalencoder",
    "outplayed",
    "insights-capture",
    "nvcontainer",
    "nvsphelper64",
    "nvidia share",
    "nvidia overlay",
    "amdrsserv",
    "amdow",
    "radeonsoftware",
    "exocapture",
    "gamebar",
    "xboxgamebarwidgets",
    "elgato*",
    "streamdeck",
];

const INPUT_NAMES: &[&str] = &[
    "lghub",
    "lghub_agent",
    "lghub_system_tray",
    "lghub_updater",
    "logioptionsplus*",
    "razer*",
    "rzsynapse*",
    "icue",
    "corsair*",
    "steelseriesengine*",
    "wootility*",
    "wooting*",
    "ds4windows",
    "dsx",
    "steam",
    "steamservice",
    "x360ce",
    "joytokey",
    "rewasd*",
    "glorious*",
    "pulsar*",
    "finalmouse*",
    "lamzu*",
    "vgtray",
    "openmouse*",
];

const GRAPHICS_NAMES: &[&str] = &[
    "nvdisplay.container",
    "nvidia web helper",
    "atieclxx",
    "atiesrxx",
    "amdfendrsr",
    "amdfendr",
    "igfxem",
    "igfxcui",
    "igfxhk",
    "igfxtray",
    "intelcphdcpsvc",
    "intelgraphicssoftware",
    "arccontrol",
    "msiafterburner",
    "rtss",
    "rtsshooksloader64",
];

const KNOWN_BACKGROUND_HOGS: &[&str] = &[
    "chrome",
    "msedge",
    "msedgewebview2",
    "firefox",
    "brave",
    "opera",
    "vivaldi",
    "arc",
    "spotify",
    "onedrive",
    "dropbox",
    "googledrivefs",
    "icloudservices",
    "creative cloud",
    "adobedesktopservice",
    "ccxprocess",
    "adobeipcbroker",
    "compattelrunner",
    "epicwebhelper",
    "steamwebhelper",
    "battlenet",
    "eadesktop",
    "eabackgroundservice",
    "upc",
    "goggalaxy",
    "notion",
    "code",
    "electron",
    "docker",
    "com.docker.backend",
];

pub fn bare_name(raw: &str) -> String {
    normalize_process_name(raw)
}

fn matches_pattern(bare: &str, list: &[&str]) -> bool {
    list.iter().any(|entry| {
        if let Some(prefix) = entry.strip_suffix('*') {
            bare.starts_with(prefix)
        } else {
            bare == *entry
        }
    })
}

pub fn is_anti_cheat(name: &str) -> bool {
    is_anti_cheat_process(name)
}

pub fn is_known_background_hog(name: &str) -> bool {
    let bare = bare_name(name);
    if bare.is_empty() {
        return false;
    }
    KNOWN_BACKGROUND_HOGS.contains(&bare.as_str())
}

#[derive(Debug, Clone, Copy)]
pub struct ProtectionContext<'a> {
    pub current_session_id: u32,
    pub protected_pids: &'a HashSet<u32>,
    pub game_names: &'a HashSet<String>,
}

pub fn why_protected(
    pid: u32,
    name: &str,
    session_id: Option<u32>,
    ctx: &ProtectionContext<'_>,
) -> Option<ProtectionReason> {
    if pid == 0 || pid == 4 {
        return Some(ProtectionReason::WindowsCore);
    }
    if ctx.protected_pids.contains(&pid) {
        return Some(ProtectionReason::ActiveGameTree);
    }
    if let Some(sid) = session_id
        && (sid == 0 || sid != ctx.current_session_id)
    {
        return Some(ProtectionReason::SessionZero);
    }
    let bare = bare_name(name);
    if bare.is_empty() {
        return Some(ProtectionReason::WindowsCore);
    }
    if ctx.game_names.contains(&bare) {
        return Some(ProtectionReason::ActiveGameTree);
    }
    if is_anti_cheat(&bare) {
        return Some(ProtectionReason::AntiCheat);
    }
    if matches_pattern(&bare, EXO_SELF) {
        return Some(ProtectionReason::ExoSelf);
    }
    if matches_pattern(&bare, WINDOWS_CORE) {
        return Some(ProtectionReason::WindowsCore);
    }
    if matches_pattern(&bare, AUDIO_NAMES) {
        return Some(ProtectionReason::Audio);
    }
    if matches_pattern(&bare, VOICE_NAMES) {
        return Some(ProtectionReason::Voice);
    }
    if matches_pattern(&bare, CAPTURE_NAMES) {
        return Some(ProtectionReason::Capture);
    }
    if matches_pattern(&bare, INPUT_NAMES) {
        return Some(ProtectionReason::Input);
    }
    if matches_pattern(&bare, GRAPHICS_NAMES) {
        return Some(ProtectionReason::Graphics);
    }
    None
}

pub fn expand_pid_tree(root_pid: u32, pid_parents: &[(u32, u32)]) -> HashSet<u32> {
    let mut tree = HashSet::new();
    if root_pid == 0 {
        return tree;
    }
    tree.insert(root_pid);
    loop {
        let mut added = false;
        for &(pid, ppid) in pid_parents {
            if tree.contains(&ppid) && tree.insert(pid) {
                added = true;
            }
        }
        if !added {
            break;
        }
    }
    tree
}
