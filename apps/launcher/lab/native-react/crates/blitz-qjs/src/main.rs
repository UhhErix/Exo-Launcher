use blitz_react_core::dom_bridge::BlitzDomHost;
use blitz_react_core::{BenchArgs, JsRuntime, run_engine_benchmark};
use rquickjs::{Context, Function, Runtime};
use serde_json::Value;
use std::cell::RefCell;
use std::rc::Rc;

struct QjsRuntime {
    _rt: Runtime,
    ctx: Context,
    errors: Rc<RefCell<Vec<String>>>,
}

impl QjsRuntime {
    fn new(host: Rc<RefCell<BlitzDomHost>>) -> Self {
        let rt = Runtime::new().expect("quickjs runtime");
        rt.set_memory_limit(512 * 1024 * 1024);
        rt.set_max_stack_size(0);
        let ctx = Context::full(&rt).expect("quickjs context");
        let errors: Rc<RefCell<Vec<String>>> = Rc::new(RefCell::new(Vec::new()));

        let host_mut = host.clone();
        let host_qry = host.clone();
        let err_log = errors.clone();

        ctx.with(|c| {
            let g = c.globals();
            let batch_fn = Function::new(c.clone(), move |json: String| -> i32 {
                match host_mut.borrow_mut().apply_batch_json(&json) {
                    Ok(n) => n as i32,
                    Err(_) => -1,
                }
            })
            .expect("batch fn");
            let query_fn = Function::new(c.clone(), move |json: String| -> String {
                host_qry.borrow_mut().query_sync(&json)
            })
            .expect("query fn");
            let log_fn = Function::new(c.clone(), move |level: String, msg: String| {
                if level == "error" {
                    err_log.borrow_mut().push(msg);
                }
            })
            .expect("log fn");

            g.set("__blitz_batch", batch_fn).unwrap();
            g.set("__blitz_query", query_fn).unwrap();
            g.set("__blitz_log", log_fn).unwrap();
        });

        Self {
            _rt: rt,
            ctx,
            errors,
        }
    }
}

impl JsRuntime for QjsRuntime {
    fn engine_name(&self) -> &'static str {
        "blitz-qjs"
    }

    fn eval_bundle(&mut self, source: &str) -> Result<(), String> {
        let res = self.ctx.with(|c| match c.eval::<(), _>(source.as_bytes()) {
            Ok(()) => Ok(()),
            Err(_) => {
                let exc = c.catch();
                let msg = exc
                    .as_exception()
                    .map(|e| format!("{:?} {}", e.message(), e.stack().unwrap_or_default()))
                    .unwrap_or_else(|| format!("{exc:?}"));
                Err(msg)
            }
        });
        while self._rt.execute_pending_job().unwrap_or(false) {}
        res
    }

    fn tick_raf(&mut self, now_ms: f64) -> Result<(), String> {
        let code = format!("globalThis.__blitz_tick && globalThis.__blitz_tick({now_ms});");
        self.eval_bundle(&code)
    }

    fn run_interactions(&mut self) -> Result<Value, String> {
        self.ctx.with(|c| {
            let res: Result<String, _> = c.eval(
                "typeof globalThis.__run_interactions === 'function' ? JSON.stringify(globalThis.__run_interactions()) : 'null'",
            );
            match res {
                Ok(s) => serde_json::from_str(&s).map_err(|e| e.to_string()),
                Err(_) => {
                    let exc = c.catch();
                    let msg = exc
                        .as_exception()
                        .map(|e| format!("{:?} {}", e.message(), e.stack().unwrap_or_default()))
                        .unwrap_or_else(|| format!("{exc:?}"));
                    Err(msg)
                }
            }
        })
    }

    fn drain_errors(&mut self) -> Vec<String> {
        std::mem::take(&mut *self.errors.borrow_mut())
    }
}

fn main() {
    let args = BenchArgs::parse();
    run_engine_benchmark(args, QjsRuntime::new);
}
