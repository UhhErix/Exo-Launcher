use crate::hud_mask::HudMaskConfig;
use crate::shaders::{
    DOWNSAMPLE_DXIL, FLOW_DP4A_DXIL, FLOW_FP16_DXIL, FLOW_SMOOTH_DXIL, HUD_MASK_DXIL,
    LUMA_PACK_DXIL, SYNTH_DXIL, SYNTH_SCENE_DXIL,
};
use crate::synth::{
    FlowSearchParams, FlowSmoothParams, PyramidParams, ScalerMode, SynthParams, SynthSceneParams,
};
use crate::telemetry::{round2, round3};
use bytemuck::cast_slice;
use serde::Serialize;
use std::mem::{ManuallyDrop, size_of};
use windows::Win32::Foundation::{CloseHandle, HANDLE};
use windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL_12_0;
use windows::Win32::Graphics::Direct3D12::{
    D3D_ROOT_SIGNATURE_VERSION_1_0, D3D_SHADER_MODEL_6_4, D3D12_COMMAND_LIST_TYPE_DIRECT,
    D3D12_COMMAND_QUEUE_DESC, D3D12_COMMAND_QUEUE_FLAG_NONE, D3D12_COMPARISON_FUNC_NEVER,
    D3D12_COMPUTE_PIPELINE_STATE_DESC, D3D12_CPU_DESCRIPTOR_HANDLE,
    D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING, D3D12_DESCRIPTOR_HEAP_DESC,
    D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE, D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
    D3D12_DESCRIPTOR_RANGE, D3D12_DESCRIPTOR_RANGE_TYPE_SRV, D3D12_DESCRIPTOR_RANGE_TYPE_UAV,
    D3D12_FEATURE_D3D12_OPTIONS4, D3D12_FEATURE_DATA_D3D12_OPTIONS4,
    D3D12_FEATURE_DATA_SHADER_MODEL, D3D12_FEATURE_SHADER_MODEL, D3D12_FENCE_FLAG_NONE,
    D3D12_FILTER_MIN_MAG_MIP_LINEAR, D3D12_FILTER_MIN_MAG_MIP_POINT, D3D12_GPU_DESCRIPTOR_HANDLE,
    D3D12_HEAP_FLAG_NONE, D3D12_HEAP_PROPERTIES, D3D12_HEAP_TYPE_DEFAULT, D3D12_HEAP_TYPE_READBACK,
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT, D3D12_QUERY_HEAP_DESC, D3D12_QUERY_HEAP_TYPE_TIMESTAMP,
    D3D12_QUERY_TYPE_TIMESTAMP, D3D12_RESOURCE_BARRIER, D3D12_RESOURCE_BARRIER_0,
    D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, D3D12_RESOURCE_BARRIER_FLAG_NONE,
    D3D12_RESOURCE_BARRIER_TYPE_TRANSITION, D3D12_RESOURCE_BARRIER_TYPE_UAV, D3D12_RESOURCE_DESC,
    D3D12_RESOURCE_DIMENSION_BUFFER, D3D12_RESOURCE_DIMENSION_TEXTURE2D,
    D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_FLAG_NONE,
    D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COPY_SOURCE,
    D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
    D3D12_RESOURCE_STATES, D3D12_RESOURCE_TRANSITION_BARRIER, D3D12_RESOURCE_UAV_BARRIER,
    D3D12_ROOT_CONSTANTS, D3D12_ROOT_DESCRIPTOR_TABLE, D3D12_ROOT_PARAMETER,
    D3D12_ROOT_PARAMETER_0, D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS,
    D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE, D3D12_ROOT_SIGNATURE_DESC,
    D3D12_ROOT_SIGNATURE_FLAG_NONE, D3D12_SHADER_BYTECODE, D3D12_SHADER_RESOURCE_VIEW_DESC,
    D3D12_SHADER_RESOURCE_VIEW_DESC_0, D3D12_SHADER_VISIBILITY_ALL, D3D12_SRV_DIMENSION_TEXTURE2D,
    D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK, D3D12_STATIC_SAMPLER_DESC, D3D12_SUBRESOURCE_FOOTPRINT,
    D3D12_TEX2D_SRV, D3D12_TEX2D_UAV, D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
    D3D12_TEXTURE_COPY_LOCATION, D3D12_TEXTURE_COPY_LOCATION_0,
    D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
    D3D12_TEXTURE_LAYOUT_ROW_MAJOR, D3D12_TEXTURE_LAYOUT_UNKNOWN, D3D12_UAV_DIMENSION_TEXTURE2D,
    D3D12_UNORDERED_ACCESS_VIEW_DESC, D3D12_UNORDERED_ACCESS_VIEW_DESC_0, D3D12CreateDevice,
    D3D12SerializeRootSignature, ID3D12CommandAllocator, ID3D12CommandList, ID3D12CommandQueue,
    ID3D12DescriptorHeap, ID3D12Device, ID3D12Fence, ID3D12GraphicsCommandList,
    ID3D12PipelineState, ID3D12QueryHeap, ID3D12Resource, ID3D12RootSignature,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT, DXGI_FORMAT_R8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R16_FLOAT,
    DXGI_FORMAT_R16G16_FLOAT, DXGI_FORMAT_R32_UINT, DXGI_FORMAT_UNKNOWN, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory2, DXGI_CREATE_FACTORY_FLAGS, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
    DXGI_MEMORY_SEGMENT_GROUP_LOCAL, DXGI_QUERY_VIDEO_MEMORY_INFO, IDXGIAdapter1, IDXGIAdapter3,
    IDXGIFactory6,
};
use windows::Win32::System::Threading::{CreateEventW, INFINITE, WaitForSingleObject};
use windows::core::Interface;

const NUM_LEVELS: usize = 4;
const DESC_TABLE_STRIDE: u32 = 16;
const MAX_TABLES: u32 = 64;

#[derive(Clone, Debug, Default, Serialize)]
pub struct GpuCapabilities {
    pub adapter_name: String,
    pub vendor_id: u32,
    pub dedicated_vram_mb: f64,
    pub native_fp16: bool,
    pub shader_model_6_4_dp4a: bool,
}

#[derive(Clone, Copy, Debug, Default, Serialize)]
pub struct GpuPassTimings {
    pub pyramid_ms: f64,
    pub flow_ms: f64,
    pub smooth_ms: f64,
    pub hud_mask_ms: f64,
    pub synth_ms: f64,
    pub total_gpu_ms: f64,
}

struct Tex2D {
    res: ID3D12Resource,
    state: D3D12_RESOURCE_STATES,
    format: DXGI_FORMAT,
    width: u32,
    height: u32,
}

