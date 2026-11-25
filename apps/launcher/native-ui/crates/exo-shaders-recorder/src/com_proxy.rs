use std::collections::{HashMap, HashSet};
use std::ffi::c_void;
use std::path::Path;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::mpsc::{self, SyncSender};
use std::thread::{self, JoinHandle};

use crate::pso_format::{
    RecordedPsoKind, RecordedPsoRecord, RecorderConfig, RecorderMode, RecorderStats,
    append_pso_record, library_entry_name, read_pipeline_library_if_compatible, read_pso_records,
};
use windows::Win32::Foundation::{E_INVALIDARG, E_NOINTERFACE, E_POINTER, HANDLE, LUID, S_OK};
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT;
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, IDXGIAdapter1, IDXGIDevice, IDXGIFactory4,
};
use windows::Win32::Security::SECURITY_ATTRIBUTES;
use windows::core::{BOOL, GUID, HRESULT, IUnknown, IUnknown_Vtbl, Interface, PCWSTR};

use crate::stream_parse::{
    record_from_compute_desc, record_from_graphics_desc, record_from_stream_desc,
};

fn mode_records(mode: RecorderMode) -> bool {
    matches!(mode, RecorderMode::Record | RecorderMode::RecordAndWarm)
}

fn mode_warms(mode: RecorderMode) -> bool {
    matches!(mode, RecorderMode::Warm | RecorderMode::RecordAndWarm)
}

fn write_stats_atomic(path: &Path, stats: &RecorderStats) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| format!("create_dir_all failed: {e}"))?;
    }
    let tmp = path.with_extension("json.tmp");
    let bytes = serde_json::to_vec_pretty(stats).map_err(|e| format!("serialize stats: {e}"))?;
    std::fs::write(&tmp, bytes).map_err(|e| format!("write stats tmp: {e}"))?;
    std::fs::rename(&tmp, path).map_err(|e| format!("rename stats: {e}"))
}

struct RecorderSession {
    config: RecorderConfig,
    library: Option<ID3D12PipelineLibrary>,
    library1: Option<ID3D12PipelineLibrary1>,
    library_blob: Option<Vec<u8>>,
    library_loaded: bool,
    root_sigs: Mutex<HashMap<usize, Vec<u8>>>,
    seen_keys: Mutex<HashSet<String>>,
    tx: Option<SyncSender<RecordedPsoRecord>>,
    writer_thread: Option<JoinHandle<()>>,
    recorded_count: AtomicU64,
    warm_hits: AtomicU64,
    warm_misses: AtomicU64,
    fallback_count: AtomicU64,
}

impl RecorderSession {
    fn new(inner_device: &ID3D12Device, config: RecorderConfig) -> Self {
        let mut library = None;
        let mut library1 = None;
        let mut library_blob = None;
        let mut library_loaded = false;

        if config.enabled
            && mode_warms(config.mode)
            && !config.library_path.is_empty()
            && let Some((vendor_id, device_id, driver_version)) = query_adapter_info(inner_device)
            && let Ok(Some((_hdr, blob))) = read_pipeline_library_if_compatible(
                Path::new(&config.library_path),
                vendor_id,
                device_id,
                &driver_version,
            )
            && let Ok(dev1) = inner_device.cast::<ID3D12Device1>()
            && let Ok(lib) = unsafe { dev1.CreatePipelineLibrary::<ID3D12PipelineLibrary>(&blob) }
        {
            library1 = lib.cast::<ID3D12PipelineLibrary1>().ok();
            library = Some(lib);
            library_blob = Some(blob);
            library_loaded = true;
        }

        let mut seen = HashSet::new();
        let (tx, writer_thread) =
            if config.enabled && mode_records(config.mode) && !config.record_path.is_empty() {
                let record_path = std::path::PathBuf::from(&config.record_path);
                if let Ok(existing) = read_pso_records(&record_path) {
                    for rec in existing {
                        seen.insert(rec.key_hex);
                    }
                }
                let (sender, receiver) = mpsc::sync_channel::<RecordedPsoRecord>(4096);
                let handle = thread::Builder::new()
                    .name("exo-pso-recorder".to_string())
                    .spawn(move || {
                        while let Ok(record) = receiver.recv() {
                            let _ = append_pso_record(&record_path, &record);
                        }
                    })
                    .ok();
                if handle.is_some() {
                    (Some(sender), handle)
                } else {
                    (None, None)
                }
            } else {
                (None, None)
            };

        Self {
            config,
            library,
            library1,
            library_blob,
            library_loaded,
            root_sigs: Mutex::new(HashMap::new()),
            seen_keys: Mutex::new(seen),
            tx,
            writer_thread,
            recorded_count: AtomicU64::new(0),
            warm_hits: AtomicU64::new(0),
            warm_misses: AtomicU64::new(0),
            fallback_count: AtomicU64::new(0),
        }
    }

    fn track_root_sig(&self, raw_ptr: *mut c_void, blob: &[u8]) {
        if raw_ptr.is_null() || blob.is_empty() {
            return;
        }
        let mut map = match self.root_sigs.lock() {
            Ok(g) => g,
            Err(_) => return,
        };
        map.insert(raw_ptr as usize, blob.to_vec());
        if let Some(canon) = canonical_iunknown(raw_ptr)
            && canon != raw_ptr as usize
        {
            map.insert(canon, blob.to_vec());
        }
    }

    fn lookup_root_sig(&self, raw_ptr: *mut c_void) -> Option<Vec<u8>> {
        if raw_ptr.is_null() {
            return None;
        }
        let map = self.root_sigs.lock().ok()?;
        if let Some(bytes) = map.get(&(raw_ptr as usize)) {
            return Some(bytes.clone());
        }
        let canon = canonical_iunknown(raw_ptr)?;
        map.get(&canon).cloned()
    }

    fn submit_record(&self, record: RecordedPsoRecord) {
        if !self.config.enabled || !mode_records(self.config.mode) {
            return;
        }
        let Some(tx) = &self.tx else {
            return;
        };
        let is_new = match self.seen_keys.lock() {
            Ok(mut set) => {
                if self.config.max_recorded_psos > 0 && set.len() >= self.config.max_recorded_psos {
                    false
                } else {
                    set.insert(record.key_hex.clone())
                }
            }
            Err(_) => false,
        };
        if is_new && tx.try_send(record).is_ok() {
            self.recorded_count.fetch_add(1, Ordering::Relaxed);
        }
    }

