use serde::{Deserialize, Serialize};
use std::collections::HashMap;

pub const ISR_WARN_US: f64 = 25.0;
pub const DPC_WARN_US: f64 = 100.0;
pub const AUDIO_DROPOUT_DANGER_US: f64 = 1000.0;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum KernelEventKind {
    Isr,
    Dpc,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverRegion {
    pub name: String,
    pub base: u64,
    pub size: u64,
}

#[derive(Debug, Clone, Default)]
pub struct DriverMap {
    regions: Vec<DriverRegion>,
}

impl DriverMap {
    pub fn new(mut regions: Vec<DriverRegion>) -> Self {
        regions.sort_by_key(|r| r.base);
        Self { regions }
    }

    pub fn resolve(&self, addr: u64) -> &str {
        let idx = self.regions.partition_point(|r| r.base <= addr);
        if idx > 0 {
            let r = &self.regions[idx - 1];
            if addr >= r.base && addr < r.base.saturating_add(r.size) {
                return &r.name;
            }
        }
        "unknown.sys"
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ParsedPerfInfoRecord {
    pub kind: KernelEventKind,
    pub initial_qpc: u64,
    pub end_qpc: u64,
    pub routine_addr: u64,
    pub duration_us: f64,
}

pub fn parse_perfinfo_record(
    kind: KernelEventKind,
    payload: &[u8],
    end_qpc: u64,
    qpc_freq_hz: u64,
    is_64bit: bool,
) -> Option<ParsedPerfInfoRecord> {
    if qpc_freq_hz == 0 {
        return None;
    }
    let min_len = if is_64bit { 16 } else { 12 };
    if payload.len() < min_len {
        return None;
    }
    let initial_qpc = u64::from_le_bytes(payload[0..8].try_into().ok()?);
    if end_qpc < initial_qpc {
        return None;
    }
    let routine_addr = if is_64bit {
        u64::from_le_bytes(payload[8..16].try_into().ok()?)
    } else {
        u32::from_le_bytes(payload[8..12].try_into().ok()?) as u64
    };
    let ticks = end_qpc - initial_qpc;
    let duration_us = (ticks as f64) * 1_000_000.0 / (qpc_freq_hz as f64);
    if !duration_us.is_finite() || duration_us > 60_000_000.0 {
        return None;
    }
    Some(ParsedPerfInfoRecord {
        kind,
        initial_qpc,
        end_qpc,
        routine_addr,
        duration_us,
    })
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverLatencyStats {
    pub driver: String,
    pub count: u64,
    pub max_us: f64,
    pub total_us: f64,
    pub exceeded_guidance: bool,
    pub audio_dropout_risk: bool,
}

#[derive(Debug, Default)]
pub struct DpcIsrAggregator {
    by_driver: HashMap<String, DriverLatencyStats>,
    pub processed: u64,
    pub rejected_malformed: u64,
}

impl DpcIsrAggregator {
    pub fn ingest(&mut self, rec: Option<ParsedPerfInfoRecord>, map: &DriverMap) {
        let Some(r) = rec else {
            self.rejected_malformed += 1;
            return;
        };
        self.processed += 1;
        let driver = map.resolve(r.routine_addr);
        let warn_limit = match r.kind {
            KernelEventKind::Isr => ISR_WARN_US,
            KernelEventKind::Dpc => DPC_WARN_US,
        };
        let entry =
            self.by_driver
                .entry(driver.to_string())
                .or_insert_with(|| DriverLatencyStats {
                    driver: driver.to_string(),
                    count: 0,
                    max_us: 0.0,
                    total_us: 0.0,
                    exceeded_guidance: false,
                    audio_dropout_risk: false,
                });
        entry.count += 1;
        entry.total_us += r.duration_us;
        if r.duration_us > entry.max_us {
            entry.max_us = r.duration_us;
        }
        if r.duration_us > warn_limit {
            entry.exceeded_guidance = true;
        }
        if r.duration_us > AUDIO_DROPOUT_DANGER_US {
            entry.audio_dropout_risk = true;
        }
    }

    pub fn top_offenders(&self, limit: usize) -> Vec<DriverLatencyStats> {
        let mut list: Vec<DriverLatencyStats> = self.by_driver.values().cloned().collect();
        list.sort_by(|a, b| b.max_us.total_cmp(&a.max_us));
        list.truncate(limit);
        list
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_malformed_truncated_and_backward_qpc_records_without_panic() {
        assert!(parse_perfinfo_record(KernelEventKind::Dpc, &[], 100, 10_000_000, true).is_none());
        assert!(
            parse_perfinfo_record(KernelEventKind::Dpc, &[0u8; 10], 100, 10_000_000, true)
                .is_none()
        );
        assert!(parse_perfinfo_record(KernelEventKind::Dpc, &[0u8; 16], 100, 0, true).is_none());

        let mut backward = [0u8; 16];
        backward[0..8].copy_from_slice(&5000u64.to_le_bytes());
        backward[8..16].copy_from_slice(&0xFFFF_0000_1000u64.to_le_bytes());
        assert!(
            parse_perfinfo_record(KernelEventKind::Dpc, &backward, 4000, 10_000_000, true)
                .is_none()
        );
    }

    #[test]
    fn parses_32bit_and_64bit_records_and_resolves_driver_names() {
        let map = DriverMap::new(vec![
            DriverRegion {
                name: "nvlddmkm.sys".into(),
                base: 0xFFFF_F800_1000_0000,
                size: 0x0200_0000,
            },
            DriverRegion {
                name: "rt25cx21x64.sys".into(),
                base: 0x1000_0000,
                size: 0x0010_0000,
            },
        ]);

        let mut p64 = [0u8; 16];
        p64[0..8].copy_from_slice(&1_000u64.to_le_bytes());
        p64[8..16].copy_from_slice(&0xFFFF_F800_1000_1234u64.to_le_bytes());
        let r64 = parse_perfinfo_record(KernelEventKind::Dpc, &p64, 15_200, 10_000_000, true)
            .expect("valid 64-bit");
        assert!((r64.duration_us - 1420.0).abs() < 0.01);

        let mut p32 = [0u8; 12];
        p32[0..8].copy_from_slice(&2_000u64.to_le_bytes());
        p32[8..12].copy_from_slice(&0x1000_0400u32.to_le_bytes());
        let r32 = parse_perfinfo_record(KernelEventKind::Isr, &p32, 2_300, 10_000_000, false)
            .expect("valid 32-bit");
        assert!((r32.duration_us - 30.0).abs() < 0.01);

        let mut agg = DpcIsrAggregator::default();
        agg.ingest(Some(r64), &map);
        agg.ingest(Some(r32), &map);
        let top = agg.top_offenders(2);
        assert_eq!(top[0].driver, "nvlddmkm.sys");
        assert!(top[0].audio_dropout_risk);
        assert_eq!(top[1].driver, "rt25cx21x64.sys");
        assert!(top[1].exceeded_guidance);
    }
}
