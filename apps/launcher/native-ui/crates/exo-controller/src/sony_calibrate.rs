use crate::sony_hid::{
    CENTER_BEGIN, CENTER_END, CENTER_SAMPLE, FINETUNE_WRITE, FIRST_SLOT_REPORT, NV_LOCK, NV_UNLOCK,
    RANGE_BEGIN, RANGE_END, is_feature_allowed,
};
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NvState {
    Locked,
    Unlocked,
    Reconnect,
    Unknown,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct EdgeSlotSummary {
    pub slot_number: u8,
    pub empty: bool,
    pub name: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct EdgeOnboardProfile {
    pub slot_number: u8,
    pub name: String,
    pub left_stick_curve: u8,
    pub right_stick_curve: u8,
    pub left_deadzone_pct: u8,
    pub right_deadzone_pct: u8,
    pub back_left_remap: u8,
    pub back_right_remap: u8,
}

impl EdgeOnboardProfile {
    pub fn to_slot_report(&self) -> Result<[u8; 64], &'static str> {
        if !(1..=4).contains(&self.slot_number) {
            return Err("DualSense Edge slot must be 1..=4.");
        }
        let mut buf = [0u8; 64];
        buf[0] = FIRST_SLOT_REPORT + (self.slot_number - 1) * 3;
        buf[1] = 0x00;
        buf[2] = self.left_stick_curve;
        buf[3] = self.right_stick_curve;
        buf[4] = self.left_deadzone_pct.min(50);
        buf[5] = self.right_deadzone_pct.min(50);
        let mut cursor = 6;
        for ch in self.name.encode_utf16().take(24) {
            let [lo, hi] = ch.to_le_bytes();
            buf[cursor] = lo;
            buf[cursor + 1] = hi;
            cursor += 2;
        }
        buf[56] = self.back_left_remap;
        buf[57] = self.back_right_remap;
        Ok(buf)
    }

    pub fn from_slot_report(slot_number: u8, report: &[u8]) -> Option<Self> {
        let summary = parse_edge_slot(slot_number, report)?;
        if summary.empty || report.len() < 58 {
            return None;
        }
        Some(Self {
            slot_number,
            name: summary.name.unwrap_or_default(),
            left_stick_curve: report[2],
            right_stick_curve: report[3],
            left_deadzone_pct: report[4],
            right_deadzone_pct: report[5],
            back_left_remap: report[56],
            back_right_remap: report[57],
        })
    }
}

pub fn parse_edge_slot(slot_number: u8, first: &[u8]) -> Option<EdgeSlotSummary> {
    if !(1..=4).contains(&slot_number)
        || first.len() < 8
        || first[0] != FIRST_SLOT_REPORT + (slot_number - 1) * 3
    {
        return None;
    }
    if first[1] == 0x10 {
        return Some(EdgeSlotSummary {
            slot_number,
            empty: true,
            name: None,
        });
    }
    let mut utf16 = Vec::new();
    let mut i = 6;
    while i + 1 < first.len().min(54) {
        let unit = u16::from_le_bytes([first[i], first[i + 1]]);
        if unit == 0 {
            break;
        }
        if unit < 0x20 {
            return Some(EdgeSlotSummary {
                slot_number,
                empty: false,
                name: None,
            });
        }
        utf16.push(unit);
        i += 2;
    }
    let name = String::from_utf16(&utf16).ok().filter(|s| !s.is_empty());
    Some(EdgeSlotSummary {
        slot_number,
        empty: false,
        name,
    })
}

pub fn calibration_answer(reply: &[u8]) -> u32 {
    if reply.len() < 4 {
        return 0;
    }
    u32::from_be_bytes([reply[0], reply[1], reply[2], reply[3]])
}

pub fn calibration_ended(answer: u32, target: u8, edge: bool, second: bool) -> bool {
    if edge {
        if second {
            answer == (0x8301_0003 | (u32::from(target) << 8))
                || (target == 1 && answer == 0x8301_0312)
        } else {
            answer == (0x8301_0001 | (u32::from(target) << 8))
        }
    } else {
        answer == (0x8301_0002 | (u32::from(target) << 8))
    }
}

pub fn parse_finetune(reply: &[u8]) -> Option<[u16; 12]> {
    if reply.len() < 28
        || reply[0] != 0x81
        || reply[1] != 0x0C
        || !matches!(reply[2], 0x02 | 0x04)
        || reply[3] != 0x02
    {
        return None;
    }
    let mut values = [0u16; 12];
    for (i, slot) in values.iter_mut().enumerate() {
        let off = 4 + i * 2;
        *slot = u16::from_le_bytes([reply[off], reply[off + 1]]);
    }
    Some(values)
}

pub fn encode_finetune_payload(values: &[u16; 12]) -> [u8; 26] {
    let mut out = [0u8; 26];
    out[0..2].copy_from_slice(&FINETUNE_WRITE);
    for (i, &v) in values.iter().enumerate() {
        let off = 2 + i * 2;
        out[off..off + 2].copy_from_slice(&v.to_le_bytes());
    }
    out
}

pub fn parse_nv_state(reply: &[u8]) -> NvState {
    if reply.len() < 5 {
        return NvState::Unknown;
    }
    match u32::from_be_bytes([reply[1], reply[2], reply[3], reply[4]]) {
        0x0303_0201 => NvState::Locked,
        0x0303_0200 => NvState::Unlocked,
        0x1501_0100 => NvState::Reconnect,
        _ => NvState::Unknown,
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CalibrationStep {
    Idle,
    CenterSampling,
    RangeSampling,
    ReadyToCommit,
    Committed,
}

#[derive(Clone, Debug)]
pub struct CalibrationSession {
    pub usb_wired: bool,
    pub edge: bool,
    pub user_confirmed_flash: bool,
    pub step: CalibrationStep,
    pub commands_emitted: Vec<(u8, Vec<u8>)>,
}

impl CalibrationSession {
    pub fn new(usb_wired: bool, edge: bool) -> Self {
        Self {
            usb_wired,
            edge,
            user_confirmed_flash: false,
            step: CalibrationStep::Idle,
            commands_emitted: Vec::new(),
        }
    }

    pub fn begin_center(&mut self) -> Result<(), &'static str> {
        self.require_usb()?;
        self.emit(0x82, &CENTER_BEGIN)?;
        self.step = CalibrationStep::CenterSampling;
        Ok(())
    }

    pub fn sample_center(&mut self) -> Result<(), &'static str> {
        if self.step != CalibrationStep::CenterSampling {
            return Err("Center calibration has not started.");
        }
        self.emit(0x82, &CENTER_SAMPLE)
    }

    pub fn end_center_and_begin_range(&mut self) -> Result<(), &'static str> {
        if self.step != CalibrationStep::CenterSampling {
            return Err("Center calibration has not started.");
        }
        self.emit(0x82, &CENTER_END)?;
        self.emit(0x82, &RANGE_BEGIN)?;
        self.step = CalibrationStep::RangeSampling;
        Ok(())
    }

    pub fn end_range(&mut self) -> Result<(), &'static str> {
        if self.step != CalibrationStep::RangeSampling {
            return Err("Range calibration has not started.");
        }
        self.emit(0x82, &RANGE_END)?;
        self.step = CalibrationStep::ReadyToCommit;
        Ok(())
    }

    pub fn commit_permanent_flash(&mut self) -> Result<(), &'static str> {
        self.require_usb()?;
        if self.edge {
            return Err(
                "DualSense Edge stick modules are write-protected; permanent flash commit is refused.",
            );
        }
        if !self.user_confirmed_flash {
            return Err(
                "Explicit user confirmation is required before committing calibration to controller flash.",
            );
        }
        if self.step != CalibrationStep::ReadyToCommit {
            return Err("Complete center and range calibration before committing.");
        }
        self.emit(0x80, &NV_UNLOCK)?;
        self.emit(0x80, &NV_LOCK)?;
        self.step = CalibrationStep::Committed;
        Ok(())
    }

    fn require_usb(&self) -> Result<(), &'static str> {
        if !self.usb_wired {
            return Err(
                "Permanent calibration requires a USB wired connection (never over Bluetooth).",
            );
        }
        Ok(())
    }

    fn emit(&mut self, report_id: u8, payload: &[u8]) -> Result<(), &'static str> {
        if !is_feature_allowed(report_id, payload, true, self.edge) {
            return Err("Feature report rejected by DualSense command allowlist.");
        }
        self.commands_emitted.push((report_id, payload.to_vec()));
        Ok(())
    }
}
