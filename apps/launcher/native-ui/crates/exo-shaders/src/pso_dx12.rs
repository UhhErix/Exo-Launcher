use crate::pso_store::{
    RecordedGraphicsState, RecordedPsoKind, RecordedPsoRecord, library_entry_name,
    write_pipeline_library,
};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::ffi::CString;
use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::Instant;
use windows::Win32::Graphics::Direct3D::Fxc::{D3DCOMPILE_OPTIMIZATION_LEVEL3, D3DCompile};
use windows::Win32::Graphics::Direct3D::{D3D_FEATURE_LEVEL_12_0, ID3DBlob};
use windows::Win32::Graphics::Direct3D12::{
    D3D_ROOT_SIGNATURE_VERSION_1, D3D12_BLEND_DESC, D3D12_COMPUTE_PIPELINE_STATE_DESC,
    D3D12_DEPTH_STENCIL_DESC, D3D12_GRAPHICS_PIPELINE_STATE_DESC,
    D3D12_INDEX_BUFFER_STRIP_CUT_VALUE, D3D12_INPUT_CLASSIFICATION, D3D12_INPUT_ELEMENT_DESC,
    D3D12_INPUT_LAYOUT_DESC, D3D12_PRIMITIVE_TOPOLOGY_TYPE, D3D12_RASTERIZER_DESC,
    D3D12_ROOT_SIGNATURE_DESC, D3D12_ROOT_SIGNATURE_FLAG_NONE, D3D12_SHADER_BYTECODE,
    D3D12CreateDevice, D3D12SerializeRootSignature, ID3D12Device1, ID3D12PipelineLibrary,
    ID3D12PipelineState, ID3D12RootSignature,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, DXGI_ADAPTER_FLAG_SOFTWARE, DXGI_ERROR_DEVICE_REMOVED, IDXGIFactory1,
};
use windows::Win32::System::Threading::{
    GetCurrentThread, SetThreadPriority, THREAD_PRIORITY_BELOW_NORMAL,
};
use windows::core::{PCSTR, PCWSTR};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PsoCompileSummary {
    pub requested: usize,
    pub compiled: usize,
    pub failed: usize,
    pub paused: bool,
    pub device_removed: bool,
    pub threads_used: usize,
    pub hlsl_compile_ms: f64,
    pub pso_create_ms: f64,
    pub pipelines_per_sec: f64,
    pub library_serialized_bytes: usize,
    pub warm_load_ms: f64,
    pub warm_pipelines_per_sec: f64,
    pub pause_latency_ms: Option<f64>,
}

pub struct Dx12WarmContext {
    device: ID3D12Device1,
    root_sig: ID3D12RootSignature,
}

unsafe impl Send for Dx12WarmContext {}
unsafe impl Sync for Dx12WarmContext {}

