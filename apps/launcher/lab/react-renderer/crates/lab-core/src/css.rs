// SPDX-License-Identifier: MIT

use crate::dom::{
    AlignMode, ComputedStyle, DimVal, DisplayMode, DomArena, DomNode, FlexDir, GlassKind,
    JustifyMode, NodeId, OverflowMode, PosMode,
};
use exo_kit::srgb;
use lightningcss::properties::Property;
use lightningcss::rules::CssRule;
use lightningcss::stylesheet::{ParserFlags, ParserOptions, PrinterOptions, StyleSheet};
use lightningcss::traits::ToCss;
use rune::Color;
use std::collections::HashMap;

#[derive(Clone, Debug, Default)]
pub struct CssStats {
    pub rules_parsed: usize,
    pub declarations_parsed: usize,
    pub media_rules: usize,
    pub keyframes_skipped: usize,
    pub font_faces_skipped: usize,
    pub unknown_at_rules: usize,
    pub parse_ms: f64,
}

#[derive(Clone, Debug)]
struct SimpleSelector {
    tag: Option<String>,
    id: Option<String>,
    classes: Vec<String>,
    attrs: Vec<(String, Option<String>)>,
    pseudos: Vec<String>,
    not_selectors: Vec<SimpleSelector>,
    any_of: Vec<Vec<SimpleSelector>>,
    is_root: bool,
    universal: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Combinator {
    Descendant,
    Child,
    AdjacentSibling,
}

#[derive(Clone, Debug)]
struct SelectorChain {
    parts: Vec<(Option<Combinator>, SimpleSelector)>,
    specificity: (u16, u16, u16),
    has_pseudo_element: bool,
}

#[derive(Clone, Debug)]
struct StyleRule {
    selectors: Vec<SelectorChain>,
    decls: Vec<(String, String, bool)>,
    media_max_w: Option<f32>,
    media_min_w: Option<f32>,
    source_order: u32,
}

#[derive(Default)]
pub struct CssEngine {
    rules: Vec<StyleRule>,
    by_class: HashMap<String, Vec<usize>>,
    by_id: HashMap<String, Vec<usize>>,
    by_tag: HashMap<String, Vec<usize>>,
    universal_rules: Vec<usize>,
    root_vars: HashMap<String, String>,
    pub stats: CssStats,
}

impl CssEngine {
    fn index_rule(&mut self, idx: usize) {
        let mut classes = Vec::new();
        let mut ids = Vec::new();
        let mut tags = Vec::new();
        let mut is_univ = false;
        for sel in &self.rules[idx].selectors {
            if let Some((_, last)) = sel.parts.last() {
                if let Some(ref c) = last.classes.first() {
                    classes.push((*c).clone());
                } else if let Some(ref id) = last.id {
                    ids.push(id.clone());
                } else if let Some(ref t) = last.tag {
                    tags.push(t.clone());
                } else {
                    is_univ = true;
                }
            }
        }
        for c in classes {
            let v = self.by_class.entry(c).or_default();
            if v.last() != Some(&idx) {
                v.push(idx);
            }
        }
        for id in ids {
            let v = self.by_id.entry(id).or_default();
            if v.last() != Some(&idx) {
                v.push(idx);
            }
        }
        for t in tags {
            let v = self.by_tag.entry(t).or_default();
            if v.last() != Some(&idx) {
                v.push(idx);
            }
        }
        if is_univ && self.universal_rules.last() != Some(&idx) {
            self.universal_rules.push(idx);
        }
    }
    pub fn parse_stylesheet(&mut self, css_text: &str) {
        let t0 = std::time::Instant::now();
        let cleaned = preprocess_css(css_text);
        let mut opts = ParserOptions::default();
        opts.flags = ParserFlags::NESTING;
        opts.error_recovery = true;

        if let Ok(sheet) = StyleSheet::parse(&cleaned, opts) {
            let mut order = self.rules.len() as u32;
            self.collect_rules(&sheet.rules.0, None, None, &mut order);
        }
        self.stats.parse_ms += t0.elapsed().as_secs_f64() * 1000.0;
    }

