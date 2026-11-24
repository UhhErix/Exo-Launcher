pub mod canvas2d;
pub mod dom_bridge;
pub mod metrics;
pub mod slot;
pub mod waapi;
pub mod webgl_wgpu;

use crate::dom_bridge::{BlitzDomHost, BridgeStats, VIEW_H, VIEW_W};
use crate::metrics::{
    FrameSummary, ProcessTreeStats, current_process_cpu_ms, round2, sample_process_tree,
    summarize_ms, write_rgba_png,
};
use crate::slot::{BLITZ_SLOT_MARK, SlotCompositor, SlotFrame};
use rune::{App, AppSpec, Color, Cx, El, Tx, el};
use serde::Serialize;
use serde_json::Value;
use std::cell::RefCell;
use std::path::PathBuf;
use std::rc::Rc;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

#[derive(Clone, Debug)]
pub struct BenchArgs {
    pub scene: String,
    pub bundle_path: PathBuf,
    pub css_path: Option<PathBuf>,
    pub out_dir: PathBuf,
    pub anim_frames: usize,
    pub idle_ms: u64,
    pub headless: bool,
}

impl BenchArgs {
    pub fn parse() -> Self {
        let mut scene = String::from("heroui-gallery");
        let mut bundle_path = PathBuf::from("bundle.js");
        let mut css_path = None;
        let mut out_dir = PathBuf::from("out");
        let mut anim_frames = 30usize;
        let mut idle_ms = 1200u64;
        let mut headless = false;

        let mut it = std::env::args().skip(1);
        while let Some(arg) = it.next() {
            match arg.as_str() {
                "--scene" => {
                    if let Some(v) = it.next() {
                        scene = v;
                    }
                }
                "--bundle" => {
                    if let Some(v) = it.next() {
                        bundle_path = PathBuf::from(v);
                    }
                }
                "--css" => {
                    if let Some(v) = it.next() {
                        css_path = Some(PathBuf::from(v));
                    }
                }
                "--out" => {
                    if let Some(v) = it.next() {
                        out_dir = PathBuf::from(v);
                    }
                }
                "--frames" => {
                    if let Some(v) = it.next() {
                        anim_frames = v.parse().unwrap_or(30);
                    }
                }
                "--idle-ms" => {
                    if let Some(v) = it.next() {
                        idle_ms = v.parse().unwrap_or(1200);
                    }
                }
                "--headless" => headless = true,
                _ => {}
            }
        }
        Self {
            scene,
            bundle_path,
            css_path,
            out_dir,
            anim_frames,
            idle_ms,
            headless,
        }
    }
}

#[derive(Clone, Debug, Serialize)]
pub struct SceneBenchResult {
    pub engine: String,
    pub scene: String,
    pub ok: bool,
    pub js_errors: Vec<String>,
    pub cold_start_ms: f64,
    pub mount_ms: f64,
    pub first_paint_ms: f64,
    pub anim_frame_ms: FrameSummary,
    pub rune_frame_ms: FrameSummary,
    pub input_latency_ms: FrameSummary,
    pub idle_cpu_pct: f64,
    pub memory: ProcessTreeStats,
    pub binary_bytes: u64,
    pub non_black_pixels_pct: f64,
    pub interactions: Value,
    pub bridge_stats: BridgeStats,
    pub screenshot: String,
}

pub trait JsRuntime {
    fn engine_name(&self) -> &'static str;
    fn eval_bundle(&mut self, source: &str) -> Result<(), String>;
    fn tick_raf(&mut self, now_ms: f64) -> Result<(), String>;
    fn run_interactions(&mut self) -> Result<Value, String>;
    fn drain_errors(&mut self) -> Vec<String>;
}

pub fn non_black_pct(rgba: &[u8]) -> f64 {
    if rgba.len() < 4 {
        return 0.0;
    }
    let total = rgba.len() / 4;
    let mut non_bg = 0usize;
    let r0 = rgba[0];
    let g0 = rgba[1];
    let b0 = rgba[2];
    for px in rgba.chunks_exact(4) {
        let dr = px[0].abs_diff(r0);
        let dg = px[1].abs_diff(g0);
        let db = px[2].abs_diff(b0);
        if dr > 8 || dg > 8 || db > 8 {
            non_bg += 1;
        }
    }
    round2((non_bg as f64 / total as f64) * 100.0)
}

struct WindowBenchState {
    engine_name: String,
    args: BenchArgs,
    js_errors: Vec<String>,
    cold_start_ms: f64,
    mount_ms: f64,
    first_paint_ms: f64,
    anim_samples: Vec<f64>,
    rune_frames: Rc<RefCell<Vec<f64>>>,
    input_samples: Vec<f64>,
    binary_bytes: u64,
    non_black: f64,
    interactions: Value,
    bridge_stats: BridgeStats,
    png_name: String,
    started_at: Instant,
    idle_cpu_start: f64,
    idle_wall_start: Instant,
    idle_started: bool,
    finished: bool,
}

