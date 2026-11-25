use std::ffi::{CStr, c_void};
use std::mem::{align_of, size_of};

use crate::pso_format::{
    RecordedGraphicsState, RecordedInputElement, RecordedPsoKind, RecordedPsoRecord,
    compute_pso_key, graphics_pso_key,
};
use windows::Win32::Graphics::Direct3D12::{
    D3D12_BLEND_DESC, D3D12_CACHED_PIPELINE_STATE, D3D12_COMPUTE_PIPELINE_STATE_DESC,
    D3D12_DEPTH_STENCIL_DESC, D3D12_DEPTH_STENCIL_DESC1, D3D12_GRAPHICS_PIPELINE_STATE_DESC,
    D3D12_INDEX_BUFFER_STRIP_CUT_VALUE, D3D12_INPUT_LAYOUT_DESC, D3D12_PIPELINE_STATE_FLAGS,
    D3D12_PIPELINE_STATE_STREAM_DESC, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_BLEND, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CACHED_PSO,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL_FORMAT,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL1, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DS,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_FLAGS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_GS,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_HS, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_IB_STRIP_CUT_VALUE,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_NODE_MASK,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PRIMITIVE_TOPOLOGY, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_DESC,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_MASK,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT,
    D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VIEW_INSTANCING, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS,
    D3D12_PRIMITIVE_TOPOLOGY_TYPE, D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE, D3D12_RASTERIZER_DESC,
    D3D12_RT_FORMAT_ARRAY, D3D12_SHADER_BYTECODE, D3D12_STREAM_OUTPUT_DESC,
    D3D12_VIEW_INSTANCING_DESC,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT, DXGI_SAMPLE_DESC};

fn align_up(value: usize, align: usize) -> usize {
    (value + align - 1) & !(align - 1)
}

fn canonical_blend_bytes(src: &D3D12_BLEND_DESC) -> Vec<u8> {
    let mut clean: D3D12_BLEND_DESC = unsafe { std::mem::zeroed() };
    clean.AlphaToCoverageEnable = src.AlphaToCoverageEnable;
    clean.IndependentBlendEnable = src.IndependentBlendEnable;
    for (dst_rt, src_rt) in clean.RenderTarget.iter_mut().zip(src.RenderTarget.iter()) {
        dst_rt.BlendEnable = src_rt.BlendEnable;
        dst_rt.LogicOpEnable = src_rt.LogicOpEnable;
        dst_rt.SrcBlend = src_rt.SrcBlend;
        dst_rt.DestBlend = src_rt.DestBlend;
        dst_rt.BlendOp = src_rt.BlendOp;
        dst_rt.SrcBlendAlpha = src_rt.SrcBlendAlpha;
        dst_rt.DestBlendAlpha = src_rt.DestBlendAlpha;
        dst_rt.BlendOpAlpha = src_rt.BlendOpAlpha;
        dst_rt.LogicOp = src_rt.LogicOp;
        dst_rt.RenderTargetWriteMask = src_rt.RenderTargetWriteMask;
    }
    unsafe { copy_struct_bytes(&clean) }
}

fn canonical_rasterizer_bytes(src: &D3D12_RASTERIZER_DESC) -> Vec<u8> {
    let mut clean: D3D12_RASTERIZER_DESC = unsafe { std::mem::zeroed() };
    clean.FillMode = src.FillMode;
    clean.CullMode = src.CullMode;
    clean.FrontCounterClockwise = src.FrontCounterClockwise;
    clean.DepthBias = src.DepthBias;
    clean.DepthBiasClamp = src.DepthBiasClamp;
    clean.SlopeScaledDepthBias = src.SlopeScaledDepthBias;
    clean.DepthClipEnable = src.DepthClipEnable;
    clean.MultisampleEnable = src.MultisampleEnable;
    clean.AntialiasedLineEnable = src.AntialiasedLineEnable;
    clean.ForcedSampleCount = src.ForcedSampleCount;
    clean.ConservativeRaster = src.ConservativeRaster;
    unsafe { copy_struct_bytes(&clean) }
}

fn canonical_depth_stencil_bytes(src: &D3D12_DEPTH_STENCIL_DESC) -> Vec<u8> {
    let mut clean: D3D12_DEPTH_STENCIL_DESC = unsafe { std::mem::zeroed() };
    clean.DepthEnable = src.DepthEnable;
    clean.DepthWriteMask = src.DepthWriteMask;
    clean.DepthFunc = src.DepthFunc;
    clean.StencilEnable = src.StencilEnable;
    clean.StencilReadMask = src.StencilReadMask;
    clean.StencilWriteMask = src.StencilWriteMask;
    clean.FrontFace = src.FrontFace;
    clean.BackFace = src.BackFace;
    unsafe { copy_struct_bytes(&clean) }
}