    fn shutdown(&mut self) {
        self.tx.take();
        if let Some(handle) = self.writer_thread.take() {
            let _ = handle.join();
        }
        self.library1.take();
        self.library.take();
        self.library_blob.take();
        if let Some(stats_path) = &self.config.stats_path
            && !stats_path.is_empty()
        {
            let stats = RecorderStats {
                library_loaded: self.library_loaded,
                warm_hits: self.warm_hits.load(Ordering::Relaxed),
                warm_misses: self.warm_misses.load(Ordering::Relaxed),
                recorded_count: self.recorded_count.load(Ordering::Relaxed),
                fallback_count: self.fallback_count.load(Ordering::Relaxed),
            };
            let _ = write_stats_atomic(Path::new(stats_path), &stats);
        }
    }
}

impl Drop for RecorderSession {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn canonical_iunknown(raw_ptr: *mut c_void) -> Option<usize> {
    if raw_ptr.is_null() {
        return None;
    }
    let mut unk: *mut c_void = std::ptr::null_mut();
    let vtbl = unsafe { &*(*(raw_ptr as *const *const IUnknown_Vtbl)) };
    let hr = unsafe { (vtbl.QueryInterface)(raw_ptr, &IUnknown::IID, &mut unk) };
    if hr.is_ok() && !unk.is_null() {
        let unk_vtbl = unsafe { &*(*(unk as *const *const IUnknown_Vtbl)) };
        unsafe { (unk_vtbl.Release)(unk) };
        Some(unk as usize)
    } else {
        None
    }
}

pub fn query_adapter_info(device: &ID3D12Device) -> Option<(u32, u32, String)> {
    let luid = unsafe { device.GetAdapterLuid() };
    let factory: IDXGIFactory4 = unsafe { CreateDXGIFactory1() }.ok()?;
    let adapter: IDXGIAdapter1 = unsafe { factory.EnumAdapterByLuid(luid) }.ok()?;
    let desc = unsafe { adapter.GetDesc1() }.ok()?;
    let ver_raw = unsafe { adapter.CheckInterfaceSupport(&IDXGIDevice::IID) }.ok()?;
    let v = ver_raw as u64;
    let driver_version = format!(
        "{}.{}.{}.{}",
        (v >> 48) & 0xffff,
        (v >> 32) & 0xffff,
        (v >> 16) & 0xffff,
        v & 0xffff
    );
    Some((desc.VendorId, desc.DeviceId, driver_version))
}

fn probe_highest_device(raw: *mut c_void) -> (*mut c_void, u8) {
    let iids: [(u8, GUID); 14] = [
        (14, ID3D12Device14::IID),
        (13, ID3D12Device13::IID),
        (12, ID3D12Device12::IID),
        (11, ID3D12Device11::IID),
        (10, ID3D12Device10::IID),
        (9, ID3D12Device9::IID),
        (8, ID3D12Device8::IID),
        (7, ID3D12Device7::IID),
        (6, ID3D12Device6::IID),
        (5, ID3D12Device5::IID),
        (4, ID3D12Device4::IID),
        (3, ID3D12Device3::IID),
        (2, ID3D12Device2::IID),
        (1, ID3D12Device1::IID),
    ];
    let vtbl = unsafe { &*(*(raw as *const *const IUnknown_Vtbl)) };
    for (level, iid) in iids {
        let mut out: *mut c_void = std::ptr::null_mut();
        let hr = unsafe { (vtbl.QueryInterface)(raw, &iid, &mut out) };
        if hr.is_ok() && !out.is_null() {
            return (out, level);
        }
    }
    let mut out: *mut c_void = std::ptr::null_mut();
    let hr = unsafe { (vtbl.QueryInterface)(raw, &ID3D12Device::IID, &mut out) };
    if hr.is_ok() && !out.is_null() {
        return (out, 0);
    }
    unsafe { (vtbl.AddRef)(raw) };
    (raw, 0)
}

pub fn device_iid_level(iid: &GUID) -> Option<u8> {
    if *iid == IUnknown::IID || *iid == ID3D12Object::IID || *iid == ID3D12Device::IID {
        Some(0)
    } else if *iid == ID3D12Device1::IID {
        Some(1)
    } else if *iid == ID3D12Device2::IID {
        Some(2)
    } else if *iid == ID3D12Device3::IID {
        Some(3)
    } else if *iid == ID3D12Device4::IID {
        Some(4)
    } else if *iid == ID3D12Device5::IID {
        Some(5)
    } else if *iid == ID3D12Device6::IID {
        Some(6)
    } else if *iid == ID3D12Device7::IID {
        Some(7)
    } else if *iid == ID3D12Device8::IID {
        Some(8)
    } else if *iid == ID3D12Device9::IID {
        Some(9)
    } else if *iid == ID3D12Device10::IID {
        Some(10)
    } else if *iid == ID3D12Device11::IID {
        Some(11)
    } else if *iid == ID3D12Device12::IID {
        Some(12)
    } else if *iid == ID3D12Device13::IID {
        Some(13)
    } else if *iid == ID3D12Device14::IID {
        Some(14)
    } else {
        None
    }
}

#[repr(C)]
pub struct ProxyDevice {
    vtbl: *const ID3D12Device14_Vtbl,
    ref_count: AtomicU32,
    inner: *mut c_void,
    max_level: u8,
    session: RecorderSession,
}

impl ProxyDevice {
    pub unsafe fn wrap_raw(raw_inner: *mut c_void, config: RecorderConfig) -> *mut c_void {
        let (inner_top, max_level) = probe_highest_device(raw_inner);
        let orig_vtbl = unsafe { &*(*(raw_inner as *const *const IUnknown_Vtbl)) };
        unsafe { (orig_vtbl.Release)(raw_inner) };

        let borrowed_dev = unsafe { ID3D12Device::from_raw_borrowed(&inner_top) }
            .expect("valid ID3D12Device pointer");
        let session = RecorderSession::new(borrowed_dev, config);

        let boxed = Box::new(Self {
            vtbl: &PROXY_VTBL,
            ref_count: AtomicU32::new(1),
            inner: inner_top,
            max_level,
            session,
        });
        Box::into_raw(boxed).cast::<c_void>()
    }

