// SPDX-License-Identifier: MIT

use lab_core::dom::NodeId;
use lab_core::{HostContext, JsEngine, parse_cli, run_lab_app};
use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Once;

static V8_INIT: Once = Once::new();

fn ensure_v8_init() {
    V8_INIT.call_once(|| {
        let platform = v8::new_default_platform(0, false).make_shared();
        v8::V8::initialize_platform(platform);
        v8::V8::initialize();
    });
}

struct V8Engine {
    isolate: v8::OwnedIsolate,
    context: v8::Global<v8::Context>,
}

fn host_from_scope(scope: &v8::PinScope<'_, '_>) -> Option<Rc<RefCell<HostContext>>> {
    scope.get_slot::<Rc<RefCell<HostContext>>>().cloned()
}

fn cb_commit(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments<'_>,
    _rv: v8::ReturnValue<'_>,
) {
    let ops_json = args.get(0).to_rust_string_lossy(scope);
    if let Some(h) = host_from_scope(scope) {
        h.borrow_mut().commit_json(&ops_json);
    }
}

fn cb_get_rect(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments<'_>,
    mut rv: v8::ReturnValue<'_>,
) {
    let id = args.get(0).uint32_value(scope).unwrap_or(0);
    let rect = host_from_scope(scope)
        .map(|h| h.borrow_mut().get_rect(id))
        .unwrap_or([0.0; 6]);
    let arr = v8::Array::new(scope, 6);
    for (i, v) in rect.iter().enumerate() {
        let num = v8::Number::new(scope, *v as f64);
        arr.set_index(scope, i as u32, num.into());
    }
    rv.set(arr.into());
}

fn cb_get_computed_prop(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments<'_>,
    mut rv: v8::ReturnValue<'_>,
) {
    let id = args.get(0).uint32_value(scope).unwrap_or(0);
    let prop = args.get(1).to_rust_string_lossy(scope);
    let val = host_from_scope(scope)
        .map(|h| h.borrow_mut().get_computed_prop(id, &prop))
        .unwrap_or_default();
    if let Some(s) = v8::String::new(scope, &val) {
        rv.set(s.into());
    }
}

fn cb_viewport(
    scope: &mut v8::PinScope<'_, '_>,
    _args: v8::FunctionCallbackArguments<'_>,
    mut rv: v8::ReturnValue<'_>,
) {
    let vp = host_from_scope(scope)
        .map(|h| h.borrow().viewport)
        .unwrap_or((1280.0, 800.0));
    let arr = v8::Array::new(scope, 2);
    let w = v8::Number::new(scope, vp.0 as f64);
    let h = v8::Number::new(scope, vp.1 as f64);
    arr.set_index(scope, 0, w.into());
    arr.set_index(scope, 1, h.into());
    rv.set(arr.into());
}

fn cb_now_ms(
    scope: &mut v8::PinScope<'_, '_>,
    _args: v8::FunctionCallbackArguments<'_>,
    mut rv: v8::ReturnValue<'_>,
) {
    let ms = host_from_scope(scope)
        .map(|h| h.borrow().started_at.elapsed().as_secs_f64() * 1000.0)
        .unwrap_or(0.0);
    rv.set(v8::Number::new(scope, ms).into());
}

fn cb_log(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments<'_>,
    _rv: v8::ReturnValue<'_>,
) {
    let level = args.get(0).to_rust_string_lossy(scope);
    let msg = args.get(1).to_rust_string_lossy(scope);
    if let Some(h) = host_from_scope(scope) {
        h.borrow_mut().push_log(&level, &msg);
    }
}

fn cb_set_timer(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments<'_>,
    _rv: v8::ReturnValue<'_>,
) {
    let id = args.get(0).uint32_value(scope).unwrap_or(0);
    let delay = args.get(1).number_value(scope).unwrap_or(0.0);
    let repeat = args.get(2).boolean_value(scope);
    if let Some(h) = host_from_scope(scope) {
        h.borrow_mut().schedule_timer(id, delay, repeat);
    }
}

fn cb_clear_timer(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments<'_>,
    _rv: v8::ReturnValue<'_>,
) {
    let id = args.get(0).uint32_value(scope).unwrap_or(0);
    if let Some(h) = host_from_scope(scope) {
        h.borrow_mut().clear_timer(id);
    }
}

fn cb_rpc_post(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments<'_>,
    _rv: v8::ReturnValue<'_>,
) {
    let msg = args.get(0).to_rust_string_lossy(scope);
    if let Some(h) = host_from_scope(scope) {
        h.borrow_mut().rpc.handle_webview_post(&msg);
    }
}

fn cb_mouse_post(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments<'_>,
    _rv: v8::ReturnValue<'_>,
) {
    let msg = args.get(0).to_rust_string_lossy(scope);
    if let Some(h) = host_from_scope(scope) {
        h.borrow_mut().rpc.handle_mouse_post(&msg);
    }
}

fn set_fn(
    scope: &mut v8::PinScope<'_, '_>,
    global: v8::Local<'_, v8::Object>,
    name: &str,
    cb: impl v8::MapFnTo<v8::FunctionCallback>,
) {
    let key = v8::String::new(scope, name).unwrap();
    let func = v8::Function::new(scope, cb).unwrap();
    global.set(scope, key.into(), func.into());
}

