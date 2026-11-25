pub mod config;
pub mod crypto;
pub mod device_code;
pub mod http;
pub mod pkce;
pub mod qr;
pub mod wam;
pub mod webauthn;

pub use config::{
    AuthError, DEFAULT_TOKEN_PROXY_BASE, FIXED_LOOPBACK_PORTS, LoopbackRedirectMode,
    OAuthClientConfig, OAuthProvider, ProviderAuthStatus, is_valid_official_client_id,
    is_valid_token_proxy_base,
};
pub use device_code::{
    DeviceCodeChallenge, DevicePollOutcome, JellyfinQuickConnect, build_device_poll_body,
    build_device_start_body,
};
pub use pkce::{LoopbackPkceServer, OAuthTokenSet, PkceAuthSession, bind_loopback_listener};
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
    use std::net::{TcpListener, TcpStream};
    use std::time::Duration;

    #[test]
    fn unconfigured_providers_are_marked_unavailable_with_clear_notes() {
        let cfg = OAuthClientConfig::empty();
        assert_eq!(OAuthProvider::ALL.len(), 8);
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
    fn provider_flows_endpoints_and_redirect_modes_match_official_specs() {
        assert_eq!(OAuthProvider::Discord.id(), "discord");
        assert_eq!(
            OAuthProvider::Discord.env_var(),
            "EXO_OAUTH_DISCORD_CLIENT_ID"
        );
        assert_eq!(OAuthProvider::Discord.flow(), "pkce-system-browser");
        assert_eq!(OAuthProvider::Discord.default_scopes(), "identify");
        assert_eq!(
            OAuthProvider::Discord.authorization_endpoint(),
            Some("https://discord.com/oauth2/authorize")
        );
        assert_eq!(
            OAuthProvider::Discord.token_endpoint(),
            "https://discord.com/api/oauth2/token"
        );
        assert_eq!(
            OAuthProvider::Discord.loopback_redirect_mode(),
            Some(LoopbackRedirectMode::FixedPorts {
                host: "127.0.0.1",
                ports: FIXED_LOOPBACK_PORTS,
            })
        );
        assert!(!OAuthProvider::Discord.requires_token_exchange_proxy());

        assert_eq!(OAuthProvider::Twitch.flow(), "device-code");
        assert_eq!(OAuthProvider::Twitch.authorization_endpoint(), None);
        assert_eq!(
            OAuthProvider::Twitch.device_authorization_endpoint(),
            Some("https://id.twitch.tv/oauth2/device")
        );
        assert_eq!(OAuthProvider::Twitch.loopback_redirect_mode(), None);
        assert!(!OAuthProvider::Twitch.requires_token_exchange_proxy());

        assert_eq!(OAuthProvider::Epic.flow(), "pkce-system-browser");
        assert_eq!(
            OAuthProvider::Epic.authorization_endpoint(),
            Some("https://www.epicgames.com/id/authorize")
        );
        assert_eq!(
            OAuthProvider::Epic.token_endpoint(),
            "https://api.epicgames.dev/epic/oauth/v2/token"
        );
        assert_eq!(OAuthProvider::Epic.device_authorization_endpoint(), None);
        assert!(OAuthProvider::Epic.requires_token_exchange_proxy());

        assert_eq!(
            OAuthProvider::Spotify.loopback_redirect_mode(),
            Some(LoopbackRedirectMode::DynamicPort { host: "127.0.0.1" })
        );
        assert_eq!(
            OAuthProvider::Spotify.registered_redirect_uris(),
            vec!["http://127.0.0.1/callback".to_string()]
        );
        assert!(!OAuthProvider::Spotify.requires_token_exchange_proxy());

        assert_eq!(
            OAuthProvider::Microsoft.loopback_redirect_mode(),
            Some(LoopbackRedirectMode::DynamicPort { host: "localhost" })
        );
        assert!(!OAuthProvider::Microsoft.requires_token_exchange_proxy());

        assert!(OAuthProvider::Google.requires_token_exchange_proxy());
        assert!(OAuthProvider::BattleNet.requires_token_exchange_proxy());
        assert!(OAuthProvider::Riot.requires_token_exchange_proxy());
    }

    #[test]
    fn twitch_device_code_uses_plural_scopes_parameter_on_start_and_poll() {
        let start = build_device_start_body(
            OAuthProvider::Twitch,
            "twitch_official_12345",
            "user:read:email",
        );
        assert!(start.contains("client_id=twitch_official_12345"));
        assert!(start.contains("scopes=user%3Aread%3Aemail"));
        assert!(!start.contains("&scope="));

        let poll = build_device_poll_body(
            OAuthProvider::Twitch,
            "twitch_official_12345",
            "dev_code_999",
            "user:read:email",
        );
        assert!(poll.contains("client_id=twitch_official_12345"));
        assert!(poll.contains("scopes=user%3Aread%3Aemail"));
        assert!(poll.contains("device_code=dev_code_999"));
        assert!(poll.contains("grant_type=urn:ietf:params:oauth:grant-type:device_code"));

        let ms_start = build_device_start_body(
            OAuthProvider::Microsoft,
            "ms_official_12345",
            "XboxLive.signin offline_access",
        );
        assert!(ms_start.contains("&scope="));
        assert!(!ms_start.contains("&scopes="));
    }

    #[test]
    fn layered_config_precedence_is_baked_then_json_then_runtime_env() {
        let cfg = OAuthClientConfig::from_layers(
            &[
                (OAuthProvider::Spotify, "baked_spotify_1111"),
                (OAuthProvider::Discord, "baked_discord_1111"),
                (OAuthProvider::Twitch, "baked_twitch_1111"),
            ],
            Some(r#"{"providers":{"discord":"json_discord_2222","twitch":"json_twitch_2222"}}"#),
            &[(OAuthProvider::Twitch, "env_twitch_3333")],
        )
        .expect("layered config should parse");

        assert_eq!(
            cfg.client_id(OAuthProvider::Spotify),
            Some("baked_spotify_1111")
        );
        assert_eq!(
            cfg.client_id(OAuthProvider::Discord),
            Some("json_discord_2222")
        );
        assert_eq!(
            cfg.client_id(OAuthProvider::Twitch),
            Some("env_twitch_3333")
        );
        assert_eq!(cfg.client_id(OAuthProvider::Epic), None);

        let proxy_cfg = OAuthClientConfig::from_layers(
            &[(OAuthProvider::Epic, "epic_official_1234")],
            None,
            &[],
        )
        .expect("epic config");
        let st = proxy_cfg.status(OAuthProvider::Epic);
        assert!(st.available);
        assert!(st.note.contains("cloud token proxy"));
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
            r#"{"providers":{"spotify":"sp_live_12345678","discord":"dc_live_87654321","microsoft":"ms_live_87654321"}}"#,
        )
        .expect("wrapped json should parse");
        assert_eq!(
            wrapped.client_id(OAuthProvider::Spotify),
            Some("sp_live_12345678")
        );
        assert_eq!(
            wrapped.client_id(OAuthProvider::Discord),
            Some("dc_live_87654321")
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
    fn fixed_loopback_port_falls_back_when_earlier_ports_are_busy() {
        let busy1 = TcpListener::bind("127.0.0.1:0").expect("bind busy1");
        let p1 = busy1.local_addr().expect("p1").port();
        let busy2 = TcpListener::bind("127.0.0.1:0").expect("bind busy2");
        let p2 = busy2.local_addr().expect("p2").port();
        let probe = TcpListener::bind("127.0.0.1:0").expect("bind probe");
        let p3 = probe.local_addr().expect("p3").port();
        drop(probe);

        let ports: &'static [u16] = Box::leak(Box::new([p1, p2, p3]));
        let server = LoopbackPkceServer::from_parts_with_mode(
            OAuthProvider::Discord,
            LoopbackRedirectMode::FixedPorts {
                host: "127.0.0.1",
                ports,
            },
            "discord_official_12345".to_string(),
            "https://discord.com/oauth2/authorize",
            "identify",
            "state-fixed-fallback".to_string(),
            "verifier-1234567890".to_string(),
            "challenge-abcdefghij".to_string(),
        )
        .expect("should fall back to third free port");

        assert_eq!(server.session.port(), p3);
        assert_eq!(
            server.session.redirect_uri,
            format!("http://127.0.0.1:{p3}/callback")
        );

        let busy_all = bind_loopback_listener(LoopbackRedirectMode::FixedPorts {
            host: "127.0.0.1",
            ports,
        });
        assert!(matches!(busy_all, Err(AuthError::Os(_))));
        drop(server);
        drop(busy1);
        drop(busy2);
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
            device_code::start_device_code(&empty, OAuthProvider::Twitch, None),
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

    #[test]
    fn confidential_providers_route_token_exchange_through_proxy_with_config_override() {
        let empty = OAuthClientConfig::empty();
        assert_eq!(
            empty.token_endpoint_for(OAuthProvider::Google),
            "https://auth.getexo.app/oauth/google/token"
        );
        assert_eq!(
            empty.token_endpoint_for(OAuthProvider::BattleNet),
            "https://auth.getexo.app/oauth/battlenet/token"
        );
        assert_eq!(
            empty.token_endpoint_for(OAuthProvider::Riot),
            "https://auth.getexo.app/oauth/riot/token"
        );
        assert_eq!(
            empty.token_endpoint_for(OAuthProvider::Epic),
            "https://auth.getexo.app/oauth/epic/token"
        );
        assert_eq!(
            empty.token_endpoint_for(OAuthProvider::Spotify),
            "https://accounts.spotify.com/api/token"
        );
        assert_eq!(
            empty.token_endpoint_for(OAuthProvider::Discord),
            "https://discord.com/api/oauth2/token"
        );
        assert_eq!(
            empty.token_endpoint_for(OAuthProvider::Twitch),
            "https://id.twitch.tv/oauth2/token"
        );
        assert_eq!(
            empty.token_endpoint_for(OAuthProvider::Microsoft),
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token"
        );

        let custom = OAuthClientConfig::from_json_str(
            r#"{"tokenProxyBase":"http://127.0.0.1:18080/","providers":{"epic":"epic_official_1234"}}"#,
        )
        .expect("custom proxy config");
        assert_eq!(
            custom.token_endpoint_for(OAuthProvider::Epic),
            "http://127.0.0.1:18080/oauth/epic/token"
        );
        assert_eq!(
            custom.token_endpoint_for(OAuthProvider::Discord),
            "https://discord.com/api/oauth2/token"
        );

        let insecure = OAuthClientConfig::from_json_str(
            r#"{"tokenProxyBase":"http://evil.example","providers":{"epic":"epic_official_1234"}}"#,
        )
        .expect("insecure base ignored");
        assert_eq!(
            insecure.token_endpoint_for(OAuthProvider::Epic),
            "https://auth.getexo.app/oauth/epic/token"
        );
    }

    #[cfg(windows)]
    #[test]
    fn pkce_exchange_code_routes_epic_through_configured_local_mock_proxy() {
        let mock_proxy = TcpListener::bind("127.0.0.1:0").expect("bind mock proxy");
        let proxy_port = mock_proxy.local_addr().expect("proxy port").port();
        let proxy_thread = std::thread::spawn(move || {
            let (mut stream, _) = mock_proxy.accept().expect("accept proxy request");
            let req = pkce::read_http_request(&mut stream);
            let body =
                r#"{"access_token":"proxied_tok_123","token_type":"Bearer","expires_in":3600}"#;
            let resp = format!(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
                body.len(),
                body
            );
            stream.write_all(resp.as_bytes()).expect("write proxy resp");
            req
        });

        let cfg = OAuthClientConfig::from_json_str(&format!(
            r#"{{"tokenProxyBase":"http://127.0.0.1:{proxy_port}","providers":{{"epic":"epic_official_9999"}}}}"#
        ))
        .expect("cfg");
        let server =
            LoopbackPkceServer::prepare(&cfg, OAuthProvider::Epic, None).expect("prepare epic");
        assert_eq!(
            server.session.token_endpoint,
            format!("http://127.0.0.1:{proxy_port}/oauth/epic/token")
        );
        let tokens = server
            .exchange_code("epic_auth_code_77")
            .expect("exchange_code via mock proxy");
        let raw_req = proxy_thread.join().expect("proxy thread");

        assert_eq!(tokens.access_token, "proxied_tok_123");
        assert!(raw_req.starts_with("POST /oauth/epic/token "));
        assert!(raw_req.contains("grant_type=authorization_code"));
        assert!(raw_req.contains("client_id=epic_official_9999"));
        assert!(raw_req.contains("code=epic_auth_code_77"));
        assert!(raw_req.contains("code_verifier="));
    }
}
