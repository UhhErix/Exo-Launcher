use std::sync::Arc;

use windows::Win32::Foundation::E_ACCESSDENIED;
use windows::Win32::Graphics::Direct3D11::ID3D11Texture2D;
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, DXGI_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM,
    DXGI_FORMAT_R16G16B16A16_FLOAT,
};
use windows::Win32::Graphics::Dxgi::{
    DXGI_ERROR_ACCESS_LOST, DXGI_ERROR_WAIT_TIMEOUT, DXGI_OUTDUPL_FRAME_INFO, IDXGIOutput5,
    IDXGIOutput6, IDXGIOutputDuplication, IDXGIResource,
};
use windows::core::Interface;

use crate::gpu::{GpuDevice, is_device_lost};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DuplicationAction {
    InspectFrame,
    NoNewFrame,
    RetryOrFallback,
    ResetDevice,
    Fallback,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureTier {
    Duplication,
    Wgc,
    GdiNonBlack,
}

pub fn should_accept_duplication_frame(last_present_time: i64) -> bool {
    last_present_time != 0
}

pub fn classify_duplication_hr(hresult: i32) -> DuplicationAction {
    if hresult >= 0 {
        DuplicationAction::InspectFrame
    } else if hresult == DXGI_ERROR_WAIT_TIMEOUT.0 {
        DuplicationAction::NoNewFrame
    } else if is_device_lost(hresult) {
        DuplicationAction::ResetDevice
    } else if hresult == DXGI_ERROR_ACCESS_LOST.0 || hresult == E_ACCESSDENIED.0 {
        DuplicationAction::RetryOrFallback
    } else {
        DuplicationAction::Fallback
    }
}

pub fn force_opaque_alpha_bgra(pixels: &mut [u8]) {
    for px in pixels.as_chunks_mut::<4>().0 {
        px[3] = 0xFF;
    }
}

pub fn is_all_black_bgra(pixels: &[u8]) -> bool {
    if pixels.len() < 4 {
        return true;
    }
    for px in pixels.as_chunks::<4>().0 {
        if px[0] != 0 || px[1] != 0 || px[2] != 0 {
            return false;
        }
    }
    true
}

pub fn is_all_black_rgba16f(pixels: &[u8]) -> bool {
    if pixels.len() < 8 {
        return true;
    }
    for px in pixels.as_chunks::<8>().0 {
        let r = u16::from_le_bytes([px[0], px[1]]) & 0x7FFF;
        let g = u16::from_le_bytes([px[2], px[3]]) & 0x7FFF;
        let b = u16::from_le_bytes([px[4], px[5]]) & 0x7FFF;
        if r != 0 || g != 0 || b != 0 {
            return false;
        }
    }
    true
}

pub fn run_capture_fallback_chain<FDup, FWgc, FGdi, T>(
    max_dup_retries: usize,
    mut try_dup: FDup,
    mut try_wgc: FWgc,
    mut try_gdi: FGdi,
) -> Result<(CaptureTier, T), Vec<String>>
where
    FDup: FnMut() -> Result<Option<T>, i32>,
    FWgc: FnMut() -> Result<Option<T>, String>,
    FGdi: FnMut() -> Result<Option<T>, String>,
{
    let mut errors = Vec::new();
    let retries = max_dup_retries.max(1);
    for attempt in 0..retries {
        match try_dup() {
            Ok(Some(frame)) => return Ok((CaptureTier::Duplication, frame)),
            Ok(None) => {
                if attempt + 1 == retries {
                    errors
                        .push("duplication: no presented frame (LastPresentTime == 0)".to_string());
                }
            }
            Err(hr) => {
                let action = classify_duplication_hr(hr);
                errors.push(format!("duplication: hr=0x{:08X} ({action:?})", hr as u32));
                if matches!(
                    action,
                    DuplicationAction::Fallback | DuplicationAction::ResetDevice
                ) {
                    break;
                }
            }
        }
    }

    match try_wgc() {
        Ok(Some(frame)) => return Ok((CaptureTier::Wgc, frame)),
        Ok(None) => errors.push("wgc: no non-black frame available".to_string()),
        Err(e) => errors.push(format!("wgc: {e}")),
    }

    match try_gdi() {
        Ok(Some(frame)) => Ok((CaptureTier::GdiNonBlack, frame)),
        Ok(None) => {
            errors.push("gdi: rejected all-black frame".to_string());
            Err(errors)
        }
        Err(e) => {
            errors.push(format!("gdi: {e}"));
            Err(errors)
        }
    }
}

pub struct DxgiDuplicator {
    gpu: Arc<GpuDevice>,
    output_index: u32,
    duplication: Option<IDXGIOutputDuplication>,
    cached_copy: Option<ID3D11Texture2D>,
    pub width: u32,
    pub height: u32,
    pub hdr: bool,
    pub had_presented_frame: bool,
}

impl DxgiDuplicator {
    pub fn new(gpu: Arc<GpuDevice>, output_index: u32) -> Result<Self, String> {
        let mut dup = Self {
            gpu,
            output_index,
            duplication: None,
            cached_copy: None,
            width: 0,
            height: 0,
            hdr: false,
            had_presented_frame: false,
        };
        dup.reopen()?;
        Ok(dup)
    }

    pub fn reopen(&mut self) -> Result<(), String> {
        self.duplication = None;
        let output = unsafe { self.gpu.adapter.EnumOutputs(self.output_index) }
            .map_err(|e| format!("EnumOutputs({}) failed: {e}", self.output_index))?;

        let mut is_hdr = false;
        if let Ok(out6) = output.cast::<IDXGIOutput6>()
            && let Ok(desc1) = unsafe { out6.GetDesc1() }
        {
            is_hdr = desc1.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
        }

        let dup = if let Ok(out5) = output.cast::<IDXGIOutput5>() {
            let formats: &[DXGI_FORMAT] = if is_hdr {
                &[DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_B8G8R8A8_UNORM]
            } else {
                &[DXGI_FORMAT_B8G8R8A8_UNORM]
            };
            match unsafe { out5.DuplicateOutput1(&self.gpu.device, 0, formats) } {
                Ok(d) => d,
                Err(_) => {
                    is_hdr = false;
                    unsafe { out5.DuplicateOutput(&self.gpu.device) }
                        .map_err(|e| format!("DuplicateOutput fallback failed: {e}"))?
                }
            }
        } else {
            is_hdr = false;
            output
                .cast::<IDXGIOutput5>()
                .and_then(|o| unsafe { o.DuplicateOutput(&self.gpu.device) })
                .map_err(|e| format!("DuplicateOutput failed: {e}"))?
        };

        let dupl_desc = unsafe { dup.GetDesc() };
        self.width = dupl_desc.ModeDesc.Width;
        self.height = dupl_desc.ModeDesc.Height;
        self.hdr = is_hdr;
        self.duplication = Some(dup);
        Ok(())
    }

    pub fn acquire_frame(&mut self, timeout_ms: u32) -> Result<Option<ID3D11Texture2D>, i32> {
        if self.duplication.is_none() && self.reopen().is_err() {
            return Err(E_ACCESSDENIED.0);
        }
        let Some(dup) = self.duplication.clone() else {
            return Err(E_ACCESSDENIED.0);
        };

        let mut info = DXGI_OUTDUPL_FRAME_INFO::default();
        let mut resource: Option<IDXGIResource> = None;
        let res = unsafe { dup.AcquireNextFrame(timeout_ms, &mut info, &mut resource) };
        match res {
            Ok(()) => {
                let mut accepted = false;
                if let Some(res_obj) = resource
                    && should_accept_duplication_frame(info.LastPresentTime)
                    && let Ok(src_tex) = res_obj.cast::<ID3D11Texture2D>()
                {
                    let mut desc = Default::default();
                    unsafe { src_tex.GetDesc(&mut desc) };
                    let recreate = match &self.cached_copy {
                        None => true,
                        Some(existing) => {
                            let mut ed = Default::default();
                            unsafe { existing.GetDesc(&mut ed) };
                            ed.Width != desc.Width
                                || ed.Height != desc.Height
                                || ed.Format != desc.Format
                        }
                    };
                    if recreate {
                        self.cached_copy = self
                            .gpu
                            .create_default_texture(desc.Width, desc.Height, desc.Format)
                            .ok();
                    }
                    if let Some(dst) = &self.cached_copy {
                        unsafe { self.gpu.context.CopyResource(dst, &src_tex) };
                        self.width = desc.Width;
                        self.height = desc.Height;
                        self.had_presented_frame = true;
                        accepted = true;
                    }
                }
                unsafe {
                    let _ = dup.ReleaseFrame();
                }
                if accepted || self.had_presented_frame {
                    Ok(self.cached_copy.clone())
                } else {
                    Ok(None)
                }
            }
            Err(e) => {
                let code = e.code().0;
                match classify_duplication_hr(code) {
                    DuplicationAction::NoNewFrame => {
                        if self.had_presented_frame {
                            Ok(self.cached_copy.clone())
                        } else {
                            Ok(None)
                        }
                    }
                    DuplicationAction::RetryOrFallback => {
                        self.duplication = None;
                        Err(code)
                    }
                    DuplicationAction::ResetDevice | DuplicationAction::Fallback => {
                        self.duplication = None;
                        self.cached_copy = None;
                        self.had_presented_frame = false;
                        Err(code)
                    }
                    DuplicationAction::InspectFrame => Ok(None),
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn issue_150_rejects_last_present_time_zero_first_frame() {
        assert!(!should_accept_duplication_frame(0));
        assert!(should_accept_duplication_frame(1));
        assert!(should_accept_duplication_frame(9_876_543_210));
    }

    #[test]
    fn issue_150_classifies_access_denied_and_access_lost_for_retry_fallback() {
        assert_eq!(
            classify_duplication_hr(E_ACCESSDENIED.0),
            DuplicationAction::RetryOrFallback
        );
        assert_eq!(
            classify_duplication_hr(DXGI_ERROR_ACCESS_LOST.0),
            DuplicationAction::RetryOrFallback
        );
        assert_eq!(
            classify_duplication_hr(DXGI_ERROR_WAIT_TIMEOUT.0),
            DuplicationAction::NoNewFrame
        );
        assert_eq!(
            classify_duplication_hr(0x887A_0005u32 as i32),
            DuplicationAction::ResetDevice
        );
    }

    #[test]
    fn issue_150_forces_opaque_alpha_and_rejects_black_sdr_and_hdr_frames() {
        let mut sdr = vec![0u8; 16];
        assert!(is_all_black_bgra(&sdr));
        force_opaque_alpha_bgra(&mut sdr);
        assert_eq!(sdr[3], 0xFF);
        assert_eq!(sdr[15], 0xFF);
        assert!(is_all_black_bgra(&sdr));
        sdr[2] = 18;
        assert!(!is_all_black_bgra(&sdr));

        let mut hdr = vec![0u8; 32];
        assert!(is_all_black_rgba16f(&hdr));
        hdr[6] = 0x00;
        hdr[7] = 0x3C;
        assert!(is_all_black_rgba16f(&hdr));
        hdr[0] = 0x00;
        hdr[1] = 0x38;
        assert!(!is_all_black_rgba16f(&hdr));
    }

    #[test]
    fn issue_150_fallback_order_is_duplication_then_wgc_then_gdi_rejecting_black() {
        let mut dup_calls = 0usize;
        let mut wgc_calls = 0usize;
        let mut gdi_calls = 0usize;

        let res = run_capture_fallback_chain(
            2,
            || {
                dup_calls += 1;
                Err(E_ACCESSDENIED.0)
            },
            || {
                wgc_calls += 1;
                Ok(Some("wgc-frame"))
            },
            || {
                gdi_calls += 1;
                Ok(Some("gdi-frame"))
            },
        );
        assert_eq!(res, Ok((CaptureTier::Wgc, "wgc-frame")));
        assert_eq!(dup_calls, 2);
        assert_eq!(wgc_calls, 1);
        assert_eq!(gdi_calls, 0);

        let black_res: Result<(CaptureTier, &str), _> =
            run_capture_fallback_chain(2, || Ok(None), || Ok(None), || Ok(None));
        assert!(black_res.is_err());
        let errs = black_res.unwrap_err();
        assert_eq!(errs.len(), 3);
        assert!(errs[0].contains("LastPresentTime == 0"));
        assert!(errs[2].contains("all-black"));
    }
}
