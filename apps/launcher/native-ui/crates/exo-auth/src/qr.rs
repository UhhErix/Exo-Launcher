#[cfg(windows)]
use crate::crypto::{
    Rsa2048Oaep, base64_decode, base64_encode, base64url_encode, csprng_bytes, dpapi_protect,
    rsa_encrypt_pkcs1, sha256,
};
#[cfg(windows)]
use crate::http::{WinHttpWs, http_request, url_encode};
use serde::Serialize;
#[cfg(windows)]
use serde_json::Value;
#[cfg(windows)]
use std::sync::Arc;
#[cfg(windows)]
use std::sync::atomic::{AtomicBool, Ordering};
#[cfg(windows)]
use std::sync::mpsc::Sender;
#[cfg(windows)]
use std::time::{Duration, Instant};

#[cfg(windows)]
const DISCORD_GATEWAY: &str = "wss://remote-auth-gateway.discord.gg/?v=2";
#[cfg(windows)]
const DISCORD_LOGIN_URL: &str = "https://discord.com/api/v9/users/@me/remote-auth/login";
#[cfg(windows)]
const STEAM_API: &str = "https://api.steampowered.com/IAuthenticationService";
const PLATFORM_STEAM_CLIENT: i32 = 1;
#[cfg(windows)]
const PERSISTENCE_PERSISTENT: i32 = 1;
const GAMING_DEVICE_STANDARD_PC: u64 = 1;
const OS_TYPE_WINDOWS_10: i32 = 16;
#[cfg(windows)]
const WEBSITE_ID: &str = "Client";

#[derive(Clone, Debug, Serialize)]
pub struct DiscordQrProbe {
    pub ok: bool,
    pub qr_url: String,
    pub fingerprint_len: usize,
    pub heartbeat_interval_ms: u64,
    pub rsa_keygen_ms: f64,
    pub handshake_ms: f64,
    pub error: Option<String>,
}

#[derive(Clone, Debug)]
pub enum DiscordQrEvent {
    Waiting {
        url: String,
        handshake_ms: f64,
        heartbeat_ms: u64,
    },
    Scanned {
        user: String,
    },
    Authenticated {
        sealed_token: Vec<u8>,
    },
    Cancelled,
    Expired,
    Error(String),
}

#[derive(Clone, Debug, Serialize)]
pub struct SteamQrStart {
    pub client_id: u64,
    pub challenge_url: String,
    #[serde(skip_serializing)]
    pub request_id: Vec<u8>,
    pub interval_secs: f32,
    pub latency_ms: f64,
}

#[derive(Clone, Debug)]
pub enum SteamPollResult {
    Waiting {
        new_challenge_url: Option<String>,
        new_client_id: Option<u64>,
    },
    Authenticated {
        account_name: String,
        refresh_token: String,
    },
    Expired,
}

