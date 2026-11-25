use crate::model::{MacroAction, MacroDef, MacroDelivery};
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum OnboardTarget {
    OpenMouse,
    WootingHid,
    DualSenseEdge,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CompiledOnboardMacro {
    pub binding_id: String,
    pub target: OnboardTarget,
    pub trigger_code: String,
    pub hid_usage: u16,
    pub payload_preview: Vec<u8>,
}

pub fn compile_onboard_remap(
    binding: &MacroDef,
    target: OnboardTarget,
) -> Result<CompiledOnboardMacro, &'static str> {
    if binding.delivery != MacroDelivery::OnboardHardware {
        return Err("Only Tier 1 onboard hardware bindings can compile to device memory.");
    }
    if binding.trigger.chord.len() != 1 {
        return Err("Onboard device remaps require a single trigger input.");
    }
    let trigger_code = binding.trigger.chord[0].code.clone();
    let target_code = binding
        .steps
        .iter()
        .find_map(|s| match &s.action {
            MacroAction::KeyDown { code } => Some(code.as_str()),
            MacroAction::MouseDown { button } => Some(button.as_str()),
            MacroAction::PadButtonDown { button } => Some(button.as_str()),
            _ => None,
        })
        .ok_or("Onboard remap needs at least one key, mouse, or controller button action.")?;

    let hid_usage = hid_usage_for_code(target_code);
    let mut payload = Vec::with_capacity(8);
    match target {
        OnboardTarget::OpenMouse => payload.extend_from_slice(&[0xA1, 0x01]),
        OnboardTarget::WootingHid => payload.extend_from_slice(&[0xD1, 0xDA, 0x11]),
        OnboardTarget::DualSenseEdge => payload.extend_from_slice(&[0x70, 0x00]),
    }
    payload.extend_from_slice(&hid_usage.to_le_bytes());

    Ok(CompiledOnboardMacro {
        binding_id: binding.id.clone(),
        target,
        trigger_code,
        hid_usage,
        payload_preview: payload,
    })
}

pub fn hid_usage_for_code(code: &str) -> u16 {
    match code.to_ascii_uppercase().as_str() {
        "A" => 0x04,
        "E" => 0x08,
        "SPACE" => 0x2C,
        "F10" => 0x43,
        "F13" => 0x68,
        "F14" => 0x69,
        "F15" => 0x6A,
        "SHIFTLEFT" | "LSHIFT" => 0xE1,
        "MOUSE4" => 0x0104,
        "MOUSE5" => 0x0105,
        _ => 0x0068,
    }
}
