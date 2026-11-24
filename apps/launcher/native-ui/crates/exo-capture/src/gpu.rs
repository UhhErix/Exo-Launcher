use std::ffi::OsString;
use std::os::windows::ffi::OsStringExt;

use windows::Win32::Foundation::{E_INVALIDARG, HMODULE};
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE_UNKNOWN, D3D_FEATURE_LEVEL, D3D_FEATURE_LEVEL_10_0, D3D_FEATURE_LEVEL_10_1,
    D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_11_1,
};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_CREATE_DEVICE_FLAG, D3D11_CREATE_DEVICE_VIDEO_SUPPORT, D3D11_SDK_VERSION,
    D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT, D3D11CreateDevice, ID3D11Device,
    ID3D11DeviceContext, ID3D11Multithread, ID3D11Texture2D, ID3D11VideoContext, ID3D11VideoDevice,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, DXGI_ADAPTER_FLAG_SOFTWARE, DXGI_ERROR_DEVICE_REMOVED,
    DXGI_ERROR_DEVICE_RESET, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, DXGI_QUERY_VIDEO_MEMORY_INFO,
    IDXGIAdapter1, IDXGIAdapter3, IDXGIDevice, IDXGIFactory1,
};
use windows::Win32::Media::MediaFoundation::{IMFDXGIDeviceManager, MFCreateDXGIDeviceManager};
use windows::core::{IUnknown, Interface};