impl Dx12WarmContext {
    pub fn new() -> Result<Self, String> {
        unsafe {
            let factory: IDXGIFactory1 =
                CreateDXGIFactory1().map_err(|e| format!("CreateDXGIFactory1 failed: {e}"))?;
            let mut chosen = None;
            let mut best_vram = 0usize;
            let mut idx = 0u32;
            while let Ok(adapter) = factory.EnumAdapters1(idx) {
                idx += 1;
                let Ok(desc) = adapter.GetDesc1() else {
                    continue;
                };
                if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32) != 0 {
                    continue;
                }
                if chosen.is_none() || desc.DedicatedVideoMemory > best_vram {
                    best_vram = desc.DedicatedVideoMemory;
                    chosen = Some(adapter);
                }
            }
            let adapter = chosen.ok_or_else(|| "No hardware DXGI adapter found".to_string())?;
            let mut device: Option<ID3D12Device1> = None;
            D3D12CreateDevice(&adapter, D3D_FEATURE_LEVEL_12_0, &mut device)
                .map_err(|e| format!("D3D12CreateDevice failed: {e}"))?;
            let device = device.ok_or_else(|| "D3D12CreateDevice returned null".to_string())?;

            let rs_desc = D3D12_ROOT_SIGNATURE_DESC {
                NumParameters: 0,
                pParameters: std::ptr::null(),
                NumStaticSamplers: 0,
                pStaticSamplers: std::ptr::null(),
                Flags: D3D12_ROOT_SIGNATURE_FLAG_NONE,
            };
            let mut sig_blob: Option<ID3DBlob> = None;
            let mut err_blob: Option<ID3DBlob> = None;
            D3D12SerializeRootSignature(
                &rs_desc,
                D3D_ROOT_SIGNATURE_VERSION_1,
                &mut sig_blob,
                Some(&mut err_blob),
            )
            .map_err(|e| format!("D3D12SerializeRootSignature failed: {e}"))?;
            let sig_blob = sig_blob.ok_or_else(|| "Empty root signature blob".to_string())?;
            let slice = std::slice::from_raw_parts(
                sig_blob.GetBufferPointer() as *const u8,
                sig_blob.GetBufferSize(),
            );
            let root_sig: ID3D12RootSignature = device
                .CreateRootSignature(0, slice)
                .map_err(|e| format!("CreateRootSignature failed: {e}"))?;
            Ok(Self { device, root_sig })
        }
    }

    pub fn compile_hlsl_corpus(
        count: usize,
        threads: usize,
    ) -> Result<(Vec<Vec<u8>>, f64), String> {
        let start = Instant::now();
        let next = AtomicUsize::new(0);
        let slots: Vec<std::sync::Mutex<Option<Vec<u8>>>> =
            (0..count).map(|_| std::sync::Mutex::new(None)).collect();
        let first_err: std::sync::Mutex<Option<String>> = std::sync::Mutex::new(None);
        let worker_count = threads.max(1).min(count.max(1));

        std::thread::scope(|s| {
            for _ in 0..worker_count {
                s.spawn(|| {
                    loop {
                        let i = next.fetch_add(1, Ordering::Relaxed);
                        if i >= count {
                            break;
                        }
                        match compile_single_cs(i) {
                            Ok(bytes) => {
                                *slots[i].lock().unwrap() = Some(bytes);
                            }
                            Err(e) => {
                                let mut err = first_err.lock().unwrap();
                                if err.is_none() {
                                    *err = Some(e);
                                }
                                break;
                            }
                        }
                    }
                });
            }
        });

        if let Some(e) = first_err.into_inner().unwrap() {
            return Err(e);
        }
        let mut blobs = Vec::with_capacity(count);
        for slot in slots {
            if let Some(b) = slot.into_inner().unwrap() {
                blobs.push(b);
            }
        }
        Ok((blobs, start.elapsed().as_secs_f64() * 1000.0))
    }

    pub fn warm_compute_psos(
        &self,
        bytecode: &[Vec<u8>],
        threads: usize,
        cancel: &Arc<AtomicBool>,
        below_normal: bool,
    ) -> Result<PsoCompileSummary, String> {
        let count = bytecode.len();
        let worker_count = threads.max(1).min(count.max(1));
        if cancel.load(Ordering::Acquire) {
            return Ok(PsoCompileSummary {
                requested: count,
                compiled: 0,
                failed: 0,
                paused: true,
                device_removed: false,
                threads_used: 0,
                hlsl_compile_ms: 0.0,
                pso_create_ms: 0.0,
                pipelines_per_sec: 0.0,
                library_serialized_bytes: 0,
                warm_load_ms: 0.0,
                warm_pipelines_per_sec: 0.0,
                pause_latency_ms: Some(0.0),
            });
        }
        let next = AtomicUsize::new(0);
        let compiled = AtomicUsize::new(0);
        let failed = AtomicUsize::new(0);
        let device_removed = AtomicBool::new(false);
        let paused = AtomicBool::new(false);
        let psos: Vec<std::sync::Mutex<Option<ID3D12PipelineState>>> =
            (0..count).map(|_| std::sync::Mutex::new(None)).collect();

        let pso_start = Instant::now();
        std::thread::scope(|s| {
            for _ in 0..worker_count {
                s.spawn(|| {
                    if below_normal {
                        unsafe {
                            let _ =
                                SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
                        }
                    }
                    loop {
                        if cancel.load(Ordering::Acquire) {
                            paused.store(true, Ordering::Release);
                            break;
                        }
                        let i = next.fetch_add(1, Ordering::Relaxed);
                        if i >= count {
                            break;
                        }
                        let code = &bytecode[i];
                        let desc = D3D12_COMPUTE_PIPELINE_STATE_DESC {
                            pRootSignature: unsafe { std::mem::transmute_copy(&self.root_sig) },
                            CS: D3D12_SHADER_BYTECODE {
                                pShaderBytecode: code.as_ptr() as *const _,
                                BytecodeLength: code.len(),
                            },
                            NodeMask: 0,
                            CachedPSO: Default::default(),
                            Flags: Default::default(),
                        };
                        let res: windows::core::Result<ID3D12PipelineState> =
                            unsafe { self.device.CreateComputePipelineState(&desc) };
                        match res {
                            Ok(pso) => {
                                compiled.fetch_add(1, Ordering::Relaxed);
                                *psos[i].lock().unwrap() = Some(pso);
                            }
                            Err(e) => {
                                failed.fetch_add(1, Ordering::Relaxed);
                                if e.code() == DXGI_ERROR_DEVICE_REMOVED {
                                    device_removed.store(true, Ordering::Release);
                                    cancel.store(true, Ordering::Release);
                                    break;
                                }
                            }
                        }
                    }
                });
            }
        });
        let pso_create_ms = pso_start.elapsed().as_secs_f64() * 1000.0;
        let compiled_n = compiled.load(Ordering::Relaxed);
        let failed_n = failed.load(Ordering::Relaxed);
        let dev_rem = device_removed.load(Ordering::Acquire);
        let was_paused = paused.load(Ordering::Acquire);

        let mut library_serialized_bytes = 0usize;
        let mut warm_load_ms = 0.0f64;
        if compiled_n > 0
            && !dev_rem
            && !was_paused
            && let Ok((bytes, load_ms)) = self.measure_pipeline_library(&psos, bytecode)
        {
            library_serialized_bytes = bytes;
            warm_load_ms = load_ms;
        }

        let pipelines_per_sec = if pso_create_ms > 0.0 {
            (compiled_n as f64) / (pso_create_ms / 1000.0)
        } else {
            0.0
        };
        let warm_pipelines_per_sec = if warm_load_ms > 0.0 {
            (compiled_n as f64) / (warm_load_ms / 1000.0)
        } else {
            0.0
        };

        Ok(PsoCompileSummary {
            requested: count,
            compiled: compiled_n,
            failed: failed_n,
            paused: was_paused,
            device_removed: dev_rem,
            threads_used: worker_count,
            hlsl_compile_ms: 0.0,
            pso_create_ms,
            pipelines_per_sec,
            library_serialized_bytes,
            warm_load_ms,
            warm_pipelines_per_sec,
            pause_latency_ms: None,
        })
    }

    fn measure_pipeline_library(
        &self,
        psos: &[std::sync::Mutex<Option<ID3D12PipelineState>>],
        bytecode: &[Vec<u8>],
    ) -> Result<(usize, f64), String> {
        unsafe {
            let lib: ID3D12PipelineLibrary = self
                .device
                .CreatePipelineLibrary(&[])
                .map_err(|e| format!("CreatePipelineLibrary(empty) failed: {e}"))?;
            let mut names: Vec<Vec<u16>> = Vec::with_capacity(psos.len());
            for (i, slot) in psos.iter().enumerate() {
                let guard = slot.lock().unwrap();
                if let Some(pso) = guard.as_ref() {
                    let mut wide: Vec<u16> = format!("pso_{i}").encode_utf16().collect();
                    wide.push(0);
                    lib.StorePipeline(PCWSTR(wide.as_ptr()), pso)
                        .map_err(|e| format!("StorePipeline failed: {e}"))?;
                    names.push(wide);
                } else {
                    names.push(Vec::new());
                }
            }
            let size = lib.GetSerializedSize();
            let mut serialized = vec![0u8; size];
            lib.Serialize(&mut serialized)
                .map_err(|e| format!("Serialize pipeline library failed: {e}"))?;

            let warm_lib: ID3D12PipelineLibrary = self
                .device
                .CreatePipelineLibrary(&serialized)
                .map_err(|e| format!("CreatePipelineLibrary(warm) failed: {e}"))?;

            let warm_start = Instant::now();
            for (i, code) in bytecode.iter().enumerate() {
                if names[i].is_empty() {
                    continue;
                }
                let desc = D3D12_COMPUTE_PIPELINE_STATE_DESC {
                    pRootSignature: std::mem::transmute_copy(&self.root_sig),
                    CS: D3D12_SHADER_BYTECODE {
                        pShaderBytecode: code.as_ptr() as *const _,
                        BytecodeLength: code.len(),
                    },
                    NodeMask: 0,
                    CachedPSO: Default::default(),
                    Flags: Default::default(),
                };
                let loaded: windows::core::Result<ID3D12PipelineState> =
                    warm_lib.LoadComputePipeline(PCWSTR(names[i].as_ptr()), &desc);
                let _ = loaded.map_err(|e| format!("LoadComputePipeline failed: {e}"))?;
            }
            let warm_ms = warm_start.elapsed().as_secs_f64() * 1000.0;
            Ok((size, warm_ms))
        }
    }

    #[allow(clippy::too_many_arguments)]
    pub fn compile_recorded_corpus(
        &self,
        records: &[RecordedPsoRecord],
        out_library_path: &Path,
        adapter_vendor: u32,
        adapter_device: u32,
        driver_version: &str,
        threads: usize,
        cancel: &Arc<AtomicBool>,
        below_normal: bool,
    ) -> Result<PsoCompileSummary, String> {
        let count = records.len();
        if count == 0 || cancel.load(Ordering::Acquire) {
            return Ok(PsoCompileSummary {
                requested: count,
                compiled: 0,
                failed: 0,
                paused: cancel.load(Ordering::Acquire),
                device_removed: false,
                threads_used: 0,
                hlsl_compile_ms: 0.0,
                pso_create_ms: 0.0,
                pipelines_per_sec: 0.0,
                library_serialized_bytes: 0,
                warm_load_ms: 0.0,
                warm_pipelines_per_sec: 0.0,
                pause_latency_ms: Some(0.0),
            });
        }

        let mut root_sigs: HashMap<[u8; 32], ID3D12RootSignature> = HashMap::new();
        for rec in records {
            let hash = *blake3::hash(&rec.root_sig_blob).as_bytes();
            if let std::collections::hash_map::Entry::Vacant(slot) = root_sigs.entry(hash)
                && !rec.root_sig_blob.is_empty()
                && let Ok(rs) = unsafe { self.device.CreateRootSignature(0, &rec.root_sig_blob) }
            {
                slot.insert(rs);
            }
        }

        let worker_count = threads.max(1).min(count.max(1));
        let next = AtomicUsize::new(0);
        let compiled = AtomicUsize::new(0);
        let failed = AtomicUsize::new(0);
        let device_removed = AtomicBool::new(false);
        let paused = AtomicBool::new(false);
        let psos: Vec<std::sync::Mutex<Option<ID3D12PipelineState>>> =
            (0..count).map(|_| std::sync::Mutex::new(None)).collect();

        let pso_start = Instant::now();
        std::thread::scope(|s| {
            for _ in 0..worker_count {
                s.spawn(|| {
                    if below_normal {
                        unsafe {
                            let _ =
                                SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
                        }
                    }
                    loop {
                        if cancel.load(Ordering::Acquire) {
                            paused.store(true, Ordering::Release);
                            break;
                        }
                        let i = next.fetch_add(1, Ordering::Relaxed);
                        if i >= count {
                            break;
                        }
                        let rec = &records[i];
                        let rs_hash = *blake3::hash(&rec.root_sig_blob).as_bytes();
                        let Some(rs) = root_sigs.get(&rs_hash) else {
                            failed.fetch_add(1, Ordering::Relaxed);
                            continue;
                        };
                        let res = create_pso_from_record(&self.device, rs, rec);
                        match res {
                            Ok(pso) => {
                                compiled.fetch_add(1, Ordering::Relaxed);
                                *psos[i].lock().unwrap() = Some(pso);
                            }
                            Err(e) => {
                                failed.fetch_add(1, Ordering::Relaxed);
                                if e.code() == DXGI_ERROR_DEVICE_REMOVED {
                                    device_removed.store(true, Ordering::Release);
                                    cancel.store(true, Ordering::Release);
                                    break;
                                }
                            }
                        }
                    }
                });
            }
        });
        let pso_create_ms = pso_start.elapsed().as_secs_f64() * 1000.0;
        let compiled_n = compiled.load(Ordering::Relaxed);
        let failed_n = failed.load(Ordering::Relaxed);
        let dev_rem = device_removed.load(Ordering::Acquire);
        let was_paused = paused.load(Ordering::Acquire);

        let mut library_serialized_bytes = 0usize;
        if compiled_n > 0 && !dev_rem && !was_paused {
            unsafe {
                let lib: ID3D12PipelineLibrary = self
                    .device
                    .CreatePipelineLibrary(&[])
                    .map_err(|e| format!("CreatePipelineLibrary(empty) failed: {e}"))?;
                let mut stored = 0usize;
                for (i, slot) in psos.iter().enumerate() {
                    if let Some(pso) = slot.lock().unwrap().as_ref() {
                        let wname = library_entry_name(&records[i].key_hex);
                        if lib.StorePipeline(PCWSTR(wname.as_ptr()), pso).is_ok() {
                            stored += 1;
                        }
                    }
                }
                let size = lib.GetSerializedSize();
                let mut serialized = vec![0u8; size];
                if size > 0 {
                    lib.Serialize(&mut serialized)
                        .map_err(|e| format!("Serialize recorded pipeline library failed: {e}"))?;
                    write_pipeline_library(
                        out_library_path,
                        adapter_vendor,
                        adapter_device,
                        driver_version,
                        stored,
                        &serialized,
                    )?;
                    library_serialized_bytes = size;
                }
            }
        }

        let pipelines_per_sec = if pso_create_ms > 0.0 {
            (compiled_n as f64) / (pso_create_ms / 1000.0)
        } else {
            0.0
        };

        Ok(PsoCompileSummary {
            requested: count,
            compiled: compiled_n,
            failed: failed_n,
            paused: was_paused,
            device_removed: dev_rem,
            threads_used: worker_count,
            hlsl_compile_ms: 0.0,
            pso_create_ms,
            pipelines_per_sec,
            library_serialized_bytes,
            warm_load_ms: 0.0,
            warm_pipelines_per_sec: 0.0,
            pause_latency_ms: None,
        })
    }
}

