use crate::canvas2d::{Canvas2dOp, CanvasBacking, CanvasWidget};
use crate::waapi::{WaapiEngine, WaapiSpec};
use crate::webgl_wgpu::{WebGlTelemetry, WebGlWgpuContext};
use anyrender::ImageRenderer;
use anyrender_vello_cpu::VelloCpuImageRenderer;
use blitz_dom::{BaseDocument, DocumentConfig, LocalName, NodeId, QualName, ns};
use blitz_html::HtmlDocument;
use blitz_paint::paint_scene;
use blitz_traits::shell::{ColorScheme, Viewport};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

pub const VIEW_W: u32 = 1280;
pub const VIEW_H: u32 = 800;

#[derive(Clone, Debug, Default, Serialize)]
pub struct BridgeStats {
    pub dom_ops: u64,
    pub elements_created: u64,
    pub text_nodes_created: u64,
    pub style_sheets_injected: u64,
    pub layout_queries: u64,
    pub computed_style_queries: u64,
    pub selector_queries: u64,
    pub canvas2d_flushes: u64,
    pub waapi_animations: u64,
    pub webgl: WebGlTelemetry,
}

#[derive(Deserialize)]
#[serde(tag = "op", rename_all = "camelCase")]
pub enum DomMutationOp {
    CreateElement {
        id: u64,
        tag: String,
        ns: Option<String>,
    },
    CreateText {
        id: u64,
        text: String,
    },
    CreateComment {
        id: u64,
        text: String,
    },
    SetText {
        id: u64,
        text: String,
    },
    SetAttr {
        id: u64,
        name: String,
        value: String,
    },
    RemoveAttr {
        id: u64,
        name: String,
    },
    SetStyleProp {
        id: u64,
        name: String,
        value: String,
    },
    RemoveStyleProp {
        id: u64,
        name: String,
    },
    AppendChild {
        parent: u64,
        child: u64,
    },
    InsertBefore {
        parent: u64,
        child: u64,
        anchor: u64,
    },
    RemoveChild {
        parent: u64,
        child: u64,
    },
    SetInnerHtml {
        id: u64,
        html: String,
    },
    AddStyleText {
        id: u64,
        css: String,
    },
    Focus {
        id: u64,
    },
    Blur,
    CanvasSize {
        id: u64,
        width: u32,
        height: u32,
    },
    Canvas2dBatch {
        id: u64,
        ops: Vec<Canvas2dOp>,
    },
    WaapiStart {
        spec: WaapiSpec,
    },
    WaapiCancel {
        anim_id: u64,
    },
}

pub struct BlitzDomHost {
    pub doc: HtmlDocument,
    pub renderer: VelloCpuImageRenderer,
    pub js_to_blitz: HashMap<u64, NodeId>,
    pub blitz_to_js: HashMap<NodeId, u64>,
    pub canvases: HashMap<u64, Arc<Mutex<CanvasBacking>>>,
    pub webgl: WebGlWgpuContext,
    pub waapi: WaapiEngine,
    pub stats: BridgeStats,
    pub dirty_layout: bool,
    pub dirty_paint: bool,
    pub anim_time_s: f64,
}

