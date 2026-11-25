#![allow(clippy::missing_safety_doc)]

pub mod com_proxy;
pub mod loader;
#[path = "../../exo-shaders/src/pso_format.rs"]
pub mod pso_format;
pub mod stream_parse;

use std::ffi::c_void;
use std::path::PathBuf;

use crate::pso_format::{RECORDER_CONFIG_NAME, RecorderConfig};
use windows::Win32::Foundation::{E_FAIL, HMODULE};
use windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL;
use windows::Win32::Graphics::Direct3D12::{
    D3D_ROOT_SIGNATURE_VERSION, D3D12_ROOT_SIGNATURE_DESC, D3D12_VERSIONED_ROOT_SIGNATURE_DESC,
    ID3D12Device,
};
use windows::Win32::System::LibraryLoader::DisableThreadLibraryCalls;
use windows::core::{BOOL, GUID, HRESULT, Interface};

const DLL_PROCESS_ATTACH: u32 = 1;

pub fn load_recorder_config() -> Option<RecorderConfig> {
    if let Ok(path) = std::env::var("EXO_SHADERS_RECORDER_CONFIG")
        && !path.is_empty()
        && let Ok(raw) = std::fs::read_to_string(&path)
        && let Ok(cfg) = serde_json::from_str::<RecorderConfig>(&raw)
    {
        return Some(cfg);
    }

    let mut candidates: Vec<PathBuf> = Vec::new();
    if let Some(dll_dir) = loader::current_module_dir() {
        candidates.push(dll_dir.join(RECORDER_CONFIG_NAME));
    }
    if let Ok(exe) = std::env::current_exe()
        && let Some(exe_dir) = exe.parent()
    {
        let p = exe_dir.join(RECORDER_CONFIG_NAME);
        if !candidates.contains(&p) {
            candidates.push(p);
        }
    }

    for candidate in candidates {
        if let Ok(raw) = std::fs::read_to_string(&candidate)
            && let Ok(cfg) = serde_json::from_str::<RecorderConfig>(&raw)
        {
            return Some(cfg);
        }
    }
    None
}

pub unsafe fn wrap_device_for_config(device: ID3D12Device, config: RecorderConfig) -> ID3D12Device {
    if !config.enabled {
        return device;
    }
    let raw = device.into_raw();
    let wrapped = unsafe { com_proxy::ProxyDevice::wrap_raw(raw, config) };
    unsafe { ID3D12Device::from_raw(wrapped) }
}

#[allow(non_snake_case)]
#[cfg_attr(not(test), unsafe(no_mangle))]
pub unsafe extern "system" fn DllMain(
    module: HMODULE,
    reason: u32,
    _reserved: *mut c_void,
) -> BOOL {
    if reason == DLL_PROCESS_ATTACH {
        let _ = unsafe { DisableThreadLibraryCalls(module) };
    }
    BOOL(1)
}

#[allow(non_snake_case)]
#[cfg_attr(not(test), unsafe(no_mangle))]
pub unsafe extern "system" fn D3D12CreateDevice(
    padapter: *mut c_void,
    min_feature_level: D3D_FEATURE_LEVEL,
    riid: *const GUID,
    ppdevice: *mut *mut c_void,
) -> HRESULT {
    let Some(real) = loader::real_d3d12().and_then(|r| r.create_device) else {
        return E_FAIL;
    };
    let hr = unsafe { real(padapter, min_feature_level, riid, ppdevice) };
    if hr.is_err() || ppdevice.is_null() || unsafe { *ppdevice }.is_null() || riid.is_null() {
        return hr;
    }
    let Some(cfg) = load_recorder_config() else {
        return hr;
    };
    if !cfg.enabled {
        return hr;
    }
    if com_proxy::device_iid_level(unsafe { &*riid }).is_none() {
        return hr;
    }
    let wrapped = unsafe { com_proxy::ProxyDevice::wrap_raw(*ppdevice, cfg) };
    unsafe { *ppdevice = wrapped };
    hr
}

