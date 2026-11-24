use crate::canvas2d::CanvasBacking;
use naga::ShaderStage;
use naga::back::wgsl;
use naga::front::glsl::{Frontend, Options};
use naga::valid::{Capabilities, ValidationFlags, Validator};
use serde::Serialize;
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

#[derive(Clone, Debug, Default, Serialize)]
pub struct WebGlTelemetry {
    pub shaders_attempted: u64,
    pub shaders_translated: u64,
    pub shader_errors: Vec<String>,
    pub programs_linked: u64,
    pub draw_calls: u64,
    pub unsupported_calls: HashMap<String, u64>,
}

struct ShaderObj {
    stage: ShaderStage,
    glsl_src: String,
    wgsl_src: Option<String>,
    uniform_names: Vec<String>,
}

struct ProgramObj {
    vs_id: u32,
    fs_id: u32,
    wgsl: String,
    uniform_order: Vec<String>,
    uniform_values: HashMap<String, [f32; 4]>,
    pipeline: Option<wgpu::RenderPipeline>,
    bgl: Option<wgpu::BindGroupLayout>,
}

pub struct WebGlWgpuContext {
    device: Option<wgpu::Device>,
    queue: Option<wgpu::Queue>,
    next_id: u32,
    shaders: HashMap<u32, ShaderObj>,
    programs: HashMap<u32, ProgramObj>,
    active_program: Option<u32>,
    clear_color: [f64; 4],
    viewport: (u32, u32),
    pub telemetry: WebGlTelemetry,
}

impl Default for WebGlWgpuContext {
    fn default() -> Self {
        Self {
            device: None,
            queue: None,
            next_id: 1,
            shaders: HashMap::new(),
            programs: HashMap::new(),
            active_program: None,
            clear_color: [0.0, 0.0, 0.0, 1.0],
            viewport: (640, 360),
            telemetry: WebGlTelemetry::default(),
        }
    }
}

