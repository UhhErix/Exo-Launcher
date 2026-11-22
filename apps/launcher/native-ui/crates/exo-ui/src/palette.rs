use crate::state::{AppState, PaletteItem};
use exo_kit::{
    BG, FG, RADIUS_SMALL, SPRING_BASE, TRANSPARENT, fg_muted, glass_panel, hairline, hover_fill,
    icon_search, type_body, type_control, type_small,
};
use rune::{El, Key, KeyEvent, Tx, col, edit_access, el, row, text, text_input};

fn palette_row(item: PaletteItem, index: usize, selected: bool) -> El {
    let target = item.target.clone();
    row()
        .key(item.id)
        .role("option")
        .label(item.title.clone())
        .focusable(true)
        .hit()
        .press_on_down()
        .w_full()
        .h(38.0)
        .px(12.0)
        .radius(RADIUS_SMALL)
        .justify_between()
        .center_y()
        .fill(if selected { hover_fill() } else { TRANSPARENT })
        .hover(hover_fill())
        .on_press(move |s: &mut AppState| {
            s.palette_index = index;
            s.execute_palette_target(target.clone());
        })
        .child(text(item.title).font(type_control()).color(FG).truncate())
        .child(text(item.subtitle).font(type_small()).color(fg_muted()))
}

pub fn handle_palette_key(s: &mut AppState, ev: &KeyEvent, _tx: &mut Tx) -> bool {
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
                s.execute_palette_target(item.target.clone());
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
        .label("Search games, rooms, and actions")
        .grow(1.0)
        .single_line()
        .color(FG)
        .placeholder("Search games, rooms, and actions…", fg_muted())
        .on_edit(edit_access(|s: &mut AppState| &mut s.palette_query))
        .on_input_change(|s: &mut AppState, _| s.palette_index = 0)
        .on_input_key(handle_palette_key)
        .on_submit(|s: &mut AppState, _| {
            let list = s.palette_results();
            if let Some(item) = list.get(s.palette_index) {
                s.execute_palette_target(item.target.clone());
            }
        });

    let header = row()
        .w_full()
        .h(48.0)
        .px(16.0)
        .gap(10.0)
        .center_y()
        .child(icon_search(15.0, fg_muted()))
        .child(input)
        .child(text("Esc").font(type_small()).color(fg_muted()));

    let body = if items.is_empty() {
        el().w_full()
            .h(72.0)
            .center()
            .child(text("No matching games or commands").font(type_small()).color(fg_muted()))
    } else {
        col()
            .w_full()
            .px(6.0)
            .py(6.0)
            .gap(2.0)
            .children(
                items
                    .into_iter()
                    .enumerate()
                    .map(|(i, item)| palette_row(item, i, i == clamped)),
            )
    };

    let card = glass_panel("command-palette", 540.0)
        .motion(SPRING_BASE)
        .child(header)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(body);

    el().key("palette-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(BG.alpha(0.45))
        .hit()
        .on_press(|s: &mut AppState| s.close_palette())
        .child(col().w_full().h_full().pt(112.0).items_center().child(card))
}