fn shader_bytecode(bytes: &[u8]) -> D3D12_SHADER_BYTECODE {
    if bytes.is_empty() {
        D3D12_SHADER_BYTECODE::default()
    } else {
        D3D12_SHADER_BYTECODE {
            pShaderBytecode: bytes.as_ptr() as *const _,
            BytecodeLength: bytes.len(),
        }
    }
}

fn read_pod_or_default<T: Default>(bytes: &[u8]) -> T {
    if bytes.len() == std::mem::size_of::<T>() {
        unsafe { std::ptr::read_unaligned(bytes.as_ptr() as *const T) }
    } else {
        T::default()
    }
}

fn create_pso_from_record(
    device: &ID3D12Device1,
    rs: &ID3D12RootSignature,
    rec: &RecordedPsoRecord,
) -> windows::core::Result<ID3D12PipelineState> {
    match rec.kind {
        RecordedPsoKind::Compute => {
            let desc = D3D12_COMPUTE_PIPELINE_STATE_DESC {
                pRootSignature: unsafe { std::mem::transmute_copy(rs) },
                CS: shader_bytecode(&rec.cs),
                NodeMask: rec.node_mask,
                CachedPSO: Default::default(),
                Flags: Default::default(),
            };
            unsafe { device.CreateComputePipelineState(&desc) }
        }
        RecordedPsoKind::Graphics => {
            let state: &RecordedGraphicsState = match rec.graphics_state.as_ref() {
                Some(s) => s,
                None => {
                    return Err(windows::core::Error::from(
                        windows::Win32::Foundation::E_INVALIDARG,
                    ));
                }
            };
            let c_names: Vec<CString> = state
                .input_elements
                .iter()
                .map(|el| CString::new(el.semantic_name.as_str()).unwrap_or_default())
                .collect();
            let elements: Vec<D3D12_INPUT_ELEMENT_DESC> = state
                .input_elements
                .iter()
                .zip(c_names.iter())
                .map(|(el, name)| D3D12_INPUT_ELEMENT_DESC {
                    SemanticName: PCSTR(name.as_ptr() as *const u8),
                    SemanticIndex: el.semantic_index,
                    Format: DXGI_FORMAT(el.format as i32),
                    InputSlot: el.input_slot,
                    AlignedByteOffset: el.aligned_byte_offset,
                    InputSlotClass: D3D12_INPUT_CLASSIFICATION(el.input_slot_class as i32),
                    InstanceDataStepRate: el.instance_data_step_rate,
                })
                .collect();
            let mut rtv_formats = [DXGI_FORMAT(0); 8];
            for (dst, &raw) in rtv_formats.iter_mut().zip(state.rtv_formats.iter()) {
                *dst = DXGI_FORMAT(raw as i32);
            }
            let desc = D3D12_GRAPHICS_PIPELINE_STATE_DESC {
                pRootSignature: unsafe { std::mem::transmute_copy(rs) },
                VS: shader_bytecode(&rec.vs),
                PS: shader_bytecode(&rec.ps),
                DS: shader_bytecode(&rec.ds),
                HS: shader_bytecode(&rec.hs),
                GS: shader_bytecode(&rec.gs),
                StreamOutput: Default::default(),
                BlendState: read_pod_or_default::<D3D12_BLEND_DESC>(&state.blend_state_bytes),
                SampleMask: state.sample_mask,
                RasterizerState: read_pod_or_default::<D3D12_RASTERIZER_DESC>(
                    &state.rasterizer_state_bytes,
                ),
                DepthStencilState: read_pod_or_default::<D3D12_DEPTH_STENCIL_DESC>(
                    &state.depth_stencil_state_bytes,
                ),
                InputLayout: D3D12_INPUT_LAYOUT_DESC {
                    pInputElementDescs: if elements.is_empty() {
                        std::ptr::null()
                    } else {
                        elements.as_ptr()
                    },
                    NumElements: elements.len() as u32,
                },
                IBStripCutValue: D3D12_INDEX_BUFFER_STRIP_CUT_VALUE(
                    state.ib_strip_cut_value as i32,
                ),
                PrimitiveTopologyType: D3D12_PRIMITIVE_TOPOLOGY_TYPE(
                    state.primitive_topology_type as i32,
                ),
                NumRenderTargets: state.num_render_targets,
                RTVFormats: rtv_formats,
                DSVFormat: DXGI_FORMAT(state.dsv_format as i32),
                SampleDesc: DXGI_SAMPLE_DESC {
                    Count: state.sample_count.max(1),
                    Quality: state.sample_quality,
                },
                NodeMask: rec.node_mask,
                CachedPSO: Default::default(),
                Flags: Default::default(),
            };
            unsafe { device.CreateGraphicsPipelineState(&desc) }
        }
    }
}

