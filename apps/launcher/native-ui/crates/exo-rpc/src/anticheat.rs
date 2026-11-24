use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AntiCheatClass {
    #[serde(alias = "competitivePvP")]
    CompetitivePvpOnline,
    #[serde(alias = "onlinePvE")]
    OnlineNonCompetitivePve,
    #[serde(alias = "offline", alias = "noAntiCheat")]
    OfflineAntiCheat,
    #[default]
    #[serde(other)]
    Unknown,
}

impl AntiCheatClass {
    pub const fn effective(self) -> Self {
        match self {
            Self::Unknown => Self::CompetitivePvpOnline,
            other => other,
        }
    }

    pub const fn strictness(self) -> u8 {
        match self {
            Self::OfflineAntiCheat => 0,
            Self::OnlineNonCompetitivePve => 1,
            Self::Unknown => 2,
            Self::CompetitivePvpOnline => 3,
        }
    }

    pub const fn strictest(self, other: Self) -> Self {
        if other.strictness() > self.strictness() {
            other
        } else {
            self
        }
    }

    pub const fn stricter_of(self, other: Self) -> Self {
        self.strictest(other)
    }

    pub const fn allows_software_macros(self) -> bool {
        matches!(self.effective(), Self::OfflineAntiCheat)
    }

    pub const fn allows_exo_flow(self) -> bool {
        matches!(
            self.effective(),
            Self::OnlineNonCompetitivePve | Self::OfflineAntiCheat
        )
    }

    pub const fn forbids_game_tree_set_rights(self) -> bool {
        matches!(self, Self::CompetitivePvpOnline | Self::Unknown)
    }

    pub const fn allows_driver_drs_override(self) -> bool {
        true
    }

    pub const fn allows_windows_audio_apo(self) -> bool {
        true
    }

    pub const fn forbids_audio_process_hooking(self) -> bool {
        true
    }

    pub const fn allows_signed_upscaler_swap(self, ban_risk_confirmed: bool) -> bool {
        match self.effective() {
            Self::OfflineAntiCheat => true,
            Self::OnlineNonCompetitivePve => ban_risk_confirmed,
            Self::CompetitivePvpOnline | Self::Unknown => false,
        }
    }
}

pub const ANTI_CHEAT_EXACT_NAMES: &[&str] = &[
    "vgc",
    "vgk",
    "vgtray",
    "vanguard",
    "easyanticheat",
    "easyanticheat_eos",
    "easyanticheat_setup",
    "easyanticheat_eos_setup",
    "easyanticheat_launcher",
    "eac_launcher",
    "start_protected_game",
    "start_protected_game64",
    "beservice",
    "beservice_x64",
    "bedaisy",
    "battleye",
    "battleye_launcher",
    "belauncher",
    "faceit",
    "faceitclient",
    "faceitservice",
    "eseaclient",
    "pnkbstra",
    "pnkbstrb",
    "pbsvc",
    "punkbuster",
    "xigncode",
    "xigncode3",
    "xhunter1",
    "nprotect",
    "gameguard",
    "gameguard.des",
    "gamemon",
    "gamemon64",
    "gamemon.des",
    "gamemon64.des",
    "npggsvc",
    "npggnt",
    "npggnt64",
    "sguard64",
    "sguardsvc64",
    "ace-tray",
    "ace-base",
    "ace-guard",
    "ace-service",
    "tencent ace",
    "tencentace",
    "tesafe",
    "equ8",
    "equ8_anticheat",
    "equ8_helper",
    "mhyprot",
    "mhyprot2",
    "mhyprot3",
    "mhyprotect",
    "hoyokprotect",
    "javelin",
    "zakynthos",
    "tvk",
    "hackshield",
    "ehsvc",
    "hsupdate",
    "ricochet",
    "randgrid",
    "blackcipher",
    "blackcipher64",
    "nexongamesecurity",
    "wellbia",
    "anticheat",
];

pub const ANTI_CHEAT_PREFIXES: &[&str] = &[
    "eaanticheat.",
    "eaanticheat",
    "easyanticheat",
    "battleye",
    "faceit",
    "pnkbstr",
    "xhunter",
    "sguard",
    "equ8",
    "ace-tray",
    "ace-base",
    "ace-guard",
    "mhyprot",
    "javelin",
    "vanguard",
    "nprotect",
    "gameguard",
    "zakynthos",
    "hackshield",
];

pub const ANTI_CHEAT_SUFFIXES: &[&str] = &["_be", "_eac", "_eac_eos"];

pub const ANTI_CHEAT_SUBSTRINGS: &[&str] =
    &["anticheat", "anti-cheat", "anti_cheat", "tencent ace"];

pub fn normalize_process_name(raw: &str) -> String {
    let trimmed = raw.trim();
    let tail = trimmed
        .rsplit_once(['\\', '/'])
        .map(|(_, f)| f)
        .unwrap_or(trimmed);
    let lower = tail.to_ascii_lowercase();
    lower
        .strip_suffix(".exe")
        .or_else(|| lower.strip_suffix(".sys"))
        .unwrap_or(&lower)
        .to_string()
}