#[derive(Clone, Debug, Serialize)]
pub struct SteamRsaKeyInfo {
    pub modulus_hex_len: usize,
    pub exponent_hex: String,
    pub timestamp: u64,
    pub latency_ms: f64,
    #[serde(skip_serializing)]
    pub modulus_hex: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct SteamCredentialsStart {
    pub client_id: u64,
    pub steam_id: u64,
    #[serde(skip_serializing)]
    pub request_id: Vec<u8>,
    pub interval_secs: f32,
    pub guard_kind: String,
    pub latency_ms: f64,
}

#[cfg(windows)]
pub fn probe_discord_qr_once() -> DiscordQrProbe {
    let t0 = Instant::now();
    let rsa = match Rsa2048Oaep::generate() {
        Ok(k) => k,
        Err(e) => {
            return DiscordQrProbe {
                ok: false,
                qr_url: String::new(),
                fingerprint_len: 0,
                heartbeat_interval_ms: 0,
                rsa_keygen_ms: 0.0,
                handshake_ms: 0.0,
                error: Some(e),
            };
        }
    };
    let rsa_keygen_ms = t0.elapsed().as_secs_f64() * 1000.0;
    let ws = match WinHttpWs::connect(DISCORD_GATEWAY, &[("Origin", "https://discord.com")]) {
        Ok(w) => w,
        Err(e) => {
            return DiscordQrProbe {
                ok: false,
                qr_url: String::new(),
                fingerprint_len: 0,
                heartbeat_interval_ms: 0,
                rsa_keygen_ms,
                handshake_ms: t0.elapsed().as_secs_f64() * 1000.0,
                error: Some(e),
            };
        }
    };

    let mut heartbeat_ms = 0u64;
    for _ in 0..8 {
        let msg = match ws.recv_text() {
            Ok(Some(m)) => m,
            Ok(None) => break,
            Err(e) => {
                ws.close();
                return DiscordQrProbe {
                    ok: false,
                    qr_url: String::new(),
                    fingerprint_len: 0,
                    heartbeat_interval_ms: heartbeat_ms,
                    rsa_keygen_ms,
                    handshake_ms: t0.elapsed().as_secs_f64() * 1000.0,
                    error: Some(e),
                };
            }
        };
        let Ok(v) = serde_json::from_str::<Value>(&msg) else {
            continue;
        };
        match v.get("op").and_then(Value::as_str).unwrap_or("") {
            "hello" => {
                heartbeat_ms = v
                    .get("heartbeat_interval")
                    .and_then(Value::as_u64)
                    .unwrap_or(41250);
                let init = serde_json::json!({
                    "op": "init",
                    "encoded_public_key": rsa.spki_base64(),
                });
                if let Err(e) = ws.send_text(&init.to_string()) {
                    ws.close();
                    return DiscordQrProbe {
                        ok: false,
                        qr_url: String::new(),
                        fingerprint_len: 0,
                        heartbeat_interval_ms: heartbeat_ms,
                        rsa_keygen_ms,
                        handshake_ms: t0.elapsed().as_secs_f64() * 1000.0,
                        error: Some(e),
                    };
                }
            }
            "nonce_proof" => {
                let enc_b64 = v
                    .get("encrypted_nonce")
                    .and_then(Value::as_str)
                    .unwrap_or("");
                let result = base64_decode(enc_b64)
                    .and_then(|c| rsa.decrypt_oaep_sha256(&c))
                    .and_then(|plain| sha256(&plain))
                    .map(|digest| base64url_encode(&digest));
                match result {
                    Ok(proof) => {
                        let reply = serde_json::json!({
                            "op": "nonce_proof",
                            "proof": proof,
                        });
                        if let Err(e) = ws.send_text(&reply.to_string()) {
                            ws.close();
                            return DiscordQrProbe {
                                ok: false,
                                qr_url: String::new(),
                                fingerprint_len: 0,
                                heartbeat_interval_ms: heartbeat_ms,
                                rsa_keygen_ms,
                                handshake_ms: t0.elapsed().as_secs_f64() * 1000.0,
                                error: Some(e),
                            };
                        }
                    }
                    Err(e) => {
                        ws.close();
                        return DiscordQrProbe {
                            ok: false,
                            qr_url: String::new(),
                            fingerprint_len: 0,
                            heartbeat_interval_ms: heartbeat_ms,
                            rsa_keygen_ms,
                            handshake_ms: t0.elapsed().as_secs_f64() * 1000.0,
                            error: Some(e),
                        };
                    }
                }
            }
            "pending_remote_init" => {
                let fp = v
                    .get("fingerprint")
                    .and_then(Value::as_str)
                    .unwrap_or("")
                    .to_string();
                let qr_url = format!("https://discord.com/ra/{fp}");
                ws.close();
                return DiscordQrProbe {
                    ok: !fp.is_empty(),
                    qr_url,
                    fingerprint_len: fp.len(),
                    heartbeat_interval_ms: heartbeat_ms,
                    rsa_keygen_ms,
                    handshake_ms: t0.elapsed().as_secs_f64() * 1000.0,
                    error: None,
                };
            }
            _ => {}
        }
    }
    ws.close();
    DiscordQrProbe {
        ok: false,
        qr_url: String::new(),
        fingerprint_len: 0,
        heartbeat_interval_ms: heartbeat_ms,
        rsa_keygen_ms,
        handshake_ms: t0.elapsed().as_secs_f64() * 1000.0,
        error: Some("gateway closed before pending_remote_init".to_string()),
    }
}

#[cfg(windows)]
pub fn start_discord_qr_session(
    tx: Sender<DiscordQrEvent>,
    cancel: Arc<AtomicBool>,
    on_event: impl Fn() + Send + 'static,
) {
    std::thread::spawn(move || {
        let notify = |ev: DiscordQrEvent| {
            let _ = tx.send(ev);
            on_event();
        };
        let t0 = Instant::now();
        let rsa = match Rsa2048Oaep::generate() {
            Ok(k) => k,
            Err(e) => {
                notify(DiscordQrEvent::Error(e));
                return;
            }
        };
        let ws = match WinHttpWs::connect(DISCORD_GATEWAY, &[("Origin", "https://discord.com")]) {
            Ok(w) => Arc::new(w),
            Err(e) => {
                notify(DiscordQrEvent::Error(e));
                return;
            }
        };
        let stop_hb = Arc::new(AtomicBool::new(false));
        let mut heartbeat_ms = 41250u64;

        loop {
            if cancel.load(Ordering::Relaxed) {
                notify(DiscordQrEvent::Cancelled);
                break;
            }
            let msg = match ws.recv_text() {
                Ok(Some(m)) => m,
                Ok(None) => {
                    notify(DiscordQrEvent::Expired);
                    break;
                }
                Err(e) => {
                    if cancel.load(Ordering::Relaxed) {
                        notify(DiscordQrEvent::Cancelled);
                    } else {
                        notify(DiscordQrEvent::Error(e));
                    }
                    break;
                }
            };
            let Ok(v) = serde_json::from_str::<Value>(&msg) else {
                continue;
            };
            match v.get("op").and_then(Value::as_str).unwrap_or("") {
                "hello" => {
                    heartbeat_ms = v
                        .get("heartbeat_interval")
                        .and_then(Value::as_u64)
                        .unwrap_or(41250);
                    let ws_hb = Arc::clone(&ws);
                    let stop_ref = Arc::clone(&stop_hb);
                    let cancel_ref = Arc::clone(&cancel);
                    std::thread::spawn(move || {
                        while !stop_ref.load(Ordering::Relaxed)
                            && !cancel_ref.load(Ordering::Relaxed)
                        {
                            let mut waited = 0u64;
                            while waited < heartbeat_ms {
                                if stop_ref.load(Ordering::Relaxed)
                                    || cancel_ref.load(Ordering::Relaxed)
                                {
                                    return;
                                }
                                std::thread::sleep(Duration::from_millis(250));
                                waited += 250;
                            }
                            if ws_hb.send_text("{\"op\":\"heartbeat\"}").is_err() {
                                return;
                            }
                        }
                    });
                    let init = serde_json::json!({
                        "op": "init",
                        "encoded_public_key": rsa.spki_base64(),
                    });
                    if let Err(e) = ws.send_text(&init.to_string()) {
                        notify(DiscordQrEvent::Error(e));
                        break;
                    }
                }
                "nonce_proof" => {
                    let enc_b64 = v
                        .get("encrypted_nonce")
                        .and_then(Value::as_str)
                        .unwrap_or("");
                    match base64_decode(enc_b64)
                        .and_then(|c| rsa.decrypt_oaep_sha256(&c))
                        .and_then(|plain| sha256(&plain))
                        .map(|digest| base64url_encode(&digest))
                    {
                        Ok(proof) => {
                            let reply = serde_json::json!({
                                "op": "nonce_proof",
                                "proof": proof,
                            });
                            if let Err(e) = ws.send_text(&reply.to_string()) {
                                notify(DiscordQrEvent::Error(e));
                                break;
                            }
                        }
                        Err(e) => {
                            notify(DiscordQrEvent::Error(e));
                            break;
                        }
                    }
                }
                "pending_remote_init" => {
                    if let Some(fp) = v.get("fingerprint").and_then(Value::as_str) {
                        notify(DiscordQrEvent::Waiting {
                            url: format!("https://discord.com/ra/{fp}"),
                            handshake_ms: t0.elapsed().as_secs_f64() * 1000.0,
                            heartbeat_ms,
                        });
                    }
                }
                "pending_ticket" => {
                    let enc_user = v
                        .get("encrypted_user_payload")
                        .and_then(Value::as_str)
                        .unwrap_or("");
                    let user = base64_decode(enc_user)
                        .and_then(|c| rsa.decrypt_oaep_sha256(&c))
                        .ok()
                        .and_then(|b| String::from_utf8(b).ok())
                        .map(|s| {
                            let parts: Vec<&str> = s.split(':').collect();
                            if parts.len() >= 4 {
                                parts[3..].join(":")
                            } else {
                                "Discord user".to_string()
                            }
                        })
                        .unwrap_or_else(|| "Discord user".to_string());
                    notify(DiscordQrEvent::Scanned { user });
                }
                "pending_login" => {
                    let ticket = v.get("ticket").and_then(Value::as_str).unwrap_or("");
                    let body = serde_json::json!({ "ticket": ticket }).to_string();
                    match http_request(
                        "POST",
                        DISCORD_LOGIN_URL,
                        &[("Content-Type", "application/json")],
                        Some(body.as_bytes()),
                        false,
                    ) {
                        Ok(resp) if resp.status == 200 => {
                            let parsed = serde_json::from_slice::<Value>(&resp.body).ok();
                            let enc_tok = parsed
                                .as_ref()
                                .and_then(|j| j.get("encrypted_token"))
                                .and_then(Value::as_str)
                                .unwrap_or("");
                            match base64_decode(enc_tok)
                                .and_then(|c| rsa.decrypt_oaep_sha256(&c))
                                .and_then(|plain| dpapi_protect(&plain))
                            {
                                Ok(sealed_token) => {
                                    notify(DiscordQrEvent::Authenticated { sealed_token });
                                }
                                Err(e) => notify(DiscordQrEvent::Error(e)),
                            }
                        }
                        Ok(resp) => notify(DiscordQrEvent::Error(format!(
                            "remote-auth/login returned HTTP {}",
                            resp.status
                        ))),
                        Err(e) => notify(DiscordQrEvent::Error(e)),
                    }
                    break;
                }
                "finish" => {
                    let enc_tok = v
                        .get("encrypted_token")
                        .and_then(Value::as_str)
                        .unwrap_or("");
                    match base64_decode(enc_tok)
                        .and_then(|c| rsa.decrypt_oaep_sha256(&c))
                        .and_then(|plain| dpapi_protect(&plain))
                    {
                        Ok(sealed_token) => notify(DiscordQrEvent::Authenticated { sealed_token }),
                        Err(e) => notify(DiscordQrEvent::Error(e)),
                    }
                    break;
                }
                "cancel" => {
                    notify(DiscordQrEvent::Cancelled);
                    break;
                }
                _ => {}
            }
        }
        stop_hb.store(true, Ordering::Relaxed);
        ws.close();
    });
}

pub(crate) struct ProtoWriter {
    buf: Vec<u8>,
}

impl ProtoWriter {
    pub(crate) fn new() -> Self {
        Self { buf: Vec::new() }
    }

