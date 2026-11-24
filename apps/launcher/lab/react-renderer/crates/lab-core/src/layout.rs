// SPDX-License-Identifier: MIT

use crate::css::parse_px;
use crate::dom::{
    AlignMode, ComputedStyle, DimVal, DisplayMode, DomArena, FlexDir, JustifyMode, LayoutRect,
    NodeId, OverflowMode, PosMode,
};
use std::collections::HashMap;
use taffy::prelude::*;
use taffy::{MinMax, Overflow, Point, compute_leaf_layout};

struct MeasureCtx {
    is_text: bool,
    text: String,
    font_size: f32,
    font_weight: f32,
    line_height: f32,
    nowrap: bool,
}

pub fn compute_layout(
    arena: &mut DomArena,
    root_id: NodeId,
    viewport_w: f32,
    viewport_h: f32,
) -> f64 {
    let t0 = std::time::Instant::now();
    let mut tree: TaffyTree<MeasureCtx> = TaffyTree::new();
    let mut map: HashMap<NodeId, taffy::NodeId> = HashMap::new();

    if let Some(taffy_root) = build_taffy_node(arena, root_id, &mut tree, &mut map) {
        let _ = tree.compute_layout_with_measure(
            taffy_root,
            Size {
                width: AvailableSpace::Definite(viewport_w),
                height: AvailableSpace::Definite(viewport_h),
            },
            |inputs, _node_id, ctx, style| {
                compute_leaf_layout(
                    inputs,
                    style,
                    |_, _| 0.0,
                    |known_dims, avail_space| {
                        let Some(ctx) = ctx.as_deref() else {
                            return Size::ZERO;
                        };
                        if !ctx.is_text {
                            return Size {
                                width: known_dims.width.unwrap_or(0.0),
                                height: known_dims.height.unwrap_or(0.0),
                            };
                        }
                        measure_text(ctx, known_dims, avail_space)
                    },
                )
            },
        );
        write_back_rects(arena, root_id, 0.0, 0.0, &tree, &map);
    }

    arena.layout_dirty = false;
    t0.elapsed().as_secs_f64() * 1000.0
}

fn build_taffy_node(
    arena: &DomArena,
    id: NodeId,
    tree: &mut TaffyTree<MeasureCtx>,
    map: &mut HashMap<NodeId, taffy::NodeId>,
) -> Option<taffy::NodeId> {
    let node = arena.nodes.get(&id)?;
    if !node.is_text && node.computed.display == DisplayMode::None {
        return None;
    }

    if node.is_text {
        let trimmed = node.text.trim();
        if trimmed.is_empty() {
            return None;
        }
        let ctx = MeasureCtx {
            is_text: true,
            text: node.text.clone(),
            font_size: node.computed.font_size,
            font_weight: node.computed.font_weight,
            line_height: node.computed.line_height,
            nowrap: node.computed.white_space_nowrap,
        };
        let style = Style {
            flex_shrink: 1.0,
            ..Default::default()
        };
        let tid = tree.new_leaf_with_context(style, ctx).ok()?;
        map.insert(id, tid);
        return Some(tid);
    }

    let mut ordered_children: Vec<(i32, usize, NodeId)> = node
        .children
        .iter()
        .enumerate()
        .map(|(i, &cid)| {
            let ord = arena.nodes.get(&cid).map(|c| c.computed.order).unwrap_or(0);
            (ord, i, cid)
        })
        .collect();
    ordered_children.sort_by_key(|&(ord, i, _)| (ord, i));

    let mut child_tids = Vec::with_capacity(ordered_children.len());
    for (_, _, cid) in ordered_children {
        if let Some(ctid) = build_taffy_node(arena, cid, tree, map) {
            child_tids.push(ctid);
        }
    }

    let style = computed_to_taffy(&node.computed, node.tag.as_str(), child_tids.len());
    let tid = tree.new_with_children(style, &child_tids).ok()?;
    map.insert(id, tid);
    Some(tid)
}

