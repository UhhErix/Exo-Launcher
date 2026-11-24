use anyrender::{AntiAliasing, PaintScene, RenderContext, Scene};
use blitz_dom::{IntrinsicSizes, Widget};
use kurbo::{Affine, Arc as KurboArc, BezPath, Point, Rect, Shape, Stroke, Vec2};
use peniko::{Blob, Color, Fill, ImageAlphaType, ImageBrush, ImageData, ImageFormat};
use serde::Deserialize;
use std::sync::{Arc, Mutex};

#[derive(Clone, Debug, Deserialize)]
#[serde(tag = "op", rename_all = "camelCase")]
pub enum Canvas2dOp {
    ClearRect {
        x: f64,
        y: f64,
        w: f64,
        h: f64,
    },
    FillRect {
        x: f64,
        y: f64,
        w: f64,
        h: f64,
        color: String,
        alpha: f64,
    },
    StrokeRect {
        x: f64,
        y: f64,
        w: f64,
        h: f64,
        color: String,
        width: f64,
        alpha: f64,
    },
    FillPath {
        commands: Vec<PathCmd>,
        color: String,
        alpha: f64,
    },
    StrokePath {
        commands: Vec<PathCmd>,
        color: String,
        width: f64,
        alpha: f64,
    },
    FillCircle {
        x: f64,
        y: f64,
        r: f64,
        color: String,
        alpha: f64,
    },
    StrokeLine {
        x1: f64,
        y1: f64,
        x2: f64,
        y2: f64,
        color: String,
        width: f64,
        alpha: f64,
    },
    SetTransform {
        a: f64,
        b: f64,
        c: f64,
        d: f64,
        e: f64,
        f: f64,
    },
    ResetTransform,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(tag = "k", rename_all = "camelCase")]
pub enum PathCmd {
    M {
        x: f64,
        y: f64,
    },
    L {
        x: f64,
        y: f64,
    },
    Q {
        cx: f64,
        cy: f64,
        x: f64,
        y: f64,
    },
    C {
        c1x: f64,
        c1y: f64,
        c2x: f64,
        c2y: f64,
        x: f64,
        y: f64,
    },
    A {
        x: f64,
        y: f64,
        r: f64,
        start: f64,
        end: f64,
        ccw: bool,
    },
    Z,
}

#[derive(Default)]
pub struct CanvasBacking {
    pub width: u32,
    pub height: u32,
    pub ops: Vec<Canvas2dOp>,
    pub rgba_frame: Option<(u32, u32, Vec<u8>)>,
    pub dirty: bool,
    pub draw_calls: u64,
}

pub struct CanvasWidget {
    pub backing: Arc<Mutex<CanvasBacking>>,
}

impl CanvasWidget {
    pub fn new(backing: Arc<Mutex<CanvasBacking>>) -> Self {
        Self { backing }
    }
}

