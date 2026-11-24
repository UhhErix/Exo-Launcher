// SPDX-License-Identifier: MIT

use crate::LabState;
use crate::dom::{DomArena, DomNode, GlassKind, NodeId, OverflowMode};
use exo_kit::{ExoGlass, apply_glass, fg_muted, hover_fill, pressed_fill, track_fill};
use rune::{Color, Cursor, El, Phase, ScrollSpec, Shape, TextStyle, canvas, col, el, text};
use std::collections::HashMap;

pub fn build_rune_tree(arena: &DomArena, root_id: NodeId) -> El {
    let mut root = el().key("lab-root").w_full().h_full().fill(Color::BLACK);
    if let Some(child) = build_node(arena, root_id, 0.0, 0.0) {
        root = root.child(child);
    }
    root
}

fn build_node(arena: &DomArena, id: NodeId, offset_x: f32, offset_y: f32) -> Option<El> {
    let node = arena.nodes.get(&id)?;
    let r = node.rect;
    if r.width <= 0.01 && r.height <= 0.01 && node.children.is_empty() && !node.is_text {
        return None;
    }

    let local_x = r.x - offset_x;
    let local_y = r.y - offset_y;

    if node.is_text {
        let content = node.text.trim();
        if content.is_empty() {
            return None;
        }
        let mut ts = TextStyle::new(node.computed.font_size, node.computed.font_weight);
        if node.computed.letter_spacing.abs() > 0.001 && node.computed.font_size > 1.0 {
            let em = (node.computed.letter_spacing / node.computed.font_size).clamp(-0.08, 0.2);
            ts = ts.letter_spacing(em);
        }
        let mut t = text(content)
            .key(format!("t-{id}"))
            .font(ts)
            .color(node.computed.color)
            .absolute(local_x, local_y);
        if r.width > 4.0 {
            t = t.w(r.width + 2.0);
        }
        if node.computed.opacity < 0.999 {
            t = t.opacity(node.computed.opacity);
        }
        return Some(t);
    }

    if node.tag == "svg" {
        return Some(build_svg_el(arena, node, local_x, local_y));
    }

    if node.tag == "input" {
        if node.attr("type") == Some("range") {
            return Some(build_range_el(node, local_x, local_y));
        }
        return Some(build_input_el(node, local_x, local_y));
    }

    let is_scroll = matches!(
        node.computed.overflow_y,
        OverflowMode::Auto | OverflowMode::Scroll
    ) && r.content_height > r.height + 2.0;

    let mut box_el = if is_scroll {
        col()
            .key(format!("s-{id}"))
            .absolute(local_x, local_y)
            .size(r.width.max(1.0), r.height.max(1.0))
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
    } else {
        el().key(format!("n-{id}"))
            .absolute(local_x, local_y)
            .size(r.width.max(0.0), r.height.max(0.0))
    };

    if node.computed.border_radius > 0.0 {
        box_el = box_el.radius(node.computed.border_radius);
        if matches!(
            node.computed.overflow_x,
            OverflowMode::Hidden | OverflowMode::Auto | OverflowMode::Scroll
        ) || matches!(
            node.computed.overflow_y,
            OverflowMode::Hidden | OverflowMode::Auto | OverflowMode::Scroll
        ) {
            box_el = box_el.clip_rounded(node.computed.border_radius);
        }
    }

    if let Some(bg) = node.computed.background {
        if bg.a > 0.001 {
            box_el = box_el.fill(bg);
        }
    }

    let bw = node.computed.border_widths;
    if let Some(bc) = node.computed.border_color {
        if bc.a > 0.001 {
            if (bw[0] - bw[1]).abs() < 0.01
                && (bw[1] - bw[2]).abs() < 0.01
                && (bw[2] - bw[3]).abs() < 0.01
                && bw[0] > 0.01
            {
                box_el = box_el.border(bw[0], bc);
            } else if bw.iter().any(|&w| w > 0.01) {
                box_el = box_el.border_sides(bw, bc);
            }
        }
    }

    if node.computed.opacity < 0.999 {
        box_el = box_el.opacity(node.computed.opacity);
    }

    if node.computed.z_index != 0 {
        box_el = box_el.paint_order(node.computed.z_index);
    }

    let is_interactive = node.tag == "button"
        || node.tag == "a"
        || node.has_event("onClick")
        || node.has_event("onPointerDown")
        || node.has_event("onMouseDown")
        || node.attr("role") == Some("switch")
        || node.attr("role") == Some("radio")
        || node.attr("role") == Some("tab");

    match node.computed.glass {
        GlassKind::Control => {
            box_el = apply_glass(box_el, ExoGlass::Control, is_interactive);
        }
        GlassKind::Panel => {
            box_el = apply_glass(box_el, ExoGlass::Panel, false);
        }
        GlassKind::Bar => {
            box_el = apply_glass(box_el, ExoGlass::Bar, false);
        }
        GlassKind::None => {}
    }

    if node.computed.cursor_pointer || is_interactive {
        box_el = box_el.cursor(Cursor::Pointer);
    } else if node.computed.cursor_text {
        box_el = box_el.cursor(Cursor::Text);
    }

    if is_interactive && !node.is_disabled() && !node.computed.pointer_events_none {
        let nid = node.id;
        box_el = box_el
            .hover(hover_fill())
            .press(pressed_fill())
            .on_press_tx(move |state: &mut LabState, _tx| {
                state.dispatch_click(nid);
            })
            .on_hover_change(move |state: &mut LabState, inside, _tx| {
                state.dispatch_hover(nid, inside);
            });
    } else if node.has_event("onMouseEnter") || node.has_event("onMouseLeave") {
        let nid = node.id;
        box_el = box_el.on_hover_change(move |state: &mut LabState, inside, _tx| {
            state.dispatch_hover(nid, inside);
        });
    }

    let mut ordered: Vec<(i32, usize, NodeId)> = node
        .children
        .iter()
        .enumerate()
        .map(|(i, &cid)| {
            let ord = arena.nodes.get(&cid).map(|c| c.computed.order).unwrap_or(0);
            (ord, i, cid)
        })
        .collect();
    ordered.sort_by_key(|&(ord, i, _)| (ord, i));

    if is_scroll {
        let mut inner = el()
            .key(format!("sc-{id}"))
            .w(r.width.max(1.0))
            .h(r.content_height.max(r.height).max(1.0));
        for (_, _, cid) in ordered {
            if let Some(child_el) = build_node(arena, cid, r.x, r.y) {
                inner = inner.child(child_el);
            }
        }
        box_el = box_el.child(inner);
    } else {
        for (_, _, cid) in ordered {
            if let Some(child_el) = build_node(arena, cid, r.x, r.y) {
                box_el = box_el.child(child_el);
            }
        }
    }

    Some(box_el)
}