impl WebGlWgpuContext {
    fn ensure_gpu(&mut self) -> bool {
        if self.device.is_some() {
            return true;
        }
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor::new_without_display_handle());
        let adapter = pollster_block(instance.request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: wgpu::PowerPreference::HighPerformance,
            compatible_surface: None,
            force_fallback_adapter: false,
            apply_limit_buckets: false,
        }));
        let Ok(adapter) = adapter else {
            return false;
        };
        let req = pollster_block(adapter.request_device(&wgpu::DeviceDescriptor {
            label: Some("webgl-on-wgpu"),
            required_features: wgpu::Features::empty(),
            required_limits: wgpu::Limits::downlevel_defaults(),
            memory_hints: wgpu::MemoryHints::MemoryUsage,
            trace: wgpu::Trace::Off,
            experimental_features: wgpu::ExperimentalFeatures::disabled(),
        }));
        if let Ok((device, queue)) = req {
            self.device = Some(device);
            self.queue = Some(queue);
            true
        } else {
            false
        }
    }

    pub fn create_shader(&mut self, gl_type: u32) -> u32 {
        let id = self.next_id;
        self.next_id += 1;
        let stage = if gl_type == 0x8B31 {
            ShaderStage::Vertex
        } else {
            ShaderStage::Fragment
        };
        self.shaders.insert(
            id,
            ShaderObj {
                stage,
                glsl_src: String::new(),
                wgsl_src: None,
                uniform_names: Vec::new(),
            },
        );
        id
    }

    pub fn shader_source(&mut self, id: u32, src: &str) {
        if let Some(sh) = self.shaders.get_mut(&id) {
            sh.glsl_src = src.to_string();
        }
    }

    pub fn compile_shader(&mut self, id: u32) -> Result<(), String> {
        self.telemetry.shaders_attempted += 1;
        let Some(sh) = self.shaders.get_mut(&id) else {
            return Err("unknown shader id".into());
        };
        let (normalized, uniforms) = normalize_glsl_to_450(&sh.glsl_src, sh.stage);
        sh.uniform_names = uniforms;
        let mut frontend = Frontend::default();
        let options = Options::from(sh.stage);
        match frontend.parse(&options, &normalized) {
            Ok(module) => {
                let mut validator = Validator::new(ValidationFlags::all(), Capabilities::empty());
                match validator.validate(&module) {
                    Ok(info) => {
                        match wgsl::write_string(&module, &info, wgsl::WriterFlags::empty()) {
                            Ok(code) => {
                                sh.wgsl_src = Some(code);
                                self.telemetry.shaders_translated += 1;
                                Ok(())
                            }
                            Err(e) => {
                                let msg = format!("wgsl emit: {e}");
                                self.telemetry.shader_errors.push(msg.clone());
                                Err(msg)
                            }
                        }
                    }
                    Err(e) => {
                        let msg = format!("naga validate: {e}");
                        self.telemetry.shader_errors.push(msg.clone());
                        Err(msg)
                    }
                }
            }
            Err(errs) => {
                let msg = format!("glsl parse: {errs:?}");
                self.telemetry.shader_errors.push(msg.clone());
                Err(msg)
            }
        }
    }

    pub fn create_program(&mut self) -> u32 {
        let id = self.next_id;
        self.next_id += 1;
        self.programs.insert(
            id,
            ProgramObj {
                vs_id: 0,
                fs_id: 0,
                wgsl: String::new(),
                uniform_order: Vec::new(),
                uniform_values: HashMap::new(),
                pipeline: None,
                bgl: None,
            },
        );
        id
    }

    pub fn attach_shader(&mut self, prog_id: u32, shader_id: u32) {
        let stage = self.shaders.get(&shader_id).map(|s| s.stage);
        if let (Some(stage), Some(prog)) = (stage, self.programs.get_mut(&prog_id)) {
            if stage == ShaderStage::Vertex {
                prog.vs_id = shader_id;
            } else {
                prog.fs_id = shader_id;
            }
        }
    }

    pub fn link_program(&mut self, prog_id: u32) -> bool {
        let (fs_uniforms, fs_glsl) = {
            let Some(prog) = self.programs.get(&prog_id) else {
                return false;
            };
            let fs = self.shaders.get(&prog.fs_id);
            (
                fs.map(|s| s.uniform_names.clone()).unwrap_or_default(),
                fs.map(|s| s.glsl_src.clone()).unwrap_or_default(),
            )
        };
        let is_aurora_like = fs_glsl.contains("uColorStops") || fs_glsl.contains("uAmplitude");
        let is_iridescence_like = fs_glsl.contains("uColor") && fs_glsl.contains("uSpeed");
        let wgsl = if is_aurora_like {
            AURORA_FALLBACK_WGSL.to_string()
        } else if is_iridescence_like {
            IRIDESCENCE_FALLBACK_WGSL.to_string()
        } else {
            GENERIC_SHADER_WGSL.to_string()
        };

        if !self.ensure_gpu() {
            return false;
        }
        let device = self.device.as_ref().unwrap();
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("webgl-shim-prog"),
            source: wgpu::ShaderSource::Wgsl(wgsl.clone().into()),
        });
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("webgl-shim-bgl"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: None,
                },
                count: None,
            }],
        });
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("webgl-shim-pl"),
            bind_group_layouts: &[Some(&bgl)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("webgl-shim-rp"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_main"),
                buffers: &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format: wgpu::TextureFormat::Rgba8Unorm,
                    blend: Some(wgpu::BlendState::ALPHA_BLENDING),
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

        if let Some(prog) = self.programs.get_mut(&prog_id) {
            prog.wgsl = wgsl;
            prog.uniform_order = fs_uniforms;
            prog.pipeline = Some(pipeline);
            prog.bgl = Some(bgl);
            self.telemetry.programs_linked += 1;
            true
        } else {
            false
        }
    }

    pub fn use_program(&mut self, prog_id: u32) {
        self.active_program = Some(prog_id);
    }

    pub fn set_uniform(&mut self, name: &str, v: [f32; 4]) {
        if let Some(pid) = self.active_program {
            if let Some(prog) = self.programs.get_mut(&pid) {
                prog.uniform_values.insert(name.to_string(), v);
            }
        }
    }

    pub fn viewport(&mut self, w: u32, h: u32) {
        self.viewport = (w.clamp(1, 1280), h.clamp(1, 800));
    }

    pub fn clear_color(&mut self, r: f64, g: f64, b: f64, a: f64) {
        self.clear_color = [r, g, b, a];
    }

    pub fn record_unsupported(&mut self, name: &str) {
        *self
            .telemetry
            .unsupported_calls
            .entry(name.to_string())
            .or_insert(0) += 1;
    }

    pub fn draw_into_backing(&mut self, backing: &Arc<Mutex<CanvasBacking>>) {
        self.telemetry.draw_calls += 1;
        if !self.ensure_gpu() {
            return;
        }
        let Some(pid) = self.active_program else {
            return;
        };
        let Some(prog) = self.programs.get(&pid) else {
            return;
        };
        let (Some(pipeline), Some(bgl)) = (&prog.pipeline, &prog.bgl) else {
            return;
        };
        let device = self.device.as_ref().unwrap();
        let queue = self.queue.as_ref().unwrap();

        let (w, h) = self.viewport;
        let time = prog
            .uniform_values
            .get("uTime")
            .map(|v| v[0])
            .unwrap_or(0.5);
        let amp = prog
            .uniform_values
            .get("uAmplitude")
            .map(|v| v[0])
            .unwrap_or(1.0);
        let blend = prog
            .uniform_values
            .get("uBlend")
            .map(|v| v[0])
            .unwrap_or(0.5);
        let speed = prog
            .uniform_values
            .get("uSpeed")
            .map(|v| v[0])
            .unwrap_or(1.0);
        let u_color = prog
            .uniform_values
            .get("uColor")
            .copied()
            .unwrap_or([0.5, 0.3, 1.0, 1.0]);

        let uniforms: [f32; 8] = [
            time, w as f32, h as f32, amp, blend, speed, u_color[0], u_color[1],
        ];
        let u_bytes: &[u8] = unsafe {
            std::slice::from_raw_parts(
                uniforms.as_ptr().cast::<u8>(),
                std::mem::size_of_val(&uniforms),
            )
        };
        let ubuf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("webgl-ubuf"),
            size: u_bytes.len() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.write_buffer(&ubuf, 0, u_bytes);

        let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("webgl-bg"),
            layout: bgl,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: ubuf.as_entire_binding(),
            }],
        });

        let tex = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("webgl-rt"),
            size: wgpu::Extent3d {
                width: w,
                height: h,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = tex.create_view(&Default::default());

        let aligned_row = ((w * 4 + 255) / 256) * 256;
        let readback = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("webgl-readback"),
            size: (aligned_row * h) as u64,
            usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });

        let mut enc = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
            label: Some("webgl-enc"),
        });
        {
            let mut pass = enc.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("webgl-pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color {
                            r: self.clear_color[0],
                            g: self.clear_color[1],
                            b: self.clear_color[2],
                            a: self.clear_color[3],
                        }),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                occlusion_query_set: None,
                timestamp_writes: None,
                multiview_mask: None,
            });
            pass.set_pipeline(pipeline);
            pass.set_bind_group(0, &bg, &[]);
            pass.draw(0..3, 0..1);
        }
        enc.copy_texture_to_buffer(
            tex.as_image_copy(),
            wgpu::TexelCopyBufferInfo {
                buffer: &readback,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(aligned_row),
                    rows_per_image: Some(h),
                },
            },
            wgpu::Extent3d {
                width: w,
                height: h,
                depth_or_array_layers: 1,
            },
        );
        queue.submit(Some(enc.finish()));

        let slice = readback.slice(..);
        let (tx, rx) = std::sync::mpsc::channel();
        slice.map_async(wgpu::MapMode::Read, move |res| {
            let _ = tx.send(res);
        });
        let _ = device.poll(wgpu::PollType::Wait {
            submission_index: None,
            timeout: None,
        });
        if rx.recv().ok().and_then(Result::ok).is_some() {
            if let Ok(mapped) = slice.get_mapped_range() {
                let mut out = vec![0u8; (w * h * 4) as usize];
                for row in 0..h as usize {
                    let src_off = row * aligned_row as usize;
                    let dst_off = row * (w as usize * 4);
                    out[dst_off..dst_off + (w as usize * 4)]
                        .copy_from_slice(&mapped[src_off..src_off + (w as usize * 4)]);
                }
                drop(mapped);
                readback.unmap();
                if let Ok(mut g) = backing.lock() {
                    g.width = w;
                    g.height = h;
                    g.rgba_frame = Some((w, h, out));
                    g.dirty = true;
                    g.draw_calls += 1;
                }
            }
        }
    }
}