    fn collect_rules(
        &mut self,
        rules: &[CssRule<'_>],
        media_max_w: Option<f32>,
        media_min_w: Option<f32>,
        order: &mut u32,
    ) {
        for rule in rules {
            match rule {
                CssRule::Style(sr) => {
                    self.stats.rules_parsed += 1;
                    let sel_str = sr
                        .selectors
                        .to_css_string(PrinterOptions::default())
                        .unwrap_or_default();
                    let selectors: Vec<SelectorChain> = sel_str
                        .split(',')
                        .filter_map(parse_selector_chain)
                        .filter(|s| !s.has_pseudo_element)
                        .collect();
                    let mut decls = Vec::new();
                    for prop in &sr.declarations.declarations {
                        if let Some((k, v)) = property_to_kv(prop) {
                            self.stats.declarations_parsed += 1;
                            decls.push((k, v, false));
                        }
                    }
                    for prop in &sr.declarations.important_declarations {
                        if let Some((k, v)) = property_to_kv(prop) {
                            self.stats.declarations_parsed += 1;
                            decls.push((k, v, true));
                        }
                    }
                    if selectors.iter().any(|s| {
                        s.parts.len() == 1 && s.parts[0].1.is_root && s.parts[0].1.attrs.is_empty()
                    }) && media_max_w.is_none()
                        && media_min_w.is_none()
                    {
                        for (k, v, _) in &decls {
                            if k.starts_with("--") {
                                self.root_vars.insert(k.clone(), v.clone());
                            }
                        }
                    }
                    if !selectors.is_empty() && !decls.is_empty() {
                        *order += 1;
                        let idx = self.rules.len();
                        self.rules.push(StyleRule {
                            selectors,
                            decls,
                            media_max_w,
                            media_min_w,
                            source_order: *order,
                        });
                        self.index_rule(idx);
                    }
                }
                CssRule::Media(mr) => {
                    self.stats.media_rules += 1;
                    let q = mr
                        .query
                        .to_css_string(PrinterOptions::default())
                        .unwrap_or_default();
                    if q.contains("forced-colors") || q.contains("prefers-reduced-motion: reduce") {
                        continue;
                    }
                    let max_w = extract_px_query(&q, "max-width").or(media_max_w);
                    let min_w = extract_px_query(&q, "min-width").or(media_min_w);
                    self.collect_rules(&mr.rules.0, max_w, min_w, order);
                }
                CssRule::Supports(sup) => {
                    self.collect_rules(&sup.rules.0, media_max_w, media_min_w, order);
                }
                CssRule::LayerBlock(lb) => {
                    self.collect_rules(&lb.rules.0, media_max_w, media_min_w, order);
                }
                CssRule::Keyframes(_) => {
                    self.stats.keyframes_skipped += 1;
                }
                CssRule::FontFace(_) => {
                    self.stats.font_faces_skipped += 1;
                }
                CssRule::Unknown(u) => {
                    self.stats.unknown_at_rules += 1;
                    if u.name.as_ref().eq_ignore_ascii_case("theme") {
                        let raw = u
                            .to_css_string(PrinterOptions::default())
                            .unwrap_or_default();
                        extract_custom_vars_from_block(&raw, &mut self.root_vars);
                    }
                }
                _ => {}
            }
        }
    }

    pub fn cascade(&self, arena: &mut DomArena, root_id: NodeId, viewport_w: f32) {
        let mut root_computed = ComputedStyle::default();
        root_computed.vars = self.root_vars.clone();
        root_computed
            .vars
            .entry("--exo-scale".into())
            .or_insert_with(|| "1".into());
        root_computed
            .vars
            .entry("--bg".into())
            .or_insert_with(|| "#000000".into());
        root_computed
            .vars
            .entry("--ink".into())
            .or_insert_with(|| "#ffffff".into());
        root_computed
            .vars
            .entry("--color-bg".into())
            .or_insert_with(|| "#000000".into());
        root_computed
            .vars
            .entry("--color-fg".into())
            .or_insert_with(|| "#ffffff".into());
        root_computed.background = Some(Color::BLACK);
        root_computed.color = Color::WHITE;
        root_computed.font_size = 13.0;
        self.cascade_node(arena, root_id, &root_computed, viewport_w);
        arena.style_dirty = false;
    }

    fn cascade_node(
        &self,
        arena: &mut DomArena,
        id: NodeId,
        parent_style: &ComputedStyle,
        viewport_w: f32,
    ) {
        let Some(node_snapshot) = arena.nodes.get(&id).cloned() else {
            return;
        };
        if node_snapshot.is_text {
            if let Some(n) = arena.nodes.get_mut(&id) {
                n.computed = parent_style.clone();
                n.computed.background = None;
                n.computed.border_widths = [0.0; 4];
                n.computed.border_color = None;
                n.computed.glass = GlassKind::None;
            }
            return;
        }

        let mut style = ComputedStyle::default();
        style.vars = parent_style.vars.clone();
        style.color = parent_style.color;
        style.font_size = parent_style.font_size;
        style.font_weight = parent_style.font_weight;
        style.line_height = parent_style.line_height;
        style.text_align_center = parent_style.text_align_center;
        style.text_align_right = parent_style.text_align_right;
        style.white_space_nowrap = parent_style.white_space_nowrap;
        style.cursor_pointer = parent_style.cursor_pointer;

        apply_ua_defaults(&node_snapshot, &mut style);

        let mut candidates: Vec<usize> = Vec::with_capacity(32);
        candidates.extend_from_slice(&self.universal_rules);
        if let Some(list) = self.by_tag.get(&node_snapshot.tag) {
            candidates.extend_from_slice(list);
        }
        if let Some(id_val) = node_snapshot.attr("id") {
            if let Some(list) = self.by_id.get(id_val) {
                candidates.extend_from_slice(list);
            }
        }
        for cls in &node_snapshot.classes {
            if let Some(list) = self.by_class.get(cls) {
                candidates.extend_from_slice(list);
            }
        }
        candidates.sort_unstable();
        candidates.dedup();

        let mut matched: Vec<((bool, u16, u16, u16, u32), &str, &str)> = Vec::new();
        for rule_idx in candidates {
            let rule = &self.rules[rule_idx];
            if let Some(max_w) = rule.media_max_w {
                if viewport_w > max_w {
                    continue;
                }
            }
            if let Some(min_w) = rule.media_min_w {
                if viewport_w < min_w {
                    continue;
                }
            }
            let mut best_spec: Option<(u16, u16, u16)> = None;
            for sel in &rule.selectors {
                if matches_chain(arena, &node_snapshot, sel) {
                    if best_spec.is_none_or(|prev| sel.specificity > prev) {
                        best_spec = Some(sel.specificity);
                    }
                }
            }
            if let Some((a, b, c)) = best_spec {
                for (k, v, imp) in &rule.decls {
                    matched.push(((*imp, a, b, c, rule.source_order), k.as_str(), v.as_str()));
                }
            }
        }
        matched.sort_by_key(|m| m.0);

        for (_, k, v) in &matched {
            if k.starts_with("--") {
                style.vars.insert((*k).to_string(), (*v).to_string());
            }
        }
        for (k, v) in &node_snapshot.inline_style {
            if k.starts_with("--") {
                style.vars.insert(k.clone(), v.clone());
            }
        }

        for (_, k, v) in matched {
            if !k.starts_with("--") {
                let resolved = resolve_vars(v, &style.vars, 0);
                style.raw_props.insert(k.to_string(), resolved.clone());
                apply_declaration(k, &resolved, &mut style, parent_style);
            }
        }
        for (k, v) in &node_snapshot.inline_style {
            if !k.starts_with("--") {
                let resolved = resolve_vars(v, &style.vars, 0);
                style.raw_props.insert(k.clone(), resolved.clone());
                apply_declaration(k, &resolved, &mut style, parent_style);
            }
        }

        if node_snapshot.is_hidden() {
            style.display = DisplayMode::None;
        }

        apply_exo_glass_mapping(&node_snapshot, &mut style);

        if let Some(n) = arena.nodes.get_mut(&id) {
            n.computed = style.clone();
        }
        for child_id in node_snapshot.children {
            self.cascade_node(arena, child_id, &style, viewport_w);
        }
    }
}

fn preprocess_css(css: &str) -> String {
    let mut out = String::with_capacity(css.len());
    for line in css.lines() {
        let t = line.trim_start();
        if t.starts_with("@import ") || t.starts_with("@custom-variant ") {
            continue;
        }
        out.push_str(line);
        out.push('\n');
    }
    out
}

fn extract_custom_vars_from_block(block: &str, out: &mut HashMap<String, String>) {
    let Some(start) = block.find('{') else {
        return;
    };
    let Some(end) = block.rfind('}') else {
        return;
    };
    for decl in block[start + 1..end].split(';') {
        if let Some((k, v)) = decl.split_once(':') {
            let k = k.trim();
            let v = v.trim();
            if k.starts_with("--") && !v.is_empty() {
                out.insert(k.to_string(), v.to_string());
            }
        }
    }
}

fn property_to_kv(prop: &Property<'_>) -> Option<(String, String)> {
    let name = prop.property_id().name().to_string();
    let val = prop.value_to_css_string(PrinterOptions::default()).ok()?;
    Some((name, val))
}

fn extract_px_query(q: &str, key: &str) -> Option<f32> {
    if let Some(idx) = q.find(key) {
        let rest = &q[idx + key.len()..];
        if let Some(colon) = rest.find(':') {
            let after = rest[colon + 1..].trim_start();
            if let Some(end) = after.find("px") {
                if let Ok(v) = after[..end].trim().parse::<f32>() {
                    return Some(v);
                }
            }
        }
    }
    let op = if key == "max-width" {
        "width <="
    } else if key == "min-width" {
        "width >="
    } else {
        return None;
    };
    if let Some(idx) = q.find(op) {
        let after = q[idx + op.len()..].trim_start();
        if let Some(end) = after.find("px") {
            if let Ok(v) = after[..end].trim().parse::<f32>() {
                return Some(v);
            }
        }
    }
    let lt_op = if key == "max-width" {
        "width <"
    } else if key == "min-width" {
        "width >"
    } else {
        return None;
    };
    if let Some(idx) = q.find(lt_op) {
        let after = q[idx + lt_op.len()..].trim_start();
        if let Some(end) = after.find("px") {
            if let Ok(v) = after[..end].trim().parse::<f32>() {
                return Some(v);
            }
        }
    }
    None
}

fn parse_selector_chain(raw: &str) -> Option<SelectorChain> {
    let s = raw.trim();
    if s.is_empty() {
        return None;
    }
    if s.contains("::before")
        || s.contains("::after")
        || s.contains("::-webkit-")
        || s.contains("::placeholder")
        || s.contains("::selection")
        || s.contains(":before")
        || s.contains(":after")
    {
        return Some(SelectorChain {
            parts: Vec::new(),
            specificity: (0, 0, 0),
            has_pseudo_element: true,
        });
    }

    let mut tokens: Vec<(Option<Combinator>, String)> = Vec::new();
    let mut cur = String::new();
    let mut pending_comb: Option<Combinator> = None;
    let mut paren_depth = 0usize;
    let mut bracket_depth = 0usize;

    for ch in s.chars() {
        match ch {
            '(' => {
                paren_depth += 1;
                cur.push(ch);
            }
            ')' => {
                paren_depth = paren_depth.saturating_sub(1);
                cur.push(ch);
            }
            '[' => {
                bracket_depth += 1;
                cur.push(ch);
            }
            ']' => {
                bracket_depth = bracket_depth.saturating_sub(1);
                cur.push(ch);
            }
            '>' if paren_depth == 0 && bracket_depth == 0 => {
                if !cur.trim().is_empty() {
                    tokens.push((pending_comb.take(), cur.trim().to_string()));
                    cur.clear();
                }
                pending_comb = Some(Combinator::Child);
            }
            '+' if paren_depth == 0 && bracket_depth == 0 => {
                if !cur.trim().is_empty() {
                    tokens.push((pending_comb.take(), cur.trim().to_string()));
                    cur.clear();
                }
                pending_comb = Some(Combinator::AdjacentSibling);
            }
            ' ' | '\t' | '\n' if paren_depth == 0 && bracket_depth == 0 => {
                if !cur.trim().is_empty() {
                    tokens.push((pending_comb.take(), cur.trim().to_string()));
                    cur.clear();
                    pending_comb = Some(Combinator::Descendant);
                }
            }
            _ => cur.push(ch),
        }
    }
    if !cur.trim().is_empty() {
        tokens.push((pending_comb.take(), cur.trim().to_string()));
    }

    let mut parts = Vec::new();
    let mut spec = (0u16, 0u16, 0u16);
    for (comb, part_str) in tokens {
        let simple = parse_simple_selector(&part_str);
        if simple.id.is_some() {
            spec.0 = spec.0.saturating_add(1);
        }
        spec.1 = spec.1.saturating_add(
            (simple.classes.len() + simple.attrs.len() + simple.pseudos.len()) as u16,
        );
        if simple.tag.is_some() {
            spec.2 = spec.2.saturating_add(1);
        }
        parts.push((comb, simple));
    }
    if parts.is_empty() {
        return None;
    }
    Some(SelectorChain {
        parts,
        specificity: spec,
        has_pseudo_element: false,
    })
}

fn parse_simple_selector(raw: &str) -> SimpleSelector {
    let mut out = SimpleSelector {
        tag: None,
        id: None,
        classes: Vec::new(),
        attrs: Vec::new(),
        pseudos: Vec::new(),
        not_selectors: Vec::new(),
        any_of: Vec::new(),
        is_root: false,
        universal: false,
    };
    let bytes = raw.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            b'*' => {
                out.universal = true;
                i += 1;
            }
            b'#' => {
                i += 1;
                let start = i;
                while i < bytes.len() && is_ident_byte(bytes[i]) {
                    i += 1;
                }
                out.id = Some(raw[start..i].to_string());
            }
            b'.' => {
                i += 1;
                let start = i;
                while i < bytes.len() && is_ident_byte(bytes[i]) {
                    i += 1;
                }
                if i > start {
                    out.classes.push(raw[start..i].to_string());
                }
            }
            b'[' => {
                i += 1;
                let start = i;
                while i < bytes.len() && bytes[i] != b']' {
                    i += 1;
                }
                let body = raw[start..i].trim();
                if i < bytes.len() {
                    i += 1;
                }
                if let Some((k, v)) = body.split_once('=') {
                    let k = k.trim().trim_end_matches(['*', '^', '$', '~', '|']);
                    let v = v.trim().trim_matches(['"', '\'']);
                    out.attrs.push((k.to_string(), Some(v.to_string())));
                } else if !body.is_empty() {
                    out.attrs.push((body.to_string(), None));
                }
            }
            b':' => {
                i += 1;
                if i < bytes.len() && bytes[i] == b':' {
                    i += 1;
                }
                let start = i;
                while i < bytes.len() && is_ident_byte(bytes[i]) {
                    i += 1;
                }
                let pseudo = raw[start..i].to_string();
                let mut arg = None;
                if i < bytes.len() && bytes[i] == b'(' {
                    i += 1;
                    let arg_start = i;
                    let mut depth = 1usize;
                    while i < bytes.len() && depth > 0 {
                        if bytes[i] == b'(' {
                            depth += 1;
                        } else if bytes[i] == b')' {
                            depth -= 1;
                        }
                        if depth > 0 {
                            i += 1;
                        }
                    }
                    arg = Some(raw[arg_start..i].to_string());
                    if i < bytes.len() {
                        i += 1;
                    }
                }
                if pseudo == "root" {
                    out.is_root = true;
                } else if pseudo == "not" {
                    if let Some(inner) = arg {
                        for item in inner.split(',') {
                            out.not_selectors.push(parse_simple_selector(item.trim()));
                        }
                    }
                } else if pseudo == "is" || pseudo == "where" {
                    if let Some(inner) = arg {
                        let list: Vec<SimpleSelector> = inner
                            .split(',')
                            .map(|item| {
                                let trimmed = item.trim();
                                let last_part =
                                    trimmed.split_whitespace().last().unwrap_or(trimmed);
                                parse_simple_selector(last_part)
                            })
                            .collect();
                        if !list.is_empty() {
                            out.any_of.push(list);
                        }
                    } else {
                        out.pseudos.push(pseudo);
                    }
                } else if !pseudo.is_empty() {
                    out.pseudos.push(pseudo);
                }
            }
            _ => {
                let start = i;
                while i < bytes.len() && is_ident_byte(bytes[i]) {
                    i += 1;
                }
                if i > start {
                    out.tag = Some(raw[start..i].to_ascii_lowercase());
                } else {
                    i += 1;
                }
            }
        }
    }
    out
}