fn computed_to_taffy(cs: &ComputedStyle, tag: &str, child_count: usize) -> Style {
    let mut st = Style::default();

    match cs.display {
        DisplayMode::None => st.display = Display::None,
        DisplayMode::Flex | DisplayMode::InlineFlex => {
            st.display = Display::Flex;
            st.flex_direction = match cs.flex_direction {
                FlexDir::Row => FlexDirection::Row,
                FlexDir::Column => FlexDirection::Column,
                FlexDir::RowReverse => FlexDirection::RowReverse,
                FlexDir::ColumnReverse => FlexDirection::ColumnReverse,
            };
            st.flex_wrap = if cs.flex_wrap {
                FlexWrap::Wrap
            } else {
                FlexWrap::NoWrap
            };
        }
        DisplayMode::Grid | DisplayMode::InlineGrid => {
            st.display = Display::Grid;
            if let Some(ref cols) = cs.grid_template_columns {
                st.grid_template_columns = parse_grid_tracks(cols, cs.font_size);
            }
            if let Some(ref rows) = cs.grid_template_rows {
                st.grid_template_rows = parse_grid_tracks(rows, cs.font_size);
            }
        }
        DisplayMode::Inline | DisplayMode::InlineBlock => {
            st.display = Display::Flex;
            st.flex_direction = FlexDirection::Row;
            st.flex_wrap = if cs.white_space_nowrap {
                FlexWrap::NoWrap
            } else {
                FlexWrap::Wrap
            };
            st.align_items = AlignItems::CENTER;
        }
        DisplayMode::Block => {
            st.display = Display::Flex;
            st.flex_direction = FlexDirection::Column;
            if child_count == 0 && matches!(tag, "span" | "div" | "p") {
                st.flex_direction = FlexDirection::Row;
            }
        }
    }

    st.position = match cs.position {
        PosMode::Absolute | PosMode::Fixed => Position::Absolute,
        _ => Position::Relative,
    };

    st.flex_grow = cs.flex_grow;
    st.flex_shrink = cs.flex_shrink;
    st.flex_basis = dim_to_dim(cs.flex_basis);

    st.align_items = match cs.align_items {
        AlignMode::Start => AlignItems::FLEX_START,
        AlignMode::Center => AlignItems::CENTER,
        AlignMode::End => AlignItems::FLEX_END,
        AlignMode::Stretch => AlignItems::STRETCH,
        AlignMode::Baseline => AlignItems::BASELINE,
        AlignMode::Auto => st.align_items,
    };
    st.align_self = match cs.align_self {
        AlignMode::Start => Some(AlignSelf::FLEX_START),
        AlignMode::Center => Some(AlignSelf::CENTER),
        AlignMode::End => Some(AlignSelf::FLEX_END),
        AlignMode::Stretch => Some(AlignSelf::STRETCH),
        AlignMode::Baseline => Some(AlignSelf::BASELINE),
        AlignMode::Auto => None,
    };
    st.justify_content = match cs.justify_content {
        JustifyMode::Start => JustifyContent::FLEX_START,
        JustifyMode::Center => JustifyContent::CENTER,
        JustifyMode::End => JustifyContent::FLEX_END,
        JustifyMode::Between => JustifyContent::SPACE_BETWEEN,
        JustifyMode::Around => JustifyContent::SPACE_AROUND,
        JustifyMode::Evenly => JustifyContent::SPACE_EVENLY,
    };

    st.gap = Size {
        width: LengthPercentage::length(cs.col_gap),
        height: LengthPercentage::length(cs.row_gap),
    };

    st.size = Size {
        width: dim_to_dim(cs.width),
        height: dim_to_dim(cs.height),
    };
    st.min_size = Size {
        width: dim_to_lpa(cs.min_width),
        height: dim_to_lpa(cs.min_height),
    };
    st.max_size = Size {
        width: dim_to_lpa(cs.max_width),
        height: dim_to_lpa(cs.max_height),
    };

    st.padding = Rect {
        top: LengthPercentage::length(cs.padding[0]),
        right: LengthPercentage::length(cs.padding[1]),
        bottom: LengthPercentage::length(cs.padding[2]),
        left: LengthPercentage::length(cs.padding[3]),
    };
    st.margin = Rect {
        top: dim_to_lpa(cs.margin[0]),
        right: dim_to_lpa(cs.margin[1]),
        bottom: dim_to_lpa(cs.margin[2]),
        left: dim_to_lpa(cs.margin[3]),
    };
    st.border = Rect {
        top: LengthPercentage::length(cs.border_widths[0]),
        right: LengthPercentage::length(cs.border_widths[1]),
        bottom: LengthPercentage::length(cs.border_widths[2]),
        left: LengthPercentage::length(cs.border_widths[3]),
    };
    st.inset = Rect {
        top: dim_to_lpa(cs.inset[0]),
        right: dim_to_lpa(cs.inset[1]),
        bottom: dim_to_lpa(cs.inset[2]),
        left: dim_to_lpa(cs.inset[3]),
    };

    st.overflow = Point {
        x: match cs.overflow_x {
            OverflowMode::Visible => Overflow::Visible,
            OverflowMode::Hidden => Overflow::Hidden,
            OverflowMode::Scroll | OverflowMode::Auto => Overflow::Scroll,
        },
        y: match cs.overflow_y {
            OverflowMode::Visible => Overflow::Visible,
            OverflowMode::Hidden => Overflow::Hidden,
            OverflowMode::Scroll | OverflowMode::Auto => Overflow::Scroll,
        },
    };

    if let Some(ref gc) = cs.grid_column {
        st.grid_column = parse_grid_line_range(gc);
    }
    if let Some(ref gr) = cs.grid_row {
        st.grid_row = parse_grid_line_range(gr);
    }

    st
}