pub struct Dx12FlowEngine {
    pub factory: IDXGIFactory6,
    pub adapter: IDXGIAdapter3,
    pub device: ID3D12Device,
    pub queue: ID3D12CommandQueue,
    pub caps: GpuCapabilities,
    allocator: ID3D12CommandAllocator,
    cmd_list: ID3D12GraphicsCommandList,
    fence: ID3D12Fence,
    fence_val: u64,
    fence_event: HANDLE,
    root_sig: ID3D12RootSignature,
    pso_luma_pack: ID3D12PipelineState,
    pso_downsample: ID3D12PipelineState,
    pso_flow_dp4a: ID3D12PipelineState,
    pso_flow_fp16: ID3D12PipelineState,
    pso_flow_smooth: ID3D12PipelineState,
    pso_hud_mask: ID3D12PipelineState,
    pso_synth: ID3D12PipelineState,
    pso_synth_scene: ID3D12PipelineState,
    heap: ID3D12DescriptorHeap,
    desc_size: u32,
    query_heap: ID3D12QueryHeap,
    query_readback: ID3D12Resource,
    timestamp_freq: u64,
    pub width: u32,
    pub height: u32,
    prev_rgba: Tex2D,
    curr_rgba: Tex2D,
    gt_rgba: Tex2D,
    out_rgba: Tex2D,
    prev_luma: [Tex2D; NUM_LEVELS],
    curr_luma: [Tex2D; NUM_LEVELS],
    prev_packed: [Tex2D; NUM_LEVELS],
    curr_packed: [Tex2D; NUM_LEVELS],
    mv_fwd: [Tex2D; NUM_LEVELS],
    mv_bwd: [Tex2D; NUM_LEVELS],
    mv_smooth_prev: Tex2D,
    mv_smooth_curr: Tex2D,
    occ_tex: Tex2D,
    hud_state_a: Tex2D,
    hud_state_b: Tex2D,
    hud_mask_raw: Tex2D,
    hud_mask_dilated: Tex2D,
    hud_ping: bool,
    has_prev_mv: bool,
    readback_buf: ID3D12Resource,
    readback_row_pitch: u32,
}

