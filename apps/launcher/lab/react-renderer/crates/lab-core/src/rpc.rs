// SPDX-License-Identifier: MIT

use exo_rpc::{HostEvent, RpcClient};
use rune::Waker;
use serde_json::{Value, json};
use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

#[derive(Clone, Debug)]
pub enum InboundHostMsg {
    Rpc(String),
    Mouse(String),
}

pub struct RpcBridge {
    client: Option<Arc<RpcClient>>,
    inbound: Arc<Mutex<VecDeque<InboundHostMsg>>>,
    waker: Arc<Mutex<Option<Waker>>>,
    pub calls_sent: u64,
    pub events_received: u64,
}

impl Default for RpcBridge {
    fn default() -> Self {
        Self::new()
    }
}

impl RpcBridge {
    pub fn new() -> Self {
        Self {
            client: None,
            inbound: Arc::new(Mutex::new(VecDeque::new())),
            waker: Arc::new(Mutex::new(None)),
            calls_sent: 0,
            events_received: 0,
        }
    }

    pub fn set_waker(&self, waker: Waker) {
        if let Ok(mut guard) = self.waker.lock() {
            *guard = Some(waker);
        }
    }

    pub fn is_connected(&self) -> bool {
        self.client.is_some()
    }

    pub fn connect(&mut self, pipe_name: &str, timeout: Duration) -> Result<(), String> {
        let inbound = Arc::clone(&self.inbound);
        let waker = Arc::clone(&self.waker);
        let client = RpcClient::connect(pipe_name, timeout, move |ev: HostEvent| {
            let envelope = match ev {
                HostEvent::Raw { event, data } => json!({
                    "version": 1,
                    "event": event,
                    "data": data,
                }),
                HostEvent::HidInput(p) => json!({
                    "exo": "mouse",
                    "event": "hid.input",
                    "data": {
                        "key": p.device,
                        "reportId": p.report_id,
                        "data": p.data,
                    },
                }),
                HostEvent::GameRunState(p) => json!({
                    "version": 1,
                    "event": "game.runState",
                    "data": {
                        "id": p.id,
                        "isRunning": p.is_running,
                        "canStop": p.can_stop,
                    },
                }),
                _ => return,
            };
            let is_mouse = envelope.get("exo").is_some();
            if let Ok(s) = serde_json::to_string(&envelope) {
                if let Ok(mut q) = inbound.lock() {
                    if is_mouse {
                        q.push_back(InboundHostMsg::Mouse(s));
                    } else {
                        q.push_back(InboundHostMsg::Rpc(s));
                    }
                }
                if let Ok(w) = waker.lock() {
                    if let Some(ref wk) = *w {
                        wk();
                    }
                }
            }
        })
        .map_err(|e| e.to_string())?;
        self.client = Some(client);
        Ok(())
    }

    pub fn handle_webview_post(&mut self, msg_json: &str) {
        let Ok(val) = serde_json::from_str::<Value>(msg_json) else {
            return;
        };
        let id = val.get("id").cloned().unwrap_or(Value::Null);
        let Some(method) = val.get("method").and_then(Value::as_str) else {
            return;
        };
        let params = val.get("params").cloned().unwrap_or_else(|| json!({}));
        self.calls_sent += 1;

        let Some(ref client) = self.client else {
            let err = json!({
                "version": 1,
                "id": id,
                "ok": false,
                "error": "No host pipe connected",
            });
            if let Ok(s) = serde_json::to_string(&err) {
                if let Ok(mut q) = self.inbound.lock() {
                    q.push_back(InboundHostMsg::Rpc(s));
                }
            }
            return;
        };

        let inbound = Arc::clone(&self.inbound);
        let waker = Arc::clone(&self.waker);
        client.call_raw(method, &params, move |res| {
            let envelope = match res {
                Ok(result) => json!({
                    "version": 1,
                    "id": id,
                    "ok": true,
                    "result": result,
                }),
                Err(err) => json!({
                    "version": 1,
                    "id": id,
                    "ok": false,
                    "error": err.to_string(),
                }),
            };
            if let Ok(s) = serde_json::to_string(&envelope) {
                if let Ok(mut q) = inbound.lock() {
                    q.push_back(InboundHostMsg::Rpc(s));
                }
                if let Ok(w) = waker.lock() {
                    if let Some(ref wk) = *w {
                        wk();
                    }
                }
            }
        });
    }