fn is_ident_byte(b: u8) -> bool {
    b.is_ascii_alphanumeric() || b == b'-' || b == b'_'
}

fn matches_simple(arena: &DomArena, node: &DomNode, s: &SimpleSelector) -> bool {
    if s.is_root && node.tag != "html" {
        return false;
    }
    if let Some(ref tag) = s.tag {
        if &node.tag != tag {
            return false;
        }
    }
    if let Some(ref id) = s.id {
        if node.attr("id") != Some(id.as_str()) {
            return false;
        }
    }
    for cls in &s.classes {
        if !node.has_class(cls) {
            return false;
        }
    }
    for (k, val) in &s.attrs {
        match val {
            Some(expected) => {
                if node.attr(k) != Some(expected.as_str()) {
                    return false;
                }
            }
            None => {
                if node.attr(k).is_none() {
                    return false;
                }
            }
        }
    }
    for p in &s.pseudos {
        match p.as_str() {
            "hover" => {
                if !node.hovered {
                    return false;
                }
            }
            "active" => {
                if !node.pressed {
                    return false;
                }
            }
            "focus" | "focus-visible" | "focus-within" => {
                if !node.focused {
                    return false;
                }
            }
            "disabled" => {
                if !node.is_disabled() {
                    return false;
                }
            }
            "first-child" => {
                if let Some(pid) = node.parent {
                    if let Some(parent) = arena.nodes.get(&pid) {
                        let first = parent
                            .children
                            .iter()
                            .copied()
                            .find(|cid| arena.nodes.get(cid).is_some_and(|n| !n.is_text));
                        if first != Some(node.id) {
                            return false;
                        }
                    }
                }
            }
            "last-child" => {
                if let Some(pid) = node.parent {
                    if let Some(parent) = arena.nodes.get(&pid) {
                        let last = parent
                            .children
                            .iter()
                            .rev()
                            .copied()
                            .find(|cid| arena.nodes.get(cid).is_some_and(|n| !n.is_text));
                        if last != Some(node.id) {
                            return false;
                        }
                    }
                }
            }
            _ => return false,
        }
    }
    for group in &s.any_of {
        if !group.iter().any(|cand| matches_simple(arena, node, cand)) {
            return false;
        }
    }
    for neg in &s.not_selectors {
        if matches_simple(arena, node, neg) {
            return false;
        }
    }
    true
}

