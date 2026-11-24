// SPDX-License-Identifier: MIT

pub mod css;
pub mod dom;
pub mod layout;
pub mod metrics;
pub mod render;
pub mod rpc;

use crate::css::CssEngine;
use crate::dom::{DomArena, HTML_ID, MutationOp, NodeId, ROOT_ID};
use crate::layout::compute_layout;
use crate::metrics::{BenchReport, percentile, sample_process_memory};
use crate::render::build_rune_tree;
use crate::rpc::{InboundHostMsg, RpcBridge};
use exo_kit::{FONT_FAMILIES, FONTS, exo_titlebar};
use rune::{App, AppSpec, Cx, El, Key, KeyEvent, Tx, Waker};
use std::cell::RefCell;
use std::collections::VecDeque;
use std::path::PathBuf;
use std::rc::Rc;
use std::time::{Duration, Instant};

#[derive(Clone, Debug)]
pub struct PendingTimer {
    pub id: u32,
    pub fire_at: Instant,
    pub delay: Duration,
    pub repeat: bool,
}

#[derive(Clone, Debug)]
pub struct LabCli {
    pub bundle: PathBuf,
    pub css: Vec<PathBuf>,
    pub pipe: Option<String>,
    pub mode: String,
    pub search: String,
    pub report_json: Option<PathBuf>,
    pub bench_clicks: usize,
    pub passthrough: Vec<String>,
}

pub fn parse_cli() -> LabCli {
    let mut bundle = PathBuf::from("js/dist/mouse.bundle.js");
    let mut css = Vec::new();
    let mut pipe = None;
    let mut mode = "mock".to_string();
    let mut search = String::new();
    let mut report_json = None;
    let mut bench_clicks = 0usize;
    let mut passthrough = Vec::new();

    let mut args = std::env::args();
    if let Some(exe) = args.next() {
        passthrough.push(exe);
    }
    while let Some(arg) = args.next() {
        if arg == "--bundle" {
            if let Some(v) = args.next() {
                bundle = PathBuf::from(v);
            }
        } else if let Some(v) = arg.strip_prefix("--bundle=") {
            bundle = PathBuf::from(v);
        } else if arg == "--css" {
            if let Some(v) = args.next() {
                css.push(PathBuf::from(v));
            }
        } else if let Some(v) = arg.strip_prefix("--css=") {
            css.push(PathBuf::from(v));
        } else if arg == "--pipe" {
            pipe = args.next();
        } else if let Some(v) = arg.strip_prefix("--pipe=") {
            pipe = Some(v.to_string());
        } else if arg == "--mode" {
            if let Some(v) = args.next() {
                mode = v;
            }
        } else if let Some(v) = arg.strip_prefix("--mode=") {
            mode = v.to_string();
        } else if arg == "--search" {
            if let Some(v) = args.next() {
                search = v;
            }
        } else if let Some(v) = arg.strip_prefix("--search=") {
            search = v.to_string();
        } else if arg == "--report-json" {
            if let Some(v) = args.next() {
                report_json = Some(PathBuf::from(v));
            }
        } else if let Some(v) = arg.strip_prefix("--report-json=") {
            report_json = Some(PathBuf::from(v));
        } else if arg == "--bench-clicks" {
            if let Some(v) = args.next() {
                bench_clicks = v.parse().unwrap_or(0);
            }
        } else if let Some(v) = arg.strip_prefix("--bench-clicks=") {
            bench_clicks = v.parse().unwrap_or(0);
        } else {
            passthrough.push(arg);
        }
    }

    if css.is_empty() {
        let inferred = bundle.with_extension("css");
        if inferred.exists() {
            css.push(inferred);
        }
    }

    LabCli {
        bundle,
        css,
        pipe,
        mode,
        search,
        report_json,
        bench_clicks,
        passthrough,
    }
}

pub struct HostContext {
    pub arena: DomArena,
    pub css: CssEngine,
    pub rpc: RpcBridge,
    pub timers: Vec<PendingTimer>,
    pub viewport: (f32, f32),
    pub started_at: Instant,
    pub logs: VecDeque<String>,
}

impl HostContext {
    pub fn new(viewport: (f32, f32)) -> Self {
        Self {
            arena: DomArena::new(),
            css: CssEngine::default(),
            rpc: RpcBridge::new(),
            timers: Vec::new(),
            viewport,
            started_at: Instant::now(),
            logs: VecDeque::new(),
        }
    }