impl V8Engine {
    fn new(
        host: Rc<RefCell<HostContext>>,
        mode: &str,
        search: &str,
        pipe_connected: bool,
    ) -> Result<Self, String> {
        ensure_v8_init();
        let mut isolate = v8::Isolate::new(v8::CreateParams::default());
        isolate.set_slot(host);

        let global_ctx = {
            v8::scope!(let handle_scope, &mut isolate);
            let ctx = v8::Context::new(handle_scope, Default::default());
            let scope = &mut v8::ContextScope::new(handle_scope, ctx);
            let global = ctx.global(scope);

            let k_mode = v8::String::new(scope, "__rune_boot_mode").unwrap();
            let v_mode = v8::String::new(scope, mode).unwrap();
            global.set(scope, k_mode.into(), v_mode.into());

            let k_search = v8::String::new(scope, "__rune_boot_search").unwrap();
            let v_search = v8::String::new(scope, search).unwrap();
            global.set(scope, k_search.into(), v_search.into());

            let k_pipe = v8::String::new(scope, "__rune_pipe_connected").unwrap();
            let v_pipe = v8::Boolean::new(scope, pipe_connected);
            global.set(scope, k_pipe.into(), v_pipe.into());

            set_fn(scope, global, "__rune_commit", cb_commit);
            set_fn(scope, global, "__rune_get_rect", cb_get_rect);
            set_fn(
                scope,
                global,
                "__rune_get_computed_prop",
                cb_get_computed_prop,
            );
            set_fn(scope, global, "__rune_viewport", cb_viewport);
            set_fn(scope, global, "__rune_now_ms", cb_now_ms);
            set_fn(scope, global, "__rune_log", cb_log);
            set_fn(scope, global, "__rune_set_timer", cb_set_timer);
            set_fn(scope, global, "__rune_clear_timer", cb_clear_timer);
            set_fn(scope, global, "__exo_rpc_post", cb_rpc_post);
            set_fn(scope, global, "__exo_mouse_post", cb_mouse_post);

            v8::Global::new(scope, ctx)
        };

        Ok(Self {
            isolate,
            context: global_ctx,
        })
    }

    fn call_global(
        &mut self,
        name: &str,
        args_fn: impl for<'s, 'i> FnOnce(&mut v8::PinScope<'s, 'i>) -> Vec<v8::Local<'s, v8::Value>>,
    ) {
        v8::scope!(let handle_scope, &mut self.isolate);
        let ctx = v8::Local::new(handle_scope, &self.context);
        let scope = &mut v8::ContextScope::new(handle_scope, ctx);
        let global = ctx.global(scope);
        let key = v8::String::new(scope, name).unwrap();
        let Some(val) = global.get(scope, key.into()) else {
            return;
        };
        let Ok(func) = v8::Local::<v8::Function>::try_from(val) else {
            return;
        };
        let args = args_fn(scope);
        v8::tc_scope!(let tc, scope);
        if func.call(tc, global.into(), &args).is_none() {
            if let Some(exc) = tc.exception() {
                eprintln!("[v8:{name}] {}", exc.to_rust_string_lossy(tc));
            }
        }
    }
}

impl JsEngine for V8Engine {
    fn engine_name(&self) -> &'static str {
        "rusty_v8 (V8 152 JIT)"
    }

    fn eval_bundle(&mut self, _name: &str, source: &str) -> Result<(), String> {
        v8::scope!(let handle_scope, &mut self.isolate);
        let ctx = v8::Local::new(handle_scope, &self.context);
        let scope = &mut v8::ContextScope::new(handle_scope, ctx);
        v8::tc_scope!(let tc, scope);
        let code =
            v8::String::new(tc, source).ok_or_else(|| "v8 string alloc failed".to_string())?;
        let script = v8::Script::compile(tc, code, None).ok_or_else(|| {
            tc.exception()
                .map(|e| e.to_rust_string_lossy(tc))
                .unwrap_or_else(|| "v8 compile error".to_string())
        })?;
        if script.run(tc).is_none() {
            let msg = tc
                .stack_trace()
                .or_else(|| tc.exception())
                .map(|e| e.to_rust_string_lossy(tc))
                .unwrap_or_else(|| "v8 runtime error".to_string());
            return Err(msg);
        }
        tc.perform_microtask_checkpoint();
        Ok(())
    }

    fn dispatch_event(&mut self, node_id: NodeId, event_type: &str, payload_json: &str) {
        let ev = event_type.to_string();
        let pl = payload_json.to_string();
        self.call_global("__rune_on_event", move |scope| {
            vec![
                v8::Integer::new_from_unsigned(scope, node_id).into(),
                v8::String::new(scope, &ev).unwrap().into(),
                v8::String::new(scope, &pl).unwrap().into(),
            ]
        });
    }

    fn fire_timer(&mut self, timer_id: u32) {
        self.call_global("__rune_on_timer", move |scope| {
            vec![v8::Integer::new_from_unsigned(scope, timer_id).into()]
        });
    }

    fn flush_raf(&mut self) {
        self.call_global("__rune_on_raf", |_| Vec::new());
    }

    fn notify_resize(&mut self) {
        self.call_global("__rune_on_resize", |_| Vec::new());
    }

    fn deliver_rpc_message(&mut self, msg_json: &str) {
        let s = msg_json.to_string();
        self.call_global("__rune_on_rpc_message", move |scope| {
            vec![v8::String::new(scope, &s).unwrap().into()]
        });
    }

    fn deliver_mouse_message(&mut self, msg_json: &str) {
        let s = msg_json.to_string();
        self.call_global("__rune_on_mouse_message", move |scope| {
            vec![v8::String::new(scope, &s).unwrap().into()]
        });
    }

    fn pump_microtasks(&mut self) {
        self.isolate.perform_microtask_checkpoint();
    }
}

fn main() {
    let cli = parse_cli();
    run_lab_app(
        cli,
        Box::new(|host, mode, search, pipe_connected| {
            Ok(Box::new(V8Engine::new(host, mode, search, pipe_connected)?))
        }),
    );
}
