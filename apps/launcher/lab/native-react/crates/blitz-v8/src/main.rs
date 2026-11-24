use blitz_react_core::dom_bridge::BlitzDomHost;
use blitz_react_core::{BenchArgs, JsRuntime, run_engine_benchmark};
use serde_json::Value;
use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Once;

static V8_INIT: Once = Once::new();

thread_local! {
    static TL_HOST: RefCell<Option<Rc<RefCell<BlitzDomHost>>>> = const { RefCell::new(None) };
    static TL_ERRORS: RefCell<Vec<String>> = const { RefCell::new(Vec::new()) };
}

fn cb_batch(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments,
    mut rv: v8::ReturnValue,
) {
    let json = args.get(0).to_rust_string_lossy(scope);
    let res = TL_HOST.with(|cell| {
        cell.borrow()
            .as_ref()
            .map(|h| {
                h.borrow_mut()
                    .apply_batch_json(&json)
                    .map(|n| n as i32)
                    .unwrap_or(-1)
            })
            .unwrap_or(-1)
    });
    rv.set(v8::Integer::new(scope, res).into());
}

fn cb_query(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments,
    mut rv: v8::ReturnValue,
) {
    let json = args.get(0).to_rust_string_lossy(scope);
    let out = TL_HOST.with(|cell| {
        cell.borrow()
            .as_ref()
            .map(|h| h.borrow_mut().query_sync(&json))
            .unwrap_or_else(|| "null".to_string())
    });
    if let Some(s) = v8::String::new(scope, &out) {
        rv.set(s.into());
    }
}

fn cb_log(
    scope: &mut v8::PinScope<'_, '_>,
    args: v8::FunctionCallbackArguments,
    _rv: v8::ReturnValue,
) {
    let level = args.get(0).to_rust_string_lossy(scope);
    let msg = args.get(1).to_rust_string_lossy(scope);
    if level == "error" {
        TL_ERRORS.with(|e| e.borrow_mut().push(msg));
    }
}

struct V8Runtime {
    isolate: v8::OwnedIsolate,
    context: v8::Global<v8::Context>,
}

impl V8Runtime {
    fn new(host: Rc<RefCell<BlitzDomHost>>) -> Self {
        V8_INIT.call_once(|| {
            let platform = v8::new_default_platform(0, false).make_shared();
            v8::V8::initialize_platform(platform);
            v8::V8::initialize();
        });
        TL_HOST.with(|c| *c.borrow_mut() = Some(host));
        TL_ERRORS.with(|e| e.borrow_mut().clear());

        let mut isolate = v8::Isolate::new(v8::CreateParams::default());
        let global_ctx = {
            v8::scope!(handle_scope, &mut isolate);
            let ctx = v8::Context::new(handle_scope, Default::default());
            let scope = &mut v8::ContextScope::new(handle_scope, ctx);
            let global = ctx.global(scope);

            let k1 = v8::String::new(scope, "__blitz_batch").unwrap();
            let f1 = v8::Function::new(scope, cb_batch).unwrap();
            global.set(scope, k1.into(), f1.into());

            let k2 = v8::String::new(scope, "__blitz_query").unwrap();
            let f2 = v8::Function::new(scope, cb_query).unwrap();
            global.set(scope, k2.into(), f2.into());

            let k3 = v8::String::new(scope, "__blitz_log").unwrap();
            let f3 = v8::Function::new(scope, cb_log).unwrap();
            global.set(scope, k3.into(), f3.into());

            v8::Global::new(scope, ctx)
        };

        Self {
            isolate,
            context: global_ctx,
        }
    }

    fn eval_string(&mut self, code: &str) -> Result<String, String> {
        v8::scope!(handle_scope, &mut self.isolate);
        let ctx = v8::Local::new(handle_scope, &self.context);
        let scope = &mut v8::ContextScope::new(handle_scope, ctx);
        v8::tc_scope!(tc, scope);
        let Some(src) = v8::String::new(tc, code) else {
            return Err("v8 string alloc failed".into());
        };
        let Some(script) = v8::Script::compile(tc, src, None) else {
            let err = tc
                .exception()
                .map(|e| e.to_rust_string_lossy(tc))
                .unwrap_or_else(|| "compile error".into());
            return Err(err);
        };
        match script.run(tc) {
            Some(val) => {
                tc.perform_microtask_checkpoint();
                Ok(val.to_rust_string_lossy(tc))
            }
            None => {
                let err = tc
                    .exception()
                    .map(|e| e.to_rust_string_lossy(tc))
                    .unwrap_or_else(|| "runtime error".into());
                Err(err)
            }
        }
    }
}

impl JsRuntime for V8Runtime {
    fn engine_name(&self) -> &'static str {
        "blitz-v8"
    }

    fn eval_bundle(&mut self, source: &str) -> Result<(), String> {
        self.eval_string(source).map(|_| ())
    }

    fn tick_raf(&mut self, now_ms: f64) -> Result<(), String> {
        let code = format!("globalThis.__blitz_tick && globalThis.__blitz_tick({now_ms});");
        self.eval_string(&code).map(|_| ())
    }

    fn run_interactions(&mut self) -> Result<Value, String> {
        let s = self.eval_string(
            "typeof globalThis.__run_interactions === 'function' ? JSON.stringify(globalThis.__run_interactions()) : 'null'",
        )?;
        serde_json::from_str(&s).map_err(|e| e.to_string())
    }

    fn drain_errors(&mut self) -> Vec<String> {
        TL_ERRORS.with(|e| std::mem::take(&mut *e.borrow_mut()))
    }
}

fn main() {
    let args = BenchArgs::parse();
    run_engine_benchmark(args, V8Runtime::new);
}