fn build_range_el(node: &DomNode, local_x: f32, local_y: f32) -> El {
    let r = node.rect;
    let min = node
        .attr("min")
        .and_then(|s| s.parse::<f32>().ok())
        .unwrap_or(0.0);
    let max = node
        .attr("max")
        .and_then(|s| s.parse::<f32>().ok())
        .unwrap_or(100.0);
    let step = node
        .attr("step")
        .and_then(|s| s.parse::<f32>().ok())
        .unwrap_or(1.0)
        .max(0.0001);
    let val = node
        .attr("value")
        .and_then(|s| s.parse::<f32>().ok())
        .unwrap_or(min);
    let frac = if (max - min).abs() > 1e-6 {
        ((val - min) / (max - min)).clamp(0.0, 1.0)
    } else {
        0.0
    };
    let w = r.width.max(80.0);
    let h = r.height.max(18.0);
    let track_h = 4.0;
    let track_y = (h - track_h) * 0.5;
    let thumb_d = 14.0;
    let thumb_x = (frac * (w - thumb_d)).clamp(0.0, (w - thumb_d).max(0.0));
    let nid = node.id;

    el().key(format!("rng-{nid}"))
        .absolute(local_x, local_y)
        .size(w, h)
        .cursor(Cursor::Pointer)
        .child(
            el().absolute(0.0, track_y)
                .size(w, track_h)
                .radius(999.0)
                .fill(track_fill()),
        )
        .child(
            el().absolute(0.0, track_y)
                .size((frac * w).max(2.0), track_h)
                .radius(999.0)
                .fill(Color::WHITE),
        )
        .child(
            el().absolute(thumb_x, (h - thumb_d) * 0.5)
                .size(thumb_d, thumb_d)
                .radius(999.0)
                .fill(Color::WHITE),
        )
        .on_drag(move |state: &mut LabState, drag, _tx| {
            if matches!(drag.phase, Phase::Down | Phase::Move | Phase::Up) && !drag.cancelled() {
                let p = (drag.x / drag.w.max(1.0)).clamp(0.0, 1.0);
                let raw = min + p * (max - min);
                let snapped = (min + ((raw - min) / step).round() * step).clamp(min, max);
                state.dispatch_input_value(nid, format!("{snapped}"));
            }
        })
}

