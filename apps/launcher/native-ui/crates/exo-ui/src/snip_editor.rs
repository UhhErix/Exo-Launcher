use crate::devices_audio::device_btn;
use crate::state::{AppState, HostStatus};
use exo_kit::{
    BG, FG, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, RADIUS_SMALL, bad, fg_faint, fg_muted,
    filter_pill, float_line, glass_control_fill, glass_rim, good, hairline, hover_fill, icon_close,
    icon_notes, icon_scissors, icon_sliders, icon_sparkles, primary_button, scaled, srgb,
    type_body, type_control, type_section, type_small,
};
use exo_rpc::{
    CaptureItem, CaptureSaveImageReply, CaptureTextReply, METHOD_CAPTURE_SAVE_IMAGE,
    METHOD_CAPTURE_TEXT,
};
use rune::{
    CanvasAnnotation, CanvasTool, Color, CropRect, Cursor, El, ImageCanvas, ImageCanvasState, Key,
    KeyEvent, Pixels, TextStyle, Tx, col, el, row, text,
};
use serde_json::json;

fn annotate_red() -> Color {
    srgb(255, 92, 92, 1.0)
}
fn highlight_yellow() -> Color {
    srgb(255, 214, 10, 0.45)
}
fn blur_tint() -> Color {
    srgb(36, 36, 41, 0.92)
}
fn crop_scrim() -> Color {
    Color::BLACK.alpha(0.6)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SnipTab {
    Crop,
    #[default]
    Markup,
    Blur,
    Text,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SnipMarkupTool {
    #[default]
    Arrow,
    Box,
    Highlight,
    Freehand,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CropShape {
    #[default]
    Free,
    Wide,
    Tall,
    Square,
}

impl CropShape {
    pub fn ratio(self) -> Option<f32> {
        match self {
            Self::Free => None,
            Self::Wide => Some(16.0 / 9.0),
            Self::Tall => Some(9.0 / 16.0),
            Self::Square => Some(1.0),
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Free => "Free",
            Self::Wide => "16:9",
            Self::Tall => "9:16",
            Self::Square => "Square",
        }
    }
}

pub fn centered_crop(ratio: f32, width: u32, height: u32) -> CropRect {
    let wf = width.max(1) as f32;
    let hf = height.max(1) as f32;
    let w = wf.min(hf * ratio);
    let h = w / ratio;
    CropRect::clamped((wf - w) * 0.5, (hf - h) * 0.5, w, h, wf, hf, 8.0)
}

pub fn blur_cell_size(rect_w: f32, rect_h: f32, unit: f32) -> f32 {
    (unit * 8.0).max(rect_w.min(rect_h)).max(4.0)
}

pub fn apply_cell_blur(
    rgba: &mut [u8],
    img_w: u32,
    img_h: u32,
    rx: u32,
    ry: u32,
    rw: u32,
    rh: u32,
) {
    if img_w == 0 || img_h == 0 || rw == 0 || rh == 0 {
        return;
    }
    let unit = (img_w.max(img_h) as f32 / 400.0).round().max(2.0);
    let cell = blur_cell_size(rw as f32, rh as f32, unit).round().max(4.0) as u32;
    let x0 = rx.min(img_w);
    let y0 = ry.min(img_h);
    let x1 = rx.saturating_add(rw).min(img_w);
    let y1 = ry.saturating_add(rh).min(img_h);

    let mut by = y0;
    while by < y1 {
        let by_end = (by + cell).min(y1);
        let mut bx = x0;
        while bx < x1 {
            let bx_end = (bx + cell).min(x1);
            let mut sr: u64 = 0;
            let mut sg: u64 = 0;
            let mut sb: u64 = 0;
            let mut sa: u64 = 0;
            let mut count: u64 = 0;
            for y in by..by_end {
                for x in bx..bx_end {
                    let idx = ((y * img_w + x) * 4) as usize;
                    if idx + 3 < rgba.len() {
                        sr += rgba[idx] as u64;
                        sg += rgba[idx + 1] as u64;
                        sb += rgba[idx + 2] as u64;
                        sa += rgba[idx + 3] as u64;
                        count += 1;
                    }
                }
            }
            if let (Some(ar), Some(ag), Some(ab), Some(aa)) = (
                sr.checked_div(count),
                sg.checked_div(count),
                sb.checked_div(count),
                sa.checked_div(count),
            ) {
                let (ar, ag, ab, aa) = (ar as u8, ag as u8, ab as u8, aa as u8);
                for y in by..by_end {
                    for x in bx..bx_end {
                        let idx = ((y * img_w + x) * 4) as usize;
                        if idx + 3 < rgba.len() {
                            rgba[idx] = ar;
                            rgba[idx + 1] = ag;
                            rgba[idx + 2] = ab;
                            rgba[idx + 3] = aa;
                        }
                    }
                }
            }
            bx = bx_end;
        }
        by = by_end;
    }
}

pub struct SnipEditorState {
    pub item_id: String,
    pub tab: SnipTab,
    pub markup: SnipMarkupTool,
    pub crop_shape: CropShape,
    pub pixels: Option<Pixels>,
    pub canvas: ImageCanvasState,
    pub busy: bool,
    pub reading_text: bool,
    pub said: Option<String>,
    pub error: Option<String>,
}

impl SnipEditorState {
    pub fn new(item_id: String, pixels: Option<Pixels>) -> Self {
        let (w, h) = pixels
            .as_ref()
            .map(|p| p.size())
            .filter(|(w, h)| *w > 0 && *h > 0)
            .unwrap_or((1280, 720));
        let mut canvas = ImageCanvasState::new(w, h);
        let unit = (w.max(h) as f32 / 400.0).round().max(2.0);
        canvas.set_tool(CanvasTool::Arrow);
        canvas.set_color(annotate_red());
        canvas.set_stroke_width(unit * 2.0);
        canvas.set_rect_fill(None);
        Self {
            item_id,
            tab: SnipTab::Markup,
            markup: SnipMarkupTool::Arrow,
            crop_shape: CropShape::Free,
            pixels,
            canvas,
            busy: false,
            reading_text: false,
            said: None,
            error: None,
        }
    }

    pub fn is_dirty(&self) -> bool {
        !self.canvas.annotations().is_empty() || self.canvas.crop().is_some()
    }

    pub fn sync_tool(&mut self) {
        let [w, h] = self.canvas.natural_size();
        let unit = (w.max(h) as f32 / 400.0).round().max(2.0);
        match self.tab {
            SnipTab::Crop | SnipTab::Text => {
                self.canvas.set_tool(CanvasTool::Crop);
            }
            SnipTab::Blur => {
                self.canvas.set_tool(CanvasTool::Rect);
                self.canvas.set_color(blur_tint());
                self.canvas.set_stroke_width(1.0);
                self.canvas.set_rect_fill(Some(blur_tint()));
            }
            SnipTab::Markup => match self.markup {
                SnipMarkupTool::Arrow => {
                    self.canvas.set_tool(CanvasTool::Arrow);
                    self.canvas.set_color(annotate_red());
                    self.canvas.set_stroke_width(unit * 2.0);
                    self.canvas.set_rect_fill(None);
                }
                SnipMarkupTool::Box => {
                    self.canvas.set_tool(CanvasTool::Rect);
                    self.canvas.set_color(annotate_red());
                    self.canvas.set_stroke_width(unit * 2.0);
                    self.canvas.set_rect_fill(None);
                }
                SnipMarkupTool::Highlight => {
                    self.canvas.set_tool(CanvasTool::Rect);
                    self.canvas.set_color(Color::TRANSPARENT);
                    self.canvas.set_stroke_width(0.0);
                    self.canvas.set_rect_fill(Some(highlight_yellow()));
                }
                SnipMarkupTool::Freehand => {
                    self.canvas.set_tool(CanvasTool::Freehand);
                    self.canvas.set_color(annotate_red());
                    self.canvas.set_stroke_width(unit * 2.0);
                    self.canvas.set_rect_fill(None);
                }
            },
        }
    }

    pub fn revert(&mut self) {
        self.canvas.clear_annotations();
        self.canvas.reset_crop();
        self.crop_shape = CropShape::Free;
        self.said = None;
        self.error = None;
    }

    pub fn export_png_bytes(&self) -> Result<Vec<u8>, String> {
        let Some(ref src_pixels) = self.pixels else {
            return Err("Exo couldn’t open the snip. Cancel and open it again.".into());
        };
        let has_blur = self.canvas.annotations().iter().any(
            |a| matches!(a, CanvasAnnotation::Rect { fill: Some(c), .. } if *c == blur_tint()),
        );
        if !has_blur {
            return self
                .canvas
                .export_png(src_pixels)
                .map_err(|e| format!("Exo couldn’t save the snip: {e:?}"));
        }
        let (src_w, src_h, mut rgba) = src_pixels.to_rgba8();
        let mut clean_canvas = ImageCanvasState::new(src_w, src_h);
        if let Some(crop) = self.canvas.crop() {
            clean_canvas.set_crop(Some(crop));
        }
        for ann in self.canvas.annotations() {
            match ann {
                CanvasAnnotation::Rect {
                    x,
                    y,
                    w,
                    h,
                    fill: Some(c),
                    ..
                } if *c == blur_tint() => {
                    apply_cell_blur(
                        &mut rgba,
                        src_w,
                        src_h,
                        x.round().max(0.0) as u32,
                        y.round().max(0.0) as u32,
                        w.round().max(1.0) as u32,
                        h.round().max(1.0) as u32,
                    );
                }
                other => clean_canvas.push_annotation(other.clone()),
            }
        }
        let blurred_pixels = Pixels::new();
        blurred_pixels.set(src_w, src_h, rgba);
        clean_canvas
            .export_png(&blurred_pixels)
            .map_err(|e| format!("Exo couldn’t save the snip: {e:?}"))
    }
}

impl AppState {
    pub fn open_snip_editor(&mut self, item_id: &str) {
        let pixels = self
            .media
            .capture_items
            .iter()
            .find(|c| c.id == item_id)
            .and_then(|item| {
                if let Some(path) = self.art_base.resolve_capture_file(&item.url)
                    && let Ok(bytes) = std::fs::read(&path)
                    && let Ok(px) = Pixels::from_png(&bytes)
                {
                    return Some(px);
                }
                None
            });
        let pixels = pixels.or_else(|| {
            (self.status == HostStatus::Standalone).then(|| {
                let (w, h) = (960u32, 540u32);
                let mut rgba = vec![0u8; (w * h * 4) as usize];
                for y in 0..h {
                    for x in 0..w {
                        let i = ((y * w + x) * 4) as usize;
                        let gx = (x * 255 / w) as u8;
                        let gy = (y * 255 / h) as u8;
                        rgba[i] = 18 + (gx / 5);
                        rgba[i + 1] = 24 + (gy / 4);
                        rgba[i + 2] = 44 + (gx / 3);
                        rgba[i + 3] = 255;
                    }
                }
                let px = Pixels::new();
                px.set(w, h, rgba);
                px
            })
        });
        let mut ed = SnipEditorState::new(item_id.to_string(), pixels);
        if ed.pixels.is_none() && self.status != HostStatus::Standalone {
            ed.error = Some("Loading snip image…".into());
        }
        self.media.snip_editor = Some(ed);
    }

    pub fn close_snip_editor(&mut self) {
        self.media.snip_editor = None;
    }

    pub fn tick_snip_editor_reload(&mut self) -> bool {
        let Some(ref ed) = self.media.snip_editor else {
            return false;
        };
        if ed.pixels.is_some() || self.status == HostStatus::Standalone {
            return false;
        }
        let item_id = ed.item_id.clone();
        let reloaded = self
            .media
            .capture_items
            .iter()
            .find(|c| c.id == item_id)
            .and_then(|item| {
                if let Some(path) = self.art_base.resolve_capture_file(&item.url)
                    && let Ok(bytes) = std::fs::read(&path)
                    && let Ok(px) = Pixels::from_png(&bytes)
                {
                    return Some(px);
                }
                None
            });
        let Some(px) = reloaded else {
            return false;
        };
        let mut next = SnipEditorState::new(item_id, Some(px));
        if let Some(ref old) = self.media.snip_editor {
            next.tab = old.tab;
            next.markup = old.markup;
            next.crop_shape = old.crop_shape;
        }
        next.sync_tool();
        self.media.snip_editor = Some(next);
        true
    }

    pub fn snip_save(&mut self) {
        let Some(ref mut ed) = self.media.snip_editor else {
            return;
        };
        if ed.busy || ed.pixels.is_none() {
            return;
        }
        let item_id = ed.item_id.clone();
        let png_bytes = match ed.export_png_bytes() {
            Ok(b) => b,
            Err(msg) => {
                ed.error = Some(msg);
                return;
            }
        };
        let b64 = exo_rpc::base64::encode(&png_bytes);
        ed.busy = true;
        ed.error = None;
        if self.status == HostStatus::Standalone {
            ed.busy = false;
            self.media.snip_editor = None;
            self.media.viewer_note = Some(("Saved and copied.".into(), false));
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CaptureSaveImageReply>(
                METHOD_CAPTURE_SAVE_IMAGE,
                &json!({ "id": item_id, "png": b64 }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(reply) if reply.ok => {
                            if let Some(saved) = reply.item
                                && let Some(slot) =
                                    s.media.capture_items.iter_mut().find(|c| c.id == saved.id)
                            {
                                *slot = saved;
                            }
                            s.media.snip_editor = None;
                            s.media.viewer_note = Some(("Saved and copied.".into(), false));
                        }
                        Ok(reply) => {
                            if let Some(ref mut ed) = s.media.snip_editor {
                                ed.busy = false;
                                ed.error = Some(reply.message.unwrap_or_else(|| {
                                    "Exo couldn’t save the snip. Try again.".into()
                                }));
                            }
                        }
                        Err(_) => {
                            if let Some(ref mut ed) = s.media.snip_editor {
                                ed.busy = false;
                                ed.error = Some("Exo couldn’t save the snip. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (item_id, b64);
    }

    pub fn snip_copy(&mut self) {
        let Some(ref ed) = self.media.snip_editor else {
            return;
        };
        if ed.busy || ed.pixels.is_none() {
            return;
        }
        if ed.is_dirty() {
            self.snip_save();
            return;
        }
        let id = ed.item_id.clone();
        self.media.snip_editor = None;
        self.capture_viewer_action(&id, "copy");
    }

    pub fn snip_delete(&mut self) {
        let Some(ref ed) = self.media.snip_editor else {
            return;
        };
        if ed.busy {
            return;
        }
        let id = ed.item_id.clone();
        self.media.snip_editor = None;
        self.capture_viewer_action(&id, "delete");
    }

    pub fn snip_copy_text(&mut self, use_crop: bool) {
        let Some(ref mut ed) = self.media.snip_editor else {
            return;
        };
        if ed.reading_text {
            return;
        }
        let item_id = ed.item_id.clone();
        let area = if use_crop {
            ed.canvas.crop().map(|c| {
                let [w, h] = ed.canvas.natural_size();
                let (x, y, cw, ch) = c.to_pixels(w, h);
                json!({ "x": x, "y": y, "width": cw, "height": ch })
            })
        } else {
            None
        };
        ed.reading_text = true;
        ed.error = None;
        ed.said = None;
        if self.status == HostStatus::Standalone {
            ed.reading_text = false;
            ed.said = Some("Copied: Sample OCR text".into());
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let payload = match area {
                Some(a) => {
                    let mut m = a.as_object().cloned().unwrap_or_default();
                    m.insert("id".into(), json!(item_id));
                    serde_json::Value::Object(m)
                }
                None => json!({ "id": item_id }),
            };
            rpc.call::<_, CaptureTextReply>(METHOD_CAPTURE_TEXT, &payload, move |res| {
                handle.update(move |s, tx| {
                    if let Some(ref mut ed) = s.media.snip_editor {
                        ed.reading_text = false;
                        match res {
                            Ok(reply) if reply.ok => {
                                if let Some(txt) = reply.text.filter(|t| !t.is_empty()) {
                                    tx.copy(txt.clone());
                                    let preview = if txt.chars().count() > 80 {
                                        let short: String = txt.chars().take(80).collect();
                                        format!("{short}…")
                                    } else {
                                        txt
                                    };
                                    ed.said = Some(format!("Copied: {preview}"));
                                } else {
                                    ed.error = Some(
                                        reply
                                            .message
                                            .unwrap_or_else(|| "No text found there.".into()),
                                    );
                                }
                            }
                            Ok(reply) => {
                                ed.error = Some(
                                    reply
                                        .message
                                        .unwrap_or_else(|| "No text found there.".into()),
                                );
                            }
                            Err(_) => {
                                ed.error = Some("Exo couldn’t read the text. Try again.".into());
                            }
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (item_id, area);
    }

    pub fn capture_viewer_copy_text(&mut self, item_id: &str) {
        self.media.viewer_note = None;
        if self.status == HostStatus::Standalone {
            self.media.viewer_note = Some(("Copied the text.".into(), false));
            return;
        }
        let id = item_id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CaptureTextReply>(
                METHOD_CAPTURE_TEXT,
                &json!({ "id": id }),
                move |res| {
                    handle.update(move |s, tx| match res {
                        Ok(reply) if reply.ok => {
                            if let Some(txt) = reply.text.filter(|t| !t.is_empty()) {
                                tx.copy(txt);
                                s.media.viewer_note = Some(("Copied the text.".into(), false));
                            } else {
                                s.media.viewer_note = Some((
                                    reply.message.unwrap_or_else(|| "No text found.".into()),
                                    true,
                                ));
                            }
                        }
                        Ok(reply) => {
                            s.media.viewer_note = Some((
                                reply.message.unwrap_or_else(|| "No text found.".into()),
                                true,
                            ));
                        }
                        Err(_) => {
                            s.media.viewer_note =
                                Some(("Exo couldn’t read the text. Try again.".into(), true));
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = id;
    }

    pub fn handle_snip_editor_key(&mut self, ev: &KeyEvent, _tx: &mut Tx) -> bool {
        if self.media.snip_editor.is_none() {
            return false;
        }
        if ev.key == Key::Escape {
            if let Some(ref mut ed) = self.media.snip_editor
                && ed.tab == SnipTab::Crop
                && ed.canvas.crop().is_some()
            {
                ed.canvas.reset_crop();
                ed.crop_shape = CropShape::Free;
                return true;
            }
            self.close_snip_editor();
            return true;
        }
        if ev.is_command('s') || ev.key == Key::Enter {
            self.snip_save();
            return true;
        }
        if ev.is_command('z') {
            if let Some(ref mut ed) = self.media.snip_editor {
                if ev.mods.shift {
                    ed.canvas.redo();
                } else {
                    ed.canvas.undo();
                }
            }
            return true;
        }
        if ev.is_command('y') {
            if let Some(ref mut ed) = self.media.snip_editor {
                ed.canvas.redo();
            }
            return true;
        }
        false
    }
}

pub fn view(s: &AppState, _item: &CaptureItem) -> El {
    let Some(ref ed) = s.media.snip_editor else {
        return el();
    };
    let dirty = ed.is_dirty();
    let can_undo = ed.canvas.can_undo();
    let can_redo = ed.canvas.can_redo();
    let can_save = ed.pixels.is_some() && !ed.busy;

    let top_bar = row()
        .w_full()
        .px(20.0)
        .py(12.0)
        .gap(8.0)
        .center_y()
        .border_sides([0.0, 0.0, 1.0, 0.0], hairline())
        .child(
            device_btn("snip-revert", "Revert", !dirty)
                .cursor(if dirty {
                    Cursor::Pointer
                } else {
                    Cursor::NotAllowed
                })
                .on_press(|s: &mut AppState| {
                    if let Some(ref mut e) = s.media.snip_editor {
                        e.revert();
                    }
                }),
        )
        .child(
            device_btn("snip-undo", "Undo", !can_undo)
                .cursor(if can_undo {
                    Cursor::Pointer
                } else {
                    Cursor::NotAllowed
                })
                .on_press(|s: &mut AppState| {
                    if let Some(ref mut e) = s.media.snip_editor {
                        e.canvas.undo();
                    }
                }),
        )
        .child(
            device_btn("snip-redo", "Redo", !can_redo)
                .cursor(if can_redo {
                    Cursor::Pointer
                } else {
                    Cursor::NotAllowed
                })
                .on_press(|s: &mut AppState| {
                    if let Some(ref mut e) = s.media.snip_editor {
                        e.canvas.redo();
                    }
                }),
        )
        .child(el().grow(1.0))
        .child(
            device_btn("snip-cancel", "Close", ed.busy)
                .cursor(if ed.busy {
                    Cursor::NotAllowed
                } else {
                    Cursor::Pointer
                })
                .on_press(|s: &mut AppState| s.close_snip_editor()),
        )
        .child(
            exo_kit::danger_button("snip-delete", "Delete", None)
                .on_press(|s: &mut AppState| s.snip_delete()),
        )
        .child(
            device_btn("snip-copy", "Copy", !can_save)
                .cursor(if can_save {
                    Cursor::Pointer
                } else {
                    Cursor::NotAllowed
                })
                .on_press(|s: &mut AppState| s.snip_copy()),
        )
        .child(if can_save {
            primary_button("snip-save", "Save", None).on_press(|s: &mut AppState| s.snip_save())
        } else {
            device_btn("snip-save", if ed.busy { "Saving…" } else { "Save" }, true)
                .cursor(Cursor::NotAllowed)
        });

    let media_stage = if let Some(ref px) = ed.pixels {
        el().key("snip-canvas-stage")
            .grow(1.0)
            .h_full()
            .min_w(0.0)
            .min_h(0.0)
            .fill(BG)
            .px(12.0)
            .pb(14.0)
            .center()
            .child(
                ImageCanvas::new(&ed.canvas, px)
                    .key("snip-image-canvas")
                    .size(940.0, 810.0)
                    .crop_scrim(crop_scrim())
                    .crop_border(FG)
                    .handle_color(FG)
                    .on_state(|s: &mut AppState| &mut s.media.snip_editor.as_mut().unwrap().canvas)
                    .build(),
            )
    } else {
        el().grow(1.0).h_full().center().child(
            text(
                ed.error
                    .clone()
                    .unwrap_or_else(|| "Exo couldn’t open the snip.".into()),
            )
            .font(type_body())
            .color(fg_muted()),
        )
    };

    let tabs_row = row().w_full().flex_wrap().gap(6.0).children(
        [
            (SnipTab::Crop, "snip-tab-crop", "Crop"),
            (SnipTab::Markup, "snip-tab-markup", "Mark up"),
            (SnipTab::Blur, "snip-tab-blur", "Blur"),
            (SnipTab::Text, "snip-tab-text", "Copy text"),
        ]
        .into_iter()
        .map(|(tab, key, label)| {
            let active = ed.tab == tab;
            let ic_color = if active { PRIMARY_FG } else { FG };
            let ic = match tab {
                SnipTab::Crop => icon_scissors(14.0, ic_color),
                SnipTab::Markup => icon_sparkles(14.0, ic_color),
                SnipTab::Blur => icon_sliders(14.0, ic_color),
                SnipTab::Text => icon_notes(14.0, ic_color),
            };
            row()
                .key(key)
                .role("tab")
                .label(label)
                .checked(active)
                .focusable(true)
                .hit()
                .cursor(Cursor::Pointer)
                .h(28.0)
                .px(10.0)
                .gap(5.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(if active { FG } else { Color::TRANSPARENT })
                .hover(if active { FG } else { hover_fill() })
                .on_press(move |s: &mut AppState| {
                    if let Some(ref mut e) = s.media.snip_editor {
                        e.tab = tab;
                        e.said = None;
                        e.sync_tool();
                    }
                })
                .child(ic)
                .child(
                    text(label)
                        .font(type_small())
                        .color(if active { PRIMARY_FG } else { FG }),
                )
        }),
    );

    let mut panel = col().w_full().gap(12.0);
    match ed.tab {
        SnipTab::Crop => {
            let shapes = row().w_full().flex_wrap().gap(8.0).children(
                [
                    CropShape::Free,
                    CropShape::Wide,
                    CropShape::Tall,
                    CropShape::Square,
                ]
                .into_iter()
                .map(|shape| {
                    filter_pill(
                        format!("snip-crop-{}", shape.label()),
                        shape.label(),
                        ed.crop_shape == shape,
                    )
                    .on_press(move |s: &mut AppState| {
                        if let Some(ref mut e) = s.media.snip_editor {
                            e.crop_shape = shape;
                            e.canvas.set_tool(CanvasTool::Crop);
                            if let Some(r) = shape.ratio() {
                                let [w, h] = e.canvas.natural_size();
                                e.canvas.set_crop(Some(centered_crop(r, w, h)));
                            }
                        }
                    })
                }),
            );
            let hint = if ed.crop_shape.ratio().is_some() {
                "Drag to place it, or keep the middle."
            } else {
                "Drag over what to keep."
            };
            panel = panel
                .child(shapes)
                .child(text(hint).font(type_small()).color(fg_muted()).wrap());
            if ed.canvas.crop().is_some() {
                panel = panel.child(
                    device_btn("snip-crop-reset", "Whole picture", false).on_press(
                        |s: &mut AppState| {
                            if let Some(ref mut e) = s.media.snip_editor {
                                e.canvas.reset_crop();
                                e.crop_shape = CropShape::Free;
                            }
                        },
                    ),
                );
            }
        }
        SnipTab::Markup => {
            let mk_tile = |tool: SnipMarkupTool, key: &'static str, label: &'static str| {
                let on = ed.markup == tool;
                col()
                    .key(key)
                    .role("button")
                    .label(label)
                    .focusable(true)
                    .hit()
                    .cursor(Cursor::Pointer)
                    .grow(1.0)
                    .min_w(0.0)
                    .h(56.0)
                    .center()
                    .radius(10.0)
                    .fill(if on { hover_fill() } else { Color::TRANSPARENT })
                    .border(1.0, if on { FG.alpha(0.35) } else { glass_rim() })
                    .hover(hover_fill())
                    .on_press(move |s: &mut AppState| {
                        if let Some(ref mut e) = s.media.snip_editor {
                            e.markup = tool;
                            e.sync_tool();
                        }
                    })
                    .child(text(label).font(type_small()).color(FG))
            };
            let tools = col()
                .w_full()
                .gap(8.0)
                .child(
                    row()
                        .w_full()
                        .gap(8.0)
                        .child(mk_tile(SnipMarkupTool::Arrow, "snip-mk-arrow", "Arrow"))
                        .child(mk_tile(SnipMarkupTool::Box, "snip-mk-box", "Box")),
                )
                .child(
                    row()
                        .w_full()
                        .gap(8.0)
                        .child(mk_tile(
                            SnipMarkupTool::Highlight,
                            "snip-mk-hl",
                            "Highlight",
                        ))
                        .child(mk_tile(SnipMarkupTool::Freehand, "snip-mk-draw", "Draw")),
                );
            let hint = match ed.markup {
                SnipMarkupTool::Arrow => "Drag from where it starts to what it points at.",
                _ => "Drag over it.",
            };
            panel = panel
                .child(tools)
                .child(text(hint).font(type_small()).color(fg_muted()).wrap());
        }
        SnipTab::Blur => {
            panel = panel.child(
                text("Drag over names, chat or anything else to hide. What's under it can't be read back.")
                    .font(type_small())
                    .color(fg_muted())
                    .wrap(),
            );
        }
        SnipTab::Text => {
            panel = panel
                .child(
                    text("Drag over words to copy them. Windows reads them on this PC.")
                        .font(type_small())
                        .color(fg_muted())
                        .wrap(),
                )
                .child(
                    row()
                        .gap(8.0)
                        .flex_wrap()
                        .child(
                            device_btn("snip-ocr-all", "Copy all text", ed.reading_text)
                                .on_press(|s: &mut AppState| s.snip_copy_text(false)),
                        )
                        .children(ed.canvas.crop().map(|_| {
                            device_btn("snip-ocr-area", "Copy selected area", ed.reading_text)
                                .on_press(|s: &mut AppState| s.snip_copy_text(true))
                        })),
                );
        }
    }

    if let Some(ref said) = ed.said {
        panel = panel.child(text(said.clone()).font(type_small()).color(good()));
    }
    if let Some(ref err) = ed.error {
        panel = panel.child(text(err.clone()).font(type_small()).color(bad()));
    }

    let inspector = col()
        .w(304.0)
        .h_full()
        .padding(16.0)
        .gap(16.0)
        .border_sides([0.0, 0.0, 0.0, 1.0], hairline())
        .child(tabs_row)
        .child(panel);

    let _ = (
        PRIMARY_FG,
        RADIUS_PANEL,
        RADIUS_PILL,
        RADIUS_SMALL,
        fg_faint,
        float_line,
        glass_control_fill,
        glass_rim,
        hover_fill,
        icon_close,
        scaled,
        type_control,
        type_section,
        Cursor::Pointer,
        TextStyle::new(13.0, 400.0),
    );

    col()
        .key("snip-editor-root")
        .w_full()
        .h_full()
        .child(top_bar)
        .child(
            row()
                .grow(1.0)
                .w_full()
                .min_h(0.0)
                .child(media_stage)
                .child(inspector),
        )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn centered_crop_preserves_aspect_ratio() {
        let wide = centered_crop(16.0 / 9.0, 1000, 1000);
        assert!((wide.w / wide.h - 16.0 / 9.0).abs() < 0.02);
        let tall = centered_crop(9.0 / 16.0, 1920, 1080);
        assert!((tall.w / tall.h - 9.0 / 16.0).abs() < 0.02);
        let sq = centered_crop(1.0, 1920, 1080);
        assert!((sq.w - 1080.0).abs() < 1.0 && (sq.h - 1080.0).abs() < 1.0);
    }

    #[test]
    fn cell_blur_averages_region_and_leaves_outside_intact() {
        let w = 16u32;
        let h = 16u32;
        let mut rgba = vec![0u8; (w * h * 4) as usize];
        for y in 0..h {
            for x in 0..w {
                let idx = ((y * w + x) * 4) as usize;
                rgba[idx] = if (x + y) % 2 == 0 { 200 } else { 40 };
                rgba[idx + 1] = 100;
                rgba[idx + 2] = 50;
                rgba[idx + 3] = 255;
            }
        }
        apply_cell_blur(&mut rgba, w, h, 0, 0, 8, 8);
        assert_eq!(rgba[0], 120);
        let outside = ((10 * w + 10) * 4) as usize;
        assert_eq!(rgba[outside], 200);
    }

    #[test]
    fn snip_editor_export_png_and_undo_redo() {
        let px = Pixels::new();
        px.set(32, 24, vec![128u8; 32 * 24 * 4]);
        let mut ed = SnipEditorState::new("s1".into(), Some(px));
        assert!(!ed.is_dirty());
        ed.canvas.push_annotation(CanvasAnnotation::Arrow {
            from: [2.0, 2.0],
            to: [20.0, 18.0],
            color: annotate_red(),
            width: 3.0,
        });
        assert!(ed.is_dirty());
        assert!(ed.canvas.can_undo());
        let png = ed.export_png_bytes().expect("png export");
        assert!(png.starts_with(b"\x89PNG\r\n\x1a\n"));
        assert!(ed.canvas.undo());
        assert!(!ed.is_dirty());
        assert!(ed.canvas.redo());
        assert!(ed.is_dirty());
    }

    #[test]
    fn snip_can_save_immediately_without_edits() {
        let mut s = AppState::with_demo_games();
        s.open_snip_editor("demo-snip-1");
        let ed = s.media.snip_editor.as_ref().expect("snip editor opened");
        assert!(ed.pixels.is_some());
        assert!(!ed.is_dirty());

        // Fresh unedited snip saves immediately without needing edits.
        s.snip_save();
        assert!(
            s.media.snip_editor.is_none(),
            "snip_save should succeed and close editor on fresh snip"
        );
        assert_eq!(
            s.media.viewer_note,
            Some(("Saved and copied.".into(), false))
        );
    }
}