pub fn is_anti_cheat_process(name: &str) -> bool {
    let bare = normalize_process_name(name);
    if bare.is_empty() {
        return false;
    }
    ANTI_CHEAT_EXACT_NAMES.contains(&bare.as_str())
        || ANTI_CHEAT_PREFIXES.iter().any(|p| bare.starts_with(p))
        || ANTI_CHEAT_SUFFIXES.iter().any(|s| bare.ends_with(s))
        || ANTI_CHEAT_SUBSTRINGS.iter().any(|sub| bare.contains(sub))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::Game;

    #[test]
    fn wire_values_round_trip_and_missing_or_unknown_maps_to_unknown() {
        let cases = [
            (
                AntiCheatClass::CompetitivePvpOnline,
                "\"competitivePvpOnline\"",
            ),
            (
                AntiCheatClass::OnlineNonCompetitivePve,
                "\"onlineNonCompetitivePve\"",
            ),
            (AntiCheatClass::OfflineAntiCheat, "\"offlineAntiCheat\""),
            (AntiCheatClass::Unknown, "\"unknown\""),
        ];
        for (variant, wire) in cases {
            let encoded = serde_json::to_string(&variant).expect("serialize");
            assert_eq!(encoded, wire);
            let decoded: AntiCheatClass = serde_json::from_str(&encoded).expect("deserialize");
            assert_eq!(decoded, variant);
        }

        let future: AntiCheatClass = serde_json::from_str("\"futureMode\"").expect("fallback");
        assert_eq!(future, AntiCheatClass::Unknown);
        assert_eq!(future.effective(), AntiCheatClass::CompetitivePvpOnline);

        let game_missing: Game = serde_json::from_str(r#"{"id":"steam:620","title":"Portal 2"}"#)
            .expect("missing field");
        assert_eq!(game_missing.anti_cheat_class, AntiCheatClass::Unknown);
        assert_eq!(
            game_missing.anti_cheat_class.effective(),
            AntiCheatClass::CompetitivePvpOnline
        );

        let game_offline: Game = serde_json::from_str(
            r#"{"id":"steam:1245620","title":"ELDEN RING","antiCheatClass":"offlineAntiCheat"}"#,
        )
        .expect("offline field");
        assert_eq!(
            game_offline.anti_cheat_class,
            AntiCheatClass::OfflineAntiCheat
        );

        let game_pve: Game = serde_json::from_str(
            r#"{"id":"steam:3513350","title":"Wuthering Waves","antiCheatClass":"onlineNonCompetitivePve"}"#,
        )
        .expect("pve field");
        assert_eq!(
            game_pve.anti_cheat_class,
            AntiCheatClass::OnlineNonCompetitivePve
        );
        assert!(!game_pve.anti_cheat_class.allows_software_macros());

        let round_trip = serde_json::to_string(&game_pve).expect("re-serialize game");
        assert!(round_trip.contains("\"antiCheatClass\":\"onlineNonCompetitivePve\""));
    }

    #[test]
    fn local_hints_can_only_tighten_class() {
        assert_eq!(
            AntiCheatClass::OfflineAntiCheat.strictest(AntiCheatClass::CompetitivePvpOnline),
            AntiCheatClass::CompetitivePvpOnline
        );
        assert_eq!(
            AntiCheatClass::CompetitivePvpOnline.strictest(AntiCheatClass::OfflineAntiCheat),
            AntiCheatClass::CompetitivePvpOnline
        );
        assert_eq!(
            AntiCheatClass::OnlineNonCompetitivePve.strictest(AntiCheatClass::OfflineAntiCheat),
            AntiCheatClass::OnlineNonCompetitivePve
        );
        assert_eq!(
            AntiCheatClass::Unknown.strictest(AntiCheatClass::OfflineAntiCheat),
            AntiCheatClass::Unknown
        );
    }

    #[test]
    fn exo_flow_follows_shared_anti_cheat_class() {
        assert!(!AntiCheatClass::CompetitivePvpOnline.allows_exo_flow());
        assert!(!AntiCheatClass::Unknown.allows_exo_flow());
        assert!(AntiCheatClass::OnlineNonCompetitivePve.allows_exo_flow());
        assert!(AntiCheatClass::OfflineAntiCheat.allows_exo_flow());
    }

    #[test]
    fn shared_anti_cheat_process_list_covers_required_engines() {
        let protected = [
            "vgc.exe",
            "vgk.sys",
            "vanguard.exe",
            "EasyAntiCheat_EOS.exe",
            "BEService_x64.exe",
            "FortniteClient-Win64-Shipping_BE.exe",
            "equ8.exe",
            "equ8_helper.exe",
            "ACE-Tray.exe",
            "ACE-Base.exe",
            "ACE-Guard.exe",
            "Tencent ACE.exe",
            "SGuard64.exe",
            "SGuardSvc64.exe",
            "mhyprot2.exe",
            "mhyprot3.exe",
            "javelin.exe",
            "EAAntiCheat.GameServiceLauncher.exe",
            "nProtect.exe",
            "GameGuard.des",
            "GameMon64.des",
            "zakynthos.exe",
            "tvk.exe",
            "HackShield.exe",
            "CustomVendorAntiCheatService.exe",
            "C:\\Program Files\\Vendor\\foo_anticheat_helper.exe",
        ];
        for name in protected {
            assert!(
                is_anti_cheat_process(name),
                "expected anti-cheat match for {name}"
            );
        }

        let normal = ["chrome.exe", "spotify.exe", "Sekiro.exe", "blender.exe", ""];
        for name in normal {
            assert!(
                !is_anti_cheat_process(name),
                "unexpected anti-cheat match for {name}"
            );
        }

        assert!(AntiCheatClass::CompetitivePvpOnline.forbids_game_tree_set_rights());
        assert!(AntiCheatClass::Unknown.forbids_game_tree_set_rights());
        assert!(!AntiCheatClass::OfflineAntiCheat.forbids_game_tree_set_rights());

        for variant in [
            AntiCheatClass::CompetitivePvpOnline,
            AntiCheatClass::OnlineNonCompetitivePve,
            AntiCheatClass::OfflineAntiCheat,
            AntiCheatClass::Unknown,
        ] {
            assert!(variant.allows_windows_audio_apo());
            assert!(variant.forbids_audio_process_hooking());
        }
    }
}