pub fn parse_css_color(s: &str, alpha_mul: f64) -> Color {
    let s = s.trim();
    let mul = alpha_mul.clamp(0.0, 1.0) as f32;
    if let Some(hex) = s.strip_prefix('#') {
        let bytes = hex.as_bytes();
        let (r, g, b, a) = match bytes.len() {
            3 => {
                let d = |i: usize| u8::from_str_radix(&hex[i..=i], 16).unwrap_or(0) * 17;
                (d(0), d(1), d(2), 255)
            }
            6 => {
                let p = |i: usize| u8::from_str_radix(&hex[i..i + 2], 16).unwrap_or(0);
                (p(0), p(2), p(4), 255)
            }
            8 => {
                let p = |i: usize| u8::from_str_radix(&hex[i..i + 2], 16).unwrap_or(255);
                (p(0), p(2), p(4), p(6))
            }
            _ => (255, 255, 255, 255),
        };
        return Color::from_rgba8(r, g, b, ((a as f32) * mul).round() as u8);
    }
    if let Some(inner) = s
        .strip_prefix("rgba(")
        .or_else(|| s.strip_prefix("rgb("))
        .and_then(|v| v.strip_suffix(')'))
    {
        let parts: Vec<&str> = inner
            .split([',', '/'])
            .map(str::trim)
            .filter(|p| !p.is_empty())
            .collect();
        if parts.len() >= 3 {
            let r = parts[0].parse::<f32>().unwrap_or(0.0).clamp(0.0, 255.0) as u8;
            let g = parts[1].parse::<f32>().unwrap_or(0.0).clamp(0.0, 255.0) as u8;
            let b = parts[2].parse::<f32>().unwrap_or(0.0).clamp(0.0, 255.0) as u8;
            let a = parts
                .get(3)
                .and_then(|v| v.parse::<f32>().ok())
                .unwrap_or(1.0)
                .clamp(0.0, 1.0);
            return Color::from_rgba8(r, g, b, (a * mul * 255.0).round() as u8);
        }
    }
    if let Some(inner) = s
        .strip_prefix("hsla(")
        .or_else(|| s.strip_prefix("hsl("))
        .and_then(|v| v.strip_suffix(')'))
    {
        let parts: Vec<&str> = inner
            .split([',', '/'])
            .map(str::trim)
            .filter(|p| !p.is_empty())
            .collect();
        if parts.len() >= 3 {
            let h = parts[0]
                .trim_end_matches("deg")
                .parse::<f32>()
                .unwrap_or(0.0)
                % 360.0;
            let sat = parts[1]
                .trim_end_matches('%')
                .parse::<f32>()
                .unwrap_or(100.0)
                / 100.0;
            let lgt = parts[2]
                .trim_end_matches('%')
                .parse::<f32>()
                .unwrap_or(50.0)
                / 100.0;
            let a = parts
                .get(3)
                .and_then(|v| v.parse::<f32>().ok())
                .unwrap_or(1.0)
                .clamp(0.0, 1.0);
            let (r, g, b) = hsl_to_rgb(h, sat, lgt);
            return Color::from_rgba8(r, g, b, (a * mul * 255.0).round() as u8);
        }
    }
    match s.to_ascii_lowercase().as_str() {
        "black" => Color::from_rgba8(0, 0, 0, (255.0 * mul) as u8),
        "white" => Color::from_rgba8(255, 255, 255, (255.0 * mul) as u8),
        "transparent" => Color::from_rgba8(0, 0, 0, 0),
        "red" => Color::from_rgba8(255, 0, 0, (255.0 * mul) as u8),
        "green" => Color::from_rgba8(0, 128, 0, (255.0 * mul) as u8),
        "blue" => Color::from_rgba8(0, 0, 255, (255.0 * mul) as u8),
        "cyan" => Color::from_rgba8(0, 255, 255, (255.0 * mul) as u8),
        _ => Color::from_rgba8(220, 220, 240, (255.0 * mul) as u8),
    }
}

fn hsl_to_rgb(h: f32, s: f32, l: f32) -> (u8, u8, u8) {
    let c = (1.0 - (2.0 * l - 1.0).abs()) * s;
    let h_prime = ((h % 360.0) + 360.0) % 360.0 / 60.0;
    let x = c * (1.0 - (h_prime % 2.0 - 1.0).abs());
    let (r1, g1, b1) = match h_prime as u32 {
        0 => (c, x, 0.0),
        1 => (x, c, 0.0),
        2 => (0.0, c, x),
        3 => (0.0, x, c),
        4 => (x, 0.0, c),
        _ => (c, 0.0, x),
    };
    let m = l - c / 2.0;
    (
        ((r1 + m).clamp(0.0, 1.0) * 255.0).round() as u8,
        ((g1 + m).clamp(0.0, 1.0) * 255.0).round() as u8,
        ((b1 + m).clamp(0.0, 1.0) * 255.0).round() as u8,
    )
}

fn build_bez_path(cmds: &[PathCmd]) -> BezPath {
    let mut path = BezPath::new();
    for cmd in cmds {
        match *cmd {
            PathCmd::M { x, y } => path.move_to(Point::new(x, y)),
            PathCmd::L { x, y } => path.line_to(Point::new(x, y)),
            PathCmd::Q { cx, cy, x, y } => path.quad_to(Point::new(cx, cy), Point::new(x, y)),
            PathCmd::C {
                c1x,
                c1y,
                c2x,
                c2y,
                x,
                y,
            } => path.curve_to(Point::new(c1x, c1y), Point::new(c2x, c2y), Point::new(x, y)),
            PathCmd::A {
                x,
                y,
                r,
                start,
                end,
                ccw,
            } => {
                let mut sweep = end - start;
                if ccw && sweep > 0.0 {
                    sweep -= std::f64::consts::TAU;
                } else if !ccw && sweep < 0.0 {
                    sweep += std::f64::consts::TAU;
                }
                let arc = KurboArc::new(Point::new(x, y), Vec2::new(r, r), start, sweep, 0.0);
                for el in arc.to_path(0.25) {
                    path.push(el);
                }
            }
            PathCmd::Z => path.close_path(),
        }
    }
    path
}

impl Widget for CanvasWidget {
    fn requires_redraw(&self) -> bool {
        self.backing.lock().map(|b| b.dirty).unwrap_or(false)
    }

    fn intrinsic_sizes(&self) -> IntrinsicSizes {
        let g = self.backing.lock().unwrap();
        let w = if g.width > 0 { g.width as f32 } else { 300.0 };
        let h = if g.height > 0 { g.height as f32 } else { 150.0 };
        IntrinsicSizes {
            width: Some(w),
            height: Some(h),
            ratio: if h > 0.0 { Some(w / h) } else { None },
        }
    }

