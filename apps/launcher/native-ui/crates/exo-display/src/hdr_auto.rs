use crate::edid::ParsedEdid;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DisplayAutoAction {
    pub enable_windows_hdr: bool,
    pub apply_mhc2_gamma_22: bool,
    pub apply_srgb_clamp: bool,
    pub recommend_vrr_flicker_cap: bool,
}

pub fn plan_display_for_game(edid: &ParsedEdid, game_verified_hdr: bool) -> DisplayAutoAction {
    let display_hdr = edid.hdr_metadata.is_some_and(|h| h.supports_pq);
    let enable_hdr = game_verified_hdr && display_hdr;
    let recommend_vrr_flicker_cap =
        edid.likely_oled && edid.vrr_range.is_some_and(|r| r.max_hz >= 120);
    DisplayAutoAction {
        enable_windows_hdr: enable_hdr,
        apply_mhc2_gamma_22: enable_hdr,
        apply_srgb_clamp: !enable_hdr,
        recommend_vrr_flicker_cap,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::edid::{parse_edid, synthetic_oled_edid_fixture};

    #[test]
    fn plans_hdr_true_black_for_hdr_game_and_srgb_clamp_for_sdr() {
        let edid = parse_edid(&synthetic_oled_edid_fixture(false)).unwrap();
        let hdr_plan = plan_display_for_game(&edid, true);
        assert!(hdr_plan.enable_windows_hdr);
        assert!(hdr_plan.apply_mhc2_gamma_22);
        assert!(!hdr_plan.apply_srgb_clamp);
        assert!(hdr_plan.recommend_vrr_flicker_cap);

        let sdr_plan = plan_display_for_game(&edid, false);
        assert!(!sdr_plan.enable_windows_hdr);
        assert!(sdr_plan.apply_srgb_clamp);
    }
}