fn dim_to_dim(d: DimVal) -> Dimension {
    match d {
        DimVal::Auto => Dimension::auto(),
        DimVal::Px(v) => Dimension::length(v),
        DimVal::Pct(p) => Dimension::percent(p),
    }
}

fn dim_to_lpa(d: DimVal) -> LengthPercentageAuto {
    match d {
        DimVal::Auto => LengthPercentageAuto::auto(),
        DimVal::Px(v) => LengthPercentageAuto::length(v),
        DimVal::Pct(p) => LengthPercentageAuto::percent(p),
    }
}

fn parse_grid_tracks(raw: &str, font_size: f32) -> Vec<GridTemplateComponent<String>> {
    let mut tracks = Vec::new();
    let tokens = split_track_tokens(raw);
    for tok in tokens {
        let t = tok.trim();
        if let Some(inner) = t.strip_prefix("repeat(").and_then(|x| x.strip_suffix(')')) {
            if let Some((count_str, track_str)) = inner.split_once(',') {
                let track = parse_single_track(track_str.trim(), font_size);
                if let Ok(n) = count_str.trim().parse::<u16>() {
                    for _ in 0..n {
                        tracks.push(GridTemplateComponent::Single(track));
                    }
                } else {
                    tracks.push(GridTemplateComponent::Single(track));
                }
            }
        } else {
            tracks.push(GridTemplateComponent::Single(parse_single_track(
                t, font_size,
            )));
        }
    }
    tracks
}

fn parse_single_track(t: &str, font_size: f32) -> TrackSizingFunction {
    if let Some(inner) = t.strip_prefix("minmax(").and_then(|x| x.strip_suffix(')')) {
        if let Some((min_s, max_s)) = inner.split_once(',') {
            let min_fn = parse_min_track(min_s.trim(), font_size);
            let max_fn = parse_max_track(max_s.trim(), font_size);
            return MinMax {
                min: min_fn,
                max: max_fn,
            };
        }
    }
    if let Some(fr_s) = t.strip_suffix("fr") {
        if let Ok(fr) = fr_s.trim().parse::<f32>() {
            return flex(fr);
        }
    }
    if t == "auto" {
        return auto();
    }
    if t == "min-content" {
        return min_content();
    }
    if t == "max-content" {
        return max_content();
    }
    if let Some(px) = parse_px(t, font_size) {
        return length(px);
    }
    flex(1.0)
}

fn parse_min_track(s: &str, font_size: f32) -> MinTrackSizingFunction {
    if s == "auto" {
        return MinTrackSizingFunction::auto();
    }
    if s == "min-content" {
        return MinTrackSizingFunction::min_content();
    }
    if s == "max-content" {
        return MinTrackSizingFunction::max_content();
    }
    if let Some(px) = parse_px(s, font_size) {
        return MinTrackSizingFunction::length(px);
    }
    MinTrackSizingFunction::length(0.0)
}

