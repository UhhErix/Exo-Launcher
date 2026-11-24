use serde::{Deserialize, Serialize};
use std::net::UdpSocket;
use std::time::{Duration, Instant};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DnsResolverResult {
    pub name: String,
    pub ip: String,
    pub succeeded: usize,
    pub attempted: usize,
    pub p50_ms: Option<f64>,
    pub p95_ms: Option<f64>,
    pub jitter_ms: Option<f64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DnsRaceReport {
    pub total_elapsed_ms: f64,
    pub winner: Option<String>,
    pub resolvers: Vec<DnsResolverResult>,
}

pub fn build_dns_a_query(txid: u16, domain: &str) -> Vec<u8> {
    let mut pkt = Vec::with_capacity(64);
    pkt.extend_from_slice(&txid.to_be_bytes());
    pkt.extend_from_slice(&0x0100u16.to_be_bytes());
    pkt.extend_from_slice(&1u16.to_be_bytes());
    pkt.extend_from_slice(&0u16.to_be_bytes());
    pkt.extend_from_slice(&0u16.to_be_bytes());
    pkt.extend_from_slice(&0u16.to_be_bytes());
    for label in domain.split('.') {
        let bytes = label.as_bytes();
        pkt.push(bytes.len().min(63) as u8);
        pkt.extend_from_slice(&bytes[..bytes.len().min(63)]);
    }
    pkt.push(0);
    pkt.extend_from_slice(&1u16.to_be_bytes());
    pkt.extend_from_slice(&1u16.to_be_bytes());
    pkt
}

pub fn race_dns_resolvers(queries_per_resolver: usize, timeout: Duration) -> DnsRaceReport {
    let targets = [
        ("Cloudflare", "1.1.1.1"),
        ("Google", "8.8.8.8"),
        ("Quad9", "9.9.9.9"),
    ];
    let start = Instant::now();
    let results: Vec<DnsResolverResult> = std::thread::scope(|s| {
        let handles: Vec<_> = targets
            .iter()
            .enumerate()
            .map(|(idx, &(name, ip))| {
                s.spawn(move || probe_resolver(name, ip, idx as u16, queries_per_resolver, timeout))
            })
            .collect();
        handles.into_iter().map(|h| h.join().unwrap()).collect()
    });
    let winner = results
        .iter()
        .filter_map(|r| r.p50_ms.map(|p| (r.name.clone(), p)))
        .min_by(|a, b| a.1.total_cmp(&b.1))
        .map(|(n, _)| n);
    DnsRaceReport {
        total_elapsed_ms: start.elapsed().as_secs_f64() * 1000.0,
        winner,
        resolvers: results,
    }
}

fn probe_resolver(
    name: &str,
    ip: &str,
    seed: u16,
    queries: usize,
    timeout: Duration,
) -> DnsResolverResult {
    let mut rtts = Vec::with_capacity(queries);
    let addr = format!("{ip}:53");
    if let Ok(sock) = UdpSocket::bind("0.0.0.0:0") {
        let _ = sock.set_read_timeout(Some(timeout));
        let _ = sock.set_write_timeout(Some(timeout));
        let mut resp = [0u8; 512];
        for i in 0..queries {
            let txid = 0x4500u16.wrapping_add(seed * 100 + i as u16);
            let q = build_dns_a_query(txid, "example.com");
            let t0 = Instant::now();
            if sock.send_to(&q, &addr).is_ok()
                && let Ok((n, _)) = sock.recv_from(&mut resp)
                && n >= 12
                && resp[0..2] == txid.to_be_bytes()
            {
                rtts.push(t0.elapsed().as_secs_f64() * 1000.0);
            }
        }
    }
    rtts.sort_by(|a, b| a.total_cmp(b));
    let succeeded = rtts.len();
    let (p50_ms, p95_ms, jitter_ms) = if succeeded > 0 {
        let p50 = rtts[succeeded / 2];
        let p95 = rtts[((succeeded as f64 * 0.95) as usize).min(succeeded - 1)];
        let mean = rtts.iter().sum::<f64>() / (succeeded as f64);
        let var = rtts.iter().map(|v| (v - mean).powi(2)).sum::<f64>() / (succeeded as f64);
        (Some(p50), Some(p95), Some(var.sqrt()))
    } else {
        (None, None, None)
    };
    DnsResolverResult {
        name: name.into(),
        ip: ip.into(),
        succeeded,
        attempted: queries,
        p50_ms,
        p95_ms,
        jitter_ms,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builds_valid_rfc1035_dns_query_packet() {
        let pkt = build_dns_a_query(0x1234, "example.com");
        assert_eq!(&pkt[0..2], &[0x12, 0x34]);
        assert_eq!(&pkt[4..6], &[0x00, 0x01]);
        assert_eq!(pkt[12], 7);
        assert_eq!(&pkt[13..20], b"example");
        assert_eq!(pkt[20], 3);
        assert_eq!(&pkt[21..24], b"com");
        assert_eq!(pkt[24], 0);
    }
}