impl Dx12FlowEngine {
    pub fn new(width: u32, height: u32) -> Result<Self, String> {
        unsafe {
            let factory: IDXGIFactory6 = CreateDXGIFactory2(DXGI_CREATE_FACTORY_FLAGS(0))
                .map_err(|e| format!("CreateDXGIFactory2: {e}"))?;
            let adapter1: IDXGIAdapter1 = factory
                .EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE)
                .map_err(|e| format!("EnumAdapterByGpuPreference: {e}"))?;
            let adapter: IDXGIAdapter3 = adapter1
                .cast()
                .map_err(|e| format!("cast IDXGIAdapter3: {e}"))?;
            let desc = adapter1.GetDesc1().map_err(|e| format!("GetDesc1: {e}"))?;
            let name_len = desc
                .Description
                .iter()
                .position(|&c| c == 0)
                .unwrap_or(desc.Description.len());
            let adapter_name = String::from_utf16_lossy(&desc.Description[..name_len]);

            let mut device_opt: Option<ID3D12Device> = None;
            D3D12CreateDevice(&adapter1, D3D_FEATURE_LEVEL_12_0, &mut device_opt)
                .map_err(|e| format!("D3D12CreateDevice: {e}"))?;
            let device = device_opt.ok_or("D3D12CreateDevice returned null")?;

            let mut opts4 = D3D12_FEATURE_DATA_D3D12_OPTIONS4::default();
            let _ = device.CheckFeatureSupport(
                D3D12_FEATURE_D3D12_OPTIONS4,
                (&mut opts4 as *mut D3D12_FEATURE_DATA_D3D12_OPTIONS4).cast(),
                size_of::<D3D12_FEATURE_DATA_D3D12_OPTIONS4>() as u32,
            );
            let mut sm = D3D12_FEATURE_DATA_SHADER_MODEL {
                HighestShaderModel: D3D_SHADER_MODEL_6_4,
            };
            let sm_ok = device
                .CheckFeatureSupport(
                    D3D12_FEATURE_SHADER_MODEL,
                    (&mut sm as *mut D3D12_FEATURE_DATA_SHADER_MODEL).cast(),
                    size_of::<D3D12_FEATURE_DATA_SHADER_MODEL>() as u32,
                )
                .is_ok();
            let caps = GpuCapabilities {
                adapter_name,
                vendor_id: desc.VendorId,
                dedicated_vram_mb: round2(desc.DedicatedVideoMemory as f64 / (1024.0 * 1024.0)),
                native_fp16: opts4.Native16BitShaderOpsSupported.as_bool(),
                shader_model_6_4_dp4a: sm_ok && sm.HighestShaderModel.0 >= D3D_SHADER_MODEL_6_4.0,
            };

            let queue: ID3D12CommandQueue = device
                .CreateCommandQueue(&D3D12_COMMAND_QUEUE_DESC {
                    Type: D3D12_COMMAND_LIST_TYPE_DIRECT,
                    Priority: 0,
                    Flags: D3D12_COMMAND_QUEUE_FLAG_NONE,
                    NodeMask: 0,
                })
                .map_err(|e| format!("CreateCommandQueue: {e}"))?;
            let timestamp_freq = queue
                .GetTimestampFrequency()
                .map_err(|e| format!("GetTimestampFrequency: {e}"))?;

            let allocator: ID3D12CommandAllocator = device
                .CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT)
                .map_err(|e| format!("CreateCommandAllocator: {e}"))?;
            let cmd_list: ID3D12GraphicsCommandList = device
                .CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocator, None)
                .map_err(|e| format!("CreateCommandList: {e}"))?;
            cmd_list.Close().map_err(|e| format!("Close: {e}"))?;

            let fence: ID3D12Fence = device
                .CreateFence(0, D3D12_FENCE_FLAG_NONE)
                .map_err(|e| format!("CreateFence: {e}"))?;
            let fence_event =
                CreateEventW(None, false, false, None).map_err(|e| format!("CreateEventW: {e}"))?;

            let root_sig = create_root_signature(&device)?;
            let pso_luma_pack = create_compute_pso(&device, &root_sig, LUMA_PACK_DXIL)?;
            let pso_downsample = create_compute_pso(&device, &root_sig, DOWNSAMPLE_DXIL)?;
            let pso_flow_dp4a = create_compute_pso(&device, &root_sig, FLOW_DP4A_DXIL)?;
            let pso_flow_fp16 = create_compute_pso(&device, &root_sig, FLOW_FP16_DXIL)?;
            let pso_flow_smooth = create_compute_pso(&device, &root_sig, FLOW_SMOOTH_DXIL)?;
            let pso_hud_mask = create_compute_pso(&device, &root_sig, HUD_MASK_DXIL)?;
            let pso_synth = create_compute_pso(&device, &root_sig, SYNTH_DXIL)?;
            let pso_synth_scene = create_compute_pso(&device, &root_sig, SYNTH_SCENE_DXIL)?;

            let heap: ID3D12DescriptorHeap = device
                .CreateDescriptorHeap(&D3D12_DESCRIPTOR_HEAP_DESC {
                    Type: D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
                    NumDescriptors: DESC_TABLE_STRIDE * MAX_TABLES,
                    Flags: D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
                    NodeMask: 0,
                })
                .map_err(|e| format!("CreateDescriptorHeap: {e}"))?;
            let desc_size =
                device.GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

            let mut qh_opt: Option<ID3D12QueryHeap> = None;
            device
                .CreateQueryHeap(
                    &D3D12_QUERY_HEAP_DESC {
                        Type: D3D12_QUERY_HEAP_TYPE_TIMESTAMP,
                        Count: 16,
                        NodeMask: 0,
                    },
                    &mut qh_opt,
                )
                .map_err(|e| format!("CreateQueryHeap: {e}"))?;
            let query_heap = qh_opt.ok_or("CreateQueryHeap null")?;
            let query_readback = create_readback_buffer(&device, 16 * 8)?;

            let prev_rgba = create_tex2d(&device, width, height, DXGI_FORMAT_R8G8B8A8_UNORM)?;
            let curr_rgba = create_tex2d(&device, width, height, DXGI_FORMAT_R8G8B8A8_UNORM)?;
            let gt_rgba = create_tex2d(&device, width, height, DXGI_FORMAT_R8G8B8A8_UNORM)?;
            let out_rgba = create_tex2d(&device, width, height, DXGI_FORMAT_R8G8B8A8_UNORM)?;

            let flow_w = (width / 2).max(16);
            let flow_h = (height / 2).max(16);
            let mut dims = [(0u32, 0u32); NUM_LEVELS];
            for (i, d) in dims.iter_mut().enumerate() {
                let shift = i as u32;
                *d = ((flow_w >> shift).max(8), (flow_h >> shift).max(8));
            }

            let prev_luma = [
                create_tex2d(&device, dims[0].0, dims[0].1, DXGI_FORMAT_R16_FLOAT)?,
                create_tex2d(&device, dims[1].0, dims[1].1, DXGI_FORMAT_R16_FLOAT)?,
                create_tex2d(&device, dims[2].0, dims[2].1, DXGI_FORMAT_R16_FLOAT)?,
                create_tex2d(&device, dims[3].0, dims[3].1, DXGI_FORMAT_R16_FLOAT)?,
            ];
            let curr_luma = [
                create_tex2d(&device, dims[0].0, dims[0].1, DXGI_FORMAT_R16_FLOAT)?,
                create_tex2d(&device, dims[1].0, dims[1].1, DXGI_FORMAT_R16_FLOAT)?,
                create_tex2d(&device, dims[2].0, dims[2].1, DXGI_FORMAT_R16_FLOAT)?,
                create_tex2d(&device, dims[3].0, dims[3].1, DXGI_FORMAT_R16_FLOAT)?,
            ];
            let prev_packed = [
                create_tex2d(&device, dims[0].0, dims[0].1, DXGI_FORMAT_R32_UINT)?,
                create_tex2d(&device, dims[1].0, dims[1].1, DXGI_FORMAT_R32_UINT)?,
                create_tex2d(&device, dims[2].0, dims[2].1, DXGI_FORMAT_R32_UINT)?,
                create_tex2d(&device, dims[3].0, dims[3].1, DXGI_FORMAT_R32_UINT)?,
            ];
            let curr_packed = [
                create_tex2d(&device, dims[0].0, dims[0].1, DXGI_FORMAT_R32_UINT)?,
                create_tex2d(&device, dims[1].0, dims[1].1, DXGI_FORMAT_R32_UINT)?,
                create_tex2d(&device, dims[2].0, dims[2].1, DXGI_FORMAT_R32_UINT)?,
                create_tex2d(&device, dims[3].0, dims[3].1, DXGI_FORMAT_R32_UINT)?,
            ];
            let mv_fwd = [
                create_tex2d(&device, dims[0].0, dims[0].1, DXGI_FORMAT_R16G16_FLOAT)?,
                create_tex2d(&device, dims[1].0, dims[1].1, DXGI_FORMAT_R16G16_FLOAT)?,
                create_tex2d(&device, dims[2].0, dims[2].1, DXGI_FORMAT_R16G16_FLOAT)?,
                create_tex2d(&device, dims[3].0, dims[3].1, DXGI_FORMAT_R16G16_FLOAT)?,
            ];
            let mv_bwd = [
                create_tex2d(&device, dims[0].0, dims[0].1, DXGI_FORMAT_R16G16_FLOAT)?,
                create_tex2d(&device, dims[1].0, dims[1].1, DXGI_FORMAT_R16G16_FLOAT)?,
                create_tex2d(&device, dims[2].0, dims[2].1, DXGI_FORMAT_R16G16_FLOAT)?,
                create_tex2d(&device, dims[3].0, dims[3].1, DXGI_FORMAT_R16G16_FLOAT)?,
            ];

            let mv_smooth_prev =
                create_tex2d(&device, dims[0].0, dims[0].1, DXGI_FORMAT_R16G16_FLOAT)?;
            let mv_smooth_curr =
                create_tex2d(&device, dims[0].0, dims[0].1, DXGI_FORMAT_R16G16_FLOAT)?;
            let occ_tex = create_tex2d(&device, dims[0].0, dims[0].1, DXGI_FORMAT_R8_UNORM)?;
            let hud_state_a = create_tex2d(&device, width, height, DXGI_FORMAT_R16G16_FLOAT)?;
            let hud_state_b = create_tex2d(&device, width, height, DXGI_FORMAT_R16G16_FLOAT)?;
            let hud_mask_raw = create_tex2d(&device, width, height, DXGI_FORMAT_R8_UNORM)?;
            let hud_mask_dilated = create_tex2d(&device, width, height, DXGI_FORMAT_R8_UNORM)?;

            let row_pitch = (width * 4).div_ceil(256) * 256;
            let readback_buf =
                create_readback_buffer(&device, (row_pitch as u64) * (height as u64))?;

            Ok(Self {
                factory,
                adapter,
                device,
                queue,
                caps,
                allocator,
                cmd_list,
                fence,
                fence_val: 0,
                fence_event,
                root_sig,
                pso_luma_pack,
                pso_downsample,
                pso_flow_dp4a,
                pso_flow_fp16,
                pso_flow_smooth,
                pso_hud_mask,
                pso_synth,
                pso_synth_scene,
                heap,
                desc_size,
                query_heap,
                query_readback,
                timestamp_freq,
                width,
                height,
                prev_rgba,
                curr_rgba,
                gt_rgba,
                out_rgba,
                prev_luma,
                curr_luma,
                prev_packed,
                curr_packed,
                mv_fwd,
                mv_bwd,
                mv_smooth_prev,
                mv_smooth_curr,
                occ_tex,
                hud_state_a,
                hud_state_b,
                hud_mask_raw,
                hud_mask_dilated,
                hud_ping: false,
                has_prev_mv: false,
                readback_buf,
                readback_row_pitch: row_pitch,
            })
        }
    }

    pub fn query_vram_mb(&self) -> f64 {
        unsafe {
            let mut info = DXGI_QUERY_VIDEO_MEMORY_INFO::default();
            if self
                .adapter
                .QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &mut info)
                .is_ok()
            {
                return round2(info.CurrentUsage as f64 / (1024.0 * 1024.0));
            }
        }
        0.0
    }

    pub fn out_resource(&self) -> &ID3D12Resource {
        &self.out_rgba.res
    }

    pub fn reset_temporal_state(&mut self) {
        self.has_prev_mv = false;
        self.hud_ping = false;
    }

    pub fn ingest_external_frame(&mut self, src: &ID3D12Resource) -> Result<(), String> {
        unsafe {
            self.begin_cmd()?;
            let prev = &mut self.prev_rgba;
            let curr = &mut self.curr_rgba;
            transition(&self.cmd_list, prev, D3D12_RESOURCE_STATE_COPY_DEST);
            transition(&self.cmd_list, curr, D3D12_RESOURCE_STATE_COPY_SOURCE);
            self.cmd_list.CopyResource(&prev.res, &curr.res);
            transition(&self.cmd_list, curr, D3D12_RESOURCE_STATE_COPY_DEST);
            self.cmd_list.CopyResource(&curr.res, src);
            self.end_cmd_and_wait()?;
        }
        Ok(())
    }

    pub fn render_synthetic_triplet(&mut self, t0: f32, t_mid: f32, t1: f32) -> Result<(), String> {
        unsafe {
            self.begin_cmd()?;
            self.cmd_list.SetComputeRootSignature(&self.root_sig);
            let heaps = [Some(self.heap.clone())];
            self.cmd_list.SetDescriptorHeaps(&heaps);
            self.cmd_list.SetPipelineState(&self.pso_synth_scene);

            let (w, h) = (self.width, self.height);
            let gx = w.div_ceil(8);
            let gy = h.div_ceil(8);

            let targets = [
                (0u32, t0, &mut self.prev_rgba as *mut Tex2D),
                (1u32, t_mid, &mut self.gt_rgba as *mut Tex2D),
                (2u32, t1, &mut self.curr_rgba as *mut Tex2D),
            ];
            for (slot, time_sec, tex_ptr) in targets {
                let tex = &mut *tex_ptr;
                transition(&self.cmd_list, tex, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
                let (srv_gpu, uav_gpu) = self.write_table(slot, &[], &[tex]);
                let params = SynthSceneParams {
                    width: w,
                    height: h,
                    time_sec,
                    pad0: 0,
                };
                set_root_consts(&self.cmd_list, &params);
                self.cmd_list.SetComputeRootDescriptorTable(1, srv_gpu);
                self.cmd_list.SetComputeRootDescriptorTable(2, uav_gpu);
                self.cmd_list.Dispatch(gx, gy, 1);
                uav_barrier(&self.cmd_list, &tex.res);
            }
            self.end_cmd_and_wait()?;
        }
        Ok(())
    }

    pub fn run_flow_and_synth(
        &mut self,
        use_dp4a: bool,
        alpha_t: f32,
        hud_mask_enabled: bool,
        hud_cfg: HudMaskConfig,
        scaler: ScalerMode,
        sharpness: f32,
    ) -> Result<GpuPassTimings, String> {
        unsafe {
            self.begin_cmd()?;
            self.cmd_list.SetComputeRootSignature(&self.root_sig);
            let heaps = [Some(self.heap.clone())];
            self.cmd_list.SetDescriptorHeaps(&heaps);

            let mut tbl = 0u32;
            self.cmd_list
                .EndQuery(&self.query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 0);

            transition(
                &self.cmd_list,
                &mut self.prev_rgba,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
            );
            transition(
                &self.cmd_list,
                &mut self.curr_rgba,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
            );

            self.cmd_list.SetPipelineState(&self.pso_luma_pack);
            for side in 0..2 {
                let src_rgba = if side == 0 {
                    &self.prev_rgba
                } else {
                    &self.curr_rgba
                };
                let dst_l = if side == 0 {
                    &mut self.prev_luma[0]
                } else {
                    &mut self.curr_luma[0]
                };
                let dst_p = if side == 0 {
                    &mut self.prev_packed[0]
                } else {
                    &mut self.curr_packed[0]
                };
                transition(&self.cmd_list, dst_l, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
                transition(&self.cmd_list, dst_p, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
                let (srv_g, uav_g) = write_desc_table(
                    &self.device,
                    &self.heap,
                    self.desc_size,
                    tbl,
                    &[src_rgba, src_rgba],
                    &[dst_l, dst_p],
                );
                tbl += 1;
                let params = PyramidParams {
                    dst_w: dst_l.width,
                    dst_h: dst_l.height,
                    src_w: src_rgba.width,
                    src_h: src_rgba.height,
                    ..Default::default()
                };
                set_root_consts(&self.cmd_list, &params);
                self.cmd_list.SetComputeRootDescriptorTable(1, srv_g);
                self.cmd_list.SetComputeRootDescriptorTable(2, uav_g);
                self.cmd_list
                    .Dispatch(dst_l.width.div_ceil(8), dst_l.height.div_ceil(8), 1);
                uav_barrier(&self.cmd_list, &dst_l.res);
                uav_barrier(&self.cmd_list, &dst_p.res);
            }

            self.cmd_list.SetPipelineState(&self.pso_downsample);
            for lvl in 1..NUM_LEVELS {
                for side in 0..2 {
                    let (src_l, dst_l, dst_p) = if side == 0 {
                        let (lo, hi) = self.prev_luma.split_at_mut(lvl);
                        (&mut lo[lvl - 1], &mut hi[0], &mut self.prev_packed[lvl])
                    } else {
                        let (lo, hi) = self.curr_luma.split_at_mut(lvl);
                        (&mut lo[lvl - 1], &mut hi[0], &mut self.curr_packed[lvl])
                    };
                    transition(
                        &self.cmd_list,
                        src_l,
                        D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                    );
                    transition(&self.cmd_list, dst_l, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
                    transition(&self.cmd_list, dst_p, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
                    let (srv_g, uav_g) = write_desc_table(
                        &self.device,
                        &self.heap,
                        self.desc_size,
                        tbl,
                        &[src_l, src_l],
                        &[dst_l, dst_p],
                    );
                    tbl += 1;
                    let params = PyramidParams {
                        dst_w: dst_l.width,
                        dst_h: dst_l.height,
                        src_w: src_l.width,
                        src_h: src_l.height,
                        ..Default::default()
                    };
                    set_root_consts(&self.cmd_list, &params);
                    self.cmd_list.SetComputeRootDescriptorTable(1, srv_g);
                    self.cmd_list.SetComputeRootDescriptorTable(2, uav_g);
                    self.cmd_list
                        .Dispatch(dst_l.width.div_ceil(8), dst_l.height.div_ceil(8), 1);
                    uav_barrier(&self.cmd_list, &dst_l.res);
                    uav_barrier(&self.cmd_list, &dst_p.res);
                }
            }
            for lvl in 0..NUM_LEVELS {
                transition(
                    &self.cmd_list,
                    &mut self.prev_luma[lvl],
                    D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                );
                transition(
                    &self.cmd_list,
                    &mut self.curr_luma[lvl],
                    D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                );
                transition(
                    &self.cmd_list,
                    &mut self.prev_packed[lvl],
                    D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                );
                transition(
                    &self.cmd_list,
                    &mut self.curr_packed[lvl],
                    D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                );
            }

            self.cmd_list
                .EndQuery(&self.query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 1);

            self.cmd_list.SetPipelineState(if use_dp4a {
                &self.pso_flow_dp4a
            } else {
                &self.pso_flow_fp16
            });
            for dir in 0..2 {
                for lvl in (0..NUM_LEVELS).rev() {
                    let has_coarse = lvl + 1 < NUM_LEVELS;
                    let radius = if lvl >= 2 { 3 } else { 2 };
                    let (a_tex, b_tex) = if use_dp4a {
                        if dir == 0 {
                            (&self.prev_packed[lvl], &self.curr_packed[lvl])
                        } else {
                            (&self.curr_packed[lvl], &self.prev_packed[lvl])
                        }
                    } else if dir == 0 {
                        (&self.prev_luma[lvl], &self.curr_luma[lvl])
                    } else {
                        (&self.curr_luma[lvl], &self.prev_luma[lvl])
                    };

                    let mv_arr = if dir == 0 {
                        &mut self.mv_fwd
                    } else {
                        &mut self.mv_bwd
                    };
                    if has_coarse {
                        let (lo, hi) = mv_arr.split_at_mut(lvl + 1);
                        let out_tex = &mut lo[lvl];
                        let coarse_tex = &mut hi[0];
                        transition(
                            &self.cmd_list,
                            coarse_tex,
                            D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                        );
                        transition(
                            &self.cmd_list,
                            out_tex,
                            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                        );
                        let (srv_g, uav_g) = write_desc_table(
                            &self.device,
                            &self.heap,
                            self.desc_size,
                            tbl,
                            &[a_tex, b_tex, coarse_tex],
                            &[out_tex],
                        );
                        tbl += 1;
                        let params = FlowSearchParams {
                            level_w: out_tex.width,
                            level_h: out_tex.height,
                            coarse_w: coarse_tex.width,
                            coarse_h: coarse_tex.height,
                            has_coarse: 1,
                            search_radius: radius,
                            reg_weight: 2.0,
                            pad0: 0,
                        };
                        set_root_consts(&self.cmd_list, &params);
                        self.cmd_list.SetComputeRootDescriptorTable(1, srv_g);
                        self.cmd_list.SetComputeRootDescriptorTable(2, uav_g);
                        self.cmd_list.Dispatch(
                            out_tex.width.div_ceil(8),
                            out_tex.height.div_ceil(8),
                            1,
                        );
                        uav_barrier(&self.cmd_list, &out_tex.res);
                    } else {
                        let out_tex = &mut mv_arr[lvl];
                        transition(
                            &self.cmd_list,
                            out_tex,
                            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                        );
                        let (srv_g, uav_g) = write_desc_table(
                            &self.device,
                            &self.heap,
                            self.desc_size,
                            tbl,
                            &[a_tex, b_tex, &self.mv_smooth_prev],
                            &[out_tex],
                        );
                        tbl += 1;
                        let params = FlowSearchParams {
                            level_w: out_tex.width,
                            level_h: out_tex.height,
                            coarse_w: out_tex.width,
                            coarse_h: out_tex.height,
                            has_coarse: 0,
                            search_radius: radius,
                            reg_weight: 2.0,
                            pad0: 0,
                        };
                        set_root_consts(&self.cmd_list, &params);
                        self.cmd_list.SetComputeRootDescriptorTable(1, srv_g);
                        self.cmd_list.SetComputeRootDescriptorTable(2, uav_g);
                        self.cmd_list.Dispatch(
                            out_tex.width.div_ceil(8),
                            out_tex.height.div_ceil(8),
                            1,
                        );
                        uav_barrier(&self.cmd_list, &out_tex.res);
                    }
                }
            }

            self.cmd_list
                .EndQuery(&self.query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 2);

            transition(
                &self.cmd_list,
                &mut self.mv_fwd[0],
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
            );
            transition(
                &self.cmd_list,
                &mut self.mv_bwd[0],
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
            );
            transition(
                &self.cmd_list,
                &mut self.mv_smooth_prev,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
            );
            transition(
                &self.cmd_list,
                &mut self.mv_smooth_curr,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            );
            transition(
                &self.cmd_list,
                &mut self.occ_tex,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            );
            self.cmd_list.SetPipelineState(&self.pso_flow_smooth);
            let (srv_g, uav_g) = self.write_table(
                tbl,
                &[
                    &self.mv_fwd[0],
                    &self.mv_bwd[0],
                    &self.prev_luma[0],
                    &self.mv_smooth_prev,
                ],
                &[&self.mv_smooth_curr, &self.occ_tex],
            );
            tbl += 1;
            let smooth_params = FlowSmoothParams {
                width: self.mv_smooth_curr.width,
                height: self.mv_smooth_curr.height,
                edge_sigma: 0.12,
                temporal_alpha: 0.25,
                occ_threshold: 2.5,
                has_prev_mv: u32::from(self.has_prev_mv),
                pad0: 0,
                pad1: 0,
            };
            set_root_consts(&self.cmd_list, &smooth_params);
            self.cmd_list.SetComputeRootDescriptorTable(1, srv_g);
            self.cmd_list.SetComputeRootDescriptorTable(2, uav_g);
            self.cmd_list.Dispatch(
                self.mv_smooth_curr.width.div_ceil(8),
                self.mv_smooth_curr.height.div_ceil(8),
                1,
            );
            uav_barrier(&self.cmd_list, &self.mv_smooth_curr.res);
            uav_barrier(&self.cmd_list, &self.occ_tex.res);

            self.cmd_list
                .EndQuery(&self.query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 3);

            self.cmd_list.SetPipelineState(&self.pso_hud_mask);
            let (hud_prev_state, hud_next_state) = if self.hud_ping {
                (&mut self.hud_state_b, &mut self.hud_state_a)
            } else {
                (&mut self.hud_state_a, &mut self.hud_state_b)
            };
            transition(
                &self.cmd_list,
                hud_prev_state,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
            );
            transition(
                &self.cmd_list,
                hud_next_state,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            );
            transition(
                &self.cmd_list,
                &mut self.hud_mask_raw,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            );
            let (srv_g, uav_g) = write_desc_table(
                &self.device,
                &self.heap,
                self.desc_size,
                tbl,
                &[
                    &self.prev_rgba,
                    &self.curr_rgba,
                    hud_prev_state,
                    &self.hud_mask_dilated,
                ],
                &[hud_next_state, &self.hud_mask_raw],
            );
            tbl += 1;
            let hm_p0 = hud_cfg.shader_params(self.width, self.height, false);
            set_root_consts(&self.cmd_list, &hm_p0);
            self.cmd_list.SetComputeRootDescriptorTable(1, srv_g);
            self.cmd_list.SetComputeRootDescriptorTable(2, uav_g);
            self.cmd_list
                .Dispatch(self.width.div_ceil(8), self.height.div_ceil(8), 1);
            uav_barrier(&self.cmd_list, &self.hud_mask_raw.res);

            transition(
                &self.cmd_list,
                &mut self.hud_mask_raw,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
            );
            transition(
                &self.cmd_list,
                &mut self.hud_mask_dilated,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            );
            let (srv_g, uav_g) = write_desc_table(
                &self.device,
                &self.heap,
                self.desc_size,
                tbl,
                &[
                    &self.prev_rgba,
                    &self.curr_rgba,
                    hud_prev_state,
                    &self.hud_mask_raw,
                ],
                &[hud_next_state, &self.hud_mask_dilated],
            );
            tbl += 1;
            let hm_p1 = hud_cfg.shader_params(self.width, self.height, true);
            set_root_consts(&self.cmd_list, &hm_p1);
            self.cmd_list.SetComputeRootDescriptorTable(1, srv_g);
            self.cmd_list.SetComputeRootDescriptorTable(2, uav_g);
            self.cmd_list
                .Dispatch(self.width.div_ceil(8), self.height.div_ceil(8), 1);
            uav_barrier(&self.cmd_list, &self.hud_mask_dilated.res);

            self.cmd_list
                .EndQuery(&self.query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 4);

            transition(
                &self.cmd_list,
                &mut self.mv_smooth_curr,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
            );
            transition(
                &self.cmd_list,
                &mut self.occ_tex,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
            );
            transition(
                &self.cmd_list,
                &mut self.hud_mask_dilated,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
            );
            transition(
                &self.cmd_list,
                &mut self.out_rgba,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            );
            self.cmd_list.SetPipelineState(&self.pso_synth);
            let (srv_g, uav_g) = self.write_table(
                tbl,
                &[
                    &self.prev_rgba,
                    &self.curr_rgba,
                    &self.mv_smooth_curr,
                    &self.mv_bwd[0],
                    &self.occ_tex,
                    &self.hud_mask_dilated,
                ],
                &[&self.out_rgba],
            );
            let mv_scale_x = self.width as f32 / self.mv_smooth_curr.width as f32;
            let mv_scale_y = self.height as f32 / self.mv_smooth_curr.height as f32;
            let sp = SynthParams {
                dst_w: self.width,
                dst_h: self.height,
                src_w: self.width,
                src_h: self.height,
                alpha_t,
                hud_mask_enabled: u32::from(hud_mask_enabled),
                scaler_mode: scaler.shader_code(),
                sharpness,
                mv_scale_x,
                mv_scale_y,
                pad0: 0,
                pad1: 0,
            };
            set_root_consts(&self.cmd_list, &sp);
            self.cmd_list.SetComputeRootDescriptorTable(1, srv_g);
            self.cmd_list.SetComputeRootDescriptorTable(2, uav_g);
            self.cmd_list
                .Dispatch(self.width.div_ceil(8), self.height.div_ceil(8), 1);
            uav_barrier(&self.cmd_list, &self.out_rgba.res);

            self.cmd_list
                .EndQuery(&self.query_heap, D3D12_QUERY_TYPE_TIMESTAMP, 5);
            self.cmd_list.ResolveQueryData(
                &self.query_heap,
                D3D12_QUERY_TYPE_TIMESTAMP,
                0,
                6,
                &self.query_readback,
                0,
            );

            self.end_cmd_and_wait()?;

            std::mem::swap(&mut self.mv_smooth_prev, &mut self.mv_smooth_curr);
            self.has_prev_mv = true;
            self.hud_ping = !self.hud_ping;

            let mut ptr_raw = std::ptr::null_mut();
            self.query_readback
                .Map(0, None, Some(&mut ptr_raw))
                .map_err(|e| format!("Map query_readback: {e}"))?;
            let ts = std::slice::from_raw_parts(ptr_raw as *const u64, 6).to_vec();
            self.query_readback.Unmap(0, None);

            let to_ms = |a: u64, b: u64| -> f64 {
                round3(b.saturating_sub(a) as f64 * 1000.0 / self.timestamp_freq as f64)
            };
            let pyramid_ms = to_ms(ts[0], ts[1]);
            let flow_ms = to_ms(ts[1], ts[2]);
            let smooth_ms = to_ms(ts[2], ts[3]);
            let hud_mask_ms = to_ms(ts[3], ts[4]);
            let synth_ms = to_ms(ts[4], ts[5]);
            let total_gpu_ms = to_ms(ts[0], ts[5]);
            Ok(GpuPassTimings {
                pyramid_ms,
                flow_ms,
                smooth_ms,
                hud_mask_ms,
                synth_ms,
                total_gpu_ms,
            })
        }
    }

    pub fn readback_output_rgba(&mut self) -> Result<Vec<u8>, String> {
        let ptr = &mut self.out_rgba as *mut Tex2D;
        unsafe { self.readback_tex(&mut *ptr) }
    }

    pub fn readback_ground_truth_rgba(&mut self) -> Result<Vec<u8>, String> {
        let ptr = &mut self.gt_rgba as *mut Tex2D;
        unsafe { self.readback_tex(&mut *ptr) }
    }

    unsafe fn readback_tex(&mut self, tex: &mut Tex2D) -> Result<Vec<u8>, String> {
        unsafe {
            self.begin_cmd()?;
            transition(&self.cmd_list, tex, D3D12_RESOURCE_STATE_COPY_SOURCE);
            let src_loc = D3D12_TEXTURE_COPY_LOCATION {
                pResource: ManuallyDrop::new(Some(tex.res.clone())),
                Type: D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
                Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 {
                    SubresourceIndex: 0,
                },
            };
            let dst_loc = D3D12_TEXTURE_COPY_LOCATION {
                pResource: ManuallyDrop::new(Some(self.readback_buf.clone())),
                Type: D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
                Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 {
                    PlacedFootprint: D3D12_PLACED_SUBRESOURCE_FOOTPRINT {
                        Offset: 0,
                        Footprint: D3D12_SUBRESOURCE_FOOTPRINT {
                            Format: DXGI_FORMAT_R8G8B8A8_UNORM,
                            Width: self.width,
                            Height: self.height,
                            Depth: 1,
                            RowPitch: self.readback_row_pitch,
                        },
                    },
                },
            };
            self.cmd_list
                .CopyTextureRegion(&dst_loc, 0, 0, 0, &src_loc, None);
            let _ = ManuallyDrop::into_inner(src_loc.pResource);
            let _ = ManuallyDrop::into_inner(dst_loc.pResource);
            self.end_cmd_and_wait()?;

            let mut mapped = std::ptr::null_mut();
            self.readback_buf
                .Map(0, None, Some(&mut mapped))
                .map_err(|e| format!("Map readback: {e}"))?;
            let pitch = self.readback_row_pitch as usize;
            let row_bytes = (self.width as usize) * 4;
            let total = (self.height as usize) * row_bytes;
            let src_slice =
                std::slice::from_raw_parts(mapped as *const u8, pitch * (self.height as usize));
            let mut out = vec![0u8; total];
            for y in 0..(self.height as usize) {
                out[y * row_bytes..(y + 1) * row_bytes]
                    .copy_from_slice(&src_slice[y * pitch..y * pitch + row_bytes]);
            }
            self.readback_buf.Unmap(0, None);
            Ok(out)
        }
    }

    unsafe fn begin_cmd(&mut self) -> Result<(), String> {
        unsafe {
            self.allocator
                .Reset()
                .map_err(|e| format!("Reset allocator: {e}"))?;
            self.cmd_list
                .Reset(&self.allocator, None)
                .map_err(|e| format!("Reset cmd_list: {e}"))?;
            Ok(())
        }
    }

    unsafe fn end_cmd_and_wait(&mut self) -> Result<(), String> {
        unsafe {
            self.cmd_list
                .Close()
                .map_err(|e| format!("Close cmd_list: {e}"))?;
            let list: ID3D12CommandList = self.cmd_list.cast().unwrap();
            self.queue.ExecuteCommandLists(&[Some(list)]);
            self.fence_val += 1;
            self.queue
                .Signal(&self.fence, self.fence_val)
                .map_err(|e| format!("Signal: {e}"))?;
            if self.fence.GetCompletedValue() < self.fence_val {
                self.fence
                    .SetEventOnCompletion(self.fence_val, self.fence_event)
                    .map_err(|e| format!("SetEventOnCompletion: {e}"))?;
                WaitForSingleObject(self.fence_event, INFINITE);
            }
            Ok(())
        }
    }

    unsafe fn write_table(
        &self,
        table_idx: u32,
        srvs: &[&Tex2D],
        uavs: &[&Tex2D],
    ) -> (D3D12_GPU_DESCRIPTOR_HANDLE, D3D12_GPU_DESCRIPTOR_HANDLE) {
        unsafe {
            write_desc_table(
                &self.device,
                &self.heap,
                self.desc_size,
                table_idx,
                srvs,
                uavs,
            )
        }
    }
}

unsafe fn write_desc_table(
    device: &ID3D12Device,
    heap: &ID3D12DescriptorHeap,
    desc_size: u32,
    table_idx: u32,
    srvs: &[&Tex2D],
    uavs: &[&Tex2D],
) -> (D3D12_GPU_DESCRIPTOR_HANDLE, D3D12_GPU_DESCRIPTOR_HANDLE) {
    unsafe {
        let base_cpu = heap.GetCPUDescriptorHandleForHeapStart().ptr;
        let base_gpu = heap.GetGPUDescriptorHandleForHeapStart().ptr;
        let stride = desc_size as usize;
        let offset = (table_idx * DESC_TABLE_STRIDE) as usize;

        for (i, tex) in srvs.iter().enumerate() {
            let cpu = D3D12_CPU_DESCRIPTOR_HANDLE {
                ptr: base_cpu + (offset + i) * stride,
            };
            let desc = D3D12_SHADER_RESOURCE_VIEW_DESC {
                Format: tex.format,
                ViewDimension: D3D12_SRV_DIMENSION_TEXTURE2D,
                Shader4ComponentMapping: D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING,
                Anonymous: D3D12_SHADER_RESOURCE_VIEW_DESC_0 {
                    Texture2D: D3D12_TEX2D_SRV {
                        MipLevels: 1,
                        ..Default::default()
                    },
                },
            };
            device.CreateShaderResourceView(&tex.res, Some(&desc), cpu);
        }

        let uav_slot = offset + 8;
        for (i, tex) in uavs.iter().enumerate() {
            let cpu = D3D12_CPU_DESCRIPTOR_HANDLE {
                ptr: base_cpu + (uav_slot + i) * stride,
            };
            let desc = D3D12_UNORDERED_ACCESS_VIEW_DESC {
                Format: tex.format,
                ViewDimension: D3D12_UAV_DIMENSION_TEXTURE2D,
                Anonymous: D3D12_UNORDERED_ACCESS_VIEW_DESC_0 {
                    Texture2D: D3D12_TEX2D_UAV::default(),
                },
            };
            device.CreateUnorderedAccessView(&tex.res, None, Some(&desc), cpu);
        }

        (
            D3D12_GPU_DESCRIPTOR_HANDLE {
                ptr: base_gpu + (offset * stride) as u64,
            },
            D3D12_GPU_DESCRIPTOR_HANDLE {
                ptr: base_gpu + (uav_slot * stride) as u64,
            },
        )
    }
}

impl Drop for Dx12FlowEngine {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.fence_event);
        }
    }
}

unsafe fn set_root_consts<T: bytemuck::Pod>(cmd: &ID3D12GraphicsCommandList, val: &T) {
    let words: &[u32] = cast_slice(std::slice::from_ref(val));
    unsafe {
        cmd.SetComputeRoot32BitConstants(0, words.len() as u32, words.as_ptr().cast(), 0);
    }
}

unsafe fn transition(
    cmd: &ID3D12GraphicsCommandList,
    tex: &mut Tex2D,
    next: D3D12_RESOURCE_STATES,
) {
    if tex.state == next {
        return;
    }
    let mut barrier = D3D12_RESOURCE_BARRIER {
        Type: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
        Anonymous: D3D12_RESOURCE_BARRIER_0 {
            Transition: ManuallyDrop::new(D3D12_RESOURCE_TRANSITION_BARRIER {
                pResource: ManuallyDrop::new(Some(tex.res.clone())),
                Subresource: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
                StateBefore: tex.state,
                StateAfter: next,
            }),
        },
    };
    unsafe {
        cmd.ResourceBarrier(std::slice::from_ref(&barrier));
        let t = &mut *barrier.Anonymous.Transition;
        ManuallyDrop::drop(&mut t.pResource);
        ManuallyDrop::drop(&mut barrier.Anonymous.Transition);
    }
    tex.state = next;
}

unsafe fn uav_barrier(cmd: &ID3D12GraphicsCommandList, res: &ID3D12Resource) {
    let mut barrier = D3D12_RESOURCE_BARRIER {
        Type: D3D12_RESOURCE_BARRIER_TYPE_UAV,
        Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
        Anonymous: D3D12_RESOURCE_BARRIER_0 {
            UAV: ManuallyDrop::new(D3D12_RESOURCE_UAV_BARRIER {
                pResource: ManuallyDrop::new(Some(res.clone())),
            }),
        },
    };
    unsafe {
        cmd.ResourceBarrier(std::slice::from_ref(&barrier));
        let u = &mut *barrier.Anonymous.UAV;
        ManuallyDrop::drop(&mut u.pResource);
        ManuallyDrop::drop(&mut barrier.Anonymous.UAV);
    }
}

unsafe fn create_tex2d(
    device: &ID3D12Device,
    width: u32,
    height: u32,
    format: DXGI_FORMAT,
) -> Result<Tex2D, String> {
    let heap = D3D12_HEAP_PROPERTIES {
        Type: D3D12_HEAP_TYPE_DEFAULT,
        ..Default::default()
    };
    let desc = D3D12_RESOURCE_DESC {
        Dimension: D3D12_RESOURCE_DIMENSION_TEXTURE2D,
        Width: width as u64,
        Height: height,
        DepthOrArraySize: 1,
        MipLevels: 1,
        Format: format,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Layout: D3D12_TEXTURE_LAYOUT_UNKNOWN,
        Flags: D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
        ..Default::default()
    };
    let mut res_opt: Option<ID3D12Resource> = None;
    unsafe {
        device
            .CreateCommittedResource(
                &heap,
                D3D12_HEAP_FLAG_NONE,
                &desc,
                D3D12_RESOURCE_STATE_COMMON,
                None,
                &mut res_opt,
            )
            .map_err(|e| format!("CreateCommittedResource({width}x{height}): {e}"))?;
    }
    Ok(Tex2D {
        res: res_opt.ok_or("CreateCommittedResource null")?,
        state: D3D12_RESOURCE_STATE_COMMON,
        format,
        width,
        height,
    })
}

unsafe fn create_readback_buffer(
    device: &ID3D12Device,
    bytes: u64,
) -> Result<ID3D12Resource, String> {
    let heap = D3D12_HEAP_PROPERTIES {
        Type: D3D12_HEAP_TYPE_READBACK,
        ..Default::default()
    };
    let desc = D3D12_RESOURCE_DESC {
        Dimension: D3D12_RESOURCE_DIMENSION_BUFFER,
        Width: bytes,
        Height: 1,
        DepthOrArraySize: 1,
        MipLevels: 1,
        Format: DXGI_FORMAT_UNKNOWN,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Layout: D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
        Flags: D3D12_RESOURCE_FLAG_NONE,
        ..Default::default()
    };
    let mut res_opt: Option<ID3D12Resource> = None;
    unsafe {
        device
            .CreateCommittedResource(
                &heap,
                D3D12_HEAP_FLAG_NONE,
                &desc,
                D3D12_RESOURCE_STATE_COPY_DEST,
                None,
                &mut res_opt,
            )
            .map_err(|e| format!("CreateCommittedResource(readback): {e}"))?;
    }
    res_opt.ok_or_else(|| "readback buffer null".into())
}

unsafe fn create_root_signature(device: &ID3D12Device) -> Result<ID3D12RootSignature, String> {
    let srv_range = D3D12_DESCRIPTOR_RANGE {
        RangeType: D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
        NumDescriptors: 6,
        BaseShaderRegister: 0,
        RegisterSpace: 0,
        OffsetInDescriptorsFromTableStart: 0,
    };
    let uav_range = D3D12_DESCRIPTOR_RANGE {
        RangeType: D3D12_DESCRIPTOR_RANGE_TYPE_UAV,
        NumDescriptors: 4,
        BaseShaderRegister: 0,
        RegisterSpace: 0,
        OffsetInDescriptorsFromTableStart: 0,
    };
    let params = [
        D3D12_ROOT_PARAMETER {
            ParameterType: D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS,
            Anonymous: D3D12_ROOT_PARAMETER_0 {
                Constants: D3D12_ROOT_CONSTANTS {
                    ShaderRegister: 0,
                    RegisterSpace: 0,
                    Num32BitValues: 16,
                },
            },
            ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL,
        },
        D3D12_ROOT_PARAMETER {
            ParameterType: D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
            Anonymous: D3D12_ROOT_PARAMETER_0 {
                DescriptorTable: D3D12_ROOT_DESCRIPTOR_TABLE {
                    NumDescriptorRanges: 1,
                    pDescriptorRanges: &srv_range,
                },
            },
            ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL,
        },
        D3D12_ROOT_PARAMETER {
            ParameterType: D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
            Anonymous: D3D12_ROOT_PARAMETER_0 {
                DescriptorTable: D3D12_ROOT_DESCRIPTOR_TABLE {
                    NumDescriptorRanges: 1,
                    pDescriptorRanges: &uav_range,
                },
            },
            ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL,
        },
    ];
    let samplers = [
        D3D12_STATIC_SAMPLER_DESC {
            Filter: D3D12_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU: D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressV: D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressW: D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            ComparisonFunc: D3D12_COMPARISON_FUNC_NEVER,
            BorderColor: D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK,
            MinLOD: 0.0,
            MaxLOD: f32::MAX,
            ShaderRegister: 0,
            RegisterSpace: 0,
            ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL,
            ..Default::default()
        },
        D3D12_STATIC_SAMPLER_DESC {
            Filter: D3D12_FILTER_MIN_MAG_MIP_POINT,
            AddressU: D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressV: D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressW: D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            ComparisonFunc: D3D12_COMPARISON_FUNC_NEVER,
            BorderColor: D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK,
            MinLOD: 0.0,
            MaxLOD: f32::MAX,
            ShaderRegister: 1,
            RegisterSpace: 0,
            ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL,
            ..Default::default()
        },
    ];
    let desc = D3D12_ROOT_SIGNATURE_DESC {
        NumParameters: params.len() as u32,
        pParameters: params.as_ptr(),
        NumStaticSamplers: samplers.len() as u32,
        pStaticSamplers: samplers.as_ptr(),
        Flags: D3D12_ROOT_SIGNATURE_FLAG_NONE,
    };
    let mut blob = None;
    let mut err_blob = None;
    unsafe {
        D3D12SerializeRootSignature(
            &desc,
            D3D_ROOT_SIGNATURE_VERSION_1_0,
            &mut blob,
            Some(&mut err_blob),
        )
        .map_err(|e| format!("D3D12SerializeRootSignature: {e}"))?;
        let blob = blob.ok_or("root signature blob null")?;
        let bytes =
            std::slice::from_raw_parts(blob.GetBufferPointer() as *const u8, blob.GetBufferSize());
        device
            .CreateRootSignature(0, bytes)
            .map_err(|e| format!("CreateRootSignature: {e}"))
    }
}

unsafe fn create_compute_pso(
    device: &ID3D12Device,
    root_sig: &ID3D12RootSignature,
    dxil: &[u8],
) -> Result<ID3D12PipelineState, String> {
    let desc = D3D12_COMPUTE_PIPELINE_STATE_DESC {
        pRootSignature: ManuallyDrop::new(Some(root_sig.clone())),
        CS: D3D12_SHADER_BYTECODE {
            pShaderBytecode: dxil.as_ptr().cast(),
            BytecodeLength: dxil.len(),
        },
        ..Default::default()
    };
    let res = unsafe {
        device
            .CreateComputePipelineState(&desc)
            .map_err(|e| format!("CreateComputePipelineState: {e}"))
    };
    let _ = ManuallyDrop::into_inner(desc.pRootSignature);
    res
}
