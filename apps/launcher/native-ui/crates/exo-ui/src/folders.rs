use crate::library::{ALL_CARD_H, ALL_CARD_W, cover_surface};
use crate::library_state::{
    CROP_ASPECT, CROP_MAX_ZOOM, FolderView, LibraryDragPayload, add_to_folder, crop_size, fit_crop,
    folder_tile_id, move_folder, reorder_folder_games, sort_folder,
};
use crate::state::{AppState, ContextMenuTarget, InputMode, LibrarySort};
use exo_kit::{
    BG, ExoGlass, FG, FOLDER_COLOR_IDS, PRESS_SCALE, PRIMARY_FG, RADIUS_CARD, RADIUS_PANEL,
    RADIUS_PILL, RADIUS_SHEET, SPRING_BASE, SPRING_FLIGHT, SPRING_MOVE, SPRING_QUICK, SelectOption,
    SelectTrigger, TRANSPARENT, bad, danger_button, fg_faint, fg_muted, folder_color_label,
    folder_color_mix_black, folder_color_swatch, glass_button_opt, glass_control_fill,
    glass_icon_circle_opt, glass_panel_fill, glass_rim, hover_fill, icon_check, icon_edit,
    icon_image_upload, icon_search, icon_zoom_in, icon_zoom_out, modal_card_transition,
    modal_scrim_transition, primary_button, scrim_fill, select_menu_layers, select_trigger,
    slider_range, slider_track, subtle_fill, type_body, type_control, type_section, type_small,
    type_title,
};
use exo_rpc::Game;
use rune::{
    Align, Cursor, El, FileDialogOptions, Phase, ScrollSpec, SurfaceFit, TextEdit, TextStyle, col,
    edit_access, el, row, text, text_input,
};
use std::rc::Rc;

fn folder_art_el(
    folder: &FolderView,
    s: &AppState,
    scale: f32,
    hovered: bool,
    w: f32,
    h: f32,
) -> El {
    let mut art = el()
        .size(w, h)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD);

    if let Some(art_url) = folder.art.as_deref()
        && let Some(img) = s.folder_art_image(art_url, scale)
    {
        art = art.pixels_fit(img.pixels(), SurfaceFit::Fill);
    } else {
        let top = folder_color_mix_black(folder.color, 0.62);
        let bot = folder_color_mix_black(folder.color, 0.34);
        art = art.child(
            el().pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .linear_gradient(std::f32::consts::PI, [(0.0, top), (1.0, bot)])
                .border(1.0, FG.alpha(0.14)),
        );

        let papers: Vec<&Game> = folder.games.iter().take(3).collect();
        let count = papers.len();
        let paper_w = w * 0.46;
        let paper_h = paper_w * 1.5;
        for (idx, game) in papers.iter().enumerate() {
            let n = idx as f32 - (count.saturating_sub(1) as f32) * 0.5;
            let spread = if hovered { 0.44 } else { 0.38 };
            let lift = if hovered { -14.0 } else { 0.0 };
            let left = (w - paper_w) * 0.5 + n * (paper_w * spread);
            let bottom = h * 0.40 - lift;
            let paper = cover_surface(game, s, scale, [paper_w, paper_h], false, true, false)
                .radius(RADIUS_CARD * 0.6)
                .clip_rounded(RADIUS_CARD * 0.6)
                .shadow(4.0, 14.0, BG.alpha(0.45));
            art = art.child(
                el().pin(Some(left), None, None, Some(bottom))
                    .offset(0.0, if hovered { -6.0 } else { 0.0 })
                    .offset_motion(SPRING_MOVE)
                    .child(paper),
            );
        }
    }

    let flap_h = h * 0.58;
    let mut flap = col()
        .pin(Some(0.0), None, Some(0.0), Some(0.0))
        .h(flap_h)
        .px(12.0)
        .pb(12.0)
        .justify_end()
        .gap(2.0)
        .radius(RADIUS_CARD * 0.85)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD * 0.85)
        .liquid_glass(exo_kit::exo_glass(ExoGlass::Control))
        .border_gradient(1.0, FG.alpha(0.22), TRANSPARENT);

    if folder.art.is_none() {
        flap = flap.linear_gradient(
            std::f32::consts::PI,
            [
                (0.0, folder_color_swatch(folder.color, 0.30)),
                (0.5, folder_color_swatch(folder.color, 0.24)),
                (1.0, folder_color_swatch(folder.color, 0.46)),
            ],
        );
    } else {
        flap = flap.fill(glass_control_fill());
    }

    flap = flap.child(
        text(folder.name.clone())
            .font(TextStyle::new(type_control().size, 650.0))
            .color(FG)
            .truncate(),
    );

    art.child(flap)
}