    fn tag(&mut self, field: u32, wire: u32) {
        self.varint_raw(((field as u64) << 3) | (wire as u64));
    }

    fn varint_raw(&mut self, mut v: u64) {
        loop {
            let mut b = (v & 0x7f) as u8;
            v >>= 7;
            if v != 0 {
                b |= 0x80;
            }
            self.buf.push(b);
            if v == 0 {
                break;
            }
        }
    }

    pub(crate) fn varint(&mut self, field: u32, v: u64) -> &mut Self {
        if v != 0 {
            self.tag(field, 0);
            self.varint_raw(v);
        }
        self
    }

    pub(crate) fn int32(&mut self, field: u32, v: i32) -> &mut Self {
        if v != 0 {
            self.tag(field, 0);
            self.varint_raw(v as i64 as u64);
        }
        self
    }

    #[cfg(windows)]
    pub(crate) fn bool_field(&mut self, field: u32, v: bool) -> &mut Self {
        if v {
            self.tag(field, 0);
            self.buf.push(1);
        }
        self
    }

    #[cfg(windows)]
    pub(crate) fn fixed64(&mut self, field: u32, v: u64) -> &mut Self {
        if v != 0 {
            self.tag(field, 1);
            self.buf.extend_from_slice(&v.to_le_bytes());
        }
        self
    }

