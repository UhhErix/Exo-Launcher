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
}
