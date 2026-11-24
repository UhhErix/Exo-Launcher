use crate::metrics::write_rgba_png;
use rune::compose::{Compositor, DeviceKey, Frame};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};

pub const BLITZ_SLOT_MARK: u32 = 0xE201;

#[derive(Default)]
pub struct SlotFrame {
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
    pub version: u64,
    pub pending_capture: Option<PathBuf>,
}

impl SlotFrame {
    pub fn update(&mut self, width: u32, height: u32, rgba: Vec<u8>) {
        self.width = width;
        self.height = height;
        self.rgba = rgba;
        self.version = self.version.wrapping_add(1);
    }

    pub fn request_capture(&mut self, path: PathBuf) {
        self.pending_capture = Some(path);
    }
}

struct GpuSlot {
    device_key: DeviceKey,
    format: wgpu::TextureFormat,
    pipeline: wgpu::RenderPipeline,
    bgl: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    texture: Option<wgpu::Texture>,
    bind_group: Option<wgpu::BindGroup>,
    tex_size: (u32, u32),
    uploaded_version: u64,
}

const BLIT_WGSL: &str = r#"
struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) idx: u32) -> VsOut {
    var positions = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, -3.0),
        vec2<f32>(3.0, 1.0),
        vec2<f32>(-1.0, 1.0),
    );
    var uvs = array<vec2<f32>, 3>(
        vec2<f32>(0.0, 2.0),
        vec2<f32>(2.0, 0.0),
        vec2<f32>(0.0, 0.0),
    );
    var out: VsOut;
    out.pos = vec4<f32>(positions[idx], 0.0, 1.0);
    out.uv = uvs[idx];
    return out;
}

@group(0) @binding(0) var slot_tex: texture_2d<f32>;
@group(0) @binding(1) var slot_samp: sampler;

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    return textureSample(slot_tex, slot_samp, in.uv);
}
"#;

impl GpuSlot {
    fn new(device: &wgpu::Device, device_key: DeviceKey, format: wgpu::TextureFormat) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("blitz-slot-blit"),
            source: wgpu::ShaderSource::Wgsl(BLIT_WGSL.into()),
        });
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("blitz-slot-bgl"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("blitz-slot-layout"),
            bind_group_layouts: &[Some(&bgl)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("blitz-slot-pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(wgpu::BlendState::REPLACE),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            multiview_mask: None,
            cache: None,
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("blitz-slot-sampler"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        Self {
            device_key,
            format,
            pipeline,
            bgl,
            sampler,
            texture: None,
            bind_group: None,
            tex_size: (0, 0),
            uploaded_version: 0,
        }
    }

    fn upload_if_needed(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        shared: &Arc<Mutex<SlotFrame>>,
    ) {
        let guard = shared.lock().unwrap();
        if guard.width == 0
            || guard.height == 0
            || guard.rgba.len() < (guard.width as usize * guard.height as usize * 4)
        {
            return;
        }
        if guard.version == self.uploaded_version && self.bind_group.is_some() {
            return;
        }
        let (w, h) = (guard.width, guard.height);
        if self.tex_size != (w, h) || self.texture.is_none() {
            let tex = device.create_texture(&wgpu::TextureDescriptor {
                label: Some("blitz-frame-tex"),
                size: wgpu::Extent3d {
                    width: w,
                    height: h,
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format: wgpu::TextureFormat::Rgba8UnormSrgb,
                usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                view_formats: &[],
            });
            let view = tex.create_view(&Default::default());
            let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("blitz-frame-bg"),
                layout: &self.bgl,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(&view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::Sampler(&self.sampler),
                    },
                ],
            });
            self.texture = Some(tex);
            self.bind_group = Some(bg);
            self.tex_size = (w, h);
        }
        if let Some(tex) = &self.texture {
            queue.write_texture(
                tex.as_image_copy(),
                &guard.rgba[..(w as usize * h as usize * 4)],
                wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(w * 4),
                    rows_per_image: Some(h),
                },
                wgpu::Extent3d {
                    width: w,
                    height: h,
                    depth_or_array_layers: 1,
                },
            );
            self.uploaded_version = guard.version;
        }
    }
}

pub struct SlotCompositor {
    shared: Arc<Mutex<SlotFrame>>,
    gpu: Option<GpuSlot>,
}

impl SlotCompositor {
    pub fn new(shared: Arc<Mutex<SlotFrame>>) -> Self {
        Self { shared, gpu: None }
    }
}

impl<S: 'static> Compositor<S> for SlotCompositor {
    fn render(&mut self, f: &mut Frame<'_, S>) {
        let key = f.device_key();
        let format = f.format;
        let rebuild = self
            .gpu
            .as_ref()
            .is_none_or(|g| g.device_key != key || g.format != format);
        if rebuild {
            self.gpu = Some(GpuSlot::new(f.device, key, format));
        }
        if let Some(gpu) = &mut self.gpu {
            gpu.upload_if_needed(f.device, f.queue, &self.shared);
        }

        let clear = f.rt.clear_color();
        f.ui.set_fade_stops(f.rt.fade_stops());
        f.ui.set_max_backdrop_blurs(f.rt.max_backdrop_blurs());
        f.ui.prepare(
            f.device,
            f.queue,
            &mut f.rt.text.atlas,
            &f.rt.instances,
            f.size,
        );

        let gpu_ref = self.gpu.as_ref();
        f.ui.draw_with(f.encoder, f.target, clear, &mut |pass, site| {
            if site.mark != BLITZ_SLOT_MARK {
                return;
            }
            let Some(gpu) = gpu_ref else { return };
            let Some(bg) = &gpu.bind_group else { return };
            let [rx, ry, rw, rh] = site.instance.rect;
            let vx = (rx - site.origin[0]).max(0.0);
            let vy = (ry - site.origin[1]).max(0.0);
            let vw = rw.min(site.size[0] as f32 - vx).max(1.0);
            let vh = rh.min(site.size[1] as f32 - vy).max(1.0);
            pass.set_viewport(vx, vy, vw, vh, 0.0, 1.0);
            pass.set_pipeline(&gpu.pipeline);
            pass.set_bind_group(0, bg, &[]);
            pass.draw(0..3, 0..1);
        });

        let capture_path = {
            let mut g = self.shared.lock().unwrap();
            g.pending_capture.take()
        };
        if let Some(path) = capture_path {
            let g = self.shared.lock().unwrap();
            if g.width > 0
                && g.height > 0
                && g.rgba.len() >= (g.width as usize * g.height as usize * 4)
            {
                let _ = write_rgba_png(&path, g.width, g.height, &g.rgba);
            }
        }
    }

    fn release_gpu(&mut self) {
        self.gpu = None;
    }

    fn draws_own(&self) -> bool {
        true
    }
}