impl BlitzDomHost {
    pub fn new(initial_css: &str) -> Self {
        let escaped_css = initial_css.replace("</style>", "<\\/style>");
        let html = format!(
            "<!DOCTYPE html><html class=\"dark\"><head><meta charset=\"utf-8\"/><style>{escaped_css}</style></head><body class=\"dark bg-black text-white\"><div id=\"root\"></div></body></html>"
        );
        let config = DocumentConfig {
            viewport: Some(Viewport::new(VIEW_W, VIEW_H, 1.0, ColorScheme::Dark)),
            ..Default::default()
        };
        let mut doc = HtmlDocument::from_html(&html, config);
        doc.resolve(0.0);

        let mut js_to_blitz = HashMap::new();
        let mut blitz_to_js = HashMap::new();

        if let Some(html_id) = doc.query_selector("html").ok().flatten() {
            js_to_blitz.insert(1, html_id);
            blitz_to_js.insert(html_id, 1);
        }
        if let Some(head_id) = doc.query_selector("head").ok().flatten() {
            js_to_blitz.insert(2, head_id);
            blitz_to_js.insert(head_id, 2);
        }
        if let Some(body_id) = doc.query_selector("body").ok().flatten() {
            js_to_blitz.insert(3, body_id);
            blitz_to_js.insert(body_id, 3);
        }
        if let Some(root_id) = doc.get_element_by_id("root") {
            js_to_blitz.insert(4, root_id);
            blitz_to_js.insert(root_id, 4);
        }

        let mut stats = BridgeStats::default();
        if !initial_css.is_empty() {
            stats.style_sheets_injected = 1;
        }

        Self {
            doc,
            renderer: VelloCpuImageRenderer::new(VIEW_W, VIEW_H),
            js_to_blitz,
            blitz_to_js,
            canvases: HashMap::new(),
            webgl: WebGlWgpuContext::default(),
            waapi: WaapiEngine::default(),
            stats,
            dirty_layout: true,
            dirty_paint: true,
            anim_time_s: 0.0,
        }
    }

