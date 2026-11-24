use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FrameLatencySample {
    pub raw_input_qpc: u64,
    pub reflex_sim_start_qpc: Option<u64>,
    pub present_start_qpc: u64,
    pub display_photon_qpc: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClickToPhotonSummary {
    pub sample_count: usize,
    pub p50_ms: f64,
    pub p95_ms: f64,
    pub max_ms: f64,
    pub jitter_ms: f64,
    pub avg_input_to_present_ms: f64,
    pub avg_present_to_photon_ms: f64,
}

pub fn summarize_click_to_photon(
    samples: &[FrameLatencySample],
    qpc_freq_hz: u64,
) -> Option<ClickToPhotonSummary> {
    if samples.is_empty() || qpc_freq_hz == 0 {
        return None;
    }
    let ticks_to_ms = |t: u64| (t as f64) * 1000.0 / (qpc_freq_hz as f64);
    let mut totals = Vec::with_capacity(samples.len());
    let mut in_to_pres_sum = 0.0f64;
    let mut pres_to_phot_sum = 0.0f64;

    for s in samples {
        if s.present_start_qpc < s.raw_input_qpc || s.display_photon_qpc < s.present_start_qpc {
            continue;
        }
        let in_to_pres = ticks_to_ms(s.present_start_qpc - s.raw_input_qpc);
        let pres_to_phot = ticks_to_ms(s.display_photon_qpc - s.present_start_qpc);
        totals.push(in_to_pres + pres_to_phot);
        in_to_pres_sum += in_to_pres;
        pres_to_phot_sum += pres_to_phot;
    }
    if totals.is_empty() {
        return None;
    }
    totals.sort_by(|a, b| a.total_cmp(b));
    let n = totals.len();
    let p50 = totals[n / 2];
    let p95 = totals[((n as f64 * 0.95) as usize).min(n - 1)];
    let max_ms = totals[n - 1];
    let mean = totals.iter().sum::<f64>() / (n as f64);
    let var = totals.iter().map(|v| (v - mean).powi(2)).sum::<f64>() / (n as f64);
    Some(ClickToPhotonSummary {
        sample_count: n,
        p50_ms: p50,
        p95_ms: p95,
        max_ms,
        jitter_ms: var.sqrt(),
        avg_input_to_present_ms: in_to_pres_sum / (n as f64),
        avg_present_to_photon_ms: pres_to_phot_sum / (n as f64),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn correlates_raw_input_present_and_photon_with_sub_0_2ms_jitter() {
        let freq = 10_000_000u64;
        let samples: Vec<FrameLatencySample> = (0..200u64)
            .map(|i| {
                let base = 1_000_000 + i * 166_667;
                let jitter = (i % 3) * 200;
                FrameLatencySample {
                    raw_input_qpc: base,
                    reflex_sim_start_qpc: Some(base + 12_000),
                    present_start_qpc: base + 65_000 + jitter,
                    display_photon_qpc: base + 115_000 + jitter,
                }
            })
            .collect();
        let sum = summarize_click_to_photon(&samples, freq).expect("summary");
        assert_eq!(sum.sample_count, 200);
        assert!((sum.p50_ms - 11.52).abs() < 0.1);
        assert!(sum.jitter_ms < 0.2);
    }
}