    pub fn ensure_layout(&mut self) -> (f64, f64) {
        let mut cascade_ms = 0.0;
        let mut layout_ms = 0.0;
        if self.arena.style_dirty {
            let t0 = Instant::now();
            self.css.cascade(&mut self.arena, HTML_ID, self.viewport.0);
            cascade_ms = t0.elapsed().as_secs_f64() * 1000.0;
            self.arena.layout_dirty = true;
        }
        if self.arena.layout_dirty {
            layout_ms = compute_layout(&mut self.arena, HTML_ID, self.viewport.0, self.viewport.1);
        }
        (cascade_ms, layout_ms)
    }

    pub fn commit_json(&mut self, ops_json: &str) {
        match serde_json::from_str::<Vec<MutationOp>>(ops_json) {
            Ok(ops) => {
                self.arena.apply_ops(ops);
            }
            Err(err) => {
                self.push_log("error", &format!("commit parse error: {err}"));
            }
        }
    }

    pub fn get_rect(&mut self, id: NodeId) -> [f32; 6] {
        self.ensure_layout();
        self.arena
            .nodes
            .get(&id)
            .map(|n| {
                [
                    n.rect.x,
                    n.rect.y,
                    n.rect.width,
                    n.rect.height,
                    n.rect.local_x,
                    n.rect.local_y,
                ]
            })
            .unwrap_or([0.0; 6])
    }

    pub fn get_computed_prop(&mut self, id: NodeId, prop: &str) -> String {
        if self.arena.style_dirty {
            self.css.cascade(&mut self.arena, HTML_ID, self.viewport.0);
        }
        let Some(node) = self.arena.nodes.get(&id) else {
            return String::new();
        };
        if prop.starts_with("--") {
            return node.computed.vars.get(prop).cloned().unwrap_or_default();
        }
        if let Some(v) = node.computed.raw_props.get(prop) {
            return v.clone();
        }
        match prop {
            "padding-left" => format!("{}px", node.computed.padding[3]),
            "padding-right" => format!("{}px", node.computed.padding[1]),
            "padding-top" => format!("{}px", node.computed.padding[0]),
            "padding-bottom" => format!("{}px", node.computed.padding[2]),
            "font-size" => format!("{}px", node.computed.font_size),
            _ => String::new(),
        }
    }

    pub fn schedule_timer(&mut self, id: u32, delay_ms: f64, repeat: bool) {
        let delay = Duration::from_secs_f64((delay_ms / 1000.0).max(0.0));
        self.timers.retain(|t| t.id != id);
        self.timers.push(PendingTimer {
            id,
            fire_at: Instant::now() + delay,
            delay,
            repeat,
        });
    }

    pub fn clear_timer(&mut self, id: u32) {
        self.timers.retain(|t| t.id != id);
    }

    pub fn pop_due_timers(&mut self) -> Vec<u32> {
        let now = Instant::now();
        let mut due = Vec::new();
        let mut i = 0;
        while i < self.timers.len() {
            if self.timers[i].fire_at <= now {
                let id = self.timers[i].id;
                due.push(id);
                if self.timers[i].repeat {
                    let d = self.timers[i].delay.max(Duration::from_millis(4));
                    self.timers[i].fire_at = now + d;
                    i += 1;
                } else {
                    self.timers.swap_remove(i);
                }
            } else {
                i += 1;
            }
        }
        due
    }

    pub fn push_log(&mut self, level: &str, msg: &str) {
        let line = format!("[js:{level}] {msg}");
        if level == "error" {
            eprintln!("{line}");
        }
        if self.logs.len() >= 128 {
            self.logs.pop_front();
        }
        self.logs.push_back(line);
    }
}

pub trait JsEngine {
    fn engine_name(&self) -> &'static str;
    fn eval_bundle(&mut self, name: &str, source: &str) -> Result<(), String>;
    fn dispatch_event(&mut self, node_id: NodeId, event_type: &str, payload_json: &str);
    fn fire_timer(&mut self, timer_id: u32);
    fn flush_raf(&mut self);
    fn notify_resize(&mut self);
    fn deliver_rpc_message(&mut self, msg_json: &str);
    fn deliver_mouse_message(&mut self, msg_json: &str);
    fn pump_microtasks(&mut self);
}

pub type EngineFactory = Box<
    dyn FnOnce(Rc<RefCell<HostContext>>, &str, &str, bool) -> Result<Box<dyn JsEngine>, String>,
>;

