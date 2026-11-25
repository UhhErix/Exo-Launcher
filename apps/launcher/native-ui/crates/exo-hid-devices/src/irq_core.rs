use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CpuCoreDescriptor {
    pub logical_index: u32,
    pub physical_core: u32,
    pub ccd_index: u8,
    pub is_efficiency_core: bool,
    pub is_smt_sibling: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct IrqAffinityPlan {
    pub controller_id: String,
    pub recommended_logical_cpu: u32,
    pub affinity_mask: u64,
    pub reason: String,
}

pub fn plan_xhci_irq_affinity(
    controller_id: &str,
    cores: &[CpuCoreDescriptor],
) -> Option<IrqAffinityPlan> {
    if cores.is_empty() {
        return None;
    }

    let candidate = cores
        .iter()
        .filter(|c| c.logical_index != 0 && !c.is_smt_sibling && c.logical_index < 64)
        .min_by_key(|c| (c.is_efficiency_core as u8, c.ccd_index, c.logical_index))
        .or_else(|| {
            cores
                .iter()
                .filter(|c| c.logical_index != 0 && c.logical_index < 64)
                .min_by_key(|c| c.logical_index)
        })
        .unwrap_or(&cores[0]);

    let mask = 1u64.checked_shl(candidate.logical_index).unwrap_or(1);
    let reason = if candidate.logical_index == 0 {
        "Single-core fallback on CPU 0.".into()
    } else {
        format!(
            "Moves USB xHCI interrupts off Core 0 onto physical core {} (logical CPU {}, CCD {}).",
            candidate.physical_core, candidate.logical_index, candidate.ccd_index
        )
    };

    Some(IrqAffinityPlan {
        controller_id: controller_id.to_string(),
        recommended_logical_cpu: candidate.logical_index,
        affinity_mask: mask,
        reason,
    })
}