#[allow(non_snake_case)]
#[cfg_attr(not(test), unsafe(no_mangle))]
pub unsafe extern "system" fn D3D12GetDebugInterface(
    riid: *const GUID,
    ppv: *mut *mut c_void,
) -> HRESULT {
    let Some(real) = loader::real_d3d12().and_then(|r| r.get_debug_interface) else {
        return E_FAIL;
    };
    unsafe { real(riid, ppv) }
}

#[allow(non_snake_case)]
#[cfg_attr(not(test), unsafe(no_mangle))]
pub unsafe extern "system" fn D3D12SerializeRootSignature(
    prootsig: *const D3D12_ROOT_SIGNATURE_DESC,
    version: D3D_ROOT_SIGNATURE_VERSION,
    ppblob: *mut *mut c_void,
    pperror: *mut *mut c_void,
) -> HRESULT {
    let Some(real) = loader::real_d3d12().and_then(|r| r.serialize_root_signature) else {
        return E_FAIL;
    };
    unsafe { real(prootsig, version, ppblob, pperror) }
}

#[allow(non_snake_case)]
#[cfg_attr(not(test), unsafe(no_mangle))]
pub unsafe extern "system" fn D3D12SerializeVersionedRootSignature(
    prootsig: *const D3D12_VERSIONED_ROOT_SIGNATURE_DESC,
    ppblob: *mut *mut c_void,
    pperror: *mut *mut c_void,
) -> HRESULT {
    let Some(real) = loader::real_d3d12().and_then(|r| r.serialize_versioned_root_signature) else {
        return E_FAIL;
    };
    unsafe { real(prootsig, ppblob, pperror) }
}

#[allow(non_snake_case)]
#[cfg_attr(not(test), unsafe(no_mangle))]
pub unsafe extern "system" fn D3D12CreateRootSignatureDeserializer(
    psrc: *const c_void,
    len: usize,
    riid: *const GUID,
    ppv: *mut *mut c_void,
) -> HRESULT {
    let Some(real) = loader::real_d3d12().and_then(|r| r.create_root_signature_deserializer) else {
        return E_FAIL;
    };
    unsafe { real(psrc, len, riid, ppv) }
}

#[allow(non_snake_case)]
#[cfg_attr(not(test), unsafe(no_mangle))]
pub unsafe extern "system" fn D3D12CreateVersionedRootSignatureDeserializer(
    psrc: *const c_void,
    len: usize,
    riid: *const GUID,
    ppv: *mut *mut c_void,
) -> HRESULT {
    let Some(real) =
        loader::real_d3d12().and_then(|r| r.create_versioned_root_signature_deserializer)
    else {
        return E_FAIL;
    };
    unsafe { real(psrc, len, riid, ppv) }
}

#[allow(non_snake_case)]
#[cfg_attr(not(test), unsafe(no_mangle))]
pub unsafe extern "system" fn D3D12EnableExperimentalFeatures(
    num_features: u32,
    piids: *const GUID,
    pconfigs: *const c_void,
    psizes: *const u32,
) -> HRESULT {
    let Some(real) = loader::real_d3d12().and_then(|r| r.enable_experimental_features) else {
        return E_FAIL;
    };
    unsafe { real(num_features, piids, pconfigs, psizes) }
}

