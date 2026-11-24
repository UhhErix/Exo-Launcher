use serde::{Deserialize, Serialize};

use crate::dpapi::TokenVault;

pub const GOG_AUTH_HOST: &str = "auth.gog.com";
pub const GOG_LIBRARY_HOST: &str = "galaxy-library.gog.com";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct GogOAuthSession {
    pub user_id: String,
    pub access_token: String,
    pub refresh_token: String,
    pub expires_at_unix: u64,
}

impl GogOAuthSession {
    pub fn needs_speculative_refresh(&self, now_unix: u64, lead_secs: u64) -> bool {
        now_unix.saturating_add(lead_secs) >= self.expires_at_unix
    }

    pub fn seal_to_vault<V: TokenVault>(&self, vault: &mut V) -> Result<Vec<u8>, String> {
        let json = serde_json::to_vec(self).map_err(|e| e.to_string())?;
        vault.seal(&format!("exo-gog-{}", self.user_id), &json)
    }

    pub fn unseal_from_vault<V: TokenVault>(
        vault: &V,
        user_id: &str,
        sealed: &[u8],
    ) -> Result<Self, String> {
        let plain = vault.unseal(&format!("exo-gog-{user_id}"), sealed)?;
        serde_json::from_slice(&plain).map_err(|e| e.to_string())
    }
}