pub fn folder_card(
    folder: &FolderView,
    folder_index: usize,
    card_w: f32,
    card_h: f32,
    s: &AppState,
    scale: f32,
) -> El {
    let tile_id = folder_tile_id(&folder.id);
    let hover_key = format!("tile-{tile_id}");
    let hovered = s.hovered_cover_key.as_deref() == Some(hover_key.as_str())
        || s.lib.folder_drop_target.as_deref() == Some(folder.id.as_str());
    let is_dragging = s.lib.dragging_folder_id.as_deref() == Some(folder.id.as_str());
    let is_open = s.lib.open_folder_id.as_deref() == Some(folder.id.as_str());
    let drop_into_active = s.lib.folder_drop_target.as_deref() == Some(folder.id.as_str());

    let art = folder_art_el(folder, s, scale, hovered, card_w, card_h);

    let hover_key_cb = hover_key.clone();
    let fid_open = folder.id.clone();
    let fid_ctx = folder.id.clone();
    let fid_drag = folder.id.clone();
    let fid_drop_folder = folder.id.clone();
    let fid_drop_game = folder.id.clone();
    let fid_hover_game = folder.id.clone();
    let fid_hover_folder = folder.id.clone();
    let fid_move_folder = folder.id.clone();
    let preview_name = folder.name.clone();
    let preview_color = folder.color;
    let selecting = s.lib.selecting;

    let mut card = el()
        .key(hover_key)
        .role("button")
        .label(format!(
            "{}, folder, {} {}",
            folder.name,
            folder.games.len(),
            if folder.games.len() == 1 {
                "game"
            } else {
                "games"
            }
        ))
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_scale(PRESS_SCALE)
        .size(card_w, card_h)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .opacity(if is_open { 0.0 } else { 1.0 })
        .scale(if is_dragging { 0.94 } else { 1.0 })
        .offset(0.0, if hovered && !is_dragging { -3.0 } else { 0.0 })
        .offset_motion(SPRING_MOVE)
        .motion(SPRING_MOVE)
        .on_hover_change(move |s: &mut AppState, inside, _| {
            if inside {
                s.hovered_cover_key = Some(hover_key_cb.clone());
            } else if s.hovered_cover_key.as_deref() == Some(hover_key_cb.as_str()) {
                s.hovered_cover_key = None;
            }
        })
        .on_press(move |s: &mut AppState| {
            s.input_mode = InputMode::Pointer;
            if !s.lib.selecting {
                s.open_folder_panel(&fid_open, false);
            }
        })
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.input_mode = InputMode::Pointer;
            s.open_folder_context_menu(fid_ctx.clone(), p.x, p.y);
        })
        .child(art);

    if !selecting {
        card = card
            .draggable(move |s: &AppState| {
                (!s.lib.selecting).then(|| LibraryDragPayload::Folder(fid_drag.clone()))
            })
            .drag_preview(move |_: &AppState| {
                el().size(card_w * 0.72, card_h * 0.72)
                    .radius(RADIUS_CARD)
                    .clip_rounded(RADIUS_CARD)
                    .fill(folder_color_mix_black(preview_color, 0.55))
                    .border(1.5, folder_color_swatch(preview_color, 0.9))
                    .px(10.0)
                    .pb(10.0)
                    .child(
                        col()
                            .w_full()
                            .h_full()
                            .justify_end()
                            .child(text(preview_name.clone()).font(type_section()).color(FG)),
                    )
            })
            .accepts_drop(move |s: &AppState, payload: &LibraryDragPayload| {
                if s.lib.selecting {
                    return false;
                }
                match payload {
                    LibraryDragPayload::Folder(from) => from != &fid_hover_folder,
                    LibraryDragPayload::Game(_) => true,
                    LibraryDragPayload::FolderGame { .. } => false,
                }
            })
            .on_drop_target_hover(
                move |s: &mut AppState, payload: Option<&LibraryDragPayload>, _| match payload {
                    Some(LibraryDragPayload::Folder(from)) => {
                        s.lib.dragging_folder_id = Some(from.clone());
                        if let Some(from_idx) = s
                            .settings
                            .library_folders
                            .iter()
                            .position(|f| f.id == *from)
                        {
                            s.lib.folder_reorder.start(from_idx);
                        }
                    }
                    Some(LibraryDragPayload::Game(gid)) => {
                        s.lib.dragging_game_id = Some(gid.clone());
                        s.lib.folder_drop_target = Some(fid_hover_game.clone());
                    }
                    _ => {
                        s.lib.folder_reorder.leave_item(folder_index);
                        if s.lib.folder_drop_target.as_deref() == Some(fid_hover_game.as_str()) {
                            s.lib.folder_drop_target = None;
                        }
                        if payload.is_none() {
                            s.lib.dragging_folder_id = None;
                            s.lib.dragging_game_id = None;
                        }
                    }
                },
            )
            .on_drop_target_move(
                move |s: &mut AppState, payload: &LibraryDragPayload, ev, _| match payload {
                    LibraryDragPayload::Folder(from) => {
                        if s.lib.folder_reorder.dragging().is_none()
                            && let Some(from_idx) = s
                                .settings
                                .library_folders
                                .iter()
                                .position(|f| f.id == *from)
                        {
                            s.lib.folder_reorder.start(from_idx);
                        }
                        s.lib
                            .folder_reorder
                            .hover_point(folder_index, ev, [card_w, card_h], false);
                    }
                    LibraryDragPayload::Game(_) => {
                        s.lib.folder_drop_target = Some(fid_move_folder.clone());
                    }
                    LibraryDragPayload::FolderGame { .. } => {}
                },
            )
            .on_drop(move |s: &mut AppState, payload: &LibraryDragPayload, tx| {
                s.lib.dragging_folder_id = None;
                s.lib.dragging_game_id = None;
                s.lib.folder_drop_target = None;
                match payload {
                    LibraryDragPayload::Folder(from) => {
                        let after = tx.drop_point().is_some_and(|p| p.x >= card_w * 0.5);
                        s.lib.folder_reorder.cancel();
                        let next =
                            move_folder(&s.settings.library_folders, from, &fid_drop_folder, after);
                        s.save_library_folders(next);
                    }
                    LibraryDragPayload::Game(gid) => {
                        let next = add_to_folder(&s.settings.library_folders, gid, &fid_drop_game);
                        s.save_library_folders(next);
                    }
                    LibraryDragPayload::FolderGame { .. } => {}
                }
            });
    }

    if drop_into_active {
        let accent = if folder.color == "gray" {
            FG
        } else {
            folder_color_swatch(folder.color, 0.95)
        };
        card = card.outline(2.0, 2.0, accent);
    }
    card = card.focus_ring(2.0, 3.0, FG);

    if let Some(bar) = s.lib.folder_reorder.indicator(folder_index, FG) {
        card = card.child(bar);
    }

    card
}