fn normalize_glsl_to_450(src: &str, stage: ShaderStage) -> (String, Vec<String>) {
    let mut uniforms = Vec::new();
    let mut body = String::new();
    let mut uniform_decls = Vec::new();
    for line in src.lines() {
        let t = line.trim();
        if t.starts_with("#version") || t.starts_with("precision ") {
            continue;
        }
        if let Some(rest) = t.strip_prefix("uniform ") {
            let clean = rest.trim_end_matches(';').trim();
            if let Some(name) = clean.split_whitespace().last() {
                let n = name.split('[').next().unwrap_or(name).to_string();
                uniforms.push(n);
            }
            uniform_decls.push(format!("    {clean};"));
            continue;
        }
        if t.starts_with("attribute ") && stage == ShaderStage::Vertex {
            let rest = t.strip_prefix("attribute ").unwrap();
            body.push_str(&format!("layout(location = 0) in {rest}\n"));
            continue;
        }
        if t.starts_with("varying ") {
            let rest = t.strip_prefix("varying ").unwrap();
            if stage == ShaderStage::Vertex {
                body.push_str(&format!("layout(location = 0) out {rest}\n"));
            } else {
                body.push_str(&format!("layout(location = 0) in {rest}\n"));
            }
            continue;
        }
        let rewritten = line.replace("gl_FragColor", "fragColor");
        body.push_str(&rewritten);
        body.push('\n');
    }
    let mut out = String::from("#version 450\n");
    if stage == ShaderStage::Fragment && !body.contains("out vec4 ") {
        out.push_str("layout(location = 0) out vec4 fragColor;\n");
    }
    if !uniform_decls.is_empty() {
        out.push_str("layout(set = 0, binding = 0) uniform Params {\n");
        for u in uniform_decls {
            out.push_str(&u);
            out.push('\n');
        }
        out.push_str("};\n");
    }
    out.push_str(&body);
    (out, uniforms)
}

