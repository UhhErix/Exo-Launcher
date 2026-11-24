use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fmt;
use std::path::{Path, PathBuf};

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum OAuthProvider {
    Spotify,
    Google,
    Twitch,
    BattleNet,
    Microsoft,
    Riot,
    Epic,
}

impl OAuthProvider {
    pub const ALL: &'static [OAuthProvider] = &[
        OAuthProvider::Spotify,
        OAuthProvider::Google,
        OAuthProvider::Twitch,
        OAuthProvider::BattleNet,
        OAuthProvider::Microsoft,
        OAuthProvider::Riot,
        OAuthProvider::Epic,
    ];

    pub fn id(self) -> &'static str {
        match self {
            Self::Spotify => "spotify",
            Self::Google => "google",
            Self::Twitch => "twitch",
            Self::BattleNet => "battlenet",
            Self::Microsoft => "microsoft",
            Self::Riot => "riot",
            Self::Epic => "epic",
        }
    }

    pub fn display_name(self) -> &'static str {
        match self {
            Self::Spotify => "Spotify",
            Self::Google => "Google / YouTube Music",
            Self::Twitch => "Twitch",
            Self::BattleNet => "Battle.net",
            Self::Microsoft => "Xbox / Microsoft",
            Self::Riot => "Riot Games",
            Self::Epic => "Epic Games",
        }
    }

    pub fn env_var(self) -> &'static str {
        match self {
            Self::Spotify => "EXO_OAUTH_SPOTIFY_CLIENT_ID",
            Self::Google => "EXO_OAUTH_GOOGLE_CLIENT_ID",
            Self::Twitch => "EXO_OAUTH_TWITCH_CLIENT_ID",
            Self::BattleNet => "EXO_OAUTH_BATTLENET_CLIENT_ID",
            Self::Microsoft => "EXO_OAUTH_MICROSOFT_CLIENT_ID",
            Self::Riot => "EXO_OAUTH_RIOT_CLIENT_ID",
            Self::Epic => "EXO_OAUTH_EPIC_CLIENT_ID",
        }
    }

    pub fn flow(self) -> &'static str {
        match self {
            Self::Spotify | Self::Google | Self::Twitch | Self::BattleNet | Self::Riot => {
                "pkce-system-browser"
            }
            Self::Microsoft => "wam",
            Self::Epic => "device-code",
        }
    }

    pub fn authorization_endpoint(self) -> Option<&'static str> {
        match self {
            Self::Spotify => Some("https://accounts.spotify.com/authorize"),
            Self::Google => Some("https://accounts.google.com/o/oauth2/v2/auth"),
            Self::Twitch => Some("https://id.twitch.tv/oauth2/authorize"),
            Self::BattleNet => Some("https://oauth.battle.net/authorize"),
            Self::Microsoft => {
                Some("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize")
            }
            Self::Riot => Some("https://auth.riotgames.com/authorize"),
            Self::Epic => None,
        }
    }

    pub fn token_endpoint(self) -> &'static str {
        match self {
            Self::Spotify => "https://accounts.spotify.com/api/token",
            Self::Google => "https://oauth2.googleapis.com/token",
            Self::Twitch => "https://id.twitch.tv/oauth2/token",
            Self::BattleNet => "https://oauth.battle.net/token",
            Self::Microsoft => "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            Self::Riot => "https://auth.riotgames.com/token",
            Self::Epic => {
                "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token"
            }
        }
    }

    pub fn device_authorization_endpoint(self) -> Option<&'static str> {
        match self {
            Self::Twitch => Some("https://id.twitch.tv/oauth2/device"),
            Self::Microsoft => {
                Some("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode")
            }
            Self::Epic => Some(
                "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/deviceAuthorization",
            ),
            _ => None,
        }
    }

    pub fn default_scopes(self) -> &'static str {
        match self {
            Self::Spotify => {
                "user-read-playback-state user-modify-playback-state user-library-read"
            }
            Self::Google => "openid profile",
            Self::Twitch => "user:read:email",
            Self::BattleNet => "openid",
            Self::Microsoft => "XboxLive.signin offline_access",
            Self::Riot => "openid offline_access",
            Self::Epic => "basic_profile",
        }
    }

    pub fn from_id(id: &str) -> Option<Self> {
        match id.trim().to_ascii_lowercase().as_str() {
            "spotify" => Some(Self::Spotify),
            "google" | "youtube" => Some(Self::Google),
            "twitch" => Some(Self::Twitch),
            "battlenet" | "battle.net" | "bnet" => Some(Self::BattleNet),
            "microsoft" | "xbox" => Some(Self::Microsoft),
            "riot" => Some(Self::Riot),
            "epic" => Some(Self::Epic),
            _ => None,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderAuthStatus {
    pub provider: String,
    pub display_name: String,
    pub flow: String,
    pub available: bool,
    pub note: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum AuthError {
    Unavailable { provider: String, note: String },
    Cancelled,
    Protocol(String),
    Os(String),
}

impl fmt::Display for AuthError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Unavailable { note, .. } => f.write_str(note),
            Self::Cancelled => f.write_str("Sign-in was cancelled."),
            Self::Protocol(msg) | Self::Os(msg) => f.write_str(msg),
        }
    }
}

