use serde::{Deserialize, Serialize};

use crate::dpapi::TokenVault;

pub const AMAZON_API_HOST: &str = "api.amazon.com";
pub const AMAZON_GAMING_HOST: &str = "gaming.amazon.com";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AmazonDeviceSession {
    pub device_serial: String,
    pub access_token: String,
    pub refresh_token: String,
    pub expires_at_unix: u64,
}

impl AmazonDeviceSession {
    pub fn needs_speculative_refresh(&self, now_unix: u64, lead_secs: u64) -> bool {
        now_unix.saturating_add(lead_secs) >= self.expires_at_unix
    }

    pub fn seal_to_vault<V: TokenVault>(&self, vault: &mut V) -> Result<Vec<u8>, String> {
        let json = serde_json::to_vec(self).map_err(|e| e.to_string())?;
        vault.seal(&format!("exo-amazon-{}", self.device_serial), &json)
    }

    pub fn unseal_from_vault<V: TokenVault>(
        vault: &V,
        device_serial: &str,
        sealed: &[u8],
    ) -> Result<Self, String> {
        let plain = vault.unseal(&format!("exo-amazon-{device_serial}"), sealed)?;
        serde_json::from_slice(&plain).map_err(|e| e.to_string())
    }
}
