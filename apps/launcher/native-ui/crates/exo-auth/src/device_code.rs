use crate::config::{AuthError, OAuthClientConfig, OAuthProvider};
#[cfg(windows)]
use crate::http::{http_request, url_encode};
use crate::pkce::OAuthTokenSet;
#[cfg(windows)]
use crate::pkce::parse_token_response;
use serde::{Deserialize, Serialize};
#[cfg(windows)]
use serde_json::Value;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceCodeChallenge {
    pub provider: OAuthProvider,
    pub user_code: String,
    pub verification_uri: String,
    pub verification_uri_complete: Option<String>,
    #[serde(skip_serializing)]
    pub device_code: String,
    pub expires_in: u64,
    pub interval: u64,
}

#[derive(Clone, Debug)]
pub enum DevicePollOutcome {
    Pending { slow_down: bool },
    Authorized(OAuthTokenSet),
    Denied,
    Expired,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JellyfinQuickConnect {
    pub code: String,
    #[serde(skip_serializing)]
    pub secret: String,
    pub authenticated: bool,
}

#[cfg(windows)]
pub fn start_device_code(
    config: &OAuthClientConfig,
    provider: OAuthProvider,
    scopes: Option<&str>,
) -> Result<DeviceCodeChallenge, AuthError> {
    let client_id = config.require_client_id(provider)?;
    let endpoint = provider.device_authorization_endpoint().ok_or_else(|| {
        AuthError::Protocol(format!(
            "{} does not expose an RFC 8628 device authorization endpoint",
            provider.display_name()
        ))
    })?;
    let scope_str = scopes.unwrap_or_else(|| provider.default_scopes());
    let body = format!(
        "client_id={}&scope={}",
        url_encode(client_id),
        url_encode(scope_str)
    );
    let resp = http_request(
        "POST",
        endpoint,
        &[
            ("Content-Type", "application/x-www-form-urlencoded"),
            ("Accept", "application/json"),
        ],
        Some(body.as_bytes()),
        false,
    )
    .map_err(AuthError::Os)?;
    if resp.status != 200 {
        return Err(AuthError::Protocol(format!(
            "{} device authorization returned HTTP {}",
            provider.display_name(),
            resp.status
        )));
    }
    let val: Value = serde_json::from_slice(&resp.body)
        .map_err(|_| AuthError::Protocol("device authorization returned non-JSON".into()))?;
    let user_code = val
        .get("user_code")
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_string();
    let device_code = val
        .get("device_code")
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_string();
    let verification_uri = val
        .get("verification_uri")
        .or_else(|| val.get("verification_url"))
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_string();
    if user_code.is_empty() || device_code.is_empty() || verification_uri.is_empty() {
        return Err(AuthError::Protocol(
            "device authorization response missing required fields".into(),
        ));
    }
    Ok(DeviceCodeChallenge {
        provider,
        user_code,
        verification_uri,
        verification_uri_complete: val
            .get("verification_uri_complete")
            .and_then(Value::as_str)
            .map(ToString::to_string),
        device_code,
        expires_in: val.get("expires_in").and_then(Value::as_u64).unwrap_or(600),
        interval: val
            .get("interval")
            .and_then(Value::as_u64)
            .unwrap_or(5)
            .max(1),
    })
}

#[cfg(windows)]
pub fn poll_device_code(
    config: &OAuthClientConfig,
    challenge: &DeviceCodeChallenge,
) -> Result<DevicePollOutcome, AuthError> {
    let client_id = config.require_client_id(challenge.provider)?;
    let body = format!(
        "grant_type=urn:ietf:params:oauth:grant-type:device_code&client_id={}&device_code={}",
        url_encode(client_id),
        url_encode(&challenge.device_code),
    );
    let resp = http_request(
        "POST",
        challenge.provider.token_endpoint(),
        &[
            ("Content-Type", "application/x-www-form-urlencoded"),
            ("Accept", "application/json"),
        ],
        Some(body.as_bytes()),
        false,
    )
    .map_err(AuthError::Os)?;
    if resp.status == 200 {
        return Ok(DevicePollOutcome::Authorized(parse_token_response(
            resp.status,
            &resp.body,
        )?));
    }
    let err = serde_json::from_slice::<Value>(&resp.body)
        .ok()
        .and_then(|v| {
            v.get("error")
                .and_then(Value::as_str)
                .map(ToString::to_string)
        })
        .unwrap_or_default();
    match err.as_str() {
        "authorization_pending" => Ok(DevicePollOutcome::Pending { slow_down: false }),
        "slow_down" => Ok(DevicePollOutcome::Pending { slow_down: true }),
        "access_denied" => Ok(DevicePollOutcome::Denied),
        "expired_token" => Ok(DevicePollOutcome::Expired),
        _ => Err(AuthError::Protocol(format!(
            "device token poll HTTP {}: {}",
            resp.status, err
        ))),
    }
}

#[cfg(windows)]
pub fn jellyfin_quick_connect_initiate(
    server_base: &str,
    device_id: &str,
) -> Result<JellyfinQuickConnect, AuthError> {
    let base = server_base.trim_end_matches('/');
    let url = format!("{base}/QuickConnect/Initiate");
    let auth_header = format!(
        "MediaBrowser Client=\"ExoLauncher\", Device=\"Windows\", DeviceId=\"{device_id}\", Version=\"1.0.0\""
    );
    let resp = http_request(
        "POST",
        &url,
        &[
            ("Authorization", &auth_header),
            ("Accept", "application/json"),
        ],
        None,
        false,
    )
    .map_err(AuthError::Os)?;
    if resp.status != 200 {
        return Err(AuthError::Protocol(format!(
            "Jellyfin QuickConnect/Initiate returned HTTP {}",
            resp.status
        )));
    }
    let val: Value = serde_json::from_slice(&resp.body)
        .map_err(|_| AuthError::Protocol("Jellyfin QuickConnect returned non-JSON".into()))?;
    let code = val
        .get("Code")
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_string();
    let secret = val
        .get("Secret")
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_string();
    if code.is_empty() || secret.is_empty() {
        return Err(AuthError::Protocol(
            "Jellyfin QuickConnect response missing Code or Secret".into(),
        ));
    }
    Ok(JellyfinQuickConnect {
        code,
        secret,
        authenticated: val
            .get("Authenticated")
            .and_then(Value::as_bool)
            .unwrap_or(false),
    })
}

#[cfg(windows)]
pub fn jellyfin_quick_connect_poll(server_base: &str, secret: &str) -> Result<bool, AuthError> {
    let base = server_base.trim_end_matches('/');
    let url = format!("{base}/QuickConnect/Connect?secret={}", url_encode(secret));
    let resp = http_request("GET", &url, &[("Accept", "application/json")], None, false)
        .map_err(AuthError::Os)?;
    if resp.status != 200 {
        return Err(AuthError::Protocol(format!(
            "Jellyfin QuickConnect/Connect returned HTTP {}",
            resp.status
        )));
    }
    let val: Value = serde_json::from_slice(&resp.body)
        .map_err(|_| AuthError::Protocol("Jellyfin QuickConnect poll returned non-JSON".into()))?;
    Ok(val
        .get("Authenticated")
        .and_then(Value::as_bool)
        .unwrap_or(false))
}

#[cfg(windows)]
pub fn jellyfin_quick_connect_exchange(
    server_base: &str,
    device_id: &str,
    secret: &str,
) -> Result<String, AuthError> {
    let base = server_base.trim_end_matches('/');
    let url = format!("{base}/Users/AuthenticateWithQuickConnect");
    let auth_header = format!(
        "MediaBrowser Client=\"ExoLauncher\", Device=\"Windows\", DeviceId=\"{device_id}\", Version=\"1.0.0\""
    );
    let payload = serde_json::json!({ "Secret": secret }).to_string();
    let resp = http_request(
        "POST",
        &url,
        &[
            ("Authorization", &auth_header),
            ("Content-Type", "application/json"),
            ("Accept", "application/json"),
        ],
        Some(payload.as_bytes()),
        false,
    )
    .map_err(AuthError::Os)?;
    if resp.status != 200 {
        return Err(AuthError::Protocol(format!(
            "Jellyfin QuickConnect exchange returned HTTP {}",
            resp.status
        )));
    }
    let val: Value = serde_json::from_slice(&resp.body)
        .map_err(|_| AuthError::Protocol("Jellyfin auth exchange returned non-JSON".into()))?;
    val.get("AccessToken")
        .and_then(Value::as_str)
        .filter(|t| !t.is_empty())
        .map(ToString::to_string)
        .ok_or_else(|| AuthError::Protocol("Jellyfin response missing AccessToken".into()))
}
