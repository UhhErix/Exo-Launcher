use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CpuSetInfo {
    pub id: u32,
    pub group: u16,
    pub core_index: u8,
    pub last_level_cache_index: u8,
    pub efficiency_class: u8,
    pub logical_processor_index: u8,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum CpuTopologyKind {
    IntelHybrid,
    AmdDualCcdX3d,
    AmdDualCcd,
    Monolithic,
    LowCoreNoOp,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TopologyPlan {
    pub kind: CpuTopologyKind,
    pub total_threads: usize,
    pub background_set_ids: Vec<u32>,
    pub summary: String,
}

pub fn is_amd_dual_ccd_x3d_brand(brand: &str) -> bool {
    let upper = brand.to_ascii_uppercase();
    upper.contains("7950X3D")
        || upper.contains("7900X3D")
        || upper.contains("9950X3D")
        || upper.contains("9900X3D")
        || (upper.contains("RYZEN")
            && upper.contains("X3D")
            && (upper.contains(" 9 ") || upper.contains("79") || upper.contains("99")))
}

pub fn classify_topology(
    sets: &[CpuSetInfo],
    cpu_brand: &str,
    vcache_mask: Option<u64>,
) -> TopologyPlan {
    let group0: Vec<CpuSetInfo> = sets.iter().copied().filter(|s| s.group == 0).collect();
    let total_threads = group0.len();
    if total_threads < 6 {
        return TopologyPlan {
            kind: CpuTopologyKind::LowCoreNoOp,
            total_threads,
            background_set_ids: Vec::new(),
            summary: format!(
                "CPU has {total_threads} threads (<6); background CPU Set corral safely disabled."
            ),
        };
    }

    let max_eff = group0.iter().map(|s| s.efficiency_class).max().unwrap_or(0);
    let min_eff = group0.iter().map(|s| s.efficiency_class).min().unwrap_or(0);
    if max_eff > min_eff {
        let mut e_cores: Vec<u32> = group0
            .iter()
            .filter(|s| s.efficiency_class < max_eff)
            .map(|s| s.id)
            .collect();
        e_cores.sort_unstable();
        if !e_cores.is_empty() {
            let count = e_cores.len();
            return TopologyPlan {
                kind: CpuTopologyKind::IntelHybrid,
                total_threads,
                background_set_ids: e_cores,
                summary: format!(
                    "Intel Hybrid detected: background hogs corralled onto {count} E-core threads via CPU Sets, keeping P-cores clear."
                ),
            };
        }
    }

    let mut llc_groups: BTreeMap<u8, Vec<CpuSetInfo>> = BTreeMap::new();
    for s in &group0 {
        llc_groups
            .entry(s.last_level_cache_index)
            .or_default()
            .push(*s);
    }

    if llc_groups.len() >= 2 {
        let x3d = vcache_mask.is_some() || is_amd_dual_ccd_x3d_brand(cpu_brand);
        if x3d {
            let ids: Vec<u32> = if let Some(mask) = vcache_mask {
                group0
                    .iter()
                    .filter(|s| {
                        s.logical_processor_index < 64
                            && ((mask >> s.logical_processor_index) & 1) == 0
                    })
                    .map(|s| s.id)
                    .collect()
            } else {
                llc_groups
                    .iter()
                    .next_back()
                    .map(|(_, list)| list.iter().map(|s| s.id).collect())
                    .unwrap_or_default()
            };
            if !ids.is_empty() {
                let count = ids.len();
                return TopologyPlan {
                    kind: CpuTopologyKind::AmdDualCcdX3d,
                    total_threads,
                    background_set_ids: ids,
                    summary: format!(
                        "AMD Dual-CCD X3D detected: background hogs corralled onto {count} non-V-Cache CCD threads via CPU Sets."
                    ),
                };
            }
        }

        let secondary: Vec<u32> = llc_groups
            .iter()
            .next_back()
            .map(|(_, list)| list.iter().map(|s| s.id).collect())
            .unwrap_or_default();
        if !secondary.is_empty() {
            let count = secondary.len();
            return TopologyPlan {
                kind: CpuTopologyKind::AmdDualCcd,
                total_threads,
                background_set_ids: secondary,
                summary: format!(
                    "Dual-CCD detected: background hogs corralled onto {count} secondary-CCD threads via CPU Sets."
                ),
            };
        }
    }

    let mut non_zero: Vec<CpuSetInfo> = group0
        .iter()
        .copied()
        .filter(|s| s.core_index != 0 && s.logical_processor_index != 0)
        .collect();
    non_zero.sort_by_key(|s| s.logical_processor_index);
    if non_zero.len() < 2 {
        return TopologyPlan {
            kind: CpuTopologyKind::LowCoreNoOp,
            total_threads,
            background_set_ids: Vec::new(),
            summary: "Insufficient non-Core-0 threads; CPU Set corral disabled.".to_string(),
        };
    }

    let take = (total_threads / 4).clamp(2, non_zero.len());
    let start = non_zero.len().saturating_sub(take);
    let ids: Vec<u32> = non_zero[start..].iter().map(|s| s.id).collect();
    let count = ids.len();
    TopologyPlan {
        kind: CpuTopologyKind::Monolithic,
        total_threads,
        background_set_ids: ids,
        summary: format!(
            "Monolithic {total_threads}-thread CPU: background hogs corralled onto {count} trailing non-Core-0 threads via CPU Sets."
        ),
    }
}
