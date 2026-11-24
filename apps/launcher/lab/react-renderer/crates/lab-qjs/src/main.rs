// SPDX-License-Identifier: MIT

use lab_core::dom::NodeId;
use lab_core::{HostContext, JsEngine, parse_cli, run_lab_app};
use rquickjs::{Context, Function, Runtime, Value};
use std::cell::RefCell;
use std::rc::Rc;

struct QuickJsEngine {
    rt: Runtime,
    ctx: Context,
}

impl QuickJsEngine {
    fn new(
        host: Rc<RefCell<HostContext>>,
        mode: &str,
        search: &str,
        pipe_connected: bool,
    ) -> Result<Self, String> {
        let rt = Runtime::new().map_err(|e| e.to_string())?;
        rt.set_memory_limit(512 * 1024 * 1024);
        rt.set_max_stack_size(6 * 1024 * 1024);
        let ctx = Context::full(&rt).map_err(|e| e.to_string())?;

        ctx.with(|js| -> rquickjs::Result<()> {
            let globals = js.globals();
            globals.set("__rune_boot_mode", mode)?;
            globals.set("__rune_boot_search", search)?;
            globals.set("__rune_pipe_connected", pipe_connected)?;

            let h = Rc::clone(&host);
            globals.set(
                "__rune_commit",
                Function::new(js.clone(), move |ops_json: String| {
                    h.borrow_mut().commit_json(&ops_json);
                })?,
            )?;

            let h = Rc::clone(&host);
            globals.set(
                "__rune_get_rect",
                Function::new(js.clone(), move |id: u32| -> Vec<f32> {
                    h.borrow_mut().get_rect(id).to_vec()
                })?,
            )?;

            let h = Rc::clone(&host);
            globals.set(
                "__rune_get_computed_prop",
                Function::new(js.clone(), move |id: u32, prop: String| -> String {
                    h.borrow_mut().get_computed_prop(id, &prop)
                })?,
            )?;

            let h = Rc::clone(&host);
            globals.set(
                "__rune_viewport",
                Function::new(js.clone(), move || -> Vec<f32> {
                    let vp = h.borrow().viewport;
                    vec![vp.0, vp.1]
                })?,
            )?;

            let h = Rc::clone(&host);
            globals.set(
                "__rune_now_ms",
                Function::new(js.clone(), move || -> f64 {
                    h.borrow().started_at.elapsed().as_secs_f64() * 1000.0
                })?,
            )?;

            let h = Rc::clone(&host);
            globals.set(
                "__rune_log",
                Function::new(js.clone(), move |level: String, msg: String| {
                    h.borrow_mut().push_log(&level, &msg);
                })?,
            )?;

            let h = Rc::clone(&host);
            globals.set(
                "__rune_set_timer",
                Function::new(js.clone(), move |id: u32, delay_ms: f64, repeat: bool| {
                    h.borrow_mut().schedule_timer(id, delay_ms, repeat);
                })?,
            )?;

            let h = Rc::clone(&host);
            globals.set(
                "__rune_clear_timer",
                Function::new(js.clone(), move |id: u32| {
                    h.borrow_mut().clear_timer(id);
                })?,
            )?;

            let h = Rc::clone(&host);
            globals.set(
                "__exo_rpc_post",
                Function::new(js.clone(), move |msg_json: String| {
                    h.borrow_mut().rpc.handle_webview_post(&msg_json);
                })?,
            )?;

            let h = Rc::clone(&host);
            globals.set(
                "__exo_mouse_post",
                Function::new(js.clone(), move |msg_json: String| {
                    h.borrow_mut().rpc.handle_mouse_post(&msg_json);
                })?,
            )?;

            Ok(())
        })
        .map_err(|e| e.to_string())?;

        Ok(Self { rt, ctx })
    }

    fn call_global_str(&self, fn_name: &str, arg: &str) {
        self.ctx.with(|js| {
            let globals = js.globals();
            if let Ok(f) = globals.get::<_, Function>(fn_name) {
                if let Err(err) = f.call::<_, Value>((arg,)) {
                    eprintln!("[qjs:{fn_name}] {err}");
                }
            }
        });
    }
}

impl JsEngine for QuickJsEngine {
    fn engine_name(&self) -> &'static str {
        "rquickjs (QuickJS 0.14)"
    }

    fn eval_bundle(&mut self, _name: &str, source: &str) -> Result<(), String> {
        let res = self.ctx.with(|js| match js.eval::<Value, _>(source) {
            Ok(_) => Ok(()),
            Err(rquickjs::Error::Exception) => {
                let exc = js.catch();
                let msg = if let Some(e) = exc.as_exception() {
                    format!(
                        "{}\n{}",
                        e.message().unwrap_or_default(),
                        e.stack().unwrap_or_default()
                    )
                } else {
                    format!("{exc:?}")
                };
                Err(msg)
            }
            Err(e) => Err(e.to_string()),
        });
        self.pump_microtasks();
        res
    }

    fn dispatch_event(&mut self, node_id: NodeId, event_type: &str, payload_json: &str) {
        self.ctx.with(|js| {
            let globals = js.globals();
            if let Ok(f) = globals.get::<_, Function>("__rune_on_event") {
                if let Err(err) = f.call::<_, Value>((node_id, event_type, payload_json)) {
                    eprintln!("[qjs:event] {err}");
                }
            }
        });
    }

    fn fire_timer(&mut self, timer_id: u32) {
        self.ctx.with(|js| {
            let globals = js.globals();
            if let Ok(f) = globals.get::<_, Function>("__rune_on_timer") {
                let _ = f.call::<_, Value>((timer_id,));
            }
        });
    }

    fn flush_raf(&mut self) {
        self.ctx.with(|js| {
            let globals = js.globals();
            if let Ok(f) = globals.get::<_, Function>("__rune_on_raf") {
                let _ = f.call::<_, Value>(());
            }
        });
    }

    fn notify_resize(&mut self) {
        self.ctx.with(|js| {
            let globals = js.globals();
            if let Ok(f) = globals.get::<_, Function>("__rune_on_resize") {
                let _ = f.call::<_, Value>(());
            }
        });
    }

    fn deliver_rpc_message(&mut self, msg_json: &str) {
        self.call_global_str("__rune_on_rpc_message", msg_json);
    }

    fn deliver_mouse_message(&mut self, msg_json: &str) {
        self.call_global_str("__rune_on_mouse_message", msg_json);
    }

    fn pump_microtasks(&mut self) {
        while self.rt.is_job_pending() {
            match self.rt.execute_pending_job() {
                Ok(true) => {}
                Ok(false) => break,
                Err(err) => {
                    eprintln!("[qjs:job] {err}");
                    break;
                }
            }
        }
    }
}

fn main() {
    let cli = parse_cli();
    run_lab_app(
        cli,
        Box::new(|host, mode, search, pipe_connected| {
            Ok(Box::new(QuickJsEngine::new(
                host,
                mode,
                search,
                pipe_connected,
            )?))
        }),
    );
}