fn matches_chain(arena: &DomArena, node: &DomNode, chain: &SelectorChain) -> bool {
    if chain.parts.is_empty() {
        return false;
    }
    let last_idx = chain.parts.len() - 1;
    if !matches_simple(arena, node, &chain.parts[last_idx].1) {
        return false;
    }
    let mut cur_id = node.id;
    for idx in (0..last_idx).rev() {
        let comb = chain.parts[idx + 1].0.unwrap_or(Combinator::Descendant);
        let target_sel = &chain.parts[idx].1;
        match comb {
            Combinator::Child => {
                let Some(pid) = arena.nodes.get(&cur_id).and_then(|n| n.parent) else {
                    return false;
                };
                let Some(pnode) = arena.nodes.get(&pid) else {
                    return false;
                };
                if !matches_simple(arena, pnode, target_sel) {
                    return false;
                }
                cur_id = pid;
            }
            Combinator::Descendant => {
                let mut found = None;
                let mut walk = arena.nodes.get(&cur_id).and_then(|n| n.parent);
                while let Some(pid) = walk {
                    if let Some(pnode) = arena.nodes.get(&pid) {
                        if matches_simple(arena, pnode, target_sel) {
                            found = Some(pid);
                            break;
                        }
                        walk = pnode.parent;
                    } else {
                        break;
                    }
                }
                let Some(fid) = found else {
                    return false;
                };
                cur_id = fid;
            }
            Combinator::AdjacentSibling => {
                let Some(pid) = arena.nodes.get(&cur_id).and_then(|n| n.parent) else {
                    return false;
                };
                let Some(pnode) = arena.nodes.get(&pid) else {
                    return false;
                };
                let elems: Vec<NodeId> = pnode
                    .children
                    .iter()
                    .copied()
                    .filter(|cid| arena.nodes.get(cid).is_some_and(|n| !n.is_text))
                    .collect();
                let pos = elems.iter().position(|&x| x == cur_id);
                let Some(prev_id) = pos.and_then(|p| p.checked_sub(1)).map(|p| elems[p]) else {
                    return false;
                };
                let Some(prev_node) = arena.nodes.get(&prev_id) else {
                    return false;
                };
                if !matches_simple(arena, prev_node, target_sel) {
                    return false;
                }
                cur_id = prev_id;
            }
        }
    }
    true
}

fn resolve_vars(raw: &str, vars: &HashMap<String, String>, depth: u8) -> String {
    if depth > 8 || !raw.contains("var(") {
        return raw.to_string();
    }
    let mut out = String::with_capacity(raw.len());
    let mut rest = raw;
    while let Some(start) = rest.find("var(") {
        out.push_str(&rest[..start]);
        let after = &rest[start + 4..];
        let mut depth_p = 1usize;
        let mut end_idx = None;
        for (i, ch) in after.char_indices() {
            if ch == '(' {
                depth_p += 1;
            } else if ch == ')' {
                depth_p -= 1;
                if depth_p == 0 {
                    end_idx = Some(i);
                    break;
                }
            }
        }
        let Some(end) = end_idx else {
            out.push_str(&rest[start..]);
            return out;
        };
        let inner = &after[..end];
        let (var_name, fallback) = split_top_level_comma(inner);
        let key = var_name.trim();
        if let Some(val) = vars.get(key) {
            out.push_str(&resolve_vars(val, vars, depth + 1));
        } else if let Some(fb) = fallback {
            out.push_str(&resolve_vars(fb.trim(), vars, depth + 1));
        }
        rest = &after[end + 1..];
    }
    out.push_str(rest);
    out
}

fn split_top_level_comma(s: &str) -> (&str, Option<&str>) {
    let mut depth = 0usize;
    for (i, ch) in s.char_indices() {
        match ch {
            '(' => depth += 1,
            ')' => depth = depth.saturating_sub(1),
            ',' if depth == 0 => return (&s[..i], Some(&s[i + 1..])),
            _ => {}
        }
    }
    (s, None)
}

