use crate::config::{AuthError, OAuthClientConfig, OAuthProvider};
use serde::Serialize;
#[cfg(windows)]
use std::time::Instant;
#[cfg(windows)]
use windows::Security::Authentication::Web::Core::{
    WebAuthenticationCoreManager, WebTokenRequest, WebTokenRequestResult, WebTokenRequestStatus,
};
#[cfg(windows)]
use windows::Win32::Foundation::HWND;
#[cfg(windows)]
use windows::Win32::System::WinRT::{
    IWebAuthenticationCoreManagerInterop, RO_INIT_MULTITHREADED, RoInitialize,
};
#[cfg(windows)]
use windows::core::{HSTRING, factory};
#[cfg(windows)]
use windows_future::IAsyncOperation;

#[derive(Clone, Debug, Serialize)]
pub struct WamProbeResult {
    pub ok: bool,
    pub default_provider_id: String,
    pub consumers_provider_found: bool,
    pub consumers_display_name: String,
    pub organizations_provider_found: bool,
    pub interop_hwnd_bound: bool,
    pub client_configured: bool,
    pub silent_status: String,
    pub latency_ms: f64,
    pub error: Option<String>,
}

#[cfg(windows)]
pub fn probe_wam(config: &OAuthClientConfig) -> WamProbeResult {
    let t0 = Instant::now();
    unsafe {
        let _ = RoInitialize(RO_INIT_MULTITHREADED);
    }
    let msa_uri = HSTRING::from("https://login.microsoft.com");
    let consumers = HSTRING::from("consumers");
    let organizations = HSTRING::from("organizations");

    let default_provider =
        WebAuthenticationCoreManager::FindAccountProviderAsync(&msa_uri).and_then(|op| op.join());
    let default_id = match &default_provider {
        Ok(p) => p.Id().map(|s| s.to_string()).unwrap_or_default(),
        Err(_) => String::new(),
    };

    let consumers_provider =
        WebAuthenticationCoreManager::FindAccountProviderWithAuthorityAsync(&msa_uri, &consumers)
            .and_then(|op| op.join());
    let configured_client = config.client_id(OAuthProvider::Microsoft);

    let (consumers_found, consumers_name, silent_status, interop_ok) = match consumers_provider {
        Ok(provider) => {
            let display = provider
                .DisplayName()
                .map(|s| s.to_string())
                .unwrap_or_default();
            let interop =
                factory::<WebAuthenticationCoreManager, IWebAuthenticationCoreManagerInterop>()
                    .is_ok();
            let silent = if let Some(client_id) = configured_client {
                let scope = HSTRING::from(OAuthProvider::Microsoft.default_scopes());
                let client = HSTRING::from(client_id);
                match WebTokenRequest::Create(&provider, &scope, &client)
                    .and_then(|req| WebAuthenticationCoreManager::GetTokenSilentlyAsync(&req))
                    .and_then(|op| op.join())
                {
                    Ok(res) => match res.ResponseStatus() {
                        Ok(st) => format!("{st:?}"),
                        Err(e) => format!("status error: {e}"),
                    },
                    Err(e) => format!("silent check error: 0x{:08x}", e.code().0 as u32),
                }
            } else {
                "unconfigured-client-id".to_string()
            };
            (true, display, silent, interop)
        }
        Err(e) => (
            false,
            String::new(),
            format!("provider lookup failed: 0x{:08x}", e.code().0 as u32),
            false,
        ),
    };

    let org_found = WebAuthenticationCoreManager::FindAccountProviderWithAuthorityAsync(
        &msa_uri,
        &organizations,
    )
    .and_then(|op| op.join())
    .is_ok();

    WamProbeResult {
        ok: consumers_found && interop_ok,
        default_provider_id: default_id,
        consumers_provider_found: consumers_found,
        consumers_display_name: consumers_name,
        organizations_provider_found: org_found,
        interop_hwnd_bound: interop_ok,
        client_configured: configured_client.is_some(),
        silent_status,
        latency_ms: t0.elapsed().as_secs_f64() * 1000.0,
        error: None,
    }
}

#[cfg(windows)]
pub fn request_token_interactive(
    config: &OAuthClientConfig,
    hwnd_raw: u64,
    scopes: Option<&str>,
) -> Result<String, AuthError> {
    let client_id = config.require_client_id(OAuthProvider::Microsoft)?;
    if hwnd_raw == 0 {
        return Err(AuthError::Os(
            "window HWND is required for WAM sign-in".into(),
        ));
    }
    unsafe {
        let _ = RoInitialize(RO_INIT_MULTITHREADED);
    }
    let msa_uri = HSTRING::from("https://login.microsoft.com");
    let consumers = HSTRING::from("consumers");
    let provider =
        WebAuthenticationCoreManager::FindAccountProviderWithAuthorityAsync(&msa_uri, &consumers)
            .and_then(|op| op.join())
            .map_err(|e| {
                AuthError::Os(format!("FindAccountProviderWithAuthorityAsync failed: {e}"))
            })?;

    let scope = HSTRING::from(scopes.unwrap_or_else(|| OAuthProvider::Microsoft.default_scopes()));
    let client = HSTRING::from(client_id);
    let request = WebTokenRequest::Create(&provider, &scope, &client)
        .map_err(|e| AuthError::Os(format!("WebTokenRequest failed: {e}")))?;

    let interop: IWebAuthenticationCoreManagerInterop =
        factory::<WebAuthenticationCoreManager, IWebAuthenticationCoreManagerInterop>().map_err(
            |e| {
                AuthError::Os(format!(
                    "IWebAuthenticationCoreManagerInterop factory failed: {e}"
                ))
            },
        )?;

    let hwnd = HWND(hwnd_raw as *mut std::ffi::c_void);
    let op: IAsyncOperation<WebTokenRequestResult> = unsafe {
        interop
            .RequestTokenForWindowAsync(hwnd, &request)
            .map_err(|e| AuthError::Os(format!("RequestTokenForWindowAsync failed: {e}")))?
    };
    let result = op
        .join()
        .map_err(|e| AuthError::Os(format!("WAM dialog join failed: {e}")))?;
    let status = result
        .ResponseStatus()
        .map_err(|e| AuthError::Os(format!("WAM response status failed: {e}")))?;
    match status {
        WebTokenRequestStatus::Success => {
            let responses = result
                .ResponseData()
                .map_err(|e| AuthError::Os(format!("WAM ResponseData failed: {e}")))?;
            let first = responses
                .GetAt(0)
                .map_err(|e| AuthError::Protocol(format!("WAM returned empty token list: {e}")))?;
            first
                .Token()
                .map(|t| t.to_string())
                .map_err(|e| AuthError::Protocol(format!("WAM token read failed: {e}")))
        }
        WebTokenRequestStatus::UserCancel => Err(AuthError::Cancelled),
        _ => {
            let err_detail = result
                .ResponseError()
                .ok()
                .and_then(|e| e.ErrorMessage().ok())
                .map(|m| m.to_string())
                .unwrap_or_else(|| format!("{status:?}"));
            Err(AuthError::Protocol(format!("WAM error: {err_detail}")))
        }
    }
}