pub struct LabState {
    pub ctx: Rc<RefCell<HostContext>>,
    pub engine: Option<Box<dyn JsEngine>>,
    pub report: BenchReport,
    pub report_path: Option<PathBuf>,
    pub bench_clicks_remaining: usize,
    pub frame_count: u64,
    pub first_frame_recorded: bool,
    pub started_at: Instant,
    pub last_view_size: (f32, f32),
}

impl Default for LabState {
    fn default() -> Self {
        Self {
            ctx: Rc::new(RefCell::new(HostContext::new((1280.0, 800.0)))),
            engine: None,
            report: BenchReport::default(),
            report_path: None,
            bench_clicks_remaining: 0,
            frame_count: 0,
            first_frame_recorded: false,
            started_at: Instant::now(),
            last_view_size: (1280.0, 800.0),
        }
    }
}

impl LabState {
    pub fn dispatch_click(&mut self, id: NodeId) {
        let t0 = Instant::now();
        if let Some(ref mut eng) = self.engine {
            eng.dispatch_event(id, "click", "{}");
            eng.pump_microtasks();
        }
        let _ = self.ctx.borrow_mut().ensure_layout();
        let ms = t0.elapsed().as_secs_f64() * 1000.0;
        self.report.input_latency_ms.push(ms);
        self.flush_report();
    }

    pub fn dispatch_hover(&mut self, id: NodeId, inside: bool) {
        let changed = self.ctx.borrow_mut().arena.set_hover(id, inside);
        if changed {
            let ev = if inside { "mouseenter" } else { "mouseleave" };
            if let Some(ref mut eng) = self.engine {
                eng.dispatch_event(id, ev, "{}");
                eng.pump_microtasks();
            }
        }
    }

    pub fn dispatch_input_value(&mut self, id: NodeId, value: String) {
        let t0 = Instant::now();
        let payload = serde_json::json!({ "value": value }).to_string();
        if let Some(ref mut eng) = self.engine {
            eng.dispatch_event(id, "input", &payload);
            eng.pump_microtasks();
        }
        let _ = self.ctx.borrow_mut().ensure_layout();
        self.report
            .input_latency_ms
            .push(t0.elapsed().as_secs_f64() * 1000.0);
    }

    pub fn focus_node(&mut self, id: NodeId) {
        self.ctx.borrow_mut().arena.set_focused(Some(id));
        if let Some(ref mut eng) = self.engine {
            eng.dispatch_event(id, "focus", "{}");
            eng.pump_microtasks();
        }
    }

    pub fn handle_key(&mut self, ev: &KeyEvent) -> bool {
        let Some(focused) = self.ctx.borrow().arena.focused_node() else {
            return false;
        };
        let (is_input, cur_val) = {
            let ctx = self.ctx.borrow();
            let node = ctx.arena.nodes.get(&focused);
            let inp = node.is_some_and(|n| n.tag == "input");
            let val = node.and_then(|n| n.attr("value")).unwrap_or("").to_string();
            (inp, val)
        };
        if !is_input {
            return false;
        }
        match &ev.key {
            Key::Text(ch) if !ev.mods.ctrl && !ev.mods.alt => {
                let mut next = cur_val;
                next.push_str(ch);
                self.dispatch_input_value(focused, next);
                true
            }
            Key::Backspace => {
                let mut next = cur_val;
                next.pop();
                self.dispatch_input_value(focused, next);
                true
            }
            Key::Escape => {
                self.ctx.borrow_mut().arena.set_focused(None);
                true
            }
            _ => false,
        }
    }

    fn run_bench_click(&mut self) -> bool {
        if self.bench_clicks_remaining == 0 {
            return false;
        }
        let target = {
            let ctx = self.ctx.borrow();
            ctx.arena
                .nodes
                .values()
                .filter(|n| {
                    !n.is_text
                        && !n.is_disabled()
                        && n.rect.width > 1.0
                        && n.rect.height > 1.0
                        && (n.tag == "button" || n.has_event("onClick"))
                })
                .map(|n| n.id)
                .collect::<Vec<_>>()
        };
        if target.is_empty() {
            self.bench_clicks_remaining = 0;
            return false;
        }
        let idx = self.bench_clicks_remaining % target.len();
        let nid = target[idx];
        self.bench_clicks_remaining -= 1;
        self.dispatch_click(nid);
        true
    }

