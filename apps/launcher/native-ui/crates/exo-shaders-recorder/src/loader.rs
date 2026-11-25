use std::ffi::c_void;
use std::path::PathBuf;
use std::sync::OnceLock;

use windows::Win32::Foundation::HMODULE;
use windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL;
use windows::Win32::Graphics::Direct3D12::{
    D3D_ROOT_SIGNATURE_VERSION, D3D12_ROOT_SIGNATURE_DESC, D3D12_VERSIONED_ROOT_SIGNATURE_DESC,
};
use windows::Win32::System::LibraryLoader::{
    GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
    GetModuleFileNameW, GetModuleHandleExW, GetProcAddress, LoadLibraryW,
};
use windows::Win32::System::SystemInformation::GetSystemDirectoryW;
use windows::core::{GUID, HRESULT, PCSTR, PCWSTR};

pub type FnD3D12CreateDevice = unsafe extern "system" fn(
    *mut c_void,
    D3D_FEATURE_LEVEL,
    *const GUID,
    *mut *mut c_void,
) -> HRESULT;

pub type FnD3D12GetDebugInterface =
    unsafe extern "system" fn(*const GUID, *mut *mut c_void) -> HRESULT;

pub type FnD3D12SerializeRootSignature = unsafe extern "system" fn(
    *const D3D12_ROOT_SIGNATURE_DESC,
    D3D_ROOT_SIGNATURE_VERSION,
    *mut *mut c_void,
    *mut *mut c_void,
) -> HRESULT;

pub type FnD3D12SerializeVersionedRootSignature = unsafe extern "system" fn(
    *const D3D12_VERSIONED_ROOT_SIGNATURE_DESC,
    *mut *mut c_void,
    *mut *mut c_void,
) -> HRESULT;

pub type FnD3D12CreateRootSignatureDeserializer =
    unsafe extern "system" fn(*const c_void, usize, *const GUID, *mut *mut c_void) -> HRESULT;

pub type FnD3D12CreateVersionedRootSignatureDeserializer =
    unsafe extern "system" fn(*const c_void, usize, *const GUID, *mut *mut c_void) -> HRESULT;

pub type FnD3D12EnableExperimentalFeatures =
    unsafe extern "system" fn(u32, *const GUID, *const c_void, *const u32) -> HRESULT;

pub type FnD3D12GetInterface =
    unsafe extern "system" fn(*const GUID, *const GUID, *mut *mut c_void) -> HRESULT;

pub struct RealD3D12 {
    pub create_device: Option<FnD3D12CreateDevice>,
    pub get_debug_interface: Option<FnD3D12GetDebugInterface>,
    pub serialize_root_signature: Option<FnD3D12SerializeRootSignature>,
    pub serialize_versioned_root_signature: Option<FnD3D12SerializeVersionedRootSignature>,
    pub create_root_signature_deserializer: Option<FnD3D12CreateRootSignatureDeserializer>,
    pub create_versioned_root_signature_deserializer:
        Option<FnD3D12CreateVersionedRootSignatureDeserializer>,
    pub enable_experimental_features: Option<FnD3D12EnableExperimentalFeatures>,
    pub get_interface: Option<FnD3D12GetInterface>,
}

unsafe impl Send for RealD3D12 {}
unsafe impl Sync for RealD3D12 {}

static REAL_D3D12: OnceLock<Option<RealD3D12>> = OnceLock::new();

unsafe fn resolve<T: Copy>(module: HMODULE, name: &'static [u8]) -> Option<T> {
    let proc = unsafe { GetProcAddress(module, PCSTR(name.as_ptr())) }?;
    Some(unsafe { std::mem::transmute_copy(&proc) })
}

pub fn real_d3d12() -> Option<&'static RealD3D12> {
    REAL_D3D12
        .get_or_init(|| {
            let mut sys_dir = [0u16; 260];
            let len = unsafe { GetSystemDirectoryW(Some(&mut sys_dir)) } as usize;
            if len == 0 || len >= sys_dir.len() {
                return None;
            }
            let mut wide = Vec::with_capacity(len + 16);
            wide.extend_from_slice(&sys_dir[..len]);
            if *wide.last().unwrap_or(&0) != b'\\' as u16 {
                wide.push(b'\\' as u16);
            }
            wide.extend("d3d12.dll".encode_utf16());
            wide.push(0);

            let module = unsafe { LoadLibraryW(PCWSTR(wide.as_ptr())) }.ok()?;
            Some(RealD3D12 {
                create_device: unsafe { resolve(module, b"D3D12CreateDevice\0") },
                get_debug_interface: unsafe { resolve(module, b"D3D12GetDebugInterface\0") },
                serialize_root_signature: unsafe {
                    resolve(module, b"D3D12SerializeRootSignature\0")
                },
                serialize_versioned_root_signature: unsafe {
                    resolve(module, b"D3D12SerializeVersionedRootSignature\0")
                },
                create_root_signature_deserializer: unsafe {
                    resolve(module, b"D3D12CreateRootSignatureDeserializer\0")
                },
                create_versioned_root_signature_deserializer: unsafe {
                    resolve(module, b"D3D12CreateVersionedRootSignatureDeserializer\0")
                },
                enable_experimental_features: unsafe {
                    resolve(module, b"D3D12EnableExperimentalFeatures\0")
                },
                get_interface: unsafe { resolve(module, b"D3D12GetInterface\0") },
            })
        })
        .as_ref()
}

pub fn current_module_dir() -> Option<PathBuf> {
    let mut module = HMODULE::default();
    let addr = current_module_dir as *const () as *const u16;
    let ok = unsafe {
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            PCWSTR(addr),
            &mut module,
        )
    };
    if ok.is_err() || module.is_invalid() {
        return None;
    }
    let mut buf = vec![0u16; 1024];
    let len = unsafe { GetModuleFileNameW(Some(module), &mut buf) } as usize;
    if len == 0 || len >= buf.len() {
        return None;
    }
    let path = PathBuf::from(String::from_utf16_lossy(&buf[..len]));
    path.parent().map(PathBuf::from)
}