pub const GPU_THREAD_PRIORITY_LOWEST: i32 = -7;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdapterInfo {
    pub luid: i64,
    pub name: String,
    pub software: bool,
    pub vendor_id: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VramUsage {
    pub current_bytes: u64,
    pub budget_bytes: u64,
}

pub fn select_adapter_index(
    adapters: &[AdapterInfo],
    requested_luid: Option<i64>,
) -> (Option<usize>, bool) {
    if let Some(want) = requested_luid
        && let Some(idx) = adapters.iter().position(|a| !a.software && a.luid == want)
    {
        return (Some(idx), false);
    }
    if let Some(idx) = adapters.iter().position(|a| !a.software) {
        return (Some(idx), requested_luid.is_some());
    }
    (None, false)
}

pub fn should_retry_without_11_1(hresult: i32) -> bool {
    hresult == E_INVALIDARG.0
}

pub fn is_device_lost(hresult: i32) -> bool {
    hresult == DXGI_ERROR_DEVICE_REMOVED.0 || hresult == DXGI_ERROR_DEVICE_RESET.0
}

pub fn format_create_failure(
    adapter_name: &str,
    hresult: i32,
    flags: u32,
    levels: &[D3D_FEATURE_LEVEL],
) -> String {
    let level_names: Vec<&str> = levels
        .iter()
        .map(|l| match *l {
            D3D_FEATURE_LEVEL_11_1 => "11_1",
            D3D_FEATURE_LEVEL_11_0 => "11_0",
            D3D_FEATURE_LEVEL_10_1 => "10_1",
            D3D_FEATURE_LEVEL_10_0 => "10_0",
            _ => "other",
        })
        .collect();
    format!(
        "Capture unavailable on this GPU: adapter={adapter_name}, hr=0x{:08X}, flags=0x{flags:X}, levels={}.",
        hresult as u32,
        level_names.join(",")
    )
}

pub fn probe_codec_availability_guarded<F, L>(
    probe: F,
    mut log: L,
    last_error: &mut Option<String>,
    logged: &mut bool,
) -> bool
where
    F: FnOnce() -> Result<bool, String>,
    L: FnMut(String),
{
    match probe() {
        Ok(ok) => {
            *last_error = None;
            *logged = false;
            ok
        }
        Err(err) => {
            *last_error = Some(err.clone());
            if !*logged {
                *logged = true;
                log(err);
            }
            false
        }
    }
}

pub struct GpuDevice {
    pub adapter: IDXGIAdapter1,
    pub device: ID3D11Device,
    pub context: ID3D11DeviceContext,
    pub video_device: ID3D11VideoDevice,
    pub video_context: ID3D11VideoContext,
    pub dxgi_manager: IMFDXGIDeviceManager,
    pub reset_token: u32,
    pub info: AdapterInfo,
}

unsafe impl Send for GpuDevice {}
unsafe impl Sync for GpuDevice {}

impl GpuDevice {
    pub fn create(requested_luid: Option<i64>) -> Result<Self, String> {
        let factory: IDXGIFactory1 = unsafe { CreateDXGIFactory1() }
            .map_err(|e| format!("CreateDXGIFactory1 failed: {e}"))?;

        let mut adapters: Vec<IDXGIAdapter1> = Vec::new();
        let mut infos: Vec<AdapterInfo> = Vec::new();
        let mut idx = 0u32;
        while let Ok(adapter) = unsafe { factory.EnumAdapters1(idx) } {
            if let Ok(desc) = unsafe { adapter.GetDesc1() } {
                let luid =
                    ((desc.AdapterLuid.HighPart as i64) << 32) | (desc.AdapterLuid.LowPart as i64);
                let end = desc
                    .Description
                    .iter()
                    .position(|&c| c == 0)
                    .unwrap_or(desc.Description.len());
                let name = OsString::from_wide(&desc.Description[..end])
                    .to_string_lossy()
                    .trim()
                    .to_string();
                let software = (desc.Flags & (DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32)) != 0;
                infos.push(AdapterInfo {
                    luid,
                    name,
                    software,
                    vendor_id: desc.VendorId,
                });
                adapters.push(adapter);
            }
            idx += 1;
        }

        let (chosen_idx, fell_back) = select_adapter_index(&infos, requested_luid);
        let Some(chosen_idx) = chosen_idx else {
            let names: Vec<&str> = infos.iter().map(|a| a.name.as_str()).collect();
            return Err(format!(
                "Capture unavailable on this GPU: no hardware DXGI adapter found (adapters={}).",
                if names.is_empty() {
                    "none".to_string()
                } else {
                    names.join(", ")
                }
            ));
        };

        let adapter = adapters.swap_remove(chosen_idx);
        let info = infos.swap_remove(chosen_idx);
        if fell_back && let Some(req) = requested_luid {
            eprintln!(
                "Requested GPU adapter LUID 0x{:016X} not found; falling back to default adapter {}.",
                req as u64, info.name
            );
        }

        let flags: D3D11_CREATE_DEVICE_FLAG =
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        let primary_levels = [
            D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL_10_1,
            D3D_FEATURE_LEVEL_10_0,
        ];
        let fallback_levels = [
            D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL_10_1,
            D3D_FEATURE_LEVEL_10_0,
        ];

        let (device, context) = match create_d3d11(&adapter, flags, &primary_levels) {
            Ok(pair) => pair,
            Err(hr) if should_retry_without_11_1(hr) => {
                eprintln!(
                    "{}",
                    format_create_failure(&info.name, hr, flags.0, &primary_levels)
                );
                create_d3d11(&adapter, flags, &fallback_levels).map_err(|hr2| {
                    format_create_failure(&info.name, hr2, flags.0, &fallback_levels)
                })?
            }
            Err(hr) => {
                return Err(format_create_failure(
                    &info.name,
                    hr,
                    flags.0,
                    &primary_levels,
                ));
            }
        };

        if let Ok(mt) = context.cast::<ID3D11Multithread>() {
            unsafe {
                let _ = mt.SetMultithreadProtected(true);
            }
        }
        if let Ok(dxgi_dev) = device.cast::<IDXGIDevice>() {
            unsafe {
                let _ = dxgi_dev.SetGPUThreadPriority(GPU_THREAD_PRIORITY_LOWEST);
            }
        }

        let video_device: ID3D11VideoDevice = device
            .cast()
            .map_err(|e| format!("ID3D11VideoDevice unavailable on {}: {e}", info.name))?;
        let video_context: ID3D11VideoContext = context
            .cast()
            .map_err(|e| format!("ID3D11VideoContext unavailable on {}: {e}", info.name))?;

        let mut reset_token = 0u32;
        let mut manager: Option<IMFDXGIDeviceManager> = None;
        unsafe { MFCreateDXGIDeviceManager(&mut reset_token, &mut manager) }
            .map_err(|e| format!("MFCreateDXGIDeviceManager failed: {e}"))?;
        let dxgi_manager = manager.ok_or("MFCreateDXGIDeviceManager returned null")?;
        let dev_unk: IUnknown = device
            .cast()
            .map_err(|e| format!("cast ID3D11Device to IUnknown failed: {e}"))?;
        unsafe { dxgi_manager.ResetDevice(&dev_unk, reset_token) }
            .map_err(|e| format!("IMFDXGIDeviceManager::ResetDevice failed: {e}"))?;

        Ok(Self {
            adapter,
            device,
            context,
            video_device,
            video_context,
            dxgi_manager,
            reset_token,
            info,
        })
    }

    pub fn create_default_texture(
        &self,
        width: u32,
        height: u32,
        format: DXGI_FORMAT,
    ) -> Result<ID3D11Texture2D, String> {
        let desc = D3D11_TEXTURE2D_DESC {
            Width: width,
            Height: height,
            MipLevels: 1,
            ArraySize: 1,
            Format: format,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: (D3D11_BIND_RENDER_TARGET.0 | D3D11_BIND_SHADER_RESOURCE.0) as u32,
            CPUAccessFlags: 0,
            MiscFlags: 0,
        };
        let mut tex: Option<ID3D11Texture2D> = None;
        unsafe { self.device.CreateTexture2D(&desc, None, Some(&mut tex)) }
            .map_err(|e| format!("CreateTexture2D({width}x{height}) failed: {e}"))?;
        tex.ok_or_else(|| "CreateTexture2D returned null".to_string())
    }

    pub fn query_vram_usage(&self) -> Option<VramUsage> {
        let adapter3: IDXGIAdapter3 = self.adapter.cast().ok()?;
        let mut info = DXGI_QUERY_VIDEO_MEMORY_INFO::default();
        unsafe { adapter3.QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &mut info) }
            .ok()?;
        Some(VramUsage {
            current_bytes: info.CurrentUsage,
            budget_bytes: info.Budget,
        })
    }

    pub fn is_nvidia(&self) -> bool {
        self.info.vendor_id == 0x10DE
    }
}