fn view_slot(_s: &WindowBenchState, _cx: &mut Cx<WindowBenchState>) -> El {
    el().w(VIEW_W as f32)
        .h(VIEW_H as f32)
        .fill(Color::BLACK)
        .effect(BLITZ_SLOT_MARK)
}

fn poll_bench(ctx: &mut WindowBenchState, _dt: f64, tx: &mut Tx) -> bool {
    if ctx.finished {
        return false;
    }
    if !ctx.idle_started && ctx.started_at.elapsed() >= Duration::from_millis(2500) {
        ctx.idle_started = true;
        ctx.idle_cpu_start = current_process_cpu_ms();
        ctx.idle_wall_start = Instant::now();
        tx.wake_after(Duration::from_millis(250));
        return false;
    }
    if ctx.idle_started && ctx.idle_wall_start.elapsed() >= Duration::from_millis(ctx.args.idle_ms)
    {
        let cpu_delta = (current_process_cpu_ms() - ctx.idle_cpu_start).max(0.0);
        let wall_ms = ctx.idle_wall_start.elapsed().as_secs_f64() * 1000.0;
        let idle_cpu_pct = round2((cpu_delta / wall_ms.max(1.0)) * 100.0);
        let mem = sample_process_tree(std::process::id());
        let rf = ctx.rune_frames.borrow().clone();
        let report = SceneBenchResult {
            engine: ctx.engine_name.clone(),
            scene: ctx.args.scene.clone(),
            ok: ctx.js_errors.is_empty(),
            js_errors: ctx.js_errors.clone(),
            cold_start_ms: ctx.cold_start_ms,
            mount_ms: ctx.mount_ms,
            first_paint_ms: ctx.first_paint_ms,
            anim_frame_ms: summarize_ms(&ctx.anim_samples),
            rune_frame_ms: summarize_ms(&rf),
            input_latency_ms: summarize_ms(&ctx.input_samples),
            idle_cpu_pct,
            memory: mem,
            binary_bytes: ctx.binary_bytes,
            non_black_pixels_pct: ctx.non_black,
            interactions: ctx.interactions.clone(),
            bridge_stats: ctx.bridge_stats.clone(),
            screenshot: ctx.png_name.clone(),
        };
        let _ = std::fs::create_dir_all(&ctx.args.out_dir);
        let json_path = ctx
            .args
            .out_dir
            .join(format!("{}-{}.json", ctx.engine_name, ctx.args.scene));
        let _ = std::fs::write(&json_path, serde_json::to_string_pretty(&report).unwrap());
        println!("{}", serde_json::to_string(&report).unwrap());
        ctx.finished = true;
        std::process::exit(0);
    }
    tx.wake_after(Duration::from_millis(250));
    false
}