fn parse_max_track(s: &str, font_size: f32) -> MaxTrackSizingFunction {
    if let Some(fr_s) = s.strip_suffix("fr") {
        if let Ok(fr) = fr_s.trim().parse::<f32>() {
            return MaxTrackSizingFunction::fr(fr);
        }
    }
    if s == "auto" {
        return MaxTrackSizingFunction::auto();
    }
    if let Some(px) = parse_px(s, font_size) {
        return MaxTrackSizingFunction::length(px);
    }
    MaxTrackSizingFunction::fr(1.0)
}

fn split_track_tokens(s: &str) -> Vec<&str> {
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

fn parse_grid_line_range(raw: &str) -> Line<GridPlacement<String>> {
    if let Some((a, b)) = raw.split_once('/') {
        let start = parse_grid_placement(a.trim());
        let end = parse_grid_placement(b.trim());
        return Line { start, end };
    }
    Line {
        start: parse_grid_placement(raw.trim()),
        end: GridPlacement::Auto,
    }
}

fn parse_grid_placement(s: &str) -> GridPlacement<String> {
    if let Some(rest) = s.strip_prefix("span ") {
        if let Ok(n) = rest.trim().parse::<u16>() {
            return GridPlacement::Span(n);
        }
    }
    if let Ok(n) = s.parse::<i16>() {
        return GridPlacement::Line(n.into());
    }
    GridPlacement::Auto
}

fn measure_text(
    ctx: &MeasureCtx,
    known: Size<Option<f32>>,
    avail: Size<AvailableSpace>,
) -> Size<f32> {
    let weight_factor = if ctx.font_weight >= 600.0 { 1.05 } else { 1.0 };
    let char_w = ctx.font_size * 0.56 * weight_factor;
    let line_h = (ctx.font_size * ctx.line_height).max(ctx.font_size + 2.0);
    let text = ctx.text.trim();
    let natural_w: f32 = text
        .chars()
        .map(|c| match c {
            ' ' | 'i' | 'l' | 'I' | '!' | '.' | ',' | ':' | ';' | '|' => char_w * 0.52,
            'm' | 'M' | 'w' | 'W' => char_w * 1.42,
            'A'..='Z' => char_w * 1.15,
            _ => char_w,
        })
        .sum::<f32>()
        .ceil();

    let max_w = known.width.or(match avail.width {
        AvailableSpace::Definite(w) => Some(w),
        AvailableSpace::MaxContent => None,
        AvailableSpace::MinContent => Some(char_w * 6.0),
    });

    if ctx.nowrap || max_w.is_none() || natural_w <= max_w.unwrap_or(f32::INFINITY) {
        return Size {
            width: known.width.unwrap_or(natural_w),
            height: known.height.unwrap_or(line_h),
        };
    }

    let limit = max_w.unwrap().max(char_w * 4.0);
    let mut lines = 1usize;
    let mut cur_w = 0.0f32;
    for word in text.split_whitespace() {
        let ww = word.chars().count() as f32 * char_w;
        if cur_w > 0.0 && cur_w + char_w * 0.52 + ww > limit {
            lines += 1;
            cur_w = ww;
        } else {
            if cur_w > 0.0 {
                cur_w += char_w * 0.52;
            }
            cur_w += ww;
        }
    }
    Size {
        width: known.width.unwrap_or(natural_w.min(limit)),
        height: known.height.unwrap_or(lines as f32 * line_h),
    }
}

fn write_back_rects(
    arena: &mut DomArena,
    id: NodeId,
    parent_x: f32,
    parent_y: f32,
    tree: &TaffyTree<MeasureCtx>,
    map: &HashMap<NodeId, taffy::NodeId>,
) {
    let Some(&tid) = map.get(&id) else {
        if let Some(n) = arena.nodes.get_mut(&id) {
            n.rect = LayoutRect::default();
        }
        return;
    };
    let Ok(layout) = tree.layout(tid) else {
        return;
    };
    let abs_x = parent_x + layout.location.x;
    let abs_y = parent_y + layout.location.y;
    let width = layout.size.width;
    let height = layout.size.height;
    let content_height = (height + layout.scroll_height()).max(height);

    let children = if let Some(n) = arena.nodes.get_mut(&id) {
        n.rect = LayoutRect {
            x: abs_x,
            y: abs_y,
            width,
            height,
            local_x: layout.location.x,
            local_y: layout.location.y,
            content_height,
        };
        n.children.clone()
    } else {
        return;
    };

    for cid in children {
        write_back_rects(arena, cid, abs_x, abs_y, tree, map);
    }
}