    pub fn apply_batch_json(&mut self, json_str: &str) -> Result<usize, String> {
        let ops: Vec<DomMutationOp> =
            serde_json::from_str(json_str).map_err(|e| format!("batch parse: {e}"))?;
        let count = ops.len();
        if count == 0 {
            return Ok(0);
        }

        let mut style_nodes_to_process: Vec<NodeId> = Vec::new();
        let mut new_canvases: Vec<(u64, NodeId)> = Vec::new();
        let mut canvas_sizes: Vec<(u64, u32, u32)> = Vec::new();
        let mut canvas_batches: Vec<(u64, Vec<Canvas2dOp>)> = Vec::new();
        let mut focus_target: Option<Option<NodeId>> = None;

        {
            let mut mutr = self.doc.mutate();
            for op in ops {
                self.stats.dom_ops += 1;
                match op {
                    DomMutationOp::CreateElement {
                        id,
                        tag,
                        ns: ns_uri,
                    } => {
                        let lower = tag.to_ascii_lowercase();
                        let qname = if ns_uri.as_deref().is_some_and(|u: &str| u.contains("svg"))
                            || lower == "svg"
                        {
                            QualName::new(None, ns!(svg), LocalName::from(tag.as_str()))
                        } else {
                            QualName::new(None, ns!(html), LocalName::from(lower.as_str()))
                        };
                        let nid = mutr.create_element(qname, Vec::new());
                        self.js_to_blitz.insert(id, nid);
                        self.blitz_to_js.insert(nid, id);
                        self.stats.elements_created += 1;
                        if lower == "canvas" {
                            new_canvases.push((id, nid));
                        }
                    }
                    DomMutationOp::CreateText { id, text } => {
                        let nid = mutr.create_text_node(&text);
                        self.js_to_blitz.insert(id, nid);
                        self.blitz_to_js.insert(nid, id);
                        self.stats.text_nodes_created += 1;
                    }
                    DomMutationOp::CreateComment { id, text } => {
                        let nid = mutr.create_comment_node(&text);
                        self.js_to_blitz.insert(id, nid);
                        self.blitz_to_js.insert(nid, id);
                    }
                    DomMutationOp::SetText { id, text } => {
                        if let Some(&nid) = self.js_to_blitz.get(&id) {
                            mutr.set_node_text(nid, &text);
                            if mutr
                                .element_name(nid)
                                .is_some_and(|q| q.local.to_string().eq_ignore_ascii_case("style"))
                            {
                                style_nodes_to_process.push(nid);
                            } else if let Some(pid) = mutr.parent_id(nid) {
                                if mutr.element_name(pid).is_some_and(|q| {
                                    q.local.to_string().eq_ignore_ascii_case("style")
                                }) {
                                    style_nodes_to_process.push(pid);
                                }
                            }
                        }
                    }
                    DomMutationOp::SetAttr { id, name, value } => {
                        if let Some(&nid) = self.js_to_blitz.get(&id) {
                            let qname = QualName::new(None, ns!(), LocalName::from(name.as_str()));
                            mutr.set_attribute(nid, qname, &value);
                        }
                    }
                    DomMutationOp::RemoveAttr { id, name } => {
                        if let Some(&nid) = self.js_to_blitz.get(&id) {
                            let qname = QualName::new(None, ns!(), LocalName::from(name.as_str()));
                            mutr.clear_attribute(nid, qname);
                        }
                    }
                    DomMutationOp::SetStyleProp { id, name, value } => {
                        if let Some(&nid) = self.js_to_blitz.get(&id) {
                            mutr.set_style_property(nid, &name, &value);
                        }
                    }
                    DomMutationOp::RemoveStyleProp { id, name } => {
                        if let Some(&nid) = self.js_to_blitz.get(&id) {
                            mutr.remove_style_property(nid, &name);
                        }
                    }
                    DomMutationOp::AppendChild { parent, child } => {
                        if let (Some(&pid), Some(&cid)) =
                            (self.js_to_blitz.get(&parent), self.js_to_blitz.get(&child))
                        {
                            if mutr.node_has_parent(cid) {
                                mutr.remove_node(cid);
                            }
                            mutr.append_children(pid, &[cid]);
                            if mutr
                                .element_name(pid)
                                .is_some_and(|q| q.local.to_string().eq_ignore_ascii_case("style"))
                            {
                                style_nodes_to_process.push(pid);
                            } else if mutr
                                .element_name(cid)
                                .is_some_and(|q| q.local.to_string().eq_ignore_ascii_case("style"))
                            {
                                style_nodes_to_process.push(cid);
                            }
                        }
                    }
                    DomMutationOp::InsertBefore {
                        parent,
                        child,
                        anchor,
                    } => {
                        if let (Some(&cid), Some(&aid)) =
                            (self.js_to_blitz.get(&child), self.js_to_blitz.get(&anchor))
                        {
                            if mutr.node_has_parent(cid) {
                                mutr.remove_node(cid);
                            }
                            if mutr.parent_id(aid).is_some() {
                                mutr.insert_nodes_before(aid, &[cid]);
                            } else if let Some(&pid) = self.js_to_blitz.get(&parent) {
                                mutr.append_children(pid, &[cid]);
                            }
                        }
                    }
                    DomMutationOp::RemoveChild { parent: _, child } => {
                        if let Some(&cid) = self.js_to_blitz.get(&child) {
                            if mutr.node_has_parent(cid) {
                                mutr.remove_node(cid);
                            }
                        }
                    }
                    DomMutationOp::SetInnerHtml { id, html } => {
                        if let Some(&nid) = self.js_to_blitz.get(&id) {
                            mutr.set_inner_html(nid, &html);
                        }
                    }
                    DomMutationOp::AddStyleText { id, css } => {
                        let style_nid = if let Some(&nid) = self.js_to_blitz.get(&id) {
                            nid
                        } else {
                            let q = QualName::new(None, ns!(html), LocalName::from("style"));
                            let nid = mutr.create_element(q, Vec::new());
                            self.js_to_blitz.insert(id, nid);
                            self.blitz_to_js.insert(nid, id);
                            if let Some(&head_id) = self.js_to_blitz.get(&2) {
                                mutr.append_children(head_id, &[nid]);
                            }
                            nid
                        };
                        mutr.remove_and_drop_all_children(style_nid);
                        let tn = mutr.create_text_node(&css);
                        mutr.append_children(style_nid, &[tn]);
                        style_nodes_to_process.push(style_nid);
                        self.stats.style_sheets_injected += 1;
                    }
                    DomMutationOp::Focus { id } => {
                        if let Some(&nid) = self.js_to_blitz.get(&id) {
                            focus_target = Some(Some(nid));
                        }
                    }
                    DomMutationOp::Blur => {
                        focus_target = Some(None);
                    }
                    DomMutationOp::CanvasSize { id, width, height } => {
                        canvas_sizes.push((id, width, height));
                    }
                    DomMutationOp::Canvas2dBatch { id, ops: cops } => {
                        canvas_batches.push((id, cops));
                    }
                    DomMutationOp::WaapiStart { spec } => {
                        self.waapi.add(spec);
                        self.stats.waapi_animations = self.waapi.total_created;
                    }
                    DomMutationOp::WaapiCancel { anim_id } => {
                        self.waapi.cancel(anim_id);
                    }
                }
            }
            mutr.flush();
        }

        for (id, width, height) in canvas_sizes {
            let backing = self.ensure_canvas_backing(id);
            if let Ok(mut b) = backing.lock() {
                b.width = width.max(1);
                b.height = height.max(1);
                b.dirty = true;
            }
        }
        for (id, cops) in canvas_batches {
            let backing = self.ensure_canvas_backing(id);
            if let Ok(mut b) = backing.lock() {
                b.ops = cops;
                b.rgba_frame = None;
                b.dirty = true;
                b.draw_calls += 1;
            }
            self.stats.canvas2d_flushes += 1;
        }

        for (js_id, nid) in new_canvases {
            let backing = self.ensure_canvas_backing(js_id);
            self.doc
                .set_custom_widget(nid, Box::new(CanvasWidget::new(backing)));
        }

        for snid in style_nodes_to_process {
            self.doc.process_style_element(snid);
        }

        if let Some(ft) = focus_target {
            match ft {
                Some(nid) => {
                    self.doc.set_focus_to(nid);
                }
                None => {
                    self.doc.clear_focus();
                }
            }
        }

        self.dirty_layout = true;
        self.dirty_paint = true;
        Ok(count)
    }