#[allow(non_snake_case)]
#[cfg_attr(not(test), unsafe(no_mangle))]
pub unsafe extern "system" fn D3D12GetInterface(
    rclsid: *const GUID,
    riid: *const GUID,
    ppv: *mut *mut c_void,
) -> HRESULT {
    let Some(real) = loader::real_d3d12().and_then(|r| r.get_interface) else {
        return E_FAIL;
    };
    unsafe { real(rclsid, riid, ppv) }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::ManuallyDrop;
    use std::sync::Arc;
    use std::sync::atomic::AtomicBool;
    use std::time::Instant;

    use crate::pso_format::{
        DEFAULT_MAX_RECORDED_PSOS, RecorderMode, RecorderStats, write_pipeline_library,
    };
    use exo_shaders::pso_dx12::Dx12WarmContext;
    use exo_shaders::pso_store::read_pso_records;
    use windows::Win32::Graphics::Direct3D::Fxc::D3DCompile;
    use windows::Win32::Graphics::Direct3D::{D3D_FEATURE_LEVEL_11_0, ID3DBlob};
    use windows::Win32::Graphics::Direct3D12::{
        D3D_ROOT_SIGNATURE_VERSION_1, D3D12_BLEND_DESC, D3D12_COMPUTE_PIPELINE_STATE_DESC,
        D3D12_CULL_MODE_NONE, D3D12_FILL_MODE_SOLID, D3D12_GRAPHICS_PIPELINE_STATE_DESC,
        D3D12_PIPELINE_STATE_FLAG_NONE, D3D12_PIPELINE_STATE_STREAM_DESC,
        D3D12_PIPELINE_STATE_SUBOBJECT_TYPE, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS,
        D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE, D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE,
        D3D12_RASTERIZER_DESC, D3D12_RENDER_TARGET_BLEND_DESC, D3D12_ROOT_CONSTANTS,
        D3D12_ROOT_DESCRIPTOR, D3D12_ROOT_PARAMETER, D3D12_ROOT_PARAMETER_0,
        D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS, D3D12_ROOT_PARAMETER_TYPE_UAV,
        D3D12_ROOT_SIGNATURE_DESC, D3D12_ROOT_SIGNATURE_FLAG_NONE, D3D12_SHADER_BYTECODE,
        D3D12_SHADER_VISIBILITY_ALL, ID3D12Device2, ID3D12PipelineState, ID3D12RootSignature,
    };
    use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_SAMPLE_DESC};
    use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIAdapter1, IDXGIFactory4};
    use windows::core::PCSTR;

    fn compile_hlsl(source: &str, entry: &str, target: &str) -> Vec<u8> {
        let mut blob: Option<ID3DBlob> = None;
        let mut err: Option<ID3DBlob> = None;
        let entry_c = std::ffi::CString::new(entry).unwrap();
        let target_c = std::ffi::CString::new(target).unwrap();
        let hr = unsafe {
            D3DCompile(
                source.as_ptr().cast(),
                source.len(),
                None,
                None,
                None,
                PCSTR(entry_c.as_ptr().cast()),
                PCSTR(target_c.as_ptr().cast()),
                0,
                0,
                &mut blob,
                Some(&mut err),
            )
        };
        assert!(hr.is_ok(), "D3DCompile failed: {hr:?}");
        let b = blob.unwrap();
        unsafe { std::slice::from_raw_parts(b.GetBufferPointer().cast::<u8>(), b.GetBufferSize()) }
            .to_vec()
    }

    fn create_hw_device() -> Option<ID3D12Device> {
        let factory: IDXGIFactory4 = unsafe { CreateDXGIFactory1() }.ok()?;
        let adapter: IDXGIAdapter1 = unsafe { factory.EnumAdapters1(0) }.ok()?;
        let mut dev: Option<ID3D12Device> = None;
        unsafe {
            windows::Win32::Graphics::Direct3D12::D3D12CreateDevice(
                &adapter,
                D3D_FEATURE_LEVEL_11_0,
                &mut dev,
            )
        }
        .ok()?;
        dev
    }

    fn serialize_root_sig() -> Vec<u8> {
        let params = [
            D3D12_ROOT_PARAMETER {
                ParameterType: D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS,
                Anonymous: D3D12_ROOT_PARAMETER_0 {
                    Constants: D3D12_ROOT_CONSTANTS {
                        ShaderRegister: 0,
                        RegisterSpace: 0,
                        Num32BitValues: 4,
                    },
                },
                ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL,
            },
            D3D12_ROOT_PARAMETER {
                ParameterType: D3D12_ROOT_PARAMETER_TYPE_UAV,
                Anonymous: D3D12_ROOT_PARAMETER_0 {
                    Descriptor: D3D12_ROOT_DESCRIPTOR {
                        ShaderRegister: 0,
                        RegisterSpace: 0,
                    },
                },
                ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL,
            },
        ];
        let desc = D3D12_ROOT_SIGNATURE_DESC {
            NumParameters: params.len() as u32,
            pParameters: params.as_ptr(),
            NumStaticSamplers: 0,
            pStaticSamplers: std::ptr::null(),
            Flags: D3D12_ROOT_SIGNATURE_FLAG_NONE,
        };
        let mut blob: Option<ID3DBlob> = None;
        let mut err: Option<ID3DBlob> = None;
        unsafe {
            windows::Win32::Graphics::Direct3D12::D3D12SerializeRootSignature(
                &desc,
                D3D_ROOT_SIGNATURE_VERSION_1,
                &mut blob,
                Some(&mut err),
            )
        }
        .unwrap();
        let b = blob.unwrap();
        unsafe { std::slice::from_raw_parts(b.GetBufferPointer().cast::<u8>(), b.GetBufferSize()) }
            .to_vec()
    }

    fn make_cs(variant: u32) -> Vec<u8> {
        let src = format!(
            "cbuffer CB : register(b0) {{ float4 p; }};\n\
             RWStructuredBuffer<float> Out : register(u0);\n\
             [numthreads(8, 8, 1)]\n\
             void main(uint3 id : SV_DispatchThreadID) {{\n\
                 float v = p.x + float(id.x) * 0.01 + {variant}.0;\n\
                 [unroll] for (int i = 0; i < 6; ++i) {{ v = sin(v + float(i)) * cos(v - {variant}.0 * 0.1) + v * 0.5; }}\n\
                 Out[id.x] = v;\n\
             }}"
        );
        compile_hlsl(&src, "main", "cs_5_0")
    }

    fn make_vs(variant: u32) -> Vec<u8> {
        let src = format!(
            "cbuffer CB : register(b0) {{ float4 p; }};\n\
             float4 main(uint id : SV_VertexID) : SV_Position {{\n\
                 return float4(float(id & 1), float((id >> 1) & 1), p.z + {variant}.0 * 0.01, 1.0);\n\
             }}"
        );
        compile_hlsl(&src, "main", "vs_5_0")
    }

    fn make_ps(variant: u32) -> Vec<u8> {
        let src = format!(
            "cbuffer CB : register(b0) {{ float4 p; }};\n\
             float4 main(float4 pos : SV_Position) : SV_Target0 {{\n\
                 float v = pos.x * 0.01 + p.y + {variant}.0;\n\
                 [unroll] for (int i = 0; i < 6; ++i) {{ v = sin(v + float(i)) + cos(v * 0.5); }}\n\
                 return float4(v, p.y, p.z, 1.0);\n\
             }}"
        );
        compile_hlsl(&src, "main", "ps_5_0")
    }

    #[repr(C, align(8))]
    struct CsStreamSubobjectRootSig {
        ty: D3D12_PIPELINE_STATE_SUBOBJECT_TYPE,
        rs: *mut c_void,
    }

    #[repr(C, align(8))]
    struct CsStreamSubobjectCs {
        ty: D3D12_PIPELINE_STATE_SUBOBJECT_TYPE,
        cs: D3D12_SHADER_BYTECODE,
    }

    #[repr(C, align(8))]
    struct CsPipelineStream {
        root_sig: CsStreamSubobjectRootSig,
        cs: CsStreamSubobjectCs,
    }

    #[test]
    fn d3d12_record_compile_and_warm_roundtrip() {
        let Some(dev1_raw) = create_hw_device() else {
            return;
        };
        let (vendor_id, device_id, driver_version) =
            com_proxy::query_adapter_info(&dev1_raw).unwrap();

        let root = std::env::temp_dir().join(format!("exo-rec-e2e-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).unwrap();

        let record_path = root.join("game.exopso");
        let library_path = root.join("game.exoplib");
        let rec_stats_path = root.join("rec-stats.json");
        let warm_stats_path = root.join("warm-stats.json");
        let mismatch_stats_path = root.join("mismatch-stats.json");

        let rs_blob = serialize_root_sig();
        let cs_list: Vec<Vec<u8>> = (0..6).map(make_cs).collect();
        let stream_cs_list: Vec<Vec<u8>> = (100..102).map(make_cs).collect();
        let gfx_pairs: Vec<(Vec<u8>, Vec<u8>)> = (0..4).map(|i| (make_vs(i), make_ps(i))).collect();

        let create_all_psos = |dev: &ID3D12Device| {
            let rs: ID3D12RootSignature = unsafe { dev.CreateRootSignature(0, &rs_blob) }.unwrap();
            let mut psos: Vec<ID3D12PipelineState> = Vec::new();
            for cs in &cs_list {
                let desc = D3D12_COMPUTE_PIPELINE_STATE_DESC {
                    pRootSignature: ManuallyDrop::new(Some(rs.clone())),
                    CS: D3D12_SHADER_BYTECODE {
                        pShaderBytecode: cs.as_ptr().cast(),
                        BytecodeLength: cs.len(),
                    },
                    NodeMask: 0,
                    CachedPSO: Default::default(),
                    Flags: D3D12_PIPELINE_STATE_FLAG_NONE,
                };
                let pso: ID3D12PipelineState =
                    unsafe { dev.CreateComputePipelineState(&desc) }.unwrap();
                let mut desc = desc;
                unsafe { ManuallyDrop::drop(&mut desc.pRootSignature) };
                psos.push(pso);
            }

            let dev2: ID3D12Device2 = dev.cast().unwrap();
            for cs in &stream_cs_list {
                let stream = CsPipelineStream {
                    root_sig: CsStreamSubobjectRootSig {
                        ty: D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE,
                        rs: rs.as_raw(),
                    },
                    cs: CsStreamSubobjectCs {
                        ty: D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS,
                        cs: D3D12_SHADER_BYTECODE {
                            pShaderBytecode: cs.as_ptr().cast(),
                            BytecodeLength: cs.len(),
                        },
                    },
                };
                let sdesc = D3D12_PIPELINE_STATE_STREAM_DESC {
                    SizeInBytes: std::mem::size_of::<CsPipelineStream>(),
                    pPipelineStateSubobjectStream: (&stream as *const CsPipelineStream)
                        .cast_mut()
                        .cast(),
                };
                let pso: ID3D12PipelineState = unsafe { dev2.CreatePipelineState(&sdesc) }.unwrap();
                psos.push(pso);
            }

            let mut blend = D3D12_BLEND_DESC::default();
            blend.RenderTarget[0] = D3D12_RENDER_TARGET_BLEND_DESC {
                RenderTargetWriteMask: 0xF,
                ..Default::default()
            };
            let raster = D3D12_RASTERIZER_DESC {
                FillMode: D3D12_FILL_MODE_SOLID,
                CullMode: D3D12_CULL_MODE_NONE,
                DepthClipEnable: BOOL(1),
                ..Default::default()
            };
            for (vs, ps) in &gfx_pairs {
                let mut rtv_formats = [Default::default(); 8];
                rtv_formats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
                let desc = D3D12_GRAPHICS_PIPELINE_STATE_DESC {
                    pRootSignature: ManuallyDrop::new(Some(rs.clone())),
                    VS: D3D12_SHADER_BYTECODE {
                        pShaderBytecode: vs.as_ptr().cast(),
                        BytecodeLength: vs.len(),
                    },
                    PS: D3D12_SHADER_BYTECODE {
                        pShaderBytecode: ps.as_ptr().cast(),
                        BytecodeLength: ps.len(),
                    },
                    BlendState: blend,
                    SampleMask: u32::MAX,
                    RasterizerState: raster,
                    PrimitiveTopologyType: D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE,
                    NumRenderTargets: 1,
                    RTVFormats: rtv_formats,
                    SampleDesc: DXGI_SAMPLE_DESC {
                        Count: 1,
                        Quality: 0,
                    },
                    ..Default::default()
                };
                let pso: ID3D12PipelineState =
                    unsafe { dev.CreateGraphicsPipelineState(&desc) }.unwrap();
                let mut desc = desc;
                unsafe { ManuallyDrop::drop(&mut desc.pRootSignature) };
                psos.push(pso);
            }
            psos
        };

        let make_cfg =
            |mode: RecorderMode, lib: &std::path::Path, stats: &std::path::Path| RecorderConfig {
                version: 1,
                enabled: true,
                game_id: "test:synthetic".into(),
                mode,
                record_path: record_path.to_string_lossy().into_owned(),
                library_path: lib.to_string_lossy().into_owned(),
                expected_adapter_vendor: vendor_id,
                expected_adapter_device: device_id,
                expected_driver_version: driver_version.clone(),
                max_recorded_psos: DEFAULT_MAX_RECORDED_PSOS,
                stats_path: Some(stats.to_string_lossy().into_owned()),
            };

        {
            let wrapped = unsafe {
                wrap_device_for_config(
                    dev1_raw,
                    make_cfg(RecorderMode::Record, &library_path, &rec_stats_path),
                )
            };
            let psos = create_all_psos(&wrapped);
            assert_eq!(psos.len(), 12);
            drop(psos);
            drop(wrapped);
        }

        let rec_stats: RecorderStats =
            serde_json::from_str(&std::fs::read_to_string(&rec_stats_path).unwrap()).unwrap();
        assert_eq!(rec_stats.recorded_count, 12);
        let corpus = read_pso_records(&record_path).unwrap();
        assert_eq!(corpus.len(), 12);

        let warm_ctx = Dx12WarmContext::new().unwrap();
        let cancel = Arc::new(AtomicBool::new(false));
        let compile_summary = warm_ctx
            .compile_recorded_corpus(
                &corpus,
                &library_path,
                vendor_id,
                device_id,
                &driver_version,
                2,
                &cancel,
                false,
            )
            .unwrap();
        assert_eq!(compile_summary.compiled, 12);
        assert_eq!(compile_summary.failed, 0);
        assert!(library_path.exists());

        let warm_start = Instant::now();
        {
            let dev2_raw = create_hw_device().unwrap();
            let wrapped = unsafe {
                wrap_device_for_config(
                    dev2_raw,
                    make_cfg(RecorderMode::Warm, &library_path, &warm_stats_path),
                )
            };
            let psos = create_all_psos(&wrapped);
            assert_eq!(psos.len(), 12);
            drop(psos);
            drop(wrapped);
        }
        let _warm_ms = warm_start.elapsed().as_secs_f64() * 1000.0;

        let warm_stats: RecorderStats =
            serde_json::from_str(&std::fs::read_to_string(&warm_stats_path).unwrap()).unwrap();
        assert!(warm_stats.library_loaded);
        assert_eq!(warm_stats.warm_hits, 12);
        assert_eq!(warm_stats.warm_misses, 0);

        let mismatch_lib = root.join("mismatch.exoplib");
        write_pipeline_library(&mismatch_lib, 0xDEAD, device_id, "99.99.99.99", 1, b"bad").unwrap();
        {
            let dev3_raw = create_hw_device().unwrap();
            let wrapped = unsafe {
                wrap_device_for_config(
                    dev3_raw,
                    make_cfg(RecorderMode::Warm, &mismatch_lib, &mismatch_stats_path),
                )
            };
            let psos = create_all_psos(&wrapped);
            assert_eq!(psos.len(), 12);
            drop(psos);
            drop(wrapped);
        }
        let mismatch_stats: RecorderStats =
            serde_json::from_str(&std::fs::read_to_string(&mismatch_stats_path).unwrap()).unwrap();
        assert!(!mismatch_stats.library_loaded);
        assert_eq!(mismatch_stats.warm_hits, 0);
        assert_eq!(mismatch_stats.fallback_count, 12);

        let _ = std::fs::remove_dir_all(&root);
    }
}
