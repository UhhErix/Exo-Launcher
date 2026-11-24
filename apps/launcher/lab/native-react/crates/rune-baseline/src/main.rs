use blitz_react_core::metrics::{
    FrameSummary, ProcessTreeStats, current_process_cpu_ms, round2, sample_process_tree,
    summarize_ms,
};
use rune::tok::type_;
use rune::{App, AppSpec, Color, Cx, El, Tx, col, el, row, text};
use serde::Serialize;
use std::cell::RefCell;
use std::path::PathBuf;
use std::rc::Rc;
use std::time::{Duration, Instant};

const VIEW_W: f32 = 1280.0;
const VIEW_H: f32 = 800.0;

#[derive(Clone, Debug, Serialize)]
struct RuneBaselineReport {
    engine: String,
    scene: String,
    cold_start_ms: f64,
    anim_frame_ms: FrameSummary,
    rune_frame_ms: FrameSummary,
    input_latency_ms: FrameSummary,
    idle_cpu_pct: f64,
    memory: ProcessTreeStats,
    binary_bytes: u64,
}

struct State {
    out_dir: PathBuf,
    boot_at: Instant,
    cold_start_ms: f64,
    switch_on: bool,
    active_tab: usize,
    open_accordion: usize,
    frame_samples: Vec<f64>,
    rune_frames: Rc<RefCell<Vec<f64>>>,
    input_samples: Vec<f64>,
    phase: u8,
    idle_cpu0: f64,
    idle_wall0: Instant,
    anim_t: f32,
}

fn c(r: f32, g: f32, b: f32, a: f32) -> Color {
    Color { r, g, b, a }
}

fn view(s: &State, _cx: &mut Cx<State>) -> El {
    let pulse = ((s.anim_t * 2.0).sin() * 0.5 + 0.5).clamp(0.0, 1.0);
    let accent = c(0.24 + pulse * 0.28, 0.52 + pulse * 0.20, 0.96, 1.0);
    let card_bg = c(0.05, 0.05, 0.07, 1.0);
    let border = c(0.18, 0.18, 0.22, 1.0);
    let fg = c(0.96, 0.96, 0.98, 1.0);
    let muted = c(0.62, 0.64, 0.70, 1.0);

    let tabs = ["Overview", "Controls", "Shaders"];
    let mut tab_row = row().gap(8.0);
    for (i, label) in tabs.iter().enumerate() {
        let active = s.active_tab == i;
        let bg = if active {
            accent
        } else {
            c(0.09, 0.09, 0.11, 1.0)
        };
        let btn = el()
            .px(14.0)
            .py(8.0)
            .radius(8.0)
            .fill(bg)
            .child(text(*label).font(type_::BODY).color(fg));
        tab_row = tab_row.child(btn);
    }

    let sw_bg = if s.switch_on {
        accent
    } else {
        c(0.22, 0.22, 0.26, 1.0)
    };
    let switch_el = el().w(44.0).h(24.0).radius(12.0).fill(sw_bg);

    let card = col()
        .gap(12.0)
        .px(20.0)
        .py(20.0)
        .radius(14.0)
        .fill(card_bg)
        .border(1.0, border)
        .child(
            text("Hand-written Rust Rune gallery parity")
                .font(type_::BODY)
                .color(fg),
        )
        .child(
            text("Zero JS engine, zero DOM, zero WebView2 child processes.")
                .font(type_::BODY)
                .color(muted),
        )
        .child(
            row()
                .gap(12.0)
                .items_center()
                .child(
                    el().px(14.0)
                        .py(8.0)
                        .radius(8.0)
                        .fill(accent)
                        .child(text("Primary action").font(type_::BODY).color(fg)),
                )
                .child(switch_el),
        );

    let mut accordion = col().gap(8.0);
    for (i, title) in [
        "Layout engine (Taffy)",
        "GPU compositor (wgpu DX12)",
        "Springs & Liquid Glass",
    ]
    .iter()
    .enumerate()
    {
        let open = s.open_accordion == i;
        let mut item = col()
            .px(14.0)
            .py(14.0)
            .radius(10.0)
            .fill(c(0.07, 0.07, 0.09, 1.0))
            .border(1.0, border)
            .child(text(*title).font(type_::BODY).color(fg));
        if open {
            item = item.child(
                text("Native Rust state update without JS bridge overhead.")
                    .font(type_::BODY)
                    .color(muted),
            );
        }
        accordion = accordion.child(item);
    }

    col()
        .w_full()
        .h_full()
        .px(28.0)
        .py(28.0)
        .gap(20.0)
        .fill(Color::BLACK)
        .child(tab_row)
        .child(card)
        .child(accordion)
}

