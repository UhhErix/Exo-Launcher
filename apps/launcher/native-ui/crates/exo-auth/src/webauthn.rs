use crate::config::AuthError;
#[cfg(windows)]
use crate::crypto::{base64_decode, base64url_encode};
use serde::Serialize;
#[cfg(windows)]
use std::ptr;
#[cfg(windows)]
use std::time::Instant;
#[cfg(windows)]
use windows::Win32::Foundation::HWND;
#[cfg(windows)]
use windows::Win32::Networking::WindowsWebServices::{
    WEBAUTHN_AUTHENTICATOR_ATTACHMENT_ANY, WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS,
    WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS_CURRENT_VERSION,
    WEBAUTHN_AUTHENTICATOR_MAKE_CREDENTIAL_OPTIONS,
    WEBAUTHN_AUTHENTICATOR_MAKE_CREDENTIAL_OPTIONS_CURRENT_VERSION, WEBAUTHN_CLIENT_DATA,
    WEBAUTHN_CLIENT_DATA_CURRENT_VERSION, WEBAUTHN_COSE_ALGORITHM_ECDSA_P256_WITH_SHA256,
    WEBAUTHN_COSE_ALGORITHM_RSASSA_PKCS1_V1_5_WITH_SHA256, WEBAUTHN_COSE_CREDENTIAL_PARAMETER,
    WEBAUTHN_COSE_CREDENTIAL_PARAMETER_CURRENT_VERSION, WEBAUTHN_COSE_CREDENTIAL_PARAMETERS,
    WEBAUTHN_CREDENTIAL_TYPE_PUBLIC_KEY, WEBAUTHN_GET_CREDENTIALS_OPTIONS,
    WEBAUTHN_GET_CREDENTIALS_OPTIONS_CURRENT_VERSION, WEBAUTHN_HASH_ALGORITHM_SHA_256,
    WEBAUTHN_RP_ENTITY_INFORMATION, WEBAUTHN_RP_ENTITY_INFORMATION_CURRENT_VERSION,
    WEBAUTHN_USER_ENTITY_INFORMATION, WEBAUTHN_USER_ENTITY_INFORMATION_CURRENT_VERSION,
    WEBAUTHN_USER_VERIFICATION_REQUIREMENT_REQUIRED, WebAuthNAuthenticatorGetAssertion,
    WebAuthNAuthenticatorMakeCredential, WebAuthNFreeAssertion, WebAuthNFreeCredentialAttestation,
    WebAuthNFreePlatformCredentialList, WebAuthNGetApiVersionNumber,
    WebAuthNGetPlatformCredentialList, WebAuthNIsUserVerifyingPlatformAuthenticatorAvailable,
};
#[cfg(windows)]
use windows::core::PCWSTR;

