use crate::config::{AuthError, LoopbackRedirectMode, OAuthClientConfig, OAuthProvider};
use crate::crypto::constant_time_eq;
#[cfg(windows)]
use crate::crypto::{base64url_encode, csprng_bytes, pkce_pair};
#[cfg(windows)]
use crate::http::{http_request, open_system_browser};
use crate::http::{url_decode, url_encode};
use serde::{Deserialize, Serialize};
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::time::{Duration, Instant};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OAuthTokenSet {
    pub access_token: String,
    pub refresh_token: Option<String>,
    pub token_type: String,
    pub expires_in: Option<u64>,
    pub scope: Option<String>,
}

#[derive(Clone, Debug)]
pub struct PkceAuthSession {
    pub provider: OAuthProvider,
    pub client_id: String,
    pub redirect_uri: String,
    pub authorize_url: String,
    pub state: String,
    pub code_verifier: String,
    pub code_challenge: String,
    listener_port: u16,
}

impl PkceAuthSession {
    pub fn port(&self) -> u16 {
        self.listener_port
    }
}

pub struct LoopbackPkceServer {
    pub session: PkceAuthSession,
    listener: TcpListener,
}

pub fn bind_loopback_listener(
    mode: LoopbackRedirectMode,
) -> Result<(TcpListener, String, u16), AuthError> {
    match mode {
        LoopbackRedirectMode::DynamicPort { host } => {
            let listener = TcpListener::bind("127.0.0.1:0")
                .map_err(|e| AuthError::Os(format!("loopback bind failed: {e}")))?;
            let port = listener
                .local_addr()
                .map_err(|e| AuthError::Os(format!("loopback local_addr failed: {e}")))?
                .port();
            Ok((listener, host.to_string(), port))
        }
        LoopbackRedirectMode::FixedPorts { host, ports } => {
            let mut last_err = None;
            for &port in ports {
                match TcpListener::bind(("127.0.0.1", port)) {
                    Ok(listener) => return Ok((listener, host.to_string(), port)),
                    Err(e) => last_err = Some(e),
                }
            }
            let tried = ports
                .iter()
                .map(u16::to_string)
                .collect::<Vec<_>>()
                .join(", ");
            let detail = last_err
                .map(|e| e.to_string())
                .unwrap_or_else(|| "no ports configured".to_string());
            Err(AuthError::Os(format!(
                "all fixed loopback ports ({tried}) are unavailable: {detail}"
            )))
        }
    }
}

impl LoopbackPkceServer {
    #[cfg(windows)]
    pub fn prepare(
        config: &OAuthClientConfig,
        provider: OAuthProvider,
        scopes: Option<&str>,
    ) -> Result<Self, AuthError> {
        let client_id = config.require_client_id(provider)?.to_string();
        let auth_endpoint = provider.authorization_endpoint().ok_or_else(|| {
            AuthError::Protocol(format!(
                "{} does not use browser authorization code flow",
                provider.display_name()
            ))
        })?;
        let mode = provider.loopback_redirect_mode().ok_or_else(|| {
            AuthError::Protocol(format!(
                "{} does not use loopback redirect URIs",
                provider.display_name()
            ))
        })?;
        let (code_verifier, code_challenge) = pkce_pair().map_err(AuthError::Os)?;
        let state = base64url_encode(&csprng_bytes(24).map_err(AuthError::Os)?);
        let scope_str = scopes.unwrap_or_else(|| provider.default_scopes());
        Self::from_parts_with_mode(
            provider,
            mode,
            client_id,
            auth_endpoint,
            scope_str,
            state,
            code_verifier,
            code_challenge,
        )
    }

