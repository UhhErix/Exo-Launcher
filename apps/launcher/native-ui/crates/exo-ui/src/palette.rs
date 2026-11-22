use crate::shell::room_icon;
use crate::state::{AppState, PaletteItem, PaletteTarget};
use exo_kit::{
    BG, FG, RADIUS_SMALL, SPRING_BASE, TRANSPARENT, fg_muted, glass_panel, hover_fill,
    icon_gamepad, icon_key, icon_refresh, icon_search, icon_sparkles, type_body, type_small,
};
use rune::{El, Key, KeyEvent, ScrollSpec, TextStyle, Tx, col, edit_access, el, row, text, text_input};

fn palette_icon(target: &PaletteTarget) -> El {
    match target {
        PaletteTarget::Room(room) => room_icon(*room, false),
        PaletteTarget::OpenGame(_) | PaletteTarget::LaunchGame(_) => icon_gamepad(15.0, fg_muted()),
        PaletteTarget::RefreshLibrary => icon_refresh(14.0, fg_muted()),
        PaletteTarget::ActivateKey => icon_key(14.0, fg_muted()),
        PaletteTarget::StartTour => icon_sparkles(14.0, fg_muted()),
        PaletteTarget::ToggleFilter(_) => icon_search(14.0, fg_muted()),
    }
}

fn palette_row(item: PaletteItem, index: usize, selected: bool) -> El {
    let target = item.target.clone();
    let icon = palette_icon(&target);
    row()
        .key(item.id)
        .role("option")
        .label(item.title.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .w_full()
        .h(36.0)
        .px(12.0)
        .gap(10.0)
        .center_y()
        .radius(RADIUS_SMALL)
        .fill(if selected { hover_fill() } else { TRANSPARENT })
        .hover(hover_fill())
        .on_press_tx(move |s: &mut AppState, tx| {
            s.palette_index = index;
            s.execute_palette_target(target.clone(), tx);
        })
        .child(icon)
        .child(
            text(item.title)
                .font(TextStyle::new(13.0, 500.0))
                .color(FG)
                .truncate(),
        )
}

pub fn handle_palette_key(s: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    match ev.key {
        Key::Escape => {
            s.close_palette();
            true
        }
        Key::Down => {
            let len = s.palette_results().len();
            if len > 0 {
                s.palette_index = (s.palette_index + 1) % len;
            }
            true
        }
        Key::Up => {
            let len = s.palette_results().len();
            if len > 0 {
                s.palette_index = (s.palette_index + len - 1) % len;
            }
            true
        }
        Key::Enter => {
            let items = s.palette_results();
            if let Some(item) = items.get(s.palette_index) {
                s.execute_palette_target(item.target.clone(), tx);
            } else {
                s.close_palette();
            }
            true
        }
        _ => false,
    }
}

pub fn view(s: &AppState) -> El {
    let items = s.palette_results();
    let clamped = if items.is_empty() {
        0
    } else {
        s.palette_index.min(items.len() - 1)
    };

    let input = text_input(&s.palette_query, type_body())
        .key("palette-input")
        .label("Search")
        .grow(1.0)
        .single_line()
        .color(FG)
        .placeholder("Search", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.palette_query))
        .on_input_change(|s: &mut AppState, _| s.palette_index = 0)
        .on_input_key(handle_palette_key)
        .on_submit(|s: &mut AppState, tx| {
            let list = s.palette_results();
            if let Some(item) = list.get(s.palette_index) {
                s.execute_palette_target(item.target.clone(), tx);
            }
        });

    let header = row()
        .w_full()
        .h(46.0)
        .px(16.0)
        .gap(10.0)
        .center_y()
        .child(icon_search(15.0, fg_muted()))
        .child(input);

    let body = if items.is_empty() {
        el().w_full()
            .h(72.0)
            .center()
            .child(
                text("No matching games or commands")
                    .font(type_small())
                    .color(fg_muted()),
            )
    } else {
        let mut rows: Vec<El> = Vec::new();
        let mut last_group: Option<&'static str> = None;
        for (i, item) in items.into_iter().enumerate() {
            if last_group != Some(item.group) {
                rows.push(
                    el().w_full()
                        .px(12.0)
                        .pt(if last_group.is_some() { 10.0 } else { 4.0 })
                        .pb(4.0)
                        .child(
                            text(item.group)
                                .font(TextStyle::new(11.0, 600.0))
                                .color(fg_muted()),
                        ),
                );
                last_group = Some(item.group);
            }
            rows.push(palette_row(item, i, i == clamped));
        }
        col()
            .w_full()
            .max_h(520.0)
            .px(6.0)
            .pb(8.0)
            .gap(1.0)
            .scroll(ScrollSpec { stick_bottom: false })
            .children(rows)
    };

    let card = glass_panel("command-palette", 468.0)
        .motion(SPRING_BASE)
        .child(header)
        .child(body);

    el().key("palette-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG.alpha(0.55))
        .hit()
        .on_press(|s: &mut AppState| s.close_palette())
        .child(col().w_full().h_full().pt(92.0).items_center().child(card))
}