fn build_input_el(node: &DomNode, local_x: f32, local_y: f32) -> El {
    let r = node.rect;
    let val = node.attr("value").unwrap_or("");
    let placeholder = node.attr("placeholder").unwrap_or("");
    let showing_placeholder = val.is_empty();
    let display = if showing_placeholder {
        placeholder
    } else {
        val
    };
    let text_color = if showing_placeholder {
        fg_muted()
    } else {
        node.computed.color
    };
    let nid = node.id;
    let border_c = if node.focused {
        Color::WHITE.alpha(0.35)
    } else {
        node.computed
            .border_color
            .unwrap_or_else(|| Color::WHITE.alpha(0.14))
    };

    let mut box_el = el()
        .key(format!("inp-{nid}"))
        .absolute(local_x, local_y)
        .size(r.width.max(40.0), r.height.max(26.0))
        .radius(node.computed.border_radius.max(8.0))
        .border(1.0, border_c)
        .cursor(Cursor::Text)
        .on_press_tx(move |state: &mut LabState, _tx| {
            state.focus_node(nid);
        });
    box_el = apply_glass(box_el, ExoGlass::Control, true);
    let pad_l = node.computed.padding[3].max(10.0);
    let ty = ((r.height.max(26.0) - node.computed.font_size * 1.25) * 0.5).max(0.0);
    if !display.is_empty() {
        box_el = box_el.child(
            text(display)
                .font(TextStyle::new(
                    node.computed.font_size,
                    node.computed.font_weight,
                ))
                .color(text_color)
                .absolute(pad_l, ty),
        );
    }
    box_el
}

fn build_svg_el(arena: &DomArena, node: &DomNode, local_x: f32, local_y: f32) -> El {
    let r = node.rect;
    let w = r.width.max(12.0);
    let h = r.height.max(12.0);
    let (vw, vh) = parse_view_box(node.attr("viewBox")).unwrap_or((24.0, 24.0));
    let color = node.computed.color;
    let mut shape = Shape::new().view_box(vw, vh);
    let stroke_w = node
        .attr("strokeWidth")
        .or_else(|| node.attr("stroke-width"))
        .and_then(|s| s.parse::<f32>().ok())
        .unwrap_or(1.8);

    for &cid in &node.children {
        if let Some(child) = arena.nodes.get(&cid) {
            add_svg_primitive(&mut shape, child, color, stroke_w);
        }
    }

    canvas(shape)
        .key(format!("svg-{}", node.id))
        .absolute(local_x, local_y)
        .size(w, h)
}

fn parse_view_box(vb: Option<&str>) -> Option<(f32, f32)> {
    let nums: Vec<f32> = vb?
        .split(|c: char| c.is_whitespace() || c == ',')
        .filter(|s| !s.is_empty())
        .filter_map(|s| s.parse::<f32>().ok())
        .collect();
    if nums.len() >= 4 {
        Some((nums[2].max(1.0), nums[3].max(1.0)))
    } else {
        None
    }
}