fn create_d3d11(
    adapter: &IDXGIAdapter1,
    flags: D3D11_CREATE_DEVICE_FLAG,
    levels: &[D3D_FEATURE_LEVEL],
) -> Result<(ID3D11Device, ID3D11DeviceContext), i32> {
    let mut device: Option<ID3D11Device> = None;
    let mut context: Option<ID3D11DeviceContext> = None;
    let res = unsafe {
        D3D11CreateDevice(
            adapter,
            D3D_DRIVER_TYPE_UNKNOWN,
            HMODULE::default(),
            flags,
            Some(levels),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )
    };
    match res {
        Ok(()) => match (device, context) {
            (Some(d), Some(c)) => Ok((d, c)),
            _ => Err(E_INVALIDARG.0),
        },
        Err(e) => Err(e.code().0),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn adapter_selection_matches_rtx_4090_handles_stale_luid_and_skips_software() {
        let adapters = vec![
            AdapterInfo {
                luid: 0x1001,
                name: "Microsoft Basic Render Driver".to_string(),
                software: true,
                vendor_id: 0x1414,
            },
            AdapterInfo {
                luid: 0x2002,
                name: "Intel(R) UHD Graphics 770".to_string(),
                software: false,
                vendor_id: 0x8086,
            },
            AdapterInfo {
                luid: 0x3003,
                name: "NVIDIA GeForce RTX 4090".to_string(),
                software: false,
                vendor_id: 0x10DE,
            },
        ];

        assert_eq!(
            select_adapter_index(&adapters, Some(0x3003)),
            (Some(2), false)
        );
        assert_eq!(
            select_adapter_index(&adapters, Some(0x9999)),
            (Some(1), true)
        );
        assert_eq!(select_adapter_index(&adapters, None), (Some(1), false));

        let software_only = vec![adapters[0].clone()];
        assert_eq!(select_adapter_index(&software_only, None), (None, false));
        assert_eq!(select_adapter_index(&[], Some(0x3003)), (None, false));
    }

    #[test]
    fn feature_level_retry_device_lost_and_diagnostics_match_capture_crash_rules() {
        assert!(should_retry_without_11_1(0x8007_0057u32 as i32));
        assert!(!should_retry_without_11_1(0x887A_0005u32 as i32));
        assert!(is_device_lost(0x887A_0005u32 as i32));
        assert!(is_device_lost(0x887A_0007u32 as i32));
        assert!(!is_device_lost(0));

        let msg = format_create_failure(
            "NVIDIA GeForce RTX 4090",
            0x887A_0004u32 as i32,
            0x820,
            &[D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0],
        );
        assert!(msg.contains("NVIDIA GeForce RTX 4090"));
        assert!(msg.contains("0x887A0004"));
        assert!(msg.contains("11_1,11_0"));
    }

    #[test]
    fn codec_probe_guard_logs_once_and_recovers_after_gpu_reset() {
        let mut logs = Vec::new();
        let mut last_err = None;
        let mut logged = false;

        assert!(!probe_codec_availability_guarded(
            || Err("Capture unavailable on this GPU.".to_string()),
            |l| logs.push(l),
            &mut last_err,
            &mut logged,
        ));
        assert_eq!(logs.len(), 1);
        assert_eq!(
            last_err.as_deref(),
            Some("Capture unavailable on this GPU.")
        );

        assert!(!probe_codec_availability_guarded(
            || Err("Capture unavailable on this GPU.".to_string()),
            |l| logs.push(l),
            &mut last_err,
            &mut logged,
        ));
        assert_eq!(logs.len(), 1);

        assert!(probe_codec_availability_guarded(
            || Ok(true),
            |l| logs.push(l),
            &mut last_err,
            &mut logged,
        ));
        assert!(last_err.is_none());
        assert!(!logged);
    }
}