fn compile_single_cs(index: usize) -> Result<Vec<u8>, String> {
    let taps = 8 + (index % 12);
    let mul = (index as f32 * 0.0137) + 1.001;
    let shift = (index % 7) + 1;
    let src = format!(
        "groupshared float gs[64];\n\
         [numthreads(64, 1, 1)]\n\
         void main(uint3 tid : SV_DispatchThreadID, uint gi : SV_GroupIndex) {{\n\
             float acc = (float)(tid.x + {index}) * {mul:.6}f;\n\
             [unroll]\n\
             for (uint k = 0; k < {taps}; ++k) {{\n\
                 acc = sin(acc * 1.17f + (float)k * 0.31f) + cos(acc * 0.73f + {shift}.0f);\n\
             }}\n\
             gs[gi] = acc;\n\
             GroupMemoryBarrierWithGroupSync();\n\
             gs[(gi + {shift}) & 63] = gs[gi] * 0.5f + acc;\n\
         }}\n"
    );
    unsafe {
        let mut blob: Option<ID3DBlob> = None;
        let mut err: Option<ID3DBlob> = None;
        let entry = b"main\0";
        let target = b"cs_5_0\0";
        D3DCompile(
            src.as_ptr() as *const _,
            src.len(),
            PCSTR::null(),
            None,
            None,
            PCSTR(entry.as_ptr()),
            PCSTR(target.as_ptr()),
            D3DCOMPILE_OPTIMIZATION_LEVEL3,
            0,
            &mut blob,
            Some(&mut err),
        )
        .map_err(|e| {
            let msg = err
                .as_ref()
                .map(|b| {
                    let s = std::slice::from_raw_parts(
                        b.GetBufferPointer() as *const u8,
                        b.GetBufferSize(),
                    );
                    String::from_utf8_lossy(s).into_owned()
                })
                .unwrap_or_default();
            format!("D3DCompile failed for shader {index}: {e} {msg}")
        })?;
        let blob = blob.ok_or_else(|| "D3DCompile returned null blob".to_string())?;
        let slice =
            std::slice::from_raw_parts(blob.GetBufferPointer() as *const u8, blob.GetBufferSize());
        Ok(slice.to_vec())
    }
}