unsafe fn copy_struct_bytes<T>(value: &T) -> Vec<u8> {
    let ptr = (value as *const T).cast::<u8>();
    unsafe { std::slice::from_raw_parts(ptr, size_of::<T>()).to_vec() }
}

unsafe fn read_bytecode(bc: &D3D12_SHADER_BYTECODE) -> Vec<u8> {
    if bc.pShaderBytecode.is_null() || bc.BytecodeLength == 0 {
        return Vec::new();
    }
    unsafe { std::slice::from_raw_parts(bc.pShaderBytecode.cast::<u8>(), bc.BytecodeLength) }
        .to_vec()
}

unsafe fn read_input_layout(layout: &D3D12_INPUT_LAYOUT_DESC) -> Vec<RecordedInputElement> {
    if layout.pInputElementDescs.is_null() || layout.NumElements == 0 {
        return Vec::new();
    }
    let slice = unsafe {
        std::slice::from_raw_parts(layout.pInputElementDescs, layout.NumElements as usize)
    };
    let mut out = Vec::with_capacity(slice.len());
    for elem in slice {
        let semantic_name = if elem.SemanticName.0.is_null() {
            String::new()
        } else {
            unsafe { CStr::from_ptr(elem.SemanticName.0.cast()) }
                .to_string_lossy()
                .into_owned()
        };
        out.push(RecordedInputElement {
            semantic_name,
            semantic_index: elem.SemanticIndex,
            format: elem.Format.0 as u32,
            input_slot: elem.InputSlot,
            aligned_byte_offset: elem.AlignedByteOffset,
            input_slot_class: elem.InputSlotClass.0 as u32,
            instance_data_step_rate: elem.InstanceDataStepRate,
        });
    }
    out
}

pub unsafe fn record_from_compute_desc(
    desc: &D3D12_COMPUTE_PIPELINE_STATE_DESC,
    root_sig_blob: Vec<u8>,
) -> Option<RecordedPsoRecord> {
    let cs = unsafe { read_bytecode(&desc.CS) };
    if cs.is_empty() {
        return None;
    }
    let key_hex = compute_pso_key(&root_sig_blob, &cs, desc.NodeMask);
    Some(RecordedPsoRecord {
        key_hex,
        kind: RecordedPsoKind::Compute,
        root_sig_blob,
        vs: Vec::new(),
        ps: Vec::new(),
        ds: Vec::new(),
        hs: Vec::new(),
        gs: Vec::new(),
        cs,
        graphics_state: None,
        node_mask: desc.NodeMask,
    })
}

pub unsafe fn record_from_graphics_desc(
    desc: &D3D12_GRAPHICS_PIPELINE_STATE_DESC,
    root_sig_blob: Vec<u8>,
) -> Option<RecordedPsoRecord> {
    let vs = unsafe { read_bytecode(&desc.VS) };
    let ps = unsafe { read_bytecode(&desc.PS) };
    let ds = unsafe { read_bytecode(&desc.DS) };
    let hs = unsafe { read_bytecode(&desc.HS) };
    let gs = unsafe { read_bytecode(&desc.GS) };
    if vs.is_empty() && ps.is_empty() {
        return None;
    }
    let mut rtv_formats = [0u32; 8];
    for (idx, fmt) in desc.RTVFormats.iter().enumerate() {
        rtv_formats[idx] = fmt.0 as u32;
    }
    let state = RecordedGraphicsState {
        blend_state_bytes: canonical_blend_bytes(&desc.BlendState),
        sample_mask: desc.SampleMask,
        rasterizer_state_bytes: canonical_rasterizer_bytes(&desc.RasterizerState),
        depth_stencil_state_bytes: canonical_depth_stencil_bytes(&desc.DepthStencilState),
        ib_strip_cut_value: desc.IBStripCutValue.0 as u32,
        primitive_topology_type: desc.PrimitiveTopologyType.0 as u32,
        num_render_targets: desc.NumRenderTargets,
        rtv_formats,
        dsv_format: desc.DSVFormat.0 as u32,
        sample_count: desc.SampleDesc.Count,
        sample_quality: desc.SampleDesc.Quality,
        input_elements: unsafe { read_input_layout(&desc.InputLayout) },
    };
    let key_hex = graphics_pso_key(
        &root_sig_blob,
        &vs,
        &ps,
        &ds,
        &hs,
        &gs,
        &state,
        desc.NodeMask,
    );
    Some(RecordedPsoRecord {
        key_hex,
        kind: RecordedPsoKind::Graphics,
        root_sig_blob,
        vs,
        ps,
        ds,
        hs,
        gs,
        cs: Vec::new(),
        graphics_state: Some(state),
        node_mask: desc.NodeMask,
    })
}