    pub fn handle_mouse_post(&mut self, msg_json: &str) {
        let Ok(val) = serde_json::from_str::<Value>(msg_json) else {
            return;
        };
        if val.get("exo").and_then(Value::as_str) != Some("mouse") {
            return;
        }
        let Some(id) = val.get("id").and_then(Value::as_u64) else {
            return;
        };
        let Some(method) = val.get("method").and_then(Value::as_str) else {
            return;
        };
        let params = val.get("params").cloned().unwrap_or_else(|| json!({}));

        match method {
            "exo.textScale" => {
                self.push_mouse_reply(id, Ok(json!({ "ok": true, "data": "1" })));
                return;
            }
            "exo.motion" => {
                self.push_mouse_reply(id, Ok(json!({ "ok": true, "data": "full" })));
                return;
            }
            "exo.theme" => {
                self.push_mouse_reply(
                    id,
                    Ok(json!({
                        "ok": true,
                        "data": "void",
                        "look": {
                            "--bg": "#000000",
                            "--ink": "#ffffff",
                            "--muted": "#b4b4b4",
                            "--faint": "#9a9a9a",
                            "--good": "#3dd68c",
                            "--bad": "#ff5c5c",
                            "--primary-ink": "#000000",
                            "--track": "rgb(255 255 255 / 0.17)",
                            "--hair": "rgb(255 255 255 / 0.055)",
                            "--hover-fill": "rgb(255 255 255 / 0.1)",
                            "--pressed": "rgb(255 255 255 / 0.133)",
                            "--selected": "rgb(255 255 255 / 0.15)"
                        }
                    })),
                );
                return;
            }
            _ => {}
        }

        let rpc_method = match method {
            "hid.list" => Some("hid.list"),
            "hid.open" => Some("hid.open"),
            "hid.close" => Some("hid.close"),
            "hid.sendReport" => Some("hid.sendReport"),
            "hid.sendFeatureReport" => Some("hid.sendFeatureReport"),
            "hid.receiveFeatureReport" => Some("hid.receiveFeatureReport"),
            "hid.receiveInputReport" => Some("hid.receiveInputReport"),
            "exo.profiles.read" => Some("mouse.profiles.get"),
            "exo.profiles.write" => Some("mouse.profiles.set"),
            "exo.presets.read" => Some("mouse.presets.get"),
            "exo.presets.write" => Some("mouse.presets.set"),
            "exo.mouseCore" => Some("system.mouseCore"),
            "exo.mouseDiag.status" => Some("mouseDiag.status"),
            "exo.mouseDiag.measure" => Some("mouseDiag.measure"),
            "exo.mouseDiag.cancel" => Some("mouseDiag.cancel"),
            "exo.mouseDiag.fix" => Some("mouseDiag.fix"),
            _ => None,
        };

        let (Some(client), Some(host_method)) = (&self.client, rpc_method) else {
            let fallback = match method {
                "hid.list" => json!({ "ok": true, "devices": [] }),
                "exo.games" => json!({ "ok": true, "games": [] }),
                "exo.profiles.read" | "exo.presets.read" => json!({ "ok": true, "data": "{}" }),
                _ => json!({ "ok": true }),
            };
            self.push_mouse_reply(id, Ok(fallback));
            return;
        };

        self.calls_sent += 1;
        let inbound = Arc::clone(&self.inbound);
        let waker = Arc::clone(&self.waker);
        client.call_raw(host_method, &params, move |res| {
            let msg = match res {
                Ok(result) => json!({ "exo": "mouse", "id": id, "result": result }),
                Err(err) => json!({ "exo": "mouse", "id": id, "error": err.to_string() }),
            };
            if let Ok(s) = serde_json::to_string(&msg) {
                if let Ok(mut q) = inbound.lock() {
                    q.push_back(InboundHostMsg::Mouse(s));
                }
                if let Ok(w) = waker.lock() {
                    if let Some(ref wk) = *w {
                        wk();
                    }
                }
            }
        });
    }

    fn push_mouse_reply(&self, id: u64, res: Result<Value, String>) {
        let msg = match res {
            Ok(result) => json!({ "exo": "mouse", "id": id, "result": result }),
            Err(err) => json!({ "exo": "mouse", "id": id, "error": err }),
        };
        if let Ok(s) = serde_json::to_string(&msg) {
            if let Ok(mut q) = self.inbound.lock() {
                q.push_back(InboundHostMsg::Mouse(s));
            }
        }
    }

    pub fn drain_inbound(&mut self) -> Vec<InboundHostMsg> {
        let mut out = Vec::new();
        if let Ok(mut q) = self.inbound.lock() {
            while let Some(m) = q.pop_front() {
                self.events_received += 1;
                out.push(m);
            }
        }
        out
    }
}