pub fn folder_panel_overlay(s: &AppState, scale: f32) -> Vec<El> {
    let Some(folder) = s.open_folder_view() else {
        return Vec::new();
    };
    let cols = folder.games.len().clamp(3, 5);
    let panel_w = (cols as f32 * ALL_CARD_W) + ((cols.saturating_sub(1)) as f32 * 12.0) + 64.0;
    let folder_id_edit = folder.id.clone();
    let folder_id_sort = folder.id.clone();
    let sort_label = if folder.sort == "folder" {
        "Folder order"
    } else {
        LibrarySort::from_id(&folder.sort).label()
    };

    let title_el = if s.lib.folder_renaming {
        el().w(260.0)
            .h(36.0)
            .px(12.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(glass_control_fill())
            .border(1.0, glass_rim())
            .child(
                text_input(&s.lib.folder_rename_input, type_title())
                    .key("folder-panel-rename-input")
                    .label("Folder name")
                    .grow(1.0)
                    .single_line()
                    .color(FG)
                    .on_edit(edit_access(|s: &mut AppState| {
                        &mut s.lib.folder_rename_input
                    }))
                    .on_submit(|s: &mut AppState, _| s.commit_folder_rename()),
            )
    } else {
        let mut btn = row()
            .key("folder-panel-title-btn")
            .role("button")
            .label(folder.name.clone())
            .cursor(Cursor::Text)
            .focusable(true)
            .hit()
            .px(14.0)
            .py(4.0)
            .gap(10.0)
            .center_y()
            .radius(RADIUS_PILL)
            .hover(hover_fill())
            .on_press_tx(|s: &mut AppState, tx| {
                s.lib.folder_renaming = true;
                if let Some(f) = s.open_folder_view() {
                    s.lib.folder_rename_input = TextEdit::new(&f.name);
                }
                tx.focus("folder-panel-rename-input", false);
            });
        if folder.color != "gray" {
            btn = btn.child(
                el().size(10.0, 10.0)
                    .radius(5.0)
                    .fill(folder_color_swatch(folder.color, 1.0)),
            );
        }
        btn.child(text(folder.name.clone()).font(type_title()).color(FG))
    };

    let sort_trigger = select_trigger(
        SelectTrigger::new("folder-panel-sort", "Sort folder", sort_label)
            .open(s.lib.folder_sort_open)
            .flat(true),
        |s: &mut AppState, _| s.lib.folder_sort_open = !s.lib.folder_sort_open,
    );

    let edit_btn = glass_icon_circle_opt(
        "folder-panel-edit",
        "Edit folder",
        32.0,
        icon_edit(15.0, fg_muted()),
        false,
    )
    .on_press(move |s: &mut AppState| s.open_edit_folder_sheet(&folder_id_edit));

    let head = row()
        .w_full()
        .center_y()
        .child(el().grow(1.0).min_w(0.0))
        .child(title_el)
        .child(
            row()
                .grow(1.0)
                .min_w(0.0)
                .justify_end()
                .center_y()
                .gap(6.0)
                .child(sort_trigger)
                .child(edit_btn),
        );

    let allow_reorder = folder.sort == "folder";
    let rows: Vec<El> = folder
        .games
        .chunks(cols)
        .enumerate()
        .map(|(row_idx, chunk)| {
            let tiles: Vec<El> = chunk
                .iter()
                .enumerate()
                .map(|(col_idx, game)| {
                    let index = row_idx * cols + col_idx;
                    folder_panel_game_tile(game, &folder.id, index, allow_reorder, s, scale)
                })
                .collect();
            row().w_full().justify_center().gap(12.0).children(tiles)
        })
        .collect();

    let row_count = folder.games.len().div_ceil(cols).max(1);
    let grid_h = (row_count as f32 * ALL_CARD_H + row_count.saturating_sub(1) as f32 * 12.0 + 16.0)
        .min(560.0);
    let grid = col()
        .w_full()
        .h(grid_h)
        .scroll(ScrollSpec {
            stick_bottom: false,
        })
        .gap(12.0)
        .py(4.0)
        .children(rows);

    let panel = col()
        .key("folder-panel")
        .w(panel_w.min(1040.0))
        .max_h(720.0)
        .px(32.0)
        .pt(26.0)
        .pb(32.0)
        .gap(20.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .clip_rounded(RADIUS_PANEL)
        .hit()
        .cursor(Cursor::Default)
        .liquid_glass(exo_kit::exo_glass(exo_kit::ExoGlass::Panel))
        .transition(modal_card_transition())
        .child(head)
        .child(grid);

    let mut layers = vec![
        el().key("folder-panel-scrim")
            .pin(Some(0.0), Some(52.0), Some(0.0), Some(0.0))
            .fill(scrim_fill())
            .center()
            .transition(modal_scrim_transition())
            .modal_tx(true, |s: &mut AppState, _| s.close_folder_panel())
            .modal_live(["top-bar"])
            .child(panel),
    ];

    if s.lib.folder_sort_open {
        let mut options = vec![SelectOption::new("folder", "Folder order")];
        for sort in LibrarySort::ALL {
            options.push(SelectOption::new(sort.id(), sort.label()));
        }
        layers.extend(select_menu_layers(exo_kit::SelectMenuSpec {
            id: "dd-folder-sort".into(),
            trigger_key: "folder-panel-sort".into(),
            selected: folder.sort.clone(),
            options,
            width: 200.0,
            align: Align::End,
            gamepad_focus: s.input_mode == InputMode::Gamepad,
            on_select: Rc::new(move |s: &mut AppState, val: &str, _| {
                let next = sort_folder(&s.settings.library_folders, &folder_id_sort, val);
                s.save_library_folders(next);
            }),
            on_dismiss: Rc::new(|s: &mut AppState, _| s.lib.folder_sort_open = false),
        }));
    }

    layers
}

fn folder_panel_game_tile(
    game: &Game,
    folder_id: &str,
    index: usize,
    allow_reorder: bool,
    s: &AppState,
    scale: f32,
) -> El {
    let gid = game.id.clone();
    let gid_ctx = game.id.clone();
    let fid_drag = folder_id.to_string();
    let gid_drag = game.id.clone();
    let fid_drop = folder_id.to_string();
    let gid_drop = game.id.clone();
    let fid_check = folder_id.to_string();
    let gid_check = game.id.clone();
    let flight_id = format!("folder-game:{}", game.id);
    let art = cover_surface(game, s, scale, [ALL_CARD_W, ALL_CARD_H], false, true, false)
        .matched(flight_id.clone())
        .motion(SPRING_FLIGHT);
    let preview_title = game.title.clone();

    let mut tile = el()
        .key(format!("folder-tile-{}", game.id))
        .role("button")
        .label(game.title.clone())
        .cursor(Cursor::Pointer)
        .focusable(true)
        .hit()
        .press_scale(PRESS_SCALE)
        .size(ALL_CARD_W, ALL_CARD_H)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .motion(SPRING_MOVE)
        .on_press_tx(move |s: &mut AppState, tx| {
            s.input_mode = InputMode::Pointer;
            s.open_game_page_from(&gid, Some(flight_id.clone()), tx);
        })
        .on_secondary_press(move |s: &mut AppState, p, _| {
            s.input_mode = InputMode::Pointer;
            s.open_context_menu(ContextMenuTarget::GameCard(gid_ctx.clone()), p.x, p.y);
        })
        .child(art);

    if allow_reorder {
        tile = tile
            .draggable(move |_: &AppState| {
                Some(LibraryDragPayload::FolderGame {
                    folder_id: fid_drag.clone(),
                    game_id: gid_drag.clone(),
                })
            })
            .drag_preview(move |_: &AppState| {
                el().size(ALL_CARD_W * 0.72, ALL_CARD_H * 0.72)
                    .radius(RADIUS_CARD)
                    .fill(glass_panel_fill())
                    .border(1.0, glass_rim())
                    .center()
                    .px(8.0)
                    .child(text(preview_title.clone()).font(type_small()).color(FG))
            })
            .accepts_drop(move |_: &AppState, payload: &LibraryDragPayload| {
                matches!(
                    payload,
                    LibraryDragPayload::FolderGame { folder_id, game_id }
                        if folder_id == &fid_check && game_id != &gid_check
                )
            })
            .on_drop_target_hover(
                move |s: &mut AppState, payload: Option<&LibraryDragPayload>, _| {
                    if let Some(LibraryDragPayload::FolderGame { folder_id, game_id }) = payload
                        && let Some(folder) = s
                            .settings
                            .library_folders
                            .iter()
                            .find(|f| f.id == *folder_id)
                        && let Some(from_idx) = folder.games.iter().position(|id| id == game_id)
                    {
                        s.lib.panel_game_reorder.start(from_idx);
                    } else {
                        s.lib.panel_game_reorder.leave_item(index);
                    }
                },
            )
            .on_drop_target_move(move |s: &mut AppState, _: &LibraryDragPayload, ev, _| {
                s.lib
                    .panel_game_reorder
                    .hover_point(index, ev, [ALL_CARD_W, ALL_CARD_H], false);
            })
            .on_drop(move |s: &mut AppState, payload: &LibraryDragPayload, tx| {
                s.lib.panel_game_reorder.cancel();
                if let LibraryDragPayload::FolderGame { folder_id, game_id } = payload
                    && folder_id == &fid_drop
                {
                    let after = tx.drop_point().is_some_and(|p| p.x >= ALL_CARD_W * 0.5);
                    let next = reorder_folder_games(
                        &s.settings.library_folders,
                        &fid_drop,
                        game_id,
                        &gid_drop,
                        after,
                    );
                    s.save_library_folders(next);
                }
            });
        if let Some(bar) = s.lib.panel_game_reorder.indicator(index, FG) {
            tile = tile.child(bar);
        }
    }

    tile
}

pub fn folder_sheet_overlay(s: &AppState, scale: f32) -> Vec<El> {
    let Some(sheet) = s.lib.folder_sheet.as_ref() else {
        return Vec::new();
    };
    let accent = if sheet.color == "gray" {
        FG
    } else {
        folder_color_swatch(sheet.color, 1.0)
    };

    let body = if let Some(crop) = s.lib.folder_cropper.as_ref() {
        folder_cropper_body(crop, sheet.name.text.trim())
    } else {
        folder_sheet_body(s, sheet, accent, scale)
    };

    let mut panel = el()
        .key("folder-sheet-dialog")
        .role("dialog")
        .label(if s.lib.folder_cropper.is_some() {
            "Crop picture"
        } else if sheet.is_new {
            "New folder"
        } else {
            "Edit folder"
        })
        .w(860.0)
        .h(760.0)
        .px(32.0)
        .pt(26.0)
        .pb(24.0)
        .radius(RADIUS_SHEET)
        .continuous_corners()
        .clip_rounded(RADIUS_SHEET)
        .hit()
        .cursor(Cursor::Default)
        .liquid_glass(exo_kit::exo_glass(exo_kit::ExoGlass::Panel))
        .transition(modal_card_transition())
        .modal_tx(true, |s: &mut AppState, tx| {
            if s.lib.folder_cropper.is_some() {
                s.lib.folder_cropper = None;
            } else {
                s.request_close_folder_sheet(tx);
            }
        })
        .modal_live(["top-bar"])
        .modal_scrim(scrim_fill())
        .child(body);

    if s.lib.folder_cropper.is_none() {
        panel = panel
            .accepts_extensions(&["png", "jpg", "jpeg", "webp"])
            .on_drop_hover(|s: &mut AppState, over, _| {
                if let Some(sh) = s.lib.folder_sheet.as_mut() {
                    sh.file_hover = over;
                }
            })
            .on_drop_files(|s: &mut AppState, files, _| {
                if let Some(sh) = s.lib.folder_sheet.as_mut() {
                    sh.file_hover = false;
                }
                if let Some(first) = files.first() {
                    s.open_folder_cropper_from_path(first);
                }
            })
            .on_drop_rejected(|s: &mut AppState, _, _| {
                if let Some(sh) = s.lib.folder_sheet.as_mut() {
                    sh.file_hover = false;
                    sh.picture_error = Some("Choose a PNG, JPEG or WebP picture.".into());
                }
            });
        if sheet.file_hover {
            panel = panel.outline(2.0, -8.0, accent);
        }
    }

    vec![
        el().key("folder-sheet-host")
            .pin(Some(0.0), Some(52.0), Some(0.0), Some(0.0))
            .center()
            .child(panel),
    ]
}

fn folder_sheet_body(
    s: &AppState,
    sheet: &crate::library_state::FolderSheetState,
    accent: rune::Color,
    scale: f32,
) -> El {
    let (library, _, _) = s.shelf_shelves();
    let query = sheet.search.text.trim().to_lowercase();
    let shown: Vec<&Game> = if query.is_empty() {
        library.iter().collect()
    } else {
        library
            .iter()
            .filter(|g| g.title.to_lowercase().contains(&query))
            .collect()
    };

    let not_installed: Vec<String> = library
        .iter()
        .filter(|g| !g.installed)
        .map(|g| g.id.clone())
        .collect();
    let all_not_installed = !not_installed.is_empty()
        && not_installed
            .iter()
            .all(|id| sheet.picked.iter().any(|p| p == id));

    let swatches: Vec<El> = FOLDER_COLOR_IDS
        .iter()
        .map(|&cid| {
            let selected = sheet.color == cid;
            let mut sw = el()
                .key(format!("folder-swatch-{cid}"))
                .role("radio")
                .label(folder_color_label(cid))
                .cursor(Cursor::Pointer)
                .focusable(true)
                .hit()
                .press_on_down()
                .press_scale(PRESS_SCALE)
                .size(24.0, 24.0)
                .radius(12.0)
                .fill(folder_color_swatch(cid, 1.0))
                .motion(SPRING_BASE)
                .on_press(move |s: &mut AppState| {
                    if let Some(sh) = s.lib.folder_sheet.as_mut() {
                        sh.color = cid;
                    }
                });
            if selected {
                sw = sw.outline(2.0, 2.0, FG);
            }
            sw
        })
        .collect();

    let name_field = row()
        .grow(1.0)
        .min_w(260.0)
        .h(36.0)
        .px(12.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border(1.0, glass_rim())
        .child(
            text_input(&sheet.name, type_body())
                .key("folder-sheet-name-input")
                .label("Folder name")
                .grow(1.0)
                .single_line()
                .color(FG)
                .placeholder("Folder name", fg_muted())
                .on_edit(edit_access(|s: &mut AppState| {
                    match s.lib.folder_sheet.as_mut() {
                        Some(sheet) => &mut sheet.name,
                        None => &mut s.ui_discard_edit,
                    }
                }))
                .on_submit(|s: &mut AppState, _| s.save_folder_sheet()),
        );

    let top_row = row()
        .w_full()
        .gap(16.0)
        .center_y()
        .child(name_field)
        .child(row().gap(8.0).center_y().children(swatches));

    let mut pic_row = row().w_full().gap(8.0).center_y();
    if let Some(art_url) = sheet.art.as_deref()
        && let Some(img) = s.folder_art_image(art_url, scale)
    {
        pic_row = pic_row.child(
            el().size(22.0, 33.0)
                .radius(4.0)
                .clip_rounded(4.0)
                .pixels_fit(img.pixels(), SurfaceFit::Fill),
        );
    }
    let pic_btn_label = if sheet.art.is_some() {
        "Change picture"
    } else {
        "Add picture"
    };
    pic_row = pic_row.child(
        glass_button_opt(
            "folder-sheet-pick-pic",
            pic_btn_label,
            Some(icon_image_upload(15.0, FG)),
            false,
        )
        .border(1.0, glass_rim())
        .on_press_tx(|_: &mut AppState, tx| {
            let opts =
                FileDialogOptions::default().filter("Pictures", &["png", "jpg", "jpeg", "webp"]);
            tx.open_file(opts, |s: &mut AppState, res, _| match res {
                Ok(Some(path)) => s.open_folder_cropper_from_path(&path),
                Ok(None) => {}
                Err(_) => {
                    if let Some(sh) = s.lib.folder_sheet.as_mut() {
                        sh.picture_error = Some("The file picker didn't open. Try again.".into());
                    }
                }
            });
        }),
    );
    if sheet.art.is_some() {
        pic_row = pic_row.child(
            glass_button_opt("folder-sheet-remove-pic", "Remove picture", None, false)
                .border(1.0, glass_rim())
                .on_press(|s: &mut AppState| {
                    if let Some(sh) = s.lib.folder_sheet.as_mut() {
                        sh.art = None;
                    }
                }),
        );
    }
    if let Some(err) = sheet.picture_error.as_deref() {
        pic_row = pic_row.child(text(err.to_string()).font(type_small()).color(bad()));
    }

    let search_field = row()
        .grow(1.0)
        .min_w(0.0)
        .h(34.0)
        .px(12.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .fill(glass_control_fill())
        .border(1.0, glass_rim())
        .child(icon_search(15.0, fg_muted()))
        .child(
            text_input(&sheet.search, type_body())
                .key("folder-sheet-search-input")
                .label("Search your games")
                .grow(1.0)
                .single_line()
                .color(FG)
                .placeholder("Search your games", fg_muted())
                .on_edit(edit_access(|s: &mut AppState| {
                    match s.lib.folder_sheet.as_mut() {
                        Some(sheet) => &mut sheet.search,
                        None => &mut s.ui_discard_edit,
                    }
                })),
        );

    let mut tools_row = row().w_full().gap(8.0).center_y().child(search_field);
    if !not_installed.is_empty() {
        let toggle_ids = not_installed;
        let remove = all_not_installed;
        tools_row = tools_row.child(
            glass_button_opt(
                "folder-sheet-toggle-not-installed",
                if remove {
                    "Remove not installed"
                } else {
                    "Add all not installed"
                },
                None,
                false,
            )
            .border(1.0, glass_rim())
            .on_press(move |s: &mut AppState| {
                if let Some(sh) = s.lib.folder_sheet.as_mut() {
                    if remove {
                        sh.picked.retain(|id| !toggle_ids.contains(id));
                    } else {
                        for id in &toggle_ids {
                            if !sh.picked.contains(id) {
                                sh.picked.push(id.clone());
                            }
                        }
                    }
                }
            }),
        );
    }

    let pick_cols = 7usize;
    let pick_w = 102.28;
    let pick_h = pick_w * 1.5;
    let pick_grid = if shown.is_empty() {
        col()
            .w_full()
            .py(32.0)
            .center()
            .child(text("No games match.").font(type_body()).color(fg_muted()))
    } else {
        let rows: Vec<El> = shown
            .chunks(pick_cols)
            .map(|chunk| {
                let items: Vec<El> = chunk
                    .iter()
                    .map(|game| {
                        let on = sheet.picked.iter().any(|p| p == &game.id);
                        let gid = game.id.clone();
                        let mut tile = el()
                            .key(format!("folder-pick-{}", game.id))
                            .role("option")
                            .label(game.title.clone())
                            .cursor(Cursor::Pointer)
                            .focusable(true)
                            .hit()
                            .press_scale(PRESS_SCALE)
                            .size(pick_w, pick_h)
                            .radius(10.0)
                            .continuous_corners()
                            .motion(SPRING_QUICK)
                            .on_press(move |s: &mut AppState| {
                                if let Some(sh) = s.lib.folder_sheet.as_mut() {
                                    if let Some(pos) = sh.picked.iter().position(|id| id == &gid) {
                                        sh.picked.remove(pos);
                                    } else {
                                        sh.picked.push(gid.clone());
                                    }
                                }
                            })
                            .child(
                                cover_surface(game, s, scale, [pick_w, pick_h], false, true, false)
                                    .radius(10.0)
                                    .clip_rounded(10.0),
                            );
                        if on {
                            tile = tile.outline(2.0, 2.0, accent).child(
                                el().pin(None, Some(6.0), Some(6.0), None)
                                    .size(22.0, 22.0)
                                    .radius(11.0)
                                    .center()
                                    .fill(FG)
                                    .child(icon_check(14.0, PRIMARY_FG)),
                            );
                        }
                        tile
                    })
                    .collect();
                row().w_full().gap(12.0).children(items)
            })
            .collect();
        col()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .gap(14.0)
            .px(4.0)
            .py(4.0)
            .children(rows)
    };

    let count_text = if sheet.picked.len() == 1 {
        "1 game".to_string()
    } else {
        format!("{} games", sheet.picked.len())
    };
    let cancel_armed = s.is_confirm_armed("folder-sheet-discard");
    let cancel_btn = if cancel_armed {
        danger_button("folder-sheet-cancel", "Discard changes?", None)
    } else {
        glass_button_opt("folder-sheet-cancel", "Cancel", None, false).border(1.0, glass_rim())
    }
    .on_press_tx(|s: &mut AppState, tx| s.request_close_folder_sheet(tx));

    let ready = sheet.is_ready();
    let save_label = if sheet.is_new { "Create" } else { "Save" };
    let save_btn = if ready {
        primary_button("folder-sheet-save", save_label, None)
            .on_press(|s: &mut AppState| s.save_folder_sheet())
    } else {
        row()
            .key("folder-sheet-save")
            .role("button")
            .label(save_label)
            .disabled(true)
            .hit()
            .cursor(Cursor::NotAllowed)
            .h(32.0)
            .px(14.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(subtle_fill())
            .child(text(save_label).font(type_control()).color(fg_faint()))
    };

    let footer = row()
        .w_full()
        .center_y()
        .gap(8.0)
        .child(
            el().grow(1.0)
                .child(text(count_text).font(type_body()).color(fg_muted())),
        )
        .child(cancel_btn)
        .child(save_btn);

    col()
        .w_full()
        .h_full()
        .gap(16.0)
        .child(
            text(if sheet.is_new {
                "New folder"
            } else {
                "Edit folder"
            })
            .font(type_title())
            .color(FG),
        )
        .child(top_row)
        .child(pic_row)
        .child(tools_row)
        .child(el().w_full().grow(1.0).min_h(0.0).child(pick_grid))
        .child(footer)
}

fn folder_cropper_body(crop: &crate::library_state::FolderCropperState, folder_name: &str) -> El {
    let frame_w = 320.0_f32;
    let frame_h = frame_w / CROP_ASPECT;
    let (cw, _) = crop_size(crop.raw_width as f32, crop.raw_height as f32, crop.zoom);
    let draw_scale = frame_w / cw.max(1.0);
    let img_w = crop.raw_width as f32 * draw_scale;
    let img_h = crop.raw_height as f32 * draw_scale;
    let img_left = -crop.crop_x * draw_scale;
    let img_top = -crop.crop_y * draw_scale;

    let label = if folder_name.is_empty() {
        "Folder"
    } else {
        folder_name
    };

    let flap_preview = col()
        .pin(Some(0.0), None, Some(0.0), Some(0.0))
        .h(frame_h * 0.58)
        .px(14.0)
        .pb(14.0)
        .justify_end()
        .radius(RADIUS_CARD * 0.85)
        .fill(glass_control_fill())
        .border_gradient(1.0, FG.alpha(0.22), TRANSPARENT)
        .inert()
        .child(
            text(label.to_string())
                .font(TextStyle::new(type_control().size, 650.0))
                .color(FG)
                .truncate(),
        );

    let crop_frame = el()
        .key("folder-crop-frame")
        .size(frame_w, frame_h)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .clip_rounded(RADIUS_CARD)
        .border(1.0, FG.alpha(0.70))
        .child(
            el().pin(Some(img_left), Some(img_top), None, None)
                .size(img_w, img_h)
                .pixels_fit(&crop.preview, SurfaceFit::Fill),
        )
        .child(flap_preview);

    let stage = el()
        .key("folder-crop-stage")
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .radius(12.0)
        .clip_rounded(12.0)
        .fill(BG.alpha(0.60))
        .center()
        .cursor(Cursor::Grab)
        .hit()
        .on_drag(move |s: &mut AppState, drag, _| {
            let Some(c) = s.lib.folder_cropper.as_mut() else {
                return;
            };
            let (cw, _) = crop_size(c.raw_width as f32, c.raw_height as f32, c.zoom);
            let sc = frame_w / cw.max(1.0);
            match drag.phase {
                Phase::Down => {
                    c.drag_origin = Some((drag.start_x, drag.start_y, c.crop_x, c.crop_y));
                }
                Phase::Move => {
                    if let Some((_, _, ox, oy)) = c.drag_origin {
                        let (z, x, y) = fit_crop(
                            c.raw_width as f32,
                            c.raw_height as f32,
                            c.zoom,
                            ox - drag.dx / sc,
                            oy - drag.dy / sc,
                        );
                        c.zoom = z;
                        c.crop_x = x;
                        c.crop_y = y;
                    }
                }
                Phase::Up => {
                    c.drag_origin = None;
                }
            }
        })
        .on_wheel(move |s: &mut AppState, ev, _| {
            let Some(c) = s.lib.folder_cropper.as_mut() else {
                return false;
            };
            let factor = (-ev.dy * 0.0025).exp();
            let (old_w, old_h) = crop_size(c.raw_width as f32, c.raw_height as f32, c.zoom);
            let next_z = (c.zoom * factor).clamp(1.0, CROP_MAX_ZOOM);
            let (new_w, new_h) = crop_size(c.raw_width as f32, c.raw_height as f32, next_z);
            let cx = c.crop_x + old_w * 0.5;
            let cy = c.crop_y + old_h * 0.5;
            let (z, x, y) = fit_crop(
                c.raw_width as f32,
                c.raw_height as f32,
                next_z,
                cx - new_w * 0.5,
                cy - new_h * 0.5,
            );
            c.zoom = z;
            c.crop_x = x;
            c.crop_y = y;
            true
        })
        .child(crop_frame);

    let zoom_frac = ((crop.zoom - 1.0) / (CROP_MAX_ZOOM - 1.0)).clamp(0.0, 1.0);
    let zoom_bar = row()
        .w_full()
        .justify_center()
        .center_y()
        .gap(12.0)
        .child(
            glass_icon_circle_opt(
                "folder-crop-zoom-out",
                "Zoom out",
                28.0,
                icon_zoom_out(14.0, fg_muted()),
                false,
            )
            .on_press(|s: &mut AppState| adjust_crop_zoom(s, 1.0 / 1.15)),
        )
        .child(slider_range(
            slider_track(
                "folder-crop-zoom-slider",
                "Zoom",
                300.0,
                zoom_frac,
                crop.drag_origin.is_some(),
                crop.busy,
            )
            .on_drag(|s: &mut AppState, drag, _| {
                let frac = (drag.x / drag.w.max(1.0)).clamp(0.0, 1.0);
                let target_zoom = 1.0 + frac * (CROP_MAX_ZOOM - 1.0);
                set_crop_zoom(s, target_zoom);
            }),
            f64::from(crop.zoom),
            1.0,
            f64::from(CROP_MAX_ZOOM),
            0.1,
            !crop.busy,
            |s: &mut AppState, v| set_crop_zoom(s, v as f32),
        ))
        .child(
            glass_icon_circle_opt(
                "folder-crop-zoom-in",
                "Zoom in",
                28.0,
                icon_zoom_in(16.0, fg_muted()),
                false,
            )
            .on_press(|s: &mut AppState| adjust_crop_zoom(s, 1.15)),
        );

    let mut foot = row().w_full().center_y().gap(8.0);
    if let Some(err) = crop.error.as_deref() {
        foot = foot.child(
            el().grow(1.0)
                .child(text(err.to_string()).font(type_small()).color(bad())),
        );
    } else {
        foot = foot.child(el().grow(1.0));
    }
    foot = foot
        .child(
            glass_button_opt("folder-crop-cancel", "Cancel", None, false)
                .on_press(|s: &mut AppState| s.lib.folder_cropper = None),
        )
        .child(
            primary_button(
                "folder-crop-done",
                if crop.busy { "Saving…" } else { "Done" },
                None,
            )
            .disabled(crop.busy)
            .on_press(|s: &mut AppState| s.save_folder_cropper()),
        );

    col()
        .w_full()
        .h_full()
        .gap(16.0)
        .child(text("Crop picture").font(type_title()).color(FG))
        .child(stage)
        .child(zoom_bar)
        .child(foot)
}

fn set_crop_zoom(s: &mut AppState, target_zoom: f32) {
    let Some(c) = s.lib.folder_cropper.as_mut() else {
        return;
    };
    let (old_w, old_h) = crop_size(c.raw_width as f32, c.raw_height as f32, c.zoom);
    let next_z = target_zoom.clamp(1.0, CROP_MAX_ZOOM);
    let (new_w, new_h) = crop_size(c.raw_width as f32, c.raw_height as f32, next_z);
    let cx = c.crop_x + old_w * 0.5;
    let cy = c.crop_y + old_h * 0.5;
    let (z, x, y) = fit_crop(
        c.raw_width as f32,
        c.raw_height as f32,
        next_z,
        cx - new_w * 0.5,
        cy - new_h * 0.5,
    );
    c.zoom = z;
    c.crop_x = x;
    c.crop_y = y;
}

fn adjust_crop_zoom(s: &mut AppState, factor: f32) {
    let Some(current) = s.lib.folder_cropper.as_ref().map(|c| c.zoom) else {
        return;
    };
    set_crop_zoom(s, current * factor);
}
