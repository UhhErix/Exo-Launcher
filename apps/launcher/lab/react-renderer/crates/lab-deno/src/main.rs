// SPDX-License-Identifier: MIT

use deno_core::{Extension, JsRuntime, OpDecl, OpState, RuntimeOptions, op2};
use lab_core::dom::NodeId;
use lab_core::{HostContext, JsEngine, parse_cli, run_lab_app};
use std::cell::RefCell;
use std::rc::Rc;

#[op2(fast)]
fn op_rune_commit(state: &mut OpState, #[string] ops_json: String) {
    if let Some(h) = state.try_borrow::<Rc<RefCell<HostContext>>>().cloned() {
        h.borrow_mut().commit_json(&ops_json);
    }
}

#[op2]
#[serde]
fn op_rune_get_rect(state: &mut OpState, id: u32) -> Vec<f32> {
    state
        .try_borrow::<Rc<RefCell<HostContext>>>()
        .cloned()
        .map(|h| h.borrow_mut().get_rect(id).to_vec())
        .unwrap_or_else(|| vec![0.0; 6])
}

#[op2]
#[string]
fn op_rune_get_computed_prop(state: &mut OpState, id: u32, #[string] prop: String) -> String {
    state
        .try_borrow::<Rc<RefCell<HostContext>>>()
        .cloned()
        .map(|h| h.borrow_mut().get_computed_prop(id, &prop))
        .unwrap_or_default()
}

#[op2]
#[serde]
fn op_rune_viewport(state: &mut OpState) -> Vec<f32> {
    state
        .try_borrow::<Rc<RefCell<HostContext>>>()
        .cloned()
        .map(|h| {
            let vp = h.borrow().viewport;
            vec![vp.0, vp.1]
        })
        .unwrap_or_else(|| vec![1280.0, 800.0])
}

#[op2(fast)]
fn op_rune_now_ms(state: &mut OpState) -> f64 {
    state
        .try_borrow::<Rc<RefCell<HostContext>>>()
        .cloned()
        .map(|h| h.borrow().started_at.elapsed().as_secs_f64() * 1000.0)
        .unwrap_or(0.0)
}

#[op2(fast)]
fn op_rune_log(state: &mut OpState, #[string] level: String, #[string] msg: String) {
    if let Some(h) = state.try_borrow::<Rc<RefCell<HostContext>>>().cloned() {
        h.borrow_mut().push_log(&level, &msg);
    }
}

#[op2(fast)]
fn op_rune_set_timer(state: &mut OpState, id: u32, delay_ms: f64, repeat: bool) {
    if let Some(h) = state.try_borrow::<Rc<RefCell<HostContext>>>().cloned() {
        h.borrow_mut().schedule_timer(id, delay_ms, repeat);
    }
}

#[op2(fast)]
fn op_rune_clear_timer(state: &mut OpState, id: u32) {
    if let Some(h) = state.try_borrow::<Rc<RefCell<HostContext>>>().cloned() {
        h.borrow_mut().clear_timer(id);
    }
}

#[op2(fast)]
fn op_exo_rpc_post(state: &mut OpState, #[string] msg: String) {
    if let Some(h) = state.try_borrow::<Rc<RefCell<HostContext>>>().cloned() {
        h.borrow_mut().rpc.handle_webview_post(&msg);
    }
}

#[op2(fast)]
fn op_exo_mouse_post(state: &mut OpState, #[string] msg: String) {
    if let Some(h) = state.try_borrow::<Rc<RefCell<HostContext>>>().cloned() {
        h.borrow_mut().rpc.handle_mouse_post(&msg);
    }
}

struct DenoCoreEngine {
    _tokio_rt: tokio::runtime::Runtime,
    runtime: JsRuntime,
}