fn poll(s: &mut State, _dt: f64, tx: &mut Tx) -> bool {
    if s.cold_start_ms == 0.0 {
        s.cold_start_ms = round2(s.boot_at.elapsed().as_secs_f64() * 1000.0);
    }
    match s.phase {
        0 => {
            let tf = Instant::now();
            s.anim_t += 0.016;
            s.frame_samples.push(tf.elapsed().as_secs_f64() * 1000.0);
            if s.frame_samples.len() >= 60 {
                let ti = Instant::now();
                s.switch_on = !s.switch_on;
                s.active_tab = 1;
                s.open_accordion = 1;
                s.input_samples.push(ti.elapsed().as_secs_f64() * 1000.0);
                s.phase = 1;
                s.idle_cpu0 = current_process_cpu_ms();
                s.idle_wall0 = Instant::now();
            }
            tx.wake_after(Duration::from_millis(16));
            true
        }
        1 => {
            if s.idle_wall0.elapsed() >= Duration::from_millis(2000) {
                s.phase = 2;
                s.idle_cpu0 = current_process_cpu_ms();
                s.idle_wall0 = Instant::now();
            }
            tx.wake_after(Duration::from_millis(250));
            false
        }
        2 => {
            if s.idle_wall0.elapsed() >= Duration::from_millis(2000) {
                let cpu_d = (current_process_cpu_ms() - s.idle_cpu0).max(0.0);
                let wall_ms = s.idle_wall0.elapsed().as_secs_f64() * 1000.0;
                let idle_cpu_pct = round2((cpu_d / wall_ms.max(1.0)) * 100.0);
                let mem = sample_process_tree(std::process::id());
                let binary_bytes = std::env::current_exe()
                    .ok()
                    .and_then(|p| std::fs::metadata(p).ok())
                    .map(|m| m.len())
                    .unwrap_or(0);
                let rf = s.rune_frames.borrow().clone();
                let report = RuneBaselineReport {
                    engine: "rune-handwritten".to_string(),
                    scene: "gallery-parity".to_string(),
                    cold_start_ms: s.cold_start_ms,
                    anim_frame_ms: summarize_ms(&s.frame_samples),
                    rune_frame_ms: summarize_ms(&rf),
                    input_latency_ms: summarize_ms(&s.input_samples),
                    idle_cpu_pct,
                    memory: mem,
                    binary_bytes,
                };
                let _ = std::fs::create_dir_all(&s.out_dir);
                let p = s.out_dir.join("rune-baseline.json");
                let _ = std::fs::write(&p, serde_json::to_string_pretty(&report).unwrap());
                println!("{}", serde_json::to_string(&report).unwrap());
                s.phase = 3;
                std::process::exit(0);
            }
            tx.wake_after(Duration::from_millis(250));
            false
        }
        _ => false,
    }
}

fn main() {
    let mut out_dir = PathBuf::from("out");
    let mut it = std::env::args().skip(1);
    while let Some(a) = it.next() {
        if a == "--out" {
            if let Some(v) = it.next() {
                out_dir = PathBuf::from(v);
            }
        }
    }

    let rune_frames = Rc::new(RefCell::new(Vec::<f64>::new()));
    let rf_clone = rune_frames.clone();
    let now = Instant::now();
    let state = State {
        out_dir,
        boot_at: now,
        cold_start_ms: 0.0,
        switch_on: true,
        active_tab: 0,
        open_accordion: 0,
        frame_samples: Vec::new(),
        rune_frames,
        input_samples: Vec::new(),
        phase: 0,
        idle_cpu0: 0.0,
        idle_wall0: now,
        anim_t: 0.0,
    };

    let mut spec = AppSpec::new(
        "Exo Hand-Written Rune Baseline",
        (VIEW_W, VIEW_H),
        || unreachable!(),
        view,
    );
    spec.poll = Some(poll);

    App::new(spec)
        .args(Vec::<String>::new())
        .init(move || state)
        .on_frame_encoded(move |fe| {
            let total = fe.update_ms().unwrap_or(0.0) + fe.encode_ms().unwrap_or(0.0);
            if total > 0.0 {
                rf_clone.borrow_mut().push(total);
            }
        })
        .run();
}