    pub fn flush_report(&mut self) {
        let Some(ref path) = self.report_path else {
            return;
        };
        {
            let ctx = self.ctx.borrow();
            self.report.dom_nodes = ctx.arena.count_live_subtree(ROOT_ID);
            self.report.commit_count = ctx.arena.commit_count;
            self.report.total_ops = ctx.arena.total_ops;
            self.report.rpc_calls_sent = ctx.rpc.calls_sent;
            self.report.rpc_events_received = ctx.rpc.events_received;
        }
        self.report.memory = sample_process_memory();
        self.report.input_latency_p50_ms = percentile(&self.report.input_latency_ms, 0.5);
        self.report.frame_p50_ms = percentile(&self.report.frame_times_ms, 0.5);
        self.report.frame_p95_ms = percentile(&self.report.frame_times_ms, 0.95);
        if let Ok(json) = serde_json::to_string_pretty(&self.report) {
            let _ = std::fs::write(path, json);
        }
    }
}

fn view_lab(state: &LabState, cx: &mut Cx<LabState>) -> El {
    let mut ctx = state.ctx.borrow_mut();
    if let Some(r) = cx.layout("lab-root") {
        let vw = r[2].max(320.0);
        let vh = r[3].max(240.0);
        if (ctx.viewport.0 - vw).abs() > 0.5 || (ctx.viewport.1 - vh).abs() > 0.5 {
            ctx.viewport = (vw, vh);
            ctx.arena.style_dirty = true;
            ctx.arena.layout_dirty = true;
        }
    }
    ctx.ensure_layout();
    build_rune_tree(&ctx.arena, HTML_ID)
}

fn setup_lab(state: &mut LabState, waker: Waker) {
    state.ctx.borrow().rpc.set_waker(waker);
}

fn poll_lab(state: &mut LabState, _t: f64, _tx: &mut Tx) -> bool {
    let t0 = Instant::now();
    let mut changed = false;

    let inbound = state.ctx.borrow_mut().rpc.drain_inbound();
    if !inbound.is_empty() {
        if let Some(ref mut eng) = state.engine {
            for msg in inbound {
                match msg {
                    InboundHostMsg::Rpc(s) => eng.deliver_rpc_message(&s),
                    InboundHostMsg::Mouse(s) => eng.deliver_mouse_message(&s),
                }
            }
            eng.pump_microtasks();
        }
        changed = true;
    }

    let due = state.ctx.borrow_mut().pop_due_timers();
    if !due.is_empty() {
        if let Some(ref mut eng) = state.engine {
            for id in due {
                eng.fire_timer(id);
            }
            eng.pump_microtasks();
        }
        changed = true;
    }

    if let Some(ref mut eng) = state.engine {
        eng.flush_raf();
        eng.pump_microtasks();
    }

    let (vp_now, dirty) = {
        let mut ctx = state.ctx.borrow_mut();
        let dirty = ctx.arena.style_dirty || ctx.arena.layout_dirty;
        if dirty {
            ctx.ensure_layout();
        }
        (ctx.viewport, dirty)
    };
    if (vp_now.0 - state.last_view_size.0).abs() > 0.5
        || (vp_now.1 - state.last_view_size.1).abs() > 0.5
    {
        state.last_view_size = vp_now;
        if let Some(ref mut eng) = state.engine {
            eng.notify_resize();
            eng.pump_microtasks();
        }
        changed = true;
    }

    if dirty {
        changed = true;
    }

    if state.frame_count >= 2 && state.run_bench_click() {
        changed = true;
    }

    let frame_ms = t0.elapsed().as_secs_f64() * 1000.0;
    state.frame_count += 1;
    if !state.first_frame_recorded {
        state.first_frame_recorded = true;
        state.report.cold_first_frame_ms = state.started_at.elapsed().as_secs_f64() * 1000.0;
    }
    if state.report.frame_times_ms.len() < 240 {
        state.report.frame_times_ms.push(frame_ms);
    }
    if state.frame_count == 1 || state.frame_count % 15 == 0 || state.bench_clicks_remaining == 0 {
        state.flush_report();
    }
    if state.frame_count == 5 && std::env::var("LAB_DUMP_DOM").is_ok() {
        let ctx = state.ctx.borrow();
        fn dump(arena: &DomArena, id: NodeId, depth: usize, left: &mut usize) {
            if *left == 0 {
                return;
            }
            let Some(n) = arena.nodes.get(&id) else {
                return;
            };
            *left -= 1;
            let pad = "  ".repeat(depth.min(10));
            if n.is_text {
                eprintln!(
                    "{pad}#text({}) {:?} rect=({:.0},{:.0},{:.0}x{:.0}) color=({:.2},{:.2},{:.2},{:.2})",
                    n.id,
                    n.text.trim(),
                    n.rect.x,
                    n.rect.y,
                    n.rect.width,
                    n.rect.height,
                    n.computed.color.r,
                    n.computed.color.g,
                    n.computed.color.b,
                    n.computed.color.a
                );
            } else {
                eprintln!(
                    "{pad}<{}#{}> .{} disp={:?} rect=({:.0},{:.0},{:.0}x{:.0}) bg={:?} glass={:?}",
                    n.tag,
                    n.id,
                    n.classes.join("."),
                    n.computed.display,
                    n.rect.x,
                    n.rect.y,
                    n.rect.width,
                    n.rect.height,
                    n.computed.background,
                    n.computed.glass
                );
            }
            for &c in &n.children {
                dump(arena, c, depth + 1, left);
            }
        }
        let mut budget = 60usize;
        dump(&ctx.arena, HTML_ID, 0, &mut budget);
    }

    changed
}