    fn paint(
        &mut self,
        _render_ctx: &mut dyn RenderContext,
        _styles: &blitz_dom::node::ComputedStyles,
        width: u32,
        height: u32,
        scale: f64,
    ) -> Scene {
        let mut scene = Scene::new();
        let mut g = self.backing.lock().unwrap();
        g.dirty = false;

        if let Some((iw, ih, ref rgba)) = g.rgba_frame {
            if iw > 0 && ih > 0 && rgba.len() >= (iw as usize * ih as usize * 4) {
                let img = ImageData {
                    data: Blob::new(Arc::new(rgba[..(iw as usize * ih as usize * 4)].to_vec())),
                    format: ImageFormat::Rgba8,
                    alpha_type: ImageAlphaType::Alpha,
                    width: iw,
                    height: ih,
                };
                let brush = ImageBrush::new(img);
                let sx = (width as f64) / (iw as f64);
                let sy = (height as f64) / (ih as f64);
                scene.draw_image(brush.as_ref(), Affine::scale_non_uniform(sx, sy));
                return scene;
            }
        }

        let logical_w = if g.width > 0 {
            g.width as f64
        } else {
            (width as f64 / scale).max(1.0)
        };
        let logical_h = if g.height > 0 {
            g.height as f64
        } else {
            (height as f64 / scale).max(1.0)
        };
        let base_xf = Affine::scale_non_uniform(
            (width as f64) / logical_w.max(1.0),
            (height as f64) / logical_h.max(1.0),
        );
        let mut cur_xf = Affine::IDENTITY;

        for op in &g.ops {
            let xf = base_xf * cur_xf;
            let aa = AntiAliasing::default();
            match op {
                Canvas2dOp::ClearRect { x, y, w, h } => {
                    let rect = Rect::new(*x, *y, *x + *w, *y + *h);
                    scene.fill(
                        Fill::NonZero,
                        xf,
                        Color::from_rgba8(0, 0, 0, 0),
                        None,
                        &rect,
                        aa,
                    );
                }
                Canvas2dOp::FillRect {
                    x,
                    y,
                    w,
                    h,
                    color,
                    alpha,
                } => {
                    let rect = Rect::new(*x, *y, *x + *w, *y + *h);
                    scene.fill(
                        Fill::NonZero,
                        xf,
                        parse_css_color(color, *alpha),
                        None,
                        &rect,
                        aa,
                    );
                }
                Canvas2dOp::StrokeRect {
                    x,
                    y,
                    w,
                    h,
                    color,
                    width,
                    alpha,
                } => {
                    let rect = Rect::new(*x, *y, *x + *w, *y + *h);
                    scene.stroke(
                        &Stroke::new(*width),
                        xf,
                        parse_css_color(color, *alpha),
                        None,
                        &rect,
                        aa,
                    );
                }
                Canvas2dOp::FillPath {
                    commands,
                    color,
                    alpha,
                } => {
                    let path = build_bez_path(commands);
                    scene.fill(
                        Fill::NonZero,
                        xf,
                        parse_css_color(color, *alpha),
                        None,
                        &path,
                        aa,
                    );
                }
                Canvas2dOp::StrokePath {
                    commands,
                    color,
                    width,
                    alpha,
                } => {
                    let path = build_bez_path(commands);
                    scene.stroke(
                        &Stroke::new(*width),
                        xf,
                        parse_css_color(color, *alpha),
                        None,
                        &path,
                        aa,
                    );
                }
                Canvas2dOp::FillCircle {
                    x,
                    y,
                    r,
                    color,
                    alpha,
                } => {
                    let c = kurbo::Circle::new(Point::new(*x, *y), *r);
                    scene.fill(
                        Fill::NonZero,
                        xf,
                        parse_css_color(color, *alpha),
                        None,
                        &c,
                        aa,
                    );
                }
                Canvas2dOp::StrokeLine {
                    x1,
                    y1,
                    x2,
                    y2,
                    color,
                    width,
                    alpha,
                } => {
                    let line = kurbo::Line::new(Point::new(*x1, *y1), Point::new(*x2, *y2));
                    scene.stroke(
                        &Stroke::new(*width),
                        xf,
                        parse_css_color(color, *alpha),
                        None,
                        &line,
                        aa,
                    );
                }
                Canvas2dOp::SetTransform { a, b, c, d, e, f } => {
                    cur_xf = Affine::new([*a, *b, *c, *d, *e, *f]);
                }
                Canvas2dOp::ResetTransform => {
                    cur_xf = Affine::IDENTITY;
                }
            }
        }
        scene
    }
}