pub fn run_engine_benchmark<F, R>(args: BenchArgs, build_runtime: F)
where
    F: FnOnce(Rc<RefCell<BlitzDomHost>>) -> R + 'static,
    R: JsRuntime + 'static,
{
    let t_boot = Instant::now();
    let css = args
        .css_path
        .as_ref()
        .and_then(|p| std::fs::read_to_string(p).ok())
        .unwrap_or_default();
    let bundle = std::fs::read_to_string(&args.bundle_path).unwrap_or_else(|e| {
        panic!("failed reading bundle {}: {e}", args.bundle_path.display());
    });

    let host = Rc::new(RefCell::new(BlitzDomHost::new(&css)));
    let mut runtime = build_runtime(host.clone());
    let engine_name = runtime.engine_name().to_string();

    let t_mount = Instant::now();
    let mut js_errors = Vec::new();
    if let Err(e) = runtime.eval_bundle(&bundle) {
        js_errors.push(e);
    }
    let _ = runtime.tick_raf(16.0);
    let _ = runtime.tick_raf(32.0);
    let mount_ms = round2(t_mount.elapsed().as_secs_f64() * 1000.0);

    let t_paint = Instant::now();
    let rgba = {
        let mut h = host.borrow_mut();
        h.tick_animations(32.0);
        h.paint_rgba()
    };
    let first_paint_ms = round2(t_paint.elapsed().as_secs_f64() * 1000.0);
    let cold_start_ms = round2(t_boot.elapsed().as_secs_f64() * 1000.0);
    let non_black = non_black_pct(&rgba);

    let mut anim_samples = Vec::with_capacity(args.anim_frames);
    let mut last_rgba = rgba;
    for i in 0..args.anim_frames {
        let sim_ms = 48.0 + (i as f64) * 16.666;
        let tf = Instant::now();
        if let Err(e) = runtime.tick_raf(sim_ms) {
            js_errors.push(e);
        }
        {
            let mut h = host.borrow_mut();
            let _ = h.tick_animations(sim_ms);
            last_rgba = h.paint_rgba();
        }
        anim_samples.push(tf.elapsed().as_secs_f64() * 1000.0);
    }

    let png_name = format!("{}-{}.png", engine_name, args.scene);
    let png_path = args.out_dir.join(&png_name);
    let _ = write_rgba_png(&png_path, VIEW_W, VIEW_H, &last_rgba);

    let mut input_samples = Vec::new();
    let t_in = Instant::now();
    let _ = runtime
        .eval_bundle("globalThis.__trigger_interactions && globalThis.__trigger_interactions();");
    let _ = runtime.tick_raf(600.0);
    let interactions = match runtime.run_interactions() {
        Ok(v) => v,
        Err(e) => {
            js_errors.push(e);
            Value::Null
        }
    };
    {
        let mut h = host.borrow_mut();
        h.tick_animations(600.0);
        let _ = h.paint_rgba();
    }
    input_samples.push(t_in.elapsed().as_secs_f64() * 1000.0);
    for step in 0..5 {
        let ti = Instant::now();
        let _ = runtime.tick_raf(620.0 + step as f64 * 16.0);
        {
            let mut h = host.borrow_mut();
            h.tick_animations(620.0 + step as f64 * 16.0);
            let _ = h.paint_rgba();
        }
        input_samples.push(ti.elapsed().as_secs_f64() * 1000.0);
    }

    js_errors.extend(runtime.drain_errors());

    let binary_bytes = std::env::current_exe()
        .ok()
        .and_then(|p| std::fs::metadata(p).ok())
        .map(|m| m.len())
        .unwrap_or(0);

    if args.headless {
        let cpu0 = current_process_cpu_ms();
        let wall0 = Instant::now();
        std::thread::sleep(Duration::from_millis(args.idle_ms.min(800)));
        let cpu_delta = (current_process_cpu_ms() - cpu0).max(0.0);
        let wall_ms = wall0.elapsed().as_secs_f64() * 1000.0;
        let idle_cpu_pct = round2((cpu_delta / wall_ms.max(1.0)) * 100.0);
        let mem = sample_process_tree(std::process::id());
        let bridge_stats = host.borrow().snapshot_stats();

        let report = SceneBenchResult {
            engine: engine_name.clone(),
            scene: args.scene.clone(),
            ok: js_errors.is_empty(),
            js_errors,
            cold_start_ms,
            mount_ms,
            first_paint_ms,
            anim_frame_ms: summarize_ms(&anim_samples),
            rune_frame_ms: FrameSummary::default(),
            input_latency_ms: summarize_ms(&input_samples),
            idle_cpu_pct,
            memory: mem,
            binary_bytes,
            non_black_pixels_pct: non_black.max(non_black_pct(&last_rgba)),
            interactions,
            bridge_stats,
            screenshot: png_name,
        };
        let _ = std::fs::create_dir_all(&args.out_dir);
        let json_path = args
            .out_dir
            .join(format!("{}-{}.json", engine_name, args.scene));
        let _ = std::fs::write(&json_path, serde_json::to_string_pretty(&report).unwrap());
        println!("{}", serde_json::to_string(&report).unwrap());
        return;
    }

    let slot = Arc::new(Mutex::new(SlotFrame::default()));
    slot.lock()
        .unwrap()
        .update(VIEW_W, VIEW_H, last_rgba.clone());
    let rune_frames = Rc::new(RefCell::new(Vec::<f64>::new()));
    let rf_clone = rune_frames.clone();
    let bridge_stats = host.borrow().snapshot_stats();

    let state = WindowBenchState {
        engine_name,
        args,
        js_errors,
        cold_start_ms,
        mount_ms,
        first_paint_ms,
        anim_samples,
        rune_frames,
        input_samples,
        binary_bytes,
        non_black: non_black.max(non_black_pct(&last_rgba)),
        interactions,
        bridge_stats,
        png_name,
        started_at: Instant::now(),
        idle_cpu_start: current_process_cpu_ms(),
        idle_wall_start: Instant::now(),
        idle_started: false,
        finished: false,
    };

    let mut spec = AppSpec::new(
        "Exo Native React Lab",
        (VIEW_W as f32, VIEW_H as f32),
        || unreachable!(),
        view_slot,
    );
    spec.poll = Some(poll_bench);

    App::new(spec)
        .args(Vec::<String>::new())
        .init(move || state)
        .compositor(SlotCompositor::new(slot))
        .on_frame_encoded(move |fe| {
            let total = fe.update_ms().unwrap_or(0.0) + fe.encode_ms().unwrap_or(0.0);
            if total > 0.0 {
                rf_clone.borrow_mut().push(total);
            }
        })
        .run();
    drop(runtime);
}