pub fn recommended_worker_threads() -> usize {
    let cores = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(4);
    cores.saturating_sub(2).max(1)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dx12_compiles_and_warms_small_corpus() {
        let ctx = Dx12WarmContext::new().expect("DX12 hardware device required");
        let (blobs, _) = Dx12WarmContext::compile_hlsl_corpus(16, 4).expect("HLSL compile");
        assert_eq!(blobs.len(), 16);
        let cancel = Arc::new(AtomicBool::new(false));
        let summary = ctx
            .warm_compute_psos(&blobs, 4, &cancel, true)
            .expect("PSO warm");
        assert_eq!(summary.compiled, 16);
        assert_eq!(summary.failed, 0);
        assert!(!summary.device_removed);
        assert!(summary.library_serialized_bytes > 0);
    }

    #[test]
    fn dx12_pauses_cooperatively_on_cancel_signal() {
        let ctx = Dx12WarmContext::new().expect("DX12 hardware device required");
        let (blobs, _) = Dx12WarmContext::compile_hlsl_corpus(60, 4).expect("HLSL compile");
        let cancel = Arc::new(AtomicBool::new(true));
        let t0 = Instant::now();
        let summary = ctx
            .warm_compute_psos(&blobs, 2, &cancel, true)
            .expect("PSO warm");
        let elapsed_ms = t0.elapsed().as_secs_f64() * 1000.0;
        assert!(summary.paused);
        assert_eq!(summary.compiled, 0);
        assert!(
            elapsed_ms < 50.0,
            "cancel check latency {elapsed_ms:.2} ms exceeded 50 ms budget"
        );
    }
}