fn apply_ua_defaults(node: &DomNode, style: &mut ComputedStyle) {
    match node.tag.as_str() {
        "html" | "body" => {
            style.display = DisplayMode::Flex;
            style.flex_direction = FlexDir::Column;
            style.width = DimVal::Pct(1.0);
            style.height = DimVal::Pct(1.0);
            style.background = Some(Color::BLACK);
        }
        "div" | "main" | "section" | "header" | "footer" | "aside" | "nav" | "article" | "form"
        | "p" | "ul" | "ol" | "li" | "h1" | "h2" | "h3" | "h4" => {
            style.display = DisplayMode::Block;
        }
        "span" | "label" | "a" | "strong" | "em" | "code" | "small" => {
            style.display = DisplayMode::Inline;
        }
        "button" => {
            style.display = DisplayMode::InlineFlex;
            style.align_items = AlignMode::Center;
            style.justify_content = JustifyMode::Center;
            style.cursor_pointer = true;
            style.font_weight = 500.0;
        }
        "input" => {
            style.display = DisplayMode::InlineFlex;
            style.align_items = AlignMode::Center;
            if node.attr("type") == Some("range") {
                style.width = DimVal::Px(140.0);
                style.height = DimVal::Px(20.0);
                style.cursor_pointer = true;
            } else {
                style.height = DimVal::Px(32.0);
                style.min_width = DimVal::Px(120.0);
                style.padding = [0.0, 10.0, 0.0, 10.0];
                style.border_radius = 999.0;
                style.cursor_text = true;
            }
        }
        "svg" => {
            style.display = DisplayMode::InlineFlex;
            style.flex_shrink = 0.0;
            if let Some(w) = node.attr("width").and_then(|s| s.parse::<f32>().ok()) {
                style.width = DimVal::Px(w);
            } else {
                style.width = DimVal::Px(16.0);
            }
            if let Some(h) = node.attr("height").and_then(|s| s.parse::<f32>().ok()) {
                style.height = DimVal::Px(h);
            } else {
                style.height = DimVal::Px(16.0);
            }
        }
        "path" | "circle" | "line" | "rect" | "polyline" | "polygon" | "g" | "defs" | "style"
        | "script" => {
            style.display = DisplayMode::None;
        }
        _ => {}
    }
    if node.attr("id") == Some("root") {
        style.display = DisplayMode::Flex;
        style.flex_direction = FlexDir::Column;
        style.width = DimVal::Pct(1.0);
        style.height = DimVal::Pct(1.0);
    }
}

fn apply_exo_glass_mapping(node: &DomNode, style: &mut ComputedStyle) {
    let is_control = node.has_class("exo-set-btn")
        || node.has_class("m-button")
        || node.has_class("m-seg")
        || node.has_class("exo-glass-control")
        || node.has_class("exo-settings-search");
    let is_panel = node.has_class("exo-glass") || node.has_class("exo-set-list");
    if is_control && !node.has_class("is-primary") {
        style.glass = GlassKind::Control;
    } else if is_panel {
        style.glass = GlassKind::Panel;
    }
}