    pub(crate) fn bytes(&mut self, field: u32, data: &[u8]) -> &mut Self {
        if !data.is_empty() {
            self.tag(field, 2);
            self.varint_raw(data.len() as u64);
            self.buf.extend_from_slice(data);
        }
        self
    }

    pub(crate) fn string(&mut self, field: u32, s: &str) -> &mut Self {
        self.bytes(field, s.as_bytes())
    }

    pub(crate) fn finish(self) -> Vec<u8> {
        self.buf
    }
}

pub(crate) struct ProtoReader<'a> {
    fields: Vec<(u32, u32, ProtoVal<'a>)>,
}

enum ProtoVal<'a> {
    Varint(u64),
    Fixed32(u32),
    Fixed64(u64),
    Bytes(&'a [u8]),
}

impl<'a> ProtoReader<'a> {
    pub(crate) fn parse(data: &'a [u8]) -> Self {
        let mut fields = Vec::new();
        let mut i = 0usize;
        while i < data.len() {
            let Some((tag, next)) = read_varint(data, i) else {
                break;
            };
            i = next;
            let field = (tag >> 3) as u32;
            let wire = (tag & 7) as u32;
            match wire {
                0 => {
                    let Some((v, next)) = read_varint(data, i) else {
                        break;
                    };
                    i = next;
                    fields.push((field, wire, ProtoVal::Varint(v)));
                }
                1 => {
                    if i + 8 > data.len() {
                        break;
                    }
                    let v = u64::from_le_bytes(data[i..i + 8].try_into().unwrap_or([0; 8]));
                    i += 8;
                    fields.push((field, wire, ProtoVal::Fixed64(v)));
                }
                2 => {
                    let Some((len, next)) = read_varint(data, i) else {
                        break;
                    };
                    i = next;
                    let len = len as usize;
                    if i + len > data.len() {
                        break;
                    }
                    fields.push((field, wire, ProtoVal::Bytes(&data[i..i + len])));
                    i += len;
                }
                5 => {
                    if i + 4 > data.len() {
                        break;
                    }
                    let v = u32::from_le_bytes(data[i..i + 4].try_into().unwrap_or([0; 4]));
                    i += 4;
                    fields.push((field, wire, ProtoVal::Fixed32(v)));
                }
                _ => break,
            }
        }
        Self { fields }
    }