impl DenoCoreEngine {
    fn new(
        host: Rc<RefCell<HostContext>>,
        mode: &str,
        search: &str,
        pipe_connected: bool,
    ) -> Result<Self, String> {
        let tokio_rt = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .map_err(|e| e.to_string())?;
        let _guard = tokio_rt.enter();
        const OPS: &[OpDecl] = &[
            op_rune_commit(),
            op_rune_get_rect(),
            op_rune_get_computed_prop(),
            op_rune_viewport(),
            op_rune_now_ms(),
            op_rune_log(),
            op_rune_set_timer(),
            op_rune_clear_timer(),
            op_exo_rpc_post(),
            op_exo_mouse_post(),
        ];
        let ext = Extension {
            name: "exo_rune_bridge",
            ops: std::borrow::Cow::Borrowed(OPS),
            ..Default::default()
        };
        let mut runtime = JsRuntime::new(RuntimeOptions {
            extensions: vec![ext],
            ..Default::default()
        });
        runtime.op_state().borrow_mut().put(host);

        let bootstrap = format!(
            r#"
            globalThis.__rune_boot_mode = {};
            globalThis.__rune_boot_search = {};
            globalThis.__rune_pipe_connected = {};
            globalThis.__rune_commit = (s) => Deno.core.ops.op_rune_commit(s);
            globalThis.__rune_get_rect = (id) => Deno.core.ops.op_rune_get_rect(id);
            globalThis.__rune_get_computed_prop = (id, p) => Deno.core.ops.op_rune_get_computed_prop(id, p);
            globalThis.__rune_viewport = () => Deno.core.ops.op_rune_viewport();
            globalThis.__rune_now_ms = () => Deno.core.ops.op_rune_now_ms();
            globalThis.__rune_log = (l, m) => Deno.core.ops.op_rune_log(l, m);
            globalThis.__rune_set_timer = (id, d, r) => Deno.core.ops.op_rune_set_timer(id, d, r);
            globalThis.__rune_clear_timer = (id) => Deno.core.ops.op_rune_clear_timer(id);
            globalThis.__exo_rpc_post = (s) => Deno.core.ops.op_exo_rpc_post(s);
            globalThis.__exo_mouse_post = (s) => Deno.core.ops.op_exo_mouse_post(s);
            "#,
            serde_json::to_string(mode).unwrap(),
            serde_json::to_string(search).unwrap(),
            pipe_connected,
        );
        runtime
            .execute_script("<exo-bridge>", bootstrap)
            .map_err(|e| e.to_string())?;
        drop(_guard);
        Ok(Self {
            _tokio_rt: tokio_rt,
            runtime,
        })
    }

    fn exec_quiet(&mut self, name: &'static str, script: String) {
        if let Err(err) = self.runtime.execute_script(name, script) {
            eprintln!("[deno:{name}] {err}");
        }
    }
}

impl JsEngine for DenoCoreEngine {
    fn engine_name(&self) -> &'static str {
        "deno_core (0.413 + V8 JIT)"
    }

    fn eval_bundle(&mut self, _name: &str, source: &str) -> Result<(), String> {
        self.runtime
            .execute_script("<bundle>", source.to_string())
            .map_err(|e| e.to_string())?;
        self.pump_microtasks();
        Ok(())
    }

    fn dispatch_event(&mut self, node_id: NodeId, event_type: &str, payload_json: &str) {
        let code = format!(
            "globalThis.__rune_on_event && globalThis.__rune_on_event({}, {}, {});",
            node_id,
            serde_json::to_string(event_type).unwrap(),
            serde_json::to_string(payload_json).unwrap()
        );
        self.exec_quiet("<event>", code);
    }

    fn fire_timer(&mut self, timer_id: u32) {
        self.exec_quiet(
            "<timer>",
            format!("globalThis.__rune_on_timer && globalThis.__rune_on_timer({timer_id});"),
        );
    }

    fn flush_raf(&mut self) {
        self.exec_quiet(
            "<raf>",
            "globalThis.__rune_on_raf && globalThis.__rune_on_raf();".into(),
        );
    }

    fn notify_resize(&mut self) {
        self.exec_quiet(
            "<resize>",
            "globalThis.__rune_on_resize && globalThis.__rune_on_resize();".into(),
        );
    }

    fn deliver_rpc_message(&mut self, msg_json: &str) {
        let code = format!(
            "globalThis.__rune_on_rpc_message && globalThis.__rune_on_rpc_message({});",
            serde_json::to_string(msg_json).unwrap()
        );
        self.exec_quiet("<rpc>", code);
    }

    fn deliver_mouse_message(&mut self, msg_json: &str) {
        let code = format!(
            "globalThis.__rune_on_mouse_message && globalThis.__rune_on_mouse_message({});",
            serde_json::to_string(msg_json).unwrap()
        );
        self.exec_quiet("<mouse>", code);
    }

    fn pump_microtasks(&mut self) {
        self.runtime.v8_isolate().perform_microtask_checkpoint();
    }
}

fn main() {
    let cli = parse_cli();
    run_lab_app(
        cli,
        Box::new(|host, mode, search, pipe_connected| {
            Ok(Box::new(DenoCoreEngine::new(
                host,
                mode,
                search,
                pipe_connected,
            )?))
        }),
    );
}