unsafe fn read_subobject<T: Copy>(base: *const u8, total: usize, offset: &mut usize) -> Option<T> {
    let type_end = offset.checked_add(size_of::<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE>())?;
    let payload_start = align_up(type_end, align_of::<T>());
    let payload_end = payload_start.checked_add(size_of::<T>())?;
    if payload_end > total {
        return None;
    }
    let val = unsafe { std::ptr::read_unaligned(base.add(payload_start).cast::<T>()) };
    *offset = align_up(payload_end, align_of::<usize>());
    Some(val)
}

pub struct ParsedStream {
    pub record: RecordedPsoRecord,
    pub root_sig_ptr: *mut c_void,
    pub flags: D3D12_PIPELINE_STATE_FLAGS,
}

pub unsafe fn record_from_stream_desc<F>(
    stream_desc: &D3D12_PIPELINE_STATE_STREAM_DESC,
    lookup_root_sig: F,
) -> Option<ParsedStream>
where
    F: Fn(*mut c_void) -> Option<Vec<u8>>,
{
    if stream_desc.pPipelineStateSubobjectStream.is_null() || stream_desc.SizeInBytes == 0 {
        return None;
    }
    let base = stream_desc.pPipelineStateSubobjectStream.cast::<u8>();
    let total = stream_desc.SizeInBytes;
    let mut offset = 0usize;

    let mut root_sig_ptr: *mut c_void = std::ptr::null_mut();
    let mut vs = Vec::new();
    let mut ps = Vec::new();
    let mut ds = Vec::new();
    let mut hs = Vec::new();
    let mut gs = Vec::new();
    let mut cs = Vec::new();
    let mut node_mask = 0u32;
    let mut pso_flags = D3D12_PIPELINE_STATE_FLAGS(0);

    let mut blend_state_bytes = canonical_blend_bytes(&D3D12_BLEND_DESC::default());
    let mut sample_mask = u32::MAX;
    let mut rasterizer_state_bytes = canonical_rasterizer_bytes(&D3D12_RASTERIZER_DESC::default());
    let mut depth_stencil_state_bytes =
        canonical_depth_stencil_bytes(&D3D12_DEPTH_STENCIL_DESC::default());
    let mut input_elements = Vec::new();
    let mut ib_strip_cut_value = 0u32;
    let mut primitive_topology_type = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE.0 as u32;
    let mut num_render_targets = 0u32;
    let mut rtv_formats = [0u32; 8];
    let mut dsv_format = 0u32;
    let mut sample_count = 1u32;
    let mut sample_quality = 0u32;

    while offset + size_of::<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE>() <= total {
        let sub_type = unsafe {
            std::ptr::read_unaligned(
                base.add(offset)
                    .cast::<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE>(),
            )
        };
        match sub_type {
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE => {
                root_sig_ptr = unsafe { read_subobject::<*mut c_void>(base, total, &mut offset) }?;
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS => {
                let bc =
                    unsafe { read_subobject::<D3D12_SHADER_BYTECODE>(base, total, &mut offset) }?;
                vs = unsafe { read_bytecode(&bc) };
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS => {
                let bc =
                    unsafe { read_subobject::<D3D12_SHADER_BYTECODE>(base, total, &mut offset) }?;
                ps = unsafe { read_bytecode(&bc) };
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DS => {
                let bc =
                    unsafe { read_subobject::<D3D12_SHADER_BYTECODE>(base, total, &mut offset) }?;
                ds = unsafe { read_bytecode(&bc) };
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_HS => {
                let bc =
                    unsafe { read_subobject::<D3D12_SHADER_BYTECODE>(base, total, &mut offset) }?;
                hs = unsafe { read_bytecode(&bc) };
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_GS => {
                let bc =
                    unsafe { read_subobject::<D3D12_SHADER_BYTECODE>(base, total, &mut offset) }?;
                gs = unsafe { read_bytecode(&bc) };
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS => {
                let bc =
                    unsafe { read_subobject::<D3D12_SHADER_BYTECODE>(base, total, &mut offset) }?;
                cs = unsafe { read_bytecode(&bc) };
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT => {
                let _so = unsafe {
                    read_subobject::<D3D12_STREAM_OUTPUT_DESC>(base, total, &mut offset)
                }?;
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_BLEND => {
                let val = unsafe { read_subobject::<D3D12_BLEND_DESC>(base, total, &mut offset) }?;
                blend_state_bytes = canonical_blend_bytes(&val);
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_MASK => {
                sample_mask = unsafe { read_subobject::<u32>(base, total, &mut offset) }?;
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER => {
                let val =
                    unsafe { read_subobject::<D3D12_RASTERIZER_DESC>(base, total, &mut offset) }?;
                rasterizer_state_bytes = canonical_rasterizer_bytes(&val);
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL => {
                let val = unsafe {
                    read_subobject::<D3D12_DEPTH_STENCIL_DESC>(base, total, &mut offset)
                }?;
                depth_stencil_state_bytes = canonical_depth_stencil_bytes(&val);
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL1 => {
                let val = unsafe {
                    read_subobject::<D3D12_DEPTH_STENCIL_DESC1>(base, total, &mut offset)
                }?;
                let base_ds = D3D12_DEPTH_STENCIL_DESC {
                    DepthEnable: val.DepthEnable,
                    DepthWriteMask: val.DepthWriteMask,
                    DepthFunc: val.DepthFunc,
                    StencilEnable: val.StencilEnable,
                    StencilReadMask: val.StencilReadMask,
                    StencilWriteMask: val.StencilWriteMask,
                    FrontFace: val.FrontFace,
                    BackFace: val.BackFace,
                };
                depth_stencil_state_bytes = canonical_depth_stencil_bytes(&base_ds);
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT => {
                let val =
                    unsafe { read_subobject::<D3D12_INPUT_LAYOUT_DESC>(base, total, &mut offset) }?;
                input_elements = unsafe { read_input_layout(&val) };
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_IB_STRIP_CUT_VALUE => {
                let val = unsafe {
                    read_subobject::<D3D12_INDEX_BUFFER_STRIP_CUT_VALUE>(base, total, &mut offset)
                }?;
                ib_strip_cut_value = val.0 as u32;
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PRIMITIVE_TOPOLOGY => {
                let val = unsafe {
                    read_subobject::<D3D12_PRIMITIVE_TOPOLOGY_TYPE>(base, total, &mut offset)
                }?;
                primitive_topology_type = val.0 as u32;
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS => {
                let val =
                    unsafe { read_subobject::<D3D12_RT_FORMAT_ARRAY>(base, total, &mut offset) }?;
                num_render_targets = val.NumRenderTargets;
                for (idx, fmt) in val.RTFormats.iter().enumerate() {
                    rtv_formats[idx] = fmt.0 as u32;
                }
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL_FORMAT => {
                let val = unsafe { read_subobject::<DXGI_FORMAT>(base, total, &mut offset) }?;
                dsv_format = val.0 as u32;
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_DESC => {
                let val = unsafe { read_subobject::<DXGI_SAMPLE_DESC>(base, total, &mut offset) }?;
                sample_count = val.Count;
                sample_quality = val.Quality;
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_NODE_MASK => {
                node_mask = unsafe { read_subobject::<u32>(base, total, &mut offset) }?;
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CACHED_PSO => {
                let _cached = unsafe {
                    read_subobject::<D3D12_CACHED_PIPELINE_STATE>(base, total, &mut offset)
                }?;
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_FLAGS => {
                pso_flags = unsafe {
                    read_subobject::<D3D12_PIPELINE_STATE_FLAGS>(base, total, &mut offset)
                }?;
            }
            D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VIEW_INSTANCING => {
                let _vi = unsafe {
                    read_subobject::<D3D12_VIEW_INSTANCING_DESC>(base, total, &mut offset)
                }?;
            }
            _ => return None,
        }
    }

    let root_sig_blob = if root_sig_ptr.is_null() {
        Vec::new()
    } else {
        lookup_root_sig(root_sig_ptr).unwrap_or_default()
    };

    if !cs.is_empty() {
        let key_hex = compute_pso_key(&root_sig_blob, &cs, node_mask);
        return Some(ParsedStream {
            record: RecordedPsoRecord {
                key_hex,
                kind: RecordedPsoKind::Compute,
                root_sig_blob,
                vs: Vec::new(),
                ps: Vec::new(),
                ds: Vec::new(),
                hs: Vec::new(),
                gs: Vec::new(),
                cs,
                graphics_state: None,
                node_mask,
            },
            root_sig_ptr,
            flags: pso_flags,
        });
    }

    if !vs.is_empty() || !ps.is_empty() {
        let state = RecordedGraphicsState {
            blend_state_bytes,
            sample_mask,
            rasterizer_state_bytes,
            depth_stencil_state_bytes,
            ib_strip_cut_value,
            primitive_topology_type,
            num_render_targets,
            rtv_formats,
            dsv_format,
            sample_count,
            sample_quality,
            input_elements,
        };
        let key_hex = graphics_pso_key(&root_sig_blob, &vs, &ps, &ds, &hs, &gs, &state, node_mask);
        return Some(ParsedStream {
            record: RecordedPsoRecord {
                key_hex,
                kind: RecordedPsoKind::Graphics,
                root_sig_blob,
                vs,
                ps,
                ds,
                hs,
                gs,
                cs: Vec::new(),
                graphics_state: Some(state),
                node_mask,
            },
            root_sig_ptr,
            flags: pso_flags,
        });
    }

    None
}