    #[inline(always)]
    unsafe fn from_this<'a>(this: *mut c_void) -> &'a Self {
        unsafe { &*(this.cast::<Self>()) }
    }
}

impl Drop for ProxyDevice {
    fn drop(&mut self) {
        self.session.shutdown();
        if !self.inner.is_null() {
            let vtbl = unsafe { &*(*(self.inner as *const *const IUnknown_Vtbl)) };
            unsafe { (vtbl.Release)(self.inner) };
            self.inner = std::ptr::null_mut();
        }
    }
}

unsafe extern "system" fn proxy_query_interface(
    this: *mut c_void,
    riid: *const GUID,
    ppv: *mut *mut c_void,
) -> HRESULT {
    if ppv.is_null() {
        return E_POINTER;
    }
    unsafe { *ppv = std::ptr::null_mut() };
    if riid.is_null() {
        return E_INVALIDARG;
    }
    let proxy = unsafe { ProxyDevice::from_this(this) };
    let iid = unsafe { *riid };
    if let Some(level) = device_iid_level(&iid) {
        if level <= proxy.max_level {
            proxy.ref_count.fetch_add(1, Ordering::Relaxed);
            unsafe { *ppv = this };
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    let vtbl = unsafe { &*(*(proxy.inner as *const *const IUnknown_Vtbl)) };
    unsafe { (vtbl.QueryInterface)(proxy.inner, riid, ppv) }
}

unsafe extern "system" fn proxy_add_ref(this: *mut c_void) -> u32 {
    let proxy = unsafe { ProxyDevice::from_this(this) };
    proxy.ref_count.fetch_add(1, Ordering::Relaxed) + 1
}

unsafe extern "system" fn proxy_release(this: *mut c_void) -> u32 {
    let proxy = unsafe { ProxyDevice::from_this(this) };
    let prev = proxy.ref_count.fetch_sub(1, Ordering::AcqRel);
    if prev == 1 {
        let _ = unsafe { Box::from_raw(this.cast::<ProxyDevice>()) };
        return 0;
    }
    prev - 1
}

unsafe extern "system" fn proxy_create_root_signature(
    this: *mut c_void,
    node_mask: u32,
    blob: *const c_void,
    blob_len: usize,
    riid: *const GUID,
    ppv: *mut *mut c_void,
) -> HRESULT {
    let proxy = unsafe { ProxyDevice::from_this(this) };
    let vtbl = unsafe { &*(*(proxy.inner as *const *const ID3D12Device_Vtbl)) };
    let hr =
        unsafe { (vtbl.CreateRootSignature)(proxy.inner, node_mask, blob, blob_len, riid, ppv) };
    if hr.is_ok() && !ppv.is_null() && !unsafe { *ppv }.is_null() && !blob.is_null() && blob_len > 0
    {
        let slice = unsafe { std::slice::from_raw_parts(blob.cast::<u8>(), blob_len) };
        proxy.session.track_root_sig(unsafe { *ppv }, slice);
    }
    hr
}

unsafe extern "system" fn proxy_create_compute_pso(
    this: *mut c_void,
    pdesc: *const D3D12_COMPUTE_PIPELINE_STATE_DESC,
    riid: *const GUID,
    ppv: *mut *mut c_void,
) -> HRESULT {
    let proxy = unsafe { ProxyDevice::from_this(this) };
    let mut record = None;
    if !pdesc.is_null() {
        let desc = unsafe { &*pdesc };
        let rs_ptr = desc
            .pRootSignature
            .as_ref()
            .map(|r| r.as_raw())
            .unwrap_or(std::ptr::null_mut());
        let rs_blob = proxy.session.lookup_root_sig(rs_ptr).unwrap_or_default();
        record = unsafe { record_from_compute_desc(desc, rs_blob) };
    }

    if let (Some(rec), Some(lib)) = (&record, &proxy.session.library)
        && proxy.session.config.enabled
        && mode_warms(proxy.session.config.mode)
    {
        let name = library_entry_name(&rec.key_hex);
        let hr = unsafe {
            (lib.vtable().LoadComputePipeline)(
                lib.as_raw(),
                PCWSTR(name.as_ptr()),
                pdesc,
                riid,
                ppv,
            )
        };
        if hr.is_ok() {
            proxy.session.warm_hits.fetch_add(1, Ordering::Relaxed);
            return hr;
        }
        proxy.session.warm_misses.fetch_add(1, Ordering::Relaxed);
    }

    let vtbl = unsafe { &*(*(proxy.inner as *const *const ID3D12Device_Vtbl)) };
    let hr = unsafe { (vtbl.CreateComputePipelineState)(proxy.inner, pdesc, riid, ppv) };
    if hr.is_ok() {
        proxy.session.fallback_count.fetch_add(1, Ordering::Relaxed);
        if let Some(rec) = record {
            proxy.session.submit_record(rec);
        }
    }
    hr
}

unsafe extern "system" fn proxy_create_graphics_pso(
    this: *mut c_void,
    pdesc: *const D3D12_GRAPHICS_PIPELINE_STATE_DESC,
    riid: *const GUID,
    ppv: *mut *mut c_void,
) -> HRESULT {
    let proxy = unsafe { ProxyDevice::from_this(this) };
    let mut record = None;
    if !pdesc.is_null() {
        let desc = unsafe { &*pdesc };
        let rs_ptr = desc
            .pRootSignature
            .as_ref()
            .map(|r| r.as_raw())
            .unwrap_or(std::ptr::null_mut());
        let rs_blob = proxy.session.lookup_root_sig(rs_ptr).unwrap_or_default();
        record = unsafe { record_from_graphics_desc(desc, rs_blob) };
    }

    if let (Some(rec), Some(lib)) = (&record, &proxy.session.library)
        && proxy.session.config.enabled
        && mode_warms(proxy.session.config.mode)
    {
        let name = library_entry_name(&rec.key_hex);
        let hr = unsafe {
            (lib.vtable().LoadGraphicsPipeline)(
                lib.as_raw(),
                PCWSTR(name.as_ptr()),
                pdesc,
                riid,
                ppv,
            )
        };
        if hr.is_ok() {
            proxy.session.warm_hits.fetch_add(1, Ordering::Relaxed);
            return hr;
        }
        proxy.session.warm_misses.fetch_add(1, Ordering::Relaxed);
    }

    let vtbl = unsafe { &*(*(proxy.inner as *const *const ID3D12Device_Vtbl)) };
    let hr = unsafe { (vtbl.CreateGraphicsPipelineState)(proxy.inner, pdesc, riid, ppv) };
    if hr.is_ok() {
        proxy.session.fallback_count.fetch_add(1, Ordering::Relaxed);
        if let Some(rec) = record {
            proxy.session.submit_record(rec);
        }
    }
    hr
}

unsafe extern "system" fn proxy_create_pipeline_state(
    this: *mut c_void,
    pdesc: *const D3D12_PIPELINE_STATE_STREAM_DESC,
    riid: *const GUID,
    ppv: *mut *mut c_void,
) -> HRESULT {
    let proxy = unsafe { ProxyDevice::from_this(this) };
    let parsed = if !pdesc.is_null() {
        let desc = unsafe { &*pdesc };
        unsafe { record_from_stream_desc(desc, |ptr| proxy.session.lookup_root_sig(ptr)) }
    } else {
        None
    };

    if let Some(ref stream) = parsed
        && proxy.session.config.enabled
        && mode_warms(proxy.session.config.mode)
    {
        let name = library_entry_name(&stream.record.key_hex);
        let mut attempted = false;
        if let Some(lib1) = &proxy.session.library1 {
            attempted = true;
            let hr = unsafe {
                (lib1.vtable().LoadPipeline)(lib1.as_raw(), PCWSTR(name.as_ptr()), pdesc, riid, ppv)
            };
            if hr.is_ok() {
                proxy.session.warm_hits.fetch_add(1, Ordering::Relaxed);
                return hr;
            }
        }
        if stream.record.kind == RecordedPsoKind::Compute
            && let Some(lib) = &proxy.session.library
        {
            attempted = true;
            let cs_desc = D3D12_COMPUTE_PIPELINE_STATE_DESC {
                pRootSignature: unsafe {
                    std::mem::ManuallyDrop::new(if stream.root_sig_ptr.is_null() {
                        None
                    } else {
                        Some(ID3D12RootSignature::from_raw(stream.root_sig_ptr))
                    })
                },
                CS: D3D12_SHADER_BYTECODE {
                    pShaderBytecode: stream.record.cs.as_ptr().cast(),
                    BytecodeLength: stream.record.cs.len(),
                },
                NodeMask: stream.record.node_mask,
                CachedPSO: Default::default(),
                Flags: stream.flags,
            };
            let hr = unsafe {
                (lib.vtable().LoadComputePipeline)(
                    lib.as_raw(),
                    PCWSTR(name.as_ptr()),
                    &cs_desc,
                    riid,
                    ppv,
                )
            };
            if hr.is_ok() {
                proxy.session.warm_hits.fetch_add(1, Ordering::Relaxed);
                return hr;
            }
        }
        if attempted {
            proxy.session.warm_misses.fetch_add(1, Ordering::Relaxed);
        }
    }

    let vtbl = unsafe { &*(*(proxy.inner as *const *const ID3D12Device2_Vtbl)) };
    let hr = unsafe { (vtbl.CreatePipelineState)(proxy.inner, pdesc, riid, ppv) };
    if hr.is_ok() {
        proxy.session.fallback_count.fetch_add(1, Ordering::Relaxed);
        if let Some(stream) = parsed {
            proxy.session.submit_record(stream.record);
        }
    }
    hr
}

macro_rules! fwd_fn {
    ($fn_name:ident, $vtbl_ty:ty, $field:ident ( $( $arg:ident : $ty:ty ),* $(,)? ) -> $ret:ty) => {
        unsafe extern "system" fn $fn_name(this: *mut c_void $(, $arg : $ty)*) -> $ret {
            let proxy = unsafe { ProxyDevice::from_this(this) };
            let vtbl = unsafe { &*(*(proxy.inner as *const *const $vtbl_ty)) };
            unsafe { (vtbl.$field)(proxy.inner $(, $arg)*) }
        }
    };
    ($fn_name:ident, $vtbl_ty:ty, $field:ident ( $( $arg:ident : $ty:ty ),* $(,)? )) => {
        unsafe extern "system" fn $fn_name(this: *mut c_void $(, $arg : $ty)*) {
            let proxy = unsafe { ProxyDevice::from_this(this) };
            let vtbl = unsafe { &*(*(proxy.inner as *const *const $vtbl_ty)) };
            unsafe { (vtbl.$field)(proxy.inner $(, $arg)*) }
        }
    };
}

fwd_fn!(
    fwd_get_private_data,
    ID3D12Object_Vtbl,
    GetPrivateData(guid: *const GUID, psize: *mut u32, pdata: *mut c_void) -> HRESULT
);
fwd_fn!(
    fwd_set_private_data,
    ID3D12Object_Vtbl,
    SetPrivateData(guid: *const GUID, size: u32, pdata: *const c_void) -> HRESULT
);
fwd_fn!(
    fwd_set_private_data_interface,
    ID3D12Object_Vtbl,
    SetPrivateDataInterface(guid: *const GUID, pdata: *mut c_void) -> HRESULT
);
fwd_fn!(
    fwd_set_name,
    ID3D12Object_Vtbl,
    SetName(name: PCWSTR) -> HRESULT
);

fwd_fn!(fwd_get_node_count, ID3D12Device_Vtbl, GetNodeCount() -> u32);
fwd_fn!(
    fwd_create_command_queue,
    ID3D12Device_Vtbl,
    CreateCommandQueue(
        pdesc: *const D3D12_COMMAND_QUEUE_DESC,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_command_allocator,
    ID3D12Device_Vtbl,
    CreateCommandAllocator(
        ty: D3D12_COMMAND_LIST_TYPE,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_command_list,
    ID3D12Device_Vtbl,
    CreateCommandList(
        mask: u32,
        ty: D3D12_COMMAND_LIST_TYPE,
        alloc: *mut c_void,
        init: *mut c_void,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_check_feature_support,
    ID3D12Device_Vtbl,
    CheckFeatureSupport(feat: D3D12_FEATURE, data: *mut c_void, size: u32) -> HRESULT
);
fwd_fn!(
    fwd_create_descriptor_heap,
    ID3D12Device_Vtbl,
    CreateDescriptorHeap(
        desc: *const D3D12_DESCRIPTOR_HEAP_DESC,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_get_descriptor_handle_increment_size,
    ID3D12Device_Vtbl,
    GetDescriptorHandleIncrementSize(ty: D3D12_DESCRIPTOR_HEAP_TYPE) -> u32
);
fwd_fn!(
    fwd_create_cbv,
    ID3D12Device_Vtbl,
    CreateConstantBufferView(
        desc: *const D3D12_CONSTANT_BUFFER_VIEW_DESC,
        dst: D3D12_CPU_DESCRIPTOR_HANDLE,
    )
);
fwd_fn!(
    fwd_create_srv,
    ID3D12Device_Vtbl,
    CreateShaderResourceView(
        res: *mut c_void,
        desc: *const D3D12_SHADER_RESOURCE_VIEW_DESC,
        dst: D3D12_CPU_DESCRIPTOR_HANDLE,
    )
);
fwd_fn!(
    fwd_create_uav,
    ID3D12Device_Vtbl,
    CreateUnorderedAccessView(
        res: *mut c_void,
        ctr: *mut c_void,
        desc: *const D3D12_UNORDERED_ACCESS_VIEW_DESC,
        dst: D3D12_CPU_DESCRIPTOR_HANDLE,
    )
);
fwd_fn!(
    fwd_create_rtv,
    ID3D12Device_Vtbl,
    CreateRenderTargetView(
        res: *mut c_void,
        desc: *const D3D12_RENDER_TARGET_VIEW_DESC,
        dst: D3D12_CPU_DESCRIPTOR_HANDLE,
    )
);
fwd_fn!(
    fwd_create_dsv,
    ID3D12Device_Vtbl,
    CreateDepthStencilView(
        res: *mut c_void,
        desc: *const D3D12_DEPTH_STENCIL_VIEW_DESC,
        dst: D3D12_CPU_DESCRIPTOR_HANDLE,
    )
);
fwd_fn!(
    fwd_create_sampler,
    ID3D12Device_Vtbl,
    CreateSampler(desc: *const D3D12_SAMPLER_DESC, dst: D3D12_CPU_DESCRIPTOR_HANDLE)
);
fwd_fn!(
    fwd_copy_descriptors,
    ID3D12Device_Vtbl,
    CopyDescriptors(
        ndst: u32,
        dst_starts: *const D3D12_CPU_DESCRIPTOR_HANDLE,
        dst_sizes: *const u32,
        nsrc: u32,
        src_starts: *const D3D12_CPU_DESCRIPTOR_HANDLE,
        src_sizes: *const u32,
        ty: D3D12_DESCRIPTOR_HEAP_TYPE,
    )
);
fwd_fn!(
    fwd_copy_descriptors_simple,
    ID3D12Device_Vtbl,
    CopyDescriptorsSimple(
        num: u32,
        dst: D3D12_CPU_DESCRIPTOR_HANDLE,
        src: D3D12_CPU_DESCRIPTOR_HANDLE,
        ty: D3D12_DESCRIPTOR_HEAP_TYPE,
    )
);
fwd_fn!(
    fwd_get_resource_allocation_info,
    ID3D12Device_Vtbl,
    GetResourceAllocationInfo(
        ret: *mut D3D12_RESOURCE_ALLOCATION_INFO,
        mask: u32,
        num: u32,
        descs: *const D3D12_RESOURCE_DESC,
    )
);
fwd_fn!(
    fwd_get_custom_heap_properties,
    ID3D12Device_Vtbl,
    GetCustomHeapProperties(ret: *mut D3D12_HEAP_PROPERTIES, mask: u32, ty: D3D12_HEAP_TYPE)
);
fwd_fn!(
    fwd_create_committed_resource,
    ID3D12Device_Vtbl,
    CreateCommittedResource(
        hp: *const D3D12_HEAP_PROPERTIES,
        flags: D3D12_HEAP_FLAGS,
        desc: *const D3D12_RESOURCE_DESC,
        st: D3D12_RESOURCE_STATES,
        clr: *const D3D12_CLEAR_VALUE,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_heap,
    ID3D12Device_Vtbl,
    CreateHeap(desc: *const D3D12_HEAP_DESC, riid: *const GUID, ppv: *mut *mut c_void) -> HRESULT
);
fwd_fn!(
    fwd_create_placed_resource,
    ID3D12Device_Vtbl,
    CreatePlacedResource(
        heap: *mut c_void,
        off: u64,
        desc: *const D3D12_RESOURCE_DESC,
        st: D3D12_RESOURCE_STATES,
        clr: *const D3D12_CLEAR_VALUE,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_reserved_resource,
    ID3D12Device_Vtbl,
    CreateReservedResource(
        desc: *const D3D12_RESOURCE_DESC,
        st: D3D12_RESOURCE_STATES,
        clr: *const D3D12_CLEAR_VALUE,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_shared_handle,
    ID3D12Device_Vtbl,
    CreateSharedHandle(
        obj: *mut c_void,
        attrs: *const SECURITY_ATTRIBUTES,
        access: u32,
        name: PCWSTR,
        out: *mut HANDLE,
    ) -> HRESULT
);
fwd_fn!(
    fwd_open_shared_handle,
    ID3D12Device_Vtbl,
    OpenSharedHandle(h: HANDLE, riid: *const GUID, ppv: *mut *mut c_void) -> HRESULT
);
fwd_fn!(
    fwd_open_shared_handle_by_name,
    ID3D12Device_Vtbl,
    OpenSharedHandleByName(name: PCWSTR, access: u32, out: *mut HANDLE) -> HRESULT
);
fwd_fn!(
    fwd_make_resident,
    ID3D12Device_Vtbl,
    MakeResident(num: u32, objs: *const *mut c_void) -> HRESULT
);
fwd_fn!(
    fwd_evict,
    ID3D12Device_Vtbl,
    Evict(num: u32, objs: *const *mut c_void) -> HRESULT
);
fwd_fn!(
    fwd_create_fence,
    ID3D12Device_Vtbl,
    CreateFence(
        init: u64,
        flags: D3D12_FENCE_FLAGS,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(fwd_get_device_removed_reason, ID3D12Device_Vtbl, GetDeviceRemovedReason() -> HRESULT);
fwd_fn!(
    fwd_get_copyable_footprints,
    ID3D12Device_Vtbl,
    GetCopyableFootprints(
        desc: *const D3D12_RESOURCE_DESC,
        first: u32,
        num: u32,
        base: u64,
        layouts: *mut D3D12_PLACED_SUBRESOURCE_FOOTPRINT,
        rows: *mut u32,
        row_bytes: *mut u64,
        total: *mut u64,
    )
);
fwd_fn!(
    fwd_create_query_heap,
    ID3D12Device_Vtbl,
    CreateQueryHeap(
        desc: *const D3D12_QUERY_HEAP_DESC,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_set_stable_power_state,
    ID3D12Device_Vtbl,
    SetStablePowerState(enable: BOOL) -> HRESULT
);
fwd_fn!(
    fwd_create_command_signature,
    ID3D12Device_Vtbl,
    CreateCommandSignature(
        desc: *const D3D12_COMMAND_SIGNATURE_DESC,
        rs: *mut c_void,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_get_resource_tiling,
    ID3D12Device_Vtbl,
    GetResourceTiling(
        res: *mut c_void,
        num_tiles: *mut u32,
        packed: *mut D3D12_PACKED_MIP_INFO,
        shape: *mut D3D12_TILE_SHAPE,
        num_sub: *mut u32,
        first_sub: u32,
        tilings: *mut D3D12_SUBRESOURCE_TILING,
    )
);
fwd_fn!(
    fwd_get_adapter_luid,
    ID3D12Device_Vtbl,
    GetAdapterLuid(ret: *mut LUID)
);

fwd_fn!(
    fwd_create_pipeline_library,
    ID3D12Device1_Vtbl,
    CreatePipelineLibrary(
        blob: *const c_void,
        len: usize,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_set_event_on_multiple_fence,
    ID3D12Device1_Vtbl,
    SetEventOnMultipleFenceCompletion(
        fences: *const *mut c_void,
        vals: *const u64,
        num: u32,
        flags: D3D12_MULTIPLE_FENCE_WAIT_FLAGS,
        evt: HANDLE,
    ) -> HRESULT
);
fwd_fn!(
    fwd_set_residency_priority,
    ID3D12Device1_Vtbl,
    SetResidencyPriority(
        num: u32,
        objs: *const *mut c_void,
        prios: *const D3D12_RESIDENCY_PRIORITY,
    ) -> HRESULT
);

fwd_fn!(
    fwd_open_existing_heap_from_addr,
    ID3D12Device3_Vtbl,
    OpenExistingHeapFromAddress(
        addr: *const c_void,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_open_existing_heap_from_file,
    ID3D12Device3_Vtbl,
    OpenExistingHeapFromFileMapping(h: HANDLE, riid: *const GUID, ppv: *mut *mut c_void) -> HRESULT
);
fwd_fn!(
    fwd_enqueue_make_resident,
    ID3D12Device3_Vtbl,
    EnqueueMakeResident(
        flags: D3D12_RESIDENCY_FLAGS,
        num: u32,
        objs: *const *mut c_void,
        fence: *mut c_void,
        val: u64,
    ) -> HRESULT
);

fwd_fn!(
    fwd_create_command_list1,
    ID3D12Device4_Vtbl,
    CreateCommandList1(
        mask: u32,
        ty: D3D12_COMMAND_LIST_TYPE,
        flags: D3D12_COMMAND_LIST_FLAGS,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_protected_session,
    ID3D12Device4_Vtbl,
    CreateProtectedResourceSession(
        desc: *const D3D12_PROTECTED_RESOURCE_SESSION_DESC,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_committed_resource1,
    ID3D12Device4_Vtbl,
    CreateCommittedResource1(
        hp: *const D3D12_HEAP_PROPERTIES,
        flags: D3D12_HEAP_FLAGS,
        desc: *const D3D12_RESOURCE_DESC,
        st: D3D12_RESOURCE_STATES,
        clr: *const D3D12_CLEAR_VALUE,
        sess: *mut c_void,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_heap1,
    ID3D12Device4_Vtbl,
    CreateHeap1(
        desc: *const D3D12_HEAP_DESC,
        sess: *mut c_void,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_reserved_resource1,
    ID3D12Device4_Vtbl,
    CreateReservedResource1(
        desc: *const D3D12_RESOURCE_DESC,
        st: D3D12_RESOURCE_STATES,
        clr: *const D3D12_CLEAR_VALUE,
        sess: *mut c_void,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_get_resource_allocation_info1,
    ID3D12Device4_Vtbl,
    GetResourceAllocationInfo1(
        ret: *mut D3D12_RESOURCE_ALLOCATION_INFO,
        mask: u32,
        num: u32,
        descs: *const D3D12_RESOURCE_DESC,
        info1: *mut D3D12_RESOURCE_ALLOCATION_INFO1,
    )
);

fwd_fn!(
    fwd_create_lifetime_tracker,
    ID3D12Device5_Vtbl,
    CreateLifetimeTracker(owner: *mut c_void, riid: *const GUID, ppv: *mut *mut c_void) -> HRESULT
);
fwd_fn!(fwd_remove_device, ID3D12Device5_Vtbl, RemoveDevice());
fwd_fn!(
    fwd_enumerate_meta_commands,
    ID3D12Device5_Vtbl,
    EnumerateMetaCommands(num: *mut u32, descs: *mut D3D12_META_COMMAND_DESC) -> HRESULT
);
fwd_fn!(
    fwd_enumerate_meta_command_params,
    ID3D12Device5_Vtbl,
    EnumerateMetaCommandParameters(
        id: *const GUID,
        stage: D3D12_META_COMMAND_PARAMETER_STAGE,
        total: *mut u32,
        count: *mut u32,
        descs: *mut D3D12_META_COMMAND_PARAMETER_DESC,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_meta_command,
    ID3D12Device5_Vtbl,
    CreateMetaCommand(
        id: *const GUID,
        mask: u32,
        params: *const c_void,
        size: usize,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_state_object,
    ID3D12Device5_Vtbl,
    CreateStateObject(
        desc: *const D3D12_STATE_OBJECT_DESC,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_get_rt_as_prebuild_info,
    ID3D12Device5_Vtbl,
    GetRaytracingAccelerationStructurePrebuildInfo(
        inputs: *const D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS,
        info: *mut D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO,
    )
);
fwd_fn!(
    fwd_check_driver_matching_id,
    ID3D12Device5_Vtbl,
    CheckDriverMatchingIdentifier(
        ty: D3D12_SERIALIZED_DATA_TYPE,
        id: *const D3D12_SERIALIZED_DATA_DRIVER_MATCHING_IDENTIFIER,
    ) -> D3D12_DRIVER_MATCHING_IDENTIFIER_STATUS
);

fwd_fn!(
    fwd_set_background_processing_mode,
    ID3D12Device6_Vtbl,
    SetBackgroundProcessingMode(
        mode: D3D12_BACKGROUND_PROCESSING_MODE,
        action: D3D12_MEASUREMENTS_ACTION,
        evt: HANDLE,
        desired: *mut BOOL,
    ) -> HRESULT
);

fwd_fn!(
    fwd_add_to_state_object,
    ID3D12Device7_Vtbl,
    AddToStateObject(
        add: *const D3D12_STATE_OBJECT_DESC,
        base: *mut c_void,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_protected_session1,
    ID3D12Device7_Vtbl,
    CreateProtectedResourceSession1(
        desc: *const D3D12_PROTECTED_RESOURCE_SESSION_DESC1,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);

fwd_fn!(
    fwd_get_resource_allocation_info2,
    ID3D12Device8_Vtbl,
    GetResourceAllocationInfo2(
        ret: *mut D3D12_RESOURCE_ALLOCATION_INFO,
        mask: u32,
        num: u32,
        descs: *const D3D12_RESOURCE_DESC1,
        info1: *mut D3D12_RESOURCE_ALLOCATION_INFO1,
    )
);
fwd_fn!(
    fwd_create_committed_resource2,
    ID3D12Device8_Vtbl,
    CreateCommittedResource2(
        hp: *const D3D12_HEAP_PROPERTIES,
        flags: D3D12_HEAP_FLAGS,
        desc: *const D3D12_RESOURCE_DESC1,
        st: D3D12_RESOURCE_STATES,
        clr: *const D3D12_CLEAR_VALUE,
        sess: *mut c_void,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_placed_resource1,
    ID3D12Device8_Vtbl,
    CreatePlacedResource1(
        heap: *mut c_void,
        off: u64,
        desc: *const D3D12_RESOURCE_DESC1,
        st: D3D12_RESOURCE_STATES,
        clr: *const D3D12_CLEAR_VALUE,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_sampler_feedback_uav,
    ID3D12Device8_Vtbl,
    CreateSamplerFeedbackUnorderedAccessView(
        tgt: *mut c_void,
        fb: *mut c_void,
        dst: D3D12_CPU_DESCRIPTOR_HANDLE,
    )
);
fwd_fn!(
    fwd_get_copyable_footprints1,
    ID3D12Device8_Vtbl,
    GetCopyableFootprints1(
        desc: *const D3D12_RESOURCE_DESC1,
        first: u32,
        num: u32,
        base: u64,
        layouts: *mut D3D12_PLACED_SUBRESOURCE_FOOTPRINT,
        rows: *mut u32,
        row_bytes: *mut u64,
        total: *mut u64,
    )
);

fwd_fn!(
    fwd_create_shader_cache_session,
    ID3D12Device9_Vtbl,
    CreateShaderCacheSession(
        desc: *const D3D12_SHADER_CACHE_SESSION_DESC,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_shader_cache_control,
    ID3D12Device9_Vtbl,
    ShaderCacheControl(
        kinds: D3D12_SHADER_CACHE_KIND_FLAGS,
        ctrl: D3D12_SHADER_CACHE_CONTROL_FLAGS,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_command_queue1,
    ID3D12Device9_Vtbl,
    CreateCommandQueue1(
        desc: *const D3D12_COMMAND_QUEUE_DESC,
        creator: *const GUID,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);

fwd_fn!(
    fwd_create_committed_resource3,
    ID3D12Device10_Vtbl,
    CreateCommittedResource3(
        hp: *const D3D12_HEAP_PROPERTIES,
        flags: D3D12_HEAP_FLAGS,
        desc: *const D3D12_RESOURCE_DESC1,
        layout: D3D12_BARRIER_LAYOUT,
        clr: *const D3D12_CLEAR_VALUE,
        sess: *mut c_void,
        nfmt: u32,
        fmts: *const DXGI_FORMAT,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_placed_resource2,
    ID3D12Device10_Vtbl,
    CreatePlacedResource2(
        heap: *mut c_void,
        off: u64,
        desc: *const D3D12_RESOURCE_DESC1,
        layout: D3D12_BARRIER_LAYOUT,
        clr: *const D3D12_CLEAR_VALUE,
        nfmt: u32,
        fmts: *const DXGI_FORMAT,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);
fwd_fn!(
    fwd_create_reserved_resource2,
    ID3D12Device10_Vtbl,
    CreateReservedResource2(
        desc: *const D3D12_RESOURCE_DESC,
        layout: D3D12_BARRIER_LAYOUT,
        clr: *const D3D12_CLEAR_VALUE,
        sess: *mut c_void,
        nfmt: u32,
        fmts: *const DXGI_FORMAT,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);

fwd_fn!(
    fwd_create_sampler2,
    ID3D12Device11_Vtbl,
    CreateSampler2(desc: *const D3D12_SAMPLER_DESC2, dst: D3D12_CPU_DESCRIPTOR_HANDLE)
);

fwd_fn!(
    fwd_get_resource_allocation_info3,
    ID3D12Device12_Vtbl,
    GetResourceAllocationInfo3(
        ret: *mut D3D12_RESOURCE_ALLOCATION_INFO,
        mask: u32,
        num: u32,
        descs: *const D3D12_RESOURCE_DESC1,
        nfmts: *const u32,
        fmts: *const *const DXGI_FORMAT,
        info1: *mut D3D12_RESOURCE_ALLOCATION_INFO1,
    )
);

fwd_fn!(
    fwd_open_existing_heap_from_addr1,
    ID3D12Device13_Vtbl,
    OpenExistingHeapFromAddress1(
        addr: *const c_void,
        size: usize,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);

fwd_fn!(
    fwd_create_rs_from_subobject,
    ID3D12Device14_Vtbl,
    CreateRootSignatureFromSubobjectInLibrary(
        mask: u32,
        blob: *const c_void,
        len: usize,
        name: PCWSTR,
        riid: *const GUID,
        ppv: *mut *mut c_void,
    ) -> HRESULT
);

static PROXY_VTBL: ID3D12Device14_Vtbl = ID3D12Device14_Vtbl {
    base__: ID3D12Device13_Vtbl {
        base__: ID3D12Device12_Vtbl {
            base__: ID3D12Device11_Vtbl {
                base__: ID3D12Device10_Vtbl {
                    base__: ID3D12Device9_Vtbl {
                        base__: ID3D12Device8_Vtbl {
                            base__: ID3D12Device7_Vtbl {
                                base__: ID3D12Device6_Vtbl {
                                    base__: ID3D12Device5_Vtbl {
                                        base__: ID3D12Device4_Vtbl {
                                            base__: ID3D12Device3_Vtbl {
                                                base__: ID3D12Device2_Vtbl {
                                                    base__: ID3D12Device1_Vtbl {
                                                        base__: ID3D12Device_Vtbl {
                                                            base__: ID3D12Object_Vtbl {
                                                                base__: IUnknown_Vtbl {
                                                                    QueryInterface: proxy_query_interface,
                                                                    AddRef: proxy_add_ref,
                                                                    Release: proxy_release,
                                                                },
                                                                GetPrivateData: fwd_get_private_data,
                                                                SetPrivateData: fwd_set_private_data,
                                                                SetPrivateDataInterface: fwd_set_private_data_interface,
                                                                SetName: fwd_set_name,
                                                            },
                                                            GetNodeCount: fwd_get_node_count,
                                                            CreateCommandQueue: fwd_create_command_queue,
                                                            CreateCommandAllocator: fwd_create_command_allocator,
                                                            CreateGraphicsPipelineState: proxy_create_graphics_pso,
                                                            CreateComputePipelineState: proxy_create_compute_pso,
                                                            CreateCommandList: fwd_create_command_list,
                                                            CheckFeatureSupport: fwd_check_feature_support,
                                                            CreateDescriptorHeap: fwd_create_descriptor_heap,
                                                            GetDescriptorHandleIncrementSize: fwd_get_descriptor_handle_increment_size,
                                                            CreateRootSignature: proxy_create_root_signature,
                                                            CreateConstantBufferView: fwd_create_cbv,
                                                            CreateShaderResourceView: fwd_create_srv,
                                                            CreateUnorderedAccessView: fwd_create_uav,
                                                            CreateRenderTargetView: fwd_create_rtv,
                                                            CreateDepthStencilView: fwd_create_dsv,
                                                            CreateSampler: fwd_create_sampler,
                                                            CopyDescriptors: fwd_copy_descriptors,
                                                            CopyDescriptorsSimple: fwd_copy_descriptors_simple,
                                                            GetResourceAllocationInfo: fwd_get_resource_allocation_info,
                                                            GetCustomHeapProperties: fwd_get_custom_heap_properties,
                                                            CreateCommittedResource: fwd_create_committed_resource,
                                                            CreateHeap: fwd_create_heap,
                                                            CreatePlacedResource: fwd_create_placed_resource,
                                                            CreateReservedResource: fwd_create_reserved_resource,
                                                            CreateSharedHandle: fwd_create_shared_handle,
                                                            OpenSharedHandle: fwd_open_shared_handle,
                                                            OpenSharedHandleByName: fwd_open_shared_handle_by_name,
                                                            MakeResident: fwd_make_resident,
                                                            Evict: fwd_evict,
                                                            CreateFence: fwd_create_fence,
                                                            GetDeviceRemovedReason: fwd_get_device_removed_reason,
                                                            GetCopyableFootprints: fwd_get_copyable_footprints,
                                                            CreateQueryHeap: fwd_create_query_heap,
                                                            SetStablePowerState: fwd_set_stable_power_state,
                                                            CreateCommandSignature: fwd_create_command_signature,
                                                            GetResourceTiling: fwd_get_resource_tiling,
                                                            GetAdapterLuid: fwd_get_adapter_luid,
                                                        },
                                                        CreatePipelineLibrary: fwd_create_pipeline_library,
                                                        SetEventOnMultipleFenceCompletion: fwd_set_event_on_multiple_fence,
                                                        SetResidencyPriority: fwd_set_residency_priority,
                                                    },
                                                    CreatePipelineState: proxy_create_pipeline_state,
                                                },
                                                OpenExistingHeapFromAddress: fwd_open_existing_heap_from_addr,
                                                OpenExistingHeapFromFileMapping: fwd_open_existing_heap_from_file,
                                                EnqueueMakeResident: fwd_enqueue_make_resident,
                                            },
                                            CreateCommandList1: fwd_create_command_list1,
                                            CreateProtectedResourceSession: fwd_create_protected_session,
                                            CreateCommittedResource1: fwd_create_committed_resource1,
                                            CreateHeap1: fwd_create_heap1,
                                            CreateReservedResource1: fwd_create_reserved_resource1,
                                            GetResourceAllocationInfo1: fwd_get_resource_allocation_info1,
                                        },
                                        CreateLifetimeTracker: fwd_create_lifetime_tracker,
                                        RemoveDevice: fwd_remove_device,
                                        EnumerateMetaCommands: fwd_enumerate_meta_commands,
                                        EnumerateMetaCommandParameters: fwd_enumerate_meta_command_params,
                                        CreateMetaCommand: fwd_create_meta_command,
                                        CreateStateObject: fwd_create_state_object,
                                        GetRaytracingAccelerationStructurePrebuildInfo: fwd_get_rt_as_prebuild_info,
                                        CheckDriverMatchingIdentifier: fwd_check_driver_matching_id,
                                    },
                                    SetBackgroundProcessingMode: fwd_set_background_processing_mode,
                                },
                                AddToStateObject: fwd_add_to_state_object,
                                CreateProtectedResourceSession1: fwd_create_protected_session1,
                            },
                            GetResourceAllocationInfo2: fwd_get_resource_allocation_info2,
                            CreateCommittedResource2: fwd_create_committed_resource2,
                            CreatePlacedResource1: fwd_create_placed_resource1,
                            CreateSamplerFeedbackUnorderedAccessView: fwd_create_sampler_feedback_uav,
                            GetCopyableFootprints1: fwd_get_copyable_footprints1,
                        },
                        CreateShaderCacheSession: fwd_create_shader_cache_session,
                        ShaderCacheControl: fwd_shader_cache_control,
                        CreateCommandQueue1: fwd_create_command_queue1,
                    },
                    CreateCommittedResource3: fwd_create_committed_resource3,
                    CreatePlacedResource2: fwd_create_placed_resource2,
                    CreateReservedResource2: fwd_create_reserved_resource2,
                },
                CreateSampler2: fwd_create_sampler2,
            },
            GetResourceAllocationInfo3: fwd_get_resource_allocation_info3,
        },
        OpenExistingHeapFromAddress1: fwd_open_existing_heap_from_addr1,
    },
    CreateRootSignatureFromSubobjectInLibrary: fwd_create_rs_from_subobject,
};
