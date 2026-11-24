use std::ffi::c_void;
use std::ptr;
use windows::Win32::Foundation::{FreeLibrary, HMODULE};
use windows::Win32::Graphics::Direct3D12::ID3D12Device;
use windows::Win32::System::LibraryLoader::{
    GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW,
};
use windows::core::{Interface, w};

pub const NV_OF_API_VERSION_5_0: u32 = 5 << 4;
pub const NV_OF_SUCCESS: i32 = 0;

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct NvOfD3d12ApiFunctionList {
    nv_create_optical_flow_d3d12:
        Option<unsafe extern "C" fn(device: *mut c_void, h_of: *mut *mut c_void) -> i32>,
    nv_of_init: Option<unsafe extern "C" fn(h_of: *mut c_void, params: *const c_void) -> i32>,
    nv_of_get_surface_format_count_d3d12: Option<
        unsafe extern "C" fn(
            h_of: *mut c_void,
            buf_type: u32,
            of_mode: u32,
            count: *mut u32,
        ) -> i32,
    >,
    nv_of_get_surface_format_d3d12: Option<
        unsafe extern "C" fn(
            h_of: *mut c_void,
            buf_type: u32,
            of_mode: u32,
            formats: *mut u32,
        ) -> i32,
    >,
    nv_of_register_resource_d3d12:
        Option<unsafe extern "C" fn(h_of: *mut c_void, params: *mut c_void) -> i32>,
    nv_of_unregister_resource_d3d12: Option<unsafe extern "C" fn(params: *mut c_void) -> i32>,
    nv_of_execute_d3d12: Option<
        unsafe extern "C" fn(
            h_of: *mut c_void,
            in_params: *const c_void,
            out_params: *mut c_void,
        ) -> i32,
    >,
    nv_of_destroy: Option<unsafe extern "C" fn(h_of: *mut c_void) -> i32>,
    nv_of_get_last_error:
        Option<unsafe extern "C" fn(h_of: *mut c_void, msg: *mut i8, size: *mut u32) -> i32>,
    nv_of_get_caps: Option<
        unsafe extern "C" fn(
            h_of: *mut c_void,
            caps_param: u32,
            caps_val: *mut u32,
            size: *mut u32,
        ) -> i32,
    >,
    _reserved: [usize; 16],
}

#[derive(Clone, Debug)]
pub struct NvofProbeResult {
    pub dll_loaded: bool,
    pub api_version_5_0_ok: bool,
    pub d3d12_instance_created: bool,
    pub status_code: i32,
    pub note: String,
}

pub struct NvofRuntime {
    module: HMODULE,
    fns: NvOfD3d12ApiFunctionList,
    handle: *mut c_void,
}

impl NvofRuntime {
    pub fn probe_and_create(
        device: &ID3D12Device,
        _cmd_queue: &windows::Win32::Graphics::Direct3D12::ID3D12CommandQueue,
    ) -> (Option<Self>, NvofProbeResult) {
        unsafe {
            let module = match LoadLibraryExW(
                w!("nvofapi64.dll"),
                None,
                LOAD_LIBRARY_SEARCH_SYSTEM32,
            ) {
                Ok(m) => m,
                Err(e) => {
                    return (
                        None,
                        NvofProbeResult {
                            dll_loaded: false,
                            api_version_5_0_ok: false,
                            d3d12_instance_created: false,
                            status_code: -1,
                            note: format!(
                                "nvofapi64.dll not present in System32 ({e}); using DX12 compute backend"
                            ),
                        },
                    );
                }
            };

            type CreateInstanceFn = unsafe extern "C" fn(u32, *mut NvOfD3d12ApiFunctionList) -> i32;
            let proc = GetProcAddress(module, windows::core::s!("NvOFAPICreateInstanceD3D12"));
            let Some(proc_fn) = proc else {
                let _ = FreeLibrary(module);
                return (
                    None,
                    NvofProbeResult {
                        dll_loaded: true,
                        api_version_5_0_ok: false,
                        d3d12_instance_created: false,
                        status_code: -2,
                        note: "nvofapi64.dll loaded but missing NvOFAPICreateInstanceD3D12 export"
                            .into(),
                    },
                );
            };
            let create_inst: CreateInstanceFn = std::mem::transmute(proc_fn);
            let mut fns = NvOfD3d12ApiFunctionList::default();
            let st = create_inst(NV_OF_API_VERSION_5_0, &mut fns);
            if st != NV_OF_SUCCESS {
                let _ = FreeLibrary(module);
                return (
                    None,
                    NvofProbeResult {
                        dll_loaded: true,
                        api_version_5_0_ok: false,
                        d3d12_instance_created: false,
                        status_code: st,
                        note: format!(
                            "NvOFAPICreateInstanceD3D12(5.0) returned status {st}; falling back to DX12 compute"
                        ),
                    },
                );
            }

            let mut handle: *mut c_void = ptr::null_mut();
            let create_status = if let Some(create_of) = fns.nv_create_optical_flow_d3d12 {
                create_of(device.as_raw(), &mut handle)
            } else {
                -3
            };

            if create_status != NV_OF_SUCCESS || handle.is_null() {
                let _ = FreeLibrary(module);
                return (
                    None,
                    NvofProbeResult {
                        dll_loaded: true,
                        api_version_5_0_ok: true,
                        d3d12_instance_created: false,
                        status_code: create_status,
                        note: format!(
                            "NVOF 5.0 API loaded; nvCreateOpticalFlowD3D12 returned {create_status}; falling back to DX12 compute"
                        ),
                    },
                );
            }

            (
                Some(Self {
                    module,
                    fns,
                    handle,
                }),
                NvofProbeResult {
                    dll_loaded: true,
                    api_version_5_0_ok: true,
                    d3d12_instance_created: true,
                    status_code: NV_OF_SUCCESS,
                    note: "NVOF 5.0 D3D12 hardware optical flow instance created on NVIDIA GPU"
                        .into(),
                },
            )
        }
    }
}

impl Drop for NvofRuntime {
    fn drop(&mut self) {
        unsafe {
            if !self.handle.is_null()
                && let Some(destroy) = self.fns.nv_of_destroy
            {
                let _ = destroy(self.handle);
            }
            self.handle = ptr::null_mut();
            let _ = FreeLibrary(self.module);
        }
    }
}