fn on_key_lab(state: &mut LabState, ev: &KeyEvent, _tx: &mut Tx) -> bool {
    state.handle_key(ev)
}

pub fn run_lab_app(cli: LabCli, factory: EngineFactory) {
    let started_at = Instant::now();
    let title: &'static str = "Exo React-in-Rune Lab";
    let mut spec = AppSpec::new(title, (1280.0, 800.0), LabState::default, view_lab);
    spec.fonts = FONTS;
    spec.families = FONT_FAMILIES;
    spec.setup = Some(setup_lab);
    spec.poll = Some(poll_lab);
    spec.on_key = Some(on_key_lab);

    let passthrough = cli.passthrough.clone();
    let app = App::new(spec)
        .args(passthrough)
        .glass(exo_kit::EXO_GLASS_POLICY)
        .max_backdrop_blurs(exo_kit::EXO_MAX_BACKDROP_BLURS)
        .titlebar(exo_titlebar())
        .init(move || {
            let ctx = Rc::new(RefCell::new(HostContext::new((1280.0, 800.0))));

            let pipe_connected = if let Some(ref pipe) = cli.pipe {
                match ctx.borrow_mut().rpc.connect(pipe, Duration::from_secs(4)) {
                    Ok(()) => true,
                    Err(e) => {
                        eprintln!("[lab] pipe connect failed ({pipe}): {e}");
                        false
                    }
                }
            } else {
                false
            };

            for css_path in &cli.css {
                if let Ok(text) = std::fs::read_to_string(css_path) {
                    ctx.borrow_mut().css.parse_stylesheet(&text);
                } else {
                    eprintln!("[lab] failed to read css: {}", css_path.display());
                }
            }

            let t_js0 = Instant::now();
            let mut engine = factory(Rc::clone(&ctx), &cli.mode, &cli.search, pipe_connected)
                .expect("failed to initialize JS engine");
            let js_init_ms = t_js0.elapsed().as_secs_f64() * 1000.0;

            let bundle_src = std::fs::read_to_string(&cli.bundle)
                .unwrap_or_else(|e| panic!("failed to read bundle {}: {e}", cli.bundle.display()));
            let t_eval0 = Instant::now();
            if let Err(err) = engine.eval_bundle(&cli.bundle.display().to_string(), &bundle_src) {
                eprintln!("[lab] bundle eval error: {err}");
            }
            engine.pump_microtasks();
            let bundle_eval_ms = t_eval0.elapsed().as_secs_f64() * 1000.0;

            let (first_cascade_ms, first_layout_ms) = ctx.borrow_mut().ensure_layout();
            engine.notify_resize();
            engine.flush_raf();
            engine.pump_microtasks();
            let _ = ctx.borrow_mut().ensure_layout();

            let css_stats = ctx.borrow().css.stats.clone();
            let mut state = LabState {
                ctx,
                report: BenchReport {
                    engine: engine.engine_name().to_string(),
                    bundle: cli.bundle.display().to_string(),
                    mode: cli.mode.clone(),
                    pipe_connected,
                    js_init_ms,
                    bundle_eval_ms,
                    css_parse_ms: css_stats.parse_ms,
                    first_cascade_ms,
                    first_layout_ms,
                    css_rules: css_stats.rules_parsed,
                    css_decls: css_stats.declarations_parsed,
                    css_keyframes_skipped: css_stats.keyframes_skipped,
                    child_processes: 0,
                    ..Default::default()
                },
                engine: Some(engine),
                report_path: cli.report_json.clone(),
                bench_clicks_remaining: cli.bench_clicks,
                frame_count: 0,
                first_frame_recorded: false,
                started_at,
                last_view_size: (1280.0, 800.0),
            };
            state.flush_report();
            state
        });

    app.run();
}