fn apply_declaration(k: &str, v: &str, s: &mut ComputedStyle, parent: &ComputedStyle) {
    let val = v.trim();
    if val.is_empty() {
        return;
    }
    match k {
        "display" => {
            s.display = match val {
                "none" => DisplayMode::None,
                "flex" => DisplayMode::Flex,
                "inline-flex" => DisplayMode::InlineFlex,
                "grid" => DisplayMode::Grid,
                "inline-grid" => DisplayMode::InlineGrid,
                "inline" => DisplayMode::Inline,
                "inline-block" => DisplayMode::InlineBlock,
                _ => DisplayMode::Block,
            };
        }
        "position" => {
            s.position = match val {
                "relative" => PosMode::Relative,
                "absolute" => PosMode::Absolute,
                "fixed" => PosMode::Fixed,
                "sticky" => PosMode::Sticky,
                _ => PosMode::Static,
            };
        }
        "flex-direction" => {
            s.flex_direction = match val {
                "column" => FlexDir::Column,
                "row-reverse" => FlexDir::RowReverse,
                "column-reverse" => FlexDir::ColumnReverse,
                _ => FlexDir::Row,
            };
        }
        "flex-wrap" => {
            s.flex_wrap = matches!(val, "wrap" | "wrap-reverse");
        }
        "flex-grow" => {
            if let Ok(g) = val.parse::<f32>() {
                s.flex_grow = g;
            }
        }
        "flex-shrink" => {
            if let Ok(sh) = val.parse::<f32>() {
                s.flex_shrink = sh;
            }
        }
        "flex-basis" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.flex_basis = d;
            }
        }
        "flex" => {
            if val == "none" {
                s.flex_grow = 0.0;
                s.flex_shrink = 0.0;
                s.flex_basis = DimVal::Auto;
            } else if val == "auto" {
                s.flex_grow = 1.0;
                s.flex_shrink = 1.0;
                s.flex_basis = DimVal::Auto;
            } else {
                let parts: Vec<&str> = val.split_whitespace().collect();
                if let Some(g) = parts.first().and_then(|p| p.parse::<f32>().ok()) {
                    s.flex_grow = g;
                    if parts.len() == 1 {
                        s.flex_shrink = 1.0;
                        s.flex_basis = DimVal::Px(0.0);
                    }
                }
                if let Some(sh) = parts.get(1).and_then(|p| p.parse::<f32>().ok()) {
                    s.flex_shrink = sh;
                }
                if let Some(b) = parts.get(2).and_then(|p| parse_dim(p, s.font_size)) {
                    s.flex_basis = b;
                }
            }
        }
        "align-items" => s.align_items = parse_align(val),
        "align-self" => s.align_self = parse_align(val),
        "justify-content" => s.justify_content = parse_justify(val),
        "place-items" => {
            let a = parse_align(val.split_whitespace().next().unwrap_or(val));
            s.align_items = a;
            s.justify_content = match a {
                AlignMode::Center => JustifyMode::Center,
                AlignMode::End => JustifyMode::End,
                _ => JustifyMode::Start,
            };
        }
        "grid-template-columns" => {
            s.grid_template_columns = Some(val.to_string());
        }
        "grid-template-rows" => {
            s.grid_template_rows = Some(val.to_string());
        }
        "grid-column" => {
            s.grid_column = Some(val.to_string());
        }
        "grid-row" => {
            s.grid_row = Some(val.to_string());
        }
        "order" => {
            if let Ok(o) = val.parse::<i32>() {
                s.order = o;
            }
        }
        "gap" => {
            let parts: Vec<&str> = val.split_whitespace().collect();
            if let Some(a) = parts.first().and_then(|p| parse_px(p, s.font_size)) {
                s.row_gap = a;
                s.col_gap = parts
                    .get(1)
                    .and_then(|p| parse_px(p, s.font_size))
                    .unwrap_or(a);
            }
        }
        "row-gap" => {
            if let Some(v) = parse_px(val, s.font_size) {
                s.row_gap = v;
            }
        }
        "column-gap" => {
            if let Some(v) = parse_px(val, s.font_size) {
                s.col_gap = v;
            }
        }
        "width" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.width = d;
            }
        }
        "height" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.height = d;
            }
        }
        "min-width" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.min_width = d;
            }
        }
        "min-height" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.min_height = d;
            }
        }
        "max-width" => {
            if val == "none" {
                s.max_width = DimVal::Auto;
            } else if let Some(d) = parse_dim(val, s.font_size) {
                s.max_width = d;
            }
        }
        "max-height" => {
            if val == "none" {
                s.max_height = DimVal::Auto;
            } else if let Some(d) = parse_dim(val, s.font_size) {
                s.max_height = d;
            }
        }
        "padding" => {
            let vals = parse_box_px(val, s.font_size);
            if let Some(p) = vals {
                s.padding = p;
            }
        }
        "padding-top" => {
            if let Some(v) = parse_px(val, s.font_size) {
                s.padding[0] = v;
            }
        }
        "padding-right" => {
            if let Some(v) = parse_px(val, s.font_size) {
                s.padding[1] = v;
            }
        }
        "padding-bottom" => {
            if let Some(v) = parse_px(val, s.font_size) {
                s.padding[2] = v;
            }
        }
        "padding-left" => {
            if let Some(v) = parse_px(val, s.font_size) {
                s.padding[3] = v;
            }
        }
        "padding-inline" => {
            if let Some(v) = parse_px(val, s.font_size) {
                s.padding[1] = v;
                s.padding[3] = v;
            }
        }
        "padding-block" => {
            if let Some(v) = parse_px(val, s.font_size) {
                s.padding[0] = v;
                s.padding[2] = v;
            }
        }
        "margin" => {
            if let Some(m) = parse_box_dim(val, s.font_size) {
                s.margin = m;
            }
        }
        "margin-top" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.margin[0] = d;
            }
        }
        "margin-right" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.margin[1] = d;
            }
        }
        "margin-bottom" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.margin[2] = d;
            }
        }
        "margin-left" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.margin[3] = d;
            }
        }
        "top" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.inset[0] = d;
            }
        }
        "right" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.inset[1] = d;
            }
        }
        "bottom" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.inset[2] = d;
            }
        }
        "left" => {
            if let Some(d) = parse_dim(val, s.font_size) {
                s.inset[3] = d;
            }
        }
        "inset" => {
            if let Some(b) = parse_box_dim(val, s.font_size) {
                s.inset = b;
            }
        }
        "background" | "background-color" => {
            if val == "none" || val == "transparent" {
                s.background = None;
            } else if let Some(c) = parse_color(val, s.color) {
                s.background = Some(c);
            }
        }
        "color" => {
            if val == "inherit" {
                s.color = parent.color;
            } else if let Some(c) = parse_color(val, parent.color) {
                s.color = c;
            }
        }
        "border" => {
            if val == "none" || val == "0" {
                s.border_widths = [0.0; 4];
                s.border_color = None;
            } else {
                let (w, col) = parse_border_shorthand(val, s.font_size, s.color);
                if let Some(w) = w {
                    s.border_widths = [w; 4];
                }
                if let Some(c) = col {
                    s.border_color = Some(c);
                }
            }
        }
        "border-top" => {
            let (w, col) = parse_border_shorthand(val, s.font_size, s.color);
            if let Some(w) = w {
                s.border_widths[0] = w;
            }
            if let Some(c) = col {
                s.border_color = Some(c);
            }
        }
        "border-right" => {
            let (w, col) = parse_border_shorthand(val, s.font_size, s.color);
            if let Some(w) = w {
                s.border_widths[1] = w;
            }
            if let Some(c) = col {
                s.border_color = Some(c);
            }
        }
        "border-bottom" => {
            let (w, col) = parse_border_shorthand(val, s.font_size, s.color);
            if let Some(w) = w {
                s.border_widths[2] = w;
            }
            if let Some(c) = col {
                s.border_color = Some(c);
            }
        }
        "border-left" => {
            let (w, col) = parse_border_shorthand(val, s.font_size, s.color);
            if let Some(w) = w {
                s.border_widths[3] = w;
            }
            if let Some(c) = col {
                s.border_color = Some(c);
            }
        }
        "border-color" => {
            if let Some(c) = parse_color(val, s.color) {
                s.border_color = Some(c);
            }
        }
        "border-radius" => {
            if let Some(r) = val
                .split_whitespace()
                .next()
                .and_then(|p| parse_px(p, s.font_size))
            {
                s.border_radius = r;
            } else if val.contains("50%") {
                s.border_radius = 999.0;
            }
        }
        "box-shadow" => {
            if val.contains("inset") && s.border_widths == [0.0; 4] {
                if let Some(c) = find_color_in_str(val, s.color) {
                    s.border_widths = [1.0; 4];
                    s.border_color = Some(c);
                }
            }
        }
        "font-size" => {
            if val == "inherit" {
                s.font_size = parent.font_size;
            } else if let Some(px) = parse_px(val, parent.font_size) {
                s.font_size = px.clamp(8.0, 64.0);
            }
        }
        "font-weight" => {
            s.font_weight = match val {
                "normal" => 400.0,
                "medium" => 500.0,
                "semibold" => 600.0,
                "bold" => 700.0,
                _ => val
                    .parse::<f32>()
                    .unwrap_or(s.font_weight)
                    .clamp(100.0, 900.0),
            };
        }
        "line-height" => {
            if val == "normal" {
                s.line_height = 1.25;
            } else if let Ok(n) = val.parse::<f32>() {
                s.line_height = n.clamp(0.9, 2.5);
            } else if let Some(px) = parse_px(val, s.font_size) {
                s.line_height = (px / s.font_size.max(8.0)).clamp(0.9, 2.5);
            }
        }
        "letter-spacing" => {
            if let Some(px) = parse_px(val, s.font_size) {
                s.letter_spacing = px;
            }
        }
        "text-align" => {
            s.text_align_center = val == "center";
            s.text_align_right = val == "right" || val == "end";
        }
        "white-space" => {
            s.white_space_nowrap = matches!(val, "nowrap" | "pre");
        }
        "opacity" => {
            if let Ok(o) = val.parse::<f32>() {
                s.opacity = o.clamp(0.0, 1.0);
            }
        }
        "overflow" => {
            let m = parse_overflow(val);
            s.overflow_x = m;
            s.overflow_y = m;
        }
        "overflow-x" => s.overflow_x = parse_overflow(val),
        "overflow-y" => s.overflow_y = parse_overflow(val),
        "z-index" => {
            if let Ok(z) = val.parse::<i16>() {
                s.z_index = z;
            }
        }
        "cursor" => {
            s.cursor_pointer = val == "pointer";
            s.cursor_text = val == "text";
        }
        "pointer-events" => {
            s.pointer_events_none = val == "none";
        }
        "backdrop-filter" | "-webkit-backdrop-filter" => {
            if let Some(i) = val.find("blur(") {
                let after = &val[i + 5..];
                if let Some(end) = after.find(')') {
                    if let Some(px) = parse_px(&after[..end], s.font_size) {
                        s.backdrop_blur = px;
                    }
                }
            }
        }
        _ => {}
    }
}