    fn ensure_canvas_backing(&mut self, js_id: u64) -> Arc<Mutex<CanvasBacking>> {
        self.canvases
            .entry(js_id)
            .or_insert_with(|| {
                Arc::new(Mutex::new(CanvasBacking {
                    width: 300,
                    height: 150,
                    ..Default::default()
                }))
            })
            .clone()
    }

    pub fn ensure_resolved(&mut self) {
        if self.dirty_layout {
            self.doc.resolve(self.anim_time_s);
            self.dirty_layout = false;
        }
    }

    pub fn tick_animations(&mut self, now_ms: f64) -> bool {
        self.anim_time_s = now_ms / 1000.0;
        let updates = self.waapi.tick(now_ms);
        let mut changed = !updates.is_empty();
        if !updates.is_empty() {
            let mut mutr = self.doc.mutate();
            for (js_id, prop, val) in updates {
                if let Some(&nid) = self.js_to_blitz.get(&js_id) {
                    mutr.set_style_property(nid, &prop, &val);
                }
            }
            mutr.flush();
        }
        let canvas_dirty = self
            .canvases
            .values()
            .any(|b| b.lock().map(|g| g.dirty).unwrap_or(false));
        if canvas_dirty {
            changed = true;
        }
        if changed {
            self.dirty_layout = true;
            self.dirty_paint = true;
        }
        self.doc.resolve(self.anim_time_s);
        self.dirty_layout = false;
        changed || self.dirty_paint
    }