impl std::error::Error for AuthError {}

#[derive(Clone, Debug, Default)]
pub struct OAuthClientConfig {
    client_ids: BTreeMap<OAuthProvider, String>,
}

#[derive(Deserialize)]
struct ClientConfigFile {
    #[serde(default)]
    providers: BTreeMap<String, String>,
}

impl OAuthClientConfig {
    pub fn empty() -> Self {
        Self::default()
    }

    pub fn default_config_path() -> Option<PathBuf> {
        std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .map(|dir| dir.join("ExoLauncher").join("oauth-clients.json"))
    }

    pub fn load_default() -> Self {
        let mut cfg = Self::default();
        if let Some(path) = Self::default_config_path() {
            cfg.merge_json_file(&path);
        }
        cfg.merge_env();
        cfg
    }

    pub fn from_json_str(raw: &str) -> Result<Self, AuthError> {
        let mut cfg = Self::default();
        cfg.merge_json_str(raw)?;
        Ok(cfg)
    }

    pub fn merge_json_file(&mut self, path: &Path) {
        if let Ok(raw) = std::fs::read_to_string(path) {
            let _ = self.merge_json_str(&raw);
        }
    }

    pub fn merge_json_str(&mut self, raw: &str) -> Result<(), AuthError> {
        let trimmed = raw.trim();
        if trimmed.is_empty() {
            return Ok(());
        }
        let entries = if let Ok(wrapped) = serde_json::from_str::<ClientConfigFile>(trimmed)
            && !wrapped.providers.is_empty()
        {
            wrapped.providers
        } else {
            serde_json::from_str::<BTreeMap<String, String>>(trimmed)
                .map_err(|e| AuthError::Protocol(format!("invalid oauth-clients.json: {e}")))?
        };
        for (key, value) in entries {
            if let Some(provider) = OAuthProvider::from_id(&key) {
                self.set_client_id(provider, value);
            }
        }
        Ok(())
    }

    pub fn merge_env(&mut self) {
        for &provider in OAuthProvider::ALL {
            if let Ok(val) = std::env::var(provider.env_var()) {
                self.set_client_id(provider, val);
            }
        }
    }

    pub fn set_client_id(&mut self, provider: OAuthProvider, value: impl Into<String>) {
        let candidate = value.into();
        let trimmed = candidate.trim();
        if is_valid_official_client_id(trimmed) {
            self.client_ids.insert(provider, trimmed.to_string());
        } else {
            self.client_ids.remove(&provider);
        }
    }

    pub fn client_id(&self, provider: OAuthProvider) -> Option<&str> {
        self.client_ids.get(&provider).map(String::as_str)
    }

    pub fn require_client_id(&self, provider: OAuthProvider) -> Result<&str, AuthError> {
        self.client_id(provider)
            .ok_or_else(|| AuthError::Unavailable {
                provider: provider.id().to_string(),
                note: unavailable_note(provider),
            })
    }

    pub fn status(&self, provider: OAuthProvider) -> ProviderAuthStatus {
        let available = self.client_id(provider).is_some();
        ProviderAuthStatus {
            provider: provider.id().to_string(),
            display_name: provider.display_name().to_string(),
            flow: provider.flow().to_string(),
            available,
            note: if available {
                format!("Ready via {}.", flow_label(provider.flow()))
            } else {
                unavailable_note(provider)
            },
        }
    }

    pub fn all_statuses(&self) -> Vec<ProviderAuthStatus> {
        OAuthProvider::ALL.iter().map(|&p| self.status(p)).collect()
    }
}

fn flow_label(flow: &str) -> &'static str {
    match flow {
        "pkce-system-browser" => "system browser OAuth + PKCE",
        "wam" => "Windows Web Account Manager",
        "device-code" => "device code sign-in",
        _ => "native sign-in",
    }
}

fn unavailable_note(provider: OAuthProvider) -> String {
    format!(
        "Unavailable: official {} OAuth client ID is not registered yet (configure oauth-clients.json or {}).",
        provider.display_name(),
        provider.env_var()
    )
}

pub fn is_valid_official_client_id(candidate: &str) -> bool {
    let trimmed = candidate.trim();
    if trimmed.len() < 8 || trimmed.len() > 256 || trimmed.chars().any(char::is_whitespace) {
        return false;
    }
    let lower = trimmed.to_ascii_lowercase();
    const REJECTED_FRAGMENTS: &[&str] = &[
        "placeholder",
        "changeme",
        "replace_me",
        "replace-me",
        "your_client",
        "your-client",
        "exo_lab",
        "dummy",
        "example",
        "todo",
    ];
    !REJECTED_FRAGMENTS.iter().any(|frag| lower.contains(frag))
}