#[derive(Clone, Debug, Serialize)]
pub struct WebAuthnProbeResult {
    pub ok: bool,
    pub api_version: u32,
    pub platform_uv_available: bool,
    pub platform_passkey_count: usize,
    pub latency_ms: f64,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RegistrationProof {
    pub credential_id: String,
    pub client_data_json: String,
    pub attestation_object: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AssertionProof {
    pub credential_id: String,
    pub client_data_json: String,
    pub authenticator_data: String,
    pub signature: String,
}

#[cfg(windows)]
fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(windows)]
pub fn probe_webauthn() -> WebAuthnProbeResult {
    let t0 = Instant::now();
    unsafe {
        let api_version = WebAuthNGetApiVersionNumber();
        let uv_avail = WebAuthNIsUserVerifyingPlatformAuthenticatorAvailable()
            .map(|b| b.as_bool())
            .unwrap_or(false);
        let opts = WEBAUTHN_GET_CREDENTIALS_OPTIONS {
            dwVersion: WEBAUTHN_GET_CREDENTIALS_OPTIONS_CURRENT_VERSION,
            pwszRpId: PCWSTR::null(),
            bBrowserInPrivateMode: false.into(),
        };
        let count = match WebAuthNGetPlatformCredentialList(&opts) {
            Ok(list_ptr) if !list_ptr.is_null() => {
                let c = (*list_ptr).cCredentialDetails as usize;
                WebAuthNFreePlatformCredentialList(list_ptr);
                c
            }
            _ => 0,
        };
        WebAuthnProbeResult {
            ok: api_version >= 1,
            api_version,
            platform_uv_available: uv_avail,
            platform_passkey_count: count,
            latency_ms: t0.elapsed().as_secs_f64() * 1000.0,
        }
    }
}

#[cfg(windows)]
pub fn make_credential(
    hwnd_raw: u64,
    rp_id: &str,
    rp_name: &str,
    origin: &str,
    challenge: &str,
    user_id_b64url: &str,
    user_name: &str,
) -> Result<RegistrationProof, AuthError> {
    if hwnd_raw == 0 {
        return Err(AuthError::Os(
            "window HWND is required for passkey creation".into(),
        ));
    }
    let user_id = base64_decode(user_id_b64url).map_err(AuthError::Protocol)?;
    let client_json = serde_json::json!({
        "type": "webauthn.create",
        "challenge": challenge,
        "origin": origin,
        "crossOrigin": false
    })
    .to_string();
    let client_bytes = client_json.as_bytes();

    let rp_id_w = to_wide(rp_id);
    let rp_name_w = to_wide(rp_name);
    let user_name_w = to_wide(user_name);

    let rp = WEBAUTHN_RP_ENTITY_INFORMATION {
        dwVersion: WEBAUTHN_RP_ENTITY_INFORMATION_CURRENT_VERSION,
        pwszId: PCWSTR(rp_id_w.as_ptr()),
        pwszName: PCWSTR(rp_name_w.as_ptr()),
        pwszIcon: PCWSTR::null(),
    };
    let user = WEBAUTHN_USER_ENTITY_INFORMATION {
        dwVersion: WEBAUTHN_USER_ENTITY_INFORMATION_CURRENT_VERSION,
        cbId: user_id.len() as u32,
        pbId: user_id.as_ptr() as *mut u8,
        pwszName: PCWSTR(user_name_w.as_ptr()),
        pwszIcon: PCWSTR::null(),
        pwszDisplayName: PCWSTR(user_name_w.as_ptr()),
    };
    let mut param_list = [
        WEBAUTHN_COSE_CREDENTIAL_PARAMETER {
            dwVersion: WEBAUTHN_COSE_CREDENTIAL_PARAMETER_CURRENT_VERSION,
            pwszCredentialType: WEBAUTHN_CREDENTIAL_TYPE_PUBLIC_KEY,
            lAlg: WEBAUTHN_COSE_ALGORITHM_ECDSA_P256_WITH_SHA256,
        },
        WEBAUTHN_COSE_CREDENTIAL_PARAMETER {
            dwVersion: WEBAUTHN_COSE_CREDENTIAL_PARAMETER_CURRENT_VERSION,
            pwszCredentialType: WEBAUTHN_CREDENTIAL_TYPE_PUBLIC_KEY,
            lAlg: WEBAUTHN_COSE_ALGORITHM_RSASSA_PKCS1_V1_5_WITH_SHA256,
        },
    ];
    let params = WEBAUTHN_COSE_CREDENTIAL_PARAMETERS {
        cCredentialParameters: param_list.len() as u32,
        pCredentialParameters: param_list.as_mut_ptr(),
    };
    let client_data = WEBAUTHN_CLIENT_DATA {
        dwVersion: WEBAUTHN_CLIENT_DATA_CURRENT_VERSION,
        cbClientDataJSON: client_bytes.len() as u32,
        pbClientDataJSON: client_bytes.as_ptr() as *mut u8,
        pwszHashAlgId: WEBAUTHN_HASH_ALGORITHM_SHA_256,
    };
    let opts = WEBAUTHN_AUTHENTICATOR_MAKE_CREDENTIAL_OPTIONS {
        dwVersion: WEBAUTHN_AUTHENTICATOR_MAKE_CREDENTIAL_OPTIONS_CURRENT_VERSION,
        dwTimeoutMilliseconds: 60_000,
        dwAuthenticatorAttachment: WEBAUTHN_AUTHENTICATOR_ATTACHMENT_ANY,
        dwUserVerificationRequirement: WEBAUTHN_USER_VERIFICATION_REQUIREMENT_REQUIRED,
        bPreferResidentKey: true.into(),
        bRequireResidentKey: false.into(),
        ..Default::default()
    };

    let hwnd = HWND(hwnd_raw as *mut std::ffi::c_void);
    unsafe {
        match WebAuthNAuthenticatorMakeCredential(
            hwnd,
            &rp,
            &user,
            &params,
            &client_data,
            Some(&opts),
        ) {
            Ok(att) if !att.is_null() => {
                let cred = std::slice::from_raw_parts(
                    (*att).pbCredentialId,
                    (*att).cbCredentialId as usize,
                );
                let att_obj = std::slice::from_raw_parts(
                    (*att).pbAttestationObject,
                    (*att).cbAttestationObject as usize,
                );
                let proof = RegistrationProof {
                    credential_id: base64url_encode(cred),
                    client_data_json: base64url_encode(client_bytes),
                    attestation_object: base64url_encode(att_obj),
                };
                WebAuthNFreeCredentialAttestation(Some(att));
                Ok(proof)
            }
            Ok(_) => Err(AuthError::Os("WebAuthN returned null attestation".into())),
            Err(e) if e.code().0 as u32 == 0x8007_04C7 => Err(AuthError::Cancelled),
            Err(e) => Err(AuthError::Os(format!("WebAuthN MakeCredential: {e}"))),
        }
    }
}

#[cfg(windows)]
pub fn get_assertion(
    hwnd_raw: u64,
    rp_id: &str,
    origin: &str,
    challenge: &str,
) -> Result<AssertionProof, AuthError> {
    if hwnd_raw == 0 {
        return Err(AuthError::Os(
            "window HWND is required for passkey sign-in".into(),
        ));
    }
    let client_json = serde_json::json!({
        "type": "webauthn.get",
        "challenge": challenge,
        "origin": origin,
        "crossOrigin": false
    })
    .to_string();
    let client_bytes = client_json.as_bytes();
    let rp_id_w = to_wide(rp_id);
    let client_data = WEBAUTHN_CLIENT_DATA {
        dwVersion: WEBAUTHN_CLIENT_DATA_CURRENT_VERSION,
        cbClientDataJSON: client_bytes.len() as u32,
        pbClientDataJSON: client_bytes.as_ptr() as *mut u8,
        pwszHashAlgId: WEBAUTHN_HASH_ALGORITHM_SHA_256,
    };
    let opts = WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS {
        dwVersion: WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS_CURRENT_VERSION,
        dwTimeoutMilliseconds: 60_000,
        dwAuthenticatorAttachment: WEBAUTHN_AUTHENTICATOR_ATTACHMENT_ANY,
        dwUserVerificationRequirement: WEBAUTHN_USER_VERIFICATION_REQUIREMENT_REQUIRED,
        pCancellationId: ptr::null_mut(),
        ..Default::default()
    };

    let hwnd = HWND(hwnd_raw as *mut std::ffi::c_void);
    unsafe {
        match WebAuthNAuthenticatorGetAssertion(
            hwnd,
            PCWSTR(rp_id_w.as_ptr()),
            &client_data,
            Some(&opts),
        ) {
            Ok(assertion) if !assertion.is_null() => {
                let cred = std::slice::from_raw_parts(
                    (*assertion).Credential.pbId,
                    (*assertion).Credential.cbId as usize,
                );
                let auth_data = std::slice::from_raw_parts(
                    (*assertion).pbAuthenticatorData,
                    (*assertion).cbAuthenticatorData as usize,
                );
                let sig = std::slice::from_raw_parts(
                    (*assertion).pbSignature,
                    (*assertion).cbSignature as usize,
                );
                let proof = AssertionProof {
                    credential_id: base64url_encode(cred),
                    client_data_json: base64url_encode(client_bytes),
                    authenticator_data: base64url_encode(auth_data),
                    signature: base64url_encode(sig),
                };
                WebAuthNFreeAssertion(assertion);
                Ok(proof)
            }
            Ok(_) => Err(AuthError::Os("WebAuthN returned null assertion".into())),
            Err(e) if e.code().0 as u32 == 0x8007_04C7 => Err(AuthError::Cancelled),
            Err(e) => Err(AuthError::Os(format!("WebAuthN GetAssertion: {e}"))),
        }
    }
}
