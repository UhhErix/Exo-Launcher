pub use exo_rpc::anticheat::AntiCheatClass;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeSet, HashSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

pub const RIOT_VANGUARD: &str = "Riot Vanguard";
pub const VALVE_ANTI_CHEAT: &str = "Valve Anti-Cheat";
pub const POSSIBLY_VAC_NAME: &str = "possibly Valve Anti-Cheat";
pub const STEAM_LISTED: &str = "an anti-cheat";
pub const UNREADABLE: &str = "unreadable";
pub const OUT_OF_TIME: &str = "out of time";
pub const NO_INSTALL: &str = "no install";
pub const WITHOUT_ANTI_CHEAT_EVIDENCE: &str = "launch without anti-cheat";

const EAC: &str = "Easy Anti-Cheat";
const BE: &str = "BattlEye";
const JAVELIN: &str = "EA Javelin";
const RICOCHET: &str = "Ricochet";
const ACE: &str = "Anti-Cheat Expert";
const HOYO: &str = "HoYoverse anti-cheat";
const NETEASE: &str = "NetEase anti-cheat";
const POE: &str = "Path of Exile's anti-cheat";
const DENUVO: &str = "Denuvo Anti-Cheat";
const FORZA: &str = "Forza's anti-cheat";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum StoreKind {
    Steam,
    Epic,
    Gog,
    Xbox,
    Riot,
    BattleNet,
    Roblox,
    Ubisoft,
    Ea,
    Amazon,
    Local,
    Other,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AntiCheatKind {
    None,
    Found,
    PossiblyVac,
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Change {
    LaunchOptions,
    WorkingDirectory,
    RunAsAdmin,
    StartThroughSteam,
    ShaderRecorder,
    ShaderPrepare,
    InjectedHook,
    FrameGenMod,
    GameSettingsFile,
    DeleteRegenerated,
    UpscalerSwap,
    Compress,
    GpuDriverTuning,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Category {
    Placed,
    OwnInterface,
    SignedUpscaler,
    Stored,
    ExternalDriver,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SteamWord {
    NotSteam,
    None,
    Vac,
    Listed,
    CannotTell,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum DllModPolicy {
    Allowed,
    AllowedWithOfflineNote,
    OptInWithBanRiskConfirmation,
    Disallowed,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MarkerRule {
    Module,
    Program,
    RootOrExe,
    Stub,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameDescriptor {
    pub id: String,
    pub title: String,
    pub store: StoreKind,
    pub path: Option<String>,
    pub launch_target: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Answer {
    pub kind: AntiCheatKind,
    pub names: Vec<String>,
    pub evidence: String,
}

impl Answer {
    pub fn clean() -> Self {
        Self {
            kind: AntiCheatKind::None,
            names: Vec::new(),
            evidence: "none".into(),
        }
    }

    pub fn assume_protected(&self) -> bool {
        self.kind != AntiCheatKind::None
    }

    pub fn name(&self) -> Option<String> {
        if self.names.is_empty() {
            None
        } else {
            Some(join_names(&self.names))
        }
    }

    pub fn label(&self) -> Option<String> {
        match self.kind {
            AntiCheatKind::None => None,
            AntiCheatKind::Unknown => Some(self.evidence.clone()),
            _ => self.name(),
        }
    }
}

pub fn is_unknown_label(label: &str) -> bool {
    matches!(label, UNREADABLE | OUT_OF_TIME | NO_INSTALL)
}

pub fn from_label(label: Option<&str>) -> Answer {
    let Some(label) = label else {
        return Answer::clean();
    };
    if is_unknown_label(label) {
        return Answer {
            kind: AntiCheatKind::Unknown,
            names: Vec::new(),
            evidence: label.to_owned(),
        };
    }
    let names = split_names(label);
    let kind = if !names.is_empty() && names.iter().all(|n| n == POSSIBLY_VAC_NAME) {
        AntiCheatKind::PossiblyVac
    } else {
        AntiCheatKind::Found
    };
    Answer {
        kind,
        names,
        evidence: "kept".into(),
    }
}

pub const fn category_of(change: Change) -> Category {
    match change {
        Change::LaunchOptions | Change::GameSettingsFile | Change::DeleteRegenerated => {
            Category::OwnInterface
        }
        Change::UpscalerSwap => Category::SignedUpscaler,
        Change::Compress => Category::Stored,
        Change::GpuDriverTuning => Category::ExternalDriver,
        _ => Category::Placed,
    }
}

pub fn dll_policy_for(class: AntiCheatClass, answer: &Answer) -> DllModPolicy {
    if class.allows_offline_dll_mod() {
        if answer.kind == AntiCheatKind::None && answer.evidence != WITHOUT_ANTI_CHEAT_EVIDENCE {
            DllModPolicy::Allowed
        } else {
            DllModPolicy::AllowedWithOfflineNote
        }
    } else if class.allows_opt_in_dll_mod() {
        DllModPolicy::OptInWithBanRiskConfirmation
    } else {
        DllModPolicy::Disallowed
    }
}

pub fn join_names(names: &[String]) -> String {
    let set: BTreeSet<&str> = names.iter().map(String::as_str).collect();
    let list: Vec<&str> = set.into_iter().collect();
    match list.len() {
        0 => String::new(),
        1 => list[0].to_owned(),
        n => format!("{} and {}", list[..n - 1].join(", "), list[n - 1]),
    }
}

pub fn split_names(phrase: &str) -> Vec<String> {
    if phrase.is_empty() {
        return Vec::new();
    }
    phrase
        .replace(" and ", ", ")
        .split(", ")
        .filter(|s| !s.is_empty())
        .map(str::to_owned)
        .collect()
}

pub fn title_key(title: &str) -> String {
    title
        .chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .map(|c| c.to_ascii_lowercase())
        .collect()
}

pub const VALVE_VAC: &[u32] = &[
    10, 30, 80, 240, 300, 320, 440, 500, 550, 570, 730, 4000, 1422450,
];

pub const KNOWN_GAMES: &[(&str, &str)] = &[
    ("steam:2139460", NETEASE),
    ("steam:2767030", NETEASE),
    ("epic:575efd0b5dd54429b035ffc8fe2d36d0", NETEASE),
    ("steam:2943650", NETEASE),
    ("steam:2782500", NETEASE),
    ("steam:1203220", NETEASE),
    ("steam:3564740", NETEASE),
    ("steam:2073850", "Elytra"),
    ("steam:2016590", "IronShield"),
    ("steam:1551360", FORZA),
    ("steam:2483190", FORZA),
    ("xbox:Microsoft.ForteBaseGame_8wekyb3d8bbwe", FORZA),
    ("xbox:Microsoft.624F8B84B80_8wekyb3d8bbwe", FORZA),
    ("steam:2000950", RICOCHET),
    ("steam:1938090", RICOCHET),
    ("steam:1962663", RICOCHET),
    ("steam:238960", POE),
    ("steam:2694490", POE),
];

pub const PER_GAME_CLASS_TABLE: &[(Option<&str>, &str, AntiCheatClass)] = &[
    (
        Some("steam:3513350"),
        "wutheringwaves",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        Some("local:wuthering-waves"),
        "wutheringwaves",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        None,
        "genshinimpact",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        None,
        "honkaistarrail",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        None,
        "zenlesszonezero",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        None,
        "honkaiimpact3rd",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        None,
        "toweroffantasy",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        None,
        "arknightsendfield",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        None,
        "punishinggrayraven",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        Some("steam:230410"),
        "warframe",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        Some("steam:238960"),
        "pathofexile",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        Some("steam:2694490"),
        "pathofexile2",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        Some("steam:553850"),
        "helldivers2",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        Some("steam:2074920"),
        "thefirstdescendant",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        Some("steam:2246340"),
        "monsterhunterwilds",
        AntiCheatClass::OnlineNonCompetitivePve,
    ),
    (
        Some("steam:1245620"),
        "eldenring",
        AntiCheatClass::OfflineAntiCheat,
    ),
    (
        Some("steam:1888160"),
        "armoredcorevifiresofrubicon",
        AntiCheatClass::OfflineAntiCheat,
    ),
    (
        Some("steam:251570"),
        "7daystodie",
        AntiCheatClass::OfflineAntiCheat,
    ),
    (
        Some("steam:976730"),
        "halothemasterchiefcollection",
        AntiCheatClass::OfflineAntiCheat,
    ),
    (
        Some("riot:valorant"),
        "valorant",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("riot:league_of_legends"),
        "leagueoflegends",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("riot:teamfighttactics"),
        "teamfighttactics",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("riot:lion"),
        "2xko",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:1938090"),
        "callofdutymodernwarfare4",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:2000950"),
        "callofdutymodernwarfare",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:1962663"),
        "callofdutywarzone",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:730"),
        "counterstrike2",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:570"),
        "dota2",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:440"),
        "teamfortress2",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (None, "fortnite", AntiCheatClass::CompetitivePvpOnline),
    (
        Some("steam:1172470"),
        "apexlegends",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:359550"),
        "tomclancysrainbowsixsiege",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("roblox:player"),
        "roblox",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (None, "overwatch2", AntiCheatClass::CompetitivePvpOnline),
    (
        None,
        "escapefromtarkov",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:2073620"),
        "arenabreakoutinfinite",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:2507950"),
        "deltaforce",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:2943650"),
        "fragpunk",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:2767030"),
        "marvelrivals",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:2073850"),
        "thefinals",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:578080"),
        "pubgbattlegrounds",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:252490"),
        "rust",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:381210"),
        "deadbydaylight",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("epic:Sugar"),
        "rocketleague",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:3472040"),
        "nba2k26",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:1085660"),
        "destiny2",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:1517290"),
        "battlefield2042",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:2807960"),
        "battlefield6",
        AntiCheatClass::CompetitivePvpOnline,
    ),
    (
        Some("steam:1551360"),
        "forzahorizon5",
        AntiCheatClass::CompetitivePvpOnline,
    ),
];

pub const MARKERS: &[(&str, &str, MarkerRule)] = &[
    ("EasyAntiCheat", EAC, MarkerRule::Module),
    ("EasyAntiCheat_EOS", EAC, MarkerRule::Module),
    ("start_protected_game.exe", EAC, MarkerRule::Module),
    ("EasyAntiCheat_EOS_Setup.exe", EAC, MarkerRule::Module),
    ("EasyAntiCheat_Setup.exe", EAC, MarkerRule::Module),
    ("easyanticheat*", EAC, MarkerRule::Module),
    ("eac_launcher*", EAC, MarkerRule::Module),
    ("*_EAC.exe", EAC, MarkerRule::Stub),
    ("BattlEye", BE, MarkerRule::Module),
    ("BEService.exe", BE, MarkerRule::Module),
    ("BEService_x64.exe", BE, MarkerRule::Module),
    ("BELauncher.exe", BE, MarkerRule::Module),
    ("BEClient_x64.dll", BE, MarkerRule::Module),
    ("BEClient.dll", BE, MarkerRule::Module),
    ("beservice*", BE, MarkerRule::Module),
    ("beclient*", BE, MarkerRule::Module),
    ("bedaisy*", BE, MarkerRule::Module),
    ("*_BE.exe", BE, MarkerRule::Stub),
    ("EAAntiCheat.Installer.exe", JAVELIN, MarkerRule::Module),
    (
        "EAAntiCheat.GameServiceLauncher.exe",
        JAVELIN,
        MarkerRule::Module,
    ),
    (
        "EAAntiCheat.GameServiceLauncher.dll",
        JAVELIN,
        MarkerRule::Module,
    ),
    ("EAAntiCheat", JAVELIN, MarkerRule::Module),
    ("eaanticheat*", JAVELIN, MarkerRule::Module),
    ("GameGuard", "nProtect GameGuard", MarkerRule::Module),
    ("nProtect", "nProtect GameGuard", MarkerRule::Module),
    ("gameguard.des", "nProtect GameGuard", MarkerRule::Module),
    ("XIGNCODE", "XIGNCODE3", MarkerRule::Module),
    ("*.xem", "XIGNCODE3", MarkerRule::Module),
    ("NCGuardSDK", "NCGuard", MarkerRule::Module),
    ("NCGuard", "NCGuard", MarkerRule::Module),
    ("randgrid.sys", RICOCHET, MarkerRule::Module),
    ("randgrid*", RICOCHET, MarkerRule::Module),
    ("ModernWarfare.exe", RICOCHET, MarkerRule::Program),
    ("cod.exe", RICOCHET, MarkerRule::RootOrExe),
    ("AntiCheatExpert", ACE, MarkerRule::Module),
    ("AceAntibotClient", ACE, MarkerRule::Module),
    ("TP3Helper.exe", ACE, MarkerRule::Module),
    ("SGuard", ACE, MarkerRule::Module),
    ("SGuard64.exe", ACE, MarkerRule::Module),
    ("SGuardSvc64.exe", ACE, MarkerRule::Module),
    ("sguard*", ACE, MarkerRule::Module),
    ("ACE-Base64.dll", ACE, MarkerRule::Module),
    ("ACE-Base.dat", ACE, MarkerRule::Module),
    ("ACE-Setup64.exe", ACE, MarkerRule::Module),
    ("ACE-Service64.exe", ACE, MarkerRule::Module),
    ("ACE-ATS64.dll", ACE, MarkerRule::Module),
    ("ACE-CSI64.dll", ACE, MarkerRule::Module),
    ("ACE-DFS64.dll", ACE, MarkerRule::Module),
    ("ACE-BASE.sys", ACE, MarkerRule::Module),
    ("ace-base*", ACE, MarkerRule::Module),
    ("TenProtect", "TenProtect", MarkerRule::Module),
    ("TesSafe.sys", "TenProtect", MarkerRule::Module),
    ("HoYoKProtect.sys", HOYO, MarkerRule::Module),
    ("mhypbase.dll", HOYO, MarkerRule::Module),
    ("mhyprot.sys", HOYO, MarkerRule::Module),
    ("mhyprot2.sys", HOYO, MarkerRule::Module),
    ("mhyprot3.sys", HOYO, MarkerRule::Module),
    ("mhyprot*", HOYO, MarkerRule::Module),
    ("mhypbase*", HOYO, MarkerRule::Module),
    ("GenshinImpact.exe", HOYO, MarkerRule::Program),
    ("YuanShen.exe", HOYO, MarkerRule::Program),
    ("StarRail.exe", HOYO, MarkerRule::Program),
    ("ZenlessZoneZero.exe", HOYO, MarkerRule::Program),
    ("BH3.exe", HOYO, MarkerRule::Program),
    ("HYP.exe", HOYO, MarkerRule::Module),
    ("HYPHelper.exe", HOYO, MarkerRule::Module),
    ("HYPWorker.exe", HOYO, MarkerRule::Module),
    ("vgk.sys", RIOT_VANGUARD, MarkerRule::Module),
    ("vgc.exe", RIOT_VANGUARD, MarkerRule::Module),
    ("VALORANT.exe", RIOT_VANGUARD, MarkerRule::Program),
    (
        "VALORANT-Win64-Shipping.exe",
        RIOT_VANGUARD,
        MarkerRule::Program,
    ),
    ("League of Legends.exe", RIOT_VANGUARD, MarkerRule::Program),
    ("LeagueClient.exe", RIOT_VANGUARD, MarkerRule::Program),
    ("LeagueClientUx.exe", RIOT_VANGUARD, MarkerRule::Program),
    (
        "LeagueClientUxRender.exe",
        RIOT_VANGUARD,
        MarkerRule::Program,
    ),
    ("LoR.exe", RIOT_VANGUARD, MarkerRule::Program),
    (
        "Lion-Win64-Shipping.exe",
        RIOT_VANGUARD,
        MarkerRule::Program,
    ),
    ("RiotClientServices.exe", RIOT_VANGUARD, MarkerRule::Module),
    ("RiotClientUx.exe", RIOT_VANGUARD, MarkerRule::Module),
    ("RiotClientUxRender.exe", RIOT_VANGUARD, MarkerRule::Module),
    ("NeacClient.exe", NETEASE, MarkerRule::Module),
    ("NeacSafe64.sys", NETEASE, MarkerRule::Module),
    ("NeacSafe64_ex.sys", NETEASE, MarkerRule::Module),
    ("neacclient*", NETEASE, MarkerRule::Module),
    ("neacsafe*", NETEASE, MarkerRule::Module),
    ("NEP2.dll", "NetEase Protect", MarkerRule::Module),
    ("NEPCleaner.exe", "NetEase Protect", MarkerRule::Module),
    ("ONCE_HUMAN.exe", NETEASE, MarkerRule::Program),
    ("Marvel-Win64-Shipping.exe", NETEASE, MarkerRule::Program),
    ("Marvel-Win64-Test.exe", NETEASE, MarkerRule::Program),
    ("Marvel.exe", NETEASE, MarkerRule::RootOrExe),
    ("MarvelRivals_Launcher.exe", NETEASE, MarkerRule::Program),
    ("FragPunk.exe", NETEASE, MarkerRule::Program),
    ("nshm.exe", NETEASE, MarkerRule::Program),
    ("NarakaBladepoint.exe", NETEASE, MarkerRule::Program),
    ("wwm.exe", NETEASE, MarkerRule::Program),
    ("BlackCall.aes", "Nexon Game Security", MarkerRule::Module),
    ("BlackCall64.aes", "Nexon Game Security", MarkerRule::Module),
    ("BlackCat64.sys", "Nexon Game Security", MarkerRule::Module),
    ("HShield", "AhnLab HackShield", MarkerRule::Module),
    ("PunkBuster", "PunkBuster", MarkerRule::Module),
    ("PnkBstrA.exe", "PunkBuster", MarkerRule::Module),
    ("pbsvc.exe", "PunkBuster", MarkerRule::Module),
    ("pbsv.dll", "PunkBuster", MarkerRule::Module),
    ("pnkbstr*", "PunkBuster", MarkerRule::Module),
    ("pbcl*", "PunkBuster", MarkerRule::Module),
    ("equ8_conf.json", "EQU8", MarkerRule::Module),
    ("EQU8", "EQU8", MarkerRule::Module),
    ("equ8*", "EQU8", MarkerRule::Module),
    ("FACEIT", "FACEIT", MarkerRule::Module),
    ("faceit*", "FACEIT", MarkerRule::Module),
    (".build.info", "Warden", MarkerRule::Module),
    (".product.db", "Warden", MarkerRule::Module),
    (r"data\.build.config", "Warden", MarkerRule::RootOrExe),
    (
        "Warframe.x64.exe",
        "Warframe's anti-cheat",
        MarkerRule::Program,
    ),
    ("Discovery.exe", "Elytra", MarkerRule::RootOrExe),
    ("Discovery-e.exe", "Elytra", MarkerRule::Program),
    ("Discovery-d.exe", "Elytra", MarkerRule::Program),
    ("Elytra", "Elytra", MarkerRule::Module),
    ("Elytra.exe", "Elytra", MarkerRule::Module),
    ("Elytra-Setup.exe", "Elytra", MarkerRule::Module),
    ("DungeonCrawler.exe", "IronShield", MarkerRule::Program),
    ("forzahorizon5.exe", FORZA, MarkerRule::Program),
    ("forzahorizon6.exe", FORZA, MarkerRule::Program),
    ("PathOfExile.exe", POE, MarkerRule::Program),
    ("PathOfExile_x64.exe", POE, MarkerRule::Program),
    ("PathOfExileSteam.exe", POE, MarkerRule::Program),
    ("PathOfExile_x64Steam.exe", POE, MarkerRule::Program),
    ("PathOfExileEGS.exe", POE, MarkerRule::Program),
    ("PathOfExile_x64EGS.exe", POE, MarkerRule::Program),
    ("PathOfExile_KG.exe", POE, MarkerRule::Program),
    ("PathOfExile_x64_KG.exe", POE, MarkerRule::Program),
    ("RobloxPlayerBeta.exe", "Hyperion", MarkerRule::Program),
    ("RobloxPlayerBeta.dll", "Hyperion", MarkerRule::Module),
    ("RobloxPlayerLauncher.exe", "Hyperion", MarkerRule::Program),
    ("RobloxPlayerInstaller.exe", "Hyperion", MarkerRule::Module),
    ("arbiter.dll", "Arbiter", MarkerRule::Module),
    ("DenuvoAC", DENUVO, MarkerRule::Module),
    ("Denuvo Anti-Cheat", DENUVO, MarkerRule::Module),
    ("denuvo-anti-cheat.sys", DENUVO, MarkerRule::Module),
    ("denuvo-anti-cheat-runtime.dll", DENUVO, MarkerRule::Module),
    (
        "denuvo-anti-cheat-update-service.exe",
        DENUVO,
        MarkerRule::Module,
    ),
    (
        "Denuvo Anti-Cheat Installer.exe",
        DENUVO,
        MarkerRule::Module,
    ),
    ("denuvo-anti-cheat*", DENUVO, MarkerRule::Module),
    ("battleye*", BE, MarkerRule::Module),
    ("vgk*", RIOT_VANGUARD, MarkerRule::Module),
    ("vgc*", RIOT_VANGUARD, MarkerRule::Module),
    ("gameguard*", "nProtect GameGuard", MarkerRule::Module),
    ("ricochet*", RICOCHET, MarkerRule::Module),
    ("xigncode*", "XIGNCODE3", MarkerRule::Module),
];

pub const SERVICE_PREFIXES: &[(&str, &str)] = &[
    ("EasyAntiCheat", EAC),
    ("BEService", BE),
    ("BEDaisy", BE),
    ("vgk", RIOT_VANGUARD),
    ("vgc", RIOT_VANGUARD),
    ("FACEIT", "FACEIT"),
    ("EAAntiCheat", JAVELIN),
    ("PnkBstr", "PunkBuster"),
    ("xhunter", "XIGNCODE3"),
    ("npggsvc", "nProtect GameGuard"),
    ("ACE-", ACE),
    ("AntiCheatExpert", ACE),
    ("mhyprot", HOYO),
    ("HoYoKProtect", HOYO),
    ("atvi-randgrid", RICOCHET),
    ("randgrid", RICOCHET),
    ("EQU8", "EQU8"),
    ("NEAC", NETEASE),
    ("TesSafe", "TenProtect"),
    ("denuvo-anti-cheat", DENUVO),
    ("Elytra", "Elytra"),
];

const TITLE_GROUPS: &[(&[&str], &str)] = &[
    (
        &[
            "valorant",
            "league of legends",
            "teamfight tactics",
            "legends of runeterra",
            "2xko",
        ],
        RIOT_VANGUARD,
    ),
    (&["call of duty", "warzone"], RICOCHET),
    (&["battlefield"], JAVELIN),
    (
        &[
            "fortnite",
            "apex legends",
            "dead by daylight",
            "elden ring",
            "hunt: showdown",
            "rust",
            "multiversus",
            "predecessor",
            "lost ark",
            "new world",
            "fall guys",
        ],
        EAC,
    ),
    (
        &[
            "pubg",
            "playerunknown's battlegrounds",
            "rainbow six",
            "escape from tarkov",
            "destiny 2",
            "dayz",
            "arma 3",
            "arma reforger",
            "the division",
            "for honor",
            "splitgate",
        ],
        BE,
    ),
    (
        &[
            "counter-strike",
            "dota 2",
            "deadlock",
            "team fortress 2",
            "left 4 dead",
            "garry's mod",
            "day of defeat",
            "half-life 2: deathmatch",
        ],
        VALVE_ANTI_CHEAT,
    ),
    (
        &[
            "overwatch",
            "world of warcraft",
            "hearthstone",
            "heroes of the storm",
        ],
        "Warden",
    ),
    (
        &[
            "marvel rivals",
            "naraka",
            "once human",
            "fragpunk",
            "where winds meet",
        ],
        NETEASE,
    ),
    (&["the finals"], "Elytra"),
    (&["delta force", "arena breakout", "wuthering waves"], ACE),
    (
        &[
            "genshin impact",
            "honkai: star rail",
            "honkai impact",
            "zenless zone zero",
        ],
        HOYO,
    ),
    (&["throne and liberty", "black desert"], STEAM_LISTED),
    (&["path of exile"], POE),
    (&["warframe"], "Warframe's anti-cheat"),
    (&["forza horizon"], FORZA),
    (&["dark and darker"], "IronShield"),
    (&["halo infinite"], "Arbiter"),
    (&["roblox"], "Hyperion"),
];

const REFUSED_SWITCHES: &[&str] = &[
    "insecure",
    "allow_third_party_software",
    "noeac",
    "no-eac",
    "no_eac",
    "eac_disable",
    "disableeac",
    "nobattleye",
    "no-battleye",
    "no_battleye",
    "nobe",
    "battleye_disable",
    "disablebattleye",
    "noanticheat",
    "no-anticheat",
    "no_anticheat",
    "disableanticheat",
    "anticheat_disable",
    "nosteamac",
    "novac",
];

pub const NO_MARKER_FOLDERS: &[&str] = &[
    "paks",
    "movies",
    "videos",
    "cinematics",
    "shadercache",
    "saved",
    "logs",
    "crashdumps",
    "node_modules",
    ".git",
    "intermediate",
];

pub fn for_store(store: StoreKind) -> Option<&'static str> {
    match store {
        StoreKind::Riot => Some(RIOT_VANGUARD),
        StoreKind::BattleNet => Some("Warden"),
        StoreKind::Roblox => Some("Hyperion"),
        _ => None,
    }
}

pub fn known_game(id: &str) -> Option<&'static str> {
    KNOWN_GAMES
        .iter()
        .find(|(k, _)| k.eq_ignore_ascii_case(id))
        .map(|(_, v)| *v)
}

pub fn is_riot(game: &GameDescriptor) -> bool {
    if game.store == StoreKind::Riot {
        return true;
    }
    if let Some(ref p) = game.path {
        let normalized = p.replace('/', "\\").to_ascii_lowercase();
        if normalized.contains("\\riot games\\") {
            return true;
        }
    }
    let candidate = game.launch_target.as_deref().or(game.path.as_deref());
    if let Some(target) = candidate
        && let Some(file_name) = Path::new(target).file_name().and_then(|s| s.to_str())
    {
        return MARKERS.iter().any(|&(pat, ac, rule)| {
            ac == RIOT_VANGUARD
                && rule == MarkerRule::Program
                && pat.eq_ignore_ascii_case(file_name)
        });
    }
    false
}

pub fn by_title(title: &str) -> Option<&'static str> {
    let lower = title.to_ascii_lowercase();
    for &(terms, anti_cheat) in TITLE_GROUPS {
        for &term in terms {
            if contains_whole_phrase(&lower, term) {
                return Some(anti_cheat);
            }
        }
    }
    None
}

fn contains_whole_phrase(haystack: &str, needle: &str) -> bool {
    let h_bytes = haystack.as_bytes();
    let n_len = needle.len();
    if n_len == 0 || n_len > h_bytes.len() {
        return false;
    }
    let mut start = 0;
    while let Some(rel) = haystack[start..].find(needle) {
        let idx = start + rel;
        let before_ok = idx == 0
            || !haystack[..idx]
                .chars()
                .next_back()
                .unwrap()
                .is_alphanumeric();
        let end = idx + n_len;
        let after_ok =
            end == haystack.len() || !haystack[end..].chars().next().unwrap().is_alphanumeric();
        if before_ok && after_ok {
            return true;
        }
        start = idx + 1;
    }
    false
}

pub fn quick(game: &GameDescriptor) -> Option<&'static str> {
    if is_riot(game) {
        return Some(RIOT_VANGUARD);
    }
    for_store(game.store)
        .or_else(|| known_game(&game.id))
        .or_else(|| by_title(&game.title))
}

pub fn for_service(service_name: &str) -> Option<&'static str> {
    let lower = service_name.to_ascii_lowercase();
    SERVICE_PREFIXES
        .iter()
        .find(|(prefix, _)| lower.starts_with(&prefix.to_ascii_lowercase()))
        .map(|(_, ac)| *ac)
}

fn matches_marker(pattern: &str, name: &str) -> bool {
    if let Some(suffix) = pattern.strip_prefix('*') {
        name.len() >= suffix.len() && name[name.len() - suffix.len()..].eq_ignore_ascii_case(suffix)
    } else if let Some(prefix) = pattern.strip_suffix('*') {
        name.len() >= prefix.len() && name[..prefix.len()].eq_ignore_ascii_case(prefix)
    } else {
        name.eq_ignore_ascii_case(pattern)
    }
}

pub fn marker_for(name: &str) -> Option<(&'static str, &'static str, MarkerRule)> {
    MARKERS
        .iter()
        .copied()
        .find(|&(pat, _, _)| !pat.contains('\\') && matches_marker(pat, name))
}

pub fn for_folder(folder_name: &str) -> Option<&'static str> {
    match marker_for(folder_name) {
        Some((_, ac, MarkerRule::Module)) => Some(ac),
        _ => None,
    }
}

pub fn for_file(file_name: &str) -> Option<&'static str> {
    match marker_for(file_name) {
        Some((_, ac, MarkerRule::Module | MarkerRule::Stub)) => Some(ac),
        _ => None,
    }
}

pub fn refused_argument(arguments: &str) -> Option<String> {
    for raw in arguments.split([' ', '\t']).filter(|s| !s.is_empty()) {
        let token = raw.trim_matches(['"', '\'']);
        let lower = token.to_ascii_lowercase();
        if lower.contains(".dll") || lower.contains("plugin") {
            return Some(raw.to_owned());
        }
        let name = lower.trim_start_matches(['-', '+', '/']);
        if REFUSED_SWITCHES.contains(&name) || is_anti_cheat_off_pair(name) {
            return Some(raw.to_owned());
        }
    }
    None
}

fn is_anti_cheat_off_pair(name: &str) -> bool {
    let Some((key, val)) = name.split_once('=').or_else(|| name.split_once(':')) else {
        return false;
    };
    let key_match = matches!(
        key,
        "eac" | "easyanticheat" | "battleye" | "be" | "anticheat" | "ac" | "vac"
    );
    let val_match = matches!(val, "0" | "off" | "false" | "no" | "disable" | "disabled");
    key_match && val_match
}

fn add_name(
    names: &mut BTreeSet<String>,
    evidence: &mut Option<&'static str>,
    name: Option<&str>,
    source: &'static str,
) {
    if let Some(n) = name
        && names.insert(n.to_owned())
        && evidence.is_none()
    {
        *evidence = Some(source);
    }
}

#[allow(clippy::too_many_arguments)]
pub fn decide(
    store: Option<&str>,
    known: Option<&str>,
    install_found: bool,
    disk: Option<&str>,
    service: Option<&str>,
    steam: SteamWord,
    title: Option<&str>,
    without_entry: bool,
) -> Answer {
    if store == Some(RIOT_VANGUARD) {
        return Answer {
            kind: AntiCheatKind::Found,
            names: vec![RIOT_VANGUARD.to_owned()],
            evidence: "store".into(),
        };
    }

    let mut names: BTreeSet<String> = BTreeSet::new();
    let mut evidence: Option<&'static str> = None;

    add_name(&mut names, &mut evidence, store, "store");
    add_name(&mut names, &mut evidence, known, "known game");
    let walked = disk.map(split_names).unwrap_or_default();
    for name in walked.iter().filter(|n| !is_unknown_label(n)) {
        add_name(&mut names, &mut evidence, Some(name.as_str()), "files");
    }
    add_name(&mut names, &mut evidence, service, "service");
    if steam == SteamWord::Vac {
        add_name(&mut names, &mut evidence, Some(VALVE_ANTI_CHEAT), "steam");
    }
    if steam == SteamWord::Listed && names.is_empty() {
        add_name(&mut names, &mut evidence, Some(STEAM_LISTED), "steam");
    }

    let unknown = if walked.iter().any(|n| n == OUT_OF_TIME) {
        Some(OUT_OF_TIME)
    } else if walked.iter().any(|n| n == UNREADABLE) {
        Some(UNREADABLE)
    } else if !install_found && names.is_empty() && title.is_none() {
        Some(NO_INSTALL)
    } else {
        None
    };

    if let Some(reason) = unknown {
        return Answer {
            kind: AntiCheatKind::Unknown,
            names: names.into_iter().collect(),
            evidence: reason.to_owned(),
        };
    }

    if names.is_empty() && title.is_some() {
        add_name(&mut names, &mut evidence, title, "title");
    }
    if steam == SteamWord::CannotTell && !names.contains(VALVE_ANTI_CHEAT) {
        add_name(&mut names, &mut evidence, Some(POSSIBLY_VAC_NAME), "steam");
    }

    if without_entry
        && !names.is_empty()
        && store.is_none()
        && service.is_none()
        && steam != SteamWord::Vac
        && !names.contains(VALVE_ANTI_CHEAT)
        && !names.contains(RIOT_VANGUARD)
    {
        return Answer {
            kind: AntiCheatKind::None,
            names: Vec::new(),
            evidence: WITHOUT_ANTI_CHEAT_EVIDENCE.into(),
        };
    }

    if names.is_empty() {
        return Answer::clean();
    }

    let list: Vec<String> = names.into_iter().collect();
    let kind = if list.iter().all(|n| n == POSSIBLY_VAC_NAME) {
        AntiCheatKind::PossiblyVac
    } else {
        AntiCheatKind::Found
    };
    Answer {
        kind,
        names: list,
        evidence: evidence.unwrap_or("none").to_owned(),
    }
}

pub fn allows(
    game: &GameDescriptor,
    answer: &Answer,
    change: Change,
    consented: Option<&str>,
) -> bool {
    let riot = is_riot(game) || answer.names.iter().any(|n| n == RIOT_VANGUARD);
    if answer.kind == AntiCheatKind::None {
        return !is_riot(game);
    }
    if riot || answer.kind == AntiCheatKind::Unknown {
        return false;
    }
    match category_of(change) {
        Category::OwnInterface | Category::ExternalDriver => true,
        Category::SignedUpscaler | Category::Stored => {
            if let (Some(label), Some(consent)) = (answer.label(), consented) {
                !consent.trim().is_empty() && consent == label
            } else {
                false
            }
        }
        Category::Placed => false,
    }
}

pub fn classify(game: &GameDescriptor, answer: &Answer) -> AntiCheatClass {
    if is_riot(game) || answer.names.iter().any(|n| n == RIOT_VANGUARD) {
        return AntiCheatClass::CompetitivePvpOnline;
    }
    if answer.kind == AntiCheatKind::None {
        return AntiCheatClass::OfflineAntiCheat;
    }
    if answer.kind == AntiCheatKind::Unknown {
        return AntiCheatClass::Unknown;
    }
    if !game.id.is_empty()
        && let Some(&(_, _, cls)) = PER_GAME_CLASS_TABLE
            .iter()
            .find(|(gid, _, _)| gid.is_some_and(|id| id.eq_ignore_ascii_case(&game.id)))
    {
        return cls;
    }
    let key = title_key(&game.title);
    if !key.is_empty() {
        if let Some(&(_, _, cls)) = PER_GAME_CLASS_TABLE.iter().find(|(_, tk, _)| *tk == key) {
            return cls;
        }
        if key.starts_with("callofduty") || key.starts_with("battlefield") {
            return AntiCheatClass::CompetitivePvpOnline;
        }
    }
    AntiCheatClass::CompetitivePvpOnline
}

pub fn wire_class(game: &GameDescriptor, cached: Option<&Answer>) -> &'static str {
    if is_riot(game) || cached.is_some_and(|a| a.names.iter().any(|n| n == RIOT_VANGUARD)) {
        return AntiCheatClass::CompetitivePvpOnline.as_wire_str();
    }
    if let Some(clean) = cached
        && clean.kind == AntiCheatKind::None
    {
        return classify(game, clean).as_wire_str();
    }
    if !game.id.is_empty()
        && let Some(&(_, _, cls)) = PER_GAME_CLASS_TABLE
            .iter()
            .find(|(gid, _, _)| gid.is_some_and(|id| id.eq_ignore_ascii_case(&game.id)))
    {
        return cls.as_wire_str();
    }
    let key = title_key(&game.title);
    if !key.is_empty() {
        if let Some(&(_, _, cls)) = PER_GAME_CLASS_TABLE.iter().find(|(_, tk, _)| *tk == key) {
            return cls.as_wire_str();
        }
        if key.starts_with("callofduty") || key.starts_with("battlefield") {
            return AntiCheatClass::CompetitivePvpOnline.as_wire_str();
        }
    }
    match cached {
        None => {
            if quick(game).is_some() {
                AntiCheatClass::CompetitivePvpOnline.as_wire_str()
            } else {
                AntiCheatClass::Unknown.as_wire_str()
            }
        }
        Some(a) if a.kind == AntiCheatKind::Unknown => {
            if quick(game).is_some() {
                AntiCheatClass::CompetitivePvpOnline.as_wire_str()
            } else {
                AntiCheatClass::Unknown.as_wire_str()
            }
        }
        Some(a) => classify(game, a).as_wire_str(),
    }
}

pub const MAX_WALK_DEPTH: usize = 16;
pub const MAX_WALK_ENTRIES: usize = 2_000_000;
pub const WALK_BUDGET: Duration = Duration::from_secs(30);

pub fn walk_install_dir(
    root: &Path,
    exe_dir: Option<&Path>,
    max_entries: usize,
    budget: Duration,
) -> Option<String> {
    if !root.is_dir() {
        return Some(UNREADABLE.to_owned());
    }
    let started = Instant::now();
    let mut entries_seen = 0usize;
    let mut found: BTreeSet<String> = BTreeSet::new();
    let mut unreadable = false;
    let mut timed_out = false;
    let mut visited_links: HashSet<PathBuf> = HashSet::new();
    let mut stack: Vec<(PathBuf, usize)> = vec![(root.to_path_buf(), 0)];

    while let Some((dir, depth)) = stack.pop() {
        if depth > MAX_WALK_DEPTH {
            unreadable = true;
            continue;
        }
        let at_root_or_exe = dir == root || exe_dir.is_some_and(|e| e == dir);
        if at_root_or_exe {
            for &(pat, ac, _) in MARKERS {
                if pat.contains('\\') && dir.join(pat.replace('\\', "/")).exists() {
                    found.insert(ac.to_owned());
                }
            }
        }
        let Ok(read_dir) = fs::read_dir(&dir) else {
            unreadable = true;
            continue;
        };
        for item in read_dir {
            entries_seen += 1;
            if entries_seen > max_entries || started.elapsed() > budget {
                timed_out = true;
                break;
            }
            let Ok(entry) = item else {
                unreadable = true;
                continue;
            };
            let name = entry.file_name();
            let Some(name_str) = name.to_str() else {
                continue;
            };
            let entry_path = entry.path();
            if let Some((_, ac, rule)) = marker_for(name_str) {
                let matched = match rule {
                    MarkerRule::Module | MarkerRule::Program => true,
                    MarkerRule::RootOrExe => at_root_or_exe,
                    MarkerRule::Stub => {
                        let folder = if ac == BE {
                            "BattlEye"
                        } else {
                            "EasyAntiCheat"
                        };
                        let suffix_len = if ac == BE { 7 } else { 8 };
                        let game_exe = name_str
                            .len()
                            .checked_sub(suffix_len)
                            .map(|n| format!("{}.exe", &name_str[..n]));
                        dir.join(folder).is_dir()
                            || game_exe.is_some_and(|exe| dir.join(exe).is_file())
                    }
                };
                if matched {
                    found.insert(ac.to_owned());
                }
            }
            let Ok(ft) = entry.file_type() else {
                continue;
            };
            if ft.is_dir() {
                if for_folder(name_str).is_some()
                    || NO_MARKER_FOLDERS
                        .iter()
                        .any(|s| s.eq_ignore_ascii_case(name_str))
                {
                    continue;
                }
                stack.push((entry_path, depth + 1));
            } else if ft.is_symlink() {
                match fs::canonicalize(&entry_path) {
                    Ok(target) if target.is_dir() => {
                        if !target.starts_with(root) && visited_links.insert(target.clone()) {
                            stack.push((target, depth + 1));
                        }
                    }
                    Ok(_) => {}
                    Err(_) => {
                        unreadable = true;
                    }
                }
            }
        }
        if timed_out {
            break;
        }
    }

    if timed_out {
        found.insert(OUT_OF_TIME.to_owned());
    } else if unreadable {
        found.insert(UNREADABLE.to_owned());
    }
    if found.is_empty() {
        None
    } else {
        let list: Vec<String> = found.into_iter().collect();
        Some(join_names(&list))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn game(id: &str, title: &str, store: StoreKind) -> GameDescriptor {
        GameDescriptor {
            id: id.into(),
            title: title.into(),
            store,
            path: None,
            launch_target: None,
        }
    }

    #[test]
    fn enforces_four_class_anti_cheat_policy_and_riot_hard_stop() {
        let val = game("riot:valorant", "VALORANT", StoreKind::Riot);
        let ans = decide(
            for_store(val.store),
            None,
            false,
            None,
            None,
            SteamWord::NotSteam,
            None,
            false,
        );
        assert_eq!(ans.kind, AntiCheatKind::Found);
        assert_eq!(classify(&val, &ans), AntiCheatClass::CompetitivePvpOnline);
        assert!(!classify(&val, &ans).allows_exo_flow());
        assert_eq!(
            dll_policy_for(classify(&val, &ans), &ans),
            DllModPolicy::Disallowed
        );
        assert!(!allows(&val, &ans, Change::GameSettingsFile, None));
        assert!(!allows(
            &val,
            &ans,
            Change::UpscalerSwap,
            ans.label().as_deref()
        ));

        let wuwa = game("steam:3513350", "Wuthering Waves", StoreKind::Steam);
        let wuwa_ans = decide(
            None,
            None,
            true,
            Some("Anti-Cheat Expert"),
            None,
            SteamWord::None,
            None,
            false,
        );
        assert_eq!(
            classify(&wuwa, &wuwa_ans),
            AntiCheatClass::OnlineNonCompetitivePve
        );
        assert!(classify(&wuwa, &wuwa_ans).allows_exo_flow());
        assert_eq!(
            dll_policy_for(classify(&wuwa, &wuwa_ans), &wuwa_ans),
            DllModPolicy::OptInWithBanRiskConfirmation
        );
        assert!(allows(&wuwa, &wuwa_ans, Change::GameSettingsFile, None));
        assert!(!allows(&wuwa, &wuwa_ans, Change::FrameGenMod, None));
        assert!(!allows(&wuwa, &wuwa_ans, Change::UpscalerSwap, None));
        assert!(allows(
            &wuwa,
            &wuwa_ans,
            Change::UpscalerSwap,
            Some("Anti-Cheat Expert")
        ));

        let elden = game("steam:1245620", "ELDEN RING", StoreKind::Steam);
        let elden_ans = decide(
            None,
            None,
            true,
            Some("Easy Anti-Cheat"),
            None,
            SteamWord::None,
            None,
            false,
        );
        assert_eq!(
            classify(&elden, &elden_ans),
            AntiCheatClass::OfflineAntiCheat
        );
        assert!(classify(&elden, &elden_ans).allows_exo_flow());
        assert_eq!(
            dll_policy_for(classify(&elden, &elden_ans), &elden_ans),
            DllModPolicy::AllowedWithOfflineNote
        );

        let unknown_game = game("steam:999999", "Unknown Multiplayer", StoreKind::Steam);
        let unknown_ans = Answer {
            kind: AntiCheatKind::Unknown,
            names: Vec::new(),
            evidence: UNREADABLE.into(),
        };
        assert_eq!(
            classify(&unknown_game, &unknown_ans),
            AntiCheatClass::Unknown
        );
        assert_eq!(
            classify(&unknown_game, &unknown_ans).effective(),
            AntiCheatClass::CompetitivePvpOnline
        );
        assert!(!classify(&unknown_game, &unknown_ans).allows_exo_flow());
        assert_eq!(
            dll_policy_for(classify(&unknown_game, &unknown_ans), &unknown_ans),
            DllModPolicy::Disallowed
        );
        assert!(!allows(
            &unknown_game,
            &unknown_ans,
            Change::GameSettingsFile,
            None
        ));
    }

    #[test]
    fn refused_arguments_and_whole_word_title_matching_work() {
        assert_eq!(
            refused_argument("-fullscreen -noeac"),
            Some("-noeac".into())
        );
        assert_eq!(refused_argument("--eac=off"), Some("--eac=off".into()));
        assert_eq!(
            refused_argument("--load myhook.dll"),
            Some("myhook.dll".into())
        );
        assert_eq!(refused_argument("-dx12 -high"), None);

        assert_eq!(by_title("Rust"), Some("Easy Anti-Cheat"));
        assert_eq!(by_title("Rusty Lake Paradise"), None);
        assert_eq!(by_title("Counter-Strike 2"), Some("Valve Anti-Cheat"));
    }

    #[test]
    fn walk_install_dir_detects_markers_and_skips_no_marker_folders() {
        let dir = std::env::temp_dir().join(format!("exo-policy-walk-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(dir.join("EasyAntiCheat")).unwrap();
        fs::create_dir_all(dir.join("Paks")).unwrap();
        fs::write(dir.join("Paks").join("BEClient.dll"), b"ignored").unwrap();

        let res = walk_install_dir(&dir, Some(&dir), 1000, Duration::from_secs(5));
        assert_eq!(res.as_deref(), Some("Easy Anti-Cheat"));

        let _ = fs::remove_dir_all(&dir);
    }
}