fn parse_align(v: &str) -> AlignMode {
    match v {
        "center" => AlignMode::Center,
        "flex-start" | "start" => AlignMode::Start,
        "flex-end" | "end" => AlignMode::End,
        "stretch" => AlignMode::Stretch,
        "baseline" => AlignMode::Baseline,
        _ => AlignMode::Auto,
    }
}

fn parse_justify(v: &str) -> JustifyMode {
    match v {
        "center" => JustifyMode::Center,
        "flex-end" | "end" => JustifyMode::End,
        "space-between" => JustifyMode::Between,
        "space-around" => JustifyMode::Around,
        "space-evenly" => JustifyMode::Evenly,
        _ => JustifyMode::Start,
    }
}

fn parse_overflow(v: &str) -> OverflowMode {
    match v {
        "hidden" | "clip" => OverflowMode::Hidden,
        "scroll" => OverflowMode::Scroll,
        "auto" => OverflowMode::Auto,
        _ => OverflowMode::Visible,
    }
}

pub fn parse_dim(raw: &str, font_size: f32) -> Option<DimVal> {
    let s = raw.trim();
    if s == "auto" {
        return Some(DimVal::Auto);
    }
    if s.ends_with('%') && !s.starts_with("calc(") {
        return s[..s.len() - 1]
            .trim()
            .parse::<f32>()
            .ok()
            .map(|p| DimVal::Pct(p / 100.0));
    }
    if s == "100vw" || s == "100vh" || s == "100dvh" {
        return Some(DimVal::Pct(1.0));
    }
    parse_px(s, font_size).map(DimVal::Px)
}

pub fn parse_px(raw: &str, font_size: f32) -> Option<f32> {
    let s = raw.trim();
    if s == "0" {
        return Some(0.0);
    }
    if let Some(inner) = s.strip_prefix("calc(").and_then(|x| x.strip_suffix(')')) {
        return eval_calc(inner, font_size);
    }
    if let Some(inner) = s.strip_prefix("min(").and_then(|x| x.strip_suffix(')')) {
        let mut best: Option<f32> = None;
        for part in inner.split(',') {
            if let Some(v) = parse_px(part.trim(), font_size) {
                best = Some(best.map_or(v, |b| b.min(v)));
            }
        }
        return best;
    }
    if let Some(inner) = s.strip_prefix("max(").and_then(|x| x.strip_suffix(')')) {
        let mut best: Option<f32> = None;
        for part in inner.split(',') {
            if let Some(v) = parse_px(part.trim(), font_size) {
                best = Some(best.map_or(v, |b| b.max(v)));
            }
        }
        return best;
    }
    if let Some(num) = s.strip_suffix("px") {
        return num.trim().parse::<f32>().ok();
    }
    if let Some(num) = s.strip_suffix("rem") {
        return num.trim().parse::<f32>().ok().map(|n| n * 16.0);
    }
    if let Some(num) = s.strip_suffix("em") {
        return num.trim().parse::<f32>().ok().map(|n| n * font_size);
    }
    s.parse::<f32>().ok()
}

fn eval_calc(expr: &str, font_size: f32) -> Option<f32> {
    let tokens = tokenize_calc(expr, font_size)?;
    let mut pos = 0;
    let val = parse_calc_add(&tokens, &mut pos)?;
    Some(val)
}

#[derive(Clone, Copy, Debug)]
enum CalcTok {
    Num(f32),
    Plus,
    Minus,
    Mul,
    Div,
    LParen,
    RParen,
}

fn tokenize_calc(expr: &str, font_size: f32) -> Option<Vec<CalcTok>> {
    let mut toks = Vec::new();
    let bytes = expr.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            b' ' | b'\t' | b'\n' => i += 1,
            b'(' => {
                toks.push(CalcTok::LParen);
                i += 1;
            }
            b')' => {
                toks.push(CalcTok::RParen);
                i += 1;
            }
            b'+' => {
                toks.push(CalcTok::Plus);
                i += 1;
            }
            b'*' => {
                toks.push(CalcTok::Mul);
                i += 1;
            }
            b'/' => {
                toks.push(CalcTok::Div);
                i += 1;
            }
            b'-' if !matches!(
                toks.last(),
                None | Some(
                    CalcTok::Plus | CalcTok::Minus | CalcTok::Mul | CalcTok::Div | CalcTok::LParen
                )
            ) =>
            {
                toks.push(CalcTok::Minus);
                i += 1;
            }
            _ => {
                let start = i;
                if bytes[i] == b'-' {
                    i += 1;
                }
                while i < bytes.len() && (bytes[i].is_ascii_digit() || bytes[i] == b'.') {
                    i += 1;
                }
                let num_end = i;
                while i < bytes.len() && (bytes[i].is_ascii_alphabetic() || bytes[i] == b'%') {
                    i += 1;
                }
                if num_end == start {
                    return None;
                }
                let n = expr[start..num_end].parse::<f32>().ok()?;
                let unit = &expr[num_end..i];
                let scaled = match unit {
                    "" | "px" => n,
                    "rem" => n * 16.0,
                    "em" => n * font_size,
                    "%" => return None,
                    _ => n,
                };
                toks.push(CalcTok::Num(scaled));
            }
        }
    }
    Some(toks)
}

fn parse_calc_add(toks: &[CalcTok], pos: &mut usize) -> Option<f32> {
    let mut left = parse_calc_mul(toks, pos)?;
    while let Some(t) = toks.get(*pos) {
        match t {
            CalcTok::Plus => {
                *pos += 1;
                left += parse_calc_mul(toks, pos)?;
            }
            CalcTok::Minus => {
                *pos += 1;
                left -= parse_calc_mul(toks, pos)?;
            }
            _ => break,
        }
    }
    Some(left)
}

fn parse_calc_mul(toks: &[CalcTok], pos: &mut usize) -> Option<f32> {
    let mut left = parse_calc_atom(toks, pos)?;
    while let Some(t) = toks.get(*pos) {
        match t {
            CalcTok::Mul => {
                *pos += 1;
                left *= parse_calc_atom(toks, pos)?;
            }
            CalcTok::Div => {
                *pos += 1;
                let r = parse_calc_atom(toks, pos)?;
                if r.abs() < 1e-6 {
                    return None;
                }
                left /= r;
            }
            _ => break,
        }
    }
    Some(left)
}

