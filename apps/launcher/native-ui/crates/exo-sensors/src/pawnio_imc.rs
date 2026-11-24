use std::path::Path;

use crate::win32::DynLib;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ImcTimings {
    pub fclk_mhz: u32,
    pub uclk_mhz: u32,
    pub mclk_mhz: u32,
    pub tcl: u16,
    pub trcd_rd: u16,
    pub trcd_wr: u16,
    pub trp: u16,
    pub tras: u16,
    pub trc: u16,
    pub trfc: u16,
    pub ratio_1_to_1: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PawnIoStatus {
    pub installed: bool,
    pub dll_loadable: bool,
    pub note: String,
    pub timings: Option<ImcTimings>,
}

pub fn probe_pawnio_readonly() -> PawnIoStatus {
    let dll_path = Path::new(r"C:\Program Files\PawnIO\PawnIOLib.dll");
    if !dll_path.is_file() {
        return PawnIoStatus {
            installed: false,
            dll_loadable: false,
            note:
                "Optional WHQL PawnIO not installed; zero-driver SMBIOS/GPU/NVMe/WHEA tier active."
                    .into(),
            timings: None,
        };
    }
    let loadable = DynLib::load_exact_path(dll_path).is_some();
    PawnIoStatus {
        installed: true,
        dll_loadable: loadable,
        note: if loadable {
            "WHQL PawnIO detected (read-only SMU/IMC precision tier available; zero WinRing0)."
                .into()
        } else {
            "PawnIO directory present but PawnIOLib.dll did not load.".into()
        },
        timings: None,
    }
}

pub fn decode_amd_umc_timings(
    fclk_mhz: u32,
    uclk_mhz: u32,
    mclk_mhz: u32,
    reg_dram_cfg_204: u32,
    reg_dram_cfg_208: u32,
    reg_dram_cfg_260: u32,
) -> ImcTimings {
    let tcl = (reg_dram_cfg_204 & 0x3F) as u16;
    let tras = ((reg_dram_cfg_204 >> 8) & 0x7F) as u16;
    let trcd_rd = ((reg_dram_cfg_204 >> 16) & 0x3F) as u16;
    let trcd_wr = ((reg_dram_cfg_204 >> 24) & 0x3F) as u16;
    let trc = (reg_dram_cfg_208 & 0xFF) as u16;
    let trp = ((reg_dram_cfg_208 >> 16) & 0x3F) as u16;
    let trfc = (reg_dram_cfg_260 & 0x7FF) as u16;
    let ratio_1_to_1 = uclk_mhz > 0 && mclk_mhz > 0 && uclk_mhz.abs_diff(mclk_mhz) <= 5;
    ImcTimings {
        fclk_mhz,
        uclk_mhz,
        mclk_mhz,
        tcl,
        trcd_rd,
        trcd_wr,
        trp,
        tras,
        trc,
        trfc,
        ratio_1_to_1,
    }
}
