use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fmt;
use std::path::{Path, PathBuf};

pub const FIXED_LOOPBACK_PORTS: &[u16] = &[47823, 47824, 47825];
pub const DEFAULT_TOKEN_PROXY_BASE: &str = "https://auth.getexo.app";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LoopbackRedirectMode {
    DynamicPort {
        host: &'static str,
    },
    FixedPorts {
        host: &'static str,
        ports: &'static [u16],
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum OAuthProvider {
    Spotify,
    Discord,
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
        OAuthProvider::Discord,
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
            Self::Discord => "discord",
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
            Self::Discord => "Discord",
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
            Self::Discord => "EXO_OAUTH_DISCORD_CLIENT_ID",
            Self::Google => "EXO_OAUTH_GOOGLE_CLIENT_ID",
            Self::Twitch => "EXO_OAUTH_TWITCH_CLIENT_ID",
            Self::BattleNet => "EXO_OAUTH_BATTLENET_CLIENT_ID",
            Self::Microsoft => "EXO_OAUTH_MICROSOFT_CLIENT_ID",
            Self::Riot => "EXO_OAUTH_RIOT_CLIENT_ID",
            Self::Epic => "EXO_OAUTH_EPIC_CLIENT_ID",
        }
    }

    pub fn baked_client_id(self) -> Option<&'static str> {
        let raw = match self {
            Self::Spotify => option_env!("EXO_OAUTH_SPOTIFY_CLIENT_ID"),
            Self::Discord => option_env!("EXO_OAUTH_DISCORD_CLIENT_ID"),
            Self::Google => option_env!("EXO_OAUTH_GOOGLE_CLIENT_ID"),
            Self::Twitch => option_env!("EXO_OAUTH_TWITCH_CLIENT_ID"),
            Self::BattleNet => option_env!("EXO_OAUTH_BATTLENET_CLIENT_ID"),
            Self::Microsoft => option_env!("EXO_OAUTH_MICROSOFT_CLIENT_ID"),
            Self::Riot => option_env!("EXO_OAUTH_RIOT_CLIENT_ID"),
            Self::Epic => option_env!("EXO_OAUTH_EPIC_CLIENT_ID"),
        };
        raw.map(str::trim)
            .filter(|s| is_valid_official_client_id(s))
    }

    pub fn flow(self) -> &'static str {
        match self {
            Self::Spotify
            | Self::Discord
            | Self::Google
            | Self::BattleNet
            | Self::Riot
            | Self::Epic => "pkce-system-browser",
            Self::Microsoft => "wam",
            Self::Twitch => "device-code",
        }
    }

    pub fn authorization_endpoint(self) -> Option<&'static str> {
        match self {
            Self::Spotify => Some("https://accounts.spotify.com/authorize"),
            Self::Discord => Some("https://discord.com/oauth2/authorize"),
            Self::Google => Some("https://accounts.google.com/o/oauth2/v2/auth"),
            Self::Twitch => None,
            Self::BattleNet => Some("https://oauth.battle.net/authorize"),
            Self::Microsoft => {
                Some("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize")
            }
            Self::Riot => Some("https://auth.riotgames.com/authorize"),
            Self::Epic => Some("https://www.epicgames.com/id/authorize"),
        }
    }

    pub fn token_endpoint(self) -> &'static str {
        match self {
            Self::Spotify => "https://accounts.spotify.com/api/token",
            Self::Discord => "https://discord.com/api/oauth2/token",
            Self::Google => "https://oauth2.googleapis.com/token",
            Self::Twitch => "https://id.twitch.tv/oauth2/token",
            Self::BattleNet => "https://oauth.battle.net/token",
            Self::Microsoft => "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            Self::Riot => "https://auth.riotgames.com/token",
            Self::Epic => "https://api.epicgames.dev/epic/oauth/v2/token",
        }
    }

    pub fn device_authorization_endpoint(self) -> Option<&'static str> {
        match self {
            Self::Twitch => Some("https://id.twitch.tv/oauth2/device"),
            Self::Microsoft => {
                Some("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode")
            }
            _ => None,
        }
    }

    pub fn loopback_redirect_mode(self) -> Option<LoopbackRedirectMode> {
        match self {
            Self::Spotify | Self::Google => {
                Some(LoopbackRedirectMode::DynamicPort { host: "127.0.0.1" })
            }
            Self::Microsoft => Some(LoopbackRedirectMode::DynamicPort { host: "localhost" }),
            Self::Discord | Self::BattleNet | Self::Riot | Self::Epic => {
                Some(LoopbackRedirectMode::FixedPorts {
                    host: "127.0.0.1",
                    ports: FIXED_LOOPBACK_PORTS,
                })
            }
            Self::Twitch => None,
        }
    }

    pub fn registered_redirect_uris(self) -> Vec<String> {
        match self {
            Self::Spotify => vec!["http://127.0.0.1/callback".to_string()],
            Self::Google | Self::Twitch => Vec::new(),
            Self::Microsoft => vec!["http://localhost".to_string()],
            Self::Discord | Self::BattleNet | Self::Riot | Self::Epic => FIXED_LOOPBACK_PORTS
                .iter()
                .map(|p| format!("http://127.0.0.1:{p}/callback"))
                .collect(),
        }
    }

    pub fn requires_token_exchange_proxy(self) -> bool {
        matches!(
            self,
            Self::Google | Self::BattleNet | Self::Riot | Self::Epic
        )
    }

    pub fn default_scopes(self) -> &'static str {
        match self {
            Self::Spotify => {
                "user-read-playback-state user-modify-playback-state user-library-read"
            }
            Self::Discord => "identify",
            Self::Google => "openid profile",
            Self::Twitch => "user:read:email",
            Self::BattleNet => "openid",
            Self::Microsoft => "XboxLive.signin offline_access",
            Self::Riot => "openid offline_access",
            Self::Epic => "basic_profile openid",
        }
    }

    pub fn from_id(id: &str) -> Option<Self> {
        match id.trim().to_ascii_lowercase().as_str() {
            "spotify" => Some(Self::Spotify),
            "discord" => Some(Self::Discord),
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
    token_proxy_base: Option<String>,
}

#[derive(Deserialize)]
struct ClientConfigFile {
    #[serde(default)]
    providers: BTreeMap<String, String>,
    #[serde(default, alias = "token_proxy_base", rename = "tokenProxyBase")]
    token_proxy_base: Option<String>,
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
        cfg.merge_baked();
        if let Some(path) = Self::default_config_path() {
            cfg.merge_json_file(&path);
        }
        cfg.merge_env();
        cfg
    }

    pub fn from_layers(
        baked: &[(OAuthProvider, &str)],
        json: Option<&str>,
        env_overrides: &[(OAuthProvider, &str)],
    ) -> Result<Self, AuthError> {
        let mut cfg = Self::default();
        for &(provider, value) in baked {
            cfg.set_client_id(provider, value);
        }
        if let Some(raw) = json {
            cfg.merge_json_str(raw)?;
        }
        for &(provider, value) in env_overrides {
            cfg.set_client_id(provider, value);
        }
        Ok(cfg)
    }

    pub fn from_json_str(raw: &str) -> Result<Self, AuthError> {
        let mut cfg = Self::default();
        cfg.merge_json_str(raw)?;
        Ok(cfg)
    }

    pub fn merge_baked(&mut self) {
        for &provider in OAuthProvider::ALL {
            if let Some(id) = provider.baked_client_id() {
                self.set_client_id(provider, id);
            }
        }
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
        if let Ok(wrapped) = serde_json::from_str::<ClientConfigFile>(trimmed)
            && (!wrapped.providers.is_empty() || wrapped.token_proxy_base.is_some())
        {
            if let Some(base) = wrapped.token_proxy_base {
                self.set_token_proxy_base(base);
            }
            for (key, value) in wrapped.providers {
                if let Some(provider) = OAuthProvider::from_id(&key) {
                    self.set_client_id(provider, value);
                }
            }
            return Ok(());
        }
        let entries = serde_json::from_str::<BTreeMap<String, String>>(trimmed)
            .map_err(|e| AuthError::Protocol(format!("invalid oauth-clients.json: {e}")))?;
        for (key, value) in entries {
            if key == "tokenProxyBase" || key == "token_proxy_base" {
                self.set_token_proxy_base(value);
            } else if let Some(provider) = OAuthProvider::from_id(&key) {
                self.set_client_id(provider, value);
            }
        }
        Ok(())
    }

    pub fn set_token_proxy_base(&mut self, value: impl Into<String>) {
        let candidate = value.into();
        let trimmed = candidate.trim().trim_end_matches('/');
        if is_valid_token_proxy_base(trimmed) {
            self.token_proxy_base = Some(trimmed.to_string());
        }
    }

    pub fn token_proxy_base(&self) -> &str {
        self.token_proxy_base
            .as_deref()
            .unwrap_or(DEFAULT_TOKEN_PROXY_BASE)
    }

    pub fn token_endpoint_for(&self, provider: OAuthProvider) -> String {
        if provider.requires_token_exchange_proxy() {
            format!(
                "{}/oauth/{}/token",
                self.token_proxy_base().trim_end_matches('/'),
                provider.id()
            )
        } else {
            provider.token_endpoint().to_string()
        }
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
        let note = if !available {
            unavailable_note(provider)
        } else if provider.requires_token_exchange_proxy() {
            format!(
                "Client ID configured for {}; confidential /token exchange requires cloud token proxy.",
                flow_label(provider.flow())
            )
        } else {
            format!("Ready via {}.", flow_label(provider.flow()))
        };
        ProviderAuthStatus {
            provider: provider.id().to_string(),
            display_name: provider.display_name().to_string(),
            flow: provider.flow().to_string(),
            available,
            note,
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

pub fn is_valid_token_proxy_base(candidate: &str) -> bool {
    let trimmed = candidate.trim();
    if trimmed.is_empty() || trimmed.len() > 256 || trimmed.chars().any(char::is_whitespace) {
        return false;
    }
    trimmed.starts_with("https://") || trimmed.starts_with("http://127.0.0.1:")
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