fn parse_calc_atom(toks: &[CalcTok], pos: &mut usize) -> Option<f32> {
    match toks.get(*pos)? {
        CalcTok::Num(v) => {
            *pos += 1;
            Some(*v)
        }
        CalcTok::LParen => {
            *pos += 1;
            let v = parse_calc_add(toks, pos)?;
            if matches!(toks.get(*pos), Some(CalcTok::RParen)) {
                *pos += 1;
            }
            Some(v)
        }
        _ => None,
    }
}

fn split_css_values(s: &str) -> Vec<&str> {
    let mut out = Vec::new();
    let mut start = None;
    let mut depth = 0usize;
    for (i, ch) in s.char_indices() {
        match ch {
            '(' => {
                if start.is_none() {
                    start = Some(i);
                }
                depth += 1;
            }
            ')' => depth = depth.saturating_sub(1),
            ' ' | '\t' if depth == 0 => {
                if let Some(st) = start.take() {
                    out.push(&s[st..i]);
                }
            }
            _ => {
                if start.is_none() {
                    start = Some(i);
                }
            }
        }
    }
    if let Some(st) = start {
        out.push(&s[st..]);
    }
    out
}

fn parse_box_px(raw: &str, font_size: f32) -> Option<[f32; 4]> {
    let parts: Vec<f32> = split_css_values(raw)
        .into_iter()
        .filter_map(|p| parse_px(p, font_size))
        .collect();
    match parts.as_slice() {
        [a] => Some([*a, *a, *a, *a]),
        [v, h] => Some([*v, *h, *v, *h]),
        [t, h, b] => Some([*t, *h, *b, *h]),
        [t, r, b, l] => Some([*t, *r, *b, *l]),
        _ => None,
    }
}

fn parse_box_dim(raw: &str, font_size: f32) -> Option<[DimVal; 4]> {
    let parts: Vec<DimVal> = split_css_values(raw)
        .into_iter()
        .filter_map(|p| parse_dim(p, font_size))
        .collect();
    match parts.as_slice() {
        [a] => Some([*a, *a, *a, *a]),
        [v, h] => Some([*v, *h, *v, *h]),
        [t, h, b] => Some([*t, *h, *b, *h]),
        [t, r, b, l] => Some([*t, *r, *b, *l]),
        _ => None,
    }
}

fn parse_border_shorthand(
    raw: &str,
    font_size: f32,
    current: Color,
) -> (Option<f32>, Option<Color>) {
    let mut w = None;
    let mut c = None;
    for part in split_css_values(raw) {
        if matches!(part, "solid" | "dashed" | "dotted" | "double") {
            continue;
        }
        if part == "none" {
            return (Some(0.0), None);
        }
        if c.is_none() {
            if let Some(col) = parse_color(part, current) {
                c = Some(col);
                continue;
            }
        }
        if w.is_none() {
            if let Some(px) = parse_px(part, font_size) {
                w = Some(px);
            }
        }
    }
    (w, c)
}

fn find_color_in_str(raw: &str, current: Color) -> Option<Color> {
    for part in split_css_values(raw) {
        let p = part.trim_end_matches(',');
        if let Some(c) = parse_color(p, current) {
            return Some(c);
        }
    }
    None
}

pub fn parse_color(raw: &str, current: Color) -> Option<Color> {
    let s = raw.trim();
    if s.eq_ignore_ascii_case("transparent") {
        return Some(Color::TRANSPARENT);
    }
    if s.eq_ignore_ascii_case("currentcolor") {
        return Some(current);
    }
    if s.eq_ignore_ascii_case("black") {
        return Some(Color::BLACK);
    }
    if s.eq_ignore_ascii_case("white") {
        return Some(Color::WHITE);
    }
    if let Some(hex_str) = s.strip_prefix('#') {
        return match hex_str.len() {
            3 => {
                let r = u8::from_str_radix(&hex_str[0..1], 16).ok()? * 17;
                let g = u8::from_str_radix(&hex_str[1..2], 16).ok()? * 17;
                let b = u8::from_str_radix(&hex_str[2..3], 16).ok()? * 17;
                Some(srgb(r, g, b, 1.0))
            }
            4 => {
                let r = u8::from_str_radix(&hex_str[0..1], 16).ok()? * 17;
                let g = u8::from_str_radix(&hex_str[1..2], 16).ok()? * 17;
                let b = u8::from_str_radix(&hex_str[2..3], 16).ok()? * 17;
                let a = (u8::from_str_radix(&hex_str[3..4], 16).ok()? * 17) as f32 / 255.0;
                Some(srgb(r, g, b, a))
            }
            6 => {
                let r = u8::from_str_radix(&hex_str[0..2], 16).ok()?;
                let g = u8::from_str_radix(&hex_str[2..4], 16).ok()?;
                let b = u8::from_str_radix(&hex_str[4..6], 16).ok()?;
                Some(srgb(r, g, b, 1.0))
            }
            8 => {
                let r = u8::from_str_radix(&hex_str[0..2], 16).ok()?;
                let g = u8::from_str_radix(&hex_str[2..4], 16).ok()?;
                let b = u8::from_str_radix(&hex_str[4..6], 16).ok()?;
                let a = u8::from_str_radix(&hex_str[6..8], 16).ok()? as f32 / 255.0;
                Some(srgb(r, g, b, a))
            }
            _ => None,
        };
    }
    if let Some(inner) = s
        .strip_prefix("rgb(")
        .or_else(|| s.strip_prefix("rgba("))
        .and_then(|x| x.strip_suffix(')'))
    {
        let normalized = inner.replace('/', " ").replace(',', " ");
        let nums: Vec<&str> = normalized.split_whitespace().collect();
        if nums.len() >= 3 {
            let r = parse_channel(nums[0])?;
            let g = parse_channel(nums[1])?;
            let b = parse_channel(nums[2])?;
            let a = nums.get(3).and_then(|x| parse_alpha(x)).unwrap_or(1.0);
            return Some(srgb(r, g, b, a));
        }
    }
    None
}

fn parse_channel(s: &str) -> Option<u8> {
    if let Some(p) = s.strip_suffix('%') {
        return p
            .parse::<f32>()
            .ok()
            .map(|v| (v * 2.55).round().clamp(0.0, 255.0) as u8);
    }
    s.parse::<f32>()
        .ok()
        .map(|v| v.round().clamp(0.0, 255.0) as u8)
}

fn parse_alpha(s: &str) -> Option<f32> {
    if let Some(p) = s.strip_suffix('%') {
        return p.parse::<f32>().ok().map(|v| (v / 100.0).clamp(0.0, 1.0));
    }
    s.parse::<f32>().ok().map(|v| v.clamp(0.0, 1.0))
}