    pub fn query_sync(&mut self, req_json: &str) -> String {
        let Ok(v) = serde_json::from_str::<Value>(req_json) else {
            return "null".to_string();
        };
        let op = v.get("op").and_then(Value::as_str).unwrap_or("");
        match op {
            "getRect" => {
                self.ensure_resolved();
                self.stats.layout_queries += 1;
                let id = v.get("id").and_then(Value::as_u64).unwrap_or(0);
                if let Some(&nid) = self.js_to_blitz.get(&id) {
                    if let Some(r) = self.doc.get_client_bounding_rect(nid) {
                        return json!({
                            "x": r.x,
                            "y": r.y,
                            "width": r.width,
                            "height": r.height,
                            "top": r.y,
                            "left": r.x,
                            "right": r.x + r.width,
                            "bottom": r.y + r.height
                        })
                        .to_string();
                    }
                }
                json!({"x":0,"y":0,"width":0,"height":0,"top":0,"left":0,"right":0,"bottom":0})
                    .to_string()
            }
            "getComputedStyle" => {
                self.ensure_resolved();
                self.stats.computed_style_queries += 1;
                let id = v.get("id").and_then(Value::as_u64).unwrap_or(0);
                let prop = v.get("prop").and_then(Value::as_str).unwrap_or("");
                if let Some(&nid) = self.js_to_blitz.get(&id) {
                    let val = self.doc.resolved_style_value(nid, prop);
                    return json!(val).to_string();
                }
                "\"\"".to_string()
            }
            "querySelector" => {
                self.stats.selector_queries += 1;
                let sel = v.get("selector").and_then(Value::as_str).unwrap_or("");
                let root = v.get("root").and_then(Value::as_u64);
                let found = if let Some(rid) = root {
                    self.js_to_blitz
                        .get(&rid)
                        .and_then(|&nid| self.doc.query_selector_in(nid, sel).ok().flatten())
                } else {
                    self.doc.query_selector(sel).ok().flatten()
                };
                if let Some(nid) = found {
                    if let Some(&js_id) = self.blitz_to_js.get(&nid) {
                        return js_id.to_string();
                    }
                }
                "null".to_string()
            }
            "querySelectorAll" => {
                self.stats.selector_queries += 1;
                let sel = v.get("selector").and_then(Value::as_str).unwrap_or("");
                let root = v.get("root").and_then(Value::as_u64);
                let list = if let Some(rid) = root {
                    self.js_to_blitz
                        .get(&rid)
                        .and_then(|&nid| self.doc.query_selector_all_in(nid, sel).ok())
                        .unwrap_or_default()
                } else {
                    self.doc.query_selector_all(sel).unwrap_or_default()
                };
                let ids: Vec<u64> = list
                    .into_iter()
                    .filter_map(|nid| self.blitz_to_js.get(&nid).copied())
                    .collect();
                json!(ids).to_string()
            }
            "matches" => {
                self.stats.selector_queries += 1;
                let id = v.get("id").and_then(Value::as_u64).unwrap_or(0);
                let sel = v.get("selector").and_then(Value::as_str).unwrap_or("");
                let m = self
                    .js_to_blitz
                    .get(&id)
                    .and_then(|&nid| self.doc.matches_selector(nid, sel).ok())
                    .unwrap_or(false);
                if m { "true" } else { "false" }.to_string()
            }
            "closest" => {
                self.stats.selector_queries += 1;
                let id = v.get("id").and_then(Value::as_u64).unwrap_or(0);
                let sel = v.get("selector").and_then(Value::as_str).unwrap_or("");
                let c = self
                    .js_to_blitz
                    .get(&id)
                    .and_then(|&nid| self.doc.closest(nid, sel).ok().flatten())
                    .and_then(|nid| self.blitz_to_js.get(&nid).copied());
                match c {
                    Some(js_id) => js_id.to_string(),
                    None => "null".to_string(),
                }
            }
            "hitTest" => {
                self.ensure_resolved();
                let x = v.get("x").and_then(Value::as_f64).unwrap_or(0.0) as f32;
                let y = v.get("y").and_then(Value::as_f64).unwrap_or(0.0) as f32;
                let hit = self
                    .doc
                    .element_from_point(x, y)
                    .and_then(|nid| self.blitz_to_js.get(&nid).copied());
                match hit {
                    Some(js_id) => js_id.to_string(),
                    None => "null".to_string(),
                }
            }
            "cssSupports" => {
                let cond = v.get("cond").and_then(Value::as_str).unwrap_or("");
                let ok = self.doc.css_supports_condition(cond);
                if ok { "true" } else { "false" }.to_string()
            }
            "webgl" => self.handle_webgl_query(&v),
            _ => "null".to_string(),
        }
    }

