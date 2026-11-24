use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BufferbloatGrade {
    APlus,
    A,
    B,
    C,
    D,
    F,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BufferbloatReport {
    pub unloaded_p50_ms: f64,
    pub loaded_p95_ms: f64,
    pub bloat_delta_ms: f64,
    pub grade: BufferbloatGrade,
    pub local_hop_culprit: bool,
}

pub fn grade_bufferbloat(
    unloaded_samples_ms: &[f64],
    loaded_samples_ms: &[f64],
    gateway_loaded_p95_ms: Option<f64>,
) -> Option<BufferbloatReport> {
    if unloaded_samples_ms.is_empty() || loaded_samples_ms.is_empty() {
        return None;
    }
    let mut u = unloaded_samples_ms.to_vec();
    let mut l = loaded_samples_ms.to_vec();
    u.sort_by(|a, b| a.total_cmp(b));
    l.sort_by(|a, b| a.total_cmp(b));
    let unloaded_p50_ms = u[u.len() / 2];
    let loaded_p95_ms = l[((l.len() as f64 * 0.95) as usize).min(l.len() - 1)];
    let bloat_delta_ms = (loaded_p95_ms - unloaded_p50_ms).max(0.0);
    let grade = if bloat_delta_ms < 5.0 {
        BufferbloatGrade::APlus
    } else if bloat_delta_ms < 15.0 {
        BufferbloatGrade::A
    } else if bloat_delta_ms < 30.0 {
        BufferbloatGrade::B
    } else if bloat_delta_ms < 60.0 {
        BufferbloatGrade::C
    } else if bloat_delta_ms < 120.0 {
        BufferbloatGrade::D
    } else {
        BufferbloatGrade::F
    };
    let local_hop_culprit = gateway_loaded_p95_ms.is_some_and(|gw| gw > 15.0);
    Some(BufferbloatReport {
        unloaded_p50_ms,
        loaded_p95_ms,
        bloat_delta_ms,
        grade,
        local_hop_culprit,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn grades_clean_and_bloated_paths_and_flags_local_gateway() {
        let clean = grade_bufferbloat(&[12.0, 12.2, 12.5], &[13.0, 14.0, 15.0], Some(2.0)).unwrap();
        assert_eq!(clean.grade, BufferbloatGrade::APlus);
        assert!(!clean.local_hop_culprit);

        let bloated = grade_bufferbloat(&[12.0, 12.2], &[85.0, 150.0], Some(90.0)).unwrap();
        assert_eq!(bloated.grade, BufferbloatGrade::F);
        assert!(bloated.local_hop_culprit);
    }
}