fn add_svg_primitive(shape: &mut Shape, node: &DomNode, color: Color, default_sw: f32) {
    let sw = node
        .attr("strokeWidth")
        .or_else(|| node.attr("stroke-width"))
        .and_then(|s| s.parse::<f32>().ok())
        .unwrap_or(default_sw);
    let stroke_op = node
        .attr("strokeOpacity")
        .or_else(|| node.attr("stroke-opacity"))
        .and_then(|s| s.parse::<f32>().ok())
        .unwrap_or(1.0);
    let c = color.alpha((color.a * stroke_op).clamp(0.0, 1.0));

    match node.tag.as_str() {
        "circle" => {
            let cx = node
                .attr("cx")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(12.0);
            let cy = node
                .attr("cy")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(12.0);
            let rad = node
                .attr("r")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(6.0);
            let pts: Vec<[f32; 2]> = (0..=16)
                .map(|i| {
                    let a = (i as f32 / 16.0) * std::f32::consts::TAU;
                    [cx + rad * a.cos(), cy + rad * a.sin()]
                })
                .collect();
            *shape = std::mem::take(shape).stroke(pts, sw, c);
        }
        "line" => {
            let x1 = node
                .attr("x1")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(0.0);
            let y1 = node
                .attr("y1")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(0.0);
            let x2 = node
                .attr("x2")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(0.0);
            let y2 = node
                .attr("y2")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(0.0);
            *shape = std::mem::take(shape).stroke(vec![[x1, y1], [x2, y2]], sw, c);
        }
        "rect" => {
            let x = node
                .attr("x")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(0.0);
            let y = node
                .attr("y")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(0.0);
            let w = node
                .attr("width")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(0.0);
            let h = node
                .attr("height")
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(0.0);
            let pts = vec![[x, y], [x + w, y], [x + w, y + h], [x, y + h], [x, y]];
            *shape = std::mem::take(shape).stroke(pts, sw, c);
        }
        "polyline" | "polygon" => {
            if let Some(pstr) = node.attr("points") {
                let nums: Vec<f32> = pstr
                    .split(|ch: char| ch.is_whitespace() || ch == ',')
                    .filter(|s| !s.is_empty())
                    .filter_map(|s| s.parse::<f32>().ok())
                    .collect();
                let mut pts: Vec<[f32; 2]> = nums.chunks_exact(2).map(|p| [p[0], p[1]]).collect();
                if node.tag == "polygon" && pts.len() >= 3 {
                    pts.push(pts[0]);
                }
                if pts.len() >= 2 {
                    *shape = std::mem::take(shape).stroke(pts, sw, c);
                }
            }
        }
        "path" => {
            if let Some(d) = node.attr("d") {
                for poly in sample_svg_path(d) {
                    if poly.len() >= 2 {
                        *shape = std::mem::take(shape).stroke(poly, sw, c);
                    }
                }
            }
        }
        _ => {}
    }
}