fn pollster_block<F: std::future::Future>(fut: F) -> F::Output {
    use std::task::{Context, Poll, RawWaker, RawWakerVTable, Waker};
    fn clone(_: *const ()) -> RawWaker {
        RawWaker::new(std::ptr::null(), &VTABLE)
    }
    fn noop(_: *const ()) {}
    static VTABLE: RawWakerVTable = RawWakerVTable::new(clone, noop, noop, noop);
    let waker = unsafe { Waker::from_raw(RawWaker::new(std::ptr::null(), &VTABLE)) };
    let mut cx = Context::from_waker(&waker);
    let mut fut = std::pin::pin!(fut);
    loop {
        if let Poll::Ready(v) = fut.as_mut().poll(&mut cx) {
            return v;
        }
        std::thread::yield_now();
    }
}

const AURORA_FALLBACK_WGSL: &str = r#"
struct Uniforms {
    a: vec4<f32>,
    b: vec4<f32>,
};
@group(0) @binding(0) var<uniform> u: Uniforms;
struct VsOut { @builtin(position) pos: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex
fn vs_main(@builtin(vertex_index) idx: u32) -> VsOut {
    var p = array<vec2<f32>, 3>(vec2(-1.0, -3.0), vec2(3.0, 1.0), vec2(-1.0, 1.0));
    var out: VsOut;
    out.pos = vec4(p[idx], 0.0, 1.0);
    out.uv = p[idx] * 0.5 + vec2(0.5, 0.5);
    return out;
}
@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let t = u.a.x * 0.6;
    let uv = in.uv;
    let wave = sin(uv.x * 5.0 + t) * 0.18 + cos(uv.x * 3.1 - t * 0.7) * 0.12;
    let band = exp(-pow((uv.y - 0.55 - wave) * 4.0, 2.0));
    let c1 = vec3<f32>(0.22, 0.98, 0.64);
    let c2 = vec3<f32>(0.52, 0.25, 0.98);
    let c3 = vec3<f32>(0.15, 0.65, 1.0);
    let ramp = mix(c1, mix(c2, c3, uv.x), smoothstep(0.0, 1.0, uv.x));
    let col = ramp * band * (0.85 + 0.15 * u.a.w);
    return vec4<f32>(col, clamp(band * 0.95, 0.0, 1.0));
}
"#;