    pub fn from_parts(
        provider: OAuthProvider,
        client_id: String,
        auth_endpoint: &str,
        scopes: &str,
        state: String,
        code_verifier: String,
        code_challenge: String,
    ) -> Result<Self, AuthError> {
        let mode = provider
            .loopback_redirect_mode()
            .unwrap_or(LoopbackRedirectMode::DynamicPort { host: "127.0.0.1" });
        Self::from_parts_with_mode(
            provider,
            mode,
            client_id,
            auth_endpoint,
            scopes,
            state,
            code_verifier,
            code_challenge,
        )
    }

    #[allow(clippy::too_many_arguments)]
    pub fn from_parts_with_mode(
        provider: OAuthProvider,
        mode: LoopbackRedirectMode,
        client_id: String,
        auth_endpoint: &str,
        scopes: &str,
        state: String,
        code_verifier: String,
        code_challenge: String,
    ) -> Result<Self, AuthError> {
        let (listener, host, port) = bind_loopback_listener(mode)?;
        let redirect_uri = format!("http://{host}:{port}/callback");
        let authorize_url = build_authorize_url(
            auth_endpoint,
            &client_id,
            &redirect_uri,
            scopes,
            &state,
            &code_challenge,
        );
        Ok(Self {
            session: PkceAuthSession {
                provider,
                client_id,
                redirect_uri,
                authorize_url,
                state,
                code_verifier,
                code_challenge,
                listener_port: port,
            },
            listener,
        })
    }

    #[cfg(windows)]
    pub fn open_browser(&self) -> Result<(), AuthError> {
        open_system_browser(&self.session.authorize_url).map_err(AuthError::Os)
    }

    pub fn wait_for_code(&self, timeout: Duration) -> Result<String, AuthError> {
        self.listener
            .set_nonblocking(true)
            .map_err(|e| AuthError::Os(format!("loopback set_nonblocking failed: {e}")))?;
        let deadline = Instant::now() + timeout;
        loop {
            match self.listener.accept() {
                Ok((mut stream, _)) => {
                    let req = read_http_request(&mut stream);
                    let first_line = req.lines().next().unwrap_or("");
                    let path = first_line.split_whitespace().nth(1).unwrap_or("");
                    if !path.starts_with("/callback") {
                        let _ = stream.write_all(
                            b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                        );
                        continue;
                    }
                    let query = path.split_once('?').map(|(_, q)| q).unwrap_or("");
                    let mut got_code = None;
                    let mut got_state = None;
                    let mut got_error = None;
                    for pair in query.split('&') {
                        if let Some((k, v)) = pair.split_once('=') {
                            match k {
                                "code" => got_code = Some(url_decode(v)),
                                "state" => got_state = Some(url_decode(v)),
                                "error" => got_error = Some(url_decode(v)),
                                _ => {}
                            }
                        }
                    }

                    let html = "<!doctype html><meta charset=utf-8><title>Exo Sign-In</title><body style=\"background:#09090b;color:#f4f4f5;font-family:system-ui;padding:32px\"><h2>You can return to Exo</h2><p>This browser tab can be closed.</p></body>";
                    let resp = format!(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nReferrer-Policy: no-referrer\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
                        html.len(),
                        html
                    );
                    let _ = stream.write_all(resp.as_bytes());
                    let _ = stream.flush();

                    if let Some(err) = got_error {
                        if err == "access_denied" {
                            return Err(AuthError::Cancelled);
                        }
                        return Err(AuthError::Protocol(format!("OAuth error: {err}")));
                    }
                    let state_ok = got_state
                        .as_deref()
                        .is_some_and(|st| constant_time_eq(st, &self.session.state));
                    if !state_ok {
                        return Err(AuthError::Protocol("OAuth state mismatch".to_string()));
                    }
                    return got_code
                        .filter(|c| !c.is_empty())
                        .ok_or_else(|| AuthError::Protocol("OAuth callback missing code".into()));
                }
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                    if Instant::now() >= deadline {
                        return Err(AuthError::Cancelled);
                    }
                    std::thread::sleep(Duration::from_millis(25));
                }
                Err(e) => return Err(AuthError::Os(format!("loopback accept failed: {e}"))),
            }
        }
    }

    #[cfg(windows)]
    pub fn exchange_code(&self, code: &str) -> Result<OAuthTokenSet, AuthError> {
        let body = format!(
            "grant_type=authorization_code&client_id={}&code={}&redirect_uri={}&code_verifier={}",
            url_encode(&self.session.client_id),
            url_encode(code),
            url_encode(&self.session.redirect_uri),
            url_encode(&self.session.code_verifier),
        );
        let resp = http_request(
            "POST",
            self.session.provider.token_endpoint(),
            &[
                ("Content-Type", "application/x-www-form-urlencoded"),
                ("Accept", "application/json"),
            ],
            Some(body.as_bytes()),
            false,
        )
        .map_err(AuthError::Os)?;
        parse_token_response(resp.status, &resp.body)
    }
}