fn sample_svg_path(d: &str) -> Vec<Vec<[f32; 2]>> {
    let mut polys: Vec<Vec<[f32; 2]>> = Vec::new();
    let mut cur_poly: Vec<[f32; 2]> = Vec::new();
    let mut cx = 0.0f32;
    let mut cy = 0.0f32;
    let mut sx = 0.0f32;
    let mut sy = 0.0f32;
    let tokens = tokenize_svg_path(d);
    let mut i = 0;
    let mut cmd = b'M';

    while i < tokens.len() {
        if tokens[i].len() == 1 && tokens[i].as_bytes()[0].is_ascii_alphabetic() {
            cmd = tokens[i].as_bytes()[0];
            i += 1;
            if cmd == b'Z' || cmd == b'z' {
                if !cur_poly.is_empty() {
                    cur_poly.push([sx, sy]);
                    polys.push(std::mem::take(&mut cur_poly));
                }
                cx = sx;
                cy = sy;
                continue;
            }
        }
        let next_f = |idx: &mut usize| -> Option<f32> {
            let v = tokens.get(*idx)?.parse::<f32>().ok()?;
            *idx += 1;
            Some(v)
        };
        match cmd {
            b'M' | b'm' => {
                if let (Some(x), Some(y)) = (next_f(&mut i), next_f(&mut i)) {
                    if !cur_poly.is_empty() {
                        polys.push(std::mem::take(&mut cur_poly));
                    }
                    if cmd == b'm' {
                        cx += x;
                        cy += y;
                    } else {
                        cx = x;
                        cy = y;
                    }
                    sx = cx;
                    sy = cy;
                    cur_poly.push([cx, cy]);
                    cmd = if cmd == b'm' { b'l' } else { b'L' };
                } else {
                    i += 1;
                }
            }
            b'L' | b'l' => {
                if let (Some(x), Some(y)) = (next_f(&mut i), next_f(&mut i)) {
                    if cmd == b'l' {
                        cx += x;
                        cy += y;
                    } else {
                        cx = x;
                        cy = y;
                    }
                    cur_poly.push([cx, cy]);
                } else {
                    i += 1;
                }
            }
            b'H' | b'h' => {
                if let Some(x) = next_f(&mut i) {
                    cx = if cmd == b'h' { cx + x } else { x };
                    cur_poly.push([cx, cy]);
                } else {
                    i += 1;
                }
            }
            b'V' | b'v' => {
                if let Some(y) = next_f(&mut i) {
                    cy = if cmd == b'v' { cy + y } else { y };
                    cur_poly.push([cx, cy]);
                } else {
                    i += 1;
                }
            }
            b'C' | b'c' => {
                if let (Some(x1), Some(y1), Some(x2), Some(y2), Some(x), Some(y)) = (
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                ) {
                    let (p1x, p1y, p2x, p2y, ex, ey) = if cmd == b'c' {
                        (cx + x1, cy + y1, cx + x2, cy + y2, cx + x, cy + y)
                    } else {
                        (x1, y1, x2, y2, x, y)
                    };
                    for step in 1..=6 {
                        let t = step as f32 / 6.0;
                        let mt = 1.0 - t;
                        let px = mt * mt * mt * cx
                            + 3.0 * mt * mt * t * p1x
                            + 3.0 * mt * t * t * p2x
                            + t * t * t * ex;
                        let py = mt * mt * mt * cy
                            + 3.0 * mt * mt * t * p1y
                            + 3.0 * mt * t * t * p2y
                            + t * t * t * ey;
                        cur_poly.push([px, py]);
                    }
                    cx = ex;
                    cy = ey;
                } else {
                    i += 1;
                }
            }
            b'Q' | b'q' => {
                if let (Some(x1), Some(y1), Some(x), Some(y)) = (
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                ) {
                    let (p1x, p1y, ex, ey) = if cmd == b'q' {
                        (cx + x1, cy + y1, cx + x, cy + y)
                    } else {
                        (x1, y1, x, y)
                    };
                    for step in 1..=5 {
                        let t = step as f32 / 5.0;
                        let mt = 1.0 - t;
                        let px = mt * mt * cx + 2.0 * mt * t * p1x + t * t * ex;
                        let py = mt * mt * cy + 2.0 * mt * t * p1y + t * t * ey;
                        cur_poly.push([px, py]);
                    }
                    cx = ex;
                    cy = ey;
                } else {
                    i += 1;
                }
            }
            b'A' | b'a' => {
                if let (Some(_rx), Some(_ry), Some(_rot), Some(_la), Some(_sw), Some(x), Some(y)) = (
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                    next_f(&mut i),
                ) {
                    let (ex, ey) = if cmd == b'a' {
                        (cx + x, cy + y)
                    } else {
                        (x, y)
                    };
                    cur_poly.push([ex, ey]);
                    cx = ex;
                    cy = ey;
                } else {
                    i += 1;
                }
            }
            _ => {
                i += 1;
            }
        }
    }
    if !cur_poly.is_empty() {
        polys.push(cur_poly);
    }
    polys
}

fn tokenize_svg_path(d: &str) -> Vec<String> {
    let mut out = Vec::new();
    let bytes = d.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        let b = bytes[i];
        if b.is_ascii_whitespace() || b == b',' {
            i += 1;
            continue;
        }
        if b.is_ascii_alphabetic() {
            out.push((b as char).to_string());
            i += 1;
            continue;
        }
        let start = i;
        if b == b'-' || b == b'+' {
            i += 1;
        }
        let mut saw_dot = false;
        while i < bytes.len() {
            let c = bytes[i];
            if c.is_ascii_digit() {
                i += 1;
            } else if c == b'.' && !saw_dot {
                saw_dot = true;
                i += 1;
            } else {
                break;
            }
        }
        if i > start {
            out.push(d[start..i].to_string());
        } else {
            i += 1;
        }
    }
    out
}

pub fn summarize_dom(arena: &DomArena, root_id: NodeId) -> HashMap<String, usize> {
    let mut counts = HashMap::new();
    fn walk(arena: &DomArena, id: NodeId, counts: &mut HashMap<String, usize>) {
        if let Some(n) = arena.nodes.get(&id) {
            *counts.entry(n.tag.clone()).or_insert(0) += 1;
            for &c in &n.children {
                walk(arena, c, counts);
            }
        }
    }
    walk(arena, root_id, &mut counts);
    counts
}