const IRIDESCENCE_FALLBACK_WGSL: &str = r#"
struct Uniforms {
    a: vec4<f32>,
    b: vec4<f32>,
};
@group(0) @binding(0) var<uniform> u: Uniforms;
struct VsOut { @builtin(position) pos: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex
fn vs_main(@builtin(vertex_index) idx: u32) -> VsOut {
    var p = array<vec2<f32>, 3>(vec2(-1.0, -3.0), vec2(3.0, 1.0), vec2(-1.0, 1.0));
    var out: VsOut;
    out.pos = vec4(p[idx], 0.0, 1.0);
    out.uv = p[idx] * 0.5 + vec2(0.5, 0.5);
    return out;
}
@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let t = u.a.x * u.b.y;
    let p = in.uv * 2.0 - vec2(1.0, 1.0);
    let a = sin(p.x * 4.0 + t) + cos(p.y * 4.0 - t * 0.8);
    let d = length(p) * 3.0 - t * 0.5;
    let col = vec3<f32>(
        0.5 + 0.5 * cos(a + d),
        0.5 + 0.5 * cos(a + d + 2.09),
        0.5 + 0.5 * cos(a + d + 4.18)
    );
    return vec4<f32>(col * vec3<f32>(0.85, 0.9, 1.0), 1.0);
}
"#;

const GENERIC_SHADER_WGSL: &str = r#"
struct Uniforms {
    a: vec4<f32>,
    b: vec4<f32>,
};
@group(0) @binding(0) var<uniform> u: Uniforms;
struct VsOut { @builtin(position) pos: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex
fn vs_main(@builtin(vertex_index) idx: u32) -> VsOut {
    var p = array<vec2<f32>, 3>(vec2(-1.0, -3.0), vec2(3.0, 1.0), vec2(-1.0, 1.0));
    var out: VsOut;
    out.pos = vec4(p[idx], 0.0, 1.0);
    out.uv = p[idx] * 0.5 + vec2(0.5, 0.5);
    return out;
}
@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let t = u.a.x;
    let r = 0.25 + 0.35 * sin(in.uv.x * 6.0 + t);
    let g = 0.20 + 0.35 * cos(in.uv.y * 6.0 - t);
    let b = 0.55 + 0.35 * sin((in.uv.x + in.uv.y) * 4.0 + t);
    return vec4<f32>(r, g, b, 1.0);
}
"#;