    pub(crate) fn varint(&self, field: u32) -> u64 {
        for (f, _, val) in &self.fields {
            if *f == field {
                match val {
                    ProtoVal::Varint(v) | ProtoVal::Fixed64(v) => return *v,
                    _ => {}
                }
            }
        }
        0
    }

    #[cfg(windows)]
    pub(crate) fn float(&self, field: u32) -> f32 {
        for (f, _, val) in &self.fields {
            if *f == field
                && let ProtoVal::Fixed32(bits) = val
            {
                return f32::from_bits(*bits);
            }
        }
        5.0
    }

    pub(crate) fn bytes(&self, field: u32) -> Vec<u8> {
        for (f, _, val) in &self.fields {
            if *f == field
                && let ProtoVal::Bytes(b) = val
            {
                return b.to_vec();
            }
        }
        Vec::new()
    }

    pub(crate) fn string(&self, field: u32) -> String {
        String::from_utf8_lossy(&self.bytes(field)).into_owned()
    }

    #[cfg(windows)]
    pub(crate) fn messages(&self, field: u32) -> Vec<&'a [u8]> {
        let mut out = Vec::new();
        for (f, _, val) in &self.fields {
            if *f == field
                && let ProtoVal::Bytes(b) = val
            {
                out.push(*b);
            }
        }
        out
    }
}

