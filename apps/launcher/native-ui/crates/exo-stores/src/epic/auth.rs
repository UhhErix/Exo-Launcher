use serde::{Deserialize, Serialize};

use crate::dpapi::TokenVault;

pub const EPIC_OAUTH_HOST: &str = "account-public-service-prod.ol.epicgames.com";
pub const EPIC_EXCHANGE_URL: &str =
    "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/exchange";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct EpicOAuthSession {
    pub account_id: String,
    pub display_name: String,
    pub access_token: String,
    pub refresh_token: String,
    pub expires_at_unix: u64,
}

impl EpicOAuthSession {
    pub fn needs_speculative_refresh(&self, now_unix: u64, lead_secs: u64) -> bool {
        now_unix.saturating_add(lead_secs) >= self.expires_at_unix
    }

    pub fn seal_to_vault<V: TokenVault>(&self, vault: &mut V) -> Result<Vec<u8>, String> {
        let json = serde_json::to_vec(self).map_err(|e| e.to_string())?;
        vault.seal(&format!("exo-epic-{}", self.account_id), &json)
    }

    pub fn unseal_from_vault<V: TokenVault>(
        vault: &V,
        account_id: &str,
        sealed: &[u8],
    ) -> Result<Self, String> {
        let plain = vault.unseal(&format!("exo-epic-{account_id}"), sealed)?;
        serde_json::from_slice(&plain).map_err(|e| e.to_string())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EpicExchangeLaunchArgs {
    pub executable: String,
    pub args: Vec<String>,
}

pub fn build_official_launch_args(
    executable: &str,
    app_name: &str,
    catalog_namespace: &str,
    catalog_item_id: &str,
    account_id: &str,
    exchange_code: &str,
) -> Result<EpicExchangeLaunchArgs, String> {
    if exchange_code.trim().is_empty() {
        return Err("Official Epic launch requires a valid non-empty exchange code".into());
    }
    if executable.trim().is_empty() {
        return Err("Launch executable is empty".into());
    }
    Ok(EpicExchangeLaunchArgs {
        executable: executable.to_string(),
        args: vec![
            "-AUTH_LOGIN=unused".into(),
            format!("-AUTH_PASSWORD={exchange_code}"),
            "-AUTH_TYPE=exchangecode".into(),
            format!("-epicapp={app_name}"),
            "-epicenv=Prod".into(),
            "-EpicPortal".into(),
            format!("-epicuserid={account_id}"),
            format!("-epicusername={account_id}"),
            format!("-epicsandboxid={catalog_namespace}"),
            format!("-epicdeploymentid={catalog_item_id}"),
        ],
    })
}