pub fn build_authorize_url(
    auth_endpoint: &str,
    client_id: &str,
    redirect_uri: &str,
    scopes: &str,
    state: &str,
    code_challenge: &str,
) -> String {
    format!(
        "{}?response_type=code&client_id={}&redirect_uri={}&scope={}&state={}&code_challenge={}&code_challenge_method=S256",
        auth_endpoint,
        url_encode(client_id),
        url_encode(redirect_uri),
        url_encode(scopes),
        url_encode(state),
        url_encode(code_challenge),
    )
}

pub fn parse_token_response(status: u16, body: &[u8]) -> Result<OAuthTokenSet, AuthError> {
    let val: serde_json::Value = serde_json::from_slice(body).map_err(|_| {
        AuthError::Protocol(format!("token endpoint HTTP {status} returned non-JSON"))
    })?;
    if status != 200 {
        let err = val
            .get("error")
            .and_then(serde_json::Value::as_str)
            .unwrap_or("token_exchange_failed");
        return Err(AuthError::Protocol(format!(
            "token endpoint HTTP {status}: {err}"
        )));
    }
    let access_token = val
        .get("access_token")
        .and_then(serde_json::Value::as_str)
        .filter(|s| !s.is_empty())
        .ok_or_else(|| AuthError::Protocol("token response missing access_token".into()))?
        .to_string();
    Ok(OAuthTokenSet {
        access_token,
        refresh_token: val
            .get("refresh_token")
            .and_then(serde_json::Value::as_str)
            .map(ToString::to_string),
        token_type: val
            .get("token_type")
            .and_then(serde_json::Value::as_str)
            .unwrap_or("Bearer")
            .to_string(),
        expires_in: val.get("expires_in").and_then(serde_json::Value::as_u64),
        scope: val
            .get("scope")
            .and_then(serde_json::Value::as_str)
            .map(ToString::to_string),
    })
}

pub(crate) fn read_http_request(stream: &mut TcpStream) -> String {
    let _ = stream.set_read_timeout(Some(Duration::from_secs(2)));
    let mut data = Vec::with_capacity(2048);
    let mut chunk = [0u8; 2048];
    let mut header_end = None;
    let mut content_len = 0usize;
    loop {
        match stream.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => {
                data.extend_from_slice(&chunk[..n]);
                if header_end.is_none()
                    && let Some(pos) = data.windows(4).position(|w| w == b"\r\n\r\n")
                {
                    let end = pos + 4;
                    header_end = Some(end);
                    let hdr_str = String::from_utf8_lossy(&data[..pos]);
                    for line in hdr_str.lines() {
                        if let Some((k, v)) = line.split_once(':')
                            && k.trim().eq_ignore_ascii_case("content-length")
                        {
                            content_len = v.trim().parse::<usize>().unwrap_or(0);
                        }
                    }
                }
                if let Some(end) = header_end
                    && data.len() >= end + content_len
                {
                    break;
                }
                if data.len() >= 65536 {
                    break;
                }
            }
            Err(_) => break,
        }
    }
    String::from_utf8_lossy(&data).into_owned()
}