fn read_varint(data: &[u8], mut i: usize) -> Option<(u64, usize)> {
    let mut val = 0u64;
    let mut shift = 0u32;
    while i < data.len() && shift < 64 {
        let b = data[i];
        i += 1;
        val |= ((b & 0x7f) as u64) << shift;
        if b & 0x80 == 0 {
            return Some((val, i));
        }
        shift += 7;
    }
    None
}

pub(crate) fn steam_device_details(device_name: &str, machine_id: &[u8]) -> Vec<u8> {
    let mut w = ProtoWriter::new();
    w.string(1, device_name)
        .int32(2, PLATFORM_STEAM_CLIENT)
        .int32(3, OS_TYPE_WINDOWS_10)
        .varint(4, GAMING_DEVICE_STANDARD_PC)
        .bytes(6, machine_id);
    w.finish()
}

#[cfg(windows)]
fn steam_proto_call(
    method: &str,
    endpoint: &str,
    payload: &[u8],
) -> Result<(Vec<u8>, i32), String> {
    let encoded = base64_encode(payload);
    let url = if method == "GET" {
        format!(
            "{STEAM_API}/{endpoint}/v1?input_protobuf_encoded={}",
            url_encode(&encoded)
        )
    } else {
        format!("{STEAM_API}/{endpoint}/v1")
    };
    let form_body = format!("input_protobuf_encoded={}", url_encode(&encoded));
    let (headers, body): (Vec<(&str, &str)>, Option<&[u8]>) = if method == "POST" {
        (
            vec![("Content-Type", "application/x-www-form-urlencoded")],
            Some(form_body.as_bytes()),
        )
    } else {
        (Vec::new(), None)
    };
    let resp = http_request(method, &url, &headers, body, false)?;
    let eresult = resp
        .header("x-eresult")
        .and_then(|v| v.parse::<i32>().ok())
        .unwrap_or(0);
    if resp.status != 200 {
        return Err(format!("Steam HTTP {} (x-eresult={eresult})", resp.status));
    }
    if eresult != 1 && eresult != 0 {
        return Err(format!("Steam refused request (x-eresult={eresult})"));
    }
    Ok((resp.body, eresult))
}

#[cfg(windows)]
pub fn steam_begin_qr(device_name: &str) -> Result<SteamQrStart, String> {
    let started = Instant::now();
    let machine_id = csprng_bytes(16)?;
    let details = steam_device_details(device_name, &machine_id);
    let mut w = ProtoWriter::new();
    w.string(1, device_name)
        .int32(2, PLATFORM_STEAM_CLIENT)
        .bytes(3, &details)
        .string(4, WEBSITE_ID);
    let (body, _) = steam_proto_call("POST", "BeginAuthSessionViaQR", &w.finish())?;
    let r = ProtoReader::parse(&body);
    let client_id = r.varint(1);
    let challenge_url = r.string(2);
    let request_id = r.bytes(3);
    let interval_secs = r.float(4).clamp(2.0, 15.0);
    if challenge_url.is_empty() || client_id == 0 {
        return Err("Steam QR response missing challenge_url".to_string());
    }
    Ok(SteamQrStart {
        client_id,
        challenge_url,
        request_id,
        interval_secs,
        latency_ms: started.elapsed().as_secs_f64() * 1000.0,
    })
}

#[cfg(windows)]
pub fn steam_poll_qr(client_id: u64, request_id: &[u8]) -> Result<SteamPollResult, String> {
    let mut w = ProtoWriter::new();
    w.varint(1, client_id).bytes(2, request_id);
    let resp = steam_proto_call("POST", "PollAuthSessionStatus", &w.finish());
    match resp {
        Ok((body, _)) => {
            let r = ProtoReader::parse(&body);
            let refresh_token = r.string(3);
            if !refresh_token.is_empty() {
                let account_name = r.string(6);
                return Ok(SteamPollResult::Authenticated {
                    account_name,
                    refresh_token,
                });
            }
            let new_client = r.varint(1);
            let new_url = r.string(2);
            Ok(SteamPollResult::Waiting {
                new_challenge_url: if new_url.is_empty() {
                    None
                } else {
                    Some(new_url)
                },
                new_client_id: if new_client == 0 {
                    None
                } else {
                    Some(new_client)
                },
            })
        }
        Err(e) if e.contains("x-eresult=9") => Ok(SteamPollResult::Expired),
        Err(e) => Err(e),
    }
}

