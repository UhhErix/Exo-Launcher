use serde::{Deserialize, Serialize};

pub const DSCP_EXPEDITED_FORWARDING: u8 = 46;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QosPolicySpec {
    pub policy_name: String,
    pub exe_name: String,
    pub dscp_value: u8,
}

pub fn build_game_qos_policy(game_id: &str, exe_name: &str) -> Result<QosPolicySpec, String> {
    let trimmed = exe_name.trim();
    if trimmed.is_empty()
        || !trimmed.to_ascii_lowercase().ends_with(".exe")
        || trimmed.contains('\\')
        || trimmed.contains('/')
    {
        return Err("QoS policy requires a plain executable filename (*.exe)".into());
    }
    Ok(QosPolicySpec {
        policy_name: format!("Exo-QoS-{game_id}"),
        exe_name: trimmed.to_string(),
        dscp_value: DSCP_EXPEDITED_FORWARDING,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builds_dscp_46_policy_and_rejects_paths() {
        let p = build_game_qos_policy("cs2", "cs2.exe").unwrap();
        assert_eq!(p.dscp_value, 46);
        assert!(build_game_qos_policy("bad", r"C:\games\cs2.exe").is_err());
    }
}
