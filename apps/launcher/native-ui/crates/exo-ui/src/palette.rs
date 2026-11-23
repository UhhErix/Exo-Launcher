use crate::shell::room_icon;
use crate::state::{AppState, PaletteItem, PaletteTarget};
use exo_kit::{
    FG, RADIUS_SHEET, RADIUS_SMALL, SPRING_BASE, SPRING_QUICK, TRANSPARENT, fg_muted,
    glass_panel_opt, hairline, hover_fill, icon_close, icon_gamepad, icon_key, icon_minimize,
    icon_refresh, icon_search, icon_settings, icon_sparkles, modal_card_transition,
    modal_scrim_transition, scaled, scrim_fill, type_body, type_control, type_small,
};
use rune::{
    Cursor, El, Key, KeyEvent, ScrollSpec, Spring, TextStyle, Transition, Tx, col, edit_access, el,
    row, text, text_input,
};

fn palette_icon(target: &PaletteTarget) -> El {
    match target {
        PaletteTarget::Room(room) => room_icon(*room, false),
        PaletteTarget::SettingsSection(_) => icon_settings(16.0, fg_muted()),
        PaletteTarget::PcSection(_) => icon_sparkles(16.0, fg_muted()),
        PaletteTarget::OpenGame(_) | PaletteTarget::AddGame => icon_gamepad(16.0, fg_muted()),
        PaletteTarget::RefreshLibrary | PaletteTarget::CheckUpdates => {
            icon_refresh(16.0, fg_muted())
        }
        PaletteTarget::ActivateKey => icon_key(16.0, fg_muted()),
        PaletteTarget::StartTour => icon_sparkles(16.0, fg_muted()),
        PaletteTarget::MinimizeWindow => icon_minimize(16.0, fg_muted()),
        PaletteTarget::CloseWindow => icon_close(16.0, fg_muted()),
        PaletteTarget::ResumeSetup => icon_sparkles(16.0, fg_muted()),
    }
}

fn palette_row(item: PaletteItem, index: usize, selected: bool) -> El {
    let target = item.target.clone();
    let icon = palette_icon(&target);
    let label = match item.detail {
        Some(detail) => format!("{}{}", item.title, detail),
        None => item.title.clone(),
    };
    let mut r = row()
        .key(format!("command-palette-{}", item.id))
        .role("option")
        .label(label)
        .focusable(true)
        .hit()
        .cursor(Cursor::Pointer)
        .press_on_down()
        .w_full()
        .h(scaled(44.0))
        .px(scaled(10.0))
        .gap(scaled(12.0))
        .center_y()
        .radius(RADIUS_SMALL + 2.0)
        .fill(if selected { hover_fill() } else { TRANSPARENT })
        .hover(hover_fill())
        .hover_motion(Spring::NONE, SPRING_QUICK)
        .on_press_tx(move |s: &mut AppState, tx| {
            s.palette_index = index;
            s.execute_palette_target(target.clone(), tx);
        })
        .child(icon)
        .child(
            text(item.title)
                .font(type_control())
                .color(FG)
                .grow(1.0)
                .min_w(0.0)
                .truncate(),
        );
    if let Some(detail) = item.detail {
        r = r.child(text(detail).font(type_small()).color(fg_muted()));
    }
    r
}

pub fn handle_palette_key(s: &mut AppState, ev: &KeyEvent, tx: &mut Tx) -> bool {
    match ev.key {
        Key::Escape => {
            tx.instant();
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
            tx.instant();
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

pub fn layers(s: &AppState, blur: bool) -> Vec<El> {
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
            tx.instant();
            let list = s.palette_results();
            if let Some(item) = list.get(s.palette_index) {
                s.execute_palette_target(item.target.clone(), tx);
            }
        });

    let header_h = scaled(48.0);
    let header = row()
        .w_full()
        .h(header_h)
        .px(scaled(16.0))
        .gap(scaled(12.0))
        .center_y()
        .cursor(Cursor::Text)
        .child(icon_search(16.0, fg_muted()))
        .child(input);

    let max_palette_h = 820.8;
    let body = if items.is_empty() {
        el().w_full().h(72.0).center().child(
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
                        .px(scaled(10.0))
                        .pt(if last_group.is_some() {
                            scaled(12.0)
                        } else {
                            scaled(8.0)
                        })
                        .pb(scaled(4.0))
                        .child(
                            text(item.group)
                                .font(TextStyle::new(scaled(12.0), 500.0))
                                .color(fg_muted()),
                        ),
                );
                last_group = Some(item.group);
            }
            rows.push(palette_row(item, i, i == clamped));
        }
        col()
            .key("command-palette-list")
            .role("listbox")
            .label("Commands")
            .w_full()
            .max_h(max_palette_h - header_h - 1.0)
            .px(scaled(6.0))
            .py(scaled(6.0))
            .scroll(ScrollSpec {
                stick_bottom: false,
            })
            .clip_fade_linear(0.0, 0.0, 0.0, 28.0)
            .children(rows)
    };

    let card = glass_panel_opt("command-palette", scaled(576.0), blur)
        .max_h(max_palette_h)
        .radius(RADIUS_SHEET)
        .clip_rounded(RADIUS_SHEET)
        .motion(SPRING_BASE)
        .transition(modal_card_transition())
        .child(header)
        .child(el().w_full().h(1.0).fill(hairline()))
        .child(body);

    let scrim = el()
        .key("palette-scrim")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .fill(scrim_fill())
        .hit()
        .cursor(Cursor::Default)
        .transition(modal_scrim_transition())
        .on_press(|s: &mut AppState| s.close_palette());

    let host = col()
        .key("palette-host")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .pt(129.6)
        .items_center()
        .transition(Transition::none())
        .child(card);

    vec![scrim, host]
}