#[cfg(windows)]
pub fn steam_get_rsa_key(account_name: &str) -> Result<SteamRsaKeyInfo, String> {
    let started = Instant::now();
    let mut w = ProtoWriter::new();
    w.string(1, account_name.trim());
    let (body, _) = steam_proto_call("GET", "GetPasswordRSAPublicKey", &w.finish())?;
    let r = ProtoReader::parse(&body);
    let modulus = r.string(1);
    let exponent = r.string(2);
    let timestamp = r.varint(3);
    if modulus.is_empty() || exponent.is_empty() {
        return Err("Steam RSA key missing modulus or exponent".to_string());
    }
    Ok(SteamRsaKeyInfo {
        modulus_hex_len: modulus.len(),
        exponent_hex: exponent,
        timestamp,
        latency_ms: started.elapsed().as_secs_f64() * 1000.0,
        modulus_hex: modulus,
    })
}

#[cfg(windows)]
pub fn steam_begin_credentials(
    device_name: &str,
    account_name: &str,
    password: &str,
) -> Result<SteamCredentialsStart, String> {
    let started = Instant::now();
    let key = steam_get_rsa_key(account_name)?;
    let encrypted = rsa_encrypt_pkcs1(&key.modulus_hex, &key.exponent_hex, password.as_bytes())?;
    let machine_id = csprng_bytes(16)?;
    let details = steam_device_details(device_name, &machine_id);
    let mut w = ProtoWriter::new();
    w.string(1, device_name)
        .string(2, account_name.trim())
        .string(3, &encrypted)
        .varint(4, key.timestamp)
        .bool_field(5, true)
        .int32(6, PLATFORM_STEAM_CLIENT)
        .int32(7, PERSISTENCE_PERSISTENT)
        .string(8, WEBSITE_ID)
        .bytes(9, &details);
    let (body, _) = steam_proto_call("POST", "BeginAuthSessionViaCredentials", &w.finish())?;
    let r = ProtoReader::parse(&body);
    let client_id = r.varint(1);
    let request_id = r.bytes(2);
    let interval_secs = r.float(3).clamp(2.0, 15.0);
    let steam_id = r.varint(5);
    let mut guard_kind = "none".to_string();
    for c_bytes in r.messages(4) {
        let cr = ProtoReader::parse(c_bytes);
        let kind = cr.varint(1);
        guard_kind = match kind {
            2 => "email_code".to_string(),
            3 => "device_code".to_string(),
            4 => "device_confirmation".to_string(),
            5 => "email_confirmation".to_string(),
            _ => continue,
        };
        break;
    }
    Ok(SteamCredentialsStart {
        client_id,
        steam_id,
        request_id,
        interval_secs,
        guard_kind,
        latency_ms: started.elapsed().as_secs_f64() * 1000.0,
    })
}

#[cfg(windows)]
pub fn steam_submit_guard_code(
    client_id: u64,
    steam_id: u64,
    code: &str,
    guard_kind: &str,
) -> Result<(), String> {
    let kind_id = match guard_kind {
        "email_code" => 2,
        "device_code" => 3,
        _ => return Err("active session is not awaiting a typed Steam Guard code".to_string()),
    };
    let mut w = ProtoWriter::new();
    w.varint(1, client_id)
        .fixed64(2, steam_id)
        .string(3, &code.trim().to_uppercase())
        .int32(4, kind_id);
    let _ = steam_proto_call("POST", "UpdateAuthSessionWithSteamGuardCode", &w.finish())?;
    Ok(())
}
