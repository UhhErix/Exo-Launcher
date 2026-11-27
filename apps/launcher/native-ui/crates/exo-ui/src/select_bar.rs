use crate::library_state::format_bytes_short;
use crate::state::{AppState, InputMode};
use exo_kit::{
    ExoGlass, FG, MenuAnchor, MenuEntry, MenuItem, RADIUS_PILL, SPRING_BASE, apply_glass,
    context_menu_layers, float_line, glass_button_opt, glass_rim, icon_eye, icon_eye_off,
    icon_folder, icon_folder_plus, icon_trash, primary_button, type_control,
};
use rune::{Align, El, Side, Tx, el, row, text};
use std::rc::Rc;

pub fn select_bar_overlay(s: &AppState) -> Vec<El> {
    if !s.lib.selecting || !s.library_all_games || s.open_game_id.is_some() {
        return Vec::new();
    }
    let (picked_games, total) = s.picked_visible_games();
    let count = picked_games.len();
    let hidden_view = s.library_show_hidden;
    let removable = count > 0
        && picked_games
            .iter()
            .all(|g| g.installed && !g.can_stop && !g.is_running);
    let all_local = count > 0 && picked_games.iter().all(|g| g.store == "local");
    let verb = if all_local { "Remove" } else { "Uninstall" };
    let freed: u64 = picked_games.iter().filter_map(|g| g.size_bytes).sum();
    let question = if verb == "Remove" {
        format!("Remove {count}? Files stay")
    } else if freed > 0 {
        format!("Uninstall {count}? {}", format_bytes_short(freed))
    } else {
        format!("Uninstall {count}?")
    };

    let count_label = if count > 0 {
        format!("{count} selected")
    } else {
        "Select games".to_string()
    };

    let hide_icon = if hidden_view {
        icon_eye(15.0, FG)
    } else {
        icon_eye_off(15.0, FG)
    };
    let mut hide_btn = glass_button_opt(
        "select-bar-hide",
        if hidden_view { "Unhide" } else { "Hide" },
        Some(hide_icon),
        false,
    )
    .px(14.0)
    .border(1.0, glass_rim())
    .disabled(count == 0);
    if count > 0 {
        hide_btn = hide_btn.on_press(|s: &mut AppState| s.bulk_hide_picked());
    } else {
        hide_btn = hide_btn.opacity(0.42);
    }

    let mut bar = apply_glass(
        row()
            .key("library-select-bar")
            .role("toolbar")
            .label("Selected games")
            .h(52.0)
            .pl(20.0)
            .pr(10.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .motion(SPRING_BASE)
            .child(
                el().min_w(96.0)
                    .pr(6.0)
                    .child(text(count_label).font(type_control()).color(FG)),
            )
            .child(hide_btn),
        ExoGlass::Bar,
    );

    if !hidden_view {
        let mut folder_btn = glass_button_opt(
            "select-bar-folder",
            "Add to folder",
            Some(icon_folder(15.0, FG)),
            false,
        )
        .px(14.0)
        .border(1.0, glass_rim())
        .disabled(count == 0);
        if count > 0 {
            folder_btn = folder_btn.on_press(|s: &mut AppState| {
                s.lib.select_folder_menu_open = !s.lib.select_folder_menu_open;
            });
        } else {
            folder_btn = folder_btn.opacity(0.42);
        }
        bar = bar.child(folder_btn);
    }

    if removable {
        const CONFIRM_KEY: &str = "select-bar-uninstall";
        let armed = s.is_confirm_armed(CONFIRM_KEY);
        let btn_label = if armed { question } else { verb.to_string() };
        let btn = row()
            .key("select-bar-uninstall")
            .role("button")
            .label(btn_label.clone())
            .cursor(rune::Cursor::Pointer)
            .focusable(true)
            .hit()
            .press_on_down()
            .press_scale(exo_kit::PRESS_SCALE)
            .h(32.0)
            .px(12.0)
            .gap(6.0)
            .center_y()
            .radius(RADIUS_PILL)
            .fill(exo_kit::bad())
            .hover(exo_kit::bad().alpha(0.9))
            .motion(exo_kit::SPRING_QUICK)
            .on_press_tx(|s: &mut AppState, tx| {
                if s.is_confirm_armed(CONFIRM_KEY) {
                    s.disarm_confirm();
                    s.bulk_uninstall_picked();
                } else {
                    s.arm_confirm_key(CONFIRM_KEY, tx);
                }
            })
            .child(icon_trash(15.0, FG))
            .child(text(btn_label).font(type_control()).color(FG));
        bar = bar.child(btn);
    }

    let rule = el().w(1.0).h(20.0).mx(4.0).fill(float_line());
    let all_or_clear = if count < total {
        glass_button_opt("select-bar-all", "Select all", None, false)
            .px(14.0)
            .border(1.0, glass_rim())
            .on_press(|s: &mut AppState| s.pick_all_visible())
    } else {
        glass_button_opt("select-bar-clear", "Clear", None, false)
            .px(14.0)
            .border(1.0, glass_rim())
            .on_press(|s: &mut AppState| {
                s.lib.picked.clear();
                s.lib.pick_anchor = None;
            })
    };
    let done_btn = primary_button("select-bar-done", "Done", None)
        .px(14.0)
        .on_press(|s: &mut AppState| s.exit_select_mode());

    bar = bar.child(rule).child(all_or_clear).child(done_btn);

    let mut layers = vec![
        row()
            .key("library-select-bar-wrap")
            .pin(Some(0.0), None, Some(0.0), Some(20.0))
            .justify_center()
            .child(bar),
    ];

    if s.lib.select_folder_menu_open && !hidden_view && count > 0 {
        let mut entries: Vec<MenuEntry<AppState>> = s
            .settings
            .library_folders
            .iter()
            .map(|folder| {
                let fid = folder.id.clone();
                MenuEntry::item(
                    MenuItem::new(format!("sel-folder-{}", folder.id), folder.name.clone())
                        .icon(icon_folder(15.0, FG))
                        .on_select(move |s: &mut AppState, _| {
                            s.bulk_add_picked_to_folder(Some(&fid));
                        }),
                )
            })
            .collect();
        entries.push(MenuEntry::item(
            MenuItem::new("sel-folder-new", "New folder")
                .icon(icon_folder_plus(15.0, FG))
                .on_select(|s: &mut AppState, _| {
                    s.bulk_add_picked_to_folder(None);
                }),
        ));

        layers.extend(context_menu_layers(exo_kit::ContextMenuSpec {
            id: "select-bar-folder-menu".into(),
            anchor: MenuAnchor::Element {
                key: "select-bar-folder".into(),
                side: Side::Top,
                align: Align::Start,
                gap: 8.0,
            },
            width: 220.0,
            open_submenu: None,
            return_focus: Some("select-bar-folder".into()),
            gamepad_focus: s.input_mode == InputMode::Gamepad,
            entries,
            on_dismiss: Rc::new(|s: &mut AppState, _: &mut Tx| {
                s.lib.select_folder_menu_open = false;
            }),
            on_submenu: Rc::new(|_: &mut AppState, _: Option<usize>, _: &mut Tx| {}),
        }));
    }

    layers
}
