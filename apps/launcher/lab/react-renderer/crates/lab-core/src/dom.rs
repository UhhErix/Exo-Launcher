// SPDX-License-Identifier: MIT

use rune::Color;
use serde::Deserialize;
use std::collections::HashMap;

pub type NodeId = u32;

pub const HTML_ID: NodeId = 1;
pub const BODY_ID: NodeId = 2;
pub const ROOT_ID: NodeId = 3;

#[derive(Clone, Copy, Debug, Default)]
pub struct LayoutRect {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
    pub local_x: f32,
    pub local_y: f32,
    pub content_height: f32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum DisplayMode {
    #[default]
    Block,
    Inline,
    InlineBlock,
    Flex,
    InlineFlex,
    Grid,
    InlineGrid,
    None,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum FlexDir {
    #[default]
    Row,
    Column,
    RowReverse,
    ColumnReverse,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum PosMode {
    #[default]
    Static,
    Relative,
    Absolute,
    Fixed,
    Sticky,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum OverflowMode {
    #[default]
    Visible,
    Hidden,
    Scroll,
    Auto,
}

#[derive(Clone, Copy, Debug, PartialEq, Default)]
pub enum DimVal {
    #[default]
    Auto,
    Px(f32),
    Pct(f32),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum AlignMode {
    #[default]
    Auto,
    Start,
    Center,
    End,
    Stretch,
    Baseline,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum JustifyMode {
    #[default]
    Start,
    Center,
    End,
    Between,
    Around,
    Evenly,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum GlassKind {
    #[default]
    None,
    Control,
    Panel,
    Bar,
}

#[derive(Clone, Debug)]
pub struct ComputedStyle {
    pub display: DisplayMode,
    pub position: PosMode,
    pub flex_direction: FlexDir,
    pub flex_wrap: bool,
    pub flex_grow: f32,
    pub flex_shrink: f32,
    pub flex_basis: DimVal,
    pub align_items: AlignMode,
    pub align_self: AlignMode,
    pub justify_content: JustifyMode,
    pub grid_template_columns: Option<String>,
    pub grid_template_rows: Option<String>,
    pub grid_column: Option<String>,
    pub grid_row: Option<String>,
    pub order: i32,
    pub row_gap: f32,
    pub col_gap: f32,
    pub width: DimVal,
    pub height: DimVal,
    pub min_width: DimVal,
    pub min_height: DimVal,
    pub max_width: DimVal,
    pub max_height: DimVal,
    pub padding: [f32; 4],
    pub margin: [DimVal; 4],
    pub inset: [DimVal; 4],
    pub border_widths: [f32; 4],
    pub border_color: Option<Color>,
    pub background: Option<Color>,
    pub color: Color,
    pub font_size: f32,
    pub font_weight: f32,
    pub line_height: f32,
    pub letter_spacing: f32,
    pub text_align_center: bool,
    pub text_align_right: bool,
    pub white_space_nowrap: bool,
    pub border_radius: f32,
    pub opacity: f32,
    pub overflow_x: OverflowMode,
    pub overflow_y: OverflowMode,
    pub z_index: i16,
    pub cursor_pointer: bool,
    pub cursor_text: bool,
    pub pointer_events_none: bool,
    pub glass: GlassKind,
    pub backdrop_blur: f32,
    pub vars: HashMap<String, String>,
    pub raw_props: HashMap<String, String>,
}

impl Default for ComputedStyle {
    fn default() -> Self {
        Self {
            display: DisplayMode::Block,
            position: PosMode::Static,
            flex_direction: FlexDir::Row,
            flex_wrap: false,
            flex_grow: 0.0,
            flex_shrink: 1.0,
            flex_basis: DimVal::Auto,
            align_items: AlignMode::Stretch,
            align_self: AlignMode::Auto,
            justify_content: JustifyMode::Start,
            grid_template_columns: None,
            grid_template_rows: None,
            grid_column: None,
            grid_row: None,
            order: 0,
            row_gap: 0.0,
            col_gap: 0.0,
            width: DimVal::Auto,
            height: DimVal::Auto,
            min_width: DimVal::Auto,
            min_height: DimVal::Auto,
            max_width: DimVal::Auto,
            max_height: DimVal::Auto,
            padding: [0.0; 4],
            margin: [DimVal::Px(0.0); 4],
            inset: [DimVal::Auto; 4],
            border_widths: [0.0; 4],
            border_color: None,
            background: None,
            color: Color::WHITE,
            font_size: 13.0,
            font_weight: 400.0,
            line_height: 1.35,
            letter_spacing: 0.0,
            text_align_center: false,
            text_align_right: false,
            white_space_nowrap: false,
            border_radius: 0.0,
            opacity: 1.0,
            overflow_x: OverflowMode::Visible,
            overflow_y: OverflowMode::Visible,
            z_index: 0,
            cursor_pointer: false,
            cursor_text: false,
            pointer_events_none: false,
            glass: GlassKind::None,
            backdrop_blur: 0.0,
            vars: HashMap::new(),
            raw_props: HashMap::new(),
        }
    }
}

#[derive(Clone, Debug)]
pub struct DomNode {
    pub id: NodeId,
    pub tag: String,
    pub is_text: bool,
    pub text: String,
    pub parent: Option<NodeId>,
    pub children: Vec<NodeId>,
    pub classes: Vec<String>,
    pub attrs: HashMap<String, String>,
    pub inline_style: HashMap<String, String>,
    pub events: Vec<String>,
    pub scroll_top: f32,
    pub scroll_left: f32,
    pub hovered: bool,
    pub pressed: bool,
    pub focused: bool,
    pub computed: ComputedStyle,
    pub rect: LayoutRect,
}

impl DomNode {
    pub fn element(id: NodeId, tag: impl Into<String>) -> Self {
        Self {
            id,
            tag: tag.into().to_ascii_lowercase(),
            is_text: false,
            text: String::new(),
            parent: None,
            children: Vec::new(),
            classes: Vec::new(),
            attrs: HashMap::new(),
            inline_style: HashMap::new(),
            events: Vec::new(),
            scroll_top: 0.0,
            scroll_left: 0.0,
            hovered: false,
            pressed: false,
            focused: false,
            computed: ComputedStyle::default(),
            rect: LayoutRect::default(),
        }
    }

    pub fn text(id: NodeId, text: impl Into<String>) -> Self {
        Self {
            id,
            tag: "#text".into(),
            is_text: true,
            text: text.into(),
            parent: None,
            children: Vec::new(),
            classes: Vec::new(),
            attrs: HashMap::new(),
            inline_style: HashMap::new(),
            events: Vec::new(),
            scroll_top: 0.0,
            scroll_left: 0.0,
            hovered: false,
            pressed: false,
            focused: false,
            computed: ComputedStyle::default(),
            rect: LayoutRect::default(),
        }
    }

    pub fn has_class(&self, cls: &str) -> bool {
        self.classes.iter().any(|c| c == cls)
    }

    pub fn attr(&self, key: &str) -> Option<&str> {
        self.attrs.get(key).map(String::as_str)
    }

    pub fn is_disabled(&self) -> bool {
        matches!(
            self.attr("disabled"),
            Some("true") | Some("") | Some("disabled")
        )
    }

    pub fn is_hidden(&self) -> bool {
        matches!(
            self.attr("hidden"),
            Some("true") | Some("") | Some("hidden")
        )
    }

    pub fn has_event(&self, name: &str) -> bool {
        self.events.iter().any(|e| e == name)
    }
}

#[derive(Debug, Deserialize)]
#[serde(tag = "op")]
pub enum MutationOp {
    #[serde(rename = "create")]
    Create {
        id: NodeId,
        tag: String,
        #[serde(default)]
        props: serde_json::Map<String, serde_json::Value>,
    },
    #[serde(rename = "createText")]
    CreateText { id: NodeId, text: String },
    #[serde(rename = "append")]
    Append { parent: NodeId, child: NodeId },
    #[serde(rename = "insertBefore")]
    InsertBefore {
        parent: NodeId,
        child: NodeId,
        before: NodeId,
    },
    #[serde(rename = "remove")]
    Remove { parent: NodeId, child: NodeId },
    #[serde(rename = "props")]
    Props {
        id: NodeId,
        props: serde_json::Map<String, serde_json::Value>,
    },
    #[serde(rename = "text")]
    Text { id: NodeId, text: String },
    #[serde(rename = "clear")]
    Clear { id: NodeId },
    #[serde(rename = "styleProp")]
    StyleProp {
        id: NodeId,
        name: String,
        value: Option<String>,
    },
    #[serde(rename = "className")]
    ClassName { id: NodeId, value: String },
    #[serde(rename = "attr")]
    Attr {
        id: NodeId,
        name: String,
        value: Option<String>,
    },
    #[serde(rename = "scroll")]
    Scroll { id: NodeId, top: f32, left: f32 },
}

pub struct DomArena {
    pub nodes: HashMap<NodeId, DomNode>,
    pub style_dirty: bool,
    pub layout_dirty: bool,
    pub commit_count: u64,
    pub total_ops: u64,
}

impl Default for DomArena {
    fn default() -> Self {
        Self::new()
    }
}

impl DomArena {
    pub fn new() -> Self {
        let mut nodes = HashMap::new();
        let mut html = DomNode::element(HTML_ID, "html");
        html.children.push(BODY_ID);
        let mut body = DomNode::element(BODY_ID, "body");
        body.parent = Some(HTML_ID);
        body.children.push(ROOT_ID);
        let mut root = DomNode::element(ROOT_ID, "div");
        root.parent = Some(BODY_ID);
        root.attrs.insert("id".into(), "root".into());
        nodes.insert(HTML_ID, html);
        nodes.insert(BODY_ID, body);
        nodes.insert(ROOT_ID, root);
        Self {
            nodes,
            style_dirty: true,
            layout_dirty: true,
            commit_count: 0,
            total_ops: 0,
        }
    }

    pub fn apply_ops(&mut self, ops: Vec<MutationOp>) {
        self.commit_count += 1;
        self.total_ops += ops.len() as u64;
        for op in ops {
            match op {
                MutationOp::Create { id, tag, props } => {
                    let mut node = DomNode::element(id, tag);
                    apply_props_to_node(&mut node, &props);
                    self.nodes.insert(id, node);
                    self.style_dirty = true;
                    self.layout_dirty = true;
                }
                MutationOp::CreateText { id, text } => {
                    self.nodes.insert(id, DomNode::text(id, text));
                    self.layout_dirty = true;
                }
                MutationOp::Append { parent, child } => {
                    self.detach(child);
                    if let Some(c) = self.nodes.get_mut(&child) {
                        c.parent = Some(parent);
                    }
                    if let Some(p) = self.nodes.get_mut(&parent) {
                        p.children.push(child);
                    }
                    self.style_dirty = true;
                    self.layout_dirty = true;
                }
                MutationOp::InsertBefore {
                    parent,
                    child,
                    before,
                } => {
                    self.detach(child);
                    if let Some(c) = self.nodes.get_mut(&child) {
                        c.parent = Some(parent);
                    }
                    if let Some(p) = self.nodes.get_mut(&parent) {
                        if let Some(pos) = p.children.iter().position(|&x| x == before) {
                            p.children.insert(pos, child);
                        } else {
                            p.children.push(child);
                        }
                    }
                    self.style_dirty = true;
                    self.layout_dirty = true;
                }
                MutationOp::Remove { parent, child } => {
                    if let Some(p) = self.nodes.get_mut(&parent) {
                        p.children.retain(|&x| x != child);
                    }
                    if let Some(c) = self.nodes.get_mut(&child) {
                        c.parent = None;
                    }
                    self.style_dirty = true;
                    self.layout_dirty = true;
                }
                MutationOp::Props { id, props } => {
                    if let Some(node) = self.nodes.get_mut(&id) {
                        apply_props_to_node(node, &props);
                        self.style_dirty = true;
                        self.layout_dirty = true;
                    }
                }
                MutationOp::Text { id, text } => {
                    if let Some(node) = self.nodes.get_mut(&id) {
                        node.text = text;
                        self.layout_dirty = true;
                    }
                }
                MutationOp::Clear { id } => {
                    let old_children = self
                        .nodes
                        .get_mut(&id)
                        .map(|n| std::mem::take(&mut n.children))
                        .unwrap_or_default();
                    for c in old_children {
                        if let Some(cn) = self.nodes.get_mut(&c) {
                            cn.parent = None;
                        }
                    }
                    self.style_dirty = true;
                    self.layout_dirty = true;
                }
                MutationOp::StyleProp { id, name, value } => {
                    if let Some(node) = self.nodes.get_mut(&id) {
                        match value {
                            Some(v) => {
                                node.inline_style.insert(name, v);
                            }
                            None => {
                                node.inline_style.remove(&name);
                            }
                        }
                        self.style_dirty = true;
                        self.layout_dirty = true;
                    }
                }
                MutationOp::ClassName { id, value } => {
                    if let Some(node) = self.nodes.get_mut(&id) {
                        node.classes = value.split_whitespace().map(str::to_string).collect();
                        node.attrs.insert("class".into(), value);
                        self.style_dirty = true;
                        self.layout_dirty = true;
                    }
                }
                MutationOp::Attr { id, name, value } => {
                    if let Some(node) = self.nodes.get_mut(&id) {
                        match value {
                            Some(v) => {
                                if name == "class" {
                                    node.classes =
                                        v.split_whitespace().map(str::to_string).collect();
                                }
                                node.attrs.insert(name, v);
                            }
                            None => {
                                if name == "class" {
                                    node.classes.clear();
                                }
                                node.attrs.remove(&name);
                            }
                        }
                        self.style_dirty = true;
                        self.layout_dirty = true;
                    }
                }
                MutationOp::Scroll { id, top, left } => {
                    if let Some(node) = self.nodes.get_mut(&id) {
                        node.scroll_top = top.max(0.0);
                        node.scroll_left = left.max(0.0);
                    }
                }
            }
        }
    }

    fn detach(&mut self, child: NodeId) {
        let old_parent = self.nodes.get(&child).and_then(|n| n.parent);
        if let Some(pid) = old_parent {
            if let Some(p) = self.nodes.get_mut(&pid) {
                p.children.retain(|&x| x != child);
            }
        }
    }

    pub fn node_count(&self) -> usize {
        self.nodes.len()
    }

    pub fn set_hover(&mut self, id: NodeId, hovered: bool) -> bool {
        if let Some(n) = self.nodes.get_mut(&id) {
            if n.hovered != hovered {
                n.hovered = hovered;
                self.style_dirty = true;
                return true;
            }
        }
        false
    }

    pub fn set_focused(&mut self, id: Option<NodeId>) {
        for n in self.nodes.values_mut() {
            let next = id == Some(n.id);
            if n.focused != next {
                n.focused = next;
                self.style_dirty = true;
            }
        }
    }

    pub fn focused_node(&self) -> Option<NodeId> {
        self.nodes.values().find(|n| n.focused).map(|n| n.id)
    }

    pub fn count_live_subtree(&self, id: NodeId) -> usize {
        let mut count = 1;
        if let Some(n) = self.nodes.get(&id) {
            for &c in &n.children {
                count += self.count_live_subtree(c);
            }
        }
        count
    }
}

fn apply_props_to_node(node: &mut DomNode, props: &serde_json::Map<String, serde_json::Value>) {
    for (k, v) in props {
        if k == "__events" {
            if let Some(arr) = v.as_array() {
                node.events = arr
                    .iter()
                    .filter_map(|x| x.as_str().map(str::to_string))
                    .collect();
            }
            continue;
        }
        if k == "className" || k == "class" {
            let s = v.as_str().unwrap_or("");
            node.classes = s.split_whitespace().map(str::to_string).collect();
            node.attrs.insert("class".into(), s.to_string());
            continue;
        }
        if k == "style" {
            node.inline_style.clear();
            if let Some(obj) = v.as_object() {
                for (sk, sv) in obj {
                    if let Some(s) = sv.as_str() {
                        node.inline_style.insert(sk.clone(), s.to_string());
                    }
                }
            }
            continue;
        }
        if v.is_null() {
            node.attrs.remove(k);
        } else if let Some(b) = v.as_bool() {
            if b {
                node.attrs.insert(k.clone(), "true".into());
            } else {
                node.attrs.remove(k);
            }
        } else if let Some(s) = v.as_str() {
            node.attrs.insert(k.clone(), s.to_string());
        } else {
            node.attrs.insert(k.clone(), v.to_string());
        }
    }
}
