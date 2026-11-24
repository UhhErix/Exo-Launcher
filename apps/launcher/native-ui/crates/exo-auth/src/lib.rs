pub mod config;
pub mod crypto;
pub mod device_code;
pub mod http;
pub mod pkce;
pub mod qr;
pub mod wam;
pub mod webauthn;

pub use config::{
    AuthError, OAuthClientConfig, OAuthProvider, ProviderAuthStatus, is_valid_official_client_id,
};
pub use device_code::{DeviceCodeChallenge, DevicePollOutcome, JellyfinQuickConnect};
pub use pkce::{LoopbackPkceServer, OAuthTokenSet, PkceAuthSession};
pub use qr::{
    DiscordQrEvent, DiscordQrProbe, SteamCredentialsStart, SteamPollResult, SteamQrStart,
    SteamRsaKeyInfo,
};
pub use wam::WamProbeResult;
pub use webauthn::{AssertionProof, RegistrationProof, WebAuthnProbeResult};

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::net::TcpStream;
    use std::time::Duration;

    #[test]
    fn unconfigured_providers_are_marked_unavailable_with_clear_notes() {
        let cfg = OAuthClientConfig::empty();
        for &provider in OAuthProvider::ALL {
            let st = cfg.status(provider);
            assert!(!st.available, "{} should be unavailable", provider.id());
            assert!(st.note.contains("Unavailable"));
            assert!(st.note.contains(provider.env_var()));
            assert!(matches!(
                cfg.require_client_id(provider),
                Err(AuthError::Unavailable { .. })
            ));
        }
    }

    #[test]
    fn placeholder_and_blank_client_ids_are_rejected() {
        let mut cfg = OAuthClientConfig::empty();
        for bad in [
            "",
            "   ",
            "short",
            "PLACEHOLDER_ID_12345",
            "exo_lab_temp_id",
            "your_client_id_here",
            "changeme-oauth-id",
            "has whitespace inside",
        ] {
            cfg.set_client_id(OAuthProvider::Spotify, bad);
            assert!(
                cfg.client_id(OAuthProvider::Spotify).is_none(),
                "should reject {bad:?}"
            );
        }

        cfg.set_client_id(OAuthProvider::Spotify, "a1b2c3d4e5f6g7h8i9j0");
        assert_eq!(
            cfg.client_id(OAuthProvider::Spotify),
            Some("a1b2c3d4e5f6g7h8i9j0")
        );
        assert!(cfg.status(OAuthProvider::Spotify).available);
    }

    #[test]
    fn json_config_parses_both_wrapped_and_flat_formats() {
        let wrapped = OAuthClientConfig::from_json_str(
            r#"{"providers":{"spotify":"sp_live_12345678","microsoft":"ms_live_87654321"}}"#,
        )
        .expect("wrapped json should parse");
        assert_eq!(
            wrapped.client_id(OAuthProvider::Spotify),
            Some("sp_live_12345678")
        );
        assert_eq!(
            wrapped.client_id(OAuthProvider::Microsoft),
            Some("ms_live_87654321")
        );
        assert!(wrapped.client_id(OAuthProvider::Epic).is_none());

        let flat = OAuthClientConfig::from_json_str(r#"{"twitch":"tw_live_abcdefgh"}"#)
            .expect("flat json should parse");
        assert_eq!(
            flat.client_id(OAuthProvider::Twitch),
            Some("tw_live_abcdefgh")
        );
    }

    #[test]
    fn pkce_loopback_roundtrip_verifies_state_and_security_headers() {
        let server = LoopbackPkceServer::from_parts(
            OAuthProvider::Spotify,
            "sp_official_123456".to_string(),
            "https://accounts.spotify.com/authorize",
            "user-read-playback-state",
            "state-nonce-987654321".to_string(),
            "verifier-1234567890".to_string(),
            "challenge-abcdefghij".to_string(),
        )
        .expect("loopback server should bind");

        assert!(
            server
                .session
                .authorize_url
                .contains("code_challenge_method=S256")
        );
        assert!(
            server
                .session
                .authorize_url
                .contains("state=state-nonce-987654321")
        );
        let port = server.session.port();

        let client = std::thread::spawn(move || {
            let mut s = TcpStream::connect(("127.0.0.1", port)).expect("connect loopback");
            let req = "GET /callback?code=auth-code-42&state=state-nonce-987654321 HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
            s.write_all(req.as_bytes()).expect("write callback");
            let mut resp = String::new();
            let _ = s.read_to_string(&mut resp);
            resp
        });

        let code = server
            .wait_for_code(Duration::from_secs(3))
            .expect("callback code should succeed");
        let raw_resp = client.join().expect("client thread");
        assert_eq!(code, "auth-code-42");
        assert!(raw_resp.contains("Referrer-Policy: no-referrer"));
        assert!(raw_resp.contains("Cache-Control: no-store"));
        assert!(raw_resp.contains("X-Content-Type-Options: nosniff"));
    }

    #[test]
    fn pkce_loopback_rejects_mismatched_state() {
        let server = LoopbackPkceServer::from_parts(
            OAuthProvider::Google,
            "google_official_123456".to_string(),
            "https://accounts.google.com/o/oauth2/v2/auth",
            "openid",
            "expected-state-123".to_string(),
            "verifier".to_string(),
            "challenge".to_string(),
        )
        .expect("loopback server should bind");
        let port = server.session.port();

        let client = std::thread::spawn(move || {
            let mut s = TcpStream::connect(("127.0.0.1", port)).expect("connect loopback");
            let req = "GET /callback?code=auth-code-42&state=tampered-state HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
            s.write_all(req.as_bytes()).expect("write callback");
            let mut buf = Vec::new();
            let _ = s.read_to_end(&mut buf);
        });

        let res = server.wait_for_code(Duration::from_secs(3));
        client.join().expect("client thread");
        assert!(matches!(res, Err(AuthError::Protocol(_))));
    }

    #[test]
    fn steam_protobuf_roundtrip_preserves_fields() {
        let details = qr::steam_device_details("ExoTestPC", b"0123456789abcdef");
        let reader = qr::ProtoReader::parse(&details);
        assert_eq!(reader.string(1), "ExoTestPC");
        assert_eq!(reader.varint(2), 1);
        assert_eq!(reader.varint(3), 16);
        assert_eq!(reader.varint(4), 1);
        assert_eq!(reader.bytes(6), b"0123456789abcdef");
    }

    #[cfg(windows)]
    #[test]
    fn windows_crypto_dpapi_wam_and_webauthn_work_natively() {
        let (verifier, challenge) = crypto::pkce_pair().expect("pkce_pair");
        assert!(verifier.len() >= 43);
        assert_eq!(
            challenge,
            crypto::base64url_encode(&crypto::sha256(verifier.as_bytes()).expect("sha256"))
        );

        let sealed = crypto::dpapi_protect(b"exo-native-auth-secret").expect("dpapi_protect");
        let opened = crypto::dpapi_unprotect(&sealed).expect("dpapi_unprotect");
        assert_eq!(opened, b"exo-native-auth-secret");

        let empty = OAuthClientConfig::empty();
        assert!(matches!(
            pkce::LoopbackPkceServer::prepare(&empty, OAuthProvider::Spotify, None),
            Err(AuthError::Unavailable { .. })
        ));
        assert!(matches!(
            device_code::start_device_code(&empty, OAuthProvider::Epic, None),
            Err(AuthError::Unavailable { .. })
        ));
        assert!(matches!(
            wam::request_token_interactive(&empty, 1, None),
            Err(AuthError::Unavailable { .. })
        ));

        let wam_probe = wam::probe_wam(&empty);
        assert!(wam_probe.ok);
        assert!(!wam_probe.client_configured);
        assert_eq!(wam_probe.silent_status, "unconfigured-client-id");

        let webauthn_probe = webauthn::probe_webauthn();
        assert!(webauthn_probe.ok);
        assert!(webauthn_probe.api_version >= 1);
    }
}