    fn handle_webgl_query(&mut self, v: &Value) -> String {
        let cmd = v.get("cmd").and_then(Value::as_str).unwrap_or("");
        match cmd {
            "createShader" => {
                let ty = v.get("type").and_then(Value::as_u64).unwrap_or(0) as u32;
                self.webgl.create_shader(ty).to_string()
            }
            "shaderSource" => {
                let id = v.get("id").and_then(Value::as_u64).unwrap_or(0) as u32;
                let src = v.get("src").and_then(Value::as_str).unwrap_or("");
                self.webgl.shader_source(id, src);
                "true".to_string()
            }
            "compileShader" => {
                let id = v.get("id").and_then(Value::as_u64).unwrap_or(0) as u32;
                match self.webgl.compile_shader(id) {
                    Ok(()) => json!({"ok": true}).to_string(),
                    Err(e) => json!({"ok": false, "error": e}).to_string(),
                }
            }
            "createProgram" => self.webgl.create_program().to_string(),
            "attachShader" => {
                let p = v.get("program").and_then(Value::as_u64).unwrap_or(0) as u32;
                let s = v.get("shader").and_then(Value::as_u64).unwrap_or(0) as u32;
                self.webgl.attach_shader(p, s);
                "true".to_string()
            }
            "linkProgram" => {
                let p = v.get("program").and_then(Value::as_u64).unwrap_or(0) as u32;
                let ok = self.webgl.link_program(p);
                if ok { "true" } else { "false" }.to_string()
            }
            "useProgram" => {
                let p = v.get("program").and_then(Value::as_u64).unwrap_or(0) as u32;
                self.webgl.use_program(p);
                "true".to_string()
            }
            "uniform" => {
                let name = v.get("name").and_then(Value::as_str).unwrap_or("");
                let vals = v
                    .get("values")
                    .and_then(Value::as_array)
                    .map(|a| {
                        let mut out = [0.0f32; 4];
                        for (i, item) in a.iter().take(4).enumerate() {
                            out[i] = item.as_f64().unwrap_or(0.0) as f32;
                        }
                        out
                    })
                    .unwrap_or([0.0; 4]);
                self.webgl.set_uniform(name, vals);
                "true".to_string()
            }
            "viewport" => {
                let w = v.get("w").and_then(Value::as_u64).unwrap_or(640) as u32;
                let h = v.get("h").and_then(Value::as_u64).unwrap_or(360) as u32;
                self.webgl.viewport(w, h);
                "true".to_string()
            }
            "clearColor" => {
                let r = v.get("r").and_then(Value::as_f64).unwrap_or(0.0);
                let g = v.get("g").and_then(Value::as_f64).unwrap_or(0.0);
                let b = v.get("b").and_then(Value::as_f64).unwrap_or(0.0);
                let a = v.get("a").and_then(Value::as_f64).unwrap_or(1.0);
                self.webgl.clear_color(r, g, b, a);
                "true".to_string()
            }
            "draw" => {
                let canvas_id = v.get("canvasId").and_then(Value::as_u64).unwrap_or(0);
                let backing = self.ensure_canvas_backing(canvas_id);
                self.webgl.draw_into_backing(&backing);
                self.dirty_paint = true;
                "true".to_string()
            }
            "unsupported" => {
                let name = v.get("name").and_then(Value::as_str).unwrap_or("unknown");
                self.webgl.record_unsupported(name);
                "null".to_string()
            }
            _ => "null".to_string(),
        }
    }

    pub fn paint_rgba(&mut self) -> Vec<u8> {
        self.ensure_resolved();
        let mut rgba = Vec::new();
        self.renderer.reset();
        let doc_mut: &mut BaseDocument = &mut self.doc;
        self.renderer.render_to_vec(
            |scene| {
                paint_scene(scene, doc_mut, 1.0, VIEW_W, VIEW_H, 0, 0);
            },
            &mut rgba,
        );
        self.dirty_paint = false;
        rgba
    }

    pub fn snapshot_stats(&self) -> BridgeStats {
        let mut st = self.stats.clone();
        st.webgl = self.webgl.telemetry.clone();
        st
    }
}
