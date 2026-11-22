use crate::library;
use crate::palette;
use crate::state::{AppState, HostStatus, Room};
use exo_kit::{
    BG, FG, GAP, GLASS_CONTROL_SIGMA, PRESS_SCALE, PRIMARY_FG, RADIUS_PANEL, RADIUS_PILL, bad,
    fg_muted, glass_capsule, glass_control_fill, glass_rim, good, hover_fill, icon_chat, icon_cpu,
    icon_grid, icon_music, icon_search, icon_settings, icon_store, nav_tab, type_body,
    type_control, type_page, type_small, warn,
};
use rune::{Cx, El, ScrollSpec, col, el, row, text};

fn room_icon(room: Room, active: bool) -> El {
    let color = if active { PRIMARY_FG } else { FG };
    match room {
        Room::Library => icon_grid(14.0, color),
        Room::Store => icon_store(14.0, color),
        Room::Discord => icon_chat(14.0, color),
        Room::Music => icon_music(14.0, color),
        Room::Pc => icon_cpu(14.0, color),
        Room::Settings => icon_settings(14.0, color),
    }
}

fn status_capsule(s: &AppState) -> El {
    let (dot, label) = match &s.status {
        HostStatus::Connected => (good(), format!("Live · {} games", s.games.len())),
        HostStatus::Connecting => (warn(), "Connecting…".into()),
        HostStatus::Standalone => (good(), "Rune DX12".into()),
        HostStatus::Error(_) => (bad(), "Host offline".into()),
    };

    row()
        .key("status-capsule")
        .h(40.0)
        .px(14.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .backdrop_blur(GLASS_CONTROL_SIGMA)
        .fill(glass_control_fill())
        .border(1.0, glass_rim())
        .child(el().size(8.0, 8.0).radius(4.0).fill(dot))
        .child(text(label).font(type_control()).color(FG))
}

fn placeholder_room(room: Room) -> El {
    col()
        .w_full()
        .pt(76.0)
        .px(24.0)
        .gap(14.0)
        .child(text(room.label()).font(type_page()).color(FG))
        .child(
            col()
                .w(540.0)
                .px(20.0)
                .py(18.0)
                .gap(8.0)
                .radius(RADIUS_PANEL)
                .fill(glass_control_fill())
                .border(1.0, glass_rim())
                .child(text("Native Rune port in progress").font(type_control()).color(FG))
                .child(text(room.next_phase_note()).font(type_body()).color(fg_muted())),
        )
}

pub fn view(s: &AppState, cx: &mut Cx<AppState>) -> El {
    let insets = cx.chrome_insets();
    let trailing_pad = if insets.trailing > 0.0 {
        insets.trailing + 8.0
    } else {
        GAP
    };
    let scale = cx.env().scale;

    let nav = glass_capsule("nav-capsule").children(Room::ALL.into_iter().map(|room| {
        let active = s.room == room;
        nav_tab(room.key(), room.label(), active, room_icon(room, active))
            .on_press(move |s: &mut AppState| s.room = room)
    }));

    let search = row()
        .key("search-capsule")
        .role("button")
        .label("Search")
        .focusable(true)
        .hit()
        .press_on_down()
        .press_scale(PRESS_SCALE)
        .h(40.0)
        .px(14.0)
        .gap(8.0)
        .center_y()
        .radius(RADIUS_PILL)
        .backdrop_blur(GLASS_CONTROL_SIGMA)
        .fill(glass_control_fill())
        .hover(hover_fill())
        .border(1.0, glass_rim())
        .on_press_tx(|s: &mut AppState, tx| {
            if s.palette_open {
                s.close_palette();
            } else {
                s.open_palette(tx);
            }
        })
        .child(icon_search(14.0, fg_muted()))
        .child(text("Search").font(type_control()).color(fg_muted()))
        .child(text("Ctrl+K").font(type_small()).color(fg_muted()));

    let top_bar = row()
        .key("top-bar")
        .window_drag()
        .pin(Some(GAP), Some(7.0), Some(trailing_pad), None)
        .h(40.0)
        .justify_between()
        .center_y()
        .child(nav)
        .child(row().gap(8.0).center_y().child(search).child(status_capsule(s)));

    let page = match s.room {
        Room::Library => library::view(s, scale),
        other => placeholder_room(other),
    };

    let scroller = col()
        .key("room-scroll")
        .w_full()
        .h_full()
        .scroll(ScrollSpec { stick_bottom: false })
        .child(page);

    let mut root = el()
        .w_full()
        .h_full()
        .fill(BG)
        .child(scroller)
        .child(top_bar);

    if s.palette_open {
        root = root.child(palette::view(s));
    }
    root
}
